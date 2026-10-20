using ControlServer.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// M6.1 备份 / 恢复 —— 覆盖 <c>docs/M6-加固交付方案.md</c> §3.5 的闭环：
/// <b>备份 → 删掉整个数据目录 → 恢复 → 逐项比对</b>。
///
/// <para>
/// 这里的第一条用例不只是测实现，它先<b>把前提钉死</b>：
/// 数据库是 WAL 模式（<c>DatabaseInitializer</c> 显式开启），
/// 于是「把 <c>control.db</c> 拷走」这件事<b>看起来像备份、实际会少最后一截</b>。
/// 用例里先用朴素 <c>File.Copy</c> 做一次对照，证明陷阱真实存在，再要求实现用
/// <c>VACUUM INTO</c> 拿到一致快照。没有这个对照，后面那条绿是<b>没有意义的绿</b>：
/// 谁也不知道它绿是因为做对了，还是因为恰好没有未合并的写入。
/// </para>
///
/// <para>
/// 🔴 第二条钉的是「不该备的东西」：<c>https/</c> 里放的是根 CA 私钥，
/// <c>IDataDirectory.CertificateDirectory</c> 的注释明确写着它<b>不许</b>进备份。
/// 备份包里多一个 <c>ca.pfx</c>，等于每次备份都在往外递一张"可以冒充本服务"的通行证。
/// </para>
/// </summary>
public sealed class BackupRestoreTests
{
    private const string ProbeKey = "m6.1-wal-probe";

    // ═══════════════════════════════════════════════════════════
    // ① 假备份陷阱：朴素拷贝会丢掉还留在 WAL 里的行
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Backup_keeps_rows_that_are_still_only_in_the_wal()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();
        (await client.GetAsync("/healthz")).EnsureSuccessStatusCode();

        var dataRoot = Path.GetDirectoryName(factory.DatabaseFile)!;

        // 写一行，并让它**一定留在 WAL 里**：关掉本连接的自动 checkpoint 再写。
        // 这正是"服务正在跑、刚有人改了配置"的真实状态。
        using (var writer = OpenWritable(factory.DatabaseFile))
        {
            Execute(writer, "PRAGMA wal_autocheckpoint=0;");
            Execute(
                writer,
                $"INSERT INTO settings (\"Key\", \"Value\", \"UpdatedAt\") "
                + $"VALUES ('{ProbeKey}', 'only-in-wal', '2026-10-17T15:00:00+08:00');");

            var wal = factory.DatabaseFile + "-wal";
            File.Exists(wal).Should().BeTrue("开了 WAL 且未 checkpoint，-wal 文件必须存在");
            new FileInfo(wal).Length.Should().BeGreaterThan(0, "-wal 里必须有内容，否则这条用例什么也没证明");

            // ── 对照：朴素拷贝（只拷 control.db，不拷 -wal）──
            var naiveDirectory = NewScratchDirectory("naive");
            Directory.CreateDirectory(naiveDirectory);
            var naive = Path.Combine(naiveDirectory, "control.db");
            File.Copy(factory.DatabaseFile, naive);

            CountRows(naive, "settings", ProbeKey).Should().Be(
                0,
                "这就是'拷数据库文件=备份'的真相：WAL 里那条写入根本不在 control.db 里，"
                + "而这种错在备份当天完全看不出来，只在真恢复那天爆");
        }

        // ── 产品实现：必须拿到一致快照 ──
        var target = NewScratchDirectory("backup");
        var sut = new DataBackupService(dataRoot);
        await sut.CreateAsync(target);

        var backedUp = Path.Combine(target, DataBackupService.DatabaseEntryName);
        CountRows(backedUp, "settings", ProbeKey).Should().Be(
            1,
            "备份必须包含 -wal 里尚未合并的写入，否则它只是'看起来像备份'");

        // 备份产物里也不该留一个 -wal：它是一致快照，不是被拷来的中途状态。
        File.Exists(backedUp + "-wal").Should().BeFalse("VACUUM INTO 产出的是自洽的单文件库");
    }

    // ═══════════════════════════════════════════════════════════
    // ② 不该备的东西：证书私钥、安装包
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Backup_leaves_the_https_private_keys_behind()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();
        (await client.GetAsync("/healthz")).EnsureSuccessStatusCode();

        var dataRoot = Path.GetDirectoryName(factory.DatabaseFile)!;
        var https = Path.Combine(dataRoot, "https");
        Directory.CreateDirectory(https);

        // 造出与真实布局同名的四个文件（内容无所谓，这里只关心"有没有被拷走"）。
        foreach (var name in new[] { "ca.pfx", "server.pfx", "pfx.password", "controlserver-ca.crt" })
        {
            await File.WriteAllTextAsync(Path.Combine(https, name), "not-a-real-cert-" + name);
        }

        var installer = Path.Combine(dataRoot, "downloads");
        Directory.CreateDirectory(installer);
        await File.WriteAllBytesAsync(Path.Combine(installer, "ControlServer-Agent-Setup-0.1.0.exe"), [1, 2, 3]);

        var target = NewScratchDirectory("backup");
        var sut = new DataBackupService(dataRoot);
        var manifest = await sut.CreateAsync(target);

        var names = Directory.GetFiles(target, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetFileName(p))
            .ToList();

        names.Should().NotContain(
            "ca.pfx",
            "根 CA 私钥 = 整条链路的安全边界；备份包里带它，等于每次备份都往外递一张可冒充本服务的通行证");
        names.Should().NotContain("server.pfx", "服务器证书同样是私钥");
        names.Should().NotContain("pfx.password", "口令与私钥同目录，一起拷走等于一起泄露");
        names.Should().NotContain(
            "ControlServer-Agent-Setup-0.1.0.exe",
            "安装包有 65 MB，且随时能从发布源重新取 —— 放进备份只会让每次备份都变慢变大");

        // 但"没备"必须是**说出来的**，不能只是静静地少一项：
        // 下一个人看到备份里没有 https/，得能立刻分清"故意的"还是"漏了"。
        manifest.Skipped.Should().Contain(
            s => s.Path.StartsWith("https", StringComparison.Ordinal),
            "跳过证书目录这件事必须写进 manifest，否则会被当成漏备");
        manifest.Skipped.Should().Contain(s => s.Path.StartsWith("downloads", StringComparison.Ordinal));

        // 公钥证书是可以分发的 —— 它应当单独被带上，恢复后至少要能看出"当初是哪个 CA"。
        names.Should().Contain(
            "controlserver-ca.crt",
            "根 CA 的公钥本来就允许分发（观看端信任的就是它），留着它才能判断恢复前后是不是同一个 CA");
    }

    [Fact]
    public async Task Backup_carries_the_licence_and_its_binding_records()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();
        (await client.GetAsync("/healthz")).EnsureSuccessStatusCode();

        var dataRoot = Path.GetDirectoryName(factory.DatabaseFile)!;
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "license.lic"), "LICENCE-PAYLOAD");
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "license.binding.json"), "{\"MachineCode\":\"X\"}");
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "license.trial.json"), "{\"StartedAtUtcMs\":1}");

        var target = NewScratchDirectory("backup");
        var sut = new DataBackupService(dataRoot);
        await sut.CreateAsync(target);

        File.ReadAllText(Path.Combine(target, "license.lic")).Should().Be(
            "LICENCE-PAYLOAD",
            "授权文件是客户花钱买的东西，恢复后没了等于把服务打回未授权");
        File.Exists(Path.Combine(target, "license.binding.json")).Should().BeTrue(
            "绑定记录丢了会被当成'首次绑定'，可能换一台机器就绑上了");
        File.Exists(Path.Combine(target, "license.trial.json")).Should().BeTrue(
            "试用记录丢了等于白送一轮试用期");
    }

    // ═══════════════════════════════════════════════════════════
    // ③ manifest：每个文件都有哈希，且哈希说得对
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Manifest_covers_every_backed_up_file_with_a_matching_sha256()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();
        (await client.GetAsync("/healthz")).EnsureSuccessStatusCode();

        var dataRoot = Path.GetDirectoryName(factory.DatabaseFile)!;
        var files = Path.Combine(dataRoot, "files");
        Directory.CreateDirectory(files);
        await File.WriteAllTextAsync(Path.Combine(files, "a.txt"), "alpha");
        var config = Path.Combine(dataRoot, "config");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "doc.json"), "{\"v\":1}");

        var target = NewScratchDirectory("backup");
        var sut = new DataBackupService(dataRoot);
        var manifest = await sut.CreateAsync(target);

        manifest.FormatVersion.Should().Be(1, "格式版本是恢复端判断'这个包我认不认'的唯一依据");
        manifest.Files.Should().NotBeEmpty();

        // 目录里除 manifest 自身之外，每个文件都必须在清单里，反之亦然 —— 两边对不上就是漏备或多备。
        var onDisk = Directory.GetFiles(target, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(target, p).Replace('\\', '/'))
            .Where(p => p != DataBackupService.ManifestEntryName)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        manifest.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal)
            .Should().Equal(onDisk, "清单与实际产物必须一一对应，不能有'清单里写了但盘上没有'或反过来");

        foreach (var entry in manifest.Files)
        {
            var path = Path.Combine(target, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            new FileInfo(path).Length.Should().Be(entry.Size, $"{entry.Path} 的大小必须与清单一致");
            Sha256Of(path).Should().Be(entry.Sha256, $"{entry.Path} 的哈希必须与清单一致");
        }

        // manifest 自己也得能被读回来 —— 恢复端第一步就是读它。
        var reread = DataBackupService.ReadManifest(target);
        reread.Files.Should().HaveCount(manifest.Files.Count);
        reread.CreatedAt.Should().Be(manifest.CreatedAt);
    }

    // ═══════════════════════════════════════════════════════════
    // ④ 恢复：篡改要拒、删库要能救回来、私钥不许被碰
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Restore_rejects_a_backup_whose_bytes_were_changed()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();
        (await client.GetAsync("/healthz")).EnsureSuccessStatusCode();

        var dataRoot = Path.GetDirectoryName(factory.DatabaseFile)!;
        var target = NewScratchDirectory("backup");
        var sut = new DataBackupService(dataRoot);
        await sut.CreateAsync(target);

        // 悄悄改一个字节（模拟拷贝中断 / 磁盘坏了 / 有人手改过）。
        var victim = Path.Combine(target, DataBackupService.DatabaseEntryName);
        var bytes = await File.ReadAllBytesAsync(victim);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(victim, bytes);

        var restoreTarget = NewScratchDirectory("restore-into");
        var restorer = new DataBackupService(restoreTarget);

        var act = async () => await restorer.RestoreAsync(target, force: true);

        var ex = await act.Should().ThrowAsync<BackupValidationException>(
            "校验不过就必须停手 —— 静默恢复一个坏包，比不恢复更危险："
            + "它会把一个'看起来能用'的半坏状态铺到唯一一份数据上");
        ex.Which.Message.Should().Contain(
            DataBackupService.DatabaseEntryName,
            "错误消息要点名是哪个文件不对，否则现场只能靠猜");
    }

    [Fact]
    public async Task Restore_brings_back_the_data_after_the_directory_is_wiped()
    {
        var dataRoot = NewScratchDirectory("live");

        long usersBefore;
        long auditBefore;

        using (var factory = new ControlServerFactory(dataRoot))
        using (var client = factory.CreateClient())
        {
            (await client.GetAsync("/healthz")).EnsureSuccessStatusCode();

            // 造三类数据：库里的行、文件仓库里的文件、配置文档。缺一类都算不上演练。
            var files = Path.Combine(dataRoot, "files");
            Directory.CreateDirectory(files);
            await File.WriteAllTextAsync(Path.Combine(files, "keepme.bin"), "the-only-copy");
            var fileSha = Sha256Of(Path.Combine(files, "keepme.bin"));

            var config = Path.Combine(dataRoot, "config");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "doc.json"), "{\"scene\":\"lobby\"}");

            usersBefore = CountRows(factory.DatabaseFile, "users");
            auditBefore = CountRows(factory.DatabaseFile, "audit_logs");
            usersBefore.Should().BeGreaterThan(0, "至少要有默认管理员，否则'恢复后一致'无从谈起");

            var target = NewScratchDirectory("backup");
            var sut = new DataBackupService(dataRoot);
            var manifest = await sut.CreateAsync(target);

            // 🔴 最狠的一步：备份完就把数据目录整个删掉。
            //    不删就等于没演练 —— 恢复脚本最常犯的错恰恰是"文件在，但它没覆盖到"。
            factory.Dispose();
            ReleasePools();
            DeleteDirectoryHard(dataRoot);
            Directory.Exists(dataRoot).Should().BeFalse("前置条件：数据目录必须真的没了");

            var restorer = new DataBackupService(dataRoot);
            var report = await restorer.RestoreAsync(target);

            report.FileCount.Should().Be(manifest.Files.Count, "清单里几个文件，就该落几个");

            // ── 五项逐项比对（§3.5 的口径）──
            CountRows(Path.Combine(dataRoot, "control.db"), "users").Should().Be(
                usersBefore, "① 用户数");
            CountRows(Path.Combine(dataRoot, "control.db"), "audit_logs").Should().BeGreaterThanOrEqualTo(
                auditBefore, "② 审计条数（恢复后可能又记了'已恢复'之类，只许多不许少）");
            File.Exists(Path.Combine(dataRoot, "config", "doc.json")).Should().BeTrue("③ 配置文档");
            Sha256Of(Path.Combine(dataRoot, "files", "keepme.bin")).Should().Be(
                fileSha, "④ 文件仓库内容必须逐字节一致");
        }
    }

    [Fact]
    public async Task Restore_never_wipes_the_https_directory()
    {
        var dataRoot = NewScratchDirectory("live");

        string caBefore;
        using (var factory = new ControlServerFactory(dataRoot))
        using (var client = factory.CreateClient())
        {
            (await client.GetAsync("/healthz")).EnsureSuccessStatusCode();

            var https = Path.Combine(dataRoot, "https");
            Directory.CreateDirectory(https);
            caBefore = Path.Combine(https, "ca.pfx");
            await File.WriteAllTextAsync(caBefore, "LOCAL-ROOT-CA-PRIVATE-KEY");
        }

        ReleasePools();

        var target = NewScratchDirectory("backup");
        var sut = new DataBackupService(dataRoot);
        {
            await sut.CreateAsync(target);
        }

        var restorer = new DataBackupService(dataRoot);
        {
            await restorer.RestoreAsync(target, force: true);
        }

        File.Exists(caBefore).Should().BeTrue(
            "证书私钥不在备份里，恢复时也绝不许删 —— 它属于这台机器，不属于这份数据。"
            + "删掉它会让所有已信任本服务的观看端一起失效（要重新分发根证书）");
        File.ReadAllText(caBefore).Should().Be(
            "LOCAL-ROOT-CA-PRIVATE-KEY", "恢复是'铺回数据'，不是'把这台机器的身份重置'");
    }

    [Fact]
    public async Task Restore_refuses_to_overwrite_a_live_directory_without_force()
    {
        var dataRoot = NewScratchDirectory("live");

        using (var factory = new ControlServerFactory(dataRoot))
        using (var client = factory.CreateClient())
        {
            (await client.GetAsync("/healthz")).EnsureSuccessStatusCode();
        }

        var target = NewScratchDirectory("backup");
        var sut = new DataBackupService(dataRoot);
        {
            await sut.CreateAsync(target);
        }

        var restorer = new DataBackupService(dataRoot);
        var act = async () => await restorer.RestoreAsync(target);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "覆盖一份还活着的数据库必须有明确同意：现场最常见的事故就是"
            + "'本想恢复到测试目录，手一抖打在正式目录上'");
    }

    [Fact]
    public async Task Restore_refuses_while_the_service_is_still_running()
    {
        var dataRoot = NewScratchDirectory("live");

        using var factory = new ControlServerFactory(dataRoot);
        using var client = factory.CreateClient();
        (await client.GetAsync("/healthz")).EnsureSuccessStatusCode();

        // 备份**允许**在服务运行时做 —— 这正是它的用处：不能为了备份去停一次展厅。
        var target = NewScratchDirectory("backup");
        var sut = new DataBackupService(dataRoot);
        await sut.CreateAsync(target, note: null);

        // 恢复**不允许**。理由不是文件锁，而是：服务会在恢复之后
        // 把它内存里的旧数据再写回去 ⇒ "恢复成功，但数据还是旧的"。
        var restorer = new DataBackupService(dataRoot);
        var act = async () => await restorer.RestoreAsync(target, force: true);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>(
            "服务没停就恢复，会得到一个'看起来成功、其实是旧数据'的结果 —— 比直接失败危险得多");
        ex.Which.Message.Should().Contain(
            "停止 ControlServer 服务",
            "错误消息必须直接告诉现场下一步做什么；原生异常只有一句 'being used by another process'");
    }

    [Fact]
    public async Task Restore_rejects_a_manifest_that_points_outside_the_backup()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();
        (await client.GetAsync("/healthz")).EnsureSuccessStatusCode();

        var dataRoot = Path.GetDirectoryName(factory.DatabaseFile)!;
        var target = NewScratchDirectory("backup");
        var sut = new DataBackupService(dataRoot);
        await sut.CreateAsync(target);

        // manifest 就是一个普通 JSON 文本文件。把它里面的路径改成 ../../xxx，
        // 一个"照单全收"的恢复器就会把文件写到备份目录之外 —— 那是任意位置写入。
        var manifestPath = Path.Combine(target, DataBackupService.ManifestEntryName);
        var json = await File.ReadAllTextAsync(manifestPath);
        var broken = json.Replace(
            "\"path\": \"" + DataBackupService.DatabaseEntryName + "\"",
            "\"path\": \"../../pwned.txt\"");
        broken.Should().NotBe(json, "前置条件：必须真的改到了清单里的路径");
        await File.WriteAllTextAsync(manifestPath, broken);

        var restoreTarget = NewScratchDirectory("restore-into");
        var restorer = new DataBackupService(restoreTarget);
        var act = async () => await restorer.RestoreAsync(target, force: true);

        var ex = await act.Should().ThrowAsync<BackupValidationException>(
            "清单里的路径必须落在归档内；这条不挡，一个能被改的文本文件就等于任意位置写入");
        ex.Which.Message.Should().Contain("逃出", "消息要说清是路径越界，而不是含糊的'校验失败'");

        Directory.Exists(restoreTarget).Should().BeFalse(
            "校验不过时一个字节都不许写 —— 包括不创建目标目录");
    }

    [Fact]
    public async Task Logs_are_excluded_by_default_and_included_on_request()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();
        (await client.GetAsync("/healthz")).EnsureSuccessStatusCode();

        var dataRoot = Path.GetDirectoryName(factory.DatabaseFile)!;
        var logs = Path.Combine(dataRoot, "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "log-2026-10-17.ndjson"), "{\"msg\":\"hi\"}");

        var without = NewScratchDirectory("backup-nolog");
        var sut = new DataBackupService(dataRoot);
        var manifestWithout = await sut.CreateAsync(without);

        File.Exists(Path.Combine(without, "logs", "log-2026-10-17.ndjson")).Should().BeFalse(
            "默认不备日志：它随运行天数线性增长，且不属于'恢复后必须一模一样'的数据");
        manifestWithout.IncludesLogs.Should().BeFalse();
        manifestWithout.Skipped.Should().Contain(
            s => s.Path.StartsWith("logs", StringComparison.Ordinal),
            "跳过日志必须写明理由 —— 否则看清单的人会以为漏了");

        var with = NewScratchDirectory("backup-log");
        var manifestWith = await sut.CreateAsync(with, includeLogs: true);

        File.Exists(Path.Combine(with, "logs", "log-2026-10-17.ndjson")).Should().BeTrue(
            "--include-logs 是给'要留证据'的场景用的，加上它就必须真的带上");
        manifestWith.IncludesLogs.Should().BeTrue(
            "manifest 要记下'这份备份里有没有日志'，否则恢复端无从判断");
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 模拟"服务真的停了"：宿主的 <c>Dispose</c> 只停 Kestrel 与 DI 容器，
    /// 而 <c>Microsoft.Data.Sqlite</c> 的连接池是<b>进程级</b>的，仍会握着库文件。
    /// 不显式清池，后面删目录 / 独占探测都会看到一个"已停服但还是被占用"的假象。
    /// </summary>
    private static void ReleasePools() => SqliteConnection.ClearAllPools();

    private static SqliteConnection OpenWritable(string dbFile)
    {
        var connection = new SqliteConnection($"Data Source={dbFile};Mode=ReadWrite;Pooling=False");
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long CountRows(string dbFile, string table)
    {
        using var connection = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";
        return (long)command.ExecuteScalar()!;
    }

    private static long CountRows(string dbFile, string table, string whereKey)
    {
        using var connection = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM \"{table}\" WHERE \"Key\" = $key;";
        command.Parameters.AddWithValue("$key", whereKey);
        return (long)command.ExecuteScalar()!;
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static string NewScratchDirectory(string tag)
        => Path.Combine(Path.GetTempPath(), "controlserver-tests", "m61-" + tag + "-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDirectoryHard(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(120);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(120);
            }
        }
    }
}
