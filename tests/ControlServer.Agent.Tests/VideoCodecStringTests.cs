using ControlServer.Agent.Video;
using ControlServer.Platform.Abstractions;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// <b>codec 串的推导</b> —— 送给 <c>VideoDecoder.isConfigSupported({ codec })</c> 的那个字符串。
///
/// <para>
/// 🔥 <b>为什么这一块值得单独一条用例</b>：串写错的后果是 <c>configure()</c> 抛
/// <c>NotSupportedError</c>，而<b>那句话的字面意思是"浏览器不支持这个编码"</b>。
/// 于是排查会一路走向"换编码格式 / 换浏览器"，而真因只是我们把 <c>640032</c> 写成了 <c>640028</c>。
/// ⇒ 串必须<b>从真实 SPS 反推</b>，不许手写（M5.4-a 实测本机是 High / level 5.0）。
/// </para>
/// </summary>
public sealed class VideoCodecStringTests
{
    [Fact]
    public void The_codec_string_comes_from_the_real_sps_of_this_machine()
    {
        AvcSpsInfo.TryParse(VideoTestVectors.RealH264Sps, out var sps).Should().BeTrue();

        sps.ProfileIdc.Should().Be(0x64, "实测 SPS 的 profile_idc 是 High");
        sps.ConstraintFlags.Should().Be(0x00);
        sps.LevelIdc.Should().Be(0x32, "0x32 = 50 = level 5.0");

        sps.ToCodecString(VideoSampleEntries.Avc1).Should().Be("avc1.640032");
        sps.ToCodecString(VideoSampleEntries.Avc3).Should().Be("avc3.640032");

        sps.Describe().Should().Contain("High").And.Contain("Level 5.0");
    }

    [Fact]
    public void A_baseline_sps_produces_the_classic_baseline_string()
    {
        // 0x67 = nal_ref_idc 3 + type 7；0x42 = Baseline；0xC0 = 约束；0x1F = level 3.1
        byte[] spsNal = [0x67, 0x42, 0xC0, 0x1F, 0x00, 0x00];

        AvcSpsInfo.TryParse(spsNal, out var sps).Should().BeTrue();

        sps.ToCodecString(VideoSampleEntries.Avc1).Should().Be("avc1.42C01F");
    }

    [Fact]
    public void A_nal_that_is_not_an_sps_is_rejected_instead_of_misread()
    {
        // PPS 的前几个字节恰好够长 —— 只有类型检查能挡住它。
        AvcSpsInfo.TryParse(VideoTestVectors.RealH264Pps, out _).Should().BeFalse();
        AvcSpsInfo.TryParse(new byte[] { 0x27, 0x64, 0x00 }, out _).Should().BeFalse("长度不够 4 字节");
        AvcSpsInfo.TryParse(new byte[0], out _).Should().BeFalse();
    }

    [Fact]
    public void The_main_profile_string_is_1_6_not_1_2()
    {
        // 🔥 位流里 flag[1]（Main）与 flag[2]（Main 10）都是 1 ⇒ 0x60000000；
        //    反转位序后是 0x6，所以串里的第二段是 6。
        //    （3GPP TS 26.244 §A.2.2 特意解释了这一点：Main 码流按 ISO/IEC 23008-2
        //      也应当标成与 Main 10 兼容。）
        var codecString = HevcString(VideoTestVectors.HevcSps(
            profileSpace: 0,
            tierFlag: false,
            profileIdc: 1,
            compatibilityFlags: 0x60000000,
            constraintFlags: 0xB00000000000,
            levelIdc: 93));

        codecString.Should().Be("hvc1.1.6.L93.B0");
    }

    [Fact]
    public void The_main10_profile_string_is_2_4()
    {
        var codecString = HevcString(VideoTestVectors.HevcSps(
            profileSpace: 0,
            tierFlag: false,
            profileIdc: 2,
            compatibilityFlags: 0x20000000,
            constraintFlags: 0xB00000000000,
            levelIdc: 120));

        codecString.Should().Be("hvc1.2.4.L120.B0");
    }

    [Fact]
    public void The_compatibility_flags_are_fully_bit_reversed()
    {
        // 只有最高位是 1 ⇒ 反转后是 1（不是 80000000）。
        HevcString(VideoTestVectors.HevcSps(0, false, 1, 0x80000000, 0xB00000000000, 93))
            .Should().EndWith(".1.L93.B0");

        // 只有最低位是 1 ⇒ 反转后成了最高位。
        HevcString(VideoTestVectors.HevcSps(0, false, 1, 0x00000001, 0xB00000000000, 93))
            .Should().EndWith(".80000000.L93.B0");

        // 整字节反转（0x0000FF00 → 0x00FF0000）在"按字节调序"下也一样 ——
        // 所以能钉住"逐位反转"的是上面两个，不是这一个。
        HevcString(VideoTestVectors.HevcSps(0, false, 1, 0x0000FF00, 0xB00000000000, 93))
            .Should().EndWith(".FF0000.L93.B0");
    }

    [Fact]
    public void The_profile_space_and_tier_are_encoded_as_a_prefix_and_a_letter()
    {
        HevcString(VideoTestVectors.HevcSps(1, false, 4, 0x80000000, 0xB00000000000, 120))
            .Should().Be("hvc1.A4.1.L120.B0");

        HevcString(VideoTestVectors.HevcSps(2, true, 1, 0x60000000, 0xB00000000000, 150))
            .Should().Be("hvc1.B1.6.H150.B0");
    }

    [Fact]
    public void Constraint_bytes_are_written_in_order_and_trailing_zeroes_may_be_dropped()
    {
        // 全零也要留一个字节 —— 整个字段不能凭空消失。
        HevcString(VideoTestVectors.HevcSps(0, false, 1, 0x60000000, 0, 93))
            .Should().Be("hvc1.1.6.L93.00");

        // 只有第一个字节非零 ⇒ 后面 5 个零字节省略。
        HevcString(VideoTestVectors.HevcSps(0, false, 1, 0x60000000, 0xB00000000000, 93))
            .Should().Be("hvc1.1.6.L93.B0");

        // 末字节有非零 ⇒ 它前面的零字节**不许**省。
        HevcString(VideoTestVectors.HevcSps(0, false, 1, 0x60000000, 0x000000000023, 93))
            .Should().Be("hvc1.1.6.L93.00.00.00.00.00.23");
    }

    [Fact]
    public void Chroma_and_bit_depth_survive_the_variable_length_profile_tier_level()
    {
        // 带子层时 profile_tier_level 会变长；色度/位深在它**后面**，
        // 所以"按固定偏移取字节"会取到子层信息上去。
        var sps = VideoTestVectors.HevcSps(
            profileSpace: 0,
            tierFlag: false,
            profileIdc: 1,
            compatibilityFlags: 0x60000000,
            constraintFlags: 0xB00000000000,
            levelIdc: 93,
            chromaFormatIdc: 1,
            bitDepthLumaMinus8: 2,
            bitDepthChromaMinus8: 2,
            maxSubLayersMinus1: 3);

        HevcSpsInfo.TryParse(sps, out var info).Should().BeTrue();

        info.ChromaFormatIdc.Should().Be(1);
        info.BitDepthLumaMinus8.Should().Be(2);
        info.BitDepthChromaMinus8.Should().Be(2);
        info.MaxSubLayersMinus1.Should().Be(3);
        info.Describe().Should().Contain("10 bit").And.Contain("4:2:0");
    }

    [Fact]
    public void A_corrupt_hevc_sps_fails_instead_of_reporting_zeros()
    {
        HevcSpsInfo.TryParse(new byte[0], out _).Should().BeFalse();
        HevcSpsInfo.TryParse(new byte[] { 0x42, 0x01 }, out _).Should().BeFalse();

        // 类型不对（这是 PPS 的头）—— 解出来的会是一串"合法但错"的值。
        HevcSpsInfo.TryParse(new byte[] { 0x44, 0x01, 0x00, 0x00, 0x00, 0x00 }, out _).Should().BeFalse();

        // 类型对、载荷全零：profile_tier_level 之后是无效的 ue(v)，必须失败而不是回一串 0。
        HevcSpsInfo.TryParse([0x42, 0x01, .. new byte[22]], out _).Should().BeFalse();
    }

    /// <summary>合成一个 HEVC SPS，解出来，给出 codec 串（解不出来就直接让断言炸掉）。</summary>
    private static string HevcString(byte[] sps)
    {
        HevcSpsInfo.TryParse(sps, out var info).Should().BeTrue("合成的 SPS 应当能被解出来");

        return info.ToCodecString(VideoSampleEntries.Hvc1);
    }
}
