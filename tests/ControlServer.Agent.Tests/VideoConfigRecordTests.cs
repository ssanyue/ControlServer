using ControlServer.Agent.Video;
using ControlServer.Platform.Abstractions;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// <b>avcC / hvcC</b>（交给 <c>configure({ description })</c> 的那段字节）与
/// 「从一段 Annex-B 里把参数集凑齐」这件事。
///
/// <para>
/// 🔥 这两种记录的形状<b>全是定长字段加保留位</b>，少写一位后面<b>整体错位</b>。
/// 而错位之后解码器只会说"配置非法"，不会说"你第 17 个字节写错了" ——
/// 所以判据只能是<b>逐字节对</b>，不能是"没抛异常"。
/// </para>
/// </summary>
public sealed class VideoConfigRecordTests
{
    [Fact]
    public void The_avcc_of_a_real_sps_and_pps_is_exactly_this()
    {
        var record = AvcDecoderConfiguration.Build(
            [VideoTestVectors.RealH264Sps],
            [VideoTestVectors.RealH264Pps]);

        record.Should().NotBeNull();

        byte[] expected =
        [
            0x01,               // configurationVersion
            0x64,               // AVCProfileIndication（High）
            0x00,               // profile_compatibility
            0x32,               // AVCLevelIndication（5.0）
            0xFF,               // 111111 + lengthSizeMinusOne = 3 ⇒ 4 字节长度前缀
            0xE1,               // 111 + 1 个 SPS
            0x00, 0x19,         // SPS 长度 25
            .. VideoTestVectors.RealH264Sps,
            0x01,               // 1 个 PPS
            0x00, 0x04,         // PPS 长度 4
            .. VideoTestVectors.RealH264Pps,
        ];

        record!.Should().Equal(expected);
    }

    [Fact]
    public void An_avcc_without_both_parameter_set_kinds_is_not_built_at_all()
    {
        AvcDecoderConfiguration.Build([], [VideoTestVectors.RealH264Pps])
            .Should().BeNull("缺 SPS");

        AvcDecoderConfiguration.Build([VideoTestVectors.RealH264Sps], [])
            .Should().BeNull("缺 PPS");

        AvcDecoderConfiguration.Build([VideoTestVectors.RealH264Pps], [VideoTestVectors.RealH264Pps])
            .Should().BeNull("第一段不是 SPS");

        AvcDecoderConfiguration.Build([VideoTestVectors.RealH264Sps], [VideoTestVectors.RealH264Sps])
            .Should().BeNull("第二段不是 PPS");
    }

    [Fact]
    public void The_reader_pulls_the_parameter_sets_out_of_a_real_shape_access_unit()
    {
        var stream = VideoTestVectors.AnnexB(
            VideoTestVectors.RealH264Aud,
            VideoTestVectors.RealH264Sps,
            VideoTestVectors.RealH264Pps,
            VideoTestVectors.RealH264IdrHead);

        var built = VideoParameterSetReader.TryRead(stream, VideoCodec.H264, VideoSampleEntries.Avc1);

        built.Should().NotBeNull();
        built!.Codec.Should().Be(VideoCodec.H264);
        built.CodecString.Should().Be("avc1.640032");
        built.ParameterSetNalCount.Should().Be(2);

        built.Description.Should().Equal(
            AvcDecoderConfiguration.Build([VideoTestVectors.RealH264Sps], [VideoTestVectors.RealH264Pps])!);

        VideoParameterSetReader.TryRead(stream, VideoCodec.H264, VideoSampleEntries.Avc3)!
            .CodecString.Should().Be("avc3.640032", "带内那条路用的是 avc3");
    }

    [Fact]
    public void A_stream_that_never_carries_an_sps_yields_nothing()
    {
        // 真机码流里 **PPS 每帧都有、SPS 只在第一个关键帧出现**（实测）。
        // 所以"只扫到一个非关键帧"这种输入是真实存在的，它必须如实返回"抽不出来"。
        var stream = VideoTestVectors.AnnexB(
            VideoTestVectors.RealH264Aud,
            VideoTestVectors.RealH264Pps,
            VideoTestVectors.RealH264IdrHead);

        VideoParameterSetReader.TryRead(stream, VideoCodec.H264, VideoSampleEntries.Avc1)
            .Should().BeNull();
    }

    [Fact]
    public void Repeated_parameter_sets_are_stored_once()
    {
        // PPS 每帧都带 ⇒ 一段多帧的输入里会有几十份一模一样的 PPS。
        var stream = VideoTestVectors.AnnexB(
            VideoTestVectors.RealH264Sps,
            VideoTestVectors.RealH264Pps,
            VideoTestVectors.RealH264Pps,
            VideoTestVectors.RealH264Pps);

        var built = VideoParameterSetReader.TryRead(stream, VideoCodec.H264, VideoSampleEntries.Avc1);

        built.Should().NotBeNull();
        built!.ParameterSetNalCount.Should().Be(4, "扫到几份要说实话");

        // 第 6 个字节的低 5 位 = SPS 个数；紧跟在 SPS 之后的那一个字节 = PPS 个数。
        var cursor = 6 + 2 + VideoTestVectors.RealH264Sps.Length;
        built.Description[cursor].Should().Be(1, "去重之后只留一份 PPS");
    }

    [Fact]
    public void The_hvcc_header_of_a_main_profile_sps_is_exactly_this()
    {
        var sps = VideoTestVectors.HevcSps(0, false, 1, 0x60000000, 0xB00000000000, 93);
        var vps = VideoTestVectors.HevcFiller(NalUnitTypes.H265Vps, 0xAA);
        var pps = VideoTestVectors.HevcFiller(NalUnitTypes.H265Pps, 0xBB);

        HevcSpsInfo.TryParse(sps, out var info).Should().BeTrue();

        var record = HvcDecoderConfiguration.Build(info, [vps], [sps], [pps]);

        record.Should().NotBeNull();

        var bytes = record!;

        bytes.Should().HaveCount(23 + (3 * 3) + (2 + vps.Length) + (2 + sps.Length) + (2 + pps.Length));

        bytes[..23].Should().Equal(new byte[]
        {
            0x01,                           // configurationVersion
            0x01,                           // profile_space 0 + tier 0 + profile_idc 1
            0x60, 0x00, 0x00, 0x00,         // ⚠️ **位流顺序**，不是 codec 串里那个反转过的值
            0xB0, 0x00, 0x00, 0x00, 0x00, 0x00,   // general_constraint_indicator_flags
            0x5D,                           // general_level_idc 93
            0xF0, 0x00,                     // 1111 + min_spatial_segmentation_idc 0
            0xFC,                           // 111111 + parallelismType 0
            0xFD,                           // 111111 + chroma_format_idc 1（4:2:0）
            0xF8,                           // 11111 + bit_depth_luma_minus8 0
            0xF8,                           // 11111 + bit_depth_chroma_minus8 0
            0x00, 0x00,                     // avgFrameRate 0
            0x0F,                           // constantFrameRate 0 + 1 个理论层 + temporalIdNested 1 + lengthSizeMinusOne 3
            0x03,                           // 3 个数组：VPS / SPS / PPS
        });

        // 数组按 VPS(32) → SPS(33) → PPS(34) 的顺序排，每段带上 array_completeness。
        var cursor = 23;

        bytes[cursor].Should().Be(0x80 | 32);
        cursor += 3 + 2 + vps.Length;

        bytes[cursor].Should().Be(0x80 | 33);
        cursor += 3 + 2 + sps.Length;

        bytes[cursor].Should().Be(0x80 | 34);
    }

    [Fact]
    public void An_hvcc_without_a_vps_still_has_two_arrays()
    {
        var sps = VideoTestVectors.HevcSps(0, false, 1, 0x60000000, 0xB00000000000, 93);

        HevcSpsInfo.TryParse(sps, out var info).Should().BeTrue();

        var record = HvcDecoderConfiguration.Build(
            info,
            [],
            [sps],
            [VideoTestVectors.HevcFiller(NalUnitTypes.H265Pps, 0xBB)]);

        record.Should().NotBeNull();

        var bytes = record!;

        bytes[22].Should().Be(2);
        bytes[23].Should().Be(0x80 | 33, "没有 VPS 就直接从 SPS 那个数组开始");
    }

    [Fact]
    public void An_hvcc_without_sps_or_pps_is_not_built_at_all()
    {
        var sps = VideoTestVectors.HevcSps(0, false, 1, 0x60000000, 0xB00000000000, 93);

        HevcSpsInfo.TryParse(sps, out var info).Should().BeTrue();

        HvcDecoderConfiguration.Build(info, [], [], []).Should().BeNull("缺 SPS 与 PPS");
        HvcDecoderConfiguration.Build(info, [], [sps], []).Should().BeNull("缺 PPS");
    }

    [Fact]
    public void The_reader_builds_the_same_hvcc_from_an_annex_b_stream()
    {
        var sps = VideoTestVectors.HevcSps(0, false, 1, 0x60000000, 0xB00000000000, 93);
        var vps = VideoTestVectors.HevcFiller(NalUnitTypes.H265Vps, 0xAA, 0xAA);
        var pps = VideoTestVectors.HevcFiller(NalUnitTypes.H265Pps, 0xBB);
        var slice = VideoTestVectors.HevcFiller(NalUnitTypes.H265IdrWRadl, 0xCC);

        var stream = VideoTestVectors.AnnexB(vps, sps, pps, slice);

        var built = VideoParameterSetReader.TryRead(stream, VideoCodec.Hevc, VideoSampleEntries.Hvc1);

        built.Should().NotBeNull();
        built!.Codec.Should().Be(VideoCodec.Hevc);
        built.CodecString.Should().Be("hvc1.1.6.L93.B0");
        built.ParameterSetNalCount.Should().Be(3);
        built.Description[22].Should().Be(3);

        VideoParameterSetReader.TryRead(stream, VideoCodec.Hevc, VideoSampleEntries.Hev1)!
            .CodecString.Should().Be("hev1.1.6.L93.B0");
    }

    [Fact]
    public void The_temporal_layer_count_is_clamped_to_what_the_field_can_hold()
    {
        // numTemporalLayers 只有 3 位，最多 7；max_sub_layers_minus1 到 7 时不能再 +1 往上顶。
        var sps = VideoTestVectors.HevcSps(0, false, 1, 0x60000000, 0xB00000000000, 93, maxSubLayersMinus1: 7);

        HevcSpsInfo.TryParse(sps, out var info).Should().BeTrue();

        var record = HvcDecoderConfiguration.Build(
            info, [], [sps], [VideoTestVectors.HevcFiller(NalUnitTypes.H265Pps, 0xBB)]);

        record.Should().NotBeNull();

        (record![21] >> 3 & 0x07).Should().Be(7, "8 层会被截到 7 —— 字段放不下 8");
    }
}
