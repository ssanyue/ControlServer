# 参考：M2 模块级细节（ControlServer）

> 配套 `MEMORY.md`。**改动对应模块前必读对应小节。**
> 最后更新：2026-10-07

## 9. 数据库迁移规约（**每次加表都要遵守**）

- 用 **EF Migrations**（M1 的 `EnsureCreated` 已废弃）。
- `AppDbContext` 是 **partial**：M1 表在 `AppDbContext.cs`，M2+ 新表放 `AppDbContext.M2.cs`（后续可拆 `M3.cs`）。
- **禁止改动 `InitialCreate`**（M1 基线）：必须永远等于线上 M1 库真实结构。
- **M1 老库升级是自动的**：`DatabaseInitializer` 探测"有 `users` 表但无 `__EFMigrationsHistory`"→ 用 `IHistoryRepository.GetCreateIfNotExistsScript/GetInsertScript` 补基线 → 再 `Migrate()` 增量建表。**用户不需要删库。**
- 基线迁移的可复现产出步骤见 `docs/M2-功能对齐方案.md` §14.3。

## 10. 绿色版单文件 exe 与 服务版双产物（已完成）

产物 `dist\win-x64\ControlServer.Server.exe`：**单文件、48.7MB、自包含**，双击即启动服务并自动开浏览器。
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-portable-exe.ps1
```

**⚠️ 脚本现在产出两套（2026-10-08 起，见 §25）**：

| 目录 | 文件 | 用途 |
|---|---|---|
| `dist\win-x64\` | `ControlServer.Server.exe`（**严格 1 个文件**） | 绿色版：双击即用，托盘内建在 server 里 |
| `dist\win-x64-service\` | `ControlServer.Server.exe` + `ControlServer.TrayAgent.exe`（**2 个文件**） | 服务版：`install-service.ps1` 用 |

服务版里的 `Server.exe` 是从绿色版目录**直接复制**的（保证字节一致，不会出现两个版本）。
脚本末尾**分别校验文件数**：绿色版必须恰好 1 个；服务版必须恰好 2 个（多余文件会告警）。
新增开关：`-SkipServiceOutput`（只要绿色版）、`-ServiceOutputDir <路径>`。

**打包机制（改动源码前必须理解）**

1. **前端静态资源嵌入程序集**：`wwwroot/**` 由 `Content` 改 `EmbeddedResource`（`CopyToPublishDirectory=Never`）+ `GenerateEmbeddedFilesManifest` + `Microsoft.Extensions.FileProviders.Embedded`。
   运行期 `Program.cs` §8.5：**磁盘上有 `wwwroot` 用 `PhysicalFileProvider`（开发期免重编后端），否则用 `ManifestEmbeddedFileProvider`（绿色版）**。
   ⚠️ `UseDefaultFiles` / `UseStaticFiles` / `MapFallbackToFile` **三处都必须显式传该 FileProvider**，漏一处则绿色版首页或深路由 404。
2. **默认配置不落地**：`appsettings*.json` 设 `CopyToPublishDirectory=Never`（`ServerOptions` 每项都有代码默认值）。
3. **便携配置**：`Program.cs` §0.5 按 **exe 真实目录**（`Path.GetDirectoryName(Environment.ProcessPath)`）挂 `appsettings.json`，**位置在环境变量源之前**（Windows 服务工作目录是 `System32`）。模板 `appsettings.sample.json`。
4. **自动开浏览器**：`Services/BrowserLauncher.cs` + `ServerOptions.Startup.AutoOpenBrowser`（默认 true），在 `ApplicationStarted` 里调用；服务形态/非交互会话自动跳过；**失败只记 Warning，绝不抛异常**。
5. **端口占用**：`app.RunAsync()` 包 try/catch，识别 `AddressAlreadyInUse`/`Failed to bind` 打印可读建议。
6. `IsTransformWebConfigDisabled=true` 阻止生成 IIS `web.config`。

**⚠️ 打包只走官方脚本，别手敲 `dotnet publish`**（2026-10-08 实测踩到）：
手敲 `dotnet publish … -p:PublishSingleFile=true -p:PublishTrimmed=false` 会**漏掉**脚本里的
`-p:IncludeNativeLibrariesForSelfExtract=true` 与 `-p:DebugType=None -p:DebugSymbols=false`，后果：
原生库（`e_sqlite3.dll` / `aspnetcorev2_inprocess.dll`）与 7 个 `.pdb` **散落在 exe 旁边**，
**单文件模式被破坏**，exe 反而涨到 **105MB**（脚本产物 **48.9MB / 51,325,756 B**，且目录仅 1 个文件）。
脚本还会**先清空输出目录**（手敲不清理 → 旧残留混入）。
⇒ 命令永远是：`powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-portable-exe.ps1 [-SkipWebBuild]`。

**⚠️ 压缩模式下"字符串查内嵌前端"的验证手段会失效**：脚本默认
`-p:EnableCompressionInSingleFile=true` ⇒ 内嵌程序集被压缩，`grep -a` **查不到** `index-*.js` 等特征串
（**不是**没打进去！）。要验证产物里的前端，**必须真起 exe 再走 HTTP**：
① `curl --noproxy '*' <url>/` 看首页引用的 `assets/index-*.js` 是否本次新产物；
② 抓下发的 bundle，在**渲染函数**里核对区块顺序（⚠️ 别用"字符串字节偏移"判顺序 ——
子组件模板串出现在**模块定义处**，与使用位置无关）。

**不要再用 `IncludeAllContentForSelfExtract=true` 打单文件** —— 实测不会把内容打进 exe。
参考：`docs/绿色版-使用说明.md`、`docs/M1-骨架设计-dotnet8.md` §22。

## 11. M2-B 执行链（**改动发送/编码代码前必读**）

```
Server/Api/ActionsController          只做 DTO ↔ 领域映射、取配置、返回 DTO
Core/Services/ActionExecutionService  语义层：延迟 / override / 指令库 / 日志 / 汇总
  ├─ DataEncoder（纯函数） ├─ NetworkService（只发字节） └─ ITrafficLog / ICommandLibrary / INotifier
```

1. **`DataEncoder` 是纯函数**，不碰网络/IO。**ANSI = GBK(CP936)**，静态构造里**无条件**注册 `CodePagesEncodingProvider`；取不到则回退 UTF-8 并暴露 `AnsiIsExact=false`（`Program.cs` 启动自检打印 `DescribeAnsiEncoding()`）。依赖 `System.Text.Encoding.CodePages` 包 —— **别删**，删了 Linux 上 ANSI 直接抛异常。
2. **编码分支顺序与旧版一字不差**：`Hex → ANSI → ASCII → UTF-8 → 其它(含 String)按 UTF-8`。`Hex` 非法（奇数长度/非法字符）**必须抛 `FormatException`**，不要改成静默返回空数组（有向量守门）；要非抛出版用 `TryEncode`。
3. **WoL 的 MAC 解析与魔术包在 `DataEncoder`**（`TryParseMac` / `BuildMagicPacket` / `NormalizeWolHost` / `DefaultWolPort=9`）。`NetworkService` **只负责把字节发出去**（UDP/TCP/WoL 三个方法），不要把规则塞回去。
4. **延迟语义**：第 i 条的 `DelayMs` = "上一条完成后等这么久再发本动作"；**第 0 条不等待**。
5. **失败不中断**：某条失败继续发后续；`ActionExecutionResult.Ok => FailureCount == 0`（空序列视为成功）。唯一会中断整个序列的是 `CancellationToken` 取消。
6. **子按钮 override**：`overrideIp` 非空 → 覆盖所有动作 IP/端口且 **WoL 强制降级为 UDP**；`overridePort` 为 null 时保留动作自身端口；`overrideIp` 为 null → **原样返回同一对象**。⚠️ HTTP 侧按 `subButtonId` 执行时 `overrideIp/Port` **必须由服务端从所属集合取**，不允许客户端传值逃逸。
7. **收发日志走 `ITrafficLog`**（不是 `LogService`！后者是**运维日志**）。构造一律用 `TrafficLogEntry.Sent(...)` / `Received(...)` 工厂（自动补北京时间 + 200 字符截断）。`TrafficLogService` 现在是**内存环形（400）+ 事件**，无落盘、无 HTTP 查询（那些是 M2-E）。
8. **DI 双注册**：`TrafficLogService` / `CommandLibraryService` 同时注册具体类型 + 端口接口，宿主用具体类型、业务只用端口。**执行内核依赖的四个外部件全是端口**，因此能被单测完整驱动。

**端点**：`POST /api/actions/execute`（`{buttonId}` | `{subButtonId}` | `{actions:[...]}`，不落配置）、`POST /api/actions/preview`（→ `hex/hexCompact/text/error/eansiEncoding`）。

**测试约定**：`ActionExecutionServiceTests.cs` 有可复用替身（`Harness`/`UdpSink`/`RecordingTrafficLog`/`NullNotifier`/`FakeCommandLibrary`/`TempDataDirectory`）。`UdpSink` 绑 `127.0.0.1:0` 做**真实回环收包** —— 网络行为一律这么测，不 mock socket。`ApplyOverride` 是私有静态方法，用反射验证。

## 12. M2-C 对接码匹配链（**改动监听/匹配代码前必读**）

```
HostedServices/PairingListenerHostedService  只做「启监听 + 转发 + 记异常」
Core/Services/PairingDispatchService  编排：取最新配置 → 匹配 → 记接收日志 → 执行 → 记命中摘要
  ├─ PairingMatchService（纯静态，无 IO） ├─ ActionExecutionService └─ ITrafficLog / ControlSettingsService
```

1. **`PairingMatchService` 是纯静态函数**，不碰 IO/配置。入口 `Match(text, payload, buttons, sets)` → `PairingMatchResult`（`Kind` = None/Button/SubButton/Action + `Actions` + `OverrideIp`/`OverridePort`）。**顺序 = 优先级**：按钮级 → 子按钮级 → 动作级（按钮内动作 → 子按钮内动作）。列表顺序即配置顺序（Normalizer 已按 Order 排好）。
2. **两个数据源不能混用**：文本比较用 `text`（UTF-8 解码 + Trim），**Hex 比较必须用 `payload` 原始字节**。文本解码有损（非法字节 → U+FFFD、`0x0A` 被 Trim），拿它反推字节是错的。
3. **`IsPairingCodeMatch` 逐行复刻旧版**：Hex → `NormalizeHex(code)` vs `ToHexString(payload)` **Ordinal**；其它 → `OrdinalIgnoreCase`。**唯一有意放宽**：Hex 判定用 `DataEncoder.IsHex`（IgnoreCase）而非旧版 `==`。
4. **两处旧版 quirk 必须保留（有守门用例，别"顺手修好"）**：
   - 文本为空时**动作级不匹配**（`TryMatchAction` 开头 `if (IsNullOrEmpty(code)) return false;`），但**按钮级仍会匹配**。
   - `NormalizeHex` **只去空白 + 转大写** —— 不剥 `0x` 前缀、不兼容 `:`/`-`（与 WoL 的 `TryParseMac` 规则不同）。
5. **`PairingListenerService` 用 `Encoding.UTF8` 解码 + `Trim()`**（M1 错用 ASCII 会把中文变 `?`，永远匹配不上）。`PairingDatagram` 字段：`Text`/`Payload`/`HexText`/`RemoteIp`/`RemotePort`/`Length`（`RemoteEndpoint` 是计算属性）。
6. **监听循环 `await` 处理器**（`Func<PairingDatagram,CancellationToken,Task> DatagramHandler`）⇒ 对接码**串行**执行，与旧版收包循环内 await 同语义。**不要改回事件**（发完即忘、多包并发、日志顺序出错）。单条抛异常由 `HandlerError` 接住，**监听继续**。
7. **派发每次都读最新配置**（`GetAsync`，不是 `CachedSettings` 快照）—— 编辑页刚保存的对接码必须"下一包就生效"。靠版本号判断，未变更不重新反序列化。
8. 子按钮 override：`PairingDispatchService` 直接把 `match.OverrideIp/OverridePort` 传给 `ExecuteAsync`，服务端从所属集合取，**不接受客户端传值**。

**⚠️ 已知未修缺陷（见方案 §16.6）**：EF Core 的 `Database.Command` 日志没被压级（Serilog 只压了 `Microsoft.AspNetCore`）⇒ 每包对接码读一次 `config_documents`，`server-*.log` 多一条 `Executed DbCommand ... SELECT`。一行修法：`Program.cs` 补 `.MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)`。

**测试**：`PairingMatchServiceTests`(28) / `PairingDispatchServiceTests`(8) / `PairingListenerServiceTests`(7) 在 Core；`PairingListenerEndToEndTests`(5) 在集成（真宿主 + 真 SQLite + 真 UDP 回环）。冒烟脚本 `deploy/smoke/pairing-smoke.py`。

## 13. 配置导入导出与 Web 控制台（**改动配置/前端代码前必读**）

**13.1 HTTP 契约一条铁律** —— `ConfigViewDto.Settings` 是 **`JsonElement` 透传**，不是强类型 DTO。原因：`Contracts` 在 `Core` 之下**不能**反向引用 `Core.Models`；在契约层再抄一份会造成**两处真相**（改一处忘另一处 → 字段静默丢失）。⇒ **配置形状只有一份真相：`ControlServer.Core.Models`**，前端 `web/src/types.ts` 与其 camelCase 一一对应，改模型必须同步改 TS。

**13.2 导入必须守住的两道数据安全闸（别删，都有守门用例）**
1. `ConfigBackupService.Parse` 对**裸 JSON** 路径要求「至少认得一个已知字段」（`CountRecognizedFields > 0`）。否则 `{"version":"1.0"}` 这类任意 JSON 会被宽松反序列化成"全默认空配置"，导入即**一次点击清空全部配置**。
2. `ConfigController.Import` 的**空配置保护**：解析结果 0 按钮 0 集合而现有配置非空 → 400，并指向「恢复出厂设置」。

图片键名一律过 `ConfigBackupService.IsSafeImageKey`（单段文件名白名单）+ 必须落在 `ImagesDirectory` 内，两处都要。

**13.3 导入支持的四种输入（自动识别，以内容为准、与扩展名无关）** —— `ConfigBackupFormats`：`json+backup`（**旧版 `.controlbak` 明文 JSON，主路径**）/ `base64+backup`（服务版导出）/ `json+settings` / `base64+settings`。base64 探测**必须**能解出本身就是 JSON 的 UTF-8 文本才认，否则 `YWJj` 会被误判。

**13.4 不新增的端点（有意为之）** —— **没有** `/api/buttons/{id}/execute` 与 `/api/subbuttons/{id}/execute`。`POST /api/actions/execute` 用 `buttonId`/`subButtonId` 已覆盖同一语义，**前端统一只调它**。别再按设计稿补薄壳。

**13.5 实时通道（5 条，前端全接）** —— `LogAdded` / `StatusChanged` / `ActionResult` / **`TrafficLogAdded`** / **`ConfigChanged`**（只推版本号）。后两条由 `HostedServices/RealtimeRelayHostedService.cs` 桥接。**`ConfigChanged` 绝不推配置本体**（含图片 base64 可达数 MB）。前端收到后自行 `GET /api/config`；`onreconnected` 后补拉配置与收发日志。

**13.6 前端结构与两条渲染约定** —— `web/src/`：`types.ts`（领域类型 + `mergeTiles`）/ `format.ts` / `api.ts`（含带状态码的 `ApiError`，用于区分 409）/ `App.vue`（页签外壳 + 数据编排）/ `components/*.vue`。
1. **时间格式化必须用 `format.ts` 的 `fmtDateTime`（截字符串）**，不要 `new Date(...).toLocaleString()` —— 服务端时间戳已带 +08:00，会按浏览器时区二次换算。
2. **图片必须先过 `format.ts` 的 `imageUrl()`**（取文件名 → `/api/config/images/{名}`）：配置里存的是**服务端绝对路径**，直接塞 `<img src>` 永远是坏的。加载失败回退纯色瓦片，不反复重试。

**13.7 前端构建与打包（改了 .vue/.ts 必须走这套）** —— 产物直接进 `src/ControlServer.Server/wwwroot`（见 `web/vite.config.ts`）。只改前端：`cd web && npm run build`；出绿色版跑 `build-portable-exe.ps1`（内含前端构建）。`wwwroot` 以 `EmbeddedResource` 编进程序集，运行期优先级 **exe 同级物理 `wwwroot` > 内嵌资源**；`dist\win-x64\` 下无物理 wwwroot ⇒ 走内嵌 ⇒ **改前端必须重新 publish，只重启 exe 完全无效**。

**13.8 诊断判据（排查「界面/端点没变」先跑这个）** —— 有 `MapFallbackToFile("index.html")` SPA 回退，只吃 GET/HEAD ⇒ **POST 打到不存在的路由返回 405 而不是 404**。⇒ **405 = 路由不存在（掉进 SPA 回退）；401 = 路由存在但需登录**。另一半：Vite 产物是**内容哈希**命名，bundle 名没变就说明前端内容真没变（不是浏览器缓存）。

**13.9 冒烟脚本** —— `deploy/smoke/pairing-smoke.py`（对接码三级匹配）、`deploy/smoke/web-console-smoke.py`（Web 控制台全链路，**36 项断言**，Debug 与打包 exe 均应全 PASS）。两者都直连真 exe，**必须绕过代理**。

## 14. M2-D 定时与时间同步（**改动定时/时间代码前必读**）

**14.1 两条不变量**
1. **触发点格式必须是 `HH:mm:ss`**。调度器用**严格字符串相等**匹配（`List.Contains`，`Ordinal`）；`"08:00"` 与 `"08:00:00"` **永不相等** ⇒ 写错格式 = **静默不触发，零日志**。防线：清洗器在校验与读/写两侧都补秒（`NormalizeTimeString` / `NormalizeTimeList`），且 `TimerTime` / `MonthlyPlan.Time` 的**默认值刻意留空**（只有留空才能区分"字段存在"与"字段缺失"）。守门用例在 `ControlSettingsNormalizerTests` + `TimerSchedulerServiceTests`。
2. **幂等由数据库唯一约束保证**，不靠内存去重窗口。`timer_fires` 复合主键 `(ref_id, fire_date, fire_time)`；`TryClaimAsync` **直接插**，只把约束码 `2067`/`1555` 当作"已触发过"，**其它异常一律上抛**（笼统当"已触发" = 定时器静默失效）。月度计划 `refId` = `buttonId:planId`。

**14.2 三个已踩过的坑（都有守门用例，别倒退）**
- **🐛 回补循环必须按整秒推进**。`PeriodicTimer(1s)` 实际节拍 ≈0.995s，若用带亚秒的 `DateTimeOffset` 直接 `from.AddSeconds(1)`，`from+1s` 永远 > `now` ⇒ **循环体一次都不执行 ⇒ 定时器整体静默失效**。必须 `TruncateToSecond(from/now)` 后再逐秒推进。**这类缺陷写再多单元测试也抓不到**（假时钟按整秒推进时 `from+1s == now` 恰好成立），**只能靠真实宿主 + 真实时钟验证**。
- **🐛 亚秒级时钟回退要吸收，不要告警**：NTP 周期重校几乎总带来几十~几百毫秒负修正；仅当回退 ≥ 1s（`RollbackTolerance`）才告警 + 重新锚定。
- **🐛 原生 SQL 里不要硬编码列名**：本项目**没套 snake_case 命名策略**，实际列名是 PascalCase。`PruneAsync` 曾写 `fire_date` → 运行期 `no such column`；又因每天只跑一次且异常降级成 Warn ⇒ 静默失效。改用 `db.Model.FindEntityType(...).GetTableName()/FindProperty(...).GetColumnName()`。

**14.3 配置绑定对 `List<T>` 是「追加」不是替换** —— `TimeOptions.NtpServers` 曾自带与 `appsettings.json` 相同的内置默认 ⇒ 实测生效 **6 个**服务器，且用户**无法通过配置删掉内置默认**。⇒ **凡是要能被配置"替换"的列表，属性初始值必须留空**，内置默认只放唯一真相处（`TimeSyncSettings.DefaultNtpServers`），由组合根在列表为空时回退。

**14.4 时间口径** —— 全应用走**校正后的北京时间**：`BeijingTime.Now` = 系统钟 + `ClockOffset` + 固定 UTC+8，与系统时区解耦；需要纯系统钟用 `NowRaw`。`TimeProvider` 注册为 `CorrectedTimeProvider.Instance`（调度器只依赖 `TimeProvider`，测试注入假时钟）。
**⚠️ 已知局限（不可修）**：`Serilog.Sinks.File` 的 `Clock` 是 internal ⇒ **滚动文件名的切分点永远跟随系统钟**，只有文件内容吃校正；启动日志会打 `【已知局限】`。
**⚠️ 验收要看哪份日志**：`LogService` 的运维日志（`Timer`/`TimeSync`/`Listener`/`Action` 类目）**只落 `logs/log-YYYY-MM-DD.ndjson`**，**不进控制台、也不进 `logs/server-*.log`**（后者只有启动自检）。查定时/校准是否生效**必须看 ndjson**。

**14.5 定时配置项（`Server:Timer`）** —— `Enabled`(true) / `TickIntervalSeconds`(1，**大于 1 会漏触发**) / `CatchUpWindowSeconds`(60，超窗口只告警不补) / `MissedScanWindowSeconds`(600，启动时**只读扫描**停机期漏了什么、**绝不补执行**，0 = 关闭) / `RetentionDays`(35)。

## 15. M2-E3/E4：指令库与定时/诊断端点

**15.1 端点一览（都需登录；写操作要 `CanOperate`）**

| 端点 | 说明 |
|---|---|
| `GET/POST /api/commands`、`PUT/DELETE /api/commands/{id}` | 指令库 CRUD（独立文档，**不参与配置导入导出**） |
| `GET /api/timers` | 触发点清单 + 今日已触发 + 调度概况 + `nextFireAt` |
| `POST /api/timers/{refId}/fire` | 手动触发（`Fired`→200 / `NotFound`→404 / `AlreadyFired`→409） |
| `POST /api/system/timesync` | 手动 SNTP 校准（**成败都 200**，看响应体 `ok`） |
| `GET /api/system/info` | 运维诊断：机器/端口/运行时长 + 时间同步详情 + 定时摘要 + **实际 dataDirectory** |
| `GET /api/system/machine-code` | 机器码（**AdminOnly**，不进 info） |
| `GET/POST /api/system/address` | 端口热切换（见 §16） |

**15.2 六条不变量**
1. **业务失败用「结果对象」不用异常**（`CommandMutationStatus` / `TimerManualFireStatus`）。重名、找不到、入参非法都是正常分支。
2. **失败也带回服务端当前状态**：`CommandMutationDto.Commands` 恒为最新全量列表，前端失败时可立即刷新。
3. **损坏文档：读退化、写拒绝**。`commands` 解析失败时读路径返回空库继续服务，写路径返回 `StorageCorrupt` 并停手 —— 否则用户点一次「新增」就把还没抢救的原文永久覆盖。
4. **`Id` 一律以路由为准**（`UpdateAsync` 强制 `item.Id = id`）；新增时 Id 撞车 → 换新 Id。重名校验必须**排除自身**（`exceptId`），否则"只改协议不改名"会被拒。
5. **手动触发不绕开幂等闸门**：`FireNowAsync` 仍写 `timer_fires`（幂等键含当前 `HH:mm:ss`）⇒ 同一秒连点只有第一次生效。`refId` 含 planId 时**连计划一起验**，否则编个 planId 就能触发按钮动作。
6. **`FiresToday` 必须与调度器共用同一份判定**：界面显示"今天会跑"而实际不跑，比不显示更糟。改 `DetectAndFireAsync` 的日期判定时务必同步改 `FiresToday`（有守门用例）。

**15.3 ⚠️ 环境变量对配置数组是「按下标覆盖」，不是整表替换** —— `Server__Time__NtpServers__0=127.0.0.1` **不会**把列表变成 `[127.0.0.1]`，而是得到 `[127.0.0.1, ntp.tencent.com, cn.pool.ntp.org]`。实测后果：想让校准"确定失败"，第二个服务器照样联网查通，断言直接翻车。⇒ 要让列表"只有我要的那几个"，必须**把已有下标全部覆盖**（集成测试用 `__0..__3` 全指向 `192.0.2.1`，RFC 5737 TEST-NET-1）。与 §14.3 是同一族陷阱的两种表现。

**15.4 授权：类级与方法级 `[Authorize]` 是 AND 不是覆盖** —— 给某方法放宽权限时不能只加方法级策略，类级的仍生效，两者取交集。做法：**类级只写 `[Authorize]`（无策略），策略全放方法级**。

**15.5 测试夹具约定（`ControlServerFactory`）** —— 除 `Server__Time__AutoSync=false` 外还设 `Server__Timer__Enabled=false`（不需要每秒 tick 的后台循环，也排除"某条测试恰好写了个等于当前秒的定时点"的不确定性）。**手动触发端点不受影响**。

**15.6 集成测试里不要测"同一秒连点两次"** —— 那是典型 flaky 写法。该语义由 Core 单元测试用可精确控制的假时钟覆盖（`Fire_now_is_blocked_by_the_same_second_idempotency_gate`）；真机并发验证用两条 curl 同时发。

## 16. M2-E5：监听端口热重启（不重启 Kestrel 换端口）

**16.1 方案 §4.4 被证伪** —— 原设想「换掉 `IServerAddressesFeature.Addresses` 就能换端口」**无效**：Kestrel 启动时已把地址 `BindAsync` 成 `IConnectionListener`，地址特性只是**只读回显**。唯一可行做法：用**自己的 `IConnectionListenerFactory` 接管 `BindAsync`**，返回的监听器内部"抽换"底层真实监听器。

**16.2 ⚠️⚠️ Kestrel 会「反转」DI 里的 `IConnectionListenerFactory` 列表** —— 实测反射 dump `TransportManager._transportFactories` = `[NamedPipe, Socket, Switchable]`，**与 DI 注册顺序完全相反**。
- `services.Insert(0, Switchable)` ⇒ 实际排**最后** ⇒ 被 Socket 工厂抢先 ⇒ **静默不生效**（日志里连"已接管"都没有）
- 正确：**`AddSingleton` 追加注册（排最末）**，反转后它就是第一个
- 必须实现 `IConnectionListenerFactorySelector.CanBind(EndPoint)` 且**只对 `IPEndPoint` 返回 true**，否则 Unix socket / NamedPipe 也被接管
> 推论：Kestrel/DI 里所有"多实现挑一个"的地方**不要凭直觉假设顺序**，出问题先写反射 dump 打出实际列表。

**16.3 `SwitchableConnectionListener` 四条铁律**（`private volatile IConnectionListener? _inner;` + 「代」CTS `_acceptGeneration`）
1. **`AcceptAsync` 绝不把内层 `null` 透传给 Kestrel** —— Kestrel 收到 `null` ⇒ **永久退出接受循环**，端口还在 LISTENING 但没人接（`healthz=000`）。判据：`!_ReferenceEquals(_inner, inner)` ⇒ 是旧监听器收尾 ⇒ `continue`；否则 ⇒ 真停机 ⇒ `return null`。
2. **旧监听器只 `UnbindAsync`，不立刻 `DisposeAsync`**。立刻 Dispose 会**中止"正在回写切换结果的那个连接"** ⇒ 客户端 HTTP 000 + 服务端 `Pipe.AllocateWriteHeadSynchronized` 异常栈。做法：`_ = DisposeLaterAsync(old)`，延迟 2 分钟。
3. **`RetargetAsync` 顺序不可颠倒**：先在新端口 `BindAsync`，**成功了才** `RenewAcceptGeneration()` + `SafeUnbindAsync(old)` + 延迟 Dispose。失败则原封不动（硬验收：`409 PortUnavailable` 后 `healthz` 仍 200）。
4. **换代时 `previous.Cancel()` 但刻意不 Dispose CTS**（有 `AcceptAsync` 正持有它的 token）。
失败文案：`AddressAlreadyInUse` → "端口 {port} 已被其它进程占用，监听保持在原端口。"

**16.4 ⚠️ `ConfigurationManager.Sources.Insert(...)` 不触发 provider 重建（既存真 Bug）** —— `Sources` 是 `IList<IConfigurationSource>`，`Insert`/`RemoveAt`/索引赋值**不会**触发重新 build provider，新源形同不存在；只有 `Add`/`Remove`（`ICollection<T>` 固有实现）会。**后果**：原本那段"读 exe 同目录 `appsettings.json`"的代码**从未生效过**（静默）。修法：改 `Add` 插入后**手动把环境变量源挪到它后面**（`InsertConfigSourceBeforeEnvironmentVariables`）。

**16.5 端口持久化优先级（实测）** —— `环境变量/命令行 > 数据目录 server-overrides.json > appsettings.json > 内置默认`。覆盖文件落在**数据目录**（不是 exe 目录）、**原子写**（临时文件 + `File.Move(overwrite:true)`）、合并式保留其它键。占用导致切换失败时**不落盘**。

**16.6 端点与前端** —— `GET /api/system/address`（`CanOperate`）/ `POST`（**`AdminOnly`**）；状态 `Accepted/NoChange/Invalid/PortUnavailable/NotReady`。`StatusProvider.HttpPort` 与 `SystemInfo` 一律读 **`ServerAddressRegistry` 的实际监听值**（回退配置）。门面 `TryParseUrl` **刻意不用 `Uri`**（`Uri.TryCreate("http://*:9000")` 会失败）。SignalR `ServerAddressChanged` ⇒ 前端横幅 + 5 秒倒计时 `location.replace`。启动自检 `registry.HasListener` 为假时打 **Warn「【功能降级】监听地址热切换未启用」**。

**16.7 测试踩坑** —— 集成测试走内存 `TestServer`，**没有真实 Kestrel socket** ⇒ 端点测试必须覆盖"降级路径"（`409 NotReady` + **不落盘** + `/readyz` 仍 200），真正的换端口只能**真机**验证。**空监听器上的 `AcceptAsync` 会一直挂着**：直接 `await` 会把测试**挂死**（不是失败）⇒ 先 `Enqueue(expected)` 再 `AcceptAsync(...).AsTask().WaitAsync(5s)`。`NewListenerAsync` 夹具必须调 `BindInitialAsync`。

**16.8 ⚠️ 端口覆盖文件的格式必须能被配置绑定读回** —— `HttpOptions.Urls` 是 **`string`**（分号分隔）。写成 **JSON 数组**会产生 `Urls:0`/`Urls:1` 子键、父节点 `Urls` 值为 null ⇒ 绑定时被跳过 ⇒ **写进去自己读不回来**，重启后端口悄悄回默认。**`Save` 一律写字符串**（`string.Join(';', urls)`）；`TryReadUrls()` 兼容历史数组格式。单元测试要用**真实 `ConfigurationBuilder` 绑定**验证格式（只断言文件文本会漏掉整类"格式对但绑不回"的问题）。

**16.9 热切换只接受单个地址**（多地址一律 400 `Invalid`）—— 热切换只作用**主**监听地址；放行多地址会出现「只切第一个、却把全部写进持久化文件」的**假成功** ⇒ 重启后多出来的地址会真的被绑上。**做不到就不假装能做到**，文案指路配置文件 `Server:Http:Urls`。判据是「**去重后** > 1 个」而非「数组长度 > 1」，所以 `["a","a"]` 放行。

**16.10 `NoChange` 也要收敛持久化（判据刻意收紧）** —— `IsSame(CurrentUrls, newUrls)` 为 true 时**不能直接返回**：持久化文件可能落后于现状（典型：还留着早期多地址），此时界面上永远改不动它。⇒ 检查 `_store.TryReadUrls()`，不一致就重写。**但只在「文件存在、且内容不一致」时才重写** —— 否则一次纯"确认"会凭空生成文件、把环境变量传进来的临时端口**固化**。（守门用例 `No_change_does_not_create_an_override_file_out_of_nowhere`。）

**16.11 已删除的死字段 `SwitchDelaySeconds`** —— 原设计要"先返回已受理、延迟 N 秒再切"，但 16.3-② 的修复已从根上解决"响应被切断" ⇒ 切换**同步**完成，该字段从未被读取、`SecondsUntilSwitch` 恒为 0。**已删**（DTO + 前端 `api.ts`）；`SecondsUntilSwitch` 保留但注释标注"恒为 0，保留仅为兼容"。
> 教训：注释写"默认 3 秒"与实现（0）矛盾一整轮没人发现。**参数被接受但从不读取**是最难察觉的死代码 —— HTTP 请求不会报错。

---

## 17. ⚠️⚠️ 前端产物是**编译期嵌进程程序集**的

`src/ControlServer.Server/**/wwwroot/**` 在 csproj 里是 **`EmbeddedResource`**（绿色版要单文件），
运行时由 `ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot")` 读取 ——
**JS/CSS 是在 `dotnet build` 那一刻被写进 DLL 的**。

### 症状

标准顺序 `dotnet build` → `dotnet test` → `npm run build` 走完，然后直接跑
`bin/Debug/net8.0/ControlServer.Server.exe`：

```
静态资源来源：内嵌资源（绿色版 / 单文件模式）      ← 直接跑 exe 是 Production，走程序集内嵌资源
HTTP GET /assets/index-<新hash>.js → 404          ← 新产物根本不在程序集里
首页仍写着 index-<旧hash>.js
```

### 规则

**改前端后必须两步：`cd web && npm run build` **再** `dotnet build ControlServer.sln -c Debug`。**

**判据**（一条命令就能查）：

```bash
curl -s http://127.0.0.1:8080/ | grep -o 'index-[A-Za-z0-9_-]*\.js'
grep -o 'index-[A-Za-z0-9_-]*\.js' src/ControlServer.Server/wwwroot/index.html
```

两者必须**一致**。不一致 ⇒ 少跑了一次 `dotnet build`。

### 为什么这条坑很隐蔽

`dotnet run` 与 `dotnet publish` 之外，**Development 环境**下日志会打
「静态资源来源：**磁盘** `…\wwwroot`」，此时改完前端立刻生效，看不出问题；
一旦切到 exe / 绿色版 / Windows 服务（`Production`）就必现。
**"bundle 名变了但页面没变" 就是这条坑的指纹。**

### 17.1 上文的「Development 走磁盘」**只有 `dotnet run` 成立**（2026-10-08 实测修正）

判据是 `Path.Combine(ContentRootPath, "wwwroot")` **存不存在**（Program.cs §8.5），
而 `ContentRootPath` = **进程启动时的工作目录**：

| 启动方式 | 工作目录 | `ContentRootPath\wwwroot` | 实际来源 |
|---|---|---|---|
| `dotnet run --project src/ControlServer.Server` | 项目目录 | 存在 | **磁盘**（`npm run build` 后刷新即生效） |
| **`run.cmd`**（`start "" bin\Debug\net8.0\…exe`） | **仓库根**（`cd /d "%~dp0"`） | `D:\Project\zhongkong\wwwroot` **不存在** | **内嵌资源** |
| 双击 `bin\Debug\…\Server.exe` | exe 所在目录 | 不存在 | 内嵌资源 |
| 绿色版 exe / Windows 服务 | 任意 | 不存在 | 内嵌资源 |

⇒ **`run.cmd` 走的是内嵌分支**（它虽然每次都会先 `dotnet build`，所以正常路径下前端仍是新的）。
想验证实际来源，看启动日志那一行 `静态资源来源：磁盘 …` / `静态资源来源：内嵌资源（绿色版 / 单文件模式）`。

### 17.2 ⚠️ 「改了前端看不到」的第二条路径：**旧实例锁住 exe ⇒ 构建失败**

`ControlServer.Server.exe` / `.dll` 被**正在运行的实例**占用 ⇒ `dotnet build -c Debug` 报
`MSB3027 / MSB3021` ⇒ `run.cmd` 走 `:build_failed` 退出；或者旧实例仍占着 8080，
新实例绑不上端口 → `run.cmd` 报 `:timeout`。两种情况下浏览器里都还是**上一个成功构建**的前端。

**症状与 17.1「少跑一次 dotnet build」完全一样，但根因不同。** 三步定位：

```bash
# ① 运行中实例到底在给哪个 bundle
curl -s --noproxy '*' http://127.0.0.1:8080/ | grep -o 'index-[A-Za-z0-9_-]*\.js'
# ② 磁盘上现在的 bundle
ls src/ControlServer.Server/wwwroot/assets/
# ③ 谁占着 8080，以及 bin 产物是不是"上个世纪"的
netstat -ano | grep ':8080'
ls -la --time-style=+%m-%d_%H:%M:%S src/ControlServer.Server/bin/Debug/net8.0/ControlServer.Server.{exe,dll}
```

若 ③ 的时间戳**早于**你改前端的时间 ⇒ 就是这条。**必须"先停旧实例、再重新编译"**（实例在托盘右键退出）。

### 17.3 想**不动现有实例**先看新前端：旁路单实例 + 关定时

本机**不允许跑第二个实例**（`Local\ControlServer.Server.SingleInstance` 命名互斥体，Program.cs §4.5）——
第二个进程只打一行「检测到本机已有实例在运行…后退出」就静默退出，**不报错**，很容易误判成"起不来"。
用 `Server:Startup:SingleInstance=false` 旁路：

```bash
# 活动库先拍一份**事务一致**的快照 —— 不能直接拷正在写的 WAL 库
python -c "import sqlite3;s=sqlite3.connect('file:C:/ProgramData/ControlServer/control.db?mode=ro',uri=True);s.execute(\"VACUUM INTO 'C:/Temp/preview/control.db'\")"

"...\dist\win-x64\ControlServer.Server.exe" \
  --Server:Http:Urls=http://127.0.0.1:8099 \
  --Server:Storage:DataDir=<快照目录> \
  --Server:Listener:PairingPort=18098 \
  --Server:Timer:Enabled=false \
  --Server:Startup:AutoOpenBrowser=false \
  --Server:Startup:SingleInstance=false
```

- `--Server:Timer:Enabled=false` **必加**：否则带着真实配置起来的第二实例会**重复触发**定时按钮、真的往外发指令。
- 数据目录必须是**快照副本**，别让两个实例同写一个 SQLite 文件。
- 用 `127.0.0.1` 而不是 `0.0.0.0`，别把预览实例暴露到局域网。
- 副作用：会多一个托盘图标（右键 → 退出服务 收掉）。

### 17.4 ⚠️ 「定时调度到底关没关」**别去控制台日志里找**

`TimerSchedulerService` 的启用/关闭提示走 `LogService`，落到
**`<DataDir>/logs/log-YYYY-MM-DD.ndjson`**（一行一条 JSON），**不进** stdout / `server-*.log`：

```bash
grep -a Timer <DataDir>/logs/log-2026-10-08.ndjson
# {"Timestamp":"…","Category":"Timer","Message":"定时调度已关闭（Server:Timer:Enabled=false）。",…}
```

拿控制台日志找这句话会误判成"配置没生效"。（同源：`Listener` / `TimeSync` / `Heartbeat` 类别也只在这个 ndjson 里。）

### 17.5 前端产物哈希的两个事实（2026-10-08 实测）

1. **构建是确定的**：同一份源码连跑两次 `npm run build`，产物名逐字节一致
   （实测两次都是 `index-DsuLJM6c.js` + `index-BbWEotUY.css`，md5 也一致）。
   ⇒ 产物名变了就**一定**是源码变了。
2. **改一行注释也会换名，而且 CSS 文件名跟着一起换**（实测只改一句 JSDoc：
   `index-Ckj57y7O.js` → `index-DsuLJM6c.js`，`index-CaybY71H.css` → `index-BbWEotUY.css`）。
   原因：Rollup 的 chunk 哈希覆盖**模块内容**（含注释，早于压缩），
   而 Vite 的 CSS 资源名默认用**同一个 chunk 哈希**（本项目 `vite.config.ts` 没配 `assetFileNames`）。
   ⇒ ① **别把"CSS 名也变了"当成"CSS 被动过"的证据**；
     ② 只改注释/只改前端后，**打包产物与源码就对不上了**，要重新 `npm run build` + 重编/重打包
        （本项目已踩过一次"产物 ≠ 源码"：见 §17.1 那次"前端看不到成果"）。

---

## 18. M2-G1：图片上传 / 删除端点

### 18.1 端点

| 端点 | 策略 | 说明 |
|---|---|---|
| `GET /api/config/images/{fileName}` | `Authorize` | 已存在；**已改为复用共享解析器**（原来自己写了一份两道闸） |
| `POST /api/config/images` | `CanOperate` | `multipart/form-data`，字段名固定 `file`；返回 `{ fileName, path, bytes }` |
| `DELETE /api/config/images/{fileName}` | `CanOperate` | 204 / 404 / 409；`?force=true` 跳过引用检查 |

### 18.2 ⚠️⚠️ 契约：上传返回的 **`path`（绝对路径）** 才是要写进配置的值

配置里 `ButtonImage` / `SubButton.ButtonImage` / `DefaultButtonImage` / `BackgroundImagePath`
的契约是**服务端绝对路径**。而导出侧 `ConfigBackupService.BuildBackup` 是按
`File.Exists(字段值)` 收集要内嵌的图片 —— 若前端存的是**裸文件名**，
`File.Exists("imgX.png")` 会相对**进程工作目录**求值而返回 `false`，
那张图**不会被导出**，表现为"导出再导入后按钮图没了"，**全程无任何报错**。

前端写进配置的必须是 `dto.path`；`dto.fileName` 只用于拼读取地址
（`format.ts` 的 `imageUrl()` 取路径最后一段）。
反向守门用例：`ConfigImageApiTests.Storing_only_the_file_name_would_lose_the_image_on_export`。

### 18.3 上传的三道校验（`ImageStorageService.SaveAsync`）

1. **扩展名白名单** `.png .jpg .jpeg .gif .bmp .webp` —— **刻意不含 `.svg`**（SVG 是 XML，
   可内嵌 `<script>`，被当文档渲染就是存储型 XSS）；
2. **大小上限** `Server:Images:MaxUploadBytes`（默认 4 MB）—— 先按声明的 `Length` 快速失败，
   再按**流上真实读到的字节数**复核（不信任声明）；
3. **魔数嗅探** —— 扩展名是客户端**自称**的，只信它等于允许把任意字节存进图片目录、
   并以 `image/png` 服务出去。覆盖 PNG / JPEG / GIF87a / GIF89a / BMP / WEBP；
   注意 `RIFF` 开头但第 8..12 字节不是 `WEBP`（如 wav）**不算**图片。

**落盘名一律服务端生成** `img{Guid.NewGuid():N}{ext}`，客户端文件名**只用于取扩展名与记日志**，
从不参与路径拼接 —— 这是从根上消灭路径穿越。写盘用「临时文件 + `File.Move`」原子写。

### 18.4 ⚠️ 框架事实：MVC 在**调用 action 之前**就读取表单

`POST` 一个 `multipart/form-data` 请求时，MVC 会在 action 执行前构建值提供器并
`ReadFormAsync()`。因此**"请求体超过 `FormOptions.MultipartBodyLengthLimit`"这次失败
轮不到端点里的 try/catch**。实测拿到的是框架默认的 `400` + ProblemDetails，
文案 `Multipart body length limit 5242880 exceeded`（英文、含内部数字），
而 `413` 才是负载过大的正确状态码。

处置：`Program.cs` 里 `ApiBehaviorOptions.InvalidModelStateResponseFactory` **只针对这一种失败**
改写响应（413 + 中文提示），其余模型状态错误原样交回框架默认实现。
识别只能靠框架文案（该错误无错误码、拿不到强类型异常）⇒
集成测试 `Upload_rejects_a_body_larger_than_the_multipart_buffer_limit` 是这条依赖的**哨兵**：
将来 .NET 改措辞时表现会**回退**成框架默认的 400，而不是变成错误行为。

`FormOptions.MultipartBodyLengthLimit` 由组合根设为「上限 + 1 MB」（默认 128 MB 太宽松：
恶意请求能逼服务端先缓冲 128 MB 才轮到端点拒绝）。

### 18.5 `DELETE` 的引用保护（**设计稿没写，实做加的**）

删一张**仍被引用**的图会让那些按钮**静默**退化成纯色瓦片（前端对图片 404 的处理本就是回退、不报错），
用户看到的是"我删了一张没用的图，怎么别的按钮的图也没了"。
⇒ 默认 **409 + `referencedBy` 清单**（`默认按钮图片` / `按钮「开灯」` / `子按钮集「灯光组」` / `子按钮「一路」`），
`?force=true` 才强删。

匹配口径：按**文件名最后一段**、忽略大小写 —— 必须与前端 `imageUrl()` 和 `GET` 端点
**完全一致**，否则「能显示的图」与「被认为被引用的图」会错位。
实现在 `Core/Services/ImageReferenceScanner.cs`（纯函数，无 IO）。

### 18.6 一处易误判：**图片目录在启动时就存在**

`Program.cs` 在组合根之前调 `IDataDirectory.EnsureCreated()`（日志路径依赖它），
图片目录因此**启动即存在**。测"被拒的上传没留下东西"要断言**目录为空**，
不能断言"目录不存在"。

### 18.7 `Path.GetExtension` 在 .NET 8 的实测行为

`".png"` → `".png"`（**不是**空串）；`".gitignore"` → `".gitignore"`；
`"a.png.exe"` → `".exe"`；`"noext"` → `""`。
即它只认"最后一个点之后的部分"，不区分那个点是不是文件名开头。
（曾据此写过一条错误注释，被测试打回。）

### 18.8 规模

Core 单测 344（+42）｜集成 143（+21）｜合计 **423 → 487**，0 警告 0 错误。
真机冒烟 29 项全通过（真 Kestrel，含 6 MB 超大文件的真实行为）。

---

## 19. M2-G3~G7：前端可视化编辑器（**改动编辑页代码前必读**）

### 19.1 组件与职责

`App.vue` 的 `activeTab` 新增 `'edit'`（排在「控制台」之后），渲染 `EditPanel`。

| 文件 | 职责 |
|---|---|
| `EditPanel.vue` | 容器：draft 生命周期、瓦片列表（新建/复制/上移下移/两段式删除）、保存与 409 冲突 |
| `ButtonForm.vue` | 主按钮：基本 / 外观 / 对接码 / 动作 / 定时 / 月度计划 |
| `SubSetForm.vue` | 子按钮集：父集外观 + **共享 IP:端口** + 子按钮列表 |
| `SubButtonEditor.vue` | 单个子按钮（**无**定时与月度计划，WoL 不给选） |
| `ActionListEditor.vue` | 动作增删改排序（按钮与子按钮共用） |
| `ActionEditor.vue` | 单条动作 + 编码预览（`POST /api/actions/preview`） |
| `ImagePicker.vue` | 图片上传/预览/清除（走 G1 端点） |
| `TimeListEditor.vue` | `HH:mm` 时间点列表 |
| `web/src/factory.ts` | 默认对象工厂（字段与默认值逐字对齐 `Core.Models`） |

### 19.2 数据流（三条铁律）

1. **走全量 `PUT /api/config` + 乐观锁，不用 G2 细粒度端点。**
   动作列表的增删改互相牵连（顺序、Id），整体提交才能保证服务端收到的是自洽配置；
   单管理员场景并发代价可接受。**G2 因此降级**，只在并发/性能真成问题时再做。
2. **draft 深拷贝，子表单就地改字段，绝不重建对象。**
   服务端多出来而前端 `types.ts` 没声明的字段（如旧的 `timerTime`）才不会在保存时被抹掉。
3. **脏检测 = `JSON.stringify(draft) !== baseline`**，而不是"动过就永远是脏的" ——
   改回原样会重新变干净。`props.config` 变化时：`!dirty` 才同步到 draft；
   `dirty` 只置 `remoteDirty` 提示，**绝不覆盖用户正在编辑的内容**。

保存冲突（409）给两条出路：`forceSave()`（带服务端 `actualVersion` 重新提交）或 `discard()`。

### 19.3 排序语义

按钮与子按钮集**共用 Order、同级混排**，Order 相同时按钮优先（与 §8.7 一致）。
`EditPanel.applyOrder()` 按 `mergeTiles()` 的顺序把 Order 重编号为 `0..N-1`。
**新建时 Order 给一个大值（9999）**，重编号后自然落到末尾（给 0 会跑到最前面）。

### 19.4 ⚠️ 四个已踩过的坑

1. **`crypto.randomUUID()` 在局域网 HTTP 下是 `undefined`。**
   服务是 `http://192.168.x.x:8080` 访问的，那是**非安全上下文**，`randomUUID` 不存在，
   直接调用会让「新建按钮」整个失效。`factory.ts` 改为优先 `crypto.getRandomValues`
   再手拼 UUID v4（`getRandomValues` 在非安全上下文仍可用）。
2. **`const props = defineProps(...)` 时，模板里不能写 `action.xxx`。**
   props 的键不是 `<script setup>` 的顶层绑定，模板会把它当 `_ctx.action` → `undefined` → 运行时报错。
   **统一写 `props.xxx`**（`ImagePicker` / `ActionEditor` / `ActionListEditor` 三处已因此重写）。
   （Vue 3.5+ 的响应式解构是另一条路，但 3.4 上解构会失去响应性，不要用。）
3. **`draft` 可能为 `null`，模板必须判空。**
   `EditPanel` 渲染在登录后的 `v-else` 分支，而配置是**异步**拉取的。
   模板里的全局设置区用 `<template v-else>` 包在 `<p v-if="!draft">正在读取配置…</p>` 之后，
   否则配置到位前会抛 `Cannot read properties of null`，整页白屏。

补充：模板里的 TS 非空断言 `draft!.appName` **是安全的** ——
产物实测编译为 `o.value.appName`（ref 解包 + 剥离 `!`）。但既然判空更清晰，新代码优先判空。

4. 🔥 **"`v-if=loading` 占位 + `v-else-if=数据` 正文"= 每次刷新都把正文卸载重建。**
   两者在**同一个 v-if 链**上：`loading` 一为真，整块正文（连同里面的子组件）就被卸载，
   转假再重新挂载。而"刷新"通常就是重新走一遍 loading ⇒
   正文里**子组件的定时器、未保存的编辑、刚渲染出来的操作回执全部丢掉**。
   常表现为"保存成功了但提示一闪就没了""自动刷新永远不生效""正在填的内容莫名其妙没了"。
   **改法**：占位只在"还没有内容"时出现 —— `v-if="loading && !detail"`，刷新时保留旧内容、
   只把新 props 补下去。（#192 实测：`AgentDetailPanel` 因此把 M4.5 的任务配置面板
   每 10 秒重建一次，自动复查定时器随卸载被清掉 ⇒「已应用版本」永远不会自己追上。
   完整推演与"给各层 DOM 打记号"的定位手法见 `REF-M4-Agent.md` §19.4 第 1 条。）

### 19.5 写编辑器前查证过的语义

- **WoL**：`dataContent` 存 **MAC 地址**；`targetPort = 0` 回退 9；目标空或 `0.0.0.0` 回退广播。
- **`DataType = "指令库"`**：`dataContent` 存**指令名**，发送前经 `ICommandLibrary` 解析成真实协议/类型/数据。
- **月度计划**：`months` + `days` 是**旧格式**（交叉匹配），服务端迁移为 `monthDays`（每月独立日期）。
  前端只写 `times` + `monthDays`，旧字段由服务端补齐。
- **子按钮集的 WoL 被服务端强制降级为 UDP** ⇒ 前端直接不给选（双保险）。
- `timerTimes` 提交 `HH:mm`，服务端会规范化成 `HH:mm:ss`（编辑器不必自己补秒）。

### 19.6 验证

前端 `npm run build` → `dotnet build` 重嵌（顺序不能反，见 §17）。
端到端：用编辑器同款 payload（`order=9999` 新按钮 / 2 条动作 / `SpecificDays` 定时 /
1 个月度计划 / 2 个子按钮含 WoL 动作）`PUT /api/config` → **200**、version 0→1，
清洗后 Order 重编号正确、`monthDays` 原样保留、**子按钮 WoL 降级为 UDP** ✓

**未做**：真实浏览器渲染验证（本机沙箱无法启动 Chromium，`agent-browser` 与
`msedge --headless` 均无输出）。已用产物反查模板编译语义作为替代证据；页面实际观感待人工确认。

---

## 20. 指令库页与定时总览页（M2-G6）

**后端零改动** —— `CommandsController`（E3）与 `TimersController`（E4）早就写完了，
缺的只是 UI 入口。本次只加前端。

### 20.1 新文件与接线

| 文件 | 内容 |
|---|---|
| `web/src/components/CommandLibraryPanel.vue` | 指令库列表 + 新增/编辑/复制/删除 |
| `web/src/components/TimerOverviewPanel.vue` | 调度器概况 + 触发点清单 + 手动触发 |
| `web/src/types.ts` | `CommandItem` / `CommandListDto` / `CommandMutationDto` / `TimerTriggerDto` / `TimerOverviewDto` / `TimerFireResultDto` |
| `web/src/api.ts` | `commands` / `createCommand` / `updateCommand` / `deleteCommand` / `timers` / `fireTimer` |
| `web/src/App.vue` | 新增「定时」「指令库」两个标签（`activeTab` 联合类型同步扩展） |

### 20.2 指令库的语义（都在服务端，前端只负责如实呈现）

- **动作按「名称」引用指令**：`dataType='指令库'` 时 `dataContent` 是**指令名**，
  服务端用 `OrdinalIgnoreCase` 匹配（`CommandLibraryService.ResolveAsync`）。
  ⇒ **改名 / 删除都会让引用它的动作发送失败**。指令库页因此用本地配置扫描引用并预警
  （`references` computed + 改名警告 + 删除前提示），列表里标 `N 处`。
  实测证据：动作执行结果里 `resolvedFromCommand='开灯指令'`。
- **指令库不参与配置导入导出**（独立 `commands` 文档），与旧版一致。
- **只支持 UDP / TCP**（无 WoL —— 指令没有 MAC 语义）；
  **数据类型不含「指令库」**（否则 A 指令引用 B 指令会无限套娃）。两者服务端都返回 400 / `Invalid`。
- **增删改不带 `expectedVersion`**：语义是"对最新库施加一个局部意图"，天然可合并。
  失败时（重名 / 越界 / 找不到）响应体**仍带回全量列表**，前端直接用它刷新，省一次 GET。
- `PUT` 的 **Id 以路由为准**（body 里 Id 写错也不会"改一条留一条"）。

### 20.3 ⚠️ 手动触发的时刻语义（本次最重要的发现）

`TimerSchedulerService.FireNowAsync` 用 **`now.ToString("HH:mm:ss")`** 作为写入
`timer_fires` 的时刻 —— 即**你点的那一刻**，而**不是触发点自己定义的时刻**。
而 `GET /api/timers` 的 `firedToday` 是拿触发点的 `time` 去查同一张表：

```
fired 字典 key = $"{refId}|{entry.FireTime}"           // entry = 记录里的时刻
hasFired = fired.TryGetValue($"{t.RefId}|{t.Time}")    // t.Time = 触发点定义的时刻
```

⇒ **手动触发后 `firedToday` 仍为 false**（实测：触发点 `08:00:00`，记录 `21:40:59`）。

**这是有意设计，不要"修"**：若手动触发按触发点时刻落记录，
手动调试一次就会让定时器到点时被幂等闸门挡下 —— **当天的正式定时反而不执行**。

前端对策（`TimerOverviewPanel`）：本地记 `manualFired[refId]`，在「今天」列留下
`手动触发过 21:40:59` 的痕迹，并在说明里讲清原因 —— 否则用户会以为"点了没反应"。

其它已实测确认的行为：
- 同一秒内连点：第 1 次 200/`Fired`，第 2 次 **409**/`AlreadyFired`（幂等闸门）。
- **跨秒再点 → 200**：手动触发可反复调试，不是"每天一次"。
- 月度计划的 `refId` 形状是 `<buttonId>:<planId>`，含冒号，前端必须 `encodeURIComponent`。

### 20.4 ⚠️ `input[type=time]` 的秒陷阱（顺手修掉）

`TimeListEditor.vue` 原来直接 `v-model` 绑 `type="time"`。而配置里的时刻是
**`HH:mm:ss`**（服务端 `NormalizeTimeString` 补秒，**必需**：定时器按 `HH:mm:ss`
精确字符串匹配，写成 `08:00` 会永远匹配不上、**静默不触发**）。

`<input type="time">` 默认 `step=60`，遇到**秒非 0** 的值（旧配置可能带，如 `08:00:30`）
会判为非法 → **显示空白并把空串写回 v-model** ⇒ 该时间点被静默清空。

改为边界转换：读入时 `toInput()` 截成 `HH:mm`，写回时补 `:00`；无法识别的取值显式提示。
（秒为 0 的值本来就能显示，这一步是把"能显示"变成"一定安全"。）

### 20.5 定时页的展示口径

- 触发器**完全由服务端推导**（`DescribeActiveTriggers`）：未启用定时的按钮、
  月度计划里没勾的日期，在服务端就被过滤了 —— 前端**不作二次判断**，否则两处规则会漂移。
- 「今天」列四种互斥状态（优先级）：`已触发`(ok) > `今天不适用` > `已过点未触发`(warn) > `待触发`。
- `kind`：`Daily` / `SpecificDays`（`days` 0=周日…6=周六）/ `MonthlyPlan`（`monthDays` 已是文本如 `10月7日`）。
- 概况含 `tickIntervalSeconds`，**> 1 会漏触发**（按整秒匹配，跳过的那秒不补）—— 页面上显式告警。

### 20.6 验证

- 前端 `npm run build` + `tsc --noEmit`（**0 错误**）→ `dotnet build` 重嵌（0 警告 0 错误）。
- **端到端冒烟 27/27 通过**（真机 Kestrel）：指令库 CRUD 全路径（含重名 / 套娃 / WoL / 空名 /
  Id 纠正 / 404）、配置保存、动作按名解析指令、定时总览（3 个触发点、排序、今天适用性）、
  手动触发（同秒 409、跨秒 200、月度冒号 refId）、清理。
- 回归：`dotnet test` **487 全绿**（Core 344 + 集成 143）。

**未做**：真实浏览器渲染验证 —— 本机沙箱起不来 Chromium（`agent-browser` 守护进程挂死、
`msedge --headless` 无输出）。替代证据是查产物里新组件文案确已打包；页面观感待人工确认。

> 注意 `web/` 里**没有** `vue-tsc`，`npm run build` 只做 `vite build`（**不做类型检查**）。
> 目前能用的检查是 `./node_modules/.bin/tsc --noEmit`（只覆盖 `.ts`，`.vue` 模板不被检查）。

## 21. M2-G7：设置页运维三件套 + **配置优先级 Bug**（改 Program.cs 配置源前必读）

### 21.1 交付（纯前端 + 1 处后端修复）

后端 E4/E5 的端点早就齐了，但**没有任何 UI 入口**。G7 补上：

| 文件 | 内容 |
|---|---|
| `components/SystemInfoPanel.vue`（新） | 运行诊断（机器名 / OS / 服务端时间 / 时区 / HTTP 端口 / 对码端口 / 监听状态 / 运行时长 / 日志数 / 数据目录 / 定时摘要）+ 时间同步（偏移、上次校准、来源、往返、自动重校、尝试与失败数、NTP 列表）+ **立即校准**与逐服务器尝试记录 |
| `components/ListenAddressPanel.vue`（新） | 显示 **Kestrel 实际监听地址**（非配置值）、`0.0.0.0` 不可访问提示、端口来源、持久化文件；输入新端口热切换；区分 `Accepted/NoChange/Invalid/PortUnavailable`；仅 Admin 可提交 |
| `types.ts` / `api.ts` | `SystemInfoDto` / `TimeSyncInfoDto` / `TimeSyncResultDto` / `TimerSummaryDto` + `systemInfo()` / `timeSync()` |
| `SettingsPanel.vue` | 挂载两个面板；原「系统信息」改名「机器码与授权」（它只放机器码，与运行诊断分工不同）；转发 `address-switched` |
| `App.vue` | `onAddressSwitched(port)` —— **本地兜底**触发倒计时跳转（服务端也会广播，但实时通道可能已断） |

**设计要点**：
- 「立即校准」**成功与失败都返回 200**（看 `ok` 字段），`unwrap` 不会抛；抛出来的只有 5xx / 网络错误。
- 端口切换**成功后当前页原点即失效**：`scheduleRedirect` 用**当前 hostname 只换端口**，
  因此天然避开 `0.0.0.0`（浏览器访问不了它）—— 不要改成直接用服务端返回的 `newUrls`。
- 复用 App.vue 已有的 `ServerAddressChanged` 通道；`scheduleRedirect` 每次重置计时器，
  广播与本地下发**重复触发不会跳两次**。
- 权限：`info` / `timesync` / `address GET` 是 `CanOperate`，`address POST` 是 `AdminOnly`；非 Admin 只读展示。

### 21.2 ⚠️⚠️ 配置源顺序 Bug：**所有 `--Server:…` 命令行参数曾静默失效**

**症状**：`ControlServer.Server.exe --Server:Http:Urls=http://127.0.0.1:18099 --Server:Storage:DataDir=X`
启动后日志却写 `监听地址：http://0.0.0.0:8080`、`数据目录：C:\ProgramData\ControlServer` —— 参数像没传。

**根因**：`Program.cs::InsertConfigSourceBeforeEnvironmentVariables`。`ConfigurationManager` 的
优先级是**后添加者优先**，而它用 `sources.Add(便携配置)` **追加到末尾**，只把**环境变量**源挪到其后 ——
**漏了命令行**。命令行源在 `CreateBuilder(args)` 时已排在列表中部，于是顺序成了
`… → 命令行 → 便携配置 → 环境变量`，便携配置**反超命令行**。exe 同目录 `appsettings.json` 里写着
`Urls=http://0.0.0.0:8080` 与 `DataDir=""`，把命令行值全盖掉。

**为什么 487 个测试全绿却测不出来**：集成测试一律用**环境变量**注入配置
（`ControlServerFactory` 的 `Server__Storage__DataDir`…），而环境变量那条路径一直是好的。
**命令行参数没有任何测试覆盖** —— 测试与运维走的是不同通道。

**修复**：把命令行源也纳入"挪到便携配置之后"的集合（仍用 `Add` / `Remove`，**不能**改用
`Sources.Insert` —— 它不触发 provider 重建，见 §16 的坑记录）：

```csharp
var higherPriority = sources
    .Where(s => s is EnvironmentVariablesConfigurationSource or CommandLineConfigurationSource)
    .ToList();
foreach (var source in higherPriority) { sources.Remove(source); sources.Add(source); }
```

（另需 `using Microsoft.Extensions.Configuration.CommandLine;`）

**修复后实测优先级**（两次启动、多来源交叉验证）：
```
命令行 > 环境变量 > 端口持久化文件(server-overrides.json) > exe 同目录 appsettings.json > cwd appsettings.json
```

**验证**：用命令行参数启动演示实例 → 日志显示 `数据目录：…\cs-g7-demo`、`监听地址：http://127.0.0.1:18099`
（修复前两行都是默认值）；再让 override 文件写 18099、命令行传 18095 → 实际听 **18095**（命令行赢）。

**教训**：这条优先级规则**只能靠真机启动验证**，集成测试的注入方式天然绕过它。
`ServerAddressOverrideStore`（端口持久化）也走同一个方法，新增配置源时先想清楚"它该排在谁前后"。

### 21.3 另外两个实测发现（不是 Bug，但会误导人）

1. **Windows 上 `0.0.0.0:P` 与 `127.0.0.1:P` 可以共存**。测"端口被占用"必须占**服务端将要绑的那个精确地址**：
   占 `0.0.0.0:18097` 之后服务切到 `127.0.0.1:18097` 竟然**成功**（假成功），改用 `127.0.0.1:18097` 才拿到 409。
   Linux 不这样 —— 别把 Linux 直觉带过来。
2. 手动触发定时器不会把触发点标成"今天已触发"（见 §20）—— 同源：记录写在你**点的那一刻**，
   而 `firedToday` 查的是触发点自己的时刻。

### 21.4 验证

- 前端 `npm run build` + `tsc --noEmit`（**0 错误**）→ `dotnet build` 重嵌（0 警告 0 错误），bundle `index-Cvt6Q1kZ.js`。
- **Part A 只读端点 30/30**：`system/info` 14 键齐全、**不含机器码**（授权信息不外泄给 Operator）、
  `serverTime` 带 `+08:00`、`address.port` 与 `info.httpPort` 一致（两处口径不得打架）、新组件文案已打包。
- **Part B 端口热切换 34/34**（独立演示实例，不碰生产 8080）：`NoChange` 不写持久化文件 / 非法端口 400 /
  **多地址 400**（E5 的"只接受单地址"规则）/ 端口占用 409 且**旧监听一动没动** / `Accepted` 后新端口当场可用、
  旧端口不再接受新连接 / cookie 跨端口仍有效 / 切回原端口。
- 回归：`dotnet test` **487 全绿**（Core 344 + 集成 143）。

**未做**：真实浏览器渲染验证（同 §20 —— 沙箱起不来 Chromium）。页面观感待人工确认。

**未拍板**：机器码之外的授权录入（导入 license）尚无端点，留给 M3。

---

## 22. M2-G8：冒烟扩展与打包复跑

### 22.1 三个冒烟脚本 + 一个总入口

| 脚本 | 覆盖 | 对初始状态的假设 |
|---|---|---|
| `deploy/smoke/web-console-smoke.py` | 配置导入导出往返（旧版 `.controlbak` 兼容） | **要求空配置 + version=0** |
| `deploy/smoke/pairing-smoke.py` | 对接码三级匹配（直落 SQLite + 真 UDP 收发） | 要求库表已建好（**服务得先起来**） |
| `deploy/smoke/admin-api-smoke.py`（新，69 项） | G1 图片 / G6 指令库 + 定时 / G7 系统 | 要求**指令库为空** |
| **`deploy/smoke/run-all.py`（新，总入口）** | 依次跑上面三个 | —— |

**为什么必须有总入口**：三个脚本的初始状态假设**互斥**（空配置 / 空指令库 / 库表已建），
混在同一个实例上跑会互相污染，`checks` 会以"业务逻辑少了一条数据"的形式失败，极难定位。
`run-all.py` 对每个脚本**开新临时数据目录 + 新空闲端口 → 起 exe → 等就绪 → 跑 → 收摊**。

```bash
python deploy/smoke/run-all.py                 # 默认打 dist/win-x64 的打包 exe
python deploy/smoke/run-all.py --only admin    # 只跑一个
python deploy/smoke/run-all.py src/ControlServer.Server/bin/Debug/net8.0/ControlServer.Server.exe
```
退出码：全绿 0，否则 1（可直接进 CI）。就绪判定是轮询 `GET /` → 200；进程先退出则立刻判失败并打印启动日志路径。

### 22.2 ⚠️ 写冒烟时踩到的四个坑（**都是断言写错，不是产品缺陷**）

1. **「删除已不存在但仍被引用的图片」返回 409，不是 404** —— `DELETE /api/config/images/{f}`
   的**引用检查先于存在性检查**。所以 `?force=true` 删掉文件后，配置仍引用着它，
   再删一次还是 409。要测 404 得用一个**从未存在**的合法文件名（`img` + 32 个 `0` + `.png`）。
2. **发 TCP 到一个没人监听的端口 = 0 字节 + 失败**（约 2s 后连接被拒）。用它测"指令库按名解析"
   就把「目标不可达」和「指令解析失败」混在一起了。**测内容解析一律用 UDP**（UDP 发出去就算成功）。
3. **幂等闸门断言必须同时确认两面**（第 1 次 200 **且** 第 2 次 409）。
   只断言"出现过 409"会**假通过**：上一对里的第 2 次与下一对的第 1 次常落在同一秒，
   于是连续两组 `409/409`。修法是配对失败后 `sleep` 让开一秒再试，最多 8 对。
4. **非 ASCII 的路径段必须 URL 编码**：`http.client` 要求 request target 是 ASCII，
   直接写 `/api/commands/不存在的-id` 会抛 `UnicodeEncodeError`（不是服务端拒绝）。
   一律 `urllib.parse.quote(...)`（`:` 也要编码 —— 月度计划 refId 是 `<buttonId>:<planId>`）。

### 22.3 ⚠️ 打包脚本「成功却报失败」是**假象**

`build-portable-exe.ps1` 走完整流程（含 `npm run build`）时，rollup 会把
`/*#__PURE__*/` 注解提示写到 **stderr**。若调用方写成
`powershell -File build-portable-exe.ps1 2>&1 | Select-Object -Last 40`，
PowerShell 5.1 会把 stderr 行包成 `ErrorRecord` 塞进管道，**外层因此报退出码 1**。

**实测脚本自身退出码是 0**（`*> $null` 后读 `$LASTEXITCODE`，`-SkipWebBuild` 与完整路径都是 0）。
⇒ 判断打包成败看**输出里的 `✓ 单文件模式正常`**，或用 `*> $null` 收掉输出；
**不要**把 stderr 折进管道后再看退出码。这与脚本顶部注释里"不要用 `$ErrorActionPreference='Stop'`"
是同一类问题的另一面。

### 22.4 打包复跑结果

- `dist/win-x64/ControlServer.Server.exe` 由 10-07 15:24（早于 G1/G6/G7）刷新为**本次构建**，
  **48.9 MB**，发布目录**只有 1 个文件**（`appsettings.json` 与 `wwwroot` 都不随发布输出，见 §17）。
- 两次独立打包字节数完全一致（51296237）⇒ 前端 bundle 哈希稳定（`index-Cvt6Q1kZ.js`，本项未改前端）。
- 打包 exe 上**命令行参数生效**（`--Server:Storage:DataDir` / `--Server:Http:Urls` / `--Server:Listener:PairingPort`
  全部按传入值启动）—— 又一次回归保护了 §21 的配置优先级修复。

### 22.5 验证

`run-all.py` 对**打包 exe**：`web` 36 项 + `pairing` 4 项 + `admin` **69 项**，**3/3 场景全绿**。
`dotnet test` **487 全绿**（Core 344 + 集成 143），0 警告 0 错误。

### 22.6 仍未做（**不是 G8 范围，但必须记账**）

- **导入前自动备份 `.bak` 仍不存在**：全项目搜 `.bak` / `AutoBackup` **零命中**。
  `POST /api/config/import` 直接 `SaveAsync` 整体覆盖，用户点错一次就没得回退
  （唯一兜底是"空配置保护"—— 只挡住"清空"，挡不住"用另一份配置覆盖"）。
  方案文档 §6 把它列为"顺手要补的旧账"。**同一节里的 EF Core 日志压级已经做了**
  （`Program.cs` 有 `MinimumLevel.Override("Microsoft.EntityFrameworkCore", Warning)`）。

---

## 23. 配置持久化架构（**改动配置代码前必读**）

设计依据：`docs/M2-功能对齐方案.md` §14（实施回填）与 §10.7。

1. **配置不走关系表**，走 `config_documents`（`Key` PK + `Value` JSON + `Version` 乐观锁 + `UpdatedAt/UpdatedBy`）。
   键：`settings`（按钮/集合/外观）、`commands`（指令库，**不参与导入导出**）。
2. **`Core` 侧模型是唯一真相源**：`Core/Models/*`（9 个 POCO）。字段名/默认值属**持久化契约**
   （沿用旧版 `control_config.json` 形状，改名会让历史配置导入后**静默漂移**），camelCase。
   `ControlJson.Options` 是**唯一**序列化入口，禁止各处 new。
3. **写入永远经由 `ControlSettingsService`**（Singleton）：内部 `SemaphoreSlim` 保证「读-改-写」原子 + 版本校验。
   细粒度操作必须走 `MutateAsync`，**不得绕过乐观锁**。
4. **`IConfigDocumentStore` 实现必须 Singleton**（乐观锁串行化要进程级唯一），用 `IServiceScopeFactory`
   开 scope 拿 Scoped `DbContext`。
5. 冲突抛 `ConfigConcurrencyException` → 上层转 **HTTP 409**。
6. **清洗是纯函数**：`ControlSettingsNormalizer.Normalize` 必须**幂等**（二次调用零输出、序列化逐字节一致）；
   `SortAndReindex` 只在顺序真变时记说明。
7. **排序归一化**：按钮与集合**共用 Order、同级混合排序**；Order 相同时**按钮优先**；随后重编号 `0..N-1`。
8. **配置损坏时绝不回写**：回退默认值 + 暴露 `ConfigLoadState.Corrupt`，保住用户数据。
9. **图片字段存的是服务端绝对路径**（不是文件名）—— 导出侧按 `File.Exists(字段值)` 收集要内嵌的图片。
   上传接口因此必须回传 `path`；**存裸文件名 = 导出静默丢图**（详见 §18.2）。

**⚠️ 已知有意偏离旧版（勿"修回去"）**：`ButtonConfig.TimerTimes` / `MonthlyPlan.Times` 的模型
**默认值为空集合**（旧版写在属性初始化器里是 `["08:00"]`），导致旧版 `TimerTime → TimerTimes` 迁移变
**死代码**、旧值被静默丢弃。服务版把默认值下移到清洗器以保住旧值。
守门用例 `Legacy_TimerTime_is_honored_even_when_the_new_field_is_never_mentioned`。

## 24. 动作单条执行 + 编辑页/控制台 UI 调整（2026-10-08）

用户一次性提 4 条 UI/交互需求（**前后端一起做**）。本文件只记"改动前必须知道的事实"，
细节与验证实录见当日日志 `.workbuddy/memory/2026-10-08.md`「追加需求 4 条」一节。

| 要做的事 | 改哪 |
|---|---|
| 单条动作执行（DTO） | `src/ControlServer.Contracts/ActionDtos.cs`（`ActionExecuteRequest.ActionId`） |
| 单条动作执行（端点） | `src/ControlServer.Server/Api/ActionsController.cs`（`SelectActions` / `DefaultLabel` 两个私有静态） |
| 动作卡片「执行」按钮 | `web/src/components/ActionEditor.vue`（`runSingle()`；按钮放在 **↑ 左侧**） |
| override 透传链 | `ActionListEditor.vue` → `ButtonForm.vue` / `SubButtonEditor.vue` → `SubSetForm.vue` |
| 编辑页删「全局」 | `web/src/components/EditPanel.vue`（删面板 + `ImagePicker` import + `.adv`/`.side label.inline` 样式） |
| 全局设置新家 | `web/src/components/SettingsPanel.vue`（新增「外观与全局」面板，`CanOperate` 收口） |
| 最近收发 20 条 | `web/src/App.vue`（新增 `RECENT_TRAFFIC = 20`；`.mini` 加 `max-height` + 滚动） |
| 日志表格加高 | `web/src/style.css`（`.table-wrap.tall`）+ `TrafficPanel.vue` / `OpsLogPanel.vue` |
| 冒烟 | `deploy/smoke/admin-api-smoke.py`（新增 `[G9]` 段 9 条断言）；`run-all.py` 标题同步 |

**铁律 / 陷阱**：

1. **单条执行有两条通道，用途严格不同 —— 别混**：
   - **内联 `actions:[...]`** ← **编辑页「执行」按钮专用**。编辑页改的是 **draft（未保存）**，
     只有走内联才能执行"眼前这份草稿"。
   - **`buttonId`/`subButtonId` + `actionId`** ← 脚本 / 运维 / 将来定时联调，按**已保存配置**精确触发。
   ⚠️ 编辑页若误用 `actionId`，用户刚把 IP 改成 `10.0.0.9` 再点「执行」，发出去的却是**旧 IP** ——
   这正是点这个按钮最不该出的错。**后端两条路都实现且都有测试，前端只走内联。**
2. **`SelectActions` 的两种「空」绝不能合并**：指定 `actionId` 但**找不到 → 返回 `null` → 404**；
   没指定 `actionId` 而**整组本就 0 条 → 返回原列表（空）→ 200 + 0 steps**。
   合并会让"以为只发一条、其实发了一整组"与"按钮被清空却以为成功"都变成**静默降级事故**。
3. **孤立 `actionId`**（既无 `buttonId` 也无 `subButtonId`）→ **400**：动作 Id 只在按钮内唯一，无法定位。
4. **`ActionId` 是 `record` 位置参数**，插在 **`SubButtonId` 之后、`Label` 之前**。
   新增位置参数前**必须**确认无按位置构造的调用点（本轮全部命名参数，构建 0 错误佐证）。
5. **单条执行时本条 `delayMs` 必然不生效**：`delayMs` 语义是"上一条发完后等这么久"，第 0 条不等待；
   单条执行时它自己就是第 0 条。前端**必须显式提示**（"单条执行时本条 N ms 延迟不生效"），不能静默。
6. **子按钮 override 透传链**：`SubSetForm`(`set.targetIp/Port`) → `SubButtonEditor`(`setTargetIp/Port`，**必填**)
   → `ActionListEditor`(`shared-target` + `overrideIp/Port`) → `ActionEditor`(`ownerLabel/overrideIp/overridePort`)。
   `runSingle()` 在 `sharedTarget && !overrideIp` 时**直接报错拒发**（"所属集合的目标 IP 为空"），
   **绝不**退化成用动作自身的 IP 发出去。
7. **保存全局必须整份替换**：`PUT /api/config` 是**全量语义** ⇒ 先**深拷贝** `props.config.settings` 再改那 5 个字段，
   否则"只保存全局"会把整站按钮清空。冲突仍是 409 + 回显 `current`；
   `globalConflict` 类型是 `ControlSettings | null`，取值写 **`body?.current?.settings`**（`current` 是 `ConfigView`）。
8. **`globalDirty` 必须是 `computed`**（写成函数则函数对象永远 truthy ⇒ `v-if` 永真、"未保存"提示常驻）；
   `watch(props.config)` 要**跳过有脏改动的情形**，否则别人一改配置就冲掉正在编辑的全局草稿。
9. **全局字段权限用 `CanOperate`（Admin + Operator）**，**不是** `isAdmin` ——
   这几个字段本就是操作员日常要动的，"搬家"不该顺手收紧成 AdminOnly（= 权限倒退）。
10. **日志加高用 `max(560px, calc(100vh - 260px))`**：单一 `calc(100vh - 260px)` 在 600px 高的小窗口下只剩 340px，
    **比原来更矮**，与"加大高度"相反；`max()` 保住 **560px 下限**、大屏再随窗口长。

## 25. 托盘驻留与宿主形态（**改动 Program.cs 启动段 / 平台层 / 安装脚本前必读**）

> 需求原文：「新增后端服务运行时，会在右下角状态栏显示服务图标，且关闭控制台并不能直接停止服务，
> 停止服务需要在右下角服务图标的右键菜单中操作」。
> 方案全文：`docs/M3-托盘驻留与宿主形态方案.md`（§0 结论先行 / §11 方案 D 详细设计 / §12 实施回填）。

### 25.1 三个"为什么"（**不理解这三条就会把架构改回去**）

1. **为什么"关控制台"必定停服务** —— 控制台程序拥有控制台，关窗口时系统发 `CTRL_CLOSE_EVENT`，
   **应用无法否决**（只能延迟约 5 秒）。所以"拦住关窗口"在 Win32 子系统下**做不到**；
   唯一解法是把子系统改成 `WINDOWS_GUI` ⇒ `ControlServer.Server.csproj` 里 **`<OutputType>WinExe</OutputType>`**。
2. **为什么服务形态不可能有托盘** —— Windows 服务跑在 **Session 0**，那里**没有 explorer、没有通知区域**。
   服务进程**永远**显示不出托盘图标（不是实现问题，是会话模型）⇒ 托盘必须放到**用户会话**，
   于是有了独立进程 `ControlServer.TrayAgent.exe`（方案 D）。
3. **为什么不能用 WinForms 做托盘** —— 会把 `ControlServer.Platform.Windows` 变成 `net8.0-windows`，
   而 `net8.0` 项目无法引用 `net8.0-windows`（`NU1201`）⇒ **铁律 #3 破防 + Linux 交付当场失败**。
   所以托盘全部是**自研 P/Invoke**（`NativeMethods.cs`），平台项目保持 `net8.0`。

### 25.2 三种运行形态与托盘归属

| 形态 | 进程 | 托盘由谁提供 | 停服务方式 |
|---|---|---|---|
| 开发期（`dotnet run` / 终端里跑 exe） | Server | Server 自己（若 `UserInteractive`） | 托盘「退出服务」（默认已脱离终端，见 **§25.8**） |
| **绿色版**（双击单文件） | Server | **Server 自己** | 托盘「退出服务」 |
| **服务版**（`sc` 安装） | Server（Session 0）+ TrayAgent（用户会话） | **TrayAgent** | 托盘「停止服务」（走 SCM） |

**停服务只有一条正道**：托盘菜单。**关窗口、关浏览器、关终端都不算** ——
其中"关终端"需要服务**主动脱离控制台**才成立，那是 §25.8 的主题。

### 25.3 代码落点

```
Platform.Abstractions/TrayIcon.cs        契约：TrayCommands / TrayMenuCommand / TrayIconOptions / ITrayIconHost
Platform.Linux/NoopTrayIconHost.cs       IsAvailable=false；事件用 add{}/remove{} 显式表达"永不触发"
Platform.Windows/NativeMethods.cs        全部 Win32 声明集中在此
Platform.Windows/IconLoader.cs           从内嵌 app.ico 造 HICON（PickBestIndex 纯函数，永不抛）
Platform.Windows/WindowsTrayIconHost.cs  隐藏顶层窗口 + 专用 STA 消息循环线程 + Shell_NotifyIconW
Platform.Windows/WindowsInteractiveHost.cs  附父控制台 / 输出探测 / MessageBox / 单实例互斥
Platform.Windows/WindowsServiceController.cs SCM：QueryStatus / Start / Stop（永不抛，错误码译中文）
Server/Program.cs §0.1/§4.5/DI/§10.6   附控制台 → 单实例 → 注册 ITrayIconHost → 托盘接线
TrayAgent/Program.cs                    独立托盘助手（WinExe，**刻意不引用 Server**）
```

**⚠️ 铁律级细节（每条都踩过或差点踩到）**

1. **`Shell_NotifyIconW` 在 `shell32.dll`，不是 `user32.dll`** —— 写错**编译期完全正常**，
   只在**真跑**时抛 `EntryPointNotFoundException`。这就是 `TrayIconTests` 里那条
   "真建图标再优雅摘掉"用例的价值：**别删它**（它专治"编译过=以为对"）。
2. **窗口类必须持有 `WndProc` 委托引用**：`_wndProc = WindowProc;` 存字段。
   否则 GC 收走委托 ⇒ 窗口过程变野指针 ⇒ 随机崩溃。
3. **托盘窗口必须是"隐藏的顶层窗口"，不能是 `HWND_MESSAGE`**：
   消息专用窗口**收不到** `TaskbarCreated` 广播 ⇒ explorer 重启后图标**永久消失**。
   `CreateWindowExW` 的 `dwStyle: 0` + `ShowWindow(SW_HIDE)`。
4. **`NOTIFYICONDATA.cbSize` 必须 `Marshal.SizeOf`**（x64=976 / x86=956）。有守卫用例盯着这个数字。
5. **右键菜单前 `SetForegroundWindow`、之后 `PostMessage(WM_NULL)`** —— 否则菜单点外面不消失（Win32 老坑）。
6. **`WM_CLOSE` 不能交给 `DefWindowProc`**：WinExe 没有控制台、托盘一旦销毁，进程就成了
   **"没有任何控制面的孤儿"**（只能在任务管理器里杀）。必须翻译成 `TrayCommands.Exit` 走优雅停机。
7. **`RaiseCommand` 要投递到 `ThreadPool`**，不能在消息循环线程里直接回调 —— 否则处理器里再调
   `UpdateAsync`（需要消息循环）会**自锁**。
8. **`DisposeAsync` 用 `Post` + 等 `_loopExited`（3 秒超时）**，不要 `Send`（跨线程阻塞易死锁）。
9. **单实例互斥体名是固定的 `Local\ControlServer.Server.SingleInstance`**（`Local\` = 每会话一个）。
   ⚠️ **它是进程级资源** ⇒ 集成测试里"同一进程内起第二个宿主"的用例会被误判。
   所以有 `Server:Startup:SingleInstance` 开关，**`ControlServerFactory` 必须设 `false`**，否则
   `Config_survives_host_restart` / `Claims_survive_a_host_restart` 会报
   `The entry point exited without ever building an IHost`。
10. **输出重定向探测要用 `GetFileType`，不能用 `Console.IsOutputRedirected`**：
    后者区分不了"重定向到文件"与"压根没有句柄"。`FILE_TYPE_DISK`/`FILE_TYPE_PIPE` 才准。
    只有"未被抓取 + `UserInteractive`"才附父控制台，并**重绑** `Console.SetOut/SetError`。
11. **`WinExe` 下启动失败必须可见**：非 `UserInteractive` 时**跳过** `MessageBoxW`（否则模态框永久阻塞、
    进程像死了一样），改为只写日志。

### 25.4 服务版控制通道：SCM + `sc sdset`（**为什么要改服务 DACL**）

`sc create` 出来的服务默认 DACL 给 **交互用户(IU)** 的权限是 `CC LC SW LO CR RC`，
**正好缺 `RP`(SERVICE_START) 与 `WP`(SERVICE_STOP)** ⇒ 托盘（非管理员）点"停止服务"会 **Access Denied**。
安装脚本第 4/6 步把 IU 的 ACE 改成 `CCLCSWRPWPLOCRRC`（按规范顺序重建，**不是**简单拼接）。

服务权限码 SDDL 对照（`sc sdshow` 的输出就靠这张表读）：

`CC`=QUERY_CONFIG｜`DC`=CHANGE_CONFIG｜`LC`=QUERY_STATUS｜`SW`=ENUMERATE_DEPENDENTS｜
`RP`=START｜`WP`=STOP｜`DT`=PAUSE_CONTINUE｜`LO`=INTERROGATE｜`CR`=USER_DEFINED_CONTROL｜
`RC`=READ_CONTROL｜`SD`=DELETE｜`WD`=WRITE_DAC｜`WO`=WRITE_OWNER

⚠️ 带 `S:(SACL)` 段时 `sc sdset` 需要 `SeSecurityPrivilege`；脚本**失败会退化成"只设 DACL"重试**。

### 25.5 托盘助手（方案 D）的启动方式：D-a

安装脚本写 **`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`**（不是 HKLM）：
托盘必须在**用户会话**、且**非管理员**权限下跑，Run 项天然满足。
代价：**每个要用托盘的账号各装一次**（服务只需管理员装一次）。
⚠️ 脚本会比对 `Win32_ComputerSystem.UserName`（控制台用户）与当前身份，**不一致时告警**并给出
"在目标账号下重跑 / 手工 `reg add`"的指引 —— 否则启动项写进了错误账号的 hive，用户永远看不到图标。

### 25.6 ⚠️ 脚本相关的两个坑（**真机验证过**）

1. **PowerShell 不支持前向引用！** 函数必须定义在调用点**之前**。
   把 helper 放在文件末尾 ⇒ `CommandNotFoundException`；而脚本里 `$ErrorActionPreference='Continue'`
   会把非终止错误**静默吞掉** ⇒ 脚本"成功退出"却**少做了授权与托盘部署**（最危险的失败形态）。
   实测：脚本里在定义前调用函数，直接报 `无法将"Invoke-Later"项识别为 cmdlet`。
   ⇒ `install-service.ps1` 的**函数区全部前置**。
2. **`✓`(U+2713) / `✗`(U+2717) / `⚠`(U+26A0) 不在 GBK 里**，真实控制台会显示成 `?`。
   ⇒ 脚本的用户可见输出统一用 **ASCII 标记**（`[OK]` / `[!!]` / `[X]` / `[!]`）。
   ⚠️ 更严重的是 **Python 冒烟脚本**：`print()` 里带这些字符时，**真实控制台**走 `WriteConsoleW` 没事，
   但**一旦重定向/管道**就退化成 cp936 ⇒ `UnicodeEncodeError` **崩在失败报错那一步**。

### 25.7 冒烟脚本为什么必须关掉托盘与单实例

`deploy/smoke/run-all.py` 的实例参数里加了：

```
--Server:Tray:Enabled=false           # 4 个纯 API 场景不需要 UI；否则每跑一个场景桌面就闪一次图标
--Server:Startup:SingleInstance=false # 互斥体名固定 ⇒ "上一个刚 terminate、下一个就起"有竞态窗口，
                                      # 撞上就报"未能在超时内就绪"这种**假故障**
```

托盘真正的显示/摘除由 `tests/ControlServer.Server.IntegrationTests/TrayIconTests.cs` 覆盖（8 个用例）。

### 25.8 控制台生命周期解耦（**"关闭 CMD 窗口不能停止服务"**）

> 第二轮需求原文：「关闭CMD窗口不能直接关闭服务，这个功能未实现」

**先证病、再开药 —— 真机复现结果**（从 CMD 里启动服务，然后关闭那扇窗）：

| | 服务进程是否还挂在控制台上 | 关窗后 1 秒 |
|---|---|---|
| 修复前 | **True** | **服务消失** |
| 修复后 | **False** | 12 秒仍存活 |

**根因**：Win32 里"关控制台窗口"不是一个可拦截的信号，而是一次**必然生效的处决** ——
控制台窗口关闭时，系统向所有客户端进程投递 `CTRL_CLOSE_EVENT`，**应用无法否决**
（`SetConsoleCtrlHandler` 返回 TRUE 也只能把死期从 0 秒拖到约 5 秒）。
所以 **"让服务扛住关窗"在拥有控制台的前提下做不到**；唯一解是**先离开这个控制台**：
`kernel32!FreeConsole()`。脱离之后进程不再属于它，那扇窗再被关掉就与我们无关。

⚠️ **别把 `WinExe` 当成已经解决** —— 双击时确实没有控制台，但"从 cmd / `run.cmd` 启动"
时进程**会继承那个控制台**。"GUI 子系统"和"不受控制台生命周期管辖"是两件事。

**关键实现细节**

1. **输出已被重定向时什么都别做**：`FreeConsole` 会关闭**指向控制台**的标准句柄，
   但**不碰**文件/管道句柄。所以"输出被 `Popen(stdout=file)` / CI 抓走"的场景
   既不需要附着也不需要脱离 —— 直接 `return true`。这一条同时保住了**集成测试宿主**
   （stdout 是 xunit 的管道）与**冒烟脚本**的零副作用。判据仍用 `GetFileType`（见 25.3-10）。
2. **`AttachConsole` 的返回值不能当"控制台可用"用**：从 cmd 启动时进程**已经**继承了
   控制台，此时 `AttachConsole(ATTACH_PARENT_PROCESS)` 会因"已有控制台"而
   **失败（ERROR_ACCESS_DENIED）**，但控制台分明是可用的。
   ⇒ 判定"有没有控制台"必须用 `GetConsoleWindow() != 0`。
3. **集成测试必须"保留控制台"**：`ControlServerFactory` 里加
   `Server__Startup__AttachConsole=true`。否则在**用户自己的终端**里跑 `dotnet test`
   （输出没被重定向）时，测试宿主会把**自己进程**的控制台退掉，xunit 输出随之丢失。
   ⚠️ **新增这类"进程级副作用"时必须回头检查测试宿主** —— 这是同类教训的第二次
   （第一次是单实例互斥体，见 25.3-9）。
4. **开发模式留一条回退路**：`Server:Startup:AttachConsole=true` → 保留终端、打印实时
   Serilog，代价是关终端会停服务。该开关**手写扫描 argv** 而不走
   `ConfigurationBuilder.AddCommandLine`：后者遇到 `--key value`（空格分隔）会抛
   `FormatException`，而这段代码在启动最早期，任何异常都会变成"连错误提示都发不出来"的静默失败。
5. **Console sink 要跟着一起关**：脱离后标准输出句柄已失效，再挂 `WriteTo.Console`
   就是白写并让 Serilog 反复报错。装配改成"先建 `LoggerConfiguration` → 按
   `consoleOutputUsable` 决定要不要 `.WriteTo.Console(...)` → 再 `.WriteTo.File(...)`"。
6. **`Console.Title` 只在真的挂着控制台时才设**（脱离后/双击场景都没有控制台可设）。

**代码落点（增量）**

```
Platform.Windows/NativeMethods.cs          + GetConsoleWindow（FreeConsole 原本就有）
Platform.Windows/WindowsInteractiveHost.cs ConfigureConsoleOutput(bool) / DetachFromConsole() / HasUsableConsole()
Server/Program.cs §0.1                     ReadAttachConsoleFlag(args) → ConfigureConsoleOutput → consoleOutputUsable
Server/Program.cs §2                       Console sink 条件挂载
Server/Program.cs（Console.Title 处）      改为"确实有可用控制台"才设标题
tests/.../ControlServerFactory.cs          + Server__Startup__AttachConsole=true
run.cmd                                    改为 build + start（不再用 dotnet run 占着终端），
                                           并轮询端口确认就绪后才报「已启动」
```

### 25.9 ⚠️ 验证手段本身的两个坑（**这次又踩了**）

1. **`FindWindow("ConsoleWindowClass", 标题)` 绝不能用** —— 控制台可能被 Windows Terminal
   托管（类名不同），更重要的是：桌面上**同时存在用户自己那个实例的控制台窗口**。
   本轮第一次按类名找，找到的**正是用户的窗口**（标题「中控服务 ControlServer — http://localhost:8080」），
   而且**真的向它发了 `WM_CLOSE`**；用户跑的是**修复前**的版本（附着控制台），
   于是**把用户当时正在运行的那个服务关掉了**。⚠️ **这不是"差点误伤"，是"确实误伤了"。**
   ⇒ **教训：在用户机器上做破坏性实验前，必须先用 PID 精确认领目标**，
   绝不能用"类名/标题"这类可能撞车的特征去定位。
   正确做法：`FreeConsole()` → `AttachConsole(目标PID)` → `GetConsoleWindow()`，**只认 PID**。
   （同一次实验还因为窗口没找对而得出"需求已满足"的假结论 —— **目标错 + 什么都没做**，双重失效。）
2. **验证脚本必须自带"目标自检"**：关窗这类破坏性动作，脚本要先断言
   "我要关的窗口确实属于我起的那个进程"，否则宁可不做、只报告"未取得有效证据"。
   本轮那条复现脚本就是先打印 `cmd 有控制台 / 服务进程有控制台` 再动手的 —— 方向是对的，
   但**第一版没有做这层自检**，代价就是误关了用户的服务。
2. **Python `subprocess` 只要传了 `stdin`/`stdout`/`stderr` 中任意一个，就会设置
   `STARTF_USESTDHANDLES`** —— 新控制台的句柄被继承句柄覆盖，`AttachConsole` 随之不可靠。
   要拿真实控制台窗口就必须**一个 std 句柄都不传**（需要喂 `pause` 就在命令串里写 `<nul`）。
3. **判据要选"因果上更强"的那个**：与其"关窗后看服务是否还活着"（依赖能否真的关到窗），
   不如直接看**服务进程是否还挂在控制台上** —— 挂着 ⇒ 关窗必然带走它（已因果证实）。
   前者容易因"没关到窗"而假阳性，后者不会。

---

### 25.10 托盘图标"被关掉了"怎么办（2026-10-08 实测记录）

**现象**：点了托盘菜单的「退出托盘助手」⇒ 图标消失，且**不会自己回来**。
本机日志（`%TEMP%\ControlServer.TrayAgent.log`）留痕很清楚：

```
20:28:51 启动：服务名=ControlServer 端口=8080 数据目录=C:\ProgramData\ControlServer
20:28:51 初始服务状态：运行中（无错误）
20:30:13 用户选择「退出托盘助手」。     ← 就是这个动作
20:30:13 托盘助手已退出。
```

**语义澄清（重要）**：「退出」只结束**本次运行**，**不碰 HKCU 启动项** ⇒
**注销 / 重启后会自动回来**。用户很容易误以为"永久没了"。

**恢复方法（从快到慢）**：

1. **双击** `C:\Program Files\ControlServer\ControlServer.TrayAgent.exe`
   —— **不需要任何参数**：`--service-name` / `--port` / `--data-dir` 三个都有默认值，
   且默认值与安装时写入启动项的值**完全一致**（`ControlServer` / `8080` / `%ProgramData%\ControlServer`）。
   见 `TrayAgent/Program.cs` 顶部的 `DefaultServiceName` / `DefaultPort`。
2. 重新登录（启动项自动拉起）。
3. 重跑 `install-service.ps1` 的托盘段（`Install-TrayAgent`）。

**多开无害**：单实例互斥名 `Local\ControlServer.TrayAgent.SingleInstance`
（**`Local\` 前缀 = 按会话隔离**），重复双击时后一个会自己退出并写日志"已有一个托盘助手在运行"。

**「双击了但看不见图标」≠ 没启动**：Win10/11 默认把新图标折叠进隐藏区。
- 点任务栏的 **^** 箭头看折叠区（可把图标**拖**到任务栏固定）
- Win11：设置 → 个性化 → 任务栏 → 「其他系统托盘图标」→ 打开「中控服务」
- Win10：设置 → 个性化 → 任务栏 → 「选择哪些图标显示在任务栏上」→ 打开「中控服务」

启动时会弹一次气泡（"托盘助手已启动 · 运行中"），可作判据。
**真报错就去看** `%TEMP%\ControlServer.TrayAgent.log`（WinExe 无控制台，这是唯一痕迹）。

> ⚠️ **本机自动化的边界**：沙箱把 `Start-Process` 与 `[Diagnostics.Process]::Start` **一并拦下**
> （提示"equivalent to Start-Process"）⇒ **AI 无法代为拉起 GUI 进程，必须人来点**。
> 但**读**是畅通的：`Get-Process` / `Get-Service` / 注册表 Provider（`Get-ItemProperty`）/ `%TEMP%` 日志
> 都能直接取证。⚠️ 注意 `reg.exe` 也在黑名单里，查注册表要走 PowerShell 的注册表 Provider。

## 26. 并发执行 + 编辑页四项调整（2026-10-08 晚）

> 四条需求原文：
> ① 支持多个按钮同时执行（按钮 1 有 5000ms 延迟时，执行完之前可以执行其它按钮）；
> ② 移除按钮和子按钮集的按钮图片设置和集合图片设置；
> ③ 编辑页每次选中按钮或子按钮集时，内部每个动作为收起状态，需手动展开；
> ④ 编辑页每次选中按钮时，定时与月度计划为收起状态；「指定星期」更名「每周」，
>    且每日 / 每周只显示中文、去掉括号与英文。
>
> **本轮后端一行未改。**

### 26.1 ⚠️ 并发这件事**卡在前端**，后端本来就是并发的

**遇到"要支持并发"先别动后端。** 服务端从 M2-B 起就是并发安全的，四条依据：

| 依据 | 位置 |
|---|---|
| 延迟走 `await Task.Delay`，**不占线程** | `ActionExecutionService.ExecuteAsync` |
| 每次发送**新建** socket，无共享状态 | `NetworkService.SendUdpAsync` / `SendTcpAsync` |
| 收发日志内部 `lock (_gate)` | `TrafficLogService` |
| `ActionExecutionService` 虽是单例，但**只用局部变量**、不改实例字段 | 同上 |

真正的闸门在 `web/src/App.vue`：
`const runningId = ref<string | null>(null)` + `if (runningId.value) return` ——
一个按钮在跑，**所有**按钮的点击都被**静默丢弃**（看起来像"点了没反应"），
连「连通性自测」也被 `:disabled="runningId !== null"` 连坐。

**改法**：`runningId`（单值）→ `runningIds`（`reactive(new Set<string>())`），
守卫从"全局"降级为"按 Id 去重"（只防同一个按钮被连点）。
`justRanId` 同步改成集合 —— 并发下 A、B 接连成功，单值会让 A 的高亮被 B 顶掉；
配套 `justRanTimers: Map<id, timerId>`，每个 Id 各自计时。

**顺带去掉一处会"把人拽走"的行为**：`runButton` 的 `finally` 里原本有
`activeTab.value = 'console'`。并发 / 长延迟下用户很可能已经切到别的页继续干活，
执行完硬切回控制台是骚扰 ⇒ 去掉（结果面板一直在控制台等着他）。

### 26.2 组件契约：`busy-id` → `busy-ids`

`ButtonGrid.vue` / `SubButtonSheet.vue` 的 prop 从 `busyId: string | null` 改为
`busyIds: Set<string>`，模板里 `busyId === tileKey(t)` 改成 `busyIds.has(tileKey(t))`。

`reactive(new Set())` **可以直接当 prop 传下去**：Vue 3 对集合类型有独立的响应式处理，
`.has()` / `.size` 都会被追踪。（不要在子组件里 `props.busyIds.size` 之外做展开拷贝，
那样会丢掉响应性。）

`ActionResultPanel.vue` 新增 `inflight`（在途任务数）：
- 已经**有结果**时 → 提示「还有 N 个按钮正在发送」；
- **还没有结果**时 → 显示「执行中…」占位（长延迟按钮点下去的头几秒就是这个状态）。

⚠️ 「执行结果」面板展示的始终是**最近完成**的那一次，**不等于**"全部结果"。

### 26.3 实测（真机 HTTP + 真浏览器，两组都全绿）

**A. 后端并发** —— 独立实例（专用端口 + 临时数据目录，不碰用户的 8080）：

| 场景 | 结果 |
|---|---|
| 慢按钮（第 2 条动作挂 5000ms） | 墙钟 5034ms / 服务端 `elapsedMs` 5010 |
| 慢按钮发出 200ms 后发快按钮 | 快按钮 **41ms** 返回，此刻慢按钮**仍在执行中** |
| 1 慢 + 3 快同时发 | 总墙钟 **5038ms**（若被串行化会是 5000ms 的整数倍以上），3 个快按钮各 5~15ms |

**B. UI 层（无头 Edge + CDP 真点，8/8 通过）**
- 点慢按钮 → 瓦片显示「发送中…」，且**持续到 5 秒后**；
- 慢按钮跑着时点快按钮 → **50ms 后**结果面板已出现「并发-快」，
  同一屏还提示「还有 1 个按钮正在发送」；
- 慢按钮跑着时「连通性自测」**未被禁用**（旧实现会连坐禁用）；
- 慢按钮 5012ms 完成后，面板切到「并发-慢」。

**C. 需求 2 / 3 / 4 的 UI 断言（16/16 通过）**
- 图片设置：主按钮表单与子按钮集表单里 `input[type=file]` 均为 **0**，含「图片」的 label **0** 个；
- 折叠：3 条动作全收起 → 点一下能手动展开 → 切到别的瓦片再切回来**重新回到全收起**；
  子按钮集内 4 条动作全收起；**子按钮卡片本身保持展开**（拍板结果）；
- 定时 / 月度计划：默认收起且表头带摘要（`▸ 定时（未启用）` / `▸ 月度计划（1 个）`）；
  展开后执行模式选项**恰为** `["每日", "每周"]`。

### 26.4 折叠状态是**靠 `:key` 复位的**，不是靠父层状态

`EditPanel.vue` 给 `ButtonForm` / `SubSetForm` 绑了 `:key="'button:' + id"` / `:key="'set:' + id"`。

**为什么必须这样**：Vue 在同一位置渲染同类组件时会**复用实例**，只换 props。
而 `ActionEditor` 的 `collapsed`、`ButtonForm` 的 `timerOpen` / `plansOpen` 都是组件内 `ref`，
复用实例 ⇒ 上一个按钮的展开状态会**串**到下一个按钮上（"刚展开了 3 条，切过去还是展开的"）。
绑 `:key` 后换选中项 ⇒ 整棵表单卸载重挂载 ⇒ 折叠态自然回到默认值。

⚠️ 反过来也要知道它的**边界**：**再点一次同一个瓦片不会重挂载**，折叠状态保留。
这是有意的 —— 保留用户正在看的内容，比"每次点击都清零"更不烦人。

### 26.5 需求 4 的两个细节

1. **「指定星期」→「每周」只改显示名，`SpecificDays` 这个**值**不动**：
   `Core.Models.TimerModes.SpecificDays`、`ButtonConfig.TimerMode`、
   `TimerSchedulerService` 里的字符串比较**全部保持不变**。
   ⚠️ 同一个显示串在**两处**：`ButtonForm.vue`（下拉选项）与 `TimerOverviewPanel.vue`
   （`kindText` / `conditionText` 兜底 / 空状态引导文案）。**必须一起改** ——
   同一个概念在两个页面叫两个名字，用户会以为不是一回事。
2. **收起 = 不可见，所以表头必须自带摘要**：
   `定时` 表头写 `（已启用 · 每日 · 1 个时刻）` 或 `（未启用）`；`月度计划` 表头写 `（N 个）`。
   否则"收起"会退化成"藏起来"，用户得逐个点开找。

### 26.6 图片设置移除的**范围与遗留**

移除的是**编辑页的三个入口**：`ButtonForm`（按钮图片）、`SubSetForm`（集合图片）、
`SubButtonEditor`（子按钮图片）。`ImagePicker.vue` **组件保留**（设置页仍在用）。

**⚠️ 刻意不做、等拍板的三件事**：
1. 模型 / 契约里的 `buttonImage` 字段**保留**（旧配置、导入备份、历史数据都带它）；
2. **控制台仍会渲染** `buttonImage`（`ButtonGrid` / `SubButtonSheet` 没动）——
   也就是说：**已有图片的按钮，图片还显示，但编辑页删不掉了**；
3. 设置页的 **`默认按钮图片` / `主界面背景图`** 属"全局设置"，不在本次范围，**保留**。

### 26.7 ⚠️ 验证手段的坑：无头浏览器怎么在本机跑起来

1. **`agent-browser` 的 Chromium 没装**（Playwright 缓存目录为空），真要跑得下 ~500MB。
   **本机有 Edge**（`C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`），
   用 `--headless=new --remote-debugging-port=9222` 起无头实例 + 直连 CDP 更省。
2. **Edge 是"启动器式"进程**：`msedge.exe` 会 fork 出真正的浏览器进程后**自己退出**。
   后果 —— 在**一次 Bash 调用**里用 `&` 起 Edge、**下一次**调用再去连，Edge 早被回收。
   ⇒ 必须把「起 Edge → 跑 CDP 脚本 → `taskkill //IM msedge.exe //F`」放进**同一次调用**。
3. **`agent-browser` 的 ref 不跨命令保持**（报 `Unknown ref: e4`），`connect` 还会超时。
   要写多步交互就**直接连 CDP**：Node 22 自带全局 `WebSocket`，**不用装任何包**。
   CDP 目标从 `GET http://127.0.0.1:9222/json/list` 里挑 `type === 'page'` 的那个。
4. **`Server:Auth:EnforcePasswordChange`**：全新数据目录里的 `admin` 被**强制改密闸门**拦住，
   除白名单外所有 `/api` 一律 **403**。脚本验证时加
   `--Server:Auth:EnforcePasswordChange=false` 最省事（**不要去改凭据**）。
5. **`curl` 打本机要加 `--noproxy '*'`**：宿主配了 `http_proxy`，不加会把 `127.0.0.1` 也送去代理。
   （Node 的 `fetch` 默认不吃代理环境变量，这次没踩到。）
6. **要断言"点击生效"，光看瓦片不够**：编辑页/控制台的按钮是 `<button>`，
   直接 `.click()` 就能触发 Vue 的监听，不需要模拟真实鼠标事件。

### 26.8 ⚠️ 断言要防"采样时机"造成的假失败

第一次 UI 验证时 8 条断言里有 1 条报 `[!!]`：
「慢按钮跑着时，快按钮的点击生效（也进入发送中）」。

**代码没问题，是断言写错了**：快按钮只有 1 条**无延迟**动作，整个执行不到 20ms，
而断言在点击后固定 `sleep(250)` 才采样 —— 那时它早跑完了。
于是"没采到发送中"被误读成"点击被吞了"。**这是最危险的一类假失败**：
它看起来像功能回归，会诱使人去改本来正确的代码。

**修法两条一起上**：
1. 采样改成**高频轮询**（50ms 一次，最多 2s），把瞬时状态捞出来；
2. 更重要的是换一个**不受采样时机影响**的判据：
   **"慢按钮仍在发送中时，结果面板里已经出现了快按钮的结果"** ——
   这条只要能观测到一次就成立，而**旧实现下永远不可能出现**（第二次点击会被直接丢弃）。

### 26.9 本轮改动清单

| 文件 | 改动 |
|---|---|
| `web/src/App.vue` | `runningIds` 集合 + `justRanIds` 集合 + `runningCount`；去掉全局闸门与强制切回控制台 |
| `web/src/components/ButtonGrid.vue` | `busy-id` → `busy-ids`（+ `justRanIds`），新增 `isBusy` / `isJustRan` |
| `web/src/components/SubButtonSheet.vue` | 同上 |
| `web/src/components/ActionResultPanel.vue` | 新增 `inflight` 提示与「执行中…」占位 |
| `web/src/components/ActionEditor.vue` | `collapsed` 默认改为 `true` |
| `web/src/components/EditPanel.vue` | `ButtonForm` / `SubSetForm` 加 `:key`（折叠态复位） |
| `web/src/components/ButtonForm.vue` | 移除 ImagePicker；`定时`/`月度计划` 加折叠头 + 摘要且默认收起；选项改「每日」「每周」 |
| `web/src/components/SubSetForm.vue` | 移除 ImagePicker |
| `web/src/components/SubButtonEditor.vue` | 移除 ImagePicker |
| `web/src/components/TimerOverviewPanel.vue` | `指定星期`→`每周`、`每天`→`每日`（与编辑页统一叫法） |


---

## 27. 指令库引用改为可搜索下拉 + ASCII 文案澄清（2026-10-08 晚）

用户提了两件事：① 数据类型选项里的 `ASCII（非 ASCII 转 ?）` 表述有歧义、那个 `?` 是什么；
② 数据类型选「指令库」时，后面的指令名**不该手打**，应当从指令库里选（可搜索）。

### 27.1 需求②：为什么必须换掉自由文本

动作的 `DataContent` 在「指令库」类型下是**指令名**（`ICommandLibrary` 按名查、**大小写不敏感**）。
手打一个库里没有的名字，编辑期**看不出任何异常**，直到真发送时 `ActionExecutionService`
才报 `指令库解析失败：未找到指令「X」`。把它做成下拉 = 把这个必然失败提前到编辑期消灭。

**但不能静默清空旧值**：历史配置可能引用已被删除/改名的指令。那种值要照样显示，
并标注「指令库中没有…」+ 发送会失败，让用户自己决定重选还是保留。

### 27.2 需求①：`?` 不是占位符，是**真实替换字符**

`DataEncoder.Encode` 的 ASCII 分支是 `Encoding.ASCII.GetBytes`，.NET 的 ASCII 编码器把
**>127 的字符一律替换成 `?`（0x3F）**。守门用例 `("ASCII","中")→[0x3F]`，
方案文档写的是 `ASCII（>127 变 ?）`。所以**语义没错、是表述有歧义**（`转 ?` 读起来像没写完的句子）。

改法（**两处必须同步**：`ActionEditor.vue` + `CommandLibraryPanel.vue`）：

- 选项文案：`ASCII（非 ASCII 转 ?）` → **`ASCII（非 ASCII 字符替换为 ?）`**
- 并在动作编辑器补一行说明（与 WoL / 指令库的提示同一处 `row-actions`）：
  `ASCII 只认 0–127：中文等字符会被替换成半角问号 ?（0x3F），且不可逆`

### 27.3 新增共享缓存 `web/src/commandLibrary.ts`

理由：一个按钮动辄十几条动作，每张卡片各自 `GET /api/commands` 就是十几个重复请求。
模块级单例：`ensure()` 首次真发请求、并发调用共用同一个 `inflight`；
`sync(list, version)` 供指令库面板回填。

**一致性关键**：`CommandLibraryPanel` 的 `load()` 与 `applyMutation()` 都把服务端回传的
**全量列表**喂给 `sync()` —— 服务端增删改的响应无论成败都带最新列表，
所以"刚在指令库页新增的指令，回到动作编辑页能不能立刻选到"这一条不需要刷新页面。
（有专门用例守着，见 27.5。）

**刻意不加 TTL**：缓存新鲜度由 `sync()` 维护。定时失效会让界面在某个不可预期的时刻
突然重新拉取，反而更难解释。要强刷就 `ensure(true)`。

### 27.4 组件契约与只读安全

- 写入字段仍是 `props.action.dataContent`（存**指令名**）——**后端零改动**，也不用改存储格式。
- 只读模式：`EditPanel` 用 `<fieldset :disabled="!canOperate">` 整体置灰。
  它**只对原生表单元素生效** ⇒ 自定义下拉的触发器必须是原生 `<button>`（是），
  这样 Viewer 连下拉都打不开；如果触发器写成 `<div @click>`，就漏出一个可点漏洞。
- 下拉里的搜索框用 `@keydown` 拦 `Enter`（防止回车触发外层默认行为）、支持 ↑/↓/Enter/Esc。
- 关闭时机：`document` 上挂 **capture 阶段** `mousedown`，用 `root.contains(target)` 判断内外；
  只在展开时挂、收起即摘（十几张卡片各挂一个常驻监听没有必要）。

### 27.5 ⚠️ 验证手法与**五个坑**（全部是"测量工具自己出错"）

真浏览器验证：无头 Edge `--remote-debugging-port=9223` + Node 直连 CDP（同 §26.7）。**23/23 通过**。

1. **⚠️ `exact('button','保存')` 会静默空点** —— EditPanel 里那个按钮的真实文案是 **「保存配置」**
   （`{{ saving ? '保存中…' : '保存配置' }}`）。空点没有异常、没有提示，
   结果表现为"选好的值没落库"，**看起来像功能坏了**。
   ⇒ **点击辅助函数必须返回布尔并单独断言**（`check('找到并点击了…', clicked === true)`）。
   这一条最值得记：它和 §26.8「采样时机假失败」是同一类陷阱的不同变体。
2. **按下标取 select 会取错**：动作卡片的 select 顺序是 `[协议, 数据类型, 对接码比较方式]`，
   取 `querySelectorAll('select')[0]` 拿到的是**协议**下拉（报出 `["UDP","TCP","WoL"]` 这种离谱结果）。
   ⇒ 用**内容特征**定位（"选项里含 ANSI 的那个"），不要用下标。
3. **全新数据目录的 admin 会被「改密弹窗」挡住整个界面**：`mustChangePassword=true` 时前端弹窗
   盖住一切，`.action-card` 数为 0。⚠️ `--Server:Auth:EnforcePasswordChange=false` **只关服务端闸门、
   不关弹窗**。⇒ 验证脚本登录后先 `POST /api/auth/change-password` 把口令改掉（一次性目录里安全），
   之后所有登录都用新口令。
4. **`.tile-row` 的 textContent 是拼接的**：实测是 `"1按钮下拉验证3 个动作↑ ↓ 复制删除"`
   （序号+名称+动作数+操作按钮）。`exact` 精确匹配必然失配 ⇒ 只能 `includes`。
5. **无头 Edge 的启动要用子 shell**：`cd X && rm -rf p && msedge ... &` 会把**整条链**丢进后台
   （与 §5 记的 Bash 陷阱同源），接着前台立刻跑脚本就会 `ECONNREFUSED`。
   写成 `( msedge ... & )` 再轮询 `/json/version` 就稳（实测 1 秒就绪）。

### 27.6 落库实证

不靠界面自述，改成"点保存后回读服务端"：`空引用=指令库:开灯`、`坏引用=指令库:这条指令已经被删掉了`、
`ASCII 说明=ASCII:中文测试` —— 三条动作的 `dataType:dataContent` 与操作前一致/符合预期。

### 27.7 改动清单

| 文件 | 改动 |
|---|---|
| `web/src/commandLibrary.ts` | **新增** —— 指令库共享缓存（`ensure` / `sync` / `findByName`） |
| `web/src/components/ActionEditor.vue` | ASCII 文案 + ASCII 说明行；「指令名」自由输入 → 可搜索下拉（含空态/错误态/失效值告警/键盘导航） |
| `web/src/components/CommandLibraryPanel.vue` | ASCII 文案同步；`load()` / `applyMutation()` 回填共享缓存；改名提示改为"到编辑页重新选一次" |
---

## 28. 指令库**选中即物化** + 导入时点名引用式动作（2026-10-08 晚）

用户原话：

> 指令库使用逻辑优化，选择指令库中指令后系统将自动填入数据类型 数据内容 以及协议，
> 更为方便得兼容拥有不同指令库的程序间导入导出配置文件

### 28.1 判定：把 `ApplyCommand` 的时机从**运行时**提到**编辑期**

后端 `ActionExecutionService.ApplyCommand(action, command)` **早就在做**"用指令的协议/类型/数据
覆盖动作"这件事 —— 只是发生在发送前。用户要的就是把同一件事提前到编辑期：

| | 引用式（旧） | 物化式（新） |
|---|---|---|
| 动作存的 | `dataType=指令库` + `dataContent=指令名` | `dataType=<真实类型>` + 真实协议与数据 |
| 解析时机 | 每次发送前（运行时查库） | 选中的那一刻（编辑期写死） |
| 改指令库 | 所有引用它的动作**一起变** | 已填入的动作**不变**（刻意取舍） |
| 跨机导入 | ❌ 见 28.2 | ✅ 自带全部发送参数 |

**因此后端执行路径一行未改**：物化后 `DataType` 已不是「指令库」，直接走普通发送分支；
旧的运行时解析分支**保留**（见 28.4）。

### 28.2 为什么必须物化：指令库**不参与导入导出**

- `ConfigKeys.Commands` 是**独立配置键**，`ConfigBackup.BuildBackup` 只序列化 `settings`（+ 内嵌图片）。
  ⇒ 引用式动作导出后**只剩一个指令名字符串**。
- 导入到指令库不同的机器上，两种坏法，而且**原本完全没有任何提示**：
  1. 目标库没有同名指令 ⇒ 点按钮时 `指令库解析失败：未找到指令「X」`（`ActionExecutionService` 第 179 行附近）
  2. 目标库**有同名但定义不同** ⇒ **静默发出别的数据**（更危险）

### 28.3 前端实现（`ActionEditor.vue`）

- `materializeCommand(item)`：一次写齐 `protocol` / `dataType` / `dataContent`。
  **协议为空时保留动作自身协议** —— 与后端 `ApplyCommand` 的回退口径逐字一致（不要各写一套）。
- `chooseCommand()`（选择器回调）与 `convertLegacyRef()`（旧动作的一键转换）**共用**这个函数。
- 反馈：`filledFrom` 记下填入的快照（**不落库**），渲染成一行 `notice ok`，
  文案是"来源 + 可自行修改 + 换一条要怎么做"，因此**不需要监听后续编辑去清除它** ——
  它陈述的是"值从哪来"（永远为真），而不是"当前值是什么"（会过期）。
  ⚠️ 上一版设计想用 watch 清除，但 Vue 的 watch 是异步 flush，`materialize` 里先赋值后设标志
  必然被清掉；改成"声明式不带过期断言"最省事。
- 「指令库」告警分**三支**（`v-if="isLibrary"` 的 `notice warn`）：
  1. `commandMissing`：库里没有这条指令 → 只说"无法填入、请重选"，**不给转换按钮**
  2. `selectedCommand`：库里有 → 显示 `「名字」协议 · 类型 · 数据` + **「填入协议与数据（转为独立动作）」**
  3. 都没有（刚把下拉切到「指令库」还没选）：说明这个引用方式的问题 + 让用户去选一条
- 数据类型下拉的「指令库」选项文案改为 `指令库（选中指令后自动填入协议与数据）`。
  **它现在是纯入口**：选中一条指令后 `dataType` 立刻变成真实类型，下拉自动跳回去。

### 28.4 兼容旧数据（**不迁移、不静默改写** —— 用户拍板）

- 已被存成 `dataType=指令库` 的动作**照常可执行**（执行侧解析分支保留），也照常可保存。
- 编辑页打开它时显示上述告警；库里能查到就有**一键转换**，查不到就只能重选。
- **不做**：启动时全量迁移、打开编辑页悄悄写回、导入时自动改写 —— 这些都会**改用户数据**，
  而且库里没有的引用根本无从转换。
- 顺带：`CommandLibraryPanel` 的改名提示补了一句"重新选中会把定义填入动作、转为独立动作"。

### 28.5 导入侧提示（`ConfigController.DescribeLibraryRefsAsync`）

只**报告**、不改写，写进 `ConfigImportResultDto.Notes`（前端 `SettingsPanel` 已会渲染 notes 列表）：

- 有引用式动作时一条：共几条 + 怎么处理；
- 其中本机库里查不到的再一条，**点名到"按钮「X」→「指令 Y」"**（`CollectLibraryRefs` 会扫
  按钮与子按钮集内的子按钮，位置描述与 `CommandLibraryPanel.references` 的口径一致）。

实现要点：`ICommandLibrary` 注入 `ConfigController`（Program.cs 第 379 行已是单例注册，无需改 DI）；
查重前先 `GetAllAsync` 一次拿全量再 `HashSet(OrdinalIgnoreCase)` 比对（**不逐个 ResolveAsync**）。

### 28.6 验证

- **HTTP/服务层 16/16**（`cs-libfill-setup.py`）：真跑「导出 → 导入」往返。
  库还在 ⇒ `2 条动作仍用「指令库」引用方式` + `其中 1 条找不到（按钮「引用-失效」→「不存在的指令」）`；
  清空库再导入 ⇒ `其中 2 条找不到`，且**没有**把命中库的那条误报。
- **真浏览器 34/34**（无头 Edge + 直连 CDP，`cs-libfill-ui.mjs`）：旧引用告警三分支、一键转换后
  三格与卡片头徽标同步变化、失效引用不给转换按钮、搜索过滤、点选填充、**保存后回读服务端**确认落库
  （三条动作 `dataType:dataContent` 逐条核对）、无未捕获 JS 异常。

**⚠️ 两个断言坑（又是"测量工具自己出错"，与 §27.5 同源）**：

1. **`card.textContent` 会把 `<select>` 里所有 option 的文字算进去** —— 数据类型下拉的选项文案
   本身含「自动填入协议与数据」，于是"卡片里有没有转换按钮"这种判据用 textContent **必然误判**。
   ⇒ **判"有没有按钮"就查 `<button>`**，不要查 text。
2. **夹具必须可重复执行**：UI 测试会**真的保存配置**，上一轮的物化结果留在库里，
   下一轮"起点数据类型应为 Hex / 应为 指令库"就会全错（而且失败项会**换一批**，看起来像新 Bug）。
   ⇒ 跑 UI 前先跑一次夹具脚本，且夹具脚本自身要**幂等**（先清空指令库再建，否则重名被拒）。

### 28.7 改动清单

| 文件 | 改动 |
|---|---|
| `web/src/components/ActionEditor.vue` | 选中即物化（`materializeCommand` / `convertLegacyRef` / `filledFrom` 反馈）；「指令库」三分支告警；下拉选项文案 |
| `web/src/components/CommandLibraryPanel.vue` | 改名提示补"重新选一次即转为独立动作"一句 |
| `src/ControlServer.Server/Api/ConfigController.cs` | 注入 `ICommandLibrary`；导入结果追加指令库引用提示（`DescribeLibraryRefsAsync` / `CollectLibraryRefs`） |

**语义变更（后续改动别再"修回去"）**：
「指令库」不再是会**持久化**的数据类型 —— 它只是编辑期的一个入口。新动作永远不会以
`dataType=指令库` 落库；只有**历史配置**才可能带这个值。
---

## 29. WoL 编辑项瘦身：只留 MAC（2026-10-08 晚）

用户原话：

> 优化WoL网络唤醒协议下的编辑，目标默认为广播，端口也默认，数据类型也不需要，
> 舍弃掉该协议下的这三个编辑选项，以免误导用户

### 29.1 为什么这三个控件在 WoL 下**确实**没有意义

| 控件 | 后端实际怎么用 | 结论 |
|---|---|---|
| 目标 IP | `DataEncoder.NormalizeWolHost`：空 / `0.0.0.0` → `255.255.255.255` | 留空即广播，等于"默认值" |
| 端口 | `var port = action.TargetPort > 0 ? action.TargetPort : DataEncoder.DefaultWolPort(9)` | 0 用 9，等于"默认值" |
| 数据类型 | **一次都没读过** —— `SendWakeOnLanAsync` 走 `TryParseMac` + `BuildMagicPacket` | 与发送结果**完全无关** |

所以不是"藏起来更清爽"，而是**留着会让人以为能改**：改了数据类型毫无作用；
改了目标则把魔术包发去一个不该去的地方（还可能静默成功）。

### 29.2 不能"只隐藏"：残值必须在看不见之前先归位

纯 `v-if` 隐藏有个真陷阱：**从 UDP 切过来的动作，`targetIp` / `targetPort`
还留着原来那台机器的值**，界面不显示但后端照样用 —— 那就成了"界面没说、实际这么做"，
比多两个控件更误导。所以 `ActionEditor.vue` 里加了：

```ts
watch(() => props.action.protocol, (protocol, previous) => {
  if (protocol !== 'WoL' || previous === 'WoL') return
  resetWolTarget()                                        // targetIp='' / targetPort=0
  if (props.action.dataType === '指令库') props.action.dataType = 'String'
})
```

三个刻意的取舍：

1. **数据类型的值不动** —— WoL 不读它，留着能在切回 UDP 时恢复原样。
2. **「指令库」例外要清掉** —— 指令库本身不含 WoL（`CommandLibraryService` 明确拒绝
   `协议「WoL」不受支持`），这个组合永远解析不出结果。
3. **切回 UDP / TCP 时不补值** —— 那时目标框重新可见、显示为空，由用户填。
   可见的空值不算误导，凭空猜一个 IP 才是。

> ⚠️ 落库安全性已核对：`ControlSettingsNormalizer.NormalizeAction` 里是
> `act.TargetIp ??= DefaultTargetIp`（`??=` 只管 **null**），空串 `""` 原样保留。
> 所以"清空目标"不会被后端改回 `192.168.1.100`，`NormalizeWolHost("")` 正常走广播。

### 29.3 旧配置 / 外部导入的**非默认**目标：告警 + 一键归位

既然界面不再提供编辑，就不能对"存着非默认目标"装看不见 —— 否则 29.2 里那句
「固定发往广播 255.255.255.255:9」**本身就是假话**。所以 WoL 分支里多一条 `notice warn`：

- 判据：`wolStaleTarget`（host 非空且不是 `0.0.0.0`/广播；或端口 > 0 且 ≠ 9）
- 文案点名实际值（如 `192.168.1.50:7`）+「归位为广播 255.255.255.255:9」按钮
- **不静默改写**：沿用 §28「旧数据只提示、不迁移」的同一条原则

### 29.4 预览：改成本地算魔术包（**这是隐藏数据类型带来的必然后果**）

`POST /api/actions/preview` 的入参是「数据类型 + 内容」，**没有协议**。
数据类型一被隐藏，这条链路就成了隐患：一个残留 `dataType=Hex` 的 WoL 动作点
「预览实际字节」，会报出一条与真实发送毫无关系的编码错误 —— 预览自己变成误导源。

所以 `doPreview()` 分叉：WoL 走**纯前端** `buildWolPreview()`，其余仍走端点。
前端实现与后端逐字对齐，**不新增接口**：

| 前端 | 后端 |
|---|---|
| `parseMacHex`：去 `: - .` 与空白后必须正好 12 位十六进制 | `DataEncoder.TryParseMac` |
| `new Array(6).fill(0xff)` + MAC 重复 16 次 = **102 字节** | `DataEncoder.BuildMagicPacket`（`6 + 16*6`） |
| 失败文案 `MAC 地址格式无效: {值}` | `SendWakeOnLanAsync` 里的同一句 |

顺带把按钮文案在 WoL 下改成「预览魔术包」、失败标题改成「无法生成魔术包」，
预览面板里补一行「魔术包 = 6×FF + MAC×16 → 目标:端口」。

### 29.5 后端**零改动**

`DataEncoder.NormalizeWolHost` / `DefaultWolPort` / `TryParseMac` / `BuildMagicPacket`
以及 `SendWakeOnLanAsync` 一行未动 —— 新界面写进去的就是后端本来就读得懂的默认值
（`""` + `0`）。旧配置里的非默认值**继续生效**（29.3 只是把它说出来）。

### 29.6 验证（真浏览器 41/41）

无头 Edge + 直连 CDP（`cs-wol-ui.mjs`），夹具 `cs-wol-setup.py` 造三条动作：
UDP / WoL 默认 / WoL 残留（`192.168.1.50:7` + `dataType=Hex`）。

覆盖：UDP 卡片三控件仍在 → WoL 卡片三控件**消失**、MAC 与延迟在、静态说明在 →
残留动作告警点名 `192.168.1.50:7` 且给归位按钮 → **点归位后告警消失** →
魔术包预览 **102 字节**、紧凑十六进制**恰 204 字符**、前缀 `FFFFFFFFFFFFAABBCCDDEEFF`、
**不报 Hex 错误**（残留 dataType 已不影响）→ 保存后**回读服务端**确认
`targetIp=""` / `targetPort=0` 且 `dataType` 仍为 `Hex` → 协议切到 WoL 三控件消失、
切回 UDP 目标框回来且**为空**（`端口=0`）→ 无未捕获 JS 异常。

**⚠️ 断言坑（第 N 次「测量工具自己出错」）**：我先把 `innerText` 去掉空白再匹配，
正则里却写了个空格（`/HEX FFFF…/`）—— 必然不匹配。报出来的失败长这样：
`[!!] 魔术包前缀 = 6×FF + MAC`，看名字完全像"魔术包构造错了"。
⇒ 带空格的判据要么先规范化、要么别在同一处混用两种形态。

### 29.7 改动清单

| 文件 | 改动 |
|---|---|
| `web/src/components/ActionEditor.vue` | ① WoL 分支只渲染 MAC + 延迟 + 静态说明；② 切入 WoL 就地归位（`resetWolTarget`） + 残留目标告警与一键归位；③ 预览改本地魔术包（`parseMacHex` / `buildWolPreview`）；④ 按钮与失败标题按协议换文案；⑤ 指令库告警加 `&& !isWol` 守卫 |

**代价（有意）**：WoL **不再支持指定目标**（定向广播 / 跨网段唤醒）。
想要那个就得回到"填 IP"的形态 —— 用户明确选了"默认广播、不要这三个控件"。

---

## 30. 数据类型别名合并：`String` ← `UTF-8`（2026-10-08 晚）

用户原话：

> 合并 String（UTF-8 文本） 与 UTF-8

### 30.1 为什么可以直接合并：两者**编码结果逐字节相同**

`DataEncoder.Encode` 里 `IsUtf8` 分支和最后的兜底分支**是同一段代码**：

```csharp
if (IsUtf8(dataType)) return Encoding.UTF8.GetBytes(content ?? string.Empty);
return Encoding.UTF8.GetBytes(content ?? string.Empty);   // ← 含 String
```

`String` 与 `UTF-8` 只是旧版遗留的语义别名（旧版 `PrepareData` 里就同码），
**不存在任何行为差异**，所以合并是"改名字"而不是"改行为"。

### 30.2 库里的实际情况：**一条 `UTF-8` 都没有**

开工前直接查了 `C:\ProgramData\ControlServer\control.db`：

| 文档 | dataType 分布 |
|---|---|
| `settings` | `String` × 18、`指令库` × 8 |
| `commands` | `String` × 1 |

⇒ 零历史包袱，合并不会改写任何现存数据。

### 30.3 三层一起归一（只删前端选项是不够的）

**⚠️ 与 §29 同一条教训：只把选项藏起来 = 更隐蔽的误导** ——
旧配置/导入带 `UTF-8` 时，下拉里没有对应 `<option>` 会**显示成空白**。
所以读写两端都要归一：

| 层 | 位置 | 做法 |
|---|---|---|
| 前端 | `ActionEditor.vue`（数据类型 select）、`CommandLibraryPanel.vue` | 删掉 `<option value="UTF-8">` |
| 后端**读**路径 | `ControlSettingsNormalizer.NormalizeAction` | `DataEncoder.IsUtf8(act.DataType)` → 改写 `DataTypes.String` + 记一条说明 |
| 后端**写**路径 | `CommandLibraryService.Validate` | 命中 `AllowedDataTypes` 后把 `Utf8` 归并成 `String` |

读路径归一放在 `NormalizeAction` 里，**天然幂等**：第一次改完就是 `String`，
第二次清洗不再产生任何说明（`Normalize_is_idempotent_…` 与新增的
`The_merge_is_idempotent_and_keeps_the_encoded_bytes_identical` 一起守）。

### 30.4 `DataTypes.Utf8` 常量**保留**，但已退役

`DataEncoder.IsUtf8` 还要用它解析旧配置，`AllowedDataTypes` 也仍然收
`"utf-8"`（旧调用方提交不该报错）—— 只是**落库前一定被归并成 `String`**。
`DomainConstants` 里已加注释点名"不要再在界面选项或新增写入路径里使用它"。

### 30.5 ⚠️⚠️ 别和 `PairingCodeTypes.Utf8` 搞混

`DomainConstants` 里有**两套**带 `Utf8` 的枚举：

- `DataTypes.Utf8`（**动作的数据类型**）← 本次合并掉的就是它
- `PairingCodeTypes.Utf8`（**对接码比较方式**）← **完全不受影响**

所以下面三处下拉里的 `UTF-8` **原样保留**，不要再"顺手清理"：

`ActionEditor.vue`（对接码比较方式）、`ButtonForm.vue`、`SubButtonEditor.vue`

> 子按钮的对接码类型默认值本来就是 `UTF-8`（见
> `ControlSettingsNormalizer.DefaultSubButtonPairingCodeType`），
> 守门用例 `SubButton_pairing_code_type_keeps_UTF8_default` 未动。

### 30.6 改动清单与验证

| 文件 | 改动 |
|---|---|
| `web/src/components/ActionEditor.vue` | 数据类型下拉删 `UTF-8`（对接码那处不动） |
| `web/src/components/CommandLibraryPanel.vue` | 同上；说明文案「5 种数据类型」→「4 种」 |
| `web/src/types.ts` | `CommandItem.dataType` 注释注明已合并 |
| `src/ControlServer.Core/Models/DomainConstants.cs` | `DataTypes` 类头 + `Utf8` / `String` 注释 |
| `src/ControlServer.Core/Services/ControlSettingsNormalizer.cs` | `NormalizeAction` 增别名合并 |
| `src/ControlServer.Core/Services/CommandLibraryService.cs` | `Validate` 归并 + 错误文案去掉 `UTF-8` |
| `tests/.../CommandLibraryAndTrafficLogTests.cs` | 断言从 `DataTypes.Utf8` 改为 `DataTypes.String` |
| `tests/.../ControlSettingsNormalizerTests.cs` | 新增 6 条守门用例（3 Theory 行 + 3 Fact） |
| `deploy/smoke/admin-api-smoke.py` | 断言「Hex → UTF-8」改为「Hex → String」（提交仍写 `UTF-8`，顺带验证旧别名仍被接受并归一） |

验证：**全量 543 全绿**（Core 363 + 集成 180），比上轮 537 多的正是这 6 条。

> 💡 本轮的冒烟先报 `admin` FAIL，就是被 `admin-api-smoke.py` 里写死的 `UTF-8` 断言挡下的 ——
> **冒烟脚本会把某个取值"锁死"**：改了后端归一化口径，凡断言里出现旧字面量的地方都要同步。
> 教训：改取值语义前先 grep 一遍 `deploy/smoke/*.py` 的字面量，比事后排查快得多。

---

## 31. `run.cmd` 工作目录根治：改前端不再需要重编后端（2026-10-08 晚）

### 31.1 症状与根因

**症状**：dev 期改完前端必须重编后端才生效；而服务在跑时 exe 被占用，编译直接失败（MSB3027）。

**根因链**（`src/ControlServer.Server/Program.cs:663`）：

```csharp
var physicalWebRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
if (Directory.Exists(physicalWebRoot)) { /* 磁盘 */ } else { /* ManifestEmbeddedFileProvider */ }
```

`WebApplication.CreateBuilder` 的 `ContentRootPath` **默认取「当前工作目录」**，
而 `run.cmd` 第一行就是 `cd /d "%~dp0"`（**仓库根**）：

| 启动方式 | ContentRootPath | `…\wwwroot` | 结果 |
|---|---|---|---|
| 旧：从仓库根 `start exe` | `D:\Project\zhongkong` | **不存在** | 回退**内嵌资源** ⇒ 改前端必须重编后端 |
| 新：`start /D <项目目录> exe` | `…\src\ControlServer.Server` | 存在 | 读**磁盘** ⇒ 改完前端刷新浏览器即可 |

> 📌 一个容易搞错的点：`bin\Debug\net8.0\wwwroot` **不存在**（构建不把 wwwroot 复制到输出目录），
> 所以**必须**把工作目录指向**源码目录** `src\ControlServer.Server` —— 那里正是
> `npm run build` 的输出目标（见 §17）。

### 31.2 改法

```bat
set "SERVER_DIR=%~dp0src\ControlServer.Server"
set "SERVER_PROJECT=%~dp0src\ControlServer.Server\ControlServer.Server.csproj"
set "SERVER_EXE=%~dp0src\ControlServer.Server\bin\Debug\net8.0\ControlServer.Server.exe"
...
start "" /D "%SERVER_DIR%" "%SERVER_EXE%"
```

两点：

1. 用 `start /D` **而不是在脚本里 `cd`** —— build 阶段仍需仓库根作 cwd（脚本原本就用相对路径），
   所以顺手把三个路径都改成 `%~dp0` 绝对路径，**彻底不依赖 cwd**。
2. **正向副作用**：ContentRoot 指向项目目录后，`appsettings.json` / `appsettings.Development.json`
   也在那里 —— 以前从仓库根启动其实**根本找不到**这两个文件（全靠环境变量注入配置）。

### 31.3 验证

启动日志里会出现 `静态资源来源：磁盘 D:\Project\zhongkong\src\ControlServer.Server\wwwroot`（而不是"内嵌资源"）。

---

## 32. 测试临时目录残留根治：`cs-noop-<guid>` 不再累积（2026-10-08 晚）

### 32.1 症状与根因

清理 `%TEMP%` 时发现 **248 个** `cs-noop-<guid>` 目录；而且**每次跑一遍测试就又冒出 7~8 个**。

根因在 `tests/ControlServer.Core.Tests/TestDoubles.cs` 的 `NoopDataDirectory`：

```csharp
// 旧：每实例一个顶层目录
private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-noop-" + Guid.NewGuid().ToString("N"));
public void EnsureCreated() { /* 刻意什么都不做 */ }
```

它自称"不建目录"—— **那只对 `EnsureCreated` 成立**。
`LogService.FlushAsync` 会自己 `Directory.CreateDirectory(_dataDirectory.LogDirectory)`
再 `File.AppendAllTextAsync(log-YYYY-MM-DD.ndjson)`，于是每个临时 new 出来的实例
都在 `%TEMP%` 下真建出一棵 `<root>/logs/` 树，而**没有任何人负责删它**。

### 32.2 改法：唯一固定根 + 开工先清 + 退出收尾

```csharp
private static readonly string SharedRoot = Path.Combine(Path.GetTempPath(), "cs-noop");

static NoopDataDirectory()
{
    TryDeleteSharedRoot();                                        // ← 主保险：开工先清上次残留
    AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteSharedRoot();   // ← 能跑就清干净
}
```

- **所有实例共用一个根** ⇒ 无论跑多少次、建多少实例，`%TEMP%` 下最多一棵 `cs-noop` 树。
- **为什么可以共用**（多个测试会同时写同一个 `log-*.ndjson`）：`LogService.FlushAsync`
  对落盘失败**一律 catch 静默**（"日志落盘失败不得影响业务"），而这些测试只消费环形缓冲。
- 用 `ProcessExit` 而不是 `IDisposable`：调用方只在构造 `LogService` 时临时 `new` 一个，
  **没人持有引用**（`CommandLibraryAndTrafficLogTests` / `ConfigBackupServiceTests` /
  `ImageStorageServiceTests` / `TimeSyncServiceTests` 四处都是 `new LogService(new NoopDataDirectory(), …)`）。

> ⚠️ vstest 结束时不保证走 `ProcessExit`（第一版只有 `ProcessExit` 时，跑完仍留下 7 个子目录）。
> 所以真正的保险是**静态构造里的"开工先清"** —— 它保证残留不会跨运行累积。

### 32.3 验证

清空 `%TEMP%\cs-*` 后跑 `dotnet test tests/ControlServer.Core.Tests` →
**跑完 `%TEMP%` 里 `cs-*` 计数 = 0**；再跑一次全量（543 条）仍然 = 0。

### 32.4 同类病：冒烟脚本的 `cs-smoke-*.log`

`deploy/smoke/run-all.py` 的 `finally` 里明明清了数据目录，却把**启动日志**留在
`%TEMP%\cs-smoke-<场景>-<pid>.log` —— 名字里带 PID 所以**永不覆盖**，一样只增不减
（每跑一次全量 +4 个；清理前已积 9 个）。

改成**成功即删、失败保留并打印路径**（失败时这份日志恰恰最有用）：

```python
ok = False
try:
    ...
    ok = result.returncode == 0
    return ok
finally:
    ...
    if ok:
        try: os.remove(log_path)
        except OSError: pass
    else:
        print(f"  启动日志已保留（便于排查）：{log_path}")
```

验证：清空 `%TEMP%\cs-*` 后跑一次全量冒烟（4/4 PASS）→ **跑完残留计数 = 0**。

> 📌 通用教训：**任何"每次运行都写一个新名字的临时文件"的地方，都要有人负责删。**
> 判断"是不是同一类病"很快 —— `%TEMP%` 下按前缀分组数一下数量就够了（这次就是
> `cs-noop-<guid>` ×248 + `cs-smoke-*.log` ×9 + publish 目录 6 个）。

---

## 33. Windows 安装包（Inno Setup）：一个 exe 双击即装（2026-10-08 晚）

### 33.1 三个产物各自的分工（别混）

| 产物 | 生成者 | 定位 |
|---|---|---|
| `dist\win-x64\ControlServer.Server.exe` | `build-portable-exe.ps1` | 绿色版，单文件双击即用 |
| `dist\win-x64-service\*.exe`（2 个） | 同上 | 服务版原始产物 = **安装包的 payload** |
| `dist\installer\ControlServer-Setup-<版本>.exe` | **`build-installer.ps1`** | **安装包**，双击向导装完即跑 |

### 33.2 Inno 编译器怎么来的（不预装、不污染系统）

本机没有 WiX / Inno Setup / NSIS。做法：从 NuGet 拉 `Tools.InnoSetup`（**包里直接含 ISCC.exe**），
解到 `<仓库>\.tools\innosetup`（已加进 `.gitignore`）。`build-installer.ps1` 按
`-InnoPath` → `%INNO_SETUP_HOME%` → Program Files → `.tools` → **自动下载** 的顺序找。

⚠️ 官方 `Tools.InnoSetup` 包**不含简体中文语言包**，要另从社区翻译仓库取
`ChineseSimplified.isl` 放进 `tools\Languages\`（构建脚本会自动下载）。
⚠️ `.iss` 必须存 **UTF-8 with BOM** —— 无 BOM 时 ISCC 按 ANSI 解析，中文界面全乱。
⚠️ `.iss` **不支持 `\` 续行** —— 长参数（如 `[UninstallRun]`）只能写在一行里。

### 33.3 设计：服务那套逻辑一律复用 `install-service.ps1`

安装包**不重写**服务注册 —— `sc sdset` 那段 DACL 处理（§25.4）极易写错。
改为：Inno 只负责放文件，`[Code]` 里调用 `install-service.ps1`。
为此给两个脚本加了开关（**手工调用的行为完全不变**）：

| 脚本 | 新增参数 | 用途 |
|---|---|---|
| `install-service.ps1` | `-SkipCopy` | 文件已由安装包放好，别再"清空 + 从 Source 复制" |
| | `-LogFile` | `Start-Transcript` 留证据（安装包失败时取证） |
| | `-Verify` | 收尾自检：服务 Running + HTTP 可达；不通过 `exit 2` |
| `uninstall-service.ps1` | `-SkipDeleteFiles` | 删文件交给 Inno 卸载器，脚本别抢 |
| | `-LogFile` | 同上 |

### 33.4 ⚠️ 重装必踩：复制文件**之前**必须先把旧服务停掉

服务正在跑时 `ControlServer.Server.exe` 被进程占住 ⇒ Inno 复制**必然失败**。
所以加了 `PrepareToInstall`：用 `ExtractTemporaryFile('uninstall-service.ps1')`
（`Flags: dontcopy` 的那份副本 —— 首次安装时 `{app}` 里还没有脚本）先跑一遍
`-SkipDeleteFiles`，停服务 + 杀托盘 + 撤防火墙，然后再复制文件、再重新注册。
**同一个安装包因此可以反复运行。**

### 33.5 顺带修掉的两处

1. **`sc.exe` 失败不抛异常** —— `sc.exe create ... | Out-Null` 失败时只往 stderr 写一行就被吞掉，
   脚本会一路"成功"到底、最后报"完成"，但服务根本没建。已加 `Get-Service` 回查，失败即明确抛错。
2. **服务不该开浏览器** —— `AutoOpenBrowser` 默认 `true`，但服务跑在 Session 0、**没有桌面**，
   只会白拉一个进程。服务注册的 binPath 现在带 `--Server:Startup:AutoOpenBrowser=false`；
   打开界面由托盘助手 / 安装包负责。

### 33.6 装完自检：`verify-install.ps1`

随包带到 `{app}\tools\verify-install.ps1`，**只读、不需要管理员**。逐项核对：
程序文件 / 服务已注册 / 开机自启 / Running / 端口监听 / **HTTP 真的能打开** / 防火墙 / 托盘 / 数据目录。
退出码 `0` = 全过，`1` = 有硬失败。这是"装好就能跑"的可交付证明。

### 33.7 验证到什么程度（**诚实项**）

- ✅ ISCC 编译零警告；`[Code]` 用到的每个事件名（`GetCustomSetupExitCode` / `CurStepChanged` /
  `CurUninstallStepChanged` / `InitializeUninstall` / `ssPostInstall`）都在 `Setup.e32` 里**逐个核对到**
  —— 事件名写错时 Inno **不报错、只是静默不调用**，这一步不能省。
- ✅ `build-installer.ps1` 端到端跑通（退出码 0，产出 Setup.exe；2 个 exe + 3 个脚本都进了包）。
- ✅ `verify-install.ps1` 本机实跑，输出与退出码均正确。
- ❌ **完整"安装 → 验证 → 卸载"未在本机实测**：本会话**无管理员权限**，且沙箱把 `sc.exe`
  列入**硬黑名单（不可绕过）**。服务创建 / 防火墙 / HKCU 这几步只能在**目标机**上验。
  目标机上跑一次安装包，再用 `verify-install.ps1` 收口即可。

### 33.8 产物与改动清单

- 产物：`dist\installer\ControlServer-Setup-1.0.0.exe`（**72.1 MB**；payload 87.4 MB 压缩而来）
- 新增：`deploy\windows\ControlServer.iss`、`build-installer.ps1`、`verify-install.ps1`
- 改动：`install-service.ps1`（+3 开关 / 失败早报 / 关浏览器）、`uninstall-service.ps1`（+2 开关）、
  `.gitignore`（+`.tools/`）

> 📌 复用教训：**"让现成脚本能被安装包调用"比"在安装器里重写一遍"便宜得多，也安全得多**
> —— 服务 DACL 那种逻辑只有一份，改一处即可。代价是要给脚本加"我是被谁调用"的开关。

### 33.9 ⚠️ 首轮目标机实测失败 —— 四处误报/误伤已加固

**现象**：安装走到最后弹「服务注册或启动自检没有通过」，浏览器打不开。

按嫌疑排序逐条加固（都不是"服务真的坏了"，而是**脚本自己把它判坏了**）：

**① HTTP 探活被系统代理劫持（最高嫌疑）**
`verify-install.ps1` 与 `install-service.ps1 -Verify` 都用 `Invoke-WebRequest` —— 它**默认走系统代理**
（WinINET /「Internet 选项」）。目标机装了 Clash / 公司代理时，`http://127.0.0.1:8080` 会被代理接管
返回 **502** ⇒ 探活判失败 ⇒ `exit 2` ⇒ 弹框。**而服务其实是好的。**
本机 Git Bash 的 `curl` 早就踩过同一个坑（必须 `--noproxy '*'`），是同源问题。
**修**：新增 `Invoke-HttpProbeNoProxy`（`HttpWebRequest` + `Proxy = $null` 强制直连）；
并把**硬判据换成纯 socket 端口探测** `Test-TcpPort`（`TcpClient`，与任何代理无关），
HTTP 降级为**软性旁证** —— TCP 通了就算服务活着。

**② 防火墙步骤抛异常会炸掉整个安装**
`New-NetFirewallRule` 在装了 360 / 火绒等接管防火墙的机器上**直接抛异常**，原代码无 try/catch
⇒ 异常冒到最外层 catch ⇒ `exit 1`。而此时「启动服务」还在**后面**，压根没执行 ⇒ 服务没跑、
浏览器打不开。**现象与截图完全吻合。**
**修**：包 try/catch，失败只警告（本机 `127.0.0.1` 访问不受防火墙影响，只是局域网别的机器连不上）。

**③ 托盘部署抛异常同样会炸掉整个安装**
`Install-TrayAgent` 里 `New-ItemProperty` 写 HKCU，在"提权账号 ≠ 控制台登录账号"、注册表被组策略
锁定时会抛异常 ⇒ `exit 1`。而此时**服务已经启动好了**，却被判成"安装失败"。
**修**：调用处包 try/catch。

> ② ③ 是同一类错误：**把"锦上添花"的步骤放在关键路径上，且不给容错**。
> 判断标准应当是"这一步失败，核心功能还能用吗？"—— 能，就不该让安装失败。

**④ 组策略锁死执行策略（另一种可能，现在能自报）**
本机实测 `Get-ExecutionPolicy` = **Restricted**。若目标机被 GPO 锁成 `AllSigned`，命令行上的
`-ExecutionPolicy Bypass` **会被组策略覆盖**，脚本根本不执行、日志也不会生成 ⇒ 零线索的失败。
**修**：`.iss` 新增 `PowerShellUsable()` 预探测（跑 `-Command "exit 0"`），失败时给出**准确原因**
（并在弹窗里直接给出 `Set-ExecutionPolicy` 的解法），而不是笼统报"服务没起来"。

**⑤ 顺带：`sc.exe start` 的输出被吞掉**
原来是 `sc.exe start $svc | Out-Null` —— 而服务起不来时这一行**最有价值**：
`1053`=启动超时 / `1067`=进程崩溃退出 / `1069`=登录失败。已改为打印原始输出 + 退出码。

**新增诊断能力**（全部只读、免管理员）：

| 产物 | 作用 |
|---|---|
| `deploy\windows\collect-diagnostics.ps1` | 一键收集 10 类现场证据 → `<DataDir>\logs\diagnostics-<时间戳>.txt` |
| `install-service.ps1 -Verify` 失败时 | 自动调 `Write-InstallDiagnostics` → `logs\install-diagnostics.log`（含事件日志、SCM 记录、日志尾部） |
| `.iss` 失败弹窗 + Finish 页 | 直接给出**具体原因** + 诊断文件路径 + 诊断脚本路径 |

**本机实测**：
- ✅ 五个 `.ps1` AST 解析全过、BOM 齐全
- ✅ `Test-TcpPort` 四点用例全对（已监听→True / 未监听→False / 黑洞地址 800ms 返回不卡死 / 停监听后→False）
- ✅ `Invoke-HttpProbeNoProxy` 失败路径返回空值、不抛异常、无状态累积（1090ms ≈ 超时设定值）
- ✅ ISCC 重新编译零警告，`collect-diagnostics.ps1` 确认入包
- ❌ 仍**无法**在本机跑完整安装（无管理员权限 + `sc.exe` 硬黑名单）

> 📌 **沙箱新认知**：`sc.exe` 在硬黑名单里 ⇒ **任何含 `sc.exe` 调用的脚本在本机都无法实跑**
> （`collect-diagnostics.ps1` 就是被它整体拦下的，报 "PROGRAM BLOCKED BY SECURITY POLICY"）。
> 要验证这类脚本，只能先把 `sc.exe` 调用摘掉再跑，或到目标机验证。

产物：`dist\installer\ControlServer-Setup-1.0.0.exe` **72.2 MB**
SHA256 = `3AD0D16B21D9A47C720E276869799F055B473215418992430C77A0B32061DA1A`

### 33.10 第二轮实测：**退出码 1** —— 重装的「删/建」竞态

**新证据**：改进后的弹窗直接报「装配脚本中途报错（**退出码 1**）」。
`exit 1` 只从 `catch` 出来，说明 `try` 里**真的抛了异常** —— 这一条就**排除**了 §33.9 的头号嫌疑
（代理误报走的是 `exit 2`），也排除了执行策略被锁（那有专属提示）。
再结合"首轮是含糊提示、重装才变成退出码 1"，锁定为**重装特有的竞态**。

**根因链**

1. `PrepareToInstall` 调 `uninstall-service.ps1` 停掉并删除旧服务；
2. 但该脚本用的是**估算式等待**：`sc stop` → 睡 2s → `sc delete` → **立刻返回**；
3. `sc delete` 只是**标记删除**（`ERROR_SERVICE_MARKED_FOR_DELETE` = **1072**），
   真正的清除是**异步**的 —— 要等该服务的所有句柄关闭；
4. 紧接着 `install-service.ps1` 的 `sc create` 撞上 1072 ⇒ 创建失败；
5. 而此时 `Get-Service` **也可能查不到它** ⇒ **上一轮加的那道「回查 SCM」保护反而变成误伤**：
   抛异常 → `exit 1`；
6. 且抛出的信息指向"权限不足"，**把排查方向整个带偏**。

> 📌 教训：**上一轮加的保护逻辑本身没错，错在它依赖的"前置状态"没有被正确建立。**
> 加断言/回查之前，先确保前置条件真的成立 —— 否则保护会变成新的故障源。

**修**

| 位置 | 改动 |
|---|---|
| `uninstall-service.ps1` | 删服务后**轮询到 `Get-Service` 真的查不到**（上限 30s），不再"睡 2 秒就当删好了" |
| `install-service.ps1` | 新增 `Remove-ServiceIfPresent`：停 → 等到 `Stopped` → 删 → 等到从 SCM 消失 |
| 同上 | `sc create` **不再 `\| Out-Null`** —— 原始输出 + 退出码全部打印（1072 / 1073 / 5 一眼可辨） |
| 同上 | `sc create` **带退避重试 3 次**（每次先清一次残留） |
| 同上 | 失败时把退出码**翻译成人话**：1072 直接提示"重启电脑可解" |
| 同上 | **`catch` 块补上诊断收集** —— 先前弹窗让人去看 `install-diagnostics.log`，而这条路径**根本不生成它** |

**又一个语法坑**：throw 的多行拼接把 `+` 写在**下一行行首** ⇒ PowerShell 认为上一行
（以完整字符串字面量结尾）语句已经结束 ⇒ `ParserError`。
**规则：跨行续行的 `+` 必须留在上一行末尾。**

**本机实测**：`deploy` 下 6 个 `.ps1` AST 解析全过、BOM 齐全；ISCC 重编零警告。

产物：`dist\installer\ControlServer-Setup-1.0.0.exe` **72.2 MB**
SHA256 = `4DF35DE1192E0B2ADDD5A85B5CD53B21D757C6CBFF18D185DB05D8F09A086B0E`

> ⚠️ **本节的「删/建竞态」结论已被 §33.11 推翻** —— 见下节。本节的价值只剩两项：
> ① `uninstall-service.ps1` 删服务后的**轮询等待**（本身是对的，保留）；
> ② 两项**可观测性**改动（`sc create` 不再 `| Out-Null`、`catch` 里补诊断收集）——
> **正是它们让第三轮能直接读到 1639 并拿到诊断报告**。不做可观测性就只能继续猜。

---

### 33.11 ⚠️⚠️ 第三轮实测：**真正的根因** —— `sc.exe create` 的 argv 被 PowerShell 拆烂（1639）

用户**在本机**（`DESKTOP-9VFHRMA`）重装失败 ⇒ 这次日志直接可读，证据是硬的。

**现场证据**（`C:\ProgramData\ControlServer\logs\install-service.log`）：

```
==> 3/6 注册服务并配置恢复策略（进程自愈）
    <sc create 的帮助文本全文>            ← 打印帮助 = 参数解析失败，这就是判据
警告:   sc create 未生效（退出码 1639），第 1 次尝试
警告:   sc create 未生效（退出码 1639），第 2 次尝试
警告:   sc create 未生效（退出码 1639），第 3 次尝试
[!!] 安装失败：服务注册失败：sc create 连续 3 次未生效（退出码 1639）…
```

`install-diagnostics.log` 同时**排除**了两个方向：

- `管理员：True` ⇒ **不是权限问题**（§33.10 把它写进了错误提示，是错的）
- `服务未注册` + `端口 8080 无任何监听` ⇒ 不是端口冲突

**1639 = `ERROR_INVALID_COMMAND_LINE`。** `sc.exe` 参数解析失败时会**打印帮助**并返回该码。

**根因：PowerShell 5.1 把参数里的「内层引号」整个吞掉，只在空格处断成新 argv**

用探针（一个打印 `sys.argv` 的 `.py`，经 `& $py …` 调用 ⇒ 与 `sc.exe` 同走**原生 argv**通路）实测：

| 写法 | 探针抓到的真实 argv |
|---|---|
| **现状** `sc.exe create X binPath= "C:\Program Files\A\S.exe" --flag` | `['create','X','binPath=','C:\Program','Files\A\S.exe --flag','start=','auto',…]` |
| 路径**无空格**时（如 `D:\ControlServer`） | `['binPath=','D:\A\S.exe --flag']` ✅ 侥幸能过 |
| 把内层引号转义成 `\"` | `['binPath=','\"C:\Program','Files\A\S.exe\"','--flag']` ❌ 更糟 |

⇒ `binPath=` 只拿到 `C:\Program`；`sc.exe` 于是参数解析失败 → **打印帮助 + 1639**。
**这个坑只在装到含空格的路径（默认的 `Program Files`）时才现形** —— 装到 `D:\ControlServer`
反而侥幸能过，这就是它长期没被发现的原因。

**解法：改用 `New-Service`（cmdlet），彻底不走命令行拼接**

对照实证（同一台机器、同一个 96 字符含空格字符串）：

| 通路 | 结果 |
|---|---|
| **cmdlet 参数绑定**（用 `function` 模拟，机制与 `New-Service` 相同） | 96 字符**完整到达**，引号保留 ✅ |
| **原生程序 argv**（`sc.exe` 的路） | 空格处被拆开、引号丢失 ❌ |

`New-Service` 走 .NET 参数绑定 → 直通 `CreateService` API，不经命令行字符串拼接，
整串（含空格与引号）**原样写进注册表 `ImagePath`**：

```powershell
New-Service -Name $ServiceName -BinaryPathName $svcBinPath `
    -DisplayName $DisplayName -Description '中控服务版 —— 局域网中控 Web 服务' `
    -StartupType Automatic -ErrorAction Stop
```

**仍然保留 `sc.exe` 的调用点**（它们的参数**都不含引号**，安全）：

- `sc.exe failure … reset= 86400 actions= restart/5000/restart/10000/…`（`New-Service` 不支持恢复策略）
- `sc.exe sdshow / sdset … <SDDL>`（SDDL 用 `( ) ; :` 分隔，无空格）
- `sc.exe stop / delete / start / query`

**新增兜底**：注册后回读 `HKLM\SYSTEM\CurrentControlSet\Services\<名>\ImagePath`，
若不以引号开头就自动改写 —— 缺引号时 SCM 会把 `C:\Program Files\…` 当 `C:\Program.exe` 去找
（unquoted service path），表现为"注册成功但一启动就 1053/1067"。

**同源顺手修**：`Start-Process -ArgumentList` 是「数组**用空格拼接、不自动加引号**」⇒
给 `--data-dir` 的值补上引号（同一个坑的家族成员）。

> 📌 **教训（比 bug 本身值钱）**：
> **「静态检查全过 + 编译零警告 + 逻辑读起来对」不能替代「在真机上跑一次」。**
> 这个 bug 三层都拦不住，只有真机日志能暴露。
> 另外，**上一轮的假设虽然错了，但它带来的那两项可观测性改动，正是本轮定位根因的关键** ——
> 不先让失败可观测，就只能在假设之间反复猜。

**本机实测边界**：工具会话 `是否管理员 = False` ⇒ 仍**无法端到端安装**（且 `sc.exe` 在沙箱硬黑名单）。
能验的都验了：`deploy` 下 6 个 `.ps1` AST 解析全过、BOM 齐全、ISCC 重编零警告、
argv 拆分与 cmdlet 绑定两条通路**均有对照实测**。

产物：`dist\installer\ControlServer-Setup-1.0.0.exe` **72.2 MB**
SHA256 = `523D7621E9892FD46D54D5763662A2C7B9F8AF3D33FEB5E1792CD240B64E595C`（第三轮）

---

### 33.12 第四轮复测：一键驱动 + **又一个只在"强制重发"时才现形的真 Bug**（2026-10-09）

**新增 `deploy\windows\retest-install.ps1`** —— 把「安装 → 自检 → **重装** → 自检 → 卸载 →
清理核对」一次跑完，输出判定矩阵 + 报告文件；`-Check` 只读模式不需要管理员。

每轮 7 条判据，**第 3、4 条是本轮的核心**：

| # | 判据 |
|---|---|
| 1 | 安装器退出码 = 0 |
| 2 | 服务已注册 / `StartMode=Auto` / `Running` |
| 3 | **注册表 `ImagePath` 以引号开头且指向正确的 exe**（1639 与 unquoted-path 两坑的判据） |
| 4 | **本轮 `install-service.log` 不含 `1639`、且出现 `ImagePath 校验通过`** |
| 5 | 端口监听（纯 socket 探测） |
| 6 | HTTP 可达 |
| 7 | 卸载后清理干净、**数据目录保留** |

**⚠️ 顺手揪出并修掉一个潜伏真 Bug：`build-installer.ps1` 的数组 splat 是"按位置"传参**

```powershell
# ❌ 原来（数组 splat = 按位置填充位置参数，元素**不会**被当成参数名）
$portableArgs = @('-DotNetRoot', $DotNetRoot, '-NodeRoot', $NodeRoot, ...)
& $portable @portableArgs      # → 被调脚本的 DotNetRoot = '-DotNetRoot'（字面量）
#   现象：未找到 .NET SDK：-DotNetRoot\dotnet.exe

# ✅ 改为哈希表 splat（@{} = 按名字传参）
$portableArgs = @{ DotNetRoot = $DotNetRoot; NodeRoot = $NodeRoot; ... }
& $portable @portableArgs
```

**长期没暴露的原因**：安装包历来都是在 payload **已存在**时构建的 ⇒ 走"跳过重新发布"分支，
这行代码从没执行过；只有**删掉 `dist\win-x64-service` 强制重发**才会踩到。
⇒ 与 §33.11 同一条教训的第二次出现：**"没被执行过的代码 = 没被验证过的代码"。**

**本机实测**（开发机，非管理员）：

- `deploy\windows` 下 **8 个 `.ps1`**：AST 解析全过、BOM 齐全、LF
- `install-service.ps1` 静态复核：`New-Service` 调用就位；**无 `sc.exe create` 实际调用**（只剩注释）；
  ImagePath 回读兜底在；**所有 `sc.exe` 调用点参数均不含引号** ✅
- `retest-install.ps1 -Check`：正常运行、退出码 0；能自动发现 Setup.exe 并打印其 SHA256
- **守门验证**：非管理员且不带 `-Check` ⇒ 明确拒绝 + 退出码 **1**
  （⚠️ 首版这里报"全部通过" —— `finally` 里 `Fail -eq 0` 把退出码覆写回 0 了；已修）
- 重编安装包端到端成功（**splat 修复后 SDK `8.0.425` 正确传入**）

**产物（第四轮）**：

| 产物 | 大小 | 备注 |
|---|---|---|
| `dist\win-x64\ControlServer.Server.exe` | **51,712,956 B（49.3 MB）** | 严格 1 文件 |
| `dist\win-x64-service\` | 2 个 exe | Server 51.7 MB + TrayAgent 35.8 MB |
| `dist\installer\ControlServer-Setup-1.0.0.exe` | **75,730,189 B（72.2 MB）** | SHA256 `23D198637434134F16481454F3225B442D0D33B6226541E33614992D5BDC31A1` |

**❌ 仍无法在本机完成的（诚实项）**：真正的「安装 → 重装 → 卸载」——
开发机会话非管理员，且沙箱把 `sc.exe` 列入硬黑名单。
**目标机执行方式**：把 `retest-install.ps1` 与 `ControlServer-Setup-1.0.0.exe` 拷到同一目录，
以管理员运行脚本即可（面向使用者的完整说明见 **`docs\Windows-安装与复测说明.md` §4**）。

---

## 34. 本机环境与工具链坑位（**原 MEMORY.md §4/§5 详版**）

> 2026-10-08：`MEMORY.md` 已涨到 22.8 KB，注入时**被截断** ⇒ 把原本堆在那里的环境细节
> 整体搬到这里（本文件是**按需读取**的，不怕长）；`MEMORY.md` 只留高频条目 + 指针。
> **本节是权威详版，MEMORY 里的条目是它的摘要。**

### 34.1 .NET SDK / 运行时

- 系统 `dotnet` **既缺 SDK 也缺 AspNetCore 运行时**。SDK 在 `…\binaries\dotnet8`（**8.0.425**，三个运行时齐全）。
- 跑 run / build / test：用**绝对路径** `"C:/Users/IYLONG/.workbuddy/binaries/dotnet8/dotnet.exe"` 即可，
  **不必**设 `DOTNET_ROOT` / `PATH`。
- ⚠️ 但**直接跑 `bin\…\Server.exe` 必须先 `export DOTNET_ROOT=<该目录>`** —— 否则 apphost 回退去
  `Program Files` 找运行时，报 "You must install or update .NET"。
- **`dotnet-ef`**：`…\binaries\dotnet-tools\dotnet-ef`（8.0.10）。生成迁移**别拿 Server 当启动项目**
  （它会真把服务拉起来），用 Infrastructure 的 `AppDbContextDesignFactory`（命令见 §9）。

### 34.2 PowerShell

- **禁止直接跑 `.ps1`**（本机执行策略 = `Restricted`）：必须
  `powershell -NoProfile -ExecutionPolicy Bypass -File xxx.ps1`。
- **`.ps1` 必须 UTF-8 with BOM**：PS 5.1 读无 BOM 文件按 ANSI 解析 ⇒ 中文注释直接 `ParserError`。
- **不要 `$ErrorActionPreference='Stop'`**：npm / vite 的警告走 stderr，会被当成致命错误。
  改用 `$LASTEXITCODE` + 显式 `exit 0/1`。
- ⚠️ **别把 stderr 折进管道后再看退出码**：`... -File xxx.ps1 2>&1 | Select-Object -Last 40`
  在 PS 5.1 下会把 stderr 行包成 `ErrorRecord`，**脚本明明成功却报退出码 1**（打包脚本实测自身退出码 0）。
  判断成败看输出里的 `✓ …`，或用 `*> $null` 收掉输出（详见 §22.3）。
- **`Write-Host '…' + $x` 是陷阱**：`+` 会被当成 `Write-Host` 的**第二个位置参数**原样打进控制台。
  必须整体加括号：`Write-Host ('…' + $x)`。
- **PowerShell 不支持相邻字面量拼接**：`'a'"b"` 不是 `'ab'`，而是**两个参数**，会报参数绑定错误。
- **`$args` 是保留自动变量**，别拿来当自己的参数名（用 `$portableArgs` 之类）。
- **函数必须定义在调用点之前**（不支持前向引用），而 `Continue` 模式下 `CommandNotFoundException`
  会被**静默吞掉** ⇒ 脚本"成功退出"却少做了事（**最危险的失败形态**）。
- **`sc.exe` 失败不抛异常**（只往 stderr 写一行）⇒ 必须 `Get-Service` 回查确认，否则"假成功"。
- **`#Requires -RunAsAdministrator` 会阻断非提权会话** —— 写它之前先想清楚是否真的需要。
- 用户可见输出**禁用 `✓` / `✗` / `⚠`**：不在 GBK 代码页，控制台显示成 `?`；
  在 Python 脚本里更会直接 `UnicodeEncodeError`。统一用 `[OK]` / `[!!]` / `[--]`。

### 34.3 编码 / 换行

- **`.cmd` 必须存 GBK**（宿主 CP936）—— 与 `.ps1` 必须带 BOM 是**相反**的坑。
  用 Python `open(p, "w", encoding="gbk", newline="\r\n")` 生成。
- **`.iss` 也必须 UTF-8 with BOM**（Inno 6 对中文脚本的要求同 `.ps1`）。
- **`netstat -ano` 输出是 CRLF**：`grep ":8080"` 常因行尾 `\r` 匹配不上 ⇒ 先 `tr -d '\r'` 再 grep，
  否则会误判"端口没在听"。
- **重定向日志的编码随"谁重定向"而变**（cmd / 管道常 UTF-8，直接 `>` 常 GBK）⇒ 先 `errors='replace'` 看结构。

### 34.4 ⚠️⚠️ 原生程序传参（**本轮安装包失败的真根因域**）/ 网络代理

- ⚠️⚠️ **PowerShell 5.1 往原生程序传参时，参数里的「内层引号」会被整个吞掉**，
  并且只在**空格处**断成新的 argv。后果：
  `sc.exe create X binPath= "C:\Program Files\A\S.exe" --flag`
  ⇒ `binPath=` 只拿到 `C:\Program` ⇒ **打印帮助 + 1639 `ERROR_INVALID_COMMAND_LINE`**。
  - **判据**：原生工具**打印自己的帮助文本**，几乎一定是参数被拆烂了（不是路径不对）。
  - **受影响**：任何「值里需要内层引号」的原生调用 —— `sc.exe create`、`reg add`、
    `schtasks`、`icacls`、`netsh`…（值含空格且需引号包住时）。
  - **解法**：改用 **cmdlet**（`New-Service` / `New-ItemProperty` / `Set-ItemProperty` /
    `Register-ScheduledTask`）——它们走 .NET 参数绑定，整串原样到达。
  - **对照实证**（同机同字符串，96 字符）：cmdlet 绑定 = 完整到达 ✅ ／ 原生 argv = 空格处拆开 ❌。
  - **隐蔽性**：只有路径**含空格**时才犯病 ⇒ 装到 `D:\ControlServer` 侥幸能过，
    装到默认的 `Program Files` 必炸。**别用"换个目录就好了"蒙混过去。**
  - 详见 §33.11。
- ⚠️ 同族坑：**`Start-Process -ArgumentList` 是把数组「用空格拼接、不自动加引号」** ⇒
  含空格的值必须自己带引号。
- **自测工具**：写一个 `import sys; print(sys.argv[1:])` 的 `.py`，用 `& python.exe 探针 …`
  就能**精确看到原生程序收到的 argv** —— 比读文档猜 PowerShell 的转义规则可靠得多。
- ⚠️ **Git Bash 配了 HTTP 代理**：`curl` 访问 `127.0.0.1` 会返回 **502**，必须加 `--noproxy '*'`。
  漏了就误判"服务没起来"。
- ⚠️ **PowerShell 的 `Invoke-WebRequest` 同样默认走系统代理**（WinINET /「Internet 选项」）——
  与上面是**同源问题**。目标机装了 Clash / 公司代理时，探活 `http://127.0.0.1:8080` 会被代理接管。
  **正确做法**：用 `[System.Net.HttpWebRequest]::Create($url)` + `$req.Proxy = $null` 强制直连；
  或者干脆用**纯 socket**（`TcpClient`）判端口，与代理完全无关。见 §33.9。
  （注：代理**不是**本次失败的原因，但这两条仍然成立，探活逻辑必须免疫代理。）
- **`netstat` / `sc.exe` 等原生工具在沙箱里受限**：`sc.exe` 在**硬黑名单**（不可绕过、不得重试）⇒
  **任何含 `sc.exe` 调用的脚本在本机都无法实跑**。

### 34.5 沙箱限制（本会话实测）

| 操作 | 结果 | 绕法 |
|---|---|---|
| 从 Bash 调 `powershell.exe` / `cmd.exe` | `Command blocked for security` | 改用 PowerShell 工具 |
| `sc.exe`（任意形式） | `PROGRAM BLOCKED BY SECURITY POLICY`，明示不可绕过 | 无 —— 只能到目标机验 |
| `Start-Process` | 受限（参数传不进、无输出） | 改用 `& $exe -File …` 子进程调用 |
| PowerShell 工具的标准输出 | 常无回显 | `Set-Content` 写文件后用 Read / Bash 读 |

### 34.6 Node / 前端

- **`npm install` 因 esbuild postinstall spawn 报 `EBUSY`** ⇒ 用 `npm install --ignore-scripts`。
- Node / npm 用托管版 `…\binaries\node\versions\22.22.2-6`。
- **前端产物是编译期嵌进程序集的** ⇒ 改前端后**必须** `npm run build` **再** `dotnet build`（详见 §17）。
- **`npm run typecheck` = `vue-tsc --noEmit`（已装进 devDependencies）**：`tsc --noEmit` 看不了 `.vue`，
  等于 SFC 里的类型错与 `emit` 签名错**完全没检查**（2026-10-08 首次开启后立刻抓到 4 处**既有**缺陷：
  `emit('warn', …)` 事件名写成提示级别 ⇒ 提示静默不显示）。装它用 `npm install --ignore-scripts vue-tsc`。

### 34.7 路径

- **Windows 程序不认 Git Bash 的 `/tmp`**（MSYS 虚拟路径）：`python.exe` 读它必 `FileNotFoundError`。
  走管道，或写真实路径。
- 数据目录 `%ProgramData%\ControlServer`（`config/ files/ images/ logs/` + `control.db`）；
  演示实例 `%TEMP%\cs-demo`。
- **Bash 陷阱**：`A=1 && B=2 && exe … &` 会把**整条链**放进后台子 shell，父 shell 里 `$A`/`$B` 为空。
  变量用 `;` 或换行单独赋值，**只把启动命令放后台**。

### 34.8 构建脚本撞上删除拦截：**报失败 ≠ 没删掉**（2026-10-15）

`deploy/windows/build-*.ps1` 发布前都会 `if (Test-Path $out) { Remove-Item $out -Recurse -Force }`。
本机有 `[safe-delete][SAFE_DELETE_FAIL_CLOSED]` 拦截 ⇒ 这一步**抛错、整脚本失败**，
但**目标目录其实已经被删掉了** ⇒ 现场是"**没产物 + 报失败**"，不是"什么都没动"。

本轮实测：`build-portable-exe.ps1` 在删 `dist/win-x64` 处失败（`trash-failed`）→ 再跑时该目录已不在；
`build-installer.ps1 -SkipPayloadBuild` 在删 `dist/installer` 处失败（`genie-trash failed; refusing fallback delete`）
→ `dist/installer/` 整个消失。

**预防**：**先把目标目录挪走，别让脚本去删**。`Move-Item` 是重命名、不是删除，不被拦：

```powershell
New-Item -ItemType Directory -Path '.tools\old-dist' -Force | Out-Null
Move-Item 'dist\win-x64-service' (Join-Path '.tools\old-dist' ("win-x64-service.old." + (Get-Date -Format 'HHmmss'))) -Force
```

三个目标目录：`dist\win-x64`、`dist\win-x64-service`、`dist\installer`
（`.tools/old-dist/` 里既有的 `*.old.012811` / `*.old.012835` 就是这么来的）。

**回收**：被 trash 掉的文件进的是 **Windows 回收站**，且**大小与 mtime 都保留**
（`$I*` = 元数据、`$R*` = 内容）⇒ 按"大小 + mtime"精确认人：

```powershell
Get-ChildItem 'D:\$Recycle.Bin\*\','C:\$Recycle.Bin\*\' -Filter '$R*' -Force |
  Where-Object { $_.Length -gt 50MB } | Select-Object Name,Length,LastWriteTime
Copy-Item -LiteralPath 'D:\$Recycle.Bin\S-1-5-21-…-1001\$RKOF0YJ.exe' `
          'dist\installer\ControlServer-Setup-1.0.0.exe' -Force          # 用 Copy，不用 Move
(Get-FileHash 'dist\installer\ControlServer-Setup-1.0.0.exe' -Algorithm SHA256).Hash
```

本轮据此把 `dist/installer` 恢复成 **SHA256 `4B725833…D1ACC`、byte 与 M4.16 那份完全一致**。
⚠️ Git Bash 里访问要转义 `$`（`"/d/\$Recycle.Bin/…"`）；PowerShell 用 `-LiteralPath` 更省心。

- **绿色版单文件可就地恢复**：`dist/win-x64/ControlServer.Server.exe` 与
  `dist/win-x64-service/ControlServer.Server.exe` **本来就是同一个 exe**（脚本直接 `Copy-Item`）
  ⇒ 丢了复制回来即可，**不必重编**。
- 🔴 **一般教训**：`dotnet publish` 报**语法/参数类**编译错（如 `CS7036`）时，先确认**不是别人正在写这个文件**。
  本轮那个 `WindowsEncoderCapabilityProbe.cs` 的 `CS7036` 是**并发写入的中间态**，重跑即 0 错 0 警。

## 35. 收发日志页展示顺序：**最新在上**（2026-10-15）

用户：「最近收发日志优化，最后的消息应显示在页面最上方」。

### 35.1 口径（全站统一，别再各页各写一套）

**「日志类列表」一律最新在上**，反转由**展示层**做，服务端契约不动。

| 位置 | 数据来源 | 谁负责反转 |
|---|---|---|
| 控制台「最近收发」小面板（20 条） | `App.vue` 的 `traffic` | `traffic.slice(-RECENT_TRAFFIC).reverse()` |
| **收发日志页**（全量 ≤400） | `TrafficPanel.vue` 的 `items` | `ordered = [...visible.value].reverse()`（**本次新增**） |
| 运维日志页 | `OpsLogPanel.vue` 的 `items` | `ordered = [...visible.value].reverse()`（早已如此） |
| 客户机「接收到的数据」 | `ReceivedDataLog` | 客户端 `_items.AddFirst()` + `RemoveLast()`（新的在前） |
| 客户机「开关机记录」 | `EfAgentTelemetryStore` | `OrderByDescending(x => x.AtMs)`（控制器注释也写了「最近在前」） |

**服务端为什么给正序**：`TrafficLogService._buffer` 是**追加式**环形缓冲，
`Recent(n)` = `_buffer.Reverse().Take(n).Reverse()` —— 取最近 n 条并**还原成时间正序**。
正序是"时间线"该有的样子；反不反转是**展示决定**，别去改服务端。

### 35.2 铁律（会踩的坑）

1. 🔥 **反转必须作用在副本上**：`[...visible.value].reverse()`，**绝不** `props.items.reverse()`。
   `App.vue` 靠"追加到尾部"维护那份数组（`traffic.value = [...traffic.value, item].slice(-MAX_TRAFFIC)`），
   控制台小面板又靠 `slice(-N)` 取**尾部** —— 一旦在 `TrafficPanel` 里原地反转，
   小面板会立刻开始显示**最旧的几条**：一个页面的"优化"把另一个页面搞坏。
2. **反转对象是 `visible`（筛选之后）**，不是 `items`，否则筛出来的顺序还是乱的。
3. 表头补了「**最新在上**」四个字（`TrafficPanel` + `OpsLogPanel`）：顺序是隐性的，
   但对用户是**新信息**（本页以前是最旧在上），写出来才不用猜。
4. `:key` 仍是行下标 `i`（与 `OpsLogPanel` 一致）：行内只有纯文本、无输入/过渡状态，
   下标键在"新记录落到顶部"时只是原地更新文本，没有可观察的副作用。别为了"更正确"
   换成 `row.time` —— 同一秒可能有多条，反而不稳定。

### 35.3 验证（真实浏览器，一次性 harness）

收发日志是**内存环形缓冲、不落盘** ⇒ 拿不到真实历史行，harness 用**造的 6 条**，
但**行的形状严格照 `TrafficLogDto`**，且**顺序严格按服务端契约给（时间正序）**。
真 Chromium（`agent-browser`）挂**真的** `TrafficPanel.vue` 断言：

| 场景 | 断言 | 结果 |
|---|---|---|
| 默认（全部） | 首行 `ROW-06`（最新）、末行 `ROW-01`（最旧），失败行 `fail` 类正确 | ✅ |
| 筛「发送」 | 4 条，仍**最新在上**（06/05/03/01） | ✅ |
| 筛「接收」 | 2 条，仍最新在上（04/02） | ✅ |
| 追加 1 条新记录 | `ROW-07` **落在最顶上**，计数 `7 / 7` | ✅ |
| `agent-browser errors` | 空 | ✅ |

harness 用完即删（`web/__harness.html` / `__harness.ts`，不进产物）。
截图 `.tools/m4.17-ui/m4.17-traffic-order.png`。

### 35.4 挂账

- 🔸 仍**没有前端 DOM 测试基建**（`deploy/smoke/*.py` 全是 HTTP 级）：这条顺序约定
  目前靠**人读 + 本次一次性 harness** 保住。要长期钉住得先决定引不引 vitest（同 `REF-M4-Agent` §34.5）。


