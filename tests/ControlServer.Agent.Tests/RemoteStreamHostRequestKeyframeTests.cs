using System.IO.Pipes;
using ControlServer.Agent.Tasks;
using ControlServer.Contracts;
using ControlServer.Platform.Abstractions;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// <b>「请补一个关键帧」这条回传链路在托盘这一侧的落点</b>（M5.4-c① 步 5）。
///
/// <para>
/// 🔥🔴 <b>它守的是一个「协议已经通了、行为还没通」的状态。</b>中心重组分片失败时
/// 早就在 <c>AgentIngestAckDto</c> 里带回了 <c>NeedKeyframe = true</c>
/// （步 2 就做了），而<b>被控端没有任何一行读它</b> ⇒ 中心丢一帧之后，
/// 观看端要花/冻<b>最多一个 GOP</b>（<c>GopSize 60</c> / 12 fps ⇒ <b>约 5 秒</b>）。
/// 补一个关键帧把这段压到<b>下一帧</b>。
/// </para>
///
/// <para>
/// ⚠️ <b>为什么这组用例非要用真的命名管道 + 真的推流泵</b>：要证的正是
/// "<b>请求置位之后，泵的下一轮真的把它变成了关键帧</b>" —— 那是<b>循环结构里的性质</b>，
/// 纯函数测不出来（与 <c>RemoteStreamOversizeTests</c> 同一条理由）。
/// </para>
///
/// <para>
/// 🔸 <b>本文件的编码器刻意"不响应第 0 帧"</b>（<c>IsKeyframe == forceKeyframe</c>，
/// 不是 <c>index == 0 || forceKeyframe</c>）：否则第一帧天生就是关键帧，
/// "请求之后才出关键帧"这件事<b>在观测上就消失了</b> —— 用例会永远绿。
/// </para>
/// </summary>
public sealed class RemoteStreamHostRequestKeyframeTests
{
    /// <summary>
    /// <b>没有正在推流的会话 ⇒ 失败，而且那句原因要说人话。</b>
    ///
    /// <para>
    /// ⚠️ 这条是<b>守卫用例</b>（一开始就该绿）。它为什么值得单独立一条：
    /// "静默成功"会让"中心一直要关键帧、画面一直不动"变成无法定位的现场 ——
    /// 而这两种情形在日志里唯一的差别就是这句话。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_a_request_without_a_stream_fails_with_a_reason()
    {
        var logs = new List<string>();

        await using var host = NewHost(new CountingEncoder(), logs);

        var result = host.RequestKeyframe();

        result.Ok.Should().BeFalse("没有流就没有\"下一帧\"，请求无处安放");
        result.Message.Should().Contain(
            "没有正在推流",
            "这句会原样进服务侧日志 —— 它必须指向真因，而不是一句「失败」");
    }

    /// <summary>
    /// 🔥 <b>核心用例</b>：请求之后，<b>泵真的把下一帧做成了关键帧</b>。
    /// <para>
    /// 基线先读两帧并断言"都是普通帧"：没有这一步，后面那个"出现了关键帧"的断言
    /// 可能只是"编码器天生就出关键帧"，用例会变成一条永远绿的假证据。
    /// </para>
    /// </summary>
    [Fact]
    public async Task B_a_requested_keyframe_shows_up_on_a_following_frame()
    {
        var sessionId = "keyframe-" + Guid.NewGuid().ToString("N")[..8];
        var logs = new List<string>();
        var encoder = new CountingEncoder();

        await using var host = NewHost(encoder, logs);

        var started = host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 60));

        started.Ok.Should().BeTrue($"开始推流本该成功，失败原因：{started.Message}");

        // 管道由**本用例**建、由**本用例**释放（与 RemoteStreamOversizeTests 同一个坑：
        // 让读取方自己 `await using` 会在"读够了"与"断言"之间插进一次断开）。
        await using var client = new NamedPipeClientStream(
            ".",
            RemoteStreamProtocol.PipeNameForSession(sessionId),
            PipeDirection.In,
            PipeOptions.Asynchronous);

        // ── 基线：请求之前都是普通帧 ──
        var before = await ReadFramesAsync(client, want: 2, timeoutMs: 6000);

        before.Should().HaveCount(2, $"基线要读得到帧，日志：{string.Join(" / ", logs)}");

        before.Should().OnlyContain(
            f => !f.Header.IsKeyframe,
            "基线必须全是普通帧 —— 否则\"请求之后出现关键帧\"这件事在观测上消失了");

        // ── 请求 ──
        var requested = host.RequestKeyframe();

        requested.Ok.Should().BeTrue($"有流在跑，请求本该成功，实际：{requested.Message}");

        // ── 之后：最多再有一帧（请求那一刻已经在编的那一帧）不是关键帧 ──
        var after = await ReadFramesAsync(client, want: 3, timeoutMs: 6000);

        after.Should().HaveCount(3, $"请求之后还要读得到帧，日志：{string.Join(" / ", logs)}");

        after.Should().Contain(
            f => f.Header.IsKeyframe,
            "请求的**全部意义**就是让下一帧成为关键帧 —— 三帧里一个都没有，说明请求没被泵消费");

        encoder.ForceKeyframeFlags.Should().Contain(
            true,
            "编码器必须真的收到 forceKeyframe=true；只看帧头里的 IsKeyframe 不够 —— "
            + "那是编码器自己的判断，而这里要证的是**我们有没有开口要**");
    }

    /// <summary>
    /// <b>请求是"一次性"的：补上一个关键帧之后就回到正常节奏。</b>
    ///
    /// <para>
    /// 🔥 这一条钉的不是"能不能补"，而是"<b>会不会补过头</b>"：把标志写成
    /// "置位后一直留着"或者"给每帧都加一次请求"，表现是<b>每一帧都是关键帧</b> ——
    /// 画面不花，但码率会翻好几倍、帧率掉下来，而现场只会看到"推流很卡"。
    /// 8K 的 IDR 正是整条链路上最贵的东西，这件事必须被钉住。
    /// </para>
    /// </summary>
    [Fact]
    public async Task C_the_request_is_consumed_once_not_kept_forever()
    {
        var sessionId = "keyframe-once-" + Guid.NewGuid().ToString("N")[..8];
        var logs = new List<string>();
        var encoder = new CountingEncoder();

        await using var host = NewHost(encoder, logs);

        var started = host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 60));

        started.Ok.Should().BeTrue($"开始推流本该成功，失败原因：{started.Message}");

        await using var client = new NamedPipeClientStream(
            ".",
            RemoteStreamProtocol.PipeNameForSession(sessionId),
            PipeDirection.In,
            PipeOptions.Asynchronous);

        (await ReadFramesAsync(client, want: 2, timeoutMs: 6000)).Should().HaveCount(2);

        host.RequestKeyframe().Ok.Should().BeTrue();

        // 读够一段：足够看见"补的那个关键帧"以及之后的若干普通帧。
        var after = await ReadFramesAsync(client, want: 8, timeoutMs: 6000);

        after.Should().HaveCount(8, $"日志：{string.Join(" / ", logs)}");

        after.Count(f => f.Header.IsKeyframe).Should().Be(
            1,
            "一次请求只该换来**一个**关键帧；补过头会让码率翻倍而画面看不出问题");

        encoder.ForceKeyframeFlags.Count(flag => flag).Should().Be(
            1,
            "「开口要了几次」比「帧头里出现了几个关键帧」更直接 —— 编码器只该被要求一次");
    }

    /// <summary>
    /// <b>补上之后还能再要一次。</b>
    ///
    /// <para>
    /// ⚠️ 与上一条是一对：上一条防"补过头"，这一条防"补一次就再也不补"。
    /// 后者在"中心连续丢帧"的现场是致命的 —— 第一帧补上了，之后每一帧又都是花的，
    /// 而请求再也进不来。
    /// </para>
    /// </summary>
    [Fact]
    public async Task D_a_later_request_is_honoured_again()
    {
        var sessionId = "keyframe-again-" + Guid.NewGuid().ToString("N")[..8];
        var logs = new List<string>();
        var encoder = new CountingEncoder();

        await using var host = NewHost(encoder, logs);

        var started = host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 60));

        started.Ok.Should().BeTrue($"开始推流本该成功，失败原因：{started.Message}");

        await using var client = new NamedPipeClientStream(
            ".",
            RemoteStreamProtocol.PipeNameForSession(sessionId),
            PipeDirection.In,
            PipeOptions.Asynchronous);

        (await ReadFramesAsync(client, want: 2, timeoutMs: 6000)).Should().HaveCount(2);

        host.RequestKeyframe().Ok.Should().BeTrue();

        (await ReadFramesAsync(client, want: 4, timeoutMs: 6000)).Should().HaveCount(4);

        // ── 第二次请求（模拟中心**又**丢了一帧）──
        host.RequestKeyframe().Ok.Should().BeTrue();

        (await ReadFramesAsync(client, want: 4, timeoutMs: 6000)).Should().HaveCount(4);

        encoder.ForceKeyframeFlags.Count(flag => flag).Should().Be(
            2,
            "两次请求该换来两个关键帧 —— 第二个请求被吞掉，意味着第一次丢帧恢复之后的情况无法恢复");
    }

    /// <summary>
    /// 🔥 <b>编码器没照办时，请求不许被吞掉。</b>
    ///
    /// <para>
    /// 🔴 「要求关键帧」是<b>请求</b>不是<b>命令</b>：编码器完全可能这一帧给不出 IDR
    /// （忙、GPU 正在重建、参数集还没就绪、分辨率刚变）。如果"成功发出去一帧"就
    /// 把需求清掉，那这次请求就<b>永久消失在空气里</b> —— 观看端继续花，
    /// 而两边日志都写着"请求已送出、帧也发出去了"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 正确的判据是<b>"真的发出了一个关键帧"</b>，而不是"发出了一个帧"。
    /// 这与同一段代码里 <c>_oversizeKeyframeStreak</c> 只在
    /// <c>payload.IsKeyframe</c> 时才清零是同一条纪律。
    /// </para>
    /// </summary>
    [Fact]
    public async Task E_a_keyframe_the_encoder_ignored_is_asked_for_again()
    {
        var sessionId = "keyframe-ignored-" + Guid.NewGuid().ToString("N")[..8];
        var logs = new List<string>();

        // 前 6 次调用即使被要求也出普通帧 —— 模拟"编码器这一阵子给不出 IDR"。
        var encoder = new CountingEncoder(ignoreForceKeyframeUntilCall: 6);

        await using var host = NewHost(encoder, logs);

        var started = host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 60));

        started.Ok.Should().BeTrue($"开始推流本该成功，失败原因：{started.Message}");

        await using var client = new NamedPipeClientStream(
            ".",
            RemoteStreamProtocol.PipeNameForSession(sessionId),
            PipeDirection.In,
            PipeOptions.Asynchronous);

        (await ReadFramesAsync(client, want: 2, timeoutMs: 6000)).Should().HaveCount(2);

        host.RequestKeyframe().Ok.Should().BeTrue();

        var after = await ReadFramesAsync(client, want: 8, timeoutMs: 6000);

        after.Should().HaveCount(8, $"日志：{string.Join(" / ", logs)}");

        after.Should().Contain(
            f => f.Header.IsKeyframe,
            "编码器最终照办了 ⇒ 必须真的出现一个关键帧（否则说明请求被那一阵子吞掉了）");

        encoder.ForceKeyframeFlags.Count(flag => flag).Should().BeGreaterThan(
            1,
            "被忽略之后必须**继续要**；只问一次就完事，等于把一次请求交给运气");
    }

    // ═══════════════════════════════════════════════════════════
    // 夹具
    // ═══════════════════════════════════════════════════════════
    private static RemoteStreamHost NewHost(CountingEncoder encoder, List<string> logs)
        => new(
            capturers: () => new MinimalCapturer(),
            encoders: _ => encoder,
            injector: null,
            inputAllowed: () => false,
            log: line =>
            {
                lock (logs) logs.Add(line);
            },

            // ⚠️ 音频工厂传 null：让托盘连音频管道都不建（否则会真采系统声音，
            //    与这里要证的东西毫无关系，还会在没声卡的机器上变成一条假 FAIL）。
            audioCapturers: null,
            audioEncoders: null);

    private static async Task<List<RemoteStreamFrame>> ReadFramesAsync(
        NamedPipeClientStream client,
        int want,
        int timeoutMs)
    {
        var frames = new List<RemoteStreamFrame>();

        using var cancel = new CancellationTokenSource(timeoutMs);

        try
        {
            // ⚠️ 同一根管道会被**多次**调用着读（"先基线两帧、再补三帧"），
            //    而 `ConnectAsync` 在已连上时会抛 `Already in a connected state`
            //    —— 那是本夹具自己的坑（第一次跑就撞上了），不是被测代码的问题。
            if (!client.IsConnected) await client.ConnectAsync(timeoutMs, cancel.Token);

            while (frames.Count < want && !cancel.IsCancellationRequested)
            {
                var frame = await RemoteStreamProtocol.ReadFrameAsync(client, cancel.Token);
                if (frame is null) break;

                frames.Add(frame);
            }
        }
        catch (OperationCanceledException)
        {
            // 读够了或超时 —— 都交给断言去说。
        }

        return frames;
    }

    /// <summary>最小抓屏器：给一张 2×2 的图，<b>不碰任何平台 API</b>。</summary>
    private sealed class MinimalCapturer : IScreenCapturer
    {
        private static readonly ScreenDescriptor Screen =
            new("screen-1", "测试屏", 2, 2, IsPrimary: true);

        private int _tick;

        public IReadOnlyList<ScreenDescriptor> EnumerateScreens() => new[] { Screen };

        public Task<CapturedFrame> CaptureAsync(string screenId, CancellationToken cancellationToken = default)
        {
            // 🔥 **每帧的像素必须不同**：M5.5 起软编路径有静止检测（`FrameFingerprint`），
            //    像素与上一帧完全相同的帧会被**正确地**跳过 —— 那正是它该做的事。
            //    恒返 `new byte[16]` 会让本文件所有"等 N 帧"的断言在第二帧就卡死，
            //    而且症状会伪装成"推流不工作"，指向完全不同的问题。
            var buffer = new byte[16];

            BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), Interlocked.Increment(ref _tick));

            return Task.FromResult(new CapturedFrame(2, 2, 8, buffer, DateTimeOffset.UtcNow));
        }

        public Task<CapturedSurface?> CaptureSurfaceAsync(
            string screenId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<CapturedSurface?>(null);
    }

    /// <summary>
    /// <b>只在被要求时才出关键帧</b>的编码器 —— 与 <c>RemoteStreamOversizeTests</c>
    /// 里那个 <c>ScriptedEncoder</c> 的差别只有一处，而那一处正是本文件的关键：
    /// 它<b>不</b>把第 0 帧当成关键帧（<c>IsKeyframe == forceKeyframe</c>）。
    /// </summary>
    private sealed class CountingEncoder : IFrameEncoder
    {
        private static readonly byte[] Payload = new byte[1];

        /// <summary>在这之前（<b>不含</b>）的每一次调用，即使被要求也不出关键帧。</summary>
        private readonly int _ignoreForceKeyframeUntilCall;

        private int _calls;

        public CountingEncoder(int ignoreForceKeyframeUntilCall = 0)
        {
            _ignoreForceKeyframeUntilCall = ignoreForceKeyframeUntilCall;
        }

        /// <summary>每次 <see cref="TryEncode"/> 收到的 <c>forceKeyframe</c>，按调用顺序。</summary>
        public List<bool> ForceKeyframeFlags { get; } = new();

        public string Codec => RemoteCodecs.Mjpeg;

        public string? LastFailure => null;

        public long DiscardedFrames => 0;

        public bool Grayscale { get; set; }

        public bool TryEncode(CapturedFrame frame, bool forceKeyframe, out EncodedStreamPayload? encoded)
        {
            var index = _calls++;

            lock (ForceKeyframeFlags) ForceKeyframeFlags.Add(forceKeyframe);

            encoded = new EncodedStreamPayload(
                Codec: RemoteCodecs.Mjpeg,

                // 🔥 如实响应 forceKeyframe（**没有** `index == 0` 那个特例，
                //    否则第一帧天生就是关键帧，"请求之后才出关键帧"就看不见了），
                //    但前几次可以"不照办" —— 那是真实编码器会有的行为。
                IsKeyframe: forceKeyframe && index >= _ignoreForceKeyframeUntilCall,
                Width: 2,
                Height: 2,
                CapturedUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data: Payload);

            return true;
        }

        public void Dispose()
        {
        }
    }
}
