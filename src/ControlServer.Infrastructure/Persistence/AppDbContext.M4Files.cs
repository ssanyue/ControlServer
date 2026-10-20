using ControlServer.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// <see cref="AppDbContext"/> 的 M4.4 增量部分（文件仓库 + 分发任务）。
/// <para>与 M4 / M4.2 的分部文件分开，理由一致：生成"只含前序表"的迁移时可以整体摘除。</para>
/// </summary>
public sealed partial class AppDbContext
{
    /// <summary>仓库文件元数据（M4.4）。</summary>
    public DbSet<FileRepoItemEntity> FileRepoItems => Set<FileRepoItemEntity>();

    /// <summary>分块上传会话（M4.4）。</summary>
    public DbSet<FileUploadSessionEntity> FileUploadSessions => Set<FileUploadSessionEntity>();

    /// <summary>分发任务（M4.4）。</summary>
    public DbSet<DistributionTaskEntity> DistributionTasks => Set<DistributionTaskEntity>();

    /// <summary>分发任务在每台机器上的明细（M4.4）。</summary>
    public DbSet<DistributionItemEntity> DistributionItems => Set<DistributionItemEntity>();

    partial void OnModelCreatingM4Files(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileRepoItemEntity>(e =>
        {
            e.ToTable("file_repo_items");
            e.HasKey(x => x.Id);

            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.StoredName).HasMaxLength(128).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            e.Property(x => x.UploadedBy).HasMaxLength(128);
            e.Property(x => x.Note).HasMaxLength(512);

            // StoredName 必须唯一：它是磁盘上的真实名，重了就意味着两个条目指向同一个文件。
            // 生成规则是 GUID，理论上不会重，但让数据库把这条不变量钉死比"相信生成器"更可靠。
            e.HasIndex(x => x.StoredName).IsUnique();

            // 列表按上传时间倒序 + 按类别过滤（"客户机回传"是常用筛选）。
            e.HasIndex(x => new { x.Kind, x.UploadedAtMs });
            e.HasIndex(x => x.OriginAgentId);
        });

        modelBuilder.Entity<FileUploadSessionEntity>(e =>
        {
            e.ToTable("file_upload_sessions");
            e.HasKey(x => x.Id);

            e.Property(x => x.Id).HasMaxLength(40);
            e.Property(x => x.FileName).HasMaxLength(256).IsRequired();
            e.Property(x => x.StoredName).HasMaxLength(128).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.CreatedBy).HasMaxLength(128);
            e.Property(x => x.State).HasMaxLength(16).IsRequired();

            // 清理任务要按"过期时刻"扫，所以给它一条索引。
            e.HasIndex(x => x.ExpiresAtMs);
        });

        modelBuilder.Entity<DistributionTaskEntity>(e =>
        {
            e.ToTable("distribution_tasks");
            e.HasKey(x => x.Id);

            e.Property(x => x.Direction).HasMaxLength(16).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(256).IsRequired();
            e.Property(x => x.State).HasMaxLength(16).IsRequired();
            e.Property(x => x.TargetDir).HasMaxLength(512);
            e.Property(x => x.SourcePath).HasMaxLength(1024);
            e.Property(x => x.Note).HasMaxLength(512);
            e.Property(x => x.CreatedBy).HasMaxLength(128);

            // 任务列表按创建时间倒序。
            e.HasIndex(x => x.CreatedAtMs);
        });

        modelBuilder.Entity<DistributionItemEntity>(e =>
        {
            e.ToTable("distribution_items");
            e.HasKey(x => x.Id);

            e.Property(x => x.State).HasMaxLength(16).IsRequired();
            e.Property(x => x.Error).HasMaxLength(1024);
            e.Property(x => x.Sha256).HasMaxLength(64);
            // 每台各自的落地目录（2026-10-13 起）。长度与任务级的 TargetDir 保持一致。
            e.Property(x => x.TargetDir).HasMaxLength(512);

            // ① 一个任务里一台机器只能有一行 —— 重复派发会变成"传两遍"。
            // ② 客户端上线补领任务是按 AgentId 查"还没完成的"，所以这条索引必须是 (AgentId, State) 起头。
            e.HasIndex(x => new { x.TaskId, x.AgentId }).IsUnique();
            e.HasIndex(x => new { x.AgentId, x.State });
        });
    }
}
