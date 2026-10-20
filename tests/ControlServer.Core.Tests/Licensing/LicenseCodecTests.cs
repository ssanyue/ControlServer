using ControlServer.Core.Licensing;
using FluentAssertions;

namespace ControlServer.Core.Tests.Licensing;

/// <summary>
/// 授权载荷编解码。
/// <para>
/// 这一层是"自签自验"事故的高发区：签约侧与验签侧只要对载荷字节的认知有一丝差别，
/// 症状就是"自己签的自己验不过"，而且签发那一刻看不出来。
/// 因此下面的用例重点守在<b>确定性</b>上，而不只是"能往返"。
/// </para>
/// </summary>
public sealed class LicenseCodecTests
{
    [Fact]
    public void Payload_is_deterministic_regardless_of_assignment_order()
    {
        var first = LicenseTestKit.NewDocument();

        // 用完全不同的赋值顺序构造出内容相同的文档 —— 属性初始化顺序不该影响载荷。
        var second = new LicenseDocument
        {
            Hn = "测试展厅",
            Ivt = first.Ivt,
            Ed = first.Ed,
            Lt = first.Lt,
            Mc = first.Mc,
            V = first.V,
            Ub = first.Ub,
            Ud = first.Ud,
            Bt = first.Bt,
            Et = first.Et,
            Md = first.Md,
            Sn = first.Sn,
            Vi = first.Vi,
            Id = first.Id,
        };

        LicenseCodec.BuildPayload(second).Should().Be(LicenseCodec.BuildPayload(first));
    }

    [Fact]
    public void Payload_never_contains_the_signature_field()
    {
        var document = LicenseTestKit.NewDocument();
        document.Sig = "SHOULD-NOT-APPEAR";

        LicenseCodec.BuildPayload(document).Should().NotContain("sig");
        LicenseCodec.BuildPayload(document).Should().NotContain("SHOULD-NOT-APPEAR");
    }

    [Fact]
    public void Payload_field_order_is_fixed()
    {
        var payload = LicenseCodec.BuildPayload(LicenseTestKit.NewDocument());

        // 顺序即契约：一旦有人调整了 BuildPayload 里的字段顺序，所有已签发授权都会失效。
        // 这条用例就是那个改动的哨兵 —— 它失败时，先问"这是有意的版本升级吗"。
        var expectedOrder = new[]
        {
            "\"v\":", "\"mc\":", "\"lt\":", "\"id\":", "\"ed\":", "\"et\":", "\"md\":",
            "\"sn\":", "\"vi\":", "\"hn\":", "\"ub\":", "\"ud\":", "\"bt\":", "\"ivt\":",
        };

        var cursor = 0;
        foreach (var token in expectedOrder)
        {
            var index = payload.IndexOf(token, cursor, StringComparison.Ordinal);
            index.Should().BeGreaterThanOrEqualTo(0, $"载荷里应当按序包含 {token}");
            cursor = index + token.Length;
        }
    }

    [Fact]
    public void Roundtrip_preserves_every_field()
    {
        var original = LicenseTestKit.NewDocument(
            kind: LicenseKind.Temp,
            expiresOn: "20271231",
            exactExpiryUtcMs: 1_820_000_000_000,
            maxDays: 90,
            universal: 0,
            importDeadlineUtcMs: 1_800_000_000_000,
            issuedAtUtcMs: 1_790_000_000_000,
            networkVerifiedUtcMs: 1_795_000_000_000);
        original.Sig = "Zm9vYmFy";

        var decoded = LicenseCodec.Decode(LicenseCodec.Encode(original));

        decoded.V.Should().Be(original.V);
        decoded.Mc.Should().Be(original.Mc);
        decoded.Lt.Should().Be(original.Lt);
        decoded.Id.Should().Be(original.Id);
        decoded.Ed.Should().Be(original.Ed);
        decoded.Et.Should().Be(original.Et);
        decoded.Md.Should().Be(original.Md);
        decoded.Sn.Should().Be(original.Sn);
        decoded.Vi.Should().Be(original.Vi);
        decoded.Hn.Should().Be(original.Hn);
        decoded.Ub.Should().Be(original.Ub);
        decoded.Ud.Should().Be(original.Ud);
        decoded.Bt.Should().Be(original.Bt);
        decoded.Ivt.Should().Be(original.Ivt);
        decoded.Sig.Should().Be(original.Sig);
    }

    [Fact]
    public void Roundtrip_preserves_non_ascii_text()
    {
        var document = LicenseTestKit.NewDocument();
        document.Hn = "上海世博展览馆 · 3 号厅";
        document.Sig = "Zm9v";

        var decoded = LicenseCodec.Decode(LicenseCodec.Encode(document));

        decoded.Hn.Should().Be("上海世博展览馆 · 3 号厅");
    }

    [Fact]
    public void Encode_wraps_long_text_but_decode_ignores_whitespace()
    {
        var document = LicenseTestKit.NewDocument();
        document.Sn = new string('X', 200);
        document.Sig = new string('Y', 200);

        var encoded = LicenseCodec.Encode(document);

        encoded.Should().Contain("\n", "长文本应当折行，便于在记事本里查看");

        // 折行、空格、缩进都不该影响解析 —— 授权文本会在邮件/微信里被反复折腾。
        var mutated = "  " + encoded.Replace("\n", "\n\t") + "  \n";
        LicenseCodec.Decode(mutated).Sn.Should().Be(document.Sn);
    }

    [Fact]
    public void Decode_accepts_url_safe_alphabet_without_padding()
    {
        var document = LicenseTestKit.NewDocument();
        document.Sig = "abc";

        var standard = LicenseCodec.Encode(document);

        // 转成 URL-safe（'/' → '_'，'+' → '-'）并去掉尾部 padding。
        var urlSafe = standard.Replace('/', '_').Replace('+', '-').TrimEnd('=');

        LicenseCodec.Decode(urlSafe).Sig.Should().Be("abc");
    }

    [Theory]
    [InlineData(null, "为空")]
    [InlineData("", "为空")]
    [InlineData("   ", "为空")]
    [InlineData("这不是 base64!!!", "Base64")]
    [InlineData("e30=", "签名")]                  // base64("{}") —— 合法 JSON 对象，但没有 sig
    [InlineData("WzEsMiwzXQ==", "JSON 对象")]     // base64("[1,2,3]") —— 不是对象
    public void Decode_rejects_malformed_input(string? text, string expectedFragment)
    {
        Action act = () => LicenseCodec.Decode(text);

        act.Should().Throw<LicenseValidationException>()
            .WithMessage($"*{expectedFragment}*");
    }

    [Fact]
    public void TryDecode_reports_reason_instead_of_throwing()
    {
        var ok = LicenseCodec.TryDecode("###", out var document, out var error);

        ok.Should().BeFalse();
        document.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Decode_rejects_json_array_payload()
    {
        var arrayJson = Convert.ToBase64String("[1,2,3]"u8.ToArray());

        Action act = () => LicenseCodec.Decode(arrayJson);

        act.Should().Throw<LicenseValidationException>().WithMessage("*JSON 对象*");
    }

    [Fact]
    public void SignedPayload_places_signature_last()
    {
        var document = LicenseTestKit.NewDocument();
        document.Sig = "SIGVAL";

        var payload = LicenseCodec.BuildSignedPayload(document);

        payload.Should().EndWith("\"sig\":\"SIGVAL\"}");
    }
}
