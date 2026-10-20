using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent.Remote;

/// <summary>
/// <b>帧头里那个"整幅画面"到底该报多大</b> —— 一个纯判据，单独钉住。
///
/// <para>
/// 🔥🔥 <b>现场代价必须写下来</b>（2026-10-19，被控端 DESKTOP-2DFUKBT，三屏拼接）：
/// </para>
/// <code>
/// 屏幕 5760×2160（三块 1920 并排）
/// 实际编码 1920×720      ← MJPEG 把长边压到 1920（GdiJpegFrameEncoder.DefaultMaxLongEdge）
/// 帧头仍报 5760×2160     ← 本类要修的就是这一句
/// ⇒ 前端画布 5760×2160、把 1920×720 画在 (0,0) ⇒ 画面只占左上 1/9，其余全黑
/// </code>
/// <para>
/// 单屏 4K 是同一个 bug 的另一种尺寸：3840×2160 压到 1920×1080 之后占左上 1/4，
/// 现场原话是"<b>4K 只显示四分之一</b>"。而两端日志、帧率、码率全都正常 ——
/// 这是典型的"不报错、只是画面不对"。
/// </para>
///
/// <para>
/// 🔥 <b>判据为什么必须由"编码能力"决定，而不是由"编码器是谁"决定</b>：
/// 帧头里的 <c>Region</c> 与 <c>FrameWidth/FrameHeight</c> 必须处在<b>同一个坐标系</b>，
/// 否则前端把一块画到画布上时比例就是错的。而这个坐标系是谁，取决于编码器有没有
/// "只编一个子矩形"的能力：
/// </para>
/// <list type="bullet">
///   <item>
///     <b>支持区域编码</b>（GPU 硬编，<see cref="IRegionSurfaceEncoder"/>）：
///     <c>EncodeRegion</c> 的契约是 <b>1:1、不缩放</b>，所以整幅恒等于源纹理尺寸，
///     <c>Region</c> 的坐标也基于它。
///   </item>
///   <item>
///     <b>不支持区域编码</b>（MJPEG 等内存路径）：它<b>编的就是整幅</b>，
///     但它会主动降采样来省 CPU。这时整幅在码流里<b>只有 payload 那么大</b>。
///   </item>
/// </list>
///
/// <para>
/// ⚠️ <b>旧的写法是无条件用屏幕尺寸</b>，理由是"内存路径按屏尺寸填"。
/// 那个假设在 <see cref="GdiJpegFrameEncoder"/> 引入降采样之后就已经不成立了，
/// 但因为降采样"看起来像是故意做的、而且确实省 CPU"，没人把它和
/// "画面缩在左上角"联系起来。
/// </para>
///
/// <para>
/// ⚠️ <b>退化时的取法</b>：两边都可能拿不到（屏尺寸为 0 / payload 尺寸为 0）。
/// 这时优先用<b>另一个</b>，因为"声明得比内容大"会让画面缩在左上，
/// "声明得比内容小"会把画面裁掉 —— 两个都不报错，很难查。宁可退到"整幅 = 这一帧"。
/// </para>
/// </summary>
public static class RemoteFrameExtent
{
    /// <summary>
    /// 算这一帧的"整幅尺寸"（帧头里 <c>FrameWidth/FrameHeight</c> 的值）。
    /// </summary>
    /// <param name="keepsNativeSize">
    /// 编码器是否承诺 <b>1:1、不缩放</b>（即实现了 <see cref="IRegionSurfaceEncoder"/>）。
    /// <c>true</c> ⇒ 整幅 = 源画面尺寸；<c>false</c> ⇒ 整幅 = 实际编出来的尺寸。
    /// </param>
    /// <param name="screenWidth">会话抓的那块屏（或拼接后的虚拟桌面）的宽度。</param>
    /// <param name="screenHeight">同上，高度。</param>
    /// <param name="payloadWidth">这一帧实际编出来的宽度。</param>
    /// <param name="payloadHeight">这一帧实际编出来的高度。</param>
    public static (int Width, int Height) Resolve(
        bool keepsNativeSize,
        int screenWidth,
        int screenHeight,
        int payloadWidth,
        int payloadHeight)
    {
        var screenKnown = screenWidth > 0 && screenHeight > 0;
        var payloadKnown = payloadWidth > 0 && payloadHeight > 0;

        if (keepsNativeSize)
        {
            // 1:1 的编码器：整幅恒是源尺寸。拿不到就退到 payload —— 那至少是"这一帧多大"，
            // 比声明成 0（前端直接不画）要好。
            return screenKnown ? (screenWidth, screenHeight) : (payloadWidth, payloadHeight);
        }

        // 编的就是整幅（可能已经降采样）⇒ 整幅在码流里的尺寸就是 payload 的尺寸。
        return payloadKnown ? (payloadWidth, payloadHeight) : (screenWidth, screenHeight);
    }

    /// <summary>
    /// <b>这一路该按多大的长边去编</b>（M7.10）—— 决定"降采样还能剩多少细节"。
    ///
    /// <para>
    /// 🔥🔥 <b>为什么不能钉死 1920</b>（2026-10-19 现场反馈"合并显示的全屏效果明显不如单屏"）：
    /// 降采样上限是<b>按整幅</b>算的，而整幅的宽度<b>随屏数增加</b>。
    /// 三块 1920 并排（虚拟桌面 5760×2160）压到长边 1920，等于每块屏只剩 <b>640 宽</b>；
    /// 缩略图状态下看着"还行"，一点全屏把画面放大三倍就满屏都是糊的 ——
    /// 而同一台机器的单屏走原生分辨率，两相对比就成了"合并显示的全屏没法看"。
    /// </para>
    ///
    /// <para>
    /// 🔥 <b>为什么敢往上提：瓶颈不在输出尺寸</b>。实测（5760×2160 源，q70）：
    /// </para>
    /// <code>
    /// 长边   目标尺寸     缩放      编码     合计     等效帧率
    /// 1920   1920×720     95 ms    20 ms    116 ms   8.7 fps
    /// 3840   3840×1440   121 ms    60 ms    182 ms   5.5 fps
    /// 5760   5760×2160    34 ms   128 ms    162 ms   6.2 fps
    /// </code>
    /// <para>
    /// 从 1920 提到 3840，成本只涨约 <b>40 %</b>（每帧多 66 ms），因为<b>贵的是"从 12.4 MPix
    /// 的源里读一遍再缩放"，不是"写多大"</b>：缩放耗时几乎与目标尺寸无关。
    /// 而清晰度是<b>每块屏 640 → 1280 宽</b>，线性翻倍。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>为什么封顶 3840 而不是"有几屏就乘几"</b>：8K 拼接（4 块 4K = 15360 宽）
    /// 若按屏数放大，JPEG 会跑到 12 MPix 以上、单帧十几 MB，
    /// 那是"用 MJPEG 传 8K"，怎么调都是错的。3840 是<b>让步的终点</b>：
    /// 再往上应该切到单屏（硬编 + 原生分辨率），而不是继续加 MJPEG 的分辨率。
    /// </para>
    /// </summary>
    /// <param name="screenWidth">这一路抓的那块屏 / 虚拟桌面的宽（&lt;= 0 表示还不知道）。</param>
    /// <param name="screenHeight">同上，高。</param>
    public static int ResolveMaxLongEdge(int screenWidth, int screenHeight)
    {
        // ⚠️ 与 <see cref="Resolve"/> 同一条纪律：**部分已知 = 未知**。
        //    宽有高没有（或反之）拿 max 去算会得到一个"看起来合理"的数，
        //    而那个数只对了一半的输入负责。
        if (screenWidth <= 0 || screenHeight <= 0) return MinMaxLongEdge;

        var longEdge = Math.Max(screenWidth, screenHeight);

        return Math.Clamp(longEdge, MinMaxLongEdge, MaxMaxLongEdge);
    }

    /// <summary>
    /// 降采样长边的下限：<b>单屏 1080p</b>。
    /// <para>
    /// ⚠️ 低于它只是更糊，省下的 CPU 已经不值钱（1920×720 的编码只要 20 ms，
    /// 而抓屏与缩放那 100 ms 一点没少）。
    /// </para>
    /// </summary>
    public const int MinMaxLongEdge = 1920;

    /// <summary>
    /// 降采样长边的上限：<b>3840</b>（约等于两块 1080p 并排的原生宽度）。
    /// <para>
    /// 见 <see cref="ResolveMaxLongEdge"/> 里"为什么封顶"那段：再大就该换路径，而不是继续加分辨率。
    /// </para>
    /// </summary>
    public const int MaxMaxLongEdge = 3840;
}
