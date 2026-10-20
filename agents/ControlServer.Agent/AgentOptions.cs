namespace ControlServer.Agent;

using ControlServer.Agent.Telemetry;
using ControlServer.Agent.Transfer;

/// <summary>
/// 客户端 Agent 的配置（<c>appsettings.json</c> 的 <c>Agent</c> 节；命令行与环境变量可覆盖）。
/// <para>
/// <b>启动顺序必须是"先找服务端，再谈别的"</b>：这里的所有项都只服务于那件事。
/// </para>
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>
    /// 中心服务端地址（形如 <c>http://10.0.0.5:8899</c>）。
    /// <para>
    /// <b>留空 = 靠 UDP 广播自动发现</b>（默认，也是"装完就自己出现"这条体验的来源）。
    /// 填了则<b>跳过发现</b>直接用 —— 这是跨网段 / 广播被禁网络里的唯一出路，
    /// 安装脚本会把 <c>--server=</c> 写进这里。
    /// </para>
    /// </summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>发现端口（必须与服务端 <c>Server:Discovery:Port</c> 一致，默认 9998）。</summary>
    public int DiscoveryPort { get; set; } = 9998;

    /// <summary>
    /// 状态目录（凭据 + 日志 + 状态文件）。留空 = <c>%ProgramData%\ControlServer.Agent</c>。
    /// </summary>
    public string StateDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 心跳间隔（秒）。<b>只是初始值</b> —— 连上之后一律以服务端下发的为准。
    /// </summary>
    public int HeartbeatSeconds { get; set; } = 10;

    /// <summary>
    /// 是否允许 UDP 广播发现。默认开启；跨网段场景关掉并配合 <see cref="ServerUrl"/>。
    /// </summary>
    public bool AllowBroadcastDiscovery { get; set; } = true;

    /// <summary>显示名（留空 = 用主机名）。一般不需要填，管理员会在网页上改。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Windows 服务名（服务侧与托盘侧必须一致 —— 托盘靠它通过 SCM 起停服务）。
    /// <para>默认 <c>ControlServerAgent</c>。安装脚本按这个名字注册服务。</para>
    /// </summary>
    public string ServiceName { get; set; } = "ControlServerAgent";

    /// <summary>
    /// 遥测节奏（M4.2）。
    /// <para>
    /// <b>这里的值只是"连上服务端之前"的默认值</b>：连上之后一律以服务端在
    /// <c>Welcome</c> 里下发的为准（见 <see cref="ControlServer.Contracts.AgentTelemetrySettingsDto"/>）。
    /// 保留本地默认值是为了让客户端在"还没连上"的那段时间也照常采样，
    /// 免得每次重连都在曲线上留一段空白。
    /// </para>
    /// </summary>
    public AgentTelemetryOptions Telemetry { get; set; } = new();

    /// <summary>
    /// 文件传输（M4.4）。
    /// <para>
    /// <b>与服务端下发的关系</b>：这里全是"本机自己的安全边界"（哪些目录允许写、允许读、几条并发），
    /// <b>刻意不由服务端下发</b> —— 一旦中心能改客户机的落地目录，那"白名单"就变成了
    /// "中心说了算"，也就等于没有边界。
    /// </para>
    /// </summary>
    public TransferOptions Transfer { get; set; } = new();

    /// <summary>
    /// 服务端下发的任务配置（M4.5，三张清单 + UDP 指令通道）。
    /// <para>
    /// 内容<b>默认由中心下发</b>；但从 2026-10-14 起<b>本机也能改</b>（本机配置页），
    /// 两边"谁后写谁生效"。这里只有一个"本机还要不要这份能力"的开关 ——
    /// 与 <see cref="Transfer"/> 同一个思路：内容两边都能写，边界听本机的。
    /// </para>
    /// </summary>
    public AgentTaskOptions Tasks { get; set; } = new();

    /// <summary>
    /// 本机配置页面（2026-10-14）。
    /// <para>
    /// 客户机上的一个<b>仅本机可访问</b>（绑定 <c>127.0.0.1</c>）的小网页，
    /// 用来在<b>没有中心服务端</b>时也能改这台机器的延时 / 循环 / 定时 / 启动程序 / UDP 端口。
    /// 托盘菜单「打开本机配置」直接把它打开。
    /// </para>
    /// </summary>
    public AgentLocalUiOptions LocalUi { get; set; } = new();
}

/// <summary>
/// 本机配置页面（<c>Agent:LocalUi</c> 节，2026-10-14）。
/// </summary>
public sealed class AgentLocalUiOptions
{
    /// <summary>
    /// 默认端口 <c>8091</c>。
    /// <para>⚠️ 托盘在"发现文件还没写出来"时会拿它兜底，所以它必须是<b>常量</b>而不是散落的字面量。</para>
    /// </summary>
    public const int DefaultPort = 8091;

    /// <summary>
    /// 是否启用本机配置页面。<b>默认开</b>。
    /// <para>
    /// 关掉之后：<b>不绑 127.0.0.1 上的端口</b>（不是"绑了再拒"），托盘的「打开本机配置」
    /// 会提示"本机配置页未启用"。中心下发那条路完全不受影响 ——
    /// 这个开关关的只是"本机改"这一条路。
    /// </para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 监听端口（<b>只绑 127.0.0.1</b>）。默认 <c>8091</c>。
    /// <para>
    /// 刻意避开本项目里已经占掉的三个号：<c>8899</c>（中心 HTTP）、
    /// <c>8090</c>（UDP 指令通道）、<c>9998/9999</c>（发现 / 对码）。
    /// </para>
    /// </summary>
    public int Port { get; set; } = DefaultPort;
}

/// <summary>
/// 客户机侧的<b>任务执行</b>开关（M4.5，<c>Agent:Tasks</c> 节）。
/// <para>
/// <b>为什么只有一个开关、没有任何"默认任务"</b>：任务内容一律来自服务端，
/// 本机不提供任何默认值 —— 一份"本机自带的默认任务"就是一条"中心不知道它在发什么"的路径。
/// </para>
/// </summary>
public sealed class AgentTaskOptions
{
    /// <summary>
    /// 是否接收并执行服务端下发的任务（三张清单）。
    /// <para>
    /// 默认开启。关掉之后本机<b>不拉配置、不载入缓存、不跑任何任务</b>，
    /// 也不使用 UDP 指令通道 —— 它是一台"只上报遥测、不接受控制"的机器。
    /// 现场常见用途：某台机器暂时不想被任务影响（例如正在做别的用途），
    /// 但又不想把它从中心列表里摘掉。
    /// </para>
    /// <para>
    /// ⚠️ 它是<b>总闸</b>：为假时 UDP 指令通道连端口都不会绑定
    /// （而不是"绑定了再丢弃"—— 不开的端口在 <c>netstat</c> 里根本看不见，那才是"关"该有的样子）。
    /// </para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// UDP 指令通道是否<b>真的执行</b>收到的指令（默认 <c>true</c>）。
    /// <para>
    /// <c>false</c> = <b>演练模式</b>：来源黑名单与报文解析<b>全部照常走完</b>
    /// （它们才是要验证的东西），只是不真的关机、不真的调音量。
    /// </para>
    /// <para>
    /// 两个真实用途：
    /// ① <b>冒烟测试</b> —— 要验证"黑名单生不生效、指令解不解得出来"，但不能把测试机真的关掉；
    /// ② <b>首次接线</b> —— 现场先挂上通道观察一段时间"到底谁在发指令、发的都是什么"，确认无误再放行。
    /// </para>
    /// <para>
    /// ⚠️ 它与服务端下发的 <c>Udp.Enabled</c> 是<b>两个维度</b>，不能互相替代：
    /// 服务端那个决定"有没有这条通道"（内容听中心的），这个决定"本机此刻敢不敢动手"（边界听本机的）。
    /// </para>
    /// <para>
    /// 🔥 <b>它是本机唯一的"敢不敢动手"总闸，两条通道都归它管</b>（2026-10-15 起）：
    /// UDP 报文**与**中心经 SignalR 下发的关机 / 重启 / 音量，一律先看这道闸。
    /// 键名沿用历史（<c>ExecuteUdpCommands</c>），但语义早已是"本机此刻敢不敢动手" ——
    /// 安全闸不能留漏勺：现场开它是为了"先观察、确认没有误触发再放行"，
    /// 若中心的关机指令能绕过它，那它就只是一个标签，不是一道闸。
    /// </para>
    /// </summary>
    public bool ExecuteUdpCommands { get; set; } = true;
}

/// <summary>
/// 客户端 Agent 的固定路径。
/// <para>
/// <b>为什么与中心服务端同级的两个进程要共用这套路径</b>：服务和托盘都读同一份凭据
/// 与同一个状态文件（见 <c>AgentStatusFile</c>），路径不一致会让托盘永远显示"未注册"。
/// </para>
/// </summary>
public static class AgentPaths
{
    /// <summary>默认数据目录名（<c>%ProgramData%\</c> 之下）。</summary>
    public const string DefaultFolderName = "ControlServer.Agent";

    /// <summary>解析状态目录（配置为空则用平台默认）。</summary>
    public static string ResolveStateDirectory(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            DefaultFolderName);
    }

    /// <summary>确保目录存在。失败时抛（调用方决定是"退出"还是"降级"）。</summary>
    public static string EnsureStateDirectory(string? configured)
    {
        var directory = ResolveStateDirectory(configured);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// 凭据文件。<b>扩展名刻意不是 <c>.json</c></b>：内容是 DPAPI 密文（或带前缀的明文 JSON），
    /// 用 <c>.json</c> 会让"双击打不开"变成一个看着像 bug 的现象。
    /// </summary>
    public static string CredentialsFile(string stateDirectory)
        => Path.Combine(stateDirectory, "credentials.dat");

    /// <summary>状态文件（服务写、托盘读；一行 JSON）。</summary>
    public static string StatusFile(string stateDirectory)
        => Path.Combine(stateDirectory, "status.json");

    /// <summary>
    /// 本机配置文件的落盘位置（M4.5；2026-10-14 起<b>可写</b>）。
    /// <para>
    /// 中心不可达时按它降级运行，本机的「本机配置」页面改的也是它。
    /// 用 <c>.json</c>：它确实是给人看的 JSON（可读性是排障时的一等需求，
    /// 现场可以直接打开看"这台机器最后生效的那一份配置长什么样"）。
    /// </para>
    /// <para>
    /// 🔥 文件名刻意从旧版的 <c>config-cache.json</c> 改成 <c>local-config.json</c>：
    /// 旧的叫"缓存"，含义是"中心的副本、本机不该改"；现在它是这台机器<b>自己的配置</b>，
    /// 名字必须跟着语义走 —— 否则以后维护的人会按旧语义去写它。
    /// 读取端对旧文件做了兼容（见 <see cref="AgentLocalConfigStore.Load"/>）。
    /// </para>
    /// </summary>
    public static string LocalConfigFile(string stateDirectory)
        => Path.Combine(stateDirectory, "local-config.json");

    /// <summary>
    /// 本机配置页面的<b>发现文件</b>（2026-10-14）：一行 JSON，记着它实际绑定的 URL。
    /// <para>
    /// 为什么要有这个文件：托盘（另一个进程）要打开这个页面，但它不读 <c>appsettings.json</c>，
    /// 也不知道端口有没有被改过。让<b>绑定端口的那个进程</b>把自己在哪写下来，
    /// 比让托盘去猜要可靠得多 —— 端口被改成非默认值时可猜的那条路必然打不开。
    /// </para>
    /// </summary>
    public static string LocalUiDiscoveryFile(string stateDirectory)
        => Path.Combine(stateDirectory, "local-ui.json");

    /// <summary>日志目录。</summary>
    public static string LogDirectory(string stateDirectory)
        => Path.Combine(stateDirectory, "logs");
}
