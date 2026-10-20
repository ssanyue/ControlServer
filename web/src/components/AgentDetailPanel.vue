<script setup lang="ts">
/**
 * 单台客户机的详情（在线表里展开的那一行）。
 *
 * M4.2 起这一块回答的问题从"它是谁"扩到"它现在怎么样、历史上怎么样"：
 *   · **实时**：最新一帧指标（列表里的实时列是同一个来源，这里是完整版）；
 *   · **历史**：CPU/内存、磁盘与网络、GPU 三张时序图；
 *   · **开关机**：开机 / 关机 / 重启 / 意外关机 / 蓝屏的逐条记录 + 统计；
 *   · **硬件清单**：CPU / 内存条 / 分区 / 物理磁盘 / 显卡（6 小时刷新一次）。
 *
 * M4.5 又加了一块「任务配置」（见 `AgentTaskConfigPanel`）：这台机器**被要求干哪些事**
 * （延时/循环发送、定时发送、延时启动程序、UDP 指令通道 + 配置版本对照）。
 * 它自成一体（自己的加载/保存/校验回显），所以拆成独立组件挂在这里，而不是继续往本文件里堆。
 *
 * ⚠️ 两块内容刻意**不合并**，因为它们的数据新鲜度差了几个数量级：
 *    指标 5 秒一帧、清单 6 小时一帧。把清单和实时数字并排放在一起，
 *    会让人以为"内存条数变了"也能立刻看到 —— 那要等下一个 6 小时。
 *
 * ⚠️ 所有数值一律可能为 null（"采不到"），显示成 `—`。
 *    这和"0"是两件完全不同的事，本文件里任何地方都不做 `?? 0`。
 */
import { computed, ref, watch } from 'vue'
import { api } from '../api'
import {
  identifyPhysicalDevices,
  pickReading,
  type PhysicalDevice,
} from '../deviceIdentity'
import type {
  AgentDetailDto,
  AgentDeviceKind,
  AgentDeviceReadingDto,
  AgentHostDto,
  AgentInventoryEnvelopeDto,
  AgentListDto,
  AgentMetricsPointDto,
  AgentMetricsSeriesDto,
  AgentPowerEventListDto,
  AgentReceivedDataDto,
  ChartSeries,
} from '../types'
import {
  AGENT_DEVICE_KINDS,
  AGENT_DEVICE_KIND_LABELS,
  AGENT_RECEIVED_KINDS,
  AGENT_RECEIVED_OUTCOMES,
  isProblemEvent,
  POWER_EVENT_LABELS,
  THERMAL_STATES,
  thermalStateLabel,
} from '../types'
import {
  beijingIso,
  fmtDateTime,
  fmtMb,
  fmtPercent,
  fmtRate,
  fmtUptimeHuman,
} from '../format'
import MetricsChart from './MetricsChart.vue'
import AgentTaskConfigPanel from './AgentTaskConfigPanel.vue'

const props = defineProps<{
  host: AgentHostDto
  isAdmin: boolean
}>()

const emit = defineEmits<{
  (e: 'saved', result: { ok: boolean; message: string; list: AgentListDto | null }): void
}>()

/**
 * 「任务配置」区块只带得动一句话的结论，这里把它接到本组件与父级既有的
 * `saved` 通道上（父级拿到 `list: null` 就只弹提示、不刷新列表 —— 配置变更不影响列表）。
 *
 * ⚠️ `err` 会被父级降级成一条 warn 提示（那条通道只有 ok/warn 两档）。
 *    失败详情本来就更适合就地显示在那个区块里（问题清单可能很长），这里只是"另开一扇窗"。
 */
function onConfigNotice(kind: 'ok' | 'err' | 'warn', text: string) {
  emit('saved', { ok: kind !== 'err', message: text, list: null })
}

const detail = ref<AgentDetailDto | null>(null)
const loading = ref(false)
const error = ref('')

const saving = ref(false)

// ── 远程指令（经 UDP 指令通道，M4.6）──
//
// 这条路径与"走 SignalR 的重启 Agent 服务"完全不同：它由服务端直接向客户机的 UDP
// 通道端口发一条**明文报文**，**不要求机器在线**（只要求网通 + 那台机器开着通道）。
//
// ⚠️ 2026-10-13 起报文不再签名（原来那套"客户机上报通道密钥、中心拿它签名"已整体删除）：
//    所以这里不再有"中心还没拿到密钥"这个失败态 —— 能拦住的只剩"网不通 / 没地址 / 通道没启用"。
const udpBusy = ref(false)

const udpResult = ref<{ kind: 'ok' | 'err'; text: string } | null>(null)

/**
 * 可下发的指令。**线路上发的是明文短码**（与用户/现场手工发的一致）：
 * `DOWN` / `RESTART` / `+` / `-` / `0` / `1`。
 *
 * `needsConfirm` 的项会**改变机器状态**（关机 / 重启整机），
 * 所以走两步式确认（与删除同一套交互）—— 误点一次就是设备被关掉。
 */
const udpCommands = [
  { command: 'DOWN', label: '关机（整机）', needsConfirm: true },
  { command: 'RESTART', label: '重启（整机）', needsConfirm: true },
  { command: '+', label: '音量 +', needsConfirm: false },
  { command: '-', label: '音量 −', needsConfirm: false },
  { command: '0', label: '静音', needsConfirm: false },
  { command: '1', label: '取消静音', needsConfirm: false },
] as const

const armedUdp = ref<string | null>(null)

/**
 * 客户机实时态那一行的样式。
 *
 * ⚠️ **"还没上报"（null）不能画成警告色**：它不是故障，而是"这台机器还没告诉我"
 * （客户端版本较旧，或中心刚重启）。给它一个警告色，会把一次正常的版本差异
 * 渲染成"某台机器出问题了"，然后所有人都去查那台机器 —— 而问题其实在这里。
 */
const udpLiveClass = computed(() => {
  const state = props.host.udpChannel

  if (!state) return 'unknown'
  return state.listening ? 'ok' : 'warn'
})

async function sendUdp(command: string, label: string, needsConfirm: boolean) {
  if (udpBusy.value) return

  if (needsConfirm && armedUdp.value !== command) {
    armedUdp.value = command
    return
  }

  armedUdp.value = null
  udpBusy.value = true
  udpResult.value = null

  try {
    const r = await api.sendAgentUdpCommand(props.host.id, command)
    udpResult.value = { kind: 'ok', text: r.message ?? `已发出「${label}」。` }
    emit('saved', { ok: true, message: `已发出 UDP 指令「${label}」。`, list: null })
  } catch (e) {
    // 409 的 message 已经写清了原因（通道没启用 / 没拿到密钥 / 不知道地址）—— 原样显示。
    const text = e instanceof Error ? e.message : '发送失败'
    udpResult.value = { kind: 'err', text }
    emit('saved', { ok: false, message: text, list: null })
  } finally {
    udpBusy.value = false
  }
}

const form = ref({ displayName: '', group: '', note: '' })

const dirty = computed(
  () =>
    form.value.displayName.trim() !== (props.host.displayName ?? '') ||
    form.value.group.trim() !== (props.host.group ?? '') ||
    form.value.note.trim() !== (props.host.note ?? ''),
)

function resetForm() {
  form.value = {
    displayName: props.host.displayName ?? '',
    group: props.host.group ?? '',
    note: props.host.note ?? '',
  }
}

/** NULL 是"取不到"，显示成 0 会变成一个假的硬件参数。 */
function fmtText(value: string | null | undefined): string {
  return value && value.trim() !== '' ? value : '—'
}

function fmtCount(value: number | null, unit: string): string {
  return value == null ? '未知' : `${value} ${unit}`
}

function fmtMemory(mb: number | null): string {
  if (mb == null) return '未知'
  return mb < 1024 ? `${mb} MB` : `${(mb / 1024).toFixed(1)} GB`
}

/**
 * 温度（℃）。
 *
 * 🔥 **null 显示 `—`，绝不显示 `0 ℃`** —— 那会被读成"机器很凉快"，与事实恰好相反。
 * 这与本文件里其它 `—` 是同一条纪律（见文件头注释）。
 */
function fmtTemp(c: number | null | undefined): string {
  return c == null ? '—' : `${c.toFixed(0)} ℃`
}

/** CPU 型号往往很长（"Intel(R) Core(TM) i5-10400 CPU @ 2.90GHz"），详情里给全量。 */
const cpuText = computed(() => detail.value?.info?.cpuName ?? props.host.cpuName ?? null)

async function load() {
  loading.value = true
  error.value = ''

  try {
    detail.value = await api.agentDetail(props.host.id)
  } catch (e) {
    error.value = e instanceof Error ? e.message : '加载详情失败'
  } finally {
    loading.value = false
  }

  resetForm()
}

async function save() {
  saving.value = true

  try {
    const result = await api.updateAgent(props.host.id, {
      displayName: form.value.displayName,
      group: form.value.group,
      note: form.value.note,
    })

    emit('saved', result)

    // 服务端可能对空值做了兜底（例如显示名回落到主机名），重新拉一次以显示真实值。
    if (result.ok) await load()
  } catch (e) {
    emit('saved', {
      ok: false,
      message: e instanceof Error ? e.message : '保存失败',
      list: null,
    })
  } finally {
    saving.value = false
  }
}

// ═══════════════════════════════════════════════════════════════
// 遥测：时序
// ═══════════════════════════════════════════════════════════════

/**
 * 可选的时间窗。
 *
 * 上限刻意只到 30 天：更长的窗口服务端只能给日聚合，
 * 而"一天一个点"的曲线在视觉上与"实时曲线"几乎一样，极易被误读成"一直很稳"。
 * 需要看更长的趋势，将来应该做一个专门的"日报表"页面，而不是把折线拉长。
 */
const RANGES = [
  { key: '1h', label: '最近 1 小时', hours: 1 },
  { key: '6h', label: '最近 6 小时', hours: 6 },
  { key: '24h', label: '最近 24 小时', hours: 24 },
  { key: '7d', label: '最近 7 天', hours: 24 * 7 },
  { key: '30d', label: '最近 30 天', hours: 24 * 30 },
] as const

const rangeKey = ref<(typeof RANGES)[number]['key']>('6h')
const series = ref<AgentMetricsSeriesDto | null>(null)
const seriesLoading = ref(false)
const seriesError = ref('')

/** 图表的纵轴/提示格式。 */
const percentFormat = (v: number) => `${v.toFixed(0)}%`
const rateFormat = (v: number) => fmtRate(v)

const cpuMemSeries: ChartSeries[] = [
  { key: 'cpuPct', label: 'CPU', color: '#2563eb' },
  { key: 'memPct', label: '内存', color: '#7c3aed' },
]

const ioSeries: ChartSeries[] = [
  { key: 'diskReadBps', label: '磁盘读', color: '#0891b2' },
  { key: 'diskWriteBps', label: '磁盘写', color: '#d97706' },
  { key: 'netUpBps', label: '网络上行', color: '#16a34a' },
  { key: 'netDownBps', label: '网络下行', color: '#dc2626' },
]

const gpuSeries: ChartSeries[] = [{ key: 'gpuPct', label: 'GPU', color: '#db2777' }]

const rangeHours = computed(
  () => RANGES.find((r) => r.key === rangeKey.value)?.hours ?? 6,
)

async function loadSeries() {
  seriesLoading.value = true
  seriesError.value = ''

  try {
    // 发给服务端的是**绝对时刻**（`Z` 结尾的 UTC 完全没问题）；
    // 服务端会把窗口归一化到 +08:00 再回显，所以轴标签不会差 8 小时。
    const to = new Date()
    const from = new Date(to.getTime() - rangeHours.value * 3600 * 1000)

    series.value = await api.agentMetrics(props.host.id, {
      from: from.toISOString(),
      to: to.toISOString(),
    })
  } catch (e) {
    seriesError.value = e instanceof Error ? e.message : '加载时序失败'
    series.value = null
  } finally {
    seriesLoading.value = false
  }
}

/** 数据来源的说明文案。**必须显示** —— 见 `AgentMetricsSeriesDto` 的注释。 */
const sourceText = computed(() => {
  const s = series.value
  if (!s) return ''

  switch (s.source) {
    case 'disabled':
      return '服务端已停用遥测采集（Server:Agent:Telemetry:Enabled=false），因此没有数据。'
    case 'daily+raw':
      return `较早的一段按「天」聚合、最近的一段用 5 秒明细（共 ${s.rawCount} 个原始采样点）。相邻两点之间可能隔了一整天。`
    case 'hourly':
      return `按小时聚合（共 ${s.rawCount} 个原始采样点）。`
    default:
      return `5 秒明细${s.bucketSeconds > 0 ? `，按 ${s.bucketSeconds} 秒合并为一个点` : ''}（共 ${s.rawCount} 个原始采样点）。`
  }
})

/**
 * 窗口里"没有数据"的那几段 —— 也就是曲线上被留空的那几段（2026-10-16）。
 *
 * 🔥 **光把线断开是不够的**：一条断掉的折线是**沉默的**，用户第一反应是"图表坏了"。
 * 所以这里要把话说出来（几段、从几点到几点、多久）——
 * 与服务端 `AgentMetricsPointDto.IsGap` 的约定配套：断开 + 说明，两件都要。
 *
 * 区间取「标记点之前那个真实点 → 之后那个真实点」：这是折线上**真正空掉的那一段**，
 * 而不是"标记点自己所在的那一格"（后者会少算一段，看着像少算了十分钟）。
 */
const gaps = computed(() => {
  const pts = series.value?.points ?? []
  const list: { fromMs: number; toMs: number }[] = []

  for (let i = 0; i < pts.length; i += 1) {
    if (!pts[i].isGap) continue

    const before = pts[i - 1]
    const after = pts[i + 1]
    if (!before || !after) continue

    const fromMs = Date.parse(before.at)
    const toMs = Date.parse(after.at)
    if (!Number.isFinite(fromMs) || !Number.isFinite(toMs) || toMs <= fromMs) continue

    list.push({ fromMs, toMs })
  }

  return list
})

/**
 * 空白段的说明文案。最多列 3 段 —— 再多的场合（一台整天掉线的机器）列全了也没人读，
 * 那句"共 N 段"才是重点。
 */
const gapNote = computed(() => {
  const list = gaps.value
  if (list.length === 0) return ''

  const total = list.reduce((sum, g) => sum + (g.toMs - g.fromMs) / 1000, 0)

  // 只到分钟：这句话是给人读的，秒在这条说明里只会把行撑长。
  const clock = (ms: number) => fmtDateTime(beijingIso(ms)).slice(0, 16)

  const shown = list
    .slice(0, 3)
    .map((g) => `${clock(g.fromMs)} → ${clock(g.toMs)}（${fmtUptimeHuman((g.toMs - g.fromMs) / 1000)}）`)
    .join('；')

  const more = list.length > 3 ? `，另有 ${list.length - 3} 段` : ''

  return `浅色区域 = 那段时间没有任何采样（共 ${list.length} 段、合计 ${fmtUptimeHuman(total)}）：${shown}${more}。`
})

/** 空白段为什么会出现 —— 与"这段时间没有数据"那句空态文案保持同一套说法。 */
const gapReason = '最常见的是被控端当时离线（关机 / 客户端没在跑），也可能是它连不上中心。空白处不连线、不补 0。'

// ═══════════════════════════════════════════════════════════════
// 逐设备曲线（M4.8；2026-10-15 重构：身份从「实例 Key」提升为「物理硬件」）
//
// 页面的形状：**上方是硬件选择器（勾选，可多选），下方是勾中的那些硬件的曲线**。
// 多路 CPU / 多块硬盘 / 多块显卡 / 多块网卡在这里各占一格，用各自的型号名区分 ——
// 这是需求原文"若该客户机有多个 CPU、多个硬盘、多个显卡、多个网卡，
// 需要使用具体型号分开显示"的落点。
//
// 🔥 **为什么身份不能是 Key（本页最容易写错的一处）**
//
//   GPU 的 `LUID`、Hyper-V 虚拟网卡的 `Id` 都是**运行时标识符 —— 系统每次启动重新分配**。
//   实测那台 `DESKTOP-1UKRNGI`（只装了 **1** 块 GTX 1060）在一天里重启 6 次，窗口内因此留下
//   **6 个两两不重叠的 LUID**；同一台机器的 `vEthernet (Default Switch)` 留下 **5 个不同的
//   GUID**，而它们的**名字一模一样**。
//
//   原实现拿"窗口内出现过的所有 Key 的并集"当条目 ⇒ 同一块硬件被列成 6 条同名选项，
//   一条本该连续的曲线被切成 6 段互不相干的片段（现场反馈："多gpu2.png"）。
//   证据（真实库反查，含 LUID 区间与 uptime 归零的对应）见
//   `.workbuddy/memory/2026-10-15.md` §5。
//
//   ⇒ 身份改为 **`kind` + 型号名**，同一身份把窗口内用过的多个 Key 全部收拢；
//     **同型号只有真的在同一帧里并存过**才加 `#n`（那才是真的多卡 / 多网卡）。
//
//   对照：磁盘（`disk0/1/2`）与 CPU（`cpu0`）的 Key **重启后是稳定的**，本来就没这个问题 ——
//   这次改动对它们无感，也正因如此，截图上"磁盘 / 处理器"那两组本来就是对的。
// ═══════════════════════════════════════════════════════════════

/** 整机概览（= 重构前那四张图）。它不是一块硬件，所以单独用一个哨兵值。 */
const OVERVIEW = 'overview'

/**
 * 勾选硬件的 id 集合（含 {@link OVERVIEW}）。
 *
 * 从"单选一个 chip"改成"勾选多块"（现场口径）：同一种类勾中几块，
 * 它们的曲线就画在**同一张图**上做对比。
 * 默认只勾「整机概览」—— 与重构前的默认视图完全一致，不改既有习惯。
 */
const checkedIds = ref<string[]>([OVERVIEW])

function isChecked(id: string): boolean {
  return checkedIds.value.includes(id)
}

function toggleChecked(id: string) {
  checkedIds.value = isChecked(id)
    ? checkedIds.value.filter((x) => x !== id)
    : [...checkedIds.value, id]
}

// 硬件身份的全部判据（为什么不能拿 Key 当身份、并存怎么判、占位名怎么办）都在
// `../deviceIdentity` 里 —— 那是个纯函数模块，可以单独喂数据验证。
// 本文件只负责"把它的结论画出来"。

/**
 * 这台机器**现在**有哪些硬件 —— 硬件列表的唯一来源。
 *
 * 🔥 **数据来源是时序点里的逐设备读数，而不是硬件清单**：清单里有型号但没有
 * `cpu0` / `disk0` 这种 Key，两者对不上号（多路 / 多盘时更对不上 —— 见
 * `AgentDeviceReadingDto.key`）。用清单去凑，只会拼出一个"型号和曲线各说各话"的选择器。
 *
 * 归并 / 并存的全部规则都在 `identifyPhysicalDevices` 里（含"为什么键不能当身份"），
 * 本文件只负责把它的结论画出来。
 */
const windowDevices = computed<PhysicalDevice[]>(() =>
  identifyPhysicalDevices(series.value?.points ?? []),
)

/** 按种类分组（空组不显示，免得出现一行光秃秃的标题）。 */
const deviceGroups = computed(() =>
  AGENT_DEVICE_KINDS.map((kind) => ({
    kind,
    label: AGENT_DEVICE_KIND_LABELS[kind],
    devices: windowDevices.value.filter((d) => d.kind === kind),
  })).filter((g) => g.devices.length > 0),
)

/** 勾中的那些硬件（按列表顺序）。 */
const checkedDevices = computed(() => windowDevices.value.filter((d) => isChecked(d.id)))

/**
 * 选择器每一格上的"当前值"。
 *
 * 取**最后一个有读数的点**（而不是最后一个点）：速率类计数器要两次采样才有值，
 * 所以**首帧**的 `readBps` / `cpuPct` / `gpuPct` 本来就是 null ——
 * 拿首帧取值会一律显示成「—」，看着像采集坏了。
 *
 * ⚠️ 2026-10-13 起实时帧**也带**逐设备读数（见下面那个 watcher），
 *    所以正常情况下这里取到的就是最新那一刻的值，不再"停在打开页面那一刻"。
 */
function deviceHeadline(dev: PhysicalDevice): string {
  const s = series.value
  if (!s) return '—'

  for (let i = s.points.length - 1; i >= 0; i -= 1) {
    const hit = pickReading(s.points[i], dev)
    if (!hit) continue

    switch (dev.kind) {
      case 'cpu':
        return hit.cpuPct == null ? '—' : fmtPercent(hit.cpuPct)
      case 'gpu':
        return hit.gpuPct == null ? '—' : fmtPercent(hit.gpuPct)
      case 'disk':
        return `读 ${hit.readBps == null ? '—' : fmtRate(hit.readBps)} / 写 ${
          hit.writeBps == null ? '—' : fmtRate(hit.writeBps)
        }`
      case 'net':
        return `↑ ${hit.upBps == null ? '—' : fmtRate(hit.upBps)} / ↓ ${
          hit.downBps == null ? '—' : fmtRate(hit.downBps)
        }`
      default:
        return '—'
    }
  }

  return '—'
}

/**
 * 同图多块硬件的配色。
 *
 * 一个颜色认**一块硬件**（不是一条线）：磁盘 / 网卡的"两条通道"共用同一色的实线 + 虚线，
 * 于是"这几条走的是同一块设备"一眼能看出来。
 */
const DEVICE_COLORS = [
  '#2563eb', // 蓝
  '#d97706', // 橙
  '#16a34a', // 绿
  '#db2777', // 玫红
  '#0891b2', // 青
  '#7c3aed', // 紫
  '#dc2626', // 红
  '#65a30d', // 橄榄
]

/** 一种设备一张图：勾中的那些画在一起做对比。 */
interface DeviceChart {
  kind: AgentDeviceKind
  devices: PhysicalDevice[]
  series: ChartSeries[]
  points: AgentMetricsPointDto[]
  yMax: number | null
  axisFormat: (v: number) => string
  emptyText: string
}

/**
 * 勾中的硬件 → 按种类各一张图。
 *
 * 摊平的手法与重构前一致：把值摊到点上的扁平字段，逐设备曲线就白捡了 `MetricsChart`
 * 那套"**null 必须断开折线**"的画法 —— 于是"没采到"在整机曲线和硬件曲线上长得一模一样，
 * 不会各自演化出两套 null 处理。
 *
 * 槽位命名（`i` = 这块硬件在**这一张图**里的下标）：
 * `d{i}` CPU / GPU 主指标；`d{i}r` / `d{i}w` 磁盘读 / 写；`d{i}u` / `d{i}n` 网卡上 / 下行。
 */
const checkedCharts = computed<DeviceChart[]>(() => {
  const s = series.value
  if (!s) return []

  const charts: DeviceChart[] = []

  for (const kind of AGENT_DEVICE_KINDS) {
    const devices = checkedDevices.value.filter((d) => d.kind === kind)
    if (devices.length === 0) continue

    const series: ChartSeries[] = []

    devices.forEach((dev, i) => {
      // ⚠️ 取模而不是"超出就报错"：8 块同图已经很难读，第 9 块复用颜色总好过不画。
      const color = DEVICE_COLORS[i % DEVICE_COLORS.length]

      switch (kind) {
        case 'cpu':
        case 'gpu':
          series.push({ key: `d${i}`, label: dev.name, color })
          break

        case 'disk':
          series.push({ key: `d${i}r`, label: `${dev.name} 读`, color })
          series.push({ key: `d${i}w`, label: `${dev.name} 写`, color, dash: '5 3' })
          break

        case 'net':
          series.push({ key: `d${i}u`, label: `${dev.name} 上行`, color })
          series.push({ key: `d${i}n`, label: `${dev.name} 下行`, color, dash: '5 3' })
          break
      }
    })

    const points: AgentMetricsPointDto[] = s.points.map((p) => {
      const row: Record<string, unknown> = {
        at: p.at,
        cpuPct: null,
        memPct: null,
        memUsedMb: null,
        memTotalMb: null,
        diskReadBps: null,
        diskWriteBps: null,
        netUpBps: null,
        netDownBps: null,
        gpuPct: null,
        uptimeSeconds: null,
        cpuTempC: null,
        gpuTempC: null,
        storageTempC: null,
        devices: null,
        // 🔥 **断点标记必须跟着摊下来**（2026-10-16）：漏了它，硬件曲线会在离线处
        //    少一张"浅底衬"（折线本身仍会断，因为所有槽位都是 null），
        //    于是同一页面上"整机图有底衬、硬件图没有"——两处对同一段时间各说各话。
        isGap: p.isGap === true,
      }

      devices.forEach((dev, i) => {
        const hit = pickReading(p, dev)

        row[`d${i}`] = kind === 'cpu' ? (hit?.cpuPct ?? null) : (hit?.gpuPct ?? null)
        row[`d${i}r`] = hit?.readBps ?? null
        row[`d${i}w`] = hit?.writeBps ?? null
        row[`d${i}u`] = hit?.upBps ?? null
        row[`d${i}n`] = hit?.downBps ?? null
      })

      return row as unknown as AgentMetricsPointDto
    })

    // ⚠️ **纵轴量纲随种类变**：CPU / 显卡是 0–100 的百分比（纵轴钉死 100），
    //    磁盘 / 网卡是字节/秒（按数据自适应）。把百分比的纵轴上限套到速率图上，
    //    或者反过来，都会让读者拿两个不同量纲的高度做比较。
    const percentLike = kind === 'cpu' || kind === 'gpu'
    const who = devices.map((d) => d.name).join('、')

    charts.push({
      kind,
      devices,
      series,
      points,
      yMax: percentLike ? 100 : null,
      axisFormat: percentLike ? percentFormat : rateFormat,
      emptyText:
        kind === 'disk'
          ? `这段时间没有「${who}」的读写采样`
          : kind === 'net'
            ? `这段时间没有「${who}」的上下行采样`
            : `这段时间没有「${who}」的占用率采样`,
    })
  }

  return charts
})

/** 这一张图里的硬件在窗口里到底有没有取到值。 */
function chartHasData(c: DeviceChart): boolean {
  return c.points.some((p) =>
    c.series.some((s) => {
      const v = (p as unknown as Record<string, unknown>)[s.key]
      return typeof v === 'number' && Number.isFinite(v)
    }),
  )
}

/**
 * 为什么一个硬件选择器都没有 —— **必须说出来**。
 *
 * 两种常见原因，用户自己看不出区别：
 *   · 窗口里含按天聚合的部分（查 7 天 / 30 天时较早那段），而日聚合**不聚合逐设备读数**；
 *   · 客户机版本较旧，根本不上报逐设备明细。
 *
 * 不说的话，用户点开"最近 30 天"只看到一片空白，然后去查一个不存在的故障。
 */
const devicesUnavailableText = computed(() => {
  const s = series.value
  if (!s || windowDevices.value.length > 0) return ''

  if (s.source === 'daily+raw' || s.source === 'daily') {
    return '这段窗口包含按「天」聚合的部分，而逐设备读数只在 5 秒明细里留存，因此没有可按硬件勾选的曲线。把范围缩到 24 小时内通常就有了。'
  }

  return '这段时间没有逐设备读数。通常是这台客户机的版本较旧（不上报各硬件的单独读数），或当时没采到。'
})

/**
 * 换窗口 / 换机器后，原先勾中的硬件可能已经不在新列表里了 —— 把那些钩去掉。
 *
 * 不清的话，勾选态会指向一块不存在的硬件：下方少一张图，而且**没有任何提示**
 * （用户会以为那台机器上确实没有它）。`OVERVIEW` 不是硬件，始终保留。
 *
 * ⚠️ 这里**不做**"一个都没勾就自动勾上第一块"——把钩全部取消是用户的合法操作
 * （看提示语，或只想清屏），自动勾回来会让人以为界面在跟自己抢。空勾时由模板给出提示。
 */
watch(windowDevices, (list) => {
  const alive = new Set<string>([OVERVIEW, ...list.map((d) => d.id)])
  const kept = checkedIds.value.filter((id) => alive.has(id))

  if (kept.length !== checkedIds.value.length) checkedIds.value = kept
})

/**
 * 一个**全 null 的点**：只用来当"这段没有数据"的断点标记（`isGap`）。
 *
 * ⚠️ 字段必须与服务端那份 `AgentMetricsPointDto` 一一对上（尤其别漏 `devices`）：
 *    漏一个，逐设备曲线的取值方读到 `undefined`，那一段的断与不断就成了巧合。
 */
function gapPoint(at: string): AgentMetricsPointDto {
  return {
    at,
    cpuPct: null,
    memPct: null,
    memUsedMb: null,
    memTotalMb: null,
    diskReadBps: null,
    diskWriteBps: null,
    netUpBps: null,
    netDownBps: null,
    gpuPct: null,
    uptimeSeconds: null,
    cpuTempC: null,
    gpuTempC: null,
    storageTempC: null,
    devices: null,
    isGap: true,
  }
}

/**
 * 把新到的实时帧"往前挪一格"接到曲线末尾。
 *
 * 为什么不在每次刷新时整段重拉：整段重拉会扫库（最长 30 天），
 * 而"往前挪一格"只需要一个点。这也是服务端把推送设计成"只带最新一帧"的原因。
 *
 * ⚠️ 末尾这一格是**原始瞬时值**，而前面是桶均值 —— 分辨率不完全一致。
 *    对一个"看趋势"的图完全够用，但别拿末尾那一格去和前面的点做精确比较。
 *
 * 🔥 **这里也要判"离线留空"**（2026-10-16）：被控端离线十分钟再回来时，
 *    服务端只会照常推来一帧 —— 不判的话，这一帧会被**一条直线**从十分钟前接过来，
 *    而那正是"客户端离线的时间段曲线必须留空"要消灭的东西。
 *    ⚠️ 更糟的是，用户点一下「刷新」（走服务端那条路）它就变回空白了：
 *    **同一段时间两种画法**，还取决于"你有没有点过刷新" —— 这种不确定性比画错更坏。
 *    判据用的是服务端下发的 `gapThresholdSeconds`，与在线表那颗灰点同源。
 */
watch(
  () => props.host.metrics,
  (m) => {
    const current = series.value

    if (!m || !m.at || !current || current.points.length === 0) return

    const last = current.points[current.points.length - 1]
    const atMs = Date.parse(m.at)
    const lastMs = Date.parse(last.at)

    // 旧帧（乱序到达）直接丢，别把曲线往回拽。
    if (!Number.isFinite(atMs) || !Number.isFinite(lastMs) || atMs <= lastMs) return

    /*
     * 这一帧与上一格之间空得太久 ⇒ 那段时间没有被控端的采样，曲线必须留空。
     *
     * 标记落在「上一格 + 一格」上（与服务端 `MarkGaps` 同一处算法）：那是空白真正开始的地方，
     * 不是随手编的一个时刻。`bucketSeconds` 为 0（服务端没给）时退回 5 秒的采样间隔。
     */
    const gapThresholdMs = (current.gapThresholdSeconds ?? 0) * 1000
    const stepMs = (current.bucketSeconds > 0 ? current.bucketSeconds : 5) * 1000

    const marker =
      gapThresholdMs > 0 && atMs - lastMs >= gapThresholdMs ? gapPoint(beijingIso(lastMs + stepMs)) : null

    const point: AgentMetricsPointDto = {
      at: m.at,
      cpuPct: m.cpuPct,
      memPct: m.memPct,
      memUsedMb: m.memUsedMb,
      memTotalMb: m.memTotalMb,
      diskReadBps: m.diskReadBps,
      diskWriteBps: m.diskWriteBps,
      netUpBps: m.netUpBps,
      netDownBps: m.netDownBps,
      gpuPct: m.gpuPct,
      uptimeSeconds: m.uptimeSeconds,
      cpuTempC: m.cpuTempC,
      gpuTempC: m.gpuTempC,
      storageTempC: m.storageTempC,
      // 🔥 实时帧**必须带上逐设备读数**（2026-10-13 修）。
      //    这里原来写的是 `devices: null`，理由是"摘要不带逐设备" —— 但那个前提本身是错的：
      //    客户端每采一帧就有逐设备明细，摘要不透传的话，页面里只有整机那几条线在往前挪，
      //    而硬件选择器上的「当前值」与硬件曲线会**永远停在打开页面那一刻**。
      //    用户切到某块硬盘时看到的正好是"整机在飙、这块盘一动不动"。
      //    这与 AgentMetricsSampleDto.Devices 定下的"逐设备与整机同一采样节奏"是同一个要求。
      devices: m.devices ?? null,
    }

    const spanMs = Date.parse(current.to) - Date.parse(current.from)
    const fromMs = atMs - spanMs

    const appended = marker ? [marker, point] : [point]
    const points = [...current.points.filter((p) => Date.parse(p.at) >= fromMs), ...appended]

    // 点数上限：窗口拉到 30 天时点数已经由服务端降采样控制过，
    // 这里只是防止"页面开着不关"无限增长。
    if (points.length > 3000) points.splice(0, points.length - 3000)

    series.value = {
      ...current,
      // 轴端点用 +08:00 形态（不是 `toISOString()`）—— 见 format.beijingIso 的注释。
      from: beijingIso(fromMs),
      to: m.at,
      points,
    }
  },
)

// ═══════════════════════════════════════════════════════════════
// 遥测：开关机记录 / 硬件清单
// ═══════════════════════════════════════════════════════════════

const power = ref<AgentPowerEventListDto | null>(null)
const powerLoading = ref(false)
const powerMore = ref(false)

async function loadPower(extend = false) {
  powerLoading.value = true

  try {
    power.value = await api.agentPowerEvents(props.host.id, {
      limit: extend ? 500 : 100,
    })
    powerMore.value = extend
  } catch {
    // 开关机记录不是本页的主内容，读不到就让区块空着，别把整页搅黄。
    power.value = null
  } finally {
    powerLoading.value = false
  }
}

const inventory = ref<AgentInventoryEnvelopeDto | null>(null)
const inventoryLoading = ref(false)
const inventoryOpen = ref(false)

async function loadInventory() {
  inventoryLoading.value = true

  try {
    inventory.value = await api.agentInventory(props.host.id)
  } catch {
    inventory.value = null
  } finally {
    inventoryLoading.value = false
  }
}

function toggleInventory() {
  inventoryOpen.value = !inventoryOpen.value
  if (inventoryOpen.value && !inventory.value) void loadInventory()
}

// ═══════════════════════════════════════════════════════════════
// 「接收到的数据」（2026-10-13）—— 排查"下发了指令但机器毫无反应"
// ═══════════════════════════════════════════════════════════════

/**
 * 客户机的接收记录。
 *
 * ⚠️ <b>默认折叠、点开才拉</b>：它是一次真实往返（网页 → 服务端 → 长连接 → 客户机，
 * 最多等 8 秒）。跟着详情一起拉的话，"翻客户机列表、挨个点开看看"会变成
 * 每台一次跨网往返 —— 而绝大多数时候没人关心这个。
 */
const receivedData = ref<AgentReceivedDataDto | null>(null)
const receivedLoading = ref(false)
const receivedError = ref('')
const receivedOpen = ref(false)

async function loadReceived() {
  receivedLoading.value = true
  receivedError.value = ''

  try {
    receivedData.value = await api.agentReceivedData(props.host.id)
  } catch (e) {
    // 走到这里说明请求本身没成功（断网 / 403 / 服务端 5xx）。
    // 与 `ok:false` 分开：那是"问到了，答案是'不行'"（机器离线、客户端太旧），
    // 页面要就地把它显示成人话，而不是当成异常。
    receivedError.value = e instanceof Error ? e.message : '读取接收记录失败'
    receivedData.value = null
  } finally {
    receivedLoading.value = false
  }
}

function toggleReceived() {
  receivedOpen.value = !receivedOpen.value
  if (receivedOpen.value && !receivedData.value && !receivedLoading.value) void loadReceived()
}

/** 链路名。未知值原样显示：客户机比网页新的时候不能变成空白。 */
function receivedKindLabel(kind: string): string {
  if (kind === AGENT_RECEIVED_KINDS.udp) return 'UDP'
  if (kind === AGENT_RECEIVED_KINDS.signalr) return '中心'
  return kind
}

/** 结论的中文名。 */
function receivedOutcomeLabel(outcome: string): string {
  switch (outcome) {
    case AGENT_RECEIVED_OUTCOMES.executed:
      return '已执行'
    case AGENT_RECEIVED_OUTCOMES.failed:
      return '执行失败'
    case AGENT_RECEIVED_OUTCOMES.ignored:
      return '未执行'
    case AGENT_RECEIVED_OUTCOMES.unrecognized:
      return '认不出'
    case AGENT_RECEIVED_OUTCOMES.blocked:
      return '已拦截'
    case AGENT_RECEIVED_OUTCOMES.accepted:
      return '已受理'
    default:
      return outcome
  }
}

/** 结论对应的徽章样式（复用页面上既有的 badge 三态）。 */
function receivedOutcomeClass(outcome: string): string {
  switch (outcome) {
    case AGENT_RECEIVED_OUTCOMES.executed:
      return 'ok'
    case AGENT_RECEIVED_OUTCOMES.failed:
    case AGENT_RECEIVED_OUTCOMES.unrecognized:
      return 'err'
    case AGENT_RECEIVED_OUTCOMES.blocked:
    case AGENT_RECEIVED_OUTCOMES.ignored:
      return 'warn'
    default:
      return ''
  }
}

/** 只取时分秒 —— 记录大多是"刚刚发生的"，日期反而占地方。 */
function receivedClock(at: string): string {
  const d = new Date(at)
  return isNaN(d.getTime()) ? at : d.toLocaleTimeString()
}

// ═══════════════════════════════════════════════════════════════
// 温度（M4.3）—— 可选能力，拿不到就如实说"拿不到"
// ═══════════════════════════════════════════════════════════════

/**
 * 温度曲线的三条线。
 *
 * 颜色刻意与上面的 CPU/GPU 使用率图区分开：那是"忙不忙"，这是"烫不烫"，
 * 两组图长得太像会让人把 60% 看成 60℃。
 */
const tempSeries: ChartSeries[] = [
  { key: 'cpuTempC', label: 'CPU', color: '#ea580c' },
  { key: 'gpuTempC', label: 'GPU', color: '#0d9488' },
  { key: 'storageTempC', label: '硬盘', color: '#4f46e5' },
]

const tempFormat = (v: number) => `${v.toFixed(0)} ℃`

/**
 * 硬件温度监控的状态（M4.3）。
 *
 * ⚠️ 与温度读数**来源不同**：读数在每个采样点里（5 秒一帧），
 * 状态在硬件清单里（机器级、6 小时一帧）—— 所以它要等清单加载完才有值。
 * 清单没回来之前是 null，页面按"未知"显示，不猜。
 */
const thermalState = computed(() => inventory.value?.inventory?.thermalState ?? null)

const thermalNote = computed(() => inventory.value?.inventory?.thermalNote ?? null)

/** 只有 `ok` 时温度读数才有意义；其余两种状态都要显示成"没读数"。 */
const thermalOk = computed(() => thermalState.value === THERMAL_STATES.ok)

/** 驱动加载失败是真正的异常（会被安全软件拦），需要显眼；"未启用"是默认状态，保持中性。 */
const thermalFailed = computed(() => thermalState.value === THERMAL_STATES.failed)

/**
 * 「未启用」时给一句"怎么才能开"。
 *
 * ⚠️ 刻意由前端写死，而不是复用服务端那条 `thermalNote`：服务端只知道"现在没启用"，
 *    不知道当初装的时候勾没勾。把"怎么开"直接写在页面上，运维才不用去翻文档。
 */
const thermalHint = computed(() => {
  switch (thermalState.value) {
    case THERMAL_STATES.disabled:
      return '温度监控是可选能力，默认不开（它需要加载一个内核驱动）。重新安装客户端时勾选「启用硬件温度监控」即可。'
    case THERMAL_STATES.failed:
      return '已启用，但没能拿到任何温度读数。'
    default:
      return '这台机器的客户端版本较旧，还没上报温度状态。'
  }
})

/** 页面上那行补充说明：优先用服务端给的原文（失败原因 / 未启用说明），没有再用兜底文案。 */
const thermalDetail = computed(() => thermalNote.value ?? thermalHint.value)

/**
 * 温度状态是 `ok`、但 CPU 这一项为 null。
 *
 * 🔥 这条是 2026-10-19 补的：AMD 机器上很常见"硬盘 / 显卡有度数、CPU 是 —"，
 *    而 `ok` 时页面只在 title 上挂了 `thermalDetail` —— 不悬停就只有一个光秃秃的「CPU —」。
 *    运维的结论于是变成"读不到 AMD 温度"，而真正的原因（没枚举到 CPU / 这颗 U 没有被
 *    驱动库识别 / 传感器读数是 0）其实已经躺在 `thermalNote` 里了。
 *
 * ⚠️ 只在 ok 且 CPU 缺失时提示：三项都齐的时候不要多一行字，
 *    未启用 / 失败时已经有整块的状态文案，不必重复。
 */
const cpuTempMissing = computed(() => thermalOk.value && props.host.metrics?.cpuTempC == null)

/** 这段时间里到底有没有温度点 —— 决定画曲线还是画"未启用"说明。 */
const hasTempData = computed(() =>
  (series.value?.points ?? []).some(
    (p) => p.cpuTempC != null || p.gpuTempC != null || p.storageTempC != null,
  ),
)

/**
 * 硬件清单的**一句话摘要**（展示在区块标题下方，展开与否都看得见）。
 *
 * 为什么要它：整块清单默认是**折叠**的，而"这台机器是什么显卡"恰恰是最常被问到的一句 ——
 * 让人为了看一眼型号先点开一大块（里面还有内存条 / 分区 / 物理磁盘）实在不划算。
 *
 * ⚠️ 取模型时优先 `videoProcessor`（芯片名，如 `NVIDIA GeForce RTX 3060`），
 *    退回 `name`；两者都空才说「未采集到」—— 不编一个型号出来。
 */
const gpuSummary = computed(() => {
  const gpus = inventory.value?.inventory?.gpus ?? []

  if (gpus.length === 0) return '未采集到'

  const first = gpus.find((g) => (g.name ?? '').trim() !== '' || (g.videoProcessor ?? '').trim() !== '') ?? gpus[0]
  const name = ((first.videoProcessor ?? '').trim() || (first.name ?? '').trim()) || '(适配器未报名字)'

  return gpus.length > 1 ? `${name} 等 ${gpus.length} 块` : name
})

/** 各种事件的徽章配色。 */
function kindClass(kind: string): string {
  if (isProblemEvent(kind)) return 'err'
  if (kind === 'boot' || kind === 'wake') return 'ok'
  if (kind === 'sleep') return 'warn'
  return ''
}

function kindLabel(kind: string): string {
  return POWER_EVENT_LABELS[kind] ?? kind
}

/** 分区使用率。**null 一律返回 null**（不显示成 0%，那会被读成"空的"）。 */
function diskUsedPercent(totalMb: number | null, freeMb: number | null): number | null {
  if (totalMb === null || freeMb === null || totalMb <= 0) return null
  return ((totalMb - freeMb) / totalMb) * 100
}

async function loadAll() {
  await load()
  await Promise.all([loadSeries(), loadPower(), loadInventory()])
}

// 父级切换展开的行时本组件会被复用（同一个 v-if 位置），因此要跟着 props 重新拉。
watch(
  () => props.host.id,
  () => {
    // 接收记录是**另一台机器的** —— 不跟着清掉就会把上一台的记录显示成这一台的。
    receivedData.value = null
    receivedError.value = ''
    receivedOpen.value = false

    // 硬件列表是**随机器变的**（同型号也未必是同一块），勾选态跟着回到默认视图。
    // 不清的话会继续勾着上一台机器的条目 —— 而列表一换，那些 id 就全失效了。
    checkedIds.value = [OVERVIEW]

    void loadAll()
  },
  { immediate: true },
)

watch(rangeKey, () => void loadSeries())
</script>

<template>
  <div class="detail">
    <p v-if="error" class="notice err" style="margin-bottom: 10px">{{ error }}</p>

    <!--
      ⚠️ 这个占位**只能**在"还没有内容"时出现（`loading && !detail`），不能只判 `loading`。

      原因是它下面那行是 `v-else-if="detail"` —— 两者是**同一个 v-if 链**：
      一旦 `loading` 为真，整块详情正文（含 M4.5 的 `AgentTaskConfigPanel`）就被**卸载**，
      `loading` 转假再重新挂载。而 `load()` 恰恰是"刷新 = 重新拉一次详情"，
      它会被这些路径触发：
        · 任务配置自己保存成功后立刻 `emit('refresh-host')`；
        · 任务配置在"已下发 ≠ 已应用"期间的 10 秒自动复查（每 10 秒一次）；
        · 台账保存成功后的重读、以及「刷新」按钮。

      整块正文被换掉的后果（实测，见 tools/m45-ui-check）：
        ① 「保存并下发」的成功回执只闪 0.25 秒就被抹掉 —— 归一化后的值对不对，
           运维在页面上根本看不到；
        ② 任务配置的自动复查定时器随组件卸载而被 `onUnmounted(stopPendingPoll)` 清掉
           ⇒ 「已应用版本」**永远不会自己追上**，页面反而提示"一直不动就点重新下发"，
           等于让运维去补一个本该自动完成的动作；
        ③ 两块表单里正在填的内容会在刷新瞬间被丢掉；
        ④ 每次刷新整块正文闪一下"加载中…"。

      改成 `loading && !detail` 之后：首次加载照旧显示占位；刷新时保留旧内容、只把新 props
      补下去（组件实例不重建，计时器与未保存的编辑都在）。
    -->
    <div v-if="loading && !detail" class="muted small">加载中…</div>

    <template v-else-if="detail">
      <!-- ═══ 实时指标 ═══ -->
      <div class="live-strip">
        <template v-if="host.metrics && host.metrics.at">
          <div class="live-item">
            <span class="live-label">CPU</span>
            <span class="live-value">{{ fmtPercent(host.metrics.cpuPct) }}</span>
          </div>
          <div class="live-item">
            <span class="live-label">内存</span>
            <span class="live-value">{{ fmtPercent(host.metrics.memPct) }}</span>
            <span class="live-sub">
              {{ fmtMb(host.metrics.memUsedMb) }} / {{ fmtMb(host.metrics.memTotalMb) }}
            </span>
          </div>
          <div class="live-item">
            <span class="live-label">磁盘</span>
            <span class="live-value">
              读 {{ fmtRate(host.metrics.diskReadBps) }} · 写 {{ fmtRate(host.metrics.diskWriteBps) }}
            </span>
          </div>
          <div class="live-item">
            <span class="live-label">网络</span>
            <span class="live-value">
              ↑ {{ fmtRate(host.metrics.netUpBps) }} · ↓ {{ fmtRate(host.metrics.netDownBps) }}
            </span>
          </div>
          <div class="live-item">
            <span class="live-label">GPU</span>
            <span class="live-value">{{ fmtPercent(host.metrics.gpuPct) }}</span>
          </div>
          <div class="live-item live-thermal" :title="thermalDetail">
            <span class="live-label">温度</span>
            <template v-if="thermalOk">
              <!-- 三项都带标签：只给后两项打标签会让第一个数看着像"温度"的总值。
                   某一项为 null 就显示 `—`（例如非管理员时 CPU / 硬盘没读到，而 GPU 有）。 -->
              <span class="live-value">
                CPU {{ fmtTemp(host.metrics.cpuTempC) }} ·
                GPU {{ fmtTemp(host.metrics.gpuTempC) }} ·
                硬盘 {{ fmtTemp(host.metrics.storageTempC) }}
              </span>
              <!-- CPU 一栏是 — 时补一句：原因在 thermalDetail（服务端的诊断），悬停可看。
                   没有这句，页面上就只有一个没有解释的 "CPU —"。 -->
              <span v-if="cpuTempMissing" class="live-sub" :title="thermalDetail">
                CPU 未读到（悬停看原因）
              </span>
            </template>
            <span v-else class="live-value" :class="{ 'thermal-failed': thermalFailed }">
              {{ thermalStateLabel(thermalState) }}
            </span>
          </div>
          <div class="live-item">
            <span class="live-label">已运行</span>
            <span class="live-value">{{ fmtUptimeHuman(host.metrics.uptimeSeconds) }}</span>
          </div>
          <div class="live-item">
            <span class="live-label">采样于</span>
            <span class="live-value mono">{{ fmtDateTime(host.metrics.at) }}</span>
          </div>
        </template>

        <div v-else class="muted small">
          没有可用的实时数据：这台机器<strong>从未上报过遥测</strong>（客户端版本太旧 / 遥测被关掉），
          或<strong>离线已超过 2 小时</strong>。下方历史曲线若仍有数据，说明它此前是正常的。
        </div>
      </div>

      <!-- ═══ 远程指令（经 UDP 指令通道，M4.6）═══ -->
      <div class="section-head">
        <span class="section-title">远程指令（UDP 通道）</span>
        <span class="muted small">
          由中心向这台机器的 UDP 端口发一条<strong>明文报文</strong>（DOWN / RESTART / + / − / 0 / 1）——
          <strong>不要求它在线</strong>（只要网络通、且它启用了 UDP 指令通道）
        </span>
      </div>

      <!-- ── 客户机**自己报的**实时态（2026-10-14）──
           这一行是"点了没反应"的分水岭：在它之前，中心只看"我下发的配置里写着启用没有"，
           而配置写着启用不等于它在听（本机停用了任务下发 / 端口绑不上 / 配置还没应用）。
           三种情形（在听 / 明确没在听 / 还没告诉我）必须分开说 —— 处置完全不同。 -->
      <div class="udp-live" :class="udpLiveClass">
        <template v-if="props.host.udpChannel">
          <strong v-if="props.host.udpChannel.listening">
            客户机实际：正在监听 :{{ props.host.udpChannel.port }}
          </strong>
          <strong v-else>客户机实际：<span class="err-text">此刻没有监听</span></strong>

          <span class="muted small">{{ props.host.udpChannel.stateText || '—' }}</span>

          <span v-if="props.host.udpChannel.listening && !props.host.udpChannel.executeEnabled" class="badge warn">
            演练模式（只识别不执行）
          </span>
        </template>

        <template v-else>
          <strong class="muted">客户机实际：尚未上报</strong>
          <span class="muted small">
            这台机器的客户端版本较旧（不会上报通道状态），或中心刚重启、还没收到它的第一次上报。
            下面的「接收到的数据」可以现问一次。
          </span>
        </template>
      </div>

      <p v-if="!isAdmin" class="muted small" style="margin: 2px 0 0">
        只有<strong>管理员</strong>能下发指令（会让别的机器关机 / 改音量）。
      </p>

      <template v-else>
        <!-- 每个按钮下方那行小字＝**线路上真正发出去的报文文本**（明文短码）。
             按钮上是给人看的名字，短码才是客户机认的东西 —— 现场用 nc / 中控面板 / 别的程序
             手工发指令时，对着发的就是这行字。它**只做说明**：不可点、不参与任何交互。 -->
        <div class="udp-row">
          <div v-for="c in udpCommands" :key="c.command" class="udp-item">
            <button
              class="sm"
              :class="{ danger: c.needsConfirm, primary: armedUdp === c.command }"
              :disabled="udpBusy"
              :title="c.needsConfirm ? '会改变这台机器的状态，需再点一次确认' : '经 UDP 通道下发'"
              @click="sendUdp(c.command, c.label, c.needsConfirm)"
            >
              {{ armedUdp === c.command ? '确认执行？' : c.label }}
            </button>
            <span class="udp-code mono">{{ c.command }}</span>
          </div>
        </div>

        <p class="muted small" style="margin: 6px 0 0">
          按钮下方的<strong>小字</strong>就是这条指令实际发出去的<strong>报文原文</strong>
          （与现场用 nc 或别的程序手工发送时敲的内容完全一致，逐字节照抄）。
          通道是<strong>明文</strong>的（2026-10-13 起不再签名）：任意来源都能发，
          只在客户机的<strong>来源黑名单</strong>里被挡下。
          「发出去了」不等于「执行了」—— UDP 没有应答，真实的执行结果只看那台机器的日志。
          通道没启用 / 端口被防火墙挡住时，这里会显示"已发出"而机器毫无反应。
        </p>

        <p
          v-if="udpResult"
          class="small"
          :class="udpResult.kind === 'err' ? 'udp-err' : 'muted'"
          style="margin: 6px 0 0"
        >
          {{ udpResult.text }}
        </p>

        <p class="muted small" style="margin: 6px 0 0">
          ⚠️ UDP <strong>无应答</strong>：「已发出」<strong>不等于</strong>「已执行」。
          报文是<strong>明文</strong>的，对方有没有收到、有没有真的关机，要看那台机器上的客户端日志（<span class="mono">Udp</span> 分类）。
          通道端口、来源黑名单等在下面的「任务配置」里，由中心统一下发。
        </p>
      </template>

      <!-- ═══ 历史曲线 ═══ -->
      <div class="section-head">
        <span class="section-title">历史曲线</span>
        <div class="row-actions">
          <button
            v-for="r in RANGES"
            :key="r.key"
            class="sm"
            :class="{ primary: rangeKey === r.key }"
            @click="rangeKey = r.key"
          >
            {{ r.label }}
          </button>
          <button class="sm" :disabled="seriesLoading" @click="loadSeries">
            {{ seriesLoading ? '加载中…' : '刷新' }}
          </button>
        </div>
      </div>

      <p v-if="seriesError" class="notice err" style="margin-bottom: 10px">{{ seriesError }}</p>

      <template v-if="series && series.points.length > 0">
        <!--
          上方：硬件选择器（**勾选**，2026-10-15 起可多选）。
          ⚠️ 选择器**只列这台机器现在真的有的硬件** —— 列一个永远画不出线的条目
             比不列更糟（用户会以为那块硬盘坏了）。
          ⚠️ 同一型号的多块硬件，只有"真的在某一帧里并存过"才带 `#n`：一台只装了 1 块
             GTX 1060 的机器重启 6 次会留下 6 个 LUID，那是**同一块卡**，不该列成 6 条。
        -->
        <div class="device-picker">
          <div class="device-picker-row">
            <label class="device-chip" :class="{ active: isChecked(OVERVIEW) }">
              <input
                type="checkbox"
                :checked="isChecked(OVERVIEW)"
                @change="toggleChecked(OVERVIEW)"
              />
              <span class="device-chip-name">整机概览</span>
              <span class="device-chip-value">全部硬件合计</span>
            </label>
          </div>

          <div v-for="g in deviceGroups" :key="g.kind" class="device-picker-row">
            <span class="device-group">{{ g.label }}</span>

            <label
              v-for="d in g.devices"
              :key="d.id"
              class="device-chip"
              :class="{ active: isChecked(d.id) }"
              :title="d.keys.join('\n')"
            >
              <input type="checkbox" :checked="isChecked(d.id)" @change="toggleChecked(d.id)" />
              <span class="device-chip-name">{{ d.name }}</span>
              <span class="device-chip-value">{{ deviceHeadline(d) }}</span>
            </label>
          </div>
        </div>

        <!-- 一个硬件都没得选时，把原因写出来（日聚合 / 老客户端），别留一片空白 -->
        <p v-if="devicesUnavailableText" class="device-note">{{ devicesUnavailableText }}</p>

        <!--
          下方：曲线。勾了「整机概览」＝原来那四张图；
          勾了硬件＝**每个种类一张图**，该种类下勾中的硬件画在同一张图上做对比
          （颜色认硬件；磁盘的读写、网卡的上/下行用同色实线 + 虚线区分两条通道）。
        -->
        <div class="charts">
          <template v-if="isChecked(OVERVIEW)">
            <MetricsChart
              :points="series.points"
              :series="cpuMemSeries"
              :from="series.from"
              :to="series.to"
              :y-max="100"
              :axis-format="percentFormat"
              :value-format="percentFormat"
              :height="150"
              empty-text="这段时间没有 CPU / 内存采样"
            />

            <MetricsChart
              :points="series.points"
              :series="ioSeries"
              :from="series.from"
              :to="series.to"
              :axis-format="rateFormat"
              :value-format="rateFormat"
              :height="150"
              empty-text="这段时间没有磁盘 / 网络采样"
            />

            <MetricsChart
              :points="series.points"
              :series="gpuSeries"
              :from="series.from"
              :to="series.to"
              :y-max="100"
              :axis-format="percentFormat"
              :value-format="percentFormat"
              :height="120"
              empty-text="这段时间没有 GPU 采样（集成显卡或驱动未提供该计数器）"
            />

            <!-- 温度：只在真有读数（或历史上有过）时画曲线，否则说明为什么没有 -->
            <MetricsChart
              v-if="thermalOk || hasTempData"
              :points="series.points"
              :series="tempSeries"
              :from="series.from"
              :to="series.to"
              :axis-format="tempFormat"
              :value-format="tempFormat"
              :height="120"
              empty-text="这段时间没有温度采样"
            />
            <p v-else class="thermal-off">
              <strong>温度：{{ thermalStateLabel(thermalState) }}</strong>
              <span>{{ thermalDetail }}</span>
            </p>

            <!-- 图上没有 CPU 那条线时把原因整句写出来（服务端的诊断：枚举到哪颗 U、
                 有没有传感器、读数是否无效）。曲线区空间大，不必让人去悬停。
                 ⚠️ 必须放在上面那条 v-if/v-else 链**之后** —— 插在中间会打断 v-else 的配对。 -->
            <p v-if="thermalOk && cpuTempMissing" class="device-note">{{ thermalDetail }}</p>
          </template>

          <template v-for="c in checkedCharts" :key="c.kind">
            <!--
              勾中的硬件在窗口里一个值都没有：说清是哪一块没数据，而不是画一张空图。
              图上看不到线 ≠ 这块硬件一直是 0。
            -->
            <p v-if="!chartHasData(c)" class="device-note">
              {{ c.emptyText }}。这些硬件在窗口里出现过，但没有取到可画的读数。
            </p>

            <MetricsChart
              v-else
              :points="c.points"
              :series="c.series"
              :from="series.from"
              :to="series.to"
              :y-max="c.yMax"
              :axis-format="c.axisFormat"
              :value-format="c.axisFormat"
              :height="170"
              :empty-text="c.emptyText"
            />
          </template>

          <!-- 一个钩都没有时给一句指引，而不是留一片空白让人猜 -->
          <p v-if="!isChecked(OVERVIEW) && checkedCharts.length === 0" class="device-note">
            勾选上方的硬件即可查看曲线；同一种类勾中多块，会画在同一张图上做对比。
          </p>
        </div>

        <p class="muted small" style="margin: 6px 0 0">数据来源：{{ sourceText }}</p>

        <!--
          离线段说明（2026-10-16）：只在窗口里真有空白段时才出现 ——
          没有空白却挂一句"空白是什么意思"，是自找的噪声。
        -->
        <p v-if="gapNote" class="muted small" style="margin: 6px 0 0">
          {{ gapNote }}
          <br />
          {{ gapReason }}
        </p>
      </template>

      <div v-else-if="!seriesLoading" class="empty">
        <strong>这段时间没有数据</strong>
        可能这台机器当时离线，或客户端还没开始上报遥测。
      </div>

      <!-- ═══ 开关机记录 ═══ -->
      <div class="section-head">
        <span class="section-title">开关机 / 异常记录</span>
        <div class="row-actions">
          <button class="sm" :disabled="powerLoading" @click="loadPower(powerMore)">
            {{ powerLoading ? '加载中…' : '刷新' }}
          </button>
        </div>
      </div>

      <template v-if="power">
        <div class="stat-strip">
          <div class="stat"><span>开机</span><b>{{ power.stats.bootCount }}</b></div>
          <div class="stat"><span>正常关机</span><b>{{ power.stats.shutdownCount }}</b></div>
          <div class="stat"><span>计划重启/关机</span><b>{{ power.stats.restartCount }}</b></div>
          <div class="stat" :class="{ alarm: power.stats.unexpectedCount > 0 }">
            <span>发现异常关机</span><b>{{ power.stats.unexpectedCount }}</b>
          </div>
          <div class="stat" :class="{ alarm: power.stats.bsodCount > 0 }">
            <span>蓝屏</span><b>{{ power.stats.bsodCount }}</b>
          </div>
          <div class="stat">
            <span>平均运行时长</span>
            <b>{{ fmtUptimeHuman(power.stats.averageUptimeSeconds) }}</b>
          </div>
          <div class="stat">
            <span>最后一次开机</span>
            <b class="mono">{{ power.stats.lastBootAt ? fmtDateTime(power.stats.lastBootAt) : '—' }}</b>
          </div>
          <div class="stat">
            <span>最后一次关机</span>
            <b class="mono">
              {{ power.stats.lastShutdownAt ? fmtDateTime(power.stats.lastShutdownAt) : '—' }}
            </b>
          </div>
        </div>

        <p class="muted small" style="margin: 6px 0 10px">
          统计按<strong>全部历史</strong>计算（不随下面的条数变化）。
          事件时刻取的是事件日志写下该条记录的时间 —— 对「发现异常关机」（6008）来说那一刻是<strong>本次开机</strong>，
          真正的断电时刻只写在事件正文里，本项目刻意不把它解析成时间字段（正文随系统语言变化，
          解析失败会给出一个看着很合理的错时间）。
          下表「详情」列就是事件正文的<strong>原文</strong>（取不到正文时退化为事件的插入字符串），
          可拿它与事件查看器逐条对照。
          要完整核对请打开那台机器的「事件查看器 → Windows 日志 → 系统」。
        </p>

        <div v-if="power.items.length === 0" class="empty">
          <strong>没有记录</strong>
          <template v-if="!inventory?.inventory?.powerEventsSupported">
            该客户端所在平台还不支持读取开关机记录。
          </template>
          <template v-else>
            这台机器的事件日志里没有相关条目（日志可能已被清理，或本机从未重启过）。
          </template>
        </div>

        <div v-else class="table-wrap">
          <table class="grid">
            <thead>
              <tr>
                <th style="width: 96px">种类</th>
                <th style="width: 156px">记录时刻</th>
                <th style="width: 72px">事件 ID</th>
                <th style="width: 150px">发起者</th>
                <th>详情</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="(e, i) in power.items" :key="`${e.at}-${e.eventId}-${i}`">
                <td>
                  <span class="badge" :class="kindClass(e.kind)">{{ kindLabel(e.kind) }}</span>
                </td>
                <td class="mono">{{ fmtDateTime(e.at) }}</td>
                <td class="mono">{{ e.eventId ?? '—' }}</td>
                <td>{{ fmtText(e.actor) }}</td>
                <td class="muted ellipsis" :title="e.detail ?? ''">{{ fmtText(e.detail) }}</td>
              </tr>
            </tbody>
          </table>
        </div>

        <p v-if="!powerMore && power.total > power.items.length" class="muted small" style="margin-top: 6px">
          共 {{ power.total }} 条，当前显示最近 {{ power.items.length }} 条。
          <button class="sm" style="margin-left: 8px" @click="loadPower(true)">显示更多</button>
        </p>
        <p v-else class="muted small" style="margin-top: 6px">共 {{ power.total }} 条。</p>
      </template>

      <!-- ═══ 硬件清单 ═══ -->
      <div class="section-head">
        <span class="section-title">硬件清单</span>
        <div class="row-actions">
          <span v-if="inventory?.capturedAt" class="muted small">
            采集于 {{ fmtDateTime(inventory.capturedAt) }}（每 6 小时刷新一次）
          </span>
          <button class="sm" @click="toggleInventory">
            {{ inventoryOpen ? '收起' : '展开' }}
          </button>
        </div>
      </div>

      <!--
        折叠状态下也能看到的一句话摘要。显卡型号是这一块里最常被问的一项，
        而清单默认折叠 —— 让人为了看一眼型号先点开一大块不划算。

        ⚠️ 顺序（2026-10-14，需求③）：**处理器最左**。它是"这台机器是什么"的第一答案，
           而且 CPU 型号往往最长，放左边读数最顺；显卡这类"配置项"排在它后面。
      -->
      <p v-if="inventory?.inventory" class="inv-summary">
        <span class="inv-sum-key">处理器</span>
        <span>{{ fmtText(inventory.inventory.cpu?.name) }}</span>
        <span class="inv-sum-key">显卡</span>
        <strong :title="gpuSummary">{{ gpuSummary }}</strong>
        <span class="inv-sum-key">物理磁盘</span>
        <span>{{ inventory.inventory.disks.length }} 块</span>
        <span class="inv-sum-key">分区</span>
        <span>{{ inventory.inventory.volumes.length }} 个</span>
        <span class="inv-sum-key">网卡</span>
        <span>{{ detail?.info?.interfaces.length ?? 0 }} 块</span>
      </p>

      <div v-if="inventoryLoading" class="muted small">加载中…</div>

      <div v-else-if="!inventory || !inventory.capturedAt" class="empty">
        <strong>从未上报过硬件清单</strong>
        这台机器的客户端还没上报过（通常是客户端版本较旧，或刚注册、还没到第一次上报）。
      </div>

      <div v-else-if="!inventory.inventory" class="empty">
        <strong>清单已损坏</strong>
        上报过（{{ fmtDateTime(inventory.capturedAt) }}），但服务端保存的那份 JSON 解析不出来。
        等客户端下一次上报（最多 6 小时）会覆盖掉。
      </div>

      <template v-else-if="inventoryOpen">
        <div class="inv-grid">
          <div class="inv-block">
            <h4>处理器</h4>
            <div class="kv">
              <div><span class="muted">型号</span><span>{{ fmtText(inventory.inventory.cpu?.name) }}</span></div>
              <div>
                <span class="muted">核心 / 线程</span>
                <span>
                  {{ fmtCount(inventory.inventory.cpu?.physicalCores ?? null, '物理核') }} ·
                  {{ fmtCount(inventory.inventory.cpu?.logicalProcessors ?? null, '逻辑处理器') }}
                </span>
              </div>
              <div>
                <span class="muted">主频</span>
                <span>
                  {{ inventory.inventory.cpu?.baseClockMhz ?? '—' }} /
                  {{ inventory.inventory.cpu?.maxClockMhz ?? '—' }} MHz
                </span>
              </div>
              <div><span class="muted">插槽</span><span>{{ fmtText(inventory.inventory.cpu?.socket) }}</span></div>
            </div>
          </div>

          <div class="inv-block">
            <h4>操作系统</h4>
            <div class="kv">
              <div><span class="muted">版本</span><span>{{ fmtText(inventory.inventory.osVersion) }}</span></div>
              <div><span class="muted">构建</span><span class="mono">{{ fmtText(inventory.inventory.osBuild) }}</span></div>
              <div><span class="muted">架构</span><span>{{ fmtText(inventory.inventory.osArchitecture) }}</span></div>
              <div><span class="muted">域 / 工作组</span><span>{{ fmtText(inventory.inventory.domain) }}</span></div>
              <div>
                <span class="muted">上次开机</span>
                <span class="mono">
                  {{ inventory.inventory.bootTime ? fmtDateTime(inventory.inventory.bootTime) : '—' }}
                  <span class="muted small">（{{ fmtUptimeHuman(inventory.inventory.uptimeSeconds) }}）</span>
                </span>
              </div>
              <div><span class="muted">登录用户</span><span>{{ fmtText(inventory.inventory.loggedOnUser) }}</span></div>
            </div>
          </div>

          <div class="inv-block">
            <h4>内存（共 {{ fmtMb(inventory.inventory.totalMemoryMb) }}）</h4>
            <div v-if="inventory.inventory.memoryModules.length === 0" class="muted small">未采集到内存条信息。</div>
            <div v-else class="inv-scroll">
              <table class="grid">
                <thead>
                  <tr><th style="width: 72px">插槽</th><th style="width: 96px">容量</th><th style="width: 72px">频率</th><th>厂商 / 型号</th></tr>
                </thead>
                <tbody>
                  <tr v-for="(m, i) in inventory.inventory.memoryModules" :key="i">
                    <td class="mono">{{ fmtText(m.bank) }}</td>
                    <td>{{ fmtMb(m.capacityMb) }}</td>
                    <td class="mono">{{ m.speedMhz ?? '—' }}</td>
                    <td>
                      {{ fmtText(m.manufacturer) }}
                      <span class="muted small">{{ fmtText(m.partNumber) }}</span>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
          </div>

          <div class="inv-block">
            <h4>分区</h4>
            <div v-if="inventory.inventory.volumes.length === 0" class="muted small">未采集到分区信息。</div>
            <div v-else class="inv-scroll">
              <table class="grid">
                <thead>
                  <tr><th style="width: 62px">盘符</th><th style="width: 84px">卷标</th><th style="width: 62px">格式</th><th style="width: 120px">已用 / 总</th><th>占用</th></tr>
                </thead>
                <tbody>
                  <tr v-for="(v, i) in inventory.inventory.volumes" :key="i">
                    <td class="mono">{{ v.drive }}</td>
                    <td>{{ fmtText(v.label) }}</td>
                    <td>{{ fmtText(v.fileSystem) }}</td>
                    <td>
                      {{ fmtMb(v.totalMb === null || v.freeMb === null ? null : v.totalMb - v.freeMb) }} /
                      {{ fmtMb(v.totalMb) }}
                    </td>
                    <td>
                      <template v-if="diskUsedPercent(v.totalMb, v.freeMb) !== null">
                        {{ fmtPercent(diskUsedPercent(v.totalMb, v.freeMb)) }}
                      </template>
                      <span v-else class="muted">—</span>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
          </div>

          <div class="inv-block">
            <h4>物理磁盘</h4>
            <div v-if="inventory.inventory.disks.length === 0" class="muted small">未采集到磁盘信息。</div>
            <div v-else class="inv-scroll">
              <table class="grid">
                <thead>
                  <tr><th style="width: 48px">序号</th><th>型号</th><th style="width: 84px">容量</th><th style="width: 78px">类型</th><th style="width: 130px">序列号</th></tr>
                </thead>
                <tbody>
                  <tr v-for="(d, i) in inventory.inventory.disks" :key="i">
                    <td class="mono">{{ d.index ?? '—' }}</td>
                    <td>{{ fmtText(d.model) }}</td>
                    <td>{{ d.sizeGb === null ? '—' : `${d.sizeGb} GB` }}</td>
                    <td>{{ fmtText(d.mediaType) }}</td>
                    <td class="mono small">{{ fmtText(d.serial) }}</td>
                  </tr>
                </tbody>
              </table>
            </div>
          </div>

          <div class="inv-block">
            <h4>显卡 / 显示器</h4>
            <div v-if="inventory.inventory.gpus.length === 0" class="muted small">未采集到显卡信息。</div>
            <div v-else class="inv-scroll">
              <table class="grid">
                <thead>
                  <tr>
                    <th>型号</th>
                    <th style="width: 84px">显存</th>
                    <th style="width: 118px">驱动版本</th>
                    <th style="width: 86px">PCI</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="(g, i) in inventory.inventory.gpus" :key="i">
                    <!--
                      "具体型号"三件套：厂商（AdapterCompatibility）+ 芯片（VideoProcessor）+ PCI 号。
                      多卡**同型号**的机器上，只有 PCI 号能区分两块卡；老客户端这三项是 null，
                      显示成「—」而不是编一个。
                    -->
                    <td>
                      <div>{{ fmtText(g.videoProcessor ?? g.name) }}</div>
                      <div class="muted small">
                        {{ fmtText(g.vendor) }}
                        <span v-if="g.name && g.videoProcessor && g.name !== g.videoProcessor">
                          · 适配器名 {{ g.name }}
                        </span>
                      </div>
                    </td>
                    <td>{{ fmtMb(g.memoryMb) }}</td>
                    <td class="mono small">{{ fmtText(g.driverVersion) }}</td>
                    <td class="mono small">{{ fmtText(g.pciId) }}</td>
                  </tr>
                </tbody>
              </table>
            </div>

            <p class="muted small" style="margin: 8px 0 0">
              显示器：{{ inventory.inventory.displays.length }} 台。
              Windows 服务跑在 Session 0，<strong>取不到用户会话里的真实显示器</strong>，
              所以这里刻意留空而不是给一个假的分辨率。
            </p>
          </div>
        </div>
      </template>

      <!-- ═══ 资产台账 ═══ -->
      <div class="grid-2">
        <div class="kv">
          <div><span class="muted">机器码</span><span class="mono wrap">{{ detail.host.machineKey }}</span></div>
          <div><span class="muted">主机名</span><span>{{ fmtText(detail.info?.hostName ?? detail.host.hostName) }}</span></div>
          <div><span class="muted">操作系统</span><span>{{ fmtText(detail.info?.osDescription ?? detail.host.osDescription) }}</span></div>
          <div><span class="muted">CPU</span><span>{{ fmtText(cpuText) }}</span></div>
          <div>
            <span class="muted">处理器 / 内存</span>
            <span>
              {{ fmtCount(detail.info?.logicalProcessors ?? detail.host.logicalProcessors, '核') }}
              ·
              {{ fmtMemory(detail.info?.totalMemoryMb ?? detail.host.totalMemoryMb) }}
            </span>
          </div>
          <div><span class="muted">客户端版本</span><span class="mono">{{ fmtText(detail.host.agentVersion) }}</span></div>
          <div><span class="muted">注册时间</span><span class="mono">{{ fmtDateTime(detail.host.registeredAt) }}</span></div>
          <div>
            <span class="muted">最后心跳</span>
            <span class="mono">
              {{ detail.host.lastSeenAt ? fmtDateTime(detail.host.lastSeenAt) : '从未连接' }}
              <span class="muted small">（{{ fmtText(detail.host.lastAddress) }}）</span>
            </span>
          </div>
          <div>
            <span class="muted">长连接</span>
            <span>
              <span class="badge" :class="detail.connected ? 'ok' : ''">
                {{ detail.connected ? '此刻在线' : '此刻未连接' }}
              </span>
            </span>
          </div>
          <div v-if="detail.info?.userName">
            <span class="muted">登录用户</span><span>{{ detail.info.userName }}</span>
          </div>
        </div>

        <div>
          <div class="muted small" style="margin-bottom: 6px">网卡（注册时上报）</div>

          <div v-if="!detail.info || detail.info.interfaces.length === 0" class="muted small">
            注册时未上报网卡信息。
          </div>

          <div v-else class="table-wrap">
            <table class="grid">
              <thead>
                <tr>
                  <th>名称</th>
                  <th style="width: 130px">地址</th>
                  <th style="width: 150px">MAC</th>
                  <th style="width: 56px">状态</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="(nic, i) in detail.info.interfaces" :key="i">
                  <td>{{ fmtText(nic.name) }}</td>
                  <td class="mono">{{ fmtText(nic.address) }}</td>
                  <td class="mono">{{ fmtText(nic.mac) }}</td>
                  <td>
                    <span class="badge" :class="nic.isUp ? 'ok' : ''">{{ nic.isUp ? '已连接' : '未连接' }}</span>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>

          <div v-if="detail.host.note" class="muted small" style="margin-top: 10px">
            备注：{{ detail.host.note }}
          </div>
        </div>
      </div>

      <!-- 台账编辑（仅管理员） -->
      <div v-if="isAdmin" class="edit-row">
        <label>
          显示名
          <input v-model="form.displayName" type="text" maxlength="64" placeholder="例如：展厅正面墙" />
        </label>
        <label>
          分组
          <input v-model="form.group" type="text" maxlength="64" placeholder="例如：一层 / 户外" />
        </label>
        <label class="grow">
          备注
          <input v-model="form.note" type="text" maxlength="200" placeholder="位置、负责人、用途…" />
        </label>
        <button class="sm primary" :disabled="saving || !dirty" @click="save">
          {{ saving ? '保存中…' : dirty ? '保存' : '无改动' }}
        </button>
      </div>

      <!--
        ═══ 任务配置（M4.5）═══
        传 `detail.host` 而不是 `props.host`：前者是**刚刚从详情接口读回来的**，
        带着最新的 `configVersion` / `appliedConfigVersion`；
        列表那份快照要等父级刷新才会变，拿它显示"已应用版本"会一直落后。
      -->
      <!-- ⚠️ 传 `props.host`（在线表里那份，持续更新）而**不是** `detail.host`（展开那一刻的快照）：
           这一块要拿 `udpChannel` 做"已下发 vs 客户机实际"的对照，而那份状态每 10 秒随心跳更新。
           用快照会让那一行永远停在打开页面的那一刻 —— 恰好看不出"刚才点的那条指令为什么没生效"。 -->
      <AgentTaskConfigPanel
        :host="props.host"
        :is-admin="isAdmin"
        @notice="onConfigNotice"
        @refresh-host="() => void load()"
      />

      <!--
        ═══ 接收到的数据（2026-10-13）═══

        它回答的是"我明明下发了指令，这台机器为什么毫无反应"。
        中心这一侧只知道"我发出去了"，而**没收到 / 收到了认不出 / 认出了没执行**
        这三种故障在中心看来长得一模一样 —— 唯一的现场证据在客户机手上。

        ⚠️ 默认折叠、点开才拉（一次跨网往返，最多 8 秒）。理由见 script 里的注释。
      -->
      <div class="section-head" style="margin-top: 14px">
        <h3>接收到的数据</h3>
        <span class="muted small">这台机器实际收到了什么 —— 「指令没反应」先看这里</span>
        <span class="spacer"></span>
        <button class="sm" @click="toggleReceived">
          {{ receivedOpen ? '收起' : '展开' }}
        </button>
        <button v-if="receivedOpen" class="sm" :disabled="receivedLoading" @click="loadReceived">
          {{ receivedLoading ? '读取中…' : '刷新' }}
        </button>
      </div>

      <div v-if="receivedOpen" class="received-block">
        <p v-if="receivedLoading && !receivedData" class="muted small">正在向客户机要接收记录…</p>

        <p v-if="receivedError" class="notice err" style="margin: 0 0 8px">{{ receivedError }}</p>

        <template v-if="receivedData">
          <!-- 业务性失败（机器离线 / 客户端太旧）：就地一句人话，不是"系统出错"。 -->
          <p v-if="!receivedData.ok" class="notice warn" style="margin: 0 0 8px">
            {{ receivedData.message || '读不到这台客户机的接收记录。' }}
          </p>

          <template v-else>
            <div class="rx-strip">
              <span class="badge" :class="receivedData.udpListening ? 'ok' : 'warn'">
                {{ receivedData.udpListening ? `UDP 在听 :${receivedData.udpPort}` : 'UDP 未监听' }}
              </span>
              <span class="muted small">{{ receivedData.udpStateText || '—' }}</span>
            </div>

            <div class="muted small" style="margin: 4px 0 8px">
              客户端 {{ fmtText(detail.host.agentVersion) }} ·
              本次启动以来收到 <b>{{ receivedData.receivedCount }}</b> 条 ·
              启动于 {{ receivedData.startedAt ? fmtDateTime(receivedData.startedAt) : '—' }}
              <template v-if="!receivedData.udpExecuteEnabled">
                ｜<b class="warn-text">演练模式：收到的 UDP 指令只识别、不执行</b>
              </template>
            </div>

            <p
              v-if="!receivedData.entries || receivedData.entries.length === 0"
              class="muted small rx-empty"
            >
              {{
                receivedData.udpListening
                  ? '通道正在监听，但一条都还没收到 —— 说明报文没到达这台机器（查防火墙 / 网段 / 是不是发给了别的机器）。'
                  : '通道没在监听，所以什么都不会收到。先按上面那行的原因把通道起起来。'
              }}
            </p>

            <ul v-else class="rx-list">
              <li v-for="(e, i) in receivedData.entries" :key="`${e.at}-${i}`">
                <span class="mono muted small">{{ receivedClock(e.at) }}</span>
                <span class="badge" :class="receivedOutcomeClass(e.outcome)">
                  {{ receivedKindLabel(e.kind) }} · {{ receivedOutcomeLabel(e.outcome) }}
                </span>
                <div>
                  <div class="mono rx-raw">{{ e.raw === '' ? '(空报文)' : e.raw }}</div>
                  <div class="muted small">
                    {{ [e.source, e.detail].filter((v) => v && v.length > 0).join(' · ') }}
                  </div>
                </div>
              </li>
            </ul>
          </template>
        </template>
      </div>
    </template>
  </div>
</template>

<style scoped>
.detail {
  padding: 12px 2px 16px;
}

.grid-2 {
  display: grid;
  grid-template-columns: minmax(320px, 1fr) minmax(280px, 1fr);
  gap: 18px;
  align-items: start;
  margin-top: 14px;
  padding-top: 12px;
  border-top: 1px solid var(--border);
}

@media (max-width: 980px) {
  .grid-2 {
    grid-template-columns: 1fr;
  }
}

.kv > div {
  display: grid;
  grid-template-columns: 96px 1fr;
  gap: 8px;
  padding: 3px 0;
  font-size: 12px;
}

.kv .wrap {
  word-break: break-all;
}

/*
  值那一列必须能收缩 + 能断行（2026-10-14，需求③）。
  `.kv > div` 是 `96px 1fr` 的两列网格，而 `1fr` 的最小宽度默认是内容宽度 ——
  一个长型号（"Intel(R) Core(TM) i5-8265U CPU @ 1.60GHz" 这种）会把轨道顶宽，
  在两列布局里把整块撑破。`min-width: 0` 才允许它收缩。
*/
.kv > div > span:last-child {
  min-width: 0;
  overflow-wrap: anywhere;
}

/* ── 实时指标条 ── */
.live-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 22px;
  padding: 9px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--panel);
}

.live-item {
  display: flex;
  align-items: baseline;
  gap: 6px;
  font-size: 12px;
}

.live-label {
  color: var(--muted);
}

.live-value {
  font-family: var(--mono);
  font-weight: 500;
}

.live-sub {
  color: var(--muted);
  font-size: 11px;
}

/* 温度状态词："未启用"是正常状态，保持中性；"驱动加载失败"才是真异常，标红。 */
.live-thermal .live-value.thermal-failed {
  color: var(--err);
}

/* ── 小节标题 ── */
.section-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
  margin: 18px 0 8px;
}

.section-title {
  font-weight: 600;
  font-size: 13px;
}

/* 远程指令（UDP 通道）按钮行：窄屏自动折行，别挤出横向滚动条。 */
.udp-row {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-start;
  gap: 6px 10px;
}

/* 一个按钮 + 它下方那行"实际发出去的报文"：竖着排，短码与按钮居中对齐。
   折行时**以按钮为单位整体换行**，不会出现"按钮在这一行、短码跑到下一行"。 */
.udp-item {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 2px;
}

/* 报文原文本身（`DOWN` / `RESTART` / `+` / `-` / `0` / `1`）。
   ⚠️ 它必须**逐字节**照抄线上发的内容：现场排障时是拿它去和客户端日志、和手工发的报文
   对照的，所以刻意不做任何美化（比如把 `-` 换成排版更好看的 `−`）。 */
.udp-code {
  font-size: 10px;
  line-height: 1.2;
  color: var(--muted);
  /* 只是文本，但允许点一下就整段选中，省得拖拽选那一个字符。 */
  user-select: all;
}

.udp-err {
  color: var(--err);
}

/* ── 「客户机实际」那一行（2026-10-14）──
   三态各有底色，且**"尚未上报"刻意用中性灰**：它不是故障。
   详见 udpLiveClass 的注释。 */
.udp-live {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin: 4px 0 6px;
  padding: 5px 8px;
  border: 1px solid var(--border);
  border-left-width: 3px;
  border-radius: var(--radius);
  font-size: 12px;
}

.udp-live.ok {
  border-left-color: var(--ok);
  background: var(--ok-soft);
}

.udp-live.warn {
  border-left-color: var(--warn);
  background: var(--warn-soft);
}

.udp-live.unknown {
  border-left-color: var(--border-strong);
  background: var(--panel-soft);
}

.udp-live .err-text {
  color: var(--err);
}

.charts {
  display: grid;
  gap: 10px;
}

/* ═══════════════════════════════════════════════════════════════
   硬件选择器（M4.8）—— 曲线上方那排可点选的硬件
   ═══════════════════════════════════════════════════════════════ */

.device-picker {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-bottom: 10px;
}

.device-picker-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}

/* 种类名（处理器 / 磁盘 / 网卡 / 显卡）。固定宽度让各行的按钮左边缘对齐。 */
.device-group {
  width: 44px;
  flex: none;
  font-size: 12px;
  color: var(--muted);
}

/*
 * 一格硬件。做成 `<label>` + `<input type=checkbox>`（2026-10-15 从按钮改过来）：
 *   · 键盘可达是白捡的（Tab 到复选框、空格切换），不必再自己补 role/tabindex；
 *   · **勾选**替代了原来的单选高亮 —— 同一种类勾中几块，曲线就画在同一张图里。
 * 整格都可点（点文字＝点复选框），所以 cursor: pointer 留在外层。
 */
.device-chip {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 4px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--panel);
  color: var(--text);
  font-size: 12px;
  line-height: 1.5;
  cursor: pointer;
  text-align: left;
  user-select: none;
}

.device-chip:hover {
  border-color: var(--border-strong);
}

/* 勾选态：用主色边框 + 主色名 + 浅主色底，不靠深色填充（浅底上填色容易把文字对比度压没）。 */
.device-chip.active {
  border-color: var(--accent);
  background: var(--accent-soft);
}

.device-chip.active .device-chip-name {
  color: var(--accent);
  font-weight: 600;
}

/* 勾选框尺寸压到与 12px 正文相称，并跟随主题主色。 */
.device-chip input[type='checkbox'] {
  width: 13px;
  height: 13px;
  margin: 0;
  flex: none;
  accent-color: var(--accent);
  cursor: pointer;
}

/*
 * 型号名可能很长（`Samsung SSD 980 PRO 1TB`），限宽截断而不是把整行撑爆。
 * ⚠️ 网卡名是「描述（别名）」，**别名在尾部**，截断会先丢掉它 —— 所以
 *    每一格都带 `title`（完整名 + 它用过的全部实例 Key），悬停即可看全。
 */
.device-chip-name {
  max-width: 300px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* 读数用等宽字体：几块硬件的数字要对齐着看，比例字体下会跳。 */
.device-chip-value {
  flex: none;
  color: var(--muted);
  font-family: var(--mono);
}

/* 硬件选择器不存在 / 某块硬件没数据时的那句话。 */
.device-note {
  margin: 0 0 10px;
  padding: 8px 10px;
  border: 1px dashed var(--border);
  border-radius: var(--radius);
  font-size: 12px;
  line-height: 1.6;
  color: var(--muted);
}

/* 温度不可用时的说明块（代替曲线）。用虚线边框：它是一句"这里本该有图"的占位。 */
.thermal-off {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin: 0;
  padding: 10px 12px;
  border: 1px dashed var(--border);
  border-radius: var(--radius);
  background: var(--panel);
  font-size: 12px;
  color: var(--muted);
}

.thermal-off strong {
  color: var(--text);
  font-weight: 600;
}

/* ═══════════════════════════════════════════════════════════════
   「接收到的数据」（2026-10-13）
   ═══════════════════════════════════════════════════════════════ */

.received-block {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

/* 通道状态那一行：徽章 + 一句人话。 */
.rx-strip {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  padding: 6px 10px;
  background: var(--panel);
  border: 1px solid var(--border);
  border-radius: var(--radius);
}

.rx-empty {
  margin: 0;
  padding: 12px;
  text-align: center;
  border: 1px dashed var(--border);
  border-radius: var(--radius);
}

/* 记录列表：等宽 + 限高可滚 —— 200 条记录不能把页面顶得没边。 */
.rx-list {
  list-style: none;
  margin: 0;
  padding: 0;
  max-height: 320px;
  overflow: auto;
  border: 1px solid var(--border);
  border-radius: var(--radius);
}

.rx-list li {
  display: grid;
  grid-template-columns: 74px 108px 1fr;
  gap: 8px;
  align-items: baseline;
  padding: 5px 10px;
  border-bottom: 1px solid var(--border);
  font-size: 12px;
}

.rx-list li:last-child {
  border-bottom: none;
}

/* 报文原文：**原样**显示（空格、不可见字符都留着）—— 那正是要排查的东西。 */
.rx-raw {
  word-break: break-all;
  font-size: 12px;
}

.warn-text {
  color: var(--warn);
}

.thermal-off span {
  line-height: 1.5;
}

/* ── 统计条 ── */
.stat-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.stat {
  flex: 1 1 116px;
  min-width: 108px;
  padding: 7px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--panel);
  font-size: 12px;
}

.stat span {
  display: block;
  color: var(--muted);
  font-size: 11px;
}

.stat b {
  font-size: 15px;
}

.stat.alarm {
  border-color: var(--err);
  background: var(--err-soft);
}

.stat.alarm b {
  color: var(--err);
}

/* ── 硬件清单 ── */
/* 折叠状态下的一句话摘要：一行铺开，窄屏自动折行（不做省略号 —— 型号被截断就没用了）。 */
.inv-summary {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 4px 8px;
  margin: -2px 0 8px;
  padding: 7px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--panel);
  font-size: 12px;
}

.inv-sum-key {
  color: var(--muted);
  font-size: 11px;
}

.inv-sum-key + span,
.inv-sum-key + strong {
  margin-right: 8px;
}

/*
  两列（2026-10-14，需求③）：**一横排两块硬件**。
  ⚠️ 轨道用 `minmax(0, 1fr)` 而不是 `1fr` —— 默认的 `1fr` 是 `minmax(auto, 1fr)`，
     最小宽度会被内容撑开，块里那张 `white-space: nowrap` 的表格能把轨道顶破，
     结果就是"右边的卡片被挤出面板"。`min` 设成 0 才允许真的收缩，
     收缩之后表格由 `.inv-scroll` 自己横向滚。
*/
.inv-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 14px;
}

@media (max-width: 900px) {
  .inv-grid {
    grid-template-columns: minmax(0, 1fr);
  }
}

.inv-block {
  min-width: 0;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--panel);
  padding: 10px 12px;
}

/*
  块内的表格横向滚动。清单里的表格是 `nowrap` 的（型号 / 序列号不该被折行），
  两列之后每块只有半个面板宽 —— 不滚就会把整块撑出边界。
*/
.inv-scroll {
  overflow-x: auto;
}

.inv-block h4 {
  margin: 0 0 8px;
  font-size: 12px;
  font-weight: 600;
  color: var(--muted);
}

.ellipsis {
  max-width: 320px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.edit-row {
  display: flex;
  gap: 12px;
  align-items: flex-end;
  flex-wrap: wrap;
  margin-top: 14px;
  padding-top: 12px;
  border-top: 1px solid var(--border);
}

.edit-row label {
  margin: 0;
}

.edit-row .grow {
  flex: 1 1 220px;
  min-width: 180px;
}

.edit-row input {
  min-width: 140px;
}
</style>
