using ControlServer.Agent.Tasks;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// <b>静止时把抓屏轮询退避下来</b>（M5.4-c② 步 8）—— 策略本身，纯函数那一层。
///
/// <para>
/// 🔥 <b>为什么要有这一层</b>：退避的判据是"桌面静止了多久"，而那个判据<b>只能真的等</b>
/// （本仓没有可注入时钟）。把"多久该退到多快"抽成纯函数之后，
/// 边界（刚好到阈值、刚好差 1 毫秒、目标帧率为 1 那种极端）可以逐条钉死，
/// 而不必让每条用例都睡 3 秒。
/// </para>
///
/// <para>
/// 🔥🔥 <b>两条铁律，都来自已经翻过的车</b>：
/// </para>
/// <list type="number">
///   <item><b>不许把"降帧"做成"停止"</b>（<c>REF-M5 §14.14</c>：静止画面曾被 Sweep 判死）。
///         退避后的周期<b>有上限</b>，所以抓屏轮询永远还在跑；</item>
///   <item><b>退避幅度必须与 <c>TargetFps</c> 成比例，不许写死"2 fps"</b>：
///         目标 60 fps 的机器退到 2 fps 与目标 12 fps 的机器退到 2 fps，
///         激进程度差 5 倍 —— 而"哪个更快"是会话参数，不是本文件的常量。</item>
/// </list>
/// </summary>
public sealed class RemoteStreamPacingTests
{
    private static TimeSpan Period(int fps) => TimeSpan.FromSeconds(1.0 / fps);

    /// <summary>桌面刚动过 ⇒ 就是目标周期，一点折扣都不打。</summary>
    [Fact]
    public void A_fresh_desktop_is_polled_at_the_target_rate()
    {
        RemoteStreamPacing.PollPeriod(12, TimeSpan.Zero).Should().Be(Period(12));
        RemoteStreamPacing.PollPeriod(30, TimeSpan.FromMilliseconds(500)).Should().Be(Period(30));
    }

    /// <summary>
    /// 刚好<b>没到</b>阈值的最后一毫秒仍是目标周期 —— 边界不能提前生效。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这条看着像抠细节，其实不然：阈值那侧写成 <c>&gt;=</c> 还是 <c>&gt;</c> 看不出区别，
    /// 但"提前一毫秒退避"意味着每一帧的间隔都可能跳一次，而画面会跟着一顿一顿的。
    /// </remarks>
    [Fact]
    public void The_threshold_is_not_crossed_early()
    {
        var justBefore = RemoteStreamPacing.IdleBackoffAfter - TimeSpan.FromMilliseconds(1);

        RemoteStreamPacing.PollPeriod(12, justBefore).Should().Be(Period(12));
        RemoteStreamPacing.PollPeriod(12, RemoteStreamPacing.IdleBackoffAfter).Should().Be(
            Period(RemoteStreamPacing.BackoffFps(12)));
    }

    /// <summary>
    /// 🔥 <b>退避幅度与目标帧率成比例</b>：12 fps ⇒ 2 fps、60 fps ⇒ 10 fps。
    /// </summary>
    /// <remarks>
    /// 这一条钉的是"<b>不写死 2 fps</b>"。写死的话 60 fps 的机器会掉到 1/30 ——
    /// 而静止期间它只需要"别把 CPU 烧了"，不需要"几乎不动"。
    /// </remarks>
    [Fact]
    public void The_backoff_is_proportional_to_the_target_rate()
    {
        RemoteStreamPacing.BackoffFps(12).Should().Be(2, "12 fps 的 1/6 正好是 2 fps");
        RemoteStreamPacing.BackoffFps(60).Should().Be(
            10,
            "60 fps 的 1/6 是 10 fps —— 与 12 fps 那台保持同一个**比例**，而不是同一个数字");
    }

    /// <summary>比例算出来低于下限 ⇒ 用下限（2 fps），不无限往下降。</summary>
    [Fact]
    public void The_floor_keeps_a_minimum_polling_rate()
    {
        // 6 fps 的 1/6 = 1 fps，低于下限 ⇒ 取 2 fps。
        RemoteStreamPacing.BackoffFps(6).Should().Be(RemoteStreamPacing.MinIdleFps);

        RemoteStreamPacing.PollPeriod(6, TimeSpan.FromSeconds(3)).Should().Be(Period(RemoteStreamPacing.MinIdleFps));
    }

    /// <summary>
    /// 🔴 <b>退避不许比目标<b>更快</b></b>（目标本来就是 1 fps 时）。
    /// </summary>
    /// <remarks>
    /// 这是"下限"与"比例"打架时唯一正确的解法：<c>Max(2, 1/6) = 2</c> 会让
    /// <b>退避后比不退避还快</b> —— 一条只会让 CPU 更忙、且完全违反本步意图的曲线。
    /// 所以退避后的帧率必须被 <c>Min(target, …)</c> 夹一遍。
    /// </remarks>
    [Fact]
    public void Backing_off_never_makes_polling_faster_than_the_target()
    {
        RemoteStreamPacing.BackoffFps(1).Should().Be(1, "目标本来就是 1 fps ⇒ 退避后不能变成 2 fps");

        RemoteStreamPacing.PollPeriod(1, TimeSpan.FromSeconds(10)).Should().Be(Period(1));
    }

    /// <summary>
    /// 🔴 <b>周期有上限</b> —— 这就是"降帧 ≠ 停止"的那一条。
    /// </summary>
    /// <remarks>
    /// 没有上限的话，目标 1 fps 的会话退避后会变成 3 秒一轮；
    /// 而"多久没上行"正是中心判活的判据（<c>§14.14</c>）。
    /// 上限保证<b>任何配置下都还在一秒以内醒一次</b>。
    /// </remarks>
    [Fact]
    public void The_polling_period_is_capped_so_it_never_stops()
    {
        foreach (var fps in new[] { 1, 2, 5, 12, 30, 60 })
        {
            var period = RemoteStreamPacing.PollPeriod(fps, TimeSpan.FromMinutes(5));

            period.Should().BeLessThanOrEqualTo(
                RemoteStreamPacing.MaxIdlePeriod,
                $"{fps} fps 退避后也必须在一秒以内醒一次 —— 抓屏轮询停了，中心就会把它判成死");

            period.Should().BeGreaterThan(
                TimeSpan.Zero,
                "周期为 0 等于把推流循环变成死转 —— 那会把被控机的 CPU 吃光");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 帧率上限：按编码器种类夹（M7.4）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>硬编路径原样放行 60</b> —— 这是"提升到 60 帧"唯一的落点。
    /// </summary>
    /// <remarks>
    /// 2026-10-18 本机实测（i5-8265U ＋ MX250，2160×1440）：DXGI 抓屏中位 3.15 ms、
    /// H.264 硬编产出 60 帧用了 482 ms（≈106 fps 余量）⇒ 60 fps 有落点。
    /// </remarks>
    [Theory]
    [InlineData(60)]
    [InlineData(30)]
    [InlineData(12)]
    public void A_hardware_encoder_keeps_the_requested_rate(int fps)
    {
        RemoteStreamPacing.ApplyEncoderCap(fps, hardwareEncode: true).Should().Be(fps);
    }

    /// <summary>
    /// 🔴 <b>软件 JPEG 那条路必须被夹住</b>：GDI 抓屏实测 47 ms/帧（≈21 fps），
    /// 不夹的话"目标 60"只会让抓屏循环满转、一帧不多，而被控机正是展厅里那台在跑业务的机器。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这条钉住的是"夹到 <see cref="RemoteStreamPacing.SoftJpegMaxFps"/> 这个数"，
    /// 不是"夹到某个差不多的小值" —— 判据漂了就变成"改了常量、用例照绿"。
    /// </remarks>
    [Theory]
    [InlineData(60)]
    [InlineData(30)]
    [InlineData(25)]
    public void A_software_jpeg_encoder_is_capped_at_what_capture_can_do(int requested)
    {
        RemoteStreamPacing.ApplyEncoderCap(requested, hardwareEncode: false)
            .Should().Be(RemoteStreamPacing.SoftJpegMaxFps);
    }

    /// <summary>低于上限的目标不许被抬高 —— 夹取只能往下。</summary>
    [Theory]
    [InlineData(20)]
    [InlineData(12)]
    [InlineData(1)]
    public void The_cap_never_raises_a_slow_request(int requested)
    {
        RemoteStreamPacing.ApplyEncoderCap(requested, hardwareEncode: false).Should().Be(requested);
    }

    /// <summary>
    /// <b>一次"没抢到新画面"只付一次钱</b>（M7.4 §12.3）。
    ///
    /// <para>
    /// 🔥 抓屏自己已经等了 15 ms（<c>AcquireNextFrame</c> 的超时），节流若再整整睡一个周期，
    /// 这一轮就是 <b>~32 ms 一帧不产</b>。60 fps 下这种错过是常态：
    /// 实测 5 秒 278 轮里 22 轮，占掉 13.7% 的窗口、约 4 fps。
    /// </para>
    /// </summary>
    [Fact]
    public void A_miss_does_not_pay_the_period_twice()
    {
        var period = RemoteStreamPacing.PollPeriod(60, TimeSpan.Zero);

        // 抓屏自己等掉 15 ms ⇒ 只该再等剩下的那一点点，而不是再来一个 16.7 ms。
        var wait = RemoteStreamPacing.DelayAfterMiss(period, TimeSpan.FromMilliseconds(15));

        wait.Should().BeLessThan(TimeSpan.FromMilliseconds(3));
        wait.Should().BeGreaterThan(TimeSpan.Zero);
    }

    /// <summary>已经超周期时不许返回负数（<c>Task.Delay</c> 会抛 <c>ArgumentOutOfRange</c>）。</summary>
    [Fact]
    public void A_round_that_already_overran_its_period_does_not_wait()
    {
        RemoteStreamPacing.DelayAfterMiss(RemoteStreamPacing.PollPeriod(60, TimeSpan.Zero), TimeSpan.FromMilliseconds(20))
            .Should().Be(TimeSpan.Zero);
    }

    /// <summary>静止退避时仍然按退避周期等 —— 修的是"重复等待"，不是"取消节流"。</summary>
    [Fact]
    public void A_backed_off_desktop_still_waits_its_backoff_period()
    {
        var backoff = RemoteStreamPacing.PollPeriod(60, TimeSpan.FromSeconds(10));

        var wait = RemoteStreamPacing.DelayAfterMiss(backoff, TimeSpan.FromMilliseconds(15));

        wait.Should().BeCloseTo(backoff - TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(1));
        wait.Should().BeGreaterThan(TimeSpan.FromMilliseconds(50));
    }
}
