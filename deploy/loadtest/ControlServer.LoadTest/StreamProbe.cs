using System.Diagnostics;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.LoadTest;

/// <summary>
/// L3：<b>在 N 台在线的情况下，跑 1–2 路假推流</b>，看中心那条转发链路会不会被饿死。
///
/// <para>
/// 🔥 <b>这不是"100 路远控"</b>。方案 §2.3 写得很直白：100 台里只有 1–2 路在推流，
/// 要回答的问题是"100 台的心跳与遥测会不会把一路推流饿死"，不是"中心能不能转 100 路"。
/// </para>
///
/// <para>
/// <b>与真实链路的差别（必须写进报告）</b>：
/// </para>
/// <list type="bullet">
///   <item>帧是<b>随机字节</b>，不是真编码器产物 —— 中心不解码，只转发，所以对中心的压力是真实的；</item>
///   <item><b>起流由本工具直接触发</b>（拿到 sessionId 就推），不等假 Agent 收到 start 命令 ——
///         真 Agent 那里还有"挑屏 + 建编码器"的一段延迟，而那段是<b>被控端</b>的成本；</item>
///   <item>观看端是<b>假观看端</b>（只数帧，不解码不画），所以这里测不到浏览器解码的开销。</item>
/// </list>
///
/// <para>
/// ⚠️ 中心那条转发队列是<b>容量 1 + 丢最旧</b>（设计如此）。所以"收到 &lt; 发出"是正常的，
/// 报告给的是<b>到达率</b>与<b>端到端延迟</b>，不是"零丢帧"。
/// </para>
/// </summary>
public sealed class StreamProbe : IAsyncDisposable
{
    private readonly LoadTestOptions _options;
    private readonly AdminClient _admin;
    private readonly Metrics _metrics;
    private readonly LogService _log;

    private readonly List<HubConnection> _viewers = new();
    private readonly byte[] _payload;

    public StreamProbe(LoadTestOptions options, AdminClient admin, Metrics metrics, LogService log)
    {
        _options = options;
        _admin = admin;
        _metrics = metrics;
        _log = log;

        _payload = new byte[options.StreamFrameBytes];
        Random.Shared.NextBytes(_payload);
    }

    /// <summary>建会话时遇到的问题（报告里要列出来，不许静默降级成"没测"）。</summary>
    public List<string> Issues { get; } = new();

    /// <summary>实际跑起来的流数。</summary>
    public int ActiveStreams { get; private set; }

    /// <summary>
    /// 对给定的假 Agent 起 <paramref name="count"/> 路推流，直到 <paramref name="stopAt"/>。
    /// <para>若某一路没建起来，记进 <see cref="Issues"/> 并继续 —— 一路失败不该毁掉整轮压测。</para>
    /// </summary>
    public async Task RunAsync(
        IReadOnlyList<AgentWorker> workers,
        DateTimeOffset startAt,
        DateTimeOffset stopAt,
        CancellationToken cancellationToken)
    {
        var streams = new List<StreamRun>();

        for (var i = 0; i < _options.Streams && i < workers.Count; i++)
        {
            var worker = workers[i];

            // 会话要等它真的连上（中心那边 StartAsync 会检查长连接在不在）。
            while (worker.Connection is null && DateTimeOffset.UtcNow < startAt && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            if (worker.Connection is null)
            {
                Issues.Add($"流 #{i + 1}：机器 #{worker.AgentId} 没有长连接，跳过。");
                continue;
            }

            var (session, message) = await _admin
                .StartSessionAsync(worker.AgentId, cancellationToken)
                .ConfigureAwait(false);

            if (session is null)
            {
                Issues.Add($"流 #{i + 1}：建会话失败 —— {message}");
                continue;
            }

            var viewer = await OpenViewerAsync(session.SessionId, cancellationToken).ConfigureAwait(false);

            if (viewer is null)
            {
                Issues.Add($"流 #{i + 1}：会话 {session.SessionId} 建好了，但假观看端没连上。");
                await _admin.StopSessionAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var run = new StreamRun(session.SessionId, worker, viewer);
            streams.Add(run);

            _log.Info("LoadTest", $"L3 流 #{i + 1} 已就绪：会话 {session.SessionId}（机器 #{worker.AgentId}）");
        }

        ActiveStreams = streams.Count;

        if (streams.Count == 0)
        {
            Issues.Add("L3：一路都没跑起来 —— 本轮的推流结论无效。");
            return;
        }

        // 每一路一个推帧循环（顺序 await 是协议要求：同一帧的片必须按序，整帧也避免交错）。
        var pumps = streams
            .Select(stream => Task.Run(() => PumpAsync(stream, stopAt, cancellationToken), cancellationToken))
            .ToArray();

        try
        {
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
        finally
        {
            foreach (var stream in streams)
            {
                await _admin.StopSessionAsync(stream.SessionId, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task PumpAsync(StreamRun stream, DateTimeOffset stopAt, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(1.0 / _options.StreamFps);
        var next = DateTimeOffset.UtcNow;
        long sequence = 0;

        while (DateTimeOffset.UtcNow < stopAt && !cancellationToken.IsCancellationRequested)
        {
            var frame = new RemoteStreamFrame(
                new RemoteStreamFrameHeader(
                    SessionId: stream.SessionId,
                    Sequence: sequence++,
                    Codec: RemoteCodecs.H264,
                    IsKeyframe: sequence % Math.Max(1, _options.StreamFps) == 0,
                    Width: 1920,
                    Height: 1080,
                    CapturedUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Dropped: 0),
                _payload);

            try
            {
                // ⚠️ 与真 Agent 同一条路：`PushRemoteFrameAsync` 会在"中心拒收"时抛。
                //    这里把它记成 send-fail 并继续 —— 压测要的是统计，不是第一帧就退出。
                await stream.Worker.Connection!
                    .PushRemoteFrameAsync(frame, cancellationToken)
                    .ConfigureAwait(false);

                _metrics.RecordFrameSent(true);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _metrics.RecordFrameSent(false);
                _log.Warn("LoadTest", $"L3 会话 {stream.SessionId} 推帧失败：{ex.Message}");
            }

            next += interval;
            var delay = next - DateTimeOffset.UtcNow;

            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            else next = DateTimeOffset.UtcNow;   // 落后了就从现在重新起算，不追帧
        }
    }

    private async Task<HubConnection?> OpenViewerAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var builder = new HubConnectionBuilder()
                .WithUrl(_options.Server + Routes.RemoteHub, options =>
                {
                    // 🔥 Hub 的 WebSocket 握手不走 HttpClientHandler，
                    //    Cookie 必须显式给，否则"HTTP 都 200、Hub 一连就 401"。
                    options.Cookies = _admin.Cookies;
                    options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
                });

            // ⚠️ 客户端**不需要**配接收上限：`MaximumReceiveMessageSize` 只存在于**服务端**
            //    （SignalR 8.0.10 的客户端程序集里没有这个符号，实测 grep 为空）。
            //    8 MiB 那条线是中心收帧用的，与本工具无关。
            var connection = builder.Build();

            connection.On<RemoteStreamFrame>(RemoteHubMethods.Frame, frame =>
            {
                var latency = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - frame.Header.CapturedUnixMs;
                _metrics.RecordFrameReceived(latency);
            });

            await connection.StartAsync(cancellationToken).ConfigureAwait(false);

            var ack = await connection
                .InvokeAsync<RemoteWatchAckDto>("Join", sessionId, cancellationToken)
                .ConfigureAwait(false);

            if (ack is { Ok: false })
            {
                _log.Warn("LoadTest", $"L3 观看端 Join 被拒：{ack.ErrorCode} {ack.Message}");
                await connection.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            _viewers.Add(connection);
            return connection;
        }
        catch (Exception ex)
        {
            _log.Warn("LoadTest", $"L3 观看端连接失败：{ex.Message}");
            return null;
        }
    }

    private sealed record StreamRun(string SessionId, AgentWorker Worker, HubConnection Viewer);

    public async ValueTask DisposeAsync()
    {
        foreach (var viewer in _viewers)
        {
            try
            {
                await viewer.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 收尾失败不影响统计。
            }
        }

        _viewers.Clear();
    }
}
