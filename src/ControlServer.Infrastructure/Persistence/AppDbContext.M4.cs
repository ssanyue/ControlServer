using ControlServer.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// <see cref="AppDbContext"/> 的 M4 增量部分（客户端 Agent 在线表 + 注册令牌）。
/// <para>
/// 与 <c>AppDbContext.M2.cs</c> 同一做法：增量表单独一个 partial 文件，
/// 这样"生成只含历史表的基线迁移"时能整体摘除而不影响编译。
/// </para>
/// </summary>
public sealed partial class AppDbContext
{
    /// <summary>客户端主机在线表（M4.1 新增）。</summary>
    public DbSet<AgentHostEntity> AgentHosts => Set<AgentHostEntity>();

    /// <summary>
    /// 注册令牌 / 加入码（M4.1 新增）。
    /// <para>
    /// ⚠️ <b>2026-10-13 起没有任何读写方</b>（准入令牌被整体取消），
    /// 这里刻意保留映射只为让表结构与迁移快照保持一致 ——
    /// 详见 <see cref="EnrollmentTokenEntity"/> 的注释。
    /// </para>
    /// </summary>
    public DbSet<EnrollmentTokenEntity> EnrollmentTokens => Set<EnrollmentTokenEntity>();

    partial void OnModelCreatingM4(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentHostEntity>(e =>
        {
            e.ToTable("agent_hosts");
            e.HasKey(x => x.Id);

            // 唯一：一台机器只该有一行。并发注册时靠它兜住"插出两行"。
            e.HasIndex(x => x.MachineKey).IsUnique();
            e.Property(x => x.MachineKey).HasMaxLength(64).IsRequired();

            // 认证热路径：每次 Agent 连上来都要按哈希查。
            e.HasIndex(x => x.AgentKeyHash);

            e.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
            e.Property(x => x.Group).HasMaxLength(64);
            e.Property(x => x.Note).HasMaxLength(512);
            e.Property(x => x.AgentKeyHash).HasMaxLength(128);
            e.Property(x => x.AgentVersion).HasMaxLength(32);
            e.Property(x => x.LastAddress).HasMaxLength(64);
            e.Property(x => x.HostInfoJson);

            // 路径记忆（2026-10-13）：512 与客户机侧的白名单目录上限同量级，
            // 足够容纳 Windows 的长路径（`\\?\C:\…` 之外，普通路径 260 是极限）。
            e.Property(x => x.LastTargetDir).HasMaxLength(512);
            e.Property(x => x.LastSourceDir).HasMaxLength(512);
        });

        modelBuilder.Entity<EnrollmentTokenEntity>(e =>
        {
            e.ToTable("enrollment_tokens");
            e.HasKey(x => x.Id);

            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.Note).HasMaxLength(256);
        });
    }
}
