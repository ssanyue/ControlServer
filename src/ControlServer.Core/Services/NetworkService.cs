using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ControlServer.Core.Services;

/// <summary>发送结果。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="BytesSent">实际发出的字节数（失败为 0）。</param>
/// <param name="Message">成功时为「协议 → 目标 (N B)」；失败时为原因。</param>
public sealed record SendResult(bool Ok, int BytesSent, string Message);

/// <summary>
/// 网络发送原语：UDP / TCP / Wake-on-LAN。
/// <para>
/// 职责边界：**只负责"把字节发出去"**。编码、延迟、指令库解析、对接码覆盖、
/// 日志记录这些<b>语义</b>一律由 <see cref="ActionExecutionService"/> 负责。
/// 这样网络层能被独立替换（未来走 SOCKS / 绑定指定网卡都不用动业务）。
/// </para>
/// <para>与旧版 <c>NetworkService</c> 的差异（有意的超集，非行为偏离）：</para>
/// <list type="bullet">
///   <item>UDP 恒开 <c>EnableBroadcast</c> —— 旧版只在 WoL 时开；服务版放开不影响正常单播。</item>
///   <item>目标地址先按 IP 解析，失败再走 DNS —— 旧版只认 IP。多支持了主机名。</item>
///   <item>TCP 连接超时 5 秒（与旧版同）。</item>
/// </list>
/// </summary>
public sealed class NetworkService
{
    /// <summary>TCP 连接 / 写入超时（与 <c>NetworkService.SendAsync</c> 一致）。</summary>
    public static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(5);

    public async Task<SendResult> SendUdpAsync(
        string host,
        int port,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new UdpClient { EnableBroadcast = true };
            var endpoint = new IPEndPoint(Resolve(host), port);
            var sent = await client.SendAsync(payload, endpoint, cancellationToken).ConfigureAwait(false);
            return new SendResult(true, sent, $"UDP -> {host}:{port} ({sent}B)");
        }
        catch (Exception ex)
        {
            return new SendResult(false, 0, $"UDP -> {host}:{port} 失败: {ex.Message}");
        }
    }

    public async Task<SendResult> SendTcpAsync(
        string host,
        int port,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TcpTimeout);

            await client.ConnectAsync(Resolve(host), port, cts.Token).ConfigureAwait(false);

            var stream = client.GetStream();
            await stream.WriteAsync(payload.AsMemory(0, payload.Length), cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(cts.Token).ConfigureAwait(false);

            return new SendResult(true, payload.Length, $"TCP -> {host}:{port} ({payload.Length}B)");
        }
        catch (Exception ex)
        {
            return new SendResult(false, 0, $"TCP -> {host}:{port} 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 发送网络唤醒魔术包。魔术包由 <see cref="DataEncoder.BuildMagicPacket"/> 构造，
    /// 本方法只负责承载（UDP 广播，需 <c>EnableBroadcast</c>）。
    /// </summary>
    public Task<SendResult> SendWakeOnLanAsync(
        string host,
        int port,
        byte[] magicPacket,
        CancellationToken cancellationToken = default)
        => SendUdpAsync(string.IsNullOrWhiteSpace(host) ? "255.255.255.255" : host, port, magicPacket, cancellationToken);

    /// <summary>
    /// 按显式编码把文本发成 UDP（M1 的测试端点在用，保留）。
    /// </summary>
    public Task<SendResult> SendTextUdpAsync(
        string host,
        int port,
        string payload,
        Encoding? encoding = null,
        CancellationToken cancellationToken = default)
        => SendUdpAsync(host, port, (encoding ?? Encoding.UTF8).GetBytes(payload), cancellationToken);

    /// <summary>
    /// 目标解析：先按 IP 直解（快路径、与旧版一致），否则走 DNS 支持主机名。
    /// </summary>
    private static IPAddress Resolve(string host)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;

        var addresses = Dns.GetHostAddresses(host);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault()
               ?? throw new InvalidOperationException($"无法解析主机名：{host}");
    }
}
