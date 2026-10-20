<script setup lang="ts">
/**
 * 客户机「任务配置」区块（M4.5 · BootAssistant 对齐）。
 *
 * 这一块回答的问题是：**这台机器除了"能连上"，还被要求干哪些事？**
 *   · 延时 / 循环发送 —— 开机延时发一次，或每隔一段重发；
 *   · 定时发送       —— 每天在若干个 HH:mm 各发一次；
 *   · 延时启动程序   —— 在**用户会话里**依次拉起程序（执行者是托盘，不是服务）；
 *   · UDP 指令通道   —— 它反过来收指令（关机等），是本项目唯一的入站端口。
 *
 * 🔥 **事实来源是服务端**（决策 D5）：这一块编辑的是服务端库里那份配置，
 *    保存即"版本 +1 并通知客户机来拉"。客户机本地只落一份**只读缓存**，
 *    没有任何"本地覆盖"入口 —— 那会制造"网页显示一份、机器实际跑另一份"。
 *
 * ⚠️ **前端不做校验**：允许/拒绝的判据只有一份，在 Core 的 `AgentConfigRules` 里
 *    （服务端保存前与客户端应用前共用）。前端把用户填的东西原样送上去，
 *    再把服务端返回的问题清单**原样显示**。各写一份判据的下场是
 *    "服务端放行、客户端拒绝"，表现为"保存成功但机器没动"。
 *
 * ⚠️ **保存后一律以服务端返回的配置为准重新渲染**（而不是拿本地那棵对象继续用）：
 *    服务端会做**静默归一化**（`9:5` → `09:05`、空格去掉、协议名大写、重复时刻去重排序），
 *    不重新渲染的话"我明明填了两个时刻，列表里只显示一个"就永远看不出来。
 */
import { computed, onUnmounted, ref, watch } from 'vue'
import { api } from '../api'
import type { AgentConfigDto, AgentConfigSaveRequest, AgentHostDto } from '../types'
import {
  AGENT_CONFIG_LIMITS,
  AGENT_DATA_TYPES,
  AGENT_SEND_PROTOCOLS,
  agentConfigVersionClass,
  agentConfigVersionLabel,
  agentConfigVersionState,
} from '../types'
import { fmtDateTime } from '../format'

const props = defineProps<{
  host: AgentHostDto
  isAdmin: boolean
}>()

const emit = defineEmits<{
  (e: 'notice', kind: 'ok' | 'err' | 'warn', text: string): void
  /** 请求父级重读主机详情 —— 「已应用版本」由那边下发，本组件拿不到新的。 */
  (e: 'refresh-host'): void
}>()

// ═══════════════════════════════════════════════════════════════
// 本地视图模型
//
// 与 DTO 刻意分开：`times` / `blockedSourceIps` 在界面上是**一行文本**
// （逗号或空格分隔），而协议里是数组。用一层映射把"怎么让人填"和"怎么上线"隔开，
// 免得每次输入都去做 split/join 的舞蹈。
// ═══════════════════════════════════════════════════════════════

interface SendRow {
  id: string
  name: string
  protocol: string
  targetIp: string
  targetPort: number
  dataType: string
  data: string
  delaySeconds: number
  isLoop: boolean
  loopIntervalSeconds: number
  enabled: boolean
}

interface ScheduledRow {
  id: string
  name: string
  protocol: string
  targetIp: string
  targetPort: number
  dataType: string
  data: string
  /** `HH:mm` 的文本形态（逗号 / 空格分隔）。 */
  timesText: string
  enabled: boolean
}

interface LaunchRow {
  id: string
  name: string
  programPath: string
  argument: string
  delaySeconds: number
  enabled: boolean
}

const udpEnabled = ref(true)
const udpPort = ref(8090)
const udpBlockedText = ref('')

/**
 * 「已下发（中心）」与「客户机实际」是否对不上（2026-10-14）。
 *
 * 🔥 这是"指令不生效"最直接的判据。在它之前，中心只有"我下发的配置"这一个视角，
 * 于是"配置明明开着"成了一个死循环 —— 没人会想到去怀疑"机器其实没在听"。
 *
 * ⚠️ <b>客户机还没上报时不判不一致</b>（`udpChannel` 为 null ⇒ 恒 false）：
 *    那是"不知道"，不是"不一致"。把它算成不一致，会在中心刚重启后的头十几秒里
 *    给每一台机器都亮一条黄条 —— 而它们全都好好的。
 */
const udpMismatch = computed(() => {
  const live = props.host.udpChannel

  if (!live) return false
  if (udpEnabled.value !== live.listening) return true

  // 都在听，但端口不一样 —— 代发指令会发到一个没人听的端口上。
  return live.listening && live.port !== null && Number(udpPort.value) !== live.port
})

const sendRows = ref<SendRow[]>([])
const scheduledRows = ref<ScheduledRow[]>([])
const launchRows = ref<LaunchRow[]>([])

const version = ref(0)
const updatedAt = ref<string | null>(null)

const loading = ref(false)
const saving = ref(false)
const error = ref('')
/** 上一次操作的结果（成功/失败都显示在保存条旁边 —— 失败信息可能很长，别只做成一闪而过的提示）。 */
const result = ref<{ kind: 'ok' | 'err'; text: string } | null>(null)

/** 已加载过（决定要不要在展开时自动拉一次）。 */
const loaded = ref(false)

/**
 * 载入时的快照（用与提交**完全相同**的序列化方式算出来）。
 * 这样"有没有未保存的改动"是拿同一把尺子量的 —— 少一处口径差异就少一次"看着没改却说改了"。
 */
const baseline = ref('')

// ═══════════════════════════════════════════════════════════════
// 序列化 / 反序列化
// ═══════════════════════════════════════════════════════════════

/**
 * 生成一个任务 Id（前端生成，服务端会 `EnsureId` 兜底）。
 *
 * ⚠️ **不用 `crypto.randomUUID()`**：它要求**安全上下文**，
 * 而这套页面在局域网里是以 `http://<内网IP>:8899` 访问的 —— 那里 `crypto.randomUUID`
 * 直接是 `undefined`，会抛异常。用不依赖任何浏览器能力的拼法最省事。
 */
let idSeq = 0
function newTaskId(): string {
  idSeq += 1
  return `t${Date.now().toString(36)}-${idSeq.toString(36)}-${Math.random().toString(36).slice(2, 8)}`
}

/** 时刻 / IP 这类"一格多值"的文本 → 数组。全角逗号分号也认。 */
function splitTokens(text: string): string[] {
  return text
    .split(/[\s,，;；、]+/)
    .map((t) => t.trim())
    .filter((t) => t.length > 0)
}

function applyConfig(config: AgentConfigDto) {
  version.value = config.version
  updatedAt.value = config.updatedAt

  udpEnabled.value = config.udp?.enabled ?? true
  udpPort.value = config.udp?.port ?? 8090
  udpBlockedText.value = (config.udp?.blockedSourceIps ?? []).join(' ')

  sendRows.value = (config.sendTasks ?? []).map((t) => ({
    id: t.id,
    name: t.name ?? '',
    protocol: t.protocol,
    targetIp: t.targetIp,
    targetPort: t.targetPort,
    dataType: t.dataType,
    data: t.data,
    delaySeconds: t.delaySeconds,
    isLoop: t.isLoop,
    loopIntervalSeconds: t.loopIntervalSeconds,
    enabled: t.enabled,
  }))

  scheduledRows.value = (config.scheduledTasks ?? []).map((t) => ({
    id: t.id,
    name: t.name ?? '',
    protocol: t.protocol,
    targetIp: t.targetIp,
    targetPort: t.targetPort,
    dataType: t.dataType,
    data: t.data,
    timesText: (t.times ?? []).join(', '),
    enabled: t.enabled,
  }))

  launchRows.value = (config.launchTasks ?? []).map((t) => ({
    id: t.id,
    name: t.name,
    programPath: t.programPath,
    argument: t.argument,
    delaySeconds: t.delaySeconds,
    enabled: t.enabled,
  }))
}

/**
 * 本地 → 请求体。
 *
 * 四块**全发**（本面板在编辑全部四块）。空数组是"清空那一块"，
 * 与"不传 = 不动那一块"是两件不同的事 —— 见 `AgentConfigSaveRequest` 的注释。
 */
function buildPatch(): AgentConfigSaveRequest {
  return {
    udp: {
      enabled: udpEnabled.value,
      port: Number(udpPort.value) || 0,
      blockedSourceIps: splitTokens(udpBlockedText.value),
    },
    sendTasks: sendRows.value.map((r) => ({
      id: r.id,
      name: r.name.trim() === '' ? null : r.name.trim(),
      protocol: r.protocol,
      targetIp: r.targetIp,
      targetPort: Number(r.targetPort) || 0,
      dataType: r.dataType,
      data: r.data,
      delaySeconds: Number(r.delaySeconds) || 0,
      isLoop: r.isLoop,
      loopIntervalSeconds: Number(r.loopIntervalSeconds) || 0,
      enabled: r.enabled,
    })),
    scheduledTasks: scheduledRows.value.map((r) => ({
      id: r.id,
      name: r.name.trim() === '' ? null : r.name.trim(),
      protocol: r.protocol,
      targetIp: r.targetIp,
      targetPort: Number(r.targetPort) || 0,
      dataType: r.dataType,
      data: r.data,
      times: splitTokens(r.timesText),
      enabled: r.enabled,
    })),
    launchTasks: launchRows.value.map((r) => ({
      id: r.id,
      name: r.name,
      programPath: r.programPath,
      argument: r.argument,
      delaySeconds: Number(r.delaySeconds) || 0,
      enabled: r.enabled,
    })),
  }
}

const dirty = computed(() => baseline.value !== '' && JSON.stringify(buildPatch()) !== baseline.value)

/**
 * 改过配置但客户机还没应用上 —— 这种状态值得**自动复查**。
 *
 * 不复查的话，运维保存完看到"尚未应用"就会以为失败、又点一次保存，
 * 于是版本号白白涨一格、客户机多拉一次配置。这里每 10 秒重读一次主机快照，
 * 最多 2 分钟（再久就不是"刚下发"的问题了，该让运维自己判断）。
 */
const PENDING_POLL_MS = 10_000
const PENDING_POLL_MAX = 12
let pendingTimer: number | null = null
let pendingTicks = 0

function stopPendingPoll() {
  if (pendingTimer !== null) window.clearTimeout(pendingTimer)
  pendingTimer = null
  pendingTicks = 0
}

function startPendingPoll() {
  stopPendingPoll()
  schedulePendingTick()
}

function schedulePendingTick() {
  pendingTimer = window.setTimeout(() => {
    pendingTicks += 1
    emit('refresh-host')
    if (pendingTicks < PENDING_POLL_MAX) schedulePendingTick()
    else stopPendingPoll()
  }, PENDING_POLL_MS)
}

// ═══════════════════════════════════════════════════════════════
// 加载 / 保存 / 重新下发
// ═══════════════════════════════════════════════════════════════

async function load(force = false) {
  if (loading.value) return
  if (loaded.value && !force) return

  loading.value = true
  error.value = ''

  try {
    applyConfig(await api.agentConfig(props.host.id))
    loaded.value = true
    baseline.value = JSON.stringify(buildPatch())
  } catch (e) {
    error.value = e instanceof Error ? e.message : '加载配置失败'
  } finally {
    loading.value = false
  }
}

function discard() {
  // 用"从服务端读回来的那份"重放一次：本地那次编辑就此消失。
  const patch = JSON.parse(baseline.value) as AgentConfigSaveRequest

  applyConfig({
    version: version.value,
    updatedAt: updatedAt.value,
    udp: patch.udp ?? { enabled: true, port: 8090, blockedSourceIps: [] },
    sendTasks: patch.sendTasks ?? [],
    scheduledTasks: patch.scheduledTasks ?? [],
    launchTasks: patch.launchTasks ?? [],
  })

  result.value = { kind: 'ok', text: '已放弃未保存的改动。' }
}

async function save() {
  if (saving.value || !dirty.value) return

  saving.value = true
  result.value = null

  try {
    const saved = await api.saveAgentConfig(props.host.id, buildPatch())

    if (saved.config) applyConfig(saved.config)

    baseline.value = JSON.stringify(buildPatch())

    const text =
      `配置已保存并下发（v${version.value}）。客户机收到通知后会自己拉取；` +
      '它回报"已应用"之后这里才会显示为已生效。'

    result.value = { kind: 'ok', text }
    emit('notice', 'ok', `已保存并下发配置 v${version.value}。`)

    // 立刻要一次主机快照：让"已下发 / 已应用"两个数字都尽快对齐。
    emit('refresh-host')
    startPendingPoll()
  } catch (e) {
    // 400 = 校验没过。服务端把**全部**问题用「；」连起来放在 message 里，
    // 原样显示即可（自己再拼一遍只会漏掉几条）。
    const text = e instanceof Error ? e.message : '保存失败'
    result.value = { kind: 'err', text }
    emit('notice', 'err', text)
  } finally {
    saving.value = false
  }
}

/** 重新下发：要求客户机**不看版本号**立刻重拉一次配置（现场排障用）。 */
async function redeliver() {
  if (saving.value) return

  saving.value = true
  result.value = null

  try {
    const r = await api.redeliverAgentConfig(props.host.id)
    result.value = { kind: 'ok', text: r.message }
    emit('notice', 'ok', r.message)
    emit('refresh-host')
  } catch (e) {
    const text = e instanceof Error ? e.message : '重新下发失败'
    result.value = { kind: 'err', text }
    emit('notice', 'err', text)
  } finally {
    saving.value = false
  }
}

// ═══════════════════════════════════════════════════════════════
// 列表增删改 · 上下排序
// ═══════════════════════════════════════════════════════════════

function addSend() {
  if (sendRows.value.length >= AGENT_CONFIG_LIMITS.maxTasksPerList) return

  sendRows.value.push({
    id: newTaskId(),
    name: '',
    protocol: 'UDP',
    targetIp: '',
    targetPort: 9000,
    dataType: 'String',
    data: '',
    delaySeconds: 0,
    isLoop: false,
    loopIntervalSeconds: 60,
    enabled: true,
  })
}

function addScheduled() {
  if (scheduledRows.value.length >= AGENT_CONFIG_LIMITS.maxTasksPerList) return

  scheduledRows.value.push({
    id: newTaskId(),
    name: '',
    protocol: 'UDP',
    targetIp: '',
    targetPort: 9000,
    dataType: 'String',
    data: '',
    timesText: '09:00',
    enabled: true,
  })
}

function addLaunch() {
  if (launchRows.value.length >= AGENT_CONFIG_LIMITS.maxTasksPerList) return

  launchRows.value.push({
    id: newTaskId(),
    name: '',
    programPath: '',
    argument: '',
    delaySeconds: 5,
    enabled: true,
  })
}

/**
 * 上移 / 下移。
 *
 * ⚠️ 对「延时启动程序」来说**顺序会改变行为**：多个任务的延时是**串行累加**的
 * （第一条相对于客户机启动，后面的相对于前一条完成），顺序就是现场要的启动次序。
 * 对两张发送清单则只是"让列表好读"。
 */
function move<T>(rows: T[], index: number, delta: number): void {
  const target = index + delta
  if (target < 0 || target >= rows.length) return

  const [item] = rows.splice(index, 1)
  rows.splice(target, 0, item)
}

/**
 * 删除一行 —— 两步式确认（与用户管理页 / 客户端列表同一套交互）。
 * 这里刻意不用 `window.confirm`：原生弹窗在"批量调整任务"的节奏里极其打断，
 * 而这个页面已经有一套统一的"再点一次确认"的做法。
 */
const armedRemove = ref<string | null>(null)

function removeRow(rows: { id: string }[], id: string) {
  if (armedRemove.value !== id) {
    armedRemove.value = id
    return
  }

  armedRemove.value = null

  const index = rows.findIndex((r) => r.id === id)
  if (index >= 0) rows.splice(index, 1)
}

// ═══════════════════════════════════════════════════════════════
// 展示辅助
// ═══════════════════════════════════════════════════════════════

/** 已应用版本的对照结论。`host` 来自父级（列表 / 详情的快照）。 */
const versionState = computed(() =>
  agentConfigVersionState(version.value, props.host.appliedConfigVersion),
)

const versionBadgeClass = computed(() => agentConfigVersionClass(versionState.value))
const versionBadgeLabel = computed(() => agentConfigVersionLabel(versionState.value))

const appliedText = computed(() =>
  props.host.appliedConfigVersion === null ? '未回报' : `v${props.host.appliedConfigVersion}`,
)

/** 版本不一致时那一句解释 —— 三种"不一致"的原因完全不同，不能共用一句话。 */
const versionNote = computed(() => {
  switch (versionState.value) {
    case 'pending':
      return (
        `已下发 v${version.value}，客户机目前应用的还是 v${props.host.appliedConfigVersion}。` +
        '通常几秒内会自动拉取；一直不动就点「重新下发」，或去那台机器上看客户端日志。'
      )
    case 'never':
      return (
        '这台客户机从来没回报过「已应用版本」。' +
        '要么它是较旧的客户端（不认识这个字段），要么刚注册还没应用过任何配置 —— ' +
        '这与"配了但没生效"是两回事，所以这里不做告警。'
      )
    case 'ahead':
      return (
        `客户机回报的已应用版本（v${props.host.appliedConfigVersion}）比服务端当前的版本` +
        `（v${version.value}）还高。这不应当出现（版本号只增不减，客户机不会自己往前跑），` +
        '请核对是否有人直接改过库。'
      )
    default:
      return `客户机已应用 v${props.host.appliedConfigVersion}，与服务端一致。`
  }
})

const online = computed(() => props.host.state === 'online')

const updatedText = computed(() =>
  updatedAt.value ? fmtDateTime(updatedAt.value) : '尚未保存过任何配置',
)

const hasAnyTask = computed(
  () => sendRows.value.length + scheduledRows.value.length + launchRows.value.length > 0,
)

/** 父级换了机器（展开了另一行）⇒ 重新拉一份，别把上一台的配置留在界面上。 */
watch(
  () => props.host.id,
  () => {
    stopPendingPoll()
    loaded.value = false
    baseline.value = ''
    result.value = null
    error.value = ''
    void load()
  },
  { immediate: true },
)

onUnmounted(stopPendingPoll)

defineExpose({ load })
</script>

<template>
  <div class="task-config">
    <!-- ═══ 版本条 ═══ -->
    <div class="ver-strip">
      <span class="muted small">配置版本</span>
      <b class="mono">{{ version === 0 ? '—' : `v${version}` }}</b>

      <span class="muted small">已应用</span>
      <b class="mono">{{ appliedText }}</b>

      <span class="badge" :class="versionBadgeClass">{{ versionBadgeLabel }}</span>

      <span class="muted small">保存于 {{ updatedText }}</span>

      <span class="spacer" />

      <button
        class="sm"
        :disabled="loading || dirty"
        :title="dirty ? '有未保存的改动 —— 先保存或放弃改动再刷新' : '重新读取服务端上的配置'"
        @click="load(true)"
      >
        {{ loading ? '读取中…' : '刷新' }}
      </button>

      <button
        v-if="isAdmin"
        class="sm"
        :disabled="saving || !online"
        :title="
          online
            ? '要求该客户机忽略版本号、立刻重新拉取一次配置'
            : '客户端当前离线，无法下发'
        "
        @click="redeliver"
      >
        重新下发
      </button>
    </div>

    <p class="muted small ver-note">{{ versionNote }}</p>

    <p v-if="error" class="notice err small" style="margin: 8px 0">{{ error }}</p>

    <p v-if="!isAdmin" class="notice info small" style="margin: 8px 0">
      配置只有<strong>管理员</strong>能修改（下发任务 = 作用到别的机器上的动作）。现在只能查看。
    </p>

    <template v-if="loaded">
      <!-- ═══ ① UDP 指令通道 ═══ -->
      <div class="tc-head">
        <span class="tc-title">UDP 指令通道</span>
        <span class="muted small">
          这是客户机<strong>唯一</strong>开着的入站端口（收关机 / 重启等指令）
        </span>
      </div>

      <div class="tc-card">
        <div class="row-head">
          <label class="inline">
            <input v-model="udpEnabled" type="checkbox" :disabled="!isAdmin" />
            启用
          </label>

          <label class="inline">
            端口
            <input
              v-model.number="udpPort"
              type="number"
              class="port"
              :min="AGENT_CONFIG_LIMITS.minUdpPort"
              :max="AGENT_CONFIG_LIMITS.maxUdpPort"
              :disabled="!isAdmin || !udpEnabled"
            />
          </label>

          <span class="spacer" />
          <span class="badge" :class="udpEnabled ? 'ok' : ''">
            {{ udpEnabled ? '已启用' : '未启用' }}
          </span>
        </div>

        <!-- ── 「已下发」与「客户机实际」并排（2026-10-14）──
             这一行专治"指令不生效"：中心说启用了，机器却说没在听 ——
             两边都没错，只是各自相信了不同的真相。不一致时必须**显式**指出来，
             否则运维会继续在"配置明明开着"的判断上打转。 -->
        <div class="udp-compare" :class="{ mismatch: udpMismatch }">
          <span class="label">已下发（中心）</span>
          <span class="mono">{{ udpEnabled ? `启用 :${udpPort}` : '未启用' }}</span>

          <span class="label">客户机实际</span>
          <template v-if="props.host.udpChannel">
            <span v-if="props.host.udpChannel.listening" class="mono ok-text">
              正在监听 :{{ props.host.udpChannel.port }}
            </span>
            <span v-else class="mono err-text">未在监听</span>
          </template>
          <span v-else class="muted">尚未上报（客户端版本较旧，或中心刚重启）</span>
        </div>

        <p v-if="props.host.udpChannel" class="muted small" style="margin: 2px 0 0">
          {{ props.host.udpChannel.stateText || '—' }}
        </p>

        <p v-if="udpMismatch" class="notice warn small" style="margin: 6px 0 0">
          ⚠️ <strong>两边不一致</strong>：中心下发的设置还没有变成客户机的实际行为。
          最常见的原因是<strong>客户机还没拉取到这一版配置</strong>（保存后它会在下一次心跳来取），
          或者它<strong>绑不上这个端口</strong>（端口被别的程序占用 / 权限不足）——
          后者在客户机的日志里有一行「UDP 指令通道启动失败」。
        </p>

        <div class="field">
          <label>来源 IP 黑名单（留空 = 不限制来源，谁都能发）</label>
          <input
            v-model="udpBlockedText"
            :disabled="!isAdmin || !udpEnabled"
            placeholder="例如 192.168.1.10 192.168.1.11（逗号或空格分隔）"
          />
        </div>

        <p class="muted small">
          ⚠️ 关闭时客户端<strong>完全不绑定该端口</strong>（不是"绑定了再丢弃"）。
          通道是<strong>明文</strong>的（2026-10-13 起不再签名）：报文就是一行
          <code class="mono">DOWN</code> / <code class="mono">RESTART</code> /
          <code class="mono">+</code> / <code class="mono">-</code> /
          <code class="mono">0</code> / <code class="mono">1</code>，
          <strong>任意来源都能发</strong>，只有黑名单里的来源被丢弃。
          所以<strong>这条通道的端口不要暴露到不可信网络</strong> ——
          同网段任何一台机器都能把设备关掉。
        </p>
      </div>

      <!-- ═══ ② 延时 / 循环发送 ═══ -->
      <div class="tc-head">
        <span class="tc-title">延时 / 循环发送</span>
        <span class="muted small">
          延时是<strong>相对于那台机器上的 Agent 服务启动</strong>（不是相对于保存配置）
        </span>
        <span class="spacer" />
        <button class="sm" :disabled="!isAdmin" @click="addSend">添加一条</button>
      </div>

      <div v-if="sendRows.length === 0" class="empty small">
        <strong>没有发送任务</strong>
        这台机器不会主动往外发任何数据。
      </div>

      <div v-else class="tc-list">
        <div v-for="(r, i) in sendRows" :key="r.id" class="tc-card">
          <div class="row-head">
            <span class="idx">#{{ i + 1 }}</span>
            <span class="badge">{{ r.protocol }}</span>
            <span class="badge">{{ r.dataType }}</span>
            <span v-if="r.isLoop" class="badge warn">循环 {{ r.loopIntervalSeconds }}s</span>
            <span v-if="!r.enabled" class="badge">已停用</span>

            <span class="spacer" />

            <label class="inline">
              <input v-model="r.enabled" type="checkbox" :disabled="!isAdmin" />
              启用
            </label>
            <button class="sm" type="button" title="上移" :disabled="!isAdmin || i === 0" @click="move(sendRows, i, -1)">↑</button>
            <button
              class="sm"
              type="button"
              title="下移"
              :disabled="!isAdmin || i === sendRows.length - 1"
              @click="move(sendRows, i, 1)"
            >
              ↓
            </button>
            <button
              class="sm danger"
              type="button"
              :title="armedRemove === r.id ? '再点一次确认删除' : '删除本条'"
              :disabled="!isAdmin"
              @click="removeRow(sendRows, r.id)"
            >
              {{ armedRemove === r.id ? '确认删除？' : '删除' }}
            </button>
          </div>

          <div class="g3">
            <div class="field">
              <label>名称（可空，仅用于辨认）</label>
              <input v-model="r.name" :disabled="!isAdmin" placeholder="例如：每天开机报数" />
            </div>
            <div class="field">
              <label>协议</label>
              <select v-model="r.protocol" :disabled="!isAdmin">
                <option v-for="p in AGENT_SEND_PROTOCOLS" :key="p" :value="p">{{ p }}</option>
              </select>
            </div>
            <div class="field">
              <label>数据类型</label>
              <select v-model="r.dataType" :disabled="!isAdmin">
                <option v-for="d in AGENT_DATA_TYPES" :key="d.value" :value="d.value">
                  {{ d.label }}
                </option>
              </select>
            </div>
          </div>

          <div class="g3">
            <div class="field">
              <label>目标 IP</label>
              <input v-model="r.targetIp" :disabled="!isAdmin" placeholder="192.168.1.100" />
            </div>
            <div class="field">
              <label>端口</label>
              <input v-model.number="r.targetPort" type="number" min="1" max="65535" :disabled="!isAdmin" />
            </div>
            <div class="field">
              <label>首次延时（秒）</label>
              <input
                v-model.number="r.delaySeconds"
                type="number"
                min="0"
                :max="AGENT_CONFIG_LIMITS.maxDelaySeconds"
                :disabled="!isAdmin"
              />
            </div>
          </div>

          <div class="g3">
            <div class="field">
              <label>循环发送</label>
              <label class="inline">
                <input v-model="r.isLoop" type="checkbox" :disabled="!isAdmin" />
                每隔一段重复发送
              </label>
            </div>
            <div class="field">
              <label>循环间隔（秒）</label>
              <input
                v-model.number="r.loopIntervalSeconds"
                type="number"
                :min="AGENT_CONFIG_LIMITS.minLoopIntervalSeconds"
                :disabled="!isAdmin || !r.isLoop"
              />
            </div>
            <div class="field"><label>&nbsp;</label></div>
          </div>

          <div class="field">
            <label>数据内容{{ r.dataType === 'Hex' ? '（十六进制串）' : '' }}</label>
            <input v-model="r.data" :disabled="!isAdmin" placeholder="要发送的内容" />
          </div>
        </div>
      </div>

      <!-- ═══ ③ 定时发送 ═══ -->
      <div class="tc-head">
        <span class="tc-title">定时发送</span>
        <span class="muted small">每天在填写的时刻各发一次（<span class="mono">HH:mm</span>，24 小时制）</span>
        <span class="spacer" />
        <button class="sm" :disabled="!isAdmin" @click="addScheduled">添加一条</button>
      </div>

      <div v-if="scheduledRows.length === 0" class="empty small">
        <strong>没有定时任务</strong>
        这台机器不会按时刻自动发送。
      </div>

      <div v-else class="tc-list">
        <div v-for="(r, i) in scheduledRows" :key="r.id" class="tc-card">
          <div class="row-head">
            <span class="idx">#{{ i + 1 }}</span>
            <span class="badge">{{ r.protocol }}</span>
            <span class="badge">{{ r.dataType }}</span>
            <span v-if="!r.enabled" class="badge">已停用</span>

            <span class="spacer" />

            <label class="inline">
              <input v-model="r.enabled" type="checkbox" :disabled="!isAdmin" />
              启用
            </label>
            <button class="sm" type="button" title="上移" :disabled="!isAdmin || i === 0" @click="move(scheduledRows, i, -1)">↑</button>
            <button
              class="sm"
              type="button"
              title="下移"
              :disabled="!isAdmin || i === scheduledRows.length - 1"
              @click="move(scheduledRows, i, 1)"
            >
              ↓
            </button>
            <button
              class="sm danger"
              type="button"
              :title="armedRemove === r.id ? '再点一次确认删除' : '删除本条'"
              :disabled="!isAdmin"
              @click="removeRow(scheduledRows, r.id)"
            >
              {{ armedRemove === r.id ? '确认删除？' : '删除' }}
            </button>
          </div>

          <div class="g3">
            <div class="field">
              <label>名称（可空，仅用于辨认）</label>
              <input v-model="r.name" :disabled="!isAdmin" placeholder="例如：早上开机广播" />
            </div>
            <div class="field">
              <label>协议</label>
              <select v-model="r.protocol" :disabled="!isAdmin">
                <option v-for="p in AGENT_SEND_PROTOCOLS" :key="p" :value="p">{{ p }}</option>
              </select>
            </div>
            <div class="field">
              <label>数据类型</label>
              <select v-model="r.dataType" :disabled="!isAdmin">
                <option v-for="d in AGENT_DATA_TYPES" :key="d.value" :value="d.value">
                  {{ d.label }}
                </option>
              </select>
            </div>
          </div>

          <div class="g3">
            <div class="field">
              <label>目标 IP</label>
              <input v-model="r.targetIp" :disabled="!isAdmin" placeholder="192.168.1.100" />
            </div>
            <div class="field">
              <label>端口</label>
              <input v-model.number="r.targetPort" type="number" min="1" max="65535" :disabled="!isAdmin" />
            </div>
            <div class="field">
              <label>发送时刻（可多个，逗号或空格分隔）</label>
              <input
                v-model="r.timesText"
                :disabled="!isAdmin"
                placeholder="09:00, 12:30, 18:00"
              />
            </div>
          </div>

          <div class="field">
            <label>数据内容{{ r.dataType === 'Hex' ? '（十六进制串）' : '' }}</label>
            <input v-model="r.data" :disabled="!isAdmin" placeholder="要发送的内容" />
          </div>

          <p class="muted small">
            时刻会在保存时被服务端<strong>归一化</strong>：<span class="mono">9:5</span> 会变成
            <span class="mono">09:05</span>，全角冒号也认，重复的会去重并按时间排序。
            保存后请核对上面这一格 —— 认不出来的写法会被丢弃（那时列表里就少了它）。
          </p>
        </div>
      </div>

      <!-- ═══ ④ 延时启动程序 ═══ -->
      <div class="tc-head">
        <span class="tc-title">延时启动程序</span>
        <span class="muted small">
          在<strong>用户会话</strong>里启动（服务在 Session 0，它弹的窗口用户看不见）
        </span>
        <span class="spacer" />
        <button class="sm" :disabled="!isAdmin" @click="addLaunch">添加一条</button>
      </div>

      <div v-if="launchRows.length === 0" class="empty small">
        <strong>没有启动任务</strong>
        这台机器不会被自动拉起任何程序。
      </div>

      <div v-else class="tc-list">
        <div v-for="(r, i) in launchRows" :key="r.id" class="tc-card">
          <div class="row-head">
            <span class="idx">#{{ i + 1 }}</span>
            <span v-if="!r.enabled" class="badge">已停用</span>
            <span class="muted small">累计延时后的次序由这个列表决定</span>

            <span class="spacer" />

            <label class="inline">
              <input v-model="r.enabled" type="checkbox" :disabled="!isAdmin" />
              启用
            </label>
            <button class="sm" type="button" title="上移" :disabled="!isAdmin || i === 0" @click="move(launchRows, i, -1)">↑</button>
            <button
              class="sm"
              type="button"
              title="下移"
              :disabled="!isAdmin || i === launchRows.length - 1"
              @click="move(launchRows, i, 1)"
            >
              ↓
            </button>
            <button
              class="sm danger"
              type="button"
              :title="armedRemove === r.id ? '再点一次确认删除' : '删除本条'"
              :disabled="!isAdmin"
              @click="removeRow(launchRows, r.id)"
            >
              {{ armedRemove === r.id ? '确认删除？' : '删除' }}
            </button>
          </div>

          <div class="g3">
            <div class="field">
              <label>显示名</label>
              <input v-model="r.name" :disabled="!isAdmin" placeholder="例如：展厅播放器" />
            </div>
            <div class="field wide">
              <label>程序完整路径</label>
              <input
                v-model="r.programPath"
                :disabled="!isAdmin"
                placeholder="D:\\Player\\player.exe"
              />
            </div>
          </div>

          <div class="g3">
            <div class="field wide">
              <label>启动参数（可空）</label>
              <input v-model="r.argument" :disabled="!isAdmin" placeholder="例如：-fullscreen" />
            </div>
            <div class="field">
              <label>延时（秒）</label>
              <input
                v-model.number="r.delaySeconds"
                type="number"
                min="0"
                :max="AGENT_CONFIG_LIMITS.maxDelaySeconds"
                :disabled="!isAdmin"
              />
            </div>
          </div>

          <p class="muted small">
            多个任务的延时是<strong>串行累加</strong>的：第一条相对于客户机 Agent 服务启动，
            后面的相对于前一条完成 —— 现场就是靠这个顺序把多个程序依次拉起来的，
            所以<strong>这个列表的上下顺序会改变实际行为</strong>。
            ⚠️ 要等那台机器上<strong>有人登录</strong>（托盘跑起来）才会执行；
            没人登录就一直等着，不会跳过。
          </p>
        </div>
      </div>

      <p v-if="!hasAnyTask" class="muted small">
        这台机器还没有任何任务 —— 上面四个区块各点「添加一条」即可。
      </p>

      <!-- ═══ 保存条 ═══ -->
      <div v-if="isAdmin" class="save-row">
        <button class="primary" :disabled="saving || !dirty" @click="save">
          {{ saving ? '保存中…' : dirty ? '保存并下发' : '没有改动' }}
        </button>

        <button class="sm" :disabled="saving || !dirty" @click="discard">放弃改动</button>

        <span v-if="dirty" class="badge warn">有未保存的改动</span>

        <span v-if="result" class="result" :class="result.kind">
          <span class="badge" :class="result.kind">{{ result.kind === 'ok' ? '成功' : '失败' }}</span>
          <span>{{ result.text }}</span>
        </span>
      </div>

      <p class="muted small" style="margin: 8px 0 0">
        保存会立刻把<strong>新版本号</strong>推给在线的客户机，它自己来拉正文并应用。
        校验不过时这里会列出全部问题（不是只报第一条）—— 判据由服务端与客户端共用同一份，
        所以"这里能保存"就等于"那台机器能应用"。
      </p>
    </template>

    <div v-else-if="!loading" class="muted small">配置尚未加载。</div>
  </div>
</template>

<style scoped>
.task-config {
  margin-top: 14px;
  padding-top: 12px;
  border-top: 1px solid var(--border);
}

/* ── udp "已下发 vs 客户机实际" 对照行（2026-10-14）── */
.udp-compare {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  margin: 6px 0 0;
  padding: 5px 8px;
  border: 1px solid var(--border);
  border-left-width: 3px;
  border-left-color: var(--border-strong);
  border-radius: var(--radius);
  background: var(--panel-soft);
  font-size: 12px;
}

/* 不一致 ⇒ 整行转成警示色。**只有真的对不上才变色**：
   客户机尚未上报时恒为中性灰（见 udpMismatch 的注释）。 */
.udp-compare.mismatch {
  border-left-color: var(--warn);
  background: var(--warn-soft);
}

.udp-compare .label {
  color: var(--muted);
}

.udp-compare .label + span {
  margin-right: 6px;
}

.udp-compare .ok-text {
  color: var(--ok);
}

.udp-compare .err-text {
  color: var(--err);
}

/* ── 版本条 ── */
.ver-strip {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--panel);
  font-size: 12px;
}

.ver-strip b {
  font-weight: 600;
}

.ver-note {
  margin: 6px 0 0;
  line-height: 1.5;
}

.spacer {
  flex: 1 1 auto;
}

/* ── 小节标题 ── */
.tc-head {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  margin: 18px 0 8px;
}

.tc-title {
  font-weight: 600;
  font-size: 13px;
}

/* ── 任务卡片 ── */
.tc-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.tc-card {
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--panel);
  padding: 9px 11px;
}

.tc-card + .tc-card {
  margin-top: 0;
}

.row-head {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  margin-bottom: 8px;
  padding-bottom: 7px;
  border-bottom: 1px dashed var(--border);
}

.idx {
  font-family: var(--mono);
  font-size: 12px;
  color: var(--muted);
}

.field {
  display: flex;
  flex-direction: column;
  gap: 3px;
  margin-bottom: 7px;
}

.field label {
  font-size: 11px;
}

/* ⚠️ 全局 `label { display: block }` 之类会命中这里的行内标签 —— 逐项复位。 */
label.inline {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  margin: 0;
  font-size: 12px;
  color: var(--text);
  cursor: pointer;

  /* 🔥 必须 nowrap：`.row-head` 是 flex 容器，窄屏/内容多时标签会被压窄，
     于是「端口」两个字逐字折成上下两行（看起来像竖排）。
     这一行只对**行内标签**（复选框 / 端口这类"文字 + 控件"）生效，
     不影响上面那些整行 block 标签（它们本来就该换行）。 */
  white-space: nowrap;
}

/* ⚠️ 全局 `input { width: 100% }` 同样命中复选框 —— 不复位的话它会吃掉整行。 */
label.inline input[type='checkbox'] {
  width: 14px;
  height: 14px;
  padding: 0;
  margin: 0;
  border: none;
  background: none;
  flex: 0 0 auto;
  accent-color: var(--accent);
}

.g3 {
  display: grid;
  grid-template-columns: repeat(3, minmax(140px, 1fr));
  gap: 0 12px;
}

.g3 .wide {
  grid-column: span 2;
}

@media (max-width: 900px) {
  .g3 {
    grid-template-columns: 1fr;
  }

  .g3 .wide {
    grid-column: span 1;
  }
}

.field .port {
  width: 100px;
}

/* ── 保存条 ── */
.save-row {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  margin-top: 16px;
  padding-top: 12px;
  border-top: 1px solid var(--border);
}

.result {
  display: flex;
  align-items: baseline;
  gap: 6px;
  flex: 1 1 320px;
  min-width: 240px;
  font-size: 12px;
  line-height: 1.5;
}

.result.ok {
  color: var(--text);
}

.result.err {
  color: var(--err);
}
</style>
