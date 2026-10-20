using ControlServer.Platform.Windows;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// M5.4-b 第 5 步：<b>指针合成</b>（<c>PointerCompositor</c>）。
///
/// <para>
/// 🔥 <b>它是纯函数，所以这一批用例一个设备都不需要</b> —— 而它要钉住的恰恰是本批最
/// "看着简单、写错了却完全看不出来"的一块：DXGI 指针形状的<b>三态语义</b>。
/// </para>
///
/// <list type="number">
///   <item><b><c>COLOR</c></b> —— 按 α 混合。⚠️ 但 <b>"整份形状的 α 全是 0"是真实存在的</b>
///         （老式光标 / 某些驱动），那时逐像素按 α 混合会把整个指针画成"全透明"＝看不见。
///         判据必须是<b>形状级</b>的，不是像素级的。</item>
///   <item><b><c>MASKED_COLOR</c></b> —— RGB 是"<b>要与背景逐位 XOR 的值</b>"，α 无效。
///         当普通位图直接画，症状是<b>指针在深色背景上变成一个黑洞</b>。</item>
///   <item><b><c>MONOCHROME</c></b> —— 1bpp 双掩码（上半 AND、下半 XOR），
///         <c>dst = (dst AND andMask) XOR xorMask</c>，共四种情形。</item>
/// </list>
///
/// <para>
/// ⚠️ 这三态在真机上"几乎总是 COLOR" ⇒ 另外两条分支**永远不会被现场跑到**。
/// 如果这里不测，"写完从没跑过"的代码会一直躺在那里，直到某台机器上出现一个黑洞。
/// </para>
/// </summary>
public class PointerCompositorTests
{
    // ═══════════════════════════════════════════════════════════
    // 区域计算
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void An_invisible_pointer_needs_no_region()
    {
        var pointer = Colour(1, 1, visible: false);

        PointerCompositor.TryGetRegion(pointer, 100, 100, out _, out var reason).Should().BeFalse();

        reason.Should().Contain("不可见");
    }

    [Fact]
    public void A_pointer_fully_outside_the_frame_needs_no_region()
    {
        // 1×1 的指针放在 x = -10：整个在画面左边界之外。
        var pointer = Colour(1, 1, x: -10);

        PointerCompositor.TryGetRegion(pointer, 100, 100, out _, out var reason).Should().BeFalse();

        reason.Should().Contain("画面外");
    }

    [Fact]
    public void An_incomplete_shape_is_refused_with_the_numbers()
    {
        // 声明 8×8（Pitch 32 ⇒ 需要 1024 字节），却只给了 4 个字节。
        var pointer = Shape(PointerShapeType.Color, new byte[4], 8, 8, 32);

        PointerCompositor.TryGetRegion(pointer, 100, 100, out _, out var reason).Should().BeFalse();

        // ⚠️ 是 `Pitch × 行数`（32 × 8 = 256），**不是** `宽 × 高 × 4` ——
        //    驱动给的行距（Pitch）才是权威值，两者不一定相等（这正是"必须用 Pitch"这条的由来）。
        reason.Should().Contain("需要 256 字节").And.Contain("只有 4 字节");
    }

    [Fact]
    public void The_region_is_expanded_outward_to_even_numbers()
    {
        // 指针 3×3、位置 (5,7) ⇒ 覆盖 (5,7)..(8,10) ⇒ 向外扩到 (4,6)..(8,10)。
        // 🔥 为什么必须偶数：这块 BGRA 还要经 VideoProcessor 转 NV12 写回去，而 NV12 的色度是 2×2。
        var pointer = Colour(3, 3, x: 5, y: 7);

        PointerCompositor.TryGetRegion(pointer, 100, 100, out var region, out _).Should().BeTrue();

        region.Left.Should().Be(4);
        region.Top.Should().Be(6);
        region.Right.Should().Be(8);
        region.Bottom.Should().Be(10);
    }

    [Fact]
    public void The_region_is_clipped_to_the_frame()
    {
        // 指针 8×8 挂在左上角外面一点：覆盖 (-3,-3)..(5,5) ⇒ 裁进画面并扩到偶数。
        var pointer = Colour(8, 8, x: -3, y: -3);

        PointerCompositor.TryGetRegion(pointer, 100, 100, out var region, out _).Should().BeTrue();

        region.Left.Should().Be(0);
        region.Top.Should().Be(0);
        region.Right.Should().Be(6, "5 向上取偶是 6");
        region.Bottom.Should().Be(6);
    }

    [Fact]
    public void The_hot_spot_shifts_the_pointer_origin()
    {
        // 位置 (10,10) ｘ热点 (1,2) ⇒ 指针左上角在 (9,8)（矩形扩到偶数 ⇒ 从 (8,8) 起）。
        var pointer = Shape(
            PointerShapeType.Color, [0x00, 0x00, 0xFF, 0xFF], 1, 1, 4,
            hotSpotX: 1, hotSpotY: 2, x: 10, y: 10);

        PointerCompositor.TryGetRegion(pointer, 100, 100, out var region, out _).Should().BeTrue();

        region.Left.Should().Be(8);
        region.Top.Should().Be(8);

        // 合成到那个 2×2 的小块上：红色像素应当落在区域内的 (1,0)。
        var background = new byte[2 * 2 * 4];

        PointerCompositor.Composite(pointer, region.Left, region.Top, background, 2 * 4, 2, 2);

        var index = 1 * 4;   // 区域内坐标 (1,0)

        background[index].Should().Be(0x00);
        background[index + 1].Should().Be(0x00);
        background[index + 2].Should().Be(0xFF, "热点没减掉的话，这个像素会整体偏右下");
        background[index + 3].Should().Be(0xFF);
    }

    // ═══════════════════════════════════════════════════════════
    // COLOR
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void An_opaque_colour_pixel_replaces_the_background()
    {
        var pointer = Shape(PointerShapeType.Color, [0x00, 0x00, 0xFF, 0xFF], 1, 1, 4);

        var background = new byte[] { 0x10, 0x20, 0x30, 0xFF };

        PointerCompositor.Composite(pointer, 0, 0, background, 4, 1, 1);

        background.Should().Equal(new byte[] { 0x00, 0x00, 0xFF, 0xFF });
    }

    [Fact]
    public void A_half_transparent_pixel_is_blended()
    {
        // 纯白、α = 128，压在纯黑背景上 ⇒ 128（(255×128 + 0×127 + 127) / 255）。
        var pointer = Shape(PointerShapeType.Color, [0xFF, 0xFF, 0xFF, 0x80], 1, 1, 4);

        var background = new byte[] { 0x00, 0x00, 0x00, 0xFF };

        PointerCompositor.Composite(pointer, 0, 0, background, 4, 1, 1);

        background[0].Should().Be(128);
        background[1].Should().Be(128);
        background[2].Should().Be(128);
        background[3].Should().Be(255, "混合之后的结果是不透明的");
    }

    [Fact]
    public void A_shape_with_no_alpha_anywhere_is_drawn_opaque()
    {
        // 🔥 整份形状的 α 全是 0、而 RGB 有内容 —— 这在真机上真的存在。
        //    逐像素按 α 混合会把它画成"什么都没有"，而观看端看到的是"这个光标不显示"。
        var pointer = Shape(
            PointerShapeType.Color, [0x11, 0x22, 0x33, 0x00], 1, 1, 4, hasAlpha: false);

        var background = new byte[] { 0xAA, 0xBB, 0xCC, 0xFF };

        PointerCompositor.Composite(pointer, 0, 0, background, 4, 1, 1);

        background.Should().Equal(
            new byte[] { 0x11, 0x22, 0x33, 0xFF },
            "整份形状没有 α ⇒ 那份 α 不算数，必须按不透明画上去");
    }

    [Fact]
    public void A_fully_transparent_pixel_is_skipped_when_the_shape_does_have_alpha()
    {
        var pointer = Shape(PointerShapeType.Color, [0xFF, 0xFF, 0xFF, 0x00], 1, 1, 4, hasAlpha: true);

        var background = new byte[] { 0x10, 0x20, 0x30, 0xFF };

        PointerCompositor.Composite(pointer, 0, 0, background, 4, 1, 1);

        background.Should().Equal(new byte[] { 0x10, 0x20, 0x30, 0xFF }, "这一份 α 是算数的，0 就是全透明");
    }

    // ═══════════════════════════════════════════════════════════
    // MASKED_COLOR
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Masked_colour_xors_against_the_background()
    {
        // 纯白 XOR 值压在 (B,G,R) = (0x60,0x40,0x20) 上 ⇒ 逐通道取反。
        var pointer = Shape(PointerShapeType.MaskedColor, [0xFF, 0xFF, 0xFF, 0x00], 1, 1, 4);

        var background = new byte[] { 0x60, 0x40, 0x20, 0xFF };

        PointerCompositor.Composite(pointer, 0, 0, background, 4, 1, 1);

        background[0].Should().Be(0x9F);
        background[1].Should().Be(0xBF);
        background[2].Should().Be(0xDF);
        background[3].Should().Be(0xFF);

        background[0].Should().NotBe(0x00, "把它当普通位图直接画，深色背景上这里会是 0（黑洞）");
    }

    [Fact]
    public void Masked_colour_ignores_the_alpha_channel()
    {
        // 同一份形状、只把 α 从 0x00 改成 0xFF，结果必须一模一样。
        var shape = new byte[] { 0xFF, 0xFF, 0xFF, 0x00 };

        var withZeroAlpha = new byte[] { 0x60, 0x40, 0x20, 0xFF };
        var withFullAlpha = new byte[] { 0x60, 0x40, 0x20, 0xFF };

        var shapeFullAlpha = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };

        PointerCompositor.Composite(
            Shape(PointerShapeType.MaskedColor, shape, 1, 1, 4), 0, 0, withZeroAlpha, 4, 1, 1);

        PointerCompositor.Composite(
            Shape(PointerShapeType.MaskedColor, shapeFullAlpha, 1, 1, 4), 0, 0, withFullAlpha, 4, 1, 1);

        withZeroAlpha.Should().Equal(withFullAlpha, "α 在这一态里根本没有意义");
    }

    // ═══════════════════════════════════════════════════════════
    // MONOCHROME
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <c>dst = (dst AND andMask) XOR xorMask</c> 的四种情形。
    /// <para>
    /// 🔥 用 <c>Theory</c> 而不是四个 <c>Fact</c>，是因为这四行<b>合起来才是一张表</b> ——
    /// 分开写会让人以为它们是四个互不相干的场景。
    /// </para>
    /// <para>背景取 (0x40,0x80,0xC0,0xFF)：它既不是黑也不是白，四种结果各自都能看出来。</para>
    /// </summary>
    [Theory]
    [InlineData(0b11111111, 0b00000000, 0x40, 0x80, 0xC0, "and=1 xor=0 ⇒ 保留背景")]
    [InlineData(0b11111111, 0b11111111, 0xBF, 0x7F, 0x3F, "and=1 xor=1 ⇒ 背景反色")]
    [InlineData(0b00000000, 0b00000000, 0x00, 0x00, 0x00, "and=0 xor=0 ⇒ 全黑")]
    [InlineData(0b00000000, 0b11111111, 0xFF, 0xFF, 0xFF, "and=0 xor=1 ⇒ 全白")]
    public void Monochrome_follows_the_and_xor_table(
        int and, int xor, int blue, int green, int red, string because)
    {
        var pointer = Shape(PointerShapeType.Monochrome, [(byte)and, (byte)xor], 8, 1, 1);

        var background = new byte[] { 0x40, 0x80, 0xC0, 0xFF };

        PointerCompositor.Composite(pointer, 0, 0, background, 4, 1, 1);

        background.Should().Equal(new byte[] { (byte)blue, (byte)green, (byte)red, 0xFF }, because);
    }

    [Fact]
    public void Monochrome_reads_the_highest_bit_first()
    {
        // AND 全 0（不保留背景）；XOR 只有**最左边**那一位是 1 ⇒ 只有第 0 个像素是白。
        var pointer = Shape(PointerShapeType.Monochrome, [0b00000000, 0b10000000], 8, 1, 1);

        var background = new byte[8 * 4];

        PointerCompositor.Composite(pointer, 0, 0, background, 8 * 4, 8, 1);

        background[0].Should().Be(0xFF, "位序反了的话，变白的是最右边那个像素");
        background[4].Should().Be(0x00);
    }

    [Fact]
    public void Monochrome_reads_the_xor_mask_from_the_second_half_of_the_buffer()
    {
        // 🔥 `MONOCHROME` 的 `Height` 是实际高度的 **2 倍**：上半 AND、下半 XOR。
        //    这里给 2 行实际高度（Pitch 1 ⇒ 缓冲 4 字节），只让**下面那一半**生效。
        var pointer = Shape(
            PointerShapeType.Monochrome,
            [0b00000000, 0b00000000, /* XOR 行 */ 0b11111111, 0b00000000],
            8, 2, 1);

        var background = new byte[8 * 2 * 4];

        PointerCompositor.Composite(pointer, 0, 0, background, 8 * 4, 8, 2);

        background[0].Should().Be(0xFF, "第 0 行应当全白");
        background[8 * 4].Should().Be(0x00, "第 1 行那一半的 XOR 是 0 ⇒ 全黑");
    }

    // ═══════════════════════════════════════════════════════════
    // 边界
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Pixels_outside_the_region_are_left_alone()
    {
        // 指针 2×2 在画面 (0,0)；却只把区域原点说成 (1,0)（人为裁掉一半）。
        var shape = new byte[2 * 2 * 4];

        for (var i = 0; i < 4; i++)
        {
            shape[(i * 4) + 2] = 0xFF;
            shape[(i * 4) + 3] = 0xFF;
        }

        var pointer = Shape(PointerShapeType.Color, shape, 2, 2, 8, x: 0, y: 0);

        var background = new byte[2 * 2 * 4];

        PointerCompositor.Composite(pointer, 1, 0, background, 2 * 4, 2, 2);

        for (var y = 0; y < 2; y++)
        {
            var inside = (y * 2) * 4;

            background[inside + 2].Should().Be(0xFF, "指针的 x=1 那一列落在区域内，要画上");
            background[inside + 3].Should().Be(0xFF);

            var outside = ((y * 2) + 1) * 4;

            background[outside + 3].Should().Be(0x00, "区域外的像素一个都不许碰（越界是正常情形，不是错误）");
        }
    }

    [Fact]
    public void A_zero_sized_frame_is_refused()
    {
        PointerCompositor.TryGetRegion(Colour(1, 1), 0, 0, out _, out var reason).Should().BeFalse();

        reason.Should().Contain("不合法");
    }

    // ═══════════════════════════════════════════════════════════
    // 素材
    // ═══════════════════════════════════════════════════════════

    private static PointerSnapshot Colour(
        int width, int height, int x = 0, int y = 0, bool visible = true)
        => Shape(
            PointerShapeType.Color,
            new byte[width * height * 4],
            width,
            height,
            width * 4,
            x: x,
            y: y,
            visible: visible);

    private static PointerSnapshot Shape(
        PointerShapeType type,
        byte[] data,
        int width,
        int height,
        int pitch,
        int hotSpotX = 0,
        int hotSpotY = 0,
        int x = 0,
        int y = 0,
        bool visible = true,
        bool hasAlpha = true)
        => new(type, width, height, pitch, hotSpotX, hotSpotY, x, y, visible, hasAlpha, data);
}
