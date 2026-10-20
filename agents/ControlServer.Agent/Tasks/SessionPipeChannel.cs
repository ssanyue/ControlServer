using System.IO.Pipes;
using System.Text.Json;
using ControlServer.Core.Services;

namespace ControlServer.Agent.Tasks;

/// <summary>
/// <b>会话侧指令通道的客户端</b> —— 跑在<b>中心服务里</b>（客户端 Agent 服务进程，M4.5）。
///
/// <para>
/// 它按"当前控制台会话"算出管道名，连上去送一条指令、读一条结果。
/// 对面是用户会话里的托盘（见 <see cref="SessionPipeServer"/>）。
/// </para>
///
/// <para>
/// 🔥 <b>为什么每次都要重新问"当前是哪个会话"</b>：会话编号是可以变的
/// （注销再登录可能换一个编号，多用户切换更是如此）。把管道名在启动时算死，
/// 就会出现"注销重登之后音量再也调不动了"这种只能靠重启服务解决的问题。
/// 取会话编号是一次 O(1) 的系统调用，不值当为它做缓存。
/// </para>
///
/// <para>
/// <b>可用性探测 = 真的连一次管道</b>，不搞心跳文件、不搞状态缓存：
/// 本地命名管道的连接开销在一百微秒量级，而"缓存一个 5 秒前的结论"
/// 会直接制造出"托盘刚起来但服务以为它不在"的窗口。宁可多连几次。
/// </para>
/// </summary>
public sealed class SessionPipeChannel : ISessionCommandChannel
{
    private readonly Func<int?> _activeSession;
    private readonly LogService _log;

    /// <summary>
    /// 管道名的"哪一套客户端"后缀（<c>null</c> = 正式那套，名字与老版一致）。
    /// <para>⚠️ 它与托盘那一侧必须算出**同一个值**，见 <see cref="SessionPipeProtocol.PipeNameForSession(int, string?)"/>。</para>
    /// </summary>
    private readonly string? _pipeScope;

    private readonly object _gate = new();

    /// <summary>上一次探测的结论（仅用于"状态翻转"时各记一行日志）。</summary>
    private bool? _lastAvailable;
    private int? _lastSession;

    /// <param name="activeSession">
    /// 返回<b>当前控制台会话编号</b>的委托（没有交互式会话时返回 null）。
    /// <para>
    /// 做成委托而不是在构造时算一次，理由见类注释；实现由组合根给出
    /// （Windows 上是 <c>WindowsSessionLocator.TryGetActiveConsoleSessionId</c>），
    /// 这样本类不必依赖 <c>Platform.Windows</c> —— Agent 库只认 <c>Platform.Abstractions</c>。
    /// </para>
    /// </param>
    /// <param name="log">服务侧的结构化日志（NDJSON 落盘）。</param>
    /// <param name="pipeScope">
    /// 区分"同机另一套客户端"用的后缀（M7.4 §11）。<c>null</c> = 正式那套：
    /// 管道名与老版<b>一个字符都不差</b>，升级前后不会出现对不上的窗口。
    /// 只有改了 <c>Agent:ServiceName</c> 的那套（冒烟 / 验证环境）才需要传自己的服务名。
    /// </param>
    public SessionPipeChannel(Func<int?> activeSession, LogService log, string? pipeScope = null)
    {
        _activeSession = activeSession;
        _log = log;
        _pipeScope = pipeScope;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 命名管道在 .NET 上其实 Linux 也支持，但本项目<b>没有为 Linux 提供会话内的托盘伴侣</b>，
    /// 也就是说 Linux 上这条通道没有对端 —— 报"结构上不可用"才是诚实的，
    /// 免得调用方一直等一个永远不会出现的托盘。
    /// </remarks>
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <inheritdoc />
    /// <remarks>
    /// ⚠️ 这个属性<b>会做一次真实的连接探测，并可能写日志</b>（仅在"可用性翻转 / 会话切换"时各一行）。
    /// 这样设计是刻意的：这类通道最典型的故障是"看起来配好了但就是不生效"，
    /// 而唯一能解释它的证据就是"托盘在不在、在哪个会话"——必须有人把它记下来。
    /// </remarks>
    public bool IsAvailable
    {
        get
        {
            if (!IsSupported) return false;

            var session = _activeSession();

            if (session is null)
            {
                Report(available: false, session: null);
                return false;
            }

            var available = Probe(SessionPipeProtocol.PipeNameForSession(session.Value, _pipeScope));

            Report(available, session);

            return available;
        }
    }

    /// <inheritdoc />
    public async Task<SessionCommandResult> SendAsync(
        SessionCommandRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsSupported)
        {
            return SessionCommandResult.Failure("当前平台没有会话侧指令通道的实现（托盘伴侣只在 Windows 上提供）。");
        }

        var session = _activeSession();

        if (session is null)
        {
            return SessionCommandResult.Failure(
                "当前没有交互式控制台会话（无人登录）—— 这类指令必须在用户桌面里执行，无法在服务会话中完成。");
        }

        var pipeName = SessionPipeProtocol.PipeNameForSession(session.Value, _pipeScope);
        string? lastError = null;

        // 重试一次：有可能刚好处在"服务侧探测连接占着那唯一一个管道实例"的瞬间。
        // 只重试一次是因为下面的等待都很短（连接超时几百毫秒）—— 第二次还失败就说明托盘确实不在，
        // 继续重试只会把一条本该立刻返回的失败拖成几秒的卡顿。
        //
        // ⚠️ 总耗时上限 = 2 × ConnectTimeoutMs + RequestTimeoutMs。把连接超时压到几百毫秒
        //    是**必要的**：Windows 上连一条不存在的命名管道不会立刻报错，
        //    它会一直等到超时。所以"连接超时"这个数字直接决定了"托盘不在时这条指令多久才回话"。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var result = await RoundTripAsync(pipeName, request, cancellationToken).ConfigureAwait(false);

                // 成功一次就重置状态，让"托盘回来了"这件事能被 Report 记下来。
                Report(available: true, session);

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 只有**调用方自己的**取消才往外抛（换配置 / 停机）。
                // ⚠️ 我们内部的"连接超时 / 读写超时"也走 OperationCanceledException 这条路，
                //    若不区分，一次"托盘不在"就会被当成"任务被取消"往上冒，
                //    表现是整条任务链静默中断，而日志里只看到一句没头没尾的取消。
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        Report(available: false, session);

        return SessionCommandResult.Failure($"与用户会话中的托盘通信失败（{pipeName}）：{lastError}");
    }

    // ═══════════════════════════════════════════════════════════
    // 一次请求 / 响应
    // ═══════════════════════════════════════════════════════════

    private async Task<SessionCommandResult> RoundTripAsync(
        string pipeName,
        SessionCommandRequest request,
        CancellationToken cancellationToken)
    {
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        // ── 连接：用自己的短超时 ──
        // 🔥 Windows 上连一条**不存在**的命名管道不是立刻失败，而是**一直等到超时**。
        //    所以这里必须用一个独立的短超时，并且把"我们自己的超时"和"调用方取消"分开：
        //    混在一起的话，"托盘不在"会伪装成"任务被取消"，把整条任务链静默带走。
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectTimeout.CancelAfter(SessionPipeProtocol.ConnectTimeoutMs);

            try
            {
                await client.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"连接会话管道超时（{SessionPipeProtocol.ConnectTimeoutMs} 毫秒）—— 用户会话中的托盘不在。");
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SessionPipeProtocol.RequestTimeoutMs);

        try
        {
            using (var writer = new StreamWriter(
                client,
                SessionPipeProtocol.Utf8NoBom,
                bufferSize: 4096,
                leaveOpen: true))
            {
                await writer.WriteLineAsync(SessionPipeProtocol.Serialize(request)).ConfigureAwait(false);
                await writer.FlushAsync(timeout.Token).ConfigureAwait(false);
            }

            using var reader = new StreamReader(
                client,
                SessionPipeProtocol.Utf8NoBom,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);

            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(line))
            {
                // 连上了、写进去了、但对面一句话没说就关了 —— 通常意味着托盘正好在退出。
                throw new IOException("托盘没有返回结果（它可能正在退出）。");
            }

            return JsonSerializer.Deserialize<SessionCommandResult>(line, SessionPipeProtocol.Json)
                   ?? throw new IOException("托盘的返回内容无法解析。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"等待托盘回应超时（{SessionPipeProtocol.RequestTimeoutMs} 毫秒）—— 它可能正忙或已经卡住。");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 探测与状态报告
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 连一次管道看它在不在。
    /// <para>
    /// ⚠️ 探测是<b>连上就断</b>（不写任何东西）—— 托盘那边读到 <c>null</c>（连接关闭）会静默返回，
    /// 这是双方约定好的"探测"形态，不会在托盘日志里留下噪音。
    /// </para>
    /// </summary>
    private bool Probe(string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            client.Connect(SessionPipeProtocol.ConnectTimeoutMs);

            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            // 管道在，但不让我们连：ACL 被改过 / 托管账户不对。
            // 这与"托盘不在"是完全不同的两码事，必须说出来。
            _log.Warn("Session", $"会话管道存在但无权连接（{pipeName}）：{ex.Message}");

            return false;
        }
        catch
        {
            // 管道不存在 / 托盘没在跑 / 用户没登录 —— 都属于"此刻不可用"，不必逐条记录。
            return false;
        }
    }

    /// <summary>
    /// 仅在 <b>结论发生变化</b> 时各记一行：
    /// 从不可用变可用、从可用变不可用、以及控制台会话换了一个。
    /// <para>
    /// 🔥 这三行是排查"音量/延时启动突然不生效"的<b>唯一线索</b>：
    /// 它们能直接区分"用户注销了""托盘被关了""机器换了个登录用户"，
    /// 而这三种情况在别的日志里长得一模一样（都只是"发指令失败了"）。
    /// </para>
    /// </summary>
    private void Report(bool available, int? session)
    {
        lock (_gate)
        {
            var previousAvailable = _lastAvailable;
            var previousSession = _lastSession;

            if (previousAvailable == available && previousSession == session) return;

            // 首次调用（两个字段都还是 null）不算"变化"，只是建立基线：
            // 启动那一刻的结论由组合根自己打一行自述日志，这里不重复。
            var first = previousAvailable is null && previousSession is null;

            _lastAvailable = available;
            _lastSession = session;

            if (!first && session != previousSession)
            {
                _log.Info("Session", $"控制台会话已切换：{Describe(previousSession)} → {Describe(session)}。");
            }

            if (available == previousAvailable) return;

            if (session is null)
            {
                if (previousAvailable == true)
                {
                    _log.Warn("Session", "会话侧指令通道已断开：当前没有交互式控制台会话（无人登录）。");
                }

                return;
            }

            if (available)
            {
                _log.Info("Session", $"会话侧指令通道已就绪：控制台会话 #{session} 中的托盘在监听。");
            }
            else if (previousAvailable == true)
            {
                // 🔥 只有"从可用变成不可用"才叫断开。第一次就是 false 的情形不在此列
                //    —— 那只是"本来就没人登录"，报"已断开"会让人以为曾经有过。
                _log.Warn("Session", $"会话侧指令通道已断开：控制台会话 #{session} 中的托盘不在（已退出 / 尚未启动）。");
            }
        }
    }

    private static string Describe(int? session) => session is { } id ? $"#{id}" : "(无)";
}
