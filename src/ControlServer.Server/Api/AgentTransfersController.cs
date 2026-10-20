using ControlServer.Contracts;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using ControlServer.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ControlServer.Server.Api;

/// <summary>
/// 客户机侧的传输端点（M4.4，<b>AgentKey 身份</b>）：补领待办、回报进度与结果。
/// <para>
/// <b>为什么单独一条路径</b>（而不是并进 <c>/api/files</c>）：这两条路走的是
/// <b>两套完全独立的身份</b>（Cookie 认人 / AgentKey 认机器，铁律见 <c>AgentAuthentication</c>）。
/// 放在 <c>api/agents</c> 之下，"这是机器在调"一眼可见，鉴权也不会互相纠缠。
/// </para>
/// <para>
/// <b>补领是"离线机器也能收到文件"的实现点</b>：任务创建时对离线机器推不动，
/// 但明细已经建好了（<c>pending</c>）。机器上线后调 <c>GET</c> 把它拉走即可。
/// </para>
/// </summary>
[ApiController]
[Route("api/agents/transfers")]
[Authorize(AuthenticationSchemes = AgentAuthentication.Scheme)]
public sealed class AgentTransfersController : ControllerBase
{
    private readonly DistributionService _distributions;
    private readonly FileRepositoryService _files;
    private readonly LogService _log;

    public AgentTransfersController(
        DistributionService distributions,
        FileRepositoryService files,
        LogService log)
    {
        _distributions = distributions;
        _files = files;
        _log = log;
    }

    /// <summary>本机还没完成的传输条目（含刚建单时它离线的那批）。</summary>
    [HttpGet]
    public async Task<IActionResult> Pending(CancellationToken cancellationToken)
    {
        if (AgentKeyAuthenticationHandler.ReadAgentId(User) is not { } agentId)
            return Unauthorized(new { message = "缺少客户端身份。" });

        var items = await _distributions.ListOpenItemsAsync(agentId, cancellationToken).ConfigureAwait(false);

        if (items.Count == 0) return Ok(Array.Empty<AgentTransferTaskDto>());

        // 任务元数据（方向 / 文件名 / 目标目录 / 源路径）按需补齐。
        var tasks = new Dictionary<int, DistributionTask?>();
        var payloads = new List<AgentTransferTaskDto>(items.Count);

        foreach (var item in items)
        {
            if (!tasks.TryGetValue(item.TaskId, out var task))
            {
                task = await _distributions.FindTaskAsync(item.TaskId, cancellationToken).ConfigureAwait(false);
                tasks[item.TaskId] = task;
            }

            if (task is null) continue; // 任务被清理了，明细成孤儿 —— 跳过而不是报错。

            string? sha256 = null;

            if (task.Direction == DistributionDirections.Push && task.FileId > 0)
            {
                var file = await _files.FindAsync(task.FileId, cancellationToken).ConfigureAwait(false);
                sha256 = file?.Sha256;
            }

            payloads.Add(new AgentTransferTaskDto(
                ItemId: item.Id,
                TaskId: task.Id,
                Direction: task.Direction,
                FileId: task.FileId,
                FileName: task.FileName,
                Size: task.FileSize,
                Sha256: sha256,
                // ⚠️ 与实时派发（`DistributionsController.DispatchAsync`）走**同一个**取值规则：
                //    每台自己的目录优先，退而用任务级的旧形状值。
                //    两条路一旦分叉，就会出现"在线时落到 A、离线补领时落到 B"。
                TargetDir: item.EffectiveTargetDir(task.TargetDir),
                SourcePath: task.SourcePath));
        }

        return Ok(payloads);
    }

    /// <summary>回报单条传输的进度。</summary>
    [HttpPost("{itemId:int}/progress")]
    public async Task<IActionResult> ReportProgress(
        int itemId,
        [FromBody] AgentTransferProgressRequest? request,
        CancellationToken cancellationToken)
    {
        if (AgentKeyAuthenticationHandler.ReadAgentId(User) is not { } agentId)
            return Unauthorized(new { message = "缺少客户端身份。" });

        if (request is null) return BadRequest(new { message = "请求体不能为空。" });

        var updated = await _distributions
            .ReportProgressAsync(itemId, agentId, request.ReceivedBytes, request.TotalBytes, request.Error, cancellationToken)
            .ConfigureAwait(false);

        // 找不到 = 不是这台机器的条目，或已被清理。回 404 让客户端停止重试。
        if (updated is null) return NotFound(new { message = "没有属于本机的这个传输条目。" });

        return Ok(new AgentTransferAckDto(true, updated.State, null));
    }

    /// <summary>回报单条传输的最终结果（成功 / 失败）。</summary>
    [HttpPost("{itemId:int}/complete")]
    public async Task<IActionResult> Complete(
        int itemId,
        [FromBody] AgentTransferCompleteRequest? request,
        CancellationToken cancellationToken)
    {
        if (AgentKeyAuthenticationHandler.ReadAgentId(User) is not { } agentId)
            return Unauthorized(new { message = "缺少客户端身份。" });

        if (request is null) return BadRequest(new { message = "请求体不能为空。" });

        var updated = await _distributions
            .CompleteItemAsync(itemId, agentId, request.Ok, request.ReceivedBytes, request.Sha256, request.Error, cancellationToken)
            .ConfigureAwait(false);

        if (updated is null) return NotFound(new { message = "没有属于本机的这个传输条目。" });

        if (!request.Ok)
        {
            _log.Warn("Files", $"客户机 {agentId} 报告传输失败（条目 {itemId}）：{updated.Error}");
        }

        return Ok(new AgentTransferAckDto(true, updated.State, null));
    }
}
