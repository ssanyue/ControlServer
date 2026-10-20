using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using ControlServer.Server.Options;
using ControlServer.Server.Services;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.HostedServices;

/// <summary>
/// 遥测的后台编排（M4.2）：落盘 / 推送 / 保留清理 / 日聚合。
/// <para>
/// 四个动作放在同一个 1 秒节拍里，各自按自己的间隔判断"到点了没"：
/// 一个循环比四个 <c>PeriodicTimer</c> 更好排障 —— 出问题时只有一条日志时间线，
/// 而且不会出现"两个任务同时抢写库"的竞态。
/// </para>
/// <para>
/// ⚠️ <b>每一条都必须独立 try/catch</b>：清理失败不该让落盘停摆，推送失败更不该。
/// 一个"因为清不掉旧数据所以连新数据也不写了"的服务，比不清理糟得多。
/// </para>
/// </summary>
public sealed class AgentTelemetryHostedService : BackgroundService
{
    /// <summary>节拍（秒）。所有间隔都是它的整数倍，所以它就是"最大额外延迟"。</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    /// <summary>保留清理的执行间隔（小时）。没必要更勤 —— 清理是删旧行，早一小时晚一小时没差别。</summary>
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    private readonly AgentTelemetryService _telemetry;
    private readonly IAgentTelemetryStore _store;
    private readonly AgentHostRegistry _registry;
    private readonly INotifier _notifier;
    private readonly LogService _log;
    private readonly IOptions<ServerOptions> _options;

    private DateTimeOffset _lastNotify = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;
    private DateOnly _lastRollupDay = DateOnly.MinValue;

    /// <summary>下次允许尝试日聚合的时刻（失败后要退避，否则会每秒重试一次把库压垮）。</summary>
    private DateTimeOffset _nextRollupAttempt = DateTimeOffset.MinValue;

    public AgentTelemetryHostedService(
        AgentTelemetryService telemetry,
        IAgentTelemetryStore store,
        AgentHostRegistry registry,
        INotifier notifier,
        LogService log,
        IOptions<ServerOptions> options)
    {
        _telemetry = telemetry;
        _store = store;
        _registry = registry;
        _notifier = notifier;
        _log = log;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var telemetry = _options.Value.Agent.Telemetry;

        _log.Info(
            "Telemetry",
            telemetry.Enabled
                ? $"遥测已启用：采样 {telemetry.MetricsSeconds} 秒 / 落盘 {telemetry.FlushSeconds} 秒 / "
                  + $"明细保留 {telemetry.DetailRetentionDays} 天 / 日聚合保留 {telemetry.DailyRetentionDays} 天。"
                : "遥测已关闭（Server:Agent:Telemetry:Enabled=false）—— 客户端上报会被拒绝，页面显示「未启用」。");

        // 日聚合补算：服务重启后如果跨了天，昨天的聚合可能还没算过。
        await RollupAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 铁律 #6：后台任务异常必须捕获并记录，绝不允许静默杀死进程。
                _log.Error("Telemetry", "遥测后台任务出错（下一次节拍继续）", ex);
            }

            try
            {
                await Task.Delay(Tick, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // 停机前把缓冲落一次盘：否则正常重启会白丢最多一个落盘周期的数据。
        try
        {
            await _telemetry.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("Telemetry", "停机前落盘失败（数据将丢失，影响最多一个落盘周期）", ex);
        }
    }

    private async Task TickOnceAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value.Agent.Telemetry;
        var now = BeijingTime.Now;

        // ── ① 实时推送：把这一段时间里变化过的机器推给网页 ──
        if (options.NotifyThrottleSeconds > 0
            && now - _lastNotify >= TimeSpan.FromSeconds(options.NotifyThrottleSeconds))
        {
            _lastNotify = now;
            await NotifyAsync(cancellationToken).ConfigureAwait(false);
        }

        // ── ② 落盘 ──
        if (now - _lastFlush >= TimeSpan.FromSeconds(Math.Max(5, options.FlushSeconds)))
        {
            _lastFlush = now;
            await _telemetry.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // ── ③ 保留清理 ──
        if (now - _lastPurge >= PurgeInterval)
        {
            _lastPurge = now;
            await PurgeAsync(now, cancellationToken).ConfigureAwait(false);
        }

        // ── ④ 日聚合：一天跑一次（跨天时）；失败后退避 10 分钟再试 ──
        if (_lastRollupDay != DateOnly.FromDateTime(now.DateTime) && now >= _nextRollupAttempt)
        {
            await RollupAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 推送"某台机器的遥测已刷新"。
    /// <para>
    /// <b>逐台推而不是打包成一条</b>：前端收到后只需重绘一行，不必把整张表重新拉一遍。
    /// 代价是消息条数多（300 台 × 每 2 秒一轮 ≈ 150 条/秒），但每条只有两百来字节，
    /// 在内网里是几百 KB/s 的量级；相比之下"整表重拉"要传几十 KB 且会让前端每一行都重绘。
    /// </para>
    /// </summary>
    private async Task NotifyAsync(CancellationToken cancellationToken)
    {
        var dirty = _telemetry.TakeDirtyAgents();

        foreach (var agentId in dirty)
        {
            if (cancellationToken.IsCancellationRequested) return;

            var summary = _registry.MetricsOf(agentId);

            try
            {
                await _notifier
                    .AgentTelemetryUpdatedAsync(new AgentLiveMetricsDto(agentId, summary, BeijingTime.Now), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 推送失败不是致命问题（前端还有轮询兜底），但必须留痕。
                _log.Warn("Telemetry", $"遥测推送失败（Id={agentId}）：{ex.Message}");
            }
        }
    }

    private async Task PurgeAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var options = _options.Value.Agent.Telemetry;

        try
        {
            var metricCutoff = now.AddDays(-options.DetailRetentionDays);
            var removed = await _store.PurgeMetricsAsync(metricCutoff, cancellationToken).ConfigureAwait(false);

            var dayCutoff = DateOnly.FromDateTime(now.DateTime).AddDays(-options.DailyRetentionDays);
            var removedDaily = await _store.PurgeDailyAsync(dayCutoff, cancellationToken).ConfigureAwait(false);

            if (removed > 0 || removedDaily > 0)
            {
                _log.Info("Telemetry", $"遥测保留清理：删除指标明细 {removed} 行（早于 {metricCutoff:yyyy-MM-dd}）、日聚合 {removedDaily} 行。");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Telemetry", "遥测保留清理失败（下一小时重试）", ex);
        }
    }

    /// <summary>
    /// 重算"昨天"的日聚合。
    /// <para>
    /// <b>只补昨天</b>（不做"补齐所有缺失日子"）：补算要按机器把一整天的明细读进内存，
    /// 300 台 × 17280 条是一次很重的库操作。而"服务停机期间没算的日聚合"在页面上
    /// 表现为那一天没有数据点 —— 远好过每次启动都把库读一遍。
    /// </para>
    /// </summary>
    private async Task RollupAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(BeijingTime.Now.DateTime);
        var yesterday = today.AddDays(-1);

        try
        {
            await _store
                .RebuildDailyAsync(yesterday, _options.Value.Agent.Telemetry.MetricsSeconds, cancellationToken)
                .ConfigureAwait(false);

            _lastRollupDay = today;

            _log.Info("Telemetry", $"日聚合已重算：{yesterday:yyyy-MM-dd}。");
        }
        catch (Exception ex)
        {
            // 不设 _lastRollupDay ⇒ 明天跨天时会再试；同时退避 10 分钟，避免每秒重试一次。
            _nextRollupAttempt = BeijingTime.Now.AddMinutes(10);

            _log.Error("Telemetry", $"日聚合重算失败（{yesterday:yyyy-MM-dd}），10 分钟后重试", ex);
        }
    }
}
