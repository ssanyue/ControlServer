namespace ControlServer.Contracts;

// ═══════════════════════════════════════════════════════════════════════════
// M4.4+ · 客户机目录浏览（选"文件存放路径"与"回传源路径"，2026-10-13）
//
// **为什么要做它**：下发要填"落到客户机的哪个目录"、回传要填"从客户机取哪个文件"，
// 而**手输路径是现场最常见的失败源**
// —— 盘符写错、目录名大小写错、多一个空格、中文路径被输入法带进全角字符。
// 更糟的是这些错要等到"传输失败"才暴露，中间隔着建单、派发、机器上线好几道环节。
//
// **方向**：服务端 →（`/hubs/agent`）→ 客户机。这是本项目**第一条"服务端要等客户机回话"**
// 的调用（此前全是单向推送）：用 SignalR 的 `ISingleClientProxy.InvokeAsync<T>` 实现 ——
// 服务端拿到一个 `Task<T>`、超时由服务端控制。客户机因此**依旧一个入站端口都不开**
// （M4.1 的承诺不能在这里破）。
//
// **安全边界仍在客户机，但浏览范围是整盘**（2026-10-13 起）：
// 浏览会把客户机的目录结构呈现给中心网页（产品决策：要像"远程资源管理器"一样能挑目录）；
// 而**真正落地 / 读取**的边界依旧是客户机的白名单 —— 选到白名单外照样会被拒，
// 只是那条拒绝发生在"传输"那一步，而不是"浏览"这一步。
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 目录浏览的结果状态（字符串常量，会跨进程传递）。
/// <para>
/// 分开列这么多值而不是"成功/失败"两态：每一条对应的<b>处置完全不同</b> ——
/// 「离线」要等机器上线、「不存在」要换个目录、「客户端太旧」要升级客户端。
/// 合并成一个失败，运维就只能靠猜。
/// </para>
/// </summary>
public static class AgentBrowseStatuses
{
    /// <summary>成功。</summary>
    public const string Ok = "ok";

    /// <summary>该客户机没启用文件传输。</summary>
    public const string Disabled = "disabled";

    /// <summary>
    /// 路径不在该客户机的可读白名单内。
    /// <para>
    /// ⚠️ <b>新版客户端（整盘浏览）不再产生它</b>；但<b>必须保留</b> ——
    /// 现场还装着旧版客户端，它们依然会在白名单越界时回这个值。
    /// 服务端与网页要能照常渲染它（一句"这台机器不允许读那里"），而不是当成未知状态。
    /// </para>
    /// </summary>
    public const string Denied = "denied";

    /// <summary>目录不存在、无权访问，或路径本身非法。</summary>
    public const string NotFound = "notfound";

    /// <summary>该路径是一个文件（不是目录），没法"进去"。</summary>
    public const string NotDirectory = "notdir";

    /// <summary>客户端没接上浏览能力（旧版本 / 该能力未装配）。</summary>
    public const string Unsupported = "unsupported";

    /// <summary>客户机没有长连接（服务端这一侧判定的）。</summary>
    public const string Offline = "offline";

    /// <summary>客户机没在时限内回话（服务端这一侧判定的）。</summary>
    public const string Timeout = "timeout";

    /// <summary>其它错误（客户机侧读取失败等）。</summary>
    public const string Error = "error";
}

/// <summary>
/// 请求列出客户机上的一个目录。
/// </summary>
/// <param name="Path">
/// 要列的目录（绝对路径）。<b>null / 空 = 列出顶层</b> ——
/// 也就是把这台机器的**全部盘符**列出来（"此电脑"那一层）。
/// </param>
public sealed record AgentBrowseRequestDto(string? Path = null);

/// <summary>
/// 目录里的一个条目（子目录或文件）。
/// </summary>
/// <param name="Name">
/// 显示名。顶层（盘符）用<b>带卷标与类型的完整路径</b>当名字（`C:\（系统）（固定）`）——
/// 那时的"名字"必须能说清是哪个盘。
/// </param>
/// <param name="Path">完整路径（点它就能进去 / 就能选中）。</param>
/// <param name="IsDirectory">true = 目录（可逐级进入）；false = 文件（可被选为回传源）。</param>
/// <param name="Size">文件字节数；目录为 0。</param>
/// <param name="ModifiedAt">
/// 最后修改时间（**北京时间**，与全项目的时间戳纪律一致）。
/// <para>读不到属性（权限 / 竞态删除）时为 null —— <b>不是 0</b>：显示"1970 年"比留空更误导。</para>
/// </param>
public sealed record AgentBrowseEntryDto(
    string Name,
    string Path,
    bool IsDirectory,
    long Size = 0,
    DateTimeOffset? ModifiedAt = null);

/// <summary>
/// 一次目录浏览的结果。
/// </summary>
/// <param name="Ok">是否成功。<b>失败时 <paramref name="Message"/> 一定有人话</b>（会原样显示在网页上）。</param>
/// <param name="Status">见 <see cref="AgentBrowseStatuses"/>。</param>
/// <param name="RequestedPath">客户机实际列出的那个目录（顶层时为 null）。</param>
/// <param name="ParentPath">
/// 上一级目录；<b>null = 已经到了盘根（如 <c>C:\</c>），没有上一级了</b> ——
/// 界面上应回到"盘符列表"，而不是给一个点了没反应的按钮。
/// </param>
/// <param name="Separator">客户机上的路径分隔符（Windows <c>\</c> / Linux <c>/</c>）。</param>
/// <param name="Roots">该机的<b>盘符全集</b>（供页面做"快速跳转 / 回此电脑"）。</param>
/// <param name="Entries">当前目录下的条目（目录在前、文件在后，同组按名排序）。</param>
/// <param name="Truncated">
/// true = 条目被截断了（超过客户机的单次上限）。
/// <para><b>必须显式告诉用户</b>：否则"列表里没有那个文件"会被当成"那个文件不存在"，
/// 而它其实只是排在截断线之后。</para>
/// </param>
public sealed record AgentBrowseResultDto(
    bool Ok,
    string Status,
    string? Message = null,
    string? RequestedPath = null,
    string? ParentPath = null,
    string? Separator = null,
    IReadOnlyList<string>? Roots = null,
    IReadOnlyList<AgentBrowseEntryDto>? Entries = null,
    bool Truncated = false)
{
    /// <summary>造一个失败结果（<b>一定要带人话原因</b>）。</summary>
    public static AgentBrowseResultDto Fail(string status, string message) => new(false, status, message);

    /// <summary>造一个"客户端不支持"的失败结果。</summary>
    public static AgentBrowseResultDto Unsupported(string message)
        => Fail(AgentBrowseStatuses.Unsupported, message);
}
