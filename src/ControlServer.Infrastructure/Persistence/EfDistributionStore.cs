using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 基于 EF Core / SQLite 的分发任务存储（M4.4）。
/// <para>Singleton + 内部 scope，与其余 EF 存储同模式。</para>
/// <para>
/// <b>进度更新是高频写</b>（100+ 台机器传输时，每台每 5% 一次）：整条链都走
/// <see cref="_writeLock"/> 串行化 —— 单条 UPDATE 很短，排队几毫秒无人察觉，
/// 而一次 SQLITE_BUSY 就是这个任务卡住不动。与 M4.2 的取舍理由一致。
/// </para>
/// </summary>
public sealed class EfDistributionStore : IDistributionStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public EfDistributionStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<DistributionTask> CreateTaskAsync(
        DistributionTask task,
        IReadOnlyList<DistributionTarget> targets,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = new DistributionTaskEntity
            {
                Direction = task.Direction,
                FileId = task.FileId,
                FileName = task.FileName,
                FileSize = task.FileSize,
                State = task.State,
                TargetDir = task.TargetDir,
                SourcePath = task.SourcePath,
                Note = task.Note,
                CreatedBy = task.CreatedBy,
                CreatedAtMs = TelemetryTime.ToMs(task.CreatedAt),
            };

            // 任务与它的明细必须在**同一个事务**里落。
            // 分两次提交（或不加事务）会出现一个窗口：任务已经存在、明细一行都没有 ——
            // 此时任务总览页打开就会显示"0 个目标"，看起来像派发失败了。
            // 这里刻意**不建外键**（理由同遥测表：移除设备时我们主动清理，不想被约束挡住），
            // 因此需要先插入任务拿到自增 Id，再拿它建明细 —— 两步包在一个事务里即可。
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            db.DistributionTasks.Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            foreach (var target in targets)
            {
                db.DistributionItems.Add(new DistributionItemEntity
                {
                    TaskId = row.Id,
                    AgentId = target.AgentId,
                    State = DistributionItemStates.Pending,
                    ReceivedBytes = 0,
                    TotalBytes = task.Direction == DistributionDirections.Push ? task.FileSize : 0,
                    // ⚠️ 逐台写各自的目录（空 = 用那台机器的默认目录）——
                    //    绝不在这里回填 task.TargetDir：那样"每台一个目录"就退化成"共用一个目录"了。
                    TargetDir = target.TargetDir,
                });
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return ToModel(row);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<DistributionTask?> FindTaskAsync(int id, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.DistributionTasks
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToModel(row);
    }

    public async Task<IReadOnlyList<DistributionTask>> ListTasksAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.DistributionTasks
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAtMs)
            .ThenByDescending(x => x.Id)
            .Take(Math.Max(1, limit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<DistributionItem>> ListItemsAsync(
        int taskId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.DistributionItems
            .AsNoTracking()
            .Where(x => x.TaskId == taskId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToModel).ToList();
    }

    public async Task<DistributionItem?> FindItemAsync(int itemId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.DistributionItems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == itemId, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToModel(row);
    }

    public async Task<IReadOnlyList<DistributionItem>> ListOpenItemsAsync(
        int agentId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.DistributionItems
            .AsNoTracking()
            .Where(x => x.AgentId == agentId
                        && (x.State == DistributionItemStates.Pending || x.State == DistributionItemStates.Running))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<DistributionItem>> ListItemsForTasksAsync(
        IReadOnlyList<int> taskIds,
        CancellationToken cancellationToken = default)
    {
        if (taskIds.Count == 0) return Array.Empty<DistributionItem>();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.DistributionItems
            .AsNoTracking()
            .Where(x => taskIds.Contains(x.TaskId))
            .OrderBy(x => x.TaskId)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToModel).ToList();
    }

    public async Task UpdateItemAsync(DistributionItem item, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = await db.DistributionItems
                .FirstOrDefaultAsync(x => x.Id == item.Id, cancellationToken)
                .ConfigureAwait(false);

            if (row is null) return;

            row.State = item.State;
            row.ReceivedBytes = item.ReceivedBytes;
            row.TotalBytes = item.TotalBytes;
            row.Error = item.Error;
            row.Sha256 = item.Sha256;
            row.StartedAtMs = item.StartedAt is { } s ? TelemetryTime.ToMs(s) : null;
            row.CompletedAtMs = item.CompletedAt is { } c ? TelemetryTime.ToMs(c) : null;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpdateTaskStateAsync(int taskId, string state, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = await db.DistributionTasks
                .FirstOrDefaultAsync(x => x.Id == taskId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null) return;

            row.State = state;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> PurgeTerminalTasksAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var cutoffMs = TelemetryTime.ToMs(cutoff);

            var rows = await db.DistributionTasks
                .Where(x => x.CreatedAtMs < cutoffMs)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // 只清"已终结"的：还在等离线机器上线的任务，再老也不能删。
            var doomed = rows.Where(x => DistributionStates.IsTerminal(x.State)).ToList();
            if (doomed.Count == 0) return 0;

            var ids = doomed.Select(x => x.Id).ToList();

            var items = await db.DistributionItems
                .Where(x => ids.Contains(x.TaskId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            db.DistributionItems.RemoveRange(items);
            db.DistributionTasks.RemoveRange(doomed);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return doomed.Count;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 映射
    // ═══════════════════════════════════════════════════════════

    private static DistributionTask ToModel(DistributionTaskEntity row) => new(
        row.Id,
        row.Direction,
        row.FileId,
        row.FileName,
        row.FileSize,
        row.State,
        row.TargetDir,
        row.SourcePath,
        row.Note,
        row.CreatedBy,
        TelemetryTime.FromMs(row.CreatedAtMs));

    private static DistributionItem ToModel(DistributionItemEntity row) => new(
        row.Id,
        row.TaskId,
        row.AgentId,
        row.State,
        row.ReceivedBytes,
        row.TotalBytes,
        row.Error,
        row.Sha256,
        row.StartedAtMs is { } s ? TelemetryTime.FromMs(s) : null,
        row.CompletedAtMs is { } c ? TelemetryTime.FromMs(c) : null,
        row.TargetDir);
}
