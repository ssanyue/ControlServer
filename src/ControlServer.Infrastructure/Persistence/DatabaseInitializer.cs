using System.Data.Common;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using ControlServer.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 数据库初始化（M2 起改用 EF Migrations）。
/// <para>
/// <b>为什么不能继续用 <c>EnsureCreated()</c></b>：M1 时期用它是为了少依赖工具链，
/// 但它**不具备演进能力** —— 已存在的库不会新增表，M2 加表后老库直接运行期报错。
/// </para>
/// <para>
/// <b>两场景都要对</b>：
/// <list type="number">
///   <item><b>全新库</b>：<c>Migrate()</c> 一次建全（含基线）。</item>
///   <item>
///     <b>M1 老库</b>（EnsureCreated 建的，有 users/settings/audit_logs 但**没有迁移历史表**）：
///     先"<b>认领基线</b>"—— 往 <c>__EFMigrationsHistory</c> 补写一条 <c>InitialCreate</c> 记录，
///     再 <c>Migrate()</c>。这样 EF 只会增量执行 M2 的新表，不会去重复创建既有表。
///   </item>
/// </list>
/// </para>
/// </summary>
public sealed class DatabaseInitializer
{
    private const string UsersTable = "users";
    private const string HistoryTable = "__EFMigrationsHistory";

    private readonly AppDbContext _db;
    private readonly LogService _log;
    private readonly IPasswordHasher _hasher;

    public DatabaseInitializer(AppDbContext db, LogService log, IPasswordHasher hasher)
    {
        _db = db;
        _log = log;
        _hasher = hasher;
    }

    public async Task InitializeAsync(
        string defaultAdminUserName,
        string defaultAdminPassword,
        CancellationToken cancellationToken = default)
    {
        await AdoptBaselineIfLegacyDatabaseAsync(cancellationToken).ConfigureAwait(false);

        await _db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        // WAL 提高并发读写稳定性（服务化后多端并发）。
        await _db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);

        await SeedDefaultAdminAsync(defaultAdminUserName, defaultAdminPassword, cancellationToken).ConfigureAwait(false);

        await ClearStaleMustChangePasswordAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 若检测到"有业务表但没有迁移历史表"，说明这是 M1 时期 <c>EnsureCreated()</c> 建的库，
    /// 把基线迁移登记为已应用，避免 Migrate 重复建表。
    /// </summary>
    private async Task AdoptBaselineIfLegacyDatabaseAsync(CancellationToken cancellationToken)
    {
        var hasUsers = await TableExistsAsync(UsersTable, cancellationToken).ConfigureAwait(false);
        var hasHistory = await TableExistsAsync(HistoryTable, cancellationToken).ConfigureAwait(false);

        if (!hasUsers || hasHistory)
        {
            return;
        }

        var baselineId = _db.Database.GetMigrations().FirstOrDefault();
        if (baselineId is null)
        {
            _log.Warn("Database", "检测到旧库但迁移集合为空，跳过基线认领");
            return;
        }

        var history = _db.GetService<IHistoryRepository>();

        await _db.Database
            .ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), cancellationToken)
            .ConfigureAwait(false);

        await _db.Database
            .ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(baselineId, ProductInfo.GetVersion())), cancellationToken)
            .ConfigureAwait(false);

        _log.Warn("Database", $"检测到 M1 时期的旧库（无迁移历史表），已认领基线迁移 {baselineId}；仅增量应用后续迁移");
    }

    private async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;

        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = $name;";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = tableName;
            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture) > 0;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task SeedDefaultAdminAsync(
        string defaultAdminUserName,
        string defaultAdminPassword,
        CancellationToken cancellationToken)
    {
        if (await _db.Users.AnyAsync(cancellationToken).ConfigureAwait(false)) return;

        var (hash, salt) = _hasher.Hash(defaultAdminPassword);
        _db.Users.Add(new AppUser
        {
            UserName = defaultAdminUserName,
            PasswordHash = hash,
            Salt = salt,
            Role = "Admin",

            // 出厂不再强制改密（2026-10-09 起）：标记恒为 false，出厂口令只是"建议尽快改掉"，
            // 不再拦截任何接口。若确需恢复旧行为，把 Server:Auth:EnforcePasswordChange 置 true
            // 并在此改回 true（闸门机制仍在，见 ServerOptions.AuthOptions）。
            MustChangePassword = false,

            IsEnabled = true,

            // 显式赋值而不依赖实体默认值 —— 全应用时间口径统一为北京时间 UTC+8。
            CreatedAt = BeijingTime.Now,
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _log.Info("Database", $"已创建默认管理员 {defaultAdminUserName}（出厂口令，建议登录后自行修改）");
    }

    /// <summary>
    /// 清掉历史遗留的"必须先改密"标记（2026-10-09 起取消强制改密）。
    /// <para>
    /// 为什么必须有这一步：闸门默认关闭 + 不再有代码路径会置 <c>true</c>，
    /// <b>但升级上来的旧库</b>里还留着旧机制写下的 <c>true</c>（出厂管理员 / 旧版新建账号 / 旧版重置密码）。
    /// 那个标记不再拦接口，却仍会随登录响应与 <c>/api/auth/me</c> 下发给前端，
    /// 触发"关不掉"的强制改密弹窗 —— 于是"取消"就没做干净。
    /// </para>
    /// <para>
    /// 一次性、<b>幂等</b>（第二次启动已无 <c>true</c>，直接返回）；有存量时打 Warning 列出账号，
    /// 便于事后追溯。这是本项目唯一一处"启动时改用户数据"的写操作，语义明确且与产品决定一致。
    /// </para>
    /// </summary>
    private async Task ClearStaleMustChangePasswordAsync(CancellationToken cancellationToken)
    {
        var stale = await _db.Users
            .Where(u => u.MustChangePassword)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (stale.Count == 0)
        {
            return;
        }

        foreach (var user in stale)
        {
            user.MustChangePassword = false;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _log.Warn(
            "Database",
            $"已清除 {stale.Count} 个账号的历史遗留\"必须先改密\"标记（2026-10-09 起取消强制改密）："
            + string.Join("、", stale.Select(u => u.UserName)));
    }
}
