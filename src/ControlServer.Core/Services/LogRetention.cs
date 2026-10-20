using System.Globalization;
using System.Text.RegularExpressions;

namespace ControlServer.Core.Services;

/// <summary>
/// 业务日志（<c>logs/log-YYYY-MM-DD.ndjson</c>）的保留策略（M6.5）。
///
/// <para>
/// <b>为什么必须有一条策略</b>：<c>LogService</c> 每天一个文件、<c>File.AppendAllTextAsync</c> 追加，
/// 全仓没有删除逻辑 ⇒ 日志体积随服役时间<b>线性增长</b>，而 <c>FlushAsync</c> 的 catch 是
/// <b>静默丢弃</b>（这是对的：写日志失败不该拖垮业务）。两条合起来的现场症状是
/// <b>"磁盘满了，第一个信号不是报错，是日志突然断了"</b> —— 极难查。
/// 策略不做，就等于把这个问题留给三个月后的现场。
/// </para>
///
/// <para>
/// 🔥 <b>只碰 <c>log-*.ndjson</c></b>。同一目录下还有 Serilog 的 <c>server-*.log</c>
/// （它自带 <c>retainedFileCountLimit</c>），以及运维可能手工放进去的排查记录。
/// 一个"按 mtime 删最旧文件"的通用清理器会在某次误配下把 <c>server-*.log</c> 一起清掉，
/// 而<b>出事时最需要看的恰恰是它</b>。所以这里用<b>文件名白名单</b>，名字对不上一律不动。
/// </para>
///
/// <para>
/// 🔥 <b>按文件名里的日期判龄，不看 <c>LastWriteTime</c></b>。复制、解压、备份恢复都会改写
/// mtime —— 那样会把"上周的日志"判成"今天的"。文件名是程序自己按北京日期写下的，才是真源。
/// （这条与 M6.4 §5 拒绝用文件时间戳当版本标识是同一个理由。）
/// </para>
/// </summary>
public sealed record LogRetentionOptions
{
    public static readonly LogRetentionOptions Default = new();

    /// <summary>总开关。默认开。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 保留天数（含今天），默认 <b>30</b>。
    /// <para>≤ 0 = 不按天数删（只受 <see cref="MaxTotalMegabytes"/> 约束）。</para>
    /// </summary>
    public int RetentionDays { get; init; } = 30;

    /// <summary>
    /// 所有 <c>log-*.ndjson</c> 的体积上限（MB），默认 <b>200</b>。
    /// <para>≤ 0 = 不按总量删（只受 <see cref="RetentionDays"/> 约束）。</para>
    /// <para>
    /// 为什么两个上限都要有：天数管"正常滚动"，总量管"某天日志突然暴涨"
    /// （例如把日志级别调到 Debug 排查问题，一天能顶一个月）。
    /// </para>
    /// </summary>
    public int MaxTotalMegabytes { get; init; } = 200;
}

/// <summary>一次清理的结果。全部字段都是"事实"，不是推测。</summary>
/// <param name="DeletedFiles">删掉的文件数。</param>
/// <param name="DeletedBytes">删掉的字节数。</param>
/// <param name="KeptFiles">留下的 <c>log-*.ndjson</c> 数量。</param>
/// <param name="KeptBytes">留下的 <c>log-*.ndjson</c> 总字节数。</param>
/// <param name="DeletedNames">删掉的文件名（按删除顺序，便于日志里复现）。</param>
/// <param name="IgnoredNames">名字不符合 <c>log-YYYY-MM-DD.ndjson</c> 而<b>刻意没动</b>的文件。</param>
/// <param name="Errors">删不掉的（被占用 / 没权限），一条一句。</param>
public sealed record LogPruneOutcome(
    int DeletedFiles,
    long DeletedBytes,
    int KeptFiles,
    long KeptBytes,
    IReadOnlyList<string> DeletedNames,
    IReadOnlyList<string> IgnoredNames,
    IReadOnlyList<string> Errors)
{
    public static readonly LogPruneOutcome Nothing = new(0, 0, 0, 0, [], [], []);

    /// <summary>这次有没有真的删掉东西（决定要不要往日志里写一行）。</summary>
    public bool HasDeletions => DeletedFiles > 0;
}

/// <summary>
/// 日志清理的<b>纯函数实现</b>：给一个目录 + 策略 + "今天是哪天"，返回做了什么。
///
/// <para>
/// 做成静态纯函数（不用 <c>BeijingTime.Now</c> 内部取时间）是为了能测：
/// "30 天前的文件会被删"这件事，靠等 30 天是测不出来的。
/// </para>
/// </summary>
public static class LogRetention
{
    public const string FilePrefix = "log-";
    public const string FileSuffix = ".ndjson";

    /// <summary>
    /// 严格匹配 <c>log-YYYY-MM-DD.ndjson</c>。
    /// <para>
    /// 用正则而不是 <c>StartsWith</c> + <c>TryParse</c>：后者会把
    /// <c>log-2026-10-17.ndjson.bak</c>、<c>log-2026-10-17-排查.ndjson</c> 也算进来，
    /// 于是"运维手工留的证据"会在某次清理里消失。
    /// </para>
    /// </summary>
    private static readonly Regex FileNamePattern = new(
        @"^log-(?<y>\d{4})-(?<m>\d{2})-(?<d>\d{2})\.ndjson$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>这一天对应的日志文件名（与 <c>LogService</c> 落盘时用的格式一致）。</summary>
    public static string FileNameFor(DateOnly date) =>
        $"{FilePrefix}{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{FileSuffix}";

    /// <summary>从文件名里取日期；名字不合规或日期非法（如 <c>2026-13-45</c>）返回 <c>null</c>。</summary>
    public static DateOnly? TryParseDate(string fileName)
    {
        var match = FileNamePattern.Match(fileName);
        if (!match.Success) return null;

        var y = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
        var m = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var d = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);

        if (m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return null;

        return new DateOnly(y, m, d);
    }

    /// <summary>
    /// 执行一次清理。<b>永不抛异常</b>（除目录本身不可枚举时把原因写进 <see cref="LogPruneOutcome.Errors"/>）。
    /// </summary>
    /// <param name="logDirectory">日志目录（不存在则视为无事可做）。</param>
    /// <param name="options">策略。</param>
    /// <param name="today">"今天"（北京日期），由调用方给 —— 便于测试。</param>
    public static LogPruneOutcome Prune(string logDirectory, LogRetentionOptions options, DateOnly today)
    {
        if (!options.Enabled) return LogPruneOutcome.Nothing;
        if (string.IsNullOrWhiteSpace(logDirectory) || !Directory.Exists(logDirectory))
        {
            return LogPruneOutcome.Nothing;
        }

        var errors = new List<string>();
        var ignored = new List<string>();
        var candidates = new List<(string Path, string Name, DateOnly Date, long Bytes)>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(logDirectory, FilePrefix + "*" + FileSuffix))
            {
                var name = Path.GetFileName(path);
                var date = TryParseDate(name);

                if (date is null)
                {
                    ignored.Add(name);
                    continue;
                }

                long bytes;
                try
                {
                    bytes = new FileInfo(path).Length;
                }
                catch (Exception ex)
                {
                    errors.Add($"{name}: 读不到大小（{ex.GetType().Name}）");
                    continue;
                }

                candidates.Add((path, name, date.Value, bytes));
            }
        }
        catch (Exception ex)
        {
            errors.Add($"目录不可枚举（{ex.GetType().Name}: {ex.Message}）");
            return new LogPruneOutcome(0, 0, 0, 0, [], ignored, errors);
        }

        // 最旧的排前面 —— 两条规则（超龄 / 超量）都按这个顺序删。
        candidates.Sort((a, b) => a.Date.CompareTo(b.Date));

        var deleted = new List<string>();
        var deletedNames = new List<string>();
        long deletedBytes = 0;

        bool IsToday(string name) => string.Equals(
            name, FileNameFor(today), StringComparison.OrdinalIgnoreCase);

        void TryDelete((string Path, string Name, DateOnly Date, long Bytes) item)
        {
            if (IsToday(item.Name))
            {
                // 🔴 今天的文件永不删：删了它，"日志断了"会变成自己造的。
                return;
            }

            try
            {
                File.Delete(item.Path);
                deleted.Add(item.Path);
                deletedNames.Add(item.Name);
                deletedBytes += item.Bytes;
            }
            catch (Exception ex)
            {
                errors.Add($"{item.Name}: 删不掉（{ex.GetType().Name}: {ex.Message}）");
            }
        }

        // ── 规则 1：超龄 ──
        var remaining = new List<(string Path, string Name, DateOnly Date, long Bytes)>();
        foreach (var item in candidates)
        {
            // 未来的日期（时钟回拨过、或运维手改过文件名）不按年龄删 —— 它可能是最新的。
            var age = today.DayNumber - item.Date.DayNumber;

            if (options.RetentionDays > 0 && age >= options.RetentionDays)
            {
                TryDelete(item);
            }
            else
            {
                remaining.Add(item);
            }
        }

        // ── 规则 2：超量（从最旧删到低于上限）──
        if (options.MaxTotalMegabytes > 0)
        {
            var cap = options.MaxTotalMegabytes * 1024L * 1024L;
            var total = remaining.Sum(x => x.Bytes);

            foreach (var item in remaining.ToArray())
            {
                if (total <= cap) break;
                if (IsToday(item.Name)) continue;   // 删到只剩今天也不能删今天

                var before = deletedBytes;
                TryDelete(item);
                if (deletedBytes > before)
                {
                    total -= item.Bytes;
                    remaining.Remove(item);
                }
            }
        }

        var keptBytes = 0L;
        foreach (var item in remaining)
        {
            keptBytes += item.Bytes;
        }

        return new LogPruneOutcome(
            deleted.Count,
            deletedBytes,
            remaining.Count,
            keptBytes,
            deletedNames,
            ignored,
            errors);
    }
}
