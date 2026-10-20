using ControlServer.Agent.Remote;
using ControlServer.Contracts;
using ControlServer.Platform.Abstractions;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// 「平台探测结论 → 上行载荷」的映射（M5.0 第二批）。
///
/// <para>
/// 它是<b>唯一</b>一道把平台层的结论翻译成对外承诺的口子。
/// 一旦它把 <c>null</c>（还没测出来）与 <c>0</c>（明确没有硬编）混掉，
/// 能力探测这个功能在整条 8K 路线上就白做了 —— 而且<b>错得看不出来</b>：
/// 中心页面上会显示一个规规矩矩的「0」，看起来像测过。
/// </para>
/// </summary>
public sealed class EncoderCapabilityMapperTests
{
    private static readonly DateTimeOffset ProbedAt = new(2026, 10, 15, 20, 0, 0, TimeSpan.FromHours(8));

    /// <summary>
    /// 🔥 <b>本组最重要的一条</b>：<c>null</c> 不许被归一化成 <c>0</c>，反过来也一样。
    /// <para>两种失败都真实存在：把 <c>null</c> 写 <c>0</c> 会凭空否定一台机器的能力；
    /// 把 <c>0</c> 写成 <c>null</c> 会让"这台机器确实编不了"被当成"还没测"，于是任务继续排队等一个永远不会来的结论。</para>
    /// </summary>
    [Fact]
    public void An_unknown_engine_count_stays_null_and_a_known_zero_stays_zero()
    {
        var report = new EncoderCapabilityReport(
            Adapters:
            [
                Adapter("未知引擎数（还没实测）", engines: null, basis: "尚未实测"),
                Adapter("明确无硬编（MX250 那种）", engines: 0, basis: "Media Foundation 未发现该厂商的硬件编码器"),
                Adapter("双引擎", engines: 2, basis: "PDH 实测"),
            ],
            SupportedCodecs: [VideoCodec.H264],
            RecommendedCodec: null,
            Summary: "测试用。");

        var dto = EncoderCapabilityMapper.ToDto(report, ProbedAt);

        dto.Adapters[0].EncodeEngines.Should().BeNull("null 是「还没测出来」，不能被写成 0");
        dto.Adapters[1].EncodeEngines.Should().Be(0, "0 是「明确没有硬编」，不能被丢成 null");
        dto.Adapters[2].EncodeEngines.Should().Be(2);
    }

    /// <summary>依赖依据（<c>EngineBasis</c>）必须原样带到线上 —— 它是现场判断可信度的唯一线索。</summary>
    [Fact]
    public void The_basis_for_every_number_is_carried_through()
    {
        var report = new EncoderCapabilityReport(
            Adapters: [Adapter("测试卡", engines: 1, basis: "PDH 实测：探测这一刻有 1 个编码引擎正在被使用。")],
            SupportedCodecs: [VideoCodec.H264],
            RecommendedCodec: null,
            Summary: "测试用。");

        var dto = EncoderCapabilityMapper.ToDto(report, ProbedAt);

        dto.Adapters[0].EngineBasis.Should().Be("PDH 实测：探测这一刻有 1 个编码引擎正在被使用。");
    }

    /// <summary>编码格式在线上用<b>字符串</b>表达（见 <c>RemoteCodecs</c>）。</summary>
    [Fact]
    public void Codecs_use_the_wire_representation()
    {
        var report = new EncoderCapabilityReport(
            Adapters: [Adapter("测试卡", engines: 1, basis: "测试", codecs: [VideoCodec.H264, VideoCodec.Hevc])],
            SupportedCodecs: [VideoCodec.H264, VideoCodec.Hevc],
            RecommendedCodec: VideoCodec.Hevc,
            Summary: "测试用。");

        var dto = EncoderCapabilityMapper.ToDto(report, ProbedAt);

        dto.SupportedCodecs.Should().Equal(RemoteCodecs.H264, RemoteCodecs.Hevc);
        dto.Adapters[0].SupportedCodecs.Should().Equal(RemoteCodecs.H264, RemoteCodecs.Hevc);
        dto.RecommendedCodec.Should().Be(RemoteCodecs.Hevc);
    }

    /// <summary>"没给建议"必须原样传成 <c>null</c>，不能变成一个空串或占位格式。</summary>
    [Fact]
    public void No_recommendation_travels_as_null()
    {
        var report = new EncoderCapabilityReport(
            Adapters: [],
            SupportedCodecs: [],
            RecommendedCodec: null,
            Summary: "测试用。");

        EncoderCapabilityMapper.ToDto(report, ProbedAt).RecommendedCodec.Should().BeNull();
    }

    /// <summary>探测时刻由调用方给（"采集那一刻"），不是"打包那一刻"。</summary>
    [Fact]
    public void The_probe_timestamp_is_taken_from_the_caller()
    {
        var report = new EncoderCapabilityReport([], [], null, "测试用。");

        EncoderCapabilityMapper.ToDto(report, ProbedAt).ProbedAt.Should().Be(ProbedAt);
    }

    private static EncoderAdapterCapability Adapter(
        string name,
        int? engines,
        string? basis,
        IReadOnlyList<VideoCodec>? codecs = null,
        bool isSoftware = false)
        => new(
            Name: name,
            VendorId: 0x10DE,
            DeviceId: 0x1C82,
            DedicatedVideoMemoryBytes: 4UL * 1024 * 1024 * 1024,
            IsSoftwareRenderer: isSoftware,
            SupportedCodecs: codecs ?? [VideoCodec.H264],
            EncodeEngines: engines,
            EngineBasis: basis);
}
