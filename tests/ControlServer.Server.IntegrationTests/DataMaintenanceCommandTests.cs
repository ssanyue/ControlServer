using ControlServer.Server.Cli;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 维护子命令的<b>参数解析</b>（M6.1）。纯逻辑，不起宿主。
///
/// <para>
/// 这几条钉的都是"会静默出错"的地方，不是"看起来能不能跑"：
/// </para>
/// <list type="bullet">
///   <item>
///   🔴 <b>服务的正常启动参数不能被当成维护命令</b>。<c>--Server:Storage:DataDir=D:\x</c>
///   也是一堆参数；若判据写成"args 非空"，带任何参数启动服务都会变成"只备份一下就退出" ——
///   那等于服务再也起不来，而且退出码还是 0。
///   </item>
///   <item>
///   🔴 <b>拼错的选项要报错，不能静默忽略</b>。<c>--includelog</c>（少个 s）若被忽略，
///   备份照样"成功"，只是没带日志 —— 等到要查日志那天才发现。
///   </item>
/// </list>
/// </summary>
public sealed class DataMaintenanceCommandTests
{
    [Fact]
    public void Backup_verb_and_target_are_parsed()
    {
        DataMaintenanceCommand.TryParse(["--backup", @"D:\bk"], out var invocation)
            .Should().BeTrue();

        invocation!.Verb.Should().Be("backup");
        invocation.Target.Should().Be(@"D:\bk");
        invocation.IncludeLogs.Should().BeFalse("默认不备日志：体积随天数线性长，且不属于'恢复后必须一模一样'");
        invocation.Force.Should().BeFalse();
    }

    [Fact]
    public void Restore_verb_accepts_force()
    {
        DataMaintenanceCommand.TryParse(["--restore", @"D:\bk", "--force"], out var invocation)
            .Should().BeTrue();

        invocation!.Verb.Should().Be("restore");
        invocation.Target.Should().Be(@"D:\bk");
        invocation.Force.Should().BeTrue("覆盖已有数据库必须是一个显式动作");
    }

    [Fact]
    public void Backup_accepts_include_logs()
    {
        DataMaintenanceCommand.TryParse(["--backup", @"D:\bk", "--include-logs"], out var invocation)
            .Should().BeTrue();

        invocation!.IncludeLogs.Should().BeTrue();
    }

    [Fact]
    public void Service_startup_arguments_are_not_mistaken_for_a_maintenance_command()
    {
        // 这是实际启动服务时用的形状。误判成维护命令的后果是：
        // 服务"启动"了，退出码 0，但什么都没在跑。
        DataMaintenanceCommand.TryParse(
                [@"--Server:Storage:DataDir=D:\data", @"--Server:Startup:AttachConsole=true"],
                out var invocation)
            .Should().BeFalse("带配置参数启动服务是常态，绝不能因此走维护分支");
        invocation.Should().BeNull();
    }

    [Fact]
    public void A_misspelled_option_is_reported_instead_of_ignored()
    {
        DataMaintenanceCommand.TryParse(["--backup", @"D:\bk", "--includelog"], out var invocation)
            .Should().BeTrue("命令本身认得出来 —— 认得出来才有机会报错");

        invocation!.Verb.Should().StartWith(
            "backup!unknown-arg:",
            "拼错的选项必须浮出水面；静默忽略会让'备份成功'和'备份带了日志'变成两件谁也说不清的事");
        invocation.Verb.Should().Contain("--includelog");
    }

    [Fact]
    public void Help_alone_is_treated_as_a_maintenance_request()
    {
        DataMaintenanceCommand.TryParse(["--help"], out var invocation).Should().BeTrue();
        invocation!.Verb.Should().Be("help");
    }

    [Fact]
    public void No_arguments_at_all_is_a_normal_service_start()
    {
        DataMaintenanceCommand.TryParse([], out var invocation).Should().BeFalse();
        invocation.Should().BeNull();
    }

    [Fact]
    public void Health_is_recognized_and_rejects_misspelled_options_too()
    {
        DataMaintenanceCommand.TryParse(["--health"], out var invocation).Should().BeTrue();
        invocation!.Verb.Should().Be("health");

        // 🔴 这条是 M6.5 顺手修的：早先只对 backup/restore 判未知参数，
        //    于是 `--health --verbose` 会被静默忽略 —— 与 `--includelog` 是同一类错误。
        DataMaintenanceCommand.TryParse(["--health", "--verbose"], out var typo)
            .Should().BeTrue();
        typo!.Verb.Should().StartWith("health!unknown-arg:");
    }

    // ═══════════════════════════════════════════════════════════
    // --health 的输出契约
    //
    // 为什么值得专门钉：这份输出会被 shell 脚本 grep、被定时任务看退出码。
    // 键名一旦变成中文、或者某一行被换行拆开，脚本会静默地什么都不匹配 ——
    // 而"巡检没报错"和"巡检什么都没查"长得一模一样。
    // ═══════════════════════════════════════════════════════════

    private static IConfiguration HealthConfig(int httpPort) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // 端口 1 上不会有任何东西：探测会立刻 connection refused，
            // 不用等 3 秒超时，也不会撞上开发机上可能正在跑的服务（铁律：不绑固定端口）。
            ["Server:Http:Port"] = httpPort.ToString(),
            ["Server:Logging:RetentionEnabled"] = "true",
            ["Server:Logging:RetentionDays"] = "30",
            ["Server:Logging:RetentionMaxMegabytes"] = "200",
        }).Build();

    [Fact]
    public void Health_reports_a_directory_without_a_database_as_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), "health-" + Guid.NewGuid().ToString("n")[..8]);
        var output = new StringWriter();

        try
        {
            Directory.CreateDirectory(root);

            var exitCode = HealthCheckCommand.Run(root, output, HealthConfig(httpPort: 1));

            exitCode.Should().Be(2, "没有 control.db 就是「这台机器还没装好」，不能只报个告警混过去");

            var lines = output.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            lines.Should().Contain("data.exists=1");
            lines.Should().Contain("health=fail");
            lines.Should().Contain("fail.count=1");

            // 每一行都是 key=value，且 key 全 ASCII（GBK 控制台 / Git Bash / Linux 三处都能 grep）。
            foreach (var line in lines.Where(l => l.Contains('=')))
            {
                var key = line[..line.IndexOf('=')];
                key.Should().MatchRegex("^[A-Za-z][A-Za-z0-9._]*$");
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Health_treats_an_unreachable_service_as_warning_not_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), "health-" + Guid.NewGuid().ToString("n")[..8]);
        var output = new StringWriter();

        try
        {
            Directory.CreateDirectory(root);

            // 一个"有库、没服务在跑"的目录：这是最常见的巡检现场（维护窗口 / 忘了开机启动）。
            File.WriteAllBytes(Path.Combine(root, "control.db"), [1, 2, 3, 4]);

            var exitCode = HealthCheckCommand.Run(root, output, HealthConfig(httpPort: 1));

            exitCode.Should().Be(1, "服务没起是告警不是失败 —— 维护窗口里它本来就该是停的");

            var text = output.ToString();
            text.Should().Contain("db.bytes=4");
            text.Should().Contain("service.healthz=unreachable");
            text.Should().Contain("health=warn");
            text.Should().Contain("fail.count=0");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // --files-recover（M7.2）
    //
    // 这一组守的是"找回"这件事本身的危险性：它是**唯一**一个会往 repo/ 里写文件的维护命令。
    // 写错一次，就是把「能看出来的坏（404）」换成「看不出来的坏（下载下来是坏数据）」。
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Files_recover_verb_is_parsed_and_rejects_typos()
    {
        DataMaintenanceCommand.TryParse(["--files-recover", @"D:\bk"], out var invocation)
            .Should().BeTrue();

        invocation!.Verb.Should().Be("files-recover");
        invocation.Target.Should().Be(@"D:\bk");

        DataMaintenanceCommand.TryParse(["--files-recover", "--forse"], out var typo).Should().BeTrue();
        typo!.Verb.Should().StartWith("files-recover!unknown-arg:",
            "拼错 --force 会让「本不想覆盖」变成「覆盖了」或反过来");
    }

    [Fact]
    public void Files_recover_without_a_backup_directory_only_reports_and_writes_nothing()
    {
        var root = NewRoot("recover-report");

        try
        {
            var storedName = "f1_lost.bin";
            CreateRepoDb(root, [("样例.bin", storedName, "hello")]);

            var repoDir = Path.Combine(root, "files", "repo");
            Directory.CreateDirectory(repoDir);

            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = DataMaintenanceCommand.RunFilesRecover(root, source: null, force: false, output, error);

            exitCode.Should().Be(2, "只报告「缺什么」而不动手，必须让脚本能判出「没完成」");
            output.ToString().Should().Contain("lost=1");
            error.ToString().Should().Contain("--files-recover",
                "得告诉人下一步该敲什么，否则这条命令只是把问题重述了一遍");

            Directory.GetFiles(repoDir).Should().BeEmpty("没有备份目录时绝不允许写任何东西");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Files_recover_rejects_a_same_name_file_whose_content_differs()
    {
        var root = NewRoot("recover-reject");
        var backup = NewRoot("recover-backup");

        try
        {
            var storedName = "f1_lost.bin";
            CreateRepoDb(root, [("样例.bin", storedName, "hello")]);
            Directory.CreateDirectory(Path.Combine(root, "files", "repo"));

            // 同名、同大小、内容不同 —— 这正是"手拷备份时拷错版本"的样子。
            var backupRepo = Path.Combine(backup, "files", "repo");
            Directory.CreateDirectory(backupRepo);
            File.WriteAllText(Path.Combine(backupRepo, storedName), "HELLO");

            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = DataMaintenanceCommand.RunFilesRecover(root, backup, force: false, output, error);

            exitCode.Should().Be(2);
            output.ToString().Should().Contain("rejected=1");

            File.Exists(Path.Combine(root, "files", "repo", storedName))
                .Should().BeFalse("内容对不上的文件绝不能进仓库 —— 那会把 404 变成坏数据");
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(backup);
        }
    }

    [Fact]
    public void Files_recover_puts_back_a_file_whose_digest_matches()
    {
        var root = NewRoot("recover-ok");
        var backup = NewRoot("recover-backup-ok");

        try
        {
            var storedName = "f1_lost.bin";
            CreateRepoDb(root, [("样例.bin", storedName, "hello")]);
            Directory.CreateDirectory(Path.Combine(root, "files", "repo"));

            var backupRepo = Path.Combine(backup, "files", "repo");
            Directory.CreateDirectory(backupRepo);
            File.WriteAllText(Path.Combine(backupRepo, storedName), "hello");

            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = DataMaintenanceCommand.RunFilesRecover(root, backup, force: false, output, error);

            exitCode.Should().Be(0);
            output.ToString().Should().Contain("recovered=1");

            var restored = Path.Combine(root, "files", "repo", storedName);
            File.Exists(restored).Should().BeTrue();
            File.ReadAllText(restored).Should().Be("hello");

            // 临时的 .recover 中间文件不能留下 —— 留着会被对账当成孤儿。
            Directory.GetFiles(Path.Combine(root, "files", "repo"), "*.recover").Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(backup);
        }
    }

    [Fact]
    public void Files_recover_refuses_a_stored_name_that_is_not_a_single_segment()
    {
        var root = NewRoot("recover-evil");
        var backup = NewRoot("recover-evil-backup");

        try
        {
            // 一个被改坏的库：StoredName 指向 repo/ 外面。
            CreateRepoDb(root, [("越界", @"..\..\https\server.pfx", "hello")]);
            Directory.CreateDirectory(Path.Combine(root, "files", "repo"));

            var backupRepo = Path.Combine(backup, "files", "repo");
            Directory.CreateDirectory(backupRepo);

            var output = new StringWriter();
            var error = new StringWriter();

            DataMaintenanceCommand.RunFilesRecover(root, backup, force: true, output, error)
                .Should().Be(0, "这种记录被跳过，不算「没找回」——它压根不该被找");

            output.ToString().Should().Contain("lost=0");
            error.ToString().Should().Contain("不是单段名");
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(backup);
        }
    }

    private static string NewRoot(string prefix)
        => Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid().ToString("n")[..8]}");

    /// <summary>
    /// 造一个只带 <c>file_repo_items</c> 的最小 control.db。
    /// <para>
    /// 不跑 EF 迁移：这条命令只读这一张表的 5 个列，
    /// 用最小 schema 反而能把"它到底依赖哪些列"钉死（多一列依赖就会在这里红）。
    /// </para>
    /// </summary>
    private static void CreateRepoDb(string root, IReadOnlyList<(string Name, string StoredName, string Content)> items)
    {
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "control.db");

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                create table file_repo_items (
                    Id integer primary key autoincrement,
                    Name text not null,
                    StoredName text not null,
                    Size integer not null,
                    Sha256 text null);
                """;
            cmd.ExecuteNonQuery();
        }

        foreach (var item in items)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(item.Content);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "insert into file_repo_items (Name, StoredName, Size, Sha256) values ($n, $s, $z, $h)";
            cmd.Parameters.AddWithValue("$n", item.Name);
            cmd.Parameters.AddWithValue("$s", item.StoredName);
            cmd.Parameters.AddWithValue("$z", bytes.Length);
            cmd.Parameters.AddWithValue("$h", sha);
            cmd.ExecuteNonQuery();
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 临时目录删不掉不影响结论。
        }
    }
}
