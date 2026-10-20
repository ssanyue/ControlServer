using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 配置兼容清洗 / 旧格式迁移 / 排序归一化 的**行为等价性测试**。
/// 语义基准：旧版载入配置时的处理顺序与判定条件。
/// </summary>
public sealed class ControlSettingsNormalizerTests
{
    // ═══════════════════════════════════════════════════════════
    // 兜底：null 集合
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Null_lists_are_filled_with_empty_collections()
    {
        // 模拟"旧配置 / 损坏文件"反序列化后出现 null 的情形。
        var settings = new ControlSettings
        {
            Buttons = null!,
            SubButtonSets = null!,
            AppName = null!,
            BackgroundImagePath = null!,
            DefaultButtonImage = null!,
        };

        ControlSettingsNormalizer.Normalize(settings);

        settings.Buttons.Should().NotBeNull().And.BeEmpty();
        settings.SubButtonSets.Should().NotBeNull().And.BeEmpty();
        settings.AppName.Should().Be("中控");
        settings.BackgroundImagePath.Should().BeEmpty();
        settings.DefaultButtonImage.Should().BeEmpty();
    }

    [Fact]
    public void Null_element_entries_are_removed_instead_of_crashing()
    {
        var settings = new ControlSettings
        {
            Buttons = new List<ButtonConfig> { new(), null! },
            SubButtonSets = new List<SubButtonSet> { null!, new() },
        };

        var act = () => ControlSettingsNormalizer.Normalize(settings);

        act.Should().NotThrow();
        settings.Buttons.Should().HaveCount(1);
        settings.SubButtonSets.Should().HaveCount(1);
    }

    // ═══════════════════════════════════════════════════════════
    // 迁移 ①：TimerTime → TimerTimes
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Legacy_TimerTime_is_migrated_into_TimerTimes()
    {
        var button = new ButtonConfig
        {
            Label = "灯",
            TimerTimes = new List<string>(),
            TimerTime = "09:30",
        };
        var settings = new ControlSettings { Buttons = { button } };

        var notes = ControlSettingsNormalizer.Normalize(settings);

        button.TimerTimes.Should().Equal(new[] { "09:30:00" }, "时间点统一补秒到 HH:mm:ss —— 定时器按 HH:mm:ss 精确匹配，少一段就永不触发");
        notes.Should().Contain(n => n.Contains("TimerTime") && n.Contains("09:30"));
    }

    [Fact]
    public void TimerTimes_falls_back_to_default_when_both_legacy_and_new_are_empty()
    {
        var button = new ButtonConfig
        {
            TimerTimes = new List<string>(),
            TimerTime = "",
        };
        var settings = new ControlSettings { Buttons = { button } };

        ControlSettingsNormalizer.Normalize(settings);

        button.TimerTimes.Should().Equal(new[] { "08:00:00" }, "默认值必须是 HH:mm:ss，否则默认配置的定时器静默不触发");
    }

    [Fact]
    public void Existing_TimerTimes_are_not_overwritten_by_legacy_TimerTime()
    {
        var button = new ButtonConfig
        {
            TimerTimes = new List<string> { "12:00", "18:00" },
            TimerTime = "09:30",
        };
        var settings = new ControlSettings { Buttons = { button } };

        ControlSettingsNormalizer.Normalize(settings);

        button.TimerTimes.Should().Equal(new[] { "12:00:00", "18:00:00" }, "新字段优先，旧字段只做补秒归一化");
    }

    /// <summary>
    /// ⚠️ 这是一条**有意偏离旧版**的行为，记录下来供核对（详见 M2 方案 §10.7）。
    /// <para>
    /// 旧版 <c>ButtonConfig.TimerTimes</c> 的属性初始化器是 <c>["08:00"]</c>，
    /// 因此当旧配置"只有 <c>timerTime</c>、没有 <c>timerTimes</c>"时，反序列化后集合已被填成 <c>["08:00"]</c>，
    /// 那条 <c>TimerTime → TimerTimes</c> 迁移**永远不触发** —— 用户设的 07:45 被静默丢弃。
    /// 服务版把默认值下移到清洗器，让旧值真正迁移过来。
    /// </para>
    /// </summary>
    [Fact]
    public void Legacy_TimerTime_is_honored_even_when_the_new_field_is_never_mentioned()
    {
        // 完全模拟"旧配置文件里只有 timerTime" —— 不显式给 TimerTimes 赋值。
        var button = new ButtonConfig { Label = "旧按钮", TimerTime = "07:45" };

        // 前置：新字段确实是"未提及即为空"，而不是被初始化器填了默认值。
        button.TimerTimes.Should().BeEmpty("默认值交由清洗器补，才可能看出旧值");

        ControlSettingsNormalizer.Normalize(new ControlSettings { Buttons = { button } });

        button.TimerTimes.Should().Equal(new[] { "07:45:00" }, "旧版在此场景会丢失旧值，服务版予以保留（并补秒）");
    }

    [Fact]
    public void Legacy_plan_Time_is_honored_when_Times_is_never_mentioned()
    {
        var plan = new MonthlyPlan { Name = "旧计划", Time = "06:20" };
        var button = new ButtonConfig { Label = "按钮", MonthlyPlans = { plan } };

        ControlSettingsNormalizer.Normalize(new ControlSettings { Buttons = { button } });

        plan.Times.Should().Equal("06:20:00");
    }

    // ═══════════════════════════════════════════════════════════
    // 迁移 ②：Time → Times
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Legacy_plan_Time_is_migrated_into_Times()
    {
        var plan = new MonthlyPlan { Name = "计划A", Times = new List<string>(), Time = "07:15" };
        var settings = new ControlSettings
        {
            Buttons = { new ButtonConfig { Label = "按钮", MonthlyPlans = { plan } } },
        };

        var notes = ControlSettingsNormalizer.Normalize(settings);

        plan.Times.Should().Equal("07:15:00");
        notes.Should().Contain(n => n.Contains("07:15"));
    }

    // ═══════════════════════════════════════════════════════════
    // 迁移 ③：Months + Days → MonthDays
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Legacy_Months_and_Days_expand_into_MonthDays()
    {
        var plan = new MonthlyPlan
        {
            Name = "旧计划",
            Months = new List<int> { 1, 3 },
            Days = new List<int> { 1, 15 },
            MonthDays = new List<MonthDaySchedule>(),
        };
        var settings = new ControlSettings
        {
            Buttons = { new ButtonConfig { Label = "按钮", MonthlyPlans = { plan } } },
        };

        ControlSettingsNormalizer.Normalize(settings);

        plan.MonthDays.Should().HaveCount(2);
        plan.MonthDays[0].Month.Should().Be(1);
        plan.MonthDays[0].Days.Should().Equal(1, 15);
        plan.MonthDays[1].Month.Should().Be(3);
        plan.MonthDays[1].Days.Should().Equal(1, 15);
    }

    [Fact]
    public void Existing_MonthDays_win_over_legacy_Months_and_Days()
    {
        var plan = new MonthlyPlan
        {
            Months = new List<int> { 1 },
            Days = new List<int> { 1 },
            MonthDays = new List<MonthDaySchedule> { new() { Month = 9, Days = new List<int> { 9 } } },
        };
        var settings = new ControlSettings
        {
            Buttons = { new ButtonConfig { MonthlyPlans = { plan } } },
        };

        ControlSettingsNormalizer.Normalize(settings);

        plan.MonthDays.Should().HaveCount(1);
        plan.MonthDays[0].Month.Should().Be(9);
    }

    // ═══════════════════════════════════════════════════════════
    // 子按钮：WoL 强制降级
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void SubButton_action_WoL_is_downgraded_to_UDP()
    {
        var sub = new SubButton
        {
            Label = "子1",
            Actions = new List<ButtonAction> { new() { Name = "唤醒", Protocol = TransportProtocols.Wol } },
        };
        var settings = new ControlSettings
        {
            SubButtonSets = { new SubButtonSet { Label = "集", SubButtons = { sub } } },
        };

        var notes = ControlSettingsNormalizer.Normalize(settings);

        sub.Actions[0].Protocol.Should().Be(TransportProtocols.Udp);
        notes.Should().Contain(n => n.Contains("WoL") && n.Contains("UDP"));
    }

    [Fact]
    public void Button_action_WoL_is_kept()
    {
        var button = new ButtonConfig
        {
            Label = "按钮",
            Actions = new List<ButtonAction> { new() { Name = "唤醒", Protocol = TransportProtocols.Wol } },
        };
        var settings = new ControlSettings { Buttons = { button } };

        ControlSettingsNormalizer.Normalize(settings);

        button.Actions[0].Protocol.Should().Be(TransportProtocols.Wol);
    }

    [Fact]
    public void SubButton_pairing_code_type_keeps_UTF8_default()
    {
        var sub = new SubButton { PairingCodeType = null! };
        var settings = new ControlSettings { SubButtonSets = { new SubButtonSet { SubButtons = { sub } } } };

        ControlSettingsNormalizer.Normalize(settings);

        sub.PairingCodeType.Should().Be(PairingCodeTypes.Utf8);
    }

    // ═══════════════════════════════════════════════════════════
    // 别名合并：数据类型 UTF-8 → String（2026-10-08）
    // ⚠️ 与上面那条「对接码类型保留 UTF-8」不是一回事 ——
    //    PairingCodeTypes.Utf8 是另一套枚举，**不参与**本次合并。
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("UTF-8")]
    [InlineData("utf-8")]
    [InlineData("Utf-8")]
    public void Action_data_type_UTF8_is_merged_into_String(string legacy)
    {
        var button = new ButtonConfig
        {
            Label = "按钮",
            Actions = new List<ButtonAction> { new() { Name = "发", DataType = legacy } },
        };
        var settings = new ControlSettings { Buttons = { button } };

        var notes = ControlSettingsNormalizer.Normalize(settings);

        button.Actions[0].DataType.Should().Be(
            DataTypes.String,
            "UTF-8 与 String 编码结果完全相同，读取时就该归一 —— 否则界面下拉里没有该选项会显示空白");
        notes.Should().Contain(n => n.Contains("UTF-8") && n.Contains("String"));
    }

    [Fact]
    public void SubButton_action_data_type_UTF8_is_merged_too()
    {
        var sub = new SubButton
        {
            Label = "子1",
            Actions = new List<ButtonAction> { new() { Name = "发", DataType = DataTypes.Utf8 } },
        };
        var settings = new ControlSettings
        {
            SubButtonSets = { new SubButtonSet { Label = "集", SubButtons = { sub } } },
        };

        ControlSettingsNormalizer.Normalize(settings);

        sub.Actions[0].DataType.Should().Be(DataTypes.String, "子按钮走同一个 NormalizeAction");
    }

    [Fact]
    public void The_merge_leaves_the_other_data_types_alone()
    {
        var actions = new List<ButtonAction>
        {
            new() { Name = "a", DataType = DataTypes.Hex },
            new() { Name = "b", DataType = DataTypes.Ansi },
            new() { Name = "c", DataType = DataTypes.Ascii },
            new() { Name = "d", DataType = DataTypes.CommandLibrary },
            new() { Name = "e", DataType = DataTypes.String },
        };
        var settings = new ControlSettings
        {
            Buttons = { new ButtonConfig { Label = "按钮", Actions = actions } },
        };

        var notes = ControlSettingsNormalizer.Normalize(settings);

        actions[0].DataType.Should().Be(DataTypes.Hex);
        actions[1].DataType.Should().Be(DataTypes.Ansi);
        actions[2].DataType.Should().Be(DataTypes.Ascii);
        actions[3].DataType.Should().Be(DataTypes.CommandLibrary);
        actions[4].DataType.Should().Be(DataTypes.String);
        notes.Should().NotContain(n => n.Contains("合并"), "没写 UTF-8 的动作不该被记一笔");
    }

    [Fact]
    public void The_merge_is_idempotent_and_keeps_the_encoded_bytes_identical()
    {
        var button = new ButtonConfig
        {
            Label = "按钮",
            Actions = new List<ButtonAction>
            {
                new() { Name = "发", DataType = DataTypes.Utf8, DataContent = "中" },
            },
        };
        var settings = new ControlSettings { Buttons = { button } };

        var bytesBefore = DataEncoder.Encode(DataTypes.Utf8, "中");

        var firstNotes = ControlSettingsNormalizer.Normalize(settings);
        var snapshot = ControlJson.Serialize(settings);
        var secondNotes = ControlSettingsNormalizer.Normalize(settings);

        firstNotes.Should().Contain(n => n.Contains("合并"));
        secondNotes.Should().BeEmpty("第二次清洗拿到的已是 String，不该再产生任何说明");
        ControlJson.Serialize(settings).Should().Be(snapshot, "清洗必须幂等");

        DataEncoder.Encode(button.Actions[0].DataType, "中").Should().Equal(
            bytesBefore,
            "合并只是换了个同义的名字，发出去的字节必须一模一样");
    }

    // ═══════════════════════════════════════════════════════════
    // 端口越界校正
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0, 9999)]
    [InlineData(-1, 9999)]
    [InlineData(70000, 9999)]
    [InlineData(9999, 9999)]
    [InlineData(9998, 9998)]
    public void Listen_port_out_of_range_falls_back(int input, int expected)
    {
        var settings = new ControlSettings { ListenPort = input };

        ControlSettingsNormalizer.Normalize(settings);

        settings.ListenPort.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 8080)]
    [InlineData(65536, 8080)]
    [InlineData(9000, 9000)]
    public void SubButtonSet_target_port_out_of_range_falls_back(int input, int expected)
    {
        var settings = new ControlSettings
        {
            SubButtonSets = { new SubButtonSet { Label = "集", TargetPort = input } },
        };

        ControlSettingsNormalizer.Normalize(settings);

        settings.SubButtonSets[0].TargetPort.Should().Be(expected);
    }

    // ═══════════════════════════════════════════════════════════
    // 排序归一化
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Sort_mixes_buttons_and_sets_by_shared_order()
    {
        var buttonA = new ButtonConfig { Label = "A", Order = 5 };
        var buttonB = new ButtonConfig { Label = "B", Order = 1 };
        var setS = new SubButtonSet { Label = "S", Order = 3 };

        var settings = new ControlSettings
        {
            Buttons = { buttonA, buttonB },
            SubButtonSets = { setS },
        };

        ControlSettingsNormalizer.Normalize(settings);

        // 期望顺序：B(1) → S(3) → A(5)
        buttonB.Order.Should().Be(0);
        setS.Order.Should().Be(1);
        buttonA.Order.Should().Be(2);
    }

    [Fact]
    public void Sort_puts_buttons_before_sets_when_order_ties()
    {
        // 旧配置里 Order 全是 0 —— 期望保持「先按钮后集合」的原有布局。
        var b1 = new ButtonConfig { Label = "B1", Order = 0 };
        var b2 = new ButtonConfig { Label = "B2", Order = 0 };
        var s1 = new SubButtonSet { Label = "S1", Order = 0 };

        var settings = new ControlSettings
        {
            Buttons = { b1, b2 },
            SubButtonSets = { s1 },
        };

        ControlSettingsNormalizer.Normalize(settings);

        settings.Buttons.Select(b => b.Label).Should().Equal("B1", "B2");
        settings.SubButtonSets.Select(s => s.Label).Should().Equal("S1");
        b1.Order.Should().Be(0);
        b2.Order.Should().Be(1);
        s1.Order.Should().Be(2);
    }

    [Fact]
    public void Sort_reindexes_contiguously_to_zero_based()
    {
        var settings = new ControlSettings
        {
            Buttons =
            {
                new ButtonConfig { Order = 100 },
                new ButtonConfig { Order = 200 },
                new ButtonConfig { Order = 300 },
            },
        };

        ControlSettingsNormalizer.Normalize(settings);

        settings.Buttons.Select(b => b.Order).Should().Equal(0, 1, 2);
    }

    // ═══════════════════════════════════════════════════════════
    // 幂等性
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Normalize_is_idempotent_and_second_pass_reports_no_migration()
    {
        var settings = new ControlSettings
        {
            ListenPort = 0,
            Buttons =
            {
                new ButtonConfig
                {
                    Label = "按钮",
                    Order = 0,
                    TimerTimes = new List<string>(),
                    TimerTime = "09:00",
                    MonthlyPlans =
                    {
                        new MonthlyPlan
                        {
                            Times = new List<string>(),
                            Time = "10:00",
                            Months = new List<int> { 2 },
                            Days = new List<int> { 3 },
                        },
                    },
                },
            },
        };

        var firstNotes = ControlSettingsNormalizer.Normalize(settings);
        var secondNotes = ControlSettingsNormalizer.Normalize(settings);

        firstNotes.Should().NotBeEmpty();
        secondNotes.Should().BeEmpty("第二次清洗不应再产生任何迁移说明");

        var snapshot = ControlJson.Serialize(settings);
        ControlSettingsNormalizer.Normalize(settings);
        ControlJson.Serialize(settings).Should().Be(snapshot, "清洗必须是幂等的");
    }

    // ═══════════════════════════════════════════════════════════
    // 时间点格式：HH:mm 与 HH:mm:ss 双标准的缺陷守门
    // ═══════════════════════════════════════════════════════════
    //
    // 背景（M2 方案 §10.7 第 2 个实例）：
    //   定时器用 times.Contains(now.ToString("HH:mm:ss")) 做**精确字符串匹配**，
    //   所以 "08:00" 永远匹配不上 "08:00:00" —— 定时器静默不触发，且日志里什么都没有。
    //   旧版靠编辑页保存时 TimeInput 往返顺带补秒，配置侧从未补过；服务版统一兜住。
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("7:45", "07:45:00")]
    [InlineData("07:45", "07:45:00")]
    [InlineData("07:45:00", "07:45:00")]
    [InlineData("0:0", "00:00:00")]
    [InlineData("00:00:00", "00:00:00")]
    [InlineData("23:59:59", "23:59:59")]
    [InlineData(" 7 : 5 ", "07:05:00")]
    [InlineData("08:00:0", "08:00:00")]
    public void NormalizeTimeString_pads_to_HH_mm_ss(string raw, string expected)
    {
        ControlSettingsNormalizer.NormalizeTimeString(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("08")]            // 只有小时
    [InlineData("08:00:00:00")]   // 段数过多
    [InlineData("08:00:")]        // 空秒段
    [InlineData("25:00")]         // 小时越界
    [InlineData("08:60")]         // 分钟越界
    [InlineData("08:00:60")]      // 秒越界
    [InlineData("-1:00")]         // 负数
    [InlineData("abc")]
    [InlineData("08:0x")]
    public void NormalizeTimeString_rejects_unrecognizable_values(string? raw)
    {
        ControlSettingsNormalizer.NormalizeTimeString(raw).Should().BeNull();
    }

    /// <summary>
    /// 核心守门用例：这正是不修就会导致「定时器到点没反应」的那条路径。
    /// </summary>
    [Fact]
    public void TimerTimes_written_as_HH_mm_are_padded_so_the_scheduler_can_actually_match()
    {
        var button = new ButtonConfig
        {
            Label = "开灯",
            TimerTimes = new List<string> { "08:00", "12:00" },
        };

        var notes = ControlSettingsNormalizer.Normalize(new ControlSettings { Buttons = { button } });

        button.TimerTimes.Should().Equal("08:00:00", "12:00:00");
        notes.Should().Contain(n => n.Contains("08:00") && n.Contains("08:00:00"), "补秒必须被显式告知，不做静默修改");

        // 与调度器的判定方式对齐：'HH:mm:ss' 形式的时刻必须能被 Contain 命中。
        var now = new DateTime(2026, 10, 7, 8, 0, 0);
        button.TimerTimes.Should().Contain(now.ToString("HH:mm:ss"), "这就是 TimerSchedulerService 的匹配方式");
    }

    [Fact]
    public void Monthly_plan_times_are_padded_too()
    {
        var plan = new MonthlyPlan { Name = "计划", Times = new List<string> { "23:00" } };
        var button = new ButtonConfig { Label = "按钮", MonthlyPlans = { plan } };

        ControlSettingsNormalizer.Normalize(new ControlSettings { Buttons = { button } });

        plan.Times.Should().Equal("23:00:00");
    }

    [Fact]
    public void Invalid_time_points_are_dropped_and_reported()
    {
        var button = new ButtonConfig
        {
            Label = "开灯",
            TimerTimes = new List<string> { "08:00", "25:00", "abc", "18:00" },
        };

        var notes = ControlSettingsNormalizer.Normalize(new ControlSettings { Buttons = { button } });

        button.TimerTimes.Should().Equal(new[] { "08:00:00", "18:00:00" }, "合法值保留、顺序不变");
        notes.Should().Contain(n => n.Contains("\"25:00\"") && n.Contains("无法识别"));
        notes.Should().Contain(n => n.Contains("\"abc\"") && n.Contains("无法识别"));
    }

    [Fact]
    public void Time_points_that_become_duplicates_after_padding_are_deduplicated()
    {
        // 补秒会让 "08:00" 与 "08:00:00" 撞在一起 —— 这是补秒本身引入的情形，必须一并处理。
        var button = new ButtonConfig
        {
            Label = "开灯",
            TimerTimes = new List<string> { "08:00", "08:00:00", "12:00" },
        };

        var notes = ControlSettingsNormalizer.Normalize(new ControlSettings { Buttons = { button } });

        button.TimerTimes.Should().Equal("08:00:00", "12:00:00");
        notes.Should().Contain(n => n.Contains("重复"));
    }

    [Fact]
    public void Legacy_TimerTime_that_cannot_be_parsed_falls_back_to_default_and_says_so()
    {
        var button = new ButtonConfig { Label = "旧按钮", TimerTime = "乱七八糟" };

        var notes = ControlSettingsNormalizer.Normalize(new ControlSettings { Buttons = { button } });

        button.TimerTimes.Should().Equal("08:00:00");
        notes.Should().Contain(n => n.Contains("乱七八糟") && n.Contains("无法识别"));
    }

    /// <summary>
    /// 反面守门：字段**根本不存在**时不得产出"旧值已迁移"的假说明。
    /// <para>
    /// 旧版把 <c>TimerTime</c> 的属性初始化器写成 <c>"08:00"</c>，于是迁移分支对每个按钮都成立，
    /// 用户会看到一条"TimerTime=08:00 已迁移"的说明，而配置里从来没有这个字段。
    /// 服务版把默认值下移，靠"默认留空"来区分"字段存在"与"字段缺失"。
    /// </para>
    /// </summary>
    [Fact]
    public void No_spurious_migration_note_when_the_legacy_field_is_absent()
    {
        var button = new ButtonConfig { Label = "新按钮", TimerTimes = new List<string>() };

        // 前置：旧字段默认留空，这是区分"缺失"的前提。
        button.TimerTime.Should().BeEmpty("旧字段默认必须留空，否则无法区分'字段缺失'");

        var notes = ControlSettingsNormalizer.Normalize(new ControlSettings { Buttons = { button } });

        button.TimerTimes.Should().Equal("08:00:00");
        notes.Should().NotContain(n => n.Contains("已迁移"), "字段从未出现，不得声称发生了迁移");
        notes.Should().NotContain(n => n.Contains("无法识别"), "字段从未出现，也不该报'无法识别'");
    }
}
