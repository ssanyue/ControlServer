# 中控 Web 服务版 — 详细落地方案

> 版本：v1.0（方案稿，未改任何代码）  
> 功能清单来源（历史）：旧版单机桌面程序（.NET 8 MAUI / Windows「中控 v3.0」，**已废弃**）  
> 新项目工作区：`D:\Project\zhongkong`  
> 编制日期：2026-10-07
>
> ⚠️ **生效范围（2026-10-07 追加）**：本文档的功能清单来自**旧版**（已废弃的单机桌面程序）的逐项拆解；
> **实现方式以本文档与服务版源码为准**。本项目与旧版**无依赖、无联动**，旧版的实现**不是**正确性判据，
> 其缺陷**不照抄**。术语见 `docs/项目边界与术语.md`。

---

## 0. 结论先行（TL;DR）

| 决策项    | 推荐结论                                                                                  | 一句话理由                                                                |
| ------ | ------------------------------------------------------------------------------------- | -------------------------------------------------------------------- |
| 运行时/后端 | **ASP.NET Core 8（Kestrel）+ IHostedService**                                           | 直接复用 ControlApp 约 **80%** 的 C# 业务代码，且天然跨平台、可托管为 Windows 服务 / systemd |
| 前端     | **Vue 3 + TypeScript + Vite**（备选 Blazor Server）                                       | 远程桌面 / 文件管理 / 拖拽布局对前端生态依赖重；若追求"一门语言复用"，退守 Blazor Server              |
| 通信     | **REST（控制类）+ SignalR/WebSocket（实时类）**                                                 | 日志、状态、分发进度、远程桌面信令都需要服务端主动推送                                          |
| 存储     | **SQLite（EF Core）+ 文件系统**                                                             | 服务化后多端并发访问，JSON 单文件并发写有丢数据风险；SQLite 单文件、跨平台、零运维                      |
| 服务托管   | **Microsoft.Extensions.Hosting** 一套代码双形态：Windows Service + Linux systemd，另附 Docker 镜像 | 一个宿主，多平台交付                                                           |
| 控制协议   | **保留 UDP/TCP/WoL 发送与对接码监听**                                                           | 与现有展厅设备协议完全兼容，零改动继承                                                  |
| 文件分发   | **服务端文件仓库 + HTTP Range 断点续传 + SHA256 校验 +（可选）局域网组播**                                  | 浏览器可直接下载；需要"自动接收"时再上轻量 Agent                                         |
| 远程桌面   | **中心服务 + 被控端 Agent 双层架构**，传输先 noVNC 后 WebRTC                                          | Windows 服务运行在 Session 0，**无法直接捕获用户桌面**，必须由用户会话内的 Agent 执行            |
| 交付节奏   | **6 个里程碑（M1→M6）**，先对齐核心控制功能，再上重资产子系统                                                  | 每阶段可独立验收，风险可控                                                        |

> **一句话概括**：把 ControlApp 的"业务内核"原样搬到浏览器可达的后台服务里，桌面 UI 换成 Web UI，Windows 专有的部分（WMI 机器码、注册表自启、托盘、屏幕捕获）抽象成跨平台接口，远程桌面/文件分发按"中心服务 + 被控端 Agent"扩展。

---

## 1. 需求与现状分析

### 1.1 参照项目 ControlApp 现状（已完整拆解）

**技术形态**

- .NET 8 MAUI，`TargetFramework = net8.0-windows10.0.19041.0`，**仅 Windows**，WinUI3 后端，非打包（`WindowsPackageType=None`）。
- 应用形态：**桌面 GUI 程序**（有主窗口、系统托盘、单实例互斥），监听服务与定时器在进程内后台跑。
- 数据落盘：`control_config.json`（配置）、`commands.json`（指令库）、`logs/log_日期.json`（日志）、`license.lic`（授权）、`.ts`（时间戳）。
- 独立配套 `LicenseManager` 项目（同样是 MAUI Windows 桌面程序），负责超级密码、机器码绑定、授权文件生成/解密。

**功能清单（新版必须 1:1 对齐）**

| #  | 功能模块   | 关键点                                                                                       | 平台相关性              |
| -- | ------ | ----------------------------------------------------------------------------------------- | ------------------ |
| 1  | 按钮（瓦片） | 名称、颜色、文字色、字号、背景图、宽高、自由排布坐标、对接码                                                            | 纯业务                |
| 2  | 动作     | 多动作顺序执行、每动作可延迟(ms)、协议 UDP/TCP/WoL、数据类型 String/Hex/ANSI/ASCII/UTF-8、目标 IP/端口、动作级对接码、可引用指令库 | 纯业务（ANSI 需注意代码页）   |
| 3  | 子按钮集   | 与按钮同级容器、圆形子按钮、**共享父集 IP/端口**、无定时/月度计划/WoL                                                 | 纯业务                |
| 4  | 对接码监听  | UDP 端口（默认 9999），三级匹配：**按钮级 → 子按钮级 → 动作级**，Hex/字符串匹配                                       | 纯业务                |
| 5  | 定时器    | 每日 / 指定星期（多时间点）、月度计划（每月独立日期、多时间点）、防重复触发                                                   | 纯业务                |
| 6  | 指令库    | 常用指令复用，独立存储、不参与配置导入导出                                                                     | 纯业务                |
| 7  | 日志     | 内存环形缓冲 + 批量落盘（500ms）、最近日志面板、按日期分文件、上限 5000 条                                              | 纯业务                |
| 8  | 授权     | AES 加密 `.lic`、机器码绑定、到期日期、2 个月试用、超级密码/用户密码                                                 | 机器码依赖 WMI（Windows） |
| 9  | 时间校准   | NTP（阿里云/腾讯云/中国 NTP 池）优先 + HTTP 备用、RTT 补偿、时间戳加密防回退、每日 5:00 记录                              | 纯业务（跨平台可移植）        |
| 10 | 机器码    | WMI 采集 CPU/主板/UUID → SHA256 → `XXXX-XXXX-XXXX-XXXX`                                       | **Windows 专有，需重写** |
| 11 | 配置导入导出 | 单文件 base64 内嵌图片、`ConfigBackup` 打包/还原                                                      | 纯业务                |
| 12 | 外观与布局  | 背景图、默认按钮图、窗口尺寸锁定、自由排布、批量对齐/分布、图片透明边裁剪                                                     | **UI 相关，需重做**      |
| 13 | 系统集成   | 开机自启（注册表 Run）、关闭最小化托盘、单实例互斥、提前台                                                           | **Windows 专有，需替换** |
| 14 | 认证     | 用户密码 / 超级密码（SHA256）                                                                       | 需升级为服务端会话认证        |

**可直接复用的代码资产盘点**

- **几乎零改动**：`Models/*`（全部 10 个模型）、`Services/BeijingTime.cs`、`Services/NetworkService.cs`、`Services/LogService.cs`、`Services/CommandLibraryService.cs`、`Services/ListenerService.cs`、`Services/TimerService.cs`、`Services/NetworkTimeService.cs`、`Services/TimeStampService.cs`、`Services/LicenseService.cs`、`Services/AuthService.cs`、`Services/ConfigService.cs`（逻辑层）
- **小幅改造**：`Services/ImageTrimHelper.cs`（ImageSharp 本身跨平台，仅 API 形态调整）、`AppServices.cs`（改为 DI 注册）
- **必须重写**：`MachineCodeService.cs`（WMI → 跨平台）、`Platform/TrayIconHelper.cs`、`App.xaml.cs`（窗口/托盘/互斥）、全部 `Pages/*.xaml(.cs)` 与 `MainPage`（→ Web UI）

> 结论：**选 .NET 后端能省掉约 80% 的重写量**。这是本方案选择 ASP.NET Core 的核心理由。

### 1.2 新项目的需求

1. **功能与 ControlApp 一致**（上表 1–12 项全部保留）。
2. **以服务方式运行**：无界面（headless）、开机自启、崩溃自愈、可远程运维。
3. **局域网 Web 操作界面**：局域网内**任意电脑/平板**用浏览器打开即用，无需安装客户端。
4. **跨平台**：Windows、Linux（后续可含 macOS）。
5. **后期扩展**：文件分发、局域网远程桌面 —— 本方案需**预先为其预留架构**（不能做完再推倒重来）。

### 1.3 从桌面版到服务版的关键变化

| 维度    | ControlApp（桌面） | 新版（服务 + Web）             | 影响                             |
| ----- | -------------- | ------------------------ | ------------------------------ |
| 界面    | MAUI 原生窗口      | 浏览器 Web UI               | 全部页面重做；布局改响应式                  |
| 运行身份  | 登录用户进程         | Windows 服务 / systemd     | **Session 0 隔离**，无法直接碰桌面（见 §6） |
| 认证    | 本进程内密码校验       | 服务端会话（Cookie/JWT）+ 多用户角色 | 必须服务端化，安全等级提高                  |
| 配置并发  | 单进程读写          | 多浏览器并发读写                 | JSON 文件不足以支撑，建议 SQLite         |
| 自启/托盘 | 注册表 Run + 托盘图标 | 服务自启；无托盘（改为 Web 控制台）     | 系统集成部分替换                       |
| 机器码   | WMI            | 跨平台指纹                    | 重写为 Provider                   |
| 实时反馈  | 进程内事件          | 服务端 → 浏览器推送              | 引入 SignalR/WebSocket           |
| 分发    | 单个 exe         | 安装包 + 服务 + 前端静态资源        | 打包链路重做                         |

### 1.4 关键约束与风险（必须前置认知）

1. **【致命】Windows 服务 Session 0 隔离**：以服务身份运行的进程位于隔离会话，**不能捕获交互式桌面、不能向用户桌面注入键鼠**。→ 远程桌面能力**必须**引入"用户会话内运行的被控端 Agent"，中心服务只做信令/中继/鉴权。这不是可选设计，是硬约束。
2. **Linux 图形栈碎片化**：X11 与 Wayland 的捕获/注入方式完全不同。→ 一期明确"X11 优先，Wayland 走 PipeWire/xdg-desktop-portal"，或先限定发行版范围。
3. **ANSI 编码跨平台**：桌面版用 GBK(936)。Linux 无 GBK 代码页时需注册 `System.Text.Encoding.CodePages` 包并显式 `Encoding.RegisterProvider`。
4. **浏览器 + 自签证书**：局域网 HTTPS 需自签 CA，浏览器首次访问会告警。→ 用 `mkcert`/自建 CA + 提供证书安装指引，或先 HTTP（内网可控时）+ 明确风险。
5. **UDP 广播 / 多网卡**：WoL 与监听在多网卡机器上需枚举网卡、绑定指定网段，不能简单 `255.255.255.255` 一脚踢。
6. **配置并发写**：服务化后多客户端同时保存 → 需要写队列或数据库事务。
7. **权限**：Linux 下 <1024 端口需 root/capability；服务最低权限原则。

---

## 2. 总体架构

### 2.1 逻辑架构

```
                       ┌──────────────────────── 局域网 ────────────────────────┐
                       │                                                        │
   [操作电脑/平板]      │                    控制指令(UDP/TCP/WoL)               │
   浏览器  ──HTTP(S)──► │  ┌──────────────────────────────┐   ─────────────►  [展厅设备]
     ▲   ◄─WebSocket──► │  │   中控中心服务 (Headless)     │                    (投影/灯控/播放器)
     │                  │  │                              │                    [被控电脑]
     │                  │  │  · Web API (REST)            │   ◄───信令──────┐
     │                  │  │  · SignalR 实时通道          │                 │
     │                  │  │  · Listener 对接码监听(UDP)   │                 │
     │                  │  │  · Timer 定时调度            │                 │
     │                  │  │  · 文件仓库 / 分发           │                 │
     │                  │  │  · 授权 / 认证 / 审计日志     │                 │
     │                  │  └────────────┬─────────────────┘                 │
     │                  │               │                                   │
   [其他浏览器] ─────────┘               │ (可选) 信令/中继                   │
                                        ▼                                   │
                       ┌──────────────────────────────┐                      │
                       │  被控端 Agent（用户会话）      │◄─────────────────────┘
                       │  · 屏幕捕获/编码              │
                       │  · 键鼠注入                  │
                       │  · 文件接收落地              │
                       └──────────────────────────────┘
```

### 2.2 分层

| 层   | 内容                                     | 载体                                  |
| --- | -------------------------------------- | ----------------------------------- |
| 表现层 | Web UI（按钮墙、编辑页、设置、日志、文件、远程桌面）          | 浏览器（Vue/Blazor）                     |
| 接入层 | REST API、SignalR Hub、静态资源、认证中间件、IP 白名单 | ASP.NET Core                        |
| 应用层 | 用例编排：执行动作、保存配置、推送文件、发起远程会话             | Core Service                        |
| 领域层 | 模型 + 领域服务（网络、监听、定时、授权、时间）              | `ControlServer.Core`（复用 ControlApp） |
| 平台层 | 机器码、防火墙、屏幕捕获、输入注入、服务生命周期               | `ControlServer.Platform`（按 OS 分支）   |
| 存储层 | SQLite（配置/日志/授权元数据/文件索引）+ 文件系统（图片/分发包） | EF Core + 磁盘                        |

---

## 3. 技术选型（含权衡）

### 3.1 后端运行时

| 方案                 | 复用 ControlApp   | 跨平台                 | 部署            | 风险          | 结论       |
| ------------------ | --------------- | ------------------- | ------------- | ----------- | -------- |
| **ASP.NET Core 8** | ★★★★★（~80% 直接搬） | Windows/Linux/macOS | 单文件/服务/Docker | 低           | **✅ 推荐** |
| Go                 | ★（需全量重写）        | 全平台                 | 单二进制最简        | 中高（重写+生态差异） | 备选       |
| Node.js + TS       | ★（重写）           | 全平台                 | 需 Node 运行环境   | 中           | 备选       |
| Python FastAPI     | ★（重写）           | 全平台                 | 打包分发繁琐        | 中           | 不推荐      |

> 推荐 **ASP.NET Core 8**：最大化继承既有资产与团队技能（.NET/WPF/MAUI 栈），且 Kestrel、Hosting、WindowsService、systemd 全部官方支持。

### 3.2 前端框架

| 方案                    | 语言统一        | 实时能力              | UI 生态                     | 远程桌面适配                     | 结论         |
| --------------------- | ----------- | ----------------- | ------------------------- | -------------------------- | ---------- |
| **Vue 3 + TS + Vite** | 否（前后端两语言）   | 好（配合 SignalR 客户端） | ★★★★★（Element Plus/Naive） | 好（Canvas/WASM/WebRTC 生态成熟） | **✅ 首选**   |
| React + TS            | 否           | 好                 | ★★★★★（Ant Design）         | 好                          | 并列首选       |
| Blazor Server         | **是（纯 C#）** | 内置 SignalR        | 中（组件库较少）                  | 一般（重 JS 场景需 JS 互操作）        | 备选（求语言统一时） |
| Blazor WASM           | 是           | 需额外 SignalR       | 中                         | 一般                         | 备选         |

> 推荐 **Vue 3 + TypeScript**。理由：本项目的重头戏（远程桌面画布、拖拽布局编辑、文件管理器、批量对齐）对前端库生态依赖强；Blazor 在这些"像素密集 + 需要成熟 JS 库"的场景会反复做 JS 互操作。若团队坚持单一语言，Blazor Server 可行，但需接受组件生态偏弱。

### 3.3 数据存储

| 方案                   | 改动量          | 并发安全      | 查询能力         | 结论          |
| -------------------- | ------------ | --------- | ------------ | ----------- |
| **SQLite + EF Core** | 中（引入 ORM/迁移） | 强（WAL 模式） | 强（可查询/分页/历史） | **✅ 推荐**    |
| 保留 JSON + 写队列/文件锁    | 小            | 中（需自建队列）  | 弱（全量读写）      | 备选（追求最小改动时） |
| PostgreSQL/MySQL     | 大（引入独立服务）    | 强         | 强            | 过重，本场景不需要   |

> 推荐 **SQLite**：单文件、跨平台、零运维，配置数据量小完全够用；日志量大时可保留"按日期 JSON/NDJSON 文件"策略。图片、分发包仍存文件系统，DB 只存索引与元数据。

### 3.4 服务托管与交付

| 平台      | 方式                                                                                                |
| ------- | ------------------------------------------------------------------------------------------------- |
| Windows | `Microsoft.Extensions.Hosting.WindowsServices` + `sc create` / MSI（WiX）安装包                        |
| Linux   | `systemd` unit（`Restart=always`、`Type=notify`）+ deb/rpm 或 tar.gz                                  |
| 通用      | **Docker 镜像**（尤其 Linux 部署最省心）+ `docker compose` 模板                                                |
| 发布      | `dotnet publish -c Release -r win-x64/linux-x64 --self-contained -p:PublishSingleFile=true`，免装运行时 |

### 3.5 实时通信

**SignalR**（ASP.NET Core 内置）：自动在 WebSocket / SSE / Long-Polling 间降级，天然适合局域网。

承载场景：日志实时流、监听/定时/服务状态、动作执行结果、文件分发进度、远程桌面**信令**（SDP/ICE 交换）、在线客户端列表。

### 3.6 前端与后端契约

- REST：资源 CRUD（按钮、子按钮集、动作、指令库、设置、文件、授权）
- SignalR：实时事件（`LogAdded`、`StatusChanged`、`TransferProgress`、`RemoteSessionSignal`）
- 统一错误模型（ProblemDetails）、统一分页、统一鉴权过滤器

---

## 4. 模块设计

### 4.1 解决方案结构

```
D:\Project\zhongkong\
├── ControlServer.sln
├── src/
│   ├── ControlServer.Core/          # 领域模型 + 业务服务（复用 ControlApp）
│   │   ├── Models/                  # ButtonConfig / ButtonAction / SubButton(Set) / MonthlyPlan ...
│   │   ├── Services/                # NetworkService / ListenerService / TimerService
│   │   │                            # LogService / CommandLibraryService / LicenseService
│   │   │                            # AuthService / BeijingTime / NetworkTimeService / TimeStampService
│   │   └── Abstractions/            # IMachineCodeProvider / IConfigStore / INotifier ...
│   ├── ControlServer.Platform/      # 跨平台实现（按 OS 分支）
│   │   ├── MachineCode/  (Windows WMI | Linux machine-id/dmi)
│   │   ├── Capture/      (DXGI | X11/PipeWire)
│   │   ├── Input/        (SendInput | XTest/uinput)
│   │   └── Service/      (WindowsService | systemd notify)
│   ├── ControlServer.Web/           # ASP.NET Core 宿主
│   │   ├── Api/                     # REST 控制器
│   │   ├── Hubs/                    # SignalR Hub
│   │   ├── Auth/                    # 认证/授权/白名单
│   │   ├── HostedServices/          # 后台服务包装（Listener/Timer/TimeStamp）
│   │   └── wwwroot/                 # 前端构建产物
│   └── ControlServer.Agent/         # 被控端 Agent（远程桌面/文件接收，用户会话运行）
├── web/                             # 前端工程（Vue 3 + Vite + TS）
├── tests/                           # 单元/集成测试
├── deploy/                          # systemd / windows-service / docker / 安装脚本
└── docs/                            # 本文档及后续设计文档
```


### 4.2 服务层迁移映射

| ControlApp 现状                                             | 新版做法                                                                           |
| --------------------------------------------------------- | ------------------------------------------------------------------------------ |
| `AppServices` 静态单例                                        | 改为 `IServiceCollection` DI 注册，`IHostedService` 承载 Listener/Timer               |
| `ListenerService.Start()` 手动调用                            | `PairingListenerHostedService : BackgroundService`，随宿主启动                       |
| `TimerService` 用 `System.Timers.Timer`                    | 保留逻辑，改为 `BackgroundService` + `PeriodicTimer`（更现代、可取消）                         |
| `ConfigService` 静态读写 JSON + 内存缓存                          | `IConfigStore`：SQLite 实现 + 内存缓存 + 写队列；保留 JSON 导入导出（兼容旧 `.json`/`ConfigBackup`） |
| `LogService` 静态 + 事件                                      | 保留环形缓冲 + 批量落盘；`OnLogAdded` 事件桥接到 SignalR 推送                                    |
| `LicenseService`（读 `license.lic`）                         | 逻辑保留；路径改为服务数据目录；机器码改由 `IMachineCodeProvider` 提供                                |
| `AuthService`（进程内静态）                                      | 升级为 ASPNET 认证方案（Cookie/JWT）+ 角色（Admin/Operator/Viewer）                         |
| `MachineCodeService`（WMI）                                 | `IMachineCodeProvider`，Windows 走 WMI（原逻辑），Linux 走 `/etc/machine-id` + DMI UUID |
| `BeijingTime` / `NetworkTimeService` / `TimeStampService` | 原样移植（纯 .NET，跨平台）                                                               |
| `ImageTrimHelper`（ImageSharp）                             | 原样移植，服务端在上传图片时预处理并落盘                                                           |

### 4.3 Web API 设计（初稿）

```
# 认证
POST   /api/auth/login              # 登录，返回会话/Token
POST   /api/auth/logout
GET    /api/auth/me                 # 当前身份与角色

# 按钮 / 子按钮集
GET    /api/buttons                 # 列表（含排序 Order）
POST   /api/buttons
PUT    /api/buttons/{id}
DELETE /api/buttons/{id}
POST   /api/buttons/{id}/execute    # 手动触发（等价点击瓦片）
POST   /api/buttons/{id}/clone
GET    /api/subbutton-sets
POST   /api/subbutton-sets
PUT    /api/subbutton-sets/{id}
DELETE /api/subbutton-sets/{id}
POST   /api/subbutton-sets/{setId}/subbuttons/{subId}/execute

# 指令库
GET/POST/PUT/DELETE  /api/commands

# 状态 / 日志
GET    /api/status                  # 监听/定时/服务健康
GET    /api/logs/recent?count=100
GET    /api/logs?date=yyyy-MM-dd
DELETE /api/logs

# 设置 / 配置
GET/PUT /api/settings
GET    /api/settings/backup         # 导出单文件（base64 内嵌图片）
POST   /api/settings/restore        # 导入

# 授权
GET    /api/license
POST   /api/license/import

# 文件分发（M4）
GET    /api/files                   # 文件/目录列表
POST   /api/files/upload
GET    /api/files/{id}/download     # 支持 Range 断点续传
DELETE /api/files/{id}
POST   /api/files/{id}/distribute   # 定向分发
GET    /api/transfers               # 分发任务与进度

# 远程桌面（M5）
GET    /api/agents                  # 在线被控端
POST   /api/remote/sessions         # 创建会话（返回 sessionId）
POST   /api/remote/sessions/{id}/close

# 实时
WS     /hubs/events                 # SignalR：日志/状态/进度/远程信令
```

### 4.4 认证与授权

- **多用户角色**：`Admin`（超级密码语义）、`Operator`（可操作按钮/触发）、`Viewer`（只读看板）。
- 会话：优先 **Cookie + SameSite** 或 **JWT**（视前端形态定），密码仍 SHA256（建议升级为 PBKDF2/BCrypt）。
- **局域网访问控制**：可配置允许网段（CIDR 白名单）+ 可选 HTTPS。
- 关键操作（改配置、恢复出厂、导入授权）写审计日志。

### 4.5 页面对应关系（桌面 → Web）

| ControlApp 页面                            | 新版路由                                  |
| ---------------------------------------- | ------------------------------------- |
| MainPage（按钮墙 + 最近日志 + 弹窗子按钮集）            | `/` （看板，响应式瓦片 + 右侧日志抽屉）               |
| ButtonEditPage                           | `/buttons/:id`                        |
| SubButtonSetEditPage / SubButtonEditPage | `/sets/:id`、`/sets/:id/sub/:sid`      |
| CommandLibraryPage                       | `/commands`                           |
| SettingsPage                             | `/settings`（分标签：安全/通用/外观/数据/授权/文件/远程） |
| LogViewPage                              | `/logs`                               |
| PasswordPage / VerifyPasswordPage        | 登录/提权弹窗                               |
| LicenseImportPage / TimeSyncAlertPage    | `/license`、全局横幅提示                     |

---

## 5. 文件分发子系统（预留设计）

### 5.1 场景定义

1. **仓库共享**：服务端集中存放分发文件，局域网任何浏览器可浏览/下载（展厅固件、素材、播放列表包）。
2. **定向分发**：把某文件/文件夹推送到指定终端（需终端运行 Agent 或浏览器主动拉取）。
3. **一致性更新**：向多台终端下发同一份内容（可选局域网组播/多播加速）。

### 5.2 设计要点

| 能力      | 方案                                                                 |
| ------- | ------------------------------------------------------------------ |
| 存储      | 文件系统分目录存储 + SQLite 存元数据（名、大小、SHA256、版本、上传者、时间）                     |
| 上传      | 分块上传（`Content-Range`），支持大文件与断点续传                                   |
| 下载      | HTTP `Range` 请求，浏览器原生断点续传 + 多线程                                    |
| 校验      | 上传/下载/分发全链路 SHA256 比对                                              |
| 权限      | 按角色控制可见/可下/可删；目录级授权                                                |
| 进度      | 分发进度经 SignalR 实时推送到所有打开的浏览器                                        |
| 定向推送    | 方案A：Agent 拉取（服务端给 Agent 下发任务，Agent HTTP 拉取落地）；方案B：暂不支持自动接收，仅提供下载链接 |
| 大并发/一致性 | 可选局域网组播（UDP Multicast）一次性推给多终端；或 P2P（终端间互传）——列为远期                  |
| 限速/配额   | 服务端可限速、磁盘配额、保留期清理                                                  |

### 5.3 与核心功能的关系

- 文件分发**复用**认证、SignalR、SQLite、日志与审计，不引入新基础设施。
- 未来"点击按钮 → 触发某终端拉取更新包"可与动作系统打通（新增 `Protocol=File` 的动作类型，或无侵入地在按钮上挂"分发"旁路动作）。

---

## 6. 局域网远程桌面子系统（预留设计）

### 6.1 硬约束（务必先看）

**Windows 服务运行在 Session 0，与用户桌面完全隔离** —— 服务进程既不能截屏用户桌面，也不能向其注入鼠标键盘。  
因此远程桌面**不能**由中心服务单独完成，必须采用：

```
[浏览器] ⇄ 中心服务(信令/中继/鉴权) ⇄ 被控端 Agent(用户会话内运行，捕获+注入)
```

> 关键澄清：**"中心服务以服务方式运行"与"被控端需要 Agent"并不矛盾**。中心服务依然是无界面 Web 服务；只有"被控的那台机器"才需要装一个随用户登录启动的轻量 Agent。

### 6.2 技术路线（两阶段）

**阶段 A（快速落地）：VNC 复用 + noVNC**

- 被控端跑 VNC Server（Windows: TightVNC/UltraVNC；Linux: x11vnc/wayvnc）
- 中心服务用 **WebSocket 代理**（websockify 逻辑）把 noVNC 前端接到 VNC Server
- 优点：成熟、跨平台、浏览器零插件；缺点：无损画质时带宽偏高、延迟中等
- 工作量最小，可作为**验收型 MVP**

**阶段 B（体验升级）：自研 WebRTC**

- 捕获：Windows **DXGI Desktop Duplication**；Linux **X11(XShm) / Wayland(PipeWire + xdg-desktop-portal)**
- 编码：H.264（优先硬件编码 NVENC/QSV/VAAPI）或 VP8/VP9
- 传输：**WebRTC**（浏览器原生、P2P、低延迟），中心服务仅做信令（SDP/ICE）与 NAT 中继（STUN/TURN 或自建中继）
- 输入：Windows **SendInput**；Linux **XTest / uinput**
- 优点：低延迟、可控、体验接近商业远程桌面；缺点：工程量大、平台适配复杂

### 6.3 建议

- 一期上 **noVNC** 打通"浏览器能看能控"闭环，验证鉴权/审计/会话管理；
- 二期再上 **WebRTC**，优先 Windows（业务主战场），Linux 按发行版逐步覆盖；
- 全程：强制认证 + 授权 + 会话审计 + 画面水印（可选）+ 可关闭开关（合规）。

---

## 7. 跨平台适配差异清单

| 能力      | Windows                              | Linux                                                | 备注                     |
| ------- | ------------------------------------ | ---------------------------------------------------- | ---------------------- |
| 服务托管    | Windows Service（`UseWindowsService`） | systemd（`Type=notify`）                               | 同一 `Program.cs` 分支     |
| 开机自启    | 服务自动启动                               | `systemctl enable`                                   | 不再用注册表 Run             |
| 机器码     | WMI（CPU/主板/UUID）                     | `/etc/machine-id` + `/sys/class/dmi/id/product_uuid` | `IMachineCodeProvider` |
| ANSI 编码 | GBK(936) 系统自带                        | 需注册 `Encoding.CodePages`                             | 显式注册 Provider          |
| 系统托盘    | 无（服务无界面）                             | 无                                                    | 改为 Web 控制台             |
| 屏幕捕获    | DXGI / GDI                           | X11 XShm / Wayland PipeWire                          | Agent 内实现              |
| 输入注入    | SendInput                            | XTest / uinput                                       | 需相应权限                  |
| 防火墙     | `netsh advfirewall` / INetFwPolicy2  | `ufw` / `firewalld` / iptables                       | 安装脚本处理，或提示手动放行         |
| 低端口权限   | 管理员                                  | `CAP_NET_BIND_SERVICE` 或高端口                          | 建议用高端口(≥1024)          |
| 数据目录    | `%ProgramData%\中控\`                  | `/var/lib/controlserver/`                            | 统一 `IDataDirectory`    |
| 打包      | MSI（WiX）/ 单文件 exe                    | deb / rpm / tar.gz / Docker                          | 见 §3.4                 |
| UDP 广播  | 需挑网卡                                 | 需挑网卡                                                 | 多网卡枚举                  |

---

## 8. 安全设计

| 面向    | 措施                                                         |
| ----- | ---------------------------------------------------------- |
| 传输    | 局域网 HTTPS（自签 CA / mkcert）+ WSS；HTTP 模式需显式确认并告警             |
| 认证    | 服务端会话；密码升级 PBKDF2；登录失败锁定；可选双因素                             |
| 授权    | 角色 RBAC + 网段白名单（CIDR）                                      |
| 配置保护  | 授权文件 AES（沿用）；配置导出继续支持加密（`LicenseManager` 语义保留为 CLI/Web 工具） |
| 时间防篡改 | 沿用 NTP 校时 + 加密时间戳 + 每日记录 + 时间回退检测                          |
| 机器绑定  | 跨平台机器码，授权文件绑定                                              |
| 远程桌面  | 强制认证、会话一次性令牌、审计、可选画面水印、可全局关闭                               |
| 文件分发  | 上传类型校验、大小限制、SHA256 校验、目录授权、防路径穿越                           |
| 服务加固  | 最低权限运行、只暴露必要端口、日志审计、崩溃重启（systemd `Restart=always`）         |
| 合规    | 远程控制/换脸类能力需显式同意与标识（如涉及），预留开关                               |

---

## 9. 分阶段落地路线图

| 里程碑            | 目标                   | 关键交付                                                                           | 验收标准                               |
| -------------- | -------------------- | ------------------------------------------------------------------------------ | ---------------------------------- |
| **M0 方案定稿**    | 技术选型确认               | 本文档 + 决策确认                                                                     | 决策清单（§10）全部拍板                      |
| **M1 骨架可跑**    | 服务能起、浏览器能开           | ASP.NET Core 宿主 + SignalR + 认证 + SQLite + 前端框架 + 静态托管；Windows 服务/systemd 双形态跑通 | 局域网另一台电脑浏览器登录看到空看板；服务开机自启          |
| **M2 功能对齐**    | 与 ControlApp 功能一致    | 按钮/动作/子按钮集/对接码监听/定时器/指令库/日志/配置导入导出 全部 Web 化                                    | 用旧配置导出的备份能导入并完整还原；对接码触发与桌面版行为一致    |
| **M3 服务化与跨平台** | Windows + Linux 正式可用 | 机器码跨平台、授权、时间校准、防火墙脚本、单文件发布、安装包                                                 | 在 Windows 与目标 Linux 上均能安装/自启/授权/校时 |
| **M4 文件分发**    | 仓库 + 分发闭环            | 上传/下载/断点续传/校验/进度/定向分发                                                          | 大文件（>2GB）断点续传成功；分发进度实时可见           |
| **M5 远程桌面**    | 浏览器看控终端              | noVNC 阶段 → WebRTC 阶段；被控端 Agent                                                 | 在浏览器中流畅查看并操作目标机器桌面；会话审计可查          |
| **M6 加固交付**    | 可交付生产                | 安全审计、压测、安装包、运维文档、备份恢复演练                                                        | 通过安全与稳定性 checklist；一线人员可按文档独立部署    |

> 建议节奏：M1→M2 是价值主线（先"能用"），M3 决定"能交付"，M4/M5 是差异化能力（先设计预留、后按需实现）。

---

## 10. 待确认决策清单（请拍板）

| #  | 决策点           | 推荐                                            | 备选                    | 影响          |
| -- | ------------- | --------------------------------------------- | --------------------- | ----------- |
| 1  | 后端框架          | ASP.NET Core 8                                | Go / Node             | 决定复用率与团队技能  |
| 2  | 前端框架          | Vue 3 + TS                                    | React / Blazor Server | 决定开发效率与体验上限 |
| 3  | 存储            | SQLite                                        | 保留 JSON + 写队列         | 决定并发安全与改动量  |
| 4  | 是否引入被控端 Agent | **需要**（远程桌面硬性要求）                              | 无 Agent（则仅 VNC 被动方案）  | 决定远程桌面可行性   |
| 5  | 远程桌面路线        | noVNC 先行 → WebRTC                             | 直接 WebRTC / 仅 RDP 网关  | 决定工作量与体验    |
| 6  | 认证模型          | 三角色 RBAC                                      | 单管理员                  | 决定权限与页面复杂度  |
| 7  | 部署形态          | 裸机服务 + Docker 双轨                              | 仅裸机 / 仅 Docker        | 决定交付与运维方式   |
| 8  | Linux 目标范围    | 先 Ubuntu/Debian x64，X11 优先                    | 含 Wayland / arm64     | 决定适配工作量     |
| 9  | 项目/产品命名       | 待定（如 `ControlServer` / 「中控服务版」）               | —                     | 影响命名空间与品牌   |
| 10 | 旧配置兼容         | 兼容导入现有 `control_config.json` / `ConfigBackup` | 不兼容                   | 影响老用户迁移     |

---

## 11. 风险登记表

| 风险                                  | 等级 | 对策                                     |
| ----------------------------------- | -- | -------------------------------------- |
| Windows Session 0 隔离导致远程桌面不可由服务独立完成 | 高  | 采用"中心服务 + 用户会话 Agent"双层架构（§6.1）        |
| Linux 图形栈碎片化（X11/Wayland）           | 高  | 一期限定 X11 + 明确发行版；Wayland 走 portal，逐步覆盖 |
| 浏览器自签证书告警影响体验                       | 中  | 自建 CA/mkcert 分发 + 安装指引；或内网 HTTP + 风险告知 |
| 配置并发写丢数据                            | 中  | SQLite WAL + 写队列 + 乐观并发                |
| UDP 多网卡/广播失效                        | 中  | 网卡枚举 + 指定绑定网段 + 配置化                    |
| 大文件分发 IO/带宽压力                       | 中  | 分块异步流 + 限速 + 断点续传 + 可选组播               |
| 服务暴露在内网带来的安全问题                      | 中  | 认证 + RBAC + 网段白名单 + (W)SS + 审计         |
| 授权/机器码跨平台不一致                        | 中  | 统一 Provider 抽象 + 授权文件格式向后兼容            |
| 前端与后端语言割裂导致联调成本                     | 低  | 统一 OpenAPI 契约 + 代码生成 TS 客户端            |

---

## 12. 附：与桌面版的"功能对齐验收清单"

- [ ] 按钮：名称/颜色/文字色/字号/图片/宽高/排布坐标 全部可编辑并生效
- [ ] 动作：多动作顺序 + 延迟、UDP/TCP/WoL、5 种数据类型、指令库引用
- [ ] 子按钮集：圆形子按钮、共享父集 IP/端口、无定时/WoL
- [ ] 对接码：按钮级/子按钮级/动作级三级匹配，Hex 与字符串匹配
- [ ] 定时器：每日/指定星期（多时间点）、月度计划（每月独立日期）
- [ ] 日志：实时流、按日期查询、上限与清理
- [ ] 配置：单文件导出/导入（含内嵌图片），兼容旧备份
- [ ] 授权：导入、到期、机器码绑定、试用
- [ ] 时间：NTP 校时、时间戳防回退
- [ ] 跨平台：Windows 服务 + Linux systemd 均自启运行
- [ ] 多端：局域网任意设备浏览器可同时访问（含平板）
- [ ] 扩展位：文件分发、远程桌面 接口与页面预留

---

（完）
