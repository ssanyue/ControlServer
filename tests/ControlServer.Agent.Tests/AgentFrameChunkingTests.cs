using ControlServer.Agent.Transport;
using ControlServer.Contracts;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// <b>被控端这一侧的分片</b>（M5.4-c①）—— 与中心那组（<c>RemoteFrameReassemblyTests</c>）
/// 合起来是"8K 能不能跑"这件事里<b>唯一能被本机验证的另一半</b>
/// （另一半 = 8K 真机端到端，本机显示器只有 2160×1440，物理上做不到）。
///
/// <para>
/// 🔥🔥 <b>本文件里没有一行"把一帧切成几片"的代码</b> —— 片由
/// <see cref="RemoteFrameChunker.Slice"/> 切，而<b>生产那条路调的也是它</b>。
/// 这就是"被控端切片与用例共用唯一一处实现"的落地方式：
/// 用例通过一个假发射器（<c>PushFrameAsync</c> 的 <c>invoke</c> 参数）看见
/// <b>生产代码到底发出去了几条什么消息</b>，而不是自己复现一遍切法。
/// </para>
///
/// <para>
/// 🔴 <b>本组用例守的三条判据，都是"静默错"那一类</b>（错了不会报错，只会慢或者花）：
/// </para>
/// <list type="number">
///   <item><b>惰性</b>：2160×1440 与 4K 必须<b>仍然一整帧一条消息</b>。
///         破了的话症状是"每一帧多一次全量拷贝" —— 没有任何一层会报错，只会变慢；</item>
///   <item><b>片数</b>：<c>ceil(帧字节 / 片上限)</c>。算少了 ⇒ 中心那条消息装不下 ⇒
///         <b>掐断整条 Hub 连接</b>（心跳 / 遥测 / 配置 / 音频全在它上面）；</item>
///   <item><b>内容</b>：各片按序拼起来必须<b>逐字节等于</b>原码流。
///         错了的症状是花屏，而长度完全正常 —— 最难查的一种。</item>
/// </list>
/// </summary>
public sealed class AgentFrameChunkingTests
{
    /// <summary>一段内容不重复的假码流（全 0 的字节流里"拼错顺序"看不出来）。</summary>
    private static byte[] Payload(int length)
    {
        var buffer = new byte[length];

        for (var i = 0; i < length; i++) buffer[i] = (byte)(i % 251);

        return buffer;
    }

    private static RemoteStreamFrame Frame(int length, long sequence = 1)
        => new(
            new RemoteStreamFrameHeader(
                SessionId: "s-1",
                Sequence: sequence,
                Codec: RemoteCodecs.H264,
                IsKeyframe: true,
                Width: 7680,
                Height: 4320,
                CapturedUnixMs: 1_700_000_000_000),
            Payload(length));

    // ═══════════════════════════════════════════════════════════
    // ① 惰性：装得下的帧必须**一个字都不变**地走老路
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>本组最重要的一条</b>：2160×1440 与 4K 的帧<b>仍然一整帧一条消息</b>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 判据是<b>发出去了哪几条消息</b>，不是"片数是不是 1" ——
    /// 后者能靠"切了但只发一片"骗过去，而那正是要防的：
    /// <c>Slice</c> <b>会拷贝</b>字节，无条件调它等于给每一帧加一次全量拷贝。
    /// </remarks>
    [Fact]
    public async Task A_a_frame_that_fits_is_still_one_whole_message()
    {
        // 2160×1440 的 IDR 实测 306.9 KB；4K 是它的 2.67 倍像素。
        foreach (var bytes in new[] { 306_900, 819 * 1024, RemoteStreamLimits.MaxChunkPayloadBytes })
        {
            var hub = new FakeHub();

            await AgentConnection.PushFrameAsync(hub.InvokeAsync, Frame(bytes), CancellationToken.None);

            hub.Methods.Should().Equal(
                new[] { AgentHubClientMethods.PushRemoteFrame },
                $"{RemoteStreamLimits.Describe(bytes)} 的帧装得进一条消息 ⇒ 不许切片、不许拷贝"
                + "（2160×1440 与 4K 的行为一个字都不变，这正是这批改动敢进的理由）");
        }
    }

    /// <summary>正好等于一片上限 ⇒ 仍然是<b>一片</b>（与 <c>FitsInPipe</c> 同一条边界语义）。</summary>
    [Fact]
    public async Task B_exactly_one_chunk_is_still_the_old_path()
    {
        var hub = new FakeHub();

        await AgentConnection.PushFrameAsync(
            hub.InvokeAsync, Frame(RemoteStreamLimits.MaxChunkPayloadBytes), CancellationToken.None);

        hub.Methods.Should().Equal(
            new[] { AgentHubClientMethods.PushRemoteFrame },
            "「正好装得下」是**一片**，而一片必须走老路 —— 中心的 `RemoteFrameReassembler` "
            + "明确拒绝 ChunkCount == 1 的片（放它过去等于在链路上开了第二条整帧通道）");
    }

    // ═══════════════════════════════════════════════════════════
    // ② 多一个字节就必须切片，且片数分毫不差
    // ═══════════════════════════════════════════════════════════

    /// <summary>上限 + 1 字节 ⇒ <b>2 片</b>（差的就是那一个字节）。</summary>
    [Fact]
    public async Task C_one_byte_over_the_limit_becomes_two_chunks()
    {
        var hub = new FakeHub();

        await AgentConnection.PushFrameAsync(
            hub.InvokeAsync, Frame(RemoteStreamLimits.MaxChunkPayloadBytes + 1), CancellationToken.None);

        hub.Methods.Should().Equal(
            new[] { AgentHubClientMethods.PushRemoteFrameChunk, AgentHubClientMethods.PushRemoteFrameChunk });

        var chunks = hub.Chunks();

        chunks.Select(c => c.ChunkCount).Should().OnlyContain(count => count == 2);
        chunks.Select(c => c.ChunkIndex).Should().Equal(0, 1);
        chunks[0].Data.Length.Should().BeLessOrEqualTo(
            RemoteStreamLimits.MaxChunkPayloadBytes,
            "每一片都必须塞得进一条 SignalR 消息 —— 破了就是**掐断整条连接**");
    }

    /// <summary>
    /// <b>6 MB 的假帧</b> ⇒ 条数 = <c>ceil(len / 片上限)</c>，且拼接<b>逐字节相等</b>。
    /// </summary>
    /// <remarks>
    /// 🔥 这一条同时钉住两件事：<b>条数</b>（少一条 ⇒ 中心那条消息超限 ⇒ 掐连接）
    /// 与<b>内容</b>（顺序或偏移错 ⇒ 长度对、内容错 ⇒ 花屏而无人报错）。
    /// 片数<b>不写死</b>，而是与 <see cref="RemoteFrameChunker.CountFor"/> 对拍 ——
    /// 写死一个数字的话，两个上限里任何一个动了，这条用例都会以一个莫名其妙的
    /// "2 vs 3" 失败，而看不出是谁动的手。
    /// </remarks>
    [Fact]
    public async Task D_a_six_megabyte_frame_slices_into_the_arithmetic_number_of_chunks()
    {
        const int size = 6 * 1024 * 1024;

        var hub = new FakeHub();

        await AgentConnection.PushFrameAsync(hub.InvokeAsync, Frame(size), CancellationToken.None);

        var expected = RemoteFrameChunker.CountFor(size);
        var chunks = hub.Chunks();

        expected.Should().BeGreaterThan(1, "6 MB 必须装不进一条消息 —— 否则这条用例根本没测到分片");
        chunks.Should().HaveCount(expected);
        chunks.Select(c => c.ChunkIndex).Should().Equal(Enumerable.Range(0, expected));
        chunks.Select(c => c.ChunkCount).Should().OnlyContain(count => count == expected);

        chunks.SelectMany(c => c.Data).Should().Equal(
            Payload(size),
            "按序拼起来必须与原来的码流**逐字节相等** —— 长度对而内容错是最难查的一种坏法");

        chunks.Select(c => c.Data.Length).Should().OnlyContain(
            length => length > 0 && length <= RemoteStreamLimits.MaxChunkPayloadBytes);
    }

    // ═══════════════════════════════════════════════════════════
    // ③ 刹车：中心说停就停，**不许**因为"已经发了几片"而继续
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 中间某一片被中心拒绝 ⇒ <b>抛出</b>（这路流停掉），且<b>后面那些片不再发</b>。
    /// </summary>
    /// <remarks>
    /// 🔥 "不再发"这句是这条用例的全部价值：<c>Rejected</c> 的含义是
    /// "会话已没了 / 不属于你"，继续发只是白烧被控端的 CPU 与这条连接。
    /// 而它极易被写成"日志警告一下、循环继续" —— 那种写法在演示时看不出任何区别。
    /// </remarks>
    [Fact]
    public async Task E_a_rejected_chunk_stops_the_rest_and_throws()
    {
        const int size = 6 * 1024 * 1024;

        var hub = new FakeHub
        {
            // 第 2 片（下标 1）被中心拒了。
            Responder = (_, payload) =>
                payload is RemoteStreamFrameChunk { ChunkIndex: 1 }
                    ? AgentIngestAckDto.Rejected(RemoteErrorCodes.SessionNotFound, "会话不存在或已结束。")
                    : AgentIngestAckDto.Success(),
        };

        Func<Task> act = () => AgentConnection.PushFrameAsync(hub.InvokeAsync, Frame(size), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*第 2/*片*", "异常里要说清是**哪一片**被拒了 —— 现场只有这一句话能指向真因");

        hub.Chunks().Should().HaveCount(2, "第 1 片照发、第 2 片被拒 ⇒ 第 3 片起必须**一片都不发**");
    }

    /// <summary>整帧那条路上的拒绝同样要抛（与分片路同一条纪律，改动不许把它弄丢）。</summary>
    [Fact]
    public async Task F_a_rejected_whole_frame_still_throws()
    {
        var hub = new FakeHub
        {
            Responder = (_, _) => AgentIngestAckDto.Rejected("not-owner", "这个会话不属于你这台机器。"),
        };

        Func<Task> act = () => AgentConnection.PushFrameAsync(hub.InvokeAsync, Frame(64 * 1024), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ═══════════════════════════════════════════════════════════
    // ④ 切片器本身（唯一那一处实现）的几个边界
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <c>Slice</c> 的片数必须与 <c>CountFor</c> <b>说同一个数</b>（同处实现的内部一致性），
    /// 且每一组片拼起来都与原码流<b>逐字节相等</b>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这一组用<b>很小的片上限</b>（7 / 100 / 4096 字节）把边界情形一次扫完：
    /// 整除、余 1、片数从 1 跳到 2 的那一格、以及"最后一片是短的"。
    /// 它们都不需要大内存，却能碰到 <see cref="RemoteFrameChunker.Slice"/> 里每一个分支。
    /// </remarks>
    [Fact]
    public void G_slice_count_always_agrees_with_count_for()
    {
        foreach (var max in new[] { 7, 100, 4096, RemoteStreamLimits.MaxChunkPayloadBytes })
        {
            foreach (var size in new[] { 1, 6, 7, 8, 100, 101, 4096 })
            {
                var frame = Frame(size);
                var expected = RemoteFrameChunker.CountFor(size, max);
                var pieces = RemoteFrameChunker.Slice(frame, max);

                pieces.Should().HaveCount(
                    expected,
                    $"{size} 字节按每片 {max} 切 —— 「算出来几片」与「真的切出几片」不许各说各的");

                RemoteFrameChunker.NeedsChunking(size, max).Should().Be(
                    expected > 1,
                    "「要不要走分片」的定义就是「片数 > 1」，不许再有第二种口径");

                pieces.Select(piece => piece.ChunkIndex).Should().Equal(Enumerable.Range(0, expected));
                pieces.Select(piece => piece.ChunkCount).Should().OnlyContain(count => count == expected);
                pieces.Select(piece => piece.Data.Length).Should().OnlyContain(
                    length => length > 0 && length <= max);

                pieces.SelectMany(piece => piece.Data).Should().Equal(
                    Payload(size),
                    $"{size} 字节按每片 {max} 切出来再拼回去，必须与原来**逐字节相等**");
            }
        }
    }

    /// <summary>空码流 ⇒ <b>一片都不切</b>（不是"一片空的"）。</summary>
    /// <remarks>
    /// ⚠️ 空片在两端都是明确的非法形状：中心的 <c>RemoteFrameReassembler</c> 拒绝它，
    /// 而前端喂一个 0 字节的 <c>EncodedVideoChunk</c> 会让解码器进 <c>error</c> 态。
    /// 所以"空帧"这件事<b>只能是老路的麻烦</b>（它照样会发出去、照样被中心拒），
    /// 不许在切片器这里变成一种新形状。
    /// </remarks>
    [Fact]
    public void H_an_empty_payload_slices_into_nothing()
    {
        RemoteFrameChunker.CountFor(0).Should().Be(0);
        RemoteFrameChunker.NeedsChunking(0).Should().BeFalse();
        RemoteFrameChunker.Slice(Frame(0)).Should().BeEmpty();
    }

    /// <summary>
    /// 片上限小到会切出<b>超过协议上限</b>的片数 ⇒ <b>当场抛</b>，而不是默默发一份中心拼不回来的东西。
    /// </summary>
    /// <remarks>
    /// 🔥 这条与中心那条「非法输入只许丢帧、绝不许抛」的纪律<b>不矛盾</b>：
    /// 那边抛出去掐的是<b>整条 Hub 连接</b>；这边只有本机自己会调，
    /// 而走到这里说明<b>我们自己的代码</b>要生成一份协议上非法的东西 ——
    /// 当场炸比"让中心悄悄丢一片"好查得多。
    /// </remarks>
    [Fact]
    public void I_a_chunk_size_that_would_break_the_protocol_throws()
    {
        RemoteFrameChunker.CountFor(RemoteStreamLimits.MaxFrameBytes).Should().BeLessOrEqualTo(
            RemoteStreamLimits.MaxChunkCount,
            "**默认**片上限折算出来的片数必须是合法的 —— 也就是说上面那条「抛」只可能被一个荒唐的参数触发");

        Action act = () => RemoteFrameChunker.Slice(
            Frame(RemoteStreamLimits.MaxFrameBytes),
            maxChunkBytes: 1024);

        act.Should().Throw<ArgumentOutOfRangeException>();

        Action zero = () => RemoteFrameChunker.Slice(Frame(16), maxChunkBytes: 0);

        zero.Should().Throw<ArgumentOutOfRangeException>("0 会让片数算成无穷大");
    }

    // ═══════════════════════════════════════════════════════════
    // ⑤ 「中心拼不起来」⇒ 开口要一个关键帧（M5.4-c① 步 5）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>一帧被中心重组失败 ⇒ 只开口要<b>一次</b>关键帧，且要说对是哪个会话。</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 为什么必须有人接这个标志：中心丢一帧 = H.264 参考链上戳了个洞
    /// ⇒ 观看端从那一刻起每帧都是花的，<b>而解码器不报错</b>。
    /// 补一个关键帧把恢复代价从"最多一个 GOP"（<c>GopSize 60</c> / 12 fps ⇒ <b>5 秒</b>）
    /// 压到"<b>下一帧</b>"。
    /// </para>
    /// <para>
    /// ⚠️ <b>「一次」而不是「每片一次」</b>：一帧有十几片，中心在同一个故障下会每片都回这个标志
    /// （它每次都判"这一帧拼不起来"）。照着每片各要一次，等于把<b>一次丢帧放大成一路 IDR</b> ——
    /// 而 8K 的 IDR 正是整条链路上最贵的东西。所以去重放在<b>帧</b>这一级：
    /// 一帧里只要有一片说"拼不起来"，这一帧就只要一次。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task J_a_frame_the_center_could_not_rebuild_asks_for_one_keyframe()
    {
        const int size = 6 * 1024 * 1024;

        var hub = new FakeHub
        {
            // 每一片都回同一个意思：这一帧我拼不起来了。
            Responder = (_, _) => AgentIngestAckDto.SuccessNeedKeyframe(),
        };

        var asked = new List<string>();

        await AgentConnection.PushFrameAsync(
            hub.InvokeAsync, Frame(size), CancellationToken.None, asked.Add);

        hub.Chunks().Should().HaveCountGreaterThan(1, "这条用例要的是分片路");

        asked.Should().ContainSingle(
            "一帧只该换来一次请求 —— 每片各要一次会把一次丢帧放大成一路 IDR")
            .Which.Should().Be("s-1", "会话 id 要说对，否则服务侧找不到那条流");
    }

    /// <summary>整帧那条路同样要读这个标志（两条路的纪律不许分叉）。</summary>
    [Fact]
    public async Task K_a_whole_frame_that_the_center_could_not_rebuild_also_asks()
    {
        var hub = new FakeHub { Responder = (_, _) => AgentIngestAckDto.SuccessNeedKeyframe() };

        var asked = new List<string>();

        await AgentConnection.PushFrameAsync(
            hub.InvokeAsync, Frame(64 * 1024), CancellationToken.None, asked.Add);

        asked.Should().ContainSingle().Which.Should().Be("s-1");
    }

    /// <summary>
    /// 守卫：<b>正常的 ack 一句话都不许说。</b>
    /// <para>
    /// ⚠️ 这一条防的是"顺手在每次推帧后都要一个关键帧"这种写法 ——
    /// 它<b>看起来</b>更安全（永远有参考帧），实际是把码率翻几倍、帧率打下来，
    /// 而画面上一眼看不出来。
    /// </para>
    /// </summary>
    [Fact]
    public async Task L_a_normal_ack_asks_for_nothing()
    {
        var hub = new FakeHub();

        var asked = new List<string>();

        await AgentConnection.PushFrameAsync(
            hub.InvokeAsync, Frame(6 * 1024 * 1024), CancellationToken.None, asked.Add);

        asked.Should().BeEmpty("没丢帧就不要开口要关键帧");
    }

    /// <summary>
    /// 守卫：<b>被拒绝的那一帧不许顺带要关键帧</b>。
    /// <para>
    /// 🔥 语义上这不是"保守"，而是"对不对"：<c>Ok:false</c> 的含义是
    /// <b>会话已经没了 / 不属于这台机器</b> —— 那条流正在收场，
    /// 补一个关键帧既没有观看端要，还会往一条正在关闭的管道里塞一条指令。
    /// </para>
    /// </summary>
    [Fact]
    public async Task M_a_rejected_frame_does_not_ask_for_a_keyframe()
    {
        var hub = new FakeHub
        {
            Responder = (_, _) => AgentIngestAckDto.Rejected(RemoteErrorCodes.SessionNotFound, "会话不存在或已结束。"),
        };

        var asked = new List<string>();

        Func<Task> act = () => AgentConnection.PushFrameAsync(
            hub.InvokeAsync, Frame(6 * 1024 * 1024), CancellationToken.None, asked.Add);

        await act.Should().ThrowAsync<InvalidOperationException>();

        asked.Should().BeEmpty("会话都没了，关键帧补到哪里去");
    }

    // ═══════════════════════════════════════════════════════════
    // ④ 诊断旋钮：把片上限降下来 ⇒ 普通帧也走分片（端到端冒烟靠它）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>阈值降下来之后，一条 2 KB 的普通帧必须真的被切成几片。</b>
    ///
    /// <para>
    /// 这条用例之所以存在的全部理由，是<b>本机没有 8K 显示器</b>：
    /// 要等"一条消息装不下"的帧，就得有 8K 屏（本机只有 2160×1440）。
    /// 于是"被控端真的切了 / 中心真的拼了 / 浏览器真的画了"这条端到端判据，
    /// 只能靠<b>把阈值降下来</b>让普通帧走到那条路上 ——
    /// 而"降下来之后真的会切"这件事，必须先在这里被钉住，
    /// 否则冒烟脚本里那条 <c>--chunk-threshold</c> 可能是个<b>没有接线的旋钮</b>：
    /// 它会安安静静跑完一整轮老路，然后报绿。
    /// </para>
    /// </summary>
    [Fact]
    public async Task N_a_lowered_threshold_sends_an_ordinary_frame_as_chunks()
    {
        const int size = 2048;
        const int threshold = 1024;

        var hub = new FakeHub();

        await AgentConnection.PushFrameAsync(
            hub.InvokeAsync,
            Frame(size),
            CancellationToken.None,
            maxChunkBytes: threshold);

        hub.Methods.Should().Equal(
            new[] { AgentHubClientMethods.PushRemoteFrameChunk, AgentHubClientMethods.PushRemoteFrameChunk },
            "2 KB 的帧在 1 KB 的片上限下装不下 ⇒ 必须切，而且要切两片");

        var chunks = hub.Chunks();

        chunks.Select(c => c.ChunkIndex).Should().Equal(0, 1);
        chunks.Select(c => c.ChunkCount).Should().OnlyContain(count => count == 2);
        chunks.Select(c => c.Data.Length).Should().OnlyContain(length => length <= threshold);

        chunks.SelectMany(c => c.Data).Should().Equal(
            Payload(size),
            "降阈值改变的是**怎么送**，不是**送什么** —— 拼回来必须逐字节相等");
    }

    /// <summary>
    /// 阈值<b>不改变内容，只改变分法</b>：同一个帧在两种阈值下拼回来是同一份字节。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这一条看起来与 N 重复，其实防的是另一件事：
    /// 把阈值"顺手"当成<b>截断长度</b>用（`AsSpan(0, threshold)`）是个很容易犯的错，
    /// 而它的症状是<b>长度对、内容少</b> —— 画面会花，但每一片都在合法尺寸内。
    /// </remarks>
    [Fact]
    public async Task O_the_threshold_changes_how_it_splits_not_what_it_carries()
    {
        const int size = 5000;

        static async Task<byte[]> ReassembledAsync(int maxChunkBytes)
        {
            var hub = new FakeHub();

            await AgentConnection.PushFrameAsync(
                hub.InvokeAsync, Frame(size), CancellationToken.None, maxChunkBytes: maxChunkBytes);

            return hub.Chunks().SelectMany(c => c.Data).ToArray();
        }

        var byHundred = await ReassembledAsync(100);
        var byThousand = await ReassembledAsync(1000);

        byHundred.Should().Equal(Payload(size));
        byThousand.Should().Equal(Payload(size));
        byHundred.Length.Should().Be(size, "任何一片都不许是「截断」，拼回来必须是整整 5000 字节");
    }

    // ═══════════════════════════════════════════════════════════
    // ⑤ 环境变量解析：非法值只警告、绝不抛（客户机不许因为一个手抖起不来）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>没设这个环境变量 = 一切照旧</b>（生产默认状态，<b>不许</b>打警告刷日志）。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void P_an_absent_threshold_means_the_protocol_default_and_no_noise(string? raw)
    {
        var (threshold, warning) = AgentConnection.ResolveChunkThreshold(raw);

        threshold.Should().Be(RemoteStreamLimits.MaxChunkPayloadBytes);
        warning.Should().BeNull(
            "没设就是没设 —— 生产机器上每一台都是这个状态，往日志里写一句话等于给自己造噪");
    }

    /// <summary>
    /// <b>一个能用的值必须原样用上</b>（诊断旋钮真的接线了）。
    /// </summary>
    [Fact]
    public void Q_a_sane_threshold_is_taken_as_is()
    {
        var (threshold, warning) = AgentConnection.ResolveChunkThreshold("65536");

        threshold.Should().Be(65536);
        warning.Should().BeNull();
    }

    /// <summary>
    /// 🔴 <b>非法值不许抛、不许静默</b>：回落到默认值，并且<b>必须</b>留一句话。
    /// </summary>
    /// <remarks>
    /// 🔥 静默回落是这里最坏的形状：以为降了阈值，结果跑了一整轮老路，
    /// 然后拿这份"全绿"去判断分片好不好 —— 那是一次<b>假 PASS</b>。
    /// ⚠️ 上一档（大于协议上限）尤其要挡：它会让中心那条消息超出 8 MiB
    /// ⇒ <b>掐断整条 Hub 连接</b>，而症状是"机器整个离线"，离真因很远。
    /// </remarks>
    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("999999999")]
    public void R_an_unusable_threshold_falls_back_and_says_so(string raw)
    {
        var (threshold, warning) = AgentConnection.ResolveChunkThreshold(raw);

        threshold.Should().Be(RemoteStreamLimits.MaxChunkPayloadBytes);
        warning.Should().NotBeNull(
            "回落到默认值这件事必须留痕 —— 不留痕的话，这一轮跑的是老路而人以为跑的是分片");
    }

    // ═══════════════════════════════════════════════════════════
    // 夹具：假 hub
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>假 hub</b>：把"发出去的那几条消息"记下来。
    ///
    /// <para>
    /// ⚠️ 它刻意只替换<b>"发"这一步</b>（<c>AgentConnection.PushFrameAsync</c> 的
    /// <c>invoke</c> 参数），而不是整个 <c>AgentConnection</c>：
    /// 被验的正是"发之前怎么决定"（老路 / 切几片 / 什么时候停），
    /// 而"发"本身是 SignalR 的事。
    /// </para>
    /// </summary>
    private sealed class FakeHub
    {
        private readonly List<(string Method, object Payload)> _calls = new();

        /// <summary>造一个应答；不设 = 一律成功。</summary>
        public Func<string, object, AgentIngestAckDto?>? Responder { get; init; }

        public IReadOnlyList<string> Methods
            => _calls.Select(call => call.Method).ToList();

        public IReadOnlyList<RemoteStreamFrameChunk> Chunks()
            => _calls
                .Select(call => call.Payload)
                .OfType<RemoteStreamFrameChunk>()
                .ToList();

        public Task<AgentIngestAckDto> InvokeAsync(string method, object payload, CancellationToken cancellationToken)
        {
            _calls.Add((method, payload));

            return Task.FromResult(Responder?.Invoke(method, payload) ?? AgentIngestAckDto.Success());
        }
    }
}
