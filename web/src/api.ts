import type {
  ActionExecutionDto,
  AgentBrowseResultDto,
  AgentConfigDto,
  AgentConfigSaveRequest,
  AgentConfigSaveResult,
  AgentDetailDto,
  AgentInstallerInfoDto,
  AgentInventoryEnvelopeDto,
  AgentListDto,
  AgentMetricsSeriesDto,
  AgentMutationResult,
  AgentPowerEventListDto,
  AgentReceivedDataDto,
  ComboExecutionDto,
  CommandItem,
  CommandListDto,
  CommandMutationDto,
  ConfigExportResult,
  ConfigImportResult,
  ConfigView,
  ControlSettings,
  CurrentUser,
  DistributionListDto,
  DistributionTaskDto,
  EncodePreviewDto,
  FileRepoListDto,
  FileUploadResultDto,
  FileUploadSessionDto,
  LicenseDto,
  LicenseMutationResult,
  LicenseRequestDto,
  LogDto,
  LoginResponse,
  RoleOption,
  ServerAddressChangeResultDto,
  ServerAddressDto,
  StatusSnapshot,
  SystemInfoDto,
  TimeSyncResultDto,
  TimerFireResultDto,
  TimerOverviewDto,
  TrafficLogDto,
  UserDto,
  UserMutationResult,
  RemoteCapabilityDto,
  RemoteClipboardDto,
  RemoteInputAckDto,
  RemoteInputEventDto,
  RemoteScreensDto,
  RemoteSessionDto,
  RemoteViewerVideoCapabilities,
} from './types'

/** 带状态码与响应体的 API 错误 —— 让调用方能区分 409（乐观锁冲突）这类可恢复错误。 */
export class ApiError extends Error {
  readonly status: number
  readonly body: unknown

  constructor(status: number, message: string, body: unknown) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.body = body
  }
}

/** 乐观锁冲突时的响应体（PUT /api/config、POST /api/config/import 在版本不符时返回）。 */
export interface ConfigConflictBody {
  message: string
  conflict: true
  expectedVersion: number
  actualVersion: number
  current: ConfigView
}

const jsonHeaders = { 'Content-Type': 'application/json' }

async function unwrap<T>(response: Response): Promise<T> {
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`
    let body: unknown = null

    try {
      body = await response.json()
      const anyBody = body as Record<string, unknown> | null
      // ASP.NET Core 的 ProblemDetails 用 title/detail；我们自己的接口用 message。
      message = String(anyBody?.message ?? anyBody?.title ?? anyBody?.detail ?? message)
    } catch {
      /* 非 JSON 错误体，保留状态码文案 */
    }

    throw new ApiError(response.status, message, body)
  }

  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

export const api = {
  // ── 会话 ──
  me: () => fetch('/api/auth/me').then(unwrap<CurrentUser>),

  login: (userName: string, password: string) =>
    fetch('/api/auth/login', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ userName, password }),
    }).then(unwrap<LoginResponse>),

  logout: () => fetch('/api/auth/logout', { method: 'POST' }).then(unwrap<void>),

  // ── 状态 / 日志 ──
  status: () => fetch('/api/status').then(unwrap<StatusSnapshot>),

  logs: (count = 200) => fetch(`/api/logs/recent?count=${count}`).then(unwrap<LogDto[]>),

  traffic: (count = 200) => fetch(`/api/traffic/recent?count=${count}`).then(unwrap<TrafficLogDto[]>),

  trafficCount: () =>
    fetch('/api/traffic/count').then(unwrap<{ count: number; capacity: number }>),

  clearTraffic: () => fetch('/api/traffic', { method: 'DELETE' }).then(unwrap<{ count: number }>),

  // ── 配置 ──
  config: () => fetch('/api/config').then(unwrap<ConfigView>),

  saveConfig: (settings: ControlSettings, expectedVersion: number | null, updatedBy?: string) =>
    fetch('/api/config', {
      method: 'PUT',
      headers: jsonHeaders,
      body: JSON.stringify({ settings, expectedVersion, updatedBy }),
    }).then(unwrap<ConfigView>),

  resetConfig: (expectedVersion: number | null, updatedBy?: string) =>
    fetch('/api/config/reset', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ expectedVersion, updatedBy }),
    }).then(unwrap<ConfigView>),

  exportConfig: (format: 'base64' | 'json' = 'base64') =>
    fetch(`/api/config/export?format=${format}`).then(unwrap<ConfigExportResult>),

  importConfig: (content: string, expectedVersion: number | null, updatedBy?: string) =>
    fetch('/api/config/import', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ content, expectedVersion, updatedBy }),
    }).then(unwrap<ConfigImportResult>),

  // ── 图片（M2-G1）──
  /*
   * ⚠️ 这里的 `uploadImage` / `deleteImage` 已随 `ImagePicker.vue` 一并撤掉
   * （2026-10-15 设置页精简：前端再没有上传入口），**服务端端点仍在**
   * （`POST` / `DELETE /api/config/images`）。将来要重新加回上传时，记住这条契约：
   * 写进配置的必须是返回的 `path`（服务端绝对路径），**不是 `fileName`** ——
   * 存裸文件名会被导出逻辑按 `File.Exists(字段值)` 判成"文件不存在"，那张图**不会被导出**，
   * 表现为"导出再导入后按钮图没了"，且全程没有任何报错。
   */

  // ── 动作执行 ──
  /**
   * 执行一组动作。三种用法：
   * `{ buttonId }` / `{ subButtonId }`（按已保存配置执行）或 `{ actions: [...] }`（临时测试）。
   */
  executeActions: (payload: {
    buttonId?: string
    subButtonId?: string
    label?: string
    overrideIp?: string
    overridePort?: number
    actions?: Array<Record<string, unknown>>
  }) =>
    fetch('/api/actions/execute', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify(payload),
    }).then(unwrap<ActionExecutionDto>),

  /**
   * **触发一个组合按钮**（2026-10-15，需求⑤）：服务端按成员顺序逐段执行、段间按配置等待。
   *
   * ⚠️ 返回的是**分段**结果（`ComboExecutionDto`），不是 `ActionExecutionDto` ——
   * 组合是"一串按钮，每个按钮各自一串动作"，拍平了就看不出"哪条属于哪个按钮"。
   * 要喂给结果面板的话，先过一遍 `flattenComboExecution`。
   */
  executeCombo: (comboId: string) =>
    fetch('/api/actions/execute', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ comboId }),
    }).then(unwrap<ComboExecutionDto>),

  previewEncode: (dataType: string, dataContent: string) =>
    fetch('/api/actions/preview', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ dataType, dataContent }),
    }).then(unwrap<EncodePreviewDto>),

  // ── 自环 / 系统 ──
  loopback: (payload: string) =>
    fetch(`/api/test/loopback?payload=${encodeURIComponent(payload)}`, { method: 'POST' }).then(
      unwrap<{ runId: string; ok: boolean; message: string; targetPort: number; bytesSent: number }>,
    ),

  sendUdp: (host: string, port: number, payload: string) =>
    fetch('/api/test/udp', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ host, port, payload }),
    }).then(unwrap<{ runId: string; ok: boolean; message: string; bytesSent: number; target: string }>),

  machineCode: () =>
    fetch('/api/system/machine-code').then(
      unwrap<{ machineCode: string; fingerprint: string; osDescription: string }>,
    ),

  /**
   * 运维诊断详情（`GET /api/system/info`）。
   *
   * 与 `status()` 的分工：后者是首页的轻量运行状态；这里叠加了**时间同步**与**定时计数**，
   * 是排查问题时才看的那一层。机器名 / 端口 / 运行时长直接复用 `StatusProvider`，
   * 所以两处不会出现「首页说 8899、诊断页说 9000」的不一致。
   *
   * 需要 `CanOperate` 权限（不含机器码，那个是 AdminOnly）。
   */
  systemInfo: () => fetch('/api/system/info').then(unwrap<SystemInfoDto>),

  /**
   * 立刻做一次 SNTP 校准。
   *
   * ⚠️ 成功与失败**都返回 200**，要读响应体的 `ok` 判断结果（`unwrap` 不会抛）。
   * 校准会**立刻改变全应用的时间口径** —— 日志时间戳、定时器判定都会跟着走，
   * 所以这是运维操作：自动重校由 `Server:Time:AutoSync` 负责，不靠手动点。
   */
  timeSync: () => fetch('/api/system/timesync', { method: 'POST' }).then(unwrap<TimeSyncResultDto>),

  // ── 监听地址（M2-E5）──
  /** 读**实际**监听地址（不是配置值）。 */
  address: () => fetch('/api/system/address').then(unwrap<ServerAddressDto>),

  /**
   * 热切换 HTTP 监听端口 —— 不重启进程，**当场返回真实结果**。
   *
   * <p>
   * 失败时服务端会返回 409（端口被占用 / 宿主不支持）或 400（地址非法），
   * 此时 `unwrap` 会抛异常、**旧监听不受任何影响**。
   * 成功之后当前页面所在的原点即失效，调用方必须自行跳到新地址。
   * </p>
   */
  /**
   * 热切换监听地址。`urls` 与 `port` 二选一。
   *
   * 注意 `urls` **一次只能给一个地址**：热切换只作用于主监听地址，
   * 多给会被服务端以 400 / `Invalid` 拒绝。
   */
  changeAddress: (payload: {
    urls?: string[] | null
    port?: number | null
  }) =>
    fetch('/api/system/address', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify(payload),
    }).then(unwrap<ServerAddressChangeResultDto>),

  // ── 指令库（M2-E3）──
  /**
   * 读取全部指令。
   *
   * ⚠️ 指令库是**独立文档**（`commands` 键），**不参与配置导入导出** ——
   * 与旧版行为一致。导出配置时不会带上指令，导入也不会覆盖指令库。
   */
  commands: () => fetch('/api/commands').then(unwrap<CommandListDto>),

  /**
   * 新增指令。
   *
   * 失败时**不会抛异常**：服务端把所有失败（重名 / 越界 / 找不到）都做成了带
   * `ok: false` 的正常响应，因为响应体里还带着**服务端最新全量列表**，
   * 调用方直接用它刷新界面即可。真正抛异常的只有 5xx 与网络错误。
   */
  createCommand: (command: CommandItem, updatedBy?: string) =>
    fetch('/api/commands', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ command, updatedBy }),
    }).then(unwrap<CommandMutationDto>),

  /** 修改指令（按 Id 定位，Id 以路由为准 —— body 里的 Id 写错也不会"改一条留一条"）。 */
  updateCommand: (id: string, command: CommandItem, updatedBy?: string) =>
    fetch(`/api/commands/${encodeURIComponent(id)}`, {
      method: 'PUT',
      headers: jsonHeaders,
      body: JSON.stringify({ command, updatedBy }),
    }).then(unwrap<CommandMutationDto>),

  deleteCommand: (id: string, updatedBy?: string) =>
    fetch(`/api/commands/${encodeURIComponent(id)}?updatedBy=${encodeURIComponent(updatedBy ?? 'web')}`, {
      method: 'DELETE',
    }).then(unwrap<CommandMutationDto>),

  // ── 定时调度（M2-E4）──
  /** 当前生效的全部触发点 + 调度器概况（含"今天跑了没有"）。 */
  timers: () => fetch('/api/timers').then(unwrap<TimerOverviewDto>),

  /**
   * 立刻触发一次指定触发点（调试 / 运维）。
   *
   * ⚠️ 它**不绕开幂等闸门**：与定时触发共用 `timer_fires` 表与 `(refId, 日期, 时刻)`
   * 唯一约束，因此同一秒内连点只有第一次生效，第二次返回 **409**。
   * 这是有意的 —— 能绕过幂等键的"调试通道"就是生产事故的入口。
   *
   * `refId` 含 `:`（月度计划是 `<buttonId>:<planId>`）时必须编码。
   */
  fireTimer: (refId: string) =>
    fetch(`/api/timers/${encodeURIComponent(refId)}/fire`, { method: 'POST' }).then(
      unwrap<TimerFireResultDto>,
    ),

  // ── 用户账号（M3 RBAC）──
  /**
   * 全部账号（**仅 Admin**）。
   *
   * 整个 `/api/users` 控制器都是 `AdminOnly` —— 账号管理是"谁能进来"这件事本身，
   * 交给 Operator 等于让他给自己提权。所以其它角色调用会得到 **403**，
   * 前端应当按角色直接隐藏入口（这里的 403 是最后一道防线，不是主流程）。
   */
  users: () => fetch('/api/users').then(unwrap<UserDto[]>),

  /** 可选角色清单（含服务端给的中文显示名，避免前端硬编码漂移）。 */
  roles: () => fetch('/api/users/roles').then(unwrap<RoleOption[]>),

  /**
   * 新建账号。
   *
   * ⚠️ 失败**不抛异常**：服务端把重名 / 角色非法 / 密码过弱都做成了带 `ok: false`
   * 的正常 400 响应，且响应体里带着**服务端最新全量列表**，直接用它刷新界面即可。
   * 真正抛异常的只有 5xx 与网络错误。（与 `createCommand` 同一套约定。）
   */
  createUser: (userName: string, password: string, role: string) =>
    fetch('/api/users', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ userName, password, role }),
    }).then(unwrap<UserMutationResult>),

  /**
   * 修改账号。
   *
   * 三个字段都是**可选**，`null`/`undefined` = 不动该字段（服务端是局部更新语义）。
   * 不做整对象提交：整对象会强迫"只改角色"这种操作回传前端的陈旧快照，
   * 两个人先后提交就会互相覆盖对方刚改的字段。
   */
  updateUser: (
    id: number,
    patch: { userName?: string | null; role?: string | null; isEnabled?: boolean | null },
  ) =>
    fetch(`/api/users/${id}`, {
      method: 'PUT',
      headers: jsonHeaders,
      body: JSON.stringify({
        userName: patch.userName ?? null,
        role: patch.role ?? null,
        isEnabled: patch.isEnabled ?? null,
      }),
    }).then(unwrap<UserMutationResult>),

  /**
   * 管理员重置他人密码。重置后该账号的 `MustChangePassword` 置回 `true`
   * —— 管理员定的密码是"临时口令"，本人必须再改一次。
   */
  resetUserPassword: (id: number, newPassword: string) =>
    fetch(`/api/users/${id}/password`, {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ newPassword }),
    }).then(unwrap<UserMutationResult>),

  /** 删除账号。删自己 / 删最后一个启用管理员 → **409**（`ApiError.status === 409`）。 */
  deleteUser: (id: number) =>
    fetch(`/api/users/${id}`, { method: 'DELETE' }).then(unwrap<UserMutationResult>),

  /**
   * 修改**自己**的密码（任何已登录角色都可用）。
   *
   * 必须提供当前密码：仅凭已登录的会话来改密，等于"会话即口令"。
   * 改成功后服务端会**立刻重签会话**，下次 `me()` 拿到的 `mustChangePassword` 已是 false。
   *
   * 失败抛异常（400，`message` 是原因）—— 这里是表单流程，需要区分"当前密码错"
   * 与"新密码不合规"，不适合 `ok: false` 那套。
   */
  changePassword: (currentPassword: string, newPassword: string) =>
    fetch('/api/auth/change-password', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ currentPassword, newPassword }),
    }).then(unwrap<{ message: string }>),

  // ── 设备授权（M3）──
  /**
   * 当前设备授权状态。
   *
   * **所有已登录角色**都能读 —— 授权状态必须人人可见，否则现场只看到
   * "按钮点了没反应"，不知道是因为没授权，只能来问我们。
   */
  license: () => fetch('/api/license').then(unwrap<LicenseDto>),

  /**
   * 生成"授权申请文本"（机器码 + 计算机名 + 申请时间）。
   *
   * 需要管理员：申请文本里含机器码，而机器码是"给谁签发"的依据。
   */
  licenseRequest: () => fetch('/api/license/request').then(unwrap<LicenseRequestDto>),

  /**
   * 导入授权文件。
   *
   * ⚠️ 失败**不抛异常**（除 503 无网络时间外）—— 服务端把验签失败 / 机器码不符 /
   * 已过期等做成带 `ok: false` 的正常 400/409 响应，且体里带着**服务端当前全量状态**，
   * 直接用它刷新界面即可。真正抛异常的只有 503（拿不到网络时间）与网络错误。
   *
   * `licenseText` 是 `.lic` 文件里的整段 Base64 文本（换行与空格会被忽略）。
   */
  importLicense: (licenseText: string) =>
    fetch('/api/license/import', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ licenseText }),
    }).then(unwrap<LicenseMutationResult>),

  /** 生成本机试用（仅管理员；仅在既无授权、也无有效试用时允许）。 */
  generateTrial: () =>
    fetch('/api/license/trial', { method: 'POST' }).then(unwrap<LicenseMutationResult>),

  /**
   * 移除授权文件。
   *
   * ⚠️ 它**不重置本机试用记录**：否则"移除授权 → 重启 → 又白拿 30 天"。
   * 若本机试用已过期，移除后依然是未授权状态。
   */
  removeLicense: () => fetch('/api/license', { method: 'DELETE' }).then(unwrap<LicenseMutationResult>),

  // ── 客户端主机（M4）──
  /**
   * 在线表（含在线 / 不稳定 / 离线计数）。
   *
   * 读的是服务端**内存快照**，所以可能落后最多 60 秒 —— 对"看板"完全够用，
   * 换来的是"20 台机器每 5 秒刷一次"不会把 SQLite 读热。
   *
   * 需要 `CanOperate`（运维日常要看），写操作才要 Admin。
   */
  agents: () => fetch('/api/agents').then(unwrap<AgentListDto>),

  /** 单台详情（完整主机信息 + 此刻是否握有长连接）。 */
  agentDetail: (id: number) => fetch(`/api/agents/${id}`).then(unwrap<AgentDetailDto>),

  /**
   * 改名 / 分组 / 备注（仅 Admin）。
   *
   * 三个字段都可空，`null` = 不动该字段（服务端是局部更新语义）——
   * 与 `updateUser` 同一套约定，避免"只改备注"把前端陈旧快照整份回传覆盖别处。
   *
   * ⚠️ 失败**不抛异常**（除 404）：服务端把失败做成带 `ok: false` 的正常响应，
   * 且体里带着最新全量列表。
   */
  updateAgent: (id: number, patch: { displayName?: string | null; group?: string | null; note?: string | null }) =>
    fetch(`/api/agents/${id}`, {
      method: 'PUT',
      headers: jsonHeaders,
      body: JSON.stringify({
        displayName: patch.displayName?.trim() || null,
        group: patch.group?.trim() || null,
        note: patch.note?.trim() || null,
      }),
    }).then(unwrap<AgentMutationResult>),

  /**
   * 吊销该机的 agentKey（仅 Admin）。
   *
   * 客户机上的服务**不会被卸载**，只是再也连不上来；服务端会顺手向那条长连接推
   * `Revoked`，让客户端自己停下来并留下本地痕迹。
   */
  blockAgent: (id: number) =>
    fetch(`/api/agents/${id}/block`, { method: 'POST' }).then(unwrap<AgentMutationResult>),

  /** 解除吊销（客户端需要重新注册才会再连上）。 */
  unblockAgent: (id: number) =>
    fetch(`/api/agents/${id}/unblock`, { method: 'POST' }).then(unwrap<AgentMutationResult>),

  /**
   * 移除记录（仅 Admin）。
   *
   * ⚠️ **不等于卸载那台机器上的 Agent**：它发现自己连不上后会重新注册、再次出现。
   * 要真正下线，得去那台机器上卸载。
   */
  deleteAgent: (id: number) =>
    fetch(`/api/agents/${id}`, { method: 'DELETE' }).then(unwrap<AgentMutationResult>),

  /*
   * ⚠️ 2026-10-15（需求③）：这里原本有 `rebootAgent`（远程重启那台机器上的 **Agent 服务**），
   *    已随"客户端主机列表取消「重启服务」操作"一并删除 —— 页面上不再有它的入口。
   *    服务端的 `POST /api/agents/{id}/reboot` **仍然保留**（它是有用的排障手段：
   *    "Agent 进程卡住了"时重启进程，而不是重启整机），由集成测试与冒烟覆盖；
   *    需要时用 curl 或脚本调它，不要为了"某个按钮"把它删掉。
   */

  /**
   * **关机 / 重启整机**（仅 Admin）。
   *
   * 两条下发通道，服务端自己挑：
   *   · 机器挂着长连接 ⇒ 走 SignalR 直推（唯一能确认"对方收到了"的通道）；
   *   · 长连接不在 ⇒ **改走 UDP 指令通道尽力发出**（2026-10-15 需求②），
   *     这时返回 `ok:true` 但 `status:"udp"`，`message` 里明写"该客户端当前离线"。
   *
   * ⚠️ 所以 **`ok:true` 不等于"机器马上会关"**：UDP 没有应答，是否真的执行
   *    要看那台机器上的日志（Udp 分类）。message 才是全部结论，原样显示。
   * ⚠️ 回 **409** 的情形：它已经离线**且**连 UDP 也发不出去（不知道地址 / 没有可用的端口 /
   *    这条指令不在 UDP 支持之列），或者它已被**拉黑**（`status:"blocked"`）。
   *    要开机的是**关着**的机器 —— 那是 `wakeAgent`。
   */
  powerAgent: (id: number, action: 'shutdown' | 'restart') =>
    fetch(`/api/agents/${id}/power`, {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ action }),
    }).then(unwrap<AgentMutationResult>),

  /**
   * **网络唤醒**（WoL，仅 Admin，2026-10-15）。
   *
   * 服务端直接向该机上报过的**每一块网卡 MAC** 各发一遍魔术包，并同时发往
   * 「该机最后来源 IP 的定向广播地址」与 `255.255.255.255` —— 多网卡机器、
   * 换过网段的机器都尽量覆盖到。
   *
   * ⚠️ **不要求那台机器在线**（它正是关着的），但也因此**没有应答**：
   *    返回 `ok` 只说明"包发出去了"，机器到底起没起只能看现场。
   * 返回 **409** 的两种情形：这台机器从没上报过网卡（没有 MAC 可发）／没有可用的广播地址。
   */
  wakeAgent: (id: number) =>
    fetch(`/api/agents/${id}/wake`, { method: 'POST' }).then(unwrap<AgentMutationResult>),

  // ── 客户机配置（M4.5，事实来源在服务端）──
  /**
   * 读该机的配置（三张任务清单 + UDP 指令通道）。
   *
   * ⚠️ 这个端点是**双身份**的：人（管理员/操作员，页面用）与**这台机器自己**
   * （AgentKey，客户端启动/收到变更通知后拉配置用）。前端走的是人的那条路，
   * 需要 `CanOperate`。
   */
  agentConfig: (id: number) => fetch(`/api/agents/${id}/config`).then(unwrap<AgentConfigDto>),

  /**
   * 保存该机的配置（仅 Admin）：整包替换内容 → 版本 +1 → **立刻通知客户机来拉**。
   *
   * ⚠️ 校验不过时返回 **400**，`unwrap` 会抛 `ApiError`，而它的 `message`
   * 就是服务端把**全部问题**用「；」连起来的那句话 —— 直接显示即可，别自己再拼一遍。
   */
  saveAgentConfig: (id: number, patch: AgentConfigSaveRequest) =>
    fetch(`/api/agents/${id}/config`, {
      method: 'PUT',
      headers: jsonHeaders,
      body: JSON.stringify(patch),
    }).then(unwrap<AgentConfigSaveResult>),

  /**
   * 要求该机立刻重新拉配置（仅 Admin，**不依赖版本判断**）。
   *
   * 用于"机器看起来没按配置动"的现场排障：常规路径是版本变更推送，这条是手动再推一次。
   * 离线时返回 **409**（请求合法，只是现在做不到）。
   */
  redeliverAgentConfig: (id: number) =>
    fetch(`/api/agents/${id}/config/redeliver`, { method: 'POST' }).then(
      unwrap<AgentMutationResult>,
    ),

  /**
   * **经 UDP 指令通道**下发一条指令（仅 Admin，M4.6）。
   *
   * 与走 SignalR 的 `rebootAgent` 不同：这条由服务端**直接向客户机的 UDP 通道端口**
   * 发一条已签名报文，**不要求机器在线**（只要网通、且那台机器开着通道）。
   *
   * ⚠️ UDP 无应答：返回 `ok` 只代表"报文已发出"，**不代表已执行** ——
   * 真正是否生效要去那台机器的日志里看。
   *
   * 三种失败都是 **409**（请求合法但此刻做不到），`message` 里写清了原因：
   * 通道没启用（`channeloff`）/ 服务端还没拿到通道密钥（`nokey`）/ 还不知道地址（`noaddress`）。
   */
  sendAgentUdpCommand: (id: number, command: string) =>
    fetch(`/api/agents/${id}/udp-command`, {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ command }),
    }).then(unwrap<AgentMutationResult>),

  // ── 客户端安装包下载（M4）──
  /**
   * 安装包元信息（顶栏按钮据此决定"能不能点 / 要不要给黄点"）。
   *
   * 服务端在数据目录的 `downloads\` 下找 `ControlServer-Agent-Setup-*`，
   * 找不到就返回 `available: false` + `hint`（"该把文件放哪"）。
   */
  agentInstallerInfo: () =>
    fetch('/api/downloads/agent-installer/info').then(unwrap<AgentInstallerInfoDto>),

  /**
   * 安装包直链（**不是** fetch，直接交给浏览器 / `<a download>`）。
   *
   * 走直链而不是先 fetch 成 blob：安装包几十 MB，而且服务端开了 Range 支持
   * （断点续传 / 下载器接管）。用 URL 让浏览器自己下，既不占内存也不会假死。
   */
  agentInstallerUrl: () => '/api/downloads/agent-installer',

  // ── 客户端遥测（M4.2）──
  /**
   * 一段时序。
   *
   * 不传 `from`/`to` 时服务端默认给最近 6 小时。
   *
   * ⚠️ 服务端会**按窗口长度自动选数据源**：
   *   窗口短 → 5 秒明细（可降采样）；窗口很长 → 老的一段用日聚合。
   *   返回体里的 `source` 必须显示在图上，否则"两条相邻折线隔了一天"会被读成"一直很稳"。
   *
   * **不要**在这里传一个"十年"的窗口靠服务端截断：每次请求都会真的扫库，
   * 而用户只是把日期滑块拖过头了。
   */
  agentMetrics: (id: number, range?: { from?: string; to?: string; step?: number; points?: number }) => {
    const params = new URLSearchParams()
    if (range?.from) params.set('from', range.from)
    if (range?.to) params.set('to', range.to)
    if (range?.step) params.set('step', String(range.step))
    if (range?.points) params.set('points', String(range.points))
    const query = params.toString()
    return fetch(`/api/agents/${id}/metrics${query ? `?${query}` : ''}`).then(
      unwrap<AgentMetricsSeriesDto>,
    )
  },

  /**
   * 开关机 / 异常记录（最近在前）+ **全量历史**统计。
   *
   * 统计不随分页变化（服务端按全部历史算）—— 否则翻到第 2 页
   * "意外关机次数"就变了，那比不显示还糟。
   */
  agentPowerEvents: (id: number, options?: { from?: string; limit?: number }) => {
    const params = new URLSearchParams()
    if (options?.from) params.set('from', options.from)
    if (options?.limit) params.set('limit', String(options.limit))
    const query = params.toString()
    return fetch(`/api/agents/${id}/power-events${query ? `?${query}` : ''}`).then(
      unwrap<AgentPowerEventListDto>,
    )
  },

  /**
   * 最新一份硬件清单。
   *
   * ⚠️ `capturedAt` 为 null = **从未上报过**（与"上报过但 JSON 废了"不同：
   * 那种情况 `capturedAt` 有值、`inventory` 为 null）。
   */
  agentInventory: (id: number) =>
    fetch(`/api/agents/${id}/inventory`).then(unwrap<AgentInventoryEnvelopeDto>),

  /**
   * 列出某台客户机上的一个目录（选"文件存放路径"与"回传源路径"用，**仅 Admin**）。
   *
   * `path` 留空 = 列出那台机器的**全部盘符**（"此电脑"那一层）。
   *
   * ⚠️ **失败不会抛**：服务端把"机器离线 / 路径不存在 / 客户端太旧"这类预期内的结果
   * 也用 200 回，载荷里 `ok:false` + `status` + `message`。所以调用方要**先看 `ok`**
   * 再决定渲染成"列表"还是"一句就地提示" —— 抛异常会在下拉框旁边弹出
   * "系统出错"，而用户只是点进了一个空的目录。
   *
   * 这条链路是 网页 → 服务端 →（长连接）→ 客户机，**一次真实的往返**：
   * 客户机离线或忙碌时会等到超时（服务端 8 秒），所以要有 loading 态。
   */
  browseAgentDirectory: (id: number, path?: string | null) =>
    fetch(
      `/api/agents/${id}/browse${path ? `?path=${encodeURIComponent(path)}` : ''}`,
    ).then(unwrap<AgentBrowseResultDto>),

  /**
   * 把浏览时挑中的目录**记成这台客户机的默认目录**（2026-10-14，仅 Admin）。
   *
   * 与"建单成功才回写"那条路的区别：这是**显式**的一次保存 ——
   * 挑对了目录但这次先不建单，也留得下来，下次打开路径框自动填上。
   *
   * ⚠️ 两个字段都是可选，**传 undefined / 不传 = 不动那一项**：
   * 下发方向只该动 `targetDir`、回传方向只该动 `sourceDir`，
   * 一次只挑了一边不该把另一边顺手清掉。
   */
  rememberAgentPath: (id: number, memory: { targetDir?: string | null; sourceDir?: string | null }) =>
    fetch(`/api/agents/${id}/path-memory`, {
      method: 'PUT',
      headers: jsonHeaders,
      // ⚠️ 用 `||` 而不是 `??`：**空串与没传都表示"不动这一项"**。
      //    空串在别处是有含义的（"就用那台机器的默认目录"），但那是**运行时**的含义；
      //    作为一个"要被预填回去的默认值"，空串等于什么都没记住 —— 所以这里归到 null。
      body: JSON.stringify({
        targetDir: memory.targetDir || null,
        sourceDir: memory.sourceDir || null,
      }),
    }).then(unwrap<AgentMutationResult>),

  /**
   * 要一份某台客户机的「接收到的数据」快照（**仅 Admin**）。
   *
   * 它是"下发了指令但机器毫无反应"的唯一现场证据：中心这一侧只知道
   * "我发出去了"，而"到底收没收到、当时判成了什么"只有客户机知道。
   *
   * ⚠️ 与目录浏览同一条约定：**失败不会抛** —— 服务端把"机器离线 / 客户端太旧"
   * 这类预期内的结果也用 200 回，载荷里 `ok:false` + `status` + `message`。
   * 调用方要**先看 `ok`**，否则会把"这台机器不在线"渲染成"系统出错了"。
   *
   * 链路同样是一次真实往返（网页 → 服务端 → 长连接 → 客户机），
   * 客户机离线或忙碌时会等到服务端的 8 秒超时，所以要有 loading 态。
   */
  agentReceivedData: (id: number) =>
    fetch(`/api/agents/${id}/received`).then(unwrap<AgentReceivedDataDto>),

  // ── 文件仓库（M4.4）──
  /**
   * 仓库文件列表（按上传时间倒序）。
   *
   * `kind` 只在**已知合法值**时该传（`normal` / `installer` / `collected`）——
   * 服务端对非法值静默忽略（返回全量），所以前端若想"筛选"，
   * 传已知值比自己过滤更省事；但两者结果一致，本页选择了本地筛选。
   */
  files: (kind?: string | null) =>
    fetch(`/api/files${kind ? `?kind=${encodeURIComponent(kind)}` : ''}`).then(
      unwrap<FileRepoListDto>,
    ),

  /**
   * 删除一个仓库文件（**仅 Admin**）。
   *
   * ⚠️ **不影响已经分发到客户机上的副本** —— 那些文件已经在那台机器的磁盘上了。
   */
  deleteFile: (id: number) =>
    fetch(`/api/files/${id}`, { method: 'DELETE' }).then(
      unwrap<{ ok: boolean; status: string; message: string }>,
    ),

  /**
   * 下载直链（**不是** fetch，直接交给浏览器 / `<a>`）。
   *
   * 与安装包同一个理由：文件可能几个 GB，服务端开了 `Range`（断点续传 / 下载器接管），
   * 交给浏览器自己下既不占内存也不会让页面假死。
   */
  fileDownloadUrl: (id: number) => `/api/files/${id}/download`,

  /**
   * 发起一次分块上传，拿回会话 id 与建议分块大小。
   *
   * ⚠️ 拿到会话只是"**报名**"，字节一个都还没传 —— 真正传字节的是
   * `PUT uploads/{id}?offset=`（见 `uploadFileChunked`）。
   *
   * `size` 必填：服务端据此预检磁盘余量，也防止"边传边涨"。
   */
  createFileUpload: (request: { fileName: string; size: number; kind?: string | null }) =>
    fetch('/api/files/uploads', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({
        fileName: request.fileName,
        size: request.size,
        kind: request.kind ?? null,
      }),
    }).then(unwrap<FileUploadSessionDto>),

  /** 查会话当前进度（续传前先问"服务端收到哪了"）。 */
  fileUploadStatus: (uploadId: string) =>
    fetch(`/api/files/uploads/${encodeURIComponent(uploadId)}`).then(
      unwrap<FileUploadSessionDto>,
    ),

  /**
   * 完成上传（服务端校验大小 + **自己算一遍 SHA256** + 原子移入仓库）。
   *
   * 不传 `sha256` 是有意的：浏览器算 2GB 文件的摘要要把整个文件读进内存，
   * 而服务端无论如何都会自己算一份 —— 客户端自报的摘要只当"期望值"比对，
   * 这里干脆不报，省掉一次注定做不到的全量读取。
   */
  completeFileUpload: (uploadId: string, sha256?: string | null) =>
    fetch(`/api/files/uploads/${encodeURIComponent(uploadId)}/complete`, {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ sha256: sha256 ?? null }),
    }).then(unwrap<FileUploadResultDto>),

  /** 中止上传（主动放弃时调用；过期会话由服务端后台清理兜底）。 */
  abortFileUpload: (uploadId: string) =>
    fetch(`/api/files/uploads/${encodeURIComponent(uploadId)}`, { method: 'DELETE' }).then(
      unwrap<{ ok: boolean }>,
    ),

  // ── 文件分发（M4.4）──
  /**
   * 分发任务列表（最近在前，含每个目标的进度）。
   *
   * 读要 `CanOperate`；**建任务只给 Admin**（推文件到客户机 / 从客户机取文件，
   * 都是"作用到别的机器上"的动作，与远程重启同级）。
   */
  distributions: (limit = 50) =>
    fetch(`/api/distributions?limit=${limit}`).then(unwrap<DistributionListDto>),

  /** 单个任务详情（含每个目标的实时进度）。 */
  distributionDetail: (id: number) =>
    fetch(`/api/distributions/${id}`).then(unwrap<DistributionTaskDto>),

  /**
   * 新建**下发**任务（把仓库里的文件推给一批客户机）。
   *
   * ⚠️ 建单时**为每个目标建满明细**（含当时离线的机器）——
   * 离线机器不推 SignalR，它们上线时会自己补领。
   * 所以"有的机器现在离线"不是错误，任务也不会因此失败。
   *
   * 🔥 `targets` 里**每台各带一个存放路径**（`targetDir`）—— 这是当前形状。
   * 路径是绝对路径，留空 = 用那台机器配置的默认目录。
   * 越界（不在那台机器的可写白名单里）时由**客户机**拒绝<b>那一台</b>并回报失败原因。
   *
   * ⚠️ 服务端另有一条**旧形状**（顶层 `targetAgentIds` + 共用的 `targetDir`），
   * 本页不再用它 —— 那是留给冒烟脚本与老调用方的。
   */
  createPushDistribution: (request: {
    fileId: number
    targets: { agentId: number; targetDir?: string | null }[]
  }) =>
    fetch('/api/distributions', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({
        fileId: request.fileId,
        // 逐台清一遍空白：空串与 undefined 对服务端是同一件事（"用默认目录"）。
        targets: request.targets.map((t) => ({
          agentId: t.agentId,
          targetDir: t.targetDir?.trim() || null,
        })),
      }),
    }).then(unwrap<DistributionTaskDto>),

  /**
   * 新建**回传**任务（把客户机上的某个文件收上来）。
   *
   * 🔥 **2026-10-15 起不再限制目录**：`sourcePath` 只要是绝对路径、文件真实存在即可，
   * 客户机<b>不再</b>拿白名单目录卡它（白名单机制已整体删除）。
   *
   * ⚠️ 服务端**无从验证**这个路径（它不知道对方磁盘长什么样），
   * 客户机是唯一执行者：真取不到时它会把原因回报上来（页面上能看到那行红字）。
   */
  createCollectDistribution: (request: {
    targetAgentIds: number[]
    sourcePath: string
    note?: string | null
  }) =>
    fetch('/api/distributions/collect', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({
        targetAgentIds: request.targetAgentIds,
        sourcePath: request.sourcePath.trim(),
        note: request.note?.trim() || null,
      }),
    }).then(unwrap<DistributionTaskDto>),

  // ═══════════════════════════════════════════════════════════════
  // 远程桌面（M5）
  //
  // ⚠️ 整组端点都是 **AdminOnly**（决策 11：非管理员连"只读观看"都不允许）。
  //    前端按角色隐藏入口只是"别让人看到一堆点了就 403 的东西"，
  //    真正的闸门在服务端。
  //
  // 🔥 **画面本身不走这里的任何一条**：它经 `/hubs/remote` 的 SignalR 推过来。
  //    走 HTTP 一问一答做不到 60 fps，也会把每一帧都变成一次完整的请求开销。
  // ═══════════════════════════════════════════════════════════════

  /** 当前所有远控会话（内存账本；中心重启后为空是正常的）。 */
  remoteSessions: () => fetch('/api/remote/sessions').then(unwrap<RemoteSessionDto[]>),

  /** 单个会话的详情；不存在时 404。 */
  remoteSession: (sessionId: string) =>
    fetch(`/api/remote/sessions/${encodeURIComponent(sessionId)}`).then(unwrap<RemoteSessionDto>),

  /**
   * 某台被控机的编码能力。
   *
   * ⚠️ 返回 `probed: false` **不等于"这台机器不支持远程桌面"** ——
   * 它只是"客户机还没上报过"（客户机建立长连接后会立刻报一次）。
   * 页面必须把这两种说法分开，否则会给出一个凭空的否定结论。
   */
  remoteCapabilities: (agentId: number) =>
    fetch(`/api/remote/capabilities/${agentId}`).then(unwrap<RemoteCapabilityDto>),

  /**
   * 某台被控机的**屏清单**（M5.5 多屏）。
   *
   * 🔥 **这个端点永远"先返回"**：它只是把索取指令发下去，清单要等客户机
   * 上行回来才知道。所以第一次调通常拿到 `pending: true` + 空列表 ——
   * 那不是"没有屏"，是"还在问"。页面必须区分，并过约 1 秒再问一次。
   *
   * @param refresh `true` = 明确要求重采（用户点了刷新）；否则只在缓存缺失/过期时才问。
   */
  remoteScreens: (agentId: number, refresh = false) =>
    fetch(
      `/api/remote/screens/${agentId}${refresh ? '?refresh=true' : ''}`,
    ).then(unwrap<RemoteScreensDto>),

  /**
   * 建立会话 —— 让一台机器开始抓屏推流。
   *
   * 🔥 **返回 200 不代表画面已经出来了**：中心下发指令用的是"发出去就不管"的方式，
   * 所以回来的状态只会是 `requested`。真正的结论（`streaming` / `failed` + 原因）
   * 由被控端上报后经 `/hubs/remote` 的 `SessionChanged` 推过来。
   * 页面必须照着这个语义画：先显示"正在连接"，而不是"已连接"。
   */
  createRemoteSession: (request: {
    agentId: number
    screenId?: string | null
    displayMode?: string | null
    maxFps?: number | null
    /**
     * **观看端能显示哪些格式**（M5.4-b 第 7 步）。
     *
     * 🔥 必须传 —— 它是编码格式协商三项输入里的第二项，而中心看不到浏览器。
     * ⚠️ 不传时的语义是**"没声明"**（中心按带内 H.264 回落），
     * 与 `{ declared: true, codecs: [] }`（"一种都显示不了"⇒ 拒绝）**不是一回事**。
     * 用 `probeViewerVideo()` 拿这个值。
     */
    viewerVideo?: RemoteViewerVideoCapabilities | null

    /**
     * 质量档（M7.16，取值见 `REMOTE_QUALITY_PROFILES`；`null` = `auto`）。
     *
     * 🔥 **它必须在建会话时就带上**：不带的话每一路新会话都从 `auto`（12 fps / 6 Mbps）
     * 起步，"档位"只能等连上之后再点一次；而「按当前设置重建会话」会把它
     * **悄悄丢掉** —— 用户刚点的「流畅（60 帧）」在一次重建后退回 12 帧，
     * 页面上没有任何一处说这件事。
     */
    quality?: string | null
  }) =>
    fetch('/api/remote/sessions', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({
        agentId: request.agentId,
        screenId: request.screenId?.trim() || null,
        displayMode: request.displayMode ?? null,
        maxFps: request.maxFps ?? null,
        viewerVideo: request.viewerVideo ?? null,
        quality: request.quality ?? null,
      }),
    }).then(unwrap<RemoteSessionDto>),

  /**
   * 结束会话。
   *
   * 💡 它在服务端的授权分类里是 **Allowed**（因为它只做减法）——
   * 未授权时仍然必须能"让正在进行的远控停下来"。
   */
  stopRemoteSession: (sessionId: string) =>
    fetch(`/api/remote/sessions/${encodeURIComponent(sessionId)}`, { method: 'DELETE' }).then(
      unwrap<RemoteSessionDto>,
    ),

  /**
   * 切换黑白（灰度）模式。
   *
   * ⚠️ 它是**会话级**的：编码只做一路，所以一个人切黑白，
   * **所有**看这路流的人都会变灰。
   *
   * ⚠️ 另外它有一个诚实的缺口：被控端执行后**没有回执**，
   * 所以返回成功只意味着"指令没抛异常"。返回的 `grayscale` 是**请求值**，
   * 不是"画面已经变灰了"。真伪只能靠眼睛看画面。
   */
  setRemoteGrayscale: (sessionId: string, enabled: boolean) =>
    fetch(`/api/remote/sessions/${encodeURIComponent(sessionId)}/grayscale`, {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ enabled }),
    }).then(unwrap<RemoteSessionDto>),

  /**
   * 切换**质量档位**（M5.5，取值见 `REMOTE_QUALITY_PROFILES`）。
   *
   * ⚠️ 与黑白模式同一个诚实缺口：被控端执行后**没有回执**，
   * 所以返回成功只意味着"指令送达了"。返回的 `quality` 是**请求值**，
   * 不是"编码器已经按这个档在跑"——后者要看 `agentStats` 的码率/帧率。
   *
   * 🔥 **它比黑白模式更可能"看不见效果"**：MF 的码率与质量参数是**创建时定死**的
   * （运行时改不动，`S_FALSE` + 回读不变），所以真正的改变往往要等编码器重建。
   * 现场如果"切了档码率没变"，先看被控端日志有没有重建，而不是怀疑这个端点。
   *
   * @param profile `auto` / `low` / `balanced` / `high`。
   * @param maxFps 帧率硬上限（可选，与档位推荐值取小）。
   */
  setRemoteQuality: (sessionId: string, profile: string, maxFps?: number | null) =>
    fetch(`/api/remote/sessions/${encodeURIComponent(sessionId)}/quality`, {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ profile, maxFps: maxFps ?? null }),
    }).then(unwrap<RemoteSessionDto>),

  /**
   * 请求**读一次**被控机的剪贴板（M5.5）。
   *
   * 🔥 它是 POST 不是 GET：这对被控机是一次真实动作（打开剪贴板读一次），
   * 而"某个进程一直开着剪贴板"在 Windows 上会让别的程序粘贴失败 ——
   * 这种事不该被当成可缓存、可预取、可被代理重放的 GET。
   *
   * ⚠️ 返回的文本**可能是上一次读到的旧内容**（服务端先把缓存给你）：
   * 这次的结果随后经 `ClipboardChanged` 推过来，页面要以推送为准。
   */
  readRemoteClipboard: (sessionId: string) =>
    fetch(`/api/remote/sessions/${encodeURIComponent(sessionId)}/clipboard/read`, {
      method: 'POST',
      headers: jsonHeaders,
    }).then(unwrap<RemoteClipboardDto | null>),

  /**
   * 最近一次读到的剪贴板内容（`null` = 这一路还没读过）。
   *
   * 💡 它**不触发**被控机动作，纯读中心的缓存 —— 页面切回这一路时用它回填，
   * 而不是再让被控机打开一次剪贴板。
   *
   * 🔴 **`?? null` 不是多余的**（2026-10-18 浏览器实证踩到）：服务端这一路是
   * `return Ok(TryGetClipboard(...))`，而 ASP.NET Core 的 `HttpNoContentOutputFormatter`
   * 会把 **null 值改成 204 No Content**，于是 `unwrap` 走 `status === 204 → undefined`。
   * 页面拿到 `undefined` 后把它当对象读 `.text` ⇒ **整个渲染函数抛异常 ⇒ 面板一片空白**
   * （连画面都出不来，因为 render 抛了之后统计卡片也不会更新）。
   * 这里把"没有内容"统一收敛成 `null`：语义只有一种，模板不必猜 `undefined`。
   */
  getRemoteClipboard: (sessionId: string) =>
    fetch(`/api/remote/sessions/${encodeURIComponent(sessionId)}/clipboard`)
      .then(unwrap<RemoteClipboardDto | null>)
      .then((dto) => dto ?? null),

  /**
   * 把一段文字**写进**被控机的剪贴板（M5.5）。
   *
   * 🔴 铁律 7：这是作用到**别的机器**的写操作，服务端是 `AdminOnly`。
   *
   * ⚠️ 超过 2 万字时服务端回 **400**（内容是调用方的问题，不是链路问题）——
   * 页面应当在发之前就判、并把"只会发前 2 万字"说出来，而不是让用户撞一次错误。
   */
  writeRemoteClipboard: (sessionId: string, text: string) =>
    fetch(`/api/remote/sessions/${encodeURIComponent(sessionId)}/clipboard`, {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ text }),
    }).then(unwrap<RemoteClipboardDto>),

  /**
   * 只编视口那一块（M5.5 的 ROI 编码；宽或高为 0 = 关掉）。
   *
   * 🔥 **调用方必须去抖**（本仓在滚动停止 300 ms 后才发）：每发一次都可能让
   * 被控端重建编码器（≈0.3 秒空窗、首帧必关键帧）。每滚一下就发一次的实现，
   * 会让画面一直停在关键帧上 —— 看起来像"帧率更低了"，而不像"发得太勤"。
   */
  setRemoteViewport: (sessionId: string, rect: { x: number; y: number; width: number; height: number }) =>
    fetch(`/api/remote/sessions/${encodeURIComponent(sessionId)}/viewport`, {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify(rect),
    }).then(unwrap<RemoteSessionDto>),

  /**
   * 送回一批输入事件（M5.2）。
   *
   * 🔥 **观看页面不走这个端点**（它走 `/hubs/remote` 的 `SendInput`）：
   *    HTTP 一问一答的固定开销远大于一批事件本身，而输入是高频、要回执、
   *    且必须与"我正在看哪一路"这条连接状态绑在一起的东西。
   *    这里是给**自动化脚本**用的低频入口。
   *
   * ⚠️ 请求体刻意**不带 sessionId**（会话 Id 在 URL 里）：同一个事实有两个来源，
   *    迟早会出现两者不一致的请求，而那时"按哪个走"就成了每次都要重新想一遍的问题。
   */
  sendRemoteInput: (sessionId: string, events: RemoteInputEventDto[]) =>
    fetch(`/api/remote/sessions/${encodeURIComponent(sessionId)}/input`, {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ events }),
    }).then(unwrap<RemoteInputAckDto>),

  /**
   * 打开 / 关闭这一路会话的输入回传（M5.2，中心侧闸门）。
   *
   * 🔥 它**不碰网络**：闸门就在中心的账本上。所以"已关闭"是一个**可靠**的结论
   *    （中心根本不会转发），而"已打开"只说明闸门开着 —— 被控端能不能收是另一件事
   *    （见 `RemoteSessionDto.inputState`）。
   *
   * 🔒 授权上是 **AdminOnly**：让别人的鼠标动起来，与"看到别人的屏幕"不是一回事。
   */
  setRemoteInputEnabled: (sessionId: string, enabled: boolean) =>
    fetch(`/api/remote/sessions/${encodeURIComponent(sessionId)}/input/enabled`, {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ enabled }),
    }).then(unwrap<RemoteSessionDto>),
}

/** 分块上传的进度回调参数。 */
export interface UploadProgress {
  /** 已传字节数（含本次分块已发出的部分）。 */
  uploaded: number
  total: number
}

/** 单次分块 PUT 的结果。 */
interface ChunkResult {
  status: number
  /** 服务端已收下的字节数（200 与 409 都会带）。 */
  receivedBytes: number
  message: string | null
}

/**
 * 分块上传一个文件（>2GB 可续传）。
 *
 * <p>
 * **为什么不用 `fetch`**：`fetch` 没有上传进度事件，一个 8 MB 的分块会"卡住不动"到整块传完。
 * `XMLHttpRequest` 的 `upload.onprogress` 才能让进度条真的动起来。
 * </p>
 *
 * <p>
 * **丢块怎么办**：`PUT` 返回 200（成功）或 **409**（偏移不符）。两种响应都带服务端的
 * `receivedBytes`，把它当作新的偏移继续即可 —— 所以**客户端不需要知道"刚才那块到底成没成"**，
 * 它只需要相信服务端报的数。这就是"可续传"的全部秘密，也是重连后能接着传的原因。
 * </p>
 *
 * <p>
 * ⚠️ 必须有"偏移不前进就报错"的守卫：若服务端因某种原因持续回同一个偏移，
 * 没有这道守卫就是**死循环**（界面上表现为进度条永远卡在同一个数、请求无限重发）。
 * </p>
 *
 * @param file 要上传的文件（`File` 可以直接 `slice`，不必读进内存）。
 * @param options.kind 类别（网页上传用 `normal`；客户机回传由服务端强制成 `collected`，传了也不作数）。
 * @param options.signal 取消信号。
 * @param options.onProgress 进度回调（服务端每收下一块就报一次）。
 */
export async function uploadFileChunked(
  file: File,
  options: {
    kind?: string | null
    signal?: AbortSignal
    onProgress?: (progress: UploadProgress) => void
  } = {},
): Promise<FileUploadResultDto> {
  const session = await api.createFileUpload({
    fileName: file.name,
    size: file.size,
    kind: options.kind ?? null,
  })

  const chunkBytes = clampChunkSize(session.chunkSizeBytes)
  let offset = Math.max(0, Math.min(session.receivedBytes ?? 0, file.size))

  options.onProgress?.({ uploaded: offset, total: file.size })

  // 空文件：一次分块都不必发，直接完成（服务端会算出空串的摘要）。
  while (offset < file.size) {
    if (options.signal?.aborted) throw new DOMException('上传已取消', 'AbortError')

    const end = Math.min(offset + chunkBytes, file.size)
    const blob = file.slice(offset, end)

    const result = await putChunk(session.uploadId, offset, blob, options.signal, (sent) =>
      options.onProgress?.({ uploaded: Math.min(offset + sent, file.size), total: file.size }),
    )

    // 200 = 收下了；409 = 你该从别处接着传（两者都带真实进度）。
    if (result.status !== 200 && result.status !== 409) {
      throw new ApiError(result.status, result.message ?? `上传分块失败（HTTP ${result.status}）`, result)
    }

    if (result.receivedBytes <= offset) {
      throw new ApiError(
        result.status,
        result.message ?? '服务端未记录新的字节，上传无法推进（已中止以免反复重发同一块）。',
        result,
      )
    }

    offset = result.receivedBytes
    options.onProgress?.({ uploaded: offset, total: file.size })
  }

  return await api.completeFileUpload(session.uploadId)
}

/** 服务端建议的块大小做上下限夹逼（与 Agent 侧同一套边界）。 */
function clampChunkSize(suggested: number): number {
  const min = 64 * 1024
  const max = 32 * 1024 * 1024
  if (!Number.isFinite(suggested) || suggested <= 0) return 8 * 1024 * 1024
  return Math.min(max, Math.max(min, suggested))
}

/** 发一个分块（请求体即原始字节），带上传进度与取消。 */
function putChunk(
  uploadId: string,
  offset: number,
  blob: Blob,
  signal: AbortSignal | undefined,
  onSent: (sentBytes: number) => void,
): Promise<ChunkResult> {
  return new Promise<ChunkResult>((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    xhr.open('PUT', `/api/files/uploads/${encodeURIComponent(uploadId)}?offset=${offset}`)
    xhr.setRequestHeader('Content-Type', 'application/octet-stream')

    xhr.upload.onprogress = (event) => {
      if (event.lengthComputable) onSent(event.loaded)
    }

    xhr.onload = () => {
      let receivedBytes = 0
      let message: string | null = null

      try {
        const parsed = JSON.parse(xhr.responseText || '{}') as {
          receivedBytes?: unknown
          message?: unknown
        }
        if (typeof parsed.receivedBytes === 'number') receivedBytes = parsed.receivedBytes
        if (typeof parsed.message === 'string') message = parsed.message
      } catch {
        /* 非 JSON 响应体（例如代理返回的 HTML）—— 保留状态码即可 */
      }

      resolve({ status: xhr.status, receivedBytes, message })
    }

    xhr.onerror = () => reject(new Error('网络错误：分块上传失败。'))
    xhr.ontimeout = () => reject(new Error('分块上传超时。'))
    xhr.onabort = () => reject(new DOMException('上传已取消', 'AbortError'))

    if (signal) {
      if (signal.aborted) {
        xhr.abort()
        return
      }
      signal.addEventListener('abort', () => xhr.abort(), { once: true })
    }

    xhr.send(blob)
  })
}
