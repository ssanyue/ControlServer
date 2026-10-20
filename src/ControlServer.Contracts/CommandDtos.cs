using System.Text.Json;

namespace ControlServer.Contracts;

/// <summary>
/// 指令库列表。
/// <para>
/// ⚠️ <b>设计说明</b>：与 <see cref="ConfigViewDto.Settings"/> 同理，<see cref="Commands"/> 用
/// <see cref="JsonElement"/> 透传而不是在契约层再抄一份 <c>CommandItem</c> ——
/// 抄一份就会有"两处真相"，改一处忘另一处 → 字段**静默丢失**。
/// 指令的形状只有一份真相（<c>Core.Models.CommandItem</c>），HTTP 层只做序列化 / 反序列化。
/// 前端 TS 类型按 camelCase（<c>id</c> / <c>name</c> / <c>protocol</c> / <c>dataType</c> / <c>data</c>）声明即可。
/// </para>
/// </summary>
/// <param name="Commands">全部指令。</param>
/// <param name="Version"><c>commands</c> 文档的乐观锁版本号。</param>
/// <param name="Count">指令条数（避免前端为显示数量而遍历数组）。</param>
public sealed record CommandListDto(
    JsonElement Commands,
    int Version,
    int Count);

/// <summary>
/// 新增 / 修改指令的请求体。
/// </summary>
/// <param name="Command">指令对象（形状同 <see cref="CommandListDto.Commands"/> 的数组元素）。</param>
/// <param name="UpdatedBy">写入者标识（写入审计与日志）。</param>
public sealed record CommandSaveRequest(
    JsonElement Command,
    string? UpdatedBy = null);

/// <summary>
/// 指令库增删改的统一响应。
/// <para>
/// 无论成败都带回**服务端最新全量列表** —— 失败（重名 / 找不到 / 内容损坏）时前端可立即用
/// 这份列表刷新界面，不必再发一次 GET。
/// </para>
/// </summary>
/// <param name="Ok">是否写入成功。</param>
/// <param name="Status">
/// 结果状态：<c>Ok</c> / <c>NotFound</c> / <c>DuplicateName</c> / <c>Invalid</c> / <c>StorageCorrupt</c>。
/// </param>
/// <param name="Version">写入后的版本号；失败时为读取时的版本号（未写入）。</param>
/// <param name="Commands">服务端当前全量指令。</param>
/// <param name="Command">本次新增 / 修改后的指令；删除或失败时为 <c>null</c>。</param>
/// <param name="ConflictId">重名时，与入参冲突的那条既有指令 Id。</param>
/// <param name="Message">面向用户的一句话说明。</param>
public sealed record CommandMutationDto(
    bool Ok,
    string Status,
    int Version,
    JsonElement Commands,
    JsonElement? Command,
    string? ConflictId,
    string? Message);
