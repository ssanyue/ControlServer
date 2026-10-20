using ControlServer.Contracts;
using ControlServer.Server.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// <b>中心侧的帧片重组</b>（M5.4-c① 的分片 / chunking）——
/// 用例经真实的 <see cref="RemoteSessionRegistry"/> 灌片，走的正是生产那条路
/// （<c>PushRemoteFrameChunk</c> → <c>AcceptFrameChunk</c> → 归属校验 → 重组 → <c>AcceptFrame</c>）。
///
/// <para>
/// 🔥🔥 <b>而片本身由生产那一份切片器切出来</b>（<see cref="RemoteFrameChunker.Slice"/>）——
/// 本文件<b>刻意没有</b>任何自己的"把一帧切成几片"的代码。
/// 这不是省事，而是<b>这组用例到底证明了什么</b>的分水岭：
/// 如果用例自己再切一份，那"3 片拼回来逐字节相等"证明的只是
/// 「<b>用例切的片</b>能被拼回来」，而生产切片器把最后一片多切一个字节时，
/// 这组用例会<b>全绿</b> —— 那正是分片最典型的一种坏法（长度对、内容错）。
/// </para>
///
/// <para>
/// ⚠️ <b>有几种片生产代码<b>永远造不出来</b>，所以只能在本文件里手搓</b>：
/// 片数荒唐（&gt; <see cref="RemoteStreamLimits.MaxChunkCount"/>）、下标越界、
/// 片数为 1、同序号下片数前后不一致、累计字节超单帧上限。
/// 它们对应的是"对方是个坏掉的实现 / 连接被污染"，而不是"我们的切片器算错了"
/// —— 所以它们<b>必须</b>绕过切片器，否则就测不到那条防御。
/// </para>
///
/// <para>
/// 🔴 <b>本组用例守的第一条纪律是「不许抛」</b>：这条路直通 SignalR 的调用栈，
/// 抛出去的后果是<b>掐断整条 Hub 连接</b>（心跳 / 遥测 / 配置 / 音频全在它上面）。
/// 而片越界 / 重复 / 前后不一致这些事，最坏的后果应该只是<b>丢一帧</b>。
/// 所以下面每一条"非法输入"的用例都同时断言两件事：<b>没抛</b>，且<b>只是丢了那一帧</b>。
/// </para>
///
/// <para>
/// 🔥 <b>第二条纪律：丢一帧必须换来一次"请补关键帧"</b>。
/// H.264 的 P 帧参考前面的帧，丢一帧就在参考链上戳一个洞 ——
/// 观看端从那一刻起解出来的每一帧都是花的，<b>而解码器不会报错</b>。
/// 而这次丢帧发生在<b>中心</b>，被控端无从知道 ⇒ 必须由应答带回去。
/// 没有它，恢复代价是<b>最多一个 GOP</b>（gopSize 60、默认 12 fps ⇒ 5 秒花屏）。
/// </para>
/// </summary>
public sealed class RemoteFrameReassemblyTests : IDisposable
{
    /// <summary>
    /// 用例灌片时用的<b>片上限</b>（100 字节）。
    ///
    /// <para>
    /// 🔥 <b>为什么用例可以传一个与生产不同的片上限</b>：因为"切成几片"这件事
    /// 由<b>同一个函数</b>算（<see cref="RemoteFrameChunker.Slice"/>），
    /// 传 100 与传 5.75 MiB 走的是<b>同一条代码路径</b>。
    /// 不这么做的话，要验"多片能拼回来"就得先分配一份 6 MB 以上的假码流 ——
    /// 那既慢，又让人把注意力放在"造数据"而不是"验协议"上。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 它与 <see cref="Sequence"/> 的长度是<b>配套的</b>：300 字节 ⇒ <b>3</b> 片。
    /// 每条用例都会先断言片数，免得这两处悄悄漂开。
    /// </para>
    /// </summary>
    private const int SmallChunkBytes = 100;

    private readonly ControlServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private RemoteSessionRegistry Registry => _factory.Services.GetRequiredService<RemoteSessionRegistry>();

    // ═══════════════════════════════════════════════════════════
    // ① 拼得回来：逐字节相等
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>核心用例</b>：3 片拼回的那一帧，与"原本那一帧"<b>逐字节相等</b>，且它真的进了转发链。
    /// </summary>
    /// <remarks>
    /// ⚠️ 断言刻意落在<b>字节</b>上，而不是"长度对不对"：长度对、内容错正是分片最典型的一种坏法
    /// （按到达顺序拼、或者某一片的偏移算错），而那种坏法在**任何一层都不会报错** ——
    /// 它表现为"画面从某个位置开始花"，看起来像编码器坏了。
    /// </remarks>
    [Fact]
    public void A_three_chunks_reassemble_byte_for_byte()
    {
        var registry = Registry;
        var sid = registry.Open(4311, null, null, null, DateTimeOffset.UtcNow).Session!.SessionId;

        var whole = Sequence(300);
        var pieces = Pieces(sid, sequence: 1, whole);

        pieces.Should().HaveCount(3, "这条组用例的前提就是 300 字节被切成 3 片 —— 切不出 3 片的话，下面每一项都测不到");

        registry.AcceptFrameChunk(4311, pieces[0]).Ok.Should().BeTrue();
        registry.AcceptFrameChunk(4311, pieces[1]).Ok.Should().BeTrue();

        // 前两片：还没有帧到过中心 —— 半帧不是帧。
        registry.TryGet(sid)!.Stats?.FramesReceived.Should().Be(
            0,
            "只收到 2/3 片时不该有任何一帧被算成「已收到」—— 否则页面上的帧率会变成「按片计数」的两三倍");

        registry.AcceptFrameChunk(4311, pieces[2]).Ok.Should().BeTrue();

        var latest = registry.Latest(sid);

        latest.Should().NotBeNull("三片齐了就必须拼出一帧交给转发链（新观众靠它对上第一帧）");
        latest!.Data.Should().Equal(whole, "拼回来的字节必须与原来那帧**逐字节相等** —— 长度对而内容错是最难查的一种坏法");
        latest.Header.Sequence.Should().Be(1);
        latest.Header.Width.Should().Be(1920, "每一片都带完整帧头 ⇒ 拼出来的帧头不该丢字段");

        registry.TryGet(sid)!.Stats!.FramesReceived.Should().Be(1, "拼齐了才是一帧");
        registry.TryGet(sid)!.Stats!.ReassemblyDroppedFrames.Should().Be(0, "一切正常时不该有任何重组丢帧");
        registry.TryGet(sid)!.Stats!.ReassembledFrames.Should().Be(
            1,
            "「进来一帧」与「其中有一帧是拼出来的」是两个数 —— 后者是现场判断"
            + "「这一路到底走没走分片」的唯一依据（本机没有 8K 屏，普通帧永远走老路）");
    }

    /// <summary>
    /// 片可以**乱序**到（按<b>下标</b>拼，不按到达顺序拼）。
    ///
    /// <para>
    /// 🔥 这一条看着"用不上"（真实链路是单条顺序连接），但它守的是一个<b>极易写出的错误</b>：
    /// 把"顺序到"当成<b>协议的性质</b>去拼字节。那样写出来的代码在真实链路上跑得完全正常，
    /// 而一旦有任何一次重发/重排，产出的就是<b>位置全错、长度全对</b>的码流。
    /// </para>
    /// </summary>
    [Fact]
    public void B_pieces_may_arrive_out_of_order()
    {
        var registry = Registry;
        var sid = registry.Open(4312, null, null, null, DateTimeOffset.UtcNow).Session!.SessionId;

        var whole = Sequence(300);
        var pieces = Pieces(sid, sequence: 1, whole);

        registry.AcceptFrameChunk(4312, pieces[2]).Ok.Should().BeTrue();
        registry.AcceptFrameChunk(4312, pieces[0]).Ok.Should().BeTrue();
        registry.AcceptFrameChunk(4312, pieces[1]).Ok.Should().BeTrue();

        registry.Latest(sid)!.Data.Should().Equal(
            whole,
            "按**下标**拼而不是按到达顺序拼 —— 顺序是链路的性质，不是协议的性质");
    }

    // ═══════════════════════════════════════════════════════════
    // ② 缺片 ⇒ 丢帧 + 计数 + 请补关键帧
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>缺一片 ⇒ 丢这一帧、计数、并在应答上带"请补关键帧"。</b>
    /// </summary>
    /// <remarks>
    /// 🔥 这一条是分片<b>唯一的</b>新增失败模式，也是"分片做完之后 8K 会不会一路花屏"的答案。
    /// 判据用"新帧的序号到来"触发（单槽的全部代价）：上一个帧的片缺了，它不会自己回来。
    /// </remarks>
    [Fact]
    public void C_a_missing_piece_drops_the_frame_and_asks_for_a_keyframe()
    {
        var registry = Registry;
        var sid = registry.Open(4313, null, null, null, DateTimeOffset.UtcNow).Session!.SessionId;

        // 帧 #1：3 片，只送到第 0、1 片（第 2 片丢了）。
        var first = Pieces(sid, sequence: 1, Sequence(300));

        registry.AcceptFrameChunk(4313, first[0]).Ok.Should().BeTrue();
        registry.AcceptFrameChunk(4313, first[1]).Ok.Should().BeTrue();

        // 帧 #2 的第一片到了 ⇒ 帧 #1 永久丢失。
        var second = Pieces(sid, sequence: 2, Sequence(200));

        var ack = registry.AcceptFrameChunk(4313, second[0]);

        ack.Ok.Should().BeTrue(
            "「还没拼齐」不是拒绝 —— 拒绝是刹车信号，会让被控端当场停掉整路流，"
            + "而这里最坏只是一帧丢了");

        ack.NeedKeyframe.Should().BeTrue(
            "中心丢了一帧 ⇒ 必须请被控端补一个关键帧：H.264 参考链上戳了洞，"
            + "观看端从这一刻起解出来的每一帧都是花的，而**解码器不会报错**。"
            + "这件事发生在中心、被控端无从知道 ⇒ 只能靠这个标志带回去");

        var stats = registry.TryGet(sid)!.Stats!;

        stats.ReassemblyDroppedFrames.Should().Be(1, "中心丢的帧要**单独**计数（不是并进被控端那个 DroppedFrames）");
        stats.DroppedFrames.Should().Be(0, "被控端一帧都没丢 —— 这两个数合并了，「它编不动」与「链路上掉了」就再也分不开");
        stats.FramesReceived.Should().Be(0, "没有任何一帧拼齐过");

        // 帧 #2 补齐 ⇒ 它必须**完好地**到达（丢的是上一帧，不是这一路）。
        registry.AcceptFrameChunk(4313, second[1]).Ok.Should().BeTrue();

        registry.Latest(sid)!.Data.Should().Equal(Sequence(200), "第二帧两片齐了 ⇒ 拼回来的必须与它自己逐字节相等");
    }

    // ═══════════════════════════════════════════════════════════
    // ③ 非法输入：一律丢帧 + 计数，**绝不抛**
    //    （下面这三条**必须**手搓 —— 生产切片器造不出这些形状，见类注释）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void D_an_absurd_chunk_count_is_dropped_not_thrown()
    {
        var registry = Registry;
        var sid = registry.Open(4314, null, null, null, DateTimeOffset.UtcNow).Session!.SessionId;

        var data = Sequence(64);

        // ChunkCount 荒唐（比防御性上限还大）：它只可能来自一个坏掉的实现。
        var ack = registry.AcceptFrameChunk(
            4314,
            new RemoteStreamFrameChunk(Header(sid, 1), ChunkIndex: 0, ChunkCount: RemoteStreamLimits.MaxChunkCount + 1, data));

        ack.Ok.Should().BeTrue("坏掉的片不该变成一次拒绝（那会让被控端停掉整路流）");
        ack.NeedKeyframe.Should().BeTrue("这一帧已经不可能拼回来了 ⇒ 请补关键帧");

        registry.TryGet(sid)!.Stats!.ReassemblyDroppedFrames.Should().Be(1);

        // 下标越界、片数为 1（整帧不许走这条路）—— 同样只丢帧。
        foreach (var bad in new[]
                 {
                     new RemoteStreamFrameChunk(Header(sid, 2), ChunkIndex: -1, ChunkCount: 3, data),
                     new RemoteStreamFrameChunk(Header(sid, 3), ChunkIndex: 3, ChunkCount: 3, data),
                     new RemoteStreamFrameChunk(Header(sid, 4), ChunkIndex: 0, ChunkCount: 1, data),
                 })
        {
            var rejected = bad;
            AgentIngestAckDto verdict = null!;

            // ⚠️ 只调**一次**（把结果接住再断言）—— 调两次会把计数也翻倍，
            //    而那是用例自己造出来的偏差。
            Action act = () => verdict = registry.AcceptFrameChunk(4314, rejected);

            act.Should().NotThrow(
                "这条路直通 SignalR 的调用栈 —— 抛出去掐的是**整条连接**（心跳/遥测/配置/音频全在它上面），"
                + "而这里最坏的后果应该只是丢一帧");

            verdict.NeedKeyframe.Should().BeTrue();
        }

        registry.TryGet(sid)!.Stats!.ReassemblyDroppedFrames.Should().Be(4);
    }

    [Fact]
    public void E_a_duplicated_piece_drops_the_frame()
    {
        var registry = Registry;
        var sid = registry.Open(4315, null, null, null, DateTimeOffset.UtcNow).Session!.SessionId;

        var pieces = Pieces(sid, sequence: 1, Sequence(300));

        registry.AcceptFrameChunk(4315, pieces[0]).NeedKeyframe.Should().BeFalse(
            "第一片：开槽，没有任何一帧被丢");

        registry.AcceptFrameChunk(4315, pieces[0]).NeedKeyframe.Should().BeTrue(
            "同一片收到两次 ⇒ 这一帧已不可信、被丢掉 ⇒ **它就是一次丢帧**，所以要请关键帧"
            + "（片里到底是什么已经说不清了，继续拼只会拼出一个位置错的码流）");

        registry.TryGet(sid)!.Stats!.ReassemblyDroppedFrames.Should().Be(1);

        // 丢掉之后槽是干净的：同一序号的后续片会开一个**新**帧，而不是污染那个坏帧。
        registry.AcceptFrameChunk(4315, Pieces(sid, sequence: 2, Sequence(10), maxChunkBytes: 5)[0]).Ok.Should().BeTrue();
    }

    [Fact]
    public void F_an_inconsistent_chunk_count_drops_the_frame()
    {
        var registry = Registry;
        var sid = registry.Open(4316, null, null, null, DateTimeOffset.UtcNow).Session!.SessionId;

        var data = Sequence(64);

        registry.AcceptFrameChunk(4316, new RemoteStreamFrameChunk(Header(sid, 1), 0, 3, data)).NeedKeyframe.Should().BeFalse();

        var ack = registry.AcceptFrameChunk(4316, new RemoteStreamFrameChunk(Header(sid, 1), 1, 2, data));

        ack.NeedKeyframe.Should().BeTrue(
            "同一个序号下前后片数不一致 ⇒ 这两片根本不是同一帧，这一帧不能要");

        registry.TryGet(sid)!.Stats!.ReassemblyDroppedFrames.Should().Be(1);
    }

    /// <summary>
    /// 累计字节超过<b>单帧防呆上限</b> ⇒ 丢帧（而不是把内存吃光）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它是"防呆"那道闸门的唯一用例：真实的片不可能这么大（每片 ≤ <c>MaxChunkPayloadBytes</c>），
    /// 而这条闸门守的是"对方（或一条被污染的连接）报出一个荒唐的总量"。
    /// 判据取"累计和"而不是"单片大小"，正是因为真正的风险在**总量**上。
    /// <para>
    /// ⚠️ 它也<b>只能手搓</b>：要触发它必须先送一片 64 MiB 的片，而生产切片器
    /// （按 <c>MaxChunkPayloadBytes</c> 切）永远切不出这么大的片 ——
    /// 这正是"这条闸门防的不是我们的切片器算错"。顺便它也是本组<b>唯一分配大内存</b>的用例。
    /// </para>
    /// </remarks>
    [Fact]
    public void G_an_oversize_total_drops_the_frame()
    {
        var registry = Registry;
        var sid = registry.Open(4317, null, null, null, DateTimeOffset.UtcNow).Session!.SessionId;

        registry.AcceptFrameChunk(
            4317,
            new RemoteStreamFrameChunk(Header(sid, 1), 0, 2, new byte[RemoteStreamLimits.MaxFrameBytes - 1]))
            .NeedKeyframe.Should().BeFalse();

        var ack = registry.AcceptFrameChunk(
            4317,
            new RemoteStreamFrameChunk(Header(sid, 1), 1, 2, new byte[2]));

        ack.NeedKeyframe.Should().BeTrue(
            $"累计 {RemoteStreamLimits.MaxFrameBytes + 1} 字节 > 单帧上限 "
            + $"{RemoteStreamLimits.Describe(RemoteStreamLimits.MaxFrameBytes)} ⇒ 丢帧，"
            + "而不是把这份内存留在槽里");

        registry.TryGet(sid)!.Stats!.ReassemblyDroppedFrames.Should().Be(1);
        registry.Latest(sid).Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════
    // ④ 迟到 / 重复的片：**不算丢帧**（否则一次无害的重复会换来一路 IDR）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void H_a_late_piece_of_a_finished_frame_is_ignored()
    {
        var registry = Registry;
        var sid = registry.Open(4318, null, null, null, DateTimeOffset.UtcNow).Session!.SessionId;

        var pieces = Pieces(sid, sequence: 1, Sequence(300));

        foreach (var piece in pieces) registry.AcceptFrameChunk(4318, piece);

        registry.Latest(sid)!.Data.Should().Equal(Sequence(300));

        // 重复的第 1 片（迟到的重发）。
        var ack = registry.AcceptFrameChunk(4318, pieces[1]);

        ack.Ok.Should().BeTrue();
        ack.NeedKeyframe.Should().BeFalse(
            "重复片**不是**丢帧的证据 —— 把它算成丢帧会让每一次无害的重发都换来一路 IDR，"
            + "而 IDR 正是 8K 上最贵的东西");

        registry.TryGet(sid)!.Stats!.ReassemblyDroppedFrames.Should().Be(0, "没有任何一帧真的丢了");
        registry.TryGet(sid)!.Stats!.FramesReceived.Should().Be(1, "也不该被算成第二帧");
    }

    // ═══════════════════════════════════════════════════════════
    // ⑤ 归属与会话校验（**每一片都做**）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void I_a_chunk_from_another_machine_is_rejected()
    {
        var registry = Registry;
        var sid = registry.Open(4319, null, null, null, DateTimeOffset.UtcNow).Session!.SessionId;

        var ack = registry.AcceptFrameChunk(9999, Pieces(sid, sequence: 1, Sequence(64), maxChunkBytes: 32)[0]);

        ack.Ok.Should().BeFalse("归属校验在**每一片**上都要做 —— 不是只在第 0 片做");
        ack.Status.Should().Be("not-owner");
    }

    [Fact]
    public void J_a_chunk_for_an_unknown_session_is_rejected()
    {
        var registry = Registry;

        var ack = registry.AcceptFrameChunk(
            4320,
            Pieces("no-such-session", sequence: 1, Sequence(64), maxChunkBytes: 32)[0]);

        ack.Ok.Should().BeFalse("会话已经没了 ⇒ 报 false 让被控端当场停下这路流（继续编只是白烧它的 CPU）");
        ack.Status.Should().Be(RemoteErrorCodes.SessionNotFound);
    }

    // ═══════════════════════════════════════════════════════════
    // ⑥ 一片也算"这个被控端还在"（明写的表态，见 Entry.TouchAgentActivity）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>收到一片就是"这个被控端还在"的证据</b> —— 与收到整帧同级。
    ///
    /// <para>
    /// ⚠️ 这条用例能<b>零等待</b>地把这件事测出来，用的是与那组 Sweep 用例同一个技巧：
    /// 会话<b>建在 60 秒前</b>（远超 requested 的 20 秒判据），而片是<b>刚刚</b>到的 ——
    /// 判据只要用的是"最后一次回话"，两个数就被拉得足够开。
    /// </para>
    /// </summary>
    [Fact]
    public void K_a_chunk_counts_as_liveness()
    {
        var registry = Registry;

        var longAgo = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(60);
        var sid = registry.Open(4321, null, null, null, longAgo).Session!.SessionId;

        // 只有一片（帧还差一片）—— 没有整帧、也没有任何状态上报。
        registry.AcceptFrameChunk(4321, Pieces(sid, sequence: 1, Sequence(64), maxChunkBytes: 32)[0]).Ok.Should().BeTrue();

        registry.Sweep(DateTimeOffset.UtcNow).Should().BeEmpty(
            "一片也是这个被控端在回话。若这里被清掉，说明分片这条新上行**没有**在"
            + "「算不算活性」那件事上表态 —— 而那种漏的表态在现场是"
            + "「某台机器每 15 秒掉一次线」，离这行代码隔着一条连接与两个进程");

        registry.TryGet(sid).Should().NotBeNull();
    }

    /// <summary>
    /// 🔥 <b>「进来几帧」与「其中几帧是拼出来的」必须是两个数。</b>
    ///
    /// <para>
    /// 这条用例守的是 <c>ReassembledFrames</c> 这个字段<b>存不存在得有意义</b>：
    /// 如果它只是 <c>FramesReceived</c> 的别名，那它不但没有信息量，还会让人
    /// 误以为"分片在跑"—— 而本机（2160×1440）的<b>每一帧都走老路</b>，
    /// 正确的读数应该是 <c>FramesReceived</c> 涨、<c>ReassembledFrames</c> 恒 0。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 这条同时钉住"整帧<b>不</b>经过重组器"：老路上那一帧若被算进重组数，
    /// 现场就会看到 "重组 12 帧/秒" 而实际一片都没切过 —— 于是有人去查分片，
    /// 而真因在别处。
    /// </para>
    /// </summary>
    [Fact]
    public void L_reassembled_frames_are_counted_apart_from_frames_that_arrived_whole()
    {
        var registry = Registry;
        var sid = registry.Open(4322, null, null, null, DateTimeOffset.UtcNow).Session!.SessionId;

        // ① 一整帧（老路 —— 2160×1440 与 4K 的常态）
        registry.AcceptFrame(4322, new RemoteStreamFrame(Header(sid, sequence: 1), Sequence(64)))
            .Ok.Should().BeTrue();

        registry.TryGet(sid)!.Stats!.ReassembledFrames.Should().Be(
            0,
            "整帧根本没有经过重组器 —— 这个数要是动了，说明统计把两条路混成了一件事");

        // ② 一帧走分片（300 字节 / 100 ⇒ 3 片）
        var pieces = Pieces(sid, sequence: 2, Sequence(300));

        pieces.Should().HaveCount(3, "前提：这一帧必须真的被切成 3 片，否则「拼出来的帧数」测不到");

        foreach (var piece in pieces) registry.AcceptFrameChunk(4322, piece).Ok.Should().BeTrue();

        var stats = registry.TryGet(sid)!.Stats!;

        stats.FramesReceived.Should().Be(2, "进来两帧（一帧整的、一帧拼的）");
        stats.ReassembledFrames.Should().Be(1, "其中只有一帧是拼出来的");
    }

    // ═══════════════════════════════════════════════════════════
    // 造数据
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 造一段<b>长度够切好几片</b>、而且内容不重复的码流。
    /// <para>
    /// ⚠️ 内容刻意用序号填充而不是 <c>new byte[n]</c>（全 0）：全 0 的字节流里
    /// "拼错顺序"是**看不出来**的 —— 而位置全错、长度全对正是这里要防的坏法。
    /// </para>
    /// </summary>
    private static byte[] Sequence(int length)
    {
        var buffer = new byte[length];

        for (var i = 0; i < length; i++) buffer[i] = (byte)(i % 251);

        return buffer;
    }

    /// <summary>
    /// <b>用它自己那一份切片器</b>把一帧折成几片（<see cref="RemoteFrameChunker.Slice"/>）
    /// —— 本文件<b>没有</b>第二份切片实现，这是刻意的。
    /// </summary>
    /// <param name="maxChunkBytes">
    /// 片上限。<b>不传</b> = 协议上限（<see cref="RemoteStreamLimits.MaxChunkPayloadBytes"/>，
    /// 那样 300 字节只会切出 1 片，测不到多片路径）；用例传 <see cref="SmallChunkBytes"/> 这类小值。
    /// </param>
    private static IReadOnlyList<RemoteStreamFrameChunk> Pieces(
        string sessionId,
        long sequence,
        byte[] whole,
        int maxChunkBytes = SmallChunkBytes)
        => RemoteFrameChunker.Slice(new RemoteStreamFrame(Header(sessionId, sequence), whole), maxChunkBytes);

    private static RemoteStreamFrameHeader Header(string sessionId, long sequence)
        => new(
            SessionId: sessionId,
            Sequence: sequence,
            Codec: RemoteCodecs.H264,
            IsKeyframe: sequence == 1,
            Width: 1920,
            Height: 1080,
            CapturedUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
