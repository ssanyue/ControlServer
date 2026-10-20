using ControlServer.Agent.Remote;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// <b>帧头的"整幅尺寸"该报多大</b>（M7.8）—— 三屏合并显示只画出左上 1/9 的那条。
///
/// <para>
/// 🔥 现场：屏幕 5760×2160、实际编码 1920×720（MJPEG 把长边压到 1920），
/// 而帧头仍报 5760×2160 ⇒ 前端画布按 5760×2160 建、把 1920×720 画在 (0,0)
/// ⇒ 画面只占左上 <b>1/9</b>，其余全黑。用户看到的就是"合并显示画面不对"。
/// </para>
///
/// <para>
/// ⚠️ 这条 bug 的危险之处在于<b>它不报任何错</b>：两端日志干净、帧率正常、
/// 码流也在流。只有把"声明的整幅"和"实际编出来的尺寸"摆在一起才看得出来 ——
/// 所以判据必须是可单测的纯函数，而不是埋在热路径里的一行三元表达式。
/// </para>
/// </summary>
public sealed class RemoteFrameExtentTests
{
    // ── 降采样路径（MJPEG：不支持区域编码，编的就是整幅）──

    [Fact]
    public void A_downscaling_encoder_reports_what_it_actually_encoded()
    {
        // 5760×2160 → 1920×720（= 长边压到 1920 的 1/3）
        var extent = RemoteFrameExtent.Resolve(
            keepsNativeSize: false,
            screenWidth: 5760,
            screenHeight: 2160,
            payloadWidth: 1920,
            payloadHeight: 720);

        extent.Should().Be(
            (1920, 720),
            "不支持区域编码的编码器编的**就是整幅**；它降采样之后整幅在码流里只有这么大。"
            + "报成屏幕尺寸会让画面缩在左上角（三屏时只占 1/9）。");
    }

    [Fact]
    public void A_single_screen_4K_downscaled_to_half_still_reports_the_encoded_size()
    {
        // 这就是"4K 只显示四分之一"的另一种尺寸：3840×2160 → 1920×1080。
        var extent = RemoteFrameExtent.Resolve(
            keepsNativeSize: false,
            screenWidth: 3840,
            screenHeight: 2160,
            payloadWidth: 1920,
            payloadHeight: 1080);

        extent.Should().Be((1920, 1080));
    }

    [Fact]
    public void An_encoder_that_did_not_downscale_is_unaffected()
    {
        // 1080p 屏、不降采样 ⇒ 两个数本来就相等，改动对它是**零影响**。
        var extent = RemoteFrameExtent.Resolve(
            keepsNativeSize: false,
            screenWidth: 1920,
            screenHeight: 1080,
            payloadWidth: 1920,
            payloadHeight: 1080);

        extent.Should().Be((1920, 1080));
    }

    // ── 1:1 路径（GPU 硬编：支持区域编码）──

    [Fact]
    public void A_region_capable_encoder_keeps_the_source_size()
    {
        // 分块时 payload 只是"第 0 块"（比如 3840×1080 的一半宽），
        // 而整幅必须仍是源纹理尺寸 —— 否则每一块都会被拉满整个画布。
        var extent = RemoteFrameExtent.Resolve(
            keepsNativeSize: true,
            screenWidth: 3840,
            screenHeight: 2160,
            payloadWidth: 1920,
            payloadHeight: 2160);

        extent.Should().Be(
            (3840, 2160),
            "支持区域编码的编码器契约是 1:1、不缩放，payload 尺寸只代表**这一块**。");
    }

    [Fact]
    public void Roi_does_not_shrink_the_declared_whole()
    {
        // ROI 视口：只编了左上角一块，整幅仍然是整个桌面。
        var extent = RemoteFrameExtent.Resolve(
            keepsNativeSize: true,
            screenWidth: 3840,
            screenHeight: 2160,
            payloadWidth: 1920,
            payloadHeight: 1080);

        extent.Should().Be((3840, 2160));
    }

    // ── 退化：两边都可能缺 ──

    [Fact]
    public void When_the_screen_size_is_unknown_a_region_encoder_falls_back_to_the_payload()
    {
        var extent = RemoteFrameExtent.Resolve(
            keepsNativeSize: true,
            screenWidth: 0,
            screenHeight: 0,
            payloadWidth: 1920,
            payloadHeight: 1080);

        extent.Should().Be(
            (1920, 1080),
            "声明成 0 会让前端直接不画 —— 退到\"整幅 = 这一帧\"至少还能看见画面。");
    }

    [Fact]
    public void When_the_payload_size_is_unknown_a_plain_encoder_falls_back_to_the_screen()
    {
        var extent = RemoteFrameExtent.Resolve(
            keepsNativeSize: false,
            screenWidth: 3840,
            screenHeight: 2160,
            payloadWidth: 0,
            payloadHeight: 0);

        extent.Should().Be((3840, 2160));
    }

    [Fact]
    public void Both_unknown_yields_zero_and_that_is_explicit()
    {
        // 两个都拿不到时不编一个"看起来合理"的数 —— 前端会把 0 当"还没有画面"。
        var extent = RemoteFrameExtent.Resolve(
            keepsNativeSize: false,
            screenWidth: 0,
            screenHeight: 0,
            payloadWidth: 0,
            payloadHeight: 0);

        extent.Should().Be((0, 0));
    }

    [Fact]
    public void A_partially_known_screen_size_is_treated_as_unknown()
    {
        // 宽有、高没有 = 不可用。拿它去算比例只会得到一条斜画面。
        var extent = RemoteFrameExtent.Resolve(
            keepsNativeSize: true,
            screenWidth: 3840,
            screenHeight: 0,
            payloadWidth: 1920,
            payloadHeight: 1080);

        extent.Should().Be((1920, 1080));
    }

    // ═══════════════════════════════════════════════════════════
    // 降采样长边该取多少（M7.10）—— "合并显示的全屏效果不如单屏"那条
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void A_three_screen_span_gets_a_much_larger_long_edge_than_the_old_1920()
    {
        // 三块 1920 并排 = 5760×2160。旧规则钉死 1920 ⇒ 每块屏只剩 640 宽，
        // 全屏放大三倍就是糊的；新规则给 3840 ⇒ 每块屏 1280 宽，线性翻倍。
        var longEdge = RemoteFrameExtent.ResolveMaxLongEdge(5760, 2160);

        longEdge.Should().Be(
            3840,
            "整幅压到长边 3840 之后每块屏 1280 宽；再往上就该切单屏，而不是继续加 MJPEG 分辨率。");
    }

    [Fact]
    public void Two_1080p_screens_side_by_side_keep_their_native_width()
    {
        // 3840×1080（两块 1920 并排）在这个上限内 ⇒ 完全不降采样，每块屏都是原生分辨率。
        RemoteFrameExtent.ResolveMaxLongEdge(3840, 1080).Should().Be(3840);
    }

    [Fact]
    public void A_single_1080p_screen_is_not_pushed_below_the_baseline()
    {
        // 单屏 1080p：长边正好 1920，等于没降采样 —— 与历史行为一致，不要因为它"看起来小"而抬高。
        RemoteFrameExtent.ResolveMaxLongEdge(1920, 1080).Should().Be(1920);
    }

    [Fact]
    public void A_small_screen_is_still_raised_to_the_baseline()
    {
        // 1280×720 的小屏：长边 1280 < 下限 ⇒ 取 1920（编码器也不会放大，
        // 这个值只是"上限"，所以对实际画面没有影响）。
        RemoteFrameExtent.ResolveMaxLongEdge(1280, 720).Should().Be(1920);
    }

    [Fact]
    public void A_single_4K_screen_gets_its_native_width()
    {
        // 单屏 4K（3840×2160）此前也被压到 1920 —— 这正是"4K 只显示四分之一"的同源问题。
        RemoteFrameExtent.ResolveMaxLongEdge(3840, 2160).Should().Be(3840);
    }

    [Fact]
    public void An_8K_span_is_capped_instead_of_multiplied_by_screen_count()
    {
        // 4 块 4K 并排 = 15360 宽。按屏数乘会得到 12 MPix 以上的 JPEG（单帧十几 MB）——
        // 那是"拿 MJPEG 传 8K"，怎么调都是错的 ⇒ 必须封顶。
        RemoteFrameExtent.ResolveMaxLongEdge(15360, 4320).Should().Be(RemoteFrameExtent.MaxMaxLongEdge);
    }

    [Fact]
    public void An_unknown_screen_size_falls_back_to_the_old_default()
    {
        // 会话还没 Prepare 完 / 抓屏器报了 0：不要给出 0（0 = 不降采样 = 4K 软编会吃光 CPU）。
        RemoteFrameExtent.ResolveMaxLongEdge(0, 0).Should().Be(RemoteFrameExtent.MinMaxLongEdge);
        RemoteFrameExtent.ResolveMaxLongEdge(-1, 2160).Should().Be(RemoteFrameExtent.MinMaxLongEdge);
    }

    [Fact]
    public void Height_driven_long_edge_is_handled_too()
    {
        // 竖排 / 竖屏：长边在高度那边。判据取 max(w,h)，不能只比宽度。
        RemoteFrameExtent.ResolveMaxLongEdge(2160, 3840).Should().Be(3840);
    }
}
