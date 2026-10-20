using System.Collections.Concurrent;
using ControlServer.Contracts;

namespace ControlServer.LoadTest;

/// <summary>
/// 压测期间的计数器与时间序列。
///
/// <para>
/// 🔥 <b>为什么要"按秒分桶"而不是只留一个总数</b>：M6 的验收要的是<b>曲线</b>，
/// 不是一句"通过"。50 台时 P95 是 3 ms、100 台时变成 300 ms —— 这个转折点只有在
/// 逐秒序列里才看得见；只看平均值会被"前 20 分钟很快、最后 5 分钟爆掉"平均掉。
/// </para>
///
/// <para>
/// 线程模型：所有写入都可能来自 100 个 worker 线程。计数器用 <see cref="Interlocked"/>，
/// 分桶用 <c>lock</c>（桶内是 <see cref="List{T}"/>，只在汇总时读）。
/// </para>
/// </summary>
public sealed class Metrics
{
    /// <summary>一秒的桶。</summary>
    public sealed class Bucket
    {
        public int Connected;
        public int HeartbeatOk;
        public int HeartbeatFail;
        public int MetricsOk;
        public int MetricsFail;
        public int Reconnects;
        public int CommandsReceived;

        // 中心进程（由 ServerProbe 填；-1 = 这一秒没采到）
        public double ServerCpuPct = double.NaN;
        public double ServerWorkingSetMb = double.NaN;
        public int ServerThreads;
    }

    private readonly ConcurrentDictionary<int, Bucket> _buckets = new();

    private readonly ConcurrentQueue<double> _heartbeatRtt = new();
    private readonly ConcurrentQueue<double> _metricsRtt = new();
    private readonly ConcurrentQueue<double> _commandRtt = new();
    private readonly ConcurrentQueue<double> _frameLatency = new();

    private long _registerOk;
    private long _registerFail;
    private long _connectFail;
    private long _welcomeFail;
    private long _reconnects;
    private long _heartbeatOk;
    private long _heartbeatFail;
    private long _metricsOk;
    private long _metricsFail;
    private long _commandsSent;
    private long _commandsSentFail;
    private long _commandsReceived;
    private long _commandsReceivedOther;

    private long _framesSent;
    private long _framesReceived;
    private long _framesSendFail;

    /// <summary>当前连上的假 Agent 数（由 worker 增减）。</summary>
    public int ConnectedNow;

    // 累计值（进度行用）。⚠️ 不要用"当前这一秒的桶"显示进度：
    // 那看起来像"5 秒才 3 次心跳"，而实际是"这一秒正好 3 次" —— 读数会骗人。
    public long HeartbeatOkTotal => Interlocked.Read(ref _heartbeatOk);
    public long HeartbeatFailTotal => Interlocked.Read(ref _heartbeatFail);
    public long MetricsOkTotal => Interlocked.Read(ref _metricsOk);
    public long CommandReceivedTotal => Interlocked.Read(ref _commandsReceived);

    /// <summary>
    /// 重连累计次数。
    /// <para>
    /// 🔴 <b>2026-10-17 补测踩出来的</b>：进度行原来读的是"当前这一秒的桶"，
    /// 而重连是<u>一次性事件</u>（100 台在 20 秒里分散重连，每一秒的桶大多是 0）
    /// ⇒ 屏幕上一直显示"重连 0"，可报告里明明是 100。
    /// 心跳那一列早就改成累计了，重连这列漏了 —— 同一个坑踩第二次。
    /// </para>
    /// </summary>
    public long ReconnectsTotal => Interlocked.Read(ref _reconnects);

    // 🔥 进度行**不能**去读"当前这一秒的桶"：采样是按整秒落桶的，而进度循环
    //    在 t≈5.0 读到的 second=5 那一格，probe 要到 t≈5.99 才写 —— 于是永远读到空值
    //    （100 台那轮进度行整列 "—"，而报告里的数字是好的）。
    //    这里保留"最近一次采到的值"，与分桶彻底解耦。
    public double LastCpuPct = double.NaN;
    public double LastWorkingSetMb = double.NaN;
    public int LastThreads;

    /// <summary>这一秒的桶（不存在就建）。</summary>
    public Bucket BucketAt(int second) => _buckets.GetOrAdd(second, _ => new Bucket());

    public void RecordRegister(bool ok)
    {
        if (ok) Interlocked.Increment(ref _registerOk);
        else Interlocked.Increment(ref _registerFail);
    }

    public void RecordConnectFail() => Interlocked.Increment(ref _connectFail);

    public void RecordWelcomeFail() => Interlocked.Increment(ref _welcomeFail);

    public void RecordReconnect(int second)
    {
        Interlocked.Increment(ref _reconnects);
        Interlocked.Increment(ref BucketAt(second).Reconnects);
    }

    public void RecordHeartbeat(int second, double ms, bool ok)
    {
        if (ok)
        {
            Interlocked.Increment(ref _heartbeatOk);
            Interlocked.Increment(ref BucketAt(second).HeartbeatOk);
            _heartbeatRtt.Enqueue(ms);
        }
        else
        {
            Interlocked.Increment(ref _heartbeatFail);
            Interlocked.Increment(ref BucketAt(second).HeartbeatFail);
        }
    }

    public void RecordMetrics(int second, double ms, bool ok)
    {
        if (ok)
        {
            Interlocked.Increment(ref _metricsOk);
            Interlocked.Increment(ref BucketAt(second).MetricsOk);
            _metricsRtt.Enqueue(ms);
        }
        else
        {
            Interlocked.Increment(ref _metricsFail);
            Interlocked.Increment(ref BucketAt(second).MetricsFail);
        }
    }

    public void RecordCommandSent(double ms, bool ok)
    {
        if (ok)
        {
            Interlocked.Increment(ref _commandsSent);
            _commandRtt.Enqueue(ms);
        }
        else
        {
            Interlocked.Increment(ref _commandsSentFail);
        }
    }

    /// <summary>
    /// 收到一条指令。
    ///
    /// <para>
    /// 🔥 <b>必须按名字分开数</b>：L3 建会话也是走 <c>Command</c> 通道的
    /// （<c>remote-session</c>），它与 L2 的 <c>restart</c> 混在一个计数器里，
    /// 会出现"中心发了 108 条、假机器收了 110 条"这种<b>收到比发出多</b>的荒唐读数 ——
    /// 第一次冒烟就是这么冒出来的。
    /// </para>
    /// </summary>
    public void RecordCommandReceived(int second, string name)
    {
        if (name == AgentCommandNames.Restart)
        {
            Interlocked.Increment(ref _commandsReceived);
            Interlocked.Increment(ref BucketAt(second).CommandsReceived);
        }
        else
        {
            // 其它指令（建流 / 停流 / 改配置……）不参与 L2 的"漏没漏"判定。
            Interlocked.Increment(ref _commandsReceivedOther);
        }
    }

    public void RecordFrameSent(bool ok)
    {
        if (ok) Interlocked.Increment(ref _framesSent);
        else Interlocked.Increment(ref _framesSendFail);
    }

    public void RecordFrameReceived(double latencyMs)
    {
        Interlocked.Increment(ref _framesReceived);
        _frameLatency.Enqueue(latencyMs);
    }

    /// <summary>某一秒的在线数快照（每台 worker 连上/断开时调，不是采样）。</summary>
    public void SetConnected(int second, int count) => BucketAt(second).Connected = count;

    /// <summary>汇总（报告用）。</summary>
    public Summary Snapshot()
    {
        var buckets = _buckets.OrderBy(p => p.Key).Select(p => (Second: p.Key, Data: p.Value)).ToList();

        return new Summary
        {
            Buckets = buckets,
            RegisterOk = Interlocked.Read(ref _registerOk),
            RegisterFail = Interlocked.Read(ref _registerFail),
            ConnectFail = Interlocked.Read(ref _connectFail),
            WelcomeFail = Interlocked.Read(ref _welcomeFail),
            Reconnects = Interlocked.Read(ref _reconnects),
            HeartbeatOk = Interlocked.Read(ref _heartbeatOk),
            HeartbeatFail = Interlocked.Read(ref _heartbeatFail),
            MetricsOk = Interlocked.Read(ref _metricsOk),
            MetricsFail = Interlocked.Read(ref _metricsFail),
            CommandsSent = Interlocked.Read(ref _commandsSent),
            CommandsSentFail = Interlocked.Read(ref _commandsSentFail),
            CommandsReceived = Interlocked.Read(ref _commandsReceived),
            CommandsReceivedOther = Interlocked.Read(ref _commandsReceivedOther),
            FramesSent = Interlocked.Read(ref _framesSent),
            FramesSendFail = Interlocked.Read(ref _framesSendFail),
            FramesReceived = Interlocked.Read(ref _framesReceived),
            HeartbeatRtt = Take(_heartbeatRtt),
            MetricsRtt = Take(_metricsRtt),
            CommandRtt = Take(_commandRtt),
            FrameLatency = Take(_frameLatency),
        };
    }

    private static double[] Take(ConcurrentQueue<double> queue)
    {
        var list = new List<double>(queue.Count);
        while (queue.TryDequeue(out var value)) list.Add(value);
        return list.ToArray();
    }
}

/// <summary>一次压测的最终读数。</summary>
public sealed class Summary
{
    public required IReadOnlyList<(int Second, Metrics.Bucket Data)> Buckets { get; init; }

    public long RegisterOk { get; init; }
    public long RegisterFail { get; init; }
    public long ConnectFail { get; init; }
    public long WelcomeFail { get; init; }
    public long Reconnects { get; init; }
    public long HeartbeatOk { get; init; }
    public long HeartbeatFail { get; init; }
    public long MetricsOk { get; init; }
    public long MetricsFail { get; init; }
    public long CommandsSent { get; init; }
    public long CommandsSentFail { get; init; }
    public long CommandsReceived { get; init; }

    /// <summary>走 <c>Command</c> 通道、但不是 L2 那条 <c>restart</c> 的指令数（建流/停流等）。</summary>
    public long CommandsReceivedOther { get; init; }
    public long FramesSent { get; init; }
    public long FramesSendFail { get; init; }
    public long FramesReceived { get; init; }

    public required double[] HeartbeatRtt { get; init; }
    public required double[] MetricsRtt { get; init; }
    public required double[] CommandRtt { get; init; }
    public required double[] FrameLatency { get; init; }
}
