using ControlServer.Core.Services;

namespace ControlServer.Core.Models;

/// <summary>
/// <b>收发日志</b>（与「运维日志」<see cref="LogEntry"/> 是两回事，见 M2 方案 §10.2）。
/// 字段与旧版 LogRowItem 对齐，供前端「最近日志」面板与历史日志页使用。
/// </summary>
/// <param name="Time">北京时间。</param>
/// <param name="Direction">「发送」或「接收」，见 <see cref="TrafficDirections"/>。</param>
/// <param name="Protocol">UDP / TCP / WoL。</param>
/// <param name="TargetIp">对端 IP（接收时为来源 IP）。</param>
/// <param name="Port">端口。</param>
/// <param name="Data">数据内容（超过 200 字符会被截断）。</param>
/// <param name="Success">是否成功（接收方向恒为 true）。</param>
/// <param name="Message">附加说明（失败原因 / 命中的按钮名等）。</param>
/// <param name="ButtonName">触发的按钮 / 子按钮名称。</param>
/// <param name="ActionName">触发的动作名称。</param>
public sealed record TrafficLogEntry(
    DateTimeOffset Time,
    string Direction,
    string Protocol,
    string TargetIp,
    int Port,
    string Data,
    bool Success,
    string Message,
    string ButtonName,
    string ActionName)
{
    /// <summary>数据展示的最大长度；超出即截断（旧版规则）。</summary>
    public const int MaxDataLength = 200;

    /// <summary>按旧版规则生成 Data 字段：超长截断并加省略号。</summary>
    public static string TruncateData(string? data)
    {
        if (string.IsNullOrEmpty(data)) return string.Empty;
        return data.Length <= MaxDataLength ? data : data[..MaxDataLength] + "…";
    }

    /// <summary>
    /// 构造一条「发送」记录（自动填北京时间 + 截断数据）。
    /// 全项目唯一入口 —— 避免各处手拼构造参数时漏掉截断或填错方向。
    /// </summary>
    public static TrafficLogEntry Sent(
        string protocol,
        string targetIp,
        int port,
        string? data,
        bool success,
        string? message = null,
        string? buttonName = null,
        string? actionName = null)
        => new(
            BeijingTime.Now,
            TrafficDirections.Send,
            protocol ?? string.Empty,
            targetIp ?? string.Empty,
            port,
            TruncateData(data),
            success,
            message ?? string.Empty,
            buttonName ?? string.Empty,
            actionName ?? string.Empty);

    /// <summary>构造一条「接收」记录（接收方向恒为成功，与旧版一致）。</summary>
    public static TrafficLogEntry Received(
        string protocol,
        string sourceIp,
        int port,
        string? data,
        string? message = null,
        string? buttonName = null,
        string? actionName = null)
        => new(
            BeijingTime.Now,
            TrafficDirections.Receive,
            protocol ?? string.Empty,
            sourceIp ?? string.Empty,
            port,
            TruncateData(data),
            true,
            message ?? string.Empty,
            buttonName ?? string.Empty,
            actionName ?? string.Empty);
}
