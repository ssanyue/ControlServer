using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;

namespace ControlServer.Server.HostedServices;

/// <summary>
/// 桥接：内核 <see cref="LogService.LogAdded"/> → SignalR 推送。
/// 这是"服务端 → 浏览器"实时日志的唯一出口。
/// </summary>
public sealed class LogRelayHostedService : BackgroundService
{
    private readonly LogService _log;
    private readonly INotifier _notifier;

    public LogRelayHostedService(LogService log, INotifier notifier)
    {
        _log = log;
        _notifier = notifier;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogAdded += OnLogAdded;
        stoppingToken.Register(() => _log.LogAdded -= OnLogAdded);
        return Task.CompletedTask;
    }

    private void OnLogAdded(LogEntry entry)
    {
        var dto = new LogDto(entry.Timestamp, entry.LevelText, entry.Category, entry.Message);

        // 即发即忘：推送失败不能反过来影响业务线程。
        _ = _notifier.LogAddedAsync(dto);
    }
}
