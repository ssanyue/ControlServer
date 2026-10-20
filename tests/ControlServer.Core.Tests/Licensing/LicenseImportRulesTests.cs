using ControlServer.Core.Licensing;
using FluentAssertions;

namespace ControlServer.Core.Tests.Licensing;

/// <summary>
/// 导入规则：只在"收下这份新文件"的那一刻跑，与"现在还能不能用"的评估是两件事。
/// </summary>
public sealed class LicenseImportRulesTests
{
    private static readonly DateTimeOffset Now = LicenseTestKit.Now;
    private static readonly TimeSpan Tolerance = LicenseEvaluator.DefaultClockTolerance;

    private static LicenseImportResult Check(
        LicenseDocument document,
        bool signatureValid = true,
        string machineCode = LicenseTestKit.MachineCode,
        DateTimeOffset? nowUtc = null,
        LicenseBinding? binding = null)
        => LicenseImportRules.Check(
            document, signatureValid, machineCode, nowUtc ?? Now, binding, Tolerance);

    // ══════════════════════════════════════════════════════════
    // 成功路径
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Machine_bound_license_imports_successfully()
    {
        var result = Check(LicenseTestKit.NewDocument(expiresOn: "20271008"));

        result.Ok.Should().BeTrue(result.Message);
        result.Status.Should().Be(LicenseImportStatus.Ok);
        result.State!.IsUsable.Should().BeTrue();
        result.Binding.Should().BeNull("绑定式授权不需要侧车记录");
    }

    [Fact]
    public void Universal_license_binds_to_this_machine_on_first_import()
    {
        var document = LicenseTestKit.NewDocument(
            machineCode: null,
            universal: 1,
            importDeadlineUtcMs: Now.AddDays(30).ToUnixTimeMilliseconds());

        var result = Check(document);

        result.Ok.Should().BeTrue(result.Message);
        result.Binding.Should().NotBeNull();
        result.Binding!.MachineCode.Should().Be(LicenseTestKit.MachineCode);
        result.Binding.BoundAtUtcMs.Should().Be(Now.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Re_importing_the_same_universal_license_keeps_the_original_binding()
    {
        var originalBinding = new LicenseBinding(
            LicenseTestKit.MachineCode, Now.AddDays(-10).ToUnixTimeMilliseconds());

        var result = Check(
            LicenseTestKit.NewDocument(machineCode: null, universal: 1),
            binding: originalBinding);

        result.Ok.Should().BeTrue(result.Message);
        result.Binding.Should().BeSameAs(originalBinding, "重新导入不该刷新绑定时刻");
    }

    [Fact]
    public void Import_success_always_implies_usable_state()
    {
        // 导入成功 与 状态可用 必须永远一致：这里另写一套判断就会分裂出
        // "导入说成功、状态说不可用"的状态。用一个最小的成功样例把不变式钉住。
        var result = Check(LicenseTestKit.NewDocument(expiresOn: null));

        result.Ok.Should().BeTrue();
        result.State.Should().NotBeNull();
        result.State!.IsUsable.Should().BeTrue();
    }

    // ══════════════════════════════════════════════════════════
    // 拒绝路径
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Invalid_signature_is_rejected()
    {
        var result = Check(LicenseTestKit.NewDocument(), signatureValid: false);

        result.Status.Should().Be(LicenseImportStatus.SignatureInvalid);
        result.Ok.Should().BeFalse();
        result.State.Should().BeNull();
    }

    [Fact]
    public void Unsupported_version_is_rejected()
    {
        var document = LicenseTestKit.NewDocument();
        document.V = 2;

        Check(document).Status.Should().Be(LicenseImportStatus.UnsupportedVersion);
    }

    [Fact]
    public void Unknown_kind_is_rejected()
    {
        Check(LicenseTestKit.NewDocument(kind: "vip")).Status
            .Should().Be(LicenseImportStatus.UnknownKind);
    }

    [Fact]
    public void Missing_network_anchor_is_rejected_on_import_too()
    {
        // 导入路径上不卡这一条，就等于"手工拼一份无 ivt 的文件即可绕过在线校对"。
        var document = LicenseTestKit.NewDocument();
        document.Ivt = 0;

        var result = Check(document);

        result.Status.Should().Be(LicenseImportStatus.MissingNetworkAnchor);
        result.Message.Should().Contain("篡改");
    }

    [Fact]
    public void Clock_behind_anchor_is_rejected()
    {
        var document = LicenseTestKit.NewDocument();
        document.Ivt = Now.AddHours(1).ToUnixTimeMilliseconds();

        Check(document).Status.Should().Be(LicenseImportStatus.ClockUnusable);
    }

    [Fact]
    public void Universal_license_past_its_import_deadline_is_rejected()
    {
        var document = LicenseTestKit.NewDocument(
            machineCode: null,
            universal: 1,
            importDeadlineUtcMs: Now.AddDays(-1).ToUnixTimeMilliseconds());

        var result = Check(document);

        result.Status.Should().Be(LicenseImportStatus.DeadlineExceeded);
        result.Message.Should().Contain("截止");
    }

    [Fact]
    public void Universal_license_already_bound_elsewhere_is_rejected()
    {
        var binding = new LicenseBinding(LicenseTestKit.OtherMachineCode, Now.ToUnixTimeMilliseconds());

        var result = Check(LicenseTestKit.NewDocument(machineCode: null, universal: 1), binding: binding);

        result.Status.Should().Be(LicenseImportStatus.MachineMismatch);
        result.Message.Should().Contain(LicenseTestKit.OtherMachineCode);
    }

    [Fact]
    public void License_bound_to_another_machine_is_rejected()
    {
        var result = Check(LicenseTestKit.NewDocument(machineCode: LicenseTestKit.OtherMachineCode));

        result.Status.Should().Be(LicenseImportStatus.MachineMismatch);
    }

    [Fact]
    public void Bound_license_without_machine_code_is_rejected()
    {
        Check(LicenseTestKit.NewDocument(machineCode: null, universal: 0)).Status
            .Should().Be(LicenseImportStatus.MachineMismatch);
    }

    [Fact]
    public void Already_expired_license_is_rejected()
    {
        var result = Check(LicenseTestKit.NewDocument(expiresOn: "20260101"));

        result.Status.Should().Be(LicenseImportStatus.Expired);
        result.Message.Should().Contain("过期");
    }

    [Fact]
    public void Expiry_date_of_today_is_still_importable()
    {
        Check(LicenseTestKit.NewDocument(expiresOn: "20261008")).Ok
            .Should().BeTrue("到期日当天（北京）应当还能导入 —— 客户往往就是最后一天才来操作");
    }

    [Theory]
    [InlineData(LicenseImportStatus.Malformed)]
    [InlineData(LicenseImportStatus.SignatureInvalid)]
    [InlineData(LicenseImportStatus.UnsupportedVersion)]
    [InlineData(LicenseImportStatus.UnknownKind)]
    [InlineData(LicenseImportStatus.MachineMismatch)]
    [InlineData(LicenseImportStatus.Expired)]
    [InlineData(LicenseImportStatus.DeadlineExceeded)]
    [InlineData(LicenseImportStatus.MissingNetworkAnchor)]
    [InlineData(LicenseImportStatus.ClockUnusable)]
    public void Only_ok_counts_as_success(LicenseImportStatus status)
    {
        // 守门：Ok 的判定必须只认 Ok 自身。参照实现用了 `ri >= 1` 的范围比较，
        // 把"已过期""异常"也当成功，导致导入过期授权后门控直接关闭。
        var result = new LicenseImportResult(status, "x", null, null, null);

        result.Ok.Should().BeFalse();
    }

    [Fact]
    public void Only_ok_status_is_considered_success()
    {
        new LicenseImportResult(LicenseImportStatus.Ok, "x", null, null, null).Ok.Should().BeTrue();
    }

    // ══════════════════════════════════════════════════════════
    // 端到端：签发 → 验签 → 导入
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Signed_license_survives_the_whole_pipeline()
    {
        var (privateKey, publicKey) = LicenseTestKit.NewKeyPair();
        var source = LicenseTestKit.NewDocument(expiresOn: "20271008");

        var text = LicenseSigner.Sign(source, privateKey);

        // 完全按服务端那条路径走一遍：解析 → 重算载荷 → 验签 → 导入规则。
        LicenseCodec.TryDecode(text, out var decoded, out var parseError).Should().BeTrue(parseError);

        using var verifier = new EcdsaLicenseVerifier(publicKey);
        var signatureValid = verifier.Verify(LicenseCodec.BuildPayload(decoded!), decoded!.Sig);

        signatureValid.Should().BeTrue();

        var result = LicenseImportRules.Check(
            decoded!, signatureValid, LicenseTestKit.MachineCode, Now, null, Tolerance);

        result.Ok.Should().BeTrue(result.Message);
        result.State!.Status.Should().Be(LicenseStatus.Authorized);
    }

    [Fact]
    public void Tampered_license_is_stopped_by_signature_check_before_any_field_rule()
    {
        var (privateKey, publicKey) = LicenseTestKit.NewKeyPair();
        var text = LicenseSigner.Sign(LicenseTestKit.NewDocument(expiresOn: "20261010"), privateKey);

        // 把到期日改到 2099 年 —— 字段层面完全合法，只有签名能抓住它。
        var tampered = LicenseCodec.Decode(text);
        tampered.Ed = "20991231";

        using var verifier = new EcdsaLicenseVerifier(publicKey);
        var signatureValid = verifier.Verify(LicenseCodec.BuildPayload(tampered), tampered.Sig);

        signatureValid.Should().BeFalse();

        var result = LicenseImportRules.Check(
            tampered, signatureValid, LicenseTestKit.MachineCode, Now, null, Tolerance);

        result.Status.Should().Be(LicenseImportStatus.SignatureInvalid);
    }
}
