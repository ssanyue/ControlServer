using ControlServer.Core.Licensing;
using FluentAssertions;

namespace ControlServer.Core.Tests.Licensing;

/// <summary>
/// 签发规则（<see cref="LicenseIssuer"/>）。这些用例的价值不在"代码能跑"，
/// 而在把"什么算一份合法的授权"逐条钉死 —— 签发侧一旦放行一份坏文件，
/// 代价是客户现场导入失败、重新走一遍签发流程。
/// </summary>
public sealed class LicenseIssuerTests
{
    private const string MachineCode = "AAAA-BBBB-CCCC-DDDD";
    private const string OtherMachineCode = "1111-2222-3333-4444";

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    // ═══════════════════════════════════════════════════════════
    // 正常路径
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Bound_license_with_date_expiry_is_issued_and_verifies_against_its_own_public_key()
    {
        var (privateKey, publicKey) = LicenseTestKit.NewKeyPair();

        var result = LicenseIssuer.Issue(Bound(machineCode: MachineCode), privateKey);

        result.Encoded.Should().NotBeNullOrWhiteSpace();

        // 走完整的服务端路径：解码 → 重算载荷 → 用签发自检带回的公钥验签。
        LicenseSigner.VerifyRoundTrip(result.Encoded, result.PublicKeyPem).Should().BeTrue();
        result.PublicKeyPem.Should().Be(publicKey);

        var decoded = LicenseCodec.Decode(result.Encoded);
        decoded.Sig.Should().NotBeNullOrWhiteSpace();
        decoded.Mc.Should().Be(MachineCode);
        decoded.Lt.Should().Be(LicenseKind.Full);
        decoded.V.Should().Be(LicenseDocument.CurrentVersion);
        decoded.Ub.Should().Be(0);
    }

    [Fact]
    public void Issued_license_carries_issue_date_and_binding_time()
    {
        var (privateKey, _) = LicenseTestKit.NewKeyPair();

        var result = LicenseIssuer.Issue(Bound(), privateKey);

        // 2026-10-08T12:00Z 的北京日期是 20261008（20:00）。
        result.Document.Id.Should().Be("20261008");
        // 绑定式授权的 bt = 签发时刻（见 LicenseDocument.Bt 的语义）。
        result.Document.Bt.Should().Be(Now.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Anchor_is_written_into_the_ivt_field()
    {
        var (privateKey, _) = LicenseTestKit.NewKeyPair();
        var anchor = Now.AddHours(-3);

        var result = LicenseIssuer.Issue(Bound() with { AnchorUtc = anchor }, privateKey);

        result.Document.Ivt.Should().Be(anchor.ToUnixTimeMilliseconds());
    }

    [Theory]
    [InlineData("a1b2c3d4e5f60718", "A1B2-C3D4-E5F6-0718")]
    [InlineData("A1B2-C3D4-E5F6-0718", "A1B2-C3D4-E5F6-0718")]
    [InlineData("  a1b2 c3d4 e5f6 0718  ", "A1B2-C3D4-E5F6-0718")]
    public void Machine_code_is_normalized_to_the_canonical_form(string input, string expected)
    {
        var (privateKey, _) = LicenseTestKit.NewKeyPair();

        var result = LicenseIssuer.Issue(Bound(machineCode: input), privateKey);

        result.Document.Mc.Should().Be(expected);
    }

    [Fact]
    public void Universal_license_leaves_machine_code_empty_and_sets_the_universal_flag()
    {
        var (privateKey, _) = LicenseTestKit.NewKeyPair();
        var request = Universal();

        var result = LicenseIssuer.Issue(request, privateKey);

        result.Document.Mc.Should().BeEmpty();
        result.Document.Ub.Should().Be(1);
        // 通用授权此时尚未绑定，bt 必须留 0 —— 否则"绑定时刻"会被误读成签发时刻。
        result.Document.Bt.Should().Be(0);
        result.Document.Ud.Should().Be(request.UseDeadlineUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Perpetual_license_for_a_bound_machine_is_allowed()
    {
        var (privateKey, _) = LicenseTestKit.NewKeyPair();

        var result = LicenseIssuer.Issue(Bound() with { ExpiryDate = string.Empty }, privateKey);

        LicenseEvaluator.ResolveExpiry(result.Document).Should().BeNull();

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(result.Document));
        state.IsPerpetual.Should().BeTrue();
    }

    [Fact]
    public void Trial_license_with_max_days_is_allowed()
    {
        var (privateKey, _) = LicenseTestKit.NewKeyPair();

        var result = LicenseIssuer.Issue(
            Bound() with { Kind = LicenseKind.Trial, ExpiryDate = string.Empty, MaxDays = 30 },
            privateKey);

        result.Document.Md.Should().Be(30);

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(result.Document));
        state.Status.Should().Be(LicenseStatus.Trial);
        state.IsUsable.Should().BeTrue();
    }

    [Fact]
    public void Exact_expiry_is_written_into_et_and_takes_effect()
    {
        var (privateKey, _) = LicenseTestKit.NewKeyPair();
        var exact = Now.AddDays(7);

        var result = LicenseIssuer.Issue(
            Bound() with { ExpiryDate = string.Empty, ExactExpiryUtcMs = exact.ToUnixTimeMilliseconds() },
            privateKey);

        result.Document.Et.Should().Be(exact.ToUnixTimeMilliseconds());
        result.Document.Ed.Should().BeEmpty();
        LicenseEvaluator.ResolveExpiry(result.Document).Should().Be(exact);
    }

    /// <summary>
    /// 最高价值的用例：签出来的东西必须能过<b>服务端自己的导入规则</b>。
    /// 这条一旦失败，"签发工具能签出可用的 .lic"这个验收标准就不成立。
    /// </summary>
    [Fact]
    public void Issued_bound_license_passes_the_import_rules_on_its_bound_machine()
    {
        var (privateKey, _) = LicenseTestKit.NewKeyPair();

        var issued = LicenseIssuer.Issue(Bound(), privateKey);

        var check = LicenseImportRules.Check(
            issued.Document,
            signatureValid: true,
            localMachineCode: MachineCode,
            nowUtc: Now,
            binding: null,
            clockTolerance: LicenseEvaluator.DefaultClockTolerance);

        check.Ok.Should().BeTrue(check.Message);
    }

    [Fact]
    public void Issued_universal_license_passes_the_import_rules_on_first_import()
    {
        var (privateKey, _) = LicenseTestKit.NewKeyPair();

        var issued = LicenseIssuer.Issue(Universal(), privateKey);

        var check = LicenseImportRules.Check(
            issued.Document,
            signatureValid: true,
            localMachineCode: OtherMachineCode,
            nowUtc: Now,
            binding: null,
            clockTolerance: LicenseEvaluator.DefaultClockTolerance);

        check.Ok.Should().BeTrue(check.Message);
        check.Binding!.MachineCode.Should().Be(OtherMachineCode);
    }

    // ═══════════════════════════════════════════════════════════
    // 拒绝路径
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("permanent", "永久")]
    [InlineData("no-deadline", "deadline")]
    public void Universal_license_that_breaks_the_rules_is_rejected(string scenario, string expectedFragment)
    {
        var request = scenario switch
        {
            "permanent" => Universal() with { ExpiryDate = string.Empty },
            _ => Universal() with { UseDeadlineUtc = null },
        };

        AssertRejected(request, expectedFragment);
    }

    [Fact]
    public void Universal_and_bound_are_mutually_exclusive()
    {
        AssertRejected(Universal() with { MachineCode = MachineCode }, "不能同时使用");
    }

    [Fact]
    public void Deadline_is_rejected_for_a_bound_license()
    {
        AssertRejected(Bound() with { UseDeadlineUtc = Now.AddDays(10) }, "只对通用授权有意义");
    }

    [Fact]
    public void Trial_without_any_expiry_is_rejected()
    {
        AssertRejected(
            Bound() with { Kind = LicenseKind.Trial, ExpiryDate = string.Empty },
            "试用授权必须指定有效期");
    }

    [Fact]
    public void Two_expiry_forms_at_once_are_rejected()
    {
        AssertRejected(Bound() with { ExpiryDate = "20271008", MaxDays = 30 }, "只能指定一种形式");
    }

    [Fact]
    public void Unknown_kind_is_rejected()
    {
        AssertRejected(Bound() with { Kind = "forever" }, "无法识别");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AAAA-BBBB-CCCC")]          // 位数不足
    [InlineData("AAAA-BBBB-CCCC-DDDD-EEEE")] // 位数过多
    [InlineData("ZZZZ-BBBB-CCCC-DDDD")]     // 非十六进制
    public void Malformed_machine_code_is_rejected(string machineCode)
    {
        AssertRejected(Bound(machineCode: machineCode), "机器码");
    }

    [Fact]
    public void Missing_network_anchor_is_rejected()
    {
        AssertRejected(Bound() with { AnchorUtc = null }, "联网校对时间");
    }

    [Fact]
    public void Anchor_in_the_future_is_rejected()
    {
        // 锚点晚于本机当前时间 ⇒ 客户机器会被判成"时间被回拨"而拒绝导入。
        AssertRejected(
            Bound() with { AnchorUtc = Now.AddHours(1) },
            "晚于本机当前时间");
    }

    [Fact]
    public void Expiry_in_the_past_is_rejected()
    {
        AssertRejected(
            Bound() with { ExpiryDate = "20250101" },
            "早于签发时刻");
    }

    [Fact]
    public void Unparsable_expiry_date_is_rejected()
    {
        AssertRejected(Bound() with { ExpiryDate = "2027-10-08" }, "无法解析");
    }

    [Fact]
    public void Negative_max_days_is_rejected()
    {
        AssertRejected(Bound() with { MaxDays = -1 }, "不能为负数");
    }

    [Fact]
    public void Deadline_after_expiry_is_rejected_for_a_universal_license()
    {
        AssertRejected(
            Universal() with { UseDeadlineUtc = Now.AddDays(400) },
            "必须早于授权到期时间");
    }

    [Fact]
    public void Deadline_in_the_past_is_rejected_for_a_universal_license()
    {
        AssertRejected(
            Universal() with { UseDeadlineUtc = Now.AddDays(-1) },
            "不晚于签发时刻");
    }

    [Fact]
    public void Issue_fails_loudly_when_the_private_key_is_unusable()
    {
        Action act = () => LicenseIssuer.Issue(Bound(), "not a pem");

        act.Should().Throw<LicenseValidationException>().WithMessage("*私钥加载失败*");
    }

    // ═══════════════════════════════════════════════════════════
    // 自检本身
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 签发自检必须使用<b>从本次私钥现推的公钥</b>。这条用例钉死"现推"这件事：
    /// 若哪天改回"让调用方传公钥文件"，用另一把钥匙的公钥就会自检失败，
    /// 而那种失败在现场会被误读成"签发坏了"。
    /// </summary>
    [Fact]
    public void Self_check_uses_the_public_key_derived_from_the_signing_private_key()
    {
        var (privateKey, publicKey) = LicenseTestKit.NewKeyPair();
        var (otherPrivateKey, otherPublicKey) = LicenseTestKit.NewKeyPair();

        var result = LicenseSigner.SignWithSelfCheck(LicenseTestKit.NewDocument(), privateKey);

        result.PublicKeyPem.Should().Be(publicKey);
        result.PublicKeyPem.Should().NotBe(otherPublicKey);

        // 用"别的钥匙"的公钥验肯定不过 —— 证明自检不是走过场。
        LicenseSigner.VerifyRoundTrip(result.Encoded, otherPublicKey).Should().BeFalse();
        LicenseSigner.VerifyRoundTrip(result.Encoded, publicKey).Should().BeTrue();

        // 另一把私钥签出来的东西，公钥指纹必须不同。
        var other = LicenseSigner.SignWithSelfCheck(LicenseTestKit.NewDocument(), otherPrivateKey);
        other.Fingerprint.Should().NotBe(result.Fingerprint);
    }

    [Fact]
    public void Fingerprint_matches_the_verifier_side_computation()
    {
        var (privateKey, publicKey) = LicenseTestKit.NewKeyPair();

        var result = LicenseSigner.SignWithSelfCheck(LicenseTestKit.NewDocument(), privateKey);

        using var verifier = new EcdsaLicenseVerifier(publicKey);
        result.Fingerprint.Should().Be(verifier.PublicKeyFingerprint);
    }

    [Theory]
    [InlineData("A1B2-C3D4-E5F6-0718", "A1B2-C3D4-E5F6-0718")]
    [InlineData("a1b2c3d4e5f60718", "A1B2-C3D4-E5F6-0718")]
    public void TryNormalizeMachineCode_accepts_both_written_forms(string input, string expected)
    {
        LicenseIssuer.TryNormalizeMachineCode(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A1B2-C3D4-E5F6-071")]     // 15 位
    [InlineData("A1B2-C3D4-E5F6-0718-A")] // 17 位
    [InlineData("G1B2-C3D4-E5F6-0718")]   // 非十六进制
    public void TryNormalizeMachineCode_rejects_garbage(string? input)
    {
        LicenseIssuer.TryNormalizeMachineCode(input, out var normalized).Should().BeFalse();
        normalized.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════
    // 夹具
    // ═══════════════════════════════════════════════════════════

    /// <summary>绑定式正式授权：绑定机器码、到期 2027-10-08、锚点 = 签发前一小时。</summary>
    private static LicenseIssuanceRequest Bound(string machineCode = MachineCode) => new()
    {
        Kind = LicenseKind.Full,
        MachineCode = machineCode,
        IssuedAtUtc = Now,
        AnchorUtc = Now.AddHours(-1),
        ExpiryDate = "20271008",
        Serial = "SN-0001",
        Vendor = "VENDOR-0001",
        Customer = "测试展厅",
    };

    /// <summary>通用授权：到期 2027-10-08、使用截止 = 签发后 30 天。</summary>
    private static LicenseIssuanceRequest Universal() => new()
    {
        Kind = LicenseKind.Full,
        Universal = true,
        IssuedAtUtc = Now,
        AnchorUtc = Now.AddHours(-1),
        ExpiryDate = "20271008",
        UseDeadlineUtc = Now.AddDays(30),
        Serial = "SN-U-0001",
        Vendor = "VENDOR-0001",
        Customer = "测试展厅",
    };

    private static void AssertRejected(LicenseIssuanceRequest request, string expectedFragment)
    {
        var (privateKey, _) = LicenseTestKit.NewKeyPair();

        Action act = () => LicenseIssuer.Issue(request, privateKey);

        act.Should().Throw<LicenseValidationException>()
            .WithMessage($"*{expectedFragment}*");
    }

    // ═══════════════════════════════════════════════════════════
    // 签发公钥 × 服务端内嵌公钥
    //
    // 这一组守的是"签发工具谎报自检"那个事故：用非厂商私钥签出来的授权，
    // 两道自检（密码学自洽、导入规则干跑）都会报"通过"，只有服务端内嵌公钥
    // 才认得出它。签发工具靠 MatchesVendorPublicKey 这一个信号去警告用户，
    // 它一旦失真，用户就会把必然被拒的文件发给客户。
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void License_signed_with_a_non_vendor_key_is_flagged_as_not_matching_the_embedded_key()
    {
        var (privateKey, _) = LicenseTestKit.NewKeyPair();

        var result = LicenseIssuer.Issue(Bound(), privateKey);

        // 测试环境按设计不存在厂商私钥 ⇒ 这里签出来的一定是"非厂商"的。
        // 为 true 就意味着比对逻辑坏了（而那会让警告彻底失效）。
        result.MatchesVendorPublicKey.Should().BeFalse();
        result.PublicKeyFingerprint.Should().NotBe(VendorPublicKey.Fingerprint);
    }

    [Fact]
    public void DescribeKeyPair_reports_the_same_fingerprint_as_the_key_pair_generator()
    {
        var (privateKey, publicKey, fingerprint) = LicenseSigner.GenerateKeyPair();

        var (describedPublicKey, describedFingerprint) = LicenseSigner.DescribeKeyPair(privateKey);

        describedFingerprint.Should().Be(fingerprint);
        describedPublicKey.Should().Be(publicKey);
    }

    [Fact]
    public void DescribeKeyPair_rejects_a_malformed_private_key()
    {
        // 界面上"选定私钥立即预检"依赖它给出人话异常，而不是抛个 NullReference 出来。
        Action act = () => LicenseSigner.DescribeKeyPair("这不是一份 PEM");

        act.Should().Throw<LicenseValidationException>();
    }
}
