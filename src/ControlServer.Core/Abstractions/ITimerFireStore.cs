namespace ControlServer.Core.Abstractions;

/// <summary>
/// 一条定时触发记录。
/// </summary>
/// <param name="RefId">触发源：任务定时是 <c>buttonId</c>，月度计划是 <c>buttonId:planId</c>。</param>
/// <param name="FireDate">触发日期 <c>yyyy-MM-dd</c>。</param>
/// <param name="FireTime">触发时刻 <c>HH:mm:ss</c>。</param>
/// <param name="Source">来源分类：<c>Daily</c> / <c>SpecificDays</c> / <c>MonthlyPlan</c> / <c>Manual</c>。</param>
/// <param name="FiredAt">实际触发时刻（北京时间，含校正）。</param>
public sealed record TimerFireEntry(
    string RefId,
    string FireDate,
    string FireTime,
    string Source,
    DateTimeOffset FiredAt);

/// <summary>
/// 定时触发记录的持久化端口（M2 方案 §5.4 的 <c>timer_fires</c> 表）。
/// <para>
/// 核心只有一件事：<see cref="TryClaimAsync"/> —— 用唯一约束做「抢触发权」。
/// 抢到才执行，抢不到说明这个触发点已经跑过了（本进程更早的 tick、或另一个实例、或服务重启前）。
/// </para>
/// <para>
/// 这样就不需要"内存去重窗口"这类有状态设计：<b>幂等性由存储保证</b>，
/// 调度器可以随意重试、重启、多实例并存。
/// </para>
/// </summary>
public interface ITimerFireStore
{
    /// <summary>
    /// 抢占一个触发点。
    /// </summary>
    /// <returns>
    /// <c>true</c> = 本次抢到，调用方应当执行；<c>false</c> = 该触发点已存在，调用方必须跳过。
    /// </returns>
    /// <remarks>
    /// 实现**不得**把"其它数据库异常"也当成"已触发"返回 false ——
    /// 那会让定时器静默失效，正是本模块要防的失败模式。异常必须上抛。
    /// </remarks>
    Task<bool> TryClaimAsync(TimerFireEntry entry, CancellationToken cancellationToken = default);

    /// <summary>列出某一天的全部触发记录（供"漏触发扫描"与前端展示）。</summary>
    Task<IReadOnlyList<TimerFireEntry>> ListAsync(DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除 <paramref name="keepFrom"/>（含）之前的记录以控制表体积。
    /// </summary>
    /// <returns>删除条数。</returns>
    Task<int> PruneAsync(DateOnly keepFrom, CancellationToken cancellationToken = default);
}
