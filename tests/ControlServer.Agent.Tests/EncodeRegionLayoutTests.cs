using ControlServer.Platform.Abstractions;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// <b>分块布局</b>（M5.5 第 4 项）—— 纯函数那一层。
///
/// <para>
/// 🔥 <b>为什么它必须被钉死</b>：块边界错一格<b>不会报错</b>。症状只有两种，
/// 而且都长得像别的问题：接缝处撕裂（像"编码质量差"）、右下角少一条（像"抓屏裁掉了"）。
/// 所以这里用<b>矩形覆盖</b>这类可验证的性质来钉，而不是"看起来差不多"。
/// </para>
///
/// <para>
/// 三条硬约束，每一条都对应一个真会翻车的现场：
/// </para>
/// <list type="number">
///   <item><b>每一块都不得超过上限</b>（H.264 4096 / HEVC 8192）——
///         超一块，整条硬编链就建不起来；</item>
///   <item><b>块必须严丝合缝地拼回整幅</b>（首尾相接、宽度之和 == 整幅、Y 都是 0）——
///         漏一格 ⇒ 画面上一条黑缝；重一格 ⇒ 那一块画在错的位置；</item>
///   <item><b>块宽与 X 都必须是偶数</b>（NV12 是 4:2:0 半平面）——
///         奇数会让色度平面没有整数行数，转换器直接建不起来。</item>
/// </list>
/// </summary>
public class EncodeRegionLayoutTests
{
    /// <summary>把一组区域拼起来的宽度（同时验证"首尾相接且 Y 全为 0"）。</summary>
    private static int CoveredWidth(IReadOnlyList<EncodeRegion> regions)
    {
        var x = 0;

        foreach (var region in regions)
        {
            region.X.Should().Be(x, "块必须首尾相接，不能留缝、也不能重叠");
            region.Y.Should().Be(0, "竖条只沿 X 切，Y 恒为 0");

            x += region.Width;
        }

        return x;
    }

    [Fact]
    public void 没有超上限时_就是整幅一块()
    {
        var regions = EncodeRegionLayout.Tiles(new EncodeRegion(0, 0, 7680, 4320), 8192);

        regions.Should().ContainSingle();
        regions[0].Width.Should().Be(7680);
        regions[0].Height.Should().Be(4320);
        regions[0].Covers(7680, 4320).Should().BeTrue("整幅一块时应当与 M5.4 的行为完全一致");
    }

    [Fact]
    public void 三块4K横拼_按8192切成两块且每块都不超上限()
    {
        // 11520 > 8192 ⇒ 必须切。这是现场最典型的"超上限"形态。
        var regions = EncodeRegionLayout.Tiles(new EncodeRegion(0, 0, 11520, 2160), 8192);

        regions.Should().HaveCount(2);

        regions.Should().OnlyContain(
            r => r.Width <= 8192,
            "任何一块超上限，那一块就建不起来 ⇒ 整条流一帧都出不来");

        CoveredWidth(regions).Should().Be(11520, "拼回去必须正好是整幅");
    }

    [Fact]
    public void 每一块的宽与X都是偶数_否则NV12没有整数色度行()
    {
        // 故意用一个"除不尽"的宽度：16400 / 8192 ⇒ 3 块。
        var regions = EncodeRegionLayout.Tiles(new EncodeRegion(0, 0, 16400, 2160), 8192);

        regions.Should().NotBeEmpty();

        regions.Should().OnlyContain(
            r => r.Width % 2 == 0 && r.X % 2 == 0,
            "奇数宽/奇数列起点会让色度平面错位，转换器直接建不起来");

        CoveredWidth(regions).Should().Be(16400);
    }

    [Fact]
    public void 视口偏移时_块坐标从视口左上角开始()
    {
        // ROI + 分块的组合：视口是从 (1000, 20) 开始的 10000×1000 区域。
        var regions = EncodeRegionLayout.Tiles(new EncodeRegion(1000, 20, 10000, 1000), 4096);

        regions.Should().HaveCountGreaterThan(1);
        regions[0].X.Should().Be(1000);
        regions[0].Y.Should().Be(20);
        regions[^1].X.Should().Be(1000 + (regions.Sum(r => r.Width) - regions[^1].Width));

        regions.Sum(r => r.Width).Should().Be(10000);
    }

    [Fact]
    public void 上限为0表示不切()
    {
        var regions = EncodeRegionLayout.Tiles(new EncodeRegion(0, 0, 11520, 2160), 0);

        regions.Should().ContainSingle("maxTileWidth=0 是「不切」，不是「切成 0 块」");
    }

    [Fact]
    public void 没有视口时返回空清单而不是一块假的()
    {
        // 🔥 空清单的含义是"这次什么都不编"。调用方（编码器）必须在这之前
        //    把"没有视口"翻译成"整幅"—— 这里返回一块 0×0 才是真正危险的行为：
        //    建转换器时会以"尺寸太小"失败，而那看起来像"机器不支持硬编"。
        EncodeRegionLayout.Tiles(null, 8192).Should().BeEmpty();
    }

    [Fact]
    public void 越界的视口被夹回画面内()
    {
        // 右下角越界：起点 1900 + 宽 500 > 1920。
        var clamped = EncodeRegionLayout.Clamp(new EncodeRegion(1900, 1000, 500, 500), 1920, 1080);

        clamped.Should().NotBeNull();
        clamped!.Value.X.Should().Be(1900);
        clamped.Value.Y.Should().Be(1000);
        clamped.Value.Width.Should().Be(20, "画面只到 1920，越界部分必须被裁掉");
        clamped.Value.Height.Should().Be(80);
    }

    [Fact]
    public void 完全在画面外的视口返回null()
    {
        EncodeRegionLayout.Clamp(new EncodeRegion(5000, 5000, 100, 100), 1920, 1080)
            .Should().BeNull("夹完是空的 —— 调用方必须据此退回整幅画面，而不是建一块 0×0");
    }

    [Fact]
    public void 奇数宽高被对齐成偶数()
    {
        var clamped = EncodeRegionLayout.Clamp(new EncodeRegion(3, 5, 101, 99), 1920, 1080);

        clamped.Should().NotBeNull();
        clamped!.Value.X.Should().Be(2, "X 向下取偶");
        clamped.Value.Y.Should().Be(4);

        // 右边界 = 3+101 = 104（本来就是偶数）；左边界取偶后是 2 ⇒ 宽 102。
        // ⚠️ 这里刻意把**算术**写清楚：宽是"两条边之差"，不是"宽再取偶" ——
        //    后者会在左边界被取偶时多算 1 像素，而那是画面左边缘被裁掉一列。
        clamped.Value.Width.Should().Be(102, "宽是右边界减左边界，两边都对齐之后自然还是偶数");
        clamped.Value.Height.Should().Be(100);
    }

    [Fact]
    public void 交集算得对_不相交时返回null()
    {
        var a = new EncodeRegion(0, 0, 100, 100);
        var b = new EncodeRegion(50, 50, 100, 100);

        a.Intersect(b).Should().Be(new EncodeRegion(50, 50, 50, 50));

        a.Intersect(new EncodeRegion(200, 200, 10, 10)).Should().BeNull();
    }
}
