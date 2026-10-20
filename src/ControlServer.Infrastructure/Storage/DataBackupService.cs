using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ControlServer.Core.Services;

namespace ControlServer.Infrastructure.Storage;

/// <summary>
/// 数据目录的<b>备份 / 恢复</b>（M6.1）。
///
/// <para>
/// 它<b>不做</b>什么，比它做什么更需要说清，所以先列出来：
/// </para>
/// <list type="bullet">
///   <item>
///   🔥 <b>不备份证书私钥</b>。<c>IDataDirectory.CertificateDirectory</c> 的接口注释已经写死：
///   那个目录里是根 CA 私钥，拿到它的人可以签出观看端会认的任意证书 ⇒
///   它<b>不许</b>进备份 / 分发 / 日志导出。这里只带走 <c>controlserver-ca.crt</c>（公钥，本来就要分发给观看端），
///   并且把"为什么少了几样"写进 <see cref="BackupManifest.Skipped"/>，
///   否则下一个人看到备份里没有 <c>https/</c> 会以为漏备，然后"顺手补上" —— 那才是真的把门打开了。
///   </item>
///   <item>
///   <b>不备份安装包目录</b>（<c>downloads/</c>，本机实测一个 Agent 安装包 65 MB）：随时可从发布源重取。
///   </item>
///   <item>
///   <b>默认不备份日志</b>（<c>logs/</c>）：体积随天数线性长，且不是"恢复后要一模一样"的东西。
///   排障需要时用 <c>includeLogs</c>。
///   </item>
///   <item>
///   🔴 <b>不用 <c>File.Copy</c> 拷数据库</b>。库是 WAL 模式
///   （<c>DatabaseInitializer</c> 显式 <c>PRAGMA journal_mode=WAL</c>），
///   正在跑的服务随时可能有"已提交但只在 <c>-wal</c> 里"的行。
///   拷 <c>control.db</c> 拿到的是一份<b>少了最后一截</b>的库，而这种错
///   <b>备份当天完全看不出来，只在真恢复那天爆</b>。
///   所以数据库一律走 <c>VACUUM INTO</c>：它由 SQLite 自己执行，产出的是一致快照。
///   </item>
/// </list>
///
/// <para>
/// <b>manifest 是提交标记</b>：备份过程中任何一步失败，目录里会留下半成品但没有
/// <see cref="ManifestEntryName"/>；恢复端<b>第一步</b>就要求它存在。
/// 于是"半成品"不可能被误当成备份 —— 不需要额外的锁文件或状态位。
/// </para>
/// </summary>
public sealed class DataBackupService
{
    public const string ManifestEntryName = "manifest.json";
    public const string DatabaseEntryName = "control.db";

    /// <summary>备份格式版本。恢复端只认它认识的值 —— 这是唯一的兼容性闸门。</summary>
    public const int FormatVersion = 1;

    /// <summary>随数据一起带走的单文件（数据目录根下）。</summary>
    private static readonly string[] RootFiles =
    [
        "license.lic",
        "license.binding.json",
        "license.trial.json",
    ];

    /// <summary>随数据一起带走的目录（递归）。</summary>
    private static readonly string[] DataDirectories = ["config", "files", "images"];

    /// <summary>证书目录里<b>唯一</b>允许离开这台机器的东西：根 CA 的公钥。</summary>
    private const string PublicCaFileName = "controlserver-ca.crt";

    private readonly string _dataRoot;

    public DataBackupService(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("数据目录不能为空", nameof(dataRoot));
        }

        _dataRoot = Path.GetFullPath(dataRoot);
    }

    // ═══════════════════════════════════════════════════════════
    // 备份
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把 <see cref="_dataRoot"/> 备到 <paramref name="targetDirectory"/>。
    /// 目标目录必须不存在或为空 —— 混进第二次备份的东西，恢复端无从分辨哪份是哪个时刻。
    /// </summary>
    /// <param name="includeLogs">是否连 <c>logs/</c> 一起备（默认否，见类注释）。</param>
    /// <param name="note">过程回调（给命令行用；服务内部备份可以传 null）。</param>
    public async Task<BackupManifest> CreateAsync(
        string targetDirectory,
        bool includeLogs = false,
        Action<string>? note = null,
        CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(targetDirectory);

        // 🔴 备份不能落在数据目录里面。两个理由，任一条都够：
        //    ① 遍历时会看到自己正在写的文件（越备越多）；
        //    ② 备份和数据在同一块盘上，那块盘坏的时候两样一起没 —— 那不是备份。
        if (IsInside(target, _dataRoot))
        {
            throw new InvalidOperationException(
                $"备份目录不能放在数据目录里面（数据目录：{_dataRoot}，备份目录：{target}）。"
                + "理由有两条：备份过程中会把自己的产物再备一遍；"
                + "而且放在同一块盘上的备份，在盘坏的那天会和原数据一起没。");
        }

        var databaseFile = Path.Combine(_dataRoot, DatabaseEntryName);
        if (!File.Exists(databaseFile))
        {
            throw new InvalidOperationException(
                $"数据目录里没有 {DatabaseEntryName}（{_dataRoot}）。"
                + "这不是一个已初始化的数据目录，别拿它当备份源。");
        }

        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            throw new InvalidOperationException(
                $"备份目标目录非空：{target}。换一个空目录，或先把它挪走 —— "
                + "两次备份混在一起，恢复时分不清哪份是哪个时刻。");
        }

        Directory.CreateDirectory(target);

        var files = new List<BackupFileEntry>();
        var skipped = new List<BackupSkipEntry>();

        // ── ① 数据库：VACUUM INTO 一致快照 ──
        note?.Invoke($"快照数据库 {databaseFile} …");
        await SnapshotDatabaseAsync(databaseFile, target, cancellationToken).ConfigureAwait(false);
        var snapshotPath = Path.Combine(target, DatabaseEntryName);
        files.Add(Describe(DatabaseEntryName, snapshotPath));

        // ── ② 数据目录 ──
        foreach (var directory in DataDirectories)
        {
            var source = Path.Combine(_dataRoot, directory);
            if (!Directory.Exists(source))
            {
                skipped.Add(new BackupSkipEntry(directory + "/", "源目录不存在（这台机器还没用它）"));
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(_dataRoot, file);
                await CopyIntoAsync(file, target, relative, cancellationToken).ConfigureAwait(false);
                files.Add(Describe(relative, Path.Combine(target, relative)));
            }
        }

        // ── ③ 根下的单文件 ──
        foreach (var name in RootFiles)
        {
            var source = Path.Combine(_dataRoot, name);
            if (!File.Exists(source))
            {
                skipped.Add(new BackupSkipEntry(name, "文件不存在（例如尚未导入授权或还没进过试用）"));
                continue;
            }

            await CopyIntoAsync(source, target, name, cancellationToken).ConfigureAwait(false);
            files.Add(Describe(name, Path.Combine(target, name)));
        }

        // ── ④ 证书目录：只带走公钥 ──
        var certificateDirectory = Path.Combine(_dataRoot, "https");
        if (Directory.Exists(certificateDirectory))
        {
            var publicCa = Path.Combine(certificateDirectory, PublicCaFileName);
            if (File.Exists(publicCa))
            {
                await CopyIntoAsync(publicCa, target, "https/" + PublicCaFileName, cancellationToken)
                    .ConfigureAwait(false);
                files.Add(Describe("https/" + PublicCaFileName, Path.Combine(target, "https", PublicCaFileName)));
            }

            skipped.Add(new BackupSkipEntry(
                "https/（私钥部分）",
                "含根 CA 与服务器证书的私钥及口令。它属于这台机器，不属于这份数据："
                + $"只有 {PublicCaFileName}（公钥）被带走。恢复时原目录保持不动。"));
        }

        skipped.Add(new BackupSkipEntry(
            "downloads/",
            "Agent 安装包，体积大且随时能从发布源重新取。恢复后按需重新分发即可。"));

        if (includeLogs)
        {
            var logs = Path.Combine(_dataRoot, "logs");
            if (Directory.Exists(logs))
            {
                foreach (var file in Directory.EnumerateFiles(logs, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(_dataRoot, file);
                    await CopyIntoAsync(file, target, relative, cancellationToken).ConfigureAwait(false);
                    files.Add(Describe(relative, Path.Combine(target, relative)));
                }
            }
        }
        else
        {
            skipped.Add(new BackupSkipEntry(
                "logs/",
                "日志体积随运行天数线性增长，且不属于'恢复后必须一模一样'的数据。"
                + "需要留证据时用 --include-logs 重备一次。"));
        }

        skipped.Add(new BackupSkipEntry(
            DatabaseEntryName + "-wal / -shm",
            "它们是数据库的中途状态，不是数据本身。快照已由 VACUUM INTO 合成自洽的单文件。"));

        // ── ⑤ manifest 最后写：它是"这一步之前全部成功"的唯一凭据 ──
        var manifest = new BackupManifest(
            FormatVersion,
            BeijingTime.Now,
            ServerVersion(),
            _dataRoot,
            includeLogs,
            files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(),
            skipped);

        await WriteManifestAsync(target, manifest, cancellationToken).ConfigureAwait(false);

        note?.Invoke(
            $"完成：{manifest.Files.Count} 个文件 / {manifest.Files.Sum(f => f.Size) / 1024.0 / 1024.0:0.0} MB"
            + $"，跳过 {manifest.Skipped.Count} 项（已写进 {ManifestEntryName}）。");

        return manifest;
    }

    /// <summary>
    /// 用 <c>VACUUM INTO</c> 生成一份一致快照，落到 <c>target/control.db</c>。
    ///
    /// <para>
    /// ⚠️ <c>VACUUM INTO</c> 要求目标文件<b>不存在</b>（SQLite 会直接拒绝覆盖），
    /// 所以先写一个临时名再改名。临时名带 GUID：万一上一次失败留了残骸，这一次也不会撞上。
    /// </para>
    /// <para>
    /// ⚠️ 路径要按 SQL 字面量转义（<c>'</c> 变 <c>''</c>）。Windows 路径里带单引号不算罕见
    /// （<c>C:\Users\O'Brien\…</c>），不转义就是语法错，恢复那天才发现备份从来没成功过。
    /// </para>
    /// </summary>
    private static async Task SnapshotDatabaseAsync(
        string databaseFile,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        var staging = Path.Combine(targetDirectory, ".control.db." + Guid.NewGuid().ToString("N") + ".snapshot");

        try
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = databaseFile,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                    Pooling = false,
                    DefaultTimeout = 30,
                }.ToString());

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // 服务正在跑的时候数据库是热的：给足等待时间，别让一次正在提交的事务
            // 把备份打成 SQLITE_BUSY。30 秒远大于任何一次正常提交。
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=30000;";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var vacuum = connection.CreateCommand())
            {
                vacuum.CommandText = "VACUUM INTO " + Quote(Path.GetFullPath(staging)) + ";";
                await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            TryDelete(staging);
            throw new InvalidOperationException(
                $"数据库快照失败（{ex.SqliteErrorCode}）：{ex.Message}。"
                + "若提示 database is locked，说明有别的东西长时间占着库；"
                + "备份可以重试，但不要为了'让它跑通'去停服务或删 -wal。", ex);
        }

        var final = Path.Combine(targetDirectory, DatabaseEntryName);
        TryDelete(final);
        File.Move(staging, final);
    }

    // ═══════════════════════════════════════════════════════════
    // 恢复
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把 <paramref name="backupDirectory"/> 的内容铺回 <see cref="_dataRoot"/>。
    ///
    /// <para>
    /// 顺序是<b>先全部校验、再动一个字节</b>。校验做到一半就开始覆盖，会出现
    /// "一半是新的一半是旧的"，而且旧的那一半已经被毁掉了 —— 那比不恢复更糟。
    /// </para>
    /// <para>
    /// 🔴 恢复前会删掉目标目录里遗留的 <c>control.db-wal</c> / <c>-shm</c>：
    /// 它们属于<b>恢复之前那个库</b>，跟恢复进来的库不是同一份。留着它们，
    /// SQLite 打开时会拿旧 WAL 去套新库（salt 不匹配时它会丢弃并重置，
    /// 但"依赖它自己纠正"不该是设计的一部分）。
    /// </para>
    /// </summary>
    /// <param name="force">
    /// 目标目录里已经有一份数据库时，必须显式给 true。
    /// 现场最常见的事故是"本想恢复到测试目录，手一抖打在正式目录上"。
    /// </param>
    public async Task<RestoreReport> RestoreAsync(
        string backupDirectory,
        bool force = false,
        Action<string>? note = null,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(backupDirectory);
        var manifest = ReadManifest(source); // 没有/损坏 ⇒ 这里就抛

        if (manifest.FormatVersion != FormatVersion)
        {
            throw new BackupValidationException(
                $"这个备份的格式版本是 {manifest.FormatVersion}，本程序只认 {FormatVersion}。"
                + "跨大版本恢复需要先升级程序，不要手改 manifest 里的版本号。");
        }

        // ── ① 先把每个文件校验一遍，一个字节都不写 ──
        var mismatches = new List<string>();
        foreach (var entry in manifest.Files)
        {
            // 路径必须落在备份目录内：manifest 是可被编辑的文本文件，
            // 若它写着 ../../Windows/System32/xxx，照做就是把任意写漏洞送上门。
            var path = ResolveInside(source, entry.Path);
            if (!File.Exists(path))
            {
                mismatches.Add($"{entry.Path}：清单里有、包里没有");
                continue;
            }

            var info = new FileInfo(path);
            if (info.Length != entry.Size)
            {
                mismatches.Add($"{entry.Path}：大小 {info.Length} ≠ 清单 {entry.Size}");
                continue;
            }

            if (!string.Equals(Sha256Of(path), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add($"{entry.Path}：内容哈希与清单不符（文件被改过或拷贝不完整）");
            }
        }

        if (mismatches.Count > 0)
        {
            throw new BackupValidationException(
                "备份校验没过，未做任何写入。以下文件与清单不符："
                + Environment.NewLine + string.Join(Environment.NewLine, mismatches.Take(20))
                + (mismatches.Count > 20 ? $"{Environment.NewLine}…共 {mismatches.Count} 项" : string.Empty)
                + Environment.NewLine
                + "这说明包本身有问题（拷贝中断 / 磁盘损坏 / 有人改过）。"
                + "请换一份备份，不要改清单里的哈希去'让它通过'。");
        }

        // ── ② 目标目录的许可 ──
        Directory.CreateDirectory(_dataRoot);
        var liveDatabase = Path.Combine(_dataRoot, DatabaseEntryName);
        if (File.Exists(liveDatabase) && !force)
        {
            throw new InvalidOperationException(
                $"目标目录里已经有一份数据库（{liveDatabase}）。恢复会覆盖它，"
                + "所以必须显式加 --force。做之前先确认路径 —— 打错一个字符就是拿备份盖掉正式数据。");
        }

        // ── ③ 数据库还被占着就不能动 ──
        //    这一条不是为了"保护文件"，而是因为：恢复时服务若还在跑，它会把自己内存里
        //    的旧数据**在恢复之后**再写回库 —— 于是备份白恢复，而且没人会知道。
        //    原生异常是 "The process cannot access the file"，现场读不懂它要干什么。
        if (File.Exists(liveDatabase))
        {
            EnsureNotInUse(liveDatabase);
        }

        // ── ④ 清掉属于"旧库"的中途状态 ──
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var stale = liveDatabase + suffix;
            if (File.Exists(stale))
            {
                note?.Invoke($"删除遗留的 {DatabaseEntryName}{suffix}（它属于恢复前那个库，不能拿去套新库）");
                TryDelete(stale);
            }
        }

        // ── ⑤ 铺文件 ──
        long bytes = 0;
        foreach (var entry in manifest.Files)
        {
            var from = ResolveInside(source, entry.Path);
            await CopyIntoAsync(from, _dataRoot, entry.Path, cancellationToken).ConfigureAwait(false);
            bytes += entry.Size;
        }

        note?.Invoke(
            $"已恢复 {manifest.Files.Count} 个文件 / {bytes / 1024.0 / 1024.0:0.0} MB"
            + $"（备份时间 {manifest.CreatedAt:yyyy-MM-dd HH:mm:ss}，来自 {manifest.SourceRoot}）。");

        return new RestoreReport(manifest.Files.Count, bytes, manifest.ServerVersion, manifest.CreatedAt);
    }

    // ═══════════════════════════════════════════════════════════
    // manifest
    // ═══════════════════════════════════════════════════════════

    public static BackupManifest ReadManifest(string backupDirectory)
    {
        var path = Path.Combine(backupDirectory, ManifestEntryName);
        if (!File.Exists(path))
        {
            throw new BackupValidationException(
                $"这个目录里没有 {ManifestEntryName}（{backupDirectory}）。"
                + "备份过程的最后一步才是写清单，所以'没有清单'意味着那次备份没有走完 —— "
                + "它可能缺文件、也可能库是半截的。不要拿它去恢复。");
        }

        try
        {
            var json = File.ReadAllText(path);
            var manifest = JsonSerializer.Deserialize<BackupManifest>(json, JsonOptions);
            if (manifest is null)
            {
                throw new BackupValidationException($"{ManifestEntryName} 是空的。");
            }

            return manifest;
        }
        catch (JsonException ex)
        {
            throw new BackupValidationException($"{ManifestEntryName} 不是合法的 JSON：{ex.Message}", ex);
        }
    }

    private static async Task WriteManifestAsync(
        string targetDirectory,
        BackupManifest manifest,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(targetDirectory, ManifestEntryName);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // manifest 里"为什么跳过"是给人看的中文，转成 \uXXXX 就没人看得懂了。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ═══════════════════════════════════════════════════════════
    // 工具
    // ═══════════════════════════════════════════════════════════

    private static async Task CopyIntoAsync(
        string sourceFile,
        string targetRoot,
        string relative,
        CancellationToken cancellationToken)
    {
        var destination = ResolveInside(targetRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // 用流拷贝而不是 File.Copy：服务在跑时源文件可能正被追加写，
        // File.Copy 会因共享冲突失败，而流式拷贝允许对方继续写。
        await using var input = new FileStream(
            sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static BackupFileEntry Describe(string relative, string path)
    {
        var info = new FileInfo(path);
        return new BackupFileEntry(NormalizeRelative(relative), info.Length, Sha256Of(path));
    }

    private static string NormalizeRelative(string relative)
        => relative.Replace('\\', '/');

    /// <summary>
    /// 把清单里的相对路径解析成绝对路径，并<b>硬性要求它落回根目录内</b>。
    /// 这一条防的不是手滑，而是"manifest 本身被改过"：
    /// 它是个普通 JSON 文本，让 <c>../../</c> 通过就等于把任意位置写入送出去。
    /// </summary>
    private static string ResolveInside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            throw new BackupValidationException("清单里有一个空的文件路径。");
        }

        if (Path.IsPathRooted(relative))
        {
            throw new BackupValidationException(
                $"清单里的路径是绝对路径（{relative}）。归档内一律用相对路径，"
                + "绝对值会让恢复写到别的地方去。");
        }

        var rootFull = Path.GetFullPath(root);
        var resolved = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!resolved.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new BackupValidationException(
                $"清单里的路径逃出了根目录（{relative}）。这份备份不可信，已停止。");
        }

        return resolved;
    }

    private static bool IsInside(string candidate, string root)
    {
        var rootFull = Path.GetFullPath(root);
        var rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        var candidateFull = Path.GetFullPath(candidate);

        return candidateFull.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
            || candidateFull.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// 独占打开一次，确认没有别的进程（或本进程的连接池）还开着这个库。
    ///
    /// <para>
    /// 🔴 为什么必须挡：恢复时服务若还在跑，它会在<b>恢复之后</b>把内存里的旧数据再写回库。
    /// 于是"恢复成功"了，数据却是旧的 —— 这种结果比报错坏得多，
    /// 因为现场的结论会变成"备份没用"，而不是"操作顺序错了"。
    /// </para>
    /// <para>
    /// 用 <see cref="FileAccess.Read"/> 而不是 ReadWrite：库文件可能被设成只读，
    /// 那也该能探测（只读库的占用与读写权限无关）。
    /// </para>
    /// </summary>
    private static void EnsureNotInUse(string databaseFile)
    {
        try
        {
            using var probe = new FileStream(
                databaseFile, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"数据库正被占用（{databaseFile}），现在不能恢复。"
                + "请先停止 ControlServer 服务并确认进程已退出："
                + "Windows 用 services.msc 或 sc stop；Linux 用 systemctl stop controlserver。"
                + "恢复本身是替换整个库文件，服务在跑会把内存里的旧数据再写回去，"
                + "结果是'恢复成功但数据还是旧的'。", ex);
        }
    }

    private static string Quote(string path) => "'" + path.Replace("'", "''") + "'";

    private static string ServerVersion()
        => typeof(DataBackupService).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // 删不掉就留着：临时文件带 GUID，不会影响下一次；
            // 而为了删一个残骸去抛异常，会把"备份已成功"变成一次假失败。
        }
    }
}

/// <summary>备份清单（<c>manifest.json</c>）。它的存在本身就是"这次备份走完了"的凭据。</summary>
public sealed record BackupManifest(
    int FormatVersion,
    DateTimeOffset CreatedAt,
    string ServerVersion,
    string SourceRoot,
    bool IncludesLogs,
    IReadOnlyList<BackupFileEntry> Files,
    IReadOnlyList<BackupSkipEntry> Skipped);

public sealed record BackupFileEntry(string Path, long Size, string Sha256);

/// <summary>
/// 一份<b>故意没备</b>的东西，以及理由。
/// <para>
/// 为什么要把它写进产物：备份里少了 <c>https/</c> 而不解释，下一个人会当成漏备，
/// 然后"顺手补上" —— 那等于每次备份都往外递一张根 CA 私钥。
/// </para>
/// </summary>
public sealed record BackupSkipEntry(string Path, string Reason);

public sealed record RestoreReport(
    int FileCount,
    long RestoredBytes,
    string ServerVersion,
    DateTimeOffset BackupCreatedAt);

/// <summary>备份包本身不可信（缺清单 / 哈希不符 / 路径越界 / 版本不认）。</summary>
public sealed class BackupValidationException : Exception
{
    public BackupValidationException(string message) : base(message)
    {
    }

    public BackupValidationException(string message, Exception inner) : base(message, inner)
    {
    }
}
