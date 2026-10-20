using ControlServer.Core.Abstractions;
using ControlServer.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 基于 EF Core / SQLite 的定时触发记录存储。
/// <para>
/// 与 <see cref="EfConfigDocumentStore"/> 同构：Singleton + <see cref="IServiceScopeFactory"/>
/// 开短生命周期 <see cref="AppDbContext"/>。
/// </para>
/// <para>
/// <b>幂等性的实现方式</b>：不"先查再插"（有竞态），而是**直接插**，
/// 靠 <c>PRIMARY KEY(ref_id, fire_date, fire_time)</c> 的唯一约束来判断是否抢到。
/// 这一步同时解决了三种重复来源：同秒内的多次 tick、服务重启后的重放、多实例并存。
/// </para>
/// </summary>
public sealed class EfTimerFireStore : ITimerFireStore
{
    /// <summary>SQLite 约束冲突的扩展错误码。</summary>
    private const int SqliteConstraintUnique = 2067;

    private const int SqliteConstraintPrimaryKey = 1555;

    private readonly IServiceScopeFactory _scopeFactory;

    public EfTimerFireStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<bool> TryClaimAsync(TimerFireEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.TimerFires.Add(new TimerFireRecord
        {
            RefId = entry.RefId,
            FireDate = entry.FireDate,
            FireTime = entry.FireTime,
            Source = entry.Source,
            FiredAt = entry.FiredAt,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (IsPrimaryKeyOrUniqueViolation(ex))
        {
            // 这个触发点已经写过了 —— 上一次 tick / 重启前 / 另一个实例。
            return false;
        }

        // 其它异常一律上抛。特别地，绝不能把"插入失败"笼统地当成"已触发"返回 false：
        // 那会让定时器静默失效，正是本模块要防的失败模式。
    }

    public async Task<IReadOnlyList<TimerFireEntry>> ListAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        var text = Format(date);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.TimerFires
            .AsNoTracking()
            .Where(x => x.FireDate == text)
            .OrderBy(x => x.RefId)
            .ThenBy(x => x.FireTime)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToEntry).ToList();
    }

    public async Task<int> PruneAsync(DateOnly keepFrom, CancellationToken cancellationToken = default)
    {
        var cutoff = Format(keepFrom);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 走原生 SQL 而不是 LINQ：FireDate 是 yyyy-MM-dd 定长文本，
        // SQLite 的默认 BINARY 排序恰好就是日期顺序；而 C# 里没有字符串的 < 运算符，
        // EF 无法把"字符串字典序比较"表达成可翻译的 LINQ。
        //
        // ⚠️ 表名与列名**必须从 EF 模型读**，不能硬编码：
        //    本项目没套用 snake_case 命名策略，实际列名是 PascalCase 的 "FireDate"。
        //    硬编码 "fire_date" 会让这条 SQL 在运行期报 "no such column" ——
        //    而它每天只跑一次、外层又把异常降级成 Warn，等于静默失效。
        //    这里读模型，将来改名/换命名策略都会自动跟上。
        var entity = db.Model.FindEntityType(typeof(TimerFireRecord))
                     ?? throw new InvalidOperationException($"{nameof(TimerFireRecord)} 未注册到 EF 模型。");

        var table = entity.GetTableName()
                    ?? throw new InvalidOperationException($"{nameof(TimerFireRecord)} 没有映射到任何表。");

        var column = entity.FindProperty(nameof(TimerFireRecord.FireDate))?.GetColumnName()
                     ?? throw new InvalidOperationException($"{nameof(TimerFireRecord.FireDate)} 未映射到任何列。");

        // 表名/列名来自代码而非用户输入，不存在注入面。
        var sql = $"DELETE FROM \"{table}\" WHERE \"{column}\" < {{0}}";

        return await db.Database
            .ExecuteSqlRawAsync(sql, new object[] { cutoff }, cancellationToken)
            .ConfigureAwait(false);
    }

    private static TimerFireEntry ToEntry(TimerFireRecord row)
        => new(row.RefId, row.FireDate, row.FireTime, row.Source, row.FiredAt);

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd");

    /// <summary>
    /// 只把"主键 / 唯一约束冲突"认定为"已触发过"。
    /// <para>
    /// 刻意**不**用「错误码 19（SQLITE_CONSTRAINT）就返回 false」的宽松写法：
    /// 那样任何约束问题（比如 NOT NULL 失败）都会被误判成"已经触发过"，
    /// 结果是定时器永远不触发且没有任何线索。
    /// </para>
    /// </summary>
    private static bool IsPrimaryKeyOrUniqueViolation(DbUpdateException ex)
    {
        if (ex.InnerException is not SqliteException sqlite)
        {
            return false;
        }

        return sqlite.SqliteExtendedErrorCode is SqliteConstraintPrimaryKey or SqliteConstraintUnique;
    }
}
