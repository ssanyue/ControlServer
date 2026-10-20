using System.Globalization;
using System.Security.Cryptography;
using ControlServer.Core.Services;
using ControlServer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Server.Cli;

/// <summary>
/// <c>--backup</c> / <c>--restore</c> / <c>--dbinfo</c> / <c>--health</c> 四个维护子命令（M6.1 / M6.4 / M6.5）。
///
/// <para>
/// <b>为什么做成同一个 exe 的子命令，而不是一份独立脚本：</b>
/// 数据库是 SQLite 的 WAL 模式，备份必须走 <c>VACUUM INTO</c> 才能拿到一致快照。
/// 现场 Windows 机器上没有 <c>sqlite3.exe</c> 是常态；而另发一个小工具又多一个
/// "要跟主程序版本对齐"的东西。用服务自己的 exe，SQLite 引擎、数据目录解析、
/// 默认路径全都不用重复实现一遍 —— 少一处能对不上的地方。
/// </para>
///
/// <para>
/// 🔥 <b>必须在建 <c>WebApplication</c> 之前分流</b>。两条理由：
/// ① <c>CreateBuilder</c> 会把 <c>--backup D:\bk</c> 这种裸参数当成非法命令行配置直接抛；
/// ② 真要启动到一半再 return，Kestrel 已经绑过端口了。
/// </para>
///
/// <para>
/// 🔥 <b>必须显式要求保留控制台</b>。服务默认会 <c>FreeConsole</c> 脱离终端
/// （"关掉启动它的那个 CMD 不该停服务"）。照搬这个默认，运维敲完
/// <c>--backup</c> 会看到一个什么都没输出的黑窗口，然后以为命令没跑。
/// </para>
///
/// <para>
/// ⚠️ 控制台输出<b>只用中文与 GBK 里存在的符号</b>（<c>√</c> / <c>×</c>），不用 emoji。
/// 这不是洁癖：中文 Windows 的控制台码页是 936，emoji 编不出来会静默变成 <c>?</c>，
/// 于是"√ 备份完成"在现场显示成"? 备份完成" —— 演练时当场见到过。
/// </para>
/// </summary>
public sealed record MaintenanceInvocation(string Verb, string? Target, bool IncludeLogs, bool Force);

public static class DataMaintenanceCommand
{
    private const string Backup = "backup";
    private const string Restore = "restore";

    /// <summary>
    /// <c>--dbinfo</c>（M6.4）：只读地把"这个数据目录里到底有什么"打出来（迁移条数 + 各表行数）。
    /// <para>
    /// <b>为什么升级/回滚需要它</b>：验收不能只有"页面能打开" —— 那证明不了数据还在。
    /// 升级前后各跑一次、逐行比对，才是"升级不丢数据"的证据。
    /// </para>
    /// <para>
    /// <b>为什么又做成 exe 的子命令</b>：现场 Windows 机器上没有 <c>sqlite3.exe</c> 是常态，
    /// 也没有 Python。用服务自己的 exe，SQLite 引擎不用重复实现，也不会出现
    /// "查库的工具和写库的程序用的不是同一个版本"这种事。
    /// </para>
    /// <para>
    /// 输出刻意用 <c>键=值</c>（<c>迁移条数=13</c>），脚本能直接 grep / 比对；
    /// 人也看得懂。⚠️ 控制台只输出 GBK 里有的字符。
    /// </para>
    /// </summary>
    private const string DbInfo = "dbinfo";

    /// <summary>
    /// <c>--health</c>（M6.5）：一条命令把巡检全项摊开（磁盘余量 / 库与 WAL 体积 / 日志体积 /
    /// 证书到期 / 授权到期 / 服务探活），带退出码，能直接进定时任务。
    /// <para>
    /// 与 <c>--dbinfo</c> 分工：<c>dbinfo</c> 回答"<b>数据还在不在</b>"（升级前后比对）；
    /// <c>health</c> 回答"<b>这台机器还能跑多久</b>"（每周巡检）。
    /// 刻意分成两个命令：把易变的项（磁盘余量、探活）混进 <c>dbinfo</c>，
    /// 会让升级脚本的前后比对每天都在"不一致"。
    /// </para>
    /// </summary>
    private const string Health = "health";

    /// <summary>
    /// <c>--files-recover</c>（M7.2）：把断链（库里有记录、盘上没文件）按备份里的同名文件找回来。
    /// <para>
    /// <b>为什么必须有它</b>：M7.1 把断链定成"只报警、永不自动删记录"—— 这是对的，
    /// 但报警之后的下一步是"人得去翻备份、找出同名的那个、再拷回来"。
    /// 手工做这件事有两个坑：① <c>StoredName</c> 是 <c>f{Id}_{guid}.bin</c> 这种名字，
    /// 人在备份里靠眼睛对得上，但很容易拷错版本；② 拷之前<b>没人验内容</b>，
    /// 把一份"同名但不同内容"的文件塞回去，比断链更坏（下载下来是坏数据，而且看起来好了）。
    /// 所以这件事必须由程序做：按 <c>files.repo</c> 的 <c>StoredName</c> 找、按大小与 SHA-256 验、验过才放回去。
    /// </para>
    /// <para>
    /// ⚠️ 它<b>只新增文件，不改库</b>：记录本来就在，缺的是盘上那份。
    /// 也<b>不覆盖</b>仓库里已存在的同名文件（那说明不是断链），除非显式 <c>--force</c>。
    /// </para>
    /// </summary>
    private const string FilesRecover = "files-recover";

    /// <summary>
    /// 认不认这次调用是维护命令。只认 <c>--backup</c> / <c>--restore</c> / <c>--dbinfo</c> / <c>--health</c>
    /// （以及单独的 <c>--help</c>）。
    /// <para>
    /// ⚠️ 不能"只要 args 非空就当维护命令"：服务的正常启动参数长这样
    /// <c>--Server:Storage:DataDir=D:\x</c>，那也是一堆参数。
    /// </para>
    /// </summary>
    public static bool TryParse(string[] args, out MaintenanceInvocation? invocation)
    {
        invocation = null;

        var verbIndex = Array.FindIndex(args, a =>
            string.Equals(a, "--backup", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "--restore", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "--dbinfo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "--health", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "--files-recover", StringComparison.OrdinalIgnoreCase));

        if (verbIndex < 0)
        {
            // `--help` 只有"单独出现"时才算维护帮助，免得抢掉服务自己的参数。
            //
            // 🔴 这里必须**同时**给出 invocation：只返回 true 而 invocation 为 null，
            //    调用方 `Execute(invocation!)` 会空引用崩掉 —— 而崩在 `--help` 上是最难看的。
            if (args.Length == 1 && (args[0] is "--help" or "-h" or "/?"))
            {
                invocation = new MaintenanceInvocation("help", null, false, false);
                return true;
            }

            return false;
        }

        var verb = args[verbIndex].TrimStart('-').ToLowerInvariant();

        string? target = null;
        var includeLogs = false;
        var force = false;

        for (var i = 0; i < args.Length; i++)
        {
            if (i == verbIndex) continue;

            var arg = args[i];

            if (string.Equals(arg, "--include-logs", StringComparison.OrdinalIgnoreCase))
            {
                includeLogs = true;
                continue;
            }

            if (string.Equals(arg, "--force", StringComparison.OrdinalIgnoreCase))
            {
                force = true;
                continue;
            }

            // 位置参数（目录）：取第一个不以 -- 开头的。
            if (!arg.StartsWith("--", StringComparison.Ordinal) && target is null)
            {
                target = arg;
                continue;
            }

            // 🔴 认不出来的参数必须报错。静默忽略的代价是：
            //    把 --includelogs 拼错成 --includelog，备份照样"成功"，只是没带日志。
            invocation = new MaintenanceInvocation(verb + "!unknown-arg:" + arg, null, false, false);
            return true;
        }

        invocation = new MaintenanceInvocation(verb, target, includeLogs, force);
        return true;
    }

    /// <summary>执行并返回进程退出码（0 成功 / 1 参数错 / 2 执行失败）。</summary>
    public static int Execute(MaintenanceInvocation invocation)
    {
        // 认不出来的参数一律报错，对**所有**子命令生效。
        // （早先只判 backup/restore，于是 `--health --verbose` 会被静默忽略 —— 那正是
        //  本文件注释里反复强调的那类错误：拼错一个字母，命令照样"成功"，只是少做了事。）
        if (invocation.Verb.Contains("!unknown-arg:", StringComparison.Ordinal))
        {
            var unknown = invocation.Verb[(invocation.Verb.IndexOf(':') + 1)..];
            Console.Error.WriteLine($"【参数不认识】{unknown}");
            PrintHelp(Console.Error);
            return 1;
        }

        var dataRoot = ResolveDataRoot();
        var isRestore = invocation.Verb == Restore;
        var isDbInfo = invocation.Verb == DbInfo;
        var isHealth = invocation.Verb == Health;

        // 无论备份还是恢复，第一件事都是把"我读到的数据目录"打出来。
        // 读到错目录时，后面每一步都还在"正确"地执行 —— 只有这一行能让人当场发现。
        Console.WriteLine($"数据目录：{dataRoot}");

        // 🔴 只有备份要求目录已存在。
        //    恢复**必须**能从零开始：灾备机 / 换机器 / 刚重装完，那台机器上根本没有这个目录。
        //    这里早先写成了"不存在就失败"，演练里当场把恢复挡死了 —— 而"删了库再恢复"
        //    恰恰是这条命令唯一的正经用法。
        //
        //    --health 也不要求：一台刚装完还没起过服务的机器，"目录还不存在"本身就是
        //    巡检要报告的结论，不是"命令用错了"。
        if (!isRestore && !isHealth && !Directory.Exists(dataRoot))
        {
            Console.Error.WriteLine($"【失败】这个数据目录不存在：{dataRoot}");
            Console.Error.WriteLine("  如果服务装在别处，用环境变量 Server__Storage__DataDir 指定，或改 appsettings.json 的 Server:Storage:DataDir。");
            return 2;
        }

        if (isRestore && !Directory.Exists(dataRoot))
        {
            Console.WriteLine("（这个目录还不存在 —— 恢复会把它建出来）");
        }

        // --dbinfo / --health 不需要备份服务，也不需要能写 —— 它们只读。
        if (isDbInfo)
        {
            return RunDbInfo(dataRoot);
        }

        if (isHealth)
        {
            return HealthCheckCommand.Run(dataRoot);
        }

        // 找回是"往 repo/ 里补文件"，既不需要备份服务，也不该被 backup/restore 的
        // 参数校验（比如 --force 只对 restore 有意义）挡住。
        if (invocation.Verb == FilesRecover)
        {
            return RunFilesRecover(dataRoot, invocation.Target, invocation.Force, Console.Out, Console.Error);
        }

        var service = new DataBackupService(dataRoot);

        try
        {
            switch (invocation.Verb)
            {
                case Backup:
                    return RunBackup(service, invocation, dataRoot);

                case Restore:
                    return RunRestore(service, invocation);

                default:
                    PrintHelp(Console.Out);
                    return 0;
            }
        }
        catch (BackupValidationException ex)
        {
            Console.Error.WriteLine("【备份包不可用】" + ex.Message);
            return 2;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine("【失败】" + ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            // 兜底：不吞。现场看到一句"失败了"比看到一个半截的"成功"好。
            Console.Error.WriteLine($"【意外失败】{ex.GetType().Name}：{ex.Message}");
            return 2;
        }
    }

    /// <summary>库里的一行仓库记录（找回需要摘要，对账不需要）。</summary>
    private sealed record RepoRow(int Id, string Name, string StoredName, long Size, string? Sha256);

    /// <summary>
    /// 只读地把 <c>file_repo_items</c> 读成 <see cref="RepoRow"/>。
    /// <para>
    /// 与 <c>--health</c> 一样刻意不用 EF：这个命令要能在服务起不来时照跑。
    /// </para>
    /// </summary>
    private static List<RepoRow> ReadRepoRows(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };

        using var conn = new SqliteConnection(builder.ToString());
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select Id, Name, StoredName, Size, Sha256 from file_repo_items order by Id";

        using var reader = cmd.ExecuteReader();

        var rows = new List<RepoRow>();
        while (reader.Read())
        {
            rows.Add(new RepoRow(
                Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.GetString(2),
                Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    /// <summary>
    /// <c>--files-recover &lt;备份目录&gt;</c>：把断链按备份里的同名文件找回来。
    /// <para>
    /// <b>判据只有一条</b>：<b>内容对得上</b>（大小 + SHA-256）。同名不算、大小对也不算 ——
    /// 把一份"同名但内容不同"的文件放进 <c>repo/</c>，会把「断链（能看出来的坏）」
    /// 变成「下载下来是坏数据（看不出来的坏）」，两者严重程度完全不同。
    /// </para>
    /// <para>
    /// 不加备份目录时<b>只报告不执行</b>（退出码 2），并把明细列出来：
    /// 现场最常见的情况是"知道该补，但不知道该从哪个备份补"，
    /// 而这一步恰恰是最容易拷错版本的地方。
    /// </para>
    /// </summary>
    internal static int RunFilesRecover(
        string dataRoot,
        string? source,
        bool force,
        TextWriter output,
        TextWriter error)
    {
        var dbPath = Path.Combine(dataRoot, "control.db");
        var repoDir = Path.Combine(dataRoot, "files", "repo");

        if (!File.Exists(dbPath))
        {
            error.WriteLine($"【失败】这个目录里没有 control.db：{dbPath}");
            return 2;
        }

        List<RepoRow> rows;
        try
        {
            rows = ReadRepoRows(dbPath);
        }
        catch (Exception ex)
        {
            error.WriteLine($"【失败】读不出文件仓库记录：{ex.GetType().Name}: {ex.Message}");
            error.WriteLine("  数据目录被 ACL 收紧时，普通用户连只读打开都做不到（WAL 需要建 -shm）——请提权执行。");
            return 2;
        }

        var lost = new List<RepoRow>();

        foreach (var row in rows)
        {
            // 单段名守卫：这个命令会按 StoredName **写** repo/ 下的文件。
            // 一个被改坏的库里若出现 `..\..\https\server.pfx`，照着写就等于覆盖任意文件。
            if (row.StoredName.Length == 0
                || !string.Equals(row.StoredName, Path.GetFileName(row.StoredName), StringComparison.Ordinal)
                || row.StoredName.Contains("..", StringComparison.Ordinal))
            {
                error.WriteLine($"【跳过】id={row.Id} 的 StoredName 不是单段名，不参与找回：{row.StoredName}");
                continue;
            }

            if (!File.Exists(Path.Combine(repoDir, row.StoredName)))
            {
                lost.Add(row);
            }
        }

        output.WriteLine($"files.db.count={rows.Count}");
        output.WriteLine($"lost={lost.Count}");

        if (lost.Count == 0)
        {
            output.WriteLine("断链=0：库里的记录在盘上都有文件，不需要找回。");
            return 0;
        }

        output.WriteLine();
        output.WriteLine("断链明细（库里有记录、files/repo 下没有文件）：");
        foreach (var row in lost)
        {
            output.WriteLine($"  id={row.Id}  {row.StoredName}  {row.Size} 字节  原名={row.Name}");
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            error.WriteLine();
            error.WriteLine("【需要备份目录】这些文件要从备份里找回来。用法：");
            error.WriteLine("  ControlServer.Server.exe --files-recover <备份目录>");
            error.WriteLine("  <备份目录> 就是当初 --backup 的目标目录（里面应当有 files/repo/）。");
            error.WriteLine("  只有内容对得上（大小 + SHA-256）的文件才会被放回去；--force 才覆盖已存在的同名文件。");
            return 2;
        }

        if (!Directory.Exists(source))
        {
            error.WriteLine($"【失败】备份目录不存在：{source}");
            return 2;
        }

        // 备份的布局是 <备份目录>/files/repo/<StoredName>；直接指到这个子目录也认。
        var searchRoot = Directory.Exists(Path.Combine(source, "files", "repo"))
            ? Path.Combine(source, "files", "repo")
            : source;

        var candidates = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;

        try
        {
            foreach (var path in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
            {
                scanned++;

                var name = Path.GetFileName(path);
                if (!candidates.TryGetValue(name, out var list))
                {
                    list = [];
                    candidates[name] = list;
                }

                // 同一份备份里同名文件正常情况下只会有一个；留 5 个上限只是为了
                // 手抄目录这种怪情况不至于把整轮拖慢。
                if (list.Count < 5)
                {
                    list.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            error.WriteLine($"【失败】扫不动备份目录：{ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        output.WriteLine();
        output.WriteLine($"候选目录：{searchRoot}");
        output.WriteLine($"候选文件：{scanned} 个");

        Directory.CreateDirectory(repoDir);

        var recovered = 0;
        var absent = 0;
        var skippedExisting = 0;
        var rejected = 0;

        foreach (var row in lost)
        {
            if (!candidates.TryGetValue(row.StoredName, out var found))
            {
                absent++;
                output.WriteLine($"  没找到：id={row.Id} {row.StoredName}");
                continue;
            }

            // 同名可能有多份（不同时刻的备份 / 手工拷过）：挑**内容对得上**的那一份。
            string? pick = null;
            var reason = "没有一份内容对得上";

            foreach (var candidate in found)
            {
                var mismatch = CheckCandidate(candidate, row);
                if (mismatch is null)
                {
                    pick = candidate;
                    break;
                }

                reason = mismatch;
            }

            if (pick is null)
            {
                rejected++;
                output.WriteLine($"  拒绝：id={row.Id} {row.StoredName} —— {reason}");
                continue;
            }

            var dest = Path.Combine(repoDir, row.StoredName);

            if (File.Exists(dest) && !force)
            {
                // 扫描之后才出现（并发 / 上一轮部分成功）：默认不覆盖。
                skippedExisting++;
                output.WriteLine($"  已存在（不覆盖，需要覆盖加 --force）：id={row.Id} {row.StoredName}");
                continue;
            }

            try
            {
                // 先写临时名再改名：中途失败不会在 repo/ 里留下半个文件 ——
                // 半个文件会让对账认为"盘上有"，比缺文件更难查。
                var temp = dest + ".recover";
                File.Copy(pick, temp, overwrite: true);
                File.Move(temp, dest, overwrite: force);

                recovered++;
                output.WriteLine($"  已找回：id={row.Id} {row.StoredName} ← {pick}");
            }
            catch (Exception ex)
            {
                rejected++;
                error.WriteLine($"【失败】id={row.Id} {row.StoredName}：{ex.GetType().Name}: {ex.Message}");
            }
        }

        output.WriteLine();
        output.WriteLine($"recovered={recovered}");
        output.WriteLine($"absent={absent}");
        output.WriteLine($"skipped.existing={skippedExisting}");
        output.WriteLine($"rejected={rejected}");

        if (recovered == lost.Count)
        {
            output.WriteLine("√ 全部断链已找回。不用重启服务 —— 下载是每次现读磁盘的。");
            return 0;
        }

        error.WriteLine(
            $"× 还有 {lost.Count - recovered} 条没找回，files.lost 会继续报警。" +
            "换一份更早的备份再跑一次（已经找回的不会重复处理）。");

        return 2;
    }

    /// <summary>
    /// 候选文件能不能顶替这条记录。<c>null</c> = 可以。
    /// <para>
    /// 库里没有摘要（老数据 / 手工插的行）时只比大小 —— 这种情况会在输出里如实写"仅按大小"，
    /// 而不是悄悄当成"验过了"。
    /// </para>
    /// </summary>
    private static string? CheckCandidate(string candidate, RepoRow row)
    {
        long length;

        try
        {
            length = new FileInfo(candidate).Length;
        }
        catch (Exception ex)
        {
            return $"读不到文件长度（{ex.GetType().Name}: {ex.Message}）";
        }

        if (length != row.Size)
        {
            return $"大小不符（备份里 {length} 字节，库里 {row.Size} 字节）";
        }

        if (row.Sha256 is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(candidate);
            var actual = Convert.ToHexString(SHA256.HashData(stream));

            return string.Equals(actual, row.Sha256, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"摘要不符（备份里 {actual}，库里 {row.Sha256}）";
        }
        catch (Exception ex)
        {
            return $"算不出摘要（{ex.GetType().Name}: {ex.Message}）";
        }
    }

    /// <summary>
    /// <c>--dbinfo</c>：只读地把数据目录的内容摊开（迁移条数 + 各表行数 + 文件仓库体积）。
    /// <para>
    /// 输出用 <c>键=值</c>，一行一项 —— 升级脚本拿它做"升级前 / 升级后"逐行比对，
    /// 人也能直接看。<b>它不写库</b>（只读连接），所以服务在跑的时候跑一次也是安全的；
    /// 但"升级前"那次应当在停服之后跑，否则刚写进 WAL 的行数会对不上。
    /// </para>
    /// <para>
    /// 🔥 <b>键名一律用 ASCII</b>（<c>table.agent_hosts=3</c>），不用中文：这段输出是给
    /// <b>脚本解析</b>的，而同一个 exe 会在三种终端里跑 —— 中文 Windows 控制台（码页 936，
    /// 输出 GBK）、Git Bash（按 UTF-8 解码，中文全是乱码）、Linux（UTF-8）。
    /// 只有 ASCII 键在这三种环境下都能被稳定 grep 到。中文标题行随便乱，不影响解析。
    /// </para>
    /// </summary>
    private static int RunDbInfo(string dataRoot)
    {
        var dbPath = Path.Combine(dataRoot, "control.db");

        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"【失败】这个目录里没有 control.db：{dbPath}");
            Console.Error.WriteLine("  如果服务装在别处，用环境变量 Server__Storage__DataDir 指定。");
            return 2;
        }

        Console.WriteLine($"db={new FileInfo(dbPath).Length}");

        // 🔴 Mode=ReadOnly：绝不能因为"看一眼"就把库动了。
        // ⚠️ WAL 模式下只读打开需要同目录可写（SQLite 要建/挂 -shm）。数据目录被 ACL 收紧到
        //    只有 SYSTEM/Administrators 可写时，普通用户跑 --dbinfo 会报错 —— 那是**预期行为**，
        //    不是 bug（审计清单 §4 #1 收紧 ACL 之后就该是这样）。
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };

        using var conn = new SqliteConnection(builder.ToString());
        conn.Open();

        long migrations = 0;
        string? latest = null;

        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "select count(*) from __EFMigrationsHistory";
                migrations = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "select MigrationId from __EFMigrationsHistory order by MigrationId desc limit 1";
                latest = cmd.ExecuteScalar() as string;
            }
        }
        catch (SqliteException)
        {
            // M1 时期 EnsureCreated 建的库没有迁移历史表。不是失败，是"这库很老"。
            Console.WriteLine("migrations=-1");
            Console.WriteLine("migrations.latest=none");
            latest = null;
        }

        if (latest is not null || migrations > 0)
        {
            Console.WriteLine($"migrations={migrations}");
            Console.WriteLine($"migrations.latest={latest ?? "none"}");
        }

        // 表清单从 sqlite_master 动态取，不写死 —— 新增表不该靠人记得回来改这个命令。
        var tables = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "select name from sqlite_master where type='table' "
                + "and name not like 'sqlite_%' and name <> '__EFMigrationsHistory' order by name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (var table in tables)
        {
            long rows;
            try
            {
                using var cmd = conn.CreateCommand();
                // 表名来自 sqlite_master，不来自用户输入；方括号防止表名里带特殊字符。
                cmd.CommandText = $"select count(*) from [{table}]";
                rows = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
            }
            catch (SqliteException ex)
            {
                Console.WriteLine($"table.{table}=?{ex.Message}");
                continue;
            }

            Console.WriteLine($"table.{table}={rows}");
        }

        // 文件仓库：备份要备它，容量估算也要它。只数大小，不读内容。
        var filesDir = Path.Combine(dataRoot, "files");
        if (Directory.Exists(filesDir))
        {
            long count = 0;
            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(filesDir, "*", SearchOption.AllDirectories))
            {
                count++;
                bytes += new FileInfo(file).Length;
            }

            Console.WriteLine($"files.count={count}");
            Console.WriteLine($"files.bytes={bytes}");
        }
        else
        {
            Console.WriteLine("files.count=0");
            Console.WriteLine("files.bytes=0");
        }

        // 图片目录：与 files/ 一样要进备份与容量估算。
        // ⚠️ 这里只数大小，不做对账 —— 对账有副作用（删文件），归后台任务与 `--health`。
        var imagesDir = Path.Combine(dataRoot, "images");
        if (Directory.Exists(imagesDir))
        {
            long count = 0;
            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(imagesDir, "*", SearchOption.TopDirectoryOnly))
            {
                // 残留的 .tmp 不是图片，单独计；否则容量估算会把"写了一半的中间态"算成配置资源。
                if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

                count++;
                bytes += new FileInfo(file).Length;
            }

            Console.WriteLine($"images.count={count}");
            Console.WriteLine($"images.bytes={bytes}");
        }
        else
        {
            Console.WriteLine("images.count=0");
            Console.WriteLine("images.bytes=0");
        }

        return 0;
    }

    private static int RunBackup(DataBackupService service, MaintenanceInvocation invocation, string dataRoot)
    {
        if (invocation.Target is null)
        {
            Console.Error.WriteLine("【缺少参数】--backup 后面要跟一个备份目录，例如：");
            Console.Error.WriteLine("  ControlServer.Server.exe --backup D:\\backup\\2026-10-17");
            Console.Error.WriteLine("  （目录可以是新的空目录；不要放在数据目录里面 —— 同盘同毁，那不叫备份）");
            return 1;
        }

        Console.WriteLine($"备份到：  {Path.GetFullPath(invocation.Target)}");
        if (invocation.IncludeLogs) Console.WriteLine("（包含日志目录 logs/）");
        Console.WriteLine();

        var manifest = service
            .CreateAsync(invocation.Target, invocation.IncludeLogs, Console.WriteLine)
            .GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine("备份内容：");
        foreach (var entry in manifest.Files)
        {
            Console.WriteLine($"  {entry.Path,-42} {entry.Size,12:N0} 字节");
        }

        Console.WriteLine();
        Console.WriteLine("刻意没备（写进 manifest 的 skipped 里）：");
        foreach (var skipped in manifest.Skipped)
        {
            Console.WriteLine($"  × {skipped.Path}");
            Console.WriteLine($"      {skipped.Reason}");
        }

        Console.WriteLine();
        Console.WriteLine($"√ 备份完成：{manifest.Files.Count} 个文件，"
            + $"{manifest.Files.Sum(f => f.Size) / 1024.0 / 1024.0:0.0} MB。");
        Console.WriteLine($"   清单：{Path.Combine(invocation.Target, DataBackupService.ManifestEntryName)}");
        Console.WriteLine($"   源：  {dataRoot}");
        Console.WriteLine($"   时间：{manifest.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine();
        Console.WriteLine("注意：把整个备份目录复制到另一台机器/另一块盘 —— 留在同一块盘上的备份不算备份。");

        return 0;
    }

    private static int RunRestore(DataBackupService service, MaintenanceInvocation invocation)
    {
        if (invocation.Target is null)
        {
            Console.Error.WriteLine("【缺少参数】--restore 后面要跟备份目录，例如：");
            Console.Error.WriteLine("  ControlServer.Server.exe --restore D:\\backup\\2026-10-17 --force");
            return 1;
        }

        var manifest = DataBackupService.ReadManifest(invocation.Target);

        Console.WriteLine($"恢复自：  {Path.GetFullPath(invocation.Target)}");
        Console.WriteLine($"备份时间：{manifest.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"备份来源：{manifest.SourceRoot}");
        // 当前程序版本用 DescribeVersion（三段+构建标识）：回滚时最要紧的是
        // "我现在跑的这个 exe 是不是我以为的那个包"，四段的 0.1.0.0 分不出来（M6.4）。
        Console.WriteLine($"程序版本：备份产生于 {manifest.ServerVersion}；当前程序 {Services.ServerRuntimeInfo.DescribeVersion()}");
        Console.WriteLine($"文件数：  {manifest.Files.Count}");
        Console.WriteLine();

        if (!invocation.Force)
        {
            Console.WriteLine("（未加 --force：目标已存在数据库时会拒绝覆盖。确认路径无误后重跑并加 --force）");
            Console.WriteLine();
        }

        Console.WriteLine("恢复会替换整个数据库文件。请确认服务已经停止 ——");
        Console.WriteLine("Windows: services.msc 或 sc stop <服务名>；Linux: systemctl stop controlserver");
        Console.WriteLine();

        var report = service
            .RestoreAsync(invocation.Target, invocation.Force, Console.WriteLine)
            .GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine($"√ 恢复完成：{report.FileCount} 个文件，"
            + $"{report.RestoredBytes / 1024.0 / 1024.0:0.0} MB。");
        Console.WriteLine("   接下来：启动服务 → 登录 → 抽查一处配置与一台客户端，确认是备份那天的样子。");

        return 0;
    }

    /// <summary>
    /// 解析数据目录，口径与 <c>Program.cs</c> 一致：exe 同目录的 appsettings.json → 环境变量 → 默认根。
    /// <para>
    /// ⚠️ 这里刻意<b>不</b>建 <c>WebApplication</c>（见类注释）。代价是配置源要手工拼一遍；
    /// 为了不让它和服务悄悄分叉，顺序必须保持"json 在前、环境变量在后"。
    /// </para>
    /// </summary>
    private static string ResolveDataRoot()
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var configured = configuration["Server:Storage:DataDir"];
        return string.IsNullOrWhiteSpace(configured)
            ? DataDirectoryDefaults.ResolveDefaultRoot()
            : configured!;
    }

    private static void PrintHelp(TextWriter writer)
    {
        writer.WriteLine("ControlServer 数据维护与巡检（M6.1 / M6.4 / M6.5 / M7.2）");
        writer.WriteLine();
        writer.WriteLine("  ControlServer.Server.exe --backup  <目录> [--include-logs]");
        writer.WriteLine("  ControlServer.Server.exe --restore <目录> [--force]");
        writer.WriteLine("  ControlServer.Server.exe --dbinfo");
        writer.WriteLine("  ControlServer.Server.exe --health");
        writer.WriteLine("  ControlServer.Server.exe --files-recover [备份目录] [--force]");
        writer.WriteLine();
        writer.WriteLine("  --backup    把数据库（一致快照）、配置、文件仓库、图片、授权文件备到指定目录。");
        writer.WriteLine("              允许服务在运行 —— 不需要为了备份停一次展厅。");
        writer.WriteLine("              注意：不含 https/ 下的私钥（根 CA 私钥不外带），只带 CA 公钥；");
        writer.WriteLine("                 不含 downloads/ 与 logs/（后者用 --include-logs 显式加）。");
        writer.WriteLine();
        writer.WriteLine("  --restore   把备份铺回数据目录。必须先停服务：服务在跑会把内存里的");
        writer.WriteLine("              旧数据在恢复之后写回去，结果是「恢复成功但数据还是旧的」。");
        writer.WriteLine("              --force 是覆盖已有数据库的确认开关。");
        writer.WriteLine();
        writer.WriteLine("  --dbinfo    只读地把数据目录摊开：库体积、迁移条数、每张表多少行、文件仓库多大。");
        writer.WriteLine("              升级/回滚验收用它做前后比对（键名是 ASCII，好让脚本 grep）。");
        writer.WriteLine("              服务在跑也能跑，但「升级前」那次请在停服后做，行数才对得上。");
        writer.WriteLine();
        writer.WriteLine("  --health    只读巡检：磁盘余量、库与 -wal 体积、日志体积、证书到期、授权到期、");
        writer.WriteLine("              服务探活。每行一个 键=值（键名 ASCII），末尾给 health=ok|warn|fail。");
        writer.WriteLine("              退出码 0=全绿 / 1=有告警 / 2=读不了数据目录 —— 可直接进定时任务。");
        writer.WriteLine();
        writer.WriteLine("  --files-recover");
        writer.WriteLine("              把断链（库里有记录、files/repo 下没文件）按备份里的同名文件找回来。");
        writer.WriteLine("              只认内容对得上（大小 + SHA-256）的那一份；同名但内容不同的一律拒绝。");
        writer.WriteLine("              默认不覆盖已存在的同名文件（--force 才覆盖）；不用停服务。");
        writer.WriteLine("              不加备份目录时只列出缺什么、不写任何东西（退出码 2）。");
        writer.WriteLine();
        writer.WriteLine("  数据目录：取 appsettings.json 的 Server:Storage:DataDir，或环境变量");
        writer.WriteLine("            Server__Storage__DataDir，都没有则用平台默认目录。");
    }
}
