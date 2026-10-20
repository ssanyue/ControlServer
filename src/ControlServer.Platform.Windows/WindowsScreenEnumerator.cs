using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// <b>"这台机器上现在有哪些屏"这件事的唯一答案</b>（M5.4）。
///
/// <para>
/// 🔥 <b>为什么要从 <c>WindowsGdiScreenCapturer</c> 里抽出来</b>：
/// 抓屏后端从 M5.4 起<b>不止一个</b>（GDI 是降级链末端，DXGI 是生产路径），
/// 而"有哪些屏、每块屏在虚拟桌面里的哪个位置、要不要列'全部显示器'"是
/// <b>Windows 层的事实，与用哪种抓屏方式无关</b>。
/// </para>
///
/// <para>
/// ⚠️ 让两个后端各抄一份的代价不是"多写 90 行"，而是
/// <b>前端下拉框里的选项与后端能抓的东西会悄悄分叉</b> ——
/// 用户选了一个后端不认识的名字，两边都"看起来正常"。
/// ⇒ 只留一份。
/// </para>
///
/// <para>
/// ⚠️ <b>DPI</b>：这里取的是<b>系统认为的</b>显示器矩形。若调用进程不是 DPI 感知的，
/// Windows 会把桌面按缩放比虚拟化给它，读出来的就是<b>缩小过的</b>尺寸。
/// 因此托盘启动时显式声明 per-monitor-v2 感知（见 <c>Agent.Tray/Program.cs</c>）。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsScreenEnumerator
{
    /// <summary>虚拟桌面（所有显示器拼成的整块桌面）的屏 ID。</summary>
    /// <remarks>
    /// 🔥 它是 <c>null</c>/空 <c>screenId</c> 的落点：被控端是 2×2 拼 4K 的视频墙时，
    /// 要抓的就是这一块（7680×4320），而不是某一个输出。
    /// </remarks>
    internal const string VirtualDesktopId = "virtual-desktop";

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const int MONITORINFOF_PRIMARY = 0x1;

    /// <summary>
    /// 枚举可捕获的显示器（<b>主显示器排最前</b>，多屏时在最前面插一条"全部显示器"）。
    /// </summary>
    /// <param name="sessionId">
    /// 调用方所在会话号，原样填进 <see cref="ScreenDescriptor.SessionId"/>。
    /// <para>⚠️ <b>必须由调用方传进来，不能在这里"猜"</b>：那个字段存在的唯一理由就是
    /// "别抓到别的会话的桌面"，而本机也<b>没有</b>可靠的"我在哪个会话"的 API。</para>
    /// </param>
    internal static IReadOnlyList<ScreenDescriptor> Enumerate(int sessionId)
    {
        var screens = new List<ScreenDescriptor>();

        try
        {
            var found = MonitorRects();

            if (found.Count == 0) return Array.Empty<ScreenDescriptor>();

            // 主显示器排前面：前端下拉框的默认项就是"第一个"。
            foreach (var monitor in found.OrderByDescending(m => m.IsPrimary))
            {
                screens.Add(new ScreenDescriptor(
                    Id: monitor.Device,
                    Name: Describe(monitor),
                    Width: monitor.Width,
                    Height: monitor.Height,
                    IsPrimary: monitor.IsPrimary,
                    SessionId: sessionId,

                    // ⚠️ 这两个偏移**不是给抓屏用的**（抓屏只按宽高截那一块），
                    //    而是给 M5.2 的输入注入用的：中心送来的是"相对画面"的归一化坐标，
                    //    注入要用虚拟桌面像素，换算缺了它就会整体错位 ——
                    //    而单屏机器上偏移恒为 0，所以这类错在开发机上根本看不出来。
                    Left: monitor.X,
                    Top: monitor.Y));
            }

            // ── 虚拟桌面（多屏拼接）──
            //
            // 只有在真的"多于一块屏"时才列出来：单屏时它与那块屏完全等价，
            // 列出来只会让下拉框出现两条一模一样的选项，让人以为哪里配错了。
            if (screens.Count > 1)
            {
                var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
                var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
                var w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                var h = GetSystemMetrics(SM_CYVIRTUALSCREEN);

                if (w > 0 && h > 0)
                {
                    screens.Insert(0, new ScreenDescriptor(
                        Id: VirtualDesktopId,
                        Name: $"全部显示器（{screens.Count} 块拼成 {w}×{h}）",
                        Width: w,
                        Height: h,
                        IsPrimary: false,
                        SessionId: sessionId,
                        IsVirtualDesktop: true,

                        // 🔥 虚拟桌面的原点就是它自己的左/上（可能是**负数**：
                        //    副屏摆在主屏左边时虚拟桌面原点在负坐标上）。
                        //    注入时用它把归一化坐标换回真实像素，正好还原这块拼接屏。
                        Left: x,
                        Top: y));
                }
            }
        }
        catch
        {
            // 枚举不出来 = 空表（"没探到"），不是"没有显示器"。
            return Array.Empty<ScreenDescriptor>();
        }

        return screens;
    }

    /// <summary>逐块显示器读位置与尺寸（<b>不排序、不加工</b>）。</summary>
    internal static List<MonitorInfo> MonitorRects()
    {
        var found = new List<MonitorInfo>();

        // ⚠️ 回调里**绝不能抛**：异常穿过原生栈会直接把进程带走。
        //    （这个 lambda 只做纯内存操作，不会抛；加 try 是为了守住这条纪律。）
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            try
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };

                if (GetMonitorInfo(monitor, ref info))
                {
                    found.Add(new MonitorInfo(
                        info.szDevice,
                        info.rcMonitor.Left,
                        info.rcMonitor.Top,
                        info.rcMonitor.Right - info.rcMonitor.Left,
                        info.rcMonitor.Bottom - info.rcMonitor.Top,
                        (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
                }
            }
            catch
            {
                // 忽略这一个显示器。
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>给人看的一行描述（前端下拉框直接显示它）。</summary>
    internal static string Describe(MonitorInfo monitor)
    {
        var size = $"{monitor.Width}×{monitor.Height}";
        var at = $"{monitor.X},{monitor.Y}";

        return monitor.IsPrimary
            ? $"主显示器 {monitor.Device}（{size} @ {at}）"
            : $"显示器 {monitor.Device}（{size} @ {at}）";
    }

    /// <summary>
    /// 一块显示器的位置与尺寸。
    /// <para>
    /// ⚠️ 刻意用 <c>record class</c> 而不是 <c>record struct</c>：
    /// 查找"这块屏还在不在"时要能表达 <c>null</c>（拔出显示器是真实场景）。
    /// 结构体的 <c>FirstOrDefault</c> 返回的是全 0 默认值，
    /// 看起来像"显示器在 0,0 且宽高为 0"，那是另一种故障的样子。
    /// </para>
    /// </summary>
    internal sealed record MonitorInfo(
        string Device,
        int X,
        int Y,
        int Width,
        int Height,
        bool IsPrimary);

    // ═══════════════════════════════════════════════════════════
    // Win32
    // ═══════════════════════════════════════════════════════════

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
