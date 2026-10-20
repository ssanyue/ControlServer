using System.Diagnostics;

namespace ControlServer.Agent.Tasks;

/// <summary>
/// <b>推流热路径的耗时探针</b>（M7.4 §6.2）：抓屏 / 编码 / 写管道三段各自的均耗。
///
/// <para>
/// 🔥 <b>为什么需要它</b>：60 帧这一轮卡在一句算不通的账上 ——
/// 抓屏实测 3.15 ms ＋ 编码约 8 ms ≈ 11 ms，而实测 18.2 fps 对应 <b>55 ms/帧</b>，
/// 中间有 <b>44 ms 没有归属</b>。在不知道这 44 ms 花在哪一段的情况下选"解耦推流循环"
/// 之类的改法，就是<b>改错层</b>；而这一层（推流循环）是本仓验证得最充分的热路径。
/// 所以先量，再改。
/// </para>
///
/// <para>
/// 🔥 <b>三段必须分开计，不能合成一个"总耗时"</b>：三段的处置完全不同 ——
/// 抓屏慢要降分辨率 / 换抓屏方式，编码慢要换编码器，写管道慢才是"回执把循环卡住了"。
/// 合成一个数会让这三种故障在日志里长得一模一样。
/// </para>
///
/// <para>
/// ⚠️ <b>抓屏这一项<b>只统计"真的抓到画面的那一次"</b></b>：DXGI 的
/// <c>CaptureSurfaceAsync</c> 内部含 <c>AcquireNextFrame</c> 的等待，桌面静止时它会
/// 一直等到超时再返回"没有新帧"。把那些等待算进抓屏，静止的机器会显示"抓屏 100 ms"，
/// 而它实际一帧都没抓 —— 结论会指向完全错误的方向（本仓"假 FAIL 比不断言更坏"的同族错误）。
/// </para>
///
/// <para>
/// 🔥 <b>默认关闭</b>（<see cref="IsRequested"/>）：热路径上加三处 <c>Stopwatch</c> 本身
/// 很便宜，但"每 5 秒一行日志"会污染托盘那份本来就不长的日志。排障时打开，
/// 打开方式见 <see cref="IsRequested"/>。
/// </para>
/// </summary>
public sealed class RemoteStreamProbe
{
    /// <summary>
    /// 打开探针的环境变量名。取值 <c>1 / true / yes / on</c>（不分大小写）即打开。
    /// </summary>
    /// <remarks>
    /// ⚠️ 环境变量只在<b>进程启动时</b>读得到，而托盘一般是开机自启的 ——
    /// 所以实际排障更常用下面那个标记文件。
    /// </remarks>
    public const string EnvironmentVariable = "CONTROLSERVER_STREAM_PROBE";

    /// <summary>
    /// 打开探针的标记文件名（放在<b>用户临时目录</b>里，与托盘的日志同一个地方）。
    ///
    /// <para>
    /// 🔥 <b>为什么是 %TEMP% 而不是数据目录</b>：托盘跑在<b>用户会话</b>里，
    /// 而数据目录（<c>%ProgramData%\ControlServer</c>）可能被 <c>-HardenDataDir</c> 收紧到
    /// 连管理员都要提权才能写。一个"为了看一眼耗时"的开关不该要求提权 ——
    /// 那种开关在现场的真实结局是"没人打开，于是没人量"。
    /// </para>
    ///
    /// <para>⚠️ 它是<b>每个会话开始时</b>读一次，所以建/删文件之后<b>下一次开流</b>生效，
    /// 不必重启托盘。</para>
    /// </summary>
    public const string MarkerFileName = "controlserver-stream-probe.on";

    /// <summary>报告周期。<b>5 秒</b>：比心跳（1 秒）稀、比人失去耐心（10 秒）密。</summary>
    public static readonly TimeSpan ReportPeriod = TimeSpan.FromSeconds(5);

    private readonly Func<long> _now;
    private readonly TimeSpan _period;

    private long _windowStart;

    private long _captureTicks;
    private int _captureSamples;

    private long _encodeTicks;
    private int _encodeSamples;

    private long _writeTicks;
    private int _writeSamples;

    private long _cycleTicks;
    private int _cycleSamples;

    private long _idleTicks;
    private int _idleSamples;

    private int _frames;
    private long _bytes;

    private int _rounds;
    private int _skipNoNewFrame;
    private int _skipEncodeNotReady;
    private int _skipCaptureFailed;
    private int _skipDropped;

    /// <summary>生产用：按环境变量 / 标记文件决定开关。</summary>
    public RemoteStreamProbe()
        : this(IsRequested(), Stopwatch.GetTimestamp, ReportPeriod)
    {
    }

    /// <summary>单测用：开关、时钟、报告周期都可控。</summary>
    internal RemoteStreamProbe(bool enabled, Func<long> now, TimeSpan period)
    {
        Enabled = enabled;
        _now = now;
        _period = period;

        // ⚠️ 起点必须现在就取：等到第一次 TryReport 再取，第一个窗口永远是空的
        //    （而"刚开流那 5 秒"恰恰是最想看的一段）。
        _windowStart = now();
    }

    /// <summary>这一段要不要统计。</summary>
    public bool Enabled { get; }

    /// <summary>
    /// <b>此刻要不要开探针</b>：环境变量说得清就用它，否则看标记文件在不在。
    /// </summary>
    /// <remarks>
    /// ⚠️ 环境变量<b>显式关掉</b>（<c>0 / false / off</c>）也算"说得清"，且优先于标记文件 ——
    /// 否则"文件忘了删"会让一次刻意关闭悄悄失效，而那种失效不会有任何提示。
    /// </remarks>
    public static bool IsRequested()
    {
        var fromEnvironment = Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

        if (fromEnvironment is not null) return fromEnvironment.Value;

        try
        {
            return File.Exists(Path.Combine(Path.GetTempPath(), MarkerFileName));
        }
        catch
        {
            // 拿不到临时目录（权限 / 路径异常）⇒ 当作关闭。
            // ⚠️ 这里刻意不抛：探针是"多看一点"，不该成为开流失败的原因。
            return false;
        }
    }

    /// <summary>
    /// 解析"这个值算不算打开"（<c>null</c> = 没说得清）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>internal</c> 只为让单测钉住那条"显式关掉优先于标记文件"的规则 ——
    /// 它是本类唯一会**静默失效**的一处（文件忘了删，于是"关"没关掉）。
    /// </remarks>
    internal static bool? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => null,
        };
    }

    /// <summary>
    /// “没打点”的标记。
    /// </summary>
    /// <remarks>
    /// 🔥 <b>它不能是 <c>0</c></b>：<c>0</c> 是一个<b>合法的时间戳</b>（时钟刻度从 0 起步时），
    /// 拿它当哨兵会让"起点恰好是 0"的那一帧被当成"关着"而**静默不计**
    /// （单测 <c>RemoteStreamProbeTests.Each_report_covers_only_its_own_window</c> 第一版就是这么红的）。
    /// 负值在任何时钟下都不可能是一个真实刻度。
    /// </remarks>
    public const long NotMarked = -1;

    /// <summary>打一个起点。关着的时候返回 <see cref="NotMarked"/>，后面的 <c>Add*</c> 会直接忽略它。</summary>
    public long Mark() => Enabled ? _now() : NotMarked;

    /// <summary>记一次抓屏耗时（<b>只该在真的抓到画面时调</b>，见类注释）。</summary>
    public void AddCapture(long startedAt) => Add(ref _captureTicks, ref _captureSamples, startedAt);

    /// <summary>记一次编码耗时。</summary>
    public void AddEncode(long startedAt) => Add(ref _encodeTicks, ref _encodeSamples, startedAt);

    /// <summary>记一次写管道耗时（<b>这一项里含着"到中心的往返"</b>，见 §6.2）。</summary>
    public void AddWrite(long startedAt) => Add(ref _writeTicks, ref _writeSamples, startedAt);

    /// <summary>
    /// 记一轮的<b>墙上时间</b>：从"本轮开始那个 <c>tick</c>"到"这一帧发完"。
    ///
    /// <para>
    /// 🔥 <b>它是三段之和的对账方</b>：抓屏 ＋ 编码 ＋ 写管道 <b>不等于</b>一帧的真实间隔 ——
    /// 中间还有打包、尺寸闸门、`await` 续体调度、探针自己那几行。
    /// 没有这一项时，"合计 15.4 ms，实测 19.6 ms/帧"这 4 ms 只能靠猜；
    /// 而猜的方向（降分辨率 / 换编码器 / 改线程模型）恰好是三种完全不同的改法。
    /// </para>
    /// </summary>
    public void AddCycle(long startedAt) => Add(ref _cycleTicks, ref _cycleSamples, startedAt);

    /// <summary>
    /// 记一次<b>节流实际睡了多久</b>（请求的 `remaining` 与真睡下去的差就是睡眠过冲）。
    ///
    /// <para>
    /// ⚠️ 样本数为 0 本身是个结论：说明这一轮**没睡**（工作已经超过周期），
    /// 也就是"帧率上不去"不是节流压的，而是热路径真的忙不过来。
    /// </para>
    /// </summary>
    public void AddIdle(long startedAt) => Add(ref _idleTicks, ref _idleSamples, startedAt);

    /// <summary>记一帧真的发出去了（用于算实测帧率与平均帧大小）。</summary>
    public void CountFrame(long bytes)
    {
        if (!Enabled) return;

        Interlocked.Increment(ref _frames);
        Interlocked.Add(ref _bytes, bytes);
    }

    /// <summary>一轮空转的原因。它把 <c>轮次 − 出帧</c> 这个差额**按责任分开**。</summary>
    public enum SkipReason
    {
        /// <summary>抓屏说没有新画面（DXGI 增量投递）或帧比对相同 —— <b>不是失败</b>。</summary>
        NoNewFrame,

        /// <summary>编码器这一帧没给出码流（异步延迟，正常现象）。</summary>
        EncodeNotReady,

        /// <summary>抓屏抛了（锁屏 / UAC / 显示器热插拔）。</summary>
        CaptureFailed,

        /// <summary>尺寸超限等原因主动丢弃。</summary>
        Dropped,
    }

    /// <summary>
    /// 记一轮循环（<b>每一轮都要记</b>，包括那些一帧都没产出的轮次）。
    ///
    /// <para>
    /// 🔥 <b>它是"帧率为什么不到目标"的分母</b>：`一轮 16.6 ms` 只统计**产出了帧的那些轮**，
    /// 于是"5 秒 238 帧"和"16.6 ms/帧"能同时成立 —— 差额全在空转轮里。
    /// 没有这一项，剩下的时间只能靠"5 秒 ÷ 帧数"倒推，而倒推出来的数
    /// 既说不清是谁花的，也验证不了任何改法。
    /// </para>
    /// </summary>
    public void CountRound()
    {
        if (!Enabled) return;

        Interlocked.Increment(ref _rounds);
    }

    /// <summary>记一轮空转（见 <see cref="SkipReason"/>）。</summary>
    public void CountSkip(SkipReason reason)
    {
        if (!Enabled) return;

        switch (reason)
        {
            case SkipReason.NoNewFrame:
                Interlocked.Increment(ref _skipNoNewFrame);
                break;

            case SkipReason.EncodeNotReady:
                Interlocked.Increment(ref _skipEncodeNotReady);
                break;

            case SkipReason.CaptureFailed:
                Interlocked.Increment(ref _skipCaptureFailed);
                break;

            default:
                Interlocked.Increment(ref _skipDropped);
                break;
        }
    }

    private void Add(ref long ticks, ref int samples, long startedAt)
    {
        // ⚠️ 判据是**负值**（`Mark()` 关着时给的哨兵），不是 0 —— 0 是合法刻度。
        //    拿 0 当哨兵时，"起点恰好是 0"的那一帧会被静默丢掉，而报告只会显示
        //    "抓屏 ×0"，看起来像"这一段没抓到画面"，不像"探针写错了"。
        if (!Enabled || startedAt < 0) return;

        Interlocked.Add(ref ticks, _now() - startedAt);
        Interlocked.Increment(ref samples);
    }

    /// <summary>
    /// 到一个窗口了就取一份报告。
    /// </summary>
    /// <param name="targetFps">会话<b>此刻生效的</b>目标帧率（写进日志的那一句要靠它自证）。</param>
    /// <param name="line">报告正文（没有到窗口时为空串）。</param>
    /// <returns>真 = 这次真的产出了一行。</returns>
    public bool TryReport(int targetFps, out string line)
    {
        line = string.Empty;

        if (!Enabled) return false;

        var now = _now();

        var elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref _windowStart), now);

        if (elapsed < _period) return false;

        // ⚠️ 先换窗口再取数：反过来的话，取数与清零之间进来的样本会被记进
        //    下一个窗口的"上一帧"，而那个数字会看起来像"这一帧特别慢"。
        Interlocked.Exchange(ref _windowStart, now);

        var frames = Interlocked.Exchange(ref _frames, 0);
        var bytes = Interlocked.Exchange(ref _bytes, 0);

        var captureTicks = Interlocked.Exchange(ref _captureTicks, 0);
        var captureSamples = Interlocked.Exchange(ref _captureSamples, 0);

        var encodeTicks = Interlocked.Exchange(ref _encodeTicks, 0);
        var encodeSamples = Interlocked.Exchange(ref _encodeSamples, 0);

        var writeTicks = Interlocked.Exchange(ref _writeTicks, 0);
        var writeSamples = Interlocked.Exchange(ref _writeSamples, 0);

        var cycleTicks = Interlocked.Exchange(ref _cycleTicks, 0);
        var cycleSamples = Interlocked.Exchange(ref _cycleSamples, 0);

        var idleTicks = Interlocked.Exchange(ref _idleTicks, 0);
        var idleSamples = Interlocked.Exchange(ref _idleSamples, 0);

        var rounds = Interlocked.Exchange(ref _rounds, 0);
        var skipNoNewFrame = Interlocked.Exchange(ref _skipNoNewFrame, 0);
        var skipEncodeNotReady = Interlocked.Exchange(ref _skipEncodeNotReady, 0);
        var skipCaptureFailed = Interlocked.Exchange(ref _skipCaptureFailed, 0);
        var skipDropped = Interlocked.Exchange(ref _skipDropped, 0);

        line = Describe(
            elapsed, frames, bytes, targetFps,
            captureTicks, captureSamples,
            encodeTicks, encodeSamples,
            writeTicks, writeSamples,
            cycleTicks, cycleSamples,
            idleTicks, idleSamples,
            rounds, skipNoNewFrame, skipEncodeNotReady, skipCaptureFailed, skipDropped);

        return true;
    }

    /// <summary>
    /// 报告正文（<b>纯函数</b>，便于单测把每一个除法边界钉住）。
    /// </summary>
    /// <remarks>
    /// 🔥 三段各带<b>自己的样本数</b>：分块编码时一次编码产出 N 块、写 N 次，
    /// 而"帧"只有 1 —— 用帧数当分母去算写管道均耗，会得到 N 分之一的假数字。
    /// </remarks>
    internal static string Describe(
        TimeSpan elapsed,
        int frames,
        long bytes,
        int targetFps,
        long captureTicks, int captureSamples,
        long encodeTicks, int encodeSamples,
        long writeTicks, int writeSamples,
        long cycleTicks = 0, int cycleSamples = 0,
        long idleTicks = 0, int idleSamples = 0,
        int rounds = 0, int skipNoNewFrame = 0, int skipEncodeNotReady = 0,
        int skipCaptureFailed = 0, int skipDropped = 0)
    {
        var seconds = elapsed.TotalSeconds > 0 ? elapsed.TotalSeconds : 0;

        var fps = seconds > 0 ? frames / seconds : 0;

        var averageKb = frames > 0 ? bytes / 1024.0 / frames : 0;

        var head = $"[Stream] 探针 {seconds:0.0}s：出帧 {frames}（{fps:0.0} fps，目标 {targetFps}）";

        if (frames == 0 && captureSamples == 0 && encodeSamples == 0 && writeSamples == 0
            && cycleSamples == 0 && idleSamples == 0 && rounds == 0)
        {
            // ⚠️ 这一句必须说出来，不能只打一行 0：
            //    "窗口内什么都没有"有三种完全不同的成因（桌面真的静止 / 抓屏一直在超时 /
            //    托盘已经卡死），而它们的下一步动作各不相同。
            return head + "｜窗口内没有任何抓屏/编码/写管道取样"
                + "（桌面静止，或这一路已经卡在更早的一步）";
        }

        var capture = Milliseconds(captureTicks, captureSamples);
        var encode = Milliseconds(encodeTicks, encodeSamples);
        var write = Milliseconds(writeTicks, writeSamples);

        var total = Milliseconds(captureTicks + encodeTicks + writeTicks, captureSamples);

        var cycle = Milliseconds(cycleTicks, cycleSamples);

        var idle = Milliseconds(idleTicks, idleSamples);

        // 🔥 **对账的那一截**（M7.4 §12.2）：只有真的按轮打了点才输出 ——
        //    没接 `AddCycle` 的调用方（单测）不该看到一句凭空出现的"节流没睡"。
        //    `一轮 − 三段` 就是"打包 / 闸门 / 续体调度"这些没有名字的开销；
        //    `节流 睡 ×0` 则直接说明这一轮**没睡**，帧率是被工作本身压住的。
        var pacing = cycleSamples == 0
            ? string.Empty
            : $"｜一轮 {cycle:0.0}ms（三段外 {cycle - total:0.0}ms）"
              + (idleSamples > 0
                  ? $"｜节流睡 {idle:0.0}ms×{idleSamples}"
                  : "｜节流没睡（一轮已超周期）");

        // 🔥 **空转轮次按责任分开**（M7.4 §12.2）：这一句回答"5 秒 238 帧为什么不是 300 帧"。
        //    合成一个"空转 62 次"没有用 —— 四种原因该去查的东西完全不同
        //    （无新帧是抓屏语义、编码未就绪是编码器、抓屏失败是桌面状态、主动丢是闸门）。
        var skips = rounds == 0
            ? string.Empty
            : $"｜轮 {rounds}（无新帧 {skipNoNewFrame} / 编码未就绪 {skipEncodeNotReady}"
              + $" / 抓屏失败 {skipCaptureFailed} / 主动丢 {skipDropped}）";

        return head
            + skips
            + $"｜均耗 抓屏 {capture:0.0}ms×{captureSamples}"
            + $" / 编码 {encode:0.0}ms×{encodeSamples}"
            + $" / 写管道 {write:0.0}ms×{writeSamples}"
            + $"｜合计 {total:0.0}ms/帧（按抓屏次数摊）"
            + pacing
            + $"｜平均 {averageKb:0} KB/帧";
    }

    private static double Milliseconds(long ticks, int samples)
        => samples > 0 ? ticks / (double)samples * 1000.0 / Stopwatch.Frequency : 0;
}
