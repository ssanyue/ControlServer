namespace ControlServer.Core.Services;

/// <summary>
/// 北京时间（UTC+8）。与系统时区解耦：无论服务部署在哪个时区，对外时间戳一律北京时间。
/// <para>
/// <b>时钟校正（M2-D）</b>：<see cref="Now"/> 在系统时钟之上叠加一个由 <see cref="TimeSyncService"/>
/// 经 SNTP 得到的偏移量 <see cref="ClockOffset"/>。这样日志、配置 <c>UpdatedAt</c>、
/// 收发日志、定时器**全应用统一**走校正后的时间（M2 方案 §5.6）。
/// </para>
/// <para>
/// 偏移默认为 <see cref="TimeSpan.Zero"/>，因此**未校准时行为与引入本特性之前完全一致**
/// （集成测试环境默认不启用自动校准，见 <c>Server:Time:AutoSync</c>）。
/// 需要"纯系统钟"时用 <see cref="NowRaw"/>。
/// </para>
/// <para>
/// ⚠️ <b>已知局限</b>：Serilog 的 <c>RollingInterval.Day</c> 用的是
/// <c>Serilog.Sinks.File</c> 内部的 <c>Clock</c>，该类型是 <b>internal</b>
/// （6.0.0 的公开类型清单里没有它），无法注入。
/// 因此**滚动文件名的切分点永远跟随系统钟**，只有文件内容里的时间戳吃校正。
/// 极端情况下（系统钟偏差跨越零点）会出现"行内日期与文件名不一致"。
/// 启动日志会显式打出这一条，不做隐瞒。
/// </para>
/// </summary>
public static class BeijingTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    public const string DisplayName = "UTC+8 (北京时间)";

    /// <summary>
    /// 校正量的 tick 数。用 <see cref="Interlocked"/> 读写：
    /// <see cref="TimeSyncService"/> 在后台线程写，业务线程读，且读的频次很高（定时器每秒）。
    /// </summary>
    private static long _clockOffsetTicks;

    /// <summary>
    /// 应用内时钟校正量（服务器时间 − 本地时间）。默认 <see cref="TimeSpan.Zero"/>。
    /// <para>由 <see cref="TimeSyncService"/> 在每次校准成功后写入；写入即对全应用生效。</para>
    /// </summary>
    public static TimeSpan ClockOffset
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _clockOffsetTicks));
        set => Interlocked.Exchange(ref _clockOffsetTicks, value.Ticks);
    }

    /// <summary>是否已应用过非零校正。</summary>
    public static bool IsCorrected => Interlocked.Read(ref _clockOffsetTicks) != 0;

    /// <summary>北京时间（<b>含</b> NTP 校正量）。全应用统一使用这个。</summary>
    public static DateTimeOffset Now => DateTimeOffset.UtcNow.Add(ClockOffset).ToOffset(Offset);

    /// <summary>
    /// 北京时间（<b>不含</b>校正量，纯系统钟）。仅用于降级，以及与"跟随系统钟的组件"
    /// 保持口径一致时（例如排查 Serilog 滚动文件名的日期）。
    /// </summary>
    public static DateTimeOffset NowRaw => DateTimeOffset.UtcNow.ToOffset(Offset);

    public static DateTimeOffset ToBeijing(DateTimeOffset value) => value.ToOffset(Offset);

    public static string Format(DateTimeOffset value) => ToBeijing(value).ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>把校正量复位为 0。供测试隔离使用。</summary>
    public static void ResetOffset() => Interlocked.Exchange(ref _clockOffsetTicks, 0);
}
