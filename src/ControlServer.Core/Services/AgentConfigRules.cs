using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// 客户机配置的**合并 / 规范化 / 校验**纯函数（M4.5）。
/// <para>
/// 放在 Core 且不依赖任何 IO：这些规则同时被"服务端保存前校验"和"客户端应用前校验"使用 ——
/// 两边各写一份的结果是"服务端放行的配置客户端拒绝执行"（或反过来），
/// 而那种不一致在界面上表现为"保存成功但机器没动"。
/// </para>
/// </summary>
public static class AgentConfigRules
{
    /// <summary>
    /// 把增量补丁叠加到已有配置上。
    /// <para><b>null = 不动</b>，<b>空列表 = 清空</b>。这是整个 <c>PUT</c> 语义的基石。</para>
    /// </summary>
    public static AgentConfigContent Merge(AgentConfigContent? existing, AgentConfigPatch patch)
    {
        var baseContent = existing ?? AgentConfigContent.Empty;

        return new AgentConfigContent(
            patch.Udp ?? baseContent.Udp,
            patch.SendTasks ?? baseContent.SendTasks,
            patch.ScheduledTasks ?? baseContent.ScheduledTasks,
            patch.LaunchTasks ?? baseContent.LaunchTasks);
    }

    /// <summary>
    /// 规范化：只做"不改变语义的书写统一"，不做"猜用户想干什么"。
    /// <list type="bullet">
    ///   <item>字符串去首尾空白；协议名统一大写（<c>udp</c> → <c>UDP</c>）；</item>
    ///   <item>缺 Id 的条目补一个（否则前端"上下排序"认不出条目）；</item>
    ///   <item>时刻统一成 <c>HH:mm</c>（<c>9:5</c> → <c>09:05</c>）。</item>
    /// </list>
    /// </summary>
    public static AgentConfigContent Normalize(AgentConfigContent raw) => new(
        NormalizeUdp(raw.Udp),
        raw.SendTasks.Select(NormalizeSend).ToList(),
        raw.ScheduledTasks.Select(NormalizeScheduled).ToList(),
        raw.LaunchTasks.Select(NormalizeLaunch).ToList());

    private static AgentUdpChannelConfig NormalizeUdp(AgentUdpChannelConfig udp) => new(
        udp.Enabled,
        udp.Port,
        (udp.BlockedSourceIps ?? Array.Empty<string>())
            .Select(static x => (x ?? string.Empty).Trim())
            .Where(static x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList());

    private static AgentSendTask NormalizeSend(AgentSendTask task) => new(
        EnsureId(task.Id),
        Blank(task.Name),
        (task.Protocol ?? string.Empty).Trim().ToUpperInvariant(),
        (task.TargetIp ?? string.Empty).Trim(),
        task.TargetPort,
        NormalizeDataType(task.DataType),
        task.Data ?? string.Empty,
        task.DelaySeconds,
        task.IsLoop,
        task.LoopIntervalSeconds,
        task.Enabled);

    private static AgentScheduledTask NormalizeScheduled(AgentScheduledTask task) => new(
        EnsureId(task.Id),
        Blank(task.Name),
        (task.Protocol ?? string.Empty).Trim().ToUpperInvariant(),
        (task.TargetIp ?? string.Empty).Trim(),
        task.TargetPort,
        NormalizeDataType(task.DataType),
        task.Data ?? string.Empty,
        (task.Times ?? Array.Empty<string>())
            .Select(TryNormalizeTime)
            .Where(static x => x is not null)
            .Select(static x => x!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToList(),
        task.Enabled);

    private static AgentLaunchTask NormalizeLaunch(AgentLaunchTask task) => new(
        EnsureId(task.Id),
        (task.Name ?? string.Empty).Trim(),
        (task.ProgramPath ?? string.Empty).Trim(),
        task.Argument ?? string.Empty,
        task.DelaySeconds,
        task.Enabled);

    /// <summary>「UTF-8」是历史别名，一律归一成「String」（与 <c>ControlSettingsNormalizer</c> 同一口径）。</summary>
    private static string NormalizeDataType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return DataTypes.String;

        var trimmed = dataType.Trim();

        return DataEncoder.IsUtf8(trimmed) ? DataTypes.String : trimmed;
    }

    // ═══════════════════════════════════════════════════════════
    // 校验
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 校验（应在 <see cref="Normalize"/> <b>之后</b>调用）。返回问题清单，空 = 通过。
    /// <para>
    /// ⚠️ <b>返回清单而不是"抛第一个错"</b>：一次改十条任务时，只报第一条会逼用户改一轮试一轮。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Validate(AgentConfigContent content)
    {
        var problems = new List<string>();

        ValidateUdp(content.Udp, problems);
        ValidateTasks(content, problems);

        return problems;
    }

    private static void ValidateUdp(AgentUdpChannelConfig udp, List<string> problems)
    {
        if (!udp.Enabled) return;

        if (udp.Port is < AgentConfigDefaults.MinUdpPort or > AgentConfigDefaults.MaxUdpPort)
        {
            problems.Add($"UDP 通道端口必须在 {AgentConfigDefaults.MinUdpPort}–{AgentConfigDefaults.MaxUdpPort} 之间（当前 {udp.Port}）。");
        }

        if (udp.BlockedSourceIps.Count > AgentConfigDefaults.MaxBlockedSourceIps)
        {
            problems.Add($"UDP 来源黑名单最多 {AgentConfigDefaults.MaxBlockedSourceIps} 条（当前 {udp.BlockedSourceIps.Count} 条）。");
        }

        foreach (var ip in udp.BlockedSourceIps)
        {
            if (!IsIpAddress(ip))
            {
                problems.Add($"UDP 来源黑名单里的「{ip}」不是合法的 IP 地址。");
            }
        }
    }

    private static void ValidateTasks(AgentConfigContent content, List<string> problems)
    {
        if (content.SendTasks.Count > AgentConfigDefaults.MaxTasksPerList
            || content.ScheduledTasks.Count > AgentConfigDefaults.MaxTasksPerList
            || content.LaunchTasks.Count > AgentConfigDefaults.MaxTasksPerList)
        {
            problems.Add($"每张任务清单最多 {AgentConfigDefaults.MaxTasksPerList} 条。");
        }

        for (var i = 0; i < content.SendTasks.Count; i++)
        {
            var task = content.SendTasks[i];
            var label = $"延时发送第 {i + 1} 条";

            ValidateSendCore(task.Protocol, task.TargetIp, task.TargetPort, task.DataType, task.Data, label, problems);

            if (task.DelaySeconds is < 0 or > AgentConfigDefaults.MaxDelaySeconds)
            {
                problems.Add($"{label}：延时必须在 0–{AgentConfigDefaults.MaxDelaySeconds} 秒之间（当前 {task.DelaySeconds}）。");
            }

            if (task.IsLoop && task.LoopIntervalSeconds < AgentConfigDefaults.MinLoopIntervalSeconds)
            {
                problems.Add($"{label}：循环间隔不得小于 {AgentConfigDefaults.MinLoopIntervalSeconds} 秒（当前 {task.LoopIntervalSeconds}）。");
            }
        }

        for (var i = 0; i < content.ScheduledTasks.Count; i++)
        {
            var task = content.ScheduledTasks[i];
            var label = $"定时发送第 {i + 1} 条";

            ValidateSendCore(task.Protocol, task.TargetIp, task.TargetPort, task.DataType, task.Data, label, problems);

            if (task.Times.Count == 0)
            {
                // 只有启用中的任务才算问题：停用的空任务留着是"还没填完"，不该拦住整次保存。
                if (task.Enabled) problems.Add($"{label}：启用中的定时任务必须至少有一个发送时刻。");
            }
        }

        for (var i = 0; i < content.LaunchTasks.Count; i++)
        {
            var task = content.LaunchTasks[i];
            var label = $"延时启动第 {i + 1} 条";

            if (string.IsNullOrWhiteSpace(task.ProgramPath))
            {
                problems.Add($"{label}：必须填写程序路径。");
            }

            if (task.DelaySeconds is < 0 or > AgentConfigDefaults.MaxDelaySeconds)
            {
                problems.Add($"{label}：延时必须在 0–{AgentConfigDefaults.MaxDelaySeconds} 秒之间（当前 {task.DelaySeconds}）。");
            }
        }
    }

    private static void ValidateSendCore(
        string protocol,
        string targetIp,
        int targetPort,
        string dataType,
        string data,
        string label,
        List<string> problems)
    {
        if (protocol is not (TransportProtocols.Udp or TransportProtocols.Tcp))
        {
            problems.Add($"{label}：协议只能是 UDP 或 TCP（当前「{protocol}」）。");
        }

        if (string.IsNullOrWhiteSpace(targetIp))
        {
            problems.Add($"{label}：必须填写目标地址。");
        }
        else if (targetIp.Any(char.IsWhiteSpace))
        {
            problems.Add($"{label}：目标地址不能含空白字符（当前「{targetIp}」）。");
        }

        if (targetPort is < 1 or > 65535)
        {
            problems.Add($"{label}：目标端口必须在 1–65535 之间（当前 {targetPort}）。");
        }

        if (dataType is not (DataTypes.String or DataTypes.Hex or DataTypes.Ansi or DataTypes.Ascii))
        {
            // 「指令库」刻意不支持：指令库在服务端，客户机拿不到定义，也就发不出字节。
            problems.Add($"{label}：数据类型只能是 String / Hex / ANSI / ASCII（当前「{dataType}」）。");
            return;
        }

        if (!DataEncoder.TryEncode(dataType, data, out _, out var error))
        {
            problems.Add($"{label}：内容无法按「{dataType}」编码 —— {error}");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 小工具
    // ═══════════════════════════════════════════════════════════

    /// <summary>时刻是否合法（<c>HH:mm</c>，24 小时制）。</summary>
    public static bool IsValidTime(string? raw) => TryNormalizeTime(raw) is not null;

    /// <summary>
    /// 把 <c>9:5</c> / <c>09:05</c> / <c>09：05</c>（全角冒号）都归一成 <c>09:05</c>。
    /// <para>返回 null = 不合法。<b>归一化也在这里</b>，避免"校验用一套规则、存储用另一套"。</para>
    /// </summary>
    public static string? TryNormalizeTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw.Trim().Replace('：', ':');
        var parts = text.Split(':');

        if (parts.Length != 2) return null;

        if (!int.TryParse(parts[0], out var hour) || !int.TryParse(parts[1], out var minute)) return null;

        if (hour is < 0 or > 23 || minute is < 0 or > 59) return null;

        return $"{hour:00}:{minute:00}";
    }

    /// <summary>是否为合法 IP（v4 或 v6）。</summary>
    public static bool IsIpAddress(string? raw)
        => !string.IsNullOrWhiteSpace(raw)
            && System.Net.IPAddress.TryParse(raw.Trim(), out _);

    private static string EnsureId(string? id)
        => string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("n") : id.Trim();

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
