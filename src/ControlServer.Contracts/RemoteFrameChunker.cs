namespace ControlServer.Contracts;

/// <summary>
/// <b>把一帧折叠成几片</b>（M5.4-c① 的分片 / chunking）——
/// <b>这条链路上唯一的切片实现</b>。
///
/// <para>
/// 🔥🔥 <b>「只有一处实现」不是洁癖，它决定这组用例到底证明了什么。</b>
/// 分片这条路上有两端：<b>被控端切</b>（<c>AgentConnection.PushRemoteFrameAsync</c>）
/// 与<b>中心拼</b>（<c>RemoteFrameReassembler</c>）。如果用例自己再写一份切片，
/// 那么"3 片拼回来逐字节相等"证明的是<b>「用例切的片能被拼回来」</b>，
/// 而不是<b>「生产切出来的片能被拼回来」</b> —— 两者的差别在于：
/// 生产那一边的片长算错（比如最后一片多切一个字节）时，用例仍然全绿。
/// </para>
///
/// <para>
/// ⇒ 所以被控端切片时调本类，用例造片时<b>也</b>调本类。
/// 于是"算出来几片"与"真的切出几片"不可能各说各的，
/// 而 <see cref="Slice"/> 里的每一个边界条件都被两端同时用着。
/// </para>
///
/// <para>
/// ⚠️ <b>它与 <see cref="RemoteStreamLimits"/> 的分工</b>：那边是<b>三个尺寸数</b>
/// （消息 / 片 / 帧各能装多大，唯一的根是 <see cref="RemoteStreamLimits.SignalRMessageBytes"/>），
/// 这边是<b>由那些数推出来的折叠算术</b>。本类<b>不写任何字面量尺寸</b> ——
/// 一旦这里出现一个独立的数字，上述"唯一的根"就断了。
/// </para>
///
/// <para>
/// 🔴 <b>分片（chunking）≠ 分块编码（tile）</b>：本类只是把<b>已经编好的字节</b>切开送，
/// <b>码流一字未变</b>，解码器根本不知道；分块编码是把一幅图切块<b>各自独立编码</b>（→ M5.5）。
/// </para>
/// </summary>
public static class RemoteFrameChunker
{
    /// <summary>
    /// <b>一帧要切成几片</b>。<c>1</c> = 装得下、<b>不必切</b>；<c>0</c> = 空帧（不上路）。
    /// </summary>
    /// <param name="payloadBytes">裸码流字节数。</param>
    /// <param name="maxChunkBytes">
    /// 单片上限。默认就是协议上限（<see cref="RemoteStreamLimits.MaxChunkPayloadBytes"/>）；
    /// ⚠️ <b>只有用例会传别的值</b> —— 用它把一块几百字节的假码流切成好几片，
    /// 免得为了验证"多片能拼回来"去分配一份 6 MB 的真码流（见 <see cref="Slice"/> 的说明）。
    /// </param>
    /// <remarks>
    /// ⚠️ <b>用 <c>long</c> 参与运算</b>：两数相加先溢出成负数的话，
    /// 上限附近（正好 64 MiB）那几帧会被算成 0 片，而症状是"那一帧凭空消失"。
    /// </remarks>
    public static int CountFor(
        long payloadBytes,
        int maxChunkBytes = RemoteStreamLimits.MaxChunkPayloadBytes)
    {
        if (maxChunkBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxChunkBytes),
                maxChunkBytes,
                "单片上限必须为正数 —— 0 会让片数算成无穷大，负数会让它算成 0。");
        }

        return payloadBytes <= 0
            ? 0
            : (int)((payloadBytes + maxChunkBytes - 1) / maxChunkBytes);
    }

    /// <summary>
    /// <b>这一帧要不要走分片那条路</b>（= 片数是不是大于 1）。
    ///
    /// <para>
    /// 🔥 <b>为什么要单独有个方法，而不是让调用方写 <c>CountFor(...) &gt; 1</c></b>：
    /// 因为"<c>1</c> 片必须走老路"是一条<b>协议约定</b>，而不是一个算术巧合 ——
    /// 中心的 <c>RemoteFrameReassembler</c> <b>明确拒绝</b> <c>ChunkCount == 1</c> 的片
    /// （放它过去等于在链路上开了第二条整帧通道）。把这条约定写进一个具名方法，
    /// 下一个改它的人才会看见"这里判的不是算术"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 它同时也是<b>性能上的第一道闸</b>：<see cref="Slice"/> 会<b>拷贝</b>字节，
    /// 而绝大多数帧（2160×1440、4K）根本装得下 —— 无条件调 <c>Slice</c>
    /// 等于给每一帧白白加一次全量拷贝。<b>先问这个方法，再决定要不要切。</b>
    /// </para>
    /// </summary>
    public static bool NeedsChunking(
        long payloadBytes,
        int maxChunkBytes = RemoteStreamLimits.MaxChunkPayloadBytes)
        => CountFor(payloadBytes, maxChunkBytes) > 1;

    /// <summary>
    /// <b>把一帧折叠成几片。</b>片走<b>下标</b>升序，拼起来<b>逐字节等于</b>原帧。
    /// </summary>
    /// <param name="frame">整帧（它的 <c>Data</c> 是裸码流，不是 base64）。</param>
    /// <param name="maxChunkBytes">单片上限，语义见 <see cref="CountFor"/>。</param>
    /// <returns>
    /// 按片下标升序排列的片。<b>空码流返回空表</b>（不是"一片空的"——
    /// 中心的 <c>RemoteFrameReassembler</c> 明确拒绝空片，而前端喂一个 0 字节的
    /// <c>EncodedVideoChunk</c> 会让解码器进 <c>error</c> 态）。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 🔥 <b>片长取"均分"而不是"前面装满、最后一片兜底"</b>：
    /// 片数 = <c>ceil(len / max)</c>，片长 = <c>ceil(len / 片数)</c>，于是每片都 ≤ 上限
    /// 且彼此相差不超过 1 字节。两种切法在协议上都合法，选均分的理由是<b>诊断</b>：
    /// <c>len = max + 1</c> 时"装满"那种切法会产出 <c>[max, 1]</c> ——
    /// 日志上看起来像"有一片几乎没内容"，而真因只是"多了一个字节"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>它<b>会</b>拷贝字节</b>（每一片是一份新数组）：中心那条 SignalR 消息要的是
    /// 独立可序列化的 <c>byte[]</c>，无法靠偏移复用原数组。所以：
    /// <b>先 <see cref="NeedsChunking"/>，再 <see cref="Slice"/></b> ——
    /// 别让 2160×1440 / 4K 这些根本装得下的帧也走一次全量拷贝。
    /// </para>
    ///
    /// <para>
    /// 🔥 <b>每一片都带完整帧头（同一个引用）</b>：片因此<b>自描述</b>，
    /// 中心不必记住"第 0 片长什么样"，也不必假定片是按序到的。
    /// ⚠️ 共享的是<b>引用</b>而不是各自深拷一份：帧头里那个 <c>Description</c>（avcC/hvcC）
    /// 有几 kB，逐片拷它等于把"分片省下来的压力"又加回去一点。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>这里抛异常是允许的，而且是有意的</b>：与中心入口那条
    /// 「一路非法输入只许丢帧、绝不许抛」的纪律<b>不矛盾</b> ——
    /// 那边抛出去掐的是<b>整条 Hub 连接</b>，而这边只有本机自己会调；
    /// 走到这里说明<b>我们自己的代码</b>要生成一份协议上非法的东西
    /// （片数超过 <see cref="RemoteStreamLimits.MaxChunkCount"/>），
    /// 当场炸比"让中心悄悄丢一片"好查得多。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<RemoteStreamFrameChunk> Slice(
        RemoteStreamFrame frame,
        int maxChunkBytes = RemoteStreamLimits.MaxChunkPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(frame, nameof(frame));

        var payload = frame.Data;

        ArgumentNullException.ThrowIfNull(payload, nameof(frame));

        var count = CountFor(payload.Length, maxChunkBytes);

        if (count == 0) return Array.Empty<RemoteStreamFrameChunk>();

        if (count > RemoteStreamLimits.MaxChunkCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxChunkBytes),
                maxChunkBytes,
                $"每片 {maxChunkBytes} 字节会把 {payload.Length} 字节切成 {count} 片，"
                + $"超过协议允许的 {RemoteStreamLimits.MaxChunkCount} 片上限"
                + "（那份帧不可能被中心拼回来）。");
        }

        // 均分：每片 ceil(总长 / 片数)。上面已证 per ≤ maxChunkBytes。
        var per = (int)(((long)payload.Length + count - 1) / count);

        var chunks = new RemoteStreamFrameChunk[count];

        for (var index = 0; index < count; index++)
        {
            var offset = index * per;
            var length = Math.Min(per, payload.Length - offset);

            chunks[index] = new RemoteStreamFrameChunk(
                frame.Header,
                ChunkIndex: index,
                ChunkCount: count,
                Data: payload.AsSpan(offset, length).ToArray());
        }

        return chunks;
    }
}
