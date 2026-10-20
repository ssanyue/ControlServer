namespace ControlServer.Core.Abstractions;

/// <summary>
/// 密码哈希。
/// <para>
/// <b>历史</b>：M1 用单轮 SHA-256 加盐（<c>Sha256PasswordHasher</c>），注释里写着"待迁移"，
/// 而这次迁移发生在 <b>M6.2 安全审计</b>（2026-10-17）：现行为 PBKDF2-HMAC-SHA256。
/// </para>
/// <para>
/// <b>为什么必须保留旧格式的验证能力</b>：现有部署的库里全是 SHA-256 值，
/// 一次性全部失效等于把所有人挡在门外。因此实现必须<b>同时认两种格式</b>，
/// 并在下次成功登录时<b>就地升级</b>（见 <see cref="NeedsRehash"/>）。
/// </para>
/// </summary>
public interface IPasswordHasher
{
    (string Hash, string Salt) Hash(string password);

    bool Verify(string password, string hash, string salt);

    /// <summary>
    /// 这条已存的哈希是否<b>弱于</b>当前算法（需在下次成功校验后就地重算）。
    /// <para>
    /// 默认实现返回 <c>false</c>：给只实现了两个方法的测试替身留活路，
    /// 也保证"不知道要不要升级"时**不会**去改库（保守优先）。
    /// </para>
    /// </summary>
    bool NeedsRehash(string hash) => false;
}
