using System.Collections.Concurrent;
using ControlServer.Contracts;
using ControlServer.Core.Services;

namespace ControlServer.Server.Services;

/// <summary>
/// 保存各台客户机<b>上报上来的屏清单</b>（M5.5 多屏选择）。
///
/// <para>
/// 🔥 <b>为什么屏清单不是能力的附庸</b>：编码能力在<b>服务进程</b>里探得到
/// （MFT 枚举），而屏清单只有<b>用户会话里的托盘</b>数得出来 ——
/// 服务在 Session 0，那里既没有桌面也没有显示器。
/// 所以这两份数据来自两个不同的进程、两条不同的链路，不能合成一次上报。
/// </para>
///
/// <para>
/// ⚠️ <b>与能力上报同一条纪律：只放内存</b>。插拔显示器会变，而"变了"这件事
/// 只有托盘知道；它不会主动告诉我们（没有热插拔监听），
/// 所以这份数据带 <see cref="AgentScreensDto.ReportedAt"/>，
/// 页面按"多久以前报的"决定要不要重新问一次。
/// </para>
///
/// <para>
/// 🔴 <b>"还没问过"与"问了但它一块屏都没有"必须分开</b>：
/// 前者 <c>null</c>（页面显示"正在向客户机索取"），
/// 后者是一份 <c>Screens = []</c> 的报告（页面显示"这台机器没有可抓的显示器"）。
/// 把前者画成后者，现场会去那台机器上查显示器插没插 —— 而它只是还没回答。
/// </para>
/// </summary>
public sealed class AgentScreenDirectory
{
    /// <summary>
    /// 一份清单"还算新鲜"的时长。
    /// <para>
    /// ⚠️ 它不是缓存过期时间（过期了也照样返回，只是页面多一句"这是几分钟前的"）：
    /// 过期就当成空的话，一次网络抖动会让屏选择框变空，而用户会以为"这台机器没屏了"。
    /// </para>
    /// </summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<int, AgentScreensDto> _latest = new();
    private readonly AgentHostRegistry _hosts;
    private readonly LogService _log;

    public AgentScreenDirectory(AgentHostRegistry hosts, LogService log)
    {
        _hosts = hosts;
        _log = log;
    }

    /// <summary>
    /// 收下一台客户机的屏清单。
    /// <para>
    /// 归属校验是<b>结构性</b>的：<paramref name="agentId"/> 来自连接身份，
    /// 客户机没法替别人上报。
    /// </para>
    /// </summary>
    public AgentIngestAckDto Accept(int agentId, AgentScreensDto? report)
    {
        if (report is null) return AgentIngestAckDto.Rejected("empty", "载荷为空。");

        Prune();

        _latest[agentId] = report;

        _log.Info(
            "AgentHub",
            $"收到客户机 {agentId} 的屏清单：{report.Screens.Count} 项"
            + (report.Note is null ? "" : $"（{report.Note}）"));

        return AgentIngestAckDto.Success();
    }

    /// <summary>
    /// 取某台机器最近一次上报的屏清单。<b><c>null</c> = 它还没回答过</b>。
    /// </summary>
    public AgentScreensDto? TryGet(int agentId) =>
        _latest.TryGetValue(agentId, out var report) ? report : null;

    /// <summary>这份清单是不是"最近 5 分钟内报的"（页面据此决定要不要再问一次）。</summary>
    public static bool IsFresh(AgentScreensDto report) =>
        DateTimeOffset.UtcNow - report.ReportedAt.ToUniversalTime() < FreshFor;

    private void Prune()
    {
        if (_latest.IsEmpty) return;

        foreach (var agentId in _latest.Keys)
        {
            if (_hosts.Hosts.All(h => h.Id != agentId)) _latest.TryRemove(agentId, out _);
        }
    }
}
