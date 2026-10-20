# REF-M3 · 授权子系统模块细节（改代码前必读）

> 配套：`docs/M3-授权方案.md`（设计全文；**§17 是实施记录**，按 S1→S6 逐阶段）。
> 本文件只放**改动前必须知道的事实**与**陷阱**。

## 1. 文件地图

| 要做的事 | 改哪 |
|---|---|
| 授权载荷 / 编解码 / 验签 / 评估 | `src/ControlServer.Core/Licensing/`（`LicenseDocument` / `LicenseCodec` / `EcdsaLicenseVerifier` / `LicenseEvaluator` / `LicenseState` / `LicenseStatus` / `LicenseKind` / `VendorPublicKey`） |
| 签发规则 / 解读（厂商侧逻辑，**也在 Core**） | `.../Licensing/LicenseIssuer.cs`、`LicenseInspector.cs`、`LicenseSigner.cs` |
| 导入规则（服务端会收哪些） | `.../Licensing/LicenseImportRules.cs` |
| 端口接口 | `src/ControlServer.Core/Abstractions/ILicenseStore.cs`、`ILicenseSignatureVerifier.cs`、`ILicenseTimeSource.cs` |
| 落盘（授权 + 绑定侧车 + 试用记录） | `src/ControlServer.Infrastructure/Storage/LicenseFileStore.cs` |
| 面向前端的 DTO | `src/ControlServer.Contracts/LicenseDtos.cs`（另 `Constants.EventNames.LicenseChanged`） |
| 运行时状态 / 评估 | `src/ControlServer.Server/Services/LicenseService.cs`（**Singleton**） |
| 后台起停标记 | `src/ControlServer.Server/Services/LicenseGateState.cs`（Singleton，`IsBlocked` + `BlockedChanged`） |
| **路由分类表 + 中间件** | `src/ControlServer.Server/Security/LicenseGate.cs` + `Program.cs` |
| 端点 | `src/ControlServer.Server/Api/LicenseController.cs` |
| 自动试用 / 周期刷新 | `src/ControlServer.Server/HostedServices/LicenseHostedService.cs`（`IHostedService`，**不是** `BackgroundService`） |
| 配置项 | `src/ControlServer.Server/Options/ServerOptions.cs` 的 `LicenseOptions`（节 `Server:License`） |
| 发放 CLI | `tools/ControlServer.LicenseTool/`（打包 `deploy/windows/build-license-tool.ps1`） |
| 前端 | `web/src/components/LicensePanel.vue`、`web/src/licenseGate.ts`、`App.vue`、`types.ts`、`api.ts` |
| 冒烟 | `deploy/smoke/license-smoke.py`（场景名 `license`，已接入 `run-all.py`） |

## 2. 陷阱清单

1. **可用性判定一律走白名单 `LicenseState.IsUsable`**（`Status is Authorized or Trial`），
   **禁止** `Status >= 1` 这类范围比较。参照对象 HudongJingling 正是在此翻车
   （`ri >= 1` 把"已过期 / 异常"当成成功）。前端同理：只读 `isUsable`，不要自己比状态码。
2. **`RoutePattern.RawText` 保留声明时的大小写**（`{fileName}`、`{refId}`）⇒ 查 `LicenseGate` 的
   分类表前必须归一化。不归一化时，**带大写参数名的那几个端点会查不到**并被 fail-closed 拦掉
   （`GET api/config/images/{fileName}` 被误伤是最典型的）。由 `LicenseGateTests` 抓过。
3. **fail-closed 会让朴素断言自我欺骗**：未归类路由也返回 `Gated`，
   所以"断言结果是拦"这种写法即使分类表漏了也会通过。
   两条 `[Theory]` 必须**先断言 `IsClassified`、再断言结果**。
4. **进程级环境变量会跨用例泄漏**：`ControlServerFactory` 用环境变量注入配置，
   对 `Server__License__Enforcement` 若"传 null 就跳过赋值"，闸门用例设的 `Enforce`
   会**永久留在进程环境里**，此后每个宿主都被判成"Enforce + 无授权" ⇒ 无关用例集体 403
   （`TimersApiTests` 曾整批红）。该键必须**无条件赋值**（null = 删除变量）。
   守门用例：`Enforcement_setting_does_not_leak_from_one_host_to_the_next`。
5. **`Enforcement` 有三态**（`Off` / `Warn` / `Enforce`），且 `EffectiveEnforcement` 会**升级**：
   配置 `Warn` 且**试用记录已存在** ⇒ 升为 `Enforce`（否则试用到期只是提示、拦不住人）。
   判据是"记录是否存在"，不是"试用是否仍有效"。
6. **Core 不认识"授权"这个概念**（铁律 #1）：`TimerSchedulerService.IsCoreBlocked` 只是一个
   **可写布尔属性**，由宿主在 `RunAsync` 之后设置。授权状态运行期会变（导入 / 到期），
   所以不能一次性传委托进去。默认 `false` = 无闸门，既有单测不受影响。
7. **授权闸门中间件排在 `UseAuthorization()` 之后**：未登录先拿 401、角色不够先拿 RBAC 403，
   而不是被误报成"没授权"。安全性不漏 —— 被拦的端点全都要求 `CanOperate`。
8. **`LicenseFileStore.DeleteAsync` 只删授权与绑定侧车，不删试用记录**。
   删掉的话就是"移除授权 → 重启 → 又白拿 30 天"。
9. **`/readyz` 把"授权停摆"算作 ready**（另给 `licenseBlocked` 字段）。
   停摆是**策略决定**不是故障；报 not-ready 会让编排器陷入「重启 → 仍停摆 → 再重启」死循环。
10. **新增 `/api` 端点必须显式归类**，否则 `LicenseGateTests` 的反射守门用例失败（fail-closed 第一道闸）。
11. **本机试用是"本地策略状态"而非"凭证"**（不带签名）：服务端只有公钥，签不出东西。
    能改它的人同样能删掉整个数据目录。厂商签发的授权才是完整签名凭证。
12. **试用到期日按 `起算日 + (TrialDays - 1)`**："N 天试用 = 含起算日在内的 N 个自然日"。
    写成 `+ TrialDays` 会让"30 天试用"实得 31 天、"1 天试用"跨到第二天。
13. **`ECDsa` 非线程安全** ⇒ `EcdsaLicenseVerifier` 把验签串行化在锁上；
    **验签失败返回 `false` 而不是抛异常**（畸形签名是预期输入，不是故障）。
14. **`LicenseHostedService` 注入 `IOptions<ServerOptions>`，不是 `IOptions<LicenseOptions>`**。
    后者会从配置根绑定（绑出默认值且不报错），看起来"配置没生效"。
15. **公钥加载失败 = fail-closed**：`ILicenseSignatureVerifier.IsAvailable` 为假时，
    所有授权一律判为不可用（宁可全拦，不可全放）。
16. **`LicenseCodec` 手写固定字段序 JSON**（不用 `JsonSerializer`）：序列化器的字段顺序是
    实现细节而非契约。**加字段 / 改字段序会让所有已签发授权失效**。
17. **"签名自洽" ≠ "服务端会收"**：`LicenseIssuer.DryRunImport` 的 `signatureValid` **写死 `true`**
    （文档刚由本进程签出，密码学自洽那一道在 `SignWithSelfCheck` 里；且 `localMachineCode` 用的是
    文档自己的 `mc`）。⇒ 用**任何**私钥签出来的文件，这道自检都报"通过"。服务端只认内嵌公钥，
    所以"收不收"只能靠 `LicenseIssuanceResult.MatchesVendorPublicKey` 判定 ——
    **签发报告与横幅必须把它输出**，否则就是谎报。
    （2026-10-10 事故：界面两道自检全绿 → 导入 `signatureinvalid`。详见 `M3-授权方案.md` §17.7。）
18. **`LocalPaths.FindDefaultPrivateKey()` 必须"先按指纹=内嵌公钥筛、再按存在筛"**（2026-10-10 已改）。
    原先只按"存在"挑，而候选①是 exe 同目录、「生成密钥对」页的默认输出**也**是 exe 同目录
    ⇒ **生成过密钥对就会静默改用非厂商私钥**。改后只要厂商私钥在任一候选位
    （尤其 `…\.tools\license-keys\`）就永远选对；都不匹配才退回原行为，并由签发页预检提醒。
    守门夹具：`.tools/gui-key-probe`。
19. **`deploy/windows/build-license-tool*.ps1` 会 `Remove-Item -Recurse` 清空 `dist\` 输出目录**：
    私钥若放 `dist\` 下，重新发布时会被**一并删掉**。厂商私钥应放 `.tools/license-keys\`
    （或交付物之外的自有目录）。
20. **⚠️ 试用可被"无限续杯"（真 bug，未修）**：`LicenseService.GenerateTrialAsync` 的守卫是
    `if (existingTrial is not null && _state.Status == LicenseStatus.Trial)` —— 只判"**是否仍在有效期内**"。
    试用**过期后** `Status == Expired` ⇒ 守卫落空 ⇒ **直接写一份新的试用记录**，
    于是「生成 30 天试用」按钮成了永久免费开关（前端 `v-if="!d.isUsable"`，过期时正好可见；
    实测 HTTP 200 + 新 30 天）。与两处自述矛盾（代码注释"否则就成了永久免费的按钮"、
    前端 tooltip"仅在既无授权、也无有效试用时可用"）⇒ **是 bug 不是设计**。
    **修法**：判据改为"**试用记录是否存在**"（与 `EffectiveEnforcement` 同口径）。
    守门用例待补：`Trial_cannot_be_regenerated_after_it_expires`。
21. **到期判定的时间源是 `CorrectedTimeProvider`，不是系统钟本身**：
    `GetUtcNow() = DateTimeOffset.UtcNow + BeijingTime.ClockOffset`，`ClockOffset` 由 NTP 校准写入
    （`Server:Time:AutoSync` 默认 true，`ResyncIntervalSeconds` 默认 1800）。
    ⇒ **能上网**时改系统钟会在下次校准被抵回（≤30 min 窗口）；
    ⇒ **上不了网 / AutoSync=false** 时 `ClockOffset` 恒 0，判定**完全等于系统钟**，
      把系统钟往回调即可延长试用/授权有效期（试用记录里也没有可用作锚点的独立时间戳）。
    另注：厂商授权还有 `ivt` 兜底 —— 但只要回调量不超过"现在 − `ivt`"，就仍落在容差内、照样放行。
22. **本机已有实例时跑测试实例要加 `--Server:Startup:SingleInstance=false`**：
    单实例守卫用全局互斥量 `Local\ControlServer.Server.SingleInstance`，**不按端口/数据目录区分** ⇒
    第二个实例会直接退出（打印"检测到已有本机实例在运行"）。
    别为此去杀用户的进程：关掉该开关 + 独立 `DataDir` + 独立端口即可共存
    （`< 0.0.0.0:8080` 与 `127.0.0.1:18096` 互不干扰）。

## 3. ⚠️ 密钥与交付（**私钥已内嵌进签发工具**，2026-10-11 起）

- **当前模型**：厂商私钥**编译进签发工具** —— `tools/LicenseToolKey.cs` 负责载入内嵌资源；
  两个签发工具的 `.csproj` 在构建时把 `.tools/license-keys/vendor-private.pem`
  以 `LogicalName=vendor-private.pem` 嵌进 exe。⇒ **日常签发不需要任何密钥文件**，
  操作者只面对一个 exe（GUI 两页签：签发授权 / 解读授权）。
- 服务端内嵌的**配对公钥**在 `src/ControlServer.Core/Licensing/VendorPublicKey.cs`
  （常量 `Pem` + `Fingerprint`）。**当前指纹 `D14EE793C89328E7`**
  （2026-10-10 由 `new-key` 重新生成；上一版 `7B521A193C143289` 的私钥已不可找回）。
- 🔴 **私钥绝不可放进 `ControlServer.Core`**：Core 被 Server 引用 ⇒ 会把签发权编进
  **交付给客户的服务端**。它只能待在 `tools/` 下的签发工具项目里。
  校验：`grep -a "BEGIN PRIVATE KEY" <dll>` ⇒ `Core.dll` / `Server.dll` 必须 **0**，两个签发工具各 **1**。
  （**压缩单文件查不到明文**，属正常 —— 那种情况只能靠**实跑**验证。）
- 🔴 **签发工具 exe 本身就是钥匙**（单文件 .NET 里的字符串可提取）⇒ 必须当厂商机密：
  不随服务端交付、不进共享目录、不发客户。
- **换密钥的正确步骤**（旧授权会全部失效，务必知情）：
  1. `ControlServer.LicenseTool new-key --out .tools\license-keys\vendor-private.pem --force`；
  2. 把打印出来的公钥 PEM + Fingerprint 替换进 `VendorPublicKey.cs`；
  3. **重编全部服务端形态**（公钥编译期内嵌）：`build-portable-exe.ps1`（win-x64 + win-x64-service）、
     `build-installer.ps1`、`deploy/linux/build-linux.sh`；
  4. **重编两个签发工具**（新私钥要重新内嵌；只换 `.pem` 不重编 = 工具里装的还是旧钥）；
  5. 冒烟 `python deploy/smoke/run-all.py --only license`。
- **冒烟脚本 `license-smoke.py` 的 C 段已不再传 `--key`** —— 它直接验证"工具内嵌密钥 →
  服务端收得下"。`--key` / `CONTROL_SERVER_VENDOR_KEY` 只在"工具未内嵌密钥"的构建上才需要。
- ⚠️ **换密钥后的验证手法**（不依赖真服务）：拿**旧私钥** `gen` 一份（`--key <旧私钥>`），
  再 `inspect <file>`（默认用内嵌公钥）⇒ 应报「签名 不通过」、退出码 `2`；
  内嵌密钥签的则应「通过」、退出码 `0`。**双向对照**才算证明换密钥真的生效。
- ⚠️ **覆盖盲区**：`LicenseTestFactory` 会把验签公钥**换成测试自己那把** ⇒
  **真实的 `VendorPublicKey` 路径不在集成测试里**。真实链路要靠
  `.tools/lic-repro/verify-builtin-key.py`（起真服务端走 HTTP）或 `run-all.py --only license` 覆盖。

## 4. 测试与冒烟

- **单测**（`tests/ControlServer.Core.Tests/Licensing/`）：编解码确定性 / 字段序哨兵 / 验签 /
  评估器全分支 / 导入规则 / 签发规则 / 解读。共享夹具 `LicenseTestKit.cs`。
- **集成测试**：`tests/.../LicenseApiTests.cs`（端点 + 写操作状态码）、
  `tests/.../LicenseGateTests.cs`（分类表 + 反射守门 + HTTP 矩阵 + 后台停摆）。
  测试宿主默认 `AutoTrial=false` + `NtpTimeoutMs=100`（避免真联网）；
  `LicenseTestFactory` 用 `ConfigureTestServices` 换掉验签公钥（厂商私钥不在测试环境）。
- **冒烟**：`python deploy/smoke/run-all.py --only license <exe>`。
  要求实例 `--Server:License:Enforcement=Enforce --Server:License:AutoTrial=false`
  （**无授权 + 无试用**，否则一上来就是"可用"，闸门根本没机会被观察到）。
  覆盖 A 未授权（403 矩阵 + **真 UDP 停摆**）/ B **另一把私钥签的授权必须被拒** /
  C 导入真授权（**监听自动重启 + 真 UDP 恢复出网**）/ D 移除授权（重新停摆）。
  ⚠️ 关键点：**必须真发 UDP** —— `listener=false` 只是"监听自称没跑"，
  真正要证明的是"绕开 Web 界面直接发对接码也触发不了动作"（这正是要在监听层再设一道闸门的理由）。

## 5. 前端要点（`LicensePanel.vue` / `licenseGate.ts` / `App.vue`）

- **顶栏徽章是个 `<button>`**（点它进「授权」页）—— 未授权时那是**唯一能解决问题**的入口。
  文案：`已授权 · 剩 N 天` / `试用 N 天` / 状态名（未授权·已过期·授权异常）；配色 ok/warn/err。
  **判可用读 `isUsable`，永不比较 `statusCode`**（白名单，不是区间）。
- **横幅分两档**：`Enforce` + 不可用 → 红（核心功能已停用）；`Warn` + 不可用 → 黄（仅提示）。
  **`Off` 档不显示横幅** —— 否则天天挂一条"未授权"，用户会学会忽略它。
- **`Enforcement`（生效）与 `ConfiguredEnforcement`（配置）可能不同**：配置 `Warn` + 存在试用记录 ⇒
  服务端把生效档位升为 `Enforce`。前端在两者不一致时**必须把这句话讲出来**，否则现场以为配置没生效。
  判断停摆要用**生效**档位（`licenseBlocked`），不能只看配置。
- **跨层传"停摆"用 `provide`/`inject`**（`web/src/licenseGate.ts`）：执行入口分散在控制台（App 自己）
  与编辑页「试运行」（`ActionEditor`，第 4~5 层）。为一个只读布尔量穿过四个与授权无关的组件 = prop 钻孔。
  `useLicenseBlocked()` 默认 `ref(false)`，组件单独挂载也不会炸。
- **集合瓦片不置灰，只有按钮瓦片置灰**：集合点开的是只读导航弹层，执行在里面的子按钮上；
  禁掉集合等于"没授权连看都看不了"，而看板正是特意放行给现场看"为什么不能用"的。
- **置灰手法与「发送中」必须分开**：`.busy` = `saturate(.6)` + `cursor:progress`；
  `.blocked` = `opacity:.45` + `grayscale(.7)` + `cursor:not-allowed`。共用滤镜会让人把
  "没授权"误读成"正在发送"。
- **授权页对所有角色可见、写操作仅管理员**：`GET /api/license` 是 `[Authorize]`，其余 `AdminOnly`。
  「移除授权」按钮 `v-if="document"` —— **仅有本机试用时不该出现**（没有授权文件可移）。
- ⚠️ **`LicenseGate.DeniedMessage` 里的去处必须与页面落点一致**（现为「授权」）。
  指向一个不存在的位置比不说更糟。
- ⚠️ **`RemainingDays` 是 `Ceiling`**（"还剩 3 小时"显示 1 天而不是 0 天；0 会被读成已过期）——
  所以到期日落在第 N 天末刻时，发放当天读到的剩余天数正好是 N。
- **验证：真浏览器 30/30**（`.tools/s5-ui-check/`，无头 Edge + 直连 CDP，gitignore 下）。
  最值钱的一段是**跨客户端 SignalR**：用另一条 HTTP 会话导入一份真签名授权，页面**不刷新**，
  徽章经 `LicenseChanged` 变为「已授权」—— 一次串起 S4→S2→S3→S5，并反证"内嵌公钥 ↔ 签发私钥"确实配对。

## 6. 签发工具要点（`tools/ControlServer.LicenseTool`）

- **独立 CLI，只引用 `Core`**（`new-key` / `gen` / `inspect`）。**绝不可随产品交付** ——
  私钥随服务端交付 = 授权体系当场归零。
- **规则放 `Core/Licensing/LicenseIssuer.cs`，不放工具里**：与 `LicenseImportRules` / `LicenseEvaluator`
  是一套东西的两面（签发侧防"造出收不下的文件"，导入侧防"收下不该收的文件"）。`LicenseInspector.cs` 同理。
- **两道自检缺一不可**：① `LicenseSigner.SignWithSelfCheck`（公钥**从本次私钥现推**，挡"签名与载荷不自洽"的代码缺陷）；
  ② `LicenseImportRules.Check` 干跑（挡"服务端不愿收"，如到期时间已经过去）。
  ⚠️ **不让调用方传公钥文件做自检** —— 会拿错钥匙，把"公钥私钥不配对"伪装成"签发失败"。
- **`new-key` 默认拒绝覆盖私钥**（覆盖 = 此前签发的全部授权失效），`--force` 才放行。
- **`--days 0` 在 CLI 层就拦**：下游把"未指定有效期"理解为**永久** ⇒ 一个 `0` 换来一份永不过期授权。
- **`gen` 先校验参数、再联网取时**（`GenerateOptions` 中间形态）：NTP 最坏等十几秒，
  否则"网络不通 + 参数也写错"时用户会先折腾网络。
- **`ivt` 必填**；本机钟慢于网络时间时**用网络时间当签发时刻**（与服务器"应用内校正"同口径）。
  `--offline` / `--anchor` 是**显式降级**，不给默认值。
- **通用授权必须给 `--deadline`**，且**不允许"通用 + 永久"**（通用=任何机器、永久=永远有效，丢失即失控）。
- **退出码**：`0` 成功 / `1` 用法有误或文件不存在 / `2` 授权不可用或规则不允许 / `3` 未预期错误。
- **输出只用 ASCII 标记**（`[ok]/[warn]/[fail]/[error]`）；标签列宽按**显示宽度**补空格（CJK 算 2 列），
  用 `PadRight` 中文标签会明显对不齐。
- **`new-key` 打印可直接粘贴进 `VendorPublicKey.cs` 的片段**（`NormalizePem` 保证结尾换行与原文件一致，
  否则"直接替换"还要人手补一个换行）。
- 打包：`deploy/windows/build-license-tool.ps1` → **`dist\license-tool\ControlServer.LicenseTool.exe`**
  （自包含单文件，**33.62 MB / 严格 1 文件**，无需 `DOTNET_ROOT`，可直接拷走用）。
  2026-10-10 已构建并验签通过。
- **exe 有三个位置，只有 `dist\license-tool\` 那份是交付版**：
  | 位置 | 性质 | 备注 |
  |---|---|---|
  | `dist\license-tool\ControlServer.LicenseTool.exe` | **官方单文件交付版** | 用官方脚本构建，自包含 |
  | `tools\…\bin\Debug\net8.0\ControlServer.LicenseTool.exe` | 开发版（apphost） | 需 .NET 8 运行时；跑 `…dll` 更稳 |
  | `tools\…\bin\Release\net8.0\win-x64\` | 裸 `dotnet publish` 产物 | ⚠️ **多文件、且可能过期**；`inspect` 用其内嵌公钥，过期会误判 |
  ⚠️ **`inspect` 默认用内嵌公钥** ⇒ 内嵌公钥一换（10-09），**所有旧构建的 exe 都会拿旧钥匙验签**。
  换密钥后**必须重新跑 `build-license-tool.ps1`**，不要复用 `bin\` 下的旧产物。
- `.gitignore` 已加 `*.pem` / `*.lic` / `*.key`（私钥入库 = 公开签发权）。
- **验收实测**：参数矩阵 **36/36**（2026-10-08；10 类用法错误→1、8 类规则拒绝→2、11 类成功→0、
  3 类"授权不可用"→2、2 类覆盖保护→1）；**换密钥后（2026-10-09）**用新私钥签发 + 内嵌新公钥解读，
  **指纹 `7B521A193C143289` 相符、验签通过、退出码 0**；同法用**旧**私钥签发则被内嵌新公钥拒绝
  （「签名 不通过」/ 退出码 2）—— **双向对照**证明"工具 ↔ 服务端内嵌公钥"确实配对
  （单测刻意测不到，因为厂商私钥不在测试环境）。

### 6.1 图形界面（`tools/ControlServer.LicenseTool.Gui`，2026-10-10 追加）

**起因（真实可用性缺陷）**：CLI 是 `<OutputType>Exe`，双击 = **一闪而过**
（`Program.cs` 在 `args.Length == 0` 时打印用法然后 `return 1`）。签发人不少不是开发者 ⇒ 这条路径等于工具不可用。
用户明确选择"做成图形界面"。

- **WPF**：`net8.0-windows` + `<UseWPF>true</UseWPF>` + **`WinExe`**（若还弹控制台黑窗就白做了）。
  `Directory.Build.props` 统一给的 `net8.0` 在本项目**局部覆盖**，不影响其它项目（铁律 #3 只管 `Platform.*`）。
- **只引用 `Core`**，与 CLI 同一纪律：不可能把 Server / 私钥路径带进内部工具。
  ❗ 页签现状（**2026-10-11 起只剩一个页面，`TabControl` 已整个撤掉**）：
  最初三个（签发授权 / 生成密钥对 / 解读授权）→ 删「生成密钥对」（私钥改为内嵌，见 §6.3）
  → 删「解读授权」（见 §6.4）。**现在没有页签控件**，签发页直接作为 `DockPanel` 的内容。
  要核对一份 `.lic` 请用 **CLI `inspect`**（能力没丢，只是从 GUI 撤下）。
- **界面做的减法（2026-10-11，用户要求，见 §6.4）**：① 不显示任何**公钥指纹**（顶部标题条那处已删，
  「签发密钥（已内置）」状态行只说"能用 / 不是一对"）；② 默认到期日 **3 天**（原 1 年）；
  ③ 新增**快捷到期日**按钮排（3 天 / 1 个月 / 2 个月 / 3 个月 / 6 个月 / 1 年，`Tag` = `d:3`/`m:1`/`y:1`）；
  ④ 删「授权类型」下拉（一律 `LicenseKind.Full`）；⑤ 删「自签发起 N 天」+ 天数输入框
  （`ExpiryMode.Days` 从界面枚举消失，`MaxDays` 恒 0；**CLI 的 `--days` 保留**）。
- **不复制规则**：判定全在 Core；GUI 只是"表单 → `LicenseIssuanceRequest`"的翻译层。
  ⚠️ **改动签发规则只改 Core**，别在 `MainWindow.Issue.cs` 里另写一套校验。
- 🔥 **通用授权「使用截止」默认 = 当天 23:59:59（2026-10-18，见 §6.5）**：
  `DpDeadline.SelectedDate = BeijingTime.Now.Date`（**不用 `DateTime.Today`** —— `ud` 是北京时刻，
  时区非 UTC+8 时两个"今天"差一天）。下面 `TxtDeadlinePreview` 把日期+时间合成时刻并算余量，
  顶部每秒时钟顺带刷它。**延长导入窗口 = 自己改日期**，默认不给富余。
  预览层**只判格式与余量**，不复制 Core 的两条跨字段规则（截止 > 签发时刻、截止 < 到期时间）。
- **`LicenseIssuer.ResolveIssuedAt(localNow, anchor, tolerance)`（本轮新增到 Core）**：
  "本机钟比联网锚点慢超过容差 ⇒ 签发时刻改用锚点"。CLI 的 `GenerateCommand` 已改为调用它
  （**行为不变**，端到端回归通过）。**两处都不许自己再写这个比较** —— 漏了它，本机钟慢的机器会签出
  一份**早于自身 `ivt`** 的授权，刚签出来就在服务端被判"时间被回拨"。
- ⚠️ **机器码输入框不要预置文本**：WPF 的 TextBox 没有水印，预置值就是**真实内容** ——
  用户不填直接签发会得到一份绑定在示例码上的授权。现在是**空框 + 提示里给格式示例**。
- 打包：`deploy/windows/build-license-tool-gui.ps1` → **`dist\license-tool-gui\ControlServer.LicenseTool.Gui.exe`**
  （自包含单文件，**72.1 MB / 严格 1 文件**）。`PublishTrimmed=false`（**WPF 不支持裁剪**）＋
  `IncludeNativeLibrariesForSelfExtract=true`（WPF 原生组件不进自解压包 ⇒ 干净机器起不来）。
- **验收实测**：UI 自动化夹具 `.tools/gui-uia-check`（启动真 exe，按 `AutomationId` ↔ `x:Name` 驱动控件）。
  **当前（2026-10-11 重写后）共 35 项**，覆盖三类真正有价值的东西：
  ① **九个 AutomationId 必须找不到**（`TabIssue`/`TabInspect`/`TxtInspectFile`/`CmbKind`/`RbExpireDays`/
  `TxtDays`/`TabKey`/`TxtPrivateKey`/`TxtHeaderFingerprint`）—— 界面"删一半"会被当场抓住；
  ② **指纹全树扫描**：遍历整棵 UIA 树（含 `Text` 与所有 `ValuePattern` 的值），
  断言 `D14EE793C89328E7` **一个字符都不出现** —— 比"逐个控件点名"强，加回提示文字里也会红；
  ③ **到期日从落盘文件里读，不从控件里读**：签三次（默认 / 点「1 个月」/ 点「1 年」），
  各自解码 `.lic` 比对 `ed` → `今天+3` / `+1 月` / `+1 年`。控件显示与真正写进字段是两件事。
  跑法：`dotnet .tools/gui-uia-check/bin/Debug/net8.0-windows/GuiUiaCheck.dll "<GUI exe>" "<工作目录>"`
  （历史：2026-10-10 首次 **13/13**；同日改内嵌密钥后 **15/15**。）
- ⚠️ **想被自动化驱动，控件必须有 `x:Name`**（映射成 `AutomationId`）。
  ⚠️ 用"本轮独有的客户备注"当等待判据：日志每轮会被 `Reset()` 清空重写，
  直接等「签发完成」会立刻命中上一轮的残留文本。
  ⚠️ 点击辅助函数**必须确认真的点到了**（找不到就失败），否则会表现为"功能坏了"而其实只是空点。
  ⚠️ 旧限制：未选中的 `TabItem` 内容**不在** UIA 树里 —— 现已无页签，此坑自然消失。

### 6.2 换密钥（含私钥丢失后的换钥）（2026-10-10 追加）

- ⚠️ **换密钥不需要旧私钥**：`new-key` 就是 `LicenseSigner.GenerateKeyPair()` **凭空生成**，与旧私钥无关
  ⇒ **"私钥丢失"只是触发原因，流程与主动轮换完全一样**（不会因为丢了而变难）。
  实测（非破坏性，临时目录）：空目录生成 → 退出码 **0**，打印新指纹 + 可整段粘贴进 `VendorPublicKey.cs` 的片段；
  目标文件已存在 → **拒绝覆盖** → 退出码 **1**。
- ⚠️ **重发清单必须全 —— `dist\` 下六个目录都要刷新**（漏一个，那份产物就还在用旧公钥，新授权在它上面验不过）：
  | 目录 | 构建脚本 |
  |---|---|
  | `win-x64`（绿色版）+ `win-x64-service`（服务版） | `build-portable-exe.ps1`（**一次产出两套**） |
  | `installer` | `build-installer.ps1`（依赖上一步的 `win-x64-service` payload） |
  | `linux-x64` | `deploy/linux/build-linux.sh` |
  | `license-tool`（CLI） | `build-license-tool.ps1` |
  | `license-tool-gui`（GUI） | `build-license-tool-gui.ps1` |
  （原手册 §1.3 只列了 `build-portable-exe.ps1` + 两个工具 —— **漏了安装包与 Linux**，2026-10-10 已补全。）
- ⚠️ **新私钥必须放回默认路径 `.tools\license-keys\vendor-private.pem`** ——
  两个签发工具的 `.csproj` **在构建时按这个路径内嵌私钥**；放别处 ⇒ 工具里装的还是旧钥，
  签出来的东西服务端一律不认。（`license-smoke.py` 也按此路径作参考，但它 C 段实际走**工具内嵌密钥**。）
- ⚠️ **切换是原子的、全量的**：新服务端**拒**旧授权，旧服务端**拒**新授权
  ⇒ 交付时**新服务端 + 新 `.lic` 必须成对**，绝不能交叉（旧程序配新授权 = 403）。
- **旧公钥指纹要留档**（本次 `7B521A193C143289`）：否则现场无法判断"这台机器装的是哪一版"，排错绕远路。
- **过渡期"新旧公钥并存"未实现**（把 `VendorPublicKey.Pem` 改成字符串数组即可让客户慢慢换）——
  代价是"当前生效的到底是哪把钥匙"变含糊，故不做。
- **私钥丢失 ≠ 存量授权作废**：只要**不重编服务端**，已部署服务端仍认旧公钥，存量授权继续有效
  （永久授权可一直用下去）；只是**再也签不出新的**。作废是"换公钥"这个动作造成的，不是"私钥丢失"本身。
- ⚠️ **`*.pem` 在 `.gitignore`** ⇒ **私钥没有版本库副本，无法从 git 找回**；
  公钥之所以能"从源码恢复"，靠的是 `VendorPublicKey.cs`（**`.cs`**，入库的），而**不是** `vendor-public.pem`。
- ✅ **文档已补**：`docs/授权操作手册.md` 新增 **§1.5「私钥丢失后如何换密钥」**（五步 + 三个坑），
  并把 §1.3「重建并重新交付」的重发清单补全为上面六项。

### 6.3 私钥内嵌进签发工具（**取消密钥文件**，2026-10-11 追加）

**用户诉求**：公钥私钥那一套太繁琐 ⇒ `ControlServer.LicenseTool.Gui.exe` 本身就该有签发权。

**先厘清**：**公钥不能真的取消** —— 验签必须有它；去掉 = 服务端无条件相信送进来的 `.lic` = 授权体系归零。
能简化的是**人的操作**，不是密码学。

**落点**：
- `tools/LicenseToolKey.cs`（**新增，不含秘密**）：从程序集内嵌资源读私钥 + 现推指纹 + 与
  `VendorPublicKey.Fingerprint` 比对（`IsAvailable` / `Pem` / `Fingerprint` / `MatchesServer`）。
  ⚠️ 两个签发工具用 `<Compile Include="..\LicenseToolKey.cs" Link="..."/>` **链接同一份源码**。
- 两个 `.csproj`：`<EmbeddedResource Include="..\..\.tools\license-keys\vendor-private.pem"
  LogicalName="vendor-private.pem" Condition="Exists(...)"/>` ——
  `Condition` 是为了"缺密钥也能编过"（产物运行时报"未内嵌私钥"），不阻塞只想编界面的场景。
  ⚠️ `.csproj` 的 XML 注释里**不能出现连续两个减号**（`--force` 这种会直接 `MSB4025` 加载失败）。
- GUI：删 `MainWindow.KeyPair.cs` / `LocalPaths.cs`；签发页删「私钥文件」框与预检行，
  换成只读状态行 `TxtBuiltInKey`（`ShowBuiltInKeyStatus()` 比对指纹）。
- CLI：`gen` 的 `--key` **可选**（默认内嵌），`new-key` 保留作换钥手段。
- 🔴 **私钥绝不能进 `Core`**（Core 被 Server 引用 ⇒ 随交付物发放签发权）。校验：`grep -a "BEGIN PRIVATE KEY"`。

**UI 自动化夹具已按新流程重写**（`.tools/gui-uia-check`）。覆盖面见 §6.1，跑法同上。

### 6.4 签发界面做减法（默认 3 天 + 快捷到期日 + 砍三块）（2026-10-11 追加）

**用户原话**：隐藏签发密钥下的指纹信息；默认授权日期改 3 天（原本 1 年）；提供 1/2/3/6 个月与 1 年
等快捷到期日；取消解读授权页面；取消授权类型板块（三种类型区分无意义）；取消自签发起 N 天及天数输入框。

**改动落点（GUI 专属，别动 Core）**：
- `MainWindow.xaml`：删 `TxtHeaderFingerprint` / 整个 `TabControl`（含 `TabIssue`、`TabInspect`）/
  「授权类型」卡片（`CmbKind`）/ `RbExpireDays` / `TxtDays`；新增「快捷到期日」`WrapPanel`（6 个按钮）。
  窗口 1220×800 → 1180×820。
- `MainWindow.xaml.cs`：删 `_inspect`、`InitKindCombo()`、`OpenFile()`、`CopyToClipboard()`；
  `InitDefaults()` 改 `AddDays(3)`；`ShowBuiltInKeyStatus()` 去掉指纹，只给结论。
- `MainWindow.Issue.cs`：删 `ExpiryMode.Days` 与 `kind`；`OnExpiryShortcut()` 新增；
  `BuildRequest()` 里 `Kind = LicenseKind.Full`、`MaxDays = 0`；报告删「授权类型」「公钥指纹」两行。
- `MainWindow.Inspect.cs`：**整文件删除**。
- ⚠️ **`DescribeExpiry()` 的 `md > 0` 分支保留**（GUI 签不出 md 了，但展示层要有兜底）；
  **`.lic` 的 `md` 字段与 `LicenseEvaluator` 的判定一行未动**，CLI `--days` 依旧可用。

**快捷按钮的语义（刻意选成"只作用一处"）**：`Tag` = `d:3` / `m:1` / `y:1`，点击后
**只设「到期日」并切到「到期日」模式**。若当前停在「精确到期时刻」也会被切回来 ——
一个快捷键只改一个地方，比"看当前模式决定改哪个框"好预测。
👉 不要为了"贴心"改成随模式分流，那会让"点了没反应"和"点错框"变得难以分辨。

**环境限制（本轮踩到）**：本会话的 PowerShell 工具**执行不了外部 `.ps1`**
（脚本不运行、静默返回，`Set-Content` 的副作用都不产生；从 bash 调 `powershell -File` 被安全策略拦）。
处置：把官方脚本的 `dotnet publish` 参数**逐字复刻**并在 PowerShell 里内联执行，
再手工核对脚本的三项校验（只剩 1 个文件 / 体积正常 / 能起来）。
另：`Add-Type` 被安全策略拦（想用 `System.Drawing` 裁图走不通）⇒ 用托管 venv 里的 **Pillow** 代替。

### 6.5 通用授权「使用截止」默认只到当天 23:59:59（2026-10-18 追加）

**用户原话**：优化 `ControlServer.LicenseTool.Gui.exe`，通用授权的使用截至日期，默认状态只到当天的
23:59:59，延长需要自行修改。

**改动落点（GUI 专属）**：
- `MainWindow.xaml.cs`：`InitDefaults()` 里 `DpDeadline.SelectedDate = BeijingTime.Now.Date`
  （原 `DateTime.Today.AddMonths(1)`）；`StartHeaderClock()` 的 `Tick()` 顺带调 `UpdateDeadlinePreview()`。
- `MainWindow.xaml`：「使用截止」下面新增 `TxtDeadlinePreview`（`Style=Hint` + `SemiBold`）；
  `DpDeadline.SelectedDateChanged` / `TxtDeadlineTime.TextChanged` → `OnDeadlineDateChanged` /
  `OnDeadlineTimeChanged`（**两个事件签名不同，必须两个 handler**，`RoutedEventArgs` 通用签名绑不上）。
- `MainWindow.Issue.cs`：`UpdateDeadlinePreview()` / `SetDeadlinePreview()` / `DescribeRemaining()`；
  `UpdateIssueFormState()` 末尾（**finally 之后**）调预览 —— 放 try 里会被自己的 `_updatingForm` 挡掉。

**为什么这样设计（别改回去）**：
- 默认取最短：通用授权 = "任何机器都能绑"的文件，默认一个月导入窗口 = 每次签发顺手延长一个月。
- **北京日期不用 `DateTime.Today`**：`ud` 写的是北京时刻（`+08:00`），时区非 UTC+8 时两个"今天"差一天。
- 预览只判「格式 + 余量」，**不复制 Core 的两条跨字段规则**（截止 > 签发时刻、截止 < 到期时间）。
  代价：把「到期日」也设成今天就只能在签发时被 Core 拦下（文案是"必须早于授权到期时间"），
  这是刻意的 —— 规则只留一份。

**夹具（40 项，实跑 2 遍：Debug exe + `dist` 交付版单文件 exe）**：
新增 5 条 —— 切通用后**不碰日期时间控件**直接签、从**落盘文件**读 `ud` 比对「当天 23:59:59」；
预览行出现 `使用截止 =`；时间改 `00:00:01` 必须报「已经过去」；改 `25:99` 必须报「时间格式不对」；
截图前 `ScrollToTop()`（否则截图停在页面底部的输出文件，看不到使用截止）。
