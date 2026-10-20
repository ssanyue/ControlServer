using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 日志保留策略（M6.5）。
///
/// <para>
/// <b>这批用例存在的理由</b>：策略的核心契约是"30 天前的会删"，而靠等 30 天是测不出来的。
/// 所以 <see cref="LogRetention.Prune"/> 把"今天"当参数，用例才能直接构造历史。
/// </para>
///
/// <para>
/// ⚠️ 铁律「断言三问」：不是只断言 <c>DeletedFiles == 1</c> —— 还要断言
/// <b>删的是哪一个</b>、<b>今天那个还在</b>、<b>别的文件一个都没动</b>。
/// 只断言数量的话，"删错了一个文件"和"删对了"看起来一模一样。
/// </para>
/// </summary>
public sealed class LogRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "logret-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly DateOnly _today = new(2026, 10, 17);

    public LogRetentionTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录删不掉不影响结论。
        }
    }

    private string MakeLog(DateOnly date, int bytes = 16)
    {
        var path = Path.Combine(_root, LogRetention.FileNameFor(date));
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static LogRetentionOptions Days(int days) => new() { RetentionDays = days, MaxTotalMegabytes = 0 };

    private static LogRetentionOptions Cap(int megabytes) => new() { RetentionDays = 0, MaxTotalMegabytes = megabytes };

    [Fact]
    public void Parse_accepts_only_the_exact_file_name_shape()
    {
        LogRetention.TryParseDate("log-2026-10-17.ndjson").Should().Be(new DateOnly(2026, 10, 17));

        // 🔴 这些都必须返回 null，否则"运维手工留的证据"会在某次清理里消失。
        LogRetention.TryParseDate("log-2026-10-17.ndjson.bak").Should().BeNull();
        LogRetention.TryParseDate("log-2026-10-17-排查.ndjson").Should().BeNull();
        LogRetention.TryParseDate("server-2026-10-17.log").Should().BeNull();
        LogRetention.TryParseDate("log-2026-13-45.ndjson").Should().BeNull();
        LogRetention.TryParseDate("log-2026-02-30.ndjson").Should().BeNull();
    }

    [Fact]
    public void Files_older_than_the_window_are_deleted_and_today_is_untouched()
    {
        var thirtyDaysAgo = MakeLog(_today.AddDays(-30));
        var twentyNineDaysAgo = MakeLog(_today.AddDays(-29));
        var today = MakeLog(_today);

        var outcome = LogRetention.Prune(_root, Days(30), _today);

        outcome.DeletedFiles.Should().Be(1);
        outcome.DeletedNames.Should().ContainSingle()
            .Which.Should().Be(LogRetention.FileNameFor(_today.AddDays(-30)));

        File.Exists(thirtyDaysAgo).Should().BeFalse();
        File.Exists(twentyNineDaysAgo).Should().BeTrue("29 天前的还在保留窗口内");
        File.Exists(today).Should().BeTrue("今天的文件永远不能删");
    }

    [Fact]
    public void Today_is_never_deleted_even_when_it_is_the_only_file_and_over_the_cap()
    {
        var today = MakeLog(_today, bytes: 4096);

        // 上限只有 1 MB 而文件 4 KB ⇒ 不触发清理；把上限压到 0 是"荒谬配置"的极端：
        // 即使那样也不许删今天 —— 删了它，"日志断了"就变成自己造的。
        var outcome = LogRetention.Prune(
            _root,
            new LogRetentionOptions { RetentionDays = 0, MaxTotalMegabytes = -1 },
            _today);

        outcome.DeletedFiles.Should().Be(0);
        File.Exists(today).Should().BeTrue();
    }

    [Fact]
    public void Total_size_cap_deletes_oldest_first()
    {
        // 三个文件各 1 MB，上限 2 MB ⇒ 只应该删掉最旧的那一个。
        var oldest = MakeLog(_today.AddDays(-2), bytes: 1024 * 1024);
        var middle = MakeLog(_today.AddDays(-1), bytes: 1024 * 1024);
        var today = MakeLog(_today, bytes: 1024 * 1024);

        var outcome = LogRetention.Prune(_root, Cap(2), _today);

        outcome.DeletedFiles.Should().Be(1);
        File.Exists(oldest).Should().BeFalse();
        File.Exists(middle).Should().BeTrue();
        File.Exists(today).Should().BeTrue();
        outcome.KeptBytes.Should().Be(2L * 1024 * 1024);
    }

    [Fact]
    public void Other_files_in_the_log_directory_are_never_touched()
    {
        // ① Serilog 的宿主日志：连通配符都匹配不上（模式是 log-*.ndjson），天然安全。
        var hostLog = Path.Combine(_root, "server-20261017.log");
        File.WriteAllText(hostLog, "启动中");

        // ② 运维手工留的证据：多一截 .bak，同样匹配不上通配符。
        var handMade = Path.Combine(_root, "log-2026-01-01.ndjson.bak");
        File.WriteAllText(handMade, "排查用，别删");

        // ③ 长得像日志、但日期不成立：会进到枚举里，必须被 Ignored 挡住而不是被删。
        var bogusDate = Path.Combine(_root, "log-2026-13-45.ndjson");
        File.WriteAllText(bogusDate, "垃圾文件");

        var ancient = MakeLog(_today.AddDays(-500));

        var outcome = LogRetention.Prune(_root, Days(30), _today);

        // 只删了那个"真的合规且够老"的。
        outcome.DeletedFiles.Should().Be(1);
        File.Exists(ancient).Should().BeFalse();
        File.Exists(hostLog).Should().BeTrue("server-*.log 由 Serilog 自己滚动，这里不许碰");
        File.Exists(handMade).Should().BeTrue("名字不合规的一律不动");
        File.Exists(bogusDate).Should().BeTrue("日期解析失败的也要留着，让人自己看");

        outcome.IgnoredNames.Should().BeEquivalentTo(["log-2026-13-45.ndjson"]);
    }

    [Fact]
    public void Future_dated_file_is_not_deleted_by_the_age_rule()
    {
        // 时钟回拨过、或运维手工改过文件名时会出现这种文件。
        // 它不是"过期"，它可能是最新的 —— 按年龄删会删掉最新的一份。
        var future = MakeLog(_today.AddDays(5));

        var outcome = LogRetention.Prune(_root, Days(30), _today);

        outcome.DeletedFiles.Should().Be(0);
        File.Exists(future).Should().BeTrue();
    }

    [Fact]
    public void Disabled_policy_deletes_nothing()
    {
        var ancient = MakeLog(_today.AddDays(-999));

        var outcome = LogRetention.Prune(
            _root,
            new LogRetentionOptions { Enabled = false, RetentionDays = 1, MaxTotalMegabytes = 1 },
            _today);

        outcome.DeletedFiles.Should().Be(0);
        File.Exists(ancient).Should().BeTrue();
    }

    [Fact]
    public void Missing_directory_is_not_an_error()
    {
        var outcome = LogRetention.Prune(Path.Combine(_root, "nope"), Days(30), _today);

        outcome.DeletedFiles.Should().Be(0);
        outcome.Errors.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════
    // LogService 侧：保留策略真的被接上了 + 写失败真的能看见
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Flush_applies_retention_once_and_reports_it_in_the_buffer()
    {
        // ⚠️ LogService 落在 <root>/logs，而 MakeLog 写在 _root 上。
        //    这条用例曾经因此"看起来什么都没删"—— 断言必须指向真正被枚举的那一层。
        var dataDirectory = new TempDataDirectory(Path.Combine(_root, "data"));
        Directory.CreateDirectory(dataDirectory.LogDirectory);

        var ancient = Path.Combine(
            dataDirectory.LogDirectory,
            LogRetention.FileNameFor(DateOnly.FromDateTime(BeijingTime.Now.DateTime).AddDays(-999)));
        File.WriteAllBytes(ancient, new byte[16]);

        await using var log = new LogService(
            dataDirectory,
            capacity: 100,
            flushIntervalMs: 600_000,
            retention: new LogRetentionOptions { RetentionDays = 30, MaxTotalMegabytes = 0 });

        log.Info("Test", "触发一次落盘");
        await log.FlushAsync();

        // 文件真的没了（不是只打了一行字）。
        File.Exists(ancient).Should().BeFalse();

        // 而且这件事在「最近日志」里看得到 —— 静默清理等于没清理。
        log.Recent(50).Should().Contain(e => e.Message.Contains("日志保留"));
    }

    [Fact]
    public async Task Sink_failure_is_reported_once_and_recovery_is_reported()
    {
        var dataDirectory = new TempDataDirectory(_root);
        await using var log = new LogService(dataDirectory, capacity: 100, flushIntervalMs: 600_000);

        var failures = 0;
        var recoveries = 0;

        log.SinkFailed += (_, _) => failures++;
        log.SinkRecovered += () => recoveries++;

        // 把日志目录"弄坏"：先用同名文件占住它，CreateDirectory 会抛。
        var blocked = Path.Combine(_root, "blocked");
        Directory.CreateDirectory(blocked);
        var blocker = new TempDataDirectory(Path.Combine(blocked, "root"));
        var logDir = Path.Combine(blocked, "root", "logs");
        Directory.CreateDirectory(Path.GetDirectoryName(logDir)!);
        File.WriteAllText(logDir, "我是个文件，不是目录");

        await using var broken = new LogService(blocker, capacity: 100, flushIntervalMs: 600_000);
        broken.SinkFailed += (_, _) => failures++;
        broken.SinkRecovered += () => recoveries++;

        broken.Info("Test", "第一轮");
        await broken.FlushAsync();
        failures.Should().Be(1);
        broken.ConsecutiveFlushFailures.Should().BeGreaterThan(0);
        broken.LastFlushError.Should().NotBeNullOrWhiteSpace();

        // 失败仍在持续：不重复喊，否则 server-*.log 会被刷爆。
        broken.Info("Test", "第二轮");
        await broken.FlushAsync();
        failures.Should().Be(1, "同一段失败只在第一次喊");

        // 把障碍挪开 ⇒ 下一轮应当恢复成功并喊一声。
        File.Delete(logDir);
        broken.Info("Test", "第三轮");
        await broken.FlushAsync();

        recoveries.Should().Be(1);
        broken.ConsecutiveFlushFailures.Should().Be(0);
        broken.LastFlushError.Should().BeNull();

        // 健康的那个实例从头到尾没报过。
        log.Info("Test", "正常");
        await log.FlushAsync();
        log.ConsecutiveFlushFailures.Should().Be(0);
    }

    [Fact]
    public async Task Written_log_file_keeps_chinese_readable_so_findstr_works()
    {
        // 🔴 这条是 M6.5 实测撞出来的：默认序列化会把中文转成 \uXXXX，
        //    于是现场 `findstr 授权 logs\log-*.ndjson` **一个字都匹配不上**，
        //    人会据此得出"日志里没有这条"的错误结论，然后往完全错的方向查。
        var dataDirectory = new TempDataDirectory(Path.Combine(_root, "data-plain"));
        Directory.CreateDirectory(dataDirectory.LogDirectory);

        await using var log = new LogService(dataDirectory, capacity: 100, flushIntervalMs: 600_000);
        log.Info("Test", "授权还有 1 天到期");
        await log.FlushAsync();

        var file = Directory.EnumerateFiles(dataDirectory.LogDirectory, "log-*.ndjson").Single();
        var text = File.ReadAllText(file);

        text.Should().Contain("授权还有 1 天到期", "落盘文件必须能被人直接 grep");
        text.Should().NotContain("\\u6388", "中文字符被转义成 \\u 之后，findstr 就再也搜不到了");
    }

    private sealed class TempDataDirectory(string root) : IDataDirectory
    {
        public string Root { get; } = root;

        public string DatabaseFile => Path.Combine(Root, "control.db");

        public string ConfigDirectory => Path.Combine(Root, "config");

        public string LogDirectory => Path.Combine(Root, "logs");

        public string FilesDirectory => Path.Combine(Root, "files");

        public string ImagesDirectory => Path.Combine(Root, "images");

        public string CertificateDirectory => Path.Combine(Root, "https");

        public string LicenseFile => Path.Combine(Root, "license.lic");

        public string TimeStampFile => Path.Combine(Root, ".ts");

        public void EnsureCreated() => Directory.CreateDirectory(Root);
    }
}
