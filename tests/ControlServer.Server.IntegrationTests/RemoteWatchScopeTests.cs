using System.Reflection;
using ControlServer.Server.Hubs;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 🔥 <b>"这条连接有没有资格操作这一路"的边界</b>（M5.2 的归属校验）。
///
/// <para>
/// 它是"直接操作别人键鼠"这条路上<b>唯一</b>的一道归属关卡，而它判错的两个方向
/// 代价不对称：判松了，一个刷新过/切走过的页面还能往被控机送鼠标事件
/// （坐标来自一张<b>已经不在屏幕上的旧画面</b>，坐在被控机前的人会看到鼠标自己动）；
/// 判紧了，正常用户点下去没反应，而"没反应"在屏幕上没有任何线索。
/// </para>
///
/// <para>
/// ⚠️ 这些用例守的全是<b>边角</b>（空 / null / 大小写 / 前缀），
/// 因为正常路径（两个相等的非空字符串）根本不会有人写错 ——
/// 会被人写错的、以及会在真实网络里出现的，恰恰是这些。
/// </para>
/// </summary>
public sealed class RemoteWatchScopeTests
{
    /// <summary>正常情况下，只有"两个 Id 完全一样"才算有资格。</summary>
    [Fact]
    public void Only_the_exact_same_session_qualifies()
    {
        RemoteWatchScope.IsWatching("093012-1a2b3c4d", "093012-1a2b3c4d")
            .Should().BeTrue("这条连接正是从这一路进来的");
    }

    /// <summary>
    /// 各种"不该有资格"的输入。
    /// </summary>
    /// <remarks>
    /// 🔥 每一行都对应一个<b>真实会出现</b>的请求，而不是为了凑覆盖率：
    /// <list type="bullet">
    ///   <item><c>(null, "x")</c> —— 从没 Join 过就发输入（刷新后的陈旧页面）；</item>
    ///   <item><c>("x", null)</c>/<c>("x", "")</c> —— 请求里没带 Id；</item>
    ///   <item><c>("", "")</c>/<c>(null, null)</c> —— <b>空不该等于空</b>：
    ///         一个从没 Join 过的连接与一个 Join 了空 Id 的连接都不该拿到资格；</item>
    ///   <item><c>("x", "X")</c> —— 会话 Id 是机器生成的，<b>大小写敏感</b>，
    ///         用区域相关的比较会引入"在某些区域设置下两个不同的 Id 被当成同一个"；</item>
    ///   <item><c>("x", "xy")</c> —— <b>前缀不算</b>（哪怕只差一个字符）；</item>
    ///   <item><c>("x", " x")</c> —— <b>不做 trim</b>：会话 Id 由中心生成，
    ///         它里面不该有空格；容忍空格等于把两个不同的 Id 混起来。</item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData(null, "093012-1a2b3c4d")]
    [InlineData("093012-1a2b3c4d", null)]
    [InlineData("093012-1a2b3c4d", "")]
    [InlineData("093012-1a2b3c4d", "   ")]
    [InlineData("", "")]
    [InlineData(null, null)]
    [InlineData("093012-1a2b3c4d", "093012-1A2B3C4D")]
    [InlineData("093012-1a2b3c4d", "093012-1a2b3c4")]
    [InlineData("093012-1a2b3c4d", " 093012-1a2b3c4d")]
    [InlineData("093012-1a2b3c4d", "093012-1a2b3c4d ")]
    public void Anything_else_does_not_qualify(string? watched, string? requested)
    {
        RemoteWatchScope.IsWatching(watched, requested).Should().BeFalse();
    }

    /// <summary>
    /// 🔥 <b>"多人同时看一路"是受支持的场景，判据里不许出现"别人"</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本方案<b>没有</b>独占控制权：闸门是<b>会话级</b>的，一个人打开，
    /// 所有正在看这一路的人都获得能力。所以"有资格"这件事只由
    /// <b>你这条连接在看哪一路</b>决定 —— 两个人同时看一路，两个人都合格。
    /// </para>
    /// <para>
    /// 这条用反射把那个性质钉住：判据<b>只有两个入参</b>。
    /// 将来若有人给它加一个"除非别人正在控"之类的参数，这里会失败 ——
    /// 而那时该做的是<b>想清楚</b>（独占控制权要动契约、动页面提示、
    /// 动"谁抢到了"的语义与超时归还），而不是顺手加个分支。
    /// </para>
    /// </remarks>
    [Fact]
    public void The_judgement_depends_only_on_this_connection()
    {
        var method = typeof(RemoteWatchScope).GetMethod(
            nameof(RemoteWatchScope.IsWatching),
            BindingFlags.Public | BindingFlags.Static);

        method.Should().NotBeNull();

        method!.GetParameters().Should().HaveCount(
            2,
            "判据只由「这条连接在看哪一路」与「它想操作哪一路」两个值决定；"
            + "多一个输入就意味着引入了「别人」这个概念，那是一次语义变更");
    }
}
