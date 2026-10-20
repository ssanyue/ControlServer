namespace ControlServer.Core.Models;

// ═══════════════════════════════════════════════════════════════════════════
// 客户机配置的**领域模型**（M4.5）。
//
// 与契约层的 `AgentConfigDto` 刻意分开：
//   · 契约层是"过网线的形状"（可空、宽松，注释全是给前端看的）；
//   · 这里是"落库与执行用的形状"（不可空、已规范化，注释是给服务端/客户端实现看的）。
// 两者的映射集中在 Server 的控制器与 Agent 的配置客户端，
// 免得"某个字段在 DTO 里可空、在模型里不可空"这类差异散落各处。
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 客户机的 UDP 指令通道（规范化后）。
/// <para><see cref="BlockedSourceIps"/> <b>永不为 null</b>（空列表 = 不拉黑任何来源）——
/// 让"没配"和"配了空"在执行期是同一个意思，少一个判空分支就少一个漏判点。</para>
/// <para>
/// 🔥 2026-10-13 起这里从"白名单"变成"黑名单"（同时报文改为纯明文）。
/// 字段名跟着语义走是必须的：一个叫 <c>AllowedSourceIps</c> 的字段装着"不许发的 IP"，
/// 以后维护的人一定会按名字去理解它。
/// </para>
/// </summary>
public sealed record AgentUdpChannelConfig(
    bool Enabled,
    int Port,
    IReadOnlyList<string> BlockedSourceIps);

/// <summary>一条延时 / 循环发送任务（<see cref="IsLoop"/> 为 true 即循环发送）。</summary>
public sealed record AgentSendTask(
    string Id,
    string? Name,
    string Protocol,
    string TargetIp,
    int TargetPort,
    string DataType,
    string Data,
    int DelaySeconds,
    bool IsLoop,
    int LoopIntervalSeconds,
    bool Enabled);

/// <summary>一条定时发送任务。</summary>
public sealed record AgentScheduledTask(
    string Id,
    string? Name,
    string Protocol,
    string TargetIp,
    int TargetPort,
    string DataType,
    string Data,
    IReadOnlyList<string> Times,
    bool Enabled);

/// <summary>一条延时启动程序任务。</summary>
public sealed record AgentLaunchTask(
    string Id,
    string Name,
    string ProgramPath,
    string Argument,
    int DelaySeconds,
    bool Enabled);

/// <summary>
/// 配置的**内容部分**（不含版本与时间戳）。
/// <para>
/// 版本号与更新时刻由存储层在保存时生成 —— 调用方**没有机会**自己指定版本，
/// 这是刻意的：版本号一旦可以被外部写，就会出现"两个客户端同时保存、版本号撞车"，
/// 而版本号是客户端判断"这版是不是比手上新"的唯一依据。
/// </para>
/// </summary>
public sealed record AgentConfigContent(
    AgentUdpChannelConfig Udp,
    IReadOnlyList<AgentSendTask> SendTasks,
    IReadOnlyList<AgentScheduledTask> ScheduledTasks,
    IReadOnlyList<AgentLaunchTask> LaunchTasks)
{
    /// <summary>
    /// 空配置：<b>UDP 指令通道默认开启</b>（端口 <see cref="AgentConfigDefaults.DefaultUdpPort"/>），三张清单皆空。
    /// 新机器的起点。
    /// <para>
    /// 🔥 2026-10-13 起通道<b>默认开着</b>（此前是关的）。原因：现场测下来"指令无效果"的头号原因是
    /// <b>通道压根没绑端口</b>（默认关闭 ⇒ <c>netstat</c> 里看不见 ⇒ 发什么都没反应），
    /// 而它本该是"装完就能用"的能力。要关就到网页/本机页面上把「启用」取消 —— 那是显式动作，
    /// 比"默认关、忘了开、然后怀疑网络"好排查得多。
    /// </para>
    /// </summary>
    public static AgentConfigContent Empty { get; } = new(
        new AgentUdpChannelConfig(true, AgentConfigDefaults.DefaultUdpPort, Array.Empty<string>()),
        Array.Empty<AgentSendTask>(),
        Array.Empty<AgentScheduledTask>(),
        Array.Empty<AgentLaunchTask>());
}

/// <summary>一份完整的配置（内容 + 版本 + 时间戳）。</summary>
/// <param name="AgentId">归属的客户机。</param>
/// <param name="Version">单调递增的版本号（从 1 开始；0 表示"该机还没有配置"）。</param>
public sealed record AgentConfigRecord(
    int AgentId,
    int Version,
    DateTimeOffset UpdatedAt,
    AgentConfigContent Content);

/// <summary>
/// 配置的**增量修改**（<c>PUT</c> 请求映射过来）。
/// <para>字段为 null = <b>不动那一块</b>；空列表 = <b>清空那一块</b>。两者不是一回事。</para>
/// </summary>
public sealed record AgentConfigPatch(
    AgentUdpChannelConfig? Udp,
    IReadOnlyList<AgentSendTask>? SendTasks,
    IReadOnlyList<AgentScheduledTask>? ScheduledTasks,
    IReadOnlyList<AgentLaunchTask>? LaunchTasks);

/// <summary>配置相关的默认值与边界（服务端校验与客户端执行共用同一套数字）。</summary>
public static class AgentConfigDefaults
{
    /// <summary>
    /// UDP 指令通道默认端口。
    /// <para>
    /// 🔥 <b>8090</b>（2026-10-13 从 8080 改过来）。原来沿用参考项目的 8080，但那恰好是
    /// <b>本项目的 HTTP 端口</b>：客户机与服务端同机部署（开发/单机演示很常见）时，
    /// 日志/文档里"8080"同时指两件事，排障时极易误判。改成 8090 后 TCP(HTTP) 与 UDP(指令)
    /// 一眼可分。
    /// </para>
    /// </summary>
    public const int DefaultUdpPort = 8090;

    /// <summary>UDP 通道端口下限（避开 1024 以下的系统保留段）。</summary>
    public const int MinUdpPort = 1024;

    /// <summary>UDP 通道端口上限。</summary>
    public const int MaxUdpPort = 65535;

    /// <summary>循环发送的最小间隔（秒）。比这更密就是在打自己的网络，没有正当用途。</summary>
    public const int MinLoopIntervalSeconds = 1;

    /// <summary>单次延时的上限（秒）。24 小时 —— 更长的"延时"应该是"定时"。</summary>
    public const int MaxDelaySeconds = 86_400;

    /// <summary>每张任务清单的条数上限（防御性上限，避免一条请求把库写爆）。</summary>
    public const int MaxTasksPerList = 500;

    /// <summary>UDP 来源黑名单的条数上限。</summary>
    public const int MaxBlockedSourceIps = 256;

    /// <summary>配置最多允许的重试解析次数之类的场景没有；这里只留一个"配置体大小上限"给存储层参考。</summary>
    public const int MaxConfigJsonBytes = 512 * 1024;
}
