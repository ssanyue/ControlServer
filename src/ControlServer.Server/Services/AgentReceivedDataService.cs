using ControlServer.Contracts;
using ControlServer.Core.Services;
using ControlServer.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ControlServer.Server.Services;

/// <summary>
/// 代网页向<b>客户机</b>要一份「接收到的数据」快照（2026-10-13）。
///
/// <para><b>为什么需要它</b>：现场说"下发了指令，客户端毫无反应"。这句话对应三种完全不同的故障
/// —— <b>没收到</b> / <b>收到了认不出</b> / <b>认出了但没执行</b>。中心这一侧<b>看不出区别</b>
/// （它只知道"我 <c>SendAsync</c> 成功了"）。唯一的证据在客户机上：
/// 它收到了什么、当时判成了什么。所以要有一条"问过去"的路。</para>
///
/// <para><b>形状与 <see cref="AgentFileBrowseService"/> 一模一样</b>（网页 → HTTP → 服务端 →
/// <c>/hubs/agent</c> → 客户机）。这不是巧合，是有意的：本项目里"服务端要等客户机回话"的调用
/// 只有这一种正确形状，多一种形状就多一套超时与失败语义。客户机因此依旧<b>一个入站端口都不开</b>。</para>
///
/// <para>⚠️ <b>超时必须由服务端掐</b>：<c>InvokeAsync</c> 自己不设时限，不掐就会一直挂着，
/// 网页那边转圈转到用户关页面，而服务端每点一次就多挂一个任务。</para>
///
/// <para>⚠️ <b>超时与离线必须分开报</b>：离线 = 等它上线；超时 = 它可能正忙。
/// 合并成"读取失败"会让运维不知道该等还是该查。</para>
/// </summary>
public sealed class AgentReceivedDataService
{
    /// <summary>
    /// 单次查询的等待上限。
    /// <para>
    /// 取 8 秒，与目录浏览同一个数：客户机那边只是把内存里一个环形缓冲拷一份，
    /// 这个时限覆盖的是"网络抖一下"，而不是"给慢操作留足时间"。
    /// </para>
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private readonly AgentHostRegistry _registry;
    private readonly IHubContext<AgentHub> _hub;
    private readonly LogService _log;

    public AgentReceivedDataService(AgentHostRegistry registry, IHubContext<AgentHub> hub, LogService log)
    {
        _registry = registry;
        _hub = hub;
        _log = log;
    }

    /// <summary>要一份快照。</summary>
    public async Task<AgentReceivedDataDto> GetAsync(
        int agentId,
        CancellationToken cancellationToken = default)
    {
        var connectionId = _registry.ConnectionIdOf(agentId);

        // 离线不是异常路径，而是一条要好好说话的正常返回 —— 想看记录的机器，
        // 十有八九正是那台"发指令没反应"的，它可能刚断线。
        if (string.IsNullOrEmpty(connectionId))
        {
            return AgentReceivedDataDto.Fail(
                AgentReceivedStatuses.Offline,
                "这台客户机当前不在线，读不到它的接收记录；等它上线后再试。");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        try
        {
            var result = await _hub.Clients
                .Client(connectionId)
                .InvokeAsync<AgentReceivedDataDto>(AgentHubMethods.ListReceivedData, cts.Token)
                .ConfigureAwait(false);

            return result
                ?? AgentReceivedDataDto.Fail(AgentReceivedStatuses.Error, "客户机没有返回接收记录。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Warn("Agent", $"读取客户机 {agentId} 的接收记录超时（{Timeout.TotalSeconds:0} 秒）");

            return AgentReceivedDataDto.Fail(
                AgentReceivedStatuses.Timeout,
                $"客户机 {Timeout.TotalSeconds:0} 秒内没有回话 —— 可能正忙或网络不稳，稍后重试；"
                + "若一直如此，请确认该客户机版本是否已支持回看接收记录。");
        }
        catch (OperationCanceledException)
        {
            // 调用方（浏览器断开）取消了：不是故障，不必留痕。
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("Agent", $"读取客户机 {agentId} 的接收记录失败：{ex.Message}");

            return AgentReceivedDataDto.Fail(AgentReceivedStatuses.Error, $"读取客户机接收记录失败：{ex.Message}");
        }
    }
}
