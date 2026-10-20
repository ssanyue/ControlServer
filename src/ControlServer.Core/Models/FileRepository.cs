namespace ControlServer.Core.Models;

/// <summary>
/// 仓库里的一个文件（M4.4，对应表 <c>file_repo_items</c>）。
/// <para>
/// <b>磁盘上的真实名由服务端生成</b>（<paramref name="StoredName"/>），
/// <paramref name="Name"/> 只是原始文件名（展示用）。两者分开是刻意的：
/// 用户的文件名里可能带 <c>..\</c>、保留字、超长、重名 ——
/// 拿它直接拼路径就是教科书级的路径穿越漏洞。
/// </para>
/// </summary>
/// <param name="StoredName">服务端生成的文件名（形如 <c>f{Id}_{guid}.bin</c>），仅此一段、无目录。</param>
public sealed record FileRepoItem(
    int Id,
    string Name,
    string StoredName,
    long Size,
    string Sha256,
    string Kind,
    string? UploadedBy,
    DateTimeOffset UploadedAt,
    int? OriginAgentId,
    string? Note);

/// <summary>
/// 一次<b>分块上传</b>的会话（M4.4，对应表 <c>file_upload_sessions</c>）。
/// <para>
/// <b>已收字节数的唯一事实来源是那个 <c>.part</c> 文件本身</b>（它的长度），
/// 不是本记录里的某个计数器 —— 后者一旦与磁盘不同步，
/// 续传就会从错误的位置写起，得到一个"长度对、内容烂"的文件。
/// 所以这里不存 <c>ReceivedBytes</c>，需要时去量文件。
/// </para>
/// </summary>
/// <param name="Id">会话 id（客户端可见，形如 <c>u{GUID:N}</c>）。</param>
/// <param name="Kind">完成后要落成哪种仓库类别。</param>
/// <param name="Sha256">创建时声明的期望摘要（可为 null）；完成时若给了就强校验。</param>
/// <param name="CreatedBy">发起者（用户名）；客户机发起时为 null。</param>
/// <param name="OriginAgentId">客户机发起（回传）时是哪一台；用户发起为 null。</param>
public sealed record FileUploadSession(
    string Id,
    string FileName,
    string StoredName,
    long Size,
    string Kind,
    string? Sha256,
    string? CreatedBy,
    int? OriginAgentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string State);

/// <summary>
/// 一个分发任务（M4.4，对应表 <c>distribution_tasks</c>）。
/// <para>状态是<b>由明细汇总出来的</b>（见 <c>DistributionService.SummarizeState</c>），
/// 每次有明细变化就重算一次 —— 不靠"手工维护一个总状态"，那必然会与明细不一致。</para>
/// </summary>
/// <param name="FileId">下发时的仓库文件 id；回传任务为 0（方向是上传，没有源文件 id）。</param>
/// <param name="FileName">下发时的文件名快照（任务列表要显示，不能每次去 join 仓库）。</param>
/// <param name="TargetDir">
/// 下发时的<b>任务级</b>目录：只有"全部目标共用一个目录"（旧形状）时才有值。
/// <para>
/// ⚠️ 2026-10-13 起每台可以各不相同，所以真正的落点看明细的
/// <see cref="DistributionItem.TargetDir"/>；本字段退化成"旧形状遗留 + 列表页摘要"。
/// </para>
/// </param>
public sealed record DistributionTask(
    int Id,
    string Direction,
    int FileId,
    string FileName,
    long FileSize,
    string State,
    string? TargetDir,
    string? SourcePath,
    string? Note,
    string? CreatedBy,
    DateTimeOffset CreatedAt);

/// <summary>
/// 下发任务的一个<b>目标</b>（M4.4+，2026-10-13 起每台可以各带一个落地目录）。
/// <para>它是"建单时每行明细"的输入形状 —— 与明细一一对应，是同一个东西的两个阶段。</para>
/// </summary>
/// <param name="TargetDir">这一台的落地目录（空 = 用那台机器的默认目录）。</param>
public sealed record DistributionTarget(int AgentId, string? TargetDir);

/// <summary>
/// 分发任务在<b>单台机器</b>上的条目（M4.4，对应表 <c>distribution_items</c>）。
/// <para>一个任务 × 一台机器 = 一行。进度与失败原因都落在这里，
/// 页面上的表格一行对一行。</para>
/// </summary>
/// <param name="TotalBytes">总字节数。下发时=文件大小；回传时要等客户端量出源文件才知道，<b>先为 0</b>。</param>
/// <param name="Sha256">客户端算出的摘要（下发时应与服务端一致；回传时是服务端收到的内容的摘要）。</param>
/// <param name="TargetDir">
/// <b>这一台</b>的落地目录（下发才有；回传为 null）。
/// <para>
/// ⚠️ 它之所以落在<b>明细</b>上而不是任务上：同一批下发的每台机器目录可以不同。
/// 派发时取 <c>item.TargetDir ?? task.TargetDir</c> —— 后者是同批的唯一目录（旧形状）或 null。
/// </para>
/// </param>
public sealed record DistributionItem(
    int Id,
    int TaskId,
    int AgentId,
    string State,
    long ReceivedBytes,
    long TotalBytes,
    string? Error,
    string? Sha256,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? TargetDir = null)
{
    /// <summary>
    /// 这台机器<b>实际</b>该落到哪个目录：明细上的优先，退而用任务级的。
    /// <para>
    /// ⚠️ <b>只此一份实现</b>：派发（实时推）与补领（机器上线拉）两条路都要算这个值，
    /// 各写一遍迟早会分叉 —— 而分叉的表现是"在线时落到 A、离线补领时落到 B"。
    /// </para>
    /// </summary>
    public string? EffectiveTargetDir(string? taskTargetDir) => TargetDir ?? taskTargetDir;
}
