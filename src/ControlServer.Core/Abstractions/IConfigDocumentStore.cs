namespace ControlServer.Core.Abstractions;

/// <summary>
/// 一份配置文档的快照。
/// </summary>
/// <param name="Key">文档键（<c>settings</c> / <c>commands</c> / <c>layout</c>）。</param>
/// <param name="Value">JSON 文本（缺失时为 null）。</param>
/// <param name="Version">乐观锁版本号；文档不存在时为 0。</param>
/// <param name="UpdatedAt">最后写入时间（北京时间）。</param>
/// <param name="UpdatedBy">最后写入者（用户名 / <c>system</c>）。</param>
public sealed record ConfigDocumentSnapshot(
    string Key,
    string? Value,
    int Version,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy)
{
    /// <summary>文档是否已存在（版本 0 表示从未写入）。</summary>
    public bool Exists => Version > 0;

    public static ConfigDocumentSnapshot Missing(string key) => new(key, null, 0, null, null);
}

/// <summary>
/// 配置文档存储端口。
/// <para>
/// 为什么是"整份 JSON 文档"而不是关系表 —— 见 M2 方案 §4.2：
/// 本项目配置写入永远是「读全量 → 改 → 写全量」，关系表的增量能力用不上；
/// 而导入导出是一等公民，用 JSON 存可做到导出即读、导入即写，零映射代码。
/// </para>
/// <para>
/// 实现方**必须**保证「读-改-写」的原子性（进程内串行化 + 版本号校验）。
/// </para>
/// </summary>
public interface IConfigDocumentStore
{
    /// <summary>读取一份文档快照（不存在时返回 <see cref="ConfigDocumentSnapshot.Missing"/>）。</summary>
    Task<ConfigDocumentSnapshot> ReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 写入一份文档。
    /// </summary>
    /// <param name="key">文档键。</param>
    /// <param name="value">JSON 文本。</param>
    /// <param name="expectedVersion">
    /// 期望的当前版本。传 <c>null</c> 表示**不做乐观锁校验**（内部调用 / 迁移用）。
    /// 版本不匹配将抛 <see cref="ConfigConcurrencyException"/>。
    /// </param>
    /// <param name="updatedBy">写入者标识。</param>
    /// <returns>写入后的新版本号。</returns>
    Task<int> WriteAsync(
        string key,
        string value,
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default);

    /// <summary>列出全部已存在的文档键。</summary>
    Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>删除一份文档（用于"恢复出厂"等场景）。</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// 乐观锁冲突：调用方持有的版本与服务端不一致（其他人已改过配置）。
/// 上层应转换为 HTTP <c>409 Conflict</c> 并把服务端最新版本返回给前端。
/// </summary>
public sealed class ConfigConcurrencyException : Exception
{
    public ConfigConcurrencyException(string key, int expectedVersion, int actualVersion)
        : base($"配置「{key}」已被其他会话修改：期望版本 {expectedVersion}，服务端实际版本 {actualVersion}。")
    {
        Key = key;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    public string Key { get; }

    public int ExpectedVersion { get; }

    public int ActualVersion { get; }
}
