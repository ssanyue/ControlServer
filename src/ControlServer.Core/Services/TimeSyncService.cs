using ControlServer.Core.Abstractions;

namespace ControlServer.Core.Services;

/// <summary>
/// 时间同步的运行时参数（由宿主从配置绑定后注入，Core 不读配置文件）。
/// </summary>
/// <param name="NtpServers">按顺序尝试的 NTP 服务器；第一个成功即停止。</param>
/// <param name="Timeout">单个服务器的查询超时。</param>
/// <param name="WarnThreshold">偏差告警阈值：超过就打警告（不拒绝触发，见 M2 方案 §5.4）。</param>
public sealed record TimeSyncSettings(
    IReadOnlyList<string> NtpServers,
    TimeSpan Timeout,
    TimeSpan WarnThreshold)
{
    /// <summary>
    /// 内置默认 NTP 服务器（国内，按优先级排序）。
    /// <para>
    /// 这是<b>唯一</b>的内置默认来源。宿主的 <c>TimeOptions.NtpServers</c> 刻意留空，
    /// 由组合根在列表为空时回退到这里 —— 否则配置绑定会对列表做<b>追加</b>，
    /// 让默认值翻倍且用户无法删除。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultNtpServers =
        new[] { "ntp.aliyun.com", "ntp.tencent.com", "cn.pool.ntp.org" };
}

/// <summary>
/// 一次校准的结果。
/// </summary>
/// <param name="Ok">是否成功（至少一个服务器给出可用响应）。</param>
/// <param name="Offset">本次采用的偏移量；失败时为当前仍生效的偏移量。</param>
/// <param name="Source">成功的服务器；失败为 null。</param>
/// <param name="RoundTrip">往返耗时；失败为 null。</param>
/// <param name="OffsetJump">相对上一次生效偏移量的变化量（失败时为 <see cref="TimeSpan.Zero"/>）。</param>
/// <param name="Error">失败原因汇总；成功为 null。</param>
/// <param name="Attempts">逐个服务器的尝试记录，便于排障。</param>
public sealed record TimeSyncOutcome(
    bool Ok,
    TimeSpan Offset,
    string? Source,
    TimeSpan? RoundTrip,
    TimeSpan OffsetJump,
    string? Error,
    IReadOnlyList<string> Attempts);

/// <summary>
/// 应用内时钟校准服务（M2 方案 §5.6）。
/// <para>
/// <b>只做应用内校正，不改系统时钟</b>：把 SNTP 得到的偏移写入
/// <see cref="BeijingTime.ClockOffset"/>，此后 <see cref="BeijingTime.Now"/> 全应用统一吃校正
/// （日志时间戳、配置 <c>UpdatedAt</c>、收发日志、定时器判定）。
/// </para>
/// <para>
/// <b>失败绝不降级成"把偏移清 0"</b>：上次校准的结果仍然有效，清 0 反而会让时间倒退。
/// 失败只更新 <see cref="LastError"/> 并保留既有偏移。
/// </para>
/// <para>
/// 这个类是 <see cref="BeijingTime.ClockOffset"/> 的<b>唯一写入者</b>，因此偏移不会有两个真相源。
/// </para>
/// </summary>
public sealed class TimeSyncService
{
    private readonly ISntpClient _client;
    private readonly TimeSyncSettings _settings;
    private readonly LogService _log;

    /// <summary>串行化：手动校准与后台周期校准可能同时来，不能让两个结果互相覆盖。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private long _attemptCount;
    private long _failureCount;

    public TimeSyncService(ISntpClient client, TimeSyncSettings settings, LogService log)
    {
        _client = client;
        _settings = settings;
        _log = log;
    }

    /// <summary>当前生效的偏移量（等于 <see cref="BeijingTime.ClockOffset"/>）。</summary>
    public TimeSpan Offset => BeijingTime.ClockOffset;

    /// <summary>最近一次成功校准的本地时刻（UTC），用于算"多久没校准了"。</summary>
    public DateTimeOffset? LastSyncAtUtc { get; private set; }

    /// <summary>
    /// 最近一次成功校准时，<b>NTP 服务器报出的</b> UTC 时刻；从未成功过为 <c>null</c>。
    /// <para>
    /// <b>与 <see cref="LastSyncAtUtc"/> 的区别很重要</b>：后者是"本机在那一刻的读数"
    /// （仍受本机时钟影响），前者是"网络说现在是几点"。
    /// 授权体系的 <c>ivt</c> 联网锚点必须用<b>后者</b> —— 否则把系统时钟往回拨、
    /// 再重新取一次锚点，锚点会跟着回拨，防线自相矛盾。
    /// </para>
    /// </summary>
    public DateTimeOffset? LastNetworkUtc { get; private set; }

    /// <summary>最近一次成功的服务器。</summary>
    public string? LastSource { get; private set; }

    /// <summary>最近一次成功的往返耗时。</summary>
    public TimeSpan? LastRoundTrip { get; private set; }

    /// <summary>最近一次失败的原因（成功后清空）。</summary>
    public string? LastError { get; private set; }

    /// <summary>是否已成功校准过。</summary>
    public bool IsSynchronized => LastSyncAtUtc is not null;

    /// <summary>累计校准次数（含失败）。</summary>
    public long AttemptCount => Interlocked.Read(ref _attemptCount);

    /// <summary>累计失败次数。</summary>
    public long FailureCount => Interlocked.Read(ref _failureCount);

    /// <summary>
    /// **生效**的 NTP 服务器列表（已应用内置默认回退）。
    /// <para>诊断端点回显它而不是回显配置原文 —— 配置为空时会回退到内置默认，两者不是一回事。</para>
    /// </summary>
    public IReadOnlyList<string> Servers => _settings.NtpServers;

    /// <summary>
    /// 执行一次校准：按 <see cref="TimeSyncSettings.NtpServers"/> 顺序尝试，第一个成功即采用。
    /// <para>本方法不抛异常 —— 校准失败是可预期的运行状态，由调用方按返回值处理。</para>
    /// </summary>
    public async Task<TimeSyncOutcome> SyncAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref _attemptCount);

            var attempts = new List<string>();

            foreach (var server in _settings.NtpServers)
            {
                if (string.IsNullOrWhiteSpace(server))
                {
                    continue;
                }

                try
                {
                    var result = await _client
                        .QueryAsync(server, _settings.Timeout, cancellationToken)
                        .ConfigureAwait(false);

                    var jump = Apply(result, server);
                    attempts.Add($"{server}: 成功（偏移 {FormatOffset(result.Offset)}，往返 {result.RoundTrip.TotalMilliseconds:F0}ms）");

                    return new TimeSyncOutcome(true, result.Offset, server, result.RoundTrip, jump, null, attempts);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 单个服务器失败不算失败，继续试下一个 —— 这正是"多服务器列表"的意义。
                    attempts.Add($"{server}: {ex.Message}");
                }
            }

            Interlocked.Increment(ref _failureCount);
            LastError = attempts.Count == 0
                ? "未配置任何 NTP 服务器"
                : string.Join("；", attempts);

            _log.Warn("TimeSync", $"SNTP 校准失败：{LastError}（继续沿用当前偏移 {FormatOffset(BeijingTime.ClockOffset)}）");

            return new TimeSyncOutcome(false, BeijingTime.ClockOffset, null, null, TimeSpan.Zero, LastError, attempts);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 把校准结果写入 <see cref="BeijingTime.ClockOffset"/>，并按偏差大小决定是否告警。
    /// </summary>
    /// <returns>相对上一次生效偏移量的变化量。</returns>
    private TimeSpan Apply(SntpQueryResult result, string server)
    {
        var previous = BeijingTime.ClockOffset;
        var jump = result.Offset - previous;

        BeijingTime.ClockOffset = result.Offset;
        LastSyncAtUtc = DateTimeOffset.UtcNow;
        LastNetworkUtc = result.ServerUtcTime;
        LastSource = server;
        LastRoundTrip = result.RoundTrip;
        LastError = null;

        var absolute = result.Offset.Duration();

        if (previous == TimeSpan.Zero)
        {
            _log.Info("TimeSync",
                $"时钟已校准：偏移 {FormatOffset(result.Offset)}（来源 {server}，往返 {result.RoundTrip.TotalMilliseconds:F0}ms）");
        }
        else
        {
            _log.Info("TimeSync",
                $"时钟已重新校准：偏移 {FormatOffset(result.Offset)}（较上次变化 {FormatOffset(jump)}，来源 {server}）");
        }

        if (absolute >= _settings.WarnThreshold)
        {
            // §5.4 的第三条：偏差超阈值要告警。这里**只告警不拒绝触发** ——
            // 拒绝触发会让"系统钟不准"变成"定时器完全不工作"，那是更糟的失败模式。
            _log.Warn("TimeSync",
                $"本地时钟与 NTP 偏差达 {FormatOffset(result.Offset)}（阈值 {FormatOffset(_settings.WarnThreshold)}），" +
                "已按应用内校正处理（未修改系统时钟）。请检查服务器时间同步配置。");
        }

        if (previous != TimeSpan.Zero && jump.Duration() >= _settings.WarnThreshold)
        {
            // 这条必须显式打出来：日志时间戳会在这一刻向前/向后跳，事后排查"日志时间对不上"时是第一现场证据。
            _log.Warn("TimeSync",
                $"⚠️ 时钟校正量发生跳变 {FormatOffset(jump)}，日志时间戳将随之跳变" +
                "（滚动文件名仍跟随系统钟，可能短暂出现'行内日期与文件名不一致'）。");
        }

        return jump;
    }

    /// <summary>把偏移格式化成带符号的可读文本，如 <c>+0.128s</c> / <c>-1.500s</c> / <c>+0s</c>。</summary>
    public static string FormatOffset(TimeSpan value)
        => $"{(value < TimeSpan.Zero ? "-" : "+")}{Math.Abs(value.TotalSeconds):0.###}s";
}
