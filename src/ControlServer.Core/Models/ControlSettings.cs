namespace ControlServer.Core.Models;

/// <summary>
/// 服务版全局配置。结构源自旧版 <c>AppSettings</c>，但**裁剪掉单机版专有字段**
/// （窗口尺寸 / 锁定窗口 / 关闭时最小化 / 开机自启注册表项 / 用户密码哈希）。
/// <para>
/// 裁剪理由见 M2 方案 §3.2；导入旧配置时这些字段会被**静默丢弃**，
/// 但导入接口会返回「被忽略字段清单」，不做无声吞掉。
/// </para>
/// <para>
/// 序列化一律 **camelCase**，与旧版 <c>control_config.json</c> 同构 —— 这是"旧配置近乎直读"的前提。
/// </para>
/// </summary>
public sealed class ControlSettings
{
    /// <summary>程序名称（默认「中控」）。</summary>
    public string AppName { get; set; } = "中控";

    /// <summary>背景图片路径。</summary>
    public string BackgroundImagePath { get; set; } = "";

    /// <summary>
    /// 对接码监听端口（默认 9999）。
    /// <para>
    /// ⚠️ M2-A 保留该字段是**为了旧配置导入/导出往返保真**；
    /// 运行期实际监听端口仍以 <c>Server:Listener:PairingPort</c>（ServerOptions）为准。
    /// 两者合并（热切换）待 M2 决策 #8 拍板后接入，此处不制造两个真相源。
    /// </para>
    /// </summary>
    public int ListenPort { get; set; } = 9999;

    /// <summary>默认按钮图片路径（所有按钮统一使用，单个按钮可覆盖）。</summary>
    public string DefaultButtonImage { get; set; } = "";

    /// <summary>按钮列表。</summary>
    public List<ButtonConfig> Buttons { get; set; } = new();

    /// <summary>子按钮集列表（主界面上与按钮同级的容器）。</summary>
    public List<SubButtonSet> SubButtonSets { get; set; } = new();

    /// <summary>
    /// 组合按钮列表（2026-10-15，需求⑤）——**主界面上的第三类瓦片**，与按钮 / 子按钮集同级。
    /// <para>
    /// ⚠️ 三者的 <c>Order</c> 共用同一条编号空间（清洗器会合并排序后重编号），
    /// 所以"界面上谁在前"只有一个真相源：`Order`。
    /// </para>
    /// <para>
    /// ⚠️ 反序列化旧配置时这个字段不存在 ⇒ 保持空列表（**不是 null**）：旧配置照样能导入，
    /// 只是没有组合按钮。
    /// </para>
    /// </summary>
    public List<ComboButton> ComboButtons { get; set; } = new();

    /// <summary>启用自由排布（编辑模式可拖拽定位）。</summary>
    public bool FreeLayoutEnabled { get; set; } = false;
}
