namespace ControlServer.Core.Models;

/// <summary>
/// <b>组合按钮</b>（2026-10-15，需求⑤）：把若干个**已有主按钮**串起来，按顺序依次执行。
///
/// <para>
/// <b>它是主界面上的第三类瓦片</b>（与 <see cref="ButtonConfig"/>「主按钮」、
/// <see cref="SubButtonSet"/>「子按钮集」同级），**共用同一条全局顺序字段 <see cref="Order"/>** ——
/// 所以三类瓦片可以在主界面上互相穿插，界面顺序就是用户配置的顺序。
/// </para>
///
/// <para>
/// <b>为什么成员是"引用主按钮的 Id"而不是"复制一份动作"</b>：
/// 复制会让同一个动作在两处各存一份，改了按钮却忘了改组合 —— 现场表现是
/// "我明明改了这个按钮，组合里还是旧行为"。引用则天然跟随：按钮改完，组合下次触发就是新的。
/// 代价是"按钮被删了"要处理（见 <see cref="ComboButtonStep"/> 与清洗器：悬空成员会被剔除并记一条说明）。
/// </para>
///
/// <para>
/// <b>成员只能选主按钮，不能选子按钮集</b>（用户明确要求）：子按钮集是一个"容器 + 子按钮"的二级结构，
/// 而子按钮的动作要靠**集合级**的 IP / 端口覆盖才有意义 —— 把这种带隐式上下文的成员塞进组合，
/// 会让"组合里到底按哪个 IP 发"变成要看两层配置才能回答的问题。
/// </para>
///
/// <para>
/// 🔸 <b>与主按钮的能力差</b>：组合按钮<b>不带动作</b>（它的动作来自成员）、
/// <b>不带定时器与月度计划</b>（那是主按钮的能力，组合要定时就给它引用的那个按钮加）。
/// 它<b>带对接码</b>（收到该码即执行整个组合）。
/// </para>
/// </summary>
public sealed class ComboButton
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>全局排序序号：**与主按钮 / 子按钮集共用**，决定主界面显示顺序（三者同级、可互相穿插）。</summary>
    public int Order { get; set; } = 0;

    /// <summary>显示名称。</summary>
    public string Label { get; set; } = "组合按钮";

    /// <summary>对接码：收到即执行**整个组合**（按成员顺序、带段间延迟）。</summary>
    public string PairingCode { get; set; } = "";

    /// <summary>对接码类型：String / UTF-8 / Hex。</summary>
    public string PairingCodeType { get; set; } = PairingCodeTypes.String;

    /// <summary>背景色。</summary>
    public string ButtonColor { get; set; } = "#512DA8";

    /// <summary>文字色。</summary>
    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>瓦片名称字号，默认 13（与主按钮一致）。</summary>
    public int NameFontSize { get; set; } = 13;

    /// <summary>背景图（为空则用颜色 / 全局默认图）。</summary>
    public string ButtonImage { get; set; } = "";

    public int Width { get; set; } = 140;

    public int Height { get; set; } = 80;

    /// <summary>自由排布 X 坐标（0 = 流式布局）。</summary>
    public double LayoutX { get; set; } = 0;

    /// <summary>自由排布 Y 坐标（0 = 流式布局）。</summary>
    public double LayoutY { get; set; } = 0;

    /// <summary>
    /// 成员列表，**按执行顺序排列**（顺序即执行顺序，前端可上移 / 下移）。
    /// </summary>
    public List<ComboButtonStep> Steps { get; set; } = new();
}

/// <summary>
/// 组合按钮里的**一步**：执行哪个主按钮 + 执行完之后等多久。
///
/// <para>
/// <b>延迟的语义（这一条最容易理解错，所以写在类型上）</b>：
/// <see cref="DelayMs"/> 是<b>"执行完本步之后、下一步之前"等待的毫秒数</b> ——
/// 也就是需求里说的"每两个组合按钮之间可以设置延迟"。
/// ⇒ <b>第一步的延迟永远是 0</b>（前面没有东西可等），
/// <b>最后一步的延迟也没有意义</b>（后面没有下一步）—— 清洗器会把最后一步的延迟强制归零，
/// 免得用户填了一个"看起来生效、实际永远不会等"的数字。
/// </para>
///
/// <para>
/// ⚠️ 它<b>不</b>与成员按钮自己那条动作的前置延迟（<see cref="ButtonAction.DelayMs"/>）相加：
/// 两者是**两个独立的概念**，各管各的 ——
/// 成员内部的延迟在"执行那个按钮"内部生效（第 1 条动作仍然不等待），
/// 组合的段间延迟在"上一个成员整个执行完"之后才开始算。
/// </para>
/// </summary>
public sealed class ComboButtonStep
{
    /// <summary>成员按钮的 <see cref="ButtonConfig.Id"/>（**引用**，不是副本）。</summary>
    public string ButtonId { get; set; } = "";

    /// <summary>本步执行完之后、下一步之前等待的毫秒数（0..<see cref="MaxDelayMs"/>）。</summary>
    public int DelayMs { get; set; } = 0;

    /// <summary>
    /// 单段延迟上限：60 秒。
    /// <para>
    /// 为什么要封顶：组合是"按钮点击 / 对接码"这条**有人在场**的链路上跑的，
    /// 一个手滑写成 <c>600000</c>（10 分钟）会让触发它的那个人以为"点了没反应"，
    /// 而实际上命令正排着队。要等那么久，应该用定时器，不该用组合。
    /// </para>
    /// </summary>
    public const int MaxDelayMs = 60_000;
}
