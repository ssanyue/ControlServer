using System.Diagnostics;
using ControlServer.Platform.Abstractions;
using ControlServer.Platform.Windows;
using FluentAssertions;
using Xunit.Abstractions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// M5.4-b 第 4 步：<b>GPU 零拷贝管线的公开门面</b>（<c>WindowsGpuFrameEncoder</c>）。
///
/// <para>
/// 🔥 <b>这一层要钉住的不是"能编出字节"，而是三件容易说不清的事</b>：
/// </para>
/// <list type="number">
///   <item><b>关键帧自带全套参数集</b> —— W3C 的 AVC 注册写明 Annex-B 格式下每一个
///         <c>type:"key"</c> 的块都必须自带解码它所需的全套参数集。本仓的丢帧恢复动作是
///         <c>reset()</c> + 等下一个关键帧，一旦这条不成立，重连之后会<b>一直花屏、
///         而两端日志都正常</b>。⇒ 无论带外有没有描述，这条都必须成立。</item>
///   <item><b>本层不填 <c>Description</c></b> —— MF 给的是 Annex-B，而那个字段的契约是
///         avcC/hvcC。<b>"填了"与"码流换成长度前缀"是连体的</b>，只做一半 = configure 失败
///         或花屏。⇒ 用例钉住"这一层交出去的一律是 <c>null</c>"，把转换与接线留给第 6 步。</item>
///   <item><b>报告不许说谎</b> —— "零拷贝成立"必须与"设备句柄真的开过一次"这个可独立读出
///         的事实一致；"多了一步显存内拷贝"必须被如实写出来。</item>
/// </list>
///
/// <para>
/// ⚠️ <b>硬件前提</b>：要有 H.264 硬件编码器 MFT ＋ 一块能建 DDA 的屏。
/// 前提不成立 ⇒ 打印原因并返回（<b>不造假 FAIL</b>）；枚举到了却建不起来 ⇒ <b>必须 FAIL</b>
/// （那是我们的缺陷）。两者的分界由 <see cref="GpuPipelineVerdict.IsEnvironmentProblem"/> 给出。
/// </para>
/// </summary>
[Collection(DesktopDuplicationCollection.Name)]
public sealed class WindowsGpuFrameEncoderSmokeTests
{
    /// <summary>一轮跑多少帧。</summary>
    private const int FramesPerPass = 40;

    /// <summary>
    /// 灰度翻转之后，最多往后看几帧里必须出现关键帧。
    /// <para>
    /// 🔥 <b>为什么不是"下一帧就必须是"</b>：异步编码器有 lookahead，<c>RequestKeyframe</c>
    /// 是给它下一次喂帧时看的，而这一帧编出来往往要到下一次调用才取得到。
    /// 钉"下一帧"会因为这条固有延迟而偶发假红 —— 那比不测更糟。
    /// </para>
    /// </summary>
    private const int KeyframeGrace = 4;

    /// <summary>
    /// 等 DDA 交第一张画面，最多等多久。
    ///
    /// <para>
    /// 🔥 <b>为什么需要"等"而不是"问一次"</b>：DDA 是<b>增量投递</b> ——
    /// 桌面和指针都不动时 <c>AcquireNextFrame</c> 等到超时就返回 <c>null</c>
    /// （<c>WindowsDxgiScreenCapturer.DefaultAcquireTimeoutMs = 15</c>）。
    /// 早先这里是"每轮重新抓一次"，40 轮 × 15 ms ≈ 0.6 秒 ——
    /// 静止的桌面上<a>必然</a>一帧都拿不到，于是用例报"这条链子实际上没通"。
    /// 那是把<b>不受控的输入</b>当成了失败，也正是本机"连跑四轮都红、
    /// 每次红的用例还不一样"的成因。
    /// </para>
    /// </summary>
    private const int FirstFrameWaitMs = 2_500;

    private readonly ITestOutputHelper _output;

    /// <summary>
    /// 已经等到的那张画面（静止桌面上 DDA 不会再给第二张）。
    /// <para>⚠️ 按 <see cref="WindowsGpuFrameEncoder"/> 实例缓存：换一个实例就是另一条 DDA 链。</para>
    /// </summary>
    private CapturedSurface? _held;

    /// <summary><see cref="_held"/> 属于哪条管线。</summary>
    private WindowsGpuFrameEncoder? _heldFor;

    public WindowsGpuFrameEncoderSmokeTests(ITestOutputHelper output) => _output = output;

    // ═══════════════════════════════════════════════════════════
    // 纯函数：尺寸/格式闸门（不需要任何设备）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 尺寸闸门拦的是三类"一定会失败、但失败信息会把排查带偏"的输入。
    /// <para>
    /// 🔥 其中最要紧的是 <b>H.264 超 4096</b>：不拦的话，它会在编码器枚举那一步
    /// 以"本机枚举到 N 个 H.264 硬件编码器，但每一个都建不起来"的形式报出来 ——
    /// 那句话把人往<b>驱动/权限</b>方向带，而真因只是<b>格式本身的上限</b>。
    /// </para>
    /// </summary>
    [Fact]
    public void The_geometry_gate_turns_a_format_limit_into_an_actionable_sentence()
    {
        // ── 通行 ──
        WindowsGpuFrameEncoder.TryValidateGeometry(VideoCodec.H264, 3840, 2160, out var ok)
            .Should().BeTrue("4K 在 H.264 的上限之内");

        WindowsGpuFrameEncoder.TryValidateGeometry(VideoCodec.Hevc, 7680, 4320, out _)
            .Should().BeTrue("8K 是 HEVC 那一档的目标尺寸");

        WindowsGpuFrameEncoder.TryValidateGeometry(VideoCodec.Hevc, 8192, 8192, out _)
            .Should().BeTrue("上限本身是闭区间的右端");

        // ── 4K 之上：H.264 不行，HEVC 行，而且**话里必须点出这条路** ──
        WindowsGpuFrameEncoder.TryValidateGeometry(VideoCodec.H264, 4320, 2160, out var tooWide)
            .Should().BeFalse();

        tooWide.Should().Contain("4096").And.Contain("HEVC",
            "只说\"超上限\"等于让人自己去查该换什么；这条错误信息的价值全在\"换 HEVC\"这四个字上");

        // ⚠️ 这条要的是"偶数但超过上限"——用 8193 会先撞上奇数那道闸门，
        //    于是拿到的原因是"奇数"而不是"超上限"。**闸门是有先后的**，用例的输入必须体现这一点。
        WindowsGpuFrameEncoder.TryValidateGeometry(VideoCodec.Hevc, 8200, 4320, out var beyondHevc)
            .Should().BeFalse();

        beyondHevc.Should().Contain("8192").And.Contain("不覆盖");

        // ── 奇数：NV12 是 4:2:0 半平面，宽高必须都为偶数 ──
        WindowsGpuFrameEncoder.TryValidateGeometry(VideoCodec.H264, 1921, 1080, out var odd)
            .Should().BeFalse();

        odd.Should().Contain("偶数").And.Contain("1921");

        // ── 退化尺寸 ──
        WindowsGpuFrameEncoder.TryValidateGeometry(VideoCodec.H264, 1, 1080, out var tiny)
            .Should().BeFalse();

        tiny.Should().Contain("太小");

        // ── 通过时**不许留下半句话**（调用方会把它拼进日志）──
        WindowsGpuFrameEncoder.TryValidateGeometry(VideoCodec.H264, 1920, 1080, out var clean)
            .Should().BeTrue();

        clean.Should().BeNull("通过了就没有原因 —— 留一句话会让调用方以为出了什么事");
    }

    /// <summary>
    /// 🔥 <b>回退判决必须能区分"环境"与"我们没做/我们坏了"</b>。
    ///
    /// <para>
    /// 这决定的是<b>截然相反的两个动作</b>：被控机上跑着别的抓屏工具属于环境
    /// （安静回落 MJPEG，用户自己关掉那个程序就能用 GPU），
    /// 而"枚举到了硬编却建不起来"是缺陷（必须进告警）。
    /// 若两者都被表达成一句"GPU 不可用"，现场只能猜 —— 这正是方案 §3.5 反复强调的那条纪律。
    /// </para>
    /// </summary>
    [Fact]
    public void The_verdict_separates_an_environment_problem_from_our_own_defect()
    {
        new GpuPipelineVerdict(GpuPipelineStage.Screen, "x").IsEnvironmentProblem
            .Should().BeTrue("会话里没有桌面 / 请求的屏不存在：换台机器或换个屏就好，不是缺陷");

        new GpuPipelineVerdict(GpuPipelineStage.Capture, "x").IsEnvironmentProblem
            .Should().BeTrue("屏被抓屏工具占着：用户自己关掉那个程序就好");

        new GpuPipelineVerdict(GpuPipelineStage.ColorConvert, "x").IsEnvironmentProblem
            .Should().BeFalse("转换器建不起来 = 我们这条链子上的问题，不许被当成\"跳过\"");

        new GpuPipelineVerdict(GpuPipelineStage.Encode, "x").IsEnvironmentProblem
            .Should().BeFalse("枚举到了硬编却建不起来，是我们的事");

        new GpuPipelineVerdict(GpuPipelineStage.Unexpected, "x").IsEnvironmentProblem
            .Should().BeFalse("抛异常永远算缺陷那一侧");

        // 🔥 ToString 要能直接进日志（否则现场还得自己拼串，而拼串就是"再抄一遍"）。
        new GpuPipelineVerdict(GpuPipelineStage.Encode, "硬编码器建不起来")
            .ToString().Should().Be("[Encode] 硬编码器建不起来");

        // ⚠️ 非 Windows 是"平台"而不是"环境"：它永远不会因为用户做什么而变好。
        new GpuPipelineVerdict(GpuPipelineStage.Platform, "x").IsEnvironmentProblem
            .Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════
    // 真机：整条链子
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>本步最要紧的一条</b>：真抓屏 → 真转 NV12 → 真硬编，
    /// 并钉住"每个关键帧都自带全套参数集"。
    /// </summary>
    [Fact]
    public void Every_keyframe_carries_its_own_parameter_sets()
    {
        using var encoder = TryBuild(out var verdict);

        if (encoder is null)
        {
            _output.WriteLine($"GPU 管线建不起来（前提不成立）：{verdict}");

            return;
        }

        var run = RunFrames(encoder, FramesPerPass);

        if (run.Skipped)
        {
            // 🔥 **前提不成立**，不是缺陷：DDA 一张画面都没交出来（桌面全程静止）。
            //    这里必须"跳过并说清原因"，不许假 FAIL —— 见 FirstFrameWaitMs 的说明。
            _output.WriteLine($"DDA 一张画面都没交出来 ⇒ 前提不成立，跳过：{run.Note}");

            return;
        }

        var produced = run.Produced;

        produced.Should().NotBeEmpty(
            $"DDA 交付了画面，但 {run.Rounds} 轮里编码器连一帧都没吐出来 —— 这条链子实际上没通。"
            + encoder.Describe());

        var keyframes = produced.Where(f => f.IsKeyframe).ToList();

        keyframes.Should().NotBeEmpty("编码器的第一个帧必然是关键帧");

        _output.WriteLine($"编出 {produced.Count} 帧，其中关键帧 {keyframes.Count} 个"
            + $"（下标 {string.Join(",", Enumerable.Range(0, produced.Count).Where(i => produced[i].IsKeyframe))}）");

        foreach (var payload in produced)
        {
            payload.Width.Should().Be(encoder.FrameSize.Width);
            payload.Height.Should().Be(encoder.FrameSize.Height);

            payload.Codec.Should().Be(StreamCodecs.H264);

            // ⚠️ 本层**不许**把 Annex-B 塞进 avcC 的槽（理由见下面的用例）。
            payload.Description.Should().BeNull();
        }

        // 🔥 逐字节数报出来 —— 这一格是"2160×1440 的关键帧到底多大"的现场证据。
        var sizes = produced.Select(f => f.Data.Length).ToList();

        _output.WriteLine($"字节数：最小 {sizes.Min()} / 最大 {sizes.Max()} / 合计 {sizes.Sum()}"
            + $"（约 {(double)sizes.Sum() / produced.Count / 1024:F1} KB/帧）");

        for (var index = 0; index < produced.Count; index++)
        {
            var payload = produced[index];
            var types = BitstreamPeek.H264NalTypes(payload.Data);

            if (!payload.IsKeyframe)
            {
                types.Should().NotContain(7, "SPS 不该出现在普通帧里");

                continue;
            }

            _output.WriteLine($"  第 {index} 帧（{payload.Data.Length} 字节）的 NAL："
                + string.Join(",", types.Select(BitstreamPeek.H264NalName)));

            types.Should().Contain(7, "关键帧必须自带 SPS（否则重连/丢帧恢复之后会一直花屏）");
            types.Should().Contain(8, "关键帧必须自带 PPS");

            // 🔥 关键帧必须是 **IDR**（类型 5）：本批定的丢帧恢复动作是
            //    `reset()` → 等下一个关键帧 → 重新 configure()，而那个动作成立的前提是
            //    "这个关键帧不参考任何更早的帧"。非 IDR 的 I 帧（靠 recovery point SEI 表达）
            //    不满足这条 ⇒ 它会以"重连之后前几帧发花"的形式表现，且两端日志都正常。
            types.Should().Contain(5, "关键帧应当是 IDR 片（能独立解码）");

            types.Should().Contain(t => t == 1 || t == 5, "关键帧里必须至少有一个片");

            var sliceIndex = types.FindIndex(t => t is 1 or 5);

            types.IndexOf(7).Should().BeLessThan(sliceIndex,
                "参数集必须在片数据**之前** —— 放在后面，解码器解析这一帧时手上还没有它");

            types.IndexOf(8).Should().BeLessThan(sliceIndex);
        }

        // ── 带外那份（有就用，没有也合法）──
        var hasOutOfBand = encoder.TryReadParameterSets(out var parameterSets);

        _output.WriteLine(hasOutOfBand
            ? $"带外参数集：{parameterSets.Length} 字节，前 8 字节 {Hex(parameterSets, 8)}"
            : $"没有带外参数集（已问 {FramesPerPass} 次）—— 走 Annex-B 内联那条路");

        if (hasOutOfBand)
        {
            BitstreamPeek.LooksLikeAnnexB(parameterSets)
                .Should().BeTrue("MF 给的是 Annex-B；**它不能直接当 WebCodecs 的 description 用**");

            BitstreamPeek.H264NalTypes(parameterSets).Should().Contain(7);
        }

        _output.WriteLine(encoder.Describe());
    }

    /// <summary>
    /// 🔥🔥 <b>把「MF 到底会不会每个关键帧都重复 SPS/PPS」从悬案变成读数 —— 靠把样本量凑够。</b>
    ///
    /// <para>
    /// 🔴 <b>为什么上面那条 <c>Every_keyframe_carries_its_own_parameter_sets</c> 不够</b>：
    /// 它用的是默认 <c>gopSize = 60</c>，而一轮只跑 40 帧 ——
    /// 于是它<b>每次只看到一个关键帧</b>。「这个关键帧带 SPS」与「每个关键帧都带 SPS」
    /// 在那里是同一件事，读不出区别。
    /// </para>
    ///
    /// <para>
    /// 本仓为此<b>在注释里留下过两种相反的说法</b>：§3.4.2 说"MF 在每个关键帧都重复"
    /// （证据是两个关键帧：第 0 与第 15 帧的 NAL 都是 <c>9,7,8,5</c>），
    /// 而 <c>VideoParameterSets.cs</c> 里曾写"落盘的 30 帧里 SPS 只出现 1 次，
    /// 所以带内那条路不成立"—— 后者正是从<b>29 P + 1 I</b> 那样一个只有一个关键帧的
    /// 样本里读出来的。
    /// </para>
    ///
    /// <para>
    /// ⇒ 本轮把样本再抬一个量级：<c>gopSize = 4</c> ⇒ 8 个关键帧，逐帧验。
    /// （两个样本已经能证伪，这一档是<b>确认并留证</b>：换机型时它就是新的第一手读数。）
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>它直接决定带内这个默认档安不安全</b>：W3C 的 AVC 注册要求每个
    /// <c>type:"key"</c> 的块自带"解码它里面所有 NAL 所需的全套参数集"，
    /// 而带内那条路的<b>全部内容</b>就是"参数集留在码流里、帧头不带 description"。
    /// 万一某个关键帧不带 SPS，观看端在中途 <c>reset()</c> 之后就再也 configure 不起来
    /// —— 症状是"重连之后一直花屏"，而两端日志都正常。
    /// </para>
    ///
    /// <para>
    /// ⇒ 做法：<c>gopSize = 4</c> ＋ 80 轮 ⇒ 十几个关键帧，然后<b>逐帧</b>检查。
    /// 读数同时对应 <c>RemoteStreamHost</c> 那两个计数（有参数集的关键帧 / 没有的）。
    /// </para>
    /// </summary>
    [Fact]
    public void A_small_gop_proves_the_parameter_sets_repeat_on_every_keyframe()
    {
        // GOP 压到 4：60 帧的默认值下，80 轮里只有 2 个关键帧 —— 那正是"上一个样本"
        // 的毛病（样本量不够，读不出"会不会重复"）。
        using var encoder = TryBuild(out var verdict, gopSize: 4);

        if (encoder is null)
        {
            _output.WriteLine($"GPU 管线建不起来（前提不成立）：{verdict}");

            return;
        }

        var run = RunFrames(encoder, frames: 80);

        if (run.Skipped)
        {
            _output.WriteLine($"DDA 一张画面都没交出来 ⇒ 前提不成立，跳过：{run.Note}");

            return;
        }

        var produced = run.Produced;

        if (produced.Count == 0)
        {
            // 画面喂进去了却一帧没出 ⇒ 这是链子坏了，不许当"前提不成立"糊过去。
            produced.Should().NotBeEmpty(
                $"DDA 交付了画面，但 80 轮里一帧都没编出来。{encoder.Describe()}");
        }

        var keyframes = produced.Where(f => f.IsKeyframe).ToList();

        _output.WriteLine($"编出 {produced.Count} 帧，其中关键帧 {keyframes.Count} 个。");

        if (keyframes.Count < 2)
        {
            // ⚠️ 前提不成立（例如桌面全程没变化，只拿到一两帧）⇒ 跳过而**不是**假红。
            _output.WriteLine("关键帧少于 2 个 ⇒ 样本量不足，读不出\"会不会重复\"—— 跳过（这不是代码缺陷）。");

            return;
        }

        var withSps = 0;

        for (var index = 0; index < keyframes.Count; index++)
        {
            var types = BitstreamPeek.H264NalTypes(keyframes[index].Data);

            if (types.Contains(7)) withSps++;

            types.Should().Contain(7, $"第 {index + 1} 个关键帧必须自带 SPS");
            types.Should().Contain(8, $"第 {index + 1} 个关键帧必须自带 PPS");
        }

        _output.WriteLine($"自带 SPS 的关键帧：{withSps} / {keyframes.Count}"
            + (withSps == keyframes.Count
                ? " ⇒ 带内那条路（本批默认）在这个编码器上成立。"
                : " ⇒ ⚠️ 有关键帧不带 SPS：带内那条路会漏参数集，届时必须走带外。"));
    }

    /// <summary>
    /// 🔴 <b>分层判据：平台层不产 <c>Description</c>。</b>
    ///
    /// <para>
    /// MF 给的参数集是 <b>Annex-B</b>（带起始码），而 <c>EncodedStreamPayload.Description</c>
    /// 的契约是 <b>avcC</b>（长度前缀的 <c>AVCDecoderConfigurationRecord</c>）。
    /// 直接塞进去就会出现"<b>贴了 avcC、码流却还是 Annex-B</b>"—— 按 W3C 注册，
    /// 那两件事是<b>连体</b>的：只换一半 ⇒ <c>configure()</c> 失败或花屏。
    /// </para>
    /// <para>
    /// ⇒ 正确做法是"转换与改封装一起做"，而那份转换是纯函数、住在
    /// <c>ControlServer.Agent.Video</c>（<b>平台层不做它</b>）。本用例把这条边界钉死：
    /// <b>本层交出去的 <c>Description</c> 恒为 <c>null</c>，参数集只从
    /// <c>TryReadParameterSets()</c> 出去。</b>
    /// </para>
    /// </summary>
    [Fact]
    public void The_platform_layer_never_fills_the_avcC_slot_with_annex_b_bytes()
    {
        using var encoder = TryBuild(out var verdict);

        if (encoder is null)
        {
            _output.WriteLine($"GPU 管线建不起来（前提不成立）：{verdict}");

            return;
        }

        var run = RunFrames(encoder, FramesPerPass);

        if (run.Skipped)
        {
            _output.WriteLine($"DDA 一张画面都没交出来 ⇒ 前提不成立，跳过：{run.Note}");

            return;
        }

        var produced = run.Produced;

        if (produced.Count == 0)
        {
            produced.Should().NotBeEmpty(
                $"DDA 交付了画面，但 {FramesPerPass} 轮里一帧都没编出来。{encoder.Describe()}");
        }

        produced.Should().OnlyContain(f => f.Description == null);

        // 🔥 反过来的那一半：**参数集不能两头都不在**。
        //
        //    带外没有 ⇒ 必须靠码流内联（上面那条用例已逐帧验过）；
        //    带外有   ⇒ 码流里也**照样**有（MF 每个关键帧都重复，已实测）。
        //    ⇒ 无论哪条路，"解码器手上一定拿得到参数集"这件事都必须成立。
        var hasOutOfBand = encoder.TryReadParameterSets(out var parameterSets);

        var keyframe = produced.FirstOrDefault(f => f.IsKeyframe);

        keyframe.Should().NotBeNull();

        var inline = BitstreamPeek.H264NalTypes(keyframe!.Data);

        _output.WriteLine(hasOutOfBand
            ? "带外有参数集，且码流内联也带着 —— 两条路都在，随便走哪条都能解"
            : "带外没有参数集 ⇒ 只能走内联那条路，而内联确实带着");

        inline.Should().Contain(7);
        inline.Should().Contain(8);
    }

    /// <summary>
    /// 🔥 <b>灰度开关必须"立刻"生效</b>（下一个关键帧），否则观众看到的是
    /// "慢慢从彩色褪成灰度"—— 历史帧全是彩色的，那看起来就像坏了。
    ///
    /// <para>
    /// 判据刻意<b>不</b>钉"下一帧就必须是关键帧"：异步编码器有 lookahead，
    /// <c>RequestKeyframe</c> 是下一次喂帧时才被读到的。⇒ 给几帧的宽限，
    /// 同时用一个**很大的 GOP**（300）保证"自然关键帧"在这段时间里根本不会出现 ——
    /// 于是"出现了关键帧"就<b>只能</b>是翻转造成的。
    /// </para>
    /// </summary>
    [Fact]
    public void Flipping_grayscale_opens_a_new_gop_right_away()
    {
        using var encoder = TryBuild(out var verdict, gopSize: 300);

        if (encoder is null)
        {
            _output.WriteLine($"GPU 管线建不起来（前提不成立）：{verdict}");

            return;
        }

        // ── 先跑一段，确认"没有外力就不会有关键帧"──
        var beforeRun = RunFrames(encoder, FramesPerPass);

        if (beforeRun.Skipped)
        {
            _output.WriteLine($"DDA 一张画面都没交出来 ⇒ 前提不成立，跳过：{beforeRun.Note}");

            return;
        }

        var before = beforeRun.Produced;

        if (before.Count < 2)
        {
            _output.WriteLine("帧太少，测不出这条 —— 前提不成立，跳过。");

            return;
        }

        before[0].IsKeyframe.Should().BeTrue("编码器的第一帧必然是关键帧");

        before.Skip(1).Should().OnlyContain(f => !f.IsKeyframe,
            "GOP 设成 300 之后，40 帧之内不该出现第二个自然关键帧 —— "
            + "否则下面\"翻转引起关键帧\"的结论就不成立（可能是自然关键帧撞上了）");

        // ── 翻一下 ──
        encoder.Grayscale = true;

        var after = RunFrames(encoder, KeyframeGrace + 2).Produced;

        after.Should().NotBeEmpty("翻转之后必须还能继续出帧");

        _output.WriteLine($"翻转后 {after.Count} 帧：{string.Join(",", after.Select(f => f.IsKeyframe ? "关键帧" : "P"))}");

        after.Take(KeyframeGrace).Should().Contain(f => f.IsKeyframe,
            $"翻转灰度必须重开 GOP（最多等 {KeyframeGrace} 帧）—— 否则观众看到的是"
            + "\"慢慢褪色\"，而那看起来像坏了");

        // ⚠️ 关掉也要重开一次（从灰切回彩色同样是内容不连续）。
        encoder.Grayscale = false;

        var back = RunFrames(encoder, KeyframeGrace + 2).Produced;

        _output.WriteLine($"关回彩色后 {back.Count} 帧：{string.Join(",", back.Select(f => f.IsKeyframe ? "关键帧" : "P"))}");

        back.Take(KeyframeGrace).Should().Contain(f => f.IsKeyframe);
    }

    /// <summary>
    /// 🔥 <b>报告不许说谎</b>：<c>Describe()</c> 是现场唯一能看见"这条链子到底长什么样"的东西，
    /// 所以它的每一句话都必须与一个<b>可以被独立读出来</b>的事实一致。
    /// </summary>
    [Fact]
    public void The_report_matches_the_flags_it_talks_about()
    {
        using var encoder = TryBuild(out var verdict);

        if (encoder is null)
        {
            _output.WriteLine($"GPU 管线建不起来（前提不成立）：{verdict}");

            return;
        }

        RunFrames(encoder, 5);

        var report = encoder.Describe();

        _output.WriteLine(report);

        var capturer = encoder.Capturer.Should().BeOfType<WindowsDxgiScreenCapturer>().Subject;

        // ① 零拷贝那句必须与"设备句柄真的开过"一致
        report.Should().Contain(
            capturer.GpuContext!.DeviceHandleVerified
                ? "零拷贝那条链子是真的"
                : "管理器手上可能没设备",
            "报告里对零拷贝的断言必须来自那个可独立读出的事实，不能是\"我们觉得应该没问题\"");

        report.Should().Contain(encoder.DeviceHandleVerified switch
        {
            true => "零拷贝那条链子是真的",
            false => "管理器手上可能没设备",
        });

        // ② 多出来的那一步必须被如实写出来（静默退化正是本方案反复踩的坑）
        report.Should().Contain(encoder.UsesIntermediateCopy ? "退化成显存内一次 CopyResource" : "输入视图直接建在");

        // ③ 尺寸与帧数
        var (width, height) = encoder.FrameSize;

        report.Should().Contain($"{width}×{height}");
        report.Should().Contain($"编出 {encoder.FramesEncoded} 帧");

        // ④ 编出过帧就不许同时说"一帧都没出来过"
        if (encoder.FramesEncoded > 0)
        {
            report.Should().NotContain("一帧都没出来过");
        }

        // ⑤ 抓屏适配器必须点名（"抓到全黑帧却不报错"那类故障第一步就问它）
        report.Should().Contain("抓屏适配器");
    }

    /// <summary>
    /// 🔥 <b>释放之后还在推帧，必须是一句能读的结论，而不是崩溃、也不许与"编码器忙"混为一谈。</b>
    ///
    /// <para>
    /// ⚠️ 本用例刻意<b>喂一张合成的 2×2 空纹理</b>（<c>TextureHandle = 0</c>），
    /// 而不是"先抓一帧、Dispose 之后再喂那一帧"：
    /// 后者在守卫被写坏时会去解引用一段<b>已释放的纹理</b>（进程级崩溃，
    /// 会把整个测试宿主一起带走）；用合成面的话，守卫写坏只会退化成
    /// "尺寸太小"—— 用例仍然是红的，但不会摧毁其余用例。
    /// </para>
    /// </summary>
    [Fact]
    public void Feeding_a_frame_after_dispose_says_why_without_throwing()
    {
        var encoder = TryBuild(out var verdict);

        if (encoder is null)
        {
            _output.WriteLine($"GPU 管线建不起来（前提不成立）：{verdict}");

            return;
        }

        encoder.Dispose();

        // 🔸 幂等：托盘在退出路径上会重复走 release，第二次不该炸。
        var repeat = () => encoder.Dispose();

        repeat.Should().NotThrow("Dispose 必须能被调用两次（退出路径上很容易重复走一遍）");

        var dummy = new CapturedSurface(
            Width: 2,
            Height: 2,
            TextureHandle: 0,
            Format: SurfacePixelFormat.Bgra8,
            IsCursorIncluded: false,
            CapturedAt: DateTimeOffset.UtcNow);

        var act = () => encoder.TryEncodeSurface(dummy, forceKeyframe: false, out _);

        act.Should().NotThrow("推流循环绝不接受异常 —— 异常一旦逃出去，整条流就静默死掉了");

        encoder.TryEncodeSurface(dummy, forceKeyframe: false, out var payload).Should().BeFalse();

        payload.Should().BeNull();

        encoder.LastFailure.Should().Contain("已经释放",
            "这是调用方的生命周期错误，与\"编码器忙\"完全不同 —— 两者混成一句，"
            + "\"释放之后还在推帧\"就永远查不出来");
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 建一条真的能跑的 GPU 管线；前提不成立时返回 <c>null</c> 并写下原因。
    ///
    /// <para>
    /// 🔥 <b>"跳过"与"FAIL"的分界由 <see cref="GpuPipelineVerdict.IsEnvironmentProblem"/> 给出</b>：
    /// 屏被占着 / 会话没桌面 ⇒ 跳过；<b>枚举到了硬编却建不起来 ⇒ 抛出去让它红</b>。
    /// 没有这条分界，一个接线错误会以"本机没有硬编"的姿态被永久跳过。
    /// </para>
    /// </summary>
    private WindowsGpuFrameEncoder? TryBuild(out GpuPipelineVerdict? verdict, int gopSize = 60)
    {
        verdict = null;

        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("非 Windows —— 前提不成立，跳过。");

            return null;
        }

        // ⚠️ 用`MediaFoundationEncoders`那条已验收过的探针判"有没有硬编"，
        //    **不是**用"TryCreate 失败"反推 —— 理由见 DxgiHardwareEncodeSmokeTests 里的同名注释。
        var available = MediaFoundationEncoders.TryEnumerateHardware();

        if (available is null)
        {
            _output.WriteLine("查不了本机的硬件编码器（mfplat.dll 不可用）—— 前提不成立，跳过。");

            return null;
        }

        var names = available
            .Where(e => e.Codec == VideoCodec.H264)
            .Select(e => e.FriendlyName)
            .Distinct()
            .ToList();

        if (names.Count == 0)
        {
            _output.WriteLine("本机没有 H.264 硬件编码器 —— 前提不成立，跳过（这不是代码缺陷）。");

            return null;
        }

        _output.WriteLine($"本机 H.264 硬件编码器：{string.Join(" / ", names)}");

        var encoder = WindowsGpuFrameEncoder.TryCreate(
            Process.GetCurrentProcess().SessionId,
            VideoCodec.H264,
            fps: 30,
            out verdict,
            screenId: null,
            grayscale: false,
            gopSize: gopSize);

        if (encoder is not null) return encoder;

        if (verdict!.IsEnvironmentProblem)
        {
            _output.WriteLine($"这台机器的抓屏环境不满足前提 —— 跳过：{verdict}");

            return null;
        }

        // 🔥 枚举到了硬编却建不起来 = **我们的缺陷**。这里必须红，不许安静地跳过。
        throw new Xunit.Sdk.XunitException($"本机有 H.264 硬编，但 GPU 管线建不起来：{verdict}");
    }

    /// <summary>
    /// 跑若干帧「喂一帧 → 编一帧」，把编出来的那些帧带回来。
    ///
    /// <para>
    /// 🔥 <b>为什么先"等到一张画面"，然后就<b>反复喂这一张</b></b>：
    /// 生产里"桌面静止"是最常见的那一档输入（每帧内容完全一样），
    /// 编码器要验的正是这一档。而 DDA 的变化是<b>事件驱动</b>的 ——
    /// 早先"每轮重新抓一次"的写法在静止桌面上必然拿到 0 帧，
    /// 于是这条用例会以"这条链子实际上没通"的姿态连红四轮，而链路本身是好的。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>所以返回值必须把"有没有画面"和"有没有码流"分开报</b>（<see cref="FrameRun"/>）：
    /// 前者不是我们能决定的（<b>前提</b>），后者才是。
    /// </para>
    /// </summary>
    private FrameRun RunFrames(WindowsGpuFrameEncoder encoder, int frames)
    {
        var produced = new List<EncodedStreamPayload>();

        var surface = EnsureSurface(encoder, out var note);

        if (surface is null) return new FrameRun(produced, Delivered: false, frames, note);

        // 给编码器**真实的时间**：它是异步 MFT，上一帧没消化完就来下一帧会被当成"忙"丢掉。
        // 生产是 30 fps（帧间 33 ms），这里按每帧 200 ms 给预算 —— 宽 6 倍，
        // 既留够突发，又保证"喂不满"是一个真信号而不是循环跑太快。
        //
        // ⚠️ 目标是**产出 frames 帧**，不是"调 frames 次"：调多少次由编码器的忙闲决定，
        //    而"编出多少帧"才是下面那些断言（每个关键帧带参数集 / GOP 重开）的分母。
        var clock = Stopwatch.StartNew();

        var budget = TimeSpan.FromMilliseconds(1_000 + (frames * 200L));

        while (produced.Count < frames && clock.Elapsed < budget)
        {
            if (encoder.TryEncodeSurface(surface, forceKeyframe: false, out var payload) && payload is not null)
            {
                produced.Add(payload);
            }

            if (produced.Count < frames) Thread.Sleep(1);
        }

        return new FrameRun(produced, Delivered: true, frames, null);
    }

    /// <summary>
    /// 等到 DDA 交第一张画面（同一个用例里只等一次，之后一直复用）。
    /// </summary>
    private CapturedSurface? EnsureSurface(WindowsGpuFrameEncoder encoder, out string? note)
    {
        note = null;

        // 🔥 换了一条管线就是另一条 DDA 链 —— 上一张纹理属于旧设备，不能再喂。
        if (!ReferenceEquals(_heldFor, encoder))
        {
            _held = null;
            _heldFor = null;
        }

        if (_held is not null) return _held;

        var capturer = (WindowsDxgiScreenCapturer)encoder.Capturer;

        // 用**主屏**（与门面自己挑的那块一致）—— 这条链子一次只能抓一块屏。
        var screen = capturer.EnumerateScreens().FirstOrDefault(s => s.IsPrimary)
            ?? capturer.EnumerateScreens().FirstOrDefault(s => !s.IsVirtualDesktop);

        if (screen is null)
        {
            note = "枚举不到可抓的屏";

            return null;
        }

        var clock = Stopwatch.StartNew();

        while (clock.ElapsedMilliseconds < FirstFrameWaitMs)
        {
            var surface = capturer.TryCaptureSurface(screen.Id, CancellationToken.None, out var failure);

            if (failure is not null)
            {
                note = $"抓屏失败：{failure}";

                return null;
            }

            if (surface is null) continue;

            _held = surface;
            _heldFor = encoder;

            _output.WriteLine($"DDA 交来第一张画面（等了 {clock.ElapsedMilliseconds} ms，"
                + $"屏幕 {screen.Id} {surface.Width}×{surface.Height}）。");

            return surface;
        }

        note = $"等了 {FirstFrameWaitMs} ms，这块屏一次都没有变化"
            + $"（DDA 只在画面/指针变化时交付帧，最近一次结论 {capturer.LastAcquireOutcome}）"
            + " —— 这是测试环境的输入条件，不是代码缺陷";

        return null;
    }

    /// <summary>
    /// 一轮「喂 → 编」的结果。
    ///
    /// <para>
    /// 🔥 <b><see cref="Delivered"/> 与 <see cref="Produced"/> 必须分开</b>：
    /// 前者说"DDA 有没有把画面交到手"（<b>前提</b>，不受控），
    /// 后者说"编码器有没有吐出码流"（<b>我们的链子</b>）。
    /// 合成一个数，"桌面静止"与"链路断了"就再也分不开了 ——
    /// 而那正是上一轮"连红四轮、每次红的用例还不同"的成因。
    /// </para>
    /// </summary>
    /// <param name="Produced">编出来的帧。</param>
    /// <param name="Delivered">DDA 是否交付过画面。</param>
    /// <param name="Rounds">这一趟想要多少帧（只用于失败信息）。</param>
    /// <param name="Note">没交付时的原因（能直接贴进日志）。</param>
    private readonly record struct FrameRun(
        List<EncodedStreamPayload> Produced,
        bool Delivered,
        int Rounds,
        string? Note)
    {
        /// <summary>DDA 一张画面都没交出来 ⇒ 这条用例的前提不成立（跳过，不假 FAIL）。</summary>
        public bool Skipped => !Delivered;
    }

    private static string Hex(ReadOnlySpan<byte> data, int count)
    {
        var take = Math.Min(count, data.Length);

        return string.Join(" ", data[..take].ToArray().Select(b => b.ToString("X2")));
    }
}
