using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 密码策略（M3）的边界用例。
/// <para>
/// 这里的重点不是"合法密码能过"，而是<b>每一条拒绝规则都在边界上被钉住</b>：
/// 少一位、多一位、等于用户名、等于随包发布的默认口令。
/// 密码策略的失效方式几乎都是"差一位就放过去了"，只有边界用例能发现。
/// </para>
/// </summary>
public sealed class PasswordPolicyTests
{
    [Fact]
    public void Accepts_an_ordinary_password()
        => PasswordPolicy.Validate("Sup3r-Secret", "alice").Should().BeNull();

    [Fact]
    public void Rejects_null_and_empty()
    {
        PasswordPolicy.Validate(null, "alice").Should().NotBeNull();
        PasswordPolicy.Validate(string.Empty, "alice").Should().NotBeNull();
    }

    [Fact]
    public void Rejects_one_character_below_the_minimum()
    {
        var tooShort = new string('a', PasswordPolicy.MinLength - 1);

        PasswordPolicy.Validate(tooShort, "alice").Should().Contain(PasswordPolicy.MinLength.ToString());
    }

    [Fact]
    public void Accepts_exactly_the_minimum_length()
    {
        // 恰好 MinLength，且不等于用户名、不等于默认口令。
        var justRight = "a1!" + new string('x', PasswordPolicy.MinLength - 3);

        justRight.Length.Should().Be(PasswordPolicy.MinLength);
        PasswordPolicy.Validate(justRight, "alice").Should().BeNull();
    }

    [Fact]
    public void Accepts_exactly_the_maximum_length()
    {
        var atLimit = new string('x', PasswordPolicy.MaxLength);

        PasswordPolicy.Validate(atLimit, "alice").Should().BeNull();
    }

    [Fact]
    public void Rejects_one_character_above_the_maximum()
    {
        var tooLong = new string('x', PasswordPolicy.MaxLength + 1);

        PasswordPolicy.Validate(tooLong, "alice").Should().Contain(PasswordPolicy.MaxLength.ToString());
    }

    [Fact]
    public void Rejects_the_shipped_default_password()
        => PasswordPolicy.Validate(PasswordPolicy.ShippedDefaultPassword, "alice").Should().NotBeNull();

    [Fact]
    public void Rejects_a_password_equal_to_the_user_name_ignoring_case()
    {
        PasswordPolicy.Validate("Alice", "alice").Should().NotBeNull();
        PasswordPolicy.Validate("ALICE", "alice").Should().NotBeNull();

        // 只挡"完全相同"，不做子串判断 —— 否则 "alice2026!" 这种正常口令会被误杀。
        PasswordPolicy.Validate("alice2026!", "alice").Should().BeNull();
    }

    [Fact]
    public void Skips_the_user_name_rule_when_no_user_name_is_given()
    {
        PasswordPolicy.Validate("alicealice").Should().BeNull();
        PasswordPolicy.Validate("alicealice", null).Should().BeNull();
        PasswordPolicy.Validate("alicealice", "   ").Should().BeNull();
    }

    [Fact]
    public void Trim_is_applied_when_comparing_with_the_user_name()
    {
        // 用户名入库前会 Trim，比较也必须用 Trim 之后的值，否则「 admin 」这种带空格的用户名会绕过规则。
        PasswordPolicy.Validate("alice", "  alice  ").Should().NotBeNull();
    }

    [Theory]
    [InlineData("Sup3r-Secret", true)]
    [InlineData("short", false)]
    [InlineData("admin123", false)]
    public void IsValid_matches_Validate(string password, bool expected)
        => PasswordPolicy.IsValid(password, "alice").Should().Be(expected);
}
