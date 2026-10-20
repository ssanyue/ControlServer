using System.Text;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent.Video;

/// <summary>
/// 编解码器在 <c>codecs</c> 串 / WebCodecs 里的<b>四字符采样入口名</b>。
/// <para>
/// ⚠️ 它<b>不是</b> <c>RemoteCodecs.H264</c>（管道上那个 <c>"h264"</c>）：
/// 那个是"我们这条链路用什么编码"，这个是"浏览器怎么称呼它"。
/// 两者都要存在，而且<b>谁都不能拿对方顶替</b> —— 混用会让管道上出现一个没人认得的名字。
/// </para>
/// </summary>
internal static class VideoSampleEntries
{
    /// <summary>H.264，参数集在 <c>description</c>（avcC）里。</summary>
    internal const string Avc1 = "avc1";

    /// <summary>H.264，参数集在码流里。</summary>
    internal const string Avc3 = "avc3";

    /// <summary>H.265，参数集在 <c>description</c>（hvcC）里。</summary>
    internal const string Hvc1 = "hvc1";

    /// <summary>H.265，参数集在码流里。</summary>
    internal const string Hev1 = "hev1";

    /// <summary>
    /// 把"带外"的采样入口名换成"带内"的那一个（<c>avc1→avc3</c> / <c>hvc1→hev1</c>）。
    ///
    /// <para>
    /// 🔥 <b>为什么用"换前缀"而不是"再解一遍 SPS"</b>：<c>codec</c> 串的形状是
    /// <c>采样入口名.ProfileIdc.ConstraintFlags.LevelIdc</c>，<b>点号后面那三段与打包方式无关</b>
    /// —— 它是从 SPS 里读出来的 profile/level，同一个 SPS 无论怎么打包都读出同一组值。
    /// 再解一遍只会多出一次"两次结果看起来一样、实际不一样"的机会，
    /// 而那种不一致的表现是浏览器 <c>configure()</c> 失败或花屏，<b>不报错</b>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 认不得的名字<b>原样返回</b>：这里不该"猜一个" —— 猜错就是把一个陌生的编码
    /// 硬说成 avc3，而调用方会拿它去 configure。
    /// </para>
    /// </summary>
    internal static string? ToInBand(string? sampleEntry)
    {
        if (sampleEntry is null) return null;

        // ⚠️ 用 `StartsWith(..., Ordinal)` + 手工拼，而不是 `[4..]`：
        //    后者在一个短于 4 个字符的串上会抛 —— 而这里**绝不抛**（见调用点）。
        if (sampleEntry.StartsWith(Avc1, StringComparison.Ordinal))
        {
            return Avc3 + sampleEntry[Avc1.Length..];
        }

        if (sampleEntry.StartsWith(Hvc1, StringComparison.Ordinal))
        {
            return Hev1 + sampleEntry[Hvc1.Length..];
        }

        return sampleEntry;
    }
}

/// <summary>
/// 管道上的编码名（<see cref="StreamCodecs"/> 的取值）↔ <see cref="VideoCodec"/> 枚举。
///
/// <para>
/// 🔥 <b>为什么需要这个映射，而不是让上层拿字符串比</b>：参数集的解析是<b>按格式分支</b>的
/// （H.264 找 SPS/PPS、H.265 找 VPS/SPS/PPS），拿错分支去解不会报错，
/// 只会"一个参数集都找不到"—— 而那正是我们用来判断"带内这条路成不成立"的信号。
/// 把判断做错，就会把一个格式问题读成一个打包问题。
/// </para>
/// </summary>
internal static class StreamCodecMap
{
    /// <summary>能不能拿 <paramref name="streamCodecName"/> 去解 H.264/HEVC 的访问单元。</summary>
    internal static bool TryParse(string? streamCodecName, out VideoCodec codec)
    {
        if (string.Equals(streamCodecName, StreamCodecs.H264, StringComparison.OrdinalIgnoreCase))
        {
            codec = VideoCodec.H264;

            return true;
        }

        if (string.Equals(streamCodecName, StreamCodecs.Hevc, StringComparison.OrdinalIgnoreCase))
        {
            codec = VideoCodec.Hevc;

            return true;
        }

        // ⚠️ MJPEG 走这里：它逐帧独立、没有参数集，**不是**"还没支持"。
        codec = default;

        return false;
    }
}

/// <summary>
/// 从一段 Annex-B 码流里抽出来的<b>"喂给 WebCodecs 之前必须知道的那几件事"</b>。
/// </summary>
/// <param name="CodecString">
/// 传给 <c>VideoDecoder.isConfigSupported({ codec })</c> 的串，例如 <c>avc1.640032</c>。
/// <b>它是从真实 SPS 反推的，不是写死的</b>：写错的表现是 <c>NotSupportedError</c>，
/// 看起来像"浏览器不支持 H.264"。
/// </param>
/// <param name="Description">
/// avcC / hvcC 字节。传进 <c>configure({ description })</c> 之后，
/// <b>码流就必须换成 <see cref="AnnexB.WriteCanonicalAccessUnit"/> 那种长度前缀形式</b>。
/// </param>
/// <param name="ParameterSetNalCount">这次一共扫到几个参数集 NAL（进日志，用于解释"为什么抽不出来"）。</param>
/// <param name="Detail">给人看的一句话。</param>
internal sealed record VideoParameterSets(
    VideoCodec Codec,
    string CodecString,
    byte[] Description,
    int ParameterSetNalCount,
    string Detail);

/// <summary>
/// 从 Annex-B 访问单元里抽出参数集，并给出 <b>codec 串 + avcC/hvcC</b>。
///
/// <para>
/// 🔥 <b>它是「参数集打包」这条路上的全部纯逻辑</b>，两条路都要用它 ——
/// 不只是带外那条：
/// </para>
/// <list type="bullet">
///   <item><b>带内</b>（<c>avc3</c>/<c>hev1</c>，<b>本批的默认</b>）：只用它解出的
///         <see cref="VideoParameterSets.CodecString"/>（帧头里那份 codec 串必须
///         <b>从真实 SPS 反推</b>），码流<b>一个字节都不动</b>；</item>
///   <item><b>带外</b>（<c>avc1</c>/<c>hvc1</c>）：额外用它的
///         <see cref="VideoParameterSets.Description"/>，并把码流换成
///         <see cref="AnnexB.WriteCanonicalAccessUnit"/> 那种长度前缀形式。</item>
/// </list>
///
/// <para>
/// 🔴 <b>这里原来写着"带外是本批选择的路线"，那句已经作废</b>（10-16 拍板：默认带内、
/// 带外做成开关）。当时的理由是"MF 编出来的码流并不是每个关键帧都带 SPS
/// —— 落盘的 30 帧里 SPS 只出现 1 次"。
/// </para>
///
/// <para>
/// 🔴🔴 <b>而那个读数是从一个只有一个关键帧的样本里读出来的，证明不了任何事</b>：
/// 那批落盘是 <b>29 个 P 帧 + 1 个 I 帧</b>（实测，见 <c>.tools/m5.4b-measure.py</c>），
/// 整段里<b>只有一个关键帧</b> ⇒ "SPS 只出现 1 次"与"SPS 每个关键帧都出现"
/// <b>是同一件事</b>。
/// </para>
///
/// <para>
/// ✅ <b>这件事已经实测结掉，而且是"每个关键帧都带"</b>：
/// </para>
/// <list type="bullet">
///   <item>§3.4.2（`DxgiHardwareEncodeSmokeTests`，强制出第二个关键帧）：
///         第 0 与第 15 帧的 NAL 序列都是 <c>9,7,8,5</c>（AUD/SPS/PPS/IDR）—— 两个样本；</item>
///   <item>本轮再加一档更硬的（`WindowsGpuFrameEncoderSmokeTests`）：把 <c>gopSize</c>
///         压到 4、跑 80 轮 ⇒ <b>32 帧里 8 个关键帧，8/8 自带 SPS</b>。</item>
/// </list>
/// <para>
/// ⇒ 带内那条路（本批默认）在这个编码器上<b>成立</b>。但结论只限于"这台机器 + Intel QSV"：
/// 它是<b>编码器的行为</b>，不是规格的保证。所以推流宿主仍然在每个关键帧上数一次
/// （<c>RemoteStreamHost</c> 的 <c>_keyframesWith/WithoutParameterSets</c>），
/// 换机型时"带内还成不成立"会自己说出来。
/// </para>
/// </summary>
internal static class VideoParameterSetReader
{
    /// <summary>
    /// 扫一遍 <paramref name="annexB"/>，把参数集凑齐并建出 description。
    /// <para>凑不齐（缺 SPS / 缺 PPS / SPS 解不动）返回 <c>null</c>。</para>
    /// </summary>
    /// <param name="sampleEntry">
    /// 用 <see cref="VideoSampleEntries.Avc1"/> / <see cref="VideoSampleEntries.Hvc1"/>。
    ///
    /// <para>
    /// ⚠️ <b>无论最终走带内还是带外，都传这两个</b>：解出来的 avcC 与 profile/level 串
    /// <b>与打包方式无关</b>（同一份 SPS 读出的就是同一组值），带内那一份只是把前缀换成
    /// <c>avc3</c>/<c>hev1</c>（见 <see cref="VideoSampleEntries.ToInBand"/>）。
    /// 按模式分开解两次，等于给自己留一个"两次结果看起来一样、实际不一样"的机会 ——
    /// 而那种不一致的表现是浏览器 configure 失败或花屏，<b>不报错</b>。
    /// </para>
    /// </param>
    internal static VideoParameterSets? TryRead(ReadOnlySpan<byte> annexB, VideoCodec codec, string sampleEntry)
    {
        var first = new List<ReadOnlyMemory<byte>>();
        var second = new List<ReadOnlyMemory<byte>>();
        var third = new List<ReadOnlyMemory<byte>>();

        var count = 0;
        var reader = new AnnexB.Enumerator(annexB);

        while (reader.MoveNext())
        {
            var nal = reader.Current;
            var type = NalUnitTypes.Read(codec, nal);

            if (!NalUnitTypes.IsParameterSet(codec, type)) continue;

            count++;

            var copy = nal.ToArray();

            switch (codec)
            {
                case VideoCodec.H264 when type == NalUnitTypes.H264Sps:
                    AddUnique(first, copy);
                    break;
                case VideoCodec.H264 when type == NalUnitTypes.H264Pps:
                    AddUnique(second, copy);
                    break;
                case VideoCodec.Hevc when type == NalUnitTypes.H265Vps:
                    AddUnique(first, copy);
                    break;
                case VideoCodec.Hevc when type == NalUnitTypes.H265Sps:
                    AddUnique(second, copy);
                    break;
                case VideoCodec.Hevc when type == NalUnitTypes.H265Pps:
                    AddUnique(third, copy);
                    break;
            }
        }

        return codec switch
        {
            VideoCodec.H264 => BuildH264(first, second, count, sampleEntry),
            VideoCodec.Hevc => BuildHevc(first, second, third, count, sampleEntry),
            _ => null,
        };
    }

    private static VideoParameterSets? BuildH264(
        List<ReadOnlyMemory<byte>> spsUnits,
        List<ReadOnlyMemory<byte>> ppsUnits,
        int count,
        string sampleEntry)
    {
        var description = AvcDecoderConfiguration.Build(spsUnits, ppsUnits);

        if (description is null) return null;

        if (!AvcSpsInfo.TryParse(spsUnits[0].Span, out var spsInfo)) return null;

        return new VideoParameterSets(
            VideoCodec.H264,
            spsInfo.ToCodecString(sampleEntry),
            description,
            count,
            $"{spsInfo.Describe()}，SPS {spsUnits[0].Length} B / PPS {ppsUnits[0].Length} B，"
                + $"avcC {description.Length} B");
    }

    private static VideoParameterSets? BuildHevc(
        List<ReadOnlyMemory<byte>> vpsUnits,
        List<ReadOnlyMemory<byte>> spsUnits,
        List<ReadOnlyMemory<byte>> ppsUnits,
        int count,
        string sampleEntry)
    {
        if (spsUnits.Count == 0) return null;

        if (!HevcSpsInfo.TryParse(spsUnits[0].Span, out var spsInfo)) return null;

        var description = HvcDecoderConfiguration.Build(spsInfo, vpsUnits, spsUnits, ppsUnits);

        if (description is null) return null;

        var detail = new StringBuilder(spsInfo.Describe());

        detail.Append('，');
        detail.Append($"VPS {vpsUnits.Count} / SPS {spsUnits.Count} / PPS {ppsUnits.Count} 个");
        detail.Append($"，hvcC {description.Length} B");

        return new VideoParameterSets(
            VideoCodec.Hevc,
            spsInfo.ToCodecString(sampleEntry),
            description,
            count,
            detail.ToString());
    }

    /// <summary>
    /// 同样的参数集只留一份。
    /// <para>
    /// 真机码流里 <b>PPS 是每帧都带的</b>（实测），所以不去重的话，
    /// 拿一个多帧的访问单元来扫就会得到几十份一样的 PPS —— avcC 允许多份，
    /// 但体积白涨、而且与"参数集其实只有一份"这件事不符。
    /// </para>
    /// </summary>
    private static void AddUnique(List<ReadOnlyMemory<byte>> units, ReadOnlyMemory<byte> unit)
    {
        foreach (var existing in units)
        {
            if (existing.Span.SequenceEqual(unit.Span)) return;
        }

        units.Add(unit);
    }
}
