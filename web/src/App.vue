<script setup lang="ts">
import { computed, onMounted, onUnmounted, provide, reactive, ref, watch } from 'vue'
import * as signalR from '@microsoft/signalr'
import { api } from './api'
import type {
  ActionExecutionDto,
  AgentInstallerInfoDto,
  AgentLiveMetricsDto,
  ButtonConfig,
  ComboButton,
  ConfigView,
  CurrentUser,
  LicenseDto,
  LogDto,
  ServerAddressChangedDto,
  StatusSnapshot,
  SubButton,
  SubButtonSet,
  TrafficLogDto,
  TransferProgressDto,
} from './types'
import { flattenComboExecution, mergeTiles } from './types'
import { fmtTimeOnly } from './format'
import { LICENSE_BLOCKED_HINT, LICENSE_BLOCKED_KEY } from './licenseGate'
import {
  clampTabForRole,
  clearActiveTab,
  persistActiveTab,
  readActiveTab,
  type TabId,
} from './tabState'
import StatusCards from './components/StatusCards.vue'
import ButtonGrid from './components/ButtonGrid.vue'
import SubButtonSheet from './components/SubButtonSheet.vue'
import TrafficPanel from './components/TrafficPanel.vue'
import OpsLogPanel from './components/OpsLogPanel.vue'
import SettingsPanel from './components/SettingsPanel.vue'
import EditPanel from './components/EditPanel.vue'
import TimerOverviewPanel from './components/TimerOverviewPanel.vue'
import CommandLibraryPanel from './components/CommandLibraryPanel.vue'
import ActionResultPanel from './components/ActionResultPanel.vue'
import UserManagementPanel from './components/UserManagementPanel.vue'
import LicensePanel from './components/LicensePanel.vue'
import AgentsPanel from './components/AgentsPanel.vue'
import FilesPanel from './components/FilesPanel.vue'
import RemoteDesktopPanel from './components/RemoteDesktopPanel.vue'
import ChangePasswordDialog from './components/ChangePasswordDialog.vue'

// ═══════════════════════════════════════════════════════════════
// 会话
// ═══════════════════════════════════════════════════════════════

const user = ref<CurrentUser | null>(null)
const booting = ref(true)
const loggingIn = ref(false)
const loginError = ref('')

// ── 记住账号密码（本机浏览器） ──
//
// ⚠️ 凭据是**明文**写进 localStorage 的：这是"局域网内网中控、登录页就在本机"场景下的取舍。
// 明文意味着同源脚本或能接触本机的人可以读到口令。UI 上**不再**显式提示这一点
//（勾选框文案只留"记住账号密码"，按需求去掉了括号说明），因此这条风险仅在代码/文档里留痕。
// 不勾选（或取消勾选后再次登录）会立即清除既有记录；读取失败一律按"没记住"处理，
// 绝不能因为 localStorage 不可用（隐私模式 / 配额满 / 数据损坏）而让登录页打不开。
const REMEMBER_KEY = 'cs.remembered-login'

interface RememberedLogin {
  userName: string
  password: string
}

function readRememberedLogin(): RememberedLogin | null {
  try {
    const raw = window.localStorage.getItem(REMEMBER_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw) as Partial<RememberedLogin>
    if (typeof parsed.userName !== 'string' || typeof parsed.password !== 'string') return null
    return { userName: parsed.userName, password: parsed.password }
  } catch {
    return null
  }
}

function persistRememberedLogin(userName: string, password: string) {
  try {
    window.localStorage.setItem(REMEMBER_KEY, JSON.stringify({ userName, password }))
  } catch {
    /* 不可用时静默降级为"本次不记住"，不打断登录 */
  }
}

function clearRememberedLogin() {
  try {
    window.localStorage.removeItem(REMEMBER_KEY)
  } catch {
    /* 同上 */
  }
}

const rememberedLogin = readRememberedLogin()

/** 登录页「记住账号密码」勾选框；初始态反映本机是否已有记录。 */
const rememberLogin = ref(rememberedLogin !== null)

const loginForm = reactive({
  userName: rememberedLogin?.userName ?? 'admin',
  password: rememberedLogin?.password ?? '',
})

// ── 改密（M3）──
/** 顶栏主动打开改密弹窗（强制态也复用同一个组件）。 */
const passwordDialogOpen = ref(false)

/**
 * 是否被强制改密。
 *
 * 由服务端给的 `mustChangePassword` 驱动 —— 登录响应与 `/api/auth/me` 都带它，
 * 所以**刷新页面也拦得住**（只在登录响应里带的话，刷一下就能绕过）。
 * 它同时决定弹窗能否关闭：强制态下弹窗没有关闭按钮，点遮罩与 ESC 都不生效。
 *
 * ⚠️ 2026-10-09 起服务端**默认不再标记任何账号**（出厂管理员、新建账号、重置密码都不标记），
 * 因此这个值通常恒为 `false`。保留它是为了兼容把 `Server:Auth:EnforcePasswordChange`
 * 显式置 `true`、并手工标记账号的部署 —— 那种情况下这里会照常弹强制改密窗。
 */
const mustChangePassword = computed(() => user.value?.mustChangePassword === true)

/** 只有管理员能进「用户」页 —— 服务端对整个 `/api/users` 也是 AdminOnly。 */
const isAdmin = computed(() => user.value?.role === 'Admin')

/**
 * 顶栏上的角色中文名。
 *
 * 这里**不得不**放一份本地映射：`GET /api/users/roles` 是 AdminOnly 的，
 * 非管理员拿不到那份权威清单。所以用户管理页用服务端返回的 `displayName`，
 * 而顶栏这个只读标签用本地表 —— 新增角色时两处都要动（见 `Contracts.Roles`）。
 */
const ROLE_LABELS: Record<string, string> = {
  Admin: '管理员',
  Operator: '操作员',
  Viewer: '查看者',
}

function roleLabel(role: string | undefined): string {
  if (!role) return '未知'
  return ROLE_LABELS[role] ?? role
}

// ═══════════════════════════════════════════════════════════════
// 设备授权（M3）
// ═══════════════════════════════════════════════════════════════

/**
 * 授权状态。**所有已登录角色**都能读（见 `LicenseController`）。
 *
 * 由 App 持有而不是授权页自己持有：顶栏徽章、未授权横幅、控制台按钮的置灰
 * 读的必须是同一份数据，否则会出现"顶栏说已授权、横幅说没授权"这种自相矛盾的画面。
 */
const license = ref<LicenseDto | null>(null)

/**
 * 核心能力是否**已被服务端拦下**。
 *
 * 判据（与服务端 `LicenseGateState.IsBlocked` 完全一致）：
 * **生效档位是 `Enforce` 且当前不可用**。
 *
 * 两个容易搞错的地方：
 * 1. 读的是**生效**档位而不是配置档位 —— 配置写 `Warn` 但本机有试用记录时，
 *    服务端会把生效档位提升为 `Enforce`，只看配置就会以为"还没开始拦"。
 * 2. 读的是 `isUsable` 而不是比较状态码 —— "可用"是一个**白名单**
 *    （只含 `Authorized` / `Trial`），用 `statusCode >= 1` 会把"已过期"当成成功。
 */
const licenseBlocked = computed(
  () => license.value !== null && license.value.enforcement === 'Enforce' && !license.value.isUsable,
)

/**
 * 是否需要在界面上提示授权问题。
 *
 * `Off` 档位下什么都不拦，也就不该有横幅 —— 那是内部开发/无需授权的场景，
 * 天天挂一条"未授权"只会让人学会忽略横幅。
 */
const licenseNotice = computed(() => {
  const s = license.value
  if (!s || s.isUsable || s.enforcement === 'Off') return null

  return s.enforcement === 'Enforce'
    ? { kind: 'err' as const, text: '本机尚未获得授权，核心功能已停用。' }
    : { kind: 'warn' as const, text: '本机尚未获得授权。当前档位为「仅提示」，功能仍可用。' }
})

/** 顶栏徽章文案：已授权 / 试用 N 天 / 未授权（含已过期、异常）。 */
const licenseBadge = computed(() => {
  const s = license.value
  if (!s) return null

  if (!s.isUsable) return { text: s.statusDisplayName, cls: 'err' }

  // 本机试用与厂商签发的试用都是 `trial` —— 都是"还剩几天"的语义。
  if (s.kind === 'trial') {
    return { text: s.remainingDays > 0 ? `试用 ${s.remainingDays} 天` : '试用', cls: 'warn' }
  }

  if (s.remainingDays > 0 && s.remainingDays < 9999) {
    return { text: `已授权 · 剩 ${s.remainingDays} 天`, cls: 'ok' }
  }

  return { text: '已授权', cls: 'ok' }
})

/**
 * 把停摆状态透传给编辑页里深藏的执行入口（见 `licenseGate.ts` 的说明）。
 */
provide(
  LICENSE_BLOCKED_KEY,
  computed(() => licenseBlocked.value),
)

async function refreshLicense() {
  try {
    license.value = await api.license()
  } catch {
    /* 拿不到就保持现状：这是展示性数据，不该因为一次失败就清空徽章 */
  }
}

/** 授权页做完写操作后回传的最新状态（避免它自己再发一次 GET）。 */
function onLicenseChanged(next: LicenseDto) {
  license.value = next
}

// ═══════════════════════════════════════════════════════════════
// 数据
// ═══════════════════════════════════════════════════════════════

const status = ref<StatusSnapshot | null>(null)
const config = ref<ConfigView | null>(null)
const traffic = ref<TrafficLogDto[]>([])
const opsLogs = ref<LogDto[]>([])
const trafficCapacity = ref(400)

const loadingStatus = ref(false)
const loadingTraffic = ref(false)
const loadingLogs = ref(false)

/**
 * 本标签页的 `sessionStorage`。
 *
 * ⚠️ 访问 `window.sessionStorage` **本身**就可能抛（隐私模式 / 被策略禁用），
 * 所以这一步也要包起来 —— 拿不到就返回 `null`，交给 `tabState` 按"没有记录"处理。
 * 这是本文件唯一该知道"存储住在哪"的地方，其余规则都在那个纯模块里。
 */
function sessionTabStorage(): Storage | null {
  try {
    return window.sessionStorage
  } catch {
    return null
  }
}

/**
 * 当前顶栏页签。
 *
 * ⚠️ 初值**不再是写死的 `'console'`**，而是从 `sessionStorage` 恢复（见 `tabState.ts`）：
 * 浏览器刷新是整页重建，内存里的 `ref` 会归零，用户正看着「远程桌面」时按一下 F5
 * 就被扔回控制台 —— 界面把用户的位置弄丢了。
 *
 * ⚠️ 此刻 `user` 还没加载（`me()` 尚未返回），所以这里**只能校验"值合不合法"**；
 * 权限收敛留到 `afterLogin()` 里再补一刀 `clampTabForRole`。
 */
const activeTab = ref<TabId>(readActiveTab(sessionTabStorage()))

// 页签一变就记下来。放在这里，而不是逐个写进模板的 `@click` 里 —— 模板中有十几处
// `activeTab = '…'`，散着写迟早漏一处，而漏掉的那处**只在那一个页签上丢状态**，
// 表现出来就像"偶发"。
watch(activeTab, (tab) => persistActiveTab(sessionTabStorage(), tab))

/**
 * 客户端安装包元信息（顶栏右上角的下载按钮用它）。
 *
 * `null` = 还没查到 / 查不到 —— 此时按钮照常显示但禁用，
 * 因为"这个功能存在但当前没包可下"和"这个功能不存在"对用户是两件事。
 */
const agentInstaller = ref<AgentInstallerInfoDto | null>(null)

/** 客户端主机页的组件引用：收到 SignalR 的 Agent* 事件时让它自己刷新。 */
const agentsPanel = ref<InstanceType<typeof AgentsPanel> | null>(null)

/**
 * 文件分发页的组件引用（M4.4）。
 *
 * 收到 `TransferProgress` 时**把那一格就地贴上去**，而不是让分发页重拉整个任务列表 ——
 * 一条 128 KB/s 的传输每秒会产生若干条事件，全量刷新等于把"50 个任务 × 每个几百条明细"
 * 反复搬运，而真正变的只是其中一台机器的一个数字。
 */
const filesPanel = ref<InstanceType<typeof FilesPanel> | null>(null)

/**
 * 正在执行中的按钮 / 子按钮 Id **集合**（可以有多个同时在跑）。
 *
 * 这里刻意从「单个 Id + `if (runningId) return`」改成集合：
 * 按钮的动作序列里常带 `DelayMs`（例如 5000ms），旧实现在那 5 秒里把整块面板锁死，
 * 用户想点别的按钮会被静默忽略 —— 看起来像"点了没反应"。
 *
 * 服务端**本来就是并发安全**的，不需要为这条需求动后端：
 * 延迟走 `await Task.Delay`（不占线程）、UDP/TCP 每次发送新建 socket、
 * 收发日志服务内部带锁、`ActionExecutionService` 是单例但只用局部变量。
 * 真正卡住的只有前端这道自我封锁。
 */
const runningIds = reactive(new Set<string>())
/** 「连通性自测」占用同一个集合，用一个不会与真实 Id 撞车的键。 */
const LOOPBACK_ID = '__loopback__'

/**
 * 刚执行过的瓦片 Id（短暂高亮）。
 *
 * 同样从单值改成集合 —— 并发下 A、B 接连成功，单值会让 A 的高亮被 B 顶掉。
 */
const justRanIds = reactive(new Set<string>())
const justRanTimers = new Map<string, number>()

const openSet = ref<SubButtonSet | null>(null)
const lastResult = ref<ActionExecutionDto | null>(null)
const lastError = ref<string | null>(null)
const notice = ref<{ kind: 'ok' | 'err' | 'warn'; text: string } | null>(null)

const connectionState = ref<'disconnected' | 'connecting' | 'connected'>('disconnected')

// 监听地址变更（M2-E5）—— 端口一换，当前页面所在的原点就没了，必须主动搬家。
const addressNotice = ref<{ kind: 'warn' | 'err'; text: string } | null>(null)
const redirectIn = ref(0)
let redirectTimer: number | null = null

let hub: signalR.HubConnection | null = null
let noticeTimer: number | null = null

// 与服务端环形缓冲容量保持一致，避免前端留一堆服务端早就丢掉的历史。
const MAX_TRAFFIC = 400
const MAX_LOGS = 500

/**
 * 控制台「最近收发」小面板显示多少条。
 *
 * 它是"执行完按钮顺手看一眼"的窗口，不是收发日志页（那里看全量）。
 * 20 条足够覆盖一次多动作按钮 + 几次对接码往返；再多就该去「收发日志」页了。
 */
const RECENT_TRAFFIC = 20

const loggedIn = computed(() => user.value?.isAuthenticated === true)
const tiles = computed(() => mergeTiles(config.value?.settings ?? null))
const freeLayout = computed(() => config.value?.settings?.freeLayoutEnabled ?? false)

/** 顶栏下载按钮的悬停说明 —— 把"为什么不能点"直接写在按钮上。 */
const agentInstallerHint = computed(() => {
  const info = agentInstaller.value

  if (!info) return '客户端安装包暂不可用。'
  if (!info.available) return info.hint ?? '服务端尚未放置客户端安装包。'

  const size = `${(info.sizeBytes / 1024 / 1024).toFixed(1)} MB`
  const version = info.version ? `v${info.version}` : '版本未标注'

  return (
    `${info.fileName}（${version}，${size}）\n`
    + '把它装到同一局域网的电脑上，客户端会自动注册并出现在「客户端」页。'
    + (info.versionMismatch ? `\n⚠️ 客户端版本与服务端 v${info.serverVersion} 不一致。` : '')
  )
})

/**
 * 正在执行的任务数（含「连通性自测」）。
 *
 * 执行结果面板只展示**最近完成**的那一次 —— 并发下必须让用户知道"还有别的在跑"，
 * 否则他会以为结果只有这一份、或者以为刚才那一下被吞了。
 */
const runningCount = computed(() => runningIds.size)

// ═══════════════════════════════════════════════════════════════
// 会话流程
// ═══════════════════════════════════════════════════════════════

async function loadMe() {
  try {
    user.value = await api.me()
  } catch {
    user.value = { userName: '', role: '', isAuthenticated: false, mustChangePassword: false }
  } finally {
    booting.value = false
  }

  if (loggedIn.value) await afterLogin()
}

async function doLogin() {
  loggingIn.value = true
  loginError.value = ''

  try {
    const res = await api.login(loginForm.userName, loginForm.password)
    user.value = {
      userName: res.userName,
      role: res.role,
      isAuthenticated: true,
      mustChangePassword: res.mustChangePassword,
    }

    // 记住凭据：以服务端规范化后的用户名为准；未勾选则清除旧记录。
    if (rememberLogin.value) {
      persistRememberedLogin(res.userName, loginForm.password)
    } else {
      clearRememberedLogin()
    }

    await afterLogin()
  } catch (e) {
    loginError.value = e instanceof Error ? e.message : '登录失败'
  } finally {
    loggingIn.value = false
  }
}

async function doLogout() {
  try {
    await api.logout()
  } catch {
    /* 登出失败也让前端回到登录态 */
  }

  await disconnectRealtime()
  user.value = { userName: '', role: '', isAuthenticated: false, mustChangePassword: false }
  passwordDialogOpen.value = false
  status.value = null
  config.value = null
  traffic.value = []
  opsLogs.value = []
  license.value = null
  lastResult.value = null
  lastError.value = null
  openSet.value = null

  // 🔥 页签也归零并抹掉记录：否则换个账号登进来，会停在**上一个用户**离开的那一页
  // （若那页是「用户」/「远程」，还会立刻被 `loadMe` 里的权限收敛打回控制台）。
  // ⚠️ 顺序有意义：先把 ref 打回控制台（`watch` 会顺手写一次），再清记录 ——
  //    这样即使 ref 本来就是 `'console'`（`watch` 不触发），残留的那条也能被删掉。
  activeTab.value = 'console'
  clearActiveTab(sessionTabStorage())
}

/**
 * 改密成功后的收尾。
 *
 * 服务端在改密成功时**已经重签了会话**（新 Cookie 的 `mustChangePassword` 已为 false），
 * 这里再拉一次 `me()` 是为了让前端的用户对象与那份 Cookie 对齐 ——
 * 否则界面会继续按旧的 `mustChangePassword = true` 弹着强制改密窗，
 * 而实际上服务端早已放行。
 */
async function onPasswordChanged() {
  passwordDialogOpen.value = false
  showNotice('ok', '密码已修改。')

  try {
    user.value = await api.me()
  } catch {
    /* 拿不到就保持现状；下次刷新会自然对齐 */
  }
}

async function afterLogin() {
  await Promise.all([
    refreshStatus(),
    refreshConfig(),
    refreshTraffic(),
    refreshLogs(),
    refreshLicense(),
    refreshAgentInstaller(),
  ])
  await connectRealtime()

  // 🔥 权限收敛（见 `tabState.ts` 的 `clampTabForRole`）。放在这里而不是 `loadMe` 的
  // 分支里，是因为**两条路径都会走到这儿**：
  //   · 刷新 —— `loadMe` → 会话仍有效 → `afterLogin`；
  //   · 登录页手动登录 —— `doLogin` → `afterLogin`。
  // 后一条同样需要收敛：否则操作员在一个"上次管理员停过「远程桌面」"的标签页上登录，
  // `activeTab === 'remote' && isAdmin` 不成立，会掉进模板末尾兜底的 `v-else`（「设置」页）。
  //
  // ⚠️ 刻意**不放在** `loadMe` 里无条件执行：`me()` 失败（Cookie 已过期）时用户还没登录，
  //    那不是"权限不匹配"，而是"还不知道是谁"。此时若也收敛，会把存储里的 `remote`
  //    抹成 `console` —— 管理员随后重新登录就再也回不到原来那一页了，正是本次要修的病。
  activeTab.value = clampTabForRole(activeTab.value, isAdmin.value)
}

// ═══════════════════════════════════════════════════════════════
// 数据加载
// ═══════════════════════════════════════════════════════════════

async function refreshStatus() {
  loadingStatus.value = true
  try {
    status.value = await api.status()
  } catch {
    /* 实时通道会兜底推送 */
  } finally {
    loadingStatus.value = false
  }
}

async function refreshConfig() {
  try {
    config.value = await api.config()

    // 若弹层里那个集合已被别处改过，用最新配置刷新它，避免显示过期内容。
    const open = openSet.value
    if (open) {
      openSet.value = config.value.settings.subButtonSets.find((s) => s.id === open.id) ?? null
    }
  } catch (e) {
    showNotice('err', `读取配置失败：${e instanceof Error ? e.message : String(e)}`)
  }
}

async function refreshTraffic() {
  loadingTraffic.value = true
  try {
    traffic.value = await api.traffic(MAX_TRAFFIC)
  } catch {
    /* 忽略：实时通道会补 */
  } finally {
    loadingTraffic.value = false
  }
}

async function refreshLogs() {
  loadingLogs.value = true
  try {
    opsLogs.value = await api.logs(MAX_LOGS)
  } catch {
    /* 忽略 */
  } finally {
    loadingLogs.value = false
  }
}

/**
 * 查一次客户端安装包元信息（顶栏右上角那个下载按钮）。
 *
 * 失败时保持 `null` —— 按钮会显示成"不可用"，而不是消失：
 * "有这功能但暂时没包"和"没这功能"，对现场工程师是两件完全不同的事。
 */
async function refreshAgentInstaller() {
  try {
    agentInstaller.value = await api.agentInstallerInfo()
  } catch {
    agentInstaller.value = null
  }
}

/**
 * 下载客户端安装包。
 *
 * 直接开直链而不是 fetch 成 blob：安装包几十 MB，走 `<a>` / 新标签由浏览器接管下载，
 * 既不占内存也不会让页面假死（服务端还开了 Range，支持断点续传）。
 */
function downloadAgentInstaller() {
  window.open(api.agentInstallerUrl(), '_blank', 'noopener')
}

async function doClearTraffic() {
  try {
    await api.clearTraffic()
    traffic.value = []
    showNotice('ok', '收发日志缓冲已清空。')
  } catch (e) {
    showNotice('err', `清空失败：${e instanceof Error ? e.message : String(e)}`)
  }
}

// ═══════════════════════════════════════════════════════════════
// 监听地址迁移（M2-E5）
// ═══════════════════════════════════════════════════════════════

/**
 * 倒计时后把页面搬到新端口的同源地址上。
 *
 * 为什么必须由前端主动跳、不能指望浏览器自动重连：
 * 端口是 origin 的一部分，旧原点上的一切（HTTP、WebSocket）都是"另一个站点"。
 * SignalR 的 withAutomaticReconnect 只会重试**同一个 URL**，它不可能猜到新端口。
 * 所以由服务端把新端口推过来，前端自己 `location.replace`。
 */
function scheduleRedirect(port: number, seconds: number) {
  if (redirectTimer !== null) window.clearInterval(redirectTimer)
  redirectIn.value = seconds

  redirectTimer = window.setInterval(() => {
    redirectIn.value -= 1
    if (redirectIn.value > 0) return

    if (redirectTimer !== null) window.clearInterval(redirectTimer)
    redirectTimer = null

    const url = new URL(window.location.href)
    url.port = String(port)
    window.location.replace(url.toString())
  }, 1000)
}

/**
 * 设置页里手动热切换了端口 —— 与 SignalR 的 `changed` 通知走同一条倒计时通道。
 *
 * 之所以要这条**本地兜底**：服务端确实会广播，但实时通道此时可能正好断线，
 * 那就没人让页面搬家了。`scheduleRedirect` 每次都会重置计时器，所以
 * 「广播 + 本地下发」重复触发不会跳两次。
 */
function onAddressSwitched(port: number) {
  addressNotice.value = { kind: 'warn', text: `监听端口已切换为 ${port}，本页所在地址已失效。` }
  scheduleRedirect(port, 5)
}

// ═══════════════════════════════════════════════════════════════
// 实时通道
// ═══════════════════════════════════════════════════════════════

async function connectRealtime() {
  if (hub) return
  connectionState.value = 'connecting'

  hub = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/events')
    .withAutomaticReconnect([0, 2000, 5000, 10000])
    .configureLogging(signalR.LogLevel.Warning)
    .build()

  hub.on('LogAdded', (item: LogDto) => {
    opsLogs.value = [...opsLogs.value, item].slice(-MAX_LOGS)
  })

  hub.on('StatusChanged', (snapshot: StatusSnapshot) => {
    status.value = snapshot
  })

  hub.on('ActionResult', (result: ActionExecutionDto) => {
    lastResult.value = result
    lastError.value = null
  })

  hub.on('TrafficLogAdded', (item: TrafficLogDto) => {
    traffic.value = [...traffic.value, item].slice(-MAX_TRAFFIC)
  })

  hub.on('ConfigChanged', () => {
    // 只推版本号，不推配置本体 —— 客户端自己重拉，避免大配置把实时通道撑爆。
    void refreshConfig()
  })

  // 授权变更（导入 / 生成试用 / 移除 / 到期）—— 载荷就是完整状态，直接替换即可。
  // 有了它，另一台设备上导入了授权，本页的徽章与横幅会**当场**跟着变。
  hub.on('LicenseChanged', (next: LicenseDto) => {
    license.value = next
  })

  // 客户端在线表变化（M4）：注册 / 改名 / 吊销 / 心跳刷新。
  //
  // 刻意**只让面板自己重拉全量列表**，不在前端做增量合并：
  // 在线表规模有限（几十到几百台），全量刷新比合并简单得多，
  // 也不会出现"漏了一条事件就永久不同步"的死角。
  //
  // ⚠️ 文件分发页也要跟着刷：它的「新建分发任务」读的是同一份在线表（目标多选）。
  //    漏了这一条，用户会在这一页等着给一台刚开机的机器分发，而列表里根本没有它。
  hub.on('AgentChanged', () => {
    void agentsPanel.value?.refresh()
    void filesPanel.value?.refreshAgents()
  })
  hub.on('AgentOnline', () => {
    void agentsPanel.value?.refresh()
    void filesPanel.value?.refreshAgents()
  })
  hub.on('AgentOffline', () => {
    void agentsPanel.value?.refresh()
    void filesPanel.value?.refreshAgents()
  })

  // 客户端实时遥测（M4.2）：载荷**只带最新一帧**（见 `AgentLiveMetricsDto`），
  // 因此这里不做全量刷新 —— 直接把它贴到对应那一行上即可。
  //
  // 为什么不图省事调 refresh()：这条事件每 2 秒来一次（服务端节流后的节奏），
  // 全量刷新会把"几十台机器的完整列表"每秒搬一次，而真正变的只是一个数字。
  hub.on('AgentTelemetryUpdated', (live: AgentLiveMetricsDto) => {
    agentsPanel.value?.applyLiveMetrics(live)
  })

  // 文件传输进度（M4.4）：一条事件只带**某个目标**的一格变化
  // （见 `TransferProgressDto`），所以就地更新那一行即可 —— 不重拉任务列表。
  //
  // 事件里**不带任务级状态**，分发页会自己按明细重算汇总；遇到"本地列表里没有的任务"
  // （别处建的 / 已经翻页翻过去了）它会自己拉一次全量兜底。
  hub.on('TransferProgress', (progress: TransferProgressDto) => {
    filesPanel.value?.applyProgress(progress)
  })

  // 端口热切换：服务端在**切换成功后**推这条。此刻旧端口已不再接受新连接，
  // 但这条 WebSocket 还活着（已建立的连接不受监听 socket 关闭影响），所以通知能送达。
  hub.on('ServerAddressChanged', (payload: ServerAddressChangedDto) => {    if (payload.phase === 'changed' && payload.newPort) {
      addressNotice.value = {
        kind: 'warn',
        text: `监听端口已变更为 ${payload.newPort}，本页所在地址已失效。`,
      }
      scheduleRedirect(payload.newPort, 5)
    } else if (payload.phase === 'failed') {
      addressNotice.value = { kind: 'err', text: payload.message }
    }
  })

  hub.onreconnecting(() => (connectionState.value = 'connecting'))
  hub.onreconnected(() => {
    connectionState.value = 'connected'
    // 断线期间可能错过事件，重连后补拉一次。
    void refreshConfig()
    void refreshTraffic()
    void refreshLicense()
    // 传输进度尤其需要补拉：断线那段时间的进度事件不会补发，
    // 而分发页只在"还有未终结任务"时才低频兜底轮询。
    void filesPanel.value?.refresh()
  })
  hub.onclose(() => (connectionState.value = 'disconnected'))

  try {
    await hub.start()
    connectionState.value = 'connected'
  } catch {
    connectionState.value = 'disconnected'
  }
}

async function disconnectRealtime() {
  if (!hub) return
  try {
    await hub.stop()
  } catch {
    /* 忽略 */
  }
  hub = null
  connectionState.value = 'disconnected'
}

// ═══════════════════════════════════════════════════════════════
// 执行
// ═══════════════════════════════════════════════════════════════

function clearResult() {
  lastResult.value = null
  lastError.value = null
}

/** 短暂高亮某个瓦片。并发下每个 Id **各自计时**，不会互相顶掉。 */
function markRan(id: string) {
  justRanIds.add(id)

  const old = justRanTimers.get(id)
  if (old !== undefined) window.clearTimeout(old)

  justRanTimers.set(
    id,
    window.setTimeout(() => {
      justRanIds.delete(id)
      justRanTimers.delete(id)
    }, 1500),
  )
}

function showNotice(kind: 'ok' | 'err' | 'warn', text: string) {
  notice.value = { kind, text }
  if (noticeTimer !== null) window.clearTimeout(noticeTimer)
  noticeTimer = window.setTimeout(() => (notice.value = null), 6000)
}

/**
 * 授权闸门的**提前拦**（M3）。
 *
 * 服务端已经会拦（`403 license-required`），这里只是把"点了才知道"提前成"点之前就说明白"，
 * 并给出唯一能解决问题的去处。**不是**安全边界 —— 绕过前端照样被服务端拒绝。
 *
 * 返回 `true` 表示已拦下、调用方应当直接返回。
 */
function blockedByLicense(): boolean {
  if (!licenseBlocked.value) return false
  showNotice('warn', LICENSE_BLOCKED_HINT)
  return true
}

async function runButton(button: ButtonConfig) {
  // 只挡「同一个按钮被重复点」（等于防抖），**不再**挡其它按钮。
  if (runningIds.has(button.id)) return
  if (blockedByLicense()) return

  runningIds.add(button.id)
  lastError.value = null

  try {
    // 由服务端从配置里取动作（而不是前端回传），确保执行的一定是已保存的那份。
    lastResult.value = await api.executeActions({ buttonId: button.id, label: button.label })
    markRan(button.id)
  } catch (e) {
    lastError.value = `执行「${button.label}」失败：${e instanceof Error ? e.message : String(e)}`
    lastResult.value = null
  } finally {
    runningIds.delete(button.id)
    // 刻意**不**强制切回「控制台」：长延迟（如 5000ms）或并发时，用户很可能已经跑去
    // 编辑页干别的，执行完把他硬拽回来是骚扰。结果面板一直在控制台等着他。
  }
}

/**
 * 触发一个**组合按钮**（2026-10-15，需求⑤）。
 *
 * ⚠️ 与单按钮的差别不只是"多几段"：
 * <list type="bullet">
 *   <item>服务端要跑**整串**成员，含段间延迟 —— 一个 5 段、每段 1 秒的组合要 4 秒以上，
 *         所以"发送中"的标记必须挂着不放（与单按钮同一套 `runningIds`）；</item>
 *   <item>它返回的是**分段结果**，而结果面板认的是平铺形状 ⇒ 过一遍
 *         `flattenComboExecution`（把成员名前置到动作名上，信息不丢）。</item>
 * </list>
 */
async function runCombo(combo: ComboButton) {
  if (runningIds.has(combo.id)) return
  if (blockedByLicense()) return

  runningIds.add(combo.id)
  lastError.value = null

  try {
    const outcome = await api.executeCombo(combo.id)

    lastResult.value = flattenComboExecution(outcome)
    markRan(combo.id)

    // 有成员被剔除（指向的按钮已被删）时**必须说出来** ——
    // 否则现场只会看到"组合少执行了一步"，而不知道为什么。
    if (outcome.skippedButtonIds?.length) {
      showNotice('warn', `${outcome.summary}（有 ${outcome.skippedButtonIds.length} 个成员指向的按钮已不存在）`)
    }
  } catch (e) {
    lastError.value = `执行组合「${combo.label}」失败：${e instanceof Error ? e.message : String(e)}`
    lastResult.value = null
  } finally {
    runningIds.delete(combo.id)
  }
}

async function runSub(sub: SubButton) {
  if (runningIds.has(sub.id)) return
  if (blockedByLicense()) return

  runningIds.add(sub.id)
  lastError.value = null

  try {
    lastResult.value = await api.executeActions({ subButtonId: sub.id, label: sub.label })
    markRan(sub.id)
  } catch (e) {
    lastError.value = `执行子按钮「${sub.label}」失败：${e instanceof Error ? e.message : String(e)}`
    lastResult.value = null
  } finally {
    runningIds.delete(sub.id)
  }
}

async function runLoopback() {
  if (runningIds.has(LOOPBACK_ID)) return
  if (blockedByLicense()) return

  runningIds.add(LOOPBACK_ID)
  lastError.value = null

  try {
    const res = await api.loopback('SELFTEST-001')
    showNotice(
      res.ok ? 'ok' : 'err',
      res.ok
        ? `自环测试成功：已向本机监听端口 ${res.targetPort} 发出 ${res.bytesSent} 字节，请在「收发日志」查看。`
        : `自环测试失败：${res.message}`,
    )
  } catch (e) {
    showNotice('err', `自环测试失败：${e instanceof Error ? e.message : String(e)}`)
  } finally {
    runningIds.delete(LOOPBACK_ID)
  }
}

onMounted(async () => {
  await loadMe()

  try {
    const info = await api.trafficCount()
    trafficCapacity.value = info.capacity
  } catch {
    /* 用默认值 */
  }
})

onUnmounted(() => {
  void disconnectRealtime()
  for (const t of justRanTimers.values()) window.clearTimeout(t)
  justRanTimers.clear()
  if (noticeTimer !== null) window.clearTimeout(noticeTimer)
  if (redirectTimer !== null) window.clearInterval(redirectTimer)
})
</script>

<template>
  <div class="wrap">
    <!--
      监听端口变更横幅（M2-E5）：端口是 origin 的一部分，一改本页就"不在这个站上了"。
      服务端切换成功后推 ServerAddressChanged，这里给出可见提示并倒计时搬家。
    -->
    <div v-if="addressNotice" :class="['notice', addressNotice.kind]" style="margin-bottom: 14px">
      {{ addressNotice.text }}
      <template v-if="redirectIn > 0">
        将在 <b>{{ redirectIn }}</b> 秒后自动跳转到新地址…
      </template>
    </div>

    <div v-if="booting" class="center muted" style="padding: 15vh 0">正在连接服务…</div>

    <!-- ═══ 登录 ═══ -->
    <div v-else-if="!loggedIn" class="login-card">
      <h1>中控服务版</h1>
      <p class="muted">局域网中控 · 浏览器操作界面</p>

      <label>用户名</label>
      <input v-model="loginForm.userName" autocomplete="username" @keyup.enter="doLogin" />

      <label>密码</label>
      <input
        v-model="loginForm.password"
        type="password"
        autocomplete="current-password"
        @keyup.enter="doLogin"
      />

      <label class="inline">
        <input v-model="rememberLogin" type="checkbox" />
        <span class="muted small">记住账号密码</span>
      </label>

      <p v-if="loginError" class="notice err" style="margin-top: 12px">{{ loginError }}</p>

      <button class="primary block" style="margin-top: 18px" :disabled="loggingIn" @click="doLogin">
        {{ loggingIn ? '登录中…' : '登录' }}
      </button>

      <p class="muted small" style="margin-top: 14px">默认账号 admin / admin123</p>
    </div>

    <!-- ═══ 主界面 ═══ -->
    <div v-else>
      <header class="topbar">
        <div class="brand">
          <strong>{{ status?.product ?? '中控服务版' }}</strong>
          <span class="muted small">v{{ status?.version ?? '0.1.0' }}</span>
        </div>

        <div class="topbar-right">
          <span class="dot" :class="connectionState" />
          <span class="muted small">
            {{ connectionState === 'connected' ? '实时已连接' : '实时' + connectionState }}
          </span>

          <!-- 授权徽章：点它直接去授权页（未授权时是唯一能解决问题的地方）。 -->
          <button
            v-if="licenseBadge"
            class="badge as-button"
            :class="licenseBadge.cls"
            :title="license?.info ?? ''"
            @click="activeTab = 'license'"
          >
            {{ licenseBadge.text }}
          </button>

          <span class="muted small">{{ user?.userName }}（{{ roleLabel(user?.role) }}）</span>

          <!--
            客户端安装包下载（M4）。放在右上角是因为现场工程师到一台新机器上，
            第一件要做的事往往就是"把客户端装上" —— 这个入口必须一眼可见，
            而不是藏在某个子页面的深处。
          -->
          <button
            class="sm primary agent-dl"
            :disabled="!agentInstaller?.available"
            :title="agentInstallerHint"
            @click="downloadAgentInstaller"
          >
            客户端安装包
            <span
              v-if="agentInstaller?.available"
              class="agent-dl-mark"
              :class="{ warn: agentInstaller.versionMismatch }"
            />
          </button>

          <button class="sm" @click="passwordDialogOpen = true">修改密码</button>
          <button class="sm" @click="doLogout">登出</button>
        </div>
      </header>

      <!--
        强制改密期间的提示条：此时服务端闸门已生效，除改密/登出/读会话外的 API 都返回 403。
        弹窗本身就是入口，这里只是给出"为什么什么都点不动"的解释。
      -->
      <p v-if="mustChangePassword" class="notice warn" style="margin-bottom: 14px">
        <strong>请先修改密码</strong>
        <span>当前账号仍在使用初始或管理员重置的临时口令，在改密之前其它功能不可用。</span>
        <button class="sm" @click="passwordDialogOpen = true">立即修改</button>
      </p>

      <!--
        授权横幅（M3）：与服务端的"强制改密"闸门是两条**独立**的闸门 ——
        那条是账号级（这个人必须改密），这条是设备级（这台机器没授权）。
      -->
      <p
        v-if="licenseNotice"
        class="notice"
        :class="licenseNotice.kind"
        style="margin-bottom: 14px"
      >
        <strong>{{ licenseNotice.text }}</strong>
        <span>
          {{ licenseBlocked
            ? '动作执行、对接码监听与定时触发已停用；登录、查看与编辑配置不受影响。'
            : '试用到期后会自动转为强制拦截。' }}
        </span>
        <button class="sm" @click="activeTab = 'license'">去授权</button>
      </p>

      <nav class="tabs">
        <button :class="{ active: activeTab === 'console' }" @click="activeTab = 'console'">控制台</button>
        <button :class="{ active: activeTab === 'edit' }" @click="activeTab = 'edit'">编辑</button>
        <button :class="{ active: activeTab === 'timers' }" @click="activeTab = 'timers'">定时</button>
        <button :class="{ active: activeTab === 'commands' }" @click="activeTab = 'commands'">指令库</button>
        <!-- 客户端页对所有角色可见（看板），写操作由服务端按 Admin 拦。 -->
        <button :class="{ active: activeTab === 'agents' }" @click="activeTab = 'agents'">
          客户端
        </button>
        <!--
          文件分发页（M4.4）：读要 CanOperate，上传要 FileIngest（Operator 也没有），
          建分发给 Admin。入口对所有角色可见 —— 只读也要能看到仓库里有什么。

          🔥 页签名从「文件」改成「文件分发」（M7.15）：原名称与**仓库**那一栏同名，
          点进去看到的却是「文件仓库 + 新建分发任务 + 分发任务」三块，
          现场把它当成"管理服务器上的文件"的页面，找不到"把这些文件发下去"的入口。
          页签名要说出**这一页能干什么**，不是"这一页碰到的对象叫什么"。
          ⚠️ 页签 key 仍是 `files`（`tabState.ts` 的白名单与地址栏都用它），改名只动文案。
        -->
        <button :class="{ active: activeTab === 'files' }" @click="activeTab = 'files'">
          文件分发
        </button>
        <!-- 远程桌面（M5.1）：入口只给管理员，服务端同样是 AdminOnly。 -->
        <button v-if="isAdmin" :class="{ active: activeTab === 'remote' }" @click="activeTab = 'remote'">
          远程桌面
        </button>
        <button :class="{ active: activeTab === 'traffic' }" @click="activeTab = 'traffic'">
          收发日志<span class="count">{{ traffic.length }}</span>
        </button>
        <button :class="{ active: activeTab === 'ops' }" @click="activeTab = 'ops'">
          系统日志<span class="count">{{ opsLogs.length }}</span>
        </button>
        <!-- 授权页对所有角色可见：不授权时"为什么按钮点不动"人人都需要知道。 -->
        <button :class="{ active: activeTab === 'license' }" @click="activeTab = 'license'">
          授权
        </button>
        <!-- 用户页只对管理员显示；服务端对整个 /api/users 也是 AdminOnly。 -->
        <button v-if="isAdmin" :class="{ active: activeTab === 'users' }" @click="activeTab = 'users'">
          用户
        </button>
        <button :class="{ active: activeTab === 'settings' }" @click="activeTab = 'settings'">设置</button>
      </nav>

      <p v-if="notice" class="notice" :class="notice.kind" style="margin-bottom: 14px">
        {{ notice.text }}
      </p>

      <!-- ── 控制台 ── -->
      <template v-if="activeTab === 'console'">
        <StatusCards
          :status="status"
          :counts="config?.counts ?? { buttons: 0, subButtonSets: 0, subButtons: 0 }"
          :config-state="config?.state ?? 'Missing'"
          :config-version="config?.version ?? 0"
        />

        <section class="panel">
          <div class="panel-head">
            <h2>
              {{ config?.settings?.appName || '按钮面板' }}
              <span class="muted small">
                （{{ tiles.length }} 个瓦片 · 点击按钮立即发送，点击虚线框的集合打开子按钮）
              </span>
            </h2>

            <div class="panel-actions">
              <button
                class="sm"
                :disabled="runningIds.has(LOOPBACK_ID) || licenseBlocked"
                :title="licenseBlocked ? LICENSE_BLOCKED_HINT : '向本机监听端口发一条报文，验证对接码链路是否通'"
                @click="runLoopback"
              >
                连通性自测
              </button>
              <button class="sm" @click="refreshConfig">刷新配置</button>
            </div>
          </div>

          <ButtonGrid
            :tiles="tiles"
            :free-layout="freeLayout"
            :busy-ids="runningIds"
            :just-ran-ids="justRanIds"
            @run-button="runButton"
            @run-combo="runCombo"
            @open-set="openSet = $event"
          />
        </section>

        <section v-if="traffic.length > 0" class="panel">
          <div class="panel-head">
            <h2>最近收发（最新 {{ Math.min(RECENT_TRAFFIC, traffic.length) }} 条）</h2>
            <button class="sm" @click="activeTab = 'traffic'">查看全部</button>
          </div>

          <div class="mini">
            <div v-for="(row, i) in traffic.slice(-RECENT_TRAFFIC).reverse()" :key="i" class="mini-row">
              <span class="mono muted">{{ fmtTimeOnly(row.time) }}</span>
              <span class="badge" :class="row.direction === '发送' ? 'send' : 'recv'">{{ row.direction }}</span>
              <span class="mono">{{ row.protocol }}</span>
              <span class="mono muted">{{ row.targetIp }}:{{ row.port }}</span>
              <span class="mini-data" :class="{ err: !row.success }">{{ row.data || row.message }}</span>
            </div>
          </div>
        </section>

        <!-- 执行结果：放在「最近收发」下方（点按钮执行后往下滚即可看到本次明细）。 -->
        <ActionResultPanel
          :result="lastResult"
          :error="lastError"
          :inflight="runningCount"
          @close="clearResult"
        />
      </template>

      <!-- ── 收发日志 ── -->
      <TrafficPanel
        v-else-if="activeTab === 'traffic'"
        :items="traffic"
        :loading="loadingTraffic"
        :capacity="trafficCapacity"
        @refresh="refreshTraffic"
        @clear="doClearTraffic"
      />

      <!-- ── 系统日志 ── -->
      <OpsLogPanel
        v-else-if="activeTab === 'ops'"
        :items="opsLogs"
        :loading="loadingLogs"
        @refresh="refreshLogs"
      />

      <!-- ── 编辑 ── -->
      <EditPanel
        v-else-if="activeTab === 'edit'"
        :config="config"
        :user="user"
        @refresh-config="refreshConfig"
        @notice="showNotice"
      />

      <!-- ── 定时（M2-G6）── -->
      <TimerOverviewPanel
        v-else-if="activeTab === 'timers'"
        :user="user"
        @notice="showNotice"
      />

      <!-- ── 指令库（M2-G6）── -->
      <CommandLibraryPanel
        v-else-if="activeTab === 'commands'"
        :user="user"
        :config="config"
        @notice="showNotice"
      />

      <!-- ── 客户端主机（M4）── -->
      <AgentsPanel
        v-else-if="activeTab === 'agents'"
        ref="agentsPanel"
        :user="user"
        @notice="showNotice"
      />

      <!-- ── 文件仓库与分发（M4.4）── -->
      <FilesPanel
        v-else-if="activeTab === 'files'"
        ref="filesPanel"
        :user="user"
        @notice="showNotice"
      />

      <!--
        ── 远程桌面（M5.1）──
        ⚠️ 只对管理员显示入口：服务端对整个 `/api/remote` 都是 AdminOnly
        （决策 11 —— 非管理员连"只读观看"都不允许，价值点是"看到别人屏幕"本身）。
        这里隐藏按钮只是"别让人点开一堆 403"，真正的闸门在服务端。
        💡 本组件**自己持有** `/hubs/remote` 连接，并随挂载/卸载起停 ——
           切走页签就断开，不会在后台白收一个画面流。
      -->
      <RemoteDesktopPanel
        v-else-if="activeTab === 'remote' && isAdmin"
        :user="user"
        @notice="showNotice"
      />

      <!-- ── 授权（M3，所有角色可见，写操作仅管理员）── -->
      <LicensePanel
        v-else-if="activeTab === 'license'"
        :user="user"
        :license="license"
        @notice="showNotice"
        @changed="onLicenseChanged"
      />

      <!-- ── 用户（M3，仅管理员）── -->
      <UserManagementPanel
        v-else-if="activeTab === 'users' && isAdmin"
        :user="user"
        @notice="showNotice"
      />

      <!-- ── 设置 ── -->
      <SettingsPanel
        v-else
        :config="config"
        :user="user"
        @refresh-config="refreshConfig"
        @address-switched="onAddressSwitched"
      />

      <!-- 子按钮集弹层 -->
      <SubButtonSheet
        v-if="openSet"
        :set="openSet"
        :busy-ids="runningIds"
        :just-ran-ids="justRanIds"
        @close="openSet = null"
        @run-sub="runSub"
      />
    </div>

    <!--
      改密弹窗放在 `v-else` 外层：登出瞬间 `user` 会变成未登录态，
      若它挂在"已登录"分支里就会被连带销毁，正在提交的表单状态会丢。
      强制态由 `mustChangePassword` 决定（关不掉），主动态可随时关闭。
    -->
    <ChangePasswordDialog
      :open="loggedIn && (passwordDialogOpen || mustChangePassword)"
      :forced="mustChangePassword"
      :user-name="user?.userName ?? ''"
      @close="passwordDialogOpen = false"
      @changed="onPasswordChanged"
    />
  </div>
</template>

<style scoped>
.wrap {
  max-width: 1180px;
  margin: 0 auto;
  padding: 22px 20px 60px;
}

.login-card {
  max-width: 360px;
  margin: 12vh auto 0;
  background: var(--panel);
  border: 1px solid var(--border);
  border-radius: 12px;
  box-shadow: var(--shadow);
  padding: 28px 26px;
}

.login-card h1 {
  font-size: 20px;
  margin-bottom: 2px;
}

.login-card > p:first-of-type {
  margin: 0 0 18px;
}

/*
  「记住账号密码」：全局 `label` 是 display:block + 上边距 10px，改成 flex 行，
  让复选框与文字横向排列、垂直居中。
*/
.login-card label.inline {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 14px 0 0;
  cursor: pointer;
  white-space: nowrap;
}

/*
  ⚠️ 这里必须逐项复位：全局 `input { width: 100%; padding: 6px 10px; ... }`
  **同样命中复选框**。在 flex 行里 `flex-basis` 默认取 `width`，于是复选框会吃掉整行、
  把文字挤成"一字一行"的竖排。复位尺寸 + padding 后，复选框回归原生大小，
  文字才能横向排开（改这段前先看一眼这条注释，别再把 width 删掉）。
*/
.login-card label.inline input[type='checkbox'] {
  width: 14px;
  height: 14px;
  padding: 0;
  margin: 0;
  border: none;
  background: none;
  flex: 0 0 auto;
  accent-color: var(--accent);
}

.topbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
  margin-bottom: 14px;
}

.brand {
  display: flex;
  align-items: baseline;
  gap: 7px;
  font-size: 16px;
}

.topbar-right {
  display: flex;
  align-items: center;
  gap: 12px;
}

.dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--muted);
}

.dot.connected {
  background: var(--ok);
}

.dot.connecting {
  background: var(--warn);
}

/*
  授权徽章是个按钮（点它去授权页），但要长得像徽章。
  `.badge` 的类选择器权重高于全局 `button` 的标签选择器，所以内边距/边框/配色
  都按徽章来；这里只补上"它可点"这件事。
*/
.badge.as-button {
  font-size: 11px;
  cursor: pointer;
}

/*
  顶栏「客户端安装包」按钮右侧的小圆点。
  存在的理由：安装包版本与服务端不一致时只在 title 里说明，而 title 要悬停才看得到 ——
  本可以什么都不显示，但那样"版本可能对不上"这件事就永远不会被发现。
*/
.agent-dl-mark {
  display: inline-block;
  width: 6px;
  height: 6px;
  margin-left: 6px;
  border-radius: 50%;
  background: #a7f3d0;
  vertical-align: 1px;
}

.agent-dl-mark.warn {
  background: #fde68a;
}

.mini {
  display: flex;
  flex-direction: column;
  font-size: 12px;

  /* 最多 20 条，不设限会把控制台拉得很长；固定高度 + 滚动条即可。 */
  max-height: 460px;
  overflow-y: auto;
}

.mini-row {
  display: grid;
  grid-template-columns: 66px 44px 46px 158px 1fr;
  gap: 8px;
  align-items: center;
  padding: 3px 0;
  border-bottom: 1px solid #f1f2f4;
}

.mini-row:last-child {
  border-bottom: none;
}

.mini-data {
  word-break: break-all;
  font-family: var(--mono);
}
</style>
