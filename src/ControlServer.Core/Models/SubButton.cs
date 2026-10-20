namespace ControlServer.Core.Models;

/// <summary>
/// 子按钮。与按钮的区别（旧版规则）：
/// <list type="number">
///   <item>不具备「任务定时」与「月度计划」；</item>
///   <item>动作**不含**网络唤醒（WoL），仅 UDP / TCP；</item>
///   <item>动作的目标 IP / 端口由所属「子按钮集」统一提供，本类不单独保存；</item>
///   <item>形状统一为圆形（仅界面区分）。</item>
/// </list>
/// </summary>
public sealed class SubButton
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>显示名称。</summary>
    public string Label { get; set; } = "子按钮";

    /// <summary>背景色。</summary>
    public string ButtonColor { get; set; } = "#00897B";

    /// <summary>文字色。</summary>
    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>名称字号，默认 12（与按钮的 13 不同）。</summary>
    public int NameFontSize { get; set; } = 12;

    /// <summary>背景图（为空则用颜色）。</summary>
    public string ButtonImage { get; set; } = "";

    /// <summary>圆形直径。</summary>
    public int Width { get; set; } = 90;

    /// <summary>圆形直径。</summary>
    public int Height { get; set; } = 90;

    /// <summary>对接码：收到即执行该子按钮的**全部动作**（IP/端口由所属集提供）。</summary>
    public string PairingCode { get; set; } = "";

    /// <summary>对接码类型。⚠️ 默认值是 <c>UTF-8</c>（与按钮的 <c>String</c> 不同）。</summary>
    public string PairingCodeType { get; set; } = PairingCodeTypes.Utf8;

    /// <summary>
    /// 动作列表（按顺序执行）。
    /// 其中 <c>TargetIp/TargetPort</c> 运行期一律被所属集覆盖，此处仅保留协议 / 数据 / 延迟等。
    /// </summary>
    public List<ButtonAction> Actions { get; set; } = new();
}

/// <summary>
/// 子按钮集：主界面上与按钮同级的容器，点击后弹出其内的子按钮。
/// 维护统一的发送目标 IP / 端口；自身不具备定时 / 月度计划 / WoL。
/// </summary>
public sealed class SubButtonSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>全局排序序号：与按钮**共用**。</summary>
    public int Order { get; set; } = 0;

    /// <summary>集合显示名称。</summary>
    public string Label { get; set; } = "子按钮集";

    /// <summary>集合瓦片背景色。</summary>
    public string ButtonColor { get; set; } = "#00695C";

    /// <summary>集合瓦片文字色。</summary>
    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>集合名称字号，默认 13。</summary>
    public int NameFontSize { get; set; } = 13;

    /// <summary>集合瓦片背景图（为空则用颜色）。</summary>
    public string ButtonImage { get; set; } = "";

    public int Width { get; set; } = 140;

    public int Height { get; set; } = 80;

    /// <summary>自由排布 X 坐标（0 = 流式布局）。</summary>
    public double LayoutX { get; set; } = 0;

    /// <summary>自由排布 Y 坐标（0 = 流式布局）。</summary>
    public double LayoutY { get; set; } = 0;

    /// <summary>集内所有子按钮动作**统一使用**的目标 IP。</summary>
    public string TargetIp { get; set; } = "192.168.1.100";

    /// <summary>集内所有子按钮动作**统一使用**的目标端口。</summary>
    public int TargetPort { get; set; } = 8080;

    /// <summary>集内子按钮列表。</summary>
    public List<SubButton> SubButtons { get; set; } = new();
}
