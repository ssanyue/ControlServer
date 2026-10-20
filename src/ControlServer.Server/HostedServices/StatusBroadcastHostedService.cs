using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using ControlServer.Server.Services;

namespace ControlServer.Server.HostedServices;

/// <summary>
/// 周期性向所有浏览器广播状态快照（默认 5s）。
/// 异常必须被吞掉并记录 —— 广播失败绝不能拖垮宿主进程（稳定性硬要求）。
/// </summary>
public sealed class StatusBroadcastHostedService : BackgroundService
{
    private readonly StatusProvider _statusProvider;
    private readonly INotifier _notifier;
    private readonly LogService _log;

    public StatusBroadcastHostedService(StatusProvider statusProvider, INotifier notifier, LogService log)
    {
        _statusProvider = statusProvider;
        _notifier = notifier;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _notifier.StatusChangedAsync(_statusProvider.Create(), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Error("Status", "状态广播异常（已忽略，服务继续运行）", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机路径。
        }
    }
}
