namespace ControlServer.Core.Models;

public enum LogLevelKind
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>内核日志条目（内存环形缓冲 + 落盘 + 推送共用）。</summary>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevelKind Level, string Category, string Message)
{
    public string LevelText => Level.ToString();
}
