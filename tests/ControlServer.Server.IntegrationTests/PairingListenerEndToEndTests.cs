using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 对接码监听的<b>真宿主端到端</b>回归：真 Kestrel 宿主 + 真 SQLite 配置 + 真 UDP 收包 + 真 UDP 出网。
/// <para>
/// 为什么不只在 Core 里测：这条链路上有三个只有真宿主才会暴露的接缝 ——
/// ① DI 是否把 <see cref="PairingDispatchService"/> 装对；② HostedService 是否真的启动了监听；
/// ③ 「编辑页保存配置 → 下一包对接码立即生效」是否成立（配置要走 EF 落盘再读回来）。
/// </para>
/// <para>网络全部走 127.0.0.1 回环，不 mock socket。</para>
/// </summary>
public sealed class PairingListenerEndToEndTests : IDisposable
{
    private readonly ControlServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // ═══════════════════════════════════════════════════════════
    // 按钮级
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Button_pairing_code_triggers_the_configured_udp_action()
    {
        using var sink = new UdpSink();
        using var client = _factory.CreateClient();

        (await client.GetAsync("/readyz")).StatusCode
            .Should().Be(HttpStatusCode.OK, "对接码监听必须已就绪");

        await AddButtonAsync(factory: _factory, new ButtonConfig
        {
            Label = "端到端按钮",
            PairingCode = "E2E_OPEN",
            Actions =
            {
                new ButtonAction
                {
                    Name = "发数据",
                    Protocol = TransportProtocols.Udp,
                    TargetIp = "127.0.0.1",
                    TargetPort = sink.Port,
                    DataType = DataTypes.Utf8,
                    DataContent = "E2E-PAYLOAD",
                },
            },
        });

        await SendAsync(_factory.PairingPort, Encoding.UTF8.GetBytes("E2E_OPEN"));

        var received = await sink.CollectAsync(1, TimeSpan.FromSeconds(8));
        received.Should().ContainSingle("命中按钮对接码必须真的把数据发出去");
        Encoding.UTF8.GetString(received[0]).Should().Be("E2E-PAYLOAD");

        // 收发日志：1 条接收（带按钮名）+ 1 条发送
        var traffic = _factory.Services.GetRequiredService<ITrafficLog>().Recent(50);
        traffic.Should().Contain(e =>
            e.Direction == TrafficDirections.Receive &&
            e.Message == "接收对接码: E2E_OPEN" &&
            e.ButtonName == "端到端按钮");
        traffic.Should().Contain(e =>
            e.Direction == TrafficDirections.Send &&
            e.ButtonName == "端到端按钮" &&
            e.Success);
    }

    [Fact]
    public async Task Chinese_utf8_pairing_code_works_end_to_end()
    {
        // §10.1 的真宿主回归：M1 用 ASCII 解码时，这条用例必然失败（"开门" → "??"）。
        using var sink = new UdpSink();
        using var client = _factory.CreateClient();
        (await client.GetAsync("/readyz")).StatusCode.Should().Be(HttpStatusCode.OK);

        await AddButtonAsync(factory: _factory, new ButtonConfig
        {
            Label = "中文码按钮",
            PairingCode = "开门",
            Actions = { Udp("开门动作", sink.Port, "OPEN") },
        });

        await SendAsync(_factory.PairingPort, Encoding.UTF8.GetBytes("开门"));

        var received = await sink.CollectAsync(1, TimeSpan.FromSeconds(8));
        received.Should().ContainSingle();
        Encoding.UTF8.GetString(received[0]).Should().Be("OPEN");

        _factory.Services.GetRequiredService<ITrafficLog>().Recent(50)
            .Should().Contain(e => e.Direction == TrafficDirections.Receive && e.Message == "接收对接码: 开门");
    }

    // ═══════════════════════════════════════════════════════════
    // 子按钮级（IP / 端口由集合统一覆盖）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task SubButton_pairing_code_uses_the_sets_target_and_overrides_the_action()
    {
        using var sink = new UdpSink();
        using var client = _factory.CreateClient();
        (await client.GetAsync("/readyz")).StatusCode.Should().Be(HttpStatusCode.OK);

        var settings = _factory.Services.GetRequiredService<ControlSettingsService>();

        var set = new SubButtonSet
        {
            Label = "机房",
            TargetIp = "127.0.0.1",
            TargetPort = sink.Port,                       // 集合端口 = 我们的收包端口
            SubButtons =
            {
                new SubButton
                {
                    Label = "空调",
                    PairingCode = "AC_ON",
                    // 动作自身端口故意写成不可达的 1：若 override 没生效，这条会失败。
                    Actions = { Udp("开空调", port: 1, content: "AC") },
                },
            },
        };

        await settings.AddSubButtonSetAsync(set, null, "e2e");

        await SendAsync(_factory.PairingPort, Encoding.UTF8.GetBytes("AC_ON"));

        var received = await sink.CollectAsync(1, TimeSpan.FromSeconds(8));
        received.Should().ContainSingle("子按钮动作的目标端口必须被所属集合覆盖");
        Encoding.UTF8.GetString(received[0]).Should().Be("AC");

        var traffic = _factory.Services.GetRequiredService<ITrafficLog>().Recent(50);
        traffic.Should().Contain(e =>
            e.Direction == TrafficDirections.Receive &&
            e.Message == "接收子按钮对接码: AC_ON" &&
            e.ButtonName == "空调");
        traffic.Should().Contain(e =>
            e.Direction == TrafficDirections.Send &&
            e.Port == sink.Port &&
            e.Success);
    }

    // ═══════════════════════════════════════════════════════════
    // 未命中 / 优先级
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Unmatched_code_produces_no_send_and_a_plain_receive_log()
    {
        using var sink = new UdpSink();
        using var client = _factory.CreateClient();
        (await client.GetAsync("/readyz")).StatusCode.Should().Be(HttpStatusCode.OK);

        await AddButtonAsync(factory: _factory, new ButtonConfig
        {
            Label = "已知",
            PairingCode = "KNOWN",
            Actions = { Udp("动作", sink.Port, "SHOULD-NOT-SEND") },
        });

        await SendAsync(_factory.PairingPort, Encoding.UTF8.GetBytes("UNKNOWN-CODE"));

        // 给监听足够时间处理，然后确认"什么都没发"。
        await Task.Delay(1500);
        sink.Received.Should().BeEmpty("未命中不得发出任何数据");

        _factory.Services.GetRequiredService<ITrafficLog>().Recent(50)
            .Should().Contain(e =>
                e.Direction == TrafficDirections.Receive &&
                e.Message == "接收数据 12 字符" &&
                e.ButtonName == string.Empty);
    }

    [Fact]
    public async Task Button_level_wins_over_action_level_for_the_same_code_on_a_live_host()
    {
        using var sink = new UdpSink();
        using var client = _factory.CreateClient();
        (await client.GetAsync("/readyz")).StatusCode.Should().Be(HttpStatusCode.OK);

        var settings = _factory.Services.GetRequiredService<ControlSettingsService>();

        // 按钮 A：按钮级对接码 = DUP，动作发 "BY-BUTTON"
        await settings.AddButtonAsync(new ButtonConfig
        {
            Label = "按钮级",
            PairingCode = "DUP",
            Actions = { Udp("甲", sink.Port, "BY-BUTTON") },
        }, null, "e2e");

        // 按钮 B：无按钮级对接码，但有一条动作级对接码 = DUP，动作发 "BY-ACTION"
        await settings.AddButtonAsync(new ButtonConfig
        {
            Label = "动作级",
            Actions = { new ButtonAction { Name = "乙", Protocol = TransportProtocols.Udp, TargetIp = "127.0.0.1", TargetPort = sink.Port, DataType = DataTypes.Utf8, DataContent = "BY-ACTION", PairingCode = "DUP" } },
        }, null, "e2e");

        await SendAsync(_factory.PairingPort, Encoding.UTF8.GetBytes("DUP"));

        var received = await sink.CollectAsync(1, TimeSpan.FromSeconds(8));
        received.Should().ContainSingle();
        Encoding.UTF8.GetString(received[0]).Should().Be("BY-BUTTON", "§11.3：按钮级优先于动作级");

        _factory.Services.GetRequiredService<ITrafficLog>().Recent(50)
            .Should().Contain(e => e.Direction == TrafficDirections.Receive && e.ButtonName == "按钮级");
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    private static async Task AddButtonAsync(ControlServerFactory factory, ButtonConfig button)
    {
        var settings = factory.Services.GetRequiredService<ControlSettingsService>();
        await settings.AddButtonAsync(button, null, "e2e");
    }

    private static ButtonAction Udp(string name, int port, string content, string ip = "127.0.0.1")
        => new()
        {
            Name = name,
            Protocol = TransportProtocols.Udp,
            TargetIp = ip,
            TargetPort = port,
            DataType = DataTypes.Utf8,
            DataContent = content,
        };

    private static async Task SendAsync(int port, byte[] payload)
    {
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        await sender.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, port));
    }

    /// <summary>回环 UDP 接收器：绑定空闲端口并收集收到的数据报。</summary>
    private sealed class UdpSink : IDisposable
    {
        private readonly UdpClient _client = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly ConcurrentQueue<byte[]> _received = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public UdpSink()
        {
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        var result = await _client.ReceiveAsync(_cts.Token);
                        _received.Enqueue(result.Buffer);
                    }
                    catch
                    {
                        break;
                    }
                }
            });
        }

        public int Port => ((IPEndPoint)_client.Client.LocalEndPoint!).Port;

        public IReadOnlyList<byte[]> Received => _received.ToArray();

        public async Task<IReadOnlyList<byte[]>> CollectAsync(int expected, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (_received.Count < expected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            return _received.ToArray();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _client.Dispose();
            try { _loop.Wait(TimeSpan.FromSeconds(1)); } catch { /* 忽略收尾异常 */ }
        }
    }
}
