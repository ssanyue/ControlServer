using ControlServer.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// <see cref="AppDbContext"/> 的 M2 增量部分（配置文档表 + 定时触发表）。
/// <para>
/// ⚠️ 这个文件被刻意独立出来：生成 M1 基线迁移时它会被临时改名（<c>.off</c>）整体摘除，
/// 使基线快照恰好等于线上 M1 库的结构。**改名操作只用于生成迁移，运行期一定存在。**
/// </para>
/// </summary>
public sealed partial class AppDbContext
{
    /// <summary>
    /// 配置文档（M2 新增）。整份配置以 JSON 存一行，带版本号做乐观锁 —— 见 M2 方案 §4.2。
    /// </summary>
    public DbSet<ConfigDocument> ConfigDocuments => Set<ConfigDocument>();

    /// <summary>
    /// 定时触发记录（M2-D 新增）。复合主键即幂等键 —— 见 M2 方案 §5.4。
    /// </summary>
    public DbSet<TimerFireRecord> TimerFires => Set<TimerFireRecord>();

    partial void OnModelCreatingM2(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConfigDocument>(e =>
        {
            e.ToTable("config_documents");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Value).IsRequired();
            e.Property(x => x.Version).IsRequired();
            e.Property(x => x.UpdatedBy).HasMaxLength(64);
        });

        modelBuilder.Entity<TimerFireRecord>(e =>
        {
            e.ToTable("timer_fires");

            // 复合主键 = 幂等键。由数据库保证"同一触发点只写一次"，
            // 调度器因此不需要任何内存去重状态。
            e.HasKey(x => new { x.RefId, x.FireDate, x.FireTime });

            e.Property(x => x.RefId).HasMaxLength(128);
            e.Property(x => x.FireDate).HasMaxLength(10);
            e.Property(x => x.FireTime).HasMaxLength(8);
            e.Property(x => x.Source).HasMaxLength(32);

            // 按日期裁剪 / 扫描是唯一的批量访问模式。
            e.HasIndex(x => x.FireDate);
        });
    }
}
