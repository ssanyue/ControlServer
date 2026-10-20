namespace ControlServer.Core.Models;

/// <summary>单月的日期计划：指定月份内的哪些天执行。</summary>
public sealed class MonthDaySchedule
{
    /// <summary>月份 1-12。</summary>
    public int Month { get; set; }

    /// <summary>该月内选中的日期列表（1-31）。</summary>
    public List<int> Days { get; set; } = new();
}

/// <summary>
/// 月度定时计划：每月可独立设置执行日期，支持多个执行时间点。
/// 与旧版字段同名同默认值。
/// </summary>
public sealed class MonthlyPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>计划名称。</summary>
    public string Name { get; set; } = "计划";

    /// <summary>是否启用此计划（每个计划独立开关）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// <b>旧字段</b>，仅用于兼容反序列化（迁移到 <see cref="Times"/>）。
    /// <para>⚠️ <b>默认刻意留空</b>，与 <see cref="ButtonConfig.TimerTime"/> 同理：只有留空才能区分"字段存在"与"字段缺失"。</para>
    /// </summary>
    public string Time { get; set; } = "";

    /// <summary>
    /// 多执行时间点，格式 **<c>HH:mm:ss</c>**（清洗器负责补秒）。
    /// <para>⚠️ <b>默认刻意留空</b>，理由与 <see cref="ButtonConfig.TimerTimes"/> 完全相同（见 M2 方案 §10.7）。</para>
    /// <para>⚠️ <b>格式必须是 <c>HH:mm:ss</c></b>，同 <see cref="ButtonConfig.TimerTimes"/>：<c>HH:mm</c> 会导致月度计划**静默不触发**。</para>
    /// </summary>
    public List<string> Times { get; set; } = new();

    /// <summary><b>旧字段</b>：选中的月份列表（1-12），仅用于兼容迁移。</summary>
    public List<int> Months { get; set; } = new();

    /// <summary><b>旧字段</b>：选中的日期列表（1-31），仅用于兼容迁移。</summary>
    public List<int> Days { get; set; } = new();

    /// <summary>
    /// 每月独立日期计划（**现役字段**）。
    /// 例：<c>[{Month:1, Days:[1,15]}]</c> 表示 1 月 1 日、1 月 15 日执行。
    /// </summary>
    public List<MonthDaySchedule> MonthDays { get; set; } = new();
}
