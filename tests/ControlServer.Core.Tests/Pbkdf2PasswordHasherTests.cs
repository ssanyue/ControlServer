using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 口令哈希（M6.2：单轮 SHA-256 → PBKDF2-HMAC-SHA256）。
/// <para>
/// 这里钉四件事，每一条都对应一个"改坏了会静默出事"的方向：
/// <list type="number">
///   <item><b>新值真的用了 PBKDF2，且参数写在值里</b> —— 否则将来调迭代数会让所有老值失效；</item>
///   <item><b>旧值必须还能验</b> —— 删掉兼容分支 ＝ 存量部署全员锁死；</item>
///   <item><b>脏值判失败而不是抛异常</b> —— 库里一行坏了不该让登录变成 500；</item>
///   <item><b>同一口令两次 Hash 结果不同</b> —— 盐必须是随机且逐次变化的。</item>
/// </list>
/// </para>
/// </summary>
public sealed class Pbkdf2PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new();

    [Fact]
    public void Hash_emits_self_describing_value()
    {
        var (hash, salt) = _hasher.Hash("Sup3r-Secret");

        hash.Should().StartWith(Pbkdf2PasswordHasher.Prefix);

        var parts = hash.Split('$');
        parts.Should().HaveCount(4, "格式是 算法$迭代数$盐$密钥");

        parts[1].Should().Be(Pbkdf2PasswordHasher.Iterations.ToString());

        // 盐列照旧写一份（老脚本/备份比对按"盐在哪一列"取值），且与新格式里的盐一致。
        salt.Should().Be(parts[2]);
        parts[2].Should().HaveLength(32, "16 字节盐 = 32 个十六进制字符");
        parts[3].Should().HaveLength(64, "32 字节派生密钥 = 64 个十六进制字符");
    }

    [Fact]
    public void Verify_accepts_correct_password_and_rejects_wrong_one()
    {
        var (hash, salt) = _hasher.Hash("Sup3r-Secret");

        _hasher.Verify("Sup3r-Secret", hash, salt).Should().BeTrue();
        _hasher.Verify("sup3r-secret", hash, salt).Should().BeFalse("口令区分大小写");
        _hasher.Verify("Sup3r-Secret ", hash, salt).Should().BeFalse("尾随空格不忽略");
        _hasher.Verify(string.Empty, hash, salt).Should().BeFalse();
    }

    [Fact]
    public void Same_password_gets_different_hash_each_time()
    {
        var first = _hasher.Hash("Sup3r-Secret");
        var second = _hasher.Hash("Sup3r-Secret");

        first.Salt.Should().NotBe(second.Salt, "盐必须逐次随机");
        first.Hash.Should().NotBe(second.Hash, "同样的口令不能产生同样的值（防彩虹表/防撞见）");
    }

    /// <summary>
    /// 兼容 M1 的旧值。<b>这条用例是"存量部署还能不能登录"的唯一证据</b>。
    /// </summary>
    [Fact]
    public void Verify_still_accepts_legacy_single_round_sha256()
    {
        // 与 Sha256PasswordHasher 完全相同的算法，在这里独立算一遍 ——
        // 而不是调用被测代码去生成期望值（那等于用同一份实现自证）。
        const string salt = "0123456789ABCDEF0123456789ABCDEF";
        var legacyHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(salt + ":Legacy-Pass")));

        _hasher.Verify("Legacy-Pass", legacyHash, salt).Should().BeTrue();
        _hasher.Verify("Not-The-Pass", legacyHash, salt).Should().BeFalse();
    }

    [Fact]
    public void Legacy_value_is_flagged_for_rehash_but_new_one_is_not()
    {
        const string salt = "0123456789ABCDEF0123456789ABCDEF";
        var legacyHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(salt + ":Legacy-Pass")));

        _hasher.NeedsRehash(legacyHash).Should().BeTrue("旧格式必须在下次成功登录时升级");

        var (fresh, _) = _hasher.Hash("Sup3r-Secret");
        _hasher.NeedsRehash(fresh).Should().BeFalse("刚生成的值不该被反复重算");
    }

    [Fact]
    public void Value_with_lower_iteration_count_is_flagged_for_rehash()
    {
        // 手工造一个"当年用 1000 次迭代存下来的值"：它必须**能验**（用值里的 1000），
        // 同时被标记为需要升级（低于当前档）。
        const int oldIterations = 1000;
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var key = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes("Old-Pass"),
            salt,
            oldIterations,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            32);

        var hash = string.Concat(
            Pbkdf2PasswordHasher.Prefix,
            oldIterations.ToString(), "$",
            Convert.ToHexString(salt), "$",
            Convert.ToHexString(key));

        _hasher.Verify("Old-Pass", hash, Convert.ToHexString(salt)).Should().BeTrue(
            "验证要用值里写的迭代数，不是当前常量 —— 否则调高默认值等于全员锁死");

        _hasher.NeedsRehash(hash).Should().BeTrue();
    }

    [Fact]
    public void Malformed_values_are_rejected_without_throwing()
    {
        // 库里一行坏值不该把"登录"变成 500（那会让局部损坏看起来像整站故障）。
        _hasher.Verify("x", string.Empty, "salt").Should().BeFalse();
        _hasher.Verify("x", "not-a-hash", "salt").Should().BeFalse();
        _hasher.Verify("x", "pbkdf2-sha256$", "salt").Should().BeFalse();
        _hasher.Verify("x", "pbkdf2-sha256$abc$def$ghi", "salt").Should().BeFalse();
        _hasher.Verify("x", "pbkdf2-sha256$0$00$00", "salt").Should().BeFalse("迭代数 0 不合法");
        _hasher.Verify("x", "pbkdf2-sha256$1000$zz$zz", "salt").Should().BeFalse("非十六进制");
    }

    [Fact]
    public void Tampered_key_does_not_verify()
    {
        var (hash, salt) = _hasher.Hash("Sup3r-Secret");

        var parts = hash.Split('$');
        parts[3] = new string('A', parts[3].Length);

        _hasher.Verify("Sup3r-Secret", string.Join('$', parts), salt).Should().BeFalse();
    }
}
