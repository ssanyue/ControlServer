namespace ControlServer.Infrastructure.Persistence.Entities;

/// <summary>
/// 配置文档（整份 JSON 一行）。见 M2 方案 §4.2 —— 配置走 JSON 文档 + 乐观锁，
/// 用户 / 审计仍走关系表。
/// </summary>
public sealed class ConfigDocument
{
    /// <summary>文档键：<c>settings</c> / <c>commands</c> / <c>layout</c>。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>JSON 文本。</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>乐观锁版本号，每次写入 +1。首次写入即为 1。</summary>
    public int Version { get; set; }

    /// <summary>最后写入时间（北京时间）。</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>最后写入者（用户名 / <c>system</c>）。</summary>
    public string UpdatedBy { get; set; } = string.Empty;
}
