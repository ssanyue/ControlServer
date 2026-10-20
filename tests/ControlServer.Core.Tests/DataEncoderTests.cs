using System.Text;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 编码内核的<b>行为等价性测试</b>。
/// 覆盖数据准备链路上的四个纯函数：
/// <c>PrepareData</c> / <c>ParseHex</c> / <c>ParseMacAddress</c> / <c>GetPreview</c>。
/// 用例来自 M2 方案 §11.3 的编码与 MAC 向量表。
/// </summary>
public sealed class DataEncoderTests
{
    // ═══════════════════════════════════════════════════════════
    // ANSI 编码可用性（方案 §10.4 的坑：Linux 必须注册代码页）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Ansi_code_page_936_is_available()
    {
        // 这是 M2-B 的**环境前提**：Core 引了 System.Text.Encoding.CodePages，
        // 静态构造里无条件注册。拿不到 CP936 就说明包没进来或运行时被裁剪。
        DataEncoder.HasCodePagesProvider.Should().BeTrue("必须成功注册 CodePagesEncodingProvider");
        DataEncoder.AnsiIsExact.Should().BeTrue(
            $"必须取到 GBK(CP936)，实际取到 {DataEncoder.AnsiEncodingName}；"
            + $"错误：{DataEncoder.DiagnosticError ?? "无"}");

        DataEncoder.DescribeAnsiEncoding().Should().NotContain("未取到");
    }

    // ═══════════════════════════════════════════════════════════
    // 编码向量（§11.3）
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Hex")]
    [InlineData("hex")]
    [InlineData("HEX")]
    public void Hex_accepts_0x_prefix_spaces_and_is_case_insensitive(string dataType)
    {
        // 旧版向量：("Hex","0x41 42") → [0x41,0x42]
        DataEncoder.Encode(dataType, "0x41 42").Should().Equal(0x41, 0x42);
    }

    [Fact]
    public void Hex_strips_newlines_tabs_and_prefixes()
    {
        DataEncoder.Encode("Hex", "0x41\r\n\t0x42").Should().Equal(0x41, 0x42);
    }

    [Fact]
    public void Hex_with_odd_length_throws_FormatException()
    {
        // 旧版向量：("Hex","ABC") → 异常。**必须是抛异常**，不能静默截断。
        var act = () => DataEncoder.Encode("Hex", "ABC");

        act.Should().Throw<FormatException>().WithMessage("*偶数*");
    }

    [Fact]
    public void Hex_with_invalid_characters_throws()
    {
        var act = () => DataEncoder.Encode("Hex", "ZZ");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Hex_with_blank_content_yields_empty_payload()
    {
        // 旧版：IsNullOrWhiteSpace → Array.Empty，不抛。
        DataEncoder.Encode("Hex", "   ").Should().BeEmpty();
        DataEncoder.Encode("Hex", "").Should().BeEmpty();
        DataEncoder.Encode("Hex", null).Should().BeEmpty();
    }

    [Fact]
    public void Ascii_replaces_non_ascii_with_question_mark()
    {
        // 旧版向量：("ASCII","中") → [0x3F]
        DataEncoder.Encode("ASCII", "中").Should().Equal(0x3F);
    }

    [Fact]
    public void Utf8_encodes_chinese_as_three_bytes()
    {
        // 旧版向量：("UTF-8","中") → [0xE4,0xB8,0xAD]
        DataEncoder.Encode("UTF-8", "中").Should().Equal(0xE4, 0xB8, 0xAD);
    }

    [Fact]
    public void Ansi_encodes_chinese_with_gbk()
    {
        // 旧版向量：("ANSI","中") → [0xD6,0xD0]（GBK）
        DataEncoder.Encode("ANSI", "中").Should().Equal(0xD6, 0xD0);
    }

    [Theory]
    [InlineData("String")]
    [InlineData("string")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("未知类型")]
    public void String_and_unknown_types_fall_back_to_utf8(string? dataType)
    {
        // 旧版 PrepareData 的 else 分支：String 与一切未知类型都按 UTF-8。
        DataEncoder.Encode(dataType, "中").Should().Equal(Encoding.UTF8.GetBytes("中"));
    }

    [Fact]
    public void Data_type_matching_is_case_insensitive()
    {
        DataEncoder.Encode("ansi", "中").Should().Equal(DataEncoder.Encode("ANSI", "中"));
        DataEncoder.Encode("ascii", "中").Should().Equal(DataEncoder.Encode("ASCII", "中"));
        DataEncoder.Encode("utf-8", "中").Should().Equal(DataEncoder.Encode("UTF-8", "中"));
    }

    [Fact]
    public void TryEncode_reports_error_instead_of_throwing()
    {
        DataEncoder.TryEncode("Hex", "ABC", out var bytes, out var error).Should().BeFalse();
        bytes.Should().BeEmpty();
        error.Should().NotBeNullOrWhiteSpace();

        DataEncoder.TryEncode("UTF-8", "中", out var ok, out var noError).Should().BeTrue();
        ok.Should().Equal(0xE4, 0xB8, 0xAD);
        noError.Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════
    // 预览（§11.3 / 旧版 GetPreview）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Preview_echoes_raw_text_for_hex()
    {
        DataEncoder.Preview("Hex", new byte[] { 0x41, 0x42 }, "41 42").Should().Be("41 42");
    }

    [Fact]
    public void Preview_decodes_ansi_with_gbk_not_utf8()
    {
        // 关键：ANSI 中文若按 UTF-8 还原就是乱码 —— 必须用 GBK 还原。
        var bytes = DataEncoder.Encode("ANSI", "中");
        DataEncoder.Preview("ANSI", bytes, "中").Should().Be("中");
    }

    [Fact]
    public void Preview_returns_empty_for_empty_payload()
    {
        DataEncoder.Preview("UTF-8", Array.Empty<byte>(), "abc").Should().BeEmpty();
        DataEncoder.Preview("UTF-8", null, "abc").Should().BeEmpty();
    }

    [Fact]
    public void ToSpacedHex_and_ToHexString_format_consistently()
    {
        var bytes = new byte[] { 0x0A, 0xFF, 0x01 };

        DataEncoder.ToSpacedHex(bytes).Should().Be("0A FF 01");
        DataEncoder.ToHexString(bytes).Should().Be("0AFF01");
        DataEncoder.ToHexString(Array.Empty<byte>()).Should().BeEmpty();
    }

    [Fact]
    public void NormalizeHex_removes_all_whitespace_and_uppercases()
    {
        DataEncoder.NormalizeHex(" 41\t42\n ").Should().Be("4142");
        DataEncoder.NormalizeHex("aa bb").Should().Be("AABB");
        DataEncoder.NormalizeHex(null).Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════
    // MAC 向量（§11.3）
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF")]
    [InlineData("AABBCCDDEEFF")]
    [InlineData("aa-bb-cc-dd-ee-ff")]
    [InlineData("AA.BB.CC.DD.EE.FF")]
    [InlineData("aa bb cc dd ee ff")]
    public void Mac_parses_all_supported_formats_to_six_bytes(string mac)
    {
        var bytes = DataEncoder.TryParseMac(mac);

        bytes.Should().NotBeNull();
        bytes!.Should().Equal(0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF);
    }

    [Theory]
    [InlineData("AABBCCDDEE")]
    [InlineData("AABBCCDDEEFFF")]
    [InlineData("GG:BB:CC:DD:EE:FF")]
    [InlineData("")]
    [InlineData(null)]
    public void Mac_shorter_than_twelve_or_invalid_returns_null(string? mac)
    {
        DataEncoder.TryParseMac(mac).Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════
    // WoL 语义（§5.3）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Magic_packet_is_six_ff_followed_by_mac_sixteen_times()
    {
        var mac = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        var packet = DataEncoder.BuildMagicPacket(mac);

        packet.Should().HaveCount(6 + 16 * 6);
        packet.Take(6).Should().AllBeEquivalentTo((byte)0xFF);

        for (var i = 0; i < 16; i++)
        {
            packet.Skip(6 + i * 6).Take(6).Should().Equal(mac, $"第 {i + 1} 次重复");
        }
    }

    [Fact]
    public void Magic_packet_rejects_wrong_length_mac()
    {
        var act = () => DataEncoder.BuildMagicPacket(new byte[] { 0x01, 0x02 });
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null, "255.255.255.255")]
    [InlineData("", "255.255.255.255")]
    [InlineData("   ", "255.255.255.255")]
    [InlineData("0.0.0.0", "255.255.255.255")]
    [InlineData("192.168.1.50", "192.168.1.50")]
    public void Wol_host_falls_back_to_broadcast(string? host, string expected)
    {
        DataEncoder.NormalizeWolHost(host).Should().Be(expected);
    }

    [Fact]
    public void Wol_default_port_is_nine()
    {
        DataEncoder.DefaultWolPort.Should().Be(9);
    }

    // ═══════════════════════════════════════════════════════════
    // 类型判定
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Type_predicates_match_desktop_values()
    {
        DataEncoder.IsHex(DataTypes.Hex).Should().BeTrue();
        DataEncoder.IsAnsi(DataTypes.Ansi).Should().BeTrue();
        DataEncoder.IsAscii(DataTypes.Ascii).Should().BeTrue();
        DataEncoder.IsUtf8(DataTypes.Utf8).Should().BeTrue();
        DataEncoder.IsCommandLibrary(DataTypes.CommandLibrary).Should().BeTrue();

        DataEncoder.IsHex(DataTypes.Utf8).Should().BeFalse();
        DataEncoder.IsCommandLibrary("String").Should().BeFalse();
        DataEncoder.IsHex(null).Should().BeFalse();
    }

    [Fact]
    public void Command_library_type_name_is_exactly_the_chinese_literal()
    {
        // 旧配置里存的是中文字面量，改一个字符旧配置就全部失效。
        DataTypes.CommandLibrary.Should().Be("指令库");
        DataTypes.Utf8.Should().Be("UTF-8");
        DataTypes.Ansi.Should().Be("ANSI");
        DataTypes.Ascii.Should().Be("ASCII");
        DataTypes.Hex.Should().Be("Hex");
        DataTypes.String.Should().Be("String");
    }
}
