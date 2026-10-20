namespace ControlServer.Infrastructure.Persistence.Entities;

/// <summary>
/// 仓库里的一个文件（M4.4）。
/// <para>
/// ⚠️ <b>这里只存元数据，不存字节</b>：字节在 <c>{FilesDirectory}\repo\</c> 下，
/// 文件名是 <see cref="StoredName"/>（服务端生成的单段名）。两者分开之后，
/// "用户的文件名"就永远不会变成磁盘路径 —— 路径穿越在这个设计里没有落脚点。
/// </para>
/// <para>
/// ⚠️ 时间戳用 Unix 毫秒（<see cref="UploadedAtMs"/>）：列表按上传时间<b>倒序</b>，
/// 而 SQLite 的 EF 提供程序拒绝翻译 <c>DateTimeOffset</c> 的 ORDER BY（详见 <c>TelemetryTime</c>）。
/// </para>
/// </summary>
public sealed class FileRepoItemEntity
{
    public int Id { get; set; }

    /// <summary>原始文件名（展示用；已清洗过外壳）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>磁盘实际文件名（服务端生成，单段、无目录）。</summary>
    public string StoredName { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>小写 hex 的 SHA256（服务端自己算的）。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>见 <c>FileKinds</c>。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>上传用户名；客户机回传时为 null。</summary>
    public string? UploadedBy { get; set; }

    /// <summary>上传时刻（Unix 毫秒）。</summary>
    public long UploadedAtMs { get; set; }

    /// <summary>回传文件来自哪台客户机；否则为 null。</summary>
    public int? OriginAgentId { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// 一次分块上传的会话（M4.4）。
/// <para>
/// ⚠️ <b>刻意不存"已收字节数"</b>：那个数字的事实来源是 <c>.part</c> 文件的长度。
/// 数据库里再记一份，就等于给自己制造了一个"两边不同步"的机会 ——
/// 而不同步的表现是"从错误的位置继续写"，产出一个长度正确、内容损坏的文件。
/// </para>
/// </summary>
public sealed class FileUploadSessionEntity
{
    /// <summary>会话 id（形如 <c>u{GUID:N}</c>）。</summary>
    public string Id { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    /// <summary><c>.part</c> 暂存文件名（复用仓库的生成规则）。</summary>
    public string StoredName { get; set; } = string.Empty;

    /// <summary>声明的总大小（完成时据此校验）。</summary>
    public long Size { get; set; }

    public string Kind { get; set; } = string.Empty;

    /// <summary>创建时声明的期望摘要（可空）。</summary>
    public string? Sha256 { get; set; }

    public string? CreatedBy { get; set; }

    public int? OriginAgentId { get; set; }

    public long CreatedAtMs { get; set; }

    public long ExpiresAtMs { get; set; }

    /// <summary>见 <c>FileUploadStates</c>。以字符串存储，避免 enum 数字漂移。</summary>
    public string State { get; set; } = string.Empty;
}

/// <summary>
/// 一个分发任务（M4.4）。
/// </summary>
public sealed class DistributionTaskEntity
{
    public int Id { get; set; }

    /// <summary>见 <c>DistributionDirections</c>（push / collect）。</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>下发时的仓库文件 id；回传为 0。</summary>
    public int FileId { get; set; }

    /// <summary>文件名快照（任务列表要显示，不该每次去 join 仓库）。</summary>
    public string FileName { get; set; } = string.Empty;

    public long FileSize { get; set; }

    /// <summary>见 <c>DistributionStates</c>（由明细汇总，不是手工维护）。</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>下发的目标目录标识（客户端白名单内的一个）。</summary>
    public string? TargetDir { get; set; }

    /// <summary>回传的客户机侧源路径。</summary>
    public string? SourcePath { get; set; }

    public string? Note { get; set; }

    public string? CreatedBy { get; set; }

    public long CreatedAtMs { get; set; }
}

/// <summary>
/// 一个任务在<b>单台机器</b>上的条目（M4.4）。
/// </summary>
public sealed class DistributionItemEntity
{
    public int Id { get; set; }

    public int TaskId { get; set; }

    /// <summary>目标客户机（<c>agent_hosts.id</c>）。不建外键，理由同遥测表（移除设备时我们主动清理）。</summary>
    public int AgentId { get; set; }

    /// <summary>
    /// <b>这一台</b>的落地目录（下发任务才有；回传为 null；空 = 那台机器的默认目录）。
    /// <para>
    /// ⚠️ 放在明细而不是任务上：同一批下发的每台机器目录可以不同（2026-10-13）。
    /// </para>
    /// </summary>
    public string? TargetDir { get; set; }

    /// <summary>见 <c>DistributionItemStates</c>。</summary>
    public string State { get; set; } = string.Empty;

    public long ReceivedBytes { get; set; }

    /// <summary>总字节数；回传任务在客户端量出大小之前为 <b>0</b>（"还不知道"，不是"零字节"）。</summary>
    public long TotalBytes { get; set; }

    /// <summary>失败 / 重试原因（人话，会原样显示在网页上）。</summary>
    public string? Error { get; set; }

    /// <summary>客户端算出的摘要。</summary>
    public string? Sha256 { get; set; }

    public long? StartedAtMs { get; set; }

    public long? CompletedAtMs { get; set; }
}
