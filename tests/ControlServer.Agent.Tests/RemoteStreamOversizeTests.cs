using System.IO.Pipes;
using ControlServer.Agent.Tasks;
using ControlServer.Contracts;
using ControlServer.Platform.Abstractions;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// <b>单帧超过管道上限时的处置</b>（M5.4-b / 方案 §3.6）。
///
/// <para>
/// 🔥🔴 <b>这组用例守的是一个「症状指向相反」的老毛病。</b>改之前：
/// <c>WriteFrameAsync</c> 对超限帧<b>抛异常</b>，而推流循环把<b>任何</b>写异常
/// 都当成「接收端已断开」⇒ <c>return</c>，<b>整条流结束</b>。
/// 于是现场看到的是「<b>码流写不出去，接收端已断开</b>」——
/// 而接收端好端端地在等着，真因只是<b>某一帧比上限大了几十 KB</b>。
/// </para>
///
/// <para>
/// ⚠️ 这条错误分类的代价之所以大，是因为它<b>把一次丢帧升级成了一次掉线</b>：
/// 丢一帧是「画面顿一下」，掉一条流是「要人工重新点一次开始」。
/// 两者在日志里长得几乎一样，而处置差了一个数量级。
/// </para>
///
/// <para>
/// 🔸 本文件用<b>真的命名管道</b>跑完整流程（托盘侧推、测试侧读），
/// 而不是只测一个纯函数：因为要证的正是「<b>循环没有因为这一帧而退出</b>」——
/// 那是循环结构里的性质，纯函数测不出来。
/// </para>
/// </summary>
public sealed class RemoteStreamOversizeTests
{
    /// <summary>
    /// 🔥 <b>核心用例</b>：一帧超限之后，<b>流还在、而且下一帧被强制成了关键帧</b>。
    /// </summary>
    [Fact]
    public async Task An_oversize_frame_is_dropped_without_ending_the_stream()
    {
        var sessionId = "oversize-" + Guid.NewGuid().ToString("N")[..8];
        var logs = new List<string>();

        // 第 1 次编码（下标 0）产出超限帧；之后都给正常大小。
        var encoder = new ScriptedEncoder(oversizeAtCall: 0);

        await using var host = NewHost(encoder, logs);

        var started = host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 60));

        started.Ok.Should().BeTrue($"开始推流本该成功，失败原因：{started.Message}");

        // 🔥 **管道要一直连到断言结束**（10-16 修的一条假 FAIL）：
        //    原来这里由 `ReadFramesAsync` 自己 `await using` 客户端并**在读完 want 帧时释放**，
        //    于是"读够 4 帧"与"下面那几条断言"之间隔了一次**断开**——
        //    服务端下一次写就会失败并如实记一句「接收端已断开」，而 `IsStreaming` 也会跟着变假。
        //    表现是本用例**取决于调度**：单独跑绿、整个程序集一起跑红（xunit 并行改了时序）。
        //    修法不是放宽断言，而是**别在断言之前把管子拔掉**：客户端在这里建、在这里释放。
        await using var client = new NamedPipeClientStream(
            ".",
            RemoteStreamProtocol.PipeNameForSession(sessionId),
            PipeDirection.In,
            PipeOptions.Asynchronous);

        var received = await ReadFramesAsync(client, want: 4, timeoutMs: 6000);

        // ── ① 流没断：超限那一帧之后还收得到帧 ──
        received.Should().HaveCountGreaterThanOrEqualTo(
            3,
            "超限的那一帧必须只被「丢掉」。收到少于 3 帧说明循环停了 —— 那正是这条改动要修的东西；"
            + $"日志：{string.Join(" / ", logs)}");

        // ── ② 序号**不该**出现空洞，丢帧的证据在 `Dropped` 里 ──
        //
        // 🔥 这一条是**实测纠正了一个想当然**：本来以为"丢一帧会在序号上留一个洞"，
        //    而实测 `Sequence == 1` 是连续的 —— 因为序号是在**构造帧头时**才取的，
        //    而超限帧根本走不到那里。
        //
        //    这就是**对的**语义，不该反过来改它：序号回答的是「真的发出去了第几帧」，
        //    而"有没有丢"要问 `Dropped`（它随每一帧带到中心、再显示到页面上）。
        //    一个没人消费的空洞只是噪音；真正该钉住的是**丢帧信息有没有跟着帧走**。
        received[0].Header.Sequence.Should().Be(
            1,
            "序号是「真的发出去了第几帧」的计数 —— 被丢掉的帧不该把它撑出空洞。"
            + "要表达丢帧请用 Dropped 那一个字段");

        received[0].Header.Dropped.Should().BeGreaterThanOrEqualTo(
            1,
            "丢帧的证据必须在帧头里跟着走：中心看不到被控端本地发生的事，"
            + "这是「画面为什么卡」唯一能穿过来的通道");

        // ── ③ 丢帧之后立刻补关键帧 ──
        //
        // 🔥 这一条是「H.264 的帧不能像 MJPEG 那样自由丢」的落点：
        //    P 帧参考前面的帧，丢一帧就在参考链上戳一个洞，此后观看端解出来的每一帧都是花的
        //    —— 而**解码器不会报错**。所以丢帧必须配一次「立刻补一个关键帧」，
        //    把恢复代价从「最多一个 GOP（默认 60 帧）」压到「下一帧」。
        received[0].Header.IsKeyframe.Should().BeTrue(
            "丢掉一帧之后，下一帧必须是关键帧 —— 否则观看端拿不回参考帧，画面会一直花");

        encoder.ForceKeyframeFlags.Should().HaveCountGreaterThanOrEqualTo(2);

        encoder.ForceKeyframeFlags[0].Should().BeFalse("第 1 帧是正常编码，没有理由强制关键帧");

        encoder.ForceKeyframeFlags[1].Should().BeTrue(
            "第 2 帧必须带着强制关键帧的旗标 —— 这正是「丢一帧、补一个关键帧」那条规矩");

        // ── ④ 日志说人话：不是「接收端已断开」 ──
        logs.Should().NotContain(
            line => line.Contains("接收端已断开", StringComparison.Ordinal),
            "接收端**一直在** —— 把这句写进日志会把排查引向网络，而真因是帧尺寸");

        logs.Should().Contain(
            line => line.Contains("超过管道上限", StringComparison.Ordinal),
            "超限必须被说出来，而且要说清是**多大**超过了**多少**");

        // ── ⑤ 计数说得清是「哪一类丢帧」 ──
        var status = host.Status;

        status.Should().NotBeNull();

        status!.DroppedFrames.Should().BeGreaterThanOrEqualTo(
            1,
            "累计丢帧要把这一帧算进去 —— 它是「画面为什么卡」唯一能穿过来的证据");

        host.IsStreaming.Should().BeTrue("这一路流应当还在推");
    }

    /// <summary>
    /// 🔥 <b>但「无限重试一件不可能的事」要有个头。</b>
    /// 关键帧本身就一直超限时，「再要一个关键帧」只会得到同样大的关键帧 ——
    /// 那是一个<b>永远不会成功的循环</b>：被控机一直抓、一直编、一直丢，
    /// 观看端什么都没有，而两边计数都「正常」。
    ///
    /// <para>
    /// 所以连续 <c>OversizeKeyframeGiveUp</c> 次之后必须<b>如实收场</b>，
    /// 并且把「这个分辨率传不过去、要么降分辨率要么等分片」写进原因里
    /// （<c>LastError</c> 会随状态上报回中心并显示在页面上）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_keyframe_that_never_fits_gives_up_with_a_reason_instead_of_spinning()
    {
        var sessionId = "oversize-cannot-" + Guid.NewGuid().ToString("N")[..8];
        var logs = new List<string>();

        // **每一次**都产出超限的关键帧 —— 模拟「这个分辨率根本传不过去」。
        var encoder = new ScriptedEncoder(oversizeAtCall: null, alwaysOversizeKeyframe: true);

        await using var host = NewHost(encoder, logs);

        var started = host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 60));

        started.Ok.Should().BeTrue($"开始推流本该成功，失败原因：{started.Message}");

        // 一帧都读不到：关键帧根本发不出去（发出来的只有参考不上的 P 帧），对面随后关掉管道。
        //
        // 🔥 与上面那个用例同一个道理（10-16 修假 FAIL 时一并改的）：
        //    管道由**本用例**建、由**本用例**释放，而不是交给 `ReadFramesAsync` 去 `await using`
        //    —— 否则"读够了"与"下面的断言"之间会插进一次**断开**，
        //    而那段断开恰好会让服务端下一次写失败并如实记一句「接收端已断开」。
        //    本用例要证的是「收场的原因是**我们自己**判定这个分辨率发不出去」，
        //    所以那句日志必须**没有**出现过，而它的存在与否不该取决于调度。
        await using var client = new NamedPipeClientStream(
            ".",
            RemoteStreamProtocol.PipeNameForSession(sessionId),
            PipeDirection.In,
            PipeOptions.Asynchronous);

        var received = await ReadFramesAsync(client, want: 1, timeoutMs: 6000);

        received.Should().BeEmpty("关键帧根本发不出去 ⇒ 观看端不可能收到任何东西");

        // 循环必须**自己收场**，而不是一直在那儿丢。
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (DateTime.UtcNow < deadline
               && host.Status?.LastError?.Contains("传不过去", StringComparison.Ordinal) is not true)
        {
            await Task.Delay(50);
        }

        host.Status!.LastError.Should().Contain(
            "传不过去",
            "连续几个关键帧都超限时必须如实说「这个分辨率传不过去」，并把两个数字并排给出来；"
            + $"日志：{string.Join(" / ", logs)}");

        host.Status.LastError.Should().Contain(
            "上限",
            "只说「太大」说不清差多少 —— 两个数（这一帧多大 / 上限多大）都要在那一句里");

        logs.Should().NotContain(
            line => line.Contains("接收端已断开", StringComparison.Ordinal),
            "这仍然不是「接收端没了」—— 收场的原因是**我们自己**判定这个分辨率发不出去");
    }

    /// <summary>
    /// 托盘侧与管道侧的尺寸判断<b>必须是同一个数</b>。
    ///
    /// <para>
    /// 🔥 两者一旦漂开，方向只有一种：<b>托盘以为能发、管道却判它超限</b> ——
    /// 而那正好落进推流循环里那个兜底 <c>catch</c>，让「预检」这件事变成摆设。
    /// 反方向（管道比预检宽松）则会白丢帧。
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_pre_check_and_the_writer_agree_on_the_limit()
    {
        var limit = RemoteStreamProtocol.MaxPayloadBytes;

        RemoteStreamProtocol.FitsInPipe(limit).Should().BeTrue("正好等于上限是**装得下**的");
        RemoteStreamProtocol.FitsInPipe(limit + 1).Should().BeFalse();

        var header = new RemoteStreamFrameHeader("s", 1, RemoteCodecs.Mjpeg, true, 2, 2, 0);

        await using (var stream = new MemoryStream())
        {
            var bytes = await RemoteStreamProtocol.WriteFrameAsync(
                stream, header, new byte[limit], CancellationToken.None);

            bytes.Should().BeGreaterThan(limit, "正好等于上限的那一帧必须写得进去");
        }

        await using (var stream = new MemoryStream())
        {
            var act = async () => await RemoteStreamProtocol.WriteFrameAsync(
                stream, header, new byte[limit + 1], CancellationToken.None);

            await act.Should().ThrowAsync<FrameTooLargeException>(
                "预检说不装就不许真发 —— 两边用同一个常量才不会自相矛盾");
        }
    }

    /// <summary>
    /// 🔴 <b>「太大」与「对面没了」必须是两个<b>抓得住</b>的类型。</b>
    ///
    /// <para>
    /// 判据刻意写成「<b>不是</b>那些 IO 异常」，而不是「是我这个类型」：
    /// 这一条真正要挡的是「有人为了省事让它继承 <see cref="IOException"/>（或反过来）」——
    /// 那样写 <c>catch (IOException)</c> 的旧代码会把它<b>静默地</b>归类成链路断了，
    /// 而静默误分类正是这个 bug 的全部内容。
    /// </para>
    /// </summary>
    [Fact]
    public void Frame_too_large_is_not_an_io_failure()
    {
        var ex = new FrameTooLargeException(RemoteStreamProtocol.MaxPayloadBytes + 4096);

        ex.Should().NotBeAssignableTo<IOException>(
            "它是「这一帧装不下」，不是「管道坏了」—— 继承 IO 异常就等于允许别人把它当成掉线处理");
        ex.Should().NotBeAssignableTo<EndOfStreamException>("对面没有结束，它只是不收这么大的东西");
        ex.Should().NotBeAssignableTo<OperationCanceledException>("会话没有被取消");

        ex.PayloadBytes.Should().Be(RemoteStreamProtocol.MaxPayloadBytes + 4096);
        ex.LimitBytes.Should().Be(RemoteStreamProtocol.MaxPayloadBytes);

        ex.Message.Should().Contain("丢弃", "错误信息要说清**该怎么办** —— 处置是丢这一帧，不是结束这条流");
    }

    /// <summary>
    /// 尺寸上限的几条<b>推导关系</b>（不是数字本身）。
    ///
    /// <para>
    /// 🔥 为什么钉推导而不钉数字：数字会随机型与批次调（512 KB → 8 MB → 分片），
    /// 而这几条关系<b>一旦破了就是错的</b>，与具体取值无关：
    /// </para>
    /// <list type="number">
    ///   <item><b>一片</b>的裸码流上限必须<b>扣掉 base64 的膨胀</b>之后仍小于中心的消息上限
    ///         —— 破了就会让「客户机还在发、中心把<b>整条连接</b>掐了」；</item>
    ///   <item>它必须<b>够 4K 的关键帧用</b> —— 这是 M5.4-b 抬上限的全部理由，
    ///         破了等于白抬（2160×1440 的 IDR 实测 306.9 KB，4K 是它的 2.67 倍像素）；</item>
    ///   <item>🔸 <b>单帧上限必须真的比一片大</b>（M5.4-c①）：两者若相等，
    ///         "分片"就永远切不出第二片 —— 而那种代码在 2160×1440 上跑得<b>完全正常</b>，
    ///         只在 8K 上静默丢帧。这正是这一批要防住的那类"形状错了但不报错"；</item>
    ///   <item>🔸 单帧上限折算出来的片数必须<b>远小于</b>防御性上限
    ///         <see cref="RemoteStreamLimits.MaxChunkCount"/> ——
    ///         否则真帧会被那个"防呆"误伤（丢帧 + 白白要关键帧）。</item>
    /// </list>
    /// </summary>
    [Fact]
    public void The_limits_leave_room_for_base64_for_a_4k_keyframe_and_for_chunking()
    {
        var chunk = RemoteStreamLimits.MaxChunkPayloadBytes;
        var frame = RemoteStreamLimits.MaxFrameBytes;

        // ① base64：JSON 下 byte[] 会变成 4 字符/3 字节的字符串。
        ((long)chunk * 4 / 3).Should().BeLessThan(
            RemoteStreamLimits.SignalRMessageBytes,
            "一片的裸码流上限必须是「扣掉 base64 之后」仍装得进一条 SignalR 消息 —— "
            + "否则界面上看着能发，实际会被中心掐断整条连接（心跳、遥测、音频全在它上面）");

        // ② 4K 的关键帧。
        chunk.Should().BeGreaterThan(
            2 * 1024 * 1024,
            "M5.4-b 抬上限就是为了 4K：2160×1440 的 IDR 实测 306.9 KB，4K 是它的 2.67 倍像素，"
            + "而关键帧还会随内容浮动。低于 2 MB 等于没解决问题");

        // ③ 分片必须**真的能发生**：单帧上限比一片大，才切得出第二片。
        frame.Should().BeGreaterThan(
            chunk,
            "分片的前提就是「一帧可以比一条消息能装的大」—— 两个上限一样大，分片是个死代码");

        RemoteFrameChunker.CountFor(frame).Should().BeGreaterThan(
            1,
            "单帧上限折算出的片数必须大于 1，否则 8K 那一帧照样一片装不下");

        // ④ 片数要能**一眼对得上**：它变了说明两个上限里有一个动了（而两者都有人改的理由）。
        RemoteFrameChunker.CountFor(frame).Should().Be(
            12,
            $"64 MiB / {RemoteStreamLimits.Describe(chunk)} = 11.2 ⇒ 12 片。"
            + "这个数字不是拍出来的，是那两个上限算出来的");

        RemoteFrameChunker.CountFor(frame).Should().BeLessThan(
            RemoteStreamLimits.MaxChunkCount,
            "真帧的片数必须远小于防御性上限 —— 否则真帧会被那个「防呆」误伤（丢帧 + 白白要关键帧）");

        // ⑤ 边界：正好一片 ⇒ 1 片；多 1 字节 ⇒ 2 片；空 ⇒ 0 片。
        RemoteFrameChunker.CountFor(chunk).Should().Be(1, "正好等于一片上限是**一片**（与 FitsInPipe 同一条边界语义）");
        RemoteFrameChunker.CountFor(chunk + 1).Should().Be(2, "多一个字节就必须多一片 —— 差的正是那一个字节");
        RemoteFrameChunker.CountFor(0).Should().Be(0, "空帧不上路（它不是「一片」）");

        RemoteStreamLimits.Describe(chunk).Should().NotBeNullOrWhiteSpace(
            "日志与报错共用同一套单位换算，免得两处写出两种说法");
        RemoteStreamLimits.Describe(frame).Should().NotBeNullOrWhiteSpace();
    }

    // ═══════════════════════════════════════════════════════════
    // 夹具
    // ═══════════════════════════════════════════════════════════

    private static RemoteStreamHost NewHost(ScriptedEncoder encoder, List<string> logs)
        => new(
            capturers: () => new StubCapturer(),
            encoders: _ => encoder,
            injector: null,
            inputAllowed: () => false,
            log: line =>
            {
                lock (logs) logs.Add(line);
            },

            // ⚠️ 音频工厂传 null：让托盘**连音频管道都不建**。
            //    否则这个用例会去真采系统声音，而那与它要证的东西毫无关系，
            //    还会在没声卡的机器上变成一条假 FAIL。
            audioCapturers: null,
            audioEncoders: null);

    /// <summary>
    /// 连上管道并读若干帧。<b>读到够数、或超时、或对面关掉就返回</b> ——
    /// 三种收场都算正常，由用例自己断言「读到了几帧」。
    /// </summary>
    private static async Task<List<RemoteStreamFrame>> ReadFramesAsync(
        NamedPipeClientStream client,
        int want,
        int timeoutMs)
    {
        var frames = new List<RemoteStreamFrame>();

        using var cancel = new CancellationTokenSource(timeoutMs);

        try
        {
            await client.ConnectAsync(timeoutMs, cancel.Token);

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

    /// <summary>最小抓屏器：给一张 2×2 的图，<b>不碰任何平台 API</b>（用例要在非 Windows 上也能编译）。</summary>
    private sealed class StubCapturer : IScreenCapturer
    {
        private static readonly ScreenDescriptor Screen =
            new("screen-1", "测试屏", 2, 2, IsPrimary: true);

        /// <summary>
        /// 每帧写进缓冲区的序号。
        /// <para>
        /// 🔥 <b>为什么帧内容必须每帧都不一样</b>：M5.5 起软编路径有静止检测
        /// （<c>FrameFingerprint</c>），像素与上一帧完全相同的帧会被<b>正确地</b>跳过。
        /// 恒返 <c>new byte[16]</c> 会让本用例从第二帧起一帧都发不出去 ——
        /// 而本用例要验证的是"超限帧被丢弃、流不结束"，不是静止检测。
        /// 用常量全零数组时，这个失败还会伪装成"帧数不够"，指向完全错误的方向。
        /// </para>
        /// </summary>
        private int _tick;

        public IReadOnlyList<ScreenDescriptor> EnumerateScreens() => new[] { Screen };

        public Task<CapturedFrame> CaptureAsync(string screenId, CancellationToken cancellationToken = default)
        {
            var buffer = new byte[16];

            // 四字节递增：回绕要跑四十多亿帧，用例里不可能撞上。
            BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), Interlocked.Increment(ref _tick));

            return Task.FromResult(new CapturedFrame(2, 2, 8, buffer, DateTimeOffset.UtcNow));
        }

        public Task<CapturedSurface?> CaptureSurfaceAsync(
            string screenId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<CapturedSurface?>(null);
    }

    /// <summary>
    /// 按脚本产出帧的编码器：<b>可以指定「第几次调用产出超限帧」，或「每一次都是超限的关键帧」</b>，
    /// 并把每次调用收到的 <c>forceKeyframe</c> 记下来。
    /// </summary>
    private sealed class ScriptedEncoder : IFrameEncoder
    {
        /// <summary>
        /// 超限帧共用的字节缓冲（见 <see cref="TryEncode"/> 里那段说明）——
        /// <b>只分配一次</b>，因为 M5.4-c① 起它是 64 MiB。
        /// </summary>
        private static readonly byte[] OversizePayload =
            new byte[RemoteStreamProtocol.MaxPayloadBytes + 1];

        private readonly int? _oversizeAtCall;
        private readonly bool _alwaysOversizeKeyframe;
        private int _calls;

        public ScriptedEncoder(int? oversizeAtCall, bool alwaysOversizeKeyframe = false)
        {
            _oversizeAtCall = oversizeAtCall;
            _alwaysOversizeKeyframe = alwaysOversizeKeyframe;
        }

        /// <summary>每次 <see cref="TryEncode"/> 收到的 <c>forceKeyframe</c>，按调用顺序。</summary>
        public List<bool> ForceKeyframeFlags { get; } = new();

        public string Codec => RemoteCodecs.Mjpeg;

        public string? LastFailure => null;

        /// <summary>它永远产出码流 ⇒ 一帧都没丢（"没编出来"这条路它不走）。</summary>
        public long DiscardedFrames => 0;

        public bool Grayscale { get; set; }

        public bool TryEncode(CapturedFrame frame, bool forceKeyframe, out EncodedStreamPayload? encoded)
        {
            var index = _calls++;

            ForceKeyframeFlags.Add(forceKeyframe);

            var oversize = _alwaysOversizeKeyframe || index == _oversizeAtCall;

            // ⚠️ **正好比上限多 1 字节**：这条用例要证的边界是「超一点点也算超」。
            //    多给几 MB 只会让失败信息看起来像「差得远」，反而掩盖了边界语义。
            //
            // 🔥 M5.4-c① 起单帧上限抬到 64 MiB（原来的 5.75 MiB 变成了**一片**的上限），
            //    所以超限帧也要跟着大起来 —— 而"每次调用都 new 一个 64 MiB"会让
            //    「永远超限」那条用例（要连试 3 次）白造 192 MiB 垃圾。
            //    这里要证的只是"超限这件事"，内容是什么无关紧要 ⇒ **共用一份**。
            var data = oversize ? OversizePayload : new byte[1];

            encoded = new EncodedStreamPayload(
                Codec: RemoteCodecs.Mjpeg,

                // 关键帧旗标要**如实响应 forceKeyframe**（真编码器就是这么做的）：
                // 不响应的话，"丢一帧、补一个关键帧"那条规矩在用例里就看不见了。
                IsKeyframe: oversize || index == 0 || forceKeyframe,
                Width: 2,
                Height: 2,
                CapturedUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data: data);

            return true;
        }

        public void Dispose()
        {
        }
    }
}
