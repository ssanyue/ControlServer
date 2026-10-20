using ControlServer.Contracts;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// <see cref="FileTransferMath"/> 的纯函数用例（M4.4）。
/// <para>
/// 这个类里的每个函数都被服务端**与客户端（Agent）共用同一份实现** ——
/// 路径白名单这种东西只要有两份实现，就一定会有一份是错的（实测已经踩过一次：
/// 图片目录的 <c>StartsWith</c> 少了末尾分隔符，把 <c>files-evil</c> 判成了目录内）。
/// 因此这里逐条钉死语义，且每一条断言背后都有一个**具体的错法**。
/// </para>
/// </summary>
public sealed class FileTransferMathTests
{
    // ═══════════════════════════════════════════════════════════
    // 进度
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 总大小未知时必须是 <b>0</b>，不是 100。
    /// <para>
    /// 回传任务在客户端量出源文件大小之前 <c>total</c> 就是 0。
    /// 若这里返回 100，页面上一个**刚开始**的回传任务会显示成"100% 已完成" ——
    /// 运维会以为传完了，而实际上一个字节都还没到。
    /// </para>
    /// </summary>
    [Fact]
    public void Progress_is_zero_when_total_is_unknown()
    {
        FileTransferMath.ProgressPercent(0, 0).Should().Be(0, "total=0 表示「还不知道」，显示 0% 比 100% 安全");
        FileTransferMath.ProgressPercent(500, 0).Should().Be(0);
        FileTransferMath.ProgressPercent(500, -1).Should().Be(0);
    }

    [Fact]
    public void Progress_is_computed_and_clamped_between_zero_and_hundred()
    {
        FileTransferMath.ProgressPercent(25, 100).Should().Be(25);
        FileTransferMath.ProgressPercent(100, 100).Should().Be(100);

        // 超过 100 说明收发两端的口径对不上（例如重试写多了一段）。
        // 夹住比让进度条飞出画布更容易被发现 —— 这是**有意的**，不是顺手 clamp。
        FileTransferMath.ProgressPercent(150, 100).Should().Be(100);

        // 负的已收字节是坏数据，画成 0 而不是负进度。
        FileTransferMath.ProgressPercent(-3, 100).Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════
    // 文件名清洗
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 只取最后一段 —— 无论对方用 <c>\</c> 还是 <c>/</c>。
    /// <para>
    /// 这是"用户文件名里的目录成分"被剥离的唯一一道关。若漏了，
    /// <c>..\..\Windows\System32\evil.dll</c> 会被当成一个合法文件名存下去，
    /// 之后任何"拿名字拼路径"的地方就都是任意写文件。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(@"..\..\evil.dll", "evil.dll")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"C:\Windows\System32\cmd.exe", "cmd.exe")]
    [InlineData(@"folder/sub/报告.pdf", "报告.pdf")]
    [InlineData("plain.txt", "plain.txt")]
    public void Sanitize_keeps_only_the_last_segment(string input, string expected)
        => FileTransferMath.SanitizeFileName(input).Should().Be(expected);

    [Fact]
    public void Sanitize_strips_leading_and_trailing_dots_and_whitespace()
    {
        // Windows 上以点结尾的目录项本身就是非法的；而 "." / ".." 更是目录穿越的原料。
        FileTransferMath.SanitizeFileName("  name.txt  ").Should().Be("name.txt");
        FileTransferMath.SanitizeFileName("...secret...").Should().Be("secret");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData(".")]
    public void Sanitize_falls_back_to_a_constant_when_nothing_usable_remains(string? input)
        => FileTransferMath.SanitizeFileName(input).Should().Be("file", "拿不到可用名就回退常量，绝不返回空串（空串会变成目录路径）");

    [Fact]
    public void Sanitize_drops_control_characters_and_caps_the_length()
    {
        FileTransferMath.SanitizeFileName("a\r\n\tb.txt").Should().Be("ab.txt", "控制字符一律去掉，不让它污染文件名");

        var longName = new string('x', 400) + ".txt";
        FileTransferMath.SanitizeFileName(longName).Length.Should().Be(180, "超长名截断，避免超过文件系统上限");
    }

    // ═══════════════════════════════════════════════════════════
    // 扩展名与落盘名
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("photo.PNG", ".png")]
    [InlineData("setup.exe", ".exe")]
    [InlineData("archive.tar", ".tar")]
    public void Safe_extension_returns_a_lowercase_dotted_extension(string input, string expected)
        => FileTransferMath.SafeExtension(input).Should().Be(expected);

    /// <summary>
    /// ⚠️ 拿不到安全扩展名时返回 <b>空串</b>，调用方回退 <c>.bin</c> ——
    /// 而不是硬塞一个"猜的"扩展名（那会让一个伪装文件看起来像合法的 png）。
    /// </summary>
    [Theory]
    [InlineData("noextension")]
    [InlineData("weird.p n g")]
    [InlineData("bad.b/../")]
    [InlineData("toolong.extensionname")]
    [InlineData(null)]
    public void Safe_extension_is_empty_for_untrustworthy_input(string? input)
        => FileTransferMath.SafeExtension(input).Should().BeEmpty();

    [Fact]
    public void Stored_name_is_a_single_segment_with_the_original_extension()
    {
        var name = FileTransferMath.GenerateStoredName("report.pdf");

        name.Should().StartWith("f_", "落盘名由服务端生成，前缀便于人工识别");
        name.Should().EndWith(".pdf", "保留扩展名只为让运维在资源管理器里看出类型");
        name.Should().NotContain("/").And.NotContain(@"\", "绝不含目录成分 —— 这是它可以直接拼路径的前提");
    }

    [Fact]
    public void Stored_name_falls_back_to_bin_and_is_unique()
    {
        FileTransferMath.GenerateStoredName("noextension").Should().EndWith(".bin");
        FileTransferMath.GenerateStoredName(null).Should().EndWith(".bin");

        var a = FileTransferMath.GenerateStoredName("same.txt");
        var b = FileTransferMath.GenerateStoredName("same.txt");

        a.Should().NotBe(b, "用 GUID 是为了杜绝覆盖与猜测");
    }

    // ═══════════════════════════════════════════════════════════
    // 路径穿越防护 —— 全篇最重要的一组
    // ═══════════════════════════════════════════════════════════

    private static string Root => Path.Combine(Path.GetTempPath(), "cs-ftm-root");

    [Fact]
    public void Is_inside_accepts_the_root_itself_and_its_descendants()
    {
        FileTransferMath.IsInside(Root, Root).Should().BeTrue();
        FileTransferMath.IsInside(Root, Path.Combine(Root, "a.txt")).Should().BeTrue();
        FileTransferMath.IsInside(Root, Path.Combine(Root, "sub", "deep", "b.bin")).Should().BeTrue();
    }

    /// <summary>
    /// 🔥🔥 <b>盘根本身也是一个合法的"目录"</b>（本项目 2026-10-15 修）。
    /// <para>
    /// <c>Path.TrimEndingDirectorySeparator("C:\")</c> 刻意<b>不裁根</b>，所以盘根归一化之后
    /// 仍然以分隔符结尾。此时再按"根 + 分隔符"拼前缀就变成 <c>C:\\</c>，
    /// 于是一个盘根之下的一切**全部**判成"不在目录内" ——
    /// 表现是"白名单里明明写了 D 盘、却一个文件都取不回来"。
    /// </para>
    /// <para>
    /// 这一条是"回传默认整盘可读"能成立的前提：可读范围就是由若干个盘根组成的。
    /// </para>
    /// </summary>
    [Fact]
    public void Is_inside_accepts_a_drive_root_as_the_directory()
    {
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(Environment.CurrentDirectory));

        driveRoot.Should().NotBeNullOrEmpty("用例依赖一个真实存在的盘根");

        FileTransferMath.IsInside(driveRoot!, driveRoot!).Should().BeTrue();
        FileTransferMath.IsInside(driveRoot!, Path.Combine(driveRoot!, "any", "deep", "file.txt"))
            .Should().BeTrue("盘根之下的一切都在盘内 —— 这里补错一个分隔符就会整盘失效");

        // 反例：另一个盘不带本盘前缀，不能因为"都是盘根"就互相通过。
        var other = OperatingSystem.IsWindows()
            ? (driveRoot!.StartsWith("C:", StringComparison.OrdinalIgnoreCase) ? @"D:\" : @"C:\")
            : "/nonexistent-other-root/";

        FileTransferMath.IsInside(driveRoot!, Path.Combine(other, "file.txt"))
            .Should().BeFalse();
    }

    /// <summary>
    /// 盘根写进白名单后，<see cref="FileTransferMath.NormalizeDirectories"/> 的产出形态
    /// 不许把盘根变成"该盘的当前目录"（<c>C:</c> 与 <c>C:\</c> 不是一回事）。
    /// </summary>
    [Fact]
    public void Normalizing_a_drive_root_keeps_it_a_root()
    {
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(Environment.CurrentDirectory))!;

        var normalized = FileTransferMath.NormalizeDirectories(new[] { driveRoot });

        normalized.Should().HaveCount(1);
        normalized[0].Should().Be(Path.GetFullPath(driveRoot), "盘根归一化后仍是盘根，不能退化成“盘符:”");

        FileTransferMath.IsInside(normalized[0], Path.Combine(driveRoot, "a", "b.txt"))
            .Should().BeTrue();
    }

    /// <summary>
    /// 🔥🔥 同名前缀的兄弟目录必须判为<b>目录外</b>。
    /// <para>
    /// <c>C:\data\files-evil</c> 以 <c>C:\data\files</c> 为前缀，
    /// 用裸 <c>StartsWith</c> 比较就会判成"在目录内" —— 本项目已经在图片目录上踩过一次。
    /// 唯一正确的做法是比较时带末尾目录分隔符。
    /// </para>
    /// </summary>
    [Fact]
    public void Is_inside_rejects_a_sibling_that_merely_shares_the_prefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs-ftm-files");
        var sibling = Path.Combine(Path.GetTempPath(), "cs-ftm-files-evil", "payload.exe");

        FileTransferMath.IsInside(root, sibling)
            .Should().BeFalse("兄弟目录共享前缀但不是目录内 —— 少了末尾分隔符就会误判");
    }

    [Fact]
    public void Is_inside_rejects_a_path_that_escapes_via_dotdot()
    {
        FileTransferMath.IsInside(Root, Path.Combine(Root, "..", "outside.txt")).Should().BeFalse();
        FileTransferMath.IsInside(Root, Path.Combine(Root, "sub", "..", "..", "escape")).Should().BeFalse();
    }

    [Fact]
    public void Is_inside_rejects_blank_inputs_and_illegal_paths()
    {
        FileTransferMath.IsInside("", Path.Combine(Root, "a")).Should().BeFalse();
        FileTransferMath.IsInside(Root, "").Should().BeFalse();

        // 含 NUL 的路径是非法路径 —— 判不通过，而不是去猜它"也许在目录里"。
        FileTransferMath.IsInside(Root, Root + "\0bad").Should().BeFalse();
    }

    [Fact]
    public void Try_resolve_inside_maps_a_relative_path_and_rejects_escapes()
    {
        FileTransferMath.TryResolveInside(Root, "sub/file.txt", out var resolved).Should().BeTrue();
        resolved.Should().Be(Path.Combine(Root, "sub", "file.txt"));

        FileTransferMath.TryResolveInside(Root, "../outside.txt", out var escaped).Should().BeFalse();
        escaped.Should().BeNull("越界时解析结果必须是 null，不能返回一个「差不多」的路径");

        FileTransferMath.TryResolveInside(Root, null, out var nothing).Should().BeFalse();
        nothing.Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════
    // 白名单归一化
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 配置来自人手，重复与末尾斜杠是常态 —— 不归一化会让"同一个目录"在比较时不等，
    /// 症状是"我明明把目录加进白名单了，还是说不在白名单里"。
    /// </summary>
    [Fact]
    public void Normalize_directories_trims_dedupes_and_drops_blanks()
    {
        var a = Path.Combine(Path.GetTempPath(), "cs-ftm-NormA");

        var result = FileTransferMath.NormalizeDirectories(new[]
        {
            a,
            a + Path.DirectorySeparatorChar,   // 末尾分隔符
            "  " + a.ToUpperInvariant() + " ", // 大小写 + 空白（Windows 下应视为同一个）
            "",
            "   ",
            null!,
        });

        result.Should().HaveCount(1, "同一个目录的不同写法必须归一成一条");
        result[0].Should().EndWith("cs-ftm-NormA", "末尾目录分隔符应被去掉");
    }

    [Fact]
    public void Normalize_directories_of_null_is_empty()
        => FileTransferMath.NormalizeDirectories(null).Should().BeEmpty();

    // ═══════════════════════════════════════════════════════════
    // 任务状态汇总
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Summarize_of_no_items_is_pending()
        => FileTransferMath.SummarizeTaskState(Array.Empty<string>()).Should().Be(DistributionStates.Pending);

    /// <summary>
    /// 只要还有明细在传，总状态就是 <c>running</c> —— 哪怕别的已经完成或失败。
    /// <para>把"正在传"显示成"已完成"是最糟的错：运维会去关机器，把传输打断。</para>
    /// </summary>
    [Fact]
    public void Summarize_running_wins_over_everything_else()
    {
        FileTransferMath.SummarizeTaskState(new[]
        {
            DistributionItemStates.Completed,
            DistributionItemStates.Running,
            DistributionItemStates.Failed,
        }).Should().Be(DistributionStates.Running);
    }

    [Fact]
    public void Summarize_pending_beats_all_terminal_states()
    {
        // 有机器还在等（离线未上线）⇒ 任务没结束，绝不能因为"其余的都成了"就报完成。
        FileTransferMath.SummarizeTaskState(new[]
        {
            DistributionItemStates.Completed,
            DistributionItemStates.Completed,
            DistributionItemStates.Pending,
        }).Should().Be(DistributionStates.Pending);
    }

    [Fact]
    public void Summarize_all_completed_is_completed()
        => FileTransferMath.SummarizeTaskState(new[]
        {
            DistributionItemStates.Completed,
            DistributionItemStates.Completed,
        }).Should().Be(DistributionStates.Completed);

    [Fact]
    public void Summarize_all_failed_is_failed()
        => FileTransferMath.SummarizeTaskState(new[]
        {
            DistributionItemStates.Failed,
            DistributionItemStates.Failed,
        }).Should().Be(DistributionStates.Failed);

    [Fact]
    public void Summarize_all_canceled_is_canceled()
        => FileTransferMath.SummarizeTaskState(new[]
        {
            DistributionItemStates.Canceled,
        }).Should().Be(DistributionStates.Canceled);

    /// <summary>混合的终态（有成有败）就是 <c>partial</c> —— 需要人看一眼。</summary>
    [Fact]
    public void Summarize_mixed_terminal_states_is_partial()
    {
        FileTransferMath.SummarizeTaskState(new[]
        {
            DistributionItemStates.Completed,
            DistributionItemStates.Failed,
        }).Should().Be(DistributionStates.Partial);

        FileTransferMath.SummarizeTaskState(new[]
        {
            DistributionItemStates.Completed,
            DistributionItemStates.Canceled,
        }).Should().Be(DistributionStates.Partial);
    }

    [Fact]
    public void Summarize_treats_an_unknown_state_as_pending()
    {
        // 老服务端遇到新客户端报的未知状态：当成"还没完"，而不是崩掉或误判成完成。
        FileTransferMath.SummarizeTaskState(new[] { "something-new-from-a-future-client" })
            .Should().Be(DistributionStates.Pending);
    }
}
