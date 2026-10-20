using ControlServer.Contracts;
using ControlServer.Core.Services;
using ControlServer.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ControlServer.Server.Services;

/// <summary>
/// 代网页向<b>客户机</b>要一份目录列表（M4.4+，回传源路径选择）。
///
/// <para><b>它在整条链路里的位置</b>：网页 →（HTTP）→ 服务端 →（<c>/hubs/agent</c>）→ 客户机。
/// 这里负责中间那一段，也就是本项目<b>第一条"服务端要等客户机回话"</b>的调用。</para>
///
/// <para>⚠️ <b>超时由服务端自己掐，而且必须掐</b>：<c>InvokeAsync</c> 在没有应答时会一直挂着
/// （它自己不设时限）。不掐的下场是"网页上那个下拉框转圈转到用户关掉页面"，
/// 而服务端这边每点一次就多挂一个任务 —— 机器一多就能把线程池拖瘦。</para>
///
/// <para>⚠️ <b>超时与离线要分开报</b>：两者的处置完全不同（离线 = 等它上线；超时 = 稍后重试）。
/// 合并成"浏览失败"会让运维不知道该等还是该查。</para>
/// </summary>
public sealed class AgentFileBrowseService
{
    /// <summary>
    /// 单次浏览的等待上限。
    /// <para>
    /// 取 8 秒：列一个目录本身是毫秒级的，这个时限覆盖的是"网络抖一下"，
    /// 而不是"给慢操作留足时间"。再长只会让网页上那个下拉框更久地转圈。
    /// </para>
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private readonly AgentHostRegistry _registry;
    private readonly IHubContext<AgentHub> _hub;
    private readonly LogService _log;

    public AgentFileBrowseService(AgentHostRegistry registry, IHubContext<AgentHub> hub, LogService log)
    {
        _registry = registry;
        _hub = hub;
        _log = log;
    }

    /// <summary>列出客户机上某个目录。<paramref name="path"/> 为 null/空时列白名单顶层。</summary>
    public async Task<AgentBrowseResultDto> BrowseAsync(
        int agentId,
        string? path,
        CancellationToken cancellationToken = default)
    {
        var connectionId = _registry.ConnectionIdOf(agentId);

        // 离线是**最常见的失败**（回传本来就常发给暂时不在线的机器），所以它不是异常路径，
        // 而是一条要好好说话的正常返回。
        if (string.IsNullOrEmpty(connectionId))
        {
            return AgentBrowseResultDto.Fail(
                AgentBrowseStatuses.Offline, "这台客户机当前不在线，无法读取它的目录；等它上线后再试。");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        try
        {
            var result = await _hub.Clients
                .Client(connectionId)
                .InvokeAsync<AgentBrowseResultDto>(
                    AgentHubMethods.ListDirectory,
                    new AgentBrowseRequestDto(path),
                    cts.Token)
                .ConfigureAwait(false);

            return result
                ?? AgentBrowseResultDto.Fail(AgentBrowseStatuses.Error, "客户机没有返回浏览结果。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Warn("Files", $"浏览客户机 {agentId} 的目录超时（{Timeout.TotalSeconds:0} 秒）：{path ?? "(顶层)"}");

            return AgentBrowseResultDto.Fail(
                AgentBrowseStatuses.Timeout,
                $"客户机 {Timeout.TotalSeconds:0} 秒内没有回话 —— 可能正忙或网络不稳，稍后重试；"
                + "若一直如此，请确认该客户机版本是否已支持目录浏览。");
        }
        catch (OperationCanceledException)
        {
            // 调用方（浏览器断开）取消了：这不是故障，不必留痕。
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("Files", $"浏览客户机 {agentId} 的目录失败：{ex.Message}");

            return AgentBrowseResultDto.Fail(AgentBrowseStatuses.Error, $"读取客户机目录失败：{ex.Message}");
        }
    }
}
