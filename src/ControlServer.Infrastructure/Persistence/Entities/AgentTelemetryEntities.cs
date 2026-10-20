namespace ControlServer.Infrastructure.Persistence.Entities;

/// <summary>
/// 一条 5 秒粒度的指标明细（M4.2 温层）。
/// <para>
/// <b>唯一的写入压力点</b>：300 台 × 5 秒 = 60 行/秒。这条路必须走"客户端攒批 + 服务端内存暂存 +
/// 定时成批落盘"，否则 SQLite 的 WAL 会被这些小行写满，而"改个备注"这种真正的写操作会被挤在后面。
/// </para>
/// <para>
/// ⚠️ <b>时间戳存 Unix 毫秒（<see cref="AtMs"/>），不是 <c>DateTimeOffset</c>。</b>
/// 原因见 <c>TelemetryTime</c> 的注释（SQLite 不支持 <c>DateTimeOffset</c> 的 ORDER BY）。
/// 转换只经 <c>TelemetryTime</c> 一处，实体层不碰 <c>DateTimeOffset</c>。
/// </para>
/// </summary>
public sealed class AgentMetricEntity
{
    public long Id { get; set; }

    /// <summary>在线表主键（<c>agent_hosts.id</c>）。不建外键：移除设备时我们<b>主动</b>清理遥测，
    /// 而不希望在"删设备"这一步被外键约束挡住（那会让移除设备变成一个可能失败的复杂操作）。</summary>
    public int AgentId { get; set; }

    /// <summary>采样时刻（Unix 毫秒；由客户端本机钟打的北京时间换算而来）。</summary>
    public long AtMs { get; set; }

    public double? CpuPct { get; set; }

    public long? MemUsedMb { get; set; }

    public long? MemTotalMb { get; set; }

    public double? DiskReadBps { get; set; }

    public double? DiskWriteBps { get; set; }

    public double? NetUpBps { get; set; }

    public double? NetDownBps { get; set; }

    public double? GpuPct { get; set; }

    public long? UptimeSeconds { get; set; }

    // ── 温度（M4.3，可选能力）──
    //
    // ⚠️ 在 SQLite 里**可空 REAL 列几乎不占空间**（NULL 在记录头里只占 1 字节类型码），
    //    所以"没启用温度监控的机器整天是 NULL"不会带来可观测的膨胀。
    //    真正要盯的是行数（300 台 × 5 秒），而不是列数。
    /// <summary>CPU 温度（摄氏度）；未启用 / 驱动失败 / 取不到时为 null。<b>绝不用 0 代替。</b></summary>
    public double? CpuTempC { get; set; }

    /// <summary>GPU 温度（摄氏度）；同上。</summary>
    public double? GpuTempC { get; set; }

    /// <summary>物理磁盘温度（摄氏度，多块取最高的那块）；同上。</summary>
    public double? StorageTempC { get; set; }

    /// <summary>
    /// 逐设备读数（M4.8，2026-10-13）：一个 JSON 数组，元素形状同
    /// <c>AgentDeviceReadingDto</c>；没有逐设备数据时为 null（<b>不是空串</b>）。
    /// <para>
    /// <b>为什么塞成一列 JSON 而不是建一张 <c>agent_metric_devices</c> 子表</b>：
    /// 这张表 300 台 × 5 秒 = 60 行/秒，而子表会让每一行明细再乘上"这台机器有几块硬件"
    /// （典型 5–10 个）。那意味着<b>写入放大 5–10 倍</b>、索引页数成倍增长，
    /// 而这个数据的查法只有一种 —— "把某个时间段这批行原样取回来画图"，
    /// 从来不需要"只查第 3 块硬盘"这种关系式查询。JSON 列正好匹配这种用法。
    /// </para>
    /// <para>
    /// ⚠️ <b>代价是"库里查不了逐设备"</b>：任何"按硬盘分组统计"的 SQL 都做不到，
    /// 只能在取回后于内存里算（<c>AgentMetricsAggregator</c> 就是这么做的）。
    /// 若将来真需要 SQL 侧聚合，再补子表 —— 别现在为想象中的需求付写入代价。
    /// </para>
    /// <para>
    /// ⚠️ 老客户端（不发逐设备）与"这一帧没采到"都写 null，两者在数据上<b>不可区分</b>，
    /// 这是刻意的：页面上的结论都是"该时段没有逐设备数据"，不必再分辨原因。
    /// </para>
    /// </summary>
    public string? DevicesJson { get; set; }
}

/// <summary>
/// 一天一行的遥测聚合（M4.2 冷层，保留 1 年）。
/// <para>由后台任务按"昨天的原始明细"重算，因此<b>可以随时用同一批明细重建</b> ——
/// 口径改了就把历史重跑一遍，不需要客户端配合。</para>
/// </summary>
public sealed class AgentMetricsDailyEntity
{
    public long Id { get; set; }

    public int AgentId { get; set; }

    /// <summary>哪一天（北京时间）。</summary>
    public DateOnly Day { get; set; }

    public int SampleCount { get; set; }

    /// <summary>当天在线秒数（估算：采样数 × 采样间隔，上限 86400）。</summary>
    public int OnlineSeconds { get; set; }

    public double? CpuAvgPct { get; set; }

    public double? CpuMaxPct { get; set; }

    public long? MemAvgMb { get; set; }

    public long? MemMaxMb { get; set; }

    public long? MemTotalMb { get; set; }

    public double? NetDownAvgBps { get; set; }

    public double? NetUpAvgBps { get; set; }

    public double? DiskReadAvgBps { get; set; }

    public double? DiskWriteAvgBps { get; set; }

    public double? GpuAvgPct { get; set; }

    // ── 温度（M4.3）──
    // 与"CPU 有均值+峰值、GPU 只有均值"的既有口径保持一致：CPU 温度是运维最常问的那个,
    // 而"这台机器最高烧到多少度"只有峰值能回答。
    public double? CpuTempAvgC { get; set; }

    public double? CpuTempMaxC { get; set; }

    public double? GpuTempAvgC { get; set; }

    public double? StorageTempAvgC { get; set; }
}

/// <summary>
/// 一条开关机 / 蓝屏记录（M4.2 事件层，<b>永久保留</b>）。
/// <para>数据量小（一台机器一天最多几条），而"这台机器这个月非正常关机了几次"是长期都要能回答的问题。</para>
/// </summary>
public sealed class AgentPowerEventEntity
{
    public long Id { get; set; }

    public int AgentId { get; set; }

    /// <summary>事件时刻（Unix 毫秒；理由同 <see cref="AgentMetricEntity.AtMs"/>）。</summary>
    public long AtMs { get; set; }

    /// <summary>种类，见 <c>AgentPowerEventKinds</c>。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// 原始 Windows 事件 ID。
    /// <para>⚠️ <b>刻意是可空 DTO 里的 null → 0</b>：SQLite 的唯一索引把 NULL 视为"互不相同"，
    /// 若允许 null，同一事件的重复上报会各插一行，去重就失效了。</para>
    /// </summary>
    public int EventId { get; set; }

    public string? Actor { get; set; }

    public string? Detail { get; set; }
}

/// <summary>
/// 一台机器最新一份硬件清单（M4.2）。主键就是 AgentId —— 只留最新一份。
/// </summary>
public sealed class AgentInventoryEntity
{
    public int AgentId { get; set; }

    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>清单 JSON（形状随契约演进；读不出来时页面显示"清单缺失"，不影响其它功能）。</summary>
    public string Json { get; set; } = string.Empty;
}
