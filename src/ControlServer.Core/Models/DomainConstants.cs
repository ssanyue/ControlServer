namespace ControlServer.Core.Models;

/// <summary>
/// 通信协议取值。与旧版 <c>ButtonAction.Protocol</c> 逐字对齐（含大小写 —— 旧配置里有 "WoL"）。
/// </summary>
public static class TransportProtocols
{
    public const string Udp = "UDP";
    public const string Tcp = "TCP";
    public const string Wol = "WoL";
}

/// <summary>
/// 动作的数据类型取值。**字符串必须与旧版一字不差**，否则旧配置导入后行为漂移。
/// <para>
/// ⚠️ 2026-10-08 起 <see cref="Utf8"/> 已并入 <see cref="String"/>（编码结果逐字节相同）：
/// 界面只提供 <see cref="String"/>，读取与写入都会把 <c>UTF-8</c> 归一为 <c>String</c>。
/// <b>注意别和 <see cref="PairingCodeTypes.Utf8"/> 搞混</b> —— 那是「对接码比较方式」，
/// 与「数据类型」是两套独立枚举，后者不受本次合并影响。
/// </para>
/// </summary>
public static class DataTypes
{
    /// <summary>
    /// 按 UTF-8 编码 —— **默认值，也是「文本类」的唯一取值**。
    /// </summary>
    public const string String = "String";

    /// <summary>十六进制字符串 → 原始字节。</summary>
    public const string Hex = "Hex";

    /// <summary>系统 ANSI 代码页（中文 Windows = GBK / CP936）。</summary>
    public const string Ansi = "ANSI";

    /// <summary>ASCII（&gt;127 的字符替换为 '?'）。</summary>
    public const string Ascii = "ASCII";

    /// <summary>
    /// UTF-8。<b>已退役为 <see cref="String"/> 的历史别名</b>（2026-10-08 合并）。
    /// <para>
    /// 编码结果与 <see cref="String"/> <b>逐字节相同</b>（<c>DataEncoder.Encode</c> 两个分支都走
    /// <c>Encoding.UTF8</c>），历史上并存只是旧版遗留的语义重复。读取时会被
    /// <c>ControlSettingsNormalizer</c> / <c>CommandLibraryService</c> 归一成 <see cref="String"/>。
    /// </para>
    /// <para>
    /// 常量保留只为两件事：旧配置经 <c>DataEncoder.IsUtf8</c> 解析，以及旧调用方提交 <c>"utf-8"</c>
    /// 时不报错。<b>不要再在界面选项或新增写入路径里使用它。</b>
    /// </para>
    /// </summary>
    public const string Utf8 = "UTF-8";

    /// <summary>数据内容为「指令库」中的指令名，运行时再解析为真实协议/类型/数据。</summary>
    public const string CommandLibrary = "指令库";
}

/// <summary>对接码比较方式。取值与旧版一致。</summary>
public static class PairingCodeTypes
{
    public const string String = "String";
    public const string Utf8 = "UTF-8";
    public const string Hex = "Hex";
}

/// <summary>任务定时的执行模式。</summary>
public static class TimerModes
{
    public const string Daily = "Daily";
    public const string SpecificDays = "SpecificDays";
}

/// <summary>收发日志方向。</summary>
public static class TrafficDirections
{
    public const string Send = "发送";
    public const string Receive = "接收";
}
