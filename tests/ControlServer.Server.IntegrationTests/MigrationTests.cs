using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 数据库迁移回归 —— 覆盖 M2 方案 §4.4 点名的**两场景**：
/// <list type="number">
///   <item><b>全新库</b>：Migrate 一次建全（含基线）。</item>
///   <item>
///     <b>M1 老库</b>：M1 时期用 <c>EnsureCreated()</c> 建的库没有迁移历史表，
///     直接 <c>Migrate()</c> 会因"表已存在"失败。必须走"<b>基线认领</b>"后只增量建新表。
///   </item>
/// </list>
/// </summary>
public sealed class MigrationTests
{
    private const string InitialCreateMigration = "20261007050617_InitialCreate";

    // ═══════════════════════════════════════════════════════════
    // 场景一：全新库
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Fresh_database_is_created_with_all_tables_and_migration_history()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();
        await client.GetAsync("/healthz");

        var tables = ReadTableNames(factory.DatabaseFile);

        tables.Should().Contain("users");
        tables.Should().Contain("settings");
        tables.Should().Contain("audit_logs");
        tables.Should().Contain("config_documents");
        tables.Should().Contain("timer_fires");
        tables.Should().Contain("agent_hosts", "M4 的客户端在线表");
        tables.Should().Contain(
            "enrollment_tokens",
            "M4 的准入令牌表 —— 2026-10-13 免凭据准入后已无任何读写方，"
            + "但表结构刻意保留（删实体就得补一个 DROP TABLE 破坏性迁移，不值当）");
        tables.Should().Contain("agent_metrics", "M4.2 的 5 秒明细（温层）");
        tables.Should().Contain("agent_metrics_daily", "M4.2 的日聚合（冷层）");
        tables.Should().Contain("agent_power_events", "M4.2 的开关机记录（永久保留）");
        tables.Should().Contain("agent_inventories", "M4.2 的硬件清单（一机一行）");
        tables.Should().Contain("file_repo_items", "M4.4 的文件仓库");
        tables.Should().Contain("file_upload_sessions", "M4.4 的分块上传会话（断点续传的载体）");
        tables.Should().Contain("distribution_tasks", "M4.4 的分发任务");
        tables.Should().Contain("distribution_items", "M4.4 的分发明细（每台目标一行）");
        tables.Should().Contain("agent_configs", "M4.5 的客户机配置（一机一行）");
        tables.Should().Contain("__EFMigrationsHistory");

        var history = ReadMigrationHistory(factory.DatabaseFile);
        history.Should().HaveCount(13, "基线 + M2 两次 + M3 账号列 + M4 客户端表 + M4.2 遥测表 + M4.3 温度列 + M4.4 文件分发表 + M4.5 客户机配置 + M4.6 每目标存放目录 + M4.7 浏览路径记忆 + M4.8 逐设备读数列 + M4.12 拉黑列改名都要登记");
        history.Should().Contain(id => id.StartsWith(InitialCreateMigration, StringComparison.Ordinal));
        history.Should().Contain(id => id.Contains("M2_ConfigDocuments", StringComparison.Ordinal));
        history.Should().Contain(id => id.Contains("M2_TimerFires", StringComparison.Ordinal));
        history.Should().Contain(id => id.Contains("M3_UserAccount", StringComparison.Ordinal));
        history.Should().Contain(id => id.Contains("M4_AgentHosts", StringComparison.Ordinal));
        history.Should().Contain(id => id.Contains("M4_2_AgentTelemetry", StringComparison.Ordinal));
        history.Should().Contain(id => id.Contains("M4_3_AgentThermal", StringComparison.Ordinal));
        history.Should().Contain(id => id.Contains("M4_4_Files", StringComparison.Ordinal));
        history.Should().Contain(id => id.Contains("M4_5_AgentConfig", StringComparison.Ordinal));
        history.Should().Contain(id => id.Contains("M4_6_DistributionItemTargetDir", StringComparison.Ordinal));
        history.Should().Contain(id => id.Contains("M4_7_AgentLastPath", StringComparison.Ordinal));
        history.Should().Contain(id => id.Contains("M4_8_AgentDeviceReadings", StringComparison.Ordinal));

        // M4.7：路径记忆两列必须**可空 TEXT**。
        //    null = 从没记过（或那一项不动）；空串 = 用那台机器的默认目录（有语义，不能当 null 吞掉）。
        //    若有人把它们建成 NOT NULL，上面第 2 条语义就没了。
        foreach (var pathColumn in new[] { "LastTargetDir", "LastSourceDir" })
        {
            ReadColumnType(factory.DatabaseFile, "agent_hosts", pathColumn).Should().Be(
                "TEXT", $"agent_hosts.{pathColumn} 是浏览路径记忆，存字符串");
            ReadColumnIsNullable(factory.DatabaseFile, "agent_hosts", pathColumn).Should().BeTrue(
                $"agent_hosts.{pathColumn} 必须可空 —— null 表示「没记过 / 这一项不动」");
        }

        // M4.8：逐设备读数必须**可空 TEXT**。
        //    建成 NOT NULL（或默认空串）的话，"这台客户机不上报逐设备明细" 与
        //    "上报了一组空数组" 就分不开，前端也没法用 null 表达"该时段没有逐设备数据"
        //    —— 而那句提示是这一整条逐设备曲线功能唯一的诚实出口。
        ReadColumnType(factory.DatabaseFile, "agent_metrics", "DevicesJson").Should().Be(
            "TEXT", "agent_metrics.DevicesJson 是一个 JSON 数组，存字符串");
        ReadColumnIsNullable(factory.DatabaseFile, "agent_metrics", "DevicesJson").Should().BeTrue(
            "agent_metrics.DevicesJson 必须可空 —— null 表示这台机/这一帧没有逐设备数据");

        // ⚠️ 遥测时间列必须是 INTEGER（Unix 毫秒），不能退回 TEXT 形态的 DateTimeOffset。
        //    原因：EF 的 SQLite 提供程序**拒绝翻译** OrderBy(DateTimeOffset)，
        //    一旦有人把它改回去，开关机列表会在运行时抛 NotSupportedException ——
        //    而单测/编译都不会发现（它只在真查库的那一刻炸）。这一刻正是它的保险丝。
        ReadColumnType(factory.DatabaseFile, "agent_metrics", "AtMs").Should().Be(
            "INTEGER", "遥测时间戳以 Unix 毫秒落库（SQLite 无法 ORDER BY DateTimeOffset）");
        ReadColumnType(factory.DatabaseFile, "agent_power_events", "AtMs").Should().Be("INTEGER");

        // M4.3：温度列必须是可空 REAL。这一条挡的是"有人把它们建成 NOT NULL 并给默认 0"——
        // 那会让"未启用温度监控"变成"机器稳定 0 ℃"，是全项目最忌讳的那种假数据。
        foreach (var tempColumn in new[] { "CpuTempC", "GpuTempC", "StorageTempC" })
        {
            ReadColumnType(factory.DatabaseFile, "agent_metrics", tempColumn).Should().Be(
                "REAL", $"agent_metrics.{tempColumn} 必须是 REAL（null 表示没读数，0 表示很凉快）");

            ReadColumnIsNullable(factory.DatabaseFile, "agent_metrics", tempColumn).Should().BeTrue(
                $"agent_metrics.{tempColumn} 必须可空 —— 温度取不到时是 null，绝不用 0 冒充");
        }

        ReadColumnType(factory.DatabaseFile, "agent_metrics_daily", "CpuTempAvgC").Should().Be("REAL");
        ReadColumnType(factory.DatabaseFile, "agent_metrics_daily", "CpuTempMaxC").Should().Be("REAL");
        ReadColumnIsNullable(factory.DatabaseFile, "agent_metrics_daily", "CpuTempMaxC").Should().BeTrue();

        // M4.5：「已应用配置版本」必须**可空**。
        // 建成 NOT NULL 并默认 0 的话，"从未回报过"与"回报了版本 0"就分不开 ——
        // 而页面上"已下发 v3 / 已应用 v0"这种显示会逼运维去查一个根本不存在的故障。
        ReadColumnIsNullable(factory.DatabaseFile, "agent_hosts", "AppliedConfigVersion").Should().BeTrue(
            "agent_hosts.AppliedConfigVersion 必须可空：null = 老客户端从未回报，不能拿 0 冒充");

        // M4.5：配置的更新时间必须是 INTEGER（Unix 毫秒）—— 理由同遥测：
        // 配置列表按更新时间倒序，而 EF 的 SQLite 提供程序拒绝翻译 DateTimeOffset 的 ORDER BY。
        ReadColumnType(factory.DatabaseFile, "agent_configs", "UpdatedAtMs").Should().Be("INTEGER");

        // 新装库的默认管理员必须处于"启用"状态 ——
        // 这条既是业务事实，也是 IsEnabled 列默认值被写成 true 的直接证据。
        ReadUserEnabled(factory.DatabaseFile).Should().BeTrue("播种出来的管理员必须是启用的");

        // 时间口径：全应用统一北京时间 UTC+8。
        // 前端 fmtDateTime 是**直接截 ISO 字符串**（刻意不做时区换算），所以带 +00:00
        // 的时间戳在界面上会实打实差 8 小时 —— 出厂管理员的"创建时间"就是第一个受害者。
        // 实体默认值曾写成 DateTimeOffset.UtcNow，这条用例把它钉住。
        var created = ReadUserCreatedAtOffset(factory.DatabaseFile);
        created.Should().Be(
            TimeSpan.FromHours(8),
            "账号创建时间必须是北京时间（UTC+8），不能落成 UTC");
    }

    // ═══════════════════════════════════════════════════════════
    // 场景二：M1 老库（无迁移历史）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Legacy_M1_database_is_adopted_and_upgraded_incrementally()
    {
        var dir = Path.Combine(Path.GetTempPath(), "controlserver-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbFile = Path.Combine(dir, "control.db");

        try
        {
            CreateLegacyM1Database(dbFile);

            // 前置断言：此刻确实是"有业务表、无迁移历史"的 M1 形态。
            var before = ReadTableNames(dbFile);
            before.Should().Contain("users");
            before.Should().NotContain("__EFMigrationsHistory");
            before.Should().NotContain("config_documents");

            // 启动宿主 —— 若基线认领没做对，这里会因"表已存在"直接失败。
            using var factory = new ControlServerFactory(dir);
            using var client = factory.CreateClient();

            var health = await client.GetAsync("/healthz");
            health.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, "M1 老库必须能直接升级启动，不需要人工删库");

            var after = ReadTableNames(dbFile);
            after.Should().Contain("config_documents", "增量迁移应把 M2 的新表建出来");
            after.Should().Contain("timer_fires", "M2-D 的触发记录表也要跟着建出来");
            after.Should().Contain("agent_hosts", "M4 的客户端在线表同样要跟着建出来");
            after.Should().Contain("enrollment_tokens", "M4 的准入令牌表同样要跟着建出来");
            after.Should().Contain("agent_metrics", "M4.2 的遥测表同样要跟着建出来");
            after.Should().Contain("agent_metrics_daily");
            after.Should().Contain("agent_power_events");
        after.Should().Contain("agent_inventories");
        after.Should().Contain("file_repo_items", "M4.4 的文件仓库同样要跟着建出来");
        after.Should().Contain("file_upload_sessions");
        after.Should().Contain("distribution_tasks");
        after.Should().Contain("distribution_items");
        after.Should().Contain("agent_configs", "M4.5 的客户机配置同样要跟着建出来");
        after.Should().Contain("__EFMigrationsHistory", "基线认领会补出历史表");

            var history = ReadMigrationHistory(dbFile);
            history.Should().Contain(id => id.StartsWith(InitialCreateMigration, StringComparison.Ordinal));
            history.Should().Contain(id => id.Contains("M2_ConfigDocuments", StringComparison.Ordinal));
            history.Should().Contain(id => id.Contains("M2_TimerFires", StringComparison.Ordinal));
            history.Should().Contain(id => id.Contains("M3_UserAccount", StringComparison.Ordinal));
            history.Should().Contain(id => id.Contains("M4_AgentHosts", StringComparison.Ordinal));
            history.Should().Contain(id => id.Contains("M4_2_AgentTelemetry", StringComparison.Ordinal));
        history.Should().Contain(id => id.Contains("M4_3_AgentThermal", StringComparison.Ordinal));
        history.Should().Contain(id => id.Contains("M4_4_Files", StringComparison.Ordinal));
        history.Should().Contain(id => id.Contains("M4_5_AgentConfig", StringComparison.Ordinal));
        history.Should().Contain(id => id.Contains("M4_6_DistributionItemTargetDir", StringComparison.Ordinal));
        history.Should().Contain(id => id.Contains("M4_7_AgentLastPath", StringComparison.Ordinal));
        history.Should().Contain(id => id.Contains("M4_8_AgentDeviceReadings", StringComparison.Ordinal));
        history.Should().HaveCount(13, "老库升级完也应当是 13 条迁移");

        // M4.7 的路径记忆列在**老库升级路径**上同样要落好（新装库建表就带上，老库得靠 ALTER TABLE 补）。
        ReadColumnIsNullable(dbFile, "agent_hosts", "LastTargetDir").Should().BeTrue(
            "M1 老库升级后 agent_hosts.LastTargetDir 必须存在且可空");
        ReadColumnIsNullable(dbFile, "agent_hosts", "LastSourceDir").Should().BeTrue(
            "M1 老库升级后 agent_hosts.LastSourceDir 必须存在且可空");

        // M4.8 的逐设备读数列同理：老库靠 ALTER TABLE 补这一列。
        //    ⚠️ 这一条尤其容易被漏 —— 老客户端升级服务端后，历史明细的 DevicesJson 全是 null，
        //    若这一列没补上，查询会在运行时抛 "no such column"（编译期与单测都发现不了）。
        ReadColumnIsNullable(dbFile, "agent_metrics", "DevicesJson").Should().BeTrue(
            "M1 老库升级后 agent_metrics.DevicesJson 必须存在且可空");

            // M4.3 的温度列在**老库升级路径**上同样要落好 ——
            // 这是最容易漏的一条：新装库建表时就带上了，老库却要靠 ALTER TABLE 补。
            ReadColumnIsNullable(dbFile, "agent_metrics", "CpuTempC").Should().BeTrue(
                "M1 老库升级后 agent_metrics.CpuTempC 必须存在且可空");

            // M4.5 的配置表 / 已应用版本列同理：老库靠 ALTER TABLE + CREATE TABLE 补出来。
            ReadColumnIsNullable(dbFile, "agent_hosts", "AppliedConfigVersion").Should().BeTrue(
                "M1 老库升级后 agent_hosts.AppliedConfigVersion 必须存在且可空");

            // 老库里已有的用户必须原样保留，且不能因为"users 为空"被重新播种。
            var users = ReadUsers(dbFile);
            users.Should().ContainSingle("不应重复播种默认管理员");
            users[0].Should().Be("legacy-admin");

            // ⚠️ 守门：M3 给 users 加 IsEnabled 时，EF 为非空 bool 生成的默认值是 false ——
            //    照它执行会把**所有存量账号一次性停用**，升级后谁都登不进来。
            //    这条断言就是那段手改默认值的保险丝：它一旦变红，说明默认值又回到 false。
            ReadUserEnabled(dbFile).Should().BeTrue(
                "存量账号在升级前一直是可用的，加列时不能被默认停用");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不影响结论。
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 工具
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 造一个"M1 形态"的库：表结构由 <c>InitialCreate</c> 基线迁移生成，
    /// 但**没有</b> <c>__EFMigrationsHistory</c> —— 精确复刻 EnsureCreated 的产物。
    /// </summary>
    private static void CreateLegacyM1Database(string dbFile)
    {
        using var connection = new SqliteConnection($"Data Source={dbFile}");
        connection.Open();

        Execute(connection, """
            CREATE TABLE "audit_logs" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_audit_logs" PRIMARY KEY AUTOINCREMENT,
                "Timestamp" TEXT NOT NULL,
                "User" TEXT NOT NULL,
                "Action" TEXT NOT NULL,
                "Detail" TEXT NOT NULL,
                "RemoteIp" TEXT NOT NULL
            );
            """);

        Execute(connection, """
            CREATE TABLE "settings" (
                "Key" TEXT NOT NULL CONSTRAINT "PK_settings" PRIMARY KEY,
                "Value" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);

        Execute(connection, """
            CREATE TABLE "users" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_users" PRIMARY KEY AUTOINCREMENT,
                "UserName" TEXT NOT NULL,
                "PasswordHash" TEXT NOT NULL,
                "Salt" TEXT NOT NULL,
                "Role" TEXT NOT NULL,
                "MustChangePassword" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """);

        Execute(connection, """CREATE UNIQUE INDEX "IX_users_UserName" ON "users" ("UserName");""");

        // 预置一个用户，用于验证升级过程不会重复播种。
        Execute(connection, """
            INSERT INTO "users" ("UserName","PasswordHash","Salt","Role","MustChangePassword","CreatedAt")
            VALUES ('legacy-admin','hash','salt','Admin',0,'2026-01-01T00:00:00+08:00');
            """);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<string> ReadTableNames(string dbFile)
    {
        using var connection = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// 某一列在库里的声明类型（SQLite 是动态类型，但 EF 迁移写下的 DDL 仍带类型名）。
    /// <para>用途：钉住"遥测时间列必须是 INTEGER（Unix 毫秒）"这个**只能在真查库时才暴露**的约束。</para>
    /// </summary>
    private static string? ReadColumnType(string dbFile, string table, string column)
    {
        using var connection = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT type FROM pragma_table_info('{table}') WHERE name = $column;";
        command.Parameters.AddWithValue("$column", column);

        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// 某一列是否可空。
    /// <para>
    /// 光看类型钉不住"M4.3 温度列必须可空"这件事 —— 一个 <c>REAL NOT NULL DEFAULT 0</c>
    /// 的类型名同样是 <c>REAL</c>，但它会把"没读数"变成"0 ℃"。
    /// 所以必须直接查 <c>pragma_table_info.notnull</c>。
    /// </para>
    /// </summary>
    private static bool ReadColumnIsNullable(string dbFile, string table, string column)
    {
        using var connection = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();

        // ⚠️ `notnull` 必须加引号：不加的话 SQLite 会把 `SELECT notnull` 里的它
        //    解析成 `NOT NULL` 两个关键字，报 `near "notnull": syntax error`。
        command.CommandText = $"SELECT \"notnull\" FROM pragma_table_info('{table}') WHERE name = $column;";
        command.Parameters.AddWithValue("$column", column);

        var raw = command.ExecuteScalar();

        return raw is long flag && flag == 0;
    }

    private static List<string> ReadMigrationHistory(string dbFile)
    {
        using var connection = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;";

        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static List<string> ReadUsers(string dbFile)
    {
        using var connection = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT UserName FROM users ORDER BY Id;";

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>读第一个账号的 <c>IsEnabled</c>（M3 新列的执行期取值）。</summary>
    private static bool ReadUserEnabled(string dbFile)
    {
        using var connection = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT IsEnabled FROM users ORDER BY Id LIMIT 1;";

        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>
    /// 读第一个账号 <c>CreatedAt</c> 的时区偏移。
    /// <para>
    /// SQLite 把 <c>DateTimeOffset</c> 存成带偏移的字符串（如 <c>...+08:00</c>），
    /// 所以这里直接解析文本 —— 用 <c>Microsoft.Data.Sqlite</c> 的默认映射读出来的是
    /// <c>DateTime</c>（偏移会被丢掉），那就看不出"是 UTC 还是北京时间"了。
    /// </para>
    /// </summary>
    private static TimeSpan ReadUserCreatedAtOffset(string dbFile)
    {
        using var connection = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CreatedAt FROM users ORDER BY Id LIMIT 1;";

        var raw = command.ExecuteScalar() as string;
        raw.Should().NotBeNullOrWhiteSpace("播种出来的管理员必须有创建时间");

        return DateTimeOffset.Parse(raw!, System.Globalization.CultureInfo.InvariantCulture).Offset;
    }
}
