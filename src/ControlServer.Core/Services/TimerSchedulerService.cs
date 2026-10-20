using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// 定时调度的运行参数。
/// </summary>
/// <param name="Enabled">总开关。关闭后不注册调度循环。</param>
/// <param name="TickInterval">检测节拍，默认 1 秒（与旧版一致）。</param>
/// <param name="CatchUpWindow">
/// 进程内追赶窗口。暂停/忙导致某次 tick 迟到时，回补这段时间内的触发点（仍受幂等键约束）。
/// 超出该窗口的<b>不补</b>，只告警 —— 避免长时间阻塞后集中爆发。
/// </param>
/// <param name="MissedScanWindow">
/// 启动时"服务停机期间漏了什么"的扫描窗口（默认 10 分钟）。
/// 不开 <paramref name="BackfillMissed"/> 时**只统计与告警，绝不执行**（M2 方案 §12 决策 #3 的默认值）。
/// </param>
/// <param name="BackfillMissed">
/// 是否补执行"服务停机期间错过"的触发点（默认 <c>false</c>）。
/// <para>
/// <b>为什么默认关</b>：补执行是**替用户按下按钮**，一旦补错（比如 22:00 关屏错过了、
/// 次日 9:30 重启才补上）会产生真实后果。所以它必须是显式开启的运维决策，
/// 而不是"更聪明的默认行为"。
/// </para>
/// <para>
/// 开启后的语义只有三条，都写在 <see cref="TimerSchedulerService.BackfillMissedAsync"/> 上：
/// ① 每个触发点<b>最多补一次</b>（取窗口内最近那一次）；② 总补执行数受
/// <paramref name="MaxBackfillCount"/> 限制；③ 超出 <paramref name="MissedScanWindow"/> 的一律不补。
/// </para>
/// </param>
/// <param name="MaxBackfillCount">
/// 单次启动补执行的**总上限**（默认 20）。超出部分只报告不执行 ——
/// 一个 60 个按钮的展厅，闹钟式配置下 10 分钟窗口内最多也就几个点；
/// 真撞到上限说明配置不是"闹钟式"而是"轮询式"，那种场景补执行本身就没意义。
/// </param>
/// <param name="RetentionDays">触发记录的保留天数。</param>
public sealed record TimerSchedulerOptions(
    bool Enabled = true,
    TimeSpan? TickInterval = null,
    TimeSpan? CatchUpWindow = null,
    TimeSpan? MissedScanWindow = null,
    int RetentionDays = 35,
    bool BackfillMissed = false,
    int MaxBackfillCount = 20)
{
    public TimeSpan EffectiveTickInterval => TickInterval ?? TimeSpan.FromSeconds(1);

    public TimeSpan EffectiveCatchUpWindow => CatchUpWindow ?? TimeSpan.FromSeconds(60);

    public TimeSpan EffectiveMissedScanWindow => MissedScanWindow ?? TimeSpan.FromMinutes(10);

    public int EffectiveMaxBackfillCount => MaxBackfillCount <= 0 ? 0 : MaxBackfillCount;
}

/// <summary>
/// 一个当前生效的定时触发点（供 <c>GET /api/timers</c> 与启动自检展示）。
/// </summary>
/// <param name="RefId">触发源标识。</param>
/// <param name="ButtonId">所属按钮。</param>
/// <param name="ButtonLabel">按钮名称。</param>
/// <param name="Kind">触发类型：<c>Daily</c> / <c>SpecificDays</c> / <c>MonthlyPlan</c>。</param>
/// <param name="Time">触发时刻 <c>HH:mm:ss</c>。</param>
/// <param name="Days">星期条件（仅 SpecificDays）；0=周日…6=周六。</param>
/// <param name="MonthDays">月度日期条件（仅 MonthlyPlan），文本形式如 <c>1月1日</c>。</param>
public sealed record TimerTriggerInfo(
    string RefId,
    string ButtonId,
    string ButtonLabel,
    string Kind,
    string Time,
    IReadOnlyList<int> Days,
    IReadOnlyList<string> MonthDays);

/// <summary>手动触发的结果状态。</summary>
public enum TimerManualFireStatus
{
    /// <summary>抢到幂等键并已执行。</summary>
    Fired,

    /// <summary>触发点不存在（按钮或月度计划找不到）。</summary>
    NotFound,

    /// <summary>同一秒内已经触发过，本次被幂等闸门挡下。</summary>
    AlreadyFired,
}

/// <summary>
/// 手动触发一次的结果。
/// <para>无论成败都带回 <c>refId</c> 与本次使用的日期 / 时刻，便于前端与日志对账。</para>
/// </summary>
/// <param name="Status">结果状态。</param>
/// <param name="RefId">触发点标识。</param>
/// <param name="ButtonLabel">所属按钮名称（找不到按钮时为 <c>null</c>）。</param>
/// <param name="Date">触发日期（北京时间，<c>yyyy-MM-dd</c>）。</param>
/// <param name="Time">触发时刻（北京时间，<c>HH:mm:ss</c>）。</param>
/// <param name="Execution">动作执行结果；仅 <see cref="TimerManualFireStatus.Fired"/> 时有值。</param>
/// <param name="Message">面向用户的一句话说明。</param>
public sealed record TimerManualFireOutcome(
    TimerManualFireStatus Status,
    string RefId,
    string? ButtonLabel,
    string Date,
    string Time,
    ActionExecutionResult? Execution,
    string Message)
{
    public bool Ok => Status == TimerManualFireStatus.Fired;

    public static TimerManualFireOutcome Fired(
        string refId, string buttonLabel, string date, string time, ActionExecutionResult execution)
        => new(TimerManualFireStatus.Fired, refId, buttonLabel, date, time, execution,
            $"已手动触发「{buttonLabel}」：{execution.Summary}");

    public static TimerManualFireOutcome NotFound(string refId, string date, string time, string message)
        => new(TimerManualFireStatus.NotFound, refId, null, date, time, null, message);

    public static TimerManualFireOutcome AlreadyFired(
        string refId, string buttonLabel, string date, string time, string message)
        => new(TimerManualFireStatus.AlreadyFired, refId, buttonLabel, date, time, null, message);
}

/// <summary>
/// 一次"补执行"的结果（<see cref="TimerSchedulerService.BackfillMissedAsync"/>）。
/// <para>
/// 四个数字刻意分开，而不是只给一个"补了几条"：现场问的从来不是"补没补"，
/// 而是「少掉的那几个去哪了」—— 是同一个触发点只补一次（<see cref="CollapsedDuplicates"/>），
/// 还是超出上限（<see cref="OverCap"/>），还是压根在窗口外（那连 <see cref="Detected"/> 都不会有）。
/// </para>
/// </summary>
/// <param name="Detected">窗口内扫出多少个错过的点。</param>
/// <param name="Executed">真正执行成功（抢到幂等键）的个数。</param>
/// <param name="CollapsedDuplicates">因"同一触发点只补最近一次"而跳过的个数。</param>
/// <param name="OverCap">因超出补执行上限而跳过的个数。</param>
/// <param name="ExecutedLabels">执行了哪些点（人话，给日志与测试看）。</param>
public sealed record TimerBackfillResult(
    int Detected,
    int Executed,
    int CollapsedDuplicates,
    int OverCap,
    IReadOnlyList<string> ExecutedLabels)
{
    /// <summary>没补任何东西（未开启 / 窗口内没漏 / 全被幂等键挡下）。</summary>
    public static readonly TimerBackfillResult None = new(0, 0, 0, 0, []);

    public bool AnythingExecuted => Executed > 0;
}

/// <summary>
/// 任务定时触发服务（M2-D）。语义按旧版逐条复刻，
/// 并按 M2 方案 §5.4 做三处服务化增强。
/// </summary>
/// <remarks>
/// <para><b>照旧版复刻的部分</b></para>
/// <list type="bullet">
///   <item>1 秒节拍；<c>HH:mm:ss</c> <b>精确字符串匹配</b>（这正是清洗器必须补秒的原因）；</item>
///   <item><c>Daily</c> 不校验星期，<c>SpecificDays</c> 校验 <c>TimerDays</c>（0=周日）；</item>
///   <item>月度计划：<c>MonthDays</c> 优先，为空时回退 <c>Months</c>+<c>Days</c> 交叉匹配；</item>
///   <item><b>月度计划触发执行的是所属按钮的 <c>Actions</c></b>（计划只决定"何时"，不携带自己的动作）；</item>
///   <item>同一按钮的任务定时与月度计划在时间点重合时**各触发一次**（旧版行为，有意保留）。</item>
/// </list>
/// <para><b>服务化增强（§5.4）</b></para>
/// <list type="number">
///   <item>去重改为 <see cref="ITimerFireStore"/> 的唯一约束 → 重启不重复、多实例不重复；</item>
///   <item>停机漏触发默认<b>不补执行</b>，只在启动时扫描并告警；</item>
///   <item>时间源用 <see cref="BeijingTime.Now"/>（含 NTP 校正），无人值守服务器漂移不会造成漏触发。</item>
/// </list>
/// <para>
/// <b>为什么依赖"缓存快照"而不是每次 <c>GetAsync()</c></b>：
/// <c>GetAsync</c> 会先查一次存储（SQLite）再看缓存，1Hz 下白白多一次查询/秒。
/// <c>ControlSettingsService.CachedSettings</c> 的文档注释本身就点了"定时器每秒轮询"这个场景。
/// 缓存未就绪时本轮跳过并预热，不做任何假设。
/// </para>
/// <para>
/// ⚠️ <b>回补循环必须按整秒推进</b>（<see cref="TruncateToSecond"/>）。
/// <c>PeriodicTimer(1s)</c> 的实际节拍常常略小于 1 秒（实测 ~0.995s），
/// 若拿带亚秒的 <see cref="DateTimeOffset"/> 直接 <c>+1s</c>，<c>from+1s</c> 会永远大于 <c>now</c>，
/// 循环体一次都不执行 —— 表现为<b>定时器整体静默失效</b>：不报错、不写日志、
/// <c>timer_fires</c> 里连一行记录都没有。截断到整秒后，各拍区间
/// <c>(floor(from), floor(now)]</c> 首尾相接，并集恰好是 <c>(floor(起), floor(止)]</c>，
/// 每个整秒被求值且仅被求值一次。守门用例见
/// <c>TimerSchedulerServiceTests.Sub_second_tick_intervals_still_evaluate_every_second</c>。
/// </para>
/// <para>
/// <b>有意偏离（已记录）</b>：未新增 <c>TimerFired</c> SignalR 通道。
/// 触发信息已经通过「运维日志」（<c>LogAdded</c>）与「动作结果 + 收发日志」两条既有通道完整到达前端，
/// 单开一条通道要多一个 DTO + 改 <c>INotifier</c> 及其实现与替身，信息量却完全重复。
/// 若前端后续需要"定时触发"的独立提示条，再在 M2-E 一并补。
/// </para>
/// </remarks>
public sealed class TimerSchedulerService
{
    private readonly ControlSettingsService _settings;
    private readonly ActionExecutionService _execution;
    private readonly ITimerFireStore _fires;
    private readonly LogService _log;
    private readonly TimerSchedulerOptions _options;
    private readonly TimeProvider _time;

    /// <summary>上一次已检测到的时刻。用于 tick 迟到时回补，避免"tick 被跳过 = 触发点丢失"。</summary>
    private DateTimeOffset? _lastDetect;

    /// <summary>
    /// 小于该幅度的"时钟回退"视为亚秒抖动直接吸收。
    /// <para>
    /// NTP 周期重校几乎总会带来几十~几百毫秒的负修正，若一律告警 + 重新锚定，
    /// 就会变成每 30 分钟刷一条毫无意义的警告。锚点不动时区间仍会被后续 tick 覆盖，不会丢秒。
    /// </para>
    /// </summary>
    private static readonly TimeSpan RollbackTolerance = TimeSpan.FromSeconds(1);

    /// <summary>上一次执行保留期裁剪的日期，用于"每天只裁一次"。</summary>
    private DateOnly? _lastPruneDate;

    private long _firedCount;
    private long _missedCount;

    private volatile bool _isCoreBlocked;

    /// <summary>停摆状态是否已记过日志（只在状态切换时记一次，不刷屏）。</summary>
    private bool _blockedLogged;

    public TimerSchedulerService(
        ControlSettingsService settings,
        ActionExecutionService execution,
        ITimerFireStore fires,
        LogService log,
        TimerSchedulerOptions options,
        TimeProvider time)
    {
        _settings = settings;
        _execution = execution;
        _fires = fires;
        _log = log;
        _options = options;
        _time = time;
    }

    /// <summary>
    /// 当前北京时间（<b>含 NTP 校正</b>）。
    /// <para>
    /// 走 <see cref="TimeProvider"/> 而不是直接读 <see cref="BeijingTime"/>，
    /// 是为了让测试能注入可推进的假时钟 —— 定时逻辑不可能靠真实等待来测。
    /// 生产实现见 <see cref="CorrectedTimeProvider"/>。
    /// </para>
    /// </summary>
    private DateTimeOffset Now() => BeijingTime.ToBeijing(_time.GetUtcNow());

    /// <summary>累计实际触发次数（成功抢到幂等键的次数）。</summary>
    public long FiredCount => Interlocked.Read(ref _firedCount);

    /// <summary>累计被跳过的"过窗口漏触发"条数。</summary>
    public long MissedCount => Interlocked.Read(ref _missedCount);

    /// <summary>调度器的运行时参数（供 <c>GET /api/timers</c> 与 <c>GET /api/system/info</c> 回显）。</summary>
    public TimerSchedulerOptions Options => _options;

    /// <summary>
    /// 授权闸门（M3）：<c>true</c> 表示"核心能力已被停摆" —— 保持 1 秒节拍，
    /// 但既不检测也不触发；置回 <c>false</c> 后自动继续，<b>无需重启服务</b>。
    ///
    /// <para>
    /// 由宿主在启动时置初值、并在授权状态变化时更新。Core 不认识"授权"这个概念，
    /// 只认识"现在允不允许触发"这一个布尔量（铁律 #1：Core 不引用宿主设施）。
    /// 默认 <c>false</c> = 无闸门，行为与引入授权之前完全一致（既有单测不受影响）。
    /// </para>
    ///
    /// <para>
    /// 用可写属性而不是 <c>RunAsync</c> 的参数：授权状态在运行期会变
    /// （导入授权 / 试用到期），一次性传入的委托要么得捕获可变状态，
    /// 要么得用别的手段在运行中改 —— 一个属性反而把这件事说到底了，
    /// 也让"停摆时到底会不会触发"能在单测里精确构造。
    /// </para>
    /// </summary>
    public bool IsCoreBlocked
    {
        get => _isCoreBlocked;
        set => _isCoreBlocked = value;
    }

    // ═══════════════════════════════════════════════════════════
    // 主循环
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 跑调度循环，直到 <paramref name="stoppingToken"/> 取消。
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _log.Info("Timer", "定时调度已关闭（Server:Timer:Enabled=false）。");
            return;
        }

        _lastDetect = Now();

        // 补执行开着走补执行那条路（它内部会把"错过多少个"一并统计/告警）；
        // 关着走只读扫描。**不能两条都调** —— 那会把同一个点统计两遍，
        // 页面上的「累计跳过」看着像出了双倍故障。
        if (_options.BackfillMissed)
        {
            var backfill = await BackfillMissedAsync(_lastDetect.Value, stoppingToken).ConfigureAwait(false);
            if (backfill.Detected > 0 && backfill.Executed == 0)
            {
                _log.Warn("Timer", "补执行已开启，但本次一个都没补上（见上一条日志的跳过原因）。");
            }
        }
        else
        {
            await ScanMissedAsync(_lastDetect.Value, stoppingToken).ConfigureAwait(false);
        }

        var active = DescribeActiveTriggers();
        _log.Info("Timer", $"定时调度已启动：节拍 {_options.EffectiveTickInterval.TotalSeconds:0.##}s，当前生效触发点 {active.Count} 个。");

        if (IsCoreBlocked)
        {
            _blockedLogged = true;
            _log.Warn("Timer", "授权不可用：定时触发已停摆（授权恢复后自动继续，无需重启服务）。");
        }

        using var timer = new PeriodicTimer(_options.EffectiveTickInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 单次 tick 失败绝不允许打挂宿主；下一拍继续。
                _log.Error("Timer", "定时检测发生异常，已跳过本轮。", ex);
            }
        }

        _log.Info("Timer", $"定时调度已停止（累计触发 {FiredCount} 次）。");
    }

    /// <summary>执行一次检测（含迟到回补）。测试直接调用它，不必等真实时间。</summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        if (IsCoreBlocked)
        {
            // 授权闸门停摆：不检测、不触发。
            // ⚠️ 锚点必须前移到"此刻" —— 否则解除拦截后这一整段会被当成
            //    "进程内检测被阻塞"而集中回补（虽然仍受 CatchUpWindow 约束，
            //    但语义上"停摆期间"本就不该补触发）。
            _lastDetect = Now();

            if (!_blockedLogged)
            {
                _blockedLogged = true;
                _log.Warn("Timer", "授权不可用：定时触发已停摆（授权恢复后自动继续，无需重启服务）。");
            }

            return;
        }

        _blockedLogged = false;

        var settings = _settings.CachedSettings;

        if (settings is null)
        {
            // 配置还没被加载过：预热一次，本轮不检测。
            // 启动自检里已经调过 GetAsync，正常情况下这里不会走到。
            await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            _lastDetect = Now();
            return;
        }

        var now = Now();
        var from = _lastDetect ?? now.AddSeconds(-1);

        if (now < from)
        {
            if (from - now >= RollbackTolerance)
            {
                // 时钟被往回拨过（NTP 大跳变、手工改系统时间）。不补、不退，重新锚定。
                _log.Warn("Timer", $"检测时钟出现回退（{from:HH:mm:ss} → {now:HH:mm:ss}），已重新锚定，跳过本轮。");
                _lastDetect = now;
            }

            // 亚秒级回退：锚点不动、本轮直接返回。下一拍 now 会重新超过 from，
            // 届时区间 (from, now] 仍然覆盖这段，不会丢秒。
            return;
        }

        var catchUpLimit = now - _options.EffectiveCatchUpWindow;
        if (from < catchUpLimit)
        {
            var skipped = (catchUpLimit - from).TotalSeconds;
            Interlocked.Increment(ref _missedCount);
            _log.Warn("Timer",
                $"进程内检测被阻塞 {(now - from).TotalSeconds:0.#}s，超出追赶窗口 " +
                $"{_options.EffectiveCatchUpWindow.TotalSeconds:0.#}s —— 其中最早的 {skipped:0.#}s 内的触发点**不予补执行**，已跳过。");
            from = catchUpLimit;
        }

        // ⚠️ 回补必须按**整秒**推进，不能拿带亚秒的 DateTimeOffset 直接 +1s：
        //    PeriodicTimer 的实际节拍常常略小于 1 秒（实测 ~0.995s），
        //    此时 from+1s 永远大于 now，循环体一次都不执行 —— 定时器**整体静默失效**，
        //    且不报错、不写日志、timer_fires 里一行记录都没有。
        //    截断到整秒后，"每个整秒恰好被求值一次"才有保证：
        //    各拍区间 (floor(from), floor(now)] 首尾相接，并集正好是 (floor(起), floor(止)]。
        var fromSecond = TruncateToSecond(from);
        var nowSecond = TruncateToSecond(now);

        for (var t = fromSecond.AddSeconds(1); t <= nowSecond; t = t.AddSeconds(1))
        {
            await DetectAndFireAsync(settings, t, cancellationToken).ConfigureAwait(false);
        }

        _lastDetect = now;

        await PruneIfDayChangedAsync(now, cancellationToken).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════
    // 检测
    // ═══════════════════════════════════════════════════════════

    private async Task DetectAndFireAsync(
        ControlSettings settings,
        DateTimeOffset moment,
        CancellationToken cancellationToken)
    {
        var time = moment.ToString("HH:mm:ss");
        var date = DateOnly.FromDateTime(moment.DateTime);
        var dateText = date.ToString("yyyy-MM-dd");
        var weekday = (int)moment.DayOfWeek; // 0=周日
        var month = moment.Month;
        var dayOfMonth = moment.Day;

        foreach (var button in settings.Buttons)
        {
            // ── 任务定时 ──
            if (button.TimerEnabled && ContainsTime(button.TimerTimes, time))
            {
                var dayMatches = !string.Equals(button.TimerMode, TimerModes.SpecificDays, StringComparison.Ordinal)
                                 || button.TimerDays.Contains(weekday);

                if (dayMatches)
                {
                    await TryFireAsync(
                        button,
                        refId: button.Id,
                        source: button.TimerMode,
                        reason: $"任务定时 {time}",
                        moment,
                        date,
                        dateText,
                        time,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            // ── 月度计划 ──
            foreach (var plan in button.MonthlyPlans)
            {
                if (!plan.Enabled || !ContainsTime(plan.Times, time))
                {
                    continue;
                }

                if (!PlanDayMatches(plan, month, dayOfMonth))
                {
                    continue;
                }

                await TryFireAsync(
                    button,
                    refId: $"{button.Id}:{plan.Id}",
                    source: MonthlyPlanSource,
                    reason: $"月度计划「{plan.Name}」{time}",
                    moment,
                    date,
                    dateText,
                    time,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private const string MonthlyPlanSource = "MonthlyPlan";

    /// <summary>
    /// 抢幂等键并执行。返回"是否真的执行了" —— 抢不到说明这个点已经跑过。
    /// </summary>
    /// <param name="moment">**原定触发时刻**。幂等键用的是它，所以补执行也必须传原定时刻。</param>
    /// <param name="firedAt">
    /// 实际执行时刻；<c>null</c> 表示"就是原定时刻"（正常路径）。
    /// 补执行时传"现在"，于是 <c>timer_fires</c> 里 <c>FiredAt ≠ FireTime</c>
    /// 就是"这条是补出来的"的判据 —— 不必为此加一列，也不会让历史数据对不上。
    /// </param>
    private async Task<bool> TryFireAsync(
        ButtonConfig button,
        string refId,
        string source,
        string reason,
        DateTimeOffset moment,
        DateOnly date,
        string dateText,
        string time,
        CancellationToken cancellationToken,
        DateTimeOffset? firedAt = null)
    {
        var entry = new TimerFireEntry(refId, dateText, time, source, firedAt ?? moment);

        // 抢到才执行。抢不到 = 这个触发点已经跑过了（上一拍 / 重启前 / 另一个实例）。
        var claimed = await _fires.TryClaimAsync(entry, cancellationToken).ConfigureAwait(false);

        if (!claimed)
        {
            return false;
        }

        Interlocked.Increment(ref _firedCount);
        _log.Info("Timer", $"⏰ {reason} —— 触发按钮「{button.Label}」的 {button.Actions.Count} 个动作。");

        // 月度计划执行的是**所属按钮的动作**（与旧版一致）。
        await _execution
            .ExecuteAsync(button.Actions, button.Label, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    private static bool ContainsTime(List<string>? times, string hhmmss)
        => times is not null && times.Contains(hhmmss, StringComparer.Ordinal);

    /// <summary>截断到整秒（保留原时区偏移）。触发点是按 <c>HH:mm:ss</c> 精确匹配的，亚秒没有意义。</summary>
    private static DateTimeOffset TruncateToSecond(DateTimeOffset value)
        => new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Offset);

    /// <summary>
    /// 月度计划的日期判定：<c>MonthDays</c> 优先；为空时回退旧字段 <c>Months</c>+<c>Days</c> 交叉匹配。
    /// <para>与旧版 <c>TimerService.OnTimerElapsed</c> 的判定顺序逐条一致。</para>
    /// </summary>
    private static bool PlanDayMatches(MonthlyPlan plan, int month, int dayOfMonth)
    {
        if (plan.MonthDays.Count > 0)
        {
            var schedule = plan.MonthDays.FirstOrDefault(md => md.Month == month);
            return schedule is not null && schedule.Days.Contains(dayOfMonth);
        }

        var monthMatch = plan.Months.Count == 0 || plan.Months.Contains(month);
        var dayMatch = plan.Days.Count == 0 || plan.Days.Contains(dayOfMonth);
        return monthMatch && dayMatch;
    }

    // ═══════════════════════════════════════════════════════════
    // 漏触发扫描 / 补执行
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 扫描出来的一个"应当触发、但 <c>timer_fires</c> 里没有记录"的点。
    /// <para>
    /// 带上 <see cref="Button"/> / <see cref="Source"/> / <see cref="Moment"/> 是给补执行用的：
    /// 「报告」与「补执行」共用同一份扫描，才不会出现两套算法各改一半的情况
    /// —— 那种漂移最终表现为"日志说漏了 3 个，实际补了 2 个"，最难查。
    /// </para>
    /// </summary>
    private sealed record MissedCandidate(
        string RefId,
        ButtonConfig Button,
        string Source,
        string PlanName,
        DateOnly Date,
        string DateText,
        string Time,
        DateTimeOffset Moment)
    {
        public string Describe()
            => PlanName.Length == 0
                ? $"按钮「{Button.Label}」{Time}"
                : $"按钮「{Button.Label}」计划「{PlanName}」{Time}";
    }

    /// <summary>
    /// 扫出"最近 <see cref="TimerSchedulerOptions.EffectiveMissedScanWindow"/> 内应当触发、
    /// 但 <c>timer_fires</c> 里没有记录"的点。纯读，不执行、不写日志。
    /// </summary>
    private async Task<List<MissedCandidate>> CollectMissedAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CachedSettings;
        if (settings is null)
        {
            return [];
        }

        var window = _options.EffectiveMissedScanWindow;
        if (window <= TimeSpan.Zero)
        {
            return [];
        }

        if (DescribeActiveTriggers().Count == 0)
        {
            return [];
        }

        var from = TruncateToSecond(now - window);
        var until = TruncateToSecond(now);
        var today = DateOnly.FromDateTime(now.DateTime);
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in await _fires.ListAsync(today, cancellationToken).ConfigureAwait(false))
        {
            claimed.Add(Key(entry.RefId, entry.FireTime));
        }

        var missed = new List<MissedCandidate>();

        // ⚠️ 上界是 `t <= until`，**不是** `<`。
        //    RunAsync 启动时把锚点取成 floor(now)，而 TickAsync 的第一拍只求值 floor(now)+1
        //    —— 也就是说「启动那一整秒」谁都不会求值。这一秒既不该漏报，也不该漏补；
        //    重复执行由幂等键（TryClaimAsync）兜住，所以放宽上界没有副作用。
        for (var t = from; t <= until; t = t.AddSeconds(1))
        {
            var time = t.ToString("HH:mm:ss");
            var date = DateOnly.FromDateTime(t.DateTime);
            var dateText = date.ToString("yyyy-MM-dd");
            var weekday = (int)t.DayOfWeek;
            var month = t.Month;
            var dayOfMonth = t.Day;

            foreach (var button in settings.Buttons)
            {
                if (button.TimerEnabled && ContainsTime(button.TimerTimes, time))
                {
                    var dayMatches = !string.Equals(button.TimerMode, TimerModes.SpecificDays, StringComparison.Ordinal)
                                     || button.TimerDays.Contains(weekday);

                    if (dayMatches && !claimed.Contains(Key(button.Id, time)))
                    {
                        missed.Add(new MissedCandidate(
                            button.Id, button, button.TimerMode, string.Empty,
                            date, dateText, time, t));
                    }
                }

                foreach (var plan in button.MonthlyPlans)
                {
                    if (!plan.Enabled || !ContainsTime(plan.Times, time) || !PlanDayMatches(plan, month, dayOfMonth))
                    {
                        continue;
                    }

                    var refId = $"{button.Id}:{plan.Id}";
                    if (!claimed.Contains(Key(refId, time)))
                    {
                        missed.Add(new MissedCandidate(
                            refId, button, MonthlyPlanSource, plan.Name,
                            date, dateText, time, t));
                    }
                }
            }
        }

        return missed;
    }

    /// <summary>
    /// 扫描"最近 <see cref="TimerSchedulerOptions.EffectiveMissedScanWindow"/> 内应当触发、
    /// 但 <c>timer_fires</c> 里没有记录"的触发点，并写一条汇总告警。
    /// <para><b>只统计与告警，绝不执行</b> —— 这是 <see cref="TimerSchedulerOptions.BackfillMissed"/>
    /// 关闭时的路径（默认值，M2 方案 §12 决策 #3）。</para>
    /// </summary>
    /// <param name="now">扫描的结束时刻（含）。</param>
    /// <returns>错过的触发点个数。0 也表示"没发现"。</returns>
    /// <remarks>
    /// 公开出来有两个用途：① 启动时由 <see cref="RunAsync"/> 调用（补执行关闭时）；
    /// ② 运维想"现在扫一下"时可由 API 直接触发 —— 这是个纯只读操作，暴露没有副作用。
    /// </remarks>
    public async Task<int> ScanMissedAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var window = _options.EffectiveMissedScanWindow;
        var missed = await CollectMissedAsync(now, cancellationToken).ConfigureAwait(false);

        if (missed.Count == 0)
        {
            if (window > TimeSpan.Zero)
            {
                _log.Info("Timer", $"漏触发扫描：最近 {window.TotalMinutes:0.#} 分钟内没有发现被错过的触发点。");
            }

            return 0;
        }

        Interlocked.Add(ref _missedCount, missed.Count);

        var shown = string.Join("；", missed.Take(10).Select(m => m.Describe()));
        var more = missed.Count > 10 ? $"…（共 {missed.Count} 个）" : string.Empty;

        // 补执行开着的时候措辞必须跟着变：这时"漏掉的"马上就会被补上，
        // 再写"按策略不补执行"会让读日志的人以为这些点就这么没了。
        _log.Warn("Timer", _options.BackfillMissed
            ? $"服务停机期间错过 {missed.Count} 个定时触发点：{shown}{more}。已开启补执行，稍后按策略补。"
            : $"服务停机期间错过 {missed.Count} 个定时触发点，按策略**不补执行**：{shown}{more}。" +
              "如需补执行，把 Server:Timer:BackfillMissed 置 true（窗口内每个触发点最多补一次）。");

        return missed.Count;
    }

    /// <summary>
    /// 补执行"服务停机期间错过"的触发点。仅在
    /// <see cref="TimerSchedulerOptions.BackfillMissed"/> 打开时真的执行，否则原样返回空结果。
    /// </summary>
    /// <param name="now">补执行发生的时刻（= 实际执行时间，写进 <c>timer_fires.FiredAt</c>）。</param>
    /// <remarks>
    /// <para><b>四条语义，缺一条都会造成现场事故：</b></para>
    /// <list type="number">
    ///   <item>
    ///     <b>每个触发点最多补一次，取窗口内最近的那一次。</b>
    ///     补的是「最终状态」而不是「每一次动作」：停机 10 分钟、某按钮每分钟一个点，
    ///     补 10 次等于把继电器连按 10 下。而"最近那一次"通常正是现场希望的那个结果。
    ///   </item>
    ///   <item>
    ///     <b>总数受 <see cref="TimerSchedulerOptions.MaxBackfillCount"/> 限制。</b>
    ///     超出只报告 —— 撞到上限本身说明配置不是"闹钟式"而是"轮询式"，
    ///     那种场景下补执行没有任何意义。
    ///   </item>
    ///   <item>
    ///     <b>只补窗口内的（默认 10 分钟）。</b>
    ///     停机三小时后再补"22:00 关屏"，会把刚开好的屏关掉。
    ///     窗口外的一律只告警 —— 这是"补执行"和"重放历史"的分界线。
    ///   </item>
    ///   <item>
    ///     <b>幂等键用「原定时刻」，<c>FiredAt</c> 用「现在」。</b>
    ///     这样同一秒以后再扫不会补第二遍，而 <c>timer_fires</c> 里
    ///     <c>FiredAt ≠ FireTime</c> 直接就是"这条是补的"的判据，不需要新列。
    ///   </item>
    /// </list>
    /// </remarks>
    public async Task<TimerBackfillResult> BackfillMissedAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!_options.BackfillMissed)
        {
            return TimerBackfillResult.None;
        }

        var missed = await CollectMissedAsync(now, cancellationToken).ConfigureAwait(false);
        if (missed.Count == 0)
        {
            return TimerBackfillResult.None;
        }

        Interlocked.Add(ref _missedCount, missed.Count);

        // ① 每个触发点只留最近一次
        var latest = missed
            .GroupBy(m => m.RefId, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(m => m.Moment, Comparer<DateTimeOffset>.Default).First())
            .OrderBy(m => m.Moment)
            .ToList();

        var collapsed = missed.Count - latest.Count;

        // ② 总上限
        var cap = _options.EffectiveMaxBackfillCount;
        var runnable = latest.Count > cap ? latest.Take(cap).ToList() : latest;
        var overCap = latest.Count - runnable.Count;

        var nowSecond = TruncateToSecond(now);
        var executed = new List<string>();

        foreach (var candidate in runnable)
        {
            var lateSeconds = (nowSecond - candidate.Moment).TotalSeconds;

            var fired = await TryFireAsync(
                candidate.Button,
                refId: candidate.RefId,
                source: candidate.Source,
                reason: $"补执行 {candidate.Describe()}（原定 {candidate.Time}，" +
                        $"服务停机错过，晚了 {lateSeconds:0}s）",
                moment: candidate.Moment,
                date: candidate.Date,
                dateText: candidate.DateText,
                time: candidate.Time,
                cancellationToken: cancellationToken,
                firedAt: nowSecond).ConfigureAwait(false);

            if (fired)
            {
                executed.Add(candidate.Describe());
            }
        }

        var summary = executed.Count > 0
            ? $"补执行 {executed.Count} 个：{string.Join("；", executed.Take(10))}"
            : "一个都没补（幂等键被别人抢走）";

        _log.Warn("Timer",
            $"服务停机期间错过 {missed.Count} 个定时触发点 —— {summary}。" +
            $"另有 {collapsed} 个因「同一触发点只补最近一次」跳过" +
            (overCap > 0 ? $"，{overCap} 个因超出补执行上限（{cap}）跳过" : string.Empty) +
            "。窗口外的不补（如需覆盖更长时间，调整 Server:Timer:MissedScanWindowSeconds）。");

        return new TimerBackfillResult(
            Detected: missed.Count,
            Executed: executed.Count,
            CollapsedDuplicates: collapsed,
            OverCap: overCap,
            ExecutedLabels: executed);
    }

    private static string Key(string refId, string fireTime) => $"{refId}|{fireTime}";

    // ═══════════════════════════════════════════════════════════
    // 保留期裁剪
    // ═══════════════════════════════════════════════════════════

    private async Task PruneIfDayChangedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(now.DateTime);

        if (_lastPruneDate == today)
        {
            return;
        }

        _lastPruneDate = today;

        try
        {
            var keepFrom = today.AddDays(-_options.RetentionDays);
            var deleted = await _fires.PruneAsync(keepFrom, cancellationToken).ConfigureAwait(false);

            if (deleted > 0)
            {
                _log.Info("Timer", $"触发记录已裁剪：删除 {keepFrom:yyyy-MM-dd} 之前的 {deleted} 条（保留 {_options.RetentionDays} 天）。");
            }
        }
        catch (Exception ex)
        {
            // 裁剪失败只影响表体积，绝不能影响触发。
            _log.Warn("Timer", $"触发记录裁剪失败（不影响定时功能）：{ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 生效触发点清单（供 /api/timers 与启动自检）
    // ═══════════════════════════════════════════════════════════

    /// <summary>列出当前配置里**所有**生效的定时触发点（不受当前时刻限制）。</summary>
    public IReadOnlyList<TimerTriggerInfo> DescribeActiveTriggers()
    {
        var settings = _settings.CachedSettings;
        if (settings is null)
        {
            return Array.Empty<TimerTriggerInfo>();
        }

        var list = new List<TimerTriggerInfo>();

        foreach (var button in settings.Buttons)
        {
            if (button.TimerEnabled)
            {
                foreach (var time in button.TimerTimes)
                {
                    list.Add(new TimerTriggerInfo(
                        RefId: button.Id,
                        ButtonId: button.Id,
                        ButtonLabel: button.Label,
                        Kind: button.TimerMode,
                        Time: time,
                        Days: button.TimerMode == TimerModes.SpecificDays ? button.TimerDays : Array.Empty<int>(),
                        MonthDays: Array.Empty<string>()));
                }
            }

            foreach (var plan in button.MonthlyPlans)
            {
                if (!plan.Enabled)
                {
                    continue;
                }

                var monthDays = plan.MonthDays
                    .SelectMany(md => md.Days.Select(d => $"{md.Month}月{d}日"))
                    .ToList();

                if (monthDays.Count == 0 && plan.Months.Count > 0 && plan.Days.Count > 0)
                {
                    monthDays = plan.Months
                        .SelectMany(m => plan.Days.Select(d => $"{m}月{d}日"))
                        .ToList();
                }

                foreach (var time in plan.Times)
                {
                    list.Add(new TimerTriggerInfo(
                        RefId: $"{button.Id}:{plan.Id}",
                        ButtonId: button.Id,
                        ButtonLabel: button.Label,
                        Kind: MonthlyPlanSource,
                        Time: time,
                        Days: Array.Empty<int>(),
                        MonthDays: monthDays));
                }
            }
        }

        return list;
    }

    /// <summary>
    /// 判断某个触发点"今天是否适用"。
    /// <para>
    /// <b>与调度器共用同一份判定逻辑</b>（<see cref="DetectAndFireAsync"/> 里的两处 dayMatches / <see cref="PlanDayMatches"/>）——
    /// 前端若显示"今天会跑"而实际不跑，比不显示更糟。任何新规则都必须同时改这两处。
    /// </para>
    /// </summary>
    public bool FiresToday(TimerTriggerInfo trigger, DateTimeOffset now)
    {
        var settings = _settings.CachedSettings;
        if (settings is null)
        {
            return false;
        }

        var button = settings.Buttons.FirstOrDefault(b => string.Equals(b.Id, trigger.ButtonId, StringComparison.Ordinal));
        if (button is null)
        {
            return false;
        }

        if (string.Equals(trigger.Kind, MonthlyPlanSource, StringComparison.Ordinal))
        {
            var planId = trigger.RefId.Split(':').ElementAtOrDefault(1);
            var plan = button.MonthlyPlans.FirstOrDefault(p => string.Equals(p.Id, planId, StringComparison.Ordinal));

            return plan is not null && plan.Enabled && PlanDayMatches(plan, now.Month, now.Day);
        }

        if (!button.TimerEnabled)
        {
            return false;
        }

        // Daily 不校验星期（旧版行为）；SpecificDays 才看 TimerDays。
        return !string.Equals(trigger.Kind, TimerModes.SpecificDays, StringComparison.Ordinal)
               || button.TimerDays.Contains((int)now.DayOfWeek);
    }

    /// <summary>
    /// 下一个将要触发的时刻；没有生效触发点、或未来 7 天内都匹配不上时返回 <c>null</c>。
    /// <para>
    /// <b>为什么扫 7 天</b>：本月度计划可能只在"每月 31 日"触发，只看今天会误报"没有下一次"。
    /// 7 天足以覆盖"每周固定星期"这类最常见的稀疏规则；再长就没有展示价值了。
    /// </para>
    /// <para>
    /// 返回 <c>null</c> 是**诚实的"算不出来"**，而不是"没有下一次" —— 前端应显示"—"而不是"永不触发"。
    /// </para>
    /// </summary>
    public DateTimeOffset? FindNextFireAt()
    {
        var settings = _settings.CachedSettings;
        if (settings is null)
        {
            return null;
        }

        var now = Now();
        DateTimeOffset? best = null;

        for (var offset = 0; offset <= 7; offset++)
        {
            var day = now.Date.AddDays(offset);
            var weekday = (int)day.DayOfWeek;

            foreach (var button in settings.Buttons)
            {
                if (button.TimerEnabled)
                {
                    var dayMatches = !string.Equals(button.TimerMode, TimerModes.SpecificDays, StringComparison.Ordinal)
                                     || button.TimerDays.Contains(weekday);

                    if (dayMatches)
                    {
                        foreach (var text in button.TimerTimes)
                        {
                            ConsiderCandidate(text, day, now, ref best);
                        }
                    }
                }

                foreach (var plan in button.MonthlyPlans)
                {
                    if (!plan.Enabled || !PlanDayMatches(plan, day.Month, day.Day))
                    {
                        continue;
                    }

                    foreach (var text in plan.Times)
                    {
                        ConsiderCandidate(text, day, now, ref best);
                    }
                }
            }

            // 今天内已经找到就不用往后扫了（同一天的时刻必然更早）。
            if (best is not null)
            {
                return best;
            }
        }

        return best;
    }

    /// <summary>把 <c>HH:mm:ss</c> + 日期拼成一个候选时刻，只保留"晚于 now 且早于已知最优"的那个。</summary>
    private static void ConsiderCandidate(string hhmmss, DateTime day, DateTimeOffset now, ref DateTimeOffset? best)
    {
        if (!TimeOnly.TryParseExact(hhmmss, "HH:mm:ss", out var timeOfDay))
        {
            return;
        }

        var candidate = new DateTimeOffset(
            day.Year, day.Month, day.Day, timeOfDay.Hour, timeOfDay.Minute, timeOfDay.Second, now.Offset);

        if (candidate <= now)
        {
            return;
        }

        if (best is null || candidate < best.Value)
        {
            best = candidate;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 手动触发（调试 / 运维）
    // ═══════════════════════════════════════════════════════════

    /// <summary>手动触发的 <c>timer_fires.Source</c> 取值，便于与定时触发区分。</summary>
    public const string ManualSource = "Manual";

    /// <summary>
    /// 立刻触发一次指定触发点（调试 / 运维用）。
    /// <para>
    /// <b>仍然走 <c>timer_fires</c> 同一道幂等闸门</b>：幂等键是
    /// <c>(refId, 今天, 当前 HH:mm:ss)</c>，因此**同一秒内连点两次只有第一次生效** ——
    /// 这正是"走同一闸门"的意义，而不是绕开它直接执行。
    /// </para>
    /// <para>
    /// <paramref name="refId"/> 形如 <c>&lt;buttonId&gt;</c>（任务定时）或
    /// <c>&lt;buttonId&gt;:&lt;planId&gt;</c>（月度计划），与 <see cref="DescribeActiveTriggers"/> 输出的口径一致。
    /// </para>
    /// </summary>
    public async Task<TimerManualFireOutcome> FireNowAsync(
        string refId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refId);

        var settings = _settings.CachedSettings
                       ?? (await _settings.GetAsync(cancellationToken).ConfigureAwait(false)).Settings;

        var now = Now();
        var time = now.ToString("HH:mm:ss");
        var date = DateOnly.FromDateTime(now.DateTime);
        var dateText = date.ToString("yyyy-MM-dd");

        var buttonId = refId.Split(':')[0];
        var button = settings.Buttons.FirstOrDefault(b => string.Equals(b.Id, buttonId, StringComparison.Ordinal));

        if (button is null)
        {
            return TimerManualFireOutcome.NotFound(refId, dateText, time, $"按钮 {buttonId} 不存在。");
        }

        // 带计划 id 时要连计划一起验，否则「随手编一个 planId」也能触发按钮动作。
        if (refId.Length > buttonId.Length + 1)
        {
            var planId = refId[(buttonId.Length + 1)..];
            if (!button.MonthlyPlans.Any(p => string.Equals(p.Id, planId, StringComparison.Ordinal)))
            {
                return TimerManualFireOutcome.NotFound(refId, dateText, time, $"按钮「{button.Label}」下没有月度计划 {planId}。");
            }
        }

        var claimed = await _fires
            .TryClaimAsync(new TimerFireEntry(refId, dateText, time, ManualSource, now), cancellationToken)
            .ConfigureAwait(false);

        if (!claimed)
        {
            return TimerManualFireOutcome.AlreadyFired(
                refId, button.Label, dateText, time,
                $"{time} 这一秒已经触发过「{refId}」，本次未重复执行。");
        }

        Interlocked.Increment(ref _firedCount);
        _log.Info("Timer", $"🔧 手动触发 {refId} —— 执行按钮「{button.Label}」的 {button.Actions.Count} 个动作。");

        var execution = await _execution
            .ExecuteAsync(button.Actions, button.Label, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return TimerManualFireOutcome.Fired(refId, button.Label, dateText, time, execution);
    }
}
