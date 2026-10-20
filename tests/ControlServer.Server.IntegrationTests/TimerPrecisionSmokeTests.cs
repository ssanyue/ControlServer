using System.Diagnostics;
using ControlServer.Platform.Windows;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// <b>推流期间的定时器粒度</b>（M7.4 §11）。
///
/// <para>
/// 🔥 <b>为什么要专门有用例</b>：这段代码的两种失效都<b>不报错</b> ——
/// ① 提不上去（调用失败 / 平台不对）⇒ 帧率悄悄低一截，日志里一切都"成功"；
/// ② 只提不还（<c>timeEndPeriod</c> 漏了或少调了）⇒ 这台机器**永久**停在 1 ms 粒度上，
/// 而现象是"耗电变高"，与推流八竿子打不着。
/// </para>
///
/// <para>
/// ⚠️ <b>它验的是"生效"，不是"调过了"</b>（铁律 19）：默认粒度下 <c>Task.Delay(8)</c>
/// 实测中位 16.5 ms，提到 1 ms 之后是 9 ms 量级 —— 这个差距远大于机器抖动，
/// 所以可以直接断言方向与幅度，而不是去读返回值。
/// </para>
/// </summary>
public sealed class TimerPrecisionSmokeTests
{
    [Fact]
    public void Raising_Precision_Actually_Makes_Short_Delays_Short()
    {
        if (!OperatingSystem.IsWindows()) return;

        // ⚠️ 别人（并行的编码器用例）已经把粒度提上去时，"默认粒度"这个前提就不成立了。
        //    这时候硬断言只会得到一条与代码无关的红 —— 而假 FAIL 比不断言更坏。
        if (WindowsTimerPrecision.IsHeld) return;

        var before = MedianDelayMs(8, samples: 15);

        using (WindowsTimerPrecision.Raise())
        {
            var after = MedianDelayMs(8, samples: 15);

            // 默认粒度 15.625 ms ⇒ 请求 8 ms 实际睡 16.5 ms 左右；
            // 1 ms 粒度下是 9 ms 左右。留足余量：只要"明显变短"就算过。
            after.Should().BeLessThan(12, $"提到 1 ms 之后 Task.Delay(8) 应当落在 9 ms 量级（实测 {after:0.0} ms）");
            after.Should().BeLessThan(before * 0.85, $"默认粒度下是 {before:0.0} ms，任何一点改善都没有就说明没生效");
        }
    }

    [Fact]
    public void Holders_Are_Counted_So_Every_Raise_Can_Be_Returned()
    {
        if (!OperatingSystem.IsWindows()) return;

        var baseline = WindowsTimerPrecision.Holders;

        var first = WindowsTimerPrecision.Raise();
        var second = WindowsTimerPrecision.Raise();

        try
        {
            WindowsTimerPrecision.Holders.Should().Be(baseline + 2, "两次 Raise 就是两个持有者");

            first!.Dispose();
            first.Dispose(); // 重复 Dispose 不能把别人的份额也退掉

            WindowsTimerPrecision.Holders.Should().Be(baseline + 1, "重复归还只能扣自己那一次");
        }
        finally
        {
            first!.Dispose();
            second!.Dispose();
        }

        WindowsTimerPrecision.Holders.Should().Be(baseline, "谁提的谁还，粒度必须回到进这个用例之前的样子");
    }

    /// <summary>取 <paramref name="samples"/> 次 <see cref="Task.Delay(TimeSpan)"/> 的实际耗时中位数。</summary>
    private static double MedianDelayMs(int milliseconds, int samples)
    {
        var measured = new List<double>(samples);

        for (var i = 0; i < samples; i++)
        {
            var started = Stopwatch.GetTimestamp();

            Task.Delay(TimeSpan.FromMilliseconds(milliseconds)).GetAwaiter().GetResult();

            measured.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        measured.Sort();

        return measured[measured.Count / 2];
    }
}
