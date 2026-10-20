using System.Diagnostics;
using System.IO.Pipes;
using ControlServer.Contracts;

namespace ControlServer.Agent.Tasks;

/// <summary>一路推流的收场结论。</summary>
/// <param name="Ok">是不是"正常收场"（对面主动断开）。</param>
/// <param name="Message">人话说明（进日志与会话状态）。</param>
/// <param name="Frames">一共收到多少帧。</param>
/// <param name="Bytes">一共收到多少码流字节。</param>
/// <param name="Seconds">这路流持续了多久。</param>
public sealed record RemoteStreamOutcome(
    bool Ok,
    string Message,
    long Frames,
    long Bytes,
    double Seconds);

/// <summary>
/// 客户机<b>服务侧</b>的接流通道（M5.1）：连上托盘的码流管道，把帧一帧帧读出来。
///
/// <para>
/// 🔥 <b>它与 <see cref="ISessionCommandChannel"/> 是两条不同的通道，别合并</b>：
/// </para>
/// <list type="bullet">
///   <item><see cref="ISessionCommandChannel"/> = <b>发一条指令、拿一个结果</b>，
///         低频（一天也没几次），一请求一连接；</item>
///   <item>本接口 = <b>持续收一串二进制帧</b>，10–30 fps，一条长连接。</item>
/// </list>
///
/// <para>
/// 硬把码流塞进前者，代价是前面那条通道会被独占（音量、延时启动全部排队到流结束）。
/// </para>
/// </summary>
public interface ISessionStreamChannel
{
    /// <summary>
    /// 这条通道在当前平台上是否<b>结构上可用</b>。
    /// <para>
    /// 与 <see cref="ISessionCommandChannel.IsSupported"/> 同一个语义：
    /// 为假时等待毫无意义，调用方应当直接放弃。
    /// </para>
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// 接一路流，直到对面断开 / 出错 / 被取消。
    /// <para>
    /// <b>不抛</b>：结果一律由 <see cref="RemoteStreamOutcome"/> 表达。
    /// 收场原因必须带回来 —— 它是"画面为什么停了"的唯一答案。
    /// </para>
    /// </summary>
    /// <param name="onFrame">
    /// 每收到一帧调一次。<b>它抛异常 = 立刻结束这路流</b>
    /// （典型场景：与中心的长连接断了，再收只会白烧被控机的 CPU）。
    /// </param>
    Task<RemoteStreamOutcome> RunAsync(
        string sessionId,
        Func<RemoteStreamFrame, CancellationToken, Task> onFrame,
        CancellationToken cancellationToken);
}

/// <summary>本平台没有接流通道时的空实现。</summary>
/// <remarks>
/// 与 <c>UnavailableSessionCommandChannel</c> 同一条理由：让上层一个 <c>if</c> 都不用写，
/// 拿到的是一句可读的失败原因，而不是 <c>NullReferenceException</c>。
/// </remarks>
public sealed class UnavailableSessionStreamChannel : ISessionStreamChannel
{
    private readonly string _reason;

    public UnavailableSessionStreamChannel(string reason) => _reason = reason;

    /// <inheritdoc />
    public bool IsSupported => false;

    /// <summary>不可用的原因（会被带进日志）。</summary>
    public string Reason => _reason;

    /// <inheritdoc />
    public Task<RemoteStreamOutcome> RunAsync(
        string sessionId,
        Func<RemoteStreamFrame, CancellationToken, Task> onFrame,
        CancellationToken cancellationToken)
        => Task.FromResult(new RemoteStreamOutcome(false, $"接流通道不可用：{_reason}", 0, 0, 0));
}

/// <summary>
/// 命名管道实现（M5.1）：连<see cref="RemoteStreamProtocol.PipeNameForSession"/>，逐帧读。
///
/// <para>
/// ⚠️ <b>它只是"读"，不做任何"要不要这一帧"的判断</b> ——
/// 丢弃策略（中心连不上了、帧太旧了）属于上层。
/// 这一层唯一的纪律是：<b>它绝不把"读失败"当正常收场</b>，
/// 而是把原因原样带回去。
/// </para>
/// </summary>
public sealed class SessionStreamChannel : ISessionStreamChannel
{
    private readonly Action<string, string> _log;

    /// <param name="log">
    /// 日志出口（等级, 消息）。刻意收委托而不是 <c>LogService</c>：
    /// 本类在测试里要能跑，不该拖一个日志服务进来。
    /// </param>
    public SessionStreamChannel(Action<string, string> log) => _log = log;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async Task<RemoteStreamOutcome> RunAsync(
        string sessionId,
        Func<RemoteStreamFrame, CancellationToken, Task> onFrame,
        CancellationToken cancellationToken)
    {
        var pipeName = RemoteStreamProtocol.PipeNameForSession(sessionId);
        var clock = Stopwatch.StartNew();

        long frames = 0;
        long bytes = 0;

        try
        {
            await using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);

            try
            {
                // ⚠️ 连接超时给得比托盘那边的等待上限（8 秒）短一点：
                //    这样"超时"这件事由**托盘**来判定并写进它自己的日志，
                //    而服务这边看到的是一句"连不上"。反过来（服务等更久）会出现
                //    "托盘已经作废了，服务还在傻等" —— 两边对不上，排查时先要解释这个。
                await client.ConnectAsync(RemoteStreamProtocol.ConnectTimeoutMs - 1500, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Finish(
                    false,
                    $"连不上被控端的码流管道「{pipeName}」：{ex.Message}"
                    + "（常见原因：托盘没在跑 / 被控端是 Session 0 里启动的托盘）。");
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                RemoteStreamFrame? frame;

                try
                {
                    frame = await RemoteStreamProtocol.ReadFrameAsync(client, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return Finish(frames > 0, $"码流读取中断：{ex.Message}");
                }

                // null = 对面干净地关了（停止推流 / 托盘退出）。
                if (frame is null) return Finish(true, "被控端已结束推流。");

                frames++;
                bytes += frame.Data.Length;

                try
                {
                    await onFrame(frame, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 🔥 上层的"送不出去"是**终止条件**：中心那条长连接断了之后，
                    //    继续收帧只会一遍遍失败，而每失败一次被控端就白抓白编一帧。
                    //    让流停下来，等重连成功再由中心重新开会话。
                    return Finish(false, $"帧送不出去，已主动结束这路流：{ex.Message}");
                }
            }

            return Finish(true, "已取消。");
        }
        catch (OperationCanceledException)
        {
            return Finish(true, "已取消。");
        }
        catch (Exception ex)
        {
            // ⚠️ 绝不外抛（见接口注释）。
            return Finish(false, $"接流时出错：{ex.GetType().Name}：{ex.Message}");
        }

        RemoteStreamOutcome Finish(bool ok, string message)
        {
            clock.Stop();

            _log(ok ? "Info" : "Warn",
                $"[Remote] 会话 {sessionId} 接流结束（{(ok ? "正常" : "异常")}）："
                + $"{message}｜收帧 {frames}｜{bytes / 1024} KB｜{clock.Elapsed.TotalSeconds:0.0} 秒");

            return new RemoteStreamOutcome(ok, message, frames, bytes, clock.Elapsed.TotalSeconds);
        }
    }
}
