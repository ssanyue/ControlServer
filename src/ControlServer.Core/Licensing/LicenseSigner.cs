using System.Security.Cryptography;
using System.Text;

namespace ControlServer.Core.Licensing;

/// <summary>
/// 授权<b>签发</b>（只在签发人的机器上使用，由 <c>ControlServer.LicenseTool</c> 调用）。
///
/// <para>
/// <b>放在 Core 里而不是签发工具里</b>：签发与验签必须共用同一份载荷构造
/// （<see cref="LicenseCodec.BuildPayload"/>）。若两边各自实现，只要有一处标点不同，
/// 产出的授权就会"在自己这边验得过、到服务端全数失败"——而且签发那一刻完全看不出来。
/// 共用同一份代码，这类分歧在编译期就不可能出现。
/// </para>
///
/// <para>
/// <b>把私钥交给本类型不会泄漏到服务端</b>：服务端只注册验签器，从不调用本类型；
/// 私钥来自命令行参数指定的 PEM 文件，不内嵌、不落库、不进配置。
/// </para>
/// </summary>
public static class LicenseSigner
{
    /// <summary>
    /// 用私钥给授权文档签名，返回可直接分发的 Base64 文本。
    /// </summary>
    /// <param name="document">待签发的文档（其 <c>Sig</c> 会被覆盖）。</param>
    /// <param name="privateKeyPem">PKCS#8 私钥 PEM。</param>
    /// <exception cref="LicenseValidationException">私钥不可用。</exception>
    public static string Sign(LicenseDocument document, string privateKeyPem)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var key = LoadPrivateKey(privateKeyPem);

        SignCore(document, key);

        return LicenseCodec.Encode(document);
    }

    /// <summary>
    /// 签发并<b>立刻按服务端那条路径自检</b>，同时把与私钥配对的公钥一并交回。
    ///
    /// <para>
    /// 签发工具在写出文件前<b>必须</b>走这条路。理由很实在：授权文件一旦发到客户手上，
    /// 再发现"验不过"的返工成本极高（要重新联系统系人、重新走一遍流程）。
    /// 在本地多花 1 毫秒就能彻底消除这类返工。
    /// </para>
    ///
    /// <para>
    /// <b>自检用的公钥是从本次签名的私钥现推的</b>，而不是让调用方再传一个公钥文件。
    /// 这一点很关键：若自检拿的是磁盘上那份可能已经过期的 <c>vendor-public.pem</c>，
    /// 结果会是"签得没错却报失败"（或更糟：拿对了旧钥匙反而掩盖了新钥匙的错），
    /// 而真正的错因（公钥文件与私钥不配对）被完全隐藏。现推 = 自检的两个角色必然同源。
    /// </para>
    /// </summary>
    /// <param name="document">待签发的文档（其 <c>Sig</c> 会被覆盖）。</param>
    /// <param name="privateKeyPem">PKCS#8 私钥 PEM。</param>
    /// <returns>授权文本、与之配对的公钥 PEM、公钥指纹。</returns>
    /// <exception cref="LicenseValidationException">私钥不可用，或签出的授权未通过自检。</exception>
    public static (string Encoded, string PublicKeyPem, string Fingerprint) SignWithSelfCheck(
        LicenseDocument document,
        string privateKeyPem)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var key = LoadPrivateKey(privateKeyPem);

        var publicKeyPem = key.ExportSubjectPublicKeyInfoPem();
        var fingerprint = EcdsaLicenseVerifier.ComputeFingerprint(key);

        SignCore(document, key);

        var encoded = LicenseCodec.Encode(document);

        // 自检：解码 → 重算载荷 → 验签。与 LicenseService 读盘后的动作**逐字一致**。
        using var verifier = new EcdsaLicenseVerifier(publicKeyPem);

        if (!verifier.IsAvailable)
        {
            throw new LicenseValidationException($"签发自检无法进行：{verifier.UnavailableReason}");
        }

        if (!verifier.Verify(LicenseCodec.BuildPayload(document), document.Sig))
        {
            // 走到这里说明签发路径与验签路径对同一份文档算出了不同的字节。
            // 这**不是**操作失误，而是代码缺陷 —— 绝不能把文件写出去，必须当场失败。
            throw new LicenseValidationException(
                "签发自检失败：刚签出的授权未被本机公钥验过。这是签发/验签路径不一致的征兆，"
                + "请勿使用该文件并向开发方报告（可能是载荷字段序或签名格式被改动）。");
        }

        return (encoded, publicKeyPem, fingerprint);
    }

    /// <summary>
    /// 自检：把刚签出来的文本按<b>服务端那条路径</b>（解码 → 重算载荷 → 验签）跑一遍。
    /// <para>
    /// 供测试与"只有授权文本、没有私钥"的场景使用。签发流程本身请用
    /// <see cref="SignWithSelfCheck"/> —— 它会从私钥现推公钥，不存在配错钥匙的可能。
    /// </para>
    /// </summary>
    /// <param name="encodedLicense"><see cref="Sign"/> 的产物。</param>
    /// <param name="publicKeyPem">与之配对的公钥 PEM。</param>
    public static bool VerifyRoundTrip(string encodedLicense, string publicKeyPem)
    {
        if (!LicenseCodec.TryDecode(encodedLicense, out var document, out _)) return false;

        using var verifier = new EcdsaLicenseVerifier(publicKeyPem);

        return verifier.IsAvailable
            && verifier.Verify(LicenseCodec.BuildPayload(document!), document!.Sig);
    }

    /// <summary>把字节序列写成 PEM 文本（Pkcs8 私钥 / SubjectPublicKeyInfo 公钥）。</summary>
    public static string ExportPrivateKeyPem(ECDsa key) => key.ExportPkcs8PrivateKeyPem();

    /// <summary>导出公钥 PEM。</summary>
    public static string ExportPublicKeyPem(ECDsa key) => key.ExportSubjectPublicKeyInfoPem();

    /// <summary>
    /// 只读地描述一把私钥：返回与它配对的公钥 PEM 与公钥指纹，<b>不签名、不落盘、不回传私钥</b>。
    ///
    /// <para>
    /// 给签发工具的界面用：选定私钥后立刻显示"这把钥匙对应哪个公钥"，
    /// 于是"拿错了钥匙"能在点「签发」之前就暴露出来，而不是等客户导入时才发现。
    /// </para>
    ///
    /// <para>
    /// 返回的指纹与 <see cref="VendorPublicKey.Fingerprint"/> 可直接比对 ——
    /// 一致才意味着"用这把私钥签出来的授权，服务端会收"。
    /// </para>
    /// </summary>
    /// <param name="privateKeyPem">PKCS#8 私钥 PEM。</param>
    /// <exception cref="LicenseValidationException">私钥不可用（空、格式错、曲线不对）。</exception>
    public static (string PublicKeyPem, string Fingerprint) DescribeKeyPair(string privateKeyPem)
    {
        using var key = LoadPrivateKey(privateKeyPem);

        return (key.ExportSubjectPublicKeyInfoPem(), EcdsaLicenseVerifier.ComputeFingerprint(key));
    }

    /// <summary>
    /// 生成一对新密钥（供 <c>LicenseTool new-key</c> 使用）。
    /// <para>
    /// ⚠️ 返回的私钥<b>只应写入签发人本机的文件</b>。任何把它放进仓库、
    /// 打进安装包、或写进配置的行为都等于把签发权公开。
    /// </para>
    /// </summary>
    public static (string PrivateKeyPem, string PublicKeyPem, string Fingerprint) GenerateKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        return (
            ExportPrivateKeyPem(key),
            ExportPublicKeyPem(key),
            EcdsaLicenseVerifier.ComputeFingerprint(key));
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    /// <summary>加载并校验私钥。密钥长度固定 P-256 —— 换曲线等于换掉整套授权体系。</summary>
    private static ECDsa LoadPrivateKey(string privateKeyPem)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            throw new LicenseValidationException("未提供私钥（PEM 内容为空）。");
        }

        ECDsa key;
        try
        {
            key = ECDsa.Create();
            key.ImportFromPem(privateKeyPem);
        }
        catch (Exception ex)
        {
            throw new LicenseValidationException($"私钥加载失败：{ex.Message}", ex);
        }

        if (key.KeySize != 256)
        {
            var size = key.KeySize;
            key.Dispose();
            throw new LicenseValidationException(
                $"私钥曲线为 {size} 位，本项目的授权体系固定使用 P-256（256 位）。");
        }

        return key;
    }

    /// <summary>
    /// 把签名写入文档。⚠️ 待签名载荷**不含 <c>sig</c> 字段**，且字段序由
    /// <see cref="LicenseCodec"/> 固定 —— 这里只负责"签"，不负责"拼"。
    /// </summary>
    private static void SignCore(LicenseDocument document, ECDsa key)
    {
        var payload = Encoding.UTF8.GetBytes(LicenseCodec.BuildPayload(document));

        var signature = key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        document.Sig = LicenseCodec.ToBase64(signature);
    }
}
