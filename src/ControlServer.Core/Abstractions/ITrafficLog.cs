using ControlServer.Core.Models;

namespace ControlServer.Core.Abstractions;

/// <summary>
/// <b>收发日志</b>端口（与运维日志 <see cref="LogEntry"/> 是两回事，见 M2 方案 §10.2）。
/// <para>
/// Core 只依赖此接口。<c>ActionExecutionService</c> / 对接码监听 / 定时器
/// 都通过它落地「谁在什么时候往哪发了什么、成功没有」。
/// </para>
/// <para>
/// M2-B 只要求「记下来 + 能被读到」；历史文件与前端推送在 M2-E / M2-F 补。
/// </para>
/// </summary>
public interface ITrafficLog
{
    /// <summary>写入一条收发日志。<b>实现方不得抛出</b> —— 日志失败绝不能影响业务发送结果。</summary>
    void Write(TrafficLogEntry entry);

    /// <summary>取最近 <paramref name="count"/> 条（时间正序），供「最近日志」面板使用。</summary>
    IReadOnlyList<TrafficLogEntry> Recent(int count = 200);

    /// <summary>当前缓冲条数。</summary>
    int Count { get; }
}
