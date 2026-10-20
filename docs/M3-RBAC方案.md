# M3 · 三角色 RBAC 与账号生命周期 —— 实施方案

> 交付日期：2026-10-08　前置：M2 全部完成（含 G8 冒烟与打包）
> 本轮范围由用户拍板：**只做「RBAC 三角色用户管理」**；授权子系统（非对称签名）与
> 精确机器码绑定**已记录待办**，本轮未动。
> 相关：`docs/中控Web服务版-落地方案.md` §9、`docs/M2-功能对齐方案.md` §0.2

---

## 0. 本轮拍板与范围

| 议题 | 结论 | 备注 |
|---|---|---|
| 本轮 M3 覆盖模块 | **RBAC 三角色 + 账号生命周期** | 未选：授权子系统、防火墙 + PBKDF2、Linux/Docker 交付 |
| 授权文件密钥模型 | 非对称签名（记录待办） | 本轮未实现 |
| 授权与机器绑定 | 精确绑定机器码（记录待办） | 本轮未实现 |

### 0.1 与既有文档的冲突（已消解）

`落地方案 §9` 把**授权**列在 M3，`M2-功能对齐方案 §0.2` 列在 M4。本轮以用户拍板为准：
M3 = RBAC，授权仍留在 M4。**两处文档的文字冲突未回改**，以本文为准。

---

## 1. 数据模型

`AppUser`（表 `users`，M1 基线表）本轮**只加列**，因此 `InitialCreate` 基线迁移保持冻结，
老库走 `ALTER TABLE ADD COLUMN` 增量升级，**用户不需要删库**。

| 新列 | 类型 | 语义 |
|---|---|---|
| `IsEnabled` | `bool` | 停用后不能登录；已签发会话在下一次请求即被作废 |
| `LastLoginAt` | `DateTimeOffset?` | 最近登录成功时刻；从未登录为 `null` |
| `FailedLoginCount` | `int` | 连续失败计数；成功后清零 |
| `LockedUntil` | `DateTimeOffset?` | 锁定到期时刻；`null` = 未锁定 |

迁移：`20261008012249_M3_UserAccount`。

### ⚠️ 两处「EF 默认值」陷阱（都已在代码里钉住）

1. **非空 `bool` 新列 + `defaultValue: false` 会把存量账号一次性停用** ——
   EF 生成的迁移给 `IsEnabled` 的默认值是 `false`，升级后**谁都登不进来**。
   已手改为 `defaultValue: true`，并加注释；`MigrationTests` 有守门断言读实际值。
2. **实体上刻意不写 `HasDefaultValue`** —— EF 对"带库端默认值的属性"按 **CLR 默认值哨兵**
   跳过插入，而 `bool` 的哨兵是 `false`，于是"新建一个已禁用的账号"会落库成启用。
   行默认值只在迁移脚本里给**存量行**用。

---

## 2. 密码策略

`Core/Services/PasswordPolicy.cs`（`Core` 内，可单测、可被 Agent 复用）：

- `MinLength = 8`、`MaxLength = 128`
- 拒绝：空、过短、过长、**等于出厂默认口令**、**等于用户名**（Trim 后、忽略大小写）
- 只做**新密码准入**；**登录不校验** —— 否则会把历史上设了弱口令的用户直接锁在门外
- `ShippedDefaultPassword = "admin123"` 与 `appsettings.json` 的 `DefaultAdminPassword` 同源

---

## 3. 会话与授权

### 3.1 授权策略

| 策略 | 角色 |
|---|---|
| `Policies.AdminOnly` | `Admin` |
| `Policies.CanOperate` | `Admin` + `Operator` |

`Viewer` 只要 `[Authorize]` 即可（只读）。

**无全局 FallbackPolicy** ⇒ 每个端点必须自带 `[Authorize]` 或显式 `[AllowAnonymous]`，
"忘标注" = "对匿名开放"。为此加了反射守门用例 `AuthorizationCoverageTests`
（`tests/.../UserRbacTests.cs`）：遍历所有 `[HttpMethod]` action，列出既没 `[Authorize]`
也没 `[AllowAnonymous]` 的 offender。

### 3.2 Cookie 逐请求复核（`OnValidatePrincipal`）

Cookie 里是**签发时的快照**。没有复核这一步，"把某人降级成 Viewer"或"停用某账号"
在对方 Cookie 过期（最长 12 小时）之前**完全不生效** —— 权限分级会退化成
"最长 12 小时后才生效"的承诺。

`Program.cs` 的 `OnValidatePrincipal` 每个请求做一次主键查询：

| 情况 | 处理 |
|---|---|
| Cookie 无账号 Id（M3 之前签发的） | 作废 + 登出，要求重新登录 |
| 账号不存在 / 已停用 | 作废 + 登出 |
| 用户名 / 角色 / 改密标记变了 | `ReplacePrincipal` + `ShouldRenew = true`（重签 Cookie） |
| 查询抛异常 | **fail-open** 放行 + 记 Warning |

fail-open 是**有意的取舍**：SQLite 并发下偶发 BUSY，若因此把所有人踢下线，
代价远大于"DB 故障期间多放行几个请求"。

### 3.3 强制改密闸门

> ⚠️ **2026-10-09 变更：本闸门已默认关闭，且不再有任何账号会被自动标记。**
> 产品取消了"首次使用强制改密"：出厂管理员、新建账号、管理员重置密码都不再置
> `MustChangePassword = true`。本节描述的是**仍然保留的机制**（可显式开启），
> 默认状态下不会触发。
>
> 另外，**升级上来的旧库**里遗留的 `MustChangePassword = true`（旧机制写下的）会在启动时
> 被一次性幂等清除（`DatabaseInitializer.ClearStaleMustChangePasswordAsync`）——
> 否则闸门虽已关，那个标记仍会随登录响应下发、让前端继续弹"关不掉"的强制改密窗。

中间件（`UseAuthentication()` 之后、`UseAuthorization()` 之前）：

```
未改密 且 路径以 /api 开头 且 不在白名单  →  403 { message, mustChangePassword: true }
```

**白名单**：`change-password`、`me`、`logout`。
（`change-password` 必须在白名单里，否则用户被锁在"必须先改密"与"改密端点不允许访问"的死结里。）

**为什么只闸 `/api`**：
- 静态资源在 `UseAuthentication` 之前就返回了，走不到这里；
- `/healthz`、`/readyz` 是探活端点，被闸住会让服务被运维判死；
- SignalR 若被拦，"改密页面"上的实时日志会断，看起来像服务挂了。

**开关**：`Server:Auth:EnforcePasswordChange`（**默认 `false`**）。
置 `true` 后，凡是被标记 `MustChangePassword = true` 的账号（须手工标记或旧库遗留）
在改密前会被拦。

### ⚠️⚠️ 3.4 关于"升级破坏性变更"（**已废止**）

> **本节的结论已被 2026-10-09 的变更推翻，保留仅作历史决策记录。**
>
> 当时出厂管理员带 `MustChangePassword = true` 播种 + 闸门默认开启，会导致：
> 全新安装的 `admin/admin123` 未改密前任何 /api 都 403；M1/M2 升级后，
> 从未改过初始口令的现场管理员会**突然被闸门拦住**。
>
> **现在**：标记不再被自动设置、闸门默认关闭，因此**不存在**上述破坏性影响。
> 只有当有人手工把闸门开回 `true` 并标记账号时，才需要重新评估这一节。
> （这也正是"取消首次强制改密"要解决的问题：出厂即用，不再有升级突袭。）

---

## 4. API 契约

### 4.1 认证（`AuthController`）

| 端点 | 说明 |
|---|---|
| `POST /api/auth/login` | 判定顺序见下 |
| `POST /api/auth/logout` | |
| `GET /api/auth/me` | **不查库**，读声明（声明已被复核刷新过） |
| `POST /api/auth/change-password` | 改自己的密码；成功后**立刻重签会话** |

**登录判定顺序是刻意设计的**（先口令、后状态）：

1. 账号不存在 **或** 口令错 → 统一 `401 "用户名或密码错误"` + 失败计数（含锁定提示）
2. 口令对但**已停用** → `401 "账号已被停用，请联系管理员。"`
3. 口令对但**锁定中** → `401 "账号处于锁定状态，请在约 N 分钟后重试。"`
4. 放行 → `RecordLoginSuccessAsync` + 签发会话 → `LoginResponse(userName, role, mustChangePassword)`

前两步不泄漏账号状态，第三步才给出精确原因（让真正的主人知道该找谁）。

### 4.2 账号管理（`UsersController`，**整类 `AdminOnly`**）

> 账号管理是"谁能进来"这件事本身。交给 Operator 等于让他给自己提权，
> 所以这里没有"部分开放"的位置。

| 端点 | 状态码映射 |
|---|---|
| `GET /api/users` | 200 |
| `GET /api/users/roles` | 200，含中文 `displayName`（前端不必硬编码，避免漂移） |
| `POST /api/users` | Invalid / DuplicateName → 400 |
| `PUT /api/users/{id:int}` | NotFound → 404；LastAdmin / SelfTarget → **409** |
| `POST /api/users/{id:int}/password` | 重置他人密码，置回 `MustChangePassword = true` |
| `DELETE /api/users/{id:int}` | NotFound → 404；LastAdmin / SelfTarget → 409 |

**为什么 409 而不是 400**：请求本身合法，只是与服务端当前状态冲突
（"最后一个管理员"、"不能操作自己"）。用 400 会让人以为是自己拼错了参数，
而这两类错误只有"换个对象"才能解决。

**写操作统一返回 `UserMutationResult`（含服务端全量列表）**：失败时前端可立即据此刷新，
不必再发 GET，也就不会出现"界面停在旧状态"。（与 `CommandMutationDto` 同一套约定。）

**`UserDto` 刻意不含 `PasswordHash` / `Salt`**：实体与视图是两个类型，
想漏就得先显式多写一行映射 —— "接口泄漏口令材料"在类型层面变得不可能。

### 4.3 本轮收紧的授权（**只收紧一处**）

| 端点 | 原来 | 现在 |
|---|---|---|
| `GET /api/config/export` | `[Authorize]` | `AdminOnly` |
| `POST /api/config/import` | `CanOperate` | `AdminOnly` |

理由：导入一次就能**整体替换全站配置**，导出等于把整套现场配置（含图片）带走 ——
两者都是"系统级"操作，不是操作员的日常编辑。其余端点**未动**。

---

## 5. 前端

| 文件 | 内容 |
|---|---|
| `web/src/types.ts` | `CurrentUser.mustChangePassword`（新增）、`UserDto`、`UserMutationResult`、`RoleOption` |
| `web/src/api.ts` | `users` / `roles` / `createUser` / `updateUser` / `resetUserPassword` / `deleteUser` / `changePassword` |
| `components/UserManagementPanel.vue`（新） | 列表 + 新建 + 改名 + 改角色 + 启停 + 重置密码 + 删除 |
| `components/ChangePasswordDialog.vue`（新） | 改密弹窗；`forced` 时**关不掉** |
| `App.vue` | 「用户」标签（仅 Admin 可见）+ 顶栏「修改密码」+ 强制改密提示条 + 弹窗宿主 |
| `components/EditPanel.vue` | 用 `fieldset :disabled="!canOperate"` 把整个可编辑区**原生**置灰 |
| `components/SettingsPanel.vue` | 导入 / 导出 / 恢复出厂 三处按 `isAdmin` 收口 |

**`CurrentUser.mustChangePassword` 两处都要带**（登录响应 + `/api/auth/me`）：
只放在登录响应里的话，用户**刷新页面即可跳过**强制改密。

**前端只做"提前禁用 + 说明"**，真正的授权判定始终在服务端；
越权请求会被 403。之所以还要拦一道：让查看者一眼看出是权限问题，
而不是改了半天、点保存才发现存不下去。

**`EditPanel` 用 `fieldset[disabled]` 而不是逐个加 `:disabled`**：
`fieldset` 的 `disabled` 会原生地禁用内部**所有**输入框与按钮，
漏一个就是一个可点的漏洞。左侧瓦片行本身是可点的 `div`，不受影响 ——
查看者仍能浏览每一项的内容。

---

## 6. 守卫规则（服务端）

| 规则 | 状态 | 说明 |
|---|---|---|
| 用户名唯一（**大小写不敏感**） | `DuplicateName` 400 | `ToLower()` 归一后比较 |
| 停用 / 删除自己 | `SelfTarget` 409 | 改名、改自己密码**允许** |
| 动**最后一个启用的管理员**（降级 / 停用 / 删除） | `LastAdmin` 409 | |
| 有同伴时**给自己降级** | **允许** | 交接后退出是正常诉求；一刀切禁掉只会逼人绕路 |
| 连续失败 5 次 | 锁定 15 分钟 | `MaxFailedAttempts = 5`、`LockDuration = 15min` |
| 锁定期间继续输错 | **不累加、不延长** | **否则账号可被永久锁死** |
| 重置密码 / 新建账号 | 置 `MustChangePassword = true` | 管理员设的是"临时口令" |
| 停用账号 | 清 `FailedLoginCount` / `LockedUntil` | 重新启用时不该继承停用前的失败计数 |
| 无实际变化 | 不写库、不记审计 | 避免"点一次保存多一条审计"的噪音 |

审计动作：`user-created` / `user-updated` / `user-deleted` / `user-password-reset` /
`password-changed` / `login-failed` / `login-rejected-disabled` / `login-rejected-locked`。

### ⚠️ 「自我降级」的必然后果（实测确认，非缺陷）

管理员**可以**在有同伴时把自己降级 —— 但降完就**回不来**了：
后续 `PUT /api/users/{自己}` 会返回 **403**（已不是管理员），
必须请另一位管理员操作。这是 RBAC 的正常语义（否则"降级"就是可自我撤销的假动作）。
前端因此**不**禁止自己那一行的降级选项，只禁"最后一个管理员"。

---

## 7. 验证

| 项 | 结果 |
|---|---|
| `dotnet build` | 0 警告 0 错误 |
| `dotnet test` | Core **357** 通过（M3 +13）、集成 **165** 通过（M3 +22） |
| `npm run typecheck`（`vue-tsc --noEmit`） | 0 错误 |
| `npm run build` | ✓ 105 modules |
| `deploy/smoke/run-all.py`（**对打包 exe**） | **4/4 场景全绿**：web 36 + pairing + admin 69 + **rbac 94** |
| 打包 | `dist/win-x64/ControlServer.Server.exe` 单文件 51 MB，目录只有 1 个文件 |
| 真机验证 | 闸门 / 三角色矩阵 / 停用与降级**跨会话当场生效** / 锁定不延长 / 时序口径 |

### 7.1 新脚本 `deploy/smoke/rbac-smoke.py`

自带**多会话**（每个角色一个独立 Cookie jar），所以"降级/停用是否当场生效"
这类**跨会话**断言能真跑出来。它会把 admin 密码从 `admin123` 改掉 —— 只对临时实例跑。

> **2026-10-09 起**：脚本用**默认配置**（闸门默认关）。A 段验证的是"出厂态开箱即用"，
> 不再是"必须先改密"。`run-all.py` 也不再需要为任何场景传
> `--Server:Auth:EnforcePasswordChange=false` —— 那曾经是必需的噪音消除，
> 现在服务端默认就是关的。

### 7.2 `vue-tsc` 抓到的两处**既有**缺陷（本轮顺带修掉）

前端从未做过 SFC 类型检查，本轮补上 `vue-tsc` 后立刻暴露：

- `CommandLibraryPanel.vue` ×3、`TimerOverviewPanel.vue` ×1：
  `emit('warn', '...')` / `emit('err', '...')` —— 事件名写成了**提示级别**，
  而声明的 `defineEmits` 里事件名是 `'notice'`。
  父组件监听的是 `@notice`，于是这些提示**静默不显示**
  （Vue 只在开发模式警告，生产构建下完全无声）。
  典型受害者：**"删除指令前提醒还有 3 个动作在引用它"** —— 这条安全提示从来没出现过。
  已全部改为 `emit('notice', 'warn' | 'err', ...)`。

### 7.3 顺带修掉的时间口径缺陷

`AppUser.CreatedAt` 与 `Setting.UpdatedAt` 的实体初始值写的是 `DateTimeOffset.UtcNow`
—— 违反铁律 #4（时间戳统一 `BeijingTime`）。实测：旧代码播种的 admin 落库为
`2026-10-08 01:43:14+00:00`（UTC）。后果是**出厂管理员的"创建时间"在界面上差 8 小时**
（前端 `fmtDateTime` 是直接截 ISO 字符串，刻意不做时区换算）。

修法（三处）：
1. `AppUser.CreatedAt` 初始值 → `BeijingTime.Now`
2. `Setting.UpdatedAt` 初始值 → `BeijingTime.Now`
3. `DatabaseInitializer.SeedDefaultAdminAsync` 显式赋 `CreatedAt = BeijingTime.Now`
   （并补 `IsEnabled = true`）

守门用例：`MigrationTests` 新增 `ReadUserCreatedAtOffset`，直接读 SQLite 原始文本
并断言偏移是 `+08:00`。**已实测该用例有牙齿**（旧值 `+00:00` ⇒ 失败）。

---

## 8. 未做 / 遗留

本轮**未做**（按用户拍板）：

- 授权子系统（License 文件、非对称签名、机器码精确绑定）
- 防火墙规则
- PBKDF2 口令哈希升级（当前仍是 M1 的 SHA-256 + salt）
- Linux / Docker 交付

延续 M2 未决（本轮未处理）：

- 导入前自动备份 `.bak`
- `Server:Storage:DataDir` 拼错时静默回退默认目录（建议启动自检 Warning）
- `Server:Timer:BackfillMissed` 只出现在告警文案里、未实现
- `POST /api/maintenance/clear-images` 不存在，且 `ClearCustomImagesAsync` 不删磁盘文件
- **页面观感仍未经真实浏览器验证**（本机沙箱起不来 Chromium），M2 累计至今仍未消除

新增待办：

- `App.vue` 顶栏的 `ROLE_LABELS` 本地映射与 `Contracts.Roles.DisplayName` 存在**双份中文名**。
  原因：`/api/users/roles` 是 AdminOnly，非管理员拿不到权威清单。
  新增角色时**两处都要动**（已在代码里注明）。
