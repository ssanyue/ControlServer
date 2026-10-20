using ControlServer.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// <see cref="AppDbContext"/> 的 M4.5 增量部分（客户机配置）。
/// <para>与 M4 / M4.2 / M4.4 的分部文件分开，理由一致：生成"只含前序表"的迁移时可以整体摘除。</para>
/// </summary>
public sealed partial class AppDbContext
{
    /// <summary>客户机配置（M4.5）。</summary>
    public DbSet<AgentConfigEntity> AgentConfigs => Set<AgentConfigEntity>();

    partial void OnModelCreatingM45Config(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentConfigEntity>(e =>
        {
            e.ToTable("agent_configs");
            e.HasKey(x => x.Id);

            e.Property(x => x.Json).IsRequired();

            // ⚠️ **唯一索引，不是普通索引**：一台机器只能有一份配置。
            // 允许重复行的话，"读配置"变成"随便取一行"，而表现是
            // "保存成功了但机器行为没变" —— 会查很久。
            e.HasIndex(x => x.AgentId).IsUnique();
        });
    }
}
