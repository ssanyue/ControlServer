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
/// <b>推流期间的上报节奏</b>（M5.4-b 收尾 · 方案 §3.12 (m)④）。
///
/// <para>
/// 🔥🔴 <b>这组用例守的是一个「页面上一格永远是 <c>—</c>」的缺陷。</b>改之前：
/// 整路会话里<b>只有终态那条</b>上报带计数（"进入 streaming"那条刻意
/// <c>withCounters: false</c>）⇒ 会话活着的时候，中心手上的 <c>AgentStats</c>
/// 恒为 <c>null</c>，页面那格「被控端实测帧率」永远是 <c>—</c>。
/// </para>
///
/// <para>
/// 它为什么不能当"小事"：那一格与"中心收了多少帧"是<b>并排看</b>才有意义的一对
/// —— 被控端发 500 / 中心收 12 ⇒ 链路上在掉；被控端发 3 / 中心收 3 ⇒ 是被控端编不动。
/// <b>只给一个数时，这两种故障长得一模一样，而处置相反。</b>
/// 所以缺的其实不是"一个数字"，是<b>一半的诊断能力</b>。
/// </para>
///
/// <para>
/// ⚠️ 本文件刻意断言"<b>不止一条</b>带计数的 streaming 上报"：这是「<b>周期性</b>」
/// 与「一次性」的分界线。只把那条上报的 <c>withCounters</c> 翻成 <c>true</c>
/// 是不够的 —— 那一刻计数必然是 0（<c>Record</c> 要等这一帧送出去之后才跑），
/// 页面上会从 <c>—</c> 变成 <c>0 fps</c>，一样是错的。
/// </para>
/// </summary>
public sealed class RemoteSessionReportTests : IAsyncDisposable
{
    /// <summary>
    /// 接流通道桩的收场文案。<b>它是"接流泵自己那条终态上报"的识别标记</b> ——
    /// 停止路径上有两条 <c>stopped</c> 上报（`StopAsync` 一条、泵自己收场再一条），
    /// 两条的<b>原因那句不一样</b>，所以用这一句就能精确等第二条。
    /// </summary>
    private const string StreamStopsMessage = "测试：接流泵自己收场";

    private readonly TestDataDirectory _dir = new(
        Path.Combine(Path.GetTempPath(), "cs-remote-report-tests", Guid.NewGuid().ToString("N")));

    private readonly LogService _log;

    public RemoteSessionReportTests()
    {
        // flushIntervalMs 设得很大：这些用例只关心内存里的日志，不关心落盘。
        _log = new LogService(_dir, capacity: 500, flushIntervalMs: 600_000);
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
            // 临时目录删不掉不该让测试变红（Windows 上偶尔有句柄没释放）。
        }
    }

    /// <summary>
    /// 🔥 <b>核心用例</b>：推流期间<b>周期性</b>地报出"被控端已经发出了多少"。
    /// </summary>
    [Fact]
    public async Task Live_counters_are_reported_repeatedly_while_streaming()
    {
        var reports = new List<AgentRemoteSessionStateDto>();

        await using var coordinator = NewCoordinator(reports);

        var started = await coordinator.ExecuteAsync(
            StartCommand("live-counters"),
            "Remote",
            CancellationToken.None);

        started.Outcome.Should().Be(
            AgentReceivedOutcomes.Executed,
            $"开始推流本该成功，实际：{started.Detail}");

        // ── 等"带非零计数的 streaming 上报"出现**两条**（够说明它是周期性的）──
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline && Snapshot(reports).Count(IsLiveCounted) < 2)
        {
            await Task.Delay(50);
        }

        var seen = Snapshot(reports);

        // ── ① 带非零计数的 streaming 上报**确实来了** ──
        seen.Should().Contain(
            dto => IsLiveCounted(dto),
            "会话活着的时候就必须把「被控端发出去多少」报给中心 —— 否则页面那格恒为「—」，"
            + "而「它发得多、我们收得少」这类链路故障在会话进行中根本看不出来。"
            + $"实际上报：{Describe(seen)}");

        // ── ② **不止一条**：是一次性的还是周期性的，分界就在这里 ──
        seen.Count(IsLiveCounted).Should().BeGreaterThanOrEqualTo(
            2,
            "只报一次等于把「活数」变成「第一秒的定格值」—— 用户看到的会是一个**不动**的帧率，"
            + "而那比「—」更坏（它看起来是正常的）。"
            + $"实际上报：{Describe(seen)}");

        // ── ③ 计数只能往前走 ──
        var counted = seen.Where(IsLiveCounted).Select(d => d.Frames).ToList();

        counted.Should().BeInAscendingOrder(
            "同一路会话的累计发帧数只会增长 —— 出现回退说明有两份计数在互相覆盖");

        // ── ④ 两条"进入 streaming"的上报里，**第一条的计数必然是 0** ──
        //
        // 🔥 这一条是**为什么不能只把 `withCounters` 翻成 `true`** 的证据：
        //    宣布"我开始推了"的那一刻，`Record` 还没跑（它在这一帧送出去之后才跑），
        //    所以计数必然是 0。报一份 0 出去，页面就从「—」变成「0 fps」。
        var firstStreaming = seen.First(d => d.State == RemoteSessionStates.Streaming);

        firstStreaming.Frames.Should().Be(
            0,
            "「开始推了」那一刻计数必然是 0 —— 这就是那条上报刻意 `withCounters: false` 的原因；"
            + "把它翻成 true 只会让页面从「—」变成「0 fps」，一样是错的");

        // ── ⑤ 收场照样要报（不能被周期上报挤掉）──
        await coordinator.ExecuteAsync(
            StopCommand("live-counters"),
            "Remote",
            CancellationToken.None);

        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (DateTime.UtcNow < deadline
               && !Snapshot(reports).Any(d => d.State == RemoteSessionStates.Stopped))
        {
            await Task.Delay(50);
        }

        Snapshot(reports).Should().Contain(
            d => d.State == RemoteSessionStates.Stopped,
            "周期上报**不许**把终态上报挤掉 —— 终态是「这路为什么停了」的唯一答案");
    }

    /// <summary>
    /// 推流期间的周期上报<b>不许</b>迟到终态之后。
    ///
    /// <para>
    /// 🔥 它挡的是一条"看起来只是慢半拍"的路：周期上报若比终态活得久，
    /// 最后一条到达中心的会是 <c>streaming</c>，页面于是显示"会话还在推流"——
    /// 而它其实早就停了，且**不会自己好**（不会再有任何一条上报来纠正它）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 中心侧其实还有一道独立的免疫（<c>ApplyState</c> 里
    /// <c>if (State == Requested) State = Streaming;</c>，即"只许 requested → streaming，
    /// 不许往回走"），所以这里证的是<b>被控端不制造这种消息</b>，不是中心接不接得住。
    /// 两道都成立才是"这件事不会发生"。
    /// </para>
    /// </summary>
    [Fact]
    public async Task No_report_arrives_after_the_stream_has_been_stopped()
    {
        var reports = new List<AgentRemoteSessionStateDto>();

        await using var coordinator = NewCoordinator(reports);

        (await coordinator.ExecuteAsync(StartCommand("report-order"), "Remote", CancellationToken.None))
            .Outcome.Should().Be(AgentReceivedOutcomes.Executed);

        // 先等它真的进了 streaming（否则下面的"停止"打在一个还没起来的会话上，测不到东西）。
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline && !Snapshot(reports).Any(IsLiveCounted))
        {
            await Task.Delay(50);
        }

        Snapshot(reports).Should().Contain(d => IsLiveCounted(d), "前置条件：这一路得先在推流");

        await coordinator.ExecuteAsync(StopCommand("report-order"), "Remote", CancellationToken.None);

        // 🔥 等的是**接流泵自己那条**终态上报，不是"第一条 stopped"。
        //
        //    停止路径上**有两条** <c>stopped</c>：`StopAsync` 报一条、接流泵自己收场再报一条
        //    （它们的内容不同 —— 原因那句一个来自停止指令、一个来自接流结论，所以分得开）。
        //
        //    ⚠️ 为什么非要等第二条：周期上报是**在泵的 finally 里先取消、await 干净、
        //       再去报终态**的 ⇒ **第二条到达**就等于"周期上报已经停干净了"。
        //       等第一条的话，泵那时可能还在收尾 —— 下面那个"计数不许再长"的比较
        //       就会把**正常收尾**当成"迟到的周期上报"，变成一条取决于调度的**假 FAIL**
        //       （这个仓里已经吃过一次这样的亏：读完 want 帧就拔管子）。
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);

        while (DateTime.UtcNow < deadline
               && !Snapshot(reports).Any(d => d.State == RemoteSessionStates.Stopped
                                              && d.Reason == StreamStopsMessage))
        {
            await Task.Delay(50);
        }

        Snapshot(reports).Should().Contain(
            d => d.State == RemoteSessionStates.Stopped && d.Reason == StreamStopsMessage,
            "前置条件：接流泵自己那条终态上报必须到 —— 它是「周期上报已经停干净了」的信号。"
            + $"实际上报：{Describe(Snapshot(reports))}");

        // ⚠️ 记下"泵收完场那一刻"的快照，再等**两个上报周期以上**看它有没有再长。
        var before = Snapshot(reports);

        await Task.Delay(TimeSpan.FromMilliseconds(2600));

        var after = Snapshot(reports);

        after.Count(d => d.State == RemoteSessionStates.Streaming).Should().Be(
            before.Count(d => d.State == RemoteSessionStates.Streaming),
            "会话停了之后不许再有新的 streaming 上报 —— 一条迟到的 streaming 会把页面钉在"
            + "「还在推流」上，而且不会自己好。"
            + $"停止后新增的上报：{Describe(after.Skip(before.Count).ToList())}");

        after.Count.Should().Be(
            before.Count,
            "接流泵收完场之后**一条上报都不该再来** —— 周期上报必须在报终态之前彻底停掉，"
            + $"而不是「再补一次」。停止后新增的上报：{Describe(after.Skip(before.Count).ToList())}");
    }

    // ═══════════════════════════════════════════════════════════
    // 桌面静止时长（M5.4-c② 步 7）—— 服务这一侧
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>托盘说得出的那个数，必须原样进每一次状态上报。</b>
    ///
    /// <para>
    /// 它关的是 <c>REF-M5 §14.14</c> 那条挂账：修完活性判据之后，
    /// <b>「桌面静止」与「托盘卡住」在协议层上一模一样</b>（都是"没帧、状态照报"），
    /// 中心只能靠猜。而被控端<b>早就知道</b>，它只是不说 —— 本步就是让它说。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 判据刻意落在 <b>值</b>（4200）上，而不是"不为 null"：
    /// 那样的话，把上一路的旧值照抄过来也会绿，而页面上会显示一个**不动的**秒数
    /// —— 比"没有"更坏（它看起来是合理的）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_desktop_idle_time_is_carried_in_periodic_reports()
    {
        var reports = new List<AgentRemoteSessionStateDto>();

        await using var coordinator = NewCoordinator(reports, desktopIdleMs: 4200);

        (await coordinator.ExecuteAsync(StartCommand("idle-report"), "Remote", CancellationToken.None))
            .Outcome.Should().Be(AgentReceivedOutcomes.Executed);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline && !Snapshot(reports).Any(d => d.DesktopIdleMs == 4200))
        {
            await Task.Delay(50);
        }

        Snapshot(reports).Should().Contain(
            d => d.DesktopIdleMs == 4200,
            "「桌面静止了多久」只有被控端知道（抓屏器回「没有新帧」就是答案）—— "
            + "它必须随周期上报到中心，否则页面在「画面卡住」与「桌面真的没动」之间只能猜。"
            + $"实际上报：{DescribeIdle(Snapshot(reports))}");
    }

    /// <summary>
    /// 托盘<b>不会回答</b>这条查询（旧版本）⇒ 上报里必须是 <c>null</c>，<b>不是 <c>0</c></b>。
    /// </summary>
    /// <remarks>
    /// 🔥 这一条钉的是三态里的第一格。<c>0</c> 的意思是"桌面刚动过"，
    /// 把它当成"不知道"用，页面上就会写着"桌面刚动过"，而实际上这台机器
    /// <b>根本没有这个信息</b> —— 一句看起来很合理、却是编出来的话。
    /// </remarks>
    [Fact]
    public async Task A_tray_that_cannot_answer_leaves_the_idle_time_unknown()
    {
        var reports = new List<AgentRemoteSessionStateDto>();

        await using var coordinator = NewCoordinator(
            reports,
            desktopIdleMs: null,
            answersDesktopIdle: false);

        (await coordinator.ExecuteAsync(StartCommand("idle-unknown"), "Remote", CancellationToken.None))
            .Outcome.Should().Be(AgentReceivedOutcomes.Executed);

        // 等两条带计数的上报：确认周期上报**跑起来了**（这才能说"它没有把 null 变成 0"）。
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline && Snapshot(reports).Count(IsLiveCounted) < 2)
        {
            await Task.Delay(50);
        }

        var seen = Snapshot(reports);

        seen.Count(IsLiveCounted).Should().BeGreaterThanOrEqualTo(
            2,
            "前置条件：周期上报得真的跑起来 —— 否则「全是 null」可能只是「什么都没上报」。"
            + $"实际上报：{Describe(seen)}");

        seen.Should().OnlyContain(
            d => d.DesktopIdleMs == null,
            "旧托盘不认识这条查询 ⇒ 这个字段只能是 null（「这台机器不提这件事」）。"
            + "填 0 会把「不知道」画成「桌面刚动过」—— 一句编出来的话。"
            + $"实际上报：{DescribeIdle(seen)}");
    }

    /// <summary>
    /// <b><c>0</c> 是一个值</b>：桌面刚动过时要原样传出去，不许被当成"没有"。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这是 <c>null</c> 与 <c>0</c> 的<b>另一侧</b>。只钉一侧是不够的：
    /// 把整条链路的判据写成 <c>if (idle &gt; 0) 更新</c> 会让这一侧静默失效
    /// （画面明明在动，页面却一直显示上次那个"已静止 12 秒"，直到会话结束）。
    /// </remarks>
    [Fact]
    public async Task Zero_idle_is_a_value_not_a_missing_one()
    {
        var reports = new List<AgentRemoteSessionStateDto>();

        await using var coordinator = NewCoordinator(reports, desktopIdleMs: 0);

        (await coordinator.ExecuteAsync(StartCommand("idle-zero"), "Remote", CancellationToken.None))
            .Outcome.Should().Be(AgentReceivedOutcomes.Executed);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline && !Snapshot(reports).Any(d => d.DesktopIdleMs == 0))
        {
            await Task.Delay(50);
        }

        var matched = Snapshot(reports).FirstOrDefault(d => d.DesktopIdleMs == 0);

        matched.Should().NotBeNull(
            "桌面刚动过（0 毫秒）是一个**答案**，不是「这条上报没提这件事」；"
            + $"实际上报：{DescribeIdle(Snapshot(reports))}");

        matched!.DesktopIdleMs.Should().NotBeNull(
            "它必须是「有值且为 0」，而不是可空类型里的 null —— 两者的页面文案不同");
    }

    // ═══════════════════════════════════════════════════════════
    // 夹具
    // ═══════════════════════════════════════════════════════════

    /// <summary>带非零计数的 <b>streaming</b> 上报 —— 也就是"页面那格能拿到数"的那一种。</summary>
    private static bool IsLiveCounted(AgentRemoteSessionStateDto dto)
        => dto.State == RemoteSessionStates.Streaming && dto.Frames > 0;

    /// <summary>
    /// 取一份上报的快照。
    /// <para>
    /// ⚠️ <b>必须加锁复制</b>：上报是别的线程写进来的（`StateSink` 可能在接流泵那条任务上跑），
    /// 直接 `foreach` 会撞上"集合已修改"。
    /// </para>
    /// </summary>
    private static List<AgentRemoteSessionStateDto> Snapshot(List<AgentRemoteSessionStateDto> reports)
    {
        lock (reports) return new List<AgentRemoteSessionStateDto>(reports);
    }

    private static string Describe(IReadOnlyList<AgentRemoteSessionStateDto> reports)
        => reports.Count == 0
            ? "(一条都没有)"
            : string.Join(" / ", reports.Select(d => $"{d.State}:{d.Frames}帧"));

    /// <summary>只描述"桌面静止时长"这一个个字段（失败消息里要能一眼看出它是 null 还是 0）。</summary>
    private static string DescribeIdle(IReadOnlyList<AgentRemoteSessionStateDto> reports)
        => reports.Count == 0
            ? "(一条都没有)"
            : string.Join(" / ", reports.Select(d => $"{d.State}:{(d.DesktopIdleMs is { } ms ? $"{ms}ms" : "null")}"));

    private static string StartCommand(string sessionId)
        => JsonSerializer.Serialize(new RemoteSessionCommandDto(RemoteSessionActions.Start, sessionId, MaxFps: 30));

    private static string StopCommand(string sessionId)
        => JsonSerializer.Serialize(new RemoteSessionCommandDto(RemoteSessionActions.Stop, sessionId));

    private RemoteSessionCoordinator NewCoordinator(
        List<AgentRemoteSessionStateDto> reports,
        long? desktopIdleMs = null,
        bool answersDesktopIdle = true)
    {
        var options = new AgentOptions { StateDirectory = _dir.Root };

        var coordinator = new RemoteSessionCoordinator(
            session: new StubCommandChannel
            {
                DesktopIdleMs = desktopIdleMs,
                AnswersDesktopIdle = answersDesktopIdle,
            },
            stream: new StubStreamChannel(),
            input: new UnavailableSessionInputChannel("测试：不接输入通道"),
            audio: new UnavailableSessionAudioChannel("测试：不接音频通道"),
            inputPolicy: new AgentInputPolicy(options, _log),
            log: _log);

        // ⚠️ 三个出口都要给：中心不在（为 null）时有两条路径会**故意抛异常**结束这一路，
        //    那在这个用例里是噪音，会让"没上报"与"流被自己掐了"混成同一个现象。
        coordinator.FrameSink = (_, _) => Task.CompletedTask;
        coordinator.AudioSink = (_, _) => Task.CompletedTask;

        coordinator.StateSink = dto =>
        {
            lock (reports) reports.Add(dto);
            return Task.CompletedTask;
        };

        return coordinator;
    }

    /// <summary>
    /// 指令通道桩：除"问桌面静止多久"之外，一律成功（这组用例要的是上报内容，不是托盘真的接上了）。
    ///
    /// <para>
    /// 🔥 <b>它必须真的解析 <c>Argument</c> 里的动作名</b>，不能对所有请求都回同一个答案：
    /// 那样"桌面静止时长"那条链路上任何一环断了（服务没问、动作名写错、托盘答的不认）
    /// 都会<b>静默绿</b> —— 因为桩对什么都点头。
    /// </para>
    /// </summary>
    private sealed class StubCommandChannel : ISessionCommandChannel
    {
        /// <summary>托盘要报的"静止了多久"；<c>null</c> = 答不出这个数。</summary>
        public long? DesktopIdleMs { get; init; }

        /// <summary>
        /// 托盘<b>认不认</b>这条查询。<c>false</c> = 旧版本托盘（回一句"不认识的远控动作"）。
        /// </summary>
        public bool AnswersDesktopIdle { get; init; } = true;

        public bool IsSupported => true;

        public bool IsAvailable => true;

        public Task<SessionCommandResult> SendAsync(
            SessionCommandRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Method == SessionCommandMethods.RemoteStream && request.Argument is not null)
            {
                var parsed = JsonSerializer.Deserialize<RemoteStreamRequest>(
                    request.Argument,
                    SessionPipeProtocol.Json);

                if (parsed?.Action == RemoteSessionActions.DesktopIdle)
                {
                    return Task.FromResult(AnswersDesktopIdle
                        ? SessionCommandResult.Success(null, null, DesktopIdleMs)

                        // 与托盘那句原文同形（那句会原样进服务侧日志）。
                        : SessionCommandResult.Failure(
                            "不认识的远控动作「desktop-idle」（服务与托盘版本可能不一致）。"));
                }
            }

            return Task.FromResult(SessionCommandResult.Success("测试：已受理", null));
        }
    }

    /// <summary>
    /// 接流通道桩：<b>一直以 50 ms 一帧地喂</b>，直到被取消。
    /// <para>
    /// ⚠️ 帧必须**真的按时间喂**（不是全速塞）：这一组用例全部建立在
    /// "推流持续了一秒以上"之上 —— 全速塞会在几毫秒内灌完，周期上报还没来得及跑第二次。
    /// </para>
    /// </summary>
    private sealed class StubStreamChannel : ISessionStreamChannel
    {
        public bool IsSupported => true;

        public async Task<RemoteStreamOutcome> RunAsync(
            string sessionId,
            Func<RemoteStreamFrame, CancellationToken, Task> onFrame,
            CancellationToken cancellationToken)
        {
            long sequence = 0;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);

                    sequence++;

                    var header = new RemoteStreamFrameHeader(
                        SessionId: sessionId,
                        Sequence: sequence,
                        Codec: RemoteCodecs.Mjpeg,
                        IsKeyframe: sequence == 1,
                        Width: 2,
                        Height: 2,
                        CapturedUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                    await onFrame(new RemoteStreamFrame(header, new byte[8]), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常收场。
            }

            return new RemoteStreamOutcome(true, StreamStopsMessage, sequence, sequence * 8, 0);
        }
    }
}
