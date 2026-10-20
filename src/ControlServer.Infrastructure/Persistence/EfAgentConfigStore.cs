using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using ControlServer.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 基于 EF Core / SQLite 的客户机配置存储（M4.5）。
/// <para>
/// 与 <see cref="EfAgentHostStore"/> 同一模式：<b>Singleton + 内部开 scope</b>，
/// 写路径用一把进程内锁串行化。
/// </para>
/// <para>
/// <b>为什么写要串行化</b>：版本号是"读当前值再加一"。两条保存请求并发时，
/// 不加锁会双双读到 v3 并都写成 v4 —— 于是有一版的变更<b>被静默吞掉</b>，
/// 而客户端看到的版本号还前进了（它会认为"已经是最新"，永远不会再拉）。
/// </para>
/// </summary>
public sealed class EfAgentConfigStore : IAgentConfigStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public EfAgentConfigStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<AgentConfigRecord?> FindAsync(int agentId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.AgentConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AgentId == agentId, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyDictionary<int, int>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var pairs = await db.AgentConfigs
            .AsNoTracking()
            .Select(x => new { x.AgentId, x.Version })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return pairs.ToDictionary(x => x.AgentId, x => x.Version);
    }

    public async Task<AgentConfigRecord> SaveAsync(
        int agentId,
        AgentConfigContent content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var json = ControlJson.Serialize(content);
        var now = BeijingTime.Now;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = await db.AgentConfigs
                .FirstOrDefaultAsync(x => x.AgentId == agentId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                row = new AgentConfigEntity
                {
                    AgentId = agentId,
                    Version = 1,
                    UpdatedAtMs = TelemetryTime.ToMs(now),
                    Json = json,
                };

                db.AgentConfigs.Add(row);
            }
            else
            {
                row.Version += 1;
                row.UpdatedAtMs = TelemetryTime.ToMs(now);
                row.Json = json;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new AgentConfigRecord(row.AgentId, row.Version, now, content);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(int agentId, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = await db.AgentConfigs
                .FirstOrDefaultAsync(x => x.AgentId == agentId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null) return false;

            db.AgentConfigs.Remove(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ── 映射 ──

    /// <summary>
    /// 读一行。<b>坏 JSON 会抛</b>（与在线表的 <c>HostInfoJson</c> 刻意不同）：
    /// 在线表里一列坏 JSON 只影响"那一台机器的信息显示"，吞掉是对的；
    /// 而这里是**配置**，读不出来就不能假装它不存在 —— 静默降级成"空配置"
    /// 会让客户机被下发一份"什么都关掉"的配置（表现为"任务全没了"）。
    /// 让异常冒到调用方，由它决定是 500 还是"跳过这一台"。
    /// </summary>
    private static AgentConfigRecord ToDomain(AgentConfigEntity row)
    {
        var content = ControlJson.Deserialize<AgentConfigContent>(row.Json)
                      ?? throw new InvalidOperationException(
                          $"客户机配置（AgentId={row.AgentId}）内容为空或无法解析。");

        return new AgentConfigRecord(
            row.AgentId,
            row.Version,
            TelemetryTime.FromMs(row.UpdatedAtMs),
            content);
    }
}
