using System.Net;
using System.Net.Sockets;
using ControlServer.Core.Abstractions;

namespace ControlServer.Core.Services;

/// <summary>
/// SNTP 客户端（RFC 4330 的精简实现，只做"取当前时间"这一件事）。
/// <para>
/// <b>为什么自己实现而不引第三方包</b>：只需要 48 字节请求 + 4 个时间戳的解析，
/// 引一个包反而多一层依赖与版本风险；而且这里要能被测试替身完全替换（见 <see cref="ISntpClient"/>）。
/// </para>
/// <para>
/// <b>精度</b>：按 SNTP 的四时间戳公式消除网络往返的一半：
/// <c>offset = ((T2 − T1) + (T3 − T4)) / 2</c>，
/// 其中 T1/T4 是本地发送/接收时刻，T2/T3 是服务器接收/发送时刻。
/// 内网 NTP 源通常能到 ±几毫秒，足够定时器判定（秒级）。
/// </para>
/// <para>
/// 无平台依赖（纯 UDP），因此放在 Core 层。
/// </para>
/// </summary>
public sealed class SntpClient : ISntpClient
{
    /// <summary>NTP 标准端口。</summary>
    public const int DefaultNtpPort = 123;

    /// <summary>SNTP 报文固定 48 字节（不含认证字段）。</summary>
    private const int PacketSize = 48;

    /// <summary>客户端请求：LI=0, VN=3, Mode=3。</summary>
    private const byte ClientRequestByte = 0x1B;

    /// <summary>NTP 纪元：1900-01-01T00:00:00Z。NTP 时间戳的秒数从这里起算。</summary>
    private static readonly DateTimeOffset NtpEpoch = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly int _port;

    /// <summary>
    /// </summary>
    /// <param name="port">
    /// 目标端口，默认 <see cref="DefaultNtpPort"/>。
    /// 可注入有两个用处：① 测试时对着本机随机端口起的假服务器跑；
    /// ② 某些内网里 NTP 被映射到非标准端口。
    /// </param>
    public SntpClient(int port = DefaultNtpPort)
    {
        _port = port;
    }

    public async Task<SntpQueryResult> QueryAsync(
        string host,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        // 显式异步解析 DNS：UdpClient.Connect(hostname, …) 内部是同步解析，
        // 在无人值守服务里一个慢 DNS 就能占住一个线程池线程。
        var address = await ResolveAsync(host, linked.Token).ConfigureAwait(false);

        using var udp = new UdpClient();
        udp.Connect(address, _port);

        var request = new byte[PacketSize];
        request[0] = ClientRequestByte;

        var t1 = DateTimeOffset.UtcNow;

        // 按 SNTP 规范把发送时刻填进 Transmit Timestamp：
        // 我们用不到它（T1 直接用本地变量），但标准客户端都填，
        // 部分服务器/中间设备会因为它为 0 而拒绝或返回异常报文。
        WriteTimestamp(request, 40, t1);

        try
        {
            await udp.SendAsync(request, linked.Token).ConfigureAwait(false);
            var received = await udp.ReceiveAsync(linked.Token).ConfigureAwait(false);
            var t4 = DateTimeOffset.UtcNow;

            return Parse(received.Buffer, t1, t4, host);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 只把"我们自己的超时"翻译成 TimeoutException；
            // 外层主动取消时保持 OperationCanceledException 语义。
            throw new TimeoutException(
                $"SNTP 查询超时（{host}:{_port}，{timeout.TotalMilliseconds:F0}ms）");
        }
    }

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        // 已经是字面 IP 时无需 DNS。
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"无法解析 NTP 服务器「{host}」：{ex.Message}", ex);
        }

        // 优先 IPv4：部分内网环境有 IPv6 地址却没有 IPv6 出口，会白等一个超时。
        return Array.Find(addresses, static a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault()
               ?? throw new InvalidOperationException($"NTP 服务器「{host}」没有任何可用地址");
    }

    /// <summary>
    /// 解析 48 字节响应。任何"看起来不对"的响应都直接拒绝 ——
    /// 时钟被错误地校正，比不校正危险得多。
    /// </summary>
    private static SntpQueryResult Parse(byte[] buffer, DateTimeOffset t1, DateTimeOffset t4, string host)
    {
        if (buffer.Length < PacketSize)
        {
            throw new InvalidOperationException(
                $"SNTP 响应长度 {buffer.Length} 不足 {PacketSize} 字节（{host}），已丢弃");
        }

        var leapIndicator = (buffer[0] >> 6) & 0x03;
        var mode = buffer[0] & 0x07;
        var stratum = buffer[1];

        if (stratum == 0)
        {
            // kiss-of-death：服务器明确拒绝服务（限速 / 拒绝访问），报文含义不是时间。
            throw new InvalidOperationException($"SNTP 服务器 {host} 返回 kiss-of-death（Stratum=0），拒绝采用");
        }

        if (mode != 4)
        {
            throw new InvalidOperationException($"SNTP 响应 Mode={mode}，应为 4（服务端），已丢弃");
        }

        if (leapIndicator == 3)
        {
            throw new InvalidOperationException($"SNTP 服务器 {host} 自述时钟未同步（LI=3），拒绝采用");
        }

        var t2 = ReadTimestamp(buffer, 32);
        var t3 = ReadTimestamp(buffer, 40);

        if (t3 == NtpEpoch)
        {
            throw new InvalidOperationException($"SNTP 响应里 Transmit Timestamp 为 0（{host}），视为无效");
        }

        // SNTP 四时间戳公式。除法用的是 TimeSpan / double，保留亚毫秒精度。
        var offset = ((t2 - t1) + (t3 - t4)) / 2;
        var roundTrip = t4 - t1;

        return new SntpQueryResult(offset, roundTrip, stratum, t3);
    }

    /// <summary>
    /// 读 NTP 短格式时间戳（高 32 位秒 + 低 32 位小数秒），大端。
    /// </summary>
    private static DateTimeOffset ReadTimestamp(byte[] buffer, int offset)
    {
        var seconds = ReadUInt32(buffer, offset);
        var fraction = ReadUInt32(buffer, offset + 4);

        var milliseconds = fraction * 1000.0 / 4294967296.0;

        // NTP 秒计数是 32 位，2036 年回绕一次。当前使用场景（2026）不涉及，
        // 真到那天需要按"结果早于参考时间就 +2^32 秒"处理。
        return NtpEpoch.AddSeconds(seconds).AddMilliseconds(milliseconds);
    }

    private static uint ReadUInt32(byte[] buffer, int offset)
        => ((uint)buffer[offset] << 24)
           | ((uint)buffer[offset + 1] << 16)
           | ((uint)buffer[offset + 2] << 8)
           | buffer[offset + 3];

    /// <summary>写 NTP 短格式时间戳（大端）。与 <see cref="ReadTimestamp"/> 互为逆运算。</summary>
    private static void WriteTimestamp(byte[] buffer, int offset, DateTimeOffset value)
    {
        var elapsed = value.ToUniversalTime() - NtpEpoch;
        var seconds = (uint)elapsed.TotalSeconds;
        var fraction = (uint)((elapsed.TotalSeconds - seconds) * 4294967296.0);

        buffer[offset] = (byte)(seconds >> 24);
        buffer[offset + 1] = (byte)(seconds >> 16);
        buffer[offset + 2] = (byte)(seconds >> 8);
        buffer[offset + 3] = (byte)seconds;

        buffer[offset + 4] = (byte)(fraction >> 24);
        buffer[offset + 5] = (byte)(fraction >> 16);
        buffer[offset + 6] = (byte)(fraction >> 8);
        buffer[offset + 7] = (byte)fraction;
    }
}
