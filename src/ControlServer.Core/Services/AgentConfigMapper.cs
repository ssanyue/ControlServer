using ControlServer.Contracts;
using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// 客户机配置的 <b>DTO ⇄ 领域模型</b> 映射（M4.5 补，2026-10-14）。
/// <para>
/// 🔥 <b>为什么放在 Core 而不是服务端控制器里</b>：这份映射现在有<b>两个</b>调用方 ——
/// 服务端保存/下发配置，以及客户机本机的「本机配置」页面。两边各写一份的结果不是编译错误，
/// 而是"保存时少映射一个字段"：服务端放行了、客户机按另一份形状解释，
/// 表现为<b>"保存成功但机器没动"</b>，且编译期完全看不出来。
/// </para>
/// <para>
/// 🔥 <b>为什么不是四处各写一份字面量</b>：位置参数 record 的 <c>new(...)</c> 顺序一旦写错
/// （例如把 <c>Name</c> 与 <c>DelaySeconds</c> 换个位置），在 <c>string</c>/<c>int</c>
/// 不同的静态类型上会直接编译失败 —— 那是好事；但同类型的字段互换（两个 <c>string</c>）
/// 会静默编译通过。收在一处，至少只需要核对一次。
/// </para>
/// </summary>
public static class AgentConfigMapper
{
    /// <summary>
    /// <see cref="AgentConfigRecord"/> → DTO。<b>record 为 null = 该机还没配过</b>，
    /// 给一份「UDP 通道默认开启（8090）+ 三张清单皆空」的空配置，版本 0、时间 null。
    /// <para>
    /// ⚠️ 这里<b>刻意不返回 404</b>：对客户端来说"没有配置"是一个合法状态，不是错误。
    /// 而"没配过"与"配过空配置"在下发链路上必须长得一样 —— 否则客户端要为前者写一条
    /// 从没被走通过的降级分支。
    /// </para>
    /// </summary>
    public static AgentConfigDto ToDto(AgentConfigRecord? record)
        => record is null
            ? ToDto(AgentConfigContent.Empty, version: 0, updatedAt: null)
            : ToDto(record.Content, record.Version, record.UpdatedAt);

    /// <summary>内容 + 版本 → DTO。</summary>
    public static AgentConfigDto ToDto(AgentConfigContent content, int version, DateTimeOffset? updatedAt)
    {
        ArgumentNullException.ThrowIfNull(content);

        return new AgentConfigDto(
            version,
            updatedAt,
            new AgentUdpChannelDto(content.Udp.Enabled, content.Udp.Port, content.Udp.BlockedSourceIps),
            content.SendTasks.Select(ToDto).ToList(),
            content.ScheduledTasks.Select(ToDto).ToList(),
            content.LaunchTasks.Select(ToDto).ToList());
    }

    /// <summary>
    /// 整份配置 → 保存请求形状（<b>四块全带</b>）。
    /// <para>
    /// 用途：客户机把"本机此刻生效的那一份"推给中心（<c>PushConfig</c>）。四块都带 = 整包替换，
    /// 与"某块为 null 就不动那一块"的区别在这里是<b>要紧的</b>：客户机手上这份就是完整的一份，
    /// 若把空清单写成 null，中心那边就会留下几条本机已经删掉的任务。
    /// </para>
    /// </summary>
    public static AgentConfigSaveRequest ToSaveRequest(AgentConfigDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new AgentConfigSaveRequest(dto.Udp, dto.SendTasks, dto.ScheduledTasks, dto.LaunchTasks);
    }

    /// <summary>DTO → 领域内容（不含版本，版本由保存方生成）。</summary>
    public static AgentConfigContent ToContent(AgentConfigDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new AgentConfigContent(
            new AgentUdpChannelConfig(
                dto.Udp?.Enabled ?? true,
                dto.Udp?.Port ?? AgentConfigDefaults.DefaultUdpPort,
                dto.Udp?.BlockedSourceIps?.ToList() ?? new List<string>()),
            dto.SendTasks?.Select(ToModel).ToList() ?? new List<AgentSendTask>(),
            dto.ScheduledTasks?.Select(ToModel).ToList() ?? new List<AgentScheduledTask>(),
            dto.LaunchTasks?.Select(ToModel).ToList() ?? new List<AgentLaunchTask>());
    }

    /// <summary>
    /// 保存请求 → 增量补丁。<b>null = 不动那一块</b>，<b>空列表 = 清空那一块</b> ——
    /// 这是整个 <c>PUT</c> 语义的基石，映射时一个字都不能含糊。
    /// </summary>
    public static AgentConfigPatch ToPatch(AgentConfigSaveRequest? request)
    {
        if (request is null) return new AgentConfigPatch(null, null, null, null);

        return new AgentConfigPatch(
            request.Udp is null
                ? null
                : new AgentUdpChannelConfig(
                    request.Udp.Enabled,
                    request.Udp.Port,
                    request.Udp.BlockedSourceIps?.ToList() ?? new List<string>()),
            request.SendTasks?.Select(ToModel).ToList(),
            request.ScheduledTasks?.Select(ToModel).ToList(),
            request.LaunchTasks?.Select(ToModel).ToList());
    }

    /// <summary>
    /// <paramref name="candidate"/> 是否比 <paramref name="current"/> 新。
    /// <para>
    /// 判据是<b>版本号优先、版本号相同再比时间戳</b>。只看版本号不够：从 2026-10-14 起
    /// 客户机也能改自己那份配置（本机配置页），它的版本号与服务端是<b>同一个计数器</b>，
    /// 但"客户机离线改了 v6、服务端同时也改到 v6"是真会发生的 —— 那种情况下版本号相等，
    /// 必须靠 <c>UpdatedAt</c> 分出先后，否则两边会各自认为"我更新"，永远不收敛。
    /// </para>
    /// <para>时间戳两边都可能为 null（从未保存过）：<b>null 视为最旧</b>。</para>
    /// </summary>
    public static bool IsNewer(AgentConfigDto candidate, AgentConfigDto? current)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (current is null) return true;

        if (candidate.Version != current.Version) return candidate.Version > current.Version;

        var candidateAt = candidate.UpdatedAt ?? DateTimeOffset.MinValue;
        var currentAt = current.UpdatedAt ?? DateTimeOffset.MinValue;

        return candidateAt > currentAt;
    }

    // ═══════════════════════════════════════════════════════════
    // 单条映射
    // ═══════════════════════════════════════════════════════════

    private static AgentSendTaskDto ToDto(AgentSendTask task) => new(
        task.Id,
        task.Protocol,
        task.TargetIp,
        task.TargetPort,
        task.DataType,
        task.Data,
        task.DelaySeconds,
        task.IsLoop,
        task.LoopIntervalSeconds,
        task.Name,
        task.Enabled);

    private static AgentScheduledTaskDto ToDto(AgentScheduledTask task) => new(
        task.Id,
        task.Protocol,
        task.TargetIp,
        task.TargetPort,
        task.DataType,
        task.Data,
        task.Times,
        task.Name,
        task.Enabled);

    private static AgentLaunchTaskDto ToDto(AgentLaunchTask task) => new(
        task.Id,
        task.Name,
        task.ProgramPath,
        task.Argument,
        task.DelaySeconds,
        task.Enabled);

    private static AgentSendTask ToModel(AgentSendTaskDto task) => new(
        task.Id ?? string.Empty,
        task.Name,
        task.Protocol ?? string.Empty,
        task.TargetIp ?? string.Empty,
        task.TargetPort,
        task.DataType ?? string.Empty,
        task.Data ?? string.Empty,
        task.DelaySeconds,
        task.IsLoop,
        task.LoopIntervalSeconds,
        task.Enabled);

    private static AgentScheduledTask ToModel(AgentScheduledTaskDto task) => new(
        task.Id ?? string.Empty,
        task.Name,
        task.Protocol ?? string.Empty,
        task.TargetIp ?? string.Empty,
        task.TargetPort,
        task.DataType ?? string.Empty,
        task.Data ?? string.Empty,
        task.Times?.ToList() ?? new List<string>(),
        task.Enabled);

    private static AgentLaunchTask ToModel(AgentLaunchTaskDto task) => new(
        task.Id ?? string.Empty,
        task.Name ?? string.Empty,
        task.ProgramPath ?? string.Empty,
        task.Argument ?? string.Empty,
        task.DelaySeconds,
        task.Enabled);
}
