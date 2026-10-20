<script setup lang="ts">
/**
 * 远程桌面观看页（M5.1）。
 *
 * 这一页回答现场三个问题：
 *   1. **哪台机器现在在推流？**（会话列表 + 实时帧率）
 *   2. **画面在哪？**（canvas）
 *   3. **没有画面的时候，卡在哪儿了？**（state + endedReason + 两个方向的帧计数）
 *
 * ⚠️ 权限：整页**只对管理员**可见，服务端对整个 `/api/remote` 也是 AdminOnly
 *    （决策 11：非管理员连"只读观看"都不允许 —— 价值点是"看到别人屏幕"本身）。
 *
 * 🔥 **码流不走 HTTP**：它经 `/hubs/remote` 推过来，而且**只推给"进了这一路分组"的连接**
 *    （先 `Join`）。所以本组件自己持有一条 Hub 连接，并且**随组件的挂载/卸载起停** ——
 *    切走页签就断开，不会在后台白收一个 4K 的画面流。
 *
 * 🔥 **M5.1 的编码是 MJPEG（不是可交付的编码格式）**：它只是让整条链路先跑通的脚手架。
 *    界面上必须把这件事说清楚，否则会被当成"远控做完了、画面就这样"。
 *    NVENC / QSV 都不编 JPEG，4K 单帧软编要 40–80 ms ⇒ 上限 12–25 fps。
 */
import { computed, onMounted, onUnmounted, ref, watch as vueWatch } from 'vue'
import * as signalR from '@microsoft/signalr'
import { api, ApiError } from '../api'
import RemoteFilesPanel from './RemoteFilesPanel.vue'
import type {
  AgentHostDto,
  CurrentUser,
  RemoteAudioFrame,
  RemoteCapabilityDto,
  RemoteClipboardDto,
  RemoteInputAckDto,
  RemoteInputEventDto,
  RemoteScreenDto,
  RemoteSessionDto,
  RemoteStreamFrame,
  RemoteWatchAckDto,
  TrustInfoDto,
} from '../types'
import {
  AGENT_AUDIO_STATES,
  AGENT_INPUT_STATES,
  REMOTE_CLIPBOARD_MAX_CHARS,
  REMOTE_DISPLAY_MODES,
  REMOTE_ERROR_CODES,
  REMOTE_HUB_CLIENT_METHODS,
  REMOTE_HUB_METHODS,
  REMOTE_INPUT_KINDS,
  REMOTE_INPUT_MAX_EVENTS,
  REMOTE_CODECS,
  REMOTE_MOUSE_BUTTONS,
  REMOTE_QUALITY_PROFILES,
  REMOTE_SESSION_STATES,
} from '../types'
import {
  audioVideoOffsetMs,
  RemoteAudioPlayer,
  RemoteVideoBuffer,
  SessionClock,
  type AudioStats,
  type VideoBufferStats,
} from '../remotePlayback'
// M7.16：播放延迟按目标帧率算（60 fps ⇒ 33 ms）。⚠️ 两条流读的是**同一个单值**，
// 所以音画对齐那条依据没有变 —— 变的只是"这个数是多少"。
import {
  getPlaybackLatencyMs,
  resetPlaybackLatency,
  setPlaybackLatencyForFps,
} from '../playbackLatency'
// ⚠️ 从它**自己的家**导入（`remotePlayback` 只是转手）：这个类型说的不是"播放"，
//    而是"解码器这一侧发生了什么"，而它还带着 `resets` 这条与排期无关的语义。
import type { FrameDecoderStats } from '../frameDecoder'
import { createMjpegDecoder } from '../mjpegDecoder'
import { createWebCodecsDecoder } from '../webCodecsDecoder'
import {
  mjpegOnlyResult,
  probeViewerVideo,
  type ViewerProbeResult,
} from '../remoteCodecProbe'
import { fmtBytes, fmtDateTime, fmtTimeOnly } from '../format'
import { describeDesktopIdle, isDesktopIdleNotice } from '../desktopIdle'
import {
  describeEncodingScale,
  describeSizeMismatch,
  pickDrawRect,
  sizeDiffers,
} from '../frameSize'
import { base64ByteLength, describeBlankFrame } from '../blankFrame'
import {
  describeFullscreenFailure,
  enterFullscreen,
  exitFullscreen,
  isElementFullscreen,
  isFullscreenSupported,
} from '../fullscreen'
// 🔥 浏览器**保留键**（Ctrl+W / Ctrl+R / F5 …）`preventDefault()` 拦不住，
//    只有全屏 + Keyboard Lock 才能把它们交还给被控端（见 `keyboardLock.ts` 的文件头）。
import { lockBrowserKeys, shouldWarnBeforeUnload, unlockBrowserKeys } from '../keyboardLock'

// M7.11：编码格式的说法抽在 `codecFallback.ts`（纯函数、有用例钉住），
// 组件里不许再重写一遍 —— 那正是"页面说胡话"能长期活下去的原因。
import { codecLabel, describeCodecFallback } from '../codecFallback'

// M7.13：断线重连的**退避决策**抽在 `reconnect.ts`（12 条用例）。理由同上：
// 那几条规则（别叠并发请求 / 合并重复触发 / 次数用完放弃）没有任何界面证据，靠读代码守不住。
import { planReconnect, RECONNECT_DELAYS, secondsLeft } from '../reconnect'

const props = defineProps<{
  user: CurrentUser | null
}>()

const emit = defineEmits<{
  (e: 'notice', kind: 'ok' | 'err' | 'warn', text: string): void
}>()

// ═══════════════════════════════════════════════════════════════
// 数据
// ═══════════════════════════════════════════════════════════════

const sessions = ref<RemoteSessionDto[]>([])
const agents = ref<AgentHostDto[]>([])
const loading = ref(false)
const loadError = ref('')

/** 正在看的会话 Id（`null` = 还没选）。 */
const activeSessionId = ref<string | null>(null)

/** 建会话请求在飞 —— 期间禁用按钮，避免连点建出一堆会话（服务端会回 SessionBusy）。 */
const starting = ref(false)

/** 建会话时选的那台机器。 */
const targetAgentId = ref<number | null>(null)

/**
 * 目标帧率（`null` = 不指定，用档位推荐值）。
 *
 * 🔥 **M7.16 起它真的说了算**：以前服务端把它与档位推荐值取更小，而默认档位是
 * `auto`（解析成 12 fps）⇒ 这里选 20 / 30 / 60 **都还是 12**，页面上没有任何一处说这件事。
 * 现在的规则是"请求优先、档位只在没指定时提供默认值"（`RemoteQualityProfiles.FoldTargetFps`），
 * 所以这一格才是一个**真的旋钮**。
 *
 * ⚠️ 它仍然<b>只决定帧率</b>：码率由「质量档」决定，60 fps 配「均衡」（6 Mbps）
 * 每帧只有 100 kbps，画面会糊 —— 要清晰得同时选「流畅」（25 Mbps）。
 */
const maxFps = ref<number | null>(null)

/**
 * 建会话时要带上的**质量档**（M7.16）。
 *
 * <para>
 * 🔥 以前建会话根本不带这个字段，于是每一路新会话都从 `auto`（＝均衡的 12 fps / 6 Mbps）
 * 起步，"档位"只能等连上之后再点一次。两个直接后果：
 * <b>①</b> 选了 60 帧也得先连一次、再切一次档；
 * <b>②</b>「按当前设置重建会话」会把档位<b>整个丢掉</b>（用户刚点的「流畅」退回「自动」），
 * 而页面上只会看到"帧率怎么又掉回 12 了"。
 * </para>
 * <para>
 * ⚠️ 它记的是**用户选的**，不是"服务端当前生效的" —— 后者在会话消失后就没有了，
 * 而重连恰恰发生在会话消失之后。
 * </para>
 */
const chosenQuality = ref<string>(REMOTE_QUALITY_PROFILES.auto)

/**
 * 帧率下拉框的选项（M7.16 新增 45 / 60）。
 *
 * 🔥 <b>为什么上限就是 60</b>：面板刷新率就是 60 Hz，再高的目标值一个都出不来，
 * 只会让页面上多一个"选了永远达不到"的数（铁律：别把做不到的事写成可选项）。
 * 被控端也会自己夹到 1–60（`RemoteStreamHost.MaxFps`）。
 */
const FPS_CHOICES = [5, 12, 20, 30, 45, 60] as const

/**
 * 当前生效的**目标播放延迟**（诊断页那一格，M7.16）。
 *
 * 🔥 它是 `playbackLatency.ts` 里那个进程级单值的**镜像** ——
 * 那边刻意不是响应式的（两条流每帧现读，跨模块传 ref 会把两处逻辑缠在一起），
 * 所以这里用一个 ref 复制一份，只为了显示。
 */
const playbackLatencyMs = ref(getPlaybackLatencyMs())


/**
 * 显示方式（**本机偏好**，不影响被控端）。
 *
 * 🔥 它**只决定这个浏览器怎么缩放**，所以它天然是"每个观看端各自一份"的。
 * 建会话时顺手把它报上去，只是为了让会话记录里能看出"当初是按什么开的"；
 * 之后再改只影响本机 —— 服务端没有"改显示方式"的端点，
 * 而**不加**那个端点是对的：缩放是纯客户端的事，让服务端去记它只会多一处会漂的状态。
 */
const displayMode = ref<string>(REMOTE_DISPLAY_MODES.fit)

const capability = ref<RemoteCapabilityDto | null>(null)
const capabilityLoading = ref(false)

// ── 多屏（M5.5）──

/**
 * 目标机器的屏清单。
 *
 * 🔥 **它必须和 {@link screensPending} 分开看**：`screens` 为空且不 pending 才是
 * "那台机器真的没有可抓的显示器"（没人登录）；pending 时为空是"还在问"。
 * 把两者画成同一句"没有显示器"，现场会去查显示器插没插。
 */
const screens = ref<RemoteScreenDto[]>([])

/** 刚发出了索取指令、清单还没回来。 */
const screensPending = ref(false)

/** 服务端给的一句话（"只有一块屏因此没有拼接项"之类）。 */
const screensNote = ref<string | null>(null)

const screensLoading = ref(false)

/**
 * 建会话时要抓哪块屏（`null` = 让被控端自动挑）。
 *
 * ⚠️ 它是**抓屏器认的 Id**，不是显示器序号 —— 见 `RemoteScreenDto.id` 的注释。
 */
const selectedScreenId = ref<string | null>(null)

/**
 * `pending` 时的重问定时器与次数。
 *
 * 🔥 为什么要重问：那个端点**永远先返回**（只是把索取指令发下去），
 * 清单要等客户机上行回来。不重问的话，第一次打开页面永远看到"正在读取…"，
 * 用户只能手动点刷新。
 */
let screensRetryTimer: number | null = null
let screensRetryCount = 0

// ── 画面 ──

const canvasRef = ref<HTMLCanvasElement | null>(null)

/** 当前画面的实际像素尺寸（= 被控端的抓屏尺寸，**不是** canvas 的 CSS 尺寸）。 */
const frameWidth = ref(0)
const frameHeight = ref(0)

/** 本机已经画出来的帧数（**本地计数**，与中心/被控端报的那两个数都不是一回事）。 */
const paintedFrames = ref(0)

/**
 * 最后一帧的"从抓到到画出来"耗时。
 *
 * ⚠️ **它含跨机时钟差**，所以只有在两台机器的时钟基本一致时才有意义。
 * 服务端与客户机都走 NTP 校准过的北京时间，正常情况下偏差在几十毫秒内；
 * 一旦拿到负数或大得离谱的值，说明某一边的时钟没同步 —— 此时**显示"时钟未同步"**
 * 而不是硬报一个数。报一个错的数是"看起来有数据、实际误导"，比空着更坏。
 */
const latencyMs = ref<number | null>(null)
const latencySuspect = ref(false)

/** 接收侧的问题（连接断了 / 加入失败）—— 与"被控端的问题"分开显示。 */
const viewerError = ref('')

const connectionState = ref<'disconnected' | 'connecting' | 'connected'>('disconnected')

let hub: signalR.HubConnection | null = null

// ═══════════════════════════════════════════════════════════════
// 远控窗口 + 悬浮参数面板（M7.13）
// ═══════════════════════════════════════════════════════════════
//
// 🔥 **为什么把参数搬进窗口**：改版前这一页从上到下是"选机器 → 抓哪块屏 →
//    显示方式 → 帧率 → 质量档 → 输入 → 剪贴板 → 声音 → 一堆读数 → 会话列表"，
//    而那些东西里有 **八成只在连接之后才有意义**。摆在外面有两个坏处：
//    ① 一进来先要跨过十几行与当前无关的表单才点到「连接」；
//    ② 连接之后，画面被这十几行挤到折叠线以下 —— 远控页最该大的东西反而最小。
//
// ⇒ 现在页面只有「机器列表 + 连接」，其余全部进窗口内的悬浮面板，按用途分 5 组。

/** 远控窗口是否打开（打开 ≠ 有会话：关窗后会话可能仍在推流）。 */
const windowOpen = ref(false)

/** 悬浮参数面板是否展开。默认开 —— 第一次进来要知道"参数在哪"。 */
const sheetOpen = ref(true)

/** 当前面板分组。 */
const SHEET_TABS = [
  { key: 'connect', label: '连接' },
  { key: 'picture', label: '画面' },
  { key: 'control', label: '控制' },
  // 「文件」放在控制之后：它也是"对这台机器做事"，不属于诊断读数。
  { key: 'files', label: '文件' },
  { key: 'audio', label: '声音' },
  { key: 'diag', label: '诊断' },
] as const

type SheetTab = (typeof SHEET_TABS)[number]['key']

const sheetTab = ref<SheetTab>('connect')

/**
 * 断线重连状态机。
 *
 * <p>
 * 🔥 <b>为什么不能只靠 SignalR 的 `withAutomaticReconnect`</b>：那只管**传输层**。
 * 远控断线有两条完全不同的来路，而只有第一条会被它救回来：
 * ① 浏览器到中心的 WebSocket 断了 → 自动重连 + `rejoinActive()` 重新入组；
 * ② **被控端那边掉了**（托盘重启 / 机器休眠 / 会话被中心清掉）→ 传输层好得很，
 *    但这一路流已经不存在了。这时页面必须<b>重新建一个会话</b>，
 *    而这件事只有页面能做（它知道连的是哪台机器、抓哪块屏）。
 * </p>
 *
 * <p>
 * ⚠️ <b>次数必须有上限</b>：被控端关机时如果无限重试，就是每 20 秒往中心打一次
 * 建会话请求，永远不会好，而日志里全是"会话建立失败"。
 * </p>
 *
 * <p>
 * ⚠️ 退避表 {@link RECONNECT_DELAYS} 与决策函数 `planReconnect` 都在 `../reconnect`，
 * 这里有**状态**，那里只有**规则**。
 * </p>
 */
const reconnect = ref({
  /** `idle` = 一切正常。 */
  state: 'idle' as 'idle' | 'waiting' | 'connecting' | 'failed',
  /** 第几次尝试（从 1 开始）。 */
  attempt: 0,
  /** 为什么在重连（给现场看的一句话）。 */
  reason: '',
  /** 距离下次尝试还有几秒（`waiting` 时显示倒计时）。 */
  secsLeft: 0,
})

/** 重连倒计时（`setTimeout` 链太长，用一个 250ms 的计时器更直观）。 */
let reconnectTimer: number | null = null

/** 下一次尝试的时间戳（`performance.now()` 口径）。 */
let reconnectDueAt = 0

/**
 * **用户主动**停掉的那一路。
 *
 * 🔥 没有这个标志，点「断开」会立刻被自动重连拉回来 —— 表现为"点了断开又自己连上"，
 * 而现场只会认为这个按钮坏了。
 */
let stoppingIntentionally = false

/** 正在看的机器（跟着**会话**走：会话没了它就是 `null`）。 */
const watchingAgentId = computed(() => activeSession.value?.agentId ?? null)

/**
 * 窗口**属于**哪台机器（M7.13 修正）。
 *
 * <p>
 * 🔥 <b>为什么不能直接用 `watchingAgentId`</b>：那个是"现在有哪一路会话在推"，
 * 会话一结束它就变 `null` —— 而窗口在重连期间<b>还开着</b>，标题与参数面板
 * 仍然要知道"这是哪台机器"。用 `activeSession?.agentId ?? 0` 的表现是
 * 断线那一刻标题从「DESKTOP-9VFHRMA」变成「<b>机器 #0</b>」，
 * 而那看起来像"窗口连到了另一台不存在的机器"。
 * </p>
 *
 * <p>
 * ⚠️ 它只在**打开窗口 / 换机器**时写，不随会话状态变化。
 * </p>
 */
const windowAgentId = ref<number | null>(null)

/** 窗口标题用哪台机器（`null` = 还没确定，标题退成中性的「远程控制」）。 */
const windowAgent = computed(() => windowAgentId.value ?? activeSession.value?.agentId ?? null)

/** 这台机器现在有没有一路会话（任意状态）。 */
function sessionOf(agentId: number): RemoteSessionDto | null {
  return sessions.value.find((s) => s.agentId === agentId) ?? null
}

/**
 * 已经画出来的最大帧号（**按块**，M5.5 分块编码）。
 *
 * 🔥 它挡的是"迟到的旧帧把新帧盖掉"：JPEG 解码是**异步**的，
 * 两个相邻的帧谁先解码完并没有保证。没有这道守卫，画面会时不时**倒退一帧**
 * （看起来像轻微的抖动，很容易被当成"网络卡"，而其实是本机的渲染顺序问题）。
 *
 * ⚠️ **必须按块分开记**：同一批块共享同一个序号（见 `onFrame` 的说明），
 * 用一个全局值会让第 2 块永远被判成"旧的"—— 表现是画面上只有左边那一条在动。
 */
const lastPaintedSeqByTile = new Map<number, number>()

/**
 * 每块在**整幅画面**里的位置与整幅尺寸（来自帧头，`onFrame` 每帧都刷新）。
 *
 * 🔥 为什么不让绘制回调从"这一帧的尺寸"推：分块之后这一帧只是画面的一条，
 * 画布与落点都要靠 `frameWidth/regionX` 这些**不属于这一块**的信息。
 *
 * ⚠️ `regionWidth/regionHeight` 是**这一块该占多大**（M7.7）。它和 `frameWidth`
 * 不是一回事：分块时整幅 4K、每块 1920 宽。绘制**必须**按它铺满 ——
 * 按"解码结果"的尺寸画，解码端一旦只出了半张，画面就会静静缩在左上角（＝四分之一）。
 */
const tileGeometry = new Map<
  number,
  { x: number; y: number; frameWidth: number; frameHeight: number; regionWidth: number; regionHeight: number }
>()

/**
 * 最近一帧**解码结果**的像素尺寸（M7.7）。
 *
 * 🔥 它是"4K 只显示四分之一"唯一能定性的读数：把它和「画面分辨率」（画布/区域尺寸）
 * 摆在一起，一眼就能看出是**编码端缩了**还是**这个浏览器只解出一角**。
 * 不显示它，现场只剩两块黑 + 一句猜。
 */
const decodedSize = ref<{ width: number; height: number } | null>(null)

/** 解码尺寸与该占区域对不上时的一句话（对得上为 `null`）。 */
const frameSizeNote = ref<string | null>(null)

/**
 * 上一次算"尺寸对不对得上"用的键（`解码尺寸|区域尺寸|整幅宽`）。
 *
 * 🔥 只在键变了才重算 —— 每帧拼字符串 / 调判据的代价虽小，但那是 60 Hz 的热路径。
 */
let lastSizeCheckKey = ''

/**
 * 这一路会话**实际在抓的那块屏**的真实像素尺寸（拿不到为 `null`）。
 *
 * <p>
 * 🔥 为什么不能用帧头里的 `frameWidth` 代替："整幅在码流坐标系里有多大"
 * 与"屏幕有多大"是两件事 —— 被控端降采样之后前者会变小
 * （M7.10 起按画面自适应，单屏 1080p 不降、三屏 5760 降到 3840）。
 * 用它跟自己对，永远对不出"缩了多少"。
 * </p>
 *
 * <p>
 * 取的是**会话正在抓的那块**（`activeSession.screenId`），不是下拉框选的那块：
 * 用户改了下拉还没点"切到这块屏"时，两者不同，而画面属于前者。
 * `screenId` 为空（"自动"）时：有拼接项就用拼接（多屏），否则就是唯一那块屏。
 * </p>
 */
const sessionDisplaySize = computed<{ width: number; height: number } | null>(() => {
  const list = screens.value

  if (list.length === 0) return null

  const wanted = activeSession.value?.screenId ?? null

  const hit = wanted
    ? (list.find((s) => s.id === wanted) ?? null)
    : (list.find((s) => s.isVirtualDesktop) ?? (list.length === 1 ? list[0] : null))

  if (!hit || hit.width <= 0 || hit.height <= 0) return null

  return { width: hit.width, height: hit.height }
})

/**
 * 被控端**编码之前**把画面缩小了多少（没缩 ⇔ `null`）。
 *
 * <p>
 * 🔴 现场："多屏合并显示画面不对"。三屏拼接 5760×2160 经 MJPEG 压到 1920×720，
 * 每块屏只剩 640 像素。画面本身是完整的（几何 bug 在别处），但"糊"这件事
 * 必须有地方说出来 —— 否则现场只会以为是远控坏了，而正确的处置是"切到单块屏"。
 * </p>
 *
 * <p>
 * 用 computed 而不是在 `onFrame` 里算：屏幕清单是异步到的，
 * 帧可能先到、清单后到。computed 会自动跟着两边重算，不必手动做失效。
 * </p>
 */
const encodingScaleNote = computed(() => {
  if (frameWidth.value <= 0 || frameHeight.value <= 0) return null

  return describeEncodingScale(sessionDisplaySize.value, {
    width: frameWidth.value,
    height: frameHeight.value,
  })
})

/** 当前这一路的块数（1 = 没分块）。块数变了就要把旧缓冲全部丢掉。 */
let activeTileCount = 1

const activeSession = computed(
  () => sessions.value.find((s) => s.sessionId === activeSessionId.value) ?? null,
)

vueWatch(
  () => activeSession.value?.targetFps ?? null,
  (fps) => {
    // 会话起来 / 帧率变了 ⇒ 重算预算。⚠️ 拿不到却**不能**当成 0：
    // 那会把预算压到下限，等于"一帧都不缓冲"（见 playbackLatency 的用例）。
    playbackLatencyMs.value = setPlaybackLatencyForFps(fps)
  },
  { immediate: true },
)
/**
 * 会话运行中，用户想把画面换到**另一块屏**（M7.7）。
 *
 * 🔥 判据是"用户**显式选了**某块屏 ＋ 与当前这一路不同"，不是"下拉框的值变了"：
 * `screenId = null`（自动）时中心回填的是被控端实际挑中的那一块，
 * 拿 `null` 去比会永远显示"不同" —— 那会让页面一打开就挂着一个切换按钮，
 * 而用户压根没想换屏。
 */
const screenSwitchPending = computed(() => {
  if (activeSessionId.value === null) return false

  const wanted = selectedScreenId.value

  if (wanted === null) return false

  const current = activeSession.value?.screenId

  // ⚠️ 会话列表还没刷新到这一路时（`undefined`）**不下结论** ——
  // 那会凭空挂出一个"要切换"的按钮，而用户只是刚点完开始。
  if (current === undefined) return false

  return current !== wanted
})

const hasFrame = computed(() => paintedFrames.value > 0)

/**
 * 被控端报的"桌面已经静止了多久"（毫秒；`null` = 这台机器不提这件事）。
 *
 * 🔥 这一格存在的理由只有一个：**一帧都不来时，页面原来只能猜**。
 * "画面卡住"与"桌面真的没动"在画面上完全一样（都是一张不动的旧图），
 * 而处置相反 —— 一个要查链路，一个什么都不用做。
 * 被控端的抓屏器一直知道是哪一种，只是以前不说。
 */
const desktopIdleMs = computed(() => activeSession.value?.desktopIdleMs ?? null)

/** 值得额外提一句"桌面静止中"（判据比那一格读数更严，见 desktopIdle.ts）。 */
const desktopIdleNotice = computed(() => isDesktopIdleNotice(desktopIdleMs.value))

/**
 * **"这一帧几乎是纯色"的那句话**（M7.6）。
 *
 * <p>
 * 🔥 治的是一个被误判过两轮的现场形态：**画面全黑，但被控端动一下鼠标就恢复**，
 * 而两端日志干干净净。前两轮分别怀疑过抓屏坏了、编码器坏了 —— 都不是：
 * 那台机器静置之后关掉了显示器（或起了黑屏屏保），DDA 抓到的就是一整片黑。
 * </p>
 *
 * <p>
 * ⇒ 页面这时必须**说出这句话**。之前它只会显示一块黑 ——
 * 而那正好是"抓屏坏了 / 编码器坏了 / 真的是黑桌面"三种情况的共同外观。
 * 判据与措辞见 `blankFrame.ts`（⚠️ 它同时给出"息屏"与"纯色壁纸"两种解释，不替人下结论）。
 * </p>
 *
 * <p>
 * ⚠️ **只在关键帧上算，且只在单块时算**：P 帧本来就小（它只描述差异），
 * 拿它算 bpp 会把每一帧都判成纯色；而分块时这一帧的像素只是整幅的一角。
 * </p>
 */
const blankFrameNote = ref<string | null>(null)

/** 被控端说自己发了多少帧但中心一个都没收到 —— 这条链路上最值得报警的一种状态。 */
const framesLostInTransit = computed(() => {
  const sent = activeSession.value?.agentStats?.framesSent ?? null
  const received = activeSession.value?.stats?.framesReceived ?? 0
  if (sent === null || sent <= 0) return null
  return Math.max(0, sent - received)
})

/**
 * 音频版的同一个读数（被控端发了多少段、中心一段都没收到）。
 *
 * 🔥 **必须与视频分开算**：视频掉帧和音频断流在页面上长得一模一样（"卡了"），
 *    但处置相反 —— 一个去查网络带宽，一个去查被控机的声卡。
 *    合成一个总数就等于把这两种情况重新糊回一起。
 */
const audioLostInTransit = computed(() => {
  const sent = activeSession.value?.agentStats?.audioFramesSent ?? null
  const received = activeSession.value?.stats?.audioFramesReceived ?? 0
  if (sent === null || sent <= 0) return null
  return Math.max(0, sent - received)
})

const targetAgent = computed(() => agents.value.find((a) => a.id === targetAgentId.value) ?? null)

// ═══════════════════════════════════════════════════════════════
// 展示辅助
// ═══════════════════════════════════════════════════════════════

function stateLabel(state: string): string {
  switch (state) {
    case REMOTE_SESSION_STATES.requested:
      return '已请求'
    case REMOTE_SESSION_STATES.streaming:
      return '推流中'
    case REMOTE_SESSION_STATES.stopped:
      return '已结束'
    case REMOTE_SESSION_STATES.failed:
      return '失败'
    default:
      return state
  }
}

function stateClass(state: string): string {
  switch (state) {
    case REMOTE_SESSION_STATES.streaming:
      return 'ok'
    case REMOTE_SESSION_STATES.failed:
      return 'err'
    case REMOTE_SESSION_STATES.requested:
      return 'warn'
    default:
      return ''
  }
}

function agentName(id: number): string {
  return agents.value.find((a) => a.id === id)?.displayName ?? `机器 #${id}`
}

/**
 * 「文件」页要用的两个参数。
 *
 * 🔥 取 **`windowAgentId`** 而不是 `watchingAgentId`（铁律 37）：远控会话断了、画面没了，
 * 用户仍然可能要继续往这台机器收发文件 —— 文件走 HTTP，与推流是两条独立的路。
 * 用 `activeSession` 的话，"会话一结束，文件页就不知道自己在跟谁说话"。
 */
const filesAgentName = computed(() =>
  windowAgentId.value === null ? '未选择机器' : agentName(windowAgentId.value),
)

/**
 * 这台机器现在是否可达。
 *
 * ⚠️ `unstable` 也算可达：那是"心跳有点迟"，长连接通常还在 ——
 * 判成离线会让文件页拒绝一个其实能用的浏览请求。
 */
const filesAgentOnline = computed(() => {
  const id = windowAgentId.value
  if (id === null) return false

  const state = agents.value.find((a) => a.id === id)?.state
  return state === 'online' || state === 'unstable'
})

/**
 * **这一路走了 MJPEG 降级路径时**给一句说明，其余情况为 `null`（什么都不显示）。
 *
 * ⚠️ 判据与文案都在 `../codecFallback`（纯函数、有用例）—— 这里只负责
 * 把它接到"当前正在看的那一路"上。**不要**在组件里重写这段话：
 * 它说的是"这一路现在是什么、为什么不如人"，是本仓最容易长期说谎的一处，
 * 必须留在能被断言的地方（见 `codecFallback.test.ts` 的头注释）。
 */
const codecFallbackNote = computed(() => describeCodecFallback(activeSession.value?.codec))

/** 本页探测出来的能力（异步填）。⚠️ 页面显示与建会话**共用这一个来源**。 */
const viewerProbe = ref<ViewerProbeResult | null>(null)

/** 探测只做一次（首次调用开始，之后复用同一个 promise）。 */
let viewerProbeInFlight: Promise<ViewerProbeResult> | null = null

/**
 * 确保探测跑过一次，并把它返回。
 *
 * 🔥 **为什么必须共用同一个结果**：页面上显示的声明与实际发出去的声明要是两套值，
 * 现场就会拿一个错的声明去解释"为什么走了 MJPEG" —— 而那正是第 7 步特意要避免的事。
 * 那时用一个 computed 就够了；现在探测变成**异步**（`isConfigSupported` 返回 Promise），
 * computed 表达不了"在飞"这个状态，所以换成"一个 ref ＋ 一个共享的 promise"。
 *
 * ⚠️ 探测**不抛**（`probeViewerVideo` 自己的合同），这里的 `catch` 是第二道：
 * 万一它还是抛了，也要落到"只声明 MJPEG、让中心回落"，而不是"建会话失败" ——
 * 后者会让一次探测问题变成"看不了"，而正确处置恰恰相反。
 */
async function ensureViewerProbe(): Promise<ViewerProbeResult> {
  if (viewerProbe.value) return viewerProbe.value

  if (!viewerProbeInFlight) {
    viewerProbeInFlight = probeViewerVideo()
      .catch(() => mjpegOnlyResult('探测本身出错了（已按"只声明 MJPEG"处理）'))
      .then((result) => {
        viewerProbe.value = result
        return result
      })
  }

  return viewerProbeInFlight
}

const viewerVideoDeclaration = computed(() => {
  const probe = viewerProbe.value

  return probe ? probe.capabilities.codecs.join(' / ') : '探测中…'
})

/** 两个方向的帧计数拼成一句人话 —— 这是这一页最重要的一个诊断。 */
function frameDiagnosis(session: RemoteSessionDto): string {
  const received = session.stats?.framesReceived ?? 0
  const sent = session.agentStats?.framesSent ?? null

  if (session.state === REMOTE_SESSION_STATES.requested) {
    return '还没有开始推流（已请求，等被控端）'
  }

  if (sent === null) {
    return received === 0 ? '中心还没收到任何一帧' : `中心已收 ${received} 帧（被控端尚未上报发出量）`
  }

  if (received === 0) {
    return `被控端说它发了 ${sent} 帧，但中心一帧都没收到 ⇒ 麻烦在链路上（Hub / 网络）`
  }

  const lost = sent - received
  if (lost > Math.max(3, sent * 0.1)) {
    return `被控端发 ${sent} / 中心收 ${received} ⇒ 掉了 ${lost} 帧，查链路`
  }

  return `被控端发 ${sent} / 中心收 ${received} ⇒ 链路是通的${
    sent < 5 ? '，帧少说明被控端编不动（查被控机负载）' : ''
  }`
}

// ═══════════════════════════════════════════════════════════════
// 加载
// ═══════════════════════════════════════════════════════════════

async function refreshSessions() {
  loading.value = true
  loadError.value = ''

  try {
    sessions.value = await api.remoteSessions()

    // ⚠️ 正在看的那个会话已经不在列表里了（被控端掉了 / 别人结束了 / 超时被清掉）——
    //    必须**当场**把画面那格收掉。不然页面会一直挂着最后一帧，
    //    而它已经是一张永远不动的旧图，看起来像"卡住了"。
    if (activeSessionId.value && !sessions.value.some((s) => s.sessionId === activeSessionId.value)) {
      await teardownViewer()

      // 🔥 窗口还开着 ⇒ 这是"断线"，不是"我不看了"：安排重连，而不是留一块黑。
      //    ⚠️ 判据必须是 `windowOpen`：关窗之后会话消失是**预期**的（用户已经不看）。
      if (windowOpen.value && !stoppingIntentionally) {
        scheduleReconnect('这一路会话在中心已经不存在了（那台机器掉线，或长时间没有帧被清理）。')
      } else if (!windowOpen.value) {
        viewerError.value = '这路会话已经结束了（可能被其它窗口结束、或长时间没有帧被中心清理）。'
      }
    }
  } catch (err) {
    loadError.value = err instanceof ApiError ? err.message : '读取会话列表失败。'
  } finally {
    loading.value = false
  }
}

async function refreshAgents() {
  try {
    const list = await api.agents()
    agents.value = list.items

    if (targetAgentId.value === null && list.items.length > 0) {
      // 默认选一台在线的（离线的必然报 AgentOffline，让用户先点一次错的没必要）。
      targetAgentId.value = list.items.find((a) => a.state === 'online')?.id ?? list.items[0].id
    }
  } catch {
    /* 在线表读不到不影响已有会话的观看，静默降级 */
  }
}

async function loadCapability() {
  capability.value = null

  if (targetAgentId.value === null) return

  capabilityLoading.value = true

  try {
    capability.value = await api.remoteCapabilities(targetAgentId.value)
  } catch {
    // 探测读不到不是错误：它只影响"建会话之前能不能给个提示"。
    capability.value = null
  } finally {
    capabilityLoading.value = false
  }
}

/** `pending` 时最多重问几次（服务端说约 1 秒，4 次 ≈ 4 秒够一次冷启动往返）。 */
const SCREEN_RETRY_LIMIT = 4

function clearScreensRetry() {
  if (screensRetryTimer !== null) {
    window.clearTimeout(screensRetryTimer)
    screensRetryTimer = null
  }
}

/**
 * 读目标机器的**屏清单**（M5.5 多屏）。
 *
 * 🔥 这个端点**永远先返回**：它只是把"报一下你有几块屏"的指令发下去，
 * 清单要等客户机上行回来。所以第一次调几乎一定拿到 `pending: true`，
 * 本函数会自己约 1 秒后重问 —— 否则页面会永远停在"正在读取…"。
 *
 * ⚠️ 读不到**不是错误**：不选屏照样能开始观看（被控端自己挑，多屏时取虚拟桌面）。
 * 所以这里只写一句说明，不弹红条。
 */
async function loadScreens(refresh = false) {
  clearScreensRetry()

  screens.value = []
  screensPending.value = false
  screensNote.value = null

  const agentId = targetAgentId.value

  if (agentId === null) {
    selectedScreenId.value = null
    return
  }

  screensLoading.value = true

  // ⚠️ 记住这次问的是谁：等待期间用户可能又换了一台机器，
  //    迟到的响应不许覆盖新的那一台（否则会列出上一台的屏）。
  const askedFor = agentId

  try {
    const dto = await api.remoteScreens(agentId, refresh)

    if (targetAgentId.value !== askedFor) return

    screens.value = dto.screens ?? []
    screensPending.value = dto.pending
    screensNote.value = dto.note

    // 选中的那块可能已经不在了（换了机器 / 拔了屏）⇒ 回到"自动"。
    // ⚠️ 不能留着那个 Id：它会被原样传给建会话请求，而被控端只会回一句"指定屏幕不存在"。
    if (
      selectedScreenId.value !== null
      && !screens.value.some((s) => s.id === selectedScreenId.value)
    ) {
      selectedScreenId.value = null
    }

    if (dto.pending && screensRetryCount < SCREEN_RETRY_LIMIT) {
      screensRetryCount += 1

      screensRetryTimer = window.setTimeout(() => {
        screensRetryTimer = null
        void loadScreens(false)
      }, 1000)
    } else if (!dto.pending) {
      screensRetryCount = 0
    }
  } catch (err) {
    if (targetAgentId.value !== askedFor) return

    // ⚠️ 502 与"读屏失败"不是一回事：那是**这台机器此刻没有长连接**。
    //    它同时意味着"接下来点开始观看多半也会失败"，所以要说清，
    //    而不是笼统地写一句"读不到屏清单"（那句话会让人以为功能坏了）。
    screensNote.value =
      err instanceof ApiError && err.status === 502
        ? `${err.message} —— 它可能刚掉线；不选屏照样能点开始，但多半也会失败。`
        : '读不到这台机器的屏清单（不影响开始观看：不选屏时由被控端自己挑）。'
  } finally {
    if (targetAgentId.value === askedFor) screensLoading.value = false
  }
}

/** 用户点"重新读取"：明确要求重采，并重置重问计数。 */
function refreshScreens() {
  screensRetryCount = 0
  void loadScreens(true)
}

/** 换了目标机器：能力与屏清单都要重读，且重问计数归零。 */
function onTargetChanged() {
  screensRetryCount = 0
  void loadCapability()
  void loadScreens()
}

// ═══════════════════════════════════════════════════════════════
// Hub
// ═══════════════════════════════════════════════════════════════

async function connectHub() {
  if (hub) return

  connectionState.value = 'connecting'

  const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/remote')
    // ⚠️ 重连**不重放**帧：这是有意的。画面是"只关心最新"的数据，
    //    补发断线期间攒下的帧只会让人看一段快进的旧录像。
    //    重连之后由 `rejoinActive` 重新入组，从下一帧接着看。
    .withAutomaticReconnect([0, 2000, 5000, 10000])
    .build()

  connection.on(REMOTE_HUB_METHODS.frame, (frame: RemoteStreamFrame) => onFrame(frame))

  // 🔥 音频走**第二条独立的消息**，不是复用 `Frame` 再按载荷分叉。
  //    理由见 `REMOTE_HUB_METHODS.audio` 的注释：视频"忙就丢"、音频"丢一段就是断点"，
  //    两套**相反**的调度策略合进一个 switch 里就等于把它们的区别抹掉。
  connection.on(REMOTE_HUB_METHODS.audio, (frame: RemoteAudioFrame) => onAudioFrame(frame))

  // 🔥 剪贴板是**推送**而不是"读一次返回一次"：读的动作只是请被控机去读，
  //    真正的文本经这条消息回来（因为服务端那条 HTTP 早就返回了）。
  // ⚠️ 订阅是全局注册一次，而这条消息只发到"当前分组"——
  //    换路时不清本地那一格，上一路迟到的推送就会变成"别人的剪贴板"（见 onClipboardChanged）。
  connection.on(REMOTE_HUB_METHODS.clipboardChanged, (dto: RemoteClipboardDto) =>
    onClipboardChanged(dto),
  )

  connection.on(REMOTE_HUB_METHODS.sessionChanged, (next: RemoteSessionDto) => {
    if (!next?.sessionId) return

    const index = sessions.value.findIndex((s) => s.sessionId === next.sessionId)

    if (index >= 0) {
      sessions.value[index] = next
    } else {
      sessions.value = [next, ...sessions.value]
    }

    // 🔥 正在看的这一路被中心判终结（被控端掉线 / 启动失败）⇒ 这是"断线"，不是"没问题"。
    //    只更新列表是不够的：画面会停在最后一帧，而页面上没有任何一处会说话。
    if (
      next.sessionId === activeSessionId.value
      && (next.state === REMOTE_SESSION_STATES.stopped || next.state === REMOTE_SESSION_STATES.failed)
    ) {
      const reason = next.endedReason
        ? `这一路结束了：${next.endedReason}`
        : '这一路被中心结束了（被控端可能掉线了）。'

      // ⚠️ 先退组再排重连：不退组的话，`Join` 会因为旧会话 Id 还在而失败，
      //    表现是"重连了 5 次全失败"，而真正的原因只是本地没松手。
      void (async () => {
        await teardownViewer()
        scheduleReconnect(reason)
      })()
    }
  })

  connection.onreconnecting(() => (connectionState.value = 'connecting'))
  connection.onreconnected(async () => {
    connectionState.value = 'connected'
    await rejoinActive()

    // 传输层自己回来了：那条"重连中"的提示要跟着收掉，否则它会一直挂在那儿。
    if (reconnect.value.state === 'waiting') cancelReconnect()
  })

  connection.onclose(() => {
    connectionState.value = 'disconnected'

    // 🔴 **必须把 hub 置空**：`connectHub()` 第一句是 `if (hub) return`，
    //    不置空就等于"重建连接这件事永远不会发生"。
    hub = null

    // 自动重连的 4 次（0/2/5/10 秒）已经用完了 ⇒ 这一路彻底断了。
    // ⚠️ 这里**不**立刻重连 hub：先让用户看到"断开"，由重连条统一负责节奏
    //    （它会调 `startSession`，而 `startSession` 内部的 `watch` 会重建 hub）。
    scheduleReconnect('实时通道断开（自动重连已放弃）。')
  })

  try {
    await connection.start()
    connectionState.value = 'connected'
    hub = connection
  } catch {
    connectionState.value = 'disconnected'
    viewerError.value = '实时通道连不上 —— 画面收不到。请刷新页面重试。'
  }
}

/**
 * 重新加入当前会话的分组（重连之后用）。
 *
 * 🔥 **必须重加**：由于上面的"重连不重放"，断线时那条连接已经被移出分组了；
 *    不重新 `Join` 就会变成"连接是好的、却再也收不到任何一帧" ——
 *    而页面上的连接指示灯是绿的。这一类"绿着但没数据"最难查。
 */
async function rejoinActive() {
  if (!hub || !activeSessionId.value) return

  try {
    await hub.invoke<RemoteWatchAckDto>('Join', activeSessionId.value)
  } catch {
    /* 会话可能已经结束了，交给下一次列表刷新去收尾 */
  }
}

async function disconnectHub() {
  const connection = hub
  hub = null

  if (!connection) return

  try {
    await connection.stop()
  } catch {
    /* 停机失败无所谓：浏览器关掉这条连接就没了 */
  }

  connectionState.value = 'disconnected'
}

// ═══════════════════════════════════════════════════════════════
// 画面 + 声音（M5.3：排期显示 + 音画对齐）
// ═══════════════════════════════════════════════════════════════

/**
 * **会话时钟**：把被控端的"会话单调毫秒"映射到本机的 `performance.now()`。
 *
 * 🔥 视频与音频**必须共用这一个实例** —— 各持一个的话，两边的锚点是分别用
 *    "各自的最小延迟"估出来的，于是"对齐"变成了"两套估计碰巧差不多"，
 *    而任何一次抖动都会让它们往不同方向偏。共用一个是"对齐"这个词唯一的含义。
 */
const clock = new SessionClock()

/** 视频抖动缓冲（按时间轴排期显示）。**恒指第 0 块** —— 统计那一格看的就是它。 */
let videoBuffer: RemoteVideoBuffer | null = null

/**
 * **每块一个**抖动缓冲（M5.5 分块编码；没分块时只有 0 号一个）。
 *
 * 🔥 分块的每一块都是一路**独立码流**（各有 SPS/PPS 与参考链），
 * 所以每块必须有独立的解码器 —— 共用一个会让几条参考链互相污染，
 * 表现是"画面轮流花屏"，而两端日志都干净。
 */
const videoBuffers = new Map<number, RemoteVideoBuffer>()

/** 音频播放器（Opus → PCM → worklet 环形缓冲）。 */
let audioPlayer: RemoteAudioPlayer | null = null

/**
 * 音频链的状态（**四态**，与页面上要说的四种话一一对应）。
 *
 * ⚠️ 刻意比被控端上报的三态多一个 `idle`：那是"这一路压根还没开始建"，
 *    而页面上不能把它和 `starting` 合并显示 —— 一个该等着，另一个也该等着，
 *    但**它们的原因完全不同**，合并之后一旦卡住就没人知道卡在哪。
 */
type AudioUiState = 'idle' | 'starting' | 'playing' | 'unavailable'

const audioUi = ref<AudioUiState>('idle')

/** 音频不可用 / 起不来时的一句人话（直接显示）。 */
const audioMessage = ref('')

/** 本机静音（**只影响这个浏览器**：不会去动被控端，也不影响别人）。 */
const audioMuted = ref(false)

/** 开始建链时还没送进解码器的段（`addModule` 是异步的，这期间会来几段）。 */
let pendingAudio: RemoteAudioFrame[] = []

/** 上面那个缓冲的上限。约 1 秒；超了从最旧的丢 —— 那是"还没开始播就已经落后 1 秒"。 */
const PENDING_AUDIO_LIMIT = 60

/** 音频统计（每 250 ms 刷一次，不逐段刷新）。 */
const audioStats = ref<AudioStats | null>(null)

/** 视频缓冲统计。 */
const videoStats = ref<VideoBufferStats | null>(null)

/**
 * 视频**解码器**这一侧的统计。
 *
 * 🔥 与 {@link videoStats} **刻意分开显示**，不合成一张表：一个是"排期丢了几帧"
 * （画面跳一下），另一个是"解码链断了几次、等了多少个关键帧"（画面花到那一刻）。
 * 合成一张表，现场就会把"帧率被降下来"读成"解码故障"。
 */
const videoDecoderStats = ref<FrameDecoderStats | null>(null)

/** 最近一次"真的画上去"的那一帧：会话单调时刻 + 画上去的本机时刻。 */
const painted = { monoMs: null as number | null, atPerf: 0 }

/**
 * **音画偏差**（毫秒）：正 = 画面超前于声音，负 = 声音超前于画面。
 *
 * 🔥 它不是我按公式算出来的，而是**测出来的**：比的是"实际在屏幕上的那一帧是哪一刻采的"
 *    与"实际在响的那一段是哪一刻采的"，两者各自带上自己的解码耗时与排队深度。
 *    所以它有意义 —— 一个恒等于 0 的读数说明测量写错了，不是说明做得好。
 */
const avOffsetMs = ref<number | null>(null)

/**
 * 音频的端到端时延（**依赖两端时钟一致**）。
 *
 * ⚠️ 画面那一个不在这里：它就是上面那个「帧时延」`latencyMs`
 * （同样用**真正上屏**的那一刻算的）。再存一份的话，两处各自更新，
 * 迟早出现"同一个量在页面上有两个不一样的值"。
 */
const audioLatencyMs = ref<number | null>(null)

/** 时钟是否可用（旧被控端没有 `capturedMonoMs`）。 */
const clockReady = ref(false)

// ── 与 https / 安全上下文有关 ──

/** 本页是不是跑在安全上下文里（决定 WebCodecs / AudioWorklet 存不存在）。 */
const isSecureContext = ref(true)

/** 服务端告诉我们的 https 端口与根证书地址。 */
const trustInfo = ref<TrustInfoDto | null>(null)

/** 横幅的 https 地址 —— 用**当前主机名** + 服务端给的端口，免得让用户自己拼。 */
const httpsAddress = computed(() => {
  const port = trustInfo.value?.httpsPort

  if (!port || typeof window === 'undefined') return null

  return `https://${window.location.hostname}:${port}${window.location.pathname}`
})

/**
 * 建立**某一块**的视频缓冲（幂等）。**每次开始看一路时重建**：
 * 上一路的锚点、`lastPaintedSeq`、队列里没画完的帧全都不能留 ——
 * 留下任何一样都会让新一路的开头几帧显示不出来或显示成上一路的画面。
 *
 * @param codec 这一路的编码格式（由**首帧的帧头**给）。它决定造哪种解码器 ——
 *   而缓冲本身**不知道**这个参数的含义（见 `remotePlayback.ts` 的类注释）。
 * @param tileIndex 第几块（M5.5 分块编码；没分块时恒为 0）。
 *
 * 🔥 **分块之后每一块都要自己的缓冲与解码器**：它们各自是一路独立码流
 * （各有自己的 SPS/PPS 与参考链），共用一个解码器会互相污染参考链 ——
 * 表现是"几条画面轮流花屏"，而两端日志都干净。
 */
function ensureVideoBuffer(codec: string, tileIndex: number): RemoteVideoBuffer {
  const existing = videoBuffers.get(tileIndex)

  if (existing) return existing

  const buffer = new RemoteVideoBuffer(
    clock,
    (source, size, meta) => {
      const canvas = canvasRef.value

      if (!canvas) return

      // ⚠️ 再检查一次：解码是异步的，用户在解码期间可能已经切走了。
      const lastSeq = lastPaintedSeqByTile.get(tileIndex) ?? -1

      if (meta.seq <= lastSeq) return

      // 这一块在整幅画面里的位置（`onFrame` 每帧刷新；没拿到时按"整幅 = 这一帧"处理，
      // 那是**唯一**安全的默认值 —— 拿 0 当整幅会让画面被缩到左上角）。
      const geometry = tileGeometry.get(tileIndex) ?? {
        x: 0,
        y: 0,
        frameWidth: size.width,
        frameHeight: size.height,
        regionWidth: size.width,
        regionHeight: size.height,
      }

      const frameW = geometry.frameWidth > 0 ? geometry.frameWidth : size.width
      const frameH = geometry.frameHeight > 0 ? geometry.frameHeight : size.height

      // 🔥 画布尺寸跟着**整幅画面**走，不是这一块 —— 分块时每块都会被画进
      //    同一个画布的对应位置（见下面的 drawImage 的四参数形式）。
      if (canvas.width !== frameW || canvas.height !== frameH) {
        canvas.width = frameW
        canvas.height = frameH
      }

      const ctx = canvas.getContext('2d')

      if (!ctx) return

      // ── M7.7：解码尺寸 vs 该占区域 ──
      //
      // 🔥 只拿第 0 块当整幅的代表（分块时每块的区域都小于整幅，混着记会互相覆盖）。
      //    判据的键是"两个尺寸"，不是"每帧" —— 每帧拼一个字符串纯属给 GC 添活。
      if (tileIndex === 0) {
        const key = `${size.width}x${size.height}|${geometry.regionWidth}x${geometry.regionHeight}|${frameW}`
        const previous = decodedSize.value

        if (previous === null || sizeDiffers(previous, size)) {
          decodedSize.value = { width: size.width, height: size.height }
        }

        if (key !== lastSizeCheckKey) {
          lastSizeCheckKey = key

          frameSizeNote.value = describeSizeMismatch(
            { width: geometry.regionWidth, height: geometry.regionHeight },
            size,
            // 🔥 单块时"这一块"就是整幅 —— 此时区域比帧头报的整幅**小**，
            //    意思就是"编码端把整幅缩小之后才编的"，必须说"整幅画面"；
            //    说成"这一块区域"会把排查引到分块/ROI 上去（那是另一件事）。
            activeTileCount === 1 ? geometry.regionWidth : frameW,
          )
        }
      }

      // 🔥 **尺寸恒取区域**（M7.7）。原来取的是解码结果：解码端一旦只出了半张，
      //    画面就静静缩在左上角（＝四分之一），而页面上没有任何一处会说话。
      const rect = pickDrawRect(
        { x: geometry.x, y: geometry.y, width: geometry.regionWidth, height: geometry.regionHeight },
        size,
      )

      // 🔥 `source` 是 `HTMLImageElement`（MJPEG）或 `VideoFrame`（H.264/HEVC）——
      //    两者都能直接进 `drawImage`，所以**上屏这一层两条路完全共用**。
      //    这不是巧合，是当初选 `VideoFrame` 那条路的主要原因之一。
      //
      // ⚠️ 分块时要给**落点与尺寸**（四个参数）：两参数版本会把每一块都拉满整个画布
      //    —— 症状是"几条画面互相覆盖"，而那看起来像编码坏了。
      ctx.drawImage(source, rect.x, rect.y, rect.width, rect.height)

      lastPaintedSeqByTile.set(tileIndex, meta.seq)

      paintedFrames.value += 1
      frameWidth.value = canvas.width
      frameHeight.value = canvas.height

      // 🔥 记的不是"该显示的时刻"而是**真正画上去的这一刻**：
      //    两者之间差着解码耗时与 rAF 的调度滞后，而那正是音画偏差的主要成分。
      //    ⚠️ 分块时这个读数会被"最后画上的那一块"更新 —— 它们本来就共享同一批时戳
      //    （同一个 `CapturedMonoMs`），所以它仍然是"这一帧的显示时刻"。
      painted.monoMs = meta.monoMs
      painted.atPerf = performance.now()

      updateLatency(meta.unixMs)
    },
    // 🔥 解码器**按这一路的编码格式选**。MJPEG 与 H.264/HEVC 的全部差别就在这一个分支里 ——
    //    而不是散在排期逻辑里（把两套相反的丢帧策略写进同一段流程，正是第 8 步要修的事）。
    (sink) =>
      codec === REMOTE_CODECS.mjpeg
        ? createMjpegDecoder(sink)
        : createWebCodecsDecoder(codec, sink),
  )

  buffer.start()

  videoBuffers.set(tileIndex, buffer)

  // `videoBuffer` 恒指第 0 块：统计那一格看的就是它（各块的帧率/队列深度是一样的，
  // 分开显示只会让页面上多出 N 个几乎相同的数字）。
  if (tileIndex === 0) videoBuffer = buffer

  return buffer
}

/**
 * 丢掉**全部**块的缓冲（换会话 / 退订时用）。
 *
 * ⚠️ 必须逐块 `stop()`：只清第 0 块会让其余块的 rAF 循环一直空转，
 * 而且它们还握着解码器与 WebCodecs 的 `VideoFrame`（那是要手动 `close()` 的 GPU 资源）。
 */
function stopAllVideoBuffers() {
  for (const buffer of videoBuffers.values()) {
    try {
      buffer.stop()
    } catch {
      /* 已经在停了 —— 不因为"多停一次"把整条收尾流程打断 */
    }
  }

  videoBuffers.clear()

  videoBuffer = null
}

/**
 * 把一帧喂给**它那一块**的视频缓冲。
 *
 * 🔥 **这里不再直接解码、也不再直接画**（M5.1 的行为）：那一版把网络抖动
 *    原样变成了画面抖动 —— 一帧晚到 30 ms，它就在屏幕上多停 30 ms。
 *    单看画面时看不出来，但它让"画面现在显示的是哪一刻"根本没有定义，
 *    于是音画对齐无从谈起。现在这一帧只是进缓冲，什么时候上屏由控制时钟说了算。
 */
function onFrame(frame: RemoteStreamFrame) {
  if (!frame?.header) return

  // 不认识的会话（别人开的 / 刚被结束的）：丢掉，别画到当前这一格上。
  if (frame.header.sessionId !== activeSessionId.value) return

  const header = frame.header

  // ── 分块 / ROI（M5.5）：先把"这块在整幅的哪儿"记下来 ──
  //
  // 🔥 三个字段都可能缺（旧被控端）：`tileCount` 缺省按 1，`frameWidth` 缺省按这一帧的宽。
  //    这样"老客户端 + 新页面"落在**完全等价于从前**的行为上（整幅 = 这一帧）。
  const tileIndex = header.tileIndex ?? 0
  const tileCount = header.tileCount && header.tileCount > 0 ? header.tileCount : 1

  // 块数变了（比如 8K 屏从"单路整编"切成"2 块"）：旧缓冲的参考链全都作废，
  // 留着会让新码流的第一帧去接旧流的状态 —— 表现是"切块之后花屏到下一个关键帧"。
  if (tileCount !== activeTileCount) {
    stopAllVideoBuffers()

    tileGeometry.clear()
    lastPaintedSeqByTile.clear()

    activeTileCount = tileCount
  }

  // 整幅尺寸（画布用）与**这一块**该占的尺寸（绘制用）—— 单块时两者相等。
  const wholeWidth = header.frameWidth && header.frameWidth > 0 ? header.frameWidth : header.width
  const wholeHeight = header.frameHeight && header.frameHeight > 0 ? header.frameHeight : header.height

  // ⚠️ `width/height` 是**这一块**的像素尺寸（真正的编码区域）。缺了就按整幅算 ——
  //    那是唯一安全的默认值：拿 0 当区域会让 drawImage 收到 0 宽，整块不显示。
  const regionWidth = header.width && header.width > 0 ? header.width : wholeWidth
  const regionHeight = header.height && header.height > 0 ? header.height : wholeHeight

  tileGeometry.set(tileIndex, {
    x: header.regionX ?? 0,
    y: header.regionY ?? 0,
    frameWidth: wholeWidth,
    frameHeight: wholeHeight,
    regionWidth,
    regionHeight,
  })

  // 旧被控端不带会话单调时刻：时钟建不起来，缓冲会退回"收到就画最新的"，
  // 页面上会明确说"无法对齐"。这里不拦，是因为画面本身仍然值得看。
  //
  // ⚠️ 格式取**首帧帧头**里的那个：一路会话的编码格式在建立时就定死了
  //    （被控端只开一个编码器），所以"用第一帧决定解码器"是安全的。
  // ── M7.6：这一帧是不是"几乎一整片纯色" ──
  //
  // 🔥 只在**关键帧**上判：P 帧本来就小（它只描述差异），拿它算 bpp 会把每一帧都判成纯色。
  //    而分块时这一帧的像素只是整幅的一角，同样不能拿去和整幅比 —— 那种情况不敢说，置空。
  //
  // ⚠️ 判出来就**一直留着**（直到下一个关键帧）：纯色是持续状态，
  //    而静止画面上关键帧可能一分钟才来一个。下一个关键帧正常 ⇒ 这一句自动消失。
  if (header.isKeyframe) {
    blankFrameNote.value =
      tileCount === 1
        ? describeBlankFrame(
            base64ByteLength(frame.data),
            header.width * header.height,
            header.width,
            header.height,
          )
        : null
  }

  ensureVideoBuffer(header.codec, tileIndex).push(frame)
}

/**
 * 收一段音频。
 *
 * ⚠️ 与视频**相反的丢帧策略**：这里**不丢**（丢一段就是一个可听见的断点），
 *    宁可晚一点响。整条链只在"已经晚过一整帧"时才放弃那一段（在 `RemoteAudioPlayer` 里）。
 */
function onAudioFrame(frame: RemoteAudioFrame) {
  if (!frame?.header) return
  if (frame.header.sessionId !== activeSessionId.value) return

  // 时钟的观测**必须在判"要不要静音"之前**：静音是"这个浏览器不出声"，
  // 不是"这段数据不存在"。不观测的话，一静音就会把锚点饿死，取消静音之后要重新收敛。
  clock.observe(frame.header.capturedMonoMs)
  clockReady.value = clock.ready

  if (audioMuted.value) return

  // 已经确定建不起来了（非 https ⇒ 浏览器里没有 `AudioDecoder`）：
  // 再往里攒也永远不会被播出去，只是每段多一次数组搬运。
  // ⚠️ 把它放在 `clock.observe` **之后**：时钟该吃到的观测一段也不能少
  //    （它是画面那一路也在用的同一个时钟）。
  if (audioUi.value === 'unavailable') return

  if (audioPlayer?.isRunning) {
    audioPlayer.push(frame)
    return
  }

  // 还没建起来：先攒着（上限 1 秒），建好之后按顺序补进去。
  if (pendingAudio.length >= PENDING_AUDIO_LIMIT) pendingAudio.shift()

  pendingAudio.push(frame)

  void ensureAudioPlayer(frame)
}

/** 建立音频链（幂等；`start` 在飞的时候重复调用只会再判一次状态）。 */
let audioStarting = false

/**
 * **建链的世代号**：每次收链（`stopAudio`）都 +1。
 *
 * 🔥 这是本段里唯一一处"不处理也**不会报错**"的坑：`player.start()` 内部要
 *    `await addModule`，这期间用户完全可能切走、停止观看或按下静音。
 *    等它回来时 `audioPlayer` 已被清空 —— 于是这一步会**把上一路的播放器重新挂上**，
 *    而页面上看不出任何异常（声音真的在响），只是响的是那一路已经结束了的声音。
 *    ⚠️ 不要"顺手把 `audioStarting` 置回 false"来收场：那一刻新一路可能**正好也在建**，
 *    置回 false 会让它的守卫失效、凭空多建一个播放器。
 */
let audioGeneration = 0

async function ensureAudioPlayer(seed: RemoteAudioFrame) {
  if (audioPlayer?.isRunning || audioStarting) return
  if (audioUi.value === 'unavailable') return

  const generation = audioGeneration

  audioStarting = true
  audioUi.value = 'starting'
  audioMessage.value = '正在建立音频通道…'

  const player = new RemoteAudioPlayer(clock)
  const result = await player.start(seed.header.sampleRate, seed.header.channels)

  // 等回来时这一路已经不是当初那一路了：就地关掉，**不要**挂到字段上。
  if (generation !== audioGeneration) {
    player.stop()
    return
  }

  audioStarting = false

  if (!result.ok) {
    // 🔥 失败时**把原因原样留着**：这一句话是整个 M5.3 里唯一能告诉人
    //    "该去开 https"还是"该换浏览器"的东西，糊成一句"音频不可用"就白做了。
    audioUi.value = 'unavailable'
    audioMessage.value = result.reason
    pendingAudio = []
    return
  }

  audioPlayer = player

  // 补上建链期间攒下的那几段（按原来的顺序）。
  const queued = pendingAudio
  pendingAudio = []

  for (const item of queued) player.push(item)

  audioUi.value = queued.length > 0 || player.stats.scheduled > 0 ? 'playing' : 'starting'

  // ⚠️ 把状态推进到"playing"要靠**真的响过**（见下面的统计刷新），
  //    而不是靠"我们排进去了" —— 排进去但一直不出声是可能的（设备被独占 / 静音），
  //    那种情况页面上必须继续显示"在连"，否则用户会去查被控端。
}

/**
 * 收掉整条音频链的**机器部分**（播放器 / 待播队列 / "在连"标记 / 统计读数）。
 *
 * ⚠️ 刻意**不碰** `audioUi` / `audioMessage`：那是"要说什么话"，而两处调用者
 *    要说的话正好相反 —— 静音时说"已静音"就够，换会话时却必须把上一路的结论
 *    （尤其是那条"该去开 https"）清掉。混在一起的话，一次误点的静音
 *    就会把用户正需要的那句失败原因抹掉。
 */
function stopAudio() {
  audioGeneration += 1

  audioPlayer?.stop()
  audioPlayer = null

  pendingAudio = []
  audioStarting = false
  audioStats.value = null
  audioLatencyMs.value = null
}

/**
 * 静音开关：**只切本机**，不通知任何人。
 *
 * ⚠️ 只有在音频可用时按钮才可点（见模板的 `:disabled`）——
 *    这样上面那条"别把失败原因抹掉"的顾虑就根本不会发生：
 *    一个"不可用"的链没有东西可静音。
 */
function toggleAudio() {
  audioMuted.value = !audioMuted.value

  if (audioMuted.value) {
    // 🔥 必须走 `stopAudio`（它会 `audioGeneration += 1`）：直接清字段的话，
    //    一次正好在飞的 `start()` 回来还会把播放器挂上 —— 静音就白按了。
    stopAudio()
    audioUi.value = 'idle'
    audioMessage.value = ''
  }
}

/**
 * 页面上那一句"声音现在怎样"。
 *
 * 🔥 三个来源**按可信度顺序问**，顺序反了就会出现"读数和事实相反"：
 *    ① 本机音频链自己的结论（最可信：我到底出没出声）；
 *    ② 被控端上报的通道状态（"它那边接不接得上"）；
 *    ③ 旧客户端的 `null`（**不是**"坏了"，是"它压根没说"）。
 *    反过来的话，"被控端说 ready、而本机因为 http 建不起链"会显示成"正常" ——
 *    那正是唯一一种"越看越错"的显示方式。
 */
const audioStateLabel = computed(() => {
  if (audioMuted.value) return '已静音（仅本机）'

  if (audioUi.value === 'playing') return '正在播放'
  if (audioUi.value === 'starting') return '正在建立音频通道…'
  if (audioUi.value === 'unavailable') return '本机放不出声'

  // 本机还没开始建：那就看被控端那边接不接得上。
  const agentState = activeSession.value?.audioState ?? null

  if (agentState === AGENT_AUDIO_STATES.ready) return '等被控端发第一段…'
  if (agentState === AGENT_AUDIO_STATES.unavailable) return '被控端没有音频'

  if (!activeSessionId.value) return '—'

  return agentState === null ? '通道状态未上报' : '等待声音…'
})

/** 声音那格的颜色：不可用＝要人动手，正在播放＝一切都对。 */
const audioStateClass = computed(() => {
  if (audioUi.value === 'unavailable') return 'warn'
  if (audioUi.value === 'playing') return 'ok'
  return ''
})

/**
 * 音画偏差那一句话。
 *
 * ⚠️ **不许只显示一个正负数**：`+40 ms` 与 `-40 ms` 在处置上是两件事
 *    （一个要去查画面的解码/排期，一个要去查音频的播放缓冲），
 *    而"正"到底是画面超前还是声音超前，没人记得住。
 *    所以这里把方向写成词，只在数值上保留符号语义。
 */
const avOffsetText = computed(() => {
  const value = avOffsetMs.value

  if (value === null) return '—'
  if (Math.abs(value) < 0.5) return '0 ms'

  return value > 0 ? `画面超前 ${Math.round(value)} ms` : `声音超前 ${Math.round(-value)} ms`
})

/**
 * 刷新播放侧读数（每 250 ms 一次）。
 *
 * ⚠️ 刻意**不逐帧刷新**：这些数字全部要参与 Vue 的响应式 diff，
 *    而它们在 10–30 fps 下变化毫无意义。250 ms 足够人看清，也不占渲染预算。
 */
function refreshPlaybackStats() {
  clockReady.value = clock.ready

  const buffer = videoBuffer

  videoStats.value = buffer ? buffer.stats : null
  videoDecoderStats.value = buffer ? buffer.decoderStats : null

  const player = audioPlayer

  if (player) {
    const stats = player.stats

    audioStats.value = stats

    if (stats.audible && stats.scheduled > 0) audioUi.value = 'playing'

    const captureUnix = player.currentCaptureUnixMs()

    audioLatencyMs.value =
      captureUnix === null ? null : Math.max(0, Date.now() - captureUnix)
  } else {
    audioStats.value = null
    audioLatencyMs.value = null
  }

  // ── 音画偏差 ──
  const now = performance.now()

  const videoPlayhead = painted.atPerf > 0 ? { monoMs: painted.monoMs, atPerf: painted.atPerf } : null

  const audioPlayhead =
    audioStats.value && audioStats.value.playheadMonoMs !== null
      ? { monoMs: audioStats.value.playheadMonoMs, atPerf: audioStats.value.playheadAtPerf }
      : null

  avOffsetMs.value =
    videoPlayhead && audioPlayhead ? audioVideoOffsetMs(videoPlayhead, audioPlayhead, now) : null
}

/**
 * 更新"帧时延"。
 *
 * ⚠️ 它是 `本机此刻 - 被控端抓屏时刻`，**含两台机器的时钟差**。
 * 取不到合理的值时如实说"时钟未同步"，不硬报一个数 —— 一个错的时延
 * 会让人去查网络，而真正的问题是某台机器的时钟没校准。
 */
function updateLatency(capturedUnixMs: number) {
  if (!Number.isFinite(capturedUnixMs) || capturedUnixMs <= 0) {
    latencyMs.value = null
    latencySuspect.value = true
    return
  }

  const value = Date.now() - capturedUnixMs

  // 负数 = 那一帧"来自未来"；超过 10 秒 = 不可能是真的传输耗时。
  // 两种都只可能是时钟没对齐。
  if (value < -500 || value > 10000) {
    latencyMs.value = null
    latencySuspect.value = true
    return
  }

  latencyMs.value = Math.max(0, value)
  latencySuspect.value = false
}

/**
 * 取服务端的 https 情况（**匿名端点**，所以直接用 `fetch`，不经 `api` 那层）。
 *
 * 🔥 这一趟请求的意义全在"把失败说清楚"上：页面跑在非安全上下文里时，
 *    `AudioDecoder` / `AudioWorklet` / `VideoDecoder` **压根不存在**，
 *    而这是本方案里唯一一处"不报错的整块功能失效"。不问这一趟，
 *    页面上就只能显示一句"音频不可用"，用户完全不知道该做什么。
 */
async function loadTrustInfo() {
  try {
    const response = await fetch('/trust/info', { credentials: 'same-origin' })

    if (!response.ok) return

    trustInfo.value = (await response.json()) as TrustInfoDto
  } catch {
    // 拿不到就只是不显示横幅 —— 这不该影响观看本身。
  }
}

// ═══════════════════════════════════════════════════════════════
// M5.2 输入回传
// ═══════════════════════════════════════════════════════════════

/**
 * 两道闸门，**缺任何一个都点不动**，而且它们是两件不同的事：
 *
 *   1. `inputEnabled` —— **中心肯不肯转发**（一个开关；关着时中心根本不会发包）；
 *   2. `inputState`   —— **被控端接不接得住**（一个能力事实，只有它自己知道）。
 *
 * 🔥 页面上必须**分开显示**这两件事。合成一句"可以控制"的话，
 * 出问题时没人知道该去点开关还是去查客户端版本 —— 而这两条路完全不搭界。
 */
const inputEnabled = computed(() => activeSession.value?.inputEnabled === true)

const canControl = computed(
  () => inputEnabled.value && activeSession.value?.inputState === AGENT_INPUT_STATES.ready,
)

/** 画面有没有拿到键盘焦点（拿不到就不该发键盘事件 —— 见 onCanvasKeyDown）。 */
const canvasFocused = ref(false)

/** 最近一次被服务端拒掉的批次（给页面显示"为什么点了没反应"）。 */
const inputAck = ref<RemoteInputAckDto | null>(null)

/** 正在切换开关（期间禁用按钮，避免连点）。 */
const inputBusy = ref(false)

/** 本机已送出的输入条数 / 被本机合并掉的移动条数（**本地计数**，只用于排障）。 */
const inputSentEvents = ref(0)
const inputMergedLocally = ref(0)

/**
 * 待发队列。
 *
 * 🔥 **它不是响应式的（`let` 而不是 `ref`）**：它是每 16 毫秒被改写一次的热数据，
 * 放进 Vue 的响应式系统只会让它参与依赖收集与调度 —— 那是纯开销，
 * 而且页面上没有任何东西是根据"队列里有几条"渲染的。
 */
let pendingInput: RemoteInputEventDto[] = []

let flushTimer: number | null = null
let inFlight = 0
let flushQueued = false

/**
 * 同一时刻允许几批在路上。
 *
 * ⚠️ 取 1 是有意的取舍：一次往返还没回来就再发一批，会让"服务端拒了"这条回执
 * 失去因果（分不清是哪一批被拒）。代价是**上限 = 1 / RTT**
 * —— 局域网里 RTT 通常 1–5 毫秒，60 Hz 的合批完全够；
 * 真要跑到跨机房（50 ms）时，鼠标会变成 20 Hz 左右的更新，
 * 那是能感觉到的、但仍然是可用的。
 */
const MAX_IN_FLIGHT = 1

/** 合批窗口（毫秒）。与服务端中继的 16 毫秒对齐，约等于 60 Hz。 */
const FLUSH_INTERVAL_MS = 16

/**
 * 本机认为"还按着"的虚拟键码。
 *
 * 🔥 **它是必须的，不是冗余**：中心可能在"按下 Ctrl"与"抬起 Ctrl"之间断掉，
 * 而那个键会**在被控机上一直按着**（此后在那台机器上做什么都带着一个隐形的 Ctrl）。
 * 后端托盘有一份同样的安全网（会话收场时强制松开），这里是前端这一侧的那一份。
 */
const pressedKeys = new Set<number>()

/** 本机认为"还按着"的鼠标键。理由同上。 */
const pressedButtons = new Set<string>()

/** 上一次报输入失败的时间，用于**限流**（见 reportInputFailure）。 */
let lastFailureCode: string | null = null
let lastFailureAt = 0

/** 被控端输入通道状态的一句话（三态，**不许压成"可用/不可用"**）。 */
const inputStateText = computed(() => {
  const state = activeSession.value?.inputState

  switch (state) {
    case AGENT_INPUT_STATES.connecting:
      return '被控端通道：正在接入…'
    case AGENT_INPUT_STATES.ready:
      return '被控端通道：就绪'
    case AGENT_INPUT_STATES.unavailable:
      return '被控端通道：不可用'
    case null:
    case undefined:
      // ⚠️ 这一句与"不可用"是**两回事**：它表示那台机器的客户端还没提过这个字段
      //    （旧版），要做的动作是升级客户端，而不是去查为什么接不上。
      return '被控端通道：未知（客户端未上报）'
    default:
      return `被控端通道：${state}`
  }
})

const inputStateClass = computed(() => {
  switch (activeSession.value?.inputState) {
    case AGENT_INPUT_STATES.ready:
      return 'ok'
    case AGENT_INPUT_STATES.unavailable:
      return 'err'
    case AGENT_INPUT_STATES.connecting:
      return 'warn'
    default:
      return ''
  }
})

/** 造一条事件（把不用的字段留成后端契约里的默认值）。 */
function inputEvent(partial: {
  kind: string
  x?: number
  y?: number
  button?: string | null
  down?: boolean
  delta?: number
  virtualKey?: number
}): RemoteInputEventDto {
  return {
    kind: partial.kind,
    x: partial.x ?? 0,
    y: partial.y ?? 0,
    button: partial.button ?? null,
    down: partial.down ?? false,
    delta: partial.delta ?? 0,
    virtualKey: partial.virtualKey ?? 0,
  }
}

/**
 * 把浏览器里的一次鼠标位置换算成**归一化的 0–65535**。
 *
 * 🔥 **为什么用 `getBoundingClientRect` 而不是 canvas 的 `width`/`height`**：
 * 前者给的是**屏幕上真正画出来的那个矩形**，`fit`（被 CSS 缩小了）与
 * `actual`（1:1，还带滚动条）两种模式下它都自动是对的。
 * 用 canvas 的像素尺寸去算，`fit` 模式下就会**整体偏掉一个缩放比** ——
 * 而且这个偏差在 1:1 模式下完全不出现，是最容易漏测的一类。
 *
 * ⚠️ 还要**再算一次 letterbox**：`object-fit: contain` 会在元素框里居中留边，
 * 此时元素框比画面大，直接用元素框会让坐标原点对不上（表现为"鼠标整体偏一点"）。
 * 这段五行的换算就是 `contain` 的定义，两种情况都覆盖。
 */
function toNormalized(event: MouseEvent): { x: number; y: number } | null {
  const canvas = canvasRef.value

  if (!canvas || canvas.width <= 0 || canvas.height <= 0) return null

  const rect = canvas.getBoundingClientRect()

  if (rect.width <= 0 || rect.height <= 0) return null

  // contain：先按宽定，若算出高度超出框，改按高定。
  const naturalAspect = canvas.width / canvas.height
  const boxAspect = rect.width / rect.height

  let drawWidth = rect.width
  let drawHeight = rect.height

  if (naturalAspect > boxAspect) drawHeight = rect.width / naturalAspect
  else drawWidth = rect.height * naturalAspect

  if (drawWidth <= 0 || drawHeight <= 0) return null

  const left = rect.left + (rect.width - drawWidth) / 2
  const top = rect.top + (rect.height - drawHeight) / 2

  // 夹到 [0,1]：鼠标可能被拖到画面外（拖动时按住不放），
  // 不夹的话会送出一个越界坐标 —— 被控端那边一夹就变成"贴边"，看起来像卡住了。
  const rx = clamp01((event.clientX - left) / drawWidth)
  const ry = clamp01((event.clientY - top) / drawHeight)

  return { x: Math.round(rx * 65535), y: Math.round(ry * 65535) }
}

function clamp01(value: number): number {
  if (!Number.isFinite(value)) return 0
  return value < 0 ? 0 : value > 1 ? 1 : value
}

/** 浏览器 `MouseEvent.button` → 线上取值。 */
function mouseButtonOf(button: number): string | null {
  switch (button) {
    case 0:
      return REMOTE_MOUSE_BUTTONS.left
    case 1:
      return REMOTE_MOUSE_BUTTONS.middle
    case 2:
      return REMOTE_MOUSE_BUTTONS.right
    default:
      // 侧键（3/4）暂不支持：支持它要处理"被控端的鼠标有没有侧键"，
      // 而送过去一个它不认识的键，表现是"点了没反应"，比不支持更难查。
      return null
  }
}

/**
 * 入队。**合批与合并都发生在这里。**
 *
 * 🔥 合并规则与服务端中继**完全一致**（相邻两条移动只留后一条）：
 * 两边各有一套缓存，如果规则不同，就会出现"前端合并掉的位置恰好是服务端也想用的那条"
 * 这类只在两家都改过之后才暴露的问题。
 * 保持一致还有个好处：任何一条离散事件（按下 / 抬起 / 滚轮）都会**截断**移动的合并链，
 * 于是"按下 → 移动 → 抬起"的顺序天然保序。
 */
function enqueue(event: RemoteInputEventDto) {
  // ⚠️ 这里判的是**中心闸门**（`inputEnabled`）而不是 `canControl`：
  //    "抬起"这类收尾事件必须在闸门还开着的时候照样能发出去 ——
  //    否则控制器一关，被控端的那个键就永远卡住了。
  if (!inputEnabled.value) return

  const last = pendingInput[pendingInput.length - 1]

  if (event.kind === REMOTE_INPUT_KINDS.pointerMove && last?.kind === REMOTE_INPUT_KINDS.pointerMove) {
    // 人眼只关心"最后停在哪"，中间的移动点没有任何价值。
    pendingInput[pendingInput.length - 1] = event
    inputMergedLocally.value += 1
    scheduleFlush()
    return
  }

  if (pendingInput.length >= REMOTE_INPUT_MAX_EVENTS) {
    // 满了。移动可以丢（它天然可丢）；离散事件**绝不能丢** ——
    // 丢一条"抬起"，那个键就在被控机上永远按着。所以先把手里的发出去腾位置。
    if (event.kind === REMOTE_INPUT_KINDS.pointerMove) return

    flushInput()
  }

  pendingInput.push(event)
  scheduleFlush()
}

function scheduleFlush() {
  if (flushTimer !== null) return

  flushTimer = window.setTimeout(() => {
    flushTimer = null
    flushInput()
  }, FLUSH_INTERVAL_MS)
}

/**
 * 把待发队列送出去。
 *
 * ⚠️ **用 `Invoke`（要回执）而不是 `Send`**：输入是唯一一类"发了没反应就等于没发"的动作
 * —— 画面卡住时人能看见，而按下一个键之后什么都没发生，到底是没送到、被拒了、
 * 还是那个键在被控机上本来就没用，没有回执完全分不出来。
 */
function flushInput() {
  if (flushTimer !== null) {
    window.clearTimeout(flushTimer)
    flushTimer = null
  }

  if (pendingInput.length === 0) return

  // 上一批还没回来：记一笔，等它回来再发（**不并发**，见 MAX_IN_FLIGHT）。
  if (inFlight >= MAX_IN_FLIGHT) {
    flushQueued = true
    return
  }

  const sessionId = activeSessionId.value
  const connection = hub

  if (!sessionId || !connection || !inputEnabled.value) {
    // 会话没了或闸门关了：手里的东西已经没有去处，**静默清掉**而不是攒着 ——
    // 攒着的话下次开一路会话会把上一路的旧坐标一起发过去，表现是"一点就乱跳"。
    pendingInput = []
    return
  }

  const events = pendingInput
  pendingInput = []
  inFlight += 1

  // ⚠️ 只送 `{ sessionId, events }`：后端那个 DTO 就是这两个字段，
  //    而会话 Id 在这条链路上是**必填**的（观看端连接的归属校验要用它）。
  void connection
    .invoke<RemoteInputAckDto>(REMOTE_HUB_CLIENT_METHODS.sendInput, { sessionId, events })
    .then((ack) => {
      inputSentEvents.value += events.length

      if (ack && ack.ok === false) reportInputFailure(ack)
    })
    .catch(() => {
      // 连接断了。**不在这里刷错误**：画面那边会立刻显示出"实时通道未连接"，
      // 而输入失败每 16 毫秒就会来一次，在这里刷等于把那条更重要的提示淹掉。
    })
    .finally(() => {
      inFlight -= 1

      if (flushQueued) {
        flushQueued = false
        flushInput()
      }
    })
}

/**
 * 把一次输入失败变成一句人话。
 *
 * 🔥 **必须限流**：鼠标一直在动的时候，失败是每 16 毫秒来一次的 ——
 * 不去重的话通知栏会被同一条消息刷爆，而真正要看的那句话反而看不见。
 */
function reportInputFailure(ack: RemoteInputAckDto) {
  const code = ack.code ?? 'unknown'
  const now = Date.now()

  if (code === lastFailureCode && now - lastFailureAt < 5000) return

  lastFailureCode = code
  lastFailureAt = now
  inputAck.value = ack

  emit('notice', 'err', describeInputFailure(ack))
}

function describeInputFailure(ack: RemoteInputAckDto): string {
  const detail = ack.message ? `（${ack.message}）` : ''

  switch (ack.code) {
    case REMOTE_ERROR_CODES.inputNotEnabled:
      return `输入没有打开：中心这一路的「允许控制」开关是关着的，中心根本没有转发。${detail}`
    case REMOTE_ERROR_CODES.inputUnavailable:
      return `输入送不进去：被控端接不住。${detail}`
    case REMOTE_ERROR_CODES.inputTooLarge:
      return `这一批输入事件太多（上限 ${REMOTE_INPUT_MAX_EVENTS} 条），整批被拒。${detail}`
    // ⚠️ 这两支曾经共用一个码，于是"会话没了"被说成"你没在看这一路" ——
    //    而它们的处置完全不搭界（一个去刷新列表，一个去重新打开这一路）。
    case REMOTE_ERROR_CODES.notWatching:
      return `这个连接现在没有在观看这一路（可能已经刷新或切走），重新打开这一路再操作。${detail}`
    case REMOTE_ERROR_CODES.sessionNotFound:
      return `这一路会话已经不在了（可能刚被结束）。刷新列表看看现在还有什么。${detail}`
    default:
      return `输入被拒${detail}`
  }
}

// ── 鼠标 ──

/** 把"此刻鼠标在画面的哪个位置"入队（移动、按下、抬起都要用它）。 */
function moveTo(event: MouseEvent) {
  const point = toNormalized(event)

  if (!point) return

  enqueue(inputEvent({ kind: REMOTE_INPUT_KINDS.pointerMove, x: point.x, y: point.y }))
}

function onPointerMove(event: MouseEvent) {
  if (!canControl.value) return

  moveTo(event)
}

function onPointerDown(event: MouseEvent) {
  if (!canControl.value) return

  // 🔥 **先补一条移动，再发按下**。
  //
  //    `mousedown` 可能发生在"上一次移动之后"（人停下来再点），甚至**完全没有过移动**
  //    （鼠标刚移进画面就直接点）。缺这一条，被控端就会在**上一次的位置**按下 ——
  //    表现是"点错了地方"，而它看起来像是坐标换算错了。
  moveTo(event)

  const button = mouseButtonOf(event.button)

  if (!button) return

  // 防止拖拽时选中文字 / 触发浏览器自己的拖放。
  event.preventDefault()

  pressedButtons.add(button)

  enqueue(inputEvent({ kind: REMOTE_INPUT_KINDS.pointerButton, button, down: true }))
}

function onPointerUp(event: MouseEvent) {
  const button = mouseButtonOf(event.button)

  if (!button) return

  // ⚠️ 没按下过就不补一条"抬起"：中心那边收到一条孤立的"抬起"是无害的，
  //    但它会让"按下/抬起配对"这个判断变得没有意义（后面要看它来判卡键）。
  if (!pressedButtons.has(button)) return

  pressedButtons.delete(button)

  moveTo(event)

  enqueue(inputEvent({ kind: REMOTE_INPUT_KINDS.pointerButton, button, down: false }))
}

/**
 * 鼠标离开画面。
 *
 * 🔥 这里**必须把按着的键松开**：拖拽时把鼠标拖出画面（很常见）之后，
 * `mouseup` 会落在别的地方 —— 我们收不到它。不收尾的话，
 * 被控端那边那个键就**永远按着**了。
 *
 * ⚠️ 代价是"把滚动条拖到画面外"这种操作会提前结束 ——
 * 那是能接受的代价，"被控机的左键一直按着"不是。
 */
function onPointerLeave() {
  releaseAllButtons()
}

/**
 * 右键。
 *
 * 🔥 只**挡掉浏览器自己的菜单**，**不透传任何事件**：一次右击会先后触发
 * `mousedown(button=2)` 与 `contextmenu`，两个都转发就会发出两条"右键按下" ——
 * 表现是被控端弹两次菜单（或菜单闪一下就没）。
 * 真正的右键按下/抬起由 `onPointerDown` / `onPointerUp` 负责。
 */
function onContextMenu(event: MouseEvent) {
  event.preventDefault()
}

function onWheel(event: WheelEvent) {
  if (!canControl.value) return

  const steps = wheelSteps(event)

  if (steps === 0) return

  enqueue(inputEvent({ kind: REMOTE_INPUT_KINDS.wheel, delta: steps }))
}

/**
 * 浏览器的滚轮增量 → 被控端能懂的"格数"（**正值 = 向上**，与 Windows 一致）。
 *
 * ⚠️ 直接透传 `deltaY` 是不行的：Chrome 一格滚轮报 ~100，某些触控板一次报 3，
 * 而 `deltaMode` 还可能是"行"或"页" —— 不折算的话，被控端那边
 * 要么"滚半天不动"（3 被当成 3 格但方向/大小都不对），
 * 要么"一滚到底"（100 被当成 100 格）。
 */
function wheelSteps(event: WheelEvent): number {
  const raw = event.deltaY

  if (!Number.isFinite(raw) || raw === 0) return 0

  // 三种 deltaMode（0=像素 / 1=行 / 2=页）都折算成"格"。
  const notches = event.deltaMode === 1 ? raw : event.deltaMode === 2 ? raw * 3 : raw / 100

  const whole = Math.trunc(notches)

  // ⚠️ 先取方向、再取大小：不这样做，触控板那种"一格 3px"的惯性滚动
  //    会被取整抹成 0，表现是"滚了半天没反应"。
  if (whole === 0) return raw < 0 ? 1 : -1

  // 上限 3 格：一次真滚轮也就 1 格，能算出 5 格一定是某个 deltaMode 没折算对。
  return Math.max(-3, Math.min(3, -whole))
}

// ── 键盘 ──

/**
 * 按下。
 *
 * 🔥 **用 `event.keyCode` 是刻意的**：它就是 Windows 虚拟键码（字母 0x41–0x5A、
 * 功能键 0x70–0x7B、修饰键 0x10/0x11/0x12…），与被控端 `SendInput` 要的完全一致。
 * 改用 `event.code`（物理位置）就得维护一张几十条的映射表，而那张表**没有测试能保证它对** ——
 * 错一个键的表现是"按 A 出来 B"，比不用它更糟。
 * 代价：`keyCode` 是废弃 API（但所有主流浏览器仍然实现它，且不会移除 —— 大量老站点依赖）。
 */
function onCanvasKeyDown(event: KeyboardEvent) {
  if (!canControl.value) return

  const virtualKey = event.keyCode

  if (virtualKey <= 0 || virtualKey > 0xfe) return

  // 🔥 **必须 preventDefault**：不拦的话，在被控机上按 Ctrl+W / Alt+← / F5
  //    会**关掉/刷新观看者自己的这个页面** —— 用户的意图明明是在被控机上开个窗口。
  //    代价：本页的浏览器快捷键在画面有焦点时全部失效。
  //    （点击画面外会让它失焦，见 onCanvasBlur。）
  event.preventDefault()

  // ⚠️ 长按的自动重复由**被控机**自己产生（它收到的是"按住"这个状态）。
  //    前端再补一串重复的 down，只会让那边的重复速率翻倍。
  if (event.repeat) return

  if (pressedKeys.has(virtualKey)) return

  pressedKeys.add(virtualKey)

  enqueue(inputEvent({ kind: REMOTE_INPUT_KINDS.key, virtualKey, down: true }))
}

function onCanvasKeyUp(event: KeyboardEvent) {
  const virtualKey = event.keyCode

  if (!pressedKeys.has(virtualKey)) return

  pressedKeys.delete(virtualKey)

  event.preventDefault()

  enqueue(inputEvent({ kind: REMOTE_INPUT_KINDS.key, virtualKey, down: false }))
}

function onCanvasFocus() {
  canvasFocused.value = true
}

/**
 * 失去焦点。
 *
 * 🔥 **这里必须松开所有键**：焦点转到别处之后，`keyup` 不会再送到画面上 ——
 * 不收尾的话，被控机那边会一直按着刚才那个键（此后做什么都带着一个隐形的修饰键）。
 */
function onCanvasBlur() {
  canvasFocused.value = false

  releaseAllKeys()
  releaseAllButtons()
}

// ── 收尾（安全网） ──

/**
 * 松开**本机认为还按着的每一个键与鼠标键**。
 *
 * 🔥 这不是可选项。中心可能在"按下 Ctrl"与"抬起 Ctrl"之间断掉，
 * 而那个键会**在被控机上一直按着** —— 而且这个状态**在被控机的画面上看不出来**，
 * 要等到用户在那台机器上打字打得莫名其妙才会发现。
 *
 * 后端托盘有一份同样的安全网（会话收场时强制松开），这里是前端这一侧的那一份。
 * 两边都做不是重复：任何一边单独失效，都还有另一边兜着。
 */
function releaseAllInput() {
  releaseAllKeys()
  releaseAllButtons()
}

function releaseAllKeys() {
  if (pressedKeys.size === 0) return

  const keys = Array.from(pressedKeys)
  pressedKeys.clear()

  for (const virtualKey of keys) {
    enqueue(inputEvent({ kind: REMOTE_INPUT_KINDS.key, virtualKey, down: false }))
  }

  // ⚠️ 立刻送出去，不等那 16 毫秒的合批窗口：这些事件是**收尾**，
  //    而"什么时候收尾"往往就是"马上就要断开了"的那个时刻。
  flushInput()
}

function releaseAllButtons() {
  if (pressedButtons.size === 0) return

  const buttons = Array.from(pressedButtons)
  pressedButtons.clear()

  for (const button of buttons) {
    enqueue(inputEvent({ kind: REMOTE_INPUT_KINDS.pointerButton, button, down: false }))
  }

  flushInput()
}

// ── 开关（中心侧那道闸门） ──

/**
 * 打开 / 关闭这一路的输入回传。
 *
 * 🔥 **关掉之前先松开所有按着的键**，顺序不能反：
 * 先关闸门的话，那几条"抬起"会被中心**直接拒掉**（`input-not-enabled`），
 * 而那个键就留在被控机上了 —— 一个由"关闭控制"这个动作本身造成的卡键。
 */
async function toggleInput() {
  const sessionId = activeSessionId.value
  const connection = hub

  if (!sessionId || !connection) return

  const next = !inputEnabled.value

  inputBusy.value = true

  try {
    if (!next) releaseAllInput()

    // 🔥 走 **Hub** 而不是 HTTP：闸门是中心账本上的一位，
    //    但要紧的是这条动作必须发生在**我正在看这一路的那条连接**上 ——
    //    归属校验（服务端 RemoteWatchScope）就是拿这条连接上记的"正在看哪一路"来判的。
    //    HTTP 端点没有"你正在看"这个概念，判不了这一条。
    //    变更结果由服务端**广播**给所有看这一路的人（见 RemoteSessionRegistry）。
    const ack = await connection.invoke<RemoteInputAckDto>(
      REMOTE_HUB_CLIENT_METHODS.setInputEnabled,
      sessionId,
      next,
    )

    // ⚠️ 被拒有三种（会话没了 / 你不在看这一路 / 请求不合法），
    //    而它们该做的事完全不搭界 —— 所以必须把码翻成人话，而不是笼统一句"失败"。
    if (!ack?.ok) {
      emit('notice', 'err', describeInputFailure(ack))
      return
    }

    // 🔥 本地**不**改状态：闸门是**会话级**的，服务端的 SessionChanged 广播会把它
    //    同步给所有正在看这一路的人（也包括自己）。本地再写一次就成了第二个事实来源，
    //    而它迟早会与广播不一致 —— 比如别人又把它关掉了，而这一页还写着"已开"。

    if (!next) {
      emit('notice', 'warn', '已关闭控制：中心不再转发任何键鼠。')
      return
    }

    // ⚠️ 这里**不能**说"可以控制了"：打开的只是中心那道闸门。
    //    被控端接不接得住是另一件事，而它可能此刻就是"不可用"。
    //
    // ⚠️ 被控端状态读的是**当前已知的最新值**，不是这次调用的返回值 ——
    //    那个状态由被控端上报、经广播落到列表上，与"谁翻了闸门"无关。
    //    从返回值里取它，反而会把"闸门"与"通道"这两件事重新搅到一起。
    const state = activeSession.value?.inputState
    const reason = activeSession.value?.inputReason

    const agentNote =
      state === AGENT_INPUT_STATES.ready
        ? '被控端通道已就绪，可以开始操作。'
        : state === AGENT_INPUT_STATES.unavailable
          ? `但被控端接不住输入${reason ? `：${reason}` : ''}`
          : '但被控端的输入通道还在接入中，等一下再试。'

    emit('notice', state === AGENT_INPUT_STATES.ready ? 'ok' : 'warn', `已打开控制 —— ${agentNote}`)
  } catch (err) {
    emit('notice', 'err', err instanceof Error ? err.message : '切换输入开关失败。')
  } finally {
    inputBusy.value = false
  }
}

// ═══════════════════════════════════════════════════════════════
// 动作
// ═══════════════════════════════════════════════════════════════

/**
 * 建一路会话并开始观看。
 *
 * @param options.silent   自动重连用：**不弹**那些"正在请求"的提示（每 16 秒弹一次
 *                         会把页面上其它通知全冲掉），失败原因改写在窗口的重连条上。
 * @param options.openWindow 连上之后打开远控窗口。
 */
async function startSession(options: { silent?: boolean; openWindow?: boolean } = {}) {
  if (targetAgentId.value === null) {
    emit('notice', 'warn', '先选一台机器。')
    return
  }

  const agentId = targetAgentId.value

  starting.value = true
  viewerError.value = ''

  try {
    // ── M7.7：先收掉正在看的那一路（多屏切换就从这里走）──
    //
    // 🔥 **切屏必须重建会话**：`screenId` 是**建会话那一刻**下发给被控端的参数，
    //    已经在推的流不会因为页面改了个下拉框就换屏 —— 被控端还在抓原来那块屏，
    //    而它压根不知道页面已经改主意了。
    //    而 M5.1 只允许一路 ⇒ 不停旧的直接建新的，得到的是 409（页面会显示"那台机器正忙"），
    //    看起来像"切换功能坏了"。
    //
    // ⚠️ 顺序：先退 Hub 组（`teardownViewer`），再让中心停会话。
    //    反过来会有一小段"中心已经停了、页面还在收"的空窗 —— 那些帧会被当成异常全丢掉，
    //    而它们和"真的断流"长得一模一样。
    const previous = activeSession.value

    if (previous) {
      await teardownViewer()

      try {
        await api.stopRemoteSession(previous.sessionId)
      } catch {
        // 404 = 它已经自己结束了；中心刚重启过也会这样。两种都不影响下面起新的。
      }

      await refreshSessions()
    }

    const session = await api.createRemoteSession({
      agentId: targetAgentId.value,
      displayMode: displayMode.value,
      maxFps: maxFps.value,

      // 质量档（M7.16）。⚠️ 不带它 ⇒ 每一路新会话都从 `auto` 起步，
      // 于是"选了流畅/60 帧"要连上之后再点一次，而"重建"会把档位丢掉。
      quality: chosenQuality.value,

      // 抓哪块屏（M5.5 多屏）。`null` = 让被控端自己挑（多屏时取虚拟桌面拼接）。
      // ⚠️ 它必须是**抓屏器认的 Id**；清单里读到的原样传，不要自己拼。
      screenId: selectedScreenId.value,

      // 🔥 必须把"本页显示得了哪些格式"带上去（M5.4-b 第 7、8 步）：
      //    它是编码格式协商三项输入里的第二项，而中心看不到浏览器。
      //    ⚠️ 不带的话语义是"**没声明**" ⇒ 中心按带内 H.264 回落；
      //       而如果本页其实渲染不了 H.264，那就是一块黑屏（两边日志还全正常）。
      //       所以这一行不是可选的装饰，是"画面出不出得来"的一部分。
      //
      // ⚠️ 这里**必须等**探测：它是异步的（`isConfigSupported` 返回 Promise）。
      //    拿一个"探测中"的占位值发出去，等于把这台机器**永久**钉在 MJPEG 上 ——
      //    直到用户手动刷新页面。而这一等通常只有几毫秒。
      viewerVideo: (await ensureViewerProbe()).capabilities,
    })

    // 🔥 状态此刻**一定**是 requested：中心只知道"指令发出去了"。
    //    页面上看到的"正在连接"与"已连接"的区别，全靠服务端随后的状态上报。
    if (!options.silent) {
      emit(
        'notice',
        'ok',
        `已向「${agentName(session.agentId)}」发出开始请求。` +
          '画面出来之前会停在「已请求」—— 这一步可能要几秒，也可能因为那台机器没人登录而失败。',
      )
    }

    // 🔥 记下"这一路是怎么开的"（重连时原样恢复，见 lastConnectParams）。
    lastConnectParams.value = {
      agentId: session.agentId,
      screenId: session.screenId ?? selectedScreenId.value,
      maxFps: maxFps.value,
      displayMode: displayMode.value,
      quality: chosenQuality.value,
    }

    await refreshSessions()
    await watch(session.sessionId)

    if (options.openWindow && activeSessionId.value) {
      windowOpen.value = true

      // 窗口归属跟着"这一路是谁"走（断线后会话没了也要留着，见 `windowAgentId`）。
      windowAgentId.value = session.agentId
      reconnect.value = { state: 'idle', attempt: 0, reason: '', secsLeft: 0 }
    }
  } catch (err) {
    const text = describeStartFailure(err)

    if (options.silent) {
      // 静默重连失败的**唯一**去处是那条重连提示（它知道是第几次、还剩几次）。
      reconnect.value = { ...reconnect.value, reason: text }
    } else {
      emit('notice', 'err', text)
    }
  } finally {
    starting.value = false
  }
}

/** 把建会话的失败翻译成人话 —— 状态码/错误码不同，下一步动作完全不同。 */
function describeStartFailure(err: unknown): string {
  if (!(err instanceof ApiError)) return '建立远程桌面会话失败。'

  const code = (err.body as { code?: unknown } | null)?.code

  switch (code) {
    case REMOTE_ERROR_CODES.agentNotFound:
      return err.message + '（这台机器不在在线表里）'
    case REMOTE_ERROR_CODES.agentOffline:
      return err.message + '（机器在，但长连接不在了）'
    case REMOTE_ERROR_CODES.sessionBusy:
      return err.message + '（先结束上面那一路）'
    case REMOTE_ERROR_CODES.sessionSideUnavailable:
      return err.message + '（被控端需要有人登录才行）'
    default:
      return err.message
  }
}

/** 开始看某一路（建会话之后，或从列表里点一条已有的）。 */
async function watch(sessionId: string) {
  // ⚠️ **切换之前**先松开上一路按着的键：`enqueue` 用的是"当前会话"，
  //    等 Id 换掉之后再发"抬起"，它们就会被送到新那一路 ——
  //    于是上一台机器上那个键永远留着了。
  releaseAllInput()

  await connectHub()

  if (!hub) return

  viewerError.value = ''

  try {
    const ack = await hub.invoke<RemoteWatchAckDto>('Join', sessionId)

    if (!ack?.ok) {
      viewerError.value = ack?.message ?? '加入这路会话失败。'
      return
    }

    // 🔥 先清本地计数再切 Id：反过来会让 `onFrame` 里的"会话 Id 对不上"守卫
    //    在切换的那一瞬把新会话的头几帧丢掉。
    resetFrameState()
    activeSessionId.value = sessionId
    inputAck.value = null
    lastFailureCode = null

    // ⚠️ **换路即清剪贴板**（载荷里没有 sessionId，上一路迟到的推送认不出来）：
    //    不清的表现是"打开 B 机器，剪贴板格里却是 A 机器刚才复制的东西" ——
    //    而剪贴板是会被当成"要不要粘贴到这台机器"的依据，错的比空的坏得多。
    clipboard.value = null
    clipboardDraft.value = ''

    // 再回填这一路**中心缓存**里的内容（不触发被控机动作）。
    void loadClipboard()

    if (ack.session) {
      const index = sessions.value.findIndex((s) => s.sessionId === sessionId)
      if (index >= 0) sessions.value[index] = ack.session
    }
  } catch (err) {
    viewerError.value = err instanceof Error ? err.message : '加入这路会话失败。'
  }
}

async function stopWatching() {
  await teardownViewer()
}

/** 只退出观看，不动会话（切到别的一路时用）。 */
async function teardownViewer() {
  const sessionId = activeSessionId.value
  const connection = hub

  // 🔴 全屏必须跟着一起收（M7.9）：下面会把画布清成 0×0，
  //    留在这个全屏里就是"整块屏幕一片黑，而且退不出去"（页面上的按钮已随会话消失）。
  //    ⚠️ 不等它 —— 调用方在切会话的关键路径上，而退出全屏要几十毫秒。
  leaveFullscreen()

  // ⚠️ **顺序**：先把收尾事件送出去（这时会话 Id 还在），再清 Id。
  //    反过来的话那些"抬起"就没有去处了，而它们的唯一作用就是让被控机松手。
  releaseAllInput()

  // 🔥 视口也要**在清 Id 之前**关掉（M5.5）：不关的话被控端会一直只编那一小块，
  //    下一个看这一路的人打开就只看到"画面的一角"，而且他不知道是上一任观众留下的视角。
  resetViewport()

  activeSessionId.value = null
  resetFrameState()

  // M7.16：预算回到上限。不留着上一路的 33 ms —— 下一路可能是 12 fps，
  // 那时 33 ms 还不到半帧，画面会一顿一顿，而"看起来像网络问题"。
  resetPlaybackLatency()
  playbackLatencyMs.value = getPlaybackLatencyMs()

  // 这里才是"不再看了"：把 rAF 循环整个停掉并把缓冲丢弃。
  // 只 `clear()` 的话，那个循环会在整页停留期间一直空转（每次 rAF 判一次队列为空）。
  stopAllVideoBuffers()

  inputAck.value = null
  canvasFocused.value = false

  // 剪贴板也一起清：停止观看之后还留着上一台的剪贴板内容，
  // 下一个会话上来就会看到"上一次别人的东西"（而且它会被当成真要粘贴的内容）。
  clipboard.value = null
  clipboardDraft.value = ''

  if (connection && sessionId) {
    try {
      await connection.invoke('Leave', sessionId)
    } catch {
      /* 连接可能已经断了 —— 分组会随连接一起消失，不必强求 */
    }
  }
}

// ═══════════════════════════════════════════════════════════════
// 打开 / 关闭远控窗口（M7.13）
// ═══════════════════════════════════════════════════════════════

/**
 * 上一次"连上这台机器"用的参数。
 *
 * 🔥 <b>重连必须靠它，不能现读下拉框</b>：断线之后那几个下拉框可能已经被用户改过，
 * 也可能因为 `targetAgentId` 被清空而全是默认值 —— 那样"自动重连"会悄悄连到
 * 另一块屏、甚至另一台机器上。重连的语义是"恢复刚才那一幕"，不是"按现在的表单再开一次"。
 */
const lastConnectParams = ref<{
  agentId: number
  screenId: string | null
  maxFps: number | null
  displayMode: string
  /**
   * 质量档（M7.16）。
   *
   * 🔥 少了它，重连会把「流畅（60 帧）」退回 `auto`（12 帧）——
   * 而重连的**唯一**用途就是"把刚才那一幕恢复回来"。
   */
  quality: string
} | null>(null)

/**
 * 从一路**已有会话**里反推"它是怎么开的"（M7.13 修正）。
 *
 * <p>
 * 🔥 <b>「接着看」这条路以前压根不记参数</b>：只有 `startSession`（自己建的那一路）
 * 才写 `lastConnectParams`。于是"别人开的那一路我点进去看"这种最常见的情况，
 * 断线后自动重连**必然**死在「没有记住上一次的连接参数」上 ——
 * 而那句话会让现场以为"这个浏览器没记性"，其实只是这条路没接上。
 * </p>
 *
 * <p>
 * ⚠️ 会话上没有原始的 `maxFps`（只有 `targetFps` = 档位与请求取小之后的<b>生效值</b>）。
 * 拿它当上限重连是<b>安全的</b>：只会不高于原来的帧率，不会突然给被控端加负载。
 * </p>
 */
function rememberParamsFromSession(session: RemoteSessionDto) {
  lastConnectParams.value = {
    agentId: session.agentId,
    screenId: session.screenId ?? null,
    maxFps: session.targetFps ?? null,
    displayMode: session.displayMode,
    quality: session.quality ?? REMOTE_QUALITY_PROFILES.auto,
  }
}

/** 这台机器有没有一路**还能看**的会话（`ended` / `failed` 的不算）。 */
function sessionToResume(agentId: number): RemoteSessionDto | null {
  const session = sessionOf(agentId)

  if (!session) return null
  if (session.state === REMOTE_SESSION_STATES.stopped) return null
  if (session.state === REMOTE_SESSION_STATES.failed) return null

  return session
}

/**
 * 列表里点「连接」。
 *
 * 🔥 <b>先看这台机器有没有现成的会话</b>：有就直接入组（`Join`），不再建一路 ——
 * 一台机器同时只允许一路，再建只会得到 409「那台机器正忙」，
 * 而那个提示会让人以为"上一路卡住了"，其实它好好的、只是正在被看。
 */
async function connectTo(agent: AgentHostDto) {
  if (starting.value) return

  const changed = targetAgentId.value !== agent.id

  targetAgentId.value = agent.id

  // 换机器 ⇒ 上一台的自动重连立刻作废（否则它会在几秒后把画面切回去）。
  cancelReconnect()

  // 🔥 **换了一台机器就必须重读它的屏清单与编码能力**（M7.13）：
  //    面板上的"抓哪块屏"是从 `screens` 渲染的，不重读就会把**上一台机器**的显示器
  //    列在这一台名下 —— 那个错误的表现是"这台机器只有一块屏"，很难往这边想。
  if (changed) onTargetChanged()

  const existing = sessionToResume(agent.id)

  if (existing) {
    // 🔥 **「接着看」也必须记住参数**（M7.13 修正）：否则从这一路进去的观众
    //    一旦断线，`doReconnect` 会因为没有参数而直接放弃 —— 现场看到的是
    //    「自动重连已放弃 —— 没有记住上一次的连接参数，请手动点「连接」」，
    //    而那条提示是个**死胡同**（它让你点「连接」，可你已经点了）。
    rememberParamsFromSession(existing)

    // 面板跟着这一路的实际参数走：不跟的话，「抓哪块屏」显示"自动"而实际正在看某块屏，
    // 用户点「按当前设置重建会话」就会把它悄悄改回自动（多屏机器上直接换了一块屏）。
    selectedScreenId.value = existing.screenId ?? null
    displayMode.value = existing.displayMode

    // ⚠️ 档位同理（M7.16）：不跟的话，进来看到「流畅」高亮、而"重建"会按 `auto` 建，
    //    结果是"点一下重建，60 帧变 12 帧"。
    chosenQuality.value = existing.quality ?? REMOTE_QUALITY_PROFILES.auto

    // ⚠️ 帧率也同理：会话上只有生效值（`targetFps`），拿它当请求值重发是安全的
    //    （它就是上一次真正下发的那个数）。但**只在下拉框里真有这个选项时**才写 ——
    //    写一个不在选项里的数（如 17），`<select>` 会一个都不高亮，看着像坏了。
    const effectiveFps = existing.targetFps ?? null
    maxFps.value =
      effectiveFps !== null && (FPS_CHOICES as readonly number[]).includes(effectiveFps)
        ? effectiveFps
        : null

    windowAgentId.value = agent.id
    await openWindow(existing.sessionId)

    return
  }

  windowAgentId.value = agent.id
  await startSession({ openWindow: true })
}

/** 入组并打开窗口。 */
async function openWindow(sessionId: string) {
  await watch(sessionId)

  if (!activeSessionId.value) return

  // 会话里能读到是谁的（调用方没预设时兜底）。
  if (windowAgentId.value === null) {
    windowAgentId.value = sessions.value.find((s) => s.sessionId === sessionId)?.agentId ?? null
  }

  windowOpen.value = true
  reconnect.value = { state: 'idle', attempt: 0, reason: '', secsLeft: 0 }

  // 🔥 屏清单可能在**窗口打开那一刻还没回来**（那台机器正忙着推流，问它要清单要等一轮）。
  //    不补这一次的话，「抓哪块屏」就是一个**空下拉框**外加一句"没有可抓的屏"，
  //    而它在画面正以 2160×1440 推流的时候出现 —— 那句话与事实正好相反。
  //    重读按钮在参数面板里，用户不一定会想到去点它。
  if (screens.value.length === 0) refreshScreens()
}

/**
 * 「挂起」（M7.14，原来叫「✕」）—— **只收窗口，会话留着**。
 *
 * <p>
 * 🔥 与「断开」刻意分开：这一路可能**还有别人在看**（远控是多人同看同一路的），
 * 关个窗口就把别人的画面停掉是不对的。要真停，用「断开」或者列表里的「结束会话」。
 * </p>
 *
 * <p>
 * 🔥 <b>为什么改名叫「挂起」并挪到最左边</b>：它原先是一个 `✕` 挂在最右侧，
 * 而 `✕` 的通用语义是"关掉/销毁"—— 现场点了会以为会话被结束了，于是不敢点；
 * 而它其实是**最安全**的那一个（什么都不销毁）。挂起 = "先放一边，稍后再进"，
 * 配上列表里的「结束会话」，"收起来"与"真结束"才各有一个说得出口的名字。
 * </p>
 */
function suspendWindow() {
  leaveFullscreen()
  windowOpen.value = false
  windowAgentId.value = null
}

/**
 * 断开 = **结束这一路会话**（影响所有看它的人）+ 关窗。
 *
 * ⚠️ 必须先置 `stoppingIntentionally`：否则会话结束时那条"自动重连"会立刻启动，
 * 表现为"点了断开，两秒后又自己连上了"。
 */
async function disconnectAndClose() {
  const sessionId = activeSessionId.value

  stoppingIntentionally = true
  cancelReconnect()

  await teardownViewer()
  windowOpen.value = false
  windowAgentId.value = null

  if (sessionId) {
    try {
      await api.stopRemoteSession(sessionId)
    } catch {
      // 404 = 它已经结束了（被控端掉线 / 中心清理过）。那正是我们要的终态。
    }
  }

  await refreshSessions()

  stoppingIntentionally = false
}

// ── 断线重连 ──────────────────────────────────────────────────

function stopReconnectTimer() {
  if (reconnectTimer !== null) {
    window.clearInterval(reconnectTimer)
    reconnectTimer = null
  }
}

function cancelReconnect() {
  stopReconnectTimer()

  if (reconnect.value.state !== 'idle') {
    reconnect.value = { state: 'idle', attempt: 0, reason: '', secsLeft: 0 }
  }
}

/**
 * 安排一次自动重连（带退避与上限）。
 *
 * <p>
 * ⚠️ <b>只对"窗口开着"的观看者生效</b>：窗口关掉之后，页面上没有人需要画面，
 * 继续每 16 秒建一次会话只是在替一个不在的观众烧带宽与被控端 CPU。
 * </p>
 *
 * <p>
 * 🔥 <b>退避与合并的规则不在这里</b>，在 {@link planReconnect}（有 9 条用例）——
 * 那三条判断（别叠并发请求 / 合并重复触发 / 次数用完放弃）都<b>没有界面证据</b>，
 * 写在组件里就只能靠人读代码，而"没人读"正是它们出错的方式。
 * </p>
 */
function scheduleReconnect(reason: string) {
  if (stoppingIntentionally || !windowOpen.value) return

  const plan = planReconnect(reconnect.value.state, reconnect.value.attempt)

  if (plan.action === 'ignore') return

  if (plan.action === 'merge') {
    reconnect.value = { ...reconnect.value, reason }
    return
  }

  if (plan.action === 'giveup') {
    reconnect.value = {
      state: 'failed',
      attempt: reconnect.value.attempt,
      reason,
      secsLeft: 0,
    }

    return
  }

  reconnectDueAt = performance.now() + plan.delayMs
  reconnect.value = { state: 'waiting', attempt: plan.attempt, reason, secsLeft: plan.delayMs / 1000 }

  stopReconnectTimer()

  reconnectTimer = window.setInterval(() => {
    const left = secondsLeft(reconnectDueAt, performance.now())

    if (left <= 0) {
      stopReconnectTimer()
      void doReconnect()
    } else if (left !== reconnect.value.secsLeft) {
      reconnect.value = { ...reconnect.value, secsLeft: left }
    }
  }, 250)
}

/**
 * 用**当前表单值**凑一份"重建参数"（只作兜底）。
 *
 * ⚠️ 它和 `lastConnectParams` 的用途刻意相反：这个读现在，那个读"刚才"。
 * 只有"不知道刚才是什么"时才用它 —— 见 `doReconnect`。
 */
function paramsFromForm() {
  const agentId = windowAgentId.value ?? targetAgentId.value

  if (agentId === null) return null

  return {
    agentId,
    screenId: selectedScreenId.value,
    maxFps: maxFps.value,
    displayMode: displayMode.value,
    quality: chosenQuality.value,
  }
}

/** 真的去重建这一路。 */
async function doReconnect() {
  // 🔥 没有记录时用**当前表单**兜底，而不是直接放弃（M7.13 修正）。
  //    放弃那条路给的提示是「请手动点『连接』」，可用户刚点的就是连接 ——
  //    那是一条**死胡同**：提示让你做的事，你已经在做了。
  //    用表单兜底是"唯一可用的信息"，最坏结果只是恢复成面板上现在的样子。
  const params = lastConnectParams.value ?? paramsFromForm()

  if (!params) {
    reconnect.value = {
      state: 'failed',
      attempt: reconnect.value.attempt,
      reason: '不知道这是哪台机器（窗口没有归属记录），请关掉窗口重新点「连接」。',
      secsLeft: 0,
    }

    return
  }

  reconnect.value = { ...reconnect.value, state: 'connecting', secsLeft: 0 }

  // 恢复到"上一次那一幕"的参数（不是现在的表单值，见 lastConnectParams 的说明）。
  targetAgentId.value = params.agentId
  selectedScreenId.value = params.screenId
  maxFps.value = params.maxFps
  displayMode.value = params.displayMode
  chosenQuality.value = params.quality

  await startSession({ silent: true, openWindow: true })

  if (activeSessionId.value) {
    emit('notice', 'ok', `已重新连上「${agentName(params.agentId)}」。`)
    reconnect.value = { state: 'idle', attempt: 0, reason: '', secsLeft: 0 }
    return
  }

  // 建不起来（机器还是离线的）⇒ 继续退避重试，直到用完次数。
  const failedAttempt = reconnect.value.attempt
  reconnect.value = { ...reconnect.value, attempt: failedAttempt }
  scheduleReconnect('重建会话失败（那台机器可能还在离线 / 没人登录）。')
}

/** 顶栏「重连」：立刻重来一次，并把计数清零（这是人工介入，不该受自动重试次数限制）。 */
async function reconnectNow() {
  stopReconnectTimer()
  reconnect.value = { state: 'connecting', attempt: 0, reason: '', secsLeft: 0 }
  await doReconnect()
}

/**
 * 「按当前设置重建会话」—— **热更改**的落地。
 *
 * <p>
 * 🔥 <b>为什么它和 `reconnectNow` 不是同一个东西</b>：
 * 重连是"恢复上一次那一幕"（用 `lastConnectParams`，见那里的说明）；
 * 而这个按钮是"**按我现在改成的样子**再开一次" —— 两者用同一套重建流程，
 * 但**参数来源相反**。合成一个函数的话，用户改完下拉框点"重连"，
 * 会被悄悄恢复成旧参数，而那看起来像"下拉框根本没生效"。
 * </p>
 *
 * <p>
 * ⚠️ 窗口**不关**：重建期间只是画面黑几秒，参数面板与悬浮位置都不动。
 * 这正是"连接状态下热更改"的含义 —— 不是"断开 → 改 → 再连"。
 * </p>
 */
async function applyParams() {
  // 🔥 目标机器取 `windowAgentId`：会话已经断了的时候 `watchingAgentId` 是 `null`，
  //    而"参数面板改成什么样了就按什么样再开一次"这件事跟"现在有没有在推"无关。
  const agentId = windowAgentId.value ?? watchingAgentId.value ?? targetAgentId.value

  if (agentId === null) return

  targetAgentId.value = agentId
  stoppingIntentionally = false

  reconnect.value = {
    state: 'connecting',
    attempt: 0,
    reason: '正在按新参数重建会话…',
    secsLeft: 0,
  }

  await startSession({ silent: true, openWindow: true })

  if (activeSessionId.value) {
    reconnect.value = { state: 'idle', attempt: 0, reason: '', secsLeft: 0 }
    emit('notice', 'ok', `已按新设置重建「${agentName(agentId)}」这一路。`)
    return
  }

  scheduleReconnect('按新参数重建会话失败。')
}

/**
 * 关标签页 / 关窗口前的最后一道闸（M7.11）。
 *
 * <p>
 * 🔥 为什么光有 Keyboard Lock 还不够：它**只在全屏时可用**。非全屏观看时
 * Ctrl+W 依旧归浏览器，标签页会**无声消失**（不报错、不崩溃，用户以为"Edge 强制关了"）。
 * 注册了 `beforeunload` 之后，Chromium 会先弹一次"离开此网站？"——
 * 于是"页面突然没了"变成"页面问了一句，我点了取消"。
 * </p>
 *
 * <p>
 * ⚠️ 只在**正在观看会话**时拦（判据见 `shouldWarnBeforeUnload`）：
 * 没在看画面时关页是正常操作，不该多一次确认。
 * </p>
 *
 * <p>
 * ⚠️ 副作用是**故意的**：它同时是这件事的取证手段 ——
 * 弹出确认框 ⇒ 是浏览器侧的关闭动作（键盘 / 鼠标 / 菜单）；
 * 页面直接消失且没弹框 ⇒ 进程被丢弃或杀掉，那要往 Edge 的内存策略上查。
 * </p>
 */
function onBeforeUnload(event: BeforeUnloadEvent) {
  if (!shouldWarnBeforeUnload(activeSessionId.value)) return

  event.preventDefault()

  // 老浏览器只认 `returnValue`；现代 Chromium 只要 preventDefault 就会弹。
  // 两样都写，才能覆盖"现场可能用的任何内核"。
  event.returnValue = ''
}

async function endSession(session: RemoteSessionDto) {
  try {
    await api.stopRemoteSession(session.sessionId)
  } catch (err) {
    // 404 是"它其实已经结束了"—— 那正是我们想要的终态，不该报成错误。
    if (!(err instanceof ApiError) || err.status !== 404) {
      emit('notice', 'err', err instanceof ApiError ? err.message : '结束会话失败。')
      return
    }
  }

  await refreshSessions()
  emit('notice', 'ok', `已结束会话「${session.sessionId}」。`)
}

/**
 * 列表里的「结束会话」（M7.14）：**不进窗口**就把这一路收掉。
 *
 * <p>
 * 🔥 典型用法是「挂起」之后：窗口关了、画面上什么按钮都没有，而这一路还在推流
 * （被控端在编码、你在占着那台机器唯一的一路会话名额）。原来只能"再点进去 → 点断开"，
 * 那要先建立一次 4K 解码再来拆它 —— 所以现场会干脆用「断开」代替「挂起」。
 * </p>
 *
 * <p>
 * ⚠️ <b>`stoppingIntentionally` 必须在这一路正好是这个窗口在看时才置</b>：
 * 窗口关着的时候 `scheduleReconnect` 本来就不会启动（它判 `windowOpen`），
 * 但"窗口开着、另一台机器"这种情形下把它置上，就会顺手掐掉**别的机器**正在跑的重连。
 * </p>
 */
async function endSessionOf(agent: AgentHostDto) {
  const session = sessionToResume(agent.id)

  if (!session) return

  const wasWatching = activeSessionId.value === session.sessionId

  if (wasWatching) {
    stoppingIntentionally = true
    cancelReconnect()
  }

  await endSession(session)

  // 会话已经没了：把"还在看这一路"的本地状态一起收干净，
  // 否则窗口标题还挂着推流中的旧帧率，而那一路上一个字都不会再来。
  if (wasWatching) {
    await teardownViewer()
    windowOpen.value = false
    windowAgentId.value = null
    stoppingIntentionally = false
  }
}

async function toggleGrayscale(session: RemoteSessionDto) {
  const next = !session.grayscale

  try {
    const updated = await api.setRemoteGrayscale(session.sessionId, next)

    const index = sessions.value.findIndex((s) => s.sessionId === updated.sessionId)
    if (index >= 0) sessions.value[index] = updated

    emit(
      'notice',
      'warn',
      `黑白模式已请求${next ? '开启' : '关闭'} —— 注意它是**会话级**的，`
        + '所有看这一路的人都会跟着变；而且被控端没有回执，以画面为准。',
    )
  } catch (err) {
    emit('notice', 'err', err instanceof ApiError ? err.message : '切换黑白模式失败。')
  }
}

// ═══════════════════════════════════════════════════════════════
// 质量档（M5.5）
// ═══════════════════════════════════════════════════════════════

const qualityBusy = ref(false)

/**
 * 五个档位的展示顺序与说明（后端 `RemoteQualityProfiles` 认的就是这五个字符串）。
 *
 * 🔥 **「自动」必须写清它与「均衡」的区别**：两者的**起始**码率/帧率是同一套数字
 * （`Resolve()` 把 auto 解析成 balanced 的数），差别只有"编不动时会不会自己往下走"。
 * 不写这句，用户切到"自动"发现码率没变，会以为这个档位坏了。
 *
 * 🔥 **「流畅」必须写清它的前提是硬编**：软件 JPEG 那条路被抓屏卡在约 20 fps，
 * 被控端会如实夹到 20。不写这句，现场切到 60 却看到 20，会以为按钮是坏的。
 */
const QUALITY_CHOICES = [
  {
    value: REMOTE_QUALITY_PROFILES.auto,
    label: '自动',
    hint: '从「均衡」起步，编不动时自动往下走 —— 起始效果与均衡一样，区别只在会不会自己降',
  },
  { value: REMOTE_QUALITY_PROFILES.low, label: '低', hint: '弱网 / 老机器：优先保「能看见」' },
  { value: REMOTE_QUALITY_PROFILES.balanced, label: '均衡', hint: '默认档' },
  { value: REMOTE_QUALITY_PROFILES.high, label: '高', hint: '4K / 8K 与局域网充裕时用' },
  {
    value: REMOTE_QUALITY_PROFILES.smooth,
    label: '流畅',
    hint: '60 帧，看动画 / 要跟手 —— 需要被控端有硬编；走软件 JPEG 的机器会停在 20 fps',
  },
] as const

/**
 * 当前档位；`null` = 服务端没给这个字段（旧版）。
 *
 * ⚠️ `null` **不许画成「自动」** —— 那是把一个"读不到"写成"它是自动"，
 * 而这正是本项目反复吃亏的那类错误（未知被写成已知）。
 */
const activeQuality = computed(() => activeSession.value?.quality ?? null)

function qualityLabel(value: string | null | undefined): string {
  switch (value) {
    case REMOTE_QUALITY_PROFILES.auto:
      return '自动'
    case REMOTE_QUALITY_PROFILES.low:
      return '低'
    case REMOTE_QUALITY_PROFILES.balanced:
      return '均衡'
    case REMOTE_QUALITY_PROFILES.high:
      return '高'
    case REMOTE_QUALITY_PROFILES.smooth:
      return '流畅'
    case null:
    case undefined:
      return '—'
    default:
      return value
  }
}

/**
 * 切质量档。
 *
 * ⚠️ 与黑白模式同一个诚实缺口：**被控端没有回执**，所以"已请求"不等于"已生效"。
 * 而它比黑白模式更可能"看不见效果"：MF 的码率与质量参数是**创建时定死**的
 * （运行时改不动，`S_FALSE` + 回读不变），真正的改变要等编码器重建。
 * 所以提示语里必须说这一句，否则现场会以为按钮没生效。
 */
async function setQuality(profile: string) {
  const session = activeSession.value

  if (!session || qualityBusy.value) return

  qualityBusy.value = true

  try {
    const updated = await api.setRemoteQuality(session.sessionId, profile)

    // 🔥 记住这个选择（M7.16）：**重建会话时必须把它带上**。
    //    不带的话「按当前设置重建会话」会把档位悄悄打回 `auto` ——
    //    用户刚点了「流畅」看 60 帧，一次"重建"就退回 12 帧，而画面上没有任何提示。
    chosenQuality.value = profile

    const index = sessions.value.findIndex((s) => s.sessionId === updated.sessionId)
    if (index >= 0) sessions.value[index] = updated

    emit(
      'notice',
      'ok',
      `已请求切到「${qualityLabel(profile)}」。⚠️ 这是会话级的（所有看这一路的人一起变），`
        + '而且被控端的码率参数要等编码器重建才真正改变 —— 一两秒内看不到变化是正常的。',
    )
  } catch (err) {
    emit('notice', 'err', describeQualityFailure(err))
  } finally {
    qualityBusy.value = false
  }
}

/** 把切档失败翻译成人话：502 与 400 的下一步动作完全不同。 */
function describeQualityFailure(err: unknown): string {
  if (!(err instanceof ApiError)) return '切换质量档位失败。'

  if (err.status === 502) {
    return `${err.message} —— 被控端可能刚掉线，画面本身也会随之中断。`
  }

  if (err.status === 400) {
    // ⚠️ 这个分支意味着**本页发了一个服务端不认的档位名**（本仓的 bug），
    //    不是用户操作的问题 —— 说清这一点，免得现场去怀疑自己的点击。
    return `${err.message} —— 这是本页与服务端的档位名不一致（本仓的问题），不是操作失误。`
  }

  if (err.status === 404) {
    return `${err.message} —— 这一路会话已经结束了，刷新一下列表。`
  }

  return err.message
}

// ═══════════════════════════════════════════════════════════════
// 剪贴板（M5.5）
// ═══════════════════════════════════════════════════════════════

const clipboardBusy = ref(false)

/** 最近一次知道的内容（`null` = 这一路还没读过）。 */
const clipboard = ref<RemoteClipboardDto | null>(null)

/** 要被写进被控机的那段文字（**本机的输入框**，与读到的那一格是两件事）。 */
const clipboardDraft = ref('')

/**
 * 收到服务端推来的剪贴板内容。
 *
 * ⚠️ **载荷里没有 sessionId**：服务端只把这条消息发到"这一路会话"的分组，
 * 所以这里能做的判断只有"此刻有没有在看某一路"。切换会话那一瞬间，
 * 上一路迟到的推送在理论上仍可能到达 —— 那正是 `watch()` 里必须**换路即清**的理由：
 * 让迟到的内容变成一个可见的空，而不是悄悄变成"别人的剪贴板"。
 */
function onClipboardChanged(dto: RemoteClipboardDto) {
  if (!activeSessionId.value) return

  // 🔴 **空载荷一律当"没内容"**：服务端在没读到文本时可能发 null，
  //    SignalR 也可能把一个空参数投递成 `undefined`。真被赋成 `undefined` 的话，
  //    模板里 `clipboard.text` 会抛异常**整个渲染函数崩掉**（画面与统计一起消失）。
  clipboard.value = dto ?? null

  // ⚠️ **不动 `clipboardDraft`**：那是用户正在编辑的东西，
  //    被一次推送覆盖掉的表现是"打着字突然没了"，而人只会以为自己误触了。
}

/**
 * 回填这一路**中心缓存**里的内容（切回某一路时用）。
 *
 * 💡 它不触发被控机动作（纯读缓存）—— 每次切路都让被控机打开一次剪贴板，
 * 会在那台机器上反复干扰其他程序的粘贴。
 */
async function loadClipboard() {
  const sessionId = activeSessionId.value

  if (!sessionId) return

  try {
    const dto = await api.getRemoteClipboard(sessionId)

    // ⚠️ 期间可能又切了路：迟到的响应不许写进新的那一格。
    if (activeSessionId.value !== sessionId) return

    // 🔴 `?? null`：这一路"还没读过"时服务端返回 204，`unwrap` 会给 `undefined`
    //    （见 api.ts 的 getRemoteClipboard 注释）。把 `undefined` 放进 `clipboard`，
    //    模板里的 `clipboard.text` 会抛异常 ⇒ **整个面板渲染失败**（画面也不出）。
    clipboard.value = dto ?? null
  } catch {
    /* 读不到缓存不是错误：那一格显示"还没读过"即可 */
  }
}

/** 请被控机**读一次**它的剪贴板（结果主要靠 `ClipboardChanged` 推回来）。 */
async function readClipboard() {
  const session = activeSession.value

  if (!session || clipboardBusy.value) return

  clipboardBusy.value = true

  try {
    const dto = await api.readRemoteClipboard(session.sessionId)

    // 服务端会先把**上一次**的内容给你（可能有）—— 这次的结果随后经推送到达。
    if (dto) clipboard.value = dto

    // ⚠️ **必须给一句反馈**：这个动作本身不返回新内容（要等推送），
    //    不给提示的话，"点了没反应"会被当成按钮坏了 —— 而那正是本项目
    //    「`void asyncFn()` 连异常一起吞」那条铁律的孪生兄弟：没反应 ≠ 没执行。
    emit('notice', 'ok', '已请被控机读一次剪贴板 —— 内容到了会直接出现在下面。')
  } catch (err) {
    emit('notice', 'err', describeClipboardFailure(err))
  } finally {
    clipboardBusy.value = false
  }
}

/**
 * 把草稿写进**被控机**的剪贴板。
 *
 * 🔴 这是作用到别的机器的写操作（服务端 `AdminOnly`）。
 * ⚠️ 超限**在前端先判**：让用户看到"只会发前 2 万字"，而不是点完之后收一个 400。
 *    这里用截断而不是拒绝，是因为 2 万字以上的输入框内容，用户要的多半是"发过去看看"，
 *    而不是"先自己拆成两半"。
 */
async function sendClipboard() {
  const session = activeSession.value

  if (!session || clipboardBusy.value) return

  const raw = clipboardDraft.value

  if (raw.length === 0) {
    emit('notice', 'warn', '输入框是空的 —— 先写点东西再发。')
    return
  }

  const truncated = raw.length > REMOTE_CLIPBOARD_MAX_CHARS
  const text = truncated ? raw.slice(0, REMOTE_CLIPBOARD_MAX_CHARS) : raw

  clipboardBusy.value = true

  try {
    const result = await api.writeRemoteClipboard(session.sessionId, text)

    clipboard.value = result

    emit(
      'notice',
      'ok',
      truncated
        ? `已发出：原 ${raw.length} 字超过上限，只把前 ${REMOTE_CLIPBOARD_MAX_CHARS} 字写进了被控机的剪贴板。`
        : `已把这 ${text.length} 字写进被控机的剪贴板。`,
    )
  } catch (err) {
    emit('notice', 'err', describeClipboardFailure(err))
  } finally {
    clipboardBusy.value = false
  }
}

/**
 * 把读到的那段文字复制到**本机**剪贴板（"读"这个动作的真正用途）。
 *
 * ⚠️ `navigator.clipboard` **只在安全上下文里存在** —— 和 `AudioDecoder` 一样，
 * 局域网里按 `http://192.168.x.x` 打开时它不是"报错"，是"压根没有"。
 * 所以这里必须给一条能走通的路（手动选中复制），而不是只回一句"复制失败"。
 */
async function copyClipboardToLocal() {
  const text = clipboard.value?.text

  if (!text) return

  if (!navigator.clipboard?.writeText) {
    emit(
      'notice',
      'warn',
      '这个页面不在安全上下文里，浏览器不让写本机剪贴板 —— 请在下面那段文字里手动选中复制。',
    )
    return
  }

  try {
    await navigator.clipboard.writeText(text)

    emit('notice', 'ok', `已复制 ${text.length} 字到本机剪贴板。`)
  } catch {
    emit('notice', 'err', '浏览器拒绝了写本机剪贴板 —— 请在下面那段文字里手动选中复制。')
  }
}

/** 把剪贴板的失败翻译成人话：400 / 502 / 404 的下一步动作完全不同。 */
function describeClipboardFailure(err: unknown): string {
  if (!(err instanceof ApiError)) return '剪贴板操作失败。'

  if (err.status === 502) {
    return `${err.message} —— 被控端可能刚掉线；被控机的剪贴板没有改变。`
  }

  if (err.status === 400) {
    return `${err.message} —— 这条请求本身有问题（内容超限或为空），改内容后再试。`
  }

  if (err.status === 404) {
    return `${err.message} —— 这一路会话已经结束了，刷新一下列表。`
  }

  return err.message
}

/** 清掉画面与声音相关的本地状态（切会话 / 收场时调）。 */
function resetFrameState() {
  paintedFrames.value = 0
  frameWidth.value = 0
  frameHeight.value = 0
  latencyMs.value = null
  latencySuspect.value = false

  // ⚠️ 必须一起清：它是"上一路那个关键帧"的读数。留着的话，切到一个正常会话之后
  //    页面会一直挂着上一路的"纯色"告警 —— 而那个告警看起来完全正常，只是指向了错的分辨率。
  blankFrameNote.value = null

  lastPaintedSeqByTile.clear()
  tileGeometry.clear()
  activeTileCount = 1

  // ── M7.7：尺寸一致性读数（换了一路 / 换了屏之后，上一路的数不能留）──
  decodedSize.value = null
  frameSizeNote.value = null
  lastSizeCheckKey = ''

  // ── M5.3：声音，以及"画面与声音共享哪条时间轴" ──
  //
  // 🔥 `clock.reset()` 必须在这里。视频与音频**共用同一个时钟实例**（那正是"对齐"
  //    这个词的全部含义），而它的锚点是从上一路估出来的 —— 留着的话，新一路的第一帧
  //    会被按上一路的时间轴排期，表现是"刚切过去画面卡住几秒"或"声音一上来就落后一大截"。
  clock.reset()
  clockReady.value = false

  // 🔥 **必须连缓冲一起丢掉**（M5.4-b 第 8 步改）：
  //    缓冲里住着一个**按上一路编码格式造出来的解码器** —— 切到另一种格式的会话时，
  //    留着它等于**拿 MJPEG 的解码器去解 H.264**（或反过来）。
  //    表现是"画面全黑 / 花屏，而两端日志全正常"，正是本步反复在防的那一类假象。
  //    丢掉之后由下一帧的 `ensureVideoBuffer(codec)` 按**新的**格式重建。
  //
  //    ⚠️ 原来这里只 `clear()`（不 `stop()`），理由是"让 rAF 循环留着跑、新一路第一帧
  //       就能立刻排期"。那个收益是一帧的启动延迟，而代价是一条"解码器可能是错的"的路 ——
  //       而且重建同样是在第一帧上、同样立刻排期，收益其实并不存在。
  stopAllVideoBuffers()

  videoStats.value = null
  videoDecoderStats.value = null
  painted.monoMs = null
  painted.atPerf = 0
  avOffsetMs.value = null

  stopAudio()
  audioUi.value = 'idle'
  audioMessage.value = ''

  const canvas = canvasRef.value

  if (canvas) {
    const ctx = canvas.getContext('2d')
    ctx?.clearRect(0, 0, canvas.width, canvas.height)
    canvas.width = 0
    canvas.height = 0
  }
}

const displayModeClass = computed(() =>
  displayMode.value === REMOTE_DISPLAY_MODES.actual ? 'actual' : 'fit',
)

// ═══════════════════════════════════════════════════════════════
// 网页全屏（M7.9）
// ═══════════════════════════════════════════════════════════════
//
// 🔥 **全屏的是"画面那一块"，不是整页**：页面上还有工具栏、读数、剪贴板。
//    全屏整页等于把一堆排障数字放大到投影上，而用户要的是**看那台机器的屏幕**。
//
// ⚠️ 状态**只认 document**（`isElementFullscreen`）：用户按 Esc 退出时不会走我们的
//    按钮，浏览器自己就退了，只补发一个 `fullscreenchange`。拿本地布尔当状态的话，
//    按钮会永远停在「全屏显示」，第二下点下去还会把别人的全屏（或全屏状态的杂乱中间态）退掉。

/** 画面外框 —— 全屏请求打在**它**身上（见 `fullscreen.ts` 的说明）。 */
const viewportRef = ref<HTMLElement | null>(null)

/** 此刻是不是"我们的画面"在全屏。 */
const isFullscreen = ref(false)

/**
 * 这个环境支不支持全屏。
 *
 * ⚠️ 先按"支持"画，挂载后再问 `document`：SSR / 首帧没有 document 时也安全，
 * 而且"不支持"这个结论必须来自浏览器（显式 `false`），不能靠猜。
 */
const fullscreenSupported = ref(true)

/** 一次切换在飞 —— 连点两下的第二下会把刚开的全屏立刻退掉（来回闪）。 */
const fullscreenBusy = ref(false)

const canFullscreen = computed(() => isFullscreen.value || fullscreenSupported.value)

/**
 * 全屏 ↔ 还原。
 *
 * 🔴 **第一个 `await` 之前就必须把请求发出去**：浏览器只认用户手势的同一个任务。
 * 这里若先 `await` 一次接口（哪怕 200 ms），凭据就失效 ⇒ Chrome 抛
 * `NotAllowedError`，现场看到的是"点了没反应"（见 `describeFullscreenFailure` 里那条文案）。
 */
async function toggleFullscreen() {
  const element = viewportRef.value

  if (!element || fullscreenBusy.value) return

  fullscreenBusy.value = true

  try {
    if (isElementFullscreen(document, element)) await exitFullscreen(document)
    else await enterFullscreen(element)
  } catch (err) {
    emit('notice', 'err', describeFullscreenFailure(err))
  } finally {
    fullscreenBusy.value = false
  }
}

/**
 * 全屏状态变了（进入 / Esc 退出 / 切窗口都走这里）。
 *
 * 🔥 两件事必须在这里做，而且**只能**在这里做：
 *   ① 把状态从 `document` 读回来（唯一真源）；
 *   ② **重报视口** —— 全屏之后可视区变大了，1:1 模式下被控端只编"这一块"，
 *      不重报的话，新露出来的那一大片会永远停在旧帧上（不报错、像"画面没跟上"）。
 */
function onFullscreenChange() {
  const now = isElementFullscreen(document, viewportRef.value)

  isFullscreen.value = now

  scheduleViewportReport()

  // 全屏之后键盘要能直接用：canvas 不聚焦就收不到 keydown。
  if (now) canvasRef.value?.focus()

  // 🔴 M7.11：全屏是 Keyboard Lock 的**硬前提** —— 也只有在这一刻，
  //    Ctrl+W / Ctrl+R / F5 才能从浏览器手里抢回来交给被控端。
  //    ⚠️ 锁不上是正常的（用户激活过期 / 浏览器不支持）：不提示、不报错，
  //    那几条键仍归浏览器，由 `beforeunload` 兜底（关页面前先问一句）。
  if (now) void lockBrowserKeys(navigator)
  else unlockBrowserKeys(navigator)
}

/**
 * 退出全屏（没有在全屏时是空操作）。
 *
 * ⚠️ 失败**不提示**：用户按过 Esc 之后浏览器自己已经退了，此时再调一次会抛
 * `InvalidStateError` —— 那正是"已经是我们要的状态"，弹一条红条只会让人以为坏了。
 */
function leaveFullscreen() {
  if (!isFullscreen.value) return

  // ⚠️ 立刻还键，不等 `fullscreenchange`：退出全屏后那些键必须重新归浏览器，
  //    否则用户回到别的标签页会发现 Ctrl+W 关不掉了（而他会以为是"这个网站很怪"）。
  unlockBrowserKeys(navigator)

  void exitFullscreen(document).catch(() => {
    /* 浏览器可能已经自己退了：按"已退出"处理。 */
  })
}

// ═══════════════════════════════════════════════════════════════
// ROI：把"用户现在看到的是哪一块"告诉被控端（M5.5 第 5 项）
// ═══════════════════════════════════════════════════════════════

/**
 * 滚动停止多久之后才发视口（毫秒）。
 *
 * 🔥 <b>300 ms 是"手停了"与"手在拖着走"的分界</b>：一次视口变化会让被控端
 * **重建编码器**（≈0.3 秒空窗、首帧必关键帧）。每滚一下就发一次的实现，
 * 会让画面始终停在关键帧上 —— 看起来是"帧率更低了"，而不像"发得太勤"。
 *
 * ⚠️ 去抖**只在这一侧做**：被控端与中心都直接转达。两处各去抖一次会变成 600 ms，
 * 用户会觉得"滚完了还卡一下"。
 */
const VIEWPORT_DEBOUNCE_MS = 300

let viewportTimer: number | null = null

/** 上一次真的发出去的视口（`"x,y,w,h"`）—— 同一条不重复发。 */
let lastReportedViewport = ''

/**
 * 算出"画布上此刻可见的那一块"（**画面像素**坐标）。
 *
 * ⚠️ 三个坑，都会安静地错：
 *   ① 用的是 **canvas 的 `getBoundingClientRect()`** 而不是 CSS 像素常量 ——
 *      `fit` 模式下 canvas 被 CSS 缩小了，拿 CSS 像素发出去会让视口偏小且位置全错；
 *   ② 滚动容器是 `.rd-viewport`（canvas 的父元素），不是 `window`；
 *   ③ 越界要夹进画面内（滚动到边缘时可见区可能比画布还大一点点，取整会多出 1 px）。
 */
function measureViewport(): { x: number; y: number; width: number; height: number } | null {
  const canvas = canvasRef.value
  const scroller = canvas?.parentElement

  if (!canvas || !scroller || canvas.width <= 0 || canvas.height <= 0) return null

  const canvasRect = canvas.getBoundingClientRect()
  const viewRect = scroller.getBoundingClientRect()

  if (canvasRect.width <= 0 || canvasRect.height <= 0) return null

  // 可见矩形 = canvas 与滚动容器的交集（设备无关像素）。
  const left = Math.max(canvasRect.left, viewRect.left)
  const top = Math.max(canvasRect.top, viewRect.top)
  const right = Math.min(canvasRect.right, viewRect.right)
  const bottom = Math.min(canvasRect.bottom, viewRect.bottom)

  if (right <= left || bottom <= top) return null

  // CSS 像素 → 画面像素。
  const scaleX = canvas.width / canvasRect.width
  const scaleY = canvas.height / canvasRect.height

  const x = Math.max(0, Math.floor((left - canvasRect.left) * scaleX))
  const y = Math.max(0, Math.floor((top - canvasRect.top) * scaleY))

  const width = Math.min(canvas.width - x, Math.ceil((right - left) * scaleX))
  const height = Math.min(canvas.height - y, Math.ceil((bottom - top) * scaleY))

  if (width < 2 || height < 2) return null

  return { x, y, width, height }
}

/**
 * 把当前视口发给中心（被控端据此只编这一块）。
 *
 * 🔥 **只在 1:1 模式下有意义**：等比缩放模式下"整幅都在屏幕上"，
 * 没有"视口"这个概念 —— 那时应该发"关掉"，否则用户从 1:1 切回来之后
 * 被控端还在只编一小块，画面上就是"只剩一角，其余全黑"。
 */
async function reportViewport(forceOff = false) {
  viewportTimer = null

  const sessionId = activeSessionId.value

  if (!sessionId) return

  const actual = !forceOff && displayMode.value === REMOTE_DISPLAY_MODES.actual
  const rect = actual ? measureViewport() : null

  // 关掉：四个 0（契约里"宽或高为 0 = 没有视口"）。
  const next = rect ?? { x: 0, y: 0, width: 0, height: 0 }
  const key = `${next.x},${next.y},${next.width},${next.height}`

  if (key === lastReportedViewport) return

  lastReportedViewport = key

  try {
    await api.setRemoteViewport(sessionId, next)

    const index = sessions.value.findIndex((s) => s.sessionId === sessionId)
    if (index >= 0 && sessions.value[index]) {
      sessions.value[index] = {
        ...sessions.value[index],
        viewportX: next.x,
        viewportY: next.y,
        viewportWidth: next.width,
        viewportHeight: next.height,
      }
    }
  } catch {
    // ⚠️ **不弹提示**：视口是一次"优化"而不是一次"操作"，
    //    失败时最坏的结果就是"整幅照旧在传"（画面仍然是对的）。
    //    弹一个红条会让用户以为画面出问题了。
    //    但要允许下一次重试：把记录清掉，否则同一个视口永远不会再发。
    lastReportedViewport = ''
  }
}

/** 滚动 / 切显示模式时调用：去抖之后才真的发。 */
function scheduleViewportReport() {
  if (viewportTimer !== null) window.clearTimeout(viewportTimer)

  viewportTimer = window.setTimeout(reportViewport, VIEWPORT_DEBOUNCE_MS)
}

/**
 * 不再看这一路时，把视口关掉。
 *
 * ⚠️ **必须发**：不关的话被控端会一直只编那一小块 —— 下一个看这一路的人
 * 打开就只看到"画面的一角"，而且他不知道是上一任观众留下的视角。
 */
function resetViewport() {
  if (viewportTimer !== null) {
    window.clearTimeout(viewportTimer)
    viewportTimer = null
  }

  lastReportedViewport = ''

  // ⚠️ **强制关**：不能按 `displayMode` 判 —— 退出时它可能还是 `actual`，
  //    那样算出来的是"当前视口"，而我们恰恰是要把它取消掉。
  void reportViewport(true)
}

// ⚠️ 用 `vueWatch` 这个别名：本文件里已经有一个 `watch(sessionId)`（开始看某一路），
//    直接叫 `watch` 会与它重名 —— 而那种冲突只会报一句 TS2440，很容易被当成"导入写错了"。
vueWatch(displayMode, () => {
  scheduleViewportReport()
})

// ═══════════════════════════════════════════════════════════════
// 生命周期
// ═══════════════════════════════════════════════════════════════

let refreshTimer: number | null = null
let playbackTimer: number | null = null

onMounted(async () => {
  // 🔥 安全上下文必须**在这里**判一次，不能等音频起不来时再问：
  //    非 https 下 `AudioDecoder` / `AudioWorklet` 是**不存在**（不是报错），
  //    等它失败时页面上只剩一句"音频不可用"，用户完全不知道该做什么。
  isSecureContext.value = typeof window === 'undefined' ? true : window.isSecureContext

  // 这一趟是给上面那句"该做什么"补上下半句（服务端备没备好 https）。
  // 拿不到就只是不显示横幅，不影响观看。
  void loadTrustInfo()

  // 🔥 编码能力探测**在挂载时就发起**（M5.4-b 第 8 步）：它是异步的，
  //    提前跑完，用户在页面上看到的声明与点"开始观看"时发出去的就是同一个值。
  //    ⚠️ 这一句不是"预热"—— 不跑它，`viewerVideoDeclaration` 会一直停在"探测中…"。
  void ensureViewerProbe()

  // M7.9：全屏能力与状态。⚠️ 只有显式 `false` 才算"不支持"（WebKit 上是 `undefined`）。
  fullscreenSupported.value = isFullscreenSupported(document)

  // ⚠️ `webkitfullscreenchange` 也要听：旧 WebKit 只发带前缀的那一个，
  //    只听标准事件的话，Safari 里的按钮会永远停在「全屏显示」。
  document.addEventListener('fullscreenchange', onFullscreenChange)
  document.addEventListener('webkitfullscreenchange', onFullscreenChange)

  // M7.11：正在观看时关页面先问一句（详见 `onBeforeUnload`）。
  window.addEventListener('beforeunload', onBeforeUnload)

  await connectHub()
  await Promise.all([refreshSessions(), refreshAgents()])

  // 🔥 **必须有这个轮询**：会话的死亡有一半是"没有事件"的 ——
  //    被控端掉线、超时被中心清理、别的窗口结束了它。
  //    只靠 `SessionChanged` 的话，页面会停在一个永远不动的最后一帧上。
  refreshTimer = window.setInterval(() => {
    void refreshSessions()
  }, 5000)

  // 播放侧读数每 250 ms 刷一次（为什么**不**逐帧刷，见 `refreshPlaybackStats`）。
  // ⚠️ 没有会话时它只是在写几个没变化的 `null` —— Vue 按值比较，不会因此重渲染，
  //    所以没必要跟着观看状态起停（起停本身就要多一条"忘了停"的路径）。
  playbackTimer = window.setInterval(refreshPlaybackStats, 250)

  // 🔥 **窗口尺寸变了也要重报视口**（1:1 模式下的一个真实缺口）：
  //    1:1 时 canvas 的 CSS 尺寸是固定的（就是画面像素），拉大浏览器窗口
  //    **不产生 scroll 事件**，但它让 `.rd-viewport` 的可见区变大了 ⇒
  //    新露出来的那一条本该是新内容，而没人重报 ⇒ 被控端还只编原来那块，
  //    页面上下/左右出现一条**永远停在旧帧**的区域（不报错，像"画面没跟上"）。
  //    ⚠️ 走同一个 300 ms 去抖：拉窗口边缘会连发几十个 resize。
  window.addEventListener('resize', scheduleViewportReport)
})

onUnmounted(() => {
  if (refreshTimer !== null) {
    window.clearInterval(refreshTimer)
    refreshTimer = null
  }

  // ⚠️ 重连计时器同理：留着它会在组件卸载后继续每 250ms 跑一次，
  //    到点还会真去建会话 —— 那是一条"页面已经不在了，却仍在往中心打请求"的路径。
  stopReconnectTimer()

  if (playbackTimer !== null) {
    window.clearInterval(playbackTimer)
    playbackTimer = null
  }

  // ⚠️ 与上面那个 `addEventListener` 成对：组件卸载后还留着一个闭包在监听 resize，
  //    它会去读已经卸载的 canvas ⇒ 每次拉窗口都白跑一次（并且 `scheduleViewportReport`
  //    会再排一个 300 ms 后执行的 `reportViewport`，那一路又没有会话可发）。
  window.removeEventListener('resize', scheduleViewportReport)

  // ⚠️ 屏清单的重问定时器同理：留着它会在组件卸载后继续每 1 秒发一次 HTTP。
  clearScreensRetry()

  // ⚠️ 与挂载时那两个 `addEventListener` 成对（M7.9）。
  document.removeEventListener('fullscreenchange', onFullscreenChange)
  document.removeEventListener('webkitfullscreenchange', onFullscreenChange)

  window.removeEventListener('beforeunload', onBeforeUnload)

  // 🔴 键必须还回去（M7.11）：组件卸载时若还在全屏/还锁着，
  //    用户回到别的页面会发现 Ctrl+W、Ctrl+R 全都不响应。
  unlockBrowserKeys(navigator)

  void teardownViewer()
  void disconnectHub()
})

defineExpose({ refreshSessions })
</script>

<template>
  <section class="panel">
    <div class="panel-head">
      <!--
        ⚠️ 标题里原来写着「（M5.1 观看 + M5.2 键鼠回传 + M5.3 声音）」——
        那是**内部里程碑编号**，不该出现在用户界面上：M5.1/M5.2 这些号对现场
        没有任何信息量，而"声音"是否可用其实只取决于一个前提（安全上下文，
        见下面那条横幅），不是"做没做 M5.3"。
      -->
      <h2>远程桌面</h2>
      <div class="panel-actions">
        <span class="badge" :class="connectionState === 'connected' ? 'ok' : 'warn'">
          实时通道：{{ connectionState === 'connected' ? '已连接' : connectionState === 'connecting' ? '连接中' : '未连接' }}
        </span>
        <button class="sm" :disabled="loading" @click="refreshSessions">刷新</button>
      </div>
    </div>

    <!--
      ── M7.11：这里原来挂着一条"M5.1 MJPEG 联调脚手架"的横幅，已删除 ──

      🔥 为什么必须删（不是"精简排版"）：那条文案在当时是真的，但 M5.4 之后
      **每一个字都不成立了** —— 硬编 H.264/HEVC 已经是正常路径，MJPEG 只是降级，
      "默认 12 fps" 也早换成了 57 fps（M7.4）。`docs/M5.4b-实施方案.md` 的 ④ 已经
      记过一句"这条文案会直接误导现场判断"，一直没清 —— 现场看到自己明明在跑硬编，
      页面却说"画面还是 MJPEG、4K60 要等 M5.4"，只能得出"这个页面在说胡话"。

      ⚠️ **无条件横幅本身就是错的形态**：它对每一路都出现，包括一切正常的那一路。
      现在改成**只在真降级时**说一句（见下面的 `codecFallbackNote`），
      键鼠那两条闸门则由输入控制区自己的两个徽标说（那里已经分开显示了）。
    -->

    <!--
      ⚠️ 这段横幅是**唯一**能解释"为什么没有声音"的地方，而且必须说成"下一步做什么"。
      浏览器的 WebCodecs（`AudioDecoder` / `VideoDecoder`）与 `AudioWorklet`
      **只在安全上下文里存在** —— 局域网里按 `http://192.168.x.x` 访问时，
      它们不是"报错"，是"这东西压根没定义"。不提前问一趟、不把原因说全，
      页面上就只剩一句"音频不可用"，而用户完全不知道该去开 https 还是换浏览器。
    -->
    <p v-if="!isSecureContext" class="notice warn" style="margin-bottom: 12px">
      <strong>这个页面不在安全上下文里，所以没有声音</strong>
      —— 浏览器的 <code>AudioDecoder</code> 与 <code>AudioWorklet</code> 在
      <code>http://</code> 的非本机地址下<strong>不存在</strong>，音频链根本建不起来。
      <br />
      <!--
        🔥 M7.11 修了一句**说反了的话**：这里原来写的是

            「画面与键鼠回传都不受影响（M5.1 的 MJPEG 走的是 <img>，没有这个限制）」

        前半句在硬编路径下是**错的**：H.264 / HEVC 走的是 WebCodecs `VideoDecoder`，
        它和 `AudioDecoder` 一样**只在安全上下文里存在** ⇒ 硬编这一路在
        `http://192.168.x.x` 下根本建不起来。画面之所以还看得见，
        是因为**降落到了 MJPEG**（`<img>`），不是因为"画面没有这个限制"。

        ⚠️ 这两种说法对现场的含义完全不同：前者意味着"你能看，但看的不是硬编"，
        后者会让人以为"https 只影响声音，画面是原样"——于是不去装证书，
        然后一直奇怪自己的 4K 为什么只有十几帧。
      -->
      <strong>画面会降级到 MJPEG</strong>
      （它走 <code>&lt;img&gt;</code>，不需要 WebCodecs），
      所以<strong>看得见，但帧率与清晰度都达不到硬编</strong>；
      键鼠回传本身不受影响。
      <br />
      <template v-if="trustInfo?.httpsEnabled && httpsAddress">
        ✅ 服务端已经备好了 https：<a :href="httpsAddress"><code>{{ httpsAddress }}</code></a>
        —— 换这个地址重开本页即可。
        <template v-if="trustInfo.caUrl">
          首次访问要装上根证书：<a :href="trustInfo.caUrl"><code>{{ trustInfo.caUrl }}</code></a>
          <template v-if="trustInfo.caFingerprint">
            （指纹 <code>{{ trustInfo.caFingerprint }}</code>，装之前对一眼，别装错机器上那张）
          </template>
        </template>
      </template>
      <template v-else>
        服务端当前<strong>没有</strong>可用的 https
        <template v-if="trustInfo?.caReason">（{{ trustInfo.caReason }}）</template>
        —— 这一路要等运维把 https 配起来。
      </template>
    </p>

    <p v-if="loadError" class="notice err">{{ loadError }}</p>
    <p v-if="viewerError" class="notice warn">{{ viewerError }}</p>

    <!-- ═══════════════════════════════════════════════════════════
         机器列表（M7.13）

         🔥 这一页现在**只有**「机器 + 连接按钮」，一行一台。改版前它是一张
         十几行的表单：选机器 → 抓哪块屏 → 显示方式 → 帧率 → 质量档 → 输入 →
         剪贴板 → 声音 → 十来个读数 → 会话列表。其中**八成只在连上之后才有意义**，
         于是想连一台机器要先跨过一整屏与当下无关的控件，连上之后画面又被挤到
         折叠线以下 —— 远控页最该大的东西反而最小。

         ⇒ 参数全部进窗口内的悬浮面板（见 `SHEET_TABS` 的 5 个分组）；
           会话也不再单列一张表：它就在对应机器那一行上（一台机器一路，本来就一一对应）。
         ═══════════════════════════════════════════════════════════ -->
    <div class="rd-machine-list">
      <div v-if="agents.length === 0" class="empty">
        <strong>还没有任何客户机</strong>
        客户机装上 Agent 并连上中心之后会出现在这里。
      </div>

      <div
        v-for="a in agents"
        :key="a.id"
        class="rd-machine-row"
        :class="{ 'is-watching': watchingAgentId === a.id }"
      >
        <div class="rd-machine-main">
          <div class="rd-machine-name">
            <strong>{{ a.displayName }}</strong>
            <span class="badge" :class="a.state === 'online' ? 'ok' : 'warn'">{{ a.stateDisplay }}</span>

            <!--
              🔥 这一格替代了原来那张「会话」表：一台机器同时只有一路，
                 所以"这台机器的会话"就是"这台机器"的一行，没必要分开看。
                 ⚠️ 没有会话时**不画**（而不是画成"空闲"）：空徽标会被读成一种状态。
            -->
            <span
              v-if="sessionOf(a.id)"
              class="badge"
              :class="stateClass(sessionOf(a.id)!.state)"
            >
              会话 {{ stateLabel(sessionOf(a.id)!.state) }}
              · {{ codecLabel(sessionOf(a.id)!.codec ?? null) }}
            </span>
          </div>

          <div class="muted small">
            {{ a.lastAddress ?? '地址未知' }}
            <template v-if="a.osDescription"> ｜ {{ a.osDescription }}</template>
            <template v-if="a.group"> ｜ {{ a.group }}</template>
          </div>
        </div>

        <div class="rd-machine-actions">
          <button
            class="sm primary"
            :disabled="starting || a.state !== 'online'"
            :title="
              a.state !== 'online'
                ? '这台机器现在不在线 —— 连不上（要等它的 Agent 重新连上中心）'
                : sessionToResume(a.id)
                  ? '这一路已经在推流，点进来直接看（不会重建会话）'
                  : '打开远程控制窗口'
            "
            @click="connectTo(a)"
          >
            {{ starting ? '连接中…' : sessionToResume(a.id) ? '接着看' : '连接' }}
          </button>

          <!--
            🔥 「结束会话」只在**这台机器真有一路还能看的会话**时出现（M7.14）。

            它的存在理由是「挂起」带出来的：挂起之后窗口没了、画面上没有任何按钮，
            要真想收掉这一路，原路只有"再点进去 → 点断开"—— 而那等于先建立一次
            4K 解码再来拆它。挂起本该是"收起来"，收起来之后却没有"丢掉"的口子，
            于是现场会习惯性地用「断开」代替「挂起」（因为断开不用再进去）。

            ⚠️ 与窗口里的「断开」是同一个动作（都结束会话，所有在看的人一起黑），
               只是**不需要先进窗口**。所以措辞也统一成"结束会话"，不另造一个词。
          -->
          <button
            v-if="sessionToResume(a.id)"
            class="sm"
            :disabled="starting"
            title="结束这一路会话（所有在看的人都会断）—— 窗口挂着、甚至没开过，都可以从这里收掉"
            @click="endSessionOf(a)"
          >
            结束会话
          </button>
        </div>
      </div>
    </div>

    <!-- ═══════════════════════════════════════════════════════════
         远控窗口（M7.13）

         🔥 **为什么用 Teleport 到 body**：这个窗口是 fixed 全屏遮罩，
            而它所在的 `.panel` 祖先里任何 `transform` / `filter` / `overflow`
            都会把 `position: fixed` 变成"相对那个祖先定位" —— 症状是窗口只盖住
            一小块、或者被面板的滚动区裁掉。Teleport 出去就没有这个问题。
         ═══════════════════════════════════════════════════════════ -->
    <Teleport to="body">
      <div v-if="windowOpen" class="rd-window">
        <header class="rd-win-bar">
          <div class="rd-win-title">
            <!--
              ⚠️ 用 `windowAgent`（窗口归属）而不是 `activeSession?.agentId`：
                 会话一断 `activeSession` 就没了，标题会退成「机器 #0」——
                 那看起来像"连到了另一台不存在的机器"，而其实只是这一路断了。
            -->
            <strong>{{ windowAgent === null ? '远程控制' : agentName(windowAgent) }}</strong>
            <span class="badge" :class="stateClass(activeSession?.state ?? '')">
              {{ stateLabel(activeSession?.state ?? '') }}
            </span>
            <span class="muted small">
              {{ codecLabel(activeSession?.codec ?? null) }}
              <template v-if="activeSession?.stats?.fps"> · {{ activeSession.stats.fps }} fps</template>
              <template v-if="frameWidth && frameHeight"> · {{ frameWidth }}×{{ frameHeight }}</template>
            </span>
            <span v-if="connectionState !== 'connected'" class="badge warn">
              实时通道{{ connectionState === 'connecting' ? '连接中' : '已断开' }}
            </span>
          </div>

          <div class="rd-win-actions">
            <!--
              ⚠️ 「挂起」「断开」**不是**一件事（M7.14 把原来最右边那个 `✕` 改成了它）：
                 · 挂起 = 只收窗口，会话留着（别人可能还在看，你也可以随时"接着看"）；
                 · 断开 = 结束这一路会话（所有看它的人一起黑）。
              合成一个按钮的话，一个人在多屏同看时点了挂起，别人的画面就没了。

              🔥 放在**最左边**是刻意的：最右端是"危险动作"的位置（断开），
                 而挂起是这几个按钮里**最安全**的一个（什么都不销毁）。
                 它原来是最右边一个 `✕` —— 那个符号的通用语义是"关掉/销毁"，
                 现场点了会以为会话被结束了，于是宁可去点「断开」。
            -->
            <button
              class="sm"
              title="挂起：收起窗口，这一路会话**保留**（可以随时回来「接着看」；要真结束用「断开」或列表里的「结束会话」）"
              @click="suspendWindow"
            >
              挂起
            </button>

            <button
              class="sm"
              :disabled="fullscreenBusy || !canFullscreen"
              :title="
                !fullscreenSupported
                  ? '这个浏览器（或这个页面环境）不支持全屏 —— 换 Edge / Chrome 打开本页'
                  : isFullscreen
                    ? '退出全屏（按 Esc 也可以）'
                    : '把画面放大到整块屏幕（只放大画面，参数面板不进全屏）'
              "
              @click="toggleFullscreen"
            >
              {{ isFullscreen ? '退出全屏' : '全屏' }}
            </button>

            <button class="sm" :class="{ primary: sheetOpen }" @click="sheetOpen = !sheetOpen">
              {{ sheetOpen ? '收起参数' : '参数面板' }}
            </button>

            <button
              class="sm"
              :disabled="starting"
              title="按上一次连接的参数重建这一路（画面会中断几秒）"
              @click="reconnectNow"
            >
              重连
            </button>

            <button class="sm danger" title="结束这一路会话（所有在看的人都会断）" @click="disconnectAndClose">
              断开
            </button>
          </div>
        </header>

        <!--
          ── 断线重连状态条 ──

          🔥 它必须说出**三件事**：为什么断、第几次、还有几秒。
          只说"重连中"的话，现场无法区分"它在努力"与"它卡住了"，
          而这两种情况的处置完全相反（等 vs 手动介入）。
        -->
        <div v-if="reconnect.state !== 'idle'" class="rd-reconnect" :class="reconnect.state">
          <div class="rd-reconnect-text">
            <template v-if="reconnect.state === 'waiting'">
              <strong>{{ reconnect.secsLeft }} 秒后自动重连</strong>
              （第 {{ reconnect.attempt }} / {{ RECONNECT_DELAYS.length }} 次）
            </template>
            <template v-else-if="reconnect.state === 'connecting'">
              <strong>正在重新连接…</strong>
            </template>
            <template v-else>
              <strong>自动重连已放弃</strong>
            </template>
            <span v-if="reconnect.reason"> —— {{ reconnect.reason }}</span>
          </div>

          <div class="rd-reconnect-actions">
            <button class="sm primary" :disabled="reconnect.state === 'connecting'" @click="reconnectNow">
              立即重连
            </button>
            <button class="sm" @click="disconnectAndClose">放弃并断开</button>
          </div>
        </div>

        <div class="rd-win-body">
          <!-- ── 画面列 ── -->
          <div class="rd-canvas-col">
            <!--
              ⚠️ 这几条提示**浮在画面上方**，不进参数面板：它们是"画面为什么不对"的答案
                 （纯色=可能息屏 / 解码尺寸对不上=只解出一角 / 编码分辨率偏小=被降采样），
                 藏进面板就等于没人在看画面时能得到它。
            -->
            <!--
              M7.6：画面几乎是一整片纯色时必须说出来。
              ⚠️ 它和上面那条"等待画面"不是一回事：这里**已经有画面了**，只是画面本身没有内容。
                 没有这一句，现场看到的就是一块黑 —— 而那正好是
                 「抓屏坏了 / 编码器坏了 / 那台机器真的息屏了」三种情况的共同外观。
            -->
            <p v-if="blankFrameNote" class="notice warn small">
              {{ blankFrameNote }}
            </p>

            <!--
              M7.7：解码尺寸 ≠ 该占区域时必须说出来。

              🔥 它和上面那条"纯色"是两个方向：纯色是"内容没有问题"，这一条是
                 **几何**出了问题 —— 现场报的"4K 只显示四分之一"就长这样。
                 没有这一句，用户只能看到"画面缩在左上角一小块"，而无法判断是
                 被控端编码时缩了、还是本机浏览器只解出了一角（下一步动作完全不同）。
            -->
            <p v-if="frameSizeNote" class="notice warn small">
              {{ frameSizeNote }}
            </p>

            <!--
              M7.8：被控端**编码前**就缩小了（MJPEG 的降采样）。

              🔥 与上面那条的区别：那条是"解码结果 < 这一帧该占的区域"（解不全），
                 这条是"整幅在码流里本来就小"（编之前就缩了）。降采样时
                 region ≡ decoded，上面那条一句话都不会说，而画面确实是糊的。
                 三屏拼接 5760×2160 降采样后每块屏只剩 1/3 宽 —— 现场只会看到
                 "合并显示很糊"，不知道正确的出路是"切到单块屏（走硬编 1:1）"。

                 ⚠️ M7.10 起降采样长边按画面自适应（1920–3840），三屏时每块屏
                    从 640 宽提到 1280 宽，**不再是固定的 1/3**。这条提示的判据
                    是"实际编出来的比屏幕小"，不依赖任何具体数字，所以不用改。
            -->
            <p v-if="encodingScaleNote" class="notice warn small">
              {{ encodingScaleNote }}
            </p>

            <div v-if="!activeSessionId" class="empty">
              <strong>当前没有画面</strong>
              <template v-if="reconnect.state !== 'idle'">正在重连，进度见上方状态条。</template>
              <template v-else>这一路已经断开 —— 点上方「重连」，或关掉窗口回列表重新连接。</template>
            </div>

            <template v-if="activeSessionId">
            <!--
              ⚠️ `1:1 原始像素` 必须真的给滚动条（需求②）：被控端分辨率可能远大于本机屏幕，
                 塞进视口就变成了"自动缩小" —— 那正是这个模式要避免的。
            -->
            <div
              ref="viewportRef"
              class="rd-viewport"
              :class="displayModeClass"
              @scroll.passive="scheduleViewportReport"
            >
              <!--
                🔥 事件绑在 canvas 上而不是外层容器上：`toNormalized` 要用 canvas 自己的
                   `getBoundingClientRect()` 才能同时吃下 `fit`（被 CSS 缩小）与
                   `actual`（1:1 + 滚动条）两种显示方式。

                ⚠️ `tabindex="0"` 是键盘能用的前提：不可聚焦的元素收不到 keydown/keyup。
                   代价是"必须先点一下画面"键盘才生效 —— 这一条在界面上明说了（那行提示）。
              -->
              <canvas
                ref="canvasRef"
                tabindex="0"
                :class="{ 'rd-input-on': canControl }"
                @mousemove="onPointerMove"
                @mousedown="onPointerDown"
                @mouseup="onPointerUp"
                @mouseleave="onPointerLeave"
                @contextmenu="onContextMenu"
                @wheel="onWheel"
                @keydown="onCanvasKeyDown"
                @keyup="onCanvasKeyUp"
                @focus="onCanvasFocus"
                @blur="onCanvasBlur"
              />

              <!--
                ⚠️ 没有画面的时候必须说清"在等什么"。`requested` 与"链路断了"是两件事，
                   而它们在页面上如果都表现为一块黑，现场就只能猜。
              -->
              <div v-if="!hasFrame" class="rd-waiting">
                <template v-if="activeSession?.state === REMOTE_SESSION_STATES.requested">
                  <strong>已请求，还没有画面</strong>
                  被控端正在处理。常见失败原因：那台机器<strong>没人登录</strong>（抓屏只能在有桌面的会话里做）、
                  或者托盘没在运行。超过 20 秒还没有任何反馈，中心会自动把它判成失败并给出原因。
                </template>
                <template v-else-if="activeSession?.state === REMOTE_SESSION_STATES.failed">
                  <strong>被控端说启动失败了</strong>
                  {{ activeSession?.endedReason ?? '（没有给出原因）' }}
                </template>
                <template v-else-if="activeSession?.state === REMOTE_SESSION_STATES.streaming">
                  <!--
                    🔥 **这一分支是本步（M5.4-c② 步 7）真正关掉的那个"猜"**：
                        原来这里只有一句"通常是链路问题"，而展厅里定格在一张图上的机器
                        完全正常 —— 它的桌面本来就没变，所以没有任何一帧需要发。
                        两句话指向的排查方向完全相反（查网络 vs 什么都不用做）。
                  -->
                  <template v-if="desktopIdleNotice">
                    <strong>被控端桌面静止中（{{ describeDesktopIdle(desktopIdleMs) }}）</strong>
                    这不是卡住、也不是链路问题 —— 那台机器上没人动过鼠标键盘，画面没有变化，
                    所以它一帧都不需要发。那边一动，画面立刻就会来。
                  </template>
                  <template v-else>
                    <strong>正在推流，但一帧都还没到</strong>
                    这通常是链路问题（Hub / 网络）。上面那行「中心收 / 被控端发」的对比能区分是链路掉了
                    还是被控端根本没在发。
                  </template>
                </template>
                <template v-else>等待画面…</template>
              </div>

              <!--
                M7.9：全屏时唯一看得见的"怎么退出去"。

                🔥 它**必须留在被全屏的那一块里面** —— 放在外面的话，一进全屏它就不见了，
                   而浏览器自己的退出方式是按 Esc（拿投影笔 / 触摸屏的人不知道这件事）。

                ⚠️ `pointer-events` 只给按钮：整条都吞点击的话，全屏画面顶上会多出一条
                   "点不动的边" —— 而画面顶上那一块很可能正是要被点的标题栏。
              -->
              <div v-if="isFullscreen" class="rd-fullscreen-hint">
                <span>全屏显示中 · 按 <kbd>Esc</kbd> 退出</span>
                <button class="sm" @click="toggleFullscreen">退出全屏</button>
              </div>
            </div>
            </template>

            <p v-if="viewerError" class="notice err small">{{ viewerError }}</p>
          </div>

          <!-- ══ 悬浮参数面板 ══ -->
          <aside v-if="sheetOpen" class="rd-sheet">
            <nav class="rd-sheet-tabs">
              <button
                v-for="t in SHEET_TABS"
                :key="t.key"
                class="sm"
                :class="{ primary: sheetTab === t.key }"
                @click="sheetTab = t.key"
              >
                {{ t.label }}
              </button>
            </nav>

            <div class="rd-sheet-body">
              <!-- ── 连接 ── -->
              <section v-show="sheetTab === 'connect'" class="rd-sheet-section">
                <label class="rd-field">
                  <span>抓哪块屏</span>
                  <select v-model="selectedScreenId" :disabled="screensLoading">
                    <option :value="null">自动（多屏时取全部拼接 · 只能软编）</option>
                    <option v-for="s in screens" :key="s.id" :value="s.id">
                      {{ s.name }} · {{ s.width }}×{{ s.height }}{{ s.isPrimary ? ' · 主屏' : '' }}
                      {{ s.isVirtualDesktop ? ' · 合成' : '' }}
                    </option>
                  </select>
                </label>

                <div class="rd-field-row">
                  <button class="sm" :disabled="screensLoading" @click="refreshScreens">
                    {{ screensLoading ? '读取中…' : '重读屏清单' }}
                  </button>
                  <!--
                    ⚠️ 「0 块」与「还在读」**必须分开说**：前者是"那台机器没人登录"
                       （要去那边开个会话），后者等一秒就好。混成一句"0 块可抓的屏"，
                       现场会直接去查显示器插没插。
                  -->
                  <span v-if="targetAgent" class="muted small">
                    <template v-if="screensLoading">正在读取…</template>
                    <template v-else-if="screensPending">还在路上（约 1 秒，会自动重读）</template>
                    <template v-else-if="screens.length === 0">
                      没有可抓的屏 —— 通常意味着<strong>那台机器没人登录</strong>
                    </template>
                    <template v-else>{{ screens.length }} 块可抓的屏</template>
                  </span>
                </div>

                <label class="rd-field">
                  <span>显示方式</span>
                  <select v-model="displayMode">
                    <option :value="REMOTE_DISPLAY_MODES.fit">等比缩放</option>
                    <option :value="REMOTE_DISPLAY_MODES.actual">1:1 原始像素</option>
                  </select>
                </label>

                <!--
                  ── 帧率（M7.16：它现在**真的说了算**）──

                  🔥 改名前叫「帧率上限」，而它当时确实只是个上限：服务端把它与档位的推荐帧率
                  **取更小**，默认档位是 `auto`（＝12 fps）⇒ 这里选 20 / 30 **都还是 12**，
                  而页面上没有任何一处说这件事。现场的唯一感受是"这个下拉框是坏的"。
                  现在请求值优先（`RemoteQualityProfiles.FoldTargetFps`），所以它叫「帧率」。

                  ⚠️ 但它仍然**只管帧率**：码率由「质量档」决定。60 fps 配「均衡」（6 Mbps）
                  每帧只有 100 kbps，会糊；要清晰得同时选「流畅」（25 Mbps）。
                  那句提示写在下面，不是装饰 —— 少了它，"选了 60 画面糊"会被当成 bug。
                -->
                <label class="rd-field">
                  <span>帧率</span>
                  <select v-model.number="maxFps">
                    <option :value="null">默认（跟档位）</option>
                    <option v-for="fps in FPS_CHOICES" :key="fps" :value="fps">{{ fps }}</option>
                  </select>
                </label>

                <!-- 生效值：把"你要的"与"真的下发的"并排给出来 -->
                <p class="muted small" style="margin: -4px 0 10px">
                  <template v-if="activeSession?.targetFps != null">
                    生效 <strong>{{ activeSession.targetFps }} fps</strong>
                    · 档位「{{ qualityLabel(activeQuality) }}」
                    <template v-if="maxFps !== null && maxFps !== activeSession.targetFps">
                      —— 你选的是 {{ maxFps }}，实际 {{ activeSession.targetFps }}：
                      <strong>被控端按自己的能力夹过</strong>（软件 JPEG 这条路上限 20 fps）
                    </template>
                  </template>
                  <template v-else>生效帧率要等会话起来才有读数</template>
                </p>

                <!--
                  60 帧的**前提**：软件 JPEG（没有硬编的机器 / 多屏拼接）这条路上限 ≈20 fps。
                  这一句只在用户真的选了 60 时出现 —— 无条件挂着会被当噪声（铁律 35）。
                -->
                <p v-if="(maxFps ?? 0) >= 60" class="muted small" style="margin: -4px 0 10px">
                  ⚠️ 60 帧要同时满足两条：<strong>被控端有硬编</strong>（H.264 / HEVC），
                  且<strong>档位给够码率</strong>（「流畅」＝25 Mbps）。走软件 JPEG 的机器会被如实夹到 20 fps。
                </p>

                <!--
                  🔥 **为什么不是一个"改了立刻生效"的下拉框**：抓屏与帧率是
                     **建会话那一刻**下发给被控端的参数，改了就必须重建那一会话
                     （画面会中断几秒，鼠标位置也要重新对齐）。做成"一变就重连"，
                     用户只想看看选项而已，画面却黑了 —— 那算 bug。
                  ⚠️ 而「显示方式」是**纯本机**的缩放，改了就生效，不需要按钮。
                -->
                <button class="sm primary rd-apply" :disabled="starting" @click="applyParams">
                  {{ starting ? '重建中…' : '按当前设置重建会话' }}
                </button>
                <p class="muted small">
                  改「抓哪块屏 / 帧率上限」要重建会话（画面中断几秒）；「显示方式」即时生效。
                </p>

                <p v-if="screenSwitchPending" class="notice warn small">
                  已选
                  「{{ screens.find((s) => s.id === selectedScreenId)?.name ?? selectedScreenId }}」，
                  与当前正在看的那块屏不同 —— 点上面那个按钮会重建会话。
                </p>

                <p class="muted small">
                  会话 <code class="mono">{{ activeSessionId ?? '—' }}</code>
                </p>
              </section>

              <!-- ── 画面 ── -->
              <section v-show="sheetTab === 'picture'" class="rd-sheet-section">
              <!--
                ── 质量档（M5.5）──

                🔥 四个档位就是服务端 `RemoteQualityProfiles` 认的那四个字符串，**不许自造**：
                写错一个字母服务端会回 400「不认得的质量档位」，而页面看不出是哪一侧写错了。

                ⚠️ 它与黑白模式一样是**会话级**的：编码只做一路，一个人切档所有人跟着变。
                而它比黑白模式更可能"看不见效果"（码率要等编码器重建才改），
                所以按钮旁边那句话不是装饰。
              -->
              <div class="rd-quality">
                <span class="muted small">质量档</span>

                <button
                  v-for="q in QUALITY_CHOICES"
                  :key="q.value"
                  class="sm"
                  :class="{ primary: activeQuality === q.value }"
                  :disabled="qualityBusy || activeSession?.state !== REMOTE_SESSION_STATES.streaming"
                  :title="q.hint"
                  @click="setQuality(q.value)"
                >
                  {{ q.label }}
                </button>

                <span class="badge">当前：{{ qualityLabel(activeQuality) }}</span>

                <span v-if="activeQuality === null" class="muted small">
                  （服务端没报这一格 —— 旧版中心；<strong>不代表</strong>它是「自动」）
                </span>
                <span v-else class="muted small">
                  切换后码率要等被控端<strong>重建编码器</strong>才真正改变
                </span>
              </div>

                <div class="rd-field-row">
                  <button class="sm" @click="activeSession && toggleGrayscale(activeSession)">
                    {{ activeSession?.grayscale ? '关掉黑白模式' : '开启黑白模式' }}
                  </button>
                </div>

                <div v-if="activeSession?.codecNote" class="muted small">
                  {{ activeSession.codecNote }}
                </div>

                <div v-if="activeSession?.codecNote" class="muted small">
                  {{ activeSession.codecNote }}
                </div>

              <!--
                ── 走了降级路径才说一句（M7.11）──

                🔥 与已删掉的那条常驻横幅是**同一条信息的两面**：那条对每一路都喊
                "画面是 MJPEG 脚手架"，包括一切正常的那一路；这一条只在
                `codec === 'mjpeg'` 时出现，说的是"这一路确实降了，出路在哪"。
                ⚠️ 硬编的会话**一个字都不会看到** —— 没有可解释的事就不要占地方。
              -->
              <p v-if="codecFallbackNote" class="notice warn small" style="margin-bottom: 10px">
                {{ codecFallbackNote }}
              </p>
              </section>

              <!-- ── 控制 ── -->
              <section v-show="sheetTab === 'control'" class="rd-sheet-section">
              <!--
                ── 输入控制（M5.2）──

                🔥 这一块的核心是**把两件事分开说清楚**：
                  · 中心闸门（一个开关，我们说了算）；
                  · 被控端通道（一个能力事实，只有它知道）。
                两个都为真才点得动。合成一句"可以控制"的话，
                出问题时没人知道该去点开关，还是去查那台机器上的客户端版本 ——
                而这两条路完全不搭界。
              -->
              <div class="rd-input">
                <button
                  class="sm"
                  :class="{ primary: !inputEnabled, danger: inputEnabled }"
                  :disabled="inputBusy || activeSession?.state !== REMOTE_SESSION_STATES.streaming"
                  @click="toggleInput"
                >
                  {{ inputEnabled ? '关闭控制（键鼠）' : '允许控制（键鼠）' }}
                </button>

                <span class="badge" :class="inputEnabled ? 'ok' : ''">
                  中心闸门：{{ inputEnabled ? '已开' : '已关' }}
                </span>

                <span class="badge" :class="inputStateClass">{{ inputStateText }}</span>
              </div>

              <!--
                🔥 **"会话级"这件事必须在闸门开着的时候说** —— 关着的时候没有任何风险，
                   而一旦打开，所有正在看这一路的人（**包括不是你的那个**）都能操作那台机器。

                ⚠️ 这里说的是**语义**，不是人数：本方案刻意**不做**"当前有几个人在看"的统计
                   （那要中心维护一张"谁在看哪一路"的表，而有表就有漏水路径 ——
                   见 RemoteHub 的类注释）。所以这句话是让现场**自己约定**谁上手，
                   而不是让界面替他们排队 —— 想排队就得先有独占控制权，那是另一件事。
              -->
              <p v-if="inputEnabled" class="rd-hint muted small">
                ⚠️ 这个开关是<strong>会话级</strong>的：此刻正在看这一路的人（可能不止你一个）
                <strong>都能操作那台机器</strong>，中心不区分谁在控。多人同看时请先约定谁上手。
              </p>

              <!--
                ⚠️ 被控端说"不可用"时，**原因必须显示出来**：
                    "客户端版本过旧"要去升级客户端，"这台机器本机禁止了远程输入"只要删一个文件 ——
                    只显示"不可用"的话，这两种情况的处置完全相反却长得一样。
              -->
              <p v-if="activeSession?.inputReason" class="rd-hint muted small">
                被控端接不住输入的原因：{{ activeSession.inputReason }}
              </p>

              <p v-if="inputEnabled && !canControl" class="rd-hint muted small">
                ⚠️ <strong>控制开关已打开，但现在还点不动</strong> —— 上一条说的那个原因还没解决。
                这一路仍然只能看。
              </p>

              <p v-if="canControl" class="rd-hint muted small">
                <template v-if="canvasFocused">
                  键盘已生效。⚠️ 在画面上按键<strong>会优先给被控机</strong>（浏览器自己的快捷键在画面有焦点时失效）
                  —— 点一下画面外即可恢复。
                </template>
                <template v-else>鼠标已可操作；<strong>点一下画面</strong>让键盘也生效。</template>
              </p>

              <p v-if="inputAck && !inputAck.ok" class="notice err small">
                {{ describeInputFailure(inputAck) }}
              </p>

              <!--
                ── 剪贴板（M5.5）──

                🔥 **两个方向必须是两个按钮**：
                  · 「读取被控机的」= 把那台机器的剪贴板拿到本机；
                  · 「把下面这段发过去」= 把本机这段文字写进**别人的机器**（后端 AdminOnly）。
                合成一个"同步"按钮的话，用户永远不知道自己按下之后方向是哪一边 ——
                而这两个方向的后果完全不同（一个只是看，一个会改别人的剪贴板）。

                ⚠️ 坑位提示：读到的那一段**可能是上一次的旧内容**（服务端先把缓存给你，
                这次的文本随后经 `ClipboardChanged` 推回来覆盖它）。
              -->
              <div class="rd-clipboard">
                <div class="rd-clipboard-head">
                  <strong>剪贴板</strong>

                  <button
                    class="sm"
                    :disabled="clipboardBusy || activeSession?.state !== REMOTE_SESSION_STATES.streaming"
                    @click="readClipboard"
                  >
                    {{ clipboardBusy ? '处理中…' : '读取被控机的' }}
                  </button>

                  <button
                    class="sm primary"
                    :disabled="
                      clipboardBusy
                      || clipboardDraft.length === 0
                      || activeSession?.state !== REMOTE_SESSION_STATES.streaming
                    "
                    @click="sendClipboard"
                  >
                    把下面这段发过去
                  </button>

                  <span v-if="clipboard?.truncated" class="badge warn">已按 2 万字截断</span>
                </div>

                <textarea
                  v-model="clipboardDraft"
                  class="rd-clipboard-draft"
                  rows="2"
                  placeholder="要塞进被控机剪贴板的文字（Ctrl+Enter 直接发送）"
                  @keydown.ctrl.enter.prevent="sendClipboard"
                />

                <!--
                  🔥 **三态不许合并**：
                    · `text === null` 且没有 `note` ⇒ 那台机器的剪贴板里**没有文本**（图片/空/被占用），
                      这不是故障、不需要处置；
                    · `text === null` 且有 `note` ⇒ **读失败**，`note` 就是原因；
                    · 有 `text` ⇒ 正常。
                  把前两者画成同一句"读取失败"，会让人去查一个根本没坏的东西。
                -->
                <div class="muted small">
                  <!--
                    🔴 判"还没读过"用 **`!clipboard`**，不是 `clipboard === null`：
                       `undefined` 也是"还没读过"，而 `=== null` 会把它漏到下面那个分支去，
                       于是 `clipboard.text` 在渲染函数里抛异常 —— 整个面板一片空白
                       （2026-10-18 浏览器实证踩到，根因是服务端 204 ⇒ `unwrap` 给 `undefined`）。
                  -->
                  <template v-if="!clipboard">还没读过被控机的剪贴板。</template>

                  <template v-else-if="clipboard.text === null && clipboard.note">
                    读取失败：{{ clipboard.note }}
                  </template>

                  <template v-else-if="clipboard.text === null">
                    被控机的剪贴板里<strong>没有文本</strong>
                    （可能是图片、空的，或被别的程序占用着）—— 这不是故障。
                  </template>

                  <template v-else>
                    读到 <strong>{{ clipboard.text.length }}</strong> 字
                    <template v-if="clipboard.truncated">
                      （原文超过上限，只拿到前 {{ REMOTE_CLIPBOARD_MAX_CHARS }} 字）
                    </template>
                    <template v-if="clipboard.at"> ｜ {{ fmtTimeOnly(clipboard.at) }}</template>
                    <button class="sm" style="margin-left: 8px" @click="copyClipboardToLocal">
                      复制到本机
                    </button>
                  </template>
                </div>

                <pre v-if="clipboard?.text" class="rd-clipboard-view">{{ clipboard.text }}</pre>
              </div>
              </section>

              <!-- ── 文件（M8.1）── -->
              <section v-show="sheetTab === 'files'" class="rd-sheet-section">
                <RemoteFilesPanel
                  :agent-id="windowAgentId"
                  :agent-name="filesAgentName"
                  :online="filesAgentOnline"
                  :active="sheetTab === 'files'"
                />
              </section>

              <!-- ── 声音 ── -->
              <section v-show="sheetTab === 'audio'" class="rd-sheet-section">
                <p v-if="!isSecureContext" class="notice warn small">
                  这个页面不在安全上下文里 ⇒ 没有声音。
                  浏览器的 <code>AudioDecoder</code> / <code>AudioWorklet</code> 在
                  <code>http://</code> 的非本机地址下<strong>不存在</strong>；
                  画面会降级到 MJPEG（看得见，但达不到硬编）。
                  <template v-if="trustInfo?.httpsEnabled && httpsAddress">
                    换 <a :href="httpsAddress"><code>{{ httpsAddress }}</code></a> 重开本页。
                  </template>
                </p>

              <!-- ── 声音（M5.3）── -->
              <div class="rd-audio">
                <div class="rd-audio-head">
                  <span class="muted small">声音</span>
                  <strong :class="audioStateClass">{{ audioStateLabel }}</strong>
                  <button class="sm" :disabled="audioUi === 'unavailable'" @click="toggleAudio">
                    {{ audioMuted ? '取消静音' : '静音（仅本机）' }}
                  </button>
                </div>

                <!--
                  ⚠️ 两种"没有声音"**必须分开显示**，它们长得一样、处置完全相反：
                     本机这条 → 看 https / 看浏览器；被控端那条 → 去那台机器查声卡。
                     ⚠️ 而且它们都**不许**用「收场原因」那个字段 —— 后者本义是
                        "这路会话为什么结束"，一条正常结束的会话也会带着它。
                        两者共用时，页面上会写着「没有声音：已在本机取消推流」，
                        而真正的原因被顶掉了。
                -->
                <p v-if="audioUi === 'unavailable'" class="notice warn small">
                  本机放不出声：{{ audioMessage }}
                </p>

                <p
                  v-else-if="activeSession?.audioState === AGENT_AUDIO_STATES.unavailable"
                  class="notice warn small"
                >
                  被控端没有音频：{{ activeSession?.audioReason ?? '（它没有给出原因）' }}
                </p>

                <div class="rd-stats">
                  <div class="rd-stat">
                    <span class="muted small">音画偏差</span>
                    <strong :class="{ warn: avOffsetMs !== null && Math.abs(avOffsetMs) > 120 }">
                      {{ avOffsetText }}
                    </strong>
                  </div>

                  <!--
                    🔥 「中心收 / 被控端发」这两个音频计数**必须并排**，与视频那条同一个理由：
                       只给一个时，"链路把音频丢了"与"被控端压根没采到音频"长得一模一样，
                       而处置相反（一个查网络，一个查那台机器的声卡）。
                  -->
                  <div class="rd-stat">
                    <span class="muted small">音频段（中心收 / 被控端发）</span>
                    <strong :class="{ warn: (audioLostInTransit ?? 0) > 0 }">
                      {{ activeSession?.stats?.audioFramesReceived ?? 0 }}
                      /
                      {{ activeSession?.agentStats?.audioFramesSent ?? '—' }}
                    </strong>
                  </div>

                  <div class="rd-stat">
                    <span class="muted small">本机已排入播放</span>
                    <strong>{{ audioStats?.scheduled ?? 0 }} 段</strong>
                  </div>

                  <div class="rd-stat">
                    <span class="muted small">音频时延</span>
                    <strong>{{ audioLatencyMs === null ? '—' : `${audioLatencyMs} ms` }}</strong>
                  </div>

                  <div class="rd-stat">
                    <span class="muted small">音频解码失败 / 缓冲饿死</span>
                    <strong
                      :class="{
                        warn: (audioStats?.decodeErrors ?? 0) + (audioStats?.underruns ?? 0) > 0,
                      }"
                    >
                      {{ audioStats?.decodeErrors ?? 0 }} / {{ audioStats?.underruns ?? 0 }}
                    </strong>
                  </div>

                  <div class="rd-stat">
                    <span class="muted small">音频格式</span>
                    <strong>
                      {{
                        audioStats && audioStats.sampleRate > 0
                          ? `${audioStats.sampleRate} Hz · ${audioStats.channels} ch`
                          : '—'
                      }}
                    </strong>
                  </div>

                  <div class="rd-stat">
                    <span class="muted small">画面缓冲（排队 / 挤出 / 过期 / 未上屏 / 未收到）</span>
                    <strong
                      :class="{
                        warn:
                          (videoStats?.overflow ?? 0) +
                            (videoStats?.stale ?? 0) +
                            (videoStats?.gaps ?? 0) >
                          0,
                      }"
                    >
                      {{ videoStats?.buffered ?? 0 }} / {{ videoStats?.overflow ?? 0 }} /
                      {{ videoStats?.stale ?? 0 }} / {{ videoStats?.skipped ?? 0 }} /
                      {{ videoStats?.gaps ?? 0 }}
                    </strong>
                  </div>

                  <!--
                    🔥 这一格与上面那格**刻意分开**（M5.4-b 第 8 步），不是排版问题：

                    上面那格说的是"**排期**这一侧丢了几帧" —— 画面会跳一下，但**不花**；
                    这一格说的是"**解码链**断了几次" —— 画面会花到下一个关键帧。
                    合成一格，现场就会把"帧率被降下来"读成"解码故障"，于是去查编码参数。

                    「未上屏」是 H.264 那条路的常态（"不丢帧、只丢上屏"的代价）。

                    🔥「未收到」与前四个**不同源**（M5.4-c① 步 4）：前四个都是"**我这边**跟不上"，
                    只有它说的是"**帧压根没到我这儿**" —— 而序号既然断了，那几帧就只可能是
                    离开被控端之后丢的（被控端自己丢帧不占序号）⇒ **它一涨就去查中心，别在本机找原因**。
                  -->
                  <div class="rd-stat">
                    <span class="muted small">解码器（产出 / 解不出 / 重建）</span>
                    <strong :class="{ warn: (videoDecoderStats?.resets ?? 0) > 0 }">
                      {{ videoDecoderStats?.decoded ?? 0 }} / {{ videoStats?.decodeErrors ?? 0 }} /
                      {{ videoDecoderStats?.resets ?? 0 }}
                    </strong>
                  </div>

                  <div class="rd-stat">
                    <span class="muted small">目标播放延迟</span>
                    <strong>{{ playbackLatencyMs }} ms</strong>
                  </div>
                </div>

                <p class="muted small">
                  ⚠️ 「音画偏差」是<strong>测</strong>出来的，不是按公式算的：比的是"实际上屏的那一帧是哪一刻采的"
                  与"实际正在响的那一段是哪一刻采的"，两边各带上自己的解码与排队耗时 ——
                  所以一个恒等于 0 的读数说明测量写错了，**不是**说明做得好。
                  <br />
                  画面与声音按**同一个**目标延迟排期（{{ playbackLatencyMs }} ms ——
                  按目标帧率买<b>两帧</b>的余量，夹在 20–60 ms；帧率越低这个数越接近 60），
                  这是它们能对齐的唯一依据。
                  <template v-if="!clockReady">
                    <br />
                    <strong>当前无法对齐</strong>：被控端没有上报会话单调时刻（旧版客户端），
                    排期显示已退化成"收到就画" —— 画面仍然能看，但音画偏差没有意义。
                  </template>
                </p>
              </div>
              </section>

              <!-- ── 诊断 ── -->
              <section v-show="sheetTab === 'diag'" class="rd-sheet-section">
              <!-- ── 实时数字 ── -->
              <div class="rd-stats">
                <!--
                  M7.8：降采样时这一格是**编码分辨率**，不是屏幕分辨率。

                  🔥 直说"画面分辨率 1920×720"而屏幕其实是 5760×2160，会让人以为
                     "被控端分辨率就这么低" —— 而事实是它被主动压小了。
                     改成"编码分辨率"＋标黄，配合下面那句提示，两个数一起给出来。
                -->
                <div class="rd-stat">
                  <span class="muted small">{{ encodingScaleNote ? '编码分辨率' : '画面分辨率' }}</span>
                  <strong :class="{ warn: encodingScaleNote !== null }">
                    {{ frameWidth && frameHeight ? `${frameWidth} × ${frameHeight}` : '—' }}
                  </strong>
                </div>

                <!--
                  M7.7：**解码尺寸**与上一格对不上，就是"4K 只显示四分之一"的那个现场。
                  🔥 必须单独一格：它是唯一能把"编码端缩了"与"本机解不全"分开的读数。
                  ⚠️ `—` 是"还没有一帧解出来"，不是 0。
                -->
                <div class="rd-stat">
                  <span class="muted small">解码尺寸</span>
                  <strong :class="{ warn: frameSizeNote !== null }">
                    {{ decodedSize ? `${decodedSize.width} × ${decodedSize.height}` : '—' }}
                  </strong>
                </div>

                <div class="rd-stat">
                  <span class="muted small">中心实测帧率</span>
                  <strong>{{ activeSession?.stats ? `${activeSession.stats.fps} fps` : '—' }}</strong>
                </div>

                <div class="rd-stat">
                  <span class="muted small">被控端实测帧率</span>
                  <strong>{{ activeSession?.agentStats ? `${activeSession.agentStats.fps} fps` : '—' }}</strong>
                </div>

                <!--
                  「桌面静止」这一格与"帧率"是**两个方向**的读数：帧率掉到 0 既可能是
                  "桌面没动"也可能是"编不出来"，而这一格只答前一种。
                  ⚠️ `—` 是"这台机器不提这件事"（旧客户端），不是"0 秒"。
                -->
                <div class="rd-stat">
                  <span class="muted small">桌面静止</span>
                  <strong>{{ describeDesktopIdle(desktopIdleMs) }}</strong>
                </div>

                <div class="rd-stat">
                  <span class="muted small">本机已画</span>
                  <strong>{{ paintedFrames }} 帧</strong>
                </div>

                <div class="rd-stat">
                  <span class="muted small">单帧大小</span>
                  <strong>
                    {{ activeSession?.stats ? fmtBytes(activeSession.stats.lastFrameBytes) : '—' }}
                  </strong>
                </div>

                <div class="rd-stat">
                  <span class="muted small">累计码流</span>
                  <strong>
                    {{ activeSession?.stats ? fmtBytes(activeSession.stats.bytesReceived) : '—' }}
                  </strong>
                </div>

                <div class="rd-stat">
                  <span class="muted small">被丢弃的帧</span>
                  <strong :class="{ warn: (activeSession?.stats?.droppedFrames ?? 0) > 0 }">
                    {{ activeSession?.stats?.droppedFrames ?? 0 }}
                  </strong>
                </div>

                <div class="rd-stat">
                  <span class="muted small">帧时延</span>
                  <strong v-if="latencySuspect" class="warn">时钟未同步</strong>
                  <strong v-else>{{ latencyMs === null ? '—' : `${latencyMs} ms` }}</strong>
                </div>

                <!--
                  ⚠️ 输入这一类数字**只在控制开着时显示**：它是一组"排障用"的计数，
                     而平时它只会占位置，让人以为"这一页跟输入有关"。
                -->
                <div v-if="inputEnabled" class="rd-stat">
                  <span class="muted small">已送出输入</span>
                  <strong>{{ inputSentEvents }} 条</strong>
                </div>

                <div v-if="inputEnabled" class="rd-stat">
                  <span class="muted small">本机合并的移动</span>
                  <strong>{{ inputMergedLocally }} 条</strong>
                </div>
              </div>

              <p class="rd-diagnosis small" :class="{ warn: (framesLostInTransit ?? 0) > 0 }">
                {{ activeSession ? frameDiagnosis(activeSession) : '' }}
              </p>

              <p v-if="activeSession?.endedReason" class="notice warn small">
                收场原因：{{ activeSession.endedReason }}
              </p>

              <p class="muted small">
                ⚠️ 「帧时延」含<strong>跨机时钟差</strong>（它是"本机此刻 − 被控端抓屏时刻"），
                所以只有在两台机器都完成 NTP 校准时才有意义；取不到合理值时显示「时钟未同步」而不是硬报一个数。
              </p>

                <p v-if="targetAgent" class="rd-hint muted small">
                  <template v-if="capabilityLoading">
                    正在读取「{{ targetAgent.displayName }}」的编码能力…
                  </template>
                  <template v-else-if="!capability">读不到该机器的编码能力（不影响观看）。</template>
                  <template v-else-if="!capability.probed">
                    这台机器<strong>尚未上报</strong>编码能力 —— 注意这<strong>不等于</strong>它不支持远程桌面。
                  </template>
                  <template v-else>
                    编码能力：{{ capability.summary }}
                  </template>
                </p>

                <p v-if="targetAgent" class="rd-hint muted small">
                  本页会声明：{{ viewerVideoDeclaration }}
                  <span v-if="viewerProbe">—— {{ viewerProbe.summary }}</span>
                </p>

                <p v-if="screensNote" class="rd-hint muted small">{{ screensNote }}</p>
              </section>
            </div>
          </aside>
        </div>
      </div>
    </Teleport>

  </section>
</template>

<style scoped>
/* ═══════════════════════════════════════════════════════════
   机器列表（M7.13／M7.14）
   🔥 一行一台，右侧是它的动作（主按钮「连接/接着看」＋**有会话时**才有「结束会话」）。
   刻意**不做**成表格：表格的每一列都会诱使人往里加"顺便显示"的读数，
   而这一页的职责就是"选一台连上去"，读数属于窗口内的诊断页。
   ═══════════════════════════════════════════════════════════ */

/* 提示条在窗口/面板里都要有下边距（原来它是挂在页面上的，随块搬迁一起保留）。 */
.rd-hint {
  margin: 0 0 10px;
}

.rd-machine-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}.rd-machine-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  border: 1px solid #e6e8ec;
  border-radius: 6px;
  background: #fff;
}

/* 正在这台机器上看画面 —— 回到列表时一眼能找到"刚才那台"。 */
.rd-machine-row.is-watching {
  border-color: #2f7cf6;
  box-shadow: inset 0 0 0 1px rgba(47, 124, 246, 0.3);
}

.rd-machine-main {
  min-width: 0;
}

.rd-machine-name {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  margin-bottom: 2px;
}

.rd-machine-actions {
  flex: 0 0 auto;

  /* M7.14：有会话时这里是两个按钮（接着看 / 结束会话），不能挤成一坨。 */
  display: flex;
  align-items: center;
  gap: 8px;
}

/* ═══════════════════════════════════════════════════════════
   远控窗口（M7.13）

   ⚠️ 窗口**用浅色**，只有画面区是黑的。理由不是审美：
      `notice` / `badge` / `muted` / `code` 这些是全局浅色类，
      把窗口做成深色就得在这里重写十几条颜色覆盖 —— 而漏掉一条的表现是
      "关键提示变成深底深字"，恰好把要看的排障信息藏起来。
   ═══════════════════════════════════════════════════════════ */

.rd-window {
  position: fixed;
  inset: 0;
  z-index: 900;
  display: flex;
  flex-direction: column;
  background: #f5f6f8;
}

.rd-win-bar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
  padding: 8px 12px;
  background: #fff;
  border-bottom: 1px solid #e6e8ec;
}

.rd-win-title {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  min-width: 0;
}

.rd-win-actions {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.rd-win-body {
  flex: 1;
  min-height: 0;
  display: flex;
}

.rd-canvas-col {
  flex: 1;
  min-width: 0;
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px;
  overflow: auto;
}

/* 画面在窗口里要吃掉剩余高度（页面里的 `max-height: 62vh` 在这里反而浪费空间）。 */
.rd-window .rd-viewport {
  flex: 1;
  min-height: 200px;
}

.rd-window .rd-viewport.fit canvas {
  max-height: 100%;
}

.rd-window .rd-viewport.actual {
  max-height: none;
  min-height: 0;
}

/* ── 悬浮参数面板 ── */

.rd-sheet {
  width: 380px;
  flex: 0 0 380px;
  display: flex;
  flex-direction: column;
  min-height: 0;
  background: #fff;
  border-left: 1px solid #e6e8ec;
}

.rd-sheet-tabs {
  display: flex;
  gap: 4px;
  flex-wrap: wrap;
  padding: 8px;
  border-bottom: 1px solid #eef0f3;
}

.rd-sheet-body {
  flex: 1;
  overflow: auto;
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.rd-sheet-section {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.rd-field {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 12px;
  color: var(--muted);
}

.rd-field select {
  padding: 5px 6px;
  border: 1px solid #d6dae2;
  border-radius: 4px;
  background: #fff;
  color: inherit;
  font-size: 13px;
}

.rd-field-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.rd-apply {
  align-self: flex-start;
}

/* ── 断线重连状态条 ──
   ⚠️ 三种状态**三种底色**：一眼就能分辨"等着"与"已经放弃"。
      都用同一块灰色的话，用户只会看到"一直有个条"，读不出进展。 */

.rd-reconnect {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  padding: 8px 12px;
}

.rd-reconnect.waiting {
  background: #fff7e6;
  border-bottom: 1px solid #ffe0a3;
}

.rd-reconnect.connecting {
  background: #eef4ff;
  border-bottom: 1px solid #cfe0ff;
}

.rd-reconnect.failed {
  background: #fdeeee;
  border-bottom: 1px solid #f5c2c2;
}

.rd-reconnect-text {
  font-size: 13px;
}

.rd-reconnect-actions {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

/* 窄屏：面板改成从下方拉出，画面仍然占满宽度。 */
@media (max-width: 900px) {
  .rd-win-body {
    flex-direction: column;
  }

  .rd-sheet {
    width: auto;
    flex: 0 0 55vh;
    border-left: none;
    border-top: 1px solid #e6e8ec;
  }
}


/*
 * ⚠️ `fit` 与 `actual` 的区别就是这一段的全部意义：
 *   · fit     —— canvas 受限于容器宽度（等比缩小）；
 *   · actual  —— canvas **保持原始像素**，容器出滚动条（需求②：被控端可能远大于本机屏幕）。
 */
.rd-viewport {
  position: relative;
  background: #101114;
  border-radius: 4px;
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 220px;
}

.rd-viewport.fit {
  padding: 8px;
}

.rd-viewport.fit canvas {
  max-width: 100%;
  max-height: 62vh;
  height: auto;
  object-fit: contain;
}

.rd-viewport.actual {
  overflow: auto;
  max-height: 62vh;
  display: block;
}

.rd-viewport.actual canvas {
  display: block;
  max-width: none;
  max-height: none;
}

/*
 * M7.9：全屏时这块就是整块屏幕。
 *
 * 🔥 两条都必要：
 *   · `fit`      —— 把 62vh 的上限换成 100vh，否则画面在全屏里还是被压到 62% 高；
 *   · `actual`   —— 去掉 62vh 的高度上限，1:1 时靠滚动看全（"原始像素"不能因为全屏就变成缩小）。
 * ⚠️ 背景走纯黑而不是 #101114：全屏时这一块紧贴屏幕边缘，肉眼能看出那点灰。
 */
.rd-viewport:fullscreen {
  width: 100vw;
  height: 100vh;
  max-height: none;
  min-height: 0;
  padding: 0;
  border-radius: 0;
  background: #000;
}

.rd-viewport:fullscreen.fit canvas {
  max-width: 100vw;
  max-height: 100vh;
}

.rd-viewport:fullscreen.actual {
  height: 100vh;
  max-height: none;
  overflow: auto;
}

/* 全屏提示条：位置在右上角，`pointer-events` 只给按钮（见模板里的说明）。 */
.rd-fullscreen-hint {
  position: absolute;
  top: 10px;
  right: 10px;
  z-index: 3;
  display: flex;
  gap: 8px;
  align-items: center;
  padding: 4px 8px;
  border-radius: 4px;
  background: rgba(16, 17, 20, 0.72);
  color: #eef0f4;
  font-size: 12px;
  pointer-events: none;
}

.rd-fullscreen-hint button {
  pointer-events: auto;
}

.rd-fullscreen-hint kbd {
  padding: 0 4px;
  border: 1px solid #5a5f6b;
  border-radius: 3px;
  font-family: inherit;
  font-size: 11px;
}

.rd-waiting {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 16px 24px;
  text-align: center;
  color: #c9ccd3;
  font-size: 12px;
  line-height: 1.7;
  background: rgba(16, 17, 20, 0.92);
}

.rd-waiting strong {
  color: #fff;
  font-size: 13px;
}

.rd-stats {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
  gap: 8px;
  margin-top: 10px;
}

.rd-stat {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 6px 8px;
  background: #fff;
  border: 1px solid #ececf0;
  border-radius: 4px;
}

/* ── 声音那一块（M5.3）──
   刻意**不做成折叠**：与输入控制同一个理由 —— "现在到底有没有声音、
   没有的话该去动哪一边"是这一页最要紧的两句话之一。*/
.rd-audio {
  margin-top: 12px;
  padding: 8px 10px;
  background: #fbfbfd;
  border: 1px solid #ececf0;
  border-radius: 4px;
}

.rd-audio-head {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  align-items: center;
}

.rd-audio-head .sm {
  margin-left: auto;
}

.rd-diagnosis {
  margin: 10px 0 0;
  padding: 6px 8px;
  background: #fff;
  border: 1px solid #ececf0;
  border-radius: 4px;
}

/* ── 输入控制那一行（M5.2）──
   一行里并排三件东西：开关按钮、两道闸门各自的状态。刻意**不做成折叠**：
   "现在这台机器能不能被控制"是这一页最要紧的一句话。

   质量档（M5.5）同理横排：`flex-wrap` 让旁边那句说明在窄屏上掉到第二行，
   而不是把按钮挤扁。*/
.rd-input,
.rd-quality {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
  margin-top: 10px;
  padding: 6px 8px;
  background: #fff;
  border: 1px solid #ececf0;
  border-radius: 4px;
}

/*
  ⚠️ 剪贴板**不能**复用 `.rd-input` 的行内布局：它里面有一个 textarea 和一段可能很长的
  内容块，横排会把输入框压到几十像素宽。
*/
.rd-clipboard {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-top: 10px;
  padding: 8px;
  background: #fff;
  border: 1px solid #ececf0;
  border-radius: 4px;
}

.rd-clipboard-head {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
}

.rd-clipboard-draft {
  width: 100%;
  box-sizing: border-box;
  padding: 6px;
  font-family: inherit;
  font-size: 12px;
  border: 1px solid #dcdce4;
  border-radius: 4px;
  resize: vertical;
}

/*
  ⚠️ `white-space: pre-wrap` 与 `word-break` **必须写**：读到的是任意文本，
  少写一条的表现是"一段长代码只能横向滚、看不到右边"，而那看起来像内容被截断了。
*/
.rd-clipboard-view {
  margin: 0;
  max-height: 140px;
  overflow: auto;
  padding: 6px;
  background: #f7f7fa;
  border: 1px solid #ececf0;
  border-radius: 4px;
  font-size: 12px;
  white-space: pre-wrap;
  word-break: break-all;
}

/* 拿到控制权时给画面一个明确的"这里是活的"信号 —— 否则点了没反应时
   分不清是"没送到"还是"这一块本来就不接收鼠标"。*/
.rd-viewport canvas.rd-input-on {
  cursor: crosshair;
}

.rd-viewport canvas:focus {
  outline: 2px solid var(--accent, #2f6feb);
  outline-offset: 1px;
}
</style>
