using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 任务定时调度器（M2-D）行为回归。
/// <para>
/// 全部用例都<b>注入可推进的假时钟</b>（<see cref="TestTimeProvider"/>）并<b>手动调
/// <c>TickAsync</c></b>，不依赖真实等待 —— 定时逻辑如果用 <c>Task.Delay</c> 来测，
/// 必然既慢又不稳定。
/// </para>
/// <para>
/// 幂等性由 <see cref="FakeTimerFireStore"/> 模拟数据库唯一约束，因此「重启不重复」「多实例不重复」
/// 这两类只能靠真数据库验证的场景放在集成测试里（<c>TimerFireStoreTests</c>），
/// 这里验证的是"调度器**用**了幂等键"这一层。
/// </para>
/// </summary>
public sealed class TimerSchedulerServiceTests
{
    /// <summary>一个顺手的时间基准：2026-07-21 是<b>星期二</b>（weekday = 2）。</summary>
    private static readonly DateTimeOffset Tuesday = new(2026, 7, 21, 0, 0, 0, TimeSpan.FromHours(8));

    private static DateTimeOffset At(int hour, int minute, int second = 0)
        => new(2026, 7, 21, hour, minute, second, TimeSpan.FromHours(8));

    // ═══════════════════════════════════════════════════════════
    // 任务定时
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Daily_timer_fires_exactly_on_the_matching_second()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("08:00:00") },
        });

        await h.AdvanceToAsync(At(7, 59, 59));

        h.Scheduler.FiredCount.Should().Be(0, "还没到点");

        await h.AdvanceToAsync(At(8, 0, 0));

        h.Scheduler.FiredCount.Should().Be(1);
        h.Fires.Entries.Should().ContainSingle()
            .Which.FireTime.Should().Be("08:00:00");
    }

    [Fact]
    public async Task Daily_timer_does_not_fire_on_a_neighbouring_second()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("08:00:00") },
        });

        await h.AdvanceToAsync(At(8, 0, 0));
        h.Scheduler.FiredCount.Should().Be(1);

        await h.AdvanceToAsync(At(8, 0, 1));
        await h.AdvanceToAsync(At(8, 0, 2));

        h.Scheduler.FiredCount.Should().Be(1, "同一触发点只跑一次");
    }

    [Fact]
    public async Task Time_points_written_without_seconds_are_padded_on_read_so_they_still_fire()
    {
        // D1 修复的是「时间点格式双标准」这个静默失效缺陷。防线有两条：
        //   ① 清洗器（ControlSettingsNormalizer）在**读**与**写**两侧都补秒；
        //   ② 调度器本身用严格字符串比较（HH:mm ≠ HH:mm:ss）。
        // 这条用例钉死①：即使有人绕过编辑页、直接往文档里塞 "08:00"，
        // 从 CachedSettings 出来时也已经是 "08:00:00"，因此**不会**静默失效。
        // （更底下那层「HH:mm 与 HH:mm:ss 永不相等」由 ControlSettingsNormalizerTests 覆盖。）
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("08:00") },
        });

        h.Settings.CachedSettings!.Buttons[0].TimerTimes.Should().Equal(new[] { "08:00:00" }, "读出来就该是补过秒的");

        await h.AdvanceToAsync(At(8, 0, 0));

        h.Scheduler.FiredCount.Should().Be(1);
    }

    [Fact]
    public async Task Disabled_timer_on_a_button_is_ignored()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons =
            {
                new ButtonConfig { Label = "关了的", TimerEnabled = false, TimerTimes = { "08:00:00" } },
            },
        });

        await h.AdvanceToAsync(At(8, 0, 0));

        h.Scheduler.FiredCount.Should().Be(0);
    }

    [Fact]
    public async Task Multiple_time_points_on_one_button_all_fire()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton(new[] { "08:00:00", "12:30:15" }) },
        });

        await h.AdvanceToAsync(At(8, 0, 0));
        await h.AdvanceToAsync(At(12, 30, 15));

        h.Fires.Entries.Select(e => e.FireTime).Should().Equal("08:00:00", "12:30:15");
    }

    // ═══════════════════════════════════════════════════════════
    // 星期模式
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Specific_days_fires_when_today_is_in_the_list()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            // 2026-07-21 是星期二 → weekday = 2
            Buttons = { WeeklyButton("09:00:00", new[] { 2, 4 }) },
        });

        await h.AdvanceToAsync(At(9, 0, 0));

        h.Scheduler.FiredCount.Should().Be(1);
    }

    [Fact]
    public async Task Specific_days_does_not_fire_when_today_is_not_in_the_list()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { WeeklyButton("09:00:00", new[] { 1, 3, 5 }) },
        });

        await h.AdvanceToAsync(At(9, 0, 0));

        h.Scheduler.FiredCount.Should().Be(0, "星期二不在 [周一、周三、周五] 里");
    }

    [Fact]
    public async Task Daily_mode_ignores_the_day_list_entirely()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            // Daily 模式下 TimerDays 是死数据 —— 旧版同样不校验，这里钉死行为。
            Buttons = { DailyButton("09:00:00", days: new() { 1, 3, 5 }) },
        });

        await h.AdvanceToAsync(At(9, 0, 0));

        h.Scheduler.FiredCount.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════
    // 月度计划
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Monthly_plan_fires_on_the_configured_month_and_day()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { MonthlyButton(times: "18:00:00", monthDays: new() { new MonthDaySchedule { Month = 7, Days = { 21 } } }) },
        });

        await h.AdvanceToAsync(At(18, 0, 0));

        h.Scheduler.FiredCount.Should().Be(1);
        h.Fires.Entries.Single().Source.Should().Be("MonthlyPlan");
    }

    [Fact]
    public async Task Monthly_plan_ignores_a_date_outside_its_month_day_list()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { MonthlyButton(times: "18:00:00", monthDays: new() { new MonthDaySchedule { Month = 7, Days = { 20, 22 } } }) },
        });

        await h.AdvanceToAsync(At(18, 0, 0));

        h.Scheduler.FiredCount.Should().Be(0);
    }

    [Fact]
    public async Task Monthly_plan_falls_back_to_legacy_months_and_days()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons =
            {
                MonthlyButton(times: "18:00:00", months: new() { 7, 8 }, days: new() { 1, 21 }),
            },
        });

        await h.AdvanceToAsync(At(18, 0, 0));

        h.Scheduler.FiredCount.Should().Be(1, "旧字段 Months=[7,8] × Days=[1,21] 交叉匹配到 7/21");
    }

    [Fact]
    public async Task Monthly_plan_with_empty_legacy_lists_matches_every_day()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            // 两个旧列表都为空 = "不限"（旧版语义），因此每天都该触发。
            Buttons = { MonthlyButton(times: "18:00:00") },
        });

        await h.AdvanceToAsync(At(18, 0, 0));

        h.Scheduler.FiredCount.Should().Be(1);
    }

    [Fact]
    public async Task Disabled_monthly_plan_is_ignored()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons =
            {
                MonthlyButton(times: "18:00:00", enabled: false,
                    monthDays: new() { new MonthDaySchedule { Month = 7, Days = { 21 } } }),
            },
        });

        await h.AdvanceToAsync(At(18, 0, 0));

        h.Scheduler.FiredCount.Should().Be(0);
    }

    [Fact]
    public async Task Task_timer_and_monthly_plan_each_fire_when_they_share_a_time_point()
    {
        // 旧版行为：两者判定**互相独立**，同一时刻各触发一次。有意保留（见类注释）。
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons =
            {
                new ButtonConfig
                {
                    Label = "双份",
                    TimerEnabled = true,
                    TimerMode = TimerModes.Daily,
                    TimerTimes = { "20:00:00" },
                    MonthlyPlans =
                    {
                        new MonthlyPlan
                        {
                            Name = "月度",
                            Enabled = true,
                            Times = { "20:00:00" },
                            MonthDays = { new MonthDaySchedule { Month = 7, Days = { 21 } } },
                        },
                    },
                },
            },
        });

        await h.AdvanceToAsync(At(20, 0, 0));

        h.Scheduler.FiredCount.Should().Be(2);
        h.Fires.Entries.Select(e => e.Source).Should().BeEquivalentTo(new[] { TimerModes.Daily, "MonthlyPlan" });
    }

    // ═══════════════════════════════════════════════════════════
    // 幂等 / 回补 / 时钟异常
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task A_trigger_point_already_claimed_by_someone_else_is_skipped()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("08:00:00") },
        });

        // 模拟"另一个实例 / 上一次运行已经把这一秒占了"。
        var buttonId = h.Settings.CachedSettings!.Buttons[0].Id;
        await h.Fires.TryClaimAsync(new TimerFireEntry(
            buttonId, "2026-07-21", "08:00:00", TimerModes.Daily, At(8, 0, 0)));

        await h.AdvanceToAsync(At(8, 0, 0));

        h.Scheduler.FiredCount.Should().Be(0, "抢不到幂等键就必须跳过");
    }

    [Fact]
    public async Task A_late_tick_backfills_the_missed_seconds()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("08:00:00") },
        });

        await h.AdvanceToAsync(At(7, 59, 50));

        // 一次性跳过 30 秒（模拟 GC 停顿 / 磁盘卡顿），仍在默认 60s 追赶窗口内。
        await h.AdvanceToAsync(At(8, 0, 20));

        h.Scheduler.FiredCount.Should().Be(1, "追赶窗口内必须回补");
        h.Scheduler.MissedCount.Should().Be(0);
    }

    [Fact]
    public async Task A_gap_beyond_the_catch_up_window_is_not_backfilled()
    {
        using var h = new Harness(
            new TimerSchedulerOptions(CatchUpWindow: TimeSpan.FromSeconds(5)));

        // 触发点刻意放在**被跳过那段**里（而不是恰好落在 now 上）——
        // 落在 now 上的触发点永远会被检测到，那样就测不出"超窗口不补"。
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("07:30:00") },
        });

        await h.AdvanceToAsync(At(7, 0, 0));

        // 一小时后才回来 —— 远超 5s 追赶窗口，07:30:00 落在被裁掉的那段里。
        await h.AdvanceToAsync(At(8, 0, 0));

        h.Scheduler.FiredCount.Should().Be(0, "超窗口的触发点一律不补");
        h.Scheduler.MissedCount.Should().Be(1);
    }

    [Fact]
    public async Task A_backward_clock_jump_reanchors_without_firing()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("08:00:00") },
        });

        await h.AdvanceToAsync(At(8, 0, 0));
        h.Scheduler.FiredCount.Should().Be(1);

        // 时钟被往回拨（NTP 校正跳变 / 手工改系统时间）。
        await h.AdvanceToAsync(At(7, 0, 0));

        h.Scheduler.FiredCount.Should().Be(1, "回退时既不补也不退，只重新锚定");

        // 重新走到 08:00:00：幂等键已经占住了，因此**不会**再触发。
        await h.AdvanceToAsync(At(8, 0, 0));
        h.Scheduler.FiredCount.Should().Be(1);
    }

    [Fact]
    public async Task Sub_second_tick_intervals_still_evaluate_every_second()
    {
        // ⚠️ 回归：PeriodicTimer 的实际节拍通常**略小于** 1 秒（实测 ~0.995s）。
        //    曾经的实现拿带亚秒的 DateTimeOffset 直接 +1s 推进回补循环，
        //    于是 from+1s 永远大于 now，循环体一次都不执行 —— 定时器整体静默失效：
        //    不报错、不写日志、timer_fires 里一行记录都没有。
        //    这是靠"真实宿主 + 真实时钟"的端到端验证才暴露出来的，单元测试用整秒推进永远测不到。
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("08:00:00") },
        });

        await h.AdvanceToAsync(At(7, 59, 59) + TimeSpan.FromMilliseconds(980));

        for (var i = 0; i < 4; i++)
        {
            await h.AdvanceByAsync(TimeSpan.FromMilliseconds(995));
        }

        h.Scheduler.FiredCount.Should().Be(1, "按整秒求值后，08:00:00 必须被走到");
    }

    [Fact]
    public async Task A_sub_second_clock_jitter_does_not_lose_a_second()
    {
        // NTP 周期重校常常带来几十~几百毫秒的**负**修正。这类亚秒回退必须被吸收：
        // 既不能告警刷屏，也不能把锚点往前挪（挪了就会把中间的整秒漏掉）。
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("08:00:02") },
        });

        await h.AdvanceToAsync(At(8, 0, 1));

        // 往回抖 100ms。
        await h.AdvanceToAsync(At(8, 0, 0) + TimeSpan.FromMilliseconds(900));

        // 继续往前 —— 08:00:02 这一秒必须仍然被求值到。
        await h.AdvanceToAsync(At(8, 0, 2) + TimeSpan.FromMilliseconds(100));

        h.Scheduler.FiredCount.Should().Be(1);
    }

    [Fact]
    public async Task Tick_warms_up_the_settings_cache_instead_of_assuming_it_is_loaded()
    {
        using var h = new Harness();

        // 刻意不 PrimeAsync —— 缓存为 null。
        await h.AdvanceToAsync(At(8, 0, 0));

        h.Scheduler.FiredCount.Should().Be(0);
        h.Settings.CachedSettings.Should().NotBeNull("本轮应当顺带预热缓存");
    }

    // ═══════════════════════════════════════════════════════════
    // 漏触发扫描（只报告，不执行）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Missed_scan_reports_but_never_executes()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("07:55:00") },
        });

        // 假设服务 07:50 挂了、08:00 才起来 —— 07:55:00 这个点被错过。
        var missed = await h.Scheduler.ScanMissedAsync(At(8, 0, 0));

        missed.Should().Be(1);
        h.Scheduler.FiredCount.Should().Be(0, "扫描**绝不能**执行动作");
        h.Traffic.Entries.Should().BeEmpty("扫描过程中不该有任何网络动作");
    }

    [Fact]
    public async Task Missed_scan_does_not_report_points_that_already_fired()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("07:55:00") },
        });

        var buttonId = h.Settings.CachedSettings!.Buttons[0].Id;
        await h.Fires.TryClaimAsync(new TimerFireEntry(
            buttonId, "2026-07-21", "07:55:00", TimerModes.Daily, At(7, 55, 0)));

        var missed = await h.Scheduler.ScanMissedAsync(At(8, 0, 0));

        missed.Should().Be(0);
    }

    [Fact]
    public async Task Missed_scan_is_disabled_when_the_window_is_zero()
    {
        using var h = new Harness(
            new TimerSchedulerOptions(MissedScanWindow: TimeSpan.Zero));

        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("07:55:00") },
        });

        (await h.Scheduler.ScanMissedAsync(At(8, 0, 0))).Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════
    // 补执行（Server:Timer:BackfillMissed）
    //
    // 这一组用例钉的是"补执行的边界"，不是"能不能补"：
    // 补执行错一次的代价是**在现场真的按下按钮**（关屏、切信号源、断电），
    // 所以每条边界都必须有测试守着，而不是靠注释说服人。
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Backfill_is_off_by_default_and_executes_nothing()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("07:55:00", "开屏") },
        });

        var result = await h.Scheduler.BackfillMissedAsync(At(8, 0, 0));

        result.Executed.Should().Be(0, "默认不补执行（M2 §12 决策 #3 的原值没有被悄悄改掉）");
        h.Scheduler.FiredCount.Should().Be(0);
        h.Fires.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Backfill_really_presses_the_button_with_the_original_idempotency_key()
    {
        using var sink = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)sink.Client.LocalEndPoint!).Port;

        using var h = new Harness(new TimerSchedulerOptions(BackfillMissed: true));

        var button = DailyButton("07:55:00", "开屏");
        button.Actions.Add(new ButtonAction
        {
            Name = "开屏",
            Protocol = TransportProtocols.Udp,
            TargetIp = "127.0.0.1",
            TargetPort = port,
            DataType = DataTypes.Utf8,
            DataContent = "BACKFILL-OK",
        });

        await h.PrimeAsync(new ControlSettings { Buttons = { button } });

        // 07:50 挂、08:00 起来 —— 07:55:00 这个点被错过。
        var result = await h.Scheduler.BackfillMissedAsync(At(8, 0, 0));

        result.Detected.Should().Be(1);
        result.Executed.Should().Be(1);
        h.Scheduler.FiredCount.Should().Be(1, "补执行必须是真的执行，不是只改个计数器");
        h.Traffic.Entries.Should().ContainSingle("动作真的发出去了");

        var entry = h.Fires.Entries.Should().ContainSingle().Subject;
        entry.FireTime.Should().Be("07:55:00", "幂等键用**原定时刻**，否则重启后又会被当成新点再补一次");
        entry.FiredAt.Should().Be(At(8, 0, 0),
            "FiredAt 写实际执行时刻 ⇒ timer_fires 里 FiredAt ≠ FireTime 就是「这条是补的」的判据");
    }

    [Fact]
    public async Task Backfill_takes_only_the_latest_point_of_one_trigger()
    {
        using var h = new Harness(new TimerSchedulerOptions(BackfillMissed: true));
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton(new[] { "07:52:00", "07:55:00" }, "灯") },
        });

        var result = await h.Scheduler.BackfillMissedAsync(At(8, 0, 0));

        result.Detected.Should().Be(2);
        result.Executed.Should().Be(1, "同一触发点补两次＝把灯连开两次");
        result.CollapsedDuplicates.Should().Be(1);
        h.Fires.Entries.Should().ContainSingle().Which.FireTime.Should().Be("07:55:00",
            "补的是「最终状态」，取最近那一次");
    }

    [Fact]
    public async Task Backfill_stops_at_the_configured_cap()
    {
        using var h = new Harness(new TimerSchedulerOptions(BackfillMissed: true, MaxBackfillCount: 2));
        await h.PrimeAsync(new ControlSettings
        {
            Buttons =
            {
                DailyButton("07:51:00", "甲"),
                DailyButton("07:52:00", "乙"),
                DailyButton("07:53:00", "丙"),
            },
        });

        var result = await h.Scheduler.BackfillMissedAsync(At(8, 0, 0));

        result.Detected.Should().Be(3);
        result.Executed.Should().Be(2);
        result.OverCap.Should().Be(1);

        h.Fires.Entries.Select(e => e.FireTime)
            .Should().BeEquivalentTo(new[] { "07:51:00", "07:52:00" },
                "上限按时间顺序截取：先到的先补，晚的放弃");
    }

    [Fact]
    public async Task Backfill_ignores_points_outside_the_scan_window()
    {
        using var h = new Harness(new TimerSchedulerOptions(
            BackfillMissed: true,
            MissedScanWindow: TimeSpan.FromMinutes(10)));

        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("05:00:00", "关屏") },
        });

        var result = await h.Scheduler.BackfillMissedAsync(At(8, 0, 0));

        result.Detected.Should().Be(0,
            "停机三小时后再补「关屏」＝把刚开好的屏关掉；窗口外的一律只告警");
        h.Traffic.Entries.Should().BeEmpty();
        h.Fires.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Backfill_skips_points_that_already_have_a_record()
    {
        using var h = new Harness(new TimerSchedulerOptions(BackfillMissed: true));
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("07:55:00") },
        });

        var buttonId = h.Settings.CachedSettings!.Buttons[0].Id;
        await h.Fires.TryClaimAsync(new TimerFireEntry(
            buttonId, "2026-07-21", "07:55:00", TimerModes.Daily, At(7, 55, 0)));

        var result = await h.Scheduler.BackfillMissedAsync(At(8, 0, 0));

        result.Detected.Should().Be(0);
        h.Scheduler.FiredCount.Should().Be(0);
    }

    [Fact]
    public async Task Backfill_does_not_fire_the_same_point_twice_across_restarts()
    {
        using var h = new Harness(new TimerSchedulerOptions(BackfillMissed: true));
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("07:55:00") },
        });

        (await h.Scheduler.BackfillMissedAsync(At(8, 0, 0))).Executed.Should().Be(1);

        // 模拟"补完立刻又重启一次"：同一个窗口内再扫一遍。
        var second = await h.Scheduler.BackfillMissedAsync(At(8, 0, 1));

        second.Detected.Should().Be(0, "补执行自己写下的记录必须能挡住第二次补");
        h.Scheduler.FiredCount.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════
    // 保留期裁剪
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Records_older_than_the_retention_window_are_pruned()
    {
        using var h = new Harness(new TimerSchedulerOptions(RetentionDays: 30));
        await h.PrimeAsync(new ControlSettings { Buttons = { DailyButton("08:00:00") } });

        var buttonId = h.Settings.CachedSettings!.Buttons[0].Id;
        await h.Fires.TryClaimAsync(new TimerFireEntry(
            buttonId, "2026-06-01", "08:00:00", TimerModes.Daily, Tuesday));

        await h.AdvanceToAsync(At(8, 0, 0));

        h.Fires.Entries.Should().NotContain(e => e.FireDate == "2026-06-01", "2026-06-01 早于 2026-06-21 的保留线");
    }

    [Fact]
    public async Task Pruning_runs_only_once_per_day()
    {
        using var h = new Harness(new TimerSchedulerOptions(RetentionDays: 30));
        await h.PrimeAsync(new ControlSettings { Buttons = { DailyButton("08:00:00") } });

        await h.AdvanceToAsync(At(8, 0, 0));
        await h.AdvanceToAsync(At(8, 0, 1));
        await h.AdvanceToAsync(At(8, 0, 2));

        // 只断言"不重复裁剪"这一行为：同一天内 PruneAsync 只该被调一次。
        h.Fires.PruneCalls.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════
    // 真的执行了动作
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task A_fired_timer_actually_sends_the_buttons_actions()
    {
        using var sink = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)sink.Client.LocalEndPoint!).Port;

        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons =
            {
                new ButtonConfig
                {
                    Label = "开灯",
                    TimerEnabled = true,
                    TimerMode = TimerModes.Daily,
                    TimerTimes = { "08:00:00" },
                    Actions =
                    {
                        new ButtonAction
                        {
                            Name = "点灯",
                            Protocol = TransportProtocols.Udp,
                            TargetIp = "127.0.0.1",
                            TargetPort = port,
                            DataType = DataTypes.Utf8,
                            DataContent = "LIGHT-ON",
                        },
                    },
                },
            },
        });

        await h.AdvanceToAsync(At(8, 0, 0));

        var received = await sink.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Encoding.UTF8.GetString(received.Buffer).Should().Be("LIGHT-ON");

        h.Traffic.Entries.Should().ContainSingle()
            .Which.Success.Should().BeTrue();
    }

    [Fact]
    public async Task A_monthly_plan_executes_the_owning_buttons_actions()
    {
        // 月度计划只决定"何时"，不携带自己的动作 —— 执行的是**所属按钮**的 Actions。
        using var sink = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)sink.Client.LocalEndPoint!).Port;

        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons =
            {
                new ButtonConfig
                {
                    Label = "计划按钮",
                    Actions =
                    {
                        new ButtonAction
                        {
                            Name = "计划动作",
                            Protocol = TransportProtocols.Udp,
                            TargetIp = "127.0.0.1",
                            TargetPort = port,
                            DataType = DataTypes.Utf8,
                            DataContent = "FROM-PLAN",
                        },
                    },
                    MonthlyPlans =
                    {
                        new MonthlyPlan
                        {
                            Name = "月度",
                            Times = { "18:00:00" },
                            MonthDays = { new MonthDaySchedule { Month = 7, Days = { 21 } } },
                        },
                    },
                },
            },
        });

        await h.AdvanceToAsync(At(18, 0, 0));

        var received = await sink.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Encoding.UTF8.GetString(received.Buffer).Should().Be("FROM-PLAN");
    }

    // ═══════════════════════════════════════════════════════════
    // 生效触发点清单
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Trigger_list_covers_task_timers_and_monthly_plans()
    {
        using var h = new Harness();
        h.Seed(new ControlSettings
        {
            Buttons =
            {
                DailyButton(new[] { "08:00:00", "09:00:00" }, label: "甲"),
                MonthlyButton(label: "乙", times: "18:00:00",
                    monthDays: new() { new MonthDaySchedule { Month = 7, Days = { 21 } } }),
            },
        });

        // DescribeActiveTriggers 走缓存快照，先读一次把它填上。
        await h.Settings.GetAsync();

        var list = h.Scheduler.DescribeActiveTriggers();

        list.Should().HaveCount(3);
        list.Count(t => t.Kind == "MonthlyPlan").Should().Be(1);
        list.Single(t => t.Kind == "MonthlyPlan").MonthDays.Should().Equal("7月21日");
    }

    [Fact]
    public async Task Trigger_list_only_includes_specific_days_when_the_mode_says_so()
    {
        using var h = new Harness();
        h.Seed(new ControlSettings
        {
            Buttons =
            {
                DailyButton("08:00:00", label: "甲"),
                WeeklyButton("09:00:00", new[] { 2, 4 }, label: "乙"),
            },
        });

        await h.Settings.GetAsync();

        var list = h.Scheduler.DescribeActiveTriggers();

        list.Single(t => t.ButtonLabel == "甲").Days.Should().BeEmpty("Daily 模式没有星期条件");
        list.Single(t => t.ButtonLabel == "乙").Days.Should().Equal(2, 4);
    }

    [Fact]
    public void Trigger_list_is_empty_when_settings_are_not_loaded()
    {
        using var h = new Harness();

        h.Scheduler.DescribeActiveTriggers().Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════
    // 关闭开关
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task A_disabled_scheduler_exits_immediately_without_scanning()
    {
        using var h = new Harness(new TimerSchedulerOptions(Enabled: false));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await h.Scheduler.RunAsync(cts.Token);

        cts.IsCancellationRequested.Should().BeFalse("应当自己退出，而不是等到超时被取消");
        h.Scheduler.FiredCount.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════
    // M3-S3：授权闸门停摆
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task A_blocked_scheduler_skips_firing_entirely()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("08:00:00") },
        });

        h.Scheduler.IsCoreBlocked = true;

        await h.AdvanceToAsync(At(7, 59, 59));
        await h.AdvanceToAsync(At(8, 0, 0));

        h.Scheduler.FiredCount.Should().Be(0, "停摆期间一秒都不许触发");
        h.Fires.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Unblocking_resumes_firing_without_backfilling_the_blocked_window()
    {
        // 停摆期间错过的触发点**不补**：锚点被一路前移，解除后只从"此刻"往后算。
        // 这与"进程内追赶窗口"是两回事 —— 后者是 tick 迟到，前者是刻意的策略。
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton(new[] { "08:00:00", "08:05:00" }) },
        });

        h.Scheduler.IsCoreBlocked = true;
        await h.AdvanceToAsync(At(7, 59, 59));
        await h.AdvanceToAsync(At(8, 0, 0));

        h.Scheduler.IsCoreBlocked = false;
        await h.AdvanceToAsync(At(8, 5, 0));

        h.Scheduler.FiredCount.Should().Be(1, "解除停摆后必须立刻恢复，且只补跑当前这一拍");
        h.Fires.Entries.Should().ContainSingle()
            .Which.FireTime.Should().Be("08:05:00", "停摆期间错过的 08:00:00 不该被回补");
    }

    [Fact]
    public async Task Blocking_mid_flight_stops_the_next_trigger()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton(new[] { "08:00:00", "08:05:00" }) },
        });

        await h.AdvanceToAsync(At(8, 0, 0));
        h.Scheduler.FiredCount.Should().Be(1, "停摆之前一切照旧");

        h.Scheduler.IsCoreBlocked = true;
        await h.AdvanceToAsync(At(8, 5, 0));

        h.Scheduler.FiredCount.Should().Be(1, "停摆生效后不得再触发");
    }

    [Fact]
    public async Task Blocked_ticks_are_not_counted_as_missed_triggers()
    {
        // 停摆是**主动**不触发，不是"来不及触发"，因此不该混进 MissedCount
        // （那个计数对应的是告警文案"服务停机期间错过 N 个触发点"）。
        // 混进去会让现场看到一条与授权无关的误导告警。
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton("08:00:00") },
        });

        h.Scheduler.IsCoreBlocked = true;
        await h.AdvanceToAsync(At(8, 0, 0));
        await h.AdvanceToAsync(At(8, 0, 1));

        h.Fires.Entries.Should().BeEmpty();
        h.Scheduler.MissedCount.Should().Be(0, "停摆不是'漏触发'，不该计入告警计数");
    }

    // ═══════════════════════════════════════════════════════════
    // 夹具
    // ═══════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════
    // M2-E4：手动触发
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Fire_now_executes_the_buttons_actions_and_records_a_manual_entry()
    {
        using var sink = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)sink.Client.LocalEndPoint!).Port;

        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons =
            {
                new ButtonConfig
                {
                    Id = "b-manual",
                    Label = "手动按钮",
                    Actions =
                    {
                        new ButtonAction
                        {
                            Name = "发",
                            Protocol = TransportProtocols.Udp,
                            TargetIp = "127.0.0.1",
                            TargetPort = port,
                            DataType = DataTypes.Utf8,
                            DataContent = "MANUAL-OK",
                        },
                    },
                },
            },
        });

        var outcome = await h.Scheduler.FireNowAsync("b-manual");

        outcome.Status.Should().Be(TimerManualFireStatus.Fired);
        outcome.Ok.Should().BeTrue(outcome.Message);

        var received = await sink.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Encoding.UTF8.GetString(received.Buffer).Should().Be("MANUAL-OK");

        h.Fires.Entries.Should().ContainSingle()
            .Which.Source.Should().Be(TimerSchedulerService.ManualSource, "手动触发要能与定时触发区分开");
        h.Scheduler.FiredCount.Should().Be(1);
    }

    [Fact]
    public async Task Fire_now_is_blocked_by_the_same_second_idempotency_gate()
    {
        // 手动触发走的是**同一张 timer_fires 表**、同一个唯一约束 —— 这正是"不绕开闸门"的含义。
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings { Buttons = { new ButtonConfig { Id = "b1", Label = "甲" } } });

        var first = await h.Scheduler.FireNowAsync("b1");
        var second = await h.Scheduler.FireNowAsync("b1");

        first.Status.Should().Be(TimerManualFireStatus.Fired);
        second.Status.Should().Be(TimerManualFireStatus.AlreadyFired, "同一秒连点只有第一次生效");
        h.Scheduler.FiredCount.Should().Be(1);
        h.Fires.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Fire_now_returns_not_found_for_an_unknown_button()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings { Buttons = { new ButtonConfig { Id = "b1", Label = "甲" } } });

        var outcome = await h.Scheduler.FireNowAsync("根本没有这个按钮");

        outcome.Status.Should().Be(TimerManualFireStatus.NotFound);
        h.Fires.Entries.Should().BeEmpty("找不到就一个字节都不该写进去");
    }

    [Fact]
    public async Task Fire_now_returns_not_found_for_an_unknown_plan_id()
    {
        // 只验按钮是不够的：随手编一个 planId 也能触发按钮动作就成了越权的小门。
        using var h = new Harness();
        var button = MonthlyButton(label: "甲");
        button.Id = "b1";
        await h.PrimeAsync(new ControlSettings { Buttons = { button } });

        var outcome = await h.Scheduler.FireNowAsync("b1:根本没有这个计划");

        outcome.Status.Should().Be(TimerManualFireStatus.NotFound);
    }

    [Fact]
    public async Task Fire_now_accepts_a_monthly_plan_ref_id()
    {
        using var sink = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)sink.Client.LocalEndPoint!).Port;

        using var h = new Harness();
        var button = MonthlyButton(label: "甲");
        button.Id = "b1";
        button.Actions.Add(new ButtonAction
        {
            Name = "发",
            Protocol = TransportProtocols.Udp,
            TargetIp = "127.0.0.1",
            TargetPort = port,
            DataType = DataTypes.Utf8,
            DataContent = "PLAN-OK",
        });
        await h.PrimeAsync(new ControlSettings { Buttons = { button } });

        var planId = button.MonthlyPlans[0].Id;
        var outcome = await h.Scheduler.FireNowAsync($"b1:{planId}");

        outcome.Status.Should().Be(TimerManualFireStatus.Fired);

        // 月度计划执行的是**所属按钮**的动作（与调度器一致）。
        var received = await sink.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Encoding.UTF8.GetString(received.Buffer).Should().Be("PLAN-OK");
    }

    [Fact]
    public async Task Fire_now_does_not_require_the_timer_to_be_enabled()
    {
        // 手动触发是调试通道：按钮没开定时也应该能点一下看能不能通。
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { new ButtonConfig { Id = "b1", Label = "没开定时的按钮", TimerEnabled = false } },
        });

        var outcome = await h.Scheduler.FireNowAsync("b1");

        outcome.Status.Should().Be(TimerManualFireStatus.Fired);
    }

    // ═══════════════════════════════════════════════════════════
    // M2-E4：诊断（下一次触发 / 今天是否适用）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Next_fire_at_picks_the_closest_future_point_today()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons = { DailyButton(new[] { "08:00:00", "23:00:00" }, label: "甲") },
        });

        h.Clock.SetUtcNow(At(9, 0, 0));

        h.Scheduler.FindNextFireAt().Should().Be(
            new DateTimeOffset(2026, 7, 21, 23, 0, 0, TimeSpan.FromHours(8)),
            "08:00 已过，今天下一个点是 23:00");
    }

    [Fact]
    public async Task Next_fire_at_rolls_over_to_tomorrow_when_today_is_done()
    {
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings { Buttons = { DailyButton("08:00:00", label: "甲") } });

        h.Clock.SetUtcNow(At(9, 0, 0));

        h.Scheduler.FindNextFireAt().Should().Be(
            new DateTimeOffset(2026, 7, 22, 8, 0, 0, TimeSpan.FromHours(8)),
            "今天的 08:00 已过，下一次是明天 08:00");
    }

    [Fact]
    public async Task Next_fire_at_is_null_when_nothing_is_configured()
    {
        // null 是**诚实的"算不出来"** —— 前端应显示「—」，而不是"永不触发"。
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings());

        h.Scheduler.FindNextFireAt().Should().BeNull();
    }

    [Fact]
    public async Task Fires_today_follows_the_same_rules_as_the_scheduler()
    {
        // 这个用例守的是"前端显示"与"调度器实际"的一致性：
        // 若哪天调度器改了判定而 FiresToday 没跟上，界面会显示"今天会跑"但实际不跑。
        using var h = new Harness();
        await h.PrimeAsync(new ControlSettings
        {
            Buttons =
            {
                DailyButton("08:00:00", label: "每天"),
                // 2026-07-21 是星期二 → weekday = 2
                WeeklyButton("09:00:00", new[] { 2 }, label: "周二"),
                WeeklyButton("10:00:00", new[] { 3 }, label: "周三"),
                MonthlyButton(label: "月度", times: "18:00:00",
                    monthDays: new() { new MonthDaySchedule { Month = 7, Days = { 21 } } }),
            },
        });

        var now = At(12, 0, 0);
        var byLabel = h.Scheduler.DescribeActiveTriggers()
            .ToDictionary(t => t.ButtonLabel, t => h.Scheduler.FiresToday(t, now));

        byLabel["每天"].Should().BeTrue("Daily 模式不校验星期");
        byLabel["周二"].Should().BeTrue();
        byLabel["周三"].Should().BeFalse("今天是星期二");
        byLabel["月度"].Should().BeTrue("7 月 21 日在计划的日期里");
    }

    /// <summary>造一个「每日定时」的按钮。<c>days</c> 在 Daily 模式下是死数据，仅用于证明"确实没被校验"。</summary>
    private static ButtonConfig DailyButton(string time, string label = "按钮", List<int>? days = null)
        => TimedButton(new[] { time }, TimerModes.Daily, days ?? DefaultWeekdays, label);

    /// <summary>造一个「每日定时 + 多个时间点」的按钮。</summary>
    private static ButtonConfig DailyButton(IEnumerable<string> times, string label = "按钮", List<int>? days = null)
        => TimedButton(times, TimerModes.Daily, days ?? DefaultWeekdays, label);

    /// <summary>造一个「指定星期」模式的按钮。</summary>
    private static ButtonConfig WeeklyButton(string time, IEnumerable<int> days, string label = "按钮")
        => TimedButton(new[] { time }, TimerModes.SpecificDays, days.ToList(), label);

    private static List<int> DefaultWeekdays => new() { 1, 2, 3, 4, 5 };

    private static ButtonConfig TimedButton(
        IEnumerable<string> times,
        string mode,
        List<int> days,
        string label)
        => new()
        {
            Label = label,
            TimerEnabled = true,
            TimerMode = mode,
            TimerTimes = times.ToList(),
            TimerDays = days,
        };

    private static ButtonConfig MonthlyButton(
        string label = "按钮",
        string times = "18:00:00",
        bool enabled = true,
        List<MonthDaySchedule>? monthDays = null,
        List<int>? months = null,
        List<int>? days = null)
        => new()
        {
            Label = label,
            MonthlyPlans =
            {
                new MonthlyPlan
                {
                    Name = "月度计划",
                    Enabled = enabled,
                    Times = { times },
                    MonthDays = monthDays ?? new List<MonthDaySchedule>(),
                    Months = months ?? new List<int>(),
                    Days = days ?? new List<int>(),
                },
            },
        };

    private sealed class Harness : IDisposable
    {
        private readonly TempDataDirectory _data = new();
        private readonly InMemoryConfigStore _store = new();

        public Harness(TimerSchedulerOptions? options = null)
        {
            Log = new LogService(_data, capacity: 500, flushIntervalMs: 60_000);
            Traffic = new RecordingTrafficLog();
            Settings = new ControlSettingsService(_store, Log);
            Clock = new TestTimeProvider(Tuesday);
            Fires = new FakeTimerFireStore();

            var execution = new ActionExecutionService(
                new NetworkService(), Traffic, new FakeCommandLibrary(), new NullNotifier(), Log);

            Scheduler = new TimerSchedulerService(
                Settings, execution, Fires, Log, options ?? new TimerSchedulerOptions(), Clock);
        }

        public LogService Log { get; }

        public RecordingTrafficLog Traffic { get; }

        public ControlSettingsService Settings { get; }

        public TestTimeProvider Clock { get; }

        public FakeTimerFireStore Fires { get; }

        public TimerSchedulerService Scheduler { get; }

        /// <summary>直接播种一份配置文档（模拟"编辑页已保存"）。</summary>
        public void Seed(ControlSettings settings)
            => _store.Seed(ConfigKeys.Settings, ControlJson.Serialize(settings), version: _store.NextVersion);

        /// <summary>播种并预热缓存（<c>CachedSettings</c> 非 null），然后可选地跑一次 tick 锚定。</summary>
        public async Task PrimeAsync(ControlSettings settings)
        {
            Seed(settings);
            await Settings.GetAsync();
        }

        /// <summary>把时钟拨到指定时刻并跑一次 tick（含迟到回补）。</summary>
        public async Task AdvanceToAsync(DateTimeOffset moment)
        {
            Clock.SetUtcNow(moment);
            await Scheduler.TickAsync();
        }

        /// <summary>把时钟往后推一小段并跑一次 tick（用于模拟真实的、略带漂移的节拍）。</summary>
        public async Task AdvanceByAsync(TimeSpan delta)
        {
            Clock.Advance(delta);
            await Scheduler.TickAsync();
        }

        public void Dispose()
        {
            Log.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _data.Dispose();
        }
    }
}
