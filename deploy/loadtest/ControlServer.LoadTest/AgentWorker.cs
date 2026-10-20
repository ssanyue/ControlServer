using System.Diagnostics;
using ControlServer.Agent.Transport;
using ControlServer.Contracts;
using ControlServer.Core.Services;

namespace ControlServer.LoadTest;

/// <summary>
/// 一台<b>协议级假 Agent</b>：注册 → 长连接 → 心跳 → 遥测，断了就重连。
///
/// <para>
/// 🔥 <b>它用的是真 Agent 的 <see cref="AgentConnection"/></b>（现场那台机器跑的同一份代码），
/// 所以这里不需要"复刻协议"：凭据头、Welcome 解析、心跳参数个数、失败是否吞掉，
/// 全部由生产代码决定。本类只负责<b>节拍</b>与<b>计数</b>。
/// </para>
///
/// <para>
/// <b>刻意不做的三件事</b>（做了就变成"测自己写的客户端"）：
/// </para>
/// <list type="number">
///   <item>不自己拼 WebSocket / 不手写 Hub 协议；</item>
///   <item>不假装执行指令 —— 收到就计数，不重启任何东西；</item>
///   <item>不模拟抓屏与硬编 —— 那是<b>被控端</b>的活，中心压测不该把它算进来
///         （方案 §2.3 的边界就是这一条）。</item>
/// </list>
/// </summary>
public sealed class AgentWorker
{
    private readonly int _index;
    private readonly LoadTestOptions _options;
    private readonly LogService _log;
    private readonly Metrics _metrics;
    private readonly Stopwatch _clock;
    private readonly string _runId;

    private AgentConnection? _connection;
    private string? _agentKey;
    private AgentWelcomeDto? _welcome;
    private bool _counted;

    public AgentWorker(int index, LoadTestOptions options, LogService log, Metrics metrics, Stopwatch clock, string runId)
    {
        _index = index;
        _options = options;
        _log = log;
        _metrics = metrics;
        _clock = clock;
        _runId = runId;
    }

    /// <summary>注册成功后中心给的 Id（L2/L3 要按它下发指令）。</summary>
    public int AgentId { get; private set; }

    /// <summary>当前连接（L3 的假推流借它把帧送出去）。</summary>
    public AgentConnection? Connection => _connection;

    /// <summary>服务端版本（从 Welcome 里拿；报告头部要写清压的是哪个版本）。</summary>
    public string? ServerVersion => _welcome?.ServerVersion;

    /// <summary>跑满整轮：注册、连接、心跳、遥测、重连，直到 <paramref name="stopAt"/>。</summary>
    public async Task RunAsync(DateTimeOffset stopAt, CancellationToken cancellationToken)
    {
        if (!await RegisterAsync(cancellationToken).ConfigureAwait(false))
        {
            _metrics.RecordRegister(false);
            return;
        }

        _metrics.RecordRegister(true);

        var backoff = TimeSpan.FromSeconds(1);

        while (DateTimeOffset.UtcNow < stopAt && !cancellationToken.IsCancellationRequested)
        {
            if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
            {
                _metrics.RecordConnectFail();
                await SleepAsync(backoff, cancellationToken).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 10));
                continue;
            }

            backoff = TimeSpan.FromSeconds(1);

            var reason = await PumpAsync(stopAt, cancellationToken).ConfigureAwait(false);

            if (reason == PumpExit.Stopped) break;

            // 连接坏了：下一次循环重建。
            _metrics.RecordReconnect(Second());
            DropConnection();
        }

        DropConnection();
    }

    private enum PumpExit
    {
        /// <summary>到点了 / 被取消。</summary>
        Stopped,

        /// <summary>连接不可用，需要重连。</summary>
        Lost,
    }

    /// <summary>
    /// 心跳 / 遥测的节拍器。
    ///
    /// <para>
    /// 🔥 <b>用"下一次该做的时间"驱动，而不是每台一个 <c>Timer</c></b>：
    /// 100 台 × 2 个定时器 = 200 个 Timer，光回调调度就够 PC 抖的；
    /// 这里每台一个循环，两次动作之间 <c>Delay</c> 到点，空闲时不占 CPU。
    /// </para>
    /// </summary>
    private async Task<PumpExit> PumpAsync(DateTimeOffset stopAt, CancellationToken cancellationToken)
    {
        var connection = _connection!;

        var heartbeat = TimeSpan.FromSeconds(_options.HeartbeatSeconds ?? _welcome?.HeartbeatSeconds ?? 10);
        var metricsInterval = TimeSpan.FromSeconds(
            _options.MetricsSeconds ?? _welcome?.Telemetry?.MetricsSeconds ?? 5);

        var nextHeartbeat = DateTimeOffset.UtcNow;   // 立刻来一次，别等 10 秒
        var nextMetrics = DateTimeOffset.UtcNow.AddSeconds(1);
        var failStreak = 0;

        while (DateTimeOffset.UtcNow < stopAt && !cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;

            if (now >= nextHeartbeat)
            {
                var watch = Stopwatch.StartNew();

                var ack = await connection
                    .HeartbeatAsync("loadtest", cancellationToken)
                    .ConfigureAwait(false);

                watch.Stop();
                _metrics.RecordHeartbeat(Second(), watch.Elapsed.TotalMilliseconds, ack is not null);

                if (ack is null)
                {
                    // 单次失败不重连（局域网抖动、服务端 GC 都会这样）；
                    // 连败 3 次才判定这条连接坏了 —— 与真 Agent 的退避口径一致。
                    if (++failStreak >= 3) return PumpExit.Lost;
                }
                else
                {
                    failStreak = 0;

                    // 服务端可以改心跳间隔：以应答里的值为准（真 Agent 也是这么做的）。
                    heartbeat = TimeSpan.FromSeconds(Math.Max(1, ack.HeartbeatSeconds));
                }

                nextHeartbeat = now + heartbeat;
            }

            if (DateTimeOffset.UtcNow >= nextMetrics)
            {
                var watch = Stopwatch.StartNew();

                var ack = await connection
                    .ReportMetricsAsync(BuildBatch(), cancellationToken)
                    .ConfigureAwait(false);

                watch.Stop();
                _metrics.RecordMetrics(Second(), watch.Elapsed.TotalMilliseconds, ack is { Ok: true });

                nextMetrics = DateTimeOffset.UtcNow + metricsInterval;
            }

            var wait = nextHeartbeat < nextMetrics ? nextHeartbeat : nextMetrics;
            var delay = wait - DateTimeOffset.UtcNow;

            if (delay > TimeSpan.Zero)
            {
                // 上限 200 ms：保证到点精度（心跳 RTT 不受影响，但间隔会飘 ——
                // 100 台的唤醒开销可以忽略，而 1 秒的粒度会让"心跳次数"这类计数失真）。
                await SleepAsync(
                        delay < TimeSpan.FromMilliseconds(200) ? delay : TimeSpan.FromMilliseconds(200),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return PumpExit.Stopped;
    }

    private AgentMetricsBatchDto BuildBatch()
    {
        var sample = new AgentMetricsSampleDto(
            At: DateTimeOffset.Now,
            CpuPct: 8 + (_index % 10),
            MemUsedMb: 4096,
            MemTotalMb: 8192,
            DiskReadBps: 0,
            DiskWriteBps: 0,
            NetUpBps: 0,
            NetDownBps: 0,
            GpuPct: null,
            UptimeSeconds: (long)_clock.Elapsed.TotalSeconds);

        return new AgentMetricsBatchDto(new[] { sample });
    }

    private async Task<bool> RegisterAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var registrar = new AgentRegistrar(http, _log);

        var info = new AgentHostInfoDto(
            MachineKey: $"loadtest-{_runId}-{_index:D4}",
            HostName: $"loadtest-{_index:D4}",
            OsDescription: "ControlServer.LoadTest (synthetic)",
            CpuName: "synthetic",
            LogicalProcessors: 4,
            TotalMemoryMb: 8192,
            AgentVersion: "0.0.0-loadtest",
            UserName: "loadtest",
            Interfaces: new[] { new AgentNetworkInterfaceDto("synthetic0", "10.99.0.1", "00:00:00:00:00:00", true) });

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = await registrar
                .RegisterAsync(_options.Server, new AgentRegisterRequest(info), cancellationToken)
                .ConfigureAwait(false);

            if (result.Ok && result.AgentId is { } id && !string.IsNullOrWhiteSpace(result.AgentKey))
            {
                AgentId = id;
                _agentKey = result.AgentKey;
                return true;
            }

            _log.Warn("LoadTest", $"#{_index:D4} 注册失败（{result.Status}）：{result.Message}");

            await SleepAsync(TimeSpan.FromMilliseconds(500 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connection is { State: Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected }) return true;

        DropConnection();
        _welcome = null;

        try
        {
            var connection = new AgentConnection(_options.Server, _agentKey!, _log);

            connection.WelcomeReceived += welcome => _welcome = welcome;

            connection.CommandReceived += command =>
            {
                // 收到 ≠ 执行：这里只数，不动任何东西。
                // L2 的"漏命令数" = 中心说发成功了 − 这里数到的。
                // ⚠️ 只数 restart：建流/停流也走同一个 Command 通道，混在一起数会得出
                //    "收到比发出多"的读数（冒烟时真出现过）。
                _metrics.RecordCommandReceived(Second(), command.Name);
                _log.Debug("LoadTest", $"#{_index:D4} 收到指令「{command.Name}」（不执行）");
            };

            await connection.StartAsync(cancellationToken).ConfigureAwait(false);

            _connection = connection;

            // Welcome 是 OnConnectedAsync 里发的，正常在 StartAsync 返回前后就到；
            // 等 3 秒拿不到不算失败（心跳/遥测不需要它），只是间隔用默认值。
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);

            while (_welcome is null && DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                await SleepAsync(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
            }

            if (_welcome is null)
            {
                _metrics.RecordWelcomeFail();
                _log.Warn("LoadTest", $"#{_index:D4} 连上了但 3 秒内没收到 Welcome —— 心跳间隔用默认值。");
            }

            if (!_counted)
            {
                _counted = true;
                Interlocked.Increment(ref _metrics.ConnectedNow);
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("LoadTest", $"#{_index:D4} 建连失败：{ex.Message}");
            DropConnection();
            return false;
        }
    }

    private void DropConnection()
    {
        var connection = _connection;
        _connection = null;

        if (connection is not null)
        {
            try
            {
                _ = connection.DisposeAsync();
            }
            catch
            {
                // 断开时的异常不影响统计。
            }
        }

        if (_counted)
        {
            _counted = false;
            Interlocked.Decrement(ref _metrics.ConnectedNow);
        }
    }

    private int Second() => (int)_clock.Elapsed.TotalSeconds;

    private static async Task SleepAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 收尾时正常退出。
        }
    }
}
