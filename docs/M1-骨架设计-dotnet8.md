# 中控 Web 服务版 — M1 骨架设计（.NET 8 路线）

> 版本：v1.0（设计稿，**未写代码**）
> 上游文档：`docs/中控Web服务版-落地方案.md`（§0 结论 / 附录 C 选型修正）
> 对应里程碑：**M1「骨架可跑」**
> 编制日期：2026-10-07
>
> ⚠️ **生效范围（2026-10-07 追加）**：本文档的功能清单来自**旧版**（已废弃的单机桌面程序）的逐项拆解；
> **实现方式以本文档与服务版源码为准**。本项目与旧版**无依赖、无联动**，旧版的实现**不是**正确性判据，
> 其缺陷**不照抄**。术语见 `docs/项目边界与术语.md`。

---

## 0. 本文件解决什么

把附录 C 的选型落成一份**可直接开工的工程骨架蓝图**：命名、解决方案结构、项目划分与依赖方向、依赖清单、宿主与生命周期、配置、存储、Web 层、认证、实时通道、后台服务、跨平台抽象、前端工程、构建发布、Windows 服务封装、M1 验收。

**读完即可开始搭骨架，不需要再做结构决策。**

---

## 1. 已锁定决策（承接附录 C）

| # | 决策项 | 结论 | 状态 |
|---|---|---|---|
| 1 | 后端运行时 | **.NET 8 LTS + ASP.NET Core（Kestrel）** | ✅ 锁定 |
| 2 | 服务托管 | Windows Service 优先 → 后续 systemd，同一宿主 | ✅ 锁定 |
| 3 | 实时通道 | **SignalR** | ✅ 锁定 |
| 4 | 存储 | **SQLite（EF Core）+ 文件系统** | ✅ 锁定 |
| 5 | 日志/可观测 | **Serilog**（控制台 + 滚动文件，Windows 事件日志可选） | ✅ 锁定 |
| 6 | 前端 | **TypeScript + Vite**（默认 Vue 3；React 等价可换） | 🟡 默认待确认 |
| 7 | 认证 | M1 单管理员 + Cookie 会话；预留 JWT / RBAC | ✅ 锁定 |
| 8 | 交付 | 自包含发布，`win-x64` 先行，`linux-x64` 后续 | ✅ 锁定 |

> 唯一仍开放的是"前端 Vue vs React"——它**不影响任何架构决策**，M1 只搭壳，晚定不阻塞。

---

## 2. M1 的目标与非目标

### 2.1 目标（Walking Skeleton —— 一条端到端细线打通）

M1 **不是"空架子"**，而是"骨架 + 一条真实业务链路"：

1. 服务能作为 **Windows 服务**安装 → 启动 → 开机自启 → 停止 → 崩溃自动重启；
2. 局域网内**另一台电脑/平板浏览器**能打开 Web UI 并登录；
3. **端到端细线跑通**：Web 点击按钮 → 服务端发 **UDP** → 设备（或回环）收到 → 结果经 **SignalR** 实时推回浏览器；
4. 具备健康检查、结构化日志、优雅启停、全局异常兜底。

### 2.2 非目标（明确排除，留到后续里程碑）

| 排除项 | 归属 |
|---|---|
| 完整 UI（按钮编辑、动作编辑、子按钮集、定时器、月度计划…） | M2 |
| SQLite 全量数据模型与迁移 | M2（M1 只建最小表） |
| Linux 正式支持 | M3（M1 只要求 `linux-x64` 能编译/发布） |
| 文件分发、远程桌面 | M4 / M5（M1 仅建项目与接口占位） |
| 机器码跨平台、NTP 完整移植 | M3 |

---

## 3. 命名与运行约定

| 项 | 取值 |
|---|---|
| 产品名 | 中控服务版 / `ControlServer` |
| 解决方案 | `ControlServer.sln` |
| 根命名空间 / 程序集前缀 | `ControlServer` / `ControlServer.*` |
| Windows 服务名 | `ControlServer`（显示名「中控服务」） |
| systemd unit | `controlserver.service` |
| 默认 HTTP 端口 | **8080**（≥1024，免管理员/root 绑定低端口） |
| 对接码监听端口 | **9999**（沿用桌面版默认） |
| 数据目录 | Windows `%ProgramData%\ControlServer\`；Linux `/var/lib/controlserver/` |

> 名字可改，改时全局替换即可，不影响结构。

---

## 4. 解决方案结构（最终）

```
D:\Project\zhongkong\
├── ControlServer.sln
├── Directory.Build.props                  # 统一 TFM / LangVersion / Nullable / TreatWarningsAsErrors
├── Directory.Packages.props               # 中央包版本管理（所有 NuGet 版本集中于此）
├── global.json                            # 锁定 SDK 8.0.x
├── .editorconfig   .gitignore
│
├── src/
│   ├── ControlServer.Contracts/           # DTO / 请求响应 / 事件契约 / 常量（前后端共享语义）
│   ├── ControlServer.Core/                # 领域内核：模型 + 业务服务（≈ 原样搬运 ControlApp）
│   │   ├── Models/                        # ButtonConfig, ButtonAction, SubButton, SubButtonSet, MonthlyPlan, ...
│   │   ├── Services/                      # NetworkService, LogService, CommandLibraryService,
│   │   │                                  # ListenerService, TimerService, LicenseService,
│   │   │                                  # AuthService(逻辑), BeijingTime, NetworkTimeService,
│   │   │                                  # TimeStampService, ImageTrimHelper
│   │   └── Abstractions/                  # 端口接口：IConfigStore, ILogStore, IMachineCodeProvider,
│   │                                      # IDataDirectory, INotifier, ISecretProtector, IClock
│   ├── ControlServer.Infrastructure/      # 适配器：持久化 + 日志 + 通知实现
│   │   ├── Persistence/                   # EF Core DbContext, 实体映射, 迁移, 仓储实现
│   │   ├── Logging/                       # Serilog 配置, 日志落盘(NDJSON)
│   │   └── Notifications/                 # SignalRNotifier : INotifier（内核事件 → Hub 桥接）
│   ├── ControlServer.Platform.Abstractions/   # 平台能力接口（输入注入/屏幕捕获/自启/防火墙）
│   ├── ControlServer.Platform.Windows/    # WMI 机器码、SendInput、DXGI 捕获(M5)、防火墙规则
│   ├── ControlServer.Platform.Linux/      # machine-id 机器码、XTest/uinput、x11grab(M5)
│   └── ControlServer.Server/              # ASP.NET Core 宿主（Web）
│       ├── Program.cs                     # 组合根：Host 构建 + 服务注册 + 中间件管线
│       ├── Api/                           # REST：Auth / Buttons / Status / Logs / Settings 控制器
│       ├── Hubs/                          # EventsHub（SignalR）
│       ├── HostedServices/                # PairingListener / Timer / TimeStamp HostedService
│       ├── Auth/                          # Cookie/JWT 方案, 角色策略, 网段白名单中间件
│       ├── Options/                       # 强类型配置 + 校验
│       ├── appsettings.json / .Development.json / .Production.json
│       └── wwwroot/                       # 前端构建产物（Vite build 输出到此）
│
├── agents/
│   └── ControlServer.Agent/               # 被控端 Agent（M5；M1 仅建空项目占位，不实现）
│
├── web/                                   # 前端工程（Vite + TS，默认 Vue 3）
│   ├── src/  (router/ stores/ api/ views/ components/)
│   ├── vite.config.ts                     # dev proxy → :8080；build.outDir → ../src/ControlServer.Server/wwwroot
│   └── package.json
│
├── tests/
│   ├── ControlServer.Core.Tests/          # 业务逻辑单测（含从 ControlApp 迁移的用例）
│   └── ControlServer.Server.IntegrationTests/  # WebApplicationFactory 端到端
│
├── deploy/
│   ├── windows/  install-service.ps1, uninstall-service.ps1, appsettings.Production.json
│   ├── linux/    controlserver.service, install.sh
│   └── docker/   Dockerfile, docker-compose.yml            # M3
│
└── docs/
    ├── 中控Web服务版-落地方案.md
    └── M1-骨架设计-dotnet8.md             # 本文件
```

---

## 5. 项目划分与依赖方向（依赖倒置）

```
Server ──► Infrastructure ──► Core ──► Contracts
   │             │              ▲
   │             └──────────────┘（实现 Core 的端口接口）
   ├──► Platform.Windows ──► Platform.Abstractions ──► Core.Abstractions
   └──► Platform.Linux   ──► Platform.Abstractions
Agent（独立，M5）──► Contracts
```

| 项目 | 职责 | 依赖 | 目标框架 |
|---|---|---|---|
| Contracts | 纯 DTO / 事件 / 常量，无逻辑 | 无 | `net8.0` |
| Core | 模型 + 业务服务 + 端口接口 | Contracts | `net8.0` |
| Infrastructure | EF Core / Serilog / Notifier 等适配器 | Core, Contracts | `net8.0` |
| Platform.Abstractions | 平台能力接口 | — | `net8.0` |
| Platform.Windows | Windows 实现 | Platform.Abstractions | `net8.0` |
| Platform.Linux | Linux 实现 | Platform.Abstractions | `net8.0` |
| Server | Web 宿主、组合根 | Infrastructure, Platform.*, Core, Contracts | `net8.0` |
| Agent | 被控端（M5） | Contracts | `net8.0-windows` / `net8.0` |

**硬规则（决定长期可维护性）**

1. `Core` **绝不引用** ASP.NET Core / EF Core / 任何平台 API —— 保证可跨平台、可单测、可被 Agent 复用。
2. 平台相关代码只落在 `Platform.Windows` / `Platform.Linux`；`Server` 通过 DI 按 OS 注册实现。
3. **`Platform.Windows` 与 `Platform.Linux` 都定 `net8.0`**（**不是** `net8.0-windows`），`Server` 无条件引用两者，运行时按 OS 注册实现。
   > ⚠️ **设计修正**：本文件 v1.0 曾写"`Platform.Windows` 定 `net8.0-windows` + 条件引用"。实践证明**行不通** —— `net8.0` 项目**无法引用** `net8.0-windows` 项目（NuGet 报 `NU1201`，目标框架不兼容），条件引用并不能绕开。正确做法是：平台项目统一 `net8.0`，Windows 专有 API 用 P/Invoke 或 `System.Management`（其 NuGet 包本身面向 `net8.0`），并用 `OperatingSystem.IsWindows()` / `[SupportedOSPlatform]` 保证安全。这样 `Server` 单一 TFM 即可在 Linux 编译。

---

## 6. 依赖清单（中央包管理）

所有版本集中在 `Directory.Packages.props`（示例，实施时取当前稳定版）：

| 包 | 用途 | 里程碑 |
|---|---|---|
| Microsoft.Extensions.Hosting | 通用宿主 | M1 |
| Microsoft.Extensions.Hosting.WindowsServices | Windows 服务托管 | M1 |
| Microsoft.Extensions.Hosting.Systemd | Linux systemd | M1 |
| Microsoft.Extensions.Options.ConfigurationExtensions | 强类型配置 | M1 |
| Serilog.AspNetCore | 宿主日志接入 | M1 |
| Serilog.Sinks.Console / .File | 控制台 / 滚动文件 | M1 |
| Serilog.Sinks.EventLog（可选） | Windows 事件日志 | M1 |
| Microsoft.EntityFrameworkCore.Sqlite | ORM + SQLite | M1 |
| Microsoft.EntityFrameworkCore.Design | 迁移工具（仅设计时） | M1 |
| Microsoft.AspNetCore.OpenApi（+ Swashbuckle/Scalar） | API 文档 | M1 |
| FluentValidation（可选） | 请求校验 | M1 |
| System.Text.Encoding.CodePages | ANSI/GBK 跨平台编码 | M2 |
| SixLabors.ImageSharp | 图片透明边裁剪 | M2 |
| System.Management | WMI 机器码 | M1/M3 |
| Vortice.Windows | DXGI 屏幕捕获 | M5 |
| SIPSorcery（或 ffmpeg 兜底） | WebRTC | M5 |

测试栈：`xunit` + `FluentAssertions` + `Microsoft.AspNetCore.Mvc.Testing`。

> SignalR 为 ASP.NET Core 框架内置，**无需额外 NuGet 包**。

---

## 7. 宿主与生命周期（Program.cs 骨架）

M1 骨架必须体现的六件事：

1. **宿主构建**：`Host.CreateApplicationBuilder(args)` → `UseSerilog()` → `ConfigureServices`。
2. **服务形态双分支**：`if (WindowsServiceHelpers.IsWindowsService()) UseWindowsService();` 与 `UseSystemd()`（按环境生效，勿在 Windows 误启用）。
3. **Kestrel 监听** `0.0.0.0:8080`（局域网可达），端口可配。
4. **全局异常兜底三件套**：`AppDomain.CurrentDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` + ASP.NET 异常中间件 → **全部先落日志，绝不静默退出**。
5. **优雅停机**：`IHostApplicationLifetime.ApplicationStopping` → 停后台服务、flush 日志与配置、关闭监听 socket、通知浏览器。
6. **启动自检**：校验端口占用、数据目录可写、授权文件可读、配置合法；失败给出明确错误（事件日志/控制台），不裸崩。

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog(/* 控制台 + 滚动文件 */);

// 配置（强类型 + 启动即校验）
builder.Services.AddOptions<ServerOptions>()
       .Bind(builder.Configuration.GetSection("Server"))
       .ValidateDataAnnotations().ValidateOnStart();

// 存储
builder.Services.AddDbContext<AppDbContext>(o =>
       o.UseSqlite($"Data Source={dataDir}/control.db"));

// 内核服务（单例，来自 ControlServer.Core）
builder.Services.AddSingleton<NetworkService, UdpTcpNetworkService>();
builder.Services.AddSingleton<IConfigStore, SqliteConfigStore>();
builder.Services.AddSingleton<INotifier, SignalRNotifier>();

// 平台实现（按 OS）
if (OperatingSystem.IsWindows()) builder.Services.AddWindowsPlatform();
else                             builder.Services.AddLinuxPlatform();

// 后台常驻任务
builder.Services.AddHostedService<PairingListenerHostedService>();
builder.Services.AddHostedService<TimerHostedService>();
builder.Services.AddHostedService<TimeStampHostedService>();

// Web
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddControlServerAuth(builder.Configuration);

var app = builder.Build();
app.UseExceptionHandler();      // 统一 ProblemDetails
app.UseControlServerAuth();
app.MapControllers();
app.MapHub<EventsHub>("/hubs/events");
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapFallbackToFile("index.html");                 // SPA
app.MapGet("/healthz", () => Results.Ok());          // liveness
app.MapGet("/readyz",  readyProbe);                  // readiness
app.Run();
```

---

## 8. 配置与数据目录

### 8.1 配置优先级（.NET 默认链，低 → 高）

`appsettings.json` → `appsettings.{Environment}.json` → 环境变量（`CONTROLSERVER_*`）→ 命令行。
生产可用环境变量在服务安装脚本中写入。

### 8.2 `ServerOptions`（M1 字段）

| 字段 | 默认 | 说明 |
|---|---|---|
| `Http.Urls` | `http://0.0.0.0:8080` | 绑定地址与端口 |
| `Listener.PairingPort` | `9999` | 对接码监听 UDP 端口 |
| `Storage.DataDir` | §3 约定 | 数据目录 |
| `Auth.Mode` | `Cookie` | Cookie / JWT |
| `Security.AllowedCidrs` | 空（不限制） | 网段白名单 |
| `Time.NtpServers` | 阿里 / 腾讯 / 中国 NTP 池 | NTP 源 |

### 8.3 数据目录布局

```
%ProgramData%\ControlServer\
├── control.db              # SQLite（WAL：control.db-wal / -shm）
├── config\
│   ├── control_config.json # 兼容旧格式（导入导出）
│   └── commands.json
├── logs\                   # 按日期滚动
├── files\                  # 文件仓库（M4）
├── images\                 # 按钮图 / 背景图
├── license.lic
└── .ts
```

> 统一由 `IDataDirectory` 解析，禁止代码里散落硬编码路径。

---

## 9. 存储（EF Core + SQLite）

- 连接建立后执行 `PRAGMA journal_mode=WAL;`（并发读写更稳，符合"稳定优先"）。
- **M1 最小实体**：`AppUser`(Id, Name, PasswordHash, Salt, Role)、`Setting`(Key, Value)、`AuditLog`(Id, Time, User, Action, Detail)。
- **M2 补齐**：`Button`、`ButtonAction`、`SubButtonSet`、`SubButton`、`SubAction`、`MonthlyPlan`、`Command`、`FileEntry`、`TransferTask`。
- **迁移策略**：`dotnet ef migrations add` 设计时生成；服务启动时自动 `Migrate()`（配合启动前备份），避免部署时手工跑脚本。
- **旧数据兼容**：M2 实现 `ICompatImporter`，读取 `control_config.json` / `ConfigBackup` 导入。

---

## 10. Web 层骨架

- **路由分组**：`/api/auth`、`/api/buttons`、`/api/subbutton-sets`、`/api/commands`、`/api/status`、`/api/logs`、`/api/settings`、`/api/license`；M4 加 `/api/files`；M5 加 `/api/remote`。
- **统一错误模型**：`ProblemDetails` + 全局异常过滤器。
- **OpenAPI**：`/openapi/v1.json` + 文档 UI（Swagger / Scalar），供前端生成 TS 客户端。
- **静态托管 + SPA fallback**：`MapFallbackToFile("index.html")`。
- **CORS**：开发期允许 Vite dev server；生产同源，无需 CORS。

### 10.1 健康检查

| 端点 | 语义 |
|---|---|
| `/healthz` | 进程存活（liveness） |
| `/readyz` | 依赖就绪（DB 可写、监听端口已绑、配置已加载） |

M3 起纳入 SCM / systemd 探活与看门狗自愈。

---

## 11. 认证（M1 骨架）

- M1：**单管理员 + Cookie 会话**（`AddAuthentication().AddCookie()`）；密码校验逻辑复用 Core 的 `AuthService`。
- 密码哈希：M1 **沿用 SHA256**（兼容旧密码）；**M3 升级 PBKDF2/BCrypt** 并做迁移。
- 预留：`AddJwtBearer`（给 Agent / 外部调用）、角色策略 `Admin/Operator/Viewer`、网段白名单中间件（`AllowedCidrs`）。
- 审计：登录、改配置、导入授权、执行动作 → 写 `AuditLog`。

---

## 12. 实时通道（SignalR）

- `EventsHub : Hub` 挂载 `/hubs/events`。
- M1 事件集：`LogAdded(LogItem)`、`StatusChanged(StatusSnapshot)`、`ActionResult(runId, ok, message)`；M4 加 `TransferProgress`，M5 加 `RemoteSessionSignal`。
- 桥接方式：Core 的 `LogService.OnLogAdded` / 监听器 / 定时器事件 → `INotifier`（`SignalRNotifier`）→ `IHubContext<EventsHub>`。
- SignalR **不依赖桌面会话**，天生契合"无界面服务 + 浏览器实时"，这点与 Session 0 约束无冲突。

---

## 13. 后台服务（BackgroundService）

| HostedService | 承载 | 来源 | M1 行为 |
|---|---|---|---|
| `PairingListenerHostedService` | UDP 对接码监听（9999） | `ListenerService` | 启动即监听；收到码 → 三级匹配 → 发动作 → 推日志 |
| `TimerHostedService` | 每日 / 星期 / 月度定时 | `TimerService`（改 `PeriodicTimer`） | 骨架跑通，M2 接完整调度 |
| `TimeStampHostedService` | 每日 5:00 时间戳记录 | `TimeStampService` | 先做 NTP 校时 + 记录 |

统一模式：`ExecuteAsync` 内 `while(!stoppingToken.IsCancellationRequested)`，**捕获异常后记录并重启子系统，单点故障不拖垮宿主**。

---

## 14. 跨平台抽象（M1 定接口，不实现全）

| 接口 | Windows 实现 | Linux 实现 |
|---|---|---|
| `IDataDirectory` | `%ProgramData%\ControlServer` | `/var/lib/controlserver` |
| `IMachineCodeProvider` | WMI（复用原逻辑） | `/etc/machine-id` + DMI UUID |
| `ISecretProtector` | DPAPI（可选） | 文件权限 + AES |
| `IInputInjector` | SendInput（M5） | XTest / uinput（M5） |
| `IScreenCapturer` | DXGI（M5） | X11 XShm / PipeWire（M5） |
| `IFirewallRuleManager` | netsh / INetFwPolicy2 | ufw / firewalld |
| `IAutoStartRegistrar` | 服务自启（安装时设置） | `systemctl enable` |
| `IClock` | 统一走 `BeijingTime` | 同 |

> M1 只需 `IDataDirectory` + `IMachineCodeProvider` 落地 Windows 实现即可启动；其余留接口 + `NotImplementedException` 占位并注明里程碑。

---

## 15. 前端工程骨架

- `web/`：**Vite + TS**，默认 **Vue 3**（Vue Router + Pinia + Element Plus）；若选 React，则 Vite + React + React Router + Ant Design，目录结构一一对应。
- **dev**：`npm run dev`（5173）；`vite.config.ts` 代理 `/api` 与 `/hubs`(ws) → `http://localhost:8080`。
- **build**：`vite build --outDir ../src/ControlServer.Server/wwwroot --emptyOutDir`。
- **M1 页面**：登录页 + 空看板（读 `/api/status` 显示服务状态）+ 一个"测试按钮"（触发一条 UDP 动作，验证端到端）+ 日志抽屉（SignalR 实时）。
- **契约**：由 OpenAPI 生成 TS 客户端（`openapi-typescript` / `NSwag`），避免手写 DTO 导致前后端漂移。

---

## 16. 构建 / 运行 / 发布

```bash
# 开发
dotnet build ControlServer.sln
dotnet run --project src/ControlServer.Server          # 控制台形态
cd web && npm install && npm run dev                    # 前端热更新

# 发布（Windows 自包含单文件）
dotnet publish src/ControlServer.Server -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false

# 发布（Linux，M3）
dotnet publish src/ControlServer.Server -c Release -r linux-x64 --self-contained true
```

> `PublishTrimmed` 暂关（SignalR / EF Core 反射较多，裁剪易踩坑）；M6 再评估 ReadyToRun / AOT。

---

## 17. Windows 服务封装（M1 必须跑通）

**方式 A：内置服务支持（推荐）**

- 引用 `Microsoft.Extensions.Hosting.WindowsServices`；
- `Program.cs` 判断 `WindowsServiceHelpers.IsWindowsService()` → `UseWindowsService()`；
- 服务无控制台 → **日志必须落文件**（Serilog 滚动文件，可选事件日志）。

**方式 B：`sc.exe` 注册**

```powershell
sc.exe create ControlServer binPath= "C:\Program Files\ControlServer\ControlServer.Server.exe" start= auto
sc.exe description ControlServer "中控服务版"
sc.exe failure ControlServer reset= 86400 actions= restart/5000/restart/10000/restart/30000
sc.exe start ControlServer
```

`deploy/windows/install-service.ps1` 封装：复制程序 → 建数据目录并授权 → 注册服务 → 配置恢复策略 → 放行防火墙端口 → 启动。

> **`sc failure` 的 restart 恢复策略 = 附录 C.3 的"进程自愈"，M1 就要配好**，这是"一定稳定"的第一道保险。

---

## 18. M1 验收清单

- [ ] `dotnet build` 全绿（多 TFM 编译通过），单测通过
- [ ] 控制台形态 `dotnet run` 可启动，`/healthz` `/readyz` 正常
- [ ] 作为 Windows 服务可安装 / 启动 / 停止 / **开机自启** / **崩溃自动重启**
- [ ] 局域网另一台电脑浏览器（含平板）可打开并登录
- [ ] **端到端细线**：Web 点"测试按钮" → 服务发 UDP → 设备（或回环）收到 → **SignalR 实时把结果推回浏览器**
- [ ] 对接码监听端口（9999）随服务启动生效
- [ ] 日志：控制台 + 滚动文件均落盘；敏感信息（密码/授权）不入日志
- [ ] 优雅停机：停止服务时后台任务与 socket 正常释放，**无数据丢失**
- [ ] 全局异常兜底：模拟未捕获异常，进程**记录日志且不静默退出**
- [ ] `linux-x64` 能 `publish` 成功（不要求运行）——证明跨平台编译未阻塞

---

## 19. M1 → M2 衔接

M1 交付后立即进入 M2「功能对齐」：把 ControlApp 的 14 项功能逐项 Web 化，模型落 SQLite、页面落前端、契约经 OpenAPI 固化。

**M1 的骨架（宿主 / DI / 存储 / 认证 / 实时 / 日志 / 平台抽象）就是 M2 的地基，M2 不再动结构。**

---

## 20. M1 风险与注意

| 风险 | 对策 |
|---|---|
| Windows 服务无控制台，日志"看不见" | 强制文件日志 + 事件日志；首启失败必须能在 `%ProgramData%` 找到原因 |
| Session 0 下网络能力正常，但**任何桌面相关调用都会失败** | M1 不碰桌面能力；远程桌面严格留 M5 + Agent |
| 单文件发布 + SQLite 原生库 | 确认 `e_sqlite3` 随发布；必要时改用 `SQLitePCLRaw.bundle_e_sqlite3` |
| `UseSystemd()` 在 Windows 上误启用 | 按环境判断（`INVOCATION_ID` / 容器），勿无条件启用 |
| EF Core 自动迁移在服务账号下无权限 | 数据目录安装时预创建并授权；迁移失败显式报错、不静默 |
| 前端 Vue / React 未定 | M1 只搭壳，晚定不影响；定了再生成脚手架 |

---

## 21. 实施回填（M1 已落地 · 2026-10-07）

> 本章由实际施工回填 —— 记录已建成什么、验证到什么程度、以及**施工中发现的方案缺陷**。

### 21.1 已建成（代码已提交可编译）

| 层 | 内容 |
|---|---|
| 解决方案 | `ControlServer.sln` + `Directory.Build.props` + `Directory.Packages.props`（中央包版本）+ `global.json` |
| 项目（10 个） | Contracts / Core / Infrastructure / Platform.Abstractions / Platform.Windows / Platform.Linux / Server / **Agent（占位）** / Core.Tests / Server.IntegrationTests |
| 宿主 | Program.cs：Serilog（控制台+滚动文件）、Windows 服务/systemd/控制台三形态、Kestrel 0.0.0.0:8080、全局异常兜底三件套、优雅停机、启动自检 |
| 内核 | `BeijingTime`、`LogService`（环形缓冲+NDJSON 批量落盘+事件）、`NetworkService`（UDP/TCP/WoL）、`PairingListenerService`（UDP 监听）、`Sha256PasswordHasher`、`MachineCodeFormatter`、`InstallIdentity`、`DataDirectoryDefaults` |
| 适配 | `FileSystemDataDirectory`、EF Core + SQLite（users/settings/audit_logs，WAL，EnsureCreated）、`DatabaseInitializer`、`SignalRNotifier<THub>` |
| 平台 | `WindowsMachineCodeProvider`（WMI→回退安装标识）、`LinuxMachineCodeProvider`（machine-id + DMI UUID）；`IInputInjector`/`IScreenCapturer`/`IFirewallRuleManager`/`IAutoStartRegistrar`/`ISecretProtector` 已定义待 M5/M3 实现 |
| Web | 认证 Cookie、RBAC 策略、`EventsHub`、`/api/auth`、`/api/status`、`/api/logs/recent`、`/api/test/udp`、`/api/test/loopback`、`/api/system/machine-code`、`/healthz`、`/readyz`、Swagger、SPA 回退 |
| 后台任务 | `LogRelayHostedService`、`PairingListenerHostedService`、`HeartbeatHostedService`、`StatusBroadcastHostedService` |
| 前端 | `web/`：Vite 5 + Vue 3 + TS；登录页 + 看板（状态卡）+ 端到端测试面板 + 实时日志抽屉（SignalR 自动重连）；构建产物输出到 `wwwroot` |
| 部署 | `deploy/windows/install-service.ps1`（含 `sc failure` 恢复策略）、`uninstall-service.ps1`、`deploy/linux/controlserver.service`、`install.sh` |

### 21.2 实测验证结果

| 验证项 | 结果 |
|---|---|
| `dotnet build`（全解决方案，10 项目） | ✅ **0 警告 0 错误** |
| `dotnet test` | ✅ **15/15 通过**（10 单测 + 5 集成，集成测试真实启动宿主） |
| `GET /healthz` | ✅ 200，`serverTime` 为 `+08:00` |
| `GET /readyz` | ✅ 200 `{database:true, listener:true, listenerPort:9999}` |
| `GET /` 与 `GET /assets/index-*.js` | ✅ 200（SPA 静态托管） |
| 深路由 `/sets/abc/sub/def` | ✅ 200 text/html（SPA 回退生效） |
| `GET /api/status` 匿名 | ✅ **401**（Cookie 认证拦截） |
| `POST /api/auth/login` | ✅ 200，返回 `mustChangePassword:true` |
| `POST /api/test/loopback` | ✅ 发出 14B → 127.0.0.1:9999，**监听侧收到**：`收到对接码 [14B] 来自 127.0.0.1:53773 · ASCII="SMOKE-TEST-001" · HEX=534D4F4B...` |
| `GET /api/system/machine-code` | ✅ 真实 WMI 指纹 → `084C-98CF-BE5A-9AEC` |
| `POST /hubs/events/negotiate` | ✅ 200（SignalR 通道可用） |
| 数据目录 | ✅ `%ProgramData%\ControlServer` 建出 config/ files/ images/ logs/ + `control.db`(+`-wal`/`-shm`) |
| 日志落盘 | ✅ `logs/server-YYYYMMDD.log` 与 `logs/log-YYYY-MM-DD.ndjson` 均有内容 |
| 数据库播种 | ✅ 日志记录"已创建默认管理员 admin，请首次登录后立即修改密码" |
| 宿主"停机→重启"3 轮 | ✅ 集成测试通过（端口重绑 + DB 复用均无冲突） |

> **端到端细线成立**：`浏览器 → HTTP → 服务端 UDP 出 → 服务端 UDP 入（对接码监听）→ 内核日志 → SignalR 推送 → 浏览器日志面板`，整条链路已通。

### 21.3 施工中发现并修正的 3 处方案缺陷

1. **`net8.0-windows` 项目无法被 `net8.0` 项目引用**（§5 硬规则 3 已修正）。
   v1.0 设想"`Platform.Windows` 定 `net8.0-windows` + `Server` 条件引用"。实测 **NuGet 报 `NU1201`**：目标框架不兼容，条件引用绕不开。
   **修正**：两个平台项目统一 `net8.0`，Windows 专有调用走 `System.Management`（其包本身面向 `net8.0`）与运行时 `OperatingSystem.IsWindows()` 守卫。这样 `Server` 单一 TFM 即可在 Linux 编译。

2. **`IDataDirectory` 无需按平台各写一份**（§14 简化）。
   平台差异仅是"默认根路径"一处。改为：`DataDirectoryDefaults.ResolveDefaultRoot()` 决定默认值 + Infrastructure 里单个 `FileSystemDataDirectory`。少一个重复实现，且宿主可配置覆盖。

3. **`EnsureCreated` 先行，Migrations 后置**（§9 时序调整）。
   M1 无迁移工具链（`dotnet-ef` 未预装），先用 `EnsureCreatedAsync` + `PRAGMA journal_mode=WAL`。**M2 引入 EF Migrations 时必须转为 `Migrate()`，并把已建库做一次基线迁移**（否则老库与新迁移冲突）。

### 21.4 尚未完成 / 待办

| 项 | 归属 |
|---|---|
| 修改密码页面（当前默认密码仅 `mustChangePassword` 标记，无改密入口） | M2 |
| 配置 CIDR 白名单中间件（`Security.AllowedCidrs` 已配置读取，尚未挂中间件） | M2 |
| NTP 校时 + 加密时间戳防回退（`Time.NtpServers` 已配置，服务未实现） | M3 |
| 跨平台机器码一致性收敛、PBKDF2 密码升级 | M3 |
| Docker 镜像 / Dockerfile | M3 |
| 按钮/动作/子按钮集/定时器/指令库全量 Web 化 + 旧配置导入 | M2 |
| 文件分发 | M4 |
| 远程桌面 + Agent 实体 | M5 |
| 独立进程的 Ctrl+C 优雅停机**未能强制验证**（沙箱内无法注入控制台信号）；已由集成测试的宿主 Dispose 路径覆盖，建议本地手工 Ctrl+C 复核一次 | 手工复核 |

---

## 22. 绿色版单文件 exe（双击即运行 · 2026-10-07 追加）

> 目标：**交付一个 Windows exe，双击就启动服务并自动打开管理界面**，不需要装 .NET，不需要命令行。

### 22.1 结论

产物：`dist\win-x64\ControlServer.Server.exe` —— **单文件、48.7 MB、自包含**。
实测：从任意工作目录双击（或命令行启动）均可正常运行，前端页面、JS、CSS、SPA 深路由全部可用。

生成命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-portable-exe.ps1
```

### 22.2 关键实现

| 能力 | 做法 | 落点 |
|---|---|---|
| **前端进 exe** | `wwwroot/**` 从 `Content` 改为 `EmbeddedResource`，发布时不再落地目录；运行时用 `ManifestEmbeddedFileProvider` 提供（磁盘存在 `wwwroot` 时优先用磁盘，便于开发期热改前端） | `ControlServer.Server.csproj`、`Program.cs` §8.5 |
| **不生成 web.config** | Web SDK 默认会给 IIS 生成 `web.config`，自托管用不上还会多一个文件 | `IsTransformWebConfigDisabled` |
| **无需外挂配置** | 所有配置项在 `ServerOptions` 里都有**代码默认值**（端口 8080、admin/admin123 等），`appsettings.json` 不随发布输出也能跑 | `Options/ServerOptions.cs` |
| **便携配置** | 显式按 **exe 真实目录**挂一个 `appsettings.json` 源，**插在"环境变量"之前** —— 保证双击与服务两种形态行为一致（服务的工作目录是 `System32`，默认按工作目录找配置会失效），同时环境变量/命令行仍可覆盖 | `Program.cs` §0.5 |
| **自动开浏览器** | `ApplicationStarted` 时用系统默认浏览器打开 `http://localhost:{port}`；**失败绝不抛异常**（只记警告），服务形态（Session 0）自动跳过 | `Services/BrowserLauncher.cs` |
| **端口占用友好提示** | 绑定失败不再甩一屏堆栈，打印可读的处置建议（改端口 / 关掉已运行实例） | `Program.cs` §11 |
| **控制台标题** | 双击时窗口标题显示产品名与访问地址，便于任务栏辨认 | `Program.cs` §10 |

### 22.3 实测证据（真实跑通，非推断）

| 验证项 | 结果 |
|---|---|
| 发布目录文件数 | ✅ **1 个**（`ControlServer.Server.exe`，48.7 MB；无 `wwwroot/`、无 `appsettings.json`、无 `web.config`） |
| exe 拷到**干净目录**、在**别的工作目录**启动 | ✅ 启动成功 |
| 启动日志 | ✅ `宿主形态：控制台` / **`静态资源来源：内嵌资源（绿色版 / 单文件模式）`** / `监听地址：http://0.0.0.0:8080` |
| `GET /healthz` · `/readyz` | ✅ 200 / 200（`ready`，listener 9999） |
| `GET /` | ✅ 200，返回含 `/assets/index-*.js` 的 SPA HTML —— **证明内嵌 wwwroot 生效** |
| `GET /assets/index-*.js` · `*.css` | ✅ 200（129,640 B `text/javascript`）/ 200（3,791 B） |
| SPA 深路由 `/sets/abc/sub/def` | ✅ 200 `text/html`（内嵌资源模式下的回退同样生效） |
| `POST /api/auth/login` | ✅ 200 |
| 数据库 | ✅ 单文件模式下正常建库（WAL）到 `%ProgramData%\ControlServer` |
| 自动开浏览器 | ✅ 日志 `已请求系统默认浏览器打开：http://localhost:8080/`，浏览器实际弹出 |
| 一键脚本退出码 | ✅ **0**（前端构建 + 发布 + 校验全通过） |
| `dotnet test` | ✅ 15/15 通过（改动未破坏既有行为） |

### 22.4 施工中踩到的 3 个坑（均为真实故障）

1. **`IncludeAllContentForSelfExtract=true` 并不能把内容打进单文件。**
   实测发布目录里 `wwwroot/`、`appsettings.json`、`web.config` 依然散落 —— 该属性指望不上。
   **结论**：单文件必须自己把静态资源**嵌入程序集**（`EmbeddedResource` + `ManifestEmbeddedFileProvider`），并把随发布输出的默认配置**排除掉**。

2. **PowerShell 5.1 会把「无 BOM 的 .ps1」按系统 ANSI(GBK) 解析。**
   脚本里的中文注释直接被解析成乱码 → `ParserError`，脚本根本跑不起来。
   **修正**：`deploy\windows\*.ps1` 全部改为 **UTF-8 with BOM**（`install-service.ps1` / `uninstall-service.ps1` 当初也是无 BOM，属同一潜在缺陷，一并修掉）。**后续新增 .ps1 必须带 BOM。**

3. **`$ErrorActionPreference='Stop'` + 原生工具写 stderr = 脚本"成功却报失败"。**
   `vite build` 会把 Rollup 的正常警告（`/*#__PURE__*/` 注解提示）写到 stderr，在 `Stop` 模式下被当成致命错误 → 打包明明完成，退出码却是 1。
   **修正**：打包脚本不用 `Stop`，改用 `$LASTEXITCODE` 判断原生命令成败 + `try/catch` 统一收口并显式 `exit 0/1`。

### 22.5 相关文件

```
deploy/windows/build-portable-exe.ps1   一键打包（构建前端 → 发布 → 校验单文件）
docs/绿色版-使用说明.md                  面向使用者的说明（怎么用/改配置/防火墙/故障排查）
appsettings.sample.json                 便携配置模板（放到 exe 同目录即可生效）
src/ControlServer.Server/Services/BrowserLauncher.cs
src/ControlServer.Server/Options/ServerOptions.cs   （新增 StartupOptions.AutoOpenBrowser）
```

### 22.6 已知限制

- exe **未做代码签名**：首次运行可能触发 SmartScreen「未知发布者」提示。
- 单文件压缩包首次启动需解压到内存，**首启约 3～8 秒**；之后 Windows 缓存会快一些。
- 目前仅覆盖 `win-x64`（脚本 `-Runtime` 参数已预留，`linux-x64` 自包含发布此前已验证可编译）。
- **无法在沙箱内验证双击行为本身**，但已验证"从任意工作目录以控制台形态启动"这条等价路径；双击与它的差别仅在 cwd，而便携配置已按 exe 真实目录解析，不受 cwd 影响。

---

（完）
