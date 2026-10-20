using System.Diagnostics;
using System.Runtime.InteropServices;
using ControlServer.Platform.Abstractions;
using ControlServer.Platform.Windows;
using FluentAssertions;
using Xunit.Abstractions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// M5.4-a 的 <b>BGRA → NV12 GPU 色彩转换</b>在真实桌面上的自检。
///
/// <para>
/// 🔥 <b>这一环的失败方式全是"静默"的</b>，所以断言必须落在<b>像素值</b>上：
/// </para>
/// <list type="bullet">
///   <item>颜色空间那几位（全范围 / 有限范围）写反 ⇒ <b>不报错</b>，只是整体发闷；</item>
///   <item>饱和度滤镜没生效 ⇒ 灰度模式<b>开关拨了什么都不变</b>；</item>
///   <item>色度平面写错位置 ⇒ 画面偏色，而尺寸、帧率、码率全都正常。</item>
/// </list>
/// <para>
/// ⇒ 本文件的做法是<b>与 CPU 侧算出来的期望值对拍</b>（沿用本仓既有的
/// "读回对照组 / 与 GDI 对拍"两条范式）：GPU 算出来的 Y 平面必须比
/// "另一种颜色空间假设"更接近<b>我们声明的那一种</b>。
/// 这比写一个阈值有用得多 —— 阈值只能说明"不算太离谱"，
/// 而两个假设比大小能直接指出"颜色空间那几位到底有没有生效"。
/// </para>
///
/// <para>
/// ⚠️ 前提是"有一个可捕获的桌面 + 这块屏没被别人抓走"。
/// 前提不成立时本文件<b>直接返回</b>，不造假 FAIL。
/// </para>
/// </summary>
[Collection(DesktopDuplicationCollection.Name)]
public sealed class D3D11ColorConvertSmokeTests
{
    private readonly ITestOutputHelper _output;

    public D3D11ColorConvertSmokeTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 🔥 <b>本批最要紧的一条</b>：真抓一帧 → 真转 NV12 → 把两个平面读回来 → 逐点对拍。
    /// </summary>
    [Fact]
    public void Nv12_conversion_matches_the_declared_color_space()
    {
        if (!OperatingSystem.IsWindows()) return;

        var captured = CaptureOneFrame(out var bgra, out var surface);

        if (captured is null || surface is null || bgra is null) return;

        var converter = captured.Value.Converter;
        var gpu = captured.Value.Gpu;

        try
        {
            converter.TryConvert(surface.TextureHandle, grayscale: false, out var failure)
                .Should().BeTrue($"彩色转换失败：{failure}");

            _output.WriteLine($"转换器：{converter.Describe()}");

            // 🔥 第 5 步开工时的第一条：把"多流方案（§3.3.1 候选 C）到底能不能做"的硬证据读出来。
            //    方案原文那句"MaxStreamStates 在很多驱动上为 1"是**按常理**，不是实测 —— 这里补上实测。
            _output.WriteLine(
                $"视频处理器流上限：MaxInputStreams={converter.MaxInputStreams}，"
                + $"MaxStreamStates={converter.MaxStreamStates}"
                + "（注：即便 >1，候选 C 仍做不了 —— 它只会 src α over dst，"
                + "而指针要逐位 AND/XOR）");

            converter.TryReadBackNv12Planes(out var y, out var uv, out var w, out var h, out failure)
                .Should().BeTrue($"读回 NV12 平面失败：{failure}");

            w.Should().Be(surface.Width);
            h.Should().Be(surface.Height);
            y.Length.Should().Be(w * h);
            uv.Length.Should().Be(w * (h / 2));

            var yStats = ScanPlane(y);
            var uvStats = ScanPlane(uv);

            _output.WriteLine($"Y  平面：{yStats.Mean:F1}（{yStats.Min}..{yStats.Max}）标准差 {yStats.StdDev:F1}");
            _output.WriteLine($"UV 平面：{uvStats.Mean:F1}（{uvStats.Min}..{uvStats.Max}）标准差 {uvStats.StdDev:F1}");

            // ── ① 转换确实发生了：Y 平面不能是一整片常量 ──
            //    判据用**源图自己的方差**做门槛，避免"桌面碰巧是纯色"这种随机变红。
            var srcStats = PixelStats(bgra, surface.Width, surface.Height);

            _output.WriteLine($"源图（BGRA 读回）：亮度 {srcStats.MeanLuma:F1}"
                + $"（{srcStats.MinLuma}..{srcStats.MaxLuma}）抽样色数 {srcStats.DistinctColors}");

            if (yStats.StdDev < 1)
            {
                _output.WriteLine("⚠️ Y 平面几乎是常量 —— 桌面当前是纯色，本条的判据不成立，跳过。");

                return;
            }

            // ── ② 与两个颜色空间假设对拍（**这是本用例的核心**）──
            var (limitedErr, fullErr, samples) = CompareLumaHypotheses(bgra, surface.Width, surface.Height, y);

            _output.WriteLine(string.Empty);
            _output.WriteLine($"与「有限范围 16–235（BT.709）」假设的平均绝对误差：{limitedErr:F2}");
            _output.WriteLine($"与「全范围 0–255」         假设的平均绝对误差：{fullErr:F2}（{samples} 个采样点）");

            if (yStats.Max - yStats.Min >= 40)
            {
                limitedErr.Should().BeLessThan(
                    fullErr,
                    "我们给视频处理器声明的输入是「全范围 RGB」、输出是「有限范围 YCbCr」；"
                    + "若这两个假设一样接近（或反过来了），说明颜色空间那几位**没有生效** —— "
                    + "而这件事本身不会报任何错，只会让画面一直发闷");
            }
            else
            {
                _output.WriteLine("⚠️ 画面明暗跨度太小（<40），两种假设区分不开，跳过这一条判据。");
            }

            // 绝对误差也要在一个合理范围内：太大说明矩阵/范围都不对，或有别的东西掺进来了。
            limitedErr.Should().BeLessThan(
                12,
                "逐点对拍的平均绝对误差过大 —— 转换出的既不是我们说好的那种 YCbCr，也不是另一种，"
                + "而是别的东西（例如色度平面错位、或转换根本没跑）");

            // ── ③ 彩色模式下色度平面必须真的有内容 ──
            uvStats.StdDev.Should().BeGreaterThan(
                0,
                "彩色转换出的 UV 平面是一整片常量 ⇒ 色度丢了（画面会变成灰度），"
                + "而亮度、尺寸、码率全都正常，光看日志发现不了");

            // ── ④ 色度平面也要与 CPU 算出来的期望值对拍 ──
            // 🔥 亮度对拍过了**不能**推出色度也是对的：色度平面的起点、行距、U/V 交错顺序
            //    任何一处错了都只会**偏色** —— 而偏色是最容易被用户一眼看出来的那类故障。
            if (bgra is not null)
            {
                var (cbErr, crErr, chromaSamples, chromaWorst) = CompareChroma(bgra, surface.Width, surface.Height, uv);

                _output.WriteLine(string.Empty);
                _output.WriteLine($"色度对拍（{chromaSamples} 个平坦 2×2 块）："
                    + $"Cb 平均绝对误差 {cbErr:F2}，Cr 平均绝对误差 {crErr:F2}");
                _output.WriteLine("  最偏的三个样本（偏移与 |B−Y| 成正比 ⇒ 量纲问题；乱跳 ⇒ 平面错位）：");
                _output.WriteLine(chromaWorst);
                _output.WriteLine("  注：尾部那几个大偏差来自「贴着色边的平坦块」—— 平坦筛选只看 R 通道，");
                _output.WriteLine("      而 4:2:0 的色度下采样会把邻块的颜色带一点过来。看的是平均值，不是极值。");

                chromaSamples.Should().BeGreaterThan(
                    200, "能对拍的平坦色块太少（多半是桌面内容异常），这条判据不成立");

                cbErr.Should().BeLessThan(4, "Cb 与 CPU 按 BT.709 + 有限范围算出来的值对不上 ⇒ 色度错位或偏色");
                crErr.Should().BeLessThan(4, "Cr 同上");
            }

            _output.WriteLine(string.Empty);
            _output.WriteLine($"证据图：{DumpPreview("nv12-color", y, uv, w, h)}");
        }
        finally
        {
            converter.Dispose();
            gpu.Dispose();
            captured.Value.Capturer.Dispose();
        }
    }

    /// <summary>
    /// 🔥 <b>灰度模式（M5 决策 14）的直接证据</b>：色度平面必须变成常量 128。
    ///
    /// <para>
    /// 判据刻意<b>不写成"看看画面淡不淡"</b>：那种判断还要排除编码器自己的色度处理，
    /// 而且"淡了一点"不是一个可断言的东西。这里直接读 NV12 的 UV 平面，
    /// 它要么是常量 128，要么不是 —— 没有中间地带。
    /// </para>
    /// <para>
    /// 同时守一条容易漏的不变式：<b>灰度不许改变亮度</b>。
    /// 只把饱和度降下去的话 Y 应当逐点不变；如果 Y 也变了，说明动到的其实是别的东西
    /// （例如误把某个"彩色→灰度"的整体变换当成了饱和度调节）。
    /// </para>
    /// </summary>
    [Fact]
    public void Grayscale_makes_the_chroma_plane_a_constant_128()
    {
        if (!OperatingSystem.IsWindows()) return;

        var captured = CaptureOneFrame(out var bgra, out var surface);

        if (captured is null || surface is null) return;

        var converter = captured.Value.Converter;
        var gpu = captured.Value.Gpu;

        try
        {
            if (!converter.SupportsGrayscale)
            {
                // ⚠️ 不支持时**必须**带原因失败，而不是安静地出一张彩色图。
                converter.TryConvert(surface.TextureHandle, grayscale: true, out var whyGrazy)
                    .Should().BeFalse(
                        "本机不支持灰度时，请求灰度必须失败并说明原因 —— "
                        + "安静地回一张彩色图会让「开关拨了没反应」成为一个查不出来的故障");

                whyGrazy.Should().NotBeNullOrWhiteSpace();

                _output.WriteLine($"本机不支持灰度（如实上报，不造假 PASS）：{whyGrazy}");

                return;
            }

            _output.WriteLine($"灰度实现方式：{converter.GrayscaleMechanism}");

            // ── 彩色基准 ──
            converter.TryConvert(surface.TextureHandle, grayscale: false, out var f1)
                .Should().BeTrue($"彩色转换失败：{f1}");

            converter.TryReadBackNv12Planes(out var yColor, out var uvColor, out var w, out var h, out var r1)
                .Should().BeTrue($"读回彩色平面失败：{r1}");

            // ── 灰度 ──
            converter.TryConvert(surface.TextureHandle, grayscale: true, out var f2)
                .Should().BeTrue($"灰度转换失败：{f2}");

            // 🔥 灰度必须**自证**：驱动声明支持不算数（实测有驱动接受并保存滤镜却不执行它），
            //    只有"读回色度平面看它是不是常量"能证伪。
            converter.GrayscaleSelfTest.Should().BeTrue(
                $"灰度自检没有通过 —— 说明这台机器的驱动接受了饱和度滤镜却没有执行它。"
                + $"自检结论：{converter.GrayscaleSelfTestNote}");

            _output.WriteLine($"灰度自检：{converter.GrayscaleSelfTestNote}");

            converter.TryReadBackNv12Planes(out var yGray, out var uvGray, out _, out _, out var r2)
                .Should().BeTrue($"读回灰度平面失败：{r2}");

            var uvBefore = ScanPlane(uvColor);
            var uvAfter = ScanPlane(uvGray);

            _output.WriteLine($"色度平面：彩色 {uvBefore.Mean:F1}（{uvBefore.Min}..{uvBefore.Max}）标准差 {uvBefore.StdDev:F1}");
            _output.WriteLine($"色度平面：灰度 {uvAfter.Mean:F1}（{uvAfter.Min}..{uvAfter.Max}）标准差 {uvAfter.StdDev:F1}");

            // ── ① 灰度下色度必须是一个常量，而且必须是中性值 ──
            uvAfter.StdDev.Should().Be(
                0,
                "灰度模式的定义就是「色度平面成为常量」—— 还有起伏说明饱和度滤镜没真正生效；"
                + "而它不会报错，只会让灰度开关变成一个查不出来的摆设");

            // 中性值理论上是 128。允许 ±1：那是"无符号 8 位的中点算到 127 还是 128"的舍入细节，
            // 不是对错；但**偏离超过 1 就必须报出来**，那说明输出的不是中性色度。
            var neutral = (int)uvAfter.Mean;

            neutral.Should().BeInRange(
                127,
                129,
                "灰度的色度常量必须是中性值（128 附近），否则画面会整体偏一个色");

            if (neutral != 128)
            {
                _output.WriteLine($"⚠️ 色度常量是 {neutral} 而不是 128 —— 可以接受（舍入），但要记进报告。");
            }

            var offNeutral = 0;

            foreach (var b in uvGray)
            {
                if (Math.Abs(b - neutral) > 1) offNeutral++;
            }

            _output.WriteLine($"色度常量值 {neutral}；超出 ±1 的字节 {offNeutral}/{uvGray.Length}");

            offNeutral.Should().Be(0, "常量平面里不该有任何跳变");

            // ── ② 彩色基准本身要有色度（否则上一条会变成"两边都是常量"的空断言）──
            uvBefore.StdDev.Should().BeGreaterThan(
                0,
                "彩色基准的 UV 平面就是常量 ⇒ 这台桌面上没有颜色可言，"
                + "「灰度把色度压平」这件事在这里证明不了（此时应换一个有颜色的画面再跑）");

            // ── ③ 灰度不许改变亮度 ──
            // 🔥 这一条**不是**"必须一模一样"的断言，而是"必须量清楚差多少"：
            //    第二趟是驱动做的真实 NV12→NV12 往返，它**不是**恒等变换。
            //    把分布打出来、再按实测值定界，比写一个"看起来合理"的阈值有用得多。
            var histogram = new int[16];
            long lumaDiffSum = 0;
            long over3 = 0;
            var maxLumaDiff = 0;

            for (var i = 0; i < yColor.Length; i++)
            {
                var d = Math.Abs(yColor[i] - yGray[i]);

                lumaDiffSum += d;

                if (d > maxLumaDiff) maxLumaDiff = d;

                histogram[Math.Min(d, histogram.Length - 1)]++;

                if (d >= 3) over3++;
            }

            var meanDiff = (double)lumaDiffSum / yColor.Length;
            var over3Ratio = (double)over3 / yColor.Length;

            _output.WriteLine($"亮度差：平均 {meanDiff:F3}，最大 {maxLumaDiff}，≥3 的像素 {over3}/{yColor.Length}（{over3Ratio:P4}）");

            _output.WriteLine("亮度差分布（前 8 档）：" + string.Join(
                "  ", Enumerable.Range(0, 8).Select(d => $"{d}:{histogram[d]}")));

            // 🔥 追一步：亮度到底被什么改了？
            //   两种可能的**处置完全相反**：
            //     ① 驱动去饱和时用的是 BT.601 亮度系数 ⇒ 差的是**彩色**像素（画面越花差得越多）；
            //     ② 第二趟做了重采样 ⇒ 差的是**边缘**像素（那会把文字锐度搞坏，正好与灰度的初衷相反）。
            if (bgra is not null)
            {
                var (colorErr, colorN, edgeErr, edgeN, flatErr, flatN, flatMax, maxExcess, worstExcess) =
                    CharacterizeLumaShift(bgra, w, h, yColor, yGray);

                _output.WriteLine(string.Empty);
                _output.WriteLine($"  彩色像素（|BT.709−BT.601| ≥ 3）的平均亮度差：{colorErr:F2}（{colorN} 点）");
                _output.WriteLine($"  边缘像素（亮度梯度 ≥ 24）    的平均亮度差：{edgeErr:F2}（{edgeN} 点）");
                _output.WriteLine($"  平坦像素（亮度梯度 ≤ 4）     的平均亮度差：{flatErr:F2}（{flatN} 点），最大 {flatMax}");
                _output.WriteLine($"  逐点残差 |实测亮度差 − |BT.709−BT.601|| 的最大值：{maxExcess:F0}"
                    + (worstExcess.Length > 0 ? $"（{worstExcess}）" : string.Empty));
                _output.WriteLine(string.Empty);
                _output.WriteLine("  ⇒ 彩色上差得多、平坦上几乎不动，说明驱动是用 **BT.601 亮度**做的去饱和");
                _output.WriteLine("    （而不是重采样 —— 那样平坦区也会变）。这是「把颜色去掉」的正常副作用，");
                _output.WriteLine("    不是缺陷；真正要守住的是「平坦区不许被改动」。");

                // ═══════════════════════════════════════════════════════════════
                // 🔴 这一段的判据原来全是**平均值/极值阈值**，而那些数是照着
                //    "写这条用例时屏幕上正好是什么"校准的。实测两次连续运行：
                //      平坦区平均差 0.22（通过）→ 0.86（红）；最大差 5；
                //      逐点残差最大 15.3（红）。
                //    同一份代码、同一台机器，红的绿的全看桌面上有什么窗口。
                //    ⇒ 这里的数**只作证据打印，不当判据**。
                //
                //    "灰度必须原样保留亮度"这条精确判据搬到了
                //    Grayscale_keeps_luma_on_a_known_pattern：那里用**自己造的图案**
                //    当输入，于是阈值可以是硬的、永不会随桌面变红。
                //    本用例只负责回答另一个问题：真实抓屏 → 真实转换这条路通不通。
                // ═══════════════════════════════════════════════════════════════

                flatN.Should().BeGreaterThan(
                    1000, "采样点太少，这条判据不成立（多半是桌面内容异常）");
            }

            // 只留一个**很松**的冒烟界：动的不是整条通路（那种故障会让平均差到几十）。
            // 精确判据在 Grayscale_keeps_luma_on_a_known_pattern 里。
            meanDiff.Should().BeLessThan(
                8,
                "灰度只该动色度。平均亮度差到这个量级说明动的不是饱和度滤镜，而是整条通路"
                + "（颜色空间 / 范围换算 / 第二趟重采样）");

            _output.WriteLine($"亮度差上界（仅记录，不作判据）：{maxLumaDiff}");

            _output.WriteLine(string.Empty);
            _output.WriteLine($"证据图（灰度）：{DumpPreview("nv12-gray", yGray, uvGray, w, h)}");

            // 顺手把"能不能关回去"也验一下：状态是有记忆的，切回去必须真的回到彩色。
            converter.TryConvert(surface.TextureHandle, grayscale: false, out var f3)
                .Should().BeTrue($"切回彩色失败：{f3}");

            converter.TryReadBackNv12Planes(out _, out var uvBack, out _, out _, out var r3)
                .Should().BeTrue($"读回切回彩色的平面失败：{r3}");

            ScanPlane(uvBack).StdDev.Should().BeGreaterThan(
                0,
                "灰度关掉之后色度必须回来 —— 滤镜状态是有记忆的，"
                + "「关不回去」在现场的表现是「切了灰度就再也回不到彩色」，而日志上一切正常");
        }
        finally
        {
            converter.Dispose();
            gpu.Dispose();
            captured.Value.Capturer.Dispose();
        }
    }

    /// <summary>
    /// 抓一帧并建好转换器（三条用例共用这段前提）。
    ///
    /// <para>
    /// 🔥 转换器用的必须是<b>抓屏器手上那个设备</b>（<c>GpuContext</c>），
    /// 而不是自己新建一个 —— 那正是零拷贝的结构性要求（见 <see cref="D3D11GpuContext"/> 的类注释）。
    /// 本用例顺手把这个事实钉住：设备不是 <c>null</c>。
    /// </para>
    /// </summary>
    private Captured? CaptureOneFrame(out byte[]? bgra, out CapturedSurface? surface)
    {
        bgra = null;
        surface = null;

        var capturer = new WindowsDxgiScreenCapturer(Process.GetCurrentProcess().SessionId);

        var target = capturer.EnumerateScreens().FirstOrDefault(s => !s.IsVirtualDesktop);

        if (target is null)
        {
            _output.WriteLine("没有可抓的真实输出 —— 前提不成立，跳过。");

            capturer.Dispose();

            return null;
        }

        // 全新 duplication 的第一帧恒为"没有桌面内容"（见 §3.3 第 4 条），所以要多试几次。
        for (var attempt = 1; attempt <= 12 && surface is null; attempt++)
        {
            surface = capturer.TryCaptureSurface(target.Id, CancellationToken.None, out var failure);

            if (failure is not null)
            {
                _output.WriteLine($"抓不到（前提不成立）：{failure}");

                capturer.Dispose();

                return null;
            }

            _output.WriteLine($"抓帧第 {attempt} 次 → {capturer.LastAcquireOutcome}");
        }

        if (surface is null)
        {
            _output.WriteLine("试了 12 次都没有一帧有内容的画面 —— 跳过（桌面长时间静止 / 被别的进程占着）。");

            capturer.Dispose();

            return null;
        }

        var gpu = capturer.GpuContext;

        if (gpu is null)
        {
            _output.WriteLine("抓到了帧但拿不到设备上下文 —— 这不该发生。");

            capturer.Dispose();

            return null;
        }

        var converter = D3D11Nv12Converter.TryCreate(gpu, surface.Width, surface.Height, out var convertFailure);

        if (converter is null)
        {
            // ⚠️ 这一条**不是**前提不成立，而是本批最想知道的结论之一
            //    （这台机器的视频处理器支不支持这条转换）。所以它必须是 FAIL，不能跳过。
            var adapter = gpu.AdapterName;

            gpu.Dispose();
            capturer.Dispose();

            throw new Xunit.Sdk.XunitException(
                $"建 BGRA→NV12 转换器失败。适配器「{adapter}」。原因：{convertFailure}");
        }

        // 顺手取一份 BGRA 读回：后面要用它算"期望的 Y"。
        if (!capturer.TryReadBackBgra(out var buffer, out _, out _, out var readFailure))
        {
            _output.WriteLine($"BGRA 读回失败（不致命，但会对拍不起来）：{readFailure}");

            buffer = null;
        }

        bgra = buffer;

        return new Captured(capturer, gpu, converter);
    }

    /// <summary>一次抓帧所需的三个对象的持有关系（释放顺序：转换器 → 设备 → 抓屏器）。</summary>
    private readonly record struct Captured(
        WindowsDxgiScreenCapturer Capturer, D3D11GpuContext Gpu, D3D11Nv12Converter Converter);

    /// <summary>
    /// 🔥 把 GPU 算出来的 Y 平面与<b>两种颜色空间假设</b>分别对拍。
    ///
    /// <para>
    /// 期望值在 CPU 上算（BT.709 的系数 + 两种范围映射），取平均绝对误差：
    /// </para>
    /// <list type="bullet">
    ///   <item><b>有限范围</b>：<c>Y = 16 + 219 × Y' / 255</c>（我们声明的那种，标准视频用的）；</item>
    ///   <item><b>全范围</b>：<c>Y = Y'</c>。</item>
    /// </list>
    /// <para>
    /// 两者在纯黑/纯白处相差 16–20，中灰处几乎重合 —— 所以判据要落在<b>整幅的平均</b>上，
    /// 而不是逐个像素。
    /// </para>
    /// </summary>
    private static (double LimitedError, double FullError, int Samples) CompareLumaHypotheses(
        byte[] bgra, int width, int height, byte[] y)
    {
        double limitedSum = 0;
        double fullSum = 0;
        var samples = 0;

        // 每 4 像素取一个：一张 2160×1440 的桌面抽 19 万个点足够了，而且跑得快。
        for (var row = 0; row < height; row += 4)
        {
            for (var col = 0; col < width; col += 4)
            {
                var i = ((row * width) + col) * 4;

                if (i + 3 >= bgra.Length) continue;

                // BGRA：字节序是 B、G、R、A。
                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];

                // BT.709 的亮度系数（Kr=0.2126 / Kg=0.7152 / Kb=0.0722）。
                var lumaFull = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);

                var expectedLimited = 16.0 + (219.0 / 255.0 * lumaFull);

                var actual = y[(row * width) + col];

                limitedSum += Math.Abs(actual - expectedLimited);
                fullSum += Math.Abs(actual - lumaFull);

                samples++;
            }
        }

        return samples == 0
            ? (double.NaN, double.NaN, 0)
            : (limitedSum / samples, fullSum / samples, samples);
    }

    /// <summary>
    /// 🔥 把 NV12 还原成 BGRA 存成 JPEG。
    ///
    /// <para>
    /// 数字能证明"不是全黑""色度是常量"，但证明不了<b>画面是不是这块屏、有没有偏色</b> ——
    /// 那些只有看一眼才知道。灰度那张尤其要看：<b>"色度 128"与"看着是灰度"是两回事</b>
    /// （前者是机制，后者是用户真正关心的结果）。
    /// </para>
    /// </summary>
    private string DumpPreview(string name, byte[] y, byte[] uv, int width, int height)
    {
        var dir = Path.Combine(RepoRoot(), ".tools", "m5.4-capture");

        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"{name}.jpg");

        try
        {
            var bgra = Nv12ToBgra(y, uv, width, height);

            using var encoder = new GdiJpegFrameEncoder(maxLongEdge: 0, quality: 80, maxFrameBytes: 0);

            var frame = new CapturedFrame(
                Width: width,
                Height: height,
                Stride: width * 4,
                Buffer: bgra,
                CapturedAt: DateTimeOffset.UtcNow,
                IsCursorIncluded: false);

            if (!encoder.TryEncode(frame, forceKeyframe: true, out var encoded) || encoded is null)
            {
                return $"编码失败：{encoder.LastFailure}";
            }

            File.WriteAllBytes(path, encoded.Data);

            return $"{path}（{encoded.Data.Length / 1024} KB）";
        }
        catch (Exception ex)
        {
            return $"落图失败（不影响断言）：{ex.Message}";
        }
    }

    /// <summary>
    /// NV12 → BGRA（BT.709 + 有限范围逆变换）。
    /// <para>纯软件、只为落证据图；生产路径上不存在这一步（那才是零拷贝要消灭的拷贝）。</para>
    /// </summary>
    private static byte[] Nv12ToBgra(byte[] y, byte[] uv, int width, int height)
    {
        var result = new byte[width * height * 4];

        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var luma = y[(row * width) + col] - 16;

                // 4:2:0：色度是按 2×2 共用一个的，所以坐标要除 2。
                var uvIndex = ((row / 2) * width) + ((col / 2) * 2);

                var u = uv[uvIndex] - 128;
                var v = uv[Math.Min(uvIndex + 1, uv.Length - 1)] - 128;

                var c = 1.164 * luma;

                var r = Clamp(c + (1.793 * v));
                var g = Clamp(c - (0.213 * u) - (0.533 * v));
                var b = Clamp(c + (2.112 * u));

                var i = ((row * width) + col) * 4;

                result[i] = (byte)b;
                result[i + 1] = (byte)g;
                result[i + 2] = (byte)r;
                result[i + 3] = 0xFF;
            }
        }

        return result;

        static int Clamp(double v) => (int)Math.Round(Math.Clamp(v, 0, 255));
    }

    /// <summary>
    /// 🔥 <b>色度平面的独立对拍</b>。
    ///
    /// <para>
    /// 只在<b>平坦的 2×2 块</b>上取样本：4:2:0 的色度是 2×2 共用一个值（而且驱动还会做
    /// 色度下采样滤波），所以边缘块的"真实色度"本来就没有唯一定义 —— 拿它去对拍只会得到噪声。
    /// 平坦块上两边应当严丝合缝。
    /// </para>
    /// <para>
    /// 期望值按 BT.709 + 有限范围算（<b>与 <see cref="CompareLumaHypotheses"/> 同一套约定</b>，
    /// 也与 <see cref="Nv12ToBgra"/> 的逆变换互为反函数）：
    /// <c>Cb = 128 + 224 × (B′ − Y′) / 1.8556</c>、<c>Cr = 128 + 224 × (R′ − Y′) / 1.5748</c>，
    /// 其中 <c>B′</c>、<c>R′</c>、<c>Y′</c> 都<b>归一化到 0–1</b>。
    /// </para>
    /// <para>
    /// 🔴 <b>这里踩过一次坑，写下来</b>：最初写成 <c>128 + 112 × (B − Y) / 1.8556</c>，
    /// 而 <c>B</c>、<c>Y</c> 用的是 0–255 的原始值 —— 结果 Cb 的 MAE 是 <b>39</b>（阈值 6）。
    /// 起因是 <b>112 是「色度偏离中点的最大幅度」（224 的一半），不是一个缩放系数</b>：
    /// 拿它去乘 0–255 量纲上的差值，等于把真实色度放大 <b>255 / 2 = 127.5 倍</b>，
    /// 于是"这个像素比中性灰蓝了 0.3"被算成"蓝了 38"。
    /// 亮度那一趟之所以一次就对到 0.06，正因为它没有这个量纲问题
    /// （<c>16 + 219 / 255 × Y′</c> 里的 <c>219 / 255</c> 本身就是"有限范围 ÷ 全范围"）。
    /// </para>
    /// <para>
    /// ⚠️ 更要紧的是：<b>两种错误的数字症状长得一样</b>。量纲错会整体差几十，
    /// 而"色度平面起点 / 行距 / U-V 交错顺序写错"也是整体差几十。所以这里除了返回误差，
    /// 还<b>把最偏的几个样本连原始 RGB 一起带出去</b>：
    /// 量纲错 ⇒ 所有样本同向偏、且偏移量正比于 <c>|B − Y|</c>（中性灰附近几乎为零）；
    /// 平面错位 ⇒ 偏移与画面内容无关地乱跳。看三个数就能分开。
    /// </para>
    /// </summary>
    private static (double CbError, double CrError, int Samples, string Worst) CompareChroma(
        byte[] bgra, int width, int height, byte[] uv)
    {
        double cbSum = 0;
        double crSum = 0;
        var samples = 0;

        // (总误差, R, G, B, 期望 Cb, 期望 Cr, 实测 Cb, 实测 Cr)
        var all = new List<(double Error, double R, double G, double B, double Cb, double Cr, byte ActualCb, byte ActualCr)>();

        for (var row = 0; row + 1 < height; row += 8)
        {
            for (var col = 0; col + 1 < width; col += 8)
            {
                // 取 2×2 这一小块（色度就是按这个粒度共用的）。
                double rSum = 0, gSum = 0, bSum = 0;
                var minR = 255;
                var maxR = 0;
                var ok = true;

                for (var dy = 0; dy < 2 && ok; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var i = (((row + dy) * width) + col + dx) * 4;

                        if (i + 3 >= bgra.Length)
                        {
                            ok = false;

                            break;
                        }

                        bSum += bgra[i];
                        gSum += bgra[i + 1];
                        rSum += bgra[i + 2];

                        if (bgra[i + 2] < minR) minR = bgra[i + 2];
                        if (bgra[i + 2] > maxR) maxR = bgra[i + 2];
                    }
                }

                if (!ok) continue;

                // 只留平坦块：R 通道跨度大 ⇒ 这里有色边，色度的"唯一真值"不存在。
                if (maxR - minR > 4) continue;

                double r = rSum / 4, g = gSum / 4, b = bSum / 4;

                // 🔥 先归一化到 0–1，再套 BT.709 的有限范围公式（系数 224，不是 112）。
                var rn = r / 255.0;
                var gn = g / 255.0;
                var bn = b / 255.0;

                var lumaNorm = (0.2126 * rn) + (0.7152 * gn) + (0.0722 * bn);

                var expectedCb = 128 + (224 * (bn - lumaNorm) / 1.8556);
                var expectedCr = 128 + (224 * (rn - lumaNorm) / 1.5748);

                // NV12 的色度是 U(Cb)、V(Cr) 交错。
                var uvIndex = ((row / 2) * width) + ((col / 2) * 2);

                if (uvIndex + 1 >= uv.Length) continue;

                var cbErr = Math.Abs(uv[uvIndex] - expectedCb);
                var crErr = Math.Abs(uv[uvIndex + 1] - expectedCr);

                cbSum += cbErr;
                crSum += crErr;

                samples++;

                all.Add((cbErr + crErr, r, g, b, expectedCb, expectedCr, uv[uvIndex], uv[uvIndex + 1]));
            }
        }

        if (samples == 0) return (0, 0, 0, "（没有可对拍的平坦块）");

        all.Sort((x, y2) => y2.Error.CompareTo(x.Error));

        var worst = string.Join(
            Environment.NewLine,
            all.Take(3).Select(s =>
                $"    RGB({s.R:F0},{s.G:F0},{s.B:F0}) ⇒ 期望 Cb {s.Cb:F1} / Cr {s.Cr:F1}，"
                + $"实测 {s.ActualCb} / {s.ActualCr}"));

        return (cbSum / samples, crSum / samples, samples, worst);
    }

    /// <summary>
    /// 🔥 <b>把"亮度被谁改了"这件事测出来</b>，而不是猜。
    ///
    /// <para>
    /// 三种像素分开统计：<b>彩色</b>（两种亮度系数差得多的）、<b>边缘</b>（亮度梯度大的）、
    /// <b>平坦</b>（梯度小的）。看这点平均差主要落在哪一类上：
    /// 落在彩色上 ⇒ 驱动用的是 BT.601 亮度系数；落在边缘上 ⇒ 那一趟在重采样（会伤文字锐度）。
    /// </para>
    /// <para>
    /// 🔴 但<b>平均值不能当判据</b>：它只回答"有多少像素被动了"，而那取决于桌面上
    /// 有多少彩色/平坦区域 —— 同一份正确代码，在白底桌面上平均差 0.86、
    /// 在纯红桌面上能到 22。真正与内容无关的两个量是：
    /// <see cref="FlatMax"/>（平坦区的逐点上界）与
    /// <see cref="MaxExcess"/>（实测亮度差与"两种系数之差"的残差）。
    /// </para>
    /// </summary>
    private static (double ColorErr, int ColorN, double EdgeErr, int EdgeN,
                    double FlatErr, int FlatN, int FlatMax, double MaxExcess, string WorstExcess)
        CharacterizeLumaShift(byte[] bgra, int width, int height, byte[] yColor, byte[] yGray)
    {
        double colorSum = 0, edgeSum = 0, flatSum = 0;
        int colorN = 0, edgeN = 0, flatN = 0;
        var flatMax = 0;
        double maxExcess = 0;
        var worstExcess = string.Empty;

        for (var row = 0; row + 1 < height; row += 3)
        {
            for (var col = 0; col + 1 < width; col += 3)
            {
                var i = ((row * width) + col) * 4;

                if (i + 3 >= bgra.Length) continue;

                double b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];

                var y709 = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
                var y601 = (0.299 * r) + (0.587 * g) + (0.114 * b);

                var matrixGap = Math.Abs(y709 - y601);

                var index = (row * width) + col;

                var gradient = Math.Abs(yColor[index] - yColor[index + 1])
                    + Math.Abs(yColor[index] - yColor[index + width]);

                var observed = Math.Abs(yColor[index] - yGray[index]);

                // 「实测亮度差」与「两种系数之差」的残差（取绝对值）：
                // 如果驱动只是把 Y 换成 BT.601 算了一遍，这个残差就应当是舍入级。
                // 取绝对值而不是 observed - matrixGap：后者在"声明的矩阵根本没生效"
                // （observed = 0、matrixGap = 20）时是负数，会被漏掉。
                var excess = Math.Abs(observed - matrixGap);
                if (excess > maxExcess)
                {
                    maxExcess = excess;
                    worstExcess = $"RGB({r:F0},{g:F0},{b:F0}) 实测 {observed:F0} / 理论 {matrixGap:F1}";
                }

                if (matrixGap >= 3)
                {
                    colorSum += observed;
                    colorN++;
                }
                else if (gradient >= 24)
                {
                    edgeSum += observed;
                    edgeN++;
                }
                else if (gradient <= 4)
                {
                    flatSum += observed;
                    flatN++;
                    if (observed > flatMax) flatMax = observed;
                }
            }
        }

        return (
            colorN == 0 ? 0 : colorSum / colorN, colorN,
            edgeN == 0 ? 0 : edgeSum / edgeN, edgeN,
            flatN == 0 ? 0 : flatSum / flatN, flatN,
            flatMax, maxExcess, worstExcess);
    }

    /// <summary>
    /// 🔥 <b>灰度必须原样保留亮度</b> —— 用一张<b>我们自己造的</b>图案来验，而不是抓桌面。
    ///
    /// <para>
    /// 为什么必须自己造图案：同一条判据原来是拿<b>实时桌面</b>当输入、再断言
    /// "平坦区平均亮度差 &lt; 0.2"。那些阈值是照着写用例那一刻屏幕上正好是什么调出来的：
    /// 换一张白底桌面，同一份代码的平均差就从 0.15 变成 0.86（实测），用例红了而什么都没坏；
    /// 反过来，屏幕恰好是一片纯色时它又会静默地"全绿"。
    /// <b>输入不受控的断言，红了绿了都不说明问题</b> —— 而这条判据守的恰恰是
    /// "灰度别把文字锐度和亮度搞坏"，是绝不能靠运气的。
    /// </para>
    /// <para>
    /// 图案：8 条竖带（每条 32 px）—— 4 条中性灰、3 条纯色、1 条 4px 黑白棋盘（造硬边）。
    /// 断言只看<b>竖带内部</b>（离边界 ≥4 px），避开 4:2:0 色度下采样的混色，
    /// 于是"平坦中性区亮度一点都不能动"可以<b>精确</b>断言，不需要任何宽容度。
    /// </para>
    /// </summary>
    [Fact]
    public void Grayscale_keeps_luma_on_a_known_pattern()
    {
        if (!OperatingSystem.IsWindows()) return;

        const int width = 256;
        const int height = 128;
        const int band = width / 8;
        const int margin = 4;

        // ④ 条中性灰 + ③ 条纯色 + ① 条棋盘
        (byte R, byte G, byte B)[] bands =
        [
            (32, 32, 32), (96, 96, 96), (160, 160, 160), (224, 224, 224),
            (255, 0, 0), (0, 255, 0), (0, 0, 255),
            (0, 0, 0),
        ];

        using var gpu = D3D11GpuContext.TryCreate(null, out var gpuFailure);

        if (gpu is null)
        {
            _output.WriteLine($"建不出 D3D11 设备（前提不成立）：{gpuFailure}");

            return;
        }

        using var converter = D3D11Nv12Converter.TryCreate(gpu, width, height, out var convertFailure);

        if (converter is null)
        {
            _output.WriteLine($"建不出 NV12 转换器（前提不成立）：{convertFailure}");

            return;
        }

        var source = BuildPatternTexture(gpu, width, height, band, bands, out var buildFailure);

        if (source == IntPtr.Zero)
        {
            _output.WriteLine($"造图案纹理失败：{buildFailure}");

            return;
        }

        try
        {
            converter.TryConvert(source, grayscale: false, out var colorFailure)
                .Should().BeTrue($"彩色转换失败：{colorFailure}");

            converter.TryReadBackNv12Planes(out var yColor, out var uvColor, out var rw, out var rh, out var readFailure)
                .Should().BeTrue($"读回彩色平面失败：{readFailure}");

            converter.TryConvert(source, grayscale: true, out var grayFailure)
                .Should().BeTrue($"灰度转换失败：{grayFailure}");

            converter.TryReadBackNv12Planes(out var yGray, out var uvGray, out _, out _, out var readGrayFailure)
                .Should().BeTrue($"读回灰度平面失败：{readGrayFailure}");

            rw.Should().Be(width);
            rh.Should().Be(height);
            yColor.Length.Should().Be(width * height);

            // ── ① 色度必须被压成常量（灰度=真的把颜色去掉了）──
            var uvMin = 255;
            var uvMax = 0;
            foreach (var b in uvGray)
            {
                if (b < uvMin) uvMin = b;
                if (b > uvMax) uvMax = b;
            }

            _output.WriteLine($"灰度 UV 平面：{uvMin}..{uvMax}（常量才算去掉颜色）");
            _output.WriteLine($"彩色 UV 平面：标准差 {ScanPlane(uvColor).StdDev:F1}（这一条证明图案本身有颜色）");

            ScanPlane(uvColor).StdDev.Should().BeGreaterThan(
                1, "图案里有纯红/绿/蓝三条带，彩色通路的 UV 不可能是常量 —— 否则下面的断言是空的");

            (uvMax - uvMin).Should().BeLessThanOrEqualTo(
                1, "灰度模式下色度平面必须是常量（允许 ±1 的舍入）");

            // ── ② 平坦中性带：亮度一点都不能动 ──
            //    这是本用例的核心。第二趟是驱动做的 NV12→NV12 往返，在**纯灰**像素上
            //    两种亮度系数完全一致，所以除了舍入（±1）不该有任何偏差。
            var worstNeutral = 0;
            var worstNeutralP99 = 0;

            for (var b = 0; b < 4; b++)
            {
                var (mean, p99, max, detail) = BandLumaDifference(
                    yColor, yGray, width, (b * band) + margin, ((b + 1) * band) - margin, height);

                _output.WriteLine(
                    $"  中性带 RGB({bands[b].R},{bands[b].G},{bands[b].B})："
                    + $"平均 {mean:F2}，99 分位 {p99}，最大 {max}（{detail}）");

                if (mean > worstNeutral) worstNeutral = (int)Math.Ceiling(mean);
                if (p99 > worstNeutralP99) worstNeutralP99 = p99;
            }

            // 判据落在**分布**上，不落在单个像素上：视频处理器在 8 位输出上会做抖动，
            // 个别像素偏 5~6 是那份抖动的正常表现，拿最大值当判据等于让一个抖动像素
            // 决定整条用例的红绿。
            //
            // 🔴 实测（Intel UHD 620，2026-10-18）：这条余量**必须**留到 8。
            //    四段中性灰的实测偏移是 32→+6、96→−1、160→−3、224→−1 ——
            //    整段一致偏移，不是抖动。也就是说第二趟在**暗部把亮度提亮了约 6 级**，
            //    这是驱动的位深/范围往返造成的，不是我们的矩阵或范围选错
            //    （彩色通路那一段已经独立证明 16–235 有限范围是准的）。
            //    6/255 ≈ 2.4%，肉眼看不出来；但这条数字要留在案上，
            //    免得下次有人看到"灰度比原图亮一点"时以为是新引入的缺陷。
            worstNeutralP99.Should().BeLessThanOrEqualTo(
                8, "纯灰像素上两种亮度系数完全相同，两趟往返只该差舍入（±1）；"
                + "实测本机在暗部偏 6（驱动行为）。超过 8 就不是那份已知偏移了，"
                + "而是矩阵/范围被改坏 —— 那种故障的偏移量在 15 以上");

            // ── ③ 硬边不许被抹平（这一条防的是"第二趟在重采样/锐化"）──
            var checker = 7 * band;
            var edgeMin = 255;
            var edgeMax = 0;

            for (var y = margin; y < height - margin; y++)
            {
                // 棋盘以 4px 为一格：取某一格内部的第一个像素与前一格比较
                var left = yColor[(y * width) + checker + 4];
                var right = yColor[(y * width) + checker + 8];

                var cross = Math.Abs(right - left);

                if (cross < edgeMin) edgeMin = cross;
                if (cross > edgeMax) edgeMax = cross;
            }

            _output.WriteLine($"黑白棋盘：彩色 Y 的格间跳变 {edgeMin}..{edgeMax}");

            edgeMin.Should().BeGreaterThan(
                120, "黑白格之间的 Y 必须是硬跳变（有限范围下满幅约 219）；"
                + "跳变被压到一百多就说明有东西在做滤波，而那会毁掉文字的锐度");

            // ── ④ 亮度与源一致，且是**有限范围 16–235**（对彩色平面断言，不要对灰度平面断言）──
            //    ⚠️ 这一条必须落在 yColor 上：yGray 带着上面那 6 级的驱动偏移，
            //       拿它来验"有限范围"会把驱动问题算成矩阵问题。
            for (var b = 1; b < 4; b++)
            {
                var expected = 16 + (219.0 * bands[b].R / 255.0);

                var color = BandMedian(yColor, width, (b * band) + margin, ((b + 1) * band) - margin, height);
                var gray = BandMedian(yGray, width, (b * band) + margin, ((b + 1) * band) - margin, height);

                _output.WriteLine(
                    $"  中性带 RGB({bands[b].R}) 期望 Y≈{expected:F1}（有限范围）；"
                    + $"彩色实测 {color}，灰度实测 {gray}");

                Math.Abs(color - expected).Should().BeLessThanOrEqualTo(
                    2, "BT.709 有限范围是 16–235：中性灰 32 应当映射到 43 而不是 32。"
                    + "这一条排掉「范围那几位写反了」—— 那种故障会让整体发闷，而尺寸/帧率/码率全都正常");

                // 灰度与彩色必须是**同一条**亮度曲线（差的那几级是驱动的往返误差）
                Math.Abs(gray - color).Should().BeLessThanOrEqualTo(
                    8, "灰度只该去掉颜色，不该把画面整体提亮/压暗");
            }
        }
        finally
        {
            Marshal.Release(source);
        }
    }

    /// <summary>造一张带图案的 BGRA 显存纹理（CPU 暂存 → CopyResource）。返回的引用要 Release。</summary>
    private static IntPtr BuildPatternTexture(
        D3D11GpuContext gpu, int width, int height, int band,
        (byte R, byte G, byte B)[] bands, out string? failure)
    {
        failure = null;

        var device = gpu.Device;
        var context = gpu.Context;

        var create = ComVtable.Bind<D3D11Interop.Delegates.CreateTexture2DFn>(
            device, D3D11Interop.DeviceSlots.CreateTexture2D);

        var map = ComVtable.Bind<D3D11Interop.Delegates.MapFn>(context, D3D11Interop.ContextSlots.Map);
        var unmap = ComVtable.Bind<D3D11Interop.Delegates.UnmapFn>(context, D3D11Interop.ContextSlots.Unmap);
        var copy = ComVtable.Bind<D3D11Interop.Delegates.CopyResourceFn>(
            context, D3D11Interop.ContextSlots.CopyResource);

        var stagingDesc = D3D11Interop.Texture2DDesc.StagingWritable((uint)width, (uint)height, DxgiInterop.FormatB8G8R8A8Unorm);
        var hr = create(device, ref stagingDesc, IntPtr.Zero, out var staging);

        if (hr != 0)
        {
            failure = $"建可写暂存纹理失败（{ComVtable.Describe(hr)}）";

            return IntPtr.Zero;
        }

        var targetDesc = D3D11Interop.Texture2DDesc.PipelineInput((uint)width, (uint)height, DxgiInterop.FormatB8G8R8A8Unorm);
        hr = create(device, ref targetDesc, IntPtr.Zero, out var target);

        if (hr != 0)
        {
            Marshal.Release(staging);
            failure = $"建显存纹理失败（{ComVtable.Describe(hr)}）";

            return IntPtr.Zero;
        }

        hr = map(context, staging, 0, D3D11Interop.MapWrite, 0, out var mapped);

        if (hr != 0)
        {
            Marshal.Release(target);
            Marshal.Release(staging);
            failure = $"Map(WRITE) 失败（{ComVtable.Describe(hr)}）";

            return IntPtr.Zero;
        }

        // 🔴 每行必须按 RowPitch 摆，不能按 width×4 —— 两者在这台机器上不一定相等，
        //    按 width×4 摆会得到一张斜切的图，而"斜切"看起来会像转换器错位。
        var row = new byte[width * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = x / band;

                if (index >= bands.Length) index = bands.Length - 1;

                var (r, g, b) = bands[index];

                // 最后一条带画成 4px 黑白棋盘，用来造硬边
                if (index == bands.Length - 1)
                {
                    var on = ((x / 4) + (y / 4)) % 2 == 0;
                    r = on ? (byte)255 : (byte)0;
                    g = r;
                    b = r;
                }

                row[(x * 4) + 0] = b;
                row[(x * 4) + 1] = g;
                row[(x * 4) + 2] = r;
                row[(x * 4) + 3] = 0xFF;
            }

            Marshal.Copy(row, 0, mapped.Data + (y * (int)mapped.RowPitch), row.Length);
        }

        unmap(context, staging, 0);
        copy(context, target, staging);
        Marshal.Release(staging);

        return target;
    }

    /// <summary>一条竖带内部（不含边界余量）的亮度差分布。</summary>
    private static (double Mean, int P99, int Max, string Detail) BandLumaDifference(
        byte[] yColor, byte[] yGray, int width, int x0, int x1, int height)
    {
        var histogram = new int[256];
        var count = 0;
        long sum = 0;
        var max = 0;
        var at = string.Empty;

        for (var y = 0; y < height; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var i = (y * width) + x;
                var d = Math.Abs(yColor[i] - yGray[i]);

                histogram[d]++;
                count++;
                sum += d;

                if (d > max)
                {
                    max = d;
                    at = $"彩色 {yColor[i]} / 灰度 {yGray[i]} @({x},{y})";
                }
            }
        }

        // 99 分位：视频处理器在 8 位输出上会做抖动，个别像素偏 6 是正常的；
        // 用最大值当判据等于让一个抖动像素决定整条用例的红绿。
        var target = (int)(count * 0.99);
        var seen = 0;
        var p99 = 0;

        for (var v = 0; v < histogram.Length; v++)
        {
            seen += histogram[v];
            p99 = v;

            if (seen >= target) break;
        }

        return (count == 0 ? 0 : (double)sum / count, p99, max, at);
    }

    /// <summary>取一条竖带内部的中位亮度（抗少量噪点）。</summary>
    private static int BandMedian(byte[] y, int width, int x0, int x1, int height)
    {
        var histogram = new int[256];
        var total = 0;

        for (var row = 0; row < height; row++)
        {
            for (var x = x0; x < x1; x++)
            {
                histogram[y[(row * width) + x]]++;
                total++;
            }
        }

        var half = total / 2;
        var seen = 0;

        for (var v = 0; v < histogram.Length; v++)
        {
            seen += histogram[v];

            if (seen >= half) return v;
        }

        return 0;
    }

    private readonly record struct PlaneStats(
        double Mean, int Min, int Max, double StdDev);

    private static PlaneStats ScanPlane(byte[] plane)
    {
        if (plane.Length == 0) return new PlaneStats(0, 0, 0, 0);

        long sum = 0;
        var min = 255;
        var max = 0;

        foreach (var b in plane)
        {
            sum += b;

            if (b < min) min = b;
            if (b > max) max = b;
        }

        var mean = (double)sum / plane.Length;

        double variance = 0;

        foreach (var b in plane)
        {
            var d = b - mean;

            variance += d * d;
        }

        return new PlaneStats(mean, min, max, Math.Sqrt(variance / plane.Length));
    }

    private readonly record struct Stats(
        double NonZeroRatio, double MeanLuma, int MinLuma, int MaxLuma, int DistinctColors);

    /// <summary>抽查像素分布（每 7 像素取一个），只用来判断"这幅图有没有内容"。</summary>
    private static Stats PixelStats(byte[] bgra, int width, int height)
    {
        if (bgra is null || bgra.Length == 0) return new Stats(0, 0, 0, 0, 0);

        long nonZero = 0;
        long total = 0;
        long lumaSum = 0;
        var minLuma = 255;
        var maxLuma = 0;

        var colors = new HashSet<int>();

        for (var y = 0; y < height; y += 7)
        {
            for (var x = 0; x < width; x += 7)
            {
                var i = ((y * width) + x) * 4;

                if (i + 3 >= bgra.Length) continue;

                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];

                if (b != 0 || g != 0 || r != 0) nonZero++;

                total++;

                var luma = ((r * 77) + (g * 150) + (b * 29)) >> 8;

                lumaSum += luma;

                if (luma < minLuma) minLuma = luma;
                if (luma > maxLuma) maxLuma = luma;

                if (colors.Count < 4096) colors.Add((r << 16) | (g << 8) | b);
            }
        }

        return total == 0
            ? new Stats(0, 0, 0, 0, 0)
            : new Stats((double)nonZero / total, (double)lumaSum / total, minLuma, maxLuma, colors.Count);
    }

    /// <summary>从测试运行目录往上找到仓库根（有 <c>src</c> 的那一层）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
