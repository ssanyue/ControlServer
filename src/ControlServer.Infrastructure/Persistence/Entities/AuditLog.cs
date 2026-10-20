namespace ControlServer.Infrastructure.Persistence.Entities;

/// <summary>审计日志：登录、改配置、导入授权、执行动作等关键操作。</summary>
public sealed class AuditLog
{
    public long Id { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public string User { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public string RemoteIp { get; set; } = string.Empty;
}
