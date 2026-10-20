using System.Globalization;
using ControlServer.Core.Services;

namespace ControlServer.LicenseTool;

/// <summary>
/// 命令行上的时间输入解析。
///
/// <para>
/// <b>一律按北京时间理解</b>。理由：写命令的人脑子里想的是"到期日是 2027 年 10 月 8 日"，
/// 而不是"那个时刻的 UTC 毫秒戳"。工具负责把北京墙钟换算成 UTC 时刻写进授权文件，
/// 与服务端展示、与客户认知完全一致。若这里改成"按本机时区解释"，
/// 同一行命令在不同时区的机器上会签出不同的到期时刻。
/// </para>
/// </summary>
internal static class BeijingInput
{
    /// <summary>精确到分钟的写法（<c>et</c>）。</summary>
    private static readonly string[] ExactFormats =
    {
        "yyyy-MM-dd HH:mm:ss",
        "yyyy/MM/dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy/MM/dd HH:mm",
    };

    /// <summary>只给日期的写法（<c>ed</c>，当天北京时间 23:59:59 结束）。</summary>
    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd",
        "yyyy/MM/dd",
        "yyyyMMdd",
    };

    /// <summary>解析成"北京时间的某个精确时刻"。</summary>
    public static bool TryParseInstant(string? text, out DateTimeOffset value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(text)) return false;

        if (!DateTime.TryParseExact(
                text.Trim(), ExactFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        value = new DateTimeOffset(parsed, BeijingTime.Offset);
        return true;
    }

    /// <summary>解析成"北京时间的某一天"。</summary>
    public static bool TryParseDate(string? text, out DateOnly value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(text)) return false;

        if (!DateOnly.TryParseExact(
                text.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>把北京日期格式化成授权文件里的 <c>yyyyMMdd</c>。</summary>
    public static string ToLicenseDate(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>可读的北京时间文本（含"北京时间"字样，避免看日志的人误当本地时间）。</summary>
    public static string Describe(DateTimeOffset value) => BeijingTime.Format(value) + "（北京时间）";
}
