using System.Net;
using System.Net.Sockets;
using ControlServer.Agent.Transport;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// 连接失败归类（<see cref="ServerConnectFailure"/>）。
///
/// <para>
/// 🔥 <b>它决定"要不要花 20 秒去问中心还认不认我的钥匙"</b>，不是日志分类玩具。
/// 现场日志（2026-10-20 09:16:07 / 09:16:49 / 09:17:34 …）的节奏是
/// "20 秒 TCP 超时 + 20 秒凭据探测 ≈ 每轮四十多秒" —— 中心一起来，
/// 客户端还要一分多钟才上线。跳过那一次注定超时的探测，这一轮就只剩 TCP 那一段。
/// </para>
///
/// <para>
/// ⚠️ 另一头必须守住：<b>被中心拒绝的机器不能不重新注册</b>。
/// 所以"拒绝连接""主机不可达""超时"之外的一律走老路（真去问一句），
/// 尤其 <c>ConnectionReset</c> —— 那是"有人接了又掐掉"，很可能正是认证层在拒。
/// </para>
/// </summary>
public class ServerConnectFailureTests
{
    [Fact]
    public void Refused_connection_is_unreachable()
    {
        var ex = new HttpRequestException(
            "连接被拒绝",
            new SocketException((int)SocketError.ConnectionRefused));

        ServerConnectFailure.Classify(ex).Should().Be(ServerConnectFailureKind.Unreachable);
    }

    [Theory]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.TimedOut)]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.NetworkDown)]
    public void Host_level_failures_are_unreachable(SocketError error)
    {
        var ex = new HttpRequestException("连不上", new SocketException((int)error));

        ServerConnectFailure.Classify(ex).Should().Be(ServerConnectFailureKind.Unreachable);
    }

    [Fact]
    public void Timeout_is_unreachable()
    {
        ServerConnectFailure.Classify(new TimeoutException()).Should().Be(ServerConnectFailureKind.Unreachable);
    }

    [Fact]
    public void Unauthorized_is_rejected()
    {
        var ex = new HttpRequestException("401", inner: null, HttpStatusCode.Unauthorized);

        ServerConnectFailure.Classify(ex).Should().Be(ServerConnectFailureKind.Rejected,
            "手上已经有结论了，连探测都不用做，直接去重新注册");
    }

    [Fact]
    public void Forbidden_is_rejected()
    {
        var ex = new HttpRequestException("403", inner: null, HttpStatusCode.Forbidden);

        ServerConnectFailure.Classify(ex).Should().Be(ServerConnectFailureKind.Rejected);
    }

    [Fact]
    public void Reset_is_unknown_because_it_may_be_the_auth_layer()
    {
        var ex = new HttpRequestException(
            "重置",
            new SocketException((int)SocketError.ConnectionReset));

        ServerConnectFailure.Classify(ex).Should().Be(ServerConnectFailureKind.Unknown,
            "宁可多花一次探测，也不要把'被移除的机器永远加不回来'那条老 bug 放回去");
    }

    [Fact]
    public void Aggregate_is_unwrapped()
    {
        var ex = new AggregateException(
            new TaskCanceledException("超时"),
            new HttpRequestException("无关", new SocketException((int)SocketError.ConnectionRefused)));

        ServerConnectFailure.Classify(ex).Should().Be(ServerConnectFailureKind.Unreachable);
    }

    [Fact]
    public void Rejection_wins_over_a_nested_socket_error()
    {
        // 同一个异常树里既有 401 又有"重置"时，按"被拒"处置 —— 走错那边的代价是"永远加不回来"。
        var ex = new HttpRequestException(
            "401",
            new SocketException((int)SocketError.ConnectionReset),
            HttpStatusCode.Unauthorized);

        ServerConnectFailure.Classify(ex).Should().Be(ServerConnectFailureKind.Rejected);
    }

    [Fact]
    public void Unknown_exception_stays_unknown()
    {
        ServerConnectFailure.Classify(new InvalidOperationException("没头没脑")).Should()
            .Be(ServerConnectFailureKind.Unknown);

        ServerConnectFailure.Classify(null).Should().Be(ServerConnectFailureKind.Unknown);
    }
}
