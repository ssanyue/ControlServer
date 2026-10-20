using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>新建分发任务的结果。</summary>
public sealed record DistributionCreateResult(
    bool Ok,
    string Status,
    string? Message,
    DistributionTask? Task)
{
    public static DistributionCreateResult Success(DistributionTask task) => new(true, "ok", null, task);

    public static DistributionCreateResult Fail(string status, string message) => new(false, status, message, null);
}

/// <summary>
/// 文件分发服务（M4.4）：建任务、记进度、汇总总状态、推事件。
/// <para>
/// <b>只做"业务编排"</b>，三件事都委托出去：
/// 元数据落库 → <see cref="IDistributionStore"/>；文件是否存在于仓库 → <see cref="FileRepositoryService"/>；
/// 给网页推进度 → <see cref="INotifier"/>。
/// 因此它可以在没有 ASP.NET Core、没有 SignalR 的环境里被单测（Agent 未来也能复用它的一部分）。
/// </para>
/// </summary>
public sealed class DistributionService
{
    private readonly IDistributionStore _store;
    private readonly FileRepositoryService _files;
    private readonly INotifier _notifier;
    private readonly LogService _log;

    public DistributionService(
        IDistributionStore store,
        FileRepositoryService files,
        INotifier notifier,
        LogService log)
    {
        _store = store;
        _files = files;
        _notifier = notifier;
        _log = log;
    }

    // ═══════════════════════════════════════════════════════════
    // 建任务
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 新建一个<b>下发</b>任务（仓库文件 → 一批客户机）。
    /// <para>
    /// <b>每台目标可以各带一个落地目录</b>（2026-10-13 起）：目录落在明细上，
    /// 派发时逐台取用。整批只有一个目录时才会顺手记到任务级（列表页摘要用）。
    /// </para>
    /// </summary>
    public async Task<DistributionCreateResult> CreatePushAsync(
        int fileId,
        IReadOnlyList<DistributionTargetRequest>? targets,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        var file = await _files.FindAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file is null) return DistributionCreateResult.Fail("notfound", "仓库里没有这个文件（可能已被删除）。");

        var resolved = NormalizeTargets(targets);
        if (resolved.Count == 0) return DistributionCreateResult.Fail("invalid", "请至少选择一台客户机。");

        var task = new DistributionTask(
            Id: 0,
            Direction: DistributionDirections.Push,
            FileId: file.Id,
            FileName: file.Name,
            FileSize: file.Size,
            State: DistributionStates.Pending,
            TargetDir: CommonTargetDir(resolved),
            SourcePath: null,
            Note: null,
            CreatedBy: createdBy,
            CreatedAt: BeijingTime.Now);

        var saved = await _store.CreateTaskAsync(task, resolved, cancellationToken).ConfigureAwait(false);

        _log.Info(
            "Files",
            $"新建下发任务 #{saved.Id}：{file.Name}（{file.Size} B）→ {resolved.Count} 台客户机"
            + $"，落地目录 {DescribeDirs(resolved)}");

        return DistributionCreateResult.Success(saved);
    }

    /// <summary>新建一个<b>回传</b>任务（一批客户机上的某个文件 → 仓库）。</summary>
    public async Task<DistributionCreateResult> CreateCollectAsync(
        IReadOnlyList<int>? targetAgentIds,
        string? sourcePath,
        string? note,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return DistributionCreateResult.Fail("invalid", "请填写要从客户机取回的源路径。");

        // 回传没有"目标目录"，所以每台的目标目录都是 null（由客户端按白名单校验源路径）。
        var targets = Deduplicate(targetAgentIds)
            .Select(id => new DistributionTarget(id, null))
            .ToList();

        if (targets.Count == 0) return DistributionCreateResult.Fail("invalid", "请至少选择一台客户机。");

        var trimmed = sourcePath.Trim();

        var task = new DistributionTask(
            Id: 0,
            Direction: DistributionDirections.Collect,
            FileId: 0, // 回传没有"源仓库文件"
            FileName: FileTransferMath.SanitizeFileName(trimmed),
            FileSize: 0, // 要等客户端量出源文件大小才知道
            State: DistributionStates.Pending,
            TargetDir: null,
            SourcePath: trimmed,
            Note: string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedBy: createdBy,
            CreatedAt: BeijingTime.Now);

        var saved = await _store.CreateTaskAsync(task, targets, cancellationToken).ConfigureAwait(false);

        _log.Info("Files", $"新建回传任务 #{saved.Id}：{trimmed} ← {targets.Count} 台客户机");

        return DistributionCreateResult.Success(saved);
    }

    // ═══════════════════════════════════════════════════════════
    // 查询
    // ═══════════════════════════════════════════════════════════

    public Task<DistributionTask?> FindTaskAsync(int id, CancellationToken cancellationToken = default)
        => _store.FindTaskAsync(id, cancellationToken);

    public Task<IReadOnlyList<DistributionTask>> ListTasksAsync(int limit, CancellationToken cancellationToken = default)
        => _store.ListTasksAsync(limit, cancellationToken);

    public Task<IReadOnlyList<DistributionItem>> ListItemsAsync(int taskId, CancellationToken cancellationToken = default)
        => _store.ListItemsAsync(taskId, cancellationToken);

    public Task<IReadOnlyList<DistributionItem>> ListItemsForTasksAsync(
        IReadOnlyList<int> taskIds,
        CancellationToken cancellationToken = default)
        => _store.ListItemsForTasksAsync(taskIds, cancellationToken);

    /// <summary>某台机器还没完成的传输条目（客户端上线时补领任务）。</summary>
    public Task<IReadOnlyList<DistributionItem>> ListOpenItemsAsync(int agentId, CancellationToken cancellationToken = default)
        => _store.ListOpenItemsAsync(agentId, cancellationToken);

    // ═══════════════════════════════════════════════════════════
    // 进度与结果（客户机回报）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 记一条进度。
    /// <para>
    /// <b>幂等且不倒退</b>：终态条目直接原样返回（重试重复上报不会把"已完成"改回"传输中"）；
    /// 已记录的字节数只增不减（乱序到达的旧进度会把进度条往回拽）。
    /// </para>
    /// </summary>
    public async Task<DistributionItem?> ReportProgressAsync(
        int itemId,
        int agentId,
        long receivedBytes,
        long totalBytes,
        string? error,
        CancellationToken cancellationToken = default)
    {
        var item = await FindOwnedItemAsync(itemId, agentId, cancellationToken).ConfigureAwait(false);
        if (item is null) return null;

        if (IsTerminal(item.State)) return item;

        var updated = item with
        {
            State = DistributionItemStates.Running,
            ReceivedBytes = Math.Max(item.ReceivedBytes, Math.Max(0, receivedBytes)),
            TotalBytes = totalBytes > 0 ? totalBytes : item.TotalBytes,
            StartedAt = item.StartedAt ?? BeijingTime.Now,
            // 进度里带来的 error 是"可恢复的提示"（例如重试原因），记下来但不算失败。
            Error = error,
        };

        await _store.UpdateItemAsync(updated, cancellationToken).ConfigureAwait(false);
        await RefreshTaskStateAsync(updated.TaskId, cancellationToken).ConfigureAwait(false);
        await NotifyAsync(updated, cancellationToken).ConfigureAwait(false);

        return updated;
    }

    /// <summary>记一条最终结果（成功 / 失败）。</summary>
    public async Task<DistributionItem?> CompleteItemAsync(
        int itemId,
        int agentId,
        bool ok,
        long receivedBytes,
        string? sha256,
        string? error,
        CancellationToken cancellationToken = default)
    {
        var item = await FindOwnedItemAsync(itemId, agentId, cancellationToken).ConfigureAwait(false);
        if (item is null) return null;

        // 已经终结过就不再改写：客户端重连后重复上报"完成"是常态。
        if (IsTerminal(item.State)) return item;

        var total = item.TotalBytes > 0 ? item.TotalBytes : Math.Max(receivedBytes, 0);

        var updated = ok
            ? item with
            {
                State = DistributionItemStates.Completed,
                ReceivedBytes = Math.Max(receivedBytes, 0),
                TotalBytes = total,
                Sha256 = sha256,
                Error = null,
                StartedAt = item.StartedAt ?? BeijingTime.Now,
                CompletedAt = BeijingTime.Now,
            }
            : item with
            {
                State = DistributionItemStates.Failed,
                ReceivedBytes = Math.Max(item.ReceivedBytes, Math.Max(receivedBytes, 0)),
                TotalBytes = total,
                Error = string.IsNullOrWhiteSpace(error) ? "客户端未说明失败原因。" : error.Trim(),
                StartedAt = item.StartedAt ?? BeijingTime.Now,
                CompletedAt = BeijingTime.Now,
            };

        await _store.UpdateItemAsync(updated, cancellationToken).ConfigureAwait(false);

        var taskState = await RefreshTaskStateAsync(updated.TaskId, cancellationToken).ConfigureAwait(false);

        if (!ok)
        {
            _log.Warn("Files", $"传输失败（任务 #{updated.TaskId} / 客户机 {agentId}）：{updated.Error}");
        }
        else if (DistributionStates.IsTerminal(taskState))
        {
            _log.Info("Files", $"传输任务 #{updated.TaskId} 已到终态：{DistributionStates.DisplayName(taskState)}");
        }

        await NotifyAsync(updated, cancellationToken).ConfigureAwait(false);

        return updated;
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    private async Task<DistributionItem?> FindOwnedItemAsync(int itemId, int agentId, CancellationToken cancellationToken)
    {
        var item = await _store.FindItemAsync(itemId, cancellationToken).ConfigureAwait(false);

        // ⚠️ 归属校验：客户机只能动自己那一行。少了这一步，任何一台拿到 AgentKey 的机器
        //    都能把别人的传输标成"完成"或"失败"。
        if (item is null || item.AgentId != agentId) return null;

        return item;
    }

    private static bool IsTerminal(string state)
        => state is DistributionItemStates.Completed or DistributionItemStates.Failed or DistributionItemStates.Canceled;

    private async Task<string> RefreshTaskStateAsync(int taskId, CancellationToken cancellationToken)
    {
        var items = await _store.ListItemsAsync(taskId, cancellationToken).ConfigureAwait(false);
        var states = items.Select(i => i.State).ToList();
        var state = FileTransferMath.SummarizeTaskState(states);

        await _store.UpdateTaskStateAsync(taskId, state, cancellationToken).ConfigureAwait(false);

        return state;
    }

    private Task NotifyAsync(DistributionItem item, CancellationToken cancellationToken)
        => _notifier.TransferProgressAsync(
            new TransferProgressDto(
                item.TaskId,
                item.Id,
                item.AgentId,
                item.State,
                item.ReceivedBytes,
                item.TotalBytes,
                Math.Round(FileTransferMath.ProgressPercent(item.ReceivedBytes, item.TotalBytes), 1),
                item.Error),
            cancellationToken);

    private static IReadOnlyList<int> Deduplicate(IReadOnlyList<int>? ids)
    {
        if (ids is null) return Array.Empty<int>();

        var set = new List<int>();

        foreach (var id in ids)
        {
            if (id > 0 && !set.Contains(id)) set.Add(id);
        }

        return set;
    }

    /// <summary>
    /// 目标清单归一化：丢掉非法 id、按 id 去重（<b>先出现的赢</b>）、目录去空白。
    /// <para>
    /// <b>为什么要去重</b>：一个任务里同一台机器只能有一行明细（数据库有唯一索引），
    /// 脏数据（同一台传两次）应该在<b>入库之前</b>就被挡掉 ——
    /// 否则表现是一个 500，而原因只是一次重复勾选。
    /// </para>
    /// </summary>
    private static IReadOnlyList<DistributionTarget> NormalizeTargets(IReadOnlyList<DistributionTargetRequest>? targets)
    {
        if (targets is null) return Array.Empty<DistributionTarget>();

        var seen = new HashSet<int>();
        var list = new List<DistributionTarget>(targets.Count);

        foreach (var target in targets)
        {
            if (target.AgentId <= 0 || !seen.Add(target.AgentId)) continue;

            var dir = string.IsNullOrWhiteSpace(target.TargetDir) ? null : target.TargetDir.Trim();

            list.Add(new DistributionTarget(target.AgentId, dir));
        }

        return list;
    }

    /// <summary>
    /// 任务级目录：<b>只有全部目标都用同一个（非空）目录时</b>才有值，否则 null。
    /// <para>它仅供任务列表的一行摘要使用；真正的落点在每条明细上。</para>
    /// </summary>
    private static string? CommonTargetDir(IReadOnlyList<DistributionTarget> targets)
    {
        if (targets.Count == 0) return null;

        var first = targets[0].TargetDir;
        if (string.IsNullOrEmpty(first)) return null;

        return targets.All(t => string.Equals(t.TargetDir, first, StringComparison.OrdinalIgnoreCase))
            ? first
            : null;
    }

    /// <summary>日志里那句"落到哪儿"：一个目录就写它，多个就写"各自指定（N 种）"。</summary>
    private static string DescribeDirs(IReadOnlyList<DistributionTarget> targets)
    {
        var distinct = targets
            .Select(t => t.TargetDir ?? "(默认)")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return distinct.Count == 1 ? distinct[0] : $"各自指定（{distinct.Count} 种）";
    }
}
