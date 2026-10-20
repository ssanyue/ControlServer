using System.Text.Json;
using ControlServer.Contracts;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ControlServer.Server.Api;

/// <summary>
/// 指令库端点（M2-E3）。
/// <para>
/// 指令库存的是"可复用的发送指令"，动作的数据类型为「指令库」时其 <c>DataContent</c> 是**指令名**。
/// 指令库是**独立文档**（<c>commands</c>），<b>不参与配置导入导出</b> —— 与旧版行为一致。
/// </para>
/// <para>
/// <b>为什么不做 <c>expectedVersion</c> 参数</b>：逐条增删改的语义是"针对最新库施加一个局部意图"，
/// 天然可合并（两个人分别加不同指令不该互相冲突）。需要整份替换的场景走配置导入导出链路。
/// </para>
/// </summary>
[ApiController]
[Route("api/commands")]
[Authorize]
public sealed class CommandsController : ControllerBase
{
    private readonly CommandLibraryService _commands;

    public CommandsController(CommandLibraryService commands)
    {
        _commands = commands;
    }

    /// <summary>读取全部指令。</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var items = await _commands.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var version = await _commands.GetVersionAsync(cancellationToken).ConfigureAwait(false);

        return Ok(ToListDto(items, version));
    }

    /// <summary>新增指令（名称为空 / 重名 / 协议或数据类型越界 → 400）。</summary>
    [HttpPost]
    [Authorize(Policy = Policies.CanOperate)]
    public async Task<IActionResult> Create(
        [FromBody] CommandSaveRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { message = "请求体不能为空。" });
        }

        if (!TryParse(request.Command, out var item, out var error))
        {
            return BadRequest(new { message = error });
        }

        var result = await _commands
            .AddAsync(item, UpdatedBy(request.UpdatedBy), cancellationToken)
            .ConfigureAwait(false);

        return Map(result);
    }

    /// <summary>修改指令（不存在 → 404；重名 → 400）。</summary>
    [HttpPut("{id}")]
    [Authorize(Policy = Policies.CanOperate)]
    public async Task<IActionResult> Update(
        string id,
        [FromBody] CommandSaveRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { message = "请求体不能为空。" });
        }

        if (!TryParse(request.Command, out var item, out var error))
        {
            return BadRequest(new { message = error });
        }

        var result = await _commands
            .UpdateAsync(id, item, UpdatedBy(request.UpdatedBy), cancellationToken)
            .ConfigureAwait(false);

        return Map(result);
    }

    /// <summary>删除指令（不存在 → 404）。</summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = Policies.CanOperate)]
    public async Task<IActionResult> Delete(
        string id,
        [FromQuery] string? updatedBy,
        CancellationToken cancellationToken)
    {
        var result = await _commands
            .DeleteAsync(id, updatedBy ?? User.Identity?.Name ?? "web", cancellationToken)
            .ConfigureAwait(false);

        return Map(result);
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    private string UpdatedBy(string? fromBody) => fromBody ?? User.Identity?.Name ?? "web";

    /// <summary>
    /// 把 <c>Command</c> 元素还原成 <see cref="CommandItem"/>。
    /// 走 <see cref="ControlJson.LenientOptions"/>（大小写不敏感 / 容忍注释），与配置保存同一套口径。
    /// </summary>
    private static bool TryParse(JsonElement element, out CommandItem item, out string? error)
    {
        item = null!;
        error = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "command 必须是 JSON 对象。";
            return false;
        }

        try
        {
            var parsed = element.Deserialize<CommandItem>(ControlJson.LenientOptions);
            if (parsed is null)
            {
                error = "command 解析结果为空。";
                return false;
            }

            item = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = $"command 格式无法解析：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 结果 → HTTP。
    /// <list type="bullet">
    ///   <item><c>Ok</c> → 200</item>
    ///   <item><c>DuplicateName</c> / <c>Invalid</c> → 400</item>
    ///   <item><c>NotFound</c> → 404</item>
    ///   <item><c>StorageCorrupt</c> → 500（服务端状态问题，不是用户的输入问题）</item>
    /// </list>
    /// 三种失败都带<b>服务端当前全量列表</b>，前端可直接刷新。
    /// </summary>
    private IActionResult Map(CommandMutationResult result)
    {
        var dto = ToMutationDto(result);

        return result.Status switch
        {
            CommandMutationStatus.Ok => Ok(dto),
            CommandMutationStatus.DuplicateName => BadRequest(dto),
            CommandMutationStatus.Invalid => BadRequest(dto),
            CommandMutationStatus.NotFound => NotFound(dto),
            CommandMutationStatus.StorageCorrupt => StatusCode(StatusCodes.Status500InternalServerError, dto),
            _ => StatusCode(StatusCodes.Status500InternalServerError, dto),
        };
    }

    private static CommandListDto ToListDto(IReadOnlyList<CommandItem> items, int version)
        => new(
            JsonSerializer.SerializeToElement(items, ControlJson.Options),
            version,
            items.Count);

    private static CommandMutationDto ToMutationDto(CommandMutationResult result)
        => new(
            result.Ok,
            result.Status.ToString(),
            result.Version,
            JsonSerializer.SerializeToElement(result.Items, ControlJson.Options),
            result.Item is null ? null : JsonSerializer.SerializeToElement(result.Item, ControlJson.Options),
            result.ConflictId,
            result.Message);
}
