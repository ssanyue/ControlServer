using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using ControlServer.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 基于 EF Core / SQLite 的配置文档存储。
/// <para>
/// 为什么注册为 <b>Singleton</b> 却注入 <see cref="IServiceScopeFactory"/>：
/// 乐观锁的串行化必须**进程级唯一**（一个 <see cref="SemaphoreSlim"/>），
/// 而 <see cref="AppDbContext"/> 是 Scoped。Singleton 持锁 + 每次操作开新 scope，
/// 是"进程级串行化 + 短生命周期 DbContext"的标准组合。
/// </para>
/// <para>
/// 稳定性约定：这里不做任何吞异常。写失败必须上抛，由上层决定是 500 还是回滚，
/// 绝不允许"以为写成功了其实没写"。
/// </para>
/// </summary>
public sealed class EfConfigDocumentStore : IConfigDocumentStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public EfConfigDocumentStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<ConfigDocumentSnapshot> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.ConfigDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == key, cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? ConfigDocumentSnapshot.Missing(key)
            : new ConfigDocumentSnapshot(row.Key, row.Value, row.Version, row.UpdatedAt, row.UpdatedBy);
    }

    public async Task<int> WriteAsync(
        string key,
        string value,
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = await db.ConfigDocuments
                .FirstOrDefaultAsync(x => x.Key == key, cancellationToken)
                .ConfigureAwait(false);

            var actualVersion = row?.Version ?? 0;

            if (expectedVersion.HasValue && expectedVersion.Value != actualVersion)
            {
                throw new ConfigConcurrencyException(key, expectedVersion.Value, actualVersion);
            }

            var now = BeijingTime.Now;

            if (row is null)
            {
                row = new ConfigDocument
                {
                    Key = key,
                    Value = value,
                    Version = 1,
                    UpdatedAt = now,
                    UpdatedBy = updatedBy ?? "system",
                };
                db.ConfigDocuments.Add(row);
            }
            else
            {
                row.Value = value;
                row.Version += 1;
                row.UpdatedAt = now;
                row.UpdatedBy = updatedBy ?? "system";
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return row.Version;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.ConfigDocuments
            .AsNoTracking()
            .OrderBy(x => x.Key)
            .Select(x => x.Key)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = await db.ConfigDocuments
                .FirstOrDefaultAsync(x => x.Key == key, cancellationToken)
                .ConfigureAwait(false);

            if (row is null) return;

            db.ConfigDocuments.Remove(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
