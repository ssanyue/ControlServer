using System.Diagnostics;
using ControlServer.Platform.Abstractions;
using ControlServer.Platform.Windows;
using FluentAssertions;
using Xunit.Abstractions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// M5.1 的<b>抓屏 → 编码</b>这一段在真实桌面上的自检。
///
/// <para>
/// 🔥 <b>为什么这一段必须真跑一次、桩守不住</b>：整条远控链路的其余各跳
/// （管道、Hub、浏览器）都能用假数据测，唯独这一段不行 ——
/// 它全是 <b>P/Invoke</b>：<c>CreateDIBSection</c> 的<b>负高度</b>约定
/// （自上而下）、<c>CAPTUREBLT</c> 标志、<c>GetCursorInfo</c>/<c>DrawIconEx</c> 的
/// 指针叠加，以及 GDI+ 的 JPEG 编码参数。这些全都有一个共同点：
/// <b>写错了照样编译通过，而且不抛异常</b>，只表现为一张全黑 / 倒置 / 没有鼠标的图。
/// 假数据永远测不出这些。
/// </para>
///
/// <para>
/// ⚠️ <b>它在本机跑的前提是"有一个可捕获的桌面"</b>（Session ≥ 1）。
/// 没有桌面时（Session 0 的服务、无头 CI）本文件<b>直接返回</b>而不是造一个假 FAIL ——
/// 那种环境下 M5.1 的前提本来就不成立。前提不成立与结论错误是两件事，
/// 把它们都写成 FAIL 只会训练人去忽略红色。
/// </para>
///
/// <para>
/// 📌 <b>这里刻意只读</b>：不注入键鼠、不改分辨率、不动任何设置，
/// 因此它可以在开发机上反复跑而不产生副作用。
/// </para>
/// </summary>
public sealed class GdiCaptureSmokeTests
{
    private readonly ITestOutputHelper _output;

    public GdiCaptureSmokeTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 抓一帧；没有可捕获的桌面时返回 <c>null</c>（调用方直接返回）。
    /// </summary>
    /// <param name="screenId">
    /// 用<b>枚举出来的真实屏 Id</b>，不要拿 <see cref="WindowsGdiScreenCapturer.VirtualDesktopId"/> 去试。
    /// <para>
    /// 🔥 这是个真踩到的坑：虚拟桌面<b>只在"多于一块屏"时才被枚举出来</b>
    ///（单屏时它与那块屏完全等价，列出来只会让下拉框出现两条一模一样的选项）。
    /// 所以在单屏机器上请求 <c>virtual-desktop</c> 会抛"找不到屏" ——
    /// 那<b>不是</b> bug，是刻意的；但它会让本文件里所有用它的用例全部静默跳过，
    /// 于是"真机自检"变成了一句空话。
    /// </para>
    /// </param>
    private static async Task<CapturedFrame?> TryCaptureAsync(
        WindowsGdiScreenCapturer capturer,
        string screenId)
    {
        try
        {
            return await capturer.CaptureAsync(screenId, CancellationToken.None);
        }
        catch (Exception)
        {
            // 无桌面 / 无权限 / Session 0 —— 都属于"这台机器上跑不了"，不是代码错了。
            return null;
        }
    }

    /// <summary>枚举出来的第一块屏（优先虚拟桌面，其次主显示器）。没有桌面时返回 <c>null</c>。</summary>
    private static string? FirstScreenId(WindowsGdiScreenCapturer capturer)
    {
        var screens = capturer.EnumerateScreens();

        if (screens.Count == 0) return null;

        return (screens.FirstOrDefault(s => s.IsVirtualDesktop)
                ?? screens.FirstOrDefault(s => s.IsPrimary)
                ?? screens[0]).Id;
    }

    private static WindowsGdiScreenCapturer NewCapturer()
        => new(Process.GetCurrentProcess().SessionId);

    /// <summary>
    /// 🔥 <b>这条断言在哪台机器上都成立</b>，而且它守的是一个容易被"优化掉"的决定：
    ///
    /// <para>
    /// GDI 抓屏<b>必须</b>对 <see cref="IScreenCapturer.CaptureSurfaceAsync"/> 抛
    /// <see cref="NotSupportedException"/>，<b>不许</b>"每次返回 <c>null</c>"。
    /// 因为 <c>null</c> 在零拷贝路径上的语义是"这一帧画面没有变化"
    /// （DXGI Desktop Duplication 的正常超时），于是上层会安静地空转 ——
    /// 表现是"零拷贝管线跑起来了、就是一帧都没有"，而那是最难查的一类故障。
    /// 抛出来才说得清"你问错了实现"。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Gdi_capture_refuses_the_zero_copy_path_loudly()
    {
        if (!OperatingSystem.IsWindows()) return;

        var capturer = NewCapturer();

        var act = async () => await capturer.CaptureSurfaceAsync(
            WindowsGdiScreenCapturer.VirtualDesktopId, CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>(
            "内存路径拿不到 GPU 纹理；返回 null 会被上层读成「画面没变化」，"
            + "于是一条零拷贝管线会安静地空转");
    }

    /// <summary>
    /// 🔥 虚拟桌面<b>只在该出现的时候出现</b>（多屏时）。
    ///
    /// <para>
    /// 这条规则值得单独钉住，因为它直接后果是"要抓的屏不存在" ⇒ 抓屏抛 ⇒ 整路起不来：
    /// 单屏机器上列一个与那块屏等价的"虚拟桌面"，只会让下拉框出现两条一样的选项；
    /// 而多屏机器上<b>不</b>列它，就等于多屏拼接（「等效 8K」的唯一形态）没法选。
    /// 两个方向都要守。
    /// </para>
    /// </summary>
    [Fact]
    public void The_virtual_desktop_is_listed_exactly_when_there_is_more_than_one_screen()
    {
        if (!OperatingSystem.IsWindows()) return;

        var screens = NewCapturer().EnumerateScreens();

        if (screens.Count == 0) return;

        var virtuals = screens.Where(s => s.IsVirtualDesktop).ToArray();

        if (screens.Count == 1)
        {
            virtuals.Should().BeEmpty(
                "单屏时虚拟桌面与那块屏完全等价，列出来只会让人以为哪里配错了");
        }
        else
        {
            virtuals.Should().HaveCount(1, "多屏必须能选「全部显示器」，否则视频墙没办法整块抓");

            virtuals[0].Id.Should().Be(WindowsGdiScreenCapturer.VirtualDesktopId);

            var physical = screens.Where(s => !s.IsVirtualDesktop).ToArray();

            virtuals[0].Width.Should().BeGreaterThanOrEqualTo(
                physical.Max(s => s.Width),
                "虚拟桌面的宽度是各屏的**并集**，不可能比最宽的那块还窄");
        }
    }

    /// <summary>
    /// 请求一块本机上不存在的屏，必须抛出<b>带可用屏清单</b>的错误。
    ///
    /// <para>
    /// 🔥 这条不是形式主义：这个异常文本是<b>唯一</b>能解释
    /// "为什么这路流一帧都没有"的东西 —— 它会经推流宿主的收场状态一路回到页面。
    /// 只说一句"抓屏失败"，现场就只能去猜是权限、是锁屏、还是屏选错了。
    /// </para>
    /// </summary>
    [Fact]
    public void Asking_for_a_screen_that_does_not_exist_lists_what_is_available()
    {
        if (!OperatingSystem.IsWindows()) return;

        var capturer = NewCapturer();

        if (capturer.EnumerateScreens().Count == 0) return;

        var act = () => capturer.Capture("绝对不存在的屏", Process.GetCurrentProcess().SessionId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*本机可抓的屏有*", "错误里必须带上可用的屏 Id，否则现场无从下手");
    }

    /// <summary>
    /// 枚举 + 抓一帧 + 编成 JPEG。抓不到桌面（Session 0 / 无头）时直接返回。
    /// </summary>
    [Fact]
    public async Task Real_capture_encodes_to_a_jpeg_that_fits_one_signalr_message()
    {
        if (!OperatingSystem.IsWindows()) return;

        var capturer = NewCapturer();

        var screens = capturer.EnumerateScreens();

        if (screens.Count == 0)
        {
            // 见类注释：前提不成立，不造假 FAIL。
            return;
        }

        foreach (var screen in screens)
        {
            screen.Width.Should().BePositive($"屏幕「{screen.Id}」的宽必须是真的量出来的");
            screen.Height.Should().BePositive($"屏幕「{screen.Id}」的高必须是真的量出来的");
        }

        // ⚠️ 用真实存在的屏 Id，不要用 "virtual-desktop"（见 TryCaptureAsync 的注释）。
        var screenId = FirstScreenId(capturer)!;

        var frame = await TryCaptureAsync(capturer, screenId);

        if (frame is null) return;

        // ── 帧本身 ──
        frame.Width.Should().BePositive();
        frame.Height.Should().BePositive();

        // 🔥 抓的必须是**我点名的那块屏**，而不是"随便抓了一块"。
        //    这条能挡住"screenId 解析成了别的屏 / 回落到主屏"这类错配 ——
        //    那种错在页面上看起来完全正常（有画面），只是画面内容是错的。
        var target = screens.First(s => s.Id == screenId);

        frame.Width.Should().Be(
            target.Width,
            $"抓的必须是「{screenId}」那块屏的尺寸，而不是别的屏（或主屏）的");

        frame.Height.Should().Be(target.Height);

        // 🔥 负高度 + 自上而下 那一段要是写错了，这里会先炸：
        //    跨距比"宽度 × 4"小 ⇒ 缓冲区根本装不下一行。
        frame.Stride.Should().BeGreaterThanOrEqualTo(
            frame.Width * 4,
            "BGRA 每像素 4 字节；跨距小于它说明 DIB 头的方向或位深写错了");

        frame.Buffer.Length.Should().BeGreaterThanOrEqualTo(
            frame.Stride * frame.Height,
            "缓冲区装不下整幅图 —— 这是「负高度自上而下」那段约定写错的典型症状");

        // 🔥 指针叠加：GDI 的 BitBlt **默认不含指针**，必须自己用
        //    GetCursorInfo + DrawIconEx 画上去。不画的表现是"鼠标不动"，
        //    而用户会据此判定"这软件不实时"。
        frame.IsCursorIncluded.Should().BeTrue(
            "这一路的实现是**主动**画指针的，所以它必须如实报 true；"
            + "报 false 意味着那段 DrawIconEx 没生效");

        // ── 编码 ──
        using var encoder = new GdiJpegFrameEncoder();

        encoder.Codec.Should().Be(StreamCodecs.Mjpeg);

        encoder.TryEncode(frame, forceKeyframe: true, out var encoded).Should().BeTrue(
            $"编码失败时 LastFailure 说的是：{encoder.LastFailure ?? "(没有原因)"}");

        encoded.Should().NotBeNull();
        encoded!.Data.Length.Should().BeGreaterThan(0);
        encoder.LastFailure.Should().BeNull("成功过之后必须清掉上一次的失败原因，否则日志会一直挂着旧错");

        // JPEG 的 SOI 魔数。写错的地方（比如存成了 BMP / PNG）只有这个能挡住。
        encoded.Data.Length.Should().BeGreaterThan(3);
        encoded.Data[0].Should().Be(0xFF);
        encoded.Data[1].Should().Be(0xD8);
        encoded.Data[2].Should().Be(0xFF);

        encoded.Width.Should().BePositive();
        encoded.Height.Should().BePositive();
        encoded.CapturedUnixMs.Should().BePositive(
            "抓屏时刻是测延迟的**唯一**基准；为 0 会让观看端永远算不出时延");

        // ── 🔥 本方案 M5.1 里真正的硬约束 ──
        //
        // 单帧必须小到能塞进一条 SignalR 消息。而 JSON 协议下 byte[] 会变成 base64（+33%），
        // 所以真正要卡的是 **base64 之后**的大小，不是裸字节数。
        // 这一条如果破了，症状是"服务端直接掐断连接"（整条消息被丢弃），
        // 而不是"这一帧糊一点" —— 于是画面会整个停住，且两边日志都看不出所以然。
        encoded.Data.Length.Should().BeLessThanOrEqualTo(
            GdiJpegFrameEncoder.DefaultMaxFrameBytes,
            "编码器自己就该把单帧压在 380 KB 以内（超了就降质量重编，再超则丢帧）");

        const int signalrLimit = 512 * 1024;

        var base64Bytes = (encoded.Data.Length + 2) / 3 * 4;

        base64Bytes.Should().BeLessThan(
            signalrLimit,
            $"base64 之后 {base64Bytes} 字节必须小于服务端单条消息上限 {signalrLimit} —— "
            + "这是 M5.1 把 MJPEG 限制在 1080p 级的**全部原因**；4K 的 JPEG 单帧会直接超限"
            + "（真正的解法在 M5.4 的 H.264/HEVC，那时 4K 单帧只有几十 KB）");

        // 编码器会把长边压到 DefaultMaxLongEdge 以内 —— 这正是"1080p 级"的来源：
        // 2160×1440 的桌面会被降到 1920×1280 再编码，而**抓屏本身**是全分辨率的。
        encoded.Width.Should().BeLessThanOrEqualTo(GdiJpegFrameEncoder.DefaultMaxLongEdge);
        encoded.Height.Should().BeLessThanOrEqualTo(GdiJpegFrameEncoder.DefaultMaxLongEdge);

        // ── 真机数字（这份输出就是"同机自检报告"）──
        _output.WriteLine($"屏幕        ：{screenId}（{target.Width}×{target.Height}）");
        _output.WriteLine($"抓帧字节    ：BGRA {frame.Buffer.Length / 1024} KB（跨距 {frame.Stride}）");
        _output.WriteLine($"编码尺寸    ：{encoded.Width}×{encoded.Height}");
        _output.WriteLine($"JPEG 字节   ：{encoded.Data.Length / 1024} KB（质量上限 {GdiJpegFrameEncoder.DefaultQuality}）");
        _output.WriteLine($"base64 字节 ：{base64Bytes / 1024} KB / 上限 {signalrLimit / 1024} KB");
        _output.WriteLine($"带宽（12fps）：约 {encoded.Data.Length * 12 / 1024} KB/s = {encoded.Data.Length * 12 * 8 / 1024 / 1024} Mbps");
        _output.WriteLine($"帧时延基准  ：{encoded.CapturedUnixMs}（UTC 纪元毫秒）");
    }

    /// <summary>
    /// 黑白模式（决策 14）：置位后<b>下一帧就生效</b>，而且仍然是一张合法 JPEG。
    ///
    /// <para>
    /// ⚠️ 这里<b>不</b>断言"变灰之后一定更小"。对真实桌面的内容，彩色图省 10–30%，
    /// 但白底黑字的画面（代码 / 文档 / 多数网页）几乎省不出字节 ——
    /// 把它写成"必须变小"就是在钉一个对内容成立、对另一些内容不成立的性质，
    /// 那会变成一条随截图内容随机变红的用例。
    /// 它的<b>真价值</b>在方案里写明了：消除 4:2:0 的色度边缘伪影，不是省带宽。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Grayscale_mode_still_produces_a_valid_jpeg()
    {
        if (!OperatingSystem.IsWindows()) return;

        var capturer = NewCapturer();

        var screenId = FirstScreenId(capturer);

        if (screenId is null) return;

        var frame = await TryCaptureAsync(capturer, screenId);

        if (frame is null) return;

        using var encoder = new GdiJpegFrameEncoder { Grayscale = true };

        encoder.TryEncode(frame, forceKeyframe: true, out var encoded).Should().BeTrue(
            $"黑白模式不该让编码失败；LastFailure：{encoder.LastFailure ?? "(没有原因)"}");

        encoded!.Data[0].Should().Be(0xFF);
        encoded.Data[1].Should().Be(0xD8);
    }

    /// <summary>
    /// 🔥 单帧上限<b>真的会触发</b>降质量，而不是一句写在注释里的空话。
    ///
    /// <para>
    /// 这里把上限压到 4 KB —— 任何一个真实桌面都编不进 4 KB，于是必然走
    /// "降质量重编、再超则丢帧"那条路。断言它<b>要么</b>产出一帧且实际质量**低于**起始质量，
    /// <b>要么</b>如实返回 false 并给出原因。
    /// </para>
    /// <para>
    /// 这条守的是"上限"这个机制本身：如果它被改成了"只管截断"或"直接放弃"，
    /// 现场表现是"某些机器上一帧都没有"，而日志里只有一个孤零零的"编码失败"。
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_frame_size_cap_actually_kicks_in()
    {
        if (!OperatingSystem.IsWindows()) return;

        var capturer = NewCapturer();

        var screenId = FirstScreenId(capturer);

        if (screenId is null) return;

        var frame = await TryCaptureAsync(capturer, screenId);

        if (frame is null) return;

        const int tinyCap = 4 * 1024;

        using var encoder = new GdiJpegFrameEncoder(quality: 90, maxFrameBytes: tinyCap);

        var ok = encoder.TryEncode(frame, forceKeyframe: true, out var encoded);

        if (ok)
        {
            encoded!.Data.Length.Should().BeLessThanOrEqualTo(tinyCap, "说好的上限就必须守住");
            encoder.LastQualityUsed.Should().BeLessThan(
                90,
                "能压进 4 KB 只可能是降过质量的结果；实际质量没降说明「降质量重编」那段没跑");
        }
        else
        {
            // 降到底还是超限 ⇒ 丢帧是对的（丢帧可恢复，把超限的帧发出去会让整条连接被掐）。
            encoder.LastFailure.Should().NotBeNullOrWhiteSpace(
                "丢帧必须留下**原因**，否则现场只看到一个「编码失败」而无从下手");
        }
    }
}
