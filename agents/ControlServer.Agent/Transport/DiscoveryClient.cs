using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using ControlServer.Core.Services;

namespace ControlServer.Agent.Transport;

/// <summary>发现到的中心服务端。</summary>
/// <param name="Address">应答来源地址（即服务端的 IP —— 应答不带 IP，取包的来源更可靠）。</param>
/// <param name="Message">应答内容（端口、版本、实例标识）。</param>
public sealed record DiscoveredServer(string Address, AgentHereMessage Message);

/// <summary>
/// 客户端侧的自动发现（M4.1）：<b>广播问"中心服务在哪"，等服务端应答</b>。
/// <para>
/// <b>为什么是客户端广播而不是服务端扫描</b>：主动扫描在 Windows 防火墙丢入站、
/// 跨网段失效、254 台超时、多网卡不知扫哪个、被安全软件告警五处同时失手；
/// 而"客户端喊一声、服务端答应"只要在同一个广播域就一定成立，
/// 并且客户端<b>一个入站端口都不用开</b>（它只用自己临时开的那个端口收应答）。
/// </para>
/// <para>
/// <b>发往哪几个地址</b>：<c>255.255.255.255</c>（受限广播，最通用）
/// ＋ <b>每个网卡的子网广播地址</b>（如 <c>192.168.1.255</c>）。
/// 两者都要：有些网络环境会丢弃受限广播；有些环境里多网卡只走其中一块，
/// 而受限广播只从一个接口出去。
/// </para>
/// </summary>
public sealed class DiscoveryClient
{
    private readonly int _port;
    private readonly LogService _log;

    public DiscoveryClient(int port, LogService log)
    {
        _port = port;
        _log = log;
    }

    /// <summary>
    /// 广播一轮并等待应答。
    /// </summary>
    /// <param name="timeout">等待应答的总时长（一轮）。调用方负责重试。</param>
    /// <returns>第一个合法应答；超时或没有任何应答时返回 null。</returns>
    public async Task<DiscoveredServer?> DiscoverAsync(
        string machineKey,
        string agentVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        UdpClient client;

        try
        {
            client = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = true,
            };
        }
        catch (Exception ex)
        {
            _log.Warn("Agent", $"发现广播：无法创建 UDP 套接字（{ex.Message}）。");
            return null;
        }

        using (client)
        {
            var payload = Encoding.UTF8.GetBytes(
                AgentDiscoveryProtocol.BuildDiscover(machineKey, agentVersion));

            await SendToBroadcastTargetsAsync(client, payload).ConfigureAwait(false);

            var deadline = DateTimeOffset.UtcNow + timeout;

            while (!cancellationToken.IsCancellationRequested)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) break;

                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(remaining);

                    var received = await client.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                    var text = Encoding.UTF8.GetString(received.Buffer);

                    if (!AgentDiscoveryProtocol.TryParseHere(text, out var here, out _)) continue;

                    // 只认本产品的应答：同网段可能有别的程序也用 9998。
                    if (!string.Equals(here.Product, AgentProtocol.Product, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return new DiscoveredServer(received.RemoteEndPoint.Address.ToString(), here);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 单个坏包不该终止整轮等待（应答可能紧接着就来）。
                    _log.Warn("Agent", $"发现广播：接收应答出错（继续等待）—— {ex.Message}");
                }
            }

            return null;
        }
    }

    private async Task SendToBroadcastTargetsAsync(UdpClient client, byte[] payload)
    {
        foreach (var target in BroadcastTargets())
        {
            try
            {
                // 用**独立的**取消源：某一个地址发不出去（比如某块网卡不允许广播），
                // 不该让其余地址也不发。
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.SendAsync(payload.AsMemory(), new IPEndPoint(target, _port), cts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("Agent", $"发现广播：向 {target}:{_port} 发送失败 —— {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 计算广播目标地址集合：受限广播 + 每个已启用 IPv4 网卡的子网广播地址。
    /// </summary>
    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        yield return IPAddress.Broadcast;

        List<IPAddress> subnetBroadcasts = new();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (unicast.IPv4Mask is null) continue;

                        var broadcast = ComputeBroadcast(unicast.Address, unicast.IPv4Mask);
                        if (broadcast is not null) subnetBroadcasts.Add(broadcast);
                    }
                }
                catch
                {
                    // 单块网卡读失败不影响其它网卡。
                }
            }
        }
        catch
        {
            // 枚举网卡失败时只剩受限广播，这仍然能覆盖大多数单网段场景。
        }

        foreach (var address in subnetBroadcasts.Distinct()) yield return address;
    }

    /// <summary>由"IP + 掩码"算出子网广播地址。</summary>
    private static IPAddress? ComputeBroadcast(IPAddress address, IPAddress mask)
    {
        var ip = address.GetAddressBytes();
        var m = mask.GetAddressBytes();

        if (ip.Length != 4 || m.Length != 4) return null;

        var result = new byte[4];

        for (var i = 0; i < 4; i++)
        {
            result[i] = (byte)(ip[i] | (byte)~m[i]);
        }

        // 0.0.0.0 或 255.255.255.255 这类结果没有意义（例如未配置的网卡），丢掉。
        if (result[0] == 0 && result[1] == 0 && result[2] == 0 && result[3] == 0) return null;

        return new IPAddress(result);
    }
}
