using System.Security.Cryptography;
using System.Text;

namespace ControlServer.Core.Services;

/// <summary>把任意指纹规范化为 XXXX-XXXX-XXXX-XXXX 机器码。</summary>
public static class MachineCodeFormatter
{
    public static string ToMachineCode(string fingerprint)
    {
        var source = string.IsNullOrWhiteSpace(fingerprint) ? Environment.MachineName : fingerprint;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        var hex = Convert.ToHexString(hash)[..16];
        return string.Join('-', Enumerable.Range(0, 4).Select(i => hex.Substring(i * 4, 4)));
    }
}
