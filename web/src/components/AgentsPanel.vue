<script setup lang="ts">
/**
 * 客户端主机页（M4.1）。
 *
 * 这一页回答两个现场高频问题：
 *   1. **哪些机器装了客户端？**（在线表 + 在线/离线计数）
 *   2. **它是谁、配置如何？**（主机名 / 系统 / CPU / 内存 / 网卡 / 客户端版本）
 *
 * ⚠️ **显卡不在这一页的列表里**（2026-10-13）：列表列宽本来就紧，而显卡型号来自
 *    6 小时一次的硬件清单。它现在只在**详情页的「硬件清单」**里显示 ——
 *    那里还有显存 / 驱动版本，且折叠时标题行也会带出型号摘要。
 *
 * 权限：读（列表 / 详情）要 `CanOperate`，写（改名 / 拉黑 / 移除 / 唤醒 / 关机 / 重启）只给 Admin。
 * 前端按角色隐藏写按钮只是"别让 Operator 看到一堆点了就 403 的东西"，
 * 真正的闸门始终在服务端。
 *
 * ⚠️ 「准入令牌」区块已于 2026-10-13 **整体移除**：注册不再需要任何凭据，
 *    装上客户端能发现到本机就自动登记。所以这一页现在**没有任何"发钥匙"的入口**。
 *
 * ⚠️ 本页底部的「客户端安装包」板块已于 2026-10-13 **移除**：顶栏右上角本来就有
 *    同一个下载入口（现场工程师到新机器上时先看到的是顶栏），两处并存只会让人
 *    怀疑"这两个是不是不一样"。安装包元信息改由 App.vue 统一持有。
 *
 * ⚠️ 两条"看起来像 bug 其实是有意为之"的语义，UI 上都写清楚了：
 *   · **移除记录 ≠ 卸载**：那台机器会重新注册并再次出现；
 *   · **拉黑 ≠ 下线**：记录与凭据都保留，只是不再与它建立连接；「取消拉黑」后它自己回来
 *     —— 要真正下线，得去那台机器上卸载（免凭据之后这一点更明显了）。
 */
import { computed, onMounted, ref } from 'vue'
import { api } from '../api'
import type {
  AgentHostDto,
  AgentListDto,
  AgentLiveMetricsDto,
  CurrentUser,
} from '../types'
import { fmtDateTime, fmtPercent, fmtRate, fmtUptimeHuman } from '../format'
import AgentDetailPanel from './AgentDetailPanel.vue'

const props = defineProps<{
  user: CurrentUser | null
}>()

const emit = defineEmits<{
  (e: 'notice', kind: 'ok' | 'err' | 'warn', text: string): void
}>()

const isAdmin = computed(() => props.user?.role === 'Admin')

// ── 在线表 ──
const list = ref<AgentListDto | null>(null)
const loading = ref(false)
const loadError = ref('')

/** 展开详情的行（同一时刻只展开一台 —— 详情挺长，多开会让页面很难读）。 */
const expandedId = ref<number | null>(null)

/** 有写请求在飞的行 —— 期间禁用它那一排按钮，避免重复提交。 */
const busyId = ref<number | null>(null)

/** 两段式删除确认（与用户管理页同一套交互：点一次变"确认移除"，再点才真删）。 */
/**
 * 两段式确认的"上膛"标记：形如 `删除:12` / `关机:12` / `重启:12`。
 *
 * 与用户管理页同一套交互 —— 第一次点只是把按钮文案变成「确认…？」，4 秒内再点才真执行。
 * ⚠️ 2026-10-15 从"只给「移除」用"泛化成通用机制：关机 / 重启整机同样是**不可撤销**的动作，
 *    它们需要同一道闸；各写一份 `confirmXxxId` 只会让三处计时器互相打架。
 */
const armed = ref<string | null>(null)
let disarmTimer: number | null = null

/** 上膛 / 触发。`key` 必须带 id（同一动作在别的行上是**另一件事**）。 */
function arm(key: string, run: () => void) {
  if (armed.value === key) {
    armed.value = null

    if (disarmTimer !== null) {
      window.clearTimeout(disarmTimer)
      disarmTimer = null
    }

    run()
    return
  }

  armed.value = key

  if (disarmTimer !== null) window.clearTimeout(disarmTimer)

  disarmTimer = window.setTimeout(() => {
    armed.value = null
    disarmTimer = null
  }, 4000)
}

const keyword = ref('')

/**
 * 排序方式（M4.2 实时列）。
 *
 * `default` 保持服务端的原顺序（按显示名）—— 那是"资产台账"的自然顺序；
 * 其余三种是**运维视角**："现在哪台最忙 / 内存快满了 / 谁在占带宽"。
 */
const sortBy = ref<'default' | 'cpu' | 'mem' | 'net'>('default')

// ═══════════════════════════════════════════════════════════════
// 展示辅助
// ═══════════════════════════════════════════════════════════════

/** 状态 → 徽章样式类。 */
function stateClass(state: string): string {
  switch (state) {
    case 'online':
      return 'ok'
    case 'unstable':
      return 'warn'
    case 'blocked':
      return 'err'
    default:
      return ''
  }
}

/**
 * 机器码打码。
 *
 * 完整机器码在列表里没有阅读价值（它只是"同一台机器"的凭据），
 * 而打码能让"贴截图求助"时不至于把设备唯一标识整条泄露出去。
 * 展开详情里给完整值 —— 那时是有人主动要看。
 */
function maskMachineKey(key: string): string {
  if (key.length <= 16) return key
  return `${key.slice(0, 8)}…${key.slice(-6)}`
}

/** NULL 是"取不到"，不是 0 —— 显示成 0 会变成一个假的硬件参数。 */
function fmtCount(value: number | null, unit: string): string {
  return value == null ? '未知' : `${value} ${unit}`
}

function fmtMemory(mb: number | null): string {
  if (mb == null) return '未知'
  return mb < 1024 ? `${mb} MB` : `${(mb / 1024).toFixed(1)} GB`
}

function fmtText(value: string | null | undefined): string {
  return value && value.trim() !== '' ? value : '—'
}

const filtered = computed(() => {
  const items = list.value?.items ?? []
  const q = keyword.value.trim().toLowerCase()

  const matched =
    q === ''
      ? items
      : items.filter((h) =>
          [h.displayName, h.hostName, h.lastAddress, h.group, h.machineKey, h.note, h.cpuName]
            .filter((x): x is string => typeof x === 'string')
            .some((x) => x.toLowerCase().includes(q)),
        )

  if (sortBy.value === 'default') return matched

  return [...matched].sort((a, b) => {
    const av = sortValue(a)
    const bv = sortValue(b)

    // ⚠️ **null 一律排到最后**，绝不当作 0 参与排序。
    //    把"采不到"当 0 会让它沉到列表底部 —— 而"最忙的那台恰好采不到"时，
    //    用户看到的是"没有机器忙"，这与事实正好相反。
    if (av === null && bv === null) return 0
    if (av === null) return 1
    if (bv === null) return -1

    return bv - av
  })
})

/** 排序键（null = 没有遥测，排最后）。 */
function sortValue(host: AgentHostDto): number | null {
  const m = host.metrics

  if (!m || !m.at) return null

  switch (sortBy.value) {
    case 'cpu':
      return m.cpuPct
    case 'mem':
      return m.memPct
    case 'net': {
      const up = m.netUpBps
      const down = m.netDownBps
      if (up === null && down === null) return null
      return (up ?? 0) + (down ?? 0)
    }
    default:
      return null
  }
}

/** 负载高低的配色阈值（纯展示，不参与任何判定）。 */
function loadClass(pct: number | null | undefined): string {
  if (pct === null || pct === undefined) return ''
  if (pct >= 85) return 'err'
  if (pct >= 60) return 'warn'
  return 'ok'
}

/** 进度条宽度（百分比）。`null` → 0 宽（配着右侧的 `—` 一起看，不会误读成"很闲"）。 */
function barWidth(pct: number | null | undefined): string {
  if (pct === null || pct === undefined || !Number.isFinite(pct)) return '0%'
  return `${Math.min(100, Math.max(0, pct))}%`
}

// ═══════════════════════════════════════════════════════════════
// 数据加载
// ═══════════════════════════════════════════════════════════════

async function load() {
  loading.value = true
  loadError.value = ''

  try {
    list.value = await api.agents()
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : '加载失败'
  } finally {
    loading.value = false
  }
}

// ═══════════════════════════════════════════════════════════════
// 写操作
// ═══════════════════════════════════════════════════════════════

/** 把一次写操作的结果统一收敛：刷新列表 + 冒提示（服务端已带回最新全量列表）。 */
function applyMutation(result: { ok: boolean; message: string; list: AgentListDto | null }) {
  if (result.list) list.value = result.list
  emit('notice', result.ok ? 'ok' : 'warn', result.message)
}

async function withBusy(id: number, action: () => Promise<void>) {
  busyId.value = id

  try {
    await action()
  } catch (e) {
    emit('notice', 'err', e instanceof Error ? e.message : '操作失败')
  } finally {
    busyId.value = null
  }
}

/**
 * 拉黑 / 取消拉黑（2026-10-15，取代原「吊销」）。
 *
 * 一句话区别：**拉黑 ≠ 下线**。记录、配置、历史遥测全部保留，只是不再与它建立连接；
 * 点「取消拉黑」之后那台机器会在 ≤15 秒内自己连回来（不需要重装、不需要重新注册）。
 * 要真正让它从列表里消失并"重新长出来"，那是「移除」。
 */
async function toggleBlock(host: AgentHostDto) {
  await withBusy(host.id, async () => {
    const result = host.isBlocked ? await api.unblockAgent(host.id) : await api.blockAgent(host.id)
    applyMutation(result)
  })
}

/**
 * 网络唤醒（WoL，2026-10-15）：给一台**已经关机**的机器发魔术包。
 *
 * ⚠️ 它走的是服务端直接发 UDP 广播，**不经过客户机**（那台机器本来就关着）——
 *    所以"已离线"不是它的失败条件，恰恰是它的使用场景。
 *    返回 `ok` 只代表"包已发出"，机器是否真的起来要去现场看（WoL 无应答）。
 */
async function wake(host: AgentHostDto) {
  await withBusy(host.id, async () => {
    emit('notice', 'ok', (await api.wakeAgent(host.id)).message)
  })
}

/**
 * 关机 / 重启整机：走 `/power`。
 *
 * 🔥 2026-10-15 起**离线也能点**（需求②）：服务端不再直接拒绝，而是改走 UDP 指令通道
 *    尽力发出，并在消息里明写"该客户端当前离线"。所以这里不能再用
 *    `result.ok` 当作"机器马上会关"—— 服务端那句 message 才是全部结论，原样显示即可
 *    （`applyMutation` 会按 `ok` 决定提示档位：UDP 兜底发出时是 ok + 一段带 ⚠ 的说明）。
 */
async function power(host: AgentHostDto, action: 'shutdown' | 'restart') {
  await withBusy(host.id, async () => {
    applyMutation(await api.powerAgent(host.id, action))
  })
}

async function doDelete(host: AgentHostDto) {
  await withBusy(host.id, async () => {
    applyMutation(await api.deleteAgent(host.id))
  })
}

/** 详情子组件保存成功后的回调：服务端已带回最新列表，直接用。 */
function onDetailsSaved(result: { ok: boolean; message: string; list: AgentListDto | null }) {
  applyMutation(result)
}

/**
 * 收到实时遥测推送（`AgentTelemetryUpdated`）时就地贴一格。
 *
 * **只改那一行**，不重拉列表：这条事件每 2 秒来一次，
 * 全量刷新等于把几十台机器的完整列表不停搬运，而真正变的只是一个数字。
 *
 * ⚠️ 必须做"新旧判定"：SignalR 不保证送达顺序，
 *    一条迟到的旧帧会把刚显示的新值盖回去 —— 表现为"实时数字偶尔往回跳一下"。
 *    这里比 `metrics.at`（采样时刻）而不是 `live.at`（广播时刻），
 *    因为采样时刻才是"这帧有多新"的真实依据。
 */
function applyLiveMetrics(live: AgentLiveMetricsDto) {
  const host = list.value?.items.find((h) => h.id === live.agentId)

  if (!host) return

  const currentAt = host.metrics?.at ? Date.parse(host.metrics.at) : Number.NEGATIVE_INFINITY
  const nextAt = live.metrics?.at ? Date.parse(live.metrics.at) : Number.NEGATIVE_INFINITY

  if (nextAt <= currentAt) return

  host.metrics = live.metrics
}

// ── 安装包 ──
//
// ⚠️ 本组件不再持有安装包元信息（2026-10-13）：本页底部的那个板块已移除，
//    下载入口统一在顶栏右上角（由 App.vue 负责查元信息与下载）。

onMounted(load)

/** 供 App.vue 在收到 SignalR 的 AgentChanged / AgentOnline / AgentOffline 时调用。 */
defineExpose({ refresh: load, applyLiveMetrics })
</script>

<template>
  <div>
    <!-- ═══ 客户端主机 ═══ -->
    <section class="panel">
      <div class="panel-head">
        <h2>
          客户端主机
          <span class="muted small">
            （共 {{ list?.total ?? 0 }} 台 · 在线 {{ list?.online ?? 0 }} ·
            不稳定 {{ list?.unstable ?? 0 }} · 离线 {{ list?.offline ?? 0 }}）
          </span>
        </h2>

        <div class="panel-actions">
          <label class="inline">
            排序
            <select v-model="sortBy" class="sort">
              <option value="default">默认（名称）</option>
              <option value="cpu">CPU 从高到低</option>
              <option value="mem">内存占用从高到低</option>
              <option value="net">网络流量从高到低</option>
            </select>
          </label>
          <input v-model="keyword" class="search" type="search" placeholder="按名称 / IP / 机器码筛选" />
          <button class="sm" :disabled="loading" @click="load">
            {{ loading ? '刷新中…' : '刷新' }}
          </button>
        </div>
      </div>

      <p class="muted small" style="margin: -4px 0 12px">
        客户机<strong>连出来</strong>注册并保持心跳（所以客户机不需要开任何入站端口、不用改防火墙）。
        状态只由心跳时间判定，不做 ping 探测 —— 那会把"网络抖一下"变成假告警。
      </p>

      <p class="muted small" style="margin: -4px 0 12px">
        实时负载来自客户端每 5 秒一次的上报；<strong>显示"—"表示采不到</strong>（不显示 0 ——
        那会被读成"很闲"）。离线超过 2 小时的机器没有"此刻"，实时列显示「无遥测」，
        点开详情仍可看历史曲线。
      </p>

      <p v-if="loadError" class="notice err" style="margin-bottom: 12px">{{ loadError }}</p>

      <div v-if="!list || list.items.length === 0" class="empty">
        <strong>还没有客户端</strong>
        同一局域网（同一广播域）的电脑装好「客户端安装包」后会<strong>自动发现</strong>本服务端，
        并<strong>自动注册</strong>出现在这里 —— <strong>不需要任何令牌或加入码</strong>。
        装完还没出现时，先确认那台机器与本服务端在<strong>同一个网段</strong>
        （跨网段 / 广播被禁的网络请在安装时填服务端地址），再看它本机的客户端日志。
      </div>

      <div v-else-if="filtered.length === 0" class="empty">
        <strong>没有匹配的客户端</strong>
        换个关键词试试。
      </div>

      <div v-else class="table-wrap tall">
        <table class="grid">
          <thead>
            <tr>
              <th style="width: 78px">状态</th>
              <th style="width: 160px">显示名</th>
              <th style="width: 150px">主机 / 地址</th>
              <th style="width: 142px">CPU / 内存</th>
              <th style="width: 178px">实时负载</th>
              <th style="width: 276px">操作</th>
            </tr>
          </thead>
          <tbody>
            <template v-for="host in filtered" :key="host.id">
              <tr :class="{ fail: host.state === 'offline' }">
                <td>
                  <!--
                    ⚠️「最后心跳」列已于 2026-10-14 **移除**（列表横向太挤，而那一列的信息
                    与左边那个状态徽章高度重复）。但**数据没有丢**：鼠标停在徽章上就能看到，
                    详情页里也有完整的一行。所以这里保留 title，而不是把信息整个抹掉 ——
                    "这台机器到底多久没说话了"是排查现场问题时第一个要问的。
                  -->
                  <span
                    class="badge"
                    :class="stateClass(host.state)"
                    :title="host.lastSeenAt ? `最后心跳：${fmtDateTime(host.lastSeenAt)}` : '从未连接过'"
                  >
                    {{ host.stateDisplay }}
                  </span>
                </td>
                <td>
                  <div class="strong">{{ host.displayName }}</div>
                  <div class="muted small mono" :title="host.machineKey">
                    {{ maskMachineKey(host.machineKey) }}
                  </div>
                </td>
                <td>
                  <div>{{ fmtText(host.hostName) }}</div>
                  <div class="muted small mono">{{ fmtText(host.lastAddress) }}</div>
                </td>
                <td>
                  <div class="ellipsis" :title="host.cpuName ?? ''">{{ fmtText(host.cpuName) }}</div>
                  <div class="muted small">
                    {{ fmtCount(host.logicalProcessors, '核') }} · {{ fmtMemory(host.totalMemoryMb) }}
                  </div>
                </td>
                <td>
                  <template v-if="host.metrics && host.metrics.at">
                    <div class="load-row">
                      <span class="load-label">CPU</span>
                      <span class="load-bar">
                        <i :class="loadClass(host.metrics.cpuPct)" :style="{ width: barWidth(host.metrics.cpuPct) }" />
                      </span>
                      <span class="load-value">{{ fmtPercent(host.metrics.cpuPct) }}</span>
                    </div>

                    <div class="load-row">
                      <span class="load-label">内存</span>
                      <span class="load-bar">
                        <i :class="loadClass(host.metrics.memPct)" :style="{ width: barWidth(host.metrics.memPct) }" />
                      </span>
                      <span class="load-value">{{ fmtPercent(host.metrics.memPct) }}</span>
                    </div>

                    <div class="muted small load-net">
                      ↑{{ fmtRate(host.metrics.netUpBps) }} ↓{{ fmtRate(host.metrics.netDownBps) }}
                      <span v-if="host.metrics.uptimeSeconds !== null" class="load-up">
                        · 已运行 {{ fmtUptimeHuman(host.metrics.uptimeSeconds) }}
                      </span>
                    </div>
                  </template>

                  <span v-else class="muted small" title="这台机器没有可用的实时数据（从未上报，或离线超过 2 小时）">
                    无遥测
                  </span>
                </td>
                <td class="actions-cell">
                  <!--
                    🔥 2026-10-14 重排：四个按钮改成**等宽两列**（每行两个），并把整格
                    垂直居中 —— 左边的信息块本来就有 2~3 行（显示名+机器码 / 主机名+地址 /
                    实时负载三行），原先按钮挤成一行贴顶，在一行里显得又偏又短，
                    现在这个 2×2 块与左边那几块**等高、同轴**，横向扫过去是齐的。
                    按钮宽度不写死：`.row-actions` 用 flex-basis 50%，所以 Operator
                    （只有「详情」一个）时它会自然占满整格，不会留一半空白。
                  -->
                  <div class="row-actions">
                    <button class="sm" @click="expandedId = expandedId === host.id ? null : host.id">
                      {{ expandedId === host.id ? '收起' : '详情' }}
                    </button>

                    <template v-if="isAdmin">
                      <!--
                        🔥 2026-10-15（需求③）：「重启服务」已**取消**（它只重启 Agent 进程，
                        在现场最需要的那几件事里排不上号），换成三件事 —— 唤醒 / 关机 / 重启整机。

                        · 唤醒：给**关机**的机器发魔术包，走服务端 UDP 广播，不经过客户机
                          ⇒ 它**不因"已离线"而禁用**（那正是它的使用场景）。
                        · 关机 / 重启：默认走 SignalR 指令；客户机离线时服务端**改走 UDP
                          指令通道尽力发出**（2026-10-15 需求②）⇒ 因此**离线也不禁用**，
                          只是提示语要写清"现在它不在线，这一发不保证生效"。
                        · 关机 / 重启**不可撤销**，所以走两段式确认（与「移除」同一道闸）。
                        · 唯一仍然禁用的情况是**已拉黑**：拉黑就是"不再与它有任何控制关系"，
                          服务端也会回 409（blocked）拦住 —— 这里先挡住少一次白跑。
                      -->
                      <button
                        class="sm"
                        :disabled="busyId === host.id"
                        title="网络唤醒：给这台已关机的机器发一组魔术包（对它上报过的每块网卡都发一遍）"
                        @click="wake(host)"
                      >
                        唤醒
                      </button>

                      <button
                        class="sm"
                        :disabled="busyId === host.id || host.isBlocked"
                        :title="host.isBlocked
                          ? '已拉黑：先「取消拉黑」再下发'
                          : host.state !== 'online'
                            ? '客户端当前不在线：将改走 UDP 指令通道尽力发出，是否生效要看那台机器'
                            : '关闭这台客户机（不可撤销）'"
                        @click="arm(`shutdown:${host.id}`, () => power(host, 'shutdown'))"
                      >
                        {{ armed === `shutdown:${host.id}` ? '确认关机？' : '关机' }}
                      </button>

                      <button
                        class="sm"
                        :disabled="busyId === host.id || host.isBlocked"
                        :title="host.isBlocked
                          ? '已拉黑：先「取消拉黑」再下发'
                          : host.state !== 'online'
                            ? '客户端当前不在线：将改走 UDP 指令通道尽力发出，是否生效要看那台机器'
                            : '重启这台客户机'"
                        @click="arm(`restart:${host.id}`, () => power(host, 'restart'))"
                      >
                        {{ armed === `restart:${host.id}` ? '确认重启？' : '重启' }}
                      </button>

                      <button class="sm" :disabled="busyId === host.id" @click="toggleBlock(host)">
                        {{ host.isBlocked ? '取消拉黑' : '拉黑' }}
                      </button>

                      <button
                        class="sm danger"
                        :disabled="busyId === host.id"
                        @click="arm(`delete:${host.id}`, () => doDelete(host))"
                      >
                        {{ armed === `delete:${host.id}` ? '确认移除？' : '移除' }}
                      </button>
                    </template>
                  </div>
                </td>
              </tr>

              <!-- 详情行：只在展开时渲染，避免几十台机器的表格里塞进几百个 DOM 节点 -->
              <tr v-if="expandedId === host.id" :key="`${host.id}-detail`">
                <td colspan="6" class="detail-cell">
                  <AgentDetailPanel :host="host" :is-admin="isAdmin" @saved="onDetailsSaved" />
                </td>
              </tr>
            </template>
          </tbody>
        </table>
      </div>
    </section>
  </div>
</template>

<style scoped>
.search {
  width: 200px;
}

.sort {
  width: 150px;
  padding: 3px 6px;
  font-size: 12px;
}

/* ── 实时负载列（M4.2）── */
.load-row {
  display: flex;
  align-items: center;
  gap: 5px;
  line-height: 1.5;
}

.load-label {
  width: 26px;
  flex: none;
  font-size: 11px;
  color: var(--muted);
}

.load-bar {
  flex: 1 1 auto;
  min-width: 40px;
  height: 6px;
  border-radius: 3px;
  background: var(--panel-soft);
  border: 1px solid var(--border);
  overflow: hidden;
}

.load-bar i {
  display: block;
  height: 100%;
  border-radius: 3px;
  background: var(--border-strong);
  transition: width 0.3s ease;
}

.load-bar i.ok {
  background: var(--ok);
}

.load-bar i.warn {
  background: var(--warn);
}

.load-bar i.err {
  background: var(--err);
}

.load-value {
  width: 44px;
  flex: none;
  text-align: right;
  font-family: var(--mono);
  font-size: 11px;
}

.load-net {
  margin-top: 1px;
  font-family: var(--mono);
}

.load-up {
  font-family: inherit;
}

.inline {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  margin: 0;
  font-size: 12px;
  color: var(--muted);
  white-space: nowrap;
}

.inline input {
  width: auto;
  margin: 0;
}

.strong {
  font-weight: 500;
}

.ellipsis {
  max-width: 150px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/**
 * 操作列：四按钮排成**等宽两列 + 整格垂直居中**（2026-10-14 重排）。
 *
 * 🔥 为什么要动它：一行里的信息块本来就有 2~3 行（显示名+机器码 / 主机名+地址 /
 * 实时负载三行），而"收起 · 拉黑 · 移除"原先挤成一行**贴着格子顶部**，
 * 于是这一行右侧看起来又偏又矮，横向扫过去对不齐。
 * 现在它占两行（与左边那几块等高）并整体居中，视觉上同轴。
 *
 * ⚠️ 用 `grid` 而不是 `flex-wrap`：`minmax(88px, 1fr)` 的**轨道最小值**会真实撑住
 * 这一列的最小宽度（表格自动布局下，只有列里存在一个"撑得住的"子元素，
 * 这一列才不会在窗口变窄时被压到只能放一个按钮 —— 那样四个按钮会竖成一溜，
 * 行高跟着涨成一屏）。flex 的 `flex-basis` 做不到这件事：可换行的 flex 容器
 * 最小宽度只等于**最宽的那一个**子项。
 *
 * ⚠️ 宽度写死 88px 的下限是有依据的：最长的按钮文案是「确认移除？」（5 个汉字
 * + 内边距，约 78px），88px 装得下且不会把文字挤断行。
 *
 * ⚠️ Operator 角色只有「详情」一个按钮 —— 它落在第 1 列、宽度与管理员那些行一致，
 * 所以上下几行的按钮左边缘仍然是一条直线（不需要为单按钮另开一套样式）。
 */
.row-actions {
  display: grid;
  grid-template-columns: repeat(2, minmax(88px, 1fr));
  gap: 6px;
  align-content: center;
}

/* 整格垂直居中 —— 全局 `table.grid td` 是 `vertical-align: top`，
   不覆盖的话按钮块会贴顶，和左边那几块居中排布的信息对不上。 */
.actions-cell {
  vertical-align: middle;
}

.detail-cell {
  background: var(--panel-soft);
  white-space: normal;
}
</style>
