namespace ControlServer.Contracts;

/// <summary>
/// 时间同步详情（<c>GET /api/system/info</c> 内嵌）。
/// </summary>
/// <param name="AutoSync">宿主是否会在启动与周期自动校准（<c>Server:Time:AutoSync</c>）。</param>
/// <param name="ResyncIntervalSeconds">自动重校间隔（秒）；<paramref name="AutoSync"/> 为 false 时无意义。</param>
/// <param name="Synchronized">是否至少成功校准过一次。</param>
/// <param name="LastSyncAt">上一次成功校准的时刻（**北京时间**）；从未成功为 <c>null</c>。</param>
/// <param name="Source">上一次成功所用的 NTP 服务器。</param>
/// <param name="OffsetMs">当前生效的时钟偏移（毫秒，正 = 系统钟慢，需要往后加）。</param>
/// <param name="OffsetText">偏移的展示文本（如 <c>+0.128s</c> / <c>-1.5s</c>）。</param>
/// <param name="RoundTripMs">上一次校准的往返耗时（毫秒）。</param>
/// <param name="Corrected">当前是否处于"已校正"状态（偏移非零）。</param>
/// <param name="LastError">最后一次失败的原因；成功后会被清空。</param>
/// <param name="AttemptCount">本进程生命周期内累计校准尝试次数。</param>
/// <param name="FailureCount">本进程生命周期内累计失败次数。</param>
/// <param name="Servers">**生效**的 NTP 服务器列表（已应用内置默认回退，见 M2-D 记录的"列表追加"陷阱）。</param>
public sealed record TimeSyncInfoDto(
    bool AutoSync,
    double ResyncIntervalSeconds,
    bool Synchronized,
    DateTimeOffset? LastSyncAt,
    string? Source,
    long OffsetMs,
    string OffsetText,
    long? RoundTripMs,
    bool Corrected,
    string? LastError,
    long AttemptCount,
    long FailureCount,
    IReadOnlyList<string> Servers);

/// <summary>
/// 一次手动校准的结果（<c>POST /api/system/timesync</c>）。
/// <para>
/// 成功与失败都返回 <b>200</b>：这是"查校准结果"的接口，失败原因本身就是要展示的信息
/// （与 <c>POST /api/actions/preview</c> 的口径一致）。调用方看 <see cref="Ok"/>。
/// </para>
/// </summary>
/// <param name="Ok">是否校准成功。</param>
/// <param name="OffsetMs">校准后生效的偏移（毫秒）。</param>
/// <param name="OffsetText">偏移的展示文本。</param>
/// <param name="OffsetJumpMs">本次校准相对上一次的偏移变化（毫秒）。</param>
/// <param name="Source">成功时命中的 NTP 服务器。</param>
/// <param name="RoundTripMs">往返耗时（毫秒）。</param>
/// <param name="Error">失败原因；成功为 <c>null</c>。</param>
/// <param name="Attempts">逐个服务器的尝试记录，便于排障。</param>
/// <param name="ServerTime">校准后的服务端时间（北京时间）。</param>
public sealed record TimeSyncResultDto(
    bool Ok,
    long OffsetMs,
    string OffsetText,
    long OffsetJumpMs,
    string? Source,
    long? RoundTripMs,
    string? Error,
    IReadOnlyList<string> Attempts,
    DateTimeOffset ServerTime);

/// <summary>
/// 当前 HTTP 监听地址（<c>GET /api/system/address</c>）。
/// <para>
/// <see cref="Urls"/> 读的是 <b>Kestrel 实际在监听的地址</b>（<c>IServerAddressesFeature</c>），
/// 不是配置里的值 —— 热切换之后这两者会短暂或永久不一致，界面必须以实际值为准。
/// </para>
/// </summary>
/// <param name="Urls">当前实际监听的地址列表。</param>
/// <param name="Port">首个地址的端口（解析不出来为 0）。</param>
/// <param name="OverrideFile">端口持久化文件路径（改端口后下次启动从它恢复）。</param>
/// <param name="Persisted">该持久化文件当前是否存在（存在 = 端口不是来自默认值）。</param>
public sealed record ServerAddressDto(
    IReadOnlyList<string> Urls,
    int Port,
    string OverrideFile,
    bool Persisted);

/// <summary>
/// 请求热切换监听地址（<c>POST /api/system/address</c>）。
/// <para><see cref="Urls"/> 与 <see cref="Port"/> 二选一：给了 <see cref="Urls"/> 就用它，否则用当前主机名 + <see cref="Port"/> 拼一个。</para>
/// </summary>
/// <param name="Urls">
/// 完整监听地址，如 <c>http://0.0.0.0:9000</c>；空则用 <see cref="Port"/> 拼。
/// <para>
/// <b>一次只能给一个地址</b>（多给一律 <c>Invalid</c>）：热切换只作用于<b>主监听地址</b>，
/// 若允许一次给多个，就会出现"只切了第一个、却把全部写进持久化文件"的假成功
/// —— 运行期与重启后行为不一致（重启后多出来的地址会真的被绑上）。
/// 需要同时监听多个地址，请直接编辑配置文件里的 <c>Server:Http:Urls</c>（分号分隔）后重启。
/// </para>
/// </param>
/// <param name="Port">只改端口时用；主机部分沿用当前第一个地址。</param>
public sealed record ServerAddressChangeRequest(
    IReadOnlyList<string>? Urls,
    int? Port);

/// <summary>
/// 热切换的结果（<c>POST /api/system/address</c>）。
/// <para>
/// <b>切换是同步完成的</b>：响应用户请求时先在新端口 <c>BindAsync</c>，成功了才解绑旧端口，
/// 因此返回 200 就意味着"新端口此刻已经在听"。承载这个请求的连接之所以不会被中断，
/// 靠的是旧监听器<b>只解绑、延迟释放</b>（见 <c>SwitchableConnectionListener</c>），
/// 而不是靠"先返回、稍后再切"那套编排 —— 所以本结果里没有任何延迟字段。
/// </para>
/// <para>
/// 失败在 <b>同步阶段</b>就被端口预检拦下了（<c>PortUnavailable</c> → 409，监听完全不动）；
/// 预检之后的意外失败会回滚，并通过 <see cref="EventNames.ServerAddressChanged"/> 广播出去。
/// </para>
/// </summary>
/// <param name="Ok">是否成功（<c>NoChange</c> 也为 true —— 目标即现状，无需动作）。</param>
/// <param name="Status">结果状态：<c>Accepted</c> / <c>NoChange</c> / <c>Invalid</c> / <c>PortUnavailable</c> / <c>NotReady</c>。</param>
/// <param name="OldUrls">切换前的地址。</param>
/// <param name="NewUrls">切换后的地址（失败时为原始请求值，便于前端回显）。</param>
/// <param name="NewPort">切换后的端口（解析不出来为 null）。</param>
/// <param name="SecondsUntilSwitch">
/// <b>恒为 0，保留仅为兼容</b>：切换是同步的，响应返回时新端口已就绪，没有需要等待的时间。
/// 前端自己的"N 秒后跳转新地址"倒计时与此字段无关。
/// </param>
/// <param name="Message">给人看的一句话。</param>
public sealed record ServerAddressChangeResultDto(
    bool Ok,
    string Status,
    IReadOnlyList<string> OldUrls,
    IReadOnlyList<string> NewUrls,
    int? NewPort,
    int SecondsUntilSwitch,
    string Message);

/// <summary>
/// 监听地址热切换的实时通知（<c>ServerAddressChanged</c>）。
/// </summary>
/// <param name="Phase">
/// <c>switching</c> 即将切换（旧端口仍活着，前端应开始倒计时跳转）/
/// <c>changed</c> 已切换完成 / <c>failed</c> 切换失败（已回滚或回滚也失败，看 <see cref="Error"/>）。
/// </param>
/// <param name="OldUrls">切换前地址。</param>
/// <param name="NewUrls">目标地址。</param>
/// <param name="NewPort">目标端口。</param>
/// <param name="Error">失败原因；非 failed 阶段为 null。</param>
/// <param name="Message">给人看的一句话。</param>
public sealed record ServerAddressChangedDto(
    string Phase,
    IReadOnlyList<string> OldUrls,
    IReadOnlyList<string> NewUrls,
    int? NewPort,
    string? Error,
    string Message);

/// <summary>
/// 定时调度摘要（<c>GET /api/system/info</c> 内嵌）。
/// <para>逐条触发点清单走 <c>GET /api/timers</c>，这里只给计数，避免系统信息页承载大数组。</para>
/// </summary>
/// <param name="Enabled">调度是否启用。</param>
/// <param name="TickIntervalSeconds">节拍（秒）。</param>
/// <param name="CatchUpWindowSeconds">进程内追赶窗口（秒）。</param>
/// <param name="RetentionDays">触发记录保留天数。</param>
/// <param name="FiredCount">本进程累计触发次数。</param>
/// <param name="MissedCount">本进程累计被跳过的漏触发条数。</param>
/// <param name="TriggerCount">当前生效触发点个数。</param>
/// <param name="NextFireAt">下一个将要触发的时刻；算不出来为 <c>null</c>。</param>
public sealed record TimerSummaryDto(
    bool Enabled,
    double TickIntervalSeconds,
    double CatchUpWindowSeconds,
    int RetentionDays,
    long FiredCount,
    long MissedCount,
    int TriggerCount,
    DateTimeOffset? NextFireAt);

/// <summary>
/// 系统信息（<c>GET /api/system/info</c>）—— 面向运维的诊断详情。
/// <para>
/// <b>与 <c>GET /api/status</c> 的分工</b>：<c>/api/status</c> 是控制台首页的轻量运行状态
/// （REST 与 SignalR 共用同一份来源，字段少、变化慢）；这里则是"排查问题时才看"的细节
/// （时间同步偏移 / 上次校准 / 来源 / 定时计数）。
/// 两者字段有重叠是**有意的** —— 首页不该为了省一次请求而背上诊断负担。
/// </para>
/// <para>
/// <b>刻意不含机器码</b>：任何带 <c>CanOperate</c> 的角色（含 Operator）都能读这个端点，
/// 而机器码属于授权信息，仍只走 <c>GET /api/system/machine-code</c>（AdminOnly）。
/// </para>
/// </summary>
/// <param name="Product">产品名。</param>
/// <param name="Version">服务端版本。</param>
/// <param name="MachineName">机器名。</param>
/// <param name="OsPlatform">操作系统描述。</param>
/// <param name="ServerTime">服务端当前时间（**校正后的北京时间**）。</param>
/// <param name="TimeZone">时间口径说明。</param>
/// <param name="HttpPort">HTTP 监听端口。</param>
/// <param name="PairingPort">监听对码 UDP 端口。</param>
/// <param name="ListenerRunning">对码监听是否在跑。</param>
/// <param name="UptimeSeconds">已运行秒数。</param>
/// <param name="RecentLogCount">内存中保留的最近日志条数。</param>
/// <param name="DataDirectory">数据目录绝对路径（排查"配置去哪了"时最有用的一条）。</param>
/// <param name="TimeSync">时间同步详情。</param>
/// <param name="Timers">定时调度摘要。</param>
public sealed record SystemInfoDto(
    string Product,
    string Version,
    string MachineName,
    string OsPlatform,
    DateTimeOffset ServerTime,
    string TimeZone,
    int HttpPort,
    int PairingPort,
    bool ListenerRunning,
    long UptimeSeconds,
    int RecentLogCount,
    string DataDirectory,
    TimeSyncInfoDto TimeSync,
    TimerSummaryDto Timers);
