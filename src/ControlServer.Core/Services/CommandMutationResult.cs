using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// 指令库写操作的结果状态。
/// <para>
/// 为什么用"结果对象"而不是抛异常：<see cref="DuplicateName"/> / <see cref="NotFound"/> /
/// <see cref="Invalid"/> 都是**正常业务分支**（用户输了个重名、点了个已被别人删掉的指令），
/// 不是异常。用异常表达就得让 HTTP 层 catch 一堆，也更容易把"校验失败"和"存储炸了"混在一起。
/// </para>
/// </summary>
public enum CommandMutationStatus
{
    /// <summary>成功写入。</summary>
    Ok,

    /// <summary>按 Id 定位不到目标指令。</summary>
    NotFound,

    /// <summary>指令名与既有指令重名（**大小写不敏感**，与 <c>ResolveAsync</c> 的查找口径一致）。</summary>
    DuplicateName,

    /// <summary>入参不合法（名称为空 / 协议或数据类型越界）。</summary>
    Invalid,

    /// <summary>
    /// 存储里的指令库 JSON 已损坏，且**不是空库**。
    /// <para>
    /// 这种状态下拒绝一切写入：若照常"按空库改写"，会把用户还没被抢救的原始内容**永久覆盖**。
    /// 读路径会退化为空库继续服务，写路径必须停下来让人去处置。
    /// </para>
    /// </summary>
    StorageCorrupt,
}

/// <summary>
/// 指令库增删改的结果。
/// <para>
/// 无论成败都带回<b>当前全量指令</b>（<see cref="Items"/>）与版本号 ——
/// 失败时前端能立刻用服务端的最新列表刷新，不必再发一次 GET。
/// </para>
/// </summary>
/// <param name="Status">结果状态。</param>
/// <param name="Version">写入后的版本号；失败时为**读取时的版本号**（未写入）。</param>
/// <param name="Items">当前全量指令（按存储顺序）。</param>
/// <param name="Item">本次写入 / 命中的那条指令；失败时为 <c>null</c>。</param>
/// <param name="ConflictId">重名时，与入参冲突的那条既有指令的 Id。</param>
/// <param name="Message">面向用户的一句话说明。</param>
public sealed record CommandMutationResult(
    CommandMutationStatus Status,
    int Version,
    IReadOnlyList<CommandItem> Items,
    CommandItem? Item,
    string? ConflictId,
    string? Message)
{
    /// <summary>是否写入成功。</summary>
    public bool Ok => Status == CommandMutationStatus.Ok;

    public static CommandMutationResult Success(
        int version,
        IReadOnlyList<CommandItem> items,
        CommandItem? item,
        string message)
        => new(CommandMutationStatus.Ok, version, items, item, null, message);

    public static CommandMutationResult Failure(
        CommandMutationStatus status,
        int version,
        IReadOnlyList<CommandItem> items,
        string message,
        string? conflictId = null)
        => new(status, version, items, null, conflictId, message);
}
