namespace ControlServer.Contracts;

/// <summary>
/// 一个生效的定时触发点。
/// </summary>
/// <param name="RefId">
/// 触发点标识：任务定时是按钮 Id；月度计划是 <c>&lt;buttonId&gt;:&lt;planId&gt;</c>。
/// 手动触发端点的路由参数用的就是它。
/// </param>
/// <param name="ButtonId">所属按钮 Id。</param>
/// <param name="ButtonLabel">所属按钮名称。</param>
/// <param name="Kind">触发类型：<c>Daily</c> / <c>SpecificDays</c> / <c>MonthlyPlan</c>。</param>
/// <param name="Time">触发时刻（北京时间，<c>HH:mm:ss</c>）。</param>
/// <param name="Days">星期条件（仅 <c>SpecificDays</c>）；0=周日 … 6=周六。</param>
/// <param name="MonthDays">月度日期条件（仅 <c>MonthlyPlan</c>），文本形式如 <c>7月21日</c>。</param>
/// <param name="AppliesToday">按调度器的规则，这个触发点今天是否适用（星期 / 月度日期条件）。</param>
/// <param name="FiredToday">今天是否已经触发过（来自 <c>timer_fires</c> 的当日记录）。</param>
/// <param name="FiredAt">今天实际触发的时间（北京时间）；未触发为 <c>null</c>。</param>
/// <param name="Elapsed">今天适用、且时刻已过（前端据此区分"待触发"与"今天已过点"）。</param>
public sealed record TimerTriggerDto(
    string RefId,
    string ButtonId,
    string ButtonLabel,
    string Kind,
    string Time,
    IReadOnlyList<int> Days,
    IReadOnlyList<string> MonthDays,
    bool AppliesToday,
    bool FiredToday,
    DateTimeOffset? FiredAt,
    bool Elapsed);

/// <summary>
/// 定时调度总览（<c>GET /api/timers</c>）。
/// </summary>
/// <param name="Enabled">调度是否启用（<c>Server:Timer:Enabled</c>）。</param>
/// <param name="TickIntervalSeconds">节拍（秒）。大于 1 会漏触发。</param>
/// <param name="CatchUpWindowSeconds">进程内追赶窗口（秒）；超出只告警不补执行。</param>
/// <param name="MissedScanWindowSeconds">启动时漏触发扫描窗口（秒）；0 表示关闭扫描。</param>
/// <param name="BackfillMissed">是否补执行停机期间错过的触发点（<c>Server:Timer:BackfillMissed</c>）。</param>
/// <param name="MaxBackfillCount">单次启动补执行上限（条）。</param>
/// <param name="RetentionDays">触发记录保留天数。</param>
/// <param name="FiredCount">本次进程生命周期内累计触发次数。</param>
/// <param name="MissedCount">本次进程生命周期内累计被跳过的漏触发条数。</param>
/// <param name="ServerTime">服务端当前时间（北京时间）。</param>
/// <param name="NextFireAt">下一个将要触发的时刻；算不出来时为 <c>null</c>（前端显示"—"）。</param>
/// <param name="Triggers">全部生效触发点。</param>
public sealed record TimerOverviewDto(
    bool Enabled,
    double TickIntervalSeconds,
    double CatchUpWindowSeconds,
    double MissedScanWindowSeconds,
    bool BackfillMissed,
    int MaxBackfillCount,
    int RetentionDays,
    long FiredCount,
    long MissedCount,
    DateTimeOffset ServerTime,
    DateTimeOffset? NextFireAt,
    IReadOnlyList<TimerTriggerDto> Triggers);

/// <summary>
/// 手动触发一次的结果（<c>POST /api/timers/{refId}/fire</c>）。
/// </summary>
/// <param name="Ok">是否真的执行了。</param>
/// <param name="Status">结果状态：<c>Fired</c> / <c>NotFound</c> / <c>AlreadyFired</c>。</param>
/// <param name="RefId">触发点标识。</param>
/// <param name="ButtonLabel">所属按钮名称。</param>
/// <param name="Date">本次使用的触发日期（北京时间）。</param>
/// <param name="Time">本次使用的触发时刻（北京时间）。</param>
/// <param name="Message">面向用户的一句话说明。</param>
/// <param name="Execution">动作执行结果；被幂等闸门挡下时为 <c>null</c>。</param>
public sealed record TimerFireResultDto(
    bool Ok,
    string Status,
    string RefId,
    string? ButtonLabel,
    string Date,
    string Time,
    string Message,
    ActionExecutionDto? Execution);
