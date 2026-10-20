namespace ControlServer.Infrastructure.Persistence.Entities;

/// <summary>
/// 定时触发记录（M2-D）。对应 M2 方案 §5.4「服务化必须补的三件事」的第 1、2 条。
/// <para>
/// 旧版把去重状态放在进程内存里（<c>Dictionary</c> + 2 秒窗口）—— 进程常驻时够用。
/// 服务版会重启 / 升级，内存状态一丢就会**重复触发**；而且多实例并存时内存状态根本不共享。
/// 因此这里持久化，并用复合主键把「同一触发点只执行一次」变成**数据库层面的保证**，
/// 而不是靠代码里的判断。
/// </para>
/// </summary>
public sealed class TimerFireRecord
{
    /// <summary>
    /// 触发源标识：任务定时是 <c>buttonId</c>；月度计划是 <c>buttonId:planId</c>。
    /// <para>月度计划必须带 <c>planId</c> —— 同一按钮的多个计划是彼此独立的触发源。</para>
    /// </summary>
    public string RefId { get; set; } = string.Empty;

    /// <summary>触发日期，<c>yyyy-MM-dd</c>（按触发时采用的时间源）。</summary>
    public string FireDate { get; set; } = string.Empty;

    /// <summary>触发时刻，<c>HH:mm:ss</c>。</summary>
    public string FireTime { get; set; } = string.Empty;

    /// <summary>实际触发时刻（北京时间，含 NTP 校正）。</summary>
    public DateTimeOffset FiredAt { get; set; }

    /// <summary>触发来源分类：<c>Daily</c> / <c>SpecificDays</c> / <c>MonthlyPlan</c> / <c>Manual</c>。</summary>
    public string Source { get; set; } = string.Empty;
}
