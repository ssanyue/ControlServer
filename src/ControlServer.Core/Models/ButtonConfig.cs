namespace ControlServer.Core.Models;

/// <summary>
/// 按钮配置（多个发送动作 + 对接码 + 任务定时 + 月度计划）。
/// **持久化契约**：字段名与默认值不得随意变更（旧版 <c>control_config.json</c> 按此形状解析）。
/// </summary>
public sealed class ButtonConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>全局排序序号：**与子按钮集共用**，决定主界面显示顺序（两者同级、可互相穿插）。</summary>
    public int Order { get; set; } = 0;

    /// <summary>按钮显示名称。</summary>
    public string Label { get; set; } = "按钮";

    /// <summary>对接码：收到即执行该按钮的**全部动作**。</summary>
    public string PairingCode { get; set; } = "";

    /// <summary>按钮级对接码类型：String / UTF-8 / Hex。</summary>
    public string PairingCodeType { get; set; } = PairingCodeTypes.String;

    /// <summary>背景色。</summary>
    public string ButtonColor { get; set; } = "#512DA8";

    /// <summary>文字色。</summary>
    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>瓦片名称字号，默认 13。</summary>
    public int NameFontSize { get; set; } = 13;

    /// <summary>背景图（为空则用颜色 / 全局默认图）。</summary>
    public string ButtonImage { get; set; } = "";

    public int Width { get; set; } = 140;

    public int Height { get; set; } = 80;

    /// <summary>自由排布 X 坐标（0 = 流式布局）。</summary>
    public double LayoutX { get; set; } = 0;

    /// <summary>自由排布 Y 坐标（0 = 流式布局）。</summary>
    public double LayoutY { get; set; } = 0;

    /// <summary>动作列表（按顺序执行，每条可带前置延迟）。</summary>
    public List<ButtonAction> Actions { get; set; } = new();

    // ── 任务定时 ──

    /// <summary>是否启用任务定时。</summary>
    public bool TimerEnabled { get; set; } = false;

    /// <summary>执行模式：Daily / SpecificDays。</summary>
    public string TimerMode { get; set; } = TimerModes.Daily;

    /// <summary>
    /// <b>旧字段</b>，仅用于兼容反序列化旧配置（迁移到 <see cref="TimerTimes"/>）。
    /// <para>
    /// ⚠️ <b>默认刻意留空</b>（旧版这里是 <c>"08:00"</c>）。理由与 <see cref="TimerTimes"/> 同源：
    /// 只有默认留空，清洗器才分得清"JSON 里确实写了 <c>timerTime</c>"与"字段根本不存在"。
    /// 旧版那个非空默认值会让迁移分支对**每个**按钮都成立，从而产出"旧值已迁移"的**假说明**。
    /// </para>
    /// </summary>
    public string TimerTime { get; set; } = "";

    /// <summary>
    /// 多执行时间点，格式 **<c>HH:mm:ss</c>**（清洗器负责补秒，见下）。
    /// <para>
    /// ⚠️ <b>默认刻意留空</b>（旧版这里是 <c>["08:00"]</c>）。原因见 M2 方案 §10.7：
    /// 旧版把默认值写在属性初始化器里，导致"旧配置只有 <c>timerTime</c>、没有 <c>timerTimes</c>"时
    /// 初始化器已把集合填成 <c>["08:00"]</c>，<c>TimerTime → TimerTimes</c> 那条迁移**永远不会触发**
    /// —— 用户设的 07:45 被静默丢弃。服务版把默认值下移到清洗器，
    /// 使旧值真正被迁移过来。这是**有意的行为修正**，非疏漏。
    /// </para>
    /// <para>
    /// ⚠️ <b>格式必须是 <c>HH:mm:ss</c></b>：<c>TimerSchedulerService</c> 用
    /// <c>times.Contains(now.ToString("HH:mm:ss"))</c> 做精确字符串匹配，
    /// <c>"08:00"</c> 永远匹配不上当前时刻 <c>"08:00:00"</c> ——
    /// 定时器会**静默不触发**且无任何日志。清洗器统一补秒，不得依赖调用方。
    /// </para>
    /// </summary>
    public List<string> TimerTimes { get; set; } = new();

    /// <summary>执行的星期列表，0=周日 … 6=周六。TimerMode=SpecificDays 时生效。</summary>
    public List<int> TimerDays { get; set; } = new() { 1, 2, 3, 4, 5 };

    // ── 月度定时计划 ──

    /// <summary>月度计划列表（可多个，各自独立开关）。</summary>
    public List<MonthlyPlan> MonthlyPlans { get; set; } = new();
}
