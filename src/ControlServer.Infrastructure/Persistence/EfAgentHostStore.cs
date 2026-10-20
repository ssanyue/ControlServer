using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using ControlServer.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 基于 EF Core / SQLite 的客户端在线表存储（M4.1）。
/// <para>
/// 注册为 <b>Singleton</b> + 内部每次开 scope 拿 Scoped 的 <c>DbContext</c>
/// —— 与 <see cref="EfConfigDocumentStore"/> 同一模式。
/// </para>
/// <para>
/// <b>为什么写路径全部串行化</b>：客户机可能在"所有人同时开机"的那一刻集中注册。
/// "按 MachineKey 查、没有就插"在并发下会插出两行，而唯一索引会让其中一台
/// <b>注册失败并报一个看不懂的 SQLite 错误</b>。这里用一把进程内的
/// <see cref="SemaphoreSlim"/> 把写路径串起来，唯一的代价是注册略微排队（毫秒级）。
/// </para>
/// </summary>
public sealed class EfAgentHostStore : IAgentHostStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public EfAgentHostStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<IReadOnlyList<AgentHost>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.AgentHosts
            .AsNoTracking()
            .OrderBy(x => x.DisplayName)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToDomain).ToList();
    }

    public async Task<AgentHost?> FindAsync(int id, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.AgentHosts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToDomain(row);
    }

    public async Task<AgentHost?> FindByMachineKeyAsync(string machineKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(machineKey)) return null;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.AgentHosts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.MachineKey == machineKey, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToDomain(row);
    }

    public async Task<AgentHost?> FindByAgentKeyHashAsync(string agentKeyHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentKeyHash)) return null;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.AgentHosts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AgentKeyHash == agentKeyHash, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToDomain(row);
    }

    public async Task<AgentHost> CreateAsync(AgentHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // 并发注册：另一条线已经抢先插入了同一台机器。
            // 这里不抛异常，而是把"我这份写作废"退化成"返回已有的那一行" ——
            // 两台机器同时开机时，谁先谁后对用户毫无区别，但报错会让其中一台停留在
            // "注册失败"，而它其实完全正常。
            var existing = await db.AgentHosts
                .FirstOrDefaultAsync(x => x.MachineKey == host.MachineKey, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null) return ToDomain(existing);

            var entity = new AgentHostEntity
            {
                MachineKey = host.MachineKey,
                DisplayName = host.DisplayName,
                Group = host.Group,
                Note = host.Note,
                AgentKeyHash = host.AgentKeyHash,
                AgentVersion = host.AgentVersion,
                LastAddress = host.LastAddress,
                RegisteredAt = host.RegisteredAt,
                LastSeenAt = host.LastSeenAt,
                IsBlocked = host.IsBlocked,
                HostInfoJson = SerializeInfo(host.Info),
                AppliedConfigVersion = host.AppliedConfigVersion,
                LastTargetDir = host.LastTargetDir,
                LastSourceDir = host.LastSourceDir,
            };

            db.AgentHosts.Add(entity);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return ToDomain(entity);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> UpdateRegistrationAsync(
        int id,
        string agentKeyHash,
        string? agentVersion,
        string? address,
        AgentHostInfoDto? info,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = await db.AgentHosts
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (row is null) return false;

            row.AgentKeyHash = agentKeyHash;
            row.AgentVersion = agentVersion ?? row.AgentVersion;
            row.LastAddress = address ?? row.LastAddress;

            // ⚠️ 刻意**不动** `LastSeenAt`：重新注册只是"重新握手"，
            //    不是一次心跳。写它会让"最后一次心跳"变成谎言，详见
            //    AgentEnrollmentService 里同一处决策的注释。
            //    上一次真实心跳的时间点保留下来，反而更准确 ——
            //    它能让页面正确显示"这台机器 3 小时前在线过，现在重新注册了但还没连上"。

            // ⚠️ 2026-10-15 起**不再清「拉黑」标记**（改之前这里会 `row.IsBlocked = false`）。
            //
            // 旧注释的理由是"保留标记会造出'重装后永远连不上、还没有任何出路'的死结"。
            // 那是**针对旧「吊销」**说的：旧吊销在认证层就把 agentKey 清空了，
            // 于是那台机器只能靠重新注册回来，而重新注册又被这一行挡着 —— 死结成立。
            //
            // 现在结构变了，死结不成立：
            //   · 被拉黑的机器**连注册都进不来**（`AgentEnrollmentService` 那一层直接拒），
            //     所以这行代码根本走不到被拉黑的机器；
            //   · 拉黑**不清 agentKey**，所以"取消拉黑"之后不需要重新注册，下一次重试就连上；
            //   · 于是"我要让它回来"这个诉求由「取消拉黑」满足，不需要靠重注册偷偷复活。
            // 换句话说：**拉黑是管理员说了算的开关，不再是一个能被客户机自己绕过的标记。**

            if (info is not null)
            {
                row.HostInfoJson = SerializeInfo(info);
                row.DisplayName = string.IsNullOrWhiteSpace(info.HostName) ? row.DisplayName : info.HostName;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> TouchAsync(
        int id,
        DateTimeOffset seenAt,
        string? address,
        string? agentVersion,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = await db.AgentHosts
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (row is null) return false;

            row.LastSeenAt = seenAt;
            if (!string.IsNullOrWhiteSpace(address)) row.LastAddress = address;
            if (!string.IsNullOrWhiteSpace(agentVersion)) row.AgentVersion = agentVersion;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> UpdateDetailsAsync(
        int id,
        string? displayName,
        string? group,
        string? note,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = await db.AgentHosts
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (row is null) return false;

            // null = "不改这一项"，空串 = "清空这一项"。两者语义不同，
            // 前端把"没填"与"填了空"区分开后，这里才能忠实执行。
            if (displayName is not null)
            {
                var trimmed = displayName.Trim();
                if (trimmed.Length > 0) row.DisplayName = trimmed;
            }

            if (group is not null) row.Group = Nullify(group);
            if (note is not null) row.Note = Nullify(note);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> SetBlockedAsync(int id, bool blocked, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = await db.AgentHosts
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (row is null) return false;

            row.IsBlocked = blocked;

            // 🔥 **刻意不动 `AgentKeyHash`**（旧吊销会把它清成 null）。
            //
            // 清哈希确实"更彻底"（那份凭据从此在库里不存在），但它把「取消拉黑」变成了
            // "请那台机器重新注册一次" —— 而需求明确要的是**取消后立即恢复连接**：
            // 密钥还在 + 认证时按标记拒绝 ⇒ 解除标记的下一秒，那台机器正常的心跳重试
            // 就直接连上了（客户端不需要任何额外动作）。
            //
            // 安全性没有损失：`AgentKeyAuthenticationHandler` 是**每一类请求**的必经之路
            // （REST 与长连接都在它后面），它看到 IsBlocked 一律 Fail ⇒ 被拉黑的机器
            // 拿不到任何一个端点。挡住它的力量在"标记"，不在"哈希存不存在"。

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> SetAppliedConfigVersionAsync(int id, int version, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // ⚠️ 用 SQL 的 MAX() 而不是"读出来比一下再写"：后者在语义上是
            //    "只增不减"由调用顺序保证，而这里让它由**数据库**保证 ——
            //    迟到的旧回报（重发 / 乱序）无论何时到达都改不动已经更大的值。
            //    COALESCE 是因为新列的初值是 NULL，而 SQLite 的 max(NULL, 3) 是 NULL。
            var affected = await db.Database
                .ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE agent_hosts
                     SET AppliedConfigVersion = MAX(COALESCE(AppliedConfigVersion, -1), {version})
                     WHERE Id = {id}
                     """,
                    cancellationToken)
                .ConfigureAwait(false);

            return affected > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> SetLastPathAsync(
        int id,
        string? targetDir,
        string? sourceDir,
        CancellationToken cancellationToken = default)
    {
        // 两个方向都没给就什么都不用做 —— 发一条空 UPDATE 只是白占写锁。
        if (targetDir is null && sourceDir is null) return false;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // ⚠️ `CASE WHEN @p IS NULL THEN 旧值 ELSE @p END` = "传 null 就不动这一项"。
            //    用一条 SQL 同时支持"只记下发方向""只记回传方向""两个都记"，
            //    免得调用方为了"只改一个"去读一遍再写一遍（那会与并发写入互相覆盖）。
            var affected = await db.Database
                .ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE agent_hosts
                     SET LastTargetDir = CASE WHEN {targetDir} IS NULL THEN LastTargetDir ELSE {targetDir} END,
                         LastSourceDir = CASE WHEN {sourceDir} IS NULL THEN LastSourceDir ELSE {sourceDir} END
                     WHERE Id = {id}
                     """,
                    cancellationToken)
                .ConfigureAwait(false);

            return affected > 0;
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

            var row = await db.AgentHosts
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (row is null) return false;

            db.AgentHosts.Remove(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ── 映射 ──

    private static AgentHost ToDomain(AgentHostEntity row)
        => new(
            row.Id,
            row.MachineKey,
            row.DisplayName,
            row.Group,
            row.Note,
            row.AgentKeyHash,
            row.AgentVersion,
            row.LastAddress,
            row.RegisteredAt,
            row.LastSeenAt,
            row.IsBlocked,
            DeserializeInfo(row.HostInfoJson),
            row.AppliedConfigVersion,
            row.LastTargetDir,
            row.LastSourceDir);

    private static string? SerializeInfo(AgentHostInfoDto? info)
        => info is null ? null : ControlJson.Serialize(info);

    /// <summary>
    /// 反序列化主机信息。<b>吞异常返回 null</b>：一列坏 JSON 不该让整张在线表打不开
    /// （那会让"改个备注"都做不了）。坏数据由列表页显示为"信息缺失"。
    /// </summary>
    private static AgentHostInfoDto? DeserializeInfo(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return ControlJson.Deserialize<AgentHostInfoDto>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? Nullify(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
