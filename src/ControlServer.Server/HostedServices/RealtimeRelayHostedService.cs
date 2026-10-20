using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;

namespace ControlServer.Server.HostedServices;

/// <summary>
/// 桥接：内核事件 → SignalR 推送（M2-F 新增的两条通道）。
/// <list type="bullet">
///   <item><see cref="TrafficLogService.TrafficLogAdded"/> → <see cref="EventNames.TrafficLogAdded"/>：收发报文实时上屏；</item>
///   <item><see cref="ControlSettingsService.Changed"/> → <see cref="EventNames.ConfigChanged"/>：配置变更通知（只推版本号，客户端自行重拉）。</item>
/// </list>
/// <para>
/// 与 <see cref="LogRelayHostedService"/>（运维日志）分开是有意的：两者是**不同的数据**、
/// 前端也挂在不同的面板上，混在一条通道里会让"日志"这个词继续含混下去（M2 方案 §10.2 的教训）。
/// </para>
/// </summary>
public sealed class RealtimeRelayHostedService : BackgroundService
{
    private readonly TrafficLogService _traffic;
    private readonly ControlSettingsService _settings;
    private readonly INotifier _notifier;

    public RealtimeRelayHostedService(
        TrafficLogService traffic,
        ControlSettingsService settings,
        INotifier notifier)
    {
        _traffic = traffic;
        _settings = settings;
        _notifier = notifier;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _traffic.TrafficLogAdded += OnTrafficLogAdded;
        _settings.Changed += OnConfigChanged;

        stoppingToken.Register(() =>
        {
            _traffic.TrafficLogAdded -= OnTrafficLogAdded;
            _settings.Changed -= OnConfigChanged;
        });

        return Task.CompletedTask;
    }

    private void OnTrafficLogAdded(TrafficLogEntry entry)
    {
        var dto = new TrafficLogDto(
            entry.Time,
            entry.Direction,
            entry.Protocol,
            entry.TargetIp,
            entry.Port,
            entry.Data,
            entry.Success,
            entry.Message,
            entry.ButtonName,
            entry.ActionName);

        // 即发即忘：推送失败不能反过来影响发送动作的成败判定。
        _ = _notifier.TrafficLogAddedAsync(dto);
    }

    private void OnConfigChanged(ConfigChangedEvent change)
    {
        var dto = new ConfigChangedDto(change.Key, change.Version, change.ChangedBy, change.At);
        _ = _notifier.ConfigChangedAsync(dto);
    }
}
