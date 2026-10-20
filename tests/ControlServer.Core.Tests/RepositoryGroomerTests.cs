using System.Security.Cryptography;
using System.Text;
using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 文件仓库对账（M7.1）：<b>库里的记录 ↔ files/ 下的文件</b>。
///
/// <para>
/// <b>这批用例存在的理由</b>：判据的核心是"超龄"与"有没有主人"，两者都无法靠等待测出来。
/// 所以 <see cref="RepositoryGroomer.Scan"/> 把"现在"当参数，把文件 mtime 直接写成历史。
/// </para>
///
/// <para>
/// 🔴 <b>最重要的一条是 <c>Inside_grace</c> 那个用例</b>：它守的是
/// 「完成上传先 Move 再落库」之间的窗口。少了它，一个"看起来更激进"的实现
/// （不在库里就删）能通过所有其他用例 —— 然后在生产上删掉刚上传成功的文件。
/// </para>
///
/// <para>
/// ⚠️ 铁律「断言三问」：不只断言 <c>Orphans.Count</c>，还要断言
/// <b>是哪一个</b>、<b>该留的还在</b>、<b>别的目录一个都没动</b>。
/// </para>
/// </summary>
public sealed class RepositoryGroomerTests : IDisposable
{
    /// <summary>固定的"现在"。用常量而不是 <c>DateTime.UtcNow</c>，用例才不会在午夜或换时区时飘。</summary>
    private static readonly DateTime NowUtc = new(2026, 10, 18, 2, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Grace = TimeSpan.FromHours(24);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "groom-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string _repo;
    private readonly string _staging;

    public RepositoryGroomerTests()
    {
        _repo = Path.Combine(_root, "files", "repo");
        _staging = Path.Combine(_root, "files", "uploads");

        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_staging);
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

    // ═══════════════════════════════════════════════════
    // 孤儿（盘上有、库里没有）
    // ═══════════════════════════════════════════════════

    [Fact]
    public void Orphan_older_than_grace_is_found_and_deleted()
    {
        var orphan = MakeFile(_repo, "f_orphan.bin", bytes: 128, ageHours: 30);

        var survey = Scan(items: [], sessions: []);

        survey.Orphans.Should().ContainSingle()
            .Which.Name.Should().Be("f_orphan.bin", "要指名道姓，不能只报个数");

        survey.OrphanBytes.Should().Be(128);

        var result = RepositoryGroomer.Collect(survey, _repo, _staging);

        result.DeletedFiles.Should().Be(1);
        result.DeletedBytes.Should().Be(128);
        result.Failures.Should().BeEmpty();

        File.Exists(orphan).Should().BeFalse("超过宽限期的孤儿就该被回收");
    }

    [Fact]
    public void Orphan_inside_grace_is_left_alone()
    {
        // 🔴 这个文件"不在库里"，但它可能是**刚刚上传完成、还没落库**的那个。
        //    按"不在库里就删"去清理，用户会看到"上传成功但文件没了"。
        var fresh = MakeFile(_repo, "f_just_moved.bin", bytes: 64, ageHours: 0.5);

        var survey = Scan(items: [], sessions: []);

        survey.Orphans.Should().BeEmpty("宽限期内的文件一律当它是\"正在落库\"，不能碰");

        var result = RepositoryGroomer.Collect(survey, _repo, _staging);

        result.DeletedFiles.Should().Be(0);
        File.Exists(fresh).Should().BeTrue();
    }

    [Fact]
    public void File_with_a_db_record_is_never_an_orphan()
    {
        var known = MakeFile(_repo, "f_known.bin", bytes: 256, ageHours: 500);

        var survey = Scan(items: [new RepoEntry(7, "说明.pdf", "f_known.bin", 256)], sessions: []);

        survey.Orphans.Should().BeEmpty("库里有记录就是有主人，再老也不能删");
        survey.Lost.Should().BeEmpty();
        survey.DbItems.Should().Be(1);
        survey.DbBytes.Should().Be(256);
        survey.RepoFiles.Should().Be(1);
        survey.RepoBytes.Should().Be(256);

        RepositoryGroomer.Collect(survey, _repo, _staging).DeletedFiles.Should().Be(0);
        File.Exists(known).Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════
    // 断链（库里有、盘上没有）
    // ═══════════════════════════════════════════════════

    [Fact]
    public void Lost_item_is_reported_but_never_deleted()
    {
        // 盘上只有其中一条 —— 另一条是"库里有、盘上没有"。
        MakeFile(_repo, "f_here.pdf", bytes: 100, ageHours: 100);

        var survey = Scan(
            items:
            [
                new RepoEntry(3, "现场录像.mp4", "f_gone.mp4", 4096),
                new RepoEntry(4, "还在.pdf", "f_here.pdf", 100),
            ],
            sessions: []);

        survey.Lost.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Id = 3, Name = "现场录像.mp4", StoredName = "f_gone.mp4", Size = 4096L });

        // 🔴 断链不是孤儿：不能因为"盘上没有"就把它当成可清理的东西。
        survey.Orphans.Should().BeEmpty();

        var result = RepositoryGroomer.Collect(survey, _repo, _staging);

        result.DeletedFiles.Should().Be(0);
        File.Exists(Path.Combine(_repo, "f_here.pdf")).Should().BeTrue();
    }

    [Fact]
    public void Everything_is_lost_when_the_repo_directory_does_not_exist()
    {
        Directory.Delete(_repo, recursive: true);

        var survey = Scan(items: [new RepoEntry(1, "a.zip", "f_a.zip", 10)], sessions: []);

        survey.Lost.Should().ContainSingle();
        survey.RepoFiles.Should().Be(0);
        survey.RepoBytes.Should().Be(0);

        // 目录都没了还去"回收"，只会把整件事变成一次静默的空操作。
        RepositoryGroomer.Collect(survey, _repo, _staging).DeletedFiles.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════
    // uploads/*.part
    // ═══════════════════════════════════════════════════

    [Fact]
    public void Part_with_a_live_session_is_never_touched_even_when_old()
    {
        // 🔴 这条是最容易被"顺手优化掉"的一条：超龄 ≠ 无主。
        //    会话还活着（比如现场把 SessionExpiryHours 调大了），这个 .part 就是它的续传进度，
        //    删掉之后客户端下一次续传会从 0 开始，而且**不会报错**。
        var part = MakeFile(_staging, "f_active.bin.part", bytes: 512, ageHours: 100);

        var survey = Scan(items: [], sessions: ["f_active.bin"]);

        survey.StaleParts.Should().BeEmpty();
        survey.PartFiles.Should().Be(1, "有主人的 .part 也要计入总数，否则 files.parts 会撒谎");
        survey.PartBytes.Should().Be(512);
        survey.LiveSessions.Should().Be(1);

        RepositoryGroomer.Collect(survey, _repo, _staging).DeletedFiles.Should().Be(0);
        File.Exists(part).Should().BeTrue();
    }

    [Fact]
    public void Part_without_any_session_and_old_is_stale()
    {
        // 恢复旧备份之后最典型的样子：会话表里没有它，磁盘上却留着半截。
        var part = MakeFile(_staging, "f_ghost.bin.part", bytes: 2048, ageHours: 48);

        var survey = Scan(items: [], sessions: []);

        survey.StaleParts.Should().ContainSingle().Which.Name.Should().Be("f_ghost.bin.part");
        survey.StalePartBytes.Should().Be(2048);

        var result = RepositoryGroomer.Collect(survey, _repo, _staging);

        result.DeletedFiles.Should().Be(1);
        File.Exists(part).Should().BeFalse();
    }

    [Fact]
    public void Part_without_session_but_fresh_is_left_alone()
    {
        // 会话刚建、记录还没落库的窗口；或者客户端刚 Abort、文件正被删。
        var part = MakeFile(_staging, "f_racing.bin.part", bytes: 32, ageHours: 0.2);

        var survey = Scan(items: [], sessions: []);

        survey.StaleParts.Should().BeEmpty();

        RepositoryGroomer.Collect(survey, _repo, _staging).DeletedFiles.Should().Be(0);
        File.Exists(part).Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════
    // 边界
    // ═══════════════════════════════════════════════════

    [Fact]
    public void Subdirectories_are_reported_and_never_walked_into()
    {
        // repo/ 本该是平铺的（StoredName 是单段名）。出现子目录说明有人手工动过 ——
        // 那种情况下"递归删"是最坏的选择。
        var nested = Path.Combine(_repo, "手工备份");
        Directory.CreateDirectory(nested);

        var inside = Path.Combine(nested, "别删我.zip");
        File.WriteAllBytes(inside, new byte[64]);
        File.SetLastWriteTimeUtc(inside, NowUtc.AddHours(-999));

        var survey = Scan(items: [], sessions: []);

        survey.UnexpectedDirectories.Should().ContainSingle().Which.Should().Be("手工备份");

        // 关键：子目录里的文件既不算孤儿（不递归），也不会被删。
        survey.Orphans.Should().BeEmpty();
        survey.RepoFiles.Should().Be(0, "只数顶层文件");

        RepositoryGroomer.Collect(survey, _repo, _staging).DeletedFiles.Should().Be(0);
        File.Exists(inside).Should().BeTrue("对账不许走进子目录");
    }

    [Fact]
    public void Delete_failure_is_reported_instead_of_thrown()
    {
        var locked = MakeFile(_repo, "f_locked.bin", bytes: 16, ageHours: 30);

        var survey = Scan(items: [], sessions: []);
        survey.Orphans.Should().ContainSingle();

        // 独占打开它，让 File.Delete 必定失败（Windows 上 Delete 对无共享句柄会抛 IOException）。
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = RepositoryGroomer.Collect(survey, _repo, _staging);

            result.DeletedFiles.Should().Be(0, "删不掉就不能报成已清理");
            result.Failures.Should().ContainSingle();

            // 失败信息里要有点名文件的东西，否则日志里只有一句"删不掉"。
            result.Failures[0].Should().Contain("f_locked.bin");
        }

        // 句柄放开之后下一轮就能删掉了。
        RepositoryGroomer.Collect(survey, _repo, _staging).DeletedFiles.Should().Be(1);
    }

    [Fact]
    public async Task Survey_reads_through_the_store_and_uses_the_same_verdict()
    {
        // 这一条守的是"接线"：Scan 是纯函数，很容易测对；真正出错的地方常常是
        // SurveyAsync 忘了把某一侧喂进去（比如漏了 ListSessionsAsync）。
        var dataDir = new TempDataDirectory(_root);
        var store = new FakeFileRepositoryStore();

        // 这条走真实时钟（SurveyAsync 内部取 BeijingTime.Now），所以 mtime 也必须按真实时钟算。
        var nowReal = DateTime.UtcNow;

        var orphan = MakeFile(_repo, "f_orphan.bin", bytes: 8, ageHours: 30, baseUtc: nowReal);
        var known = MakeFile(_repo, "f_known.bin", bytes: 8, ageHours: 30, baseUtc: nowReal);
        var partOfLive = MakeFile(_staging, "f_live.bin.part", bytes: 8, ageHours: 30, baseUtc: nowReal);

        await store.AddAsync(new FileRepoItem(
            Id: 0,
            Name: "在用.bin",
            StoredName: "f_known.bin",
            Size: 8,
            Sha256: new string('a', 64),
            Kind: FileKinds.Normal,
            UploadedBy: "tester",
            UploadedAt: DateTimeOffset.UtcNow,
            OriginAgentId: null,
            Note: null));

        await store.CreateSessionAsync(new FileUploadSession(
            Id: "u1",
            FileName: "活的.bin",
            StoredName: "f_live.bin",
            Size: 1024,
            Kind: FileKinds.Normal,
            Sha256: null,
            CreatedBy: "tester",
            OriginAgentId: null,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(12),
            State: FileUploadStates.Open));

        var groomer = new RepositoryGroomer(
            store,
            dataDir,
            new FileRepositoryOptions { OrphanGraceHours = 24 });

        var survey = await groomer.SurveyAsync();

        survey.DbItems.Should().Be(1);
        survey.Orphans.Select(x => x.Name).Should().Equal("f_orphan.bin");
        survey.StaleParts.Should().BeEmpty("会话还活着");
        survey.Lost.Should().BeEmpty();

        (await groomer.CollectAsync(survey)).DeletedFiles.Should().Be(1);

        File.Exists(orphan).Should().BeFalse();
        File.Exists(known).Should().BeTrue();
        File.Exists(partOfLive).Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════

    private RepositorySurvey Scan(IReadOnlyList<RepoEntry> items, IReadOnlyList<string> sessions)
        => RepositoryGroomer.Scan(items, sessions, _repo, _staging, NowUtc, Grace);

    /// <summary>
    /// 造一个文件，并把 mtime 改成 <paramref name="ageHours"/> 小时之前。
    /// <para>
    /// ⚠️ <paramref name="baseUtc"/> 必须与调用方的"现在"对齐：<see cref="Scan"/> 的用例
    /// 用固定的 <see cref="NowUtc"/>，而走 <c>SurveyAsync</c> 的用例只能拿到<b>真实</b>时钟 ——
    /// 两者混用会让"30 小时前"变成"未来"，文件被判成"太新"而静默跳过
    /// （第一版就是这么错的：11 条里 10 条绿，唯一漏掉的是接线那条）。
    /// </para>
    /// </summary>
    // ═══════════════════════════════════════════════════════════
    // 内容校验（重算 SHA-256，M7.2）
    //
    // 这一组守的是"对账看不出来的那种坏"：大小一模一样、字节被换掉。
    // 它比断链更危险 —— 断链会 404（一眼可见），内容坏了是"下载成功、打开是坏的"。
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task VerifyContents_reports_a_file_whose_bytes_changed_but_size_stayed()
    {
        WriteText(_repo, "f_1.bin", "hello");
        WriteText(_repo, "f_2.bin", "HELLO");

        var items = new List<RepoEntry>
        {
            new(1, "原样.bin", "f_1.bin", 5, Sha("hello")),
            new(2, "被换.bin", "f_2.bin", 5, Sha("hello")),
        };

        var result = await RepositoryGroomer.VerifyContentsAsync(items, _repo, maxFiles: 10, maxBytes: 1024 * 1024);

        result.Checked.Should().Be(2);
        result.Mismatches.Should().ContainSingle()
            .Which.Id.Should().Be(2, "大小相同、内容不同的那一份才是这里要抓的");
        result.Mismatches[0].Reason.Should().Contain("摘要不符");
        result.Skipped.Should().Be(0);
    }

    [Fact]
    public async Task VerifyContents_reports_a_size_mismatch_without_reading_the_whole_file()
    {
        WriteText(_repo, "f_1.bin", "hello world");

        var items = new List<RepoEntry> { new(1, "样例.bin", "f_1.bin", 5, Sha("hello")) };

        var result = await RepositoryGroomer.VerifyContentsAsync(items, _repo, maxFiles: 10, maxBytes: 1024 * 1024);

        result.Mismatched.Should().Be(1);
        result.Mismatches[0].Reason.Should().Contain("大小对不上");
        result.Checked.Should().Be(0, "大小先比对得上才值得读字节；这一条没读");
    }

    [Fact]
    public async Task VerifyContents_counts_lost_files_as_skipped_not_as_mismatch()
    {
        // 盘上根本不存在 —— 那是断链（Lost 那条线）的活。
        // 两边都报会让现场以为"文件既丢了又坏了"，按两种方向各修一次。
        var items = new List<RepoEntry> { new(1, "丢了.bin", "f_missing.bin", 5, Sha("hello")) };

        var result = await RepositoryGroomer.VerifyContentsAsync(items, _repo, maxFiles: 10, maxBytes: 1024 * 1024);

        result.Mismatched.Should().Be(0);
        result.Checked.Should().Be(0);
        result.Skipped.Should().Be(1);
    }

    [Fact]
    public async Task VerifyContents_stops_at_the_budget_and_says_how_many_it_skipped()
    {
        for (var i = 1; i <= 5; i++)
        {
            WriteText(_repo, $"f_{i}.bin", "hello");
        }

        var items = Enumerable.Range(1, 5)
            .Select(i => new RepoEntry(i, $"第{i}个.bin", $"f_{i}.bin", 5, Sha("hello")))
            .ToList();

        var result = await RepositoryGroomer.VerifyContentsAsync(items, _repo, maxFiles: 2, maxBytes: 1024 * 1024);

        result.Checked.Should().Be(2, "预算先到先停：顺序固定才说得出「验的是最老的两个」");
        result.Skipped.Should().Be(3, "没验的必须如实报出来 —— 不报就等于假装全验过了");
        result.Mismatched.Should().Be(0);
    }

    [Fact]
    public async Task VerifyContents_refuses_a_stored_name_that_is_not_a_single_segment()
    {
        // 库里被塞了 `..\..\https\server.pfx` 这类名字时，绝不能照着它去读盘
        // —— 那等于给了一个"用摘要探测任意文件"的接口。
        var items = new List<RepoEntry>
        {
            new(1, "越界", @"..\..\https\server.pfx", 5, Sha("hello")),
            new(2, "带目录", @"sub/f_1.bin", 5, Sha("hello")),
        };

        var result = await RepositoryGroomer.VerifyContentsAsync(items, _repo, maxFiles: 10, maxBytes: 1024 * 1024);

        result.Checked.Should().Be(0);
        result.Failures.Should().HaveCount(2);
        result.Failures.Should().OnlyContain(f => f.Contains("不是单段名"));
    }

    [Fact]
    public async Task VerifyContents_skips_records_without_a_digest_and_says_so()
    {
        WriteText(_repo, "f_1.bin", "hello");

        var items = new List<RepoEntry> { new(1, "老数据.bin", "f_1.bin", 5, Sha256: null) };

        var result = await RepositoryGroomer.VerifyContentsAsync(items, _repo, maxFiles: 10, maxBytes: 1024 * 1024);

        result.Checked.Should().Be(0, "没有摘要就无从校验 —— 不能默认当成「通过」");
        result.Mismatched.Should().Be(0);
        result.Failures.Should().ContainSingle().Which.Should().Contain("没有摘要");
    }

    private static string Sha(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void WriteText(string dir, string name, string text)
        => File.WriteAllText(Path.Combine(dir, name), text, new UTF8Encoding(false));

    private static string MakeFile(string dir, string name, int bytes, double ageHours, DateTime? baseUtc = null)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);

        // 显式写 mtime —— 判龄靠它，不靠"创建时间"或"目录里躺着多久"。
        File.SetLastWriteTimeUtc(path, (baseUtc ?? NowUtc).AddHours(-ageHours));

        return path;
    }

    private sealed class TempDataDirectory(string root) : IDataDirectory
    {
        public string Root => root;

        public string DatabaseFile => Path.Combine(root, "control.db");

        public string ConfigDirectory => Path.Combine(root, "config");

        public string LogDirectory => Path.Combine(root, "logs");

        public string FilesDirectory => Path.Combine(root, "files");

        public string ImagesDirectory => Path.Combine(root, "images");

        public string CertificateDirectory => Path.Combine(root, "https");

        public string LicenseFile => Path.Combine(root, "license.lic");

        public string TimeStampFile => Path.Combine(root, "timestamp.json");

        public void EnsureCreated()
        {
        }
    }
}
