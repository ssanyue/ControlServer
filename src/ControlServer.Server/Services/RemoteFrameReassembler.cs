using ControlServer.Contracts;

namespace ControlServer.Server.Services;

/// <summary>
/// <b>中心侧的「单槽」帧重组器</b>（M5.4-c① 的分片 / chunking）。
///
/// <para>
/// 被控端在装不下一条消息时会把一帧切成若干片（<see cref="RemoteStreamFrameChunk"/>），
/// 本类负责把它们<b>拼回整帧</b>。它<b>每路会话一个实例</b>，而"单槽"的意思是：
/// <b>同一时刻至多只有一帧不完整</b> —— 这不是省事，是<b>可以论证</b>的（见下）。
/// </para>
///
/// <para>
/// 🔥 <b>为什么终止在中心（而不是端到端透传给浏览器）</b>：
/// 中心的转发队列（<c>Entry.Fanout</c>）是"容量 1 + 丢最旧"，而它丢弃的单位是
/// <b>一条消息</b>。端到端分片的话，它丢掉的会是<b>同一帧的中间一片</b> ——
/// 前端于是拼出一帧<b>长度对、内容错</b>的码流 ⇒ 花屏（甚至解码器 <c>error</c>），
/// 而<b>中心与前端都不会报错</b>。终止在中心则跳 4/5/6 一行都不用改，
/// 前端也一行都不用改（<c>Latest</c> 的语义也不变）。
/// </para>
///
/// <para>
/// 🔥 <b>为什么"单槽"够用 —— 不需要定时器、超时、后台清扫</b>：
/// </para>
/// <list type="number">
///   <item>服务 → 中心是<b>单条顺序连接上的一段顺序流</b>（SignalR 的一条 WebSocket）；</item>
///   <item>被控端的推流泵是 <c>await</c> 完这一帧才读下一帧的（<c>SessionStreamChannel</c>）；</item>
///   <item>分片的各片在<b>同一帧内</b>也是顺序 <c>await</c> 的。</item>
/// </list>
/// <para>
/// ⇒ <b>当下一个帧序号出现时，上一帧要么已经拼齐、要么已经永久丢了。</b>
/// 于是"槽"只需要三条规则（见 <see cref="Accept"/>），而"回收"这件事根本不存在。
/// </para>
///
/// <para>
/// 🔴 <b>防御性上限一律按"丢帧 + 计数"处理，绝不抛异常</b>：
/// 调用方这条路（<c>PushRemoteFrameChunk</c> → 本类）一旦抛出去，
/// 后果是<b>掐断整条 Hub 连接</b>（心跳 / 遥测 / 配置 / 音频全在它上面），
/// 而这里最坏的后果应该只是<b>丢一帧</b>。
/// 这与 <c>FrameTooLargeException</c> 当初被从 <c>InvalidOperationException</c> 里摘出来
/// 是<b>同一条纪律</b>。
/// </para>
///
/// <para>
/// ⚠️ <b>它不是<b>线程安全</b>的"随便用"，而是自带一把小锁</b>：调用方刻意<b>不</b>拿
/// 会话账本那把大锁去调它（见 <c>RemoteSessionRegistry.AcceptFrameChunk</c>）——
/// 拼一份十几 MB 的字节不应该让"所有会话的所有帧"排队等它。
/// </para>
/// </summary>
public sealed class RemoteFrameReassembler
{
    /// <summary>它自己的锁，只保护本类的字段（<b>刻意不与会话账本共用</b>，理由见类注释）。</summary>
    private readonly object _gate = new();

    private long _sequence = -1;
    private int _chunkCount;
    private Dictionary<int, byte[]>? _pieces;
    private int _received;
    private long _bytes;

    /// <summary>
    /// <b>最近一个拼齐过的帧序号</b> —— 用来把"迟到 / 重复的片"与"真丢了"分开。
    /// <para>
    /// ⚠️ 没有它，一帧拼齐之后的<b>任何一个重复片</b>都会被当成本帧的第 0 片，
    /// 于是那个槽一直等到下一帧的序号出现，被判成"帧被打断了" ——
    /// 也就是说，一次<b>无害的重复</b>会换来一次"丢帧 + 请补关键帧"（一路 IDR）。
    /// </para>
    /// </summary>
    private long _lastCompletedSequence = -1;

    /// <summary>槽里现在有没有还没拼齐的帧（诊断与用例用）。</summary>
    public bool HasPendingFrame
    {
        get
        {
            lock (_gate) return _pieces is not null;
        }
    }

    /// <summary>
    /// 收一片。
    /// </summary>
    /// <param name="chunk">一片码流（<b>自描述</b>：每一片都带完整帧头）。</param>
    /// <param name="dropReason">
    /// 非 <c>null</c> = <b>有一帧被丢掉了</b>，值就是给人看的理由
    /// （调用方据此计数 + 在下一条应答上带"请补关键帧"）。
    ///
    /// <para>
    /// ⚠️ 它与返回值<b>相互独立</b>：可能"丢了一帧、同时这一片也没能拼齐"（两者都为 null / 非 null），
    /// 也可能"丢了一帧、而这一片正好把新帧拼齐了"（同时有值）。
    /// 把它折进返回值会让调用方没法分开说这两件事。
    /// </para>
    ///
    /// <para>⚠️ <b>迟到 / 重复的片不算"丢帧"</b> ⇒ 这一项为 <c>null</c>（理由见
    /// <see cref="_lastCompletedSequence"/>）。</para>
    /// </param>
    /// <returns>
    /// 拼齐的整帧；还没齐（或这一片无效）时为 <c>null</c>。
    /// </returns>
    public RemoteStreamFrame? Accept(RemoteStreamFrameChunk chunk, out string? dropReason)
    {
        dropReason = null;

        var header = chunk.Header;

        if (header is null || chunk.Data is null)
        {
            dropReason = "这一片里没有帧头或没有码流数据。";
            return null;
        }

        lock (_gate)
        {
            // ── ① 形状闸门：越界 / 荒唐的片数 ⇒ 丢这一帧 ──
            //
            // ⚠️ 判据用「< 2 而不是 < 1」：ChunkCount == 1 的帧**不许走这条路**
            //    （老路 PushRemoteFrame 才是它该走的），而"一个声称只有 1 片的 chunk 消息"
            //    只可能来自一个坏掉的实现 —— 放它过去等于在链路上开了第二条整帧通道。
            if (chunk.ChunkCount < 2
                || chunk.ChunkCount > RemoteStreamLimits.MaxChunkCount
                || chunk.ChunkIndex < 0
                || chunk.ChunkIndex >= chunk.ChunkCount)
            {
                dropReason =
                    $"一片的片数/下标不合法（第 {chunk.ChunkIndex} 片，共 {chunk.ChunkCount} 片，"
                    + $"允许 2..{RemoteStreamLimits.MaxChunkCount}）—— 这一帧已丢弃。";

                // 既然它的身份都不可信，就**不留半帧**：宁可丢一帧，也不要一个可能被污染的半帧
                // 在槽里等到下一帧、把下一帧也带坏。
                ResetLocked();

                return null;
            }

            if (chunk.Data.Length == 0)
            {
                dropReason = "一片的载荷是空的 —— 这一帧已丢弃（空片只可能来自坏掉的实现）。";

                ResetLocked();

                return null;
            }

            // ── ② 迟到 / 重复：安静丢掉，**不算丢帧** ──
            if (header.Sequence <= _lastCompletedSequence)
            {
                return null;
            }

            // ⚠️ 另一类迟到片：比"正在拼的那一帧"更旧的。
            //    顺序连接上它本不该发生（TCP 保序、丢就永久丢了），但真发生时
            //    若把它当"新帧"，它会**把正在拼的好帧顶掉**（下面那条"新序号打断"），
            //    于是一次无害的重复换来一次丢帧 + 一路 IDR。这里明确丢掉。
            if (_pieces is not null && header.Sequence < _sequence)
            {
                return null;
            }

            // ── ③ 开槽 / 换槽 ──
            if (_pieces is null || _sequence != header.Sequence)
            {
                if (_pieces is not null)
                {
                    // 🔥 这一条就是"单槽"的全部代价：新序号出现 ⇒ 上一帧永远地丢了。
                    //    会走到这里只有两种可能：片真的在路上丢了，或者对方的顺序流断了
                    //    —— 而两者对观看端是同一件事（参考链上有个洞）。
                    dropReason =
                        $"帧 #{_sequence} 只收到 {_received}/{_chunkCount} 片时，"
                        + $"新帧 #{header.Sequence} 就到了 —— 前一帧已永久丢失。";
                }

                OpenLocked(header.Sequence, chunk.ChunkCount);
            }
            else if (chunk.ChunkCount != _chunkCount)
            {
                // ⚠️ 同一序号下 ChunkCount 不一致 ⇒ 这两片根本不是同一帧。
                //    这里**不比整份帧头**：帧头里的 Description 是 byte[]，
                //    而数组在 record 的相等性里按**引用**比 —— 两片内容相同的 avcC 会被判成"不一样"，
                //    于是每一帧都被自己判成非法。判据只取"能定身份"的那几项。
                dropReason =
                    $"帧 #{_sequence} 的片数前后不一致（先 {_chunkCount} 片，后来 {chunk.ChunkCount} 片）"
                    + "—— 这一帧已丢弃。";

                ResetLocked();

                return null;
            }

            var pieces = _pieces!;

            if (pieces.ContainsKey(chunk.ChunkIndex))
            {
                dropReason = $"帧 #{_sequence} 的第 {chunk.ChunkIndex} 片收到了两次 —— 这一帧已丢弃。";

                ResetLocked();

                return null;
            }

            if (_bytes + chunk.Data.Length > RemoteStreamLimits.MaxFrameBytes)
            {
                dropReason =
                    $"帧 #{_sequence} 的累计字节数将超过单帧上限 "
                    + $"{RemoteStreamLimits.Describe(RemoteStreamLimits.MaxFrameBytes)} —— 这一帧已丢弃。";

                ResetLocked();

                return null;
            }

            pieces[chunk.ChunkIndex] = chunk.Data;
            _received++;
            _bytes += chunk.Data.Length;

            // ── ③.5 还没齐 ⇒ 半帧不是帧 ──
            //
            // 🔥 **没有这一句，下面的按下标拼接会直接炸**（`pieces[index]` 抛
            //    `KeyNotFoundException`）—— 而路径上"抛"的后果是**掐断整条 Hub 连接**。
            //    这一句是"半帧"与"整帧"之间唯一的分界，它守的正是本类那句
            //    "最坏的后果应该只是丢一帧"。
            //
            //    ⚠️ 它同时是"帧率不会被算成两三倍"的原因：拼齐之前一帧都不算"已收到"
            //       （见 `Entry.Observe` 里的计数）。
            if (_received < _chunkCount) return null;

            // ── ④ 拼齐 ──
            //
            // 🔥 按**下标**拼，不按到达顺序拼：片在一条顺序连接上确实是顺序到的，
            //    但"顺序到"是**链路的性质**，不是**协议的性质** —— 把两者混起来，
            //    将来任何一次"重发/重排"都会变成"位置全错、长度全对"的花屏，
            //    而那是最难查的一类（长度对得上，没有任何一层会报错）。
            var buffer = new byte[(int)_bytes];
            var offset = 0;

            for (var index = 0; index < _chunkCount; index++)
            {
                var piece = pieces[index];

                Buffer.BlockCopy(piece, 0, buffer, offset, piece.Length);
                offset += piece.Length;
            }

            var assembled = new RemoteStreamFrame(header, buffer);

            _lastCompletedSequence = _sequence;
            ResetLocked();

            return assembled;
        }
    }

    /// <summary>开一个新槽（调用方必须已持锁）。</summary>
    private void OpenLocked(long sequence, int chunkCount)
    {
        _sequence = sequence;
        _chunkCount = chunkCount;
        _pieces = new Dictionary<int, byte[]>(chunkCount);
        _received = 0;
        _bytes = 0;
    }

    /// <summary>清槽（调用方必须已持锁）。<b>不碰 <c>_lastCompletedSequence</c></b>。</summary>
    private void ResetLocked()
    {
        _sequence = -1;
        _chunkCount = 0;
        _pieces = null;
        _received = 0;
        _bytes = 0;
    }
}
