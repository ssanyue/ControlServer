namespace ControlServer.LicenseTool;

/// <summary>
/// 控制台输出。
///
/// <para>
/// <b>只用 ASCII 标记</b>（<c>[ok]</c> / <c>[warn]</c> / <c>[error]</c>），不用 <c>✓</c>/<c>✗</c>/<c>⚠</c>：
/// 签发人常常是把它跑在 <c>cmd.exe</c>（代码页 936）里，甚至把输出重定向进日志文件。
/// 那些符号在 GBK 控制台会变成 <c>?</c>，在 Python 脚本里更会直接 <c>UnicodeEncodeError</c>。
/// 正文是中文没问题（那是真内容），标记本身则必须是最保险的字符集。
/// </para>
/// </summary>
internal static class ConsoleOut
{
    /// <summary>默认标签列宽（以<b>显示宽度</b>计，中日韩字符算 2 列）。</summary>
    public const int DefaultLabelWidth = 12;

    public static void Line(string text = "") => Console.WriteLine(text);

    /// <summary>段落标题。</summary>
    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"== {title} ==");
    }

    public static void Ok(string text) => Console.WriteLine($"[ok]    {text}");

    public static void Warn(string text) => Console.WriteLine($"[warn]  {text}");

    /// <summary>
    /// "结论是否定"的标记，写 <b>stdout</b>。
    /// <para>
    /// 与 <see cref="Error"/> 的区别很重要：<c>inspect</c> 报出"这份授权不可用"是<b>正常结论</b>，
    /// 不是命令执行失败。把它写进 stderr 会让"报告的一部分"和"出错了"混在一起，
    /// 用管道取输出的人会莫名其妙地丢掉半截结论。
    /// </para>
    /// </summary>
    public static void Fail(string text) => Console.WriteLine($"[fail]  {text}");

    /// <summary>错误写 stderr（脚本据此区分正常输出与失败原因），并保证退出码非零。</summary>
    public static void Error(string text) => Console.Error.WriteLine($"[error] {text}");

    /// <summary>补充说明，跟在 ok/warn/error 后面。</summary>
    public static void Hint(string text) => Console.WriteLine($"        {text}");

    /// <summary>
    /// 对齐的"标签 — 值"行。
    /// <para>
    /// 按<b>显示宽度</b>补空格，不是按字符数：标签里有中文，一个汉字占两列。
    /// 用 <c>PadRight</c> 会让"私钥"与"公钥指纹"这两行差 2 列，
    /// 整个报告看起来就是"没对齐"——而这类工具的第一印象很重要。
    /// </para>
    /// </summary>
    public static void Field(string label, string value, int labelWidth = DefaultLabelWidth)
    {
        var padding = Math.Max(1, labelWidth - DisplayWidth(label));
        Console.WriteLine("  " + label + new string(' ', padding) + value);
    }

    /// <summary>文本在等宽控制台里占的列数（东亚全角字符按 2 列计）。</summary>
    public static int DisplayWidth(string text)
    {
        var width = 0;

        foreach (var ch in text)
        {
            width += IsWide(ch) ? 2 : 1;
        }

        return width;
    }

    /// <summary>空值显示成 <c>（无）</c>，避免出现"字段后面什么都没有"这种看不出是空还是漏印的行。</summary>
    public static string OrNone(string? value)
        => string.IsNullOrWhiteSpace(value) ? "（无）" : value;

    /// <summary>缩进的原文块（用于打印可粘贴的代码片段）。</summary>
    public static void Block(string text, string indent)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            Console.WriteLine(indent + line);
        }
    }

    /// <summary>
    /// 该字符在等宽终端里是否占两列。
    /// <para>
    /// 覆盖范围刻意保守（CJK 统一表意文字、假名、韩文、全角形式等），
    /// 只求"中文报告能对齐"，不追求 <c>wcwidth</c> 的完整语义。
    /// </para>
    /// </summary>
    private static bool IsWide(char ch)
        => ch >= 0x1100
           && (ch <= 0x115F                                  // 韩文字母
               || ch is '\u2329' or '\u232A'                 // 〈 〉
               || (ch >= 0x2E80 && ch <= 0xA4CF && ch != 0x303F) // CJK 部首 / 汉字 / 假名
               || (ch >= 0xAC00 && ch <= 0xD7A3)             // 韩文音节
               || (ch >= 0xF900 && ch <= 0xFAFF)             // CJK 兼容象形文字
               || (ch >= 0xFE30 && ch <= 0xFE6F)             // CJK 兼容形式
               || (ch >= 0xFF00 && ch <= 0xFF60)             // 全角形式
               || (ch >= 0xFFE0 && ch <= 0xFFE6));           // 全角符号

    /// <summary>
    /// PEM 文本规范化：以<b>恰好一个换行</b>结尾。
    /// <para>
    /// <c>ExportSubjectPublicKeyInfoPem()</c> 返回的内容不带结尾换行，
    /// 而项目里已有的 <c>VendorPublicKey.Pem</c> 是带换行的（原生字符串字面量的形状）。
    /// 不统一的话，"把生成结果粘进源码"会在最后一行产生一个多余的 diff ——
    /// 一份号称"直接替换"的片段却需要人手补一个换行，这种事会让人怀疑其他部分是不是也要改。
    /// </para>
    /// </summary>
    public static string NormalizePem(string pem)
        => pem.TrimEnd('\r', '\n') + "\n";
}
