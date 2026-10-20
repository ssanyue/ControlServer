using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ControlServer.Server.Api;

/// <summary>
/// 定时调度端点（M2-E4）。
/// <para>
/// <c>GET /api/timers</c> 给前端定时页一份"当前生效触发点 + 今天跑了没有"的完整视图；
/// <c>POST /api/timers/{refId}/fire</c> 是调试 / 运维用的手动触发。
/// </para>
/// <para>
/// <b>手动触发不绕开幂等闸门</b>：它走的是与定时触发**同一张 <c>timer_fires</c> 表**、同一个
/// <c>(refId, 日期, 时刻)</c> 唯一约束，因此同一秒内连点只有第一次生效。
/// 这是有意的 —— 一条"调试通道"如果能绕过幂等键，它就成了生产事故的入口。
/// </para>
/// </summary>
[ApiController]
[Route("api/timers")]
[Authorize]
public sealed class TimersController : ControllerBase
{
    private readonly TimerSchedulerService _scheduler;
    private readonly ITimerFireStore _fires;

    public TimersController(TimerSchedulerService scheduler, ITimerFireStore fires)
    {
        _scheduler = scheduler;
        _fires = fires;
    }

    /// <summary>当前生效的定时触发点清单 + 调度器概况。</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var now = BeijingTime.Now;
        var today = DateOnly.FromDateTime(now.DateTime);
        var nowText = now.ToString("HH:mm:ss");

        // 今天的触发记录：key = "refId|HH:mm:ss" —— 同一个按钮同一天可能有多个时间点各触发一次。
        var fired = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var entry in await _fires.ListAsync(today, cancellationToken).ConfigureAwait(false))
        {
            fired[$"{entry.RefId}|{entry.FireTime}"] = entry.FiredAt;
        }

        var triggers = _scheduler
            .DescribeActiveTriggers()
            .Select(t =>
            {
                var appliesToday = _scheduler.FiresToday(t, now);
                var hasFired = fired.TryGetValue($"{t.RefId}|{t.Time}", out var firedAt);

                return new TimerTriggerDto(
                    t.RefId,
                    t.ButtonId,
                    t.ButtonLabel,
                    t.Kind,
                    t.Time,
                    t.Days,
                    t.MonthDays,
                    AppliesToday: appliesToday,
                    FiredToday: hasFired,
                    FiredAt: hasFired ? firedAt : null,
                    Elapsed: appliesToday && string.CompareOrdinal(t.Time, nowText) <= 0);
            })
            .OrderBy(t => t.Time, StringComparer.Ordinal)
            .ThenBy(t => t.ButtonLabel, StringComparer.Ordinal)
            .ToList();

        var options = _scheduler.Options;

        return Ok(new TimerOverviewDto(
            Enabled: options.Enabled,
            TickIntervalSeconds: options.EffectiveTickInterval.TotalSeconds,
            CatchUpWindowSeconds: options.EffectiveCatchUpWindow.TotalSeconds,
            MissedScanWindowSeconds: options.EffectiveMissedScanWindow.TotalSeconds,
            BackfillMissed: options.BackfillMissed,
            MaxBackfillCount: options.EffectiveMaxBackfillCount,
            RetentionDays: options.RetentionDays,
            FiredCount: _scheduler.FiredCount,
            MissedCount: _scheduler.MissedCount,
            ServerTime: now,
            NextFireAt: _scheduler.FindNextFireAt(),
            Triggers: triggers));
    }

    /// <summary>
    /// 立刻触发一次指定触发点（调试 / 运维）。
    /// <list type="bullet">
    ///   <item><c>Fired</c> → 200（响应体里带逐条动作结果）</item>
    ///   <item><c>NotFound</c> → 404</item>
    ///   <item><c>AlreadyFired</c> → 409（同一秒已经触发过，本次被幂等闸门挡下）</item>
    /// </list>
    /// </summary>
    /// <param name="refId">
    /// 触发点标识：任务定时是按钮 Id，月度计划是 <c>buttonId:planId</c>（形状同 <see cref="TimerTriggerDto.RefId"/>）。
    /// 含 <c>:</c> 时请 URL 编码成 <c>%3A</c>（前端 <c>encodeURIComponent</c> 已覆盖）。
    /// </param>
    [HttpPost("{refId}/fire")]
    [Authorize(Policy = Policies.CanOperate)]
    public async Task<IActionResult> Fire(string refId, CancellationToken cancellationToken)
    {
        var outcome = await _scheduler.FireNowAsync(refId, cancellationToken).ConfigureAwait(false);

        var dto = new TimerFireResultDto(
            outcome.Ok,
            outcome.Status.ToString(),
            outcome.RefId,
            outcome.ButtonLabel,
            outcome.Date,
            outcome.Time,
            outcome.Message,
            outcome.Execution is null ? null : ActionDtoMapper.ToDto(outcome.Execution));

        return outcome.Status switch
        {
            TimerManualFireStatus.Fired => Ok(dto),
            TimerManualFireStatus.NotFound => NotFound(dto),
            TimerManualFireStatus.AlreadyFired => Conflict(dto),
            _ => StatusCode(StatusCodes.Status500InternalServerError, dto),
        };
    }
}
