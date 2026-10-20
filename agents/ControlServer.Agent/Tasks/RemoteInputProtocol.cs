using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using ControlServer.Contracts;

namespace ControlServer.Agent.Tasks;

/// <summary>
/// <b>会话 ↔ 服务的「输入回传」管道协议</b>（M5.2）。
///
/// <para>
/// 🔥 <b>为什么必须新开一条管道，而不是在码流那条管上反着写</b>：
/// </para>
/// <list type="bullet">
///   <item><b>帧边界会失去意义</b>：码流那条是"一串只有生产者在写的块"，
///         读取方靠"先读长度前缀、再读那么多字节"来切帧。
///         一旦两个方向往同一个字节流里写，读到的长度前缀可能来自另一方向的半句话 ——
///         表现是<b>解码出一堆垃圾或者直接卡死</b>，而两边日志都正常；</item>
///   <item><b>超时语义不同</b>：码流断了 = 这路画面没了；输入管道断了 = 还能看、只是点不动。
///         两者合成一条，"要不要把会话收掉"就没有判据了；</item>
///   <item><b>生命周期不同</b>：输入管道可以（也应当）在建会话<b>之后</b>才接上，
///         甚至允许"看了一会儿才打开控制"。</item>
/// </list>
///
/// <para>
/// 🔥 <b>方向：仍然是托盘建（服务端）、服务连上来 —— 与码流那条<b>一致</b>，别读反。</b>
/// 理由与 <see cref="RemoteStreamProtocol"/> 完全相同：命名管道的默认 DACL 是
/// 「SYSTEM + 管理员 + 创建者」。服务是 SYSTEM，由<b>它</b>建管道的话
/// 普通用户身份的托盘<b>连不上</b>（错误 5）。所以两条管道都必须托盘建。
/// </para>
/// <para>
/// ⚠️ 但<b>数据流的方向是反的</b>：这条是<b>服务写、托盘读</b>
/// （<c>PipeDirection.In</c> —— 站在"建管道那一方"看，它只<b>读</b>）。
/// 于是托盘那条 <c>WaitForConnectionAsync</c> 等的不是"消费者"，而是"下达指令的人"。
/// </para>
///
/// <para>
/// <b>帧形状</b>（小端，与码流那条同一套思路）：
/// <code>[int32 JSON 长度][JSON（UTF-8）]</code>
/// </para>
/// <list type="bullet">
///   <item>用长度前缀而不是"一行一条"：一行一条要假设 JSON 里不会出现换行，
///         而那个假设在字符串字段变异时会静默失效（读出一个半截对象）；</item>
///   <item>单条事件很小（几十到几百字节），所以这条管道上<b>没有</b>单独拆出二进制体的必要
///         —— 那正是码流那条要分头/体的理由（一帧 300 KB 的 base64 是纯浪费）。</item>
/// </list>
/// </summary>
public static class RemoteInputProtocol
{
    /// <summary>
    /// 管道名前缀。后面接会话 Id。
    /// <para>
    /// ⚠️ 与码流管道（<c>ControlServer.Agent.RemoteStream.</c>）**只差一个词**。
    /// 敲错一个字符的后果不是报错，而是"连上了另一条管子"：服务往码流管道里写输入事件，
    /// 托盘把它当成帧去解码 —— 那会是一串莫名其妙的"帧头不是合法 JSON"。
    /// 所以两边的 <c>PipeNameForSession</c> 都各自封在这个类里，调用方不许自己拼字符串。
    /// </para>
    /// </summary>
    public const string PipeNamePrefix = "ControlServer.Agent.RemoteInput.";

    /// <summary>
    /// 托盘等"下指令的人"连上来的时限（毫秒）。
    ///
    /// <para>
    /// 🔥 它比码流那条<b>宽松</b>是故意的：码流是"开始推流"的下一步，服务几毫秒内就会来接；
    /// 而输入管道允许"先看了一会儿、再打开控制开关"，工程上也可能出现
    /// "服务重启了一次、重连晚了几秒"。给得太短会让"控制开关打开了但点不动"随机出现，
    /// 而那种偶发故障最难复现。
    /// </para>
    /// </summary>
    public const int ConnectTimeoutMs = 15_000;

    /// <summary>单条输入事件的 JSON 上限（防"对方发了个离谱长度前缀把内存吃光"）。</summary>
    /// <remarks>
    /// 真实事件不到 200 字节，4 KB 已经宽松十倍以上。定这么小的好处是：
    /// 一个畸形前缀最多让我们申请 4 KB，而不是几个 G。
    /// </remarks>
    public const int MaxEventBytes = 4 * 1024;

    /// <summary>
    /// 管道上的 JSON 约定。
    /// <para>
    /// ⚠️ 与 <c>RemoteSessionCoordinator</c> 那份<b>必须一致</b>（camelCase + 大小写不敏感）：
    /// 这两个进程是用同一组 DTO 的两端，一旦有一边换了命名策略，
    /// 另一边会<b>静默地反序列化成一堆默认值</b> —— 表现是"收到了一批空事件"，
    /// 而看不出任何异常。
    /// </para>
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,

        // 只写非 null：一条"移动"事件不该带上 button/delta/virtualKey 三个 null，
        // 而这条管道每秒要跑几十条。
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>某个会话的输入管道名。</summary>
    public static string PipeNameForSession(string sessionId) => PipeNamePrefix + sessionId;

    // ═══════════════════════════════════════════════════════════
    // 写（服务侧）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 写一条输入事件。<b>会把异常抛给调用方</b> —— 由它判断"托盘那边是不是已经没了"。
    /// </summary>
    /// <returns>写进去的字节数。</returns>
    public static async Task<int> WriteAsync(
        Stream stream,
        RemoteInputEventDto input,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(input, Json);

        if (bytes.Length > MaxEventBytes)
        {
            // 不是"截断后再发"：截断后的 JSON 对面根本解析不了，
            // 而且那会让"我们发了个畸形东西"这件事消失得无影无踪。
            throw new InvalidOperationException(
                $"输入事件序列化后 {bytes.Length} 字节，超过上限 {MaxEventBytes}。");
        }

        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);

        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        return prefix.Length + bytes.Length;
    }

    // ═══════════════════════════════════════════════════════════
    // 读（托盘侧）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 读一条输入事件。
    /// </summary>
    /// <returns>
    /// 事件；<c>null</c> = <b>对面干净地结束了</b>（控制开关关了 / 服务退出）。
    /// <para>
    /// ⚠️ 只有"干净结束"才回 <c>null</c>：畸形帧一律抛。两边对协议的理解一旦不一致，
    /// 继续读只会把后面的字节也解释错，产出一堆看似合理的假事件 ——
    /// 而假事件会被真的注入进用户的桌面。
    /// </para>
    /// </returns>
    public static async Task<RemoteInputEventDto?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[4];

        if (!await TryFillAsync(stream, prefix, cancellationToken).ConfigureAwait(false)) return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);

        if (length <= 0 || length > MaxEventBytes)
        {
            throw new InvalidDataException($"输入事件长度非法：{length}。");
        }

        var bytes = new byte[length];

        if (!await TryFillAsync(stream, bytes, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("输入事件读到一半就断开了。");
        }

        RemoteInputEventDto? input;

        try
        {
            input = JsonSerializer.Deserialize<RemoteInputEventDto>(bytes, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"输入事件不是合法 JSON：{ex.Message}", ex);
        }

        if (input is null) throw new InvalidDataException("输入事件解析为空。");

        return input;
    }

    /// <summary>
    /// 把 <paramref name="buffer"/> 填满。
    /// <para>
    /// ⚠️ <b>不能只读一次就当成读满</b>：命名管道是字节流，一次 <c>ReadAsync</c>
    /// 只保证"至少读到 1 个字节"。拿半截长度前缀去解析，会得到一个随机的巨大数字。
    /// </para>
    /// </summary>
    /// <returns><c>false</c> = 一个字节都没读到（对面干净地关了）。</returns>
    private static async Task<bool> TryFillAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return offset > 0
                    ? throw new EndOfStreamException($"流读到一半结束（已读 {offset}/{buffer.Length} 字节）。")
                    : false;
            }

            offset += read;
        }

        return true;
    }
}
