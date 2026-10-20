namespace ControlServer.Core.Models;

/// <summary>
/// 单条发送动作（一个按钮可包含多个动作）。
/// <para>
/// **持久化契约**：字段名与默认值不得随意变更 ——
/// 旧配置（<c>control_config.json</c>）与历史备份按此形状解析，改名会让导入后行为静默漂移。
/// 所有属性可写（编辑-保存 + 旧格式迁移需要）。
/// </para>
/// </summary>
public sealed class ButtonAction
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>动作名称（用户自定义，仅用于识别与日志展示）。</summary>
    public string Name { get; set; } = "";

    /// <summary>目标 IP（WoL 模式下可填广播地址）。</summary>
    public string TargetIp { get; set; } = "192.168.1.100";

    /// <summary>目标端口。</summary>
    public int TargetPort { get; set; } = 8080;

    /// <summary>通信协议：UDP / TCP / WoL。取值见 <see cref="TransportProtocols"/>。</summary>
    public string Protocol { get; set; } = TransportProtocols.Udp;

    /// <summary>数据类型：String / Hex / ANSI / ASCII / UTF-8 / 指令库。取值见 <see cref="DataTypes"/>。</summary>
    public string DataType { get; set; } = ControlServer.Core.Models.DataTypes.String;

    /// <summary>发送数据（WoL 时填 MAC；指令库时填**指令名**）。</summary>
    public string DataContent { get; set; } = "Hello";

    /// <summary>
    /// 前置延迟（毫秒）：本动作在其「前一个动作」完成后，再等待该毫秒数才发送。
    /// <b>第 1 条动作的 DelayMs 不生效</b>（立即发送）。
    /// </summary>
    public int DelayMs { get; set; } = 0;

    /// <summary>
    /// 动作级对接码：命中后**仅执行本条动作**（不触发同按钮的其它动作）。为空表示不参与匹配。
    /// 子按钮内的动作被此码触发时，目标 IP/端口仍由所属子按钮集统一提供。
    /// </summary>
    public string PairingCode { get; set; } = "";

    /// <summary>动作级对接码类型：String / UTF-8 / Hex。</summary>
    public string PairingCodeType { get; set; } = PairingCodeTypes.String;
}
