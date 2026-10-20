using System.Diagnostics;

namespace ControlServer.LoadTest;

/// <summary>
/// 每秒采一次<b>中心进程</b>的 CPU / 内存 / 线程数。
///
/// <para>
/// 🔥 <b>为什么按进程采，而不是看整机 CPU</b>：压测工具与中心通常跑在同一台机器上，
/// 整机 CPU 里混着压测工具自己那 100 个连接的开销。报告要说的是
/// "中心这一侧吃了多少"，那就只能是中心那个进程的数。
/// </para>
///
/// <para>
/// <b>口径</b>：CPU% = 本秒内该进程消耗的 CPU 时间 ÷ 墙上时间 ÷ 逻辑核数 × 100，
/// 也就是<b>占整机</b>的百分比（4 核跑满 = 100%，不是 400%）。
/// </para>
///
/// <para>
/// ⚠️ 它拿不到的：GC 次数、GC 暂停、线程池队列长度。那些要走 <c>dotnet-counters</c>
/// 或 EventPipe，本工具不带 —— 报告里会明确写"GC 未采集"，不假装有。
/// </para>
/// </summary>
public sealed class ServerProbe : IDisposable
{
    private readonly Process _process;
    private readonly Metrics _metrics;
    private readonly Stopwatch _clock;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _processName;
    private Task? _loop;

    private TimeSpan _lastCpu;
    private DateTime _lastAt;
    private bool _primed;

    private ServerProbe(Process process, Metrics metrics, Stopwatch clock)
    {
        _process = process;
        _metrics = metrics;
        _clock = clock;

        // 🔥 进程名**必须在构造时取一次就存下来**：它在收尾后要进报告，
        //    而那时 `Process.Dispose()` 已经调过 —— 再访问 `_process.ProcessName`
        //    会抛 `InvalidOperationException: No process is associated with this object`。
        //    这个坑第一次跑就踩到了：报告一行没写，整轮数据全丢。
        try
        {
            _processName = process.ProcessName;
        }
        catch
        {
            _processName = "ControlServer.Server";
        }
    }

    /// <summary>采到的进程名（报告里写清楚到底采的是谁）。</summary>
    public string ProcessName => _processName;

    /// <summary>该进程的工作集峰值（MB），收尾时读一次。</summary>
    public double PeakWorkingSetMb { get; private set; }

    /// <summary>
    /// 定位中心进程：优先用显式 PID（脚本启动服务时拿到的最准），
    /// 否则按进程名找<b>启动时间最晚</b>的那个 —— 开发机上常常还挂着另一个实例，
    /// 采错进程会让整份报告的数字变成另一个服务的。
    /// </summary>
    public static ServerProbe? TryStart(int pid, Metrics metrics, Stopwatch clock, out string? warning)
    {
        warning = null;

        Process? process = null;

        if (pid > 0)
        {
            try
            {
                process = Process.GetProcessById(pid);
            }
            catch (Exception ex)
            {
                warning = $"指定的 PID {pid} 打不开（{ex.Message}），改用进程名查找。";
            }
        }

        if (process is null)
        {
            var candidates = Process.GetProcessesByName("ControlServer.Server")
                .Select(p =>
                {
                    try { return (Process: p, Start: p.StartTime); }
                    catch { return (Process: p, Start: DateTime.MinValue); }
                })
                .OrderByDescending(p => p.Start)
                .ToList();

            if (candidates.Count == 0)
            {
                warning = "没找到 ControlServer.Server 进程 —— CPU/内存一栏将是空的（心跳/延迟不受影响）。";
                return null;
            }

            process = candidates[0].Process;

            if (candidates.Count > 1)
            {
                warning = $"找到 {candidates.Count} 个 ControlServer.Server 进程，已取启动最晚的那个"
                          + $"（PID {process.Id}）。要精确指定请用 --server-pid。";
            }
        }

        var probe = new ServerProbe(process, metrics, clock);
        probe._loop = Task.Run(probe.LoopAsync);
        return probe;
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
        {
            Sample();
        }
    }

    private void Sample()
    {
        try
        {
            _process.Refresh();

            var now = DateTime.UtcNow;
            var cpu = _process.TotalProcessorTime;
            var second = (int)_clock.Elapsed.TotalSeconds;

            var bucket = _metrics.BucketAt(second);

            // 🔥 在线数也在这里写：它是**每秒一次**的自然节拍器。
            //    交给 worker 在连/断时写的话，只有"发生变化的那些秒"有值，
            //    报告里就会出现"峰值在线 0 台"——第一次冒烟正是这个读数。
            bucket.Connected = Volatile.Read(ref _metrics.ConnectedNow);
            bucket.ServerWorkingSetMb = _process.WorkingSet64 / 1024.0 / 1024.0;
            bucket.ServerThreads = _process.Threads.Count;

            if (_primed)
            {
                var wallMs = (now - _lastAt).TotalMilliseconds;
                var cpuMs = (cpu - _lastCpu).TotalMilliseconds;

                if (wallMs > 0)
                {
                    bucket.ServerCpuPct = Math.Max(0, cpuMs / wallMs / Environment.ProcessorCount * 100.0);
                }
            }

            _lastCpu = cpu;
            _lastAt = now;
            _primed = true;

            // 给进度行用的"最近一次读数"（与分桶解耦，见 Metrics 里的注释）。
            _metrics.LastCpuPct = bucket.ServerCpuPct;
            _metrics.LastWorkingSetMb = bucket.ServerWorkingSetMb;
            _metrics.LastThreads = bucket.ServerThreads;

            PeakWorkingSetMb = Math.Max(PeakWorkingSetMb, bucket.ServerWorkingSetMb);
        }
        catch (InvalidOperationException)
        {
            // 进程退了：停止采样，但不要影响压测收尾。
            _cts.Cancel();
        }
        catch (Exception)
        {
            // 采样失败不算压测失败 —— 报告里那一秒是空的，别的数字仍然有效。
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // 收尾时的等待失败无所谓。
        }

        _cts.Dispose();
        _process.Dispose();
    }
}
