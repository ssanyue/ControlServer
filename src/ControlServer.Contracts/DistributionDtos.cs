namespace ControlServer.Contracts;

// ═══════════════════════════════════════════════════════════════════════════
// M4.4 · 文件分发（第二部分：任务 / 进度 / Agent 侧载荷）
//
// 传输模型（方案 §7.2 已定，这里是它的合同）：
//
//   · **控制信令走 SignalR**：服务端把任务推给 Agent（`/hubs/agent`），
//     Agent 把进度回报上来，服务端再转推给网页（`/hubs/events`）。
//   · **字节走 Agent 主动 HTTP**：下载用 `Range` 断点续传，上传用分块追加。
//     客户端因此**依旧一个入站端口都不开**（M4.1 的承诺不能在这里破）。
//
// 两个方向：
//   · **下发（push）**：仓库文件 → 客户机某个白名单目录。
//   · **回传（collect）**：客户机上某个白名单目录里的文件 → 仓库（`kind=collected`）。
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>分发方向（M4.4）。</summary>
public static class DistributionDirections
{
    /// <summary>下发：中心 → 客户机。</summary>
    public const string Push = "push";

    /// <summary>回传：客户机 → 中心。</summary>
    public const string Collect = "collect";

    /// <summary>全部方向（顺序即前端展示顺序）。</summary>
    public static readonly IReadOnlyList<string> All = new[] { Push, Collect };

    /// <summary>中文显示名（仅展示用）。</summary>
    public static string DisplayName(string? direction) => direction switch
    {
        Push => "下发",
        Collect => "回传",
        _ => direction ?? string.Empty,
    };

    /// <summary>是否为合法方向（服务端摄入时的白名单校验）。</summary>
    public static bool IsValid(string? direction) => direction is not null && All.Contains(direction);
}

/// <summary>
/// 一个<b>分发任务</b>的总体状态（跨所有目标汇总）。
/// <para><b>只用字符串常量不用 enum</b>（理由见 <see cref="AgentStates"/>）。会落库，改名即迁移。</para>
/// </summary>
public static class DistributionStates
{
    /// <summary>已创建：目标里有仍在等待的（含当时离线的机器，等它上线再传）。</summary>
    public const string Pending = "pending";

    /// <summary>进行中：至少有一个目标正在传输。</summary>
    public const string Running = "running";

    /// <summary>全部目标成功。</summary>
    public const string Completed = "completed";

    /// <summary>部分成功、部分失败（<b>不是失败</b>：得让运维看到"哪几台好了、哪几台没好"）。</summary>
    public const string Partial = "partial";

    /// <summary>全部目标失败。</summary>
    public const string Failed = "failed";

    /// <summary>已取消。</summary>
    public const string Canceled = "canceled";

    /// <summary>全部状态（顺序即前端筛选下拉框顺序）。</summary>
    public static readonly IReadOnlyList<string> All =
        new[] { Pending, Running, Completed, Partial, Failed, Canceled };

    /// <summary>中文显示名（仅展示用，不参与比较）。</summary>
    public static string DisplayName(string? state) => state switch
    {
        Pending => "等待中",
        Running => "传输中",
        Completed => "已完成",
        Partial => "部分成功",
        Failed => "失败",
        Canceled => "已取消",
        _ => state ?? string.Empty,
    };

    /// <summary>是否已终结（终态不再变化，前端可停止轮询）。</summary>
    public static bool IsTerminal(string? state) =>
        state is Completed or Partial or Failed or Canceled;

    /// <summary>是否为"需要运维看一眼"的状态（页面上标黄/标红）。</summary>
    public static bool NeedsAttention(string? state) => state is Partial or Failed;

    /// <summary>是否为合法状态（服务端摄入时的白名单校验）。</summary>
    public static bool IsValid(string? state) => state is not null && All.Contains(state);
}

/// <summary>单个目标（一台机器）的传输状态。</summary>
public static class DistributionItemStates
{
    /// <summary>等待传输（含刚建单、那台机器还离线的情形）。</summary>
    public const string Pending = "pending";

    /// <summary>正在传输。</summary>
    public const string Running = "running";

    /// <summary>完成（下载：已校验并原子落地；回传：已落进仓库）。</summary>
    public const string Completed = "completed";

    /// <summary>失败（原因见 <c>Error</c>）。</summary>
    public const string Failed = "failed";

    /// <summary>已取消。</summary>
    public const string Canceled = "canceled";

    /// <summary>全部状态。</summary>
    public static readonly IReadOnlyList<string> All =
        new[] { Pending, Running, Completed, Failed, Canceled };

    /// <summary>中文显示名。</summary>
    public static string DisplayName(string? state) => state switch
    {
        Pending => "等待中",
        Running => "传输中",
        Completed => "已完成",
        Failed => "失败",
        Canceled => "已取消",
        _ => state ?? string.Empty,
    };

    /// <summary>是否为合法状态。</summary>
    public static bool IsValid(string? state) => state is not null && All.Contains(state);
}

/// <summary>
/// 下发任务里的<b>一个目标</b>（M4.4+，2026-10-13 起支持每台一个目录）。
/// <para>
/// <b>为什么要按台给</b>：现场每台客户机的磁盘布局并不一样（一台有 D 盘、一台只有 C 盘，
/// 一台装在机器上、一台挂在采集机上），"所有目标共用一个目录"会逼着运维分批建单。
/// </para>
/// </summary>
/// <param name="AgentId">目标客户机 id。</param>
/// <param name="TargetDir">
/// 这台机器上的落地目录（<b>绝对路径</b>）。留空 = 用那台机器配置的默认目录。
/// <para>
/// ⚠️ 客户机侧有<b>可写目录白名单</b>，这里传的值仍会被它校验一次；
/// 越界即拒绝<b>这一台</b>（其余目标照常传），失败原因会回报到该行上。
/// 服务端<b>无权也无从</b>代替它校验（它不知道对方磁盘长什么样）。
/// </para>
/// </param>
public sealed record DistributionTargetRequest(int AgentId, string? TargetDir = null);

/// <summary>
/// 新建一个<b>下发</b>任务（M4.4）。
/// </summary>
/// <param name="FileId">仓库文件 id（<b>不是路径</b>）。</param>
/// <param name="Targets">
/// 目标清单：<b>每台一条，各自可以带一个落地目录</b>（当前形状）。
/// 与 <paramref name="TargetAgentIds"/> 同时给出时<b>以本字段为准</b>。
/// </param>
/// <param name="TargetAgentIds">
/// ⚠️ <b>旧形状</b>（2026-10-13 之前）：只有一个 id 列表，所有目标共用 <paramref name="TargetDir"/>。
/// 保留它只是为了让既有调用方（冒烟脚本 / 老客户端）不改也能继续用；
/// 新代码请用 <paramref name="Targets"/>。
/// </param>
/// <param name="TargetDir">旧形状配套的"所有目标共用的目录"。</param>
public sealed record DistributionCreateRequest(
    int FileId,
    IReadOnlyList<DistributionTargetRequest>? Targets = null,
    IReadOnlyList<int>? TargetAgentIds = null,
    string? TargetDir = null);

/// <summary>
/// 新建一个<b>回传</b>任务（M4.4）：把客户机上的某个文件收上来。
/// </summary>
/// <param name="TargetAgentIds">目标客户机 id 列表。</param>
/// <param name="SourcePath">
/// 客户机上的源路径，<b>必须是绝对路径</b>。
///
/// <para>
/// 🔥 <b>2026-10-15 起不再有"必须落在白名单目录内"这个约束</b>：客户机只校验
/// "路径合法 + 文件真实存在且非空"，<b>任意绝对路径都能取</b>。
/// 取舍理由见 <c>FileTransferClient.ResolveSourcePath</c>：能建回传单的角色已经被
/// 授权闸门（<c>collect</c> 归 Gated）+ <c>AdminOnly</c> 两道门筛过，
/// 而"取哪个文件"是管理员在浏览里逐个点出来的。
/// </para>
///
/// <para>
/// ⚠️ <b>服务端无从验证这一点</b>（它不知道对方磁盘长什么样），
/// 所以客户机是这条约束的<b>唯一执行者</b>，拒绝时要把原因回传上来（页面上那行红字）。
/// </para>
/// </param>
/// <param name="Note">备注（例如"收 10 月现场日志"）。</param>
public sealed record CollectCreateRequest(
    IReadOnlyList<int>? TargetAgentIds,
    string? SourcePath,
    string? Note = null);

/// <summary>分发任务里的一个目标（一台机器）的进度与结果。</summary>
/// <param name="ReceivedBytes">已传输字节数（断点续传时就是"续到哪了"）。</param>
/// <param name="TotalBytes">总字节数；未知/回传尚未开始为 0。</param>
/// <param name="ProgressPct">百分比（0–100，服务端算好，前端不必自己除）。</param>
/// <param name="Error">失败原因（人话）；非失败为 null。</param>
/// <param name="TargetDir">
/// <b>这一台</b>的落地目录（下发任务才有；回传为 null）。
/// <para>为空表示"用那台机器的默认目录"—— 界面上要区分"没指定"与"指定了某个目录"。</para>
/// </param>
/// <param name="ResultFileId">
/// <b>回传完成后</b>，这一台的文件落进仓库后的 id（下发任务为 null）。
///
/// <para>
/// 🔥 <b>为什么必须由服务端给</b>：回传任务创建时只有"源路径"，没有仓库文件 id
/// （<c>DistributionTask.FileId</c> 恒为 0，见 <c>CreateCollectAsync</c>）——
/// 文件是客户机后来分块上传才落进仓库的。页面若自己按文件名去仓库里猜，
/// 同名文件一多就会下载到<b>错的那一份</b>，而症状是"取回来了但内容是上次的"。
/// </para>
///
/// <para>
/// ⚠️ 它是<b>读时派生</b>的（按来源客户机 + 文件名 + 字节数匹配最近一条 <c>collected</c>），
/// 不落库、不加列：所以历史任务也照样能显示，不需要数据迁移。
/// 匹配不到（文件被从仓库删了）就是 <c>null</c> —— 页面应当说"文件已不在仓库"，
/// 而不是给一个点了 404 的下载按钮。
/// </para>
/// </param>
public sealed record DistributionItemDto(
    int Id,
    int AgentId,
    string AgentName,
    string AgentState,
    string State,
    long ReceivedBytes,
    long TotalBytes,
    double ProgressPct,
    string? Error,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? TargetDir = null,
    int? ResultFileId = null);

/// <summary>一个分发任务的完整视图（含每个目标的明细）。</summary>
public sealed record DistributionTaskDto(
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
    DateTimeOffset CreatedAt,
    int Total,
    int Succeeded,
    int Failed,
    IReadOnlyList<DistributionItemDto> Items);

/// <summary>分发任务列表。</summary>
public sealed record DistributionListDto(
    IReadOnlyList<DistributionTaskDto> Items,
    int Total);

/// <summary>
/// 一条传输进度推送（M4.4，<c>TransferProgress</c> 的载荷，服务端 → 网页）。
/// <para>只带单个目标的变化 —— 前端据此原地更新那一行，不必重拉整个任务。</para>
/// </summary>
public sealed record TransferProgressDto(
    int TaskId,
    int ItemId,
    int AgentId,
    string State,
    long ReceivedBytes,
    long TotalBytes,
    double ProgressPct,
    string? Error);

/// <summary>
/// 服务端推给客户机的一条传输任务（M4.4，Hub 方法 <c>PushTransferTask</c> 的载荷）。
/// <para>
/// <b>客户端拿它做什么</b>：按 <paramref name="Direction"/> 决定是"去下载"还是"去上传"。
/// 这是客户端与中心之间关于"文件该怎么传"的<b>唯一合同</b>。
/// </para>
/// </summary>
/// <param name="ItemId">该任务在这台机器上的条目 id（进度/完成都按它回报）。</param>
/// <param name="FileId">下发时的仓库文件 id；回传时为 0（回传的方向是上传，没有源文件 id）。</param>
/// <param name="Sha256">下发时的期望摘要（客户端落地前必须校验）；回传时为 null。</param>
/// <param name="TargetDir">
/// 下发的目标目录（<b>这一条任务专属的</b> —— 同一批里每台机器可以各不相同）；
/// 留空 = 用本机默认目录。回传为 null。
/// </param>
/// <param name="SourcePath">回传的源路径；下发为 null。</param>
public sealed record AgentTransferTaskDto(
    int ItemId,
    int TaskId,
    string Direction,
    int FileId,
    string FileName,
    long Size,
    string? Sha256 = null,
    string? TargetDir = null,
    string? SourcePath = null);

/// <summary>客户机回报单条传输的进度（M4.4）。</summary>
/// <param name="ReceivedBytes">已传字节数。</param>
/// <param name="TotalBytes">总字节数（下发取自文件大小；回传由客户端量出）。</param>
/// <param name="Error">非空表示这一轮出了可恢复的问题（会被记录，但不一定失败）。</param>
public sealed record AgentTransferProgressRequest(
    long ReceivedBytes,
    long TotalBytes,
    string? Error = null);

/// <summary>客户机回报单条传输的最终结果（M4.4）。</summary>
/// <param name="Ok">是否成功（失败时 <paramref name="Error"/> 必须有值）。</param>
/// <param name="ReceivedBytes">最终字节数。</param>
/// <param name="Sha256">客户端<b>自己算出的</b>摘要；下发时应与服务端给的一致。</param>
/// <param name="Error">失败原因（人话，会原样显示在网页上，所以要写清楚，例如
/// "目标目录不在白名单内：D:\somewhere" / "SHA256 不一致" / "磁盘空间不足"）。</param>
public sealed record AgentTransferCompleteRequest(
    bool Ok,
    long ReceivedBytes,
    string? Sha256 = null,
    string? Error = null);

/// <summary>单条传输回报的应答（客户端据此决定要不要重试）。</summary>
public sealed record AgentTransferAckDto(bool Ok, string Status, string? Message)
{
    public static AgentTransferAckDto Success() => new(true, "ok", null);

    public static AgentTransferAckDto Rejected(string status, string message) => new(false, status, message);
}
