using ControlServer.Agent.Video;
using ControlServer.Platform.Abstractions;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// <b>Annex-B 的"拆"与"改封装"</b>：枚举 NAL、剥尾部填充、换成长度前缀、去掉参数集。
///
/// <para>
/// 🔥 这块单独测的理由：它是<b>唯一一处"写错了也不报错"的字节搬运</b> ——
/// 长度前缀写成小端、或者把 3 字节起始码后面的 <c>0x00</c> 也算进 NAL，
/// 得到的都是一段<b>结构合法、内容错位</b>的码流。解码器只会说"解不出来"，
/// 而现场看到的是黑屏。
/// </para>
/// </summary>
public sealed class VideoBitstreamFramingTests
{
    [Fact]
    public void The_real_captured_access_unit_yields_its_parameter_sets()
    {
        var stream = VideoTestVectors.AnnexB(
            VideoTestVectors.RealH264Aud,
            VideoTestVectors.RealH264Sps,
            VideoTestVectors.RealH264Pps,
            VideoTestVectors.RealH264IdrHead);

        var units = ReadAll(stream);

        units.Should().HaveCount(4);

        units[0].Should().Equal(VideoTestVectors.RealH264Aud);
        units[1].Should().Equal(VideoTestVectors.RealH264Sps);
        units[2].Should().Equal(VideoTestVectors.RealH264Pps);
        units[3].Should().Equal(VideoTestVectors.RealH264IdrHead);
    }

    [Fact]
    public void Trailing_zero_padding_belongs_to_the_start_code_not_to_the_nal()
    {
        // 真机文件的第一个起始码就是 4 字节的 00 00 00 01；两个 NAL 之间也可能有一串 0x00。
        byte[] stream =
        [
            0x00, 0x00, 0x00, 0x01, 0x09, 0x10,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x28, 0xEE,
        ];

        var units = ReadAll(stream);

        units.Should().HaveCount(2);
        units[0].Should().Equal(new byte[] { 0x09, 0x10 });
        units[1].Should().Equal(new byte[] { 0x28, 0xEE });
    }

    [Fact]
    public void Two_start_codes_in_a_row_do_not_produce_an_empty_nal()
    {
        byte[] stream = [0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x01, 0x28, 0xEE];

        var units = ReadAll(stream);

        units.Should().HaveCount(1, "空 NAL 没有语义，交出去只会让上层的计数与索引都错位一位");
        units[0].Should().Equal(new byte[] { 0x28, 0xEE });
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x01, 0x02, 0x03 })]
    [InlineData(new byte[] { 0x00, 0x00, 0x02, 0x01 })]
    public void A_buffer_without_a_start_code_yields_nothing(byte[] stream)
        => ReadAll(stream).Should().BeEmpty();

    [Fact]
    public void The_canonical_form_is_length_prefixed_big_endian()
    {
        var stream = VideoTestVectors.AnnexB(VideoTestVectors.RealH264Aud, VideoTestVectors.RealH264Pps);

        var size = AnnexB.MeasureCanonicalAccessUnit(stream, VideoCodec.H264, includeParameterSets: true);

        size.Should().Be((4 + 2) + (4 + 4));

        var destination = new byte[size];

        AnnexB.WriteCanonicalAccessUnit(stream, VideoCodec.H264, includeParameterSets: true, destination)
            .Should().Be(size);

        destination.Should().Equal(new byte[]
        {
            0x00, 0x00, 0x00, 0x02, 0x09, 0x10,
            0x00, 0x00, 0x00, 0x04, 0x28, 0xEE, 0x3C, 0xB0,
        });
    }

    [Fact]
    public void Parameter_sets_are_left_out_when_the_description_carries_them()
    {
        var stream = VideoTestVectors.AnnexB(
            VideoTestVectors.RealH264Aud,
            VideoTestVectors.RealH264Sps,
            VideoTestVectors.RealH264Pps,
            VideoTestVectors.RealH264IdrHead);

        var withSets = AnnexB.MeasureCanonicalAccessUnit(stream, VideoCodec.H264, includeParameterSets: true);
        var withoutSets = AnnexB.MeasureCanonicalAccessUnit(stream, VideoCodec.H264, includeParameterSets: false);

        var expectedSaving = (4 + VideoTestVectors.RealH264Sps.Length) + (4 + VideoTestVectors.RealH264Pps.Length);

        (withSets - withoutSets).Should().Be(expectedSaving, "带外那条路只允许 AUD 与片数据进码流");

        var destination = new byte[withoutSets];

        AnnexB.WriteCanonicalAccessUnit(stream, VideoCodec.H264, includeParameterSets: false, destination)
            .Should().Be(withoutSets);

        // 第一段必须直接是 AUD（2 字节），而不是 SPS —— 顺序错了不会报错，只会让解码器先撞上一段 NAL 长度对不上的数据。
        destination[..8].Should().Equal(new byte[]
        {
            0x00, 0x00, 0x00, 0x02, 0x09, 0x10,
            0x00, 0x00,
        });
    }

    [Fact]
    public void A_destination_that_is_one_byte_too_small_fails_instead_of_overrunning()
    {
        var stream = VideoTestVectors.AnnexB(
            VideoTestVectors.RealH264Aud,
            VideoTestVectors.RealH264Sps,
            VideoTestVectors.RealH264Pps);

        var size = AnnexB.MeasureCanonicalAccessUnit(stream, VideoCodec.H264, includeParameterSets: true);

        AnnexB.WriteCanonicalAccessUnit(stream, VideoCodec.H264, includeParameterSets: true, new byte[size - 1])
            .Should().Be(-1);

        AnnexB.MeasureCanonicalAccessUnit(stream, VideoCodec.H264, includeParameterSets: true)
            .Should().Be(size, "测量是纯读操作，不该被上一次写失败影响");
    }

    [Fact]
    public void Emulation_prevention_bytes_are_dropped_by_input_context()
    {
        // 源数据 00 00 03 在码流里长成 00 00 03 03：第一个 03 是转义字节，第二个是原始数据。
        // ⚠️ 如果判据看的是"已经解出来的输出"，第二个 03 会被一起删掉，解成 00 00。
        var reader = new RbspBitReader(new byte[] { 0x00, 0x00, 0x03, 0x03 });

        ReadByte(ref reader).Should().Be(0x00);
        ReadByte(ref reader).Should().Be(0x00);
        ReadByte(ref reader).Should().Be(0x03);

        reader.TryReadBits(8, out _).Should().BeFalse("总共只有 3 个有效字节");
    }

    [Fact]
    public void A_third_zero_byte_is_not_swallowed_as_an_escape()
    {
        // 源数据 00 00 00 在码流里长成 00 00 03 00：转义字节之后那个 00 是真数据。
        var reader = new RbspBitReader(new byte[] { 0x00, 0x00, 0x03, 0x00, 0x2A });

        ReadByte(ref reader).Should().Be(0x00);
        ReadByte(ref reader).Should().Be(0x00);
        ReadByte(ref reader).Should().Be(0x00);
        ReadByte(ref reader).Should().Be(0x2A);
    }

    [Theory]
    [InlineData(0u, 0x80)]   // '1'
    [InlineData(1u, 0x40)]   // '010'
    [InlineData(2u, 0x60)]   // '011'
    [InlineData(3u, 0x20)]   // '00100'
    [InlineData(6u, 0x38)]   // '00111' → 前导 2 个 0，然后是 111
    public void Exp_golomb_values_match_the_specification(uint expected, byte firstByte)
    {
        var reader = new RbspBitReader(new byte[] { firstByte, 0x00, 0x00, 0x00 });

        reader.TryReadUe(out var value).Should().BeTrue();

        value.Should().Be(expected);
    }

    [Fact]
    public void An_exp_golomb_run_that_cannot_fit_is_reported_as_a_failure()
    {
        // 40 个 0 位：前导零超过 31 位就说明这个值装不进 uint。回绕会读出一个"合法但错"的数。
        var reader = new RbspBitReader(new byte[5]);

        reader.TryReadUe(out _).Should().BeFalse();
    }

    [Fact]
    public void The_two_codecs_take_their_nal_type_from_different_bits()
    {
        // H.265 SPS 的 NAL 头首字节 0x42：(0x42 >> 1) & 0x3F = 33 = SPS。
        NalUnitTypes.Read(VideoCodec.Hevc, new byte[] { 0x42, 0x01 }).Should().Be(NalUnitTypes.H265Sps);

        // 🔥 拿 H.264 的取法去读同一个字节会得到 2 —— 一个**合法但不对**的类型。
        // 于是 H.265 的参数集会被当成图像数据，一路安静地错下去。
        NalUnitTypes.Read(VideoCodec.H264, new byte[] { 0x42 }).Should().Be(2);
    }

    [Fact]
    public void An_idr_is_a_keyframe_but_a_cra_is_not()
    {
        NalUnitTypes.IsKeyframeSlice(VideoCodec.H264, NalUnitTypes.H264IdrSlice).Should().BeTrue();
        NalUnitTypes.IsKeyframeSlice(VideoCodec.H264, NalUnitTypes.H264NonIdrSlice).Should().BeFalse();

        NalUnitTypes.IsKeyframeSlice(VideoCodec.Hevc, NalUnitTypes.H265IdrWRadl).Should().BeTrue();
        NalUnitTypes.IsKeyframeSlice(VideoCodec.Hevc, NalUnitTypes.H265IdrNLp).Should().BeTrue();

        NalUnitTypes.IsKeyframeSlice(VideoCodec.Hevc, 21)
            .Should().BeFalse("CRA 能独立解码，但后面可以跟前置的 RASL 图，语义不等于「从这里开始全都能解」");
    }

    [Fact]
    public void The_null_nal_type_is_not_zero()
    {
        NalUnitTypes.Read(VideoCodec.H264, new byte[0]).Should().Be(-1, "读不出来不是「类型 0」");
        NalUnitTypes.Read(VideoCodec.Hevc, new byte[] { 0x42 })
            .Should().Be(-1, "H.265 的 NAL 头是 2 字节，只给 1 字节读不出类型");

        NalUnitTypes.IsParameterSet(VideoCodec.H264, -1).Should().BeFalse();
    }

    private static List<byte[]> ReadAll(ReadOnlySpan<byte> annexB)
    {
        var units = new List<byte[]>();
        var reader = new AnnexB.Enumerator(annexB);

        while (reader.MoveNext()) units.Add(reader.Current.ToArray());

        return units;
    }

    private static byte ReadByte(ref RbspBitReader reader)
    {
        reader.TryReadBits(8, out var value).Should().BeTrue();

        return (byte)value;
    }
}
