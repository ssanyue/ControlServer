using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ControlServer.Platform.Windows;

/// <summary>
/// 进程级 DPI 感知（M5.1）。
///
/// <para>
/// 🔥 <b>为什么托盘必须显式声明它</b>：抓屏要拿到的是<b>真实像素</b>。
/// 一个非 DPI 感知的进程，Windows 会把整个桌面按缩放比<b>虚拟化</b>给它 ——
/// 在 150% 缩放的显示器上，<c>GetSystemMetrics</c> / <c>GetMonitorInfo</c> 报的是
/// "逻辑尺寸"，而 <c>BitBlt</c> 抓到的也是被缩放过的画面。
/// </para>
/// <list type="bullet">
///   <item>抓屏的结果：<b>缩小的、模糊的</b>画面，而日志上一切正常；</item>
///   <item>注入的结果（M5.2）：<b>点哪儿都偏</b>，且偏的倍数正好是缩放比 ——
///         这种偏差在 100% 缩放的机器上<b>完全不出现</b>，是最容易漏测的一类 bug。</item>
/// </list>
///
/// <para>
/// ⚠️ <b>必须在创建任何窗口之前调用</b>。Windows 在进程创建第一个 HWND 之后
/// 会锁定 DPI 感知模式，此时再调返回 <c>E_ACCESSDENIED</c>（且<b>不报错、只是不变</b>）。
/// 所以调用点必须在 <c>Program.cs</c> 最前面，而不是"用到抓屏时再设"。
/// </para>
///
/// <para>
/// <b>为什么用 <c>SetProcessDpiAwarenessContext</c> 而不是清单</b>：
/// 清单要改 csproj（<c>ApplicationManifest</c>），而本仓的托盘是"同一个 exe、
/// 既能被服务拉起也能双击排障"的形态，加清单会顺带影响那些形态的相容性。
/// 运行时调用只有一行，且失败可观测（返回值 + 日志）。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsProcessDpi
{
    /// <summary><c>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2</c>（Windows 10 1703+）。</summary>
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    /// <summary><c>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE</c>（Windows 8.1+）。</summary>
    private static readonly IntPtr PerMonitorAware = new(-3);

    /// <summary>
    /// 尽力把本进程设为 per-monitor-v2 感知。
    /// </summary>
    /// <returns>
    /// 人话结果（成功 / 已经是了 / 设不了）。<b>不抛</b> —— 这件事失败
    /// 不该让托盘起不来，但必须留一句能在日志里看到的话。
    /// </returns>
    public static string TryEnablePerMonitorV2()
    {
        // 先问现在是什么，免得"设成功了"与"本来就是"分不出来。
        var before = DescribeCurrent();

        try
        {
            if (SetProcessDpiAwarenessContext(PerMonitorAwareV2)) return $"已设为 per-monitor-v2（原本：{before}）。";

            var error = Marshal.GetLastWin32Error();

            // 老系统（Win8.1 之前）没有 V2，退到 V1。V1 在"跨不同缩放的显示器拖动窗口"时
            // 会有渲染问题，但对抓屏来说两者等价（都拿到真实像素）。
            if (SetProcessDpiAwarenessContext(PerMonitorAware)) return $"已设为 per-monitor-v1（V2 不可用：Win32 {error}；原本：{before}）。";

            return $"未能设置（Win32 {error}；原本：{before}）。"
                   + "最常见的原因是**进程已经创建过窗口**，DPI 感知模式已被锁定 —— "
                   + "抓屏会拿到被缩放过的画面。";
        }
        catch (Exception ex)
        {
            return $"设置 DPI 感知时出错（原本：{before}）：{ex.Message}";
        }
    }

    /// <summary>当前 DPI 感知模式的<b>人话</b>描述（进日志用）。</summary>
    private static string DescribeCurrent()
    {
        try
        {
            var context = GetThreadDpiAwarenessContext();

            if (AreDpiAwarenessContextsEqual(context, PerMonitorAwareV2)) return "per-monitor-v2";
            if (AreDpiAwarenessContextsEqual(context, PerMonitorAware)) return "per-monitor-v1";

            // 旧式的一族（system-aware / unaware）：
            // 它们的具体取值在 DPI_AWARENESS 枚举里，但那是"进程级"的概念。
            return "system-aware 或 unaware（< per-monitor）";
        }
        catch
        {
            return "未知";
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
}
