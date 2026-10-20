using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// <b>MJPEG 帧编码器</b>（M5.1 脚手架；降级链末端）。
///
/// <para>
/// 🔥 <b>先把预期摆正：这条路<b>永远</b>不可能满足 4K60。</b>
/// NVENC / QSV 都不编 JPEG，所以它<b>只有软编</b>；方案 §1.1 算过，
/// 4K 单帧要 40–80 ms ⇒ 上限 12–25 fps。它在本项目里的价值只有两条：
/// </para>
/// <list type="number">
///   <item><b>M5.1 打通链路</b> —— 用最省事的一步验证
///         「抓屏 → 编码 → 管道 → 中继 → 浏览器」整条链子；</item>
///   <item><b>降级链末端</b>（方案 §1.5）—— 连硬编都没有的机器上，
///         "降采样后的低帧率 MJPEG"是<b>还能看见画面</b>的最后一档。</item>
/// </list>
///
/// <para>
/// 🔥 <b>所以它主动降采样，而不是硬编原尺寸。</b>原尺寸硬编的后果不是"慢"，
/// 是<b>把被控机的 CPU 吃光</b> —— 而那台机器正在跑展厅业务，
/// 用户的感受是"开了远控，我的程序卡了"。降采样到长边
/// <see cref="DefaultMaxLongEdge"/> 后，1080p 左右的画面在这个质量下约 5–15 ms/帧，
/// 是能长期开着的量级。
/// </para>
///
/// <para>
/// <b>为什么用 GDI+（<c>System.Drawing.Common</c>）而不是 Media Foundation</b>：
/// MF 里能编 JPEG 的只有 WIC 那条链（一堆 COM 接口），换来的收益是零 ——
/// 这条路本来就是脚手架，M5.4 一到就整体换成 <c>IVideoEncoder</c>（MF 硬编）。
/// 在脚手架上下重注，是方案 §1.1 明确警告过的做法。
/// </para>
///
/// <para>
/// ⚠️ <b>GDI+ 的 JPEG 是 4:2:0</b> ⇒ 彩色文字边缘会有色边（方案 §6.3）。
/// 这正是"黑白模式"能<b>免费</b>换到文字锐度的原因（色度被压成常量，
/// 色边在原理上不可能产生）—— 见 <see cref="Grayscale"/>。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GdiJpegFrameEncoder : IFrameEncoder, IScalableFrameEncoder
{
    /// <summary>默认降采样长边（像素）。</summary>
    /// <remarks>
    /// 🔥 <b>它只是"没拿到画面尺寸时"的兜底，不再是一路流的上限</b>（M7.10）：
    /// 开流时 <c>RemoteStreamHost</c> 会按<b>实际抓屏尺寸</b>把它改成
    /// `clamp(长边, 1920, 3840)`（见 <c>RemoteFrameExtent.ResolveMaxLongEdge</c>）。
    ///
    /// <para>
    /// 钉死 1920 的代价是<b>按屏数摊薄</b>的：单屏 1080p 正好，三屏 5760 宽就变成
    /// 每块屏只剩 640 宽。缩略图状态看不出来，一点全屏就满屏糊 ——
    /// 而同一台机器的单屏是全分辨率，于是用户会说"合并显示的全屏效果差得多"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 为什么 1920 仍是<b>下限</b>：单屏 1080p 是"能长期开着"的那条基准线
    /// （2160×1440 实测编码 ≈27 ms）；低于它只会更糊，省下的 CPU 已经不值钱了。
    /// </para>
    /// </remarks>
    public const int DefaultMaxLongEdge = 1920;

    /// <summary>默认 JPEG 质量。</summary>
    /// <remarks>
    /// ⚠️ 它与码流大小是<b>指数</b>关系（70 → 85 大约是翻倍），
    /// 而 M5.1 的上行通道受 SignalR 单条消息上限约束（见
    /// <c>AgentHubClientMethods.PushRemoteFrame</c> 的备注）。
    /// 调高之前先确认帧不会撞上那个上限。
    /// </remarks>
    public const int DefaultQuality = 70;

    /// <summary>
    /// 单帧字节预算的基准（约 380 KB）—— <b>按 1080p 定的</b>，实际预算会按输出像素等比放大。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔥🔸 <b>M5.4-b 之后，这个数不再是"中心允许的上限"，而是本路径自己定的预算。</b>
    /// 这一点必须说清楚，否则下一个读这段注释的人会把它当成协议约束去改。
    /// </para>
    ///
    /// <para>
    /// 它原来的推导是：中心 <c>MaximumReceiveMessageSize</c> 是 512 KB，
    /// 而 JSON 下 <c>byte[]</c> 会变成 base64（+33%）⇒ 单帧超过约 384 KB 会被中心
    /// <b>直接掐断整条连接</b>。那条约束现在<b>已经放宽</b>（上限抬到了
    /// <c>RemoteStreamLimits.SignalRMessageBytes</c> 的 8 MB，理由见那个类型，
    /// 且已经按 <c>RemoteStreamLimits.MaxChunkPayloadBytes</c> 分片），
    /// 所以"再大一点就会被掐断"这句话<b>今天不成立了</b>。
    /// </para>
    ///
    /// <para>
    /// 🔥🔸 <b>M7.10 起它变成了"基准"而不是"硬上限"</b>：合并显示把长边提到 3840 之后，
    /// 输出像素是 1080p 的 4 倍。如果预算仍按 380 KB 死守，编码器会把一帧好画面
    /// <b>降质量重编到 q20</b> —— 于是"分辨率提上去了、画面反而更糊"，
    /// 而且多花 2–3 次 Save 的时间。所以实际预算 = 基准 × (输出像素 / 1080p 像素)，
    /// 上限见 <see cref="MaxAutoFrameBytesCap"/>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 它与 <c>RemoteStreamProtocol.MaxPayloadBytes</c>（管道硬上限）是<b>两个不同的问题</b>：
    /// 这里是"我想用多大"，那里是"链路装得下多大"。本类先按自己的预算压，
    /// 万一还是超了管道上限，托盘那条流会按"丢一帧 + 下一帧补关键帧"处置
    /// （见 <c>RemoteStreamHost</c>）—— 那一步不会再升级成断流。
    /// </para>
    /// </remarks>
    public const int DefaultMaxFrameBytes = 380 * 1024;

    /// <summary>
    /// 按像素等比放大后的预算上限（4 MiB）。
    /// <para>
    /// ⚠️ 它不是"想要多大都行"：8K 拼接（长边会被夹到 3840）也只到 1.5 MiB 左右。
    /// 留 4 MiB 是为了挡住"基准被调大 + 超大画面"两个因素相乘之后的意外值 ——
    /// 真撞上它说明有人在拿 MJPEG 传不该传的东西，那时降质量比忠实传过去更有用。
    /// </para>
    /// </summary>
    public const int MaxAutoFrameBytesCap = 4 * 1024 * 1024;

    /// <summary>预算基准对应的像素数（1080p）。放大倍数就是"输出像素 ÷ 它"。</summary>
    private const double BudgetBaselineMegapixels = 1920.0 * 1080.0 / 1_000_000.0;

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>
    /// 降采样长边上限。<b>可运行时改</b>（M7.10，实现 <see cref="IScalableFrameEncoder"/>）。
    /// <para>
    /// ⚠️ 用 <see cref="Volatile"/> 读写成对：写的是开流那条命令线程，
    /// 读的是推流循环线程 —— 撕裂读不会崩，但会让某一帧突然按错误的尺寸编。
    /// </para>
    /// </summary>
    private int _maxLongEdge;

    private readonly int _quality;

    /// <summary>单帧字节预算的<b>基准</b>（按 1080p 计）；见 <see cref="DefaultMaxFrameBytes"/>。</summary>
    private readonly int _maxFrameBytesBase;

    /// <param name="maxLongEdge">降采样后的长边上限；<c>0</c> 或负数 = 不降采样（<b>不建议</b>）。</param>
    /// <param name="quality">JPEG 质量 1–100。</param>
    /// <param name="maxFrameBytes">
    /// 单帧字节预算的<b>基准</b>（按 1080p 计，实际按输出像素等比放大，封顶
    /// <see cref="MaxAutoFrameBytesCap"/>）；<c>0</c> = 不限制。
    /// </param>
    public GdiJpegFrameEncoder(
        int maxLongEdge = DefaultMaxLongEdge,
        int quality = DefaultQuality,
        int maxFrameBytes = DefaultMaxFrameBytes)
    {
        _maxLongEdge = maxLongEdge;
        _quality = Math.Clamp(quality, 1, 100);
        _maxFrameBytesBase = Math.Max(0, maxFrameBytes);
    }

    /// <inheritdoc />
    public int MaxLongEdge
    {
        get => Volatile.Read(ref _maxLongEdge);
        set => Volatile.Write(ref _maxLongEdge, value);
    }

    /// <inheritdoc />
    public string Codec => StreamCodecs.Mjpeg;

    /// <inheritdoc />
    /// <remarks>
    /// 置位后<b>下一帧就生效</b>（MJPEG 每帧独立），不存在"要等关键帧"的问题。
    /// </remarks>
    public bool Grayscale { get; set; }

    /// <summary>最近一次"没编出来"的原因（进日志用；成功时为 <c>null</c>）。</summary>
    public string? LastFailure { get; private set; }

    private long _discarded;

    /// <summary>
    /// 真的丢了多少帧。
    /// <para>
    /// ⚠️ 这条路上它与"<c>TryEncode</c> 返回 <c>false</c>"<b>恰好等价</b>（GDI+ 是同步的，
    /// 一次调用就是一帧：要么出码流、要么这一帧没了，没有"晚一次才到"那回事）。
    /// 但接口上仍然分开报 —— 因为 <c>WindowsGpuFrameEncoder</c> 那条路
    /// <b>两者并不等价</b>（异步 MFT 的输出晚一次到），调用方只能按统一口径用。
    /// </para>
    /// </summary>
    public long DiscardedFrames => Interlocked.Read(ref _discarded);

    /// <summary>记一次"这一帧真的没了"并返回 <c>false</c>（把"原因 + 计数"绑在一起，避免漏记）。</summary>
    private bool Fail(string reason)
    {
        LastFailure = reason;

        Interlocked.Increment(ref _discarded);

        return false;
    }

    /// <summary>最后一帧实际用的质量（降质量重编后它会低于构造时给的值）。</summary>
    public int LastQualityUsed { get; private set; }

    /// <inheritdoc />
    public bool TryEncode(CapturedFrame frame, bool forceKeyframe, out EncodedStreamPayload? encoded)
    {
        _ = forceKeyframe;

        encoded = null;
        LastFailure = null;

        // 🔥 抓屏时刻的**单调**读数（AV 同步用）。同步编码器在入口取一次就够 ——
        //    本次调用产出的码流就是这一帧的，不存在"晚一次才到"。
        var capturedMonoTicks = Stopwatch.GetTimestamp();

        if (frame is null || frame.Width <= 0 || frame.Height <= 0)
        {
            return Fail("抓到的帧尺寸非法。");
        }

        // Stride 可能比 宽×4 大（GDI 的行对齐）—— 少算了就是越界读。
        var required = (long)frame.Stride * frame.Height;

        if (frame.Buffer is null || frame.Buffer.LongLength < required)
        {
            return Fail($"帧缓冲区太小：{frame.Buffer?.LongLength ?? 0} < {required}。");
        }

        GCHandle pinned = default;

        try
        {
            // 直接**贴**在原始像素上建 Bitmap，不先拷一份（省一次整帧拷贝）。
            pinned = GCHandle.Alloc(frame.Buffer, GCHandleType.Pinned);

            using var source = new Bitmap(
                frame.Width,
                frame.Height,
                frame.Stride,
                PixelFormat.Format32bppRgb,
                pinned.AddrOfPinnedObject());

            var (targetWidth, targetHeight) = ChooseSize(frame.Width, frame.Height);

            // 🔥 一律**画进自己拥有的位图**，绝不原地改 source：
            //    source 贴着调用方的 byte[]，改它就是改调用方的数据。
            //    这一步同时承担降采样与灰度化（一个 DrawImage 两件事），
            //    而且不增加拷贝次数 —— 反正 JPEG 编码前也要读一遍整帧。
            using var canvas = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppRgb);

            using (var graphics = Graphics.FromImage(canvas))
            {
                graphics.InterpolationMode = InterpolationMode.Bilinear;
                graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                graphics.CompositingMode = CompositingMode.SourceCopy;

                if (Grayscale)
                {
                    using var attributes = new ImageAttributes();

                    attributes.SetColorMatrix(LumaMatrix);

                    graphics.DrawImage(
                        source,
                        new Rectangle(0, 0, targetWidth, targetHeight),
                        0, 0, frame.Width, frame.Height,
                        GraphicsUnit.Pixel,
                        attributes);
                }
                else
                {
                    graphics.DrawImage(
                        source,
                        new Rectangle(0, 0, targetWidth, targetHeight),
                        0, 0, frame.Width, frame.Height,
                        GraphicsUnit.Pixel);
                }
            }

            using var buffer = new MemoryStream(256 * 1024);

            Save(canvas, buffer, _quality);

            LastQualityUsed = _quality;

            // ── 超限时**降质量重编**，而不是把这一帧直接扔掉 ──
            //
            // 🔥 为什么不直接丢：MJPEG 的帧大小是跟着画面内容跳的（打开一个图片浏览器
            //    就能让单帧翻三倍），而丢帧会表现成**画面卡顿**。
            //    重编一次只花几毫秒（缩放/抓屏那两步都不用重做），
            //    换到的是"仍然有画面"。丢帧只作为最后的兜底。
            //
            // ⚠️ 仍然超限就必须丢：那个字节数**上不去中心那条消息**，
            //    硬发的结果是整条连接被掐断 —— 用断流换一帧，绝对不划算。
            var frameBudget = BudgetFor(targetWidth, targetHeight);

            if (frameBudget > 0 && buffer.Length > frameBudget)
            {
                var best = buffer.ToArray();

                foreach (var candidate in new[] { (int)(_quality * 0.6), (int)(_quality * 0.36), 20 })
                {
                    if (candidate >= _quality || candidate < 1) continue;

                    buffer.SetLength(0);

                    Save(canvas, buffer, candidate);

                    LastQualityUsed = candidate;
                    best = buffer.ToArray();

                    if (best.Length <= frameBudget) break;
                }

                if (best.Length > frameBudget)
                {
                    return Fail(
                        $"单帧 {best.Length} 字节，降到质量 {LastQualityUsed} 仍超过上限 {frameBudget} —— "
                        + "已丢弃这一帧（硬发会把中心那条长连接掐断）。");
                }

                encoded = new EncodedStreamPayload(
                    Codec: StreamCodecs.Mjpeg,
                    IsKeyframe: true,
                    Width: targetWidth,
                    Height: targetHeight,
                    CapturedUnixMs: frame.CapturedAt.ToUnixTimeMilliseconds(),
                    Data: best,
                    CapturedMonoTicks: capturedMonoTicks);

                return true;
            }

            encoded = new EncodedStreamPayload(
                Codec: StreamCodecs.Mjpeg,

                // MJPEG 每帧都是独立可解的画面 —— 语义上都是关键帧。
                // 报 false 会让观看端以为"要等关键帧"，白白多等一轮。
                IsKeyframe: true,
                Width: targetWidth,
                Height: targetHeight,
                CapturedUnixMs: frame.CapturedAt.ToUnixTimeMilliseconds(),
                Data: buffer.ToArray(),
                CapturedMonoTicks: capturedMonoTicks);

            return true;
        }
        catch (Exception ex)
        {
            // ⚠️ 绝不从推流循环里抛出：异常逃出去就是整条流静默死掉。
            return Fail($"JPEG 编码失败：{ex.Message}");
        }
        finally
        {
            if (pinned.IsAllocated) pinned.Free();
        }
    }

    /// <summary>
    /// 目标尺寸：长边不超过 <see cref="MaxLongEdge"/>，等比。
    /// <para>已经是小图就原样（<b>不放大</b> —— 放大只会让码流变大而画质不变好）。</para>
    /// </summary>
    private (int Width, int Height) ChooseSize(int width, int height)
    {
        // 读一次：这个值可以在推流中改（见 MaxLongEdge），同一帧读两次可能读到两个值，
        // 结果是一帧按 1920 算宽、按 3840 算高。
        var maxLongEdge = MaxLongEdge;

        if (maxLongEdge <= 0) return (width, height);

        var longEdge = Math.Max(width, height);

        if (longEdge <= maxLongEdge) return (width, height);

        var ratio = (double)maxLongEdge / longEdge;

        // 至少 2 像素：GDI+ 对 1 像素的位图行为古怪。
        return (Math.Max(2, (int)Math.Round(width * ratio)), Math.Max(2, (int)Math.Round(height * ratio)));
    }

    /// <summary>
    /// 这一帧实际可用的字节预算：<b>基准按输出像素等比放大</b>，封顶
    /// <see cref="MaxAutoFrameBytesCap"/>。
    /// <para>
    /// 🔥 <b>为什么必须随像素放大</b>：长边提到 3840 之后输出像素是 1080p 的 4 倍，
    /// 同样的画质下字节数也大致是 4 倍。预算不动的话，第一帧就会被判"超限"，
    /// 然后走降质量重编到 q20 —— 于是分辨率白提了、画面反而更糊，
    /// 还额外多花两三次 JPEG 编码的时间。
    /// </para>
    /// <para>
    /// ⚠️ <c>0</c> 仍然是"不限制"（照旧），不是"预算为 0"。
    /// </para>
    /// </summary>
    private int BudgetFor(int width, int height) =>
        ComputeFrameBudget(_maxFrameBytesBase, width, height);

    /// <summary>
    /// 预算的纯算法（<c>internal</c> 以便单测钉住；实例那个只是把基准传进来）。
    /// <para>
    /// 🔥 判据必须可单测：它一旦写错，症状是"分辨率提上去了、画面反而更糊"
    /// —— 因为每一帧都被判超限，然后降质量重编到 q20。而这件事<b>不报错</b>。
    /// </para>
    /// </summary>
    /// <param name="baseBytes">按 1080p 定的基准预算；<c>0</c> = 不限制。</param>
    /// <param name="width">这一帧实际编出来的宽。</param>
    /// <param name="height">同上，高。</param>
    internal static int ComputeFrameBudget(int baseBytes, int width, int height)
    {
        if (baseBytes <= 0) return 0;

        if (width <= 0 || height <= 0) return baseBytes;

        var megapixels = (double)width * height / 1_000_000.0;
        var scale = Math.Max(1.0, megapixels / BudgetBaselineMegapixels);
        var budget = baseBytes * scale;

        return budget >= MaxAutoFrameBytesCap ? MaxAutoFrameBytesCap : (int)budget;
    }

    /// <summary>把画布按指定质量存成 JPEG 写进 <paramref name="destination"/>。</summary>
    private static void Save(Bitmap canvas, MemoryStream destination, int quality)
    {
        // ⚠️ EncoderParameters 与 EncoderParameter 都实现 IDisposable，
        //    而且它们持有的是**原生 GDI+ 编码器参数**（不是托管小对象）——
        //    每帧漏两个，跑几小时就是"编码器突然建不起来了"。
        using var parameters = new EncoderParameters(1);

        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);

        canvas.Save(destination, JpegCodec, parameters);
    }

    /// <summary>
    /// 去饱和矩阵（BT.601 亮度权重）。
    /// <para>
    /// ⚠️ 权重取 <c>0.299 / 0.587 / 0.114</c> 而不是等权的 1/3：
    /// 等权会让红色与绿色在灰度下<b>亮度相同</b>，而人眼不是这样看的 ——
    /// 界面里靠颜色区分的地方会糊成一片。
    /// </para>
    /// </summary>
    private static readonly ColorMatrix LumaMatrix = new(new[]
    {
        new[] { 0.299f, 0.299f, 0.299f, 0f, 0f },
        new[] { 0.587f, 0.587f, 0.587f, 0f, 0f },
        new[] { 0.114f, 0.114f, 0.114f, 0f, 0f },
        new[] { 0f, 0f, 0f, 1f, 0f },
        new[] { 0f, 0f, 0f, 0f, 1f },
    });

    /// <inheritdoc />
    public void Dispose()
    {
        // 本实现不持有非托管资源：位图与句柄都在每次 TryEncode 的 using 里放掉了。
    }
}
