using System.Collections.Concurrent;
using ControlServer.Contracts;
using ControlServer.Core.Services;

namespace ControlServer.Server.Services;

/// <summary>
/// 保存各台客户机<b>上报上来的</b>编码能力（M5.0 第二批）。
///
/// <para>
/// 🔥 <b>为什么必须由客户机报，而不是中心自己探</b>：要编码的是<b>被控端</b>。
/// 中心那台机器上有没有 NVENC，与"这台客户机能不能跑远控"完全无关 ——
/// 在中心本地探测只能回答一个错误的问题（这也正是 M5.0 第一批把探测实现挂在
/// <c>Platform.*</c>、而不是写死在 <c>RemoteController</c> 里的原因）。
/// </para>
///
/// <para>
/// <b>为什么只放内存、不落库</b>：
/// </para>
/// <list type="bullet">
///   <item>这份数据<b>每次连接都会重新上报</b>（客户机在 <c>Welcome</c> 之后报一次）——
///         而<b>中心重启必然导致所有长连接断开</b>，于是每台机器都会重连并重报。
///         ⇒ "中心重启后这份数据会空"这件事在下一个重连周期内自愈，不需要持久化。</item>
///   <item>硬件能力在一次开机周期内不变（换显卡要重启），落库只会多一张要迁移的表
///         和一条"库里的和实际的不一致"的新故障面。</item>
/// </list>
///
/// <para>
/// ⚠️ <b>刻意区分"没报过"与"报了但没能力"</b>：前者返回 <c>null</c>
/// （页面必须说"尚未上报"，见 <see cref="RemoteCapabilityDto.Probed"/>），
/// 后者返回一份 <c>Probed = true</c> 的报告。把前者显示成"不支持远控"是一个
/// <b>凭空的否定结论</b>。
/// </para>
/// </summary>
public sealed class AgentEncoderCapabilityService
{
    private readonly ConcurrentDictionary<int, AgentEncoderCapabilityDto> _latest = new();
    private readonly AgentHostRegistry _hosts;
    private readonly LogService _log;

    public AgentEncoderCapabilityService(AgentHostRegistry hosts, LogService log)
    {
        _hosts = hosts;
        _log = log;
    }

    /// <summary>
    /// 收下一台客户机的能力上报。
    /// <para>
    /// 归属校验是<b>结构性</b>的：<paramref name="agentId"/> 来自连接身份
    /// （<c>Context.User</c> 里的 AgentId），客户机<b>没法</b>替别人上报 ——
    /// 所以这里不需要再写"这个 id 是不是你自己的"那种判断（与 <c>PushConfig</c> 同一条判据）。
    /// </para>
    /// </summary>
    public AgentIngestAckDto Accept(int agentId, AgentEncoderCapabilityDto? report)
    {
        if (report is null) return AgentIngestAckDto.Rejected("empty", "载荷为空。");

        Prune();

        _latest[agentId] = report;

        _log.Info(
            "AgentHub",
            $"收到客户机 {agentId} 的编码能力上报：{report.Summary}");

        return AgentIngestAckDto.Success();
    }

    /// <summary>
    /// 取某台机器最近一次上报的能力。<b><c>null</c> = 它还没报过</b>
    /// （不是"它没有编码能力"）。
    /// </summary>
    public RemoteCapabilityDto? TryGet(int agentId)
    {
        if (!_latest.TryGetValue(agentId, out var report)) return null;

        return new RemoteCapabilityDto(
            Probed: true,
            Adapters: report.Adapters,
            SupportedCodecs: report.SupportedCodecs,
            RecommendedCodec: report.RecommendedCodec,
            Summary: report.Summary,
            ProbedAt: report.ProbedAt);
    }

    /// <summary>
    /// 丢掉已经不在在线表里的机器。
    /// <para>
    /// ⚠️ 这不是"以防万一"的清理，而是**唯一**的内存边界：管理员「移除」一台机器之后
    /// 它的记录是<b>硬删</b>的，而这张表按 AgentId 存 ——
    /// 不裁的话，反复"移除 + 重新注册"会让它无限增长。
    /// （裁掉是安全的：那些 Id 已经不在在线表里，<c>GET capabilities</c> 会先回 404，
    /// 本来就取不到。）
    /// </para>
    /// <para>放在 <see cref="Accept"/> 里顺手做：它本来就很稀（每次连接一次）。</para>
    /// </summary>
    private void Prune()
    {
        if (_latest.IsEmpty) return;

        var alive = _hosts.Hosts.Select(h => h.Id).ToHashSet();

        foreach (var id in _latest.Keys)
        {
            if (!alive.Contains(id)) _latest.TryRemove(id, out _);
        }
    }
}
