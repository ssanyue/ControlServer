using ControlServer.Platform.Abstractions;
using ControlServer.Platform.Windows;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 编码能力探测（M5.0 第一批 + 第二批）。
///
/// <para>
/// 这组用例守的<b>不是"探测得准不准"</b>（那要靠 §16.1 的现场标定），
/// 而是一条**比准确性更重要的性质**：<b>探测结果绝不能说谎</b>。
/// </para>
///
/// <para>
/// 依据是：整个 8K 目标（以及"要不要买双引擎显卡"这类采购决策）
/// 都建立在这份探测结论上。一旦"没测出来"被写成"不支持"、
/// 或者"猜的引擎数"被当成"测出来的引擎数"，错误会<b>一路传到验收和采购</b>，
/// 而中间没有任何一环能发现它。
/// </para>
///
/// <para>
/// 因此这里钉死三条不变量：
/// <list type="number">
/// <item><description>报了任何一项能力结论，就必须能<b>说出依据</b>；</description></item>
/// <item><description>没测出来的东西<b>不许给建议值</b>（宁可 null）；</description></item>
/// <item><description><b>"0" 与 "null" 不可混同</b> —— 前者是"明确没有"，后者是"还没测出来"。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class EncoderCapabilityProbeTests
{
    [Fact]
    public async Task Probe_never_throws_even_when_nothing_can_be_enumerated()
    {
        if (!OperatingSystem.IsWindows()) return;

        var report = await new WindowsEncoderCapabilityProbe().ProbeAsync();

        report.Should().NotBeNull();
        report.Summary.Should().NotBeNullOrWhiteSpace(
            "探测可以失败，但不能沉默 —— 空结论也必须有一句人话说明为什么是空的");
    }

    /// <summary>
    /// 🔥 铁律：<b><c>0</c> 与 <c>null</c> 是两件不同的事</b>。
    ///
    /// <para>
    /// <c>EncodeEngines = 0</c> 的含义是"<b>明确</b>没有硬编"（如 MX250，官方矩阵写明不支持），
    /// <c>null</c> 的含义是"<b>还没测出来</b>"。
    /// 把后者写成前者，等于在"这台机器能不能跑 8K"这件事上<b>凭空给出否定结论</b>。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_reported_engine_count_never_comes_without_its_basis()
    {
        if (!OperatingSystem.IsWindows()) return;

        var report = await new WindowsEncoderCapabilityProbe().ProbeAsync();

        foreach (var adapter in report.Adapters)
        {
            if (adapter.EncodeEngines is null && adapter.EngineBasis is null)
            {
                // "未测出"也必须写清楚为什么 —— 否则现场只能看到一个空栏，
                // 分不清"没测"与"代码没跑到这里"。但这一条比"报了数字却没依据"轻，
                // 所以只在两者都缺失时报错。
                Assert.Fail($"适配器「{adapter.Name}」的引擎数为 null，但连一句说明都没有。");
            }

            if (adapter.EncodeEngines is null) continue;

            adapter.EngineBasis.Should().NotBeNullOrWhiteSpace(
                $"适配器「{adapter.Name}」报了引擎数 {adapter.EncodeEngines}，"
                + "就必须带上「这个数是怎么来的」—— 没有依据的数字在采购论证里没有价值");
        }
    }

    /// <summary>
    /// 🔥 <b>引擎数与"支持格式"必须自洽</b>。
    ///
    /// <para>
    /// 两条推论，反了任何一条都说明探测在自相矛盾：
    /// </para>
    /// <list type="number">
    ///   <item><b>观测到引擎在使用 ⇒ 一定存在硬件编码器</b>（否则那些实例是哪来的），
    ///         所以 <c>EncodeEngines &gt; 0</c> 时支持格式不能是空表；</item>
    ///   <item><b>报 <c>0</c> ⇒ 必须一个硬件编码器都没发现</b>
    ///         （<c>0</c> 是"明确没有"，不能与"发现了编码器但数不出几个"并存）。</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Engine_counts_and_supported_codecs_must_not_contradict_each_other()
    {
        if (!OperatingSystem.IsWindows()) return;

        var report = await new WindowsEncoderCapabilityProbe().ProbeAsync();

        foreach (var adapter in report.Adapters.Where(a => !a.IsSoftwareRenderer))
        {
            if (adapter.EncodeEngines is > 0)
            {
                adapter.SupportedCodecs.Should().NotBeEmpty(
                    $"适配器「{adapter.Name}」报了 {adapter.EncodeEngines} 个引擎，"
                    + "却又说一个硬编格式都不支持 —— 这两条不可能同时成立");
            }

            if (adapter.EncodeEngines == 0)
            {
                adapter.SupportedCodecs.Should().BeEmpty(
                    $"适配器「{adapter.Name}」报了 0 个引擎（明确无硬编），"
                    + "却又列出了支持格式 —— 「0」的含义只能是没有硬编");
            }
        }
    }

    /// <summary>
    /// 软件光栅器（WARP）<b>不是一块显卡</b>，绝不能报出硬编能力。
    /// <para>
    /// 它会被 DXGI 与 GPU 计数器都报出来（本机实测就在列表里），
    /// 把它当成"一块能编码的卡"会让页面上凭空多出一块"支持的显卡"。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_software_rasterizer_never_claims_hardware_encoding()
    {
        if (!OperatingSystem.IsWindows()) return;

        var report = await new WindowsEncoderCapabilityProbe().ProbeAsync();

        foreach (var adapter in report.Adapters.Where(a => a.IsSoftwareRenderer))
        {
            adapter.SupportedCodecs.Should().BeEmpty(
                $"「{adapter.Name}」是软件光栅器，不可能有硬件编码器");
            adapter.EncodeEngines.Should().Be(0, "软件光栅器的引擎数只能是明确的 0");
        }
    }

    /// <summary>
    /// 全机的"支持格式"必须等于各物理显卡支持格式的并集。
    /// <para>
    /// 这条守的是"汇总时把软件光栅器混进来"这类错误 ——
    /// 汇总值会成为 M5.1 选择编码格式的直接输入，混进一个不存在的格式，
    /// 代价是现场一路试到失败才发现。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Machine_level_codecs_are_exactly_the_union_of_the_physical_adapters()
    {
        if (!OperatingSystem.IsWindows()) return;

        var report = await new WindowsEncoderCapabilityProbe().ProbeAsync();

        var expected = report.Adapters
            .Where(a => !a.IsSoftwareRenderer)
            .SelectMany(a => a.SupportedCodecs)
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        report.SupportedCodecs.Should().Equal(expected);
    }

    /// <summary>
    /// 探测层<b>不许推荐编码格式</b>。
    /// <para>
    /// 格式由<b>目标分辨率</b>决定（≤4096 宽可 H.264，更宽只能 HEVC），
    /// 而分辨率是<b>会话</b>才知道的事 —— 同一台机器接不同的屏，答案就不同。
    /// 在这一层给一个格式，等于替一个还不知道分辨率的会话做决定。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Probe_does_not_pick_a_codec_because_it_cannot_know_the_resolution()
    {
        if (!OperatingSystem.IsWindows()) return;

        var report = await new WindowsEncoderCapabilityProbe().ProbeAsync();

        report.RecommendedCodec.Should().BeNull(
            "编码格式取决于目标分辨率，而分辨率是会话才知道的 —— 探测层替现场决定就是无依据的决定");
    }

    /// <summary>
    /// 兜底实现必须说<b>"我们没做探测"</b>，而不是"这台机器没有编码能力"。
    /// </summary>
    [Fact]
    public async Task The_unsupported_probe_says_it_is_not_implemented_instead_of_claiming_no_capability()
    {
        var report = await new UnsupportedEncoderCapabilityProbe().ProbeAsync();

        report.Adapters.Should().BeEmpty();
        report.SupportedCodecs.Should().BeEmpty();
        report.RecommendedCodec.Should().BeNull();

        // 关键区别：它说的是"我们没为这个平台写探测"，
        // 而不是"这台机器没有硬件编码能力"。
        report.Summary.Should().Contain("不代表本机没有硬件编码能力",
            "空实现必须把话说明白，不能让读者误以为「这台机器没有编码能力」");
    }

    /// <summary>
    /// <b>两层的编码格式名必须字面相等</b>（M5.1）。
    ///
    /// <para>
    /// 🔥 这条守的是一个**跨程序集的字符串合同**，而它在编译期完全不受约束：
    /// </para>
    /// <list type="bullet">
    ///   <item><c>ControlServer.Contracts.RemoteCodecs</c> —— <b>线上</b>的表示
    ///         （能力探测的结论、帧头、DTO 里用的就是它）；</item>
    ///   <item><c>ControlServer.Platform.Abstractions.StreamCodecs</c> ——
    ///         <b>本地管道</b>上码流的表示（托盘与服务之间那一跳）。</item>
    /// </list>
    /// <para>
    /// 两处各写一遍是有原因的（<c>Platform.Abstractions</c> 刻意<b>不引 Contracts</b>：
    /// 它是最底层的契约层，往上引会让依赖方向倒过来）。
    /// 但"刻意各写一遍"必须配一条用例把它们焊在一起 —— 否则某天有人把
    /// 其中一处改成 <c>"h265"</c>、另一处还是 <c>"hevc"</c>，
    /// 后果是<b>画面永远不出来</b>：管道上写着 <c>hevc</c>，
    /// 而中心侧按能力表去比对时一个都认不得，两边日志里全是对的字符串。
    /// </para>
    /// </summary>
    [Fact]
    public void The_stream_codec_names_line_up()
    {
        StreamCodecs.H264.Should().Be(ControlServer.Contracts.RemoteCodecs.H264);
        StreamCodecs.Hevc.Should().Be(ControlServer.Contracts.RemoteCodecs.Hevc);
        StreamCodecs.Mjpeg.Should().Be(ControlServer.Contracts.RemoteCodecs.Mjpeg);
    }

    /// <summary>
    /// MJPEG 是<b>软编降级路径</b>，不是一种"硬件能编"的格式 —— 它<b>不该</b>出现在
    /// <see cref="VideoCodec"/> 这个"硬编器真能编什么"的结论枚举里。
    ///
    /// <para>
    /// ⚠️ <b>NVENC / QSV 都不编 JPEG</b>。把 MJPEG 塞进能力枚举，
    /// 会让"这台机器支持 H.264"与"这台机器只能软编"变成同一类事实 ——
    /// 而前者能支撑 8K 验收、后者连 4K60 的门都摸不到。
    /// </para>
    /// <para>
    /// ⚠️ 但"降级路径"<b>不等于</b>"脚手架"（M7.11 更正措辞）：多屏拼接与无硬编的机器
    /// <b>只能</b>走它，它是那个场景下的正确答案，只是不该冒充硬编能力。
    /// </para>
    /// </summary>
    [Fact]
    public void Mjpeg_is_not_a_hardware_video_codec()
    {
        Enum.GetNames<VideoCodec>()
            .Should().NotContain(
                ControlServer.Contracts.RemoteCodecs.Mjpeg,
                "MJPEG 是软编降级路径：NVENC/QSV 都不编 JPEG，4K 软编只有 12–25 fps");
    }
}
