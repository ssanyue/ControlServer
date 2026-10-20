namespace ControlServer.Contracts;

/// <summary>推送到浏览器的日志条目。</summary>
public sealed record LogDto(DateTimeOffset Timestamp, string Level, string Category, string Message);
