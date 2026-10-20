namespace ControlServer.Core.Models;

/// <summary>
/// 指令库中的单条指令。独立保存（配置 key = <c>commands</c>），
/// **不参与配置文件的导入 / 导出**（与旧版 <c>commands.json</c> 的语义一致）。
/// </summary>
public sealed class CommandItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>指令名称（**唯一**，用于动作以「指令库」类型引用）。</summary>
    public string Name { get; set; } = "";

    /// <summary>发送协议：UDP / TCP（默认 UDP，**不含 WoL**）。</summary>
    public string Protocol { get; set; } = TransportProtocols.Udp;

    /// <summary>数据类型：String / Hex / ANSI / ASCII / UTF-8（**不含「指令库」**，避免递归）。</summary>
    public string DataType { get; set; } = DataTypes.String;

    /// <summary>发送数据内容。</summary>
    public string Data { get; set; } = "";
}
