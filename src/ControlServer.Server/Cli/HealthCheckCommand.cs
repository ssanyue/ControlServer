using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ControlServer.Core.Licensing;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using ControlServer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Server.Cli;

/// <summary>
/// <c>--health</c>：一条命令把"这台机器现在到底怎么样"摊开（M6.5 运维巡检）。
///
/// <para>
/// <b>为什么必须有一条命令，而不是一张"请依次检查"的清单</b>：巡检最容易糊弄的地方
/// 就是"照文档手工敲五六条命令，敲了两条就走神了"。运维手册里每一个"请检查 X"
/// 都应该能被这一条命令替代，否则那份手册现场不会有人真的照做。
/// </para>
///
/// <para>
/// <b>输出契约（与 <c>--dbinfo</c> 一致）</b>：
/// ① 一行一项 <c>键=值</c>，键名<b>全 ASCII</b>（同一 exe 要在 GBK 控制台 / Git Bash / Linux 三处被 grep）；
/// ② 所有判据的结论汇总成 <c>warn.N=…</c> 与 <c>health=ok|warn|fail</c>；
/// ③ 退出码 <b>0 = 全绿 / 1 = 有告警 / 2 = 有失败项</b> —— 定时任务只看退出码。
/// </para>
///
/// <para>
/// 🔴 <b>它是只读的</b>：不建目录、不写库、不改文件。唯一的外呼是打本机
/// <c>http://127.0.0.1:{端口}/healthz</c>，用来回答"服务到底在不在跑"。
/// </para>
/// </summary>
public sealed class HealthCheckCommand
{
    /// <summary>磁盘余量的绝对下限。低于它，先别谈备份，连日志和库都写不动了。</summary>
    private const long MinFreeDiskBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>备份余量系数，与 <c>deploy/upgrade/upgrade.ps1</c> 的"数据目录 × 1.2"同源。</summary>
    private const double BackupHeadroomFactor = 1.2;

    private const int CertWarnDays = 30;

    /// <summary>
    /// 正式授权的告警提前量：30 天。比证书长，因为续期要**客户配合**（走商务报价、付款、签发），
    /// 提前一个月开始是真的必要，不是保守。
    /// </summary>
    private const int LicenseWarnDays = 30;

    /// <summary>
    /// 本机试用的告警提前量：7 天。
    /// <para>
    /// 🔥 比正式授权短得多，这是刻意的：试用续命只是"导入一个授权文件"，
    /// 一小时能搞定；而按 30 天报的话，**一台刚从箱子里拆出来装好的机器第一天就带告警** ——
    /// 那会让值班对"巡检有告警"这件事脱敏，等真出事时就不看了。
    /// </para>
    /// </summary>
    private const int TrialWarnDays = 7;

    /// <summary>
    /// 数据库的软上限。判据来自 M6.3 实测（100 台 × 30 天 ≈ 2.8 GB）：
    /// 超过 4 GB 意味着要么规模比压测大，要么保留策略没在裁，两种都该看一眼。
    /// </summary>
    private const long DbWarnBytes = 4L * 1024 * 1024 * 1024;

    private readonly TextWriter _out;
    private readonly IConfiguration _configuration;
    private readonly string _root;
    private readonly List<string> _warnings = [];
    private readonly List<string> _failures = [];

    private HealthCheckCommand(TextWriter output, IConfiguration configuration, string root)
    {
        _out = output;
        _configuration = configuration;
        _root = root;
    }

    /// <summary>
    /// 执行巡检。返回进程退出码（0 全绿 / 1 有告警 / 2 有失败项）。
    /// </summary>
    /// <param name="dataRoot">数据目录（由调用方解析，与 <c>--backup</c> 同源）。</param>
    /// <param name="output">输出目标；默认控制台。留参数是为了让输出契约能被断言。</param>
    /// <param name="configuration">配置源；默认按 exe 目录读 appsettings.json + 环境变量。</param>
    public static int Run(string dataRoot, TextWriter? output = null, IConfiguration? configuration = null) =>
        new HealthCheckCommand(
            output ?? Console.Out,
            configuration ?? LoadConfiguration(),
            Path.GetFullPath(dataRoot)).Execute();

    private int Execute()
    {
        // ⚠️ 数据目录不存在时**记失败**，不是记告警。
        //    曾经想过"一台刚装完还没起过服务的机器就是这个状态，别吓人" —— 但那是个
        //    只持续几秒的瞬态，而定时巡检一天只跑一次。"报了 warn" 与 "报了 fail"
        //    在值班那里的区别是"明天再看"与"现在看"，后者才是对的。
        var rootExists = Directory.Exists(_root);

        Line("data.root", _root);
        Line("data.exists", rootExists ? "1" : "0");

        // 体积只量一次：现场数据目录是 GB 级，量两遍就是白多一秒多。
        var parts = MeasureDataDirectory(_root);

        CheckVolumes(parts, rootExists);
        CheckRepository(rootExists);
        CheckImages(rootExists);
        CheckDisk(parts);
        CheckCertificates();
        CheckLicense();
        CheckService();

        return Report();
    }

    private void CheckVolumes(DirectorySizes parts, bool rootExists)
    {
        Line("data.bytes", parts.Total);
        Line("db.bytes", parts.Db);
        Line("db.wal.bytes", parts.DbWal);
        Line("logs.bytes", parts.Logs);
        Line("logs.files", CountFiles(Path.Combine(_root, "logs")));
        Line("files.bytes", parts.Files);
        Line("downloads.bytes", parts.Downloads);

        if (!rootExists)
        {
            _failures.Add($"数据目录不存在：{_root} —— 路径指错了，或者这台机器还没装过服务");
        }
        else if (parts.Db <= 0)
        {
            _failures.Add("control.db 不存在 —— 服务还没初始化过，或数据目录指错了");
        }
        else if (parts.Db > DbWarnBytes)
        {
            _warnings.Add(
                $"db.bytes={parts.Db / 1024.0 / 1024.0:0} MB 超过 {DbWarnBytes / 1024 / 1024 / 1024} GB 软上限："
                + "确认 Server:Agent:Telemetry:DetailRetentionDays 是否在裁，或按现场规模调小采样率");
        }

        // 🔴 WAL 的坑：库本体会话 5 MB、-wal 却可能几百 MB（长时间没 checkpoint）。
        //    备份拿的是 VACUUM INTO 的一致快照，不受影响；但磁盘占用要一起看。
        if (parts.DbWal > 256L * 1024 * 1024)
        {
            _warnings.Add(
                $"db.wal.bytes={parts.DbWal / 1024.0 / 1024.0:0} MB 偏大（库本体只有 {parts.Db / 1024.0 / 1024.0:0} MB）——"
                + "通常意味着长时间没有 checkpoint，重启一次服务即可回收");
        }

        // 日志保留策略是不是真的在生效：用配置的上限当判据，而不是另拍一个数。
        var retention = ReadLoggingOptions();

        Line("logs.retention.enabled", retention.Enabled ? "1" : "0");
        Line("logs.retention.days", retention.Days);
        Line("logs.retention.mb", retention.MaxMegabytes);

        if (!retention.Enabled)
        {
            _warnings.Add("业务日志保留策略已关闭（Server:Logging:RetentionEnabled=0）—— logs/ 会无限增长");
        }
        else if (retention.MaxMegabytes > 0 && parts.Logs > retention.MaxMegabytes * 1024L * 1024L)
        {
            _warnings.Add(
                $"logs.bytes={parts.Logs / 1024.0 / 1024.0:0} MB 超过策略上限 {retention.MaxMegabytes} MB ——"
                + "保留策略没有生效（检查 logs/ 里是否有名字不合规的 log-*.ndjson，或删除失败被占用）");
        }
    }

    /// <summary>
    /// 文件仓库对账（M7.1）：<b>库里的记录 ↔ <c>files/repo</c> 下的文件</b>。
    ///
    /// <para>
    /// <b>为什么要单独一节</b>：这是运维手册里唯一一个"没有人会主动去看"的目录 ——
    /// 它不会报错、页面也正常，只是在慢慢变大或悄悄少文件。给它一组键，才能被巡检看见。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>这里刻意不用 EF</b>：本命令跑在 <c>WebApplication</c> 之前，拿不到 DI 容器；
    /// 而为了这个问题去把整条宿主编起来，等于让"巡检"依赖"服务能被正常启动"——
    /// 服务起不来时恰恰最需要巡检。所以只读连库、取最小投影，再交给
    /// <see cref="RepositoryGroomer.Scan"/> 用<b>和后台任务完全相同</b>的判据算。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>断链（<c>files.lost</c>）是告警而不是失败</b>：服务还能跑，只是某些文件下载会失败。
    /// 记失败会让人以为"整个系统挂了"，反而盖住了真正的问题。
    /// </para>
    /// </summary>
    private void CheckRepository(bool rootExists)
    {
        if (!rootExists) return;

        var dbPath = Path.Combine(_root, "control.db");

        // 库不存在时，`--health` 已经在 CheckVolumes 里记了一条失败，这里不再重复啰嗦。
        if (!File.Exists(dbPath)) return;

        List<RepoEntry> items;
        List<string> sessionNames;

        try
        {
            (items, sessionNames) = ReadRepositoryRows(dbPath);
        }
        catch (Exception ex)
        {
            Line("files.db.count", "?");

            // 表不存在（老库没跑过 M4.4 迁移）与库损坏都会走到这里，两者都要人看。
            _warnings.Add(
                $"文件仓库读不出来，无法对账（{ex.GetType().Name}: {ex.Message}）——"
                + "若这是刚升级完的库，说明迁移没有跑完");
            return;
        }

        var graceHours = Math.Max(1, ReadInt("Server:Files:OrphanGraceHours", 24));

        var survey = RepositoryGroomer.Scan(
            items,
            sessionNames,
            Path.Combine(_root, "files", "repo"),
            Path.Combine(_root, "files", "uploads"),
            BeijingTime.Now.UtcDateTime,
            TimeSpan.FromHours(graceHours));

        Line("files.db.count", survey.DbItems);
        Line("files.db.bytes", survey.DbBytes);
        Line("files.disk.count", survey.RepoFiles);
        Line("files.parts", survey.PartFiles);
        Line("files.orphans", survey.Orphans.Count);
        Line("files.orphan.bytes", survey.OrphanBytes);
        Line("files.lost", survey.Lost.Count);
        Line("files.stale.parts", survey.StaleParts.Count);
        Line("files.grace.hours", graceHours);

        if (survey.Lost.Count > 0)
        {
            var sample = string.Join(
                "；",
                survey.Lost.Take(3).Select(x => $"id={x.Id} {x.Name}（{x.Size} 字节）"));

            var more = survey.Lost.Count > 3 ? $"……共 {survey.Lost.Count} 条" : string.Empty;

            _warnings.Add(
                $"文件仓库断链 {survey.Lost.Count} 条：库里有记录、磁盘上没有文件（{sample}{more}）——"
                + "这些文件下载会失败。对账「不会」自动删记录，请从备份找回 files/repo/ 下的同名文件");
        }

        if (survey.Orphans.Count > 0)
        {
            _warnings.Add(
                $"文件仓库有 {survey.Orphans.Count} 个孤儿文件（{survey.OrphanBytes} 字节，"
                + $"盘上有、库里没有，且已超过 {graceHours} 小时宽限期）——"
                + "后台任务每 30 分钟会回收一次；若长期不降，看日志里的 Files 告警（多半是被占用删不掉）");
        }

        if (survey.UnexpectedDirectories.Count > 0)
        {
            _warnings.Add(
                $"files/repo 下出现子目录（{string.Join("、", survey.UnexpectedDirectories.Take(3))}）——"
                + "对账只处理顶层文件，这些目录里的一切都不会被回收");
        }

        CheckRepositoryContents(items);
    }

    /// <summary>
    /// 文件仓库的<b>内容</b>校验（重算 SHA-256）：抓「大小对得上、内容被换掉」这种
    /// 对账看不出来的坏法。
    /// <para>
    /// <b>为什么放在巡检而不是 30 分钟对账里</b>：对账只 <c>stat</c>，这个要把字节读一遍。
    /// 每周一次、带预算，才是能长期开着的形状；放到后台循环里等于让服务持续读盘。
    /// </para>
    /// <para>
    /// ⚠️ <b>预算用尽是有意的行为，不是缺陷</b>：<c>files.verify.skipped</c> 会如实报出
    /// "这轮没验的条数"。不报的话，200 个文件的仓库里坏了第 201 个，
    /// 巡检会一直显示"全部正常" —— 那比不校验更坏。
    /// </para>
    /// </summary>
    private void CheckRepositoryContents(IReadOnlyList<RepoEntry> items)
    {
        var maxFiles = Math.Max(0, ReadInt("Server:Files:VerifyMaxFiles", 200));
        var maxMb = Math.Max(0, ReadInt("Server:Files:VerifyMaxMb", 256));

        Line("files.verify.limit.files", maxFiles);
        Line("files.verify.limit.bytes", (long)maxMb * 1024 * 1024);

        if (items.Count == 0 || (maxFiles == 0 && maxMb == 0))
        {
            Line("files.verify.checked", 0);
            Line("files.verify.mismatch", 0);
            Line("files.verify.skipped", 0);
            return;
        }

        ContentVerificationResult verify;

        try
        {
            // 巡检是同步命令，没有同步上下文 —— 这里等异步不会死锁。
            verify = RepositoryGroomer
                .VerifyContentsAsync(items, Path.Combine(_root, "files", "repo"), maxFiles, (long)maxMb * 1024 * 1024)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Line("files.verify.checked", "?");

            // 校验失败不能把整条巡检打挂：别的项（磁盘、证书、探活）更急着看。
            _warnings.Add($"文件内容校验没跑完（{ex.GetType().Name}: {ex.Message}）—— 这一项本轮没有结论");
            return;
        }

        Line("files.verify.checked", verify.Checked);
        Line("files.verify.bytes", verify.CheckedBytes);
        Line("files.verify.mismatch", verify.Mismatched);
        Line("files.verify.skipped", verify.Skipped);

        if (verify.Mismatched > 0)
        {
            var sample = string.Join(
                "；",
                verify.Mismatches.Take(3).Select(m => $"id={m.Id} {m.Name}（{m.Reason}）"));

            var more = verify.Mismatched > 3 ? $"……共 {verify.Mismatched} 条" : string.Empty;

            _warnings.Add(
                $"文件仓库内容不一致 {verify.Mismatched} 条：大小或摘要与库里记录不符（{sample}{more}）——"
                + "这类文件下载下来是坏数据（大小对、内容错），比断链更难发现。"
                + "请从源头重传；**不要**用备份覆盖（如果盘上这份是坏的，同一批备份通常也是）");
        }

        if (verify.Skipped > 0 && verify.Mismatched == 0)
        {
            _warnings.Add(
                $"文件内容只校验了 {verify.Checked} 个、{verify.Skipped} 个未校验"
                + $"（预算：每轮 {maxFiles} 个 / {maxMb} MB）—— 未校验的不代表正常，"
                + "要全覆盖请调大 Server:Files:VerifyMaxFiles / VerifyMaxMb 后重跑");
        }

        foreach (var failure in verify.Failures.Take(3))
        {
            _warnings.Add("文件内容校验：" + failure);
        }
    }

    /// <summary>
    /// 图片目录（<c>images/</c>）的对账（M7.1 补：此前只覆盖了 <c>files/</c>）。
    ///
    /// <para>
    /// <b>为什么要给它一组键</b>：这个目录比 <c>files/repo</c> 更容易被忽略 ——
    /// 图片删了只是页面上那块变成纯色，<b>前端是静默回退，不会报错</b>；
    /// 反过来，用户换图/删按钮留下的旧文件也不会有人去看。两种问题都不会自己浮现。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>断链（<c>images.lost</c>）是告警而不是失败</b>：服务完全能跑，
    /// 只是那几处图片显示成纯色瓦片。记失败会盖住真正的问题。
    /// </para>
    /// </summary>
    private void CheckImages(bool rootExists)
    {
        if (!rootExists) return;

        var imagesDir = Path.Combine(_root, "images");
        var dbPath = Path.Combine(_root, "control.db");
        var graceHours = Math.Max(1, ReadInt("Server:Files:OrphanGraceHours", 24));

        ControlSettings? settings = null;

        // 与 CheckRepository 同样刻意不用 EF：巡检要能在服务起不来时照跑。
        if (File.Exists(dbPath))
        {
            try
            {
                settings = ReadSettingsOrDefault(dbPath);
            }
            catch (Exception ex)
            {
                // 🔴 读不出配置就**不判孤儿** —— "没人引用"只说明我们没读懂，不能推出"可以删"。
                _warnings.Add(
                    $"图片目录：配置读不出来，本轮不判孤儿（{ex.GetType().Name}: {ex.Message}）——"
                    + "若这是刚升级完的库，说明迁移没有跑完");
            }
        }

        var survey = ImageRepositoryGroomer.Scan(
            settings,
            imagesDir,
            BeijingTime.Now.UtcDateTime,
            TimeSpan.FromHours(graceHours));

        Line("images.count", survey.DiskFiles);
        Line("images.bytes", survey.DiskBytes);
        Line("images.orphans", survey.Orphans.Count);
        Line("images.orphan.bytes", survey.OrphanBytes);
        Line("images.temps", survey.StaleTemps.Count);
        Line("images.lost", survey.Lost.Count);
        Line("images.grace.hours", graceHours);

        if (survey.Lost.Count > 0)
        {
            var sample = string.Join(
                "；",
                survey.Lost.Take(3).Select(x => $"{x.Name}（{string.Join("、", x.Referrers.Take(2))}）"));

            var more = survey.Lost.Count > 3 ? $"……共 {survey.Lost.Count} 张" : string.Empty;

            _warnings.Add(
                $"图片断链 {survey.Lost.Count} 张：配置里引用了、images/ 下没有文件（{sample}{more}）——"
                + "页面不会报错，那几处会显示成纯色瓦片。请从备份找回同名文件，或重新上传后重新选择。");
        }

        if (survey.Orphans.Count > 0)
        {
            _warnings.Add(
                $"图片目录有 {survey.Orphans.Count} 个孤儿文件（{survey.OrphanBytes} 字节，"
                + $"没人引用且已超过 {graceHours} 小时宽限期）——"
                + "后台任务每 30 分钟会回收一次；若长期不降，看日志里的 Files 告警（多半是被占用删不掉）");
        }

        if (survey.UnexpectedDirectories.Count > 0)
        {
            _warnings.Add(
                $"images/ 下出现子目录（{string.Join("、", survey.UnexpectedDirectories.Take(3))}）——"
                + "对账只处理顶层文件，这些目录里的一切都不会被回收");
        }
    }

    /// <summary>
    /// 只读地把当前配置文档取出来（图片对账要知道"哪张图还有人用"）。
    /// <para>
    /// 复用与巡检其余部分相同的 <c>Mode=ReadOnly</c> + <c>Pooling=false</c>。
    /// 返回 <c>null</c> = 文档不存在（全新部署）⇒ 调用方按"没有引用"处理是安全的：
    /// 默认配置里确实一张图都不引用。
    /// </para>
    /// </summary>
    private static ControlSettings? ReadSettingsOrDefault(string dbPath)
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

        // 列名 Key 在 SQLite 里是关键字，必须加引号，否则整条语句是语法错误。
        cmd.CommandText = "select \"Value\" from config_documents where \"Key\" = 'settings' limit 1";

        var raw = cmd.ExecuteScalar() as string;

        return string.IsNullOrWhiteSpace(raw) ? null : ControlJson.Deserialize<ControlSettings>(raw);
    }

    /// <summary>
    /// 只读地把对账要用的两列取出来（仓库记录 + 会话的 <c>StoredName</c>）。
    /// <para>
    /// <c>Mode=ReadOnly</c> + <c>Pooling=false</c>：前者保证绝不可能写库，
    /// 后者是为了让连接在方法返回时真的关掉 —— 池化的空闲连接会让"巡检"在服务
    /// 之后的独占操作（如整库备份）上留一个看不见的句柄。
    /// </para>
    /// </summary>
    private static (List<RepoEntry> Items, List<string> Sessions) ReadRepositoryRows(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };

        using var conn = new SqliteConnection(builder.ToString());
        conn.Open();

        var items = new List<RepoEntry>();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "select Id, Name, StoredName, Size, Sha256 from file_repo_items";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new RepoEntry(
                    Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                    reader.GetString(1),
                    reader.GetString(2),
                    Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        var sessions = new List<string>();

        using (var cmd = conn.CreateCommand())
        {
            // 全取，不按过期过滤：一个**活跃**会话的 .part 必须被认作"有主人"，
            // 否则它会在一小时后被当成无主文件删掉，客户端续传时从 0 重来。
            cmd.CommandText = "select StoredName from file_upload_sessions";

            using var reader = cmd.ExecuteReader();
            while (reader.Read()) sessions.Add(reader.GetString(0));
        }

        return (items, sessions);
    }

    private void CheckDisk(DirectorySizes parts)
    {
        var (free, total) = MeasureDisk(_root);

        if (free <= 0)
        {
            _warnings.Add("拿不到磁盘余量（数据目录不在任何已挂载的卷上？）");
            return;
        }

        // 判据取"绝对下限"与"够下一次全量备份"里更严的那个 ——
        // 一次升级要先把整个数据目录复制成备份，余量不足时升级会中途失败。
        var needed = Math.Max(MinFreeDiskBytes, (long)(parts.Total * BackupHeadroomFactor));

        Line("disk.free", free);
        Line("disk.total", total);
        Line("disk.needed", needed);

        if (free < needed)
        {
            _warnings.Add(
                $"磁盘余量 {free / 1024.0 / 1024.0 / 1024.0:0.0} GB < 需要 {needed / 1024.0 / 1024.0 / 1024.0:0.0} GB"
                + "（下限 5 GB 与「数据目录 × 1.2」取严者）—— 备份与升级会失败");
        }
    }

    private void CheckCertificates()
    {
        var certDir = Path.Combine(_root, "https");

        // server.pfx 是 HTTPS 真正在用的那张；CA 那张 20 年，只在轮换密钥时才会变。
        ReportCertificate(certDir, "server.pfx", "cert.server");
        ReportCertificate(certDir, "controlserver-ca.crt", "cert.ca");
    }

    private void CheckLicense()
    {
        var store = new LicenseFileStore(new FileSystemDataDirectory(_root));

        ReportSignedLicense(store);
        ReportTrial(store);
    }

    private void ReportSignedLicense(LicenseFileStore store)
    {
        try
        {
            var snapshot = store.ReadAsync().GetAwaiter().GetResult();

            if (!snapshot.Exists || string.IsNullOrWhiteSpace(snapshot.RawText))
            {
                Line("license.present", "0");
                return;
            }

            Line("license.present", "1");

            // ⚠️ 这里**真的验签**，不是只解出字段：一份"看起来是正式授权"但签名不对的文件，
            // 在现场的表现是"页面一直提示未授权"，而巡检如果只报日期就会把人带到错方向。
            var inspection = LicenseInspector.Inspect(
                snapshot.RawText!,
                VendorPublicKey.Pem,
                DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(120));

            var state = inspection.State;

            Line("license.signature", inspection.SignatureValid ? "ok" : "bad");
            Line("license.status", state.Status.ToString());
            Line("license.kind", string.IsNullOrWhiteSpace(state.Kind) ? "?" : state.Kind);
            Line(
                "license.machinecode",
                string.IsNullOrWhiteSpace(inspection.Document.Mc) ? "universal" : inspection.Document.Mc);

            // 永久授权的 RemainingDays 是哨兵值 99999，不是"还有 273 年"。
            if (state.IsPerpetual || state.ExpiresAt is null)
            {
                Line("license.expires", "perpetual");
                Line("license.daysleft", "none");

                if (!inspection.SignatureValid)
                {
                    _warnings.Add($"授权验签失败：{inspection.SignatureError}");
                }

                return;
            }

            Line(
                "license.expires",
                state.ExpiresAt.Value.ToOffset(BeijingTime.Offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Line("license.daysleft", state.RemainingDays);

            if (!inspection.SignatureValid)
            {
                _warnings.Add($"授权验签失败：{inspection.SignatureError}");
            }
            else if (state.Status == LicenseStatus.Expired)
            {
                // 🔴 必须判 Status，不能用 `RemainingDays < 0`。
                //    LicenseState 的口径是「已过期为 0」，于是 `<= 30` 分支会把一份**已经过期**的授权
                //    报成"还有 0 天到期" —— 那听起来像"今天还能用"，实际是"今天已经不能用"。
                _warnings.Add($"授权已过期（{state.ExpiresAt:yyyy-MM-dd}）—— Enforce 档位下 /api 会全线 403");
            }
            else if (state.RemainingDays >= 0 && state.RemainingDays <= LicenseWarnDays)
            {
                _warnings.Add(
                    $"授权还有 {state.RemainingDays} 天到期（{state.ExpiresAt:yyyy-MM-dd}）—— 现在就该续期，续期要客户配合");
            }
        }
        catch (Exception ex)
        {
            Line("license.present", "?");
            _warnings.Add($"授权文件读不出来（{ex.GetType().Name}: {ex.Message}）");
        }
    }

    private void ReportTrial(LicenseFileStore store)
    {
        // 没有正式授权时，真正在起作用的往往是试用记录 —— 只看 license.lic 会漏掉它。
        try
        {
            var trial = store.ReadTrialAsync().GetAwaiter().GetResult();

            if (trial is null)
            {
                Line("trial.present", "0");
                return;
            }

            Line("trial.present", "1");
            Line("trial.expires", trial.ExpiresOn);

            if (!DateTime.TryParseExact(
                    trial.ExpiresOn,
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var trialDate))
            {
                _warnings.Add($"试用记录的到期日 {trial.ExpiresOn} 不是 yyyyMMdd，无法判断还剩几天");
                return;
            }

            var daysLeft = DateOnly.FromDateTime(trialDate).DayNumber
                - DateOnly.FromDateTime(BeijingTime.Now.DateTime).DayNumber;

            Line("trial.daysleft", daysLeft);

            if (daysLeft < 0)
            {
                _warnings.Add($"本机试用已于 {trial.ExpiresOn} 到期，且没有正式授权 —— Enforce 档位下 /api 会全线 403");
            }
            else if (daysLeft <= TrialWarnDays)
            {
                _warnings.Add($"本机试用还有 {daysLeft} 天到期（{trial.ExpiresOn}）—— 到期前必须导入正式授权");
            }
        }
        catch (Exception ex)
        {
            Line("trial.present", "?");
            _warnings.Add($"试用记录读不出来（{ex.GetType().Name}: {ex.Message}）");
        }
    }

    /// <summary>
    /// 探一次本机 <c>/healthz</c>。
    /// <para>
    /// ⚠️ <b>只打 127.0.0.1</b>：巡检命令不做任何网段扫描（铁律：服务端不扫子网）。
    /// 服务没起时记为告警而不是失败 —— 维护窗口里它本来就该是停的，
    /// 但"以为它在跑其实没跑"必须能一眼看出来。
    /// </para>
    /// </summary>
    private void CheckService()
    {
        var port = ReadInt("Server:Http:Port", 8899);
        Line("service.port", port);

        try
        {
            // ⚠️ UseProxy = false：巡检不该被系统代理拦走，也不该往代理日志里塞一条内网地址。
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };

            using var response = client.GetAsync($"http://127.0.0.1:{port}/healthz").GetAwaiter().GetResult();

            Line("service.healthz", (int)response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                _warnings.Add($"本机 :{port}/healthz 返回 {(int)response.StatusCode} —— 服务在跑但探活不通过");
                return;
            }

            using var json = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());

            if (json.RootElement.TryGetProperty("version", out var version))
            {
                Line("service.version", version.GetString() ?? "?");
            }

            if (json.RootElement.TryGetProperty("logSink", out var sink)
                && sink.TryGetProperty("healthy", out var healthy)
                && healthy.ValueKind == JsonValueKind.False)
            {
                var lastError = sink.TryGetProperty("lastError", out var error) ? error.GetString() : "?";
                _warnings.Add($"业务日志落盘连续失败（{lastError}）—— 磁盘满 / 权限 / 占用，见 server-*.log 里的 Error");
            }
        }
        catch (Exception ex)
        {
            Line("service.healthz", "unreachable");
            _warnings.Add(
                $"本机 :{port}/healthz 连不上（{ex.GetType().Name}）—— 服务没在运行，"
                + "或换个端口（Server:Http:Port）。维护窗口里这是正常的");
        }
    }

    private int Report()
    {
        // 汇总放在最后：告警内容里可能有换行（异常消息），必须压平才不破坏"一行一项"。
        for (var i = 0; i < _warnings.Count; i++)
        {
            Line($"warn.{i + 1}", _warnings[i]);
        }

        Line("warn.count", _warnings.Count);
        Line("fail.count", _failures.Count);

        foreach (var failure in _failures)
        {
            Line("fail", failure);
        }

        var verdict = _failures.Count > 0 ? "fail" : _warnings.Count > 0 ? "warn" : "ok";
        Line("health", verdict);

        _out.WriteLine();
        _out.WriteLine(verdict switch
        {
            "ok" => "√ 全绿：没有需要处理的项目。",
            "warn" => "! 有告警：逐条看 warn.N，按 docs/运维手册.md §2 的判读表处理。",
            _ => "× 有失败项：先解决它，其余的告警现在没有意义。",
        });

        return _failures.Count > 0 ? 2 : _warnings.Count > 0 ? 1 : 0;
    }

    /// <summary>写一行 <c>键=值</c>。值里的换行会毁掉"一行一项"的契约，必须压平。</summary>
    private void Line(string key, string value) =>
        _out.WriteLine($"{key}={value.ReplaceLineEndings(" ")}");

    private void Line(string key, long value) =>
        _out.WriteLine($"{key}={value.ToString(CultureInfo.InvariantCulture)}");

    private static IConfiguration LoadConfiguration()
    {
        // 与 DataMaintenanceCommand.ResolveDataRoot 同源：json 在前、环境变量在后。
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";

        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }

    private int ReadInt(string key, int fallback) =>
        int.TryParse(_configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private sealed record LoggingSnapshot(bool Enabled, int Days, int MaxMegabytes);

    private LoggingSnapshot ReadLoggingOptions()
    {
        // 直接读配置而不是反序列化 ServerOptions：这条命令跑在 WebApplication 之前，
        // 拉不进 ValidateOnStart 那一套；键名与 ServerOptions.LoggingOptions 保持一致。
        var enabled = !string.Equals(
            _configuration["Server:Logging:RetentionEnabled"], "false", StringComparison.OrdinalIgnoreCase);

        return new LoggingSnapshot(
            enabled,
            ReadInt("Server:Logging:RetentionDays", 30),
            ReadInt("Server:Logging:RetentionMaxMegabytes", 200));
    }

    private sealed record DirectorySizes(long Total, long Db, long DbWal, long Logs, long Files, long Downloads);

    private static DirectorySizes MeasureDataDirectory(string root)
    {
        if (!Directory.Exists(root))
        {
            return new DirectorySizes(0, 0, 0, 0, 0, 0);
        }

        return new DirectorySizes(
            MeasureTree(root),
            SafeLength(Path.Combine(root, "control.db")),
            SafeLength(Path.Combine(root, "control.db-wal")),
            MeasureTree(Path.Combine(root, "logs")),
            MeasureTree(Path.Combine(root, "files")),
            MeasureTree(Path.Combine(root, "downloads")));
    }

    private static long MeasureTree(string path)
    {
        if (!Directory.Exists(path)) return 0;

        long sum = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                sum += SafeLength(file);
            }
        }
        catch
        {
            // 枚举中途被拒绝（ACL / 占用）时，结果就是"已经数到的部分"。
            // 不抛：巡检命令不该因为一个读不了的子目录整份失败。
        }

        return sum;
    }

    private static int CountFiles(string path)
    {
        if (!Directory.Exists(path)) return 0;

        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count();
        }
        catch
        {
            return 0;
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static (long Free, long Total) MeasureDisk(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return (0, 0);

            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.AvailableFreeSpace, drive.TotalSize) : (0, 0);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// 证书到期日。
    /// <para>
    /// ⚠️ <c>server.pfx</c> 的口令在 <c>https/pfx.password</c>（与 <c>LocalCertificateAuthority</c> 同源）。
    /// 读它意味着<b>本命令要能读私钥文件</b> —— 数据目录 ACL 收紧之后，这必然要求提权运行。
    /// 读不到就报 <c>unreadable</c>，不猜。
    /// </para>
    /// </summary>
    private void ReportCertificate(string certDirectory, string fileName, string keyPrefix)
    {
        var path = Path.Combine(certDirectory, fileName);

        if (!File.Exists(path))
        {
            Line($"{keyPrefix}.notafter", "none");
            Line($"{keyPrefix}.daysleft", "none");
            return;
        }

        try
        {
            using var certificate = LoadCertificate(certDirectory, fileName, path);
            var notAfter = certificate.NotAfter.ToUniversalTime();
            var daysLeft = (int)Math.Floor((notAfter - DateTime.UtcNow).TotalDays);

            Line($"{keyPrefix}.notafter", notAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Line($"{keyPrefix}.daysleft", daysLeft);
            Line($"{keyPrefix}.subject", certificate.Subject);

            if (daysLeft < 0)
            {
                _warnings.Add($"{fileName} 已于 {notAfter:yyyy-MM-dd} 过期 —— HTTPS 会失败，观看端会报证书错误");
            }
            else if (daysLeft <= CertWarnDays)
            {
                _warnings.Add($"{fileName} 还有 {daysLeft} 天到期（{notAfter:yyyy-MM-dd}）—— 提前续签，别等到当天");
            }
        }
        catch (Exception ex)
        {
            Line($"{keyPrefix}.notafter", "unreadable");
            Line($"{keyPrefix}.daysleft", "unreadable");
            _warnings.Add($"{fileName} 读不出来（{ex.GetType().Name}: {ex.Message}）");
        }
    }

    private static X509Certificate2 LoadCertificate(string certDirectory, string fileName, string path)
    {
        // pfx 要用口令打开；crt 是纯公钥，直接读字节。
        if (!fileName.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase))
        {
            return new X509Certificate2(File.ReadAllBytes(path));
        }

        var passwordPath = Path.Combine(certDirectory, "pfx.password");
        if (!File.Exists(passwordPath))
        {
            throw new InvalidOperationException("找不到 https/pfx.password，无法打开 pfx");
        }

        var password = File.ReadAllLines(passwordPath)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#'))
            ?? throw new InvalidOperationException("https/pfx.password 里没有有效口令行");

        // EphemeralKeySet：不在磁盘上落密钥容器 —— 巡检命令不该留下任何痕迹。
        return new X509Certificate2(path, password, X509KeyStorageFlags.EphemeralKeySet);
    }
}
