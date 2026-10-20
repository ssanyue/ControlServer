using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent.Video;

/// <summary>
/// <b>NAL 单元类型</b>（H.264 / H.265 各一套取值）。
///
/// <para>
/// 🔥 <b>两个编码的取法不一样，而且取错不会报错</b>：
/// <list type="bullet">
///   <item>H.264（7.4.1.2.1）：<c>b0 &amp; 0x1F</c> —— 低 5 位；</item>
///   <item>H.265（7.4.2.2）：<c>(b0 &gt;&gt; 1) &amp; 0x3F</c> —— 中间 6 位。</item>
/// </list>
/// 拿 H.264 的取法去读 H.265 的 SPS，会得到一个"合法但不对"的数字（33 → 1，正好是"非 IDR 片"），
/// 于是参数集被当成图像数据 —— 一路安静地错下去。
/// </para>
/// </summary>
internal static class NalUnitTypes
{
    // ── H.264（ITU-T H.264 Table 7-1） ──
    internal const int H264NonIdrSlice = 1;
    internal const int H264IdrSlice = 5;
    internal const int H264Sei = 6;
    internal const int H264Sps = 7;
    internal const int H264Pps = 8;
    internal const int H264Aud = 9;

    // ── H.265（ITU-T H.265 Table 7-1） ──
    internal const int H265IdrWRadl = 19;
    internal const int H265IdrNLp = 20;
    internal const int H265Vps = 32;
    internal const int H265Sps = 33;
    internal const int H265Pps = 34;
    internal const int H265Aud = 35;

    /// <summary>读出一个 NAL 的类型；<b>读不出来返回 <c>-1</c></b>（长度不够，不是"类型 0"）。</summary>
    internal static int Read(VideoCodec codec, ReadOnlySpan<byte> nalUnit) => codec switch
    {
        VideoCodec.H264 => nalUnit.Length >= 1 ? nalUnit[0] & 0x1F : -1,
        VideoCodec.Hevc => nalUnit.Length >= 2 ? (nalUnit[0] >> 1) & 0x3F : -1,
        _ => -1,
    };

    /// <summary>这个类型是不是<b>参数集</b>（要进 <c>description</c>、而不是进图像数据的那几个）。</summary>
    internal static bool IsParameterSet(VideoCodec codec, int nalUnitType) => codec switch
    {
        VideoCodec.H264 => nalUnitType is H264Sps or H264Pps,
        VideoCodec.Hevc => nalUnitType is H265Vps or H265Sps or H265Pps,
        _ => false,
    };

    /// <summary>
    /// 这个类型是不是<b>可以独立解码的关键帧片</b>。
    ///
    /// <para>
    /// ⚠️ H.265 只认 <b>IDR</b>（<c>IDR_W_RADL</c> / <c>IDR_N_LP</c>）。
    /// <c>CRA_NUT</c>(21) <b>刻意不算</b>：它能独立解码，但后面可以跟"前置"的 RASL 图，
    /// 语义是"开放 GOP 的随机接入点"，与 <c>type: "key"</c> 要的"从这里开始全都能解"不是一回事。
    /// 把它算进来，观看端就会在一个其实还依赖前向参考的位置上重置解码器。
    /// </para>
    /// </summary>
    internal static bool IsKeyframeSlice(VideoCodec codec, int nalUnitType) => codec switch
    {
        VideoCodec.H264 => nalUnitType == H264IdrSlice,
        VideoCodec.Hevc => nalUnitType is H265IdrWRadl or H265IdrNLp,
        _ => false,
    };
}
