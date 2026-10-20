using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ControlServer.LicenseTool.Gui;

/// <summary>结论横幅的级别。</summary>
internal enum OutcomeLevel
{
    /// <summary>还没有结论（空横幅）。</summary>
    None,

    /// <summary>成功。</summary>
    Ok,

    /// <summary>成功但有需要知情的地方（例如"取不到网络时间，已用本机时钟"）。</summary>
    Warn,

    /// <summary>失败。横幅里必须写"为什么 + 下一步怎么办"。</summary>
    Error,
}

/// <summary>
/// 把"结论横幅 + 明细日志"这一组控件包成一个对象。
///
/// <para>
/// 页面逻辑只管喊话（<see cref="Ok"/> / <see cref="Error"/>…），不碰任何控件属性。
/// 好处是每个页面的代码读起来就是一段流程，而不是"设置这个控件的那个属性"的流水账。
/// </para>
/// </summary>
internal sealed class OutputPanel
{
    private readonly TextBox _log;
    private readonly Border _banner;
    private readonly TextBlock _bannerText;

    public OutputPanel(TextBox log, Border banner, TextBlock bannerText)
    {
        _log = log;
        _banner = banner;
        _bannerText = bannerText;
    }

    /// <summary>清空。每次操作开始前调用，避免把上一次的结论误当成这一次的。</summary>
    public void Reset()
    {
        _log.Clear();
        SetBanner(OutcomeLevel.None, string.Empty);
    }

    public void Line(string text = "")
    {
        _log.AppendText(text + Environment.NewLine);
        _log.ScrollToEnd();
    }

    public void Section(string title)
    {
        Line();
        Line($"== {title} ==");
    }

    public void Ok(string text) => Line($"[ok]    {text}");

    public void Warn(string text) => Line($"[warn]  {text}");

    public void Fail(string text) => Line($"[fail]  {text}");

    public void Error(string text) => Line($"[error] {text}");

    /// <summary>补充说明，跟在 ok/warn/error 后面。</summary>
    public void Hint(string text) => Line($"        {text}");

    /// <summary>
    /// 对齐的"标签 — 值"行。
    /// <para>
    /// 按<b>显示宽度</b>补空格，不是按字符数：标签里有中文，一个汉字占两列。
    /// 用 <c>PadRight</c> 会让"私钥"与"公钥指纹"两行差 2 列，整份报告看起来就没对齐 ——
    /// 而这类工具的第一印象很重要。与命令行版的 <c>ConsoleOut.Field</c> 同一套算法。
    /// </para>
    /// </summary>
    public void Field(string label, string value, int labelWidth = 12)
    {
        var padding = Math.Max(1, labelWidth - DisplayWidth(label));
        Line("  " + label + new string(' ', padding) + value);
    }

    /// <summary>文本在等宽显示下占的列数（东亚全角字符按 2 列计）。</summary>
    public static int DisplayWidth(string text)
    {
        var width = 0;

        foreach (var ch in text)
        {
            width += IsWide(ch) ? 2 : 1;
        }

        return width;
    }

    private static bool IsWide(char ch)
        => ch >= 0x1100
           && (ch <= 0x115F
               || ch is '\u2329' or '\u232A'
               || (ch >= 0x2E80 && ch <= 0xA4CF && ch != 0x303F)
               || (ch >= 0xAC00 && ch <= 0xD7A3)
               || (ch >= 0xF900 && ch <= 0xFAFF)
               || (ch >= 0xFE30 && ch <= 0xFE6F)
               || (ch >= 0xFF00 && ch <= 0xFF60)
               || (ch >= 0xFFE0 && ch <= 0xFFE6));

    /// <summary>设置结论横幅。</summary>
    public void Banner(OutcomeLevel level, string text) => SetBanner(level, text);

    /// <summary>把整段文本替换为日志内容（用于一次性输出的报告）。</summary>
    public void SetText(string text)
    {
        _log.Clear();
        _log.AppendText(text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
        _log.ScrollToEnd();
    }

    public string Text => _log.Text;

    private void SetBanner(OutcomeLevel level, string text)
    {
        // 与 ConsoleOut 同一纪律：只用 ASCII 级别的语义，颜色承担情绪，文字承担事实。
        var (background, foreground) = level switch
        {
            OutcomeLevel.Ok => ("#E7F6EC", "#15803D"),
            OutcomeLevel.Warn => ("#FEF3C7", "#92400E"),
            OutcomeLevel.Error => ("#FEE2E2", "#B91C1C"),
            _ => ("#EEF1F6", "#6B7280"),
        };

        _banner.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        _banner.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background));
        _bannerText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(foreground));
        _bannerText.Text = text;
    }
}
