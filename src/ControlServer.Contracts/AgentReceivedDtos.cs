namespace ControlServer.Contracts;

// ═══════════════════════════════════════════════════════════════════════════
// · 客户机「接收到的数据」（2026-10-13）
//
// **为什么要做它**：现场反馈"下发了指令，客户端毫无反应"。
// 这句话里的"毫无反应"其实是**三种完全不同的故障**，而它们的处置互不相同：
//
//   ① **根本没收到** —— 防火墙没放行 / 中心与客户机不同网段 / 端口不是那个端口；
//   ② **收到了但认不出** —— 报文被中间设备改写、发的是旧格式、多了不可见字符；
//   ③ **收到了也认出了，但不敢做 / 做失败了** —— 演练模式开着、音量那台机器没有交互式会话。
//
// 只看"服务端说发出去了"和"机器没关机"，这三者长得一模一样。
// 所以这里把客户机**收到的每一条东西**记下来（原文 + 来源 + 时刻 + 结论），
// 现场打开本机页面就能一眼分到 ①②③ 里哪一类 —— 诊断从"猜"变成"看"。
//
// **记什么**：不只记 UDP 数据报，也记中心经 SignalR 下发的指令（Command / 传输任务 / 配置变更）。
// 因为"指令不生效"也可能走的根本不是 UDP 那条路，只记 UDP 会把视线带偏。
//
// **记在哪**：客户机内存里的一个环形缓冲（默认 200 条），**不落盘、不上报**。
// 它是排查工具，不是审计日志 —— 落盘会带来"这文件能长多大""要不要轮转"一串新问题，
// 而它要回答的问题只有"刚刚那一下到底发生了什么"。
// 中心要看时经 `/hubs/agent` 现取一次（与目录浏览同一条"一问一答"的路子）。
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 收到的这一条是<b>从哪条链路</b>来的。
/// </summary>
public static class AgentReceivedKinds
{
    /// <summary>UDP 指令通道（局域网里发来的明文指令）。</summary>
    public const string Udp = "udp";

    /// <summary>中心服务端的 SignalR 长连接（下发的指令 / 任务 / 配置变更）。</summary>
    public const string SignalR = "signalr";
}

/// <summary>
/// 这一条的<b>结论</b>。
/// <para>
/// ⚠️ 值分得细是有意的：<see cref="Unrecognized"/> 与 <see cref="Blocked"/> 在日志里
/// 都可能只表现为"没有反应"，但一个是"报文不对"、一个是"这台机器被拉黑了"，处置完全不同。
/// </para>
/// </summary>
public static class AgentReceivedOutcomes
{
    /// <summary>认出来了，而且确实执行成功。</summary>
    public const string Executed = "executed";

    /// <summary>认出来了，执行了但失败（原因在 <c>Detail</c> 里）。</summary>
    public const string Failed = "failed";

    /// <summary>
    /// 认出来了，但<b>故意没执行</b> —— 演练模式（<c>Agent:Tasks:ExecuteUdpCommands=false</c>），
    /// 或者这条指令本机不处理。
    /// </summary>
    public const string Ignored = "ignored";

    /// <summary>
    /// <b>报文认不出来</b>（不是那几条明文指令，也不是 <c>名字:参数</c>）。
    /// <para>现场最容易被这一条坑到："我明明发了 DOWN" —— 而实际发过去的是 <c>down </c>（多个空格）。</para>
    /// </summary>
    public const string Unrecognized = "unrecognized";

    /// <summary>来源 IP 在本机黑名单里，报文在解析之前就被丢了。</summary>
    public const string Blocked = "blocked";

    /// <summary>中心的 SignalR 消息<b>已受理</b>（入队 / 转交对应处理器）。</summary>
    public const string Accepted = "accepted";
}

/// <summary>
/// 一次"接收到的数据"查询的结果状态。
/// <para>
/// 取值与 <see cref="AgentBrowseStatuses"/> 刻意一致（<c>ok</c> / <c>unsupported</c> /
/// <c>offline</c> / <c>timeout</c> / <c>error</c>）：这两条链路的失败形态一模一样，
/// 而网页那侧的错误分支可以复用同一套判断。
/// </para>
/// </summary>
public static class AgentReceivedStatuses
{
    public const string Ok = "ok";

    /// <summary>客户端版本太旧 / 这条能力未装配。</summary>
    public const string Unsupported = "unsupported";

    /// <summary>客户机没有长连接（服务端这一侧判定的）。</summary>
    public const string Offline = "offline";

    /// <summary>客户机没在时限内回话（服务端这一侧判定的）。</summary>
    public const string Timeout = "timeout";

    /// <summary>其它错误。</summary>
    public const string Error = "error";
}

/// <summary>
/// 客户机收到的<b>一条</b>东西。
/// </summary>
/// <param name="At">收到的时刻（<b>北京时间</b>，与全项目时间戳纪律一致）。</param>
/// <param name="Kind">见 <see cref="AgentReceivedKinds"/>。</param>
/// <param name="Source">
/// 来源的人话描述：UDP 是 <c>192.168.1.7:53421</c>；SignalR 是 <c>中心服务端</c>。
/// </param>
/// <param name="Raw">
/// 原始内容：UDP 是<b>原样的报文文本</b>（含空格、不可见字符也照原样留着 ——
/// 现场最常见的坑就是"看着一样、其实多一个空格"）；SignalR 是指令名 + 参数。
/// <para>过长会截断（避免一条 60 KB 的垃圾报文把整个页面撑爆）。</para>
/// </param>
/// <param name="Outcome">见 <see cref="AgentReceivedOutcomes"/>。</param>
/// <param name="Detail">人话补充（失败原因 / 执行结果 / "本机不处理这条"）。</param>
public sealed record AgentReceivedEntryDto(
    DateTimeOffset At,
    string Kind,
    string Source,
    string Raw,
    string Outcome,
    string? Detail = null);

/// <summary>
/// 客户机「接收到的数据」快照。
/// <para>
/// 🔥 <b>它不只有条目列表</b>：前面那几项"通道状态"才是回答"为什么一条都没收到"的关键 ——
/// 一个空的列表配上一句"UDP 通道未启动：中心下发的配置没有启用它"，
/// 比一百条日志都有用。
/// </para>
/// </summary>
/// <param name="Ok">查询本身是否成功。</param>
/// <param name="Status">见 <see cref="AgentReceivedStatuses"/>。</param>
/// <param name="Message">失败时的人话原因。</param>
/// <param name="UdpListening">
/// 此刻 UDP 通道<b>是否真的绑定了端口在听</b>。
/// <para>这是"什么都没收到"的头号分水岭：它为 false 时，报文在网卡那一步就被内核丢了，
/// 客户机<b>不可能</b>有记录 —— 该去查配置，而不是查防火墙。</para>
/// </param>
/// <param name="UdpPort">正在听的端口；没在听则为 null。</param>
/// <param name="UdpStateText">
/// 通道状态的人话解释。正在听时写"监听 0.0.0.0:8090"；没在听时写清<b>是哪一个原因</b>
/// （本机停用 / 中心配置没启用 / 端口越界 / 启动失败）。
/// </param>
/// <param name="UdpExecuteEnabled">
/// 是否真的会执行收到的 UDP 指令。false = <b>演练模式</b>（只识别不动手）——
/// 这一条能直接解释"指令收到了、也认出来了，但机器没反应"。
/// </param>
/// <param name="Entries">最近的条目（<b>新的在前</b>）。</param>
/// <param name="ReceivedCount">
/// 本进程启动以来收到的报文总数，**含已经被挤出环形缓冲的那些**。
/// <para>它与 <c>Entries.Count</c> 的差值就是"被挤掉了多少" ——
/// 缓冲满了之后，条目会开始丢最旧的，但计数不会。</para>
/// </param>
/// <param name="StartedAt">本进程启动的时刻（用来判断"这段时间里到底收没收过东西"）。</param>
public sealed record AgentReceivedDataDto(
    bool Ok,
    string Status,
    string? Message = null,
    bool UdpListening = false,
    int? UdpPort = null,
    string? UdpStateText = null,
    bool UdpExecuteEnabled = true,
    IReadOnlyList<AgentReceivedEntryDto>? Entries = null,
    long ReceivedCount = 0,
    DateTimeOffset? StartedAt = null)
{
    /// <summary>造一个失败结果（<b>一定要带人话原因</b>）。</summary>
    public static AgentReceivedDataDto Fail(string status, string message) => new(false, status, message);

    /// <summary>造一个"客户端不支持"的失败结果（旧版本客户端不会回这条）。</summary>
    public static AgentReceivedDataDto Unsupported(string message)
        => Fail(AgentReceivedStatuses.Unsupported, message);
}
