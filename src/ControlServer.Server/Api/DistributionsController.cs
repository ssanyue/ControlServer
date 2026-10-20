using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using ControlServer.Server.Hubs;
using ControlServer.Server.Options;
using ControlServer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.Api;

/// <summary>
/// 文件分发任务（M4.4）：下发 / 回传 / 查看进度。
/// <para>
/// <b>权限</b>：建立任务是 <see cref="Policies.AdminOnly"/>（推文件到客户机、从客户机取文件，
/// 都是"作用到别的机器上"的动作，与远程重启同级）；看任务是 <see cref="Policies.CanOperate"/>。
/// </para>
/// <para>
/// <b>派发方式</b>：任务落库后，给<b>当前在线</b>的目标机器各推一条 <c>PushTransferTask</c>。
/// 离线机器不推 —— 它上线时会调 <c>GET /api/agents/transfers</c> 自己补领
/// （这就是"任务建满所有目标"的意义：离线不是失败，只是晚一点）。
/// </para>
/// </summary>
[ApiController]
[Route("api/distributions")]
public sealed class DistributionsController : ControllerBase
{
    private readonly DistributionService _distributions;
    private readonly FileRepositoryService _files;
    private readonly IAgentHostStore _hosts;
    private readonly AgentHostRegistry _registry;
    private readonly IHubContext<AgentHub> _agentHub;
    private readonly LogService _log;
    private readonly IOptions<ServerOptions> _options;

    public DistributionsController(
        DistributionService distributions,
        FileRepositoryService files,
        IAgentHostStore hosts,
        AgentHostRegistry registry,
        IHubContext<AgentHub> agentHub,
        LogService log,
        IOptions<ServerOptions> options)
    {
        _distributions = distributions;
        _files = files;
        _hosts = hosts;
        _registry = registry;
        _agentHub = agentHub;
        _log = log;
        _options = options;
    }

    // ═══════════════════════════════════════════════════════════
    // 建任务
    // ═══════════════════════════════════════════════════════════

    /// <summary>新建一个下发任务（把仓库里的文件推给一批客户机）。</summary>
    [HttpPost]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> CreatePush(
        [FromBody] DistributionCreateRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { message = "请求体不能为空。" });

        var result = await _distributions
            .CreatePushAsync(request.FileId, ResolveTargets(request), User.Identity?.Name, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Ok || result.Task is not { } task)
        {
            return result.Status == "notfound"
                ? NotFound(new { message = result.Message })
                : BadRequest(new { message = result.Message });
        }

        await DispatchAsync(task, cancellationToken).ConfigureAwait(false);

        await RememberPathsAsync(task, push: true, cancellationToken).ConfigureAwait(false);

        return Ok(await ToDtoAsync(task, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>新建一个回传任务（把客户机上的某个文件收上来）。</summary>
    [HttpPost("collect")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> CreateCollect(
        [FromBody] CollectCreateRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { message = "请求体不能为空。" });

        var result = await _distributions
            .CreateCollectAsync(request.TargetAgentIds, request.SourcePath, request.Note, User.Identity?.Name, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Ok || result.Task is not { } task)
        {
            return BadRequest(new { message = result.Message });
        }

        await DispatchAsync(task, cancellationToken).ConfigureAwait(false);

        await RememberPathsAsync(task, push: false, cancellationToken).ConfigureAwait(false);

        return Ok(await ToDtoAsync(task, cancellationToken).ConfigureAwait(false));
    }

    // ═══════════════════════════════════════════════════════════
    // 查任务
    // ═══════════════════════════════════════════════════════════

    /// <summary>任务列表（最近在前，含每个任务的进度汇总）。</summary>
    [HttpGet]
    [Authorize(Policy = Policies.CanOperate)]
    public async Task<IActionResult> List([FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var tasks = await _distributions
            .ListTasksAsync(limit is > 0 and <= 500 ? limit.Value : 50, cancellationToken)
            .ConfigureAwait(false);

        if (tasks.Count == 0) return Ok(new DistributionListDto(Array.Empty<DistributionTaskDto>(), 0));

        // 一次性把明细全捞回来（避免"一个任务一条查询"的 N+1）。
        var allItems = await _distributions
            .ListItemsForTasksAsync(tasks.Select(t => t.Id).ToList(), cancellationToken)
            .ConfigureAwait(false);

        var byTask = allItems.GroupBy(i => i.TaskId).ToDictionary(g => g.Key, g => (IReadOnlyList<DistributionItem>)g.ToList());

        var dtos = new List<DistributionTaskDto>(tasks.Count);

        foreach (var task in tasks)
        {
            var items = byTask.TryGetValue(task.Id, out var list) ? list : Array.Empty<DistributionItem>();
            dtos.Add(await ToDtoAsync(task, items, cancellationToken).ConfigureAwait(false));
        }

        return Ok(new DistributionListDto(dtos, dtos.Count));
    }

    /// <summary>单个任务详情（含每个目标的实时进度）。</summary>
    [HttpGet("{id:int}")]
    [Authorize(Policy = Policies.CanOperate)]
    public async Task<IActionResult> Detail(int id, CancellationToken cancellationToken)
    {
        var task = await _distributions.FindTaskAsync(id, cancellationToken).ConfigureAwait(false);
        if (task is null) return NotFound(new { message = "分发任务不存在。" });

        return Ok(await ToDtoAsync(task, cancellationToken).ConfigureAwait(false));
    }

    // ═══════════════════════════════════════════════════════════
    // 私有：派发与映射
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把两种请求形状统一成"每台一条目标"（HTTP 形状 → 领域形状的翻译就在这里）。
    /// <para>
    /// · <see cref="DistributionCreateRequest.Targets"/>（当前形状）—— 每台可各带一个目录，<b>优先</b>；
    /// · <see cref="DistributionCreateRequest.TargetAgentIds"/> + <c>TargetDir</c>（旧形状）——
    ///   所有目标共用一个目录，保留只是为了让既有调用方不改也能继续用。
    /// </para>
    /// <para>两者都没给 → 返回空表，由服务层给出"请至少选择一台客户机"。</para>
    /// </summary>
    private static IReadOnlyList<DistributionTargetRequest> ResolveTargets(DistributionCreateRequest request)
    {
        if (request.Targets is { Count: > 0 }) return request.Targets;

        if (request.TargetAgentIds is { Count: > 0 })
        {
            return request.TargetAgentIds
                .Select(id => new DistributionTargetRequest(id, request.TargetDir))
                .ToList();
        }

        return Array.Empty<DistributionTargetRequest>();
    }

    /// <summary>
    /// 记下"这次用的是哪个目录"，下次打开这一页直接预填（2026-10-13）。
    ///
    /// <para>
    /// 现场的场景：同一台机器每次落的目录几乎都一样（<c>D:\播放内容</c>、<c>E:\素材</c>），
    /// 而在此之前每建一次单都要从"此电脑"开始重新点进去一遍 —— 这个记忆就是为它做的。
    /// </para>
    ///
    /// <para>
    /// 🔥 <b>只有"这一次确实指定了目录"才回写记忆</b>（2026-10-15 修）。
    /// 从前这里写的是 <c>EffectiveTargetDir(...) ?? string.Empty</c> —— 于是一次
    /// <b>"留空 = 用客户机默认目录"</b>的建单，会把刚在浏览面板里点「记为此机默认目录」
    /// 存下的路径<b>当场冲成空串</b>。现场表现就是"<b>路径保存不上</b>"：
    ///
    /// <code>
    /// 16:47:08  客户端默认目录已记忆（Id=11）
    /// 16:47:18  客户端默认目录已记忆（Id=11）
    /// 16:47:43  客户端默认目录已记忆（Id=11）   ← 连点三次（屏幕上什么都没变）
    /// 16:47:49  新建下发任务 #10 … 落地目录 (默认)  ← 这一单把记忆写成了空串
    /// 18:28     GET /api/agents ⇒ lastTargetDir = ""   ← 记住的目录不见了
    /// </code>
    ///
    /// <b>留空只是"这一次不定目录"，不该有删掉记忆的副作用</b> —— 删记忆是一个
    /// 破坏性的、不可见的动作，而"这次用默认目录"这个意图已经由任务本身表达了。
    /// </para>
    ///
    /// <para>⚠️ <b>失败只记日志</b>，绝不让建单失败：它只是"省一次点击"的便利，
    /// 而建单本身已经把任务落库并派发出去了。</para>
    /// </summary>
    private async Task RememberPathsAsync(
        DistributionTask task,
        bool push,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DistributionItem> items;

        try
        {
            items = await _distributions.ListItemsAsync(task.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("Files", $"读取任务 {task.Id} 的明细失败（路径记忆跳过）：{ex.Message}");
            return;
        }

        foreach (var item in items)
        {
            // 回传方向记的是**源文件所在的目录**（不是文件本身）——
            // 预填时它要直接当浏览器的落点用。
            var dir = push
                ? item.EffectiveTargetDir(task.TargetDir)
                : ParentDirectoryOf(task.SourcePath);

            // 🔥 没有目录就**什么都别写**（从前下发方向会写一个空串，把记忆冲掉）。
            //    两个来源都可能给不出目录：下发时运维留空（= 用客户机默认目录），
            //    回传时源路径是个推不出父目录的相对名。两种都是"这次不确定"，不是"清除"。
            if (string.IsNullOrWhiteSpace(dir)) continue;

            try
            {
                await _hosts
                    .SetLastPathAsync(item.AgentId, push ? dir : null, push ? null : dir, cancellationToken)
                    .ConfigureAwait(false);

                // 内存快照同步刷一遍：否则下发页要等最多 60 秒才回显（见 AgentHostRegistry.SetLastPath）。
                _registry.SetLastPath(item.AgentId, push ? dir : null, push ? null : dir);
            }
            catch (Exception ex)
            {
                _log.Warn("Files", $"记录客户机 {item.AgentId} 的落脚目录失败（不影响任务）：{ex.Message}");
            }
        }
    }

    /// <summary>取一个文件路径所在的目录；推不出来（相对名 / 非法字符）返回 null。</summary>
    private static string? ParentDirectoryOf(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        try
        {
            var parent = Path.GetDirectoryName(filePath.Trim());

            return string.IsNullOrEmpty(parent) ? null : parent;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 给<b>当前在线</b>的目标机器推任务。
    /// <para>
    /// 只推在线的：离线机器的连接 Id 是空的，硬推会得到一串异常日志而毫无效果 ——
    /// 它们会在上线时通过 <c>GET /api/agents/transfers</c> 补领。
    /// </para>
    /// </summary>
    private async Task DispatchAsync(DistributionTask task, CancellationToken cancellationToken)
    {
        var items = await _distributions.ListItemsAsync(task.Id, cancellationToken).ConfigureAwait(false);

        string? sha256 = null;

        if (task.Direction == DistributionDirections.Push && task.FileId > 0)
        {
            var file = await _files.FindAsync(task.FileId, cancellationToken).ConfigureAwait(false);
            sha256 = file?.Sha256;
        }

        var pushed = 0;

        foreach (var item in items)
        {
            var connectionId = _registry.ConnectionIdOf(item.AgentId);
            if (string.IsNullOrEmpty(connectionId)) continue;

            var payload = new AgentTransferTaskDto(
                ItemId: item.Id,
                TaskId: task.Id,
                Direction: task.Direction,
                FileId: task.FileId,
                FileName: task.FileName,
                Size: task.FileSize,
                Sha256: sha256,
                // 这一台自己的目录优先（每台可以不同），退而用任务级的旧形状值。
                TargetDir: item.EffectiveTargetDir(task.TargetDir),
                SourcePath: task.SourcePath);

            try
            {
                await _agentHub.Clients.Client(connectionId)
                    .SendAsync(AgentHubMethods.PushTransferTask, payload, cancellationToken)
                    .ConfigureAwait(false);

                pushed++;
            }
            catch (Exception ex)
            {
                // 一台推失败不该让整个任务建不起来：那条明细会留在 pending，
                // 机器下次上线时自己补领。
                _log.Warn("Files", $"向客户机 {item.AgentId} 派发传输任务失败：{ex.Message}");
            }
        }

        _log.Info("Files", $"传输任务 #{task.Id} 已派发：{pushed}/{items.Count} 台在线并收到指令。");
    }

    private async Task<DistributionTaskDto> ToDtoAsync(DistributionTask task, CancellationToken cancellationToken)
    {
        var items = await _distributions.ListItemsAsync(task.Id, cancellationToken).ConfigureAwait(false);
        return await ToDtoAsync(task, items, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DistributionTaskDto> ToDtoAsync(
        DistributionTask task,
        IReadOnlyList<DistributionItem> items,
        CancellationToken cancellationToken)
    {
        var now = BeijingTime.Now;
        var options = _options.Value.Agent;

        var dtos = new List<DistributionItemDto>(items.Count);

        foreach (var item in items)
        {
            // 客户端可能已经被移除（记录删了、分发明细还在）—— 那时显示一个可读的占位名，
            // 而不是让整个任务详情页因为"找不到主机"而打不开。
            var host = _registry.Hosts.FirstOrDefault(h => h.Id == item.AgentId);

            var agentName = host?.DisplayName ?? $"客户机 #{item.AgentId}";
            var agentState = host is null ? AgentStates.Offline : _registry.ToDto(host, options, now).State;

            dtos.Add(new DistributionItemDto(
                item.Id,
                item.AgentId,
                agentName,
                agentState,
                item.State,
                item.ReceivedBytes,
                item.TotalBytes,
                Math.Round(FileTransferMath.ProgressPercent(item.ReceivedBytes, item.TotalBytes), 1),
                item.Error,
                item.StartedAt,
                item.CompletedAt,
                item.TargetDir,
                await ResolveCollectedFileIdAsync(task, item, cancellationToken).ConfigureAwait(false)));
        }

        return new DistributionTaskDto(
            task.Id,
            task.Direction,
            task.FileId,
            task.FileName,
            task.FileSize,
            task.State,
            task.TargetDir,
            task.SourcePath,
            task.Note,
            task.CreatedBy,
            task.CreatedAt,
            dtos.Count,
            dtos.Count(x => x.State == DistributionItemStates.Completed),
            dtos.Count(x => x.State == DistributionItemStates.Failed),
            dtos);
    }

    /// <summary>
    /// 回传完成后，这一条的源文件在仓库里是哪一份。<b>读时派生，不落库</b>。
    ///
    /// <para>
    /// 匹配键是 <c>来源客户机 + 文件名 + 字节数</c>，取最近上传的那一条：
    /// 客户机回传时，服务端在 <c>CompleteUpload</c> 里已经强制 <c>kind=collected</c> 并写入
    /// <c>OriginAgentId</c>，所以这三个字段是可信的。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>不能用 <c>Path.GetFileName</c></b>：服务端可能跑在 Linux 上，而
    /// <c>SourcePath</c> 是 Windows 路径（<c>D:\logs\a.log</c>）—— 那时 <c>GetFileName</c>
    /// 会把整串原样返回（反斜杠在 Linux 上不是分隔符），匹配永远失败，
    /// 症状是"回传成功了但下载按钮永远不出现"。见 <see cref="LeafNameOf"/>。
    /// </para>
    /// </summary>
    private async Task<int?> ResolveCollectedFileIdAsync(
        DistributionTask task,
        DistributionItem item,
        CancellationToken cancellationToken)
    {
        if (task.Direction != DistributionDirections.Collect) return null;

        // 只有真的完成了才可能有仓库文件。失败 / 取消 / 传输中都不该给下载入口。
        if (item.State != DistributionItemStates.Completed) return null;

        var fileName = LeafNameOf(task.SourcePath);
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var files = await CollectedFilesAsync(cancellationToken).ConfigureAwait(false);

        var match = files
            .Where(f => f.OriginAgentId == item.AgentId)
            .Where(f => string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase))
            // `TotalBytes` 是客户机量出来的源文件大小；为 0 表示"没量到"，此时只按名字匹配。
            .Where(f => item.TotalBytes <= 0 || f.Size == item.TotalBytes)
            .OrderByDescending(f => f.UploadedAt)
            .FirstOrDefault();

        return match?.Id;
    }

    /// <summary>
    /// 本次请求内只查一次 <c>collected</c> 列表。
    /// <para>
    /// 一个列表请求里最多 50 个任务、每个任务若干明细 —— 每条都去查一遍全表会把
    /// "看一下进度"变成几十次数据库扫描，而这些行在同一瞬间毫无变化。
    /// </para>
    /// </summary>
    private Task<IReadOnlyList<FileRepoItem>>? _collectedFiles;

    private Task<IReadOnlyList<FileRepoItem>> CollectedFilesAsync(CancellationToken cancellationToken)
        // ⚠️ 刻意用 `CancellationToken.None`：缓存的是**共享的** task，
        //    若绑到第一个请求的 token 上，那个请求一取消，缓存就永远是个已取消的 task。
        => _collectedFiles ??= _files.ListAsync(FileKinds.Collected, CancellationToken.None);

    /// <summary>
    /// 取路径末段（文件名）。<b>同时认 <c>\</c> 与 <c>/</c></b>，不依赖运行平台的分隔符。
    /// </summary>
    private static string LeafNameOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var trimmed = path.Trim().TrimEnd('\\', '/');
        var index = trimmed.LastIndexOfAny(new[] { '\\', '/' });

        return index >= 0 ? trimmed[(index + 1)..] : trimmed;
    }
}
