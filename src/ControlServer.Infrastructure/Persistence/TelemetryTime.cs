using ControlServer.Core.Services;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 遥测时间戳在**库里的表示**（M4.2）。
/// <para>
/// ⚠️ <b>为什么遥测表存 Unix 毫秒（<c>long</c>）而不是 <c>DateTimeOffset</c></b> ——
/// 这不是风格偏好，是被 SQLite 逼出来的，改动前请先读完：
/// </para>
/// <list type="number">
///   <item>
///     <b>ORDER BY 直接不行</b>：EF Core 的 SQLite 提供程序<b>拒绝翻译</b>
///     <c>OrderBy(x =&gt; x.At)</c>（<c>At</c> 是 <c>DateTimeOffset</c>），运行时抛
///     <c>NotSupportedException: SQLite does not support expressions of type
///     'DateTimeOffset' in ORDER BY clauses</c>。而"某台机器最近 N 小时"这种查询
///     天生就要排序 —— 整个遥测都建立在它上面。
///   </item>
///   <item>
///     <b>范围比较只是"看起来能用"</b>：EF 把 <c>DateTimeOffset</c> 存成 TEXT，
///     范围比较退化成字符串的字典序比较。它在"所有行的偏移量完全一致"这个
///     <b>隐含前提</b>成立时才正确。少一次（有人写进一个 UTC 值、或将来改了时区策略），
///     范围查询就会<b>静默地少查一段</b> —— 页面上只是"曲线短了"，没人查得出来。
///     整数比较没有这个前提。
///   </item>
///   <item>
///     <b>顺带省 25 字节/行</b>：明细表按目标规模是 5 秒/台，TEXT 形态的时间戳
///     （<c>yyyy-MM-dd HH:mm:ss.fffffff+08:00</c>，33 字节）比 8 字节的 long 贵得多。
///   </item>
/// </list>
/// <para>
/// <b>精度无损</b>：写入前一律经 <c>EfAgentTelemetryStore</c> 截断到毫秒
/// （去重键要求如此），因此 <c>ToUnixTimeMilliseconds</c> 不会丢任何有效位，
/// 读出后能原样还原。
/// </para>
/// <para>
/// <b>例外</b>：<c>agent_inventories</c>（一台机器一行、无排序无范围查询）仍用
/// <c>DateTimeOffset</c> —— 那里"人能直接看懂"比省几个字节值钱。
/// </para>
/// </summary>
public static class TelemetryTime
{
    /// <summary>北京时间 → Unix 毫秒（<b>先截到毫秒</b>，与去重键口径一致）。</summary>
    public static long ToMs(DateTimeOffset value)
    {
        var truncated = new DateTimeOffset(
            value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond),
            value.Offset);

        return truncated.ToUnixTimeMilliseconds();
    }

    /// <summary>Unix 毫秒 → 北京时间（<c>DateTimeOffset</c>，偏移固定 +08:00）。</summary>
    public static DateTimeOffset FromMs(long milliseconds)
        => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToOffset(BeijingTime.Offset);
}
