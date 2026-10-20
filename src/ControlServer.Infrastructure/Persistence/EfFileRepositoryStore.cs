using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 基于 EF Core / SQLite 的文件仓库存储（M4.4）。
/// <para>与 <c>EfAgentHostStore</c> / <c>EfAgentTelemetryStore</c> 同一模式：Singleton + 内部开 scope。</para>
/// <para>
/// <b>只碰元数据行</b>；字节的读写在 Core 的 <c>FileRepositoryService</c>。因此这里的每个方法
/// 都短得很 —— 这也正是这么分层的目的。
/// </para>
/// </summary>
public sealed class EfFileRepositoryStore : IFileRepositoryStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public EfFileRepositoryStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    // ═══════════════════════════════════════════════════════════
    // 仓库条目
    // ═══════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<FileRepoItem>> ListAsync(
        string? kind,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var query = db.FileRepoItems.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(x => x.Kind == kind);

        var rows = await query
            .OrderByDescending(x => x.UploadedAtMs)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToModel).ToList();
    }

    public async Task<FileRepoItem?> FindAsync(int id, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.FileRepoItems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToModel(row);
    }

    public async Task<FileRepoItem> AddAsync(FileRepoItem item, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = new FileRepoItemEntity
            {
                // Id 由数据库生成（传入值的 Id 刻意忽略）。
                Name = item.Name,
                StoredName = item.StoredName,
                Size = item.Size,
                Sha256 = item.Sha256,
                Kind = item.Kind,
                UploadedBy = item.UploadedBy,
                UploadedAtMs = TelemetryTime.ToMs(item.UploadedAt),
                OriginAgentId = item.OriginAgentId,
                Note = item.Note,
            };

            db.FileRepoItems.Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return ToModel(row);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = await db.FileRepoItems
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (row is null) return false;

            db.FileRepoItems.Remove(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 上传会话
    // ═══════════════════════════════════════════════════════════

    public async Task CreateSessionAsync(FileUploadSession session, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            db.FileUploadSessions.Add(new FileUploadSessionEntity
            {
                Id = session.Id,
                FileName = session.FileName,
                StoredName = session.StoredName,
                Size = session.Size,
                Kind = session.Kind,
                Sha256 = session.Sha256,
                CreatedBy = session.CreatedBy,
                OriginAgentId = session.OriginAgentId,
                CreatedAtMs = TelemetryTime.ToMs(session.CreatedAt),
                ExpiresAtMs = TelemetryTime.ToMs(session.ExpiresAt),
                State = session.State,
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<FileUploadSession?> FindSessionAsync(string id, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.FileUploadSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToModel(row);
    }

    public async Task<IReadOnlyList<FileUploadSession>> ListZombieSessionsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var nowMs = TelemetryTime.ToMs(now);

        // 过期的，或状态已经不在 open 上的（例如被标记 expired）—— 两类都该回收。
        var rows = await db.FileUploadSessions
            .AsNoTracking()
            .Where(x => x.ExpiresAtMs < nowMs || x.State != FileUploadStates.Open)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<FileUploadSession>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 不加过滤：调用方（对账）要的是"哪些 .part 有主人"，
        // 少返回一条活会话就会让一个正在上传的文件被当成孤儿删掉。
        var rows = await db.FileUploadSessions
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToModel).ToList();
    }

    public async Task<bool> DeleteSessionAsync(string id, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = await db.FileUploadSessions
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (row is null) return false;

            db.FileUploadSessions.Remove(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 映射
    // ═══════════════════════════════════════════════════════════

    private static FileRepoItem ToModel(FileRepoItemEntity row) => new(
        row.Id,
        row.Name,
        row.StoredName,
        row.Size,
        row.Sha256,
        row.Kind,
        row.UploadedBy,
        TelemetryTime.FromMs(row.UploadedAtMs),
        row.OriginAgentId,
        row.Note);

    private static FileUploadSession ToModel(FileUploadSessionEntity row) => new(
        row.Id,
        row.FileName,
        row.StoredName,
        row.Size,
        row.Kind,
        row.Sha256,
        row.CreatedBy,
        row.OriginAgentId,
        TelemetryTime.FromMs(row.CreatedAtMs),
        TelemetryTime.FromMs(row.ExpiresAtMs),
        row.State);
}
