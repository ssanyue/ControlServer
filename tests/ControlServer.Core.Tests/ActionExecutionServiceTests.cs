using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 动作执行内核的<b>行为等价性测试</b>。
/// 语义基准：旧版的「执行动作序列」与「单次发送」两条链路。
/// 用例覆盖 M2 方案 §11.3 的「延迟」「失败不中断」「指令库」「override」四类向量，
/// 以及 §5.3 的 WoL 语义。网络全部走 <b>127.0.0.1 真实回环</b>，不 mock socket。
/// </summary>
public sealed class ActionExecutionServiceTests
{
    // ═══════════════════════════════════════════════════════════
    // 前置延迟（§11.3）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Delay_vector_waits_only_for_actions_after_the_first()
    {
        // 旧版向量：3 条动作 [0,100,50] → 仅第 2、3 条等待；总耗时 ≈150ms。
        using var harness = new Harness();
        using var sink = new UdpSink();

        var actions = new List<ButtonAction>
        {
            Udp("A", sink.Port, delayMs: 0, content: "A"),
            Udp("B", sink.Port, delayMs: 100, content: "B"),
            Udp("C", sink.Port, delayMs: 50, content: "C"),
        };

        var result = await harness.Service.ExecuteAsync(actions, "延迟测试");

        result.SuccessCount.Should().Be(3);
        result.FailureCount.Should().Be(0);

        var elapsedMs = result.Elapsed.TotalMilliseconds;
        elapsedMs.Should().BeGreaterThanOrEqualTo(145, "两条延迟之和为 150ms（留 5ms 计时容差）");
        elapsedMs.Should().BeLessThan(700, "不应有多余等待");

        var received = await sink.CollectAsync(3, TimeSpan.FromSeconds(2));
        received.Select(Encoding.UTF8.GetString).Should().BeEquivalentTo("A", "B", "C");
    }

    [Fact]
    public async Task First_action_delay_is_ignored()
    {
        // 旧版语义：第 0 条的 DelayMs **不生效**（没有"前一个动作"可等）。
        using var harness = new Harness();
        using var sink = new UdpSink();

        var actions = new List<ButtonAction> { Udp("A", sink.Port, delayMs: 1500) };

        var result = await harness.Service.ExecuteAsync(actions, "首条不等待");

        result.SuccessCount.Should().Be(1);
        result.Elapsed.TotalMilliseconds.Should().BeLessThan(600, "首条动作的 DelayMs 必须被忽略");

        (await sink.CollectAsync(1, TimeSpan.FromSeconds(2))).Should().HaveCount(1);
    }

    // ═══════════════════════════════════════════════════════════
    // 失败不中断（§5.3）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Failure_does_not_stop_the_remaining_actions()
    {
        using var harness = new Harness();
        using var sink = new UdpSink();

        var actions = new List<ButtonAction>
        {
            // Hex 长度为奇数 → 编码期抛 FormatException → 本条失败。
            new()
            {
                Name = "坏数据", Protocol = TransportProtocols.Udp,
                TargetIp = "127.0.0.1", TargetPort = sink.Port,
                DataType = DataTypes.Hex, DataContent = "ABC",
            },
            Udp("后面的动作", sink.Port),
        };

        var result = await harness.Service.ExecuteAsync(actions, "失败不中断");

        result.Ok.Should().BeFalse();
        result.SuccessCount.Should().Be(1);
        result.FailureCount.Should().Be(1);

        result.Steps[0].Ok.Should().BeFalse();
        result.Steps[0].Message.Should().Contain("失败");
        result.Steps[1].Ok.Should().BeTrue("前一条失败不得中断后续动作");

        (await sink.CollectAsync(1, TimeSpan.FromSeconds(2))).Should().HaveCount(1);
    }

    [Fact]
    public async Task Overall_result_is_failure_when_any_step_fails()
    {
        using var harness = new Harness();

        var actions = new List<ButtonAction>
        {
            new()
            {
                Name = "非法 MAC", Protocol = TransportProtocols.Wol,
                TargetIp = "255.255.255.255", TargetPort = 9,
                DataContent = "NOT-A-MAC",
            },
        };

        var result = await harness.Service.ExecuteAsync(actions, "WoL 校验");

        result.Ok.Should().BeFalse();
        result.Steps.Single().Message.Should().Contain("MAC 地址格式无效");
        result.Summary.Should().Contain("0 成功").And.Contain("1 失败");
    }

    [Fact]
    public async Task Empty_action_list_is_a_no_op_success()
    {
        using var harness = new Harness();

        var result = await harness.Service.ExecuteAsync(Array.Empty<ButtonAction>(), "空按钮");

        result.Ok.Should().BeTrue();
        result.Steps.Should().BeEmpty();
        result.Summary.Should().Contain("没有可执行的动作");
        harness.Notifier.Results.Should().ContainSingle("每次执行都应推送一次结果");
    }

    // ═══════════════════════════════════════════════════════════
    // 子按钮 override（§5.3）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Override_replaces_ip_and_port_for_every_action()
    {
        using var harness = new Harness();
        using var sink = new UdpSink();

        var actions = new List<ButtonAction>
        {
            Udp("子按钮动作", port: 1234, ip: "10.0.0.9"),
        };

        var result = await harness.Service.ExecuteAsync(
            actions, "子按钮", overrideIp: "127.0.0.1", overridePort: sink.Port);

        result.Steps.Single().TargetIp.Should().Be("127.0.0.1");
        result.Steps.Single().TargetPort.Should().Be(sink.Port);

        (await sink.CollectAsync(1, TimeSpan.FromSeconds(2))).Should().HaveCount(1);
    }

    [Fact]
    public async Task Override_downgrades_wol_to_udp()
    {
        // 子按钮集不支持网络唤醒 → 被 override 时 WoL 必须**强制降级为 UDP**（旧版语义）。
        using var harness = new Harness();
        using var sink = new UdpSink();

        var actions = new List<ButtonAction>
        {
            new()
            {
                Name = "唤醒", Protocol = TransportProtocols.Wol,
                TargetIp = "10.0.0.9", TargetPort = 7,
                DataType = DataTypes.String, DataContent = "AABBCCDDEEFF",
            },
        };

        var result = await harness.Service.ExecuteAsync(
            actions, "子按钮", overrideIp: "127.0.0.1", overridePort: sink.Port);

        var step = result.Steps.Single();
        step.Protocol.Should().Be(TransportProtocols.Udp, "override 下 WoL 必须降级为 UDP");
        step.TargetIp.Should().Be("127.0.0.1");
        step.TargetPort.Should().Be(sink.Port);

        var received = await sink.CollectAsync(1, TimeSpan.FromSeconds(2));
        Encoding.UTF8.GetString(received.Single()).Should().Be("AABBCCDDEEFF");
    }

    [Fact]
    public void Override_without_port_keeps_action_own_port()
    {
        var action = new ButtonAction { TargetIp = "10.0.0.9", TargetPort = 7000, Protocol = TransportProtocols.Wol };

        var overridden = InvokeApplyOverride(action, "192.168.1.10", null);

        overridden.TargetIp.Should().Be("192.168.1.10");
        overridden.TargetPort.Should().Be(7000, "overridePort 为 null 时保留动作自身端口");
        overridden.Protocol.Should().Be(TransportProtocols.Udp);
    }

    [Fact]
    public void No_override_returns_the_action_untouched()
    {
        var action = new ButtonAction { TargetIp = "10.0.0.9", TargetPort = 7, Protocol = TransportProtocols.Wol };

        InvokeApplyOverride(action, null, null).Should().BeSameAs(action, "不覆盖时不应新建对象");
    }

    // ═══════════════════════════════════════════════════════════
    // Wake-on-LAN
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Wol_defaults_port_to_nine_when_action_port_is_zero()
    {
        using var harness = new Harness();

        var actions = new List<ButtonAction>
        {
            new()
            {
                Name = "开机", Protocol = TransportProtocols.Wol,
                TargetIp = "127.0.0.1", TargetPort = 0,   // 0 → 用默认 9
                DataType = DataTypes.String, DataContent = "AA:BB:CC:DD:EE:FF",
            },
        };

        var result = await harness.Service.ExecuteAsync(actions, "唤醒");

        var step = result.Steps.Single();
        step.Protocol.Should().Be(TransportProtocols.Wol);
        step.TargetPort.Should().Be(DataEncoder.DefaultWolPort);
        step.BytesSent.Should().Be(6 + 16 * 6);
        step.Message.Should().StartWith("WoL ->");
    }

    [Fact]
    public async Task Wol_delivers_the_magic_packet_bytes()
    {
        using var harness = new Harness();
        using var sink = new UdpSink();

        var actions = new List<ButtonAction>
        {
            new()
            {
                Name = "开机", Protocol = TransportProtocols.Wol,
                TargetIp = "127.0.0.1", TargetPort = sink.Port,
                DataType = DataTypes.String, DataContent = "AA:BB:CC:DD:EE:FF",
            },
        };

        var result = await harness.Service.ExecuteAsync(actions, "唤醒");

        result.Steps.Single().Ok.Should().BeTrue();

        var received = await sink.CollectAsync(1, TimeSpan.FromSeconds(2));
        received.Should().ContainSingle();
        received.Single().Should().Equal(DataEncoder.BuildMagicPacket(DataEncoder.TryParseMac("AABBCCDDEEFF")!));
    }

    [Fact]
    public async Task Wol_with_invalid_mac_fails_without_sending()
    {
        using var harness = new Harness();

        var actions = new List<ButtonAction>
        {
            new() { Protocol = TransportProtocols.Wol, TargetIp = "255.255.255.255", TargetPort = 9, DataContent = "12345" },
        };

        var result = await harness.Service.ExecuteAsync(actions, "唤醒");

        result.Ok.Should().BeFalse();
        result.Steps.Single().BytesSent.Should().Be(0);
        harness.Traffic.Entries.Single().Success.Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════
    // 指令库解析（§5.3）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Command_library_action_sends_the_resolved_payload()
    {
        using var harness = new Harness(
            new CommandItem { Name = "开灯", Protocol = TransportProtocols.Udp, DataType = DataTypes.Hex, Data = "41 42" });
        using var sink = new UdpSink();

        var actions = new List<ButtonAction>
        {
            new()
            {
                Name = "走指令库", Protocol = TransportProtocols.Udp,
                TargetIp = "127.0.0.1", TargetPort = sink.Port,
                DataType = DataTypes.CommandLibrary, DataContent = "开灯",
            },
        };

        var result = await harness.Service.ExecuteAsync(actions, "指令库");

        var step = result.Steps.Single();
        step.Ok.Should().BeTrue();
        step.ResolvedFromCommand.Should().Be("开灯");
        step.DataPreview.Should().Be("41 42");

        (await sink.CollectAsync(1, TimeSpan.FromSeconds(2))).Single().Should().Equal(0x41, 0x42);
    }

    [Fact]
    public async Task Missing_command_fails_only_that_action()
    {
        using var harness = new Harness();
        using var sink = new UdpSink();

        var actions = new List<ButtonAction>
        {
            new()
            {
                Name = "缺指令", Protocol = TransportProtocols.Udp,
                TargetIp = "127.0.0.1", TargetPort = sink.Port,
                DataType = DataTypes.CommandLibrary, DataContent = "不存在的指令",
            },
            Udp("后续动作", sink.Port),
        };

        var result = await harness.Service.ExecuteAsync(actions, "指令库缺失");

        result.Steps[0].Ok.Should().BeFalse();
        result.Steps[0].Message.Should().Contain("指令库解析失败");
        result.Steps[0].BytesSent.Should().Be(0);
        result.Steps[1].Ok.Should().BeTrue("指令库未找到不应中断后续动作");

        (await sink.CollectAsync(1, TimeSpan.FromSeconds(2))).Should().HaveCount(1);
    }

    // ═══════════════════════════════════════════════════════════
    // 收发日志 / 推送
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Every_action_produces_one_traffic_log_entry()
    {
        using var harness = new Harness();
        using var sink = new UdpSink();

        var actions = new List<ButtonAction> { Udp("甲", sink.Port), Udp("乙", sink.Port) };

        await harness.Service.ExecuteAsync(actions, "日志验证");

        harness.Traffic.Entries.Should().HaveCount(2);
        harness.Traffic.Entries.Should().OnlyContain(e => e.Direction == TrafficDirections.Send);
        harness.Traffic.Entries.Should().OnlyContain(e => e.Success);
        harness.Traffic.Entries.Select(e => e.ActionName).Should().Equal("甲", "乙");
        harness.Traffic.Entries.Should().OnlyContain(e => e.ButtonName == "日志验证");
        harness.Traffic.Entries.Should().OnlyContain(e => e.Protocol == TransportProtocols.Udp);
    }

    [Fact]
    public async Task Traffic_log_truncates_overlong_data()
    {
        using var harness = new Harness();
        using var sink = new UdpSink();

        var big = new string('x', 500);
        var actions = new List<ButtonAction> { Udp("超长", sink.Port, content: big) };

        await harness.Service.ExecuteAsync(actions, "截断");

        var entry = harness.Traffic.Entries.Single();
        entry.Data.Should().HaveLength(TrafficLogEntry.MaxDataLength + 1, "200 字符 + 省略号");
        entry.Data.Should().EndWith("…");
    }

    [Fact]
    public async Task Execution_pushes_exactly_one_action_result()
    {
        using var harness = new Harness();
        using var sink = new UdpSink();

        await harness.Service.ExecuteAsync(new List<ButtonAction> { Udp("甲", sink.Port) }, "推送");

        var pushed = harness.Notifier.Results.Should().ContainSingle().Subject;
        pushed.Ok.Should().BeTrue();
        pushed.RunId.Should().NotBeNullOrWhiteSpace();
        pushed.Message.Should().Contain("全部成功");
    }

    [Fact]
    public async Task Action_name_falls_back_to_protocol_when_blank()
    {
        using var harness = new Harness();
        using var sink = new UdpSink();

        var action = Udp("", sink.Port);

        var result = await harness.Service.ExecuteAsync(new List<ButtonAction> { action }, "回退");

        result.Steps.Single().ActionName.Should().Be(TransportProtocols.Udp);
    }

    [Fact]
    public async Task Cancellation_between_actions_propagates()
    {
        using var harness = new Harness();
        using var sink = new UdpSink();

        using var cts = new CancellationTokenSource();
        var actions = new List<ButtonAction>
        {
            Udp("甲", sink.Port, delayMs: 0),
            Udp("乙", sink.Port, delayMs: 5000),
        };

        // 首条发出后立刻取消 → 第二条的延迟应当抛出 OperationCanceledException。
        _ = Task.Run(async () => { await Task.Delay(150); cts.Cancel(); });

        var act = async () => await harness.Service.ExecuteAsync(actions, "取消", cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    private static ButtonAction Udp(
        string name,
        int port,
        int delayMs = 0,
        string ip = "127.0.0.1",
        string content = "PING")
        => new()
        {
            Name = name,
            Protocol = TransportProtocols.Udp,
            TargetIp = ip,
            TargetPort = port,
            DataType = DataTypes.Utf8,
            DataContent = content,
            DelayMs = delayMs,
        };

    /// <summary>
    /// <c>ApplyOverride</c> 是私有静态方法。它是"子按钮统一 IP/端口 + WoL 降级"这条
    /// 行为契约的落点，必须能单独验证，因此这里显式反射调用（比放宽可见性更可控）。
    /// </summary>
    private static ButtonAction InvokeApplyOverride(ButtonAction action, string? overrideIp, int? overridePort)
    {
        var method = typeof(ActionExecutionService).GetMethod(
            "ApplyOverride",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        method.Should().NotBeNull("ApplyOverride 必须存在（私有静态）");

        return (ButtonAction)method!.Invoke(null, new object?[] { action, overrideIp, overridePort })!;
    }

    // ── 测试替身 ──

    /// <summary>统一装配：临时数据目录 + LogService + 三个替身 + 被测服务。</summary>
    private sealed class Harness : IDisposable
    {
        private readonly TempDataDirectory _dataDirectory = new();

        public Harness(params CommandItem[] commands)
        {
            Log = new LogService(_dataDirectory, capacity: 200, flushIntervalMs: 60_000);
            Traffic = new RecordingTrafficLog();
            Notifier = new NullNotifier();
            Commands = new FakeCommandLibrary(commands);

            Service = new ActionExecutionService(new NetworkService(), Traffic, Commands, Notifier, Log);
        }

        public LogService Log { get; }

        public RecordingTrafficLog Traffic { get; }

        public NullNotifier Notifier { get; }

        public FakeCommandLibrary Commands { get; }

        public ActionExecutionService Service { get; }

        public void Dispose()
        {
            Log.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _dataDirectory.Dispose();
        }
    }

    private sealed class RecordingTrafficLog : ITrafficLog
    {
        private readonly List<TrafficLogEntry> _entries = new();

        public IReadOnlyList<TrafficLogEntry> Entries => _entries;

        public int Count => _entries.Count;

        public void Write(TrafficLogEntry entry) => _entries.Add(entry);

        public IReadOnlyList<TrafficLogEntry> Recent(int count = 200)
            => _entries.TakeLast(count).ToList();
    }

    private sealed class FakeCommandLibrary : ICommandLibrary
    {
        private readonly List<CommandItem> _items;

        public FakeCommandLibrary(params CommandItem[] items) => _items = items.ToList();

        public Task<IReadOnlyList<CommandItem>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommandItem>>(_items);

        public Task<CommandItem?> ResolveAsync(string? name, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(name)) return Task.FromResult<CommandItem?>(null);

            var target = name.Trim();
            return Task.FromResult(_items.FirstOrDefault(c =>
                string.Equals(c.Name, target, StringComparison.OrdinalIgnoreCase)));
        }
    }


    /// <summary>回环 UDP 接收器：拿空闲端口 + 收集收到的数据报。</summary>
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

        /// <summary>等到收满 <paramref name="expected"/> 个数据报或超时，返回已收到的全部。</summary>
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
