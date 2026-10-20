using ControlServer.Contracts;
using ControlServer.Infrastructure.Persistence.Entities;
using ControlServer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ControlServer.Server.Api;

/// <summary>
/// 用户账号管理（M3 RBAC）。
/// <para>
/// <b>整个控制器都是 <see cref="Policies.AdminOnly"/></b>：账号管理是"谁能进来"这件事本身，
/// 交给 Operator 就意味着 Operator 可以给自己提权到 Admin。所以这里没有"部分开放"的位置。
/// </para>
/// <para>
/// 写操作统一返回 <see cref="UserMutationResult"/>（含<b>服务端全量账号列表</b>）：
/// 失败时前端可立刻据此刷新，不必再发一次 GET，也就不会出现"界面停在旧状态"。
/// </para>
/// </summary>
[ApiController]
[Route("api/users")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class UsersController : ControllerBase
{
    private readonly UserAccountService _users;

    public UsersController(UserAccountService users)
    {
        _users = users;
    }

    /// <summary>全部账号（按 Id 升序）。</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var users = await _users.ListAsync(cancellationToken).ConfigureAwait(false);
        return Ok(users.Select(ToDto).ToList());
    }

    /// <summary>
    /// 可选角色清单（含中文显示名）。
    /// <para>
    /// 单独开一个端点是为了让前端<b>不必硬编码角色名与中文名</b> ——
    /// 那两份字符串一旦与服务端漂移，界面会显示出一个后端不认的角色。
    /// </para>
    /// </summary>
    [HttpGet("roles")]
    public IActionResult RoleList()
        => Ok(Roles.All.Select(r => new { name = r, displayName = Roles.DisplayName(r) }).ToList());

    /// <summary>新建账号（用户名重复 / 角色或密码不合法 → 400）。</summary>
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateUserRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { message = "请求体不能为空。" });

        var result = await _users
            .CreateAsync(request.UserName, request.Password, request.Role, Actor(), cancellationToken)
            .ConfigureAwait(false);

        return Map(result);
    }

    /// <summary>修改账号（不存在 → 404；重名 → 400；最后一个管理员 / 操作自己 → 409）。</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(
        int id,
        [FromBody] UpdateUserRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { message = "请求体不能为空。" });

        var result = await _users
            .UpdateAsync(id, request.UserName, request.Role, request.IsEnabled, Actor(), cancellationToken)
            .ConfigureAwait(false);

        return Map(result);
    }

    /// <summary>重置他人密码（不存在 → 404；密码不合法 → 400）。</summary>
    [HttpPost("{id:int}/password")]
    public async Task<IActionResult> ResetPassword(
        int id,
        [FromBody] ResetPasswordRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { message = "请求体不能为空。" });

        var result = await _users
            .ResetPasswordAsync(id, request.NewPassword, Actor(), cancellationToken)
            .ConfigureAwait(false);

        return Map(result);
    }

    /// <summary>删除账号（不存在 → 404；删自己 / 删最后一个管理员 → 409）。</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await _users.DeleteAsync(id, Actor(), cancellationToken).ConfigureAwait(false);
        return Map(result);
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 当前操作者。
    /// <para>
    /// Id 取自会话声明 —— 它是"不能停用/删除自己"这条守卫的唯一依据。
    /// 会话里由 <c>OnValidatePrincipal</c> 保证其存在且有效。
    /// </para>
    /// </summary>
    private ActorContext Actor()
        => new(
            SessionPrincipal.ReadUserId(User) ?? 0,
            User.Identity?.Name ?? "unknown",
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty);

    private static UserDto ToDto(AppUser user)
        => new(
            user.Id,
            user.UserName,
            user.Role,
            user.IsEnabled,
            user.MustChangePassword,
            UserAccountService.IsLocked(user),
            user.CreatedAt,
            user.LastLoginAt,
            user.LockedUntil);

    private IActionResult Map(UserAccountResult result)
    {
        var dto = new UserMutationResult(
            result.Ok,
            result.Status.ToString(),
            result.Message,
            result.User is null ? null : ToDto(result.User),
            result.Users.Select(ToDto).ToList());

        return result.Status switch
        {
            UserMutationStatus.Ok => Ok(dto),
            UserMutationStatus.Invalid => BadRequest(dto),
            UserMutationStatus.DuplicateName => BadRequest(dto),
            UserMutationStatus.NotFound => NotFound(dto),

            // 409：请求本身合法，但与服务端当前状态冲突（"最后一个管理员"、"不能操作自己"）。
            // 用 400 会让人以为是自己拼错了参数，而这两类错误只有"换个对象"才能解决。
            UserMutationStatus.LastAdmin => Conflict(dto),
            UserMutationStatus.SelfTarget => Conflict(dto),

            _ => StatusCode(StatusCodes.Status500InternalServerError, dto),
        };
    }
}
