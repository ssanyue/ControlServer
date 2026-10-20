using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 客户机配置的纯函数规则（M4.5）：<c>Merge</c> / <c>Normalize</c> / <c>Validate</c>。
///
/// <para>
/// 这三条规则<b>同时被服务端保存前校验与客户端应用前校验使用</b>，
/// 所以它们的语义不是"某一侧的实现细节"，而是两侧共同遵守的契约 ——
/// 各写一份的下场是"服务端放行、客户端拒绝执行"，界面上表现为
/// <b>"保存成功但机器没动"</b>，且两边日志都不报错。
/// </para>
///
/// <para>
/// 这一组用例的重点是把<b>容易写错的那几个语义</b>钉死：
/// <list type="bullet">
///   <item><c>Merge</c>：<b>null = 不动该块</b>，<b>空列表 = 清空该块</b>（一字之差，语义相反）；</item>
///   <item><c>Normalize</c>：只做书写统一（协议大写、补 Id、时刻归一），<b>不猜用户想干什么</b>；</item>
///   <item><c>Validate</c>：<b>返回问题清单而不是抛第一个错</b>（否则改十条任务要试十轮）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class AgentConfigRulesTests
{
    // ═══════════════════════════════════════════════════════════
    // Merge：null = 不动，空 = 清空
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Merge_onto_nothing_with_an_empty_patch_produces_the_empty_config()
    {
        var merged = AgentConfigRules.Merge(null, AllNullPatch());

        // 2026-10-13 起：新机器的起点是「UDP 通道**默认开启**（8090）+ 三张清单皆空」。
        // 默认关闭会让现场"发指令毫无反应"，是本轮明确要消灭的静默失败。
        merged.Udp.Enabled.Should().BeTrue("新机器的起点是「通道默认开启、三张清单皆空」");
        merged.Udp.Port.Should().Be(8090, "默认端口 8090（避开 HTTP 的 8080）");
        merged.SendTasks.Should().BeEmpty();
        merged.ScheduledTasks.Should().BeEmpty();
        merged.LaunchTasks.Should().BeEmpty();
    }

    /// <summary>
    /// 🔥 这条与下一条是同一个硬币的两面，也是最容易写反的地方：
    /// <c>null</c> 块<b>保留</b>原有内容，空数组<b>清空</b>。
    /// </summary>
    [Fact]
    public void Null_blocks_keep_the_existing_content()
    {
        var existing = Content(
            send: new[] { Send("keep") },
            scheduled: new[] { Scheduled("keep") },
            launch: new[] { Launch("keep") });

        var merged = AgentConfigRules.Merge(existing, AllNullPatch());

        merged.SendTasks.Should().HaveCount(1, "null = 不动那一块");
        merged.ScheduledTasks.Should().HaveCount(1);
        merged.LaunchTasks.Should().HaveCount(1);
        merged.SendTasks[0].Id.Should().Be("keep");
    }

    [Fact]
    public void Empty_arrays_clear_the_existing_content()
    {
        var existing = Content(
            send: new[] { Send("gone") },
            scheduled: new[] { Scheduled("gone") },
            launch: new[] { Launch("gone") });

        var merged = AgentConfigRules.Merge(existing, new AgentConfigPatch(
            null,
            Array.Empty<AgentSendTask>(),
            Array.Empty<AgentScheduledTask>(),
            Array.Empty<AgentLaunchTask>()));

        merged.SendTasks.Should().BeEmpty("空数组 = 清空那一块（与 null 恰好相反）");
        merged.ScheduledTasks.Should().BeEmpty();
        merged.LaunchTasks.Should().BeEmpty();
    }

    [Fact]
    public void A_non_null_block_replaces_the_existing_one_wholesale()
    {
        var existing = Content(send: new[] { Send("old-1"), Send("old-2") });

        var merged = AgentConfigRules.Merge(existing, new AgentConfigPatch(
            null,
            new[] { Send("new-only") },
            null,
            null));

        merged.SendTasks.Should().HaveCount(1, "整包替换，不是追加/合并");
        merged.SendTasks[0].Id.Should().Be("new-only");
    }

    [Fact]
    public void Udp_block_is_replaced_by_the_patch_when_present()
    {
        var existing = Content(udp: new AgentUdpChannelConfig(true, 9000, new[] { "10.0.0.1" }));

        var merged = AgentConfigRules.Merge(existing, new AgentConfigPatch(
            new AgentUdpChannelConfig(false, 7000, Array.Empty<string>()),
            null,
            null,
            null));

        merged.Udp.Enabled.Should().BeFalse();
        merged.Udp.Port.Should().Be(7000);
    }

    // ═══════════════════════════════════════════════════════════
    // Normalize：只统一书写，不猜意图
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Protocol_is_trimmed_and_upper_cased()
    {
        var normalized = AgentConfigRules.Normalize(Content(
            send: new[] { Send("a", protocol: "  udp "), Send("b", protocol: "tcp") }));

        normalized.SendTasks.Select(t => t.Protocol).Should().Equal(
            new[] { TransportProtocols.Udp, TransportProtocols.Tcp },
            "协议名统一大写是为了让名单式的协议校验能命中 —— 小写 udp 会被判成「非法协议」");
    }

    [Fact]
    public void Missing_ids_are_filled_but_existing_ones_are_kept()
    {
        var normalized = AgentConfigRules.Normalize(Content(
            send: new[] { Send(null), Send("kept") }));

        normalized.SendTasks[0].Id.Should().NotBeNullOrWhiteSpace(
            "缺 Id 的条目必须补一个 —— 否则前端「上下排序」认不出条目，拖动会错位");
        normalized.SendTasks[1].Id.Should().Be("kept", "已有的 Id 不能被改写");
    }

    [Theory]
    [InlineData("9:5", "09:05")]
    [InlineData("09:05", "09:05")]
    [InlineData("09：05", "09:05")]   // 全角冒号
    [InlineData(" 23:59 ", "23:59")]
    public void Times_are_normalized_to_two_digit_hh_mm(string raw, string expected)
    {
        AgentConfigRules.TryNormalizeTime(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("-1:00")]
    [InlineData("12")]
    [InlineData("12:00:00")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ab:cd")]
    public void Invalid_times_are_rejected(string? raw)
    {
        AgentConfigRules.TryNormalizeTime(raw).Should().BeNull();
        AgentConfigRules.IsValidTime(raw).Should().BeFalse();
    }

    [Fact]
    public void Scheduled_times_are_cleaned_sorted_and_deduplicated()
    {
        var normalized = AgentConfigRules.Normalize(Content(
            scheduled: new[]
            {
                new AgentScheduledTask(
                    "s1", "甲", "UDP", "10.0.0.1", 9000, DataTypes.String, "x",
                    new[] { "9:5", "08:00", "09:05", " 07:30 ", "nonsense", "25:00" },
                    true),
            }));

        normalized.ScheduledTasks[0].Times.Should().Equal(
            new[] { "07:30", "08:00", "09:05" },
            "非法时刻要丢弃、重复的要合并、结果要按序 —— 存储用的是同一套归一结果");
    }

    [Fact]
    public void Utf8_data_type_is_folded_into_String()
    {
        var normalized = AgentConfigRules.Normalize(Content(
            send: new[] { Send("a", dataType: DataTypes.Utf8) },
            scheduled: new[] { Scheduled("b", dataType: DataTypes.Utf8) }));

        normalized.SendTasks[0].DataType.Should().Be(
            DataTypes.String, "UTF-8 是历史别名（编码逐字节相同），一律归一成 String");
        normalized.ScheduledTasks[0].DataType.Should().Be(DataTypes.String);
    }

    [Fact]
    public void A_blank_data_type_defaults_to_String()
    {
        var normalized = AgentConfigRules.Normalize(Content(send: new[] { Send("a", dataType: "  ") }));

        normalized.SendTasks[0].DataType.Should().Be(DataTypes.String);
    }

    [Fact]
    public void Udp_blacklist_entries_are_trimmed_deduplicated_and_blanks_dropped()
    {
        var normalized = AgentConfigRules.Normalize(Content(
            udp: new AgentUdpChannelConfig(
                true, 8080,
                new[] { " 192.168.1.5 ", "", "   ", "192.168.1.5", "10.0.0.1" })));

        normalized.Udp.BlockedSourceIps.Should().Equal(
            new[] { "192.168.1.5", "10.0.0.1" },
            "空白项要去掉、重复项要去重（去重不区分大小写，IPv6 写法不统一）");
    }

    [Fact]
    public void Blank_names_become_null_so_they_are_not_stored_as_empty_strings()
    {
        var normalized = AgentConfigRules.Normalize(Content(send: new[] { Send("a", name: "   ") }));

        normalized.SendTasks[0].Name.Should().BeNull();
    }

    [Fact]
    public void Normalize_never_collapses_a_blacklist_of_only_blanks_into_null()
    {
        var normalized = AgentConfigRules.Normalize(Content(
            udp: new AgentUdpChannelConfig(true, 8080, new[] { " ", "" })));

        normalized.Udp.BlockedSourceIps.Should().NotBeNull().And.BeEmpty(
            "永远返回列表而不是 null —— 让「没配」与「配了空」在执行期是同一个意思");
    }

    // ═══════════════════════════════════════════════════════════
    // Validate：返回问题清单
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void A_clean_config_has_no_problems()
    {
        AgentConfigRules.Validate(AgentConfigContent.Empty).Should().BeEmpty();
    }

    [Fact]
    public void A_disabled_udp_channel_is_not_validated_at_all()
    {
        // 关掉的通道，端口填了什么都不该拦住保存 —— 否则用户"先关掉再改端口"这个正常动作会失败。
        var problems = AgentConfigRules.Validate(Content(
            udp: new AgentUdpChannelConfig(false, 80, new[] { "not-an-ip" })));

        problems.Should().BeEmpty();
    }

    [Fact]
    public void An_enabled_udp_channel_checks_port_range_and_blacklist_entries()
    {
        var problems = AgentConfigRules.Validate(Content(
            udp: new AgentUdpChannelConfig(true, 80, new[] { "192.168.1.1", "不是IP" })));

        problems.Should().Contain(p => p.Contains("端口"));
        problems.Should().Contain(p => p.Contains("不是IP"));
    }

    [Fact]
    public void The_blacklist_size_is_capped()
    {
        var tooMany = Enumerable.Range(0, AgentConfigDefaults.MaxBlockedSourceIps + 1)
            .Select(i => $"10.0.{i / 256}.{i % 256}")
            .ToArray();

        var problems = AgentConfigRules.Validate(Content(
            udp: new AgentUdpChannelConfig(true, 8080, tooMany)));

        problems.Should().Contain(p => p.Contains("黑名单"));
    }

    /// <summary>
    /// 🔥 一次改十条任务时，只报第一条会逼用户"改一轮、试一轮"。
    /// 这条用例专门守住"把所有问题一次说清"。
    /// </summary>
    [Fact]
    public void Multiple_problems_are_reported_in_one_pass()
    {
        var problems = AgentConfigRules.Validate(Content(send: new[]
        {
            Send("bad-protocol", protocol: "FTP"),
            Send("bad-ip", targetIp: " 10.0.0.1 "),   // 含空白
            Send("bad-port", targetPort: 0),
            Send("bad-delay", delaySeconds: -1),
        }));

        problems.Should().HaveCountGreaterThanOrEqualTo(4, "四个独立的问题要一次全报出来");
        problems.Should().Contain(p => p.Contains("协议"));
        problems.Should().Contain(p => p.Contains("目标地址"));
        problems.Should().Contain(p => p.Contains("目标端口"));
        problems.Should().Contain(p => p.Contains("延时"));
    }

    [Fact]
    public void A_send_task_rejects_the_command_library_data_type()
    {
        var problems = AgentConfigRules.Validate(Content(
            send: new[] { Send("lib", dataType: DataTypes.CommandLibrary) }));

        problems.Should().Contain(p => p.Contains("数据类型"),
            "指令库在服务端，客户机拿不到定义也就发不出字节 —— 必须在保存时就挡住");
    }

    [Fact]
    public void A_send_task_rejects_content_that_cannot_be_encoded()
    {
        var problems = AgentConfigRules.Validate(Content(
            send: new[] { Send("hex", dataType: DataTypes.Hex, data: "ABC") }));   // 奇数长度

        problems.Should().Contain(p => p.Contains("编码"));
    }

    [Fact]
    public void A_looping_send_task_needs_a_sane_interval()
    {
        var problems = AgentConfigRules.Validate(Content(
            send: new[] { Send("loop", isLoop: true, loopIntervalSeconds: 0) }));

        problems.Should().Contain(p => p.Contains("循环间隔"));
    }

    [Fact]
    public void A_non_looping_task_does_not_care_about_the_loop_interval()
    {
        var problems = AgentConfigRules.Validate(Content(
            send: new[] { Send("once", isLoop: false, loopIntervalSeconds: 0) }));

        problems.Should().BeEmpty();
    }

    [Fact]
    public void An_enabled_scheduled_task_must_have_at_least_one_time()
    {
        var enabled = AgentConfigRules.Validate(Content(
            scheduled: new[] { Scheduled("s", times: Array.Empty<string>(), enabled: true) }));

        enabled.Should().Contain(p => p.Contains("时刻"));
    }

    /// <summary>
    /// 停用中的空任务留着是"还没填完"，不该拦住整次保存 ——
    /// 否则用户"先加一条、停用着慢慢填"这个正常动作会失败。
    /// </summary>
    [Fact]
    public void A_disabled_scheduled_task_may_be_empty()
    {
        var disabled = AgentConfigRules.Validate(Content(
            scheduled: new[] { Scheduled("s", times: Array.Empty<string>(), enabled: false) }));

        disabled.Should().BeEmpty();
    }

    [Fact]
    public void A_launch_task_requires_a_program_path()
    {
        var problems = AgentConfigRules.Validate(Content(
            launch: new[] { Launch("l", programPath: "   ") }));

        problems.Should().Contain(p => p.Contains("程序路径"));
    }

    [Fact]
    public void The_task_list_size_is_capped()
    {
        var tooMany = Enumerable.Range(0, AgentConfigDefaults.MaxTasksPerList + 1)
            .Select(i => Send($"s{i}"))
            .ToArray();

        var problems = AgentConfigRules.Validate(Content(send: tooMany));

        problems.Should().Contain(p => p.Contains("最多"));
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    private static AgentConfigPatch AllNullPatch() => new(null, null, null, null);

    private static AgentConfigContent Content(
        AgentUdpChannelConfig? udp = null,
        IEnumerable<AgentSendTask>? send = null,
        IEnumerable<AgentScheduledTask>? scheduled = null,
        IEnumerable<AgentLaunchTask>? launch = null)
        => new(
            udp ?? new AgentUdpChannelConfig(false, AgentConfigDefaults.DefaultUdpPort, Array.Empty<string>()),
            send?.ToList() ?? new List<AgentSendTask>(),
            scheduled?.ToList() ?? new List<AgentScheduledTask>(),
            launch?.ToList() ?? new List<AgentLaunchTask>());

    private static AgentSendTask Send(
        string? id,
        string? name = "任务",
        string protocol = TransportProtocols.Udp,
        string targetIp = "192.168.1.10",
        int targetPort = 9000,
        string dataType = DataTypes.String,
        string data = "PAYLOAD",
        int delaySeconds = 0,
        bool isLoop = false,
        int loopIntervalSeconds = 5) => new(
            id!, name, protocol, targetIp, targetPort, dataType, data, delaySeconds, isLoop, loopIntervalSeconds, true);

    private static AgentScheduledTask Scheduled(
        string? id,
        string? name = "定时",
        string dataType = DataTypes.String,
        IReadOnlyList<string>? times = null,
        bool enabled = true) => new(
            id!, name, TransportProtocols.Udp, "192.168.1.10", 9000, dataType, "PAYLOAD",
            times ?? new[] { "08:00" }, enabled);

    private static AgentLaunchTask Launch(
        string? id,
        string? programPath = @"C:\demo\app.exe") => new(
            id!, "启动", programPath!, string.Empty, 0, true);
}
