namespace ControlServer.Contracts;

/// <summary>
/// 客户端主机的在线状态（M4）。
/// <para>
/// <b>只用字符串常量，不用 enum</b>：这些值要经 JSON 走过 SignalR（SignalR 默认用
/// <c>System.Text.Json</c>，enum 会序列化成数字，前端拿到的就是 <c>0</c>/<c>1</c>，
/// 一旦服务端与前端对枚举顺序的理解有偏差，界面会把"离线"画成"在线"）。
/// 字符串是自解释的，加一个状态也不会让已有值错位。
/// </para>
/// <para>
/// ⚠️ <b>判定只依赖心跳时间戳</b>（见 <c>AgentPresence</c>）：不做 ping、不做 TCP 探测 ——
/// 那些会把"网络抖一下"变成"设备离线"的假告警，而假告警会让运维彻底不信这个页面。
/// </para>
/// </summary>
public static class AgentStates
{
    /// <summary>心跳在有效窗口内。</summary>
    public const string Online = "online";

    /// <summary>心跳间断但尚未达到离线阈值（页面标注"数据可能过期"）。</summary>
    public const string Unstable = "unstable";

    /// <summary>超过离线阈值仍无心跳。</summary>
    public const string Offline = "offline";

    /// <summary>注册过但从未连上（首次注册后还没建立过长连接）。</summary>
    public const string Never = "never";

    /// <summary>管理员已把该机拉黑（保留记录，但拒绝它的任何连接）。</summary>
    public const string Blocked = "blocked";

    /// <summary>中文显示名（仅展示用，不参与任何比较）。</summary>
    public static string DisplayName(string? state) => state switch
    {
        Online => "在线",
        Unstable => "不稳定",
        Offline => "离线",
        Never => "等待连接",
        Blocked => "已拉黑",
        _ => state ?? string.Empty,
    };
}

/// <summary>客户端的一块网卡（注册时上报）。</summary>
/// <param name="Name">适配器名（如"以太网"）。</param>
/// <param name="Address">IPv4 地址（点分十进制）。</param>
/// <param name="Mac">MAC 地址（冒号分隔，大写）。</param>
/// <param name="IsUp">当前是否为"已连接"。</param>
public sealed record AgentNetworkInterfaceDto(string Name, string Address, string Mac, bool IsUp);

/// <summary>
/// 客户端上报的主机信息（注册时一次性上报，M4.2 起作为"硬件清单"的初版）。
/// </summary>
/// <param name="MachineKey">
/// 机器稳定标识（由机器码派生）。<b>在线表按它识别设备，不按 IP</b> ——
/// 客户机的 IP 会变（DHCP / 换网段），机器码不会。
/// </param>
/// <param name="LogicalProcessors">逻辑处理器数；取不到为 null（<b>不要用 0 代替</b>）。</param>
/// <param name="TotalMemoryMb">物理内存总量（MB）；取不到为 null（<b>不要用 0 代替</b>）。</param>
/// <param name="AgentVersion">客户端 Agent 版本（与服务端版本比对，用于提示"安装包不一致"）。</param>
public sealed record AgentHostInfoDto(
    string MachineKey,
    string HostName,
    string OsDescription,
    string? CpuName,
    int? LogicalProcessors,
    long? TotalMemoryMb,
    string AgentVersion,
    string? UserName,
    IReadOnlyList<AgentNetworkInterfaceDto> Interfaces);

/// <summary>
/// 注册请求。
/// <para>
/// <b>免凭据（2026-10-13 起）</b>：注册不再需要任何令牌或加入码 ——
/// 装好客户端、能发现到中心服务端，就自动登记并拿到 agentKey。
/// </para>
/// <para>
/// ⚠️ <b>这等于承认"同网段任意机器都能注册成一台客户机"</b>。原设计刻意不给这条路
/// （注释写着「绝不提供免凭据模式」），但产品决定是"装完即出现"，遂按此放开。
/// 兜底只剩在线表容量上限（<c>Server:Agent:MaxHosts</c>）与管理员的「拉黑」/「移除」；
/// 因此<b>中心服务的 HTTP 端口不该暴露到不可信网络</b>（端口映射 / 公网可达等于对外开放注册）。
/// </para>
/// <para>
/// 请求体里若仍带旧的 <c>enrollmentToken</c> / <c>joinCode</c> 字段，
/// 会被反序列化直接忽略（<c>System.Text.Json</c> 默认忽略未知成员）——
/// 这保证"旧安装包 + 新服务端"这个组合不会因为多两个字段而注册失败。
/// </para>
/// </summary>
/// <summary>
/// 客户机告知中心「本机要换到别的服务端地址去了」（2026-10-15，需求①）。
/// <para>
/// <b>它只带"要去哪"，不带任何指令</b>：服务端收到后只记一条日志（谁、从哪、去哪）。
/// 之所以要有这条，是为了把"被搬走了"和"掉线了"分开 —— 否则运维在中心侧看到的
/// 只是"那台机器忽然不心跳了"。
/// </para>
/// </summary>
/// <param name="ServerUrl">客户机即将连接的新地址（原样带回，便于日志里对照）。</param>
public sealed record AgentServerChangeRequest(string ServerUrl);

public sealed record AgentRegisterRequest(AgentHostInfoDto? HostInfo);

/// <summary>
/// 注册结果。
/// <para>
/// 结构照 <see cref="UserMutationResult"/> 的做法：<b>成败与否都返回完整状态</b>，
/// 让客户端能一句话说清"为什么没注册上"（缺机器标识 / 在线表已满 / 服务端写入失败）
/// —— 现场排障时这一句话就是全部线索。
/// </para>
/// </summary>
/// <param name="AgentKey">
/// <b>仅注册成功时返回一次</b>。客户端必须立刻落盘（DPAPI 保护）；
/// 服务端只存它的 SHA-256，<b>永远无法再次给出</b> —— 丢了只能移除记录再重新注册。
/// </param>
/// <param name="HeartbeatSeconds">服务端要求的心跳间隔（客户端据此定时上报）。</param>
public sealed record AgentRegisterResult(
    bool Ok,
    string Status,
    string? Message,
    int? AgentId,
    string? AgentKey,
    int? HeartbeatSeconds,
    string? ServerVersion,
    string? DisplayName);

/// <summary>
/// 在线表里的一台客户机（列表与详情共用）。
/// </summary>
/// <param name="State">见 <see cref="AgentStates"/>（已由服务端结合心跳时间与拉黑标记算好）。</param>
/// <param name="StateDisplay">状态的中文名（服务端给，避免前端硬编码一份对照表）。</param>
/// <param name="MachineKey">机器码派生标识（列表页展示时前端会做中间打码）。</param>
/// <param name="LastAddress">最后一次联系的来源地址（注册或心跳；从未联系过则为空）。</param>
/// <param name="Metrics">
/// 最新一帧遥测摘要（M4.2）。<b>null = 从未上报过或上报链路还没跑起来</b>
/// （不是"占用为 0"）—— 列表页据此显示「—」。
/// </param>
/// <param name="ConfigVersion">服务端当前下发给该机的配置版本（M4.5）。</param>
/// <param name="AppliedConfigVersion">
/// 客户机回报的「已应用版本」（M4.5）。
/// <para>
/// <b>null = 该机还没回报过</b>（老客户端 / 刚注册）。
/// 与 <paramref name="ConfigVersion"/> 不一致时，页面要能显示"已下发 v5 / 已应用 v4" ——
/// 否则"改了配置但没生效"会被误判成"保存失败"，而其实只是客户端还没应用上。
/// </para>
/// </param>
/// <param name="LastTargetDir">
/// 这台机器**上一次下发文件时用的落脚目录**（2026-10-13）。
/// <para>
/// 下发页把它预填进那一行的「存放路径」，省掉"每回都重新点一遍目录"。
/// <b>null = 还没记过</b>。
/// </para>
/// <para>
/// 🔥 2026-10-15 起：<b>只有"这一次确实指定了目录"才会回写</b>。从前一次
/// "存放路径留空"的建单会把它写成空串 —— 那等于<b>删掉运维刚存下的默认目录</b>，
/// 而屏幕上完全看不出来（现场表现就是"路径保存不了"）。
/// 今天空串只可能来自显式调 <c>PUT /api/agents/{id}/path-memory</c>。
/// </para>
/// </param>
/// <param name="LastSourceDir">
/// 上一次**回传**时读的目录（回传方向），语义同 <paramref name="LastTargetDir"/>。
/// <para>⚠️ 存的是**目录**而非完整文件路径 —— 预填时能直接当浏览器的落点。</para>
/// </param>
public sealed record AgentHostDto(
    int Id,
    string MachineKey,
    string DisplayName,
    string? Group,
    string? Note,
    string State,
    string StateDisplay,

    /// <summary>
    /// 已被管理员拉黑（2026-10-15，取代原「吊销」）。
    /// <para>
    /// 与「移除」的区别：**记录保留、agentKey 也保留**，只是不再接受它的连接 ——
    /// 所以"取消拉黑"之后那台机器<b>下一次重试就能连上</b>，不需要重新注册。
    /// </para>
    /// </summary>
    bool IsBlocked,

    string? AgentVersion,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? LastSeenAt,
    string? LastAddress,
    string? HostName,
    string? OsDescription,
    string? CpuName,
    int? LogicalProcessors,
    long? TotalMemoryMb,
    AgentMetricsSummaryDto? Metrics = null,
    int ConfigVersion = 0,
    int? AppliedConfigVersion = null,
    // LastTargetDir / LastSourceDir（2026-10-13）：路径记忆。
    // ⚠️ 参数列表里只用 `//`，`///` 放不进这里。完整说明见顶部 `<param>` 块。
    string? LastTargetDir = null,
    string? LastSourceDir = null,
    // UdpChannel（2026-10-14）：客户机**自己报上来的** UDP 通道实时态（随心跳）。
    // ⚠️ 它是**进程内**的：中心重启后为 null（下次心跳即补上）。null 的语义是
    //    「还不知道」—— 与「知道它没在听」是两件事，页面上必须分开说。
    AgentUdpChannelStateDto? UdpChannel = null);

// ⚠️ **显卡不在这里**（2026-10-13 曾短暂加过 `GpuName` / `GpuCount`，同日移除）。
//
// 理由：在线表的每一行每 60 秒重建一次，而显卡只在一台机器的**硬件清单**里
// （`GET /api/agents/{id}/inventory`，6 小时一次的全量大对象）。为了在列表里多显示
// 一格型号，要额外维护"清单 → 内存摘要 → 水合"一整套链路（含中心重启后最长 6 小时
// 的空白窗口），代价与收益不成比例。
//
// 显卡型号现在只在**详情页的「硬件清单」**里显示（那里本来就有完整的一块），
// 且详情页折叠时也会在标题行带出摘要 —— 见 `AgentDetailPanel.vue`。

/// <summary>在线表 + 计数（计数由服务端算好，前端不必自己 filter 一遍）。</summary>
public sealed record AgentListDto(
    IReadOnlyList<AgentHostDto> Items,
    int Total,
    int Online,
    int Unstable,
    int Offline);

/// <summary>改名 / 分组 / 备注。三者都可空，只改传了的那些。</summary>
public sealed record AgentUpdateRequest(string? DisplayName, string? Group, string? Note);

/// <summary>
/// 记住某台客户机的「默认目录」（2026-10-14）。
///
/// <para>
/// <b>它解决什么</b>：下发 / 回传的路径输入框过去只在**建单成功之后**才被中心记住
/// （`agent_hosts.LastTargetDir` 由建单流程回写）。于是"挑对了目录、但这次先不建单"
/// 就白挑了 —— 下次打开还得再点进去找一遍，而"从盘符一层层点进去"正是这套界面最烦的一步。
/// 这个端点是"把人挑的结果直接存下来"的显式动作。
/// </para>
///
/// <para>⚠️ 两者<b>都可空，且 null = 不动那一项</b>（与 <see cref="AgentUpdateRequest"/> 同一约定）：
/// 下发方向只该动 <paramref name="TargetDir"/>，回传方向只该动 <paramref name="SourceDir"/> ——
/// 一次只挑了一边，不该把另一边顺手清成 null。</para>
///
/// <para>⚠️ <b>它们只是"预填值"，不是权限</b>：真正拦越界的是客户机自己的白名单
/// （见 <c>FileTransferClient</c>），这里存什么都不会放宽那条边界。</para>
/// </summary>
/// <param name="TargetDir">下发文件时默认的存放目录；null = 不改。</param>
/// <param name="SourceDir">回传文件时默认读取的目录；null = 不改。</param>
public sealed record AgentPathMemoryRequest(string? TargetDir, string? SourceDir);

/// <summary>
/// 一台客户机被移除 / 拉黑等写操作的结果（携带最新全量列表，前端一次刷新到位）。
/// </summary>
public sealed record AgentMutationResult(
    bool Ok,
    string Status,
    string? Message,
    AgentListDto? List);

/// <summary>注册令牌 / 加入码的 DTO 已随「取消准入令牌」一并移除（2026-10-13）。</summary>

/// <summary>
/// 客户端安装包的元信息（顶栏按钮据此决定"能不能下 / 是否要提示版本不一致"）。
/// </summary>
/// <param name="Available">数据目录的 <c>downloads\</c> 下是否有安装包。</param>
/// <param name="Version">从文件名解析出的客户端版本（解析不到则 null）。</param>
/// <param name="VersionMismatch">
/// 主次版本与服务端不一致 ⇒ 页面给黄点提示。
/// 这是**提示而非阻断**：现场常有"客户端版本落后一版"的过渡期，不允许下载反而更糟。
/// </param>
public sealed record AgentInstallerInfoDto(
    bool Available,
    string? FileName,
    long SizeBytes,
    string? Version,
    string ServerVersion,
    bool VersionMismatch,
    string? Hint);

/// <summary>单台客户机的详情（列表项 + 完整主机信息）。</summary>
/// <param name="Connected">当前是否握着一条活跃长连接（与 <see cref="AgentHostDto.State"/> 不完全等价：
/// 状态是"最近有心跳"，连接是"此刻在线路那头"）。</param>
// ⚠️ 这里用 `//` 而不是 `///` 注释：参数列表里的 `///` 会被文档生成器当成
//    "放在非法语法元素上的 XML 注释"，在 0 警告的构建策略下直接失败。
//
// 历史上还有过一个 `UdpChannelReady`（"服务端是否持有该机的 UDP 通道密钥"）——
// 2026-10-13 明文化之后通道不再需要密钥，那个字段**连同它的整条上报链路**一并删除。
// 现在"能不能从中心发 UDP"只取决于三件事：网通、知道对方地址、对方启用了通道。
public sealed record AgentDetailDto(
    AgentHostDto Host,
    AgentHostInfoDto? Info,
    bool Connected);

/// <summary>
/// 客户端连上 <c>/hubs/agent</c> 后服务端发的第一条消息。
/// <para>
/// <b>为什么由服务端下发心跳间隔</b>：间隔是运维参数（机器多就调大、要更灵敏就调小），
/// 若烧死在客户端里，改它就得重装所有客户机。下发之后，改配置 + 重连即生效。
/// </para>
/// </summary>
/// <param name="DisplayName">服务端记录里的显示名（客户端托盘气泡会用它，让用户看到管理员给它起的名字）。</param>
/// <param name="Telemetry">
/// 遥测节奏（M4.2）。<b>null = 服务端没启用遥测</b> —— 客户端据此完全不上报，
/// 而不是"上报了被拒"（后者会白耗客户机的 CPU 和网络）。
/// </param>
public sealed record AgentWelcomeDto(
    int AgentId,
    string DisplayName,
    string ServerVersion,
    int HeartbeatSeconds,
    int ConfigVersion,
    DateTimeOffset ServerTime,
    AgentTelemetrySettingsDto? Telemetry = null,

    /// <summary>
    /// 服务端**手上没有这台机器的硬件清单**，请客户端立刻补报一份（2026-10-15）。
    /// <para>
    /// <b>为什么必须由服务端说</b>：清单的排期由客户端**本地持久化**的"上次上报时刻"
    /// 决定（周期默认 6 小时，见 <c>AgentTelemetryOptions.InventoryHours</c>）。
    /// 而"服务端这边有没有这份清单"客户端<b>没法知道</b> —— 一旦记录被删掉
    /// （管理员「移除」客户机、或那台机器重新注册拿到新 Id），服务端就是一张白纸，
    /// 而客户端还以为"6 小时前刚报过"，于是<b>最长 6 小时不会再报</b>。
    /// 现场表现就是"这台客户机不上报硬件清单"。
    /// </para>
    /// <para>
    /// ⚠️ 默认 <c>false</c>：老服务端不会带这个字段（反序列化后取默认值），
    /// 于是新客户端对老服务端的行为**与改之前完全一致**，不会多报。
    /// </para>
    /// </summary>
    bool NeedsInventory = false);

/// <summary>
/// 服务端下发的遥测节奏（M4.2）。
/// <para>
/// <b>为什么由服务端下发而不是客户端自己定</b>：与心跳间隔同理 ——
/// 采样频率直接决定库长多快、客户机多耗电。做成下发之后，"设备从 20 台涨到 300 台"
/// 只需要在服务端改一个数（甚至改配置热重启），不必重新去每台客户机上改配置。
/// </para>
/// </summary>
/// <param name="Enabled">是否启用遥测。</param>
/// <param name="MetricsSeconds">指标采样间隔（秒）。</param>
/// <param name="PowerEventPollSeconds">开关机事件轮询间隔（秒）。</param>
/// <param name="InventoryHours">硬件清单上报间隔（小时）。</param>
/// <param name="PowerEventBackfillDays">
/// 首次上报时的回溯天数。<b>0 = 不回溯</b>（只在服务端已确认过水位时才有意义）。
/// </param>
public sealed record AgentTelemetrySettingsDto(
    bool Enabled,
    int MetricsSeconds,
    int PowerEventPollSeconds,
    int InventoryHours,
    int PowerEventBackfillDays);

/// <summary>
/// 一次心跳的应答。
/// <para>
/// 回传 <paramref name="ServerTime"/> 是让客户端能发现自己时钟偏差并记一条本地日志 ——
/// 客户机时钟不准会让"开关机记录"的时间线（M4.2）整体偏移，那是现场最说不清的一类问题。
/// </para>
/// </summary>
public sealed record AgentHeartbeatAckDto(
    DateTimeOffset ServerTime,
    int HeartbeatSeconds,
    int ConfigVersion);

/// <summary>
/// 客户机此刻的 <b>UDP 指令通道状态</b>（2026-10-14 随心跳上报）。
///
/// <para><b>它解决什么</b>：在它之前，"这台机器能不能收 UDP 指令"这件事，中心只能<b>猜</b> ——
/// 猜的依据是「我下发过的配置里写着 Enabled=true」。而配置写着启用不等于它在听：
/// 本机可能停用了任务下发、端口可能被占用绑不上、配置可能还没应用。
/// 于是现场最常见的一幕是<b>中心说"已发出"、机器毫无反应</b>，两边都没错，只是各自相信了不同的真相。
/// </para>
///
/// <para>🔑 <b>权威在客户机</b>：这份状态由客户机的 <c>UdpCommandListener</c> <b>现读</b>产生
/// （不缓存），跟着心跳走，于是"中心看到的"与"机器实际的"最迟一个心跳周期就对齐。</para>
///
/// <para>⚠️ 它<b>不等于</b>"网线通、防火墙放行"：UDP 本就没有应答，
/// 这里报的是"端口绑着在听"，不是"你发的我一定收得到"。</para>
/// </summary>
/// <param name="Listening">此刻是否真的绑定了端口在听（<c>netstat</c> 里能看见这个端口）。</param>
/// <param name="Port">
/// 正在听的端口；没在听时为 null。
/// <para>🔥 <b>代发指令要用它，而不是用配置里的端口</b>：两者可能不一致（改了配置但还没应用、
/// 或者客户机拒绝了越界端口），拿配置端口发就是发给一个没人听的端口。</para>
/// </param>
/// <param name="StateText">
/// 客户机自己给的<b>人话</b>状态（没在听时带原因：「本机已在配置中停用任务下发」/
/// 「中心下发的配置没有启用 UDP 指令通道」/「监听 8090 端口失败：…」）。
/// 中心页面原样显示它，比自己总结一句"UDP 未监听"有用得多。
/// </param>
/// <param name="ExecuteEnabled">
/// 收到了会不会真的执行。false = <b>演练模式</b>（只识别不动手）——
/// 这一条能直接解释"指令收到了、也认出来了，但机器没反应"。
/// </param>
public sealed record AgentUdpChannelStateDto(
    bool Listening,
    int? Port = null,
    string? StateText = null,
    bool ExecuteEnabled = true);

/// <summary>服务端下发给客户端的指令（M4.1 只用到重启服务）。</summary>
public sealed record AgentCommandDto(string Name, string? Argument);

/// <summary>客户端指令名（服务端与客户端共用的常量，避免两边字符串漂移）。</summary>
public static class AgentCommandNames
{
    /// <summary>
    /// 重启 Agent 服务。
    /// <para>
    /// ⚠️ <b>这不是重启计算机</b> —— 重启整机用 <see cref="Power"/> + <see cref="AgentPowerActions.Restart"/>。
    /// 混用会出现"想重启服务却把用户机器关了"，两者必须分开。
    /// </para>
    /// </summary>
    public const string Restart = "restart";

    /// <summary>重新拉取配置（M4.5 起真正生效：客户端重新拉全量配置并应用；M4.1 只是让客户端重新上报主机信息）。</summary>
    public const string RefreshConfig = "refresh-config";

    /// <summary>
    /// 电源操作。<c>Argument</c> 取 <see cref="AgentPowerActions"/> 之一（<c>shutdown</c> / <c>restart</c>）。
    /// <para>服务侧执行（服务是 SYSTEM，<c>shutdown</c> 天然有权限）。</para>
    /// </summary>
    public const string Power = "power";

    /// <summary>
    /// 音量操作。<c>Argument</c> 取 <see cref="AgentVolumeActions"/> 之一（<c>up</c> / <c>down</c> / <c>mute</c> / <c>unmute</c>）。
    /// <para>
    /// <b>服务侧收到后要转交给会话侧的托盘执行</b>：Session 0 里没有音频会话，
    /// 服务进程自己改音量是"无声失败"（改的是 Session 0 的音频，用户听不到任何变化）。
    /// </para>
    /// </summary>
    public const string Volume = "volume";

    /// <summary>
    /// 远程桌面会话（M5.1）：开始 / 停止推流、切黑白。
    /// <c>Argument</c> 是 <see cref="RemoteSessionCommandDto"/> 的 JSON。
    ///
    /// <para>
    /// 🔥 <b>它必须由<b>会话侧</b>（托盘）执行，服务只负责转达</b>：
    /// 抓屏要求当前进程有桌面，而服务跑在 Session 0 —— 那里没有桌面。
    /// 服务收到后经会话管道把 <c>remote-stream</c> 转给托盘
    /// （与音量走的是同一条路，理由也相同）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 它<b>受演练模式（<c>Agent:Tasks:ExecuteUdpCommands</c>）约束</b>：那道闸的语义是
    /// "本机此刻敢不敢动手"，而"开始把桌面推出去"显然属于动手。
    /// 让它绕过闸门，那道闸就只是个标签。
    /// </para>
    /// </summary>
    public const string RemoteSession = "remote-session";
}
