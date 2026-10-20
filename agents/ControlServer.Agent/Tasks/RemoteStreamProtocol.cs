using System.Buffers.Binary;
using System.Text.Json;
using ControlServer.Contracts;

namespace ControlServer.Agent.Tasks;

/// <summary>
/// <b>会话 ↔ 服务的「远控码流」管道协议</b>（M5.1）。
///
/// <para>
/// 🔥 <b>为什么不能复用 <see cref="SessionPipeProtocol"/> —— 三条硬冲突</b>：
/// </para>
/// <list type="bullet">
///   <item><b>形状</b>：那条是「一请求一连接」，发完就 <c>Disconnect</c>。
///         视频流按 10–30 fps 跑，等于<b>每秒几十次建连</b>；</item>
///   <item><b>单实例</b>：那条管道同一时刻只处理一条指令。码流一上来，
///         <c>launch</c> / <c>volume</c> 就<b>全部排队</b>到流结束；</item>
///   <item><b>文本行</b>：那条协议是「一行 JSON 进去、一行 JSON 出来」，
///         天然表达不了"一串二进制块、只在结尾断开"的长连接。</item>
/// </list>
///
/// <para>
/// 🔥 <b>方向：托盘建管道（服务端）、服务连上来（客户端）—— 与直觉相反，但不可换。</b>
/// 服务跑在 <b>Session 0（SYSTEM）</b>。如果反过来由服务建管道，
/// <c>CreateNamedPipe</c> 在 <c>lpSecurityAttributes = NULL</c> 时给出的默认 DACL 是
/// 「<b>SYSTEM + 管理员 + 创建者</b>」，而创建者是 SYSTEM ——
/// <b>普通用户身份的托盘根本连不上</b>（错误 5，拒绝访问）。
/// 要让托盘连得上就得写一份自定义安全描述符、引 <c>System.IO.Pipes.AccessControl</c>，
/// 而 <see cref="SessionPipeServer"/> 当初刻意避开的正是这个包袱。
/// </para>
/// <para>
/// 顺带一提：这个方向也<b>刚好符合</b>生产者/消费者关系 —— 托盘是生产码流的一方，
/// 由它把管子架起来、等消费者接上，是很自然的做法。
/// </para>
///
/// <para>
/// <b>帧形状</b>（小端）：
/// <code>
/// [int32 头部长度][int32 码流长度][头部 JSON（UTF-8）][码流裸字节]
/// </code>
/// </para>
/// <list type="bullet">
///   <item>头走 JSON、体走<b>裸字节</b>：整帧都 JSON 化的话，<c>byte[]</c> 会变 base64，
///         白白多 33% 字节与一份编解码 CPU —— 而这是本机管道，没有理由付这个税。</item>
///   <item>两个长度前缀都<b>先写在前面</b>，读取方可以一次读满整帧，
///         不必"边读边猜还要不要继续"。</item>
/// </list>
/// </summary>
public static class RemoteStreamProtocol
{
    /// <summary>
    /// 管道名前缀。<b>后面接的是会话 Id</b>（<c>sessionId</c>，不是会话编号）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 与 <see cref="SessionPipeProtocol.PipeNamePrefix"/> 一样带 <c>ControlServer.Agent.</c> 段，
    /// 但名字里明确写了 <c>RemoteStream</c> —— 两条管道可能同时在跑，
    /// 靠"名字长得很像但后缀不同"区分，迟早会有人敲错门。
    /// </remarks>
    public const string PipeNamePrefix = "ControlServer.Agent.RemoteStream.";

    /// <summary>等接收端连接上来的时限（毫秒）。超时就收摊。</summary>
    /// <remarks>
    /// 🔥 <b>必须有这个上限</b>：服务侧的接流任务是"被控端说开始了"之后才发起的，
    /// 正常情况下几毫秒就接上。而<b>托盘一旦把管道架在那儿等，就会一直占着抓屏设备</b>——
    /// 如果服务那边因为任何原因没来接（重启中 / 会话已结束），
    /// 没有超时的话托盘会永远开着一条没人看的流，悄悄烧 CPU 和带宽。
    /// </remarks>
    public const int ConnectTimeoutMs = 8000;

    /// <summary>头部 JSON 的长度上限（防"对方发了个畸形长度前缀"把内存吃光）。</summary>
    public const int MaxHeaderBytes = 16 * 1024;

    /// <summary>
    /// 码流单帧的长度上限（本地管道上）—— <b>直接引用共享口径，不在这里另起一个数</b>。
    ///
    /// <para>
    /// 🔥 <b>它<b>不是</b>随便定的，而它守的那道墙在 M5.4-c① 换了一道</b>：
    /// M5.4-b 之前它等于"单片上限"，因为它同时兼任"帧上限"，
    /// 而那时一帧必须塞进中心的一条 SignalR 消息。
    /// 现在一帧可以<b>切成若干片分多条消息送</b>，于是这条管道的上限抬到了
    /// <see cref="RemoteStreamLimits.MaxFrameBytes"/>（<b>防呆</b>：挡住畸形/异常大的帧），
    /// 而"别撞穿中心的单条消息"这件事改由<b>切片</b>保证
    /// （片大小是 <see cref="RemoteStreamLimits.MaxChunkPayloadBytes"/>，
    /// 由 <c>RemoteFrameUploader</c> 用）。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>为什么管道这一端也必须抬到帧上限</b>：分片发生在<b>服务</b>（它才有 Hub 连接），
    /// 而一帧必须先<b>整帧</b>穿过这条本机管道。管道若还卡在 5.75 MiB，
    /// 8K 的那一帧会在这里就被丢掉 —— 分片根本轮不到。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 两处曾经各写一份（这里是 <c>384 KB</c>、中心那边是 <c>512 KB</c>），
    /// 而那样必然漂：漂的方向是"客户机还在发、中心已经掐了"。
    /// </para>
    /// </summary>
    public const int MaxPayloadBytes = RemoteStreamLimits.MaxFrameBytes;

    /// <summary>
    /// 一个裸码流长度<b>装不装得下</b>。
    ///
    /// <para>
    /// 🔥 <b>为什么要有这个方法，而不是让调用方自己写 <c>length &gt; MaxPayloadBytes</c></b>：
    /// 调用方需要的是"在写之前就知道"，因为那时候它还<b>来得及做正确的事</b>
    /// （丢这一帧、并且下一帧要求关键帧），而不必先撞一次异常再回头判断。
    /// 热路径上不抛异常的写法既快，又不会让人把"太大"和"写失败"混成一个 <c>catch</c>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 它与 <see cref="WriteFrameAsync"/> 内部的判断是<b>同一个常量</b>，
    /// 所以不会出现"预检说能发、真发却抛"这种自相矛盾。
    /// 用例 <c>RemoteStreamProtocolTests</c> 钉住了这一点。
    /// </para>
    /// </summary>
    public static bool FitsInPipe(int payloadBytes) => payloadBytes <= MaxPayloadBytes;

    /// <summary>
    /// 管道的 JSON 约定：与 <see cref="SessionPipeProtocol.Json"/> 一致（camelCase + 紧凑 + 中文不转义）。
    /// </summary>
    public static readonly JsonSerializerOptions Json = SessionPipeProtocol.Json;

    /// <summary>某个会话的码流管道名。</summary>
    public static string PipeNameForSession(string sessionId) => PipeNamePrefix + sessionId;

    // ═══════════════════════════════════════════════════════════
    // 写
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 写一帧。
    ///
    /// <para>
    /// <b>会把"对面没了"这类异常抛给调用方</b> —— 推流循环靠它判断对面还在不在。
    /// </para>
    ///
    /// <para>
    /// 🔴🔴 <b>但"这一帧太大"<b>不</b>属于那一类，所以它有一个自己的异常类型</b>
    /// （<see cref="FrameTooLargeException"/>）。
    /// 这条区分是本项目<b>花了代价才买到的</b>：
    /// 早先这里统一抛 <see cref="InvalidOperationException"/>，而调用方
    /// （<c>RemoteStreamHost</c> 的推流循环）把<b>任何</b>写异常都当成
    /// "接收端已断开" ⇒ <c>return</c>，<b>整条流结束</b>。
    /// 于是现场看到的是<b>「码流写不出去，接收端已断开」</b>，
    /// 而真因只是"某一帧比上限大了几十 KB"，接收端好端端地在等着。
    /// </para>
    ///
    /// <para>
    /// ⇔ <b>"可恢复"与"终止"这两件事必须用不同的类型带出去，不能靠人去读异常消息。</b>
    /// 读消息判断是脆的：文案一改，分类就错了，而错的方向恰好是"把一次丢帧升级成一次掉线"。
    /// </para>
    /// </summary>
    /// <returns>写进去的字节数（头 + 体），用于统计实际码率。</returns>
    /// <exception cref="FrameTooLargeException">
    /// 载荷超过 <see cref="MaxPayloadBytes"/>。<b>这是可恢复的</b>：丢这一帧、继续下一帧。
    /// </exception>
    public static async Task<int> WriteFrameAsync(
        Stream stream,
        RemoteStreamFrameHeader header,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, Json);

        if (headerBytes.Length > MaxHeaderBytes)
        {
            // ⚠️ 这一条与"载荷太大"不同：帧头是我们自己拼的，它超了说明代码有 bug
            //    （比如某个字段被塞进了一段巨大的字符串），继续推只会每帧都失败。
            throw new InvalidOperationException($"帧头过大（{headerBytes.Length} 字节）。");
        }

        if (!FitsInPipe(payload.Length))
        {
            throw new FrameTooLargeException(payload.Length);
        }

        var prefix = new byte[8];

        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(0, 4), headerBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4, 4), payload.Length);

        // ⚠️ 不用 BinaryWriter/StreamWriter 包一层：它们会各带一个缓冲区，
        //    而"写完头要立刻 Flush 才轮到体"这种时序在缓冲之上很难说清。
        //    这里直接顺序写裸流 —— 管道是字节流，顺序就是语义。
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        return prefix.Length + headerBytes.Length + payload.Length;
    }

    // ═══════════════════════════════════════════════════════════
    // 读
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 读一帧。
    /// </summary>
    /// <returns>
    /// 读到的一帧；<c>null</c> = <b>对面正常结束了这条流</b>（连接断开、没有更多数据）。
    /// <para>
    /// ⚠️ 只有"干净地结束"才返回 <c>null</c>。畸形帧（长度前缀离谱、头不是 JSON）
    /// 一律<b>抛异常</b> —— 因为那说明双方对协议的理解已经不一致了，
    /// 继续读下去只会把后面的字节也解释错，产出一堆"看着像帧但不是帧"的东西。
    /// </para>
    /// </returns>
    public static async Task<RemoteStreamFrame?> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[8];

        if (!await TryFillAsync(stream, prefix, cancellationToken).ConfigureAwait(false)) return null;

        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(0, 4));
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4, 4));

        if (headerLength <= 0 || headerLength > MaxHeaderBytes)
        {
            throw new InvalidDataException($"帧头长度非法：{headerLength}。");
        }

        if (payloadLength <= 0 || payloadLength > MaxPayloadBytes)
        {
            throw new InvalidDataException($"帧长度非法：{payloadLength}。");
        }

        var headerBytes = new byte[headerLength];
        var payload = new byte[payloadLength];

        if (!await TryFillAsync(stream, headerBytes, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("帧头读到一半就断开了。");
        }

        if (!await TryFillAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("码流读到一半就断开了。");
        }

        RemoteStreamFrameHeader? header;

        try
        {
            header = JsonSerializer.Deserialize<RemoteStreamFrameHeader>(headerBytes, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"帧头不是合法 JSON：{ex.Message}", ex);
        }

        if (header is null) throw new InvalidDataException("帧头解析为空。");

        return new RemoteStreamFrame(header, payload);
    }

    /// <summary>
    /// 把 <paramref name="buffer"/> 填满。
    /// <para>
    /// ⚠️ <b>不能只读一次就当成读满</b>：命名管道是字节流，
    /// 一次 <c>ReadAsync</c> 只保证"至少读到 1 个字节"。
    /// 拿半截头部去解析长度前缀，会得到一个随机的巨大数字，
    /// 然后就是"申请几个 G 内存"或"帧长度非法"——两种都不是真正的原因。
    /// </para>
    /// </summary>
    /// <returns><c>false</c> = 一个字节都没读到（对面干净地关了）。</returns>
    /// <remarks>
    /// ⚠️ 它是 <c>internal</c> 而不是 <c>private</c>：音频管道（<see cref="RemoteAudioProtocol"/>）
    /// 走的是<b>同一种帧边界</b>（长度前缀 + 裸字节），没有理由把这段逻辑抄第二遍 ——
    /// 抄一遍的代价不是那 20 行，而是"两个版本在某次改动后悄悄分叉"。
    /// </remarks>
    internal static async Task<bool> TryFillAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);

            // 第一个字节就读不到 ⇒ 对面关掉了（正常收场）。
            // 读到一半断 ⇒ 由调用方判成畸形帧（那是异常收场，两者要分开）。
            if (read == 0) return offset > 0 ? throw new EndOfStreamException(
                $"流读到一半结束（已读 {offset}/{buffer.Length} 字节）。") : false;

            offset += read;
        }

        return true;
    }
}

/// <summary>
/// <b>"这一帧装不下"</b> —— 与被控端推流循环里那些"对面没了"的异常<b>刻意分开</b>的类型。
///
/// <para>
/// 🔥🔴 <b>它的全部价值在于"能被单独 catch"。</b>这两类失败的处置是相反的：
/// </para>
/// <list type="table">
///   <item>
///     <term><b>装不下</b>（本类型）</term>
///     <description><b>可恢复</b>：丢掉这一帧，<b>并且让下一帧强制是关键帧</b>
///     （否则丢掉的这一帧会成为参考链上的一个洞，观看端从此刻起画面全花，
///     而两边的帧计数都正常 —— 这正是"能自由丢帧"在 H.264 上不成立的地方）。</description>
///   </item>
///   <item>
///     <term><b>对面没了</b>（<see cref="IOException"/> / <see cref="EndOfStreamException"/> / <see cref="OperationCanceledException"/>)</term>
///     <description><b>终止</b>：接收端（服务侧）已经不在，继续循环只会一遍遍失败，
///     而每失败一次就白抓白编一帧 —— 一直在烧被控机的 CPU。</description>
///   </item>
/// </list>
///
/// <para>
/// ⚠️ <b>刻意不继承 <see cref="InvalidOperationException"/></b>：那样的话，
/// 任何一处写着 <c>catch (InvalidOperationException)</c> 的旧代码会<b>静默地</b>
/// 把"帧太大"吃掉（或反过来），而这类静默归类正是本项目反复踩到的坑。
/// 让它继承 <see cref="Exception"/> 的直接子类，是为了<b>逼调用方显式处理</b>。
/// </para>
///
/// <para>
/// 🔸 <b>它在生产路径上几乎不会被抛到</b>：调用方应当先用
/// <see cref="RemoteStreamProtocol.FitsInPipe"/> 预检。留着它是为了
/// <b>不依赖调用方记得预检</b> —— 忘了预检的后果是"一次丢帧"而不是"一次掉线"。
/// </para>
/// </summary>
public sealed class FrameTooLargeException : Exception
{
    public FrameTooLargeException(int payloadBytes)
        : base($"单帧码流 {RemoteStreamLimits.Describe(payloadBytes)}（{payloadBytes} 字节）"
            + $"超过单帧上限 {RemoteStreamLimits.Describe(RemoteStreamProtocol.MaxPayloadBytes)}"
            + $"（{RemoteStreamProtocol.MaxPayloadBytes} 字节）——"
            + "它是防呆值（挡住畸形 / 异常大的帧），不是「一条消息装不下」："
            + "后者由分片（chunking）解决，"
            + $"每片上限 {RemoteStreamLimits.Describe(RemoteStreamLimits.MaxChunkPayloadBytes)}。"
            + "这一帧应当被丢弃，而不是结束这条流。")
    {
        PayloadBytes = payloadBytes;
    }

    /// <summary>这一帧实际有多大（字节）。</summary>
    public int PayloadBytes { get; }

    /// <summary>上限是多少（字节）。日志里要能把两个数并排写出来 —— 只说"太大"说不清差多少。</summary>
    public int LimitBytes => RemoteStreamProtocol.MaxPayloadBytes;
}

/// <summary>
/// 「远控码流」的会话侧指令参数（服务 → 托盘）。
///
/// <para>
/// ⚠️ <b>它刻意只表达"要什么"，不表达"怎么编"。</b><c>JpegQuality</c> 是 M5.1
/// 脚手架专有的旋钮（换成 H.264 之后它没有意义），所以它出现在这个<b>内部</b>类型里、
/// 而不是中心的 HTTP 请求体里 —— M5.4 换编码器时改的是这里，对外一行都不用动。
/// </para>
///
/// <para>
/// 三类动作共用<b>一个扁平类型</b>而不是三个子类型：它只在这一条本地管道上跑一趟，
/// 拆成继承层次只会让"反序列化到哪个子类"变成一个新的失败点，
/// 而收益是零（这里没有需要扩展的第三方消费者）。
/// </para>
///
/// <para>
/// ⚠️ <see cref="Action"/> 的取值用<b>中心那边同一组常量</b>
/// （<c>RemoteSessionActions</c>）—— 两跳各写一组名字必然漂，
/// 而漂掉的表现是"服务说已下发、托盘不认得"。
/// </para>
/// </summary>
/// <param name="Action">见 <c>RemoteSessionActions</c>。</param>
/// <param name="SessionId">会话 Id（决定码流管道名）。</param>
/// <param name="ScreenId">要抓哪块屏；<c>null</c> = 自动（多屏时取虚拟桌面）。</param>
/// <param name="MaxFps">目标帧率上限；<c>null</c> = 本机默认值。</param>
/// <param name="JpegQuality">MJPEG 质量（脚手架旋钮）。</param>
/// <param name="Enabled"><c>grayscale</c> 用：开还是关。</param>
/// <param name="Packaging">
/// <b>参数集放在哪</b>（见 <see cref="RemoteStreamPackagings"/>）：<c>inband</c> / <c>outofband</c>。
///
/// <para>
/// 🔥 <b>做成"会话参数"而不是"被控端本地默认"，是因为它必须由观看端的能力决定</b>：
/// 能不能认 <c>avc3</c> 前缀、认不认带 <c>description</c> 的配置，都是<b>浏览器</b>说了算。
/// 被控端自己挑一个默认值，等于替浏览器做决定 —— 而它看不到浏览器。
/// </para>
///
/// <para>
/// ⚠️ <c>null</c> = <b>调用方没指定</b> ⇒ 走本批的默认（<see cref="RemoteStreamPackagings.InBand"/>）。
/// 它与"指定了 <c>inband</c>"是同一件事，但<b>不能用同一个值表达"没说"</b>：
/// 将来要改成别的默认时，"没说"必须还能被识别出来。
/// </para>
/// </param>
/// <param name="Codecs">
/// <b>可接受的编码格式，按偏好排序</b>（第 0 项 = 首选；M5.4-b 第 7 步）。
/// 取值见 <c>RemoteCodecs</c>；<c>null</c> / 空表 = 调用方没指定。
///
/// <para>
/// 🔥 <b>为什么是一个"有序集合"而不是一个格式</b>：中心算不出最终答案 ——
/// 格式由<b>目标分辨率</b>决定（H.264 硬编封顶 4096、HEVC 封顶 8192），
/// 而分辨率<b>只有在挑屏之后才知道</b>（同一台机器接不同的屏，答案就不同）。
/// 所以中心给的是"偏好 ＋ 可接受的边界"，最后一步由本机裁决。
/// </para>
///
/// <para>
/// ⚠️ <b>本机只许在这个集合里挑</b>，不许挑集合外的格式：集合本身就是
/// "浏览器声明过 ∩ 本机做得到"，越出去就会出现浏览器解不开的黑屏。
/// </para>
/// </param>
public sealed record RemoteStreamRequest(
    string Action,
    string? SessionId = null,
    string? ScreenId = null,
    int? MaxFps = null,
    int? JpegQuality = null,
    bool? Enabled = null,
    string? Packaging = null,
    IReadOnlyList<string>? Codecs = null,

    /// <summary>
    /// 质量档位名（M5.5；见 <c>RemoteQualityProfiles</c>）。<c>null</c> = 中心没提。
    /// <para>
    /// ⚠️ 它<b>只用来留痕与描述</b>（进 CodecNote / 日志），真正决定码率与帧率的是
    /// <see cref="BitrateKbps"/> 与 <see cref="MaxFps"/> —— 那两个是已经折好的数。
    /// 让托盘再按档位名自己折一次，就会出现"中心按高档折、托盘按低档折"的分叉。
    /// </para>
    /// </summary>
    string? Quality = null,

    /// <summary>
    /// 目标码率（kbps；M5.5）。<c>null</c> = 用编码器默认。
    /// <para>
    /// 🔥 <b>只有它是"能真正生效"的那一项</b>：MF 的码率在<b>创建时</b>定死，
    /// 运行中改不回来（REF-M5 铁律 16）—— 所以改码率必然重建编码器。
    /// </para>
    /// </summary>
    int? BitrateKbps = null,

    /// <summary>剪贴板动作（<c>get</c> / <c>set</c>；M5.5）。</summary>
    string? ClipboardAction = null,

    /// <summary>剪贴板文本（<c>set</c> 时）。</summary>
    string? ClipboardText = null,

    /// <summary>
    /// 只编画面里的这一块（M5.5 第 5 项：ROI）。四个值都是<b>画面像素坐标</b>。
    /// <para>
    /// ⚠️ <see cref="ViewportWidth"/> / <see cref="ViewportHeight"/> 为 <c>0</c> = 关掉 ROI。
    /// </para>
    /// </summary>
    int ViewportX = 0,

    /// <summary>视口左上角 Y。</summary>
    int ViewportY = 0,

    /// <summary>视口宽（0 = 关掉 ROI）。</summary>
    int ViewportWidth = 0,

    /// <summary>视口高（0 = 关掉 ROI）。</summary>
    int ViewportHeight = 0);
