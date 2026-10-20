using ControlServer.Contracts;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// <b>编码格式协商</b>（M5.4-b §8 第 7 步）的纯函数用例。
///
/// <para>
/// 🔥 <b>为什么这些用例值得单独存在</b>：这段规则同时被中心与客户机使用
/// （两个程序集、没有引用关系），而它错了的表现<b>没有任何编译错误</b> ——
/// 只会变成一次"编码器建不起来"，或者更糟：一块<b>浏览器解不开的黑屏</b>。
/// </para>
///
/// <para>
/// ⚠️ 本文件盯住的是<b>四件容易搞混的事</b>（每一件都对应一条真实会犯的错）：
/// </para>
/// <list type="number">
///   <item><b><c>null</c>（没声明）与 <c>[]</c>（明确说一种都显示不了）</b>处置相反；</item>
///   <item><b><c>mjpeg</c> 不该过被控端的硬编能力筛</b> —— 它走 GDI+ 软编，永远可用；</item>
///   <item><b>被控端"还没上报"不是"一台硬编都没有"</b> —— 前者回落，后者才可能拒绝；</item>
///   <item><b>偏好顺序是 H.264 &gt; HEVC &gt; MJPEG</b>，而且顺序就是给被控端的裁决依据。</item>
/// </list>
/// </summary>
public sealed class RemoteVideoNegotiationTests
{
    // 常用组合，省得每处都写全。
    private static readonly string[] H264Only = { RemoteCodecs.H264 };
    private static readonly string[] H264AndHevc = { RemoteCodecs.H264, RemoteCodecs.Hevc };
    private static readonly string[] ViewerH264HevcMjpeg =
        { RemoteCodecs.H264, RemoteCodecs.Hevc, RemoteCodecs.Mjpeg };

    private static RemoteViewerVideoCapabilities Viewer(params string[] codecs)
        => new(codecs, Declared: true, Packaging: RemoteStreamPackagings.InBand);

    private static IReadOnlyList<string> Decide(
        IReadOnlyList<string>? agent,
        RemoteViewerVideoCapabilities? viewer,
        out string note,
        out string? failure)
        => RemoteVideoNegotiation.Decide(
            agent, viewer, out _, out note, out failure);

    /// <summary>
    /// 同上，但把**封装**取出来。
    /// <para>
    /// ⚠️ 单独一个助手是有理由的：上面那个把封装丢掉了，而"封装"与"说明"都是
    /// <c>string</c> —— 混用的时候<b>不会有任何编译错误</b>，
    /// 只是断言会去看一句人话（本文件的这个坑已经踩过一次）。
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> DecideWithPackaging(
        IReadOnlyList<string>? agent,
        RemoteViewerVideoCapabilities? viewer,
        out string packaging)
        => RemoteVideoNegotiation.Decide(
            agent, viewer, out packaging, out _, out _);

    // ═══════════════════════════════════════════════════════════════
    // 1. 正常取交集
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Both_sides_known_takes_the_intersection_in_preference_order()
    {
        // 观看端两样都行，被控端只有 H.264 ⇒ 交集 = H.264
        var plan = Decide(H264Only, Viewer(RemoteCodecs.Hevc, RemoteCodecs.H264), out var note, out var failure);

        failure.Should().BeNull();
        plan.Should().Equal(RemoteCodecs.H264);

        // ⚠️ 顺序必须是偏好顺序（H.264 在前），而不是"观看端声明的顺序"：
        //    观看端写的是 hevc 在前，若照抄过去，被控端就会去试 HEVC。
        note.Should().Contain("h264");
    }

    [Fact]
    public void Preference_is_h264_then_hevc_then_mjpeg()
    {
        // 故意把声明写反 —— 出来的必须是偏好顺序。
        var plan = Decide(
            H264AndHevc,
            Viewer(RemoteCodecs.Mjpeg, RemoteCodecs.Hevc, RemoteCodecs.H264),
            out _,
            out var failure);

        failure.Should().BeNull();
        plan.Should().Equal(RemoteCodecs.H264, RemoteCodecs.Hevc, RemoteCodecs.Mjpeg);
    }

    [Fact]
    public void Unknown_codec_names_are_dropped_not_treated_as_unsupported()
    {
        // ⚠️ 一个更新版本报上来的新格式名不能把这一路搞死：
        //    它只是"我不认得"，不是"不支持"。剔掉它，剩下的照算。
        RemoteVideoNegotiation.Normalize(new[] { "av1", RemoteCodecs.H264, "AV1" })
            .Should().Equal(RemoteCodecs.H264);

        var plan = Decide(
            new[] { RemoteCodecs.H264, "av1" },
            Viewer("av1", RemoteCodecs.H264),
            out _,
            out var failure);

        failure.Should().BeNull();
        plan.Should().Equal(RemoteCodecs.H264);
    }

    [Fact]
    public void Duplicates_and_case_are_normalized()
    {
        RemoteVideoNegotiation.Normalize(new[] { "H264", "h264", " H264 ", "" })
            .Should().Equal(RemoteCodecs.H264);
    }

    // ═══════════════════════════════════════════════════════════════
    // 2. 🔴 回落：null（没声明）与 []（明确说没有）处置相反
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void A_viewer_that_did_not_declare_falls_back_to_in_band_h264()
    {
        // 老页面：压根没带这个字段。
        var plan = Decide(H264AndHevc, viewer: null, out var note, out var failure);

        failure.Should().BeNull("'没声明'必须回落，不是拒绝 —— 那才是本步的决策");

        plan.Should().Equal(RemoteCodecs.H264);

        // 🔥 刻意**不带 HEVC**：这个观看端从来没说过它认不认 HEVC。
        //    给它 HEVC 就等于拿一次探测失败去换一块黑屏。
        plan.Should().NotContain(RemoteCodecs.Hevc);

        // ⚠️ 也必须**不带 MJPEG**：1440p 的 MJPEG 只有 12–25 fps，
        //    把它塞进回落集合等于"一次探测失败就永久降级"。
        plan.Should().NotContain(RemoteCodecs.Mjpeg);

        // ⚠️ 过渡期风险要在话里说清（旧页面只渲染 MJPEG ⇒ 会黑屏 ⇒ 强制刷新）。
        note.Should().Contain("刷新");
    }

    [Fact]
    public void A_viewer_that_declared_nothing_and_has_no_agent_report_still_falls_back()
    {
        var plan = Decide(agent: null, viewer: null, out _, out var failure);

        failure.Should().BeNull();
        plan.Should().Equal(RemoteCodecs.H264);
    }

    [Fact]
    public void A_fallback_prefers_h264_even_when_the_machine_also_has_hevc()
    {
        var plan = Decide(
            new[] { RemoteCodecs.Hevc, RemoteCodecs.H264 },
            viewer: null,
            out _,
            out var failure);

        failure.Should().BeNull();
        plan.Should().Equal(RemoteCodecs.H264);
    }

    [Fact]
    public void A_machine_with_only_hevc_and_a_silent_viewer_gets_hevc()
    {
        // 罕见但真实：机器只有 HEVC 硬编。这时没有任何"更保守"的选择。
        var plan = Decide(new[] { RemoteCodecs.Hevc }, viewer: null, out _, out var failure);

        failure.Should().BeNull();
        plan.Should().Equal(RemoteCodecs.Hevc);
    }

    [Fact]
    public void A_viewer_explicitly_claiming_it_can_display_nothing_is_rejected_not_fallen_back()
    {
        // 🔥 `declared: true` + 空表 = "我一种都显示不了"。
        //    而 MJPEG 任何浏览器都显示得了（只需要 <img>）⇒ 这句话一定写错了。
        var plan = Decide(
            H264Only,
            new RemoteViewerVideoCapabilities(Array.Empty<string>(), Declared: true),
            out var note,
            out var failure);

        failure.Should().NotBeNull("这与'没声明'的处置**相反**，两者在代码里长得几乎一样");
        plan.Should().BeEmpty();

        // 理由必须直指"声明写错了"，而不是把人带去查浏览器版本。
        failure.Should().Contain("声明");
        note.Should().Contain("拒绝");
    }

    // ═══════════════════════════════════════════════════════════════
    // 3. 🔴 mjpeg 不许过硬编能力这道筛
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Mjpeg_does_not_need_a_hardware_encoder()
    {
        RemoteVideoNegotiation.NeedsHardwareEncoder(RemoteCodecs.H264).Should().BeTrue();
        RemoteVideoNegotiation.NeedsHardwareEncoder(RemoteCodecs.Hevc).Should().BeTrue();
        RemoteVideoNegotiation.NeedsHardwareEncoder(RemoteCodecs.Mjpeg).Should().BeFalse();
        RemoteVideoNegotiation.NeedsHardwareEncoder(null).Should().BeFalse();
    }

    [Fact]
    public void A_viewer_that_can_only_display_mjpeg_still_gets_mjpeg_on_a_machine_with_hevc_hardware()
    {
        // 🔥 这就是第 8 步之前页面的真实处境：它只会渲染 MJPEG。
        //    若拿机器的硬编能力去筛 mjpeg，交集会变成空 ⇒ 拒绝建会话 ⇒ 一步都走不了。
        //    正确结果是"这一路走 MJPEG"，画面照旧。
        var plan = Decide(new[] { RemoteCodecs.Hevc }, Viewer(RemoteCodecs.Mjpeg), out _, out var failure);

        failure.Should().BeNull("mjpeg 走 GDI+ 软编，永远可用，不该被硬编能力筛掉");
        plan.Should().Equal(RemoteCodecs.Mjpeg);
    }

    [Fact]
    public void A_machine_with_no_hardware_at_all_can_still_serve_a_mjpeg_only_viewer()
    {
        // 空表 = 这台机器一台硬编都没有。这**不是**拒绝的理由 —— MJPEG 还能用。
        var plan = Decide(Array.Empty<string>(), Viewer(RemoteCodecs.Mjpeg), out _, out var failure);

        failure.Should().BeNull();
        plan.Should().Equal(RemoteCodecs.Mjpeg);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4. 🔴 "还没上报" ≠ "一台硬编都没有"
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void An_agent_that_has_not_reported_yet_is_not_a_rejection()
    {
        // null = 它还没报过（AgentEncoderCapabilityService 的注释）。
        // ⚠️ 如果这里返回拒绝，一台刚重连、还没报能力的机器就**永远建不起会话**。
        var plan = Decide(null, Viewer(RemoteCodecs.H264), out var note, out var failure);

        failure.Should().BeNull("'还没上报'与'明确无能力'是两件事");
        plan.Should().Equal(RemoteCodecs.H264);
        note.Should().Contain("尚未上报");
    }

    [Fact]
    public void An_empty_agent_report_is_also_not_a_rejection_when_the_viewer_takes_mjpeg()
    {
        var plan = Decide(
            Array.Empty<string>(),
            Viewer(RemoteCodecs.H264, RemoteCodecs.Mjpeg),
            out _,
            out var failure);

        failure.Should().BeNull();
        plan.Should().Equal(RemoteCodecs.Mjpeg);
    }

    // ═══════════════════════════════════════════════════════════════
    // 5. 唯一会拒绝的那一种：两边都清楚，交集为空，且没有 mjpeg 兜底
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void A_known_viewer_and_a_known_agent_with_no_overlap_is_rejected()
    {
        var plan = Decide(H264Only, Viewer(RemoteCodecs.Hevc), out var note, out var failure);

        failure.Should().NotBeNull();
        plan.Should().BeEmpty();

        // 理由里两边都要出现 —— 只报一侧，现场会去查错的那一侧。
        failure.Should().Contain(RemoteCodecs.Hevc);
        failure.Should().Contain(RemoteCodecs.H264);
        note.Should().Contain("拒绝");
    }

    // ═══════════════════════════════════════════════════════════════
    // 6. 封装：由观看端定，认不得的值回落并留痕
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Packaging_comes_from_the_viewer_and_unknown_values_fall_back_to_in_band()
    {
        var ok = DecideWithPackaging(
            H264Only,
            new RemoteViewerVideoCapabilities(H264Only, Declared: true, Packaging: RemoteStreamPackagings.OutOfBand),
            out var outOfBand);

        ok.Should().Equal(RemoteCodecs.H264);
        outOfBand.Should().Be(RemoteStreamPackagings.OutOfBand);

        // 连字符写法（`out-of-band`）是一个真实会犯的错：它必须回落**而不是**报错。
        DecideWithPackaging(
            H264Only,
            new RemoteViewerVideoCapabilities(H264Only, Declared: true, Packaging: "out-of-band"),
            out var typo);

        typo.Should().Be(
            RemoteStreamPackagings.InBand,
            "认不得的封装要回落；留痕由调用方在做得到日志的地方补（它才有日志出口）");

        // 没声明封装 ⇒ 带内（与第 6 步的默认一致）。
        DecideWithPackaging(H264Only, Viewer(RemoteCodecs.H264), out var byDefault);

        byDefault.Should().Be(RemoteStreamPackagings.InBand);
    }

    [Fact]
    public void A_viewer_that_only_sets_codecs_is_treated_as_declared()
    {
        // ⚠️ 页面忘了把 `declared` 置位、但给了格式名 —— 这种情况不该被当成"没探测"：
        //    集合非空**本身就等于**声明过，`declared` 只是用来消解 null 与 [] 的歧义。
        var plan = Decide(
            H264Only,
            new RemoteViewerVideoCapabilities(new[] { RemoteCodecs.H264 }, Declared: false),
            out _,
            out var failure);

        failure.Should().BeNull();
        plan.Should().Equal(RemoteCodecs.H264);
    }
}
