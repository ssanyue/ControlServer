namespace ControlServer.Core.Licensing;

/// <summary>
/// 一份已签发授权的解读结果。
/// </summary>
/// <param name="Document">解析出的文档。</param>
/// <param name="SignatureValid">签名是否与所给公钥相符。</param>
/// <param name="SignatureError">验签失败原因；成功时为空串。</param>
/// <param name="State">
/// 授权在<b>其绑定机器上</b>此刻的状态。
/// <para>
/// 注意口径：<c>inspect</c> 不掌握"执行这条命令的这台机器"的机器码，因此这里回答的是
/// "这份授权在它自己的绑定机器上会是什么状态"，而不是"在这台机器上能不能用"。
/// 两者对同一份文件可能是不同答案，展示时必须把这句话说清楚。
/// </para>
/// </param>
/// <param name="ExpiresAtUtc">解析出的到期时刻；永久或无期限为 <c>null</c>。</param>
/// <param name="PublicKeyFingerprint">本次验签所用公钥的指纹。</param>
/// <param name="SignedByVendorKey">所用公钥是否就是服务端内嵌的那把厂商公钥。</param>
public sealed record LicenseInspection(
    LicenseDocument Document,
    bool SignatureValid,
    string SignatureError,
    LicenseState State,
    DateTimeOffset? ExpiresAtUtc,
    string PublicKeyFingerprint,
    bool SignedByVendorKey);

/// <summary>
/// 授权解读（<c>LicenseTool inspect</c> 的核心）。
///
/// <para>
/// 与评估器的分工：评估器回答"这份<b>已落盘</b>的授权在本机能不能用"（要机器码、要读盘）；
/// 本类型回答"<b>给我一段授权文本</b>，它是什么、签名对不对、在它自己的机器上会怎样"。
/// 后者不需要私钥，也不需要本机机器码 —— 这正是它可用于"客户发来一份文件，先看看有没有问题"的原因。
/// </para>
/// </summary>
public static class LicenseInspector
{
    /// <summary>
    /// 通用授权在导入前没有机器码。解读时用一个自洽的占位值喂给评估器，
    /// 只为拿到"有效期 / 锚点 / 类型"这些与机器无关的结论 ——
    /// 否则评估器会在"尚未绑定"这一步就返回，解读看不到任何有用信息。
    /// </summary>
    public const string UnboundMachineCodePlaceholder = LicenseIssuer.UnboundMachineCodePlaceholder;

    /// <summary>
    /// 解读一段授权文本。
    /// </summary>
    /// <param name="text">授权文本（Base64，空白不限）。</param>
    /// <param name="publicKeyPem">用于验签的公钥 PEM。</param>
    /// <param name="nowUtc">当前时刻（UTC）。</param>
    /// <param name="clockTolerance">时钟容差。</param>
    /// <exception cref="LicenseValidationException">文本无法解析成授权文件。</exception>
    public static LicenseInspection Inspect(
        string text,
        string publicKeyPem,
        DateTimeOffset nowUtc,
        TimeSpan clockTolerance)
    {
        // 只做结构解析；签名与否是下面的结论之一，不是失败条件 ——
        // "签名不对"恰恰是现场最需要看到的信息。
        var document = LicenseCodec.Decode(text);

        using var verifier = new EcdsaLicenseVerifier(publicKeyPem);

        var signatureValid = verifier.IsAvailable
            && verifier.Verify(LicenseCodec.BuildPayload(document), document.Sig);

        var signatureError = signatureValid
            ? string.Empty
            : verifier.IsAvailable
                ? "签名与所提供的公钥不符：授权可能被改动过，或者这里用的不是签发它的那把公钥。"
                : $"无法验签：{verifier.UnavailableReason}";

        var isUniversal = string.IsNullOrWhiteSpace(document.Mc);

        var probeMachineCode = isUniversal ? UnboundMachineCodePlaceholder : document.Mc;
        var probeBinding = isUniversal ? new LicenseBinding(probeMachineCode, 0) : null;

        var state = LicenseEvaluator.Evaluate(new LicenseEvaluationInput(
            document,
            probeBinding,
            SignatureValid: signatureValid,
            SignatureError: signatureError,
            LocalMachineCode: probeMachineCode,
            NowUtc: nowUtc,
            ClockTolerance: clockTolerance));

        return new LicenseInspection(
            document,
            signatureValid,
            signatureError,
            state,
            LicenseEvaluator.ResolveExpiry(document),
            verifier.PublicKeyFingerprint,
            string.Equals(verifier.PublicKeyFingerprint, VendorPublicKey.Fingerprint, StringComparison.OrdinalIgnoreCase));
    }
}
