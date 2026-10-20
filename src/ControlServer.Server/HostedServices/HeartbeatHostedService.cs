using ControlServer.Core.Services;

namespace ControlServer.Server.HostedServices;

/// <summary>
/// 心跳：演示并固化 <see cref="BackgroundService"/> + <see cref="PeriodicTimer"/> 的标准写法
/// （可取消、可优雅退出）—— M2 的定时器服务会沿用同一模式。
/// </summary>
public sealed class HeartbeatHostedService : BackgroundService
{
    private readonly LogService _log;
    private readonly ServerState _state;

    public HeartbeatHostedService(LogService log, ServerState state)
    {
        _log = log;
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                _log.Debug("Heartbeat", $"服务运行中，已运行 {_state.Uptime:hh\\:mm\\:ss}");
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机路径。
        }
    }
}
