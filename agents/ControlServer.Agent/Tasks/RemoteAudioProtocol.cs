using System.Buffers.Binary;
using System.Text.Json;
using ControlServer.Contracts;

namespace ControlServer.Agent.Tasks;

/// <summary>
/// <b>会话 ↔ 服务的「远控音频」管道协议</b>（M5.3）。
///
/// <para>
/// 🔥 <b>为什么是第三条独立管道，而不是给码流那条加一个"媒体类型位"</b>：
/// </para>
/// <list type="bullet">
///   <item><b>丢帧策略相反</b>：码流那条是"队列深度 1、忙就丢"（丢一帧只是画面顿一下），
///         而<b>音频丢一帧就是一声可听见的断点</b>。一条队列没法同时服务两种相反的策略 ——
///         写代码的人迟早要在某个地方写一个 <c>if (isAudio)</c>，而那就是"两条路被塞进一条"的告示；</item>
///   <item><b>节奏差两个量级</b>：视频 12–60 fps，音频恒定 50 fps。混在一条队列里，
///         视频的"丢旧留新"会顺手把音频也丢掉（音频在队列里等的时间恰好也是几十毫秒）；</item>
///   <item><b>生死要分开</b>：音频设备掉了不该让画面停（反之亦然）。分开之后
///         "只有一边坏"这件事在结构上就是可能的，而在合并的实现里它做不到。</item>
/// </list>
///
/// <para>
/// 🔥 <b>方向：托盘建管道（服务端）、服务连上来（客户端）—— 与码流那条一致。</b>
/// 理由同 <see cref="RemoteStreamProtocol"/>：服务跑在 Session 0（SYSTEM），
/// 由它建管道的话默认 DACL 只有 SYSTEM + 管理员 + 创建者，普通用户身份的托盘连不上。
/// 而且音频同样是"托盘产、服务消费"，由生产者架管子是自然的。
/// </para>
///
/// <para>
/// <b>帧形状</b>（小端，与码流一致）：
/// <code>
/// [int32 头部长度][int32 载荷长度][头部 JSON（UTF-8）][载荷裸字节]
/// </code>
/// </para>
/// </summary>
public static class RemoteAudioProtocol
{
    /// <summary>管道名前缀，后面接会话 Id。</summary>
    /// <remarks>
    /// ⚠️ 名字里明确写了 <c>RemoteAudio</c>。当前一个会话上可能同时开着三条管道
    /// （码流 / 输入 / 音频），靠"前缀长得像但后缀不同"区分迟早会有人敲错门。
    /// </remarks>
    public const string PipeNamePrefix = "ControlServer.Agent.RemoteAudio.";

    /// <summary>
    /// 等接收端连接上来的时限（毫秒）。
    /// <para>
    /// ⚠️ 比码流那条（8 秒）短：音频<b>不是画面</b> —— 它接不上时，用户看到的应该是
    /// "有画面但没声音"，而不是让抓屏也一起多等几秒。而且音频通道接不上几乎总是
    /// "服务侧没起这条泵"（旧版本 / 刚重启），多等等不出结果。
    /// </para>
    /// </summary>
    public const int ConnectTimeoutMs = 5_000;

    /// <summary>头部 JSON 的长度上限（防畸形长度前缀把内存吃光）。</summary>
    public const int MaxHeaderBytes = 4 * 1024;

    /// <summary>
    /// 单个音频帧的载荷上限。
    /// <para>取值理由见 <see cref="RemoteAudioLimits.MaxFrameBytes"/>（8 KB）——
    /// 它比"20 ms 的 Opus"大出两个数量级，只用来挡畸形输入。</para>
    /// </summary>
    public const int MaxPayloadBytes = RemoteAudioLimits.MaxFrameBytes;

    /// <summary>管道上的 JSON 约定（与其它管道一致：camelCase + 紧凑 + 中文不转义）。</summary>
    public static readonly JsonSerializerOptions Json = SessionPipeProtocol.Json;

    /// <summary>某个会话的音频管道名。</summary>
    public static string PipeNameForSession(string sessionId) => PipeNamePrefix + sessionId;

    // ═══════════════════════════════════════════════════════════
    // 写
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 写一帧音频。<b>会把异常抛给调用方</b> —— 采集循环靠它判断"对面还在不在"。
    /// </summary>
    /// <returns>写进去的字节数（头 + 载荷），用于统计实际码率。</returns>
    public static async Task<int> WriteFrameAsync(
        Stream stream,
        RemoteAudioFrameHeader header,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, Json);

        if (headerBytes.Length > MaxHeaderBytes)
        {
            throw new InvalidOperationException($"音频帧头过大（{headerBytes.Length} 字节）。");
        }

        if (payload.Length > MaxPayloadBytes)
        {
            throw new InvalidOperationException(
                $"单帧音频 {payload.Length} 字节，超过上限 {MaxPayloadBytes} —— "
                + "正常 20 ms 的 Opus 只有几百字节，出现这个数说明采集侧送来了不是 Opus 的东西。");
        }

        var prefix = new byte[8];

        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(0, 4), headerBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4, 4), payload.Length);

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
    /// 读一帧音频。
    /// </summary>
    /// <returns>
    /// 读到的一帧；<c>null</c> = <b>对面正常结束了这条流</b>。
    /// <para>畸形帧一律抛异常（与码流同一条理由：双方对协议的理解已经不一致了）。</para>
    /// </returns>
    public static async Task<RemoteAudioFrame?> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[8];

        if (!await RemoteStreamProtocol.TryFillAsync(stream, prefix, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(0, 4));
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4, 4));

        if (headerLength <= 0 || headerLength > MaxHeaderBytes)
        {
            throw new InvalidDataException($"音频帧头长度非法：{headerLength}。");
        }

        if (payloadLength <= 0 || payloadLength > MaxPayloadBytes)
        {
            throw new InvalidDataException($"音频帧长度非法：{payloadLength}。");
        }

        var headerBytes = new byte[headerLength];
        var payload = new byte[payloadLength];

        if (!await RemoteStreamProtocol.TryFillAsync(stream, headerBytes, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("音频帧头读到一半就断开了。");
        }

        if (!await RemoteStreamProtocol.TryFillAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("音频载荷读到一半就断开了。");
        }

        RemoteAudioFrameHeader? header;

        try
        {
            header = JsonSerializer.Deserialize<RemoteAudioFrameHeader>(headerBytes, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"音频帧头不是合法 JSON：{ex.Message}", ex);
        }

        if (header is null) throw new InvalidDataException("音频帧头解析为空。");

        return new RemoteAudioFrame(header, payload);
    }
}
