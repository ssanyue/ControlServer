using System.Diagnostics;
using System.Runtime.InteropServices;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// Windows 上的唤醒锁（M7.6）：<c>SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED
/// | ES_SYSTEM_REQUIRED)</c> 阻止关屏 / 屏保 / 睡眠，外加"点亮一次"。
///
/// <h2>🔴 为什么必须由一个专用线程持有</h2>
///
/// <para>
/// <c>SetThreadExecutionState</c> 带 <c>ES_CONTINUOUS</c> 时，那个"持续保持"的状态是
/// <b>调用线程</b>的属性，不是进程的、也不是全局的。线程一退出（或被线程池回收复用），
/// 这个状态就<b>静默消失</b> —— 没有任何错误、没有任何回调。
/// </para>
///
/// <para>
/// 这正是最坏的一种失败方式：<c>Acquire()</c> 明明返回了 <c>true</c>，日志上也写了
/// "已阻止息屏"，可半小时后被控端还是黑了。⇒ 本实现<b>起一个只为这件事活着的线程</b>，
/// 申请成功后它就阻塞在 <see cref="ManualResetEventSlim"/> 上不退出；
/// <see cref="IsHeld"/> 也据此回答（看线程还活着，而不是看"我们曾经申请成功过"）。
/// </para>
///
/// <h2>🔴 为什么"点亮一次"要和"保持"放在一起</h2>
///
/// <para>
/// 只预防不点亮，等于对着已经黑掉的屏做无用功：会话是<b>在黑屏之后</b>才建的，
/// 那时用户看到的仍然是一块黑，只不过不会再更黑。⇒ <see cref="Acquire"/> 里先发一次
/// <c>SC_MONITORPOWER(-1)</c>，再做持续保持。
/// </para>
///
/// <para>
/// ⚠️ <b>点亮对"屏保"无效</b>：屏保按设计不接受模拟输入，也就能被真实输入退出。
/// 所以 <see cref="Acquire"/> 的 <c>note</c> 里必须带上"点亮有没有成功"，
/// 让人能区分"已经点亮了"与"发了指令但没用"。
/// </para>
///
/// <h2>⚠️ 在 Session 0（服务）里调用是无效的</h2>
///
/// <para>
/// 服务进程没有交互桌面，<c>ES_DISPLAY_REQUIRED</c> 对用户的会话不起作用，
/// 而且 API 仍然返回非零（<b>它会假装成功</b>）。本仓的推流在托盘进程里（铁律 14），
/// 所以正常路径有效；但实现必须把"我在 Session 0"如实说出来，
/// 否则这条能力会在服务模式下变成一句永远为真的谎话。
/// </para>
/// </summary>
public sealed class WindowsDisplayWakeLock : IDisplayWakeLock
{
    // ── SetThreadExecutionState 的标志位 ──
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;
    private const uint EsContinuous = 0x80000000;

    // ── 点亮显示器用 ──
    private const int WmSysCommand = 0x0112;
    private const int ScMonitorPower = 0xF170;
    private const int MonitorOn = -1;
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    private const uint SmtoAbortIfHung = 0x0002;

    /// <summary>点亮那一下最多等多久（广播是同步的，卡住等于把推流启动路径一起卡住）。</summary>
    private const uint WakeTimeoutMs = 1_000;

    /// <summary>释放时最多等持有线程多久。</summary>
    private static readonly TimeSpan ReleaseJoinTimeout = TimeSpan.FromSeconds(3);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    private readonly object _gate = new();

    private Thread? _holder;

    private ManualResetEventSlim? _stop;

    private volatile bool _disposed;

    /// <summary>最近一次申请/释放的说明（现场问"到底有没有在保持"时就靠它）。</summary>
    private volatile string _note = "还没有申请过（没人看的时候本来就不该拦着系统息屏）。";

    /// <inheritdoc />
    public bool IsHeld
    {
        get
        {
            lock (_gate)
            {
                // 🔥 判据是"线程还活着"，不是"申请过"：线程没了 = 锁已经悄悄失效了。
                return _holder is { IsAlive: true };
            }
        }
    }

    /// <summary>最近一次申请 / 释放的说明。</summary>
    public string Note => _note;

    /// <inheritdoc />
    public bool IsSupported(out string note)
    {
        if (!OperatingSystem.IsWindows())
        {
            note = "只有 Windows 有 SetThreadExecutionState —— 本平台不支持。";

            return false;
        }

        var sessionId = Process.GetCurrentProcess().SessionId;

        if (sessionId == 0)
        {
            note = "当前进程在 Session 0（服务）：SetThreadExecutionState 对用户的桌面会话不起作用，"
                + "而且 API 仍会返回成功。这条能力必须在托盘进程里用。";

            return false;
        }

        note = $"可用（Session {sessionId}）。";

        return true;
    }

    /// <inheritdoc />
    public bool Acquire(out string note)
    {
        if (_disposed)
        {
            note = "这个唤醒锁已经释放过了。";

            return false;
        }

        if (!IsSupported(out var support))
        {
            _note = support;
            note = support;

            return false;
        }

        lock (_gate)
        {
            if (_holder is { IsAlive: true })
            {
                note = _note;

                return true;
            }

            var stop = new ManualResetEventSlim(false);
            var ready = new ManualResetEventSlim(false);

            string? failure = null;

            var holder = new Thread(() =>
            {
                // 🔥 **必须在这个线程自己身上调**：ES_CONTINUOUS 是线程级状态，
                //    换个线程调（例如 Task.Run 的池线程）拿到的是一个很快就会消失的锁。
                var previous = SetThreadExecutionState(EsContinuous | EsDisplayRequired | EsSystemRequired);

                if (previous == 0)
                {
                    failure = $"SetThreadExecutionState 失败（Win32 错误 "
                        + $"{Marshal.GetLastWin32Error()}）—— 这条路在本机不可用。";

                    ready.Set();

                    return;
                }

                ready.Set();

                try
                {
                    stop.Wait();
                }
                catch (ObjectDisposedException)
                {
                    // 释放路径已经把这个事件丢掉 —— 当成"该收工了"。
                }

                // 恢复系统原本的策略：只留 ES_CONTINUOUS（= 允许按计划息屏）。
                SetThreadExecutionState(EsContinuous);
            })
            {
                IsBackground = true,
                Name = "display-wake-lock",
            };

            holder.Start();

            // ⚠️ 等线程把第一件事（申请）做完再返回：不等的话，"Acquire 成功"这句话
            //    可能在申请失败之后才被说出来 —— 那就成了假报告。
            //    超时只用于防御"线程根本没被调度"这种极端情况。
            var started = ready.Wait(TimeSpan.FromSeconds(5));

            if (!started || failure is not null)
            {
                stop.Set();
                ready.Dispose();

                if (!started)
                {
                    stop.Dispose();

                    _note = "唤醒锁线程 5 秒内没有跑起来 —— 不计为已持有。";
                    note = _note;

                    return false;
                }

                // 线程已经自己收工了，别再留着 stop。
                stop.Dispose();

                _note = failure!;
                note = _note;

                return false;
            }

            ready.Dispose();

            _holder = holder;
            _stop = stop;

            TryWakeMonitorOnce(out var wakeNote);

            _note = "已阻止系统息屏（关闭显示器 / 屏保 / 睡眠），持有线程 display-wake-lock 仍在跑。"
                + wakeNote;

            note = _note;

            // ⚠️ 点亮失败**不算申请失败**：保持这件事已经成立了，
            //    只是"原来黑着的现在还黑着"。这个区别写在 note 里，判据仍是 true。
            return true;
        }
    }

    /// <inheritdoc />
    public bool Release(out string note)
    {
        Thread? holder;
        ManualResetEventSlim? stop;

        lock (_gate)
        {
            holder = _holder;
            stop = _stop;
            _holder = null;
            _stop = null;

            if (holder is null || stop is null)
            {
                _note = "本来就没持有 —— 不用释放。";
                note = _note;

                return true;
            }

            // ⚠️ 只在锁内 Set，Join 放到锁外：Join 可能要等到线程调度，
            //    在锁里等会把 IsHeld / 另一个 Release 一起卡住。
            stop.Set();
        }

        var joined = holder.Join(ReleaseJoinTimeout);

        stop.Dispose();

        _note = joined
            ? "已释放：系统的息屏策略恢复原样（该关屏还是关屏）。"
            : $"已发出释放信号，但持有线程 {ReleaseJoinTimeout.TotalSeconds:0} 秒内没退出 —— "
                + "它是个后台线程，进程退出时会被一并带走，这里如实记下来。";

        note = _note;

        return joined;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            // 幂等：退出路径上很容易重复走一遍。
            return;
        }

        _disposed = true;

        Release(out _);
    }

    /// <summary>
    /// 点亮一次显示器（<c>SC_MONITORPOWER(-1)</c>）。
    ///
    /// <para>
    /// ⚠️ <b>它对"屏保"无效</b>：屏保要真实输入才退。这里只负责把 DPMS 关掉的屏重新点亮。
    /// </para>
    /// <para>
    /// ⚠️ 用 <c>SendMessageTimeout</c> 而不是 <c>SendMessage</c>：后者是同步广播，
    /// 任何一个顶层窗口卡住都会把我们的推流启动路径一起挂住 —— 而这条路径在会话刚建起来
    /// 的那一刻，卡住的代价是"用户点了没反应"。
    /// </para>
    /// </summary>
    private static bool TryWakeMonitorOnce(out string note)
    {
        try
        {
            var sent = SendMessageTimeout(
                HwndBroadcast,
                WmSysCommand,
                new IntPtr(ScMonitorPower),
                new IntPtr(MonitorOn),
                SmtoAbortIfHung,
                WakeTimeoutMs,
                out _);

            if (sent == IntPtr.Zero)
            {
                note = $"另外，点亮显示器那一下没有送到（Win32 错误 {Marshal.GetLastWin32Error()}）。";

                return false;
            }

            note = "另外已广播一次「点亮显示器」（对已经起来的屏保无效，屏保只认真实输入）。";

            return true;
        }
        catch (Exception ex)
        {
            note = $"另外，点亮显示器时抛出 {ex.GetType().Name}：{ex.Message}。";

            return false;
        }
    }
}
