using System.Security.Claims;
using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using ControlServer.Infrastructure.Persistence;
using ControlServer.Infrastructure.Persistence.Entities;
using ControlServer.Server.Security;
using ControlServer.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ControlServer.Server.Api;

/// <summary>
/// 认证端点（M1 登录 / 登出 / 会话；<b>M3 加改密与账号状态判定</b>）。
/// <para>
/// <b>登录的判定顺序是刻意设计的</b>：先校验口令，再看账号状态。
/// 这样"口令错"永远只得到一句笼统的"用户名或密码错误"，
/// 而"口令正确却被拦"才给出精确原因（已停用 / 已锁定到几点）。
/// 前者不泄漏账号状态，后者让真正的主人一眼知道该找谁。
/// </para>
/// </summary>
[ApiController]
[Route("api/auth")]
[Authorize]
public sealed class AuthController : ControllerBase
{
    /// <summary>
    /// 时序对齐用的假哈希（只会被 <c>Verify</c>，永远不会被写进库）。
    /// <para>
    /// 为什么是 <c>Lazy</c> 且静态：生成一次 PBKDF2 要 40–90 ms，
    /// 每个请求算一次等于把"登录"变慢十倍，而它本来就只是一个"陪跑"的固定值。
    /// 故意用 <see cref="Pbkdf2PasswordHasher"/> 而不是注入的实例 ——
    /// 陪跑者的耗时要与"真实的新格式用户"完全一致，不能跟着测试替身漂。
    /// </para>
    /// </summary>
    private static readonly Lazy<(string Hash, string Salt)> TimingDummy =
        new(() => new Pbkdf2PasswordHasher().Hash("m6.2-timing-alignment-only"));

    private readonly AppDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly LogService _log;
    private readonly UserAccountService _users;

    public AuthController(AppDbContext db, IPasswordHasher hasher, LogService log, UserAccountService users)
    {
        _db = db;
        _hasher = hasher;
        _log = log;
        _users = users;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitPolicies.Login)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var userName = request.UserName?.Trim() ?? string.Empty;
        var remoteIp = RemoteIp();
        var password = request.Password ?? string.Empty;

        // 超长口令一律按"口令错误"处理，而且**不喂给哈希函数**：
        //   · PasswordPolicy.MaxLength 是 128，正常口令远小于它；
        //   · 一个 100 MB 的请求体如果直接进 PBKDF2，那就是一次免费的 CPU 放大攻击
        //     （PBKDF2 的代价与口令长度无关，但把 100 MB 转成字节数组本身很贵）。
        // ⚠️ 不单独回一句"口令太长"：那等于告诉攻击者"我走到了哪一步"。
        var withinLength = password.Length <= PasswordPolicy.MaxLength;

        var user = await _users.FindByNameAsync(userName, cancellationToken).ConfigureAwait(false);

        var passwordOk = user is not null && withinLength
            && _hasher.Verify(password, user.PasswordHash, user.Salt);

        // ── 时序对齐（M6.2）──
        // 账号不存在时若直接返回，响应时间会**直接回答**"这个用户名存在吗"：
        // PBKDF2 是几十毫秒，而查不到账号是几毫秒，差一个数量级，局域网里测得很稳。
        // 因此这条路径也必须付一次同样的哈希代价。代价是"每次不存在的登录多 40–90 ms"，
        // 但登录是低频动作，换掉用户名枚举是划算的。
        if (user is null || !withinLength)
        {
            _hasher.Verify(password, TimingDummy.Value.Hash, TimingDummy.Value.Salt);
        }

        // ── ① 账号不存在 / 口令不正确：一律笼统拒绝，不泄漏账号是否存在 ──
        if (!passwordOk)
        {
            string? lockedNote = null;

            if (user is not null)
            {
                var lockedUntil = await _users
                    .RecordLoginFailureAsync(user, cancellationToken)
                    .ConfigureAwait(false);

                if (lockedUntil is { } until)
                {
                    lockedNote = $"连续失败 {UserAccountService.MaxFailedAttempts} 次，"
                        + $"账号已锁定至 {until:HH:mm:ss}（{BeijingTime.DisplayName}）。";
                }
            }

            await AuditAsync("login-failed", userName, remoteIp, cancellationToken).ConfigureAwait(false);
            _log.Warn("Auth", $"登录失败：{userName}（来自 {remoteIp}）{lockedNote ?? string.Empty}");

            return Unauthorized(new ProblemDetails
            {
                Title = lockedNote ?? "用户名或密码错误",
                Status = StatusCodes.Status401Unauthorized,
            });
        }

        // ── ② 口令正确，但账号状态不允许登录：这时才说清原因 ──

        // 不可达分支：能走到这里说明 passwordOk 为 true，而它蕴含 user 非空。
        // 留着不是为了防御"现在"，而是为了让可空分析通过、并且万一将来有人把上面的
        // 分支条件改坏，这里会老老实实回 401，而不是一个 NullReferenceException 500 ——
        // 登录端点抛 500 会让现场以为"服务坏了"，而真相往往只是"口令不对"。
        if (user is null)
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "用户名或密码错误",
                Status = StatusCodes.Status401Unauthorized,
            });
        }

        if (!user.IsEnabled)
        {
            await AuditAsync("login-rejected-disabled", user.UserName, remoteIp, cancellationToken)
                .ConfigureAwait(false);
            _log.Warn("Auth", $"登录被拒（账号已停用）：{user.UserName}（来自 {remoteIp}）");

            return Unauthorized(new ProblemDetails
            {
                Title = "账号已被停用，请联系管理员。",
                Status = StatusCodes.Status401Unauthorized,
            });
        }

        if (UserAccountService.IsLocked(user))
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((user.LockedUntil!.Value - BeijingTime.Now).TotalMinutes));

            await AuditAsync("login-rejected-locked", user.UserName, remoteIp, cancellationToken)
                .ConfigureAwait(false);
            _log.Warn("Auth", $"登录被拒（账号锁定中）：{user.UserName}（来自 {remoteIp}）");

            return Unauthorized(new ProblemDetails
            {
                Title = $"账号处于锁定状态，请在约 {minutes} 分钟后重试。",
                Status = StatusCodes.Status401Unauthorized,
            });
        }

        // ── ③ 放行 ──
        //
        // 旧哈希（M1 的单轮 SHA-256）在这里**就地升级**为 PBKDF2。
        // 为什么必须放在"口令已验证成功"之后：否则任何人都能触发某个账号的重算，
        // 而重算的输入是**调用方给的口令** —— 那会把库里的值改成一个他也不知道的密码，
        // 变成一次不需要权限的"账号锁死"。
        if (_hasher.NeedsRehash(user.PasswordHash))
        {
            var (hash, salt) = _hasher.Hash(password);
            user.PasswordHash = hash;
            user.Salt = salt;

            _log.Info("Auth", $"口令哈希已从旧格式升级为 PBKDF2：{user.UserName}");
        }

        // RecordLoginSuccessAsync 内部会 SaveChanges —— 于是哈希升级与登录记账一次落库。
        await _users.RecordLoginSuccessAsync(user, cancellationToken).ConfigureAwait(false);
        await SignInAsync(user).ConfigureAwait(false);

        _log.Info("Auth", $"登录成功：{user.UserName}（{user.Role}，来自 {remoteIp}）");

        return Ok(new LoginResponse(user.UserName, user.Role, user.MustChangePassword));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var name = User.Identity?.Name ?? "unknown";
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        _log.Info("Auth", $"已登出：{name}");
        return NoContent();
    }

    /// <summary>
    /// 当前会话用户。
    /// <para>
    /// 刻意<b>不查库</b>：声明已在 <c>OnValidatePrincipal</c> 里逐请求复核并刷新过，
    /// 这里再查一次既是多余的开销，也制造了"两处真相"。
    /// </para>
    /// </summary>
    [HttpGet("me")]
    public IActionResult Me()
    {
        if (User.Identity is not { IsAuthenticated: true })
        {
            return Ok(new CurrentUser(string.Empty, string.Empty, false, false));
        }

        return Ok(new CurrentUser(
            User.Identity.Name ?? string.Empty,
            User.FindFirstValue(ClaimTypes.Role) ?? string.Empty,
            true,
            SessionPrincipal.ReadMustChangePassword(User)));
    }

    /// <summary>
    /// 修改自己的密码（任何已登录角色都可用）。
    /// <para>
    /// 这是 <c>MustChangePassword</c> 唯一的出口，因此必须对本端点开"强制改密闸门"的白名单，
    /// 否则用户会被锁在"必须先改密"与"改密端点不允许访问"之间的死结里。
    /// </para>
    /// </summary>
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { message = "请求体不能为空。" });

        var userId = SessionPrincipal.ReadUserId(User);
        if (userId is null)
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "会话无效，请重新登录。",
                Status = StatusCodes.Status401Unauthorized,
            });
        }

        var error = await _users
            .ChangeOwnPasswordAsync(userId.Value, request.CurrentPassword, request.NewPassword, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null) return BadRequest(new { message = error });

        // 立刻重签会话：改密后 MustChangePassword 变 false。
        // 若只等下一次请求由复核顺带刷新，则"改密成功"这个响应之后的任何一次
        // 基于旧声明的判断（例如前端紧接着调的 me()）都可能读到过期的 true。
        var user = await _users.FindAsync(userId.Value, cancellationToken).ConfigureAwait(false);
        if (user is not null) await SignInAsync(user).ConfigureAwait(false);

        return Ok(new { message = "密码已修改。" });
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    private Task SignInAsync(AppUser user)
        => HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, SessionPrincipal.Create(user));

    private string RemoteIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private async Task AuditAsync(string action, string user, string remoteIp, CancellationToken cancellationToken)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            Timestamp = BeijingTime.Now,
            Action = action,
            User = user,
            RemoteIp = remoteIp,
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
