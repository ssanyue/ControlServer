using System.Diagnostics;
using ControlServer.Platform.Windows;
using FluentAssertions;
using Xunit.Abstractions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// M7.6：被控端<b>唤醒锁</b>（<see cref="WindowsDisplayWakeLock"/>）。
///
/// <para>
/// 🔥 <b>这条能力治的是什么，必须先写清楚，否则用例会验错东西</b>：
/// 一台没人碰的机器静置几分钟后，系统按电源计划关掉显示器或起屏保。
/// DDA 这时仍然"抓得到" —— 抓到的是一整片纯黑，于是
/// <b>帧在流、码率正常、两端日志干干净净，画面全黑</b>；被控端有人动一下就恢复。
/// 这个形态在本仓被误判过两轮（先怀疑抓屏坏了，又怀疑编码器坏了）。
/// </para>
///
/// <para>
/// 🔴 <b>本文件验的是"锁真的拿到了、也真的放掉了"，不验"屏幕真的没黑"</b>：
/// 后者要静置到电源计划超时（分钟级），不该塞进单元测试。
/// 但"拿到锁"这件事<b>不是形式</b> —— 它的判据有两条，缺一都不算：
/// </para>
/// <list type="number">
///   <item>Windows 的 <c>SetThreadExecutionState</c> 返回非 0（API 契约）；</item>
///   <item><b>那个线程还活着</b> —— <c>ES_CONTINUOUS</c> 是<b>线程级</b>状态，
///         线程一退出锁就<b>静默消失</b>，而 <c>Acquire()</c> 早就返回过 true 了。</item>
/// </list>
///
/// <para>
/// ⚠️ 前提：Session ≠ 0（有交互桌面的会话）。服务（Session 0）里
/// <c>SetThreadExecutionState</c> 会<b>假装成功</b>，那条路本类如实拒绝，用例跳过。
/// </para>
/// </summary>
public sealed class DisplayWakeLockSmokeTests
{
    private readonly ITestOutputHelper _output;

    public DisplayWakeLockSmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Acquiring_holds_the_lock_and_releasing_gives_it_back()
    {
        if (!TryCreate(out var lockObject, out var why)) return;

        using (lockObject)
        {
            lockObject!.IsHeld.Should().BeFalse("刚建出来还没申请，不许报「已持有」");

            lockObject.Acquire(out var note).Should().BeTrue($"本机应当能做这件事：{note}");

            lockObject.IsHeld.Should().BeTrue(
                "Acquire 返回 true 之后必须真的持有 —— 判据是「持有线程还活着」，不是「我们申请过」");

            _output.WriteLine(note);

            // 🔥 在**另一个线程**上读一次：这个数会被 UI / 状态上报从别的线程问，
            //    只在申请它的那个线程上正确，等于没做。
            var seenFromElsewhere = false;

            var reader = new Thread(() => seenFromElsewhere = lockObject.IsHeld);

            reader.Start();
            reader.Join(TimeSpan.FromSeconds(2)).Should().BeTrue();
            seenFromElsewhere.Should().BeTrue("IsHeld 必须能从别的线程读到同一个事实");

            lockObject.Release(out var released).Should().BeTrue(released);

            lockObject.IsHeld.Should().BeFalse("放掉之后就是放掉了");
        }
    }

    /// <summary>
    /// 🔥 <b>幂等</b>：推流路径上很容易重复申请（重连 / 换屏重建 / 会话被接管），
    /// 第二次申请<b>不该</b>再起一个线程 —— 那会留下一个没人放得掉的孤儿，
    /// 一直替我们拦着"别关屏"，一直到进程退出。
    /// </summary>
    [Fact]
    public void Acquiring_twice_is_a_no_op_and_does_not_leak_a_thread()
    {
        if (!TryCreate(out var lockObject, out _)) return;

        using (lockObject)
        {
            lockObject!.Acquire(out _).Should().BeTrue();

            lockObject.Acquire(out var again).Should().BeTrue("第二次申请是幂等的，不是失败");

            lockObject.IsHeld.Should().BeTrue();

            _output.WriteLine(again);

            lockObject.Release(out _).Should().BeTrue();

            lockObject.IsHeld.Should().BeFalse();

            // 第二次 Release 也不能抛（退出路径上会重复走）。
            lockObject.Release(out var nothing).Should().BeTrue(nothing);

            nothing.Should().Contain("没持有", "没持有时的回答必须是「本来就没持有」，不能装成刚放掉");
        }
    }

    /// <summary>
    /// 反复申请 / 释放之后不留后台线程。
    ///
    /// <para>
    /// ⚠️ 断言的是"<b>我们自己的</b>线程没了"，不是"进程线程数不涨"：
    /// 测试宿主里线程池本来就会抖动，拿总线程数当判据是一个典型的
    /// <b>输入不受控</b>的断言（铁律 9）。
    /// </para>
    /// </summary>
    [Fact]
    public void Repeated_acquire_release_leaves_no_holder_thread_behind()
    {
        if (!TryCreate(out var lockObject, out _)) return;

        using (lockObject)
        {
            for (var round = 0; round < 10; round++)
            {
                lockObject!.Acquire(out _).Should().BeTrue($"第 {round + 1} 轮申请");
                lockObject.IsHeld.Should().BeTrue($"第 {round + 1} 轮应当持有");
                lockObject.Release(out _).Should().BeTrue($"第 {round + 1} 轮释放");
                lockObject.IsHeld.Should().BeFalse($"第 {round + 1} 轮放掉之后不该还持有");
            }

            // ⚠️ `ProcessThreadCollection` 只实现非泛型 `ICollection`，LINQ 的 `Count(谓词)`
            //    在它身上不可用 —— 先 `Cast<ProcessThread>()` 再数。
            var leftovers = Process.GetCurrentProcess().Threads
                .Cast<ProcessThread>()
                .Count(t => t.ThreadState == System.Diagnostics.ThreadState.Wait
                    && t.Id != Environment.CurrentManagedThreadId);

            // 只把数量写出来给人看：**它不是判据**（理由见方法注释）。
            _output.WriteLine($"释放十轮之后，进程里处于等待态的线程共 {leftovers} 个（仅供参考，不是判据）。");

            lockObject!.IsHeld.Should().BeFalse("十轮下来一次都不许泄漏持有状态");
        }
    }

    /// <summary>
    /// 🔴 <b>不支持的平台 / Session 0 必须如实拒绝，而不是假装成功。</b>
    ///
    /// <para>
    /// 服务（Session 0）里 <c>SetThreadExecutionState</c> 会返回非零 ——
    /// 也就是说它会<b>假装成功</b>，而用户的会话照样息屏。
    /// 如果本类照抄那个返回值，这条能力在服务模式下就变成了一句永远为真的谎话。
    /// </para>
    /// </summary>
    [Fact]
    public void A_platform_that_cannot_do_it_says_so_instead_of_pretending()
    {
        using var lockObject = new WindowsDisplayWakeLock();

        var supported = lockObject.IsSupported(out var note);

        _output.WriteLine($"IsSupported={supported}｜{note}");

        note.Should().NotBeNullOrWhiteSpace("无论支持与否都必须给出一句人话");

        if (!supported)
        {
            lockObject.Acquire(out var refused).Should().BeFalse("说不支持就不许申请成功");

            lockObject.IsHeld.Should().BeFalse();

            refused.Should().NotBeNullOrWhiteSpace();
        }
        else
        {
            // 支持的时候要能说出来"为什么可以"与"当前在哪个会话"。
            note.Should().Contain("Session");
        }
    }

    private bool TryCreate(out WindowsDisplayWakeLock? lockObject, out string why)
    {
        lockObject = null;
        why = string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("非 Windows —— 前提不成立，跳过。");

            return false;
        }

        var candidate = new WindowsDisplayWakeLock();

        if (!candidate.IsSupported(out why))
        {
            _output.WriteLine($"本机做不了这件事 —— 跳过：{why}");

            candidate.Dispose();

            return false;
        }

        lockObject = candidate;

        return true;
    }
}
