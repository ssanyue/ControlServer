using ControlServer.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// <see cref="AppDbContext"/> 的 M4.2 增量部分（客户端遥测：指标明细 / 日聚合 / 开关机 / 硬件清单）。
/// <para>
/// 与 <c>AppDbContext.M4.cs</c>（在线表 + 令牌）分开成两个 partial 文件，理由与 M2 那层一样：
/// 生成"只含前序表"的迁移时能整体摘除，不靠"记得排除"。
/// </para>
/// </summary>
public sealed partial class AppDbContext
{
    /// <summary>5 秒粒度的指标明细（M4.2）。</summary>
    public DbSet<AgentMetricEntity> AgentMetrics => Set<AgentMetricEntity>();

    /// <summary>按天聚合的指标（M4.2）。</summary>
    public DbSet<AgentMetricsDailyEntity> AgentMetricsDaily => Set<AgentMetricsDailyEntity>();

    /// <summary>开关机 / 蓝屏记录（M4.2）。</summary>
    public DbSet<AgentPowerEventEntity> AgentPowerEvents => Set<AgentPowerEventEntity>();

    /// <summary>最新硬件清单（M4.2）。</summary>
    public DbSet<AgentInventoryEntity> AgentInventories => Set<AgentInventoryEntity>();

    partial void OnModelCreatingM4Telemetry(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentMetricEntity>(e =>
        {
            e.ToTable("agent_metrics");
            e.HasKey(x => x.Id);

            // ① 去重：客户端断线重连后会重发攒下的批次，"重连"在内网里一天发生几十次，
            //    没有这条唯一索引，库里会成片出现重复采样点，曲线会出现锯齿。
            // ② 查询：几乎每一次读都是"某台机器在某个时间段"，因此 (AgentId, AtMs) 必须连在一起做索引。
            //    ⚠️ 排序列用的是 AtMs（Unix 毫秒）而不是 DateTimeOffset —— 见 TelemetryTime 的注释。
            e.HasIndex(x => new { x.AgentId, x.AtMs }).IsUnique();

            // 逐设备读数（M4.8）：不设 HasMaxLength —— SQLite 里 TEXT 本来就是无长度的，
            // 写一个假的"上限"只会让人以为超过会被截断（实际不会）。真正的上限在写入侧把守：
            // 服务端清洗时限制设备条数与单串长度（AgentTelemetryService / TelemetryCollector 的同名常量）。
            e.Property(x => x.DevicesJson);
        });

        modelBuilder.Entity<AgentMetricsDailyEntity>(e =>
        {
            e.ToTable("agent_metrics_daily");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AgentId, x.Day }).IsUnique();
        });

        modelBuilder.Entity<AgentPowerEventEntity>(e =>
        {
            e.ToTable("agent_power_events");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AgentId, x.AtMs, x.EventId }).IsUnique();

            e.Property(x => x.Kind).HasMaxLength(32).IsRequired();
            e.Property(x => x.Actor).HasMaxLength(160);
            e.Property(x => x.Detail).HasMaxLength(512);
        });

        modelBuilder.Entity<AgentInventoryEntity>(e =>
        {
            e.ToTable("agent_inventories");

            // 主键就是 AgentId：只留最新一份（型号 / 序列号不会变，留历史只会让库无限膨胀）。
            e.HasKey(x => x.AgentId);
            e.Property(x => x.Json).IsRequired();
        });
    }
}
