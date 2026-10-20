using System.Diagnostics;
using System.Text;
using ControlServer.Core.Services;

namespace ControlServer.LoadTest;

/// <summary>
/// M6.3 压测工具：起 N 台<b>协议级假 Agent</b>，把中心压到 100 台在线的形态，出一份曲线报告。
///
/// <para>
/// 🔥 <b>它只做一件事：量中心。</b>不抓屏、不编码、不模拟桌面 —— 那些是被控端的成本，
/// 混进来之后"100 台"到底压的是谁就说不清了（方案 §2.3）。
/// </para>
///
/// <para>
/// <b>三条纪律</b>：
/// </para>
/// <list type="number">
///   <item>目标必须显式给（<c>--server</c>），没有默认值 —— 免得把 100 台假机器灌进现场库；</item>
///   <item>不绑固定端口（铁律 12）：端口由启动脚本挑空闲的传进来；</item>
///   <item>报告只给事实，不给"通过/不通过" —— 门槛要人拿着曲线去定（方案 §7②）。</item>
/// </list>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        LoadTestOptions? options;

        try
        {
            options = LoadTestOptions.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"参数有误：{ex.Message}");
            LoadTestOptions.PrintUsage();
            return 2;
        }

        if (options is null)
        {
            LoadTestOptions.PrintUsage();
            return 2;
        }

        var clock = Stopwatch.StartNew();
        var metrics = new Metrics();
        var dataDirectory = new ProbeDataDirectory();

        await using var log = new LogService(dataDirectory, capacity: 1000, flushIntervalMs: 1000);
        using var admin = new AdminClient(options.Server, options.User, options.Password);

        Console.WriteLine($"[加载] 目标中心 {options.Server}");
        Console.WriteLine($"[加载] {options.Agents} 台 × {options.Minutes:0.##} 分钟，爬坡 {options.RampSeconds:0.#} s");

        // ── 探活 ──
        var ping = await admin.PingAsync(CancellationToken.None);

        if (ping is null)
        {
            Console.Error.WriteLine($"[错误] 中心不可达：{options.Server}/healthz —— 服务没起来，或地址写错。");
            return 3;
        }

        Console.WriteLine($"[加载] 中心健康检查 {ping:0.0} ms");

        // ── 登录（只有 L2/L3 需要；L1 不需要，所以登录失败不阻断 L1）──
        var login = await admin.LoginAsync(CancellationToken.None);
        var needAdmin = options.CommandsPerMinute > 0 || options.Streams > 0;

        if (!login.Ok)
        {
            if (needAdmin)
            {
                Console.Error.WriteLine($"[错误] 管理员登录失败（{login.Message}）—— L2/L3 需要它，本轮无法继续。");
                return 3;
            }

            Console.WriteLine($"[警告] 管理员登录失败（{login.Message}）—— 本轮只测 L1，不受影响。");
        }
        else if (needAdmin)
        {
            Console.WriteLine("[加载] 管理员登录成功（L2/L3 可用）");
        }

        // ── 中心进程采样 ──
        var probe = ServerProbe.TryStart(options.ServerPid, metrics, clock, out var probeWarning);

        if (probeWarning is not null) Console.WriteLine($"[警告] {probeWarning}");

        // ── 起假 Agent ──
        var runId = DateTime.Now.ToString("MMddHHmmss");
        var workers = Enumerable
            .Range(0, options.Agents)
            .Select(i => new AgentWorker(i, options, log, metrics, clock, runId))
            .ToList();

        var startAt = DateTimeOffset.UtcNow;
        var stopAt = startAt.AddMinutes(options.Minutes);

        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("[中断] 收到 Ctrl+C，开始收尾……");
            cts.Cancel();
        };

        var tasks = new List<Task>();
        var rampGapMs = options.Agents <= 1 ? 0 : (int)(options.RampSeconds * 1000 / (options.Agents - 1));

        for (var i = 0; i < workers.Count; i++)
        {
            var worker = workers[i];
            tasks.Add(Task.Run(() => worker.RunAsync(stopAt, cts.Token), cts.Token));

            if (rampGapMs > 0 && i < workers.Count - 1) await Task.Delay(rampGapMs, cts.Token).ConfigureAwait(false);
        }

        // ── L2：命令下发 ──
        Task? commandTask = null;

        if (options.CommandsPerMinute > 0 && login.Ok)
        {
            commandTask = Task.Run(
                () => CommandLoopAsync(options, admin, metrics, workers, startAt, stopAt, cts.Token),
                cts.Token);
        }

        // ── L3：假推流 ──
        StreamProbe? stream = null;
        Task? streamTask = null;

        if (options.Streams > 0 && login.Ok)
        {
            stream = new StreamProbe(options, admin, metrics, log);

            var streamProbe = stream;
            var readyAt = startAt.AddSeconds(Math.Max(3, options.RampSeconds + 3));

            streamTask = Task.Run(async () =>
            {
                // 等爬坡完成：一路推流要在"已经有 N 台在线"的前提下测，否则测的是空场。
                while (DateTimeOffset.UtcNow < readyAt && !cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(100, cts.Token).ConfigureAwait(false);
                }

                // 提前 3 秒收，留出 StopSession 的时间。
                await streamProbe.RunAsync(workers, readyAt, stopAt.AddSeconds(-3), cts.Token).ConfigureAwait(false);
            }, cts.Token);
        }

        // ── 在线数采样 ──
        //
        // ⚠️ 不依赖 ServerProbe：它可能因为"进程名对不上"整个缺席，
        //    而"峰值在线几台"是整份报告最基础的一个数 —— 不能跟着一起消失。
        var sampler = Task.Run(
            async () =>
            {
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        var second = (int)clock.Elapsed.TotalSeconds;
                        metrics.BucketAt(second).Connected = Volatile.Read(ref metrics.ConnectedNow);
                        await Task.Delay(500, cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常收尾。
                }
            },
            cts.Token);

        // ── 进度 ──
        var progress = Task.Run(
            () => ProgressLoopAsync(options, metrics, clock, stopAt, cts.Token),
            cts.Token);

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C 的正常路径。
        }

        // 🔥 **必须先停掉 cts**：worker 到点是**自己**退出的（看 stopAt），
        //    而进度条 / L2 / L3 三个循环还在等 cts —— 不取消的话，
        //    `await progress` 会永远不返回，进程就挂在那儿一直打"剩余 0.0 分"。
        cts.Cancel();

        try
        {
            if (commandTask is not null) await commandTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常收尾。
        }

        try
        {
            if (streamTask is not null) await streamTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常收尾。
        }

        try
        {
            await progress.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常收尾。
        }

        try
        {
            await sampler.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常收尾。
        }

        // ── 收尾 ──
        var elapsed = clock.Elapsed;
        var summary = metrics.Snapshot();
        var serverVersion = workers.Select(w => w.ServerVersion).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "unknown";

        probe?.Dispose();

        var (mdPath, csvPath) = Report.Write(options, summary, probe, stream, serverVersion, elapsed);

        if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);

        dataDirectory.Cleanup();

        Console.WriteLine();
        Console.WriteLine("=== 完成 ===");
        Console.WriteLine($"注册 {summary.RegisterOk} / 失败 {summary.RegisterFail}；"
                          + $"心跳 ok {summary.HeartbeatOk} / fail {summary.HeartbeatFail}；重连 {summary.Reconnects} 次");
        Console.WriteLine($"报告：{Path.GetFullPath(mdPath)}");
        Console.WriteLine($"序列：{Path.GetFullPath(csvPath)}");

        return 0;
    }

    /// <summary>L2：按分钟速率轮流对假机器下发「重启 Agent 服务」（假机器只计数、不执行）。</summary>
    private static async Task CommandLoopAsync(
        LoadTestOptions options,
        AdminClient admin,
        Metrics metrics,
        IReadOnlyList<AgentWorker> workers,
        DateTimeOffset startAt,
        DateTimeOffset stopAt,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(60.0 / options.CommandsPerMinute);
        var next = DateTimeOffset.UtcNow.AddSeconds(Math.Max(2, options.RampSeconds));

        var cursor = 0;

        while (DateTimeOffset.UtcNow < stopAt && !cancellationToken.IsCancellationRequested)
        {
            var ready = workers.Where(w => w.AgentId > 0 && w.Connection is not null).ToArray();

            if (ready.Length == 0)
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var target = ready[cursor++ % ready.Length];
            var (ok, ms, message) = await admin.RebootAsync(target.AgentId, cancellationToken).ConfigureAwait(false);

            metrics.RecordCommandSent(ms, ok);

            if (!ok && message is not null)
            {
                Console.WriteLine($"[L2] 给机器 #{target.AgentId} 下发失败：{message}");
            }

            next += interval;
            var delay = next - DateTimeOffset.UtcNow;

            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            else next = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>每 5 秒打一行进度，让人能看着它跑（也方便判断"卡住了"还是"在慢慢涨"）。</summary>
    private static async Task ProgressLoopAsync(
        LoadTestOptions options,
        Metrics metrics,
        Stopwatch clock,
        DateTimeOffset stopAt,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var second = (int)clock.Elapsed.TotalSeconds;

                var remaining = stopAt - DateTimeOffset.UtcNow;

                Console.WriteLine(
                    $"[{second,4}s] 在线 {Volatile.Read(ref metrics.ConnectedNow),3}/{options.Agents}　"
                    + $"心跳累计 {metrics.HeartbeatOkTotal} ok / {metrics.HeartbeatFailTotal} fail　"
                    + $"遥测 {metrics.MetricsOkTotal}　"
                    + $"重连 {metrics.ReconnectsTotal}　"
                    + $"CPU {(double.IsNaN(metrics.LastCpuPct) ? "—" : metrics.LastCpuPct.ToString("0.0") + "%")}　"
                    + $"内存 {(double.IsNaN(metrics.LastWorkingSetMb) ? "—" : metrics.LastWorkingSetMb.ToString("0") + "MB")}　"
                    + $"剩余 {Math.Max(0, remaining.TotalMinutes):0.0} 分");
            }
        }
        catch (OperationCanceledException)
        {
            // 正常收尾。
        }
    }
}
