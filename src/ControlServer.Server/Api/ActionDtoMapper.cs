using ControlServer.Contracts;
using ControlServer.Core.Services;

namespace ControlServer.Server.Api;

/// <summary>
/// <see cref="ActionExecutionResult"/> → <see cref="ActionExecutionDto"/> 的映射。
/// <para>
/// 抽成独立静态类是因为它现在有**两个消费者**：<c>POST /api/actions/execute</c> 与
/// <c>POST /api/timers/{refId}/fire</c>（手动触发）。放在任一控制器里都意味着
/// 另一个要去 <c>using</c> 一个控制器 —— 那才是真正难看的耦合。
/// </para>
/// </summary>
internal static class ActionDtoMapper
{
    public static ActionExecutionDto ToDto(ActionExecutionResult result)
        => new(
            result.RunId,
            result.ButtonName,
            result.StartedAt,
            (long)result.Elapsed.TotalMilliseconds,
            result.Ok,
            result.SuccessCount,
            result.FailureCount,
            result.Summary,
            result.Steps
                .Select(s => new ActionStepDto(
                    s.Index, s.ActionName, s.Protocol, s.TargetIp, s.TargetPort,
                    s.Ok, s.BytesSent, s.Message, s.DataPreview, s.ResolvedFromCommand))
                .ToList());

    /// <summary>组合按钮执行结果的映射（2026-10-15，需求⑤）。</summary>
    public static ComboExecutionDto ToDto(ComboExecutionResult result)
        => new(
            result.RunId,
            result.ComboLabel,
            result.StartedAt,
            (long)result.Elapsed.TotalMilliseconds,
            result.Ok,
            result.Segments.Count,
            result.SuccessCount,
            result.FailureCount,
            result.Summary,
            result.Segments
                .Select(s => new ComboSegmentDto(
                    s.ButtonId,
                    s.ButtonLabel,
                    s.DelayAfterMs,
                    s.Result is null ? null : ToDto(s.Result),
                    s.Skipped))
                .ToList(),
            result.SkippedButtonIds);
}
