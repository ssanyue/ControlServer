namespace ControlServer.Contracts;

// ═══════════════════════════════════════════════════════════════════════════
// M4.4 · 文件仓库与分发（第一部分：仓库文件 + 可续传分块上传）
//
// 三条贯穿全篇的约定：
//
// ① **仓库只认"文件 id"，绝不接受客户端提交的任意路径**。
//    下载/删除的入参永远是一个 id，服务端自己把它映射到磁盘上的受控路径。
//    这是"路径穿越"这个漏洞类别在接口层面的根除 —— 而不是靠事后再去过滤 `..`。
//
// ② **上传一律分块 + 可续传**。>2GB 的文件用一次性 multipart 传，
//    要么被框架的 `MultipartBodyLengthLimit` 拦死，要么把整个文件读进内存。
//    分块之后：客户端断线只要记住"已传多少字节"，重连后从 `offset` 接着传。
//
// ③ **SHA256 全链路**。上传结束时服务端**自己算一遍**摘要（不信客户端自报），
//    与期望值比对通过才落进仓库；下载时客户端边下边算，落地前再比一次。
//    这样"文件在传输中损坏"与"文件本来就不是你要的那个"都能被抓住。
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 仓库文件的类别（M4.4）。
/// <para>
/// <b>只用字符串常量不用 enum</b> —— 理由同 <see cref="AgentStates"/>（SignalR / JSON 会把 enum 变数字）。
/// 这些名字<b>会落库</b>（<c>file_repo_items.kind</c>），改名等于要写数据迁移。
/// </para>
/// </summary>
public static class FileKinds
{
    /// <summary>普通文件：管理员在网页上手工上传，供"仓库共享"下载或"定向下发"推给客户机。</summary>
    public const string Normal = "normal";

    /// <summary>
    /// 客户端安装包（预留）。<b>M4.4 只定义不实现</b>。
    /// <para>
    /// 现有安装包托管走的是数据目录的 <c>downloads\</c>（M4.1，<c>DownloadsController</c>），
    /// 把它"收编"进仓库（方案 §10.5 的决策 D11）是一次独立改造，本轮不动 ——
    /// 动了就会牵动绿色版"发布目录恰好 1 个文件"那条硬校验。
    /// </para>
    /// </summary>
    public const string Installer = "installer";

    /// <summary>客户机<b>回传</b>上来的文件（现场日志 / 截图等）。</summary>
    public const string Collected = "collected";

    /// <summary>全部类别（顺序即前端筛选下拉框顺序）。</summary>
    public static readonly IReadOnlyList<string> All = new[] { Normal, Installer, Collected };

    /// <summary>中文显示名（仅展示用，不参与比较）。</summary>
    public static string DisplayName(string? kind) => kind switch
    {
        Normal => "普通文件",
        Installer => "安装包",
        Collected => "客户机回传",
        _ => kind ?? string.Empty,
    };

    /// <summary>是否为合法类别（服务端摄入时的白名单校验）。</summary>
    public static bool IsValid(string? kind) => kind is not null && All.Contains(kind);
}

/// <summary>上传会话的状态（M4.4）。</summary>
public static class FileUploadStates
{
    /// <summary>进行中：可以继续追加分块。</summary>
    public const string Open = "open";

    /// <summary>已超期（服务端清理时标记；客户端应重新发起）。</summary>
    public const string Expired = "expired";
}

/// <summary>
/// 仓库里一个文件的元信息（M4.4）。
/// </summary>
/// <param name="Id">仓库内唯一 id。<b>下载 / 删除 / 分发都用它，而不是文件名</b>。</param>
/// <param name="Name">原始文件名（展示用；磁盘上的实际名由服务端生成）。</param>
/// <param name="Size">字节数。</param>
/// <param name="Sha256">全量摘要（小写 hex）。上传时服务端自己算的，可直接用于校验。</param>
/// <param name="Kind">见 <see cref="FileKinds"/>。</param>
/// <param name="UploadedBy">
/// 上传者。<b>可空</b>：客户机回传（<see cref="FileKinds.Collected"/>）没有"用户"，
/// 此时为 null，来源看 <paramref name="OriginAgentId"/>。
/// </param>
/// <param name="OriginAgentId">回传文件来自哪台客户机；非回传为 null。</param>
public sealed record FileRepoItemDto(
    int Id,
    string Name,
    long Size,
    string Sha256,
    string Kind,
    string? UploadedBy,
    DateTimeOffset UploadedAt,
    int? OriginAgentId,
    string? Note);

/// <summary>仓库列表（含累计占用，便于运维判断该不该清理）。</summary>
public sealed record FileRepoListDto(
    IReadOnlyList<FileRepoItemDto> Items,
    int Total,
    long TotalBytes);

/// <summary>
/// 发起一次分块上传（M4.4）。
/// </summary>
/// <param name="FileName">原始文件名（服务端会取扩展名并做安全化，绝不直接当路径用）。</param>
/// <param name="Size">文件总字节数。<b>必填</b> —— 服务端据此预检磁盘余量并防止"边传边涨"。</param>
/// <param name="Kind">见 <see cref="FileKinds"/>；为空时按调用者身份决定（用户→normal，Agent→collected）。</param>
/// <param name="Sha256">可选的期望摘要；给了就在完成时强校验。</param>
public sealed record FileUploadCreateRequest(
    string? FileName,
    long Size,
    string? Kind = null,
    string? Sha256 = null);

/// <summary>
/// 上传会话状态（既用于创建应答，也用于"续传前先问服务端收到哪了"）。
/// </summary>
/// <param name="UploadId">会话 id（后续追加分块与完成都用它）。</param>
/// <param name="ReceivedBytes">
/// <b>服务端已收下的字节数</b>。断线重连后客户端<b>先读它</b>，再从该偏移继续 ——
/// 这就是"可续传"的全部秘密：进度的事实来源在服务端，不在客户端内存里。
/// </param>
/// <param name="ChunkSizeBytes">服务端建议的分块大小（客户端可据此切块）。</param>
public sealed record FileUploadSessionDto(
    string UploadId,
    string FileName,
    long Size,
    string Kind,
    long ReceivedBytes,
    long ChunkSizeBytes,
    string State);

/// <summary>完成上传时的请求体。</summary>
/// <param name="Sha256">期望摘要（可选；给了就强校验）。与创建时的值必须一致，否则以创建时的为准。</param>
public sealed record FileUploadCompleteRequest(string? Sha256 = null);

/// <summary>上传成功后返回的仓库条目摘要（含最终 id 与摘要，可直接拿去建分发任务）。</summary>
public sealed record FileUploadResultDto(
    int Id,
    string Name,
    long Size,
    string Sha256,
    string Kind);
