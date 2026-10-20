using System.Security.Cryptography;
using System.Text;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// 用 DPAPI 保护本地敏感数据（M4.1：客户端 Agent 的 agentKey）。
/// <para>
/// <b>为什么必须是 <see cref="DataProtectionScope.LocalMachine"/> 而不是 CurrentUser</b>：
/// 客户端是"服务 + 托盘"两个进程 —— 前者以 <b>SYSTEM</b> 运行，后者以<b>登录用户</b>运行。
/// 凭据由服务写入、托盘也要读（显示状态、打开详情页），CurrentUser 作用域下两者互相解不开，
/// 表现为"托盘永远显示未注册"。
/// </para>
/// <para>
/// <b>代价（必须知情）</b>：LocalMachine 作用域意味着<b>本机任何用户</b>都能解密。
/// 这是让两个进程共用一份凭据所必须付的价；相比"明文躺在 <c>%ProgramData%</c> 里"
/// （任何用户 <c>type</c> 一下就拿到），仍然是实质提升。真正的多用户隔离要等
/// "每用户独立凭据"这类设计，本轮不做。
/// </para>
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ControlServer.Agent.v1");

    public string Protect(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI 仅在 Windows 上可用。");
        }

        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plainText),
            Entropy,
            DataProtectionScope.LocalMachine);

        return Convert.ToBase64String(encrypted);
    }

    public string Unprotect(string protectedText)
    {
        ArgumentNullException.ThrowIfNull(protectedText);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI 仅在 Windows 上可用。");
        }

        var decrypted = ProtectedData.Unprotect(
            Convert.FromBase64String(protectedText),
            Entropy,
            DataProtectionScope.LocalMachine);

        return Encoding.UTF8.GetString(decrypted);
    }
}
