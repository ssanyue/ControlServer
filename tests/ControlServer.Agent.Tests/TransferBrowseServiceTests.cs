using ControlServer.Agent.Transfer;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// 客户机侧<b>目录浏览</b>（<see cref="TransferBrowseService"/>）的单元测试。
///
/// <para>
/// <b>2026-10-13 起浏览是"整盘"的</b>（产品决策：要像远程资源管理器一样能挑目录），
/// 所以本组用例分两半：
/// </para>
/// <list type="bullet">
///   <item><b>该看得见的都看得见</b>：盘符列表、任意目录逐级进入、<c>..</c> 被正确解析；</item>
///   <item><b>该老实的地方仍老实</b>：只读（不建不删）、非法路径给人话而不是异常、
///         传输关掉时连盘符都不给。</item>
/// </list>
///
/// <para>
/// ⚠️ <b>"能浏览"不等于"能写"</b>：真正落地 / 读取的边界依旧是客户机的白名单
/// （见 <c>FileTransferClient</c> 的用例）。浏览放开只是让人挑得到路径，
/// 选到白名单外照样会在传输那一步被拒。
/// </para>
///
/// <para>用真的临时目录（真造文件、真枚举），不是桩文件系统 ——
/// 要验的正是"<c>Path.GetFullPath</c> 到底把 <c>..</c> 解析成了什么"，
/// 那件事只有真的调它才能验。</para>
/// </summary>
public sealed class TransferBrowseServiceTests
{
    // ═══════════════════════════════════════════════════════════
    // 顶层 = 这台机器有几个盘
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 顶层现在是<b>盘符列表</b>（"此电脑"），不再是"白名单根目录"。
    /// <para>它是"整盘浏览"这个能力的入口 —— 列不出盘符，用户就无从下手。</para>
    /// </summary>
    [Fact]
    public async Task The_top_level_lists_the_drive_letters()
    {
        await using var h = new Harness();

        var result = h.Browse(null);

        result.Ok.Should().BeTrue();
        result.RequestedPath.Should().BeNull("顶层不是一个真实目录，不能假装进到了某个目录里");
        result.ParentPath.Should().BeNull();

        result.Roots.Should().NotBeNullOrEmpty("一台能跑 Windows 的机器至少有一个已就绪的盘");
        result.Roots!.Should().OnlyContain(r => Path.IsPathRooted(r), "列出的必须都是盘符（可点进去的路径）");

        result.Entries.Should().HaveCount(result.Roots!.Count);
        // ⚠️ 比较整段集合要传"一个数组"，不能写成 `Equal(a, b)` —— 那会被当成"两个期望元素"，
        //    报错是含糊的"少了一项"，与真实原因（参数绑错）毫无关系（本项目踩过 4 次）。
        result.Entries!.Select(e => e.Path).Should().Equal(result.Roots.ToArray(), "条目与 Roots 必须是同一批");
        result.Entries.Should().OnlyContain(e => e.IsDirectory);
    }

    /// <summary>
    /// 盘符的显示名要能说清是哪个盘（<c>D:\（数据）（固定）</c>），不能只剩一个字母。
    /// <para>现场同一台机器上 C 与 D 一眼分不出哪个是数据盘，而卷标是运维自己起的名字。</para>
    /// </summary>
    [Fact]
    public async Task A_drive_entry_carries_more_than_just_the_letter()
    {
        await using var h = new Harness();

        var entries = h.Browse(null).Entries!;

        entries.Should().OnlyContain(e => e.Name.StartsWith(e.Path, StringComparison.Ordinal),
            "名字以盘符开头，后面带着卷标 / 类型");
        entries.Should().OnlyContain(e => e.Name.Length > e.Path.Length, "至少带上盘的类型");
    }

    [Fact]
    public async Task An_empty_path_is_the_same_as_no_path()
    {
        await using var h = new Harness();

        var blank = h.Browse(string.Empty);

        blank.Ok.Should().BeTrue();
        blank.RequestedPath.Should().BeNull();
        blank.Entries.Should().HaveCount(h.Browse(null).Entries!.Count, "空串与 null 都是'给我顶层'");
    }

    // ═══════════════════════════════════════════════════════════
    // 列目录
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Directories_come_before_files_and_carry_their_attributes()
    {
        await using var h = new Harness();

        h.Dir("logs");
        h.Dir("zebra");
        h.File("readme.txt", "hello");
        h.File("a.dat", "x");

        var result = h.Browse(h.Root);

        result.Ok.Should().BeTrue();
        result.RequestedPath.Should().Be(h.Root);
        result.Separator.Should().Be(Path.DirectorySeparatorChar.ToString());

        var names = result.Entries!.Select(e => e.Name).ToArray();

        // 目录在前（同组按名排序），文件在后（同组按名排序）。
        names.Should().Equal(new[] { "logs", "zebra", "a.dat", "readme.txt" });

        var readme = result.Entries!.Single(e => e.Name == "readme.txt");

        readme.IsDirectory.Should().BeFalse();
        readme.Size.Should().Be(5);
        readme.ModifiedAt.Should().NotBeNull("能读到修改时间就该带上");
        readme.Path.Should().Be(Path.Combine(h.Root, "readme.txt"), "条目要带可直接使用的完整路径");
    }

    /// <summary>
    /// 🔥 盘根的"上一级"必须是 <b>null</b>。
    /// <para>
    /// 界面上要能区分"再往上一级"与"已经没有上一级了（回盘符列表）"——
    /// 给一个点了没反应的按钮，用户只会以为界面坏了。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_drive_root_has_no_parent_and_deeper_directories_walk_back_up()
    {
        await using var h = new Harness();

        h.Dir("inner");

        var driveRoot = Path.GetPathRoot(h.Root)!;

        h.Browse(driveRoot).ParentPath.Should().BeNull("盘根没有上一级");

        // 子目录指着父目录，一路能走回盘根 —— 这正是"资源管理器"该有的行为。
        var inner = h.Browse(Path.Combine(h.Root, "inner"));
        inner.Ok.Should().BeTrue();
        inner.ParentPath.Should().Be(h.Root);

        h.Browse(h.Root).ParentPath.Should().Be(Path.GetDirectoryName(h.Root));
    }

    /// <summary>空目录是正常结果，不是失败。</summary>
    [Fact]
    public async Task An_empty_directory_is_a_clear_empty_result()
    {
        await using var h = new Harness();

        var result = h.Browse(h.Root);

        result.Ok.Should().BeTrue("空目录是正常结果，不是失败");
        result.Entries.Should().BeEmpty();
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task A_directory_is_never_listed_beyond_the_cap_and_says_so()
    {
        await using var h = new Harness();

        // 上限 + 5：证明确实截断了，而且截断这件事是被说出来的。
        for (var i = 0; i < TransferBrowseService.MaxEntries + 5; i++)
        {
            h.File($"f{i:0000}.dat", "x");
        }

        var result = h.Browse(h.Root);

        result.Ok.Should().BeTrue();
        result.Entries.Should().HaveCount(TransferBrowseService.MaxEntries);
        result.Truncated.Should().BeTrue(
            "不告诉用户就等于把'排在截断线之后'说成'这个文件不存在'");
    }

    // ═══════════════════════════════════════════════════════════
    // 整盘浏览：白名单**不再**是浏览的边界
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>本组最重要的一条回归</b>：白名单<b>之外</b>的目录现在必须可以浏览。
    /// <para>
    /// 2026-10-13 之前它会被 <c>denied</c> 挡下（当年"只列白名单内"的设计）。
    /// 现在它必须返回 <c>ok</c> —— 否则用户根本挑不到"想落到哪儿"，
    /// 而"选到白名单外会被拒"是<b>传输那一步</b>的事。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_directory_outside_the_write_whitelist_is_still_browsable()
    {
        await using var h = new Harness();

        var outside = h.Outside("elsewhere");

        var result = h.Browse(outside);

        result.Ok.Should().BeTrue("浏览已放开为整盘：能不能写是传输那一步的事");
        result.Status.Should().Be(AgentBrowseStatuses.Ok);
        result.RequestedPath.Should().Be(outside);
    }

    /// <summary>
    /// 🔥 <c>..</c> 必须被<b>解析</b>过再返回，不能原样丢回去。
    /// <para>原样返回会让上层拿到一个"字面上不是绝对路径"的值，
    /// 而客户机之后的写入校验是按解析后的绝对路径比对的 —— 两边对不上就会出现
    /// "看着是同一个目录、一边说在名单内一边说不在"。</para>
    /// </summary>
    [Fact]
    public async Task A_dot_dot_path_is_resolved_before_it_is_reported()
    {
        await using var h = new Harness();

        h.Dir("inner");

        var result = h.Browse(Path.Combine(h.Root, "inner", ".."));

        result.Ok.Should().BeTrue();
        result.RequestedPath.Should().Be(h.Root, "`inner\\..` 解析之后就是根本身");
    }

    [Fact]
    public async Task A_path_that_is_a_file_is_reported_as_such_not_as_not_found()
    {
        await using var h = new Harness();

        var file = h.File("app.log", "x");

        var result = h.Browse(file);

        // 与"不存在"分开报：一个是"这不是目录"，一个是"没有这东西"，
        // 运维要做的处置完全不同。
        result.Ok.Should().BeFalse();
        result.Status.Should().Be(AgentBrowseStatuses.NotDirectory);
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_missing_directory_is_reported_as_not_found()
    {
        await using var h = new Harness();

        var result = h.Browse(Path.Combine(h.Root, "no-such-dir"));

        result.Ok.Should().BeFalse();
        result.Status.Should().Be(AgentBrowseStatuses.NotFound);
    }

    /// <summary>
    /// 非法路径要给<b>人话</b>，不能抛异常 —— 抛出去会把一次浏览变成一条 500，
    /// 而用户在界面上看到的会是"系统出错"（实际他只是手输时多打了一个字符）。
    /// </summary>
    [Theory]
    [InlineData("C:\\a\0b")]          // 含 NUL
    [InlineData("|:\\nope")]          // 非法盘符
    public async Task An_illegal_path_is_reported_as_a_readable_failure(string bad)
    {
        await using var h = new Harness();

        var result = h.Browse(bad);

        result.Ok.Should().BeFalse();
        result.Status.Should().Be(AgentBrowseStatuses.NotFound);
        result.Message.Should().NotBeNullOrWhiteSpace("必须是一句人话");
    }

    /// <summary>文件传输整个关掉时，不该还能"看见"任何东西。</summary>
    [Fact]
    public async Task Browsing_is_refused_when_transfer_is_disabled()
    {
        await using var h = new Harness(transferEnabled: false);

        var result = h.Browse(null);

        result.Ok.Should().BeFalse();
        result.Status.Should().Be(AgentBrowseStatuses.Disabled);
        result.Entries.Should().BeNull("关掉之后连「有哪些盘」都不该给");
        result.Roots.Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    /// <summary>真目录、真文件、真枚举；只有日志是"不落盘"的。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TestDataDirectory _data;
        private readonly TransferBrowseService _service;

        public Harness(bool transferEnabled = true)
        {
            _data = new TestDataDirectory(
                Path.Combine(Path.GetTempPath(), "cs-browse-tests", Guid.NewGuid().ToString("N")));

            // flushIntervalMs 设得很大：这些用例只关心浏览结果，不需要日志落盘。
            Log = new LogService(_data, capacity: 500, flushIntervalMs: 600_000);

            Root = Path.Combine(_data.Root, "whitelist");
            Directory.CreateDirectory(Root);

            // ⚠️ 刻意仍然配上下发（可写）白名单：本组要证明的正是"浏览不看这份名单"。
            var options = new TransferOptions
            {
                Enabled = transferEnabled,
                InboxDirectories = new List<string> { Root },
                DefaultInboxDirectory = Root,
            };

            _service = new TransferBrowseService(options, Log);
        }

        public LogService Log { get; }

        /// <summary>用例造东西的地方（同时被配成了"白名单目录"）。</summary>
        public string Root { get; }

        public AgentBrowseResultDto Browse(string? path) => _service.Browse(new AgentBrowseRequestDto(path));

        public string Dir(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public string File(string name, string content)
        {
            var path = Path.Combine(Root, name);
            System.IO.File.WriteAllText(path, content);
            return path;
        }

        /// <summary>白名单<b>之外</b>的一个目录（用来证明"名单外也看得到"）。</summary>
        public string Outside(string name)
        {
            var path = Path.Combine(_data.Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public async ValueTask DisposeAsync()
        {
            await Log.DisposeAsync();

            try
            {
                if (Directory.Exists(_data.Root)) Directory.Delete(_data.Root, recursive: true);
            }
            catch
            {
                // 测试清理失败不影响结论。
            }
        }
    }
}
