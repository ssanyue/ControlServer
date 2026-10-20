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
/// 对接码<b>派发链</b>测试：收包 → 取配置 → 三级匹配 → 记接收日志 → 执行动作。
/// 网络走 <b>127.0.0.1 真实回环</b>（不 mock socket），配置用内存版文档存储。
/// </summary>
public sealed class PairingDispatchServiceTests
{
    [Fact]
    public async Task Unmatched_datagram_only_writes_one_receive_log()
    {
        using var h = new Harness();
        using var sink = new UdpSink();

        h.SeedConfig(new ControlSettings
        {
            Buttons =
            {
                new ButtonConfig { Label = "按钮", PairingCode = "KNOWN", Actions = { Udp("动作", sink.Port, "PAYLOAD") } },
            },
        });

        var outcome = await h.Dispatcher.DispatchAsync(Datagram("UNKNOWN"));

        outcome.Matched.Should().BeFalse();
        outcome.Execution.Should().BeNull("未命中就没有动作可执行");

        h.Traffic.Entries.Should().ContainSingle();
        var entry = h.Traffic.Entries[0];
        entry.Direction.Should().Be(TrafficDirections.Receive);
        entry.Data.Should().Be("UNKNOWN");
        entry.Message.Should().Be("接收数据 7 字符", "与旧版未匹配分支的措辞一致");
        entry.ButtonName.Should().BeEmpty();

        await Task.Delay(80);
        sink.Received.Should().BeEmpty("未命中绝不能发出任何数据");
    }

    [Fact]
    public async Task Button_match_executes_every_action_and_logs_receive_and_send()
    {
        using var h = new Harness();
        using var sink = new UdpSink();

        h.SeedConfig(new ControlSettings
        {
            Buttons =
            {
                new ButtonConfig
                {
                    Label = "客厅灯",
                    PairingCode = "LIGHT_ON",
                    Actions = { Udp("开灯", sink.Port, "A"), Udp("留痕", sink.Port, "B") },
                },
            },
        });

        var outcome = await h.Dispatcher.DispatchAsync(Datagram("LIGHT_ON"));

        outcome.Matched.Should().BeTrue();
        outcome.Kind.Should().Be(PairingMatchKind.Button);
        outcome.OwnerLabel.Should().Be("客厅灯");
        outcome.Execution!.SuccessCount.Should().Be(2);

        // 接收日志（1 条）+ 发送日志（2 条）
        h.Traffic.Entries.Should().HaveCount(3);
        h.Traffic.Entries[0].Direction.Should().Be(TrafficDirections.Receive);
        h.Traffic.Entries[0].Message.Should().Be("接收对接码: LIGHT_ON");
        h.Traffic.Entries[0].ButtonName.Should().Be("客厅灯");

        h.Traffic.Entries.Skip(1).Select(e => e.Direction).Should().AllBe(TrafficDirections.Send);
        h.Traffic.Entries.Skip(1).Should().OnlyContain(e => e.ButtonName == "客厅灯");

        // 真的出网了
        var received = await sink.CollectAsync(2, TimeSpan.FromSeconds(2));
        received.Select(Encoding.UTF8.GetString).Should().Equal("A", "B");
    }

    [Fact]
    public async Task SubButton_match_forces_the_owning_sets_ip_and_port()
    {
        using var h = new Harness();
        using var sink = new UdpSink();

        h.SeedConfig(new ControlSettings
        {
            SubButtonSets =
            {
                new SubButtonSet
                {
                    Label = "机房",
                    TargetIp = "127.0.0.1",
                    TargetPort = sink.Port,
                    SubButtons =
                    {
                        new SubButton
                        {
                            Label = "空调",
                            PairingCode = "AC_ON",
                            // 故意把动作自身的目标指到别处（不可达端口），验证 override 真的生效。
                            Actions = { Udp("开空调", port: 1, content: "AC") },
                        },
                    },
                },
            },
        });

        var outcome = await h.Dispatcher.DispatchAsync(Datagram("AC_ON"));

        outcome.Kind.Should().Be(PairingMatchKind.SubButton);
        outcome.Execution!.Steps.Single().TargetPort.Should().Be(sink.Port, "子按钮动作的端口必须被所属集合覆盖");
        outcome.Execution.Steps.Single().TargetIp.Should().Be("127.0.0.1");

        h.Traffic.Entries[0].Message.Should().Be("接收子按钮对接码: AC_ON");
        h.Traffic.Entries[0].ButtonName.Should().Be("空调");

        (await sink.CollectAsync(1, TimeSpan.FromSeconds(2))).Select(Encoding.UTF8.GetString).Should().Equal("AC");
    }

    [Fact]
    public async Task Action_match_inside_a_subbutton_sends_only_that_action_with_set_target()
    {
        using var h = new Harness();
        using var sink = new UdpSink();

        h.SeedConfig(new ControlSettings
        {
            SubButtonSets =
            {
                new SubButtonSet
                {
                    Label = "集",
                    TargetIp = "127.0.0.1",
                    TargetPort = sink.Port,
                    SubButtons =
                    {
                        new SubButton
                        {
                            Label = "子",
                            PairingCode = "",
                            Actions =
                            {
                                Udp("命中这条", port: 1, content: "HIT", pairingCode: "SUB_ACT"),
                                Udp("不该发", port: 1, content: "MISS"),
                            },
                        },
                    },
                },
            },
        });

        var outcome = await h.Dispatcher.DispatchAsync(Datagram("SUB_ACT"));

        outcome.Kind.Should().Be(PairingMatchKind.Action);
        outcome.Execution!.Steps.Should().ContainSingle("动作级命中只执行一条");

        h.Traffic.Entries[0].Message.Should().Be("接收动作对接码: SUB_ACT");
        h.Traffic.Entries[0].ButtonName.Should().Be("子 / 命中这条");

        (await sink.CollectAsync(1, TimeSpan.FromSeconds(2))).Select(Encoding.UTF8.GetString).Should().Equal("HIT");
    }

    [Fact]
    public async Task Chinese_utf8_pairing_code_end_to_end()
    {
        // §10.1 的正面用例：UTF-8 中文对接码必须能命中（M1 的 ASCII 解码下必然失败）。
        using var h = new Harness();
        using var sink = new UdpSink();

        h.SeedConfig(new ControlSettings
        {
            Buttons =
            {
                new ButtonConfig { Label = "门", PairingCode = "开门", Actions = { Udp("开门动作", sink.Port, "OPEN") } },
            },
        });

        var payload = Encoding.UTF8.GetBytes("开门");
        var outcome = await h.Dispatcher.DispatchAsync(Datagram("开门", payload));

        outcome.Matched.Should().BeTrue();
        outcome.OwnerLabel.Should().Be("门");
        (await sink.CollectAsync(1, TimeSpan.FromSeconds(2))).Select(Encoding.UTF8.GetString).Should().Equal("OPEN");
    }

    [Fact]
    public async Task Latest_config_edit_takes_effect_on_the_next_datagram()
    {
        using var h = new Harness();
        using var sink = new UdpSink();

        // 先配一个不匹配的按钮。
        h.SeedConfig(new ControlSettings
        {
            Buttons = { new ButtonConfig { Label = "旧", PairingCode = "OLD" } },
        });

        (await h.Dispatcher.DispatchAsync(Datagram("NEW"))).Matched.Should().BeFalse();

        // 编辑页改成 NEW 之后，下一包必须立刻生效（不能吃到旧配置缓存）。
        h.SeedConfig(new ControlSettings
        {
            Buttons = { new ButtonConfig { Label = "新", PairingCode = "NEW", Actions = { Udp("动作", sink.Port, "N") } } },
        });

        var outcome = await h.Dispatcher.DispatchAsync(Datagram("NEW"));

        outcome.Matched.Should().BeTrue();
        outcome.OwnerLabel.Should().Be("新");
        (await sink.CollectAsync(1, TimeSpan.FromSeconds(2))).Should().ContainSingle();
    }

    [Fact]
    public async Task Matched_button_without_actions_reports_success_with_zero_steps()
    {
        using var h = new Harness();

        h.SeedConfig(new ControlSettings
        {
            Buttons = { new ButtonConfig { Label = "空按钮", PairingCode = "EMPTY" } },
        });

        var outcome = await h.Dispatcher.DispatchAsync(Datagram("EMPTY"));

        outcome.Matched.Should().BeTrue();
        outcome.Execution!.Steps.Should().BeEmpty();
        outcome.Execution.Ok.Should().BeTrue("空序列视为成功（旧版语义）");
    }

    [Fact]
    public async Task One_failing_action_does_not_stop_the_rest_of_a_button()
    {
        using var h = new Harness();
        using var sink = new UdpSink();

        h.SeedConfig(new ControlSettings
        {
            Buttons =
            {
                new ButtonConfig
                {
                    Label = "混合",
                    PairingCode = "MIX",
                    Actions =
                    {
                        Udp("坏Hex", sink.Port, "ABC", dataType: DataTypes.Hex),
                        Udp("好动作", sink.Port, "OK"),
                    },
                },
            },
        });

        var outcome = await h.Dispatcher.DispatchAsync(Datagram("MIX"));

        outcome.Execution!.Steps[0].Ok.Should().BeFalse("奇数长度 Hex 必须失败");
        outcome.Execution.Steps[1].Ok.Should().BeTrue("失败不中断后续动作");
        outcome.Execution.Ok.Should().BeFalse();

        (await sink.CollectAsync(1, TimeSpan.FromSeconds(2))).Select(Encoding.UTF8.GetString).Should().Equal("OK");
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助 / 替身
    // ═══════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════
    // 组合按钮（2026-10-15，需求⑤）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 组合对接码命中时：**按成员顺序**把两个按钮的动作都发出去，段间等待配置的延迟。
    /// <para>
    /// 断言三件事，缺一个都不足以说明"组合真的按顺序执行了"：
    /// ① 两个成员的包**都发出去了**；② 顺序正确（先 A 后 B）；③ 段间延迟真的等了。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Combo_match_executes_every_member_in_order_with_the_configured_delay()
    {
        using var h = new Harness();
        using var sink = new UdpSink();

        var first = new ButtonConfig { Label = "灯光", Actions = { Udp("开灯", sink.Port, "LIGHT") } };
        var second = new ButtonConfig { Label = "音响", Actions = { Udp("开音响", sink.Port, "AUDIO") } };

        h.SeedConfig(new ControlSettings
        {
            Buttons = { first, second },
            ComboButtons =
            {
                new ComboButton
                {
                    Label = "开场",
                    PairingCode = "OPEN",
                    Steps = new List<ComboButtonStep>
                    {
                        // ⚠️ 延迟挂在**第一步**上 = "执行完灯光之后、音响之前等 150ms"。
                        new() { ButtonId = first.Id, DelayMs = 150 },
                        new() { ButtonId = second.Id, DelayMs = 0 },
                    },
                },
            },
        });

        var started = DateTime.UtcNow;
        var outcome = await h.Dispatcher.DispatchAsync(Datagram("OPEN"));

        var packets = await sink.CollectAsync(2, TimeSpan.FromSeconds(2));
        var elapsed = DateTime.UtcNow - started;

        outcome.Matched.Should().BeTrue();
        outcome.Kind.Should().Be(PairingMatchKind.Combo);
        outcome.OwnerLabel.Should().Be("开场");

        outcome.Execution.Should().BeNull("组合走的是 ExecuteComboAsync 那条路，不是单按钮那条");
        outcome.ComboExecution.Should().NotBeNull();

        var combo = outcome.ComboExecution!;
        combo.Ok.Should().BeTrue();
        combo.Segments.Should().HaveCount(2);
        combo.Segments.Select(x => x.ButtonLabel).Should().Equal(new[] { "灯光", "音响" }, "顺序就是配置顺序");
        combo.TotalSteps.Should().Be(2);

        packets.Should().HaveCount(2);
        Encoding.UTF8.GetString(packets[0]).Should().Be("LIGHT", "先执行第一个成员");
        Encoding.UTF8.GetString(packets[1]).Should().Be("AUDIO", "再执行第二个成员");

        // 延迟是"真的等"而不是"写进结果里给人看"。
        // ⚠️ 阈值取 100ms（配置 150ms）：CI 与本地都不该因为几十毫秒的调度抖动而红。
        elapsed.TotalMilliseconds.Should().BeGreaterThan(
            100, "段间延迟必须真的等 —— 只把它记进结果而不等待，现场会看到两条命令几乎同时发出");
    }

    /// <summary>
    /// 成员指向的按钮已被删除时：组合**照样能跑**，跑的是剩下的那些成员。
    /// <para>
    /// ⚠️ 这里断言 `SkippedButtonIds` <b>为空</b>，因为悬空成员在**配置载入时**就被清洗器
    /// （<c>ControlSettingsNormalizer.NormalizeComboButton</c>）剔掉了 ——
    /// 也就是说"组合不会带着悬空成员跑到执行期"这条保证落在**清洗**那一步。
    /// 执行期仍会再算一次（`ComboResolver`，防"匹配时还在、执行时被删"），
    /// 那条路由纯逻辑用例 `Resolver_keeps_order_and_reports_what_it_skipped` 覆盖。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Combo_with_a_dangling_member_still_runs_the_rest()
    {
        using var h = new Harness();
        using var sink = new UdpSink();

        var alive = new ButtonConfig { Label = "音响", Actions = { Udp("开音响", sink.Port, "AUDIO") } };

        h.SeedConfig(new ControlSettings
        {
            Buttons = { alive },
            ComboButtons =
            {
                new ComboButton
                {
                    Label = "开场",
                    PairingCode = "OPEN",
                    Steps = new List<ComboButtonStep>
                    {
                        new() { ButtonId = "已经删掉的按钮", DelayMs = 0 },
                        new() { ButtonId = alive.Id, DelayMs = 0 },
                    },
                },
            },
        });

        var outcome = await h.Dispatcher.DispatchAsync(Datagram("OPEN"));
        var packets = await sink.CollectAsync(1, TimeSpan.FromSeconds(2));

        outcome.Kind.Should().Be(PairingMatchKind.Combo);
        outcome.ComboExecution!.Segments.Should().ContainSingle("只剩一个真实成员");
        outcome.ComboExecution.SkippedButtonIds.Should().BeEmpty(
            "悬空成员在**配置载入时**就被清洗器剔掉了 —— 它根本走不到执行期");
        outcome.ComboExecution.Ok.Should().BeTrue();

        packets.Should().ContainSingle();
        Encoding.UTF8.GetString(packets[0]).Should().Be("AUDIO");
    }

    /// <summary>
    /// 对接码同时配在主按钮与组合按钮上时：<b>按全局顺序（order）先出现的胜出</b>。
    /// <para>
    /// 这条守卫的是"两者同级"这个语义 —— 若实现改回"先扫所有按钮、再扫组合"，
    /// 组合哪怕排在按钮前面也会被抢走，用户看到的就是"我把组合拖到前面了却没用"。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_combo_earlier_in_order_wins_the_code_over_a_button()
    {
        using var h = new Harness();
        using var sink = new UdpSink();

        var button = new ButtonConfig
        {
            Label = "按钮",
            Order = 5,
            PairingCode = "SAME",
            Actions = { Udp("按钮动作", sink.Port, "FROM-BUTTON") },
        };

        var member = new ButtonConfig { Label = "成员", Order = 6, Actions = { Udp("成员动作", sink.Port, "FROM-COMBO") } };

        h.SeedConfig(new ControlSettings
        {
            Buttons = { member, button },
            ComboButtons =
            {
                new ComboButton
                {
                    Label = "组合",
                    Order = 1, // ← 排在按钮前面
                    PairingCode = "SAME",
                    Steps = new List<ComboButtonStep> { new() { ButtonId = member.Id, DelayMs = 0 } },
                },
            },
        });

        var outcome = await h.Dispatcher.DispatchAsync(Datagram("SAME"));
        var packets = await sink.CollectAsync(1, TimeSpan.FromSeconds(2));

        outcome.Kind.Should().Be(PairingMatchKind.Combo, "order 更小的组合先命中");

        packets.Should().ContainSingle();
        Encoding.UTF8.GetString(packets[0]).Should().Be("FROM-COMBO");
    }

    private static PairingDatagram Datagram(string text, byte[]? payload = null)
    {
        var bytes = payload ?? Encoding.UTF8.GetBytes(text);

        return new PairingDatagram(
            BeijingTime.Now, "192.168.1.50", 40000,
            text, bytes, Convert.ToHexString(bytes), bytes.Length);
    }

    private static ButtonAction Udp(
        string name,
        int port,
        string content,
        string? pairingCode = null,
        string dataType = DataTypes.Utf8,
        string ip = "127.0.0.1")
        => new()
        {
            Name = name,
            Protocol = TransportProtocols.Udp,
            TargetIp = ip,
            TargetPort = port,
            DataType = dataType,
            DataContent = content,
            PairingCode = pairingCode ?? string.Empty,
        };

    private sealed class Harness : IDisposable
    {
        private readonly TempDataDirectory _dataDirectory = new();
        private readonly InMemoryConfigStore _store = new();

        public Harness()
        {
            Log = new LogService(_dataDirectory, capacity: 200, flushIntervalMs: 60_000);
            Traffic = new RecordingTrafficLog();
            Settings = new ControlSettingsService(_store, Log);

            var executor = new ActionExecutionService(
                new NetworkService(), Traffic, new FakeCommandLibrary(), new NullNotifier(), Log);

            Dispatcher = new PairingDispatchService(Settings, executor, Traffic, Log);
        }

        public LogService Log { get; }

        public RecordingTrafficLog Traffic { get; }

        public ControlSettingsService Settings { get; }

        public PairingDispatchService Dispatcher { get; }

        /// <summary>直接写入一份配置文档（模拟"编辑页已保存"）。</summary>
        public void SeedConfig(ControlSettings settings)
            => _store.Seed(ConfigKeys.Settings, ControlJson.Serialize(settings), version: _store.NextVersion);

        public void Dispose()
        {
            Log.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _dataDirectory.Dispose();
        }
    }

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
