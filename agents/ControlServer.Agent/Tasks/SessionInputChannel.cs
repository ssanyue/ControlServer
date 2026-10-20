using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;
using ControlServer.Contracts;

namespace ControlServer.Agent.Tasks;

/// <summary>一路输入回传的收场结论。</summary>
/// <param name="Ok">是不是"正常收场"（对面主动断开 / 被取消）。</param>
/// <param name="Message">人话说明（进日志）。</param>
/// <param name="Written">一共写过去的事件条数。</param>
/// <param name="Seconds">这条通道活了多久。</param>
public sealed record RemoteInputOutcome(
    bool Ok,
    string Message,
    long Written,
    double Seconds);

/// <summary>
/// 客户机<b>服务侧</b>的输入回传通道（M5.2）：连上托盘的输入管道，把事件一条条写过去。
///
/// <para>
/// 🔥 <b>它与 <see cref="ISessionStreamChannel"/> 是镜像关系，但方向相反</b>：
/// 那条是"托盘写、服务读"（收画面），这条是"服务写、托盘读"（下输入）。
/// 两条各自独立 —— 断开一条不影响另一条（画面断了还能点、输入断了还能看）。
/// </para>
///
/// <para>
/// ⚠️ <b>它绝不能因为"写不出去"而把会话打掉</b>：输入失败只是"点不动"，
/// 而画面还在 —— 把整路会话收掉是把一个局部故障升级成全局故障。
/// 所以本类<b>不抛</b>，结论一律由 <see cref="RemoteInputOutcome"/> 表达。
/// </para>
/// </summary>
public interface ISessionInputChannel
{
    /// <summary>这条通道在当前平台上是否<b>结构上可用</b>（非 Windows 为假）。</summary>
    bool IsSupported { get; }

    /// <summary>
    /// 连上托盘的输入管道，把 <paramref name="inputs"/> 里的事件<b>按顺序</b>写过去，
    /// 直到被取消 / 对面断开 / 队列结束。
    /// </summary>
    /// <remarks>
    /// 顺序不能乱：拖拽是"按下 → 移动 → 松开"，中间任何两条换位都会画出错的轨迹。
    /// 所以本方法<b>单线程串行</b>地读队列、写字，不做任何并行加速。
    /// </remarks>
    /// <param name="onConnected">
    /// 🔥 <b>接入结果</b>（恰好回调一次）：真 = 管道接上了，可以注入；
    /// 假 = 确定接不上（原因在第二个参数里，人话）。
    ///
    /// <para>
    /// <b>为什么这件事非要现在就报出来、而不是等 RunAsync 结束</b>：
    /// 本方法要<b>活到会话结束</b>才返回，而"接不上"是会话刚开始就确定的事实。
    /// 只靠返回值的话，中心要等这路流结束才知道"其实一直点不动" ——
    /// 而用户是在**点不动的那一刻**需要这个答案。
    /// </para>
    /// <para>回调抛异常会被当成通道故障处理（不吞）。</para>
    /// </param>
    Task<RemoteInputOutcome> RunAsync(
        string sessionId,
        ChannelReader<RemoteInputEventDto> inputs,
        Func<bool, string?, CancellationToken, Task>? onConnected,
        CancellationToken cancellationToken);
}

/// <summary>本平台没有输入回传通道时的空实现。</summary>
/// <remarks>
/// 与 <see cref="UnavailableSessionStreamChannel"/> 同一条理由：让上层一个 <c>if</c> 都不用写，
/// 拿到的是一句可读的失败原因，而不是一次静默无操作 ——
/// 而"静默无操作"在这里的现场表现正是"点了没反应"。
/// </remarks>
public sealed class UnavailableSessionInputChannel : ISessionInputChannel
{
    private readonly string _reason;

    public UnavailableSessionInputChannel(string reason) => _reason = reason;

    /// <inheritdoc />
    public bool IsSupported => false;

    /// <summary>不可用的原因（会被带进日志与页面）。</summary>
    public string Reason => _reason;

    /// <inheritdoc />
    public async Task<RemoteInputOutcome> RunAsync(
        string sessionId,
        ChannelReader<RemoteInputEventDto> inputs,
        Func<bool, string?, CancellationToken, Task>? onConnected,
        CancellationToken cancellationToken)
    {
        // ⚠️ 即使是"这条通道根本不存在"，也要把结论回调出去：
        //    不回调的话中心会一直显示"正在连接输入通道"，而它永远不会接上。
        if (onConnected is not null)
        {
            await onConnected(false, _reason, cancellationToken).ConfigureAwait(false);
        }

        return new RemoteInputOutcome(false, $"输入回传通道不可用：{_reason}", 0, 0);
    }
}

/// <summary>
/// 命名管道实现（M5.2）：连 <see cref="RemoteInputProtocol.PipeNameForSession"/>，逐条写事件。
///
/// <para>
/// ⚠️ <b>它只是"写"，不做任何"要不要发这一条"的判断</b> ——
/// 合并（只留最新一条移动）、限流、权限判断全在上层（中心侧）。
/// 这一层唯一的纪律是<b>保序</b>与<b>如实带回原因</b>。
/// </para>
/// </summary>
public sealed class SessionInputChannel : ISessionInputChannel
{
    private readonly Action<string, string> _log;

    /// <param name="log">
    /// 日志出口（等级, 消息）。收委托而不是 <c>LogService</c>：
    /// 本类在用例里要能跑，不该拖一个日志服务进来（与 <c>SessionStreamChannel</c> 一致）。
    /// </param>
    public SessionInputChannel(Action<string, string> log) => _log = log;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async Task<RemoteInputOutcome> RunAsync(
        string sessionId,
        ChannelReader<RemoteInputEventDto> inputs,
        Func<bool, string?, CancellationToken, Task>? onConnected,
        CancellationToken cancellationToken)
    {
        var pipeName = RemoteInputProtocol.PipeNameForSession(sessionId);
        var clock = Stopwatch.StartNew();

        long written = 0;

        try
        {
            await using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);

            try
            {
                // 比托盘那边的等待上限短一点，让"超时"由**托盘**判定并写进它自己的日志
                // （与接流通道同一条理由：两边都等，排查时先要解释"为什么两边都超时"）。
                await client
                    .ConnectAsync(RemoteInputProtocol.ConnectTimeoutMs - 1500, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var reason = $"连不上被控端的输入管道「{pipeName}」：{ex.Message}"
                             + "（最常见的原因：被控端的客户端版本过旧，托盘没有建这条管道；"
                             + "其次是被控端不在用户会话里）。画面不受影响，只是点不动。";

                await NotifyAsync(onConnected, false, reason, cancellationToken).ConfigureAwait(false);

                return Finish(false, reason);
            }

            // 🔥 接上了 —— **立刻回报**，不等这一路流结束。
            //    这一刻起"点不动"的原因就只剩"中心那边的控制开关没打开"了。
            await NotifyAsync(onConnected, true, null, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                RemoteInputEventDto input;

                try
                {
                    if (!await inputs.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) break;

                    if (!inputs.TryRead(out var read) || read is null) continue;

                    input = read;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    await RemoteInputProtocol.WriteAsync(client, input, cancellationToken)
                        .ConfigureAwait(false);

                    written++;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 🔥 写不出去 = 托盘那条管子没了（托盘退出 / 被控端换了会话）。
                    //    **终止条件是"这条通道结束"，不是"这路会话结束"** ——
                    //    见类注释：输入断了画面还能看。
                    return Finish(false, $"输入写不出去，被控端那条管道已断开：{ex.Message}");
                }
            }

            return Finish(true, "输入通道已结束。");
        }
        catch (OperationCanceledException)
        {
            return Finish(true, "已取消。");
        }
        catch (Exception ex)
        {
            // ⚠️ 绝不外抛（见接口注释）。
            return Finish(false, $"输入通道出错：{ex.GetType().Name}：{ex.Message}");
        }

        RemoteInputOutcome Finish(bool ok, string message)
        {
            clock.Stop();

            _log(
                ok ? "Info" : "Warn",
                $"[Remote] 会话 {sessionId} 输入通道结束（{(ok ? "正常" : "异常")}）："
                + $"{message}｜写入 {written} 条｜{clock.Elapsed.TotalSeconds:0.0} 秒");

            return new RemoteInputOutcome(ok, message, written, clock.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// 回调接入结果。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它<b>只回调一次</b>，而且这里不吞异常：回调是"上报给中心"的那个动作，
    /// 而"报不出去"与"接不上"是两件不同的事 —— 吞掉会让上游以为接上了。
    /// （调用方 <c>RemoteSessionCoordinator</c> 自己会把它兜住并只记日志。）
    /// </remarks>
    private static Task NotifyAsync(
        Func<bool, string?, CancellationToken, Task>? onConnected,
        bool ok,
        string? reason,
        CancellationToken cancellationToken)
        => onConnected is null ? Task.CompletedTask : onConnected(ok, reason, cancellationToken);
}
