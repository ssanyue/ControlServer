using System.Runtime.InteropServices;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using ControlServer.Server.Options;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.Services;

/// <summary>组装状态快照（REST 与 SignalR 共用同一份来源，避免两处不一致）。</summary>
public sealed class StatusProvider
{
    private static readonly string ProductVersion =
        typeof(StatusProvider).Assembly.GetName().Version?.ToString() ?? "0.1.0";

    private readonly IOptions<ServerOptions> _options;
    private readonly PairingListenerService _listener;
    private readonly LogService _log;
    private readonly ServerState _state;
    private readonly ServerAddressService _address;

    public StatusProvider(
        IOptions<ServerOptions> options,
        PairingListenerService listener,
        LogService log,
        ServerState state,
        ServerAddressService address)
    {
        _options = options;
        _listener = listener;
        _log = log;
        _state = state;
        _address = address;
    }

    public StatusSnapshot Create() => new(
        Product: "中控服务版",
        Version: ProductVersion,
        MachineName: Environment.MachineName,
        OsPlatform: RuntimeInformation.OSDescription,
        ServerTime: BeijingTime.Now,
        TimeZone: BeijingTime.DisplayName,
        // ⚠️ 端口一律读**实际监听值**，不读配置：M2-E5 之后端口可以在运行期热切换，
        //    读配置会出现"实际连 9000、界面说 8899"这种最难排查的不一致。
        HttpPort: _address.CurrentPort is var port and > 0 ? port : _options.Value.Http.Port,
        PairingPort: _options.Value.Listener.PairingPort,
        ListenerRunning: _listener.IsRunning,
        UptimeSeconds: (long)_state.Uptime.TotalSeconds,
        RecentLogCount: _log.Count);
}
