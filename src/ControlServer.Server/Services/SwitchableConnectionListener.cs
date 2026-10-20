using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace ControlServer.Server.Services;

/// <summary>
/// 可在运行期换端口的 <see cref="IConnectionListener"/>（M2-E5 的技术核心）。
///
/// <para>
/// <b>为什么需要它 —— 方案 §4.4 里"<c>StopAsync</c> 之后再 <c>StartAsync</c> 复用 IServer"是行不通的</b>：
/// <c>KestrelServerImpl.StartAsync</c> 有一个 <c>_hasStarted</c> 标志，停止时<b>不会</b>复位，
/// 二次启动直接抛 <c>InvalidOperationException("The server has already started.")</c>。
/// 而通过公开 API 又造不出第二个 Kestrel 实例（<c>StartAsync</c> 要的
/// <c>IHttpApplication&lt;HostingApplication.Context&gt;</c> 是 internal 类型，拿不到）。
/// </para>
/// <para>
/// <b>换一条路：Kestrel 不重启，只把底下的监听 socket 换掉。</b>
/// Kestrel 只通过 <c>IConnectionListener.AcceptAsync</c> 要连接，它对"这个连接从哪个端口来"并不关心。
/// 于是：<c>AcceptAsync</c> 从"当前"内层 listener 转取，换端口时先在<b>新</b>端口绑好，
/// 成功了才关旧端口并替换引用 —— 正在等待中的 <c>AcceptAsync</c> 会因旧 listener 关闭而抛异常，
/// 循环重试后自然接到新 listener 上。
/// </para>
/// <para>
/// <b>由此带来的三个好处</b>（对比"重启 Kestrel"）：
/// ① 切换可以在<b>处理请求的过程中同步完成</b> —— 不用"先返回已受理、再延迟切换"，
///    前端当场就能拿到真实结果（成功 / 端口被占用）；
/// ② <b>天然没有"旧端口已停、新端口没起"的窗口</b>：新端口绑失败时旧 listener 一动没动，
///    连回滚都不需要（回滚逻辑本身才是最容易出错的地方）；
/// ③ 已建立的连接（包括正在发这个响应的那条）不受影响，响应一定能送达浏览器。
/// </para>
/// <para>
/// <b>已知局限（不掩饰）</b>：旧端口上<b>已经建立</b>的 keep-alive 连接不会被主动断开 ——
/// 它们持有的是已 accept 的 socket，与监听 socket 无关。因此旧标签页在
/// Kestrel 默认 130 秒的 keep-alive 超时内仍能发请求成功。
/// 处置：前端收到切换结果后<b>立刻</b> <c>location.replace</c> 到新原点（G7），
/// 这一点使实际影响收敛为一个"待保存的新地址"提示，而不是"服务不可用"。
/// </para>
/// </summary>
public sealed class SwitchableConnectionListener : IConnectionListener
{
    /// <summary>
    /// 旧监听器的延迟释放时间。取值理由：既要让"切换响应 + 前端跳转"跑完，
    /// 也要覆盖浏览器最后一次 keep-alive 复用（Kestrel 默认 keep-alive 超时 130 秒，
    /// 这里取 2 分钟与之同量级）。再长没有意义，只是拖着不还资源。
    /// </summary>
    private static readonly TimeSpan DeferredDisposeDelay = TimeSpan.FromMinutes(2);

    private readonly IConnectionListenerFactory _factory;
    private readonly IPAddress _address;
    private readonly string _displayHost;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile IConnectionListener? _inner;
    private volatile int _port;

    /// <summary>
    /// 当前这一"代"接受操作的取消源（读用 <see cref="Volatile.Read{T}"/>、换用 <see cref="Interlocked.Exchange{T}"/>）。
    /// 换 listener / 停止监听时整代替换并取消，用以<b>确定性地</b>打断正在等待的
    /// <see cref="AcceptAsync"/> —— 见那里的说明。
    /// </summary>
    private CancellationTokenSource _acceptGeneration = new();

    internal SwitchableConnectionListener(
        IConnectionListenerFactory factory,
        IPAddress address,
        string displayHost,
        int port,
        ILogger logger)
    {
        _factory = factory;
        _address = address;
        _displayHost = displayHost;
        _port = port;
        _logger = logger;
    }

    /// <summary>用于拼给人看的 URL 的 host 段（<c>0.0.0.0</c> / <c>[::]</c> / 具体 IP）。</summary>
    public string DisplayHost => _displayHost;

    /// <summary>当前实际监听的端口。</summary>
    public int Port => _port;

    public EndPoint EndPoint => _inner?.EndPoint ?? new IPEndPoint(_address, _port);

    /// <summary>首次绑定（由工厂在 <c>BindAsync</c> 里调用，Kestrel 期望返回时已在监听）。</summary>
    internal async ValueTask BindInitialAsync(CancellationToken cancellationToken)
    {
        var endpoint = new IPEndPoint(_address, _port);
        _inner = await _factory.BindAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 取连接。
    ///
    /// <para>
    /// <b>这个循环里有三个必须同时成立的细节，缺一个就会"端口切了但没人接"：</b>
    /// </para>
    /// <para>
    /// ① <b>换端口时必须主动打断正在等待的 Accept</b>（<see cref="_acceptGeneration"/> 那一代 CTS）。
    /// 旧 listener 被 <c>UnbindAsync</c> 之后，内层 <c>AcceptAsync</c> 是抛异常、返回 <c>null</c>、
    /// 还是干脆继续挂着，属于实现细节；靠"等它自己结束"就把正确性押在别人的实现上了。
    /// 主动取消则是确定性的：一换代，当前这次等待立刻结束、循环重来、接到新 listener 上。
    /// </para>
    /// <para>
    /// ② <b>内层返回 <c>null</c> 时不能直接透传</b>。Kestrel 用 <c>null</c> 表示"这个监听结束了"，
    /// 收到就<b>永久退出接受循环</b>。实测症状极具迷惑性：新旧端口都能看到
    /// <c>LISTENING</c>、TCP 也能 <c>ESTABLISHED</c>（内核完成了三次握手，连接躺在 backlog 里），
    /// 但<b>没有任何请求得到响应</b>。所以必须先判断"是换了 listener 还是真的停止"。
    /// </para>
    /// <para>
    /// ③ 连续异常要退避，避免拿到一个"秒抛"的 listener 时把 CPU 空转烧满。
    /// </para>
    /// </summary>
    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var consecutiveFailures = 0;

        while (true)
        {
            var inner = _inner;

            // _inner 为 null = 已被 Unbind/Dispose，此时才如实告诉 Kestrel"监听结束了"。
            if (inner is null)
            {
                return null;
            }

            var generation = Volatile.Read(ref _acceptGeneration);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, generation.Token);

            try
            {
                var connection = await inner.AcceptAsync(linked.Token).ConfigureAwait(false);

                if (connection is null)
                {
                    // 内层说"我停了"。两种含义要分开：
                    //   · 已被 Retarget 换掉 → 继续循环，去接新 listener；
                    //   · 整个监听真的停了 → 才能把 null 交给 Kestrel。
                    if (!ReferenceEquals(_inner, inner))
                    {
                        continue;
                    }

                    return null;
                }

                consecutiveFailures = 0;
                return connection;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 换端口触发的"代取消"，不是停机 —— 接着用新 listener 接。
                if (!ReferenceEquals(_inner, inner))
                {
                    continue;
                }

                throw;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsTransient(ex))
            {
                // 连续失败说明拿到的是一个"立刻抛"的 listener（不该发生），
                // 退避一下避免把 CPU 烧在忙循环上。
                if (++consecutiveFailures > 8)
                {
                    _logger.LogWarning(ex, "监听接受循环连续失败 {Count} 次，退避 100ms 后重试。", consecutiveFailures);
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Yield();
                }
            }
        }
    }

    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var inner = _inner;
            _inner = null;

            // 先把 _inner 置空再换代：这样正在等待的 AcceptAsync 醒来后
            // 会看到"已经不该再接了"，如实返回 null 给 Kestrel。
            RenewAcceptGeneration();

            if (inner is not null)
            {
                await SafeUnbindAsync(inner, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var inner = _inner;
            _inner = null;
            RenewAcceptGeneration();

            if (inner is not null)
            {
                await SafeUnbindAsync(inner, CancellationToken.None).ConfigureAwait(false);
                await inner.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 把监听从一个端口挪到另一个端口。
    /// <para>
    /// <b>顺序不可颠倒</b>：先在<b>新</b>端口绑定，成功了才关旧端口。
    /// 反过来的话，新端口一旦被占用就留下"两个端口都没在听"的空档 —— 那正是方案里要求避免的状态。
    /// </para>
    /// </summary>
    internal async ValueTask<PortSwitchOutcome> RetargetAsync(int newPort, CancellationToken cancellationToken)
    {
        if (newPort is < 1 or > 65535)
        {
            return PortSwitchOutcome.Failed($"端口 {newPort} 越界，必须在 1-65535 之间。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_inner is null)
            {
                return PortSwitchOutcome.Failed("监听已停止（服务正在停机），无法切换端口。");
            }

            if (newPort == _port)
            {
                return PortSwitchOutcome.Unchanged();
            }

            IConnectionListener fresh;
            try
            {
                fresh = await _factory.BindAsync(new IPEndPoint(_address, newPort), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 旧 listener 一动没动 —— 服务仍在原端口正常服务。这就是"失败无副作用"。
                _logger.LogWarning(ex, "新端口 {Port} 绑定失败，监听保持在 {OldPort}。", newPort, _port);
                return PortSwitchOutcome.Failed(DescribeBindFailure(ex, newPort), ex);
            }

            var old = _inner;
            _inner = fresh;
            _port = newPort;

            // 先换引用、再换代：正在等待的 AcceptAsync 醒来时
            // 会看到 _inner 已经不是它等待的那一个，于是继续循环、接到新 listener 上。
            RenewAcceptGeneration();

            if (old is not null)
            {
                // ⚠️ 只"停止接受新连接"，**绝不立刻 Dispose**。
                //
                // Kestrel 的 SocketConnectionListener 在 Dispose 时会中止它已经接受、
                // 但仍在服务的连接 —— 而"正在把这次切换结果写回浏览器的那个连接"
                // 恰恰就是它接受的。实测症状：切换本身完全成功（新端口确实在 LISTEN，
                // 旧端口也停了），但客户端收到的却是 HTTP 000（响应写到一半 Pipe 被释放，
                // 服务端日志里是 Pipe.AllocateWriteHeadSynchronized 的异常栈）。
                //
                // 对一个"点了改端口却拿不到结果"的功能来说，这跟失败没有区别 ——
                // 用户不知道到底成没成、新地址是多少。所以旧监听器改为延迟释放：
                // 已建立的连接继续自然结束，前端也早就在这段时间里跳到新原点了。
                await SafeUnbindAsync(old, CancellationToken.None).ConfigureAwait(false);
                _ = DisposeLaterAsync(old);
            }

            return PortSwitchOutcome.Success(newPort);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 换代：取消上一代（打断正在等待的 <see cref="AcceptAsync"/>），换上新的。
    /// <para>
    /// 刻意<b>不 Dispose</b> 上一代：此刻仍有代码挂在它的 token 上，
    /// Dispose 会让那些注册在清理时抛 <c>ObjectDisposedException</c>。
    /// 一次切换泄漏一个几十字节的 CTS 对象，而换端口是人工低频操作 —— 这个交换划算。
    /// </para>
    /// </summary>
    private void RenewAcceptGeneration()
    {
        var previous = Interlocked.Exchange(ref _acceptGeneration, new CancellationTokenSource());

        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经被别处释放过，忽略。
        }
        catch (AggregateException)
        {
            // 取消回调里抛异常不影响"已经打断"这个事实。
        }
    }

    private static async ValueTask SafeUnbindAsync(IConnectionListener listener, CancellationToken cancellationToken)
    {
        try
        {
            await listener.UnbindAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 关闭路径上的异常没有处置手段，且此时已不再提供服务 —— 吞掉比让宿主崩掉好。
        }
    }

    /// <summary>
    /// 延迟释放旧监听器：等已建立的连接自然结束（或前端跳走）之后再 Dispose。
    /// <para>
    /// 之所以要延迟，见 <see cref="RetargetAsync"/> 里的说明 —— 立即 Dispose 会连带
    /// 中止正在回写切换结果的那个连接。
    /// </para>
    /// </summary>
    private async Task DisposeLaterAsync(IConnectionListener listener)
    {
        try
        {
            await Task.Delay(DeferredDisposeDelay).ConfigureAwait(false);
            await listener.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 释放失败没有处置手段（此时它已不在提供服务），Debug 级别留个痕即可。
            _logger.LogDebug(ex, "延迟释放旧监听器时抛出异常（已忽略）。");
        }
    }

    /// <summary>
    /// 只在"监听器被替换 / 被关闭"这一族情况下重试，其余异常照旧上抛。
    /// 用白名单而不是 <c>catch (Exception)</c>，是为了不把真正的故障（如 OOM、编程错误）吞成静默重试。
    /// </summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        ObjectDisposedException => true,
        IOException => true,
        System.Net.Sockets.SocketException => true,
        InvalidOperationException => true,
        _ => false,
    };

    internal static string DescribeBindFailure(Exception exception, int port)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse })
            {
                return $"端口 {port} 已被其它进程占用，监听保持在原端口。";
            }

            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AccessDenied })
            {
                return $"端口 {port} 需要更高权限（1024 以下的端口在 Linux 上属特权端口），监听保持在原端口。";
            }
        }

        if (exception.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("address is already in use", StringComparison.OrdinalIgnoreCase))
        {
            return $"端口 {port} 已被其它进程占用，监听保持在原端口。";
        }

        return $"端口 {port} 绑定失败（{exception.GetType().Name}: {exception.Message}），监听保持在原端口。";
    }
}

/// <summary>换端口的结果。</summary>
internal sealed record PortSwitchOutcome(bool Ok, bool Changed, int Port, string? Error, Exception? Exception)
{
    public static PortSwitchOutcome Success(int port) => new(true, true, port, null, null);

    public static PortSwitchOutcome Unchanged() => new(true, false, 0, null, null);

    public static PortSwitchOutcome Failed(string error, Exception? exception = null) => new(false, false, 0, error, exception);
}
