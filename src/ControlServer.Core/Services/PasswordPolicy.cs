namespace ControlServer.Core.Services;

/// <summary>
/// 密码策略（M3）。
/// <para>
/// 只做<b>新密码</b>的准入校验 —— 登录时不校验（老账号里已存在的弱密码不应该把用户锁在门外，
/// 而应该在他下次改密时被政策拦下）。
/// </para>
/// <para>
/// 刻意<b>不</b>强制"大小写+数字+符号"这类复杂度组合：
/// 它会把"一句话口令"（长度换熵）这种更安全的做法判为非法，
/// 也会让中文口令难以通过。长度下限 + 不许等于用户名 + 不许用随程序发布的默认口令，
/// 是性价比最高的三条。
/// </para>
/// </summary>
public static class PasswordPolicy
{
    /// <summary>最小长度。</summary>
    public const int MinLength = 8;

    /// <summary>
    /// 最大长度。设限是为了防止把超长输入喂给哈希函数做无谓的计算量（拒绝服务面）。
    /// 128 对任何人写得出的一口令都绰绰有余。
    /// </summary>
    public const int MaxLength = 128;

    /// <summary>
    /// 随程序发布的默认管理员口令。它写在 <c>appsettings.json</c> 与部署文档里，
    /// 等于<b>公开值</b>，因此禁止被任何人设为新口令。
    /// </summary>
    public const string ShippedDefaultPassword = "admin123";

    /// <summary>
    /// 校验新密码。
    /// </summary>
    /// <param name="password">待校验的口令。</param>
    /// <param name="userName">该账号的用户名；用于"口令不得与用户名相同"这一条。传 <c>null</c> 则跳过该条。</param>
    /// <returns>通过返回 <c>null</c>；否则返回一句面向用户的错误说明。</returns>
    public static string? Validate(string? password, string? userName = null)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "密码不能为空。";
        }

        if (password.Length < MinLength)
        {
            return $"密码至少 {MinLength} 位。";
        }

        if (password.Length > MaxLength)
        {
            return $"密码不能超过 {MaxLength} 位。";
        }

        if (string.Equals(password, ShippedDefaultPassword, StringComparison.Ordinal))
        {
            return "不能使用随程序发布的默认密码，请另设一个。";
        }

        if (!string.IsNullOrWhiteSpace(userName)
            && string.Equals(password, userName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "密码不能与用户名相同。";
        }

        return null;
    }

    /// <summary>便捷判定：策略是否通过。</summary>
    public static bool IsValid(string? password, string? userName = null)
        => Validate(password, userName) is null;
}
