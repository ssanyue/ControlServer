using ControlServer.Core.Licensing;
using FluentAssertions;

namespace ControlServer.Core.Tests.Licensing;

/// <summary>
/// 授权解读（<see cref="LicenseInspector"/>）。
/// <para>
/// 这条路径的用途是"客户把 .lic 发过来，先看看它是什么、有没有问题" ——
/// 因此它必须在<b>签名无效、缺 ivt、已过期</b>这些"坏"情形下都能给出结论，
/// 而不是抛异常。只有"根本不是授权文件"才该抛。
/// </para>
/// </summary>
public sealed class LicenseInspectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Valid_license_reports_valid_signature_and_its_expiry()
    {
        var (privateKey, publicKey) = LicenseTestKit.NewKeyPair();
        var document = LicenseTestKit.NewDocument(expiresOn: "20271008");
        var text = LicenseSigner.Sign(document, privateKey);

        var inspection = LicenseInspector.Inspect(
            text, publicKey, Now, LicenseEvaluator.DefaultClockTolerance);

        inspection.SignatureValid.Should().BeTrue();
        inspection.SignatureError.Should().BeEmpty();
        inspection.ExpiresAtUtc.Should().Be(new DateTimeOffset(2027, 10, 8, 23, 59, 59, TimeSpan.FromHours(8)));
        inspection.State.Status.Should().Be(LicenseStatus.Authorized);
        inspection.State.IsUsable.Should().BeTrue();
    }

    /// <summary>被改过的授权必须<b>报到</b>，而不是安静地算出一堆好看的字段。</summary>
    [Fact]
    public void Tampered_license_reports_invalid_signature()
    {
        var (privateKey, publicKey) = LicenseTestKit.NewKeyPair();
        var document = LicenseTestKit.NewDocument(expiresOn: "20271008");

        var original = LicenseSigner.Sign(document, privateKey);
        LicenseSigner.VerifyRoundTrip(original, publicKey)
            .Should().BeTrue("先确认原件是验得过的，否则下面的失败可能来自别的原因");

        // 改到期日却沿用原签名 —— 这正是"改文件续期"的实际形态。
        document.Ed = "20991008";
        var tampered = LicenseCodec.Encode(document);

        var inspection = LicenseInspector.Inspect(
            tampered, publicKey, Now, LicenseEvaluator.DefaultClockTolerance);

        inspection.SignatureValid.Should().BeFalse();
        inspection.SignatureError.Should().Contain("签名");
        inspection.State.Status.Should().Be(LicenseStatus.Error);
        inspection.State.IsUsable.Should().BeFalse();
    }

    [Fact]
    public void Verification_with_a_non_matching_public_key_reports_invalid_signature()
    {
        var (privateKey, _) = LicenseTestKit.NewKeyPair();
        var (_, otherPublicKey) = LicenseTestKit.NewKeyPair();
        var text = LicenseSigner.Sign(LicenseTestKit.NewDocument(), privateKey);

        var inspection = LicenseInspector.Inspect(
            text, otherPublicKey, Now, LicenseEvaluator.DefaultClockTolerance);

        inspection.SignatureValid.Should().BeFalse();
        inspection.SignedByVendorKey.Should().BeFalse();
        // 指纹必须报出来：现场最常见的错就是"核对时用错了钥匙"，没有指纹就只能靠猜。
        inspection.PublicKeyFingerprint.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>用内嵌的厂商公钥解读时，必须标出"这就是厂商那把钥匙"（签名对不对是另一回事）。</summary>
    [Fact]
    public void Inspecting_with_the_embedded_vendor_key_flags_it_as_the_vendor_key()
    {
        var (privateKey, _) = LicenseTestKit.NewKeyPair();
        var text = LicenseSigner.Sign(LicenseTestKit.NewDocument(), privateKey);

        var inspection = LicenseInspector.Inspect(
            text, VendorPublicKey.Pem, Now, LicenseEvaluator.DefaultClockTolerance);

        inspection.SignedByVendorKey.Should().BeTrue();
        inspection.PublicKeyFingerprint.Should().Be(VendorPublicKey.Fingerprint);
    }

    /// <summary>
    /// 缺 <c>ivt</c> 的手工文件：签名可以是真的（有人拿到私钥后拼的），
    /// 但解读必须把"会被服务端拒绝"这件事说出来。
    /// </summary>
    [Fact]
    public void License_without_network_anchor_is_reported_as_unusable()
    {
        var (privateKey, publicKey) = LicenseTestKit.NewKeyPair();
        var document = LicenseTestKit.NewDocument();
        document.Ivt = 0;   // 手工拼出来的文件就是这个样子
        var text = LicenseSigner.Sign(document, privateKey);

        var inspection = LicenseInspector.Inspect(
            text, publicKey, Now, LicenseEvaluator.DefaultClockTolerance);

        inspection.SignatureValid.Should().BeTrue();
        inspection.State.IsUsable.Should().BeFalse();
        inspection.State.LastError.Should().Contain("ivt");
    }

    [Fact]
    public void Expired_license_is_reported_as_expired()
    {
        var (privateKey, publicKey) = LicenseTestKit.NewKeyPair();
        var text = LicenseSigner.Sign(LicenseTestKit.NewDocument(expiresOn: "20250101"), privateKey);

        var inspection = LicenseInspector.Inspect(
            text, publicKey, Now, LicenseEvaluator.DefaultClockTolerance);

        inspection.State.Status.Should().Be(LicenseStatus.Expired);
        inspection.State.IsUsable.Should().BeFalse();
    }

    /// <summary>
    /// 通用授权在导入前没有机器码。解读不能因此短路成"尚未绑定"就完事 ——
    /// 那样客户发来一份通用授权，除了"没绑定"什么也看不到。
    /// </summary>
    [Fact]
    public void Universal_license_is_inspected_without_a_machine_code()
    {
        var (privateKey, publicKey) = LicenseTestKit.NewKeyPair();
        var document = LicenseTestKit.NewDocument(
            machineCode: string.Empty,
            universal: 1,
            importDeadlineUtcMs: Now.AddDays(30).ToUnixTimeMilliseconds());
        var text = LicenseSigner.Sign(document, privateKey);

        var inspection = LicenseInspector.Inspect(
            text, publicKey, Now, LicenseEvaluator.DefaultClockTolerance);

        inspection.SignatureValid.Should().BeTrue();
        inspection.State.IsUsable.Should().BeTrue();
        inspection.ExpiresAtUtc.Should().NotBeNull();
        inspection.Document.Ub.Should().Be(1);
        inspection.Document.Mc.Should().BeEmpty();
    }

    [Fact]
    public void Permanent_license_has_no_expiry()
    {
        var (privateKey, publicKey) = LicenseTestKit.NewKeyPair();
        var text = LicenseSigner.Sign(LicenseTestKit.NewDocument(expiresOn: null), privateKey);

        var inspection = LicenseInspector.Inspect(
            text, publicKey, Now, LicenseEvaluator.DefaultClockTolerance);

        inspection.ExpiresAtUtc.Should().BeNull();
        inspection.State.IsPerpetual.Should().BeTrue();
    }

    [Fact]
    public void Garbage_text_throws_a_readable_rejection()
    {
        Action notBase64 = () => LicenseInspector.Inspect(
            "这不是授权文件", VendorPublicKey.Pem, Now, LicenseEvaluator.DefaultClockTolerance);

        notBase64.Should().Throw<LicenseValidationException>().WithMessage("*Base64*");

        // 合法 Base64、合法 JSON 对象，但没有签名 —— 报错必须指向缺签名，而不是笼统的"格式错误"。
        Action noSignature = () => LicenseInspector.Inspect(
            "e30=", VendorPublicKey.Pem, Now, LicenseEvaluator.DefaultClockTolerance);

        noSignature.Should().Throw<LicenseValidationException>().WithMessage("*签名*");
    }
}
