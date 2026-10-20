using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 对接码监听器的收包 / 解码 / 处理调度测试。
/// <para>
/// 重点守两件事：
/// <list type="number">
///   <item><b>§10.1 回归</b>：必须用 UTF-8 解码（M1 用 ASCII，中文对接码会变 <c>?</c> 从而永远匹配不上）；</item>
///   <item><b>异常隔离</b>：单条处理失败 / 处理器报错都不能让监听退出。</item>
/// </list>
/// </para>
/// </summary>
public sealed class PairingListenerServiceTests
{
    [Fact]
    public async Task Utf8_chinese_pairing_code_is_decoded_losslessly()
    {
        // 这是 M1 缺陷的直接回归用例：ASCII 解码下 "开门" 会变成 "??"。
        await using var listener = new PairingListenerService();
        var received = new TaskCompletionSource<PairingDatagram>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.DatagramHandler = (datagram, _) =>
        {
            received.TrySetResult(datagram);
            return Task.CompletedTask;
        };

        var port = ReserveFreeUdpPort();
        listener.Start(port);

        var payload = Encoding.UTF8.GetBytes("开门");
        await SendAsync(port, payload);

        var datagram = await WithTimeout(received.Task, TimeSpan.FromSeconds(5));

        datagram.Text.Should().Be("开门", "§10.1：必须 UTF-8 解码，ASCII 会把中文变成 ??");
        datagram.Payload.Should().Equal(payload, "原始字节必须原样保留（Hex 匹配依赖它）");
        datagram.HexText.Should().Be("E5BC80E997A8");
        datagram.Length.Should().Be(payload.Length);
        datagram.RemoteIp.Should().Be("127.0.0.1");
        datagram.RemotePort.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Hex_text_is_uppercase_and_separator_free()
    {
        await using var listener = new PairingListenerService();
        var received = new TaskCompletionSource<PairingDatagram>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.DatagramHandler = (d, _) =>
        {
            received.TrySetResult(d);
            return Task.CompletedTask;
        };

        var port = ReserveFreeUdpPort();
        listener.Start(port);

        await SendAsync(port, new byte[] { 0x0A, 0xFF, 0x41 });

        var datagram = await WithTimeout(received.Task, TimeSpan.FromSeconds(5));

        datagram.HexText.Should().Be("0AFF41", "与旧版 ToHexString（大写、无分隔）一致");

        // 0x0A 是换行、会被 Trim 掉；0xFF 不是合法 UTF-8 字节，解码后被替换为 U+FFFD。
        // 这也正是「Hex 匹配必须用原始字节」的原因：文本解码是有损的。
        datagram.Text.Should().Be("\uFFFD" + "A");
        datagram.Payload.Should().Equal(0x0A, 0xFF, 0x41);
    }

    [Fact]
    public async Task Handler_that_throws_does_not_stop_the_listener()
    {
        await using var listener = new PairingListenerService();
        var seen = new List<string>();
        var errors = new List<Exception>();
        var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        listener.DatagramHandler = (datagram, _) =>
        {
            seen.Add(datagram.Text);

            if (datagram.Text == "BOOM") throw new InvalidOperationException("处理器故意失败");

            second.TrySetResult(true);
            return Task.CompletedTask;
        };
        listener.HandlerError = (_, ex) => errors.Add(ex);

        var port = ReserveFreeUdpPort();
        listener.Start(port);

        await SendAsync(port, Encoding.UTF8.GetBytes("BOOM"));
        await SendAsync(port, Encoding.UTF8.GetBytes("NEXT"));

        await WithTimeout(second.Task, TimeSpan.FromSeconds(5));

        seen.Should().Equal("BOOM", "NEXT");
        errors.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>();
        listener.IsRunning.Should().BeTrue("一包坏数据不得搞停监听");
    }

    [Fact]
    public async Task Handler_is_awaited_so_datagrams_are_processed_serially()
    {
        await using var listener = new PairingListenerService();
        var order = new List<int>();
        var maxConcurrent = 0;
        var concurrent = 0;
        var lockObj = new object();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        listener.DatagramHandler = async (datagram, _) =>
        {
            lock (lockObj)
            {
                concurrent++;
                maxConcurrent = Math.Max(maxConcurrent, concurrent);
            }

            // 故意让处理变慢：若监听循环不 await，第二个包会并发进来。
            await Task.Delay(120);

            lock (lockObj)
            {
                order.Add(int.Parse(datagram.Text));
                concurrent--;
            }

            if (order.Count == 3) done.TrySetResult(true);
        };

        var port = ReserveFreeUdpPort();
        listener.Start(port);

        for (var i = 1; i <= 3; i++)
        {
            await SendAsync(port, Encoding.UTF8.GetBytes(i.ToString()));
        }

        await WithTimeout(done.Task, TimeSpan.FromSeconds(10));

        order.Should().Equal(new[] { 1, 2, 3 }, "监听循环 await 处理器 ⇒ 对接码依次执行（与旧版一致）");
        maxConcurrent.Should().Be(1, "不得并发处理，否则日志与动作顺序会出现歧义");
    }

    [Fact]
    public async Task Datagrams_are_dropped_while_no_handler_is_attached()
    {
        await using var listener = new PairingListenerService();

        var port = ReserveFreeUdpPort();
        listener.Start(port);

        // 没有处理器时不能抛异常（相当于静默忽略）。
        var act = async () => await SendAsync(port, Encoding.UTF8.GetBytes("ignored"));

        await act.Should().NotThrowAsync();
        listener.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void Stop_is_idempotent_and_reports_not_running()
    {
        var listener = new PairingListenerService();
        var port = ReserveFreeUdpPort();
        listener.Start(port);

        listener.IsRunning.Should().BeTrue();
        listener.Port.Should().Be(port);

        listener.Stop();
        listener.Stop();   // 二次停止不得抛

        listener.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Start_after_stop_rebinds_the_port()
    {
        await using var listener = new PairingListenerService();
        var received = new TaskCompletionSource<PairingDatagram>(TaskCreationOptions.RunContinuationsAsynchronously);

        var port = ReserveFreeUdpPort();
        listener.DatagramHandler = (d, _) =>
        {
            received.TrySetResult(d);
            return Task.CompletedTask;
        };

        listener.Start(port);
        listener.Stop();
        listener.Start(port);   // 同一端口重新绑定

        await SendAsync(port, Encoding.UTF8.GetBytes("AGAIN"));

        var datagram = await WithTimeout(received.Task, TimeSpan.FromSeconds(5));
        datagram.Text.Should().Be("AGAIN");
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    private static async Task SendAsync(int port, byte[] payload)
    {
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        await sender.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        completed.Should().BeSameAs(task, $"等待数据报超时（{timeout.TotalSeconds}s）");
        return await task;
    }

    private static int ReserveFreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
