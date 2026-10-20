using System.Security.Cryptography;
using System.Text;
using ControlServer.Core.Abstractions;

namespace ControlServer.Core.Licensing;

/// <summary>
/// ECDSA P-256（NIST P-256 / secp256r1）+ SHA-256 的授权验签器。
///
/// <para>
/// <b>为什么是 P-256 而不是 RSA</b>：签名字节数 64 vs 256，公钥 PEM 只有 178 字节 ——
/// 授权文本短一截，粘贴出错的机会就少一截；验签速度也更快。安全性上两者对"厂商签发给客户"
/// 这个场景都远远够用。.NET 8 内置 <see cref="ECDsa"/>，<b>零第三方依赖</b>。
/// </para>
///
/// <para>
/// <b>签名格式必须显式指定 IEEE-P1363</b>。<see cref="ECDsa"/> 的默认格式恰好就是它，
/// 但"恰好"不能作为契约：一旦将来某处换成 DER 格式（.NET 也支持），
/// 症状会是"所有历史授权突然全部验不过"，且很难联想到是格式问题。所以这里两处都写死。
/// </para>
///
/// <para>
/// 公钥在构造时加载一次并长期持有。<see cref="ECDsa"/> <b>不是线程安全的</b>，
/// 因此所有验签都串行化在一把锁上。授权验签是低频操作（每次状态刷新一次），
/// 串行化的代价可以忽略，换来的是彻底避免并发下的间歇性验签失败。
/// </para>
/// </summary>
public sealed class EcdsaLicenseVerifier : ILicenseSignatureVerifier, IDisposable
{
    private readonly ECDsa? _publicKey;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>用指定的公钥 PEM 构造（单测注入自己生成的密钥对）。</summary>
    public EcdsaLicenseVerifier(string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            UnavailableReason = "公钥为空。";
            PublicKeyFingerprint = string.Empty;
            return;
        }

        try
        {
            var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);

            _publicKey = key;
            PublicKeyFingerprint = ComputeFingerprint(key);
        }
        catch (Exception ex)
        {
            // 公钥加载失败 = 服务端自身的配置/构建问题。这里不抛：抛出去会让整个服务起不来，
            // 而正确的降级行为是"授权一律判为不可用"并给出明确原因（fail-closed）。
            UnavailableReason = $"公钥加载失败：{ex.Message}";
            PublicKeyFingerprint = string.Empty;
        }
    }

    /// <summary>用编译期内嵌的厂商公钥构造（生产路径）。</summary>
    public static EcdsaLicenseVerifier FromVendorPublicKey() => new(VendorPublicKey.Pem);

    public bool IsAvailable => _publicKey is not null && !_disposed;

    public string? UnavailableReason { get; }

    public string PublicKeyFingerprint { get; }

    public bool Verify(string payload, string base64Signature)
    {
        if (_publicKey is null || _disposed) return false;
        if (string.IsNullOrEmpty(payload) || string.IsNullOrWhiteSpace(base64Signature)) return false;

        byte[] signature;
        try
        {
            signature = LicenseCodec.DecodeBase64(base64Signature);
        }
        catch (Exception)
        {
            // 畸形签名是**预期输入**（文件被改过），不是异常。
            return false;
        }

        var data = Encoding.UTF8.GetBytes(payload);

        lock (_gate)
        {
            try
            {
                return _publicKey.VerifyData(
                    data,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _publicKey?.Dispose();
    }

    /// <summary>
    /// 公钥指纹：<b>公钥 SPKI(DER) 的 SHA-256 前 8 字节</b>（十六进制大写，16 个字符）。
    /// <para>
    /// <b>本方法是全项目指纹算法唯一定义处</b>。签发工具用它报告"刚生成的是哪把钥匙"，
    /// 验签器用它报告"当前在用的是哪把钥匙"，两边的字符串要能直接比对 ——
    /// 一旦两处各写一遍，任何一处改动（哪怕是大小写）都会让诊断信息开始说谎。
    /// </para>
    /// <para>取前 8 字节是刻意的：指纹用于"人眼核对 + 日志比对"，不是安全边界（防伪靠签名本身）。</para>
    /// </summary>
    public static string ComputeFingerprint(ECDsa key)
    {
        try
        {
            var der = key.ExportSubjectPublicKeyInfo();
            var hash = SHA256.HashData(der);
            return Convert.ToHexString(hash.AsSpan(0, 8));
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
