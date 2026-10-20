using ControlServer.Agent;
using ControlServer.Agent.Config;
using ControlServer.Agent.Diagnostics;
using ControlServer.Agent.Remote;
using ControlServer.Agent.Service;
using ControlServer.Agent.Tasks;
using ControlServer.Agent.Telemetry;
using ControlServer.Agent.Transfer;
using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using ControlServer.Platform.Abstractions;
using ControlServer.Platform.Linux;
using ControlServer.Platform.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════════
// 中控服务 · 客户端 Agent（服务侧，M4.1）
//
// 它跑在 **Session 0**（Windows 服务），所以它**看不到用户桌面**，
// 也因此它只做"不需要桌面"的事：
//   · 发现中心服务端（UDP 广播）
//   · 注册（拿 agentKey）与维持长连接（心跳）
//   · 执行服务端下发的指令
// 需要桌面的能力（托盘、截屏、键鼠注入、音量）全部在 Agent.Tray 里 —— 那是另一个进程。
//
// ⚠️ 这个进程**默认不监听任何端口**：所有通信都是它连出去。
//    两个例外，都在本地且都要加固：
//      · M4.5 的「UDP 指令通道」（远程关机 / 重启 / 音量）—— 端口由中心下发（默认 8090），
//        🔥 2026-10-13 起是**纯明文 + 来源 IP 黑名单**（不再有 HMAC 验签）；
//      · 2026-10-14 的「本机配置页面」—— 只绑 127.0.0.1（默认 8091），
//        校验 Host 头 + 写操作要带本进程随机令牌（见 AgentLocalConfigUi 的三道闸门）。
//    除此之外，客户端不需要动防火墙。
//
// 同一个 exe 既可作为 Windows 服务运行，也可在命令行前台运行（排障用）：
//   ControlServer.Agent.Service.exe --Agent:ServerUrl=http://10.0.0.5:8899
// ═══════════════════════════════════════════════════════════════════════════

var builder = Host.CreateApplicationBuilder(args);

// 服务形态识别：以 Windows 服务身份被 SCM 拉起时自动接上 SCM；
// 在命令行里跑时它什么都不做（因此两种形态共用同一份代码）。
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = builder.Configuration["Agent:ServiceName"] ?? "ControlServerAgent";
});

// ── 状态目录：必须在建 DI 之前算出来（日志目录依赖它）──
var stateDirectory = AgentPaths.ResolveStateDirectory(builder.Configuration["Agent:StateDirectory"]);

Directory.CreateDirectory(stateDirectory);

var dataDirectory = new AgentDataDirectory(stateDirectory);
dataDirectory.EnsureCreated();

// ── 日志 ──
//
// 控制台（前台排障看）+ Core 的 LogService（NDJSON 落盘，与中心服务端同一套格式）。
// 落盘的路径是 %ProgramData%\ControlServer.Agent\logs\ —— 服务形态没有控制台，
// 没有文件日志就等于"它到底干了什么一概不知"。
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

builder.Services.AddSingleton<IDataDirectory>(dataDirectory);
builder.Services.AddSingleton<LogService>();

// ── 平台能力（客户端全部在 Windows 上；抽象已在，Linux 留接口）──
builder.Services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
builder.Services.AddSingleton<IMachineCodeProvider, WindowsMachineCodeProvider>();
builder.Services.AddSingleton<IHostHardwareInfo, WindowsHardwareInfo>();

// ── Agent 配置：直接绑定成一个单例实例 ──
// 运行期会被服务端下发的心跳间隔更新，因此必须是**同一个实例**而不是每次新绑一份。
builder.Services.AddSingleton(sp =>
{
    var options = new AgentOptions();
    builder.Configuration.GetSection(AgentOptions.SectionName).Bind(options);
    return options;
});

builder.Services.AddSingleton(sp => new AgentIdentityStore(
    stateDirectory,
    sp.GetRequiredService<ISecretProtector>()));

// ── 遥测采集源（M4.2）──
//
// ⚠️ 全部注册为**单例**：它们都是有状态的差分采样器
//    （PDH 速率计数器要两次采样才出值、网卡速率要记住上次累计字节）。
//    注册成 Transient 会让每一帧都"第一次采样"，所有速率类指标永远是 null。
//
// Windows 上指标是**两个源合成**：PDH 给 CPU/磁盘/GPU，BCL 给网络速率。
// 合成逻辑放在 CompositeHostMetricsSource 里，上层（AgentRuntime）看不到这个差别。
builder.Services.AddSingleton<IHostMetricsSource>(_ => OperatingSystem.IsWindows()
    ? new CompositeHostMetricsSource(new WindowsHostMetricsSource(), new BasicHostMetricsSource())
    : new BasicHostMetricsSource());

// 非 Windows 上注册 Noop 而不是"不注册"：让上层一个 if 都不写，
// "本平台不支持开关机记录"由 IsAvailable 一路带到页面上。
//
// 🔥 Linux 那一侧**不是 Noop**：`wtmp`（`last -x`）能把"正常关机"与"断电/崩溃"分开，
//    而这正是这张表上最要紧的一栏。⚠️ 它要读 `/var/log/wtmp`（root 或 utmp 组），
//    读不到时 `IsAvailable` 会是 false，并带着权限原因 —— 页面上那句话与"空表"含义完全不同。
builder.Services.AddSingleton<IPowerEventSource>(_ => OperatingSystem.IsWindows()
    ? new WindowsPowerEventSource()
    : new LinuxPowerEventSource());

builder.Services.AddSingleton<IHostInventorySource>(_ => OperatingSystem.IsWindows()
    ? new WindowsHostInventorySource()
    : new BasicHostInventorySource());

// ── 视频硬编码能力探测（M5.0 第二批）──
//
// 🔥 它跑在**客户机**上，而且是**服务进程**里（不是托盘）：枚举适配器 / 查 MFT / 读 PDH
//    都不需要桌面，Session 0 里可用（见 docs/M5.0-实施清单.md §1.2）。
//    需要桌面的抓屏 / 编码 / 注入才必须待在托盘进程 —— 那是 M5.1 的事。
//
// ⚠️ 探测结论要经 SignalR 上报给中心（见 AgentRuntime.ReportEncoderCapabilityAsync）：
//    中心那边**探不出**这个结论 —— 它探的是中心自己那块显卡。
//
// 非 Windows 注册"如实说明未实现"的兜底（本仓惯例：上层一个 if 都不写）。
builder.Services.AddSingleton<IEncoderCapabilityProbe>(_ => OperatingSystem.IsWindows()
    ? new WindowsEncoderCapabilityProbe()
    : new UnsupportedEncoderCapabilityProbe("客户端 Agent 的编码能力探测目前只在 Windows 上实现。"));

// ── 硬件温度（M4.3，**可选能力**：装上就有、裸起不带）──
//
// 🔥 这一段是"驱动只在需要时才会碰"的实现本体：注册的是 NoopThermalSource 时，
//    它连 LibreHardwareMonitorLib 都不会碰，那个内嵌的 WinRing0 驱动
//    也就永远不会落盘、永远不会加载、不会创建任何内核服务。
//
// ⚠️ **默认值到底在哪一头**，2026-10-14 起是这样（三方必须一致，别只看一处）：
//    · 安装向导「启用硬件温度监控」**默认勾选**（需求①）⇒ `install-agent.ps1`
//      会把 `Agent:Telemetry:ThermalEnabled=true` **显式**写进 appsettings.json；
//      取消勾选才写 false ⇒ **正常现场装完就是开的**。
//    · 而**配置缺失**（绿色版 / 手工排障 / 冒烟直接起 exe）⇒ 键读不到 ⇒ **关**。
//      这一条是**故意**的：一个会释放并加载内核级驱动（WinRing0）的能力，
//      不该"没人告诉它就自己开"。方案 §22 的「默认不装驱动」就是靠它成立的。
//    · ⚠️ 所以别在 `TelemetryCollector` 里找开关 —— 那里曾有一个默认 `true` 的
//      `ThermalEnabled` 属性，**全仓无人读写**，已于同日删除（见该处的注释）。
//      一个写了却不生效的默认值，比没有它更危险：它会让人得出相反结论。
var thermalRaw = builder.Configuration["Agent:Telemetry:ThermalEnabled"];
var thermalEnabled = bool.TryParse(thermalRaw, out var thermalFlag) && thermalFlag;

// 🔥 关掉的时候，把**是哪一种关**算清楚再传下去 —— 组合根是唯一分得清的地方。
//    `thermalRaw == null` ⇒ 配置文件里**根本没有这一项**（绿色版 / 手工起的 exe / 冒烟裸起）；
//    `thermalRaw` 有值但为假 ⇒ 是**装的时候没勾**（或有人显式改成 false）。
//    两者的排查动作完全不同：一个是"去补配置"，一个是"去翻安装勾选框"。
//    ⚠️ 早先 Platform 层写死一句「安装时未启用」，在绿色版上就是假话 ——
//       会把运维支去翻一个他从没用过的安装包。详见 WindowsThermalSource 的 ctor 注释。
var thermalDisabledNote = thermalEnabled
    ? null
    : string.IsNullOrWhiteSpace(thermalRaw)
        ? "配置里没有 Agent:Telemetry:ThermalEnabled 这一项"
          + "（绿色版 / 手工启动的客户端不会自动开启硬件温度监控；加上这一项即可）。"
        : "配置里把 Agent:Telemetry:ThermalEnabled 关掉了（安装时未勾选，或显式设成了 false）。";

builder.Services.AddSingleton<IThermalSource>(_ => OperatingSystem.IsWindows()
    ? new WindowsThermalSource(thermalEnabled, thermalDisabledNote)
    : new LinuxThermalSource());

builder.Services.AddSingleton(sp => new TelemetryStateStore(
    stateDirectory,
    sp.GetRequiredService<LogService>()));

builder.Services.AddSingleton(sp => new TelemetryCollector(
    sp.GetRequiredService<IHostMetricsSource>(),
    sp.GetRequiredService<IPowerEventSource>(),
    sp.GetRequiredService<IHostInventorySource>(),
    sp.GetRequiredService<IThermalSource>(),
    // 传的是那个**单例配置对象里的子对象**：服务端下发的节奏会就地改写它，
    // 因此采集器读到的永远是"当前生效值"，不需要额外的通知机制。
    sp.GetRequiredService<AgentOptions>().Telemetry,
    sp.GetRequiredService<TelemetryStateStore>(),
    sp.GetRequiredService<LogService>()));

// ── 服务端下发的任务配置与执行（M4.5）──
//
// 三件事：拿配置（HTTP 拉）、记配置（本机文件，中心不可达时降级运行）、跑配置（三张清单）。
// 🔥 2026-10-14 起这份文件**本机也能写**（本机配置页），所以它的语义从"中心的只读缓存"
//    变成了"这台机器自己的配置"：两边都能改，谁后写谁生效（版本优先、同版本比时间戳）。
builder.Services.AddSingleton(sp => new AgentConfigClient(
    new HttpClient { Timeout = TimeSpan.FromSeconds(20) },
    sp.GetRequiredService<LogService>()));

builder.Services.AddSingleton(sp => new AgentLocalConfigStore(
    stateDirectory,
    sp.GetRequiredService<LogService>()));

builder.Services.AddSingleton(sp => new AgentConfigManager(
    sp.GetRequiredService<AgentConfigClient>(),
    sp.GetRequiredService<AgentLocalConfigStore>(),
    sp.GetRequiredService<AgentOptions>(),
    sp.GetRequiredService<LogService>()));

// 本机配置页面（2026-10-14）：客户机**自己**的编辑入口（仅 127.0.0.1 可访问）。
// 它是"客户端可独立使用"的落地 —— 没有中心服务端时也能改任务与 UDP 端口。
// ── 「接收到的数据」（2026-10-13，排查"指令不生效"）──
//
// 一个纯内存的环形缓冲：客户机收到的每一条东西（UDP 数据报 + 中心下发的 SignalR 消息）
// 连同"当时发生了什么"记在里面。**必须早于 UdpCommandListener 与 AgentRuntime 注册** ——
// 那两位都要往里写。
builder.Services.AddSingleton<ReceivedDataLog>();

// ── 本机配置页面（2026-10-14）──
// 它同时提供 `GET /api/received`（把上面那个缓冲给现场看）——
// 这是"客户端内显示接收到的数据"的落地：**不连中心也能看**。
// 服务端地址的本机覆盖（2026-10-15，需求①）：本机页上填的地址优先级最高。
// 必须早于 AgentRuntime / AgentLocalConfigUi 注册 —— 两边都依赖它这一份实例
// （页面写、主循环读，**不能各拿一个**，否则改完地址运行期永远不知道）。
builder.Services.AddSingleton<AgentServerOverride>();

builder.Services.AddSingleton<AgentLocalConfigUi>();

// ── 会话侧指令通道（M4.5）──
//
// 「延时启动程序」与「音量」必须在**用户会话**里执行：服务跑在 Session 0，
// 那里没有桌面也没有音频端点，自己动手是"执行成功、毫无效果"的无声失败。
//
// 通道本体是一条**命名管道**，对面是用户会话里的托盘（`ControlServer.Agent.Tray`）。
// ⚠️ 管道名按**当前控制台会话编号**区分（见 SessionPipeProtocol 注释）：
//    多用户同时登录时会有多个托盘，共用一个名字会让服务固定敲到"先登录那个人"的桌面上。
//    所以这里传的是**一个委托**而不是算好一次的字符串 —— 会话编号是可以在运行期变的。
//
// 非 Windows 上注册"不可用实现"：`IsSupported = false`，
// 于是延时启动程序会**直接放弃**（而不是去等一个永远不会出现的托盘）。
// ⚠️ 服务名在这里取一次：`options`（WindowsService 的那个回调参数）只活在它自己的 lambda 里。
var configuredServiceName = builder.Configuration["Agent:ServiceName"];

builder.Services.AddSingleton<ISessionCommandChannel>(sp => OperatingSystem.IsWindows()
    ? new SessionPipeChannel(
        WindowsSessionLocator.TryGetActiveConsoleSessionId,
        sp.GetRequiredService<LogService>(),
        // 🔥 只有**改了服务名**的那一套才在管道名里带自己的名字（M7.4 §11）：
        //    正式安装（ControlServerAgent）拿到的仍是老的管道名，升级前后不会对不上；
        //    而冒烟 / 验证环境与正式那套装在同一台机器上时，各走各的管道，
        //    不会再出现"会话管道存在但无权连接（Access is denied）"那种像权限配错的症状。
        PipeScope(configuredServiceName))
    : new UnavailableSessionCommandChannel("非 Windows 平台尚未提供会话内的托盘伴侣，因此没有会话侧指令通道。"));

/// <summary>
/// 服务名 → 管道后缀：<b>正式那套给空</b>，其余给服务名本身。
/// </summary>
/// <remarks>
/// ⚠️ 托盘那一侧（<c>ControlServer.Agent.Tray/Program.cs</c>）必须用<b>同一个判据</b>，
/// 否则两侧算出的管道名只差一个后缀，症状是"会话侧一律不可用"，看不出原因。
/// </remarks>
static string? PipeScope(string? serviceName)
    => string.IsNullOrWhiteSpace(serviceName)
        || string.Equals(serviceName.Trim(), "ControlServerAgent", StringComparison.OrdinalIgnoreCase)
            ? null
            : serviceName.Trim();

builder.Services.AddSingleton(sp => new AgentTaskRunner(
    sp.GetRequiredService<LogService>(),
    sp.GetRequiredService<ISessionCommandChannel>()));

// ── 电源控制（M4.5）──
//
// 关机 / 重启执行在**服务侧**（SYSTEM 天然持有 SeShutdownPrivilege）。
// 做成端口而不是在这里直接调 shutdown.exe：命令名与参数是平台专有的，
// 按项目铁律 #2 必须落在 Platform.Windows / Platform.Linux 里。
//
// 非 Windows 上注册"不支持实现"而不是不注册：让上层一个 if 都不写，
// "本平台不支持"由 IsAvailable 一路带到日志上，而不是一次静默无操作。
builder.Services.AddSingleton<IHostPowerController>(_ => OperatingSystem.IsWindows()
    ? new WindowsHostPowerController()
    : new LinuxHostPowerController());

// ── 音量控制（M7.3）──
//
// Windows：音量由**托盘进程**持有（服务在 Session 0 里没有音频端点，自己改是"成功、无声"），
//          所以服务侧注册的是"不支持实现"，真正的执行走 ISessionCommandChannel。
// Linux：**没有托盘**（见上面那条通道注册），于是由服务侧的实现代劳 ——
//          `LinuxAudioController` 内部会定位图形会话、再用 runuser / sudo -n
//          带着那个用户的 XDG_RUNTIME_DIR 去执行（否则改的是 root 的音频上下文）。
//
// ⚠️ 两个平台都注册、且都实现 IAudioController：让 AgentCommandExecutor 一个 if 都不用写，
//    "本平台不支持"由 IsAvailable / AvailabilityNote 一路带到日志与页面上。
builder.Services.AddSingleton<IAudioController>(sp => OperatingSystem.IsWindows()
    ? new UnsupportedAudioController(
        "Windows 上音量由托盘进程持有（服务在 Session 0 里没有音频端点），服务侧不直接控制。")
    : new LinuxAudioController(message => sp.GetRequiredService<LogService>().Info("Agent", message)));

// ── 指令执行器（2026-10-15）──
//
// 🔥 **UDP 报文与中心（SignalR）指令共用同一个实例**：两条通道、一份执行。
//    在它出现之前，"关机 / 重启 / 音量"的实现长在 UdpCommandListener 里，
//    中心页面上下发的 power 指令到了客户端没有任何人处理 ⇒
//    表现为"中心说已下发、机器毫无反应"（而 UDP 那条路一直正常）。
builder.Services.AddSingleton(sp => new AgentCommandExecutor(
    sp.GetRequiredService<AgentOptions>(),
    sp.GetRequiredService<LogService>(),
    sp.GetRequiredService<ISessionCommandChannel>(),
    sp.GetRequiredService<IHostPowerController>(),
    sp.GetRequiredService<RemoteSessionCoordinator>(),
    // 会话侧通道在本平台结构上不可用时的回落实现（Linux 走它，Windows 永远不碰）。
    sp.GetRequiredService<IAudioController>()));

// ── 远控码流通道（M5.1）──
//
// 🔥 它与上面那条会话指令通道是**两条不同的管道**，别合并：
//   · 会话指令通道 = 「发一条、拿一个结果」，一天也没几次；
//   · 码流通道     = 「持续收一串二进制帧」，10–30 fps。
// 把码流塞进前者，会让音量/延时启动在那条管子里排队到流结束。
//
// ⚠️ 方向也是反的：这条由**托盘建管道**、服务连上去读。
//    原因是命名管道的默认 DACL 只放 SYSTEM / 管理员 / 创建者 ——
//    服务是 SYSTEM，由它建管道的话普通用户身份的托盘**根本连不上**
//    （见 RemoteStreamProtocol 的注释）。
builder.Services.AddSingleton<ISessionStreamChannel>(sp => OperatingSystem.IsWindows()
    ? new SessionStreamChannel((level, message) =>
    {
        var log = sp.GetRequiredService<LogService>();

        if (string.Equals(level, "Warn", StringComparison.OrdinalIgnoreCase)) log.Warn("Remote", message);
        else log.Info("Remote", message);
    })
    : new UnavailableSessionStreamChannel("非 Windows 平台尚未提供会话侧的推流通道。"));

// ── 远控输入回传通道（M5.2）──
//
// 🔥 它是上面那条的**镜像**：那条是"托盘写、服务读"（收画面），
//    这条是"服务写、托盘读"（下输入）。**必须分开两条管道**：
//    同一条字节流上双向写，读到的长度前缀可能来自另一个方向的半句话 ——
//    表现是"解码出一堆垃圾或者直接卡死"，而两边日志都正常。
//
// ⚠️ 方向仍然由**托盘建管道**：命名管道的默认 DACL 只放 SYSTEM / 管理员 / 创建者，
//    服务是 SYSTEM，由它建的话普通用户身份的托盘连不上（与码流那条同一条理由）。
builder.Services.AddSingleton<ISessionInputChannel>(sp => OperatingSystem.IsWindows()
    ? new SessionInputChannel((level, message) =>
    {
        var log = sp.GetRequiredService<LogService>();

        if (string.Equals(level, "Warn", StringComparison.OrdinalIgnoreCase)) log.Warn("Remote", message);
        else log.Info("Remote", message);
    })
    : new UnavailableSessionInputChannel("非 Windows 平台尚未提供会话侧的输入回传通道。"));

// ── 远控音频回传通道（M5.3）──
//
// 🔥 这是**第三条**管道，既不复用码流那条，也不复用输入那条。理由是这三条的
//    「丢帧策略」根本对立，复用任何一条都会把另一条弄坏：
//      · 码流：「队列深度 1、忙就丢」——丢一帧只是画面抖一下；
//      · 音频：「**丢一帧就是一个可听见的断点**」——同一套队列会把哒哒声做成常态；
//      · 输入：单向、一条不丢、要回执。
//    节奏也差两个量级：视频 10–30 fps，音频 50 包/秒（20 ms 定帧）。
//    三者都是**托盘建、服务连**（同一条 DACL 理由，见上）。
builder.Services.AddSingleton<ISessionAudioChannel>(sp => OperatingSystem.IsWindows()
    ? new SessionAudioChannel((level, message) =>
    {
        var log = sp.GetRequiredService<LogService>();

        if (string.Equals(level, "Warn", StringComparison.OrdinalIgnoreCase)) log.Warn("Remote", message);
        else log.Info("Remote", message);
    })
    : new UnavailableSessionAudioChannel("非 Windows 平台尚未提供会话侧的音频回传通道。"));

// ── 远控会话中枢（M5.1）──
//
// 它是"承上启下"的唯一一处：向上接中心（经 AgentRuntime 挂上来的出口），
// 向下把抓屏/编码的活转给托盘、把码流接回来、把输入送下去、把音频接回来。
// 单例：M5.1 只允许一路会话，而且它的状态（当前会话 Id / 接流任务）必须全局唯一。
//
// 🔒 注入 AgentInputPolicy（M5.2 的本机否决）：
//    它决定"要不要起输入通道"，以及"报给中心的结论是不是 unavailable"。
//    ⚠️ 它与托盘**各判一次**（两边读同一个标记文件）：任何一边单独失效都不会变成
//       "中心能注进去"。这不是重复，而是"作用点在哪就在哪拦"。
//
// 🔥 三条通道**不在同一个开关下**（M5.3）：输入有本机否决，音频没有。
//    音频只受"会话在不在"约束，理由见 RemoteSessionCoordinator.AudioPumpAsync。
builder.Services.AddSingleton<AgentInputPolicy>();

builder.Services.AddSingleton(sp => new RemoteSessionCoordinator(
    sp.GetRequiredService<ISessionCommandChannel>(),
    sp.GetRequiredService<ISessionStreamChannel>(),
    sp.GetRequiredService<ISessionInputChannel>(),
    sp.GetRequiredService<ISessionAudioChannel>(),
    sp.GetRequiredService<AgentInputPolicy>(),
    sp.GetRequiredService<LogService>()));

// ── UDP 加固指令通道（M4.5）──
//
// 本项目里**唯一一个客户机主动开着的入站端口**。端口 / 开关 / 来源黑名单全部由中心下发
// （决策 D5：事实来源是服务端），报文是**纯明文**（决策 D10 于 2026-10-13 改为明文）——
// 所以它只要拿到配置就起得来，由 AgentRuntime 在正确的时机 Apply。
//
// ⚠️ 第 5 个参数（回落端口）**刻意不传**：不传 = 生产的那个默认端口 8090。
//    它是给用例留的缝（本机装着真 Agent 时 8090 会被占住），**这里永远不要传**。
builder.Services.AddSingleton(sp => new UdpCommandListener(
    sp.GetRequiredService<AgentOptions>(),
    sp.GetRequiredService<LogService>(),
    sp.GetRequiredService<AgentCommandExecutor>(),
    sp.GetRequiredService<ReceivedDataLog>()));

builder.Services.AddSingleton<AgentRuntime>();
builder.Services.AddHostedService<AgentServiceHost>();

var host = builder.Build();

// 启动一行"我在哪、我用什么配置"的自述 —— 现场排障时这一行能省掉半小时。
var startupOptions = host.Services.GetRequiredService<AgentOptions>();
var startupLog = host.Services.GetRequiredService<LogService>();

startupLog.Info("Agent", $"客户端 Agent 启动（版本 {AgentVersion.Current}，状态目录 {stateDirectory}）");

startupLog.Info(
    "Agent",
    string.IsNullOrWhiteSpace(startupOptions.ServerUrl)
        ? $"服务端地址：未指定（将用 UDP {startupOptions.DiscoveryPort} 广播自动发现）"
        : $"服务端地址：{startupOptions.ServerUrl}（已跳过广播发现）");

// 远程输入这一行（M5.2）：它必须**单独一行**，而且"禁止"要连**怎么改**一起说 ——
// 现场遇到"画面能看、点不动"时，先看到的往往是启动日志；没有这一行，
// 那个状态和"被控端没有键鼠注入能力"、"中心没开闸门"三种情况看起来一模一样。
var startupInputPolicy = host.Services.GetRequiredService<AgentInputPolicy>();

startupLog.Info(
    "Agent",
    startupInputPolicy.AllowsInput
        ? "远程键鼠注入：允许（仍需观看端在每一路会话上显式打开控制开关）。"
        : $"远程键鼠注入：<b>已被本机禁止</b>（检测到 {startupInputPolicy.FilePath}）"
          + "—— 这是本机的否决，中心无法覆盖；删掉该文件即可恢复。");

// 遥测这一行要说清"节奏是本地的，连上后会被服务端覆盖" —— 现场看到
// "配置里写 5 秒但实际是 30 秒"时，能立刻想到是服务端下发的，而不是配置没生效。
startupLog.Info(
    "Agent",
    startupOptions.Telemetry.Enabled
        ? $"遥测：本地节奏 指标 {startupOptions.Telemetry.MetricsSeconds} 秒 / "
          + $"开关机轮询 {startupOptions.Telemetry.PowerEventPollSeconds} 秒 / "
          + $"清单 {startupOptions.Telemetry.InventoryHours} 小时（连上后以服务端下发为准）"
        : "遥测：已在本地配置中停用。");

// 温度状态**单独一行**：这是"明明勾了却没生效"最难自查的一处。
// 日志里必须能一眼看到"是没启用、还是启用但驱动挂了、挂在哪一步" ——
// 否则现场只能靠"页面上没有温度"这一个现象猜。
//
// ⚠️ 这一行会**触发一次性驱动加载**（仅当配置里启用了）。刻意放在这里：
//    让加载发生在启动阶段、结果立刻落一行日志，而不是散在 5 秒一次的采样里。
var startupCollector = host.Services.GetRequiredService<TelemetryCollector>();

var thermalLine = $"硬件温度：{AgentThermalStates.DisplayName(startupCollector.ThermalState)}";

if (!string.IsNullOrWhiteSpace(startupCollector.ThermalNote))
{
    thermalLine += $"（{startupCollector.ThermalNote}）";
}

startupLog.Info("Agent", thermalLine);

// 文件传输（M4.4）：把**生效的白名单**印出来。
// 现场两类问题靠这一行就能分清 —— "配了却没生效"（配置没被读到）与"任务说越界"
// （中心给的目录确实不在名单里）。少了它，这两者看起来完全一样。
var transferOptions = startupOptions.Transfer;

if (transferOptions.Enabled)
{
    startupLog.Info(
        "Agent",
        $"文件传输：启用（并发 {Math.Clamp(transferOptions.MaxConcurrent, 1, 8)} 路）｜"
        + $"可写 {string.Join("、", TransferPaths.InboxWhitelist(transferOptions, stateDirectory))}｜"
        + "可读 全部目录（回传不限制，2026-10-15 起）");
}
else
{
    startupLog.Info("Agent", "文件传输：已在本地配置中停用（不接下发、不回传）。");
}

// 任务配置（M4.5）：说清"任务是中心下发的、本机只负责跑"，以及会话侧能力当前可不可用 ——
// 现场看到"延时启动程序没反应"时，这一行能直接给出答案（而不是让人去猜是配置没下发还是通道没接）。
startupLog.Info(
    "Agent",
    startupOptions.Tasks.Enabled
        ? "任务配置：接收中心下发的三张清单（发送 / 定时 / 启动），并在中心不可达时按本地缓存降级运行。"
        : "任务配置：已在本地配置中停用（不拉配置、不载入缓存、不执行任何任务）。");

var sessionChannel = host.Services.GetRequiredService<ISessionCommandChannel>();
var consoleSession = OperatingSystem.IsWindows() ? WindowsSessionLocator.TryGetActiveConsoleSessionId() : null;

// 这一行必须能回答三个问题：**这条通道结构上有没有 / 此刻通不通 / 敲的是哪个会话的门**。
// 现场"延时启动程序没反应""音量调不动"时，看它一眼就能分清是
// "本机没有托盘"、"没人登录"、"还是根本没这条通道"。
startupLog.Info(
    "Agent",
    !sessionChannel.IsSupported
        ? "会话侧指令通道：本平台不支持 —— 音量与延时启动程序会被跳过（服务在 Session 0 里做这两件事是无声失败）。"
        : sessionChannel.IsAvailable
            ? $"会话侧指令通道：就绪（控制台会话 #{consoleSession} 中的托盘在监听）｜音量与延时启动程序由它执行。"
            : "会话侧指令通道：暂不可用 —— 当前没有交互式控制台会话（无人登录）或托盘未启动"
              + "（音量指令会当场失败；延时启动程序会一直等到用户登录后再执行）。");

// UDP 指令通道（M4.5）：这一行要能回答"端口是多少、谁给的、为什么没开"。
// 现场看到"发了关机指令没反应"时，先看这一行 —— 它是"配置没下发 / 还没注册 / 端口被占"
// 这几种情形的分水岭。真正"通道已启动"的那一行由 UdpCommandListener 在绑定成功后打。
startupLog.Info(
    "Agent",
    startupOptions.Tasks.Enabled
        ? "UDP 指令通道：随中心下发的配置启用（端口 / 来源黑名单由服务端下发，报文为明文）"
          + $"｜{DescribeUdpExecution(startupOptions.Tasks.ExecuteUdpCommands)}"
        : "UDP 指令通道：已在本地配置中停用（端口不会被绑定）。");

/// <summary>
/// 「演练模式」那一句话。<b>刻意单独成句而不是合并进上面那行</b>：
/// 开着演练模式却以为是真执行，是这类功能里最容易出现、也最危险的一次误判。
/// </summary>
static string DescribeUdpExecution(bool execute)
    => execute
        ? "指令会真实执行"
        : "⚠️ 演练模式（Agent:Tasks:ExecuteUdpCommands=false）：命令已验签但不执行";

// ── 本机配置页面（2026-10-14）──
//
// 刻意在 `RunAsync` **之前**显式启动，而不是做成 HostedService：
// 它必须在"用户点托盘菜单打开页面"之前就已经在听，而 HostedService 的启动时机由宿主决定；
// 显式启动还让"端口被占用"这条失败在启动日志里立刻可见（而不是等用户点开才发现打不开）。
// 停机时由 DI 释放（它实现了 IAsyncDisposable）。
//
// ⚠️ 失败**不影响主循环**：这个页面是个便利，起不来最多是"本机改不了配置"，
//    中心下发那条路完全不受影响（见 AgentLocalConfigUi.Start 的注释）。
var localUi = host.Services.GetRequiredService<AgentLocalConfigUi>();

localUi.Start();

startupLog.Info(
    "Agent",
    localUi.Url is { } localUrl
        ? $"本机配置页：{localUrl}（仅本机可访问）—— 托盘菜单「打开本机配置」可直接打开。"
        : "本机配置页：未启动（已停用或端口被占用）—— 详见上一行。");

try
{
    await host.RunAsync();
}
catch (Exception ex)
{
    // 铁律 #6：绝不允许静默死掉。服务形态下控制台不可见，所以必须落盘。
    startupLog.Error("Agent", "客户端 Agent 异常退出", ex);
    await startupLog.FlushAsync();
    Environment.ExitCode = 1;
}
finally
{
    await startupLog.DisposeAsync();
}
