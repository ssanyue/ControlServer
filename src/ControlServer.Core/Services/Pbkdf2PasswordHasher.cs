using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Core.Abstractions;

namespace ControlServer.Core.Services;

/// <summary>
/// PBKDF2-HMAC-SHA256 口令哈希（<b>M6.2</b>），并<b>兼容</b> M1 的单轮 SHA-256 旧值。
///
/// <para><b>为什么换</b>：单轮 SHA-256 的代价是"一次哈希"，与现代 GPU 的每秒百亿次量级相比
/// 等于明文。而本系统的库文件会<b>离开服务器</b>——M6.1 的备份产物就是一份 <c>control.db</c>，
/// 它会躺在 NAS、U 盘、同事的桌面上。一份能离线秒破的口令表，等于把所有人的口令一起送出去。</para>
///
/// <para><b>迭代次数</b>：<see cref="Iterations"/> = 210 000，取 OWASP 2023 对
/// PBKDF2-HMAC-SHA256 的建议档。实测单次约 40–90 ms，登录是低频动作，这点延迟买得到的是
/// "离线爆破成本放大 20 万倍"。⚠️ <b>不要为了"快一点"调低</b>——它调的正是唯一的安全参数。</para>
///
/// <para><b>存储格式</b>（<c>PasswordHash</c> 列，自描述，含算法与参数）：</para>
/// <code>pbkdf2-sha256$210000$&lt;salt 十六进制&gt;$&lt;派生密钥十六进制&gt;</code>
/// <para>
/// 把迭代数写进值里，是为了将来提高 <see cref="Iterations"/> 时<b>老值仍然可验</b>：
/// 验的时候用值里写的那个数，而不是当前的常量。否则"调高默认值"＝"所有人登不进来"。
/// </para>
/// <para>
/// <c>Salt</c> 列照旧写一份同样的盐（32 个十六进制字符）。它在新格式里是冗余的，
/// 但保留是为了让备份比对、DB 直查、以及任何按"盐在哪一列"写的老脚本都不必改。
/// </para>
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    /// <summary>当前默认迭代次数（OWASP 2023 建议档）。</summary>
    public const int Iterations = 210_000;

    /// <summary>派生密钥长度（字节）。</summary>
    private const int KeyBytes = 32;

    /// <summary>盐长度（字节）。16 字节 = 128 位，足够避免彩虹表与跨用户复用。</summary>
    private const int SaltBytes = 16;

    /// <summary>新格式的前缀（含版本化的算法名）。</summary>
    public const string Prefix = "pbkdf2-sha256$";

    public (string Hash, string Salt) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Derive(password, salt, Iterations);

        var hash = string.Concat(
            Prefix,
            Iterations.ToString(CultureInfo.InvariantCulture), "$",
            Convert.ToHexString(salt), "$",
            Convert.ToHexString(key));

        return (hash, Convert.ToHexString(salt));
    }

    /// <summary>
    /// 校验口令。<b>两条路径的耗时不同</b>（PBKDF2 慢、SHA-256 快），
    /// 因此调用方对"账号不存在"也必须走一次 <see cref="Verify"/>，
    /// 否则响应时间会直接泄漏"这个用户名存不存在"。
    /// </summary>
    public bool Verify(string password, string hash, string salt)
    {
        if (string.IsNullOrEmpty(hash)) return false;

        if (hash.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return VerifyPbkdf2(password, hash);
        }

        // ── 旧格式：SHA-256(salt + ":" + password) ──
        // 不是"顺手兼容"，而是**必须留着**：存量库全是这种值，删掉这行等于全员锁死。
        return VerifyLegacySha256(password, hash, salt);
    }

    /// <summary>旧格式，或迭代数低于当前档 ⇒ 登录成功后应就地重算。</summary>
    public bool NeedsRehash(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return true;
        if (!hash.StartsWith(Prefix, StringComparison.Ordinal)) return true;

        var parts = hash.Split('$');
        if (parts.Length != 4) return true;

        return !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations)
            || iterations < Iterations;
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    private static bool VerifyPbkdf2(string password, string hash)
    {
        var parts = hash.Split('$');

        // 四段：算法、迭代数、盐、密钥。任何一段缺失都判失败（不抛异常 ——
        // 库里的脏值不该把登录变成 500，那会让"某一行坏了"升级成"整站登不进来"）。
        if (parts.Length != 4) return false;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations)
            || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;

        try
        {
            salt = Convert.FromHexString(parts[2]);
            expected = Convert.FromHexString(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0) return false;

        var actual = Derive(password, salt, iterations);

        // 长度不等时 FixedTimeEquals 会直接返回 false —— 这不是时序泄漏，
        // 因为长度是公开的（它写在库里，不由口令决定）。
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool VerifyLegacySha256(string password, string hash, string salt)
    {
        var computed = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(salt + ":" + password)));

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(hash));
    }

    private static byte[] Derive(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
}
