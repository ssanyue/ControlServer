using ControlServer.Core.Services;

namespace ControlServer.Server.HostedServices;

/// <summary>
/// 时钟校准的后台驱动：启动后做一次首校，然后按固定间隔重校。
/// <para>
/// <b>刻意不阻塞启动</b>（M2 方案 §13 风险表："NTP 不可达导致启动慢 → 超时 2s + 异步校准，不阻塞启动"）。
/// 代价是启动最初的几百毫秒内 <see cref="BeijingTime.ClockOffset"/> 仍为 0，
/// 那段时间的日志时间戳等于系统钟 —— 首校完成后开始吃校正，并由
/// <see cref="TimeSyncService"/> 在偏移跳变超阈值时打警告。
/// </para>
/// </summary>
public sealed class TimeSyncHostedService : BackgroundService
{
    private readonly TimeSyncService _timeSync;
    private readonly TimeSyncHostedOptions _options;
    private readonly LogService _log;

    public TimeSyncHostedService(TimeSyncService timeSync, TimeSyncHostedOptions options, LogService log)
    {
        _timeSync = timeSync;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.AutoSync)
        {
            _log.Info("TimeSync",
                "自动时钟校准已关闭（Server:Time:AutoSync=false）—— BeijingTime 使用系统时钟，" +
                "可随时用 POST /api/system/timesync 手动校准一次。");
            return;
        }

        await RunOnceAsync("启动首校", stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_options.ResyncInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RunOnceAsync("周期重校", stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunOnceAsync(string reason, CancellationToken stoppingToken)
    {
        try
        {
            var outcome = await _timeSync.SyncAsync(stoppingToken).ConfigureAwait(false);

            if (!outcome.Ok)
            {
                // TimeSyncService 内部已经打过 Warn，这里只补一句"这是第几次失败"，避免重复刷屏。
                _log.Warn("TimeSync", $"{reason}失败（累计失败 {_timeSync.FailureCount} 次），继续沿用当前偏移。");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机。
        }
        catch (Exception ex)
        {
            // 后台服务异常绝不允许冒泡 —— 冒泡会让 BackgroundService 的默认行为
            // 在 .NET 6+ 里直接把宿主打挂（HostOptions.BackgroundServiceExceptionBehavior）。
            _log.Error("TimeSync", $"{reason}时发生未预期异常，已忽略（不影响服务运行）。", ex);
        }
    }
}

/// <summary>
/// 校准调度的宿主参数。与 <see cref="TimeSyncSettings"/> 分开：
/// 后者是"怎么查"，这里是"多久查一次、要不要查"。
/// </summary>
/// <param name="AutoSync">是否启用自动校准。</param>
/// <param name="ResyncInterval">周期重校间隔。</param>
public sealed record TimeSyncHostedOptions(bool AutoSync, TimeSpan ResyncInterval);
