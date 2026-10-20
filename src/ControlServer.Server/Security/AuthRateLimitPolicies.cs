namespace ControlServer.Server.Security;

/// <summary>
/// 限速策略名（M6.2）。<b>只在这里定义一次</b>。
/// <para>
/// 名字用在两处：<c>Program.cs</c> 注册策略、控制器上标注 <c>[EnableRateLimiting(...)]</c>。
/// 两处各写一个字面量的话，改名时必然只改对一边 —— 而"策略名对不上"的后果不是报错，
/// 是<b>限速静默失效</b>：端点照常工作，没人发现那层保护已经不在了。
/// </para>
/// </summary>
public static class AuthRateLimitPolicies
{
    /// <summary>登录端点：按来源 IP 的固定窗口。</summary>
    public const string Login = "auth-login";
}
