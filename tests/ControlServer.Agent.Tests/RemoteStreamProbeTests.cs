using System.Diagnostics;
using ControlServer.Agent.Tasks;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// <b>推流热路径的耗时探针</b>（M7.4 §6.2）—— 报告口径与窗口边界。
///
/// <para>
/// 🔥 <b>为什么这些用例值得写</b>：探针本身不改行为，所以它出错时<b>不会有任何故障</b>，
/// 只会给出一份<b>看起来很像回事的错数字</b>。而这份数字接下来要用来决定
/// "改推流循环还是改编码器" —— 它是**决策的唯一依据**。
/// 一个除错分母的探针比没有探针更坏：它会把结论指向另一层。
/// </para>
///
/// <para>
/// ⚠️ 本文件<b>不测真实 Stopwatch</b>（那要靠睡，脆且慢）：时钟由用例推进，
/// 所以"窗口到了没有""窗口清零了没有"可以逐毫秒钉死。
/// </para>
/// </summary>
public class RemoteStreamProbeTests
{
    /// <summary>把秒换成 <see cref="Stopwatch"/> 的 tick 数（喂给假时钟）。</summary>
    private static long Ticks(double seconds) => (long)(Stopwatch.Frequency * seconds);

    private static long Milliseconds(double ms) => (long)(Stopwatch.Frequency * ms / 1000.0);

    [Fact]
    public void A_disabled_probe_never_reports_and_its_mark_is_zero()
    {
        var now = 0L;
        var probe = new RemoteStreamProbe(enabled: false, () => now, TimeSpan.FromSeconds(5));

        probe.Enabled.Should().BeFalse();

        // 🔥 关着时给 `NotMarked`（负值）是**契约**：它必须与"合法刻度"分得开。
        //    早先这里用的是 0，而 0 是一个合法的时钟刻度 ⇒ 假时钟从 0 起步时
        //    第一帧会被静默不计（本条用例就是这么把那个缺陷逼出来的）。
        probe.Mark().Should().Be(RemoteStreamProbe.NotMarked);
        probe.Mark().Should().BeNegative("哨兵必须是时钟取不到的值");

        probe.AddCapture(123);
        probe.AddEncode(123);
        probe.AddWrite(123);
        probe.CountFrame(1024);

        now += Ticks(60);

        probe.TryReport(60, out var line).Should().BeFalse("关着的探针一行都不该产出");
        line.Should().BeEmpty();
    }

    [Fact]
    public void A_timestamp_of_zero_is_a_legal_start()
    {
        var now = 0L;
        var probe = new RemoteStreamProbe(enabled: true, () => now, TimeSpan.FromSeconds(5));

        var startedAt = probe.Mark();

        // 🔥 这一条钉的是"0 不能当哨兵"：假时钟（以及任何从 0 起步的刻度）里，
        //    0 就是一个真实起点。把它当"没打点"，会让这一帧静默消失，
        //    而报告上只会少一次取样 —— 看起来像"没抓到画面"。
        startedAt.Should().Be(0);

        now += Milliseconds(30);
        probe.AddCapture(startedAt);
        now += Ticks(5);

        probe.TryReport(60, out var line).Should().BeTrue();
        line.Should().Contain($"抓屏 {(30.0):0.0}ms×1");
    }

    [Fact]
    public void The_report_waits_for_the_whole_window()
    {
        var now = 0L;
        var probe = new RemoteStreamProbe(enabled: true, () => now, TimeSpan.FromSeconds(5));

        now += Ticks(4.9);

        probe.TryReport(60, out _).Should().BeFalse("差 0.1 秒也还没到窗口");

        now += Ticks(0.2);

        probe.TryReport(60, out var line).Should().BeTrue();
        line.Should().StartWith("[Stream] 探针");
    }

    [Fact]
    public void Each_report_covers_only_its_own_window()
    {
        var now = 0L;
        var probe = new RemoteStreamProbe(enabled: true, () => now, TimeSpan.FromSeconds(5));

        // 第一个窗口：1 帧、抓屏 20 ms。
        var startedAt = probe.Mark();
        now += Milliseconds(20);
        probe.AddCapture(startedAt);
        probe.CountFrame(2048);

        now += Ticks(5);

        probe.TryReport(60, out var first).Should().BeTrue();
        first.Should().Contain($"抓屏 {(20.0):0.0}ms×1");
        first.Should().Contain("出帧 1（");

        // 第二个窗口：什么都没有 —— 如果窗口没清零，这里会重复报上一窗的 1 帧。
        now += Ticks(5);

        probe.TryReport(60, out var second).Should().BeTrue();
        second.Should().Contain("出帧 0（");
        second.Should().Contain("没有任何抓屏", "空窗口必须说出来，而不是显示一行像 0 ms 的数");
    }

    [Fact]
    public void An_empty_window_says_nothing_was_sampled_instead_of_dividing_by_zero()
    {
        var line = RemoteStreamProbe.Describe(
            elapsed: TimeSpan.FromSeconds(5),
            frames: 0,
            bytes: 0,
            targetFps: 60,
            captureTicks: 0, captureSamples: 0,
            encodeTicks: 0, encodeSamples: 0,
            writeTicks: 0, writeSamples: 0);

        // ⚠️ 不许出现 NaN / Infinity：那是"分母为 0"的直接症状，
        //    而现场看到 "抓屏 NaNms" 只会以为探针坏了（事实也确实是坏了）。
        line.Should().NotContain("NaN");
        line.Should().NotContain("Infinity");
        line.Should().Contain("没有任何抓屏/编码/写管道取样");
    }

    [Fact]
    public void Each_stage_is_averaged_over_its_own_sample_count()
    {
        // 🔥 这一条钉的是分块编码时的真实现场：一次编码产出 N 块、写 N 次，而"帧"只有 1。
        //    用帧数当分母 ⇒ 写管道均耗被除以 N，看起来"写管道很快"，而它恰恰是慢的那一环。
        var line = RemoteStreamProbe.Describe(
            elapsed: TimeSpan.FromSeconds(5),
            frames: 1,
            bytes: 50 * 1024,
            targetFps: 60,
            captureTicks: Milliseconds(20), captureSamples: 1,
            encodeTicks: Milliseconds(80), encodeSamples: 1,
            writeTicks: Milliseconds(160), writeSamples: 8);

        line.Should().Contain($"抓屏 {(20.0):0.0}ms×1");
        line.Should().Contain($"编码 {(80.0):0.0}ms×1");
        line.Should().Contain($"写管道 {(20.0):0.0}ms×8", "160 ms 摊给 8 次写 = 每次 20 ms");
    }

    [Fact]
    public void The_line_carries_the_effective_target_fps_and_the_frame_rate()
    {
        // 5 秒出 42 帧 = 8.4 fps；每一帧 50 KB。
        var line = RemoteStreamProbe.Describe(
            elapsed: TimeSpan.FromSeconds(5),
            frames: 42,
            bytes: 42 * 50 * 1024,
            targetFps: 60,
            captureTicks: Milliseconds(3), captureSamples: 42,
            encodeTicks: Milliseconds(8), encodeSamples: 42,
            writeTicks: Milliseconds(41), writeSamples: 42);

        line.Should().Contain("出帧 42");
        line.Should().Contain($"{(8.4):0.0} fps");
        line.Should().Contain("目标 60", "改档到底有没有到托盘，这一句要和'目标帧率改为'那句对得上");
        line.Should().Contain($"平均 {(50):0} KB/帧");
    }

    [Fact]
    public void The_total_is_what_makes_the_missing_time_visible()
    {
        // 抓屏 3 ms ＋ 编码 8 ms ＋ 写管道 41 ms = 52 ms/帧 ⇒ 约 19 fps。
        // 🔥 这正是本轮要回答的那个问题：55 ms 里到底有没有 40 多毫秒卡在"写管道"上。
        var line = RemoteStreamProbe.Describe(
            elapsed: TimeSpan.FromSeconds(10),
            frames: 100,
            bytes: 100 * 1024,
            targetFps: 60,
            captureTicks: Milliseconds(3 * 100), captureSamples: 100,
            encodeTicks: Milliseconds(8 * 100), encodeSamples: 100,
            writeTicks: Milliseconds(41 * 100), writeSamples: 100);

        line.Should().Contain($"合计 {(52.0):0.0}ms/帧");
    }

    [Fact]
    public void A_cycle_minus_the_three_stages_is_reported_as_the_unattributed_time()
    {
        // 🔥 这条钉的是 M7.4 §12.2 那个对账：三段合计 15.4 ms，而实测 19.6 ms/帧 ——
        //    差的 4.2 ms 必须**出现在同一行里**，否则下一步只能靠猜
        //    （降分辨率 / 换编码器 / 改线程模型，三种改法完全不在同一层）。
        var line = RemoteStreamProbe.Describe(
            elapsed: TimeSpan.FromSeconds(5),
            frames: 255,
            bytes: 255 * 12 * 1024,
            targetFps: 60,
            captureTicks: Milliseconds(3.0 * 255), captureSamples: 255,
            encodeTicks: Milliseconds(12.2 * 255), encodeSamples: 255,
            writeTicks: Milliseconds(0.2 * 255), writeSamples: 255,
            cycleTicks: Milliseconds(19.6 * 255), cycleSamples: 255,
            idleTicks: Milliseconds(1.4 * 255), idleSamples: 255);

        line.Should().Contain("一轮 19.6ms");
        line.Should().Contain("三段外 4.2ms", "15.4 ms 的三段之外还有 4.2 ms 没有归属");
        line.Should().Contain("节流睡 1.4ms×255");
    }

    [Fact]
    public void A_cycle_without_any_idle_says_so_instead_of_showing_zero()
    {
        // ⚠️ 睡眠样本为 0 不是"漏记"，而是一个结论：**这一轮根本没睡** ——
        //    帧率是被工作本身压住的。这时去打"节流睡 0.0ms"会把人引向节流，方向就错了。
        var line = RemoteStreamProbe.Describe(
            elapsed: TimeSpan.FromSeconds(5),
            frames: 200,
            bytes: 200 * 12 * 1024,
            targetFps: 60,
            captureTicks: Milliseconds(3.0 * 200), captureSamples: 200,
            encodeTicks: Milliseconds(14.0 * 200), encodeSamples: 200,
            writeTicks: Milliseconds(0.2 * 200), writeSamples: 200,
            cycleTicks: Milliseconds(17.3 * 200), cycleSamples: 200);

        line.Should().Contain("节流没睡");
        line.Should().NotContain("节流睡");
    }

    [Fact]
    public void Empty_rounds_are_split_by_reason_so_the_frame_rate_can_be_explained()
    {
        // 🔥 这一条钉的是"5 秒 238 帧为什么不是 300 帧"：265 轮里 27 轮没产出帧，
        //    而这 27 轮必须**按责任分开**——无新帧/编码未就绪/抓屏失败/主动丢
        //    四种原因该去查的东西完全不同，合成一个"空转 27 次"等于没回答。
        var line = RemoteStreamProbe.Describe(
            elapsed: TimeSpan.FromSeconds(5),
            frames: 238,
            bytes: 238 * 12 * 1024,
            targetFps: 60,
            captureTicks: Milliseconds(2.4 * 238), captureSamples: 238,
            encodeTicks: Milliseconds(14.0 * 238), encodeSamples: 238,
            writeTicks: Milliseconds(0.2 * 238), writeSamples: 238,
            cycleTicks: Milliseconds(16.6 * 238), cycleSamples: 238,
            idleTicks: Milliseconds(1.3 * 148), idleSamples: 148,
            rounds: 265, skipNoNewFrame: 27, skipEncodeNotReady: 0,
            skipCaptureFailed: 0, skipDropped: 0);

        line.Should().Contain("出帧 238");
        line.Should().Contain("轮 265");
        line.Should().Contain("无新帧 27");
        line.Should().Contain("编码未就绪 0");
    }

    [Fact]
    public void A_caller_that_never_counts_rounds_gets_no_rounds_clause()
    {
        // ⚠️ "没记轮次"与"轮次为 0"是两件事：老调用方不该看到一句"轮 0（无新帧 0…）"，
        //    那会把"没接线路"伪装成"一个样本都没有"。
        var line = RemoteStreamProbe.Describe(
            elapsed: TimeSpan.FromSeconds(5),
            frames: 238,
            bytes: 238 * 1024,
            targetFps: 60,
            captureTicks: Milliseconds(2 * 238), captureSamples: 238,
            encodeTicks: Milliseconds(14 * 238), encodeSamples: 238,
            writeTicks: Milliseconds(0.2 * 238), writeSamples: 238);

        line.Should().NotContain("轮");
        line.Should().NotContain("无新帧");
    }

    [Fact]
    public void A_caller_that_never_wires_a_cycle_gets_no_pacing_clause()
    {
        // ⚠️ 老调用方（只打三段）不该凭空看到一句"节流没睡" —— 那是**没接线路**，
        //    不是"没睡"。这一条把"没数据"与"数据是 0"分开，正是本类反复强调的那条界线。
        var line = RemoteStreamProbe.Describe(
            elapsed: TimeSpan.FromSeconds(5),
            frames: 10,
            bytes: 10 * 1024,
            targetFps: 60,
            captureTicks: Milliseconds(3 * 10), captureSamples: 10,
            encodeTicks: Milliseconds(8 * 10), encodeSamples: 10,
            writeTicks: Milliseconds(1 * 10), writeSamples: 10);

        line.Should().NotContain("节流");
        line.Should().NotContain("一轮");
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("ON", true)]
    [InlineData("  yes  ", true)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData("False", false)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("随便写的", null)]
    [InlineData(null, null)]
    public void The_switch_value_is_parsed_explicitly(string? value, bool? expected)
    {
        // 🔥 `false` 与 `null` 的区分是**功能性**的：`false` 意味着"显式关掉"，
        //    它会压过标记文件；`null` 意味着"没表态"，于是标记文件说了算。
        //    把两者合成一个 bool，就会出现"文件忘了删 ⇒ 关不掉"。
        RemoteStreamProbe.Parse(value).Should().Be(expected);
    }
}
