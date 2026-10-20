using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ControlServer.LicenseTool.Gui;

/// <summary>
/// 应用入口。
///
/// <para>
/// 只做一件事：<b>把任何未处理异常变成一句人话</b>。
/// 签发工具崩掉时的最坏情形不是弹窗，而是<b>静默退出</b> —— 用户会以为"双击没反应"，
/// 于是反复双击，而每次都什么也没发生。所以三个入口（UI 线程、后台线程、未观察的任务）
/// 全都要兜住，且弹窗里必须带上异常类型与消息。
/// </para>
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 强制软件渲染。这不是性能取舍，是可靠性取舍：本工具只有表单，不吃 GPU；
        // 而 WPF 的硬件加速在**远程桌面 / 虚拟机 / 老旧或远程显卡驱动**上反而容易出故障
        // （黑块、残影、甚至窗口起不来）。签发工具会被装到各种客户的机器上跑，稳比快重要。
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        // UI 线程：能拦下就拦下，界面继续活着。
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 后台线程：拦不住（进程终将结束），但至少把原因说出来。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ShowFatal("后台线程异常", args.ExceptionObject as Exception);

        // 被丢弃的 Task 异常：默认会被静默吞掉，这里显式拿出来。
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            ShowFatal("后台任务异常", args.Exception);
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ShowFatal("界面操作出错", e.Exception);
    }

    private static void ShowFatal(string title, Exception? ex)
    {
        var detail = ex is null
            ? "（没有可用的异常信息）"
            : $"{ex.GetType().Name}: {ex.Message}";

        MessageBox.Show(
            $"{detail}\n\n这属于工具自身的缺陷，请把这句话连同当时的操作一起反馈给开发方。",
            $"授权签发工具 — {title}",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
