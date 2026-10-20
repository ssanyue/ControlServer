using System.Diagnostics;
using System.IO.Pipes;
using ControlServer.Contracts;

namespace ControlServer.Agent.Tasks;

/// <summary>一路音频的收场结论。</summary>
/// <param name="Ok">是不是"正常收场"（对面主动断开 / 采集结束）。</param>
/// <param name="Message">人话说明（进日志与会话状态，<b>并且会成为"音频不可用"的原因</b>）。</param>
/// <param name="Frames">一共收到多少段音频。</param>
/// <param name="Bytes">一共收到多少码流字节。</param>
/// <param name="Seconds">这路音频持续了多久。</param>
public sealed record RemoteAudioOutcome(
    bool Ok,
    string Message,
    long Frames,
    long Bytes,
    double Seconds);

/// <summary>
/// 客户机<b>服务侧</b>的接音频通道（M5.3）：连上托盘的音频管道，把一段段音频读出来。
///
/// <para>
/// 🔥 <b>它与 <see cref="ISessionStreamChannel"/> 是两条通道，别合并</b>：
/// 视频那条的失败是"画面没了"，音频这条的失败是"声音没了" ——
/// 它们<b>互不影响</b>，而且要保证这一点在结构上成立（分开的两条管道 + 两条任务）。
/// 合并成一条"媒体通道"之后，音频设备掉线会把画面一起带走，而现场只会看到"整条远控挂了"。
/// </para>
/// </summary>
public interface ISessionAudioChannel
{
    /// <summary>这条通道在当前平台上是否<b>结构上可用</b>。为假时等待毫无意义。</summary>
    bool IsSupported { get; }

    /// <summary>
    /// 接一路音频，直到对面断开 / 出错 / 被取消。<b>不抛</b>：结果由 <see cref="RemoteAudioOutcome"/> 表达。
    /// </summary>
    /// <param name="onFrame">
    /// 每收到一段调一次。<b>它抛异常 = 立刻结束这路音频</b>
    /// （典型场景：与中心的长连接断了，再收只会白烧被控机的 CPU）。
    /// </param>
    Task<RemoteAudioOutcome> RunAsync(
        string sessionId,
        Func<RemoteAudioFrame, CancellationToken, Task> onFrame,
        CancellationToken cancellationToken);
}

/// <summary>本平台没有接音频通道时的空实现。</summary>
public sealed class UnavailableSessionAudioChannel : ISessionAudioChannel
{
    private readonly string _reason;

    public UnavailableSessionAudioChannel(string reason) => _reason = reason;

    /// <inheritdoc />
    public bool IsSupported => false;

    /// <summary>不可用的原因（会被带进日志与页面）。</summary>
    public string Reason => _reason;

    /// <inheritdoc />
    public Task<RemoteAudioOutcome> RunAsync(
        string sessionId,
        Func<RemoteAudioFrame, CancellationToken, Task> onFrame,
        CancellationToken cancellationToken)
        => Task.FromResult(new RemoteAudioOutcome(false, _reason, 0, 0, 0));
}

/// <summary>
/// 命名管道实现（M5.3）：连 <see cref="RemoteAudioProtocol.PipeNameForSession"/>，逐段读。
///
/// <para>
/// ⚠️ 与 <see cref="SessionStreamChannel"/> 一样：<b>它只负责读，不做任何丢弃判断</b>。
/// 这一层唯一的纪律是"绝不把读失败当正常收场"——原因要原样带回去，
/// 因为它是"为什么没有声音"唯一能到页面上的一句话。
/// </para>
///
/// <para>
/// 🔥 <b>连接超时给得比托盘那边（5 秒）短 1 秒</b>：让"超时"这件事由<b>托盘</b>判定，
/// 服务这边看到的是"连不上"。反过来（服务等更久）会出现"托盘已经作废了、服务还在傻等"，
/// 两边日志对不上，排查时先要解释这个。
/// </para>
/// </summary>
public sealed class SessionAudioChannel : ISessionAudioChannel
{
    private readonly Action<string, string> _log;

    /// <param name="log">日志出口（等级, 消息）。收委托而不是 <c>LogService</c>：本类要能在测试里跑。</param>
    public SessionAudioChannel(Action<string, string> log) => _log = log;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async Task<RemoteAudioOutcome> RunAsync(
        string sessionId,
        Func<RemoteAudioFrame, CancellationToken, Task> onFrame,
        CancellationToken cancellationToken)
    {
        var pipeName = RemoteAudioProtocol.PipeNameForSession(sessionId);
        var clock = Stopwatch.StartNew();

        long frames = 0;
        long bytes = 0;

        try
        {
            await using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);

            try
            {
                await client.ConnectAsync(RemoteAudioProtocol.ConnectTimeoutMs - 1000, cancellationToken)
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
                    $"连不上被控端的音频管道「{pipeName}」：{ex.Message}"
                    + "（最常见：那台机器没有可用的音频输出设备，或托盘版本过旧不认识这条通道）。");
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                RemoteAudioFrame? frame;

                try
                {
                    frame = await RemoteAudioProtocol.ReadFrameAsync(client, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return Finish(frames > 0, $"音频读取中断：{ex.Message}");
                }

                // null = 对面干净地关了（停止推流 / 托盘退出 / 采集结束）。
                if (frame is null) return Finish(true, "被控端已结束音频。");

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
                    //    继续收只会一遍遍失败，而每失败一次被控端就白采白编一段音频。
                    return Finish(false, $"音频送不出去，已主动结束这路音频：{ex.Message}");
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
            return Finish(false, $"接音频时出错：{ex.GetType().Name}：{ex.Message}");
        }

        RemoteAudioOutcome Finish(bool ok, string message)
        {
            clock.Stop();

            _log(
                ok ? "Info" : "Warn",
                $"[Remote] 会话 {sessionId} 音频结束（{(ok ? "正常" : "异常")}）："
                + $"{message}｜收段 {frames}｜{bytes / 1024} KB｜{clock.Elapsed.TotalSeconds:0.0} 秒");

            return new RemoteAudioOutcome(ok, message, frames, bytes, clock.Elapsed.TotalSeconds);
        }
    }
}
