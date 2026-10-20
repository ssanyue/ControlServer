using ControlServer.Contracts;
using ControlServer.Server.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// <b>中心的"桌面静止了多久"</b>（M5.4-c② 步 7）。
///
/// <para>
/// 🔥 <b>为什么它必须活在会话级、而不是塞进 <c>AgentStats</c> 里</b>：
/// <c>AgentStats</c> 有一条<b>为 null 的判据</b>（"一帧都没发过就还不报数"），
/// 而展厅里那台"从开流第一秒就静止"的机器<b>恰恰一帧都发不出去</b> ——
/// 塞进去的话，最该看见"它只是静止、不是坏了"的那一台，页面上反而是 <c>—</c>。
/// 所以这个值走会话级字段，与"发了几帧"彻底解耦。
/// </para>
///
/// <para>
/// 🔥 <b>三态是核心</b>：<c>null</c>（这台机器不提这件事 / 还没报过）、
/// <c>0</c>（刚动过）、<c>12000</c>（已静止 12 秒）。
/// 下面每条用例对应其中一种，而"<c>null</c> 不许被当成 0"是这里最容易写错的一条：
/// 把两者合并，页面上"桌面刚动过"与"这台机器没这个信息"就再也分不开了。
/// </para>
/// </summary>
public sealed class RemoteDesktopIdleReportTests : IDisposable
{
    private readonly ControlServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private RemoteSessionRegistry Registry => _factory.Services.GetRequiredService<RemoteSessionRegistry>();

    /// <summary>
    /// <b>核心用例</b>：被控端报上来的静止时长，原样出现在给页面的 DTO 上。
    /// </summary>
    [Fact]
    public void The_idle_time_reaches_the_page_dto()
    {
        var (registry, sid) = OpenSession(4411);

        registry.AcceptAgentState(4411, Idle(sid, 4200));

        registry.TryGet(sid)!.DesktopIdleMs.Should().Be(
            4200,
            "这个数的全部意义就是「被控端知道、中心不知道」 —— 中途丢在任何一层，"
            + "页面就只能继续猜「到底静止了还是在掉帧」");
    }

    /// <summary>
    /// 🔥 <b>一帧都没发过（<c>AgentStats</c> 仍然是 null）时，静止时长照样要在。</b>
    /// </summary>
    /// <remarks>
    /// 这不是凑数的场景：<b>展厅里定格在一张图上的那台机器正好就是它</b> ——
    /// 开流那一刻桌面就没动过，所以被控端一帧都发不出来。
    /// 把这个字段塞进 <c>AgentStats</c> 就会让它成为页面上唯一"什么都没有"的那种情况，
    /// 而那正是最该被解释的一种。
    /// </remarks>
    [Fact]
    public void The_idle_time_survives_when_no_frame_has_ever_been_sent()
    {
        var (registry, sid) = OpenSession(4412);

        registry.AcceptAgentState(
            4412,
            new AgentRemoteSessionStateDto(
                SessionId: sid,
                State: RemoteSessionStates.Streaming,
                Reason: null,
                Frames: 0,
                Bytes: 0,
                Fps: 0,
                Dropped: 0,
                DesktopIdleMs: 5000));

        var dto = registry.TryGet(sid)!;

        dto.AgentStats.Should().BeNull("前置条件：一帧都没发过 ⇒ 计数那份刻意还是 null（那条判据本身不该动）");

        dto.DesktopIdleMs.Should().Be(
            5000,
            "「桌面从开流起就没动过」正是最需要这句话的场景 —— 把它挂在 AgentStats 上，"
            + "这种机器在页面上就永远是「—」，看上去和「坏了」一模一样");
    }

    /// <summary>
    /// 上报里<b>没提</b>这个字段（旧客户端）⇒ 保留上一次的值，<b>不许清成 <c>null</c></b>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 与 <c>InputState</c> / <c>AudioState</c> 同一条规矩：<b>「没说」不是「没有」</b>。
    /// 每次上报都清一遍的话，只要有一条老版本的消息混进来，
    /// 页面就会从"已静止 12 秒"闪回"—"，而那条消息其实什么都没否认。
    /// </remarks>
    [Fact]
    public void A_report_that_says_nothing_keeps_the_last_known_value()
    {
        var (registry, sid) = OpenSession(4413);

        registry.AcceptAgentState(4413, Idle(sid, 4200));
        registry.AcceptAgentState(4413, Idle(sid, null));

        registry.TryGet(sid)!.DesktopIdleMs.Should().Be(
            4200,
            "这条上报只是没提这件事（null = 旧客户端 / 这一条没带），不是「桌面动了」");
    }

    /// <summary>
    /// 🔥 <b><c>0</c> 是一个值</b>：桌面重新开始变化时必须把它顶掉。
    /// </summary>
    /// <remarks>
    /// 只钉"null 保留"这一侧是不够的：实现若写成 <c>if (idle &gt; 0)</c>，
    /// 那么"静止 12 秒 → 用户动鼠标"之后，页面会一直停在"已静止 12 秒"，
    /// <b>直到会话结束</b>。而这恰恰是用户最容易盯着看的那一刻。
    /// </remarks>
    [Fact]
    public void Zero_overwrites_a_previous_idle_time()
    {
        var (registry, sid) = OpenSession(4414);

        registry.AcceptAgentState(4414, Idle(sid, 4200));
        registry.AcceptAgentState(4414, Idle(sid, 0));

        registry.TryGet(sid)!.DesktopIdleMs.Should().Be(
            0,
            "0 的意思是「刚刚还在动」；把它当成「没提」，页面上那句「已静止 12 秒」就再也不会消失");
    }

    /// <summary>
    /// 一路<b>从没报过</b>这个字段的会话（M5.4-c② 之前的客户端）⇒ 页面拿到的必须是 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 🔥 这条守的是"默认值不许是有意义的数"：默认写成 <c>0</c> 的话，
    /// 所有旧客户端的机器都会统一显示"桌面刚动过" —— 一句看起来很正常、却完全是编的话。
    /// </remarks>
    [Fact]
    public void An_old_client_leaves_it_unknown_instead_of_zero()
    {
        var (registry, sid) = OpenSession(4415);

        registry.TryGet(sid)!.DesktopIdleMs.Should().BeNull(
            "还没收到过这条信息 ⇒ 「不知道」。默认成 0 会让每台旧机器都显示「桌面刚动过」");
    }

    // ═══════════════════════════════════════════════════════════
    // 夹具
    // ═══════════════════════════════════════════════════════════

    private (RemoteSessionRegistry Registry, string SessionId) OpenSession(int agentId)
    {
        var registry = Registry;

        var opened = registry.Open(agentId, null, null, null, DateTimeOffset.UtcNow);

        opened.Session.Should().NotBeNull($"会话本该建起来；实际：{opened.ErrorCode} / {opened.Message}");

        return (registry, opened.Session!.SessionId);
    }

    /// <summary>
    /// 造一条<b>真的</b>状态上报（走生产那个 DTO 的构造，不手搓匿名对象）。
    /// <para>
    /// ⚠️ 参数全部具名：这个记录有十几个位置参数，少传一个就整体错位，
    /// 而编译器不会说一个字（见 <c>AgentRemoteSessionStateDto</c> 那段说明）。
    /// </para>
    /// </summary>
    private static AgentRemoteSessionStateDto Idle(string sessionId, long? idleMs)
        => new(
            SessionId: sessionId,
            State: RemoteSessionStates.Streaming,
            Reason: null,
            Frames: 12,
            Bytes: 4096,
            Fps: 12,
            Dropped: 0,
            DesktopIdleMs: idleMs);
}
