using ControlServer.Core.Licensing;
using FluentAssertions;

namespace ControlServer.Core.Tests.Licensing;

/// <summary>
/// ECDSA 验签器与签发器。
/// </summary>
public sealed class EcdsaLicenseVerifierTests
{
    [Fact]
    public void Accepts_signature_produced_by_the_matching_private_key()
    {
        var (privateKey, publicKey) = LicenseTestKit.NewKeyPair();
        var document = LicenseTestKit.NewDocument();

        var license = LicenseTestKit.Issue(document, privateKey);

        LicenseTestKit.Verify(license, publicKey).Should().BeTrue();
    }

    [Fact]
    public void Rejects_signature_when_payload_was_altered_after_signing()
    {
        var (privateKey, publicKey) = LicenseTestKit.NewKeyPair();
        var document = LicenseTestKit.NewDocument(expiresOn: "20261101");

        var license = LicenseTestKit.Issue(document, privateKey);

        // 攻击者最自然的动作：把到期日往后改。签名必须立刻失配。
        var tampered = LicenseCodec.Decode(license);
        tampered.Ed = "20991231";

        _Verify(tampered, publicKey).Should().BeFalse();
    }

    [Fact]
    public void Rejects_license_signed_by_a_different_vendor_key()
    {
        var (realPrivate, realPublic) = LicenseTestKit.NewKeyPair();
        var (impostorPrivate, _) = LicenseTestKit.NewKeyPair();

        // 冒充者用自己的私钥签一份"永久授权"。
        var forged = LicenseTestKit.Issue(
            LicenseTestKit.NewDocument(expiresOn: null, kind: LicenseKind.Full), impostorPrivate);

        LicenseTestKit.Verify(forged, realPublic).Should().BeFalse();
        _ = realPrivate;
    }

    [Fact]
    public void Rejects_signature_when_a_signed_field_is_blanked_out()
    {
        var (privateKey, publicKey) = LicenseTestKit.NewKeyPair();
        var license = LicenseTestKit.Issue(LicenseTestKit.NewDocument(), privateKey);

        var tampered = LicenseCodec.Decode(license);
        tampered.Ivt = 0;   // 抹掉联网锚点 —— 正是"改文件绕过在线校对"的手法

        _Verify(tampered, publicKey).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-signature")]
    [InlineData("!!!")]
    public void Returns_false_for_malformed_signature_instead_of_throwing(string signature)
    {
        var (_, publicKey) = LicenseTestKit.NewKeyPair();
        using var verifier = new EcdsaLicenseVerifier(publicKey);

        var act = () => verifier.Verify("{\"v\":1}", signature);

        act.Should().NotThrow();
        act().Should().BeFalse();
    }

    [Fact]
    public void Returns_false_for_empty_payload()
    {
        var (_, publicKey) = LicenseTestKit.NewKeyPair();
        using var verifier = new EcdsaLicenseVerifier(publicKey);

        verifier.Verify(string.Empty, "AAAA").Should().BeFalse();
    }

    [Fact]
    public void Is_unavailable_when_public_key_cannot_be_loaded()
    {
        using var verifier = new EcdsaLicenseVerifier("-----BEGIN PUBLIC KEY-----\nnonsense\n-----END PUBLIC KEY-----");

        verifier.IsAvailable.Should().BeFalse();
        verifier.UnavailableReason.Should().NotBeNullOrWhiteSpace();
        verifier.Verify("payload", "AAAA").Should().BeFalse("公钥不可用时必须 fail-closed");
    }

    [Fact]
    public void Is_unavailable_when_public_key_is_blank()
    {
        using var verifier = new EcdsaLicenseVerifier(string.Empty);

        verifier.IsAvailable.Should().BeFalse();
        verifier.Verify("payload", "AAAA").Should().BeFalse();
    }

    /// <summary>
    /// 守门用例：内嵌的厂商公钥必须真的能加载，且指纹常量与公钥一致。
    /// <para>
    /// 这两条一旦不成立，症状是"所有授权都导入失败"，而现场第一反应会是"文件发错了"。
    /// 把它们钉在单测里，问题在提测那一刻就暴露。
    /// </para>
    /// </summary>
    [Fact]
    public void Embedded_vendor_public_key_is_loadable_and_fingerprint_matches()
    {
        using var verifier = EcdsaLicenseVerifier.FromVendorPublicKey();

        verifier.IsAvailable.Should().BeTrue(verifier.UnavailableReason);
        verifier.PublicKeyFingerprint.Should().Be(
            VendorPublicKey.Fingerprint,
            "换密钥后必须同步更新 VendorPublicKey.Fingerprint，否则诊断信息会说谎");
    }

    [Fact]
    public void Dispose_stops_accepting_signatures()
    {
        var (privateKey, publicKey) = LicenseTestKit.NewKeyPair();
        var license = LicenseTestKit.Issue(LicenseTestKit.NewDocument(), privateKey);

        var verifier = new EcdsaLicenseVerifier(publicKey);
        var document = LicenseCodec.Decode(license);

        verifier.Verify(LicenseCodec.BuildPayload(document), document.Sig).Should().BeTrue();

        verifier.Dispose();

        verifier.IsAvailable.Should().BeFalse();
        verifier.Verify(LicenseCodec.BuildPayload(document), document.Sig).Should().BeFalse();
    }

    private static bool _Verify(LicenseDocument document, string publicKeyPem)
    {
        using var verifier = new EcdsaLicenseVerifier(publicKeyPem);
        return verifier.Verify(LicenseCodec.BuildPayload(document), document.Sig);
    }
}

public sealed class LicenseSignerTests
{
    [Fact]
    public void Sign_then_verify_roundtrip_succeeds()
    {
        var (privateKey, publicKey) = LicenseTestKit.NewKeyPair();
        var document = LicenseTestKit.NewDocument();

        var license = LicenseSigner.Sign(document, privateKey);

        LicenseSigner.VerifyRoundTrip(license, publicKey).Should().BeTrue();
        document.Sig.Should().NotBeNullOrWhiteSpace("签发应当把签名回填到文档上");
    }

    [Fact]
    public void VerifyRoundTrip_fails_for_the_wrong_public_key()
    {
        var (privateKey, _) = LicenseTestKit.NewKeyPair();
        var (_, otherPublicKey) = LicenseTestKit.NewKeyPair();

        var license = LicenseSigner.Sign(LicenseTestKit.NewDocument(), privateKey);

        LicenseSigner.VerifyRoundTrip(license, otherPublicKey).Should().BeFalse();
    }

    [Fact]
    public void Sign_produces_a_different_signature_each_time()
    {
        var (privateKey, _) = LicenseTestKit.NewKeyPair();

        // ECDSA 是随机化签名：同样内容两次签名结果不同是**正确的**（防止签名本身成为指纹）。
        // 这条用例同时提醒：不能靠"签名串是否相同"判断两份授权是不是同一份。
        var first = LicenseSigner.Sign(LicenseTestKit.NewDocument(), privateKey);
        var second = LicenseSigner.Sign(LicenseTestKit.NewDocument(), privateKey);

        first.Should().NotBe(second);
    }

    [Fact]
    public void Sign_rejects_blank_private_key()
    {
        Action act = () => LicenseSigner.Sign(LicenseTestKit.NewDocument(), "  ");

        act.Should().Throw<LicenseValidationException>().WithMessage("*私钥*");
    }

    [Fact]
    public void Sign_rejects_garbage_private_key()
    {
        Action act = () => LicenseSigner.Sign(LicenseTestKit.NewDocument(), "not a pem");

        act.Should().Throw<LicenseValidationException>();
    }

    [Fact]
    public void GenerateKeyPair_produces_a_matching_pair()
    {
        var (privatePem, publicPem, fingerprint) = LicenseSigner.GenerateKeyPair();

        privatePem.Should().Contain("BEGIN PRIVATE KEY");
        publicPem.Should().Contain("BEGIN PUBLIC KEY");
        fingerprint.Should().MatchRegex("^[0-9A-F]{16}$");

        var license = LicenseSigner.Sign(LicenseTestKit.NewDocument(), privatePem);
        LicenseSigner.VerifyRoundTrip(license, publicPem).Should().BeTrue();
    }
}
