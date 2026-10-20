using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent.Video;

/// <summary>
/// 一个 H.264 <b>SPS</b> 里、我们真正要用的三个字节。
///
/// <para>
/// 🔥 <b>幸好 H.264 的这件事是字节对齐的</b>：SPS 的载荷开头就是
/// <c>profile_idc</c>(8) / 一组约束标志(8) / <c>level_idc</c>(8)，
/// <b>不需要解析任何位</b>就能拿到 codec 串要的那 6 个十六进制位。
/// 这也是"codec 串必须从真实 SPS 反推"这句话成本极低的原因 —— 真的只是抄 3 个字节。
/// </para>
/// </summary>
/// <param name="ProfileIdc">例：<c>0x64</c> = High。</param>
/// <param name="ConstraintFlags">SPS 载荷第 2 字节（<c>constraint_set0..5_flag</c> + 2 位保留）。</param>
/// <param name="LevelIdc">例：<c>0x32</c> = level 5.0（值是 10 倍，即 <c>50</c>）。</param>
internal readonly record struct AvcSpsInfo(byte ProfileIdc, byte ConstraintFlags, byte LevelIdc)
{
    /// <summary>至少要这么多字节才读得出上面三个值（含 1 字节 NAL 头）。</summary>
    internal const int MinNalLength = 4;

    /// <summary>从一个 <b>SPS NAL</b>（不含起始码）里读出来；长度不够或类型不对时返回 <c>false</c>。</summary>
    internal static bool TryParse(ReadOnlySpan<byte> spsNal, out AvcSpsInfo info)
    {
        info = default;

        if (spsNal.Length < MinNalLength) return false;
        if (NalUnitTypes.Read(VideoCodec.H264, spsNal) != NalUnitTypes.H264Sps) return false;

        info = new AvcSpsInfo(spsNal[1], spsNal[2], spsNal[3]);
        return true;
    }

    /// <summary>
    /// <b>RFC 6381 §3.4 / ISO/IEC 14496-15 §5.4.1</b> 的 6 位十六进制串（本文件只负责这 6 位）。
    /// </summary>
    /// <param name="sampleEntry">
    /// <c>avc1</c>（参数集在 <c>description</c> 里）或 <c>avc3</c>（参数集在码流里）。
    /// ⚠️ 写错前缀的后果不是报错：WebCodecs 只把它当"哪一种 AVC"，
    /// 真正决定码流形态的是 <b>description 在不在</b>。
    /// </param>
    internal string ToCodecString(string sampleEntry) => $"{sampleEntry}.{ProfileIdc:X2}{ConstraintFlags:X2}{LevelIdc:X2}";

    /// <summary>给人看的一句话（进日志与页面）。</summary>
    internal string Describe()
        => $"{ProfileName(ProfileIdc)}（profile_idc 0x{ProfileIdc:X2}）"
            + $"，constraint 0x{ConstraintFlags:X2}"
            + $"，Level {LevelIdc / 10}.{LevelIdc % 10}（level_idc {LevelIdc}）";

    private static string ProfileName(byte profileIdc) => profileIdc switch
    {
        66 => "Baseline",
        77 => "Main",
        88 => "Extended",
        100 => "High",
        110 => "High 10",
        122 => "High 4:2:2",
        244 => "High 4:4:4 Predictive",
        _ => "未知 profile",
    };
}

/// <summary>
/// <b>AVCDecoderConfigurationRecord</b>（avcC，ISO/IEC 14496-15 §5.3.3.1）。
///
/// <para>
/// 它就是 WebCodecs <c>VideoDecoderConfig.description</c> 要的东西。
/// 形状是固定的：6 字节头 + N 个（2 字节长度 + SPS）+ PPS 计数 + M 个（2 字节长度 + PPS）。
/// </para>
///
/// <para>
/// ⚠️ <b>刻意不写"高档次扩展块"</b>（<c>chroma_format</c> / <c>bit_depth_luma_minus8</c> /
/// <c>bit_depth_chroma_minus8</c> / <c>numOfSequenceParameterSetExt</c>）：
/// 那 4 个字段在 <c>profile_idc ∈ {100,110,122,144}</c> 时<b>可以</b>出现，但它们是
/// <b>可选</b>的，而写它们需要把 SPS 一路解到 <c>seq_scaling_matrix</c> 之后 ——
/// 为了一段没人读的可选字节去实现一整套 SPS 解析，代价与风险都不划算。
/// 解码器（含 ffmpeg）对"没有这段"是容忍的；<b>真正要的 profile/level 已经在头 4 个字节里</b>。
/// </para>
/// </summary>
internal static class AvcDecoderConfiguration
{
    /// <summary><c>configurationVersion</c>，规格钉死为 1。</summary>
    internal const byte ConfigurationVersion = 1;

    /// <summary>长度前缀用 4 字节（值 3 = 长度字节数减一）。与 <see cref="AnnexB"/> 的写法必须一致。</summary>
    internal const byte LengthSizeMinusOne = 3;

    private const int MaxSpsCount = 31;          // numOfSequenceParameterSets 是 5 位
    private const int MaxPpsCount = 255;         // numOfPictureParameterSets 是 8 位
    private const int MaxParameterSetLength = 0xFFFF;   // 每段的长度字段是 16 位

    /// <summary>
    /// 用真实的 SPS/PPS NAL 拼一份 avcC。
    /// <para>
    /// 缺 SPS 或缺 PPS <b>返回 <c>null</c> 而不是拼一份半成品</b>：一份没有参数集的 avcC
    /// 会让解码器在建的时候就失败，而现场看到的是"配置不支持"，指向完全错误的方向。
    /// </para>
    /// </summary>
    internal static byte[]? Build(
        IReadOnlyList<ReadOnlyMemory<byte>> spsUnits,
        IReadOnlyList<ReadOnlyMemory<byte>> ppsUnits)
    {
        if (spsUnits.Count is 0 || spsUnits.Count > MaxSpsCount) return null;
        if (ppsUnits.Count is 0 || ppsUnits.Count > MaxPpsCount) return null;

        if (!AvcSpsInfo.TryParse(spsUnits[0].Span, out var spsInfo)) return null;

        foreach (var unit in spsUnits)
        {
            if (unit.Length is 0 or > MaxParameterSetLength) return null;
        }

        foreach (var unit in ppsUnits)
        {
            if (unit.Length is 0 or > MaxParameterSetLength) return null;
            if (NalUnitTypes.Read(VideoCodec.H264, unit.Span) != NalUnitTypes.H264Pps) return null;
        }

        var total = 6;
        foreach (var unit in spsUnits) total += 2 + unit.Length;
        total += 1;
        foreach (var unit in ppsUnits) total += 2 + unit.Length;

        var record = new byte[total];
        var cursor = 0;

        record[cursor++] = ConfigurationVersion;
        record[cursor++] = spsInfo.ProfileIdc;
        record[cursor++] = spsInfo.ConstraintFlags;
        record[cursor++] = spsInfo.LevelIdc;
        record[cursor++] = 0xFC | LengthSizeMinusOne;
        record[cursor++] = (byte)(0xE0 | spsUnits.Count);

        foreach (var unit in spsUnits) cursor = WriteUnit(record, cursor, unit.Span);

        record[cursor++] = (byte)ppsUnits.Count;

        foreach (var unit in ppsUnits) cursor = WriteUnit(record, cursor, unit.Span);

        return record;
    }

    private static int WriteUnit(byte[] destination, int cursor, ReadOnlySpan<byte> unit)
    {
        destination[cursor++] = (byte)(unit.Length >> 8);
        destination[cursor++] = (byte)unit.Length;
        unit.CopyTo(destination.AsSpan(cursor));

        return cursor + unit.Length;
    }
}
