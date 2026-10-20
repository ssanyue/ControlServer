<script setup lang="ts">
/**
 * 文件分发（M4.4）：新建任务 + 任务进度。
 *
 * 两种方向：
 *   · **下发**（push）：仓库文件 → 客户机的存放目录。字节由客户机用 `Range` 从服务端拉，
 *     所以**客户机依旧不开任何入站端口**（M4.1 的承诺不能在这里破）。
 *   · **回传**（collect）：客户机目录里的文件 → 仓库（`kind=collected`）。
 *
 * 🔥 **2026-10-13 起：目标一台一行，且每台可以各自指定存放路径**。
 *   · 一行一台客户机（横排），右侧就是**这台机器**的存放路径 —— 不再是一个全局输入框；
 *   · 路径可以直接点「浏览…」读取**那台机器**的目录树来挑（整盘浏览，像远程资源管理器）；
 *   · 挑到客户机白名单之外的目录也允许提交 —— **能不能写由那台客户机判**，
 *     被拒只影响那一行（其余目标照常传），原因会显示在该行上。
 *
 * ⚠️ 四条在 UI 上写明白、否则会被当成 bug 的语义：
 *   ① **离线机器照样建单**：建单时为每个目标建满明细，离线的不推 SignalR，
 *      它们上线时会自己补领。所以"选了 3 台、其中 2 台离线"不是错误 ——
 *      进度会停在"等待中"，等那两台开机后自己动起来。
 *   ② **失败率不是 0% 的常见原因**是**客户机拒绝了越界的目录**（下发的存放路径不在那台机器
 *      允许**写入**的目录内）。服务端无从验证这一点，客户机是唯一执行者。
 *      **回传（读）方向没有这个限制**：客户机对源路径**不限制目录**（2026-10-15 用户拍板，
 *      连同原来的可收窄白名单一并删除）—— 从前那套机制两次翻过车：默认只读一个私有目录时
 *      "浏览里挑好的文件"建单后必然失败；改成"默认可读整盘 + 可配置收窄"之后，
 *      留着的那个开关又成了"配了才生效"的陷阱。
 *      回传的唯一门槛是**谁能建这张单**（Admin + 授权闸门），不是目录。
 *   ③ **回传的"总大小"一开始是未知的**：所以进度会显示"已收 X"而不是百分比 ——
 *      0% 会被读成"一点没动"（服务端也是在知道总大小后才算得出百分比）。
 *   ④ **只有终态任务才该被清理**：还在等离线机器的任务再老也留着，否则那台机器上线补领不到。
 *   ⑤ **没勾选的机器也要看得见"它的文件会落到哪"**（2026-10-15）。那一格从前写的是
 *      "勾选后才能指定" —— 等于把用户最想知道的那件事藏在一个**必须先点一下**的复选框后面；
 *      而"先看一圈各台机器的落点，再决定勾哪几台"才是它真正的用法。
 *      现在未勾选时**只读显示**这台的当前落点（取值优先级见 `dirPreviews`），
 *      勾选后才换成可编辑的输入框，且**初值就是刚在屏幕上看到的那一个** ——
 *      不勾选看什么、勾上就是什么，两者不允许说两套话。
 *   ⑥ **两个量都是前端派生的**（服务端既不推速率，也不推"仓库变了"）：
 *      · **速率**：从进度事件算的，见 `../transferRate`（20 秒滑窗，传完冻结成均值）；
 *      · **"回传成功后仓库立刻可见"**：靠往上报 `collected` 让仓库页重拉 ——
 *        任务卡片刷成"已完成"、而右边文件仓库里死活找不到那个文件，
 *        是最容易被当成"回传其实没成功"的一幕。
 *
 * 权限：看任务要 `CanOperate`；**建任务只给 Admin**（推/取文件都是"作用到别的机器上"的动作）。
 */
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { api } from '../api'
import AgentPathPicker from './AgentPathPicker.vue'
import type {
  AgentHostDto,
  AgentInventoryDto,
  AgentListDto,
  CurrentUser,
  DistributionItemDto,
  DistributionTaskDto,
  FileRepoListDto,
  TransferProgressDto,
} from '../types'
import {
  DISTRIBUTION_DIRECTIONS,
  DISTRIBUTION_ITEM_STATES,
  distributionDirectionLabel,
  distributionItemStateClass,
  distributionItemStateLabel,
  distributionStateClass,
  distributionStateLabel,
  isDistributionTerminal,
  summarizeDistributionState,
} from '../types'
import { fmtBytes, fmtDateTime, fmtRate } from '../format'
import { RateTracker } from '../transferRate'

const props = defineProps<{
  user: CurrentUser | null
  files: FileRepoListDto | null
  agents: AgentListDto | null
}>()

const emit = defineEmits<{
  (e: 'notice', kind: 'ok' | 'err' | 'warn', text: string): void
  /** 有一台客户机把文件**成功回传**进仓库了（上层据此立刻重拉仓库列表）。 */
  (e: 'collected'): void
}>()

const isAdmin = computed(() => props.user?.role === 'Admin')

const root = ref<HTMLElement | null>(null)

// ═══════════════════════════════════════════════════════════════
// 数据
// ═══════════════════════════════════════════════════════════════

const tasks = ref<DistributionTaskDto[]>([])
const loading = ref(false)
const loadError = ref('')

/** 展开了明细的任务 id（同一时刻只展开一个 —— 明细可能有几百行）。 */
const expandedId = ref<number | null>(null)

/** 有建单请求在飞。 */
const creating = ref(false)

async function refresh() {
  loading.value = true
  loadError.value = ''

  try {
    const list = await api.distributions(50)
    tasks.value = list.items
    // 列表整体被替换过了 ⇒ 采样里那些已经不在列表里的行要清掉，否则只会一直涨。
    pruneRates()
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : '加载失败'
  } finally {
    loading.value = false
  }
}

// ═══════════════════════════════════════════════════════════════
// 实时进度（`TransferProgress`）
// ═══════════════════════════════════════════════════════════════

/**
 * 收到一条单目标进度时**就地改那一格**，不动整个列表。
 *
 * 之后自己**重算任务汇总**（服务端那条事件里不带任务级状态）——
 * 不重算的话卡片上的"成功 2/5"会停在建单那一刻，而进度条却在动，非常迷惑。
 *
 * 终态事件到达时**额外拉一次全量**：那时服务端的计数才是权威值（例如"部分成功"
 * 是按全部目标算的，而本地可能还有明细没收到过事件）。
 */
function applyProgress(progress: TransferProgressDto) {
  const task = tasks.value.find((t) => t.id === progress.taskId)

  if (!task) {
    // 未知任务（别处建的 / 列表里已经翻过去了）—— 拉一次全量，别再猜。
    void refresh()
    return
  }

  const item = task.items.find((i) => i.id === progress.itemId)

  if (item) {
    // 已收字节只增不减：迟到的旧事件不该把进度条往回拨。
    item.receivedBytes = Math.max(item.receivedBytes, progress.receivedBytes)
    item.totalBytes = progress.totalBytes > 0 ? progress.totalBytes : item.totalBytes
    item.progressPct = Math.max(item.progressPct, progress.progressPct)
    item.state = progress.state
    item.error = progress.error

    // 采样必须在 `receivedBytes` **更新之后**：否则采到的是上一格的旧值，
    // 算出来的速率会整体慢一拍。
    // 「还没开始」（0 字节）不采 —— 那会在滑窗里塞一个无意义的零点，把窗口均值拉低。
    if (item.receivedBytes > 0) {
      rates.observe(item.id, Date.now(), item.receivedBytes, item.startedAt, item.completedAt)
    }
  }

  recomputeTask(task)

  if (isDistributionTerminal(progress.state)) {
    // 回传成功的文件**这一刻已经落在仓库里了** —— 立刻让仓库页重拉一次，
    // 而不是等用户自己去点那个刷新按钮（任务列表刷了、仓库没刷，正是从前最容易
    // 被当成"回传没成功"的一幕）。下发方向不往仓库写东西，不通知。
    if (
      progress.state === DISTRIBUTION_ITEM_STATES.completed &&
      task.direction === DISTRIBUTION_DIRECTIONS.collect
    ) {
      emit('collected')
    }

    void refresh()
  }
}

/** 按明细状态重算卡片上的汇总（镜像服务端的 `FileTransferMath.SummarizeTaskState`）。 */
function recomputeTask(task: DistributionTaskDto) {
  task.total = task.items.length
  task.succeeded = task.items.filter((i) => i.state === DISTRIBUTION_ITEM_STATES.completed).length
  task.failed = task.items.filter((i) => i.state === DISTRIBUTION_ITEM_STATES.failed).length
  task.state = summarizeDistributionState(task.items.map((i) => i.state))
}

// ═══════════════════════════════════════════════════════════════
// 速率（由 `TransferProgress` 派生，服务端不推这个值）
// ═══════════════════════════════════════════════════════════════

/**
 * 每个明细行一份**滑动窗口**字节采样，用来算"现在多快"。
 *
 * 算法本身在 `../transferRate`（纯逻辑、有单测）；这里只负责"什么时候喂、什么时候读"。
 *
 * ⚠️ 它**刻意不是响应式的**（普通 class 实例，不是 `reactive`）：
 * 读它的只有 `itemRateText()`，而每次喂它的 `applyProgress()` 都同时把 `item` 上的响应式
 * 字段（`receivedBytes` / `state`）改成了新值 —— 渲染本来就会被带动一次，`itemRateText`
 * 会跟着重跑。（字节没变的事件会被 Vue 的"值相同不触发"挡掉，那种情况速率本来也就
 * 只在停滞中缓慢下滑，晚一拍显示无所谓。）
 * 把它做成响应式反而会引入 `Map.keys()` 的**全表迭代依赖**（`prune` 要用），
 * 任何一个 key 变化都让整个明细列表重算，白白浪费。
 */
const rates = new RateTracker()

/** 采样窗口按 `item.id` 存；列表整体刷新后清掉已不在列表里的那些，防止越积越多。 */
function pruneRates() {
  const alive = new Set<number>()
  for (const task of tasks.value) for (const item of task.items) alive.add(item.id)

  rates.prune(alive)
}

/** 明细行上的速率文字。只有"在传"和"已传完"两种状态才谈得上速率。 */
function itemRateText(item: DistributionItemDto): string {
  if (
    item.state !== DISTRIBUTION_ITEM_STATES.running &&
    item.state !== DISTRIBUTION_ITEM_STATES.completed
  ) {
    return '—'
  }

  return fmtRate(rates.rate(item.id))
}

/** 传输中的速率取强调色（那正是用户在盯的数字），跑完之后冻结成均值、弱化掉。 */
function itemRateClass(item: DistributionItemDto): string {
  return item.state === DISTRIBUTION_ITEM_STATES.running ? 'live' : 'frozen'
}

// ═══════════════════════════════════════════════════════════════
// 新建任务
// ═══════════════════════════════════════════════════════════════

const form = reactive({
  direction: DISTRIBUTION_DIRECTIONS.push as string,
  fileId: 0,
  sourcePath: '',
  note: '',
})

/**
 * **每台目标各自的存放路径**（下发方向用）。
 *
 * 用普通对象而不是 `Map`：`v-model="targetDirs[id]"` 需要一个可读写的**属性**
 * 才能双向绑定（Vue 3 的 `reactive` 对新增键同样是响应式的）。
 *
 * 取消勾选时**刻意不清空** —— 用户常常是"先取消再看一眼"，清掉就得重挑一次。
 */
const targetDirs = reactive<Record<number, string>>({})

/** 已选目标（Set 的增删在 Vue 3 里是可追踪的）。 */
const selectedAgents = ref(new Set<number>())
const agentKeyword = ref('')

const agents = computed<AgentHostDto[]>(() => props.agents?.items ?? [])

/**
 * 本页刚刚「记为此机默认目录」的结果（键 = `target:12` / `source:12`）—— 一层**乐观覆盖**。
 *
 * 🔥 **为什么必须有这一层**：`agents` 是父级传下来的**只读快照**
 * （`computed(() => props.agents?.items ?? [])`），本页改不了里面任何一个字段。
 * 而"记住默认目录"是**刚刚发生、并且用户已经看到确认**的一件事 ——
 * 不覆盖的话，页面会一边说"已记住 D:\Logs\2026"、一边在下一次打开浏览器时
 * 仍然从**旧目录**开始（`lastSourceDir` / `lastTargetDir` 还是老值）。
 * 两种说法并存，用户只会得出"没存上"的结论。
 *
 * ⚠️ 它**只是显示层**：真值在服务端（`agent_hosts.LastTargetDir/LastSourceDir`）。
 * 所以一旦父级送来新列表，就把覆盖清掉 —— 那份列表里已经带着真值了
 * （PUT 是 await 完才 emit 的，之后任何一次 `GET /api/agents` 都读得到）。
 * 留着不清反而会让**别人改的值**被本页的旧值一直遮住。
 */
const remembered = reactive<Record<string, string>>({})

// 新列表到达 = 服务端真值到了 ⇒ 撤掉乐观覆盖（否则真值会被本地旧值反遮）。
watch(
  () => props.agents,
  () => {
    for (const key of Object.keys(remembered)) delete remembered[key]
  },
)

/**
 * 这台机器"上次用的下发落脚目录"。
 *
 * 优先取本页刚记住的值（见 `remembered`），没有才退回服务端快照。
 */
function lastTargetDirOf(id: number): string {
  return remembered[`target:${id}`] ?? agents.value.find((a) => a.id === id)?.lastTargetDir ?? ''
}

/** 这台机器"上次用的回传读取目录"。取值规则同上。 */
function lastSourceDirOf(id: number): string {
  return remembered[`source:${id}`] ?? agents.value.find((a) => a.id === id)?.lastSourceDir ?? ''
}

/**
 * 路径选择器刚刚记住了某个目录 —— 覆盖本地快照，让"记住"这件事**当场**看得见。
 *
 * 🔥 **2026-10-15 修：还要把"这一台那一格"也填上。**
 *
 * 只更新 `remembered`（覆盖层）是不够的：那一格的 `targetDirs[id]` 在**勾选时就已经被
 * 物化**成当时的 `lastTargetDirOf(id)`（那时是空的），而物化**只发生一次**
 * （见下面 watch 里的 `if (id in targetDirs) continue`）。于是"刚记住了一个目录"
 * 在屏幕上**什么都不变** —— 输入框仍然空着、你无从知道保存的是哪个路径，
 * 而建单时发出去的仍是空串（= 客户机默认目录）。现场表现就是
 * "<b>保存了路径，但下发没按它走、也看不到它</b>"。
 *
 * 现场证据（服务端日志）：
 * ```
 * 16:47:08 / 16:47:18 / 16:47:43  客户端默认目录已记忆（Id=11）   ← 连点三次（屏幕上没反应）
 * 16:47:49                        新建下发任务 #10 … 落地目录 (默认)
 * ```
 *
 * ⚠️ <b>不能无条件覆盖</b>：那一格里可能是用户**手输的**另一个目录（这次就用它）。
 * 所以只在"这格还是空的"或"它正好等于记住之前的那个记忆值"（= 没人动过）时才填。
 */
function onPathRemembered(payload: { agentId: number; targetDir?: string; sourceDir?: string }) {
  if (payload.targetDir !== undefined) {
    // 先取旧值：下面要靠它判断"这一格还是记忆值、不是人手输的"。
    const before = lastTargetDirOf(payload.agentId)

    remembered[`target:${payload.agentId}`] = payload.targetDir

    const current = (targetDirs[payload.agentId] ?? '').trim()

    if (current === '' || current === before.trim()) {
      targetDirs[payload.agentId] = payload.targetDir
    }
  }

  if (payload.sourceDir !== undefined) remembered[`source:${payload.agentId}`] = payload.sourceDir
}

const filteredAgents = computed(() => {
  const q = agentKeyword.value.trim().toLowerCase()
  if (q === '') return agents.value

  return agents.value.filter((a) =>
    [a.displayName, a.hostName, a.lastAddress, a.group, a.machineKey]
      .filter((x): x is string => typeof x === 'string')
      .some((x) => x.toLowerCase().includes(q)),
  )
})

const onlineAgents = computed(() => agents.value.filter((a) => a.state === 'online'))

/** 选了但当前离线的台数 —— 提前告诉用户"这几台要等它开机"。 */
const selectedOfflineCount = computed(
  () => agents.value.filter((a) => selectedAgents.value.has(a.id) && a.state !== 'online').length,
)

const selectedFile = computed(
  () => (props.files?.items ?? []).find((f) => f.id === form.fileId) ?? null,
)

const isPush = computed(() => form.direction === DISTRIBUTION_DIRECTIONS.push)

/** 已选目标（按 id 升序，避免"哪个是第一台"在勾选之间跳来跳去）。 */
const selectedIds = computed(() => [...selectedAgents.value].sort((a, b) => a - b))

const canSubmit = computed(() => {
  if (!isAdmin.value || creating.value) return false
  if (selectedAgents.value.size === 0) return false

  if (isPush.value) return form.fileId > 0
  return form.sourcePath.trim() !== ''
})

/** 有路径的行数（提示"留空的那几台会落到各自默认目录"）。 */
const dirsFilledCount = computed(
  () => selectedIds.value.filter((id) => (targetDirs[id] ?? '').trim() !== '').length,
)

/** 首台已填路径 → 才允许"套用到全部"。 */
const canApplyFirstToAll = computed(() => {
  const first = selectedIds.value[0]
  return first !== undefined && (targetDirs[first] ?? '').trim() !== ''
})

function toggleAgent(id: number) {
  if (selectedAgents.value.has(id)) selectedAgents.value.delete(id)
  else selectedAgents.value.add(id)
}

function selectFiltered() {
  for (const a of filteredAgents.value) selectedAgents.value.add(a.id)
}

function selectOnline() {
  for (const a of onlineAgents.value) selectedAgents.value.add(a.id)
}

function clearSelection() {
  selectedAgents.value.clear()
}

/** 把首台填好的路径抄给其余已选目标 —— 大部分场景下同一批机器落点相同。 */
function applyFirstToAll() {
  const first = selectedIds.value[0]
  if (first === undefined) return

  const value = targetDirs[first] ?? ''

  for (const id of selectedIds.value) targetDirs[id] = value
}

/**
 * 勾选即把「上次给这台机器用的落脚目录」物化进输入框（M4.7 路径记忆）。
 *
 * 🔥 **只在"这一格还没被摆弄过"时才填**（用 `in` 判断，而不是判断值是否为空）——
 * 用户手改过、甚至特意清空要回落到默认目录之后，再勾一次不该把他的输入冲掉。
 * 这也是为什么"空串"必须和"没记过"分开存：空串是用户**明确**想让文件落到默认目录。
 *
 * 记忆值是服务端在每个下发任务建好后回写的（`agent_hosts.LastTargetDir`），
 * 所以这是**跨浏览器、跨操作员**的：换个电脑打开这个页面，路径照样在。
 *
 * 🔥 **2026-10-15 修：预填的值必须与"未勾选时显示的那个"完全同源**（`landingPathOf`）。
 *
 * 从前这里只填 `lastTargetDirOf(id)` —— 那是**上一次下发用过**的目录，
 * 对一台**从没被下发过**的机器是空的。而那一行未勾选时显示的可能是
 * "默认 `C:\…\inbox`"（客户机上报的默认落地目录，见 `dirPreviews`）。
 * 于是同一件事在屏幕上说了两遍、还说得不一样：
 * 未勾选写着"默认 C:\…\inbox"，勾上之后输入框却是**空的** ——
 * 用户只会认为"这个路径没生效"，而它其实生效了（空串 = 默认目录）。
 * 现在两处都走 `landingPathOf`：**看到什么、勾上就是什么**。
 *
 * ⚠️ 这也意味着"客户机默认目录"那一档会被**显式写进输入框**、随任务发出去。
 * 值的语义完全一样（客户机的默认目录就是它白名单的第一项，显式写它照样通过校验），
 * 但代价要讲清：它**不再随白名单变化自动跟随**，而且这次下发会把它记成这台机器的
 * "上次目录"。要回到"跟随默认目录"，把输入框清空即可（空串的语义仍在）。
 */
watch(
  () => selectedIds.value,
  (ids) => {
    for (const id of ids) {
      if (id in targetDirs) continue

      // ⚠️ 即便没有记忆值也要**显式写入空串**，不能留一个"不存在的键"（2026-10-14 修）。
      //
      // 绑的是 `v-model="targetDirs[agent.id]"`：键不存在时子组件收到的是 `undefined`，
      // 而它声明的是 `string` —— 子组件里 `props.modelValue.trim()` 会抛 TypeError，
      // 被 `void openAt()` 无声吞掉，表现是**点开浏览面板什么都不发生**。
      // 只在"这台机器还没记过目录"时出现 ⇒ 全新部署必现、老现场看不见，最难查的那一类。
      targetDirs[id] = landingPathOf(id)
    }
  },
)

/**
 * 一条"可以浏览的机器"记录（AgentPathPicker 要"名字 + 在线状态"来预告离线这个头号失败原因）。
 *
 * 把 id 与可选的主机对象一起收进来，是为了让**行内**（手上就有 agent）与
 * **回传那一侧**（只有 id，要去列表里找）两处共用同一份构造逻辑。
 */
function pickerTarget(id: number, agent?: AgentHostDto) {
  return {
    id,
    name: agent?.displayName || agent?.hostName || `#${id}`,
    online: agent?.state === 'online',
  }
}

// ═══════════════════════════════════════════════════════════════
// 白名单目录候选（来自客户机自己上报的清单）—— 手输时的下拉提示
// ═══════════════════════════════════════════════════════════════

/**
 * 目标超过这个数就不去拉候选了。
 *
 * <b>为什么要有上限</b>：清单是**一台一个请求**（`GET /api/agents/{id}/inventory`）。
 * 一次给 100 台下发是常态，那就是 100 个请求换来一堆"反正用不掉"的候选 ——
 * 而批量下发几乎总是用"客户机默认目录"（路径留空）。
 * 小批量的定向下发（几台）才是真正需要挑目录的场景。
 */
const DIR_HINT_MAX_AGENTS = 12

/** 每台已查过的清单（null = 拉失败或这台还没上报过）。缓存住，避免反复选中/取消时重复请求。 */
const agentDirs = ref(new Map<number, AgentInventoryDto | null>())
const dirsLoading = ref(false)

watch(
  () => [...selectedAgents.value].sort((a, b) => a - b),
  (ids) => void loadDirs(ids),
)

async function loadDirs(ids: number[]) {
  if (ids.length === 0 || ids.length > DIR_HINT_MAX_AGENTS) return

  const missing = ids.filter((id) => !agentDirs.value.has(id))
  if (missing.length === 0) return

  dirsLoading.value = true

  try {
    const fetched = await Promise.all(
      missing.map(async (id) => {
        try {
          const env = await api.agentInventory(id)
          return [id, env.inventory] as const
        } catch {
          // 单台拉失败不该影响其它台：那台退回"手输"就行。
          return [id, null] as const
        }
      }),
    )

    for (const [id, inv] of fetched) agentDirs.value.set(id, inv)
  } finally {
    dirsLoading.value = false
  }
}

/**
 * 机群小（≤ 上限）时**把整个列表的清单也读回来**（2026-10-15）。
 *
 * **为什么**：下发方向**未勾选的行也要显示"这台现在的落点"**（见 `dirPreviews`），
 * 而对一台**从没被下发过**的机器，那个落点只能从**它自己上报的默认目录**里取 ——
 * 不预取的话这种行会一直停在"未记录"，而"刚装好一批新机器、还没下发过"正是最常见的场景。
 *
 * ⚠️ 上限沿用 `DIR_HINT_MAX_AGENTS`，理由与原注释一模一样：清单是**一台一个请求**。
 * 上百台的机群为了一格提示发上百个请求不划算；而那种规模的现场早就逐台下发过，
 * 每台都有 `lastTargetDir` 直接可显示 —— 不需要清单兜底。
 *
 * ⚠️ 只在**下发**方向预取：回传那边的候选是从"已勾选"的并集里来的，与这里无关。
 *
 * ⚠️ <b>它必须排在 `agentDirs` / `dirsLoading` / `loadDirs` 之后</b>（现在就是）：
 * `immediate: true` 会让回调**在 setup 期间同步跑一次**，而 `loadDirs` 要读那三个名字 ——
 * 往上挪到它们之前就会撞 TDZ。**今天往上挪也不会炸，只是因为**父级刚挂载时
 * `props.agents` 还是 `null` ⇒ 提前 return 了 —— 那是巧合，不是保证。
 */
watch(
  () =>
    isPush.value && agents.value.length > 0 && agents.value.length <= DIR_HINT_MAX_AGENTS
      ? agents.value.map((a) => a.id)
      : [],
  (ids) => void loadDirs(ids),
  { immediate: true },
)

/** 某台机器上报过的"允许写入目录"（没查到 / 没上报 ⇒ 空表，退化为纯手输/浏览）。 */
function inboxDirsOf(id: number): string[] {
  return agentDirs.value.get(id)?.transferInboxDirectories ?? []
}

/**
 * 这台机器**此刻的落点** —— 唯一事实源。
 *
 * 🔥 **未勾选时显示的是它，勾选后预填进输入框的也是它**（见下面那个物化 watch）。
 * 两处必须同源：一旦各行其是，就会出现"屏幕上写着 `默认 C:\…\inbox`、
 * 勾上之后输入框却是空的"这种**同一件事说两遍、还说得不一样**的现场 ——
 * 用户只会得出"这个路径没生效"的结论。
 * （2026-10-15 真机验证时被这条断言当场抓住：加上默认目录兜底后，
 *   显示有值、输入框仍走旧逻辑填空串。）
 *
 * 取值优先级：
 *   ① `lastTargetDir`（服务端路径记忆 = 上次下发用过的落脚目录）；
 *   ② 客户机上报的默认落地目录 `transferInboxDirectories[0]`
 *      （= 任务不带目标目录时的落点，见 `FileTransferClient.ResolveInboxDirectory`）；
 *   ③ 都没有 ⇒ 空串（界面上照实说"未记录"，不编一个路径出来）。
 */
function landingPathOf(id: number): string {
  const memory = lastTargetDirOf(id).trim()
  if (memory !== '') return memory

  return (inboxDirsOf(id)[0] ?? '').trim()
}

/** 上面那个路径是**哪来的** —— 只为了在界面上说清"这是上次用过的，还是它自己上报的默认值"。 */
function landingWhenceOf(id: number): string {
  if (lastTargetDirOf(id).trim() !== '') return '上次'
  return (inboxDirsOf(id)[0] ?? '').trim() !== '' ? '默认' : ''
}

/**
 * **没勾选**那一行要显示的「这台机器现在的落点」。
 *
 * 🔥 为什么不能只显示服务端记的 `lastTargetDir`：那只是"**上次**用了哪个目录"，
 * 对一台**从没被下发过**的机器它是空的 —— 而空的时候用户真正想知道的是
 * "那这次会落到哪"，答案在**客户机上报的默认目录**里。
 * 两者合起来才是一句完整的话："这台机器现在的文件会落到哪"。
 *
 * ⚠️ 手改过输入框之后，这里显示的仍是**记忆值/默认值**，不是那一格里的内容 ——
 * 这是**故意的**："上次用过的"和"你这次填的"本来就是两件事，
 * 混成一个只会让下一次勾选时看不出哪个是哪个（真正发出去的是 `targetDirs[id]`）。
 */
const dirPreviews = computed<Record<number, { path: string; whence: string }>>(() => {
  const out: Record<number, { path: string; whence: string }> = {}

  for (const agent of filteredAgents.value) {
    out[agent.id] = { path: landingPathOf(agent.id), whence: landingWhenceOf(agent.id) }
  }

  return out
})

/** 已选目标里**查到过清单**的那些（拉失败 / 未上报的不计入，它们不提供候选）。 */
const selectedInventories = computed<AgentInventoryDto[]>(() =>
  [...selectedAgents.value]
    .map((id) => agentDirs.value.get(id))
    .filter((inv): inv is AgentInventoryDto => !!inv),
)

function unionDirs(lists: (string[] | null)[]): string[] {
  const out = new Set<string>()
  for (const list of lists) if (list) for (const dir of list) out.add(dir)
  return [...out]
}

/**
 * 候选是否有效（值得显示）。
 *
 * 超过上限时**不显示任何候选**：那是几十台机器目录提示的并集，可能上百行，
 * 摆在表单里只会让人以为"必须从中选"。
 */
const hintEnabled = computed(
  () => selectedAgents.value.size > 0 && selectedAgents.value.size <= DIR_HINT_MAX_AGENTS,
)

/**
 * 回传源路径的**候选提示**：所选机器报上来的盘符（`C:\` / `D:\` …）。
 *
 * ⚠️ 它**不是白名单**（2026-10-15 起回传不限制目录）：只是"这台机器上有哪几个盘"的提示，
 * 免得运维盲填一个不存在的盘符。填候选之外的路径照样能提交、也能取回来。
 */
const outboxDirOptions = computed(() =>
  hintEnabled.value ? unionDirs(selectedInventories.value.map((i) => i.transferOutboxDirectories)) : [],
)

/**
 * 可浏览的机器（= 已选目标，回传那一侧用）。
 *
 * **为什么要把"在线状态"也带上**：浏览是一次真实往返，**离线是它的头号失败原因**。
 * 把状态提前摆出来，用户就不会点一次、等 8 秒超时、再看到"这台机器离线"。
 *
 * 顺序按 id 排（`selectedIds` 已排好）：Set 的迭代顺序会让"默认浏览哪一台"在勾选之间跳来跳去。
 */
const browseTargets = computed(() =>
  selectedIds.value.map((id) => pickerTarget(id, agents.value.find((a) => a.id === id))),
)

/**
 * 回传方向的浏览起点（M4.7 的"路径记忆"）。
 *
 * **只在选了一台**时给：回传是"从 A 机取某个路径"，多台共用同一个源路径，
 * 而每台记的上次目录各不相同 —— 拿哪一台的都像在骗人。所以宁可不给（起点=盘符列表）。
 *
 * ⚠️ 经 `lastSourceDirOf` 取：本页刚在这台机器上点过「记为此机默认目录」，
 * 下次打开浏览器就**从刚记住的那一层**开始，而不是老值。
 */
const lastSourceDirHint = computed(() => {
  const ids = selectedIds.value
  if (ids.length !== 1) return ''
  return lastSourceDirOf(ids[0])
})

/**
 * 已选目标里有机器**把文件传输关掉了**（`[]` 与 `null` 都代表"没有候选"，但含义不同：
 * `[]` = 这个客户端版本知道这件事、但本机没启用；`null` = 老客户端没上报过）。
 */
const anyTransferDisabled = computed(() =>
  selectedInventories.value.some(
    (i) => i.transferInboxDirectories !== null && i.transferInboxDirectories.length === 0,
  ),
)

function switchDirection(direction: string) {
  form.direction = direction
}

/**
 * 仓库页点「分发」时把那个文件带进来（并滚到本面板）。
 *
 * 由父组件**直接调用**而不是走 prop：走 prop 的话"对同一个文件再点一次"
 * 值没变化、watch 不会触发，表现为"第二次点没反应"。
 *
 * 只切到"下发"方向：回传与仓库里的文件无关（它是"从客户机取"）。
 */
function focusFile(fileId: number) {
  form.direction = DISTRIBUTION_DIRECTIONS.push
  form.fileId = fileId

  void nextTick(() => {
    root.value?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  })
}

// 新到的文件列表里如果已经没有当前选中的文件（被删了），清掉选择。
watch(
  () => props.files,
  (list) => {
    if (form.fileId > 0 && list && !list.items.some((f) => f.id === form.fileId)) form.fileId = 0
  },
)

async function create() {
  if (selectedAgents.value.size === 0) {
    emit('notice', 'warn', '请先选择至少一台目标客户机。')
    return
  }

  creating.value = true
  const ids = selectedIds.value

  try {
    const created = isPush.value
      ? await api.createPushDistribution({
          fileId: form.fileId,
          // 每台一条，各带自己的存放路径（留空 = 该机的默认目录）。
          targets: ids.map((id) => ({ agentId: id, targetDir: targetDirs[id] ?? '' })),
        })
      : await api.createCollectDistribution({
          targetAgentIds: ids,
          sourcePath: form.sourcePath,
          note: form.note,
        })

    // 去重后插到最前（服务端用的是"最近在前"的顺序）。
    tasks.value = [created, ...tasks.value.filter((t) => t.id !== created.id)]
    expandedId.value = created.id

    const waiting = created.items.filter((i) => i.state !== DISTRIBUTION_ITEM_STATES.completed).length

    emit(
      'notice',
      'ok',
      `已创建${distributionDirectionLabel(created.direction)}任务 #${created.id}（${created.total} 个目标` +
        (waiting > 0 ? `，${waiting} 个在等待传输` : '') +
        '）。离线机器会在上线后自动补领。',
    )
  } catch (e) {
    emit('notice', 'err', e instanceof Error ? e.message : '创建任务失败')
  } finally {
    creating.value = false
  }
}

// ═══════════════════════════════════════════════════════════════
// 展示辅助
// ═══════════════════════════════════════════════════════════════

function itemBarWidth(item: DistributionItemDto): string {
  if (item.totalBytes <= 0) return item.state === DISTRIBUTION_ITEM_STATES.completed ? '100%' : '0%'
  return `${Math.min(100, Math.max(0, item.progressPct))}%`
}

function itemBarClass(item: DistributionItemDto): string {
  switch (item.state) {
    case DISTRIBUTION_ITEM_STATES.completed:
      return 'ok'
    case DISTRIBUTION_ITEM_STATES.failed:
      return 'err'
    case DISTRIBUTION_ITEM_STATES.running:
      return 'run'
    default:
      return ''
  }
}

/** 进度文字：总大小未知时只报"已收多少"，**不报百分比**（0% 会被读成"没动"）。 */
function itemText(item: DistributionItemDto): string {
  if (item.totalBytes > 0) return `${fmtBytes(item.receivedBytes)} / ${fmtBytes(item.totalBytes)}`
  if (item.receivedBytes > 0) return `已收 ${fmtBytes(item.receivedBytes)}`
  return '—'
}

function taskTitle(task: DistributionTaskDto): string {
  return task.direction === DISTRIBUTION_DIRECTIONS.push ? task.fileName : task.sourcePath || '（未指定路径）'
}

/**
 * 任务级的"落到哪儿"摘要。
 *
 * ⚠️ 任务级 `targetDir` **只在全批共用一个目录时才有值**。每台各不相同时它是 null，
 * 此时**不能显示成"（客户机默认）"** —— 那会把"各自指定了不同目录"说成"都没指定"。
 * 逐台的真实落点在展开的明细里。
 */
function taskDirSummary(task: DistributionTaskDto): string {
  if (task.direction !== DISTRIBUTION_DIRECTIONS.push) return ''
  if (task.targetDir) return task.targetDir

  const dirs = task.items.map((i) => (i.targetDir ?? '').trim())
  if (dirs.every((d) => d === '')) return '（客户机默认目录）'
  return '各自指定（见明细）'
}

/** 明细行上那一台的落点。 */
function itemDirText(item: DistributionItemDto): string {
  return item.targetDir?.trim() || '（默认目录）'
}

function agentStateClass(state: string): string {
  switch (state) {
    case 'online':
      return 'ok'
    case 'unstable':
      return 'warn'
    case 'revoked':
      return 'err'
    default:
      return ''
  }
}

function agentStateLabel(state: string): string {
  switch (state) {
    case 'online':
      return '在线'
    case 'unstable':
      return '不稳定'
    case 'offline':
      return '离线'
    case 'never':
      return '从未连接'
    case 'revoked':
      return '已吊销'
    default:
      return state
  }
}

// ═══════════════════════════════════════════════════════════════
// 轮询（兜底）
// ═══════════════════════════════════════════════════════════════

/**
 * 低频兜底刷新：只在"还有没跑完的任务"时才发请求。
 *
 * 实时事件已经覆盖了绝大多数进度变化，但有两条路它覆盖不到：
 *   · 页面在这期间断过线（漏掉的事件不会补发）；
 *   · 离线机器上线后补领 —— 那台机器上的进度上报会来，但它的**在线状态**变化
 *     只会经 `AgentChanged` 打到客户端页，不会打到这里。
 * 20 秒一次、且只有未终结任务存在时才发，代价可以忽略。
 */
let pollTimer: number | null = null

function hasOpenTasks(): boolean {
  return tasks.value.some((t) => !isDistributionTerminal(t.state))
}

onMounted(() => {
  void refresh()

  pollTimer = window.setInterval(() => {
    if (hasOpenTasks()) void refresh()
  }, 20000)
})

onUnmounted(() => {
  if (pollTimer !== null) window.clearInterval(pollTimer)
  pollTimer = null
})

defineExpose({ refresh, applyProgress, focusFile })
</script>

<template>
  <div ref="root">
    <!-- ═══ 新建任务 ═══ -->
    <section v-if="isAdmin" class="panel">
      <div class="panel-head">
        <h2>新建分发任务</h2>
        <span class="muted small">字节走客户机主动 HTTP（下载用 Range 续传），客户端不开任何入站端口</span>
      </div>

      <div class="dir-switch">
        <button
          :class="{ active: form.direction === DISTRIBUTION_DIRECTIONS.push }"
          @click="switchDirection(DISTRIBUTION_DIRECTIONS.push)"
        >
          下发到客户机
        </button>
        <button
          :class="{ active: form.direction === DISTRIBUTION_DIRECTIONS.collect }"
          @click="switchDirection(DISTRIBUTION_DIRECTIONS.collect)"
        >
          从客户机回传
        </button>
      </div>

      <!-- 下发：只选文件；存放路径在下面每一台那一行上。 -->
      <div v-if="isPush" class="form-row">
        <label>
          仓库文件
          <select v-model.number="form.fileId">
            <option :value="0">— 请选择 —</option>
            <option v-for="f in props.files?.items ?? []" :key="f.id" :value="f.id">
              {{ f.name }}（{{ fmtBytes(f.size) }}）
            </option>
          </select>
        </label>
      </div>

      <!-- 回传：源路径 + 备注 -->
      <div v-else class="form-row">
        <AgentPathPicker
          class="wide"
          v-model="form.sourcePath"
          :targets="browseTargets"
          :suggestions="outboxDirOptions"
          :initial-path="lastSourceDirHint"
          remember="source"
          @remembered="onPathRemembered"
        />

        <label>
          备注
          <input v-model="form.note" placeholder="例如：收 10 月现场日志" />
        </label>
      </div>

      <!-- 候选提示：只在**小批量定向**（≤ 上限）时显示 —— 上百台的并集没有参考价值。 -->
      <p v-if="hintEnabled && selectedAgents.size > 0" class="muted small" style="margin: 2px 0 10px">
        <template v-if="dirsLoading">正在读取所选机器允许的目录…</template>
        <template v-else-if="anyTransferDisabled">
          ⚠️ 所选机器里<strong>有机器关闭了文件传输</strong>（或还没上报目录）—— 它们不会接收下发。
        </template>
        <template v-else-if="isPush">
          <template v-if="selectedInventories.length > 0">
            这些机器各自允许写入的目录会作为该行输入框的下拉候选。
          </template>
          <template v-else>所选机器还没上报目录，可点各行「浏览…」直接读它的磁盘。</template>
        </template>
        <template v-else>
          <template v-if="outboxDirOptions.length > 0">
            所选机器上的盘符（来自客户机上报，<strong>仅供参考，不是限制</strong>）：
            <code v-for="d in outboxDirOptions" :key="d" class="mono">{{ d }}</code>
          </template>
          <template v-else>所选机器还没上报盘符，直接手填绝对路径即可。</template>
        </template>
      </p>

      <p class="muted small" style="margin: 2px 0 10px">
        <template v-if="isPush">
          每台客户机独占一行，可<strong>各自指定存放路径</strong>（留空 = 用那台机器的默认目录）。
          未勾选时这一格<strong>也会只读显示该机当前的落点</strong>（上次用过的目录，或它上报的默认目录），
          勾选后才变成可编辑，<strong>初值就是刚显示的那一个</strong> —— 先看一圈各台的落点再决定勾谁。
          点「浏览…」会读取<strong>那台机器</strong>的目录树让你挑。挑到它不允许写入的目录也没关系 ——
          客户机会拒绝<strong>那一台</strong>并把原因回报到该行，其余目标照常传。
        </template>
        <template v-else>
          回传是<strong>把客户机上的文件读到仓库</strong>：源路径填<strong>绝对路径</strong>即可，
          <strong>不限制目录</strong>（与「浏览」看到的范围一致，任何盘、任何目录都行）。
          上面列出的盘符只是提示，填别的路径一样能取。
          读不到时失败原因会显示在该行上（最常见的是路径写错或文件不存在）。
          收上来的文件在仓库里标记为「客户机回传」。
        </template>
      </p>

      <!-- 目标机器 -->
      <div class="target-head">
        <span class="muted small">
          目标客户机（已选 <strong>{{ selectedAgents.size }}</strong> 台
          <template v-if="selectedOfflineCount > 0">
            · 其中 <strong>{{ selectedOfflineCount }}</strong> 台当前离线，会在上线后自动补领
          </template>
          <template v-if="isPush && selectedAgents.size > 0">
            · 已填存放路径 <strong>{{ dirsFilledCount }}</strong>/{{ selectedAgents.size }} 台
          </template>）
        </span>

        <div class="panel-actions">
          <input
            v-model="agentKeyword"
            class="search"
            type="search"
            placeholder="按名称 / IP / 机器码筛选"
          />
          <button class="sm" @click="selectFiltered">全选（筛选后 {{ filteredAgents.length }}）</button>
          <button class="sm" :disabled="onlineAgents.length === 0" @click="selectOnline">
            只选在线（{{ onlineAgents.length }}）
          </button>
          <button class="sm" :disabled="selectedAgents.size === 0" @click="clearSelection">清空</button>
        </div>
      </div>

      <div v-if="agents.length === 0" class="empty">
        <strong>还没有客户端</strong>
        先在「客户端」页把安装包装到目标机器上。
      </div>

      <div v-else-if="filteredAgents.length === 0" class="empty">
        <strong>没有匹配的客户端</strong>
        换个关键词试试。
      </div>

      <template v-else>
        <!--
          表头与下面的行**共用同一套列宽**（同一个 grid 模板），这样"名称 / 状态 / IP / 存放路径"
          才会上下对齐。以前这里是 flex + 行内排布，各列宽度随内容浮动，
          一列长了整行就歪 —— 一眼看过去没法纵向扫。
        -->
        <div class="rows-head" :class="{ push: isPush }">
          <span></span>
          <span class="muted small">客户机</span>
          <span class="muted small">状态</span>
          <span class="muted small">IP 地址</span>
          <span v-if="isPush" class="muted small head-dir">
            存放路径 · 那一台机器上的落地目录（未勾选时只读显示当前落点）
            <button
              class="sm ghost"
              :disabled="!canApplyFirstToAll"
              title="把第一台已填的路径抄给其余已选客户机"
              @click="applyFirstToAll"
            >
              首台套用到全部
            </button>
          </span>
        </div>

        <div class="agent-rows">
          <div
            v-for="agent in filteredAgents"
            :key="agent.id"
            class="agent-row"
            :class="{ picked: selectedAgents.has(agent.id), push: isPush }"
          >
            <input
              :id="`pick-agent-${agent.id}`"
              class="pick"
              type="checkbox"
              :checked="selectedAgents.has(agent.id)"
              :title="`勾选「${agent.displayName}」`"
              @change="toggleAgent(agent.id)"
            />

            <!-- label + for：点名字也能勾选（表格里这是最顺手的一个大靶子）。 -->
            <label
              class="target-name ellipsis"
              :for="`pick-agent-${agent.id}`"
              :title="agent.displayName"
            >
              {{ agent.displayName }}
            </label>

            <span class="badge state-cell" :class="agentStateClass(agent.state)">
              {{ agentStateLabel(agent.state) }}
            </span>

            <span class="muted small mono target-addr" :title="agent.lastAddress || '（还不知道地址）'">
              {{ agent.lastAddress || '—' }}
            </span>

            <!-- 存放路径：只有下发方向才需要；每一行都是"这一台"自己的。 -->
            <div v-if="isPush" class="dir-cell">
              <!--
                `initial-path` = 这台机器上次用的落脚目录：输入框还空着时，
                点「浏览…」**直接从那一层开始**，不必从盘符列表一层层点进去。
                `remember="target"` = 允许把浏览到的目录直接记成这台机器的默认目录
                （记的就是当前所在那一层，所以下次还能当起落点用）。
              -->
              <AgentPathPicker
                v-if="selectedAgents.has(agent.id)"
                v-model="targetDirs[agent.id]"
                :targets="[pickerTarget(agent.id, agent)]"
                :suggestions="inboxDirsOf(agent.id)"
                :initial-path="lastTargetDirOf(agent.id)"
                label=""
                accept="directory"
                remember="target"
                @remembered="onPathRemembered"
              />
              <!--
                未勾选：**只读**显示这台机器"现在的落点"（2026-10-15）。

                以前这里写"勾选后才能指定" —— 那句话对"我该勾哪几台"毫无帮助，
                而"各台机器分别落到哪"恰恰是决定勾选之前要看的东西。
                现在显示的值**就是勾选后输入框的初值**（同源），不存在"看到 A、勾上变 B"。

                取值优先级见 `dirPreviews`：① 上次下发用过的目录；② 客户机上报的默认目录。
              -->
              <span v-else class="dir-view" :title="dirPreviews[agent.id]?.path || '这台机器还没有可显示的落点'">
                <template v-if="dirPreviews[agent.id]?.path">
                  <span class="muted small dir-whence">{{ dirPreviews[agent.id]?.whence }}</span>
                  <code class="mono dir-path">{{ dirPreviews[agent.id]?.path }}</code>
                </template>
                <span v-else class="muted small">未记录（勾选后可指定，留空 = 客户机默认目录）</span>
              </span>
            </div>
          </div>
        </div>
      </template>

      <div class="submit-row">
        <button class="primary" :disabled="!canSubmit" @click="create">
          {{ creating ? '创建中…' : isPush ? '开始下发' : '开始回传' }}
        </button>
        <span v-if="selectedFile && isPush" class="muted small">
          将推送：{{ selectedFile.name }}（{{ fmtBytes(selectedFile.size) }}）到 {{ selectedAgents.size }} 台机器
        </span>
      </div>
    </section>

    <!-- ═══ 任务列表 ═══ -->
    <section class="panel">
      <div class="panel-head">
        <h2>
          分发任务
          <span class="muted small">（最近 {{ tasks.length }} 条）</span>
        </h2>

        <div class="panel-actions">
          <button class="sm" :disabled="loading" @click="refresh">{{ loading ? '刷新中…' : '刷新' }}</button>
        </div>
      </div>

      <p class="muted small" style="margin: -4px 0 12px">
        进度由客户机上报后经服务端实时推来（每传一段报一次）。<strong>「等待中」不等于卡住</strong> ——
        目标机器离线时明细就停在等待中，它开机连上来会自己补领。
      </p>

      <p v-if="loadError" class="notice err" style="margin-bottom: 12px">{{ loadError }}</p>

      <div v-if="tasks.length === 0" class="empty">
        <strong>还没有分发任务</strong>
        在上面选一个仓库文件和几台目标机器开始下发；或在「文件仓库」里点某个文件的「分发」。
      </div>

      <div v-else class="task-list">
        <div v-for="task in tasks" :key="task.id" class="task">
          <div class="task-head" @click="expandedId = expandedId === task.id ? null : task.id">
            <span class="badge" :class="task.direction === DISTRIBUTION_DIRECTIONS.push ? 'send' : 'recv'">
              {{ distributionDirectionLabel(task.direction) }}
            </span>

            <span class="badge" :class="distributionStateClass(task.state)">
              {{ distributionStateLabel(task.state) }}
            </span>

            <span class="task-title ellipsis" :title="taskTitle(task)">#{{ task.id }} · {{ taskTitle(task) }}</span>

            <span class="muted small mono nowrap">
              成功 {{ task.succeeded }}/{{ task.total }}<template v-if="task.failed > 0"> · 失败 {{ task.failed }}</template>
            </span>

            <span class="muted small nowrap">{{ fmtDateTime(task.createdAt) }}</span>
            <span class="muted small">{{ expandedId === task.id ? '收起' : '明细' }}</span>
          </div>

          <div class="task-meta muted small">
            <template v-if="task.direction === DISTRIBUTION_DIRECTIONS.push">
              大小 {{ fmtBytes(task.fileSize) }}
              · 目标目录 {{ taskDirSummary(task) }}
            </template>
            <template v-else>
              源路径 <span class="mono">{{ task.sourcePath || '—' }}</span>
            </template>
            <template v-if="task.note"> · 备注 {{ task.note }}</template>
            <template v-if="task.createdBy"> · 发起人 {{ task.createdBy }}</template>
          </div>

          <!-- 目标明细 -->
          <div v-if="expandedId === task.id" class="items">
            <div v-for="item in task.items" :key="item.id" class="item">
              <div class="item-line">
                <span class="ellipsis item-name" :title="item.agentName">{{ item.agentName }}</span>
                <span class="badge" :class="agentStateClass(item.agentState)">{{ agentStateLabel(item.agentState) }}</span>
                <span class="badge" :class="distributionItemStateClass(item.state)">
                  {{ distributionItemStateLabel(item.state) }}
                </span>

                <!-- 这一台自己的落点：每台可能不同，所以必须逐行显示。 -->
                <span class="ellipsis mono item-dir" :title="itemDirText(item)">
                  {{ task.direction === DISTRIBUTION_DIRECTIONS.push ? itemDirText(item) : '—' }}
                </span>

                <span class="item-bar">
                  <i :class="itemBarClass(item)" :style="{ width: itemBarWidth(item) }" />
                </span>

                <span class="mono item-text">{{ itemText(item) }}</span>

                <!-- 速率：传输途中是 20 秒滑窗的吞吐，跑完冻结成整段平均速率。 -->
                <span class="mono item-rate" :class="itemRateClass(item)">{{ itemRateText(item) }}</span>
              </div>

              <div v-if="item.error" class="item-error err">{{ item.error }}</div>
            </div>
          </div>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.dir-switch {
  display: flex;
  gap: 6px;
  margin-bottom: 12px;
}

.dir-switch button {
  font-size: 13px;
}

.dir-switch button.active {
  background: var(--accent-soft);
  border-color: var(--accent);
  color: var(--accent);
  font-weight: 500;
}

.form-row {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
}

.form-row label.wide {
  grid-column: span 1;
}

/* ── 目标机器 ── */
.target-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
  margin: 8px 0 6px;
}

.search {
  width: 200px;
  font-size: 12px;
}

/* ═══ 客户机行：表头与行共用一个 grid 模板 ⇒ 各列上下对齐 ═══
   `勾选 | 名称 | 状态 | IP 地址 | 存放路径`
   最后一条是"占满剩余宽度"的那一列（路径本身可能很长，不能给它固定宽度）。 */
.rows-head,
.agent-row {
  display: grid;
  grid-template-columns: 24px minmax(110px, 1fr) 76px 130px;
  align-items: center;
  gap: 10px;
}

.rows-head.push,
.agent-row.push {
  grid-template-columns: 24px minmax(110px, 1fr) 76px 130px minmax(240px, 1.5fr);
}

/* 表头：与下面的行共用同一套列宽。 */
.rows-head {
  padding: 0 10px 6px;
  border-bottom: 1px solid var(--border);
}

.head-dir {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

/* 一台一行的横排列表 —— 每行右侧就是这台机器自己的存放路径。 */
.agent-rows {
  display: flex;
  flex-direction: column;
  gap: 2px;
  max-height: 420px;
  overflow-y: auto;
  padding: 6px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--panel-soft);
}

.agent-row {
  padding: 4px 6px;
  border-radius: 5px;
  font-size: 12px;
  color: var(--text);
}

.agent-row:hover {
  background: var(--panel);
}

.agent-row.picked {
  background: var(--accent-soft);
}

/* ⚠️ 全局 `input { width: 100% }` 同样命中复选框 —— 必须逐项复位，否则它会吃掉整列。 */
.agent-row input[type='checkbox'].pick {
  width: 14px;
  height: 14px;
  padding: 0;
  margin: 0;
  border: none;
  background: none;
  flex: none;
  justify-self: center;
  accent-color: var(--accent);
}

/* 名字是一个 `<label for>`（点它能勾选），因此 `cursor: pointer` 是必要的提示。 */
.target-name {
  margin: 0;
  min-width: 0;
  cursor: pointer;
}

/* 状态列固定宽度 ⇒ "在线 / 不稳定 / 已吊销"这些长短不一的词不会把后面的列顶歪。 */
.state-cell {
  justify-self: start;
  white-space: nowrap;
}

.target-addr {
  font-size: 11px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* 这一行的存放路径（含它自己的浏览面板）。 */
.dir-cell {
  min-width: 0;
}

.dir-cell label {
  margin: 0;
}

/* 未勾选时的落点：**只读文本**。刻意不做成输入框的样子 ——
   做得像输入框就会有人先去点它，然后以为"点了没反应"。 */
.dir-view {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  line-height: 1.5;
}

/* 「上次 / 默认」：两个字说明那个路径是哪来的，否则"为什么这台显示 A、那台显示 B"没法解释。 */
.dir-whence {
  flex: none;
  font-size: 11px;
}

/* 路径可能很深：截断显示，全文在 `title` 里 —— 这一列的宽度不能被一个长路径顶开。 */
.dir-path {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 11px;
}

.submit-row {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
  margin-top: 12px;
}

button.ghost {
  border-color: transparent;
  background: transparent;
  color: var(--accent);
  padding: 1px 6px;
}

button.ghost:disabled {
  color: var(--muted);
}

/* ── 任务卡片 ── */
.task-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.task {
  border: 1px solid var(--border);
  border-radius: var(--radius);
  padding: 8px 10px;
}

.task-head {
  display: grid;
  grid-template-columns: 52px 68px minmax(120px, 1fr) 150px 142px 44px;
  gap: 8px;
  align-items: center;
  cursor: pointer;
  font-size: 12px;
}

.task-title {
  font-weight: 500;
}

.task-meta {
  margin-top: 3px;
  padding-left: 2px;
}

.items {
  margin-top: 8px;
  padding-top: 8px;
  border-top: 1px dashed var(--border);
  max-height: 360px;
  overflow-y: auto;
}

.item + .item {
  margin-top: 5px;
}

/* 列：机器名 | 在线 | 传输状态 | 这一台的落点 | 进度条 | 字节 | 速率 */
.item-line {
  display: grid;
  grid-template-columns:
    minmax(110px, 1.1fr) 58px 62px minmax(110px, 1fr) minmax(80px, 1fr) 128px 84px;
  gap: 8px;
  align-items: center;
  font-size: 12px;
}

.item-name {
  max-width: none;
}

.item-dir {
  font-size: 11px;
}

.item-bar {
  height: 6px;
  border-radius: 3px;
  background: var(--panel-soft);
  border: 1px solid var(--border);
  overflow: hidden;
}

.item-bar i {
  display: block;
  height: 100%;
  border-radius: 3px;
  background: var(--border-strong);
  transition: width 0.3s ease;
}

.item-bar i.ok {
  background: var(--ok);
}

.item-bar i.err {
  background: var(--err);
}

.item-bar i.run {
  background: var(--accent);
}

.item-text {
  font-size: 11px;
  text-align: right;
  white-space: nowrap;
}

/* 速率与字节同侧右对齐，形成一块"数字区"，扫一眼就能对上。 */
.item-rate {
  font-size: 11px;
  text-align: right;
  white-space: nowrap;
}

.item-rate.live {
  color: var(--accent);
}

.item-rate.frozen {
  color: var(--muted);
}

.item-error {
  margin: 2px 0 0 2px;
  font-size: 11px;
  word-break: break-all;
}

.nowrap {
  white-space: nowrap;
}

.ellipsis {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
