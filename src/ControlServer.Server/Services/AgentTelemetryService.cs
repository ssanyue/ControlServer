using System.Collections.Concurrent;
using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using ControlServer.Server.Options;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.Services;

/// <summary>
/// 遥测的摄入与查询（M4.2）。
/// <para>
/// <b>两条完全不同的持久化路径，这是本类最重要的一个决定</b>：
/// </para>
/// <list type="bullet">
///   <item>
///     <b>指标（高频）</b>：客户端每 5 秒一批 ⇒ <b>先落到内存缓冲，应答"已接受"，
///     再由后台任务成批写库</b>。直接写库的话，300 台就是 60 次写/秒，
///     SQLite 的 WAL 会被这种小行写满，而"改个备注"这类真正的写操作会被挤在后面。
///     代价：进程崩溃最多丢一个落盘周期的数据（可配，默认 30 秒）—— 曲线少一段，
///     没人会因此做错决定。
///   </item>
///   <item>
///     <b>开关机事件（低频、高价值）</b>：一天最多几条，但<b>丢一条就永远补不回来</b>
///     （客户端的水位一旦推进，那条事件不会再发）。因此这一条路<b>同步写库后再应答</b> ——
///     应答说"接受"就意味着已经落盘。用一点延迟换数据的真实性，这个交换在任何时候都划算。
///   </item>
/// </list>
/// <para>
/// <b>硬件清单</b>同开关机事件：6 小时一次，同步写。
/// </para>
/// </summary>
public sealed class AgentTelemetryService
{
    /// <summary>
    /// 单台机器内存缓冲里最多攒多少条样本。
    /// <para>超过就丢<b>最旧</b>的：缓冲存在的意义是"扛过短暂的写库故障"，
    /// 而不是"把数据库离线期间的所有数据都攒在内存里"（那会先把服务端 OOM）。</para>
    /// </summary>
    private const int MaxQueuedSamplesPerAgent = 5000;

    /// <summary>
    /// 单次时序查询最多从库里取多少行原始样本。
    /// <para>它同时也是"纯明细路径能覆盖多长窗口"的推导依据（见 <see cref="QuerySeriesAsync"/>）。</para>
    /// </summary>
    private const int MaxRawRows = 200_000;

    /// <summary>
    /// 单个样本里最多接受几台设备（2026-10-13）。
    /// <para>
    /// ⚠️ 与客户端 <c>TelemetryCollector.MaxDevicesPerSample</c> 是同一个语义 —— 改动要一起改。
    /// 客户端那道是"别发太多"，这里是"别收太多"：服务端不能假设对面一定是本项目的客户端。
    /// </para>
    /// </summary>
    private const int MaxDevicesPerSample = 64;

    private readonly IAgentTelemetryStore _store;
    private readonly AgentHostRegistry _registry;
    private readonly LogService _log;
    private readonly IOptions<ServerOptions> _options;

    /// <summary>待落盘的样本（按主机）。值是 <see cref="List{T}"/>，读写都在 <c>lock (list)</c> 下。</summary>
    private readonly ConcurrentDictionary<int, List<AgentMetricsSampleDto>> _pending = new();

    /// <summary>有新的遥测待推送的主机（前端刷新用）。</summary>
    private readonly ConcurrentDictionary<int, byte> _dirty = new();

    public AgentTelemetryService(
        IAgentTelemetryStore store,
        AgentHostRegistry registry,
        LogService log,
        IOptions<ServerOptions> options)
    {
        _store = store;
        _registry = registry;
        _log = log;
        _options = options;
    }

    private AgentTelemetryOptions Options => _options.Value.Agent.Telemetry;

    // ═══════════════════════════════════════════════════════════
    // 摄入（客户端调用）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 接受一批指标样本。
    /// <para><b>同步返回"已接受"，异步落盘</b>（见类注释）。</para>
    /// </summary>
    public AgentIngestAckDto AcceptMetrics(int agentId, AgentMetricsBatchDto? batch)
    {
        var options = Options;

        if (!options.Enabled)
        {
            return AgentIngestAckDto.Rejected("disabled", "服务端未启用遥测（Server:Agent:Telemetry:Enabled=false）。");
        }

        if (batch is null || batch.Samples.Count == 0)
        {
            // 空批次不值得报错：客户端在刚连上、还没到第一个采样点时就会发一次。
            return AgentIngestAckDto.Success();
        }

        if (batch.Samples.Count > options.MaxBatchSamples)
        {
            // 整批拒收而不是截断：截断会静默丢一半数据，而"少了一半采样点"在曲线上
            // 看起来只是"有点稀疏"，没人会去查原因。
            return AgentIngestAckDto.Rejected(
                "toomany",
                $"单批样本数 {batch.Samples.Count} 超过上限 {options.MaxBatchSamples}。");
        }

        var now = BeijingTime.Now;

        // 时区/时钟离谱的样本直接丢：一台时钟错乱的机器能把整张图拉成一根平线。
        // 允许 1 天的未来偏差（客户机时钟略快是常态）。
        var earliest = now.AddDays(-400);
        var latest = now.AddDays(1);

        var accepted = new List<AgentMetricsSampleDto>(batch.Samples.Count);
        var dropped = 0;

        foreach (var sample in batch.Samples)
        {
            if (sample.At < earliest || sample.At > latest)
            {
                dropped++;
                continue;
            }

            accepted.Add(SanitizeDevices(sample));
        }

        if (dropped > 0)
        {
            _log.Warn("Telemetry", $"客户端 Id={agentId} 上报了 {dropped} 条时间戳越界的样本，已丢弃。");
        }

        if (accepted.Count == 0) return AgentIngestAckDto.Success();

        Enqueue(agentId, accepted);

        // 热层：把"最新一帧"给在线表，页面上的实时数字立刻有值（不等落盘）。
        var newest = accepted.OrderByDescending(s => s.At).First();
        var summary = AgentMetricsAggregator.Summarize(newest);

        if (summary is not null)
        {
            _registry.SetMetrics(agentId, summary);
            _dirty[agentId] = 0;
        }

        return AgentIngestAckDto.Success(accepted.Count);
    }

    /// <summary>
    /// 接受一批开关机事件。<b>同步落盘后再应答</b>（见类注释：这类数据丢一条就补不回来）。
    /// </summary>
    public async Task<AgentIngestAckDto> AcceptPowerEventsAsync(
        int agentId,
        AgentPowerEventBatchDto? batch,
        CancellationToken cancellationToken = default)
    {
        if (batch is null || batch.Events.Count == 0) return AgentIngestAckDto.Success();

        var options = Options;

        if (batch.Events.Count > options.MaxBatchPowerEvents)
        {
            return AgentIngestAckDto.Rejected(
                "toomany",
                $"单批事件数 {batch.Events.Count} 超过上限 {options.MaxBatchPowerEvents}。");
        }

        try
        {
            var written = await _store.AppendPowerEventsAsync(agentId, batch.Events, cancellationToken).ConfigureAwait(false);

            if (written > 0)
            {
                _log.Info("Telemetry", $"客户端 Id={agentId} 上报开关机事件 {written} 条（去重后）。");
            }

            return AgentIngestAckDto.Success(written);
        }
        catch (Exception ex)
        {
            // 注意这里**不能**回"已接受"：客户端据此推进水位，回了就永远丢这些记录。
            _log.Error("Telemetry", $"开关机事件写入失败（Id={agentId}）—— 已要求客户端重发", ex);
            return AgentIngestAckDto.Rejected("error", "服务端写入失败，请稍后重发。");
        }
    }

    /// <summary>
    /// 服务端手上有没有这台机器的硬件清单（2026-10-15）。
    /// <para>
    /// 唯一用途：<see cref="Hubs.AgentHub.OnConnectedAsync"/> 把它随 Welcome 下发给客户端，
    /// 让"服务端这边是白纸、客户端却以为刚报过"这种状态能被立刻纠正
    /// （见 <see cref="AgentWelcomeDto.NeedsInventory"/> 的长注释）。
    /// </para>
    /// <para>
    /// ⚠️ <b>一次连接查一次</b>（不是每次心跳）：它只在建连时被调用，
    /// 代价是一次按主键的读；放进心跳就成了"每 10 秒一次没人需要的查库"。
    /// </para>
    /// </summary>
    public async Task<bool> HasInventoryAsync(int agentId, CancellationToken cancellationToken = default)
    {
        try
        {
            var record = await _store.GetInventoryAsync(agentId, cancellationToken).ConfigureAwait(false);

            // 记录存在**且**正文非空才算"有"：`agent_device_readings` 之类的伴生行
            // 可能先于清单正文落库，只看"记录非 null"会得出相反（且错误）的结论。
            return record?.Inventory is not null;
        }
        catch (Exception ex)
        {
            // 查不出来时**说"有"**（＝不下发补报要求）：宁可漏一次补报，
            // 也不要在库抖一下的时候让全网机器一起重采一遍 WMI（那正是要避免的代价）。
            _log.Error("Telemetry", $"查硬件清单是否存在失败（Id={agentId}）—— 按「已有」处理，不要求补报", ex);

            return true;
        }
    }

    /// <summary>接受一份硬件清单（同步写，6 小时一次）。</summary>
    public async Task<AgentIngestAckDto> AcceptInventoryAsync(
        int agentId,
        AgentInventoryDto? inventory,
        CancellationToken cancellationToken = default)
    {
        if (inventory is null) return AgentIngestAckDto.Success();

        // 热状态是客户端自己上报的，因此要过一遍白名单（服务端不猜也不照单全收）。
        // 页面拿它去匹配三个分支，匹配不上就只能显示"未知" —— 那等于悄悄丢掉了信息，
        // 还不如明确地清成 null（"老客户端还没上报过"）。
        // ⚠️ 状态不合法时**连 note 一起清**：留一条"失败原因"配着"未知"状态只会让人更困惑。
        var sanitized =
            inventory.ThermalState is null || AgentThermalStates.IsValid(inventory.ThermalState)
                ? inventory
                : inventory with { ThermalState = null, ThermalNote = null };

        try
        {
            await _store.SaveInventoryAsync(agentId, sanitized, cancellationToken).ConfigureAwait(false);

            return AgentIngestAckDto.Success();
        }
        catch (Exception ex)
        {
            _log.Error("Telemetry", $"硬件清单写入失败（Id={agentId}）", ex);
            return AgentIngestAckDto.Rejected("error", "服务端写入失败。");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 落盘 / 推送（后台任务调用）
    // ═══════════════════════════════════════════════════════════

    /// <summary>把内存缓冲里的样本成批写库。失败时把数据<b>放回去</b>，下轮重试。</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        foreach (var agentId in _pending.Keys.ToList())
        {
            if (cancellationToken.IsCancellationRequested) return;

            if (!_pending.TryRemove(agentId, out var list)) continue;

            List<AgentMetricsSampleDto> drained;

            lock (list)
            {
                drained = new List<AgentMetricsSampleDto>(list);
                list.Clear();
            }

            if (drained.Count == 0) continue;

            try
            {
                await _store.AppendMetricsAsync(agentId, drained, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("Telemetry", $"指标落盘失败（Id={agentId}，{drained.Count} 条）—— 已放回缓冲等待重试", ex);

                var target = _pending.GetOrAdd(agentId, _ => new List<AgentMetricsSampleDto>());

                lock (target)
                {
                    // 放回队首（这些比新来的更旧，顺序上应当靠前）。
                    target.InsertRange(0, drained);

                    // 缓冲有上限：超了先丢最旧的（见 MaxQueuedSamplesPerAgent 的注释）。
                    var overflow = target.Count - MaxQueuedSamplesPerAgent;
                    if (overflow > 0) target.RemoveRange(0, overflow);
                }
            }
        }
    }

    /// <summary>取出"自上次推送以来有新遥测"的主机 Id（并清空标记）。</summary>
    public IReadOnlyList<int> TakeDirtyAgents()
    {
        if (_dirty.IsEmpty) return Array.Empty<int>();

        var result = new List<int>(_dirty.Count);

        foreach (var key in _dirty.Keys.ToList())
        {
            if (_dirty.TryRemove(key, out _)) result.Add(key);
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════
    // 查询（网页调用）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 组装一段时序。
    /// <para>
    /// <b>分层取值</b>（方案 §6.4）：窗口较短时读 5 秒明细并降采样；
    /// 太长时（明细行数会超上限）<b>老的那一段读日聚合、最近的一段仍读明细</b> ——
    /// 直接读明细会因为行数上限被静默截断，表现是"图只画到一半"。
    /// </para>
    /// </summary>
    public async Task<AgentMetricsSeriesDto> QuerySeriesAsync(
        int agentId,
        DateTimeOffset from,
        DateTimeOffset to,
        int? bucketSeconds,
        int? maxPoints,
        CancellationToken cancellationToken = default)
    {
        var options = Options;

        if (to <= from) to = from.AddMinutes(1);

        // ⚠️ 把窗口**归一化到 +08:00 再回显**：前端有一处按"截 ISO 字符串"来显示的约定
        // （`fmtDateTime`，见 format.ts），它假定所有时间戳都是 +08:00 形态。
        // 如果原样回传客户端用 `new Date().toISOString()` 算出来的 `...Z`（UTC）窗口，
        // 图表的横轴就会整体少显示 8 小时 —— 而这种错"看着只是时间不对"，
        // 排查成本极高。归一化放在这里，任何调用方都不会踩到。
        from = BeijingTime.ToBeijing(from);
        to = BeijingTime.ToBeijing(to);

        var span = to - from;
        var bucket = bucketSeconds is > 0
            ? bucketSeconds.Value
            : AgentMetricsAggregator.ChooseBucketSeconds(span, maxPoints ?? AgentMetricsAggregator.MaxChartPoints);

        // 纯明细能覆盖多长窗口：行数上限 ÷ 采样密度。
        var metricsSeconds = Math.Max(1, options.MetricsSeconds);
        var rawSpan = TimeSpan.FromSeconds((double)MaxRawRows * metricsSeconds);

        var rawFrom = to - span > to - rawSpan ? to - rawSpan : from;

        // ── 空白（客户端离线）在曲线上必须留空（2026-10-16）──────────────────
        //
        // ⚠️ 下面两处 `MarkGaps` 是**同一条需求**的两半，别只留一半：
        //    空桶本来就被跳过（不补 0），但"跳过"只让中间没有点，**折线仍会跨过去连成直线**。
        //    插一个全 null 的标记点，前端那套"null 必须断开折线"才会真的把它断开。
        //    判据（多久算离线）与在线表的灰点同源，见 AgentMetricsAggregator.GapThreshold。
        var offlineAfter = TimeSpan.FromSeconds(Math.Max(1, _options.Value.Agent.OfflineAfterSeconds));
        var step = TimeSpan.FromSeconds(bucket);
        var gapThresholdSeconds = (int)AgentMetricsAggregator.GapThreshold(step, offlineAfter).TotalSeconds;

        if (rawFrom <= from)
        {
            var samples = await _store.QueryMetricsAsync(agentId, from, to, MaxRawRows, cancellationToken).ConfigureAwait(false);
            var points = AgentMetricsAggregator.Bucketize(samples, from, to, bucket);

            return new AgentMetricsSeriesDto(
                agentId,
                from,
                to,
                bucket,
                "raw",
                samples.Count,
                AgentMetricsAggregator.MarkGaps(points, step, offlineAfter),
                gapThresholdSeconds);
        }

        // 老的一段：日聚合。
        var offset = BeijingTime.Offset;
        var dailyFrom = DateOnly.FromDateTime(from.ToOffset(offset).DateTime);
        var dailyTo = DateOnly.FromDateTime(rawFrom.ToOffset(offset).DateTime).AddDays(-1);

        var daily = dailyTo >= dailyFrom
            ? await _store.QueryDailyAsync(agentId, dailyFrom, dailyTo, cancellationToken).ConfigureAwait(false)
            : Array.Empty<Core.Models.AgentMetricsDaily>();

        var recent = await _store.QueryMetricsAsync(agentId, rawFrom, to, MaxRawRows, cancellationToken).ConfigureAwait(false);

        // ⚠️ 两段**各判各的空白**（判据与"接缝不算离线"的理由都在 `MergeLayers` 里）：
        //    日聚合那段的点落在"每天 00:00"，明细段从 rawFrom 起，接缝两侧天然隔了将近一天。
        var merged = AgentMetricsAggregator.MergeLayers(
            AgentMetricsAggregator.DailyToPoints(daily),
            AgentMetricsAggregator.Bucketize(recent, rawFrom, to, bucket),
            TimeSpan.FromDays(1),
            step,
            offlineAfter);

        return new AgentMetricsSeriesDto(
            agentId,
            from,
            to,
            bucket,
            "daily+raw",
            daily.Count + recent.Count,
            merged,
            gapThresholdSeconds);
    }

    /// <summary>取一台机器的开关机记录（列表 + 统计）。</summary>
    public async Task<AgentPowerEventListDto> QueryPowerEventsAsync(
        int agentId,
        DateTimeOffset? from,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var items = await _store
            .QueryPowerEventsAsync(agentId, from, null, limit, cancellationToken)
            .ConfigureAwait(false);

        // 统计按"全部历史"算（数据量小），而不是当前这一页 ——
        // 否则翻到第 2 页，"意外关机次数"就变了。
        var all = await _store.QueryAllPowerEventsAsync(agentId, cancellationToken).ConfigureAwait(false);

        return new AgentPowerEventListDto(
            agentId,
            items,
            all.Count,
            AgentMetricsAggregator.SummarizePower(all));
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    private void Enqueue(int agentId, List<AgentMetricsSampleDto> samples)
    {
        var list = _pending.GetOrAdd(agentId, _ => new List<AgentMetricsSampleDto>());

        lock (list)
        {
            list.AddRange(samples);

            var overflow = list.Count - MaxQueuedSamplesPerAgent;
            if (overflow > 0) list.RemoveRange(0, overflow);
        }
    }

    /// <summary>
    /// 逐设备读数的服务端清洗（2026-10-13）。
    ///
    /// <para>
    /// <b>为什么要过一遍</b>：这些值会<b>原样落进数据库并被画成曲线</b>，
    /// 而客户机是现场机器上跑的、版本参差不齐。一条 <c>Kind</c> 拼错的记录会让页面上
    /// 多出一个永远没有曲线的分组；同一个 <c>Key</c> 出现两次则会让曲线自相矛盾
    /// （同一时刻两个不同的值）。这些都不是"数据多了一点"，而是"图错了"。
    /// </para>
    ///
    /// <para>
    /// 与整机指标那条路径的取舍保持一致：<b>这里不整批拒收</b>。
    /// 逐设备明细是"锦上添花"，为了一条坏记录把整帧的 CPU / 内存都丢掉是不划算的。
    /// </para>
    /// </summary>
    private static AgentMetricsSampleDto SanitizeDevices(AgentMetricsSampleDto sample)
    {
        if (sample.Devices is not { Count: > 0 }) return sample with { Devices = null };

        var cleaned = new List<AgentDeviceReadingDto>(Math.Min(sample.Devices.Count, MaxDevicesPerSample));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var device in sample.Devices)
        {
            if (cleaned.Count >= MaxDevicesPerSample) break;

            if (!AgentDeviceKinds.IsValid(device.Kind)) continue;
            if (string.IsNullOrWhiteSpace(device.Key)) continue;

            // 同一帧里同一个设备只能有一条 —— 两条同 Key 记录落库后，前端聚合时
            // "取哪一条"取决于遍历顺序，那是一条会自己变来变去的曲线。
            if (!seen.Add($"{device.Kind}\u0001{device.Key}")) continue;

            cleaned.Add(device with
            {
                Key = Clamp(device.Key, 128),
                Name = Clamp(device.Name, 128),

                // 🔥 非有限值（NaN / ±∞）一律折成 null（2026-10-13 补）。
                //
                // 与温度的 `AgentMetricsAggregator.Temperature` 是同一条纪律，理由也一样 ——
                // 而且这里**更容易出事**：逐设备读数会随"最新一帧摘要"进入在线表，
                // 而 `GET /api/agents` 用的是 System.Text.Json 的严格模式，**默认拒绝 NaN/Infinity**。
                // 也就是说：一个坏读数原本只毁它自己那条曲线，现在能**让整张在线表序列化失败** ——
                // 一台坏客户端拖垮所有人的列表页。
                //
                // ⚠️ 只丢"非有限"，**不做物理区间判断**：CPU/GPU 是 0–100，但读写速率与上下行
                //    没有有意义的上下界（几十字节/秒到几十 GB/秒都合法），套一个区间只会误伤真实数据。
                CpuPct = Finite(device.CpuPct),
                ReadBps = Finite(device.ReadBps),
                WriteBps = Finite(device.WriteBps),
                UpBps = Finite(device.UpBps),
                DownBps = Finite(device.DownBps),
                GpuPct = Finite(device.GpuPct),
            });
        }

        return sample with { Devices = cleaned.Count > 0 ? cleaned : null };
    }

    /// <summary>非有限值（NaN / ±∞）折成 null —— 见 <see cref="SanitizeDevices"/> 里的说明。</summary>
    private static double? Finite(double? value)
        => value is { } v && double.IsFinite(v) ? v : null;

    private static string Clamp(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        return value.Length <= max ? value : value[..max];
    }
}
