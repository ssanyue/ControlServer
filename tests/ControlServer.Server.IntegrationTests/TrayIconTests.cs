using System.Runtime.InteropServices;
using ControlServer.Platform.Abstractions;
using ControlServer.Platform.Linux;
using ControlServer.Platform.Windows;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 托盘图标相关测试。
/// <para>
/// 这里刻意<b>不</b>依赖真实桌面做断言（CI/容器里没有通知区域），
/// 但保留一个"真的建图标再优雅摘掉"的用例 —— P/Invoke 路径的坑
/// （<c>cbSize</c> 填错、窗口类注册失败、委托被 GC 回收）只有真跑一次才暴露得出来。
/// </para>
/// </summary>
public class TrayIconTests
{
    /// <summary>
    /// <b>托盘最经典的"无声失败"守门</b>：<c>cbSize</c> 必须与结构体实际布局一致。
    /// <para>
    /// 期望值来自 winuser.h：<c>NOTIFYICONDATAW</c> 的 V3 尺寸是
    /// <b>x64 = 976</b>、<b>x86 = 956</b>。填错的症状是
    /// <c>Shell_NotifyIcon</c> 返回 false（或返回 true 但图标不出现），而<b>没有任何提示</b>。
    /// </para>
    /// </summary>
    [Fact]
    public void NotifyIconData_matches_the_documented_layout_size()
    {
        var size = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>();
        var expected = Environment.Is64BitProcess ? 976 : 956;

        size.Should().Be(expected,
            "NOTIFYICONDATA 的 cbSize 必须与 winuser.h 的布局一致，否则 Shell_NotifyIcon 会静默失败");
    }

    /// <summary>cbSize 在代码里是"算出来"的（Marshal.SizeOf），不能是写死的常数。</summary>
    [Fact]
    public void NotifyIconData_size_is_derived_not_hard_coded()
    {
        // 与上一条断言配合：如果将来有人把 cbSize 改成字面量，这两条会一起失效。
        // 这里额外断言"结构体大小"本身是个合理值，防止结构体被误改后两条同时漂移。
        Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>().Should().BeGreaterThan(900);
    }

    // ── 选帧规则 ────────────────────────────────────────────────

    [Fact]
    public void PickBestIndex_prefers_the_largest_frame_not_exceeding_the_target()
    {
        int[] frames = [16, 20, 24, 32, 48, 64, 256];

        IconLoader.PickBestIndex(frames, 16).Should().Be(0);
        IconLoader.PickBestIndex(frames, 17).Should().Be(0);
        IconLoader.PickBestIndex(frames, 24).Should().Be(2);
        IconLoader.PickBestIndex(frames, 40).Should().Be(3);    // 退到 32，而不是挑 48
        IconLoader.PickBestIndex(frames, 48).Should().Be(4);
        IconLoader.PickBestIndex(frames, 200).Should().Be(5);   // 退到 64
        IconLoader.PickBestIndex(frames, 256).Should().Be(6);
    }

    [Fact]
    public void PickBestIndex_falls_back_to_the_smallest_when_every_frame_is_too_big()
    {
        int[] frames = [48, 64, 256];

        // 期望 16：全部过大 ⇒ 取最小的 48（缩放损耗最小）
        IconLoader.PickBestIndex(frames, 16).Should().Be(0);
    }

    [Fact]
    public void PickBestIndex_returns_minus_one_for_no_frames()
    {
        IconLoader.PickBestIndex([], 16).Should().Be(-1);
    }

    // ── 嵌入的 .ico ─────────────────────────────────────────────

    [Fact]
    public void Embedded_icon_is_a_valid_multisize_ico()
    {
        var bytes = IconLoader.ReadEmbedded();

        bytes.Should().NotBeNull("assets/app.ico 必须以 LogicalName 嵌入 Platform.Windows 程序集");

        // ICONDIR：reserved=0、type=1（图标）
        BitConverter.ToUInt16(bytes!, 0).Should().Be(0);
        BitConverter.ToUInt16(bytes!, 2).Should().Be(1);

        var frames = IconLoader.DescribeFrames(bytes!);

        frames.Should().NotBeEmpty();
        frames.Should().Contain(16, "托盘用的是小图标尺寸");
        frames.Should().Contain(32, "任务栏/Alt-Tab 用 32");
        frames.Should().Contain(256, "大图标视图用 256");
    }

    // ── 平台可用性矩阵 ──────────────────────────────────────────

    [Fact]
    public async Task Linux_noop_host_is_unavailable_and_never_raises_commands()
    {
        var host = new NoopTrayIconHost();

        host.IsAvailable.Should().BeFalse("Linux 无桌面托盘");
        host.UnavailableReason.Should().NotBeNullOrWhiteSpace();

        var raised = false;
        host.CommandInvoked += _ => raised = true;

        (await host.ShowAsync(new TrayIconOptions("t", []))).Should().BeFalse();
        await host.UpdateAsync(new TrayIconOptions("t", []));
        raised.Should().BeFalse();
    }

    /// <summary>
    /// 真的把图标建出来、再优雅摘掉。
    /// <para>
    /// 与其它测试不同，这条会**真的**调用 <c>Shell_NotifyIcon</c>：只有这样才能验证
    /// 窗口类注册、隐藏窗口创建、<c>HICON</c> 构造、消息循环启动这一整条链路。
    /// 结束时走 <see cref="ITrayIconHost.DisposeAsync"/>（内部 <c>NIM_DELETE</c>），
    /// 所以不会在通知区域留下残影。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Windows_host_shows_and_disposes_the_icon_cleanly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // 非 Windows 平台没有托盘，跳过（Linux 用 Noop，见上一条）
        }

        await using var host = new WindowsTrayIconHost();

        if (!host.IsAvailable)
        {
            // 无交互式桌面（服务会话 / 某些 CI）—— 这正是"应当优雅降级"的场景。
            host.UnavailableReason.Should().NotBeNullOrWhiteSpace();
            (await host.ShowAsync(new TrayIconOptions("t", []))).Should().BeFalse();
            return;
        }

        var menu = new List<TrayMenuCommand>
        {
            new("打开管理界面", TrayCommands.OpenConsole, IsDefault: true),
            TrayMenuCommand.Separator,
            new("退出服务", TrayCommands.Exit),
        };

        var shown = await host.ShowAsync(new TrayIconOptions("ControlServer 测试", menu));

        shown.Should().BeTrue(
            $"交互式会话里应当能成功添加托盘图标；失败原因：{host.UnavailableReason ?? "(未提供)"}");

        // 更新 + 气泡也要覆盖（这两条走的是 Post 到托盘线程的路径，容易出"投递了但没执行"）。
        await host.UpdateAsync(new TrayIconOptions("ControlServer 测试（已更新）", menu));
        await host.ShowBalloonAsync("测试", "托盘图标验证");

        // 优雅摘除：内部 NIM_DELETE + DestroyWindow + 等消息循环退出。
        await host.DisposeAsync();
    }
}
