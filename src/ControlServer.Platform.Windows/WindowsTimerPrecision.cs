using System.Runtime.InteropServices;

namespace ControlServer.Platform.Windows;

/// <summary>
/// <b>推流期间把本进程的定时器分辨率提到 1 ms</b>（M7.4 §11）。
///
/// <para>
/// 🔥 <b>为什么这条是帧率的一部分，而不是"优化"</b>：Windows 默认的定时器粒度是
/// <b>15.625 ms</b>，而 <c>Task.Delay</c> / <c>Thread.Sleep</c> 都要落到下一个刻度上。
/// 本机实测（<c>%TEMP%\delay-probe</c>）：
/// </para>
/// <list type="bullet">
///   <item><c>Task.Delay(16.7)</c> 中位 17.0 ms（P90 30.8）；</item>
///   <item><c>Task.Delay(8)</c> 中位 <b>16.5 ms</b> —— 请求 8 ms，实睡 16.5 ms；</item>
///   <item><c>Task.Delay(4)</c> 中位 <b>16.1 ms</b> —— 请求 4 ms，实睡 16 ms。</item>
/// </list>
/// <para>
/// ⇒ 目标 60 fps（周期 16.7 ms）时，"把剩下的 8 ms 睡掉"这一步实际会睡掉 16 ms，
/// 于是每帧被拉长到 25–30 ms。这与编码器快不快<b>毫无关系</b>，
/// 却足以让 60 档停在 40 出头。
/// </para>
///
/// <para>
/// ⚠️ <b>只在本进程有效</b>（Windows 10 2004 起 <c>timeBeginPeriod</c> 不再污染全局），
/// 而且必须<b>成对</b>归还：<see cref="Raise"/> 的返回值 <c>Dispose</c> 一次对应一次
/// <c>timeEndPeriod</c>。托盘一直开着流却不还，会把整机（包括别人的进程）的
/// 定时器粒度永久拉高 —— 那是"我们省了 50 ms，整机多耗电"的坏事。
/// </para>
///
/// <para>
/// ⚠️ <b>失败不是错误</b>：拿不到 1 ms 粒度只是帧率低一点，不该因此开不了流。
/// 所以这里吞掉所有异常，并把"到底提上去了没有"记在 <see cref="CurrentNote"/> 里。
/// </para>
/// </summary>
internal static class WindowsTimerPrecision
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);

    private static long _holders;

    /// <summary>当前有没有人还持有 1 ms 粒度（诊断用）。</summary>
    internal static bool IsHeld => Interlocked.Read(ref _holders) > 0;

    /// <summary>当前持有者个数（用例用它钉住"成对"这条不变式）。</summary>
    internal static long Holders => Interlocked.Read(ref _holders);

    /// <summary>一句话说明"现在是不是 1 ms"，给会话日志用。</summary>
    internal static string CurrentNote => IsHeld
        ? "定时器分辨率 1 ms（本次会话持有）"
        : "定时器分辨率 系统默认（15.625 ms）";

    /// <summary>
    /// 把粒度提到 <paramref name="milliseconds"/>，返回一个"归还"用的凭据。
    /// <para>
    /// ⚠️ 返回 <c>null</c> = 没提上去（非 Windows / 调用失败）——调用方<b>不要</b>把它当失败。
    /// </para>
    /// </summary>
    internal static IDisposable? Raise(uint milliseconds = 1)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            // 返回 0 才是成功（TIMERR_NOERROR）；非 0 表示这次没生效。
            if (TimeBeginPeriod(milliseconds) != 0) return null;
        }
        catch
        {
            return null;
        }

        Interlocked.Increment(ref _holders);

        return new Scope(milliseconds);
    }

    private sealed class Scope(uint milliseconds) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;

            _released = true;

            try
            {
                TimeEndPeriod(milliseconds);
            }
            catch
            {
                // 归还失败最坏是粒度一直是 1 ms（进程退出时内核会收回去）——
                // 对"把流停下来"这件事没有任何影响，不该抛。
            }

            Interlocked.Decrement(ref _holders);
        }
    }
}
