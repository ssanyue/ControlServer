using System.Reflection;
using ControlServer.Agent.Config;
using ControlServer.Agent.Diagnostics;
using ControlServer.Agent.Remote;
using ControlServer.Agent.Tasks;
using ControlServer.Agent.Telemetry;
using ControlServer.Agent.Transfer;
using ControlServer.Agent.Transport;
using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using ControlServer.Platform.Abstractions;
using Microsoft.AspNetCore.SignalR.Client;

namespace ControlServer.Agent;

/// <summary>本程序集的产品版本（三段，如 <c>0.1.0</c>）。</summary>
public static class AgentVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = typeof(AgentVersion).Assembly;

        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        var plus = version.IndexOf('+');
        var trimmed = plus >= 0 ? version[..plus] : version;
        var parts = trimmed.Split('.');

        return parts.Length switch
        {
            >= 3 => string.Join('.', parts.Take(3)),
            2 => $"{parts[0]}.{parts[1]}.0",
            _ => $"{trimmed}.0.0",
        };
    }
}

/// <summary>
/// 客户端 Agent 的**主循环**（M4.1）：找服务端 → 注册 → 连上 → 心跳 → 断了再来。
/// <para>
/// <b>为什么四件事收在一个循环里，而不是拆成四个后台任务</b>：
/// "发现 / 注册 / 连接"有严格的先后顺序（没地址谈不上注册，没凭据谈不上连接）。
/// 拆成并行任务就要写状态机与条件变量来维持这个顺序，而"顺序错一步"的失败形态
/// 是"某个任务在空转、却没有任何日志"。一个循环 + 一处退避，是这里最省心的结构。
/// </para>
/// <para>
/// <b>退避策略</b>：1s → 2s → 4s → … → 60s 封顶。<b>成功连上一轮后立刻重置回 1s</b> ——
/// 否则"网络抖动 20 分钟后恢复"还要再等 60 秒才重连，现场看着就像客户端死了。
/// 「被拉黑」「在线表已满」是例外：前者 15 秒（需求要"取消后<b>立即</b>恢复"），
/// 后者 5 分钟 —— 那才是<b>必须人工介入</b>的状态，
/// 反复重试只会把日志刷满。
/// </para>
/// </summary>
public sealed class AgentRuntime : IAsyncDisposable
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 被中心**拉黑**时的重试间隔（2026-10-15）。
    /// <para>
    /// 🔥 <b>15 秒，而不是旧吊销的 5 分钟</b>：需求是"取消黑名单后<b>立即</b>恢复连接"，
    /// 而被拉黑的机器没有任何连接可用（服务端一律拒绝），只能靠轮询发现"已经放开了"。
    /// 5 分钟意味着解除后最长还要等 5 分钟，那不叫立即。
    /// </para>
    /// <para>
    /// 代价：被拉黑的机器每 15 秒发两个很小的请求（一次连接尝试 + 一次注册探测）。
    /// 被拉黑是**个别**机器的状态，不是全网状态，这个量可以接受；
    /// 而且全程只刷状态文案、<b>不写日志</b>，不会把日志刷满。
    /// </para>
    /// </summary>
    private static readonly TimeSpan BlockedRetry = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 「在线表已满」时的重试间隔：**这一条才是必须人工介入的**（去中心移除不用的机器），
    /// 所以与拉黑分开、用 5 分钟档，别每 15 秒去撞一次上限。
    /// </summary>
    private static readonly TimeSpan EnrollmentFullRetry = TimeSpan.FromMinutes(5);

    /// <summary>单轮广播发现的总等待时长。</summary>
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(3);

    /// <summary>广播发现的轮数（3 轮 × 3 秒 = 最多 9 秒，覆盖"服务端刚重启"的窗口）。</summary>
    private const int DiscoveryRounds = 3;

    /// <summary>
    /// 遥测两条循环的节拍（秒）。
    /// <para>
    /// 取 1 秒：比最快的采样间隔（5 秒）细，因此"到点就采"不会有肉眼可见的抖动；
    /// 又足够粗，300 台机器一起跑也只是每秒一次几乎空转的判断。
    /// </para>
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 硬件清单上报失败后的重试间隔。
    /// <para>
    /// <b>不能跟着 1 秒的节拍重试</b>：采一份清单要跑一串 WMI 查询（1～2 秒 CPU），
    /// 服务端一挂就变成"每秒给客户机上一次满负荷"，比不上报还糟。
    /// </para>
    /// </summary>
    private static readonly TimeSpan InventoryRetryInterval = TimeSpan.FromMinutes(5);

    private readonly AgentOptions _options;
    private readonly IHostHardwareInfo? _hardware;
    private readonly IMachineCodeProvider? _machineCode;
    private readonly AgentIdentityStore _identity;
    private readonly TelemetryCollector? _telemetry;

    /// <summary>
    /// 视频硬编码能力探测（M5.0 第二批）。
    /// <para>
    /// 🔥 它<b>只能在这里跑</b>：要编码的是这台机器，所以"本机能编什么"必须由本机回答 ——
    /// 中心替任何一台客户机探出来的结论都是错的（它探的是中心自己那块显卡）。
    /// </para>
    /// <para>
    /// 为 null = 宿主没装配（用例 / 非 Windows 现场）：那时<b>只是不上报能力</b>，
    /// 其余功能一个都不受影响 —— 所以它是可选的，而不是必须的。
    /// </para>
    /// </summary>
    private readonly IEncoderCapabilityProbe? _encoderProbe;

    /// <summary>
    /// 远控会话的中枢（M5.1）。
    /// <para>
    /// 🔥 <b>它在这里的唯一职责是"接线"</b>：把「往中心送一帧」「往中心报一条状态」
    /// 这两个出口挂到<b>当前这条</b>长连接上。会话本身怎么起停，
    /// 由 <c>AgentCommandExecutor</c> 经指令那条路调它（不经过主循环）。
    /// </para>
    /// <para>为 null = 宿主没装配（用例 / 非 Windows 现场），那时远控指令会回一句明确的话。</para>
    /// </summary>
    private readonly RemoteSessionCoordinator? _remote;

    private readonly LogService _log;
    private readonly string _stateDirectory;
    private readonly HttpClient _http;

    /// <summary>
    /// 搬字节专用的 HttpClient（M4.4）：<b>没有总超时</b>。
    /// <para>
    /// 与 <see cref="_http"/> 分开是刻意的：控制类调用（补领 / 进度 / 完成）需要短超时，
    /// 而一个 2GB 的文件在慢链路上跑十几分钟是正常的 —— 用同一个客户端就只能二选一。
    /// </para>
    /// </summary>
    private readonly HttpClient? _transferHttp;

    private readonly AgentRegistrar _registrar;
    private readonly DiscoveryClient _discovery;

    /// <summary>
    /// 文件传输工作线程（M4.4）。<b>跑在进程生命周期上</b>，不随长连接生灭 ——
    /// 详见 <see cref="TransferWorker"/> 的类注释。
    /// </summary>
    private readonly TransferWorker? _transfers;

    /// <summary>
    /// 目录浏览（M4.4+，回传源路径选择）。null = 本机未启用文件传输。
    /// <para>与 <see cref="_transfers"/> 同生共死：连"哪些目录可读"这件事都没有时，浏览无从谈起。</para>
    /// </summary>
    private readonly TransferBrowseService? _browse;

    /// <summary>服务端下发的任务配置（M4.5）。null = 本机未启用该能力。</summary>
    private readonly AgentConfigManager? _configs;

    /// <summary>三张清单的执行者（M4.5）。为 null 时任务不下发。</summary>
    private readonly AgentTaskRunner? _tasks;

    /// <summary>UDP 加固指令通道（M4.5）。null = 本机未启用该能力（<c>Agent:Tasks:Enabled=false</c>）。</summary>
    private readonly UdpCommandListener? _udp;

    /// <summary>
    /// 指令执行器（2026-10-15）：中心下发的「关机 / 重启 / 音量」最终由它落地。
    /// <para>
    /// 🔥 与 <see cref="_udp"/> **共用同一个实例**：UDP 报文与中心指令是两条通道、一份执行，
    /// 否则"UDP 能关机、中心页面关不了"这种事迟早再来一次。
    /// </para>
    /// </summary>
    private readonly AgentCommandExecutor _commands;

    /// <summary>
    /// 「接收到的数据」环形缓冲（2026-10-13）。
    /// <para>
    /// UDP 那条链路由 <see cref="_udp"/> 自己往里写；这里负责写<b>中心经 SignalR 下发</b>的那些 ——
    /// "指令不生效"也可能走的根本不是 UDP，只记 UDP 会把视线带偏。
    /// </para>
    /// </summary>
    private readonly ReceivedDataLog _received;

    private AgentCredentials? _cachedCredentials;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    /// <summary>「在线表已满」的暂停截止时刻（与「被拉黑」分开：那是 15 秒档，这是 5 分钟档）。</summary>
    private DateTimeOffset _enrollmentFullUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// 最后一次应用到 UDP 指令通道的那份配置（2026-10-14）。
    /// <para>
    /// 为什么不现读 <c>_configs.Current</c>：<b>崭新机器（还没有过配置）时它是 null</b>，
    /// 而按需求① 那种机器<b>仍然要继续听 UDP</b> —— 端口得沿用<b>上一次知道的那份</b>
    /// （现场那些发送脚本指着它）。现读的话拿到的是 null，端口会悄悄回到 8090，
    /// 而没有任何一行日志解释为什么。
    /// </para>
    /// </summary>
    private AgentConfigDto? _lastUdpConfig;

    /// <summary>
    /// 「本机页上填的服务端地址」覆盖（2026-10-15，需求①）。null = 没装这个能力（测试/嵌入式）。
    /// <para>优先级<b>高于</b> <c>Agent:ServerUrl</c> 与广播发现 —— 见 <see cref="ResolveServerUrlAsync"/>。</para>
    /// </summary>
    private readonly AgentServerOverride? _serverOverride;

    /// <summary>
    /// 当前这个地址已经<b>连续失败了几轮</b>（2026-10-20）。
    /// <para>
    /// 🔥 它唯一的用途是回答"记住的那个地址还值不值得信"：
    /// 达到 <see cref="ServerAddressResolver.DiscoveryFallbackAfterFailures"/> 之后，
    /// 客户端必须去问一句广播 —— 否则"服务端比客户端晚启动"这件事
    /// 会变成"永远连不上"（现场日志 2026-10-20 09:15–09:20 就是这个形状）。
    /// </para>
    /// <para>
    /// 连上就清零、地址一变更就清零：它描述的是<b>当前这个地址</b>，不是这台机器的历史。
    /// </para>
    /// </summary>
    private int _addressFailures;

    /// <summary>
    /// 通过广播发现<b>改用</b>的地址（2026-10-20）。
    /// <para>
    /// ⚠️ 为什么需要一个字段而不是直接写回凭据：凭据里那把 agentKey 属于<b>旧中心</b>，
    /// 把它改成新地址会让 <see cref="EnsureRegisteredAsync"/> 以为"已经注册过"，
    /// 于是拿着旧钥匙去连新中心（必然 401）。所以这里只记"这一轮起改连谁"，
    /// 真正的凭据替换交给注册那条路（它比较 ServerUrl 时会作废旧身份）。
    /// </para>
    /// <para>注册成功之后清零：那时凭据里已经是新地址了，不需要这个临时结论。</para>
    /// </summary>
    private string? _adoptedUrl;

    /// <summary>
    /// 上一句"地址相关的说明"。用来<b>去重</b>：中心不可达时这段逻辑每轮都会走一遍，
    /// 同一句话重复几十遍会把真正要看的那几行淹掉（铁律：日志要能 grep）。
    /// </summary>
    private string? _lastResolveNote;

    /// <summary>这一轮是不是刚通过广播换了地址（决定要不要立刻重试、不等退避）。</summary>
    private bool _switchedServerThisRound;

    /// <summary>
    /// 中心服务端的版本（来自注册应答 / 欢迎消息）。null = 还没跟中心说上话。
    /// <para>本机配置页的「服务端」卡片要显示它：现场"某个功能对不上"时，
    /// 第一件事就是确认两边版本对得上。</para>
    /// </summary>
    private string? _serverVersion;

    /// <summary>
    /// 最后一次与中心<b>成功通信</b>的时间（欢迎消息 + 每一条成功的心跳回执）。
    /// <para>它与状态文件里的 <c>UpdatedAt</c> 不是一回事 —— 状态没变时那个不动，
    /// 而这个会随着心跳一直往前走；断线之后它停住，一眼就能看出"最后一次说得上话是什么时候"。</para>
    /// </summary>
    private DateTimeOffset? _lastSeenAt;

    /// <summary>
    /// 已经<b>成功回报给服务端</b>的配置版本（0 = 还没报过）。
    /// <para>
    /// 单独记一份而不是直接读 <c>_configs.AppliedVersion</c>：应用成功与回报成功是两件事，
    /// 回报失败（那一刻连接刚好断了）之后必须能补报 —— 否则页面上会永远停在
    /// "已下发 v5 / 已应用 v4"，运维会以为机器没生效。
    /// </para>
    /// </summary>
    private int _reportedConfigVersion;

    /// <summary>
    /// 三类上报各自的连续失败次数（只用来给日志节流，不参与任何决策）。
    /// <para>
    /// ⚠️ <b>必须分开计数</b>：指标每 2～5 秒就成功一次，而开关机事件可能一直失败 ——
    /// 共用一个计数器时，指标的成功会把开关机的计数清零，于是"每轮都记一行"，
    /// 日志在一夜之间被刷爆（实测：5 秒轮询下 1 分钟刷了 24 行）。
    /// </para>
    /// </summary>
    private int _metricsFailureStreak;
    private int _powerFailureStreak;
    private int _inventoryFailureStreak;

    /// <summary>
    /// 服务端**明确要求**补报一次硬件清单（Welcome 里的 <c>NeedsInventory</c>，2026-10-15）。
    /// <para>
    /// 置位后上报循环会**忽略 6 小时的排期**立刻采一次；<b>只有上报成功才清零</b>
    /// （失败仍按 5 分钟重试，与排期失败同一条路）。
    /// </para>
    /// <para>
    /// ⚠️ 写入方是 SignalR 的 Welcome 回调，读取方是上报循环 —— 两个线程。
    /// 用一个 <c>bool</c> 是够的：读写本身原子，而最坏情况只是"晚一个 tick 才补报"。
    /// </para>
    /// </summary>
    private bool _inventoryRequested;

    public AgentRuntime(
        AgentOptions options,
        IHostHardwareInfo? hardware,
        IMachineCodeProvider? machineCode,
        AgentIdentityStore identity,
        LogService log,
        ReceivedDataLog received,
        AgentCommandExecutor commands,
        TelemetryCollector? telemetry = null,
        AgentConfigManager? configs = null,
        AgentTaskRunner? tasks = null,
        UdpCommandListener? udp = null,
        AgentServerOverride? serverOverride = null,
        IEncoderCapabilityProbe? encoderProbe = null,
        RemoteSessionCoordinator? remote = null)
    {
        _options = options;
        _serverOverride = serverOverride;
        _hardware = hardware;
        _machineCode = machineCode;
        _identity = identity;
        _log = log;
        _received = received;
        _commands = commands;
        _telemetry = telemetry;
        _encoderProbe = encoderProbe;
        _remote = remote;
        _stateDirectory = AgentPaths.ResolveStateDirectory(options.StateDirectory);

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _registrar = new AgentRegistrar(_http, log);
        _discovery = new DiscoveryClient(options.DiscoveryPort, log);

        // 文件传输（M4.4）：只有配置里启用了才建。
        if (options.Transfer.Enabled)
        {
            _transferHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _transfers = new TransferWorker(_http, _transferHttp, options.Transfer, _stateDirectory, log);

            // 目录浏览（M4.4+）与它同条件创建：它服务的正是"给传输挑路径"这件事，
            // "能传"却不"能看"是个没有意义的组合。
            // 🔥 2026-10-13 起浏览是**整盘**的（不再需要状态目录去算白名单根）。
            _browse = new TransferBrowseService(options.Transfer, log);
        }

        // 任务配置（M4.5）：配置一到手就让执行者重起任务。
        // 两者要么都有、要么都没有 —— 只有配置没有执行者等于"拉了配置却没人跑"，
        // 那种组合不该被表达出来，所以这里显式要求成对。
        _configs = configs;
        _tasks = tasks;
        _udp = udp;

        if (_configs is not null && _tasks is not null)
        {
            _configs.Applied += OnConfigApplied;
        }
        else if (_configs is not null || _tasks is not null)
        {
            throw new ArgumentException("AgentConfigManager 与 AgentTaskRunner 必须同时提供或同时省略。");
        }

        // ⚠️ UDP 通道**必须挂在同一对身上**：它吃的就是"配置里的 Udp 节"。
        // 少了配置它没有端口与来源黑名单 —— 单独提供等于一个永远起不来的监听，
        // 那种组合不该被表达出来。
        // （2026-10-13 前这里还要求 agentKey 以派生通道密钥；明文化之后那个依赖没了。）
        if (_udp is not null && (_configs is null || _tasks is null))
        {
            throw new ArgumentException("UdpCommandListener 必须与 AgentConfigManager / AgentTaskRunner 一起提供。");
        }
    }

    /// <summary>当前状态（人话）。托盘直接显示它。</summary>
    public string Status { get; private set; } = "正在启动…";

    /// <summary>此刻是否握着长连接。</summary>
    public bool Connected { get; private set; }

    /// <summary>当前使用的中心服务地址。</summary>
    public string? ServerUrl { get; private set; }

    /// <summary>服务端给的显示名。</summary>
    public string? DisplayName { get; private set; }

    /// <summary>最后一次错误（正常时为 null）。</summary>
    public string? LastError { get; private set; }

    /// <summary>状态变化（托盘可订阅；服务侧不需要）。</summary>
    public event Action<AgentStatusSnapshot>? StatusChanged;

    /// <summary>收到服务端指令（重启等）。</summary>
    public event Action<AgentCommandDto>? CommandReceived;

    /// <summary>
    /// 主循环。<b>只要没被取消就一直不返回。</b>
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // ── 先按本机那份配置把任务跑起来（M4.5；2026-10-14 起本机也能改）──
        // 刻意放在"找服务端"之前：中心不可达时这台机器仍然要做它该做的事
        // （展厅里中心在维护，设备的报文一条都不能少）。拿到服务端的新版本后会整代换掉。
        _configs?.RestoreFromStore();

        // ── 🔥 无论有没有本机配置，都先按"未连接中心"决策一次 UDP 通道（2026-10-14，需求①）──
        //
        // 为什么必须**无条件**额外来这一次：
        //   `RestoreFromStore()` 只在**本机有配置**时才触发 `Applied` 事件，进而走到
        //   `OnConfigApplied` → `ApplyUdpChannel`；连上中心之后又会补一次（见下面 §连接成功）。
        //   于是一台**从没连过中心、本机也没有配置**的新机器，这两个口子**一个都不会开** ——
        //   `Apply` 一次都没被调用过，结果是 `netstat` 里 **8090 压根不存在**。
        //   现场形态是"装完就能用"这句话不成立：机器装好了，发指令却毫无反应。
        //
        // ⚠️ 传 `connected: false` 而不是让它读 `Connected`：这里肯定还没连上（连上要等到
        //    下面迭代里的 ③），显式写出来是为了让"未连接 ⇒ 强制监听"这条规则在这行上自明。
        ApplyUdpChannel(_configs?.Current, connected: false);

        // ── 采样循环与连接循环**分开**（M4.2）──
        // 这样"断线的那几分钟"也在采样：重连后的第一批能把缺口补上。
        // 若把采样塞进连接循环里，曲线上每次网络抖动都会变成一段空白，
        // 而"这台机器刚才是不是卡过"恰恰是断线前后最想知道的事。
        using var samplerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sampler = RunSamplerAsync(samplerCts.Token);

        // 文件传输工作线程（M4.4）与采样循环同寿：**进程生命周期**，不随长连接生灭。
        // 传字节走的是普通 HTTP，与 SignalR 是两条独立通路 —— 一次心跳抖动不该把
        // 正在传的 2GB 掐掉（详见 TransferWorker 的类注释）。
        var transfers = _transfers?.RunAsync(samplerCts.Token);

        var backoff = InitialBackoff;

        while (!cancellationToken.IsCancellationRequested)
        {
            IterationOutcome outcome;

            try
            {
                outcome = await IterateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 铁律 #6：后台循环绝不允许因异常静默退出。
                _log.Error("Agent", "客户端主循环出错（将退避后重试）", ex);
                LastError = ex.Message;
                UpdateStatus($"运行出错：{ex.Message}", connected: false);
                outcome = IterationOutcome.Retry;

                // 🔥 `Connected` 这个字段也要跟着落地（2026-10-14）。
                //
                // ⚠️ 它的值直接决定 UDP 通道"该不该听"（见 ApplyUdpChannel），而它本来是
                //    只在 RunConnectedAsync 的两端（连上 / 正常退出）被赋值的。异常从中间
                //    跑出去时那两行都没走到，字段就**停在 true** —— 于是"中心已经不可达了，
                //    这台机器却还按'中心在'的语义决定端口"，正好把需求① 想保证的那条
                //    能力漏掉。这里补上，并顺手按"未连接"重新决策一次通道。
                Connected = false;
                ApplyUdpChannel(_lastUdpConfig);
            }
            finally
            {
                // 🔥 每一轮连接收尾都把远控的出口摘掉（正常断开 / 异常 / 被取消都算）。
                //
                // 为什么非摘不可：码流送不出去之后，被控端**仍然在抓屏、仍然在编码** ——
                // 它只是把结果丢掉。现场看到的是"中心上这台机器是离线的，
                // 可它的 CPU 一直是满的"，而两边日志都不会说这是同一个原因。
                // 摘掉出口 ⇒ 正在推的流当场停下（见 RemoteSessionCoordinator.OnDisconnected）。
                DetachRemoteSession();
            }

            TimeSpan delay;

            if (outcome.ResetBackoff)
            {
                // 这一轮刚换了地址（广播发现到一个能用的中心）：
                // 上一段退避是针对**旧地址**攒的，不该让新地址再等它一轮。
                backoff = InitialBackoff;
            }

            if (outcome.OverrideDelay is { } fixedDelay)
            {
                // "等一个明知原因的固定时长"（例如被拉黑 / 在线表满）：不参与退避递增。
                delay = fixedDelay;
            }
            else if (outcome.Succeeded)
            {
                // 刚成功连过：重置退避，立刻准备下一轮。
                backoff = InitialBackoff;
                delay = InitialBackoff;
            }
            else
            {
                delay = backoff;
                var doubled = backoff * 2;
                backoff = doubled > MaxBackoff ? MaxBackoff : doubled;
            }

            try
            {
                // ── 🔥 等退避，但"本机刚改过地址"要能**当场打断**它（2026-10-20）──
                //
                // 现场实测：在本机配置页上把地址改对之后，还要等 **48 秒**才真的去连
                // （那一刻主循环正睡在 60 秒退避里）。用户的感受是"改了没用"，
                // 于是他会再改一次、再保存一次 —— 而问题从来不在保存。
                if (await WaitBackoffAsync(delay, cancellationToken).ConfigureAwait(false))
                {
                    backoff = InitialBackoff;
                    ResetAddressState();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Connected = false;
        UpdateStatus("已停止", connected: false);

        // 收尾：等采样循环真正退出再返回 —— 停机钩子里把进程掐掉会让
        // telemetry-state.json 停在一个"读到一半"的状态。
        samplerCts.Cancel();

        try
        {
            await sampler.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常路径：取消就是它退出的方式。
        }
        catch (Exception ex)
        {
            _log.Error("Agent", "采样循环收尾异常", ex);
        }

        if (transfers is not null)
        {
            try
            {
                await transfers.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常路径。
            }
            catch (Exception ex)
            {
                _log.Error("Agent", "传输工作线程收尾异常", ex);
            }
        }

        // 任务执行者收尾：停掉全部循环/定时，并等它们真正退出。
        // ⚠️ 放在最后：它可能正在等一个 24 小时的定时点，取消是唯一能让它立刻返回的方式。
        if (_tasks is not null)
        {
            try
            {
                await _tasks.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("Agent", $"任务执行者收尾异常：{ex.Message}");
            }
        }

        // UDP 指令通道收尾：把入站端口关掉（DisposeAsync 是幂等的，DI 停机时会再收一次）。
        // ⚠️ 刻意写在这里而不是只靠 DI：这一行日志要能在**日志对象还活着**的时候落盘。
        if (_udp is not null)
        {
            try
            {
                await _udp.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("Agent", $"UDP 指令通道收尾异常：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 一份配置生效（来自服务端或本地缓存）⇒ 让执行者按它重起全部任务。
    /// <para>配置管理与任务执行之间<b>只有这一根接线</b>：谁都不需要知道对方怎么实现。</para>
    /// </summary>
    private void OnConfigApplied(AgentConfigDto config, bool fromCache)
    {
        try
        {
            _tasks?.Apply(config);
        }
        catch (Exception ex)
        {
            // 铁律 #6：应用配置出错不能让连接循环挂掉（下一条任务该跑还得跑）。
            _log.Error("Agent", $"应用配置 v{config.Version} 时出错", ex);
        }

        // UDP 通道也吃这份配置（端口 / 开关 / 来源黑名单）。
        // 明文协议之后它**不再依赖 agentKey**，所以从缓存降级载入时（还没注册）就能直接起起来 ——
        // 这正是"客户端本身可独立使用"该有的样子。注册成功后仍会再 Apply 一次（幂等，无副作用）。
        ApplyUdpChannel(config);
    }

    /// <summary>按当前配置重新决定 UDP 指令通道的状态（幂等）。</summary>
    /// <param name="connected">
    /// 覆盖"此刻是否连着中心"。<c>null</c> = 用 <see cref="Connected"/> 那个字段的现值。
    /// <para>
    /// ⚠️ 它存在的唯一理由：<b>被拉黑的那一刻 <c>Connected</c> 还是 true</b>
    /// （长连接正在收尾，HubConnectionState 尚未翻），但语义上那台机器已经"不算连着中心"了，
    /// 得显式传 false 才能表达"按未连接处理"。
    /// </para>
    /// </param>
    private void ApplyUdpChannel(AgentConfigDto? config, bool? connected = null)
    {
        if (_udp is null) return;

        try
        {
            // 记下来：断线之后"端口沿用配置"靠的就是这一份（见 _lastUdpConfig）。
            _lastUdpConfig = config;

            var isConnected = connected ?? Connected;

            // ⚠️ 2026-10-13 起这里**不再传 agentKey**：报文改成纯明文了，
            //    通道不再依赖凭据派生密钥 —— 于是"还没注册成功"时也能起起来。
            //
            // 🔥 2026-10-14 起多传一个 isConnected：**没连着中心时一律监听**（需求①）。
            _udp.Apply(config, isConnected);
        }
        catch (Exception ex)
        {
            // 铁律 #6：通道起不来只是"这条能力不可用"，绝不该影响主循环。
            _log.Error("Agent", "应用 UDP 指令通道配置时出错", ex);
        }
    }

    /// <summary>
    /// 当前持有的长期凭据（agentKey）。<b>只读内存字段，绝不做文件 IO</b>：
    /// 它被配置应用与连接建立这两条热路径调用，去读一次 DPAPI 文件不值得。
    /// </summary>
    private string? CurrentAgentKey()
    {
        var cached = _cachedCredentials;
        return string.IsNullOrWhiteSpace(cached?.AgentKey) ? null : cached!.AgentKey;
    }

    /// <summary>
    /// 等一段退避；返回 <c>true</c> = <b>被"本机地址刚改过"打断</b>（不是睡满）。
    /// <para>没有本机页覆盖（用例 / 嵌入式）时退化成普通 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>。</para>
    /// </summary>
    private async Task<bool> WaitBackoffAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (_serverOverride is null)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return false;
        }

        return await _serverOverride.WaitForChangeAsync(delay, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把"当前地址"相关的记忆全部清掉（换址 / 改地址 / 连上时都要做）。
    /// <para>
    /// ⚠️ 三样必须一起清：失败计数（否则新地址立刻被判"不可信"）、
    /// 采用过的地址、去重用的上一句话（否则新地址的第一条说明会被当成重复而被吞掉）。
    /// </para>
    /// </summary>
    private void ResetAddressState()
    {
        _addressFailures = 0;
        _adoptedUrl = null;
        _lastResolveNote = null;
        _switchedServerThisRound = false;
    }

    /// <summary>同一句说明只记一次（中心长期不可达时，这段逻辑每轮都会走到）。</summary>
    private void NoteAddressOnce(string text)
    {
        if (string.Equals(text, _lastResolveNote, StringComparison.Ordinal)) return;

        _lastResolveNote = text;
        _log.Warn("Agent", text);
    }

    /// <summary>跑一轮：发现 → 注册 → 连接 → 心跳。</summary>
    private async Task<IterationOutcome> IterateAsync(CancellationToken cancellationToken)
    {
        _switchedServerThisRound = false;

        // ── 本机页刚改过服务端地址 ⇒ 先（尽力）告诉旧中心，再作废本地身份按新地址重来 ──
        //
        // 🔥 顺序很重要：**先上报、再切**。上报失败也继续（需求口径"只通知，不阻塞保存"），
        //    因为"中心已经搬走、这里正要改地址"恰恰是最常见的场景 —— 那时上报注定失败，
        //    而它不该挡住用户。
        if (_serverOverride?.ConsumeChangePending() == true)
        {
            await ReportServerChangeAsync(cancellationToken).ConfigureAwait(false);

            // 本地那份凭据属于**旧**中心，带着它去新中心毫无意义 ⇒ 作废，让下一轮重新注册。
            // （与"换了服务端地址"的判定同一条路：EnsureRegisteredAsync 比 ServerUrl 时也会发现，
            //   但这里显式清一次，免得依赖那条隐式判定。）
            _identity.Clear();
            _cachedCredentials = null;
            _transfers?.ClearSession();

            // 换中心之后"被旧中心拉黑"这件事不该继续压着它 —— 新中心未必拉黑了它。
            _blockedUntil = DateTimeOffset.MinValue;

            LastError = null;

            // 地址是人重新指定的 ⇒ 之前攒的失败计数与"广播采用过的地址"全部作废。
            ResetAddressState();
        }

        // ── 被拉黑：等人去中心取消 ──
        //
        // ⚠️ 这里**只刷状态、不写日志**：它每 15 秒会走到一次，写日志就是刷屏。
        //    状态文案本身就是给现场看的那句话，够用了。
        if (DateTimeOffset.Now < _blockedUntil)
        {
            UpdateStatus("已被中心拉黑，等待管理员取消…", connected: false);
            return IterationOutcome.FixedDelay(BlockedRetry);
        }

        // ── 在线表已满：也是人工介入型，但换 5 分钟档 ──
        if (DateTimeOffset.Now < _enrollmentFullUntil)
        {
            UpdateStatus("中心在线表已满，请先在中心移除不用的客户机…", connected: false);
            return IterationOutcome.FixedDelay(EnrollmentFullRetry);
        }

        // ── ① 找服务端 ──
        var serverUrl = await ResolveServerUrlAsync(cancellationToken).ConfigureAwait(false);

        if (serverUrl is null)
        {
            LastError = "未发现中心服务";
            UpdateStatus("正在寻找中心服务端（UDP 广播）…", connected: false);
            _addressFailures++;
            return IterationOutcome.Retry;
        }

        ServerUrl = serverUrl;

        // ── ② 确保已注册 ──
        var credentials = await EnsureRegisteredAsync(serverUrl, cancellationToken).ConfigureAwait(false);

        if (credentials is null)
        {
            _addressFailures++;
            return _switchedServerThisRound ? IterationOutcome.RetryAfterSwitch : IterationOutcome.Retry;
        }

        DisplayName = null;

        // ── ③ 连上并心跳 ──
        var connected = await RunConnectedAsync(credentials, cancellationToken).ConfigureAwait(false);

        if (connected)
        {
            ResetAddressState();
            return IterationOutcome.Success;
        }

        _addressFailures++;
        return _switchedServerThisRound ? IterationOutcome.RetryAfterSwitch : IterationOutcome.Retry;
    }

    /// <summary>
    /// 这一轮该连哪个地址。
    ///
    /// <para>
    /// <b>优先级</b>：本机页填的 &gt; 安装时写的 &gt; 上次注册成功记住的 &gt; 广播发现。
    /// </para>
    ///
    /// <para>
    /// 🔥🔥 <b>2026-10-20 修的那条</b>：原来"记住的地址"是<b>终结答案</b> —— 只要凭据里有一份地址，
    /// 广播发现<b>一步都不会走</b>。于是"客户端先启动、中心后启动"（或者中心换了 IP）
    /// 表现为<b>永远连不上</b>：它每轮都在敲一个没人听的地址，而中心明明就在同一个局域网里应答广播。
    /// 现在改成：记住的地址连续失败 <see cref="ServerAddressResolver.DiscoveryFallbackAfterFailures"/> 轮之后，
    /// 先去广播问一声 —— 发现到别的中心就换过去（凭据属于旧中心，会重新注册），
    /// 什么都没发现就继续试它。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>人指定的地址（本机页 / 安装参数）仍然不自动替换</b>：同一个局域网里可能有两套中心，
    /// 悄悄把机器挪到别人的中心上比"连不上"贵得多。那种情况只记一条线索（打印出发现的地址），
    /// 让人自己决定要不要改 —— 改地址本来就在本机页上一步。
    /// </para>
    /// </summary>
    private async Task<string?> ResolveServerUrlAsync(CancellationToken cancellationToken)
    {
        // ── ① 本机页上填的地址最优先（2026-10-15，需求①）──
        //
        // 它必须排在 `Agent:ServerUrl` 前面：用户去本机页改地址，改的就是"我连谁"，
        // 而 appsettings.json 里可能还留着装机器时写的旧地址（而且是**不可写**的那一份）。
        // 现场最典型的场景：中心换 IP 了，机器上的旧地址已经连不上，
        // 这时候唯一能改的就是本机页 —— 如果它排不过 appsettings，这个功能等于没有。
        if (_serverOverride?.Url is { } overridden)
        {
            await HintIfOtherServerExistsAsync(overridden, cancellationToken).ConfigureAwait(false);
            return overridden;
        }

        // 安装时显式指定 → 跳过发现（跨网段 / 广播被禁的网络里唯一的路）。
        if (!string.IsNullOrWhiteSpace(_options.ServerUrl))
        {
            var configured = _options.ServerUrl.Trim();
            await HintIfOtherServerExistsAsync(configured, cancellationToken).ConfigureAwait(false);
            return configured;
        }

        // ── ② 记住的地址（缓存）+ 广播发现（回落）──
        //
        // 注意 `??` 会短路：**不能**写成 `_cachedCredentials ?? _identity.Load(out var e)`
        // —— 那样 e 在缓存命中时根本没有赋值（编译期 CS0165）。
        var cached = _cachedCredentials;

        if (cached is null)
        {
            cached = _identity.Load(out _);
            _cachedCredentials = cached;
        }

        // `_adoptedUrl` 优先：那是"本轮起已经改连谁"的结论，凭据要等注册成功才会跟着变。
        var candidate = _adoptedUrl
            ?? (string.IsNullOrWhiteSpace(cached?.ServerUrl) ? null : cached!.ServerUrl);

        DiscoveredServer? found = null;

        if (!_options.AllowBroadcastDiscovery && candidate is null)
        {
            LastError = "未配置服务端地址且广播发现已关闭";
            _log.Warn("Agent", "未配置 Agent:ServerUrl 且 AllowBroadcastDiscovery=false —— 无法找到服务端。");
            return null;
        }

        if (ServerAddressResolver.WantsDiscovery(candidate, _addressFailures, _options.AllowBroadcastDiscovery))
        {
            found = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        }

        var choice = ServerAddressResolver.Resolve(
            candidate,
            found is null ? null : BuildServerUrl(found),
            _addressFailures,
            _options.AllowBroadcastDiscovery);

        switch (choice.Source)
        {
            case ServerAddressSource.None:
                LastError = choice.Note ?? "未发现中心服务";
                return null;

            case ServerAddressSource.Discovered when found is not null:
            {
                // 换址：这是"中心搬家 / 客户端先起来"这条路唯一能自愈的一步。
                // ⚠️ 记在 `_adoptedUrl` 而不是写进凭据 —— 理由见那个字段的注释。
                var changed = !ServerAddressResolver.SameAddress(choice.Url, candidate);

                _adoptedUrl = choice.Url;

                if (changed)
                {
                    _switchedServerThisRound = true;
                    _addressFailures = 0;

                    if (choice.Note is not null) _log.Warn("Agent", choice.Note);
                    else _log.Info("Agent", $"发现中心服务端：{choice.Url}");
                }

                return choice.Url;
            }

            default:
                // 还是记住的那个地址（发现到的就是它，或者压根没发现到）。
                if (choice.Note is not null) NoteAddressOnce(choice.Note);
                return choice.Url;
        }
    }

    /// <summary>
    /// 本机被<b>固定</b>到一个连不上的地址时，广播一次看看局域网里有没有别的中心，
    /// 有就<b>只记一条线索</b>（不自动改）。
    /// <para>
    /// ⚠️ 为什么值得花这 9 秒：现场"中心换 IP 了、客户端钉在旧地址上"是一种很常见的死局 ——
    /// 日志里只有"连接失败"，人会以为是网络问题。把发现的地址写出来，
    /// 运维照着它在本机页上改一下就好了。
    /// </para>
    /// </summary>
    private async Task HintIfOtherServerExistsAsync(string pinned, CancellationToken cancellationToken)
    {
        if (!_options.AllowBroadcastDiscovery) return;
        if (_addressFailures < ServerAddressResolver.DiscoveryFallbackAfterFailures) return;

        var found = await DiscoverAsync(cancellationToken).ConfigureAwait(false);

        if (found is null) return;

        var url = BuildServerUrl(found);

        if (ServerAddressResolver.SameAddress(url, pinned)) return;

        NoteAddressOnce(
            $"本机被固定连接到 {pinned}（连不上），"
            + $"但局域网里还发现了一个中心 {url} —— 如果中心换过地址，请到本机配置页把地址改成它。");
    }

    /// <summary>广播发现（最多 <see cref="DiscoveryRounds"/> 轮）。<b>不写日志</b>：由调用方决定怎么说。</summary>
    private async Task<DiscoveredServer?> DiscoverAsync(CancellationToken cancellationToken)
    {
        var machineKey = ResolveMachineKey();

        for (var round = 1; round <= DiscoveryRounds && !cancellationToken.IsCancellationRequested; round++)
        {
            var found = await _discovery
                .DiscoverAsync(machineKey, AgentVersion.Current, DiscoveryTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (found is not null) return found;
        }

        return null;
    }

    /// <summary>
    /// 由发现结果拼地址。<b>只在这一处拼</b>：端口取自服务端应答（<c>HttpPort</c>），
    /// 客户端不猜 —— 猜错的形态是"发现成功了却连不上"，那种故障最难查。
    /// </summary>
    private static string BuildServerUrl(DiscoveredServer found)
        => $"http://{found.Address}:{found.Message.HttpPort}";

    private async Task<AgentCredentials?> EnsureRegisteredAsync(string serverUrl, CancellationToken cancellationToken)
    {
        // 注意：`??` 会短路，所以**不能**写成 `_cachedCredentials ?? _identity.Load(out var e)`
        // —— 那样 e 在缓存命中时根本没有赋值（编译期 CS0165）。
        var cached = _cachedCredentials;

        if (cached is null)
        {
            cached = _identity.Load(out var loadError);

            if (loadError is not null) _log.Warn("Agent", loadError);
        }

        _cachedCredentials = cached;

        var machineKey = ResolveMachineKey();

        if (cached is not null
            && string.Equals(cached.ServerUrl, serverUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(cached.MachineKey, machineKey, StringComparison.OrdinalIgnoreCase))
        {
            return cached;
        }

        if (cached is not null)
        {
            // 换了服务端（或机器码变了）：旧凭据在新服务端上毫无意义，直接作废重注册。
            _log.Info("Agent", "中心服务端或机器标识已变化，作废旧凭据并重新注册。");
            _identity.Clear();
            _cachedCredentials = null;
        }

        UpdateStatus($"正在向 {serverUrl} 注册…", connected: false);

        var info = HostInfoCollector.Collect(
            machineKey,
            AgentVersion.Current,
            _hardware,
            _options.DisplayName);

        // 免凭据准入（2026-10-13）：请求里只带主机信息，没有任何令牌 / 加入码。
        var request = new AgentRegisterRequest(info);

        var result = await _registrar.RegisterAsync(serverUrl, request, cancellationToken).ConfigureAwait(false);

        if (!result.Ok || result.AgentKey is null || result.AgentId is null)
        {
            LastError = result.Message;
            _log.Warn("Agent", $"注册失败（{result.Status}）：{result.Message}");

            UpdateStatus($"注册被拒绝：{result.Message}", connected: false);

            // 「在线表已满」是**必须人工介入**的状态（去中心移除不再使用的机器），
            // 反复重试只会把日志刷满 —— 单独放慢到 5 分钟档。
            //
            // ⚠️ 以前这里判的是 `unauthorized`（凭据不对）；免凭据之后那个状态不可能再出现，
            //    注册侧剩下的失败只有 invalid / toomany / blocked / error 四种。
            if (result.Status == "toomany")
            {
                _enrollmentFullUntil = DateTimeOffset.Now + EnrollmentFullRetry;
            }

            // 🔥 被拉黑（2026-10-15）：服务端在**注册**这一步就把门关上了。
            //    这里刻意**什么都不清**（本地身份、配置全留着）：拉黑是可逆的，
            //    清掉身份反而会让"取消拉黑"变成"必须重新注册"。
            //    只把状态和计时器推进 —— 15 秒后再来一次。
            if (result.Status == "blocked")
            {
                _blockedUntil = DateTimeOffset.Now + BlockedRetry;

                UpdateStatus("已被中心拉黑，等待管理员取消…", connected: false);
            }

            return null;
        }

        var credentials = new AgentCredentials(
            result.AgentId.Value,
            result.AgentKey,
            serverUrl,
            machineKey,
            DateTimeOffset.Now);

        _identity.Save(credentials);
        _cachedCredentials = credentials;

        // 注册应答里就带着服务端版本 —— 先记下来，本机页面立刻就能显示，
        // 不必等欢迎消息（那要等长连接建立，遇到"连不上"的情形就永远显示不出来）。
        _serverVersion = result.ServerVersion;
        TouchLastSeen();

        if (result.HeartbeatSeconds is { } heartbeat and > 0)
        {
            _options.HeartbeatSeconds = heartbeat;
        }

        _log.Info(
            "Agent",
            $"注册成功：Id={credentials.AgentId}，显示名 {result.DisplayName ?? "(未命名)"}，服务端版本 {result.ServerVersion}");

        return credentials;
    }

    /// <summary>
    /// 把"本机要换中心了"告诉**当前**中心（2026-10-15，需求①）。<b>尽力而为</b>。
    /// <para>
    /// 没有凭据、或者旧中心已经不可达时，只写一行日志就往下走 ——
    /// 这正是"只通知，不阻塞保存"的落点。
    /// </para>
    /// </summary>
    private async Task ReportServerChangeAsync(CancellationToken cancellationToken)
    {
        var newUrl = _serverOverride?.Url;

        var cached = _cachedCredentials;
        var key = CurrentAgentKey();

        if (cached is null || key is null)
        {
            _log.Info("Agent", $"本机尚未与任何中心建立过连接，无需上报；直接按新地址 {newUrl} 连接。");
            return;
        }

        var outcome = await _registrar
            .ReportServerChangeAsync(cached.ServerUrl, cached.AgentId, key, newUrl ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        _log.Info("Agent", $"本机服务端地址变更为 {newUrl}：{outcome}");
    }

    /// <summary>
    /// 中心拒收了本机凭据之后的**分流处置**（2026-10-15）。
    /// <para>
    /// 之所以把"重新注册"当成探测器：中心拒收只有两种原因，而正确处置是**反的**
    /// （见调用点的长注释）——
    /// </para>
    /// <list type="bullet">
    ///   <item>记录被「移除」⇒ 注册成功，换一把新钥匙（服务端会新建一行，<b>新 Id</b>）；</item>
    ///   <item>本机被「拉黑」⇒ 注册被拒（<c>status=blocked</c>），<b>本地身份原封不动</b>，
    ///         交给主循环按 15 秒轮询 —— 管理员一点「取消拉黑」就恢复。</item>
    /// </list>
    /// <para>
    /// ⚠️ <b>刻意"先注册、再决定要不要清"</b>：<b>不在调用前清身份</b>。
    /// 先清的话，"被拉黑"这条路上身份已经没了 —— 而拉黑期间又注册不进来 ⇒
    /// 取消拉黑之后它还得多走一次注册，甚至可能因为注册被拒而**永远回不来**。
    /// </para>
    /// </summary>
    /// <returns>是否拿到了可用的新凭据（仅用于日志与状态，调用方一律按"下一轮再说"处理）。</returns>
    private async Task<bool> TryRecoverIdentityAsync(AgentCredentials stale, CancellationToken cancellationToken)
    {
        UpdateStatus("中心已不再接受本机凭据，正在重新注册…", connected: false);

        var machineKey = ResolveMachineKey();

        var info = HostInfoCollector.Collect(
            machineKey,
            AgentVersion.Current,
            _hardware,
            _options.DisplayName);

        var result = await _registrar
            .RegisterAsync(stale.ServerUrl, new AgentRegisterRequest(info), cancellationToken)
            .ConfigureAwait(false);

        if (result.Ok && result.AgentKey is not null && result.AgentId is not null)
        {
            var fresh = new AgentCredentials(
                result.AgentId.Value,
                result.AgentKey,
                stale.ServerUrl,
                machineKey,
                DateTimeOffset.Now);

            _identity.Save(fresh);
            _cachedCredentials = fresh;

            // 旧钥匙在飞的传输全部作废（它们带着的是 stale.AgentKey）。
            // （清掉 ≠ 删掉已完成的部分：下一轮连上后按 `.part` 长度续传。）
            _transfers?.ClearSession();

            _serverVersion = result.ServerVersion;
            TouchLastSeen();

            if (result.HeartbeatSeconds is { } heartbeat and > 0)
            {
                _options.HeartbeatSeconds = heartbeat;
            }

            _blockedUntil = DateTimeOffset.MinValue;
            LastError = null;

            // ⚠️ 措辞必须与 EnsureRegisteredAsync 里那句**保持同一个前缀**（"注册成功"）：
            //    冒烟 H 段就是靠数这个前缀来确认"它真的重新注册过"，
            //    而不是"列表里碰巧又有一台机器"。
            _log.Info(
                "Agent",
                $"注册成功：Id={fresh.AgentId}，显示名 {result.DisplayName ?? "(未命名)"}，"
                + $"服务端版本 {result.ServerVersion}｜原因：中心已不再接受本机凭据（记录被移除或凭据被作废）。");

            return true;
        }

        if (result.Status == "blocked")
        {
            // 被管理员拉黑：**身份、配置、任务全部保留**，只进"被拉黑"状态。
            _blockedUntil = DateTimeOffset.Now + BlockedRetry;
            LastError = "已被中心拉黑。";
            UpdateStatus("已被中心拉黑，等待管理员取消…", connected: false);

            _log.Warn("Agent", $"本机已被中心拉黑，暂停连接（每 {BlockedRetry.TotalSeconds:0} 秒重试一次）：{result.Message}");

            return false;
        }

        // 其余（网络不通 / 在线表满 / 服务端写入失败）：**什么都不清** ——
        // 一次网络抖动不该让我们丢掉一份可能还有效的凭据。
        LastError = result.Message;
        UpdateStatus($"重新注册失败：{result.Message}", connected: false);

        return false;
    }

    /// <summary>
    /// 主动断开长连接（被拉黑时用），<b>吞掉一切异常</b>。
    /// <para>
    /// 断开失败不值得记录：那条连接的结局已经定了（中心不再承认它），
    /// 而这里跑在 SignalR 的回调里 —— 抛出去只会变成一个没有信息量的
    /// "连接异常断开"。真正的状态由主循环按 15 秒轮询决定。
    /// </para>
    /// </summary>
    private static async Task StopConnectionQuietlyAsync(AgentConnection connection)
    {
        try
        {
            await connection.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // 见上面的注释：这是"尽力而为"的一步。
        }
    }

    /// <summary>连上并维持心跳。</summary>
    /// <returns>是否成功建立过连接（用于决定要不要重置退避）。</returns>
    private async Task<bool> RunConnectedAsync(AgentCredentials credentials, CancellationToken cancellationToken)
    {
        await using var connection = new AgentConnection(credentials.ServerUrl, credentials.AgentKey, _log);

        var heartbeat = TimeSpan.FromSeconds(Math.Max(2, _options.HeartbeatSeconds));

        void OnWelcome(AgentWelcomeDto welcome)
        {
            if (welcome.HeartbeatSeconds > 0)
            {
                heartbeat = TimeSpan.FromSeconds(welcome.HeartbeatSeconds);
            }

            // ── 遥测节奏由服务端下发（M4.2）──
            // 直接改写那个单例配置对象：采样循环每个 tick 都重新读它，
            // 所以"服务端改一个数 = 全网客户机的节奏跟着变"，不必重装客户机。
            // 与 HeartbeatSeconds 的处理方式一致（同一个理由：下发优先于本地配置）。
            if (welcome.Telemetry is { } telemetry)
            {
                var options = _options.Telemetry;

                options.Enabled = telemetry.Enabled;
                options.MetricsSeconds = telemetry.MetricsSeconds;
                options.PowerEventPollSeconds = telemetry.PowerEventPollSeconds;
                options.InventoryHours = telemetry.InventoryHours;
                options.PowerEventBackfillDays = telemetry.PowerEventBackfillDays;

                _log.Info(
                    "Agent",
                    telemetry.Enabled
                        ? $"服务端下发遥测节奏：指标 {telemetry.MetricsSeconds} 秒 / 开关机轮询 {telemetry.PowerEventPollSeconds} 秒 / 清单 {telemetry.InventoryHours} 小时。"
                        : "服务端已停用遥测上报。");
            }

            DisplayName = welcome.DisplayName;
            _serverVersion = welcome.ServerVersion;
            TouchLastSeen();

            // ── 服务端说"我这边没有这台机器的硬件清单" ⇒ **立刻补报一份**（2026-10-15）──
            //
            // 🔥 这是"客户机不上报硬件清单"那个 BUG 的落点，而且它**只在重新注册之后出现**：
            //    清单的排期由**本地持久化**的"上次上报时刻"决定（周期 6 小时），
            //    可服务端那边的记录（连同清单）可能已经被删掉 ——
            //    管理员「移除」了这台机器、或它重新注册拿到了新 Id。
            //    客户端不知道这件事，于是照旧等满 6 小时 ⇒ 新记录上永远没有硬件清单。
            //
            // 之所以由服务端在 Welcome 里说，而不是客户端自己"每次注册都重报一遍"：
            // 后者会让**每一次**服务重启 / 网络抖动的重连都去跑一遍 WMI 采清单
            // （1～2 秒 CPU，见 InventoryRetryInterval 的注释）—— 那正是当初要避免的代价。
            if (welcome.NeedsInventory)
            {
                _inventoryRequested = true;

                _log.Info(
                    "Agent",
                    "中心那边没有本机的硬件清单（多半是记录被移除过 / 重新注册换了新 Id）—— 立刻补报一份。");
            }

            _log.Info(
                "Agent",
                $"服务端已确认连接：AgentId={welcome.AgentId}，心跳 {heartbeat.TotalSeconds:0} 秒，服务端版本 {welcome.ServerVersion}");

            // 记一条"我刚连上"：排查"指令没反应"时，第一个要看的就是
            // "那台机器当时到底连没连上"，而这一条正是那个问题的直接答案。
            _received.Add(
                AgentReceivedKinds.SignalR,
                "中心服务端",
                $"Welcome（AgentId={welcome.AgentId}，配置版本 v{welcome.ConfigVersion}）",
                AgentReceivedOutcomes.Accepted,
                $"连接已建立，心跳 {heartbeat.TotalSeconds:0} 秒。");

            // ── 配置对齐（M4.5）：Welcome 里带着服务端当前的配置版本 ──
            // 这是"推送 + 版本对齐"两条腿里的第二条：推送丢了、机器离线期间改过配置，
            // 都在这里被发现并补齐。刻意不 await —— 它是"连上之后顺手对个版本"，
            // 不该拖住心跳循环的启动（SyncConfigAsync 内部全量捕获异常）。
            _ = SyncConfigAsync(credentials, welcome.ConfigVersion, force: false, connection, cancellationToken);

            // ── 本机编码能力：连上就报一次（M5.0 第二批）──
            //
            // 🔥 每次连接都报，而不是"探测结果变了才报"：中心那份是**内存态**，
            //    它一重启就空了 —— 好在重启必然断开所有长连接，
            //    于是每台机器都会重连、并在这里把结论重新报一遍，自愈不需要额外的机制。
            //
            // 刻意不 await：它与"连接可用"无关，报不上去也不该拖住心跳循环的启动
            // （方法内部自带全量异常捕获，见 ReportEncoderCapabilityAsync）。
            _ = ReportEncoderCapabilityAsync(connection, cancellationToken);

            // ── 远控的出口挂到这条连接上（M5.1）──
            //
            // 🔥 必须在**连上之后、且在这条连接还活着的时候**挂：码流的每一帧都要经
            //    `AgentConnection` 出去，而 `AgentConnection` 是**每轮连接新建**的
            //    （见 RunConnectedAsync 里的 `await using var connection`）。
            //    把它当成"启动时挂一次"的长期对象，重连之后就再也送不出帧了 ——
            //    而表现是"中心显示在线、画面却停了"。
            AttachRemoteSession(connection);
        }

        void OnCommand(AgentCommandDto command)
        {
            _log.Info("Agent", $"收到服务端指令：{command.Name}");

            // ── 🔥 「电源 / 音量」就地执行（2026-10-15 修）──
            //
            // 这两条以前**没有任何人处理**：这里只认 RefreshConfig，其余一律转交给宿主，
            // 而宿主的 switch 里没有 power / volume ⇒ 落到 default ⇒ 「收到未知指令（已忽略）」。
            // 现场看到的是"中心说已下发、机器毫无反应" —— 两边都没错，各自相信了不同的真相，
            // 而真正的证据（"已忽略"）只躺在客户机日志里。
            if (command.Name is AgentCommandNames.Power or AgentCommandNames.Volume
                or AgentCommandNames.RemoteSession)
            {
                _ = ExecuteLocallyAsync(command);
                return;
            }

            // 先记"收到了"再动手：这条指令如果让进程重启（restart），
            // 等到动手之后再记就永远记不上了 —— 而那恰恰是最需要留痕的一条。
            _received.Add(
                AgentReceivedKinds.SignalR,
                "中心服务端",
                DescribeCommand(command),
                AgentReceivedOutcomes.Accepted,
                "已转交本机指令处理。");

            // ── 「重新拉取配置」（M4.5）──
            // 这条指令**刻意不看版本号**：它就是为"版本没变但机器看起来没按配置动"准备的
            // （运维在页面上点「重新下发配置」）。看完版本再决定拉不拉，等于把这条指令废掉。
            if (command.Name == AgentCommandNames.RefreshConfig)
            {
                _ = SyncConfigAsync(credentials, knownVersion: null, force: true, connection, cancellationToken);
            }

            try
            {
                CommandReceived?.Invoke(command);
            }
            catch (Exception ex)
            {
                _log.Error("Agent", $"处理服务端指令 {command.Name} 时出错", ex);
            }
        }

        /**
         * 就地执行一条「电源 / 音量」指令，并把**真实结论**写进「接收到的数据」。
         *
         * ⚠️ 这里写的是结论（已执行 / 失败 / 演练模式未执行），**不是"已收到"**：
         *    页面上的「接收到的数据」是现场唯一能回答"到底做成没有"的地方，
         *    写一句"已转交本机指令处理"等于什么都没说 —— 那正是这次故障被掩盖掉的原因。
         *
         * ⚠️ 是 fire-and-forget（`_ =`），所以函数体**必须**自己吞掉一切异常：
         *    SignalR 的回调里抛出去的异常不会有人看见，只会表现为"指令没反应"。
         */
        async Task ExecuteLocallyAsync(AgentCommandDto command)
        {
            var raw = DescribeCommand(command);

            try
            {
                // 用 None 而不是主循环的那个 token：指令已经收到了就该执行完，
                // 主循环正在停机不该把一条已受理的关机指令半途取消。
                var outcome = await _commands.ExecuteAsync(
                    command.Name,
                    command.Argument,
                    "中心服务端下发的指令",
                    "Agent",
                    CancellationToken.None).ConfigureAwait(false);

                _received.Add(AgentReceivedKinds.SignalR, "中心服务端", raw, outcome.Outcome, outcome.Detail);
            }
            catch (Exception ex)
            {
                _log.Error("Agent", $"执行服务端指令 {command.Name} 时出错", ex);

                _received.Add(
                    AgentReceivedKinds.SignalR,
                    "中心服务端",
                    raw,
                    AgentReceivedOutcomes.Failed,
                    $"执行指令时出错：{ex.Message}");
            }
        }

        /** 指令在留痕里长什么样（"power:shutdown"，无参数时就是 "power"）。 */
        static string DescribeCommand(AgentCommandDto command)
            => string.IsNullOrEmpty(command.Argument) ? command.Name : $"{command.Name}:{command.Argument}";

        void OnConfigChanged(AgentConfigChangedDto changed)
        {
            _log.Info("Agent", $"服务端通知配置已变更为 v{changed.Version}。");

            _received.Add(
                AgentReceivedKinds.SignalR,
                "中心服务端",
                $"ConfigChanged（v{changed.Version}）",
                AgentReceivedOutcomes.Accepted,
                "已开始重新拉取并应用配置。");

            _ = SyncConfigAsync(credentials, changed.Version, force: false, connection, cancellationToken);
        }

        // ── 🔥 本机被管理员**拉黑**（2026-10-15，取代原「吊销」）──
        //
        // 与旧吊销最关键的一处差别：**什么都不清**。
        //
        // 旧吊销在这里 `_identity.Clear()` + `_cachedCredentials = null`，于是那台机器
        // 只剩"重新注册"一条路 —— 而重新注册会把吊销标记清掉，等于**吊销自己解除了自己**
        // （这正是上一轮记录在案的"吊销很弱"）。拉黑是管理员说了算的开关，所以：
        //   · 本地身份、配置、任务编排**全部保留**（它只是暂时进不来，不是被判了死刑）；
        //   · 客户端进"被拉黑"状态，按 15 秒轮询重试；
        //   · 管理员一点「取消拉黑」，**下一次重试就直接连上**（不需要重新注册、
        //     也不需要在客户机上做任何事）。
        //
        // ⚠️ 刻意**不** `_tasks?.Stop()`、**不** `_configs?.Clear()`：一旦清了配置与
        //    `_reportedConfigVersion`，重新连上时版本号会对齐到 0 ⇒ Welcome 带回的版本
        //    与本地一致 ⇒ **配置不会被重新应用** ⇒ 任务停在那儿再也不会起来。
        //    （旧吊销靠"重新注册拿新 Id ⇒ 服务端版本从 0 开始"绕过了这个问题，
        //      而拉黑不重新注册，那条路走不通。）
        void OnBlocked(string reason)
        {
            _log.Warn("Agent", $"本机已被管理员拉黑（中心不再与本机建立连接）：{reason}");

            _received.Add(
                AgentReceivedKinds.SignalR,
                "中心服务端",
                "Blocked",
                AgentReceivedOutcomes.Executed,
                $"本机已被管理员拉黑，中心不再与本机连接：{reason}");

            _blockedUntil = DateTimeOffset.Now + BlockedRetry;

            // 中止在飞的传输：这些 HTTP 请求同样会撞上 401（认证层一律拒绝），
            // 留着只会让它们反复失败、把日志刷满。
            // （清掉 ≠ 删掉已完成的部分：取消拉黑后按 `.part` 长度续传。）
            _transfers?.ClearSession();

            // UDP 通道：**照旧"按未连接重新决策一次"**（2026-10-14 需求①的规则）。
            //
            // 拉黑 ≠ 该失去 UDP 控制：被拉黑的机器往往正是那台需要被现场工具立刻关掉的机器
            // （被误操作拉黑 / 已经归别的中心管）。让它连"关机"都做不到才是最糟的。
            //
            // ⚠️ 显式传 `connected: false`：这一刻 `Connected` 还是 true（长连接正在收尾）。
            ApplyUdpChannel(_lastUdpConfig, connected: false);

            // 主动断开：服务端那边下一次心跳也会把它掐掉（≤10 秒），
            // 但这里是"管理员刚点的按钮"，没必要让对方再等一个心跳周期。
            _ = StopConnectionQuietlyAsync(connection);
        }

        connection.WelcomeReceived += OnWelcome;
        connection.CommandReceived += OnCommand;
        connection.Blocked += OnBlocked;
        connection.TransferTaskReceived += OnTransferTask;
        connection.ConfigChanged += OnConfigChanged;

        // ── 目录浏览能力（M4.4+）──
        // 一次连接装配一次：本机没启用文件传输时**不挂**，于是服务端拿到的是
        // "本客户端不支持目录浏览"这个明确答复，而不是一次没有信息量的调用失败。
        // 不挂也不设空委托：`DirectoryBrowser` 为 null 正是"没这个能力"的表示。
        if (_browse is not null) connection.DirectoryBrowser = _browse.Browse;

        // ── 「接收到的数据」能力（2026-10-13）──
        // 与上面相反，这里**无条件挂上**：它不依赖任何可选能力，
        // 环形缓冲是构造 AgentRuntime 就连带进来的。中心想看随时能看。
        connection.ReceivedDataProvider = SnapshotReceived;

        try
        {
            await connection.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            // ── 🔥 中心**明确不认这把钥匙**了 ⇒ 作废本地身份，下一轮重新注册（2026-10-14 修）──
            //
            // 触发它的现实场景只有一个，而且很常见：管理员在网页上把一台客户机「移除」。
            // 那一刻服务端把记录（连同 agentKey 的哈希）**硬删**了，于是本地这份凭据
            // 在服务端谁也认不出来，重连必然 401。
            //
            // 原来的做法是把它当普通连接失败：`EnsureRegisteredAsync` 一看"本地凭据里的
            // serverUrl + machineKey 与当前一致"就直接复用 ⇒ 拿着这把废钥匙无限重试
            // （退避到 60 秒封顶）。**连重启客户端都救不回来** —— 本地凭据还在，重启后照样 401。
            // 现场表现就是这条 bug："移除之后无法再次连接添加"。
            //
            // 判定交给 <see cref="AgentRegistrar.IsCredentialRejectedAsync"/>（拿一次普通 HTTP
            // 请求去问中心），而不是去猜 SignalR 异常的形状 —— 理由见那个方法的注释。
            //
            // 🔥 2026-10-20：**先按异常性质分流，再决定要不要问**。
            //    "连不上"（拒绝连接 / 主机不可达 / 超时）时对面连话都说不上，
            //    那一问注定超时 —— 而它每轮都要占满一个 20 秒的 HttpClient 超时，
            //    于是"中心起来了、客户端要一分多钟才上线"（现场日志 09:16–09:20 的节奏）。
            //    401 / 403 则相反：结论已经在手上了，连问都不用问。
            var failureKind = ServerConnectFailure.Classify(ex);

            var credentialRejected = failureKind == ServerConnectFailureKind.Rejected
                || (failureKind == ServerConnectFailureKind.Unknown
                    && await _registrar
                        .IsCredentialRejectedAsync(
                            credentials.ServerUrl,
                            credentials.AgentId,
                            credentials.AgentKey,
                            cancellationToken)
                        .ConfigureAwait(false));

            if (credentialRejected)
            {
                // 🔥 2026-10-15：从"判定被拒 ⇒ 作废身份"改成"**直接去注册一次，按结果分流**"。
                //
                // 为什么不能再无脑清身份：中心拒收这把钥匙有**两种**截然不同的原因，
                // 而它们的正确处置是反的 ——
                //   · 记录被「移除」⇒ 该清身份、重新注册拿新钥匙（上一轮修的那条）；
                //   · 本机被「拉黑」⇒ **绝不能清身份**：拉黑是可逆开关，密钥一直有效，
                //     清掉之后"取消拉黑"就得靠重新注册，而重新注册同样会被拒 ⇒ 死循环。
                //
                // 而"注册"这一步本身就是最好的探测器：服务端在**注册**入口就把被拉黑的
                // 挡在外面（`AgentEnrollmentService` 返回 status=blocked），
                // 于是同一个请求既完成了"重新拿钥匙"，也回答了"我到底是什么状态"。
                // 少一个端点、少一次往返，也不会出现"探测说没事、注册却失败"的不同步。
                await TryRecoverIdentityAsync(credentials, cancellationToken).ConfigureAwait(false);

                // 一律返回 false ⇒ 交给主循环退避重试。
                // 成功的话下一轮会拿着新钥匙连上；被拉黑的话主循环会按 15 秒轮询。
                return false;
            }

            LastError = ex.Message;
            _log.Warn("Agent", $"连接中心服务端失败：{ex.Message}");
            UpdateStatus($"连接失败：{ex.Message}", connected: false);
            return false;
        }

        Connected = true;
        LastError = null;
        UpdateStatus($"已连接 {credentials.ServerUrl}", connected: true);

        // ── UDP 指令通道：连上之后再补一次 Apply ──
        // 必须在这里补，因为它是"配置在缓存里、服务端版本又没变"那条路径上唯一的机会：
        // 那种情况下 `ConfigChanged` 不会来、`Applied` 事件也不会再触发，
        // 光靠 OnConfigApplied 的话通道会一直停着（表现为"重启一次之后 UDP 就不灵了"）。
        //
        // ⚠️ 2026-10-13 起这里**不再需要 agentKey**（报文改为纯明文，不再派生通道密钥），
        //    所以这一步纯粹是"再对齐一次配置"，与凭据无关。
        ApplyUdpChannel(_configs?.Current);

        // ── 文件传输：绑定会话 + 补领（M4.4）──
        // 凭据只在连上之后才有效。绑定给**进程级**的工作线程后，即使这条长连接随后断开，
        // 正在跑的传输也能用同一份凭据继续（HTTP 那条路跟 SignalR 没关系）。
        if (_transfers is not null)
        {
            _transfers.SetSession(credentials.ServerUrl, credentials.AgentKey);

            // 补领刻意**不 await**：它是"连上之后顺手补个课"，不该拖住心跳循环的启动；
            // 方法内部自带 30 秒超时与全量异常捕获（见 CatchupTransfersAsync）。
            _ = CatchupTransfersAsync(credentials);
        }

        // ── 遥测上报与心跳**并行**跑（M4.2）──
        // 心跳被遥测的 IO 拖住会让服务端误判这台机器离线（那会连带影响"在线/离线"列表）；
        // 反过来，"上报慢几秒"最多让曲线晚几秒。两者的代价完全不对称，所以不能串在一个循环里。
        using var telemetryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var telemetryTask = RunReporterAsync(connection, telemetryCts.Token);

        // 配置上行（2026-10-14）：本机改过配置就推给中心。与心跳**并行**、1 秒节拍，
        // 理由同遥测：拿心跳间隔（默认 10 秒，还是服务端下发的）当推送节流，
        // 会让"本机页面改完 → 中心页面变化"隔上十来秒，而这一步用户的预期是"实时"。
        var configPushTask = RunConfigPushLoopAsync(connection, telemetryCts.Token);

        while (!cancellationToken.IsCancellationRequested
               && connection.State == HubConnectionState.Connected)
        {
            // ── 本机页刚改了服务端地址 ⇒ **立刻断开**去接新中心（2026-10-15，需求①）──
            //
            // 不这么做的话，改完地址要等这条长连接自然断掉才生效 —— 而那可能是几小时之后
            // （只要心跳还通，它就一直连着）。用户点完"保存"却什么都没发生，是最糟的反馈。
            //
            // ⚠️ 这里只**看**（ChangePending）、不消费：消费在主循环里 ——
            //    它还要负责"上报旧中心 + 作废凭据"那一整套动作。
            if (_serverOverride is { ChangePending: true }) break;

            var ack = await connection.HeartbeatAsync(AgentVersion.Current, cancellationToken).ConfigureAwait(false);

            // ── 刷新「最后通信时间」（2026-10-14）──
            // `HeartbeatAsync` 失败时返回 null（它内部吞掉异常），所以非 null 就是"中心确实回话了"。
            // 这是本机页面那张「服务端」卡片上唯一会**一直往前走**的字段 ——
            // 它停住了，就等于"这台机器已经很久没跟中心说上话了"。
            if (ack is not null) TouchLastSeen();

            // ── 顺带把"本机 UDP 通道此刻在不在听"告诉中心（2026-10-14）──
            //
            // ⚠️ 它是**独立的一条消息**，不能塞进心跳：实测 SignalR 要求实参个数与签名严格一致，
            //    给心跳加参数会让旧客户端的心跳整条失败（见 AgentConnection.HeartbeatAsync 的备注）。
            //
            // 🔥 为什么**每一跳都发**而不是"变了才发"：
            //    · 中心是进程内保存这份状态的，它一重启就回到了"不知道"——
            //      只在变化时发，那台机器就得等下一次"恰好变化"才恢复，可能是一整天；
            //    · 报文很小（四个字段），每 10 秒一条比一次断线重连便宜得多。
            // 失败不影响心跳：ReportUdpChannelAsync 内部吞掉一切异常并返回 null
            // （旧服务端不认识这个方法，那时会抛 —— 那正是要吞掉的情形）。
            try
            {
                await connection
                    .ReportUdpChannelAsync(CurrentUdpState(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 铁律 #6：这里绝不能把主循环带崩（上报失败只是"中心暂时不知道状态"）。
                _log.Warn("Agent", $"上报 UDP 通道状态失败（下次心跳会再试）：{ex.Message}");
            }

            // ── 心跳里顺带做两件配置相关的小事（M4.5）──
            // 都是"顺手"，不能影响心跳本身的节奏：都只做本地判断，真正的网络动作丢给后台。
            if (_configs is not null)
            {
                var applied = _configs.AppliedVersion;

                if (applied > 0 && applied != _reportedConfigVersion && _configs.IsVersionKnownToServer(applied))
                {
                    // ① 补报：上一次"应用成功但回报失败"（那一刻连接刚好断了）。
                    //    没有这一步，页面会永远停在"已下发 v5 / 已应用 v4"。
                    //
                    // ⚠️ 多一个 `IsVersionKnownToServer` 闸门：本机页面改出来的版本号
                    //    （中心还不知道）不回报 —— 否则中心页面上会冒出"客户机回报的版本比服务端还高"，
                    //    那条告警的含义是"有人改过库"，用它来描述"本机自己改了一版"是误导。
                    //    推给中心（#219 的双向同步）之后中心就认识它了，那时自然会补报。
                    _ = ReportAppliedAsync(connection, applied, cancellationToken);
                }
                else if (ack is { ConfigVersion: > 0 } && ack.ConfigVersion > applied)
                {
                    // ② 版本对齐：服务端比本机新（推送丢了 / 本机离线期间改过配置）。
                    _ = SyncConfigAsync(credentials, ack.ConfigVersion, force: false, connection, cancellationToken);
                }
            }

            try
            {
                await Task.Delay(heartbeat, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Connected = false;

        // ── 🔥 断线 ⇒ 立刻按"未连接中心"重新决策 UDP 通道（2026-10-14，需求①）──
        //
        // 这一行是需求① 在**运行中**的那一半：连上时的配置可能写了 `Enabled=false`
        // （中心不让这台机器听），那一刻中心还在，尊重配置没问题；但**中心一断**，
        // "现场还能不能关机"就变成了更要紧的事 —— 于是这条通道必须自己开起来。
        //
        // ⚠️ 用 `_lastUdpConfig` 而不是 `_configs.Current`：本机可能还没有过配置（那是 null），
        //    而端口要沿用的正是"上一次知道的那份"（见 _lastUdpConfig 的注释）。
        // ⚠️ 端口与黑名单没变时它只刷状态文案，**不会重绑端口** —— 一次网络抖动不该
        //    在端口上留一个收包空窗（见 UdpCommandListener.Listen）。
        ApplyUdpChannel(_lastUdpConfig);

        // 先停上报再断连接：反过来的话，正在飞行中的 InvokeAsync 会拿到
        // "连接已释放"这类没有信息量的异常，掩盖真正的断开原因。
        telemetryCts.Cancel();

        try
        {
            await telemetryTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常路径。
        }
        catch (Exception ex)
        {
            _log.Warn("Agent", $"遥测上报循环退出时异常：{ex.Message}");
        }

        try
        {
            await configPushTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常路径。
        }
        catch (Exception ex)
        {
            _log.Warn("Agent", $"配置上行循环退出时异常：{ex.Message}");
        }

        connection.WelcomeReceived -= OnWelcome;
        connection.CommandReceived -= OnCommand;
        connection.Blocked -= OnBlocked;
        connection.TransferTaskReceived -= OnTransferTask;
        connection.ConfigChanged -= OnConfigChanged;

        // 摘掉浏览能力：连接就要释放了，别留一个"还能被调用"的委托（它捕获着本机路径信息）。
        connection.DirectoryBrowser = null;

        // ⚠️ 刻意**不**清除传输会话：SignalR 断了不代表 HTTP 断了。
        // 会话只在"被拉黑"（OnBlocked）与"进程停机"（DisposeAsync）时清。
        return true;
    }

    /// <summary>
    /// 收到一条派发过来的传输任务（M4.4）。<b>只入队，不在这里搬字节</b> ——
    /// SignalR 的回调运行在它自己的调度上，在这里干长活会把心跳挤住。
    /// </summary>
    private void OnTransferTask(AgentTransferTaskDto task)
    {
        var direction = task.Direction == DistributionDirections.Push ? "下发" : "回传";

        if (_transfers is null)
        {
            // ⚠️ 这条以前是**静默丢弃**的：中心那边看到"任务一直 pending"，
            //    而客户机日志里一个字都没有 —— "指令不生效"最难查的样子就是这个。
            _received.Add(
                AgentReceivedKinds.SignalR,
                "中心服务端",
                $"PushTransferTask（{direction} {task.FileName}，任务 {task.TaskId}）",
                AgentReceivedOutcomes.Ignored,
                "本机未启用文件传输（Agent:Transfer:Enabled=false），任务已忽略。");

            return;
        }

        _log.Info(
            "Agent",
            $"收到传输任务：{direction}"
            + $" {task.FileName}（任务 {task.TaskId}，条目 {task.ItemId}）");

        _received.Add(
            AgentReceivedKinds.SignalR,
            "中心服务端",
            $"PushTransferTask（{direction} {task.FileName}，任务 {task.TaskId}）",
            AgentReceivedOutcomes.Accepted,
            "已入队，由本机传输线程处理。");

        _transfers.Enqueue(task);
    }

    /// <summary>
    /// 本机 UDP 指令通道<b>此刻</b>的状态（现读 <see cref="_udp"/>，不缓存）。
    /// <para>
    /// 🔥 <b>三处共用这一份判断</b>：本机页面、中心详情页（「接收到的数据」）、随心跳上报的
    /// <see cref="AgentUdpChannelStateDto"/>。各写一遍必然漂移 —— 而这三处不一致时，
    /// 现场看到的正是"页面说在听、指令却没反应"这类说不清的现象。
    /// </para>
    /// <para>
    /// <c>_udp</c> 为 null = 本机连通道都没装配（任务下发整体停用），
    /// 此时要给一句<b>明确的原因</b>而不是一个空的 false。
    /// </para>
    /// </summary>
    private (bool Listening, int? Port, string StateText, bool ExecuteEnabled) UdpChannelState()
        => (
            _udp?.IsRunning ?? false,
            _udp?.Port,
            _udp?.StateText
                ?? "本机已停用任务下发（Agent:Tasks:Enabled=false）—— UDP 指令通道不会启动，任何指令都收不到。",
            _udp?.ExecuteEnabled ?? false);

    /// <summary>
    /// 给本机页面 / 中心详情页的一份「接收到的数据」快照（2026-10-13）。
    /// <para>
    /// 通道状态从 <see cref="UdpChannelState"/> 现读，<b>不缓存</b> —— 这个页面存在的意义就是
    /// "此刻到底是什么状态"，缓存一份等于再造一个可能过期的真相。
    /// </para>
    /// </summary>
    private AgentReceivedDataDto SnapshotReceived()
    {
        var (listening, port, stateText, executeEnabled) = UdpChannelState();

        return _received.Snapshot(
            udpListening: listening,
            udpPort: port,
            udpStateText: stateText,
            udpExecuteEnabled: executeEnabled);
    }

    /// <summary>
    /// 随心跳上报的 UDP 通道状态（2026-10-14）。
    /// <para>
    /// 每跳<b>现读一次</b>：通道可能刚被改了端口、刚启动失败、刚被本机停用 ——
    /// 拿一份缓存上报，中心看到的就还是上一分钟的那个真相。
    /// </para>
    /// </summary>
    private AgentUdpChannelStateDto CurrentUdpState()
    {
        var (listening, port, stateText, executeEnabled) = UdpChannelState();

        return new AgentUdpChannelStateDto(listening, port, stateText, executeEnabled);
    }

    /// <summary>
    /// 补领本机未完成的传输任务（M4.4）。
    /// <para>
    /// <b>这是"离线机器也能收到文件"的落地</b>：建单时不在线的机器推不动 SignalR，
    /// 但明细已经为它建好了（<c>pending</c>），上线后由它自己拉走。
    /// </para>
    /// <para>
    /// 自带超时、自己吞掉所有异常：它只是"顺手补个课"，失败了下次重连还会再补一次，
    /// <b>绝不该影响心跳循环的节奏</b>。
    /// </para>
    /// </summary>
    private async Task CatchupTransfersAsync(AgentCredentials credentials)
    {
        if (_transfers is null) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            var client = new FileTransferClient(
                _http,
                _transferHttp!,
                credentials.ServerUrl,
                credentials.AgentKey,
                _options.Transfer,
                _stateDirectory,
                _log);

            var pending = await client.ListPendingAsync(cts.Token).ConfigureAwait(false);

            if (pending.Count == 0) return;

            _log.Info("Agent", $"补领到 {pending.Count} 条未完成的传输任务。");

            _transfers.EnqueueRange(pending);
        }
        catch (Exception ex)
        {
            _log.Warn("Agent", $"补领传输任务失败（下次重连会再试）：{ex.Message}");
        }
    }

    /// <summary>
    /// 与服务端对齐一次配置（M4.5）。
    /// <para>
    /// <b>这里刻意不 await</b>：三个调用点（Welcome、ConfigChanged、心跳版本对齐）
    /// 全都在"不能被打断的循环"里，一次拉取慢上几秒会把心跳挤住 ——
    /// 而心跳被挤住的代价是服务端误判这台机器离线（那会污染整张在线表）。
    /// 方法内部自带全量异常捕获，不需要调用方再管。
    /// </para>
    /// </summary>
    private async Task SyncConfigAsync(
        AgentCredentials credentials,
        int? knownVersion,
        bool force,
        AgentConnection connection,
        CancellationToken cancellationToken)
    {
        if (_configs is null) return;

        ConfigSyncOutcome outcome;

        try
        {
            outcome = await _configs
                .SyncAsync(credentials.ServerUrl, credentials.AgentId, credentials.AgentKey, knownVersion, force, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.Warn("Agent", $"配置同步失败（保持现状）：{ex.Message}");
            return;
        }

        if (!outcome.Applied) return;

        // 应用成功之后立刻回报 —— 页面上的"已应用 vN"就是靠这一条。
        await ReportAppliedAsync(connection, outcome.Version, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 回报「配置已应用」。<b>只有服务端明确应答 ok 才推进本地水位</b>，
    /// 否则下一次心跳会再报一次（心跳处的补报逻辑）。
    /// </summary>
    private async Task ReportAppliedAsync(
        AgentConnection connection,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            var ack = await connection
                .ReportConfigAppliedAsync(new AgentConfigAppliedDto(version, true), cancellationToken)
                .ConfigureAwait(false);

            if (ack is { Ok: true })
            {
                _reportedConfigVersion = version;
                return;
            }

            _log.Warn("Agent", $"回报「已应用配置 v{version}」未获应答（下次心跳会再报一次）。");
        }
        catch (OperationCanceledException)
        {
            // 正常停机 / 重连：水位不推进，下次心跳补。
        }
        catch (Exception ex)
        {
            _log.Warn("Agent", $"回报「已应用配置 v{version}」出错：{ex.Message}");
        }
    }

    /// <summary>
    /// 配置上行循环（2026-10-14）：<b>本机改过配置就推给中心</b>，1 秒一拍。
    ///
    /// <para><b>为什么单独一条循环，而不是塞进心跳</b>：心跳间隔是服务端下发的（默认 10 秒），
    /// 拿它当推送节流会让"本机页面改完 → 中心页面变化"之间隔上十来秒，而这一步的预期是"实时"。
    /// 单独一条 1 秒循环的代价极小（绝大多数时候只是读一个 <c>bool</c>），换来的确定性高得多。</para>
    ///
    /// <para><b>为什么要"补推"而不是"保存那一刻直接推"</b>：本机页面保存的那一刻不一定连着中心
    ///（独立使用的客户机就是常态），而"待同步"这件事实必须能跨重启活下来（它落在配置文件里）。
    /// 这条循环把三种情形一并覆盖：离线改的、重启前改的、推失败过的。</para>
    /// </summary>
    private async Task RunConfigPushLoopAsync(AgentConnection connection, CancellationToken cancellationToken)
    {
        if (_configs is null) return;

        while (!cancellationToken.IsCancellationRequested
               && connection.State == HubConnectionState.Connected)
        {
            try
            {
                if (_configs.HasUnsyncedLocalEdit)
                {
                    await PushLocalConfigAsync(connection, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 铁律 #6：一次推送失败绝不能把这条循环带走 —— 下一拍还会再试。
                _log.Warn("Config", $"配置上行循环出错（将继续）：{ex.Message}");
            }

            try
            {
                await Task.Delay(TickInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 把<b>本机改过的那一版</b>推给中心（2026-10-14 的双向同步）。
    ///
    /// <para>三种结局都要处理干净，否则会留下"两边各跑一份"的中间状态：</para>
    /// <list type="bullet">
    ///   <item><b>没拿到应答</b> ⇒ 什么都不做，保留"待同步"，1 秒后的下一次心跳再推；</item>
    ///   <item><b>中心收下了</b> ⇒ 采纳回执里那份（中心可能 +1 版本、可能做了归一化），标志清掉；</item>
    ///   <item><b>中心更晚</b>（<c>conflict</c>）⇒ 这就是"后写覆盖"里落败的一方：
    ///         当场改用中心那份，而不是让两边继续各跑一份。</item>
    /// </list>
    ///
    /// <para>⚠️ 回执里那份内容与本机一致时，内部会认出"内容没变"而<b>不重起任务</b>
    /// （见 <c>AgentConfigManager.Apply</c>）—— 否则"本机改一次"会变成"任务被重启两遍"
    /// （一次是本地生效，一次是中心回执），循环发送的计时会被平白重置。</para>
    /// </summary>
    private async Task PushLocalConfigAsync(AgentConnection connection, CancellationToken cancellationToken)
    {
        if (_configs is null) return;

        var current = _configs.Current;

        if (current is null) return;

        try
        {
            var pushed = new AgentConfigPushDto(
                current.Version,
                current.UpdatedAt,
                AgentConfigMapper.ToSaveRequest(current));

            var result = await connection.PushConfigAsync(pushed, cancellationToken).ConfigureAwait(false);

            if (result is null) return;

            if (result.Config is not null)
            {
                if (result.Ok)
                {
                    _log.Info("Config", $"本机改动已同步到中心（中心 v{result.Config.Version}）。");
                }
                else
                {
                    _log.Info(
                        "Config",
                        $"中心那份比本机改动更晚（中心 v{result.Config.Version}），已改用中心那份。");
                }

                _configs.AcceptServerConfig(result.Config);
                return;
            }

            // ── 明确被回绝，且重试不会有不同结果 ──
            // 除了"停止重推"没有别的处置：本机那份是用户在页面上亲手写的，不该被悄悄丢掉
            //（机器继续按它跑），但也不能 1 秒一次地重推 —— 那只会把日志刷爆。
            // 两边从此不一致，所以这里按 ERROR 记，而不是 WARN。
            _configs.AbandonLocalSync();

            _log.Error(
                "Agent",
                $"推送本机配置被中心回绝（{result.Status}）：{result.Message}"
                + " —— 已停止重推，本机仍按自己那份跑（本机与中心的配置从此不一致，请到中心页面核对）。",
                null);
        }
        catch (OperationCanceledException)
        {
            // 停机 / 重连：标志保留，下次连上再推。
        }
        catch (Exception ex)
        {
            _log.Warn("Config", $"推送本机配置失败（下次心跳会重试）：{ex.Message}");
        }
    }

    /// <summary>
    /// 指标采样循环（M4.2）：<b>整条进程生命周期都在跑</b>，与"连没连上"无关。
    /// <para>
    /// 采样节奏取 <c>Agent:Telemetry</c> 的当前值 —— 连上后它会被服务端下发的值覆盖
    /// （见 <c>OnWelcome</c>）。首帧的速率类指标为 null（计数器要两次采样才有差分），
    /// 这是正确行为，不是缺陷。
    /// </para>
    /// </summary>
    private async Task RunSamplerAsync(CancellationToken cancellationToken)
    {
        if (_telemetry is null) return;

        var next = DateTimeOffset.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var options = _options.Telemetry;

                if (options.Enabled)
                {
                    var now = DateTimeOffset.Now;

                    if (now >= next)
                    {
                        // 间隔下限 1 秒、上限 1 小时：配置写错（0 / 负 / 太大）不该让循环空转或饿死。
                        next = now + TimeSpan.FromSeconds(Math.Clamp(options.MetricsSeconds, 1, 3600));

                        _telemetry.Sample();

                        // 上限兜底：无界队列在"服务端地址配错"这种情形下会把客户机内存吃光。
                        _telemetry.Trim(Math.Max(100, options.MaxQueuedSamples));
                    }
                }
            }
            catch (Exception ex)
            {
                // 铁律 #6：后台循环绝不允许因异常静默退出。
                _log.Warn("Agent", $"指标采样出错（将继续）：{ex.Message}");
            }

            try
            {
                await Task.Delay(TickInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 把远控的两个出口挂到<b>这条</b>长连接上（M5.1）。
    /// <para>
    /// 🔥 两个出口是分开的，<b>而且失败语义刻意相反</b>：
    /// </para>
    /// <list type="bullet">
    ///   <item><b>帧</b>：送不出去要<b>抛</b> —— 让正在推的流当场停下
    ///         （见 <c>AgentConnection.PushRemoteFrameAsync</c>）；</item>
    ///   <item><b>状态</b>：送不出去只记日志 —— 它最多让页面晚一拍，
    ///         为此把会话打掉完全不划算。</item>
    /// </list>
    /// </summary>
    private void AttachRemoteSession(AgentConnection connection)
    {
        if (_remote is null) return;

        // ── 分片阈值（M5.4-c① 步 6 的诊断旋钮）──
        //
        // 🔥 **为什么需要它**：本机（以及绝大多数客户机）没有 8K 屏，而分片那条路
        //    只在"一条消息装不下"时才会走 ⇒ 平时它**是一段死代码**。
        //    要把端到端冒烟（`remote-e2e.py --chunk-threshold`）变成"真的碰过分片"，
        //    只能把阈值降下来，让 2160×1440 的普通帧也切几片。
        //
        // ⚠️ **默认状态一个字都不变**：没设这个环境变量 ⇒ 阈值 = 协议上限
        //    ⇒ `NeedsChunking` 对普通帧返回 false ⇒ 老路，一个字节都不多拷。
        //    这就是它敢进生产代码的底气（改动对现有场景是**惰性**的）。
        //
        // ⚠️ 放在这里（而不是 `AgentConnection` 构造里）：这是**唯一**装配这条
        //    上行出口的地方，与 `FrameSink` / `AudioSink` / `KeyframeRequested`
        //    挨在一起 —— "发帧这条路有哪几个旋钮"应该一眼看完。
        //    重连时重设一次是**幂等**的，且环境变量本来就随进程生命周期固定。
        var (chunkThreshold, chunkWarning) = AgentConnection.ResolveChunkThreshold(
            Environment.GetEnvironmentVariable(AgentConnection.ChunkThresholdEnvVar));

        if (chunkWarning is not null) _log.Warn("Remote", chunkWarning);

        if (chunkThreshold != RemoteStreamLimits.MaxChunkPayloadBytes)
        {
            _log.Warn(
                "Remote",
                $"🔧 分片阈值已被降到 {RemoteStreamLimits.Describe(chunkThreshold)}"
                + $"（{AgentConnection.ChunkThresholdEnvVar}）—— "
                + "这是**诊断模式**：本来一整帧一条消息的帧现在会被切成几片。"
                + "正常运行时不该看到这一行。");
        }

        connection.ChunkThresholdBytes = chunkThreshold;

        _remote.FrameSink = (frame, token) => connection.PushRemoteFrameAsync(frame, token);

        // ── M5.5：两条「回答问题」的上行（屏清单 / 剪贴板）──
        //
        // 🔥 和 FrameSink / AudioSink 不是一类东西：那两条是**持续不断的码流**，
        //    这两条是**一问一答**。中心下发的 `screens` / `clipboard` 指令是
        //    fire-and-forget（`SendAsync` 对已消失的连接静默成功），回执止步于
        //    服务进程 —— 中心根本看不到。所以答案必须从这两个口子主动上行，
        //    否则页面永远停在"正在索取"，而两边日志都写着"成功"。
        //
        // ⚠️ 用 None 而不是主循环的 token：这是对一次具体询问的答复，
        //    已经在托盘那边取到手了，哪怕此刻主循环正在收尾也该送出去。
        _remote.ScreensSink = screens => connection
            .ReportScreensAsync(screens, CancellationToken.None);

        _remote.ClipboardSink = clipboard => connection
            .ReportClipboardAsync(clipboard, CancellationToken.None);

        // 🔥 **中心丢了帧 ⇒ 请托盘补一个关键帧**（M5.4-c① 步 5）。
        //
        //    链路的最后一段：中心在分片重组失败时把 `NeedKeyframe` 塞在 ack 里回来
        //    ⇒ 发帧那一侧读它（`AgentConnection.PushFrameAsync`）⇒ 调到这里
        //    ⇒ 协调器节流后经会话指令管道发给托盘 ⇒ 托盘置标志、推流泵下一轮做成关键帧。
        //
        //    ⚠️ 为什么必须绕这么一圈：**丢帧发生在中心**，而托盘那边
        //    「我只是把每一片都发出去了」—— 它无从知道对面拼不拼得起来。
        //    这个信号只能由中心往回带。
        //
        //    ⚠️ 用方法组而不是 lambda：这个赋值在每次重连时都会跑一遍，
        //    而方法组是编译器缓存的、不产生每次一个新委托。
        connection.KeyframeRequested = _remote.RequestKeyframe;

        // 音频与画面**各走各的上行**（M5.3）：共用一条会把"音频送不出去"
        // 与"画面送不出去"变成一个信号，而处置完全不同（查声卡 vs 查编码）。
        _remote.AudioSink = (frame, token) => connection.PushRemoteAudioAsync(frame, token);

        _remote.StateSink = async state =>
        {
            // 用 None 而不是主循环的 token：会话的终态上报**必须送出去**，
            // 哪怕此刻主循环正在收尾 —— "为什么停了"的答案就是它。
            await connection
                .ReportRemoteSessionAsync(state, CancellationToken.None)
                .ConfigureAwait(false);
        };

        // 🔥 与前两条**方向相反**：这是入口 —— 中心送回来的输入事件落到协调器，
        //    再由协调器经命名管道转给托盘去注入。
        //
        //    ⚠️ 它**不经过 AgentCommandExecutor**（那条路是"指令 + 回执 + 记录到
        //    接收数据环形缓冲"的重形态）：拖动鼠标每秒几十批，走那条路会把
        //    "接收到的数据"面板刷成全是鼠标移动，而那个面板存在的意义
        //    恰恰是"让人看清到底收到了什么指令"。
        connection.InputReceiver = batch => _remote.AcceptInput(batch);
    }

    /// <summary>
    /// 摘掉远控的出口，并<b>停掉正在推的流</b>（M5.1）。
    /// <para>见主循环里那处调用的注释：不摘的后果是"离线的机器一直烧着 CPU"。</para>
    /// </summary>
    private void DetachRemoteSession()
    {
        if (_remote is null) return;

        _remote.FrameSink = null;
        _remote.StateSink = null;
        _remote.ScreensSink = null;
        _remote.ClipboardSink = null;
        _remote.OnDisconnected();
    }

    /// <summary>
    /// 探测并上报本机的视频硬编码能力（M5.0 第二批）。
    ///
    /// <para>
    /// 🔥 <b>它为什么值得单独一趟网络往返</b>：这份结论是整个 M5 的地基 ——
    /// 编码格式（H.264 封顶 4096 / 超 4K 只能 HEVC）、要不要开分帧编码、
    /// 8K 能不能跑、甚至"现场要不要买双引擎显卡"，全都建在它上面。
    /// 而这些决定<b>只能由被控端的事实支撑</b>：中心那块显卡能编什么，与这台机器无关。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>它失败必须是"无声的"</b>：旧中心不认识这个方法、网络抖动、
    /// 被控机没有 WMI/PDH 权限 —— 都不该升级成断线重连或任何可见告警。
    /// 最坏结果只是"中心页面上那台机器的能力空白"，而它下次重连会再试一遍。
    /// </para>
    ///
    /// <para>
    /// <b>为什么套一层 <see cref="Task.Run{TResult}(Func{TResult})"/></b>：
    /// 探测实现是<b>同步的</b>（COM/DXGI/MFT/PDH 枚举），而这里是从 SignalR 的回调线程
    /// （<c>OnWelcome</c> 就在那条线上）发起的 —— 直接跑会占住消息处理线程几十毫秒。
    /// 探测结果有 10 分钟缓存，所以真正的开销只在首次连接那一次。
    /// </para>
    /// </summary>
    private async Task ReportEncoderCapabilityAsync(AgentConnection connection, CancellationToken cancellationToken)
    {
        if (_encoderProbe is null) return;

        try
        {
            var report = await Task
                .Run(() => _encoderProbe.ProbeAsync(cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            var payload = EncoderCapabilityMapper.ToDto(report, BeijingTime.Now);

            var ack = await connection
                .ReportEncoderCapabilityAsync(payload, cancellationToken)
                .ConfigureAwait(false);

            if (ack is { Ok: true })
            {
                _log.Info("Agent", $"已上报本机编码能力：{report.Summary}");
                return;
            }

            // 没拿到 ok 的两种可能：旧中心不认识这个方法，或它明确拒收。
            // 都只记一行 warn —— 下一次重连会自动重报。
            _log.Warn("Agent", $"中心没有接受本机编码能力上报（{ack?.Status ?? "无应答"}）—— 下次重连会再报一次。");
        }
        catch (OperationCanceledException)
        {
            // 连接收尾中，正常。
        }
        catch (Exception ex)
        {
            // 铁律 #6：上报失败绝不能把主循环带崩。
            _log.Warn("Agent", $"上报本机编码能力失败（不影响其它功能）：{ex.Message}");
        }
    }

    /// <summary>
    /// 遥测上报循环（M4.2）：只在"握着长连接"的那段时间里跑。
    /// <para>
    /// 三件事各自的节奏独立（指标按批、开关机轮询、清单按小时），所以用一个 1 秒节拍
    /// 统一驱动，各自记自己的"下次到点时间"。这样只有一个循环要维护、也只有一个地方
    /// 会踩到网络异常。
    /// </para>
    /// </summary>
    private async Task RunReporterAsync(AgentConnection connection, CancellationToken cancellationToken)
    {
        if (_telemetry is null) return;

        var options = _options.Telemetry;

        // 清单的排期从**本地持久化的"上次上报时刻"**起算，而不是从"本次进程启动"起算：
        // Agent 服务重启很频繁（升级、改配置），若按进程起算，每次重启都会立刻重采一遍
        // WMI（1～2 秒 CPU）—— 而清单内容 6 小时内基本不会变。
        var inventoryInterval = TimeSpan.FromHours(Math.Clamp(options.InventoryHours, 1, 168));

        var nextInventory = _telemetry.LastInventoryAt is { } lastInventoryAt
            ? lastInventoryAt + inventoryInterval
            : DateTimeOffset.MinValue;

        var nextPower = DateTimeOffset.MinValue;

        while (!cancellationToken.IsCancellationRequested
               && connection.State == HubConnectionState.Connected)
        {
            try
            {
                if (_options.Telemetry.Enabled)
                {
                    var now = DateTimeOffset.Now;

                    await FlushMetricsAsync(connection, cancellationToken).ConfigureAwait(false);

                    // ⚠️ `_inventoryRequested` 压过排期：中心说"我这边没有清单"时，
                    //    再等 6 小时就是 bug 本身（见那个字段的注释）。
                    if (_inventoryRequested || now >= nextInventory)
                    {
                        // 无论成败都先把下一次排远：失败时用短重试间隔，别每个 tick 都去跑 WMI。
                        nextInventory = now + InventoryRetryInterval;

                        if (await ReportInventoryAsync(connection, cancellationToken).ConfigureAwait(false))
                        {
                            // 只有**成功**才清掉"补报要求"，否则 5 分钟后继续试。
                            _inventoryRequested = false;

                            nextInventory = now + TimeSpan.FromHours(
                                Math.Clamp(_options.Telemetry.InventoryHours, 1, 168));
                        }
                    }

                    if (now >= nextPower)
                    {
                        nextPower = now + TimeSpan.FromSeconds(
                            Math.Clamp(_options.Telemetry.PowerEventPollSeconds, 5, 3600));

                        await ReportPowerEventsAsync(connection, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 铁律 #6：一次上报失败绝不能把连接拖垮 —— 最坏也不过是曲线晚几秒。
                _log.Warn("Agent", $"遥测上报出错（将继续）：{ex.Message}");
            }

            try
            {
                await Task.Delay(TickInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 把积压的指标样本送出去（每轮最多一批）。
    /// <para>
    /// <b>成功的判据是服务端明确应答 ok</b>，不是"InvokeAsync 没抛"：
    /// 服务端可能因为"批次超限 / 时间戳离谱"而明确拒收，那种情况重发是没用的，
    /// 得像现在这样把队列留着（服务端按 <c>(AgentId, At)</c> 去重，重发安全）。
    /// </para>
    /// </summary>
    private async Task FlushMetricsAsync(AgentConnection connection, CancellationToken cancellationToken)
    {
        if (_telemetry is null) return;

        var pending = _telemetry.PendingSampleCount;

        if (pending <= 0) return;

        var samples = _telemetry.PeekSamples(Math.Clamp(_options.Telemetry.MaxBatchSamples, 1, 5000));

        if (samples.Count == 0) return;

        var ack = await connection
            .ReportMetricsAsync(new AgentMetricsBatchDto(samples), cancellationToken)
            .ConfigureAwait(false);

        if (ack is { Ok: true })
        {
            _telemetry.CommitSamples(samples.Count);
            _metricsFailureStreak = 0;
            return;
        }

        LogIngestFailure("指标", pending, ack, ref _metricsFailureStreak);
    }

    /// <summary>
    /// 读并上报开关机事件。
    /// <para>
    /// ⚠️ <b>水位只在服务端明确接受之后才推进</b>。事件丢一条就永远补不回来 ——
    /// 这是整条遥测链路上唯一一处"宁可重发十次也不许丢一次"的地方。
    /// </para>
    /// </summary>
    private async Task ReportPowerEventsAsync(AgentConnection connection, CancellationToken cancellationToken)
    {
        if (_telemetry is null) return;

        var read = _telemetry.ReadPowerEvents(DateTimeOffset.Now);

        // null = 平台不支持（Linux）或本次读取失败：两种都什么都不做。
        if (read is null) return;

        if (!read.HasEvents)
        {
            // "读到 0 条"也是一次成功的读取：推进水位 + 打上"已回溯"标记，
            // 否则一个 7 天没关过机的窗口会每 30 秒重扫一遍 7 天的日志。
            _telemetry.CommitPowerEvents(read.Watermark, DateTimeOffset.Now);
            return;
        }

        var ack = await connection
            .ReportPowerEventsAsync(read.Batch, cancellationToken)
            .ConfigureAwait(false);

        if (ack is { Ok: true })
        {
            _telemetry.CommitPowerEvents(read.Watermark, DateTimeOffset.Now);
            _powerFailureStreak = 0;

            _log.Info(
                "Agent",
                $"上报开关机记录 {read.Batch.Events.Count} 条（服务端入库 {ack.Accepted} 条）。");

            return;
        }

        LogIngestFailure("开关机记录", read.Batch.Events.Count, ack, ref _powerFailureStreak);
    }

    /// <summary>采集并上报硬件清单。</summary>
    /// <returns>是否被服务端接受（决定下一次排期是"一个间隔后"还是"5 分钟后重试"）。</returns>
    private async Task<bool> ReportInventoryAsync(AgentConnection connection, CancellationToken cancellationToken)
    {
        if (_telemetry is null) return false;

        var inventory = _telemetry.CollectInventory();

        if (inventory is null) return false;

        // 把本机文件传输的白名单目录一并报给服务端（M4.4）。
        //
        // 为什么走"清单"而不是新开一个上报：这些是机器级恒定量（管理员改 appsettings.json 才变），
        // 清单"一机一行、6 小时一次"正好对应；再开一条上报通道只会多一份要维护的失败重试逻辑。
        //
        // 未启用传输时报**空数组**（而不是 null）：
        //   · null = "老客户端，没这个字段"（页面按未知处理）
        //   · []   = "这个客户端版本知道这件事，但本机没启用文件传输"
        // 两者在页面上该说的话不一样，不能混。
        //
        // ⚠️ 2026-10-15 起**回传不再有白名单**（读不限制目录，用户拍板）。
        //    这里报的"可读目录"因此只是**候选提示**（= 本机盘符，与「浏览」顶层同一集合），
        //    它不构成限制：页面拿它当输入建议用，客户机对回传源路径不再做任何范围校验。
        inventory = _transfers is null
            ? inventory with { TransferInboxDirectories = Array.Empty<string>(), TransferOutboxDirectories = Array.Empty<string>() }
            : inventory with
            {
                TransferInboxDirectories = TransferPaths.InboxWhitelist(_options.Transfer, _stateDirectory),
                TransferOutboxDirectories = TransferPaths.DriveRoots(),
            };

        var ack = await connection
            .ReportInventoryAsync(inventory, cancellationToken)
            .ConfigureAwait(false);

        if (ack is { Ok: true })
        {
            _telemetry.CommitInventory(DateTimeOffset.Now);
            _inventoryFailureStreak = 0;

            _log.Info(
                "Agent",
                $"硬件清单已上报：{inventory.Cpu?.Name ?? "CPU 未知"}，"
                + $"内存 {inventory.TotalMemoryMb?.ToString() ?? "未知"} MB，"
                + $"分区 {inventory.Volumes.Count} 个，显卡 {inventory.Gpus.Count} 块。");

            return true;
        }

        LogIngestFailure("硬件清单", 1, ack, ref _inventoryFailureStreak);
        return false;
    }

    /// <summary>
    /// 记一次上报失败。
    /// <para>
    /// <b>必须节流</b>：连不上服务端时这个循环每秒都会走到这里，
    /// 每个 tick 记一行会让客户机的日志文件在一夜之间涨到几十 MB。
    /// 第 1 次、之后每 20 次各记一行 —— 既能看到"出问题了"，又不会被刷屏。
    /// </para>
    /// </summary>
    private void LogIngestFailure(string what, int pending, AgentIngestAckDto? ack, ref int streak)
    {
        streak++;

        if (streak > 1 && streak % 20 != 0) return;

        _log.Warn(
            "Agent",
            ack is null
                ? $"{what}上报未获应答（本机待发 {pending} 条），稍后重试。"
                : $"{what}上报被拒（{ack.Status}）：{ack.Message}（本机待发 {pending} 条）");
    }

    /// <summary>机器稳定标识（机器码）。取不到时退化成"主机名"，绝不返回空。</summary>
    private string ResolveMachineKey()
    {
        try
        {
            var code = _machineCode?.GetMachineCode();

            if (!string.IsNullOrWhiteSpace(code)) return code.Trim();
        }
        catch (Exception ex)
        {
            _log.Warn("Agent", $"读取机器码失败（改用主机名兜底）：{ex.Message}");
        }

        return $"host-{Environment.MachineName}";
    }

    private void UpdateStatus(string status, bool connected)
    {
        Status = status;
        Connected = connected;

        var snapshot = WriteStatus();

        try
        {
            StatusChanged?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            _log.Error("Agent", "状态变化通知出错", ex);
        }
    }

    /// <summary>
    /// 刷新「最后一次与中心成功通信」的时间并落盘（2026-10-14）。
    /// <para>
    /// 它<b>刻意不经过 <see cref="UpdateStatus"/></b>：那个方法的契约是"状态变了就通知"，
    /// 而心跳是每 10 秒一次的正常节奏，不该被当成状态变化（何况 <c>Status</c> 一个字都没变）。
    /// </para>
    /// </summary>
    private void TouchLastSeen()
    {
        _lastSeenAt = DateTimeOffset.Now;
        WriteStatus();
    }

    /// <summary>
    /// 把此刻的状态写进状态文件（托盘与本机配置页的唯一读取来源），并返回那份快照。
    /// <para>
    /// 抽出来是因为有<b>两个</b>调用方：状态变化（<see cref="UpdateStatus"/>）与心跳刷新
    /// （<see cref="TouchLastSeen"/>）。它们要写的是同一份东西 —— 各写一遍必然漂移。
    /// </para>
    /// </summary>
    private AgentStatusSnapshot WriteStatus()
    {
        var snapshot = new AgentStatusSnapshot(
            Status,
            Connected,
            ServerUrl,
            _cachedCredentials?.AgentId,
            DisplayName,
            LastError,
            DateTimeOffset.Now,
            _serverVersion,
            _lastSeenAt);

        // 状态文件是"托盘看服务"的唯一通道（见 AgentStatusFile 注释）。
        AgentStatusFile.Write(_stateDirectory, snapshot);

        return snapshot;
    }

    public async ValueTask DisposeAsync()
    {
        if (_configs is not null && _tasks is not null)
        {
            _configs.Applied -= OnConfigApplied;
        }

        // 先关入站端口：停机过程中不该还留着一个"能收指令但没人处理"的窗口。
        if (_udp is not null)
        {
            try
            {
                await _udp.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("Agent", $"关闭 UDP 指令通道时异常：{ex.Message}");
            }
        }

        if (_transfers is not null)
        {
            // 先收传输：它会中止在飞的搬运（保留 .cs-part，下次接着续）。
            await _transfers.DisposeAsync().ConfigureAwait(false);
        }

        _transferHttp?.Dispose();
        _http.Dispose();
    }

    /// <summary>一轮的结果。</summary>
    private readonly record struct IterationOutcome(bool Succeeded, TimeSpan? OverrideDelay, bool ResetBackoff = false)
    {
        /// <summary>普通失败：参与退避递增。</summary>
        public static IterationOutcome Retry { get; } = new(false, null);

        /// <summary>
        /// 这一轮<b>刚通过广播换了地址</b>：下一轮要立刻试，而且退避要从头算。
        /// <para>
        /// ⚠️ 不能复用 <see cref="Success"/>：那会把"连上了"这件事写错（状态文件、UDP 通道、远端会话
        /// 全都按它决策）。这里只是"换了个地址再试一次"。
        /// </para>
        /// </summary>
        public static IterationOutcome RetryAfterSwitch { get; } = new(false, null, ResetBackoff: true);

        /// <summary>成功连过一轮：重置退避。</summary>
        public static IterationOutcome Success { get; } = new(true, null);

        /// <summary>等一个明知原因的固定时长（被吊销等人工介入）。</summary>
        public static IterationOutcome FixedDelay(TimeSpan delay) => new(false, delay);
    }
}
