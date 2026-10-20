using ControlServer.Agent.Remote;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// <b>"M5.1 只允许一路"该怎么落地</b>（<see cref="RemoteSessionAdmission"/>）。
///
/// <para>
/// 🔥 这组用例守的是一次真实现场故障（2026-10-19，DESKTOP-2DFUKBT）：
/// 中心在 09:18:22 把一路会话判死后<b>没能把 stop 送到被控端</b>，
/// 而被控端"只允许一路"的实现是<b>拒绝新会话</b> —— 于是那台机器
/// 在之后十分钟里<b>彻底远控不了</b>，页面上还写着"没人登录 / 托盘没在跑"。
/// </para>
///
/// <para>
/// 判据一旦写错，代价不是"少开一路"，而是<b>这台机器再也连不上</b>：
/// 所以三条分支（没在推 / 同一路 / 另一路）各钉一条，外加一条"不许把同 id 当成接管"。
/// </para>
/// </summary>
public sealed class RemoteSessionAdmissionTests
{
    [Fact]
    public void No_active_session_means_start()
    {
        RemoteSessionAdmission.Decide(null, "s-1").Should().Be(RemoteSessionAdmissionDecision.Start);
        RemoteSessionAdmission.Decide("", "s-1").Should().Be(RemoteSessionAdmissionDecision.Start);
        RemoteSessionAdmission.Decide("   ", "s-1").Should().Be(RemoteSessionAdmissionDecision.Start);
    }

    [Fact]
    public void The_same_session_again_is_a_duplicate_not_a_rebuild()
    {
        // 中心重连后可能重发同一条 start。**重建的代价**是把刚建好的抓屏器 / 硬编 MFT
        // 拆掉重来，用户看到"画面黑一下、再等首帧" —— 所以同 id 必须幂等。
        RemoteSessionAdmission.Decide("s-1", "s-1")
            .Should().Be(RemoteSessionAdmissionDecision.Duplicate);
    }

    [Fact]
    public void A_different_session_supersedes_the_orphan()
    {
        // 中心对每台机器也只允许一路 ⇒ 一个不同 id 的 start 意味着"中心已经不要旧那一路了"。
        RemoteSessionAdmission.Decide("091729-c6566be2", "091831-ecddd605")
            .Should().Be(RemoteSessionAdmissionDecision.SupersedeAndStart);
    }

    /// <summary>
    /// 会话 id 是中心生成的机器标识，<b>不是给人看的文本</b> ——
    /// 大小写/文化相关的比较在这里只会造出"明明是同一路却被当成另一路"的分叉。
    /// </summary>
    [Fact]
    public void Session_ids_are_compared_ordinally()
    {
        RemoteSessionAdmission.Decide("S-1", "s-1")
            .Should().Be(RemoteSessionAdmissionDecision.SupersedeAndStart);

        RemoteSessionAdmission.Decide("s-1", "s-1")
            .Should().Be(RemoteSessionAdmissionDecision.Duplicate);
    }

    [Fact]
    public void The_supersede_line_names_both_sessions()
    {
        var line = RemoteSessionAdmission.DescribeSupersede("old-id", "new-id");

        // 现场排查时这行日志是"为什么换了会话"的唯一线索：两个 id 都得在。
        line.Should().Contain("old-id").And.Contain("new-id");
        line.Should().Contain("接管");
    }
}
