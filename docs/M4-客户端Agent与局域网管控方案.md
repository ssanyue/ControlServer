# M4 · 客户端 Agent 与局域网管控方案

> 状态：**方案（未改一行代码）**　｜　2026-10-11
> 覆盖需求：客户端安装包下载入口 / 自动发现 / 硬件遥测 / 文件分发与传输 / 局域网远程桌面 /
> 服务形态 + 托盘 / 对齐参考项目 `D:\Project\BootAssistant` 的全部功能。
> 关联文档：`中控Web服务版-落地方案.md`（§4.3 / §5 / §6 为本文的原始预留设计）、
> `M3-托盘驻留与宿主形态方案.md`（§11 双进程托盘，本文直接复用）、`项目边界与术语.md`（§3 独立性纪律）。

---

## 0. 结论先行（TL;DR）

1. **这不是新建一个项目，而是把已预留的 `ControlServer.Agent` 从"库占位"提升为一条主线**。
   现有代码里 **60% 的地基已经浇好**：托盘（`WindowsTrayIconHost` 441 行，成品）、
   服务管理器（`WindowsServiceController`）、UDP 收包（`PairingListenerService`）、
   端口热切换、SignalR、RBAC、定时调度、NTP 校时、打包与安装脚本 —— 全部可直接复用。
2. **中心服务端**（ControlServer.Server）**不主动扫描子网**。"自动扫描到"的正确实现是
   **客户端 Agent 主动报到 + 保持一条出站长连接**。原因见 §5.1：服务端主动扫子网在防火墙、
   多网段、ARP 表容量三处都会失手，而"客户端连出来"永远成立。
3. **客户端必须是两个进程，不是一个。** 这是 Windows 会话模型决定的硬约束（§2.1），
   与需求里"以服务方式运行 + 右下角托盘"**在字面上就冲突** ——
   服务在 Session 0 没有桌面、不可能有托盘。解法与中心服务端**完全同构**：
   服务进程 `ControlServer.Agent.exe` + 用户会话托盘进程 `ControlServer.Agent.Tray.exe`。
4. **BootAssistant 的功能不能整体搬进来**：它是 MAUI（WinUI 3）应用，
   **MAUI 根本无法以 Windows 服务身份运行**。其 25 项功能需逐条判定归"服务侧"还是"会话侧"（§2.2 表）。
   其中 3 项**刻意不照抄**（无鉴权 UDP 关机、无取消的循环任务、无补偿的定时比对）——
   这些在参考项目里是缺陷，见 `项目边界与术语.md` §3。
5. **"各项硬件温度"要求一个内核驱动，这是本方案最大的不确定项**（§6.2）。
   建议做法：**温度/风扇/电压做成"可选驱动"**，未启用时字段显示"未启用"而非 0 或假值；
   其余占用类指标零驱动即可拿到。这条**必须你来拍**（决策 D6）。
6. 客户端安装包**不进发布目录**（发布目录"恰好 1 个文件"是绿色版承诺，`build-portable-exe.ps1` 有校验），
   改为放进**服务端文件仓库**（M4 存储），前端右上角直接下它（§10.5）。

### 必须你拍板的 13 件事（§15 有完整表格）

| # | 决策 | 我的推荐 |
|---|---|---|
| D1 | 客户端平台范围 | **仅 Windows 先行**，Linux 留接口 |
| D2 | 命名 | 正式名用「客户端 Agent」，包名 `ControlServer-Agent-Setup-x.y.z.exe` |
| D3 | 准入方式 | **安装包内置注册令牌** + 支持"加入码"两种 |
| D4 | 服务端地址 | **UDP 广播自动发现**为主，安装时可填固定地址兜底 |
| D5 | 配置事实来源 | **服务端下发为准**，客户端本地只做缓存 |
| D6 | 温度采集 | **可选内核驱动**（LibreHardwareMonitor）。⚠️ 2026-10-14 起**默认装**（安装勾选框默认勾上，需求①）；绿色版 / 手工起 exe 仍是"未启用" |
| D7 | 上报频率 / 历史 | 实时 5s、事件 1s；明细保留 **30 天**、日聚合保留 **1 年** |
| D8 | 远程桌面路线 | **自研 MJPEG-over-WebSocket 先行**，二期 H.264/WebRTC |
| D9 | 文件方向 | **双向**（下发 + 回传） |
| D10 | BootAssistant 的 UDP 指令通道 | **保留但加签名**，绝不照抄无鉴权版本 |
| D11 | 安装包托管 | **文件仓库**（M4 存储）+ 首启从 `downloads\` 种子 |
| D12 | 客户端页权限 | 看板 `CanOperate`；装机/改名/远控/分发 = **`AdminOnly`** |
| D13 | 远程"卸载/重启 Agent" | 做（重启 + 停止；卸载只给指引） |

---

## 1. 需求拆解与术语

### 1.1 用户原话逐条拆解

| 你的原话 | 拆出的能力 | 归属 |
|---|---|---|
| 「前端网页右上角，可以下载"客户服务端"安装包」 | 安装包托管 + 下载端点 + 顶栏入口 | §10.5 / §11.1 |
| 「同一局域网电脑安装客户服务端后可以被服务端自动扫描到」 | 发现与注册（UDP 广播 + HTTP 注册） | **§5** |
| 「读取客户端相关信息：实时硬件占用 / 各项硬件温度 / 开关机时间记录」 | 遥测采集（**用户列举 3 类 + 我补充 8 类**） | **§6.1** |
| 「（没有列举完，需要你补充）」 | 见 §6.1 的 A–H 八组 | §6.1 |
| 「文件分发，文件传输」 | 文件仓库 + 定向下发 + 回传 | **§7** |
| 「远程桌面（仅限局域网）」 | 屏幕捕获 + 输入注入 + 浏览器看控 | **§8** |
| 「客户端以服务方式运行，并在右下角托盘显示图标」 | 服务宿主 + 用户会话托盘 | **§2.1 / §9** |
| 「具备 `D:\Project\BootAssistant` 的所有功能（参考项目，二者没有关联）」 | 逐条功能对齐 | **§2.2** |

### 1.2 术语（正式名 vs 你的叫法）

⚠️ "**客户服务端**"这个叫法会和"**中心服务端**"撞车 —— 两个都是"服务端"，但一个在一台机器上管全局，
一个装在每台受控机器上。文档里统一如下（**这只是命名，不是改需求**）：

| 正式术语 | 你的叫法 | 是什么 | 工程名 |
|---|---|---|---|
| **中心服务端** | 服务端 / 中控服务 | 已有的 `ControlServer.Server.exe`，一台 | `ControlServer.Server` |
| **客户端 Agent** | **"客户服务端"** | 装在每台受控电脑上的常驻程序 | `ControlServer.Agent`（服务）<br>`ControlServer.Agent.Tray`（托盘） |
| **客户端主机** | 客户机 | 装了 Agent 的那台机器（作为一条记录） | 表 `AgentHosts` |
| **被控端** | — | 远程桌面语境下的客户端主机 | 同"客户端主机" |

> 若你更希望界面上就叫"客户端/客户机"，很容易改（都是展示文案）。**`Agent` 这个词建议保留在工程名里**
> ——它与 `ControlApp`/`BootAssistant` 无任何关系，纯粹是"被控端代理"的通用叫法。

### 1.3 与 BootAssistant 的关系（**这是纪律问题，先说**）

`BootAssistant` 是 `D:\Project\BootAssistant` 下的 **.NET MAUI 应用**（`net8.0-windows10.0.19041.0`，
`WinExe`，unpackaged，**无管理员清单**）。

按 `项目边界与术语.md` §3：

- 本方案**不引用**它的任何工程（无 `ProjectReference`、无硬路径、不读它的数据目录、不要求它存在）。
- 它的角色与 `ControlApp` 完全一样：**"要有哪些功能"的清单来源**。
  "**怎么实现**"以本项目自身的技术路线为准。
- **它的缺陷不照抄**。已识别 3 处必须偏离（§2.2 表末），另有 18 处健壮性问题仅作提示。

---

## 2. 两个必须先说清楚的硬约束

### 2.1 Session 0 隔离 ⇒ 客户端必须拆两个进程

**不是实现选择，是 Windows 会话模型。**

| | `ControlServer.Agent.exe` | `ControlServer.Agent.Tray.exe` |
|---|---|---|
| 运行会话 | **Session 0**（服务窗口站） | **登录用户的交互会话** |
| 谁启动 | SCM（`sc create` 装的服务） | 用户登录时（`HKCU\…\Run`） |
| 有桌面吗 | ❌ 无 ⇒ 托盘/截屏/注入**代码必须完全不启动** | ✅ 有 ⇒ 托盘、屏幕捕获、输入注入都在这 |
| 需要管理员 | 服务本身需要（**安装时**） | **不需要** |
| 能干什么 | 遥测采集、文件收发、UDP 指令、关机重启 | 托盘图标、音量键、启动程序、**远程桌面执行体** |

> **为什么"一个 exe 兼两职"不可行**：SCM 启动的进程由 `services.exe` 创建在 Session 0，
> 它无法"切"到用户会话，也无法通过 `Environment.UserInteractive` 之类的开关跳到 Session 1。
> 要让服务拉起到用户会话，必须 `WTSQueryUserToken` + `DuplicateTokenEx` + `CreateProcessAsUser`
> —— 这条能力在 `M3-托盘驻留与宿主形态方案.md` §11.4 已明确**推迟到 M5**，
> 且**恰好就是本方案要做的**（客户端托盘 + 远控执行体），一笔投入两处收益。

**好消息：这个双进程模式本项目已经跑通一遍了。** 中心服务端的服务形态就是这样交付的
（`dist\win-x64-service\` 里恰好 2 个文件）。同理，客户端安装包最终也是 2 个 exe。

### 2.2 BootAssistant 功能逐条归类（25 项）

来源：对 `D:\Project\BootAssistant` 的全量只读分析（19 个源文件）。
**"归服务侧/会话侧"是本方案的核心判断**，因为它直接决定客户端能不能"以服务方式运行还敢说自己功能全"。

| # | BootAssistant 功能 | 归 | 怎么落地 | 与参考项目的差异 |
|---|---|---|---|---|
| 1 | 单实例保护（`Global\` 互斥体） | 两侧各自 | 服务侧用 `Global\`，托盘侧用 `Local\`（**必须不同**，否则托盘会被服务误判为重复实例） | 修正：参考项目只有一处、且用 `Global\` |
| 2 | 崩溃日志（`crash.log`） | 服务侧 | 直接换成 Serilog 滚动文件（栈里已有） | 复用本项目已有能力 |
| 3 | 启动即隐藏到托盘（移屏外 + Activated 里 Hide） | 会话侧 | 不需要 —— 托盘进程**本来就没有主窗口**，不必"隐藏" | **偏离**：参考项目的时序 hack 是易碎实现 |
| 4 | 系统托盘图标（`Shell_NotifyIcon`） | 会话侧 | 复用 `ITrayIconHost` / `WindowsTrayIconHost`（成品，441 行） | 复用本项目已有能力 |
| 5 | 托盘右键菜单 | 会话侧 | 同上（`TrayMenuCommand`） | 同上 |
| 6 | 窗口消息钩子（子类化 WndProc） | 会话侧 | 由 `WindowsTrayIconHost` 内部承担 | 同上 |
| 7 | 关闭改为最小化托盘 | 会话侧 | 无主窗口，不适用 | 不需要 |
| 8 | 从托盘打开界面 | 会话侧 | 打开**服务端网页**里的"这台机器"详情页（`http://<中心服务>/#/agents/<id>`） | **升级**：参考项目只打开本地界面 |
| 9 | 真正退出（释放托盘 + 强杀兜底） | 会话侧 | 与 `TrayAgent` 同路径（优雅退出信号 + disposе） | 复用本项目已有能力 |
| 10 | **UDP 指令监听**（收 `DOWN` 关机等） | **服务侧** | **纯明文报文**（`DOWN`/`RESTART`/`+`/`-`/`0`/`1`，或 `name:arg`）+ **来源 IP 黑名单**（**2026-10-13 起，见 §27**；HMAC 签名与防重放已删） | ⚠️ **偏离**：旧版 `IPAddress.Any` 无任何鉴权；本项目**接受**"同网段任意主机可发"、把边界交给防火墙 |
| 11 | 远程关机（`shutdown /s`） | 服务侧 | 服务是 SYSTEM，天然有权限 | 一致 |
| 12 | 音量 `+`/`-`（`keybd_event`） | **会话侧** | Session 0 注入键盘是"无声失败"，必须放会话侧 | **必须拆分**（参考项目单进程，天然没问题） |
| 13 | 静音 / 解除静音（状态在内存） | **会话侧** | 同上；**状态要从真实系统读**（`IAudioEndpointVolume`）而非内存标志 | ⚠️ **偏离**：参考项目的 `_isMuted` 静态变量遇外部改静音即失准 |
| 14 | 延时发送任务（一次性） | 服务侧 | 复用 `NetworkService` | 复用本项目已有能力 |
| 15 | 循环发送任务 | 服务侧 | 同上，**必须挂 `CancellationToken`** | ⚠️ **偏离**：参考项目 `while(true)` 无取消，改配置后旧循环累积、重复发送 |
| 16 | 定时发送指令（`HH:mm:ss` 秒级比对） | 服务侧 | 复用 `TimerSchedulerService`（有**追赶窗口** `CatchUpWindow`） | **升级**：参考项目错过那一秒即**永久跳过**，且程序不在运行就完全不执行 |
| 17 | 定时合法性与 24 小时制 | 服务侧 | 配置校验在服务端做，客户端不做输入 | 一致性更好 |
| 18 | **延时启动程序**（串行累加延时） | **会话侧** | 要弹窗口，必须在用户会话 | **必须拆分** |
| 19 | 网络信息展示（计算机名 / 网卡） | 服务侧 | 并入遥测（§6.1 组 C），不再单独做一页 | **升级**：集中到服务端看板 |
| 20 | 一键复制网络信息 | 服务端网页 | 网页"复制"按钮 | 位置从本地移到网页 |
| 21 | 监听端口设置 | 服务端配置 | 客户端不接受本地配置（§9.5） | **偏离**：配置改为服务端下发 |
| 22 | 三类任务 CRUD + 上下排序 | 服务端网页 | 在"客户端详情"页里编辑，服务端下发 | **升级**：从单机本地编辑变为集中编辑 |
| 23 | 开机自启 | 服务侧 | 服务形态 = SCM 自启（**不是**注册表 `Run`） | **偏离**：参考项目写 `HKCU\Run`；本项目中心服务端已明确"服务形态不用 Run" |
| 24 | 配置持久化（exe 目录 `config.json`） | 服务侧 + 服务端 | 服务端为事实来源，客户端本地仅缓存（§9.5） | **升级** |
| 25 | 应用并刷新（重载配置重启服务） | 服务侧 | 配置变更走服务端推送 + 客户端热应用 | 复用本项目 SignalR 能力 |

**统计**：25 项中 **12 项直接复用本项目已有能力**、**6 项必须拆到会话侧**、
**5 项是对参考项目缺陷的刻意偏离**、2 项不需要。

---

## 3. 现状盘点：已经有什么可以复用

> 这张表决定工作量。**"复用"= 不需要新写，只做接线**。

| 资产 | 位置 | 复用到客户端 Agent 的方式 | 改动量 |
|---|---|---|---|
| `ControlServer.Agent` 工程 | `agents/ControlServer.Agent/` | **当前是 Library**（无 `OutputType`，只有一个 `AgentMilestone.cs` 占位）⇒ 直接升级为"Agent 共享库" | 小 |
| `ControlServer.TrayAgent` | `src/ControlServer.TrayAgent/Program.cs`（295 行） | **现成范本**：托盘进程该怎么写、怎么单实例、怎么走 SCM、怎么留日志，照抄结构 | 小 |
| `ITrayIconHost` / `WindowsTrayIconHost` | `Platform.Abstractions/TrayIcon.cs` / `Platform.Windows/WindowsTrayIconHost.cs`（441 行） | **直接复用**，零改动（图标、菜单、气泡、双击、菜单 id 都已实现） | **零** |
| `NativeMethods` | `Platform.Windows/NativeMethods.cs`（366 行） | 复用（`NOTIFYICONDATA`、`WM_*`、`Shell_NotifyIconW`、`SetWindowPos`…） | **零** |
| `WindowsServiceController` | `Platform.Windows/WindowsServiceController.cs`（256 行） | 复用（托盘进程启动/停止 Agent 服务，与中心服务端同一套 `sc sdset` 授权模式） | **零** |
| `WindowsInteractiveHost` | `Platform.Windows/WindowsInteractiveHost.cs`（255 行） | 复用（单实例互斥体、`MessageBoxW` 兜底、控制台附着） | **零** |
| `IconLoader` | `Platform.Windows/IconLoader.cs`（150 行） | 复用（从 exe 提取图标 + 缩放） | **零** |
| `IInputInjector` / `IScreenCapturer` | `Platform.Abstractions/InputAndCapture.cs` | **接口已定义好**（`MovePointer`/`ClickPointer`/`KeyPress`/`TypeText`、`EnumerateScreens`/`CaptureAsync`）⇒ M5 只需写实现 | 小 |
| `IFirewallRuleManager` | `Platform.Abstractions/HostIntegration.cs` | 安装时放行 Agent 的出站/本地回环 | 小 |
| `IAutoStartRegistrar` | 同上 | 托盘侧 `HKCU\Run` 注册 | 小 |
| `ISecretProtector` | 同上 | 保护 Agent 的长期凭据（DPAPI） | 小 |
| `PairingListenerService` | `Core/Services/PairingListenerService.cs`（169 行） | 复用其 `UdpClient` + `EnableBroadcast` 收包环 + 异常隔离；**广播发现**也用它同款写法 | 小 |
| `PairingMatchService` / `PairingDispatchService` | `Core/Services/` | 复用对接码三级匹配语义（与 BootAssistant 的"指令通道"同类） | 小 |
| `NetworkService` | `Core/Services/NetworkService.cs` | 复用 UDP/TCP 发送 + `DataEncoder` 编码（String/Hex/UTF-8 已合并，见 REF §30） | **零** |
| `TimerSchedulerService` | `Core/Services/TimerSchedulerService.cs` | 复用定时/月度计划 + 追赶窗口 + 触发记录 | **零** |
| `TimeSyncService` / `BeijingTime` / `SntpClient` | `Core/Services/` | 复用（客户端时间戳也必须是北京时间） | **零** |
| `LogService` + Serilog | `Core/Services/LogService.cs` | 复用（客户端日志本地落盘 + 可选回传中心服务端） | 小 |
| RBAC（三角色） + `LicenseGate` + 审计 | `Server/Security/`、`Contracts/Constants.cs` | 复用（新增的客户端页/端点直接挂在既有 Policy 上） | 小 |
| SignalR `/hubs/events` + `EventNames` | `Server/Hubs/EventsHub.cs` | 新增 `AgentChanged` / `AgentTelemetryUpdated` / `TransferProgress` / `RemoteSessionChanged` 事件 | 小 |
| `SwitchableConnectionListener` | `Server/Services/`（403 行） | 复用（端口热切换，与客户端无关但这个"测试过的基础设施"可参考） | **零** |
| `IConfigDocumentStore`（EF + 乐观并发） | `Core/Abstractions/` + `Infrastructure/` | **新增 `IAgentHostStore` 照它的模式写**（Singleton + 串行化写） | 中 |
| 图片服务端点 | `Server/Api/ConfigController.cs:287` | 下载端点照它写（单段文件名 + `PhysicalFileProvider` 防穿越） | 小 |
| `deploy/windows/*.ps1` + `ControlServer.iss` | `deploy/windows/` | 新增 `build-agent-installer.ps1` + `ControlServer.Agent.iss`，复用 Inno Setup 套路与 `sc sdset` 授权段 | 中 |
| `.tools/gui-uia-check`（UIA 夹具） | `.tools/` | 客户端托盘的自动化验证照它写（`AutomationId` ↔ `x:Name`） | 中 |
| `deploy/smoke/*.py` + `run-all.py` | `deploy/smoke/` | 新增 `agent-smoke.py`（起服务 + 起 Agent + 断言发现/遥测） | 中 |
| 6 个交付产物目录 | `dist/` | 新增 `dist\agent\`（2 文件）+ `dist\agent-setup\`（1 个安装包） | 小 |

**结论**：这不是"从零造"，而是**在一个已经跑通"服务 + 托盘双进程"的代码库上，加一层被控端**。

---

## 4. 总体架构

### 4.1 三层结构

```
┌──────────────────────────────────────────────────────────────────────────┐
│  ① 浏览器（展厅/机房任意电脑）                                            │
│     顶栏右上角：［客户端安装包 ↓］  页签：［客户端］                       │
└───────────────────────────────┬──────────────────────────────────────────┘
                                │ HTTPS/HTTP + SignalR(/hubs/events)
┌───────────────────────────────▼──────────────────────────────────────────┐
│  ② 中心服务端  ControlServer.Server.exe        （1 台，已有，本方案扩它） │
│     · REST /api/agents*   · Hub /hubs/agent   · 文件仓库 /api/files*       │
│     · AgentHosts 在线表   · 遥测时序库         · 分发任务   · 远控会话     │
└───────────────────────────────┬──────────────────────────────────────────┘
                                │ ┌───── Agent 出站长连接（SignalR over WS）──┐
                                │ │ ▲ 遥测上报   ▼ 指令下发 / 文件任务 / 远控 │
                                │ └──────────────────────────────────────────┘
        ┌───────────────────────┼───────────────────────┐
        │                       │                       │
┌───────▼──────────┐   ┌────────▼─────────┐   ┌─────────▼────────┐
│ 客户机 A          │   │ 客户机 B          │   │ 客户机 C          │
│ ┌──────────────┐ │   │ ┌──────────────┐ │   │ ┌──────────────┐ │
│ │ Agent.exe    │ │   │ │ Agent.exe    │ │   │ │ Agent.exe    │ │
│ │ Session 0    │ │   │ │ Session 0    │ │   │ │ Session 0    │ │
│ │ 遥测/文件/UDP│ │   │ │              │ │   │ │              │ │
│ └──────┬───────┘ │   │ └──────────────┘ │   │ └──────────────┘ │
│        │ 命名管道 │   │                  │   │                  │
│ ┌──────▼───────┐ │   │                  │   │                  │
│ │ Agent.Tray   │ │   │                  │   │                  │
│ │ 用户会话     │ │   │                  │   │                  │
│ │ 托盘/截屏/键鼠│ │   │                  │   │                  │
│ └──────────────┘ │   │                  │   │                  │
└──────────────────┘   └──────────────────┘   └──────────────────┘
```

### 4.2 连接方向（**本设计最关键的一条**）

| 方向 | 谁发起 | 走什么 | 为什么这样 |
|---|---|---|---|
| 发现 | **Agent → 中心**（UDP 广播） | `CS-DISCOVER` / `CS-HERE` | 零配置；服务端不需要扫子网 |
| 注册 | **Agent → 中心**（HTTP POST） | `/api/agents/register` | 需要凭据，走 HTTPS |
| 心跳 / 遥测 | **Agent → 中心**（SignalR WS） | `/hubs/agent` | 出站长连接，**穿透客户端防火墙** |
| 指令 / 任务下发 | 中心 → Agent（**同一条 WS**） | Hub 方法 `PushCommand` | 服务端不需要连客户端任何端口 |
| 文件下发 | Agent → 中心（HTTP GET，Range） | `/api/files/{id}/download` | 支持断点续传，走现成的 Kestrel |
| 文件回传 | Agent → 中心（HTTP POST 分块） | `/api/files/upload` | 同上 |
| 远程桌面 | 中心 → 浏览器（WS 中继）⇄ Agent（WS） | 新建 `/hubs/remote` | 中心只做信令/中继，符合 M5 原始设计 |

> **一句铁律**：**客户端不开任何入站端口**（除绑定 `127.0.0.1` 的本地管道）。
> 所有通信都是"客户端连出来"。这一条同时解决了 Windows 防火墙、NAT、以及"客户机 IP 会变"三类问题。

### 4.3 端口与进程清单（新增）

| 进程 | 监听 | 说明 |
|---|---|---|
| `ControlServer.Agent.exe` | 无（**只出站**） | 可选：`127.0.0.1:<pipe/port>` 供同机托盘连接 |
| `ControlServer.Agent.Tray.exe` | 无 | 连本机 Agent 的命名管道 |
| 中心服务端新增 | 现有的 `Http:Urls` 上（不新开端口） | `/hubs/agent`、`/hubs/remote`、`/api/agents*`、`/api/files*` 都复用同一 Kestrel |
| 中心服务端新增（UDP） | `Server:Discovery:Port`（默认 **9998**，避开对码 9999） | 收 Agent 的发现广播 |

---

## 5. 自动发现（本方案的技术核心）

### 5.1 三种方案对比

| # | 方案 | 机制 | 优点 | 致命问题 |
|---|---|---|---|---|
| **S** | **中心服务端主动扫描子网** | 遍历 `192.168.x.1-254` 探端口 | 字面最贴近"服务端自动扫描到" | ① Windows 防火墙默认丢入站 ⇒ 扫不到；② 只覆盖本网段，跨 VLAN 全失效；③ 254 台 × 超时 = 分钟级；④ 需要知道网段（多网卡要枚举）；⑤ 扫描行为本身会被安全软件告警 |
| **A** | **Agent 主动注册 + 心跳** | Agent 连中心服务 | 永远成立（出站不受防火墙限制）；即时；跨网段也行 | Agent 必须**预先知道中心服务地址** ⇒ 装机时要填 |
| **A+** | **A + UDP 广播自动发现** | Agent 先广播问"中心在哪"，中心应答 | 兼得：**零配置 + 永远成立** | 需要同广播域；多中心时需仲裁 |

**推荐 A+**。落到用户视角就是"装完就自己出现了"，但底层不是"服务端扫描客户端"，
而是"客户端广播找到服务端 → 主动注册"。

> ⚠️ **诚实说明**：如果你要求"**必须**由服务端发起扫描、客户端完全被动"（例如客户机不允许主动外连），
> 方案 S 也能做，但它会在上面 5 个问题上反复失手，且需要逐台放行入站端口。**建议不要**。

### 5.2 发现流程

```
Agent 启动
   │
   ├─(1) 读本地缓存的中心服务地址 ── 有 ──► 直接跳到 (4)
   │
   ├─(2) UDP 广播 CS-DISCOVER   →  255.255.255.255:9998（并在每个网卡的子网广播地址各发一次）
   │                                    等待 CS-HERE 应答（超时 3s × 3 轮）
   │
   ├─(3) 收到若干 CS-HERE（可能不止一台中心）──► 选择规则：
   │        ① 若有本地缓存的"上次成功地址"，优先它
   │        ② 否则取第一个应答，并把候选列表落盘，**在网页上显示"发现 N 台中心服务"供管理员确认**
   │
   ├─(4) POST /api/agents/register {machineKey, enrolledToken, hostInfo}  ← 带凭据
   │        200 → 拿到 {agentId, agentKey, heartbeatSec, serverVersion}，落盘（DPAPI 保护）
   │        401 → 令牌不对 ⇒ 托气泡提示"需要管理员在网页上重新生成加入码"
   │        403 → 已被吊销 ⇒ 同上
   │
   └─(5) 建立 SignalR 长连接到 /hubs/agent（带 agentKey）→ 开始心跳 + 遥测
```

**应答报文**（明文，不含秘密，仅用于定位）：

```
CS-HERE <product> <version> <httpPort> <pairingPort> <discoveryPort> <serverId>
例：CS-HERE ControlServer 0.1.0 8080 9999 9998 3F2A-91BC
```

**广播报文**：

```
CS-DISCOVER <agentMachineKey> <agentVersion>
例：CS-DISCOVER 084C-98CF-BE5A-9AEC 0.1.0
```

> ⚠️ **不做**的一件事：**发现广播里不含令牌**。广播是明文的、可被同网段任意主机看到的，
> 令牌只走 HTTPS。这条必须写死。

### 5.3 在线判定

| 状态 | 判据 | 网页表现 |
|---|---|---|
| **在线** | 心跳在 `3 × HeartbeatSec` 内 | 绿点 + 实时遥测 |
| **不稳定** | 心跳间断但 5 分钟内有过 | 黄点 + 最后遥测（标注"数据可能过期"） |
| **离线** | 超过 5 分钟无心跳 | 灰点 + 最后在线时间 |
| **从未上线** | 注册过但从未连上 | 灰点 + "已注册，等待连接" |
| **已吊销** | 管理员在网页上吊销 | 红点 + 不再接受连接 |

> ⚠️ 判定**只依赖心跳时间戳**，不做 ping、不做 TCP 探测（那些会引入"网络抖动 = 设备离线"的假告警）。

### 5.4 准入与认证（三方案对比）

> ## ⛔ 本节设计已被推翻（2026-10-13，产品决策）
>
> 产品口径最终定为 **「取消客户端准入令牌的设定，只要安装了客户端，局域网内都能自动发现，不设任何门槛」**。
> 本节当时选的是 **E1 + E2**、并**明确否决 E3"只校验来源网段"**；**实际落地就是 E3** ——
> 而且比 E3 更进一步：连"校验来源网段"都没有，注册端点不做任何来源判定。
>
> **落地现状**（实现细节见 `REF-M4-Agent.md` §23）：
>
> - **注册不需要任何凭据**。`POST /api/agents/register` 只校验一件内容层面的事：
>   机器标识（`machineKey`）是否报上来了 —— 没有它就没法去重，每次重试都会多出一台幽灵机器。
> - **E1 / E2 整体移除**（行为 + 界面全拆，不留开关）：令牌表读写、`/api/agents/enrollment`
>   三个端点、加入码生成与规范化、安装包烘焙令牌、安装向导的令牌 / 加入码输入框、
>   客户端的 `EnrollmentToken` / `JoinCode` 两个配置项 —— **全部删除**。
> - **安全兜底只剩两条**：在线表容量 `Server:Agent:MaxHosts`（默认 500，**防撑爆、不是准入门槛**）
>   与管理员在网页上的手工吊销 / 移除（**事后**处置，不是事前拦截）。
> - **"吊销"不再是让机器下线的可靠手段**：同一 `machineKey` 重新注册会让记录复活 ——
>   服务端无法区分"这台机器重装了"与"别人抄了它的机器码"。要真正下线一台机器，
>   **去那台机器上卸载**，而不是在中心侧吊销。
> - 因此 **中心服务的 HTTP 端口绝不该暴露到不可信网络**：一旦被端口映射 / 公网可达，
>   等于对不可信网络开放注册。这是本次放开之后**必须向部署方讲清**的一条。
>
> 下文 E1–E3 的对比、凭据生命周期与"两项凭据至少一个"的硬约束，
> **保留为设计档案，不再代表当前实现**。

| # | 方式 | 装机体验 | 安全性 | 批量部署 |
|---|---|---|---|---|
| **E1** | **安装包内置注册令牌**（编译进安装包 / `--token=` 参数） | 双击即装，零输入 | 中：**安装包泄露 = 令牌泄露**（但令牌只能注册，不能读数据；且可一键吊销） | **最好**（可静默批量） |
| **E2** | **加入码**（管理员在网页生成，如 `7K4P-9QW2`，有效期 2h） | 安装向导里有独立的「加入码」输入框；静默部署用 `/JOINCODE=` | 高（一次性 + 短时效） | 中（每台要一个码，但不必手工改配置文件） |
| **E3** | LAN 免凭据（只校验来源网段） | 零输入 | **低**：同网段任意机器可冒充 | 好 |

**原推荐 E1 + E2 都支持**（⚠️ 已作废，见上方"本节设计已被推翻"）：
- 默认走 **E1**（安装包内置，适合"一个展厅一批机器"的批量部署）；
- 网页提供"生成加入码"作为**兜底**（安装包丢了 / 临时加机器 / 令牌已吊销）；
- **绝不提供 E3**。

**凭据生命周期（⚠️ 已作废：整套令牌机制已于 2026-10-13 移除，下列流程不再存在）**：

```
① 管理员在网页「客户端」页点［生成安装包令牌］
      → 服务端生成 enrollmentToken（随机 32 字节 base64），落库，默认永久有效（可设过期）
      → 构建脚本把它编译进安装包，或装机时用 --token= 传入
② Agent 首次注册：enrollmentToken → 换取 agentKey（每台机器唯一、随机 32 字节）
      → agentKey 用 DPAPI 加密后落盘（ISecretProtector）
      → enrollmentToken 本身**不落盘**
③ 之后所有通信只用 agentKey（HTTP 头 / WS query 参数）
④ 管理员可随时：吊销单台（作废其 agentKey）/ 吊销令牌（作废全部未注册的）/ 轮换令牌
```

> ⚠️ **一个必须知道的取舍**：E1 意味着"谁能拿到安装包，谁就能往中心服务端注册一台机器"。
> 在"局域网内网 + 安装包只发给现场工程师"的场景下可接受，但要**明确告知**。
> 想收紧就切 E2（加入码），代价是每台机器要人工输码。

> 🔥 **两项凭据至少要有一个 —— 这不是建议，是硬约束**（2026-10-13 现场故障后补）
> —— ⛔ **该结论已于同日被产品决策推翻**（改为免凭据准入，见上方 §5.4 顶部说明）：
> 现在两项都空也**照样注册成功**，"装了客户端却没出现"**不再可能由凭据引起**，
> 排查可以直接跳到网络与在线表容量。下面这张排障表**保留为历史档案**。
>
> **发现与注册是两段**。UDP 广播发现只要在同一广播域就**一定成功**，
> 但注册是**匿名端点**、必须自己把住门（服务端**没有**"只校验来源网段"的档，E3 已否决）。
> 两项都空 ⇒ 客户端**每 60 秒**被拒一次（HTTP **400**），机器**永远不会出现在网页的「客户端」页**。
>
> | 现象 | 含义 |
> |---|---|
> | 服务端日志成对出现 `已应答客户端发现请求` + `POST /api/agents/register → 400` | 发现正常、**注册缺凭据**（不是"发现不了"） |
> | 同上但状态码是 **401** | 凭据**存在但不对**（过期 / 已使用 / 已吊销 / 抄错） |
>
> 失败类型从**重试节奏**反推：缺凭据走退避 `1s → … → 60s` 饱和；
> 凭据被吊销走固定 **5 分钟**（`RevokedRetry`）。
> 服务端会在 `Enrollment` 类别下记一条 **Warning**（含来源 IP、机器码、原因与状态），
> **同一来源 + 同一原因只记首次**（客户端重试很密，不去重会淹掉日志）。

### 5.5 多网卡 / 跨网段 / 防火墙

| 情况 | 处置 |
|---|---|
| 客户机多网卡 | 每个网卡的子网广播地址都发一次 `CS-DISCOVER`；注册时上报**全部**网卡 |
| 跨网段（无广播） | 广播必然失败 ⇒ 安装时用 `--server=http://10.0.0.5:8080` 指定，或**预先在服务端点"登记固定地址"**下发 |
| Windows 防火墙 | **不需要开任何入站规则**（本设计全出站）；安装脚本仍会为"局域网远程桌面"预留可选的入站规则说明 |
| 多中心服务端同网段 | 记录候选列表，网页显示让管理员确认；**不做自动仲裁**（猜错更难查） |
| 客户机 IP 变化 | 无影响 —— 在线表按 `machineKey`（机器码派生的稳定标识）识别，不按 IP |

---

## 6. 遥测采集

### 6.1 采集项清单（**用户列举 3 类 + 我补充 8 组**）

标注：🟢 零依赖（服务身份即可拿到）｜🟡 需额外 API/权限（不装驱动）｜🔴 需内核驱动（可选）

#### A. 计算与系统 🟢

| 指标 | 来源 |
|---|---|
| CPU 型号 / 物理核数 / 逻辑核数 / 基准频率 / 最大频率 | WMI `Win32_Processor` |
| CPU 当前占用率 / 每核占用率 | `PerformanceCounter`（PDH）或 `GetSystemTimes` |
| CPU 当前频率（实时） | `Win32_Processor.CurrentClockSpeed`（部分机器为 0 ⇒ 显示"—"） |
| 进程数 / 线程数 / 句柄数 / 上下文切换 | PDH `System\*` |
| 内存 总量 / 已用 / 可用 / 占用率 | `GlobalMemoryStatusEx` |
| 内存 提交量 / 提交上限 / 分页文件 | PDH `Memory\*` |
| 内存条：型号 / 容量 / 频率 / 插槽 | WMI `Win32_PhysicalMemory` |
| OS 版本 / 内部版本 / 补丁号 / 架构 | `Environment` + 注册表 `CurrentVersion` |
| 计算机名 / 域或工作组 / 当前登录用户 | `Environment` / WMI |
| 系统启动时长（uptime） | `Environment.TickCount64` 或 `Win32_OperatingSystem.LastBootUpTime` |

#### B. 存储 🟢（SMART 详情为 🟡/🔴）

| 指标 | 来源 |
|---|---|
| 各分区：盘符 / 卷标 / 文件系统 / 总容量 / 已用 / 可用 / 占用率 | `DriveInfo` |
| 各物理磁盘：型号 / 序列号 / 容量 / 分区表 | WMI `Win32_DiskDrive` |
| 磁盘读写速率（B/s）/ 队列长度 / 平均响应时间 | PDH `PhysicalDisk\*` |
| 磁盘阵列/RAID 状态 | WMI（部分支持） |
| SMART 健康状态 / 通电时长 / 重映射扇区 | 🟡 `MSStorageDriver_FailurePredictStatus`（**多数需驱动，见 §6.2**） |
| 硬盘温度 | 🔴 同上 |

#### C. 网络 🟢

| 指标 | 来源 |
|---|---|
| 网卡列表：名称 / 描述 / MAC / IPv4 / IPv6 / 子网掩码 / 网关 / DNS | `NetworkInterface` |
| 网卡 连接速度 / 状态（Up/Down）/ 是否无线 | `NetworkInterface.Speed` / `OperationalStatus` |
| 实时 上行 / 下行速率 | PDH `Network Interface\*` 或 `NetworkInterface.GetIPv4Statistics()` 差分 |
| 累计收发字节 / 丢包 / 错误包 | 同上 |
| 活动 TCP 连接数 | `GetExtendedTcpTable`（可选） |
| 默认网关连通性 / 中心服务端连通性（RTT） | 主动探测（低频，如 30s） |

#### D. 图形 🟢（GPU 温度 🔴）

| 指标 | 来源 |
|---|---|
| GPU 型号 / 驱动版本 / 驱动日期 | WMI `Win32_VideoController` |
| 显存 总量 / 已用 | PDH `GPU Adapter Memory\*` |
| GPU 使用率（整体 / 各引擎） | PDH `GPU Engine\*` |
| 显示器数量 / 各自分辨率 / 刷新率 / 是否主屏 | `EnumDisplayMonitors` + `GetDeviceCaps` |
| GPU 温度 / 功耗 / 风扇转速 | 🔴 驱动，或 NVIDIA NVML（仅 N 卡，无需驱动） |

#### E. 温度 / 电压 / 风扇 🔴（**本方案最大不确定项**）

| 指标 | 说明 |
|---|---|
| CPU 封装温度 / 各核心温度 | 需 MSR 访问 ⇒ 驱动 |
| 主板 / 芯片组温度 | 需 SMBus ⇒ 驱动 |
| GPU 温度 | 见组 D |
| 硬盘温度 | 需 SMART 直读 ⇒ 驱动 |
| 风扇转速（CPU / 机箱 / GPU） | 需 SMBus/EC ⇒ 驱动 |
| 各路电压（Vcore / +12V / +5V / +3.3V） | 需 SMBus ⇒ 驱动 |

#### F. 开关机与稳定性记录 🟢（**你的第 3 条要求**）

| 指标 | 来源（Windows 事件日志） |
|---|---|
| 开机时间（每次） | `System` 日志 **6005**（事件日志服务启动）+ **6013**（启动时长） |
| 正常关机时间 | `System` 日志 **6006**（事件日志服务停止） |
| 用户/进程发起的重启关机 | `System` 日志 **1074**（含发起者进程与用户） |
| **意外断电 / 非正常关机** | `System` 日志 **6008**（意外关机）+ **41**（Kernel-Power，含电源异常） |
| 蓝屏（BSOD） | `System` 日志 **1001**（BugCheck）+ `Microsoft-Windows-Kernel-Boot` |
| 系统休眠 / 恢复 | `System` 日志 **42**（进入睡眠）/ **107**（从睡眠恢复） |
| 开机耗时分解（BIOS→内核→登录） | `Microsoft-Windows-Diagnostics-Performance/Operational` **100** |
| 每日在线时长 / 开关机次数统计 | **由服务端从上述原始记录聚合**（客户端只上报原始事件） |

> ⚠️ 读事件日志需**加入 `Event Log Readers` 组或服务身份（SYSTEM）** ——
> 我们的 Agent 以服务运行，天然满足。这是"做成服务"的一个实打实的好处。

#### G. 软件与外设（可选，默认关，按需开）

| 指标 | 来源 |
|---|---|
| 已安装软件清单（名称/版本/发布者/安装日期） | 注册表 `Uninstall` 两处 + `Win32_Product`（**不用** `Win32_Product`，它会触发 MSI 修复） |
| 服务列表与运行状态 | `ServiceController.GetServices()` |
| 自启动项（Run 键 / 启动文件夹 / 计划任务） | 注册表 + `schtasks /query` + 启动目录 |
| Top N 进程（CPU / 内存） | `Process.GetProcesses()` 采样 |
| 打印机 / USB 设备 | WMI（可选） |
| 系统补丁列表 | WMI `Win32_QuickFixEngineering` |

#### H. Agent 自身与中控相关 🟢

| 指标 | 说明 |
|---|---|
| Agent 版本 / 在线状态 / 最后心跳 | 在线表 |
| 客户端配置版本 / 已应用版本 | 判断配置是否下发成功 |
| 远程指令执行记录 | 审计（谁在什么时候让这台机器做了什么） |
| 屏幕分辨率与多屏布局 | 远控前置信息 |
| 局域网内其他客户机的可见性（可选） | 远期 |

### 6.2 ⚠️ 温度采集的现实约束（**必须知情**）

这是本方案**唯一一处我不能承诺"一定拿到"的能力**。逐条说明：

| 路线 | 覆盖 | 代价 | 结论 |
|---|---|---|---|
| WMI `MSAcpi_ThermalZoneTemperature` | **极少**（多为部分笔记本/品牌机；大量台式机返回固定值 300K 或直接不支持） | 零依赖 | ❌ 单独用**基本等于没有** |
| **LibreHardwareMonitorLib**（NuGet，MPL-2.0） | **广**：CPU 核心温度 / 主板 / GPU / 硬盘温度、风扇转速、电压、SMART | 需要**内核驱动**（WinRing0/其自带签名驱动）；**杀软可能误报**；需管理员；引入第三方依赖 | ✅ **推荐，但做成"可选安装"** |
| 厂商 SDK（Intel PCM / AMD μProf / NVIDIA NVML） | 各自覆盖，NVML 可拿 N 卡 GPU 温度**无需驱动** | 多个依赖、平台绑定 | 🟡 二期可加 NVML 作为"免驱动拿 GPU 温度" |
| 自研内核驱动 | 全 | 需 **EV 代码签名 + WHQL**，成本极高；每次系统更新可能失效 | ❌ 不做 |

**建议的落地方式**：

```
安装客户端时，勾选项：
  [ ] 启用硬件温度 / 风扇 / 电压监控（需安装一个内核级驱动，部分杀软会提示）

未勾选时：
  · 遥测里温度/风扇/电压字段返回 null  → 网页显示「未启用」，**绝不显示 0 或假值**
  · 其余 A–D、F、H 全部正常工作

勾选时：
  · Agent 服务进程加载 LibreHardwareMonitorLib，采集温度类指标
  · 若驱动加载失败（被安全软件拦截 / 系统不支持），**降级为"未启用"并记录原因**，
    不重试、不刷日志、不弹窗打扰用户
```

> **为什么不做"假值兜底"**：温度显示 `0°C` 会被当成"机器很凉快"，比显示"未启用"危险得多。
> 这个原则同样适用于所有采集不到的可选字段（CPU 当前频率、SMART、显存占用等）。

### 6.3 采集频率与开销

| 类别 | 频率 | 理由 |
|---|---|---|
| 实时占用（CPU/内存/磁盘/网络/GPU） | **5s**（可配 1–60s） | PDH 采样本身有开销；5s 足够画曲线 |
| 进程/服务/Top 列表 | 60s | 开销大，只需粗看 |
| 硬件清单（型号/序列号/内存条/分区） | 启动时 + **每 6h** | 变化极少 |
| 开关机 / 蓝屏事件 | **事件驱动**（订阅事件日志推送） | 不需要轮询；错过时启动补扫 |
| 温度 / 风扇 / 电压 | 5s（仅当启用驱动） | 同实时占用 |
| 软件清单 / 自启动项 | 每 24h | 开销大 |

**开销预算与验收**：Agent 服务进程常驻内存 **< 60 MB**、CPU 平均占用 **< 1%**（5s 采样周期下）、
无驱动时不加载任何内核组件。这条要**真机实测**（用 PDH 量 Agent 自己）。

### 6.4 存储与保留

| 层 | 存什么 | 保留 | 位置 |
|---|---|---|---|
| **热层（内存）** | 每台设备最新一份快照 | 只留最新 | 中心服务端内存字典 |
| **温层（SQLite）** | 5s 明细 `AgentMetrics` | **30 天**（可配） | 现有 `control.db` |
| **冷层（SQLite）** | 按小时/按天聚合 `AgentMetricsDaily` | **1 年** | 同上 |
| **事件层（SQLite）** | `AgentPowerEvents`（开关机/蓝屏/异常） | **永久**（数据量小） | 同上 |
| **审计层** | `AgentAuditLog`（谁对哪台机器做了什么） | 与现有审计一致 | 同上 |

> ⚠️ **容量估算必须先做**：假设 20 台设备 × 5s 采样 × 每条约 400 字节 ≈ **1.4 GB / 30 天**。
> 这对展厅级（< 50 台）可接受；**上百台就必须改用"仅事件 + 日聚合"或降采样**。
> 要在方案确认时把**设备规模上限**问清楚（决策补充项）。

### 6.5 开关机时间记录的具体来源（你的第 3 条要求）

| 记录类型 | 事件 ID | 日志 | 含义 |
|---|---|---|---|
| 开机 | **6005** | System | 事件日志服务已启动（近似开机时刻） |
| 开机时长 | **6013** | System | 本次开机已运行 N 秒 |
| 正常关机 | **6006** | System | 事件日志服务已停止（干净关机） |
| 计划内重启/关机 | **1074** | System | 由某进程/用户发起，含发起者 |
| **意外关机** | **6008** | System | 上次关机是意外的 |
| **电源异常** | **41** | System | 内核未干净关闭（断电/死机） |
| 蓝屏 | **1001** | System | BugCheck 代码与转储信息 |
| 睡眠 / 唤醒 | 42 / 107 | System | 睡眠与恢复 |

**实现要点**：
- 用 `EventLogWatcher`（**订阅式**，不轮询）监听 `System` 日志的四类 ID；
- Agent 启动时先做一次**回溯补扫**（默认回溯 7 天，去重后入库），解决"Agent 没运行时发生的开关机"；
- **原始事件上传**，**统计（在线时长、每日开关机次数、异常率）由服务端聚合** ——
  这样改统计口径不需要更新客户端（版本兼容性关键）。

---

## 7. 文件分发与文件传输

### 7.1 三种语义（都要）

| 语义 | 方向 | 场景 | 传输方式 |
|---|---|---|---|
| **仓库共享** | 中心 → 浏览器 | 任何人在网页上下载服务端仓库里的文件 | 现有静态/下载端点（**M4 原始设计**） |
| **定向下发** | 中心 → 指定客户端 | 把固件/素材推给某台或某组客户机 | Agent 主动 HTTP `GET`（Range 断点续传） |
| **回传采集** | 客户端 → 中心 | 把客户机上的日志/截图/文件收上来 | Agent 主动 HTTP `POST` 分块上传 |

### 7.2 推 vs 拉（**关键取舍**）

| | 服务端推送（服务端 → 客户端监听端口） | **Agent 拉取（推荐）** |
|---|---|---|
| 网络要求 | 客户端必须**开入站端口** + 放行防火墙 | **零入站**，出站即可 |
| NAT/跨网段 | 失败 | 成立 |
| 限速/断点续传 | 服务端实现复杂 | 客户端 `HttpClient` 原生 + `Range` |
| 服务端视角 | 要管每台的连接与重试 | 只下发"任务"，进度由 Agent 回报 |
| 结论 | ❌ | ✅ |

**推荐：控制信令走 SignalR 长连接，实际字节走 Agent 主动 HTTP 拉/推。**

```
① 管理员在网页选文件 + 选目标客户端 → POST /api/distributions
② 服务端落库任务，并通过 /hubs/agent 给目标 Agent 发 PushTransferTask
③ Agent 收到任务 → GET /api/files/{id}/download（带 Range，边下边算 SHA256）
④ 每完成 5% / 每次重试 → Agent 回报进度 → 服务端 SignalR 推给网页（进度条实时）
⑤ 下载完 → SHA256 比对 → 落地到目标目录（原子替换：先写 .tmp 再 Move）
⑥ 回报"完成/失败(原因)" → 网页可见
```

### 7.3 校验与原子性

| 环节 | 措施 |
|---|---|
| 上传到仓库 | 分块 + `Content-Range` + 全量 SHA256 |
| 存储 | 文件系统分目录 + SQLite 存元数据（名/大小/SHA256/版本/上传者/时间） |
| 下载 | `Range` 断点续传；**边下边算 SHA256**（不二次读盘） |
| 落地 | **先写 `xxx.tmp` → 校验 SHA256 → `File.Move(overwrite:true)`**；绝不直接覆盖目标文件 |
| 失败 | 保留 `.tmp` 供续传；超过 N 次重试后上报失败原因（磁盘满/权限/被杀软拦截） |
| 路径安全 | 服务端只允许"仓库内文件 id"，**不接受客户端提交的任意路径**；客户端也只允许落在**服务端指定的白名单目录**（防路径穿越） |

### 7.4 与按钮/动作系统的打通（远期，但先留位）

M4 原始设计已提到：**新增 `Protocol=File` 的动作类型**，或"在按钮上挂分发旁路动作"。
建议本期**只留接口不实现**，避免把分发耦合进按钮配置的数据模型（那会带来配置迁移）。

---

## 8. 局域网远程桌面

### 8.1 四条路线对比

| # | 路线 | 延迟 | 带宽 | 工作量 | 依赖 | 评价 |
|---|---|---|---|---|---|---|
| **R1** | **noVNC + 外部 VNC Server** | 中 | 中 | **最小** | 客户机要装 TightVNC/UltraVNC；中心做 WebSocket 代理 | 快，但**引入第三方组件**且与"自研 Agent"目标不符 |
| **R2** | **自研 MJPEG-over-WebSocket** | 中（~100-200ms） | **高**（JPEG 逐帧） | 中 | 只需 `IScreenCapturer` 实现（GDI/BitBlt 或 DXGI）+ `<img>` 渲染 | ✅ **一期推荐**：局域网带宽充足，实现最直接 |
| **R3** | **自研 H.264 + WebSocket（MSE/WebCodecs）** | 低 | **低** | 大 | 需编码器（硬件 NVENC/QSV 或 `OpenH264`）+ 浏览器 MSE | 二期升级 |
| **R4** | **WebRTC + 信令** | **最低** | 低 | **最大** | 浏览器原生，但信令/ICE/中继都要自建 | 三期，或明确不做 |

### 8.2 推荐：R2 先行，R3 二期

**为什么 MJPEG 在局域网是对的**：
- 局域网千兆 = ~100 MB/s，1080p MJPEG@15fps 约 **15-30 Mbps**，绰绰有余；
- 浏览器侧**零依赖**（一个 `<img src="data:image/jpeg;base64,...">` 或 `Blob` URL 即可），不引入 MSE/WebCodecs 兼容性问题；
- 实现路径最短：`IScreenCapturer.CaptureAsync()` → `System.Drawing` 编码 JPEG → WS 二进制帧；
- 后续上 H.264 时**只换编码与前端解码**，传输层（WS 中继 + 会话管理 + 审计）**不用返工**。

**架构**（中心服务端**只做中继，不解码、不存帧**）：

```
浏览器 ──WS──► /hubs/remote ──WS──► Agent.Tray（会话侧，执行体）
   ▲                                      │
   │  ① 浏览器请求 POST /api/remote/sessions {agentId, screenId}
   │  ② 中心生成一次性 sessionToken（60s 有效），校验 RBAC + 授权 + 审计
   │  ③ 中心把 sessionToken 通过 /hubs/agent 推给目标 Agent（服务侧→管道→会话侧）
   │  ④ 会话侧用 token 连 /hubs/remote，开始推帧
   │  ⑤ 浏览器连 /hubs/remote?session=... ，中心双向转发（帧 + 输入事件）
   └── 中心记录：谁在何时看了哪台机器多久（审计）
```

### 8.3 输入注入与多屏

| 能力 | 实现 | 注意 |
|---|---|---|
| 鼠标移动/点击/滚轮 | `IInputInjector`（`SendInput`） | 坐标要从"显示像素"映射回**虚拟屏幕坐标**（多屏 + DPI 缩放） |
| 键盘 | `IInputInjector`（`SendInput`，扫描码优于虚拟键） | 需处理 Ctrl+Alt+Del（**做不到**，Windows 安全序列不允许注入 ⇒ 需在文档里写明） |
| 文本粘贴 | 走剪贴板或 `KEYEVENTF_UNICODE` 逐字符 | 中文输入法无法注入（只能注入 Unicode 字符） |
| 多显示器 | `EnumerateScreens()` 已在接口里 | 网页提供屏幕选择；建议先只支持"整屏" |
| DPI 缩放 | 需处理 `SetProcessDpiAwareness` | 否则坐标全偏 —— 这是最容易踩的坑 |

### 8.4 安全与合规

| 措施 | 说明 |
|---|---|
| 强制认证 | 未登录不能建会话；建会话需 `AdminOnly`（决策 D12） |
| 一次性令牌 | 会话令牌 60s 有效、一次使用、绑定 agentId |
| 审计 | 记录"谁 / 哪台 / 起止时间 / 时长"；远端托盘可弹气泡"本机正在被远程查看" |
| 可全局关闭 | `Server:Remote:Enabled=false` ⇒ 端点直接 404，客户端不建连接 |
| 画面水印（可选） | 浏览器侧叠加"操作者 + 时间"半透明水印 |
| ⚠️ 合规提示 | 远程控制类能力在部分场所需**明确告知被控端**；建议默认开启"被控时托盘提示" |

---

## 9. 客户端 Agent 设计

### 9.1 工程结构（新增/改造）

```
agents/
├── ControlServer.Agent/                     # 【改造】现在是空库 → 变 Agent 共享库
│   ├── Protocols/                           #   发现报文、注册/心跳 DTO、任务模型
│   ├── Telemetry/                           #   采集器（A–H 八组，各自独立可测）
│   │   ├── CpuCollector.cs  MemoryCollector.cs  DiskCollector.cs
│   │   ├── NetworkCollector.cs  GpuCollector.cs  DisplayCollector.cs
│   │   ├── PowerEventCollector.cs           #   开关机/蓝屏（EventLogWatcher）
│   │   └── HardwareSensorCollector.cs       #   🔴 可选驱动（LibreHardwareMonitor）
│   ├── Transport/                           #   发现广播、SignalR 客户端、HTTP 文件收发
│   ├── Tasks/                               #   发送/定时/启动 三类任务执行器
│   └── AgentOptions.cs / AgentState.cs
├── ControlServer.Agent.Service/             # 【新增】Windows 服务宿主（Session 0）
│   └── ControlServer.Agent.exe
└── ControlServer.Agent.Tray/                # 【新增】用户会话托盘 + 远控执行体
    ├── ControlServer.Agent.Tray.exe
    ├── ScreenCapture/                       #   IScreenCapturer 实现（GDI / DXGI）
    ├── InputInjection/                      #   IInputInjector 实现（SendInput）
    └── Audio/                               #   音量/静音（IAudioEndpointVolume）
```

**依赖方向**（与现有铁律一致）：

```
Agent.Service ─┐
               ├─► ControlServer.Agent(lib) ─► Contracts + Core + Platform.Abstractions
Agent.Tray ────┘                             └► Platform.Windows（托盘/截屏/注入）
```

> ⚠️ **`ControlServer.Agent` 库绝不能引用 `ControlServer.Server`**
> —— 那会把 ASP.NET Core + EF Core 打进客户端安装包（体积翻倍），
> 也违反了"客户端与服务端是两个独立部署物"的边界。

### 9.2 服务侧职责（`ControlServer.Agent.exe`）

| 能力 | 说明 |
|---|---|
| 发现与注册 | UDP 广播 + HTTP 注册 + 凭据落盘（DPAPI） |
| 长连接维护 | SignalR 客户端，断线指数退避重连（1s→2s→…→60s 封顶） |
| 遥测采集与上报 | A–H 组；按频率分组节流 |
| **文件收发** | 拉取任务、下载（Range）、上传（分块）、SHA256 校验、原子落地 |
| **UDP 指令通道** | 复用 `PairingListenerService`，**加 HMAC 签名 + 来源白名单**（决策 D10） |
| 远程关机/重启 | `shutdown /s|/r`（服务是 SYSTEM，有权限） |
| 三类任务执行 | 延时发送 / 循环发送 / 定时发送（复用 `NetworkService` + `TimerSchedulerService`） |
| **开关机事件订阅** | `EventLogWatcher` + 启动回溯补扫 |
| 本地日志 | Serilog 滚动文件，路径 `%ProgramData%\ControlServer.Agent\logs\` |
| 管道服务端 | 命名管道 `\\.\pipe\ControlServer.Agent`，供同机托盘连接 |
| 自更新（可选，远期） | 服务端下发新版本 → Agent 下载 → 静默替换 → 重启服务 |

### 9.3 会话侧职责（`ControlServer.Agent.Tray.exe`）

| 能力 | 说明 |
|---|---|
| **托盘图标 + 右键菜单** | 复用 `WindowsTrayIconHost`；菜单见 §9.7 |
| **屏幕捕获** | `IScreenCapturer` 实现；仅在远控会话建立时才启动（**平时不截屏**） |
| **输入注入** | `IInputInjector` 实现；同上 |
| **音量 +/- / 静音** | `IAudioEndpointVolume`（比 `keybd_event` 可靠，且状态可读） |
| **延时启动程序** | `Process.Start(UseShellExecute:true)`（必须在会话侧才能弹窗口） |
| 打开自己的详情页 | 打开 `http://<中心服务>/#/agents/<agentId>` |
| 单实例 | `Local\ControlServer.Agent.Tray.SingleInstance` |
| 日志 | 写 `%TEMP%`（普通用户无数据目录写权限，与 `TrayAgent` 同） |

> ⚠️ **平时不截屏**：只在"远控会话已建立"时启动采集循环，会话结束立即停。
> 这既是性能要求，也是**合规要求**（不能有"偷偷在录屏"的观感）。

### 9.4 两个进程怎么通信

| # | 通道 | 优点 | 缺点 | 结论 |
|---|---|---|---|---|
| **C1** | **命名管道**（`\\.\pipe\...`，ACL 限本机） | 快、ACL 可限、无需端口 | 要自定义协议（可用 `System.IO.Pipelines` + JSON 行） | ✅ **推荐** |
| **C2** | 本地 HTTP（`127.0.0.1:<port>`） | 可复用 ASP.NET/Kestrel | 要引入 Web 栈（体积）；端口冲突；本机任意进程可访问 | ❌ |
| **C3** | 共享内存 / 文件轮询 | 简单 | 脆弱、无背压 | ❌ |

**C1 协议**（极简，一行一个 JSON）：

```
服务侧 → 会话侧：  {"t":"remote-start","session":"...","token":"..."}
                   {"t":"remote-stop","session":"..."}
                   {"t":"launch","path":"...","args":"..."}
                   {"t":"query","id":12,"what":"screens"}
会话侧 → 服务侧：  {"t":"reply","id":12,"data":{...}}
                   {"t":"log","level":"warn","msg":"..."}
                   {"t":"remote-ended","session":"...","reason":"user"}
```

> 用**行分隔 JSON**而不是二进制协议：排障时能直接 `type` 管道调试；
> 带宽需求极低（只有信令，画面走 WS 直连中心）。

### 9.5 配置事实来源（**决策 D5**）

| 方案 | 说明 | 评价 |
|---|---|---|
| 服务端下发为准 | 客户端的监听端口、发送/定时/启动任务、目标地址全部由服务端存 | ✅ **推荐**：中控场景本就该集中管；"在网页改一次，全部机器生效" |
| 本地为准 | 沿用 BootAssistant 的本地 `config.json` | ❌ 要一台台去改，与"中控"背道而驰 |
| 下发 + 本地可覆盖 | 折中 | 🟡 会产生"网页显示的和实际跑的不一致"这类最难查的问题 |

**推荐纯下发**，具体机制：

```
服务端 SQLite = 唯一事实来源（AgentConfigs 表）
   ↓ 配置变更时
   ↓ 通过 /hubs/agent 推 ConfigChanged（带版本号）
   ↓
Agent 收到 → 拉全量 → 落本地缓存（只读缓存，供服务端不可达时降级运行）
   ↓
应用成功 → 回报 AppliedConfigVersion
```

**降级运行**（中心服务端不可达时）：继续执行**本地缓存里的最后一版配置**，
但**不接收新任务**；重新连上后自动对齐版本。这条要写清楚，避免"中心挂了客户机也停摆"。

### 9.6 安装 / 卸载 / 升级 / 静默部署

| 项 | 做法 |
|---|---|
| 安装 | Inno Setup（复用现有 `ControlServer.iss` 套路）：装 2 个 exe 到 `%ProgramFiles%\ControlServer Agent\`，注册服务（`sc create` + `sc sdset` 给普通用户停/启权限），写 `HKCU\Run` 拉起托盘，放行防火墙（可选），首次启动即注册 |
| 静默部署 | `ControlServer-Agent-Setup-x.y.z.exe /VERYSILENT /TOKEN=<令牌> /SERVER=<地址>`；可配合组策略/批处理批量推 |
| 卸载 | `uninstall-agent.ps1`：停服务 → 删服务 → 删 `HKCU\Run` → 卸程序；**保留数据目录**（与中心服务端同一纪律） |
| 升级 | ① 手动：重跑新安装包（就地覆盖，服务自动重启）；② 远期：服务端下发新版本 + Agent 自更新（需签名校验） |
| 数据目录 | `%ProgramData%\ControlServer.Agent\`（`config/`、`logs/`、`cache/`）—— 经 `IDataDirectory`，不硬编码 |

### 9.7 托盘菜单建议

```
中控客户端 · 在线 / 离线
────────────────────
打开本机详情页          （默认项；浏览器打开中心服务端该机器的页面）
本机状态                （灰显信息行：CPU 42% · 内存 61% · 已连接）
────────────────────
重新连接                （服务在跑但连不上中心时可用）
查看日志                （打开 %ProgramData%\ControlServer.Agent\logs）
────────────────────
停止 Agent 服务         （走 SCM，需要安装期 sc sdset 授权）
退出托盘助手
```

⚠️ **"被远程查看中"必须显眼**：远控会话激活时，把托盘提示文本与图标状态改为
"⚠ 本机正在被远程查看"，并弹一次气泡。这是合规要求，不是可选项。

---

## 10. 中心服务端改造

### 10.1 新增端点

```
# 发现与在线表
GET    /api/agents                      # 在线/离线客户机列表（含最新遥测摘要）
GET    /api/agents/{id}                 # 单台详情（硬件清单 + 配置 + 状态）
PUT    /api/agents/{id}                 # 改名 / 分组 / 备注            [AdminOnly]
DELETE /api/agents/{id}                 # 移除记录（不等于卸载）        [AdminOnly]
POST   /api/agents/{id}/revoke          # 吊销该机 agentKey             [AdminOnly]
POST   /api/agents/{id}/reboot          # 远程重启 Agent 服务           [AdminOnly]
GET    /api/agents/{id}/metrics?from=&to=&step=   # 遥测时序（明细/聚合/日）
GET    /api/agents/{id}/power-events    # 开关机/蓝屏记录

# 准入
# ⛔ 以下三个端点已于 2026-10-13 整体删除（免凭据准入，见 §5.4 顶部说明）；保留存档：
# GET    /api/agents/enrollment           # 查看令牌状态                  [AdminOnly]
# POST   /api/agents/enrollment           # 生成/轮换安装包令牌           [AdminOnly]
# POST   /api/agents/join-codes           # 生成一次性加入码（2h）        [AdminOnly]

# Agent 专用（Agent 身份，非用户会话）
POST   /api/agents/register             # 免凭据注册（只校验 machineKey）[Anonymous]
GET    /api/agents/{id}/config          # 拉配置                        [AgentKey]
GET    /api/downloads/agent-installer   # 下载客户端安装包              [登录用户]

# 文件（M4）
GET    /api/files                       # 仓库列表
POST   /api/files/upload                # 分块上传
GET    /api/files/{id}/download         # 下载（支持 Range）
DELETE /api/files/{id}
POST   /api/distributions               # 新建分发任务（目标客户端/组）  [AdminOnly]
GET    /api/distributions/{id}          # 进度与结果

# 远程桌面（M5）
POST   /api/remote/sessions             # 创建会话 → sessionToken       [AdminOnly]
DELETE /api/remote/sessions/{id}        # 结束会话
GET    /api/remote/sessions             # 活动会话列表（审计用）

# 实时
WS     /hubs/agent                      # ★ Agent 专用（agentKey 认证）
WS     /hubs/remote                     # ★ 远程桌面中继（一次性令牌）
WS     /hubs/events                     #   已有，新增若干事件
```

### 10.2 新增数据表

| 表 | 关键字段 | 说明 |
|---|---|---|
| `AgentHosts` | `Id` / `MachineKey`(唯一) / `DisplayName` / `Group` / `Note` / `AgentKeyHash` / `AgentVersion` / `LastSeenAt` / `RegisteredAt` / `State` / `ConfigVersion` / `AppliedConfigVersion` / `LastIp` | 在线表 + 注册信息 |
| `AgentMetrics` | `AgentId` / `At` / `CpuPct` / `MemUsedMb` / `MemTotalMb` / `DiskReadBps` / `DiskWriteBps` / `NetUpBps` / `NetDownBps` / `GpuPct` / `TempsJson`(仅启用驱动时有值) | 5s 明细 |
| `AgentMetricsDaily` | `AgentId` / `Day` / 平均 / 峰值 / 在线时长 | 日聚合（1 年） |
| `AgentPowerEvents` | `AgentId` / `At` / `Kind`(Boot/Shutdown/Unexpected/Reboot/Bsod/Sleep/Wake) / `EventId` / `Actor`(1074 的发起者) / `Detail` | 开关机记录（永久） |
| `AgentHardware` | `AgentId` / `CapturedAt` / `Json` | 硬件清单快照（6h 一次，只留最新） |
| `AgentConfigs` | `AgentId` / `Version` / `Json` | 下发给该机的配置（含三类任务） |
| `EnrollmentTokens` | `Id` / `TokenHash` / `CreatedAt` / `ExpiresAt` / `RevokedAt` / `UsedBy` | 安装包令牌 / 加入码 —— ⚠️ **表结构保留、但 2026-10-13 起已无任何读写方**（删实体就得补一个 `DROP TABLE` 破坏性迁移，不值当） |
| `DistributionTasks` | `Id` / `FileId` / `Targets` / `State` / `CreatedBy` / `CreatedAt` | 分发任务 |
| `DistributionItems` | `TaskId` / `AgentId` / `State` / `Progress` / `Bytes` / `Sha256` / `Error` | 每目标一行，进度 |
| `FileRepoItems` | `Id` / `Name` / `Size` / `Sha256` / `Kind`(Normal/Installer) / `UploadedBy` / `UploadedAt` | 文件仓库 |
| `RemoteSessions` | `Id` / `AgentId` / `StartedBy` / `StartedAt` / `EndedAt` / `Reason` | 远控审计 |

> ⚠️ 迁移走既有流程（`dotnet-ef` + `AppDbContextDesignFactory`，见 REF §9），
> **不要**用 Server 当启动项目生成迁移。

### 10.3 新增 SignalR 事件（`EventNames`）

| 事件 | 方向 | 用途 |
|---|---|---|
| `AgentOnline` / `AgentOffline` | 服务端 → 网页（`/hubs/events`） | 在线表变化 |
| `AgentTelemetryUpdated` | 服务端 → 网页 | 遥测刷新（**服务端节流到 2s 一次**，避免前端被刷爆） |
| `AgentConfigChanged` | 服务端 → Agent（`/hubs/agent`） | 下发配置 |
| `TransferProgress` | 服务端 → 网页 | 分发进度 |
| `RemoteSessionChanged` | 服务端 → 网页 | 远控会话启停 |

### 10.4 权限落点（决策 D12）

| 能力 | 最低角色 | 理由 |
|---|---|---|
| 看客户端列表 / 遥测看板 / 开关机记录 | `CanOperate`（Admin + Operator） | 运维日常 |
| 改名/分组/备注 | `AdminOnly` | 改的是资产台账 |
| 生成令牌 / 吊销 / 移除记录 / 远程重启 | `AdminOnly` | 影响设备准入 |
| 文件仓库上传/下载 | 上传 `AdminOnly`，下载 `CanOperate` | 上传等于能往所有客户机推东西 |
| 发起远程桌面 | `AdminOnly` | 能看到别人屏幕，权限必须最高 |

### 10.5 客户端安装包托管（决策 D11）

**约束**：绿色版发布目录必须**恰好 1 个文件**（`build-portable-exe.ps1` 有校验，是资产），
所以客户端安装包**不能**放进 `dist\win-x64\`。

| 方案 | 说明 | 评价 |
|---|---|---|
| A. `{DataDir}\downloads\` | 管理员手工放安装包进去 | 简单，但"放哪"要靠文档说 |
| **B. 文件仓库（`FileRepoItems.Kind=Installer`）** | 管理员在网页上传一次，右上角直接下 | ✅ **推荐**：复用 M4 存储、有版本与校验、可替换 |
| C. 编译进 exe | 51.7 MB 会涨到 120+ MB | ❌ 破坏单文件承诺的体积预期 |

**推荐 B + A 作为种子**：安装中心服务端时，把与它配套的客户端安装包放进
`{DataDir}\downloads\`；服务端首次启动时若仓库为空，自动把它"收编"为一个 `Kind=Installer` 条目。
**且必须做版本校验**：网页上下载的是 `AgentVersion`，若 `AgentVersion ≠ ServerVersion`（主次版本不一致）
就在按钮旁给一个黄点提示"客户端安装包与当前服务端版本不一致，建议重新上传"。

---

## 11. 前端改造

### 11.1 右上角下载入口（**你明确要求的那一处**）

位置：`web/src/App.vue` 的 `.topbar-right` 区块（当前 **761–781 行**），
插在"授权徽章"与"用户名"之间：

```html
<!-- 客户端安装包：现场工程师第一次接触本系统最需要的入口 -->
<button
  class="sm"
  :title="installerHint"
  :disabled="!installer.available || installer.downloading"
  @click="downloadInstaller"
>
  {{ installer.downloading ? '下载中…' : '客户端安装包' }}
</button>
<span v-if="installer.versionMismatch" class="badge warn" title="与服务端版本不一致">!</span>
```

| 行为 | 说明 |
|---|---|
| 数据来源 | `GET /api/downloads/agent-installer` 的 HEAD/元信息（另加一个 `GET /api/downloads/agent-installer/info`） |
| 未上传时 | 按钮置灰 + tooltip"管理员尚未上传客户端安装包" |
| 点击 | 触发浏览器下载（`Content-Disposition: attachment; filename=...`），大文件走 **Range** ⇒ 支持断点续传与下载器接管 |
| 版本不一致 | 黄点 + tooltip |

### 11.2 「客户端」页签（新增）

在现有页签体系（`activeTab`）里加一个 `agents`：

```
┌─ 客户端 ──────────────────────────────────────────────────────────────────┐
│ [全部 12] [在线 9] [离线 2] [不稳定 1]        🔍搜索  分组:全部▾  [刷新]     │
├───────────────────────────────────────────────────────────────────────────┤
│ ● Lobby-PC-01      展厅A / 前台      CPU 42% 内存 61% 磁盘 3%  ▲1.2M ▼3.4M │
│   Win11 22H2 · 16GB · i5-12400 · Agent 0.1.0 · 在线 2h13m      [详情][远控] │
│ ● Lobby-PC-02      展厅A / 后台      CPU 8%  内存 33% 磁盘 0%  ▲20K ▼80K  │
│   Win10 21H2 · 8GB  · i3-9100  · Agent 0.1.0 · 在线 5h01m      [详情][远控] │
│ ○ Hall-PC-07       展厅B / 侧屏      最后在线 3 天前                        │
│   Win10 1909 · Agent 0.1.0 · **已离线**                        [详情][分发] │
└───────────────────────────────────────────────────────────────────────────┘
                                  [生成安装包令牌]  [生成加入码]  [批量分发]
```

### 11.3 单台详情页 `/#/agents/{id}`

| 区块 | 内容 |
|---|---|
| **概览** | 名称（可改）、分组、备注、状态、IP、Agent 版本、最后心跳 |
| **实时** | CPU / 内存 / 磁盘 / 网络 / GPU 的实时数值 + 折线（2s 刷新） |
| **温度** | 温度 / 风扇 / 电压 —— 未启用驱动时整块显示"⚠ 未启用（需安装时勾选硬件监控驱动）" |
| **硬件清单** | CPU / 主板 / 内存条 / 磁盘 / 显卡 / 显示器 |
| **开关机记录** | 时间线：开机 / 关机 / 意外断电 / 蓝屏 / 睡眠；含"每日在线时长"柱状图 |
| **任务配置** | 延时发送 / 定时发送 / 延时启动 三条列表（**布局参考现有编辑页规范**） |
| **文件** | 已分发到该机的文件 + 手动分发入口 |
| **操作** | 远程桌面 / 重新下发配置 / 重启 Agent / 吊销 / 移除 |

### 11.4 复用现有组件规范

- 列表/表格/徽章/抽屉沿用现有 `style.css` 与 `StatusCards`/`OpsLogPanel` 的写法；
- 实时刷新复用现有 SignalR 客户端（`App.vue` 里已有 `signalR.HubConnectionBuilder`）；
- 图表：**当前项目无图表库**。建议引入 **Chart.js**（轻量、按需引入、无框架绑定），
  或先用纯 CSS 折线（数据点少时够用）。⚠️ **这是新增依赖，需要你确认**。

---

## 12. 安全设计汇总

| 面 | 措施 |
|---|---|
| 准入 | ⛔ **已改为免凭据（2026-10-13）**：装完客户端即自动注册，**不设任何门槛**；原 `enrollmentToken` / 一次性加入码已整体移除。**兜底只剩在线表容量上限（`Server:Agent:MaxHosts`，防撑爆、非准入）与管理员手工吊销 / 移除（事后处置）** ⇒ **中心端口绝不该暴露到不可信网络** |
| 身份 | 每机唯一 `agentKey`（随机 32B），DPAPI 落盘；服务端只存 hash |
| 传输 | 生产环境要求 HTTPS/WSS；HTTP 模式在网页显著告警（沿用现有策略） |
| 发现广播 | 报文**不含任何秘密**，仅用于定位 |
| 指令通道（UDP） | ⚠️ **必须加 HMAC 签名 + 来源 IP 白名单**（参考项目的无鉴权版本**不照抄**） |
| 远程桌面 | 一次性令牌 + `AdminOnly` + 全程审计 + 被控端托盘提示 + 可全局关闭 |
| 文件分发 | SHA256 全链路 + 路径穿越防护 + 目标目录白名单 + 大小/类型限制 |
| 客户端提权 | 服务侧以 SYSTEM 运行（必需）；托盘侧**普通权限**运行（与 `TrayAgent` 同） |
| 吊销 | 单机吊销 / 令牌吊销 / 令牌轮换；吊销后 Agent 连接被拒并托盘提示 |
| 审计 | 所有管理动作（改名/吊销/分发/远控）写现有审计日志 |
| 隐私 | 只在建立远控会话时截屏；遥测只上报指标，**不上报文件内容/键盘记录** |

> ⚠️ **必须在文档里写明的两条边界**：
> ① 客户端能看到目标机器的**屏幕内容**（远控时）与**硬件/软件清单** —— 这属于强管控能力，
>    在部署前必须取得设备所有者同意（展厅设备通常是自有的，风险低）。
> ② **不做键盘记录、不做文件内容扫描、不做摄像头/麦克风访问** —— 明确划出红线。

---

## 13. 跨平台

| | Windows（本期） | Linux（接口预留） |
|---|---|---|
| 服务托管 | Windows Service（`UseWindowsService`） | systemd（`UseSystemd`）—— **同一套 `Program.cs`** |
| 托盘 | `WindowsTrayIconHost` | ❌ 无桌面 ⇒ `NoopTrayIconHost`（已有） |
| 屏幕捕获 | GDI `BitBlt` / DXGI | X11 `XShm` / Wayland PipeWire |
| 输入注入 | `SendInput` | XTest / uinput |
| 遥测 | WMI + PDH + 性能计数器 | `/proc` + `/sys` + `lm-sensors`（温度反而**更容易**！） |
| 开关机记录 | Windows 事件日志 | `journalctl` / `wtmp` |
| 音量 | `IAudioEndpointVolume` | PulseAudio/PipeWire API |

**建议：本期只做 Windows**（展厅/机房的受控电脑几乎全是 Windows），
但**所有采集器必须走 `IReadOnlyCollection<IMetricCollector>` 抽象**，
Linux 实现后期补齐时不改协议、不改服务端、不改前端。

---

## 14. 里程碑拆分与实施顺序

| 阶段 | 目标 | 关键交付 | 验收标准 |
|---|---|---|---|
| **M4.1 骨架 + 发现** | 网页能"自己看到"客户机 | Agent 库 + 服务侧 + 托盘侧 + 广播发现 + 注册/心跳 + 在线表 + 安装包下载入口 + 前端"客户端"页（只列在线/离线） | 客户机装完 **≤60s** 自动出现；关机 **≤90s** 变离线；顶栏能下到安装包 |
| **M4.2 遥测** | 看板跑起来 | A–D + F + H 组采集器 + 时序库 + 前端实时/历史/开关机页 | CPU/内存/磁盘/网络/GPU 与任务管理器**偏差 ≤5%**；开关机记录与事件查看器**逐条一致** |
| **M4.3 温度（可选驱动）** | E 组 | LibreHardwareMonitor 集成 + 安装勾选项 + 降级展示 + 未启用文案 | 勾选时拿到 CPU/GPU/硬盘温度；未勾选/驱动失败时显示"未启用"且**不报错不刷日志** |
| **M4.4 文件分发** | 仓库 + 下发 + 回传 | `/api/files*` + `/api/distributions` + Agent 收发 + 进度 + SHA256 | **>2GB 文件断点续传成功**；拔网线 30s 后恢复续传；SHA256 全链路一致 |
| **M4.5 BootAssistant 对齐** | 25 项功能闭环 | 三类任务下发 + UDP 指令通道（**加固版**）+ 音量/静音/启动程序（会话侧）+ 配置中心化 | 逐条对照 §2.2 表；**无鉴权的 UDP 关机绝不上线** |
| **M5.1 远程桌面（MJPEG）** | 浏览器能看能控 | `IScreenCapturer`/`IInputInjector` 实现 + `/hubs/remote` + 会话审计 + 托盘提示 | 1080p **≥15fps**；鼠标键盘可用（Ctrl+Alt+Del 除外，文档写明）；审计可查 |
| **M5.2 远程桌面（H.264）** | 提质 | 硬件编码 + WebCodecs/MSE | 带宽降到 MJPEG 的 1/5；延迟 ≤80ms（**可选，看是否值得**） |
| **M6 加固交付** | 可交付 | 安全审计 + 压测（20/50/100 台）+ 安装包 + 运维文档 + 备份恢复演练 | 通过 checklist；一线人员按文档能独立部署 |

> **建议节奏**：**M4.1 是价值确认点** —— 它一做完你就能看到"装完就自己出现"。
> 如果发现交互或命名不对，此时返工成本最低。**强烈建议 M4.1 做完先验一次再往下走。**

---

## 15. 待拍板决策清单（**请逐条回**）

| # | 决策点 | 我的推荐 | 备选 | 影响 |
|---|---|---|---|---|
| **D1** | 客户端平台范围 | **仅 Windows 先行**，抽象层预留 Linux | 同步做 Linux | 决定工作量（Linux 约 +40%） |
| **D2** | 命名 | 工程用「客户端 Agent」，界面可叫「客户端/客户机」 | 界面保留"客户服务端" | 纯文案，随时可改 |
| **D3** | 准入方式 | **E1 安装包内置令牌 + E2 加入码兜底** | 仅 E1 / 仅 E2 | 决定装机体验与批量能力 |
| **D4** | 服务端地址来源 | **UDP 广播自动发现** + 安装参数兜底 | 只用手工填写 | 决定"装完即用"能否成立 |
| **D5** | 配置事实来源 | **服务端下发为准**，本地只做降级缓存 | 本地为准 / 双源覆盖 | 决定数据模型与编辑页位置 |
| **D6** | 温度采集 | **可选内核驱动**（LibreHardwareMonitor）。⚠️ 2026-10-14 起安装时**默认勾选**（需求①）⇒ 装完即启用；未启用时显示"未启用"+**具体原因**，绝不显示 0 | 完全不做温度 / 强制装驱动（无法关闭） | **本方案最大不确定项**（仍待真机验 Session 0 下的驱动加载） |
| **D7** | 上报频率 / 保留 | 实时 **5s**；明细 **30 天**，日聚合 **1 年**，事件 **永久** | 1s / 更长保留 | 决定磁盘占用 |
| **D8** | 远程桌面路线 | **MJPEG-over-WS 先行**，二期 H.264 | noVNC 先行 / 直接 WebRTC | 决定工作量与体验 |
| **D9** | 文件方向 | **双向**（下发 + 回传） | 只下发 | 回传是"收现场日志"的关键 |
| **D10** | UDP 指令通道 | ~~保留 + HMAC 签名 + IP 白名单~~ ⇒ **保留 + 纯明文 + 来源 IP 黑名单**（**2026-10-13 改，见 §27**） | 不要该通道（改用网页下发） | 兼容旧版习惯 vs 安全；**最终取"兼容优先"，边界交给防火墙** |
| **D11** | 安装包托管 | **文件仓库（M4 存储）** + 首启从 `downloads\` 种子 | 只放数据目录 / 编进 exe | 决定右上角按钮怎么工作 |
| **D12** | 客户端页权限 | 看板 `CanOperate`；装机/改名/**远控**/分发 = `AdminOnly` | 全部 `AdminOnly` | 决定一线可用性 |
| **D13** | 远程重启/卸载 Agent | 做"重启服务"；卸载只给指引 | 全做 / 都不做 | 现场排障便利性 |
| **D14** | 设备规模上限 | 需你明确（影响 §6.4 存储设计与压测目标） | 20 / 50 / 100+ | **必须回答**（我不知道数字） |
| **D15** | 图表库 | 引入 **Chart.js**（轻量、按需） | 纯 CSS 折线 / ECharts | 新增前端依赖，需你确认 |

---

## 16. 风险登记表

| 风险 | 等级 | 对策 |
|---|---|---|
| **温度/风扇拿不到**（需内核驱动，杀软可能拦） | **高** | 做成可选 + 降级显示"未启用"；本期验收不把温度计入必达项（决策 D6） |
| Session 0 隔离导致"单进程实现"不可行 | 高 | 双进程架构（§2.1），复用已跑通的 `TrayAgent` 模式 |
| 遥测明细把磁盘写爆（20 台 × 5s ≈ 1.4GB/30 天） | 中 | 分层保留 + 日聚合；**先确认设备规模**（D14） |
| 远程桌面带宽/延迟不达预期 | 中 | MJPEG 先行验证；不达预期再上 H.264（传输层不返工） |
| 客户端安装包泄露 ⇒ 可被注册 | 中 | 令牌可吊销/轮换；必要时切加入码模式 |
| 多网卡/跨网段导致广播发现失败 | 中 | 安装参数兜底 + 网页登记固定地址 |
| 客户端防火墙拦截出站 | 低 | 安装脚本放行；全出站设计已最大化兼容 |
| Agent 崩溃/被用户杀掉 | 中 | 服务配"失败自动重启"（`sc failure`）；托盘被杀不影响服务 |
| Linux 后期补齐时协议要改 | 低 | 采集器全走抽象接口，协议与平台无关 |
| 引入第三方库（LibreHardwareMonitor / Chart.js）的许可与体积 | 低 | MPL-2.0 / MIT，均可商用；体积影响需实测 |
| 前端新增页与现有页签体系冲突 | 低 | 沿用现有 `activeTab` 机制，不引路由（现在也没用 vue-router） |

---

## 17. 明确不做（本轮）

| 不做 | 原因 |
|---|---|
| 客户端主动扫描"同局域网其他客户机" | 无业务价值，且是横向移动风险面 |
| 键盘记录 / 文件内容扫描 / 摄像麦克风访问 | **红线**，与需求无关 |
| 客户端自更新（自动升级） | 有代码签名与回滚复杂度，放 M6 之后 |
| Linux 客户端实现 | 本期只留接口（D1） |
| 互联网穿透（NAT 打洞 / 云中继） | 需求明确"仅限局域网" |
| 移动端 App | 浏览器已覆盖 |
| 客户机之间的 P2P 文件互传 | 收益低、风险高 |
| 把 BootAssistant 作为库引用 | **违规**（`项目边界与术语.md` §3） |

---

## 18. 影响面：预计要动的文件

| 类别 | 文件/目录 | 动作 |
|---|---|---|
| **新增工程** | `agents/ControlServer.Agent.Service/`、`agents/ControlServer.Agent.Tray/` | 新建 |
| **改造工程** | `agents/ControlServer.Agent/` | 空库 → 共享库（协议/采集器/任务） |
| **服务端** | `Server/Api/AgentsController.cs`、`FilesController.cs`、`DistributionsController.cs`、`RemoteController.cs` | 新建 |
| | `Server/Hubs/AgentHub.cs`、`RemoteHub.cs` | 新建 |
| | `Server/Services/AgentHostRegistry.cs`（内存在线表）、`AgentEnrollmentService.cs`、`AgentConfigService.cs` | 新建 |
| | `Server/HostedServices/DiscoveryListenerHostedService.cs`（UDP 9998）、`AgentMetricsRetentionHostedService.cs` | 新建 |
| | `Server/Security/AgentKeyAuthenticationHandler.cs` | 新建 |
| | `Infrastructure/` 的 `AppDbContext` + 迁移 | 加 11 张表 |
| **契约** | `Contracts/AgentDtos.cs`、`FileDtos.cs`、`RemoteDtos.cs`；`Constants.cs` 加事件名与 Policy | 新建/追加 |
| **Core** | `Core/Abstractions/IAgentHostStore.cs`、`IFileRepository.cs` 等 | 新建（端口接口） |
| **平台层** | `Platform.Windows/ScreenCapture/GdiScreenCapturer.cs`、`InputInjection/SendInputInjector.cs` | 新建（**保持 `net8.0`**，P/Invoke + 运行时守卫） |
| **前端** | `web/src/App.vue`（顶栏 + 新页签）、`components/AgentsPanel.vue`、`AgentDetailPanel.vue`、`TelemetryChart.vue`、`DistributionPanel.vue`、`RemoteDesktopPanel.vue`、`types.ts`、`api.ts` | 新建/修改 |
| **部署** | `deploy/windows/build-agent.ps1`、`build-agent-installer.ps1`、`ControlServer.Agent.iss`、`install-agent.ps1`、`uninstall-agent.ps1` | 新建 |
| | `deploy/smoke/agent-smoke.py`、`run-all.py` | 新建/追加 |
| **验证夹具** | `.tools/agent-uia-check/`（托盘自动化）、`.tools/agent-repro/`（发现/遥测/分发/远控回归） | 新建 |
| **文档** | 本文 + `授权操作手册.md`（加"客户端授权"节）+ `REF-M4-Agent.md` + `MEMORY.md` | 新建/追加 |

---

## 19. 下一步

1. **你逐条回 §15 的 D1–D15**（尤其 **D6 温度**与 **D14 设备规模**，这两条直接影响其他设计）。
2. 你确认后，我把本方案收敛成 **`M4.1 骨架 + 发现` 的实施细则**（含具体类名、端点签名、报文格式、
   验收脚本用例清单），**仍然不改代码**。
3. M4.1 细则确认后，再按"先出方案 → 确认 → 再实施"的节奏开工。

> **§1–§19 是设计方案**，成稿时"未修改任何代码、未新增任何工程、未改动任何配置"。
> 所有"复用"判断均基于对现有源码的实读（`ITrayIconHost` 441 行 / `TrayAgent` 295 行 /
> `NativeMethods` 366 行 / `PairingListenerService` 169 行等），不是推测。
> ⚠️ **§20 起已是实施记录**（M4.1 骨架 + 发现已落地）—— 与 §1–§19 若有出入，**以 §20 为准**。

---

## 20. 实施记录：M4.1「骨架 + 发现」**已落地**（2026-10-11）

> §19 的"先出方案 → 确认 → 再实施"已经走完前两步。本节的 §20 是**实际做出来的东西**，
> 与 §1–§19 的设计若有出入，**以本节为准**。
> §15 的 D1–D15 尚未逐条答复，本次按**方案默认值**推进（下表列出实际生效的值，便于回头推翻）。

### 20.1 实际落地范围

| 层 | 产出 |
|---|---|
| **契约** | `Contracts/AgentDtos.cs`（`AgentStates` 五态常量 / `AgentHostDto` / `AgentListDto` / `AgentMutationResult` / `AgentDetailDto` / `AgentInstallerInfoDto` / `AgentRegisterResult` / `AgentWelcomeDto` / `AgentHeartbeatAckDto` / `AgentCommandDto` + `AgentCommandNames`）；`Constants.cs` 加 `Routes.AgentHub`、`AgentAuthentication`（独立认证 scheme）、`EventNames.AgentOnline/AgentOffline/AgentChanged`。（⚠️ 原 `AgentEnrollmentTokenDto` / `AgentEnrollmentDto` / `CreateEnrollmentRequest` 已于 2026-10-13 删除） |
| **Core** | `Services/AgentDiscoveryProtocol.cs` + `Services/AgentPresence.cs`（**都是纯函数**，因此可以脱离服务器单测）、`Models/AgentHost.cs`、`Abstractions/IAgentHostStore.cs` |
| **Infrastructure** | `Persistence/Entities/AgentHostEntity.cs`、`Persistence/EfAgentHostStore.cs`、迁移 `20261011161356_M4_AgentHosts`（新增 `agent_hosts` / `enrollment_tokens` 两张表）。⚠️ **`EfEnrollmentTokenStore.cs` 已于 2026-10-13 删除**；`EnrollmentTokenEntity` ＋ `AppDbContext.EnrollmentTokens` 映射**刻意保留**（删了要补破坏性迁移），但已无任何读写方 |
| **Server** | `Api/AgentsController.cs`（⚠️ 2026-10-13 起 **7 个端点**：原 10 个中的 3 个令牌端点已删）、`Api/DownloadsController.cs`（2 个）、`Hubs/AgentHub.cs`、`Services/AgentHostRegistry.cs`（内存快照）、`Services/AgentEnrollmentService.cs`（免凭据注册）、`Services/AgentInstallerLocator.cs`（+ `ServerRuntimeInfo`）、`HostedServices/AgentPresenceHostedService.cs`、`Security/AgentKeyAuthenticationHandler.cs`；`ServerOptions` 加 `Discovery` / `Agent` 两节（⚠️ `Agent:JoinCodeLifetimeMinutes` 已删） |
| **Platform** | `IHostHardwareInfo` / `BasicHostHardwareInfo`（Windows 实现 `WindowsHardwareInfo`）、`DpapiSecretProtector`（`LocalMachine` 作用域） |
| **Agent 库** | `AgentOptions`、`AgentIdentityStore`（凭据 + `AgentStatusFile`）、`AgentRuntime`（`AgentVersion`）、`HostInfoCollector`、`AgentDataDirectory`、`Transport/DiscoveryClient.cs`、`Transport/AgentConnection.cs`（含 `AgentRegistrar`） |
| **两个宿主** | `agents/ControlServer.Agent.Service`（`Exe`，控制台子系统）、`agents/ControlServer.Agent.Tray`（`WinExe`）；**均已加入 `ControlServer.sln`**，归到 `agents` 解决方案文件夹 |
| **前端** | `App.vue` 顶栏右上角「客户端安装包」按钮（带版本不一致小黄点）+ 「客户端」页签；新增 `components/AgentsPanel.vue`、`components/AgentDetailPanel.vue`；`types.ts` / `api.ts` 加 M4 契约与调用 |
| **部署** | 新增 `build-agent.ps1`、`build-agent-installer.ps1`、`ControlServer.Agent.iss`、`install-agent.ps1`、`uninstall-agent.ps1`；`deploy/smoke/agent-smoke.py` + `run-all.py` 接入 `agent` 场景 |
| **测试** | `tests/ControlServer.Core.Tests/AgentProtocolAndPresenceTests.cs`（54 例）；`tests/ControlServer.Server.IntegrationTests/AgentsApiTests.cs`（14 例）；`MigrationTests` / `ControlServerFactory` / 两个通知器替身同步更新 |
| **记忆/文档** | 新增 `.workbuddy/memory/REF-M4-Agent.md`；`MEMORY.md` 加 §5 并瘦身 |

### 20.2 实际生效的默认值（= §15 决策的"未答复取值"）

| 项 | 生效值 | 备注 |
|---|---|---|
| 发现端口 | **9998** | 刻意避开对接码的 9999 |
| 心跳间隔 | **10 s** | 由服务端在客户端连上时下发（客户端可被远程调整） |
| `StaleAfter` | **30 s** | 超过即黄点「不稳定」 |
| `OfflineAfter` | **75 s** | 最坏 10+75=**85 s** ≤ 90 s，满足"关机 ≤90 秒变离线" |
| 在线表上限 | **500** | |
| 加入码有效期 | **120 分钟**，单次使用 | 字母表去掉 `0/O/1/I/L` |
| 安装包目录 | `{DataDir}\downloads`，文件名 `ControlServer-Agent-Setup-<版本>.exe` | 版本不一致只提示、不阻断 |
| 客户端状态目录 | `%ProgramData%\ControlServer.Agent` | 服务与托盘**必须**同一份 |

### 20.3 实施中暴露并修掉的真问题（都有守门用例/脚本）

1. **写操作返回体被 MVC 包了两层**：`IActionResult Mutation(...) => Ok(new AgentMutationResult(...))` 被调用方再 `Ok(...)`，
   线上实得 `{"value":{…},"formatters":[],"statusCode":200}`。→ 改为返回 DTO **本体**；
   集成测试 `Mutation_endpoints_return_the_payload_directly` 守门。
2. **「注册」被当成「见过」**：注册时写了 `LastSeenAt = now`，导致「等待连接（`never`）」这个状态**永不可达**。
   → 新建行 `LastSeenAt: null`；`UpdateRegistrationAsync` 移除 `seenAt` 参数且**不再写该列**。
   **`LastSeenAt` 只由心跳写** —— 这条语义现在被集成测试钉死。
3. **冒烟脚本的断言形状写错了**（不是产品 bug）：写操作返回的是 `{ok,status,message,list:{items,total,…}}`，
   而脚本按 `body["items"]` / `body["total"]` 取 —— 表现为"选了值没落库"式的假失败。
   → 加 `agents_of()` / `total_of()` 兼容两种形状。
4. **"移除后列表清空"这个断言本身是错的**：D 段的**真客户端**已用自己机器码在表里注册过一条，
   删掉 B 段那台之后剩 1 条才对。→ 改成"这台不在了 + 条数正好减 1"。
5. **PowerShell 未绑定的 `[string]` 参数是 `''` 而不是 `$null`**：`build-agent-installer.ps1` 里用
   `-not $PublishTo -and $PublishTo -ne ''` 判"传没传"→ 永远判成"传了空"，
   **默认投放从未生效**却打印"跳过投放"。→ 改用 `$PSBoundParameters.ContainsKey('PublishTo')`。
6. **`appsettings.json` 到底读不读得到（实测确认）**：`Host.CreateApplicationBuilder` + `AddWindowsService`
   会把 ContentRoot 钉在 **`AppContext.BaseDirectory`** —— 所以服务形态下即使 cwd 是
   `C:\Windows\System32`，exe 同目录的 `appsettings.json` **照样被读到**（默认状态目录未被创建，
   配置里的临时目录被创建 = 铁证）。安装脚本因此可以放心把"服务端地址 / 注册令牌"写配置文件而非服务命令行
   （放 binPath 会被 `sc qc` 原样打印给任何本机用户）。

### 20.4 验证结果（本轮）

| 项目 | 结果 |
|---|---|
| 全解决方案编译 | **0 警告 0 错误** |
| 前端 `vue-tsc --noEmit` + `vite build` | 通过 |
| `ControlServer.Core.Tests` | **575 通过 / 0 失败**（含新增 M4 单测 54） |
| `ControlServer.Server.IntegrationTests` | **259 通过 / 0 失败**（含新增 M4 集成 14） |
| `agent-smoke.py` | **79 通过 / 0 失败**（安装包 / 令牌准入 / 在线表 / 真 UDP 发现 / 真客户端端到端 / 权限矩阵） |
| `dist\agent\` | 恰好 2 个 exe：`ControlServer.Agent.Service.exe` 35.1 MB、`ControlServer.Agent.Tray.exe` 34.6 MB |
| `dist\agent-setup\` | `ControlServer-Agent-Setup-0.1.0.exe` **58.6 MB** |
| `install-agent.ps1` | 非管理员路径实跑：复制 / 状态目录 / 配置写入 / ACL 收紧全通过，服务注册失败给出可操作提示并 **exit 1** |
| 服务注册 / 卸载真跑 | ⏳ **本机非管理员 + `sc.exe` 被沙箱黑名单** ⇒ 待目标机以管理员执行（与安装包复测同一批） |

### 20.5 本轮**没做**（顺延到 M4.2+）

遥测图表与**硬件温度**（D6 未定）/ **文件分发与文件传输** / **局域网远程桌面**（M5 与之重叠）/
BootAssistant 功能对齐（开关机记录、定时关机等） / 客户端自更新 / Linux 客户端实现 /
`.tools/agent-uia-check/` 托盘自动化夹具。

---

## 21. 实施记录：M4.2「遥测」**已落地**（2026-10-12）

> 验收依据 §14：CPU / 内存 / 磁盘 / 网络 / GPU 与任务管理器偏差 ≤5%；开关机记录与事件查看器逐条一致。
> 本轮把遥测从契约一路做到前端页面，并跑通「真客户端 → 落库 → 曲线」的端到端。

### 21.1 实际落地范围

| 层 | 内容 |
|---|---|
| 契约 | `AgentTelemetryDtos.cs` —— `AgentMetricsSampleDto`（10 字段全可空）/ `AgentMetricsSummaryDto` / `AgentMetricsPointDto` / `AgentMetricsSeriesDto` / `AgentPowerEventKinds`（7 种）/ `AgentPowerEventDto` / `AgentPowerStatsDto` / `AgentInventoryDto`（CPU·内存条·分区·磁盘·显卡·显示器·OS·域·开机时刻·登录用户·`PowerEventsSupported`）/ `AgentIngestAckDto` |
| Core | `Models/AgentTelemetry.cs`、`Abstractions/IAgentTelemetryStore.cs`、`Services/AgentMetricsAggregator.cs`（**全静态纯函数**：`MemoryPercent` / `Summarize` / `ToPoint` / `ChooseBucketSeconds` / `Bucketize` / `AggregateDaily` / `DailyToPoints` / `SummarizePower`） |
| Platform | `HostTelemetry.cs`（`IHostMetricsSource` / `IPowerEventSource` / `IHostInventorySource`）、`WindowsHostMetricsSource`（PDH 计数器）、`WindowsPowerEventSource`（事件日志）、`WindowsHostInventorySource`（WMI）、`BasicHostMetricsSource` / `BasicHostInventorySource` / `NoopPowerEventSource`（Linux 侧**明确不支持**，不是坏掉） |
| Agent | `Telemetry/TelemetryCollector.cs`（采样；取批**不删**、成功才提交；记录号水位增量读开关机；2 小时重叠窗口）、`Telemetry/TelemetryStateStore.cs`（`telemetry-state.json`）、`AgentRuntime` 加 `RunSamplerAsync`（整进程）+ `RunReporterAsync`（1 秒节拍驱动三条独立排期） |
| Infrastructure | `Persistence/TelemetryTime.cs`（**毫秒换算唯一入口**）、`AgentTelemetryEntities.cs`（4 表）、`AppDbContext.M4Telemetry.cs`、`EfAgentTelemetryStore.cs`、迁移 `20261011183719_M4_2_AgentTelemetry` |
| Server | `Services/AgentTelemetryService.cs`（摄入 / 落盘 / 查询）、`HostedServices/AgentTelemetryHostedService.cs`、`AgentsController` 加 `metrics` / `power-events` / `inventory`、`AgentHub` 加三个摄入方法 + `AgentTelemetryUpdated` 推送 |
| 前端 | `MetricsChart.vue`（**零依赖手写 SVG**：按 null 断开折线 + 悬停读数）、`AgentsPanel.vue`（「实时负载」列 + 排序 + `applyLiveMetrics`）、`AgentDetailPanel.vue`（实时条 + 五档时间窗 × 三张图 + 开关机统计与记录表 + 硬件清单折叠）、`format.ts`（`beijingIso` / `fmtRate` / `fmtMb` / `fmtUptimeHuman`） |

### 21.2 存储分层与两条**故意不同**的持久化路径

| 层 | 表 / 载体 | 粒度 | 保留 | 写时机 |
|---|---|---|---|---|
| 热 | `AgentHostRegistry` 内存 | 最新一帧 | 进程存活期 | **同步**（页面实时数字不等落盘） |
| 温 | `agent_metrics` | 5 秒明细 | 30 天 | **异步成批**（`FlushAsync`） |
| 冷 | `agent_metrics_daily` | 日聚合（均值 + 峰值 + 采样数） | 1 年 | 每日滚动 |
| 事件 | `agent_power_events` | 逐条 | **永久** | **同步落盘后才应答** |
| 清单 | `agent_inventories` | 一机一行（最新） | 覆盖 | 同步 |

- **指标走"内存缓冲 + 异步落盘"**：300 台 × 5 秒 = 60 写/秒，直写会让 SQLite 的 WAL 被小行写满，
  把"改个备注"这种真写操作挤在后面。代价是崩溃最多丢一个落盘周期的数据。
- **开关机事件 / 清单走"同步落盘后再应答"**：客户端的记录号水位一旦推进，那条事件**永不会再发** ——
  用一点延迟换数据真实性，任何时候都划算。**写入失败绝不能回 `Ok`**。

### 21.3 🔥 时间戳存 **Unix 毫秒**（`AtMs`，`long`）而不是 `DateTimeOffset`

**真因**：EF 的 SQLite 提供程序**拒绝翻译** `OrderBy(x => x.DateTimeOffset)`，运行时抛
`NotSupportedException: SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses`。
首次出现在 `EfAgentTelemetryStore.QueryPowerEventsAsync` —— **开关机记录一打开就 500**，
客户端侧表现为"上报被拒（error）"每分钟刷屏。

- 换算只经 `Persistence/TelemetryTime.cs` 的 `ToMs` / `FromMs`（后者统一 `.ToOffset(BeijingTime.Offset)`）。
- **代价**：亚毫秒被裁掉 —— 写测试时别用带亚毫秒的期望值（会得到看似时区错的假失败）。
- **例外**：`agent_inventories.CapturedAt` 仍是 `DateTimeOffset`（一机一行、无排序无范围查询）。
- `MigrationTests` 用 `pragma_table_info` 断言 `AtMs` 必须是 `INTEGER`，改回去会立刻红。

### 21.4 聚合口径（纯函数，可脱离服务器单测）

- **null 参与运算 = 忽略，而不是当 0**（3 个点、1 个 CPU 采不到 ⇒ 均值是另 2 个的均值）。
- **空桶跳过，不补 0**（补 0 会画出一条假的掉底曲线）。桶边界锚在查询窗口左界。
- 分桶粒度只取固定梯级 `5,10,…,86400`。
- 日聚合只存均值 + 峰值 + 采样数；在线时长 = 采样数 × 间隔，上限 86400（**估算**）。
- 开关机统计：末尾还开着的机器**不计入**平均在线时长；睡眠 / 唤醒**只计数不配对**；
  **6008 与 41 在 10 分钟窗口内合并成一次意外关机**（同一次事故两条记录，不合并会整整翻一倍）。

### 21.5 实施中暴露并修掉的真问题

1. `DateTimeOffset` 的 `ORDER BY` —— 见 §21.3（本轮最贵的一个坑）。
2. **上报失败日志刷屏**（实测 1 分钟 24 行）：三条上报循环共用一个失败计数，指标的成功会**清掉**
   开关机的失败计数 ⇒ 每轮重记一行。→ 拆成三个独立计数器。
3. `TelemetryStateStore` 属性名 `Path` 遮掉 `System.IO.Path` ⇒ `CS1061: "string" 未包含 "Combine"`。
4. **开关机「详情」列是噪音**：`property.Value.ToString()` 无差别转换 ⇒ `byte[]` 变成字面量
   `System.Byte[]`，Kernel-Power 41 的载荷变成一串裸数字（实测 19 条里一半以上不可读），
   而这一列的**唯一用途**就是与事件查看器逐条核对。
   → 改为**优先取 `FormatDescription()` 正文原文**（与事件查看器逐字一致），退路是**纯文本型**插入字符串。
   ⚠️ 与"不解析正文"**不矛盾**：不解析 = 不从正文抠断电时刻当 `At`；这里是**原样呈现**。
   修后 19/19 条可读，例如 `上一次系统的 17:09:49 在 ‎2026/‎10/‎8 上的关闭是意外的。`
5. **遥测三端点漏进授权闸门分类表** ⇒ fail-closed 会把它们当"不可用"拦掉；失败形态极隐蔽
   （有授权时一切正常、没授权时详情页一片空白）。→ 归入 `Allowed`，由 `LicenseGateTests` 反射守门钉住。
6. **前端图表横轴少 8 小时**：`toISOString()` 产出 `...Z`，被 `fmtDateTime`（截 ISO 字符串）显示。
   → 新增 `beijingIso(ms)`；服务端 `QuerySeriesAsync` 也把窗口归一化回 `+08:00` 再回显。
7. `在 <script setup>` 里 `export interface` 不合法（`ChartSeries` 移到 `types.ts`）；`sortBy` 重复声明。

### 21.6 验证结果（本轮）

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln` | **0 警告 / 0 错误** |
| `npm run typecheck` / `npm run build` | 通过（`index-D-W-GUZa.js` 309.91 kB / gzip 104.47 kB） |
| `ControlServer.Core.Tests` | **610 / 0**（新增 `AgentMetricsAggregatorTests` **35 例**） |
| `ControlServer.Server.IntegrationTests` | **277 / 0**（新增 `AgentTelemetryApiTests` **18 例**；`MigrationTests` 迁移历史 **5 → 6**，新增 4 张表 + `AtMs` 的 `pragma_table_info` 断言） |
| `agent-smoke.py` | **99 通过 / 0 失败**（D 段新增遥测段，含"详情无 `System.Byte[]` 泄漏 / 无纯数字转储"回归守卫） |
| M4.2 端到端临时脚本 | **25 / 25**（含正文型详情 19/19） |
| 真浏览器（无头 Edge + CDP） | 列表页实时负载列（CPU 绿条 27.8% / 内存红条 98.8%，阈值着色正确）+ 排序 4 项；详情页 7 项实时条 + **3 张 SVG 图** + 数据来源文案 + 五档时间窗；图表 x 轴为**北京时间**（`2026-10-12 03:08:12`）；开关机统计（开机 5 / 异常 5 高亮 / 蓝屏 0 / 平均 16 小时 10 分）+ 19 条记录表 + 口径说明；硬件清单 6 块。**控制台零错误** |

**实测数据形状**：清单 `8037MB` / `powerEventsSupported=true`；时序 `bucketSeconds=30` / `source=raw`，
CPU 14.48% / 内存 89.88% / GPU 5.30%，**首帧速率类为 null**；开关机 19 条（5 开机 / 5 异常 / 睡眠唤醒齐全，
EventId 6005·6008·41·42·107）；客户端退出 **77 s** 判离线（阈值 75 s）。

### 21.7 本轮**没做**（顺延）

- ~~`dist\` 未重打~~ → **收官已重打并复验**：`build-portable-exe.ps1`（`win-x64` 1 文件 49.5 MB / `win-x64-service` 2 文件）、
  `build-agent.ps1`（`dist\agent` 2 exe）、`build-agent-installer.ps1`（`ControlServer-Agent-Setup-0.1.0.exe` 58.7 MB，
  SHA256 `A076AD7A…C9CDD50`，已投放 `C:\ProgramData\ControlServer\downloads`）；
  打包产物跑 `--only agent` 冒烟 **99/0**、全量冒烟 **6/6 场景**，并起 `dist\win-x64` 常驻实例做了真浏览器复验。
- **文件分发与文件传输** / **局域网远程桌面**（M5 与之重叠）/ 客户端自更新（需签名 + 回滚）/
  Linux 客户端实现（三个遥测源在 Linux 侧是 `Basic*` / `Noop`）/ `.tools/agent-uia-check/` 托盘夹具。
- **硬件温度** → **已在 M4.3 落地**（见 §22）。
- §15 其余决策仍待拍板（尤其 **D14 设备规模** —— 它决定保留策略是否要再调）。

---

## 22. 实施记录：M4.3「硬件温度」**已落地**（2026-10-12）

> **口径**：硬件温度是**可选能力**。引入 `LibreHardwareMonitorLib`（MPL-2.0，
> 内含 WinRing0 内核驱动）。
>
> 🔥 **2026-10-14 起默认值翻过来了**（需求①）：**安装向导那个勾选框默认勾上**
> ⇒ 装完就是开的。不想装驱动时：
> 图形安装**取消勾选**，或 `install-agent.ps1 -NoThermal`，或静默安装 `\THERMAL=0`。
> ⚠️ 旧的 `-ThermalEnabled` **已废弃**（保留只为不破坏既有批处理，行为等同不传）。
> 拿不到就显示「未启用」/「—」，**绝不显示 0**。
>
> ⚠️ **"配置缺失 = 关"这条没变**（绿色版 / 手工起的 exe / 冒烟裸起）：默认值在**安装侧**，
> 客户端代码里没有这个默认值 —— 一个会释放并加载内核级驱动的能力，不该"没人告诉它就自己开"。

### 22.1 🔥「默认不装」凭什么成立：靠代码，不靠打包排除

Windows 上读 CPU / 主板温度**没有用户态正路**，必须加载内核级驱动。LHM 把
`WinRing0x64.sys` 以 **gzip 资源内嵌在 DLL 里**（`LibreHardwareMonitor.Resources.WinRing0x64.gz`），
**只有真的调用 `Ring0.Open()`（即 `new Computer{...}.Open()`）时才解出来落盘并加载**。

于是"默认不装"是**天然成立**的，实现方式就是「**未启用时根本不去 `new Computer`**」：

```csharp
public HostThermalReading Read()
{
    // 未启用：连 Computer 都不构造。这是"默认不装驱动"的实现本体。
    if (_disposed || !_enabled) return HostThermalReading.Empty;
    ...
}
```

⚠️ **刻意不用"打包时排除驱动文件"的方案**：那种排除迟早会被某次"顺手引用一下"破坏，
而且排除之后**连勾选也装不上了**。用代码守住，是唯一不会随时间腐坏的做法。

⚠️ `appsettings.json` 里 `ThermalEnabled` **总是被显式写出**（`true` 或 `false`），
合并语义只覆盖显式传入的项 —— 所以"重跑安装脚本"就是改这个开关的**唯一**正当途径
（那个文件已收紧为仅 SYSTEM/管理员可读，**不要手工改**）。

⚠️ **升级安装包时的行为**（2026-10-14 起）：重跑安装脚本会按**那一次**的勾选重写这个键；
不勾就是 `false`。想保留原来的状态就**别加 `-NoThermal`**（默认是开）。

⚠️ `disabled` 时 `ThermalNote` **必须具体到"去哪儿解决"**：组合根分得清
「配置里没有这一项」（绿色版 / 手工启动 ⇒ 去补配置）与「显式关掉了」（⇒ 去翻安装勾选框），
两种说法**指向不同的排查动作**，不能含糊成同一句（详见 `WindowsThermalSource` 的 ctor 注释）。

### 22.2 三态语义（`AgentThermalStates`）

| 状态 | 含义 | 页面表现 |
|---|---|---|
| `ok` | 已启用，且**拿到了至少一项**读数。**CPU 缺失时 `thermalNote` 说明原因**（2026-10-19，见 §22.9） | 显示实际度数；拿不到的那一项显示 `—`，CPU 缺失时附带一行提示 |
| `disabled` | 未启用。**这是默认值，也是正常状态** | 显示「未启用」+ 怎么开 |
| `failed` | 已启用但**一项都没拿到**。原因进 `ThermalNote` | 标红「驱动加载失败」+ 原文原因 |

🔥 **`ok` 的真实语义是"拿到了至少一项"，不是"驱动装上了"** —— 这一点由真机实测纠正（见 §22.5）：
显卡温度（NVIDIA / AMD）走的是**用户态** NVAPI / ADL，**不需要内核驱动**。
于是完全可能出现"驱动没装上、GPU 有度数、CPU 与硬盘为 null"的机器，状态是 `ok`。
契约与前端注释都已按此改写。

**绝不用 0 冒充**：`0 ℃` 会被读成"机器很凉快"，与事实恰好相反，比显示「未启用」危险得多。
反向也守住：**`0 ℃` 是合法读数，不许被净化抹掉**（冷库 / 冬天户外机柜），
"见 0 就丢"属于替传感器做决定 —— 本项目一贯「原样呈现、让异常可见」。

⚠️ **唯一的例外是 Linux 的 `LinuxThermalSource`**：它把 `0` 当作"没读到"。
理由不是"冷库不存在"，而是 sysfs 的事实不同：驱动没填时内核就往 `temp*_input` 里写 `0`，
"0"在那里几乎总是占位。**Windows（LHM）读不到时给的是 `NaN`**，所以 Windows 侧保留 0。
两个平台上同一个数字含义不同，不要把其中一边的判据搬过去。

### 22.3 数据落点

| 项 | 放哪 | 为什么 |
|---|---|---|
| `thermalState` / `thermalNote` | **硬件清单**（`agent_inventories`，一机一行、6 小时一次） | 它是**机器级恒定量**（驱动在进程启动时加载一次，之后不变）。塞进 5 秒一条的指标里 = 用 300 台 × 17280 条/天 的代价重复一个恒定值 |
| `cpuTempC` / `gpuTempC` / `storageTempC` | 每个采样点（`agent_metrics` 3 列 REAL，可空） | 是随时间变的量，要画曲线 |
| 日聚合 `CpuTempAvgC` / `CpuTempMaxC` / `GpuTempAvgC` / `StorageTempAvgC` | `agent_metrics_daily` 4 列 REAL，可空 | 冷层里温度最有用的一个数是**峰值**（"这周最高烧到多少度"），均值会把一次过热抹平 |

采样时**一次采样只读一次温度**（`var thermal = ReadThermal()`）：三次 `Read()` 就是三倍驱动开销。

### 22.4 🔥 净化必须**两条路径共用**（`AgentMetricsAggregator.Temperature`）

存储路径（`EfAgentTelemetryStore`）与**在线表摘要路径**（`AgentMetricsAggregator.Summarize`）
必须用**同一套**净化。只做一条会分裂出「**库里是 null、在线表里是 NaN**」：

> System.Text.Json 的严格模式（本项目只开了 `AllowReadingFromString`，**没开**
> `AllowNamedFloatingPointLiterals`）**拒绝** NaN / Infinity。摘要里混进一个 NaN，
> 整个 `GET /api/agents` 就会序列化失败 ⇒ **一台坏客户端让所有机器从页面上消失**。

净化规则：`NaN` / `±Infinity` / `< -40 ℃` / `> 150 ℃` → `null`。**0 保留**。

### 22.5 真机双路径验证（**已做**）

| 路径 | 怎么跑 | 实测结果 |
|---|---|---|
| **未勾选** | `python .tools/thermal-probe.py dist/agent/ControlServer.Agent.Service.exe` | **5 / 0**。日志 `[Info] 硬件温度：未启用（安装时未启用硬件温度监控。）`；该行**只出现 1 次**（不重试）；无任何 Warning/Error 级驱动行；**数据目录/程序目录里没有释放出任何 `WinRing0*`** |
| **勾选（端到端）** | `python .tools/thermal-enabled-e2e.py` | **10 / 0**。状态 `ok`；`/api/agents/{id}/metrics` 里 **`gpuTempC` 真实落库可取（两次实测 42 / 44 ℃）** |

🔥 **实测纠正了一个契约假设**：本机当前会话**不是管理员**（`IsUserAnAdmin: False`），
但状态依然是 `ok` —— 因为 **GPU 温度走 NVAPI 用户态**拿到了读数，而 CPU / 硬盘（需要内核驱动）
为 `null`。这与 §22.2 的口径一致，也让"`ok` ≠ 驱动装上了"从猜测变成事实。
**因此"驱动真的被加载、进而读到 CPU / 主板温度"这条路径在本机无法验证** ——
它需要在**管理员 / SYSTEM** 下跑（服务形态天然是 LocalSystem）。
这条缺口如实记录，不当作已通过。

### 22.6 实施中暴露并修掉的真问题

1. **`AgentThermalStates.IsValid` 写了却没人调用** —— 客户端上报的 `thermalState` 未过白名单就被原样存进清单。
   页面拿它去匹配三个分支，匹配不上只能显示"未知"，等于**悄悄丢掉了信息**。
   修法：`AcceptInventoryAsync` 里过白名单，不合法则 `ThermalState` 与 `ThermalNote` **一起清空**
   （留一条"失败原因"配着"未知状态"只会更让人困惑）。
2. **`LHM 0.9.4` 会带来 `NU1605`（检测到包降级）** —— 它把传递依赖升到
   `System.Management >= 9.0.0`，与本项目集中钉的 `8.0.0` 冲突。**必须钉 0.9.3**
   （依赖 `8.0.0`，与 .NET 8 对齐）。0.9.3 无 `net8.0` 资产，NuGet 回退到其 `net7.0` 资产（正常可用）。
3. **`dotnet ef migrations add --no-build` 会用改动前的旧程序集 ⇒ 生成空迁移**；
   而 `migrations remove --no-build` 更坑：**删掉的是上一个迁移**（本次误删了 `M4_2`）。
   反制：`export DOTNET_ROOT=... && export PATH="$DOTNET_ROOT:$PATH"` 并**去掉 `--no-build`**；
   动手前先查真实库 `__EFMigrationsHistory`（本机真实库只到 `M4_AgentHosts`，`M4_2` 从未应用，故可安全重建）。
4. **`SELECT notnull` 会被 SQLite 解析成 `NOT NULL`** ⇒ 迁移测试报 `near "notnull": syntax error`。
   必须写 `SELECT "notnull"`。
5. **客户端自己的日志走 `<StateDir>\logs\<日期>.ndjson`（Serilog 文件 sink）**，
   控制台上只有 ASP.NET 那 3 行生命周期日志。冒烟/探针若读 stdout 去断言日志内容，
   会得到**永远通过**的空检查 —— 比没有断言更糟。

### 22.7 验证结果（本轮）

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln` | **0 警告 / 0 错误** |
| `npm run typecheck`（`vue-tsc`）/ `npm run build` | 通过（`index-rj8XW5R8.js` 311.84 kB / gzip 105.33 kB） |
| `ControlServer.Core.Tests` | **628 / 0**（+12：温度净化 `An_impossible_temperature_is_scrubbed_to_null` 等 3 组用例） |
| `ControlServer.Server.IntegrationTests` | **282 / 0**（+5：温度往返 / 非法值净化 / 0 值保留 / 坏读数不得拖垮在线表 / 热状态白名单） |
| `MigrationTests` | 迁移历史 **6 → 7**，新增 `M4_3_AgentThermal`；断言 3 个 `agent_metrics.*Temp*` 与 `agent_metrics_daily.CpuTempAvgC/MaxC` 类型为 `REAL` **且可空** |
| `agent-smoke.py`（打包客户端） | 温度段 **6 条断言全 PASS**；全量冒烟 **6 / 6 场景** |
| 真机双路径探针 | 未勾选 **5 / 0**；勾选端到端 **10 / 0**（实测 GPU 42 / 44 ℃） |
| `dist\` 重打 | `win-x64` 1 文件 50.5 MB / `win-x64-service` 2 文件 / `dist\agent` 2 exe（36.2 + 35.7 MB）/ 安装包 60.7 MB（SHA256 `CE9CC724…C1765`）。**单文件形态未被 LHM 破坏**（无散落的 `.sys`/`.dll`） |

### 22.8 本轮**没做**

- **"驱动真的加载成功 ⇒ 读到 CPU / 主板温度"**未验证（需要管理员 / SYSTEM 会话，见 §22.5）。
- **前端温度的浏览器复验**只做到"类型检查 + 构建 + 服务端契约一致"，未起 CDP 无头浏览器逐项核对。
- `dist\` 之外的分发渠道（MSI / Linux 客户端温度 / Docker）未动：Linux 侧 `NoopThermalSource`
  会如实上报「当前平台未实现硬件温度采集」，**是明确不支持，不是坏掉**。

### 22.9 AMD CPU 温度读不到：四条原因与修法（2026-10-19）

> **现场现象**：AMD 机器的温度一栏是 `CPU —`，而 GPU / 硬盘有度数；同一份客户端在 Intel 机器上正常。
> 状态是 `ok`（因为硬盘有值），`ThermalNote` 是 `null` —— **页面上没有一个字解释**。

查下来是四条彼此独立的缺口，共同点是**都不会报错**：

| # | 原因 | 旧行为 | 修法 |
|---|---|---|---|
| 1 | **老 AMD 的 CPU 温度不在 CPU 硬件上**（K10/K15、FM2/AM3）：由主板 SuperIO（Nuvoton / ITE）提供，挂在 `Motherboard → SubHardware` 下 | 只遍历顶层 `HardwareType.Cpu` ⇒ **永远 null** | 硬件树递归拍平（`Flatten`）；SuperIO 里**名字带 CPU** 的温度作为最后一档兜底 |
| 2 | **AMD 命名不止一个**：`Core (Tctl/Tdie)`、`Core (Tdie)`、`Core (Tctl)`、`CCD1 (Tdie)`、`CCDs Max (Tdie)`、`CCDs Average (Tdie)` | "含 Tdie 就取" ⇒ 把**单个 CCD 的裸片温度**当成整颗 CPU | 分级：`Package`/`Tctl`/`Tdie`（排除 CCD）→ `CCDs Max` → `Average` → 逐核心平均 → SuperIO |
| 3 | **`ok` 的判据过宽**：只要 CPU / GPU / 硬盘**任意一项**有值就是 `ok` | CPU 缺失时 `Note = null`，页面只剩一个 `CPU —` | `ok` 但 CPU 缺失时，`Note` 写清"枚举到哪颗 CPU / 有没有传感器 / 读数是否无效" |
| 4 | **驱动起来了但首次枚举没拿到**（AMD 的 SMN 读取比 Intel 的 MSR 慢） | 只刷 2 次，失败即永久 `failed` | 连刷 3 次（间隔 80 ms）；**仅当"枚举到 CPU 硬件却没读到温度"时**最多再试 2 次、间隔 30 s。驱动加载失败（被拦 / 无权限）**仍然只试一次** |

**关键修法：把"挑哪个传感器"抽成 `ThermalSensorPicker`（Platform.Abstractions）。**
理由不是"代码更整齐"，而是**它原来完全测不到**：逻辑写在 `WindowsThermalSource` 里，
而测试工程不引 `Platform.Windows`（引了在 Linux 上跑不起来）⇒ 这段策略**从来没有一条测试**。
AMD 的命名差异与"温度挂在主板"这两件事，正是因为没测试才一直没人发现。
现在平台实现只剩"把硬件树拍平成读数"，20 条用例可以在任何平台上跑。

**诊断文案的三种指向**（这是本轮真正的价值 —— 读不到时必须知道下一步做什么）：

| `ThermalNote` 里的话 | 说明 | 该做什么 |
|---|---|---|
| 硬件枚举里没有 CPU 项 | 驱动没生效 | 查安全软件拦截 / 是否以 SYSTEM 运行 |
| 已枚举到「AMD Ryzen 7 9700X」，但没识别出任何温度传感器 | **这颗 U 比驱动库新**（Zen5 / 部分 Zen4 APU） | 升级 LibreHardwareMonitor（0.9.4 也不行，要 0.9.5+，见下） |
| 传感器有，但读数全是 NaN / 超范围 | 驱动读到了传感器、没读到温度 | 查驱动版本 / 是否有其他监控软件抢占 |

🔥 **为什么不顺手升级 LHM**：`0.9.3` / `0.9.4` 的 CPU 实现类**完全相同**
（`Amd0FCpu` / `Amd10Cpu` / `Amd17Cpu` / `GenericCpu`，没有新增 AMD 家族），
而 `0.9.4` 会把传递依赖升到 `System.Management 9` 与本项目集中钉的 `8` 冲突（NU1605，§22.6-2）。
**升 0.9.4 拿不到任何 AMD 收益，只换来构建链风险。** Zen5 支持要等 0.9.5+，
那是一次独立的依赖决策（要连带升 `System.Management` 并复验单文件打包）。

⚠️ **本轮没有 AMD 真机**：本机是 Intel i5-8265U，且当前会话不是管理员。
新增的 20 条用例覆盖的是**输入 → 输出**的映射（命名 / 分级 / 值域 / 诊断文案），
**"真机 AMD 能读到度数"仍未实测** —— 这条如实记录，不当作已通过。

---

## 23. 实施记录：M4.4「文件分发 / 文件传输」**已落地**（2026-10-12）

> **口径**：**控制信令走 SignalR，实际字节走 Agent 主动 HTTP**（§7.2 的取舍）。
> 这条决定的直接后果是：**客户端依旧一个入站端口都不开** —— M4.1 的承诺在这里没有被破坏。
> 三条语义全部实现：仓库共享（中心→浏览器）、定向下发（中心→指定客户机）、回传采集（客户机→中心）。

### 23.1 落地范围

| 层 | 内容 |
|---|---|
| 契约 | `FileRepoDtos.cs`（`FileKinds` / 上传会话 / 完成请求）、`DistributionDtos.cs`（两方向 + 任务/明细/进度状态机）、`Policies.FileRead` / `FileIngest`、`AgentHubMethods`、`EventNames.TransferProgress` |
| Core | `FileRepositoryService`（分块会话 + 摘要 + 原子落地 + 过期清理）、`DistributionService`（建单建满明细 + 进度 + 完成 + 状态汇总）、`FileTransferMath`（纯函数：进度 / 文件名清洗 / 扩展名白名单 / **路径穿越判定** / 汇总）、两个存储端口 |
| Infrastructure | 4 张表（`file_repo_items` / `file_upload_sessions` / `distribution_tasks` / `distribution_items`）+ 两个 EF 存储 + 迁移 `M4_4_Files` |
| Server | 3 个控制器（`Files` / `Distributions` / `Agents.transfers`）+ `FileTransferHostedService`（30 分钟一轮：清过期会话、清终态任务）+ 授权闸门分类 + 双身份策略 |
| Agent | `FileTransferClient`（`Range` 续传下载 + 分块上传 + 边算 SHA256 + 原子落地）、`TransferWorker`（队列 + 限并发 2 + 去重 + 致命/可重试分流）、**白名单上报** |
| 前端 | 「文件」页三件套：仓库（分块队列上传 / 列表 / 下载 / 两段式删除 / 分发入口）、分发（方向 + 目标多选 + **候选目录** + 任务卡片 + 实时进度条）、页签宿主 |

### 23.2 🔥 三个"改错了就静默"的决策

1. **已收字节的唯一事实来源是 `.part` 文件的长度，不是数据库计数器。**
   `FileUploadSession` **刻意不存** `ReceivedBytes`。两份计数器迟早不同步，
   而不同步的表现是"从错误位置继续写"——**长度对、内容坏**，等摘要校验发现时已经查不出是谁先错的。
2. **偏移不符返回 `409` + 真实 `receivedBytes`，这是"对齐信号"不是错误。**
   客户端把回的真实偏移当新偏移继续，于是**不需要知道"刚才那块到底成没成"**；
   也因此在真实网络抖动下，续传逻辑不需要任何"猜"。
3. **`TotalBytes` 未知时进度返回 `0`，不是 `100`。**
   `0` 是"还不知道"，`100` 会被读成"传完了"——回传任务在客户机开始读文件前就是未知状态。

其余同源决策：**摘要谁落盘谁自算**（不信客户端自报，自报值只当期望值）；
**`File.Move` 原子落地**（先 `.cs-part` → 校验 → Move，没有"半个文件可被下载"的窗口）；
**落库失败把文件挪回暂存**（不留无元数据的孤儿文件）；
**建任务时建满明细**（离线机器上线补领）；**进度幂等不倒退**；**只终态任务可清**。

### 23.3 权限与闸门（判据：**会不会真的作用到别的机器上**）

- **`FileRead`** = Admin / Operator **或**合法客户机，**不含 Viewer**（回传文件可能含现场敏感内容）。
- **`FileIngest`** = Admin **或**合法客户机，**不含 Operator**（能往仓库写 = 能把任意内容推给所有客户机，这是提权）。
- 判机器身份用 `User.FindFirst(AgentAuthentication.ClaimAgentId) is not null`，
  **不看 `AuthenticationType`**（后者随 scheme 名改动会静默失效）。
- **闸门 Gated**：`POST api/distributions`、`POST api/distributions/collect`（建任务 = 让一批机器去下载/上传）。
- **闸门 Allowed**（13 条）：仓库自身 8 条（全在服务端本地完成，**字节不出这台机器**）+
  分发查询 2 条 + 客户机三端点 3 条（只有任务真建起来才有流量，绕不开闸门）。

### 23.4 🔥 路径安全：白名单是**客户端独有**的职责，而且现在会**上报**

服务端不知道对方磁盘长什么样，它只能"把目标目录原样下发"；真正知道"这台机器上哪些目录允许被写 / 读"的只有客户机自己。
于是安全边界**只能**落在客户机：解析不出白名单就**一律拒收**（`fatal`，不重试），
且**比较路径时必须带末尾目录分隔符**（否则 `C:\data\files-evil` 会被 `StartsWith("C:\data\files")` 判成目录内 —— 本项目在图片目录上踩过）。

**本轮补上的一环：客户机把白名单目录随硬件清单上报**（`AgentInventoryDto.TransferInbox/OutboxDirectories`）。
这让分发页能把"这台机器允许的目录"列成**候选**，运维是**从里面挑**而不是手输一个路径去撞白名单 ——
这正是"分发任务从中选一个"的落地形态。

- `null` = 老客户端没这个字段（页面退回手输）；`[]` = **本机未启用文件传输**。两种文案不同，不能混。
- 走**硬件清单**（机器级恒定量、6 小时一次）而不是新开通道 —— 与 §22.3 的 `thermalState` 同一个道理。
- ⚠️ 它只是**提示**，不是安全边界：拦截仍然只发生在客户机落地前。

### 23.5 实施中暴露并修掉的真问题

1. **`EfDistributionStore.CreateTaskAsync` 里 `TaskId = 0` 指望 EF 回填不成立**（未配导航关系）。
   改为**事务内两步 `SaveChangesAsync`**（先插任务拿自增 Id，再建明细）。
2. **`MigrationTests` 的表清单与迁移条数没跟着改** ⇒ 全量测试**回红 2 条**。
   已补齐（8 条迁移 + 4 张新表）。**教训：#179 当时只编译、没跑全量测试，这个回归被漏过一轮。**
3. **文件页只在挂载时拉一次在线表** ⇒ 用户在文件页等一台刚开机的机器，目标列表里**根本没有它**，
   必须切页才出现。已把 `AgentChanged/Online/Offline` 一并接到文件页。
4. **`fmtBytes` 原先到 MB 就停**，>2GB 会显示成 `2048.0 MB`；已补 GB 档。
5. **`.tools/transfer-resume.py` 的 Cookie 头是空的**：`HTTPCookieProcessor` 上那个 jar 字段叫 `cookiejar`
   （不是 `_cookies`）。表现是裸连接 401，**看起来像"下载端点需要额外授权"**，其实是自己把凭据弄丢了。
6. **浏览器验证脚本断言读错单元格**（摘要是 `<td>` 里的 `<button title=...>`，取 `<td>.title` 得到空串）
   ⇒ 报"摘要不一致"，**失败的是脚本不是应用**。已写进教训：*"取到的值是空串"要当成"选择器写错"的第一嫌疑*。

### 23.6 验证结果（本轮）

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln` | **0 警告 / 0 错误** |
| `dotnet test` | **Core 707 + 集成 316 = 1023 通过 / 0 失败** |
| `deploy/smoke/transfer-smoke.py`（真客户端端到端） | **68 / 0**：分块上传 / 409 对齐 / 摘要不符被拒 / `Range` 206 拼接摘要一致 / 离线补领 / 终态幂等 / 回传入库记录来源 / **真客户端落地 + 越界被拒** |
| `.tools/m44-ui-check/`（**真浏览器 + 真客户端**） | **48 / 0**：页面造 20MB 文件 → 3 次分块 `PUT` → 进度中间态 → 下载 → 选目标（**候选目录来自客户机上报**）→ 下发 → 进度靠 SignalR 推进 → 客户机磁盘落点 SHA256 一致 → 两段式删除（客户机副本不受影响） |
| **`.tools/transfer-resume.py`（>2GB 验收）** | **25 / 0**，实测 **2.5 GiB**：上传传到 896MB **主动掐断** → 等 30s → 问服务端拿真实偏移（一致）→ 续传补完；下载读 64MB 后 `close()` 模拟拔网线 → 等 30s → `Content-Range: bytes 67108864-2684354559/2684354560` → 续传；两处 **SHA256 全链路一致**；暂存无 `.part` 残留 |
| 打包复跑 | **7/7 场景全过**（服务端 `dist\win-x64` 单文件 50.5 MB、客户端 `CS_AGENT_EXE=dist\agent\...`）：`dist\win-x64`（严格 1 文件）/ `dist\win-x64-service`（2 文件）/ `dist\agent`（2 exe）/ `dist\agent-setup` 60.8 MB / `dist\installer` 74.6 MB |

### 23.7 本轮**没做** / 顺延

- **与按钮/动作系统打通**（§7.4）：只留接口，未实现 `Protocol=File` 动作类型。
- **客户端自更新**（需签名 + 回滚）：未做。
- **服务端侧限速 / 配额**：未做；客户端"限 2 路并发"提供天然背压（拉取模型下"慢"本身就是背压）。
- **Linux 客户端宿主**：传输逻辑跨平台，但宿主未构建（与 M4 整体一致）。
- 顺延到 M4.5：BootAssistant 25 项功能对齐（**无鉴权的 UDP 关机绝不上线**）。

---

## 24. 实施记录：M4.5「任务下发 + UDP 加固通道 + 会话侧」**已落地**（2026-10-13）

> **口径**：**服务端是唯一事实源 —— 整包替换、版本单调递增**（§16 的取舍落地）。
> 客户端联系不上中心时**降级跑本地只读缓存**，两条腿自愈。
> 三条"重来"被严格分开：**关 / 重启 Agent 服务** ↔ **重启整机**（`Power` + `Restart`）——
> 混用 = 想重启服务却关了机。
> 这是 M4 里**唯一**给客户机开入站端口的里程碑，所以它同时是一次加固设计：**无签名的报文绝不上线**。

### 24.1 落地范围

| 层 | 内容 |
|---|---|
| 契约 | `AgentConfigDtos`（三类任务 + 通道配置）、`UdpCommandSignature` 线路格式、`EventNames.AgentConfigChanged`、`Policies.AgentConfigRead/Write` |
| Core | `AgentConfigRules`（`Merge` / `Normalize` / `Validate`，**服务端与客户端共用同一份纯函数**）、`UdpCommandSignature`（HKDF + 验签）、`UdpReplayGuard`、三类执行器抽象 |
| Agent | 配置拉取 / 缓存 / 降级 + **整代重启**任务宿主、`UdpCommandListener`（四关）、`SessionCommandChannel`（会话侧命名管道） |
| Server | `Agents.config` 端点（GET/PUT + 版本 + 推送）、授权闸门分类、**双身份归属校验** |
| 前端 | 「客户端」页新增**任务配置区块**；准入可见性三修（见 24.4） |

### 24.2 🔥 UDP 指令通道的**四关**（少一关就等于没有）

| 关 | 判据 | 为什么不能省 |
|---|---|---|
| ① 本机开关 | `Agent:Tasks:Enabled=false` ⇒ **根本不绑端口** | "关掉"必须是**看不见端口**，不是"绑着但丢弃"（后者在 `netstat` 里仍是一个开着的口子） |
| ② 来源 IP 白名单 | 空 = 放行；非空则必须是名单内 | 端口由中心下发、改端口不写防火墙规则，所以**必须**有应用层来源约束 |
| ③ HMAC-SHA256 验签 | 密钥 = `HKDF-SHA256(agentKey)`（salt `ControlServer.UdpChannel`、info `channel-v1`） | agentKey 只在客户机本地（DPAPI），中心只留哈希 ⇒ 通道密钥**不需要第二条分发路径** |
| ④ 防重放 | 进程级 nonce 窗口（有界 1024、240s）；空 nonce 拒收 | 报文可被原样重放，否则"关机"能被反复触发 |

- **先验签、后看时间**：顺序反了会给攻击者一个**免费的探测器**（"时间不对"＝签名其实是对的）。
- 不过任何一关：**丢弃，且绝不回包**（回包 = 告诉对方"这里有个中控"）。丢弃日志**按时间节流**。

### 24.3 三个"改错了就静默"的决策

1. **`Merge` 里 `null` = 不动那一块；空数组 = 清空那一块。**
   一字之差、语义相反 ⇒ 单测里这两条**必须成对**；只写一条时，把它们实现成同一个行为也能过。
2. **版本 +1 而内容没存 = 灾难**。校验失败时**不许动版本号**：否则客户端白拉一次，
   还会把自己手上的**好配置**当成旧的。
3. **"已应用版本"只增不减、且可空**（`MAX(COALESCE(x,-1), @v)`）。
   **"从未回报"(null) ≠ "已应用 v0"** —— 前者是"这台机器还没连上过"，后者是"它拿到了空配置"。

### 24.4 实施中暴露并修掉的真问题

1. **注册被拒在中心侧**只留一行 `400`、**没有原因**（原因写在客户机日志里）⇒
   "装了客户端却没出现"极易被误判成"发现不了客户端"。
   现按 `来源 + 机器码 + 状态` 去重记一条 `Enrollment` Warning（`missing → unauthorized` 是最该看到的转变）。
2. 🔥 **前端"刷新"把正文卸载重建**：`v-if="loading"` 占位与 `v-else-if="数据"` 正文同属**一个 v-if 链**
   ⇒ 每次刷新都卸载正文，**子组件的定时器 / 未保存编辑 / 刚渲染的操作回执一起丢**。
   改成占位只用于"还没有内容"（`v-if="loading && !detail"`）。
3. **`GET …/config` 是双身份同端点**（用户读 / 机器读）⇒ 策略只能判"是不是一台合法机器"，
   判不了"是不是**这台**" ⇒ **归属校验必须显式写在控制器里**，越权读回 **403**（不是 404 —— 它确实是台合法机器）。
   这是安全边界：任务清单里有目标 IP 与发送内容。
4. **UDP 复位（`ConnectionReset` 10054）是 Windows 伪错误**（回给了已关闭的临时端口）⇒ 降 Debug；
   `NetworkReset` 仍留 Error。
5. **按文案数日志必须同时按级别过滤** —— 去重压下去的 Debug 行**文案一模一样**，不过滤会把结论判反。

### 24.5 验证结果（2026-10-13）

| 项 | 结果 |
|---|---|
| `dotnet build` | **0 警告 / 0 错误** |
| `dotnet test` | **Core 804 + Agent 39 + 集成 339 = 1182 / 0**（M4.4 结束时 1055 → **+127**） |
| → 增量构成 | Core **+97**（配置规则 + UDP 签名）、Agent **+16**（四关**真 UDP socket**）、集成 **+14**（config 端点） |
| `deploy/smoke/run-all.py`（打打包 exe） | **7/7 场景**；agent 段 **110 / 0**（新增 UDP 五条全绿） |
| 打包复跑 | 绿色版（严格 1 文件）/ 服务版（2 文件）/ 客户端（2 exe）/ 两个安装包，全部重生成并核对 SHA256 |

**测试金字塔的分工**（刻意不重复）：判定逻辑只在 **Core** 测一次；**Agent** 只验"配置 → 绑定 → 报文到桩"的接线
（真开 UDP socket，**电源 / 音量全是桩，绝不真关机**）；**集成**验双身份与权限边界；**冒烟**验"门是活的"。

### 24.6 真机验证：验了什么、**没**验什么

- **验了（服务侧）**：用交付的绿色版 exe 起隔离实例 —— 首页引用的资源哈希**正是本轮 vite 输出的那两个**，
  返回字节与磁盘 `wwwroot` **逐字节一致**（证明前端真的被重嵌）；登录 200；M4.5 路由齐全
  （`config` 匿名 401 / 未知 agent 404）。
- **没验（会话侧）**：音量 / 静音 / 启动程序的真机执行。它需要**管理员安装 + 交互会话 + 真实指令旁观**三步齐备。
  **本轮按用户选择不做**，视为**已知验证缺口**；现场照 REF-M4 §22.3 的手动步骤核对即可。
- **没验（安装路径）**：两个安装包会注册 Windows 服务、改防火墙、写启动项，属于**改动本机**；
  产物正确性由 ISCC 编译成功 + 文件数 / 体积 / SHA256 核对覆盖。
- **没验（真关机）**：正确签名 → 真执行只能在现场做；单测用桩电源，冒烟用 `ExecuteUdpCommands=false` 演练模式。

### 24.7 本轮**没做** / 顺延

- **正确签名 → 真执行**的真机端到端（要真关一次机）。
- **跨会话真机联调**（托盘在会话 1、服务在会话 0）。
- **Linux 客户端宿主**（新增冒烟断言依赖 `netstat`，Linux 侧 `ss -lunp` 未适配）。
- 无声卡机器的 `E_NOTFOUND` 现场核对。
- `AllowedCidrs` 仍无消费者。

---

## 25. 实施记录：免凭据准入 —— 整体拆除「准入令牌 / 加入码」**已落地**（2026-10-13）

> 一句话：产品口径改为 **「取消客户端准入令牌的设定，只要安装了客户端，局域网内都能自动发现，不设任何门槛」**，
> 于是 **§5.4 的 E1 + E2 设计被推翻、落地为 E3（并更进一步：连来源网段都不校验）**。
> 实现细节与回归要点见 `REF-M4-Agent.md` **§23**；本节只记"做了什么 / 为什么 / 代价"。

### 25.1 落地范围（行为 + 界面全拆，**不留应急开关**）

| 层 | 动作 |
|---|---|
| 服务端 | `AgentEnrollmentService.RegisterAsync` **不再解析凭据**，只做"容量闸门 → 发 agentKey → 落库"；三个令牌端点与其他元数据随之消失 |
| 契约 | `AgentRegisterRequest` 收敛为 `(HostInfo)`；三个令牌 DTO 删除 |
| Core / Infrastructure | `IEnrollmentTokenStore`、`EnrollmentTokenRecord`、`JoinCode` 生成与规范化、`EfEnrollmentTokenStore` 删除 |
| 客户端 | 两个配置项删除；注册请求不再携带凭据；重试分支改判 `toomany` |
| 前端 | 「准入令牌（仅管理员）」整块删除；空列表提示改为"不需要任何令牌或加入码，确认在同一网段" |
| 安装 | `install-agent.ps1` 的 `-Token` / `-JoinCode`、`.iss` 的嵌入式令牌与向导输入框、构建脚本的 `/DEmbeddedToken` 全删（**保留服务端地址输入**） |
| 配置 | `ServerOptions.Agent.JoinCodeLifetimeMinutes` 删除（已无消费者） |

⚠️ **刻意保留**：`enrollment_tokens` 表结构（实体 + DbSet 映射）。删实体就得补一个
`DROP TABLE` 破坏性迁移，而表里没有需要抢救的数据 ⇒ 保留的代价更低。
`MigrationTests` 对该表的断言**仍然是绿的**，不是漏改。

### 25.2 未被本次改动触及的部分（防止误读成"整条准入链都翻了"）

- **UDP 发现（9998）** 本来就**没有凭据**，一行没改。
- **UDP 指令通道** 仍走 **HKDF(agentKey) + HMAC-SHA256 验签 + 来源 IP 白名单**，一行没改。
- **心跳 / 在线状态判定 / 遥测 / 文件分发 / 配置下发** 全部未动。

### 25.3 代价（必须向部署方讲清的两条）

1. **同网段任何能访问 `POST /api/agents/register` 的机器都能注册成一台客户机。**
   ⇒ **中心服务的 HTTP 端口绝不该暴露到不可信网络**；一旦被端口映射 / 公网可达，等于对外网开放注册。
2. **「吊销」不再是让机器下线的可靠手段** —— 同 `machineKey` 重新注册会让记录**复活**
   （服务端无法区分"重装"与"冒充"）。**要真正下线一台机器，去那台机器上卸载。**

兜底只剩 **在线表容量上限 `Server:Agent:MaxHosts`（默认 500，防撑爆、**非**准入门槛）**
与 **管理员手工吊销 / 移除（事后处置）**。

### 25.4 验证结果（2026-10-13）

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln -c Debug` | **0 警告 0 错误** |
| `dotnet test ControlServer.sln` | **1172 项全绿**（Core **793** / Agent **39** / 集成 **340**） |
| `npm run typecheck`（`vue-tsc --noEmit`） | 退出码 **0** |
| `npm run build` → `dotnet build` | 前端产物重嵌（旧 `index-mmx5kqWn.js` 已清理） |
| `run-all.py` 全量冒烟 | **7/7 场景通过**（agent **106** / transfer **67**，均 0 失败） |

### 25.5 本轮新踩到的坑

**判"某个 API 端点还在不在"不能看状态码。**
`Program.cs` 末尾的 `app.MapFallbackToFile("index.html", …)`（SPA 回退）会把
**任何未匹配的路径**（含 `/api/...`）用 `index.html` 以 **200** 应答 ——
于是"端点已删除 ⇒ 应返回 404"这个断言**永远不成立**。
正确判据是"返回的**不是** XX"（例如：响应不是带 `tokens` 的 JSON 对象，实得 `<!doctype html>`）。
详见 `REF-M4-Agent.md` §23.9。

### 25.6 本轮**没做**

- 未做任何数据库迁移（刻意）。
- 未新增"注册限速 / 网段白名单"这类兜底 —— 与"不设任何门槛"的口径直接冲突。
- `Security:AllowedCidrs` 仍是无消费者的死配置（继续挂账）。

---

## 26. 实施记录：M4.6「UDP 默认开启 + 客户端独立可用 / 双向同步 + 回传路径下拉」**已落地**（2026-10-13，#216–#221）

> 细节、常量与陷阱清单见 **`REF-M4-Agent.md` §24**；这里只记需求、决定与验收。

### 26.1 三项需求

| # | 需求原话要点 | 落地 |
|---|---|---|
| ① | UDP 指令通道**默认开启**、默认端口 **8090**；「测试发现指令无效果」；网页里「端口」要**横排** | 默认配置改为"开启 + 8090"；新增服务端**代发**（`AgentUdpCommandService`）；行内标签 `nowrap` |
| ② | 客户端**本身**能编辑延时/循环、定时、延时启动、端口，**可独立使用**；连上中心时**自动实时同步** | 本机配置页 `AgentLocalConfigUi`（127.0.0.1:**8091**）；配置权威改为**双向 · 后写覆盖** |
| ③ | 文件回传**新增下拉选客户机路径**，避免手输出错 | 客户机目录浏览（`TransferBrowseService` + `AgentFileBrowseService`）+ 前端 `AgentPathPicker` |

### 26.2 经确认的四个决定性选择（用户全选"推荐"）

1. **客户端本地编辑界面 = 本机网页**（`127.0.0.1:8091`，托盘一键打开，复用同一套 Vue 构建产物，
   **不改目标框架**）—— 不新做一套桌面 UI。
2. **配置权威 = 双向 · 后写覆盖**（**推翻原决策 D5** 的"服务端唯一事实源"）——
   客户端离线也能改，改完标记"待同步"，连上再补推。
3. **回传文件浏览范围 = 白名单目录内逐级浏览**（不做整盘浏览）。
4. **UDP 指令发送端 = 中心服务端代发**（客户机仍不开入站端口之外的口）。

### 26.3 三条"改错了就静默"的决策

1. 🔥 **`DefaultUdpPort` 8080 → 8090**：8080 在**同一系统里同时指中心 HTTP 与客户机 UDP**，
   排障时"8080 不通"有歧义。新号与本项目其余三个（8080 / 8091 / 9998·9999）互不重叠。
2. 🔥 **"没配过"不再等于"通道关着"**：空配置现在是**开启 + 8090 + 三张清单皆空**。
   所有按"默认关"写的旧假设都要重新审。
3. 🔥 **「本机有未同步改动」这个标志必须落盘**（`AgentLocalConfigDocument.HasUnsyncedLocalEdit`）。
   只留内存 ⇒ 客户端一重启就丢 ⇒ **离线改的那版永远推不上去，且两边都不报错**。
   （§26.4 第 1 条就是这条的实证。）

### 26.4 实施中暴露并修掉的真问题

1. **本机改动重启后丢失**（真实缺陷）：`RestoreFromStore()` 没恢复"待同步"状态 ⇒
   离线编辑的那一版**永远不会被推给中心**，本机照跑、中心一直显示旧配置，**双方都不报错**。
   修法：新增落盘字段（**旧文档按 `origin` 推断**），`Save(...)` **刻意不给默认值**强制调用点表态。
   **验证非空**：退化掉该字段 ⇒ 单测 **2 条立刻变红**；恢复后全绿。
2. **"UDP 指令无效果"的根因是"没有发送端"**：四关（开关/白名单/验签/防重放）一直是好的，
   但服务端**只存 agentKey 的哈希**，自己算不出通道密钥（HKDF），于是**根本发不出报文**。
   修法：客户机连上后 `ReportChannelKey` 主动上报，服务端**只存内存**（重启后重连重报）。
3. **目录浏览的应答会超过 SignalR 默认 32 KB 上限**（最坏约 80 KB）⇒ 上限提到 **512 KB**。
   超限的症状极迷惑：**整条消息被丢弃**，表现成"某个目录永远失败、换个目录又好了"。
4. **越界判定不能看字面 `..`**：必须先 `Path.GetFullPath` 解析再比对白名单，
   否则 `D:\Inbox\..\Windows` 会被放过。

### 26.5 验证结果（2026-10-13）

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln` | **0 警告 / 0 错误** |
| `dotnet test`（全量） | **Core 793 + Agent 60 + 集成 340 = 1193 / 0** |
| `npm run typecheck`（`vue-tsc --noEmit`） | 退出码 **0** |
| `npm run build` → `dotnet build` | 前端产物重嵌（`index-zTj6tsxX.js` / `index-CNmnh0By.css`） |
| **真浏览器 UI 验证**（真中心 + 真客户机 + `agent-browser`） | **30 通过 / 0 失败**（见 §26.6） |
| `run-all.py` 全量冒烟（**9 个场景**，打 `dist/win-x64` 交付 exe） | **9/9 场景通过**（web 36 / pairing 4 / admin 78 / **agent 106** / transfer 67 / **sync 35** / **browse 24** / license 45 / rbac 94），`[FAIL]` 合计 **0** |

### 26.6 冒烟并入两个新场景（#221）

| 场景 | 脚本 | 断言 | 重点 |
|---|---|---|---|
| `sync` | `deploy/smoke/sync-e2e.py` | 35 | 本机改→推中心、中心改→客户机采纳、「已应用版本」对齐、**无中心独立使用 + 跨重启持久化** |
| `browse` | `deploy/smoke/browse-e2e.py` | 24 | 白名单根、逐级进入、**4 种越界**、notdir / notfound / 404 / **离线→offline** |

接法与既有 7 个场景一致：**中心由 `run-all.py` 起好，脚本只管客户机那一侧**；
`run-all.py` 新增客户机 exe 解析（`CS_AGENT_EXE` → 被测程序旁边 → `dist/agent` → 调试构建）
与 `work_dir` + 两个 **TCP** 端口（本机配置页是 HTTP，不能用既有那批 UDP 端口）。

**真浏览器验证（30 项，全过）**：登录 → 文件页 → 切「从客户机回传」→ 未选目标时「浏览…」禁用 →
选目标后可用 → 顶层 = 白名单根 → 进根（`logs` / `readme.txt`，目录在前）→「不能再往上」提示 →
**点文件回填输入框**（根与子目录各一条）→「← 可读目录」→ **杀掉客户机后点「刷新」给出就地提示**
（"这台客户机当前不在线…" + 重试），**不弹错误框**。

### 26.7 本轮**没做** / 挂账

- 目录浏览未做"多台并行浏览"（白名单每台一份，语义上不成立）。
- 「刷新」未做前端防连点（靠服务端 8 秒超时兜底）。
- 独立使用场景未验"中心不可达时是否无限重试刷日志"。
- 继续挂账：`AllowedCidrs` 无消费者、PBKDF2、正式 `.ico`、原生 MSI、100 台压测、真机跨会话联调。

---

## 27. 实施记录：M4.7「UDP 明文 / 显卡 / 每目标独立目录 / 整盘浏览」**已落地**（2026-10-13，#222–#229）

### 27.1 四项需求

1. **UDP 明文指令**：`DOWN` 关机 / `RESTART` 重启 / `+` 音量加 / `-` 音量减 / `0` 静音 / `1` 取消静音；
   **任意来源皆可**；**白名单取消 → 改黑名单**（不接收黑名单 IP 的指令）。
2. **显示客户端显卡信息**（列表 + 详情）。
3. **文件分发优化**：每台客户机**独占一横排**，可为每台**独立设置存放路径**，路径显示在该客户机后方，
   且能**读到该客户机的资源管理器**便于选路径。
4. **移除客户端页底部「客户端安装包」板块**（右上角已有入口，重复）。

### 27.2 决定性选择（用户已确认）

| 需求 | 选择 |
|---|---|
| 需求① UDP | **只认明文，彻底删签名** |
| 需求② 显卡 | **列表 + 详情都要** |
| 需求③ 独立路径 | **下发方向：每台一个存放路径** |
| 需求③ 浏览范围 | **整盘浏览（真资源管理器）** |

### 27.3 落地要点

- **需求①**：报文改**一行纯文本**（`DOWN`/`RESTART`/`+`/`-`/`0`/`1`，或 `name:arg`）；
  `UdpCommandSignature` → 纯解析器；`UdpReplayGuard` **整类删除**（重发 = 合法新指令，音量连按 3 次 = +3）；
  白名单翻转为 `BlockedSourceIps`（**空 = 不拦**；解析失败且有黑名单 ⇒ **fail-closed**；`MapToIPv4()` 归一化）。
  🔥 **唯一边界 = 防火墙**。**现场 Bug「指令发了没反应」的根因**：旧实现只认五段式签名报文，
  明文在"段数不对"这关**被静默丢弃**。
- **需求②**：新增 `AgentGpuSummaryDto(Name, Count)`；列表与详情共用；数据源自硬件清单（6h 一次）
  ⇒ `AgentHostRegistry.HydrateGpuSummaries` 从库**水合**（否则刷新后列表为空）。
- **需求③**：`DistributionItem.TargetDir`（**明细级**）；`EffectiveTargetDir(taskTargetDir)` 是**唯一取值口**
  （派发 `DistributionsController` + 补领 `AgentTransfersController` 共用）；契约同时接受
  `Targets[]`（新，每台一条）与 `TargetAgentIds`+`TargetDir`（旧，全批共用），控制器 `ResolveTargets` 翻译；
  迁移 **`M4_6_DistributionItemTargetDir`**；前端 `DistributionPanel.vue` 改**一台一行横排** + 每行自带 `AgentPathPicker`。
- **需求③续（浏览）**：`TransferBrowseService` 顶层 = `DriveInfo.GetDrives()` **全部盘符**；
  `Resolve` 只做 `Path.GetFullPath`（**不再比对白名单**）；盘根 `ParentPath = null`；
  **安全边界退到"传输落地"那一步**。
- **需求④**：只删 UI 板块，不动 `AgentInstallerLocator` / 下载端点。

### 27.4 安全取舍（**必须向部署方讲清**）

- **UDP 明文** ⇒ 同网段任意主机可关机 / 改音量。**唯一防线是防火墙**（端口只在内网 + 入站规则）。
- **整盘浏览 + 回传不限制目录** ⇒ 客户机目录结构对中心网页**可见**，且**任意文件都能被取走**。
  🔥 **2026-10-15 定稿（§40）**：回传（读）**没有白名单、不限制目录**。
  曾经设计过两版"限制"，两次都是坑：

  | 版本 | 规则 | 结果 |
  |---|---|---|
  | 初版（M4.4） | 默认只读 `{状态目录}\outbox` | 与「浏览」（整盘）不一致 ⇒ **在浏览里挑好的文件建单后必然失败**，现场连撞 5 次 |
  | 二版（§38） | 默认可读整盘 + `OutboxDirectories` 可收窄 | 默认值仍与浏览分叉的隐患未除，且那个开关本身成了"配了才生效"的陷阱 |
  | **三版（现行）** | **不限制目录**，只校验"绝对路径 + 文件存在" | 与浏览**不可能再分叉**（因为读根本没有范围） |

  | 方向 | 默认 | 可配置项 |
  |---|---|---|
  | 写（下发落地） | `{状态目录}\inbox`（**仍然很窄**） | `Agent:Transfer:InboxDirectories` / `DefaultInboxDirectory` |
  | 读（回传读取） | **不限制**（本机任意绝对路径） | **无** —— 刻意不留开关，见 §40.3 |

  ⇒ **回传的边界不再是目录，而是"谁能建这张单"**：`AdminOnly` + 授权闸门
  （`POST api/distributions/collect` 归 Gated）+ "运维显式点名一个文件"。
  等价于"管理员能读这台机器上的文件"，与他直接坐到那台机器前同一量级。
  需要把数据挡在机器之外的部署方，要靠**系统 ACL / 磁盘加密**这类操作系统手段，
  而不是本产品的目录白名单 —— **这一点必须向部署方讲清**。

### 27.5 验证

- `dotnet build` **0 警告 / 0 错误**；`dotnet test` 全量 **Core 783 / Agent 78 / 集成 342 = 1203，0 失败**。
- 前端 `vue-tsc --noEmit` + `vite build` 退出码 **0**。
- 关键回归：`TransferBrowseServiceTests.A_directory_outside_the_write_whitelist_is_still_browsable`
  （以前断言 `denied`，现在**必须 `ok`**）。
- **待真机**：明文 UDP 真关机 / 真音量 / 黑名单真拦；每目标独立目录真落到不同目录；真机上看到全部盘符。

### 27.6 ⚠️ 本文档中已被本节取代的旧叙述

- §3 决策表 **D10**、§2 功能表 **第 10 项**（已就地标注）。
- 凡出现 **HMAC 签名 / 来源 IP 白名单 / 验签 / 防重放 / `UdpCommandSignature` / `UdpReplayGuard` / `ReportChannelKey`**
  的段落（主要在 §24、§26 及顶部决策区）。
- 目录浏览：凡写 **"白名单根 / 逐级浏览 / 不做整盘浏览"** 的段落（主要在 §24、§26）。
- **权威现状**：`REF-M4-Agent.md` **§25**。

---

## 28. 实施记录：M4.8「客户端页瘦身 / 文件页对齐 / 浏览提速 / 接收留痕 / 逐设备曲线」**已落地**（2026-10-13，#230–#234）

### 28.1 五项需求（用户原文）

1. 客户端页主机列表**移除显卡信息**，显卡挪到详情的硬件清单里，且要显示**具体型号**。
2. 历史曲线改为**上方硬件选择器 + 下方曲线**，点上方硬件切换下方曲线；多路 CPU / 多块硬盘 /
   多块显卡 / 多块网卡要**用具体型号分开显示**。
3. 客户端里**新增「接收到的数据」**；另反馈"指令仍不生效"。
4. 文件页客户机列表各行参数**上下对齐**（名称 / 在线离线 / IP / 存放路径）。
5. 客户机资源管理器**打开某个盘特别慢**；且要**记住上次的路径**，不用每次重选。

### 28.2 决定性选择（用户已确认）

| 需求 | 选择 |
|---|---|
| ② 逐设备粒度 | **完整逐设备**（不是"只分开硬盘"） |
| ③ 显示位置 | **两边都要**（客户端本机页 + 中心详情页） |
| ③ UDP 现象 | 「**显示已发出…但机器毫无反应**」 |
| ⑤ 路径记忆存哪 | **服务端记**（客户端换机/重装也还在） |

### 28.3 落地要点

- **需求①**：主机列表列定义里去掉显卡列；显卡型号改由**硬件清单**（`AgentInventoryDto.Gpus`）在详情页
  「硬件清单」区块逐块列出（型号 + 显存）。⚠️ 列表页不再水合显卡摘要，`AgentHostRegistry.HydrateGpuSummaries`
  的那条路径**只剩详情页在用**。
- **需求④**：`FilesPanel.vue` 的客户机行从"一行流式排布"改为**固定列宽的网格**，
  表头与数据行共用同一套 `grid-template-columns`，于是"名称 / 在线离线 / IP / 存放路径"逐列对齐。
- **需求⑤（提速 + 记路径）**：
  - **提速**：`AgentFileBrowseService` 的枚举改为**先返回、再补细节**的形状；盘根列表不再逐盘调
    `DriveInfo.VolumeLabel`（那一步在慢盘 / 网络盘上会卡住秒级）。
  - **记路径**：契约新增 `AgentHostDto.LastSourceDir` / `LastTargetDir`（**可空 TEXT**），
    迁移 **`M4_7_AgentLastPath`**；客户端**每次成功浏览后回报**，服务端按
    `CASE WHEN @p IS NULL THEN 旧值 ELSE @p END` 落库 —— **传 null = 不动那一项**，**空串 = 用默认目录**
    （两种语义必须分开，否则"清空输入框"会变成"忘掉记住的路径"）。
  - 前端 `AgentPathPicker` 新增 `initialPath`（打开时直接落在记住的目录），
    并在 `notfound` 时**自动退回盘符列表**（记住的目录可能已经被删掉 / 盘符已摘除）。
- **需求③（接收留痕）**：
  - 客户端新增环形缓冲 `ReceivedDataLog`（容量 200、原文最多 200 字符、**不落盘** ——
    它是排查工具，不是审计日志）。
  - UDP 监听与 SignalR 回调**每条入站都留痕**：`executed` / `failed` / `ignored` / `unrecognized` / `blocked`。
    ⚠️ SignalR 的 `OnCommand` **先记再动手** —— `restart` 会让进程重启，记在后面就永远记不上。
  - 读取走 `AgentHubMethods.ListReceivedData`（一问一答，8 秒超时），
    服务端 `AgentReceivedDataService` → `GET /api/agents/{id}/received`（AdminOnly，已登记进 `LicenseGate`）。
  - 🔥 **三种"长得一模一样"的故障**（这正是需求③想要能分辨的东西）：
    **没收到**（防火墙 / 网段）／**收到了认不出**（多一个空格、段数不对）／**认出了没执行**
    （演练模式、会话侧不在）。页面按 `outcome` 把三者分开显示，并附**原文**。
- **需求②（逐设备曲线）**：见 §28.4。

### 28.4 需求②的分层设计（五层链，缺一层就静默失效）

| 层 | 实体 | 关键约束 |
|---|---|---|
| 契约 | `AgentDeviceKinds` / `AgentDeviceReadingDto(Kind, Key, Name, …)` | `Kind` 白名单；四种设备**共用一个 DTO**，每种只填自己那一两个字段 |
| 采集 | `WindowsHostMetricsSource`（PDH 逐实例） | 实例名是**唯本身份**：`PhysicalDisk(*)` = `0 C:`、`GPU Engine(*)` = `pid_…_luid_…_engtype_3D`。`_Total` 必须剔。⚠️ `Processor Information(*)` 的第二段是**组内逻辑处理器序号**（**不是插槽号**，2026-10-13 更正）——8 线程的机器会给出 8 个实例，当成 8 颗 CPU 就是在页面上撒谎 |
| 命名 | `TelemetryCollector.NameDevices` | **不猜型号**：只有该种类**唯一**时才用清单型号覆盖；多路时保留 `物理磁盘 0（C:）` 这类占位名。**显卡是例外**（需求②，2026-10-14）：`GPU Engine(*)` 只给 LUID，所以按 LUID 去**翻译成真实型号**（`GpuAdapterNames`：DXGI 首选 + D3DKMT 兜底；软件光栅器 WARP 剔掉）。两条路都失败才退回 `GPU 适配器 N` |
| 存储 | `AgentMetricEntity.DevicesJson`（迁移 **`M4_8_AgentDeviceReadings`**） | 一列 JSON 而不是子表：这张表 60 行/秒，子表会让写入放大 5–10 倍 |
| 聚合 | `AgentMetricsAggregator.Accumulator` | 按 `(Kind, Key)` **分别**开一组 Series；命名取桶内**最后**见到的那个 |

**🔑 两条一旦违反就静默出错的纪律**：

1. **`Key` 必须是硬件的稳定身份，绝不能用显示名** —— 否则用户重命名一次网卡，
   曲线上就是"旧网卡下线 + 新网卡上线"，**断成两截**，而硬件一块没变。
   （`cpu0` / `disk0` / 网卡适配器 `Id`(GUID) / 显卡 `luid_0x…_0x…`）
2. **`Devices` 为 null ≠ 空列表** —— null = "这段没有逐设备数据"（日聚合层 / 老客户端），
   前端据此说人话；空列表会被渲染成一条**贴着 0 的直线**，看起来像"这块硬盘一直不干活"。

**前端**：`AgentDetailPanel.vue` 的「历史曲线」区块改为
**上方硬件选择器（按种类分组，每格显示型号名 + 当前值）+ 下方单张曲线**；
曲线数据由 `devicePoints` 把选中设备的值**摊到与整机同形的扁平字段**上，
于是逐设备曲线白捡了 `MetricsChart` 那套"**null 必须断开折线**"的画法，两处不会各自演化。
纵轴量纲随种类变：CPU / 显卡钉死 0–100，磁盘 / 网卡按数据自适应。

### 28.5 验证（2026-10-13）

- `dotnet build` **0 警告 / 0 错误**；`dotnet test` 全量 **Core 792 / Agent 90 / 集成 349 = 1231，0 失败**
  （上一轮基线 783 / 78 / 342 = 1203）。
- 前端 `vue-tsc --noEmit` + `vite build` 退出码 **0**。
- 新增守门用例（要点）：
  - Core：`Bucketize_averages_each_device_on_its_own_key`（多路不揉成一条）、
    `Bucketize_keeps_two_devices_of_the_same_kind_apart`、
    `Bucketize_uses_the_last_name_seen_for_a_device`（**Key 而非 Name 才是身份**）、
    `Bucketize_leaves_devices_null_when_no_sample_carries_them`、
    `Daily_to_points_carries_no_per_device_readings`。
  - 集成：`Device_readings_round_trip_through_storage`（6 块硬件原样往返）、
    `Unusable_device_readings_are_dropped_at_ingest_without_losing_the_frame`（坏记录不连累整机指标）、
    `More_devices_than_the_cap_are_truncated_at_ingest`（上限 64，截断而非整帧丢）。
  - 迁移：`MigrationTests` 断言 `agent_metrics.DevicesJson` = **可空 TEXT**（新装 + 老库升级两条路径）。
- **冒烟（`agent-smoke.py`）新增两段**：
  - ④.6 `GET /api/agents/{id}/received` —— 真客户端跑起来后，端点上必须能看到
    `udpListening=true` / `udpPort` 与刚下发的配置一致 / 留痕里同时有
    **`ignored`（演练模式：认出了没执行）** 与 **`blocked`（本机黑名单拒绝）**，且**报文原文原样保留**。
  - ③ 遥测段追加逐设备断言 —— 真机上五层链（采集 → 命名 → 上报 → 落库 → 聚合）必须端到端跑通：
    点里 `devices` 非 null、至少含 `disk` 与 `net`、每条都有稳定 `key`、
    且**非本种类的字段保持 null**（不串味、不拿 0 冒充）。
- ⚠️ **重建 `dist/agent` 是跑这两个场景的前提**（见 §25.8 的教训：旧 agent 会让新断言红得莫名其妙）。

### 28.6 本轮**没做** / 挂账

- **日聚合层不聚合逐设备**：查 7 天 / 30 天时较早那段只有整机曲线，页面已明说原因（不画空线）。
  要做就得在 `agent_metrics_daily` 再加一列 JSON，代价是每天一行也要存 5–10 块硬件的历史 ——
  **没有实际需求不付这个代价**。
- **Linux 侧逐设备**：`Platform.Linux` 目前只报整机指标，逐设备留空（`Devices = null`）。
- 采样上限 **64 台/帧**（客户端与服务端同名常量）：一台机器暴露上百个计数器时截断，需要再谈。
- **性能未做量化**：需求⑤的"打开盘慢"只做了**成因层面**的修复（不再逐盘取卷标），
  没有在真实慢盘 / 断连网络盘上量过秒表。

### 28.7 ⚠️ 本文档中已被本节取代的旧叙述

- §27.3 **需求②** 那句"新增 `AgentGpuSummaryDto(Name, Count)`"：显卡改由硬件清单呈现，
  **不再有**专门的显卡摘要 DTO。
- 凡把历史曲线描述成"CPU/内存、磁盘/网络、GPU、温度**四张固定图**"的段落（主要在 §24、§26）。
- **权威现状**：`REF-M4-Agent.md`。

### 28.8 🔥 收口复查：三个**只有真机 / 真浏览器**才暴露的缺陷（同日补）

§28.5 那句"已落地"当时是基于**全绿的测试**写的。随后的真机与真浏览器验证推翻了它的一半 ——
三个缺陷全部通过了编译、单测与桩测试，且**症状都伪装成"这台机器没采到数据"**（完全正常的一种形态）。

| # | 缺陷 | 为什么测试抓不到 | 修法 |
|---|---|---|---|
| 1 | `PdhGetFormattedCounterArray` 缺 `CharSet.Unicode` ⇒ 绑到 `…ArrayA`，实例名是 ANSI 乱码 ⇒ 上层静默跳过 ⇒ **磁盘与 CPU 一条都不剩** | 结构体布局与 W 版**一样**，**值全对**、只有名字坏了；不抛异常、不给错误码 | 加 `CharSet.Unicode` + 真机守门用例 |
| 2 | 把 `Processor Information(*)` 的 `组号,处理器号` 当成**插槽** ⇒ 1 插槽 / 4 核 / 8 线程的机器被报成 **8 颗物理 CPU** | 编译期与桩测试都看不出"8"是错的；它是**在页面上说假话**，不是精度问题 | `GetLogicalProcessorInformationEx(RelationProcessorPackage)` 建映射再聚合（⚠️ 该枚举 **Package = 3 不是 0**） |
| 3 | `AgentMetricsSummaryDto` 不带逐设备读数 ⇒ 实时帧 `devices: null` ⇒ 硬件「当前值」与硬件曲线**永远停在打开页面那一刻** | 单看前端是"这段没数据"的合法形态；而它**正好违反契约里明令禁止的那种漂移** | 摘要加 `Devices`（末位可选），`Summarize` 透传，前端 `devices: m.devices ?? null` |
| 4 | **修第 3 条时自己引入的**：逐设备读数进了在线表 JSON，而入站**没挡 NaN / ±∞** ⇒ 一个坏读数从"只毁自己那条曲线"升级成 **`GET /api/agents` 整个序列化失败**（一台坏客户端拖垮所有人的列表页） | 与当年温度那次同形态（见 `AgentMetricsAggregator.Temperature` 的注释），只是换了字段 | `SanitizeDevices` 六个数值字段过 `Finite()` → null（只丢非有限，**不套物理区间**：速率没有有意义的上下界）；`Series.Add` 再挡一次（幂等，同时补上整机侧） |

**顺带修掉两条"假通过"的断言** —— 它们正是上面第 1、3 条溜过去的原因：

- `Disk_and_cpu_devices_are_actually_reported` 只断言磁盘**存在**，没断言读写**有值**。
- UI 验证的「当前值不是空的」用的是 `value !== '—'`，而磁盘那格显示的是 **`读 — / 写 —`**
  （整串确实不等于 `—`，于是通过，屏幕上一个数都没有）。判据已改为**串里出现数字**（`/\d/`）。

> **通用教训**：写"这个字段不为空"的断言时，先问清楚**这个项目里"空"长什么样**。
> `—`、`null`、`0`、`读 — / 写 —` 四种形态各出现过一次，每一个都曾让断言在功能坏掉时照样变绿。

**收口后的验证**：`dotnet test` 全量 **Core 796 / Agent 90 / 集成 356 = 1242，0 失败**；
`vue-tsc --noEmit` + `vite build` 退出码 0；
`.tools/m48-ui-check` 真浏览器验证 **25/25**（其中一条日志明确写着
"打开页面时这段历史**还是全空的**" —— 历史全空而格子上有真数，证明数只可能来自实时帧）。

---

## 29. 实施记录：M4.9「温度默认勾选 / GPU 型号 / 清单版面 / 子按钮地址 / UDP 实况 / 浏览提速 / 主机列表」**已落地**（2026-10-14，#236–#247）

> **逐条实现、决策留档、全部坑位**：见 `REF-M4-Agent.md` **§27**。这里只放**口径变化**与**两条必须记住的领域规则**。

### 29.1 三处**口径变了**（旧文档写反了，以本节为准）

| 主题 | 旧口径 | 现口径（2026-10-14） |
|---|---|---|
| 硬件温度默认值 | 「可选能力、**默认关闭**」 | **安装向导那个勾选框默认勾上** ⇒ 装完就是开的；不要就取消勾选 / `-NoThermal` / `\THERMAL=0`。⚠️ **"配置缺失 = 关"没变**（绿色版 / 手工起 exe）—— 默认值在**安装侧**，客户端代码里没有它 |
| `install-agent.ps1 -ThermalEnabled` | 打开温度 | **已废弃**（默认就是开的 ⇒ 空操作）。保留只为不破坏既有批处理 |
| GPU 命名 | 「**不猜型号**」，多路时保留 `GPU 适配器 N` | GPU **要翻译成真实型号**（需求②）：`GPU Engine(*)` 只给 LUID ⇒ 按 LUID 查型号（DXGI 首选 + D3DKMT 兜底，剔软光栅器）。两条路都失败才退回占位名。其余种类**仍然不猜型号** |

⚠️ 顺带更正一处**错事实**：`Processor Information(*)` 的第二段是**组内逻辑处理器序号**，
**不是插槽号**（8 线程机器会给出 8 个实例；直接当 CPU 数会把人引去找 8 颗 CPU）。

### 29.2 两条必须记住的领域规则

1. **UDP 有两种状态，别混**：**配置态** `udp.enabled/port`＝"**我要它**怎样"；
   **实时态** `host.udpChannel`＝"**它现在**怎样"（客户机上报）。
   把前者当后者，就是"点了没反应却两边都没错"的根源。`null` ＝ **还没上报**，与"明确说没在听"是两件事。
2. **SignalR 的实参个数必须与签名严格一致** —— C# 默认参数**完全不生效**。
   所以给既有 Hub 方法"加个可选参数"= 让**老客户端直接调用失败**；新增上报一律**独立方法**。

### 29.3 本轮最贵的一课：**假 FAIL 比不做断言更坏**

三次断言事故（两次假 FAIL、一次假通过）**没有一次是产品缺陷**，
但每一次都足以让人去改一段**本来就是对的**代码：

- 把需求①读成"客户端不带配置也该默认开" ⇒ 裸起 exe 时断言必红（代码是对的）；
- 轮询的退出条件只看"对象非空" ⇒ 客户机还没重绑就收手 ⇒ 两条断言双红；
- 温度说明的断言只判 `bool(note)` ⇒ 一句**指错方向**的假话照样通过。

**收口后的验证**：`dotnet build` **0 警告 0 错误**；`dotnet test` **Core 796 / Agent 98 / 集成 385 = 1279，0 失败**；
`vue-tsc --noEmit` + `vite build` 退出码 0；
冒烟 `--only agent` **143 PASS / 0 FAIL**（含新增 10 条**安装脚本静态检查**，并做了变异测试证明其有效）；
`.tools/m49-ui-check` 真浏览器验证 **53/53**；全量冒烟 `run-all.py` **9/9 场景通过**。

## 30. 实施记录：M4.10「未连接必开 UDP + 托盘菜单收敛 + 本机页服务端卡片」**已落地**（2026-10-14，#248–#254）

### 30.0 需求与决策

| # | 需求 | 落地 |
|---|---|---|
| ① | **未连接到服务端时，必须打开 UDP 监听** | UDP 通道的"该不该听"改成**两个决定者**：本机总开关 > 是否连着中心。**未连接 ⇒ 一律监听**（忽略配置里的 `enabled=false`，端口沿用配置） |
| ② | 托盘右键菜单**只留「客户端服务：运行中」与「打开本机配置」**，本机配置页里显示服务端信息 | 托盘菜单 4 项（+ 保留数据/日志目录）；本机页新增「服务端」卡片 |

四个当场确认的语义（写进代码注释，后续按此执行）：

1. 未连接时**一律监听**，端口沿用配置（没配置用 8090）；
2. **被吊销也算未连接 ⇒ 也打开**（接受"被吊销 ≠ 失去 UDP 控制"）；
3. 被移除的入口**全删**（服务端网页 / 启停服务 / 退出托盘），**数据目录与日志目录保留**；
4. 本机页做**完整一条「服务端」卡片**（地址 / 连接状态 / 名称 + ID / 最后通信 / 版本 / 配置同步状态 + 打开网页按钮）。

### 30.1 🔥 这条需求揭开的是一个**真缺陷**

改版前 `UdpCommandListener.Apply` 只有两个调用口子：**本机有配置时**（`RestoreFromStore` 成功触发 `Applied`）
与**连上中心之后**。一台**从没连过中心、本机也没有配置**的新机器，**两个口子一个都走不到** ——
`Apply` 一次都没被调用过，`netstat` 里连 8090 都不存在。

**现场形态**：机器装好了，往 8090 发 `DOWN` 毫无反应，而机器上、中心上都不报错。
"装完就能用"这句话在此之前是**断的**。

### 30.2 新的判定规则（表）

| 条件 | 结果 |
|---|---|
| `Agent:Tasks:Enabled=false` | **不听**（总开关，压过一切，含"未连接必须开"） |
| 未连接 + 配置 `enabled=false` | **听**，端口 = 配置里的端口 |
| 未连接 + 配置端口越界 | **听**，端口回落 8090（并留一句 warn），**不是**拒绝监听 |
| 未连接 + 没有配置 | **听**，8090 |
| 已连接 + 配置 `enabled=false` | **不听**（配置说了算） |
| 已连接 + 端口越界 | **不听**，并说明原因（原语义不变） |

端口**沿用配置**是刻意的：现场发送脚本指着固定端口，断一次中心就换端口等于把脚本一起弄坏。

### 30.3 托盘菜单（收敛后）

```
客户端服务：运行中          （信息行，走 SCM 查）
────────────────────
打开本机配置                （唯一的功能入口；双击图标也是它）
────────────────────
打开数据目录
打开日志目录
```

⚠️ **两个后果，已接受但必须讲清**：

- 托盘**不再是启停服务的入口** —— 用 `services.msc` / `sc` 操作；
- **没有"退出托盘"菜单项** —— 要停只能 `taskkill`（不带 `/F`，那条路仍是优雅停机：
  平台层把 `WM_CLOSE` 翻译成退出命令）。

移出去的信息（连没连上中心、连的是谁、会话侧通道）**没有丢**：
前两者搬进了本机配置页的「服务端」卡片，悬停提示里仍有一句"客户端连没连上"。

### 30.4 本机配置页的「服务端」卡片

一行一个事实，缺什么画 `—`（**空值绝不显示成 0 或空串** —— 那会把"还没跟中心说上话"
伪装成"中心那边没这项"）：

```
服务端地址 / 本机名称 / 本机 ID / 最后通信 / 服务端版本
配置版本 / 配置来源 / 配置最后写入 / 同步状态 / 客户端版本
                          [打开服务端网页]
```

- 「打开服务端网页」**点击时现取一次地址**再开（页面可能几小时前就打开了，那时还不知道地址）；
- `最后通信` 由心跳刷新（每跳一次），**断线后它就停住** —— `—` 与"停在某个时刻"都能一眼读懂；
- 为它给状态文件加了 `ServerVersion` 与 `LastSeenAt` 两个字段（旧文件仍能读，参数带默认值）。

### 30.5 验证（#248–#254）

- `dotnet build` **0 警告 0 错误**；`dotnet test` **Core 796 / Agent 104 / 集成 385 = 1285，0 失败**；
- **变异测试**：把"未连接 ⇒ 强制监听"那条分支短路掉 ⇒ 4 条新用例立刻变红 ⇒ 断言有牙齿；
- 冒烟 **`--only agent` 159 PASS / 0 FAIL**（新增 **G 段「离线客户端独立可用」** 16 条），退出码 0；
- **全量冒烟 9/9 场景通过**；交付产物（`dist/agent`、`dist/win-x64`、`dist/win-x64-service`、`dist/agent-setup`、`dist/installer`）
  全部重建，前端真重嵌经 `verify-dist.sh` 校验一致；
- 详见 `.workbuddy/memory/REF-M4-Agent.md` §28。

## 31. 实施记录：M4.11「移除客户机之后加不回来」修复（2026-10-14，已落地）

### 31.0 症状

管理员在网页的客户端列表里点「移除」，那台机器**再也回不来** ——
页面上永远不再出现，**重启客户机上的 Agent 也没用**。
唯一的现场绕法是去那台机器上删掉本地状态目录（等于手工丢掉凭据）。

### 31.1 根因：两个洞，缺一不可

| # | 位置 | 旧行为 | 后果 |
|---|---|---|---|
| ① | `AgentHub.Heartbeat` | 查不到这台机器时，`?.` 短路让"已吊销"判定变成 false ⇒ 走正常分支，照常 `MarkSeen` 并回 ack | 服务端已经忘了它，客户端却以为自己在线；长连接变僵尸，**永不再注册** |
| ② | `AgentRuntime.EnsureRegisteredAsync` | "本地凭据里的 serverUrl + machineKey 与当前一致"⇒ 直接复用，不再注册 | 被删掉的记录让这把 `agentKey` 必然 401，而客户端把 401 当普通连接失败 ⇒ **拿废钥匙无限重试**（退避到 60 秒封顶） |

两者都是同一个形状：**"查不到"被当成了"一切正常"**。

### 31.2 修法

- **服务端**（`AgentHub`）：心跳与上报类方法在"库里已经没有这台机器"时 **`Context.Abort()`**
  —— 强制断开，让客户端不得不重连。依据是那份在线表快照（启动时装载 + 每 60 秒刷新，
  刷新失败沿用旧快照，注册成功立刻补进去）⇒ **"库里有的机器不在快照里"只可能是刚被删的**。
- **客户端**（`AgentRegistrar` + `AgentRuntime`）：连接失败后**拿一次普通 HTTP 请求问中心**
  "还认不认这把钥匙"（`GET /api/agents/{id}/config`，只有 401/403 才算被拒），
  被拒则**作废本地身份并重新注册**。
  ⚠️ 刻意**不**去猜 SignalR 异常里的状态码 —— 那依赖内部实现，不成立时会静默失效。

### 31.3 与"吊销"的关系（用户决策）

决策：**被吊销的客户机也一样自动回来**，服务端不做"移除 / 吊销"的区分。
于是：被移除 ⇒ 立刻以**新 Id** 回来；被吊销（离线）⇒ 也立刻回来；
被吊销（在线）⇒ 走既有的 `Revoked` 消息 + 5 分钟退避后回来。

⇒ 「吊销」变得更弱了，但那是**本来就存在**的性质（免凭据准入下重新注册必定复活，
`AgentEnrollmentService` 已写明"要真正下线一台机器，去那台机器上卸载"）。
要给"永久封禁"，需要真凭据，属新需求。

### 31.4 验证

- 冒烟新增 **H 段「移除之后加得回来」**（8 条断言），`--only agent` **167 PASS / 0 FAIL**；
- **变异测试**：单独关掉服务端 Abort ⇒ H 段 FAIL；单独关掉客户端清身份 ⇒ H 段 FAIL
  ⇒ **两个半边各自都是必需的**；
- 全量测试 Core 796 / Agent 104 / 集成 385 = **1285，失败 0**；构建 0 警告 0 错误。

详见 `.workbuddy/memory/REF-M4-Agent.md` §29。

## 32. 实施记录：M4.12 五项需求（2026-10-15，①–④ 已落地，⑤ 未实施）

### 32.0 需求与决策

| # | 需求 | 状态 |
|---|---|---|
| ① | 本机页可改所连服务端 IP/端口（已连接时先上报再保存） | **已落地**（口径：**只通知、不阻塞保存**） |
| ② | 取消吊销 → 改为拉黑（拉黑不连接；取消后立即恢复） | **已落地**（拒绝连接 + 客户端保留身份重试；「移除」保留） |
| ③ | 主机列表不显示版本、取消「重启服务」，新增唤醒/关机/重启 | **已落地**（唤醒＝全部网卡 MAC + 两个广播地址） |
| ④ | 客户机不上报硬件清单的 BUG（疑似重新注册后） | **已落地**（根因见下） |
| ⑤ | 组合按钮（多个主按钮组合 + 段间延迟 + 触发执行） | **已落地**（同日补上，见 §33） |

### 32.1 需求④的根因（本批唯一一个"真 BUG"）

清单排期由客户端**本地持久化**的"上次上报时刻"决定，周期 6 小时；而记录被移除 /
重新注册换了新 Id 之后**服务端是白纸**，客户端却不知道 ⇒ 最长 6 小时不再上报。
修法：`AgentWelcomeDto` 带 `NeedsInventory`（默认 false，老服务端行为不变），
服务端按"我这边有没有这份清单"如实下发，客户端收到即**忽略排期立刻补报**。

### 32.2 需求②：拉黑与吊销的三处差别（这就是"真围栏"的全部内容）

1. **不清 agentKey**（旧吊销清）⇒ 取消拉黑后**下一次重试就连上**，不必重新注册；
2. **注册入口也拒**（旧吊销会被重新注册复活）⇒ 客户机无法靠自己绕过管理员的决定；
3. 客户端收到后**什么都不清**（旧吊销清身份/配置/任务）⇒ 保留本地状态，只进"被拉黑"状态按 15 秒轮询。

⚠️ 拉黑**不影响 UDP 指令通道**（按"未连接"处理，照旧监听）—— 与吊销同一口径。

### 32.3 需求③：唤醒为什么只能在服务端做

目标机器是关着的 —— 它连不上中心、收不到任何指令。只能由服务端发以太网广播帧，
让网卡在"主机关机、网卡待机供电"时认出魔术包。**对每一块上报过的网卡 MAC 各发一遍**，
每块发往 `255.255.255.255` 与该机最后来源 IP 的定向广播（对 /24 的假定），端口 9 与 7 都发。
WoL **无应答**，所以返回 `ok` 只代表包已发出（这句话写在响应里）。

### 32.4 验证

- 构建 **0 警告 0 错误**；`dotnet test` Core **796** / Agent **104** / 集成 **386** = 失败 **0**；
- `npm run typecheck` + `npm run build` 通过；内嵌本机页 JS `node --check` 通过；
- EF 迁移 `M4_12_AgentBlock`（只做列重命名），迁移数守门用例 12→13；
- 冒烟 `--only agent`（含 H 段「移除后加得回来」、B 段「拉黑」）通过；
- 守门用例：`A_blocked_host_cannot_register_again`（被拉黑连注册都拒，取消后能进门）、
  `A_removed_host_comes_back_as_a_new_record`（取代旧的"吊销会复活"）。

详见 `.workbuddy/memory/REF-M4-Agent.md` §30。


## 33. 实施记录：M4.13 组合按钮（2026-10-15，需求⑤ 已落地）

> §32（同日的上一批）里这一项只定了口径、没有实现；本节是它的落地记录。

### 33.0 形态（用户已确认）

**第三类瓦片** `ComboButton`：与主按钮 / 子按钮集同级、**共用全局 `Order`**（三类可以互相穿插）；
成员**只引用主按钮**（不含子按钮集）；**相邻两个成员之间**可设延迟（毫秒，第一个之前不等）；
顺序可上移 / 下移；触发＝按顺序执行成员的全部动作并逐段等待；**支持对接码**。

### 33.1 延迟语义（这一条最容易理解错）

`ComboButtonStep.DelayMs` ＝ **「执行完本步之后、下一步之前」等待的毫秒数**。三个直接推论：

| 推论 | 为什么 |
|---|---|
| 第一步的延迟恒为 0 | 前面没有东西可等 |
| **最后一步的延迟永远不会生效** | 后面没有下一步 ⇒ 清洗器**强制归零**、解析时再归零、界面**不显示**这个框（填了也没用） |
| 与成员动作的「前置延迟」不是一回事 | 两者各算各的：成员内部仍是「第 1 条不等待」，段间延迟在上一段**整个执行完**之后才开始 |

⚠️ 上限 **60 秒**：组合跑在「点击 / 对接码」这条**有人在场**的链路上，填 10 分钟会让人以为「点了没反应」。

### 33.2 为什么成员是「引用」而不是「动作副本」

复制动作会让同一份行为在两处各存一份：改了按钮却忘了改组合，现场表现是
「我明明改了这个按钮，组合里还是旧行为」。引用则天然跟随。
代价是「按钮被删了」要处理 —— 清洗器会剔除悬空成员**并记一条说明**（不静默吞掉），
执行前再算一遍（防「匹配时还在、执行时被删」）。

### 33.3 执行：分段，不拍平

`ActionExecutionService.ExecuteComboAsync` **逐段复用** `ExecuteAsync`，不把成员动作拍成一条长列表：

1. 每个成员要有**自己的 runId / 日志** —— 现场排障时「是哪个按钮出的问题」必须一眼可见；
2. 成员的失败**不中断**后续成员（与「单按钮内某条失败不中断」同一口径）；
3. 段间延迟与成员内部的前置延迟互不干扰。

⚠️ **「没有动作的成员」照样占一段**（记为 `skipped`）：少一段会让「3 个成员只回 2 段」没法解释。

### 33.4 对接码：与主按钮**同级**、按 Order 合并扫描

主按钮与组合按钮**合并成一个按 `Order` 排序的序列**再逐个比对 —— 界面上谁靠前谁先命中。
⚠️ **不是**「先扫完所有按钮、再扫组合」。因此 `PairingMatchKind` 的**枚举数值不再等于优先级**，
旧注释已改写（不改写的话，下一个改这块的人一定会按老注释理解错）。

### 33.5 验证

- 构建 **0 警告 0 错误**；`dotnet test`：Core **808** / Agent **104** / 集成 **389** = 失败 **0**；
- **全量冒烟 9/9 场景通过**（跑的是本次重建的 `dist/` 产物）；交付产物五套全部重建；
- 冒烟 `pairing`（**真 UDP 回环**）**5/5 通过**，新增
  `⑤ 组合按钮级命中（按成员顺序执行两个按钮，段间延迟真的生效）`，**量了间隔 ≥100ms**（配置 150ms）；
- 新增守门用例：`ComboButtonTests`（纯逻辑 8 条）、`PairingDispatchServiceTests` +3、
  `ActionsApiTests` +3（含 `comboId` 与 `buttonId` 互斥 ⇒ 400、未知 `comboId` ⇒ 404）、
  `ConfigBackupServiceTests` +1（组合按钮导出再导入不丢）。

### 33.6 顺带修掉的一个真缺陷

测试逮到：`ConfigBackupService.KnownSettingsFields` 是**手写的配置字段清单**，
加了 `comboButtons` 却没同步 ⇒ 导入含组合按钮的配置时界面会报
「**已忽略字段：comboButtons**」（数据其实没丢，但那句话会让人以为组合全丢了，
还会把真正该提醒的单机版字段淹掉）。
已改成**从 `ControlSettings` 反射生成** —— 「加了字段却忘了登记」这一类 bug 从根上消失。

详见 `.workbuddy/memory/REF-M4-Agent.md` §31。

---

## 34. 实施记录：M4.14「本机页服务端明细默认收起」（2026-10-15，已落地）

> 用户原话：**「新增客户端 服务端信息默认为收起状态 仅显示最上方的已连接中心及地址」**。
> 详见 `.workbuddy/memory/REF-M4-Agent.md` §32。

### 34.0 需求

本机配置页（`local-ui.html`，内嵌资源）的「服务端」卡片一进来就把十行信息全摊开；
日常只看一句"连没连上、连的哪个地址"，其余九行是排障时才翻的。

### 34.1 改法（一处文件，零 C# 改动）

| | 内容 |
|---|---|
| **摘要行（常显）** | 「已连接中心/未连接中心」徽标 ＋ **地址** ＋ 展开/收起开关 |
| **明细区（默认 `hidden`）** | 十行信息表、同步说明、改中心地址的输入框与说明 |

三条纪律：

1. 收起状态**只由 `serverOpen` 一个变量说了算**，渲染函数**不碰 `hidden`** ——
   否则每几秒一次的状态刷新会把用户刚展开的明细按回去；
2. **收起不能把故障一起收进去**：连不上时 `statusText`（被拉黑 / 在线表满 / 正在广播找）
   留在摘要行；读配置失败也直接改写摘要行；
3. 没连上时退而显示"要连的地址"必须**带「（目标）」后缀**，不能冒充成"已连的"。

### 34.2 验证

- 内嵌 JS `node --check` 通过；
- `agent-smoke.py` G 段新增 **4 条结构断言**（用 `html.parser` 建"id → 祖先 id"表），
  证据取自**跑着的客户端真返回的页面**；并另有一份变异测试证明这 4 条**各自都咬得动**
  （`.tools/check-collapse-assertions.py`，5/5）；
- `--only agent` **172 PASS / 0 FAIL**；全量 `run-all.py` **9/9**；
- 交付产物重建：`dist/agent`（36.3 ＋ 35.9 MiB）＋ `dist/agent-setup`（61.0 MiB，
  SHA256 `60F08AEE…FF27`），已补投到 `%ProgramData%\ControlServer\downloads\`。

### 34.3 本轮踩到的坑（都进 REF §32.4）

1. **别用二进制 grep 验单文件 exe 里的内嵌页面**（`EnableCompressionInSingleFile=true` 会压掉）⇒ 假 FAIL；
2. **`check()` 在 PASS 时也照印 `detail`** ⇒ detail 必须写中性证据，否则读成假 FAIL；
3. **"不在明细里"对"元素不存在"也会通过** ⇒ 存在性必须单独断言；
4. 抽代码做变异测试的工具第一版**切片为空 ⇒ 全绿却什么都没跑** ⇒ 加守卫。

---

## 35. 实施记录：M4.15 修复「中心页面的关机 / 重启毫无效果」（2026-10-15，已落地）

> 用户原话：**「客户端列表右侧的关机和重启功能无法使用（无效果），但远程指令有效」**。
> 详情见 `.workbuddy/memory/REF-M4-Agent.md` §33。

### 35.0 根因（一句话）

`AgentCommandNames.Power` **契约写了、实现没写**：常量注释写着「服务侧执行」，
但中心的 `power` 指令到了客户端**没有任何人处理** ——
实现只长在 UDP 通道里，SignalR 那条路是空的（`AgentServiceHost` 的 switch 里没有它，
落到 default ⇒「收到未知指令（已忽略）」）。

于是"UDP 明文指令有效、页面按钮无效"—— 用户描述的两半同时对上。
同一个洞还覆盖 `volume`（页面音量按钮，同样无效果）。

### 35.1 修法

抽一层**共享执行器** `AgentCommandExecutor`（UDP 与中心指令共用同一份实现）：

- 电源 → 服务侧（`IHostPowerController`）；音量 → 会话侧（`ISessionCommandChannel`）；
- 返回执行结论（不抛），由调用方写进「接收到的数据」；
- `AgentRuntime` 收到中心的 `power` / `volume` 后**就地执行并写真实结论**，
  不再写"已转交本机指令处理"（那句话正是把这次故障掩盖掉的元凶）。

⚠️ **不采取**"在宿主的 switch 里补一个 case"：两条通道两份实现必然漂，执行的真相只能有一份。

### 35.2 三处语义决策

1. **演练模式（`Agent:Tasks:ExecuteUdpCommands=false`）是全局闸** —— 中心的关机指令也被它拦下
   （安全闸不能留漏勺；键名沿用历史，语义是"本机此刻敢不敢动手"）；
2. **留痕写结论**，不写"已转交"；
3. **服务端文案**：成功时补上"是否真的执行，请看那台机器的「接收到的数据」"，
   并在客户端上报了演练模式时追加提示 —— **下发 ≠ 执行**。

### 35.3 验证

- 新增 `AgentCommandExecutorTests` **12 条**；集成测试 +2（400 / 409）；
- 🔥 **冒烟先红后绿**：旧产物跑出 `[FAIL] …['收到未知指令：power（已忽略）']`，
  重建产物后 `[PASS] 指令真的走到了本机执行器… []`；客户端跑在演练模式 ⇒ **全程没有真关机**；
- 全量 `run-all.py` **9/9**（576 PASS / 0 FAIL）；五套交付产物全部重建。

### 35.4 待补

- **真关机**没有端到端验证（冒烟不敢真关一台机器）—— 验证到"走到了执行器"为止；
- 音量已修，但**会话侧托盘不在时**的表现只能真机看。

---

## 36. 实施记录：M4.16 修「同一硬件在历史曲线里出现多个选项」（2026-10-15，已落地）

### 36.0 症状

「历史曲线」的硬件选择器里，显卡 `NVIDIA GeForce GTX 1060 6GB` **6 条同名**、
网卡 `Hyper-V Virtual Ethernet Adapter（vEthernet (Default Switch)）` **6 条同名**。
现场口径：**同一个硬件不该出现多个选项**；若本意是"同一硬件的不同资源占用"，
就该画在同一张图上、用**勾选**筛选。

### 36.1 根因

反查真实库（`DESKTOP-1UKRNGI`，962 条遥测）：那台机器**只装了 1 块 GTX 1060**，
却在一天里重启 6 次 ⇒ 留下 **6 个两两不重叠的 LUID**（每次重启紧接一次 uptime 归零）；
网卡同理留下 5 个不同的 `Id(GUID)`，名字**一模一样**。

🔥 **`LUID` 与 Hyper-V 虚拟网卡的 `Id` 都是运行时标识符，系统每次启动重新分配。**
而硬件列表取的是"**窗口内出现过的所有 Key 的并集**" ⇒ 同一块硬件被数成 6 块。

（磁盘 `disk0/1/2` 与 CPU `cpu0` 的 Key 重启后稳定，所以那两组本来就对。）

### 36.2 改法（前端）

新增纯函数模块 `web/src/deviceIdentity.ts`：身份改为 `kind` + **展示名**；
**只有同一帧内同名 ≥2 条**才加 `#n`（那才是真的多卡）；列表**每个种类取它最后出现的那一帧**
（回看 3 帧）；取值按当时活着的键（**不把同组的键相加**）。

交互由**单选**改为**勾选**：同类勾中多块画在同一张图，颜色认硬件、线型认通道。

### 36.3 验证

- `web/tools/check-device-identity.mjs` **19 PASS / 0 FAIL**（样本为真实 9 帧）；
  条数 **17 → 7**；
- 🔥 **变异测试 3/3**：把身份退回 Key ⇒ 6 条断言变红（证明核心被钉住）；
- `npm run typecheck` + `npm run build` 通过；服务端产物重建。

### 36.4 已知代价

改名会让旧名字那一代从列表消失（但**数据仍画得出来**，取值走 Key）；
同型号且都认不出型号的两块卡，重启后编号可能对调；真并存的多卡**不合并**。

详见 `.workbuddy/memory/REF-M4-Agent.md` §34。

---

## 37. 实施记录：M4.17 修「明明在线却提示离线」+ 离线仍尽力下发（2026-10-15，已落地）

> 用户原话：**「出现了客户端在线，但在使用关机、重启等功能时提示客户端不在线离线，无法下发指令的提示。
> 需要修复明明在线却提示离线的 BUG；另外即便客户端离线，仍需要向该客户端发送相关指令，但提示离线。」**
> 详情见 `.workbuddy/memory/REF-M4-Agent.md` §35。

### 37.0 根因（一句话）

**"在线"与"能不能下发"是两套判据**，所以必然互相打架：

| 用途 | 判据 | 位置 |
|---|---|---|
| 页面显示"在线" | 只看**最后心跳距今多久**（10 / 30 / 75 s） | `AgentPresence.Classify` ← `AgentHostRegistry.ToDto` |
| 下发指令的闸门 | 只看**内存里有没有 SignalR 连接 Id** | `AgentHostRegistry.ConnectionIdOf` ← `AgentsController.SendCommandAsync` |

两者在"长连接已经断了、但最后心跳还很新"的窗口里**一定**矛盾：

1. `MarkDisconnected` 会把"最后心跳"**钉成断开那一刻**（为了给离线计时一个确定起点）
   ⇒ **断开后的头 30 秒页面照样是绿的**，而下发闸门此刻已经变成 null；
2. **中心一重启**，内存连接表清空，而库里 `LastSeenAt` 可能只落后几秒 ⇒ 页面绿、下发必然 409；
3. 客户机重连退避是 **1→2→4→…→60 秒**（`AgentRuntime`）⇒ 这个"假在线"窗口能长达几十秒。

### 37.1 修法（两条，都是用户拍板的取舍）

**① 让"在线"与"能不能下发"同源**：把"中心是否知道它挂着连接"这条事实喂进状态判定
（`Classify` 新增 `bool? longConnection`）：

- `true` 连接在 —— **时间判定照旧**（僵尸连接仍要变黄 / 变灰，不能把故障藏在绿点后面）；
- `false` **已知连接断了 ⇒ 不可能判成"在线"**，最多"不稳定"；
- `null` 不知道（防御性，沿用旧行为）。

配套：`RefreshAsync` 装载库里的主机时**给每台建一条"本次运行期"的内存记录**
（`GetOrAdd`，**绝不覆盖已有的连接 Id**）—— 否则"中心刚重启"会落到"不知道"上，
那半边的假在线又会回来。

**② 离线不再等于"拒绝下发"**：`SendCommandAsync` 在长连接不在时改走 **UDP 指令通道兜底**
（新增 `SendWhileUnreachableAsync`）—— 它正是 M4.6 那条"长连接断了但机器还在时唯一够得着的路径"：

- **端口**：客户机自报的实时态优先（"它现在怎样"），没有才退回配置端口（"我要它怎样"）并说明是配置态；
- **token**：新增 `UdpCommandProtocol.TryFormat`（与 `TryResolveCommand` **成对**，
  保证"这里写出去的"必然"客户机认得出"）；
- 返回 **200 + `status:"udp"`**，消息里**明写"该客户端当前离线"**；
- 只有"连 UDP 也够不着"（不知道地址 / 无从判断端口 / 这条指令不在 UDP 支持之列）才回 409。

### 37.2 三处边界（顺手补掉的洞）

1. **拉黑的机器不再接受任何指令**（409 `blocked`）：UDP 兜底恰恰是"不建连接也能控制"的后门，
   不挡住，"拉黑"就成了半拉 —— 一台被拉黑的机器照样能被中心关机；
2. **`/reboot`（重启 Agent 服务）没有 UDP 兜底**：UDP 只支持关机 / 重启整机 / 音量，
   它必须由 Agent 自己在长连接上处理 ⇒ **仍然 409**，但提示语指向正确方向（"只能经长连接下发"），
   而不是让人照着"查地址 / 开机"去排查；
3. **`/config/redeliver` 同样不兜底**：断线时把"去拉配置"发过去毫无意义；
   配置留在中心、按**版本号**自愈，所以这句话要说清"不会丢"。

### 37.3 验证

- **单测**：`AgentProtocolAndPresenceTests` **+8**（"已知断开的连接不可能在线"、"时钟快 + 断连仍是黄"、
  "连接在**不**额外放宽时间阈值"、"`null` 沿用旧行为"、拉黑压过断连、从未连接仍是"等待连接"）、
  `UdpCommandProtocolTests` **+9**（含 **TryFormat ↔ TryResolveCommand 往返一致**）；
  Core 全量 **124 PASS / 0 FAIL**。
- **集成 +4**：离线但地址已知 ⇒ **真发到回环 socket 并收到 `DOWN`**、连接断掉后**不再显示在线**、
  **中心重启后装载的主机不显示在线**、拉黑机器拒发；集成全量 **396 PASS / 0 FAIL**。
- **前端**：`npm run typecheck` 通过；关机 / 重启按钮**离线不再禁用**（只有"已拉黑"禁用），
  文案改成"将改走 UDP 指令通道尽力发出，是否生效要看那台机器"。

### 37.4 已知代价 / 挂账

- 🔸 **客户端短暂断开重连时会一闪而过黄点**：这是诚实的（那一刻中心确实发不出指令），
  但会让人以为"网络在抖"。若现场嫌吵，可考虑给"连接已断"一个几秒的宽限（**未做**，先看真实反馈）。
- 🔸 **UDP 兜底没有应答**：页面说"已发出"时，报文可能被防火墙拦、也可能被客户机的演练模式拦下。
  返回语已写明"是否生效请看那台机器"，但**"到底关没关"仍然只能去机器上看**。
- 🔸 **排队补发没做**（用户本轮明确选"尽力而为"）：真正够不着的机器（关机 / 不在网）指令会直接作废，
  不会等它上线后再执行。

---

## 38. 实施记录：M4.18 修「从客户机回传到仓库失败」（2026-10-15，已落地）

> ⚠️ **本节描述的第二版设计（默认可读整盘 + `OutboxDirectories` 收窄开关）已被 §40 取代**：
> 用户随后要求把回传白名单机制**整个删掉**。本节保留为演进史（"为什么会有那个开关"），
> **不要照它去配置 `OutboxDirectories`** —— 那个键已经不存在了。

> 用户原话：**「文件传输优化：从客户机回传到仓库失败，需要修复」**。
> 详情见 `.workbuddy/memory/REF-M4-Agent.md` §36。

### 38.0 根因（一句话）

**「浏览」能看到的范围（整盘）与「回传」能读取的范围（一个私有目录）是两个集合。**

M4.7 把目录浏览放开了（`TransferBrowseService` 顶层 = `DriveInfo.GetDrives()`，**整盘**），
但**回传的读取白名单没跟着改** —— 它默认仍是 `{状态目录}\outbox`
（`TransferPaths.ReadWhitelist` 的旧兜底）。于是在网页上：

- 「浏览」能一路点进 D 盘、能选中 `D:\BaiduNetdiskDownload\unity项目测试.zip`；
- 建单后客户机**立刻拒绝**：`源路径不在本机白名单目录内：D:\…（允许：C:\ProgramData\ControlServer.Agent\outbox）`。

**这不是偶发**：只要用户不是在 `{状态目录}\outbox` 里放文件，回传就 100% 失败。
证据取自中心服务端日志 `C:\ProgramData\ControlServer\logs\log-2026-10-15.ndjson`
（**同一条路径连撞 5 次**，任务 #5–#9 全部同一原因；随后用户改用「下发」才成功 —— #10 已完成）。
而这两个集合没有任何一处会告诉用户"它们不一样"。

### 38.1 决定（用户已确认）

<div align="center">

| 方案 | 取舍 | 结论 |
|---|---|---|
| **A. 默认可读 = 整盘**（与浏览同一集合），`OutboxDirectories` 保留为收窄开关 | 回传开箱即用；代价是客户机上任意文件都能被取走 | ✅ **用户选它** |
| B. 保持严格，改成"选之前就拦住"（浏览只导航到可读目录内） | 安全姿态不变；但用户要取的那个 zip **仍然取不到** | ✗ |
| C. 默认整盘但排除网络盘 / 光驱 | 与浏览范围又不一致，同类事故会以更隐蔽的形式回来 | ✗ |

> 判据沿用 §27.4 已经拍过的那条：**「浏览」既然已经是整盘，安全边界就落在"传输落地"那一步**。
> 只放开浏览、不放开回传，等于把边界挪了一半 —— 用户看到的是"能选、选完必失败"。

### 38.2 读与写刻意做成不对称

| 方向 | 默认范围 | 为什么 |
|---|---|---|
| **写**（下发落地） | `{状态目录}\inbox` **一个目录** | 中心**单方面**让客户机落一个文件，客户机事前不知道要落什么 ⇒ 必须窄（防任意写 / 覆盖系统文件） |
| **读**（回传读取） | **整盘**（所有盘符根） | 中心**点名**取某一个文件，而且运维**已经能在浏览里看到它** ⇒ 白名单挡不住任何真实威胁，却挡住了全部正常使用 |

**收窄开关只有 `Agent:Transfer:OutboxDirectories` 一个。**
刻意**不**拿 `InboxDirectories` 当收窄开关：那两个是**写**的设置，让"写"顺带把"读"掐掉
是一条没人猜得到的隐式耦合 —— 而本次事故的根子恰恰就是"两处语义悄悄分叉"。

### 38.3 🔥 顺带修掉一个潜伏的致命缺陷：盘根当前缀会整盘失效

要让"可读 = 盘符根"成立，先得让 **`FileTransferMath.IsInside` 认盘根**：

- `Path.TrimEndingDirectorySeparator("C:\")` **刻意不裁根** ⇒ 归一化后盘根**仍以分隔符结尾**；
- 旧实现在比较时**无条件再补一个分隔符** ⇒ 前缀变成 `C:\\` ⇒
  `"C:\foo".StartsWith("C:\\")` = **false**。

也就是说：**任何把盘根写进白名单的配置，都是"写了等于没写、而且比没写更糟"**
（`{状态目录}\inbox` 落在盘内所以一直没暴露）。修法是"根自带分隔符时直接拿它当前缀"。
这条本身与"回传"无关，是**路径比较**层的缺陷，顺手一并钉死。

### 38.4 唯一实现（防再次分叉）

盘符枚举收进 **`TransferPaths.DriveRoots()`**，`TransferBrowseService` 改为调用它 ——
**「浏览看得见」与「回传读得到」从此是同一个集合、同一份代码**。
本次事故的正因就是这两处各写了一遍枚举，且只改了其中一处。

### 38.5 验证

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln` | **0 警告 / 0 错误** |
| `dotnet test` | **Core 845 + Agent 122 + 集成 396 = 1363 通过 / 0 失败**（新增 `TransferReadScopeTests` **6 条** + Core `FileTransferMathTests` **2 条**） |
| `deploy/smoke/run-all.py --only transfer`（**真服务端 + 真客户端**） | **74 / 0**，1/1 场景通过。关键三条：`上报了可读目录 = ['c:\','d:\']`（**默认可读＝整盘**）、**旧默认之外的文件也取得回来**（新加的 ⑤b，正是本次事故那条路径）、**收窄后越界仍被拒**（新加的 ⑥，客户机回报"源路径不在本机白名单目录内：…"） |
| 新增用例覆盖 | 默认可读整盘、**可读根集合 == 浏览列出的盘符**、配置后真收窄、只配写不构成收窄、**越界仍是 fatal**、**`UploadAsync` 真的越过白名单**（失败点推进到"建上传会话"） |
| 变异验证 | 把 `ReadWhitelist` 兜底改回旧值 ⇒ Agent 新用例**4/6 变红**；把 `IsInside` 前缀改回旧写法 ⇒ Core 新用例**2/2 变红**（不是"顺手都过"） |
| 前端 | `npm run typecheck` + `npm run build` 通过（`index-m5_F8jaW.js`）；回传方向文案改为"默认可读整盘，与浏览一致" |

> ⚠️ **旧数字已被本节取代**：`deploy/smoke/transfer-smoke.py` 从 **68** 条变成 **74** 条
> （删掉"默认配置下越界被拒"那一条 —— 默认整盘之后它永远不可能成立；
> 换成 ⑤b 整盘可读 + ⑥ 收窄后越界被拒，外加清单断言由 1 条拆成 2 条）。
> §23.6 与 `REF-M4-Agent.md` §21 里写的 `68 / 0` 是 **M4.4 当时**的口径，未回改。

### 38.6 代价 / 挂账

- 🔸 **可读范围默认 = 整盘**：客户机上的任意文件都能被中心取走。前置门槛是
  `AdminOnly` + 授权闸门（`POST api/distributions/collect` 归 Gated）+ 运维**显式点名一个文件**，
  且"目录结构"在 M4.7 就已经可见了。**要严格边界的部署方必须显式配 `OutboxDirectories`** ——
  这一点要写进部署说明。
- 🔸 **未做**：把"本机可读范围"在分发页做成更显眼的提示（当前只在候选区与一行小字里）；
  网络盘 / 光驱的默认取舍（本轮选了"与浏览完全一致"，不额外排除）。
- 🔸 **现场需重装客户端**：改动全在客户机侧（`TransferPaths` / `FileTransferMath`），
  **只重启中心不生效** —— 必须把新客户端装到那台机器上。

---

## 39. 实施记录：M4.19 修「下发到客户机的路径保存不了」（2026-10-15，已落地）

用户原话：**「文件下发到客户机的路径无法保存的问题，保存后需要能看到具体路径」**。

### 39.0 根因（一句话）

**点「记为此机默认目录」确实存进库了，但紧接着的那一次建单又把它抹掉了；
而"存进去了"这件事在屏幕上没有任何表现。**

两条链路各自都对，合起来是"保存成功、然后马上被删除"：

| # | 缺陷 | 位置 |
|---|---|---|
| ① | 记住之后，**那一行的输入框不更新** —— 用户看不到刚保存的路径 | 前端 `DistributionPanel.onPathRemembered` |
| ② | 随后一次**"存放路径留空"的建单会回写空串** = 把刚记住的目录删掉 | 后端 `DistributionsController.RememberPathsAsync` |

### 39.1 现场证据（服务端日志，不是猜的）

`%ProgramData%\ControlServer\logs\log-2026-10-15.ndjson`：

```
16:47:08  客户端默认目录已记忆（Id=11，操作者 admin）
16:47:18  客户端默认目录已记忆（Id=11，操作者 admin）   ← 连点三次（屏幕上什么都没变）
16:47:43  客户端默认目录已记忆（Id=11，操作者 admin）
16:47:49  新建下发任务 #10：陕西项目清单(ww).xlsx → 1 台客户机，落地目录 (默认)
                                                    ↑ 这一单把记忆写成了空串
18:28:xx  GET /api/agents ⇒ 客户机 11 的 lastTargetDir = ""   ← 记住的目录不见了
```

三点互相印证：**连点三次**（说明"点了没反应"）、**建单用的是默认目录**（说明输入框里是空的）、
**之后记忆变成空串**（说明被建单回写冲掉了）。

### 39.2 为什么输入框是空的（①）

路径记忆是"**勾选时物化**"进那一行的：

```ts
watch(() => selectedIds.value, (ids) => {
  for (const id of ids) {
    if (id in targetDirs) continue          // ← 只物化一次
    targetDirs[id] = lastTargetDirOf(id)
  }
})
```

而「记为此机默认目录」此前只更新了 `remembered`（**供下一次物化的覆盖层**），
**没有动已经物化好的那一格**。于是：勾选（此时还没记过 ⇒ 填的是空串）→ 浏览 →
点「记为此机默认目录」→ 屏幕上**什么都不变** → 用户以为没存上（连点三次）→
建单发出去的仍是空串（= 客户机默认目录）。

### 39.3 改法

**① 前端（`DistributionPanel.onPathRemembered`）**：记住之后，把**这一台那一格**也填上。

⚠️ 不能无条件覆盖 —— 那格里可能是用户**手输的另一个目录**（本次就用它）。
只在"这格还是空的"或"它正好等于记住之前的那个记忆值"（= 没人动过）时才填。

**② 后端（`RememberPathsAsync`）**：`?? string.Empty` 改成 **`IsNullOrWhiteSpace ⇒ continue`**。

> **留空只是"这一次不定目录"，不该有删掉记忆的副作用。**
> "这次用客户机默认目录"这个意图已经由任务本身（明细 `TargetDir = null`）表达了；
> 而"删记忆"是一个破坏性的、屏幕上完全看不见的动作 —— 它正是本次事故的成因。

集齐之后，`上次落脚目录` 的语义变成：**只有"确实指定了目录"的建单才会改写它**。

### 39.4 验证

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln` | **0 警告 / 0 错误** |
| `dotnet test` | **Core 845 + Agent 122 + 集成 399 = 1366 通过 / 0 失败**（新增 `DistributionApiTests` **3 条**） |
| 新增用例 | ① 留空建单**不再冲掉**记忆；② 指定目录时记忆**跟着更新**、且不动回传方向那一格；③ 回传记的是**父目录**、且不动下发方向 |
| 变异验证 | 把 `RememberPathsAsync` 改回 `?? string.Empty` + `dir is null` ⇒ 新增用例**立刻变红**（`DistributionApiTests.cs:578`） |
| 真服务端 + 真实接口 | 记住 `D:\播放内容\2026` → 建"留空"的单（明细 `targetDir = null`，正确）→ 再读 `lastTargetDir` ⇒ **仍是 `D:\播放内容\2026`** ✅；再建一单指定 `D:\素材\2026-10` ⇒ 记忆更新为新值、`lastSourceDir` 未被动 |
| 前端 | `npm run typecheck` + `npm run build` 通过（`index-BqHihaHg.js`） |

### 39.5 代价 / 挂账

- 🔸 **"清空记忆"现在没有 UI 入口了**：从前"留空建单"顺带把记忆抹掉，如今不再。
  要真正清除得调 `PUT /api/agents/{id}/path-memory`（`targetDir: null` 表示"不动"，空串才会清）。
  这是刻意的取舍：**让"这次不定目录"和"永久忘掉这个目录"不再共用一个动作**。
- 🔸 记住的路径只是**预填值**，不是权限：它仍会被客户机**可写目录白名单**再校验一次
  （默认只有 `{状态目录}\inbox`）。要真的落到 `D:\播放内容`，得在那台客户机上
  配 `Agent:Transfer:InboxDirectories` —— 越界时明细行会红字写"目标目录不在白名单内"。
- 🔸 前端改动需重发 `dist/`（`wwwroot` 是编译期快照）；**服务端改动只需重启中心**
  （这一处全在服务端）。

---

## 40. 实施记录：M4.20 取消回传白名单机制（2026-10-15，已落地）

> 用户原话：**「取消文件回传的白名单机制，不限制目录」**。
> 详情见 `.workbuddy/memory/REF-M4-Agent.md` §38。

### 40.0 一句话

**回传（读）从"有范围"改成"没有范围"** —— 删掉校验、连同它的配置项一起删，
并同步删掉会因此变成**假绿**的旧断言（§40.4）。

### 40.1 改了什么

| 层 | 文件 | 改动 |
|---|---|---|
| 客户机 | `FileTransferClient.ResolveSourcePath` | **删掉范围校验**；只保留"非空 + 必须是绝对路径 + 能解析"。改为 `static` |
| 客户机 | `FileTransferClient` | 删掉 `ReadWhitelist` 属性与 `BuildReadWhitelist()`（只剩可写那一份） |
| 客户机 | `TransferPaths` | **删除 `ReadWhitelist()`**；`DriveRoots()` 保留（浏览顶层 + 上报候选） |
| 客户机 | `TransferOptions` | **删除 `OutboxDirectories`**（那个收窄开关）；类注释改为"读无白名单" |
| 客户机 | `Program.cs` 启动日志 | `可读 全部目录（回传不限制，2026-10-15 起）` |
| 客户机 | `AgentRuntime` 清单上报 | `TransferOutboxDirectories` 改报 **`DriveRoots()`**（仅供页面提候选） |
| 服务端 | `AgentTelemetryDtos` | 字段注释改为"可访问盘符 / 候选提示，**不是限制**"（字段名沿用，不动线协议） |
| 服务端 | `AgentsController` | 浏览端点注释：`path` 留空 = 顶层（盘符），**不存在"白名单根"** |
| 公共库 | `FileTransferMath.IsInsideAny` | **删除**（唯一消费者就是那段被删的校验）；`IsInside` 仍被"下发落地"用着 |
| 前端 | `DistributionPanel.vue` | 候选区与说明文案改为"盘符提示（仅供参考，不是限制）" |
| 前端 | `types.ts` | `transferOutboxDirectories` 注释同步 |

**唯一没跟着放开的边界**：下发落地（写）仍然是白名单，默认仍是 `{状态目录}\inbox` ——
用户要取消的是"回传"，不是"下发"。**这两者不要再一起动。**

### 40.2 为什么不留一个"想收窄就配"的开关

因为那正是上一版（§38）的问题：**开关留着，就会有人以为配了生效、也会有人以为不配就不安全。**
本项目对这类"配了等于没配 / 看着放开了其实没放开"的陷阱是零容忍的（同一纪律见
`TransferOptions` 里那条"命令行传列表必须带 `:0`"）。所以：

- **要么有边界、要么没有** —— 现在选"没有"，并且写清楚边界退化到哪一层（§40.3）；
- 将来若要恢复限制，**必须连同 `ResolveSourcePath` 的校验一起设计**，
  不允许只把配置项加回来（那会造出一个静默无效的开关）。

### 40.3 边界退到哪里去了（**必须向部署方讲清**）

```
回传的边界 = 谁能建这张单
           = AdminOnly + 授权闸门（POST api/distributions/collect 归 Gated）+ 运维显式点名一个文件
```

等价于"**管理员能读这台机器上的文件**"，与他直接坐到那台机器前是同一量级 ——
而"目录结构可见"这件事在 M4.7（整盘浏览）就已经发生了。
**需要把数据挡住的产品级需求，请用操作系统手段**（ACL / 磁盘加密 / 域策略），
**不要再指望本产品的目录白名单** —— 那条路今天正式关掉了。

### 40.4 删掉了两条会变成"假绿"的断言

改动会让两处既有验证**自动失效**（不删就会留下永远为真的假绿）：

| 位置 | 为什么失效 | 处理 |
|---|---|---|
| `deploy/smoke/transfer-smoke.py` ⑥ | "换一个配了 `OutboxDirectories` 的实例重起，断言越界仍被拒" —— 配置项与"越界"概念都没了 | **整段删除**，原地留注释说明为何删（改成弱断言不如删掉诚实） |
| `tests/.../TransferReadScopeTests.cs` | 6 条全部围绕 `ReadWhitelist` / 收窄开关 | **整文件重写**为"任意绝对路径可读 + 路径合法性"（见 40.5） |

`deploy/smoke/browse-e2e.py` 里那行 `--Agent:Transfer:OutboxDirectories:0=…` 也一并删除
（否则客户端会因未知配置键而**启动失败**——那比断言失败更难查）。

### 40.5 验证

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln` | **0 警告 / 0 错误** |
| `dotnet test` | **Core 844 / Agent 122 / 集成 399**：Core + 集成 **0 失败**；Agent **117 通过、5 失败**，5 条全是 `UdpCommandListenerTests`，原因是**本机真实安装的 Agent 服务（PID 3908）占着 UDP 8090**（`Get-NetUDPEndpoint -LocalPort 8090` 证实），与本次改动无关（详见 40.6） |
| 重写用例 | `TransferReadScopeTests` **6 条**：任意无关目录 / **系统目录**（`…\drivers\etc\hosts`）/ **另一个盘** 都能过；相对路径、空路径、文件不存在仍是**致命**错误 |
| 变异验证 | 把 `ResolveSourcePath` 改回"只读一个私有目录" ⇒ 新用例 **4/6 立刻变红**（剩下 2 条在变异点之前就被拦下） |
| 端到端（真服务端 + 真客户端） | `run-all.py --only transfer` **70 通过 / 0 失败**；`上报了可访问盘符 = ['c:\','d:\']`、`旧默认之外的文件也取得回来`、`整盘回传任务标记为 completed` ✅ |
| 前端 | `npm run typecheck` + `npm run build` ✅（`index-LsRx_SA5.js`） |

> 冒烟条数从 74 降到 70：少的 4 条正是 40.4 里被删掉的 ⑥ 段断言（不是回归）。

### 40.6 顺手发现：Agent 测试套件对"本机装了 Agent"不免疫

`UdpCommandListenerTests` 的 5 条会去**真绑 UDP 8090**，而产品自己也绑这个端口。
本机既然装了 `ControlServerAgent` 服务（开机自启），这 5 条就必然红：

```
监听 8090 端口失败：通常每个套接字地址(协议/网络地址/端口)只允许使用一次。
```

- **不是本次改动引起的**：`netstat`/`Get-NetUDPEndpoint` 指向 PID 3908 = `ControlServer.Agent.Service`（真实服务），
  上一轮（18:5x 之前）这套用例还是 122/0，差别只是那台机器上**此刻有真 Agent 在跑**。
- ~~**未修**（不在本轮范围）：正解是让用例用**临时端口**（配置注入一个随机端口）而不是固定 8090，
  这样测试就与"本机是否装了产品"解耦。**留给 M6 加固收口**，先把事实记下来。~~
  ✅ **已修**（用户回单：「修复这一条」）—— 见下面 **§42**，不必再等 M6。

---

## 41. 实施记录：M4.21 「未勾选也要看得见落点」（2026-10-15，已落地）

> 用户原话：**「在不勾选客户机的情况下，需要能看到客户机的文件下发路径」**。
> 详情见 `.workbuddy/memory/REF-M4-Agent.md` §39。

### 41.0 一句话

新建分发任务里，目标客户机那一行的「存放路径」**不再只在勾选后才出现** ——
未勾选时**只读显示该机当前会落到的目录**，勾选后的输入框初值**与它逐字相同**。

### 41.1 用户看到的问题

那一格在未勾选时写的是「勾选后才能指定」。而"**各台机器分别落到哪**"恰恰是
**决定该勾哪几台**的依据（批量下发时各台落点常常不同）—— 把最该先看的东西
藏在一个**必须先点一下**的复选框后面，等于逼用户先盲勾、再逐台取消。

### 41.2 取值优先级（**唯一事实源 = `landingPathOf()`**）

| 档 | 来源 | 界面标记 | 含义 |
|---|---|---|---|
| ① | `agent_hosts.LastTargetDir`（服务端路径记忆） | **上次** | 上一次给它下发用过的落脚目录 |
| ② | 客户机上报的 `transferInboxDirectories[0]` | **默认** | 任务不带目标目录时的落点（= `FileTransferClient.ResolveInboxDirectory` 的 `allowed[0]`） |
| ③ | 两者都没有 | **未记录** | 照实说，不编一个路径出来 |

### 41.3 本轮真正修掉的缺陷（**真机验证当场抓出来的**）

第一版只做了"显示"（`dirPreviews`），**物化逻辑仍走旧的 `lastTargetDirOf`**。后果：

```
一台从没被下发过的机器：屏幕上写「默认 C:\…\inbox」，勾上之后输入框却是空的
```

空串在协议里表示"用客户机默认目录"，**行为其实是对的**；但**同一件事在屏幕上说了两遍、
还说得不一样** ⇒ 用户只会得出"这个路径没生效"的结论。
这与 §39 修过的那个病是同一个（"两种说法并存"），所以处理方式也一样：**两处同源** ——
`targetDirs[id] = landingPathOf(id)`。

**代价（必须讲清）**：「客户机默认目录」那一档会被**显式写进输入框**并随任务发出去。

- 值的语义**完全等价**（客户机的默认目录就是它白名单的第一项，显式写它照样过校验）；
- 但它**不再随白名单变化自动跟随**；
- 且这次下发会把它**记成这台机器的"上次目录"**（下次那一行就显示"上次"而不是"默认"）。
- 想回到"跟随默认目录"：**把输入框清空**即可（空串的语义仍在）。

### 41.4 顺带：小机群预取清单

②档来自客户机上报的清单，而清单是**一台一个请求**。因此：

- 机群 **≤ `DIR_HINT_MAX_AGENTS`（12）** ⇒ 把整个列表的清单预取回来（未勾选的行才有"默认"可显示）；
- 超过 12 ⇒ **只按"已勾选"取**（沿用原有理由：上百台为了一格提示发上百个请求不划算，
  而那种规模的现场早已逐台下发过，每台都有 `lastTargetDir` 直接可显示）。

### 41.5 改了什么

| 层 | 文件 | 改动 |
|---|---|---|
| 前端 | `web/src/components/DistributionPanel.vue` | 新增 `landingPathOf()` / `landingWhenceOf()` / `dirPreviews`；未勾选那一格改为**只读路径**（`上次 / 默认 / 未记录`）；**物化 watch 改用 `landingPathOf`**；表头与说明文案；小机群预取清单 |
| 前端 | 同上（`<style>`） | `.dir-view` / `.dir-whence` / `.dir-path`（截断 + `title` 给全文） |

**服务端、客户机、线协议：零改动。** 用的都是既有字段（`lastTargetDir` / `transferInboxDirectories`）。

### 41.6 验证

| 项 | 结果 |
|---|---|
| 类型检查 / 构建 | `vue-tsc --noEmit` **0 错**；`vite build` ✅ `index-BjLLLOL0.js`（`.css` → `index-DLCyGG5Z.css`） |
| **真机端到端（UI 级）** | 真服务端（源码态 ⇒ 读磁盘 `wwwroot`）＋真客户机＋**无头 Edge（CDP）驱动真实 DOM** ⇒ 场景一 **18 通过 / 0 失败**，场景二 **13 通过 / 0 失败** |
| 场景一 `default`（客户机启用传输、上报默认目录） | 未勾选显示「默认 `<路径>`」且**没有**输入框 ⇒ 勾选后输入框 = **同一路径**；`PUT /path-memory` 后重载 ⇒ 显示「上次 `D:\播放内容`」⇒ 勾选后输入框 = **同一路径** |
| 场景二 `notrecorded`（客户机关闭传输 ⇒ 上报空表） | 未勾选显示「未记录…」⇒ 勾选后输入框为空（空 = 默认目录，与屏幕上那句话一致） |
| **变异验证** | 把物化改回 `lastTargetDirOf`（= 41.3 那个缺陷）⇒ **恰好那一条断言变红（17/1）**，其余 17 条全绿 —— 证明它抓的是真东西，不是恒真式 |
| 截图 | `%TEMP%\cs-ui-verify\{default,notrecorded}\*.png` |

> ⚠️ 为什么分两次跑：客户机身份是**机器码**（硬件派生），同一台电脑上起两个客户端进程
> 只会得到**同一个主机**（服务端只认机器码）⇒ "两种上报状态"只能各起一次实例。

### 41.7 交付注意

- 🔸 **纯前端**：源码态下重启中心后**刷新页面**即生效；但 `dist/` 用的是**编译期快照** ⇒
  **要给现场用必须重发**（本轮 `dist/` **未重发**）。
- 🔸 ②档依赖客户机上报清单：**老客户端**（不上报）那一行会显示「未记录」——
  这是**照实说**，不是缺陷；升级客户端后自然就有了。

---

## 42. 实施记录：M4.22 「用例不再真绑 8090」（2026-10-15，已落地）

> 用户原话：**「修复这一条」**（回的是 §40.6 那条挂账：本机装了 Agent ⇒ 5 条用例变红）。
> 速查见 `.workbuddy/memory/REF-M4-Agent.md` §40。

### 42.0 一句话

`UdpCommandListenerTests` 里那 5 条**真的去绑 UDP 8090** 的用例，改成绑**当时的空闲端口**；
「生产那个回落端口确实是 8090」改由**一条只读属性、从不绑端口**的用例单独钉住。

### 42.1 复现（**先把故障变成可重复的**）

原来的复现前提是"这台机器恰好装着一个开机自启的 Agent" —— 那是不可控前提。
本轮先用一个 10 行的占位脚本把 8090 占住（`%TEMP%\cs-udp-occupy\occupy.py`，未入库），
于是"有真 Agent 在跑"这件事被**确定性地**复制出来：

| 状态 | `dotnet test` Agent 工程 |
|---|---|
| 8090 空闲（改动前） | **122 通过 / 0 失败** |
| 8090 被占（改动前） | **117 通过 / 5 失败** ← 与 §40.6 现场完全一致 |

红的正是这 5 条，一条不多一条不少：
`A_missing_config_defaults_to_enabled_on_the_default_port`、
`An_explicit_disable_still_leaves_no_port_behind`、
`Stop_closes_the_channel_even_though_the_config_says_enabled`、
`A_brand_new_machine_listens_before_it_ever_reaches_the_centre`、
`Not_connected_with_an_out_of_range_port_falls_back_to_the_default`。

它们的共同点一眼可见：**全都走"没有配置 / 端口越界 ⇒ 用默认端口"那条路**，
所以全都落在了那个写死的 8090 上。

### 42.2 为什么不能"直接把断言改成随机端口"

那会**把要守的语义一起改掉**。"没有配置 ⇒ 用 8090"这件事本身是有价值的
（现场发送脚本 `nc -u 主机 8090` 指着它，文档里也写着它），
只是"**验证**它"不该通过"**真的去占**那个端口"来完成。

所以拆成两半，各守各的：

| 要守的东西 | 由谁来守 | 会不会绑 8090 |
|---|---|---|
| 没配置 / 越界 ⇒ **回落到默认端口**（行为语义） | 那 5 条用例（改用**注入的空闲端口**） | **不会** |
| 那个默认端口**就是 8090**（生产接线 + 常量） | 新增 `The_default_port_is_the_one_the_field_scripts_aim_at`（**只读属性、从不 Apply**） | **不会** |

### 42.3 改了什么

| 文件 | 改动 |
|---|---|
| `agents/ControlServer.Agent/Tasks/UdpCommandListener.cs` | 构造函数新增**可选**参数 `int? fallbackPort = null`（不传 = `AgentConfigDefaults.DefaultUdpPort`）；新增只读属性 `FallbackPort`；`Apply` 里 3 处（回落取值 / 回落 warn 文案里的数字 / `wanted` 初值）由常量改为 `FallbackPort` |
| `agents/ControlServer.Agent.Service/Program.cs` | **只加注释**：第 5 个参数**刻意不传**（＝走生产默认值 8090），并写明"这里永远不要传" |
| `tests/.../UdpCommandListenerTests.cs` | `Harness` 新增 `useProductionDefaultPort`（默认 **false** ＝换空闲端口）＋ `FallbackPort` 便捷属性；5 条用例的断言与发包目标由 `AgentConfigDefaults.DefaultUdpPort` 改为 `harness.FallbackPort`；新增 1 条钉住生产默认端口的用例 |
| `docs/M4-…方案.md` | §40.6 从"未修（留给 M6）"改为"已修，见 §42"；新增本节 |

**生产行为零变化**：`Program.cs` 不传参数 ⇒ `FallbackPort` 就是 `AgentConfigDefaults.DefaultUdpPort`，
与改前逐字一致（这一点由 42.2 那张表第二行钉住）。**也正因为如此，本轮不需要重发客户端。**

> ⚠️ 为什么参数放在**构造函数**而不是配置里：它是个**测试缝**，不是给现场调的旋钮 ——
> 放进配置就等于多一个"能改默认端口"的入口，而它一旦被改，
> "文档写 8090、实际听在别处"就会在现场重演（本项目已经吃过一次这种亏，见 24.1）。

### 42.4 验证

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln` | **0 警告 / 0 错误** |
| `dotnet test ControlServer.sln`（**8090 全程被占**） | Core **844/0**、Agent **123/0**、集成 **403/0** —— **全绿** |
| `dotnet test` Agent 工程（**8090 空闲**） | **123/0** —— 两种机器状态都绿 |
| **变异 A**：把回落默认值写成 `?? 8091` | **恰好 1 条变红**（`The_default_port_is_the_one_the_field_scripts_aim_at`），其余 45 条绿 ⇒ 那条新断言抓的是真东西 |
| **变异 B**：让构造函数**忽略**注入值（＝退回旧行为） | 8090 被占时**恰好那 5 条变红** ⇒ 证明"这 5 条现在能过"确实是**注入**换来的，不是碰巧 |

用例总数 Agent **122 → 123**（+1 条钉生产默认端口）。

### 42.5 交付注意

- 🔸 **纯测试 + 注释改动**：不改线协议、不改服务端、不改前端，`dist/` **无需重发**。
- 🔸 那个占位脚本（`occupy.py`）只是**复现工具**，留在临时目录、**未入库** ——
  它存在的意义是：以后谁再怀疑"这组用例是不是只在没装产品的机器上才绿"，
  可以两分钟内把结论摆出来。
- 🔸 结论对**以后的测试**同样成立：**用例不许绑全局固定端口**（8090 / 8080 / 9998 都一样），
  要绑就绑"当时空闲的"，再单独钉住"生产那个值是几"。

---

## 43. 实施记录：M4.23「客户端离线的时间段，历史曲线直接留空」（2026-10-16，已落地）

> 用户原话：**「历史曲线优化：客户端离线的时间段，历史曲线直接留空」**。

### 43.0 一句话

空白段不再被折线**一条直线接过去** —— 服务端在空得够久的地方插一个**全 null 的断点标记**，
前端那套「null 必须断开折线」自然把它断开；再补一层浅底衬 + 一句说明，
免得"断线"被读成"图表坏了"。

### 43.1 根因：**"空桶跳过"只保证没有点，不保证断线**

`Bucketize` 一直有一条明确的规矩 —— **空桶直接跳过，不补 0**（补 0 会画出一条"掉到 0 又上来"的假曲线）。
这条规矩本身没错，但它只做到了一半：

| 做法 | 图上看到 | 有没有编数据 |
|---|---|---|
| 空桶补 0（早就被否） | 掉到 0 再上来 | **有**（假的掉底） |
| 空桶跳过（现状） | 前后两点**被直线连起来** | **也有**（假的平稳） |

第二种比第一种**更隐蔽**：补 0 至少看得出掉下去了，连直线看不出任何异常 ——
被控端关机两小时，图上是一条"这段时间 CPU 一直是 40%"的直线。

### 43.2 判据：`max(离线判定阈值, 2 × 分桶粒度)`

| 取哪一项 | 为什么 |
|---|---|
| `Server:Agent:OfflineAfterSeconds`（默认 **75 s**） | 与在线表上那颗**灰点**是同一个数。页面上显示"离线"的那段时间，曲线上就是空白 —— 两处不该各有一套说法 |
| `2 × 分桶粒度` | 分桶粗细随窗口变（1 小时窗 5 秒一格，30 天窗 1 小时一格）。没有这个下限，粗粒度窗口上"少了一格"就会被判成离线 |

⚠️ **为什么不取"少一格就算"**：采样 5 秒一条，且客户端在断线重连期间会把样本攒进下一批
（见 `AgentMetricsBatchDto`），偶尔丢一两格是**网络抖动**而不是离线。
拿它去断线，曲线上会布满密密麻麻的小豁口 —— 看着像图表坏了，真正的离线反而被淹在里面。

### 43.3 改了什么

| 层 | 文件 | 改动 |
|---|---|---|
| 契约 | `ControlServer.Contracts/AgentTelemetryDtos.cs` | `AgentMetricsPointDto.IsGap`（末尾、带默认值 = 兼容既有夹具）；`AgentMetricsSeriesDto.GapThresholdSeconds`（同上，随序列下发） |
| 纯函数 | `Core/Services/AgentMetricsAggregator.cs` | 新增 `GapThreshold` / `GapMarker` / `MarkGaps` / `MergeLayers` |
| 服务 | `Server/Services/AgentTelemetryService.cs` | `QuerySeriesAsync` 两条路径都标空白；`daily+raw` 走 `MergeLayers` |
| 前端 | `web/src/types.ts`、`MetricsChart.vue`、`AgentDetailPanel.vue` | 断点底衬 + 悬停"（这段没有数据）"+ 空白段说明；**实时帧也要判**（见 43.4） |

**`MarkGaps` 的三个约束**（都由用例钉住）：

1. **只标两段真实数据之间** —— 开头/结尾的空白不标。末尾尤其不能标，
   否则读数条的"最新"会变成一整排 `—`，看着像"这台机器此刻什么都没采到"。
2. **幂等** —— 先丢掉入参里已有的标记再重判。两段各有各的粒度/判据，将来若有人
   把它挪到更外层、让一段数据流过两次，结果不该变成"每次多一个标记点"。
3. **零副作用** —— 没有空白时返回**原列表**（不分配、不改顺序）。

### 43.4 两处最容易漏的地方

**① 接缝不是离线。** 长窗口的取数是"老一段读日聚合（每天 00:00 一个点）+ 最近一段读明细"，
两者在接缝处天然隔了将近一天。空白判据若跨着接缝用，**每一次查询**都会在同一个位置插一个假断点：
图上一条永远存在的豁口，还会随"现在几点"左右滑动，看着像离线，其实什么都没发生。
⇒ 由 `MergeLayers` **各判各的**（这条规则现在是个可单测的纯函数）。

**② 实时帧那条路也要判。** 详情页的曲线是"打开时拉一段历史，之后每来一帧往前挪一格"。
被控端离线十分钟再回来时，服务端只会照常推来一帧 —— 不判的话这一帧会被直线从十分钟前接过来。
更糟的是：用户点一下「刷新」（走服务端那条路）它就变回空白，**同一段时间两种画法**，
还取决于"有没有点过刷新"。⇒ 挪格时用同一个 `gapThresholdSeconds` 判，必要时先插标记点。

### 43.5 验证

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln` | **0 警告 / 0 错误** |
| `npm run typecheck` | 通过 |
| Core 单测 | **854 / 0**（新增 10 条；基线 844） |
| 遥测集成用例 | **32 / 0**（新增 1 条端到端：摄入 → 落库 → 查询 → **断点位置/两侧跨度/判据下发**逐条断言） |
| `Agent.Tests` / 集成全量 | 见 43.7 |

### 43.6 ⚠️ 附带发现（**未在本轮修**）：`daily+raw` 是一条**跑不到的路**

写"接缝不判空白"的用例时，必须先让查询真的走 `daily+raw`，
于是发现 **`QuerySeriesAsync` 里 `rawFrom` 的三元表达式**取的是 `min` 而不是 `max`：

```csharp
var rawFrom = to - span > to - rawSpan ? to - rawSpan : from;   // 恒等于 from
```

`from = to - span`，`to - rawSpan` 比它晚（当 `span > rawSpan`）⇒ 比较为假 ⇒ 取 `from`
⇒ `rawFrom <= from` **恒真** ⇒ **永远走明细路径，日聚合那段是死代码**。

- **证据**：一个 18.6 天的窗口（`2026-09-28 ~ 2026-10-16`）返回 `source = "raw"`、`bucketSeconds = 3600`
  —— 而不是文档 §6.4 说的"老的一段读日聚合"。
- **实际影响**：`rawSpan = MaxRawRows(200 000) × MetricsSeconds(5) = 11.57 天`，
  于是 **7 天窗不受影响**（604 800 行 < 20 万），而 **30 天窗（518 400 行）会被
  `OrderBy(AtMs).Take(200 000)` 截成"**最老**的 11.57 天"** —— 最近 18 天在图上没有点。
- **本轮为什么不顺手改**：改一行会让 30 天窗的取数方式整个变样（日聚合层第一次真正上线），
  而它撞上的是另一个既有设计（接缝那天 00:00 → rawFrom 之间**按设计就是空的**）。
  两件事都该单独一轮：**先定行为，再改那一行**。

### 43.7 交付注意

- 🔸 前端有改动 ⇒ **`dist/` 与 `wwwroot` 需重发**（本轮已重跑 `npm run build` + `dotnet build`）。
- 🔸 **无需重发客户端**：动的是服务端聚合与网页，线协议只多两个**带默认值**的字段。
- 🔸 老客户端 / 老服务端并存时，两个新字段缺失的语义都是"不判"（`IsGap = false` / `GapThresholdSeconds = 0`），
  页面退化成改动前的样子，不会报错。

---

## 44. 实施记录：M4.24 「回传还是被白名单拦住」＝**发出去的客户端包是旧的**（2026-10-16，已落地）

> 用户原话：**「修复一下这个BUG，不应该限制目录」**（附现场截图：回传 #17 失败，
> 「源路径不在本机白名单目录内：`D:\BaiduNetdiskDownload\unity项目测试.zip`
> （允许：`C:\ProgramData\ControlServer.Agent\outbox`）」）。
> 速查见 `.workbuddy/memory/REF-M4-Agent.md` §41。

### 44.0 一句话（结论先行）

**源码里早就不限制了** —— 10-15 的 M4.18/M4.20 已经把白名单连机制一起删掉（§38、§40）。
真正的问题是 **`dist/agent` 还是 10-15 `10:45` 那一批产物**，早于那两次修复；
于是从它装的机器（`DESKTOP-1UKRNGI`）**至今仍带着旧限制**。
修法＝**重新打包客户端 + 把那台机器换掉**。**代码一行没动**（只改了两处"说反了"的注释）。

### 44.1 证据链（四条，互相独立）

| # | 证据 | 说明 |
|---|---|---|
| ① | `logs\log-2026-10-16.ndjson` **14:48:38** | 「传输失败（任务 #17 / 客户机 11）：源路径不在本机白名单目录内…」—— 这句是**客户机回报上来的** |
| ② | 全仓搜这句文案 | **生产代码 0 处**（只剩用例/冒烟脚本里的注释在引用这条历史事故）⇒ 现在的代码**不可能**说出这句话 |
| ③ | `agent_hosts` 第 11 行 | `DESKTOP-1UKRNGI`，`RegisteredAt = 2026-10-15 10:09:39`；而 `dist/agent` 的时间戳是 **10-15 10:45** ⇒ 装的就是那一批 |
| ④ | 旧包实测 | 拿 `.tools/old-dist/agent-20261015-1045/` 跑冒烟 ⇒ **65 / 3**，报的字**一模一样**（见 44.4） |

> ② 是关键：它把"这是个还没修的代码缺陷"直接排除掉 —— 剩下的只能是**产物**问题。

### 44.2 真正修的是什么

| 项 | 动作 |
|---|---|
| `dist/agent`（2 个 exe） | **重新打包**（`deploy/windows/build-agent.ps1`）—— 本轮的**唯一修复动作** |
| `dist/agent-setup`（安装包） | **重新打包**（`build-agent-installer.ps1`），并**自动投放进中心下载目录** |
| `src/ControlServer.Contracts/DistributionDtos.cs` | `SourcePath` 的文档注释还写着"必须落在白名单目录内" ⇒ 改成"不再限制，只要求绝对路径" |
| `web/src/api.ts` | 同类：`createCollectDistribution` 的注释还写着"必须在白名单目录内" |
| `deploy/smoke/run-all.py` | 见 44.3 —— **让这次事故瞒过冒烟的那一环** |

⚠️ 那两处注释不是"顺手清理"：它们**说反了**。下一个读到它们的人会以为白名单还在，
于是要么照着它写校验，要么以为"回传本来就该被限制"。**过时注释比没有注释更危险。**

### 44.3 🔥 为什么"代码修了、包没更"能瞒过冒烟

`run-all.py` 解析客户机 exe 时，`dist/agent` 排在**仓库调试构建**前面。
而被测服务端是 `bin/Debug`（**最常见的一种跑法**）时，那条"同源"相对路径（`sibling`）
指向 `src\ControlServer.Agent.Service\debug\`（**不存在**）⇒ 照样一路落到 `dist/agent`。
于是实际测到的是：**现在的代码的服务端 + 上一次打包的客户端**。
而"上次这个包是什么时候打的"没人记得 ⇒ **客户端那半边的断言根本没碰新代码，却全绿**。

修法：按**被测服务端住在哪**决定先看谁 ——
住在仓库 `dist\` 下 ⇒ `dist\agent`（同一批交付产物）打头；否则 ⇒ 同一次构建的调试产物打头。
两边都留作候选，所以"只有一个存在"时行为不变。

**探针实测**（把 `run-all.py` 里真实的那个函数抠出来跑）：

| 被测服务端 | 解析到的客户机 exe |
|---|---|
| `src\ControlServer.Server\bin\Debug\net8.0\` | `agents\ControlServer.Agent.Service\bin\Debug\net8.0\` ✅ **同源**（改前会落到 `dist\agent`） |
| `dist\win-x64\` | `dist\agent\` ✅ |
| `.artifacts-check\bin\ControlServer.Server\debug\` | `.artifacts-check\bin\ControlServer.Agent.Service\debug\` ✅ |

### 44.4 验证（**before / after 打在同一对真产物上**）

| 客户机 exe | 结果 | 关键失败项 |
|---|---|---|
| **旧包** `…old-dist\agent-20261015-1045\`（10-15 10:45） | **65 / 3** | `旧默认之外的文件也取得回来`、`整盘回传任务标记为 completed`、`上报的盘符 = 本机全部盘符` |
| **新包** `dist\agent\`（10-16 15:04） | **70 / 0** | —— |

旧包那三行红字里，**客户机自己的启动自述就是答案**：

```
（旧包）文件传输：启用（并发 2 路）｜可写 …\inbox｜可读 …\outbox          ← 它只敢读 outbox
（新包）文件传输：启用（并发 2 路）｜可写 …\inbox｜可读 全部目录（回传不限制，2026-10-15 起）
```

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln` | **0 警告 / 0 错误** |
| `run-all.py --only transfer`（新包，跑了两遍） | **70 / 0**，1/1 场景通过 |
| 客户机 exe 解析探针 | 三种被测服务端位置各得其所（见 44.3） |

### 44.5 产物指纹（现场核对用）

| 文件 | 大小 | SHA256 前 16 位 |
|---|---|---|
| `dist/agent/ControlServer.Agent.Service.exe` | 36.9 MB | `D9AD0F125CCBD921` |
| `dist/agent/ControlServer.Agent.Tray.exe` | 36.4 MB | `F99E32569CB2151E` |
| `dist/agent-setup/ControlServer-Agent-Setup-0.1.0.exe` | 62.1 MB | `4D87F5551A96FE0D` |
| 中心下载目录里那一份 | 62.1 MB | `4D87F5551A96FE0D` ⇐ **与 dist 逐字节相同** |
| （对照）旧 `…Agent.Service.exe` 10-15 10:45 | 36.3 MB | `B57CB380034BA518` |

### 44.6 现场怎么收口（**这一步不做，现象一模一样**）

1. 那台机器（`DESKTOP-1UKRNGI`）上的客户端**必须换掉**：中心网页右上角「客户端安装包」直接下
   （44.2 已投放），或带过去 `ControlServer-Agent-Setup-0.1.0.exe`。**中心侧什么都不用动。**
2. 装完想确认"新不新"，看**客户机自己的日志**里那一行：
   `可读 全部目录（回传不限制，2026-10-15 起）`。
3. ⚠️ **别指望中心页面的「客户端版本」栏** —— 见 44.7。

### 44.7 ⚠️ 顺带发现的两个缺口（**未动，等拍板**）

1. 🔴 **版本号分辨不出新旧**：`Directory.Build.props` 的 `<Version>0.1.0</Version>` 是**全局**的，
   于是 `agent_hosts.AgentVersion` **每一台都是 `0.1.0`**。而这个字段自己的注释写着
   "与服务端版本比对，用于提示'安装包不一致'" —— 也就是说**它本来就是为抓这一次这种事造的**
   （"你这台装的客户端和中心不是一批的"），却因为版本号从不递增而**永远哑火**。
   本轮现场因此只能靠"人肉比时间戳"定案。
   可选修法：① 打客户端包时把版本第三位 +1；② 或者上报**构建时间戳**（比版本号更直白）。
2. 🔸 **`dist/` 没有任何"产物落后于源码"的检查**。本轮是靠比对时间戳发现的，
   换成别的场景未必有这么好运。可在打包时留一个 `dist/agent-build.txt`
   （源码时间 / 版本 / 产物哈希），或让 `run-all.py` 在客户机 exe **早于源码**时
   **打一句醒目警告**（警告，不是失败 —— 旧包有时就是被测对象）。

---

## 45. 实施记录：服务端晚于客户端启动时「永远连不上」**已修**（2026-10-20）

### 45.1 现场证据（客户机 `log-2026-10-20.ndjson`，一字未改）

| 时刻 | 日志 | 说明 |
|---|---|---|
| 09:15:21 | `服务端地址：未指定（将用 UDP 9998 广播自动发现）` | 安装参数里**没有**地址，本该走广播 |
| 09:16:07 | `连接中心服务端失败：…连接尝试失败。 (192.168.3.33:8899)` + `凭据探测没问出结果…20 秒` | 拿的是**旧网段**那个地址 |
| 09:16:49 / 09:17:34 / 09:18:19 / 09:19:08 / 09:20:05 | 同上，每轮约 42～48 秒 | **一次广播都没发过** |
| 09:20:09 | `已把本机连接的服务端地址改为 http://192.168.101.57:8899` | 人只能到本机页手改 |
| 09:20:57 | `注册成功：Id=14…` | **改完地址后 48 秒**才真的去连（主循环睡在退避里） |

结论：这不是"网络还没好"，而是**客户端把自己记住的地址当成了终结答案**。

### 45.2 四条根因（彼此独立，都不报错）

| # | 根因 | 旧行为 |
|---|---|---|
| 1 | 🔴 `ResolveServerUrlAsync` 里"记住的地址"是**终点** | 只要 `credentials.dat` 里有地址，`DiscoverClient` **一步都不走** ⇒ 中心就在同网段也永远发现不了 |
| 2 | 退避等待是裸 `Task.Delay` | 本机页改完地址要等退避睡满（实测 **48 秒**），用户以为"改了没用" |
| 3 | 每轮连接失败都白做一次凭据探测（HttpClient 20 秒） | 中心不可达时一轮 **40 多秒**；中心一起来，客户端一分多钟才上线 |
| 4 | `AgentRegistrar.RegisterAsync` 把 HttpClient 超时也 `throw` | 一个普通的"服务端不可达"窜成 Error 级「客户端主循环出错」+ 栈 ⇒ 现场像客户端有 bug |

### 45.3 修法（规则收在可断言的地方）

- **优先级**：本机页地址 > 安装参数（`Agent:ServerUrl`）> 凭据里记住的 > 广播发现。
- 🔥 **缓存地址连续失败 2 轮 ⇒ 必须去广播**；发现到<b>不同</b>地址就换过去（凭据属于旧中心，
  会重新注册），发现到的还是它 / 什么都没发现就继续用它。判定在
  `agents/ControlServer.Agent/Transport/ServerAddressResolver.cs`（纯函数，10 条用例）。
- 🔴 **人指定的地址（本机页 / 安装参数）永不自动替换**：同网段可能有两套中心，
  悄悄把机器挪到别人的中心上比"连不上"贵得多 ⇒ 这种情况下只**记一条线索**
  （把发现的地址打出来，运维照着改一步就行）。
- **退避可被打断**：`AgentServerOverride.WaitForChangeAsync`（容量 1 的信号），
  改完地址当场重连；**换址后立刻重试**（`IterationOutcome.ResetBackoff`），不替旧地址还债。
- **失败按性质分流**：`ServerConnectFailure.Classify` —— 连不上（拒绝/不可达/超时）**跳过**凭据探测；
  401/403 直接重新注册；其余（含 `ConnectionReset`，可能是认证层在拒）才去问一句。
  探测预算 20 秒 → **5 秒**。
- `RegisterAsync` 的超时改成 `status=network` 的正常失败（只有**调用方取消**才往上抛）。

### 45.4 验证

| 项 | 结果 |
|---|---|
| `Agent.Tests` | **429 / 0 失败**（+35：地址判定 10 + 连不上归类 9 + 变更信号 2 + 其余既有） |
| 复现脚本 `.build/repro-late-server.py`（隔离：假凭据 + 假中心 UDP，不碰真中心） | **6 / 6** |
| 实测换址耗时 | 首次失败 → 采用发现地址 **21 秒**（旧代码：永不） |
| 复现脚本抓到的额外缺陷 | 第 4 条（注册超时被记成"主循环出错"）——已修，脚本里能看到干净的 `注册请求超时（…）` |
| `dotnet build`（Agent 库 / Agent.Service） | **0 警告 0 错误** |

复现脚本钉住的六条：①先按凭据地址试 ②失败后确实广播 ③采用发现的地址 ④换址后不再退回旧地址
⑤之后多轮都用新地址 ⑥不再有「凭据探测没问出结果」的 20 秒白等。

### 45.5 生效条件

**客户端要重新发布**（`dist/agent` + 安装包）—— 服务端不用动。装完看客户机日志里这几行：
`发现中心服务端：…` / `记住的地址 … 连续 N 轮连不上，改用广播发现的 …`。
仍然连不上时，日志会明确落在一种情形里（没有可依据的地址 / 固定地址连不上 / 广播什么都没有）。

---

（完）
