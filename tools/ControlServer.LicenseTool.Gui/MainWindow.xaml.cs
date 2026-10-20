using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ControlServer.Core.Services;

namespace ControlServer.LicenseTool.Gui;

/// <summary>
/// 主窗口：签发表单的外壳与公共设施。
///
/// <para>
/// 业务逻辑在 <c>MainWindow.Issue.cs</c> 这个 partial 里，本文件只放"跟签发无关"的东西：
/// 初始值、内置密钥状态、时钟、忙碌/兜底、文件对话框。
/// </para>
///
/// <para>
/// <b>本工具只有一个页面。</b>两条边界是刻意的：
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>没有"解读授权"页</b> —— 签发人日常不需要它；真要核对一份 .lic，命令行版
/// <c>ControlServer.LicenseTool.exe inspect</c> 给的报告更完整，也能贴进工单。
/// </description></item>
/// <item><description>
/// <b>没有"授权类型"选择</b> —— full / trial / temp 三种取值对签发人没有实际区别
/// （临时授权 = 期限很短的正式授权），一律按 <c>full</c> 签发。
/// </description></item>
/// </list>
///
/// <para>
/// <b>没有"密钥"页签</b>：厂商私钥已编译进本工具（见 <c>tools/LicenseToolKey.cs</c>），
/// 签发人不需要、也不应该去保管或指认任何密钥文件。换钥属于厂商侧的一次性维护动作，
/// 走命令行的 <c>new-key</c>（见 <c>docs/授权操作手册.md</c>）。
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    private readonly OutputPanel _issue;

    /// <summary>初始化期间抑制控件事件 —— 否则给 RadioButton 设初值时会触发一串校验。</summary>
    private bool _loading = true;

    public MainWindow()
    {
        InitializeComponent();

        _issue = new OutputPanel(IssueLog, IssueBanner, IssueBannerText);

        InitDefaults();
        StartHeaderClock();
        ShowBuiltInKeyStatus();

        // 序列号与客户备注都会参与"默认输出文件名"的推导，任一变化都要刷新提示。
        TxtSerial.TextChanged += OnCustomerChanged;

        _loading = false;
        UpdateIssueFormState();
        OnCustomerChanged(this, null!);
    }

    // ═══════════════════════════════════════════════════════════
    // 初始化
    // ═══════════════════════════════════════════════════════════

    private void InitDefaults()
    {
        // 默认到期日给「3 天后」：授权基本都是按天/按月发的，给一年只会每次都手改一遍。
        // 要更长期限就点「快捷到期日」那一排按钮，或用日期选择器自己挑。
        DpExpireDate.SelectedDate = DateTime.Today.AddDays(3);
        DpExpireInstant.SelectedDate = DateTime.Today.AddDays(3);

        // 「使用截止」是通用授权"最迟什么时候还能被导入绑定"，与授权本身的期限是两件事。
        //
        // 默认给「今天」（时间框在 XAML 里恒为 23:59:59，见 MainWindow.xaml）—— 也就是"只到当天结束"。
        // 这是刻意取最短：通用授权是一份"任何机器都能绑"的文件，默认给一个月的导入窗口，
        // 等于每次签发都顺手把这份权限的可用期延长了一个月。要更长的窗口，由签发人自己把日期往后改：
        // 改多少，就等于明确地担了多少责任。余量会实时显示在「使用截止」下面那一行。
        //
        // 用北京日期而不是 DateTime.Today：使用截止写进授权的是北京时刻（ud），
        // 机器时区一旦不是 UTC+8，两个"今天"会差一天。
        DpDeadline.SelectedDate = BeijingTime.Now.Date;
    }

    /// <summary>
    /// 显示"内置签发密钥"能不能用。
    ///
    /// <para>
    /// 密钥内置之后，"选错钥匙"这类操作失误已经不可能发生。这里要盯的是另一个更根本的问题：
    /// <b>内置私钥与服务端内嵌公钥是否配对</b> —— 换钥时若只更新了一边，就会出现"签得出来、
    /// 服务端一律拒收"的情况。这必须在点「签发」之前就说清楚，而不是等客户导入时才发现。
    /// </para>
    ///
    /// <para>
    /// <b>刻意不显示指纹</b>：那是换钥时给维护人员做核对用的，日常签发看了没有意义，
    /// 反而让界面显得像在演示密码学。这里只回答"能不能签"。
    /// </para>
    /// </summary>
    private void ShowBuiltInKeyStatus()
    {
        if (!LicenseToolKey.IsAvailable)
        {
            SetProbeText(TxtBuiltInKey, LicenseToolKey.UnavailableMessage(LicenseToolKey.Error), "C.Danger");
            return;
        }

        if (LicenseToolKey.MatchesServer)
        {
            SetProbeText(
                TxtBuiltInKey,
                "已内置厂商签发密钥，与服务端内嵌公钥一致 —— 签发的授权服务端会收下。",
                "C.Ok");
        }
        else
        {
            SetProbeText(
                TxtBuiltInKey,
                "已内置厂商签发密钥，但它与服务端内嵌的公钥不是一对：签发的授权会被服务端判「签名无效」。"
                + "通常是换钥时只更新了一边 —— 请重编并重新交付服务端，同时重新构建本工具。",
                "C.Danger");
        }
    }

    /// <summary>设置提示行的文案与颜色（画刷键取自 <c>App.xaml</c> 的调色板）。</summary>
    private void SetProbeText(TextBlock target, string text, string brushKey)
    {
        target.Text = text;
        target.Foreground = (Brush)FindResource(brushKey);
    }

    /// <summary>
    /// 顶部时钟。签发人对"现在几点"很敏感（授权是按时间判定的），
    /// 所以一直把北京时间摆在眼前；用的是与服务端同一套北京时区口径。
    /// </summary>
    private void StartHeaderClock()
    {
        void Tick()
        {
            TxtHeaderClock.Text = $"北京时间 {BeijingTime.Format(BeijingTime.Now)}";

            // 「使用截止」默认只到"当天结束"，余量可能只剩几小时 —— 让它跟着这个每秒一次的时钟走，
            // 否则界面会停在一个几小时前的读数上，越看越安心。
            UpdateDeadlinePreview();
        }

        Tick();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    // ═══════════════════════════════════════════════════════════
    // 公共设施
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 执行一次"会用到网络/密码学"的操作：操作期间禁用按钮并显示忙碌提示，
    /// 任何异常都翻译成横幅上的一句人话，绝不让界面卡住或静默失败。
    /// </summary>
    private static async Task RunGuardedAsync(
        Button button,
        TextBlock busy,
        OutputPanel panel,
        string busyText,
        Func<Task> action)
    {
        button.IsEnabled = false;
        busy.Text = busyText;

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            panel.Error($"未预期的错误：{ex.GetType().Name}: {ex.Message}");
            panel.Banner(OutcomeLevel.Error, "操作失败：发生了未预期的错误，详见右侧明细。");
        }
        finally
        {
            button.IsEnabled = true;
            busy.Text = string.Empty;
        }
    }

    /// <summary>打开"另存为"对话框。返回选中的路径；取消时返回 <c>null</c>。</summary>
    private static string? SaveFile(string title, string filter, string suggestedName, string? initialPath = null)
    {
        var initialDirectory = !string.IsNullOrWhiteSpace(initialPath)
            ? Path.GetDirectoryName(initialPath)
            : null;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = suggestedName,
            OverwritePrompt = true,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
