using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using ControlServer.Contracts;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// 从 Windows「System」事件日志里读开关机 / 蓝屏 / 睡眠记录（M4.2）。
/// <para>
/// <b>为什么要做服务才有这一块</b>：读事件日志需要本机 <c>Event Log Readers</c> 组成员身份
/// 或服务身份（LocalSystem）。Agent 以服务运行，天然满足 —— 这是"做成服务"的一个实打实的好处。
/// </para>
/// <para>
/// <b>为什么是"轮询 + 记录号水位"而不是 <c>EventLogWatcher</c></b>：
/// </para>
/// <list type="bullet">
///   <item><b>必须补扫</b>：Agent 没运行时发生的关机根本不会被任何订阅收到，
///         所以"启动时回溯"这条路一定要走。既然走了回溯，订阅就只是增量的一条捷径。</item>
///   <item><c>EventLogWatcher</c> 在服务里会静默失效（日志被清理、策略变更、句柄被回收），
///         而<b>失效时它不报错</b> —— 表现是"这台机器的开关机记录从某天起就再也没有了"，
///         这种"安静地少了一半数据"比"晚 30 秒拿到"糟得多。</item>
///   <item>轮询的成本可以忽略：30 秒一次、只在 System 日志里按 EventID 过滤。</item>
/// </list>
/// <para>
/// <b>增量水位用 <c>RecordId</c> 而不是时间戳</b>：记录号单调递增，不受时钟校正、
/// 服务重启、夏令时影响；时间戳会。两个条件（时间 ≥ from、记录号 &gt; 水位）取交集，
/// 再靠服务端的 (AgentId, At, EventId) 去重兜底。
/// </para>
/// </summary>
public sealed class WindowsPowerEventSource : IPowerEventSource
{
    private const string LogName = "System";

    /// <summary>单次最多返回多少条（避免日志被洪水式写入时把内存吃光）。取<b>最新的</b>那批。</summary>
    private const int MaxEventsPerRead = 500;

    private const int DetailMaxLength = 400;

    /// <summary>
    /// 关注的事件 ID → 我们的事件种类。
    /// <para>6008（上次关机是意外的）与 41（内核未干净关闭）常常同一次事故各出一条 ——
    /// 去重那一层在服务端统计里做（见 <c>AgentMetricsAggregator.SummarizePower</c>）。</para>
    /// </summary>
    private static readonly Dictionary<int, string> Watched = new()
    {
        [6005] = AgentPowerEventKinds.Boot,        // 事件日志服务已启动
        [6006] = AgentPowerEventKinds.Shutdown,    // 事件日志服务已停止（干净关机）
        [1074] = AgentPowerEventKinds.Restart,     // 由进程 / 用户发起的重启或关机
        [6008] = AgentPowerEventKinds.Unexpected,  // 上次关机是意外的
        [41] = AgentPowerEventKinds.Unexpected,    // Kernel-Power：未干净关闭
        [1001] = AgentPowerEventKinds.Bsod,        // BugCheck
        [42] = AgentPowerEventKinds.Sleep,         // 进入睡眠
        [107] = AgentPowerEventKinds.Wake,         // 从睡眠恢复
    };

    public bool IsAvailable => OperatingSystem.IsWindows();

    public HostPowerEventBatch Read(DateTimeOffset from, long? afterRecordId)
    {
        if (!OperatingSystem.IsWindows()) return HostPowerEventBatch.Empty;

        try
        {
            var query = new EventLogQuery(LogName, PathType.LogName, BuildXPath(from));
            var events = new List<HostPowerEventReading>();
            long? maxRecordId = null;

            using var reader = new EventLogReader(query);

            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    var recordId = record.RecordId;

                    // 水位过滤：只收"比上次新"的。
                    if (afterRecordId is { } floor && recordId is { } id && id <= floor) continue;

                    if (recordId is { } rid && (maxRecordId is null || rid > maxRecordId)) maxRecordId = rid;

                    var reading = Convert(record);
                    if (reading is not null) events.Add(reading);
                }

                // 记录可能很多（日志被清理过），先攒着；超出上限时保留最新的那批。
                if (events.Count > MaxEventsPerRead * 2) break;
            }

            // 按时间升序（事件日志的枚举顺序不保证是时间序，尤其是按记录号回绕之后）。
            events.Sort((a, b) => a.At.CompareTo(b.At));

            if (events.Count > MaxEventsPerRead) events.RemoveRange(0, events.Count - MaxEventsPerRead);

            return new HostPowerEventBatch(events, maxRecordId);
        }
        catch
        {
            // 日志被禁用 / 权限不足 / 被安全软件拦截 —— 都不是致命错误：
            // 返回空批，开关机页显示"暂无记录"。绝不抛，否则会把上报循环带下去。
            return HostPowerEventBatch.Empty;
        }
    }

    /// <summary>
    /// XPath：一次把关注的 EventID 全部筛出来，并按时间下界过滤（否则会枚举整个日志）。
    /// <para>⚠️ <c>TimeCreated/@SystemTime</c> 是 ISO8601（UTC、带 7 位小数、以 Z 结尾），
    /// 同格式下字符串比较等价于时间比较；因此这里必须按同一格式、同一时区（UTC）拼。</para>
    /// </summary>
    private static string BuildXPath(DateTimeOffset from)
    {
        var ids = string.Join(" or ", Watched.Keys.OrderBy(x => x).Select(id => $"EventID={id}"));
        var since = from.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

        return $"*[System[({ids}) and TimeCreated[@SystemTime>='{since}']]]";
    }

    private static HostPowerEventReading? Convert(EventRecord record)
    {
        try
        {
            if (!Watched.TryGetValue(record.Id, out var kind)) return null;
            if (record.TimeCreated is not { } created) return null;

            var properties = ReadProperties(record);

            return new HostPowerEventReading(
                ToBeijing(created),
                kind,
                record.Id,
                record.RecordId,
                ExtractActor(record.Id, properties),
                BuildDetail(record, properties));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把事件时间转成北京时间。
    /// <para>
    /// ⚠️ <b>这里踩过一个"安静的坑"，务必不要改回固定偏移量</b>：
    /// <c>EventRecord.TimeCreated</c> 的 <c>Kind</c> 是 <see cref="DateTimeKind.Local"/>，
    /// 而 <c>new DateTimeOffset(created, TimeSpan.Zero)</c> 在 Kind=Local 且偏移量不匹配时会
    /// <b>抛 <see cref="ArgumentException"/></b>。上一版正是这么写的，结果每一场事件都被
    /// 外层 catch 吞掉 —— 表现为「读到了记录、却返回 0 条」，页面上一片空白，
    /// 而日志里连一条错误都没有。用单参构造让它自己按 Kind 判偏移即可（永不抛）。
    /// </para>
    /// </summary>
    private static DateTimeOffset ToBeijing(DateTime created)
    {
        try
        {
            return new DateTimeOffset(created).ToOffset(BeijingOffset);
        }
        catch
        {
            // 兜底：取不到就退化为"现在"（宁可时间不准，也不要丢掉这条记录本身）。
            return DateTimeOffset.Now.ToOffset(BeijingOffset);
        }
    }

    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

    /// <summary>
    /// 取出事件的<b>文本型</b>渲染参数（<c>InsertionStrings</c>）。
    /// <para>
    /// <b>取值方式刻意"最保守"</b>：绝不去猜"第 6 个字段一定是用户名"，只原样收集，
    /// 顺序照样<b>不是契约</b>。挑"发起者"的启发式见 <see cref="ExtractActor"/>。
    /// </para>
    /// <para>
    /// ⚠️ <b>这里必须按类型过滤，不能无差别 <c>Value.ToString()</c></b>（实测踩过）：
    /// </para>
    /// <list type="bullet">
    ///   <item><c>byte[]</c> 属性会渲染成字面量 <c>"System.Byte[]"</c> —— 一个 .NET 类型名
    ///         泄漏到界面上，既不是"详情"也不是"未知"，纯属噪音。</item>
    ///   <item>另一些事件（如 Kernel-Power 41）的载荷是一串<b>没有 schema 的裸数字</b>
    ///         （<c>0 | 0 | 0 | 6 | 3225616396 | ...</c>），人眼无法解读，
    ///         与事件查看器里那条消息也对不上。</item>
    /// </list>
    /// 这两类值会让「详情」列在<b>一半以上</b>的记录上失效 —— 而这一列的<b>唯一用途</b>
    /// 就是让管理员逐条与事件查看器核对。本项目宁可留空（前端显示 <c>—</c>），
    /// 也不喂"看着像数据"的东西。
    /// </summary>
    private static List<string> ReadProperties(EventRecord record)
    {
        var result = new List<string>();

        try
        {
            if (record.Properties is null) return result;

            foreach (var property in record.Properties)
            {
                try
                {
                    if (property?.Value is string text && !string.IsNullOrWhiteSpace(text))
                        result.Add(text.Trim());
                }
                catch
                {
                    // 单个属性读不出来就跳过。
                }
            }
        }
        catch
        {
            // 忽略。
        }

        return result;
    }

    /// <summary>
    /// 从 1074 的属性里挑出"发起者"。
    /// <para>
    /// <b>为什么用启发式而不是固定下标</b>：1074 的属性顺序在不同 Windows 版本上并不一致，
    /// 硬编码下标会在某版本上取到一个进程名然后把它当用户名显示 —— 那是"看着像真的"的错数据。
    /// 这里只接受"看起来像账号"的字符串（含 <c>\</c>、不含盘符冒号、不以 <c>.exe</c> 结尾），
    /// 挑不到就老老实实为空。
    /// </para>
    /// </summary>
    private static string? ExtractActor(int eventId, List<string> properties)
    {
        if (eventId != 1074) return null;

        foreach (var value in properties)
        {
            if (value.Contains('\\', StringComparison.Ordinal)
                && !value.Contains(':', StringComparison.Ordinal)
                && !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && value.Length <= 128)
            {
                return value;
            }

            // UPN 形式（user@domain）
            if (value.Contains('@', StringComparison.Ordinal)
                && !value.Contains(' ', StringComparison.Ordinal)
                && value.Length <= 128)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// 「详情」列的文本。
    /// <para>
    /// 优先级：<b>事件正文原文</b>（<c>FormatDescription</c>）→ 退路是插入字符串。
    /// </para>
    /// <para>
    /// <b>这与"不解析正文"并不矛盾，别再把它当成同一个决定</b>：
    /// 「不解析」指的是<b>不从正文里抠出断电时刻当作 <c>At</c></b>（正文随系统语言变化，
    /// 抠错会得到一个"看着很合理"的错时间）；而这里只是把那条消息<b>原样显示</b>出来，
    /// 正是管理员打开事件查看器要看的那一行，人工核对最省事。两者的差别是
    /// <b>解析成字段</b> vs <b>原样呈现</b>。
    /// </para>
    /// <para>
    /// 正文取不到（消息资源 DLL 缺失 / 提供程序未注册 → <c>FormatDescription</c> 会抛）时，
    /// 退回插入字符串拼接 —— 那时至少还有 6008 的 <c>17:09:49 | 2026/10/8</c> 这类可读片段。
    /// </para>
    /// </summary>
    private static string? BuildDetail(EventRecord record, List<string> properties)
    {
        try
        {
            var message = record.FormatDescription();

            if (!string.IsNullOrWhiteSpace(message))
                return Truncate(Collapse(message));
        }
        catch
        {
            // 落到下面的退路。
        }

        return Join(properties);
    }

    /// <summary>把正文里的换行 / 连续空白压成单空格 —— 表格单元格里多行会撑烂排版。</summary>
    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var ch in text)
        {
            var isSpace = ch is ' ' or '\t' or '\r' or '\n' or '\f' or '\v';

            // 前导空白丢掉；内部连续空白只留一个。
            if (isSpace)
            {
                if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString().TrimEnd();
    }

    private static string Truncate(string text)
        => text.Length > DetailMaxLength ? text[..DetailMaxLength] : text;

    private static string? Join(List<string> properties)
    {
        if (properties.Count == 0) return null;

        var builder = new StringBuilder();

        foreach (var value in properties)
        {
            if (builder.Length > 0) builder.Append(" | ");
            builder.Append(value);

            if (builder.Length > DetailMaxLength) break;
        }

        return Truncate(builder.ToString());
    }
}
