# REF-M3 · RBAC 模块细节（改代码前必读）

> 配套：`docs/M3-RBAC方案.md`（设计全文）。本文件只放**改动前必须知道的事实**与**陷阱**。

## 0. ⚠️ 2026-10-09 变更：已取消"首次使用强制改密"

**行为变更（产品决定）**：
- `AuthOptions.EnforcePasswordChange` **默认 `true` → `false`**（`Server/Options/ServerOptions.cs`）。
- `MustChangePassword` **不再被自动置 true** —— 三处全改：
  `DatabaseInitializer.SeedDefaultAdminAsync`（出厂 admin）、
  `UserAccountService.CreateAsync`（新建账号）、
  `UserAccountService.ResetPasswordAsync`（管理员重置，现在还会**主动清掉**该标记）。
- 前端 `App.vue` 的 `mustChangePassword` / 强制弹窗（`ChangePasswordDialog` 的 `forced` 态）**逻辑保留**：
  只有"闸门被显式开启 + 账号被手工标记"时才会出现；默认恒为 false。
- 登录页「记住账号密码」：明文存 `localStorage['cs.remembered-login']`（`{userName, password}`）。
  勾选框文案**只有"记住账号密码"**（括号说明按 2026-10-10 需求去掉，风险只留在代码/文档里）。
  样式：`.login-card label.inline` 是 flex 行 + `align-items:center`（全局 `label` 是 block，
  不显式写就会因复选框默认基线而错位）；**别删这段 scoped 样式**，它在别的组件里不会被继承。
  ⚠️ **复选框必须逐项复位尺寸/padding**：全局 `input { width:100%; padding:6px 10px }` **也命中**
  `input[type=checkbox]`；放进 flex 行后 `flex-basis` 取 `width` ⇒ 复选框吃满整行、
  文字被挤成"一字一行"的竖排。已在样式里写明"别再把 width 删掉"。
  读写**全部 try/catch** —— localStorage 不可用（隐私模式 / 配额满 / 数据损坏）必须降级为"不记住"，
  **绝不能让登录页打不开**。
  验证：`.tools/remember-check/run.sh`（无头 Edge + CDP，**21 / 21**），
  关键断言 —— "清 Cookie 后密码回填"、"取消勾选后记录被清除"、
  "文案只有「记住账号密码」"、"复选框与文字垂直中心偏差 ≤ 1px"、"文字单行横排"。
  改登录页后重跑它，别只看 `vue-tsc`（类型对不代表 v-model 真的收到值）。
  截图复核：`.tools/remember-check/shot.sh`。

**⚠️ 旧库清洗（少了它"取消"就不彻底）**：`DatabaseInitializer.ClearStaleMustChangePasswordAsync`
在每次启动时把存量 `MustChangePassword = 1` 全清为 `0`（幂等；有存量时打 Warning 列出账号）。
原因：闸门关了，但旧库遗留的 `true` 仍会随 `LoginResponse` / `/api/auth/me` 下发，
前端照旧弹强制改密窗。守门用例
`ForcedPasswordChangeTests.Stale_flag_in_an_older_database_is_cleared_on_startup`
（预置标记 → 同目录重启 → 断言登录响应为 false）。

**验证方式变了**：已经没有代码路径会自动标记账号，所以测试闸门必须**直接写库**：
`UserRbacTests.ForcedPasswordChangeTests.Gate_still_enforced_when_enabled_and_account_is_flagged`
（`UPDATE users SET MustChangePassword=1` 后再登录）。
排查"闸门没生效"时先确认**是不是根本没有账号被标记**，而不是闸门坏了。

**测试/冒烟口径**：`ControlServerFactory` 仍显式传 `false`（无害，且防将来默认值再变）；
`rbac-smoke.py` A 段已改为断言"出厂态开箱即用（不带标记、业务接口直接 200）"；
`run-all.py` **不再传** `--Server:Auth:EnforcePasswordChange=false`（默认就是关的）。

## 1. 文件地图

| 要做的事 | 改哪 |
|---|---|
| 角色 / 策略常量 | `src/ControlServer.Contracts/Constants.cs`（`Roles`、`Policies`、`AuthClaims`） |
| 用户面 DTO | `src/ControlServer.Contracts/UserDtos.cs` |
| 会话主体构造 | `src/ControlServer.Server/Services/SessionPrincipal.cs`（**唯一入口**） |
| 账号业务与守卫 | `src/ControlServer.Server/Services/UserAccountService.cs`（Scoped） |
| 密码复杂度 | `src/ControlServer.Core/Services/PasswordPolicy.cs`（**在 Core，可单测**） |
| 用户管理端点 | `src/ControlServer.Server/Api/UsersController.cs`（整类 AdminOnly） |
| 登录 / 改密 | `src/ControlServer.Server/Api/AuthController.cs` |
| 闸门 + 会话复核 | `src/ControlServer.Server/Program.cs`（`OnValidatePrincipal`、改密中间件） |
| 实体 | `src/ControlServer.Infrastructure/Persistence/Entities/AppUser.cs` |
| 播种 | `.../Persistence/DatabaseInitializer.cs` |
| 前端 | `web/src/components/UserManagementPanel.vue`、`ChangePasswordDialog.vue`、`api.ts`、`types.ts` |

**`UserAccountService` 放在 `Server` 而不是 `Core`**：它依赖 Scoped 的 `AppDbContext`，
而铁律 #1 规定 `Core` 不得引用 EF Core。

## 2. 陷阱清单

1. **`IsEnabled` 迁移默认值必须是 `true`**。EF 生成的是 `false` ⇒ 升级会把**所有存量账号停用**，
   谁都登不进来。`MigrationTests.ReadUserEnabled` 守门。
2. **实体上不要给 `IsEnabled` 加 `HasDefaultValue`**。EF 按 CLR 默认值哨兵（`bool` 是 `false`）
   跳过带库端默认值的列 ⇒ "新建已禁用账号"会落库成启用。
3. **`CurrentUser` 必须两处都带 `mustChangePassword`**（`LoginResponse` + `/api/auth/me`）。
   只放在登录响应里 ⇒ **刷新页面即可跳过强制改密**。
   （机制保留；2026-10-09 起默认不再有账号被标记，见 §0。）
4. **改密白名单必须含 `change-password` 自身**，否则"必须先改密"与"改密端点不可访问"成死结。
5. **闸门只拦 `/api`**：`/healthz`、`/readyz`（探活）、SignalR、静态资源都要放行。
6. **`OnValidatePrincipal` 选 fail-open**：DB 异常时放行 + 记 Warning，不把人踢下线。
   ⚠️ 别"顺手改成 fail-closed" —— 那是把 SQLite 偶发 BUSY 升级成全站登出事故。
7. **锁定期间继续输错必须早退**（`if (IsLocked(user)) return null;`），
   否则 `LockedUntil` 每次都被推后 ⇒ **账号可被永久锁死**。
8. **停用必须清 `FailedLoginCount` / `LockedUntil`**，否则重新启用会继承停用前的失败计数。
9. **"改自己名字的大小写"不是重名**：`NameTakenAsync(name, excludeId)` 排除自己，
   所以 `operator-one → OPERATOR-ONE` 返回 200（只改了大小写）；要与**别人**撞名才 400。
10. **自我降级后无法自我恢复**（403）—— 这是 RBAC 正常语义，不是缺陷。
    测试里必须请另一位管理员提回来。
11. **`Policies` 没有全局 FallbackPolicy** ⇒ 新增控制器**必须**自带 `[Authorize]`
    或显式 `[AllowAnonymous]`，否则等于对匿名开放。`AuthorizationCoverageTests` 反射守门。
12. **前端 `emit` 事件名**：声明的是 `'notice'`，调用必须写
    `emit('notice', 'ok'|'err'|'warn', text)` 三个参数。
    写成 `emit('warn', text)` **不会报错、提示也不会显示**（生产构建下完全无声）。
    `npm run typecheck`（vue-tsc）能抓到。

## 3. 时间口径（全应用铁律 #4）

- 一切时间戳走 `BeijingTime.Now`（UTC+8 + NTP 偏移校正），**禁止** `DateTime.Now` / `UtcNow`。
- 实体默认值也已改为 `BeijingTime.Now`（`AppUser.CreatedAt`、`Setting.UpdatedAt`）。
  原因：前端 `fmtDateTime` 是**直接截 ISO 字符串**（刻意不做时区换算，否则非 +8 时区的浏览器显示错时间），
  所以落库成 UTC 会在界面上实打实差 8 小时。
- 守门：`MigrationTests` 的 `ReadUserCreatedAtOffset` 断言 `+08:00`。

## 4. 测试与冒烟

- 测试宿主（`WebApplicationFactory`）**显式关掉闸门**（`ControlServerFactory(..., enforcePasswordChange: false)`），
  否则每次业务断言都会先撞 403。闸门机制由 `ForcedPasswordChangeTests` 覆盖
  （其中"仍生效"一条**直接写库标记账号**，因为已无自动标记路径）。
- 每个测试独占 `ControlServerFactory`（唯一临时数据目录 + 唯一空闲 UDP 端口），程序集级禁并行。
- 冒烟：`deploy/smoke/rbac-smoke.py`（多会话，**会改掉 admin 密码** ⇒ 只对临时实例跑）。
  A 段现在是"出厂态开箱即用"，不再是"必须先改密"。
  `run-all.py` **不再需要**为任何场景传 `--Server:Auth:EnforcePasswordChange=false`。

## 5. 前端构建闸门（本轮新增）

```bash
cd web && npm run typecheck   # vue-tsc --noEmit —— 能抓模板里的 TS 错误与 emit 签名错
cd web && npm run build       # vite build → src/ControlServer.Server/wwwroot
dotnet build ControlServer.sln -c Debug   # wwwroot 是 EmbeddedResource，必须重编才生效
```

`vue-tsc` 已进 `devDependencies`（安装时用 `npm install --ignore-scripts`，见环境事实）。
