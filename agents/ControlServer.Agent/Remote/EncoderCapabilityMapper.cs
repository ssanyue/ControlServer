using ControlServer.Contracts;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent.Remote;

/// <summary>
/// 把平台层的探测结论（<see cref="EncoderCapabilityReport"/>，用<b>枚举</b>表达编码格式）
/// 映射成上行载荷（<see cref="AgentEncoderCapabilityDto"/>，用<b>字符串</b>表达）（M5.0 第二批）。
///
/// <para>
/// 🔥 <b>为什么要多这一层、而不是直接上报平台类型</b>：
/// <list type="bullet">
///   <item>平台层的 <c>VideoCodec</c> 是<b>枚举</b>，而线上表示必须是字符串 ——
///         枚举序列化成数字之后，<b>加一个成员就会让旧中心把新格式读成另一个值</b>
///         （<c>0</c>/<c>1</c> 一旦写进过任何持久化或日志就改不动了）。
///         字符串对不上时至少是"读不懂"，而不是"读错"。</item>
///   <item><c>Contracts</c> <b>不能引用</b> <c>Platform.Abstractions</c>（铁律 1：分层）。
///         这个映射只能住在同时看得到两边的程序集里 —— 客户机正是这样一个位置。</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ 映射是<b>纯函数</b>，刻意做成可以单独被测的形式：它是"平台结论"到"对外承诺"的
/// 唯一一道口子，一旦它把 <c>null</c> 与 <c>0</c> 混掉，能力探测在整条 8K 路线上就白做了。
/// </para>
///
/// <para>
/// ⚠️ <b>M5.4-b 第 7 步起它是 <c>public</c></b>：托盘（另一个程序集）要用
/// <see cref="TryParseWireCodec"/> 把中心回下来的格式名翻成平台枚举。
/// 让托盘自己写一份"名字 → 枚举"的 switch 就等于把这对映射拆成两半 ——
/// 而它们漂掉的表现是"本机报了一个自己都不认得的格式名"。
/// </para>
/// </summary>
public static class EncoderCapabilityMapper
{
    /// <summary>
    /// 反向映射：<b>线上格式名 → 平台枚举</b>（M5.4-b 第 7 步）。
    ///
    /// <para>
    /// 🔥 <b>为什么反向映射必须与正向映射住在同一个类里</b>：它们是一对 ——
    /// 正向决定"我们对外报的名字"，反向决定"我们认得哪些名字"。
    /// 分开写必然漂，而漂掉的表现是<b>本机报了一个自己都不认得的格式名</b>
    /// （中心把 <c>SupportedCodecs</c> 原样回下来，本机却解不开它）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <c>false</c> 的意思是<b>"这个格式名我不认得"</b>，不是"这台机器不支持"：
    /// 它可能来自一个更新的中心（新格式），也可能是报文写错了。
    /// 调用方要把这两种区分开 —— 当成"不支持"是一个凭空的否定结论。
    /// </para>
    /// </summary>
    /// <param name="wireCodec"><see cref="RemoteCodecs"/> 的取值。</param>
    /// <param name="codec">解析出来的平台枚举。</param>
    public static bool TryParseWireCodec(string? wireCodec, out VideoCodec codec)
    {
        if (string.Equals(wireCodec, RemoteCodecs.H264, StringComparison.OrdinalIgnoreCase))
        {
            codec = VideoCodec.H264;

            return true;
        }

        if (string.Equals(wireCodec, RemoteCodecs.Hevc, StringComparison.OrdinalIgnoreCase))
        {
            codec = VideoCodec.Hevc;

            return true;
        }

        // ⚠️ `mjpeg` 也走这里：它**不是** `VideoCodec` 的成员（见 RemoteCodecs.Mjpeg 的注释），
        //    因为"我们临时用 JPEG 联调"与"这台机器支持 H.264"不是同一类事实。
        codec = default;

        return false;
    }

    /// <summary>把一次探测结论打成上行载荷。</summary>
    /// <param name="probedAt">
    /// 探测发生的时刻（<b>由调用方给</b>，不是在这里取当前时间）——
    /// 这样页面上的"探测于"是采集时刻，而不是"打包那一刻"。
    /// </param>
    public static AgentEncoderCapabilityDto ToDto(EncoderCapabilityReport report, DateTimeOffset probedAt)
        => new(
            Adapters: report.Adapters.Select(ToAdapterDto).ToList(),
            SupportedCodecs: report.SupportedCodecs.Select(ToWireCodec).ToList(),
            RecommendedCodec: report.RecommendedCodec is { } codec ? ToWireCodec(codec) : null,
            Summary: report.Summary,
            ProbedAt: probedAt);

    private static RemoteAdapterDto ToAdapterDto(EncoderAdapterCapability adapter)
        => new(
            adapter.Name,
            adapter.VendorId,
            adapter.DeviceId,
            adapter.DedicatedVideoMemoryBytes,
            adapter.IsSoftwareRenderer,
            adapter.SupportedCodecs.Select(ToWireCodec).ToList(),

            // ⚠️ 原样透传：`null`（未知）与 `0`（明确无硬编）在这里<b>绝不能</b>被归一化，
            //    那正是整个能力探测要保住的那条区分。
            adapter.EncodeEngines,
            adapter.EngineBasis);

    /// <summary>编码格式的线上表示（见 <see cref="RemoteCodecs"/>）。</summary>
    private static string ToWireCodec(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => RemoteCodecs.H264,
        VideoCodec.Hevc => RemoteCodecs.Hevc,

        // 未来新增格式时的退化路径：给出**小写枚举名**，它一定不等于任何已知常量，
        // 于是中心那边是"读不懂这个格式"（可发现），而不是"用错格式"（不可发现）。
        _ => codec.ToString().ToLowerInvariant(),
    };
}
