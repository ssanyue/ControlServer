using System.Net;
using System.Net.Sockets;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using ControlServer.Server.Options;
using ControlServer.Server.Services;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.HostedServices;

/// <summary>
/// 客户端自动发现的**应答端**（M4.1，UDP 默认 9998）。
/// <para>
/// <b>它只做一件事</b>：收到 <c>CS-DISCOVER</c> 就回一条 <c>CS-HERE</c>，告诉对方
/// "中心服务在这个 IP、这个端口"。
/// </para>
/// <para>
/// <b>为什么不是"服务端扫描子网"</b>：主动扫描在 Windows 防火墙丢入站、跨网段失效、
/// 254 台地址超时、多网卡不知扫哪个、被安全软件告警五处同时失手。
/// 而"客户端广播 + 服务端应答"只要在同一个广播域就一定成立，
/// 并且<b>客户端一个入站端口都不用开</b>。
/// </para>
/// <para>
/// <b>安全边界（必须写死）</b>：应答里<b>只有定位信息，没有任何凭据</b>。
/// 广播域内任何主机都能看到它 —— 这不是漏洞，是设计：应答的作用是"告诉客户端往哪连"，
/// 真正需要保密的注册令牌只走 HTTPS 的注册请求。
/// 因此<b>发现广播里也绝不放令牌</b>（见 <c>AgentDiscoveryProtocol</c> 的注释）。
/// </para>
/// <para>
/// <b>失败只记录、不抛出</b>：端口被占用时 Web 界面仍要可用，
/// 只是"客户端发现不了服务端"，用户可以在安装时改填固定地址兜底。
/// </para>
/// </summary>
public sealed class DiscoveryListenerHostedService : BackgroundService
{
    private readonly IOptions<ServerOptions> _options;
    private readonly IMachineCodeProvider _machineCode;
    private readonly ServerRuntimeInfo _runtime;
    private readonly LogService _log;

    private UdpClient? _client;
    private string _serverId = "-";

    public DiscoveryListenerHostedService(
        IOptions<ServerOptions> options,
        IMachineCodeProvider machineCode,
        ServerRuntimeInfo runtime,
        LogService log)
    {
        _options = options;
        _machineCode = machineCode;
        _runtime = runtime;
        _log = log;
    }

    /// <summary>当前是否在监听（供 <c>/readyz</c> 与诊断页展示）。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>实际监听的端口。</summary>
    public int Port { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var discovery = _options.Value.Discovery;

        if (!discovery.Enabled)
        {
            _log.Info("Discovery", "客户端自动发现已禁用（Server:Discovery:Enabled=false）—— 客户端需在安装时指定服务端地址。");
            return;
        }

        Port = discovery.Port;
        _serverId = ReadServerId();

        try
        {
            _client = new UdpClient(new IPEndPoint(IPAddress.Any, Port));

            // 虽然本服务只回包不发广播，打开这个开关能让"同一台机器上跑了多个实例做验证"
            // 这种场景下的回包源地址正确（否则可能从别的网卡出去，客户端拿到一个连不通的地址）。
            _client.EnableBroadcast = true;
            IsRunning = true;
        }
        catch (Exception ex)
        {
            _log.Error(
                "Discovery",
                $"客户端自动发现监听启动失败（UDP {Port}）—— 端口可能被占用。客户端需在安装时指定服务端地址。",
                ex);

            return;
        }

        _log.Info(
            "Discovery",
            $"客户端自动发现已启动（UDP {Port}）：收到 CS-DISCOVER 即回 CS-HERE（HTTP {_runtime.HttpPort}，实例 {_serverId}）。");

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult received;

            try
            {
                received = await _client.ReceiveAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 收包环绝不能因单次失败而退出 —— 退出后表现为"发现功能悄悄废了"，
                // 且没有任何界面提示。铁律 #6。
                if (IsBenignUdpReset(ex))
                {
                    // ⚠️ 这是 Windows 上 UDP 的"伪错误"，不是故障：
                    // 服务端把 CS-HERE 回给了**客户端那一轮的临时端口**，而客户端每轮发现都新开一个
                    // socket（退出时自然关闭）⇒ 对端主机回的 ICMP「端口不可达」会被记在这个
                    // UDP socket 上，并在**下一次** ReceiveAsync 时以 SocketException(10054) 抛出来。
                    //
                    // 为什么必须降级而不是照原样记 Error：
                    // 它**每台机器每次重启/每轮发现都会来一条**。挂在 Error 级上，
                    // 百来台机器就足以把真正要看的日志（如 Enrollment 类别的
                    // "拒绝客户端注册"）淹掉 —— 那正是本次修复要解决的问题本身。
                    // 而且它是自愈的：监听循环继续，下一包照收。
                    _log.Debug("Discovery", $"接收发现广播时收到 UDP 复位（{ex.Message}）—— 对端临时端口已关闭，无害。");
                    continue;
                }

                _log.Error("Discovery", "接收发现广播失败（监听继续）", ex);
                continue;
            }

            // 逐条处理：解析是纯计算、回包是一次 send，都是微秒级，
            // 串行处理反而避免了"广播风暴时开出几百个并发任务"。
            await HandleDatagramAsync(received, stoppingToken).ConfigureAwait(false);
        }

        StopClient();
        _log.Info("Discovery", "客户端自动发现已停止。");
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        StopClient();
        return base.StopAsync(cancellationToken);
    }

    private async Task HandleDatagramAsync(UdpReceiveResult received, CancellationToken cancellationToken)
    {
        try
        {
            // 先按字节数挡一道：UDP 端口对整个局域网开放，任何主机都能往这里灌数据。
            if (received.Buffer.Length > AgentProtocol.MaxDatagramBytes) return;

            var text = System.Text.Encoding.UTF8.GetString(received.Buffer);

            if (!AgentDiscoveryProtocol.TryParseDiscover(text, out var discover, out var error))
            {
                // 只记 Debug 级噪音不写日志：同网段可能有别的东西在往这个端口发数据，
                // 每包记一条会把日志淹掉。
                _ = error;
                _ = discover;
                return;
            }

            var reply = AgentDiscoveryProtocol.BuildHere(new AgentHereMessage(
                AgentProtocol.Product,
                _runtime.Version,
                _runtime.HttpPort,
                _runtime.PairingPort,
                _runtime.DiscoveryPort,
                _serverId));

            var payload = System.Text.Encoding.UTF8.GetBytes(reply);

            if (_client is null) return;

            await _client
                .SendAsync(payload, payload.Length, received.RemoteEndPoint)
                .ConfigureAwait(false);

            _log.Info(
                "Discovery",
                $"已应答客户端发现请求：{discover.MachineKey}（Agent {discover.AgentVersion}，来自 {received.RemoteEndPoint}）");
        }
        catch (Exception ex)
        {
            // 单包处理失败绝不影响监听（铁律 #6）。
            _log.Error("Discovery", "处理发现广播失败（监听继续）", ex);
        }
        finally
        {
            _ = cancellationToken;
        }
    }

    /// <summary>
    /// 这个异常是不是 UDP 上"对端已经不在了"的**无害**信号。
    /// <para>
    /// 只认 <c>ConnectionReset</c>（WSAECONNRESET / 10054）：Windows 会把 ICMP「端口不可达」
    /// 记在 UDP socket 上，下一次接收时报出来。这是它在本服务里出现过的**唯一**形态。
    /// </para>
    /// <para>
    /// 刻意**不**把 <c>NetworkReset</c>（10052）算进来 —— 它也可能意味着网卡/链路真的断了，
    /// 那种情况应当留下 Error。宁可多留一条噪音，也不要把真实故障藏起来。
    /// </para>
    /// </summary>
    private static bool IsBenignUdpReset(Exception exception)
        => exception is SocketException { SocketErrorCode: SocketError.ConnectionReset };

    private void StopClient()
    {
        IsRunning = false;

        try
        {
            _client?.Dispose();
        }
        catch
        {
            // 停机路径不再抛。
        }
        finally
        {
            _client = null;
        }
    }

    /// <summary>
    /// 服务端实例标识（机器码派生）。取不到时退化成 <c>-</c> ——
    /// 它只是给"多台中心服务同网段"时区分用的，取不到不该阻断发现。
    /// </summary>
    private string ReadServerId()
    {
        try
        {
            var code = _machineCode.GetMachineCode();
            return string.IsNullOrWhiteSpace(code) ? "-" : code;
        }
        catch (Exception ex)
        {
            _log.Warn("Discovery", $"读取服务端实例标识失败（不影响发现应答）：{ex.Message}");
            return "-";
        }
    }
}
