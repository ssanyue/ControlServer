using System.Diagnostics;
using System.Runtime.InteropServices;
using ControlServer.Platform.Abstractions;
using ControlServer.Platform.Windows;
using FluentAssertions;
using Xunit.Abstractions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// M5.4-b 第 10 步 —— <b>指针叠加那一刀</b>（<c>D3D11Nv12Converter.TryOverlay</c>）的真机自检。
///
/// <para>
/// 🔥 <b>它钉住的是一个已经吃过亏的尺寸约定</b>：这条链上有三个"回了成功也未必做成"的地方 ——
/// <c>UpdateSubresource</c> 返回 <c>void</c>、<c>VideoProcessorSetStreamSourceRect</c> /
/// <c>VideoProcessorSetStreamDestRect</c> 也返回 <c>void</c>（设错了不报错，只安静地按旧状态走）、
/// 而 <c>VideoProcessorBlt</c> 更是<b>照样回 <c>S_OK</c></b> —— 所以判据<b>只能落在像素上</b>。
/// </para>
///
/// <para>
/// 🔴 <b>它出生时是红的</b>：当时的实现把"指针那一小块"建成了 region 尺寸的纹理，
/// 而 VideoProcessor 在创建时声明的是<b>输入＝整屏</b>（且本仓<b>从不</b>调
/// <c>VideoProcessorSetStreamFormat</c> 去改它）⇒ 实测「矩形内亮度变化 56.5、
/// <b>矩形外 183.00</b>」：指针写进去了，<b>整屏也被写坏了</b>，
/// 于是观看端看到的是"没有鼠标"（被控端却报「画上 140 帧 / 失败 0 帧」）。
/// </para>
///
/// <para>
/// ⚠️ 做法是"<b>同一帧内前后对照</b>"：转换一次 → 读回平面 → 叠加 → 再读回平面。
/// 中间不插第二次转换，于是两份平面的差别<b>只可能</b>来自叠加那一趟。
/// 现在输入纹理是<b>画面尺寸</b>的、用 <c>D3D11_BOX</c> 只写 region、源与目标矩形恒等 ——
/// <b>与产品路径逐字一致</b>。
/// </para>
///
/// <para>
/// ⚠️ 前提是"有一个可捕获的桌面 ＋ 这块屏没被别人抓走"。前提不成立时<b>直接返回</b>，
/// 不造假 FAIL —— 但"转了之后叠加把整屏写坏"属于<b>结论</b>，那必须是 FAIL
/// （正是本文件存在的理由）。
/// </para>
/// </summary>
[Collection(DesktopDuplicationCollection.Name)]
public sealed class D3D11PointerOverlaySmokeTests
{
    private readonly ITestOutputHelper _output;

    public D3D11PointerOverlaySmokeTests(ITestOutputHelper output) => _output = output;

    /// <summary>要叠加的那块矩形（画面坐标；远离边缘，免得撞上"画面外"那种正常跳过）。</summary>
    private const int RegionLeft = 200;

    /// <summary>同上。</summary>
    private const int RegionTop = 200;

    /// <summary>那块矩形的边长。取 64：足够大，任何缩放/对齐的实现细节都藏不住，且是偶数（NV12 的色度是 2×2）。</summary>
    private const int RegionSize = 64;

    [Fact]
    public void Overlay_writes_only_the_target_rectangle_when_the_input_matches_the_declared_size()
    {
        if (!OperatingSystem.IsWindows()) return;

        var capturer = new WindowsDxgiScreenCapturer(Process.GetCurrentProcess().SessionId);

        var full = IntPtr.Zero;

        try
        {
            var target = capturer.EnumerateScreens().FirstOrDefault(s => !s.IsVirtualDesktop);

            if (target is null)
            {
                _output.WriteLine("没有可抓的真实输出 —— 前提不成立，跳过。");

                return;
            }

            CapturedSurface? surface = null;

            // 全新 duplication 的第一帧恒为"没有桌面内容"，所以要多试几次（沿用既有夹具的写法）。
            for (var attempt = 1; attempt <= 12 && surface is null; attempt++)
            {
                surface = capturer.TryCaptureSurface(target.Id, CancellationToken.None, out var captureFailure);

                if (captureFailure is not null)
                {
                    _output.WriteLine($"抓不到（前提不成立）：{captureFailure}");

                    return;
                }
            }

            if (surface is null)
            {
                _output.WriteLine("试了 12 次都没有一帧有内容的画面 —— 跳过（桌面长时间静止 / 被别的进程占着）。");

                return;
            }

            var gpu = capturer.GpuContext;

            gpu.Should().NotBeNull("抓到了帧却拿不到设备上下文 —— 这不该发生");

            _output.WriteLine($"适配器：{gpu!.AdapterName}｜画面 {surface.Width}×{surface.Height}");

            if (surface.Width < RegionLeft + RegionSize + 8 || surface.Height < RegionTop + RegionSize + 8)
            {
                _output.WriteLine("画面太小，放不下那块测试矩形 —— 跳过。");

                return;
            }

            var converter = D3D11Nv12Converter.TryCreate(gpu, surface.Width, surface.Height, out var createFailure);

            // ⚠️ 建不起转换器不是"前提不成立"，而是本批最想知道的结论之一 ⇒ 必须 FAIL。
            converter.Should().NotBeNull(
                $"建 BGRA→NV12 转换器失败（适配器「{gpu.AdapterName}」）：{createFailure}");

            var c = converter!;

            var region = new DxgiInterop.Rect
            {
                Left = RegionLeft,
                Top = RegionTop,
                Right = RegionLeft + RegionSize,
                Bottom = RegionTop + RegionSize,
            };

            // ── ① 整屏转一次，作为"叠加前"的基准 ──
            c.TryConvert(surface.TextureHandle, grayscale: false, out var convertFailure)
                .Should().BeTrue($"整屏转换失败：{convertFailure}");

            c.TryReadBackNv12Planes(out var yBefore, out _, out var width, out var height, out var readFailure)
                .Should().BeTrue($"读回平面失败：{readFailure}");

            // ── ② 造"要叠加的那一块"：纯洋红（与桌面内容拉开距离，免得判据被"恰好同色"糊掉）──
            var pixels = new byte[RegionSize * RegionSize * 4];

            for (var i = 0; i < RegionSize * RegionSize; i++)
            {
                pixels[i * 4 + 0] = 255;   // B
                pixels[i * 4 + 1] = 0;     // G
                pixels[i * 4 + 2] = 255;   // R
                pixels[i * 4 + 3] = 255;   // A
            }

            // 🔥 关键：纹理是**画面尺寸**的（＝ VideoProcessor 建时声明的输入尺寸），
            //    像素用 `D3D11_BOX` 只写到 region 那一个 box —— 这正是产品路径。
            full = CreateBgra(gpu, width, height, region, pixels, RegionSize * 4, out var textureFailure);

            full.Should().NotBe(
                IntPtr.Zero,
                $"建「画面尺寸的上传纹理」失败：{textureFailure} —— 这一条与产品路径用的是同一组调用"
                + "（CreateTexture2D ＋ UpdateSubresource ＋ D3D11_BOX），所以它失败也说明生产那条路建不起来");

            // ── ③ 钉住那道守卫：源与目标尺寸不等必须被拒 ──
            //    ⚠️ 不等时 VideoProcessor 会**静默缩放**（回 S_OK，画出来的指针是错的、日志上看不出异常）——
            //       这正是本仓反复吃亏的那一类。它必须在这里被按死。
            var shrunk = new DxgiInterop.Rect
            {
                Left = region.Left,
                Top = region.Top,
                Right = region.Left + (RegionSize / 2),
                Bottom = region.Top + (RegionSize / 2),
            };

            c.TryOverlay(full, region, shrunk, out var mismatchFailure)
                .Should().BeFalse(
                    "源与目标尺寸不等时 VideoProcessor 会静默缩放 —— 必须被前置拒绝，而不是画出个错的指针");

            mismatchFailure.Should().NotBeNull();
            mismatchFailure!.Should().Contain("尺寸不一致", $"实得：{mismatchFailure}");

            // ── ④ 正确路径：源 ＝ 目标 ＝ region（1:1，不缩放）──
            c.TryOverlay(full, region, region, out var overlayFailure)
                .Should().BeTrue($"TryOverlay 返回了失败：{overlayFailure}");

            // ── ⑤ 再读回 ──
            c.TryReadBackNv12Planes(out var yAfter, out _, out _, out _, out var readFailure2)
                .Should().BeTrue($"读回平面失败：{readFailure2}");

            yBefore.Length.Should().Be(yAfter.Length);

            var inside = MeanAbsDiff(yBefore, yAfter, width, region.Left, region.Top, region.Right, region.Bottom);
            var outside = MeanAbsDiffOutside(
                yBefore, yAfter, width, height, region.Left, region.Top, region.Right, region.Bottom);

            _output.WriteLine(
                $"叠加矩形 {region.Left},{region.Top}..{region.Right},{region.Bottom}"
                + $"｜矩形内亮度平均变化 {inside:F1}｜矩形外 {outside:F2}");

            // ── ⑥ 判据 ──
            inside.Should().BeGreaterThan(
                20,
                "这一趟**声称成功了**（TryOverlay 回了 true），而它没有任何返回值能证明像素真的写进去了 ——"
                + "`UpdateSubresource` 与 `VideoProcessorSetStreamDestRect` 都返回 void，设错了不报错。"
                + "「矩形内一个像素都没变」正是现场看到的症状：被控端说画上了 140 帧，观看端画面里却没有指针");

            outside.Should().BeLessThan(
                2,
                "🔴 矩形之外**必须**原封不动。修之前这里是 184.00（整屏被抹成背景色）——"
                + "成因是 `VideoProcessorSetOutputTargetRect` **从来没设过**，而 Blt 会把"
                + "target rect 内没被 stream 覆盖的部分填背景色（默认黑）。"
                + "这种错在日志上与「成功」完全一样，只有像素能分辨");

            // ── ⑦ 复位是否真的干净 ──
            //    🔥 这一条钉的是"矩形挂在处理器上、残留到下一帧"那个坑（本仓注释里警告过很多次）：
            //       **整屏那一趟依赖这些矩形处于「未设」状态**，一旦残留成指针那一小块，
            //       整屏转换就只写一个角 —— 而那画面与"抓屏错了"长得一模一样。
            //
            //    ⚠️ 前提是"桌面静止"。所以先自己验一遍前提（连转两趟比一比）：
            //       前提不成立就**如实跳过**，绝不造一个假 FAIL（铁律 9）。
            c.TryConvert(surface.TextureHandle, grayscale: false, out var quietFailureA)
                .Should().BeTrue($"整屏转换失败：{quietFailureA}");

            c.TryReadBackNv12Planes(out var quietA, out _, out _, out _, out var quietReadA)
                .Should().BeTrue($"读回平面失败：{quietReadA}");

            c.TryConvert(surface.TextureHandle, grayscale: false, out var quietFailureB)
                .Should().BeTrue($"整屏转换失败：{quietFailureB}");

            c.TryReadBackNv12Planes(out var quietB, out _, out _, out _, out var quietReadB)
                .Should().BeTrue($"读回平面失败：{quietReadB}");

            var quietDrift = MeanAbsDiff(quietA, quietB, width, 0, 0, width, height);

            if (quietDrift > 2)
            {
                _output.WriteLine(
                    $"⚠️ 桌面在这几毫秒里自己在变（连转两趟整屏的漂移 {quietDrift:F2}）——"
                    + "「复位是否干净」这条跳过，不造假 FAIL。");
            }
            else
            {
                // 复位干净 ⇒ 这一趟整屏转换的结果应当与"叠加前"那趟几乎一致。
                // 若 target rect 残留成指针那一小块，矩形外会停在上一次的 0 ⇒ 整屏平均差会接近 184。
                var residual = MeanAbsDiff(quietB, yBefore, width, 0, 0, width, height);

                _output.WriteLine($"复位后整屏重转：与「叠加前」的整屏平均漂移 {residual:F2}");

                residual.Should().BeLessThan(
                    2,
                    "🔴 output target rect 与两个流矩形都挂在处理器上（不是每次调用的参数）——"
                    + "残留到下一帧会让整屏那一趟只转一个角，而那种画面与「抓屏错了」长得一模一样。"
                    + "这条钉的就是「复位是不是真的干净」");
            }
        }
        finally
        {
            if (full != IntPtr.Zero) ComVtable.ReleasePointer(ref full);

            capturer.Dispose();
        }
    }

    /// <summary>
    /// 建一张 BGRA 纹理并把 <paramref name="pixels"/> 写到它的 <paramref name="box"/> 上
    /// （与 <c>WindowsGpuFrameEncoder.EnsurePointerTexture</c> ＋ <c>TryUploadAndOverlay</c> 同一组调用）。
    /// </summary>
    private static IntPtr CreateBgra(
        D3D11GpuContext gpu, int width, int height, DxgiInterop.Rect box, byte[] pixels, int pitch,
        out string? failure)
    {
        failure = null;

        // ⚠️ `bindFlags: 0`：它是 VideoProcessor 的**输入**，产品路径也是这么建的。
        var desc = D3D11Interop.Texture2DDesc.Plain(
            (uint)width, (uint)height, DxgiInterop.FormatB8G8R8A8Unorm, bindFlags: 0);

        var create = ComVtable.Bind<D3D11Interop.Delegates.CreateTexture2DFn>(
            gpu.Device, D3D11Interop.DeviceSlots.CreateTexture2D);

        var hr = create(gpu.Device, ref desc, IntPtr.Zero, out var texture);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"CreateTexture2D 失败（{ComVtable.Describe(hr)}）";

            return IntPtr.Zero;
        }

        // 🔥 box **就是** region（不是 0,0..w,h）：源数据只有"那一块"，写到画面上"那一个位置"。
        var d3dBox = D3D11Interop.D3D11Box.From(box);

        var update = ComVtable.Bind<D3D11Interop.Delegates.UpdateSubresourceFn>(
            gpu.Context, D3D11Interop.ContextSlots.UpdateSubresource);

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

        try
        {
            update(gpu.Context, texture, 0, ref d3dBox, handle.AddrOfPinnedObject(), (uint)pitch, 0);
        }
        finally
        {
            handle.Free();
        }

        return texture;
    }

    /// <summary>矩形内的平均绝对亮度差。</summary>
    private static double MeanAbsDiff(byte[] a, byte[] b, int width, int left, int top, int right, int bottom)
    {
        var sum = 0L;
        var count = 0;

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                sum += Math.Abs(a[y * width + x] - b[y * width + x]);

                count++;
            }
        }

        return count == 0 ? 0 : (double)sum / count;
    }

    /// <summary>矩形外的平均绝对亮度差。</summary>
    private static double MeanAbsDiffOutside(
        byte[] a, byte[] b, int width, int height, int left, int top, int right, int bottom)
    {
        var sum = 0L;
        var count = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (x >= left && x < right && y >= top && y < bottom) continue;

                sum += Math.Abs(a[y * width + x] - b[y * width + x]);

                count++;
            }
        }

        return count == 0 ? 0 : (double)sum / count;
    }
}
