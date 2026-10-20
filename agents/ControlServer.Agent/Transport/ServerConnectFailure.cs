using System.Net;
using System.Net.Sockets;

namespace ControlServer.Agent.Transport;

/// <summary>长连接建立失败的性质 —— <b>决定接下来该做什么</b>，不只是给日志分类。</summary>
public enum ServerConnectFailureKind
{
    /// <summary>
    /// 中心<b>明确拒收</b>这把钥匙（HTTP 401 / 403）：要去问一句"我是不是被移除了 / 被拉黑了"，
    /// 必要时重新注册。这是唯一值得花时间做凭据探测的情形。
    /// </summary>
    Rejected,

    /// <summary>
    /// <b>根本连不上</b>（拒绝连接 / 主机不可达 / 超时）：这时候去问"中心还认不认我的钥匙"
    /// 是<b>没有意义的</b> —— 对面连话都说不上。多问一次只是白等一个超时。
    /// </summary>
    Unreachable,

    /// <summary>说不清（其他异常）：沿用老做法，花一次探测去问。</summary>
    Unknown,
}

/// <summary>
/// 把"连接失败的异常"归到 <see cref="ServerConnectFailureKind"/>（2026-10-20）。
///
/// <para>
/// 🔥 <b>为什么要有它</b>：中心没起来时，每一轮都先撞 20 秒 TCP 超时，
/// 然后<b>再花 20 秒</b>去做一次凭据探测（那个探测的 HttpClient 超时就是 20 秒），
/// 才记下"连接失败"这一行 —— 一轮四十多秒。现场日志（2026-10-20 09:16:07 / 09:16:49 / …）
/// 就是这个节奏：中心一起来，还要等一分多钟才看见客户端上线。
/// </para>
///
/// <para>
/// ⚠️ <b>只有"连不上"才允许跳过探测</b>。「连接被重置」「没有更多信息」这些仍然去问 ——
/// 重置可能正是中心在握手阶段把证书/凭据拒了，跳过就会把"被移除的机器永远加不回来"
/// 那条老 bug 放回去（见 <c>AgentRegistrar.IsCredentialRejectedAsync</c> 的注释）。
/// 宁可多花一次探测，也不要漏掉"该重新注册"。
/// </para>
/// </summary>
public static class ServerConnectFailure
{
    /// <summary>归类。传入 null / 无法识别时返回 <see cref="ServerConnectFailureKind.Unknown"/>。</summary>
    public static ServerConnectFailureKind Classify(Exception? exception)
    {
        if (exception is null) return ServerConnectFailureKind.Unknown;

        var chain = Unwrap(exception).ToList();

        // 先找"明确被拒"：它一旦出现就是结论，不该被同一棵树里的 SocketException 盖掉。
        foreach (var ex in chain)
        {
            if (ex is HttpRequestException
                {
                    StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                })
            {
                return ServerConnectFailureKind.Rejected;
            }
        }

        foreach (var ex in chain)
        {
            if (ex is SocketException socket && IsUnreachable(socket.SocketErrorCode))
            {
                return ServerConnectFailureKind.Unreachable;
            }

            // System.Net.Http 的超时会包成 TaskCanceledException + 内层 TimeoutException；
            // 那个 TaskCanceledException 在调用点已经被 OperationCanceledException 拦下了，
            // 这里接住的是 SignalR / 底层套接字直接抛上来的超时。
            if (ex is TimeoutException or TaskCanceledException)
            {
                return ServerConnectFailureKind.Unreachable;
            }
        }

        return ServerConnectFailureKind.Unknown;
    }

    /// <summary>
    /// 这个套接字错误是不是"对面根本不在"。
    /// <para>
    /// ⚠️ <b>刻意不含 <c>ConnectionReset</c></b>：它意味着<b>有人接了</b>然后掐掉，
    /// 那可能是中心在协议层拒绝本机 —— 正是需要探测的那一类。
    /// </para>
    /// </summary>
    private static bool IsUnreachable(SocketError error)
        => error is SocketError.ConnectionRefused
            or SocketError.HostUnreachable
            or SocketError.NetworkUnreachable
            or SocketError.HostDown
            or SocketError.NetworkDown
            or SocketError.TimedOut
            or SocketError.HostNotFound
            or SocketError.NoData;

    /// <summary>把 <see cref="AggregateException"/>（SignalR 有时会包）摊平。</summary>
    private static IEnumerable<Exception> Unwrap(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                foreach (var item in Unwrap(inner)) yield return item;
            }

            yield break;
        }

        yield return exception;

        if (exception.InnerException is { } nested)
        {
            foreach (var item in Unwrap(nested)) yield return item;
        }
    }
}
