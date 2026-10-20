using System.Diagnostics;
using System.Runtime.InteropServices;
using ControlServer.Platform.Abstractions;
using ControlServer.Platform.Windows;
using FluentAssertions;
using Xunit.Abstractions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// M5.4-a 的 <b>DXGI Desktop Duplication 抓屏</b>在真实桌面上的自检。
///
/// <para>
/// 🔥 <b>为什么这一段只能真跑、桩守不住</b>：从这里往下全是手写 COM ——
/// 每个 vtable 槽位号、每个结构体布局都<b>不会被编译器检查</b>。
/// 槽位错一个的后果不是崩溃，而是<b>调到一个别的函数</b>（本文件里就真出现过：
/// 从 <c>ID3D11Device</c> 上取"第 10 槽"拿到的是 <c>CreateInputLayout</c>，
/// 返回 <c>E_INVALIDARG</c>）或者拿到一张<b>全黑的图，且一个错误码都不报</b>。
/// 唯一能发现它的方式是：把像素读回来，看它到底是不是黑的。
/// </para>
///
/// <para>
/// ⚠️ 前提是"有一个可捕获的桌面 + 这块屏没被别的进程抓走"。
/// 前提不成立时本文件<b>直接返回</b>，不造假 FAIL（理由见 <see cref="GdiCaptureSmokeTests"/> 的类注释）。
/// </para>
/// </summary>
[Collection(DesktopDuplicationCollection.Name)]
public sealed class DxgiCaptureSmokeTests
{
    private readonly ITestOutputHelper _output;

    public DxgiCaptureSmokeTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// <b>已知"环境不允许抓屏"的 HRESULT</b>。
    /// <para>
    /// 🔥 这张表是<b>白名单</b>：只有它能换来一次 Skip，别的失败一律让用例红。
    /// 反过来的写法（"抓不到就跳过"）会把真回归也变成跳过 —— 那是从"假 FAIL"
    /// 换成了更坏的"假 PASS"。
    /// </para>
    /// </summary>
    private static readonly int[] EnvironmentBlockedCodes =
    [
        unchecked((int)0x80070005), // E_ACCESSDENIED：别的程序占着 / 会话不可交互 / UAC 桌面
        unchecked((int)0x887A0022), // DXGI_ERROR_NOT_CURRENTLY_AVAILABLE：同上，DXGI 的说法
    ];

    /// <summary>
    /// 断言之前先问一句：<b>这台机器现在允许抓屏吗</b>。返回 <c>true</c> = 本次跳过，调用方应当 <c>return</c>。
    ///
    /// <para>
    /// 🔥🔥 <b>为什么是"返回 true 然后 return"而不是 <c>Assert.Skip</c></b>：
    /// 本仓用的是 xunit <b>2.x</b>，它<b>没有</b>动态跳过（<c>Assert.Skip</c> 是 v3 的 API，
    /// v2 也没有可用的 <c>SkipException</c>）。所以这里只能二选一：
    /// 当成通过（<c>return</c>）或者当成失败。
    /// </para>
    ///
    /// <para>
    /// 我们选"通过 + 明确输出"，理由与 <see cref="GdiCaptureSmokeTests"/> 的类注释一致：
    /// 环境不允许抓屏时，把它报成失败会让整轮回归永远带红 —— 而那种红会训练人忽略红色，
    /// 等真回归来了就没人看了。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>但"跳过"绝对不是无条件的</b>：只有 <see cref="EnvironmentBlockedCodes"/> 里的码
    /// 才允许跳过。为了让这件事本身有判据（而不是散落在三个方法里的 if），另有
    /// <c>Environment_failure_is_either_absent_or_a_known_code</c> 那条用例专门守它 ——
    /// 出现任何别的错误码，它会红。
    /// </para>
    /// </summary>
    private bool SkipIfScreenCaptureIsBlocked(WindowsDxgiScreenCapturer capturer)
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("跳过：非 Windows，DXGI 抓屏不存在。");
            return true;
        }

        // 建得起来 ⇒ 环境没问题，继续走真正的断言。
        if (capturer.TryInitialize(null, out var failure)) return false;

        var hr = capturer.LastFailureHResult;

        if (EnvironmentBlockedCodes.Contains(hr))
        {
            _output.WriteLine(
                $"跳过：环境不允许抓屏（0x{hr:X8}）。{failure}"
                + " ⚠️ 这条用例本次没有验证任何东西 —— 想让它真的跑，先腾出这块屏（关掉录屏/投屏、解锁桌面）。");

            return true;
        }

        // 🔥 不在白名单 ⇒ 这是回归，不是环境。把码带出去，别让人靠猜。
        throw new InvalidOperationException(
            $"建 duplication 失败，且不属于已知的环境原因（0x{hr:X8}）：{failure}");
    }

    /// <summary>
    /// <b>守「跳过」这件事本身</b>：这台机器的抓屏要么能建，要么失败码在白名单里。
    ///
    /// <para>
    /// 🔥 它的作用是给 <see cref="SkipIfScreenCaptureIsBlocked"/> 上锁：
    /// 三条真冒烟在环境不可用时是"通过"（xunit v2 没有动态跳过），
    /// 于是"到底为什么没跑"只能靠这条用例来回答 ——
    /// 出现 <b>白名单之外</b>的错误码，它立刻红，而不是被三条用例一起安静地跳过。
    /// </para>
    /// </summary>
    [Fact]
    public void Environment_failure_is_either_absent_or_a_known_code()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var capturer = new WindowsDxgiScreenCapturer(Process.GetCurrentProcess().SessionId);

        if (capturer.TryInitialize(null, out var failure))
        {
            _output.WriteLine("本机抓屏可用：三条 DXGI 冒烟本次会真的跑。");
            return;
        }

        var hr = capturer.LastFailureHResult;

        _output.WriteLine($"本机抓屏不可用（0x{hr:X8}）：{failure}");

        EnvironmentBlockedCodes.Should().Contain(
            hr,
            "🔴 抓屏失败的码必须在已知的环境白名单里 —— 否则那不是环境，是回归。"
            + $"实际原因：{failure}");
    }

    /// <summary>本机所有 DXGI 适配器节点（型号 / 厂商 / LUID / 挂着哪些输出）。</summary>
    [Fact]
    public void Adapters_are_enumerable_and_report_their_outputs()
    {
        if (!OperatingSystem.IsWindows()) return;

        var adapters = D3D11GpuContext.DescribeAdapters();

        _output.WriteLine($"DXGI 适配器节点数：{adapters.Count}");

        foreach (var a in adapters)
        {
            _output.WriteLine(
                $"  #{a.Index} {a.Name}  vendor=0x{a.VendorId:X4} device=0x{a.DeviceId:X4} luid={a.Luid}");

            foreach (var o in a.Outputs)
            {
                _output.WriteLine(
                    $"      输出 {o.DeviceName} attached={o.AttachedToDesktop} {o.Width}×{o.Height} {o.Rotation}");
            }
        }

        if (adapters.Count == 0) return;

        adapters.Should().OnlyHaveUniqueItems(a => a.Index);

        _output.WriteLine(string.Empty);
        _output.WriteLine("⚠️ 节点数 ≠ 显卡块数：一块物理卡可以暴露成多个节点（本机 Intel 核显实测 2 个不同 LUID）。");
        _output.WriteLine("⚠️ 选卡必须按 output（显示器）反查 —— 按序号挑在本机就会挑到没有输出的那三个节点。");
    }

    /// <summary>
    /// 🔥 <b>对照组</b>：完全绕开抓屏，只验 <c>CreateTexture2D</c> / <c>CopyResource</c> /
    /// <c>Map</c> / <c>Unmap</c> / <c>GetDesc</c> 这一串手写声明本身对不对。
    ///
    /// <para>
    /// 做法：造一张 <b>CPU 可写</b>的暂存纹理并填一个已知图案 →
    /// <c>CopyResource</c> 到显存纹理 → 再 <c>CopyResource</c> 到一张 <b>CPU 可读</b>的暂存纹理 →
    /// <c>Map</c> 读回来逐字节比对。
    /// </para>
    /// <para>
    /// 它把"抓屏"与"读回"这两件事<b>切开</b>了：图案能原样回来 ⇒ 读回这条链子是对的，
    /// "图是黑的"就只能出在抓屏那一侧。没有这条对照组，
    /// 一张黑图到底是抓错了还是读错了，只能靠猜。
    /// </para>
    /// </summary>
    [Fact]
    public void Readback_plumbing_round_trips_a_known_pattern()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var gpu = D3D11GpuContext.TryCreate(null, out var failure);

        if (gpu is null)
        {
            _output.WriteLine($"建不出 D3D11 设备（前提不成立）：{failure}");

            return;
        }

        const int size = 16;

        var device = gpu.Device;
        var context = gpu.Context;

        var create = ComVtable.Bind<D3D11Interop.Delegates.CreateTexture2DFn>(
            device, D3D11Interop.DeviceSlots.CreateTexture2D);

        var map = ComVtable.Bind<D3D11Interop.Delegates.MapFn>(context, D3D11Interop.ContextSlots.Map);
        var unmap = ComVtable.Bind<D3D11Interop.Delegates.UnmapFn>(context, D3D11Interop.ContextSlots.Unmap);
        var copy = ComVtable.Bind<D3D11Interop.Delegates.CopyResourceFn>(
            context, D3D11Interop.ContextSlots.CopyResource);

        var writeDesc = D3D11Interop.Texture2DDesc.StagingWritable(size, size, DxgiInterop.FormatB8G8R8A8Unorm);

        create(device, ref writeDesc, IntPtr.Zero, out var source).Should().Be(0, "建可写暂存纹理");

        var midDesc = D3D11Interop.Texture2DDesc.Copyable(size, size, DxgiInterop.FormatB8G8R8A8Unorm);
        create(device, ref midDesc, IntPtr.Zero, out var middle).Should().Be(0, "建显存纹理");

        var readDesc = D3D11Interop.Texture2DDesc.Staging(size, size, DxgiInterop.FormatB8G8R8A8Unorm);
        create(device, ref readDesc, IntPtr.Zero, out var target).Should().Be(0, "建可读暂存纹理");

        try
        {
            // ⚠️ 槽位必须**从纹理对象自己**取，不能从 device 上取 —— 跨接口的槽位号不通用。
            //    这条断言独立验证了 ID3D11Texture2D::GetDesc == 10：
            //    它对**我们自己刚造的**纹理必须回出我们填进去的宽高与格式。
            var getDesc = ComVtable.Bind<D3D11Interop.Delegates.GetTexture2DDescFn>(
                middle, D3D11Interop.Texture2DSlots.GetDesc);

            getDesc(middle, out var echoed).Should().Be(0, "读回自己造的纹理描述");

            echoed.Width.Should().Be(size, "GetDesc 的槽位要是错了，这里会先炸（而不是悄悄给垃圾）");
            echoed.Height.Should().Be(size);
            echoed.Format.Should().Be(DxgiInterop.FormatB8G8R8A8Unorm);
            echoed.Usage.Should().Be(D3D11Interop.UsageDefault);
            echoed.MipLevels.Should().Be(1);
            echoed.ArraySize.Should().Be(1);

            _output.WriteLine($"GetDesc = {echoed.Width}×{echoed.Height} fmt={echoed.Format} "
                + $"usage={echoed.Usage} bind=0x{echoed.BindFlags:X} cpu=0x{echoed.CPUAccessFlags:X}");

            // ── 填图案 ──
            map(context, source, 0, D3D11Interop.MapWrite, 0, out var mapped).Should().Be(0, "Map(WRITE)");

            var pattern = new byte[size * 4];

            for (var i = 0; i < size; i++)
            {
                pattern[(i * 4) + 0] = (byte)(0x11 + i);
                pattern[(i * 4) + 1] = (byte)(0x40 + i);
                pattern[(i * 4) + 2] = (byte)(0x80 + i);
                pattern[(i * 4) + 3] = 0xFF;
            }

            for (var y = 0; y < size; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    pattern, 0, mapped.Data + (y * (int)mapped.RowPitch), pattern.Length);
            }

            unmap(context, source, 0);

            _output.WriteLine($"源行距 RowPitch={mapped.RowPitch}（width×4={size * 4}）");

            copy(context, middle, source);     // 暂存 → 显存
            copy(context, target, middle);     // 显存 → 暂存

            map(context, target, 0, D3D11Interop.MapRead, 0, out var back).Should().Be(0, "Map(READ)");

            try
            {
                var actual = new byte[size * 4];

                System.Runtime.InteropServices.Marshal.Copy(back.Data, actual, 0, actual.Length);

                var mismatches = 0;

                for (var i = 0; i < actual.Length; i++)
                {
                    if (actual[i] != pattern[i]) mismatches++;
                }

                _output.WriteLine($"读回 {actual.Length} 字节，与源不一致 {mismatches} 字节");
                _output.WriteLine($"源首行   ：{Convert.ToHexString(pattern.AsSpan(0, 16))}");
                _output.WriteLine($"读回首行 ：{Convert.ToHexString(actual.AsSpan(0, 16))}");

                mismatches.Should().Be(0, "CopyResource + Map/Unmap 这条链子必须原样搬运 —— "
                    + "不成立的话「抓到黑帧」与 DXGI 无关，是读回代码错了");
            }
            finally
            {
                unmap(context, target, 0);
            }
        }
        finally
        {
            ComVtable.ReleasePointer(ref target);
            ComVtable.ReleasePointer(ref middle);
            ComVtable.ReleasePointer(ref source);
        }
    }

    /// <summary>
    /// 🔥 <b>本文件里最要紧的一条</b>：真的抓帧、真的把像素读回来，并确认
    /// <b>交出去的每一帧都是 DDA 真的填过内容的</b>。
    ///
    /// <para>
    /// 这里守的是一个真踩到的坑（M5.4-a 实测）：
    /// <b>全新 duplication 的第一帧，<c>AccumulatedFrames</c> 恒为 0</b>，
    /// 而那一帧的纹理内容是<b>全 0</b>。
    /// 症状是"刚连上远控看见一片黑，几秒后又自己好了"——
    /// 页面上看像网络慢，日志上看一切正常。
    /// </para>
    /// <para>
    /// 断言刻意<b>不写成"像素必须非黑"</b>：那对一台真的全黑的桌面就不成立，
    /// 会变成一条随桌面内容随机变红的用例。
    /// 守的是<b>真正的那条不变式</b>：交出来的帧，<c>AccumulatedFrames</c> 必须 &gt; 0。
    /// </para>
    /// <para>
    /// 而"内容对不对"用<b>与 GDI 对拍</b>来判断（见下一条用例）。
    /// </para>
    /// </summary>
    [Fact]
    public void Every_frame_handed_out_came_from_a_populated_dda_frame()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var capturer = new WindowsDxgiScreenCapturer(Process.GetCurrentProcess().SessionId);

        if (SkipIfScreenCaptureIsBlocked(capturer)) return;

        var target = capturer.EnumerateScreens().FirstOrDefault(s => !s.IsVirtualDesktop);

        if (target is null)
        {
            _output.WriteLine("没有可抓的真实输出 —— 前提不成立，跳过。");

            return;
        }

        var frames = 0;
        var firstOutcome = WindowsDxgiScreenCapturer.AcquireOutcome.None;

        for (var attempt = 1; attempt <= 12 && frames < 3; attempt++)
        {
            var surface = capturer.TryCaptureSurface(target.Id, CancellationToken.None, out var failure);

            if (attempt == 1) firstOutcome = capturer.LastAcquireOutcome;

            if (failure is not null)
            {
                _output.WriteLine($"第 {attempt} 次失败：{failure}");

                break;
            }

            _output.WriteLine($"第 {attempt} 次 → {capturer.LastAcquireOutcome}"
                + $"（累计帧 {capturer.Diagnostics.AccumulatedFrames}，脏区 {DescribeDirty(surface?.DirtyRects)}）");

            if (surface is null) continue;

            frames++;

            surface.Width.Should().Be(target.Width);
            surface.Height.Should().Be(target.Height);
            surface.Format.Should().Be(SurfacePixelFormat.Bgra8);

            // 🔥 核心不变式（M5.4-b 之后**表述变了、含义没变**）。
            //
            //   原来是"这一帧的 AccumulatedFrames 必须 > 0"；现在是"副本必须已经被填过" ——
            //   因为纯鼠标帧（AccumulatedFrames == 0）现在**也是合法的一帧**了，
            //   它发的是副本里上一次的桌面内容（不发的话观看端的指针就是不动）。
            //   ⚠️ 但那条真正要防的东西一丝没松：**内容没被证明有效过，就不许交出去**
            //      —— 全新 duplication 的头几帧仍然被拦在外面。
            capturer.HasPopulatedDesktopCopy.Should().BeTrue(
                "交出去的帧必须建立在「桌面内容真的被填过一次」之上 —— 否则就是全新 duplication"
                + " 的头几帧（内容未定义，本机实测为全 0），交出去即「刚连上看见一片黑」");

            // 🔥 把指针三态**实测打印出来**：本机实际出现的是哪一种，此前从没有过证据。
            //    三态里 `MASKED_COLOR` / `MONOCHROME` 在真机上几乎不出现，
            //    所以那两条分支如果不在这里留下痕迹，就是"写完从没跑过"。
            _output.WriteLine($"      指针：Visible={capturer.Pointer.Visible}，"
                + $"形状 {capturer.Pointer.Type} {capturer.Pointer.Width}×{capturer.Pointer.ActualHeight}"
                + $"（Pitch {capturer.Pointer.Pitch}，热点 {capturer.Pointer.HotSpotX},{capturer.Pointer.HotSpotY}），"
                + $"位置 {capturer.Pointer.X},{capturer.Pointer.Y}"
                + $"（消费端结果：{capturer.LastAcquireOutcome}）"
                + (capturer.PointerNote is null ? string.Empty : $"；备注：{capturer.PointerNote}"));

            // DDA 的帧里**从来没有**鼠标指针，如实上报 false。
            surface.IsCursorIncluded.Should().BeFalse(
                "DDA 的桌面图不含指针（指针走 PointerPosition 那条线）；"
                + "报 true 会让上层以为已经画上去了，于是永远不会去补");

            capturer.TryReadBackBgra(out var bgra, out var w, out var h, out var readFailure)
                .Should().BeTrue($"读回失败：{readFailure}");

            var stats = PixelStats(bgra, w, h);

            _output.WriteLine($"      像素：非黑 {stats.NonZeroRatio:P2}，亮度 {stats.MeanLuma:F1}"
                + $"（{stats.MinLuma}..{stats.MaxLuma}），抽样色数 {stats.DistinctColors}");

            // 🔥 落一张**人眼能看**的图。数字能证明"不是全黑"，但证明不了"抓的是这块屏" ——
            //    画面斜了、颜色错了、抓的是别的显示器，都只有看一眼才知道。
            //    落点沿用 .tools/ 下按批次分目录的老规矩。
            DumpJpeg(bgra, w, h, surface.IsCursorIncluded, "dxgi-captured-frame.jpg");
        }

        _output.WriteLine(string.Empty);
        _output.WriteLine($"第一帧的结局：{firstOutcome}"
            + (firstOutcome == WindowsDxgiScreenCapturer.AcquireOutcome.NoDesktopUpdate
                ? "  ← 就是实测到的那条：全新 duplication 的第一帧没有桌面内容"
                : "  ← 这次第一帧就有内容（说明这一瞬间桌面刚更新过）"));

        frames.Should().BeGreaterThan(
            0,
            "连试了 12 次都没拿到一帧有内容的画面 —— 那就是抓屏坏了，不是「桌面静止」");
    }

    /// <summary>
    /// 🔥 <b>与 GDI 对拍</b>：同一块屏，两个后端抓出来的内容必须说得通。
    ///
    /// <para>
    /// 这条是"黑帧"最可靠的判据：GDI 那一路（M5.1 已验收）说"这块屏上有大量非黑像素"，
    /// 而 DXGI 那一路如果给出全黑，那<b>不可能是桌面本来就黑</b>，只能是 DXGI 抓错了。
    /// </para>
    /// <para>
    /// ⚠️ 判据只在 <b>GDI 明确有内容</b>时才成立（GDI 自己也全黑时直接跳过）——
    /// 否则这会变成一条"取决于当前桌面长什么样"的随机用例。
    /// 另外 DXGI 侧允许试几次取最好的一帧：两次抓屏之间桌面可能刚好在变。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Dxgi_content_agrees_with_gdi_on_the_same_screen()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sessionId = Process.GetCurrentProcess().SessionId;

        using var dxgi = new WindowsDxgiScreenCapturer(sessionId);
        var gdi = new WindowsGdiScreenCapturer(sessionId);

        if (SkipIfScreenCaptureIsBlocked(dxgi)) return;

        var target = dxgi.EnumerateScreens().FirstOrDefault(s => !s.IsVirtualDesktop);

        if (target is null) return;

        // ── GDI 侧（已知可用，当作基准）──

        CapturedFrame? reference;

        try
        {
            reference = await gdi.CaptureAsync(target.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"GDI 抓不到（前提不成立）：{ex.Message}");

            return;
        }

        var referenceStats = PixelStats(reference.Buffer, reference.Width, reference.Height);

        _output.WriteLine($"GDI ：{reference.Width}×{reference.Height} 非黑 {referenceStats.NonZeroRatio:P2} "
            + $"亮度 {referenceStats.MeanLuma:F1}");

        if (referenceStats.NonZeroRatio < 0.5)
        {
            _output.WriteLine("GDI 自己也没多少内容（桌面本来就黑？）—— 跳过对拍。");

            return;
        }

        // ── DXGI 侧：试几次，取内容最多的一帧 ──

        var best = 0.0;

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var surface = dxgi.TryCaptureSurface(target.Id, CancellationToken.None, out var failure);

            if (failure is not null)
            {
                _output.WriteLine($"DXGI 第 {attempt + 1} 次失败：{failure}");

                break;
            }

            if (surface is null) continue;

            if (!dxgi.TryReadBackBgra(out var bgra, out var w, out var h, out var readFailure))
            {
                _output.WriteLine($"DXGI 读回失败：{readFailure}");

                break;
            }

            var s = PixelStats(bgra, w, h);

            if (s.NonZeroRatio > best) best = s.NonZeroRatio;

            _output.WriteLine($"DXGI 第 {attempt + 1} 次：{w}×{h} 非黑 {s.NonZeroRatio:P2} "
                + $"亮度 {s.MeanLuma:F1}（{s.MinLuma}..{s.MaxLuma}）抽样色数 {s.DistinctColors}");
        }

        best.Should().BeGreaterThan(
            0,
            "GDI 在同一块屏上抓到了大量非黑像素，DXGI 却给全黑 —— "
            + "这不是「桌面本来就黑」，是 DXGI 这一侧抓错了（选错适配器节点 / 读回链路错）");
    }

    /// <summary>
    /// 🔥 抓到的图<b>尺寸必须与两套来源都对得上</b>，格式与旋转也要在预期内。
    ///
    /// <para>
    /// 这里刻意把<b>两套来源并排断言</b>（<c>EnumDisplayMonitors</c> 与 DDA 的 <c>DXGI_OUTDUPL_DESC</c>）：
    /// 只给一个数时，"抓错屏"与"枚举过期了"长得一模一样，而处置完全相反。
    /// </para>
    /// </summary>
    [Fact]
    public void Captured_size_matches_both_sources()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var capturer = new WindowsDxgiScreenCapturer(Process.GetCurrentProcess().SessionId);

        var target = capturer.EnumerateScreens().FirstOrDefault(s => !s.IsVirtualDesktop);

        if (target is null) return;

        CapturedSurface? surface = null;

        for (var attempt = 0; attempt < 8 && surface is null; attempt++)
        {
            surface = capturer.TryCaptureSurface(target.Id, CancellationToken.None, out var failure);

            if (failure is not null)
            {
                _output.WriteLine($"抓不到（前提不成立）：{failure}");

                return;
            }
        }

        if (surface is null)
        {
            _output.WriteLine("试了 8 次都没拿到有内容的帧 —— 跳过（可能桌面长时间静止）。");

            return;
        }

        var d = capturer.Diagnostics;

        surface.Width.Should().Be(target.Width, "抓的必须是我点名那块屏的尺寸");
        surface.Height.Should().Be(target.Height);

        d.ModeWidth.Should().Be((uint)surface.Width, "DDA 自己的描述（另一套来源）也必须一致");
        d.ModeHeight.Should().Be((uint)surface.Height);

        d.ModeFormat.Should().Be(DxgiInterop.FormatB8G8R8A8Unorm,
            "桌面格式不是 BGRA 时（例如开了 HDR 的 FP16），后续按 BGRA 解释会得到一堆诡异颜色");

        d.Rotation.Should().Be("identity", "旋转不为 identity 时 DDA 给的图与所见不符，本批不支持");

        _output.WriteLine($"尺寸 {surface.Width}×{surface.Height}，格式 {d.ModeFormat}，旋转 {d.Rotation}");
        _output.WriteLine($"脏区：{DescribeDirty(surface.DirtyRects)}");
        _output.WriteLine($"指针：{capturer.LastPointerPosition}（DDA 单独给的那份）");
        _output.WriteLine($"适配器：#{capturer.AdapterIndex} {capturer.AdapterName}");
    }

    /// <summary>
    /// ⚠️ <b>"全部显示器"这条路本批做不到，而且必须说清楚为什么</b>。
    ///
    /// <para>
    /// DDA 一次只能对<b>一个 output</b> 建 duplication，虚拟桌面是多块屏拼出来的。
    /// 如果这里安静地回落到主屏，用户会看到"选全部显示器却只出来半块画面"，
    /// 而尺寸还是错的 —— 那比报错难查得多。
    /// </para>
    /// </summary>
    [Fact]
    public void The_virtual_desktop_is_refused_with_a_reason()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var capturer = new WindowsDxgiScreenCapturer(Process.GetCurrentProcess().SessionId);

        var surface = capturer.TryCaptureSurface(
            WindowsDxgiScreenCapturer.VirtualDesktopId, CancellationToken.None, out var failure);

        surface.Should().BeNull();

        failure.Should().NotBeNullOrWhiteSpace("拒绝必须带原因，否则现场只会看到「没画面」");

        failure.Should().Contain("一块", "原因要说清是「一次只能抓一块显示器」这件事");

        _output.WriteLine($"拒绝理由：{failure}");
    }

    /// <summary>
    /// 🔥 <b>内存路径也不许吐一张没填过内容的图。</b>
    ///
    /// <para>
    /// <see cref="IScreenCapturer.CaptureAsync"/> 的返回类型<b>不可为空</b>，
    /// 而 DXGI 在桌面静止时给不出新帧 —— 这是个真实的两难。
    /// 本实现对它的处置是「没有新帧就交上一次那一帧」＋「从来没过就有界等一小段」，
    /// 而<b>唯一不能做的</b>是把那张内容未定义的纹理读回来交出去
    /// （那正是 M5.4-a 踩到的坑，见 §3.3 第 4 条）。
    /// </para>
    /// <para>
    /// 判据与对拍那条一样：只有 GDI 说"这块屏有内容"时才要求 DXGI 也有内容。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Memory_path_never_returns_an_unpopulated_frame()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sessionId = Process.GetCurrentProcess().SessionId;

        var gdi = new WindowsGdiScreenCapturer(sessionId);

        using var dxgi = new WindowsDxgiScreenCapturer(sessionId);

        if (SkipIfScreenCaptureIsBlocked(dxgi)) return;

        var target = dxgi.EnumerateScreens().FirstOrDefault(s => !s.IsVirtualDesktop);

        if (target is null) return;

        CapturedFrame reference;

        try
        {
            reference = await gdi.CaptureAsync(target.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"GDI 抓不到（前提不成立）：{ex.Message}");

            return;
        }

        var referenceStats = PixelStats(reference.Buffer, reference.Width, reference.Height);

        if (referenceStats.NonZeroRatio < 0.5)
        {
            _output.WriteLine("GDI 自己也没多少内容 —— 跳过。");

            return;
        }

        try
        {
            var frame = await dxgi.CaptureAsync(target.Id, CancellationToken.None);

            var stats = PixelStats(frame.Buffer, frame.Width, frame.Height);

            _output.WriteLine($"DXGI 内存路径：{frame.Width}×{frame.Height} 非黑 {stats.NonZeroRatio:P2} "
                + $"亮度 {stats.MeanLuma:F1} 捕获时刻 {frame.CapturedAt:HH:mm:ss.fff}");

            frame.Width.Should().Be(target.Width);
            frame.Stride.Should().Be(frame.Width * 4);

            stats.NonZeroRatio.Should().BeGreaterThan(
                0,
                "GDI 说这块屏有内容，DXGI 却给全 0 —— 那说明它把「DDA 还没填过内容的纹理」交出来了");

            frame.IsCursorIncluded.Should().BeFalse("DDA 的桌面图不含指针");
        }
        catch (InvalidOperationException ex)
        {
            // 桌面全程静止时"拿不到首帧"是可接受的结局 —— 但**必须带原因**。
            _output.WriteLine($"拿不到首帧（可接受，但必须说清原因）：{ex.Message}");

            ex.Message.Should().Contain("静止", "拿不到时要说清是「桌面静止」而不是含糊的失败");
        }
    }

    /// <summary>
    /// 🔥 <b>"指针到底有没有被写进编码器看到的那张图"</b> —— 第 5 步留下、
    /// 到第 8 步为止<b>一张真像素证据都没有</b>的那条链（`REF-M5` §5）。
    ///
    /// <para>
    /// ⚠️ <b>为什么现有夹具都回答不了这个问题</b>：
    /// <c>DxgiHardwareEncodeSmokeTests</c> 喂给编码器的是 <c>surface.TextureHandle</c>
    /// （<b>裸桌面纹理，不含指针</b>）；而本文件那条主用例 dump 的是
    /// <c>IsCursorIncluded=false</c> 的 DDA 原图 ⇒ <b>两边都看不见指针</b>。
    /// </para>
    ///
    /// <para>
    /// 🔥 判据不是"看着像不像鼠标"，而是<b>可数的</b>：合成前后，
    /// <b>指针矩形内</b>的像素必须<b>真的变了</b>、<b>矩形外</b>必须<b>一个都没变</b>。
    /// 前者证"合成器确实写了"，后者证"没有越界乱涂"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>覆盖到哪里、不覆盖到哪里</b>（不夸大）：它走的是<b>与生产同一条</b>
    /// <c>PointerCompositor.TryGetRegion</c> ＋ <c>PointerCompositor.Composite</c>，
    /// 用的是<b>真实桌面读回的 BGRA</b> 与<b>真实光标形状</b>（含三态）。
    /// 但它<b>不覆盖</b> GPU 那三步（<c>CopySubresourceRegion</c> / <c>UpdateSubresource</c> /
    /// VideoProcessor 叠加）—— 那三步返回 <c>void</c>、<b>错了不报</b>，
    /// 由 <c>DxgiHardwareEncodeSmokeTests</c> 的 <c>FramesWithCursor</c> / <c>CursorFailures</c> 自证。
    /// </para>
    /// </summary>
    [Fact]
    public void Pointer_is_composited_into_the_frame_with_real_pixels()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sessionId = Process.GetCurrentProcess().SessionId;

        using var capturer = new WindowsDxgiScreenCapturer(sessionId);

        var target = capturer.EnumerateScreens().FirstOrDefault(s => !s.IsVirtualDesktop);

        if (target is null)
        {
            _output.WriteLine("没有可抓的真实输出 —— 前提不成立，跳过。");

            return;
        }

        // 🔥 **先把光标挪到这块屏的中央，再抓。**
        //    不挪的后果上一轮实测过：指针恰好 `Visible=false` ⇒ 合成那条链**根本没执行**，
        //    于是"写完了"与"跑过了"分不清。放到一个确定的、屏内的位置，这一条才每次真走一遍。
        var hadCursor = GetCursorPos(out var original);

        var centerX = target.Left + (target.Width / 2);
        var centerY = target.Top + (target.Height / 2);

        SetCursorPos(centerX, centerY);

        // ⚠️ **必须回读一次**：`SetCursorPos` 会失败（返回 false），而失败时它什么都不说。
        //    不回读的话，"指针不可见"这条结论就分不清是"环境不给"还是"我们没挪动"。
        //
        //    🔴 **实测更狠：它返回 `true` 也未必真的移动了。** 本次运行 `SetCursorPos(1080,720)`
        //    返回 `True`，紧接着回读却是 `1692,1173`（＝原位置，一步没动）。
        //    根因未查明（有别的输入在持续驱动光标 / 调用被系统忽略），但**结论是硬的**：
        //    **回了 true 也不能当真 —— 一切以 `GetCursorPos` 的回读为准。**
        var moved = GetCursorPos(out var afterMove);

        _output.WriteLine($"光标：{(hadCursor ? $"原在 {original.X},{original.Y}" : "读不到原位置")}"
            + $" → SetCursorPos({centerX},{centerY}) 返回 {moved}"
            + $" → 回读 {afterMove.X},{afterMove.Y}"
            + $"（屏 {target.Name} {target.Width}×{target.Height}，原点 {target.Left},{target.Top}）");

        try
        {
            CapturedSurface? surface = null;

            byte[]? visibleBgra = null;
            PointerSnapshot? visiblePointer = null;
            var width = 0;
            var height = 0;

            // 🔴 **缺陷计数器**：DDA 说"指针不可见 (0,0)"，而**系统**说光标就在屏上别处。
            //    见方法头 —— 这一条是本用例真正要抓的东西。
            var invisibleWhileShown = 0;

            var samples = new List<string>();

            for (var attempt = 1; attempt <= 15; attempt++)
            {
                // 🔥 前 5 轮**轻轻抖一下**光标：DDA 只在"指针动了"时单独给一帧。
                //    不抖的话可能一轮"指针可见"的帧都抓不到，像素级那一半就永远跳过。
                //    第 6 轮起**停住不动** —— 让"内容变了但没有鼠标更新"的那种帧占多数，
                //    因为缺陷恰好只出现在那种帧上。
                if (attempt <= 5)
                {
                    SetCursorPos(centerX + (attempt % 2), centerY);
                }

                Thread.Sleep(80);

                // ⚠️ **每轮都要真的抓一帧**（不能写成 `surface ??= ...`：那样第一轮成功之后
                //    后面 14 轮全在复用同一帧，采样表会变成 15 行一模一样的假数据）。
                var captured = capturer.TryCaptureSurface(target.Id, CancellationToken.None, out var failure);

                surface ??= captured;

                if (failure is not null)
                {
                    _output.WriteLine($"抓不到（前提不成立）：{failure}");

                    return;
                }

                var outcome = capturer.LastAcquireOutcome;
                var dda = capturer.Pointer;

                // ⚠️ **系统的真话**：`GetCursorPos` 与 DDA 无关，是判"谁对"的基准。
                GetCursorPos(out var truth);

                var line = $"  {attempt,2}｜{outcome,-16}｜DDA "
                    + $"{(dda.Visible ? "可见  " : "不可见")} {dda.X,4},{dda.Y,-4}"
                    + $"｜系统 {truth.X,4},{truth.Y,-4}";

                if (!dda.Visible && dda.X == 0 && dda.Y == 0 && (truth.X != 0 || truth.Y != 0))
                {
                    invisibleWhileShown++;

                    line += "   ← 🔴 系统说光标在屏上，DDA 却说不可见 (0,0)";
                }

                samples.Add(line);

                // 留一帧"指针可见"的读回，用来做像素级合成验证。
                // ⚠️ 读的是**桌面副本**（与生产 `TryReadPointerRegion` 同一张纹理），
                //    而那份副本**从不含指针** ⇒ 用相邻帧的指针快照来合成是成立的。
                if (visibleBgra is null && dda.Visible && surface is not null
                    && capturer.TryReadBackBgra(out var buffer, out var w, out var h, out _))
                {
                    visibleBgra = buffer;
                    visiblePointer = dda;
                    width = w;
                    height = h;
                }
            }

            _output.WriteLine("采样（每行一帧；DDA 与系统并列，好判「谁对」）：");

            foreach (var line in samples) _output.WriteLine(line);

            _output.WriteLine(string.Empty);

            if (surface is null)
            {
                _output.WriteLine("试了 15 次都没拿到一帧有内容的画面 —— 跳过（桌面长时间静止）。");

                return;
            }

            surface.Width.Should().Be(target.Width);
            surface.Height.Should().Be(target.Height);

            // ═══════════════════════════════════════════════════════
            // 🔴 判据一（本用例的重点）：指针位置与可见性**不许被内容帧清掉**
            // ═══════════════════════════════════════════════════════
            invisibleWhileShown.Should().Be(
                0,
                "`WindowsDxgiScreenCapturer.Pointer` 在「内容变了、但这一帧没有鼠标更新」时，"
                + "把位置与可见性**无条件**取自 `DXGI_OUTDUPL_FRAME_INFO.PointerPosition`；"
                + "而 DDA 的规矩是 `LastMouseUpdateTime == 0` 时那个字段**不可读**"
                + "（本机实测回 `Visible=0, X=0, Y=0`）。⇒ 后果是：用户不动鼠标而屏幕在变"
                + "（播视频 / 动效 / 别人在操作）时，指针被判成不可见、"
                + "`WindowsGpuFrameEncoder.TryCompositePointer` 提前返回 ⇒ "
                + "**观看端画面里的鼠标消失**，而且不报任何错、日志里什么都看不到。"
                + "同一份知识本来就写在 `LastPointerPosition` 的注释里，只是 `TryReadPointer` 没照做。");

            // ═══════════════════════════════════════════════════════
            // 判据二：指针**真的被写进**那一块像素（第 5 步欠的账）
            // ═══════════════════════════════════════════════════════
            if (visibleBgra is null || visiblePointer is null)
            {
                _output.WriteLine("这 15 轮里没有一轮是「指针可见」的帧 —— 像素级合成这一半跳过（前提不成立）。");

                return;
            }

            var pointer = visiblePointer;

            _output.WriteLine($"用于合成的那一帧：形状 {pointer.Type} "
                + $"{pointer.Width}×{pointer.ActualHeight}（Pitch {pointer.Pitch}，"
                + $"热点 {pointer.HotSpotX},{pointer.HotSpotY}），位置 {pointer.X},{pointer.Y}");

            if (!PointerCompositor.TryGetRegion(pointer, width, height, out var region, out var why))
            {
                // ⚠️ **不 FAIL**：与生产 `TryCompositePointer` 同口径 ——
                //    "指针不可见 / 整个在画面外 / 形状还没拿到"都是**正常情形**，不是缺陷。
                //    但必须把原因写出来，否则这里会变成一条"永远静默跳过、还显得全绿"的用例。
                _output.WriteLine($"这一帧不需要合成（正常情形，不是失败）：{why}");

                return;
            }

            var bgra = visibleBgra;

            // 🔥 **必须先裁出"只含矩形的那一小块"** ——
            //    `PointerCompositor.Composite` 的入参语义是"**你给我的缓冲就是那块矩形**"
            //    （`backgroundPitch == regionWidth * 4`），**不是**"整屏缓冲 ＋ 一个矩形"。
            //    生产那条路同样如此：它用 GPU `CopySubresourceRegion` 把子矩形拷进 staging 再读回
            //    （`WindowsGpuFrameEncoder.TryReadPointerRegion`，`:1109`）。
            //
            //    ⚠️ 这条用例的第一版**正是栽在这里**：把整屏 BGRA 配 `region.Width×Height`
            //    传了进去 ⇒ `baseX/baseY` 被算成"缓冲左上角"的坐标（0,1）⇒ 指针被画到
            //    **屏幕左上角**去。症状是「矩形内 **0** 个像素变了、矩形外变了 **144** 个」——
            //    看起来像"合成整个坏了"，其实只是搬错缓冲。**先怀疑自己的入参**。
            var rectPitch = region.Width * 4;
            var rect = new byte[rectPitch * region.Height];

            for (var y = 0; y < region.Height; y++)
            {
                Buffer.BlockCopy(
                    bgra,
                    ((((region.Top + y) * width) + region.Left) * 4),
                    rect,
                    y * rectPitch,
                    rectPitch);
            }

            // ⚠️ `before` 取的是**整屏**快照（不是 rect 的），这样"矩形外有没有被改到"
            //    才能真的验出来 —— 只对 rect 自己比对的话，那个断言就恒真、等于没写。
            var before = (byte[])bgra.Clone();

            // 🔥 先落一张**合成之前**的图：它与下面那张是**同一帧**（`before` 的克隆）⇒
            //    两张并排就是"指针画上去了没有"的**同帧对照**。
            //    ⚠️ 别拿 `.tools/m5.4-capture/dxgi-captured-frame.jpg` 当这一张 ——
            //    那是**另一个用例在别的时间**抓的（`IsCursorIncluded=false` 的 DDA 原图），
            //    两次之间屏幕内容早变了，并排看会把"屏幕变了"读成"指针画上了"。
            DumpJpeg(before, width, height, cursorIncluded: false, "dxgi-pointer-before.jpg");

            PointerCompositor.Composite(
                pointer, region.Left, region.Top, rect, rectPitch, region.Width, region.Height);

            // 贴回整屏，供下面"内外各变了几个像素"与落盘用。
            for (var y = 0; y < region.Height; y++)
            {
                Buffer.BlockCopy(
                    rect,
                    y * rectPitch,
                    bgra,
                    ((((region.Top + y) * width) + region.Left) * 4),
                    rectPitch);
            }

            var (insideChanged, insideTotal) = CountChanged(before, bgra, width, height, region, inside: true);
            var (outsideChanged, outsideTotal) = CountChanged(before, bgra, width, height, region, inside: false);

            _output.WriteLine($"合成矩形：{region.Left},{region.Top} {region.Width}×{region.Height}"
                + "（向外扩到偶数 —— NV12 的色度是 2×2）");
            _output.WriteLine($"矩形内：{insideChanged} / {insideTotal} 个像素变了；"
                + $"矩形外：{outsideChanged} / {outsideTotal} 个像素变了");

            insideChanged.Should().BeGreaterThan(
                0,
                "`PointerCompositor` 认为这一块该画指针，但像素一个都没变 —— 那是 `MASKED_COLOR` 的"
                + " XOR 或 `MONOCHROME` 的 AND/XOR 分支什么都没干，而现场看到的就是「鼠标不在」");

            outsideChanged.Should().Be(0, "只许改指针矩形那一块；越界就是坐标算错了");

            DumpJpeg(bgra, width, height, cursorIncluded: true, "dxgi-pointer-composited.jpg");
        }
        finally
        {
            if (hadCursor) SetCursorPos(original.X, original.Y);
        }
    }

    /// <summary>
    /// 数一数<b>指针矩形内 / 外</b>各有多少像素在合成前后变了。
    ///
    /// <para>
    /// 🔥 刻意拆成两个数：只报一个"变了 1234 个"是判断不了"变对地方了没有"的 ——
    /// 而"改了矩形外的像素"正是坐标算错时最典型的样子（不会报错，图看着只是"有点花"）。
    /// </para>
    /// </summary>
    private static (int Changed, int Total) CountChanged(
        byte[] before, byte[] after, int width, int height, DxgiInterop.Rect region, bool inside)
    {
        var changed = 0;
        var total = 0;

        for (var y = 0; y < height; y++)
        {
            var yInRegion = y >= region.Top && y < region.Top + region.Height;

            for (var x = 0; x < width; x++)
            {
                var inRegion = yInRegion && x >= region.Left && x < region.Left + region.Width;

                if (inRegion != inside) continue;

                total++;

                var i = ((y * width) + x) * 4;

                if (i + 3 >= after.Length) continue;

                if (before[i] != after[i] || before[i + 1] != after[i + 1]
                    || before[i + 2] != after[i + 2] || before[i + 3] != after[i + 3])
                {
                    changed++;
                }
            }
        }

        return (changed, total);
    }

    /// <summary>
    /// 🔥 把光标挪到指定位置（屏幕坐标）。
    ///
    /// <para>
    /// ⚠️ 这里<b>故意直接 P/Invoke</b>、不走 Agent 的输入注入：本文件是<b>夹具</b>，
    /// 要的是"确定地把光标放到某处"，而不是"验证注入链路"（那是 M5.2 的活，
    /// 有它自己的用例）。混用会让这条像素用例的失败原因变得说不清。
    /// </para>
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    /// <summary>读光标当前位置（用完要放回去 —— 夹具不该改使用者的桌面状态）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out CursorPoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;

        public int Y;
    }

    private static string DescribeDirty(IReadOnlyList<DirtyRect>? rects) => rects is null
        ? "null（本帧没有脏区元数据）"
        : rects.Count == 0
            ? "空表（按约定 = 整屏都可能变了）"
            : $"{rects.Count} 块，首块 {rects[0]}";

    /// <summary>
    /// 把一帧 BGRA 编成 JPEG 落到 <c>.tools/m5.4-capture/</c>（给人眼看）。
    ///
    /// <para>
    /// ⚠️ <paramref name="fileName"/> 是**必需的参数**而不是写死的常量：同一次跑里
    /// 现在要落**两张**不同的图（DDA 原图 vs 合成指针之后的图），写死会把前一张覆盖掉 ——
    /// 而那正是"看起来只落了一张、其实另一张被吃了"的经典方式。
    /// </para>
    /// </summary>
    private void DumpJpeg(byte[] bgra, int width, int height, bool cursorIncluded, string fileName)
    {
        try
        {
            var dir = Path.Combine(RepoRoot(), ".tools", "m5.4-capture");

            Directory.CreateDirectory(dir);

            using var encoder = new GdiJpegFrameEncoder(maxLongEdge: 0, quality: 80, maxFrameBytes: 0);

            var frame = new CapturedFrame(
                Width: width,
                Height: height,
                Stride: width * 4,
                Buffer: bgra,
                CapturedAt: DateTimeOffset.UtcNow,
                IsCursorIncluded: cursorIncluded);

            if (!encoder.TryEncode(frame, forceKeyframe: true, out var encoded) || encoded is null)
            {
                _output.WriteLine($"      JPEG 编码失败：{encoder.LastFailure}");

                return;
            }

            var path = Path.Combine(dir, fileName);

            File.WriteAllBytes(path, encoded.Data);

            _output.WriteLine($"      已存证据图：{path}（{encoded.Data.Length / 1024} KB）");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"      落图失败（不影响断言）：{ex.Message}");
        }
    }

    /// <summary>从测试运行目录往上找到仓库根（有 <c>src</c> 与 <c>web</c> 的那一层）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private readonly record struct Stats(
        double NonZeroRatio, double MeanLuma, int MinLuma, int MaxLuma, int DistinctColors);

    /// <summary>抽查像素分布（每 7 像素取一个）。</summary>
    private static Stats PixelStats(byte[] bgra, int width, int height)
    {
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
}
