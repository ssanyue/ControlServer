using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using ControlServer.Infrastructure.Persistence;
using ControlServer.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Server.Services;

/// <summary>
/// 用户写操作的结果状态。
/// <para>
/// 与 <c>CommandMutationStatus</c> 同思路：这些都是<b>正常业务分支</b>（重名、最后一个管理员、
/// 操作自己），不是异常。用异常表达就得让 HTTP 层 catch 一堆，也更容易把"校验失败"和"存储炸了"混在一起。
/// </para>
/// </summary>
public enum UserMutationStatus
{
    /// <summary>写入成功。</summary>
    Ok,

    /// <summary>入参不合法（用户名 / 角色 / 密码不满足规则）。</summary>
    Invalid,

    /// <summary>用户名已被占用（**大小写不敏感**）。</summary>
    DuplicateName,

    /// <summary>按 Id 定位不到账号。</summary>
    NotFound,

    /// <summary>
    /// 该操作会移除<b>最后一个启用的管理员</b>，系统将再也无人能管理。
    /// </summary>
    /// <remarks>
    /// 经 HTTP 触达它<b>只有一条路</b>：唯一的管理员给自己降级（停用 / 删除自己被
    /// <see cref="SelfTarget"/> 更早拦下）。这是刻意保留的路径 ——
    /// "只有我一个管理员，我想把自己降成查看者"必须被明确拒绝，而不是留下一个没人能登录管理的系统。
    /// </remarks>
    LastAdmin,

    /// <summary>不允许对自己执行该操作（<b>停用自己 / 删除自己</b>）。</summary>
    /// <remarks>
    /// 注意<b>不含</b>"给自己降级"：那条由 <see cref="LastAdmin"/> 把关 ——
    /// 只要还有别的启用管理员，把自己退成查看者是正常交接动作。
    /// </remarks>
    SelfTarget,
}

/// <summary>操作者上下文（审计落库 + "不能操作自己"判定）。</summary>
/// <param name="Id">操作者账号 Id；系统内部动作为 0。</param>
/// <param name="Name">操作者登录名。</param>
/// <param name="RemoteIp">来源 IP（审计用）。</param>
public sealed record ActorContext(int Id, string Name, string RemoteIp)
{
    /// <summary>无人在场的内部动作（例如播种默认管理员）。</summary>
    public static ActorContext System { get; } = new(0, "system", string.Empty);
}

/// <summary>
/// 用户增删改与登录记账的结果。
/// 无论成败都带回<b>当前全量账号</b>，失败时前端可立刻刷新，不必再发一次 GET。
/// </summary>
public sealed record UserAccountResult(
    UserMutationStatus Status,
    string Message,
    AppUser? User,
    IReadOnlyList<AppUser> Users)
{
    public bool Ok => Status == UserMutationStatus.Ok;
}

/// <summary>
/// 用户账号服务（M3 RBAC）。
/// <para>
/// <b>为什么放在 Server 而不是 Core</b>：它直接操作 <c>AppDbContext</c>，而 Core 按铁律
/// 不得引用 EF。这里承载的是"账号生命周期"的应用逻辑（守卫规则、审计、登录记账），
/// 与既有 <see cref="AuthController"/> 的口径一致 —— 都属于"认证与账号"这一竖切，
/// 与 <c>Core</c> 里那些与存储无关的领域服务（编码、匹配、调度）不是一类东西。
/// </para>
/// <para>
/// <b>守卫规则</b>（任何一条被违反都会让系统变成"没人能管理"）：
/// ① 不能删除 / 停用 / 降级<b>最后一个启用的管理员</b>；② 不能停用 / 删除自己；
/// ③ 用户名唯一且大小写不敏感。
/// </para>
/// </summary>
public sealed class UserAccountService
{
    /// <summary>连续登录失败多少次后锁定账号。</summary>
    public const int MaxFailedAttempts = 5;

    /// <summary>锁定时长。</summary>
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    private readonly AppDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly LogService _log;

    public UserAccountService(AppDbContext db, IPasswordHasher hasher, LogService log)
    {
        _db = db;
        _hasher = hasher;
        _log = log;
    }

    // ═══════════════════════════════════════════════════════════
    // 读
    // ═══════════════════════════════════════════════════════════

    /// <summary>全部账号（按 Id 升序）。</summary>
    public async Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken cancellationToken = default)
        => await _db.Users
            .AsNoTracking()
            .OrderBy(u => u.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>按 Id 查账号；不存在返回 <c>null</c>。</summary>
    public async Task<AppUser?> FindAsync(int id, CancellationToken cancellationToken = default)
        => await _db.Users
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// 按登录名查账号（<b>精确匹配</b>，与登录口径一致）。
    /// 唯一性约束是大小写不敏感的，所以精确匹配不会产生歧义。
    /// </summary>
    public async Task<AppUser?> FindByNameAsync(string? userName, CancellationToken cancellationToken = default)
    {
        var name = userName?.Trim() ?? string.Empty;
        if (name.Length == 0) return null;

        return await _db.Users
            .FirstOrDefaultAsync(u => u.UserName == name, cancellationToken)
            .ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════
    // 增
    // ═══════════════════════════════════════════════════════════

    /// <summary>新建账号。新建的账号<b>不再</b>强制改密（<c>MustChangePassword = false</c>，2026-10-09 起）。</summary>
    public async Task<UserAccountResult> CreateAsync(
        string? userName,
        string? password,
        string? role,
        ActorContext actor,
        CancellationToken cancellationToken = default)
    {
        var nameError = ValidateUserName(userName);
        if (nameError is not null) return await FailureAsync(UserMutationStatus.Invalid, nameError, cancellationToken)
            .ConfigureAwait(false);

        var name = userName!.Trim();

        if (!Roles.IsValid(role))
        {
            return await FailureAsync(
                UserMutationStatus.Invalid,
                $"角色不合法，只能是 {string.Join(" / ", Roles.All)}。",
                cancellationToken).ConfigureAwait(false);
        }

        var passwordError = PasswordPolicy.Validate(password, name);
        if (passwordError is not null)
        {
            return await FailureAsync(UserMutationStatus.Invalid, passwordError, cancellationToken)
                .ConfigureAwait(false);
        }

        if (await NameTakenAsync(name, excludeId: null, cancellationToken).ConfigureAwait(false))
        {
            return await FailureAsync(
                UserMutationStatus.DuplicateName,
                $"已存在名为「{name}」的账号。",
                cancellationToken).ConfigureAwait(false);
        }

        var (hash, salt) = _hasher.Hash(password!);

        var user = new AppUser
        {
            UserName = name,
            PasswordHash = hash,
            Salt = salt,
            Role = role!,
            MustChangePassword = false,
            IsEnabled = true,
            CreatedAt = BeijingTime.Now,
        };

        _db.Users.Add(user);
        AddAudit(actor, "user-created", $"新建账号 {name}（角色 {Roles.DisplayName(role)}）");

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _log.Info("User", $"{actor.Name} 新建账号 {name}（{role}）");

        return await SuccessAsync(user, $"已新建账号「{name}」。", cancellationToken).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════
    // 改
    // ═══════════════════════════════════════════════════════════

    /// <summary>修改账号。三个字段都可选，<c>null</c> 表示不动。</summary>
    public async Task<UserAccountResult> UpdateAsync(
        int id,
        string? userName,
        string? role,
        bool? isEnabled,
        ActorContext actor,
        CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return await FailureAsync(UserMutationStatus.NotFound, $"找不到 Id = {id} 的账号。", cancellationToken)
                .ConfigureAwait(false);
        }

        var changes = new List<string>();

        // ── 用户名 ──
        if (userName is not null)
        {
            var nameError = ValidateUserName(userName);
            if (nameError is not null)
            {
                return await FailureAsync(UserMutationStatus.Invalid, nameError, cancellationToken)
                    .ConfigureAwait(false);
            }

            var name = userName.Trim();
            if (!string.Equals(name, user.UserName, StringComparison.Ordinal))
            {
                if (await NameTakenAsync(name, id, cancellationToken).ConfigureAwait(false))
                {
                    return await FailureAsync(
                        UserMutationStatus.DuplicateName,
                        $"已存在名为「{name}」的账号。",
                        cancellationToken).ConfigureAwait(false);
                }

                changes.Add($"登录名 {user.UserName} → {name}");
                user.UserName = name;
            }
        }

        // ── 角色 ──
        if (role is not null && !string.Equals(role, user.Role, StringComparison.Ordinal))
        {
            if (!Roles.IsValid(role))
            {
                return await FailureAsync(
                    UserMutationStatus.Invalid,
                    $"角色不合法，只能是 {string.Join(" / ", Roles.All)}。",
                    cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(user.Role, Roles.Admin, StringComparison.Ordinal))
            {
                // 刻意**允许**管理员给自己降级（只要还有别的启用管理员）——
                // "交接后把自己退成查看者"是正常诉求，一刀切禁掉只会逼人绕路。
                // 真正危险的是"降完以后系统没人管"，那由下面这条守卫负责。
                if (await IsLastEnabledAdminAsync(id, cancellationToken).ConfigureAwait(false))
                {
                    return await FailureAsync(
                        UserMutationStatus.LastAdmin,
                        "这是最后一个启用的管理员，不能降级 —— 系统会变成无人可管理。"
                        + "请先新建（或启用）另一位管理员。",
                        cancellationToken).ConfigureAwait(false);
                }
            }

            changes.Add($"角色 {user.Role} → {role}");
            user.Role = role;
        }

        // ── 启用状态 ──
        if (isEnabled is { } enabled && enabled != user.IsEnabled)
        {
            if (!enabled)
            {
                if (id == actor.Id)
                {
                    return await FailureAsync(
                        UserMutationStatus.SelfTarget,
                        "不能停用自己的账号。",
                        cancellationToken).ConfigureAwait(false);
                }

                if (string.Equals(user.Role, Roles.Admin, StringComparison.Ordinal)
                    && await IsLastEnabledAdminAsync(id, cancellationToken).ConfigureAwait(false))
                {
                    return await FailureAsync(
                        UserMutationStatus.LastAdmin,
                        "这是最后一个启用的管理员，不能停用 —— 系统会变成无人可管理。",
                        cancellationToken).ConfigureAwait(false);
                }
            }

            changes.Add(enabled ? "启用" : "停用");

            user.IsEnabled = enabled;

            if (!enabled)
            {
                // 停用即解除锁定：重新启用时不该继承停用前的失败计数。
                user.FailedLoginCount = 0;
                user.LockedUntil = null;
            }
        }

        if (changes.Count == 0)
        {
            // 没有实际变化就不写库、不记审计 —— 避免"点一次保存多一条审计"的噪音。
            return await SuccessAsync(user, "没有改动。", cancellationToken)
                .ConfigureAwait(false);
        }

        AddAudit(actor, "user-updated", $"{user.UserName}：{string.Join("；", changes)}");
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _log.Warn("User", $"{actor.Name} 修改账号 {user.UserName}：{string.Join("；", changes)}");

        return await SuccessAsync(user, "已保存。", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>管理员重置他人密码。重置后该账号必须再次改密。</summary>
    public async Task<UserAccountResult> ResetPasswordAsync(
        int id,
        string? newPassword,
        ActorContext actor,
        CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return await FailureAsync(UserMutationStatus.NotFound, $"找不到 Id = {id} 的账号。", cancellationToken)
                .ConfigureAwait(false);
        }

        var passwordError = PasswordPolicy.Validate(newPassword, user.UserName);
        if (passwordError is not null)
        {
            return await FailureAsync(UserMutationStatus.Invalid, passwordError, cancellationToken)
                .ConfigureAwait(false);
        }

        var (hash, salt) = _hasher.Hash(newPassword!);

        user.PasswordHash = hash;
        user.Salt = salt;
        // 重置后不再要求对方立刻改密（2026-10-09 起取消强制改密）。
        // 这里顺带把标记清掉：万一该账号此前被手工标记过，重置密码即视为一次"重新交付"。
        user.MustChangePassword = false;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;

        AddAudit(actor, "user-password-reset", $"重置 {user.UserName} 的密码");
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _log.Warn("User", $"{actor.Name} 重置了账号 {user.UserName} 的密码");

        return await SuccessAsync(user, "密码已重置。", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>用户修改<b>自己</b>的密码。必须提供当前密码。</summary>
    public async Task<string?> ChangeOwnPasswordAsync(
        int id,
        string? currentPassword,
        string? newPassword,
        CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(id, cancellationToken).ConfigureAwait(false);

        // 会话可能属于一个已被删除的账号（GodMode 之外唯一会出现这种情况的路径）。
        if (user is null) return "当前会话对应的账号已不存在，请重新登录。";

        if (!_hasher.Verify(currentPassword ?? string.Empty, user.PasswordHash, user.Salt))
        {
            _log.Warn("User", $"账号 {user.UserName} 改密失败：当前密码不正确");
            return "当前密码不正确。";
        }

        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            return "新密码不能与当前密码相同。";
        }

        var passwordError = PasswordPolicy.Validate(newPassword, user.UserName);
        if (passwordError is not null) return passwordError;

        var (hash, salt) = _hasher.Hash(newPassword!);

        user.PasswordHash = hash;
        user.Salt = salt;
        user.MustChangePassword = false;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;

        AddAudit(new ActorContext(user.Id, user.UserName, string.Empty), "password-changed", "修改自己的密码");
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _log.Info("User", $"账号 {user.UserName} 修改了自己的密码");

        return null;
    }

    // ═══════════════════════════════════════════════════════════
    // 删
    // ═══════════════════════════════════════════════════════════

    /// <summary>删除账号。</summary>
    public async Task<UserAccountResult> DeleteAsync(
        int id,
        ActorContext actor,
        CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return await FailureAsync(UserMutationStatus.NotFound, $"找不到 Id = {id} 的账号。", cancellationToken)
                .ConfigureAwait(false);
        }

        if (id == actor.Id)
        {
            return await FailureAsync(
                UserMutationStatus.SelfTarget,
                "不能删除自己的账号。",
                cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(user.Role, Roles.Admin, StringComparison.Ordinal)
            && await IsLastEnabledAdminAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return await FailureAsync(
                UserMutationStatus.LastAdmin,
                "这是最后一个启用的管理员，不能删除 —— 系统会变成无人可管理。",
                cancellationToken).ConfigureAwait(false);
        }

        var name = user.UserName;

        _db.Users.Remove(user);
        AddAudit(actor, "user-deleted", $"删除账号 {name}（角色 {Roles.DisplayName(user.Role)}）");
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _log.Warn("User", $"{actor.Name} 删除了账号 {name}");

        return await SuccessAsync(null, $"已删除账号「{name}」。", cancellationToken).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════
    // 登录记账
    // ═══════════════════════════════════════════════════════════

    /// <summary>账号当前是否处于锁定状态（<c>LockedUntil</c> 未过期）。</summary>
    public static bool IsLocked(AppUser user)
        => user.LockedUntil is { } until && until > BeijingTime.Now;

    /// <summary>
    /// 记一次登录失败：累加计数，达到阈值则锁定。
    /// </summary>
    /// <returns>本次失败导致的锁定到期时刻；未触发锁定返回 <c>null</c>。</returns>
    /// <remarks>
    /// <b>已锁定的账号不再累加、不再延长锁定期</b>。
    /// 否则每次输错都会把 <c>LockedUntil</c> 往后推 15 分钟 ——
    /// 只要持续尝试，账号就被永久锁在门外：攻击者能这么干，单纯记不住密码的人也会被自己锁死。
    /// 锁一旦落下就是个固定的时间窗，到点自动解除。
    /// </remarks>
    public async Task<DateTimeOffset?> RecordLoginFailureAsync(
        AppUser user,
        CancellationToken cancellationToken = default)
    {
        if (IsLocked(user))
        {
            return null;
        }

        user.FailedLoginCount += 1;

        DateTimeOffset? lockedUntil = null;

        if (user.FailedLoginCount >= MaxFailedAttempts)
        {
            lockedUntil = BeijingTime.Now.Add(LockDuration);
            user.LockedUntil = lockedUntil;
            user.FailedLoginCount = 0;

            _log.Warn(
                "User",
                $"账号 {user.UserName} 连续登录失败 {MaxFailedAttempts} 次，已锁定至 {lockedUntil:yyyy-MM-dd HH:mm:ss}（北京时间）");
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return lockedUntil;
    }

    /// <summary>记一次登录成功：写最近登录时刻，清空失败计数与锁定。</summary>
    public async Task RecordLoginSuccessAsync(AppUser user, CancellationToken cancellationToken = default)
    {
        user.LastLoginAt = BeijingTime.Now;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 校验用户名。返回 <c>null</c> 表示通过。
    /// <para>
    /// 刻意<b>不</b>限制成 ASCII：中文名在这类内网工具里很常见。
    /// 只挡住会让日志/审计变得无法阅读的字符（空白、控制符、路径分隔符）。
    /// </para>
    /// </summary>
    private static string? ValidateUserName(string? raw)
    {
        var name = raw?.Trim() ?? string.Empty;

        if (name.Length == 0) return "用户名不能为空。";
        if (name.Length > 64) return "用户名不能超过 64 个字符。";
        if (name.Any(char.IsWhiteSpace)) return "用户名不能包含空格或制表符。";
        if (name.Any(char.IsControl)) return "用户名不能包含控制字符。";
        if (name.Contains('/') || name.Contains('\\')) return "用户名不能包含斜杠。";

        return null;
    }

    /// <summary>
    /// 用户名是否已被占用。<b>大小写不敏感</b>（避免出现 admin / Admin 两个只差大小写的账号）。
    /// <para>
    /// 比较放在 <c>ToLower()</c> 上，SQLite 的 <c>lower()</c> 只处理 ASCII ——
    /// 对非 ASCII 名字退化为精确比较。这恰好是想要的行为：中文没有大小写概念。
    /// </para>
    /// </summary>
    private async Task<bool> NameTakenAsync(string name, int? excludeId, CancellationToken cancellationToken)
    {
        var lowered = name.ToLowerInvariant();

        var query = _db.Users.Where(u => u.UserName.ToLower() == lowered);

        if (excludeId is { } exclude)
        {
            query = query.Where(u => u.Id != exclude);
        }

        return await query.AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>除 <paramref name="excludeId"/> 之外，是否已经没有别的"启用中的管理员"。</summary>
    private async Task<bool> IsLastEnabledAdminAsync(int excludeId, CancellationToken cancellationToken)
        => !await _db.Users
            .AnyAsync(
                u => u.Id != excludeId && u.Role == Roles.Admin && u.IsEnabled,
                cancellationToken)
            .ConfigureAwait(false);

    private void AddAudit(ActorContext actor, string action, string detail)
        => _db.AuditLogs.Add(new AuditLog
        {
            Timestamp = BeijingTime.Now,
            User = actor.Name,
            Action = action,
            Detail = detail,
            RemoteIp = actor.RemoteIp,
        });

    private async Task<UserAccountResult> SuccessAsync(
        AppUser? user,
        string message,
        CancellationToken cancellationToken)
        => new(
            UserMutationStatus.Ok,
            message,
            user,
            await ListAsync(cancellationToken).ConfigureAwait(false));

    private async Task<UserAccountResult> FailureAsync(
        UserMutationStatus status,
        string message,
        CancellationToken cancellationToken)
        => new(
            status,
            message,
            null,
            await ListAsync(cancellationToken).ConfigureAwait(false));
}
