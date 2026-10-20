namespace ControlServer.Contracts;

public sealed record LoginRequest(string UserName, string Password);

public sealed record LoginResponse(string UserName, string Role, bool MustChangePassword);

/// <summary>
/// 当前会话用户。
/// <para>
/// <see cref="MustChangePassword"/> 是 M3 补上的：登录响应里本来就有它，但 <c>/api/auth/me</c>
/// 没有 —— 于是"刷新页面后应强制改密"这条约束在刷新那一刻就丢了，用户可以直接跳过。
/// 会话恢复与登录必须对外表现一致，两处都要带。
/// </para>
/// </summary>
public sealed record CurrentUser(
    string UserName,
    string Role,
    bool IsAuthenticated,
    bool MustChangePassword);
