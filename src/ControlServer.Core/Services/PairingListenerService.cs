using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ControlServer.Core.Services;

/// <summary>收到的一条对接码数据报。</summary>
/// <param name="Timestamp">接收时间（北京时间）。</param>
/// <param name="RemoteIp">来源 IP。</param>
/// <param name="RemotePort">来源端口。</param>
/// <param name="Text">
/// 原始字节按 <b>UTF-8</b> 解码并 <c>Trim()</c> 后的文本。
/// <para>
/// ⚠️ M1 这里用的是 <c>Encoding.ASCII</c>，与旧版（<c>Encoding.UTF8</c>）<b>不等价</b>：
/// 中文 / 非 ASCII 对接码会被解码成 <c>?</c>，从而<b>永远匹配不上</b>。
/// 这条在 M2-B 的真服务冒烟中被实证（监听日志出现 <c>ASCII="??" · HEX=D6D0</c>），
/// 修正见 M2 方案 §10.1。字段名同时由 <c>AsciiText</c> 改为 <c>Text</c>，避免误导。
/// </para>
/// </param>
/// <param name="Payload">
/// <b>原始字节</b>（不做任何解码）。Hex 类型对接码的比较必须用它 ——
/// 文本解码是有损的（非法字节会被替换），不能拿解码结果反推字节。
/// </param>
/// <param name="HexText">原始字节的大写十六进制串（无分隔），仅用于日志展示。</param>
/// <param name="Length">数据报字节数。</param>
public sealed record PairingDatagram(
    DateTimeOffset Timestamp,
    string RemoteIp,
    int RemotePort,
    string Text,
    byte[] Payload,
    string HexText,
    int Length)
{
    /// <summary>「ip:port」形式，仅用于日志展示。</summary>
    public string RemoteEndpoint => $"{RemoteIp}:{RemotePort}";
}

/// <summary>
/// 对接码监听（UDP，默认 9999）。只负责「收包 → 解码」这一段。
/// <para>
/// 本类<b>只负责收包与解码</b>：把原始字节变成 <see cref="PairingDatagram"/>，
/// 逐个交给 <see cref="DatagramHandler"/>。匹配与执行在 Core 的
/// <see cref="PairingDispatchService"/>，编排在宿主的 HostedService —— 职责单向，便于单测。
/// </para>
/// <para>
/// <b>串行语义（与旧版一致）</b>：监听循环会 <c>await</c> 处理器，因此多个对接码是
/// <b>依次</b>执行的（旧版同样在收包循环里 await 执行动作）。
/// 这避免了两个对接码并发触发时的日志交错与顺序歧义。
/// </para>
/// </summary>
public sealed class PairingListenerService : IAsyncDisposable
{
    private UdpClient? _client;
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }

    public int Port { get; private set; }

    /// <summary>
    /// 数据报处理器。<b>监听循环会 await 它</b>，所以处理器内部可以放心做
    /// "取配置 → 匹配 → 发 UDP" 这类长耗时工作。处理器抛出的异常只会被记录，不会终止监听。
    /// </summary>
    public Func<PairingDatagram, CancellationToken, Task>? DatagramHandler { get; set; }

    /// <summary>单条处理抛出异常时的回调（供宿主记日志）。回调自身异常被吞掉。</summary>
    public Action<PairingDatagram, Exception>? HandlerError { get; set; }

    /// <summary>启动监听。失败会抛出，由调用方（HostedService）记录。</summary>
    public void Start(int port)
    {
        if (IsRunning) Stop();

        Port = port;
        _cts = new CancellationTokenSource();

        // 绑定 IPAddress.Any：局域网任意网卡的对接码都要能收到。
        // EnableBroadcast：允许收广播（部分设备以 255.255.255.255 方式发对接码）。
        _client = new UdpClient(new IPEndPoint(IPAddress.Any, port)) { EnableBroadcast = true };
        IsRunning = true;
        _ = Task.Run(() => ReceiveLoopAsync(_client, _cts.Token));
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            PairingDatagram datagram;

            // ── 收包（异常只影响本次接收，绝不退出循环）──
            try
            {
                var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                datagram = BuildDatagram(result);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception)
            {
                // 网卡抖动 / ICMP 端口不可达等：短暂退避后继续。
                try { await Task.Delay(200, cancellationToken).ConfigureAwait(false); }
                catch { break; }

                continue;
            }

            var handler = DatagramHandler;
            if (handler is null) continue;

            // ── 处理（await ⇒ 串行；异常隔离在单条之内）──
            try
            {
                await handler(datagram, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                try { HandlerError?.Invoke(datagram, ex); } catch { /* 回调异常同样吞掉 */ }
            }
        }
    }

    /// <summary>
    /// 原始数据报 → <see cref="PairingDatagram"/>。
    /// 解码规则<b>逐字照旧版</b>：<c>Encoding.UTF8.GetString(buffer).Trim()</c>。
    /// </summary>
    private static PairingDatagram BuildDatagram(UdpReceiveResult result)
    {
        var bytes = result.Buffer ?? Array.Empty<byte>();

        return new PairingDatagram(
            BeijingTime.Now,
            result.RemoteEndPoint.Address.ToString(),
            result.RemoteEndPoint.Port,
            Encoding.UTF8.GetString(bytes).Trim(),
            bytes,
            DataEncoder.ToHexString(bytes),
            bytes.Length);
    }

    public void Stop()
    {
        IsRunning = false;

        try { _cts?.Cancel(); } catch { /* ignore */ }

        try { _client?.Dispose(); } catch { /* ignore */ }
        _client = null;

        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
