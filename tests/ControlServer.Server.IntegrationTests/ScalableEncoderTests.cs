using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ControlServer.Platform.Abstractions;
using ControlServer.Platform.Windows;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// <b>降采样长边能不能按画面改</b>、<b>单帧预算会不会跟着放大</b>（M7.10）。
///
/// <para>
/// 🔥 这两件事合起来才解决现场那一句"<b>合并显示的全屏效果明显不如单屏</b>"：
/// </para>
/// <list type="number">
///   <item>长边钉死 1920 ⇒ 三屏 5760 宽压完每块屏只剩 640 宽，一点全屏就满屏糊；</item>
///   <item>只提长边、不提预算 ⇒ 每一帧都被判"超过 380 KB"，降质量重编到 q20
///         —— <b>分辨率提上去了，画面反而更糊</b>，而且这件事不报错。</item>
/// </list>
///
/// <para>
/// ⚠️ 这里刻意<b>不</b>断言"提上去之后一定更好看"：那是人眼判断。
/// 断的是两条可证伪的性质 —— 尺寸变了、预算是按像素算的。
/// </para>
/// </summary>
public sealed class ScalableEncoderTests
{
    // ── 预算（纯算法，不需要真编码）──

    [Fact]
    public void The_budget_keeps_the_baseline_at_1080p()
    {
        var baseBytes = GdiJpegFrameEncoder.DefaultMaxFrameBytes;

        GdiJpegFrameEncoder.ComputeFrameBudget(baseBytes, 1920, 1080)
            .Should().Be(baseBytes, "1080p 就是基准尺寸，不该被放大也不该被缩小");
    }

    [Fact]
    public void The_budget_grows_with_output_pixels()
    {
        var baseBytes = GdiJpegFrameEncoder.DefaultMaxFrameBytes;

        // 三屏拼接提到长边 3840 ⇒ 3840×1440 = 5.53 MPix，是 1080p（2.07 MPix）的 2.67 倍。
        var wide = GdiJpegFrameEncoder.ComputeFrameBudget(baseBytes, 3840, 1440);

        wide.Should().BeGreaterThan((int)(baseBytes * 2.6));
        wide.Should().BeLessThan((int)(baseBytes * 2.8));

        // 单屏 4K（8.29 MPix）≈ 4 倍。
        GdiJpegFrameEncoder.ComputeFrameBudget(baseBytes, 3840, 2160)
            .Should().BeGreaterThan((int)(baseBytes * 3.9));
    }

    [Fact]
    public void The_budget_is_capped_instead_of_following_an_8K_span()
    {
        // 4 块 4K 拼到 15360×4320（66 MPix）会算出 24 倍的基准 —— 必须封顶。
        GdiJpegFrameEncoder.ComputeFrameBudget(GdiJpegFrameEncoder.DefaultMaxFrameBytes, 15360, 4320)
            .Should().Be(GdiJpegFrameEncoder.MaxAutoFrameBytesCap);
    }

    [Fact]
    public void A_negative_or_zero_budget_means_unlimited_not_zero()
    {
        // ⚠️ 0 的语义是"不限制"（历史约定）。把它当成"预算 0"会让每一帧都被判超限然后丢掉。
        GdiJpegFrameEncoder.ComputeFrameBudget(0, 3840, 2160).Should().Be(0);
        GdiJpegFrameEncoder.ComputeFrameBudget(-5, 3840, 2160).Should().Be(0);
    }

    [Fact]
    public void An_unknown_size_falls_back_to_the_baseline_budget()
    {
        var baseBytes = GdiJpegFrameEncoder.DefaultMaxFrameBytes;

        GdiJpegFrameEncoder.ComputeFrameBudget(baseBytes, 0, 0).Should().Be(baseBytes);
    }

    // ── 长边（要真编一帧）──

    [Fact]
    public void Raising_MaxLongEdge_after_construction_applies_to_the_next_frame()
    {
        if (!OperatingSystem.IsWindows()) return;

        var frame = SyntheticFrame(5760, 2160);

        using var encoder = new GdiJpegFrameEncoder(maxLongEdge: 1920, quality: 70, maxFrameBytes: 0);

        encoder.TryEncode(frame, forceKeyframe: true, out var before).Should().BeTrue();
        before!.Width.Should().Be(1920, "构造时给的是 1920 —— 三屏拼接按旧规则被压到长边 1920");
        before.Height.Should().Be(720);

        // 开流之后宿主按实际画面把长边提到 3840（见 RemoteFrameExtent.ResolveMaxLongEdge）。
        encoder.MaxLongEdge = 3840;

        encoder.TryEncode(frame, forceKeyframe: true, out var after).Should().BeTrue();

        after!.Width.Should().Be(
            3840, "设置之后**下一帧**就该生效，不需要重建编码器（M7.10：每块屏 640 → 1280 宽）");
        after.Height.Should().Be(1440);
    }

    [Fact]
    public void A_frame_smaller_than_the_long_edge_is_never_upscaled()
    {
        if (!OperatingSystem.IsWindows()) return;

        var frame = SyntheticFrame(1280, 720);

        using var encoder = new GdiJpegFrameEncoder(maxLongEdge: 3840, quality: 70, maxFrameBytes: 0);

        encoder.TryEncode(frame, forceKeyframe: true, out var encoded).Should().BeTrue();

        // 放大只会让码流变大，画质一点不变好 —— 所以编码器不放大。
        encoded!.Width.Should().Be(1280);
        encoded.Height.Should().Be(720);
    }

    /// <summary>造一张"像界面"的帧：白底 + 横线。避免全白/全黑让 JPEG 小得失真。</summary>
    private static CapturedFrame SyntheticFrame(int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);

        using (var graphics = Graphics.FromImage(bitmap))
        using (var pen = new Pen(Color.FromArgb(60, 60, 60), 2f))
        {
            graphics.Clear(Color.White);

            for (var y = 0; y < height; y += 40) graphics.DrawLine(pen, 0, y, width, y);
            for (var x = 0; x < width; x += 120) graphics.DrawLine(pen, x, 0, x, height);
        }

        var stride = width * 4;
        var buffer = new byte[stride * height];

        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);

        try
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, buffer, y * stride, stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return new CapturedFrame(width, height, stride, buffer, DateTimeOffset.UtcNow);
    }
}
