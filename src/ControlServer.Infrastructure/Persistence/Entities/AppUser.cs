namespace ControlServer.Infrastructure.Persistence.Entities;

using ControlServer.Core.Services;

/// <summary>
/// 应用用户（M1 仅管理员；<b>M3 起启用三角色与账号生命周期</b>）。
/// <para>
/// 表名 <c>users</c>，是 M1 基线就存在的表 —— M3 只<b>加列</b>，不改列、不删列，
/// 因此 <c>InitialCreate</c> 基线迁移保持冻结，老库 <c>Migrate()</c> 时走
/// <c>ALTER TABLE ADD COLUMN</c> 增量升级，用户不需要删库。
/// </para>
/// </summary>
public sealed class AppUser
{
    public int Id { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public string Salt { get; set; } = string.Empty;

    /// <summary>角色，取值见 <c>ControlServer.Contracts.Roles</c>。</summary>
    public string Role { get; set; } = "Admin";

    /// <summary>
    /// 是否必须先改密码。管理员新建账号 / 重置密码时置 <c>true</c>；
    /// 用户自己改过密码后置 <c>false</c>。
    /// </summary>
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// 创建时刻（北京时间）。
    /// <para>
    /// ⚠️ 初始值是 <c>BeijingTime.Now</c> 而**不是** <c>DateTimeOffset.UtcNow</c>：
    /// 播种路径依赖这个默认值，而前端 <c>fmtDateTime</c> 是<b>直接截取 ISO 字符串</b>
    /// （刻意不做时区换算，否则非 +8 时区的浏览器会显示错时间）。
    /// 若这里是 UTC，出厂管理员的"创建时间"会在界面上差 8 小时 ——
    /// 让"忘了显式赋值"等于"时区错"是最容易踩的坑，所以默认值本身就取北京时。
    /// </para>
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = BeijingTime.Now;

    // ═══════════════════════════════════════════════════════════
    // M3 增量（账号生命周期）
    //
    // ⚠️ 这四列**刻意不在模型上声明 `HasDefaultValue`**。EF 会把"带库端默认值的属性"
    //    在插入时按 <b>CLR 默认值哨兵</b> 跳过，而 <c>bool</c> 的哨兵是 <c>false</c> ——
    //    于是「新建一个已被禁用的账号」会被静默省略该列、落库成库默认值，
    //    账号变成启用状态。行默认值只在迁移脚本里给<b>存量行</b>用。
    // ═══════════════════════════════════════════════════════════

    /// <summary>账号是否启用。禁用后不能登录（已登录会话在 Cookie 过期前仍然有效）。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>最近一次登录成功时刻（北京时间）。从未登录为 <c>null</c>。</summary>
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>连续登录失败次数。登录成功后清零。</summary>
    public int FailedLoginCount { get; set; }

    /// <summary>锁定到期时刻（北京时间）。<c>null</c> = 未锁定；已过期视为已解锁。</summary>
    public DateTimeOffset? LockedUntil { get; set; }
}
