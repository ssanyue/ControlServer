using ControlServer.Contracts;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 🔥 <b>通道状态（输入 / 音频）与它"为什么不可用"之间唯一的一条规矩</b>
/// （<see cref="RemoteChannelStates.ReasonToKeep"/>，M5.2 补齐 / M5.3 扩到音频）。
///
/// <para>
/// 规矩只有一句：<b>原因只在 <c>unavailable</c> 时有值</b>。
/// 它值钱的地方不在"该留的时候留"（那种情况一眼可见），而在
/// <b>"不该留的时候必须清掉"</b> —— 留着它的页面会写成
/// "通道就绪：本机已禁止远程输入"，一句自相矛盾、却看起来很像回事的话。
/// </para>
///
/// <para>
/// ⚠️ 为什么这条规矩值得单独立一组用例：它在实现里被抄过 <b>5 遍</b>
/// （被控端两处、托盘一处、中心两处）。抄 5 遍的东西一定有第 6 处会写错，
/// 而写错之后的症状是"页面上有个看起来很合理的原因"，<b>没有人会觉得那是个 bug</b>。
/// 现在它只剩一份实现，这组用例就是那份实现的守卫。
/// </para>
///
/// <para>
/// 🔥 还有一层更贵的：<b>它与"这路会话为什么结束"是两件事</b>。
/// 在这条规矩出现之前，输入的原因借的是会话的 <c>Reason</c> ——
/// 于是"会话<b>正常</b>结束（观众点了停止）、而输入本来就不通"时，
/// 页面会写着「输入通道不可用：已在本机取消推流」，
/// 真正的原因（"本机已禁止远程输入"）被顶掉：
/// 一句很像回事、却<b>指向完全不同处置方法</b>的话。
/// </para>
/// </summary>
public sealed class RemoteChannelStateTests
{
    /// <summary>要上页面的那句人话（真实句子，不是占位符）。</summary>
    private const string Reason = "这台机器本机禁止了远程输入（remote-input.deny）。";

    /// <summary>"不可用"时原因必须原样留下 —— 否则用户拿不到"下一步做什么"。</summary>
    [Fact]
    public void Unavailable_keeps_the_reason()
    {
        RemoteChannelStates.ReasonToKeep(AgentInputStates.Unavailable, AgentInputStates.Unavailable, Reason)
            .Should().Be(Reason, "这一句是整条链路上唯一能告诉人「该去删哪个文件 / 该去升级客户端」的东西");
    }

    /// <summary>
    /// 🔥 <b>本组的核心</b>：只要不是"不可用"，原因就必须被清成 <c>null</c>。
    ///
    /// <para>
    /// 每一行都对应一个真实会出现的时刻，而不是为了凑行数：
    /// <list type="bullet">
    ///   <item><c>ready</c> —— 接上了：还挂着上一次的失败原因，页面就是"就绪：禁止远程输入"；</item>
    ///   <item><c>connecting</c> —— 正在接：这时候显示上一次的原因会让人以为已经失败了，
    ///         于是去重启客户端，而它其实只是还在建管道；</item>
    ///   <item><c>null</c> —— 旧客户端不认识这个字段：<b>「没说」不是「不可用」</b>，
    ///         更不该把上一路的原因留到这一路上；</item>
    ///   <item><c>""</c> / 未知取值 —— 线协议上出现了我们不认识的东西：
    ///         这时说"不可用"是凭空下结论；</item>
    ///   <item><c>"Unavailable"</c> —— <b>大小写不同就是另一个值</b>：
    ///         线协议上的取值刻意区分大小写，容忍它等于把"状态传错了"这件事藏起来，
    ///         而那是比"原因丢了"更该被看见的问题。</item>
    /// </list>
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(AgentInputStates.Ready)]
    [InlineData(AgentInputStates.Connecting)]
    [InlineData("")]
    [InlineData("Unavailable")]
    [InlineData("UNAVAILABLE")]
    [InlineData(" unavailable")]
    [InlineData("unavailable ")]
    [InlineData("unavailable-")]
    [InlineData("whatever")]
    public void Anything_but_unavailable_drops_the_reason(string? state)
    {
        RemoteChannelStates.ReasonToKeep(state, AgentInputStates.Unavailable, Reason)
            .Should().BeNull(
                "就绪 / 接入中 / 不认识的状态下留着失败原因，页面上就成了一句自相矛盾的话；"
                + "而「没说」也不等于「不可用」");
    }

    /// <summary>"状态是 null"也要走上面那条：单列出来是因为它最容易被写成特例。</summary>
    [Fact]
    public void A_missing_state_does_not_keep_the_reason()
    {
        RemoteChannelStates.ReasonToKeep(null, AgentInputStates.Unavailable, Reason)
            .Should().BeNull("旧客户端不上报这个字段 ⇒ 中心对状态一无所知，不该顺手把原因也带上");
    }

    /// <summary>
    /// 状态对了但原因是 <c>null</c>：返回 <c>null</c>，<b>不编一句话出来</b>。
    ///
    /// <para>
    /// ⚠️ 这是刻意允许的一种组合：被控端可能只判出"接不上"而没能给出人话
    /// （比如托盘那次调用超时了）。这时页面显示"不可用（没有给出原因）"是对的 ——
    /// 比中心自己拼一句"可能是声卡问题"要好得多，因为那句话是<b>猜的</b>，
    /// 而猜错的方向会被用户当成结论。
    /// </para>
    /// </summary>
    [Fact]
    public void Unavailable_without_a_sentence_stays_silent()
    {
        RemoteChannelStates.ReasonToKeep(AgentInputStates.Unavailable, AgentInputStates.Unavailable, null)
            .Should().BeNull();

        RemoteChannelStates.ReasonToKeep(AgentInputStates.Unavailable, AgentInputStates.Unavailable, "")
            .Should().BeEmpty("空串是「说了但没说」—— 页面按「没有给出原因」显示，不要自己补一句");
    }

    /// <summary>
    /// 🔥 <b>两个域（输入 / 音频）走的是同一份实现</b> —— 逐域跑一遍。
    /// </summary>
    /// <remarks>
    /// <para>
    /// M5.3 加音频时最容易犯的错，就是"照着输入抄一份，抄的时候少抄了<b>清空</b>那半句" ——
    /// 而少抄的那半句只在"先失败、后成功"的顺序里才显形，手工点很难碰上。
    /// 所以两个域都在这里跑一遍，断言的是同一句话。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>写成 <c>[Fact]</c> 而不是 <c>[Theory(InlineData(…))]</c> 是必须的</b>：
    /// 两个域的 <c>Unavailable</c> <b>目前是同一个字面量</b>，
    /// 所以两行 <c>InlineData</c> 在编译期就是重复的（xUnit 会直接告警）——
    /// 而那个告警正好在说"这两行跑的是同一件事"。
    /// 真正防止"两个字面量哪天被改成不一样"的是下面那条
    /// <see cref="The_wire_values_are_pinned"/>，不是这一条。
    /// </para>
    /// </remarks>
    [Fact]
    public void Both_domains_follow_the_same_rule()
    {
        foreach (var (domain, unavailableState) in new[]
        {
            ("输入", AgentInputStates.Unavailable),
            ("音频", AgentAudioStates.Unavailable),
        })
        {
            RemoteChannelStates.ReasonToKeep(unavailableState, unavailableState, Reason)
                .Should().Be(Reason, $"{domain}这一域在「不可用」时同样要把原因带到页面上");

            RemoteChannelStates.ReasonToKeep(AgentInputStates.Ready, unavailableState, Reason)
                .Should().BeNull($"{domain}这一域同样要在「不再不可用」时把原因清掉");
        }
    }

    /// <summary>
    /// 🔥 <b>两个域的"不可用"取值必须真的是同一个字面量。</b>
    ///
    /// <para>
    /// 它们目前都写作 <c>"unavailable"</c>，而前端 <c>types.ts</c> 里那张表
    /// （<c>AGENT_INPUT_STATES</c> / <c>AGENT_AUDIO_STATES</c>）也是照抄这个字面量。
    /// 一旦哪天有一个被改成别的写法，<b>跨语言那一跳不会报任何错</b> ——
    /// 前端会安静地走进"不认识的状态"那一支，页面上显示"等待声音…"之类的等待文案，
    /// 而实际上它永远好不了。
    /// </para>
    ///
    /// <para>
    /// 所以这条用例把两个字面量钉死在测试里：改名的人会看到它失败，
    /// 然后被迫去把前端那张表一起改掉。
    /// </para>
    /// </summary>
    [Fact]
    public void The_wire_values_are_pinned()
    {
        AgentInputStates.Connecting.Should().Be("connecting");
        AgentInputStates.Ready.Should().Be("ready");
        AgentInputStates.Unavailable.Should().Be("unavailable");

        AgentAudioStates.Connecting.Should().Be("connecting");
        AgentAudioStates.Ready.Should().Be("ready");
        AgentAudioStates.Unavailable.Should().Be("unavailable");
    }
}
