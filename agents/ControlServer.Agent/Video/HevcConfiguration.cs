using System.Text;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent.Video;

/// <summary>
/// 从 H.265 <b>SPS</b> 里读出来的那些"要给 WebCodecs / hvcC 用"的字段。
///
/// <para>
/// 🔥 <b>与 H.264 那份是两件完全不同的事</b>：H.264 的 codec 串只要抄 SPS 的前 3 个字节，
/// 而 H.265 的 <c>profile_tier_level()</c> <b>不是从载荷第 1 字节开始的</b> ——
/// 前面还有 <c>sps_video_parameter_set_id</c>(4) / <c>sps_max_sub_layers_minus1</c>(3) /
/// <c>sps_temporal_id_nesting_flag</c>(1) 这 8 位，而且后面还跟着<b>可变长</b>的子层信息。
/// 所以这里必须真的用位读取器解析，不能"抄几个字节"。
/// </para>
/// </summary>
/// <param name="ProfileSpace">0–3，对应 codec 串里 <c>""</c>/<c>A</c>/<c>B</c>/<c>C</c>。</param>
/// <param name="ProfileCompatibilityFlags">
/// <b>位流顺序</b>的 32 位（最高位 = <c>general_profile_compatibility_flag[0]</c>）。
/// ⚠️ 这个顺序是<b>直接放进 hvcC</b> 用的；codec 串要的是它的<b>位序反转</b>（见 <see cref="ToCodecString"/>）。
/// 两种顺序都要，所以存位流顺序这一种，反转只发生在拼串的那一刻。
/// </param>
/// <param name="ConstraintFlags">低 48 位有效，位流顺序（第 1 字节含 <c>general_progressive_source_flag</c>）。</param>
internal readonly record struct HevcSpsInfo(
    byte ProfileSpace,
    bool TierFlag,
    byte ProfileIdc,
    uint ProfileCompatibilityFlags,
    ulong ConstraintFlags,
    byte LevelIdc,
    byte ChromaFormatIdc,
    byte BitDepthLumaMinus8,
    byte BitDepthChromaMinus8,
    byte MaxSubLayersMinus1,
    bool TemporalIdNesting)
{
    /// <summary>H.265 的 NAL 头是 2 字节，所以能解出东西的最小长度是 2 + 1。</summary>
    internal const int MinNalLength = 3;

    /// <summary>
    /// 从一个 <b>SPS NAL</b>（不含起始码）里解出来。
    /// <para>
    /// 任何一步读不到底、或者读出来的值超出语法允许的范围，都返回 <c>false</c> ——
    /// 与能力探测同一条纪律：<b>不确定就说不知道，不许用 0 顶上</b>。
    /// </para>
    /// </summary>
    internal static bool TryParse(ReadOnlySpan<byte> spsNal, out HevcSpsInfo info)
    {
        info = default;

        if (spsNal.Length < MinNalLength) return false;
        if (NalUnitTypes.Read(VideoCodec.Hevc, spsNal) != NalUnitTypes.H265Sps) return false;

        var rbsp = new RbspBitReader(spsNal[2..]);

        // sps_video_parameter_set_id(4) + sps_max_sub_layers_minus1(3) + sps_temporal_id_nesting_flag(1)
        if (!rbsp.TryReadBits(4, out _)) return false;
        if (!rbsp.TryReadBits(3, out var maxSubLayersMinus1)) return false;
        if (!rbsp.TryReadFlag(out var temporalIdNesting)) return false;

        // profile_tier_level(profilePresentFlag = 1, maxNumSubLayersMinus1)
        if (!rbsp.TryReadBits(2, out var profileSpace)) return false;
        if (!rbsp.TryReadFlag(out var tierFlag)) return false;
        if (!rbsp.TryReadBits(5, out var profileIdc)) return false;
        if (!rbsp.TryReadBits(32, out var compatibilityFlags)) return false;
        if (!rbsp.TryReadBits(32, out var constraintHigh)) return false;
        if (!rbsp.TryReadBits(16, out var constraintLow)) return false;
        if (!rbsp.TryReadBits(8, out var levelIdc)) return false;

        var constraintFlags = ((ulong)constraintHigh << 16) | constraintLow;

        Span<bool> subLayerProfilePresent = stackalloc bool[7];
        Span<bool> subLayerLevelPresent = stackalloc bool[7];

        for (var i = 0; i < maxSubLayersMinus1 && i < 7; i++)
        {
            if (!rbsp.TryReadFlag(out var profilePresent)) return false;
            if (!rbsp.TryReadFlag(out var levelPresent)) return false;

            subLayerProfilePresent[i] = profilePresent;
            subLayerLevelPresent[i] = levelPresent;
        }

        // maxNumSubLayersMinus1 个 2 位标志之后补足到 8 个，凑成整字节。
        if (maxSubLayersMinus1 > 0)
        {
            for (var i = (int)maxSubLayersMinus1; i < 8; i++)
            {
                if (!rbsp.TryReadBits(2, out _)) return false;
            }
        }

        for (var i = 0; i < maxSubLayersMinus1 && i < 7; i++)
        {
            if (subLayerProfilePresent[i] && !rbsp.TrySkipBits(88)) return false;
            if (subLayerLevelPresent[i] && !rbsp.TryReadBits(8, out _)) return false;
        }

        // 到这儿才是我们真正要的色度/位深 —— 它们藏在变长的 profile_tier_level 之后，
        // 所以"按固定偏移取字节"在这里是不成立的。
        if (!rbsp.TryReadUe(out _)) return false;                        // sps_seq_parameter_set_id
        if (!rbsp.TryReadUe(out var chromaFormatIdc)) return false;
        if (chromaFormatIdc == 3 && !rbsp.TryReadFlag(out _)) return false;   // separate_colour_plane_flag
        if (!rbsp.TryReadUe(out _)) return false;                        // pic_width_in_luma_samples
        if (!rbsp.TryReadUe(out _)) return false;                        // pic_height_in_luma_samples

        if (!rbsp.TryReadFlag(out var conformanceWindow)) return false;

        if (conformanceWindow)
        {
            for (var i = 0; i < 4; i++)
            {
                if (!rbsp.TryReadUe(out _)) return false;
            }
        }

        if (!rbsp.TryReadUe(out var bitDepthLumaMinus8)) return false;
        if (!rbsp.TryReadUe(out var bitDepthChromaMinus8)) return false;

        // 语法允许的范围。超出就是这个 SPS 我们理解错了 —— 不许蒙着往下走。
        if (chromaFormatIdc > 3) return false;
        if (bitDepthLumaMinus8 > 8 || bitDepthChromaMinus8 > 8) return false;
        if (profileSpace > 3) return false;

        info = new HevcSpsInfo(
            (byte)profileSpace,
            tierFlag,
            (byte)profileIdc,
            compatibilityFlags,
            constraintFlags,
            (byte)levelIdc,
            (byte)chromaFormatIdc,
            (byte)bitDepthLumaMinus8,
            (byte)bitDepthChromaMinus8,
            (byte)maxSubLayersMinus1,
            temporalIdNesting);

        return true;
    }

    /// <summary>
    /// <b>ISO/IEC 14496-15 §E.3</b>（经 3GPP TS 26.244 §A.2.2 / MDN 转述）的 codec 串。
    ///
    /// <para>
    /// 🔴 <b>兼容性标志必须"位序反转"</b>：规格原文是"the 32 bits of the
    /// <c>general_profile_compatibility_flags</c>, but <b>in reverse bit order</b>，
    /// 以 <c>flag[31]</c> 为最高位、<c>flag[0]</c> 为最低位，以十六进制编码"。
    /// 于是 Main profile（位流里 <c>flag[1]</c> 与 <c>flag[2]</c> 为 1，即 <c>0x60000000</c>）
    /// 反出来是 <c>0x6</c>，写成 <b><c>hvc1.1.6.L93.B0</c></b> 而不是 <c>…1.2…</c>。
    /// （TS 26.244 里专门有一句解释：第二个点后面的值是 6 不是 2，是因为
    /// Main profile 的码流按 ISO/IEC 23008-2 也应当标成与 Main 10 兼容。）
    /// 手工按"直读"写这个串，得到的是一串<b>语法合法但浏览器不认</b>的值。
    /// </para>
    ///
    /// <para>
    /// 约束标志那 6 个字节<b>不反转</b>，按字节序逐个写成十六进制、点分隔，
    /// 末尾的零字节可省但<b>至少留一个</b>（全零要写成 <c>.00</c>，不是省略）。
    /// </para>
    /// </summary>
    internal string ToCodecString(string sampleEntry)
    {
        var builder = new StringBuilder(sampleEntry);

        builder.Append('.').Append(ProfileSpace switch { 0 => string.Empty, 1 => "A", 2 => "B", _ => "C" });
        builder.Append(ProfileIdc);
        builder.Append('.').Append(ReverseBits(ProfileCompatibilityFlags).ToString("X"));
        builder.Append('.').Append(TierFlag ? 'H' : 'L').Append(LevelIdc);

        Span<byte> constraint = stackalloc byte[6];
        for (var i = 0; i < 6; i++) constraint[i] = (byte)(ConstraintFlags >> (8 * (5 - i)));

        var last = 5;
        while (last > 0 && constraint[last] == 0) last--;

        for (var i = 0; i <= last; i++) builder.Append('.').Append(constraint[i].ToString("X2"));

        return builder.ToString();
    }

    /// <summary>给人看的一句话（进日志与页面）。</summary>
    internal string Describe()
        => $"profile_space {ProfileSpace} / profile_idc {ProfileIdc}"
            + $"，{(TierFlag ? "High" : "Main")} tier，Level {LevelIdc / 30.0:F1}（level_idc {LevelIdc}）"
            + $"，{ChromaName(ChromaFormatIdc)}，{8 + BitDepthLumaMinus8} bit"
            + $"，理论层数 {MaxSubLayersMinus1 + 1}";

    private static string ChromaName(byte chromaFormatIdc) => chromaFormatIdc switch
    {
        0 => "单色",
        1 => "4:2:0",
        2 => "4:2:2",
        3 => "4:4:4",
        _ => "未知色度格式",
    };

    private static uint ReverseBits(uint value)
    {
        var result = 0u;

        for (var i = 0; i < 32; i++)
        {
            result = (result << 1) | (value & 1u);
            value >>= 1;
        }

        return result;
    }
}

/// <summary>
/// <b>HEVCDecoderConfigurationRecord</b>（hvcC，ISO/IEC 14496-15 §8.3.3.1）。
///
/// <para>
/// 固定 23 字节头 + 若干"参数集数组"。固定头的字段里有一半是
/// <c>reserved = '111…'</c> 的填充位 —— <b>它们不是可有可无的</b>：
/// 少写一位后面所有字段整体错位，而解码器只会说"配置非法"。
/// </para>
/// </summary>
internal static class HvcDecoderConfiguration
{
    internal const byte ConfigurationVersion = 1;
    internal const byte LengthSizeMinusOne = 3;

    private const int HeaderLength = 23;
    private const int MaxParameterSetLength = 0xFFFF;
    private const int MaxArrayCount = 255;

    /// <summary>
    /// 用真实的 VPS/SPS/PPS NAL 拼一份 hvcC。
    /// <para>缺 SPS 或缺 PPS 返回 <c>null</c>（理由同 avcC：半成品只会把排查带偏）。</para>
    /// </summary>
    /// <param name="vpsUnits">可以没有 —— VPS 在 hvcC 的数组里是可选的。</param>
    internal static byte[]? Build(
        HevcSpsInfo spsInfo,
        IReadOnlyList<ReadOnlyMemory<byte>> vpsUnits,
        IReadOnlyList<ReadOnlyMemory<byte>> spsUnits,
        IReadOnlyList<ReadOnlyMemory<byte>> ppsUnits)
    {
        if (spsUnits.Count == 0 || ppsUnits.Count == 0) return null;

        var arrays = (vpsUnits.Count > 0 ? 1 : 0) + 1 + 1;

        if (arrays > MaxArrayCount) return null;

        foreach (var unit in vpsUnits.Concat(spsUnits).Concat(ppsUnits))
        {
            if (unit.Length is 0 or > MaxParameterSetLength) return null;
        }

        var total = HeaderLength + (arrays * 3);
        foreach (var unit in vpsUnits) total += 2 + unit.Length;
        foreach (var unit in spsUnits) total += 2 + unit.Length;
        foreach (var unit in ppsUnits) total += 2 + unit.Length;

        var record = new byte[total];
        var cursor = 0;

        record[cursor++] = ConfigurationVersion;
        record[cursor++] = (byte)((spsInfo.ProfileSpace << 6) | (spsInfo.TierFlag ? 0x20 : 0) | spsInfo.ProfileIdc);

        for (var i = 3; i >= 0; i--) record[cursor++] = (byte)(spsInfo.ProfileCompatibilityFlags >> (8 * i));
        for (var i = 5; i >= 0; i--) record[cursor++] = (byte)(spsInfo.ConstraintFlags >> (8 * i));

        record[cursor++] = spsInfo.LevelIdc;

        // min_spatial_segmentation_idc：我们不做空间分割 ⇒ 0（reserved '1111' + 12 位）。
        record[cursor++] = 0xF0;
        record[cursor++] = 0x00;

        // parallelismType：0 = 未知/未指定（reserved '111111' + 2 位）。
        record[cursor++] = 0xFC;

        record[cursor++] = (byte)(0xFC | spsInfo.ChromaFormatIdc);
        record[cursor++] = (byte)(0xF8 | spsInfo.BitDepthLumaMinus8);
        record[cursor++] = (byte)(0xF8 | spsInfo.BitDepthChromaMinus8);

        // avgFrameRate：0 = 未指定。**不许**拿会话的 fps 来填 —— 那是"我们打算发多少"，
        // 而这个字段说的是"码流里真的是多少"，两者在降帧时就分叉了。
        record[cursor++] = 0x00;
        record[cursor++] = 0x00;

        var temporalLayers = Math.Min(spsInfo.MaxSubLayersMinus1 + 1, 7);

        record[cursor++] = (byte)((0 << 6) | (temporalLayers << 3) | (spsInfo.TemporalIdNesting ? 0x04 : 0) | LengthSizeMinusOne);
        record[cursor++] = (byte)arrays;

        cursor = WriteArray(record, cursor, NalUnitTypes.H265Vps, vpsUnits);
        cursor = WriteArray(record, cursor, NalUnitTypes.H265Sps, spsUnits);
        cursor = WriteArray(record, cursor, NalUnitTypes.H265Pps, ppsUnits);

        return record;
    }

    private static int WriteArray(byte[] destination, int cursor, int nalUnitType, IReadOnlyList<ReadOnlyMemory<byte>> units)
    {
        if (units.Count == 0) return cursor;

        // array_completeness = 1（这一类参数集全都在这条记录里），reserved=0，NAL_unit_type。
        destination[cursor++] = (byte)(0x80 | nalUnitType);
        destination[cursor++] = (byte)(units.Count >> 8);
        destination[cursor++] = (byte)units.Count;

        foreach (var unit in units)
        {
            destination[cursor++] = (byte)(unit.Length >> 8);
            destination[cursor++] = (byte)unit.Length;
            unit.Span.CopyTo(destination.AsSpan(cursor));
            cursor += unit.Length;
        }

        return cursor;
    }
}
