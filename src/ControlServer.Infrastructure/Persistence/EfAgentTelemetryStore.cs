using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using ControlServer.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 基于 EF Core / SQLite 的遥测存储（M4.2）。
/// <para>
/// 与 <c>EfAgentHostStore</c> 同一模式：<b>Singleton + 内部开 scope 拿 Scoped 的 <c>DbContext</c></b>。
/// </para>
/// <para>
/// <b>写路径串行化</b>：这里不像注册那样存在"先查后插"的并发争抢（每个 Agent 的批次由它自己按序发），
/// 但一条 <see cref="SemaphoreSlim"/> 仍然比"偶发 SQLITE_BUSY"便宜 —— 落盘是后台任务，
/// 排队几十毫秒没人会察觉，而一次 BUSY 就是一批数据丢失。
/// </para>
/// </summary>
public sealed class EfAgentTelemetryStore : IAgentTelemetryStore
{
    /// <summary>一次查询最多返回多少行明细（防止一个"查一年"的请求把内存吃光）。</summary>
    private const int MaxQueryRows = 200_000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public EfAgentTelemetryStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    // ═══════════════════════════════════════════════════════════
    // 指标明细
    // ═══════════════════════════════════════════════════════════

    public async Task<int> AppendMetricsAsync(
        int agentId,
        IReadOnlyList<AgentMetricsSampleDto> samples,
        CancellationToken cancellationToken = default)
    {
        if (samples.Count == 0) return 0;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var times = samples.Select(s => TelemetryTime.ToMs(s.At)).ToList();
            var min = times.Min();
            var max = times.Max();

            // 去重：只查这一批覆盖的时间窗，而不是整张表。
            // ⚠️ 同一批里也可能自重复（客户端时钟回拨后重采），因此还要在内存里去一次。
            var existing = await db.AgentMetrics
                .Where(x => x.AgentId == agentId && x.AtMs >= min && x.AtMs <= max)
                .Select(x => x.AtMs)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var seen = existing.ToHashSet();
            var rows = new List<AgentMetricEntity>(samples.Count);

            foreach (var sample in samples)
            {
                var atMs = TelemetryTime.ToMs(sample.At);
                if (!seen.Add(atMs)) continue;

                rows.Add(new AgentMetricEntity
                {
                    AgentId = agentId,
                    AtMs = atMs,
                    CpuPct = Percent(sample.CpuPct),
                    MemUsedMb = NonNegative(sample.MemUsedMb),
                    MemTotalMb = NonNegative(sample.MemTotalMb),
                    DiskReadBps = NonNegative(sample.DiskReadBps),
                    DiskWriteBps = NonNegative(sample.DiskWriteBps),
                    NetUpBps = NonNegative(sample.NetUpBps),
                    NetDownBps = NonNegative(sample.NetDownBps),
                    GpuPct = Percent(sample.GpuPct),
                    UptimeSeconds = NonNegative(sample.UptimeSeconds),
                    // 净化统一走 Core 的那一份（见 AgentMetricsAggregator.Temperature）：
                    // 存储与"在线表摘要"必须用同一套规则，否则会出现"库里是 null、在线表是 NaN"。
                    CpuTempC = AgentMetricsAggregator.Temperature(sample.CpuTempC),
                    GpuTempC = AgentMetricsAggregator.Temperature(sample.GpuTempC),
                    StorageTempC = AgentMetricsAggregator.Temperature(sample.StorageTempC),
                    DevicesJson = SerializeDevices(sample.Devices),
                });
            }

            if (rows.Count == 0) return 0;

            db.AgentMetrics.AddRange(rows);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return rows.Count;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<AgentMetricsSampleDto>> QueryMetricsAsync(
        int agentId,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var take = limit <= 0 ? MaxQueryRows : Math.Min(limit, MaxQueryRows);

        var fromMs = TelemetryTime.ToMs(from);
        var toMs = TelemetryTime.ToMs(to);

        var rows = await db.AgentMetrics
            .AsNoTracking()
            .Where(x => x.AgentId == agentId && x.AtMs >= fromMs && x.AtMs <= toMs)
            .OrderBy(x => x.AtMs)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToSample).ToList();
    }

    public async Task<IReadOnlyList<AgentMetricsDaily>> QueryDailyAsync(
        int agentId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.AgentMetricsDaily
            .AsNoTracking()
            .Where(x => x.AgentId == agentId && x.Day >= from && x.Day <= to)
            .OrderBy(x => x.Day)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToDomain).ToList();
    }

    public async Task RebuildDailyAsync(
        DateOnly day,
        int sampleIntervalSeconds,
        CancellationToken cancellationToken = default)
    {
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), BeijingTime.Offset);
        var end = start.AddDays(1);

        var startMs = TelemetryTime.ToMs(start);
        var endMs = TelemetryTime.ToMs(end);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var agentIds = await db.AgentMetrics
                .Where(x => x.AtMs >= startMs && x.AtMs < endMs)
                .Select(x => x.AgentId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // 已经有明细的机器：逐台重算（一天 288 条 × 6 = 17280 条/台，单台一次性加载是可接受的）。
            foreach (var agentId in agentIds)
            {
                var samples = await db.AgentMetrics
                    .AsNoTracking()
                    .Where(x => x.AgentId == agentId && x.AtMs >= startMs && x.AtMs < endMs)
                    .OrderBy(x => x.AtMs)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var dto = samples.Select(ToSample).ToList();
                var daily = AgentMetricsAggregator.AggregateDaily(day, dto, sampleIntervalSeconds);

                var row = await db.AgentMetricsDaily
                    .FirstOrDefaultAsync(x => x.AgentId == agentId && x.Day == day, cancellationToken)
                    .ConfigureAwait(false);

                if (row is null)
                {
                    row = new AgentMetricsDailyEntity { AgentId = agentId, Day = day };
                    db.AgentMetricsDaily.Add(row);
                }

                ApplyDaily(row, daily);
            }

            // 明细已经被清理掉、只剩日聚合残留的机器：把对不上的那行删掉，避免"线上有时长但那天其实没数据"。
            var stale = await db.AgentMetricsDaily
                .Where(x => x.Day == day && !agentIds.Contains(x.AgentId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (stale.Count > 0) db.AgentMetricsDaily.RemoveRange(stale);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> PurgeMetricsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoffMs = TelemetryTime.ToMs(cutoff);

        return await db.AgentMetrics
            .Where(x => x.AtMs < cutoffMs)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> PurgeDailyAsync(DateOnly cutoffDay, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.AgentMetricsDaily
            .Where(x => x.Day < cutoffDay)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<int, AgentMetricsSummaryDto>> GetLatestSummariesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<int, AgentMetricsSummaryDto>();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var sinceMs = TelemetryTime.ToMs(BeijingTime.Now.AddHours(-2));

        // 只扫最近两小时：在线机器的"最新一帧"必定在这段窗口里；
        // 离线更久的机器不显示实时值（它本来就没有"此刻"），详情页要看历史走时序端点。
        //
        // ⚠️ **分组聚合必须在库里做**。早先的写法是把这两小时的行全拉回来再内存 GroupBy ——
        //    按目标规模（300 台 × 5 秒 = 60 行/秒）那是 43 万行 / 次，
        //    而"每次刷新在线表"都会跑一遍。这里下推成 SQL 的 MAX，
        //    正文只按命中的精确键取回一行（走 (AgentId, AtMs) 索引）。
        var latestPerAgent = await db.AgentMetrics
            .AsNoTracking()
            .Where(x => x.AtMs >= sinceMs)
            .GroupBy(x => x.AgentId)
            .Select(g => new { AgentId = g.Key, AtMs = g.Max(x => x.AtMs) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in latestPerAgent)
        {
            var row = await db.AgentMetrics
                .AsNoTracking()
                .Where(x => x.AgentId == item.AgentId && x.AtMs == item.AtMs)
                .OrderBy(x => x.Id)   // 同一毫秒理论上可能有两条（客户端时钟回拨后重采），取先写入的那条
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (row is null) continue;

            var summary = AgentMetricsAggregator.Summarize(ToSample(row));

            if (summary is not null) result[item.AgentId] = summary;
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════
    // 开关机事件
    // ═══════════════════════════════════════════════════════════

    public async Task<int> AppendPowerEventsAsync(
        int agentId,
        IReadOnlyList<AgentPowerEventDto> events,
        CancellationToken cancellationToken = default)
    {
        if (events.Count == 0) return 0;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var times = events.Select(e => TelemetryTime.ToMs(e.At)).ToList();
            var min = times.Min();
            var max = times.Max();

            var existing = await db.AgentPowerEvents
                .Where(x => x.AgentId == agentId && x.AtMs >= min && x.AtMs <= max)
                .Select(x => new { x.AtMs, x.EventId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var seen = existing.Select(x => (x.AtMs, x.EventId)).ToHashSet();
            var rows = new List<AgentPowerEventEntity>(events.Count);

            foreach (var e in events)
            {
                if (!AgentPowerEventKinds.IsValid(e.Kind)) continue;

                var atMs = TelemetryTime.ToMs(e.At);
                var eventId = e.EventId ?? 0;

                if (!seen.Add((atMs, eventId))) continue;

                rows.Add(new AgentPowerEventEntity
                {
                    AgentId = agentId,
                    AtMs = atMs,
                    Kind = e.Kind,
                    EventId = eventId,
                    Actor = TrimText(e.Actor, 160),
                    Detail = TrimText(e.Detail, 512),
                });
            }

            if (rows.Count == 0) return 0;

            db.AgentPowerEvents.AddRange(rows);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return rows.Count;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<AgentPowerEventDto>> QueryPowerEventsAsync(
        int agentId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var query = db.AgentPowerEvents.AsNoTracking().Where(x => x.AgentId == agentId);

        if (from is { } start) query = query.Where(x => x.AtMs >= TelemetryTime.ToMs(start));
        if (to is { } end) query = query.Where(x => x.AtMs <= TelemetryTime.ToMs(end));

        var rows = await query
            .OrderByDescending(x => x.AtMs)
            .ThenByDescending(x => x.Id)
            .Take(limit <= 0 ? 500 : Math.Min(limit, 5000))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<AgentPowerEventDto>> QueryAllPowerEventsAsync(
        int agentId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.AgentPowerEvents
            .AsNoTracking()
            .Where(x => x.AgentId == agentId)
            .OrderBy(x => x.AtMs)
            .ThenBy(x => x.Id)
            .Take(MaxQueryRows)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToDto).ToList();
    }

    // ═══════════════════════════════════════════════════════════
    // 硬件清单
    // ═══════════════════════════════════════════════════════════

    public async Task<AgentInventoryRecord?> GetInventoryAsync(
        int agentId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.AgentInventories
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AgentId == agentId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null) return null;

        // 一列坏 JSON 不该让详情页整页打不开：解不出来就当"清单缺失"。
        AgentInventoryDto? inventory = null;

        try
        {
            inventory = ControlJson.Deserialize<AgentInventoryDto>(row.Json);
        }
        catch (System.Text.Json.JsonException)
        {
            inventory = null;
        }

        return new AgentInventoryRecord(agentId, row.CapturedAt, inventory);
    }

    public async Task SaveInventoryAsync(
        int agentId,
        AgentInventoryDto inventory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = await db.AgentInventories
                .FirstOrDefaultAsync(x => x.AgentId == agentId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                row = new AgentInventoryEntity { AgentId = agentId };
                db.AgentInventories.Add(row);
            }

            row.CapturedAt = Trim(inventory.CapturedAt);
            row.Json = ControlJson.Serialize(inventory);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 移除设备时清理
    // ═══════════════════════════════════════════════════════════
    public async Task PurgeAgentAsync(int agentId, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await db.AgentMetrics.Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.AgentMetricsDaily.Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.AgentPowerEvents.Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.AgentInventories.Where(x => x.AgentId == agentId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 映射 / 清洗
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 时间戳规范化到毫秒。
    /// <para>
    /// <b>为什么必须截断</b>：去重键是"同一台机器 + 同一毫秒"，而客户端两次发同一条样本时
    /// 小数位的尾数必须完全一致才判得出"同一条"。截到毫秒既远高于采样精度（5 秒），
    /// 又能把序列化带来的微小差异抹平。
    /// </para>
    /// <para>
    /// ⚠️ 遥测明细 / 事件表的时间戳走 <see cref="TelemetryTime.ToMs"/>（<c>long</c>，同一套截断）；
    /// 本方法只服务于仍以 <c>DateTimeOffset</c> 落库的硬件清单。
    /// </para>
    /// </summary>
    private static DateTimeOffset Trim(DateTimeOffset value)
        => new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), value.Offset);

    private static double? Percent(double? value)
        => value is { } v ? Math.Clamp(v, 0, 100) : null;

    // ── 逐设备读数（M4.8）──
    //
    // 这一对是"库 ↔ DTO"的边界，纪律与其它字段一致：**坏数据不能炸掉整批**。
    // 序列化失败 → 丢这一帧的逐设备（整机数据照写）；反序列化失败 → 当"没有逐设备"。

    /// <summary>逐设备读数一次最多存几条。<b>与客户端 / 服务端清洗的同名常量同值</b>，改动要一起改。</summary>
    private const int MaxDevicesPerSample = 64;

    private static string? SerializeDevices(IReadOnlyList<AgentDeviceReadingDto>? devices)
    {
        if (devices is null || devices.Count == 0) return null;

        // 兜底截断：正常路径上 AgentTelemetryService 已经洗过一遍，这里是"万一别的调用方直接喂"。
        var list = devices.Count <= MaxDevicesPerSample
            ? devices
            : devices.Take(MaxDevicesPerSample).ToList();

        try
        {
            return ControlJson.SerializeCompact(list);
        }
        catch (System.Text.Json.JsonException)
        {
            // 宁可丢掉这一帧的逐设备数据，也不要让整批样本写不进去 ——
            // 整机指标（CPU/内存/磁盘合计）比"哪块硬盘在忙"重要得多。
            return null;
        }
    }

    private static IReadOnlyList<AgentDeviceReadingDto>? DeserializeDevices(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var list = ControlJson.DeserializeCompact<List<AgentDeviceReadingDto>>(json);

            return list is { Count: > 0 } ? list : null;
        }
        catch (System.Text.Json.JsonException)
        {
            // 一列坏 JSON 不该让整段历史打不开（与 GetInventoryAsync 同一个取舍）。
            return null;
        }
    }

    private static long? NonNegative(long? value) => value is { } v && v >= 0 ? v : null;

    private static double? NonNegative(double? value) => value is { } v && v >= 0 && !double.IsNaN(v) && !double.IsInfinity(v) ? v : null;

    private static string? TrimText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static AgentMetricsSampleDto ToSample(AgentMetricEntity row)
        => new(
            TelemetryTime.FromMs(row.AtMs),
            row.CpuPct,
            row.MemUsedMb,
            row.MemTotalMb,
            row.DiskReadBps,
            row.DiskWriteBps,
            row.NetUpBps,
            row.NetDownBps,
            row.GpuPct,
            row.UptimeSeconds,
            row.CpuTempC,
            row.GpuTempC,
            row.StorageTempC,
            DeserializeDevices(row.DevicesJson));

    private static AgentPowerEventDto ToDto(AgentPowerEventEntity row)
        => new(
            TelemetryTime.FromMs(row.AtMs),
            row.Kind,
            row.EventId == 0 ? null : row.EventId,
            row.Actor,
            row.Detail);

    private static AgentMetricsDaily ToDomain(AgentMetricsDailyEntity row)
        => new(
            row.Day,
            row.SampleCount,
            row.OnlineSeconds,
            row.CpuAvgPct,
            row.CpuMaxPct,
            row.MemAvgMb,
            row.MemMaxMb,
            row.MemTotalMb,
            row.NetDownAvgBps,
            row.NetUpAvgBps,
            row.DiskReadAvgBps,
            row.DiskWriteAvgBps,
            row.GpuAvgPct,
            row.CpuTempAvgC,
            row.CpuTempMaxC,
            row.GpuTempAvgC,
            row.StorageTempAvgC);

    private static void ApplyDaily(AgentMetricsDailyEntity row, AgentMetricsDaily daily)
    {
        row.SampleCount = daily.SampleCount;
        row.OnlineSeconds = daily.OnlineSeconds;
        row.CpuAvgPct = daily.CpuAvgPct;
        row.CpuMaxPct = daily.CpuMaxPct;
        row.MemAvgMb = daily.MemAvgMb;
        row.MemMaxMb = daily.MemMaxMb;
        row.MemTotalMb = daily.MemTotalMb;
        row.NetDownAvgBps = daily.NetDownAvgBps;
        row.NetUpAvgBps = daily.NetUpAvgBps;
        row.DiskReadAvgBps = daily.DiskReadAvgBps;
        row.DiskWriteAvgBps = daily.DiskWriteAvgBps;
        row.GpuAvgPct = daily.GpuAvgPct;
        row.CpuTempAvgC = daily.CpuTempAvgC;
        row.CpuTempMaxC = daily.CpuTempMaxC;
        row.GpuTempAvgC = daily.GpuTempAvgC;
        row.StorageTempAvgC = daily.StorageTempAvgC;
    }
}
