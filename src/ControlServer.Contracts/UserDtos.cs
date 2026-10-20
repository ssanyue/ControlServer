namespace ControlServer.Contracts;

/// <summary>
/// 用户账号的对外视图。
/// <para>
/// <b>刻意不含 <c>PasswordHash</c> / <c>Salt</c></b>：密码材料永远不出接口。
/// 这不是"顺手不传"，而是把"接口泄漏口令材料"这件事在类型层面变得不可能 ——
/// 实体（<c>AppUser</c>）与视图（本记录）是两个类型，想漏就得先显式多写一行映射。
/// </para>
/// </summary>
/// <param name="Id">主键。</param>
/// <param name="UserName">登录名。</param>
/// <param name="Role">角色，取值见 <see cref="Roles"/>。</param>
/// <param name="IsEnabled">账号是否启用（禁用后不能登录）。</param>
/// <param name="MustChangePassword">是否必须先修改密码才能使用。</param>
/// <param name="IsLocked">当前是否处于"连续登录失败锁定"状态（<see cref="LockedUntil"/> 未过期）。</param>
/// <param name="CreatedAt">创建时刻。</param>
/// <param name="LastLoginAt">最近登录成功时刻；从未登录为 <c>null</c>。</param>
/// <param name="LockedUntil">锁定到期时刻；未锁定为 <c>null</c>。</param>
public sealed record UserDto(
    int Id,
    string UserName,
    string Role,
    bool IsEnabled,
    bool MustChangePassword,
    bool IsLocked,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? LockedUntil);

/// <summary>新建账号。新建出来的账号不再强制改密（<c>MustChangePassword = false</c>）。</summary>
public sealed record CreateUserRequest(string UserName, string Password, string Role);

/// <summary>
/// 修改账号。三个字段都是"可选"，<c>null</c> 表示不动该字段。
/// <para>
/// 为什么不做整对象提交：整对象提交会强迫"只改角色"这种操作回传前端的陈旧快照，
/// 两个人先后提交就会互相覆盖对方刚改的字段。局部语义让"我只改角色"在网络上是真的只改角色。
/// </para>
/// </summary>
public sealed record UpdateUserRequest(string? UserName, string? Role, bool? IsEnabled);

/// <summary>管理员重置他人密码。重置后该账号 <c>MustChangePassword</c> 保持/置为 <c>false</c>（不强制改密）。</summary>
public sealed record ResetPasswordRequest(string NewPassword);

/// <summary>修改自己的密码。必须提供当前密码 —— 仅凭已登录的会话来改密，等于会话即口令。</summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// 用户写操作的统一响应。
/// <para>
/// 与 <see cref="CommandMutationDto"/> 同思路：<b>无论成败都带回服务端当前全量用户列表</b>，
/// 失败时前端可立即据此刷新，不必再发一次 GET。
/// </para>
/// </summary>
/// <param name="Ok">是否写入成功。</param>
/// <param name="Status">
/// 结果状态：<c>Ok</c> / <c>Invalid</c> / <c>DuplicateName</c> / <c>NotFound</c> /
/// <c>LastAdmin</c>（会移除最后一个启用管理员）/ <c>SelfTarget</c>（不允许对自己做该操作）。
/// </param>
/// <param name="Message">面向用户的一句话说明。</param>
/// <param name="User">本次新增 / 修改后的账号；删除或失败时为 <c>null</c>。</param>
/// <param name="Users">服务端当前全量用户（按 Id 升序）。</param>
public sealed record UserMutationResult(
    bool Ok,
    string Status,
    string Message,
    UserDto? User,
    IReadOnlyList<UserDto> Users);
