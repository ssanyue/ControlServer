using ControlServer.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// EF Core / SQLite 上下文。
/// <para>
/// <b>M1 基线</b>：users / settings / audit_logs（见本文件）。
/// </para>
/// <para>
/// <b>M2 增量</b>：config_documents 等新表放在 <c>AppDbContext.M2.cs</c> 这个 partial 文件里
/// —— 这样生成"只含 M1 表"的基线迁移 <c>InitialCreate</c> 时，可以整体摘除该文件而不影响编译，
/// 保证基线快照与线上 M1 库的真实结构严丝合缝。
/// </para>
/// </summary>
public sealed partial class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<AppUser> Users => Set<AppUser>();

    public DbSet<Setting> Settings => Set<Setting>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserName).IsUnique();
            e.Property(x => x.UserName).HasMaxLength(64).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.Salt).HasMaxLength(128).IsRequired();
            e.Property(x => x.Role).HasMaxLength(32).IsRequired();

            // ── M3 增量列（账号生命周期：IsEnabled / LastLoginAt / FailedLoginCount / LockedUntil）──
            //
            // 这里**刻意不给新列写 `HasDefaultValue`**，与文件里其它列的处理方式不同：
            // EF 会把"带库端默认值"的属性在插入时按 <b>CLR 默认值哨兵</b> 跳过，
            // 而 <c>bool</c> 的哨兵是 <c>false</c> —— 于是「新建一个已被禁用的账号」会被
            // 静默省略该列、落库成库默认值 <c>true</c>，账号反而变成启用。
            // 存量行的默认值只在迁移脚本里给（见 Migrations/*_M3_UserAccount.cs）。
        });

        modelBuilder.Entity<Setting>(e =>
        {
            e.ToTable("settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
            e.Property(x => x.Value).HasMaxLength(4000);
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.User).HasMaxLength(64);
            e.Property(x => x.Action).HasMaxLength(128);
            e.Property(x => x.Detail).HasMaxLength(2000);
            e.Property(x => x.RemoteIp).HasMaxLength(64);
        });

        // M2 及后续增量表（实现见 AppDbContext.M2.cs）。
        OnModelCreatingM2(modelBuilder);

        // M4 增量表：客户端 Agent 在线表 + 注册令牌（实现见 AppDbContext.M4.cs）。
        OnModelCreatingM4(modelBuilder);

        // M4.2 增量表：遥测明细 / 日聚合 / 开关机记录 / 硬件清单（实现见 AppDbContext.M4Telemetry.cs）。
        OnModelCreatingM4Telemetry(modelBuilder);

        // M4.4 增量表：文件仓库 / 上传会话 / 分发任务与明细（实现见 AppDbContext.M4Files.cs）。
        OnModelCreatingM4Files(modelBuilder);

        // M4.5 增量表：客户机配置（三张任务清单 + UDP 通道，实现见 AppDbContext.M45Config.cs）。
        OnModelCreatingM45Config(modelBuilder);
    }

    /// <summary>M2 增量模型的挂载点。</summary>
    partial void OnModelCreatingM2(ModelBuilder modelBuilder);

    /// <summary>M4 增量模型的挂载点。</summary>
    partial void OnModelCreatingM4(ModelBuilder modelBuilder);

    /// <summary>M4.2 遥测增量模型的挂载点。</summary>
    partial void OnModelCreatingM4Telemetry(ModelBuilder modelBuilder);

    /// <summary>M4.4 文件仓库与分发增量模型的挂载点。</summary>
    partial void OnModelCreatingM4Files(ModelBuilder modelBuilder);

    /// <summary>M4.5 客户机配置增量模型的挂载点。</summary>
    partial void OnModelCreatingM45Config(ModelBuilder modelBuilder);
}
