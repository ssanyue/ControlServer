using ControlServer.Contracts;
using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// 遥测数据的**纯函数**聚合内核（M4.2）。
/// <para>
/// <b>为什么全部做成静态纯函数</b>：这些算法是"数据对不对"的唯一裁决者
/// （降采样会不会把峰值抹平、日聚合的平均值算不算 null、开关机统计配对对不对），
/// 而它们跑在服务端、依赖库与时钟。做成纯函数之后可以脱离服务器直接单测 ——
/// 与 <see cref="AgentPresence"/>、<see cref="AgentDiscoveryProtocol"/> 同一思路。
/// </para>
/// <para>
/// ⚠️ <b>贯穿全篇的一条规则：null 参与运算的结果是"忽略它"，而不是"当成 0"。</b>
/// 一台机器有 3 个采样点，其中 1 个 CPU 取不到 —— 平均值是另外 2 个的均值，
/// 不是 <c>(a + b + 0) / 3</c>。"当成 0"会让"采集失败"看起来像"机器很闲"。
/// </para>
/// </summary>
public static class AgentMetricsAggregator
{
    /// <summary>
    /// 前端一条折线最多画多少个点。
    /// <para>720 是"1080p 下每点约 1.5 像素"的经验值：再多也看不出差别，
    /// 只是把 JSON 和浏览器的绘制成本往上抬。</para>
    /// </summary>
    public const int MaxChartPoints = 720;

    /// <summary>可选的分桶粒度（秒）。从"原始 5 秒"一路到"一天"。</summary>
    private static readonly int[] BucketSteps =
    {
        5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400,
    };

    // ═══════════════════════════════════════════════════════════
    // 单点换算
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 内存占用率（百分数）。
    /// <para>任一项缺失 → null。<b>总量为 0 时也返回 null</b>（除以 0 之外，
    /// "总量 0"本身说明数据是坏的，给它一个 0% 更误导）。</para>
    /// </summary>
    public static double? MemoryPercent(long? usedMb, long? totalMb)
    {
        if (usedMb is not { } used || totalMb is not { } total || total <= 0) return null;

        var pct = used * 100.0 / total;

        // 页面只画 0–100 的曲线；超 100 说明采集口径有问题（例如把缓存算进已用），
        // 夹住比让曲线飞出画布更容易发现问题。
        return pct < 0 ? 0 : pct > 100 ? 100 : pct;
    }

    /// <summary>把一条原始样本折算成"最新一帧摘要"。</summary>
    public static AgentMetricsSummaryDto? Summarize(AgentMetricsSampleDto? sample)
    {
        if (sample is null) return null;

        return new AgentMetricsSummaryDto(
            sample.At,
            sample.CpuPct,
            MemoryPercent(sample.MemUsedMb, sample.MemTotalMb),
            sample.MemUsedMb,
            sample.MemTotalMb,
            sample.DiskReadBps,
            sample.DiskWriteBps,
            sample.NetUpBps,
            sample.NetDownBps,
            sample.GpuPct,
            sample.UptimeSeconds,
            // 温度过一遍净化（见 Temperature 注释）：这一条是"在线表"的数据源，
            // 一个 NaN 就足以让整张在线表的 JSON 序列化炸掉。
            Temperature(sample.CpuTempC),
            Temperature(sample.GpuTempC),
            Temperature(sample.StorageTempC),
            // 逐设备读数**原样透传**（2026-10-13）：详情页靠"每来一帧摘要就往前挪一格"
            // 把曲线接下去，摘要不带它，硬件的"当前值"与曲线就会停在打开页面那一刻。
            // 理由与取舍见 AgentMetricsSummaryDto.Devices 的注释。
            sample.Devices);
    }

    /// <summary>把一条原始样本折算成图上的一个点（内存占用率在这里算好）。</summary>
    public static AgentMetricsPointDto ToPoint(AgentMetricsSampleDto sample)
        => new(
            sample.At,
            sample.CpuPct,
            MemoryPercent(sample.MemUsedMb, sample.MemTotalMb),
            sample.MemUsedMb,
            sample.MemTotalMb,
            sample.DiskReadBps,
            sample.DiskWriteBps,
            sample.NetUpBps,
            sample.NetDownBps,
            sample.GpuPct,
            sample.UptimeSeconds,
            // 与 Summarize 同一套净化：正常路径上这些点来自库里（已净化过），
            // 这里再挡一次是**幂等**的，为的是万一有别的调用方直接喂原始样本。
            Temperature(sample.CpuTempC),
            Temperature(sample.GpuTempC),
            Temperature(sample.StorageTempC),
            // 逐设备读数**原样透传**：它已经是"某一刻的读数"，没有需要在这里算的东西。
            // 只有"跨采样求平均"那种加工才发生在 Accumulator 里（见下）。
            sample.Devices);

    // ═══════════════════════════════════════════════════════════
    // 分桶降采样
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 按"目标点数上限"挑一个分桶粒度。
    /// <para>
    /// 只会返回 <see cref="BucketSteps"/> 里的值（整齐的数字便于人看：
    /// "每 5 分钟一个点"比"每 287 秒一个点"好解释得多）。
    /// </para>
    /// </summary>
    public static int ChooseBucketSeconds(TimeSpan span, int maxPoints = MaxChartPoints)
    {
        if (span <= TimeSpan.Zero || maxPoints <= 0) return BucketSteps[0];

        var raw = span.TotalSeconds / maxPoints;

        foreach (var step in BucketSteps)
        {
            if (step >= raw) return step;
        }

        return BucketSteps[^1];
    }

    /// <summary>
    /// 分桶降采样：把 <paramref name="samples"/> 按 <paramref name="bucketSeconds"/> 分组取均值。
    /// <para>
    /// <b>空桶直接跳过</b>（而不是补 0）：一段"机器关机"的空白应该画成空白。
    /// 补 0 会画出一条"CPU 掉到 0% 然后又上来"的假曲线，看图的人会去追一个不存在的事件。
    /// </para>
    /// <para>桶的边界锚在 <paramref name="from"/> 上（而不是 Unix 纪元）——
    /// 这样"查询窗口的左边界"永远是第一个桶的起点，图形与查询条件对得上。</para>
    /// </summary>
    public static IReadOnlyList<AgentMetricsPointDto> Bucketize(
        IReadOnlyList<AgentMetricsSampleDto> samples,
        DateTimeOffset from,
        DateTimeOffset to,
        int bucketSeconds)
    {
        if (samples.Count == 0) return Array.Empty<AgentMetricsPointDto>();

        var step = bucketSeconds > 0 ? bucketSeconds : BucketSteps[0];

        // 桶 → 累加器。用 SortedDictionary 保证输出按时间升序（SQLite 已经排过，
        // 但聚合函数不该假设调用方排过序 —— 它是个纯函数，输入顺序不该改变语义）。
        var buckets = new SortedDictionary<long, Accumulator>();

        foreach (var sample in samples)
        {
            if (sample.At < from || sample.At > to) continue;

            var offset = (sample.At - from).TotalSeconds;
            var index = (long)Math.Floor(offset / step);

            if (!buckets.TryGetValue(index, out var acc))
            {
                acc = new Accumulator();
                buckets[index] = acc;
            }

            acc.Add(sample);
        }

        var result = new List<AgentMetricsPointDto>(buckets.Count);

        foreach (var (index, acc) in buckets)
        {
            result.Add(acc.ToPoint(from.AddSeconds(index * (double)step)));
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════
    // 空白（客户端离线）→ 断点标记（2026-10-16）
    //
    // 🔥 **要解决的问题**：上面那个「空桶直接跳过」的规矩本身是对的（补 0 会画出假的掉底曲线），
    //    但它只保证"没有数据的桶不产生点"，**不保证折线在那里断开**。
    //    一段两小时的空桶被跳过后，图上剩下前后两个点 —— 而折线会把它们**直接连起来**。
    //    于是"客户端关机两小时"看起来像"CPU 一直 45% 很稳"，
    //    这比补 0 更隐蔽：补 0 至少看得出来掉下去了，连直线是**看不出任何异常**的。
    //
    //    现场口径（2026-10-16）：**客户端离线的时间段，历史曲线直接留空。**
    //
    // **怎么断**：在两段真实数据之间插一个"全 null 的点"（`IsGap = true`）。
    //    前端 `MetricsChart` 的立身之本就是"**null 必须断开折线**"（见该组件文件头），
    //    于是整机曲线与逐设备曲线**同时**断开，两处不会各自演化出一套判据。
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把「日聚合的老一段」与「明细的最近一段」拼成一条时序，并各自标好空白（2026-10-16）。
    /// <para>
    /// <b>为什么不合成一段再判空白</b>：两段用**不同的标称点距**（日聚合是每天 00:00 一个点，
    /// 明细是每 <c>bucket</c> 秒一个点），而且接缝本身天然就隔了一段 ——
    /// 老一段的最后一个点是「rawFrom 前一天的 00:00」，明细段从 rawFrom 开始。
    /// 拿一个判据跨着接缝去判，会<b>每一次查询</b>都在同一个位置插一个假断点：
    /// 图上一条永远存在的豁口，还会随"现在几点"左右滑动 —— 看着像离线，其实什么也没发生。
    /// </para>
    /// <para>
    /// ⚠️ 接缝处的空白<b>是设计里就有的一段</b>（页面上的数据来源说明写着"相邻两点之间可能隔了一整天"），
    /// 不是离线，因此不标、也不该标。
    /// </para>
    /// </summary>
    /// <param name="daily">日聚合层的点（每天 00:00）。</param>
    /// <param name="recent">明细层的点。</param>
    /// <param name="dailyStep">日聚合层的标称点距（一天）。</param>
    /// <param name="recentStep">明细层的标称点距（分桶粒度）。</param>
    /// <param name="offlineAfter">在线表判"离线"的秒数。</param>
    public static IReadOnlyList<AgentMetricsPointDto> MergeLayers(
        IReadOnlyList<AgentMetricsPointDto> daily,
        IReadOnlyList<AgentMetricsPointDto> recent,
        TimeSpan dailyStep,
        TimeSpan recentStep,
        TimeSpan offlineAfter)
    {
        var merged = new List<AgentMetricsPointDto>(daily.Count + recent.Count);

        merged.AddRange(MarkGaps(daily, dailyStep, offlineAfter));
        merged.AddRange(MarkGaps(recent, recentStep, offlineAfter));

        merged.Sort((a, b) => a.At.CompareTo(b.At));

        return merged;
    }

    /// <summary>
    /// 一段空白"算不算离线"的长度判据（取 <c>max(离线判定阈值, 2 × 分桶粒度)</c>）。
    /// <list type="bullet">
    ///   <item>
    ///     <b>离线判定阈值</b>（<c>Server:Agent:OfflineAfterSeconds</c>，默认 75 秒）——
    ///     与在线表上那颗<b>灰点</b>是同一个数。页面上显示"离线"的那段时间，曲线上就是空白，
    ///     两处不该各有一套说法（"图上没断但列表说离线"会让人怀疑其中一个是错的）。
    ///   </item>
    ///   <item>
    ///     <b>2 × 分桶粒度</b>——分桶粗细随窗口变（1 小时窗口 5 秒一格，30 天窗口 1 小时一格），
    ///     没有这个下限的话，粗粒度窗口上"少了一格"就会被判成离线；
    ///     而粗粒度下的"少一格"本来就意味着<b>整整一格的时间</b>没有数据。
    ///   </item>
    /// </list>
    /// <para>
    /// ⚠️ <b>为什么不取"少一格就算"</b>：采样是每 5 秒一条，且客户端在断线重连期间会把样本
    /// 攒进下一批（见 <see cref="AgentMetricsBatchDto"/> 的注释），所以偶尔少一两格是网络的正常抖动、
    /// 不是离线。拿它去断线，曲线上会布满密密麻麻的小豁口 —— 看起来像图表坏了，
    /// 而真正的离线反而被淹没在里面。
    /// </para>
    /// </summary>
    /// <param name="nominalStep">这一段的标称点距（原始段 = 分桶粒度；日聚合段 = 一天）。</param>
    /// <param name="offlineAfter">在线表判"离线"的秒数（<c>Server:Agent:OfflineAfterSeconds</c>）。</param>
    public static TimeSpan GapThreshold(TimeSpan nominalStep, TimeSpan offlineAfter)
    {
        var floor = offlineAfter > TimeSpan.Zero ? offlineAfter : TimeSpan.FromSeconds(75);
        var stepLimit = nominalStep > TimeSpan.Zero ? nominalStep + nominalStep : TimeSpan.Zero;

        return stepLimit > floor ? stepLimit : floor;
    }

    /// <summary>
    /// 一个断点标记：<b>所有指标都是 null</b> 的点，只用来让折线在这里断开。
    /// <para>
    /// 时间戳用<b>空白起点那个桶的边界</b>（调用方算好传进来），不是随手编一个刻度：
    /// 它确实对应库里"从这一刻起没有数据"这件事。
    /// </para>
    /// </summary>
    public static AgentMetricsPointDto GapMarker(DateTimeOffset at) =>
        new(
            at,
            CpuPct: null,
            MemPct: null,
            MemUsedMb: null,
            MemTotalMb: null,
            DiskReadBps: null,
            DiskWriteBps: null,
            NetUpBps: null,
            NetDownBps: null,
            GpuPct: null,
            UptimeSeconds: null,
            IsGap: true);

    /// <summary>
    /// 把时间轴上"空得够久"的空白标成断点，好让折线在那里断开（见本节顶部注释）。
    /// <para>
    /// <b>只标两段真实数据之间</b>的空档：窗口开头与结尾的空白不需要标记
    /// （那里本来就没有线可以画错），<b>免得末尾多出一个全 null 的点</b>去当"最新读数"。
    /// </para>
    /// <para>
    /// <b>幂等</b>：先丢掉入参里已有的标记点再重新判一遍，于是重复调用不会越插越多。
    /// 这一点有实际用处 —— 本函数在"明细段"与"日聚合段"上各要跑一次（两段粒度不同，
    /// 判据也不同），而将来若有人把它挪到更外层、让一段数据被流过两次，结果不会变样。
    /// </para>
    /// </summary>
    /// <param name="points">已按时间升序的点（<see cref="Bucketize"/> / <see cref="DailyToPoints"/> 的输出）。</param>
    /// <param name="nominalStep">标称点距，见 <see cref="GapThreshold"/>。</param>
    /// <param name="offlineAfter">在线表判"离线"的秒数。</param>
    public static IReadOnlyList<AgentMetricsPointDto> MarkGaps(
        IReadOnlyList<AgentMetricsPointDto> points,
        TimeSpan nominalStep,
        TimeSpan offlineAfter)
    {
        // 先滤掉已有的标记（幂等，见方法注释）。没有标记时这一步零分配 —— 返回的是原列表。
        if (points.Any(p => p.IsGap))
        {
            points = points.Where(p => !p.IsGap).ToList();
        }

        if (points.Count < 2) return points;

        var threshold = GapThreshold(nominalStep, offlineAfter);
        var step = nominalStep > TimeSpan.Zero ? nominalStep : TimeSpan.FromSeconds(5);

        List<AgentMetricsPointDto>? result = null;

        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].At - points[i - 1].At >= threshold)
            {
                // 第一次遇到空白才复制一份，之后都往这份上追加。
                result ??= new List<AgentMetricsPointDto>(points.Take(i));

                // 标记落在"上一个有数据的桶之后的那一格"上 —— 即空白真正开始的地方。
                result.Add(GapMarker(points[i - 1].At + step));
            }

            result?.Add(points[i]);
        }

        return result ?? points;
    }

    // ═══════════════════════════════════════════════════════════
    // 日聚合
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把某一天的明细聚成一行。
    /// <para>
    /// <paramref name="sampleIntervalSeconds"/> 用来估算在线时长：
    /// <c>在线秒数 = 采样条数 × 采样间隔</c>，上限一天。这是<b>估算</b>而不是精确值 ——
    /// 精确值要从开关机事件算（见 <see cref="SummarizePower"/>），而那条链路的覆盖率
    /// 取决于事件日志有没有被清理。这里给的是一个"够用且不会撒谎"的数。
    /// </para>
    /// </summary>
    public static AgentMetricsDaily AggregateDaily(
        DateOnly day,
        IReadOnlyList<AgentMetricsSampleDto> samples,
        int sampleIntervalSeconds)
    {
        var acc = new Accumulator();

        foreach (var sample in samples) acc.Add(sample);

        var interval = sampleIntervalSeconds > 0 ? sampleIntervalSeconds : 5;
        var online = Math.Min((long)samples.Count * interval, 86400);

        return new AgentMetricsDaily(
            day,
            samples.Count,
            (int)online,
            acc.Cpu.Average,
            acc.Cpu.Max,
            Round(acc.MemUsed.Average),
            Round(acc.MemUsed.Max),
            Round(acc.MemTotal.Average),
            acc.NetDown.Average,
            acc.NetUp.Average,
            acc.DiskRead.Average,
            acc.DiskWrite.Average,
            acc.Gpu.Average,
            acc.CpuTemp.Average,
            acc.CpuTemp.Max,
            acc.GpuTemp.Average,
            acc.StorageTemp.Average);
    }

    /// <summary>把日聚合行折算成图上的点（<c>UptimeSeconds</c> 用当天在线秒数近似）。</summary>
    public static IReadOnlyList<AgentMetricsPointDto> DailyToPoints(IReadOnlyList<AgentMetricsDaily> rows)
    {
        var result = new List<AgentMetricsPointDto>(rows.Count);

        foreach (var row in rows)
        {
            // 日聚合的"时刻"统一落在当天 00:00（北京时间）—— 图表按天排布时这是唯一自洽的锚点。
            var at = new DateTimeOffset(row.Day.ToDateTime(TimeOnly.MinValue), BeijingTime.Offset);

            result.Add(new AgentMetricsPointDto(
                at,
                row.CpuAvgPct,
                MemoryPercent(row.MemAvgMb, row.MemTotalMb),
                row.MemAvgMb,
                row.MemTotalMb,
                row.DiskReadAvgBps,
                row.DiskWriteAvgBps,
                row.NetUpAvgBps,
                row.NetDownAvgBps,
                row.GpuAvgPct,
                row.SampleCount > 0 ? row.SampleCount : null,
                row.CpuTempAvgC,
                row.GpuTempAvgC,
                row.StorageTempAvgC));
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════
    // 开关机统计
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 从原始开关机事件里算统计。
    /// <para>
    /// <b>在线时长的算法</b>：把事件按时间升序走一遍，遇到"开机"记下起点，
    /// 遇到"关机 / 计划重启 / 意外关机 / 蓝屏"就把 <c>终点 − 起点</c> 计入并清空起点。
    /// 因此：
    /// </para>
    /// <list type="bullet">
    ///   <item>最后一台还开着的机器（没有配对的结束事件）<b>不计入</b> —— 计入就会得到
    ///         "平均在线时长随时间越来越大"这种明显荒谬的曲线；</item>
    ///   <item>意外关机与蓝屏<b>算进</b>在线时长（它确实运行到那一刻）。</item>
    /// </list>
    /// <para>
    /// 睡眠/唤醒不参与配对：休眠期间机器并没有"下线"，把它算成一次关机
    /// 会让"每日开关机次数"翻倍，而运维关心的是"它是不是被频繁重启"。
    /// </para>
    /// </summary>
    /// <summary>
    /// 同一次事故的两条记录（6008「上次关机是意外的」+ 41「内核未干净关闭」）的合并窗口。
    /// <para>实测（2026-10-12 本机）：一次意外断电必然同时产生 6008 与 41，
    /// 不合并的话"意外关机次数"会整整翻一倍。10 分钟足够覆盖两者的时间差（实测在秒级）。</para>
    /// </summary>
    private static readonly TimeSpan UnexpectedCollapseWindow = TimeSpan.FromMinutes(10);

    public static AgentPowerStatsDto SummarizePower(IReadOnlyList<AgentPowerEventDto> events)
    {
        var boots = 0;
        var shutdowns = 0;
        var restarts = 0;
        var unexpected = 0;
        var bsod = 0;

        DateTimeOffset? lastBoot = null;
        DateTimeOffset? lastBootAt = null;
        DateTimeOffset? lastShutdownAt = null;
        DateTimeOffset? lastUnexpectedAt = null;

        var uptimeSum = 0.0;
        var uptimeCount = 0;

        foreach (var e in events.OrderBy(x => x.At))
        {
            switch (e.Kind)
            {
                case AgentPowerEventKinds.Boot:
                    boots++;
                    lastBoot = e.At;
                    if (lastBootAt is null || e.At > lastBootAt) lastBootAt = e.At;
                    break;

                case AgentPowerEventKinds.Shutdown:
                    shutdowns++;
                    if (lastShutdownAt is null || e.At > lastShutdownAt) lastShutdownAt = e.At;
                    CloseUptime(e.At);
                    break;

                case AgentPowerEventKinds.Restart:
                    restarts++;
                    if (lastShutdownAt is null || e.At > lastShutdownAt) lastShutdownAt = e.At;
                    CloseUptime(e.At);
                    break;

                case AgentPowerEventKinds.Unexpected:
                    // 6008 与 41 是同一次事故的两条记录，只把"新的一次"算进去（见常量注释）。
                    if (lastUnexpectedAt is null || e.At - lastUnexpectedAt > UnexpectedCollapseWindow)
                    {
                        unexpected++;
                        lastUnexpectedAt = e.At;
                    }

                    if (lastShutdownAt is null || e.At > lastShutdownAt) lastShutdownAt = e.At;
                    CloseUptime(e.At);
                    break;

                case AgentPowerEventKinds.Bsod:
                    bsod++;
                    if (lastShutdownAt is null || e.At > lastShutdownAt) lastShutdownAt = e.At;
                    CloseUptime(e.At);
                    break;

                // 睡眠 / 唤醒 / 未知种类：只计数不配对（见方法注释）。
                default:
                    break;
            }
        }

        return new AgentPowerStatsDto(
            boots,
            shutdowns,
            restarts,
            unexpected,
            bsod,
            uptimeCount > 0 ? uptimeSum / uptimeCount : null,
            lastBootAt,
            lastShutdownAt);

        void CloseUptime(DateTimeOffset end)
        {
            if (lastBoot is not { } start) return;

            var seconds = (end - start).TotalSeconds;

            // 负值 = 事件乱序或时钟跳变。丢掉这一对，不污染平均值。
            if (seconds >= 0)
            {
                uptimeSum += seconds;
                uptimeCount++;
            }

            lastBoot = null;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    /// <summary>一个桶（或一天）的累加器：均值 + 峰值，null 一律不参与。</summary>
    private sealed class Accumulator
    {
        public Series Cpu = new();
        public Series MemUsed = new();
        public Series MemTotal = new();
        public Series DiskRead = new();
        public Series DiskWrite = new();
        public Series NetUp = new();
        public Series NetDown = new();
        public Series Gpu = new();

        // M4.3 温度：与其它序列**完全同源同语义** —— null 不参与、整桶全 null 则整桶为 null。
        // 于是"没启用温度监控"的机器画出来的温度曲线就是一条**彻底的空白**，
        // 而不是一条贴着 0℃ 的直线（后者的含义完全不同：那叫"机器很凉快"）。
        public Series CpuTemp = new();
        public Series GpuTemp = new();
        public Series StorageTemp = new();

        /// <summary>
        /// 逐设备累加器，键是 <c>(Kind, Key)</c>（2026-10-13）。
        /// <para>
        /// <b>为什么键是 <c>Key</c> 而不是 <c>Name</c></b>：Key 是硬件的稳定身份
        /// （见 <see cref="AgentDeviceReadingDto"/> 的 Key 说明），Name 只是展示名。
        /// 用 Name 当键会让"用户重命名网卡"变成"旧网卡下线 + 新网卡上线"，
        /// 曲线上表现为<b>断成两截</b> —— 而硬件其实一块没变。
        /// </para>
        /// <para>
        /// <b>为什么 SortedDictionary</b>：与外面的桶一样，输出顺序不该取决于输入顺序。
        /// 每个桶里再排一次，保证同一台机器的曲线图例顺序在每次查询里都一样。
        /// </para>
        /// </summary>
        private readonly SortedDictionary<(string Kind, string Key), DeviceAccumulator> _devices = new();

        public void Add(AgentMetricsSampleDto sample)
        {
            Cpu.Add(sample.CpuPct);
            MemUsed.Add(sample.MemUsedMb);
            MemTotal.Add(sample.MemTotalMb);
            DiskRead.Add(sample.DiskReadBps);
            DiskWrite.Add(sample.DiskWriteBps);
            NetUp.Add(sample.NetUpBps);
            NetDown.Add(sample.NetDownBps);
            Gpu.Add(sample.GpuPct);
            CpuTemp.Add(sample.CpuTempC);
            GpuTemp.Add(sample.GpuTempC);
            StorageTemp.Add(sample.StorageTempC);

            AddDevices(sample.Devices);
        }

        /// <summary>
        /// 把一帧里的逐设备读数并进各自的分组。
        /// <para>
        /// ⚠️ <b>老客户端（<paramref name="devices"/> 为 null 或空）不是错误</b>：
        /// 它只是不上报逐设备明细。此时这一桶的逐设备列表就少几块硬件 ——
        /// 前端要能说清"该客户机版本不支持"，而不是把空白当成"读数为 0"。
        /// </para>
        /// </summary>
        private void AddDevices(IReadOnlyList<AgentDeviceReadingDto>? devices)
        {
            if (devices is null || devices.Count == 0) return;

            foreach (var d in devices)
            {
                // 防御：Kind/Key 为空说明上游清洗漏了。这里跳过而不是抛 ——
                // 一条坏读数不该让整桶（以及整张图）取不出来。
                if (string.IsNullOrEmpty(d.Kind) || string.IsNullOrEmpty(d.Key)) continue;

                var slot = (d.Kind, d.Key);

                if (!_devices.TryGetValue(slot, out var acc))
                {
                    acc = new DeviceAccumulator(d.Kind, d.Key);
                    _devices[slot] = acc;
                }

                acc.Add(d);
            }
        }

        public AgentMetricsPointDto ToPoint(DateTimeOffset at)
            => new(
                at,
                Cpu.Average,
                MemoryPercent(Round(MemUsed.Average), Round(MemTotal.Average)),
                Round(MemUsed.Average),
                Round(MemTotal.Average),
                DiskRead.Average,
                DiskWrite.Average,
                NetUp.Average,
                NetDown.Average,
                Gpu.Average,
                null,
                CpuTemp.Average,
                GpuTemp.Average,
                StorageTemp.Average,
                DeviceReadings());

        /// <summary>
        /// 把逐设备分组折算成输出列表，顺序为「<see cref="AgentDeviceKinds.All"/> 的先后 → Key 字典序」。
        /// <para>整桶没有任何逐设备读数时返回 <c>null</c>（而不是空列表）——
        /// 与 <see cref="AgentMetricsSampleDto.Devices"/> 的语义对齐：
        /// "这一帧没带逐设备数据"用 null 表达，前端据此提示"该时段没有逐设备数据"。</para>
        /// </summary>
        private IReadOnlyList<AgentDeviceReadingDto>? DeviceReadings()
        {
            if (_devices.Count == 0) return null;

            var list = new List<AgentDeviceReadingDto>(_devices.Count);

            foreach (var acc in _devices.Values) list.Add(acc.ToReading());

            // 分组时已按 (Kind, Key) 字典序排好，这里只把 Kind 提到"展示顺序"上。
            // 用 OrderBy 是稳定排序，(Kind, Key) 的既有相对次序不会被打乱。
            return list
                .OrderBy(x => KindOrder(x.Kind))
                .ToList();
        }
    }

    /// <summary>
    /// 一块硬件在一个桶里的累加器。
    /// <para>
    /// 六路序列全开、按下发上来的 <c>Kind</c> 各用各的：CPU 只会有 <c>CpuPct</c> 有值、
    /// 磁盘只会有读写、网卡只会有上下行、显卡只有 <c>GpuPct</c>。
    /// 不按 Kind 拆成四个类型，是因为它们的外形完全一样（都是一组 Series + 一个 ToReading），
    /// 拆开只会让上面那个字典变成四个字典。
    /// </para>
    /// </summary>
    private sealed class DeviceAccumulator
    {
        private readonly string _kind;
        private readonly string _key;

        public DeviceAccumulator(string kind, string key)
        {
            _kind = kind;
            _key = key;
            Name = key; // 兜底：万一整桶都没有名字（理论上不会），至少给个能辨认的东西
        }

        /// <summary>
        /// 最近一次见到的展示名。
        /// <para>同一桶内改名（正常不该发生，除非中途换了驱动/重命名网卡）取<b>最后</b>一次 ——
        /// 与"这一桶代表最近的状况"一致。</para>
        /// </summary>
        public string Name { get; private set; }

        public Series CpuPct { get; } = new();
        public Series ReadBps { get; } = new();
        public Series WriteBps { get; } = new();
        public Series UpBps { get; } = new();
        public Series DownBps { get; } = new();
        public Series GpuPct { get; } = new();

        public void Add(AgentDeviceReadingDto d)
        {
            if (!string.IsNullOrWhiteSpace(d.Name)) Name = d.Name;

            CpuPct.Add(d.CpuPct);
            ReadBps.Add(d.ReadBps);
            WriteBps.Add(d.WriteBps);
            UpBps.Add(d.UpBps);
            DownBps.Add(d.DownBps);
            GpuPct.Add(d.GpuPct);
        }

        public AgentDeviceReadingDto ToReading()
            => new(
                _kind,
                _key,
                Name,
                CpuPct.Average,
                ReadBps.Average,
                WriteBps.Average,
                UpBps.Average,
                DownBps.Average,
                GpuPct.Average);
    }

    /// <summary>
    /// 设备种类在输出里的排序权重。未登记的种类排到最后（而不是抛异常）——
    /// 未来加了新种类但忘了往 <see cref="AgentDeviceKinds.All"/> 里登记时，
    /// 曲线还能出来，只是排在末尾。
    /// </summary>
    private static int KindOrder(string kind)
    {
        for (var i = 0; i < AgentDeviceKinds.All.Count; i++)
        {
            if (AgentDeviceKinds.All[i] == kind) return i;
        }

        return AgentDeviceKinds.All.Count;
    }

    /// <summary>一路数值的均值与峰值（忽略 null；全 null 则两者都是 null）。</summary>
    private sealed class Series
    {
        private double _sum;
        private int _count;
        private double? _max;

        public void Add(double? value)
        {
            if (value is not { } v) return;

            // 🔥 NaN / ±∞ 忽略掉，**不能累加**（2026-10-13 补）。
            //    一个 NaN 就能把 _sum 变成 NaN，于是这一桶的均值、以及整条曲线都变成 NaN ——
            //    而 NaN 进了 JSON 会让 GET /api/agents（严格模式）整个序列化失败。
            //    入站已有一道清洗（AgentTelemetryService.SanitizeDevices），这里再挡一次是
            //    **幂等**的：为的是库里可能还躺着历史脏数据，或将来有别的调用方直接喂原始样本。
            if (!double.IsFinite(v)) return;

            _sum += v;
            _count++;
            if (_max is null || v > _max) _max = v;
        }

        public void Add(long? value)
        {
            if (value is { } v) Add((double)v);
        }

        public double? Average => _count > 0 ? _sum / _count : null;

        public double? Max => _max;
    }

    private static long? Round(double? value)
        => value is { } v ? (long)Math.Round(v, MidpointRounding.AwayFromZero) : null;

    /// <summary>
    /// 温度净化（M4.3）：只挡掉<b>物理上不可能</b>的值，不做别的加工。
    /// <para>
    /// 区间取 −40 ~ 150 ℃：CPU / GPU / 硬盘在真实机器上都落在这个范围内
    /// （风冷场合最冷也就十度上下；超过 150 ℃ 的读数一定是传感器或驱动坏了）。
    /// 挡掉它们是为了不让一个坏读数把日峰值、曲线纵轴一起拉飞。
    /// </para>
    /// <para>
    /// ⚠️ <b>刻意不把 <c>0</c> 当成"没有读数"</b>：LHM 确实会在某些驱动上对读不到的传感器报 0，
    /// 但"见 0 就丢"同样会误伤真实数据，而且属于"替传感器做决定"。
    /// 本项目的一贯做法是<b>原样呈现、让异常可见</b>（页面上就是 0 ℃，一眼能看出不对），
    /// 而不是悄悄把它变成 null、让人以为"只是没启用"。
    /// </para>
    /// <para>
    /// 🔥 <b>必须由"存储"与"内存摘要"两条路径共用</b>：只要有一条漏了，
    /// 就会出现"库里是 null、在线表里是 <c>NaN</c>"的分裂 —— 而 <c>NaN</c> 进了 JSON
    /// 会让整个 <c>GET /api/agents</c> 序列化失败（System.Text.Json 严格模式默认拒绝
    /// NaN/Infinity），<b>一台坏客户端就能拖垮整张在线表</b>。
    /// </para>
    /// </summary>
    public static double? Temperature(double? value)
    {
        if (value is not { } v) return null;
        if (double.IsNaN(v) || double.IsInfinity(v)) return null;

        return v is < -40 or > 150 ? null : v;
    }
}
