using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace ControlServer.Infrastructure.Notifications;

/// <summary>
/// 把内核事件桥接到 SignalR。
/// 泛型 <typeparamref name="THub"/> 让 Infrastructure 不必引用具体 Hub（解耦），
/// 由宿主在组合根指定 <c>SignalRNotifier&lt;EventsHub&gt;</c>。
/// </summary>
public sealed class SignalRNotifier<THub> : INotifier
    where THub : Hub
{
    private readonly IHubContext<THub> _hubContext;

    public SignalRNotifier(IHubContext<THub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task LogAddedAsync(LogDto item, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.SendAsync(EventNames.LogAdded, item, cancellationToken);

    public Task StatusChangedAsync(StatusSnapshot snapshot, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.SendAsync(EventNames.StatusChanged, snapshot, cancellationToken);

    public Task ActionResultAsync(ActionResultDto result, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.SendAsync(EventNames.ActionResult, result, cancellationToken);

    public Task TrafficLogAddedAsync(TrafficLogDto item, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.SendAsync(EventNames.TrafficLogAdded, item, cancellationToken);

    public Task ConfigChangedAsync(ConfigChangedDto change, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.SendAsync(EventNames.ConfigChanged, change, cancellationToken);

    public Task LicenseChangedAsync(LicenseDto license, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.SendAsync(EventNames.LicenseChanged, license, cancellationToken);

    public Task AgentChangedAsync(AgentHostDto host, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.SendAsync(EventNames.AgentChanged, host, cancellationToken);

    public Task AgentOnlineAsync(AgentHostDto host, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.SendAsync(EventNames.AgentOnline, host, cancellationToken);

    public Task AgentOfflineAsync(AgentHostDto host, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.SendAsync(EventNames.AgentOffline, host, cancellationToken);

    public Task AgentTelemetryUpdatedAsync(AgentLiveMetricsDto live, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.SendAsync(EventNames.AgentTelemetryUpdated, live, cancellationToken);

    public Task TransferProgressAsync(TransferProgressDto progress, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.SendAsync(EventNames.TransferProgress, progress, cancellationToken);

    public Task AgentConfigChangedAsync(int agentId, CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.SendAsync(EventNames.AgentConfigChanged, agentId, cancellationToken);
}
