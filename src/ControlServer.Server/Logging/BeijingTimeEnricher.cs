using Serilog.Core;
using Serilog.Events;
using ControlServer.Core.Services;

namespace ControlServer.Server.Logging;

/// <summary>
/// 把<b>校正后的北京时间</b>作为日志属性注入。
/// <para>
/// <b>为什么必须这么做</b>：Serilog 的 <c>{Timestamp}</c> 来自 <c>LogEvent.Timestamp</c>，
/// 那是 <c>DateTimeOffset.Now</c>（系统钟），不会跟随 <see cref="BeijingTime.ClockOffset"/>。
/// 而 M2 方案 §5.6 要求"日志时间戳走校正后的 BeijingTime" —— 只有把模板里的
/// <c>{Timestamp:…}</c> 换成 <c>{BeijingTime:…}</c>，文件内容才真正吃到校正。
/// </para>
/// <para>
/// ⚠️ Serilog 的 <c>RollingInterval.Day</c> 用的是 <c>Serilog.Sinks.File</c> 内部的
/// <c>Clock</c>（<b>internal</b>，6.0.0 的公开类型清单里没有它），无法注入，
/// 所以**滚动文件名的切分点仍是系统钟**。这一条在启动日志里有显式说明。
/// </para>
/// </summary>
public sealed class BeijingTimeEnricher : ILogEventEnricher
{
    /// <summary>模板里引用的属性名。</summary>
    public const string PropertyName = "BeijingTime";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty(PropertyName, BeijingTime.Now));
    }
}
