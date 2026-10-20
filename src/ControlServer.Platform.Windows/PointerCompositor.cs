namespace ControlServer.Platform.Windows;

/// <summary>
/// <b>DDA 指针形状的三态</b>。
///
/// <para>
/// 🔥 数值<b>核自 <c>dxgi1_2.h</c> 的 <c>DXGI_OUTDUPL_POINTER_SHAPE_TYPE</c></b>：
/// <c>MONOCHROME = 0x1</c> / <c>COLOR = 0x2</c> / <c>MASKED_COLOR = 0x4</c>。
/// ⇒ <b>不是 1 / 2 / 3 连号</b>，也别按"先 COLOR 再 MASKED"的语感去排 ——
/// 三态里唯一"正常"的那种（<c>COLOR</c>）排在中间。
/// </para>
/// </summary>
internal enum PointerShapeType
{
    /// <summary>1bpp 双掩码：上半 <c>AND</c>、下半 <c>XOR</c>，<c>Height</c> 是实际高度的 <b>2 倍</b>。</summary>
    Monochrome = 0x1,

    /// <summary>32bpp BGRA，按 α 通道混合。</summary>
    Color = 0x2,

    /// <summary>32bpp BGRA，但 <b>RGB 是"要与背景逐位 XOR 的值"</b>，α 无效。</summary>
    MaskedColor = 0x4,
}

/// <summary>
/// 一帧的<b>指针快照</b>（形状 ＋ 位置），由抓屏器在每个"有更新"的帧上产出。
///
/// <para>
/// ⚠️ <b>"形状变了"与"位置变了"是两件独立的事</b>：DDA 只在形状<b>真的变了</b>时才给形状字节
/// （<c>PointerShapeBufferSize &gt; 0</c>），而位置<b>每帧都可能变</b>。
/// ⇒ 位置来自当帧的 <c>PointerPosition</c>，形状则要靠抓屏器<b>缓存上一份</b>
/// （<c>Shape</c> 字段因此是"当前有效的那一份"，不一定来自本帧）。
/// </para>
/// </summary>
/// <param name="ActualHeight">
/// <b>实际高度</b>（已经替调用方除掉了 <c>MONOCHROME</c> 的 2 倍关系）——
/// 抓屏器在构造时就把 <c>OutduplPointerShapeInfo.Height</c> 折算掉了。
/// </param>
/// <param name="ShapeHasAlpha">
/// 仅对 <see cref="PointerShapeType.Color"/> 有意义：整个形状里<b>是否存在非 0 的 α</b>。
/// <para>
/// 🔥 为什么需要它：<b>α 全 0 而 RGB 有内容</b>的指针位图是真实存在的（老式光标 / 某些驱动），
/// 那时逐像素按 α 混合会把整个指针画成"全透明"＝看不见。
/// ⇒ 逐像素判"α==0 就跳过"是错的，必须<b>按整个形状</b>决定"这份 α 算不算数"。
/// </para>
/// </param>
internal sealed record PointerSnapshot(
    PointerShapeType Type,
    int Width,
    int ActualHeight,
    int Pitch,
    int HotSpotX,
    int HotSpotY,
    int X,
    int Y,
    bool Visible,
    bool ShapeHasAlpha,
    byte[] Shape)
{
    /// <summary>形状在缓冲里占的行数（<c>MONOCHROME</c> 是双掩码 ⇒ 2 倍）。</summary>
    internal int ShapeRows => Type == PointerShapeType.Monochrome ? ActualHeight * 2 : ActualHeight;

    /// <summary>形状至少需要多少字节。</summary>
    internal int NeededBytes => Pitch * ShapeRows;

    /// <summary>指针左上角在画面里的坐标（热点要从位置里减掉）。</summary>
    internal (int X, int Y) Origin => (X - HotSpotX, Y - HotSpotY);

    /// <summary>"什么都没有"（还没抓到过指针，或指针形状一直没拿到）。</summary>
    internal static readonly PointerSnapshot None = new(
        PointerShapeType.Color, 0, 0, 0, 0, 0, 0, 0, Visible: false, ShapeHasAlpha: false, Shape: []);
}

/// <summary>
/// <b>把指针合成进 BGRA 背景</b>（M5.4-b 第 5 步的核心，纯函数、可脱离设备测）。
///
/// <para>
/// 🔥 <b>为什么要有这一层</b>：DDA 给的桌面图里<b>从来没有</b>指针
/// （<c>WindowsDxgiScreenCapturer</c> 里 <c>IsCursorIncluded</c> 恒 <c>false</c> 是如实上报）；
/// 而 DDA 又单独给了指针形状与位置。不做合成的话，观看端看到的画面里<b>没有鼠标</b>，
/// 现场会直接判定"这软件不实时"。
/// </para>
///
/// <para>
/// 🔥 <b>为什么放在 CPU 上做</b>：三态里有两态（<c>MASKED_COLOR</c> 的逐位 XOR、
/// <c>MONOCHROME</c> 的 <c>(dst AND and) XOR xor</c>）是<b>位运算</b> ——
/// 硬件的固定功能混合单元只会 <c>src α over dst</c>，做不了这两件事。
/// 而按 CPU 做的时候，<b>颜色转换仍交给 VideoProcessor</b>（见调用方），
/// 所以不会出现"指针那一块的颜色和周围对不上"的贴片感。
/// </para>
///
/// <para>
/// ⚠️ 坐标约定：本类只认"画面坐标"。调用方负责把"指针所在的画面矩形"读回来
/// （<see cref="TryGetRegion"/> 会给这个矩形），再原样交给 <see cref="Composite"/>。
/// </para>
/// </summary>
internal static class PointerCompositor
{
    /// <summary>
    /// 算出"为了合成指针，需要从桌面读回哪一块"。
    ///
    /// <para>
    /// 🔥 矩形会<b>向外扩到偶数</b>：这块 BGRA 合成完还要经 VideoProcessor 转成 NV12 写回输出，
    /// 而 NV12 的色度是 2×2 —— 奇数边界会让这一趟的色度与"整屏那一趟"对不齐。
    /// 扩出来的那些像素读回来的<b>就是原始背景</b>，覆盖回去等于没改（无损）。
    /// </para>
    /// </summary>
    /// <returns>
    /// <c>false</c> ＝ <b>不需要合成</b>（指针不可见 / 完全在画面外 / 形状不完整）。
    /// <paramref name="reason"/> 会写清是哪一种 —— 它是给人看的，不是给分支用的。
    /// </returns>
    internal static bool TryGetRegion(
        PointerSnapshot pointer,
        int frameWidth,
        int frameHeight,
        out DxgiInterop.Rect region,
        out string? reason)
    {
        region = default;
        reason = null;

        if (frameWidth <= 1 || frameHeight <= 1)
        {
            reason = $"画面尺寸 {frameWidth}×{frameHeight} 不合法。";

            return false;
        }

        if (!pointer.Visible)
        {
            reason = "指针当前不可见（PointerPosition.Visible == 0）。";

            return false;
        }

        if (pointer.Width <= 0 || pointer.ActualHeight <= 0 || pointer.Pitch <= 0)
        {
            reason = $"指针形状的尺寸不合法（{pointer.Width}×{pointer.ActualHeight}，Pitch {pointer.Pitch}）。";

            return false;
        }

        if (pointer.Shape.Length < pointer.NeededBytes)
        {
            reason = $"指针形状缓冲不够：需要 {pointer.NeededBytes} 字节，手上只有 {pointer.Shape.Length} 字节"
                + $"（{pointer.Type}，{pointer.Width}×{pointer.ActualHeight}，Pitch {pointer.Pitch}）。";

            return false;
        }

        var (originX, originY) = pointer.Origin;

        if (originX + pointer.Width <= 0 || originY + pointer.ActualHeight <= 0
            || originX >= frameWidth || originY >= frameHeight)
        {
            reason = $"指针整个在画面外（热点把左上角推到了 {originX},{originY}）。";

            return false;
        }

        var left = Math.Max(0, originX);
        var top = Math.Max(0, originY);
        var right = Math.Min(frameWidth, originX + pointer.Width);
        var bottom = Math.Min(frameHeight, originY + pointer.ActualHeight);

        left &= ~1;
        top &= ~1;
        right = Math.Min(frameWidth, (right + 1) & ~1);
        bottom = Math.Min(frameHeight, (bottom + 1) & ~1);

        if (right - left <= 0 || bottom - top <= 0)
        {
            reason = "指针矩形扩到偶数之后成了空矩形（画面太小）。";

            return false;
        }

        region = new DxgiInterop.Rect { Left = left, Top = top, Right = right, Bottom = bottom };

        return true;
    }

    /// <summary>
    /// 把指针<b>原地</b>合成到背景小块上。
    ///
    /// <para>
    /// <paramref name="regionX"/> / <paramref name="regionY"/> 是这个小块在<b>画面坐标</b>里的原点；
    /// 指针超出小块的像素会被安静跳过 —— 越界是<b>正常情形</b>（指针贴边时矩形会被裁），
    /// 不是错误。
    /// </para>
    /// </summary>
    /// <param name="background">BGRA 小块（本方法直接改它）。</param>
    /// <param name="backgroundPitch">小块的每行字节数（<b>必须用真实 RowPitch</b>，不是 width×4）。</param>
    internal static void Composite(
        PointerSnapshot pointer,
        int regionX,
        int regionY,
        Span<byte> background,
        int backgroundPitch,
        int regionWidth,
        int regionHeight)
    {
        var (originX, originY) = pointer.Origin;

        var baseX = originX - regionX;
        var baseY = originY - regionY;

        var shape = pointer.Shape.AsSpan();

        switch (pointer.Type)
        {
            case PointerShapeType.Monochrome:
                CompositeMonochrome(
                    shape, pointer.Width, pointer.ActualHeight, pointer.Pitch,
                    background, backgroundPitch, baseX, baseY, regionWidth, regionHeight);

                break;

            case PointerShapeType.MaskedColor:
                CompositeMaskedColor(
                    shape, pointer.Width, pointer.ActualHeight, pointer.Pitch,
                    background, backgroundPitch, baseX, baseY, regionWidth, regionHeight);

                break;

            default:
                CompositeColor(
                    shape, pointer.Width, pointer.ActualHeight, pointer.Pitch, pointer.ShapeHasAlpha,
                    background, backgroundPitch, baseX, baseY, regionWidth, regionHeight);

                break;
        }
    }

    /// <summary>
    /// <c>COLOR</c>：32bpp BGRA，<c>dst = src α over dst</c>。
    /// <para>
    /// ⚠️ <paramref name="hasAlpha"/> 为 <c>false</c>（整份形状的 α 全 0）时<b>忽略 α 通道、
    /// 整块不透明覆盖</b> —— 否则那种位图会被画成"什么都没有"。
    /// </para>
    /// </summary>
    private static void CompositeColor(
        ReadOnlySpan<byte> shape, int width, int height, int pitch, bool hasAlpha,
        Span<byte> background, int backgroundPitch,
        int baseX, int baseY, int regionWidth, int regionHeight)
    {
        for (var py = 0; py < height; py++)
        {
            var by = baseY + py;

            if (by < 0 || by >= regionHeight) continue;

            var sourceRow = py * pitch;
            var destinationRow = by * backgroundPitch;

            for (var px = 0; px < width; px++)
            {
                var bx = baseX + px;

                if (bx < 0 || bx >= regionWidth) continue;

                var si = sourceRow + (px * 4);
                var di = destinationRow + (bx * 4);

                var alpha = hasAlpha ? shape[si + 3] : (byte)255;

                if (alpha == 0) continue;

                if (alpha == 255)
                {
                    background[di] = shape[si];
                    background[di + 1] = shape[si + 1];
                    background[di + 2] = shape[si + 2];
                    background[di + 3] = 255;

                    continue;
                }

                BlendOver(background, di, shape, si, alpha);
            }
        }
    }

    /// <summary>
    /// <c>MASKED_COLOR</c>：<b>RGB 与背景逐位 XOR</b>，α 无效（该像素恒不透明）。
    ///
    /// <para>
    /// ⚠️ 把它当普通位图直接画，症状是<b>指针在深色背景上变成一个黑洞</b> ——
    /// 因为"要 XOR 的颜色"里全是 0（黑），直接画上去就是一块黑。
    /// </para>
    /// </summary>
    private static void CompositeMaskedColor(
        ReadOnlySpan<byte> shape, int width, int height, int pitch,
        Span<byte> background, int backgroundPitch,
        int baseX, int baseY, int regionWidth, int regionHeight)
    {
        for (var py = 0; py < height; py++)
        {
            var by = baseY + py;

            if (by < 0 || by >= regionHeight) continue;

            var sourceRow = py * pitch;
            var destinationRow = by * backgroundPitch;

            for (var px = 0; px < width; px++)
            {
                var bx = baseX + px;

                if (bx < 0 || bx >= regionWidth) continue;

                var si = sourceRow + (px * 4);
                var di = destinationRow + (bx * 4);

                background[di] ^= shape[si];
                background[di + 1] ^= shape[si + 1];
                background[di + 2] ^= shape[si + 2];
                background[di + 3] = 255;
            }
        }
    }

    /// <summary>
    /// <c>MONOCHROME</c>：1bpp 双掩码，逐位 <c>dst = (dst AND andMask) XOR xorMask</c>。
    ///
    /// <para>
    /// 🔥 展开成四种情形（本方法实际做的就是这张表）：
    /// </para>
    /// <list type="table">
    /// <item><term>and=1, xor=0</term><description>保留背景（唯一"透明"的那个）</description></item>
    /// <item><term>and=1, xor=1</term><description>背景<b>反色</b></description></item>
    /// <item><term>and=0, xor=0</term><description>全 0（黑）</description></item>
    /// <item><term>and=0, xor=1</term><description>全 1（白）</description></item>
    /// </list>
    /// <para>
    /// ⚠️ 缓冲里<b>上半是 AND、下半是 XOR</b>（所以行索引要加 <c>height</c>）。
    /// 位序是<b>高位在前</b>（同一字节里最左边那一位是最高位）。
    /// </para>
    /// </summary>
    private static void CompositeMonochrome(
        ReadOnlySpan<byte> shape, int width, int height, int pitch,
        Span<byte> background, int backgroundPitch,
        int baseX, int baseY, int regionWidth, int regionHeight)
    {
        for (var py = 0; py < height; py++)
        {
            var by = baseY + py;

            if (by < 0 || by >= regionHeight) continue;

            var andRow = py * pitch;
            var xorRow = (py + height) * pitch;
            var destinationRow = by * backgroundPitch;

            for (var px = 0; px < width; px++)
            {
                var bx = baseX + px;

                if (bx < 0 || bx >= regionWidth) continue;

                var byteIndex = px >> 3;
                var bit = 7 - (px & 7);

                var and = (shape[andRow + byteIndex] >> bit) & 1;
                var xor = (shape[xorRow + byteIndex] >> bit) & 1;

                if (and == 1 && xor == 0) continue;

                var di = destinationRow + (bx * 4);

                background[di] = MonochromeByte(background[di], and, xor);
                background[di + 1] = MonochromeByte(background[di + 1], and, xor);
                background[di + 2] = MonochromeByte(background[di + 2], and, xor);
                background[di + 3] = 255;
            }
        }
    }

    /// <summary>单色掩码作用到一个颜色通道上的结果（就是 <c>(dst AND and) XOR xor</c> 的逐位展开）。</summary>
    private static byte MonochromeByte(byte background, int and, int xor)
        => (byte)((background & (and == 1 ? 0xFF : 0x00)) ^ (xor == 1 ? 0xFF : 0x00));

    /// <summary>直通 α 混合（<c>+127</c> 是四舍五入，避免整体偏暗）。结果 α 恒为不透明。</summary>
    private static void BlendOver(Span<byte> destination, int di, ReadOnlySpan<byte> source, int si, int alpha)
    {
        var inverse = 255 - alpha;

        destination[di] = (byte)((source[si] * alpha + destination[di] * inverse + 127) / 255);
        destination[di + 1] = (byte)((source[si + 1] * alpha + destination[di + 1] * inverse + 127) / 255);
        destination[di + 2] = (byte)((source[si + 2] * alpha + destination[di + 2] * inverse + 127) / 255);
        destination[di + 3] = 255;
    }
}
