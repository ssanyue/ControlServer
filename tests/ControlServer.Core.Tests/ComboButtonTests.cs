using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 组合按钮（2026-10-15，需求⑤）：清洗、解析、执行三条纯逻辑链。
///
/// <para>
/// 对接码那条端到端链在 <see cref="PairingDispatchServiceTests"/> 里（真 UDP 回环）；
/// 这里专测"没有 IO 也能判定的部分"，让失败点一眼能指到具体规则。
/// </para>
/// </summary>
public sealed class ComboButtonTests
{
    // ═══════════════════════════════════════════════════════════
    // 清洗
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Normalizer_drops_members_whose_button_is_gone_and_says_so()
    {
        var alive = new ButtonConfig { Label = "在的" };

        var settings = new ControlSettings
        {
            Buttons = { alive },
            ComboButtons =
            {
                new ComboButton
                {
                    Label = "开场",
                    Steps = new List<ComboButtonStep>
                    {
                        new() { ButtonId = "已删除", DelayMs = 300 },
                        new() { ButtonId = alive.Id, DelayMs = 0 },
                        new() { ButtonId = "  ", DelayMs = 0 },
                    },
                },
            },
        };

        var notes = ControlSettingsNormalizer.Normalize(settings);

        var combo = settings.ComboButtons.Single();
        combo.Steps.Should().ContainSingle("只有指向现存按钮的那一步留着");
        combo.Steps[0].ButtonId.Should().Be(alive.Id);

        notes.Should().ContainMatch(
            "*2 个成员指向的主按钮已不存在*",
            "剔除必须**说出来** —— 静默少一步在现场表现为「组合少执行了一个动作」，没人查得出来");
    }

    [Fact]
    public void Normalizer_zeroes_the_last_step_delay_and_clamps_the_rest()
    {
        var a = new ButtonConfig { Label = "A" };
        var b = new ButtonConfig { Label = "B" };
        var c = new ButtonConfig { Label = "C" };

        var settings = new ControlSettings
        {
            Buttons = { a, b, c },
            ComboButtons =
            {
                new ComboButton
                {
                    Label = "开场",
                    Steps = new List<ComboButtonStep>
                    {
                        new() { ButtonId = a.Id, DelayMs = -50 },
                        new() { ButtonId = b.Id, DelayMs = 999_999 },
                        new() { ButtonId = c.Id, DelayMs = 1234 },
                    },
                },
            },
        };

        var notes = ControlSettingsNormalizer.Normalize(settings);

        var steps = settings.ComboButtons.Single().Steps;
        steps[0].DelayMs.Should().Be(0, "负数按 0 处理");
        steps[1].DelayMs.Should().Be(ComboButtonStep.MaxDelayMs, "超过上限截到上限");
        steps[2].DelayMs.Should().Be(0, "**最后一步的延迟永远不会生效 ⇒ 必须归零**，否则界面上会留一个骗人的数字");

        notes.Should().ContainMatch("*负数*");
        notes.Should().ContainMatch("*超出上限*");
    }

    [Fact]
    public void Normalizer_keeps_a_button_referenced_twice()
    {
        var a = new ButtonConfig { Label = "A" };

        var settings = new ControlSettings
        {
            Buttons = { a },
            ComboButtons =
            {
                new ComboButton
                {
                    Label = "连按两次",
                    Steps = new List<ComboButtonStep>
                    {
                        new() { ButtonId = a.Id },
                        new() { ButtonId = a.Id },
                    },
                },
            },
        };

        ControlSettingsNormalizer.Normalize(settings);

        settings.ComboButtons.Single().Steps.Should().HaveCount(
            2, "「同一个按钮按两次」是有意义的行为（连点），不该被当成重复项去掉");
    }

    [Fact]
    public void Three_kinds_of_tiles_share_one_order_space()
    {
        var settings = new ControlSettings
        {
            Buttons =
            {
                new ButtonConfig { Label = "按钮2", Order = 2 },
                new ButtonConfig { Label = "按钮0", Order = 0 },
            },
            ComboButtons =
            {
                new ComboButton { Label = "组合0", Order = 0 },
                new ComboButton { Label = "组合1", Order = 1 },
            },
            SubButtonSets =
            {
                new SubButtonSet { Label = "集0", Order = 0 },
            },
        };

        ControlSettingsNormalizer.SortAndReindex(settings);

        // 🔥 合并排序后的**唯一序号**：order=0 那一档按「按钮 → 组合 → 集」排，
        //    然后才是 order=1 的组合、order=2 的按钮。
        //    这条断言是整个"三类瓦片同级"语义的落点 ——
        //    它与前端 `TILE_KIND_RANK` 必须逐一对应，否则界面顺序与服务端保存后的顺序会对不上。
        var merged = settings.Buttons.Select(b => (b.Order, b.Label))
            .Concat(settings.ComboButtons.Select(c => (c.Order, c.Label)))
            .Concat(settings.SubButtonSets.Select(x => (x.Order, x.Label)))
            .OrderBy(x => x.Order)
            .Select(x => x.Label)
            .ToArray();

        merged.Should().Equal(new[] { "按钮0", "组合0", "集0", "组合1", "按钮2" });

        // 三类共用同一条编号空间 ⇒ 序号互不重复，且是连续的 0..N-1。
        var orders = settings.Buttons.Select(b => b.Order)
            .Concat(settings.ComboButtons.Select(c => c.Order))
            .Concat(settings.SubButtonSets.Select(x => x.Order))
            .OrderBy(x => x)
            .ToArray();

        orders.Should().Equal(new[] { 0, 1, 2, 3, 4 }, "三者共用同一条编号空间，重编号后连续且不重复");
    }

    // ═══════════════════════════════════════════════════════════
    // 解析
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Resolver_keeps_order_and_reports_what_it_skipped()
    {
        var a = new ButtonConfig { Label = "A" };
        var b = new ButtonConfig { Label = "B" };

        var combo = new ComboButton
        {
            Label = "开场",
            Steps = new List<ComboButtonStep>
            {
                new() { ButtonId = b.Id, DelayMs = 200 },
                new() { ButtonId = "没了", DelayMs = 0 },
                new() { ButtonId = a.Id, DelayMs = 700 },
            },
        };

        var plan = ComboResolver.Resolve(combo, new List<ButtonConfig> { a, b });

        plan.ComboLabel.Should().Be("开场");
        plan.Segments.Select(s => s.Button.Label).Should().Equal(new[] { "B", "A" }, "顺序=配置顺序");
        plan.SkippedButtonIds.Should().Equal(new[] { "没了" });

        plan.Segments[0].DelayAfterMs.Should().Be(200);
        plan.Segments[1].DelayAfterMs.Should().Be(0, "最后一段的延迟不生效");
        plan.TotalDelayMs.Should().Be(200, "合计只算真正会等待的那一段");
    }

    [Fact]
    public void Resolver_tolerates_a_missing_button_list()
    {
        var combo = new ComboButton
        {
            Label = "开场",
            Steps = new List<ComboButtonStep> { new() { ButtonId = "任意" } },
        };

        var plan = ComboResolver.Resolve(combo, null);

        plan.IsExecutable.Should().BeFalse();
        plan.SkippedButtonIds.Should().Equal(new[] { "任意" });
    }

    // ═══════════════════════════════════════════════════════════
    // 执行
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_combo_records_a_member_without_actions_as_skipped_and_continues()
    {
        using var dir = new TempDataDirectory();

        // ⚠️ `LogService` 是 **IAsyncDisposable**（不是 IDisposable）⇒ 必须 `await using`。
        await using var log = new LogService(dir, capacity: 100, flushIntervalMs: 60_000);
        using var sink = new UdpSink();

        var executor = new ActionExecutionService(
            new NetworkService(), new RecordingTrafficLog(), new FakeCommandLibrary(), new NullNotifier(), log);

        var silent = new ButtonConfig { Label = "空按钮", Actions = new List<ButtonAction>() };
        var loud = new ButtonConfig { Label = "有声", Actions = { Udp("发一条", sink.Port, "HELLO") } };

        var plan = new ComboPlan(
            "开场",
            new[]
            {
                new ComboSegment(silent, 0),
                new ComboSegment(loud, 0),
            },
            Array.Empty<string>());

        var result = await executor.ExecuteComboAsync(plan);
        var packets = await sink.CollectAsync(1, TimeSpan.FromSeconds(2));

        // ⚠️ "没有动作的成员"**也要占一段**：否则"3 个成员只回 2 段"没法解释。
        result.Segments.Should().HaveCount(2);
        result.Segments[0].Skipped.Should().Be("这个按钮里没有动作");
        result.Segments[0].Result.Should().BeNull();
        result.Segments[1].Result.Should().NotBeNull();

        result.Ok.Should().BeTrue("空成员不算失败");
        result.TotalSteps.Should().Be(1);
        result.Summary.Should().Contain("2 个成员");

        packets.Should().ContainSingle();
        Encoding.UTF8.GetString(packets[0]).Should().Be("HELLO");
    }

    [Fact]
    public async Task Execute_combo_keeps_going_after_a_failing_member()
    {
        using var dir = new TempDataDirectory();

        // ⚠️ `LogService` 是 **IAsyncDisposable**（不是 IDisposable）⇒ 必须 `await using`。
        await using var log = new LogService(dir, capacity: 100, flushIntervalMs: 60_000);
        using var sink = new UdpSink();

        var executor = new ActionExecutionService(
            new NetworkService(), new RecordingTrafficLog(), new FakeCommandLibrary(), new NullNotifier(), log);

        // 奇数长度的 Hex ⇒ 编码失败（沿用既有用例的构造方式），用来造一个"必失败的成员"。
        var broken = new ButtonConfig
        {
            Label = "坏按钮",
            Actions = { Udp("坏数据", sink.Port, "ABC", dataType: DataTypes.Hex) },
        };

        var good = new ButtonConfig { Label = "好按钮", Actions = { Udp("好数据", sink.Port, "OK") } };

        var plan = new ComboPlan(
            "开场",
            new[] { new ComboSegment(broken, 0), new ComboSegment(good, 0) },
            Array.Empty<string>());

        var result = await executor.ExecuteComboAsync(plan);
        var packets = await sink.CollectAsync(1, TimeSpan.FromSeconds(2));

        result.Segments.Should().HaveCount(2, "一个成员失败**不中断**后续成员 —— 与单按钮内「失败不中断」同一口径");
        result.FailureCount.Should().Be(1);
        result.SuccessCount.Should().Be(1);
        result.Ok.Should().BeFalse("有失败就不算整体成功");

        packets.Should().ContainSingle("后面的成员照样执行了");
        Encoding.UTF8.GetString(packets[0]).Should().Be("OK");
    }

    // ═══════════════════════════════════════════════════════════

    private static ButtonAction Udp(
        string name,
        int port,
        string content,
        string dataType = DataTypes.Utf8)
        => new()
        {
            Name = name,
            Protocol = TransportProtocols.Udp,
            TargetIp = "127.0.0.1",
            TargetPort = port,
            DataType = dataType,
            DataContent = content,
        };

    /// <summary>真回环 UDP 收集器（与其它用例同一套做法：不 mock socket）。</summary>
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
