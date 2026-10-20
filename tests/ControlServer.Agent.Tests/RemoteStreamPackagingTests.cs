using System.Diagnostics;
using System.IO.Pipes;
using ControlServer.Agent.Tasks;
using ControlServer.Agent.Video;
using ControlServer.Contracts;
using ControlServer.Platform.Abstractions;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// <b>参数集打包：帧头与码流必须自洽</b>（M5.4-b / 方案 §3.4.2、§8 第 6 步）。
///
/// <para>
/// 🔥🔥 <b>这组用例守的是一类「不报错的错」</b>，而它的两个方向都很难在真机上发现：
/// </para>
/// <list type="bullet">
///   <item><b>贴了 avcC、码流却还是 Annex-B</b> ⇒ 浏览器 <c>configure()</c> 失败或直接花屏；</item>
///   <item><b>说好带内、却把码流换成了长度前缀</b> ⇒ 解码器把长度当 NAL 头读，
///         解出一大片垃圾，而<b>两端日志都干净</b>。</item>
/// </list>
///
/// <para>
/// ⇒ 所以这里不只测「<c>Description</c> 是 null」这一半，而是<b>成对</b>地测
/// 「帧头 + 码流形状」：两半必须指向同一条路。这正是本批决定
/// 「两条路各自一整块写、不做成两个开关」的理由。
/// </para>
///
/// <para>
/// 🔸 素材用 <see cref="VideoTestVectors.RealH264Sps"/>（真机 QSV 落盘抠出来的），
/// 而<b>不是</b>手搓的十六进制：真 SPS 才带得出 <c>avcC</c> 里那几个"我们会读错"的字段。
/// </para>
/// </summary>
public sealed class RemoteStreamPackagingTests
{
    // ═══════════════════════════════════════════════════════════
    // ① 带内（本批默认）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>默认档：码流一个字节都不许动，<c>Description</c> 必须是 <c>null</c>。</b>
    ///
    /// <para>
    /// 「一个字节都不动」不是洁癖：带内那条路的<b>全部价值</b>就是热路径零转换
    /// （4K 一帧 800 KB，改写一次就是一次全量重排）。一旦有人"顺手规范化一下起始码"，
    /// 它就不再是那条零成本的路了 —— 而性能退化没有任何断言会红。
    /// </para>
    /// </summary>
    [Fact]
    public async Task In_band_leaves_the_bitstream_untouched_and_sends_no_description()
    {
        var sessionId = "packaging-inband-" + Guid.NewGuid().ToString("N")[..8];

        var accessUnit = VideoTestVectors.AnnexB(
            VideoTestVectors.RealH264Sps,
            VideoTestVectors.RealH264Pps,
            VideoTestVectors.RealH264IdrHead);

        var encoder = new ScriptedSurfaceEncoder(accessUnit);
        var logs = new List<string>();

        await using var host = NewHost(encoder, logs);

        // ⚠️ **不传 Packaging**：默认就该是带内。传了等于没测默认值。
        var started = host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 60));

        started.Ok.Should().BeTrue($"开始推流本该成功，失败原因：{started.Message}");

        var received = await ReadFramesAsync(sessionId, want: 1, timeoutMs: 6000);

        received.Should().HaveCountGreaterThanOrEqualTo(1, $"日志：{string.Join(" / ", logs)}");

        var frame = received[0];

        frame.Header.Codec.Should().Be(StreamCodecs.H264);

        // ── ① 码流原样 ──
        frame.Data.Should().Equal(
            accessUnit,
            "带内那条路**不许碰码流**：它的全部价值就是零转换。"
            + "一旦有人在这里做规范化，4K 就多出一次全量重排，而没有任何断言会红");

        // ── ② 不许带 description ──
        frame.Header.Description.Should().BeNull(
            "带内 = 参数集在码流里。这时候再贴一份 avcC，浏览器的 configure() 会失败或花屏 —— "
            + "「贴了 avcC、码流却还是 Annex-B」正是这两半脱节的经典症状");

        // ── ③ codec 串必须**从真实 SPS 反推**，而且是 avc3 那一支 ──
        frame.Header.CodecString.Should().Be(
            "avc3.640032",
            "带内要用 avc3（参数集在码流里）；而这串的 profile/level 必须来自真实的 SPS —— "
            + "手写错一个字节的表现是 NotSupportedError，看起来像「浏览器不支持 H.264」");
    }

    // ═══════════════════════════════════════════════════════════
    // ② 带外（留的开关）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>带外：码流必须换成长度前缀，而且帧头必须带上 avcC</b> —— 缺一半都是错的。
    ///
    /// <para>
    /// ⚠️ 这条用例故意<b>同时</b>断言"帧头有 description"和"码流里没有起始码了"：
    /// 只断言其中一个，就等于允许另一半悄悄走偏 —— 而走偏的那一半正是真机上
    /// 表现为"花屏、日志干净"的那个。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Out_of_band_prefixes_lengths_and_carries_the_configuration_record()
    {
        var sessionId = "packaging-outofband-" + Guid.NewGuid().ToString("N")[..8];

        var accessUnit = VideoTestVectors.AnnexB(
            VideoTestVectors.RealH264Sps,
            VideoTestVectors.RealH264Pps,
            VideoTestVectors.RealH264IdrHead);

        var encoder = new ScriptedSurfaceEncoder(accessUnit);
        var logs = new List<string>();

        await using var host = NewHost(encoder, logs);

        var started = host.Start(new RemoteStreamRequest(
            RemoteSessionActions.Start, sessionId, MaxFps: 60,
            Packaging: RemoteStreamPackagings.OutOfBand));

        started.Ok.Should().BeTrue($"开始推流本该成功，失败原因：{started.Message}");

        var received = await ReadFramesAsync(sessionId, want: 1, timeoutMs: 6000);

        received.Should().HaveCountGreaterThanOrEqualTo(1, $"日志：{string.Join(" / ", logs)}");

        var frame = received[0];

        // ── ① 帧头带 avcC ──
        frame.Header.Description.Should().NotBeNull(
            "带外 = 参数集随帧头走。没有它，观看端手里一个参数集都没有 —— "
            + "而 AVCDecoderConfigurationRecord 的第一个字节按规格恒为 1（configurationVersion）");

        frame.Header.Description![0].Should().Be(1, "configurationVersion 按规格恒为 1");

        frame.Header.CodecString.Should().Be(
            "avc1.640032",
            "带外要用 avc1（参数集在 description 里）；同一个 SPS 读出来的 profile/level 段必须与带内一致");

        // ── ② 码流换成"4 字节大端长度 + NAL"，且**参数集被摘掉了** ──
        //
        // 🔥 参数集必须摘掉：规格写明"description 在时，参数集不出现在码流里"。
        //    留着它不会报错，只会每帧白发几十字节，而且在"参数集也变了"的场景下
        //    与 description 里那份产生两种说法 —— 那种不一致最难查。
        var expectedLength = 4 + VideoTestVectors.RealH264IdrHead.Length;

        frame.Data.Should().HaveCount(
            expectedLength,
            $"只剩那一个 IDR 片（SPS/PPS 已进 description），所以长度是 4 字节长度前缀 + {VideoTestVectors.RealH264IdrHead.Length} 字节片头；"
            + "长度不对通常说明参数集没被摘掉，或者起始码没被换成长度前缀");

        ((frame.Data[0] << 24) | (frame.Data[1] << 16) | (frame.Data[2] << 8) | frame.Data[3])
            .Should().Be(
                VideoTestVectors.RealH264IdrHead.Length,
                "每个 NAL 前面是 **4 字节大端**长度（对应 lengthSizeMinusOne = 3）");

        frame.Data.AsSpan(4).ToArray().Should().Equal(
            VideoTestVectors.RealH264IdrHead,
            "长度前缀只换封装、不动载荷 —— 顺手去剥 00 00 03（emulation prevention）就把码流改坏了");

        // ── ③ 码流里不该再有起始码 ──
        ContainsStartCode(frame.Data).Should().BeFalse(
            "还有 Annex-B 起始码就等于「只换了帧头、没换码流」—— 那正是这条用例要挡的连体错误。"
            + $"实际字节：{VideoTestVectors.Hex(frame.Data)}");
    }

    /// <summary>
    /// 🔥 <b>带外解不出参数集时，这一帧要如实退回带内</b>，而不是发一个"半个带外"。
    ///
    /// <para>
    /// 退化的那一步如果写错，症状与"带外正常"几乎一样（画面花、日志干净）——
    /// 所以这里既断言帧头（<c>Description</c> 为 null）、又断言码流（原样），
    /// 还要断言<b>日志说了这件事</b>：否则现场只看到"花屏"，无从知道是配置没建出来。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Out_of_band_without_parameter_sets_falls_back_to_in_band_for_that_frame()
    {
        var sessionId = "packaging-outofband-nosets-" + Guid.NewGuid().ToString("N")[..8];

        // 这一段里**一个 SPS/PPS 都没有**：只有 IDR 片。
        // 模拟"编码器这一帧没把参数集带出来，而我们也还一次都没解到过"。
        var accessUnit = VideoTestVectors.AnnexB(VideoTestVectors.RealH264IdrHead);

        var encoder = new ScriptedSurfaceEncoder(accessUnit)
        {
            // 连兜底那条路也堵上（序列头读不出来）—— 于是真的一份参数集都没有。
            ParameterSets = null,
        };

        var logs = new List<string>();

        await using var host = NewHost(encoder, logs);

        var started = host.Start(new RemoteStreamRequest(
            RemoteSessionActions.Start, sessionId, MaxFps: 60,
            Packaging: RemoteStreamPackagings.OutOfBand));

        started.Ok.Should().BeTrue($"开始推流本该成功，失败原因：{started.Message}");

        var received = await ReadFramesAsync(sessionId, want: 1, timeoutMs: 6000);

        received.Should().HaveCountGreaterThanOrEqualTo(1, $"日志：{string.Join(" / ", logs)}");

        var frame = received[0];

        frame.Header.Description.Should().BeNull(
            "avcC 建不出来 ⇒ 这一帧**只能**按带内发。发一个“没有 description 却说自己是长度前缀”的帧，"
            + "观看端会把长度当 NAL 头读，解出一大片垃圾而两端都不报错");

        frame.Data.Should().Equal(
            accessUnit,
            "退回带内就必须退回**完整的一整条**：码流原样（Annex-B、参数集留在里面）");

        logs.Should().Contain(
            line => line.Contains("按带内发", StringComparison.Ordinal),
            "退化必须留痕：不然现场只看到花屏，无从知道是「参数集没解出来」"
            + $"。日志：{string.Join(" / ", logs)}");

        // ⚠️ 这一条是"两个计数必须并排"的又一次应用：**说的是关键帧**，那就得能看出来
        //    有几帧自带参数集、几帧没有 —— 否则"带内这条路成不成立"永远只是个说法。
        logs.Should().Contain(
            line => line.Contains("没有完整 SPS/PPS", StringComparison.Ordinal),
            "它同时是“MF 到底会不会每个关键帧都重复参数集”这个悬案的取证点");
    }

    // ═══════════════════════════════════════════════════════════
    // ③ 认不得的打包方式
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 <b>一个不认得的打包方式必须「回落 + 留痕」，两件事缺一不可。</b>
    ///
    /// <para>
    /// 只回落不留痕 = 中心写错一个连字符（<c>out-of-band</c>），被控端安静地按带内跑，
    /// 而观看端拿着 avc1 的串去解 Annex-B 码流 ⇒ <b>症状在浏览器那边，真因在中心这边</b>。
    /// 只报错不回落 = 一个笔误让整个远控不可用，那更糟。
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unknown_packaging_falls_back_to_in_band_and_says_so()
    {
        var sessionId = "packaging-unknown-" + Guid.NewGuid().ToString("N")[..8];

        var accessUnit = VideoTestVectors.AnnexB(
            VideoTestVectors.RealH264Sps,
            VideoTestVectors.RealH264Pps,
            VideoTestVectors.RealH264IdrHead);

        var encoder = new ScriptedSurfaceEncoder(accessUnit);
        var logs = new List<string>();

        await using var host = NewHost(encoder, logs);

        // ⚠️ 连字符写法：这是最容易出现的一种笔误（契约里是 `outofband`，没有连字符）。
        var started = host.Start(new RemoteStreamRequest(
            RemoteSessionActions.Start, sessionId, MaxFps: 60,
            Packaging: "out-of-band"));

        started.Ok.Should().BeTrue($"不认得的取值只该回落，不该让整路流起不来：{started.Message}");

        var received = await ReadFramesAsync(sessionId, want: 1, timeoutMs: 6000);

        received.Should().HaveCountGreaterThanOrEqualTo(1, $"日志：{string.Join(" / ", logs)}");

        received[0].Header.Description.Should().BeNull("回落的方向是带内 —— 那条路对码流一个字节都不改");
        received[0].Data.Should().Equal(accessUnit);

        logs.Should().Contain(
            line => line.Contains("不是一个已知取值", StringComparison.Ordinal),
            "回落**必须**留痕。否则这个笔误的表现是“浏览器解码器报错”，"
            + $"而没人会想到去查中心下发的那个字符串。日志：{string.Join(" / ", logs)}");

        // ── 纯函数那一层也钉住：两个方向都要判对 ──
        RemoteStreamPackagings.IsKnown(RemoteStreamPackagings.InBand).Should().BeTrue();
        RemoteStreamPackagings.IsKnown(RemoteStreamPackagings.OutOfBand).Should().BeTrue();
        RemoteStreamPackagings.IsKnown(null).Should().BeFalse();
        RemoteStreamPackagings.IsKnown("").Should().BeFalse();
        RemoteStreamPackagings.IsKnown("out-of-band").Should().BeFalse();

        RemoteStreamPackagings.Normalize("out-of-band").Should().Be(
            RemoteStreamPackagings.InBand,
            "回落到带内，不是带外：带内那条路最坏只是“浏览器解不了”（一个布尔值就能说清），"
            + "而带外回落错方向会得到“半个 avcC”这种自相矛盾的东西");
    }

    // ═══════════════════════════════════════════════════════════
    // ④ 「画面没有新帧」不是失败
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥🔥 <b>「没有新帧」既不是丢帧，更不该把这一路收场。</b>
    ///
    /// <para>
    /// 这条守的是一个<b>很具体的现场</b>：展厅里定格在一张图上、没人动鼠标。
    /// DXGI Desktop Duplication 是增量投递的 ⇒ 它会一直回"没有新帧"。
    /// 把这一格当成失败，会同时得到两个错误结论：
    /// </para>
    /// <list type="number">
    ///   <item><c>Dropped</c> 开始涨 ⇒ 页面报"掉了很多帧"，而实际一帧都没丢；</item>
    ///   <item>启动预算按"从没出过帧"判定 ⇒ <b>30 秒后把一路完全正常的流收掉</b>。</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task A_desktop_that_never_changes_is_not_a_drop_and_not_a_failure()
    {
        var sessionId = "packaging-idle-" + Guid.NewGuid().ToString("N")[..8];

        var encoder = new ScriptedSurfaceEncoder(VideoTestVectors.AnnexB(VideoTestVectors.RealH264IdrHead));

        var logs = new List<string>();

        // 抓屏器永远回 null = 桌面内容与指针都没变（这是 DDA 的**约定**，不是错误）。
        var capturer = new StubCapturer { NeverChanges = true };

        // 🔥 **必须把这个抓屏器交给门面**（`CapturerToReport`）：
        //    GPU 那条路上，抓屏器是**编码器自己持有的**（零拷贝要求两者共用同一个 D3D11 设备），
        //    宿主用的就是它 —— 而不是 `capturers` 那个工厂。不交过去，这里拿到的会是一个
        //    "会出画面"的默认抓屏器，于是这条用例会以一种非常难懂的方式红。
        encoder.CapturerToReport = capturer;

        await using var host = NewHost(encoder, logs, capturer);

        var started = host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 60));

        started.Ok.Should().BeTrue($"开始推流本该成功，失败原因：{started.Message}");

        // 给它足够的时间把"没有新帧"跑上个几十轮。
        var received = await ReadFramesAsync(sessionId, want: 1, timeoutMs: 2500);

        received.Should().BeEmpty("桌面一直没变 ⇒ 观看端本来就该停在上一张画面上，而不是收到“重复帧”");

        var status = host.Status;

        status.Should().NotBeNull();
        status!.FramesSent.Should().Be(0);
        status.DroppedFrames.Should().Be(
            0,
            "🔴 「没有新帧」**不是丢帧**。把它算进 Dropped，页面就会报“掉了很多帧”"
            + "—— 而实际一帧都没丢，排查方向会被从“编码器”带到“网络”去");

        encoder.FramesFed.Should().Be(0, "没有新帧就**不该**去喂编码器：喂一个我们不拥有的画面等于无中生有");

        host.IsStreaming.Should().BeTrue(
            "🔴 一路“桌面静止”的流必须继续活着。旧的判据（从没出过帧 ⇒ 30 秒收场）"
            + "会把一台完全正常的机器收掉，而现场完全看不懂为什么");

        logs.Should().Contain(
            line => line.Contains("没有新帧", StringComparison.Ordinal),
            "必须留一句话：否则“推流已开始却一直没有画面”现场没法区分“桌面没动”与“坏了”"
            + $"（而且要说清**没有丢帧**）。日志：{string.Join(" / ", logs)}");
    }

    // ═══════════════════════════════════════════════════════════
    // ⑤ 异步编码的"输出晚一次到"不是丢帧
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥🔥 <b>异步编码器"这一轮没吐出码流"不等于"丢了一帧"。</b>
    ///
    /// <para>
    /// 异步 MFT 的输出<b>晚一次调用才到</b>（M5.4-b 实测：喂 17 帧出 16 帧）
    /// ⇒ 每编 N 帧就有 N-1 轮拿到的是"还没出来"。把那些轮次都记成丢帧，
    /// 得到的是<b>一个虚高的丢帧数</b>：页面报"掉了很多帧"、而画面一帧没缺，
    /// 于是排查方向从"编码器"被带到"网络"。
    /// </para>
    /// <para>
    /// ⚠️ 判据是<b>计数</b>（<see cref="IVideoFrameEncoder.DiscardedFrames"/>），不是失败文案：
    /// 文案一改分类就错。这条用例同时钉住这两点 —— 那个编码器<b>确实返回了 false</b>。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_late_encoder_output_is_not_counted_as_a_dropped_frame()
    {
        var sessionId = "packaging-late-" + Guid.NewGuid().ToString("N")[..8];

        var accessUnit = VideoTestVectors.AnnexB(
            VideoTestVectors.RealH264Sps,
            VideoTestVectors.RealH264Pps,
            VideoTestVectors.RealH264IdrHead);

        // 每一轮都"没吐出来"，但**一帧都没丢**（FramesFed 在涨、DiscardedFrames 不动）。
        // 这正是异步 MFT 的真实形状，只是把频率调到了 100% 让它可测。
        var encoder = new ScriptedSurfaceEncoder(accessUnit) { NeverProduces = true };

        var logs = new List<string>();

        await using var host = NewHost(encoder, logs);

        var started = host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 60));

        started.Ok.Should().BeTrue($"开始推流本该成功，失败原因：{started.Message}");

        // 🔥 **必须先有人连上管道**：`LoopAsync` 在收第 N 帧之前会先等接收端，
        //    等不到就按 `ConnectTimeoutMs`（8 秒）收摊。所以"只等一秒再看状态"
        //    测到的是"还没开始推"，不是"推得怎么样"。
        //    ⇒ 直接复用读帧那条路（它连上之后会一直读到超时），
        //      这样这一段等待同时完成了"接上接收端"与"让它跑一会儿"两件事。
        var received = await ReadFramesAsync(sessionId, want: 1, timeoutMs: 1500);

        received.Should().BeEmpty(
            "这个编码器**永远不吐码流** ⇒ 一帧也发不出去。有帧到达说明读的不是同一路流");

        var status = host.Status;

        status.Should().NotBeNull();
        status!.DroppedFrames.Should().Be(
            0,
            "🔴 编码器只是“还没吐出来”，一帧都没丢。把它算成丢帧，页面就会报“掉了很多帧”"
            + "—— 而那个方向会把人带去查网络");

        encoder.FramesFed.Should().BeGreaterThan(0, "这些帧**确实喂进去了**（所以它们不属于丢帧）");

        host.IsStreaming.Should().BeTrue(
            "「还没出来」是有时限的正常状态，不该把流收掉。"
            + "⚠️ 但如果它真的永远不出来，启动预算那一关会兜住（收场并说明原因）");

        logs.Should().Contain(
            line => line.Contains("不是丢帧", StringComparison.Ordinal),
            "文案必须与真丢帧**分开说**：两种情况的处置完全不同（一个要去查编码器，一个只是等一下）"
            + $"。日志：{string.Join(" / ", logs)}");
    }

    // ═══════════════════════════════════════════════════════════
    // ⑥ 帧头的抓屏时刻由编码器带出来（AV 同步差一帧那处既有缺陷）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 <b>帧头里的"抓到这一帧的时刻"必须是<b>那一帧码流自己</b>的，而不是"编完的现在"。</b>
    ///
    /// <para>
    /// 异步编码器<b>晚一次调用才吐字节</b> ⇒ 这一轮拿到的码流其实是<b>上一帧</b>的。
    /// 在编完之后取时刻，就把"这一帧的到达时刻"贴到了"上一帧的字节"上：
    /// </para>
    /// <list type="number">
    ///   <item>音画同步整体差一帧（30 fps 下 33 ms —— 唇音看得出来）；</item>
    ///   <item>而且<b>没人会往这个方向查</b>：因为 <c>Dropped</c> 同时也在虚高，
    ///         现场会先怀疑网络。</item>
    /// </list>
    ///
    /// <para>
    /// ⚠️ 断言方式刻意是<b>相对关系</b>（"比现在早约 1 秒"）而不是绝对值：
    /// 会话单调时钟的原点（<c>_startQpc</c>）是会话私有的，用例拿不到它 ——
    /// 而"早了多少"是可以独立验证的。
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_frame_timestamp_comes_from_the_encoder_not_from_encode_completion()
    {
        var sessionId = "packaging-ticks-" + Guid.NewGuid().ToString("N")[..8];

        var accessUnit = VideoTestVectors.AnnexB(
            VideoTestVectors.RealH264Sps,
            VideoTestVectors.RealH264Pps,
            VideoTestVectors.RealH264IdrHead);

        // 🔥 编码器把"这一帧码流的抓屏时刻"报成**大约 1 秒前**。
        //    这正是异步 MFT 的形状（只是把延迟放大到可测的量级）：
        //    字节是旧的，而任何"现在取一次"的写法都会把它拉回现在。
        var backdate = TimeSpan.FromSeconds(1);

        var encoder = new ScriptedSurfaceEncoder(accessUnit, backdate);

        var logs = new List<string>();

        await using var host = NewHost(encoder, logs);

        var started = host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 60));

        started.Ok.Should().BeTrue($"开始推流本该成功，失败原因：{started.Message}");

        var received = await ReadFramesAsync(sessionId, want: 3, timeoutMs: 6000);

        received.Should().HaveCountGreaterThanOrEqualTo(1, $"日志：{string.Join(" / ", logs)}");

        var status = host.Status!;

        // "读取这一刻"相对会话开始的墙上时间 —— 与帧头里那个单调时刻**同源**（都从 Begin 起算）。
        var elapsedMs = (DateTimeOffset.UtcNow - status.StartedAt).TotalMilliseconds;

        var last = received[^1];

        last.Header.CapturedMonoMs.Should().BeLessThan(
            (long)elapsedMs - 500,
            "帧头里的时刻必须来自**编码器带出来的那个抓屏瞬间**（它比“现在”早约 1 秒）。"
            + "如果它≈现在，说明有人在编完之后自己取了一次时刻 —— "
            + "那会让 AV 同步整体偏一帧，而排查方向会被虚高的丢帧数带到网络去");

        last.Header.CapturedMonoMs.Should().BeGreaterThan(
            (long)elapsedMs - 2500,
            "但也不能早得离谱：那是“这一帧码流自己那一帧”的时刻，误差量级应该在编码延迟以内");
    }

    // ═══════════════════════════════════════════════════════════
    // ⑦ 屏号以门面为准（"看着 A 的屏幕、实际操作 B"）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴🔴 <b>GPU 那条路上，宿主的屏号/尺寸必须<b>来自门面</b>，宿主不许自己挑。</b>
    ///
    /// <para>
    /// 两个后端的挑屏规则<b>刻意不同</b>：内存路径优先<b>虚拟桌面</b>（GDI 能拼），
    /// GPU 路径优先<b>主屏</b>（DDA 一次只能抓一个 output）。
    /// 若宿主自己也挑一次，多屏机上必然挑到另一块屏 ——
    /// 两个组件各自都"挑对了"，于是<b>没有任何一处报错</b>。
    /// 症状是 <b>看着 A 的屏幕、实际操作 B</b>，而这是本方案里最贵的一类 bug。
    /// </para>
    ///
    /// <para>
    /// 🔸 夹具刻意让"虚拟桌面"和"主屏"是<b>两块不同的屏</b>：一块 100×100 的虚拟桌面、
    /// 一块 2×2 的主屏。宿主若按老规矩挑，就会拿到 100×100。
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_gpu_screens_choice_wins_over_the_hosts_own_screen_picking()
    {
        var sessionId = "packaging-screen-" + Guid.NewGuid().ToString("N")[..8];

        var accessUnit = VideoTestVectors.AnnexB(
            VideoTestVectors.RealH264Sps,
            VideoTestVectors.RealH264Pps,
            VideoTestVectors.RealH264IdrHead);

        var capturer = new StubCapturer { VirtualDesktopFirst = true };

        var encoder = new ScriptedSurfaceEncoder(accessUnit)
        {
            CapturerToReport = capturer,
            ScreenIdToReport = "screen-primary",
            ScreenNameToReport = "主屏（门面挑的）",
            FrameSizeToReport = (2, 2),
        };

        var logs = new List<string>();

        await using var host = NewHost(encoder, logs, capturer);

        var started = host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 60));

        started.Ok.Should().BeTrue($"开始推流本该成功，失败原因：{started.Message}");

        var status = host.Status;

        status.Should().NotBeNull();

        status!.ScreenId.Should().Be(
            "screen-primary",
            "🔴 屏号必须**以门面为准**。若这里是 `screen-virtual`，就说明宿主按老规矩自己挑了一次 —— "
            + "于是“被控端在抓 A、中心以为在抓 B、输入坐标按 B 算”。"
            + "两个组件都没报错，而操作员看到的是“鼠标点的地方和光标不在一个屏幕上”");

        status.Width.Should().Be(2, "尺寸也要跟着门面走：它握的是**真实纹理**的尺寸");
        status.Height.Should().Be(2);

        // 关键帧确实发出去了 ⇒ 上面的结论不是"因为根本没跑起来"而得出的。
        var received = await ReadFramesAsync(sessionId, want: 1, timeoutMs: 6000);

        received.Should().HaveCountGreaterThanOrEqualTo(
            1,
            $"这一路必须真的推起来了，否则上面的断言只是“什么都没发生”。日志：{string.Join(" / ", logs)}");

        received[0].Header.Width.Should().Be(2);
        received[0].Header.Codec.Should().Be(StreamCodecs.H264);
    }

    // ═══════════════════════════════════════════════════════════
    // ⑧ 编码格式协商：把"集合"交到工厂，并把"集合内改判"说出来
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 <b>中心给的是"一个可接受集合"，宿主不许先替它挑一个。</b>
    ///
    /// <para>
    /// 交集有<b>三项</b>输入，而第三项（<b>本次会话的分辨率</b>）只有被控端在
    /// <b>挑屏之后</b>才知道：H.264 硬编封顶 4096、HEVC 封顶 8192。
    /// 反例是现成的 —— 浏览器只支持 h264、机器 h264+hevc、屏 5120×2880
    /// ⇒ <c>h264</c> 恰好不可行，而<b>被排除在首选之外的 <c>hevc</c> 恰好是唯一出路</b>。
    /// </para>
    ///
    /// <para>
    /// ⇒ 所以这条用例钉的是"<b>整个集合到了工厂</b>"。宿主若写成 <c>requested[0]</c>
    /// （看上去更"直接"），在那个反例上工厂连次选都没机会试，
    /// 表现为<b>会话建不起来、日志说"h264 编不了"</b>，而真正的出路就在手上。
    /// </para>
    ///
    /// <para>
    /// 🔸 用例同时钉第二件事：这次改判<b>必须带一句理由</b>。它是一次<b>正确的</b>偏离
    /// （本机没有越出集合），但中心并不知道发生了改判 —— 没有那句话，现场看到的是
    /// "页面写着 H.264、码流却是 HEVC"，而"哪一侧坏了"完全无法判断。
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_negotiated_set_reaches_the_factory_and_a_within_set_change_carries_a_note()
    {
        var sessionId = "codec-note-" + Guid.NewGuid().ToString("N")[..8];

        VideoEncoderRequest? seen = null;

        // 模拟"工厂按序尝试：h264 因为那块屏 >4096 建不起来 ⇒ 落到 hevc"。
        var encoder = new ScriptedSurfaceEncoder(HevcAccessUnit())
        {
            CodecToReport = StreamCodecs.Hevc,
        };

        var logs = new List<string>();

        await using var host = NewHost(encoder, logs, captureRequest: request => seen = request);

        // 中心按「浏览器 ∩ 被控端」算出来的集合：首选 h264、次选 hevc。
        var started = host.Start(new RemoteStreamRequest(
            RemoteSessionActions.Start,
            sessionId,
            MaxFps: 60,
            Codecs: [RemoteCodecs.H264, RemoteCodecs.Hevc]));

        started.Ok.Should().BeTrue($"开始推流本该成功，失败原因：{started.Message}");

        // ── ① 集合原样到工厂（顺序也不能丢：第 0 项是偏好）──
        seen.Should().NotBeNull("工厂必须被调用过一次，否则下面的结论只是在说“什么都没发生”");

        seen!.Value.Codecs.Should().Equal(
            new[] { RemoteCodecs.H264, RemoteCodecs.Hevc },
            "宿主把**整个集合**交出去，而不是替工厂挑一个：格式由目标分辨率决定，"
            + "而分辨率要等挑屏之后才知道 —— 挑屏是工厂在做的事");

        // ── ② 这一路确实推起来了，且实际送出来的是 HEVC ──
        var received = await ReadFramesAsync(sessionId, want: 1, timeoutMs: 6000);

        received.Should().HaveCountGreaterThanOrEqualTo(
            1,
            $"结论不能建立在“什么都没跑起来”上。日志：{string.Join(" / ", logs)}");

        received[0].Header.Codec.Should().Be(
            StreamCodecs.Hevc,
            "帧头上的编码名来自**载荷**（编码器实际交出来的那个），不是来自中心下发的那个");

        // ── ③ 理由必须上报，而且两边名字都在、还要点明没越出集合 ──
        started.CodecNote.Should().NotBeNull(
            "中心首选 h264、本机用了 hevc ⇒ 必须上报改判理由。"
            + "它**不**复用 Reason —— 那个字的语义是“这路会话为什么结束”，与这里正好相反");

        started.CodecNote.Should().Contain(RemoteCodecs.H264)
            .And.Contain(RemoteCodecs.Hevc, "两边的名字都要在，否则看不出是“从哪个改到哪个”");

        started.CodecNote.Should().Contain(
            "没有越出",
            "这句是给现场的关键信息：集合内 ⇒ 观看端的浏览器一定解得了，不必往兼容性方向查");

        logs.Should().Contain(
            line => line.Contains(RemoteCodecs.Hevc, StringComparison.Ordinal),
            $"改判理由也得进本机日志（现场先看的是这里）。日志：{string.Join(" / ", logs)}");
    }

    /// <summary>
    /// <b>照中心首选时，状态上报里不许留一句话。</b>
    ///
    /// <para>
    /// 反面是"每次都填一句"，于是 <c>codecNote</c> 变成一个恒有值的字段 ——
    /// 观看端要么每路会话都挂一条没有信息量的说明，要么学会忽略它；
    /// 两者都会让<b>真正该看的那一次改判</b>被淹没。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Following_the_centres_preference_leaves_no_note()
    {
        var sessionId = "codec-note-silent-" + Guid.NewGuid().ToString("N")[..8];

        var accessUnit = VideoTestVectors.AnnexB(
            VideoTestVectors.RealH264Sps,
            VideoTestVectors.RealH264Pps,
            VideoTestVectors.RealH264IdrHead);

        var encoder = new ScriptedSurfaceEncoder(accessUnit);   // 自称 H.264

        var logs = new List<string>();

        await using var host = NewHost(encoder, logs);

        var started = host.Start(new RemoteStreamRequest(
            RemoteSessionActions.Start,
            sessionId,
            MaxFps: 60,
            Codecs: [RemoteCodecs.H264, RemoteCodecs.Hevc]));

        started.Ok.Should().BeTrue($"开始推流本该成功，失败原因：{started.Message}");

        started.CodecNote.Should().BeNull(
            "实际用的就是集合第 0 项 ⇒ 没什么可说的，别在状态上报里占地方");
    }

    /// <summary>
    /// <b>中心没指定格式时，也要说一句"这是本机自选"。</b>
    ///
    /// <para>
    /// 这一格沉默的含义是"照中心首选"—— 而这里根本没有首选。
    /// 不说的话，日后看到"实际是 HEVC"会被追查成"中心什么时候下发过 HEVC"。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Without_a_centre_choice_the_note_says_it_was_the_machines_own_pick()
    {
        var sessionId = "codec-note-selfpick-" + Guid.NewGuid().ToString("N")[..8];

        var accessUnit = VideoTestVectors.AnnexB(
            VideoTestVectors.RealH264Sps,
            VideoTestVectors.RealH264Pps,
            VideoTestVectors.RealH264IdrHead);

        var encoder = new ScriptedSurfaceEncoder(accessUnit);

        var logs = new List<string>();

        await using var host = NewHost(encoder, logs);

        // ⚠️ 完全不传 Codecs（旧版中心 / 还没升级的那一侧）。
        var started = host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 60));

        started.Ok.Should().BeTrue($"开始推流本该成功，失败原因：{started.Message}");

        started.CodecNote.Should().NotBeNull(
            "中心没说 ⇒ 必须点明“本机自选”，否则日后会被当成“中心下发的”");

        started.CodecNote.Should().Contain("本机自选");
    }

    /// <summary>
    /// 拼一段 <b>HEVC 访问单元</b>（VPS ＋ SPS ＋ PPS ＋ IDR 片），形状与真机一致。
    /// <para>
    /// ⚠️ SPS 是<b>按规格合成</b>的（本机这轮落盘的是 H.264 码流，没有真实 HEVC SPS）——
    /// 详见 <see cref="VideoTestVectors.HevcSps"/> 那一段注释。这个用例要证的是
    /// <b>编码名与理由的流转</b>，不是 HEVC 位流解析的真机一致性（那件事有单独的挂账）。
    /// </para>
    /// </summary>
    private static byte[] HevcAccessUnit()
    {
        var vps = VideoTestVectors.HevcFiller(NalUnitTypes.H265Vps, 0x00, 0x00);
        var pps = VideoTestVectors.HevcFiller(NalUnitTypes.H265Pps, 0x00, 0x00);
        var idr = VideoTestVectors.HevcFiller(NalUnitTypes.H265IdrNLp, 0xAF, 0x08, 0x00, 0x00);

        var sps = VideoTestVectors.HevcSps(
            profileSpace: 0,
            tierFlag: false,
            profileIdc: 1,
            compatibilityFlags: 0x60000000,
            constraintFlags: 0xB00000000000,
            levelIdc: 93);

        return VideoTestVectors.AnnexB(vps, sps, pps, idr);
    }

    // ═══════════════════════════════════════════════════════════
    // 夹具
    // ═══════════════════════════════════════════════════════════

    private static RemoteStreamHost NewHost(
        IVideoFrameEncoder encoder,
        List<string> logs,
        IScreenCapturer? capturer = null,
        Action<VideoEncoderRequest>? captureRequest = null)
        => new(
            capturers: () => capturer ?? new StubCapturer(),

            // 🔥 第 7 步：工厂收到的**那个请求**要能被用例看见。
            //    「中心给的可接受集合有没有真的到工厂」与「编码器选了什么」是两件事，
            //    而前者一旦断掉（宿主自己先挑一个），症状与"工厂挑了别的"长得一模一样。
            encoders: request =>
            {
                captureRequest?.Invoke(request);

                return encoder;
            },

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
    /// 一段字节里有没有 Annex-B 起始码（<c>00 00 01</c>）。
    /// <para>
    /// ⚠️ 刻意手写而不借 FluentAssertions 的 <c>NotContain</c>：那个 API 在集合上的语义是
    /// "不含这些<b>元素</b>"（它看到任何一个是 <c>0x00</c> 就算命中），
    /// 而我们要问的是"不含这个<b>子序列</b>" —— 两件事差得很远。
    /// </para>
    /// </summary>
    private static bool ContainsStartCode(byte[] data)
    {
        for (var i = 0; i + 2 < data.Length; i++)
        {
            if (data[i] == 0x00 && data[i + 1] == 0x00 && data[i + 2] == 0x01) return true;
        }

        return false;
    }

    /// <summary>连上管道读若干帧（读到够数、超时、或对面关掉就返回）。</summary>
    private static async Task<List<RemoteStreamFrame>> ReadFramesAsync(string sessionId, int want, int timeoutMs)
    {
        var frames = new List<RemoteStreamFrame>();

        using var cancel = new CancellationTokenSource(timeoutMs);

        try
        {
            await using var client = new NamedPipeClientStream(
                ".", RemoteStreamProtocol.PipeNameForSession(sessionId), PipeDirection.In, PipeOptions.Asynchronous);

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

    /// <summary>
    /// 最小抓屏器：给一张 2×2 的图，<b>不碰任何平台 API</b>（用例要在非 Windows 上也能编译）。
    /// </summary>
    private sealed class StubCapturer : IScreenCapturer
    {
        private static readonly ScreenDescriptor Primary =
            new("screen-primary", "主屏", 2, 2, IsPrimary: true);

        /// <summary>多一块 100×100 的"虚拟桌面"，而且排在<b>前面</b>。</summary>
        private static readonly ScreenDescriptor Virtual =
            new("screen-virtual", "全部显示器", 100, 100, IsPrimary: false, IsVirtualDesktop: true);

        /// <summary>把虚拟桌面排在前面（内存路径"优先虚拟桌面"就会挑到它）。</summary>
        public bool VirtualDesktopFirst { get; init; }

        /// <summary>永远回"没有新帧"（模拟静止的桌面）。</summary>
        public bool NeverChanges { get; init; }

        public IReadOnlyList<ScreenDescriptor> EnumerateScreens() =>
            VirtualDesktopFirst ? [Virtual, Primary] : [Primary];

        /// <summary>软编路径每帧写进缓冲区的序号（见 <see cref="CaptureAsync"/> 的说明）。</summary>
        private int _tick;

        public Task<CapturedFrame> CaptureAsync(string screenId, CancellationToken cancellationToken = default)
        {
            // 🔥 **软编路径的帧内容必须每帧不同**：M5.5 起有静止检测
            //    （`FrameFingerprint`），像素与上一帧相同的帧会被正确地跳过。
            //    ⚠️ 这一条与 <see cref="NeverChanges"/> 是**两件事**：那个开关管的是
            //    GPU 路径"返回 null = 没有新帧"，与内存缓冲区无关。
            var buffer = new byte[16];

            BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), Interlocked.Increment(ref _tick));

            return Task.FromResult(new CapturedFrame(2, 2, 8, buffer, DateTimeOffset.UtcNow));
        }

        public Task<CapturedSurface?> CaptureSurfaceAsync(
            string screenId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<CapturedSurface?>(NeverChanges
                ? null
                : new CapturedSurface(2, 2, TextureHandle: 1, SurfacePixelFormat.Bgra8, false, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// 脚本化的 <b>GPU 路径</b>编码器：每一轮都交出一段给定的 Annex-B 访问单元。
    ///
    /// <para>
    /// ⚠️ 它<b>不模拟"输出晚一次到"的时序</b>（那需要内部真的排队），
    /// 而是把这个形状做成两个开关：<see cref="NeverProduces"/>（永远"还没出来"）
    /// 与 <see cref="ScriptedSurfaceEncoder"/> 的 <c>backdate</c>（码流属于一秒前那一帧）。
    /// 时序本身由门面自己那套 FIFO 配对的用例覆盖。
    /// </para>
    /// </summary>
    private sealed class ScriptedSurfaceEncoder : ISurfaceFrameEncoder
    {
        private readonly byte[] _accessUnit;
        private readonly TimeSpan _backdate;

        public ScriptedSurfaceEncoder(byte[] accessUnit, TimeSpan backdate = default)
        {
            _accessUnit = accessUnit;
            _backdate = backdate;
        }

        /// <summary>永远"还没吐出来"（但每一帧都确实喂进去了）。</summary>
        public bool NeverProduces { get; init; }

        /// <summary>喂进去多少帧（<c>TryEncodeSurface</c> 被调用且被"接受"的次数）。</summary>
        public long FramesFed { get; private set; }

        /// <summary>
        /// 门面"自己"持有的那个抓屏器 —— <b>GPU 路径上宿主用的就是它</b>
        /// （不是 <c>capturers</c> 那个工厂）。做成可写是因为测试要先建抓屏器、再建编码器。
        /// </summary>
        public IScreenCapturer? CapturerToReport { get; set; }

        public string ScreenIdToReport { get; init; } = "screen-primary";

        public string ScreenNameToReport { get; init; } = "主屏";

        public (int Width, int Height) FrameSizeToReport { get; init; } = (2, 2);

        /// <summary>
        /// 编码器自己声明的参数集（<c>MF_MT_MPEG_SEQUENCE_HEADER</c>）；
        /// <c>null</c> = 读不出来（兜底那条路也走不通）。
        /// </summary>
        public byte[]? ParameterSets { get; init; }

        public string Codec => CodecToReport;

        /// <summary>
        /// 这个编码器<b>自称</b>用的格式。默认 H.264；第 7 步那条"集合内改判"的用例
        /// 把它设成 HEVC，用来模拟"工厂按序尝试后落到了次选"。
        /// <para>
        /// ⚠️ 它同时决定 <c>Codec</c> 与 <c>EncodedStreamPayload.Codec</c> —— 两处必须一致，
        /// 因为真编码器不会出现"自称 H.264、交出来的载荷写着 HEVC"这种状态。
        /// </para>
        /// </summary>
        public string CodecToReport { get; init; } = StreamCodecs.H264;

        public string ScreenId => ScreenIdToReport;

        public string ScreenName => ScreenNameToReport;

        public (int Width, int Height) FrameSize => FrameSizeToReport;

        public IScreenCapturer Capturer => CapturerToReport ?? new StubCapturer();

        public string? LastFailure => NeverProduces
            ? "编码器收下了这一帧，但码流还没出来（异步 MFT 的正常延迟，不是丢帧）。"
            : null;

        /// <summary>它<b>一帧都不丢</b>：没吐出来是因为延迟，不是因为丢弃。</summary>
        public long DiscardedFrames => 0;

        public bool Grayscale { get; set; }

        public bool TryEncodeSurface(
            CapturedSurface surface,
            bool forceKeyframe,
            out EncodedStreamPayload? encoded)
        {
            // ⚠️ 顺序讲究：真实编码器是先"收下"（FramesFed 涨）再决定这一轮吐不吐。
            //    反过来写的话，"收下但没吐"这个形状就造不出来了 —— 而那正是要测的东西。
            FramesFed++;

            if (NeverProduces)
            {
                encoded = null;

                return false;
            }

            encoded = new EncodedStreamPayload(
                Codec: CodecToReport,
                IsKeyframe: true,
                Width: surface.Width,
                Height: surface.Height,
                CapturedUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data: _accessUnit,

                // 门面交出来的 description 恒为 null（转换在 Agent 层做）—— 这里如实照做。
                Description: null,
                CapturedMonoTicks: Stopwatch.GetTimestamp() - (long)(_backdate.TotalSeconds * Stopwatch.Frequency));

            return true;
        }

        public bool TryReadParameterSets(out byte[] parameterSets)
        {
            parameterSets = ParameterSets ?? [];

            return ParameterSets is not null;
        }

        public void Dispose()
        {
        }
    }
}
