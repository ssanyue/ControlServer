using ControlServer.Agent.Tasks;
using ControlServer.Contracts;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// <b>「本机为什么用了这个格式」这句话的四种说法</b>（<c>RemoteStreamHost.DescribeCodecChoice</c>）。
///
/// <para>
/// 🔥 <b>这组用例守的是一条排障纪律，不是文案</b>：同一个观察结果「实际用了 MJPEG」
/// 至少有<b>两个完全相反</b>的原因 ——
/// </para>
/// <list type="bullet">
///   <item><b>硬编建不起来</b>（驱动 / 被别人占用 / 尺寸超格式上限）⇒ <b>要去查</b>；
///         而且原因<b>不确定</b>，只有被控端日志里那句分级说明说得清；</item>
///   <item><b>请求的是「全部显示器」</b> ⇒ <b>正常，不用查</b>。DDA 一次只能对一个 output
///         建 duplication，拼接本来就只能走 GDI/MJPEG。这句话必须<b>自己说全</b>：
///         中心页面上看不到被控端日志的「上一行」。</item>
/// </list>
///
/// <para>
/// ⚠️ 合成一句的后果不是「文案不好看」，而是：多屏机上一选「全部显示器」画质就变，
/// 现场照着「硬编没建起来」去查驱动和占用，查一整天，而那条路<b>本来就是这么设计的</b>。
/// 所以下面两条<b>互相排除</b>地断言：说了「全部显示器」就不许再说「没建起来」。
/// </para>
///
/// <para>
/// 🔸 纯函数用例，不需要 GPU、不需要桌面、不需要多屏 —— 也就是说
/// <b>「全部显示器 ⇒ MJPEG」这条分支今天就能在单屏开发机上被钉住</b>，
/// 不必等到那台两屏的机器到手。
/// </para>
/// </summary>
public sealed class RemoteStreamCodecNoteTests
{
    private static readonly string[] PreferredH264 = { RemoteCodecs.H264 };

    /// <summary>
    /// <b>照中心首选 ⇒ 不说任何话</b>。
    /// <para>
    /// 它是一条「沉默即正常」的守卫：这句话会进状态上报、跟着会话一路推到页面上。
    /// 正常路径上多一句解释，等于训练现场忽略这个位置。
    /// </para>
    /// </summary>
    [Fact]
    public void A_matching_choice_says_nothing()
    {
        RemoteStreamHost.DescribeCodecChoice(PreferredH264, RemoteCodecs.H264).Should().BeNull();

        RemoteStreamHost.DescribeCodecChoice(
            PreferredH264, RemoteCodecs.H264, isVirtualDesktop: true).Should().BeNull(
            "虚拟桌面本身不是理由 —— 只有它导致落到 MJPEG 时才有话要说");
    }

    /// <summary>
    /// <b>中心没指定 ⇒ 说清这是本机自选</b>。
    /// <para>
    /// 不说的话，日后有人看到「实际是 hevc」会当成「中心下发的 hevc」，
    /// 于是去中心那侧找一条从来不存在的规定。
    /// </para>
    /// </summary>
    [Fact]
    public void An_unspecified_request_is_reported_as_a_local_choice()
    {
        var note = RemoteStreamHost.DescribeCodecChoice(Array.Empty<string>(), RemoteCodecs.Hevc);

        note.Should().NotBeNull();
        note!.Should().Contain("没有指定").And.Contain(RemoteCodecs.Hevc);
    }

    /// <summary>
    /// <b>同一集合内改判 ⇒ 必须点明「没有越出集合」。</b>
    /// <para>
    /// 这句话是给观看端的人看的：它决定了「浏览器解不解得开」这个问题有没有答案 ——
    /// 集合是中心按浏览器的能力算出来的，只要没越出，就一定解得开。
    /// </para>
    /// </summary>
    [Fact]
    public void An_in_set_switch_mentions_the_set_boundary()
    {
        var requested = new[] { RemoteCodecs.H264, RemoteCodecs.Hevc };

        var note = RemoteStreamHost.DescribeCodecChoice(requested, RemoteCodecs.Hevc);

        note.Should().NotBeNull();
        note!.Should().Contain("改判").And.Contain("没有越出")
            .And.Contain(RemoteCodecs.Hevc, "要说清改成了什么");
    }

    /// <summary>
    /// <b>不是「全部显示器」的 MJPEG ⇒ 说硬编没建起来，并且不许提拼接。</b>
    /// </summary>
    [Fact]
    public void A_plain_mjpeg_fallback_points_at_the_hardware_path()
    {
        var note = RemoteStreamHost.DescribeCodecChoice(PreferredH264, RemoteCodecs.Mjpeg);

        note.Should().NotBeNull();
        note!.Should().Contain("没建起来").And.Contain("见上一行",
            "这是唯一一个「原因在被控端日志里」的分支，必须把读者指过去");

        note.Should().NotContain("全部显示器",
            "这条路与拼接无关，混进来会让现场以为是屏选错了");
    }

    /// <summary>
    /// <b>「全部显示器」⇒ MJPEG 时必须说全三件事</b>：
    /// ① 是因为选了全部显示器；② 拼接只能走 MJPEG；③ 画面是全的、这不是故障。
    ///
    /// <para>
    /// ⚠️ 第 ③ 件不能省：这句话要挡住的正是「看到 MJPEG 就以为画面只有一半」的猜测，
    /// 而那个猜测在像素上<b>曾经</b>是真的（安静回落到主屏是禁止的，见
    /// <c>WindowsDxgiScreenCapturer.EnsureBound</c>），所以必须明说这次不是。
    /// </para>
    /// </summary>
    [Fact]
    public void A_virtual_desktop_mjpeg_fallback_explains_the_stitching()
    {
        var note = RemoteStreamHost.DescribeCodecChoice(
            PreferredH264, RemoteCodecs.Mjpeg, isVirtualDesktop: true);

        note.Should().NotBeNull();
        note!.Should().Contain("全部显示器").And.Contain("拼接只能走 MJPEG")
            .And.Contain("全的", "必须挡掉只有半块画面这个猜测")
            .And.Contain("不是故障");

        note.Should().NotContain("没建起来",
            "硬编没建起来意味着要去查驱动/占用；这里没坏，不许把人指到那条路上去");

        note.Should().NotContain("见上一行",
            "中心页面上没有上一行 —— 这个分支的原因必须自解释");
    }

    /// <summary>
    /// <b>中心没指定 + 全部显示器 ⇒ 仍然要说拼接原因。</b>
    ///
    /// <para>
    /// 这是最容易漏的一格：先命中「中心没指定 ⇒ 本机自选」就 return，
    /// 于是多屏机上最常见的一种组合（页面没声明偏好）反而得不到解释。
    /// </para>
    /// </summary>
    [Fact]
    public void An_unspecified_request_on_the_virtual_desktop_still_explains_the_stitching()
    {
        var note = RemoteStreamHost.DescribeCodecChoice(
            Array.Empty<string>(), RemoteCodecs.Mjpeg, isVirtualDesktop: true);

        note.Should().NotBeNull();
        note!.Should().Contain("全部显示器").And.Contain("拼接只能走 MJPEG")
            .And.Contain("没有指定", "本机自选这件事仍然要说");
    }

    /// <summary>
    /// <b>将来硬编支持拼接之后，这句话不许再出现。</b>
    ///
    /// <para>
    /// 这一条是给「以后」写的：拼接一旦走通硬编，<c>actual</c> 就不再是 MJPEG，
    /// 而这条解释若还挂着，就成了<b>错的</b>原因 —— 比没有原因更坏。
    /// 用一条断言把「解释必须跟随事实」钉住，改代码时它会挡住顺手复制粘贴。
    /// </para>
    /// </summary>
    [Fact]
    public void The_stitching_explanation_disappears_once_hardware_can_stitch()
    {
        var note = RemoteStreamHost.DescribeCodecChoice(
            PreferredH264, RemoteCodecs.H264, isVirtualDesktop: true);

        note.Should().BeNull("硬编真的编成了，就没有 MJPEG 这件事要解释");
    }
}
