using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// 组合按钮里**可执行的一段**：成员按钮 + 执行完之后等多久。
/// </summary>
/// <param name="Button">成员按钮（一定是配置里真实存在的那一个）。</param>
/// <param name="DelayAfterMs">执行完这一段之后、下一段之前等待的毫秒数（最后一段恒为 0）。</param>
public sealed record ComboSegment(ButtonConfig Button, int DelayAfterMs);

/// <summary>
/// 一次组合解析的结果。
/// </summary>
/// <param name="ComboLabel">组合按钮的显示名。</param>
/// <param name="Segments">按执行顺序排好的段（悬空成员已被剔除）。</param>
/// <param name="SkippedButtonIds">被剔除的成员 Id（对应主按钮已不存在）。</param>
public sealed record ComboPlan(
    string ComboLabel,
    IReadOnlyList<ComboSegment> Segments,
    IReadOnlyList<string> SkippedButtonIds)
{
    /// <summary>有没有可执行的段。</summary>
    public bool IsExecutable => Segments.Count > 0;

    /// <summary>整轮组合要等待的延迟合计（毫秒）。用于日志与"要等多久"的展示。</summary>
    public int TotalDelayMs => Segments.Sum(static s => s.DelayAfterMs);
}

/// <summary>
/// 把「组合按钮」解析成「一串可执行的段」（2026-10-15，需求⑤）。
///
/// <para>
/// <b>为什么单独抽一个纯函数</b>：组合的成员是<b>按 Id 引用</b>主按钮的，
/// 于是"引用还在不在"这件事必须在**执行之前**结算清楚 —— 而结算规则属于领域知识
/// （谁被剔除、剔除之后延迟怎么算），不该散落在控制器 / 对接码派发 / 执行器三处各写一遍。
/// 纯函数 ⇒ 可被逐条断言，也保证三条触发路径（控制台点击 / 对接码 / 未来的其它入口）
/// 行为完全一致。
/// </para>
///
/// <para>
/// <b>它与 <see cref="ControlSettingsNormalizer"/> 的分工</b>：
/// 清洗器在<b>保存时</b>就把悬空成员剔掉了，所以正常路径上这里不会有东西可剔 ——
/// 但执行发生在"配置可能已经被下一次保存改过"之后，而且老配置 / 手工改过的 JSON
/// 都可能带悬空引用，所以这里**再算一遍**（幂等，代价可以忽略）。
/// </para>
/// </summary>
public static class ComboResolver
{
    /// <summary>
    /// 解析组合按钮。
    /// </summary>
    /// <param name="combo">组合按钮。</param>
    /// <param name="buttons">当前配置里的**全部主按钮**。</param>
    public static ComboPlan Resolve(ComboButton? combo, IReadOnlyList<ButtonConfig>? buttons)
    {
        if (combo is null) return new ComboPlan("未命名", Array.Empty<ComboSegment>(), Array.Empty<string>());

        var label = string.IsNullOrWhiteSpace(combo.Label) ? "未命名" : combo.Label;

        var byId = new Dictionary<string, ButtonConfig>(StringComparer.Ordinal);

        if (buttons is not null)
        {
            foreach (var button in buttons)
            {
                if (button is null) continue;

                // 同 Id 只认第一个：重复 Id 属于配置被手工改坏的情形，
                // 认第一个与清洗器"按列表顺序"的口径一致。
                byId.TryAdd(button.Id, button);
            }
        }

        var steps = combo.Steps ?? new List<ComboButtonStep>();
        var segments = new List<ComboSegment>(steps.Count);
        var skipped = new List<string>();

        foreach (var step in steps)
        {
            if (step is null) continue;

            var id = (step.ButtonId ?? string.Empty).Trim();

            if (id.Length == 0 || !byId.TryGetValue(id, out var member))
            {
                skipped.Add(id);
                continue;
            }

            segments.Add(new ComboSegment(member, Math.Clamp(step.DelayMs, 0, ComboButtonStep.MaxDelayMs)));
        }

        // 最后一段的延迟永远不会生效（后面没有下一段）⇒ 归零，别让"合计等待"这个数字虚高。
        if (segments.Count > 0)
        {
            var last = segments[^1];
            segments[^1] = last with { DelayAfterMs = 0 };
        }

        return new ComboPlan(label, segments, skipped);
    }
}
