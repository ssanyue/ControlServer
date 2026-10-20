using ControlServer.Contracts;
using ControlServer.Server.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// <b>会话活性判据</b>（<see cref="RemoteSessionRegistry.Sweep"/>）。
///
/// <para>
/// 🔥 <b>它守的是一条产品级承诺，不是一条实现细节</b>：被控端的桌面<b>一丝都不动</b>时，
/// DXGI Desktop Duplication 按约定返回"没有新帧"，于是链条上一帧都不会产生 ——
/// 而<b>这恰恰是展厅最常见的样子</b>（机器定格在一张图上、没人碰鼠标）。
/// 把"没有新帧"当成"这路流断了"，得到的不是"少一帧"，而是
/// <b>这路会话每 15 秒被判死一次、永远连不上</b>。
/// </para>
///
/// <para>
/// 🔥 <b>判据本身写在三处注释里，而且三处说的是同一句话</b>：
/// <list type="bullet">
///   <item><c>IdleTimeout</c>：「不再收到帧、<b>也没有状态上报</b>时，多久判成"断了"」；</item>
///   <item><c>RequestedTimeout</c>：「多久还没等到被控端的<b>任何</b>状态上报就判成"没起来"」；</item>
///   <item><c>Sweep</c> 自己的文档：「清掉已经断掉的会话（<b>帧停了、状态也不再上报</b>）」。</item>
/// </list>
/// 三处都写着"帧 <b>或</b> 状态上报"，所以本组用例断言的就是这句话。
/// </para>
///
/// <para>
/// ⚠️ <b>时间只有一半能注入</b>，这一点决定了三条用例的写法，也决定了为什么有一条必须真等：
/// <list type="bullet">
///   <item><c>Open(…, now:)</c> 与 <c>Sweep(now:)</c> 的 <c>now</c> <b>可以注入</b> ——
///         所以"建会话后过了多久"这类判据可以<b>零等待</b>地测（<c>B</c>/<c>C</c> 用例）；</item>
///   <item>而<b>事件自己的时刻</b>（<c>Observe</c> / <c>ApplyState</c> 里的
///         <c>DateTimeOffset.UtcNow</c>）<b>注入不了</b> —— 所以
///         "一帧停在十几秒前、而这十几秒里一直在回话"这种<b>两个时刻的相对位置</b>，
///         只能真的等过去（<c>A</c> 用例）。这不是懒，是没有别的办法：
///         把 <c>Sweep</c> 的 <c>now</c> 往前推，会把"回话"和"帧"一起推旧，
///         那样测出来的东西与要证的东西不是同一件事。</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ <b>断言尽量落在"后果"上（会话还在不在），而不是落在 <c>Sweep</c> 的返回值上</b>：
/// 测试宿主里那个每 5 秒醒一次的 <c>RemoteSessionSweeperHostedService</c> 是**真的在跑**的，
/// 它可能在断言之前就把会话清掉 —— 那时"是谁清的"已经不重要了，
/// 重要的是"这路会话到底有没有被清掉"。
/// </para>
/// </summary>
public sealed class RemoteSessionSweepTests : IDisposable
{
    /// <summary>被控端那条周期上报的节奏（`RemoteSessionCoordinator` 里是每秒一条）。</summary>
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(1);

    private readonly ControlServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private RemoteSessionRegistry Registry => _factory.Services.GetRequiredService<RemoteSessionRegistry>();

    // ═══════════════════════════════════════════════════════════
    // A / 静止画面：**必须真等**（见类注释里那段说明）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task A_static_screen_is_not_a_dead_session()
    {
        var registry = Registry;
        var sid = registry.Open(4211, null, null, null, DateTimeOffset.UtcNow).Session!.SessionId;

        // 第一帧（也是**唯一**一帧）：到此为止画面还是动的。
        registry.AcceptFrame(4211, Frame(sid, sequence: 1)).Ok.Should().BeTrue();

        // 之后桌面冻住 —— 一帧都不再来（DDA 就是这么工作的），但被控端**每秒回话一次**。
        //
        // 18 秒 > IdleTimeout（15 秒），而"最后一次回话"离现在不到 1 秒 ⇒ 余量 14 秒。
        // 不是那种"差一点点就翻面"的断言。
        for (var i = 0; i < 18; i++)
        {
            await Task.Delay(ReportEvery);

            registry.AcceptAgentState(4211, State(sid, RemoteSessionStates.Streaming, frames: 1));
        }

        // 先自己判一次（谁先清掉的都算，所以下面还要看会话本身在不在）。
        registry.Sweep(DateTimeOffset.UtcNow).Should().BeEmpty(
            "被控端一直在上报状态 ⇒ 这路会话活着。**画面静止不是断开** —— "
            + "展厅里定格在一张图上的机器走的正是这条路，而它完全正常。");

        registry.TryGet(sid).Should().NotBeNull(
            "会话必须还在表里。这一条比上面那句更硬：宿主里那个每 5 秒醒一次的巡检"
            + "**也是真的在跑的**，它要是先动手，上面那句 `BeEmpty` 会因为「已经没了」而假绿。");
    }

    // ═══════════════════════════════════════════════════════════
    // B / requested：被控端在回话 ⇒ 不该清（**零等待**）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void B_requested_session_that_keeps_acknowledging_is_not_swept()
    {
        var registry = Registry;

        // ⚠️ 会话**建在 30 秒前**：这样"建会话到现在"早就超过 RequestedTimeout（20 秒），
        //    而"最后一次回话"就是刚刚 —— 两个数被拉开，判据才分得出用的是哪一个。
        var longAgo = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30);
        var sid = registry.Open(4212, null, null, null, longAgo).Session!.SessionId;

        // 被控端**接到了**指令、在回话，但迟迟没有第一帧（挑屏 / 建编码器 / 切分辨率）。
        registry.AcceptAgentState(4212, State(sid, RemoteSessionStates.Requested, frames: 0));

        registry.Sweep(DateTimeOffset.UtcNow).Should().BeEmpty(
            "「多久还没等到被控端的**任何**状态上报」才是那条判据的本义 —— "
            + "它在回话就说明指令送到了。真正卡死的那条路由被控端自己的启动预算收场。");

        registry.TryGet(sid).Should().NotBeNull();
    }

    // ═══════════════════════════════════════════════════════════
    // C / 真断了的，一条都不许漏
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void C_requested_session_that_never_answers_is_swept()
    {
        var registry = Registry;
        var sid = registry.Open(4213, null, null, null, DateTimeOffset.UtcNow).Session!.SessionId;

        // 这条指令压根没送到（没人登录 / 托盘没跑 / 客户端版本过旧不认识它）。
        registry.Sweep(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(40)).Should().ContainSingle()
            .Which.SessionId.Should().Be(sid);

        // 🔥 顺带钉住"判死的产物带着机器 Id"：巡检的调用方要靠它给那台机器补发 stop，
        //    没有它，被控端会继续以为自己有一路在推（2026-10-19 那十分钟）。
        registry.Sweep(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(41))
            .Should().BeEmpty("已经被清掉的那一路不该被重复判死（否则会重复发 stop）。");

        registry.TryGet(sid).Should().BeNull(
            "没有任何回话的 requested 会话必须清掉 —— 它的存在会把 `_byAgent` 一直占着，"
            + "之后每一次建会话都被 SessionBusy 挡掉（「这台机器再也连不上」）。");
    }

    [Fact]
    public void D_session_that_stops_reporting_is_still_swept()
    {
        var registry = Registry;
        var sid = registry.Open(4214, null, null, null, DateTimeOffset.UtcNow).Session!.SessionId;

        registry.AcceptFrame(4214, Frame(sid, sequence: 1)).Ok.Should().BeTrue();

        // 一帧之后彻底安静：没有帧、也没有任何状态上报（服务被杀 / 连接断了）。
        registry.Sweep(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(40));

        registry.TryGet(sid).Should().BeNull(
            "帧与状态**两条**都停了才算断 —— 但两条都停了就必须清掉，"
            + "否则页面上会永远挂着一路「正在推流」的死会话。");
    }

    [Fact]
    public void E_audio_alone_is_not_a_liveness_signal()
    {
        var registry = Registry;
        var sid = registry.Open(4215, null, null, null, DateTimeOffset.UtcNow).Session!.SessionId;

        registry.AcceptFrame(4215, Frame(sid, sequence: 1)).Ok.Should().BeTrue();

        // 只推音频、没有任何状态上报。
        registry.AcceptAudio(4215, AudioFrame(sid, sequence: 1)).Ok.Should().BeTrue();

        registry.Sweep(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(40));

        registry.TryGet(sid).Should().BeNull(
            "音频**不是**活性信号（`ObserveAudio` 刻意不碰时间戳，那是一次明写的决定）："
            + "一台没有声卡的机器照样在正常推画面，反过来「只有声音在走」也不该"
            + "让一路死会话赖着不走。");
    }

    // ═══════════════════════════════════════════════════════════
    // F / 判死的产物必须带够"去通知那台机器"的信息
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥🔥 <b>这是 2026-10-19 那场"黑屏 → 断线 → 连不上"的根因所在</b>：
    /// <c>Sweep</c> 原先只回会话 Id，调用方拿不到机器 Id ⇒ 一条 stop 都发不出去。
    /// 于是中心把账本摘掉之后<b>什么也没发生</b>，而被控端仍然认为自己有一路在推，
    /// 从此拒绝这台机器上<b>所有</b>新会话（十分钟里四次建会话全被挡）。
    /// </summary>
    [Fact]
    public void F_swept_entry_carries_the_agent_id_so_the_stop_can_be_delivered()
    {
        var registry = Registry;
        const int agentId = 4216;
        var sid = registry.Open(agentId, null, null, null, DateTimeOffset.UtcNow).Session!.SessionId;

        var swept = registry.Sweep(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(40))
            .Should().ContainSingle().Which;

        swept.SessionId.Should().Be(sid);
        swept.AgentId.Should().Be(
            agentId,
            "收尸人要照着它去给那台机器补 stop —— 回一个空 Id 等于这条 stop 永远发不出去");
        swept.Reason.Should().NotBeNullOrWhiteSpace(
            "判死理由要能进日志，否则事后只能看到一句\"已结束\"，查不出是谁先放弃的");
    }

    // ═══════════════════════════════════════════════════════════
    // 造帧
    // ═══════════════════════════════════════════════════════════

    private static RemoteStreamFrame Frame(string sessionId, long sequence)
        => new(
            new RemoteStreamFrameHeader(
                SessionId: sessionId,
                Sequence: sequence,
                Codec: RemoteCodecs.H264,
                IsKeyframe: sequence == 1,
                Width: 1920,
                Height: 1080,
                CapturedUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            new byte[] { 0x00, 0x00, 0x01, 0x65 });

    private static RemoteAudioFrame AudioFrame(string sessionId, long sequence)
        => new(
            new RemoteAudioFrameHeader(
                SessionId: sessionId,
                Sequence: sequence,
                Codec: RemoteAudioCodecs.Opus,
                SampleRate: 48000,
                Channels: 1,
                SamplesPerFrame: 960,
                CapturedUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CapturedMonoMs: 0),
            new byte[] { 0x01, 0x02, 0x03 });

    private static AgentRemoteSessionStateDto State(string sessionId, string state, long frames)
        => new(
            SessionId: sessionId,
            State: state,
            Reason: null,
            Frames: frames,
            Bytes: frames * 4,
            Fps: 0,
            Dropped: 0);
}
