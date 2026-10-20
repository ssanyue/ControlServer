using ControlServer.Core.Licensing;
using FluentAssertions;

namespace ControlServer.Core.Tests.Licensing;

/// <summary>
/// 授权评估器：纯函数，覆盖全部分支。
/// <para>
/// 时钟由参数注入，所以"试用最后一天""时钟被回调 3 分钟"这类边界可以精确构造，
/// 不需要等真实时间走到那一刻（与 <c>TimerSchedulerServiceTests</c> 注入假时钟同理）。
/// </para>
/// </summary>
public sealed class LicenseEvaluatorTests
{
    private static readonly DateTimeOffset Now = LicenseTestKit.Now;

    // ══════════════════════════════════════════════════════════
    // 无文件 / 不可信输入
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Missing_document_is_not_authorized()
    {
        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(null));

        state.Status.Should().Be(LicenseStatus.NotAuthorized);
        state.IsUsable.Should().BeFalse();
        state.LastError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Invalid_signature_is_error_even_when_every_field_looks_perfect()
    {
        var state = LicenseEvaluator.Evaluate(
            LicenseTestKit.Input(LicenseTestKit.NewDocument(expiresOn: null), signatureValid: false,
                signatureError: "签名不匹配"));

        state.Status.Should().Be(LicenseStatus.Error);
        state.IsUsable.Should().BeFalse();
        state.LastError.Should().Contain("签名不匹配");
    }

    [Fact]
    public void Unsupported_version_is_error()
    {
        var document = LicenseTestKit.NewDocument();
        document.V = 99;

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(document));

        state.Status.Should().Be(LicenseStatus.Error);
        state.LastError.Should().Contain("99");
    }

    [Fact]
    public void Unknown_kind_is_error()
    {
        var document = LicenseTestKit.NewDocument(kind: "platinum");

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(document));

        state.Status.Should().Be(LicenseStatus.Error);
        state.LastError.Should().Contain("platinum");
    }

    // ══════════════════════════════════════════════════════════
    // 机器码绑定
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Machine_code_mismatch_is_error_and_names_both_codes()
    {
        var document = LicenseTestKit.NewDocument(machineCode: LicenseTestKit.OtherMachineCode);

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(document));

        state.Status.Should().Be(LicenseStatus.Error);
        state.LastError.Should().Contain(LicenseTestKit.OtherMachineCode);
        state.LastError.Should().Contain(LicenseTestKit.MachineCode);
    }

    [Fact]
    public void Machine_code_comparison_ignores_case_and_padding()
    {
        var document = LicenseTestKit.NewDocument(machineCode: "  aaaa-bbbb-cccc-dddd  ");

        LicenseEvaluator.Evaluate(LicenseTestKit.Input(document)).IsUsable
            .Should().BeTrue("机器码是十六进制串，大小写与空白不该造成误判");
    }

    [Fact]
    public void Bound_license_without_machine_code_is_error()
    {
        var document = LicenseTestKit.NewDocument(machineCode: null, universal: 0);

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(document));

        state.Status.Should().Be(LicenseStatus.Error);
        state.LastError.Should().Contain("未绑定");
    }

    [Fact]
    public void Universal_license_without_binding_is_error_with_actionable_message()
    {
        var document = LicenseTestKit.NewDocument(machineCode: null, universal: 1);

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(document));

        state.Status.Should().Be(LicenseStatus.Error);
        state.LastError.Should().Contain("通用授权");
        state.LastError.Should().Contain("导入");
    }

    [Fact]
    public void Universal_license_uses_machine_code_from_the_binding_sidecar()
    {
        var document = LicenseTestKit.NewDocument(machineCode: null, universal: 1);
        var binding = new LicenseBinding(LicenseTestKit.MachineCode, Now.ToUnixTimeMilliseconds());

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(document, binding: binding));

        state.IsUsable.Should().BeTrue();
    }

    [Fact]
    public void Universal_license_bound_to_another_machine_is_error()
    {
        var document = LicenseTestKit.NewDocument(machineCode: null, universal: 1);
        var binding = new LicenseBinding(LicenseTestKit.OtherMachineCode, Now.ToUnixTimeMilliseconds());

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(document, binding: binding));

        state.Status.Should().Be(LicenseStatus.Error);
        state.LastError.Should().Contain(LicenseTestKit.OtherMachineCode);
    }

    // ══════════════════════════════════════════════════════════
    // 联网锚点 ivt
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Missing_network_anchor_is_error()
    {
        var document = LicenseTestKit.NewDocument();
        document.Ivt = 0;

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(document));

        state.Status.Should().Be(LicenseStatus.Error);
        state.LastError.Should().Contain("联网校对");
    }

    [Fact]
    public void Negative_network_anchor_is_error()
    {
        var document = LicenseTestKit.NewDocument();
        document.Ivt = -1;

        LicenseEvaluator.Evaluate(LicenseTestKit.Input(document)).Status.Should().Be(LicenseStatus.Error);
    }

    [Fact]
    public void Clock_rolled_back_beyond_tolerance_is_error()
    {
        var document = LicenseTestKit.NewDocument();
        document.Ivt = Now.AddMinutes(30).ToUnixTimeMilliseconds();

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(document));

        state.Status.Should().Be(LicenseStatus.Error);
        state.LastError.Should().Contain("早于");
    }

    [Fact]
    public void Clock_slightly_behind_anchor_is_tolerated()
    {
        // 锚点比"现在"晚 1 分钟，容差 2 分钟 ⇒ 属于正常时钟抖动，不该拦。
        var document = LicenseTestKit.NewDocument();
        document.Ivt = Now.AddMinutes(1).ToUnixTimeMilliseconds();

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(document));

        state.IsUsable.Should().BeTrue();
    }

    [Fact]
    public void Anchor_exactly_at_tolerance_boundary_is_accepted()
    {
        var document = LicenseTestKit.NewDocument();
        document.Ivt = Now.Add(LicenseEvaluator.DefaultClockTolerance).ToUnixTimeMilliseconds();

        LicenseEvaluator.Evaluate(LicenseTestKit.Input(document)).IsUsable
            .Should().BeTrue("判定用的是 now + 容差 >= ivt，等于边界应当放行");
    }

    // ══════════════════════════════════════════════════════════
    // 有效期
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Perpetual_license_is_authorized_with_sentinel_remaining_days()
    {
        var document = LicenseTestKit.NewDocument(expiresOn: null);

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(document));

        state.Status.Should().Be(LicenseStatus.Authorized);
        state.Kind.Should().Be(LicenseKind.Full);
        state.RemainingDays.Should().Be(LicenseState.PermanentRemainingDays);
        state.IsPerpetual.Should().BeTrue();
        state.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public void Dated_license_reports_remaining_days_rounded_up()
    {
        // 到期日 2026-10-10（北京）→ 到期时刻 2026-10-10 15:59:59 UTC；
        // 现在 2026-10-08 12:00 UTC ⇒ 还剩 2.17 天 ⇒ 应显示 3 天（向上取整）。
        var document = LicenseTestKit.NewDocument(expiresOn: "20261010");

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(document));

        state.Status.Should().Be(LicenseStatus.Authorized);
        state.RemainingDays.Should().Be(3);
        state.ExpiresAt.Should().NotBeNull();
    }

    [Fact]
    public void Expiry_day_itself_is_still_valid_until_beijing_midnight()
    {
        // 到期日 2026-10-08（北京）。北京 20:00（= 12:00 UTC）当天显然还该能用。
        var document = LicenseTestKit.NewDocument(expiresOn: "20261008");

        LicenseEvaluator.Evaluate(LicenseTestKit.Input(document, nowUtc: Now)).IsUsable
            .Should().BeTrue();
    }

    [Fact]
    public void License_expires_right_after_beijing_midnight_of_the_expiry_day()
    {
        var document = LicenseTestKit.NewDocument(expiresOn: "20261008");

        // 2026-10-09 00:00（北京）= 2026-10-08 16:00 UTC，刚过到期时刻 1 秒。
        var justAfter = new DateTimeOffset(2026, 10, 8, 16, 0, 1, TimeSpan.Zero);

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(document, nowUtc: justAfter));

        state.Status.Should().Be(LicenseStatus.Expired);
        state.IsUsable.Should().BeFalse();
    }

    [Fact]
    public void Expired_license_reports_zero_remaining_days()
    {
        var document = LicenseTestKit.NewDocument(expiresOn: "20261007");

        var state = LicenseEvaluator.Evaluate(
            LicenseTestKit.Input(document, nowUtc: new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)));

        state.Status.Should().Be(LicenseStatus.Expired);
        state.RemainingDays.Should().Be(0);
        state.LastError.Should().Contain("过期");
    }

    [Fact]
    public void Exact_expiry_takes_priority_over_expiry_date()
    {
        // ed 写的是 2099 年（看起来很安全），但 et 已经过期 —— et 必须赢。
        var document = LicenseTestKit.NewDocument(
            expiresOn: "20991231",
            exactExpiryUtcMs: Now.AddHours(-1).ToUnixTimeMilliseconds());

        LicenseEvaluator.Evaluate(LicenseTestKit.Input(document)).Status
            .Should().Be(LicenseStatus.Expired);
    }

    [Fact]
    public void Max_days_fallback_uses_binding_time_as_start()
    {
        var document = LicenseTestKit.NewDocument(
            expiresOn: null,
            maxDays: 40,
            issuedAtUtcMs: Now.AddDays(-30).ToUnixTimeMilliseconds());

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(document));

        state.Status.Should().Be(LicenseStatus.Authorized);
        state.RemainingDays.Should().Be(10);
    }

    [Fact]
    public void Max_days_fallback_uses_issue_date_when_binding_time_is_absent()
    {
        // 签发日 2026-10-08（北京 00:00）+ 5 天 = 2026-10-13 00:00（北京）
        // = 2026-10-12 16:00 UTC；现在 2026-10-08 12:00 UTC ⇒ 还剩 4.17 天 ⇒ 5 天。
        var document = LicenseTestKit.NewDocument(expiresOn: null, maxDays: 5, issuedAtUtcMs: 0);

        LicenseEvaluator.Evaluate(LicenseTestKit.Input(document)).RemainingDays.Should().Be(5);
    }

    [Fact]
    public void License_without_any_expiry_information_is_perpetual()
    {
        var document = LicenseTestKit.NewDocument(expiresOn: null, maxDays: 0);

        LicenseEvaluator.Evaluate(LicenseTestKit.Input(document)).IsPerpetual.Should().BeTrue();
    }

    // ══════════════════════════════════════════════════════════
    // 试用
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Running_trial_reports_trial_status()
    {
        var document = LicenseTestKit.NewDocument(kind: LicenseKind.Trial, expiresOn: "20261107");

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(document));

        state.Status.Should().Be(LicenseStatus.Trial);
        state.IsUsable.Should().BeTrue();
        state.Kind.Should().Be(LicenseKind.Trial);
    }

    [Fact]
    public void Expired_trial_reports_expired()
    {
        var document = LicenseTestKit.NewDocument(kind: LicenseKind.Trial, expiresOn: "20261001");

        LicenseEvaluator.Evaluate(LicenseTestKit.Input(document)).Status.Should().Be(LicenseStatus.Expired);
    }

    [Fact]
    public void Trial_without_expiry_is_error_not_perpetual()
    {
        // 这是本评估器里最值得单独钉住的一条：缺期限的"试用"若被当成永久授权放行，
        // 就等于"签发工具一有 bug，所有客户白拿永久授权"。
        var document = LicenseTestKit.NewDocument(kind: LicenseKind.Trial, expiresOn: null, maxDays: 0);

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(document));

        state.Status.Should().Be(LicenseStatus.Error);
        state.IsUsable.Should().BeFalse();
        state.LastError.Should().Contain("有效期");
    }

    [Fact]
    public void Temp_kind_behaves_like_a_dated_full_license()
    {
        var document = LicenseTestKit.NewDocument(kind: LicenseKind.Temp, expiresOn: "20261020");

        var state = LicenseEvaluator.Evaluate(LicenseTestKit.Input(document));

        state.Status.Should().Be(LicenseStatus.Authorized);
        state.Kind.Should().Be(LicenseKind.Temp);
    }

    // ══════════════════════════════════════════════════════════
    // 守门：白名单判定
    // ══════════════════════════════════════════════════════════

    [Theory]
    [InlineData(LicenseStatus.NotAuthorized, "未授权")]
    [InlineData(LicenseStatus.Expired, "已过期")]
    [InlineData(LicenseStatus.Error, "错误")]
    public void Non_usable_statuses_are_never_reported_as_usable(LicenseStatus status, string label)
    {
        // 参照实现曾在导入处写 `ri >= 1`，把"已过期(2)/错误(3)"也当成功 ——
        // 症状是导入过期授权后功能照常可用且无任何提示。这条用例专门把该语义钉死。
        var state = new LicenseState(status, LicenseKind.Full, 0, null, label, label);

        state.IsUsable.Should().BeFalse($"{status} 不在白名单里");
    }

    [Theory]
    [InlineData(LicenseStatus.Authorized)]
    [InlineData(LicenseStatus.Trial)]
    public void Usable_statuses_are_reported_as_usable(LicenseStatus status)
    {
        new LicenseState(status, LicenseKind.Full, 1, null, "ok", string.Empty)
            .IsUsable.Should().BeTrue();
    }

    // ══════════════════════════════════════════════════════════
    // 辅助函数
    // ══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("20261008", true)]
    [InlineData("2026-10-08", false)]
    [InlineData("", false)]
    [InlineData("20261301", false)]   // 13 月
    [InlineData("20260230", false)]   // 2 月 30 日
    public void Beijing_date_parser_is_strict(string text, bool expected)
    {
        LicenseEvaluator.TryParseBeijingDate(text, out _).Should().Be(expected);
    }

    [Fact]
    public void Utc_millisecond_formatter_returns_blank_for_zero()
    {
        LicenseEvaluator.FormatUtcMs(0).Should().BeEmpty();
        LicenseEvaluator.FormatUtcMs(Now.ToUnixTimeMilliseconds()).Should().NotBeNullOrWhiteSpace();
    }
}
