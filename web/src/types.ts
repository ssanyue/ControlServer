/**
 * 与 `ControlServer.Core.Models` 一一对应的领域类型（camelCase）。
 *
 * ⚠️ 服务端 `GET /api/config` 返回的 `settings` 就是这些对象的**原样 JSON**
 * （契约层用 JsonElement 透传，配置形状只有一份真相）。
 * 因此这里改字段名必须同步改 C# 的 `ControlServer.Core.Models`，反之亦然。
 */

// ═══════════════════════════════════════════════════════════════
// 会话 / 状态 / 日志
// ═══════════════════════════════════════════════════════════════

export interface CurrentUser {
  userName: string
  role: string
  isAuthenticated: boolean
  /**
   * 是否被要求先改密码（M3）。
   *
   * ⚠️ 登录响应与 `/api/auth/me` **两处都带**它 —— 只在登录响应里带的话，
   * 用户刷新页面就会丢掉这个标记，于是"必须改密"这条约束在刷新那一刻失效。
   * 前端据此弹出不可关闭的改密弹窗；真正的闸门在服务端（未改密时除白名单外一律 403），
   * 所以这里漏判只是"少弹了个窗"，不会变成"可以不改"。
   */
  mustChangePassword: boolean
}

export interface LoginResponse {
  userName: string
  role: string
  mustChangePassword: boolean
}

export interface StatusSnapshot {
  product: string
  version: string
  machineName: string
  osPlatform: string
  serverTime: string
  timeZone: string
  httpPort: number
  pairingPort: number
  listenerRunning: boolean
  uptimeSeconds: number
  recentLogCount: number
}

/** 运维日志（服务端 Serilog + LogService 环形缓冲）。 */
export interface LogDto {
  timestamp: string
  level: string
  category: string
  message: string
}

/** 收发日志（发了什么字节 / 收到了什么字节）—— 与运维日志是两回事。 */
export interface TrafficLogDto {
  time: string
  direction: string
  protocol: string
  targetIp: string
  port: number
  data: string
  success: boolean
  message: string
  buttonName: string
  actionName: string
}

// ═══════════════════════════════════════════════════════════════
// 配置领域模型
// ═══════════════════════════════════════════════════════════════

export interface ButtonAction {
  id: string
  name: string
  protocol: string
  targetIp: string
  targetPort: number
  dataType: string
  dataContent: string
  delayMs: number
  pairingCode: string
  pairingCodeType: string
}

/** 月度计划里的「某月 + 该月生效的日期」。 */
export interface MonthDaySchedule {
  /** 月份 1–12。 */
  month: number
  /** 该月生效的日期 1–31。 */
  days: number[]
}

/**
 * 月度定时计划。
 *
 * ⚠️ `time` / `months` / `days` 是**旧格式**字段，服务端清洗时会自动迁移：
 * `time → times`、`months + days`（交叉匹配）`→ monthDays`（每月独立日期）。
 * 编辑器只写 `times` 与 `monthDays`，旧字段靠服务端补齐，不必在前端维护。
 */
export interface MonthlyPlan {
  id: string
  enabled: boolean
  name: string
  /** 旧字段（单个时间点），保留以兼容旧配置。 */
  time: string
  times: string[]
  /** 旧字段（月份列表）。 */
  months: number[]
  /** 旧字段（日期列表）。 */
  days: number[]
  monthDays: MonthDaySchedule[]
}

export interface ButtonConfig {
  id: string
  order: number
  label: string
  pairingCode: string
  pairingCodeType: string
  buttonColor: string
  textColor: string
  nameFontSize: number
  buttonImage: string
  width: number
  height: number
  layoutX: number
  layoutY: number
  actions: ButtonAction[]
  timerEnabled: boolean
  timerMode: string
  timerTimes: string[]
  timerDays: number[]
  monthlyPlans: MonthlyPlan[]
}

export interface SubButton {
  id: string
  label: string
  buttonColor: string
  textColor: string
  nameFontSize: number
  buttonImage: string
  width: number
  height: number
  pairingCode: string
  pairingCodeType: string
  actions: ButtonAction[]
}

export interface SubButtonSet {
  id: string
  order: number
  label: string
  buttonColor: string
  textColor: string
  nameFontSize: number
  buttonImage: string
  width: number
  height: number
  layoutX: number
  layoutY: number
  targetIp: string
  targetPort: number
  subButtons: SubButton[]
}

export interface ControlSettings {
  appName: string
  backgroundImagePath: string
  listenPort: number
  defaultButtonImage: string
  buttons: ButtonConfig[]

  /**
   * 组合按钮（2026-10-15，需求⑤）。
   * ⚠️ 旧配置（后端还没这个字段时导出的）反序列化后可能是 undefined ——
   * `mergeTiles` 用 `?? []` 兜住，不要在这里假设它一定存在。
   */
  comboButtons?: ComboButton[]
  subButtonSets: SubButtonSet[]
  freeLayoutEnabled: boolean
}

export interface ConfigCounts {
  buttons: number
  subButtonSets: number
  subButtons: number
}

export interface ConfigView {
  settings: ControlSettings
  version: number
  /** Ok / Missing / Corrupt */
  state: string
  notes: string[]
  counts: ConfigCounts
  isEmpty: boolean
}

export interface ConfigExportResult {
  fileName: string
  encoding: string
  content: string
  version: number
  imageCount: number
  bytes: number
}

export interface ConfigImportResult {
  ok: boolean
  version: number
  counts: ConfigCounts
  imageCount: number
  ignoredFields: string[]
  notes: string[]
  message: string
}

// ═══════════════════════════════════════════════════════════════
// 动作执行
// ═══════════════════════════════════════════════════════════════

export interface ActionStepDto {
  index: number
  actionName: string
  protocol: string
  targetIp: string
  targetPort: number
  ok: boolean
  bytesSent: number
  message: string
  dataPreview: string
  resolvedFromCommand: string | null
}

export interface ActionExecutionDto {
  runId: string
  buttonName: string
  startedAt: string
  elapsedMs: number
  ok: boolean
  successCount: number
  failureCount: number
  summary: string
  steps: ActionStepDto[]
}

/** 组合按钮里**一段**的执行结果（2026-10-15，需求⑤）。 */
export interface ComboSegmentDto {
  buttonId: string
  buttonLabel: string
  /** 本段执行完之后等待的毫秒数（最后一段恒为 0）。 */
  delayAfterMs: number
  /** 这一段内部的动作结果；`null` = 这一段没有执行（见 `skipped`）。 */
  result: ActionExecutionDto | null
  skipped: string | null
}

/** 一次「组合按钮执行」的结果。⚠️ 它是**分段**的，不是动作平铺。 */
export interface ComboExecutionDto {
  runId: string
  comboLabel: string
  startedAt: string
  elapsedMs: number
  ok: boolean
  segmentCount: number
  successCount: number
  failureCount: number
  summary: string
  segments: ComboSegmentDto[]
  /** 被剔除的成员 Id（对应主按钮已不存在）。 */
  skippedButtonIds: string[]
}

/**
 * 把组合的执行结果**压平**成结果面板认的那一种形状。
 *
 * <p>
 * 面板（`ActionResultPanel`）是按"一次执行 = 一串动作"渲染的，而组合是"一串按钮、每个按钮各自一串动作"。
 * 与其让面板再学一遍组合的形状（它会立刻长出两套分支），不如在这里把成员名前置到动作名上 ——
 * 面板照旧渲染，而"这一条属于哪个按钮"这个信息一点没丢。
 * </p>
 */
export function flattenComboExecution(combo: ComboExecutionDto): ActionExecutionDto {
  return {
    runId: combo.runId,
    buttonName: combo.comboLabel,
    startedAt: combo.startedAt,
    elapsedMs: combo.elapsedMs,
    ok: combo.ok,
    successCount: combo.successCount,
    failureCount: combo.failureCount,
    summary: combo.summary,
    steps: combo.segments.flatMap((seg) =>
      (seg.result?.steps ?? []).map((step) => ({
        ...step,
        // ⚠️ 前缀用「成员名 / 」而不是覆盖：原来的动作名（可能来自指令库）必须留着。
        actionName: `${seg.buttonLabel} / ${step.actionName}`,
      })),
    ),
  }
}

export interface EncodePreviewDto {
  dataType: string
  text: string
  hex: string
  hexCompact: string
  length: number
  ansiEncoding: string
  error: string | null
}

// ═══════════════════════════════════════════════════════════════
// 系统诊断与时间同步（M2-E4）
// ═══════════════════════════════════════════════════════════════

/** 时间同步详情（`GET /api/system/info` 内嵌）。 */
export interface TimeSyncInfoDto {
  /** 宿主是否会自动校准（`Server:Time:AutoSync`）。 */
  autoSync: boolean
  /** 自动重校间隔（秒）；`autoSync` 为 false 时无意义。 */
  resyncIntervalSeconds: number
  /** 是否至少成功校准过一次。 */
  synchronized: boolean
  /** 上次成功校准时刻（**北京时间**）；从未成功为 null。 */
  lastSyncAt: string | null
  /** 上次成功所用的 NTP 服务器。 */
  source: string | null
  /** 当前生效的时钟偏移（毫秒，正 = 系统钟慢，需往后加）。 */
  offsetMs: number
  /** 偏移的展示文本，如 `+0.128s`。 */
  offsetText: string
  /** 上次校准的往返耗时（毫秒）。 */
  roundTripMs: number | null
  /** 当前是否处于「已校正」状态（偏移非零）。 */
  corrected: boolean
  /** 最后一次失败原因；成功后清空。 */
  lastError: string | null
  attemptCount: number
  failureCount: number
  /** **生效**的 NTP 服务器列表（已应用内置默认回退）。 */
  servers: string[]
}

/**
 * 一次手动校准的结果（`POST /api/system/timesync`）。
 *
 * 成功与失败都返回 **200** —— 这是「查校准结果」的接口，失败原因本身就是要展示的信息。
 * 调用方看 `ok`。
 */
export interface TimeSyncResultDto {
  ok: boolean
  offsetMs: number
  offsetText: string
  /** 本次校准相对上一次的偏移变化（毫秒）。 */
  offsetJumpMs: number
  source: string | null
  roundTripMs: number | null
  error: string | null
  /** 逐个服务器的尝试记录，便于排障。 */
  attempts: string[]
  /** 校准后的服务端时间（北京时间）。 */
  serverTime: string
}

/** 定时调度摘要（`GET /api/system/info` 内嵌，只给计数）。 */
export interface TimerSummaryDto {
  enabled: boolean
  tickIntervalSeconds: number
  catchUpWindowSeconds: number
  retentionDays: number
  firedCount: number
  missedCount: number
  triggerCount: number
  nextFireAt: string | null
}

/**
 * 系统信息（`GET /api/system/info`）—— 面向运维的诊断详情。
 *
 * 与 `GET /api/status` 的分工：后者是控制台首页的轻量状态；这里是排查问题时才看的细节。
 * **刻意不含机器码**（机器码是授权信息，只走 AdminOnly 的 `machine-code`）。
 */
export interface SystemInfoDto {
  product: string
  version: string
  machineName: string
  osPlatform: string
  /** 服务端当前时间（**校正后的北京时间**）。 */
  serverTime: string
  timeZone: string
  httpPort: number
  pairingPort: number
  listenerRunning: boolean
  uptimeSeconds: number
  recentLogCount: number
  dataDirectory: string
  timeSync: TimeSyncInfoDto
  timers: TimerSummaryDto
}

// ═══════════════════════════════════════════════════════════════
// 监听地址热切换（M2-E5）
// ═══════════════════════════════════════════════════════════════

/** 当前**实际**监听地址（`GET /api/system/address`）。注意不是配置里的值。 */
export interface ServerAddressDto {
  urls: string[]
  port: number
  /** 端口持久化文件路径（改了端口之后重启会从这里恢复）。 */
  overrideFile: string
  /** 该文件是否存在（true = 端口不是来自默认配置）。 */
  persisted: boolean
}

/** 热切换的受理结果（`POST /api/system/address`）。 */
export interface ServerAddressChangeResultDto {
  ok: boolean
  /** Accepted / NoChange / Invalid / PortUnavailable / NotReady */
  status: string
  oldUrls: string[]
  newUrls: string[]
  newPort: number | null
  /** 切换在服务端是**同步完成**的，故恒为 0；留给"未来若要改成延迟切换"的余地。 */
  secondsUntilSwitch: number
  message: string
}

/**
 * 监听地址变更的实时通知（`ServerAddressChanged`）。
 *
 * <p>
 * 这是唯一一条"让客户端自己搬家"的事件：端口换了，当前页面所在的原点就不存在了。
 * `changed` 相位到达时旧端口已经不再接受新连接，但**已建立的 WebSocket 仍然可用**，
 * 所以这条通知本身能送达 —— 前端据此跳转。
 * </p>
 */
export interface ServerAddressChangedDto {
  phase: 'switching' | 'changed' | 'failed'
  oldUrls: string[]
  newUrls: string[]
  newPort: number | null
  error: string | null
  message: string
}

// ═══════════════════════════════════════════════════════════════
// 网格渲染用的合并视图
// ═══════════════════════════════════════════════════════════════

/**
 * 主界面上的一个瓦片：按钮 / 组合按钮 / 子按钮集（**三者同级**、按 order 混排）。
 *
 * ⚠️ order 相同时的次序必须是 **按钮 → 组合按钮 → 子按钮集**：
 * 服务端清洗器（`ControlSettingsNormalizer.SortAndReindex`）就是这么排的，
 * 两边不一致会出现"界面顺序与保存后的顺序对不上"这种说不清的现象。
 */
export type Tile =
  | { kind: 'button'; order: number; button: ButtonConfig }
  | { kind: 'combo'; order: number; combo: ComboButton }
  | { kind: 'set'; order: number; set: SubButtonSet }

/** 三者的同级次序（越小越靠前）——与清洗器 `KindButton/KindCombo/KindSet` 一一对应。 */
const TILE_KIND_RANK: Record<Tile['kind'], number> = { button: 0, combo: 1, set: 2 }

/** 把 buttons、comboButtons 与 subButtonSets 按 order 合并成一个序列。 */
export function mergeTiles(settings: ControlSettings | null): Tile[] {
  if (!settings) return []

  const tiles: Tile[] = [
    ...(settings.buttons ?? []).map<Tile>((button) => ({ kind: 'button', order: button.order ?? 0, button })),
    ...(settings.comboButtons ?? []).map<Tile>((combo) => ({ kind: 'combo', order: combo.order ?? 0, combo })),
    ...(settings.subButtonSets ?? []).map<Tile>((set) => ({ kind: 'set', order: set.order ?? 0, set })),
  ]

  return tiles.sort((a, b) => {
    if (a.order !== b.order) return a.order - b.order
    return TILE_KIND_RANK[a.kind] - TILE_KIND_RANK[b.kind]
  })
}

/**
 * 组合按钮（2026-10-15，需求⑤）：把若干个**已有主按钮**按顺序串起来，段间可设延迟。
 *
 * ⚠️ 成员是**引用**（`buttonId`）而不是动作副本：按钮改了，组合下次触发就是新行为。
 * 代价是"按钮被删了"要处理 —— 服务端清洗时会剔除悬空成员，
 * 所以正常情况下 `steps` 里的每一项都指向一个真实存在的按钮。
 */
export interface ComboButton {
  id: string
  order: number
  label: string
  pairingCode: string
  pairingCodeType: string
  buttonColor: string
  textColor: string
  nameFontSize: number
  buttonImage: string
  width: number
  height: number
  layoutX: number
  layoutY: number
  steps: ComboButtonStep[]
}

/** 组合按钮里的一步：执行哪个主按钮 + **执行完之后**等多久。 */
export interface ComboButtonStep {
  buttonId: string
  /**
   * 本步执行完之后、下一步之前等待的毫秒数（0..60000）。
   *
   * ⚠️ **最后一步的延迟永远不会生效**（后面没有下一步）—— 服务端清洗时会把最后一步归零，
   * 所以界面上不该给最后一行留一个"填了也没用"的输入框。
   */
  delayMs: number
}

// ═══════════════════════════════════════════════════════════════
// 指令库（M2-E3）
// ═══════════════════════════════════════════════════════════════

/**
 * 一条可复用的发送指令。
 *
 * ⚠️ 形状由 `Core.Models.CommandItem` 定义（服务端用 JsonElement 透传，契约层不另抄一份）。
 *
 * 与 `ButtonAction` 的关键差别：**没有目标 IP / 端口 / 延迟 / 对接码** ——
 * 指令只描述"发什么"，"发到哪"由引用它的动作决定。
 */
export interface CommandItem {
  id: string
  name: string
  /** 仅 `UDP` | `TCP`（**不含 WoL** —— 指令库里的指令没有 MAC 语义）。 */
  protocol: string
  /**
   * String / Hex / ANSI / ASCII（**不含「指令库」**，否则会递归解析）。
   *
   * 历史上还有一个 `UTF-8` 取值，与 `String` 编码结果逐字节相同 —— 2026-10-08 已合并，
   * 界面上不再提供；服务端收到 `UTF-8` 会归一为 `String`。
   */
  dataType: string
  data: string
}

export interface CommandListDto {
  commands: CommandItem[]
  /** `commands` 文档的乐观锁版本号。 */
  version: number
  count: number
}

/**
 * 指令库增删改的统一响应。
 *
 * 无论成败都带回**服务端最新全量列表**，所以失败时直接用 `commands` 刷新界面即可，
 * 不必再发一次 GET。
 */
export interface CommandMutationDto {
  ok: boolean
  /** Ok / NotFound / DuplicateName / Invalid / StorageCorrupt */
  status: string
  version: number
  commands: CommandItem[]
  /** 删除或失败时为 null。 */
  command: CommandItem | null
  /** 重名时，与之冲突的那条既有指令 Id。 */
  conflictId: string | null
  message: string | null
}

// ═══════════════════════════════════════════════════════════════
// 定时调度（M2-E4）
// ═══════════════════════════════════════════════════════════════

/** 一个生效的定时触发点。 */
export interface TimerTriggerDto {
  /** 按钮 Id；月度计划是 `<buttonId>:<planId>`。手动触发端点用的就是它。 */
  refId: string
  buttonId: string
  buttonLabel: string
  /** Daily / SpecificDays / MonthlyPlan */
  kind: string
  /** 触发时刻（北京时间 `HH:mm:ss`）。 */
  time: string
  /** 星期条件（仅 SpecificDays）；0=周日 … 6=周六。 */
  days: number[]
  /** 月度日期条件的文本形式（仅 MonthlyPlan），如 `7月21日`。 */
  monthDays: string[]
  /** 按调度规则，这个触发点今天是否适用。 */
  appliesToday: boolean
  firedToday: boolean
  firedAt: string | null
  /** 今天适用、且时刻已过。 */
  elapsed: boolean
}

export interface TimerOverviewDto {
  enabled: boolean
  tickIntervalSeconds: number
  catchUpWindowSeconds: number
  missedScanWindowSeconds: number
  retentionDays: number
  firedCount: number
  missedCount: number
  serverTime: string
  nextFireAt: string | null
  triggers: TimerTriggerDto[]
}

export interface TimerFireResultDto {
  ok: boolean
  /** Fired / NotFound / AlreadyFired */
  status: string
  refId: string
  buttonLabel: string | null
  date: string
  time: string
  message: string
  /** 被幂等闸门挡下时为 null。 */
  execution: ActionExecutionDto | null
}

// ═══════════════════════════════════════════════════════════════
// 用户账号管理（M3 RBAC）
// ═══════════════════════════════════════════════════════════════

/**
 * 一个账号的对外视图（`GET /api/users`）。
 *
 * ⚠️ **刻意没有 `passwordHash` / `salt`** —— 服务端把"口令材料不出接口"
 * 做成了类型级保证（实体与视图是两个类型）。这里也不该出现任何口令字段。
 */
export interface UserDto {
  id: number
  userName: string
  /** Admin / Operator / Viewer（与服务端 `Roles.All` 一致）。 */
  role: string
  /** 停用后不能登录；已签发的会话会在**下一次请求**被 `OnValidatePrincipal` 作废。 */
  isEnabled: boolean
  /** 为 true 时服务端强制改密闸门生效，除白名单端点外一律 403。 */
  mustChangePassword: boolean
  /** 连续登录失败达上限后的锁定是否仍在有效期内。 */
  isLocked: boolean
  createdAt: string
  lastLoginAt: string | null
  lockedUntil: string | null
}

/**
 * 账号写操作的统一响应（`POST/PUT/DELETE /api/users`）。
 *
 * 与 `CommandMutationDto` 同思路：**无论成败都带回服务端当前全量列表**。
 * 因此失败时（含 409）前端可直接用 `users` 覆盖本地，不必再发一次 GET，
 * 界面也不会停在"我以为改成了"的旧状态。
 */
export interface UserMutationResult {
  ok: boolean
  /**
   * Ok / Invalid / DuplicateName / NotFound / LastAdmin / SelfTarget。
   *
   * ⚠️ 走到前端时通常已经被 HTTP 状态码分流（400/404/409），`status` 用于区分
   * **同类状态码下的不同原因**（例如 409 既可能是 `LastAdmin` 也可能是 `SelfTarget`）。
   */
  status: string
  message: string
  /** 新增/修改后的账号；删除或失败时为 null。 */
  user: UserDto | null
  users: UserDto[]
}

/** 可选角色（`GET /api/users/roles`）—— 中文显示名由服务端给，前端不硬编码。 */
export interface RoleOption {
  name: string
  displayName: string
}

// ═══════════════════════════════════════════════════════════════
// 设备授权（M3）
// ═══════════════════════════════════════════════════════════════

/** 授权文件里的展示性信息（不含签名字段 —— 签名属内部实现，没有展示价值）。 */
export interface LicenseDocumentDto {
  serialNumber: string
  vendor: string
  customer: string
  /** 签发日（北京日期 `yyyy-MM-dd`）。 */
  issuedOn: string
  isUniversal: boolean
  /** 当前生效的绑定机器码（文档里的 `mc`，或通用授权的侧车绑定）。 */
  boundMachineCode: string
  /** 绑定时刻（北京时间文本）；未记录为空串。 */
  boundAt: string
  /** 授权里的联网校对时间 `ivt`（北京时间文本）。 */
  networkVerifiedAt: string
  /** 通用授权的最迟导入绑定时刻（北京时间文本）；不限为空串。 */
  importDeadline: string
}

/** 本机自动试用的展示信息。 */
export interface LicenseTrialDto {
  /** 试用起始时刻（北京时间文本）。 */
  issuedAt: string
  /** 到期日（北京日期 `yyyy-MM-dd`）。 */
  expiresOn: string
  days: number
  /** 起点时间来源：`ntp` = 联网校时；`local` = 联网不可得，退回本机时钟。 */
  anchorSource: string
}

/**
 * 设备授权状态（横幅 / 徽章 / 授权页共用）。
 *
 * ⚠️ **判断"能不能用"一律读 `isUsable`，不要自己比较 `statusCode`** ——
 * 状态码是有序数字，但"可用"是一个**白名单**（只含 `Authorized` / `Trial`），
 * 不是区间。用 `statusCode >= 1` 会把"已过期 / 异常"当成成功。
 */
export interface LicenseDto {
  /** 0..4，与 Core 的 `LicenseStatus` 对齐。 */
  statusCode: number
  /** NotAuthorized / Authorized / Expired / Error / Trial。 */
  status: string
  statusDisplayName: string
  /** 设备当前是否可用。**前端只读这个字段**。 */
  isUsable: boolean
  /** full / trial / temp；本机自动试用也是 `trial`。 */
  kind: string
  kindDisplayName: string
  /** 剩余天数：`-1` 未知、`99999` 永久、`0` 已过期。 */
  remainingDays: number
  /** 到期时刻（北京时间）；永久或未知为 null。 */
  expiresAt: string | null
  /** 面向用户的一句话摘要。 */
  info: string
  /** 失败原因；可用时为空串。 */
  lastError: string
  /** 本机机器码（管理员可复制给供应方）。 */
  machineCode: string
  /** 当前**生效**的执行档位：Off / Warn / Enforce。 */
  enforcement: string
  /** 配置文件里写的档位（可能被服务端升级，见下）。 */
  configuredEnforcement: string
  /**
   * 当前生效的是否为**本机自动试用**（而不是厂商签发的授权）。
   *
   * 服务端有条升级规则：配置写 `Warn` 但本机已存在试用记录时，
   * **生效档位升为 `Enforce`** —— 否则试用到期只是提示、拦不住人。
   */
  isLocalTrial: boolean
  /** 厂商授权的展示信息；无授权文件为 null。 */
  document: LicenseDocumentDto | null
  /** 本机试用记录；从未生成过为 null。 */
  trial: LicenseTrialDto | null
}

/**
 * 授权写操作的统一响应（`POST /api/license/import` 等）。
 *
 * 与 `UserMutationResult` 同思路：**无论成败都带回服务端当前全量状态**，
 * 失败时前端直接用它刷新界面即可，不会停在"导入失败了但界面还在转圈"的中间态。
 */
export interface LicenseMutationResult {
  ok: boolean
  /**
   * Ok / Malformed / SignatureInvalid / UnsupportedVersion / UnknownKind /
   * MachineMismatch / Expired / DeadlineExceeded / MissingNetworkAnchor /
   * ClockUnusable / no-network-time / already-licensed / trial-active …
   */
  status: string
  message: string
  /**
   * 服务端当前授权状态（全量）。
   *
   * ⚠️ 声明成可空不是因为服务端会"偶尔忘记带"：`POST /api/license/import` 在
   * **文本为空**时会在进服务层之前就 400 返回，那条路径上确实没有状态可带。
   * 消费方遇到 null 时应当保留当前显示，而不是当成"未授权"。
   */
  license: LicenseDto | null
}

/** 「授权申请文本」：发给供应方，对方据此签发绑定本机的授权。 */
export interface LicenseRequestDto {
  machineCode: string
  machineName: string
  requestedAt: string
  /** 整段申请文本（可直接复制发送）。 */
  text: string
}

// ═══════════════════════════════════════════════════════════════
// 客户端主机（M4）
//
// 与 `ControlServer.Contracts.AgentDtos.cs` 一一对应。
// ═══════════════════════════════════════════════════════════════

/**
 * 客户端在线状态的**字符串常量**（与服务端 `AgentStates` 同值）。
 *
 * ⚠️ 服务端刻意用字符串而不是 enum：这些值要经 SignalR 走 JSON，
 * enum 会被序列化成数字 —— 一旦两边对枚举顺序的理解有偏差，
 * 界面就会把"离线"画成"在线"。
 */
export const AGENT_STATES = {
  online: 'online',
  unstable: 'unstable',
  offline: 'offline',
  never: 'never',
  blocked: 'blocked',
} as const

export type AgentState = (typeof AGENT_STATES)[keyof typeof AGENT_STATES]

/** 客户端的一块网卡（注册时上报）。 */
export interface AgentNetworkInterfaceDto {
  name: string
  address: string
  mac: string
  isUp: boolean
}

/**
 * 客户端主机信息（注册时上报）。
 *
 * ⚠️ `logicalProcessors` / `totalMemoryMb` / `cpuName` 都可能为 **null**（取不到），
 * 不要用 0 兜底 —— "0 核 CPU" 会被当成真数据展示出去。
 */
export interface AgentHostInfoDto {
  machineKey: string
  hostName: string
  osDescription: string
  cpuName: string | null
  logicalProcessors: number | null
  totalMemoryMb: number | null
  agentVersion: string
  userName: string | null
  interfaces: AgentNetworkInterfaceDto[]
}

/** 在线表里的一台客户机。 */
export interface AgentHostDto {
  id: number
  machineKey: string
  displayName: string
  group: string | null
  note: string | null
  /** 见 `AGENT_STATES`（服务端已结合心跳时间与拉黑标记算好）。 */
  state: string
  /** 状态中文名（服务端下发，前端不硬编码对照表）。 */
  stateDisplay: string
  isBlocked: boolean
  agentVersion: string | null
  registeredAt: string
  lastSeenAt: string | null
  lastAddress: string | null
  hostName: string | null
  osDescription: string | null
  cpuName: string | null
  logicalProcessors: number | null
  totalMemoryMb: number | null
  /**
   * 最新一帧遥测摘要（M4.2）。
   *
   * ⚠️ **为 null 有两种完全不同的含义**，界面必须区分：
   *   · 从没上报过遥测（M4.2 之前注册的客户端 / 客户端关掉了遥测）；
   *   · 这台机器离线超过 2 小时。
   * 服务端只在"最近 2 小时有样本"时才填它，所以"离线很久的机器"这里是 null ——
   * 那**不是**"它所有指标都是 0"。
   */
  metrics?: AgentMetricsSummaryDto | null
  /** 服务端当前下发给该机的配置版本（M4.5）。**0 = 该机还没有配置。** */
  configVersion: number
  /**
   * 客户机回报的「已应用版本」（M4.5）。
   *
   * ⚠️ **null = 该机从来没回报过**（客户端版本较旧 / 刚注册还没应用过），
   *    与"已应用 v0"是两回事 —— 界面上必须分开说，不能一律当成 0 再比大小。
   */
  appliedConfigVersion: number | null
  /**
   * 上次给这台机器**下发文件**时用过的落脚目录（M4.7）。
   *
   * ⚠️ **null = 从没记过**（这台机器还没被下发过）。
   *    文件页据此预填"存放路径"，省得每次都手选。
   *
   * 🔥 2026-10-15 起**空串几乎不会出现**了：从前"建一次存放路径留空的任务"会把记忆
   *    写成空串（一次留空 = 把记住的目录删掉），于是"保存了路径却怎么都不生效"。
   *    现在只有**确实指定了目录**才会回写，留空什么都不动。
   */
  lastTargetDir: string | null
  /**
   * 上次从这台机器**取回文件**时读的那个目录（M4.7）。语义同 {@link lastTargetDir}。
   * 回传方向用它预填"源目录"。
   */
  lastSourceDir: string | null
  /**
   * 客户机**自己报上来的** UDP 指令通道实时态（2026-10-14）。
   *
   * ⚠️ **null ≠ 未监听**，它表示「这台机器还没告诉我」：
   *   · 客户端版本较旧（不会走 `ReportUdpChannel` 那条上报）；
   *   · 页面打开时中心刚重启，还没收到该机的第一次上报。
   * 前者要做的是"升级客户端"，后者要做的是"等一个心跳周期" ——
   * 一律显示成"未监听"会把这两条完全不同的路都指错。
   */
  udpChannel?: AgentUdpChannelStateDto | null
}

/**
 * 客户机此刻的 UDP 指令通道状态（随心跳周期上报，2026-10-14）。
 *
 * **它解决什么**：中心要判"这台机器能不能收 UDP 指令"，此前只能看"我下发过的配置里写着
 * 启用没有" —— 而配置写着启用**不等于它在听**（本机可能停用了任务下发、端口可能绑不上、
 * 配置可能还没应用）。于是"中心说已发出、机器毫无反应"成了最难查的一类现场问题。
 */
export interface AgentUdpChannelStateDto {
  /** 此刻是否真的绑定了端口在听（`netstat` 里能看见这个端口）。 */
  listening: boolean
  /** 正在听的端口；没在听时为 null。**代发指令用的就是它**，不是配置里的端口。 */
  port: number | null
  /** 客户机自己给的人话状态（没在听时带原因）。 */
  stateText: string | null
  /** 收到了会不会真的执行。false = **演练模式**（只识别不动手）。 */
  executeEnabled: boolean
}

// ⚠️ **显卡字段不在在线表里**（`gpuName` / `gpuCount` 于 2026-10-13 短暂存在后移除）。
// 显卡只从**硬件清单**（`GET /api/agents/{id}/inventory`）读，展示在详情页的「硬件清单」里 ——
// 在线表每 60 秒重建，为一格型号维护"清单 → 内存摘要 → 水合"整条链路不划算。

/** 在线表 + 计数（计数由服务端算好，前端不必自己 filter 一遍）。 */
export interface AgentListDto {
  items: AgentHostDto[]
  total: number
  online: number
  unstable: number
  offline: number
}

/** 单台详情：列表项 + 完整主机信息 + 此刻是否握着长连接。 */
export interface AgentDetailDto {
  host: AgentHostDto
  info: AgentHostInfoDto | null
  connected: boolean
}

/**
 * 改名 / 拉黑 / 移除 / 唤醒 / 关机 / 重启等写操作的结果。
 *
 * 与 `UserMutationResult` 同思路：**无论成败都带回最新全量列表**，
 * 失败时前端直接用它刷新界面即可。
 */
export interface AgentMutationResult {
  ok: boolean
  status: string
  message: string
  list: AgentListDto | null
}

/** 客户端安装包元信息（顶栏下载按钮据此决定"能不能点 / 要不要给黄点"）。 */
export interface AgentInstallerInfoDto {
  available: boolean
  fileName: string | null
  sizeBytes: number
  /** 从文件名解析出的客户端版本；解析不到为 null。 */
  version: string | null
  serverVersion: string
  /** 主次版本与服务端不一致（提示而非阻断）。 */
  versionMismatch: boolean
  /** 不可用时的原因 / 指引（例如"请把安装包放到 xxx 目录"）。 */
  hint: string | null
}

// ═══════════════════════════════════════════════════════════════
// 客户机配置（M4.5 · BootAssistant 对齐）
//
// 与 `ControlServer.Contracts.AgentConfigDtos.cs` 一一对应。
//
// 🔥 **事实来源是服务端**：这份配置由服务端保存并按版本号下发，客户机本地只落一份
//    **只读缓存**（中心不可达时降级运行）。所以前端在这里做的就是"改服务端那份"，
//    没有任何"本地覆盖"入口 —— 那会制造"网页显示一份、机器实际跑另一份"。
//
// ⚠️ **三张任务清单的划分照搬参考项目**，其中「循环发送」**不是独立一张表**：
//    它就是发送任务上的 `isLoop` 标志。界面上也照此呈现（一个开关，而不是两个列表）。
//
// ⚠️ **校验规则不在前端**：允许/拒绝的判据只有一份，在 Core 的 `AgentConfigRules` 里
//    （服务端保存前与客户端应用前共用它）。前端只负责"把用户填的东西原样送上去"，
//    然后把服务端返回的问题清单**原样显示出来** —— 各写一份判据的下场是
//    "服务端放行、客户端拒绝"，表现为"保存成功但机器没动"。
// ═══════════════════════════════════════════════════════════════

/**
 * 客户机上的 UDP 指令通道（M4.5）。
 *
 * **它是本项目里唯一一个客户机主动开着的入站端口。**
 *
 * 🔥 2026-10-13 起是**明文 + 来源 IP 黑名单**（此前是 HMAC 验签 + 白名单）。
 * 报文就是一行纯文本：`DOWN` / `RESTART` / `+` / `-` / `0` / `1`，**任意来源都能发**，
 * 只有被点名拉黑的主机发不进来。
 *
 * ⚠️ 这是主动降低安全强度的取舍：**同网段任意主机都能把设备关掉**。
 * 所以这条通道的端口不能暴露到不可信网络。
 */
export interface AgentUdpChannelDto {
  /** 关闭时客户端**完全不绑定该端口**（不是"绑定了再丢弃"）。 */
  enabled: boolean
  port: number
  /**
   * 来源 IP **黑名单**：**在名单里的来源 IP 发来的指令一律丢弃**。
   *
   * **空 = 不拉黑任何来源**（默认，也就是"任意来源皆可"）。
   * 按 IP 精确比较，不支持网段 —— 它是"防误触 / 防一台乱发的机器"，**不是安全边界**：
   * 伪造源 IP 的 UDP 报文能绕过它。
   */
  blockedSourceIps: string[] | null
}

/**
 * 一条「发送」任务：延时发一次，或（`isLoop` 时）循环发。
 *
 * 🔥 `isLoop` 即「循环发送」—— **不是另一张表**（参考项目的数据模型就是这样，
 * 早期方案文档把它写成独立一栏是笔误）。
 */
export interface AgentSendTaskDto {
  /** 稳定标识（前端生成）。改配置后要能认出"还是那条任务"。 */
  id: string
  name: string | null
  protocol: string
  targetIp: string
  targetPort: number
  dataType: string
  data: string
  /** 首次发送前的延时秒数（**相对于客户机 Agent 服务启动**）。 */
  delaySeconds: number
  isLoop: boolean
  /** 循环间隔秒数（仅 `isLoop` 时有意义）。 */
  loopIntervalSeconds: number
  enabled: boolean
}

/**
 * 一条「定时发送」任务：每天在若干个 `HH:mm` 时刻各发一次。
 *
 * ⚠️ 时刻是**列表**。本项目刻意不保留参考项目那个"单时间"兼容字段
 * （新表不带历史包袱，旧配置的迁移由导入端负责）。
 */
export interface AgentScheduledTaskDto {
  id: string
  name: string | null
  protocol: string
  targetIp: string
  targetPort: number
  dataType: string
  data: string
  /** `HH:mm`（24 小时制）。为空 = 该任务不触发。 */
  times: string[] | null
  enabled: boolean
}

/**
 * 一条「延时启动程序」任务（M4.5）。
 *
 * 🔥 **执行在会话侧（托盘进程），不是服务侧**：服务在 Session 0 里 `Process.Start`
 * 出来的窗口用户**根本看不见**（那里没有桌面），表现为"程序启动了但没反应"。
 *
 * 🔥 **多条任务是串行累加延时**：第一条相对于客户机启动，后续相对于前一条完成 ——
 * 现场就是靠这个顺序把多个程序依次拉起来的。所以**列表顺序是有意义的**。
 */
export interface AgentLaunchTaskDto {
  id: string
  name: string
  programPath: string
  argument: string
  delaySeconds: number
  enabled: boolean
}

/** 下发给某台客户机的完整配置（整包下发、整包替换，不做差量）。 */
export interface AgentConfigDto {
  /** 版本号**单调递增**；**0 = 该机还没有配置**。 */
  version: number
  /** 最后保存时间。**null = 还没有配置**（不拿"当前时间"凑 —— 那会让人以为刚配过）。 */
  updatedAt: string | null
  udp: AgentUdpChannelDto
  sendTasks: AgentSendTaskDto[]
  scheduledTasks: AgentScheduledTaskDto[]
  launchTasks: AgentLaunchTaskDto[]
}

/**
 * 保存配置请求（`PUT /api/agents/{id}/config`）。
 *
 * 🔥 **null = 不动那一块；空数组 = 清空那一块** —— 两件完全不同的事。
 * 本面板一次提交全部四块（它在编辑全部），但类型上保留这个可空语义。
 */
export interface AgentConfigSaveRequest {
  udp?: AgentUdpChannelDto | null
  sendTasks?: AgentSendTaskDto[] | null
  scheduledTasks?: AgentScheduledTaskDto[] | null
  launchTasks?: AgentLaunchTaskDto[] | null
}

/**
 * 保存配置的结果。
 *
 * ⚠️ 校验不过时服务端返回 **400**（不是 200 + `ok:false`），
 * 且 `message` 里是**全部问题**用「；」连起来的结果 ——
 * 一次改十条任务却只报第一条，会逼用户"改一轮、试一轮"。
 */
export interface AgentConfigSaveResult {
  ok: boolean
  status: string
  message: string | null
  /** 保存成功后的配置（含**新版本号**）；失败时为 null。 */
  config: AgentConfigDto | null
}

/** 发送任务可选的传输协议（与 `TransportProtocols` 同值）。 */
export const AGENT_SEND_PROTOCOLS = ['UDP', 'TCP'] as const

/**
 * 发送任务可选的数据类型（与 `DataTypes` 同值）。
 *
 * ⚠️ **没有「指令库」**：指令库定义在服务端，客户机拿不到，也就发不出字节。
 * 所以客户机配置里的数据类型只能是这四个。
 */
export const AGENT_DATA_TYPES = [
  { value: 'String', label: 'String（UTF-8 文本）' },
  { value: 'Hex', label: 'Hex（十六进制）' },
  { value: 'ANSI', label: 'ANSI（本机代码页 GBK）' },
  { value: 'ASCII', label: 'ASCII（非 ASCII 字符替换为 ?）' },
] as const

/**
 * 配置的边界值（与 Core 的 `AgentConfigDefaults` 同值）。
 *
 * ⚠️ **只用于 input 的 `min`/`max` 提示**（让用户不至于填一个必然被拒的数），
 * **不是校验**：真正的判据在服务端（`AgentConfigRules`），
 * 前端不复制一份判据 —— 那正是"服务端放行、客户端拒绝"的来源。
 * 这些数字如果在服务端被调整，这里只是提示变得不准，不会放行任何非法配置。
 */
export const AGENT_CONFIG_LIMITS = {
  minUdpPort: 1024,
  maxUdpPort: 65535,
  maxDelaySeconds: 86400,
  minLoopIntervalSeconds: 1,
  maxTasksPerList: 500,
} as const

/**
 * 「已应用版本」与「已下发版本」的对照结论 —— 决定版本条上那个徽章长什么样。
 *
 * 四种情形的语义差别很大，**不能压成"相等/不等"两个分支**：
 *   · `pending`：确实还没应用上（**这是唯一需要运维动手看的情形**）；
 *   · `never`：这台机器从来没回报过 —— 通常是客户端版本较旧，不是"没生效"；
 *   · `synced`：一致；
 *   · `ahead`：已应用版本比服务端版本还高 —— 理论上不该出现，如实显示而不是掩盖。
 */
export type AgentConfigVersionState = 'synced' | 'pending' | 'never' | 'ahead'

export function agentConfigVersionState(
  configVersion: number,
  appliedVersion: number | null,
): AgentConfigVersionState {
  if (appliedVersion === null) return 'never'
  if (appliedVersion > configVersion) return 'ahead'
  return appliedVersion === configVersion ? 'synced' : 'pending'
}

/** 版本条徽章的样式类（`synced` 用中性 —— 一切正常时不该有彩色徽章在喊）。 */
export function agentConfigVersionClass(state: AgentConfigVersionState): string {
  switch (state) {
    case 'pending':
      return 'warn'
    case 'never':
      return ''
    case 'ahead':
      return 'err'
    default:
      return 'ok'
  }
}

/** 版本条徽章的文案。 */
export function agentConfigVersionLabel(state: AgentConfigVersionState): string {
  switch (state) {
    case 'pending':
      return '尚未应用'
    case 'never':
      return '未回报过'
    case 'ahead':
      return '版本异常'
    default:
      return '已生效'
  }
}

// ═══════════════════════════════════════════════════════════════
// 客户端遥测（M4.2）
//
// 与 `ControlServer.Contracts.AgentTelemetryDtos.cs` 一一对应。
//
// ⚠️ **贯穿全篇的一条约定：所有数值都可能为 null，且 null ≠ 0。**
//    "CPU 0%" 和 "CPU 查不到" 在页面上是"一切正常"与"采集坏了"两件相反的事。
//    所以每一个数字在渲染前都必须先判 null，绝不写 `v ?? 0`。
// ═══════════════════════════════════════════════════════════════

/** 一台机器"最新一帧"的指标摘要（列表页实时列 / 详情页顶部用）。 */
export interface AgentMetricsSummaryDto {
  /** 这一帧的时刻；null = 没有任何有效数据。 */
  at: string | null
  cpuPct: number | null
  /** 内存占用率（服务端由 used/total 算好，前端不重复算）。 */
  memPct: number | null
  memUsedMb: number | null
  memTotalMb: number | null
  /** 字节/秒。首次采样后才有值（需要两次采样求差）；**不是** 0。 */
  diskReadBps: number | null
  diskWriteBps: number | null
  netUpBps: number | null
  netDownBps: number | null
  gpuPct: number | null
  uptimeSeconds: number | null
  /**
   * 温度（℃）。
   *
   * 🔥 **null = 没有读数**（未启用温度监控 / 驱动加载失败 / 该硬件没有传感器），
   * **不是 0**。页面上必须显示「未启用」或「—」，绝不能渲染成 `0 ℃` ——
   * 那会被读成"机器很凉快"，与事实恰好相反。
   *
   * 具体是"未启用"还是"驱动失败"，看 `AgentInventoryDto.thermalState`。
   */
  cpuTempC: number | null
  gpuTempC: number | null
  storageTempC: number | null
  /**
   * 这一帧的**逐设备读数**（2026-10-13）。
   *
   * 🔥 详情页的曲线是这样长出来的：打开时整段拉一次历史，之后每来一帧摘要就"往前挪一格"。
   * 带上它，硬件选择器上的「当前值」和硬件曲线才会跟着动；
   * 不带的话它们会永远停在**打开页面那一刻**（整机在飙，切过去看某块盘却一动不动）。
   *
   * `null` / 空 = 这台客户机不上报逐设备明细（老客户端），页面按"该时段没有逐设备数据"处理。
   */
  devices: AgentDeviceReadingDto[] | null
}

/**
 * 逐设备指标的设备种类（与服务端 `AgentDeviceKinds` 同值）。
 *
 * 这些值**会落库**（`agent_metrics.DevicesJson`），改名等于要写数据迁移 —— 别在这里"顺手统一命名"。
 */
export const AGENT_DEVICE_KINDS = ['cpu', 'disk', 'net', 'gpu'] as const

export type AgentDeviceKind = (typeof AGENT_DEVICE_KINDS)[number]

/** 设备种类的中文名（仅展示用）。 */
export const AGENT_DEVICE_KIND_LABELS: Record<AgentDeviceKind, string> = {
  cpu: '处理器',
  disk: '磁盘',
  net: '网卡',
  gpu: '显卡',
}

/**
 * 一块硬件在某一刻的读数。
 *
 * ⚠️ **每种设备真正会填的字段只有一两个**（磁盘只填读写、网卡只填上下行），
 * 其余保持 null —— 别把它们渲染成 0。
 *
 * 🔥 `key` 是**稳定标识**，曲线历史靠它接续（`cpu0` / `disk0` / 网卡 GUID / `luid_0x…`）。
 * **绝不要拿 `name` 当身份**：改个名字两条曲线就断成两截了。
 */
export interface AgentDeviceReadingDto {
  kind: AgentDeviceKind | string
  key: string
  /** 展示名（型号优先）。服务端填不出来时退回 `物理磁盘 0` 这类能辨认的名字，但**绝不编造型号**。 */
  name: string
  cpuPct: number | null
  readBps: number | null
  writeBps: number | null
  upBps: number | null
  downBps: number | null
  gpuPct: number | null
}

/** 时序图上的一个点。 */
export interface AgentMetricsPointDto {
  at: string
  cpuPct: number | null
  memPct: number | null
  memUsedMb: number | null
  memTotalMb: number | null
  diskReadBps: number | null
  diskWriteBps: number | null
  netUpBps: number | null
  netDownBps: number | null
  gpuPct: number | null
  uptimeSeconds: number | null
  /** 温度（℃）；null = 这个点没有读数（曲线在那里断开，不补 0）。 */
  cpuTempC: number | null
  gpuTempC: number | null
  storageTempC: number | null
  /**
   * 该时刻的逐设备读数。
   *
   * 🔥 **null = 这段时间只有整机聚合**（日聚合层不聚合逐设备，老客户端也不上报）。
   * 页面据此提示"该时段逐设备曲线不可用"，而**不是**画一条空线 ——
   * 空线看起来就像"这块硬盘一直 0 字节/秒"。
   */
  devices: AgentDeviceReadingDto[] | null
  /**
   * **这个点不是一次采样，而是一个「这里没有数据」的断点标记**（2026-10-16 补）。
   *
   * 🔥 它只出现在**两段真实数据之间**（`at` = 空白开始的那个桶边界，所有指标一律 null）。
   * 空桶在服务端聚合时就被丢掉了，于是"客户端离线两小时"在数据里只是
   * **前后两个点相隔两小时** —— 折线会把这**两点直接连起来**，那是凭空编了一段数据。
   * 有标记点之后，折线在它那里自然断开（本项目的"**null 必须断开折线**"，
   * 见 `MetricsChart` 文件头），整机曲线与逐设备曲线同时留空。
   *
   * ⚠️ 它同时是那一格"是不是空白"的**唯一出处**：别用"所有值都是 null"去反推 ——
   * 一帧真的采到了、只是每个计数器都没读到，那是另一件事。
   */
  isGap?: boolean
}

/**
 * 硬件温度监控的状态（与服务端 `AgentThermalStates` 同值）。
 *
 * 这些值**会落库**（在清单 JSON 里），改名等于要处理历史数据 —— 别在这里"顺手统一命名"。
 */
export const THERMAL_STATES = {
  /** 已启用，且**拿到了至少一项**读数。
   *  ⚠️ 不等于"驱动装上了" —— 显卡温度走用户态 NVAPI，所以可能 CPU/硬盘是 null 而它是 ok。 */
  ok: 'ok',
  /** 未启用（安装时未勾选 / 配置关掉）。**这是默认值，也是正常状态。** */
  disabled: 'disabled',
  /** 已启用但驱动加载失败（被安全软件拦 / 无权限 / 系统不支持）。 */
  failed: 'failed',
} as const

export type ThermalState = (typeof THERMAL_STATES)[keyof typeof THERMAL_STATES]

/** 热状态的中文显示名。 */
export function thermalStateLabel(state: string | null | undefined): string {
  switch (state) {
    case THERMAL_STATES.ok:
      return '已启用'
    case THERMAL_STATES.disabled:
      return '未启用'
    case THERMAL_STATES.failed:
      return '驱动加载失败'
    default:
      return '未知'
  }
}

/**
 * 一段时序查询的结果。
 *
 * ⚠️ `source` 必须显示在界面上：
 *   · `raw`         —— 5 秒明细（可降采样成桶均值）；
 *   · `daily+raw`   —— **老的那一段是日聚合**，相邻两点可能隔了一整天；
 *   · `disabled`    —— 服务端把遥测关掉了（`Server:Agent:Telemetry:Enabled=false`）。
 * 把 `daily` 画成一条平滑曲线是最容易骗人的表现：用户以为"CPU 一直 40% 很稳"，
 * 其实那是 90 个"当天均值"连起来的。
 */
export interface AgentMetricsSeriesDto {
  agentId: number
  from: string
  to: string
  /** > 0 表示每个点是该桶内的均值（桶宽秒数）。 */
  bucketSeconds: number
  source: string
  /** 聚合前命中的原始样本行数。 */
  rawCount: number
  points: AgentMetricsPointDto[]
  /**
   * 「多久没有采样就算客户端离线（曲线留空）」的判据（秒，2026-10-16 补）。
   *
   * = `max(离线判定阈值, 2 × 分桶粒度)`，由服务端算好下发：详情页的曲线是
   * "打开时拉一段历史、之后每来一帧就往右挪一格"，挪格那一步同样会遇到
   * "上一格是十分钟前"——**两处判据必须同源**，否则"刷新一下就好了"会变成一种随机行为。
   *
   * 0 / 缺省 = 服务端没算（老服务端），按"不插断点"处理。
   */
  gapThresholdSeconds?: number
}

/**
 * 开关机 / 异常事件的种类常量（与服务端 `AgentPowerEventKinds` 同值）。
 *
 * 这些值**会落库**，改名等于要写数据迁移 —— 别在这里"顺手统一命名"。
 */
export const POWER_EVENT_KINDS = {
  boot: 'boot',
  shutdown: 'shutdown',
  restart: 'restart',
  unexpected: 'unexpected',
  bsod: 'bsod',
  sleep: 'sleep',
  wake: 'wake',
} as const

export type PowerEventKind = (typeof POWER_EVENT_KINDS)[keyof typeof POWER_EVENT_KINDS]

/** 种类中文名（与服务端 `DisplayName` 同表，前端不另立一套）。 */
export const POWER_EVENT_LABELS: Record<string, string> = {
  boot: '开机',
  shutdown: '正常关机',
  restart: '计划重启/关机',
  unexpected: '发现异常关机',
  bsod: '蓝屏',
  sleep: '进入睡眠',
  wake: '睡眠唤醒',
}

/** 是否属于"坏事"（页面上标红/标黄）。 */
export function isProblemEvent(kind: string | null | undefined): boolean {
  return kind === POWER_EVENT_KINDS.unexpected || kind === POWER_EVENT_KINDS.bsod
}

/**
 * 一条开关机 / 异常记录。
 *
 * ⚠️ `at` 是**事件日志写下这条记录的时刻**。
 *   对 6008（发现上次异常关机）来说，那一刻是**本次开机**，
 *   而不是真正的断电时刻 —— 真正的时刻只写在事件正文里，本项目刻意不解析它
 *   （正文随系统语言变化，解析失败会给出一个看着很合理的错时间）。
 */
export interface AgentPowerEventDto {
  at: string
  kind: string
  /** 原始 Windows 事件 ID（6005 / 6006 / 6008 / 41 / 1074 / 1001 / 42 / 107）。 */
  eventId: number | null
  /** 发起者（仅 1074 有）。事件日志的属性顺序不是契约，取不到就是 null。 */
  actor: string | null
  detail: string | null
}

/** 开关机记录的统计摘要（服务端算，按**全量历史**而不是当前页）。 */
export interface AgentPowerStatsDto {
  bootCount: number
  shutdownCount: number
  restartCount: number
  /** 异常关机的**次数**（6008 与 41 在同一次事故里只算一次）。 */
  unexpectedCount: number
  bsodCount: number
  /** 由"开机 → 关机"事件配对算出的平均运行时长（秒）；配不出对时为 null。 */
  averageUptimeSeconds: number | null
  lastBootAt: string | null
  lastShutdownAt: string | null
}

/** 一台机器的开关机记录列表 + 统计。 */
export interface AgentPowerEventListDto {
  agentId: number
  items: AgentPowerEventDto[]
  /** 全量历史条数（不是 `items.length`）。 */
  total: number
  stats: AgentPowerStatsDto
}

/** CPU 明细。 */
export interface AgentCpuInfoDto {
  name: string | null
  physicalCores: number | null
  logicalProcessors: number | null
  baseClockMhz: number | null
  maxClockMhz: number | null
  socket: string | null
}

/** 一根内存条（`Win32_PhysicalMemory`）。 */
export interface AgentMemoryModuleDto {
  bank: string | null
  manufacturer: string | null
  capacityMb: number | null
  speedMhz: number | null
  partNumber: string | null
}

/** 一个分区 / 卷。 */
export interface AgentVolumeDto {
  drive: string
  label: string | null
  fileSystem: string | null
  totalMb: number | null
  freeMb: number | null
}

/** 一块物理磁盘。 */
export interface AgentDiskDto {
  index: number | null
  model: string | null
  serial: string | null
  sizeGb: number | null
  mediaType: string | null
}

/** 一块显卡。 */
export interface AgentGpuDto {
  name: string | null
  driverVersion: string | null
  driverDate: string | null
  memoryMb: number | null
  /**
   * 厂商名（`AdapterCompatibility`，如 `NVIDIA` / `Intel Corporation`）。
   * 🔥 老客户端不报这一项（→ null），页面按「—」显示，**不推断**。
   */
  vendor?: string | null
  /**
   * GPU 芯片名（`VideoProcessor`）。常比 `name` 更短更准，
   * 多卡同型号时用它 + `pciId` 区分。
   */
  videoProcessor?: string | null
  /** PCI 厂商:设备号，形如 `10DE:2504`。取不到为 null。 */
  pciId?: string | null
}

/** 一台显示器。 */
export interface AgentDisplayDto {
  deviceName: string | null
  width: number | null
  height: number | null
  refreshHz: number | null
  primary: boolean
}

/** 完整硬件清单。 */
export interface AgentInventoryDto {
  capturedAt: string
  cpu: AgentCpuInfoDto | null
  totalMemoryMb: number | null
  memoryModules: AgentMemoryModuleDto[]
  volumes: AgentVolumeDto[]
  disks: AgentDiskDto[]
  gpus: AgentGpuDto[]
  displays: AgentDisplayDto[]
  osVersion: string | null
  osBuild: string | null
  osArchitecture: string | null
  domain: string | null
  bootTime: string | null
  uptimeSeconds: number | null
  loggedOnUser: string | null
  /**
   * 该客户端所在平台是否支持开关机记录。
   *
   * 由客户端自己上报（服务端不猜）：于是页面能显示"当前平台不支持"，
   * 而不是一张让人以为"这台机器从没关过机"的空表。
   */
  powerEventsSupported: boolean
  /**
   * 硬件温度监控的状态（M4.3），取值见 `THERMAL_STATES`。
   *
   * 🔥 **放在清单里而不是每个采样点里**：这个状态是**机器级**的（驱动在进程启动时
   * 加载一次，之后不再变化），塞进 5 秒一条的指标里等于每秒重复一个恒定值。
   *
   * null = 老客户端还没上报过这个字段，页面按"未知"处理。
   */
  thermalState: string | null
  /**
   * 温度不可用时的人话原因（可选）。
   *
   * `failed` 时是失败原因、`disabled` 时是未启用原因 —— 原样显示出来，
   * 否则运维只能对着"驱动加载失败"六个字猜。
   */
  thermalNote: string | null
  /**
   * 本机允许被**下发写入**的目录白名单（M4.4）。
   *
   * 由客户端自己上报 —— 服务端不知道对方磁盘长什么样。分发页用它给"目标目录"提候选，
   * 于是运维是**从这台机器允许的目录里选一个**，而不是手输一个路径再去撞客户机的白名单。
   *
   * ⚠️ 这只是"能选什么"的提示，**不是安全边界**：真正的拦截在客户机落地前
   * （越界会被拒绝并回报 `failed`）。
   *
   * - `null` = 老客户端没这个字段（页面按"未上报"处理，退回手输）
   * - `[]`   = 本机**未启用文件传输**
   */
  transferInboxDirectories: string[] | null
  /**
   * 本机可访问的**根目录（盘符）**，用作回传"源路径"输入框的候选提示（M4.4 起）。
   *
   * 🔥 **2026-10-15 起它不是白名单、也不构成任何限制**：客户机对回传源路径
   * **不限制目录**（用户拍板），原来那个可收窄的 `Agent:Transfer:OutboxDirectories`
   * 已连同机制一并删除。字段名沿用是为了不动线协议与预填逻辑。
   *
   * 仍然有用：回传页拿它当"这台机器上有哪几个盘"的提示（与「浏览」顶层同一集合），
   * 免得运维盲填一个不存在的盘符。填候选之外的路径照样能提交。
   *
   * 语义同 `transferInboxDirectories`（`null` / `[]` 分别代表"未上报"与"未启用"）。
   */
  transferOutboxDirectories: string[] | null
}

/**
 * 清单查询的结果。
 *
 * `capturedAt` 为 **null = 从未上报过**（不是"采到了空清单"）——
 * 与"上报过但 JSON 损坏"（`capturedAt` 有值、`inventory` 为 null）是两件事。
 */
export interface AgentInventoryEnvelopeDto {
  agentId: number
  capturedAt: string | null
  inventory: AgentInventoryDto | null
}

/** 目录浏览的结果状态（`GET /api/agents/{id}/browse`）。 */
export const AGENT_BROWSE_STATUSES = {
  ok: 'ok',
  /** 那台机器没启用文件传输。 */
  disabled: 'disabled',
  /**
   * 路径不在那台机器的可读白名单内。
   *
   * ⚠️ 新版客户机（整盘浏览）**不再产生它**；保留是因为现场还装着旧版客户端，
   * 它们依然会回这个值 —— 页面要照常显示一句人话，而不是当成未知状态。
   */
  denied: 'denied',
  /** 目录不存在、无权访问，或路径本身非法。 */
  notfound: 'notfound',
  /** 是文件不是目录。 */
  notdir: 'notdir',
  /** 客户端版本过旧、还没支持目录浏览。 */
  unsupported: 'unsupported',
  /** 那台机器当前不在线。 */
  offline: 'offline',
  /** 那台机器没在规定时限内回话。 */
  timeout: 'timeout',
  error: 'error',
} as const

/** 客户机上的一个目录条目。 */
export interface AgentBrowseEntryDto {
  /** 显示名。顶层（盘符）是带卷标与类型的完整路径，例如 `C:\（系统）（固定）`。 */
  name: string
  /** 完整路径：目录点它就能进去，文件点它就是选中它。 */
  path: string
  isDirectory: boolean
  /** 文件字节数；目录为 0。 */
  size: number
  /** 最后修改时间（北京时间）；读不到属性时为 null。 */
  modifiedAt: string | null
}

/**
 * 一次目录浏览的结果。
 *
 * 🔥 **失败也用 `ok:false` 表达，服务端回的是 200** —— 因为这里的失败几乎都是
 * 预期内的业务结果（机器离线、路径不存在、客户端太旧），页面要把它们
 * **就地显示在下拉框旁边**，而不是弹一个"系统出错"的提示。
 */
export interface AgentBrowseResultDto {
  ok: boolean
  status: string
  /** 失败原因（人话，服务端/客户机给）。 */
  message: string | null
  /** 客户机实际列出的目录；顶层为 null。 */
  requestedPath: string | null
  /**
   * 上一级目录；**null = 已经在盘根（如 `C:\`）上，没有上一级了** ——
   * 此时"返回上级"应回到盘符列表，而不是给一个点了没反应的按钮。
   */
  parentPath: string | null
  /** 客户机上的路径分隔符（Windows `\` / Linux `/`）。 */
  separator: string | null
  /** 该机的**盘符全集**（供"回此电脑"）。 */
  roots: string[] | null
  /** 当前目录下的条目（目录在前、文件在后）。 */
  entries: AgentBrowseEntryDto[] | null
  /** true = 条目被截断了 —— 必须显式告诉用户，否则会以为"文件不存在"。 */
  truncated: boolean
}

/**
 * 「接收到的数据」里那一条是**从哪条链路**来的（`GET /api/agents/{id}/received`）。
 */
export const AGENT_RECEIVED_KINDS = {
  /** 局域网里直接发给这台客户机的明文指令（UDP 通道）。 */
  udp: 'udp',
  /** 中心服务端经长连接下发的指令 / 任务 / 配置变更。 */
  signalr: 'signalr',
} as const

/**
 * 一条接收记录的**结论**。
 *
 * ⚠️ 分得细是有意的：`unrecognized` 与 `blocked` 在日志里都只是"没有反应"，
 * 但一个是"报文不对"、一个是"这台机器被拉黑了"，处置完全不同。
 */
export const AGENT_RECEIVED_OUTCOMES = {
  /** 认出来了，而且确实执行成功。 */
  executed: 'executed',
  /** 认出来了，执行了但失败（原因在 `detail`）。 */
  failed: 'failed',
  /** 认出来了但**故意没执行**（演练模式 / 本机不处理这条）。 */
  ignored: 'ignored',
  /** **报文认不出来** —— 现场最常见的坑（多发一个空格就会这样）。 */
  unrecognized: 'unrecognized',
  /** 来源 IP 在黑名单里，解析之前就被丢了。 */
  blocked: 'blocked',
  /** 中心的 SignalR 消息已受理。 */
  accepted: 'accepted',
} as const

/** 「接收到的数据」的结果状态（取值与 `AGENT_BROWSE_STATUSES` 刻意一致）。 */
export const AGENT_RECEIVED_STATUSES = {
  ok: 'ok',
  /** 客户端版本太旧、还没支持回看接收记录。 */
  unsupported: 'unsupported',
  /** 那台机器当前不在线。 */
  offline: 'offline',
  /** 那台机器没在规定时限内回话。 */
  timeout: 'timeout',
  error: 'error',
} as const

/** 客户机收到的一条东西。 */
export interface AgentReceivedEntryDto {
  /** 收到的时刻（北京时间）。 */
  at: string
  /** 见 `AGENT_RECEIVED_KINDS`。 */
  kind: string
  /** 来源的人话描述：UDP 是 `192.168.1.7:53421`，SignalR 是「中心服务端」。 */
  source: string
  /** **原样的报文文本**（空格、不可见字符都留着 —— 那正是要排查的东西）。 */
  raw: string
  /** 见 `AGENT_RECEIVED_OUTCOMES`。 */
  outcome: string
  /** 人话补充（失败原因 / 执行结果）。 */
  detail: string | null
}

/**
 * 客户机「接收到的数据」快照（`GET /api/agents/{id}/received`）。
 *
 * 🔥 **它不只有条目列表**：前面那几项"通道状态"才是回答"为什么一条都没收到"的关键 ——
 * 一个空列表配上一句「UDP 通道未启动：中心下发的配置没有启用它」，
 * 比一百条日志都有用。
 */
export interface AgentReceivedDataDto {
  ok: boolean
  status: string
  /** 失败原因（人话）。 */
  message: string | null
  /**
   * 此刻 UDP 通道**是否真的绑定了端口在听**。
   *
   * 这是"什么都没收到"的头号分水岭：为 false 时报文在网卡那一步就被丢了，
   * 客户机**不可能**有记录 —— 该去查配置，而不是查防火墙。
   */
  udpListening: boolean
  /** 正在听的端口；没在听则为 null。 */
  udpPort: number | null
  /** 通道状态的人话解释（没在听时说清是哪一个原因）。 */
  udpStateText: string | null
  /** 是否真的会执行收到的 UDP 指令。false = **演练模式**。 */
  udpExecuteEnabled: boolean
  /** 最近的条目（**新的在前**）。 */
  entries: AgentReceivedEntryDto[] | null
  /** 本进程启动以来收到的总数（**含已被挤出环形缓冲的**）。 */
  receivedCount: number
  /** 本进程启动的时刻。 */
  startedAt: string | null
}

/**
 * 一条"这台机器此刻怎么样"的实时推送（`AgentTelemetryUpdated` 的载荷）。
 *
 * **只带最新一帧，不带历史** —— 历史曲线由前端按需拉一次
 * `GET /api/agents/{id}/metrics`，之后靠本事件"往前挪一格"。
 * 把历史塞进推送会让每条消息涨到几十 KB，而 300 台时那是每秒几 MB 的广播。
 */
export interface AgentLiveMetricsDto {
  agentId: number
  metrics: AgentMetricsSummaryDto | null
  /** 服务端广播这一刻（前端据此判断"这帧是不是新的"，防止乱序覆盖）。 */
  at: string
}

/**
 * 时序图上的一条曲线定义（`MetricsChart.vue` 的入参）。
 *
 * 放在这里而不是组件里：`<script setup>` 里不能写 `export`，
 * 而详情页需要自己声明若干组曲线。
 */
export interface ChartSeries {
  /**
   * 取点上的哪个字段。
   *
   * ⚠️ 特意是 `string` 而不是 `keyof AgentMetricsPointDto`（2026-10-15）：
   * 逐设备曲线要把**运行时构造的槽位字段**摊进点里（`d0` / `d0r` / `d0w`…），
   * 这样"多块硬件画在同一张图"才有落脚处。取值方（`MetricsChart`）已按宽化的
   * 键去读，写错键名的症状是"这条线一直没数据"，和该组件对 null 的处理一致。
   */
  key: string
  label: string
  color: string
  /**
   * 线型（SVG `stroke-dasharray`）。不传＝实线。
   *
   * 用途：**同一种类的一台设备有两条通道**（磁盘读/写、网卡上/下）时，
   * 多台设备同图会出现"颜色不够用"——让颜色认设备、线型认通道，两条都读得出来。
   */
  dash?: string
}

// ═══════════════════════════════════════════════════════════════
// 文件仓库与分发（M4.4）
//
// 与 `ControlServer.Contracts.FileRepoDtos.cs` / `DistributionDtos.cs` 一一对应。
//
// 两条贯穿全篇的约定：
//   ① **接口只认 id，不认路径** —— 下载 / 删除 / 分发都传文件 id，服务端自己映射到受控路径。
//      （这是"路径穿越"在接口层面的根除，而不是事后再去过滤 `..`。）
//   ② **"已传多少字节"的事实来源永远是服务端** —— 上传时是那个 `.part` 文件的长度。
//      本地维护的 `uploaded` 只用来画进度条；一旦服务端回了 `receivedBytes`，
//      一律以它为准（偏移不符时服务端回 409 + 真实进度，客户端据此重新对齐）。
// ═══════════════════════════════════════════════════════════════

/** 仓库文件的类别（与服务端 `FileKinds` 同值）。**会落库**，改名等于要写数据迁移。 */
export const FILE_KINDS = {
  /** 管理员在网页上手工上传的普通文件。 */
  normal: 'normal',
  /** 客户端安装包（本轮只定义不实现 —— 现有安装包托管仍走 `downloads/`）。 */
  installer: 'installer',
  /** 客户机**回传**上来的文件（现场日志 / 截图等）。 */
  collected: 'collected',
} as const

export type FileKind = (typeof FILE_KINDS)[keyof typeof FILE_KINDS]

/** 类别中文名（与服务端 `FileKinds.DisplayName` 同一张表，前端不另立一套）。 */
export const FILE_KIND_LABELS: Record<string, string> = {
  normal: '普通文件',
  installer: '安装包',
  collected: '客户机回传',
}

export function fileKindLabel(kind: string | null | undefined): string {
  if (!kind) return '未知'
  return FILE_KIND_LABELS[kind] ?? kind
}

/** 仓库里的一个文件。 */
export interface FileRepoItemDto {
  /** **下载 / 删除 / 分发都用它**，而不是文件名。 */
  id: number
  /** 原始文件名（展示用；磁盘上的实际名由服务端生成）。 */
  name: string
  size: number
  /** 全量摘要（小写 hex）。上传时由**服务端自己算**，可直接用于校验。 */
  sha256: string
  /** 见 `FILE_KINDS`。 */
  kind: string
  /**
   * 上传者。**可空** —— 客户机回传没有"用户"，此时服务端已把 `originAgentId`
   * 解析成显示名填在这里（不必再拉一次在线表）。
   */
  uploadedBy: string | null
  uploadedAt: string
  /** 回传文件来自哪台客户机；非回传为 null。 */
  originAgentId: number | null
  note: string | null
}

/** 仓库列表（含累计占用，便于运维判断该不该清理）。 */
export interface FileRepoListDto {
  items: FileRepoItemDto[]
  total: number
  totalBytes: number
}

/** 上传会话状态（与服务端 `FileUploadStates` 同值）。 */
export const FILE_UPLOAD_STATES = {
  open: 'open',
  expired: 'expired',
} as const

/** 上传会话（创建应答 / 续传前"问收到哪了"的应答，同一个形状）。 */
export interface FileUploadSessionDto {
  uploadId: string
  fileName: string
  size: number
  kind: string
  /** **服务端已收下的字节数** —— 断线重连后从这里接着传。 */
  receivedBytes: number
  /** 服务端建议的分块大小。 */
  chunkSizeBytes: number
  state: string
}

/** 上传成功后的仓库条目摘要。 */
export interface FileUploadResultDto {
  id: number
  name: string
  size: number
  sha256: string
  kind: string
}

/** 分发方向（与服务端 `DistributionDirections` 同值）。 */
export const DISTRIBUTION_DIRECTIONS = {
  /** 下发：中心 → 客户机。 */
  push: 'push',
  /** 回传：客户机 → 中心。 */
  collect: 'collect',
} as const

export type DistributionDirection =
  (typeof DISTRIBUTION_DIRECTIONS)[keyof typeof DISTRIBUTION_DIRECTIONS]

export function distributionDirectionLabel(direction: string | null | undefined): string {
  switch (direction) {
    case DISTRIBUTION_DIRECTIONS.push:
      return '下发'
    case DISTRIBUTION_DIRECTIONS.collect:
      return '回传'
    default:
      return direction ?? ''
  }
}

/** 分发任务的总体状态（与服务端 `DistributionStates` 同值）。**会落库**。 */
export const DISTRIBUTION_STATES = {
  /** 已创建：目标里有仍在等待的（**含当时离线的机器** —— 离线不是失败，只是晚一点）。 */
  pending: 'pending',
  running: 'running',
  completed: 'completed',
  /** 部分成功、部分失败（**不是失败**：得让运维看到"哪几台好了、哪几台没好"）。 */
  partial: 'partial',
  failed: 'failed',
  canceled: 'canceled',
} as const

export type DistributionState = (typeof DISTRIBUTION_STATES)[keyof typeof DISTRIBUTION_STATES]

export function distributionStateLabel(state: string | null | undefined): string {
  switch (state) {
    case DISTRIBUTION_STATES.pending:
      return '等待中'
    case DISTRIBUTION_STATES.running:
      return '传输中'
    case DISTRIBUTION_STATES.completed:
      return '已完成'
    case DISTRIBUTION_STATES.partial:
      return '部分成功'
    case DISTRIBUTION_STATES.failed:
      return '失败'
    case DISTRIBUTION_STATES.canceled:
      return '已取消'
    default:
      return state ?? ''
  }
}

/** 状态 → 徽章样式类（`.badge` 的既有配色）。 */
export function distributionStateClass(state: string | null | undefined): string {
  switch (state) {
    case DISTRIBUTION_STATES.completed:
      return 'ok'
    case DISTRIBUTION_STATES.partial:
      return 'warn'
    case DISTRIBUTION_STATES.failed:
      return 'err'
    case DISTRIBUTION_STATES.running:
      return 'send'
    default:
      return ''
  }
}

/** 是否已终结（终态不再变化，前端可停止期待后续事件）。 */
export function isDistributionTerminal(state: string | null | undefined): boolean {
  return (
    state === DISTRIBUTION_STATES.completed ||
    state === DISTRIBUTION_STATES.partial ||
    state === DISTRIBUTION_STATES.failed ||
    state === DISTRIBUTION_STATES.canceled
  )
}

/** 单个目标（一台机器）的传输状态（与服务端 `DistributionItemStates` 同值）。 */
export const DISTRIBUTION_ITEM_STATES = {
  pending: 'pending',
  running: 'running',
  completed: 'completed',
  failed: 'failed',
  canceled: 'canceled',
} as const

export function distributionItemStateLabel(state: string | null | undefined): string {
  switch (state) {
    case DISTRIBUTION_ITEM_STATES.pending:
      return '等待中'
    case DISTRIBUTION_ITEM_STATES.running:
      return '传输中'
    case DISTRIBUTION_ITEM_STATES.completed:
      return '已完成'
    case DISTRIBUTION_ITEM_STATES.failed:
      return '失败'
    case DISTRIBUTION_ITEM_STATES.canceled:
      return '已取消'
    default:
      return state ?? ''
  }
}

export function distributionItemStateClass(state: string | null | undefined): string {
  switch (state) {
    case DISTRIBUTION_ITEM_STATES.completed:
      return 'ok'
    case DISTRIBUTION_ITEM_STATES.failed:
      return 'err'
    case DISTRIBUTION_ITEM_STATES.running:
      return 'send'
    default:
      return ''
  }
}

/** 分发任务里的一个目标（一台机器）的进度与结果。 */
export interface DistributionItemDto {
  id: number
  agentId: number
  agentName: string
  /** 那台机器**此刻**的在线状态（见 `AGENT_STATES`）。 */
  agentState: string
  state: string
  receivedBytes: number
  /** 总字节数；**回传尚未开始 / 大小未知时为 0**（0 是"还不知道"，不是"传完了"）。 */
  totalBytes: number
  /** 百分比 0–100（服务端算好）。`totalBytes` 为 0 时它是 0。 */
  progressPct: number
  /** 失败原因（人话）；非失败为 null。 */
  error: string | null
  startedAt: string | null
  completedAt: string | null
  /**
   * **这一台**的落地目录（下发任务才有；回传为 null）。
   *
   * null = 用那台机器的默认目录 —— 界面上要区分"没指定"与"指定了某个目录"。
   * 同一批下发的每台可以各不相同（2026-10-13 起）。
   */
  targetDir: string | null
  /**
   * **回传完成后**，这一台的文件落进仓库后的 id（下发任务为 null）。
   *
   * 🔥 回传任务的 `fileId` 恒为 0 —— 文件是客户机后来上传才落库的。
   * 所以"取回后要下载"必须用这个字段，别去按文件名猜（同名会下载到错的那一份）。
   *
   * null 有两种含义：还没完成，或**仓库里的那份已经被删了** ——
   * 后者要给"文件已不在仓库"，而不是一个点了 404 的下载按钮。
   */
  resultFileId: number | null
}

/** 一个分发任务的完整视图（含每个目标的明细）。 */
export interface DistributionTaskDto {
  id: number
  direction: string
  /** 下发时的仓库文件 id；回传为 0。 */
  fileId: number
  fileName: string
  fileSize: number
  state: string
  /**
   * 任务级目录：**只有全部目标共用一个目录时才有值**，每台各不相同时为 null。
   *
   * ⚠️ 它只是列表页的一行摘要；真正的落点看每条明细的 `targetDir`。
   */
  targetDir: string | null
  /** 回传的源路径；下发为 null。 */
  sourcePath: string | null
  note: string | null
  createdBy: string | null
  createdAt: string
  total: number
  succeeded: number
  failed: number
  items: DistributionItemDto[]
}

export interface DistributionListDto {
  items: DistributionTaskDto[]
  total: number
}

/**
 * 一条传输进度推送（`TransferProgress` 的载荷，服务端 → 网页）。
 *
 * ⚠️ **只带单个目标的变化，不带任务级状态** —— 所以前端收到后要自己重算任务汇总，
 * 否则任务卡片上的"成功 2/5"会停在建单那一刻。
 */
export interface TransferProgressDto {
  taskId: number
  itemId: number
  agentId: number
  state: string
  receivedBytes: number
  totalBytes: number
  progressPct: number
  error: string | null
}

/**
 * 由明细状态汇总出任务状态。
 *
 * ⚠️ **这是服务端 `FileTransferMath.SummarizeTaskState` 的镜像**，两边必须逐条对齐 ——
 * 服务端算的是"落库那一刻"的真相，前端算的是"事件到达那一刻"的即时显示，
 * 若优先级不同，用户会看到任务卡片在两次刷新之间来回抖。
 * 优先级：**进行中 > 等待中 > 全成功 / 全失败 / 全取消 > 部分成功**。
 */
export function summarizeDistributionState(itemStates: readonly string[]): DistributionState {
  if (itemStates.length === 0) return DISTRIBUTION_STATES.pending

  let running = 0
  let pending = 0
  let completed = 0
  let failed = 0
  let canceled = 0

  for (const state of itemStates) {
    switch (state) {
      case DISTRIBUTION_ITEM_STATES.running:
        running++
        break
      case DISTRIBUTION_ITEM_STATES.pending:
        pending++
        break
      case DISTRIBUTION_ITEM_STATES.completed:
        completed++
        break
      case DISTRIBUTION_ITEM_STATES.failed:
        failed++
        break
      case DISTRIBUTION_ITEM_STATES.canceled:
        canceled++
        break
      default:
        pending++
        break
    }
  }

  if (running > 0) return DISTRIBUTION_STATES.running
  if (pending > 0) return DISTRIBUTION_STATES.pending

  if (completed > 0 && failed === 0 && canceled === 0) return DISTRIBUTION_STATES.completed
  if (failed > 0 && completed === 0 && canceled === 0) return DISTRIBUTION_STATES.failed
  if (canceled > 0 && completed === 0 && failed === 0) return DISTRIBUTION_STATES.canceled

  return DISTRIBUTION_STATES.partial
}



// ═══════════════════════════════════════════════════════════════
// 远程桌面（M5）
//
// ⚠️ 这一节的每个常量**都必须与服务端 C# 里那份逐字一致**：
//    `RemoteSessionStates` / `RemoteDisplayModes` / `RemoteCodecs` / `RemoteErrorCodes`。
//    它们跨语言，编译器管不到，写错一个字母的表现是"页面上状态永远显示未知"
//    而两边都没有任何报错。改服务端那几个类时，**必须回来一起改**。
// ═══════════════════════════════════════════════════════════════

/**
 * 会话状态（= 服务端 `RemoteSessionStates`）。
 *
 * 🔥 `requested` 与 `streaming` **必须分开**：建会话是"让被控端开始干活"的指令，
 * 它发出去之后，被控端可能正在建 D3D 设备、也可能因为**没人登录**而根本做不到。
 * 合成一个"开启中"，现场就只能看到"点了没反应"。
 */
export const REMOTE_SESSION_STATES = {
  /** 已向被控端发出请求，尚未开始推流。 */
  requested: 'requested',
  /** 正在推流。 */
  streaming: 'streaming',
  /** 已正常结束。 */
  stopped: 'stopped',
  /** 失败（无硬编 / 无人登录 / 托盘不在）。 */
  failed: 'failed',
} as const

/** 观看端的显示模式（= 服务端 `RemoteDisplayModes`）。 */
export const REMOTE_DISPLAY_MODES = {
  /** 整帧等比缩放进视口。 */
  fit: 'fit',
  /** 按原始像素 1:1 显示，超出部分出滚动条。 */
  actual: 'actual',
} as const

/**
 * 质量档位（= 服务端 `RemoteQualityProfiles`，M5.5）。
 *
 * 🔥 **`auto` 不是"第四套数字"**：服务端的 `Resolve()` 把它解析成 `balanced` 的数，
 * 再由 governor 按实测往下调。所以页面上选「自动」与选「均衡」的**起始**效果一样，
 * 区别只在于"编不动时会不会自动降" —— 这一点必须在按钮的说明里讲清，
 * 否则用户会觉得"自动没生效"。
 *
 * ⚠️ 与服务端 `RemoteQualityProfiles` 逐字一致：写错一个字母的后果是
 * 服务端回 400「不认得的质量档位」，而页面看不出是哪一侧写错了。
 */
export const REMOTE_QUALITY_PROFILES = {
  /** 自动：从 balanced 起步，编不动就往下走。 */
  auto: 'auto',
  /** 低：弱网 / 老机器，优先"能看见"。 */
  low: 'low',
  /** 均衡：默认档。 */
  balanced: 'balanced',
  /** 高：4K/8K 与局域网充裕时用。 */
  high: 'high',
  /**
   * 流畅：60 帧（M7.4）。
   *
   * ⚠️ 前提是被控端**有硬编**：软件 JPEG 那条路被抓屏卡在约 20 fps，
   * 被控端会如实夹到 20 并在状态里显示出来 —— 切到这一档却看到 20 fps，
   * 说明那台机器没走硬编，不是档位没生效。
   */
  smooth: 'smooth',
} as const

/**
 * 编码格式的**线上表示**（= 服务端 `RemoteCodecs`）。
 *
 * ⚠️ 它是**字符串**而不是枚举，是**刻意的**：它要能表达"还不知道"（`null`），
 * 而枚举的默认值会悄悄变成某个具体格式 —— 那正是"未知被写成已知"。
 */
export const REMOTE_CODECS = {
  /** H.264 / AVC。硬编封顶 4096×4096。 */
  h264: 'h264',
  /** HEVC / H.265。硬编封顶 8192×8192，**超 4K 的唯一硬编路径**。 */
  hevc: 'hevc',
  /**
   * MJPEG（每帧一张独立 JPEG），**降级路径**。
   *
   * 🔥 **它不是正常路径，但也不是"临时脚手架"**（M7.11 更正）：它是接线正确的
   * 降级档，且在两种情况下**是唯一可行的选择** ——
   * ① **多屏拼接**：DDA 一次只对一个 output 建 duplication，拼接只能走
   * GDI + CPU 软编；② **没有硬编能力的机器**。
   *
   * ⚠️ 代价是明摆着的：NVENC / QSV 都不编 JPEG，4K 级软编单帧 40–80 ms，
   * 60 fps 连门都摸不到（M7.4 实测硬编能到 57 fps，MJPEG 卡在 11–12）。
   * ⇒ 要看细节必须走硬编（切单块屏 + 1:1），别指望把 MJPEG 调到满意。
   */
  mjpeg: 'mjpeg',
} as const

/** 一个显示适配器（能力探测结果里的一项）。 */
export interface RemoteAdapterDto {
  name: string
  vendorId: number
  deviceId: number
  dedicatedVideoMemoryBytes: number
  isSoftwareRenderer: boolean
  /** 这块适配器**确实能硬件编码**的格式（取值见 {@link REMOTE_CODECS}）。空表 = 没发现硬编能力。 */
  supportedCodecs: string[]
  /**
   * 编码引擎数。
   *
   * ⚠️ **`null`（尚未测出）与 `0`（明确无硬编）含义不同** ——
   * 页面必须把这两者画成不同的东西。把 `null` 显示成"0 个引擎"
   * 就是在 8K 路线最关键的地方说了谎。
   */
  encodeEngines: number | null
  /** 这个引擎数是怎么来的。**报数字就必须带依据**。 */
  engineBasis: string | null
}

/** 某台被控机的编码能力。 */
export interface RemoteCapabilityDto {
  /**
   * 🔥 这台机器到底报没报过能力。
   *
   * 它与"这台机器没有编码能力"是**两件事**：`false` 表示尚未上报。
   * 若不区分，页面会在"还没上报"时显示"不支持远控" —— 一个凭空的否定结论。
   */
  probed: boolean
  adapters: RemoteAdapterDto[]
  supportedCodecs: string[]
  recommendedCodec: string | null
  /** 给人看的一句话结论（直接来自被控端的探测）。 */
  summary: string
  probedAt: string | null
}

/** 中心侧的实时计数。 */
export interface RemoteSessionStatsDto {
  /** 中心收到的帧数（**含被丢掉的旧帧**，即"到过多少"）。 */
  framesReceived: number
  /** 中心收到的码流字节数（不含编码/传输开销）。 */
  bytesReceived: number
  /** 最近一段时间的**实测**帧率（不是目标值）。 */
  fps: number
  /** 最后一帧的字节数。 */
  lastFrameBytes: number
  lastFrameAt: string | null
  /**
   * 被控端累计**丢弃**的帧数（编码超时 / 超出单条消息上限）。
   *
   * ⚠️ 与 {@link framesReceived} 必须分开看：
   * 丢帧多而收到少 ⇒ 该降质量或降帧率；两者都少 ⇒ 链路本身有问题。
   */
  droppedFrames: number
  /**
   * **中心收到的音频段数**（M5.3）。
   *
   * 🔥 与 {@link framesReceived} 分开计，理由与"两个帧计数必须并排"完全一致：
   * "画面卡了"与"声音断了"在只给一个总数的时候长得一模一样，而处置相反。
   * ⚠️ 它还要与 {@link RemoteSessionAgentStatsDto.audioFramesSent} 摆在一起看。
   */
  audioFramesReceived: number
  /** 中心收到的音频码流字节数（M5.3）。 */
  audioBytesReceived: number
}

/**
 * **被控端自己报**的发送计数（与 {@link RemoteSessionStatsDto} 是两边的东西）。
 *
 * 🔥 两个数摆在一起才是这条链路上最有用的一句诊断：
 * - 它发 500 / 我们收 12 ⇒ **链路上在掉**（Hub / 网络 / 中心侧丢帧）；
 * - 它发 3 / 我们收 3 ⇒ 链路是好的，**是被控端编不动**（CPU / 编码器 / 抓屏慢）。
 *
 * 只有其中一个数的时候，这两种故障长得**一模一样**（都是"画面很卡"），
 * 而它们该做的处置完全相反。
 */
export interface RemoteSessionAgentStatsDto {
  framesSent: number
  bytesSent: number
  fps: number
  /** 被控端**发出**的音频段数（M5.3）。 */
  audioFramesSent: number
  /** 被控端发出的音频码流字节数（M5.3）。 */
  audioBytesSent: number
  /**
   * 被控端累计丢弃的音频段数（M5.3；正常恒为 0）。
   *
   * 🔥 音频的丢与视频的丢不是同一个量级的事：视频丢一帧只是抖一下，
   * 音频丢一段就是**一个可听见的断点** —— 所以它一旦非 0 必须显眼地说出来。
   */
  audioDropped: number
}

/** 一路远程桌面会话。 */
export interface RemoteSessionDto {
  sessionId: string
  agentId: number
  /** 取值见 {@link REMOTE_SESSION_STATES}。 */
  state: string
  /** 取值见 {@link REMOTE_DISPLAY_MODES}。 */
  displayMode: string
  /**
   * 黑白（灰度）模式。
   *
   * ⚠️ 它是**会话级**的，不是观看端私有的：编码只做一路，
   * 所以一个人切黑白，**所有**看这路流的人都会变灰。UI 上必须提示这一点。
   */
  grayscale: boolean
  /**
   * 这一路此刻的**质量档位**（M5.5，取值见 {@link REMOTE_QUALITY_PROFILES}）。
   *
   * ⚠️ 与 {@link grayscale} 同一条纪律：它是**会话级**的，一个人改所有人都变。
   *
   * 🔥 它记的是"**中心要求的档位**"，不是"编码器真的在按这个跑"——
   * 后者要去 `agentStats` 的码率/帧率那一格看。两者不一致正是"切了档没效果"
   * 该被看见的样子（例如 MF 的码率在运行中改不动，只能重建）。
   *
   * ⚠️ 写成可选：旧服务端不认识这个字段时页面必须仍能画（`null`/`undefined` ⇒ 画「—」），
   * 而不是把"我读不到"画成"它是 auto"。
   */
  quality?: string | null
  /**
   * 这一路**当前的目标帧率**（服务端 `TargetFps`）。
   *
   * 🔥 它是**生效值**（档位与请求里的 `maxFps` 取小之后的那个数），
   * 不是用户当初填的那个上限 —— 两者在「自动」档降过档之后就不一样了。
   * 页面上"恢复上一次那一幕"用它当上限是安全的（只会不高于原来）。
   *
   * ⚠️ 可选：旧服务端没有这个字段 ⇒ `undefined`，不许当成 `0`（那会变成"要 0 帧"）。
   */
  targetFps?: number | null
  /** 这一路当前的目标码率（kbps）。可选，理由同上。 */
  targetBitrateKbps?: number | null
  requestedAt: string
  startedAt: string | null
  endedAt: string | null
  /** 为什么停 / 为什么失败（人话，可直接展示）。 */
  endedReason: string | null
  /** 抓的是哪块屏；`null` = 整个虚拟桌面。 */
  screenId: string | null
  /** 实际用的编码格式（如 `mjpeg`）；`null` = 还没开始推流。 */
  codec: string | null
  /** 中心侧计数；`null` = 还没有任何一帧到过中心。 */
  stats: RemoteSessionStatsDto | null
  /** 被控端报的发送计数；`null` = 它还没报过。 */
  agentStats: RemoteSessionAgentStatsDto | null
  /**
   * **被控端的桌面已经静止了多久**（毫秒；M5.4-c② 步 7）。
   *
   * 🔥 一帧都不来时，现场只有三种可能：**桌面真的没动**（展厅里定格在一张图上，正常）、
   * **被控端编不出来**、**链路把帧丢了**。三者在画面上完全一样，处置却相反。
   * 只有被控端的抓屏器知道是哪一种 —— 这一格就是它说的那句话。
   *
   * ⚠️ **三态，不许合并**：`null` = 这台机器不提这件事（旧客户端 / 还没上报过，
   * 画 `—`）；`0` = 刚动过（画"画面在变"）；`12000` = 已静止 12 秒。
   * 把 `null` 画成 `0` 不会报任何错 —— 它只会让每台旧机器都显示"画面在变"。
   *
   * ⚠️ 它**不在** {@link agentStats} 里：那份统计有一条"一帧都没发过就是 null"的判据，
   * 而"从开流起桌面就没动过"的机器恰恰一帧都发不出去 —— 最该解释的那台反而没有。
   */
  desktopIdleMs: number | null
  /**
   * **中心侧**的输入闸门：这一路会话此时此刻是否允许把键鼠送下去（M5.2）。
   *
   * ⚠️ 它与 {@link inputState} 是**两件独立的事**，页面上必须分开说：
   * 这里是"中心**肯不肯**转发"（一个开关，关着时中心根本不会发包），
   * 那里是"被控端**接不接得住**"（一个能力事实）。两个都为真才点得动，
   * 而合成一个"可以控制"的话，出问题时**没人知道该去查哪一边**。
   */
  inputEnabled: boolean
  /**
   * **被控端**的输入通道处于什么状态（M5.2），取值见 {@link AGENT_INPUT_STATES}。
   *
   * 🔥 `null`（=**旧版客户端压根没提过这个字段**）与 `'unavailable'`（= 新版客户端
   * 明确说"我接不上"）含义完全不同：前者要去升级客户端，后者不用。
   * 页面上不许把 `null` 画成"不可用"。
   */
  inputState: string | null
  /**
   * `inputState === 'unavailable'` 时**为什么**（人话，可直接展示；其它情况为 `null`）。
   *
   * 🔥 这一句是唯一能告诉人"下一步该做什么"的东西 ——
   * "客户端版本过旧"要去升级客户端，"这台机器本机禁止了远程输入"只要删一个文件。
   * 只有"不可用"三个字时，这两种情况的处置完全相反却长得一样。
   */
  inputReason: string | null
  /**
   * **被控端**的音频通道处于什么状态（M5.3），取值见 {@link AGENT_AUDIO_STATES}。
   *
   * 🔥 与 {@link inputState} 同一条纪律：三态字符串，**不许压成 bool**。
   * 音频最常见的失败原因是"被控机根本没有音频输出设备"（虚拟机 / 无声卡工控机），
   * 而那与"通道还在建"如果都表现为"没有声音"，现场只会去查网络。
   *
   * ⚠️ `null` = 旧版客户端（不认识这个字段），与 `'unavailable'` 是两件事。
   */
  audioState: string | null
  /**
   * `audioState === 'unavailable'` 时**为什么**（人话；其它情况为 `null`）。
   *
   * 🔥 它**不是** `endedReason`：后者是"这路会话为什么结束"，
   * 一条正常结束的会话也会带上它。拿它当"没有声卡"的原因去显示，
   * 用户会看到一句完全不相关的话（见服务端同名 DTO 的注释）。
   */
  audioReason: string | null
  /**
   * 被控端对**这一路用什么格式编**的裁决说明（M5.4-b 第 7 步）；
   * `null` = 没有额外说明（照中心首选做了，或者是旧客户端不认识这个字段）。
   *
   * 🔥 **它与 {@link codec} 是两件事，不许合并**：`codec` 是**从首帧头里读到的事实**
   * （"实际用的是 h264"），本字段是**被控端给出的理由**
   * （"中心首选 h264、本机改判为 hevc，因为那块屏 5120 宽"）。一个是"是什么"，一个是"为什么"。
   *
   * ⚠️ 为什么理由必须由被控端给：中心算出来的是"偏好 ＋ 可接受集合"，而最终裁决在被控端
   * —— 交集还有第三项输入（**本次会话的分辨率**），只有它挑屏之后才知道。
   * 没有这句话，现场看到的是"中心写着 h264、码流却是 hevc"，而"哪一侧做的决定"完全无法判断。
   */
  codecNote: string | null
  /**
   * **中心要求"只编这一块"**的左上角 X（画面像素；M5.5 的 ROI 编码）。
   * 四个值全为 `0` = 没有视口（整幅画面）。
   *
   * ⚠️ 它记的是"**要求**"，不是"已经生效"：切换要重建编码器（≈0.3 秒）。
   * 真正的事实看画面本身（以及帧头里的 `regionX/regionY`）。
   */
  viewportX?: number
  /** 同上，Y。 */
  viewportY?: number
  /** 视口宽（0 = 没有视口）。 */
  viewportWidth?: number
  /** 视口高（0 = 没有视口）。 */
  viewportHeight?: number
  /**
   * 这一路**实际**切成几块在编（M5.5 分块编码；1 = 没切）。
   *
   * 🔥 它是从**帧头**读回来的事实，不是中心下发的值 —— 切不切由被控端的硬件决定。
   * 只写"分辨率 11520×2160"而看不出切了几块，就无法解释帧率。
   */
  tileCount?: number
}

/**
 * **观看端（浏览器）的编码能力** —— 建会话时声明（M5.4-b 第 7 步）。
 *
 * 🔥 **它声明的是"这一路用哪些格式，本页能显示出来"，不是"这个浏览器的解码器支持什么"。**
 * 两件事不一样，而混起来会踩一个很贵的坑：
 * `VideoDecoder.isConfigSupported('avc1...')` 返回真，只说明**浏览器**能解 H.264；
 * 而**我们这个页面第 8 步之前只会渲染 MJPEG**（`new Image()`，见 `remotePlayback.ts`）。
 * 按浏览器能力去声明，会让中心挑一个"能解但本页不会去解"的格式 —— 画面直接不出来。
 */
export interface RemoteViewerVideoCapabilities {
  /**
   * 本页**显示得了**的格式（取值见 {@link REMOTE_CODECS}）。
   *
   * ⚠️ `mjpeg` 是合法成员，而且它**不需要 WebCodecs**（`<img>` 就能显示）。
   */
  codecs: string[]
  /**
   * 🔥 **到底做没做过探测** —— 它是 `null` 与 `[]` 这件事的唯一判据。
   *
   * 两者在业务代码里几乎总被写成同一句判断，而处置**相反**：
   * - **不发** `viewerVideo`（或 `declared: false`）= **没声明** ⇒ 中心按带内 H.264 回落；
   * - `declared: true` 且 `codecs: []` = **明确说"一种都显示不了"** ⇒ 中心拒绝建会话
   *   （而这在真实浏览器里不可能成立：MJPEG 总是显示得了 ⇒ 那句声明一定写错了）。
   */
  declared: boolean
  /**
   * 本页想要的**封装**（`inband` / `outofband`）；`null` ⇒ 带内。
   *
   * 🔥 由**观看端**定、中心只透传：`codec` 串用哪个前缀（`avc1` 还是 `avc3`）、
   * `description` 到底要不要，是**这个浏览器的解码器**说了算 —— 中心看不到它。
   */
  packaging: string | null
}

/** 一帧码流的头（不含码流本身）。 */
export interface RemoteStreamFrameHeader {
  sessionId: string
  sequence: number
  codec: string
  isKeyframe: boolean
  width: number
  height: number
  /**
   * 抓屏时刻（**UTC 纪元毫秒**，不是北京时间）。
   *
   * ⚠️ 它是用来测延迟的时长基准 —— 观看端要算的是"这一帧从抓到到画出来隔了多久"，
   * 与本地时区无关。人看的那些时间戳仍然一律走北京时间。
   */
  capturedUnixMs: number
  /**
   * **会话单调时钟**上的抓屏时刻（毫秒，M5.3）。
   *
   * 🔥 它是**音画对齐的唯一基准**，与 {@link capturedUnixMs} 分工完全不同：
   * - `capturedUnixMs` 是墙钟，用来算"端到端多久"（依赖两台机器时钟一致）；
   * - 这一条是"这一路会话开始以来的毫秒数"，两台机器**不需要**对时，
   *   而音频帧头上带的是同一条轴上的值 —— 前端拿两者的差做对齐。
   *
   * ⚠️ `null` = 旧被控端（不认识这个字段）。此时视频只能"收到就画"，
   * 音画对齐退化成"各自随缘"，页面上必须把这件事说出来，而不是假装对齐了。
   */
  capturedMonoMs: number | null
  /** 被控端**累计**丢弃的帧数（不是增量）。 */
  dropped: number
  /**
   * **解码器初始化用的 codec 串**（例如 `avc3.640032`）—— M5.4-b 第 8 步才用上。
   *
   * 🔥 **必须由被控端给**：串里的 profile / level 藏在 SPS 里，中心与前端都推不出来。
   * 手写一个"看起来对"的串，错的表现是**能力探测说支持、真解却失败**。
   *
   * ⚠️ 前缀决定**封装**（与 {@link description} 连体，三者必须自洽）：
   * `avc3`/`hev1` = 带内（参数集在码流里，**不带** `description`）；
   * `avc1`/`hvc1` = 带外（带 `description`，码流是长度前缀）。
   *
   * ⚠️ 它**不是**要逐字去 `isConfigSupported` 的那个串 —— 探测阶段拿不到真串
   * （真串要从首帧 SPS 反推），所以探测只能试候选串，"大致支持/不支持"。
   */
  codecString: string | null
  /**
   * **解码器初始化描述**（`avcC` / `hvcC` 字节），**base64 字符串**（与 {@link RemoteStreamFrame.data} 同族）。
   *
   * 🔥 **有它还是没它，决定的是码流的封装，不是"多一段字节"**：带上它 ⇒ 码流必须是
   * **长度前缀**形式；不带 ⇒ 码流是 **Annex-B**（参数集混在码流里）。
   * 这两件事是**连体**的 ——「贴了 avcC、码流却还是 Annex-B」= `configure()` 失败或花屏。
   *
   * ⚠️ **只在关键帧上有值**，且**允许跨帧可变**（带外模式下某个关键帧解不出参数集时，
   * 那一帧会单独退回带内）⇒ 前端必须按"**关键帧上 `(codecString, description)` 这一对
   * 与上次不同就重新 `configure()`**"写，不能只在第一帧 configure 一次。
   */
  description: string | null
  /**
   * **整幅画面**的宽（M5.5 分块/ROI）。`0`/缺省 = 这个被控端没有这个概念
   * ⇒ 整幅画面就等于 {@link width}。
   *
   * 🔥 **不能用 {@link width} 代替**：分块之后 `width` 只是**某一块**的宽
   * （11520 切两块 ⇒ 每块 5760），而画布要的是 11520。
   * 混用会让画布缩成一条、每块再画到错的位置 —— 而且**没有任何一处报错**。
   */
  frameWidth?: number
  /** 整幅画面的高（同上）。 */
  frameHeight?: number
  /** 这一帧画的是整幅画面里的**哪一块**（左上角 X，画面坐标系）。 */
  regionX?: number
  /** 同上，Y。 */
  regionY?: number
  /** 第几块（0 起；没分块时恒为 0）。 */
  tileIndex?: number
  /**
   * 这一帧一共几块（**1 = 没分块**）。
   * ⚠️ 缺省按 1 处理：落成 0 会让"到齐判断"永远等不齐（画面一片空白，日志正常）。
   */
  tileCount?: number
}

/**
 * 一帧码流。
 *
 * 🔥 **`data` 是一段 base64 字符串，不是字节数组** —— 这一点反直觉但很关键：
 * SignalR 的 JSON 协议把 `byte[]` 序列化成 base64，而 JS 客户端**不会**帮你转回来，
 * 你收到什么就是什么。好消息是 base64 可以直接当成 `data:` URL 喂给 `Image`，
 * 所以这里反而省掉了 `atob` 那一趟。
 *
 * ⚠️ 代价是**体积多 33%**（服务端为此把单帧压在 ~380 KB 以内）。
 * M5.4 改走 MessagePack 之后这里会变成 `Uint8Array` —— 届时这个字段的类型要跟着改。
 */
export interface RemoteStreamFrame {
  header: RemoteStreamFrameHeader
  /** base64（无 `data:` 前缀）。 */
  data: string
}

/** 观看端加入一路会话的答复（`Join` 的返回值）。 */
export interface RemoteWatchAckDto {
  ok: boolean
  /** 失败原因（机器可读），取值见 {@link REMOTE_ERROR_CODES}。 */
  errorCode: string | null
  message: string | null
  /** 加入时的会话快照（页面据此立刻把标题、分辨率、帧率画出来）。 */
  session: RemoteSessionDto | null
}

/** 服务端 → 观看端的 Hub 事件名（= `RemoteHubMethods`）。 */
export const REMOTE_HUB_METHODS = {
  /** 一帧画面。载荷 {@link RemoteStreamFrame}。 */
  frame: 'Frame',
  /** 会话状态变化。载荷 {@link RemoteSessionDto}。 */
  sessionChanged: 'SessionChanged',
  /**
   * 一段音频（M5.3）。载荷 {@link RemoteAudioFrame}。
   *
   * 🔥 **与 {@link frame} 是两条独立的消息**，理由和它们在后端是两条管道一样：
   * 前端要按"收到的是哪种"分别喂给两条独立的抖动缓冲 ——
   * 视频"忙就丢"、音频"丢一段就是可听见的断点"，两者的调度策略**相反**。
   * 合成一条消息再靠载荷里的字段分叉，就等于把两套相反的策略写在一个 switch 里。
   */
  audio: 'Audio',
  /**
   * 一段剪贴板内容（M5.5）。载荷 {@link RemoteClipboardDto}。
   *
   * 🔥 **载荷里没有 `sessionId`**：服务端只把这条消息发到「这一路会话」的分组
   * （剪贴板一次最多 60 KB，全站广播等于每次读都发给所有控制台）。
   * 所以页面在**切换会话时必须清空本地那一格** —— 上一路迟到的推送没法靠
   * `sessionId` 认出来，能做的只有"换路即清"，让迟到的内容变成可见的空而不是别人的内容。
   */
  clipboardChanged: 'ClipboardChanged',
} as const

/** 远程桌面相关的机器可读错误码（= 服务端 `RemoteErrorCodes`）。 */
export const REMOTE_ERROR_CODES = {
  /**
   * 该功能尚未接入。
   *
   * ⚠️ M5.2 起已经没有端点在用它了（骨架期的产物），但**不要删**：
   * M5.3–M5.5（音频 / 硬编 / 多屏）还会再出现骨架期。
   */
  notImplemented: 'remote-not-implemented',
  /** 请求体不符合约定（空体 / 缺字段 / 畸形）—— 是"请求发错了"，不是"功能没做"也不是"闸门没开"。 */
  badRequest: 'remote-bad-request',
  sessionNotFound: 'remote-session-not-found',
  /** 在线表里没有这台机器（去装 Agent）。 */
  agentNotFound: 'remote-agent-not-found',
  /** 有这台机器但此刻没有长连接（等它重连）—— 与上一个必须分开。 */
  agentOffline: 'remote-agent-offline',
  /** 被控端**会话侧不在**（没人登录 / 托盘没跑）—— 远控最常见的失败。 */
  sessionSideUnavailable: 'remote-session-side-unavailable',
  startFailed: 'remote-start-failed',
  /** 这台机器上已经有一个未结束的会话（M5.1 只允许一路）。 */
  sessionBusy: 'remote-session-busy',
  /** 这一路会话还没打开"允许控制"开关（中心**根本没发包**）。 */
  inputNotEnabled: 'remote-input-not-enabled',
  /** 打开了开关也做不了（被控端本机禁止 / 客户端版本过旧 / 平台不支持注入）。 */
  inputUnavailable: 'remote-input-unavailable',
  /** 一批输入事件过大（防"一次塞十万条把队列打爆"）。 */
  inputTooLarge: 'remote-input-too-large',
  /**
   * 这条连接当前**没有在观看这一路**（M5.2 的归属校验）——
   * 会话还在，只是你这条连接已经不在看它了（刷新过 / 切走过 / `Leave` 那次调用丢了）。
   *
   * 🔥 它与 `sessionNotFound` 是**两句不同的话**：那个要刷新列表，
   * 这个只要**把这一路重新打开**。两者曾经共用一个码，于是页面只能笼统说一句"失败了"。
   *
   * ⚠️ 它也**不是**"控制权被别人抢了"：本方案**没有**独占控制权 ——
   * 多人同时看一路时，任何一个正在看这一路的人都有资格操作。
   */
  notWatching: 'remote-not-watching',
} as const

// ═══════════════════════════════════════════════════════════════════════
// M5.2 输入回传
// ═══════════════════════════════════════════════════════════════════════

/**
 * 输入事件的类型（= 服务端 `RemoteInputKinds`）。
 *
 * 🔥 四种里**只有指针移动可以被丢弃/合并**。其余三种一旦丢一条，后果是
 * "那个键在被控机上永远卡着"（丢的是抬起）或"点了没反应"（丢的是按下）——
 * 而这两种症状在画面上都看不出来。合批逻辑必须严守这一点。
 */
export const REMOTE_INPUT_KINDS = {
  pointerMove: 'pointer-move',
  pointerButton: 'pointer-button',
  wheel: 'wheel',
  key: 'key',
} as const

/** 鼠标键的线上取值（= 服务端 `RemoteMouseButtons`）。 */
export const REMOTE_MOUSE_BUTTONS = {
  left: 'left',
  middle: 'middle',
  right: 'right',
} as const

/**
 * 被控端的输入通道状态（= 服务端 `AgentInputStates`）。
 *
 * ⚠️ **三态，不许压成 bool**：把 `connecting` 与 `unavailable` 压成一个 `false`，
 * 页面就只能说"不可用" —— 而"正在连"的时候该做的是**等一秒**，
 * 不是去查客户端版本。看错这一眼的代价是一台好好的机器被重装客户端。
 */
export const AGENT_INPUT_STATES = {
  /** 正在接入（会话刚起，管道还没接上）—— 该等着，不该报错。 */
  connecting: 'connecting',
  /** 已接上，随时可以注入。 */
  ready: 'ready',
  /** **确定接不上**（客户端版本过旧 / 被控端本机禁止 / 接入超时）。一定带原因。 */
  unavailable: 'unavailable',
} as const

/**
 * 单批最多多少条事件（= 服务端 `RemoteInputLimits.MaxEventsPerBatch`）。
 *
 * ⚠️ 它挡的不是"正常的鼠标拖动"（通常是几十条），而是"一个坏掉的脚本一次塞十万条"——
 * 那会同时打爆中心内存与被控端注入循环，表现是"远控一开，被控机就卡死"。
 * 超限的处理是**拒绝整批**（不是截断）：截断会让"按下"留下、"抬起"被砍掉。
 */
export const REMOTE_INPUT_MAX_EVENTS = 256

/**
 * **一条输入事件**（浏览器 → 中心 → 客户机 → 托盘）。
 *
 * 🔥 **坐标是归一化的 0–65535，不是像素**：观看端可能用 `fit`（缩放过）
 * 或 `1:1`（带滚动条）两种视口，两者算出来的像素完全不同；而且被控端分辨率
 * 可能**大于**观看端的显示器（被控端最高 8K，看的人可能只有 1080p），
 * 像素坐标根本表达不了"画面的 60% 处"。
 *
 * ⚠️ 归一化的基准是**这一路画面的矩形**，由托盘换算成虚拟桌面像素 ——
 * 别在这里加屏幕偏移，那件事只有托盘知道（它才知道抓的是哪块屏）。
 *
 * ⚠️ 它是个"宽类型"（每种 kind 只用其中几个字段），与后端一致。
 */
export interface RemoteInputEventDto {
  /** 取值见 {@link REMOTE_INPUT_KINDS}。 */
  kind: string
  /** 归一化横坐标 0–65535。仅 `pointer-move` 用。 */
  x: number
  /** 归一化纵坐标 0–65535。仅 `pointer-move` 用。 */
  y: number
  /** 取值见 {@link REMOTE_MOUSE_BUTTONS}。仅 `pointer-button` 用。 */
  button: string | null
  /** 按下（真）/ 抬起（假）。指针键与键盘共用。 */
  down: boolean
  /** 滚轮刻度，**正值 = 向上**。仅 `wheel` 用。 */
  delta: number
  /** Windows 虚拟键码。仅 `key` 用。 */
  virtualKey: number
}

/**
 * **一批输入事件**（M5.2 的传输单位）。
 *
 * 🔥 为什么批量：鼠标拖动一次就是几十到几百个移动事件。一条一个 Hub 调用
 * 意味着**每个像素移动都走一次完整往返**，延迟会从"毫秒"抖到没法用。
 */
export interface RemoteInputBatchDto {
  sessionId: string
  /** 按数组顺序执行 —— 拖拽必须保序。 */
  events: RemoteInputEventDto[]
}

/**
 * 输入回传的结果（= 服务端 `RemoteInputAckDto`）。
 *
 * 🔥 **必须有它**：输入是唯一一类"发了没反应就等于没发"的动作。画面卡住时人能看见，
 * 而按下一个键之后什么都没发生 —— 到底是"键没送到""被控端拒了"
 * 还是"送到了但那个键在那台机器上没用"，没有回执完全分不出来。
 */
export interface RemoteInputAckDto {
  /** 真 = 这批已经交给下一跳（**不保证被控端已经作用于桌面**）。 */
  ok: boolean
  /** 失败原因（机器可读），取值见 {@link REMOTE_ERROR_CODES}。 */
  code: string | null
  message: string | null
  /** 实际下发了多少条。 */
  accepted: number
  /** 被**合并**掉的条数（只有指针移动会被合并）。合并是设计如此，不是故障。 */
  coalesced: number
}

/** 观看端 → 中心的 Hub 方法名（= 服务端 `RemoteHubClientMethods`）。 */
export const REMOTE_HUB_CLIENT_METHODS = {
  /** 送回一批输入事件。载荷 {@link RemoteInputBatchDto}，返回值 {@link RemoteInputAckDto}。 */
  sendInput: 'SendInput',
  /** 打开 / 关闭这一路会话的输入回传。参数 `(sessionId, enabled)`，返回变更后的会话。 */
  setInputEnabled: 'SetInputEnabled',
} as const

// ═══════════════════════════════════════════════════════════════════════
// M5.3 音频
// ═══════════════════════════════════════════════════════════════════════

/**
 * 被控端的音频通道状态（= 服务端 `AgentAudioStates`）。
 *
 * ⚠️ **三态，不许压成 bool** —— 与 {@link AGENT_INPUT_STATES} 同一条纪律。
 * 但它比输入少一态：音频**没有中心侧的会话级闸门**
 * （"有声音"是这路会话的默认行为，不像"能不能动别人鼠标"那样需要逐次授权）。
 */
export const AGENT_AUDIO_STATES = {
  /** 正在建音频通道（托盘在架管道 / 服务在接）—— 该等一秒，不该报错。 */
  connecting: 'connecting',
  /** 已就绪，音频正在流。 */
  ready: 'ready',
  /** **确定接不上**，原因见 `audioReason`。一定带原因。 */
  unavailable: 'unavailable',
} as const

/** 音频编码格式（= 服务端 `RemoteAudioCodecs`）。 */
export const REMOTE_AUDIO_CODECS = {
  /** Opus（20 ms 一帧 / 48 kHz）。浏览器 `AudioDecoder` 的原生支持格式。 */
  opus: 'opus',
} as const

/**
 * 一段音频的**头**（不含载荷）。
 *
 * 🔥 音频也必须带帧头，它承担三件在别处无法表达的事：
 * ① **解码参数**（Opus **不带内省信息**，解码端必须预先知道采样率与声道数）；
 * ② **音画对齐的时间戳**（{@link capturedMonoMs}）；③ 与视频对账的序号。
 */
export interface RemoteAudioFrameHeader {
  sessionId: string
  /** 单调递增序号。`AudioDecoder` 按提交顺序出结果，所以它只用于"有没有漏"的对账。 */
  sequence: number
  /** 取值见 {@link REMOTE_AUDIO_CODECS}。 */
  codec: string
  /** 采样率（Hz）。Opus 固定 48000。 */
  sampleRate: number
  /** 声道数（1 或 2）。 */
  channels: number
  /** 这一帧包含多少个**每声道**样本。20 ms @ 48 kHz = 960。 */
  samplesPerFrame: number
  /** 采集时刻（UTC 纪元毫秒）。与视频同义：**只用于测延迟**，不参与播放调度。 */
  capturedUnixMs: number
  /**
   * **会话单调时钟**上的采集时刻（毫秒）。🔊 **音画同步靠的就是它**。
   *
   * ⚠️ 它取自 WASAPI 给出的 QPC 位置（音频引擎真正采到这一段的那一刻），
   * 而不是"被控端读到它的那一刻"—— 后者会带上一个可变的设备缓冲延迟（10–30 ms），
   * 而 40 ms 的同步预算经不起这么花。
   */
  capturedMonoMs: number
  /** 被控端**累计**丢弃的音频段数（正常恒为 0；非 0 意味着音频有断点）。 */
  dropped: number
}

/**
 * 一段音频。**管道、中心中继、浏览器三处用的是同一个形状**。
 *
 * ⚠️ 与 {@link RemoteStreamFrame} 同一条约定：`data` 是 **base64 字符串** ——
 * SignalR 的 JSON 协议把 `byte[]` 序列化成 base64，而 JS 客户端不会帮你转回来。
 * 但音频**必须** `atob` 出真正的字节才能喂给 `AudioDecoder`
 * （视频那边是直接当 `data:` URL 用，所以省掉了这一趟）。
 */
export interface RemoteAudioFrame {
  header: RemoteAudioFrameHeader
  /** base64（无前缀）。 */
  data: string
}

/**
 * 根证书与 https 地址（`GET /trust/info`，M5.3）。
 *
 * 🔥 前端要它只为一件事：**当页面跑在非安全上下文里时，告诉用户该怎么办**。
 * 那种上下文里 `AudioDecoder` / `AudioWorklet` / `VideoDecoder` **压根不存在**
 * （Chrome 94 起，141 复验），而这是本方案里唯一一处"不报错的整块功能失效"——
 * 音频区域一句错都没有，就是永远没声音。所以必须有这条路把结论说出口。
 */
export interface TrustInfoDto {
  /** 服务端**确实**能提供 https（配置开着**而且**证书建得出来）。 */
  httpsEnabled: boolean
  /** https 端口（前端据此拼地址；**不要猜**，端口是运维改得动的）。 */
  httpsPort: number
  /** 服务端实际在听的 https 地址（排障用）。 */
  httpsUrls: string[]
  /** 根证书的**同源相对**地址。 */
  caUrl: string
  /** 根证书指纹（大写十六进制、冒号分隔），用于人工核对"装的是不是这一张"。 */
  caFingerprint: string | null
  /** https 不可用时的原因（人话）。 */
  caReason: string | null
}

// ═══════════════════════════════════════════════════════════════
// 多屏 / 剪贴板（M5.5）
// ═══════════════════════════════════════════════════════════════

/**
 * 被控机上的一块屏（= 服务端 `RemoteScreenDto`）。
 *
 * 🔥 {@link id} 是**抓屏器认的那个 Id**，不是"第几块显示器"。
 * 它直接进建会话请求的 `screenId`，两端只认这一个约定 ——
 * 拿序号（0/1/2）当 Id 传下去，被控端会找不到屏，而错误信息只说"指定屏幕不存在"。
 */
export interface RemoteScreenDto {
  /** 抓屏器认的 Id（原样回传给建会话请求）。 */
  id: string
  /** 给人看的名字（"显示器 2" / "全部显示器（拼接）"）。 */
  name: string
  /** 在虚拟桌面里的原点 X（多屏拼接时用；单屏恒为 0）。 */
  x: number
  /** 同上，Y。 */
  y: number
  width: number
  height: number
  /** 是不是主屏。 */
  isPrimary: boolean
  /**
   * 是不是"全部显示器（拼接）"这一项。
   *
   * ⚠️ **必须与真实显示器分开画**：它只是一块合成出来的大画面，
   * 而它的宽高可能超过任何硬编格式的上限（选了它却回落到 MJPEG 时，
   * 用户要知道原因在这一项上，而不是"这台机器不支持硬编"）。
   */
  isVirtualDesktop: boolean
}

/**
 * 中心侧看到的屏清单（= 服务端 `RemoteScreensDto`）。
 *
 * 🔥 **三态必须分开画**，把①画成②会让现场去查显示器插没插：
 * ①`pending=true` = 正在问，还没回来；②`pending=false` 且 `screens=[]` = 那台机器
 * **真的没有可抓的显示器**（没人登录）；③有列表 = 正常。
 */
export interface RemoteScreensDto {
  screens: RemoteScreenDto[]
  /** 刚向客户机发出了索取指令，清单还没回来。 */
  pending: boolean
  /** 给人看的一句话（`null` = 没什么可说的）。 */
  note: string | null
  /** 这份清单是什么时候采的（`null` = 从没采到过）。 */
  reportedAt: string | null
}

/**
 * 一段剪贴板内容（= 服务端 `RemoteClipboardDto`，M5.5）。
 *
 * 🔥 {@link text} 为 `null` 有**两种完全不同的意思**，靠 {@link note} 区分：
 * · `text === null` 且 `note === null` ⇒ 被控机剪贴板里**没有文本**（有图片 / 空 / 被别的程序锁着）——
 *   这不是故障，不需要处置；
 * · `text === null` 且 `note` 有内容 ⇒ **读失败**，`note` 就是原因。
 * 把两者画成同一句"读取失败"，会让人去查一个根本没坏的东西。
 */
export interface RemoteClipboardDto {
  /** 文本；`null` = 没有文本（见上）。 */
  text: string | null
  /** 给人看的一句话（正常时为 `null`；失败时是原因）。 */
  note: string | null
  /** 读取时刻（北京时间）。 */
  at: string | null
  /** 是否因超过上限（2 万字）被截断。 */
  truncated: boolean
}

/**
 * 剪贴板同步的单次上限（= 服务端 `RemoteClipboardLimits.MaxChars`）。
 *
 * ⚠️ 前端也判一次**不是为了安全**（服务端会拒），而是为了"在发之前就说清"：
 * 让用户先看到"超过 2 万字，只会发前 2 万字"，而不是点了之后收到一个 400。
 */
export const REMOTE_CLIPBOARD_MAX_CHARS = 20_000

