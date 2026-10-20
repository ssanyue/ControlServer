# REF-M4 · 客户端 Agent 与局域网管控模块细节（改代码前必读）

> 设计全文：`docs/M4-客户端Agent与局域网管控方案.md`（**§20 起是实施记录**）。
> 本文件只放"动手时需要立刻知道"的东西：文件地图、协议、语义铁律、陷阱清单、验证手法。
> 记录时间：2026-10-12（**M4.1 骨架 + 发现** ✅、**M4.2 遥测** ✅、**M4.3 硬件温度** ✅）。M4.4+ 尚未开工。
> 🔥 **2026-10-13 M4.7 变更**（需求 1–4）：UDP 改**纯明文＋来源 IP 黑名单**（删签名 / 删防重放）、新增**显卡摘要**、文件下发**每目标独立存放目录**、目录浏览**放开为整盘**。
> ⚠️ 本文件里 `UdpCommandSignature` / `UdpReplayGuard` / `AllowedSourceIps` / HMAC / 「四关」/「白名单根」等叙述**均已作废**；权威版见文末 **§25**。

---

## 1. 文件地图

### 1.1 客户端（`agents/`）

| 文件 | 职责 |
|---|---|
| `ControlServer.Agent/AgentOptions.cs` | `Agent` 配置节 + `AgentPaths`（状态目录 / 凭据文件 / 状态文件 / 日志目录） |
| `ControlServer.Agent/AgentIdentityStore.cs` | 凭据落盘（DPAPI 密文，`credentials.dat`）+ `AgentStatusSnapshot` / `AgentStatusFile`（`status.json`，**服务写、托盘读**） |
| `ControlServer.Agent/AgentRuntime.cs` | `AgentVersion`（版本常量）+ `AgentRuntime`（发现→注册→连接→心跳→**遥测上报**的运行时状态机） |
| `ControlServer.Agent/Telemetry/TelemetryCollector.cs` | 采样 / 取批 / 提交 / 增量读开关机事件 / 采集清单（`AgentTelemetryOptions`） |
| `ControlServer.Agent/Telemetry/TelemetryStateStore.cs` | 本地水位（`telemetry-state.json`：记录号水位 / 清单上报时刻 / 是否已补扫） |
| `ControlServer.Agent/HostInfoCollector.cs` | 采集主机名 / OS / CPU / 内存 / 网卡清单 |
| `ControlServer.Agent/AgentDataDirectory.cs` | 实现 `IDataDirectory`（复用 Core 的 `LogService`） |
| `ControlServer.Agent/Transport/DiscoveryClient.cs` | UDP 广播发现（发 `CS-DISCOVER`，收 `CS-HERE`） |
| `ControlServer.Agent/Transport/AgentConnection.cs` | SignalR 长连接 + **`AgentRegistrar`**（HTTP 注册） |
| `ControlServer.Agent.Service/Program.cs` | 服务宿主（**`Exe`，控制台子系统**：可前台跑、可看日志） |
| `ControlServer.Agent.Service/AgentServiceHost.cs` | `IHostedService` 包装：把 `AgentRuntime` 挂到宿主生命周期上 |
| `ControlServer.Agent.Tray/Program.cs` | 托盘伴侣（**`WinExe`**）；只读状态文件 + 走 SCM 起停服务 |

### 1.2 服务端

| 层 | 文件 |
|---|---|
| Contracts | `AgentDtos.cs`、`AgentTelemetryDtos.cs`（M4.2：指标 / 开关机 / 清单 / 应答）、`Constants.cs`（`Routes.AgentHub` / `AgentAuthentication` / `EventNames.Agent*`） |
| Core | `Services/AgentDiscoveryProtocol.cs`、`Services/AgentPresence.cs`、`Services/AgentMetricsAggregator.cs`（M4.2 纯函数聚合）、`Models/AgentHost.cs`、`Models/AgentTelemetry.cs`（`AgentMetricsDaily` / `AgentInventoryRecord`）、`Abstractions/IAgentHostStore.cs`、`Abstractions/IAgentTelemetryStore.cs` |
| Infrastructure | `Persistence/Entities/AgentHostEntity.cs`、`Entities/AgentTelemetryEntities.cs`（4 表）、`Persistence/TelemetryTime.cs`（**毫秒换算唯一入口**）、`Persistence/EfAgentHostStore.cs`、`Persistence/EfAgentTelemetryStore.cs`、`Persistence/AppDbContext.M4Telemetry.cs`、迁移 `20261011161356_M4_AgentHosts` ＋ `20261011183719_M4_2_AgentTelemetry` ＋ `_M4_3_AgentThermal` |
| Server | `Api/AgentsController.cs`、`Api/DownloadsController.cs`、`Hubs/AgentHub.cs`、`Services/AgentHostRegistry.cs`、`Services/AgentEnrollmentService.cs`、`Services/AgentInstallerLocator.cs`（+ `ServerRuntimeInfo`）、`Services/AgentTelemetryService.cs`（M4.2 摄入/落盘/查询；M4.3 清单热状态白名单）、`HostedServices/AgentPresenceHostedService.cs`、`HostedServices/AgentTelemetryHostedService.cs`、`Security/AgentKeyAuthenticationHandler.cs`、`Security/LicenseGate.cs`（**必须归类遥测三端点**） |
| Platform | `IHostHardwareInfo` + `BasicHostHardwareInfo`、`WindowsHardwareInfo`、`DpapiSecretProtector`、M4.2：`HostTelemetry.cs`（`IHostMetricsSource` / `IPowerEventSource` / `IHostInventorySource` + `CompositeHostMetricsSource` / `Basic*` / `NoopPowerEventSource`）、`WindowsHostMetricsSource.cs`、`PowerEventSource.cs`、`WindowsHostInventorySource.cs`、**M4.3：`HostTelemetry.cs` 里的 `IThermalSource` / `NoopThermalSource` ＋ `WindowsThermalSource.cs`**（LibreHardwareMonitor 封装，`net8.0`——**不引 `net8.0-windows`**） |
| 前端 | `web/src/components/AgentsPanel.vue`、`AgentDetailPanel.vue`、`MetricsChart.vue`（M4.2 零依赖 SVG 图）、`App.vue`（顶栏按钮 + 页签 + `AgentTelemetryUpdated`）、`types.ts`、`api.ts`、`format.ts`（`beijingIso` / `fmtRate` / `fmtMb` / `fmtUptimeHuman`） |

---

## 2. 铁律

1. **客户端连出来，服务端不扫描。** 客户端**不开任何入站端口**（广播是它发出去的、长连接是它连出去的）。
   ⇒ 安装客户端**不需要放行任何防火墙端口**，这是"客户机零配置"的实现基础。
2. **`ControlServer.Agent` 绝不引用 `ControlServer.Server`。** 引了就会把 ASP.NET Core 宿主 + EF Core
   打进每个客户机的安装包（体积翻倍）。允许依赖：`Contracts` + `Core` + `Platform.Abstractions`。
3. **"注册" ≠ "见过"。** `LastSeenAt` **只由心跳写**。注册时写它会让
   「等待连接（`never`）」这个状态**永不可达**（已踩，见 §6-2）。
4. **状态判定只用心跳时间戳**，不做 ping / TCP 探测 —— 客户机可能关着 ICMP/相关端口，
   探测只会制造假离线。
5. **在线状态用字符串常量**（`AgentStates.Online/Unstable/Offline/Never/Revoked`），
   **不用 enum** —— enum 会被 SignalR 序列化成数字，前端比较字符串就永远不匹配。
6. **托盘与服务必须解析出同一个状态目录**，否则托盘永远显示"未连接"而服务其实连着
   （最难查的一类误判）。所以 `install-agent.ps1` 把它同时写进服务命令行与 HKCU 启动项。
7. **`Server` / `Agent` 相关的运行期事实从 `ServerRuntimeInfo` 取**（真正在听的端口可能来自
   端口覆盖文件，不一定是配置里的 8080）。别让消费方各自去读配置，否则会重演"写 8080、听 8090"。
8. **温度净化只有一份：`AgentMetricsAggregator.Temperature`**（`NaN`/`±Infinity`/`<-40`/`>150` → `null`，**0 保留**）。
   **存储路径与在线表摘要路径必须共用它** —— 只做一条会分裂出"库里是 null、在线表是 NaN"，
   而 System.Text.Json 严格模式拒绝 NaN ⇒ **`GET /api/agents` 整个 500，一台坏客户端让所有机器从页面上消失**。
9. **可选能力（如温度）的"默认不装"必须靠代码守，不靠打包排除。** LHM 把 WinRing0 以 gz 资源
   内嵌在 DLL 里，**只有真的 `new Computer(...).Open()` 才落盘**；未启用时**连 `Computer` 都不构造**。
   打包排除的方案迟早被"顺手引用一下"破坏，而且排掉之后连勾选也装不上了。

---

## 3. 发现协议（UDP 广播）

```
客户端 → 广播（默认 9998）:  CS-DISCOVER <machineKey> <agentVersion>
服务端 → 单播应答        :  CS-HERE <product> <version> <httpPort> <pairingPort> <discoveryPort> <serverId>
```

| 项 | 值 |
|---|---|
| 关键字 | `CS-DISCOVER` / `CS-HERE`（`AgentProtocol`） |
| 产品标识 | `ControlServer`（应答第 2 字段） |
| 应答字段数 | **恰好 7**（客户端按固定下标取值） |
| 端口 | 默认 **9998**（`DiscoveryOptions.Port` / `AgentOptions.DiscoveryPort`）；**刻意避开对接码的 9999** |
| 报文上限 | **512 字节**（超长丢弃，不应答） |
| 凭据 | 应答里**不含任何凭据**（它对整个广播域可见），仍被冒烟逐字检查 |

**解析绝不抛异常**：`AgentDiscoveryProtocol.TryParse*` 对乱码/畸形/超长一律返回 `false`，
监听循环因此不会被一条垃圾包打死（冒烟专门发过乱码再发正常包，必须仍能应答）。

---

## 4. 在线状态判定（`AgentPresence`，纯函数）

| 状态 | 常量 | 判据（**从上往下先命中者胜**） |
|---|---|---|
| 已吊销 | `revoked` | 台账里 `RevokedAt` 非空 —— **压过一切时间判据** |
| 等待连接 | `never` | `LastSeenAt` 为空（注册过、从没心跳过） |
| 在线 | `online` | `now - LastSeenAt ≤ StaleAfter` |
| 不稳定 | `unstable` | `StaleAfter < now - LastSeenAt ≤ OfflineAfter` |
| 离线 | `offline` | 超过 `OfflineAfter` |

默认阈值：**心跳 10 s / `StaleAfter` 30 s / `OfflineAfter` 75 s**。
最坏情形（硬断电，最后心跳最早可能早 10 s）：**10 + 75 = 85 s ≤ 90 s** —— 这就是
"关机 ≤90 秒变离线"这条验收指标的算法来源。调阈值时**别破坏这个不等式**。

⚠️ 时钟超前（客户端机器时间比服务端快）时 `now - LastSeenAt` 为负 ⇒ 归到 `online`，
不报异常、不判离线。

---

## 5. 凭据与准入

> ⛔ **本节的"准入"部分已于 2026-10-13 整体作废**（产品决策：免凭据准入，见 **§23**）。
> 下表里 **agentKey 一行仍然有效**；**安装包令牌 / 加入码两行已不存在**（表结构保留但无读写方）。

| 凭据 | 形态 | 生命周期 | 存放 |
|---|---|---|---|
| **agentKey** | 32 字节随机 → **Base64Url（43 字符）** | 长期（换机器才变） | 服务端只存 **SHA-256（小写 64 hex，无盐 —— 输入本身已是高熵随机）**；客户端用 **DPAPI(`LocalMachine`)** 加密后落 `credentials.dat` |
| ~~安装包令牌~~ | ~~高熵随机串~~ | **已删除**（2026-10-13） | — |
| ~~加入码~~ | ~~短码，字母表去掉 `0/O/1/I/L`~~ | **已删除**（2026-10-13） | — |

- **准入现状（2026-10-13 起）**：**注册不需要任何凭据**。详见 **§23**。
- 注册端点 `POST /api/agents/register` 是 **`[AllowAnonymous]`**：它认的是"机器"不是"用户会话"，
  所以**不被 RBAC / 授权闸门拦**（由 `AgentEnrollmentService` 自己判）。
- **`AgentKeyAuthenticationHandler` 是独立认证 scheme**（`AgentAuthentication.Scheme = "AgentKey"`）：
  读 **`X-Agent-Key` 头**或 **`?agentKey=` 查询串**。它与 Cookie 是**两套完全独立的身份** ——
  Cookie 认"人"，AgentKey 认"机器"。
- 🔒 **读端点从不回显 agentKey**（有集成测试守门）。首次注册的明文**只出现一次**。

---

## 6. 陷阱清单（按踩到的先后）

1. **写操作返回体被 MVC 包了两层。**
   `IActionResult Mutation(...) => Ok(new AgentMutationResult(...))` 被调用方再 `Ok(...)`，
   线上实得 `{"value":{…},"formatters":[],"declaredType":null,"statusCode":200}`。
   前端读 `result.ok` 得到 `undefined` —— **按钮点了"没反应"却不报错**，最像"功能坏了"。
   → `Mutation()` 必须返回 **DTO 本体**。集成测试 `Mutation_endpoints_return_the_payload_directly` 守门。
   同类防线：不要在任何写端点返回值上再包一层 `Ok(...)`。

2. **注册时写 `LastSeenAt` ⇒ 「等待连接」永不可达。**
   新建行必须 `LastSeenAt: null`；`IAgentHostStore.UpdateRegistrationAsync` **刻意不接收**心跳时间参数，
   也**不写** `LastSeenAt`。有集成测试 `A_freshly_registered_host_is_waiting_to_connect_not_offline`。

3. **冒烟脚本的断言形状容易写错（不是产品 bug，却最像产品 bug）。**
   写操作返回 `{ok, status, message, list:{items,total,…}}` —— 列表是**嵌在 `list` 里**的，
   不是摊平的。脚本里若按 `body["items"]` / `body["total"]` 取，会得到"空列表"，
   表现为"选了值没落库"。→ 用 `agents_of()` / `total_of()` 兼容两种形状。

4. **"移除后列表清空"这种断言本身就是错的。**
   `agent-smoke.py` 的 D 段会起一个**真客户端**，它已经用自己的机器码在表里注册过一条记录。
   所以删掉 B 段那台之后，正确预期是**剩 1 条**。
   → 断言改成"这台不在了 + 条数正好减 1"，与表里还有别的机器无关。

5. **PowerShell 未绑定的 `[string]` 参数是 `''`，不是 `$null`。**
   所以 `-not $PublishTo -and $PublishTo -ne ''` 这种"区分没传 / 传空"的写法**永远判成传了空**。
   → 用 `$PSBoundParameters.ContainsKey('PublishTo')`。
   （`build-agent-installer.ps1` 第一版就踩了：默认投放**从未生效**，却打印"跳过投放"。）

6. **`appsettings.json` 到底读不读得到 —— 实测结论：读得到。**
   `Host.CreateApplicationBuilder` + `AddWindowsService` 会把 ContentRoot 钉在
   **`AppContext.BaseDirectory`**，所以服务形态下即使 cwd 是 `C:\Windows\System32`，
   **exe 同目录的 `appsettings.json` 照样被读到**。
   验证手法（可行且便宜）：在 exe 目录放一份指向临时状态目录的 `appsettings.json`，
   从 `C:\Windows\System32` 启动该 exe → 日志显示 `Content root path` = exe 目录、
   **配置里的临时目录被创建、默认目录没被创建**。
   ⇒ 因此安装脚本把"服务端地址 / 注册令牌"写**配置文件**而不是服务命令行 ——
   命令行会被 `sc qc <服务名>` 原样打印给任何本机用户。

7. **注册服务必须用 `New-Service`（cmdlet），不能用 `sc.exe create`。**
   PowerShell 5.1 给原生程序传参时会吞掉内层引号，装到含空格路径
   （`C:\Program Files\...`）时 `binPath` 被截断 ⇒ **1639 ERROR_INVALID_COMMAND_LINE**。
   已实测（详见 `install-service.ps1` 的长注释）。`sc failure` 仍可用（它的参数不含引号）。

8. **`sc delete` 是异步的。** 删完立刻重建会撞 **1072「已标记为删除」**。
   → `Remove-ServiceIfPresent` 必须轮询等到服务真的从 SCM 消失。

9. **收紧 `appsettings.json` 的 ACL 要用 SID，不要用组名。**
   `icacls ... /grant:r '*S-1-5-18:(F)' '*S-1-5-32-544:(F)'`（SYSTEM / Administrators）。
   组名在本地化系统上可能取不到。⚠️ 收紧后**普通用户读不到该文件** ⇒
   脚本里给用户的操作指引**不能**写"手工编辑 appsettings.json"，要给
   "以管理员重跑 `install-agent.ps1 -JoinCode <加入码>`"这种真能走通的路。

10. **`deploy\**\*.ps1` 里函数必须写在调用点之前**（PowerShell 不支持前向引用，
    而 `Continue` 模式会把 `CommandNotFoundException` 静默吞掉 ⇒ 脚本"成功"却少做了事）。
    新建的 `*.ps1` **必须 UTF-8 with BOM**（PS 5.1 无 BOM 按 ANSI 解析，中文注释直接 `ParserError`）。
    非破坏性语法自检：
    ```powershell
    $t=$null;$e=$null
    [void][System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $f).Path,[ref]$t,[ref]$e)
    $e.Count   # 0 = 语法 OK
    ```

11. **本会话里 PowerShell 工具的 stdout 不回显。** 需要看输出时把结果落文件再用 Read 读；
    `*>` 重定向得到的是 **UTF-16**（Read 工具会拒绝），要用 Python 按 `utf-16` 解码。

---

## 7. 服务端节奏与内存快照

| 组件 | 节奏 | 说明 |
|---|---|---|
| `AgentPresenceHostedService` | **5 s** 巡检 | 只推**状态跃迁**（`AgentOnline` / `AgentOffline`），不推每次心跳 |
| 同上 | **60 s** 批量落盘 | 心跳**只更新内存**；每 60 秒成批写 SQLite |
| 同上 | **60 s** 刷新快照 | |
| 同上 | **30 s** 空闲广播 | 无变化时的保活/续期广播 |

- `AgentHostRegistry` 是内存快照（volatile 数组 + 内存态），**读端点走它**，不每次查库。
- **写操作会立刻刷快照** —— 所以"改名后列表里立刻可见"是设计承诺（冒烟有对应用例）。
- `AgentChanged` = 任意字段变化；`AgentOnline` / `AgentOffline` = **状态跃迁**
  （这才是"页面该弹个提示"的时刻）。三个事件都在 `/hubs/events`。

---

## 8. 端点与权限

| 端点 | 权限 |
|---|---|
| `GET /api/agents`、`GET /api/agents/{id}` | `Policies.CanOperate` |
| `PUT /api/agents/{id}`（改名/分组/备注） | `Policies.AdminOnly` |
| `POST /api/agents/{id}/revoke` · `/unrevoke` | `AdminOnly` |
| `DELETE /api/agents/{id}` | `AdminOnly`（**只删台账记录，不会卸载客户机上的 Agent** —— 响应文案必须讲清） |
| `POST /api/agents/{id}/reboot` | `AdminOnly`；客户端**离线时返回 409**（请求合法但"现在做不到"） |
| `GET /api/agents/enrollment` · `POST /api/agents/enrollment` · `POST /api/agents/enrollment/{id}/revoke` | `AdminOnly` |
| `POST /api/agents/register` | **`[AllowAnonymous]`**（凭据自判） |
| `GET /api/downloads/agent-installer/info` · `GET /api/downloads/agent-installer` | `[Authorize]`（**含 Range 支持**，有集成测试） |
| `/hubs/agent` | AgentKey 认证 |

> **铁律 #7（记忆 §2）在这里的落点**：新增控制器必须自带 `[Authorize]` 或显式 `[AllowAnonymous]`；
> 新增 `/api` 端点必须进授权闸门分类表 —— 两者都有反射守门用例，漏了就红。

---

## 9. 安装包托管

- 位置：**`{DataDir}\downloads`**（`AgentOptions.InstallerDirectoryName`，默认 `downloads`）。
  ⚠️ **不要放进发布目录**：`dist\win-x64\` 必须**恰好 1 个文件**，那是绿色版的承诺，
  `build-portable-exe.ps1` 有硬校验，塞进去必然报错。
- 命名约定：**`ControlServer-Agent-Setup-<版本>.exe`**（`AgentInstallerLocator` 的 `Prefix`
  就是它，版本号也从它解析）。改名 ⇒ 下载按钮退化成"随便取一个 exe"。
- **版本不一致只提示、不阻断**（比前两段：只比主次版本）。现场常有"客户端还差一版"的过渡期，
  禁掉按钮只会把人逼回"私下拷文件"这条更不可控的路。
- 手工改名也认：找不到约定前缀时退化为"取目录里最新的 exe"。

---

## 10. 构建与交付

```bash
# 客户端两个 exe → dist\agent\（恰好 2 个文件）
powershell -NoProfile -ExecutionPolicy Bypass -File deploy/windows/build-agent.ps1
# 安装包 → dist\agent-setup\ControlServer-Agent-Setup-<版本>.exe，并投放到服务端 downloads\
powershell -NoProfile -ExecutionPolicy Bypass -File deploy/windows/build-agent-installer.ps1
#   批量部署：把地址与令牌烘焙进安装包
powershell -NoProfile -ExecutionPolicy Bypass -File deploy/windows/build-agent-installer.ps1 `
    -Server http://10.0.0.5:8080 -Token <注册令牌>
#   只编译不投放（注意是显式空串）
powershell -NoProfile -ExecutionPolicy Bypass -File deploy/windows/build-agent-installer.ps1 -PublishTo ''
```

| 产物 | 大小（本机实测） |
|---|---|
| `dist\agent\ControlServer.Agent.Service.exe` | 35.1 MB |
| `dist\agent\ControlServer.Agent.Tray.exe` | 34.6 MB |
| `dist\agent-setup\ControlServer-Agent-Setup-0.1.0.exe` | 58.6 MB |

发布参数与绿色版一致（`--self-contained true -p:PublishSingleFile=true
-p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false`）——
**目标机不需要预装 .NET 运行时**。

### 安装 / 卸载

```bash
# 管理员
powershell -NoProfile -ExecutionPolicy Bypass -File deploy/windows/install-agent.ps1 [-Server ...] [-Token ...] [-JoinCode ...]
powershell -NoProfile -ExecutionPolicy Bypass -File deploy/windows/uninstall-agent.ps1 [-RemoveStateDir]
```

- 装到 `%ProgramFiles%\ControlServer Agent`，状态 `%ProgramData%\ControlServer.Agent`。
- 服务名 `ControlServerAgent`；`HKCU\...\Run` 值名 `ControlServerAgent.Tray`。
- **不建任何防火墙规则**（客户端不开入站端口）。
- 卸载**默认保留状态目录**（里面有 DPAPI 凭据 ⇒ 重装不必重新注册）。
- 退出码：`0` 成功 / `1` 装配中途失败 / `2` 服务没起来 / `3` 脚本被策略拒绝。
- 安装包静默部署：`ControlServer-Agent-Setup-x.y.z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
  [/SERVER=...] [/TOKEN=...]`。

---

## 11. 冒烟（`deploy/smoke/agent-smoke.py`）

```bash
python deploy/smoke/run-all.py src/ControlServer.Server/bin/Debug/net8.0/ControlServer.Server.exe --only agent
```

| 段 | 覆盖 |
|---|---|
| A | 安装包：`info` 元信息 / 真下载 / **Range 206** / 版本不一致提示 |
| B | 准入与在线表：**免凭据注册**（不需要令牌 / 加入码）、**同机器码重复注册复用同一条记录**、缺机器码 **400 / `invalid`**、agentKey **只出现一次**、`never` 状态、改名/吊销/移除、离线重启 **409**、**已删令牌端点不再是令牌清单**（⚠️ 不能按状态码判，见 §23.9） |
| C | **真 UDP 发现**：`CS-DISCOVER` → 7 字段应答、不含凭据、乱码后仍工作、超长丢弃 |
| D | **真客户端端到端**：起 `ControlServer.Agent.Service.exe` → **自动注册**（不带任何凭据）/ 长连接 / 判在线 → **遥测三段（时序明细 / 硬件清单 / 在线表最新帧 / 开关机记录）** → **温度默认关闭段（M4.3）** → 下发重启 200 → 杀进程 → **30 s 内判离线**、最后心跳被保留 |
| E | 权限矩阵：Operator 可读在线表、写操作 403；移除并校验条数减 1 |

- `run-all.py` 为 `agent` 场景显式传
  `--Server:Agent:HeartbeatSeconds=5 --Server:Agent:StaleAfterSeconds=15 --Server:Agent:OfflineAfterSeconds=30`
  ＋ M4.2 的 `--Server:Agent:Telemetry:{MetricsSeconds=2, FlushSeconds=2, PowerEventPollSeconds=5, NotifyThrottleSeconds=1}`
  （把 90 秒的验收 / 30 秒的落盘压缩到几十秒内可测）。
- ⚠️ 遥测段含一条**回归守卫**：`详情`列出现 `System.Byte[]` 或纯数字转储即判失败（见 §13-5）。
- ⚠️ **温度段（M4.3，共 6 条）** 钉的是"关着的时候老不老实"：`thermalState == disabled`、
  `thermalNote` 非空、**温度字段全为 null（不是 0）**、日志里写明「硬件温度：未启用」、
  且**没有 Warning/Error 级的驱动/温度行**。
- ⚠️⚠️ 温度段的日志断言读的是 **`<StateDir>\logs\*.ndjson`**（Serilog 文件 sink），
  **不是 stdout** —— 客户端的 stdout 只有 ASP.NET 那 3 行生命周期日志，
  读错地方会让断言**永远通过**（比没有断言更糟）。见 §14-5。
- 🔧 `CS_AGENT_EXE=<exe>`（环境变量）可让 D 段去跑**打包产物**而不是开发产物：
  `CS_AGENT_EXE=dist/agent/ControlServer.Agent.Service.exe python deploy/smoke/run-all.py --only agent`。
  打包会改 DebugType / 单文件形态，这些差异**只在打包产物上才暴露**。
- D 段找不到客户端 exe 时会 **SKIP**（不误报失败）。
- ⚠️ 五个冒烟脚本对初始状态的假设互斥，**别塞进同一个实例**跑。
- 当前结果：**106 通过 / 0 失败**（M4.1 时 79；M4.2 遥测段 +20；M4.3 温度段 +6；免凭据准入 #215 调整后 106）。

### 11.1 文件分发冒烟（`deploy/smoke/transfer-smoke.py`，M4.4）

```bash
python deploy/smoke/run-all.py src/ControlServer.Server/bin/Debug/net8.0/ControlServer.Server.exe --only transfer
```

| 段 | 覆盖 |
|---|---|
| A | 身份：伪造 AgentKey 401、Viewer 不能读文件、Operator 能读**不能写仓库** |
| B | **分块上传**：错偏移 **409 + 真实 `receivedBytes`**、摘要不符被拒并**保留会话**、`Kind` 强制 `collected` |
| C | **下发**：离线也建满明细、上线补领、**`Range` 206 两段拼接后 SHA256 一致**、终态幂等 |
| D | 汇总：任务状态由明细汇总、终态任务不再出现在补领列表 |
| E | **回传**：客户机分块 `PUT` 入库、记录来源机器、回传分区可见 |
| F | **真客户端端到端**：起 `ControlServer.Agent.Service.exe` → 自动注册握手 → **上报白名单目录** → 下发 300KB **真落地**（大小 + SHA256 + 不留 `.cs-part`）→ 回传现场文件入库 → **越界路径被客户机拒绝**并如实回报 `failed`（原因写明"源路径不在本机白名单目录内"） |

- 当前结果：**67 通过 / 0 失败**（M4.4 首版 63，补白名单上报段 +5；#215 免凭据准入后去掉"生成安装包令牌"一条 ⇒ 67）。
- 另有两条独立验收：`.tools/m44-ui-check/`（真浏览器 48 条）与 **`.tools/transfer-resume.py`（>2GB 断点续传 25 条）**。

---

## 12. 尚未做（M4.5+）

**局域网远程桌面**（与 M5 重叠）/ 客户端自更新（需签名 + 回滚）/
Linux 客户端实现（`Platform.Linux` 侧三个遥测源目前是 `Basic*` / `NoopPowerEventSource` = **明确不支持，不是坏掉**；
温度侧是 `NoopThermalSource`，会如实上报「当前平台未实现硬件温度采集」）/
`.tools/agent-uia-check/` 托盘自动化夹具。开关机记录与定时关机已在 M4.2 落地，**硬件温度已在 M4.3 落地**（见 §14），
**文件分发 / 文件传输已在 M4.4 落地**（见 §15）。**M4.5 BootAssistant 对齐进行中**：服务端四层
（契约 / Core / Infrastructure / Server）与**客户端配置拉取·缓存·降级·三类任务执行器**已落地（见 §16），
**UDP 加固指令通道**与**会话侧（命名管道 → 音量 / 静音 / 延时启动程序）**尚未接入。

---

## 13. M4.2 遥测（2026-10-12 落地）

### 13.1 三层存储 + 事件层

| 层 | 表 / 载体 | 粒度 | 保留 | 谁写 |
|---|---|---|---|---|
| 热 | `AgentHostRegistry` 内存（注册表对象上的 `Metrics`） | 最新一帧 | 进程存活期 | `AgentTelemetryService.AcceptMetrics`（**同步**） |
| 温 | `agent_metrics` | 5 秒明细 | 30 天 | `FlushAsync`（**异步、成批**） |
| 冷 | `agent_metrics_daily` | 日聚合（均值 + 峰值 + 采样数） | 1 年 | `AgentTelemetryHostedService` 每日滚动 |
| 事件 | `agent_power_events` | 逐条 | **永久** | `AcceptPowerEventsAsync`（**同步落盘后才应答**） |
| 清单 | `agent_inventories` | 一机一行（最新） | 覆盖 | `AcceptInventoryAsync`（同步） |

### 13.2 两条持久化路径**故意不同**

- **指标**：先入内存缓冲 → 立刻应答"已接受" → 后台成批写库。理由：300 台 × 5 秒 = 60 写/秒，
  直写会让 SQLite 的 WAL 被小行写满，把"改个备注"这种真写操作挤在后面。代价是崩溃最多丢一个落盘周期的数据。
- **开关机事件 / 清单**：**同步写库后再应答**。理由：客户端的记录号水位一旦推进，那条事件**永不会再发**。
  用一点延迟换数据真实性，任何时候都划算。**绝不能**在写入失败时回 `Ok`（`AcceptPowerEventsAsync` 里那句注释就是为此）。

### 13.3 🔥 时间戳存 **Unix 毫秒**（`AtMs`，`long`），不是 `DateTimeOffset`

- 真因：**EF 的 SQLite 提供程序拒绝翻译 `OrderBy(x => x.DateTimeOffset)`**，运行时抛
  `NotSupportedException: SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses`。
  首次出现在 `EfAgentTelemetryStore.QueryPowerEventsAsync`（开关机列表一打开就 500）。
- 换算只经 `Persistence/TelemetryTime.cs` 的 `ToMs` / `FromMs` 两处（`FromMs` 统一 `.ToOffset(BeijingTime.Offset)`）。
- **代价**：亚毫秒被裁掉。写测试时**别用带亚毫秒的 `DateTimeOffset` 当期望值**（会得到一个看着像时区错的假失败）；
  这条由 `AgentTelemetryApiTests.Telemetry_timestamps_round_trip_exactly_at_millisecond_precision` 钉住。
- **例外**：`agent_inventories.CapturedAt` 仍是 `DateTimeOffset`（一机一行、无排序无范围查询）。
- 迁移回归里有一条 `pragma_table_info` 断言 `AtMs` 必须是 `INTEGER` —— 改回去会立刻红。

### 13.4 聚合口径（`AgentMetricsAggregator`，纯函数）

- **null 参与运算 = 忽略，不是当 0**。一台机器 3 个点、1 个 CPU 采不到 ⇒ 均值是另 2 个的均值。
  "当成 0"会把"采集失败"画成"机器突然闲下来"。
- **空桶跳过，不补 0**（补 0 会画出一条假的掉底曲线）。桶边界锚在查询窗口左界 `from`。
- **日聚合只存均值 + 峰值 + 采样数**；在线时长 = 采样数 × 间隔，**上限 86400**（估算值，精确值要从开关机事件算）。
- **开关机统计**：开机 → 后续终止事件配对求在线时长；**末尾还开着的机器不计入**（否则"平均在线时长"会无限增长）；
  睡眠 / 唤醒**只计数不配对**（算成关机则"每日开关机次数"翻倍）；
  **6008 与 41 在 10 分钟窗口内合并成一次意外关机**（同一次事故两条记录，不合并会整整翻一倍）。
- 分桶粒度只取固定梯级 `5,10,…,86400`（"每 5 分钟一个点"比"每 287 秒"好解释）。

### 13.5 陷阱清单（M4.2 专属，按踩到的先后）

1. **`DateTimeOffset` 的 `ORDER BY`** —— 见 §13.3。症状："开关机记录全部被拒收"＋ 客户端日志刷屏 `上报被拒（error）`。
2. **上报失败日志刷屏**（实测 1 分钟 24 行）：三个上报循环共用一个 `_ingestFailureStreak`，
   指标的成功会**清掉**开关机的失败计数 ⇒ 每轮都重记一行。修法：**三个独立计数器**
   （`_metricsFailureStreak` / `_powerFailureStreak` / `_inventoryFailureStreak`），`ref int streak` 传引用。
3. **`TelemetryStateStore` 里属性名不能叫 `Path`** —— 会遮掉 `System.IO.Path`，
   `Path.Combine` 变成 `CS1061: "string" 未包含 "Combine"`。改用 `FilePath` + `System.IO.Path.Combine`。
4. **`在 <script setup>` 里 `export interface` 不合法** —— `ChartSeries` 必须放 `types.ts`。
5. **前端图表轴时区错位**：`new Date(ms).toISOString()` 产出 `...Z`，被 `fmtDateTime`（截 ISO 字符串）显示会**少 8 小时**。
   用 `format.ts` 的 `beijingIso(ms)`。服务端 `QuerySeriesAsync` 也把窗口归一化回 `+08:00` 再回显。
6. **`MetricsChart` 必须按 null 断开折线**（图表库会把 null 当 0 或强行跨过连接）—— 单文件双 `path` 分段实现。
7. **开关机「详情」列不能无差别 `property.Value.ToString()`**：`byte[]` → 字面量 `System.Byte[]`；
   Kernel-Power 41 的载荷是一串没有 schema 的裸数字。两类都让"与事件查看器逐条核对"这个唯一用途失效。
   修法：**优先取 `FormatDescription()` 正文原文**（与事件查看器逐字一致），退路才是**纯文本型**插入字符串。
   ⚠️ 这与"不解析正文"**不矛盾**：不解析 = 不从正文里抠断电时刻当 `At`；这里是**原样呈现**。
8. **`AgentsPanel.applyLiveMetrics` 必须按 `metrics.at` 做新旧判定**，迟到旧帧直接丢 ——
   SignalR 推送与 HTTP 刷新会并发，旧帧覆盖新帧会让实时数字"往回跳"。
9. **遥测三端点必须进 `LicenseGate` 分类表**（漏了 = fail-closed 拦）—— 失败形态是
   "有授权时一切正常、没授权时详情页一片空白"，极难联想到授权闸门。
10. **`RegisterOneAsync` 式的测试辅助必须能传机器码**：同一机器码重复注册是"这台机器重装客户端"，
    服务端**复用**原记录（M4.1 有意设计）—— 想拿两台机器必须给两个机器码。

---

## 14. M4.3 硬件温度（2026-10-12 落地）

> 口径：**可选能力、默认不装**。设计全文见 `docs/M4-客户端Agent与局域网管控方案.md` **§22**。

### 14.1 三态语义 + 「ok」的真实含义

| 状态 | 含义 | 页面 |
|---|---|---|
| `ok` | 已启用，且**拿到了至少一项**读数 | 显示度数；拿不到的项显示 `—` |
| `disabled` | 未启用。**默认值，也是正常状态** | 「未启用」+ 怎么开 |
| `failed` | 已启用但**一项都没拿到**，原因在 `ThermalNote` | 标红「驱动加载失败」+ 原文原因 |

🔥 **`ok` ≠ "驱动装上了"** —— 显卡温度（NVIDIA / AMD）走**用户态** NVAPI / ADL。
真机实测（非管理员）：状态就是 `ok`、`gpuTempC` 有度数，而 `cpuTempC` / `storageTempC` 是 `null`
（那两项要内核驱动）。契约与前端的注释都已按这句改写。

### 14.2 "默认不装"的实现位置

`WindowsThermalSource.Read()` 的**第一行**：`if (_disposed || !_enabled) return HostThermalReading.Empty;`
—— 未启用时**连 `Computer` 都不构造**。LHM 的 WinRing0 是 DLL 里的一段 gz 资源，
**只有 `Computer.Open()` 才解出来落盘并加载**，所以"不构造"就等于"一个字节都不碰"。

初始化**对"驱动加载失败"仍然只做一次**：每 5 秒重试一次驱动加载会把事件日志刷爆，
还会让杀软反复弹窗。`State` / `Note` 的 getter **会触发**这次初始化
（避免"清单上报"与"首次采样"竞态把已启用机器报成未启用）。

⚠️ **2026-10-19 起有唯一的例外**（`ShouldAttempt`）：只有"**枚举到了 CPU 硬件、却没读到任何温度**"
这一种失败允许再试，最多 3 次、间隔 30 秒。理由是传感器初始化慢，不是驱动装不上；
判据收在 `_retryable = sensors.Any(Kind == Cpu)`。

### 14.3 数据落点

| 项 | 表 / 载体 | 列 |
|---|---|---|
| 状态 + 原因 | 硬件清单（`agent_inventories`，一机一行） | JSON 里的 `ThermalState` / `ThermalNote` |
| 三项温度 | `agent_metrics` | `CpuTempC` / `GpuTempC` / `StorageTempC`（REAL 可空） |
| 日聚合 | `agent_metrics_daily` | `CpuTempAvgC` / `CpuTempMaxC` / `GpuTempAvgC` / `StorageTempAvgC`（REAL 可空） |
| 合同 | `AgentTelemetryDtos.cs` | `AgentThermalStates`（只用字符串常量，理由同 `AgentPowerEventKinds`） |

采样时**一次采样只读一次温度**（`TelemetryCollector.ReadThermal()`），三次 `Read()` = 三倍驱动开销。

### 14.4 开关与配置

- 安装脚本：`install-agent.ps1 -ThermalEnabled` → 写 `Agent:Telemetry:ThermalEnabled`（**总是显式写出**，含 `false`）。
- Inno：新增「可选能力」向导页，复选框**默认不勾**；静默安装用 `/THERMAL=1`。
- 组合根：`bool.TryParse(builder.Configuration["Agent:Telemetry:ThermalEnabled"], ...)` →
  Windows 走 `WindowsThermalSource(enabled)`，其他平台走 `NoopThermalSource("当前平台未实现…")`。
- ⚠️ `ThermalEnabled` **刻意不进服务端下发的 `AgentTelemetrySettingsDto`**：用不用内核驱动是**现场决定**，
  不该由中心服务端远程打开（那等于给所有客户机远程装驱动）。

### 14.5 陷阱清单（M4.3 专属）

1. **LHM 必须钉 `0.9.3`**：0.9.4 起把传递依赖升到 `System.Management >= 9.0.0`，与本项目集中钉的
   `8.0.0` 冲突 ⇒ 还原报 **NU1605「检测到包降级」**。0.9.3 依赖的正是 8.0.0。
   0.9.3 没有 `net8.0` 资产，NuGet 回退到它的 `net7.0` 资产（正常可用）。
   真实依赖足迹 = 7 个传递包（HidSharp、Microsoft.Win32.Registry、Mono.Posix.NETStandard〔含 Linux `.so`〕、
   System.IO.FileSystem.AccessControl、System.IO.Ports、System.Management）。
2. **净化必须两条路径共用**（见 §2-8）：`EfAgentTelemetryStore` 与 `AgentMetricsAggregator.Summarize`
   都调 `AgentMetricsAggregator.Temperature`。漏一条 = 在线表 500。
3. **`LHM` 的 `Sensor.Value` 是 `float?`，`NaN` 表示"这次没读到"** —— 必须同时挡 `NaN` 与 `±Infinity`
   （进 JSON 非法、进均值污染整段）。`ValueOf(ISensor)` 里做了。
4. **传感器名字不是契约**：Intel 叫 "CPU Package"、AMD 叫 "Core (Tctl/Tdie)"、NVIDIA 叫 "GPU Core"、
   AMD 显卡叫 "GPU Hot Spot"。取值必须**分级回退**而不是精确匹配。
   `Hot Spot` / `Junction` 刻意排最后（结温比核心高 10~20 ℃，当"显卡温度"会让人以为过热）。
   磁盘多块时**取最高**（这个字段是回答"有没有哪块盘快热炸了"，均值会把 70 ℃ 混进 30 ℃ 里）。
   **2026-10-19 起分级策略抽到 `ThermalSensorPicker`（Platform.Abstractions，20 条用例）**，
   因为写在 Windows 实现里**一条测试都跑不了**（测试工程不引 Platform.Windows）。三处 AMD 专属坑：
   ① **老 AMD（K10/K15、FM2/AM3）的 CPU 温度在主板 SuperIO**（`Motherboard → SubHardware`），
      只遍历顶层 `HardwareType.Cpu` 永远读不到 ⇒ `Flatten` 必须递归，子硬件继承父分类；
   ② `CCD1 (Tdie)` / `CCDs Max` / `CCDs Average` 名字里都有 Tdie，**不能**当成整颗 CPU
      （顺序：Package/Tctl/Tdie〔排除 CCD〕→ CCDs Max → Average → 逐核平均 → SuperIO）；
   ③ **0 ℃ 是合法读数，不许丢**（文档 §22.2）；只有 Linux sysfs 才把 0 当"驱动没填"的占位。
5. **客户端自己的日志在 `<StateDir>\logs\<日期>.ndjson`（Serilog 文件 sink）**，
   stdout 只有 ASP.NET 那 3 行。冒烟/探针断言日志内容时**必须读 ndjson 并解析**，
   按 `Level >= 2`（Warning）过滤 —— 读错地方 = 永远通过的空检查。
6. **`dotnet ef migrations add --no-build` 会用旧程序集 ⇒ 生成空迁移**；
   `migrations remove --no-build` 更坑：**删掉的是上一个迁移**（本次误删了 `M4_2`）。
   动手前先 `export DOTNET_ROOT=... && export PATH="$DOTNET_ROOT:$PATH"`、**去掉 `--no-build`**，
   并查真实库 `__EFMigrationsHistory` 确认受影响 ID 是否已应用。
7. **`SELECT notnull` 会被 SQLite 解析成 `NOT NULL`** ⇒ 报 `near "notnull": syntax error`。要写 `SELECT "notnull"`。
8. **前端三处 `—` 口径**：`fmtTemp(null)` → `—`；`thermalState != ok` 时实时条只显示状态词；
   温度曲线在 null 处**断开**（复用 `MetricsChart` 的 `splitSegments`）。
   `ok` 但某项为 null 是**正常**的（见 §14.1），页面照常显示 `—`。

### 14.6 验证手法

```bash
# ① 未勾选路径（无副作用）
python .tools/thermal-probe.py dist/agent/ControlServer.Agent.Service.exe        # 5/0

# ② 勾选路径端到端（真服务端 + 真客户端 + 真落库）—— 会尝试加载内核驱动！
python .tools/thermal-enabled-e2e.py                                              # 10/0

# ③ 打包产物上的冒烟温度段
CS_AGENT_EXE=dist/agent/ControlServer.Agent.Service.exe python deploy/smoke/run-all.py --only agent
```

- 实测（2026-10-12，本机**非管理员**）：① **5/0**（日志「未启用」、只出现 1 次、无 Warning、无 `WinRing0*` 落盘）；
  ② **10/0**，状态 `ok`、`gpuTempC` 实测 **42 / 44 ℃** 真实落库。
- ⚠️ **"驱动真的加载成功 ⇒ 读到 CPU / 主板温度"未验证**：那需要**管理员 / SYSTEM** 会话
  （服务形态天然是 LocalSystem）。这条缺口如实记录，不当已通过。

---

## 15. M4.4 文件分发 / 文件传输（2026-10-12 落地）

> 口径：**控制信令走 SignalR，实际字节走 Agent 主动 HTTP**。方案 §7.2 的取舍不是"可选偏好"——
> 它决定客户端**仍然一个入站端口都不开**（M4.1 铁律）。任何"让服务端直连客户机推文件"的改法都违反铁律。

### 15.1 三种语义与数据流

| 语义 | 方向 | 谁发起 | 字节怎么走 |
|---|---|---|---|
| **仓库共享** | 中心 → 浏览器 | 人在网页点下载 | `GET /api/files/{id}/download`（`PhysicalFile` + `enableRangeProcessing`） |
| **定向下发** | 中心 → 指定客户机 | 管理员建任务 | 客户机 `GET /api/files/{id}/download` 带 `Range` |
| **回传采集** | 客户机 → 中心 | 管理员建任务 | 客户机分块 `PUT /api/files/uploads/{id}?offset=N` |

下发六步：① `POST /api/distributions` → ② 落库 + `PushTransferTask`（只推**在线**目标）→
③ 客户机带 `Range` 拉取、边下边算 SHA256 → ④ 每 5% / 每次重试回报进度 → 服务端 `TransferProgress` 事件 →
⑤ 服务端自算摘要比对 → `.cs-part` → 校验 → `File.Move` 原子落地 → ⑥ 回报完成/失败。

### 15.2 文件地图（新增）

| 位置 | 文件 |
|---|---|
| 契约 | `Contracts/FileRepoDtos.cs`、`DistributionDtos.cs`、`Constants.cs`（`Policies.FileRead/FileIngest`、`AgentHubMethods`、`EventNames.TransferProgress`） |
| Core | `Models/FileRepository.cs`、`Abstractions/IFileRepositoryStore.cs`、`IDistributionStore.cs`、`Services/FileTransferMath.cs`（纯函数）、`FileRepositoryOptions.cs`、`FileRepositoryService.cs`、`DistributionService.cs` |
| Infra | `Persistence/Entities/FileRepoEntities.cs`、`AppDbContext.M4Files.cs`、`EfFileRepositoryStore.cs`、`EfDistributionStore.cs`、迁移 `20261012054937_M4_4_Files` |
| Server | `Api/FilesController.cs`、`Api/DistributionsController.cs`、`Api/AgentTransfersController.cs`、`HostedServices/FileTransferHostedService.cs` |
| Agent | `Transfer/TransferOptions.cs`、`TransferPaths.cs`、`FileTransferClient.cs`、`TransferWorker.cs` |
| 前端 | `components/FileRepoPanel.vue`、`DistributionPanel.vue`、`FilesPanel.vue` |
| 验证 | `deploy/smoke/transfer-smoke.py`（68 条）、`.tools/m44-ui-check/`（48 条）、`.tools/transfer-resume.py`（25 条） |

### 15.3 端点与**双身份**权限

| 端点 | 策略 | 谁能过 |
|---|---|---|
| `GET api/files`（列表） | `CanOperate` | Admin / Operator / Viewer |
| `GET api/files/{id}/download` | **`FileRead`** | Admin / Operator / **合法客户机**（`X-Agent-Key`）—— **不含 Viewer** |
| `POST api/files/uploads*`（上传四端点） | **`FileIngest`** | Admin / **合法客户机** —— **不含 Operator** |
| `DELETE api/files/{id}`、`DELETE api/files/uploads/{id}` | `AdminOnly` | Admin |
| `POST api/distributions`、`POST api/distributions/collect` | `AdminOnly` | Admin |
| `GET api/distributions*`（列表/详情） | `CanOperate` | Admin / Operator / Viewer |
| `api/agents/transfers*`（补领/进度/完成） | `AgentAuthentication.Scheme` | 合法客户机 |

- 🔥 **为什么仓库读要"人 + 机器同一个端点"**：客户机拉文件与浏览器下文件是**同一个字节流**，
  端点不同会让"下载链接"这种最基础的东西有两份实现。判据是
  `User.FindFirst(AgentAuthentication.ClaimAgentId) is not null`（**认机器身份，不认 `AuthenticationType`** —— 后者随 scheme 名改动会静默失效）。
- 🔥 **`FileIngest` 刻意不含 Operator**：能往仓库写 = 能把任意内容推给**所有**客户机。这是提权，不是"编辑文件"。
- 🔥 **`FileRead` 含 Operator 但不含 Viewer**：回传文件可能含现场敏感内容，Viewer 是"只读看板"角色。

### 15.4 分块上传协议（可断点续传）

```
POST /api/files/uploads              → { uploadId, chunkSizeBytes, receivedBytes: 0 }
PUT  /api/files/uploads/{id}?offset=N（请求体 = 这一块的原始字节）→ { receivedBytes }
POST /api/files/uploads/{id}/complete（可选 sha256）→ { id, name, size, sha256, kind }
```

- 🔥 **已收字节的唯一事实来源是 `.part` 文件的长度**，**不是数据库计数器**。
  `FileUploadSession` 刻意**不存** `ReceivedBytes` —— 两份计数器迟早不同步，
  而不同步的表现是"从错误位置继续写"：**长度对、内容坏**，摘要校验才发现，那时已经查不出来是谁先错的。
- **断线重连先 `GET /api/files/uploads/{id}` 问"收到哪了"**，从服务端回的真实偏移继续。
- **偏移不符返回 `409` + 真实 `receivedBytes`** —— 这不是错误，是"你该从这儿接着传"。
  客户机把它当新偏移继续，于是**不需要知道"刚才那块到底成没成"**。
- **`TotalBytes` 未知时进度返回 `0` 而不是 `100`**：`0` 是"还不知道"，`100` 会被读成"传完了"。
- **客户机发起的上传强制 `Kind=Collected`**：不让客户机自称 kind（否则能把回传伪装成 installer/normal）。
- 会话有过期时间（默认 24h），由后台任务清；**读取时才判过期**，不靠定时器"抢在读取前删掉"。

### 15.5 SHA256 全链路 + 原子落地

- **摘要谁落盘谁自算**：上传结束时**服务端自己算一遍**（客户端给的值只当"期望值"比对，**不信自报**）；
  下载时客户端边下边算。
- **原子落地**：先写 `.cs-part` → 校验 SHA256 → `File.Move(overwrite: false)`（同卷元数据操作，
  没有"半个文件可被下载"的窗口）。
- **落库失败把文件挪回暂存**（`TryMoveBack`），不留"无元数据的孤儿文件"——
  那种文件磁盘上占着空间、页面上不存在，是排障黑洞。

### 15.6 路径穿越防护 + 客户端白名单（**唯一安全边界**）

- 唯一实现 `FileTransferMath.IsInside`：**比较时必须带末尾目录分隔符**。
  少了它 `C:\data\files-evil` 会被 `StartsWith("C:\data\files")` 判成目录内
  （本项目已在图片目录上踩过这个坑）。
- 落盘名一律 `f_{guid}.ext`，**由服务端生成**；用户文件名只用于展示与取扩展名。
- 🔥 **白名单是客户端独有的职责**：服务端不知道对方磁盘长什么样，它只能"把目标目录原样下发"。
  客户机 `ResolveInboxDirectory` / `ResolveSourcePath` 解析不出就**一律拒收**
  （`fatal: true`，不重试）—— 宁可让运维看到"目录不在白名单"，
  也不让一个错拼的路径变成一次任意写。
- 客户端认三种写法：① 空 ⇒ 默认目录；② 绝对路径 ⇒ 与白名单**逐项精确比对**；
  ③ 目录末段名（`logs` / `inbox`）⇒ 按 `Path.GetFileName` 比对。
- **白名单上报**（`AgentInventoryDto.TransferInbox/OutboxDirectories`）：让分发页能把"这台机器允许的目录"
  列成候选，运维**从里面挑**而不是手输撞白名单。
  - 🔥 `null` = 老客户端没这个字段（页面退回手输）；`[]` = **本机未启用文件传输**。两者文案不同。
  - ⚠️ 它只是**提示**，不是安全边界 —— 拦截仍然只发生在客户机落地前。
  - 上报走**硬件清单**（机器级恒定量、6 小时一次），不新开通道。

### 15.7 离线机器也能收到文件

- 建任务时**一次性为每个目标建满明细行**（`pending`）。离线机器连接 Id 为空 ⇒ 不推 SignalR，
  它**上线时调 `GET /api/agents/transfers` 补领**。
- **归属校验**：客户机只能动自己那一行（`FindOwnedItemAsync` 比 `AgentId`）——
  少了它，任何拿到 AgentKey 的机器都能把**别人**的传输标成完成/失败。
- **进度幂等且不倒退**：终态直接原样返回；已记字节只增不减（迟到的旧事件不许把进度条往回拨）。
- **只终态任务可清**（`DistributionStates.IsTerminal`）：还在等离线机器上线的任务**再老也不删**，
  否则那台机器上线补领不到、运维看到"任务无故消失"。

### 15.8 授权闸门分类（判据：**会不会真的作用到别的机器上**）

- **Gated**：`POST api/distributions`、`POST api/distributions/collect`
  —— 建任务 = 让一批客户机去下载/上传（与 `POST api/agents/{id}/reboot` 同类）。
- **Allowed**（13 条）：仓库自身（8 条，全在服务端本地完成，字节不出这台机器）+
  分发查询（2 条）+ 客户机三端点（3 条，只有任务真建起来才有流量，绕不开闸门）。

### 15.9 陷阱清单（M4.4 专属）

1. 🔥 **MVC 路由常量不能带前导斜杠**：`Routes` 里给 `MapHub` 用的常量是 `/hubs/...`；
   若控制器写 `[Route("/api/files")]`，路由模板变成 `/api/files`，
   而闸门按 `StartsWith("api/")` 判定 ⇒ **静默判不中、失去拦截**。
   **控制器一律用不带斜杠的内联 `[Route("api/files")]`**。这条写进了 `AgentHubMethods` 的注释。
2. 🔥 **实体的 `TaskId = 0` 指望 EF 回填不成立**（未配导航关系）。
   `EfDistributionStore.CreateTaskAsync` 用**事务内两步 `SaveChangesAsync`**：
   先插任务拿自增 Id，再建明细。
3. 🔥 **SQLite 的 EF 提供程序拒绝翻译 `DateTimeOffset` 的 ORDER BY**（M4.2 踩过）。
   文件列表按上传时间倒序、任务列表按创建时间倒序 ⇒ 一律存 **Unix 毫秒**（`TelemetryTime.ToMs/FromMs`）。
4. **文件列表/任务的"倒序"口径要一致**：服务端返回"最近在前"，前端新建后**去重插到最前**，
   而不是重新排序（服务端才是权威顺序）。
5. 🔥 **`MigrationTests` 的表清单与迁移条数必须同步**：#179 当时只编译没跑全量测试，
   于是"迁移历史还是 7 条、缺 4 张新表"的回红被漏过一轮。**加迁移后必须跑全量测试**。
6. **前端上传必须用 XHR，不能 fetch**：`fetch` 没有上传进度事件，8MB 分块会"卡住不动"到整块传完。
7. **前端必须有"偏移不前进就抛错"的守卫**，否则是**死循环**（表现：进度条卡住 + 请求无限重发）。
8. **前端上传不报 SHA256**：浏览器算 2GB 摘要要读进内存；服务端自算那份才是权威。
9. **`TransferProgress` 事件不带任务级状态** ⇒ 前端按明细重算汇总（镜像 `SummarizeTaskState`），
   终态事件再额外拉一次全量。已收字节**只增不减**。
10. **「分发」按钮要用方法调用而不是 prop**：走 prop 时"同一文件再点一次"值没变、
    `watch` 不触发 ⇒ 表现为"第二次点没反应"。
11. **文件页要订阅 `AgentChanged/Online/Offline`**：只在挂载时拉一次在线表 ⇒
    用户在文件页等一台刚开机的机器，目标列表里**根本没有它**，必须切页才出现。
12. **`fmtBytes` 原先到 MB 就停**，>2GB 会显示成 `2048.0 MB`。补到 GB 档。
13. **`HTTPCookieProcessor` 上那个 cookie jar 字段叫 `cookiejar`**（不是 `_cookies`）。
    猜错的表现是"Cookie 头是空的"⇒ 裸连接 401，**看起来像"下载端点需要额外授权"**，
    其实是自己把凭据弄丢了。**"凭据为空"先怀疑取凭据的代码。**
14. **两个 HttpClient**：控制面（补领/进度/完成）短超时；字节面（搬 2GB）**没有总超时**。
    共用一个就只能二选一。
15. **传输线程跑在进程生命周期上**，不随 SignalR 生灭 —— 一次心跳抖动不该掐掉正在传的大文件。

### 15.10 验证结果（2026-10-12）

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln` | **0 警告 / 0 错误** |
| `dotnet test` | **Core 707 + 集成 316 = 1023 通过 / 0 失败** |
| `deploy/smoke/transfer-smoke.py` | **68 / 0**（含真客户端端到端：下发落地 + 回传入库 + 越界被拒） |
| `.tools/m44-ui-check/`（真浏览器 + 真客户端） | **48 / 0**（分块上传 3 块、Range 下载、进度中间态、落地 SHA256、两段式删除） |
| `.tools/transfer-resume.py`（**>2GB 验收**） | **25 / 0**（实测 **2.5 GiB**：上传 896MB 处掐断 → 等 30s → 真实偏移续传；下载 64MB 处断连 → 等 30s → `Content-Range` 续传；两处 SHA256 全链路一致） |
| **打包产物冒烟**（服务端 `dist/win-x64`、客户端 `dist/agent`） | **7/7 场景全过**（web / pairing / admin / agent / transfer / license / rbac）；单文件形态未被 M4.4 破坏（`dist\win-x64` 严格 1 文件 50.5 MB） |

### 15.11 本轮**没做** / 顺延

- **与按钮/动作系统打通**（方案 §7.4）：只留接口，未实现 `Protocol=File` 动作。
- **客户端自更新**（需签名 + 回滚）：未做。
- **限速 / 断点续传的服务端侧配额**：未做；客户端用"限 2 路并发"提供天然背压。
- **Linux 客户端**：`TransferOptions` 与传输逻辑是跨平台的，但 Linux 客户端宿主未构建（与 M4 整体一致）。

---

## 16. M4.5 客户机配置与任务下发（2026-10-12 起）

来源：`D:/Project/BootAssistant`（.NET MAUI，**只读参考**；"要有哪些功能"以它为准，"怎么实现"以本项目文档为准）。
方案：`docs/M4-客户端Agent与局域网管控方案.md` §1.3 / §2.2（25 项功能表）/ §9.x / §10.x / §14 / §15（决策 D10）。

### 16.1 §2.2 的 25 项对照结论

- **12 项复用**（M4.1–M4.4 已落地）：发现 / 注册 / 心跳 / 在线状态 / 遥测 / 开关机记录 / 硬件清单 / 文件下发 …
- **2 项不需要**：#3「启动时隐藏窗口」、#7「关闭转最小化」—— 托盘进程**没有主窗口**，这两个语义不存在。
- **11 项本轮新增**：服务侧 #10 UDP 指令通道 / #11 远程关机(#22 重启) / #14 定时发送 / #15 延时发送 /
  #21 循环发送 / #24 配置中心化；会话侧 #12 音量 / #13 静音 / #18 延时启动程序；＋跨进程命名管道 IPC。
- **三条岔路（用户已拍板，不要再问）**：① 本期范围 = **全做**；② UDP 签名密钥 = **复用 agentKey 派生**；
  ③ IP 白名单 = **服务端下发**（随 AgentConfigs 一起下；名单为空 = 不按 IP 限制，仅凭 HMAC 判来源真实性）。

### 16.2 数据模型（**照搬参考项目**，三张清单）

| 参考项目 | 本项目 | 关键语义 |
|---|---|---|
| `SendTask{DelaySeconds, Protocol, TargetIp, TargetPort, DataType, Data, Enabled, IsLoop, LoopIntervalSeconds}` | `AgentSendTaskDto` | 🔥 **`IsLoop` 标志即「循环发送」，不是另一张表**（早期方案文档把它写成独立一栏是笔误） |
| `ScheduledSendTask{Times, Enabled, Time}` | `AgentScheduledTaskDto` | `Times` 是**列表**（多时刻）；**刻意不保留单值 `Time` 旧兼容字段**（新表不带历史包袱） |
| `StartupTask{Name, ProgramPath, Arguments, DelaySeconds, Enabled}` | `AgentLaunchTaskDto` | 🔥 **多个任务串行累加延时**（现场靠这个按顺序拉起多个程序） |
| `AppSettings{ListenPort=8080, ...}` | `AgentUdpChannelDto{Enabled, Port=8080, AllowedSourceIps}` | 默认端口沿用 8080（参考项目习惯），但**由服务端下发、可随时改** |

### 16.3 决策 D5：**事实来源是服务端**

- **整包下发、整包替换、不做差量**（配置体量很小，差量协议的"两边版本对不上"远比省下的带宽贵）。
- **版本号单调递增**（从 1 开始；`0` = 该机还没有配置）。客户端据此判新旧，**乱序到达的旧版必须丢弃**。
- **降级运行**：中心不可达时跑本地**只读缓存**的最后一版，**不接收新任务**（客户端不提供任何"本地覆盖"入口
  —— 那会制造"网页显示一份、机器实际跑另一份"）。
- **两条腿自愈**：① 推送（`ConfigChanged` **只带版本号**，正文走 HTTP 拉）；② 版本对齐（`Welcome` 与
  `HeartbeatAck` 回传服务端当前版本，客户端落后就自己拉）。
- **「已应用版本」只增不减**：SQL 用 `UPDATE … SET x = MAX(COALESCE(x,-1), @v)`；内存快照同步也只增
  （迟到的旧回报不能把页面上的版本退回去）。`AppliedConfigVersion` 字段**必须可空**（null = 从未回报，
  **不能拿 0 冒充"已应用 v0"**）。

### 16.4 服务端（四层已落地）

- **契约层** `src/ControlServer.Contracts/AgentConfigDtos.cs`（新增）+ `Constants.cs` / `AgentDtos.cs`（改）
  - DTO 一族：`AgentUdpChannelDto` / `AgentSendTaskDto` / `AgentScheduledTaskDto` / `AgentLaunchTaskDto` /
    `AgentConfigDto` / `AgentConfigSaveRequest` / `AgentConfigSaveResult` / `AgentConfigChangedDto` /
    `AgentConfigAppliedDto`；`AgentPowerActions` / `AgentPowerRequest` / `AgentVolumeActions` / `AgentVolumeRequest`。
  - `AgentConfigDto.UpdatedAt` **可空**（null = 还没配置，**不拿"当前时间"凑** —— 那会让人以为刚配过）。
  - `AgentConfigSaveRequest` 的约定：**null = 不动那块；空数组 = 清空那块**。
  - `Constants.cs`：新增 `AgentHubMethods.ConfigChanged`、**`AgentHubClientMethods`**（客户端→服务端方向：
    `Heartbeat`/`ReportMetrics`/`ReportPowerEvents`/`ReportInventory`/`ReportConfigApplied`）、
    `Policies.AgentConfigRead`、`EventNames.AgentConfigChanged`（载荷 = 客户机 Id，与高频 `AgentChanged` 分开）。
  - `AgentDtos.cs`：`AgentCommandNames` 新增 `Power` / `Volume`（`Restart` 注释补"**这不是重启计算机**"）；
    `AgentHostDto` 末尾加 `ConfigVersion` / `AppliedConfigVersion`。

- **Core 层**：`Models/AgentConfig.cs`（领域模型，不可空规范化形态 + `AgentConfigDefaults` 边界常量）、
  `Abstractions/IAgentConfigStore.cs`、`Services/AgentConfigRules.cs`（**纯函数**：`Merge` / `Normalize` / `Validate`
  —— **返回问题清单而不是抛第一个错**；`9:5`/`09：05` → `09:05`）、`Services/UdpCommandSignature.cs`（见 16.6）。
  `IAgentHostStore` 加 `SetAppliedConfigVersionAsync`，`AgentHost` 加 `int? AppliedConfigVersion`。

- **Infrastructure 层**：`Entities/AgentConfigEntity.cs`、`AppDbContext.M45Config.cs`（表 `agent_configs`，
  **`HasIndex(AgentId).IsUnique()`**）、`EfAgentConfigStore.cs`（Singleton + 内部 scope + `_writeLock`；
  🔥 **SaveAsync 读改写版本必须串行化** —— 并发会双双读到 v3 都写 v4、**静默吞掉一次变更**；
  🔥 **坏 JSON 会抛**（与在线表 `HostInfoJson` 吞异常刻意不同：配置读不出**不能**降级成空配置））、
  `EfAgentHostStore.SetAppliedConfigVersionAsync`（`ExecuteSqlInterpolatedAsync` + `MAX(COALESCE(…,-1), @v)`）。
  **第 9 个迁移** `20261012175800_M4_5_AgentConfig`：`agent_hosts.AppliedConfigVersion`（可空 INTEGER）+
  `agent_configs`（`Id`/`AgentId`/`Version`/`UpdatedAtMs`(long)/`Json`）+ 唯一索引。`MigrationTests` 表清单 8 → 9。

- **Server 层**
  - `Services/AgentConfigService.cs`（新增）：`_versions` **内存版本缓存（写穿透：先落库再改缓存）**
    —— 心跳每 10 秒要回传版本，100 台 = 10 次/秒的库读完全没必要；`AcceptAppliedAsync` 落库 + **同步刷内存快照**
    （不刷的话页面上"已应用 v3"最多晚 60 秒才出现，运维会以为下发失败又点一次）；
    `PushConfigChangedAsync`（离线机器不推，它上线时靠 `Welcome` 对齐）；`RequestReloadAsync`（发 `Command(RefreshConfig)`）。
    ⚠️ **"同步刷内存快照"这句在 #192 之前是假的**（当时写成 `ApplyChange(host with {...})` ⇒ **静默空操作**）——
    两条必须避开的写法见 §16.7 的 **12 / 13**。
  - `Hubs/AgentHub.cs`：`Welcome` 的 `ConfigVersion` 由 `GetVersionAsync` 填（原先写死 0）；`Heartbeat` 改 `async`
    返回**真实**配置版本；**新增 `ReportConfigApplied(AgentConfigAppliedDto?)`**（`RequireAgent` 复核 + `AcceptAppliedAsync`）。
  - `Api/AgentsController.cs`：新增 `GET {id}/config`（`AgentConfigRead`）、`PUT {id}/config`（`AdminOnly`，
    Merge→Normalize→Validate→Save）、`POST {id}/config/redeliver`（`AdminOnly`）、`POST {id}/power`、`POST {id}/volume`。
    原 10 处 `Mutation(...)` 全改 `MutationAsync(..., ct)`（列表要带配置版本就得查库）。
  - `Security/LicenseGate.cs`：**Gated** 加 4 条（`power` / `volume` / `PUT config` / `config/redeliver`）；
    **Allowed** 加 1 条（`GET config` —— 客户机拉自己配置才能跑任务）。
  - `Program.cs`：DI + 授权策略 `AgentConfigRead`（`AddAuthenticationSchemes(agentSchemes)` +
    `FindFirst(ClaimAgentId) is not null || IsInRole(Admin) || IsInRole(Operator)`）。

### 16.5 客户端（Agent 侧已落地）

文件：`agents/ControlServer.Agent/Config/{AgentConfigClient,AgentConfigCache,AgentConfigManager}.cs`、
`Tasks/{SessionCommandChannel,AgentTaskRunner}.cs`；`AgentOptions` 加 `AgentTaskOptions Tasks`；
`AgentPaths.ConfigCacheFile` = `config-cache.json`；`AgentConnection` 加 `ConfigChanged` 事件 +
`ReportConfigAppliedAsync`；`AgentRuntime` 接线；`.Agent.Service/Program.cs` DI + 启动自述日志。

- **`AgentConfigClient`**：`GET /api/agents/{id}/config`，凭据走**请求头**（查询串会被服务端请求日志记下来，等于把
  agentKey 写进服务器日志）。**404 一律按"暂无配置"返回 null**（不翻译成"一份空配置去覆盖本机" ——
  ｜否则"一次瞬时 404"会变成"客户机任务全被清掉"，代价不对称）。要清空任务就在页面上保存一次（版本 +1）。
- **`AgentConfigCache`**：**只读**缓存，存**"已经应用成功的那一版"**（不是"拿到手的最后一版"）；
  临时文件 + 原子替换（半截 JSON ⇒ 下次启动读不出 ⇒ "离线时任务全没了"，看着像服务端问题）。
- **`AgentConfigManager`**：版本判新旧 + 应用 + 通知。🔥 **`SemaphoreSlim(1,1).WaitAsync(0)`** 把
  Welcome / ConfigChanged / 心跳 / 手动刷新**四条路串起来** —— 没有它，四路同时拉取会产生四个"新版本"，
  各自重启一遍任务，表现是"改一次配置，循环发送被启动了四次（四个循环同时往一个目标发）"。
- **`AgentTaskRunner`**：**"整代重启"** 模型 —— 换一份配置就把上一代全部执行体取消、按新配置重起。
  只有一个取消源，不需要维护 `Id → 执行体` 映射（配置里的 Id 是前端生成的，可能被复制粘贴产生重复）。
  - 三类任务：**延时/循环发送**与**定时发送**跑在**服务侧**（纯网络发送，不需要桌面）；
    **延时启动程序**交给**会话侧**（`ISessionCommandChannel`）。
  - 延时语义 = **相对于本次应用配置**（照搬参考项目"相对于客户机启动"，多一个"配置可在运行期换"的维度）。
  - 定时发送**不用每秒轮询**：算出"下一个触发点"再睡过去，**等待切成 ≤30 秒的小段**（长 `Task.Delay` 在
    系统休眠/改钟后会整体偏移；切段则每段重新按当前时间算，跨午夜的日期翻转也自然处理）。
    时间源 `BeijingTime.Now`；触发后**跳过 61 秒**（`Task.Delay` 可能因定时器粒度在"还差几毫秒"时返回 ⇒
    同一分钟发两遍）。
  - 🔥 **循环任务只在第 1 次打成功日志**、失败按 **1 / 每 20 次**节流 —— 5 秒一次的成功日志会在一夜之间
    把客户机日志刷到几十 MB（M4.2 踩过同一个坑）。
  - 🔥 **取消后不能立刻 `Dispose` 取消源**：还在 `Task.Delay` 上的任务没摘掉注册，Dispose 会让它抛
    `ObjectDisposedException` —— 一个只在"改配置的瞬间恰好到点"时才出现的偶发异常。收尾走
    `TrackAsync`（等这一代真正退出再 Dispose）。
- **`AgentRuntime` 接线点**：
  - `RunAsync` 开头先 `RestoreFromCache()` —— **中心不可达时这台机器仍要做它该做的事**；
  - `OnWelcome` → 按 `welcome.ConfigVersion` 对齐（不 await，避免拖住心跳）；
  - `OnConfigChanged` → 按推送的版本号对齐；
  - **`RefreshConfig` 指令 → `force: true`**（🔥 刻意不看版本号：这条指令就是为"版本没变但机器看起来没按配置动"
    准备的，看版本再决定拉不拉等于把它废掉）；
  - 心跳里做两件小事：**补报**（`_reportedConfigVersion` ≠ `AppliedVersion` 时）与**版本对齐**
    （`ack.ConfigVersion > AppliedVersion` 时）。🔥 **"已应用版本"与"已回报版本"必须分开记** ——
    应用成功 ≠ 回报成功，没有补报的话页面会永远停在"已下发 v5 / 已应用 v4"；
  - `OnRevoked` → `_tasks.Stop()` + `_configs.Clear()`（同时清缓存，让重新注册后能从 0 重新对齐，
    不会出现"任务卡在停止状态"）。
- **`ISessionCommandChannel`**：服务侧 → **用户会话**（托盘）的指令通道。当前注册的是
  **`UnavailableSessionCommandChannel`（明确不可用 + 一句可读原因，会进启动日志）**，
  「延时启动程序 / 音量」会被**明确跳过并说明原因**，而不是静默什么都不做。接入后换掉这一行注册即可，
  其余代码一行不用改。

### 16.6 UDP 指令通道的签名设计（**绝不能照抄参考项目的无鉴权版本**）

参考项目：`IPAddress.Any` + 明文收 `"DOWN"` ⇒ **同网段任意主机（包括一台被插进来的笔记本）都能把设备关掉**。

- **线路格式**（一行 UTF-8，便于 `nc` 手工调试）：
  `CS1|<command>|<unixSeconds>|<nonce>|<base64url(sig)>`；签名对象 = 去掉签名的那一整串
  `CS1|<command>|<unixSeconds>|<nonce>`；`sig = HMAC-SHA256(通道密钥, 上面那串)`。
  **竖线不属于 base64url 字母表 ⇒ 按 `'|'` 切分不会歧义**。
- **密钥来源**：由该机 `agentKey` 经 **HKDF-SHA256** 派生 32 字节（salt `ControlServer.UdpChannel`，info `channel-v1`）。
  🔥 **刻意不直接用 agentKey**：那是这台机器的**身份凭据**（能注册、能连 Hub、能改自己的一切），
  把它交给每一个"要发指令的发送方"＝把设备身份散给现场每一台控制器。**HKDF 单向**，派生密钥泄漏不影响 agentKey。
  同一个 agentKey 永远派生出同一个密钥 ⇒ 客户机不必额外存一份通道密钥。
- 时间窗 **±120s**；🔥 **先验签、后看时间**（反过来的话，攻击者能免费探测"签名对不对"这个分支）。
- `UdpReplayGuard`：**有界 `LinkedList`（容量 1024，窗口 240 秒）** 防重放。
- `IsSourceAllowed`：IP 白名单，**IPv4-mapped IPv6 要 `MapToIPv4()` 再比**。
- 命令映射（**双语写法**，兼容现场已有脚本）：`DOWN→power:shutdown`、`REBOOT→power:restart`、
  `+→volume:up`、`-→volume:down`、`0→volume:mute`、`1→volume:unmute`。
- **关闭 / 重启 Agent 服务 / 重启整机三者分开**：`AgentCommandNames.Restart`（重启 **Agent 服务**）
  ≠ `AgentCommandNames.Power` + `AgentPowerActions.Restart`（重启**整机**）。混用 = 「想重启服务却把机器关了」。
- **Session 0 隔离**：音量 / 延时启动程序**必须在会话侧（托盘）执行**；服务侧只做电源操作
  （`shutdown`，SYSTEM 天然有权限）。

### 16.7 陷阱清单（M4.5 专属）

1. 🔥 **`[Route("api/...")]` 不能带前导斜杠** —— 闸门 `LicenseGate` 按模板 `StartsWith("api/")` 判定，
   `[Route("/api/xxx")]` 产出的模板是 `/api/xxx`，**静默判不中、闸门形同虚设**。
2. 🔥 **双身份同端点时，归属校验只能在控制器里做**：策略 `AgentConfigRead` 只能判断"是不是一台合法机器"，
   判断不了"它是不是**这台**"。少了 `AgentId` 显式比较，任何一台机器都能读到别台的配置（含目标 IP 与发送内容）。
3. 🔥 **机器身份读配置时不能查内存快照**：客户机常在注册成功后**下一秒**来拉配置，而在线表快照最长落后 60 秒
   ⇒ `FindHost` 返回 null ⇒ 客户端拿 404。身份已由 AgentKey 对上库校验确认，**不需要快照背书**。
4. **`AgentConfigService` 的版本缓存必须"写穿透"**（先落库再改缓存）；反过来会在落库失败时留下
   "页面说 v5、库里还是 v4"的假象。
5. **`EfAgentConfigStore.SaveAsync` 的"读当前版本 → +1 → 写"必须串行化**，否则并发保存双双读到 v3、
   都写 v4，**静默吞掉一次变更**。
6. **配置 JSON 坏掉要抛，不要降级成空配置**（与 `HostInfoJson` 吞异常**刻意不同**）。
7. **`AgentConfigDto.UpdatedAt` 可空**，别拿"当前时间"凑 —— 凑出来的时间戳会让人以为"这台机器刚配过"。
8. **`dotnet ef migrations add` 不要加 `--no-build`**（会用旧程序集生成**空迁移**）；要 `export DOTNET_ROOT` + `PATH`，
   用 Infrastructure 当 `--project`/`--startup-project`（用 Server 当启动项目会**真把服务拉起来**）。
9. **`HttpRequestMessage.Headers` 没有索引器**（是 `HttpRequestHeaders`，不是 `Dictionary`）⇒
   用 `TryAddWithoutValidation`，写 `headers[name] = value` 直接 `CS0021`。
   （`HubConnectionOptions.Headers` 才是字典，那边可以索引赋值。）
10. **`AgentRuntime` 的 `AgentConfigManager` 与 `AgentTaskRunner` 必须成对提供或成对省略**：
    只给配置没有执行者 = "拉了配置却没人跑"，那种组合不该被表达出来（构造里显式校验）。
11. **`AgentTaskRunner.DisposeAsync` 要幂等**：它既是 DI 单例（宿主停机时会 Dispose 一次），
    也会被 `AgentRuntime` 在主循环收尾时显式收一次。
12. 🔥 **`AgentHostRegistry.ToDto` 必须显式带上 `AppliedConfigVersion`**（#192 抓到）：
    该字段**可空**，而 record 的可选参数默认 `null` —— 构造 DTO 时漏传**不报错、不告警**，
    表现是 `GET /api/agents` 与 `GET /api/agents/{id}` 里这个字段**恒为 null**，
    页面永远显示"未回报"（哪怕客户端明明回报过）。原三处调用点都只
    `with { ConfigVersion = … }`，谁也没补它 ⇒ **修在 `ToDto` 里**，不要分散到调用点去补。
    回归：`IntegrationTests/AgentConfigVersionTests.cs`（其中一条**先红后绿**，就是它抓出来的）。
13. 🔥 **不能用 `AgentHostRegistry.ApplyChange` 刷业务字段**（#192 抓到）：
    它**只读传入 host 的 `IsRevoked` / `LastSeenAt`**，其余字段一律忽略 ⇒
    `ApplyChange(host with { AppliedConfigVersion = v })` 是**静默空操作**（不报错、不生效）；
    而且 `_hosts` 是 `volatile AgentHost[]`、**整体换代的不可变快照**，改 record 也反映不进去。
    正解是加针对性方法（`SetAppliedConfigVersion(id, version)`：`Clone()` 数组、只替换那一个元素、
    **只增不减**）。一句话教训：**"改不可变 record"与"让快照数组看见"是两件事**。

### 16.8 验证结果（#185–#189）

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln`（每层逐层构建） | **0 警告 / 0 错误** |
| `dotnet test` | Core **707 / 0**；集成测试见下 |
| 第 9 个迁移 | `20261012175800_M4_5_AgentConfig`，**已验证非空（53 行）**；`MigrationTests` 表清单 8 → 9 并补 `agent_hosts.AppliedConfigVersion` 可空断言 |


---

## 17. M4.5 客户端侧：UDP 加固指令通道（2026-10-13）

### 17.1 落地文件

| 文件 | 职责 |
|---|---|
| `agents/ControlServer.Agent/Tasks/UdpCommandListener.cs` | UDP 加固指令通道（监听 + 四道校验 + 派发），`Apply(config, agentKey)` 是唯一入口 |
| `src/ControlServer.Platform.Abstractions/HostPower.cs` | `IHostPowerController` + `UnsupportedHostPowerController` |
| `src/ControlServer.Platform.Windows/WindowsHostPowerController.cs` | 调 `shutdown.exe /s|/r /t 5` |
| `Core/Services/UdpCommandSignature.cs`（改） | `TryVerify` 改为输出 `UdpVerifiedCommand`（带 `nonce`）；指令名改用契约层常量 |
| `AgentOptions`（改） | `AgentTaskOptions.ExecuteUdpCommands`（演练开关，默认 true） |
| `AgentRuntime`（改） | 接线：`OnConfigApplied` / 连上后 / 被吊销 / 收尾四处 |
| `.Agent.Service/Program.cs`（改） | DI + 启动自述两行 |
| `deploy/windows/{install,uninstall}-agent.ps1`、`ControlServer.Agent.iss`、`build-agent.ps1` | 入站防火墙规则 |

### 17.2 四道关卡（任一不过即丢弃，**绝不回包**）

1. `Agent:Tasks:Enabled=false` ⇒ **根本不绑定端口**（不是"绑定了再丢弃"）；
2. 来源 IP 白名单（空 = 放行；白名单只是纵深防御，判"合法"靠签名）；
3. HMAC-SHA256 验签（密钥 = HKDF(agentKey)，**先验签后看时间**）；
4. 防重放（`UdpReplayGuard`，进程级字段，**不随端口变更重置**）。

### 17.3 陷阱清单（M4.5 UDP 通道专属）

1. 🔥 **丢弃日志必须按时间节流**：第 1 条立刻记，之后每 5 秒一条并附"被压掉 N 条"。
   一次灌包可每秒上万条，逐条记录会写爆客户机磁盘、日志滚动还会挤掉有用历史 ——
   **"防重放"的实现反过来变成一种 DoS**。
2. 🔥 **防重放表做进程级**，不做"每次监听一份"：否则攻击者等一次配置变更就能重放旧报文。
3. 🔥 **`TryVerify` 显式交出 `nonce`**，不让调用方自己再切一遍报文 ——
   切分规则是"线路格式"的一部分，只能有一处实现（两处实现的典型后果是按老格式取错段）。
4. 🔥 **取消挂起的 UDP 接收，Windows 抛的是 `SocketException(995, WSA_OPERATION_ABORTED)`**
   而不是 `OperationCanceledException` ⇒ 必须补
   `catch (SocketException) when (ct.IsCancellationRequested)`，
   否则每次停监听都多打一行"网卡抖动"**假警告**（假警告会让真警告变得不可信）。
5. 🔥 **`UdpClient` 的 `Stop()` 里 `Dispose` 了 socket，但取消源要等收包循环真正退出再 Dispose**
   （`FinishAsync`）；否则"改配置的瞬间恰好来了一条报文"会抛 `ObjectDisposedException`。
6. **`AgentConfigManager` / `AgentTaskRunner` / `UdpCommandListener` 三者必须一起提供**：
   UDP 通道吃的是"配置里的 Udp 节 + 本机 agentKey"，缺一样就是个永远起不来的监听。
   构造里显式校验（与 M4.5 那一对同一个理由）。
7. **`Apply` 必须在两个时机都调用，少一个就会在一条路径上"通道没起来"**：
   ① 配置换版；② **本机刚注册成功**。因为配置可能在"还没注册"时就从本地缓存载入了
   （降级运行），那时派生不出密钥；而如果服务端版本恰好与本机相同，
   `ConfigChanged` 与 `Applied` 事件**都不会再来**，只有"连上后补一次 Apply"能救 ——
   少了它，现象是"重启一次之后 UDP 就不灵了"。

### 17.4 电源控制为什么要做成端口

- **`shutdown.exe` 而不是 P/Invoke `ExitWindowsEx`**：后者只是"通知系统准备关机"，
  不会真的断电（还得自己调 `SetSystemPowerState`），而 `shutdown.exe` 一把做对；
  更要紧的是 `/t 5` 延时期内可用 `shutdown /a` **撤销**（误发后有救回来的机会）。
- 命令名与参数是平台专有的 ⇒ 按铁律 #2 落在 `Platform.Windows`；
  非 Windows 注册 `UnsupportedHostPowerController`（带可读原因，进启动日志）。
- **"已发起" ≠ "已关机"**：返回 true 的含义是"命令已被系统接受"，真正断电在几秒之后。
- 退出码原样进日志（`5` = 拒绝访问），而不是笼统的"失败了"。

### 17.5 防火墙：**打破了"客户端不开任何入站端口"这条旧承诺**

原状：`install-agent.ps1` 与 M4 方案 §5.5 都写着"客户端不开任何入站端口、安装时不建任何防火墙规则"。
M4.5 的 UDP 指令通道是该承诺的**唯一例外**，而 Windows 防火墙默认丢入站 ⇒
规则不建时通道**能绑定成功但一个包都收不到**（正是本项目最讨厌的静默失败）。

**决策（用户拍板）：安装时按「程序」放行。**

| 方案 | 结论 |
|---|---|
| **按程序放行（采用）** | `New-NetFirewallRule -Protocol UDP -Program <Agent 服务 exe>`；一次装好、**换端口不用再动** |
| 按端口放行（否） | 端口由中心下发、可随时改 ⇒ 没法预先写死，改漏了表现是"指令发出去机器没反应" |
| 运行时动态加/删（否） | 生命周期最难管：进程被强杀残留旧端口规则；运行时改机器防火墙，面太大 |

- 作用域确实比端口级略宽（该程序可监听任意 UDP 端口），但**鉴权在程序内部**（验签 + 白名单），
  防火墙只解决"包收得到收不到"。
- 规则显示名固定 `ControlServer Agent UDP`；重装**先删后建**（幂等）；
  **无条件创建**（通道启不启用是中心下发的，装的时候还不知道；通道关着时该规则无副作用）。
- 新增 `-SkipFirewallRule`（现场有统一防火墙策略 / 本机不用该通道时跳过）；
  跳过时**打警告**而不是静默。
- 🔥 **安装侧与卸载侧对失败的处理刻意相反**：
  安装侧 **吞掉**异常（与 `install-service.ps1` 一致 —— 不让"防火墙建不上"变成"服务没跑"），
  只给手工放行提示；卸载侧 **不吞**（明说"请手工删规则"）——
  吞掉就留下一条谁也看不见、谁也想不起来的**悬空规则**，
  下次有别的程序恰好装到同一路径时会莫名"白得"一次入站放行。
- 安装脚本步骤 5/5 ⇒ 6/6（新增"放行 UDP 指令通道入站"，位于**复制程序之后**：
  `-Program` 指向的路径不存在时 `New-NetFirewallRule` 会直接报错）；
  卸载脚本 4/4 ⇒ 5/5（新增"清理防火墙规则"，位于**删程序之后**）。

### 17.6 演练模式（`Agent:Tasks:ExecuteUdpCommands`）

默认 `true`。置 `false` 时**白名单 / 验签 / 防重放全部照常走完**（它们才是要验证的东西），
只是不真的关机、不真的调音量。两个用途：
① 冒烟测试（要验证签名与白名单，但不能把测试机真的关掉）；
② 现场首次接线时先观察"到底谁在发指令"，确认无误再放行。

⚠️ 它与服务端下发的 `Udp.Enabled` 是**两个维度**，不能互相替代：
服务端那个决定"有没有这条通道"（内容听中心的），这个决定"本机此刻敢不敢动手"（边界听本机的）。

### 17.7 验证结果（#190）

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln` | **0 警告 / 0 错误** |
| `dotnet test` | **Core 707 + 集成 316 = 1023 / 0**（与 #189 后一致 ⇒ 无回归） |
| `install-agent.ps1` / `uninstall-agent.ps1` 语法 | 解析 **0 错误**；函数定义在调用点之前 |
| `.ps1` 编码 | UTF-8 **with BOM**、CRLF、无孤行 LF / 无重复 CR |

⚠️ **UDP 端到端真机验证尚未跑**（属于 #193）：届时用 `ExecuteUdpCommands=false`
演练模式验证"签名正确才被接受、篡改/错误密钥/超窗/重放一律丢弃"，不真的关机。

---

## 18. M4.5 会话侧：命名管道 IPC + 音量 / 静音 + 延时启动程序（2026-10-13）

### 18.1 落地文件

| 文件 | 侧 | 职责 |
|---|---|---|
| `src/ControlServer.Platform.Abstractions/HostAudio.cs` | 端口 | `IAudioController` + `HostAudioAction` / `HostAudioState` / `HostAudioResult` + `UnsupportedAudioController` |
| `src/ControlServer.Platform.Windows/WindowsHostAudioController.cs` | 平台 | CoreAudio（`IAudioEndpointVolume`）实现 + COM 声明 |
| `src/ControlServer.Platform.Windows/WindowsSessionLocator.cs` | 平台 | `WTSGetActiveConsoleSessionId` ⇒ "用户的桌面对应哪个会话" |
| `agents/ControlServer.Agent/Tasks/SessionPipeProtocol.cs` | 共用 | 管道名 + 单行 JSON 约定 + 三个超时常量 |
| `agents/ControlServer.Agent/Tasks/SessionPipeServer.cs` | 托盘 | 收连接 → 执行 → 回结果 |
| `agents/ControlServer.Agent/Tasks/SessionPipeChannel.cs` | 服务 | `ISessionCommandChannel` 的命名管道实现 + 可用性探测 + 状态翻转日志 |
| `agents/ControlServer.Agent/Tasks/ProgramLauncher.cs` | 托盘 | 在用户会话里 `Process.Start`（`UseShellExecute: true`） |
| `tests/ControlServer.Agent.Tests/`（**新测试工程**） | 测试 | 真管道端到端 23 条（协议 / 分发 / 坏输入 / 可用性与日志 / 启动器） |

改动：`SessionCommandChannel.cs`（接口加 `IsSupported`；`SessionCommandResult.Success` 带可选说明）、
`AgentTaskRunner.cs`（延时启动改为**等会话**）、`UdpCommandListener.cs`（音量分支去掉预检、记录真实状态）、
`.Agent.Service/Program.cs`（注册管道客户端 + 启动自述三态）、`.Agent.Tray/Program.cs`（起管道服务端 + 音频控制器 + 菜单信息行）、
`deploy/windows/install-agent.ps1`（收尾提示 + `-SkipTray` 警告）。

### 18.2 协议形状：**一次连接 = 一条指令**

客户端（服务）连上 → 写一行 → 读一行 → 断开；服务端（托盘）收一行 → 执行 → 回一行 → 断开。

- 🔥 **不搞"长连接 + 请求 Id 配对"**：那要维护"谁在等哪个 Id"、处理半截响应、处理并发写。
  而这里送的是"按一下音量 / 拉起一个程序"这类**一天也没几次**的低频动作，
  本地管道上一次连接的开销远低于一百微秒。用最简单的形状换掉一整类"偶发串包"的疑难杂症。
- 因为只有一条指令，**连请求 Id 都不需要**。
- ⚠️ **行分隔的前提是"JSON 里不出现裸换行"**：紧凑序列化会把字符串里的换行转义成 `\n`（两个字符），
  所以序列化结果必然只有一行。**`WriteIndented = true` 会让一条请求被拆成多行** ⇒
  接收方读到第一行就当成完整请求，剩下几行变成"下一条请求"，表现是"参数莫名其妙丢了半个"。
  🔥 因此管道**绝不复用 `ControlJson.Options`**（它是给配置文件用的，`WriteIndented = true`）。
  这一条被 `Serialized_Request_Stays_On_A_Single_Line_Even_With_Nested_Indented_Json` 钉死
  （启动参数本身就是一段 **多行缩进** JSON，正好是最容易踩中的形态）。
- UTF-8 **不带 BOM**：BOM 会粘在第一行 JSON 前面，解析直接失败，报错信息却像"JSON 格式错误"。
- `StreamReader` / `StreamWriter` 一律 `leaveOpen: true`（同一条流上先读后写，谁都不许把流关掉）。

### 18.3 管道名按**会话编号**区分

`ControlServer.Agent.Session.<sessionId>`。

- 🔥 托盘的"单实例"互斥量是 `Local\` 作用域（**每会话一个**）⇒ 多用户同时登录（快速用户切换 / RDP）时
  会有**多个托盘同时运行**。全局共用一个管道名的话，只有先到的那个能建起管道，
  服务此后固定敲到**先登录那个人的桌面**上 —— 音量改在别人账户里，日志毫无异常。
- 服务侧**每次都要现问**"当前控制台会话是几号"（`WindowsSessionLocator`）：
  会话编号在运行期会变（注销重登 / 换人登录），把它在启动时算死 ⇒
  现象是"注销重登之后音量再也调不动了"，只能重启服务才能恢复。
  所以 `SessionPipeChannel` 收的是 `Func<int?>` 而不是字符串。
- 托盘侧用 `Process.GetCurrentProcess().SessionId`（BCL 自带，无需 P/Invoke）。
- ⚠️ 覆盖范围：本方法取的是**控制台会话**（物理显示器前那个人）。
  纯远程桌面（无人在控制台）时可能取不到活动会话 —— 那种情况下通道会明确报告
  "当前没有交互式控制台会话"，而不是随便挑一个会话乱改。

### 18.4 权限模型：**不设自定义 ACL，用默认 DACL**

`CreateNamedPipe` 在 `lpSecurityAttributes = NULL` 时的默认 DACL 是
「**SYSTEM 完全控制 + 管理员完全控制 + 创建者完全控制**」。本管道的创建者是当前登录用户（托盘），
需要连上来的只有 SYSTEM（服务）—— 三方恰好都在默认 DACL 里。

- 刻意**不引** `System.IO.Pipes.AccessControl` 去写一份自定义描述符：多一个包、多一份要跟着权限模型
  一起维护的东西，而安全性并没有变强。
- 默认 DACL 的一个重要后果：**同机其他普通用户连不上**（既不是创建者也不是 SYSTEM/管理员）——
  这正是要的隔离。
- 管道用全局命名空间 `\\.\pipe\`（不是 `\\.\pipe\locals\`），所以跨会话可达。

### 18.5 音量：CoreAudio，且**只报读回来的真实状态**

- 🔥 为什么不用那两条"看起来更省事"的路：
  `waveOutSetVolume`（winmm）改的是**旧版波形输出**的音量，现代应用走 WASAPI 会话，
  多半**完全不受影响** ⇒ "命令成功、声音没变"；
  发送 `VK_VOLUME_UP` 键盘事件需要目标窗口在前台，而托盘没有前台窗口 ⇒ 时灵时不灵。
  `VolumeStepUp/Down` 是**系统自己那套**步进，与按键盘音量键完全等价。
- 🔥 **COM 接口声明里的 vtable 槽位顺序就是契约**：方法名只对人类有意义，
  调用哪个位置由"它是第几个方法"决定。**少写一个方法 ⇒ 后面所有方法整体前移一格**
  ⇒ 调 `GetMute` 实际调到 `SetMute`，参数个数还对不上 —— 表现是**栈被写坏 / 进程直接崩**，
  而不是编译错误。所以 `IAudioEndpointVolume` 的 18 个方法**全部按序声明**，用不到的用 `IntPtr` 占位。
- 读不到就是 `null`（`HostAudioState` 两个可空值），**不用 `false` 冒充"没静音"**。
- 🔥 **没有音频输出设备的机器**（虚拟机上极常见）`GetDefaultAudioEndpoint` 返回 `E_NOTFOUND(0x80070490)`：
  转成一句人话（"本机没有可用的音频输出设备…"），而不是抛 `COMException` 把指令链打断。
- 每次操作**独立 CoCreateInstance**（不缓存端点对象）：设备会插拔、默认端点会换，
  缓存的"上次那个"很快指向不存在的设备；而音量是低频操作，重建开销不值得优化。
- `Marshal.FinalReleaseComObject`（这几个对象是独占创建独占使用的）—— .NET 8 上**不产生警告**
  （`CA1416` 已在 `Directory.Build.props` 的 `NoWarn` 里）。
- ✅ **2026-10-13 真机实测**（一次性探针，验完即删、系统状态已复位）：
  `IsAvailable = True` / `Note = ''`；`Read()` 读出真实系统状态 `36%（未静音）`；
  `Mute` → 读回 `True` → `Unmute` → 读回 `False`；`VolumeUp` 36→38、`VolumeDown` 38→36
  （与键盘音量键步进一致，且**精确回到原值**）；`Apply((HostAudioAction)999)` 回
  `E_INVALIDARG(0x80070057)` 失败而**不是崩进程**。
  ⇒ **vtable 槽位顺序被真机证实正确**（`GetMute` 真的读到了静音位，没有写坏栈 / 打到 `SetMute`）。
  这件事只有真跑一次能证明 —— 桩永远证明不了。
- 🔒 永久回归守在这两条：`tests/…IntegrationTests/WindowsHostAudioControllerTests.cs`
  三条**只读**用例（可用性与备注严格互反 / `Read` 不抛且数值合理 / 未知动作明确失败）。
  刻意**不**在测试里做写操作：在开发机上把开发机静音，这种"副作用比被测缺陷还烦人"的事不做；
  写路径靠上面那次探针覆盖。

### 18.6 延时启动程序：`UseShellExecute: true` + 显式工作目录

- `UseShellExecute = true` 与"用户在资源管理器里双击它"等价：目标程序拿到的是**用户**的环境变量、
  用户配置目录与交互式桌面，而不是托盘的继承环境；顺带支持 `.bat` / `.lnk` / 文档路径。
- 🔥 **必须显式设 `WorkingDirectory` 为程序所在目录**：不设时 ShellExecute 出来的进程
  工作目录可能是 `C:\Windows\System32` ⇒ "程序找不到自己旁边的配置文件"，
  而现象是"程序起来了但行为不对"，与"启动失败"看起来完全是两码事。
- 失败时把 **Win32 错误码**一起带出来（`系统错误 2` = 路径不存在 / `5` = 拒绝访问）——
  现场靠它一眼分清"路径写错了"与"权限不够"。
- **不做可执行文件白名单**：任务内容由管理员在中心网页配置，执行身份是当前登录用户（**不提权**）。
  能改中心配置的人本来就能对全部客户机为所欲为；在托盘这层再拦一道只会制造
  "网页上配了却不生效"的新谜题。真正的边界在服务端（有登录、RBAC、授权闸门）。

### 18.7 延时启动：**等用户会话**，不是"不可用就跳过"

🔥 这是 #191 里最重要的行为改动。

- 服务在**开机时**就起来了，而托盘要等**用户登录**才起来 ⇒ 服务启动后的头几十秒里
  "会话侧不可用"是**常态而不是故障**。
- 原来（#189）的写法是"不可用就跳过 + 记一条 WARN" ⇒ 等价于
  **"开机自启的程序永远不启动"**，而这条 WARN 谁也不会去看。
- 现在：**先等会话出现，再开始按任务延时计时**（等待不设上限；换配置 / 停机 / 被吊销都会取消它）。
  一直没人登录时就挂着，每 30 分钟提醒一次（把"怎么办"一起说出来：
  若本机没装 / 没启动托盘，这类任务不会执行）。
- 「先等会话、再计时」的顺序是刻意的：这里的延时语义是"相对于本次应用配置"，
  但没有桌面时那个时刻根本无法兑现 —— 等会话就绪再开始计时，才与"现场想看到的效果"一致。

### 18.8 `IsSupported` 与 `IsAvailable` 必须分开

| 属性 | 回答的问题 | 为假时调用方该做什么 |
|---|---|---|
| `IsSupported` | 这东西**有朝一日**能不能用（平台 + 是否接线） | **直接放弃**（等下去毫无意义） |
| `IsAvailable` | **此刻**能不能用（有没有人登录、托盘在不在） | **等它出现** |

只给一个布尔值的话，这两种情形会被迫同样处理，必然错一边。
非 Windows 上注册 `UnavailableSessionCommandChannel`（`IsSupported = false`），
Windows 上注册 `SessionPipeChannel`（`IsSupported = true`）。

⚠️ 同理，`WindowsHostAudioController.IsAvailable` **也不能只判 `OperatingSystem.IsWindows()`**：
没有音频输出设备的机器（虚拟机 / 无声卡工控机 / 声卡被禁用）上"支持音量控制"这句话是**假的**，
报 `true` 会让上层提示从"本机没有可用的音频输出设备"退化成一句空话 ⇒
现在它**真的去取一次端点**，`AvailabilityNote` 返回实际原因，二者**严格互反**（有测试钉死）。
代价是每次询问都真取一次端点 —— 调用点只有"托盘启动时显示一行"和"收到音量指令时"，
频率低到不值得缓存（而缓存端点对象的坏处见 §18.5）。

### 18.9 陷阱清单（#191 专属）

1. 🔥 **Windows 上连一条不存在的命名管道不会立刻报错，而是一直等到超时**。
   所以"连接超时"这个数字直接决定"托盘不在时这条指令多久才回话" ⇒
   必须用一个**独立的短超时**（`ConnectTimeoutMs = 400`，与 `RequestTimeoutMs = 5000` 分开）。
   `SessionPipeChannel` 的 `ConnectAsync` 一开始用的是 5 秒的请求超时，
   单测 `Missing_Tray_Fails_The_Send_Without_Throwing` 跑了 5 秒才失败 —— 实测抓出来的。
2. 🔥 **`ConnectAsync(ct)` 取消时抛的是 `OperationCanceledException`**，与"调用方取消"同一个类型。
   不区分的话，"托盘不在"会伪装成"任务被取消"往上冒 ⇒ 整条任务链静默中断，
   日志里只看到一句没头没尾的取消。判据只能是 `when (cancellationToken.IsCancellationRequested)`。
3. 🔥 **探测连接必须不产生日志**：服务每几十秒探一次；若托盘每次都记一行"连上没说话"，
   托盘日志会被刷爆，真正超时的那条反而被埋掉。约定：**读到 `null`（连上就断）= 探测 ⇒ 静默**；
   **读超时 = 卡住的客户端 ⇒ 记一行**。
   单测 `Availability_Probe_Leaves_No_Log_Noise` 用"紧跟一条真指令"来消除时序依赖
   （单实例管道是先进先出的，真指令能完成就说明探测早已被处理完）。
4. **管道对象建一次、反复用**（连接之间 `Disconnect`）：改成"每处理完就 Dispose 再重建"会在两次之间
   留一段"管道不存在"的空窗，撞上的服务端会拿到一次连接失败 ⇒ "偶发一条指令没反应"。
5. **状态翻转日志只记一次**：`IsAvailable` 会做真实探测**并可能写日志**（仅"可用性翻转 / 会话切换"）。
   这三行是排查"音量/延时启动突然不生效"的**唯一线索** —— 它们能区分
   "用户注销了""托盘被关了""换了个登录用户"，而这三者在别的日志里长得一模一样。
   注意"首次调用"不算变化（启动那一刻的结论由组合根自己打一行自述日志）。
6. **`ISessionCommandChannel` 的调用方不能自己预检 `IsAvailable` 再决定发不发**：
   那会丢掉 `SendAsync` 给的**具体**原因（无人登录 / 托盘不在 / 管道无权连接）。
   音量分支因此在 #191 去掉了预检。
7. **托盘刻意不引 `LogService`**（它是"零 DI"进程，只有一个写 `%TEMP%` 的日志函数）⇒
   `SessionPipeServer` 收的是 `Action<string> log` 委托，而服务侧的 `SessionPipeChannel`
   收的是 `LogService`（它有 NDJSON 落盘）。两侧各用自己有的东西，不必强行统一。
8. **托盘跑在 Session 0 时**（有人拿 SYSTEM 身份直接拉起它）管道名是 `…Session.0`，
   与服务的预期对不上 —— 通道不会工作，但它本来也不该工作（那里没有桌面）。
9. **`.ps1` 的行尾在本仓库是 LF**（13 个脚本里 12 个如此）：`Edit` 工具会保留这个约定，
   别用 Python 手写成 CRLF（#190 那么干过一次，把 `uninstall-agent.ps1` 写成了全 CRLF，属于白添差异）。

### 18.10 验证结果（#191）

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln` | **0 警告 / 0 错误** |
| `dotnet test` | **Agent 23 + Core 707 + 集成 319 = 1049 / 0**（对比 #190 的 1023 ⇒ 无回归，+23 会话侧 + 3 音频真机） |
| 音量真机实测 | 一次性探针全绿并复位（详见 §18.5）；vtable 槽位顺序被真机证实正确 |
| `install-agent.ps1` / `uninstall-agent.ps1` 语法 | 解析 **0 错误**；函数定义在调用点之前 |
| `.ps1` 编码 | UTF-8 **with BOM**、LF（仓库既有约定） |

⚠️ **仍未做**：真机验证"托盘起在会话 1、服务在会话 0 跨会话连上"（需要真实服务安装）；
`Agent.Tray` 实跑（含菜单信息行的实际显示）。
音量落到真实端点**已做**（见 §18.5），无声卡机器上的 `E_NOTFOUND` 分支仍待有该环境的机器复现。

---

## 19. M4.5 前端：客户端详情页「任务配置」区块（2026-10-13，#192）

### 19.1 落地文件

| 文件 | 说明 |
|---|---|
| `web/src/components/AgentTaskConfigPanel.vue`（**新增**） | 版本条 + 四个区块（UDP 指令通道 / 延时·循环发送 / 定时发送 / 延时启动程序）+ 保存条 |
| `web/src/components/AgentDetailPanel.vue`（改） | 挂载点。**传 `detail.host`**（刚读回来的那份）而不是 `props.host`（列表快照，会落后）；`onConfigNotice` 把面板的提示桥到既有的 `saved` 通道 |
| `web/src/types.ts`（改） | M4.5 的 DTO 类型 + 常量 + **四态版本判定**；`AgentHostDto` 补 `configVersion` / `appliedConfigVersion` |
| `web/src/api.ts`（改） | `agentConfig` / `saveAgentConfig` / `redeliverAgentConfig` 三个端点 |
| `web/src/components/AgentDetailPanel.vue`（改） | ⚠️ 顺带修掉"刷新把整块正文卸载"的缺陷，见 **19.4 第 1 条** |
| `.tools/m45-ui-check/{run.sh,ui-check.mjs}`（**新增**） | 无头 Edge + CDP 的外部观察式验证（71 项） |

刻意**新开一个组件**而不是往已经 1100+ 行的 `AgentDetailPanel.vue` 里继续堆。

### 19.2 两条不动摇的约定

- 🔥 **前端不复制校验规则**：允许/拒绝的判据只有一份，在 Core 的 `AgentConfigRules`
  （服务端保存前、客户端应用前共用）。前端把用户填的**原样**送上去，再把服务端返回的问题清单
  **原样**显示。`PUT` 校验不过回 **400**，`message` 是**全部**问题用「；」连接
  —— 别自己再拼一遍（只报第一条正是"改一处漏一处"的开始）。
  已验证：页面显示的那串字与 `PUT` 400 响应**逐字相同**。
- 🔥 **保存后一律用服务端返回的配置重渲染**（不是继续用本地那棵对象）：
  服务端会**静默归一化**（`9:5` / `09：05`（全角冒号）→ `09:05`、去空白、协议名大写、
  时刻去重排序、缺 Id 补一个）。不重渲染的话"我填了两个时刻、列表里只剩一个"永远看不出来。

### 19.3 版本条：**四态**，不能压成"相等 / 不等"

| 状态 | 判据 | 文案 | 样式 |
|---|---|---|---|
| `never` | `appliedConfigVersion === null` | 未回报过 | 中性（**不告警**） |
| `synced` | `=== configVersion` | 已生效 | `ok` |
| `pending` | `< configVersion` | 尚未应用 | `warn` |
| `ahead` | `> configVersion` | 版本异常（不该出现，查是否有人直接改库） | `err` |

- 🔥 **`null` ≠ `0`**：`null` = **从来没回报过**（旧客户端不认识这个字段 / 刚注册还没应用过），
  与"已应用 v0"是两回事 ⇒ 版本条的"已应用"格显示 `未回报`，**不显示 `v0`**，也不比大小。
  （与 `AgentDetailPanel` 里 `fmtTemp` 的"`null` 显示 `—`，绝不显示 `0 ℃`"同一条纪律。）
- 三种"不一致"的解释文案**各不相同** —— 合成一句话就丢掉了"该不该去处理"这个信息。

### 19.4 陷阱清单（#192 专属 / 前端）

1. 🔥🔥 **详情正文的 `loading` 占位不能是"刷新遮罩"**（**本轮最值钱的一条**）。
   `AgentDetailPanel` 原先是：
   ```html
   <div v-if="loading">加载中…</div>
   <template v-else-if="detail"> …整块正文，含 <AgentTaskConfigPanel>… </template>
   ```
   两者在**同一个 v-if 链**上 ⇒ 只要 `loading` 为真，**整块正文被卸载**，转假再重新挂载。
   而 `load()` 恰恰是"刷新"：任务配置自己保存成功后立刻 `emit('refresh-host')`、
   "已下发 ≠ 已应用"期间每 10 秒自动复查一次、台账保存后的重读、以及「刷新」按钮，全都走它。
   实测后果（四条，全为用户可见）：
   - 「保存并下发」的**成功回执只闪 0.25 秒就被抹掉** —— 归一化后的值对不对，运维在页面上看不到；
   - 任务配置的自动复查定时器随组件卸载被 `onUnmounted(stopPendingPoll)` 清掉 ⇒
     **「已应用版本」永远不会自己追上**，页面反而提示"一直不动就点「重新下发」"，
     等于让运维去补一个本该自动完成的动作；
   - 两块表单里**正在填的内容在刷新瞬间被丢掉**；
   - 每次刷新整块正文闪一下"加载中…"。
   **改法**：`v-if="loading && !detail"` —— 占位只在"还没有内容"时出现，刷新时保留旧内容、
   只把新 props 补下去（实例不重建，计时器与未保存的编辑都在）。
   **怎么发现的**：给链路上三层 DOM 各打一个 expando 记号（`__stamp`），保存后看哪个丢了 ——
   结果是 `.detail-cell` 与 `.detail` 都在、只有 `.task-config` 丢了 ⇒ 定位在"正文被换掉"，
   而不是"父级列表重排"。**"整棵子树闪一下就没了"这类问题，先给各层打记号再猜。**
   （这条是**通用前端纪律**：任何"`v-if=loading` 占位 + `v-else-if=数据` 正文"的写法，
   只要刷新会经过 loading，就会把正文里的子组件及其定时器/未保存编辑一起干掉。
   已同步记入 `REF-M2-模块细节.md` §19.4。）
2. **上下移是语义，不是排版**：多个「延时启动程序」的延时是**串行累加**的
   （第一条相对 Agent 服务启动，后面的相对前一条完成）⇒ 列表顺序改变实际行为。
   两张发送清单的排序只影响可读性，但**同一套 UI**，别在文案上撒谎。
3. **两步式删除**（点一次变「确认删除？」）—— 仓库**不用** `window.confirm`（无先例，
   且原生弹窗在"批量调整任务"的节奏里极其打断）。测试断言里必须验到"**第一次点击没有真的删掉**"。
4. **`crypto.randomUUID()` 在非安全上下文是 `undefined`**（局域网 `http://<IP>:8080` 访问）
   ⇒ 任务 Id 用不依赖浏览器能力的拼法生成（同 `REF-M2` §19.4 第 1 条）。
5. **`input { width: 100% }` 会命中复选框** ⇒ 面板里逐项复位
   （`label.inline input[type=checkbox] { width: 14px }`），否则复选框吃掉整行。
6. **按"第 n 个 `.tc-list`"取列表是错的**：空列表渲染的是 `.empty` 而不是 `.tc-list`，
   下标会被后面的区块顶掉（"给第 2 个区块加一条"会加到第 3 个区块上）。
   改成从 `.tc-head` 的兄弟节点往下找、遇到下一个 `.tc-head` 就停。
7. **`AgentConfigSaveRequest` 的 null/空数组语义**：`null` = 不动那一块；**空数组 = 清空那一块**。
   本面板编辑全部四块 ⇒ **四块全发**。

### 19.5 验证结果（#192）

| 项 | 结果 |
|---|---|
| `npm run typecheck`（`vue-tsc --noEmit`） | **通过**（无输出） |
| `npm run build` | 成功 → `wwwroot`（`index-CcY-87-t.js` 359.81 kB / `index-bxtqfKDA.css` 40.27 kB） |
| `dotnet build ControlServer.sln`（`--artifacts-path`） | **0 警告 / 0 错误** |
| `dotnet test`（全量） | **Agent 23 + Core 707 + 集成 322 = 1052 / 0**（对比 #191 的 1049：+3 为新增的 `AgentConfigVersionTests`） |
| **浏览器验证** `.tools/m45-ui-check/run.sh` | **71 通过 / 0 失败**（无头 Edge + CDP，真起一台客户端） |

CDP 脚本覆盖的关键项：区块挂载 / 四个区块齐全 / UDP 默认 8080 未启用 / 版本条 `—` + `未回报` + 中性徽章 /
添加两条并上下移（含两端按钮禁用）/ 两步式删除 / 非法输入列出**全部**问题且与 400 响应逐字一致 /
保存后版本 0 → 1 且按钮回到「没有改动」/ 归一化回显（`9:5, 09：05, 18:00, 9:05` → `09:05, 18:00`）/
**客户机真的拉走、应用、回报 → 版本条变 `v2 已生效`**，且它本地只读缓存也落到 v2。

**顺带抓出并修掉的两个服务端缺陷**见 §16.7 的 **12 / 13**（`ToDto` 漏填 `AppliedConfigVersion`、
用 `ApplyChange` 刷业务字段是空操作）—— 它们都是"不报错、就是一直不对"的类型，
其中一条回归用例**先红后绿**。

### 19.6 跑这个 UI 验证前要知道的四件事（harness）

1. 🔥 **必须显式关掉单实例守卫**：`--Server:Startup:SingleInstance=false`。
   守卫用的是命名互斥体 `Local\ControlServer.Server.SingleInstance`，**在整个用户会话里可见** ——
   开发机上那个正在跑的实例会占住它，于是临时实例启动后"检测到已有实例"、
   把浏览器指向**那个**实例然后退出（**退出码还是 0**，看起来像起成功了）。
2. 🔥 **临时实例必须"每次一个新目录"，不要"固定目录 + 先 `rm -rf`"**：
   批量删除可能被安全策略拦住（本机实测遇到 `[safe-delete][SAFE_DELETE_BULK_CONFIRM_REQUIRED]`
   —— 对 771 个文件被拒），而**脚本不会因此报错** ⇒ 服务端**沿用上一次的数据库**：
   版本号不是 0、多出上一次的任务、"保存于"是上一次的时间。实测这样跑出 **22 个假失败**
   （看着全像功能坏了）。改用 `run/<时间戳>/` 就**从构造上**保证空数据目录。
   配套加一条前置断言：`GET …/config` 必须是 `version 0 + updatedAt null`，不是就立刻中止并说明原因。
3. **还没配过的机器，`GET /api/agents/{id}/config` 返回的是 `200 + version: 0 + updatedAt: null`**
   （**不是 404**）—— "没有配置"是用 0 / null 表达的，不是用 HTTP 状态码。
4. **客户端的只读缓存文件开头有 UTF-8 BOM**：`File.WriteAllText(…, Encoding.UTF8)` 里 .NET 的
   `Encoding.UTF8` **带 BOM**（要 `new UTF8Encoding(false)` 才不带）。Node 直接
   `JSON.parse(readFileSync(p,'utf8'))` 会抛 —— 表现是"文件内容明明对，判定却一直失败"。
   读之前先剥 `^\uFEFF`。（客户端自己读回去没问题：`StreamReader` 会识别并跳过 BOM。）

---

## 20. 现场故障「服务端发现不了装了客户端的电脑」→ 被拒注册的可见性（2026-10-13，#195–#197）

> ⛔ **本节已部分作废（同日晚些时候的 #209–#215）**：准入改为**免凭据**（见 **§23**），
> 所以"**缺凭据 ⇒ 400**""**凭据错 ⇒ 401**""**刻意没有免凭据档**""**准入凭据二选一**"
> 这些结论**全部不再成立** —— 现在不带任何凭据**也能注册成功**。
>
> **保留下来的、仍然有效的是两件事**：
> 1. **"发现成功 ≠ 被接纳"这个方法论** —— 只是现在"不被接纳"的原因**只剩网络与在线表容量**；
> 2. **"被拒的注册必须在中心侧可见"这一整套机制与它的坑**（去重键含 status、按级别过滤、
>    `LogService.FlushAsync()` 的 `WaitAsync(0)` 空转、NDJSON 是 PascalCase + Level 数字、
>    `ConnectionReset 10054` 是 Windows 伪错误 ⇒ 降 Debug）。
>
> 下文**保留为那次现场故障的完整档案**（含"为什么当时会判错方向"）。阅读时请带上上述前提。

> 现场：服务端 `192.168.101.57:8080`，客户机 `192.168.101.177`；网页「客户端」页**始终为空**。

### 20.1 结论先行：**发现是好的，是注册被拒**（⚠️ 当时的结论）

**客户端接入是两段式，现场把两段混为一谈：**

| 阶段 | 协议 | 端口 | 会不会被拒 |
|---|---|---|---|
| ① **发现** | UDP 广播 `CS-DISCOVER` → 服务端回 `CS-HERE`（纯文本、空格分隔） | `9998`（刻意避开对码 9999） | 不会被拒，**永远成功** |
| ② **注册** | `POST /api/agents/register`（**`[AllowAnonymous]`**） | HTTP | ~~缺凭据 ⇒ 400；凭据错 ⇒ 401~~ ⇒ **现为：缺 machineKey ⇒ 400 / 表满 ⇒ 409** |

⇒ **发现成功 ≠ 被接纳。** ~~准入凭据二选一：安装包令牌 `Agent:EnrollmentToken`（可复用）
或一次性加入码 `Agent:JoinCode`（120 分钟、单次）。**两个都空** ⇒
`ResolveCredentialAsync` 返回 `("missing", …)` ⇒ **400**。
**刻意没有"只校验来源网段"的免凭据档** —— 那等于同网段任意机器都能冒充客户机（有集成测试守门）。~~
**（以上整套已删除，见本节顶部说明与 §23。）**

**硬证据链（现场只有这三条，但足够定案）**：

1. 服务端日志里 `[Discovery] 已应答客户端发现请求` 与 `HTTP POST /api/agents/register → 400`
   **每 60 秒成对出现** ⇒ 发现正常、注册被拒。
2. 只读快照 `control.db`：`agent_hosts` = **0 行**、`enrollment_tokens` = **0 行**
   ⇒ 从来没生成过任何凭据（不是"生成过但没用上"）。
3. 重试节奏 **恰好 60 秒**，与 `IterationOutcome.Retry` 的 `MaxBackoff = 60s` **饱和**吻合
   ⇒ 是 **400（缺凭据）**，不是 401（凭据错）。

🔥 **重试节奏是现场反推失败类型的唯一判据**：

| 失败 | 状态码 | 客户端节奏 |
|---|---|---|
| 缺凭据 / 凭据非法 | **400** | 退避 `1s → 2s → … → 60s` **饱和** |
| 凭据被吊销 | **401** | `RevokedRetry` = **固定 5 分钟** |

### 20.2 为什么现场**什么也看不到**（结构性缺陷，本次才修）

`register` 是**匿名**端点，失败原因只写在**客户机自己**的日志里。
中心侧运维能看到的**全部痕迹**只有 Serilog 请求日志的一行
`POST /api/agents/register → 400` —— **不带任何原因**。

⇒ "某台机器一直在敲门但进不来"这件事在中心侧**完全不可见**，
现场唯一的现象就是"装了客户端，页面上一直没有它"，
于是排查一开始就朝"服务端发现不了客户端"走偏 —— **而发现一直是好的**。

### 20.3 三处修复

**(1) #195 服务端记录被拒的注册**（`src/ControlServer.Server/Services/AgentEnrollmentService.cs`）

- `Fail` 由 `static` 改成**实例方法**，在返回失败结果的同时写一条
  `_log.Warn("Enrollment", "拒绝客户端注册（{status}）：{message}｜来源 {ip}｜机器 {machineKey}")`。
- **去重键 = `来源 | 机器码 | 失败状态`**，同一组合**只记首次**，后续降到 `Debug`。
  - 为什么必须去重：客户端每 60 秒重试，一台没配凭据的机器**一天 1440 行**、十台 14400 行。
  - 🔥 **`status` 必须进键**：`missing → unauthorized`（没带凭据 → 凭据不对）
    是运维最需要看到的**一次转变**，把 status 排除在键外就等于把这次转变藏起来。
- 记忆集合有上限 `MaxTrackedRejections = 512`，满了**整表清空**重来：
  键里含**客户端自报**的 machineKey 而端点匿名 ⇒ 容量必须有界，否则一串伪造 machineKey 能顶爆内存。
  代价是"最坏情况多记几行重复日志"，比内存无界划算得多。
- 删掉了 `toomany` 分支里原有的 `_log.Warn`（改由 `Fail` 统一写）——
  同一件事写两行、内容还各有侧重，排障时反而要花时间判断它们是不是两回事。

**(2) #196 三处「装完就自动出现」的文案说准**

| 位置 | 改前 | 改后要点 |
|---|---|---|
| `AgentPresenceHostedService`（启动日志） | 「暂无注册记录（安装客户端后会在 1 分钟内自动出现）」 | 明确"能自动发现，但**首次注册需要凭据**；没带凭据会被拒绝、不会出现在这里" |
| `web/src/components/AgentsPanel.vue`（空态） | 「安装后它会自动出现（通常 1 分钟内）」 | 同上，并**指路**：被拒的注册记在服务端日志的 `Enrollment` 类别下（含来源与原因） |
| `deploy/windows/ControlServer.Agent.iss`（向导副标题 + 标签 + 4 条完成页文案） | 「这两项都可以留空」 | 「两项凭据**至少填一个**，否则永远不会出现」+ 补救步骤 |

**事实**：留空 ⇒ 发现一定成功、注册一定失败，机器**永远不会出现**。改前的文案在实践中是**反的**。

**(3) #197 安装向导支持加入码**（`ControlServer.Agent.iss`）

- 新增 `/JOINCODE=<加入码>` 命令行开关 + `#ifndef EmbeddedJoinCode`（可与 `EmbeddedToken` 并存）。
- `ConnectionPage` 由 2 个输入变 **3 个**：服务端地址 / 注册令牌 / **加入码**。
- 两个凭据**都传给 `install-agent.ps1`，不替用户二选一**（该脚本两项都写；
  客户端判定顺序是"有令牌先用令牌"）。`install-agent.ps1` 本来就支持 `-JoinCode`，只是向导从未暴露。
- 静默安装（`/SILENT`）仍然生效：`ConnectionPage.Values[0..2]` 在**创建页面时**就赋好值，
  而 `ShouldSkipPage` 只是跳过显示。

**(4) 顺带：发现收包环的 UDP「伪错误」从 Error 降到 Debug**（`DiscoveryListenerHostedService.cs`）
—— **这一条超出用户拍板的范围**，是复现脚本当场撞出来的，理由与"让现场能自查"直接相关：

- 现象：阶段 A 每轮发现之后服务端都来一条
  `Error [Discovery] 接收发现广播失败（监听继续） | SocketException: 远程主机强迫关闭了一个现有的连接。`
- 机制（**Windows 上 UDP 的伪错误，不是故障**）：服务端把 CS-HERE 回给了客户端**那一轮的临时端口**，
  而客户端**每轮发现都新开一个 socket**（退出即关闭）⇒ 对端主机回的 ICMP「端口不可达」被记在这个
  UDP socket 上，并在**下一次** `ReceiveAsync` 时以 `SocketException(10054 / ConnectionReset)` 抛出。
  自愈：监听循环继续，下一包照收（原代码的 `continue` 是对的）。
- **为什么必须降级**：它**每台机器每次重启 / 每轮发现都会来一条**。挂在 Error 级上，
  百来台机器就足以把真正要看的日志（`Enrollment` 类别的"拒绝客户端注册"）淹掉 ——
  **那正是本次修复要解决的问题本身**。
- 只认 `ConnectionReset`；**刻意不把 `NetworkReset`(10052) 算进来**（它也可能意味着网卡/链路真的断了，
  那种情况应当留下 Error）。宁可多留一条噪音，也不要把真实故障藏起来。

### 20.4 本轮新增的陷阱

**(a) 🔥 本机代理会吞掉注册请求（脚本侧的坑，差点被当成产品缺陷）。**
本机 Git Bash 配了 HTTP 代理，而 .NET `HttpClient` **默认读 `HTTP_PROXY`/`http_proxy`**
（`HttpClient.DefaultProxy`）⇒ 客户端的 POST 被送到代理，**服务端一条注册请求都收不到**，
客户端报 `'u' is an invalid start of a value. Path: $ | LineNumber: 0`（那是代理回的纯文本）。
**广播走 UDP 不经代理 ⇒ 发现照常成功**，所以症状是"发现好了、注册没反应"。
→ 起客户端必须 `env -u HTTP_PROXY -u HTTPS_PROXY -u ALL_PROXY -u NO_PROXY -u http_proxy …`。
⚠️ 现实世界同样成立：**客户机上装了全局代理软件会出现一模一样的效果**。

**(b) `LogService` 的 NDJSON 属性名是 PascalCase，且非 ASCII 被转义成 `\uXXXX`。**
`JsonSerializer.Serialize(entry)` 用**默认选项**（无 naming policy）⇒
`{"Timestamp":…,"Level":2,"Category":"Enrollment","Message":"\u62D2…","LevelText":"Warning"}`。
→ 断言必须**解析 JSON**（`Category`/`Message`），**绝不能 `grep 中文`**（文件里没有那几个字节）。
→ `Level` 是**数字**（`LogLevelKind`：Debug 0 / Info 1 / Warning 2 / Error 3）。
→ 端点回显的 `LogDto.Level` 却是**字符串**（`"Warning"`）—— 三处形态各不相同，别串了。

**(c) ⚠️ `LogService.FlushAsync()` 可能**什么都不做**。**
抢锁用的是 `WaitAsync(0)`：后台刷盘定时器（500 ms）恰好握着锁时它**直接返回**。
⇒ 测试里不能"调一次 FlushAsync 就去读文件"，必须**轮询**（见 `WaitForPersistedLogAsync`）。

**(d) 测试里 `HttpContext.Connection.RemoteIpAddress` 在 `TestServer` 下不带真 socket**，
所以"来源"列的断言**不能写死 IP**，只能按机器码 + 状态筛。

**(e) 🔥 按文案数日志必须**同时按级别过滤**。**
去重把重复项降成了 `Debug`，而 `Debug` 行的**文案与被降级的那条一模一样**
（只少了末尾那句"同一来源的相同原因只记录一次"）⇒ 只按文案筛会把它们一起数进来，
于是"4 次被拒"看起来像"记了 4 行"，**去重是否生效正好判反**。
本脚本第一版就踩了，输出是 `[--] 去重无从体现（不是失败）` —— 明明生效了。

**(f) ⚠️ `dotnet test` 全量耗时 ~13 分钟**（集成 325 条里有多条要等超时/退避）。
不要在前台跑；`--artifacts-path .artifacts-check` 是必需的（`Server/bin` 常被在跑的实例锁住）。

### 20.5 复现 / 回归资产

| 资产 | 作用 |
|---|---|
| `.tools/discovery-diag/run.sh` | 真起服务端 + 真起客户端，**只改"有没有凭据"一个变量**做 A/B：阶段 A（无凭据）/ 阶段 B（合法加入码）；两侧日志并排打印 + 在线表；末尾给 `[OK]/[FAIL]` 验收（被拒原因可见 + 重试去重生效） |
| `.tools/cfg-source/` | 配置读取路径调查（`matrix.sh` 受控矩阵 + `IlProbe`/`CfgProbe` 两个探针）—— 结论见 §6 第 6 条 |
| `AgentsApiTests.Rejected_registration_is_visible_in_the_server_log_once_per_cause` | 被拒注册必须留下 `Enrollment` 类别、`Warning` 级别、含来源与机器码的一条 |
| `AgentsApiTests.Repeated_rejections_are_deduplicated_but_a_changed_cause_is_not` | 同因去重；换机器不串；`missing → unauthorized` **必须各记一条** |
| `AgentsApiTests.Rejection_warning_reaches_the_persisted_ndjson_log` | 真的落盘到 `logs/log-*.ndjson`（现场运维 tail 的就是它） |

### 20.6 现场补救三步（**不改代码**）

1. 服务端网页「客户端」页 → **生成加入码**（120 分钟、一次性）或**生成安装包令牌**（长期、可批量）。
2. 在客户机把凭据写进 `C:\Program Files\ControlServer Agent\appsettings.json` 的
   `Agent:JoinCode`（或 `Agent:EnrollmentToken`）——
   ⚠️ 该文件 ACL 已收紧到 SYSTEM + Administrators（见 §6 第 9 条），**必须用管理员权限**，
   所以给用户的指引**不能**写"手工编辑 appsettings.json"，
   要给"**以管理员重跑 `install-agent.ps1 -JoinCode <加入码>`**"这种真能走通的路。
3. 重启服务：`Restart-Service ControlServerAgent`；之后服务端日志应出现
   `客户端注册成功：…（machineKey，来自 192.168.101.177）`。

### 20.7 验收（2026-10-13）

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln`（`--artifacts-path`） | **0 警告 / 0 错误** |
| `dotnet test`（全量，~13 分钟） | **Core 707 + Agent 23 + 集成 325 = 1055 / 0**（对比 #192 的 1052：**+3** 为本轮新增的三条） |
| `npm run typecheck`（`vue-tsc --noEmit`） | **通过** |
| `npm run build` → `Server/wwwroot` | `index-mmx5kqWn.js` 385.6 kB / `index-1amjB9ZE.css` 40.3 kB，`index.html` 引用**已同步** |
| `.tools/discovery-diag/run.sh` | **3 项全 `[OK]`**（见下） |
| `ControlServer.Agent.iss` 编译（ISCC） | 通过（`/JOINCODE=` + 第三个输入框） |

`.tools/discovery-diag/run.sh` 实测（阶段 A 窗口 8 s）：

```
小结：服务端收到 4 次注册请求，其中 4 次是 400。
服务端记下的「被拒注册」：Warning 1 行 / 含 Debug 共 4 行      → [OK] 服务端给出了被拒原因
服务端 Error 级的「接收发现广播失败」：0 行                     → [OK] UDP 复位已降级
验收点：[OK] 可查  [OK] UDP 复位不再刷 Error  [OK] 重试去重生效：4 次被拒只记了 1 行 Warning
阶段 B（合法加入码）：客户端注册成功 → 在线表 1 台 online/在线
```

阶段 A 服务端侧实际落盘的那一行（**这就是现场要照着抄的判据**）：
```
Warning [Enrollment] 拒绝客户端注册（missing）：缺少注册凭据：请使用随安装包下发的令牌，
或由管理员在网页上生成一个加入码。｜来源 192.168.101.57｜机器 084C-98CF-BE5A-9AEC
｜同一来源的相同原因只记录一次，重试不再重复。
```

### 20.8 顺延 / 未验

- **客户机上有代理软件时注册会不会被吞**：机制在本机已复现成立，但没有**真机对照**。
- `AllowedCidrs` 仍无消费者（挂账，见 MEMORY §3）。


## 21. M4.5 测试与冒烟（#193，2026-10-13）

> 这一节回答"**M4.5 那几条链路凭什么说是通的**"。
> 它同时是**踩坑记录**：本轮的大部分时间花在"让测试别撒谎"上，
> 而不是"让测试变绿"——三次假红全都来自**观测手段**而不是被测代码。

### 21.1 落地文件

| 文件 | 覆盖 |
|---|---|
| `tests/ControlServer.Core.Tests/AgentConfigRulesTests.cs` | `Merge` / `Normalize` / `Validate` 纯函数（配置的合并·清洗·校验） |
| `tests/ControlServer.Core.Tests/UdpCommandSignatureTests.cs` | HKDF 派生·导入导出 / 验签 / 时间窗与**先验签后看时间** / 报文格式容错 / 双语指令映射 / `UdpReplayGuard` / `IsSourceAllowed` |
| `tests/ControlServer.Agent.Tests/UdpCommandListenerTests.cs` | **真 UDP socket** 跑"四关"+ 演练模式 + 换端口 + 幂等（电源/音量全是桩，**绝不真关机**）；⚠️ 端口一律用 `Harness` 换过的**空闲端口**（§40），**不许绑 8090** |
| `tests/ControlServer.Server.IntegrationTests/AgentConfigApiTests.cs` | `GET/PUT config` 往返·版本·推送 / 校验失败**不留半个效果** / **双身份归属校验** / 三角色边界 / `redeliver` 离线 409 |
| `deploy/smoke/agent-smoke.py`（改） | D 段新增 **④.5 UDP 指令通道**：配置下发 → 客户机真绑端口 → 白名单拦 / 签名拦（见 21.5） |

### 21.2 Core：把"容易写反的语义"钉死

- 🔥 **`Merge`：`null` = 不动那一块；空数组 = 清空那一块。** 一字之差、语义相反。
  两条用例（`Null_blocks_keep_the_existing_content` / `Empty_arrays_clear_the_existing_content`）
  是同一个硬币的两面，**必须成对**——只写一条的话，把两者实现成同一个行为也能过。
- `Normalize` 只做书写统一（协议大写 / 补 Id / `9:5`→`09:05` / `UTF-8`→`String` / 白名单 trim+去重），
  **不猜用户想干什么**；`AllowedSourceIps` **永不为 null**（"没配"与"配了空"在执行期同义）。
- 🔥 `Validate` **一次把所有问题都报出来**（`Multiple_problems_are_reported_in_one_pass`）：
  一次改十条任务时只报第一条＝逼用户"改一轮、试一轮"。
- 🔥 `Validate` 里两条**"不该拦"**的边界同样有用例：**停用的 UDP 通道完全不校验**
  （否则"先关掉再改端口"这个正常动作会失败）、**停用中的空定时任务不算问题**
  （"先加一条、停用着慢慢填"）。
- 指令库数据类型 `指令库` 在**保存时**就要被拒（客户机拿不到定义，发不出字节）。
- 🔥 **先验签、后看时间**（`Signature_is_checked_before_the_timestamp`）：同时篡改签名
  并把时间戳推到窗外，断言报的是**签名**错。顺序反了会给攻击者一个免费的探测器。
- 🔥 **IPv4-mapped IPv6 归一化**（`::ffff:192.168.1.5` ↔ `192.168.1.5`）：不归一化时现场
  表现为"明明配了 192.168.1.5 却被拒"，而日志上看起来根本是两个地址。
- `UdpReplayGuard`：同 nonce 只生效一次、**有界**（容量挤出后允许再现，由时间窗兜底）、空 nonce 拒收。

### 21.3 Agent：为什么用**真 socket**而不是 mock 掉 `UdpClient`

四关的判定全在纯函数里（21.2 已覆盖），所以这里**刻意不重复判定逻辑**，
而是验"**接线**"：配置怎么变成一次绑定、报文怎么走到桩上。
因此 `UdpCommandListenerTests` 真开一个 UDP socket 收真报文：

- **绝不真关机**：`IHostPowerController` / `ISessionCommandChannel` 都是桩。
  桩不只是"避免副作用"，**它的调用计数就是"这一关到底放行了没有"的唯一事实**。
- 🔥 `Tasks.Enabled=false` 的断言是"**根本没绑端口**"（`IsRunning == false`），
  不是"绑了但丢弃"——后者在 `netstat` 里仍是一个开着的端口。
- 🔥 **`Apply(null, key)`（还没注册）→ 之后补一次 `Apply(config, key)` 必须能起来**：
  通道密钥由 agentKey 派生，而配置在"还没注册"时就已经从本地缓存载入了（降级运行）。
  少了这条重试路径，现场就是"配置启用了 UDP、端口却一直没开"。
- 白名单用例用**两段式**：先把名单设成无关地址 → 报文被拦（`不在白名单内`）；
  再改成 `127.0.0.1` → 同一条报文立刻执行。
  **后半段是前提**：它证明"上一条没执行"确实是白名单拦的，而不是 socket 根本没收到。
- 同一配置重复 `Apply` **不重绑**（心跳/版本对齐会反复调它，否则每次心跳都抖一下监听）。
- 丢包日志**按时间节流**（50 条灌包 ≤ 2 行）。

### 21.4 集成：双身份与"被拒不留半个效果"

- 🔥 **归属校验只能在控制器做**（策略 `AgentConfigRead` 只判"是不是一台合法机器"）：
  用 A 机的 `X-Agent-Key` 读 B 机的配置必须 **403**（不是 404——它确实是台合法机器）。
  这条是**安全边界**：任务清单里有目标 IP 与发送内容。
- 🔥 **校验失败不能动版本号**：400 之后重新 `GET`，版本仍是 1、内容仍是原来那份。
  版本 +1 而内容没存 ⇒ 客户端白拉一次，还会把自己手上的好配置当成旧的。
- `UpdatedAt` 未配置时必须是 **null**（拿"现在"凑会让人以为刚配过）。
- 保存成功推一条 `AgentConfigChanged`（**只带版本号**，正文走 HTTP 拉）。
  记录型 `INotifier` 用 **`ReplaceServices`（`ConfigureTestServices`）** 换上——
  在 `ConfigureWebHost` 里直接 `AddSingleton` 会被应用自身的注册盖掉。
- `redeliver` 在没有长连接时 **409**（200 会让运维以为"推过去了、机器没反应说明它坏了"）。
- 三角色边界：**Operator 能读不能写**；**Viewer 连读都不行**。

### 21.5 冒烟：UDP 通道那一段**只能**验什么

🔥 **冒烟脚本拿不到通道密钥**：密钥由该机 agentKey 经 HKDF 派生，
而 agentKey **只在客户机本地**（DPAPI 保护落盘，服务端只留哈希）。
这是**有意的安全属性，不是冒烟的缺陷**。所以 `agent-smoke.py` 只验"门是活的"：

1. 配置下发后客户机**真的在监听**那个端口（`netstat` + **PID 比对**）；
2. 来源不在白名单 → 丢弃；
3. 来源在名单内、但**签名不对** → 丢弃。

**正向**路径（正确签名才执行 / 演练模式不执行 / 重放 / 换端口 / 关掉不绑端口）
由 21.3 的真 socket 单测覆盖 —— 那里才知道密钥。

客户端启动参数新增 **`--Agent:Tasks:ExecuteUdpCommands=false`**（演练模式）：
它是"冒烟绝不关掉跑它的这台机器"的唯一保险。

### 21.6 陷阱清单（#193 专属，全部是"观测手段"而不是被测代码）

1. 🔥🔥 **客户机的日志要趁它`还活着`读**。冒烟是用 `proc.terminate()` 收尾的，
   在 Windows 上就是 `TerminateProcess` —— **没有优雅停机**，日志 sink 的尾巴会留在缓冲区里
   再也落不了盘。第一版"发完报文 → 杀进程 → 再读日志"，结果第二条报文对应的那行
   根本没写进文件 ⇒ 断言红，**看起来像"第 3 关没生效"，其实只是读得太晚**。
   正解：`client_messages(state_dir)` 在动作发生时**轮询**读。
2. 🔥🔥 **不要用"睡几秒"代替"可观测的生效证据"**。改白名单在**外部看不出来**，
   睡多久都是猜；猜短了第二条报文仍被白名单拦下，于是**报错指向错误的关卡**。
   正解：**换一个端口**——`netstat` 能看到新端口绑上了，就等于"这份配置（含新白名单）确实生效"。
3. 🔥 **丢弃日志按时间节流（同类 5 秒一条）**：两次"应当被丢弃"的发送必须**隔开 > 5 秒**，
   否则第二条被压掉，断言红得莫名其妙。
4. 🔥 **诊断文本的过滤词要精确**：用 `"签名"` 去挑失败样例，会命中启动那一行
   「**无签名**的报文一律丢弃」，于是打印出来的是一句毫不相干的日志（本轮踩过，两轮才看出来）。
5. 🔥 **判断"某端口被某进程监听"必须连 PID 一起比**：只比端口号会假阳性
   （本机别的程序占着同一个端口时照样"通过"）。
6. 🔥 **C# 测试里写"说明"文案不能出现半角引号**：`Should().BeTrue("……「空口令密钥」……")`
   里若写成 `"……"空口令密钥"……"` 会**截断字符串字面量**（本轮一次性踩到 5 处）。
   中文引号一律用 `「」`。
7. FluentAssertions `Equal` 的 params 陷阱继续适用：**带说明就写 `Equal(new[] { … }, "说明")`**。
8. 集成测试里"机器身份"要**手工加请求头**：`HttpRequestMessage.Headers` **没有索引器**
   ⇒ 用 `TryAddWithoutValidation(AgentAuthentication.HeaderName, key)`。
9. 测试里换 `INotifier` 这类服务必须走 **`ConfigureTestServices`**（工厂里的 `ReplaceServices`）。
10. 前端相关：本轮**没动前端**，所以 `npm run typecheck / build` 不需要重跑
    （上一轮 #195–#197 已跑过并核对过产物哈希）。

### 21.7 验收（2026-10-13）

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln`（`--artifacts-path`） | **0 警告 / 0 错误** |
| `dotnet test`（全量） | **Core 804 + Agent 39 + 集成 339 = 1182 / 0**（对比 1055：**+127**） |
| → 增量构成 | Core **+97**（配置规则 + UDP 签名）、Agent **+16**（四关真 socket）、集成 **+14**（config 端点） |
| `python deploy/smoke/run-all.py <dev exe> --only agent` | **通过 110 / 失败 0**（新 UDP 段 5 条全绿） |

冒烟新增断言的实际输出：
```
[PASS] 配置下发后客户机真的绑定了 UDP 指令端口（≤30s） —— 端口 56284
[PASS] 客户端日志里写明 UDP 指令通道已启动
[PASS] 来源不在白名单的报文被丢弃（第 2 关） —— 丢弃来自 127.0.0.1 的报文：来源 IP 不在白名单内
[PASS] 改端口 / 白名单后客户机按新配置重新绑定（≤30s） —— 端口 56285
[PASS] 签名不对的报文被丢弃（第 3 关） —— 丢弃来自 127.0.0.1 的报文：签名不匹配。
```

### 21.8 本轮**没做**（顺延到 #194 或更后）

- **正确签名 → 真执行**的**真机/真进程**端到端（只能在单测里做，理由见 21.5）；
  只有"跑一次真客户端 + 真关机"才能端到端验的，属于 **#194 真机验证**。
- 三个新冒烟断言依赖 `netstat`（Windows）。Linux 侧（`ss -lunp`）未适配 ——
  Linux 交付本就未做，一并顺延。


## 22. M4.5 打包与真机验证（#194，2026-10-13）

> 这一节回答"**交付出去的那个 exe，凭什么说和你刚编出来的一样**"。
> 结论先说：**五组产物全部重打并逐份核验**；**服务侧真机验完**；
> **会话侧（音量 / 静音 / 启动程序）如实记为验证缺口** —— 它需要一次管理员安装 + 交互会话，
> 本轮按用户选择**不做**（见 22.4）。

### 22.1 交付产物（2026-10-13 重打，全部来自新构建）

| 产物 | 路径 | 大小 | SHA256（前 8） |
|---|---|---|---|
| 绿色版 · 严格 1 文件 | `dist/win-x64/ControlServer.Server.exe` | 53,045,375 B | `566c143c` |
| 服务版 · 2 文件 | `dist/win-x64-service/`（Server 与绿色版**逐字节相同** + TrayAgent） | 同上 / 37,027,522 B | `566c143c` / `108aee5e` |
| 客户端 · 2 exe | `dist/agent/ControlServer.Agent.{Service,Tray}.exe` | 38,033,517 / 37,563,894 B | `ea46e3f1` / `1ce760a0` |
| 服务端安装包 | `dist/installer/ControlServer-Setup-1.0.0.exe` | 78,250,058 B（74.6 MB） | `2ac70620` |
| 客户端安装包 | `dist/agent-setup/ControlServer-Agent-Setup-0.1.0.exe` | 63,831,291 B（60.9 MB） | `296a74a8` |

- 服务版里的 `ControlServer.Server.exe` 与绿色版是**同一份拷贝**（`build-portable-exe.ps1` 直接 `Copy-Item`），
  因此两者 SHA256 必然相同 —— 这是"不会出现两个版本"的机器可检证据。
- 客户端安装包编译完会**自动投放**到 `C:\ProgramData\ControlServer\downloads`（先删旧包），
  网页右上角「客户端安装包」按钮拿到的就是刚打的那一份。
- 本机 Inno 工具链在 `.tools/innosetup/tools/ISCC.exe`（含 `ChineseSimplified.isl`），**不需要联网**。

### 22.2 打包顺序**不能颠倒**：前端 → 再发布

前端产物是**编译期**嵌进程序集的（`ControlServer.Server.csproj` 的 EmbeddedResource），所以顺序只能是：

```
cd web && npm run typecheck && npm run build          # → src/ControlServer.Server/wwwroot
... build-portable-exe.ps1 -SkipWebBuild              # 绿版 + 服务版
... build-agent.ps1                                   # → dist/agent（严格 2 文件）
... build-installer.ps1                               # 服务端 Setup
... build-agent-installer.ps1                         # 客户端 Setup
```

⚠️ `-SkipWebBuild` **只在"刚刚构建过前端"时安全**。反过来（先发布、后构建前端）打进去的是**上一版**页面，
而 `index.html` 里引用的资源名（带哈希）还是旧的 —— **服务能起来、页面是旧的**，
从"能不能访问"这条路径上完全看不出来。

### 22.3 真机验证：验了什么、**没**验什么

**验了（服务侧，用交付的绿色版 exe 起的隔离实例）**：

| 项 | 结果 |
|---|---|
| 首页引用的资源名 | `index-mmx5kqWn.js` / `index-1amjB9ZE.css` —— **正是本轮 vite 输出的那两个哈希** |
| 服务端返回的 JS/CSS 字节 | 与 `src/ControlServer.Server/wwwroot/assets/` **逐字节一致**（`f3e9f631` / `ca8634ad`） |
| 静态资源 / 登录 | 资源 200；`POST /api/auth/login`（admin/admin123）→ 200 |
| M4.5 路由存在性 | `/api/agents` 匿名 401 / 登录 200；`/api/agents/{id}/config` 匿名 401 / 未知 agent 404；`/api/agent-package` 200 |
| 全量冒烟（**打的就是这一份 exe**） | **7/7 场景**（见 22.5） |

🔥 **"首页引用的哈希 == 刚构建出来的哈希"是"前端真的被重新嵌进去了"的证据。**
它比"页面能打开"强得多：页面能打开，用**旧前端**也一样能打开。

**没验（会话侧）**：音量 / 静音 / 启动程序的**真机**执行。它同时要求三件事：

1. 以**管理员**跑 `install-agent.ps1`（注册 Windows 服务 + 按程序放行 UDP 入站规则 + 写 HKCU 启动项）；
2. 有一个**交互会话**（服务在 Session 0，那里没有音频端点）；
3. 再经由 UDP 通道 / 会话侧管道下发一次真实指令，并**旁观音量真的变了**。

**手动核对步骤（现场照做）：**

```powershell
# 0) 中心侧先起来（绿色版双击即可）
# 1) 客户机（管理员）：装客户端并指定中心地址
powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\install-agent.ps1 -Server http://<中心IP>:8080
# 2) 中心网页「客户端」页确认它上线 → 进该机的「任务配置」
# 3) 打开 UDP 指令通道（端口默认 8080；来源白名单填中心 IP），保存
# 4) 客户机核对端口真的绑上了（netstat -ano | findstr :8080），且日志有「UDP 指令通道已启动」
# 5) 中心发一次音量指令，旁观客户机音量变化
# 6) 反向验一条：把白名单改成无关 IP，同一条指令应当**无声无息地被丢弃**
#    （客户机日志：丢弃来自 <IP> 的报文：来源 IP 不在白名单内）
```

⚠️ 第 6 步是**唯一**能证明"拦的是白名单而不是别的关卡"的做法（同 §21.6 第 2 条）。

### 22.4 本轮**有意不做**（用户 2026-10-13 选择）

- **不跑安装包**（`ControlServer-Setup-1.0.0.exe` / `ControlServer-Agent-Setup-0.1.0.exe`）：
  它们会注册 Windows 服务、改防火墙、写启动项 —— 属于**改动本机**的动作。
  产物本身的正确性由"ISCC 编译成功 + 文件数/体积/SHA 核对 + 压缩清单里两个 exe 都在"覆盖。
- **不做真关机**（正确签名 → 真执行）：单测（§21.3）用的是**桩电源**；真关一次机只能现场做，
  而 `ExecuteUdpCommands=false` 演练模式是"冒烟不关机"的那道保险。
- **不装客户端 Agent** ⇒ 会话侧就是 22.3 的缺口 + 手动步骤。
- **不清理** `%TEMP%` 下的历史 `cs-smoke-*.log`（只在场景失败时保留，是过去的失败痕迹）。

### 22.5 验收（2026-10-13）

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln`（`--artifacts-path`） | **0 警告 / 0 错误** |
| `npm run typecheck` | **通过**（`vue-tsc --noEmit`） |
| `npm run build` | 通过（唯一告警是 `@microsoft/signalr` 里 `/*#__PURE__*/` 位置提示，无害） |
| `dotnet test`（全量） | **Core 804 + Agent 39 + 集成 339 = 1182 / 0** |
| `python deploy/smoke/run-all.py`（**打 `dist/win-x64` 的打包 exe**） | **7/7 场景**：web 36 / pairing 4 / admin 78 / **agent 110** / transfer 68 / license 45 / rbac 94 |
| M4.5 UDP 五条断言 | **全绿**（绑端口 / started 日志 / 白名单拦 / 换端口重绑 / 签名拦） |
| 打包复跑 | 五组产物全部重生成，文件数与体积符合约定（1 / 2 / 2 / 1 / 1） |

### 22.6 陷阱（打包与真机验证专属）

1. 🔥🔥 **Bash 里 `exe ... &` 起的进程，会随这个 shell 一起被收走。**
   表现极其像"exe 起不来"：**日志文件 0 字节**、`curl` 退出码 7、端口没在听。
   同一份 exe 换个跑法就正常 ⇒ **是跑法的问题，不是产物的问题**。
   正解：用后台任务机制起（`run_in_background`），不要裸 `&`。
2. 🔥 **同机起第二个实例必须带 `--Server:Startup:SingleInstance=false`**，否则互斥体
   （`Local\ControlServer.Server.SingleInstance`）被第一个实例占着，第二个**直接退出**——
   日志里只有开头几行，看着像"启动就崩"。冒烟脚本一直带着它，所以从来不撞；手敲命令最容易忘。
3. 🔥 **Git Bash 会把"像路径的实参"做 MSYS→Windows 转换**（`/c/Users/x` → `C:\Users\x`），
   **但不会转换**写在脚本/配置里的字符串。所以"手敲能跑、写进脚本就找不到文件"是这类问题的典型长相；
   想稳就当它**不转换**，一律写 `C:/Users/...`。
4. ⚠️ **`sc.exe` 在本机被沙箱程序黑名单挡下**（`PROGRAM BLOCKED BY SECURITY POLICY`）。
   查服务用 `Get-Service`，查端口占用用 `Get-NetTCPConnection`，结束进程用 `Stop-Process`。
   安装/卸载脚本自身用的是 `New-Service` / `Remove-Service` 这类 cmdlet，不受影响。
5. ⚠️ **构建脚本被当子进程调起时输出可能不回显**。判断成败看 **Tee 出来的日志**
   （`[OK] …` / `[!!] …`）与**产物的时间戳/体积**，别因为"屏幕上什么都没有"就当成失败。
6. ⚠️ **`run-all.py` 只在场景失败时保留启动日志**（成功即删），且日志名带着**它自己的 PID**
   （`cs-smoke-<场景>-<pid>.log`）。`%TEMP%` 里剩下的 `cs-smoke-*.log` 是**历史失败**的痕迹，不是本轮。
7. ⚠️ 本机可能**长期挂着**上一次遗留的 `ControlServer.Server.exe`（占着 8080）。
   起新实例前先 `tasklist | grep ControlServer` 看一眼，否则会得到"端口被占 / 单实例互斥"
   这类**与产物无关**的假故障。

### 22.7 至此 M4.5 收口

M4.5 全部子项（服务端四层 / 配置中心化下发 / UDP 加固通道 / 会话侧管道 / 前端任务配置区块 /
准入可见性三修 / 测试与冒烟 / 打包与真机验证）**全部完成**。M4 之后是 **M5 远程桌面**。

---

## 23. 免凭据准入：整体拆除「准入令牌 / 加入码」（2026-10-13，#209–#215）

### 23.1 一句话

产品口径：**「取消客户端准入令牌的设定，只要安装了客户端，局域网内都能自动发现，不设任何门槛」**。
⇒ **注册不再校验任何凭据**；`enrollmentToken` / 加入码这套东西**行为 + 界面全拆、不留开关**。

### 23.2 门槛到底在哪一步（改动前先想清楚了这个）

准入链是四步，**只有第 ③ 步有门槛**：

```
① 安装客户端
② UDP 发现（9998: CS-DISCOVER → CS-HERE）—— 一直没有凭据
③ 注册 POST /api/agents/register      ← 唯一门槛（原需 enrollmentToken / joinCode）
④ 换 agentKey，建立长连接上线
```

⚠️ 这条前提很重要：**「发现」本来就是无门槛的**，所以本次改动**只动注册**，
发现链、心跳链、SignalR 链**一行没改**。

### 23.3 被显式推翻的旧结论（原注释里写得很硬）

原设计在 `AgentEnrollmentService` 与 `AgentRegisterRequest` 的注释里写着：

> **绝不提供"只校验来源网段"的免凭据模式** —— 那等于同网段任意机器都能冒充一台客户机。

这条结论**已被本次产品决策推翻**，且做得比"只校验来源网段"更彻底：**连来源网段都不看**。
留在代码里的不再是那句硬话，而是**新的代价说明**（见 §23.5）。

### 23.4 拆了什么（清单，便于回归核对）

| 层 | 删除内容 |
|---|---|
| **服务端行为** | `AgentEnrollmentService`：`IEnrollmentTokenStore` 依赖、`ListAsync` / `CreateAsync` / `RevokeAsync` / `ToDto` / `ResolveCredentialAsync` **全部删除**；`RegisterAsync` 变成"容量闸门 → 生成 agentKey → Create/Update" |
| **服务端端点** | `GET /api/agents/enrollment`、`POST /api/agents/enrollment`、`POST /api/agents/enrollment/{id}/revoke` —— **三个端点整体删除** |
| **授权闸门** | `LicenseGate` 的 Allowed 名单里相应三行删除；**`POST api/agents/register` 保留在 Allowed** |
| **契约** | `AgentRegisterRequest` 由 `(HostInfo, EnrollmentToken, JoinCode)` → **`(HostInfo)`**；`AgentEnrollmentTokenDto` / `AgentEnrollmentDto` / `CreateEnrollmentRequest` **删除** |
| **Core** | `IEnrollmentTokenStore` 接口、`EnrollmentTokenRecord` 模型、`AgentKeys.NewJoinCode()` / `NormalizeJoinCode()` **删除**（保留 `SecretBytes=32` / `NewSecret()` / `Hash()`） |
| **Infrastructure** | `EfEnrollmentTokenStore.cs` **删除** |
| **客户端** | `AgentOptions.EnrollmentToken` / `JoinCode` **删除**；`AgentRuntime` 改为 `new AgentRegisterRequest(info)`，重试分支由 `unauthorized` 改判 **`toomany`**；`.Service/Program.cs` 的"未配置凭据"警告块删除 |
| **前端** | `AgentsPanel.vue` 的整个「准入令牌（仅管理员）」区块、令牌相关状态与三个 API 函数、`types.ts` / `api.ts` 里的三个类型与函数 **全部删除** |
| **安装** | `install-agent.ps1` 的 `-Token` / `-JoinCode`；`ControlServer.Agent.iss` 的 `EmbeddedToken` / `EmbeddedJoinCode` 与向导里的令牌 / 加入码输入框（**保留服务端地址输入**）；`build-agent-installer.ps1` 的 `-Token` 与 `/DEmbeddedToken` |
| **配置** | `ServerOptions.Agent.JoinCodeLifetimeMinutes` **删除**（已无消费者）；`MaxHosts` 注释改写为"免凭据后唯一兜底" |

### 23.5 🔥 代价与兜底（必须向部署方讲清）

> **放开之后，同网段任何能访问到 `POST /api/agents/register` 的机器都能注册成一台客户机。**
> 如果 8080 被端口映射 / 公网可达，**等于对不可信网络开放注册**。

剩下的兜底**只有两条**，且都不是准入门槛：

1. **在线表容量上限** `Server:Agent:MaxHosts`（默认 500）—— 到上限后新机器注册被拒（**409 / `toomany`**）。
   它**只防撑爆**。已在表里的机器重新注册**不受上限影响**（否则到达上限后那些机器重装就再也回不来）。
2. **管理员在网页上手工吊销 / 移除** —— **事后**处置，不是事前拦截。

**🔴 「吊销」不再是让机器下线的可靠手段。** 免凭据后同一 `machineKey` 重新注册会让记录**复活**，
因为服务端**无法区分**"这台机器重装了"与"别人抄了它的机器码"。
`EfAgentHostStore.UpdateRegistrationAsync` 里的 `row.IsRevoked = false` **必须保留** ——
去掉它会造出"重装过的机器永远连不上"的死结。
👉 **要真正下线一台机器，去那台机器上卸载。**

**不受影响的部分**：UDP 指令通道仍走 **HKDF(agentKey) + HMAC-SHA256 验签 + 来源 IP 白名单**（§17），
本次一行没动。

### 23.6 失败态收敛（免凭据后只剩三种）

| status | HTTP | 触发 | 含义 |
|---|---|---|---|
| **`invalid`** | **400** | `HostInfo` 为空 / `machineKey` 空白 | 唯一一条**内容层面**的校验：没 machineKey 就没法去重，每次重试都会多出一台幽灵机器 |
| **`toomany`** | **409** | 在线表已满且是新机器 | 兜底容量闸门 |
| **`error`** | **500** | 写库异常 | 服务端问题 |
| ~~`missing`~~ | ~~400~~ | — | **已随令牌一起消失** |
| ~~`unauthorized`~~ | ~~401~~ | — | **同上** |

⇒ **"装了客户端却在中心侧看不见"，不再可能由凭据问题引起**；
排查可以直接跳到**网络（9998 / 8080 是否通）与在线表容量**。
`_reportedRejections` 去重键仍是 `来源+机器码+状态`（去重逻辑没动，只是 status 取值集合变小了）。

**客户端的重试节奏**：`RevokedRetry` 分支现在挂的是 `toomany`（被拒后固定间隔重试），
注释同步改为"被吊销 / 在线表已满时的重试间隔"。

### 23.7 🔥 为什么**保留** `enrollment_tokens` 表（最容易被"顺手清理"的一处）

`EnrollmentTokenEntity` 与 `AppDbContext.EnrollmentTokens` 的映射**刻意保留**：

- 删实体 / 删 DbSet 会让 **EF 模型与 `M4_AgentHosts` 迁移快照不一致** ⇒ 必须补一个
  **`DROP TABLE` 破坏性迁移**，而这张表里没有任何需要抢救的数据。
- 保留的代价是"一个没有读写方的死表 + 一行注释"，**远低于**引入破坏性迁移的风险。
- `MigrationTests` 里"`enrollment_tokens` 表存在"的断言**因此仍然是绿的**（不是漏改）。

### 23.8 向后兼容：旧安装包 + 新服务端

旧安装包仍会带着 `enrollmentToken` / `joinCode` 两个字段发出请求。
`System.Text.Json` **默认忽略未知成员**，所以**不会**因为多两个字段而注册失败。
这条有测试注释钉着，避免有人把 `JsonSerializerOptions` 改成 `UnmappedMemberHandling.Disallow`。

### 23.9 🔥 新踩到的坑：判"端点还在不在"不能看状态码

冒烟脚本里我写了 `check("令牌端点已整体移除（404）", status == 404)` —— **它 FAIL 了，返回 200**。

原因：`Program.cs` 末尾有 `app.MapFallbackToFile("index.html", …)`（SPA 回退），
**任何未匹配的路径**（包括 `/api/...`）都会被它用 `index.html` 以 **200** 应答。

⇒ **正确的判据是"返回的不是 XX"**，例如断言响应不是带 `tokens` 的 JSON 对象
（实得 `body=<!doctype html>`）。这条同样适用于将来任何"验证某个 API 端点已下线"的场景。
⚠️ 顺带：`LicenseGate.IsGated` 只对 `GetEndpoint() is RouteEndpoint` 生效，
未匹配路径**没有 endpoint** ⇒ 不会被闸门拦，也不会得到 403，只会落到 SPA 回退。

### 23.10 测试与冒烟的改法（回归时照这个看）

- **Core**：删掉三条 `JoinCode` 用例（`AgentKeys.NewJoinCode/NormalizeJoinCode` 已不存在）。
- **集成测试（全绿 340 项）**：
  - `AgentsApiTests` 重写为**免凭据**语义：多台机器都能注册、**同机器码重复注册复用同一条记录**
    （`agentId` 不变、`agentKey` 换新）、缺 `machineKey` → **400 / `invalid`**、
    **吊销后重新注册会复活**（把这条运维结论写进测试）、`Operator` 那条令牌清单 403 断言删除。
  - 新增 `AgentRegistrationRejectionTests`：把"被拒注册的可见性 + 去重 + 落盘"三条回归**单独成类** ——
    因为它们要触发 **`toomany`**，而按生产默认容量得真灌 500 台。为此给
    `ControlServerFactory` 加了 **`agentMaxHosts`** 参数并**无条件赋值**环境变量
    （不无条件赋值 ⇒ 跨用例泄漏，把之后所有注册多台机器的用例一起打成 409；这是本文件记录过的头号陷阱）。
  - 其余四个测试类的 `RegisterAgentAsync` 辅助方法去掉"先建加入码"的前置步骤。
  - `MigrationTests` 的 `enrollment_tokens` 断言**保留**（见 §23.7）。
- **冒烟**：`agent-smoke.py` B 段重写为"免凭据注册 / 重复注册复用 / 缺机器码 400"，
  D 段去掉 `--Agent:EnrollmentToken=`；`transfer-smoke.py` 的 `enroll_agent()` 简化。
- **验证结果**：`dotnet test` **1172 项全绿**（Core 793 / Agent 39 / 集成 340）；
  `vue-tsc --noEmit` 退出码 0；`run-all.py` 全量 **7/7 场景通过**（agent 106 项、transfer 67 项均 0 失败）。

### 23.11 本轮**没做**

- 未做任何数据库迁移（刻意，见 §23.7）。
- 未加"注册速率限制 / 来源网段白名单"之类的新兜底 —— 与"不设任何门槛"的口径直接冲突，不做。
- `Security:AllowedCidrs` 仍然是**没有消费者的死配置**（一直挂账，本次没顺手删）。

### 23.12 交付产物（#215 收尾）与两个"打包专属"新坑

| 产物 | 大小 | SHA256 |
|---|---|---|
| `dist/win-x64/ControlServer.Server.exe`（绿色版·单文件） | 50.6 MB | — |
| `dist/win-x64-service/`（服务版·2 文件） | 50.6 + 35.3 MB | — |
| `dist/installer/ControlServer-Setup-1.0.0.exe` | 78.2 MB | `6B6F491B9233AC55FB4E4C969AEFC0E4E68B3715126C8119AEEF37D1B599DFCC` |
| `dist/agent-setup/ControlServer-Agent-Setup-0.1.0.exe` | 60.9 MB | `4B1E1B13644D637AD46383317C63101DF0104682D62C47B89CF8967DBF8F3D62` |

客户端安装包已投放到 `C:\ProgramData\ControlServer\downloads`。

🔥 **坑 1：`-SkipPayloadBuild` 会让你把旧客户端打进新安装包。**
`build-agent-installer.ps1 -SkipPayloadBuild` 复用 `dist/agent/*.exe`；
如果这些 exe 比 `agents/` 下的源码**旧**，安装包里就是**旧代码**，而且构建**完全成功、不报任何错**。
👉 **判据**：`ls -la dist/agent/` 的时间戳必须**晚于**客户端源码的改动时间。
本轮实测踩到（payload 13:47 / 源码 14:09）⇒ 必须不带 `-SkipPayloadBuild` 重跑一次。

🔥 **坑 2：验证"前端是否真被重嵌"不能裸 `grep` 交付 exe。**
单文件打包会**压缩内嵌资源** ⇒ 在 exe 里搜资源文件名**永远是 0 命中**，看着像"没嵌进去"。
👉 **正确做法**：起绿色版 exe（临时数据目录 + 空闲端口），`curl --noproxy '*'` 抓 `/`，
比对首页引用的 `assets/index-<hash>.js` 与刚构建的 `Server/wwwroot/assets/` 是否一致。
本轮实得 `index-BiU29thk.js` + `index-BDNHDv6_.css`，一致。

⚠️ **坑 3（PS 5.1）**：用 `-PublishTo ''` 表达"只编译不投放"**会失效** ——
空字符串实参被丢掉，脚本报「缺少参数 PublishTo 的某个参数」。
要么省略该参数（走默认投放），要么传一个真实目录。

**对打包 exe 的全量冒烟**：`python deploy/smoke/run-all.py`（默认打 `dist/win-x64`）
⇒ **7/7 场景通过**（agent 106 / transfer 67，均 0 失败）。

---

## 24. 三项新需求：UDP 默认开启 / 客户端独立可用 + 双向同步 / 回传路径下拉（2026-10-13，#216–#220）

### 24.0 需求原话与落点

| 需求 | 原话要点 | 落在哪 |
|---|---|---|
| ① | 「UDP 指令通道**默认开启**，默认端口 **8090**；测试发现指令**无效果**；网页里「端口」两个字要**横排**」 | `AgentConfigDefaults` / `AgentConfigMapper`、`AgentUdpCommandService`（新）、`AgentTaskConfigPanel.vue` |
| ② | 「客户端**本身应有**编辑延时/循环、定时、延时启动、端口修改，**可独立使用**；连上服务端时**自动实时同步**」 | `AgentLocalConfigStore` / `AgentLocalConfigUi`（新）、`AgentConfigManager`、`DistributionPanel` 等 |
| ③ | 「文件回传**新增下拉选客户机路径**，避免手输出错」 | `AgentBrowseDtos` / `TransferBrowseService`（新）/ `AgentFileBrowseService`（新）/ `AgentPathPicker.vue`（新） |

### 24.1 需求①：默认开启 + 端口 8080 → **8090**（#216）

- **`AgentConfigDefaults.DefaultUdpPort = 8090`**（原 8080）。
  🔥 **为什么必须改**：8080 在**同一个系统里同时指两件事**（中心 HTTP 与客户机 UDP 指令通道）。
  排障时"8080 不通"这句话是有歧义的，日志/文档里也分不清指哪个。8090 与本项目其余三个号
  （**8080** HTTP / **8091** 本机配置页 / **9998·9999** 发现·对码）**互不重叠**。
- 🔥 **语义变更（比端口号更容易被忽略）**：`AgentConfigMapper` 给的**空配置**从"通道关着"
  改成 **"UDP 默认开启（8090）+ 三张清单皆空"**。
  ⇒ 「**没配过**」不再等于「**通道是关的**」。任何按"默认关"写的旧假设都要重新审一遍。
  - 单测钉住：`AgentConfigRulesTests`（默认端口 8090）、`AgentConfigApiTests`（空配置 = 开启 + 8090）。
  - 客户机据此在**没有任何配置**时也会去绑 8090 开始收指令 —— 这正是需求①要的"默认开启"。
- **前端「端口」竖排**：`.form-row` 那类行内标签（文字 + 控件）在窄列里会把「端口」**逐字折成上下两行**。
  修法是给**行内标签**加 `white-space: nowrap`（**只对行内标签**，长段说明文字仍要能折行）。
  见 `AgentTaskConfigPanel.vue` 里那条长注释。

### 24.2 需求①（续）：**"指令无效果"的真正原因 = 没有发送端**（#217，`AgentUdpCommandService`）

冒烟与真机都显示：通道**验签、白名单、防重放**四关全对（§17），可现场就是"发了没反应"。
根因不是通道坏了 —— 而是**根本没人发得出来**：UDP 通道此前**没有任何服务端发送方**。

🔥 **这里有个绕不开的设计冲突**：报文签名密钥 = `HKDF-SHA256(agentKey)`，
而服务端**只存 agentKey 的哈希**（§5）—— **它自己算不出密钥**。

解法：**让客户机主动上报**（`ReportChannelKey`，每次连上长连接后发一次）。
- **只存内存、绝不落库**：密钥随 agentKey 变（重装/换机即变），落库要配一整套失效逻辑；
  服务端重启后客户机重连会重报，内存表自然恢复；代价只是"重启后那几秒发不出去"——
  而那几秒**本来就发不出去**（连接刚断）。
- **安全性**：泄漏这把密钥**不会泄漏 agentKey**（HKDF 单向），且它**只能发指令** ——
  而服务端本来就有权下关机令（走 SignalR 的 `power`）。⇒ **没有扩大任何权限**，只补了一条备用路径。
- `AgentUdpSendOutcome.Status`：`ok` / `nokey`（还没上报过，**不是故障**，是"刚连上或没连"）/
  `noaddress` / `badport` / `error`。⚠️ `ok` 只表示"**已发出**"，**不是"对方已执行"**（UDP 无应答）。

### 24.3 需求②：客户端独立可用（本机配置页）+ 与中心**双向 · 后写覆盖**（#218/#219）

#### 24.3.1 本机配置页 `AgentLocalConfigUi`（`Agent:LocalUi` 节）

- **默认开**，端口 **8091**（`AgentLocalUiOptions.DefaultPort`，刻意避开 8080/8090/9998/9999）。
  关掉（`Enabled=false`）时**根本不绑端口**（不是"绑了再拒"）。
- 🔥 **三道闸门**（缺一不可）：
  1. **只绑 `IPAddress.Loopback`** —— 物理上拒绝一切非本机来源；
  2. **`Host` 头必须是 `127.0.0.1` / `localhost`** —— 挡 **DNS 重绑定**（攻击者页面让浏览器
     去连 `127.0.0.1`，但 Host 头是它自己的域名）；
  3. **写操作要带随机令牌** `X-CS-Local-Token`（进程启动时随机生成，**内联进返回的 HTML**，
     页面上的 fetch 再带回来）—— 挡"同机另一个浏览器页面"的 CSRF。
- **托盘菜单「打开本机配置」**：地址由 `AgentLocalUiDiscoveryFile`（状态目录里的一个小 JSON）发布，
  托盘读不到就用 `DefaultPort` 兜底。⇒ `DefaultPort` **必须是常量**，不能是散落的字面量。
- ⚠️ **这个开关只关"本机改"这一条路**；中心下发那条路完全不受影响。

#### 24.3.2 配置权威 = **双向 · 后写覆盖**（**推翻原决策 D5**）

原决策 D5 是"事实源 = 服务端、客户机只读缓存"。需求②要求客户端**自己也能编辑并独立使用**，
于是改成**两边都能改、谁后写谁生效**。`AgentConfigDtos.cs` 的文件头注释已同步改写（**别只看那里还写着 D5**）。

- 🔥 **版本号不是同一基准，所以要分情况**：
  - **本机没有未同步改动**时：客户机采纳中心那份 = 中心的版本号；
  - **本机有未同步改动**时：本机版本取 `max(本机, 已知中心版本) + 1`，**只比时间戳**决定谁赢
    —— 两边的版本号本来就是不同基准，直接比大小是错的。
- 🔥 **中心落库的版本号永远是「中心那一份 + 1」**，**不采用客户机送来的数字**
  （`AgentConfigService.PushFromAgentAsync`）。中心起手 v0、客户机推 v1 ⇒ **中心也落 v1**（不是 v2）。
  这条在 e2e 里**一开始被判成 bug**（断言写的 v2）——**产品是对的，是测试期望错**。

#### 24.3.3 🔥 「待同步」标志**必须落盘**（#219 抓到的真实缺陷）

`AgentLocalConfigDocument.HasUnsyncedLocalEdit`（第三字段，可空）。

- **缺陷形态**：只把"本机有未同步改动"留在**内存**里 ⇒ 客户机一重启就丢 ⇒
  **离线改的那一版永远不会被推上去**，而**两边都不报错**（本机照跑、中心一直显示旧配置）。
  这是本项目最讨厌的"静默失败"。
- **修法**：字段落盘；`Save(config, origin, hasUnsyncedLocalEdit)` **刻意不给默认值**
  （强制每个调用点表态）；`RestoreFromStore()` 恢复，**旧文档（没有该字段）按 `origin` 推断**。
- **验证非空**：临时把 `unsynced = false` 退化 ⇒ `AgentConfigManagerTests` **2 条立刻变红**；
  恢复后全绿。⚠️ 不这么验一遍，等于没验（这个字段"改错了也不会报错"）。

### 24.4 需求③：回传源路径下拉 = 客户机目录浏览（#220）

#### 24.4.1 链路与角色

```
网页 AgentPathPicker → GET /api/agents/{id}/browse（服务端）
                     → 经 SignalR 长连接**反向调用客户机**（本项目唯一"一问一答"）
                     → 客户机 TransferBrowseService 校验白名单 + 列目录 → 原路返回
```

- **服务端 → 客户机的"一问一答"是本项目第一处**：此前 Hub 全是**单向推送**。
  用的是 `ISingleClientProxy.InvokeAsync<T>` + **服务端自持超时**（`AgentFileBrowseService.Timeout = 8s`）。
  ⇒ **客户机依旧不开任何入站端口**（M4.1 的承诺没破）。
- `AgentHubMethods.ListDirectory`（`Constants.cs`）—— 与其余"入队即返回"的 Hub 方法**不同**，
  这个**带返回值**。客户机侧 `AgentConnection.DirectoryBrowser`（为 null = 没这个能力）→
  未装配时回 `AgentBrowseResultDto.Unsupported(...)`。

#### 24.4.2 🔥 安全边界只有一条：**白名单在客户机手上**

服务端**不知道对方磁盘长什么样**，白名单是**客户机独有的知识**。所以：

- **越界判定必须先 `Path.GetFullPath` 解析、再比对** ——
  **不能看字面量有没有 `..`**（`D:\Inbox\..\Windows` 会被解析掉，字面判据会放过它）。
- 返回的状态（`AgentBrowseStatuses`）：
  `ok` / `disabled`（本机没启用传输）/ `denied`（越界）/ `notfound` / `notdir`（指到了文件）/
  `unsupported`（老客户端没这能力）/ `offline` / `timeout` / `error`。
- `ParentPath` 的语义：**null = 已经在白名单根上、不能再往上**（≠ "没有上级"）。
  前端据此显示"这里已经是那台机器允许读取的最上层目录"。
- `TransferBrowseService.MaxEntries = 300`，超出置 `Truncated` ⇒ 前端必须说清
  「**列表里没有的条目不代表它不存在**」（截断提示不能少，否则会被读成"文件不在"）。

#### 24.4.3 🔥 SignalR 单条消息上限：32 KB → **512 KB**

目录浏览的**应答**最坏约 80 KB（300 条 × 255 字符路径）> 默认 **32 KB**。
⇒ `builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 512 * 1024)`。

**症状极具迷惑性**：超限时**整条消息被丢弃**，表现为"**某个目录永远失败、换个目录又好了**" ——
极容易被当成"那个目录有问题"，而不是"消息太大"。

#### 24.4.4 端点与闸门

- `GET /api/agents/{id}/browse`（`AgentsController`，`AdminOnly`）；
  ⚠️ **失败也回 200**（业务性失败用 `ok:false` + `status` 表达，不是 HTTP 状态码）。
  只有"机器不存在"回 **404**、"path 过长"回 400。
- `LicenseGate` 归类为 **Allowed**（理由：只读查询；真正**作用到别的机器**的是"建回传任务"，那条已在 Gated）。
  ⇒ 新增 `/api` 端点**必须补分类表**（fail-closed 守门用例 `LicenseGateTests` 会拦）。

#### 24.4.5 前端 `AgentPathPicker.vue`（三条 UI 约定）

1. **失败不是异常，是就地一句话**：`result.ok === false` 用 `statusHint` **就地显示**（含「重试」），
   **不弹"系统出错"** —— 弹窗会把"点进了没权限的目录"渲染成"软件坏了"。
   与**网络层**失败（`failure`：断网/403/5xx）分开存。
2. 🔥 **多台目标时只能浏览一台**：白名单是**每台机器各有一份**的，没有"多台并集"这种东西。
   ⇒ 用一个显式选择器说明"现在看的是哪台"（并在选项里带上**离线**标记：离线是浏览的头号失败原因）。
3. **它只是"帮着填"，不是安全边界** —— 选出来的路径同样会被客户机再校验一次。

### 24.5 冒烟并入（#221）：`sync` / `browse` 两个新场景

两个脚本原本是 `.artifacts-check/` 下的临时验证脚本，本轮**并入正式冒烟**：

| 场景 | 脚本 | 断言数 | 内容 |
|---|---|---|---|
| `sync` | `deploy/smoke/sync-e2e.py` | 35 | ①注册 ②本机改→推中心（中心 v1 / 端口 / 任务名）③中心改→客户机采纳 ④「已应用版本」对齐 ⑤**无中心独立使用**（编辑 + 跨重启持久化 + 待同步标志存活） |
| `browse` | `deploy/smoke/browse-e2e.py` | 24 | 顶层=白名单根 / 进根（目录在前）/ 进子目录 / 根 `parentPath=null` / **4 种越界** / notdir / notfound / 404 / **离线→offline** |

**接法**（与既有 7 个场景同一套）：**中心由 `run-all.py` 起好**，脚本只负责**客户机那一侧**。
- `run-all.py` 新解析 **客户机 exe**（`CS_AGENT_EXE` → 被测程序旁边 → `dist/agent` → 调试构建），
  并经 `CHILD_ENV` 传给子脚本（沿用 `agent-smoke.py` 的既有约定）；
- `ctx` 新增 `work_dir`（落盘的临时目录）与两个 **TCP** 端口 `agent_ui` / `standalone_ui`
  （本机配置页是 HTTP ⇒ 要 TCP，不能用既有那批 UDP 端口）；
- ⇒ `--only` 可选值现在是 **web / pairing / admin / agent / transfer / sync / browse / license / rbac**。
- 单独跑也可以：`python deploy/smoke/sync-e2e.py <base_url> <work_dir> <ui> <standalone_ui>`
  （中心要自己先起好）；`CS_AGENT_EXE` 指客户机 exe。

⚠️ `sync` 的**阶段⑤**（独立使用）跑在"中心还开着"的时候 —— 靠的是那台客户机
**既没配 `ServerUrl`、又关掉了广播发现**，一个中心都找不到，与"世上没有中心"等价（注释里写明了）。

### 24.6 🔥 本轮踩到的坑

1. **`agent-browser` 的守护进程会在两次工具调用之间被回收**。上一条命令里 `snapshot` 拿到的
   `@eN` ref，下一条就 `Unknown ref`，`get url` 回到 `about:blank`。
   ⇒ **整个浏览器流程必须写在一次 bash 调用里**，且全程用 CSS 选择器 + `eval`，**不用 ref**。
2. 🔥 **`agent-browser eval` 的输出是 JSON**：字符串带包裹引号（`"OK"`）。
   不剥掉最外层引号，一切 `[ "$r" = "OK" ]` 都是**假失败**（本轮因此白跑一轮：
   连"页面标题 = 中控服务版"都判 FAIL，而值明明是对的）。
3. ⚠️ **不要把 `agent-browser` 的输出接管道**（`| head` / `| tail`）—— 实测会被 **SIGTERM**
   且退出码 1，看起来像命令本身失败。一律**重定向到文件**再读。
4. ⚠️ `screenshot <相对路径>` **不生效**（存到 `~/.agent-browser/tmp/`）；要传**绝对路径**。
5. 登录页有 `mustChangePassword` 弹窗会挡住整个界面 —— 本轮 `EnforcePasswordChange=false` 时
   响应里 `mustChangePassword: false`，**没有弹窗**（#134 起出厂管理员不再被强制改密）。

### 24.7 验证结果（#216–#221）

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln` | **0 警告 / 0 错误** |
| `dotnet test`（全量） | **Core 793 + Agent 60 + 集成 340 = 1193 / 0** |
| `npm run typecheck`（`vue-tsc --noEmit`）+ `npm run build` | **退出码 0**；产物 `index-zTj6tsxX.js` + `index-CNmnh0By.css`（**与上一次逐字节同名同大小 ⇒ 构建确定性**） |
| **真浏览器验证**（`agent-browser` + 真中心 + 真客户机） | **30 通过 / 0 失败**：登录 → 文件页 → 切「从客户机回传」→ 未选目标时「浏览…」禁用 → 选目标后可用 → 顶层=白名单根 → 进根（logs / readme.txt，目录在前）→ 「不能再往上」提示 → **点文件回填输入框**（根与子目录两条）→「← 可读目录」→ **杀掉客户机后「刷新」给出就地提示**「这台客户机当前不在线…」+ 重试 |
| `run-all.py --only sync` / `--only browse` | **各 0 失败**（35 + 24 条断言） |
| `run-all.py`（**9 个场景**，打 `dist/win-x64` 交付 exe） | **9/9 通过**：web 36 / pairing 4 / admin 78 / **agent 106** / transfer 67 / **sync 35** / **browse 24** / license 45 / rbac 94；`[FAIL]` = **0** |
| 前端确已重嵌（**运行时**核对，非裸 grep） | 交付 exe 返回的 JS/CSS 与磁盘 `wwwroot/assets/` **逐字节一致**（`398e5c52…` / `4a46774c…`）；`可读目录` 标记命中 |

⚠️ **冒烟日志里各场景的输出是交错的**：父进程 `run-all.py` 的 `print` 被缓冲、子进程直写文件 ⇒
按行序读会看到"下一个场景的标题出现在上一个场景的内容之前"。认结果要认**标记行**
（`结果：…` / `通过 N / 失败 …` / `N/9 场景通过`），别数行。

### 24.8 本轮**没做** / 挂账

- **`sync` 阶段⑤未验证"中心不可达时会不会无限重试打日志"** —— 只验了"找不到中心也能正常用"。
- **目录浏览未做"多台并行浏览"**（刻意：白名单每台一份，语义上不成立）。
- **`400 越界/超时` 未做前端限流**：连点「刷新」会并发发请求，靠服务端 8 秒超时兜底。
- 仍然挂账：`AllowedCidrs` 无消费者、PBKDF2、正式 `.ico`、原生 MSI、100 台压测、真机跨会话联调。

---

## 25. 🔥 2026-10-13 M4.7 变更（需求 1–4，#222–#229）

> **本节 = 当前事实**；与前文（尤其 §16.1/§16.2/§16.6/§17/§21/§24）冲突处，**一律以本节为准**。

### 25.1 需求①：UDP 指令**纯明文**（删签名、删防重放）

- 报文 = **一行 UTF-8 纯文本**：`DOWN`/`RESTART`/`+`/`-`/`0`/`1`，也接受 `name:argument`
  （`power:shutdown`/`power:restart`/`volume:up`/`volume:down`/`volume:mute`/`volume:unmute`）。
- **任意来源可发**；`UdpCommandSignature` 退化为**纯解析器**（不再有 `CS1|…|sig` 五段式）；
  🔥 `UdpReplayGuard` **整类删除** —— 明文无 nonce，"同一条指令再发一次"就是**合法新指令**（音量连按 3 次必须 +3）。
- 白名单**翻转为来源 IP 黑名单** `UdpChannelOptions.BlockedSourceIps`：**空 = 不拦**；命中即丢弃。
  🔥 **fail-closed**：源地址解析失败**且有**黑名单 ⇒ 判为拦；`MapToIPv4()` 归一化 `::ffff:x`。
- 🔥 **唯一边界 = 防火墙**（`install-agent.ps1` 的 UDP 入站规则）。代价已如实告知：同网段任意主机都能关机。
- **演练模式**（`ExecuteUdpCommands=false`）：**报文解析照常走完**，只有"真执行"被短路
  （否则冒烟里发的那条明文 `DOWN` 会把跑测试的这台机器**真关掉**）。
- 回归守卫：`agent-smoke.py` ④.5 发明文 `DOWN` → 断言日志出现 `收到指令：DOWN`（**"指令无效果"现场 Bug 的守卫**）+ 演练模式；换端口后发到 `blockedSourceIps:["127.0.0.1"]` → 断言 `来源 IP 在黑名单内`。

### 25.2 需求②：显卡信息（列表 + 详情都要）

- 契约 `AgentGpuSummaryDto(Name, Count)` + 静态 `From(...)`；数据来自硬件清单（**6h 一次**）
  ⇒ 必须 `AgentHostRegistry.HydrateGpuSummaries` 从库**水合**到在线表（否则刷新后列表是空的）。

### 25.3 需求③：文件下发**每台客户机独立存放目录**

- **目录落在明细级**（不是任务级）：`DistributionItem.TargetDir`；
  `DistributionItem.EffectiveTargetDir(taskTargetDir) => TargetDir ?? taskTargetDir` 是**唯一取值口**，
  派发（`DistributionsController.DispatchAsync`）与补领（`AgentTransfersController`）两处**共用**。
- 契约兼容**两种形状**：`DistributionCreateRequest(FileId, Targets?, TargetAgentIds?, TargetDir?)` ——
  `Targets` = 新（每台一条 `DistributionTargetRequest(AgentId, TargetDir?)`）；`TargetAgentIds`+`TargetDir` = 旧（全批共用）。
  控制器 `ResolveTargets` 做形状翻译；存储端口 `IDistributionStore.CreateTaskAsync(task, targets, …)`。
- 迁移 **`M4_6_DistributionItemTargetDir`**（`distribution_items.TargetDir`，maxLength 512，可空）。
- 服务层：`NormalizeTargets`（丢非法 id / 按 id 去重先出现者赢 / 目录去空白）、
  `CommonTargetDir`（**全部相同才**给任务级值，否则 null）、`DescribeDirs`（日志文案）。
- 前端 `DistributionPanel.vue`：目标区改**一台一行横排**（`.agent-row`），每行右侧自带 `AgentPathPicker`
  （`accept="directory"`、`label=""`、`suggestions=inboxDirsOf(id)`）；新增「首台套用到全部」；明细多一列 `item-dir`（列数 5→6）；
  任务级目录为 null 且各台不同时显示「各自指定（见明细）」。

### 25.4 需求③续 / ④：目录浏览**放开为整盘**（真资源管理器）

- `TransferBrowseService` 顶层 = `DriveInfo.GetDrives()` **全部盘符**；`Resolve` 只做 `Path.GetFullPath`
  （**不再比对白名单**）；`Parent` 用 `Path.GetDirectoryName`，盘根返回 `null`。删除 `IsUnder`/`Normalize`/`Roots` 白名单比对。
  构造函数变 2 参 `(TransferOptions, LogService)`（不再要 `stateDirectory`）。
- 🔥 `DriveInfo.IsReady`/`VolumeLabel` **必须各自 try**（光驱 / 可移动盘 / 断连网络盘会抛）；
  名字形如 `C:\（系统）（固定）`，未就绪加 `·未就绪`。
- 契约：`AgentBrowseRequestDto.Path` 空 = 全部盘符；`AgentBrowseResultDto.Roots` = 盘符全集；
  顶层 `AgentBrowseEntryDto.Name` = **带卷标的完整路径**；`ParentPath=null` = **盘根**。
  `AgentBrowseStatuses.Denied` **保留但新版不再产生**（旧版仍会回）。
- 前端 `AgentPathPicker.vue`：新增 `label`（默认 `'源路径'`）/`accept: 'file'|'directory'` props；
  🔥 **datalist id 必须逐实例唯一**（模块级自增 `instanceSeq`，否则"一台目标渲染一个"会**共用 id 串候选**）；
  目录模式加「用这个目录 / ✓ 已选中此目录」；"← 可读目录"→"← 此电脑"；"不能再往上"→"已经在盘根上"；文件在目录模式灰显不可点。
- ⚠️ **安全取舍**（已写进注释）：整盘浏览把客户机目录结构暴露给中心网页；
  真正的越界拒绝**退到"传输落地"那一步**（`FileTransferClient` 白名单仍在，拒绝发生在传输时）。
- 关键回归：`TransferBrowseServiceTests.A_directory_outside_the_write_whitelist_is_still_browsable`
  （以前断言 `denied`，现在必须 `ok`）；`browse-e2e.py` 同步改为断言白名单外目录 / `..` 走上去都 `ok`。

### 25.5 需求④：移除客户端页底部「客户端安装包」板块

- 右上角已有入口，底部重复 ⇒ 只删 UI 板块，**不动** `AgentInstallerLocator` / 下载端点。

### 25.6 本轮坑位（可复用）

- 🔥 **FluentAssertions `Equal(a, b)`**：比较**整段集合**必须 `Equal(a.ToArray())`，否则 `b` 被当成**第 2 个期望元素**
  —— 本项目**已踩第 4 次**。同陷阱适用于 `StartWith` 等 params 重载。
- **`dotnet-ef.exe` 自解析 SDK 失败**（`global.json` 要 8.0.100，加 `DOTNET_ROOT`/PATH 都无效）⇒
  改用 `dotnet.exe exec …/.store/dotnet-ef/8.0.10/…/tools/net8.0/any/dotnet-ef.dll migrations add …`。
- **`dotnet test` 一次只能给一个项目**（给两个报 **MSB1008**）⇒ shell `for` 循环分别跑。
- **迁移数守门用例**：新增迁移后 `MigrationTests` 的 `HaveCount` 要同步（本轮 9→**10**）。

### 25.7 验证结果（#222–#229）

| 项 | 结果 |
|---|---|
| `dotnet build ControlServer.sln` | **0 警告 / 0 错误** |
| `dotnet test`（全量，`--no-build`） | **Core 783 / Agent 78 / 集成 342 = 1203，失败 0** |
| `npm run typecheck`（`vue-tsc --noEmit`）+ `npm run build` | 退出码 0；产物 `index-CnXB6mw1.css` + `index-Ba6xmLWY.js` |
| 冒烟脚本 | 4 个 Python 脚本 `py_compile` 全 OK；`run-all.py`（**9 场景**，打 `dist/win-x64` 交付 exe）**9/9 通过，`[FAIL]` = 0** |
| 真浏览器/真机 | ⚠️ 见 §25.8 —— 首次 9 场景跑出 **agent / browse 两失败**，**根因 = `dist/agent` 是旧的**（旧 agent 仍认五段式签名 + 白名单浏览）；重建 `dist/agent` 后两场景**全绿**（agent **107/0**、browse **全部通过**） |
| 交付产物重建 | 绿色版 `dist/win-x64`（单文件 50.6 MB）、服务版 `dist/win-x64-service`（2 exe）、agent `dist/agent`（2 exe）、服务端安装包 `ControlServer-Setup-1.0.0.exe`（74.6 MB）、客户端安装包 `ControlServer-Agent-Setup-0.1.0.exe`（63.9 MB，已投放 `C:\ProgramData\ControlServer\downloads`） |

### 25.8 本轮**没做** / 待真机

- 🔥 **教训（必记）**：`run-all.py` 解析客户机 exe 的顺序是 `CS_AGENT_EXE` → 被测程序旁边 → **`dist/agent`** → 调试构建。
  改了 Agent 代码却**没重建 `dist/agent`** 时，agent / browse 两个场景会**拿着旧 agent 跑**，
  症状是"白名单拒绝 / 五段式报错"，**看起来像新代码坏了，其实是跑了旧二进制**。
  排查入口：先看 `dist/agent/*.exe` 的 mtime 是否晚于源码改动。
- **真机端到端（第二台物理机）**：明文 UDP 真关机 / 真音量、黑名单真拦、每目标独立目录真落到不同盘目录 —— 未做。
- ⚠️ **打包脚本与沙箱的冲突**：`build-*.ps1` 会 `Remove-Item` 已存在的输出目录；在本环境的沙箱下
  `Remove-Item` 被改道到 trash 且可能**报错退出**（有时实际上已删除）⇒ **重建前先把旧输出目录 `mv` 走**，
  再用 `-SkipPayloadBuild` 复用 payload。另：`build-agent.ps1` 会**先删后建**，
  被中断时会留下**空的 `dist/agent`**（`-SkipPayloadBuild` 会因此报"缺少两个 exe"）。

---

## 26. 🔥 2026-10-13 M4.8 变更（需求 1–5，#230–#234）

> **本节 = 当前事实**；与 §25.2（显卡摘要）、§24/§26（四张固定曲线）冲突处，**以本节为准**。

### 26.1 需求①：显卡从「主机列表」挪进「详情 · 硬件清单」

- 列表列定义去掉显卡列；型号改由 `AgentInventoryDto.Gpus` 在详情页「硬件清单」区块**逐块列出**（型号 + 显存）。
- ⚠️ 列表页不再水合显卡摘要 ⇒ `AgentGpuSummaryDto` / `AgentHostRegistry.HydrateGpuSummaries`
  那条路径**只剩详情页在用**（别再往列表上接）。

### 26.2 需求④：文件页客户机行**按列对齐**

- `FilesPanel.vue` 由"一行流式排布"改为**固定列宽 grid**；表头与数据行共用同一套
  `grid-template-columns` ⇒ 名称 / 在线离线 / IP / 存放路径 逐列对齐。
  改列宽时**只改一处**（共用变量），别在表头和数据行各写一套。

### 26.3 需求⑤：浏览提速 + **服务端**记路径

- **提速**：`AgentFileBrowseService` 改为"**先返回、再补细节**"；盘根枚举**不再逐盘调
  `DriveInfo.VolumeLabel`**（慢盘 / 断连的网络盘上那一步会卡住秒级，正是"打开某个盘特别慢"的根因）。
- **记路径**：契约 `AgentHostDto.LastSourceDir` / `LastTargetDir`（**可空 TEXT**，maxLength 512），
  迁移 **`M4_7_AgentLastPath`**；客户端**每次成功浏览后回报**。
- 🔥 **落库语义** `CASE WHEN @p IS NULL THEN 旧值 ELSE @p END`：
  **传 null = 这一项不动**、**空串 = 用那台机器的默认目录**。两条必须分开 ——
  混成一条的话"清空输入框"会变成"忘掉记住的路径"，而用户的本意恰恰相反。
- 前端 `AgentPathPicker` 新增 `initialPath`（打开时**直接落在记住的目录**）；
  ⚠️ `notfound` 时**自动退回盘符列表** —— 记住的目录可能已被删 / 盘符已摘除，不退就卡在死路径上。
- `DistributionPanel` 勾选客户机时用各自的 `lastTargetDir` 预填，但**用 `if (id in targetDirs) continue`
  判断"这一格还没被摆弄过"** —— 否则会覆盖用户手改过的值。

### 26.4 需求③：「接收到的数据」+ 指令不生效的排查（#233）

**客户端侧**：

- `ReceivedDataLog` 环形缓冲：容量 **200**、原文最多 **200** 字、**不落盘**（排查工具，不是审计日志）。
  计数与条目数**分开**（溢出后计数仍在涨）。
- **UDP 监听**（`UdpCommandListener`）四条路径各留一条：
  黑名单→`blocked`、解析失败→`unrecognized`（带 `reason`）、演练模式→`ignored`、执行完→`(outcome, detail)`。
  `ExecuteAsync` 由 `Task` 改为 `Task<(string Outcome, string? Detail)>`。
- **SignalR 回调**（`AgentRuntime`）：`OnCommand` / `OnConfigChanged` / `OnRevoked` / `OnWelcome` / `OnTransferTask` 各留一条。
  🔥 `OnCommand` **先记再动手** —— `restart` 会让进程重启，记在后面就**永远记不上**。
  `OnTransferTask` 原本 `_transfers is null` 时**静默丢弃**，现改为记 `ignored` 并说明原因。
- 本机页 `GET /api/received`（**只读，不过令牌闸门**）；`local-ui.html` 第 ⑤ 区块，3 秒自动刷新。

**服务端侧**：`AgentHubMethods.ListReceivedData`（无载荷，一问一答）→ `AgentReceivedDataService`（**8 秒超时**，
离线 / 超时**分开报**）→ `GET /api/agents/{id}/received`（AdminOnly，已登记 `LicenseGate`）。

🔥🔥 **三种"长得一模一样"的故障**（这就是需求③的存在理由，页面必须能分开）：

| 现象 | 根因 | 留痕 `outcome` |
|---|---|---|
| 机器毫无反应 | **根本没收到**（防火墙 / 网段不通） | 一条都没有 + `udpListening=false` 或端口不对 |
| 机器毫无反应 | **收到了认不出**（多一个空格 / 段数不对 / 拼错指令名） | `unrecognized`（**附原文**） |
| 机器毫无反应 | **认出了没执行**（演练模式 / 会话侧不在） | `ignored` |

⚠️ 解析器 `TryParse` 会 **`Trim()`** ⇒ 带尾随空格的 `"DOWN "` **能**解析成功、走的是"执行"而不是"认不出"。
测试里要验证"认不出"必须用 `"shutdown"` 这种**真的拼错**的串。

### 26.5 需求②：逐设备指标（#234）——**五层链，缺一层就静默失效**

| 层 | 实体 | 关键约束 |
|---|---|---|
| 契约 | `AgentDeviceKinds`（cpu/disk/net/gpu）、`AgentDeviceReadingDto(Kind, Key, Name, …)` | 四种设备**共用一个 DTO**，每种只填自己那一两个字段，其余 **null（不是 0）** |
| 采集 | `WindowsHostMetricsSource`（PDH 逐实例） | 实例名是**唯本身份**：`PhysicalDisk(*)`=`0 C:`、`Processor Information(*)`=**`组号,组内逻辑处理器号`（不是插槽！见 §26.7）**、`GPU Engine(*)`=`pid_…_luid_…_engtype_3D`；**`_Total` 必须剔** |
| 命名 | `TelemetryCollector.NameDevices` | 🔥 **不猜型号**：只有该种类**唯一**时才用清单型号覆盖；多路时保留 `物理磁盘 0（C:）` / `GPU 适配器 1` |
| 存储 | `AgentMetricEntity.DevicesJson`，迁移 **`M4_8_AgentDeviceReadings`** | **一列 JSON 而不是子表**（见 26.6） |
| 聚合 | `AgentMetricsAggregator.Accumulator` | 按 `(Kind, Key)` **各开一组 Series**；命名取桶内**最后**见到的 |

**🔑 两条一旦违反就静默出错的纪律**：

1. 🔥 **`Key` 是硬件的稳定身份，绝不能用显示名** —— `cpu0` / `disk0` / 网卡适配器 `Id`(GUID) /
   显卡 `luid_0x……_0x……`。用名字当 Key 的话，用户重命名一次网卡，曲线上就是
   "旧网卡下线 + 新网卡上线"、**断成两截**，而硬件一块没变。（守门用例
   `Bucketize_uses_the_last_name_seen_for_a_device`）
2. 🔥 **`Devices = null` ≠ 空列表** —— null = "这段没有逐设备数据"（**日聚合层不聚合逐设备** / 老客户端）；
   空列表会被前端渲染成一条**贴着 0 的直线**，看起来像"这块硬盘一直不干活"。

**前端**（`AgentDetailPanel.vue`）：区块改为**上方硬件选择器 + 下方单张曲线**。
- 选择器**只列窗口里真的出现过读数的硬件**（列一个永远画不出线的格子比不列更糟）；
  按 `AgentDeviceKinds.All` 顺序分组，每格显示**型号名 + 当前值**（当前值取**最后一个有读数的点**，
  因为速率类计数器要两次采样才有值、**首帧本来就是 null**，拿首帧会一律显示「—」）。
- `windowDevices` 的**数据源是时序点里的逐设备读数，不是硬件清单** —— 清单有型号但没有 `cpu0`/`disk0`
  这种稳定 Key，多路/多盘时对不上号。
- `devicePoints` 把选中设备的值**摊到与整机同形的扁平字段**上（其余一律 null）⇒
  逐设备曲线白捡了 `MetricsChart` 那套"**null 必须断开折线**"的画法，两处不会各自演化出两套 null 处理。
- 纵轴量纲随种类变：CPU / 显卡**钉死 0–100**；磁盘 / 网卡按数据自适应。
- `devicesUnavailableText`：一个硬件都列不出来时**必须说出原因**（日聚合 / 老客户端），
  否则用户点开"最近 30 天"只看到一片空白，然后去查一个不存在的故障。

### 26.6 为什么 `DevicesJson` 是**一列 JSON 而不是子表**（决策留档）

`agent_metrics` 是 **300 台 × 5 秒 = 60 行/秒**的表。子表会让每行明细再乘上"这台机器有几块硬件"
（典型 5–10）⇒ **写入放大 5–10 倍**、索引页数成倍增长。而这个数据的查法**只有一种**：
"把某个时间段这批行原样取回来画图"，从来不需要"只查第 3 块硬盘"。
**代价**：库里查不了逐设备（任何"按硬盘分组统计"的 SQL 都做不到），只能在取回后内存里算 ——
`AgentMetricsAggregator` 就是这么做的。真要 SQL 侧聚合时再补子表，**别为想象中的需求付写入代价**。

- 序列化走 `ControlJson.CompactOptions`（**不缩进 + 不写 null**）：
  缩进在配置文件里是优点，在这一列里纯属浪费（6 块硬件的 JSON 从约 1.2 KB 涨到约 1.8 KB，
  按 300 台 × 5 秒算是**每天多写 GB 级空白字符**）；逐设备读数是"每种只填一两个字段"的形状，
  原样写出去每块硬件都跟着 4 个 `null`。**配置仍用 `ControlJson.Serialize`（缩进版）**，别混。
- 读写两侧都 **try/catch `JsonException`**：序列化失败 → 丢这一帧的逐设备（**整机数据照写**）；
  反序列化失败 → 当"没有逐设备"。**一列坏 JSON 不该让整段历史打不开**。
- 上限 **64 台/帧**（`TelemetryCollector.MaxDevicesPerSample` / `AgentTelemetryService.MaxDevicesPerSample`
  / `EfAgentTelemetryStore.MaxDevicesPerSample` **三处同值**，改动要一起改）；
  服务端清洗：种类白名单 / Key 非空白 / **同帧 `(Kind,Key)` 去重（先出现者赢）** / Key 与 Name 截到 128。
  🔥 坏记录**逐条剔**，**不整批拒收** —— 为一条坏记录把整帧 CPU / 内存丢掉不划算。

### 26.7 🔥 2026-10-13 收口：四个**只有真机 / 真浏览器 / 真思考**才暴露的缺陷

M4.8 的逐设备链在编译期、单测、桩测试里**全绿**，却在真机与真浏览器里错了三处
（第 4 处是修第 3 处时自己引入的）。
留档的原因：前三处的**症状都伪装成"这台机器没采到数据"**，而那是完全正常的形态。

#### (1) `PdhGetFormattedCounterArray` 缺 `CharSet.Unicode` ⇒ 磁盘与 CPU **一条都不剩**

不加这一句，运行时把入口点解析成 **`…ArrayA`**；它填的是 `PDH_FMT_COUNTERVALUE_ITEM_A`，
**结构体布局与 W 版一模一样**（都是"指针 + 16 字节值"）⇒ **值全都读得对**，
只有 `szName` 是一串 ANSI 字节；再用 `PtrToStringUni` 解出来就是**乱码**。
上层按设计"认不出的名字静默跳过" ⇒ 磁盘 / CPU 全部消失（只剩 BCL 那条网卡）。
**不抛异常、不给错误码**，所以任何测试都测不出来 —— 除非真的调一次 Windows API 并检查名字。
守门用例：`WindowsHostMetricsSourceTests.Device_names_are_human_readable`。

#### (2) `Processor Information(*)` 的实例**不是插槽** ⇒ 1 颗 CPU 被报成 8 颗

实例名 `组号,处理器号` 里的第二个数是**组内逻辑处理器序号**。实测本机（1 插槽 / 4 核 / 8 线程）
得到 `0,0`…`0,7` 共 **8 个**实例。直接拿它当插槽 = **在页面上撒谎**（用户会去找 8 颗 CPU）。
修法：`GetLogicalProcessorInformationEx(RelationProcessorPackage)` 建"逻辑处理器 → 物理封装"映射再聚合。
⚠️ **那个枚举里 `RelationProcessorPackage = 3`，不是 0** —— 传 0 拿到的是 `RelationProcessorCore`，
4 核机器上会得到 4 颗"CPU"。
守门用例：`Cpu_device_count_matches_the_physical_package_count`。

#### (3) 实时帧不带逐设备 ⇒ 硬件曲线**永远停在打开页面那一刻**

`AgentMetricsSummaryDto`（实时推送 + 在线表用的摘要）原本**没有** `Devices`，
前端那格就写了 `devices: null`。于是详情页里只有整机那几条线在"往前挪一格"，
而硬件选择器上的「当前值」与硬件曲线**停在打开页面那一刻** ——
用户切到某块硬盘，看到的正好是"整机在飙、这块盘一动不动"，
而这**正是 `AgentMetricsSampleDto.Devices` 的注释里明令禁止的那种漂移**。
修法：`AgentMetricsSummaryDto` 加 `Devices`（末位可选参数，既有调用点不动），
`Summarize` 原样透传，前端实时帧改 `devices: m.devices ?? null`。
取舍留档见该属性的文档注释（否掉了"详情页每 N 秒重拉整个窗口"—— 那是一次真扫库查询，
而这份数据客户端**本来就采到了**）。

#### (4) **修 (3) 时自己引入的**：逐设备读数进在线表，却没挡 NaN / ±∞

把 `Devices` 放进摘要，等于把逐设备读数从"只存在于库和时序端点"搬到了 **`GET /api/agents` 的 JSON** 里。
而入站的 `AgentTelemetryService.SanitizeDevices` 当时**只清洗 Kind / Key / Name，没碰数值**。
于是风险等级变了：一个 `NaN` 原本只毁它自己那条曲线，现在能让
**整个在线表序列化失败**（`GET /api/agents` 走 System.Text.Json 严格模式，默认拒绝 NaN/Infinity）——
**一台坏客户端拖垮所有人的列表页**。这与当年温度的坑是同一个形态。

修法两道（幂等）：
1. `SanitizeDevices` 把六个数值字段过一遍 `Finite()`：非有限 → `null`（= "没采到"，**不是 0**）。
   ⚠️ 只丢非有限，**不做物理区间判断** —— CPU/GPU 是 0–100，但读写速率与上下行没有有意义的上下界
   （几十字节/秒到几十 GB/秒都合法），套区间只会误伤真实数据。这一点与 `Temperature()` 刻意不同。
2. `AgentMetricsAggregator.Series.Add` 里加 `if (!double.IsFinite(v)) return;` ——
   求和最容易踩这个：`_sum += NaN` 之后整桶均值永远是 NaN。
   这道挡的是"库里躺着历史脏数据"和"将来有别的调用方直接喂原始样本"。
   顺带说明：`Series` 是**整机指标与逐设备共用**的一路，所以这一行同时补上了整机侧的同类暴露。

守门用例：Core `A_not_a_number_device_reading_cannot_poison_the_bucket` /
`A_not_a_number_machine_reading_cannot_poison_the_bucket`；
集成 `A_not_a_number_device_reading_cannot_take_down_the_online_list`（要求**返回 200** 且坏字段为 null）。

#### ⚠️ 顺带：两条**假通过**的断言（这才是上面三个缺陷能溜过去的原因）

| 断言 | 假在哪里 | 改成 |
|---|---|---|
| `Disk_and_cpu_devices_are_actually_reported` | 只断言磁盘**存在**，没断言 `ReadBps` **有值** | 另补"至少一项有数"（UI 侧）／真机侧靠 §(1) 的名字检查 |
| UI 验证「当前值不是空的」用 `value !== '—'` | 磁盘那格显示的是 `读 — / 写 —`，**整串不等于 `—`** ⇒ 通过，而屏幕上一个数都没有 | 判据改为**串里出现数字**（`/\d/`） |

**通用教训**：断言"这个字段不为空"时，要问清楚**空值长什么样**。
本项目里 `—`、`null`、`0`、`读 — / 写 —` 四种"空"形态各出现过一次，
而每一个都曾让一条断言在功能坏掉的时候**照样变绿**。

#### 🔧 复用的排查手段

- **`.tools/device-probe/`**（本地，已 gitignore）：直接 `new WindowsHostMetricsSource()` 采 3 帧，
  把逐设备读数**原文**打出来。真机采集类缺陷**只有把原文打出来才看得见** ——
  它不抛异常、不给错误码，结构上只表现为"某个字段是 null"或"少了几条"。
- **`.tools/m48-ui-check` 的传输层探针**：`Page.addScriptToEvaluateOnNewDocument` 在页面脚本**之前**
  替换 `WebSocket` / `EventSource`，把 SignalR 收到的帧原文留在 `window.__live`。
  两个坑：① 必须在页面脚本之前装，加载后再替换已经来不及（SignalR 已抓走原始构造函数）；
  ② SignalR 的 JSON 协议**每条消息尾部有一个 `\u001e`(RS) 分隔符**，不剥掉它 `JSON.parse` 必抛 ——
  表现是"帧明明收到了、却一条都解析不出来"（第一版就是这么误判成"推送不带逐设备"的）。
- **验证"实时帧有没有被用上"的正确姿势**：**故意在数据长出来之前打开页面**。
  那一刻历史是**合法但全空**的（速率类计数器要两次采样），于是格子上的数**只可能**来自实时帧 ——
  把实时帧丢掉（`devices: null`）的实现在那里必然超时。数据等齐了再开页面，两条路径就分不开了。
- **`.tools/verify-dist.sh`**：校验**交付 exe 里内嵌的前端 == 刚构建的那一份**。
  单文件 exe 的 wwwroot 是**压缩**内嵌的 ⇒ 直接 grep 二进制只能看到 `index.html` 这个文件名，
  看不到资源名。所以它把 exe 跑在**一个没有 wwwroot 的目录**里（否则会读磁盘上的
  `src\ControlServer.Server\wwwroot`，那等于在验磁盘而不是验交付物），再请求首页比对资源哈希。
  用法：`bash .tools/verify-dist.sh [exe路径]`。

---

## 27. 🔥 2026-10-14 M4.9 变更（需求 1–7，#236–#247）

七项需求一次给齐，落点几乎全在**前端版面**与**两条状态同步链**上。
但本轮真正值得留档的不是功能，而是**断言本身写错**这一类事故：
一共踩到三次（两次假 FAIL、一次假通过），每一次都足以让人去改一段**本来就是对的**代码。

### 27.0 需求原话 → 落点

| # | 需求原话（摘要） | 落点 | 关键文件 |
|---|---|---|---|
| ① | 客户端的硬件温度监控设置为**默认勾选** | **安装侧**的默认值（客户端代码里**没有**这个默认值） | `deploy/windows/ControlServer.Agent.iss`、`install-agent.ps1` |
| ② | 历史曲线要显示**具体 GPU 型号**，不要 `GPU1/GPU2` | LUID → 型号 翻译（DXGI 首选 + D3DKMT 兜底） | `src/ControlServer.Platform.Windows/GpuAdapterNames.cs` |
| ③ | 硬件清单**收起时处理器最左**；展开时**每横排两种参数** | 摘要键顺序 + `grid-template-columns: repeat(2, …)` | `web/src/components/AgentDetailPanel.vue` |
| ④ | 子按钮集里的子按钮动作**改不了 IP / 端口** | 灰掉的可编辑框 → **只读文本 + 跳转按钮** | `ActionEditor.vue`（显示/跳转）、`SubSetForm.vue`（跳转目标） |
| ⑤ | 客户端 UDP 监听**默认开启**；中心与客户端的监听状态**实时同步** | 独立 Hub 方法 `ReportUdpChannel` + 在线表 `udpChannel` | `Server/Hubs/AgentHub.cs`、`agents/…/AgentRuntime.cs`、`Server/Services/AgentHostRegistry.cs` |
| ⑥ | 文件分发浏览**很慢、像卡住**；要能把浏览到的目录**存成该机默认目录** | 缓存 + **看得见**的加载态 + 显式「记住」+ 前端乐观覆盖层 | `AgentPathPicker.vue`、`DistributionPanel.vue` |
| ⑦ | 主机列表**去掉最后心跳**；4 个按钮的排版与左侧信息**对齐** | 删列（信息挪进状态徽章的 `title`）+ 2×2 grid | `web/src/components/AgentsPanel.vue` |

三个语义决策（用 `AskUserQuestion` 定的，留档）：

| 需求 | 备选 | 定下来的 |
|---|---|---|
| ④ | **保持共享、只改提示** ／ 子按钮各自独立地址 | **保持共享、只改提示** —— 灰框换成只读文本 + 「去设置集合地址」 |
| ⑤ | **心跳这一路要携带真实状态** ／ 另起一条独立上报 | **心跳携带真实状态**（实现上见 §27.5：因为 SignalR 实参规则，落在"同一个上报周期里的**一条独立消息**"） |
| ⑥ | **服务端记住、用来自动预填** ／ 只记在浏览器本地 | **服务端记住**（换电脑 / 换浏览器仍然预填得上） |

### 27.1 需求①：温度默认值**在安装侧**

**先说结论**：`Agent:Telemetry:ThermalEnabled` 的默认值**不在客户端代码里**。
客户端读的是"配置里写没写"：写了 `true` 才是开，**键缺失 = 关**，而且是**故意**的
（方案 §22「默认不装驱动」—— 一个会释放并加载内核级驱动 `WinRing0` 的能力，不该"没人告诉它就自己开"）。

于是"默认勾选"这句话只能落在**安装向导**上，串起来是三处：

```
.iss  GetThermalValue()：/THERMAL= 缺失 ⇒ True   ──┐
.iss  ThermalCheck.Checked := GetThermalValue()    ├─ 图形装 = 勾上；静默装 = 开
.iss  只有 `not ThermalValue` 才追加 -NoThermal    ──┘
install-agent.ps1  $thermal = -not $NoThermal.IsPresent
install-agent.ps1  把 Agent:Telemetry:ThermalEnabled = $thermal 写进 appsettings.json
```

⚠️ **方向不许写反**。旧写法是"勾了才传 `-ThermalEnabled`"；默认值翻过来之后，
那个方向会**静默地反过来**（不传 = 关）—— 界面上勾还是勾着的，现场却拿到「未启用」。
所以 `.iss` 改成"**没勾**才传 `-NoThermal`"，并在冒烟里把方向本身钉死（§27.9 的 F 段）。

**顺带删掉一个"看着像默认值"的死属性**：
`TelemetryCollector` 上曾有 `public bool ThermalEnabled { get; set; } = true;`，
注释还写着"与安装勾选项一致"。它**全仓零读写** —— 一个写了却不生效的默认值，
比没有它更危险：下一个人会照着它得出**相反**的结论。已删除，原处留注释说明真正的开关在哪。

### 27.2 需求②：GPU 型号（LUID → 型号）

`\GPU Engine(*)' 计数器**只按 LUID 区分适配器**，名字里没有任何型号信息。
两条路互相兜底（`GpuAdapterNames`）：

1. **DXGI**（首选）：一次拿到全部适配器，且带 `DXGI_ADAPTER_FLAG_SOFTWARE`
   ⇒ 能干净地剔掉软件光栅器（WARP / `Microsoft Basic Render Driver`，占用率恒为 0，
   当成一块显卡列出来会让人以为装了三块卡）。代价是裸 COM vtable。
2. **D3DKMT**（兜底）：纯 `gdi32`，不需要 COM，也不依赖枚举结构体版本；
   拿 PDH 已经给出的 LUID 逐个问。**走 GDI 的路径在 Session 0 里可用性更好** ——
   而 Agent 服务恰恰就跑在 Session 0。

🔥 **为什么不能"按顺序把硬件清单里的显卡名配上去"**：清单（WMI `Win32_VideoController`）与
计数器（PDH）的**枚举顺序、数量、成员集合都不保证一致**。本机实测清单是
`Intel UHD 620 + NVIDIA MX250`，而 GPU Engine 报出**三**个适配器（多一个软件适配器）。
按顺序硬配会把 MX250 的占用率画到 UHD 620 上 —— 那个错误**看起来是对的**，比占位名更坏。

两条路都失败 ⇒ 返回空表 ⇒ 调用方退回占位名（`GPU 适配器 N`）：宁可不说，也不说错。
`LUID` 用**数值**当字典键，不用 PDH 给的字符串 —— 十六进制补零位数是实现细节，
拿它当键会在某台机器上突然"一条都匹配不上"，且**没有任何报错**。

### 27.3 需求③：硬件清单版面

- **收起态**：摘要键顺序固定为 `处理器 → 显卡 → 物理磁盘 → 分区 → 网卡`。
  「处理器」是"这台机器是什么"的第一答案，必须在最左。
- **展开态**：`.inv-grid` 用 `grid-template-columns: repeat(2, minmax(0, 1fr))`，每横排两块。

⚠️ **为什么是 grid 而不是 flex-wrap**：可换行 flex 的"最小宽度"只等于**最宽的那个子项**，
所以一长串型号会把整行挤爆、或把第二块甩到下一行去；而 grid 轨道的
`minmax(0, 1fr)` 能真实地把两块压在各自一半里，长型号自己换行。

### 27.4 需求④：子按钮集动作的地址 —— 只读 + 跳转

**语义不变**：子按钮集的子按钮**仍然共享**父集的 IP / 端口，单条动作里的值**保存时会被集合覆盖**。
改的只是**提示**——原来的做法是"灰掉的可编辑输入框"，用户看到的是一个**能点、能输、却不会生效**的控件
（这类"看起来能改"的控件是现场最容易变成工单的东西）。

现在：

- `ActionEditor.vue`：`.shared-ro`（虚线框的只读文本）+ 说明「目标 IP 与端口由所属子按钮集统一提供
  —— 单条动作改了不生效（保存时会被集合的值覆盖）」+ 「去设置集合地址」按钮；
- 按钮 → `gotoSetAddress()` → `document.getElementById('sub-set-target-ip')` → `focus()`；
- `SubSetForm.vue` 里那个 `<input id="sub-set-target-ip">` 就是**跳转目标**，
  两边靠**同一个 id** 耦合（改动任何一边都要同时改另一边，已在两侧注释里互相点名）。

⚠️ 界面显示的一定是**集合的值**，不是这条动作里存的那份 —— 冒烟与 UI 验证都故意在动作里
塞一个假地址（`10.0.0.99:12345`）来证明这一点（否则"显示成动作里那份"会被漏掉）。
**主按钮那边必须没被顺手改坏**（仍然可编辑），这条也在 UI 验证里做回归。

### 27.5 需求⑤：UDP 默认开启 + **实时态**同步

**核心区分（本轮最重要的一条领域规则）**：

| | 是什么 | 谁说了算 |
|---|---|---|
| **配置态** `udp.enabled` / `udp.port` | "**我要它**怎样" | 中心下发 |
| **实时态** `host.udpChannel` | "**它现在**怎样" | 客户机自己上报 |

"点了没反应却两边都没错"的根源，就是把**配置态**当成了**实时态**。
所以页面上现在是**两行对照**（`已下发（中心）启用 :58182` ／ `客户机实际 正在监听 :8090`），
一致时**不报**"不一致"（不制造假警报），不一致时明确亮出来。

**实时态怎么来的**：客户机在**与心跳同一个上报周期**里发一条独立消息 `ReportUdpChannel`
（`AgentUdpChannelStateDto = { listening, port, stateText, executeEnabled }`）。

> 🔥 **为什么不在心跳里加个可选参数**：**SignalR 的实参个数必须与签名严格一致**，
> C# 的默认参数值在 Hub 调用里**完全不生效**（用 `.tools/hub-arity-probe` 实测过）。
> 给既有 `Heartbeat` 加一个可选参数，会让**没升级的老客户端**在调用时直接失败 ——
> 那等于用一个新字段把所有人踢下线。所以新增信息一律走**独立方法**。

**状态三分（null 语义）**，页面必须分开说：

- `UdpChannel == null` ⇒ **还没告诉我**（老客户端 / 刚上线）——「尚未上报」，不是"没在听"；
- `listening == false` ⇒ **明确说没在听**（附客户机自己给的原因）；
- `listening == true` ⇒ 在听。

**"默认开启"落在哪**：客户端**没有配置**时用自己的默认值（**启用 + 端口 8090**）；
配置里显式 `enabled=false` 才关。"除非手动关掉，否则监听皆为开启"就是靠这一条实现的。

代发指令被拒时（通道没在听）返回 **409**，并把**客户机自己给的原因**转述出来 ——
中心不再自己猜一句"可能是防火墙"。

### 27.6 需求⑥：浏览提速 + 路径记忆

**"卡顿假象"的成因**：本机回环上一趟 `/browse` 只有几毫秒，所以"点进去 → 页面毫无变化"
不是慢，而是**没有任何进行中的表示**。用户于是以为卡住、再点一次，而**每次点击都真的会再发一个往返**。
三件事一起改：

1. **缓存**：`Map<cacheKey, {at, result}>` + `CACHE_TTL_MS = 20000`；
   ⚠️ 键里**必须带 `targetId`** —— 不同机器的磁盘完全不一样，共用一份缓存会串味。
   ⚠️ **失败的也要缓存**（"这台机器离线"在 20 秒内同样不会变，而反复点一台离线的机器最费人）。
2. **看得见的加载态**：`loading` 时**不清空**旧列表（清空会闪白，反而更像卡住），
   而是给它加 `.entries.stale`（变灰）+ 顶部 `.status-line.busy`（转圈 + 「正在读取客户机目录…
   （一次真实往返：网页 → 服务端 → 客户机）」）。
   ⚠️ 这条进度行**从"只在列表为空时显示"改成一直显示** —— 那正是本需求的核心。
   反过来，旧内容仍可见 ⇒ `pick()` 里必须有 `if (loading) return` 挡住误点，
   否则用户点的是**上一层**的条目，却以为点的是新列表里的。
3. **显式「记为此机默认目录」**：把当前所在目录存到服务端
   （`PUT /api/agents/{id}/path-memory`，`AgentPathMemoryRequest { TargetDir, SourceDir }`），
   下次打开这台机器时路径框**自动预填**、浏览**从那一层开始**。
   ⚠️ 两个字段都是**可选**：传 `undefined` / 不传 = **不动那一项** ——
   下发方向只该动 `targetDir`、回传方向只该动 `sourceDir`，一次只挑了一边不该把另一边顺手清掉。

#### ⚠️ 本轮查出的**真缺陷**：「点了浏览没反应」——一个被 `void` 吞掉的 TypeError

**症状**：在一台**从没记过目录**的客户机上，点「浏览…」→ 面板打开、然后**什么都不发生**：
不发请求、不转圈、不报错、盘符列表空着。

**根因链**（四步，每一步单独看都很合理）：

1. `DistributionPanel` 的 `targetDirs` 是 `reactive<Record<number,string>>({})`，
   填值的那段 `watch` 是 `if (rememberedDir !== '') targetDirs[id] = rememberedDir`
   —— **没有记忆值就什么都不写**，键**不存在**；
2. 行内绑的是 `v-model="targetDirs[agent.id]"` ⇒ 子组件收到的 `modelValue` 是 **`undefined`**；
3. 而 `AgentPathPicker` 声明的是 `modelValue: string`，`withDefaults` 里**又没给它默认值**
   ⇒ `props.modelValue.trim()` 抛 **TypeError**；
4. 调用点是 `void openAt()` ⇒ **异常被无声吞掉**，`open` 已经翻成 `true`，
   于是"面板开着、内容空着、错误没有"。

**为什么它特别难抓**：只在"这台机器还没有记忆目录"时出现 ⇒ **全新部署必现、老现场看不见**；
从外面看（DOM、网络请求数、按钮状态）**全都正常**，唯一知道真相的是**浏览器控制台**。

**修法（三处，纵深防御）**：

- `AgentPathPicker`：`withDefaults` 给 `modelValue` 加默认值 `''`（顺带消掉 Vue 的
  "Missing required prop" 警告）；`startPath()` / `adopted` 里再 `?? ''` 兜一层。
- `DistributionPanel`：填值改成**无条件** `targetDirs[id] = lastTargetDirOf(id)`
  —— 哪怕记的是空串也要让键存在。`v-model` 的目标**不该是一个可能不存在的键**。
- `.tools/m49-ui-check`：新增 **Q 段**——监听 `Runtime.exceptionThrown` 与 `console.error`，
  整轮结束断言"页面没有未捕获异常"。

> **通用教训**：`void someAsync()` 是**故意丢结果**，但它同时把**异常**一起丢了。
> 凡是"点了没反应"的现场，第一件事是**看控制台**，而不是猜网络。
> 一个从不看控制台的前端探针，会系统性地漏掉**整类**静默失败 ——
> 本轮 40 条断言里，**没有一条**能发现这个缺陷，直到补上 Q 段。

🔥 **前端为什么要"乐观覆盖层"**：`DistributionPanel` 里的 `agents` 是**父级传下来的只读 computed**，
本页改不动它。点完「记住」如果只等父级刷新，会有一段"界面还是旧值"的窗口。
所以 `remembered: Record<string, string>` 记下刚记住的值，`lastTargetDirOf()` 优先读它，
并在 **父级送来新列表时整体清空**（那时服务端的值已经追上来了）。这是"本地乐观更新 + 明确失效"，
不是"再缓存一份真值"。

### 27.7 需求⑦：主机列表

- **删掉「最后心跳」列**（表头 8 列 → 7 列，`colspan` 同步改）。
  ⚠️ **信息不丢**：挪进状态徽章的 `:title`（`最后心跳：…` / `从未连接过`）。
  删列删的是**版面**，不是**事实** —— 这两件事必须分开。
- **4 个按钮（重启服务 / 吊销 / 移除 / 详情）排成 2×2**，并且与左侧多行信息**同轴**：

```css
.row-actions {
  display: grid;
  grid-template-columns: repeat(2, minmax(88px, 1fr));  /* 同排等宽 */
  gap: 6px;
  align-content: center;                                 /* 块在行内垂直居中 */
}
.actions-cell { vertical-align: middle; }
```

这次没有靠眼睛，而是**量**：按钮块中心 vs 整行中心（≤4px）、按钮块高度 vs 整行高度、
四个按钮落在几行、同排两按钮宽度差（≤2px）。实测：中心相差 **0.5px**、按钮块 60px vs 整行 71px、
2 行各 2 个、宽度 `[98,98,98,98]`。

### 27.8 🔥 本轮踩到的坑（可复用）

#### (1) **假 FAIL 之一：断言把"需求"读错了对象**（需求①，已修）

冒烟里写的是 `check("默认启用温度监控（不是 disabled）", thermal != "disabled")`。
它把需求①读成了"客户端不带配置也该默认开"，而需求要的是"**安装向导那个勾选框默认勾上**"。
冒烟跑的是**裸起 exe**（没有 appsettings.json）⇒ 键缺失 ⇒ 关 ⇒ **代码完全正确也照样红**。
**教训**：断言里出现"默认"二字时，先问清楚**默认值是哪一层定的**（配置？安装器？代码字面量？）。
改法：断言改成"裸起 ⇒ 如实报未启用"，而"默认勾上"由 **F 段静态检查**（读 `.iss` / `.ps1`）钉住。

#### (2) **假 FAIL 之二：断言比被测行为更早下结论**（需求⑤，已修）

`.tools/m49-ui-check` 里"等客户机绑上刚下发的端口"那段，原本是
`if (liveChannel) break` —— 只要**上报了一个通道**就收手。
而客户机在下发之前**就已经带着自己的默认端口 8090 在听了**，所以第一次轮询就满足条件，
画面永远停在 8090 ⇒ 两条断言双双变红，看起来像"下发的配置没生效"。
**教训**：轮询的退出条件必须精确到**你要的那个值**（`port === udpPort`），
不能是"对象非空"这种近似条件。修完立刻全绿（同一台客户机，冒烟里等 30 秒本来就是绿的）。

#### (3) **假通过：`non-empty` 掩盖了"话说错了"**（需求①，已修）

`WindowsThermalSource` 关掉时写死了一句 `"安装时未启用硬件温度监控。"`。
在**绿色版**与**裸起 exe** 这两条路上那是**假话**（根本没有"安装时"这回事），
它会把运维支去翻一个他从没用过的安装包。
而冒烟那条"有没有给原因"的断言只判 `bool(note)` —— **它非空、没错字、还挺像人话，于是照样绿**。
**教训**：断言"给了说明"时，要问**说明指的方向对不对**。
修法：组合根分得清"**配置里没有这一项**"与"**显式关掉了**"，把结论算好传下去
（`WindowsThermalSource(bool enabled, string? disabledNote)`），冒烟再加一条
"原因里说的是「配置里没有」，不是甩给安装向导"。

#### (4) 探针把失败原因吞掉 ⇒ 白跑一轮

第一版的等待写成 `waitFor(条件 ? p : null)`，超时时只能报出 `null`：
"面板没打开 / 打开了但 0 条 / 压根没渲染"，一概看不出来。改成自己轮询、
**留着最后一次原始探针**再报出去，一次就定位到根因。

#### (5) **`node --check` 是模板串脚本的第一道闸**

探针是 JS 模板字面量，注释里顺手写的反引号会**提前把串截断** ⇒ 整个脚本一行都跑不到
（而且 `bash run.sh` 的 `&&` 把它挡在后面，日志里只剩**上一次**的运行结果 —— 极易误判成"改了没生效"）。
写完探针**先 `node --check`**。

#### (6) 又一条"同源"坑的复现（沿用 §26 的结论）

`run-all.py` 的 `_resolve_agent_exe()` 之前会落到 `dist/agent/`（**上一次打包**的客户端），
而服务端是刚构建的 `.artifacts-check` 产物 ⇒ 服务端测新代码、客户机测旧代码，
四条与客户端能力有关的断言（显卡型号 / 温度 / UDP 实况）**全红**。
已加 `sibling` 解析项并**排在 `dist/agent` 前面**：按被测服务端所在目录反推同一次构建的客户机。
**假 FAIL 比不做断言更坏** —— 它会让人去改一段本来就是对的代码。

### 27.9 验证结果（#236–#247）

- **构建**：`dotnet build ControlServer.sln -c Debug --artifacts-path .artifacts-check` ⇒ **0 警告 0 错误**。
  （⚠️ 跑 `dotnet test` 中途被打断会留下 `testhost.exe` 持有产物 → `MSB3027` 文件被锁；
  `taskkill //F //IM testhost.exe` 后即可。）
- **测试**：Core **796** / Agent **98** / 集成 **385** = **1279，失败 0**
  （⚠️ 三个项目并行 + 中途 kill 会造成"测试主机进程崩溃"的假象；**拆成三条命令分别跑**。）
- **前端**：`npm run typecheck`（`vue-tsc --noEmit`）+ `npm run build` 退出码 **0**
  （产物 `index-CbSwjS-N.css` 47.84 kB + `index-c5xxe72D.js` 380.01 kB），已重新嵌进 `Server/wwwroot`。
  ⚠️ 前端产物是**编译期嵌进程序集**的 ⇒ 改前端后必须 `npm run build` **再** `dotnet build`。
- **冒烟 `--only agent`：143 PASS / 0 FAIL**。新增 **F 段（10 条静态检查）** 钉住需求①的默认值方向，
  并对四条关键检查做了**变异测试**（把方向写反 / 多加一处赋值 / 复选框默认改不勾 / 改回旧反向写法），
  **四条全部被抓住** —— 静态检查是有牙齿的，不是"写上去好看"。
- **真浏览器验证 `.tools/m49-ui-check`：53 PASS / 0 FAIL，退出码 0**（覆盖需求 ③④⑤⑥⑦ 的版面与交互）。
  工具链：`run.sh` 一次调用完成"起临时实例 → 无头 Edge + CDP → 跑探针 → 收摊"。
  要点：`Network.emulateNetworkConditions` 加 **1200ms 延迟**才能复现"卡顿假象"（回环上几毫秒根本复现不出来）；
  `Network.requestWillBeSent` 数 `/browse` 往返次数验缓存；
  `Emulation.setDeviceMetricsOverride` 固定 1680×1000 量像素；
  **Q 段**监听 `Runtime.exceptionThrown` / `console.error` 守"静默失败"（见 §27.8 那个真缺陷）。
  实测到的版面数字：按钮块中心与整行中心差 **0.5px**、按钮块 60px vs 整行 71px、四个按钮 2 行各 2 个且等宽 `[98,98,98,98]`；
  硬件清单展开态每行 `[2,2,2]` 块；摘要键顺序 `处理器 → 显卡 → 物理磁盘 → 分区 → 网卡`；
  浏览缓存生效（再进同一层 **0 个**新往返）；「记住」后库里与界面**同为** `C:\`。
- **全量冒烟 `run-all.py`：9/9 场景通过**（web / pairing / admin / agent / transfer / sync / browse / license / rbac）。
  `agent-smoke.py` 单独跑是 **143 PASS / 0 FAIL**。

### 27.10 本轮**没做** / 待真机

- ⚠️ **Session 0 下 DXGI / D3DKMT 能否拿到 GPU 型号，只能在真机上验**。
  本机只能验证"有桌面的会话"里两条路都成立。若 DXGI 在服务里枚举不出来，
  表现是**退回占位名**（"这个改进悄悄没生效"）—— 所以 D3DKMT 兜底必须留着。
- ⚠️ 温度那条：本机是**开发机裸起**，拿到的是"未启用"（正确）。
  "装上驱动之后真的读到温度"需要真机 + 管理员/SYSTEM 权限再验一次。
- 绿色版（`dist/win-x64`）**没有安装向导**，所以需求① 对它不适用：
  它走的是"配置缺失 = 关"。需要温度时得手写 `appsettings.json` —— 这一点写进了启动日志的提示语。

---

## 28. 🔥 2026-10-14 M4.10 变更（需求 1–2，#248–#254）

### 28.0 需求原话 → 落点

| # | 原话 | 落点 |
|---|---|---|
| ① | 客户端功能优化，**在未连接到服务端时，必须打开 udp 监听** | `UdpCommandListener.Apply(config, connected)` 新增第二参数 + `AgentRuntime` **六处**接线 + 单测 7 条 + 冒烟 **G 段** |
| ② | 客户端托盘右键菜单优化，仅保留「客户端服务：运行中」与「打开本机配置」两个选项，**本机配置网页内显示服务端相关信息** | `Agent.Tray/Program.cs` 菜单收敛到 4 项；`AgentStatusSnapshot` 加 `ServerVersion`/`LastSeenAt`；`local-ui.html` 新增「服务端」卡片（含「打开服务端网页」按钮） |

**四个语义决策**（用户当场选定；后面一切按它做，不再回问）：

1. 配置里**曾经显式关掉**过 UDP、且此刻未连接 ⇒ **一律监听**，端口沿用配置（没配置用 8090）。
2. **被管理员吊销**的客户机在未连接期间 ⇒ **也打开**。用户明确接受"被吊销 ≠ 失去 UDP 控制"这个代价。
3. 托盘被移除的入口 ⇒ 服务端网页 / 启动停止客户端服务 / 退出托盘**全删**；
   **数据目录与日志目录刻意保留**（排障第一步）。
4. 本机页 ⇒ **完整一条「服务端」卡片**：地址 / 连接状态 / 本机在中心里的名称与 ID /
   最后通信时间 / 服务端版本 / 配置来源与同步状态 + 「打开服务端网页」按钮。

### 28.1 🔥 需求① 背后是一个**真缺陷**，不是"加一个功能"

`Apply` 原来有**两个**调用口子，而**新机器一个都走不到**：

| 口子 | 触发条件 | 新机器的实际情况 |
|---|---|---|
| `OnConfigApplied` → `ApplyUdpChannel` | `RestoreFromStore()` **成功**（本机有配置）才触发 `Applied` | 没有配置 ⇒ 不触发 |
| `RunConnectedAsync` 连上中心之后补一次 | **必须连上中心** | 没中心 ⇒ 永远不到 |

⇒ 一台**从没连过中心、本机也没有配置**的机器，`Apply` **一次都没被调用过**：
`netstat` 里连 8090 都不存在。现场形态就是"机器装好了，发指令毫无反应"。
**"装完就能用"这句话在此之前是断的**（2026-10-13 改成明文协议时只解决了"没凭据也能起"，没解决"没人调它起"）。

### 28.2 改法：把"该不该听"拆成**两个决定者**（优先级从高到低）

1. **本机总开关** `Agent:Tasks:Enabled` —— 假则**永不听**，任何配置、任何连接状态都翻不过来；
2. **是否连着中心**（`Apply` 的 `connected` 参数）——
   **未连接 ⇒ 一律监听**（忽略 `Enabled=false`；端口沿用配置，越界则回落 8090）；
   **连上中心 ⇒ 回到"配置说了算"**（`Enabled=false` 就真的不绑端口）。

- 端口**沿用配置**是刻意的：现场发送脚本指着固定端口，因为断一次中心就换端口等于把脚本一起弄坏。
- 越界时**回落 8090 而不是 `Stop`** —— 拒绝监听等于把这条能力关掉，与需求正好相反（并留一句 `Warn`）。
- `StateText` 分两种说法：`正在监听 0.0.0.0:{port}（未连接中心，已强制开启）` / 不带括号那句。
  **"为什么在听"必须说得出来**，否则现场会以为这是配置的功劳，然后去配置里找一个不存在的开关。

### 28.3 🔥 只换连接状态**不重绑端口**（一个新加的判据）

`Listen(port, blocked, standalone)` 里，`standalone` 变了**只翻标志 + 刷文案**，
**不参与 `Matches` 比较**（端口与黑名单才比）。

理由：连接状态是会抖的（一次网络抖动就是"断 → 再连"），而重绑端口在那两个瞬间
会让端口短暂没人听 —— 那是实打实的**收包空窗**，可能正好丢掉一条 DOWN。
更关键的是：**"未连接才强制听"影响的从来不是"听不听"，只是"为什么在听"** ⇒ 端口本就该一动不动。

### 28.4 🔥 吊销路径**反转**了，以及为什么现在要有 `_lastUdpConfig`

改版前：`OnRevoked` → `_udp.Stop("本机已被管理员吊销…")`（理由是"中心不认它了，还开着一个谁都能发的口子说不过去"）。
现在：`OnRevoked` → `ApplyUdpChannel(_lastUdpConfig, connected: false)` —— **照听**。
理由：被吊销的机器往往**正是那台需要被现场工具立刻关掉的机器**（误吊销 / 换中心），
这时候连"关机"都做不到才是最糟的。

⚠️ **由此多出一个必须记住的接线**：`_configs.Clear()`（吊销时会清掉内存与盘上的配置）
会让 `_configs.Current` 变成 `null`，而这台机器仍要继续听 UDP —— 端口要沿用**吊销前那份**。
现读 `_configs.Current` 的话，端口会在"被吊销"这一刻**悄悄回到 8090，且没有任何一行日志解释为什么**。
所以 `AgentRuntime` 新增 `_lastUdpConfig`（`ApplyUdpChannel` 里同步一份）。

**六处接线**（缺一处就是一个"某些情况下不灵"的现场问题）：

| 位置 | 时机 | 传的 connected |
|---|---|---|
| `RunAsync` 开头（**新增**，关键修复） | 启动后**无条件**决策一次 | 显式 `false` |
| `OnConfigApplied` | 配置生效（中心推送 / 本机页面 / 缓存恢复） | 用 `Connected` 现值 |
| `RunConnectedAsync` 连上之后 | 覆盖"配置在缓存、服务端版本没变"那条路 | 用 `Connected` 现值（此刻 true） |
| 主循环 `catch`（**新增**） | 迭代抛异常 ⇒ 顺手把 `Connected` 落地 | 用 `Connected` 现值（已置 false） |
| `OnRevoked` | 吊销 | 显式 `false`（此刻 `Connected` **还是 true**，长连接正在收尾） |
| `RunConnectedAsync` 心跳循环退出后（**新增**） | 断线 ⇒ 立刻按"未连接"重新决策 | 用 `Connected` 现值（已置 false） |

⚠️ 主循环 `catch` 里补 `Connected = false` 是顺手修的一个**状态机空洞**：
`Connected` 原本只在 `RunConnectedAsync` 的两端被赋值，异常从中间跑出去时两行都没走到，
字段就**停在 true** —— 于是"中心已经不可达了，却还按'中心在'的语义决定端口"。

### 28.5 需求②：托盘 4 项 + 本机配置页「服务端」卡片

**托盘菜单**（`BuildOptions`）现在是：`客户端服务：{运行中|已停止|未安装}`（信息行）→
「打开本机配置」→「打开数据目录」「打开日志目录」。
默认项（双击）**固定**为「打开本机配置」（以前是"有中心就开中心"，收敛后那会变成一个**隐形入口**）。

- ⚠️ `case TrayCommands.Exit:` **不能删**：它不在菜单里了，但平台层收到 `WM_CLOSE`
  （`taskkill` 不带 `/F`）时会翻译成这条命令 —— 那是外部关闭请求**唯一**的优雅停机路径。
- ⚠️ `TrayCommands` 枚举值**保留**：服务端托盘 `ControlServer.TrayAgent` 还在用。
- ⚠️ `BuildSignature` 同步瘦身（去掉了 `ServerUrl`）：判据必须与实际渲染的东西对齐，
  否则菜单会在没人看得见的地方白刷，将来变成"它为什么老闪"的疑案。
- ⚠️ **后果要说清**（已接受）：托盘不再是启停服务的入口（用 `services.msc` / `sc`）；
  也没有"退出托盘"菜单项（要停只能 `taskkill`，那条路仍是优雅停机）。

**状态文件新增两个字段**（`AgentStatusSnapshot`）：
`ServerVersion`（来自注册应答 / 欢迎消息）、`LastSeenAt`（欢迎 + 每条成功心跳回执刷新）。

- 位置参数**带默认值**（`= null`）⇒ 旧 `status.json` 仍能反序列化。
- `LastSeenAt` 走**独立的** `TouchLastSeen()`，**刻意不经过 `UpdateStatus`**：
  后者的契约是"状态变了就通知"，而心跳是每 10 秒一次的正常节奏，`Status` 一个字都没变。
- `UpdateStatus` / `TouchLastSeen` 共用抽出来的 `WriteStatus()`（两个调用方写同一份东西，各写一遍必然漂移）。

**本机配置页**：原来的 `#strip` 状态条（配置版本 / 最后写入 / 保存于 / 连接徽章）**整体升级**成
一张「服务端」卡片（`dl.kv` 左名右值，值一律等宽 —— 这一栏的用处就是"照着念 / 抄下来核对"），
信息挪进去且不再重复；`renderStrip` → `renderServer`，`refreshStrip` → `refreshServer`。
新增「打开服务端网页」按钮：**点击时现取一次地址**再开（页面可能几小时前就打开了，那时还没连上中心），
并对老版本状态文件里的纯地址补 `http://`。

⚠️ 顺带改掉一处**已经变成假话**的说明：UDP 区块原文写「关闭时客户端**完全不绑定该端口**」——
需求① 之后只在"已连接"时成立，已改写并说明唯一能彻底不听的办法是 `Agent:Tasks:Enabled=false`。

### 28.6 🔥 顺带修的两处隐患（不修迟早变成"偶发故障"）

1. **状态文件写入不再原子**（原来直接 `File.WriteAllText` 覆盖）。
   心跳每跳都刷 `LastSeenAt` ⇒ 写入频率从"状态变化时"提到"每 10 秒"，
   而托盘与本机页面正在**同时**读它 —— 读到半截 JSON 就是 `Read` 返回 `null`，
   托盘冷不丁闪一下"未连接"。改成"写 `.tmp` 再 `File.Move(overwrite: true)`"，
   并加一道**进程内写锁**（写它的线程不止一个：主循环 / SignalR 回调 / 心跳循环，
   它们共用同一个 `.tmp` 文件名）。
2. **`Connected` 字段的状态机空洞** —— 见 §28.4 最后一段。

### 28.7 验证结果（#248–#254）

- **构建**：`dotnet build ControlServer.sln -c Debug --artifacts-path .artifacts-check` ⇒ **0 警告 0 错误**。
- **单元测试**：Core **796** / Agent **104**（+6）/ 集成 **385** = **1285，失败 0**。
- **变异测试**：把 `Apply` 里的 `if (!connected)` 短路成 `if (false && !connected)` ⇒
  **4 条新用例立刻变红**（另 2 条走的是不同轴：总开关 / 无配置默认值，本就不该红）⇒ 断言有牙齿。
- **冒烟 `--only agent`：159 PASS / 0 FAIL**（上轮 143，**新增 G 段 16 条**），退出码 0。
  G 段打出来的实证：`正在监听 0.0.0.0:54166（未连接中心，已强制开启）`、
  `[PASS] 配置说「关」、又没有中心 ⇒ 端口仍然绑着（需求① 核心）`、
  `[PASS] 演练模式挡住了执行（机器没有被关掉）—— ignored`。
- **全量冒烟**：`python deploy/smoke/run-all.py`（被测＝刚打的 `dist/win-x64/ControlServer.Server.exe`）
  ⇒ **9/9 场景通过**（web / pairing / admin / agent / transfer / sync / browse / license / rbac）。
- **交付产物已重建**：`dist/agent`（36.3＋35.9 MiB）、`dist/win-x64`（50.6）、`dist/win-x64-service`（50.6＋35.3）、
  `dist/agent-setup`（61.0）、`dist/installer`（74.7），四脚本 `PORTABLE=0 INSTALLER=0`；
  前端真重嵌由 `bash .tools/verify-dist.sh <exe绝对路径>` 校验（`index-BZ4Ty4Ew.css` / `index-D5HYp8R1.js` 一致）。

### 28.8 本轮踩到的坑 / 判断

1. **`Apply(null)` 不能表达"关"**（老坑，本轮又验证了一次）：`null` 的语义是
   "还没有配置 ⇒ 用默认值"，而默认值是**开着**的。要"关"只能用 `Stop(原因)` 或"已连接 + `Enabled=false`"。
   而 `Stop` 的场景本轮**收窄**成只剩"配置层面明确说不要听"。
2. ⚠️ **G 段必须看"8090 有没有被别人占着"**（`udp_port_owner`）。
   开发机上很可能装着一个客户端服务（默认也听 8090）：只看端口号会把**它的**监听算成我们的
   ⇒ 一条**假 PASS**；而我们的绑定真失败时，那条红又看不出原因。
   所以先问一句"开工前是谁占着"，占着就 **SKIP** 而不是硬判。
3. ⚠️ **G 段/`sync` 场景都要 `--Agent:Tasks:ExecuteUdpCommands=false`**。
   需求① 之后"离线客户机**一定**在听 8090"，而冒烟里会真的发一条明文 `DOWN` ——
   少了那个开关，跑冒烟的这台机器会被**真的关掉**。已给 `sync-e2e.py` 的两处启动补上这个保险。
4. **日期**：本轮所有新注释统一写 `2026-10-14`（当天第二轮），不要写 10-15 —— 首版写错了，已全量改回。

### 28.9 本轮**没做** / 待真机 / 挂账

- ⚠️ **托盘只能真机看**：菜单项、双击默认项、`taskkill` 优雅停机这三件事需要真机（或至少有人桌面的会话）确认。
- ⚠️ **被吊销 + 未连接**那条组合路径只有**单测级别**的覆盖（`Apply(config, false)`）；
  "真去吊销一台客户机、看它是否仍然听 UDP"需要真机 + 中心配合。
- 🔸 上一轮挂账未动：Session 0 取 GPU 型号、装上驱动后是否真读到温度、MSI 复测、Linux/Docker、100 台压测、
  `AllowedCidrs` 无消费者、import 前无 `.bak`、`DataDir` 拼错静默回退、PBKDF2。
- 🔸 前端控制台那条 `No client method with the name 'connected' found.` 仍未清（不影响功能）。


---

## 29. 🔥 2026-10-14 M4.11 修复：移除客户机之后**加不回来**

> 用户原话：**"在服务端的客户端列表中移除某个客户机之后，无法再次连接添加该客户机。"**

### 29.0 一句话

**两个洞，缺一不可**：服务端不告诉客户端"我把你删了"（僵尸长连接），客户端也不知道
自己手里的钥匙已经作废（拿废钥匙无限重试）。**连重启客户端都救不回来** ——
本地凭据还在，重启后照样 401。现场唯一的绕法是去那台机器上删掉本地状态目录。

### 29.1 两个洞（都是"查不到 ⇒ 当成正常"这个形状）

| # | 位置 | 旧行为 | 后果 |
|---|---|---|---|
| ① | `AgentHub.Heartbeat` | `_registry.Hosts.FirstOrDefault(h => h.Id == id)?.IsRevoked == true` ⇒ 查不到时 `?.` 短路成 `false` ⇒ 走 `else` 分支照常 `MarkSeen` 并回 ack | 服务端早就忘了它，客户端却**以为自己还在线**；长连接变成僵尸，**永远不会重新注册** |
| ② | `AgentRuntime.EnsureRegisteredAsync` | 只要"本地凭据里的 `ServerUrl` + `MachineKey` 与当前一致"就直接 `return cached` | 记录被删后那把 `agentKey` 必然 401，而客户端把 401 当普通连接失败 ⇒ **拿着废钥匙无限重试**（退避到 60 秒封顶） |

⚠️ 洞①还有个副作用：`MarkSeen` 内部是 `GetOrAdd(id)`，会给一个**已经不存在的 Id**
建一份内存态 —— 也就是"服务端替一台已经被删掉的机器记心跳"。

### 29.2 服务端改法：查不到 ⇒ **强制断开**

`AgentHub.Heartbeat` 与 `RequireAgent` 都改成先解析再分流：

```
host is null          → Context.Abort()  + Warn 日志（"心跳来自一台已被移除的客户机"）
host.IsRevoked        → Context.Abort()  + Warn 日志（原有行为）
否则                  → MarkSeen + 取配置版本
```

`RequireAgent` 那一处也要改，理由是**上报比心跳更密**（遥测 / 配置上行是秒级）：
不改的话，在"被移除"到"下一次心跳把它掐掉"之间那几秒里，中心会替一台**已经不存在的机器**
写数据（那些行永远不会被读到）。

**凭什么说"快照里查不到"就等于"已被移除"**：
- 快照由 `AgentHostRegistry.RefreshAsync` 从库里装载 —— **启动时一次**（`AgentPresenceHostedService.ExecuteAsync` 开头）+ **每 60 秒一次**；
- 刷新**失败时保留旧快照**（不会变空 ⇒ 不会误伤全网机器）；
- 注册成功那条路径会立刻 `RefreshAsync()` + `ApplyChange(host)` 补进去。

⇒ **"库里有的机器不在快照里"只可能是刚被删掉的那台。**

### 29.3 客户端改法：拿一次普通 HTTP 请求去问，而不是猜异常

`AgentRegistrar.IsCredentialRejectedAsync(serverUrl, agentId, agentKey, ct)`：

- 请求：`GET {serverUrl}/api/agents/{id}/config`，带 `X-Agent-Key`（就是客户端本来就在用的配置拉取路径）；
- **只有 401 / 403 返回 true**；连不通、超时、500、404 一律 false（＝"还轮不到作废凭据"）。

**为什么不看 `HubConnection.StartAsync` 抛出的异常**：那要先假设 SignalR 把 401 放进了
`HttpRequestException.StatusCode` —— 不同版本、不同传输（WebSocket / 长轮询）的包装方式并不一致，
一旦不成立，修复就会**静默失效**（测试红得莫名其妙）。改成"问一句"之后，
判定与 SignalR 的内部实现彻底解耦；代价只是"连接已经失败之后"多一个请求。

命中之后 `AgentRuntime.RunConnectedAsync` 做四件事：
`_identity.Clear()` + `_cachedCredentials = null` + `_transfers?.ClearSession()`（那把钥匙在飞的传输也废了）
+ 状态文案，然后 `return false` 让主循环下一轮走"发现 → 注册"。

**⚠️ 刻意不设 `_revokedUntil`**：这与"被管理员吊销"那条路**不一样** ——
吊销要等 5 分钟（那是个要人工介入的状态），而"移除之后再把它加回来"是运维的**正常动作**，
让现场等 5 分钟毫无道理。

### 29.4 与"吊销"的关系（用户的决策 A）

决策（`AskUserQuestion`）：**被吊销的客户机也一样自动回来**，不额外做区分。

因此出现一处**不对称**，必须知道：

| 场景 | 现在 |
|---|---|
| 在线时被**吊销** | 服务端推 `Revoked` ⇒ 客户端清身份 + `_revokedUntil` 5 分钟 ⇒ 5 分钟后重新注册**复活** |
| 离线时被**吊销** | 重连 401 ⇒ 探测确认 ⇒ **立刻**清身份 + 重注册 ⇒ 复活（**没有 5 分钟**） |
| 被**移除** | 同上 ⇒ 立刻复活（**新 Id**） |

⇒ **"吊销"因此变得更弱了**。但这不是新增的性质：`AgentEnrollmentService` 早就写明
"已吊销的机器重新注册会复活……要真正下线一台机器，去那台机器上卸载，而不是靠吊销"
（免凭据准入的直接后果）。真要"永久封禁"，得换一套真凭据，那是另一件事。

🔸 另一处要知道的连带效果：被移除的机器重新注册后，**本机那份配置会随双向同步推回中心**
（权威 = 双向·后写覆盖）。即"移除"不会清掉那台机器上的配置 —— 想要干净重来，
得在移除前先把它的配置清空。

### 29.5 验证（M4.11）

**冒烟 H 段「移除之后加得回来」**（`agent-smoke.py`，8 条断言）：

```
[PASS] 客户机上线（移除之前）
[PASS] 移除返回成功 —— HTTP 200
[PASS] 移除后它从在线表里消失
[PASS] 移除后客户机自动重新注册回来（≤150s）
[PASS]   回来的还是同一台机器（机器码一致）
[PASS]   是**新记录**而不是旧记录被复活（Id 变了） —— Id 2 → 3
[PASS]   客户机日志里出现了第二次「注册成功」 —— ['注册成功：Id=2…', '注册成功：Id=3…']
[PASS]   日志里说明了原因是「凭据被中心作废」
```

`--only agent`：**167 PASS / 0 FAIL**（上轮 159；H 段 8 条）。

**H 段的四条设计纪律**（都是"怎么才不假绿"）：
1. **判据用列表里的 `state`（online/unstable），不用详情里的 `connected`** ——
   列表给的是"由最后心跳算出的在线态"，而"重新注册之后还能不能上线"要的正是后者；
2. **Id 必须变**：硬删之后重新注册必然新插一行。不变就说明"回来的"是旧记录被复活；
3. **日志里必须出现第二次「注册成功」** —— 只断言"列表里又有了"是不够的，
   那也可能是**别的**机器补上来的；
4. **必须说出原因**（"凭据被作废"）—— 否则只看到"它自己回来了"，不知道为什么。

**变异测试（两半各自都是必需的）**：

| 变异 | 结果 |
|---|---|
| A：关掉服务端那两处 `Abort`（`if (false && host is null)`），客户端修复保留 | **H 段 FAIL** —— 机器没回来（客户端根本不知道） |
| B：关掉客户端那处清身份（`if (false && await …)`），服务端 Abort 保留 | **H 段 FAIL** —— 机器没回来（拿着废钥匙一直重试） |

### 29.6 本轮踩到的坑

1. 🔥 **复现时"证据被测试自己的清理删掉了"**：第一版 H 段照抄 D 段的 `finally`，
   `shutil.rmtree(state_dir)` 把客户机的 NDJSON 日志一并删了 —— 于是**
   脚本说"没回来"，而"为什么没回来"的那几行已经不存在了**。
   改成**失败时保留状态目录**，并把日志路径打印出来。
   （与 `client_messages` 那条"必须在客户端活着时读"的注释是同一个教训的两个侧面。）
2. **`HttpRequestHeaders` 的索引器只有 getter**：`request.Headers[name] = value` **编译不过**（CS0021），
   要用 `TryAddWithoutValidation`。
3. **变异测试会带出 CS0162**（`false && …` 之后的代码不可达）—— 那是预期的，不是修复引入的问题。
4. **`run-all.py` 打的是 `dist/` 产物**，而 `dist/` 在你改完代码后是**旧的**：
   直接 `--only agent` 会拿上次打包的客户端去验这次的代码。
   开发期把被测服务端指到 `.artifacts-check/bin/ControlServer.Server/debug/`，
   `_resolve_agent_exe` 会按"同源"自动挑同一批构建的客户机。

### 29.7 本轮**没做** / 挂账

- 🔸 服务端**不主动推**"你已被移除"这条消息：靠 Abort + 401 已经够，且对**旧客户端**同样有效
  （旧客户端会断开重连，只是它会继续拿废钥匙重试 —— 与改之前一样，没有变坏）。
- 🔸 真要"永久封禁某台机器"，当前机制做不到（吊销/移除都会被自动复活）。这需要真凭据，属新需求。
- 🔸 其余待真机项同 §28.9。
\n

---

## 30. 🔥 2026-10-15 M4.12：五项需求（本地页改中心·拉黑·主机操作·清单 BUG·组合按钮）

> 需求原话（用户一次给齐）：
> ① 本机设置页可改所连服务端的 IP/端口（已连接时要先向服务端上报操作再保存）；
> ② 取消吊销、改为拉黑（拉黑后不连接；取消后立即恢复）；
> ③ 主机列表不显示版本、取消「重启服务」，新增唤醒/关机/重启；
> ④ 客户机不上报硬件清单的 BUG（疑似重新注册后出现）；
> ⑤ 新增组合按钮（多个主按钮组合 + 段间延迟 + 触发执行）。

### 30.0 四个语义决策（`AskUserQuestion`）

| # | 问题 | 用户选择 |
|---|---|---|
| ① | 「先上报再保存」做到什么程度 | **只通知、不阻塞保存**（上报失败仅记日志） |
| ② | 「拉黑」的准确含义 | **拒绝连接 + 客户端保留身份继续重试**；「移除」保留 |
| ③ | 唤醒的多网卡策略 | **全部网卡 MAC + 两个广播地址** |
| ⑤ | 组合按钮的形态 | **第三类瓦片 + 支持对接码** |

### 30.1 需求④：真 BUG —— 硬件清单在**重新注册之后**不再上报

**根因（一行）**：清单排期由客户端**本地持久化**的"上次上报时刻"（`TelemetryStateStore.InventoryReportedAt`）决定，
周期默认 **6 小时**；而"服务端这边还有没有这份清单"客户端**不知道** ——
记录被移除 / 重新注册换了新 Id 之后服务端是白纸，客户端却以为"6 小时前刚报过"。

**修法（服务端说，客户端听）**：

- `AgentWelcomeDto` 新增 `bool NeedsInventory = false`（**带默认值** ⇒ 老服务端不带这个字段时，
  新客户端行为与改之前**完全一致**，不会多报）；
- `AgentHub.OnConnectedAsync` 用 `AgentTelemetryService.HasInventoryAsync(id)` 算出来**随 Welcome 下发**
  （一次连接查一次；查库失败时**按"已有"处理** —— 宁可漏一次补报，也不要在库抖一下时让全网机器一起重采 WMI）；
- 客户端 `OnWelcome` 置 `_inventoryRequested`，上报循环里 `_inventoryRequested || now >= nextInventory`
  ⇒ **忽略 6 小时排期立刻补报**；**只有上报成功才清零**（失败仍走 5 分钟重试）。

⚠️ 为什么不让客户端"每次注册都重报一遍"：那会让**每一次**服务重启 / 网络抖动的重连都去跑 WMI 采清单
（1～2 秒 CPU），正是当初要避免的代价。

### 30.2 需求②：吊销 → 拉黑（一条真围栏）

| | 旧「吊销」 | 新「拉黑」 |
|---|---|---|
| 服务端标记 | `IsRevoked` | `IsBlocked`（迁移 `M4_12_AgentBlock` 只做列重命名） |
| **agentKey** | **清成 null**（凭据立即失效） | **保留**（认证时按标记拒绝） |
| **重新注册** | **会复活**（`UpdateRegistrationAsync` 里有 `IsRevoked = false`） | **被拒**（`RegisterAsync` 返回 `status=blocked`） |
| 客户端收到后 | 清身份 + 清配置 + 停任务 + 5 分钟退避 | **什么都不清** + 15 秒轮询 |
| 取消之后 | 要重新注册（旧密钥已没了） | **下一次重试就连上**（密钥一直有效） |

**两处关键实现**：

1. **`EfAgentHostStore.SetBlockedAsync` 刻意不动 `AgentKeyHash`** —— 这是"取消后立即恢复"的全部前提。
   安全性没有损失：`AgentKeyAuthenticationHandler` 是每一类请求的必经之路，看到 `IsBlocked` 一律 Fail。
2. **`AgentEnrollmentService.RegisterAsync` 在注册入口就拒**（按 machineKey 判，兜住"客户机把本地状态删了换个身份来注册"）。

**客户端的两处行为改动**：

- **连接被拒（401）之后不再"无脑清身份"，改成"直接去注册一次，按结果分流"**
  （`TryRecoverIdentityAsync`）：注册成功 ⇒ 记录被移除过，换新钥匙（**新 Id**）；
  返回 `blocked` ⇒ **身份、配置、任务一律不动**，进"被拉黑"状态按 15 秒轮询。
  ⚠️ 这也修掉了"先清身份再判定"那个隐患 —— 那样"取消拉黑"就得多走一次注册，而注册在被拉黑期间同样被拒。
- `OnBlocked`（原先的 `OnRevoked`）不再 `_tasks.Stop()` / `_configs.Clear()` / 重置 `_reportedConfigVersion`：
  一旦清了配置与版本水位，**重新连上时版本号会对齐到 0** ⇒ Welcome 带回的版本与本地一致
  ⇒ **配置不会被重新应用** ⇒ 任务停在那儿再也起不来。（旧吊销靠"重新注册拿新 Id、服务端版本从 0 开始"
  绕过了这个问题；拉黑不重新注册，那条路走不通。）
- 退避拆成两档：**`BlockedRetry = 15 秒`**（要"取消后立即恢复"，且全程**只刷状态不写日志**）、
  **`EnrollmentFullRetry = 5 分钟`**（在线表已满 —— 这才是必须人工介入的那个）。旧代码把两者挤在同一个 `_revokedUntil` 上。

**⚠️ 一处不对称（要知道）**：拉黑**不影响 UDP 指令通道** —— 被拉黑的机器按"未连接"处理，
照旧监听（M4.10 需求①的规则）。理由同吊销：被拉黑的机器往往正是那台需要被现场工具立刻关掉的机器。

**旧客户端降级**：服务端不再发 `Revoked`、改发 `Blocked`。不认识这个方法的客户端会安静忽略，
多挂最多一个心跳周期（≤10 秒后被服务端 `Context.Abort()` 掐掉）—— 没有变坏。

### 30.3 需求③：主机列表操作改版 + 网络唤醒

- **去掉**：版本列（表格少一列，`colspan` 7→6）、「重启服务」按钮（连同前端 `api.rebootAgent`）。
  ⚠️ 服务端 `POST /api/agents/{id}/reboot` **保留**（"Agent 进程卡住"时的排障手段），只是没有 UI 入口。
- **新增**：`唤醒` / `关机` / `重启`。
  - 关机 / 重启走**既有的** `POST /api/agents/{id}/power`（SignalR 指令，要求在线）——
    这个端点早就有了，只是前端一直没人调。两者都**不可撤销**，所以走两段式确认：
    `confirmDeleteId` 泛化成 `armed`（`${动作}:${id}`），三种危险操作共用同一道闸。
  - **唤醒**是全新的：`POST /api/agents/{id}/wake` + `AgentWakeService`。

**唤醒为什么在服务端做**：目标机器是**关着的** —— 它连不上中心、也收不到任何指令，
只能由服务端朝局域网发以太网广播帧，让网卡硬件在"主机关机、网卡待机供电"时认出来。

**多网卡怎么处理**（用户点名要care的）：

- 对**每一块上报过的网卡**各发一遍（不同网卡的 MAC 不同 ⇒ 魔术包内容不同）；
- 每块网卡发往**两个地址**：`255.255.255.255`（本地广播）与该机最后来源 IP 的**定向广播**
  （`192.168.1.37` ⇒ `192.168.1.255`，跨网段时唯一能到的一条）；
- 端口 **9 与 7 都发**（9 是默认，7 是老工具习惯；每包 102 字节，成本可忽略）。
- ⚠️ "最后一节换 255"是**对 /24 的假定**（清单里没有掩码）—— 已在注释里写明，不假装支持 /16。
- ⚠️ **MAC 来自注册时上报的主机信息（`HostInfoJson`），不在 6 小时一次的硬件清单里**
  （清单 DTO 里压根没有网卡字段 —— 找了一圈才确认）。客户机每次重启都会重新注册，所以最迟在那之后跟上。
- ⚠️ 返回 `ok` 只代表**包发出去了**：WoL 无应答，这句话写在响应 message 里，免得被读成"已开机"。

### 30.4 需求①：本机页改「所连接的服务端地址」

- **存储**：`AgentServerOverride`（状态目录下 `server-override.json`，**独立文件**）。
  为什么不回写 `appsettings.json`：程序目录通常**不可写**（Program Files + 普通用户）；
  而且回写会让"安装时写的"与"运行期改的"混在一起、升级覆盖安装就丢。
  删掉该文件 = 立刻回到"按安装配置 / 广播发现"。
- **优先级**：`本机覆盖 > Agent:ServerUrl > 凭据里记住的地址 > UDP 广播发现`。
  覆盖必须排在最前 —— "中心搬走了、旧地址连不上"恰恰是最需要改地址的时刻，
  排不过 appsettings 的话这个功能等于没有。
- **生效路径**：页面 `PUT /api/server`（令牌闸门 ③）→ 落盘 + 置"改过"信号 →
  **心跳循环窥见信号立刻断开**（否则要等这条长连接自然断掉，可能是几小时）→
  主循环消费信号：**先尽力上报旧中心** → 作废本地身份 → 按新地址重新注册。
- **上报**：`POST /api/agents/{id}/server-change`（新策略 `AgentSelfReport`，机器身份 + 只能报自己名下）。
  ⚠️ 它**刻意不改任何服务端状态**（不摘记录、不标离线）：需求口径是"只通知，不阻塞保存"。
  它的价值是让日志里留下"这台机器是**主动搬走**的"，而不是"忽然掉线了"。
  ⚠️ 上报失败**不阻塞**：旧中心不可达时正是最需要改地址的时刻。

### 30.5 需求⑤：组合按钮 —— **已于同日落地，见 §31**

> 本轮（§30）当时只定了口径；下一条 §31 是它的落地记录，这里保留当时的决策以免丢失上下文。

设计已定（用户已选"第三类瓦片 + 支持对接码"）：`ComboButton` 与主按钮 / 子按钮集同级、共用全局 `Order`；
成员只引用**主按钮**；相邻两个成员之间可设延迟（毫秒，第一个之前不等）；顺序可上移/下移；
触发＝按顺序执行成员的全部动作并逐段等待；**支持对接码**（收到该码执行整个组合）。
落地范围：Core 模型 + 清洗校验 + 配置 API + 执行服务 + 对接码三级匹配 + 前端编辑器与控制台瓦片 + 测试。

### 30.6 验证（M4.12）

| 项 | 结果 |
|---|---|
| `dotnet build` | **0 警告 0 错误** |
| `dotnet test` | Core **796** / Agent **104** / 集成 **386**（+1）= 失败 **0**（加 §31 的组合按钮后为 Core 807 / 集成 389） |
| `npm run typecheck` | 通过 |
| `npm run build` | 通过（`index-DK1ctOzb.css` / `index-BVX0M3uo.js`） |
| 内嵌本机页 JS（`node --check`） | **SYNTAX OK**（22625 字符） |
| EF 迁移 | `M4_12_AgentBlock`（**只做列重命名**）；迁移数守门用例 12→13 |
| 冒烟 `--only agent`（debug 产物） | **通过 1/1 场景**（含 H 段「移除后加得回来」、B 段「拉黑」） |
| **全量冒烟 `run-all.py`**（打的是本轮重建的 `dist/`） | **9/9 场景通过** |
| 交付产物 | 五套全部重建（`PORTABLE=0 AGENTINSTALLER=0 INSTALLER=0`）；agent-setup `90C8EE68…A87CB`、installer `F913455C…F2E87`；`verify-dist.sh` 校验前端一致 |

**新增/改写的守门用例**：
- `AgentsApiTests.Block_marks_the_host_and_unblock_clears_it`
- `AgentsApiTests.A_blocked_host_cannot_register_again`（**核心**：被拉黑连注册都拒；取消后能重新进门）
- `AgentsApiTests.A_removed_host_comes_back_as_a_new_record`（**取代**原先那条"吊销会复活"——
  那条钉的是旧语义，现在反过来：移除才是"会回来"的那一个）
- `AgentProtocolAndPresenceTests` 的状态中文名：`revoked`/已吊销 → `blocked`/已拉黑

### 30.7 本轮踩到的坑

1. **批量重命名误伤了另一个实体**：`RevokedAt` 同时存在于在线表与**已废弃的令牌表**
   （`EnrollmentTokenEntity`）⇒ 我的 `replace_all` 把令牌表那个也改了。
   更糟的是我顺手给在线表加了 `row.BlockedAt = …`，而**在线表根本没有这一列**（CS1061 才暴露）。
   教训：批量重命名前先 `grep -c` 数一数命中，**并且确认命中的是哪个类型**。
2. **迁移文件被误改**：排除模式写的是 `'Migrations/'`，而 Windows 上是 `src\…\Migrations\…`
   ⇒ 25 个历史迁移文件被改。历史迁移**必须原样**（它是已应用的记录），已全部还原，
   只让 `dotnet ef migrations add` 重新生成快照。
3. **`register` 返回 500 给一条业务拒绝**：`StatusCodes` 映射表里没有 `blocked` ⇒ 落到默认的 500。
   现场看到"服务端坏了"，而原因就写在响应体里。已改成 **409**。
4. **测试辅助方法内部自带断言**：`AgentsApiTests.RegisterAsync` 里断言"必须 200"，
   于是"预期被拒"的用例必红 —— 是**用例写法**的问题，不是产品的。加了一个 `RegisterRawAsync`。
5. **属性名遮蔽 `System.IO.Path`**：`AgentServerOverride.Path` 让类里所有 `Path.Combine` 编译不过（CS1061）
   ⇒ 改名 `FilePath`。
6. **`run-all.py` 打的是 `dist/` 产物**（旧代码）—— 开发期要显式把被测服务端指到 `.artifacts-check`（见 §29.6）。

---

## 31. 🔥 2026-10-15 M4.13：组合按钮（需求⑤，本轮补上）

> 上一轮（§30）里需求⑤ 只定了口径、一行代码都没写。这一轮把它落地。

### 31.0 口径（用户已确认）

新增**第三类瓦片** `ComboButton`：与主按钮 / 子按钮集同级、**共用全局 `Order`**；
成员**只引用主按钮**（不含子按钮集）；**相邻两个成员之间**可设延迟（毫秒，第一个之前不等）；
顺序可上移 / 下移；触发＝按顺序执行成员的全部动作并逐段等待；**支持对接码**。

### 31.1 延迟的语义（最容易理解错的一处，所以写在类型上）

`ComboButtonStep.DelayMs` ＝ **「执行完本步之后、下一步之前」等待的毫秒数**。三个直接推论：

| 推论 | 落点 |
|---|---|
| **第一步的延迟永远是 0**（前面没东西可等） | 界面上第一步不给延迟框；程序里也不读它 |
| **最后一步的延迟永远不会生效** | 清洗器**强制归零**（`NormalizeComboButton`）＋解析时再归零（`ComboResolver`）＋界面**不显示**这一框 |
| 与成员自己动作的 `DelayMs`（前置延迟）**不是一回事** | 两者各算各的：成员内部照旧「第 1 条不等待」，段间延迟在上一段**整个执行完**之后才开始算 |

⚠️ 上限 **60 秒**（`ComboButtonStep.MaxDelayMs`）：组合跑在「按钮点击 / 对接码」这条**有人在场**的链路上，
一个手滑写成 10 分钟会让触发它的人以为「点了没反应」。要等那么久，该用定时器。

### 31.2 四层落地（缺一层这个功能就是半成品）

| 层 | 位置 | 关键决定 |
|---|---|---|
| **模型** | `Core/Models/ComboButton.cs` ＋ `ControlSettings.ComboButtons` | 成员是**引用**（`buttonId`）而不是动作副本 —— 按钮改了，组合下次触发就是新行为 |
| **清洗** | `ControlSettingsNormalizer.NormalizeComboButton` / `SortAndReindex` | 悬空成员**剔除并记说明**；三类瓦片**共用一条 Order 空间**（同级次序：按钮 0 → 组合 1 → 集 2） |
| **解析** | `Core/Services/ComboResolver.cs`（纯函数） | 执行前再算一遍「谁还在」，产出 `ComboPlan`（段 ＋ 被跳过的成员） |
| **执行** | `ActionExecutionService.ExecuteComboAsync` | **逐段复用 `ExecuteAsync`**，不把成员动作拍平 |

**为什么执行不拍平**：① 每个成员要有自己的 runId / 日志（排障时「哪个按钮出的问题」要一眼可见）；
② 成员失败**不中断**后续成员（与单按钮同口径，复用现成执行器天然如此）；
③ 段间延迟与成员内部的前置延迟互不干扰。

**「没有动作的成员」照样占一段**（记为 `skipped`）—— 少一段会让「3 个成员只回 2 段」没法解释。

### 31.3 对接码：与主按钮**同级**、按 Order 合并扫描

`PairingMatchKind` 新增 `Combo`。**扫描次序**（`PairingMatchService.Match`）：

1. **瓦片级**：主按钮与组合按钮**合并成一个按 `Order` 排序的序列**再逐个比对 ——
   谁在界面上靠前，谁先命中。⚠️ **不是**「先扫完所有按钮、再扫组合」；
2. 子按钮级；
3. 动作级（**组合不参与动作级**：它的「动作」是别人的按钮）。

⚠️ 因此**枚举数值不再等于优先级**（以前是）。旧注释里那句「数值顺序即优先级顺序」已改写 ——
要判断先后请看 `Match`。这条很容易被下一个改这块的人按老注释理解错。

### 31.4 前端

- `types.ts`：`ComboButton` / `ComboButtonStep` / `ComboExecutionDto`；
  `mergeTiles` 把三类瓦片按同一套 `TILE_KIND_RANK`（按钮 0 / 组合 1 / 集 2）合并 ——
  **必须与清洗器逐一对应**，否则会出现「界面顺序与保存后的顺序对不上」；
- `ComboForm.vue`：成员下拉（只列主按钮）＋ 上移/下移/删除 ＋ 段间延迟；
  **最后一步不显示延迟框**（填了也没用）；指向已删按钮的成员显示成「（按钮已不存在）」；
- `ButtonGrid.vue`：三类瓦片共用一套外观算法（`looks(t)` 挑字段），组合瓦片的角标是暖色的「组」；
- `App.vue`：`runCombo` → `api.executeCombo` → **过一遍 `flattenComboExecution`** 再喂给结果面板
  （把成员名前置到动作名上，信息不丢；面板不必学两套形状）。

### 31.5 验证

- 构建 **0 警告 0 错误**；`dotnet test`：Core **808** / Agent **104** / 集成 **389** = 失败 **0**；
- **全量冒烟 `run-all.py`（9 场景）在重建后的 `dist/` 产物上 9/9 通过**；交付产物五套全部重建（`PORTABLE=0 AGENTINSTALLER=0 INSTALLER=0`，agent-setup `F0669360…1A5A77`、installer `0E1BE347…AB8D0D`）；
- 冒烟 `pairing`（**真 UDP 回环**）：**5/5 通过**。其中新增的
  `⑤ 组合按钮级命中（按成员顺序执行两个按钮，段间延迟真的生效）` **量了间隔 ≥100ms**（配置 150ms）——
  只断言「收到 2 条」是分不出「带延迟」与「几乎同时发」的，所以必须计时；
- 守门用例：
  - `ComboButtonTests`（纯逻辑 8 条）：悬空成员剔除＋记说明、最后一步延迟归零、延迟上下限、
    **重复引用同一按钮要保留**、三类瓦片共用 Order 空间、解析顺序／跳过、执行时段数与失败不中断；
  - `PairingDispatchServiceTests` ＋3：组合按序带延迟、悬空成员不影响其余、
    **组合 order 更小就先命中**（守卫「同级」语义）；
  - `ActionsApiTests` ＋3：`comboId` 执行、`comboId` ＋ `buttonId` ⇒ **400**、未知 `comboId` ⇒ 404。

### 31.6 本轮踩到的坑

1. 🔥 **C# 字符串里嵌了 ASCII 双引号**（在中文说明里写「…`"一串按钮"`…」）⇒ 编译器直接语法错误。
   本轮**连续踩了三次**（`ActionsController` / `ComboButtonTests` ×2）——
   中文注释里的引号一律用 `「」`。
2. **`LogService` 是 `IAsyncDisposable`**：写 `using var log = new LogService(...)` 会 `CS8418`
   ⇒ 测试里必须 `await using`（顺带把 `TempDataDirectory` 用 `using` 单独兜住）。
3. 🔥 **断言前先问「这条链路上谁先动手」**：派发测试里 `Harness.SeedConfig` 写的是**原始 JSON**，
   而 `ControlSettingsService` 读出来时会跑清洗 ⇒ 悬空成员在**载入时**就没了，
   于是「执行期跳过悬空成员」那条断言永远看不到 `SkippedButtonIds`。
   ⇒ 改成断言「清洗那一步负责剔除」，执行期那条路由**纯逻辑用例**（直接调 `ComboResolver`）覆盖。
4. 🔥 **测试抓到一个真缺陷（差点发出去）**：`ConfigBackupService.KnownSettingsFields`
   原先是**手写的一张配置字段清单**。加了 `comboButtons` 之后没人想起同步它 ⇒
   导入一份含组合按钮的配置时，界面会告诉用户「**已忽略字段：comboButtons**」——
   数据其实没丢（反序列化走的是模型），但那句话会让人以为组合按钮全丢了，
   而且真正该提醒的单机版字段会被这句话淹掉。
   ⇒ 已把该清单改成**从 `ControlSettings` 反射生成**（"加了字段却忘了登记"这类 bug 从根上消失），
   并补了一条"组合按钮导出再导入不丢"的用例。
   **教训：手写的"已知字段清单"就是一份平行的真相，它一定会漂。**
5. **断言写成了自相矛盾**：先写「三类瓦片的 Order 都是 0」，又写「它们共用同一条编号空间」——
   这两条不可能同时成立（共用编号 ⇒ 重编号后互不相同）。是**测试自己的错**，不是实现的。

### 31.7 没做 / 挂账

- 🔸 组合**不带定时器与月度计划**（要定时就给它引用的那个按钮加）—— 刻意如此，已写在类型注释里。
- 🔸 组合成员**不能选子按钮集**（用户明确要求）。将来若要支持，得先定义「子按钮的 IP / 端口覆盖」
  在组合语境下的含义，那是一个独立决策。
- 🔸 真机项同 §30／§29（MSI 复测、Linux/Docker、温度驱动、100 台压测、Session 0 取 GPU）。

## 32. 🔥 2026-10-15 M4.14：本机页「服务端」明细**默认收起**（只留"已连接中心 + 地址"）

> 用户原话：「新增客户端 服务端信息默认为收起状态 仅显示最上方的已连接中心及地址」。
> 落点只有一处：`agents/ControlServer.Agent/Config/local-ui.html`（**内嵌资源，零 C# 改动**）。

### 32.0 为什么是"默认收起"

「服务端」卡片原有十行（地址 / 名称 / ID / 最后通信 / 服务端版本 / 配置版本 / 来源 / 写入时间 /
同步状态 / 客户端版本）。日常要看的只有一句：**连没连上、连的是哪个地址**；
剩下九行是排障时才翻的 —— 它们一进来就摊开，会把下面真正的正文（任务配置）顶到屏幕外。

### 32.1 结构：谁常显、谁被收起来

| 位置 | 元素 | 说明 |
|---|---|---|
| 摘要行（**常显**） | `#serverBadge` ＋ `#serverAddr` ＋ `#serverStatus` ＋ `#serverToggle` | 徽标 ＋ 地址 ＋ 展开开关 |
| 明细区 `#serverDetail`（**`hidden`，默认收起**） | `#serverInfo`（10 行 `dl.kv`）、`#serverNote`、`#serverTarget` ＋ `#saveServer`、`#serverEditNote` | 排障时才翻 |

CSS：`.row.summary` 自己不带下边框，分隔线交给 `#serverDetail` 的 `border-top` ——
**收起时不该留一条悬空的虚线**（`.row:last-child` 那条规则救不了：`#serverDetail` 还在 DOM 里）。

### 32.2 三条纪律（本轮真正容易写错的地方）

1. 🔥 **收起状态只有一个事实源 `serverOpen`，渲染函数全程不碰 `hidden`。**
   页面会周期性重拉状态（接收记录每 3 秒一次）；若让 `renderServer` 去写 `hidden`，
   用户刚展开的明细会被**下一次刷新按回去**。
   ⇒ 与「输入框有焦点时不让刷新覆盖」是同一条纪律：**渲染只写"值"，不写"用户的意图"**。
   收起状态**不做持久化**（每次打开页面都回到收起）——"默认收起"就是它的规格。
2. 🔥 **收起不能把故障一起收进去。** `statusText` 连上时是「已连接 192.168.1.10:8080」
   （与徽标 ＋ 地址完全重复 ⇒ 收起态不留）；没连上时它正是"为什么"
   （被拉黑 / 在线表已满 / 正在广播找 / 注册被拒）⇒ **必须留在摘要行**。
   `renderServerError` 同理：改写摘要行的徽标 / 地址 / 状态，而不是藏进明细。
3. **没连上时不能拿"要连的地址"冒充"已连的地址"**：`serverUrl` 为空时退而显示
   `serverOverride / configuredServerUrl`，但**必须带「（目标）」后缀**（`—` 为最后的兜底）。

### 32.3 验证

- 内嵌 JS：`node --check` 通过（脚本块抽到 `.tools/local-ui-check.js` 后校验）；
- **端到端**：`agent-smoke.py` G 段新增 **4 条结构断言**（`PageStructure` 用标准库
  `html.parser` 建出"id → 祖先 id"表），证据取自**跑着的客户端真返回的页面** ⇒
  同时证明"新页面真的被嵌进 exe 了"：
  `明细默认收起（serverDetail 带 hidden）` / `摘要行四件都在` /
  `被收进去的就是那份明细` / `收起后该露的仍露着`；
- 单跑 `--only agent`：**172 PASS / 0 FAIL**；全量 `run-all.py`：**9/9**；
- 内嵌核对：未压缩的 `bin/Release/net8.0/ControlServer.Agent.dll` 里
  `serverDetail` ×4 / `serverToggle` ×4 / `serverBadge` ×3（与源码引用数一致）；
- 交付产物重建：`dist/agent`（Service 36.3 MiB ＋ Tray 35.9 MiB）＋
  `dist/agent-setup`（`ControlServer-Agent-Setup-0.1.0.exe` 61.0 MiB，
  SHA256 `60F08AEE…FF27`），并已补投到 `%ProgramData%\ControlServer\downloads\`。
  `dist/installer` / `dist/win-x64*` **未重建**（payload 是服务端程序，不含本机页）。

### 32.4 本轮踩到的坑

1. 🔥 **别拿二进制 grep 去验单文件 exe 里的内嵌页面**：`EnableCompressionInSingleFile=true`
   会把程序集压掉 ⇒ `grep -a -c 'serverDetail' dist/agent/*.exe` 恒为 **0**。
   **那是个假 FAIL**。要验就验未压缩的中间程序集（上面那条），或让冒烟从跑着的客户端手里取页面。
2. 🔥 **`check()` 在 PASS 时也照印 `detail`** ⇒ `detail` 必须写**中性证据**（属性列表 / 名单）。
   第一版写成"serverDetail 上没有 hidden —— 明细会一进来就摊开"，日志里就变成
   `[PASS] … —— serverDetail 上没有 hidden…` —— **读成一条假 FAIL**。
   （文件里既有的若干条 detail 也是这个毛病，本轮只改了新增的四条。）
3. 🔥 **"元素不在明细里"这条断言对"元素根本不存在"也会通过** —— `parents` 缺失 = 空列表，
   和"在卡片里"长得一模一样 ⇒ **存在性必须单独断言**。
   变异测试第 ④ 格就是专门钉这个陷阱的。
4. 断言的变异测试（`.tools/check-collapse-assertions.py`）第一版**全绿但什么都没跑**：
   终点标记 `# ── ③` 在文件里出现三次，直接 `index()` 拿到的是前面那个 ⇒ 切片为空。
   ⇒ 起点之后往回找，并加一条"抽出来是空就退出"的守卫。
   **教训：抽代码做测试的工具，自己也要能被证伪。**

### 32.5 没做 / 挂账

- 🔸 收起状态**不做持久化**（每次开页面回到收起）—— 是规格，不是偷懒；要改成"记住上次"是独立决策。
- ⚠️ **点击"详情 / 收起"的行为没有自动化测试**：页面没有 DOM 测试环境，本轮只做到
  `node --check`（语法）＋ 结构断言（谁在谁里面）＋ 断言的变异测试。
  **真机点一下**（展开 → 明确保持展开、等几秒刷新后仍是展开）这条待补。
- 🔸 真机项同 §31.7。

## 33. 🔥 2026-10-15 M4.15 修复：中心页面的「关机 / 重启」**毫无效果**

> 用户原话：「客户端列表右侧的关机和重启功能无法使用（无效果），但远程指令有效」。
> 方案侧见 `docs/M4-…方案.md` §35。

### 33.0 症状与根因（一句话）

`AgentCommandNames.Power` 的**契约写了、实现没写**：
常量注释白纸黑字写着「服务侧执行（服务是 SYSTEM，shutdown 天然有权限）」，
但中心的 `power` 指令到了客户端**没有任何人处理** ——
实现只长在 `UdpCommandListener`（UDP 通道）里，SignalR 那条路是空的。

于是「UDP 明文指令有效、页面按钮无效」—— 用户描述的两半都对上了。

### 33.1 证据链（逐段验过）

| 环节 | 代码 | 结论 |
|---|---|---|
| 前端 | `AgentsPanel.vue:283` → `POST /api/agents/{id}/power {action}` | ✅ |
| 服务端下发 | `AgentsController.cs:932` `SendAsync("Command", new AgentCommandDto("power","shutdown"))` | ✅ 单实参，签名一致 |
| 客户端收 | `AgentConnection.cs:48` `On<AgentCommandDto>("Command")` | ✅ 收到 |
| 主循环分发 | `AgentRuntime.OnCommand` 只处理 `RefreshConfig`，其余转交 | ⚠️ |
| **最终落点** | **`AgentServiceHost.cs` switch 只有 Restart / RefreshConfig** | ❌ **`power` 落 default ⇒ 「收到未知指令（已忽略）」** |

同一个洞还覆盖 `volume`（页面音量按钮走同一条路，同样"无效果"）。

### 33.2 修法：抽一层**共享执行器**

`agents/ControlServer.Agent/Tasks/AgentCommandExecutor.cs`（新）：

- `Power → IHostPowerController`（服务侧）；`Volume → ISessionCommandChannel`（会话侧）；
- 返回 `AgentCommandOutcome(Outcome, Detail)`，**不抛**（异常转成 Failed）；
- `UdpCommandListener` 改为调它（删掉重复实现；UDP 特有的来源 IP / 黑名单 / 留痕留在原处）；
- `AgentRuntime.OnCommand`：`Power`/`Volume` **就地执行并把真实结论写进「接收到的数据」**；
  `Restart` 仍转交宿主（只有宿主能重启自己）。

🔥 **为什么不直接在那边补一个 case**：两条通道两份实现必然漂 ——
今天补电源、明天补音量，迟早只有一边被补上。**执行的真相只能有一份。**

### 33.3 三处语义决策

1. **演练模式是全局闸**（`Agent:Tasks:ExecuteUdpCommands=false`，默认 true）：
   中心的关机指令**也**被它拦下。理由：安全闸不能留漏勺 ——
   现场开它是为了"先观察、确认没有误触发再放行"，若中心的指令能绕过它，它就只是个标签。
   ⚠️ 键名沿用历史（兼容性），语义早已是"本机此刻敢不敢动手"。
2. **留痕写结论，不写"已转交"**：以前写的是"已转交本机指令处理"（Accepted），
   而转交之后**没人处理** —— 那句话正是这次把故障掩盖掉的元凶。
   现在写的是"已执行 / 失败原因 / 演练模式未执行"。
3. **服务端文案**：成功时不再只回"已下发「关机」指令"，而是补上
   "是否真的执行，请看那台机器上的「接收到的数据」"；
   若客户端上报了演练模式，再补一句"⚠ 该机处于演练模式…不会真的执行"。
   （`下发` ≠ `执行`：中心只能保证指令上了路，后面还有平台权限 / 会话侧 / 本机闸。）

### 33.4 验证（实测）

- 新增 `AgentCommandExecutorTests` **12 条**（电源 / 音量 / 演练模式 / 参数非法 / 异常不炸通道）；
  集成测试 `AgentsApiTests` +2（动作非法 ⇒ 400、无长连接 ⇒ 409 且 status=offline）；
- 🔥 **冒烟先红后绿**（最有说服力的那一条）：
  用**旧产物**跑 `agent-smoke.py` ⇒ `[FAIL] 日志里不再出现「收到未知指令」—— ['收到未知指令：power（已忽略）']`；
  重建产物后再跑 ⇒ `[PASS] 指令真的走到了本机执行器… —— []`。
  客户端跑在演练模式 ⇒ **全程没有真的关机**；
- 全量 `run-all.py` **9/9**（576 PASS / 0 FAIL）；
- 交付产物**五套全部重建**（服务端与客户端本轮都改了）。

### 33.5 本轮踩到的坑

1. 🔥 **断言顺序会造成假 PASS**：冒烟里"演练模式已开启"这句话
   UDP 那条路**也会记**。新断言必须排在"真发 UDP 报文"那一段**之前**，
   否则分不出是中心的指令走到了执行器，还是 UDP 的功劳。
2. `CancellationToken` 没给默认值 ⇒ 测试里每个调用都得补一个参数（已改为 `= default`）。
3. `LogService` 在 `ControlServer.Core.Services`（不在 `ControlServer.Agent.Config`）⇒ 新测试文件的 using 要带它。
4. 上一轮（组合按钮）的集成测试留了一条 `CS8604` 警告（`traffic.Select` 少了 `!`）⇒ 本轮顺手清掉，保持 0 警告。

### 33.6 没做 / 挂账

- 🔸 **真关机**没有端到端验证（不敢让冒烟真关一台机器）：验证到"走到了执行器"为止，
  真正的 `shutdown /s` 由平台实现与既有 UDP 路径背书。
- 🔸 中心的音量按钮同样已修，但**会话侧（托盘）不在时**的表现只能真机看。

---

## §34 逐设备曲线：身份从「实例 Key」提升为「物理硬件」（2026-10-15）

### 34.0 症状

现场截图（`多gpu2.png`）：「历史曲线」的硬件选择器里，**显卡
`NVIDIA GeForce GTX 1060 6GB` 6 条同名**、**网卡
`Hyper-V Virtual Ethernet Adapter（vEthernet (Default Switch)）` 6 条同名**，
而磁盘 / 处理器那两组是正常的。

### 34.1 根因（真实库反查，一锤定音）

拿 `C:\ProgramData\ControlServer\control.db` 里 `DESKTOP-1UKRNGI`（AgentId=11）
的 **962 条**遥测反查：

- 这台机器**只有 1 块 GTX 1060**，但窗口内出现过 **6 个 LUID**，**两两零重叠**，
  且每次 LUID 变化都**紧跟一次重启**（uptime 归零 ×4 已捕获）：

  | LUID | 区间 | 帧数 |
  |---|---|---|
  | `0x0001245E` | 10:11:17→10:13:09 | 22 |
  | `0x00012360` | 10:14:09→10:15:02 | 9 |
  | `0x000123B4` | 10:15:52→10:16:12 | 4 |
  | `0x000122FF` | 10:17:09→10:19:39 | 28 |
  | `0x01B697A3` | 11:10:42→11:14:38 | 45 |
  | `0x000123CA` | 11:16:48→12:32:17 | 845 |

- 网卡同理：**5 个不同的 `Id(GUID)`**，名字**全都一样**。

🔥 **`LUID`（GPU）与 `Id`（Hyper-V 虚拟网卡）都是运行时标识符 —— 系统每次启动重新分配。**
原实现取的是"**窗口内出现过的所有 Key 的并集**"当条目 ⇒ 同一块硬件的多个历史身份
被当成多块硬件列出来，一条本该连续的曲线被切成 6 段互不相干的片段。

对照：磁盘（`disk0/1/2`）与 CPU（`cpu0`）的 Key **重启后稳定** ⇒ 那两组本来就对。

### 34.2 修法（纯前端；零契约 / 数据库 / 客户端改动）

新模块 **`web/src/deviceIdentity.ts`**（纯函数，可单独喂数据验证）：

| 判据 | 规则 |
|---|---|
| **归并身份** | `kind` + **展示名**（名字为空时退回 Key） |
| **真的并存** | **同一帧内**同名 ≥2 条才加 `#1/#2`；跨帧各一条＝同一块硬件换了身份，直接归并 |
| **列表来源** | **每个种类取它最后一次出现的那一帧**（至多回看 **3** 帧）—— 既不被采集抖动抹掉整类，也不让拔掉的硬件永远赖着 |
| **取值** | 按时刻挑"当时还活着的那一个 Key"，**绝不把同组的键加起来**（重启前后不会同时有值，相加＝把同一份占用算两遍） |

交互从「**单选 chip**」改成「**勾选多选**」：同一种类勾中几块 → 画在**同一张图**上对比；
颜色认**硬件**、线型认**通道**（磁盘读写 / 网卡上下行＝同色实线 + 虚线）。

配套：`ChartSeries.key` 放宽为 `string` ＋ 新增 `dash`；`MetricsChart` 内部加一层
`raw()` 宽化取值（不把入参类型改成带索引签名的形状——那会波及所有使用方）。

### 34.3 验证（实测）

- **`web/tools/check-device-identity.mjs` 19 PASS / 0 FAIL**，样本是**真实数据**
  （`web/tools/device-identity-real-frames.json`：从上面那台机器的 962 条里，挑出覆盖
  全部身份漂移的 **9 帧**，含 6 个 LUID + 6 个 GUID + 一帧"没有 GPU"的采集抖动）。
  修复前后条数：**17 条 → 7 条**（显卡 6→1、网卡 7→2，磁盘 / CPU 不变）；
- 🔥 **变异测试 3/3 被逮住**（`--verify-mutations`）：把身份退回 Key ⇒ **6 条断言变红**；
  关掉并存编号 ⇒ 4 条；关掉回看 ⇒ 16 条；
- `npm run typecheck` + `npm run build` 通过；服务端交付产物重建。

**界面实测（同日补做；`agent-browser` 真浏览器 + 真实 962 帧，非合成）**

临时 harness 把**真的 `AgentDetailPanel`** 挂起来、只把 `fetch` 换成真实数据（用完即删，
截图存 `.tools/m4.16-ui/`）。数据源 A = 本机真实库那台机器的全部 962 帧；数据源 B = 合成"真的并存"。

| 场景 | DOM 实测断言 | 结果 |
|---|---|---|
| 默认视图 | 选择器 **8 条** = 概览 + CPU 1 + 磁盘 3 + 网卡 **2** + 显卡 **1** | ✅ |
| 取消「整机概览」 | 图 **0** 张 + 出现"勾选上方的硬件即可查看曲线…"指引 | ✅ |
| 只勾 1 块显卡 | **1** 张图 / **1** 条线 | ✅ |
| 勾 2 块磁盘 | **1** 张图 / **4** 条线（读+写 ×2 设备），同色实线 + 虚线 `5 3` | ✅ |
| 勾 2 网卡 + 显卡 | **2** 张图（网卡 4 线、显卡 1 线）—— **按种类各一张** | ✅ |
| 概览 + 硬件共存 | **6** 张图 = 概览 4 + 网卡 1 + 显卡 1 | ✅ |
| 数据源 B（真并存） | `#1`/`#2` 各一条、`keys` 互不重叠，同图 2 条线（`path` 3 段，缺报那帧断开） | ✅ |
| `agent-browser errors` | 空 | ✅ |

**同一份真实窗口下三种口径的条数**（把"回看 3 帧"的作用量化）：

| 口径 | 条数 | 构成 |
|---|---|---|
| 旧：窗口内 `(kind,key)` 并集 | **16** | 显卡 6 + 网卡 6 + 磁盘 3 + CPU 1 |
| 中：`kind` + 名 归并（**无回看**） | 11 | 磁盘 6 + CPU 2 + 网卡 2 + 显卡 1 |
| 新：`kind` + 名 归并 + 回看 3 帧 | **7** | 显卡 1 + 网卡 2 + 磁盘 3 + CPU 1 |

🔥 中间那一行是这次实测**新发现**的一件事：这台机器在 `10:11:22–10:13:09` 的 **21 帧**里报的是
**型号名**（`ST1000DM003-1ER162（D:）` / `12th Gen Intel(R) Core(TM) i7-12700KF`），之后改回
`物理磁盘 0（D:）` / `物理 CPU 0` —— **客户端的命名口径换过一代**（不是重命名，是上报口径变了）。
"回看 3 帧"顺手把这一代也收掉了；否则磁盘 / CPU 也会像显卡一样冒出重复条目。

### 34.4 已知代价（已钉进用例，免得被当成新 bug）

- **重命名一块网卡**后，"改名之前"那一代名字会从列表里消失 —— 但只要 **Key 没变，
  那段数据仍然画得出来**（取值走 Key，不走名字）。要让它真的断，得**改名与重启同时发生**。
- **两块同型号、且都认不出型号**（占位名）的机器，重启后 `GPU 适配器 1/2` 的编号可能对调。
- 真并存的同型号多卡：靠 `#n` 区分，**不合并** —— 合并会把两块卡的读数混成一条。
- **命名口径换代发生在最近 3 帧内**时，会有约 **15 秒**（3 帧）的窗口里同一块硬件出现两条
  （旧名 + 新名），随后旧的那条自动消失。这是 `LATEST_FRAME_LOOKBACK` 的直接推论，
  不是 bug；要彻底消掉它就得回到"拿 Key 当身份"，那代价更大。

### 34.5 没做 / 挂账

- ✅ 页面的**点击 / 勾选行为**已用 `agent-browser`（真 Chromium）实测，见 §34.3 的表。
  仓库里**仍然没有**前端 DOM 测试基建（`package.json` 只有 `dev` / `build` / `typecheck` / `preview`）
  —— 那次 harness 是一次性的、已删；若以后要长期钉住交互，需要先决定引不引 vitest。
- 🔸 `device-identity-real-frames.json` 是**某一次**快照：那台机器重启更多次时会新增 LUID，
  但用例断言的是"归并成 1 条"，不受影响。

---

## 35. 🔥 2026-10-15 M4.17 修复：**「明明在线却提示离线」** + 离线仍尽力下发

> 用户原话：「出现了客户端在线，但在使用关机、重启等功能时提示客户端不在线离线，无法下发指令的提示。
> 需要修复明明在线却提示离线的 BUG；另外即便客户端离线，仍需要向该客户端发送相关指令，但提示离线。」
> 方案文档见 `docs/M4-客户端Agent与局域网管控方案.md` §37。

### 35.1 根因：**"在线"与"能不能下发"是两套判据**

| 用途 | 判据 | 代码位置 |
|---|---|---|
| 页面显示"在线" | **只看最后心跳距今多久**（10 / 30 / 75 s） | `AgentPresence.Classify` ← `AgentHostRegistry.ToDto` |
| 下发指令的闸门 | **只看内存里有没有 SignalR 连接 Id** | `AgentHostRegistry.ConnectionIdOf` ← `AgentsController.SendCommandAsync` |

两者在"**长连接已经断了、但最后心跳还很新**"的窗口里必然矛盾：

1. 🔥 `MarkDisconnected` 把"最后心跳"**钉成断开那一刻**（原意：给离线计时一个确定起点）
   ⇒ **断开后头 30 秒页面照旧是绿的**（`age≈0` ⇒ Online），而 `ConnectionIdOf` 此刻已经是 null；
2. 🔥 **中心一重启**：内存连接表清空，而库里 `LastSeenAt` 可能只落后几秒 ⇒ 页面绿、下发必然 409；
3. 客户机**不使用 SignalR 自动重连**（`AgentConnection` 注释写明），走 `AgentRuntime` 的
   统一退避 **1→2→4→…→60 秒** ⇒ 这个"假在线"窗口能长达几十秒 —— 现场"一点就报离线"就是这么来的。

### 35.2 修法①：判据同源（`bool? longConnection`）

`AgentPresence.Classify` 新增可选参数，语义三态：

- `true` 连接在 —— **时间判定照旧**（僵尸连接仍要变黄/变灰：把"心跳停了"藏在绿点后面是同一个坑的另一半）；
- `false` **已知连接断了 ⇒ 不可能判成 Online**，最多 Unstable（`age > offlineAfter` ⇒ Offline）；
- `null` 不知道 —— 沿用旧行为（防御性）。

`AgentHostRegistry.ToDto` 里**必须写成**
```csharp
longConnection = live is null ? null : live.ConnectionId is not null;
```
⚠️ 不能写 `live?.ConnectionId is not null` —— 那样 `live` 为 null 会得到 `false`（"确定它没连着"），
把"不知道"和"知道它断了"混为一谈，症状是"中心一重启，所有机器集体变黄"（或反过来）。

配套：`RefreshAsync` 装载库里的主机时 `foreach (var host in _hosts) GetOrAdd(host.Id);`
—— 给每台机器建一条"本次运行期"的内存记录（**绝不覆盖已有连接 Id**）。
不做这一步，"中心刚重启"会落到 `null` 上，那一半假在线又回来了。

### 35.3 修法②：离线不再"拒绝下发"，改走 UDP 兜底

`AgentsController.SendCommandAsync` 重构为：

```
有长连接 → SignalR 直推（失败 ⇒ 兜底，不再回 500）
无长连接 → SendWhileUnreachableAsync（UDP 指令通道尽力发出）
```

`SendWhileUnreachableAsync` 的判定顺序（**顺序本身有语义**）：

1. **先判能力**：`UdpCommandProtocol.TryFormat(name, argument, out token)` 失败
   ⇒ 409 `offline` +"「X」只能经长连接下发"（`/reboot` 走这条）；
2. **再判地址**：`_registry.EffectiveAddress(host)` 为空 ⇒ 409 `offline` +"还不知道它的网络地址…上线一次之后离线也能发"；
3. **端口**：`UdpStateOf` 报 `Listening=true && Port` 合法 ⇒ 用**它实际在听的那个**；
   否则退回配置端口并说明是"配置态"；两者都没有（无实时态 + 无配置）⇒ 409 `channeloff`；
4. **发出** ⇒ `200 + status:"udp"`，消息明写"⚠ 该客户端当前离线（长连接已断开），已改走 UDP 尽力发出…UDP 无应答…"。

新增 `UdpCommandProtocol.TryFormat`（`power:shutdown → DOWN` 等六个 token）——
**与 `TryResolveCommand` 成对**，两张表各写一份时"改了一处"没有任何编译错误。

⚠️ 与 `/udp-command` 的**一处刻意不同**：那个端点"客户机自述没在听"⇒ 直接拒（中心不该谎报"已发出"）；
`/power` 的兜底**仍然发**（那份自述是上一次连着时报的、可能已过期），
但把客户机自己那句原因**原样带进返回语**。

### 35.4 顺手补掉的三个洞

1. **拉黑的机器拒发**（409 `blocked`）：UDP 兜底正是"不建连接也能控制"的后门，
   不挡，"拉黑"就成了半拉（被拉黑的机器照样能被关机）；
2. **`/reboot` 无 UDP 兜底**：UDP 只支持关机 / 重启整机 / 音量；
   它必须由 Agent 在长连接上处理 ⇒ 仍 409，但提示语必须指向"只能经长连接下发"这个正确方向；
   ⇒ `Reboot` 改为复用 `SendCommandAsync`（原先是一份重复实现）；
3. **`/config/redeliver` 不兜底**（断线时"去拉配置"没有意义）+ 文案改成"配置不会丢、按版本号自愈"。
   ⚠️ 消息里**必须保留"离线"二字** —— `AgentConfigApiTests` 用 `Message.Contains("离线")` 钉着它。

### 35.5 前端（`web/src/components/AgentsPanel.vue` / `api.ts`）

- 关机 / 重启按钮的 `:disabled` 从 `state !== 'online'` 改为 **`host.isBlocked`**（离线不再禁用）；
- `title` 三态：已拉黑 / 在线 / **"客户端当前不在线：将改走 UDP 指令通道尽力发出，是否生效要看那台机器"**；
- `api.ts` 的 `powerAgent` 文档改写：**`ok:true` ≠ "机器马上会关"**，`status:"udp"` 是兜底发出。

### 35.6 验证

- 单测：`AgentProtocolAndPresenceTests` +8、`UdpCommandProtocolTests` +9（含往返一致）；
  **Core 124 PASS / 0 FAIL**；`AgentHostRegistry` 的两条路径由集成用例覆盖；
- 集成 +4：离线但地址已知 ⇒ **回环 socket 真收到 `DOWN`**、连接断掉后**不再显示在线**、
  **新注册表（＝中心刚重启）装载的主机不显示在线**、拉黑机器拒发；
  全量 **396 PASS / 0 FAIL**；`npm run typecheck` ✅；
- 复现口径：把 `MarkConnected` + `MarkDisconnected` 连起来看 `GET /api/agents` —— **旧代码返回 `online`**。

### 35.7 挂账

- 🔸 客户端短暂断开重连会有**一闪而过的黄点**（诚实但可能被当成"网络在抖"）；
  要压掉得给"连接已断"几秒宽限 —— **本轮不做**，先看现场反馈；
- 🔸 **排队补发不做**（用户明确选"尽力而为"）：真够不着的机器（关机 / 不在网）指令直接作废；
- 🔸 UDP 兜底**无应答**，"到底关没关"仍然只能去机器上看；
- 🔸 交付产物（`dist/`）**本轮未重发** —— `wwwroot` 是编译期快照，要发版得重跑三个打包脚本。

---

## 36. 🔥 2026-10-15 修复：**「从客户机回传到仓库失败」**（M4.18）

> ⚠️ **本节的第二版设计（默认可读整盘 + `OutboxDirectories` 收窄开关）已被 §38 取代**：
> 那个配置键**已经不存在**。本节保留为演进史，**不要照它去配 `OutboxDirectories`**。

用户原话：**「文件传输优化：从客户机回传到仓库失败，需要修复」**。
方案 → `docs/M4-客户端Agent与局域网管控方案.md` §38。

### 36.0 根因（一句话）

**「浏览」能看到的范围（整盘）与「回传」能读取的范围（一个私有目录）是两个集合。**

`TransferBrowseService` 在 M4.7 放开成整盘（顶层 = `DriveInfo.GetDrives()`），
但 `TransferPaths.ReadWhitelist` 的兜底**没跟着改**（仍是 `{状态目录}\outbox`）。
两条代码路径各自维护"本机能碰哪些路径"，只改了其中一处。

**现场证据**（中心服务端日志 `%ProgramData%\ControlServer\logs\log-2026-10-15.ndjson`）：

```
16:44:10  新建回传任务 #5：D:/BaiduNetdiskDownload/unity项目测试.zip ← 1 台客户机
16:44:10  传输失败（任务 #5 / 客户机 11）：源路径不在本机白名单目录内：
          D:/BaiduNetdiskDownload/unity项目测试.zip（允许：C:/ProgramData/ControlServer.Agent/outbox）
          （#6 #7 #8 #9 同一路径连撞 4 次，全部同一原因；#10 改用「下发」立即完成）
```

⇒ 只要源文件不在 `{状态目录}\outbox` 里，回传 **100% 失败**；
而网页上「浏览」能一路点进 D 盘并选中那个文件 —— **没有任何一处告诉用户这两个范围不一样**。

### 36.1 修法（用户已确认：方案 A）

| 方向 | 默认范围 | 收窄开关 |
|---|---|---|
| **写**（下发落地） | `{状态目录}\inbox`（**不变**，仍然很窄） | `InboxDirectories` / `DefaultInboxDirectory` |
| **读**（回传读取） | **整盘**（所有盘符根，= 浏览的可见范围） | **只认 `OutboxDirectories`** |

- `TransferPaths.ReadWhitelist`：`OutboxDirectories` 归一化后非空 ⇒ 收窄（只读这些 + 下发目录）；
  为空 ⇒ **整盘**（`DriveRoots()`）；枚举不出盘符（异常）才退回 `{状态目录}\outbox`
  （宁可范围小到"一看就不对"，也不要给一个"看着放开了、其实一个都读不了"的空表）。
- **不拿 `InboxDirectories` 当收窄开关**：那是**写**的设置，让"写"顺带把"读"掐掉是没人猜得到的隐式耦合
  —— 本次事故的根子正是"两处语义悄悄分叉"。
- **盘符枚举收进唯一实现** `TransferPaths.DriveRoots()`，`TransferBrowseService` 改为调用它：
  「浏览看得见」与「回传读得到」从此同一集合、同一份代码（防再次分叉）。

### 36.2 🔥 顺带修掉的潜伏致命缺陷：盘根当前缀会**整盘失效**

`FileTransferMath.IsInside` 旧实现在比较时**无条件补一个目录分隔符**：

- `Path.TrimEndingDirectorySeparator("C:/")` **刻意不裁根** ⇒ 归一化后盘根仍以分隔符结尾；
- 于是前缀变成 `C://` ⇒ `"C:/foo".StartsWith("C://")` = **false**。

⇒ **任何把盘根写进白名单的配置都是"写了等于没写、比没写更糟"**。
`{状态目录}\inbox` 落在盘内，所以这个坑一直没暴露；本次"可读 = 盘符根"才把它逼出来。
修法：`HasTrailingSeparator(root) ? root : root + Path.DirectorySeparatorChar`。

### 36.3 验证

- `dotnet build ControlServer.sln` **0 警告 / 0 错误**；
- `dotnet test`：**Core 845 / Agent 122 / 集成 396 = 1363** 全部 0 失败；
- `deploy/smoke/run-all.py --only transfer`（真服务端 + 真客户端）**74 / 0**（旧口径 68）：
  关键三条 = `上报了可读目录 ['c:\','d:\']`（默认整盘）、**旧默认之外的文件也取得回来**（⑤b）、
  **收窄后越界仍被拒**（⑥，换一个 `OutboxDirectories:0=<narrow>` 的实例重起）。
  ⚠️ **命令行传列表必须带索引 `:0`** —— 裸键 `--Agent:Transfer:OutboxDirectories=X` 不报错也不生效
  （配置绑定器当标量处理），第一轮冒烟就是栽在这上面（服务照常起来、日志里可读范围仍是整盘）；
- 新增用例：`tests/ControlServer.Agent.Tests/TransferReadScopeTests.cs` **6 条**
  （默认可读整盘、可读根集合 == 浏览列出的盘符、配置后真收窄、只配写不构成收窄、
  越界仍是 fatal、**`UploadAsync` 真的越过白名单**：桩回 500 时失败点推进到"建上传会话"）；
  `FileTransferMathTests` **+2**（盘根作为目录、盘根归一化后不退化成 `C:`）；
- **变异验证**：把 `ReadWhitelist` 兜底改回旧值 ⇒ `TransferReadScopeTests` **4/6 变红**；
  把 `IsInside` 前缀改回旧写法 ⇒ 新增两条 Core 用例 **2/2 变红**；
- 前端 `npm run typecheck` + `npm run build` ✅（产物 `index-m5_F8jaW.js`），
  回传方向文案改为"默认可读整盘，与浏览一致"。

### 36.4 挂账 / 交付注意

- 🔸 **改动全在客户机侧**（`TransferPaths` / `FileTransferMath` 的 Agent 侧消费者）：
  **只重启中心不生效**，必须把新客户端装到目标机器上。
- 🔸 可读范围默认整盘 = 客户机任意文件都能被取走；前置门槛是 `AdminOnly` +
  授权闸门（`POST api/distributions/collect` 归 Gated）+ 运维显式点名一个文件，
  且目录结构在 M4.7 就已可见。**要严格边界的部署方必须显式配 `OutboxDirectories`。**
- 🔸 `dist/` 交付产物**本轮未重发**（`wwwroot` 是编译期快照）。

---

## 37. 🔥 2026-10-15 修复：**「下发到客户机的路径保存不了」**（M4.19）

用户原话：**「文件下发到客户机的路径无法保存的问题，保存后需要能看到具体路径」**。
方案 → `docs/M4-客户端Agent与局域网管控方案.md` §39。

### 37.0 根因（一句话）

**「记为此机默认目录」真的存进库了，但紧接着的那一次建单又把它抹掉了，而"存进去了"在屏幕上毫无表现。**

两条链路各自都对，合起来是"保存成功、然后马上被删除"：

1. **前端**（`DistributionPanel.onPathRemembered`）：只更新了 `remembered`（= **下次物化**用的覆盖层），
   没动**已经物化好的那一格**（`targetDirs[id]`）。而物化**只发生一次**
   （`watch(selectedIds)` 里的 `if (id in targetDirs) continue`）——
   勾选时这台机器还没记过 ⇒ 填的是空串 ⇒ **点完按钮屏幕上什么都不变**。
2. **后端**（`DistributionsController.RememberPathsAsync`）：`EffectiveTargetDir ?? string.Empty`
   —— 一次**"存放路径留空"的建单会把记忆写成空串**，而那等于**删掉运维刚存下的目录**。

### 37.1 现场证据（服务端日志）

```
16:47:08 / 16:47:18 / 16:47:43  客户端默认目录已记忆（Id=11）      ← 连点三次（点了没反应）
16:47:49                        新建下发任务 #10 … 落地目录 (默认)   ← 输入框是空的 + 把记忆写成 ""
18:28                           GET /api/agents ⇒ lastTargetDir = "" ← 记住的目录不见了
```

### 37.2 改法

- 前端：记住之后**把这一台那一格也填上**；⚠️ **不能无条件覆盖** ——
  只在该格为空、或它正好等于"记住之前那个记忆值"（= 没人动过）时才填
  （那格里可能是用户手输的另一个目录，本次就该用它）。
- 后端：`?? string.Empty` → **`IsNullOrWhiteSpace(dir) ⇒ continue`**。
  🔥 **留空只是"这一次不定目录"，不该有删掉记忆的副作用** ——
  "这次用客户机默认目录"已由明细的 `TargetDir = null` 表达；删记忆是破坏性且不可见的动作。

### 37.3 验证

- `dotnet build` **0 警告 / 0 错误**；`dotnet test` **Core 845 + Agent 122 + 集成 399 = 1366，0 失败**；
- 新增 `DistributionApiTests` **3 条**（留空不冲记忆、指定目录才更新、回传记父目录且不动下发方向）；
- **变异验证**：`RememberPathsAsync` 改回旧写法 ⇒ 新用例**立刻变红**（`DistributionApiTests.cs:578`）；
- 真服务端 + 真实接口：记住 `D:/播放内容/2026` → 建"留空"单（明细 `targetDir = null` 正确）
  → 再读 `lastTargetDir` **仍是 `D:/播放内容/2026`** ✅；
- 前端 `typecheck` + `build` ✅（`index-BqHihaHg.js`）。

### 37.4 挂账 / 交付注意

- 🔸 **"清空记忆"没有 UI 入口了**（刻意）：要清得调 `PUT /api/agents/{id}/path-memory`
  （`null` = 不动，空串才清）。**让"这次不定目录"与"永久忘掉这个目录"不再共用一个动作。**
- 🔸 记忆只是**预填值不是权限**：仍受客户机**可写白名单**再校验（默认只有 `{状态目录}\inbox`）；
  要落到自定义目录必须在客户机配 `Agent:Transfer:InboxDirectories`。
- 🔸 本次改动**全在服务端 + 前端**，**重启中心即生效**（与 §36 那条"必须换客户端"不同）。
- 🔸 `dist/` 交付产物**未重发**。

---

## 38. 🔥 2026-10-15 定稿：**彻底取消回传白名单机制**（M4.20）

用户原话：**「取消文件回传的白名单机制，不限制目录」**。
方案 → `docs/M4-客户端Agent与局域网管控方案.md` §40。

### 38.0 一句话

**回传（读）从"有范围"改成"没有范围"**：删校验、连配置项一起删，
并同步删掉会因此变成**假绿**的旧断言。

⇒ 与 §36（先修成"默认可读整盘＋可收窄"）的关系：**§36 的第二版设计被本节取代**，
`OutboxDirectories` 这个键**已经不存在了**，别再照 §36 去配它。

### 38.1 为什么不留"想收窄就配"的开关

那正是 §36 留下的问题：**开关在，就有人以为配了生效、也有人以为不配就不安全**。
本项目对"配了等于没配／看着放开了其实没放开"这类陷阱零容忍。所以
**要么有边界、要么没有**；将来若恢复限制，**必须连同 `ResolveSourcePath` 的校验一起设计**，
不许只把配置项加回来。

### 38.2 改动清单

| 层 | 文件 | 改动 |
|---|---|---|
| 客户机 | `FileTransferClient.ResolveSourcePath` | **删范围校验**；只留「非空＋绝对路径＋可解析」。改 `static` |
| 客户机 | `FileTransferClient` | 删 `ReadWhitelist` 属性 / `BuildReadWhitelist()` |
| 客户机 | `TransferPaths` | **删 `ReadWhitelist()`**；`DriveRoots()` 保留（浏览顶层＋上报候选） |
| 客户机 | `TransferOptions` | **删 `OutboxDirectories`**；类注释改为"读无白名单" |
| 客户机 | `Program.cs` | 启动日志：`可读 全部目录（回传不限制，2026-10-15 起）` |
| 客户机 | `AgentRuntime` | 清单上报 `TransferOutboxDirectories = DriveRoots()`（**仅供页面提候选**） |
| 服务端 | `AgentTelemetryDtos` / `AgentsController` | 注释改为"候选提示／顶层＝盘符，不是限制"（字段名沿用，不动线协议） |
| 公共库 | `FileTransferMath.IsInsideAny` | **删除**（唯一消费者就是那段被删的校验）。`IsInside` 仍供"下发落地"用 |
| 前端 | `DistributionPanel.vue` / `types.ts` | 文案改为"盘符提示（仅供参考，不是限制）" |

**没跟着放开的只有下发落地（写）**：仍白名单、默认 `{状态目录}\inbox`。
用户要取消的是"回传"，**这两者不要再一起动**。

### 38.3 边界退到哪一层（必须向部署方讲清）

```
回传边界 = 谁能建这张单 = AdminOnly + 授权闸门（collect 归 Gated）+ 运维显式点名一个文件
```

≡ "管理员能读这台机器上的文件"，与他坐到那台机器前同一量级（而"目录结构可见"在 M4.7 就发生了）。
**要挡住数据请用操作系统手段**（ACL／磁盘加密／域策略）——本产品给不了目录级保护了。

### 38.4 删掉了两条会变"假绿"的断言（重要）

| 位置 | 为何失效 | 处理 |
|---|---|---|
| `transfer-smoke.py` ⑥ | 它断言"换一个配了 `OutboxDirectories` 的实例重起后越界仍被拒"——配置项与"越界"概念都没了 | **整段删除**，原地留注释说明为何删 |
| `TransferReadScopeTests.cs` | 6 条全围绕 `ReadWhitelist`／收窄开关 | **整文件重写** |
| `browse-e2e.py` | 那行 `--Agent:Transfer:OutboxDirectories:0=…` 会让客户端因**未知配置键启动失败** | 删除该行 |

### 38.5 验证

- `dotnet build` **0 警告 / 0 错误**；
- `dotnet test`：**Core 844 / Agent 122 / 集成 399**；Core＋集成 **0 失败**；
  **Agent 117 通过、5 失败**（全是 `UdpCommandListenerTests`，因**本机真实 Agent 服务 PID 3908 占着 UDP 8090**，
  `Get-NetUDPEndpoint` 已证实；与本次改动无关，见 §38.6）；
- 重写用例 `TransferReadScopeTests` **6 条**：任意无关目录／**系统目录**／**另一个盘**都能过；
  相对路径、空路径、文件不存在仍**致命**；
- **变异验证**：`ResolveSourcePath` 改回"只读一个私有目录" ⇒ 新用例 **4/6 变红**；
- 端到端真服务端＋真客户端 `run-all.py --only transfer`：**70 通过 / 0 失败**
  （旧口径 74，少的 4 条正是 38.4 删掉的 ⑥ 段）；
- 前端 `typecheck` + `build` ✅（`index-LsRx_SA5.js`）。

### 38.6 顺手发现：Agent 测试套件对"本机装了 Agent"不免疫（**挂账 M6**）

`UdpCommandListenerTests` 会**真绑 UDP 8090**，而产品自己也绑它 ⇒ 本机装了
`ControlServerAgent`（开机自启）时这 5 条**必然红**：`监听 8090 端口失败：每个套接字地址只允许使用一次`。
正解＝让用例注入**临时端口**，与"本机是否装了产品"解耦。**本轮未修**，留给 M6 加固收口。

### 38.7 交付注意

- 🔸 **改动含客户机侧**（`FileTransferClient`／`TransferPaths`／`TransferOptions`／`AgentRuntime`）：
  **只重启中心不生效**，必须把新客户端装到目标机器；
  服务端与前端改动（上报字段注释、分发页文案）重启中心即可。
- 🔸 **升级顺序无所谓**，但**老客户端上报的"可读范围"仍会显示在页面上** ——
  新前端把它当"候选提示"渲染（文案已改成"仅供参考，不是限制"），老客户端的那个列表
  在新前端下不再被误解为限制。
- 🔸 `dist/` 交付产物**本轮未重发**。

---

## 39. 🔥 2026-10-15 M4.21：**未勾选的客户机也要看得见「落点」**

用户原话：**「在不勾选客户机的情况下，需要能看到客户机的文件下发路径」**。
方案 → `docs/M4-客户端Agent与局域网管控方案.md` §41。

### 39.0 一句话

分发页目标行的「存放路径」**不再只在勾选后才出现**：未勾选时**只读显示该机当前会落到的目录**，
勾选后的输入框初值**与它逐字相同**。**纯前端改动，服务端／客户机／线协议零改动。**

### 39.1 症状与取值优先级

未勾选那一格从前写「勾选后才能指定」——把"该勾哪几台"最需要的信息**藏在复选框后面**。
现在是三档（**唯一事实源 `landingPathOf()`**）：

| 档 | 来源 | 显示 |
|---|---|---|
| ① | `agent_hosts.LastTargetDir`（路径记忆） | **上次** `D:\播放内容` |
| ② | 客户机上报 `transferInboxDirectories[0]` | **默认** `C:\…\inbox` |
| ③ | 都没有 | **未记录（勾选后可指定，留空 = 客户机默认目录）** |

② 就是"任务不带目标目录时的落点"（`FileTransferClient.ResolveInboxDirectory` 的 `allowed[0]`）。
③ 对**老客户端**（不上报清单）是常态 —— 照实说，不是缺陷。

### 39.2 ⚠️ 本轮真修掉的是"**两种说法并存**"（真机验证抓的）

第一版只做显示，**物化仍走 `lastTargetDirOf`** ⇒ 从没下发过的机器：
**屏幕写「默认 C:\…\inbox」、勾上之后输入框是空的**。
空串本就表示"用默认目录"（行为没错），但**同一件事说了两遍、还说得不一样** ⇒
用户只会认为"路径没生效"。**修法＝两处同源**：`targetDirs[id] = landingPathOf(id)`。

**代价（讲清）**：②档会被**显式写进输入框**并随任务发出去。语义等价（客户机默认目录＝白名单第一项，
显式写照样过校验），但 **① 不再随白名单变化自动跟随；② 这次下发会把它记成"上次目录"**
（下次那行改显示"上次"）。想回到跟随默认目录 ⇒ **清空输入框**（空串语义仍在）。

### 39.3 小机群才预取清单

② 档要客户机上报的清单，**一台一个请求** ⇒ 机群 **≤ `DIR_HINT_MAX_AGENTS`(12)** 才全量预取；
超过就只按"已勾选"取（上百台为了一格提示发上百个请求不划算，而那种规模现场早有 `lastTargetDir`）。
预取仅在**下发**方向，且与既有的 `DIR_HINT_MAX_AGENTS` 上限同一套理由。

### 39.4 验证（**新增「UI 级」验证手段**）

| 项 | 结果 |
|---|---|
| `vue-tsc` / `vite build` | 0 错 ✅ `index-BjLLLOL0.js` |
| **真机 e2e（UI 级）** | 真服务端（源码态→读磁盘 `wwwroot`）＋真客户机＋**无头 Edge（CDP 驱动 Runtime.evaluate / Page.captureScreenshot）** ⇒ 直接断言**渲染出来的 DOM**：场景 `default` **18/0**、场景 `notrecorded` **13/0** |
| **变异验证** | 物化改回 `lastTargetDirOf` ⇒ **恰好那一条变红（17/1）**，其余全绿 |
| 截图 | `%TEMP%\cs-ui-verify\{default,notrecorded}\*.png` |

🔥 **这套 UI 级探针值得复用**（脚本在 `%TEMP%\cs-ui-verify\verify.mjs`，未入库）：
Node 22 自带 `WebSocket` + `fetch` ⇒ **不需要 Playwright**，直接用 CDP 驱动本机 Edge（`--headless=new`
+ `--remote-debugging-port`）就能对真实 DOM 下断言 + 截图。它抓的正是**"显示 A、勾上 B"**
这类纯前端缺陷 —— 而这类缺陷 API 级冒烟**永远看不见**。
⚠️ 客户机身份＝**机器码** ⇒ **同一台电脑起两个客户端进程只会得到同一个主机**
（服务端只认机器码），要覆盖两种上报状态只能**各起一次实例**（分两次跑）。

### 39.5 交付注意

- 🔸 **纯前端**：源码态刷新页面即生效；`dist/` 是**编译期快照** ⇒ 给现场用**必须重发**（**本轮未重发**）。


---

## 40. 🔥 2026-10-15 M4.22：**用例不再真绑 8090**（测试与"本机装了什么"解耦）

方案 → `docs/M4-客户端Agent与局域网管控方案.md` §42（§40.6 那条挂账的收口）。

### 40.1 症状：同一份代码，红绿取决于**这台机器上装了什么**

`UdpCommandListenerTests` 有 **5 条**真去**绑 UDP 8090**；而产品自己也绑它。
开发机上装着一个开机自启的真 Agent ⇒ 这 5 条**稳定红**，报
`监听 8090 端口失败：通常每个套接字地址…只允许使用一次`。
**那种红比不做断言更坏** —— 它会训练人忽略红色。

5 条的共同点：**全走"没有配置 / 端口越界 ⇒ 用默认端口"那条路**。

### 40.2 修法：把"回落端口"做成**可注入**，语义与接线**分开守**

| 要守的东西 | 谁守 | 绑 8090 吗 |
|---|---|---|
| 没配置/越界 ⇒ **回落到默认端口**（行为） | 原来那 5 条（改用**注入的空闲端口**） | **不绑** |
| 那个默认端口**就是 8090**（常量 + 生产接线） | 新增 1 条**只读 `FallbackPort`、从不 Apply** | **不绑** |

- `UdpCommandListener` 构造新增**可选** `int? fallbackPort = null`（不传＝`AgentConfigDefaults.DefaultUdpPort`）
  ⇒ **生产零改动**（`Program.cs` 只是加了句"这里永远不要传"的注释）；`Apply` 里 3 处常量改用 `FallbackPort`。
- ⚠️ 参数放**构造函数**而非配置：它是**测试缝**，不是现场旋钮 ——
  放进配置＝多一个"能改默认端口"的口子，一旦被改就重演"文档写 8090、实际听在别处"（§24.1 吃过）。

### 40.3 验证（**故障先变成可重复的**）

原前提"这台机器恰好装着 Agent"不可控 ⇒ 先用占位脚本（`%TEMP%\cs-udp-occupy\occupy.py`，未入库）
把 8090 占住，**确定性地**复制出"有真 Agent 在跑"：

| 状态 | 改动前 | 改动后 |
|---|---|---|
| 8090 空闲 | 122/0 | **123/0** |
| 8090 被占 | **117/5**（就是 §40.6 现场） | **123/0** |

全解决方案（8090 全程被占）：Core **844/0**、Agent **123/0**、集成 **403/0**；`build` 0 警告 0 错误。
**变异 A**（回落值写 `?? 8091`）⇒ **恰好新那条变红**；
**变异 B**（构造忽略注入＝退回旧行为）⇒ 8090 被占时**恰好那 5 条变红**
（证明"现在能过"确实是注入换来的，不是碰巧）。

### 40.4 铁律（对**以后的**测试同样成立）

1. 🔥 **用例不许绑全局固定端口**（8090/8080/9998 一样）—— 要绑就绑"**当时空闲的**"，
   再**单独**钉"生产那个值是几"。
2. 🔥 **排查"这组用例稳红"的第一步**：问"它依赖这台机器上的什么状态？"（装了服务 / 占了端口 / 有网）。
3. 🔸 交付：**纯测试 + 注释**改动，不改协议/服务端/前端 ⇒ `dist/` **无需重发**。


---

## 41. 🔥 2026-10-16 M4.23：**客户端离线的时间段，历史曲线直接留空**

> 用户原话：「历史曲线优化：客户端离线的时间段，历史曲线直接留空」。详版见 `docs/M4-…方案.md` §43。

### 41.0 一句话

空白段不再被折线**一条直线接过去**：服务端在空得够久处插一个**全 null 的断点标记**（`IsGap`），
前端那套「null 必须断开折线」自然断开；再加浅底衬 + 一句说明，免得"断线"被读成"图表坏了"。

### 41.1 根因：**"空桶跳过"只做了一半**

| 做法 | 图上看到 | 编数据了吗 |
|---|---|---|
| 空桶补 0（早就否掉） | 掉到 0 再上来 | **有**（假掉底） |
| 空桶跳过（原状） | 前后两点**被直线连起来** | **有**（假平稳，更隐蔽） |

补 0 至少看得出掉下去了；连直线看不出任何异常 —— 被控端关机两小时，图上是一条"CPU 一直 40%"的直线。

### 41.2 判据

`GapThreshold = max(Server:Agent:OfflineAfterSeconds(75s), 2 × 分桶粒度)`：

- 前半与在线表那颗**灰点**同源（"页面说离线"与"曲线留空"必须是同一段时间）；
- 后半防粗粒度窗口（30 天窗 1 小时一格）"少一格"就被判成离线；
- ⚠️ **不能"少一格就算"** —— 采样 5 秒一条且断线重连期间样本会攒进下一批，
  偶尔丢一两格是网络抖动，用它断线会让曲线布满小豁口。

### 41.3 落点（4 处）

| 层 | 内容 |
|---|---|
| Core 纯函数 | `GapThreshold` / `GapMarker` / `MarkGaps`（幂等、只标两段真数据之间、无空白零分配）/ `MergeLayers` |
| 契约 | `AgentMetricsPointDto.IsGap`、`AgentMetricsSeriesDto.GapThresholdSeconds`（都在末尾带默认值 ⇒ 老夹具不用改） |
| 服务 | `QuerySeriesAsync` 两条路径都标；`daily+raw` 走 `MergeLayers` |
| 前端 | 断点底衬／悬停"（这段没有数据）"／空白段说明／**实时帧挪格也要判** |

### 41.4 两处最容易漏的

1. **接缝不是离线**：长窗口＝"老一段日聚合 + 最近一段明细"，接缝天然隔了将近一天；
   跨接缝判会让**每次查询**都在同一位置插假断点（一条随"现在几点"滑动的豁口）。⇒ `MergeLayers` 各判各的。
2. **实时帧那条路**：详情页是"拉一段历史 + 每来一帧往前挪一格"。被控端离线十分钟后回来的第一帧
   会被直线接过去；更糟的是**用户点一下「刷新」它就变回空白** ⇒ 同一段时间两种画法。
   挪格时用同一个 `GapThresholdSeconds` 判。

### 41.5 ⚠️ 附带发现（**未修，挂账**）：`daily+raw` 是一条**跑不到的路**

`QuerySeriesAsync` 里 `var rawFrom = to - span > to - rawSpan ? to - rawSpan : from;` 取的是 `min`
⇒ 恒等于 `from` ⇒ `rawFrom <= from` **恒真** ⇒ **永远走明细，日聚合那段是死代码**。

- **证据**：18.6 天窗口（`2026-09-28 ~ 2026-10-16`）返回 `source="raw"`、`bucketSeconds=3600`。
- **影响**：`rawSpan = 200 000 行 × 5 秒 = 11.57 天` ⇒ **7 天窗不受影响**，
  **30 天窗（518 400 行）被 `OrderBy(AtMs).Take(200 000)` 截成「最老的」11.57 天**，最近 18 天图上没有点。
- **为什么不顺手改**：改一行会让 30 天窗取数整个变样（日聚合层第一次真正上线），
  而它撞上另一个既有设计（接缝那天 `00:00 → rawFrom` **按设计就是空的**）⇒ 先定行为，再改那一行。

### 41.6 验证

- `dotnet build ControlServer.sln` **0 警告 0 错误**；`npm run typecheck` 干净；`vite build` 成功（**先前端后后端**）。
- Core 单测 **873/0**（其中聚合器 **78**，本轮 +12）；遥测集成用例 **32/0**（+1 条端到端：断点位置／两侧跨度／判据下发）；
  集成全量 **528/0**。
- ⚠️ 本轮与另一路（M5.4b 第 7 步）**并行改同一个仓库**：Core 从 844 → 873 里有 17 条是那边的 `RemoteVideoNegotiationTests`。

---

# M4 不许忘（从 `MEMORY.md` 迁入，2026-10-16）

> 迁入理由与 `REF-M5-远程桌面.md` 相同：`MEMORY.md` 超长被截断。
> M1–M4 已全部交付，这些是**历史结论**；`MEMORY.md` 里只留一行指针。

- **双进程（Service＋Tray）/发现**：UDP **9998**；**不做 ping 探测**；状态＝**心跳（10/30/75s）＋长连接在不在**（**连接断⇒绝不"在线"**）。
- 🔥 **在线与下发同源**（10-15）：曾各说各话⇒「在线却说离线」。改＝`Classify(…, longConnection)`；**离线不再拒发**：`/power` 走 UDP 兜底（`status:"udp"`），够不着才 409；拉黑一律拒发；`/reboot`、`/config/redeliver` **无兜底**。§35。
- 🔥 **Session 0** 无桌面·**无音频端点** ⇒ 窗口/音量**必须**会话侧做；**三类「重来」分开**。
- 🔥 **文件分发**：信令走 SignalR，字节走 **Agent 主动 HTTP**；**已收字节唯一事实源＝`.part` 长度**。
- 🔥 **回传不限制目录**（10-15 定稿）：**白名单机制已删**（连 `OutboxDirectories` 一起）——只校验「绝对路径＋文件存在」。边界退到"谁能建单"（AdminOnly＋闸门）；**写**仍是 `{state}\inbox`。浏览/回传不可能再分叉。§40。
- 🔥 **`IsInside` 盘根前缀**（10-15）：`TrimEndingDirectorySeparator` **不裁根** ⇒ 盘根自带尾分隔符，**不能再补一个**（补成 `C:\\` ⇒ 整个盘判成"不在目录内"）。
- 🔥 **「记住的默认目录」＝只在真指定目录时回写**（10-15 修）：曾「留空建单」也写空串 ⇒ **把刚存的路径抹掉**（现场＝连点三次"记为此机默认目录"、屏幕上毫无变化、建单仍是"默认"）；同时记住后**必须把该行输入框填上**（键只在勾选时物化一次，不填＝白存）。§39。
- 🔥 **未勾选的客户机也要看得见「落点」**（10-15）：分发页那一格曾写"勾选后才能指定"。取值＝`lastTargetDir`（标**上次**）→ 客户机上报的默认目录（标**默认**）→ **未记录**；**显示与勾选后预填必须同源**（`landingPathOf`，否则"写着默认 `C:\…`、勾上是空框"＝§39 同一种病）。机群 ≤12 才预取清单兜底。§41。
- 🔥 **配置中心化**：整包替换·版本递增；`GET …/config` 双身份同端点⇒**归属校验只能在控制器做**。
- 🔥 **UDP**：**纯明文一行**（无签名·无防重放）；白名单→**来源 IP 黑名单**；**防火墙是唯一边界**。
- 🔥 **UDP 听不听＝两关**：①总开关 `Agent:Tasks:Enabled=false`＝永不听；②**未连中心⇒一律听**（端口沿用配置、越界回落 8090）；**拉黑也算未连＝照听**；**已连才配置说了算**。两态：`udp.enabled/port`＝"要它怎样"／`host.udpChannel`＝"它现在怎样"（`null`≠没听）。
- 🔥 **托盘只剩 4 项**（服务信息行＋打开本机配置＋数据/日志目录）；`Exit` **仍须处理**（`WM_CLOSE`＝`taskkill` 优雅停机）。
- 🔥 **SignalR 实参个数必须与签名严格一致**⇒ 新增上报一律**独立方法**。
- 🔥 **温度默认值在安装侧**（`install-agent.ps1` 不传 `-NoThermal`＝开）；客户端**配置缺失＝关**是**故意**。
- 🔥 **准入免凭据**：门槛＝`Server:Agent:MaxHosts`；未匹配 `/api` 被 SPA 200 应答⇒**判端点别看状态码**；**移除后能加回来**：心跳查不到⇒服务端 `Abort`＋客户端遇 401 去注册一次；**拉黑≠吊销**：不清 `agentKey`＋注册入口也拒；「移除」＝硬删⇒**新 Id**（§30）。
- 🔥 **组合按钮（第三类）**：成员**引用主按钮**；段间延迟＝**本步之后**等（**末步归零且不显示**）、上限 60s；与主按钮**同级**参与对接码；执行**分段**（不拍平）。
- 🔸 **本机页「服务端」明细默认收起**：**只由 `serverOpen` 说**，渲染别写 `hidden`；**指令执行只有一份**（`AgentCommandExecutor`，演练＝全局闸）。
- 🔸 **遥测**：`cpu{socket}`/`disk{N}` 键稳定，**`luid`/Hyper-V 网卡 GUID 重启即变**⇒逐设备曲线身份＝**`kind`+名**（同帧同名才 `#N`；取值挑当时活着的键；列表＝每类最后一帧＋回看 3 帧）；**不猜型号**（§34）。
- 🔥 **曲线留空**（10-16）：空桶跳过只保证"**没有点**"、**不保证折线断开** ⇒ 服务端插**全 null 的 `isGap` 标记点**；
  判据 = `max(离线判定 75s, 2 × 分桶)`，`GapThresholdSeconds` **随序列下发**；**两段聚合层不跨接缝判**（`MergeLayers`）；
  **实时帧"往前挪一格"也要判**（否则"刷新"与"不刷新"是两种画法）。⚠️ 挂账：`rawFrom` 三元取了 `min`
  ⇒ `daily+raw` 跑不到、30 天窗被截成**最老** 11.57 天。§41。
- 🔸 **EF 迁移**：`--project` 用 Infrastructure。

---

## 41. 🔥 2026-10-16 M4.24：**回传还被白名单拦住 ＝ 发出去的客户端包是旧的**

方案 → `docs/M4-客户端Agent与局域网管控方案.md` §44。
用户原话：「修复一下这个BUG，不应该限制目录」（附截图：回传 #17 / 「源路径不在本机白名单目录内…（允许：…\outbox）」）。

### 41.1 结论先行

**源码里早就不限制**（10-15 M4.18 放开、M4.20 连机制删掉）。
问题在 **`dist/agent` 还是 10-15 `10:45` 的产物**，早于那两次修复 ⇒ 从它装的机器一直带着旧限制。
**修法＝重打客户端包＋换那台机器；代码一行没动。**

### 41.2 定案靠四条独立证据（**这条套路要记住**）

1. 服务端日志有"客户机回报的那句话"（时间/任务号/客户机号齐全）；
2. **全仓搜那句话 ⇒ 生产代码 0 处** ← 最要命的一条：直接排除"代码还没修"；
3. `agent_hosts` 那台的 `RegisteredAt` 早于 `dist/agent` 的 mtime ⇒ 装的就是旧包；
4. **拿旧包本身跑一遍冒烟**（`CS_AGENT_EXE=<旧包>`）⇒ 报错一模一样。

> 🔥 **"某句话在源码里已经不存在了，现场还在报" ⇒ 一律先怀疑产物/已装客户端，别去改代码。**

### 41.3 🔥 为什么冒烟没拦住（**run-all.py 的配对陷阱**）

`_resolve_agent_exe()` 里 `dist/agent` 排在**仓库调试构建**前面，而"同源 sibling"那条
在被测服务端是 `bin/Debug` 时**指向不存在的路径** ⇒ 照样落到 `dist/agent`。
结果：**现在的代码的服务端 + 上一次打包的客户端**，客户端那半边断言根本没测到新代码，**却全绿**。
已修：住在 `dist\` 的 ⇒ `dist\agent` 打头；否则 ⇒ 调试构建打头（探针验证三种位置各得其所）。

### 41.4 验证（真产物 before / after）

| 客户机 exe | 结果 |
|---|---|
| 旧包（10-15 10:45，存 `.tools/old-dist/`） | **65 / 3**（失败＝"旧默认之外的文件也取得回来"等三条） |
| 新包（10-16 15:04） | **70 / 0** |

**最快的"新不新"判据＝客户机启动自述那一行**：
旧包 `…｜可读 …\outbox`；新包 `…｜可读 全部目录（回传不限制，2026-10-15 起）`。
`build` 0 警告 0 错误。产物指纹（SHA256 前 16）：
Service `D9AD0F125CCBD921`、Tray `F99E32569CB2151E`、Setup `4D87F5551A96FE0D`（中心下载目录那份**逐字节相同**）。

### 41.5 两个缺口（**未动，等拍板**）

1. 🔴 **版本号分辨不出新旧**：`Directory.Build.props` 的 `<Version>` 是全局 `0.1.0` ⇒
   `agent_hosts.AgentVersion` 每台都 0.1.0。而它本来就是为了提示"安装包不一致"的字段，
   **因为版本从不递增而永远哑火**（本轮只能靠人肉比时间戳）。修法：打客户端包时版本 +1，
   或改上报**构建时间戳**。
2. 🔸 `dist/` 没有"产物落后于源码"的检查 ⇒ 留 `agent-build.txt`，或让冒烟在
   客户机 exe 早于源码时**打警告**（警告，不是失败）。

### 41.6 交付注意

- 🔸 **现场必须换客户端**（中心侧零改动）：中心网页右上角「客户端安装包」已是新包。
- 🔸 已修两处**说反了的注释**（`DistributionDtos.SourcePath`、`web/src/api.ts`）
  —— 过时注释比没有注释更危险：它会让人把校验加回来。

### 42. 显示名改名：中控客户端 Agent →「客户端Server」（2026-10-16 晚）

**拍板**：新名 **客户端Server**（无空格、S 大写）；范围**只到"显示名 + deploy 脚本注释"**
（`src/` 代码注释与 `docs/` 未动）。

| 用户可见 | 位置 | 效果 |
|---|---|---|
| 安装包 AppName | `deploy/windows/ControlServer.Agent.iss:52` | 程序与功能 / 开始菜单组 / 向导标题 / 卸载项 |
| 服务显示名 | `deploy/windows/install-agent.ps1:104`（`-DisplayName`，**唯一出口**） | `services.msc` 里那一行 |
| 服务描述 | 同文件 `:675` | 服务属性里的说明 |

**刻意不动**（改了就会"装两个"或找不到安装包）：AppId `{8B4C5D2E…}`｜ServiceName `ControlServerAgent`｜
安装目录 `{autopf}\ControlServer Agent`｜状态目录 `%ProgramData%\ControlServer.Agent`｜
产物名 `ControlServer-Agent-Setup-<ver>.exe`（`AgentInstallerLocator` 按**文件名前缀**找包）。
⇒ 升级 / 卸载 / 自检 / 诊断**零影响**。

**顺手一改**：完成页 `客户端服务「{#AppName}」` → `服务「{#AppName}」`
（否则读成"客户端服务「客户端Server」"）。

**产物**：ISCC 重编（29.5 s，exit 0）→ `dist/agent-setup/ControlServer-Agent-Setup-0.1.0.exe`；
版本资源 `ProductName=客户端Server`、`FileDescription=客户端Server Setup` 已验证；
已投放 `%ProgramData%\ControlServer\downloads\`（两边 sha256 一致＝`6494E1A2…FDA1AC`）。
⚠️ **`dist\agent\*.exe` 不用重打** —— 显示名不在客户端 exe 里（是安装时 `-DisplayName` 传进去的）。
旧包两份（dist 版 + 原 downloads 版）备份在 `.tools/old-dist/agent-setup-20261016-1845\`。
⇒ §41.6 那个 Setup 指纹（`4D87F5551A96FE0D`）**已因改名失效**，判"包新不新"别再拿它对。

### 42.1 第二批：托盘也跟着改名（同日晚）

> ⚠️ **先纠正一条我写错的**：**客户端的托盘不在 `src/ControlServer.TrayAgent`！**
> 那是**服务端**的托盘（文案「中控服务 · 托盘助手」，属于中心服务，**不该改**）。
> 客户端托盘是独立工程 **`agents/ControlServer.Agent.Tray`**，原文案是「中控客户端」。
> ⇒ 两个托盘、两套代码、两个 exe（`ControlServer.TrayAgent.exe` vs `ControlServer.Agent.Tray.exe`），
>   改名前**先确认改的是哪一个**。

**改法**：在 `agents/ControlServer.Agent.Tray/Program.cs:65` 立**唯一来源**常量
`const string ProductName = "客户端Server";`，把 6 处"露脸"文案全部改成走它
（气泡标题 / 悬停提示 / 三处出错弹窗 / 本机配置页标题）：

| 位置 | 旧 | 新 |
|---|---|---|
| `:415` 气泡标题 | `"中控客户端"` | `ProductName` |
| `:519` 悬停提示 | `$"中控客户端 · {DescribeClient(…)}"` | `$"{ProductName} · {…}"` |
| `:333/:407/:624` 弹窗标题 | `"中控客户端 · 托盘"` | `$"{ProductName} · 托盘"` |
| `:577` 配置页弹窗 | `$"中控客户端 · 本机配置（{url}）"` | `$"{ProductName} · 本机配置（{url}）"` |
| `Agent/Config/local-ui.html:7` | `<title>本机配置 · 中控客户端</title>` | `<title>本机配置 · 客户端Server</title>` |

**刻意没改**：菜单第一行 `客户端服务：运行中`（说的是"Windows 服务"这个**角色**，
不是产品名）；`DefaultServiceName="ControlServerAgent"`、单实例互斥体名、
`HKCU\...\Run` 值名 `ControlServerAgent.Tray` —— 都是内部标识。

**产物（第二次）**：`build-agent.ps1` 重发 → `dist\agent\` 2 个 exe（20:26）；
ISCC 重编（exit 0）→ Setup `sha256 bd088e8b…`（= `65131205` 字节，20:28）；
已投放 `%ProgramData%\ControlServer\downloads\`（两边一致）。
旧 exe 备份 `.tools/old-dist/agent-20261016-2030\`；旧 Setup 备份
`.tools/old-dist/agent-setup-20261016-2035\`。
⚠️ **本条覆盖 §42 里的 `6494E1A2…`**：判"包新不新"以 `bd088e8b…` 为准。

### 42.2 坑与验证边界（下次直接用）

1. 🔥 **`dotnet publish` 打 `dist\agent` 会被沙箱拦掉一个文件**：本机第一次跑
   `build-agent.ps1` 时，Service 成功、Tray 报
   `MSB4018 … UnauthorizedAccessException: Access to the path '…\ControlServer.Agent.Tray.exe' is denied`
   —— 那是**沙箱拒写**（不是文件被进程占用：`tasklist` 查过没有客户端进程）。
   ⇒ 症状与 `MSB3027`（真被占用）**一模一样**，别急着去杀进程；重发那一个工程即过
   （单独 `dotnet publish agents\ControlServer.Agent.Tray\...csproj -o dist\agent` → exit 0）。
2. ⚠️ **单文件 exe 里搜字符串是搜不到的**（`EnableCompressionInSingleFile=true` ⇒
   整包压缩，UTF-8 / UTF-16 都 0 命中，**新旧都 0**，别把它当"没改上"）。
   要核对就用**中间产物**：`agents\ControlServer.Agent.Tray\bin\Release\net8.0\win-x64\*.dll`
   （IL 里字符串是 UTF-16；嵌入资源如 `local-ui.html` 是 UTF-8）。
   本轮实测：Tray.dll 里 `客户端Server` UTF-16 命中 **4**、`中控客户端` **0**；
   Agent.dll 里新的 `<title>` UTF-8 命中 **1**。
3. 🔸 **仍未做**：托盘 exe 的**真机运行**验证（本机起一次看气泡/悬停是否显示
   「客户端Server」）—— 单文件包完整性目前只由"publish exit 0 + 无 MSB4018"背书。

### 42.3 剩一条（等拍板）

- 🔸 **已装机器的服务显示名不变**：要重装 / 升级一次才会更新
  （`install-agent.ps1` 会先删旧服务再建）。**现场必须换客户端**，中心侧零改动。
