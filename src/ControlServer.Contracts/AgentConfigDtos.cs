namespace ControlServer.Contracts;

// ═══════════════════════════════════════════════════════════════════════════
// 客户机配置（M4.5 · BootAssistant 对齐）
//
// **双向 · 后写覆盖**（2026-10-14 起，取代原决策 D5 的"事实来源只有服务端"）：
// 任务清单、监听端口、来源 IP 黑名单既存在服务端库里（按版本号下发），客户机也能通过
// 它的「本机配置」页面直接改（这是"客户端本身可独立使用"的前提）。两边都能写，
// **谁后写谁生效** —— 版本号优先、同版本比时间戳（见 AgentConfigMapper.IsNewer）。
//
// ⚠️ 仍然坚持的一件事：**"两边各跑一份不同的配置"必须能立刻看出来**。于是客户机的
// 每一版都带着"来源"（中心下发 / 本机页面）与"待同步"标志并显示在页面上；
// 否则排障时最说不清的就是"网页显示一份、机器上跑的是另一份"。
//
// ⚠️ 三张任务清单的划分**照搬参考项目**（它是"要有哪些功能"的清单来源）：
//   · SendTasks        —— 延时发送；`IsLoop=true` 时就是「循环发送」（**不是另一张表**）
//   · ScheduledTasks   —— 定时发送（每天在若干个 HH:mm 时刻发）
//   · LaunchTasks      —— 延时启动程序（**执行在会话侧**，服务侧做不到"弹窗口"）
// 参考项目确实只有这三张表 —— 早期的方案文档里把「循环发送」写成独立一栏是笔误，
// 这里以参考项目的数据模型为准。
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 客户机上的 UDP 指令通道（M4.5）。
/// <para>
/// <b>它是"入站"的</b>：客户机在这个端口上收指令（关机等），这是本项目里
/// <b>唯一一个客户机主动开着的入站端口</b>（M4.1 的铁律是"客户端不开入站端口"，
/// 这里按参考项目 <c>BootAssistant</c> 的习惯开一个口子）。
/// </para>
/// <para>
/// 🔥 <b>2026-10-13 起是"明文 + 来源 IP 黑名单"</b>（此前是 HMAC 验签 + 白名单）。
/// 报文就是一行纯文本：<c>DOWN</c> / <c>RESTART</c> / <c>+</c> / <c>-</c> / <c>0</c> / <c>1</c>，
/// <b>任意来源都能发</b>，只有被点名拉黑的主机发不进来。
/// </para>
/// <para>
/// ⚠️ <b>这是一次主动降低安全强度的取舍</b>（原设计写着"无签名的版本绝不上线"）：
/// 它换来的是"用任意工具发一个 DOWN 就能用"。代价是
/// <b>同网段任意主机都能把设备关掉</b>，因此这条通道的端口<b>绝不能暴露到不可信网络</b>。
/// 要收紧就两件事：把该关的机器在「客户端」页上关掉通道，或把可疑主机填进黑名单。
/// </para>
/// </summary>
/// <param name="Enabled">是否启用。<b>默认 true</b>；关闭时客户端完全不绑定该端口（不是"绑定了但丢弃"）。</param>
/// <param name="Port">监听端口。默认 <b>8090</b>（刻意避开本项目 HTTP 的 8899，见 `AgentConfigDefaults.DefaultUdpPort`），由服务端下发，可随时改。</param>
/// <param name="BlockedSourceIps">
/// 来源 IP <b>黑名单</b>：<b>在名单里的来源 IP 发来的指令一律丢弃</b>。
/// <para><b>为空 = 不拉黑任何来源</b>（默认）。名单按 IP 精确比较，不支持网段。</para>
/// <para>
/// 语义上它是"防误触 / 防一台乱发的机器"，<b>不是一道安全边界</b>：
/// 伪造源 IP 的 UDP 报文能绕过它（UDP 没有握手，源地址不可信）——
/// 真正的边界是"这条通道只在可信网络里可达"。
/// </para>
/// </param>
public sealed record AgentUdpChannelDto(
    bool Enabled = true,
    int Port = 8090,
    IReadOnlyList<string>? BlockedSourceIps = null);

/// <summary>
/// 一条「发送」任务：延时发一次，或（<see cref="IsLoop"/> 时）循环发。
/// <para>
/// 字段与参考项目的 <c>SendTask</c> 对齐；协议 / 数据类型取值沿用本项目的
/// <c>TransportProtocols</c>（UDP/TCP）与 <c>DataTypes</c>（String/Hex/ANSI/ASCII）。
/// 这里用 <see cref="string"/> 而不是引用 Core 的常量类 —— 契约层是最底层，不能反过来依赖 Core。
/// </para>
/// </summary>
/// <param name="Id">稳定标识（前端生成；改配置后要能认出"还是那条任务"）。</param>
/// <param name="Data">待发送内容（<c>Hex</c> 类型时是十六进制串）。</param>
/// <param name="DelaySeconds">首次发送前的延时秒数（<b>相对于客户机服务启动</b>）。</param>
/// <param name="IsLoop">true = 循环发送（发送后每隔 <paramref name="LoopIntervalSeconds"/> 再发）。</param>
/// <param name="LoopIntervalSeconds">循环间隔秒数（仅 <see cref="IsLoop"/> 时有意义）。</param>
public sealed record AgentSendTaskDto(
    string Id,
    string Protocol,
    string TargetIp,
    int TargetPort,
    string DataType,
    string Data,
    int DelaySeconds = 0,
    bool IsLoop = false,
    int LoopIntervalSeconds = 60,
    string? Name = null,
    bool Enabled = true);

/// <summary>
/// 一条「定时发送」任务：每天在若干个 <c>HH:mm</c> 时刻各发一次。
/// <para>
/// 时刻用 <b>列表</b>（参考项目已从"单时间"升级为多时间，并保留单时间字段兼容旧配置）。
/// 本项目是新建的，**不再保留那个单值兼容字段** —— 旧配置的迁移由导入端负责，
/// 不该把历史包袱带进新表。
/// </para>
/// </summary>
/// <param name="Times">发送时刻，格式 <c>HH:mm</c>（24 小时制）。为空 = 该任务不触发。</param>
public sealed record AgentScheduledTaskDto(
    string Id,
    string Protocol,
    string TargetIp,
    int TargetPort,
    string DataType,
    string Data,
    IReadOnlyList<string>? Times = null,
    string? Name = null,
    bool Enabled = true);

/// <summary>
/// 一条「延时启动程序」任务（M4.5）。
/// <para>
/// <b>执行位置是会话侧（托盘进程），不是服务侧</b>：服务在 Session 0 里
/// <c>Process.Start</c> 出来的窗口用户**根本看不见**（没有桌面），表现为"程序启动了但没反应"。
/// 所以服务只是把这条任务交给托盘，由托盘在用户会话里启动。
/// </para>
/// </summary>
/// <param name="Name">界面显示名（程序名）。</param>
/// <param name="ProgramPath">程序完整路径。</param>
/// <param name="DelaySeconds">
/// 延时秒数。<b>多个任务时是"串行累加"</b>：第一个相对于客户机启动，后续相对于前一个任务完成
/// （照搬参考项目语义 —— 现场是靠这个顺序把多个程序依次拉起来的）。
/// </param>
public sealed record AgentLaunchTaskDto(
    string Id,
    string Name,
    string ProgramPath,
    string Argument = "",
    int DelaySeconds = 5,
    bool Enabled = true);

/// <summary>
/// 下发给某台客户机的完整配置。
/// <para>
/// <b>整包下发、整包替换</b>，不做差量：配置体量很小（几十条任务），
/// 差量协议带来的"两边版本对不上"远比省下的那点带宽贵。
/// </para>
/// </summary>
/// <param name="Version">
/// 配置版本号，<b>单调递增</b>。客户端据此判断"收到的这版是不是比手上这份新"
/// —— 乱序到达时旧版必须被丢弃，否则会出现"刚应用完新配置又被旧配置覆盖回去"。
/// <b>0 = 该机还没有配置</b>。
/// </param>
/// <param name="UpdatedAt">
/// 最后保存时间。<b>null = 还没有配置</b>（与 <see cref="Version"/> 为 0 是同一个时刻，
/// 但这里如实给 null 而不是拿"当前时间"或"注册时间"来凑 —— 那种凑出来的时间戳
/// 会让人以为"这台机器刚配过"）。
/// </param>
public sealed record AgentConfigDto(
    int Version,
    DateTimeOffset? UpdatedAt,
    AgentUdpChannelDto Udp,
    IReadOnlyList<AgentSendTaskDto> SendTasks,
    IReadOnlyList<AgentScheduledTaskDto> ScheduledTasks,
    IReadOnlyList<AgentLaunchTaskDto> LaunchTasks);

/// <summary>
/// 保存配置请求（<c>PUT /api/agents/{id}/config</c>）。
/// <para>
/// 全部可选：前端可以只改其中一块。<b>null = 不动那一块</b>，
/// 这与"传空数组 = 清空那一块"是两件不同的事（同 <c>AgentUpdateRequest</c> 的约定）。
/// </para>
/// </summary>
public sealed record AgentConfigSaveRequest(
    AgentUdpChannelDto? Udp = null,
    IReadOnlyList<AgentSendTaskDto>? SendTasks = null,
    IReadOnlyList<AgentScheduledTaskDto>? ScheduledTasks = null,
    IReadOnlyList<AgentLaunchTaskDto>? LaunchTasks = null);

/// <summary>
/// 保存配置的结果（<c>PUT /api/agents/{id}/config</c>）。
/// <para>
/// <b>校验不过时把全部问题一起返回</b>（<see cref="Message"/> 里用「；」连接）：
/// 一次改十条任务却只报第一条，会逼用户"改一轮、试一轮"。
/// </para>
/// </summary>
/// <param name="Config">保存成功后的配置（含新版本号）；失败时为 null。</param>
public sealed record AgentConfigSaveResult(
    bool Ok,
    string Status,
    string? Message,
    AgentConfigDto? Config);

/// <summary>「配置已变更」推送载荷（服务端 → Agent，走 <c>/hubs/agent</c>）。</summary>
/// <param name="Version">新版本号。客户端对比本地已应用版本，不一致才去拉全量。</param>
public sealed record AgentConfigChangedDto(int Version);

/// <summary>
/// 客户机把它<b>本机那份配置</b>推给中心（2026-10-14 的双向同步，走 <c>/hubs/agent</c>）。
///
/// <para>背景：从 2026-10-14 起客户机自己也有一份可写的配置（本机配置页），
/// 于是"事实来源只有服务端"（原决策 D5）被<b>双向·后写覆盖</b>取代 ——
/// 两边都能改，谁后写谁生效。这条消息就是"本机刚写的那一版"。</para>
///
/// <para><b>为什么带版本号与时间戳</b>（缺一不可）：</para>
/// <list type="bullet">
///   <item><paramref name="Version"/> 是客户机手上的版本号。它通常已经<b>跳过</b>中心那一档
///         （客户机保存时会取 <c>max(本机, 已知的中心版本) + 1</c>），所以"版本更高"就说明是本机后写的；</item>
///   <item><paramref name="UpdatedAt"/> 是那一版在客户机上的保存时刻。它兜住另一种情形：
///         客户机<b>从没成功同步过</b>就离线改了配置，那时它的版本号可能低于中心 ——
///         光看版本号会把这份改动判成"旧的"，而它其实是刚写的。</item>
/// </list>
///
/// <para>⚠️ 中心<b>不会照着它写版本号</b>：中心一律用"自己那份版本 + 1"落库，
/// 免得客户机的版本号把中心那条单调递增的水位带乱。客户机拿到的回执里带着中心的新版本号，
/// 照它对齐即可。</para>
/// </summary>
/// <param name="Version">客户机手上那一版的版本号。</param>
/// <param name="UpdatedAt">那一版在客户机上的保存时刻（可空：客户机的时钟不可信时为 null）。</param>
/// <param name="Content">
/// 整份配置（四块都发）。用的就是保存请求那个形状：<c>null</c> = 不动那一块。
/// 客户机推的是它<b>此刻生效</b>的整份，所以四块都会带上。
/// </param>
public sealed record AgentConfigPushDto(
    int Version,
    DateTimeOffset? UpdatedAt,
    AgentConfigSaveRequest Content);

/// <summary>
/// 客户端回报「配置已应用」（Agent → 服务端）。
/// <para>
/// 服务端据此在页面上显示"已下发 v5 / 已应用 v4"——<b>两者不一致时页面要能看出来</b>，
/// 否则"改了配置但机器没生效"会被当成"保存失败"，而其实是客户端还没应用上。
/// </para>
/// </summary>
/// <param name="Version">已成功应用的版本号（<b>不是收到的版本号</b>：解析失败 / 应用失败时不该推进它）。</param>
/// <param name="Ok">应用是否成功。</param>
/// <param name="Message">失败原因（成功时为 null）。</param>
public sealed record AgentConfigAppliedDto(int Version, bool Ok, string? Message = null);

/// <summary>
/// 客户机可执行的**电源操作**（M4.5）。
/// <para>
/// ⚠️ 与 <c>AgentCommandNames.Restart</c> 区分：那个重启的是 **Agent 服务**，
/// 这里的 <see cref="Restart"/> 重启的是**整台计算机**。两者混用会出现在
/// "想重启服务却把用户机器关了"这种事上，所以刻意分成两套常量。
/// </para>
/// </summary>
public static class AgentPowerActions
{
    /// <summary>关机（<c>shutdown /s</c>）。</summary>
    public const string Shutdown = "shutdown";

    /// <summary>重启计算机（<c>shutdown /r</c>）。</summary>
    public const string Restart = "restart";

    /// <summary>是否合法。</summary>
    public static bool IsValid(string? action)
        => action is Shutdown or Restart;

    /// <summary>中文显示名（仅展示用）。</summary>
    public static string DisplayName(string? action) => action switch
    {
        Shutdown => "关机",
        Restart => "重启",
        _ => action ?? string.Empty,
    };
}

/// <summary>电源操作请求体。</summary>
public sealed record AgentPowerRequest(string Action);

/// <summary>客户机可执行的**音量操作**（M4.5，执行在会话侧）。</summary>
public static class AgentVolumeActions
{
    /// <summary>提高音量。</summary>
    public const string Up = "up";

    /// <summary>降低音量。</summary>
    public const string Down = "down";

    /// <summary>静音。</summary>
    public const string Mute = "mute";

    /// <summary>解除静音。</summary>
    public const string Unmute = "unmute";

    /// <summary>是否合法。</summary>
    public static bool IsValid(string? action)
        => action is Up or Down or Mute or Unmute;

    /// <summary>中文显示名（仅展示用）。</summary>
    public static string DisplayName(string? action) => action switch
    {
        Up => "提高音量",
        Down => "降低音量",
        Mute => "静音",
        Unmute => "解除静音",
        _ => action ?? string.Empty,
    };
}

/// <summary>音量操作请求体。</summary>
public sealed record AgentVolumeRequest(string Action);

/// <summary>
/// <b>经 UDP 指令通道</b>下发一条指令的请求体（<c>POST /api/agents/{id}/udp-command</c>）。
/// <para>
/// 这是与 <see cref="AgentPowerRequest"/> / <see cref="AgentVolumeRequest"/> <b>不同的一条路径</b>：
/// 那两条走 SignalR（要求机器在线），这一条是服务端<b>直接向客户机的 UDP 指令通道端口</b>
/// 发一条<b>明文</b>报文 —— 它不问 SignalR 在不在，只要求那张网通、且那台机器开着通道。
/// </para>
/// </summary>
/// <param name="Command">
/// 线路 token。两种写法都收：
/// <list type="bullet">
///   <item><b>明文短码</b>：<c>DOWN</c> / <c>RESTART</c> / <c>+</c> / <c>-</c> / <c>0</c> / <c>1</c>；</item>
///   <item><b>本项目写法</b>：<c>power:shutdown</c> / <c>volume:up</c> 这类 <c>name:argument</c>。</item>
/// </list>
/// <para>
/// 刻意<b>不是</b> <c>{name, argument}</c> 两个字段：token 就是报文里那一段原文，
/// 用与客户机<b>同一个</b>解析器（<c>UdpCommandProtocol.TryResolveCommand</c>）校验，
/// 能保证"这里放行的"与"客户机能认的"永远一致 ——
/// 否则会出现"端点说合法、客户机却当垃圾丢掉"（改版前"指令无效果"的根因之一）。
/// </para>
/// </param>
public sealed record AgentUdpCommandRequest(string Command);
