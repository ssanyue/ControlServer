using System.Net;
using System.Net.Sockets;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// <see cref="SntpClient"/> 的**协议级**测试。
/// <para>
/// 做法是在本机起一个真 UDP 假 NTP 服务器，让真实的 <see cref="SntpClient"/>
/// 走完整收发路径 —— 报文偏移写错（T2 写在 32、T3 写在 40）这类问题只有这样才能被抓到。
/// 纯 mock 掉 UdpClient 会让这个文件失去意义。
/// </para>
/// </summary>
public sealed class SntpClientTests
{
    private static readonly DateTimeOffset NtpEpoch = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>本机回环的往返延迟是亚毫秒级，50ms 容差足够吸收线程调度抖动。</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task Query_returns_the_offset_between_server_and_local_clock()
    {
        // 让"服务器"比本机快 1.234 秒。
        var declared = TimeSpan.FromSeconds(1.234);
        using var server = new FakeNtpServer(_ => BuildReply(DateTimeOffset.UtcNow.Add(declared)));

        var client = new SntpClient(server.Port);

        var result = await client.QueryAsync("127.0.0.1", TimeSpan.FromSeconds(3));

        result.Offset.Should().BeCloseTo(declared, Tolerance);
        result.Stratum.Should().Be(2);
        result.RoundTrip.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task Query_returns_a_negative_offset_when_the_local_clock_is_ahead()
    {
        var declared = TimeSpan.FromSeconds(-2.5);
        using var server = new FakeNtpServer(_ => BuildReply(DateTimeOffset.UtcNow.Add(declared)));

        var result = await new SntpClient(server.Port).QueryAsync("127.0.0.1", TimeSpan.FromSeconds(3));

        result.Offset.Should().BeCloseTo(declared, Tolerance);
    }

    [Fact]
    public async Task Query_sends_a_well_formed_client_request()
    {
        byte[]? captured = null;
        using var server = new FakeNtpServer(request =>
        {
            captured = request;
            return BuildReply(DateTimeOffset.UtcNow);
        });

        await new SntpClient(server.Port).QueryAsync("127.0.0.1", TimeSpan.FromSeconds(3));

        captured.Should().NotBeNull();
        captured!.Length.Should().Be(48, "SNTP 报文固定 48 字节");
        captured[0].Should().Be(0x1B, "客户端请求应为 LI=0 / VN=3 / Mode=3");

        // Transmit Timestamp 必须被填写（我们不用它，但标准客户端都要填，
        // 部分设备会因为它为 0 而拒绝服务）。
        var transmit = ReadTimestamp(captured, 40);
        transmit.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5),
            "请求里的 Transmit Timestamp 应是本次发送时刻");
    }

    [Fact]
    public async Task Query_rejects_kiss_of_death()
    {
        using var server = new FakeNtpServer(_ => BuildReply(DateTimeOffset.UtcNow, stratum: 0));

        var act = async () => await new SntpClient(server.Port).QueryAsync("127.0.0.1", TimeSpan.FromSeconds(3));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*kiss-of-death*", "Stratum=0 的报文含义不是时间，绝不能拿来校正时钟");
    }

    [Fact]
    public async Task Query_rejects_a_response_that_is_not_from_a_server()
    {
        using var server = new FakeNtpServer(_ => BuildReply(DateTimeOffset.UtcNow, mode: 3));

        var act = async () => await new SntpClient(server.Port).QueryAsync("127.0.0.1", TimeSpan.FromSeconds(3));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Mode=3*", "Mode 应为 4（服务端）");
    }

    [Fact]
    public async Task Query_rejects_an_unsynchronized_server()
    {
        using var server = new FakeNtpServer(_ => BuildReply(DateTimeOffset.UtcNow, leapIndicator: 3));

        var act = async () => await new SntpClient(server.Port).QueryAsync("127.0.0.1", TimeSpan.FromSeconds(3));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*未同步*", "LI=3 表示服务器自己都没对上，它的时间不可用");
    }

    [Fact]
    public async Task Query_rejects_a_short_response()
    {
        using var server = new FakeNtpServer(_ => new byte[20]);

        var act = async () => await new SntpClient(server.Port).QueryAsync("127.0.0.1", TimeSpan.FromSeconds(3));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*长度*");
    }

    [Fact]
    public async Task Query_times_out_when_nothing_responds()
    {
        // 响应器返回 null = 丢弃请求，模拟不可达。
        using var server = new FakeNtpServer(_ => null!);

        var act = async () => await new SntpClient(server.Port).QueryAsync("127.0.0.1", TimeSpan.FromMilliseconds(300));

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("*超时*");
    }

    [Fact]
    public async Task Query_honours_caller_cancellation()
    {
        using var server = new FakeNtpServer(_ => null!);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var act = async () => await new SntpClient(server.Port)
            .QueryAsync("127.0.0.1", TimeSpan.FromSeconds(30), cts.Token);

        // 调用方主动取消时必须保持 OperationCanceledException 语义，
        // 不能被翻译成"超时" —— 否则停机流程会被误判成网络故障。
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ═══════════════════════════════════════════════════════════
    // 报文构造 / 解析辅助
    // ═══════════════════════════════════════════════════════════

    private static byte[] BuildReply(
        DateTimeOffset serverTime,
        byte stratum = 2,
        int mode = 4,
        int leapIndicator = 0)
    {
        var packet = new byte[48];
        packet[0] = (byte)((leapIndicator << 6) | (4 << 3) | mode);
        packet[1] = stratum;

        // Receive 与 Transmit 用同一时刻：等价于"服务器零处理耗时"，
        // 此时 SNTP 公式给出的偏移恰好等于真实偏移（网络往返恰好被抵消）。
        WriteTimestamp(packet, 32, serverTime);
        WriteTimestamp(packet, 40, serverTime);

        return packet;
    }

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

    private static DateTimeOffset ReadTimestamp(byte[] buffer, int offset)
    {
        uint seconds = ((uint)buffer[offset] << 24)
                       | ((uint)buffer[offset + 1] << 16)
                       | ((uint)buffer[offset + 2] << 8)
                       | buffer[offset + 3];

        uint fraction = ((uint)buffer[offset + 4] << 24)
                        | ((uint)buffer[offset + 5] << 16)
                        | ((uint)buffer[offset + 6] << 8)
                        | buffer[offset + 7];

        return NtpEpoch.AddSeconds(seconds).AddMilliseconds(fraction * 1000.0 / 4294967296.0);
    }

    /// <summary>
    /// 本机回环上的假 NTP 服务器：绑随机端口，收到请求就用 <c>responder</c> 造回复。
    /// 响应器返回 <c>null</c> 表示丢弃（模拟不可达）。
    /// </summary>
    private sealed class FakeNtpServer : IDisposable
    {
        private readonly UdpClient _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly Func<byte[], byte[]> _responder;

        public FakeNtpServer(Func<byte[], byte[]> responder)
        {
            _responder = responder;
            _listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_listener.Client.LocalEndPoint!).Port;
            _loop = Task.Run(LoopAsync);
        }

        public int Port { get; }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                UdpReceiveResult request;
                try
                {
                    request = await _listener.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                try
                {
                    var reply = _responder(request.Buffer);
                    if (reply is not null)
                    {
                        await _listener.SendAsync(reply, request.RemoteEndPoint, _cts.Token).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // 替身不向上抛：单个请求出错不应让监听循环退出。
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Dispose();

            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // 忽略取消导致的异常。
            }

            _cts.Dispose();
        }
    }
}
