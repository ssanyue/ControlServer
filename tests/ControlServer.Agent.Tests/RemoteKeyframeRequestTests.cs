using System.Text.Json;
using ControlServer.Agent;
using ControlServer.Agent.Config;
using ControlServer.Agent.Remote;
using ControlServer.Agent.Tasks;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// <b>「中心丢了帧 ⇒ 请托盘补一个关键帧」这条链路的中段</b>（M5.4-c① 步 5）。
///
/// <para>
/// 链路的形状是<b>四跳</b>：
/// <c>中心重组失败</c> → ack 里带 <c>NeedKeyframe</c>（步 2 已做）→
/// <c>AgentConnection</c> 读它（本文件上一批已做）→
/// <b>本文件</b>（<see cref="RemoteSessionCoordinator"/> 把它变成一条发给托盘的指令）→
/// 托盘置 <c>Session.RequestKeyframe</c>（上一批已做）。
/// </para>
///
/// <para>
/// 🔴 <b>本文件守的是"节流"，而它为什么不是优化而是必需</b>：
/// 中心<b>持续</b>丢帧时会<b>每帧都要</b>（一帧十几片，每片都可能带这个标志）。
/// 不做节流 = <b>每帧都出一个 IDR</b> ⇒ 码率翻几倍、帧率掉下来，
/// 而现场只会看到"推流很卡"，没有人会想到根因是"补救动作做得太勤"。
/// 8K 的 IDR 正是整条链路上最贵的东西。
/// </para>
///
/// <para>
/// ⚠️ 反过来<b>也不能不要</b>：完全不给 ⇒ 中心丢一帧之后观看端花/冻到下一个自然 GOP
/// （<c>GopSize 60</c> / 12 fps ⇒ <b>约 5 秒</b>），用户会以为软件卡死了。
/// 所以这是一个<b>两头都不能走极端</b>的参数 —— 用例把两头都钉住。
/// </para>
/// </summary>
public sealed class RemoteKeyframeRequestTests : IAsyncDisposable
{
    private readonly TestDataDirectory _dir = new(
        Path.Combine(Path.GetTempPath(), "cs-keyframe-request-tests", Guid.NewGuid().ToString("N")));

    private readonly LogService _log;

    public RemoteKeyframeRequestTests()
    {
        _log = new LogService(_dir, capacity: 200, flushIntervalMs: 600_000);
    }

    public async ValueTask DisposeAsync()
    {
        await _log.DisposeAsync().ConfigureAwait(false);

        try
        {
            Directory.Delete(_dir.Root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不该让测试变红。
        }
    }

    /// <summary>
    /// 🔥 <b>核心用例</b>：一秒之内连要 5 次，<b>只发出去一条</b>指令。
    /// </summary>
    /// <remarks>
    /// ⚠️ 5 次而不是 2 次：中心在"一帧十几片、片片都丢"时就是这个形状，
    /// 而"合并 2 次"很容易靠一个碰巧的时序通过。
    /// </remarks>
    [Fact]
    public async Task A_repeated_requests_inside_the_interval_are_coalesced_into_one()
    {
        var sessionId = "kf-throttle-" + Guid.NewGuid().ToString("N")[..8];
        var channel = new RecordingCommandChannel();

        await using var coordinator = NewCoordinator(channel);

        (await coordinator.ExecuteAsync(StartCommand(sessionId), "Remote", CancellationToken.None))
            .Outcome.Should().Be(AgentReceivedOutcomes.Executed, "先得有一路在跑，否则请求无处安放");

        (await WaitUntilAsync(() => coordinator.ActiveSessionId is not null, TimeSpan.FromSeconds(5)))
            .Should().BeTrue("前置：这一路要真的在跑 —— 否则后面全在测空气");

        channel.Reset();

        for (var i = 0; i < 5; i++) coordinator.RequestKeyframe(sessionId);

        (await WaitUntilAsync(() => channel.KeyframeRequests() >= 1, TimeSpan.FromSeconds(3)))
            .Should().BeTrue($"等了一会儿一条都没发出去；实际发出的：{Describe(channel)}");

        // 再等一会儿：节流窗口内的第二条若存在，会在这段时间里露出来。
        await Task.Delay(300);

        channel.KeyframeRequests().Should().Be(
            1,
            $"一帧丢一次、一帧十几片 ⇒ 不节流就是每帧一个 IDR；而 8K 的 IDR 是最贵的东西。"
            + $"实际发出的：{Describe(channel)}");
    }

    /// <summary>
    /// <b>会话不在 ⇒ 一条都不发。</b>
    /// </summary>
    /// <remarks>
    /// 🔥 这不是"保守"：请求是发给"正在推流的那一路"的，
    /// 而没有那一路时发过去只会得到一句"此刻没有正在推流的会话" ——
    /// 把一条必然失败的指令塞进管道，还会在日志里制造一条<b>看起来像故障</b>的记录。
    /// </remarks>
    [Fact]
    public async Task B_a_request_for_an_unknown_session_sends_nothing()
    {
        var channel = new RecordingCommandChannel();

        await using var coordinator = NewCoordinator(channel);

        coordinator.RequestKeyframe("no-such-session");

        await Task.Delay(300);

        channel.KeyframeRequests().Should().Be(0, "没有那一路流，指令发给谁？");
    }

    /// <summary>
    /// <b>过了节流窗口，下一次请求必须发出去。</b>
    ///
    /// <para>
    /// ⚠️ 与第一条是一对：上一条防"补太勤"，这一条防"补了一次就再也不补"。
    /// 后者在中心<b>持续</b>丢帧的现场是致命的 —— 第一个关键帧之后又是漫长的花屏，
    /// 而请求再也进不来。
    /// </para>
    /// </summary>
    [Fact]
    public async Task C_after_the_interval_a_new_request_goes_out_again()
    {
        var sessionId = "kf-again-" + Guid.NewGuid().ToString("N")[..8];
        var channel = new RecordingCommandChannel();

        await using var coordinator = NewCoordinator(channel);

        (await coordinator.ExecuteAsync(StartCommand(sessionId), "Remote", CancellationToken.None))
            .Outcome.Should().Be(AgentReceivedOutcomes.Executed);

        (await WaitUntilAsync(() => coordinator.ActiveSessionId is not null, TimeSpan.FromSeconds(5)))
            .Should().BeTrue("前置：这一路要真的在跑");

        channel.Reset();

        coordinator.RequestKeyframe(sessionId);

        (await WaitUntilAsync(() => channel.KeyframeRequests() == 1, TimeSpan.FromSeconds(3)))
            .Should().BeTrue($"第一次请求就该发出去；实际发出的：{Describe(channel)}");

        // ⚠️ 等的是**公开的那个常量**（不是我自己写死的 1 秒）：
        //    否则以后调这个参数，这条用例会以一个莫名其妙的"1 vs 2"失败。
        await Task.Delay(RemoteSessionCoordinator.KeyframeRequestMinInterval + TimeSpan.FromMilliseconds(250));

        coordinator.RequestKeyframe(sessionId);

        (await WaitUntilAsync(() => channel.KeyframeRequests() == 2, TimeSpan.FromSeconds(3)))
            .Should().BeTrue($"过了节流窗口，第二次请求也该发出去；实际发出的：{Describe(channel)}");

        channel.KeyframeRequests().Should().Be(
            2,
            "中心还在丢帧 ⇒ 一秒一个关键帧是能承受的上限；一个都不再给就是" +
            "「恢复了一次之后，后面再也恢复不了」");
    }

    // ═══════════════════════════════════════════════════════════
    // 夹具
    // ═══════════════════════════════════════════════════════════

    private RemoteSessionCoordinator NewCoordinator(RecordingCommandChannel channel)
    {
        var options = new AgentOptions { StateDirectory = _dir.Root };

        var coordinator = new RemoteSessionCoordinator(
            session: channel,
            stream: new IdleStreamChannel(),
            input: new UnavailableSessionInputChannel("测试：不接输入通道"),
            audio: new UnavailableSessionAudioChannel("测试：不接音频通道"),
            inputPolicy: new AgentInputPolicy(options, _log),
            log: _log);

        // ⚠️ 两个出口都要给：中心不在（为 null）时有两条路径会**故意抛异常**结束这一路，
        //    那会在这个用例里变成噪音（"流被自己掐了"看起来像"指令没发出去"）。
        coordinator.FrameSink = (_, _) => Task.CompletedTask;
        coordinator.AudioSink = (_, _) => Task.CompletedTask;
        coordinator.StateSink = _ => Task.CompletedTask;

        return coordinator;
    }

    private static string StartCommand(string sessionId)
        => JsonSerializer.Serialize(new RemoteSessionCommandDto(RemoteSessionActions.Start, sessionId, MaxFps: 30));

    /// <summary>
    /// 轮询等一个条件成立，<b>返回是否等到了</b>。
    /// </summary>
    /// <remarks>
    /// 🔥🔴 <b>必须把返回值断言掉，不许"超时就往下走"</b>：
    /// 超时静默继续会造出一种<b>假 PASS</b> —— 会话压根没起来，后面的断言却在
    /// "发出去的指令条数"上比 0 与 0，看起来一切正常。这正是本仓那条
    /// 「假 FAIL 比不做断言更坏」的镜像：<b>静默超时比假 FAIL 更坏</b>。
    /// </remarks>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;

            await Task.Delay(20);
        }

        return condition();
    }

    /// <summary>把"到底发出去了些什么"写成人话，塞进失败消息里（否则只能猜）。</summary>
    private static string Describe(RecordingCommandChannel channel)
        => channel.Sent.Count == 0
            ? "(一条都没有)"
            : string.Join(" | ", channel.Sent.Select(request => $"{request.Method}:{request.Argument}"));

    /// <summary>
    /// 指令通道桩：<b>把发出去的每一条都记下来</b>。
    /// <para>
    /// ⚠️ 要验的是"发了几条 <c>keyframe</c>"，所以必须解析 <c>Argument</c> 里那个
    /// <c>RemoteStreamRequest</c> 的 <c>Action</c> —— 只看 <c>Method</c> 分不出
    /// <c>start</c> / <c>stop</c> / <c>grayscale</c> / <c>keyframe</c>，它们都走同一条 <c>remote-stream</c>。
    /// </para>
    /// </summary>
    private sealed class RecordingCommandChannel : ISessionCommandChannel
    {
        private readonly List<SessionCommandRequest> _sent = new();

        public bool IsSupported => true;

        public bool IsAvailable => true;

        public IReadOnlyList<SessionCommandRequest> Sent
        {
            get
            {
                lock (_sent) return _sent.ToList();
            }
        }

        public void Reset()
        {
            lock (_sent) _sent.Clear();
        }

        /// <summary>已经发出去的<b>关键帧请求</b>条数。</summary>
        public int KeyframeRequests()
            => Sent.Count(request => request.Method == SessionCommandMethods.RemoteStream
                                     && ReadAction(request.Argument) == RemoteSessionActions.RequestKeyframe);

        public Task<SessionCommandResult> SendAsync(
            SessionCommandRequest request,
            CancellationToken cancellationToken)
        {
            lock (_sent) _sent.Add(request);

            return Task.FromResult(SessionCommandResult.Success("测试：已受理", null));
        }

        private static string? ReadAction(string? argument)
        {
            if (string.IsNullOrWhiteSpace(argument)) return null;

            try
            {
                // 🔴 **必须大小写不敏感**：服务侧序列化用的是 camelCase（`"action"`），
                //    而 `System.Text.Json` 默认<b>大小写敏感</b> ⇒ 读出来 `Action == null`
                //    ⇒ 计数恒为 0。这条坑一开始就把两条用例判成了红（而它们红的原因是**假象**），
                //    所以这里把理由写下来。
                return JsonSerializer.Deserialize<RemoteStreamRequest>(argument, ReadOptions)?.Action;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };
    }

    /// <summary>
    /// 接流通道桩：<b>什么都不送，就一直挂着</b>，直到会话被取消。
    /// <para>
    /// ⚠️ 不喂帧是刻意的：本组用例要证的是"指令发了几条"，
    /// 而喂帧会把 <c>PumpAsync</c> 里的周期上报与计数一起搅进来。
    /// </para>
    /// </summary>
    private sealed class IdleStreamChannel : ISessionStreamChannel
    {
        public bool IsSupported => true;

        public async Task<RemoteStreamOutcome> RunAsync(
            string sessionId,
            Func<RemoteStreamFrame, CancellationToken, Task> onFrame,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常收场。
            }

            return new RemoteStreamOutcome(true, "测试：空闲接流通道收场", 0, 0, 0);
        }
    }
}
