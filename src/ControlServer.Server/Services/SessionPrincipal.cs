using System.Globalization;
using System.Security.Claims;
using ControlServer.Contracts;
using ControlServer.Infrastructure.Persistence.Entities;

namespace ControlServer.Server.Services;

/// <summary>
/// 会话主体（Cookie 里那份 <see cref="ClaimsPrincipal"/>）的唯一构造入口。
/// <para>
/// <b>为什么必须唯一</b>：登录时签发的声明，与"每个请求复核后刷新"的声明必须是同一套，
/// 否则两处迟早漂移 —— 表现为"登录后是管理员，刷新一下变查看者"这类幽灵问题。
/// </para>
/// <para>
/// 会话里放的是<b>快照</b>：改角色 / 停用账号不会立刻反映到已签发的 Cookie 上，
/// 因此必须配合 <c>OnValidatePrincipal</c> 的逐请求复核（见 <c>Program.cs</c>）。
/// </para>
/// </summary>
internal static class SessionPrincipal
{
    /// <summary>由账号当前状态构造主体。</summary>
    public static ClaimsPrincipal Create(AppUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Role, user.Role),
            new(AuthClaims.MustChangePassword, user.MustChangePassword ? "true" : "false"),
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
    }

    /// <summary>从主体取值（不用扩展方法，避免与框架的同名扩展产生命名空间歧义）。</summary>
    private static string? Value(ClaimsPrincipal? principal, string type)
        => principal?.FindFirst(type)?.Value;

    /// <summary>读取会话里的账号 Id；M3 之前签发的旧会话没有该声明，返回 <c>null</c>。</summary>
    public static int? ReadUserId(ClaimsPrincipal? principal)
        => int.TryParse(Value(principal, ClaimTypes.NameIdentifier), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    /// <summary>会话里的登录名。</summary>
    public static string ReadUserName(ClaimsPrincipal? principal)
        => Value(principal, ClaimTypes.Name) ?? string.Empty;

    /// <summary>会话里的角色。</summary>
    public static string ReadRole(ClaimsPrincipal? principal)
        => Value(principal, ClaimTypes.Role) ?? string.Empty;

    /// <summary>会话里是否标记了"必须先改密"。</summary>
    public static bool ReadMustChangePassword(ClaimsPrincipal? principal)
        => string.Equals(Value(principal, AuthClaims.MustChangePassword), "true", StringComparison.Ordinal);
}
