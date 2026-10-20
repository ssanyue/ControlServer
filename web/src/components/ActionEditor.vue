<script setup lang="ts">
/**
 * 单条动作的编辑卡片。
 *
 * 直接就地修改传入的 `action` 对象（它来自编辑面板的 draft 深拷贝），
 * 不重建对象 —— 这样服务端多出来、前端类型里还没声明的字段不会被抹掉。
 *
 * 卡片头右侧除了移动 / 复制 / 删除，还有一个「执行」按钮：把**这一条**动作
 * 单独发出去，便于一条一条联调。它的位置固定在「↑」左边（同一组操作按钮里，
 * 位置最靠前），这样多张卡片上的执行按钮纵向对齐，扫一眼就能连点几条。
 */
import { computed, onUnmounted, ref, watch } from 'vue'
import { api } from '../api'
import { commandLibrary } from '../commandLibrary'
import type { ActionStepDto, ButtonAction, CommandItem, EncodePreviewDto } from '../types'
import { LICENSE_BLOCKED_HINT, useLicenseBlocked } from '../licenseGate'

const props = withDefaults(
  defineProps<{
    action: ButtonAction
    index: number
    /** 子按钮集里 WoL 会被服务端强制降级为 UDP，所以直接不给选。 */
    allowWol?: boolean
    /** 该动作来自子按钮（目标 IP/端口由所属集合统一提供，逐条改会被覆盖）。 */
    sharedTarget?: boolean
    /** 宿主名称（按钮名 / 子按钮名），用于单条执行时的展示名与日志。 */
    ownerLabel?: string
    /** 子按钮场景：所属集合的目标地址（单条执行时必须用它覆盖动作自身的旧值）。 */
    overrideIp?: string
    overridePort?: number
  }>(),
  { allowWol: true, sharedTarget: false, ownerLabel: '', overrideIp: undefined, overridePort: undefined },
)

const emit = defineEmits<{
  (e: 'move', delta: number): void
  (e: 'duplicate'): void
  (e: 'remove'): void
}>()

/**
 * 展开 / 收起。**默认收起**（`true`）—— 一个按钮动辄十几条动作，全展开时
 * 光滚动就要翻好几屏，根本看不到下面还有没有别的动作。收起后只留一行标题
 * （序号 + 名称 + 协议/类型/延迟徽标 + 操作按钮），扫一眼就知道整组动作长什么样。
 *
 * 之所以不用 props 控制：`ActionListEditor` 里每个卡片都带 `:key="item.id"`，
 * 所以**切换选中的按钮 / 子按钮集时会整棵重挂载**，折叠状态自然回到"全收起"，
 * 不需要在父层维护一份折叠态。
 */
const collapsed = ref(true)
const previewing = ref(false)
const preview = ref<EncodePreviewDto | null>(null)
const previewError = ref('')

// ── 单条执行 ──
const running = ref(false)
const runResult = ref<ActionStepDto | null>(null)
const runError = ref('')

/**
 * 未授权时「执行」置灰。
 *
 * 这条路径同样落在 `/api/actions/execute`（用的是内联 `{ actions: [...] }`），
 * 因此与看板上的按钮一样会被服务端以 403 拒绝。在这里也提前禁用，
 * 是为了不让用户在编辑页反复试一条注定被拒的动作、还以为是自己配错了。
 */
const blocked = useLicenseBlocked()

const isWol = computed(() => props.action.protocol === 'WoL')
const isLibrary = computed(() => props.action.dataType === '指令库')
const title = computed(() => props.action.name?.trim() || `动作 ${props.index + 1}`)

/** 单条执行的展示名，与后端按 `actionId` 执行时的命名规则一致（`宿主 · 动作名`）。 */
const runLabel = computed(() =>
  props.ownerLabel?.trim() ? `${props.ownerLabel.trim()} · ${title.value}` : title.value,
)

// ═══════════════════════════════════════════════════════════════
// WoL（网络唤醒）—— 可编辑的只剩 MAC
//
// 网络唤醒的目标与端口**由协议定死**：目标是广播、端口是 9，这是后端
// `DataEncoder.NormalizeWolHost` / `DataEncoder.DefaultWolPort` 的语义；
// 数据类型更是完全不参与发送 —— `SendWakeOnLanAsync` 走的是
// `TryParseMac` + `BuildMagicPacket`，从头到尾没读过 `DataType`。
//
// 所以这三个控件不再渲染：留着只会让人以为"可以改"，而真改了要么毫无作用
// （数据类型），要么把魔术包发去一个不该去的地方（目标 / 端口）。
// ═══════════════════════════════════════════════════════════════

/** 与后端 `DataEncoder` 逐字对齐：默认端口 9，空目标 / 0.0.0.0 一律视为广播。 */
const WOL_BROADCAST = '255.255.255.255'
const WOL_DEFAULT_PORT = 9
/** 魔术包长度：6 字节 0xFF + MAC 重复 16 次。 */
const WOL_MAGIC_LENGTH = 6 + 16 * 6

/**
 * 旧版 / 外部导入的配置里可能存着非默认的目标或端口。
 * 界面已经不给编辑入口，但**后端仍会用它** —— 所以要显式告警，不能装作没看见。
 */
const wolStoredHost = computed(() => {
  const ip = (props.action.targetIp ?? '').trim()
  return !ip || ip === '0.0.0.0' || ip === WOL_BROADCAST ? '' : ip
})

const wolStoredPort = computed(() => {
  const port = Number(props.action.targetPort ?? 0)
  return Number.isFinite(port) && port > 0 && port !== WOL_DEFAULT_PORT ? port : 0
})

const wolStaleTarget = computed(
  () => isWol.value && (!!wolStoredHost.value || !!wolStoredPort.value),
)

/** 归位成协议默认：空目标 = 广播、端口 0 = 9（都是后端读取时的口径）。 */
function resetWolTarget() {
  props.action.targetIp = ''
  props.action.targetPort = 0
}

/**
 * 切到 WoL 时把目标 / 端口就地归位。
 *
 * 不归位的话，从 UDP 切过来的动作会把原目标的 IP / 端口留在**看不见**的地方
 * 继续生效 —— 那就成了"界面没说、实际这么做"，比多两个控件更误导。
 *
 * 数据类型的值**不动**：WoL 不读它，留着能让切回 UDP 时恢复原样。
 * 只有「指令库」例外 —— 指令库本身不含 WoL（见 `CommandLibraryService`），
 * 这个组合永远解析不出结果，一并清掉。
 *
 * 切回 UDP / TCP 时**不补值**：那时目标框重新可见、显示为空，由用户填。
 * 可见的空值不算误导，凭空猜一个 IP 才是。
 */
watch(
  () => props.action.protocol,
  (protocol, previous) => {
    if (protocol !== 'WoL' || previous === 'WoL') return
    resetWolTarget()
    if (props.action.dataType === '指令库') props.action.dataType = 'String'
  },
)

// ═══════════════════════════════════════════════════════════════
// 指令库选择器 —— **选中即物化**（把指令的定义填进动作，不再保留引用）
//
// 这里刻意**不提供自由文本输入**：动作里的「指令名」是给后端去指令库查表的键，
// 手打一个库里没有的名字，界面上看不出任何异常，直到真发送时才报
// 「指令库解析失败：未找到指令 X」（见 ActionExecutionService）。
//
// 为什么选中后要**写入**而不是只存名字：导出的配置文件只带 `settings`，
// 指令库是独立配置键、**不参与导入导出**（见 `ConfigKeys.Commands`）。
// 于是引用式动作导出后只剩一个指令名，搬到指令库不同的机器上会
// 「找不到 ⇒ 发送失败」或更糟的「同名不同义 ⇒ 静默发错数据」。
// 物化后动作自包含，导出即自带全部发送参数。
//
// 但**不能静默清空**旧值 —— 历史配置里可能引用着已被删除/改名的指令，
// 那种值要照样显示出来并标注「库里已不存在」，让用户自己决定重选还是保留。
// ═══════════════════════════════════════════════════════════════

const isAscii = computed(() => props.action.dataType === 'ASCII')

const pickerOpen = ref(false)
const pickerRoot = ref<HTMLElement | null>(null)
const searchBox = ref<HTMLInputElement | null>(null)
const query = ref('')
/** 键盘高亮项在 `filteredCommands` 里的下标。 */
const highlight = ref(0)

/**
 * 刚从指令库**填入**的即时反馈（`null` = 本轮编辑没填过）。
 * 只用于界面提示、**不落库** —— 物化之后动作就是普通动作，不留任何"来源"字段。
 */
const filledFrom = ref<{
  name: string
  protocol: string
  dataType: string
  data: string
} | null>(null)

/** 当前值在库里命中的那条指令（大小写不敏感，与后端一致）。 */
const selectedCommand = computed(() => commandLibrary.findByName(props.action.dataContent))

/** 值非空但库里查不到 —— 旧的引用，或者指令已被删/改名。 */
const commandMissing = computed(
  () => isLibrary.value
    && !!props.action.dataContent?.trim()
    && commandLibrary.loaded.value
    && !selectedCommand.value,
)

const filteredCommands = computed<CommandItem[]>(() => {
  const key = query.value.trim().toLowerCase()
  const all = commandLibrary.commands.value

  if (!key) return all
  return all.filter((c) => (c.name ?? '').toLowerCase().includes(key))
})

// 切到「指令库」类型时才去拉库（缓存是共享的，十几张卡片也只有一次真实请求）。
watch(
  isLibrary,
  (v) => {
    if (v) void commandLibrary.ensure()
  },
  { immediate: true },
)

// 换类型就没必要留着下拉。
watch(
  () => props.action.dataType,
  () => closePicker(),
)

function onDocumentMouseDown(e: MouseEvent) {
  const el = pickerRoot.value
  if (el && !el.contains(e.target as Node)) closePicker()
}

function openPicker() {
  if (!isLibrary.value || pickerOpen.value) return

  pickerOpen.value = true
  query.value = ''
  // 让高亮落在当前值上：直接回车就是"确认原来那条"，不会莫名其妙跳到第一条。
  const idx = filteredCommands.value.findIndex(
    (c) => (c.name ?? '').trim().toLowerCase() === (props.action.dataContent ?? '').trim().toLowerCase(),
  )
  highlight.value = idx >= 0 ? idx : 0

  document.addEventListener('mousedown', onDocumentMouseDown, true)
  // 等下拉渲染出来再聚焦，否则 input 还不存在。
  window.setTimeout(() => searchBox.value?.focus(), 0)
}

function closePicker() {
  if (!pickerOpen.value) return

  pickerOpen.value = false
  document.removeEventListener('mousedown', onDocumentMouseDown, true)
}

function chooseCommand(item: CommandItem) {
  materializeCommand(item)
  closePicker()
}

/**
 * 把指令库条目的定义**物化**进动作：协议 / 数据类型 / 数据内容 一次写齐。
 *
 * 与后端 `ActionExecutionService.ApplyCommand` 是**同一件事**，区别只在时机 ——
 * 这里发生在**编辑期**，后端那次发生在运行时。物化后动作的 `dataType` 已不是「指令库」，
 * 后端直接走普通发送分支，因此**执行路径一行都不用改**。
 *
 * 为什么必须有这一步：导出的配置文件只带 `settings`，指令库刻意独立成键、不参与导入导出。
 * 于是「指令库」引用式动作导出后**只剩一个指令名** —— 搬到指令库不同的机器上，
 * 目标库没有同名指令则发送时报「指令库解析失败」，同名但定义不同则**静默发错数据**。
 * 物化让动作自带全部发送参数，这两个问题一起消失。
 */
function materializeCommand(item: CommandItem) {
  // 协议为空时保留动作自身的协议 —— 与后端 `ApplyCommand` 的回退口径一致。
  props.action.protocol = item.protocol?.trim() ? item.protocol : props.action.protocol
  props.action.dataType = item.dataType
  props.action.dataContent = item.data ?? ''

  filledFrom.value = {
    name: item.name,
    protocol: props.action.protocol,
    dataType: item.dataType,
    data: item.data ?? '',
  }
}

/**
 * 旧配置里的「引用式」动作就地转为独立动作 —— 等价于"重新选一次当前命中的那条"。
 * 库里已查不到这条指令时**不渲染**这个按钮（见模板的三分支）：不能凭空造出数据。
 */
function convertLegacyRef() {
  const hit = selectedCommand.value
  if (hit) materializeCommand(hit)
}

/** 反馈行里显示的实际填入值（`协议 · 类型 · 数据`，数据过长截断）。 */
const filledSummary = computed(() => {
  const f = filledFrom.value
  if (!f) return ''
  const data = f.data.length > 24 ? `${f.data.slice(0, 24)}…` : f.data
  return `${f.protocol} · ${f.dataType}${data ? ` · ${data}` : ''}`
})

function onSearchKeydown(e: KeyboardEvent) {
  const list = filteredCommands.value

  if (e.key === 'Escape') {
    e.preventDefault()
    closePicker()
    return
  }

  if (e.key === 'ArrowDown') {
    e.preventDefault()
    if (list.length > 0) highlight.value = (highlight.value + 1) % list.length
    return
  }

  if (e.key === 'ArrowUp') {
    e.preventDefault()
    if (list.length > 0) highlight.value = (highlight.value - 1 + list.length) % list.length
    return
  }

  if (e.key === 'Enter') {
    // 必须拦：否则在 fieldset 里回车有触发"保存"这类默认行为的风险。
    e.preventDefault()
    const picked = list[highlight.value]
    if (picked) chooseCommand(picked)
  }
}

/** 高亮项变化时把它滚进可视区（列表可能几十条）。 */
function onHighlighted(el: unknown) {
  if (el instanceof HTMLElement) el.scrollIntoView({ block: 'nearest' })
}

onUnmounted(() => document.removeEventListener('mousedown', onDocumentMouseDown, true))

/** 下拉里显示的一行摘要：这条指令实际会发什么。 */
function commandSummary(item: CommandItem): string {
  const data = item.data ?? ''
  const shown = data.length > 24 ? `${data.slice(0, 24)}…` : data
  return `${item.protocol} · ${item.dataType}${shown ? ` · ${shown}` : ''}`
}

// 切换协议/类型/内容/目标后旧预览就失效了，避免"看着 UDP 的结果在配 WoL"；
// 执行结果同理 —— 显示着上一条配置的"发送成功"比不显示更危险。
watch(
  () => [
    props.action.protocol,
    props.action.dataType,
    props.action.dataContent,
    props.action.targetIp,
    props.action.targetPort,
    props.overrideIp,
    props.overridePort,
  ],
  () => {
    preview.value = null
    previewError.value = ''
    runResult.value = null
    runError.value = ''
  },
)

// ── WoL 魔术包预览（纯前端，与后端 `DataEncoder.BuildMagicPacket` 逐字对齐）──
//
// 刻意**不走编码端点**：那个端点的输入是「数据类型 + 内容」，而 WoL 压根不看
// 数据类型。数据类型已经被界面隐藏，再让预览依赖它，就会因为一个残留的 `Hex`
// 报出一条与真实发送毫无关系的「Hex 非法」—— 预览反而成了新的误导源。

/** 与后端 `TryParseMac` 同口径：去掉 `:` `-` `.` 与空白后，必须正好 12 位十六进制。 */
function parseMacHex(raw: string | null | undefined): number[] | null {
  const cleaned = (raw ?? '').replace(/[:.\-\s]/g, '')
  if (cleaned.length !== 12 || !/^[0-9a-fA-F]{12}$/.test(cleaned)) return null

  const bytes: number[] = []
  for (let i = 0; i < 6; i += 1) bytes.push(parseInt(cleaned.slice(i * 2, i * 2 + 2), 16))
  return bytes
}

const toCompactHex = (bytes: number[]): string =>
  bytes.map((b) => b.toString(16).padStart(2, '0')).join('').toUpperCase()

/** 构造 `EncodePreviewDto`，让结果复用既有的预览面板（字段口径同后端）。 */
function buildWolPreview(): EncodePreviewDto {
  const raw = (props.action.dataContent ?? '').trim()
  if (!raw) {
    return {
      dataType: 'WoL',
      text: '',
      hex: '',
      hexCompact: '',
      length: 0,
      ansiEncoding: '',
      error: '还没填 MAC 地址 —— 需要 12 位十六进制，例如 AA:BB:CC:DD:EE:FF',
    }
  }

  const mac = parseMacHex(raw)
  if (!mac) {
    return {
      dataType: 'WoL',
      text: '',
      hex: '',
      hexCompact: '',
      length: 0,
      ansiEncoding: '',
      // 与后端失败文案逐字一致（`SendWakeOnLanAsync`：`MAC 地址格式无效: {值}`）。
      error: `MAC 地址格式无效: ${raw}`,
    }
  }

  const packet = new Array<number>(6).fill(0xff)
  for (let i = 0; i < 16; i += 1) packet.push(...mac)

  const host = wolStoredHost.value || WOL_BROADCAST
  const port = wolStoredPort.value || WOL_DEFAULT_PORT

  return {
    dataType: 'WoL',
    text: `魔术包 → ${host}:${port}`,
    hex: packet.map((b) => b.toString(16).padStart(2, '0')).join(' ').toUpperCase(),
    hexCompact: toCompactHex(packet),
    length: packet.length,
    ansiEncoding: '',
    error: null,
  }
}

async function doPreview() {
  previewing.value = true
  previewError.value = ''
  preview.value = null

  try {
    preview.value = isWol.value
      ? buildWolPreview()
      : await api.previewEncode(props.action.dataType, props.action.dataContent)
  } catch (e) {
    previewError.value = e instanceof Error ? e.message : String(e)
  } finally {
    previewing.value = false
  }
}

/**
 * 这条动作**实际会发往**的地址（2026-10-14，需求④）。
 *
 * 子按钮的目标地址不在动作上（它是**集合**的属性），所以这里显示 `overrideIp` / `overridePort`
 * —— 与保存后执行、以及上面「单条执行」用的是**同一个来源**。
 * 反过来显示动作自己那份旧值会误导：那份值根本不参与发送。
 */
const sharedIpText = computed(() => props.overrideIp?.trim() || '（集合里未填 IP）')

const sharedPortText = computed(() =>
  props.overridePort === undefined || props.overridePort === null || Number.isNaN(props.overridePort)
    ? '（集合里未填端口）'
    : String(props.overridePort),
)

const sharedTargetText = computed(() => `所属集合：${sharedIpText.value} : ${sharedPortText.value}`)

/**
 * 滚到并聚焦「共享目标地址」里的目标 IP 输入框。
 *
 * 用 DOM id 而不是逐层 emit：这个按钮埋在
 * SubSetForm → SubButtonEditor → ActionListEditor → ActionEditor 四层里，
 * 为一次滚动在每一层加一个事件是四倍的噪音，而每一层都不关心里面到底要干什么。
 */
function gotoSetAddress() {
  const el = document.getElementById('sub-set-target-ip')

  if (!(el instanceof HTMLInputElement)) return

  el.scrollIntoView({ block: 'center', behavior: 'smooth' })
  el.focus({ preventScroll: true })
}

/**
 * 单独把这一条动作发出去。
 *
 * ⚠️ 走的是**内联动作**那条路（`{ actions: [...] }`），而不是服务端的 `actionId`。
 * 原因：编辑页改的是草稿，屏幕上这一条很可能**还没保存** —— 用 `actionId` 会发出
 * "已保存的旧版本"，那正是点这个按钮时最不想看到的结果（改完试发，发的却是旧数据）。
 *
 * 两个后果要讲在明处：
 *   · 单条执行时**本条的「延迟」不生效**（服务端规则是"第 1 条不等待"，
 *     而单条执行时它就是第 1 条），所以下面会显式提示，免得以为延迟没配上；
 *   · 子按钮的动作，目标 IP/端口由所属集合提供（与保存后执行的语义一致），
 *     此时动作自己那份旧 IP 不参与发送。
 */
async function runSingle() {
  if (running.value) return
  const sharedIp = props.overrideIp?.trim()
  if (props.sharedTarget && !sharedIp) {
    runError.value = '所属集合的目标 IP 为空，请先在「共享目标地址」里填写后再试。'
    return
  }

  running.value = true
  runError.value = ''
  runResult.value = null

  const a = props.action

  try {
    const res = await api.executeActions({
      label: runLabel.value,
      overrideIp: props.sharedTarget ? sharedIp : undefined,
      overridePort: props.sharedTarget ? props.overridePort : undefined,
      actions: [
        {
          name: a.name,
          targetIp: a.targetIp,
          targetPort: a.targetPort,
          protocol: a.protocol,
          dataType: a.dataType,
          dataContent: a.dataContent,
          delayMs: a.delayMs,
          pairingCode: a.pairingCode,
          pairingCodeType: a.pairingCodeType,
        },
      ],
    })

    const step = res.steps?.[0]
    if (!step) {
      runError.value = '服务端没有返回执行结果。'
      return
    }

    if (step.ok) runResult.value = step
    else runError.value = step.message || '发送失败（没有更多说明）。'
  } catch (e) {
    runError.value = e instanceof Error ? e.message : String(e)
  } finally {
    running.value = false
  }
}
</script>

<template>
  <div class="action-card">
    <div class="card-head">
      <button
        class="sm ghost"
        type="button"
        :title="collapsed ? '展开' : '收起'"
        @click="collapsed = !collapsed"
      >
        {{ collapsed ? '▸' : '▾' }}
      </button>
      <span class="idx">#{{ props.index + 1 }}</span>
      <strong class="name">{{ title }}</strong>

      <span class="badge">{{ props.action.protocol }}</span>
      <span class="badge">{{ props.action.dataType }}</span>
      <span v-if="props.action.delayMs > 0" class="badge warn">延迟 {{ props.action.delayMs }}ms</span>

      <span class="spacer" />

      <!--
        「执行」在「↑」左边 —— 单条联调是这个卡片里最常用的动作，放在操作组最前，
        多张卡片纵向对齐，可以连着点几条看收发日志。
      -->
      <button
        class="sm run"
        type="button"
        :title="blocked ? LICENSE_BLOCKED_HINT : `单独执行本动作（立即发送）`"
        :disabled="running || blocked"
        @click="runSingle"
      >
        {{ running ? '发送中…' : '执行' }}
      </button>
      <button class="sm" type="button" title="上移" @click="emit('move', -1)">↑</button>
      <button class="sm" type="button" title="下移" @click="emit('move', 1)">↓</button>
      <button class="sm" type="button" title="复制本条" @click="emit('duplicate')">复制</button>
      <button class="sm danger" type="button" title="删除本条" @click="emit('remove')">删除</button>
    </div>

    <!--
      单条执行的结果就地显示在卡片头上方（不在折叠区里）：
      收起状态点「执行」也要能看到结果，否则按钮看起来像没反应。
    -->
    <div v-if="runError" class="notice err small run-note">
      <strong>发送失败</strong>
      <span>{{ runError }}</span>
    </div>

    <div v-else-if="runResult" class="notice ok small run-note">
      <strong>已发送</strong>
      <span class="mono">{{ runResult.bytesSent }} 字节</span>
      <span class="mono">{{ runResult.protocol }} → {{ runResult.targetIp }}:{{ runResult.targetPort }}</span>
      <span v-if="runResult.resolvedFromCommand" class="mono">
        经指令库「{{ runResult.resolvedFromCommand }}」
      </span>
      <span v-if="props.action.delayMs > 0" class="muted">
        （单条执行时本条 {{ props.action.delayMs }}ms 延迟不生效）
      </span>
    </div>

    <div v-if="!collapsed" class="card-body">
      <div class="grid-2">
        <div>
          <label>动作名称（为空时日志里显示协议名）</label>
          <input v-model="props.action.name" placeholder="例如：开灯" />
        </div>
        <div>
          <label>协议</label>
          <select v-model="props.action.protocol">
            <option value="UDP">UDP</option>
            <option value="TCP">TCP</option>
            <option v-if="props.allowWol" value="WoL">WoL（网络唤醒）</option>
          </select>
        </div>
      </div>

      <!--
        ── WoL（网络唤醒）：可编辑的只剩 MAC ──
        目标（广播）、端口（9）、数据类型这三项在 WoL 下没有意义：前两个由协议定死，
        第三个发送路径根本不读。不渲染它们，是为了不让用户以为"可以改"。
      -->
      <template v-if="isWol">
        <div class="grid-2">
          <div>
            <label>MAC 地址</label>
            <input v-model="props.action.dataContent" placeholder="AA:BB:CC:DD:EE:FF" />
          </div>
          <div>
            <label>延迟（毫秒）</label>
            <input v-model.number="props.action.delayMs" type="number" min="0" />
          </div>
        </div>

        <p class="muted small" style="margin: 4px 0 0">
          网络唤醒固定发往广播 <span class="mono">{{ WOL_BROADCAST }}:{{ WOL_DEFAULT_PORT }}</span>
          （魔术包 = 6×FF + MAC×16，共 {{ WOL_MAGIC_LENGTH }} 字节）——
          不需要目标、端口与数据类型。
        </p>

        <!-- 旧版 / 外部导入留下的非默认目标：界面不给编辑了，但后端仍会用它 -->
        <p v-if="wolStaleTarget" class="notice warn small" style="margin: 6px 0 0">
          这条动作里还存着非默认的目标
          <span class="mono">{{ wolStoredHost || WOL_BROADCAST }}:{{ wolStoredPort || WOL_DEFAULT_PORT }}</span>
          （旧版或外部导入留下的值）—— 界面不再提供编辑，但实际发送仍会用它。
          <button class="sm" type="button" style="margin-left: 6px" @click="resetWolTarget()">
            归位为广播 {{ WOL_BROADCAST }}:{{ WOL_DEFAULT_PORT }}
          </button>
        </p>
      </template>

      <template v-else>
        <div class="grid-3">
          <div>
            <label>目标 IP</label>
            <input v-if="!props.sharedTarget" v-model="props.action.targetIp" placeholder="192.168.1.100" />
            <!--
              子按钮：IP / 端口由所属集合统一提供（2026-10-14，需求④）。
              ⚠️ 这里**不再是禁用输入框**：灰掉的可编辑控件看着像"控件坏了"，
                 而它其实根本不是这一层能决定的值。改成只读文本 + 一个跳过去的按钮。
            -->
            <div v-else class="shared-ro" :title="sharedTargetText">{{ sharedIpText }}</div>
          </div>
          <div>
            <label>端口</label>
            <input
              v-if="!props.sharedTarget"
              v-model.number="props.action.targetPort"
              type="number"
              min="0"
              max="65535"
            />
            <div v-else class="shared-ro" :title="sharedTargetText">{{ sharedPortText }}</div>
          </div>
          <div>
            <label>延迟（毫秒）</label>
            <input v-model.number="props.action.delayMs" type="number" min="0" />
          </div>
        </div>

        <p v-if="props.sharedTarget" class="muted small shared-note">
          目标 IP 与端口由所属<strong>子按钮集</strong>统一提供 —— 单条动作改了不生效（保存时会被集合的值覆盖）。
          <button class="sm" type="button" @click="gotoSetAddress()">去设置集合地址</button>
        </p>

        <div class="grid-2">
          <div>
            <label>数据类型</label>
            <select v-model="props.action.dataType">
              <option value="String">String（UTF-8 文本）</option>
              <option value="Hex">Hex（十六进制）</option>
              <option value="ANSI">ANSI（本机代码页 GBK）</option>
              <option value="ASCII">ASCII（非 ASCII 字符替换为 ?）</option>
              <option value="指令库">指令库（选中指令后自动填入协议与数据）</option>
            </select>
          </div>
          <div>
            <label>{{ isLibrary ? '指令（从指令库中选择）' : '数据内容' }}</label>

            <!-- 指令库：只能从库里选，不给自由文本（见脚本区注释的理由） -->
            <div v-if="isLibrary" ref="pickerRoot" class="cmd-picker">
              <button
                type="button"
                class="cmd-picker-trigger"
                :class="{ open: pickerOpen, empty: !props.action.dataContent }"
                @click="pickerOpen ? closePicker() : openPicker()"
              >
                <span class="cmd-picker-text">
                  <template v-if="selectedCommand">{{ selectedCommand.name }}</template>
                  <template v-else-if="props.action.dataContent">{{ props.action.dataContent }}</template>
                  <template v-else>点击选择指令</template>
                </span>
                <span class="cmd-picker-caret">{{ pickerOpen ? '▴' : '▾' }}</span>
              </button>

              <div v-if="pickerOpen" class="cmd-picker-pop">
                <input
                  ref="searchBox"
                  v-model="query"
                  class="cmd-picker-search"
                  type="text"
                  placeholder="搜索指令名…"
                  @keydown="onSearchKeydown"
                />

                <p v-if="commandLibrary.loading.value" class="muted small pad">读取指令库中…</p>

                <p v-else-if="commandLibrary.error.value" class="notice err small pad">
                  读取指令库失败：{{ commandLibrary.error.value }}
                </p>

                <p v-else-if="commandLibrary.commands.value.length === 0" class="muted small pad">
                  指令库还是空的 —— 先到「指令库」页新增一条，再回来引用它。
                </p>

                <p v-else-if="filteredCommands.length === 0" class="muted small pad">
                  没有匹配「{{ query }}」的指令。
                </p>

                <ul v-else class="cmd-picker-list">
                  <li
                    v-for="(c, i) in filteredCommands"
                    :key="c.id"
                    :ref="(el) => (i === highlight ? onHighlighted(el) : undefined)"
                    class="cmd-picker-item"
                    :class="{ active: i === highlight, picked: c.name === selectedCommand?.name }"
                    @mousemove="highlight = i"
                    @click="chooseCommand(c)"
                  >
                    <span class="name">{{ c.name }}</span>
                    <span class="meta mono">{{ commandSummary(c) }}</span>
                  </li>
                </ul>
              </div>
            </div>

            <input v-else v-model="props.action.dataContent" placeholder="Hello" />
          </div>
        </div>
      </template>

      <!-- ── 刚从指令库填入的即时反馈（物化之后动作已是普通动作）── -->
      <p v-if="filledFrom" class="notice ok small" style="margin: 6px 0 0">
        已从指令库「{{ filledFrom.name }}」填入
        <span class="mono">{{ filledSummary }}</span>
        —— 这条动作现在是独立动作，导出到别的程序也能原样发出。
        要换一条：把数据类型选回「指令库」再选一次即可。
      </p>

      <!-- ── 仍在用「指令库」引用方式：旧配置，或刚切过来还没选 ──
           WoL 分支里没有上面的选择器（而且指令库本来就不含 WoL），所以不在这里提示。 -->
      <div v-if="isLibrary && !isWol" class="notice warn small" style="margin: 6px 0 0">
        <template v-if="commandMissing">
          指令库中没有「{{ props.action.dataContent }}」这条指令（可能已被删除或改名），
          无法填入 —— 请从上面重新选一条。
        </template>

        <template v-else-if="selectedCommand">
          本动作仍是「指令库」引用：只存了一个指令名。
          导出到指令库不同的机器上，会发送失败、甚至发出别的数据。
          当前指向 <span class="mono">「{{ selectedCommand.name }}」{{ commandSummary(selectedCommand) }}</span>
          <button
            class="sm"
            type="button"
            style="margin-left: 6px"
            @click="convertLegacyRef()"
          >
            填入协议与数据（转为独立动作）
          </button>
        </template>

        <template v-else>
          「指令库」引用方式只在动作里存一个指令名 —— 导出到指令库不同的机器上会发送失败或发错数据。
          请从上面选一条指令：选中后会自动填入协议、数据类型与数据内容。
        </template>
      </div>

      <div class="grid-2">
        <div>
          <label>对接码（监听匹配用，可留空）</label>
          <input v-model="props.action.pairingCode" />
        </div>
        <div>
          <label>对接码比较方式</label>
          <select v-model="props.action.pairingCodeType">
            <option value="String">String</option>
            <option value="UTF-8">UTF-8</option>
            <option value="Hex">Hex</option>
          </select>
        </div>
      </div>

      <div class="row-actions">
        <button class="sm" type="button" :disabled="previewing" @click="doPreview">
          {{ previewing ? '解析中…' : isWol ? '预览魔术包' : '预览实际字节' }}
        </button>
        <span v-if="isAscii" class="muted small">
          ASCII 只认 0–127：中文等字符会被替换成半角问号 ?（0x3F），且不可逆
        </span>
      </div>

      <div v-if="previewError" class="notice err small" style="margin-top: 8px">{{ previewError }}</div>

      <div
        v-if="preview"
        class="notice small"
        :class="preview.error ? 'err' : 'info'"
        style="margin-top: 8px"
      >
        <template v-if="preview.error">
          <strong>{{ isWol ? '无法生成魔术包' : '无法编码' }}</strong><span>{{ preview.error }}</span>
        </template>
        <template v-else>
          <span class="mono">{{ preview.length }} 字节</span>
          <span v-if="isWol" class="mono">
            魔术包 = 6×FF + MAC×16 → {{ wolStoredHost || WOL_BROADCAST }}:{{ wolStoredPort || WOL_DEFAULT_PORT }}
          </span>
          <span class="mono">HEX {{ preview.hexCompact }}</span>
          <span v-if="preview.ansiEncoding" class="mono">ANSI {{ preview.ansiEncoding }}</span>
        </template>
      </div>
    </div>
  </div>
</template>

<style scoped>
.action-card {
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--panel-soft);
  padding: 8px 10px;
}

.action-card + .action-card {
  margin-top: 8px;
}

.card-head {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.card-head .spacer {
  flex: 1 1 12px;
}

.card-head .name {
  font-size: 13px;
  font-weight: 500;
}

.card-head .idx {
  font-family: var(--mono);
  font-size: 11px;
  color: var(--muted);
}

button.ghost {
  border-color: transparent;
  background: none;
  padding: 2px 5px;
  color: var(--muted);
}

/* 「执行」用描边强调：它是动作而不是"整理"类操作（移动/复制/删除），
   但不能用 solid primary —— 一张卡片里最该被点的是它旁边的输入框，不是发送。 */
button.run {
  border-color: var(--accent);
  color: var(--accent);
}

button.run:hover:not(:disabled) {
  background: var(--accent-soft);
}

.run-note {
  margin-top: 8px;
}

.card-body {
  margin-top: 8px;
  border-top: 1px dashed var(--border);
  padding-top: 4px;
}

.grid-2 {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 0 12px;
}

.grid-3 {
  display: grid;
  grid-template-columns: 1.4fr 0.8fr 0.9fr;
  gap: 0 12px;
}

@media (max-width: 780px) {
  .grid-2,
  .grid-3 {
    grid-template-columns: 1fr;
  }
}

/*
  子按钮动作里"目标 IP / 端口"的**只读展示**（2026-10-14，需求④）。
  刻意长得像输入框（同样的 padding / 圆角 / 高度）却又是虚线 + 灰底：
  位置对得上、一眼看出"这两项不在这层"，又不会让人以为是可以点进去改的控件。
*/
.shared-ro {
  display: flex;
  align-items: center;
  min-height: 32px;
  padding: 6px 10px;
  border: 1px dashed var(--border);
  border-radius: 6px;
  background: var(--panel-soft);
  color: var(--muted);
  font-family: var(--mono);
  font-size: 12px;
  overflow-wrap: anywhere;
}

.shared-note {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  margin: 4px 0 0;
}

.row-actions {
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
  margin-top: 10px;
}

/* ═══ 指令库选择器 ═══
   `position: relative` 是下拉定位的锚点；`z-index` 让下拉盖住后面那张动作卡片
   （卡片本身没有定位、也没建立层叠上下文，所以这里给个整数就够）。 */
.cmd-picker {
  position: relative;
}

.cmd-picker-trigger {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  text-align: left;
  background: var(--panel);
  border-color: var(--border-strong);
}

.cmd-picker-trigger .cmd-picker-text {
  flex: 1 1 auto;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* 未选任何指令时用灰色，与"已选中一个真实指令"区分开。 */
.cmd-picker-trigger.empty .cmd-picker-text {
  color: var(--muted);
}

.cmd-picker-trigger .cmd-picker-caret {
  color: var(--muted);
  font-size: 10px;
}

.cmd-picker-pop {
  position: absolute;
  z-index: 20;
  left: 0;
  right: 0;
  top: calc(100% + 2px);
  background: var(--panel);
  border: 1px solid var(--border-strong);
  border-radius: var(--radius);
  box-shadow: 0 6px 20px rgba(16, 24, 40, 0.12);
  padding: 6px;
}

.cmd-picker-pop .pad {
  margin: 4px 2px;
}

.cmd-picker-search {
  width: 100%;
  margin-bottom: 4px;
}

/* 列表限高：指令再多也不能把整页顶下去。 */
.cmd-picker-list {
  list-style: none;
  margin: 0;
  padding: 0;
  max-height: 220px;
  overflow-y: auto;
}

.cmd-picker-item {
  display: flex;
  flex-direction: column;
  gap: 1px;
  padding: 4px 6px;
  border-radius: 4px;
  cursor: pointer;
}

/* 键盘高亮与鼠标悬停共用一套视觉（鼠标移动时会同步 highlight）。 */
.cmd-picker-item.active {
  background: var(--accent-soft);
}

.cmd-picker-item .name {
  font-size: 13px;
}

.cmd-picker-item .meta {
  font-size: 11px;
  color: var(--muted);
}

/* 当前已选中的那条：左侧加一道竖条，滚动列表时一眼能找到自己在哪。 */
.cmd-picker-item.picked {
  box-shadow: inset 2px 0 0 var(--accent);
  padding-left: 8px;
}
</style>
