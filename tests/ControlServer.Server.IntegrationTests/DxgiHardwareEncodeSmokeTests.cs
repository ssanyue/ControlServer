using System.Diagnostics;
using System.Runtime.InteropServices;
using ControlServer.Platform.Abstractions;
using ControlServer.Platform.Windows;
using FluentAssertions;
using Xunit.Abstractions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// M5.4-a 的末端：<b>抓屏 → GPU 转 NV12 → MF 硬编 → 落文件 → 解回来</b>。
///
/// <para>
/// 🔥 <b>为什么最后一定要"解回来"</b>：编码器最擅长的一件事是<b>成功地产出一堆垃圾</b>。
/// 它会把字节写得满满当当、帧率漂亮、日志干净 —— 而画面是花的。
/// 判据只有一条硬标准：<b>把码流喂给解码器 MFT，它得解出帧来，而且尺寸对。</b>
/// 文件非空、字节数合理、<c>TryEncode</c> 返回 <c>true</c> 都不算证据。
/// </para>
///
/// <para>
/// ⚠️ 本文件<b>有硬件前提</b>（要有 H.264/HEVC 硬件编码器 MFT）。
/// 前提不成立时<b>打印原因并返回</b>，不造假 FAIL ——
/// 但"枚举到了却建不起来"是<b>代码/接口</b>的问题，那种情况必须 FAIL。
/// </para>
/// </summary>
[Collection(DesktopDuplicationCollection.Name)]
public sealed class DxgiHardwareEncodeSmokeTests
{
    /// <summary>一次跑多少帧。30 帧足以把"编码器有 lookahead、尾巴要 flush"这件事逼出来。</summary>
    private const int FramesPerPass = 30;

    /// <summary>
    /// 在喂到第几帧时**再要一个关键帧**。
    ///
    /// <para>
    /// 🔥 它存在的唯一理由：回答"M5.4-b 到底该不该把参数集放到 <c>description</c> 里"。
    /// WebCodecs 的 AVC/HEVC 注册里写明，<b>Annex-B 格式下每一个 <c>type: "key"</c> 的块
    /// 都必须自带全套参数集</b>；而本仓的丢帧恢复动作是 <c>reset()</c> + 等下一个关键帧，
    /// 一旦参数集只出现在<b>第一个</b>关键帧里，那个动作就会让解码器手上没有参数集。
    /// ⇒ 所以这里强制第二个关键帧，看它带不带 SPS。
    /// </para>
    /// </summary>
    private const int SecondKeyframeAt = 15;

    /// <summary>
    /// 等 DDA 交第一张画面，最多等多久。
    ///
    /// <para>
    /// 🔥 <b>早先这里是"试 12 次"</b>：12 × <c>DefaultAcquireTimeoutMs(15)</c> ≈ <b>180 ms</b>，
    /// 而 DDA 只在桌面/指针变化时交付帧 —— 180 ms 内没有任何绘制动作是<b>常态</b>，
    /// 于是这条用例会随"那一刻桌面上有没有人动鼠标"随机跳过。
    /// 它不报红，只<b>静静地什么都没验</b> —— 比假红更难发现。
    /// </para>
    /// </summary>
    private const int FirstFrameWaitMs = 2_500;

    /// <summary>
    /// "解码器不收输入、也取不出输出"最多容忍多久才判它真卡住。
    /// <para>
    /// 🔥 这个数存在的原因见 <c>TryDecodeOnce</c> 里那段注解：那个状态是 MF 的
    /// <b>正常中间态</b>，当场判死会产出随机假红（而且把人往"码流坏了"方向带）。
    /// </para>
    /// </summary>
    private const int StallBudgetMs = 500;

    private readonly ITestOutputHelper _output;

    public DxgiHardwareEncodeSmokeTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 🔥 <b>本批最要紧的一条</b>：真抓一帧 → 真转 NV12 → 真硬编 → 落 <c>.h264</c> → 真解回来。
    /// </summary>
    [Fact]
    public void Hardware_encoder_turns_a_captured_frame_into_a_decodable_bitstream()
    {
        if (!OperatingSystem.IsWindows()) return;

        if (!TryRequireHardwareEncoder(VideoCodec.H264)) return;

        var captured = CaptureOneFrame(out var surface);

        if (captured is null || surface is null) return;

        var converter = captured.Value.Converter;
        var gpu = captured.Value.Gpu;

        WindowsMediaFoundationVideoEncoder? encoder = null;

        try
        {
            var options = MediaFoundationEncoderOptions.Default(
                VideoCodec.H264, surface.Width, surface.Height, 30);

            encoder = WindowsMediaFoundationVideoEncoder.TryCreate(gpu, options, out var why);

            if (encoder is null)
            {
                // ⚠️ 枚举到了编码器却建不起来 —— 这是**我们的问题**，必须是 FAIL。
                throw new Xunit.Sdk.XunitException($"建 H.264 硬件编码器失败：{why}");
            }

            _output.WriteLine(encoder.Describe());
            _output.WriteLine($"零拷贝链路：{encoder.ZeroCopyNote}");

            var sequenceHeader = encoder.TryReadSequenceHeader();

            _output.WriteLine(
                $"输出类型里的解码器描述（MF_MT_MPEG_SEQUENCE_HEADER）："
                + (sequenceHeader is null
                    ? "没有 ⇒ 说明走的是 Annex-B（SPS/PPS 混在码流里）"
                    : $"{sequenceHeader.Length} 字节：{Hex(sequenceHeader, 12)}"));

            // ── 喂帧 ──
            //
            // 🔥 **必须给它真实的时间。** 异步编码器只在"消化完上一帧"之后才补发下一个
            //    `METransformNeedInput` 令牌；而这段循环如果全速转，30 次调用在几微秒内就
            //    跑完了 —— 编码器连第一帧都还没编完，后 29 帧全部被判"忙"丢掉。
            //    ⚠️ 那不是编码器的毛病，也不是 `TryEncode` 的毛病：生产的取帧循环本来就是
            //      30 fps（帧与帧之间隔 33 ms），这里必须**把那份时间也给它**，
            //      否则测的不是编码器，而是"我们的循环有多快"。
            //    ⇒ 以"喂满 FramesPerPass 帧"为目标，配一个按帧数算出来的时间预算。
            var produced = new List<EncodedFrame>();
            var clock = Stopwatch.StartNew();
            var budget = TimeSpan.FromMilliseconds(1_000 + (FramesPerPass * 200L));
            var wantKeyframe = true;

            while (encoder.FramesFed < FramesPerPass && clock.Elapsed < budget)
            {
                converter.TryConvert(surface.TextureHandle, grayscale: false, out var convertFailure)
                    .Should().BeTrue($"转 NV12 失败：{convertFailure}");

                // ⚠️ 必须"转完立刻取"：灰度模式下这个值是另一张纹理（见转换器注释）。
                var texture = converter.OutputTexture;
                var fedBefore = encoder.FramesFed;

                // 第一帧强制关键帧 = 模拟"新观众加入"，也保证码流有一个可解码的起点。
                if (encoder.TryEncode(texture, surface.Width, surface.Height, wantKeyframe, out var frame)
                    && frame is not null)
                {
                    produced.Add(frame);
                }

                if (encoder.FramesFed > fedBefore) wantKeyframe = encoder.FramesFed == SecondKeyframeAt;

                if (encoder.FramesFed < FramesPerPass) Thread.Sleep(1);
            }

            encoder.TryFlush(out var tail, out var flushFailure)
                .Should().BeTrue($"收尾 flush 失败：{flushFailure}");

            produced.AddRange(tail);

            _output.WriteLine(string.Empty);
            _output.WriteLine($"喂进去 {encoder.FramesFed} 帧、因忙丢 {encoder.FramesDroppedBusy} 帧、"
                + $"编出 {produced.Count} 帧（其中 flush 补出 {tail.Count} 帧）");

            // ⚠️ 这一条是**跑完之后**的快照：上面那条 Describe 是在喂帧之前打的，
            //    那时的计数器全是 0 —— 出了故障只看那一条等于什么都没看。
            //    事件的直方图（NeedInput / HaveOutput / 未知N / 读类型失败）就在这里。
            _output.WriteLine($"跑完之后的实况：{encoder.Describe()}");

            // ── ① 真的编出来了 ──
            encoder.FramesFed.Should().BeGreaterThan(
                0, "一帧都没喂进去 ⇒ 握手环节（NeedInput 令牌 / 输入状态）没走通");

            produced.Should().NotBeEmpty(
                "编码器一帧都没吐出来。它不会抛异常 —— 只会安静地什么都不做，所以必须在这里拦下");

            var totalBytes = produced.Sum(f => f.Data.Length);

            totalBytes.Should().BeGreaterThan(0, "码流是空的");

            _output.WriteLine($"码流合计 {totalBytes} 字节（{totalBytes / 1024.0:F1} KB），"
                + $"单帧 {totalBytes / (double)produced.Count / 1024.0:F1} KB");

            // ── ② 关键帧标记 ──
            var keyframes = produced.Count(f => f.IsKeyframe);

            if (encoder.KeyframeFlagAvailable)
            {
                keyframes.Should().BeGreaterThan(
                    0, "整段码流里一个关键帧都没有 ⇒ 新观众永远接不进来");

                _output.WriteLine($"关键帧 {keyframes}/{produced.Count}："
                    + $"第 {string.Join(",", produced.Select((f, i) => (f, i))
                        .Where(p => p.f.IsKeyframe).Take(8).Select(p => p.i))} 帧");
            }
            else
            {
                // ⚠️ 不猜。如实说清楚，并把"所以本批拿不到这个信息"写进报告。
                _output.WriteLine("⚠️ 本机编码器不给样本打 CleanPoint ⇒ 关键帧标记不可用（如实上报，不猜）。"
                    + "协议层要它，就必须走「扫 NAL」那条路 —— 而那条路要先定下封装格式。");
            }

            // ── ③ 码流长什么样（人眼可核对）──
            _output.WriteLine($"首帧前 24 字节：{Hex(produced[0].Data.Span, 24)}");

            var looksAnnexB = BitstreamPeek.LooksLikeAnnexB(produced[0].Data.Span);

            _output.WriteLine($"首帧是不是 Annex-B（00 00 00 01 起始码）：{(looksAnnexB ? "是" : "不是")}");

            // ── ③b 🔥 每个关键帧里到底带了什么（M5.4-b 的路线选择就压在这上面）──
            //
            // 注册原文（W3C WebCodecs AVC registration §4）：Annex-B 格式下，
            // 每个 type:"key" 的块“expected to contain both a primary coded picture that is an IDR
            // picture, and **all parameter sets necessary to decode all video data NAL units**”。
            // ⇒ 若 SPS 只出现在第一个关键帧里，那么本仓定的"reset() 之后等下一个关键帧"
            //   这条丢帧恢复路径就会让新解码器手上没有 SPS，症状是"重连之后一直花屏"。
            var keyframeIndices = produced
                .Select((frame, index) => (frame, index))
                .Where(pair => pair.frame.IsKeyframe)
                .Select(pair => pair.index)
                .ToList();

            _output.WriteLine($"关键帧下标：{string.Join(",", keyframeIndices)}");
            _output.WriteLine("各关键帧的 NAL 类型序列（7=SPS，8=PPS，5=IDR 片，9=AUD，1=非 IDR 片）：");

            foreach (var index in keyframeIndices.Take(4))
            {
                _output.WriteLine($"  第 {index} 帧（{produced[index].Data.Length} 字节）："
                    + $"{string.Join(",", BitstreamPeek.H264NalTypes(produced[index].Data.Span))}");
            }

            if (keyframeIndices.Count > 0)
            {
                var firstKeyframe = BitstreamPeek.H264NalTypes(produced[keyframeIndices[0]].Data.Span);

                firstKeyframe.Should().Contain(
                    7, "第一个关键帧必须自带 SPS —— 它是 decoder description（avcC）的唯一来源");

                firstKeyframe.Should().Contain(8, "第一个关键帧必须自带 PPS");

                if (keyframeIndices.Count > 1)
                {
                    var later = BitstreamPeek.H264NalTypes(produced[keyframeIndices[1]].Data.Span);

                    _output.WriteLine(later.Contains(7)
                        ? " ⇒ 第二个关键帧**也**带 SPS：参数集留在码流里（annexb 那条路）在本机可行。"
                        : " ⇒ 第二个关键帧**没有** SPS：SPS 只在第一个关键帧出现过一次 ⇒ "
                            + "Annex-B 那条路在本机不可用于 reset 恢复，必须走 description（已定的路线）。");
                }
            }

            // ── ④ 落文件 ──
            var path = Path.Combine(RepoRoot(), ".tools", "m5.4-capture", $"capture-{VideoCodec.H264}.h264");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using (var stream = File.Create(path))
            {
                foreach (var frame in produced) stream.Write(frame.Data.Span);
            }

            new FileInfo(path).Length.Should().Be(totalBytes, "文件长度必须与码流字节数一致");

            _output.WriteLine($"码流文件：{path}（{new FileInfo(path).Length / 1024} KB）");

            // ── ⑤ 🔥 解回来（这一条才是"码流正确"的证据）──
            _output.WriteLine(string.Empty);

            var decoded = MfDecoderProbe.TryDecode(
                produced,
                sequenceHeader,
                surface.Width,
                surface.Height,
                out var decodeReport);

            _output.WriteLine(decodeReport);

            decoded.Should().BeGreaterThan(
                0,
                "编出来的码流解不出一帧来 ⇒ 它不是一个能用的码流。"
                + "「编码成功、码流无用」正是这一环最典型的失败方式");
        }
        finally
        {
            encoder?.Dispose();
            converter.Dispose();
            gpu.Dispose();
            captured.Value.Capturer.Dispose();
        }
    }

    /// <summary>
    /// 🔥 <b>灰度省多少带宽</b>（M5 决策 14 的账）。
    ///
    /// <para>
    /// 判据刻意<b>不是"看着是不是灰的"</b>：那种判断无法断言，而且灰度是不是真的生效
    /// 已经由 <c>D3D11ColorConvertSmokeTests</c> 在像素层证过了（UV 常量 128）。
    /// 这里只问一件事：<b>同样的内容、同样的质量模式，灰度是不是真的更小</b>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>本用例的数字有明确的局限，写在这里免得被当成承诺</b>：内容是<b>静止的一帧重复 30 次</b>
    /// （桌面不动，DDA 就不再给新帧），所以第一个关键帧占了大头。
    /// 真实的动态桌面（滚动、拖窗、播视频）里色度的占比会变。⇒ 这里的结论只用来
    /// 证伪"灰度完全没用"或"灰度必然大幅省钱"这两种极端说法。
    /// </para>
    /// </summary>
    [Fact]
    public void Grayscale_costs_fewer_bytes_than_color_at_the_same_quality()
    {
        if (!OperatingSystem.IsWindows()) return;

        if (!TryRequireHardwareEncoder(VideoCodec.H264)) return;

        var captured = CaptureOneFrame(out var surface);

        if (captured is null || surface is null) return;

        var converter = captured.Value.Converter;
        var gpu = captured.Value.Gpu;

        // ⚠️ 显式声明成**不可空**类型再交给局部函数捕获：`CapturedSurface` 是个类，
        //    而编译器对"被内部函数捕获的变量"会丢掉判空结论 ⇒ 那里会开始报可能为空。
        //    这一句把结论固化下来。
        CapturedSurface frame = surface!;

        try
        {
            if (!converter.SupportsGrayscale)
            {
                _output.WriteLine($"本机不支持灰度（{converter.GrayscaleMechanism}）—— 这条判据不成立，跳过。");

                return;
            }

            var color = RunPass(grayscale: false, out var colorFrames, out var colorFailure);
            var gray = RunPass(grayscale: true, out var grayFrames, out var grayFailure);

            color.Should().BeGreaterThan(0, $"彩色那一趟没编出东西：{colorFailure}");
            gray.Should().BeGreaterThan(0, $"灰度那一趟没编出东西：{grayFailure}");

            // 两趟编出的帧数必须一样，否则字节数没有可比性（少一帧就等于白省一点）。
            grayFrames.Should().Be(
                colorFrames, "两趟编出的帧数不一样 ⇒ 字节数没有可比性，这个对比不成立");

            var ratio = color / (double)gray;

            _output.WriteLine(string.Empty);
            _output.WriteLine($"同一帧画面 × {FramesPerPass} 帧，固定质量模式（eAVEncCommonRateControlMode_Quality）：");
            _output.WriteLine($"  彩色 {color} 字节（{color / 1024.0:F1} KB，{colorFrames} 帧）");
            _output.WriteLine($"  灰度 {gray} 字节（{gray / 1024.0:F1} KB，{grayFrames} 帧）");
            _output.WriteLine($"  ⇒ 灰度是彩色的 {gray / (double)color:P1}（彩色/灰度 = {ratio:F3}）");

            // ⚠️ 刻意**不断言「灰度必须更小」**：白底黑字的界面本来就可能一模一样
            //    （文档 §16.1 ⑥ 的预期正是「趋近 0」）。这里断言的是一条更弱、
            //    但一定成立的不变式：灰度**不许明显更大** ——
            //    更大就说明那条路本身出了问题（例如多出来的那一趟反而引入了噪声）。
            gray.Should().BeLessThan(
                (long)(color * 1.05),
                "灰度比彩色还大 5% 以上 ⇒ 不是「没收益」，是那条路有问题");
        }
        finally
        {
            converter.Dispose();
            gpu.Dispose();
            captured.Value.Capturer.Dispose();
        }

        // ── 一趟完整的「转 → 编 → flush」──
        long RunPass(bool grayscale, out int frames, out string? failure)
        {
            frames = 0;
            failure = null;

            var options = MediaFoundationEncoderOptions.Default(
                VideoCodec.H264, frame.Width, frame.Height, 30);

            options.RateControl.Should().Be(
                EncoderRateControl.Quality,
                "带宽对比必须在固定质量下做 —— CBR 会把两种内容都撑到同一个码率上，"
                + "于是无论灰度有没有用，测出来都是「零收益」");

            using var encoder = WindowsMediaFoundationVideoEncoder.TryCreate(gpu, options, out failure);

            if (encoder is null) return 0;

            // 🔥 喂到「确实喂满 FramesPerPass 帧」为止，而不是「调 FramesPerPass 次」：
            //    编码器刚起流时还没有 NeedInput 令牌，头几帧会被当作忙而丢掉，
            //    而丢几帧是**不确定的** —— 于是两趟的帧数会不一样，
            //    字节数就没法比了（那正是这条用例要防的那种"看起来很像真有差异"的假结果）。
            //
            // 🔥 **而且必须给它真实的时间**：异步编码器只在"消化完上一帧"之后才补发下一个
            //    NeedInput 令牌。全速转的话 180 次调用在几十微秒内就跑完，而编码器连一帧都没编完
            //    ⇒ 实测只喂进去 5 帧（180 次"机会"里）。生产的取帧循环是 30 fps
            //    （帧间 33 ms），所以这里的预算是按**每帧 200 ms** 算的 —— 比生产宽 6 倍，
            //    留够机械硬盘式的突发，又能保证"喂不满"是一个真信号而不是循环太快。
            //    ⚠️ 时间预算用完仍没喂满 ⇒ 断言失败并把两个数都报出来（见下），不静默降级。
            var clock = Stopwatch.StartNew();
            var budget = TimeSpan.FromMilliseconds(1_000 + (FramesPerPass * 200L));

            var total = 0L;
            var forced = false;

            while (encoder.FramesFed < FramesPerPass && clock.Elapsed < budget)
            {
                if (!converter.TryConvert(frame.TextureHandle, grayscale, out failure)) return total;

                // ⚠️ 必须「转完立刻取」：灰度模式下这个值是另一张纹理。
                var fedBefore = encoder.FramesFed;

                var fed = encoder.TryEncode(
                    converter.OutputTexture, frame.Width, frame.Height, forceKeyframe: !forced, out var encoded);

                if (encoder.FramesFed > fedBefore && !forced) forced = true;

                if (fed && encoded is not null)
                {
                    total += encoded.Data.Length;

                    frames++;
                }

                // 已经喂满就不要再等一下了（剩下的输出由下面的 flush 收干净）。
                if (encoder.FramesFed < FramesPerPass) Thread.Sleep(1);
            }

            encoder.FramesFed.Should().Be(
                FramesPerPass,
                $"给了 {budget.TotalMilliseconds:F0} ms（每帧 200 ms 的预算，生产是 33 ms），"
                + $"只喂进去 {encoder.FramesFed} 帧 —— 编码器一直说它忙。"
                + $"最近一次失败：{encoder.LastFailure}");

            _output.WriteLine($"  [{(grayscale ? "灰度" : "彩色")}] {encoder.Describe()}");

            if (encoder.TryFlush(out var tail, out failure))
            {
                foreach (var encoded in tail)
                {
                    total += encoded.Data.Length;

                    frames++;
                }
            }

            return total;
        }
    }

    /// <summary>
    /// 本机有没有可用的硬件编码器。
    /// <para>
    /// 🔥 这里刻意用 <see cref="MediaFoundationEncoders.TryEnumerateHardware"/>（已验收过的那条探针）
    /// 来判断"前提成不成立"，而<b>不是</b>用"TryCreate 失败"来判断 ——
    /// 因为这两种情况的处置完全相反：没编码器是<b>硬件前提</b>（跳过），
    /// 有编码器但建不起来是<b>我们的缺陷</b>（FAIL）。
    /// </para>
    /// </summary>
    private bool TryRequireHardwareEncoder(VideoCodec codec)
    {
        var available = MediaFoundationEncoders.TryEnumerateHardware();

        if (available is null)
        {
            _output.WriteLine("查不了本机的硬件编码器（mfplat.dll 不可用）—— 前提不成立，跳过。");

            return false;
        }

        var names = available.Where(e => e.Codec == codec).Select(e => e.FriendlyName).Distinct().ToList();

        if (names.Count == 0)
        {
            _output.WriteLine($"本机没有 {codec} 硬件编码器 —— 前提不成立，跳过（这不是代码缺陷）。");

            return false;
        }

        _output.WriteLine($"本机 {codec} 硬件编码器：{string.Join(" / ", names)}");

        return true;
    }

    /// <summary>
    /// 抓一帧并建好转换器（与 <c>D3D11ColorConvertSmokeTests</c> 同一条前提）。
    /// <para>🔥 转换器必须建在<b>抓屏器手上那个设备</b>上 —— 零拷贝的结构性要求。</para>
    /// </summary>
    private Captured? CaptureOneFrame(out CapturedSurface? surface)
    {
        surface = null;

        var capturer = new WindowsDxgiScreenCapturer(Process.GetCurrentProcess().SessionId);

        var target = capturer.EnumerateScreens().FirstOrDefault(s => !s.IsVirtualDesktop);

        if (target is null)
        {
            _output.WriteLine("没有可抓的真实输出 —— 前提不成立，跳过。");

            capturer.Dispose();

            return null;
        }

        // 全新 duplication 的第一帧恒为"没有桌面内容"（见方案 §3.3 第 4 条），
        // 而**之后的**帧要等桌面真的变化 —— 所以这里按<b>时间</b>等，不按次数。
        //
        // ⚠️ 旧写法"试 12 次"≈180 ms，窗太窄且把结论硬写成 NoDesktopUpdate（实际多半是 TimedOut）。
        var clock = Stopwatch.StartNew();

        while (surface is null && clock.ElapsedMilliseconds < FirstFrameWaitMs)
        {
            surface = capturer.TryCaptureSurface(target.Id, CancellationToken.None, out var failure);

            if (failure is not null)
            {
                _output.WriteLine($"抓不到（前提不成立）：{failure}");

                capturer.Dispose();

                return null;
            }

            if (surface is null) Thread.Sleep(1);
        }

        if (surface is null)
        {
            _output.WriteLine($"等了 {FirstFrameWaitMs} ms 都没等到一帧有内容的画面"
                + $"（最近一次结论 {capturer.LastAcquireOutcome}）—— 跳过：桌面这段时间没有变化 / 被别的进程占着。"
                + "前提不成立，不是代码缺陷。");

            capturer.Dispose();

            return null;
        }

        _output.WriteLine($"抓到了：{surface.Width}×{surface.Height}（等了 {clock.ElapsedMilliseconds} ms，"
            + $"最近一次结论 {capturer.LastAcquireOutcome}）");

        var gpu = capturer.GpuContext;

        if (gpu is null)
        {
            _output.WriteLine("抓到了帧但拿不到设备上下文 —— 这不该发生。");

            capturer.Dispose();

            return null;
        }

        var converter = D3D11Nv12Converter.TryCreate(gpu, surface.Width, surface.Height, out var convertFailure);

        if (converter is null)
        {
            gpu.Dispose();
            capturer.Dispose();

            throw new Xunit.Sdk.XunitException($"建 BGRA→NV12 转换器失败：{convertFailure}");
        }

        return new Captured(capturer, gpu, converter);
    }

    private readonly record struct Captured(
        WindowsDxgiScreenCapturer Capturer, D3D11GpuContext Gpu, D3D11Nv12Converter Converter);

    private static string Hex(ReadOnlySpan<byte> data, int count)
    {
        var take = Math.Min(count, data.Length);

        return string.Join(" ", data[..take].ToArray().Select(b => b.ToString("X2")));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ControlServer.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    /// <summary>
    /// <b>用 MF 的解码器 MFT 把码流解回来</b>（"自编自解"）。
    ///
    /// <para>
    /// 🔥 <b>它故意留在测试里、不进平台库</b>：产品路径上<b>解码发生在浏览器</b>
    /// （WebCodecs），Agent 只编不解。为了验收而往产品库里塞一个解码器，
    /// 等于凭空多一份要长期维护、却没有任何调用方的代码。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 解码器的输入类型谈法比编码器敏感：H.264 的构造有 Annex-B 与 AVCC 两种，
    /// 后者<b>必须</b>把 <c>MF_MT_MPEG_SEQUENCE_HEADER</c> 一起给它。
    /// 所以这里先按"有描述就带上"谈一次，不行再按"没描述"谈一次 ——
    /// 两次都失败才会如实报错误原因。
    /// </para>
    /// </summary>
    private static class MfDecoderProbe
    {
        /// <summary>返回解出来的帧数；<c>report</c> 里是全过程（成功与失败都要能看懂）。</summary>
        internal static int TryDecode(
            IReadOnlyList<EncodedFrame> stream,
            byte[]? sequenceHeader,
            int expectWidth,
            int expectHeight,
            out string report)
        {
            var log = new List<string>();

            try
            {
                if (stream.Count == 0)
                {
                    report = "码流是空的，没什么可解。";

                    return 0;
                }

                var codec = stream[0].Codec;

                var subtype = codec == VideoCodec.H264
                    ? MediaFoundationInterop.MFVideoFormat_H264
                    : MediaFoundationInterop.MFVideoFormat_HEVC;

                if (!TryEnumerateDecoder(subtype, out var activates, out var count, out var why))
                {
                    report = $"解不了：{why}";

                    return 0;
                }

                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        var activate = Marshal.ReadIntPtr(activates, i * IntPtr.Size);

                        try
                        {
                            var name = MediaFoundationEncoders.TryReadFriendlyName(activate) ?? $"(第 {i} 条)";

                            var frames = TryDecodeWith(
                                activate, name, stream, sequenceHeader, expectWidth, expectHeight, log);

                            if (frames > 0)
                            {
                                report = string.Join(Environment.NewLine, log);

                                return frames;
                            }
                        }
                        catch (Exception ex)
                        {
                            log.Add($"  [{i}] 建的时候抛出 {ex.GetType().Name}：{ex.Message}");
                        }
                    }

                    report = $"本机枚举到 {count} 个 {codec} 解码器，但每一个都没能把这段码流解出来："
                        + Environment.NewLine + string.Join(Environment.NewLine, log);

                    return 0;
                }
                finally
                {
                    for (var i = 0; i < count; i++)
                    {
                        var activate = Marshal.ReadIntPtr(activates, i * IntPtr.Size);

                        if (activate != IntPtr.Zero) Marshal.Release(activate);
                    }

                    MediaFoundationInterop.CoTaskMemFree(activates);
                }
            }
            catch (Exception ex)
            {
                report = $"解码探针自己抛了 {ex.GetType().Name}：{ex.Message}";

                return 0;
            }
        }

        private static bool TryEnumerateDecoder(
            Guid subtype, out IntPtr activates, out uint count, out string? failure)
        {
            activates = IntPtr.Zero;
            count = 0;

            var info = new MediaFoundationInterop.MftRegisterTypeInfo
            {
                Major = MediaFoundationInterop.MFMediaType_Video,
                Sub = subtype,
            };

            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<MediaFoundationInterop.MftRegisterTypeInfo>());

            try
            {
                Marshal.StructureToPtr(info, buffer, false);

                _ = MediaFoundationInterop.MFStartup((int)MediaFoundationInterop.MfVersion, 0);

                // ⚠️ 不加 HARDWARE：硬解 MFT 只吃它能接受的那几种码流，而我们要的是
                //    "这段码流本身合不合法"。软件解码器更宽容，也更适合当裁判。
                //
                // 🔴 **H.264 在解码器上是「输入」，不是「输出」** —— 所以它必须落在**第三个**
                //    参数（pInputType），不是第四个。落到第四个就等于在问
                //    "哪台解码器能**输出** H.264"，那是个没人能回答的问题，
                //    于是拿到 0 个，报出来是"本机没有这个格式的解码器 MFT" ——
                //    一个把人往"机器上没装解码器"方向带的假结论。
                //    （编码器那边反过来：H.264 是它的输出，所以用第四个参数，别照抄。）
                var hr = MediaFoundationInterop.MFTEnumEx(
                    MediaFoundationInterop.CategoryVideoDecoder,
                    0,
                    buffer,
                    IntPtr.Zero,
                    out activates,
                    out count);

                if (!ComVtable.Succeeded(hr))
                {
                    failure = $"MFTEnumEx（解码器）失败（{ComVtable.Describe(hr)}）。";

                    return false;
                }

                if (count == 0 || activates == IntPtr.Zero)
                {
                    failure = "本机没有这个格式的解码器 MFT。";

                    return false;
                }

                failure = null;

                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>成功返回帧数（&gt; 0），失败返回 -1（原因写进 <paramref name="log"/>）。</summary>
        /// <remarks>
        /// 🔴 <b>"跑完了但一帧都没解出来"必须算失败，不能算成功</b>：
        /// 早先这里是 <c>frames &gt;= 0</c>，于是 0 帧会被记成
        /// <c>用「X」解出 0 帧</c> 并<b>提前返回</b>——既不再试别的解码器/别的谈法，
        /// 又把一个"解不出来"写成了"解出来了，只是 0 帧"。两者对读日志的人来说是同一句话。
        /// </remarks>
        private static int TryDecodeWith(
            IntPtr activate,
            string name,
            IReadOnlyList<EncodedFrame> stream,
            byte[]? sequenceHeader,
            int expectWidth,
            int expectHeight,
            List<string> log)
        {
            // 各种组合都试一遍：带/不带描述。谈不成输入类型就换一种。
            var attempts = sequenceHeader is null
                ? new byte[]?[] { null }
                : new byte[]?[] { sequenceHeader, null };

            foreach (var header in attempts)
            {
                var frames = TryDecodeOnce(activate, name, stream, header, expectWidth, expectHeight, out var why);

                if (frames > 0)
                {
                    log.Add($"  用「{name}」解出 {frames} 帧"
                        + $"（解码器描述：{(header is null ? "未带" : $"{header.Length} 字节")}）");

                    return frames;
                }

                log.Add($"  [{name}｜{(header is null ? "未带描述" : "带描述")}] {why}");
            }

            return -1;
        }

        private static int TryDecodeOnce(
            IntPtr activate,
            string name,
            IReadOnlyList<EncodedFrame> stream,
            byte[]? sequenceHeader,
            int expectWidth,
            int expectHeight,
            out string? failure)
        {
            failure = null;

            var transform = IntPtr.Zero;
            var outputType = IntPtr.Zero;

            try
            {
                // 异步解码器同样要在 activate 上解锁。
                SetUInt32(activate, MediaFoundationInterop.MF_TRANSFORM_ASYNC_UNLOCK, 1);

                var iid = MediaFoundationInterop.IID_IMFTransform;

                var activateFn = ComVtable.Bind<MediaFoundationInterop.Delegates.ActivateObjectFn>(
                    activate, MediaFoundationInterop.ActivateSlots.ActivateObject);

                var hr = activateFn(activate, ref iid, out transform);

                if (!ComVtable.Succeeded(hr))
                {
                    failure = $"ActivateObject 失败（{ComVtable.Describe(hr)}）。";

                    return -1;
                }

                // ── 输入类型：视频 / H.264（+ 可选描述）──
                hr = MediaFoundationInterop.MFCreateMediaType(out var inputType);

                if (!ComVtable.Succeeded(hr))
                {
                    failure = $"MFCreateMediaType 失败（{ComVtable.Describe(hr)}）。";

                    return -1;
                }

                try
                {
                    var codec = stream[0].Codec;

                    _ = SetGuid(inputType, MediaFoundationInterop.MF_MT_MAJOR_TYPE, MediaFoundationInterop.MFMediaType_Video);

                    _ = SetGuid(
                        inputType,
                        MediaFoundationInterop.MF_MT_SUBTYPE,
                        codec == VideoCodec.H264
                            ? MediaFoundationInterop.MFVideoFormat_H264
                            : MediaFoundationInterop.MFVideoFormat_HEVC);

                    _ = SetUInt64(
                        inputType,
                        MediaFoundationInterop.MF_MT_FRAME_SIZE,
                        MediaFoundationInterop.Pack((uint)expectWidth, (uint)expectHeight));

                    _ = SetUInt64(
                        inputType,
                        MediaFoundationInterop.MF_MT_FRAME_RATE,
                        MediaFoundationInterop.Pack(30, 1));

                    if (sequenceHeader is not null)
                    {
                        var blob = Marshal.AllocHGlobal(sequenceHeader.Length);

                        try
                        {
                            Marshal.Copy(sequenceHeader, 0, blob, sequenceHeader.Length);

                            var key = MediaFoundationInterop.MF_MT_MPEG_SEQUENCE_HEADER;

                            var setBlob = ComVtable.Bind<MediaFoundationInterop.Delegates.AttributeSetBlobFn>(
                                inputType, MediaFoundationInterop.AttributeSlots.SetBlob);

                            _ = setBlob(inputType, ref key, blob, (uint)sequenceHeader.Length);
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(blob);
                        }
                    }

                    var setInput = ComVtable.Bind<MediaFoundationInterop.Delegates.SetTypeFn>(
                        transform, MediaFoundationInterop.TransformSlots.SetInputType);

                    hr = setInput(transform, 0, inputType, 0);

                    if (!ComVtable.Succeeded(hr))
                    {
                        failure = $"解码器不接受这个输入类型（{ComVtable.Describe(hr)}）。";

                        return -1;
                    }
                }
                finally
                {
                    ComVtable.ReleasePointer(ref inputType);
                }

                // ── 输出类型：挑一个 NV12 的 ──
                if (!TrySetNv12OutputType(transform, ref outputType, out failure)) return -1;

                // ── 解码 ──
                var processMessage = ComVtable.Bind<MediaFoundationInterop.Delegates.ProcessMessageFn>(
                    transform, MediaFoundationInterop.TransformSlots.ProcessMessage);

                _ = processMessage(transform, MediaFoundationInterop.MessageNotifyBeginStreaming, 0);
                _ = processMessage(transform, MediaFoundationInterop.MessageNotifyStartOfStream, 0);

                var decoded = 0;
                var step = 10_000_000L / 30;

                for (var i = 0; i < stream.Count; i++)
                {
                    // 🔥 **同步解码器不是随时都收。**
                    //    `MF_E_NOTACCEPTING`（0xC00D36B5）的意思是「我手上这帧还没消化完，
                    //    你先把输出取走」。它**不是错误** —— 把它当错误处理，
                    //    "一段完全合法的码流"会被判成"解不了"。
                    //    本项目就在这一点上栽过：2 帧的码流侥幸能过（解码器一次吞得下两帧），
                    //    30 帧就必挂，而报出来的是"本机枚举到 1 个 H264 解码器，
                    //    但每一个都没能把这段码流解出来" —— 一个把人往"码流有问题"方向带的假结论。
                    //    ⇒ 收到它就**先把输出抽干，再重试喂**。
                    //
                    // 🔴 **再加一层：不许"抽不出东西"就当场判死。**
                    //    `MF_E_NOTACCEPTING` 与 `ProcessOutput` 回 NEED_MORE_INPUT 可以**同时**成立
                    //    （解码器要攒够下一帧才开始吐），这是合法中间态。
                    //    早先这里一见 `drained == 0` 就报"卡住了"，后果是<b>随机假红</b>：
                    //    同一台机器同一天两次全量，一次解出 30 帧、一次报"卡住了" ——
                    //    而那个结论会把人往"码流坏了/编码有 bug"方向带，错的。
                    //    ⇒ 给一个有界时间窗，仍然没动静才算真卡住。
                    var stalled = Stopwatch.StartNew();

                    while (true)
                    {
                        var outcome = TryFeed(transform, stream[i].Data.Span, i * step, out var feedFailure);

                        if (outcome == FeedOutcome.Fed) break;

                        if (outcome == FeedOutcome.Failed)
                        {
                            failure = feedFailure;

                            return -1;
                        }

                        var drained = Drain(transform, ref outputType, expectWidth, expectHeight, out var drainFailure);

                        if (drainFailure is not null)
                        {
                            failure = drainFailure;

                            return -1;
                        }

                        decoded += drained;

                        if (drained > 0)
                        {
                            stalled.Restart();

                            continue;
                        }

                        if (stalled.ElapsedMilliseconds > StallBudgetMs)
                        {
                            failure = $"解码器连续 {StallBudgetMs} ms 拒绝接收输入，又取不出任何输出 —— 卡住了"
                                + $"（第 {i} 帧，累计已解 {decoded} 帧）。";

                            return -1;
                        }

                        Thread.Sleep(2);
                    }

                    decoded += Drain(transform, ref outputType, expectWidth, expectHeight, out var drainFailure2);

                    if (drainFailure2 is not null)
                    {
                        failure = drainFailure2;

                        return -1;
                    }
                }

                // 收尾：解码器手里也有重排延迟，不 drain 会少帧。
                _ = processMessage(transform, MediaFoundationInterop.MessageNotifyEndOfStream, 0);

                // ⚠️ `MFT_MESSAGE_COMMAND_DRAIN` 对**同步** MFT 是允许返回 E_NOTIMPL 的
                //    （它本来是为异步 MFT 设计的），所以这里**不看它的返回值** ——
                //    真正把尾巴挤出来的是下面那个"一直取到取不出为止"的循环。
                _ = processMessage(transform, MediaFoundationInterop.MessageCommandDrain, 0);

                // 🔥 上限给足 64：`Drain` 一次最多取 4 帧，而解码器的重排缓冲可能压着十几帧。
                //    早先写 8 轮 × 每轮最多 4 帧，看起来够，但**第一轮返回 0 就 break 了** ——
                //    于是尾巴一帧都没挤出来，30 帧进、22 帧出，差的那 8 帧被静默吞掉。
                //    （那 8 帧不是码流的问题，是**这段验证代码没取干净** ——
                //     而"少几帧"会让"解码器解出来多少"不再是"码流好不好"的证据。）
                for (var i = 0; i < 64; i++)
                {
                    var more = Drain(transform, ref outputType, expectWidth, expectHeight, out var tailFailure);

                    if (tailFailure is not null) break;

                    if (more == 0) break;

                    decoded += more;
                }

                return decoded;
            }
            catch (Exception ex)
            {
                failure = $"抛出 {ex.GetType().Name}：{ex.Message}";

                return -1;
            }
            finally
            {
                ComVtable.ReleasePointer(ref outputType);
                ComVtable.ReleasePointer(ref transform);
            }
        }

        /// <summary>
        /// 挑一个 NV12 输出类型并按上。
        ///
        /// <para>
        /// 🔥 <b>它会被调两次</b>：一次在建流时，一次在解码器回
        /// <c>MF_E_TRANSFORM_STREAM_CHANGE</c> 时 —— 解码器要解到第一个关键帧
        /// 才知道真实分辨率，那时它会要求重谈输出类型。
        /// </para>
        /// <para>
        /// ⚠️ <b>那件事不是错误</b>。把它当错误处理，"能解的码流"会被判成"不能解" ——
        /// 而这是一个<b>必然会发生</b>的事件（只要码流里有关键帧）。
        /// </para>
        /// </summary>
        private static bool TrySetNv12OutputType(IntPtr transform, ref IntPtr outputType, out string? failure)
        {
            failure = null;

            ComVtable.ReleasePointer(ref outputType);

            var getType = ComVtable.Bind<MediaFoundationInterop.Delegates.GetAvailableTypeFn>(
                transform, MediaFoundationInterop.TransformSlots.GetOutputAvailableType);

            for (var i = 0; i < 16; i++)
            {
                // ⚠️ 取完时回的是 MF_E_NO_MORE_TYPES，那是"没有了"，不是失败。
                if (!ComVtable.Succeeded(getType(transform, 0, (uint)i, out var candidate))) break;

                if (outputType == IntPtr.Zero && IsNv12(candidate)) outputType = candidate;
                else ComVtable.ReleasePointer(ref candidate);
            }

            if (outputType == IntPtr.Zero)
            {
                failure = "解码器没提供 NV12 输出类型。";

                return false;
            }

            var setOutput = ComVtable.Bind<MediaFoundationInterop.Delegates.SetTypeFn>(
                transform, MediaFoundationInterop.TransformSlots.SetOutputType);

            var hr = setOutput(transform, 0, outputType, 0);

            if (!ComVtable.Succeeded(hr))
            {
                failure = $"设 NV12 输出类型失败（{ComVtable.Describe(hr)}）。";

                ComVtable.ReleasePointer(ref outputType);

                return false;
            }

            return true;
        }

        /// <summary>喂一个样本的结果。</summary>
        /// <para>
        /// 🔥 <b>必须是三态，不能压成 bool</b>：<c>MF_E_NOTACCEPTING</c> 既不是"成功"
        /// 也不是"失败"，它是"**现在不行，先把输出取走再来**"。
        /// 压成 bool 就只能把它归到两边之一，而两种归法都是错的 ——
        /// 归"失败"会把好码流判死，归"成功"会把那一帧**静默丢掉**（于是解得出的帧数对不上）。
        /// </para>
        private enum FeedOutcome
        {
            /// <summary>收下了。</summary>
            Fed,

            /// <summary><c>MF_E_NOTACCEPTING</c> —— 先把输出抽干，再重试。</summary>
            NotAccepting,

            /// <summary>真出错了，<c>failure</c> 里有话。</summary>
            Failed,
        }

        private static FeedOutcome TryFeed(
            IntPtr transform, ReadOnlySpan<byte> payload, long time, out string? failure)
        {
            failure = null;

            var hr = MediaFoundationInterop.MFCreateMemoryBuffer((uint)Math.Max(payload.Length, 1), out var buffer);

            if (!ComVtable.Succeeded(hr))
            {
                failure = $"MFCreateMemoryBuffer 失败（{ComVtable.Describe(hr)}）。";

                return FeedOutcome.Failed;
            }

            var sample = IntPtr.Zero;

            try
            {
                var lockFn = ComVtable.Bind<MediaFoundationInterop.Delegates.BufferLockFn>(
                    buffer, MediaFoundationInterop.MediaBufferSlots.Lock);

                hr = lockFn(buffer, out var data, out _, out _);

                if (!ComVtable.Succeeded(hr))
                {
                    failure = $"IMFMediaBuffer::Lock 失败（{ComVtable.Describe(hr)}）。";

                    return FeedOutcome.Failed;
                }

                try
                {
                    if (payload.Length > 0)
                    {
                        // Marshal.Copy 只吃数组（本工程没开 unsafe），所以这里必然有一次复制。
                        // 它只发生在测试里 —— 产品路径上喂给编码器的是纹理句柄，不经过这里。
                        var temp = payload.ToArray();

                        Marshal.Copy(temp, 0, data, temp.Length);
                    }

                    // 🔥 不设当前长度，解码器收到的是一个"零字节样本" —— 不报错，也不出帧。
                    var setLength = ComVtable.Bind<MediaFoundationInterop.Delegates.BufferSetCurrentLengthFn>(
                        buffer, MediaFoundationInterop.MediaBufferSlots.SetCurrentLength);

                    hr = setLength(buffer, (uint)payload.Length);

                    if (!ComVtable.Succeeded(hr))
                    {
                        failure = $"SetCurrentLength 失败（{ComVtable.Describe(hr)}）。";

                        return FeedOutcome.Failed;
                    }
                }
                finally
                {
                    var unlock = ComVtable.Bind<MediaFoundationInterop.Delegates.BufferUnlockFn>(
                        buffer, MediaFoundationInterop.MediaBufferSlots.Unlock);

                    _ = unlock(buffer);
                }

                hr = MediaFoundationInterop.MFCreateSample(out sample);

                if (!ComVtable.Succeeded(hr))
                {
                    failure = $"MFCreateSample 失败（{ComVtable.Describe(hr)}）。";

                    return FeedOutcome.Failed;
                }

                var addBuffer = ComVtable.Bind<MediaFoundationInterop.Delegates.AddBufferFn>(
                    sample, MediaFoundationInterop.SampleSlots.AddBuffer);

                hr = addBuffer(sample, buffer);

                if (!ComVtable.Succeeded(hr))
                {
                    failure = $"AddBuffer 失败（{ComVtable.Describe(hr)}）。";

                    return FeedOutcome.Failed;
                }

                var setTime = ComVtable.Bind<MediaFoundationInterop.Delegates.SetSampleTimeFn>(
                    sample, MediaFoundationInterop.SampleSlots.SetSampleTime);

                _ = setTime(sample, time);

                var processInput = ComVtable.Bind<MediaFoundationInterop.Delegates.ProcessInputFn>(
                    transform, MediaFoundationInterop.TransformSlots.ProcessInput);

                hr = processInput(transform, 0, sample, 0);

                if (hr == MediaFoundationInterop.HResults.NotAccepting)
                {
                    // ⚠️ **不是失败** —— 见 FeedOutcome 的说明。
                    return FeedOutcome.NotAccepting;
                }

                if (!ComVtable.Succeeded(hr))
                {
                    failure = $"解码器 ProcessInput 失败（{ComVtable.Describe(hr)}）。";

                    return FeedOutcome.Failed;
                }

                return FeedOutcome.Fed;
            }
            finally
            {
                ComVtable.ReleasePointer(ref sample);
                ComVtable.ReleasePointer(ref buffer);
            }
        }

        /// <summary>把解码器手上已经备好的帧取出来（只看数量；像素不在这里验）。</summary>
        private static int Drain(IntPtr transform, ref IntPtr outputType, int width, int height, out string? failure)
        {
            failure = null;

            var decoded = 0;

            var getInfo = ComVtable.Bind<MediaFoundationInterop.Delegates.GetOutputStreamInfoFn>(
                transform, MediaFoundationInterop.TransformSlots.GetOutputStreamInfo);

            if (!ComVtable.Succeeded(getInfo(transform, 0, out var info)))
            {
                failure = "GetOutputStreamInfo（解码器）失败。";

                return 0;
            }

            var provides = (info.Flags & MediaFoundationInterop.OutputStreamProvidesSamples) != 0;

            var size = info.Size > 0
                ? info.Size
                : (uint)Math.Max((long)width * height * 3 / 2, 1 << 20);

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var sample = provides ? IntPtr.Zero : CreateSample(size, out failure);

                if (!provides && sample == IntPtr.Zero) return decoded;

                var slot = new MediaFoundationInterop.MftOutputDataBuffer
                {
                    StreamId = 0,
                    Sample = sample,
                    Status = 0,
                    Events = IntPtr.Zero,
                };

                try
                {
                    var processOutput = ComVtable.Bind<MediaFoundationInterop.Delegates.ProcessOutputFn>(
                        transform, MediaFoundationInterop.TransformSlots.ProcessOutput);

                    var hr = processOutput(transform, 0u, 1, ref slot, out _);

                    if (hr == MediaFoundationInterop.HResults.TransformNeedMoreInput)
                    {
                        // 🔥 这是"还要更多输入"的正常状态，不是错误。
                        return decoded;
                    }

                    if (hr == MediaFoundationInterop.HResults.BufferTooSmall)
                    {
                        size *= 2;

                        continue;
                    }

                    if (hr == MediaFoundationInterop.HResults.TransformStreamChange)
                    {
                        // 🔥 解到第一个关键帧时，解码器会要求重谈输出类型 —— **这是必经的一步**。
                        if (!TrySetNv12OutputType(transform, ref outputType, out var resetFailure))
                        {
                            failure = $"解码器要求重设输出类型，而重设失败：{resetFailure}";

                            return decoded;
                        }

                        continue;
                    }

                    if (!ComVtable.Succeeded(hr))
                    {
                        failure = $"解码器 ProcessOutput 失败（{ComVtable.Describe(hr)}）。";

                        return decoded;
                    }

                    decoded++;
                }
                finally
                {
                    ComVtable.ReleasePointer(ref slot.Sample);
                }

                if (!provides) break;
            }

            return decoded;
        }

        private static IntPtr CreateSample(uint size, out string? failure)
        {
            failure = null;

            var hr = MediaFoundationInterop.MFCreateMemoryBuffer(size, out var buffer);

            if (!ComVtable.Succeeded(hr))
            {
                failure = $"MFCreateMemoryBuffer({size}) 失败（{ComVtable.Describe(hr)}）。";

                return IntPtr.Zero;
            }

            var releaseBuffer = true;

            try
            {
                hr = MediaFoundationInterop.MFCreateSample(out var sample);

                if (!ComVtable.Succeeded(hr))
                {
                    failure = $"MFCreateSample 失败（{ComVtable.Describe(hr)}）。";

                    return IntPtr.Zero;
                }

                var addBuffer = ComVtable.Bind<MediaFoundationInterop.Delegates.AddBufferFn>(
                    sample, MediaFoundationInterop.SampleSlots.AddBuffer);

                hr = addBuffer(sample, buffer);

                if (!ComVtable.Succeeded(hr))
                {
                    failure = $"AddBuffer 失败（{ComVtable.Describe(hr)}）。";

                    var local = sample;

                    ComVtable.ReleasePointer(ref local);

                    return IntPtr.Zero;
                }

                ComVtable.ReleasePointer(ref buffer);

                releaseBuffer = false;

                return sample;
            }
            finally
            {
                if (releaseBuffer) ComVtable.ReleasePointer(ref buffer);
            }
        }

        private static bool IsNv12(IntPtr mediaType)
        {
            var key = MediaFoundationInterop.MF_MT_SUBTYPE;

            var getGuid = ComVtable.Bind<MediaFoundationInterop.Delegates.AttributeGetGuidFn>(
                mediaType, MediaFoundationInterop.AttributeSlots.GetGUID);

            if (!ComVtable.Succeeded(getGuid(mediaType, ref key, out var subtype))) return false;

            return subtype == MediaFoundationInterop.MFVideoFormat_NV12;
        }

        private static bool SetUInt32(IntPtr attributes, Guid key, uint value)
        {
            var fn = ComVtable.Bind<MediaFoundationInterop.Delegates.AttributeSetUInt32Fn>(
                attributes, MediaFoundationInterop.AttributeSlots.SetUINT32);

            return ComVtable.Succeeded(fn(attributes, ref key, value));
        }

        private static bool SetUInt64(IntPtr attributes, Guid key, ulong value)
        {
            var fn = ComVtable.Bind<MediaFoundationInterop.Delegates.AttributeSetUInt64Fn>(
                attributes, MediaFoundationInterop.AttributeSlots.SetUINT64);

            return ComVtable.Succeeded(fn(attributes, ref key, value));
        }

        private static bool SetGuid(IntPtr attributes, Guid key, Guid value)
        {
            var fn = ComVtable.Bind<MediaFoundationInterop.Delegates.AttributeSetGuidFn>(
                attributes, MediaFoundationInterop.AttributeSlots.SetGUID);

            return ComVtable.Succeeded(fn(attributes, ref key, ref value));
        }
    }
}
