using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 会改动进程级 <see cref="BeijingTime.ClockOffset"/> 的用例所属的 collection 名。
/// 单独放一个常量类，避免在 <see cref="CollectionDefinitionAttribute"/> 里自引用。
/// </summary>
internal static class ClockCollections
{
    public const string BeijingTime = "beijing-time-clock";
}

/// <summary>
/// 关闭该 collection 与其他 collection 的并行 —— 全局静态状态 + 并行执行 = 不确定的坑。
/// </summary>
[CollectionDefinition(ClockCollections.BeijingTime, DisableParallelization = true)]
public sealed class BeijingTimeClockCollection
{
}

/// <summary>
/// <see cref="TimeSyncService"/> 的行为测试。
/// <para>
/// 用假的 <see cref="ISntpClient"/> 而非真 UDP：时钟校正一旦依赖真实网络就会变成不稳定测试，
/// 而真正的协议收发已经由 <c>SntpClientTests</c> 用本机假服务器覆盖。
/// </para>
/// </summary>
[Collection(ClockCollections.BeijingTime)]
public sealed class TimeSyncServiceTests : IAsyncLifetime
{
    private LogService _log = null!;

    public Task InitializeAsync()
    {
        // 前后都复位：任何一个用例漏了复位也不会污染其他用例。
        BeijingTime.ResetOffset();
        _log = new LogService(new NoopDataDirectory());
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        BeijingTime.ResetOffset();
        await _log.DisposeAsync();
    }

    /// <summary>
    /// 单服务器（多数用例只关心一个源）。
    /// <para>
    /// ⚠️ 刻意**不用 <c>params</c> + "传空就补默认"** 的写法：那样
    /// <c>NewService(c, servers: Array.Empty&lt;string&gt;())</c> 会被悄悄补成 <c>["a.ntp"]</c>，
    /// "未配置任何服务器"这条用例就测不到真实路径了（第一版就栽在这里）。
    /// 必须让"显式空"与"未传"可区分。
    /// </para>
    /// </summary>
    private TimeSyncService NewService(ISntpClient client, string servers = "a.ntp")
        => NewService(client, new[] { servers });

    private TimeSyncService NewService(ISntpClient client, string[] servers)
        => new(
            client,
            new TimeSyncSettings(servers, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3)),
            _log);

    // ═══════════════════════════════════════════════════════════
    // 成功路径
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_applies_the_offset_from_the_first_reachable_server()
    {
        var client = new FakeSntpClient().Returns("a.ntp", TimeSpan.FromMilliseconds(128));
        var service = NewService(client);

        service.IsSynchronized.Should().BeFalse("还没校准过");

        var outcome = await service.SyncAsync();

        outcome.Ok.Should().BeTrue();
        outcome.Source.Should().Be("a.ntp");
        outcome.Offset.Should().Be(TimeSpan.FromMilliseconds(128));

        service.IsSynchronized.Should().BeTrue();
        service.LastSource.Should().Be("a.ntp");
        service.LastError.Should().BeNull();
        client.Queried.Should().Equal(new[] { "a.ntp" }, "第一个成功就不该再问后面的服务器");
    }

    [Fact]
    public async Task Sync_makes_BeijingTime_follow_the_corrected_clock()
    {
        var offset = TimeSpan.FromSeconds(4);
        var service = NewService(new FakeSntpClient().Returns("a.ntp", offset), servers: "a.ntp");

        BeijingTime.IsCorrected.Should().BeFalse();
        BeijingTime.Now.Should().BeCloseTo(BeijingTime.NowRaw, TimeSpan.FromMilliseconds(50),
            "未校准时 Now 与 NowRaw 必须一致 —— 这是「引入校正不改变既有行为」的保证");

        await service.SyncAsync();

        BeijingTime.IsCorrected.Should().BeTrue();
        (BeijingTime.Now - DateTimeOffset.UtcNow).Should().BeCloseTo(offset, TimeSpan.FromMilliseconds(50));
        BeijingTime.Now.Offset.Should().Be(TimeSpan.FromHours(8), "校正量不影响 UTC+8 这个偏移属性");

        BeijingTime.NowRaw.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(50),
            "NowRaw 永远不吃校正");
    }

    // ═══════════════════════════════════════════════════════════
    // 失败与回退
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_falls_back_to_the_next_server_when_the_first_fails()
    {
        var client = new FakeSntpClient()
            .Fails("a.ntp", "超时")
            .Returns("b.ntp", TimeSpan.FromMilliseconds(60));

        var service = NewService(client, servers: new[] { "a.ntp", "b.ntp" });

        var outcome = await service.SyncAsync();

        outcome.Ok.Should().BeTrue();
        outcome.Source.Should().Be("b.ntp");
        client.Queried.Should().Equal(new[] { "a.ntp", "b.ntp" });
        outcome.Attempts.Should().HaveCount(2).And.Contain(a => a.StartsWith("a.ntp") && a.Contains("超时"));
    }

    /// <summary>
    /// 这条是**关键行为**：全部服务器失败时绝不能把偏移清 0 ——
    /// 那会让时间倒退一次，比"用着略旧的校正值"糟得多。
    /// </summary>
    [Fact]
    public async Task Sync_keeps_the_previous_offset_when_every_server_fails()
    {
        var client = new FakeSntpClient()
            .Returns("a.ntp", TimeSpan.FromSeconds(2))
            .Fails("b.ntp", "超时");

        var good = NewService(client, servers: "a.ntp");
        await good.SyncAsync();
        BeijingTime.ClockOffset.Should().Be(TimeSpan.FromSeconds(2));

        var failing = NewService(client, servers: "b.ntp");
        var outcome = await failing.SyncAsync();

        outcome.Ok.Should().BeFalse();
        outcome.Offset.Should().Be(TimeSpan.FromSeconds(2), "失败时沿用既有偏移");
        BeijingTime.ClockOffset.Should().Be(TimeSpan.FromSeconds(2), "绝不能因为查不到就清零");

        failing.LastError.Should().NotBeNull();
        failing.FailureCount.Should().Be(1);
    }

    [Fact]
    public async Task Sync_skips_blank_server_entries()
    {
        var client = new FakeSntpClient().Returns("b.ntp", TimeSpan.FromMilliseconds(10));
        var service = NewService(client, servers: new[] { "", "  ", "b.ntp" });

        var outcome = await service.SyncAsync();

        outcome.Ok.Should().BeTrue();
        client.Queried.Should().Equal(new[] { "b.ntp" }, "空白条目必须先被跳过，且不计入尝试记录");
    }

    [Fact]
    public async Task Sync_reports_a_failure_when_no_server_is_configured()
    {
        var service = NewService(new FakeSntpClient(), servers: Array.Empty<string>());

        var outcome = await service.SyncAsync();

        outcome.Ok.Should().BeFalse();
        outcome.Error.Should().Contain("未配置");
        outcome.Attempts.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════
    // 偏移跳变
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_reports_the_jump_relative_to_the_previous_offset()
    {
        var client = new FakeSntpClient()
            .Returns("a.ntp", TimeSpan.FromMilliseconds(100));

        var service = NewService(client, servers: "a.ntp");

        var first = await service.SyncAsync();
        first.OffsetJump.Should().Be(TimeSpan.FromMilliseconds(100), "首次校准的'上次偏移'是 0");

        client.Returns("a.ntp", TimeSpan.FromMilliseconds(450));
        var second = await service.SyncAsync();

        second.OffsetJump.Should().Be(TimeSpan.FromMilliseconds(350));
        second.Offset.Should().Be(TimeSpan.FromMilliseconds(450));
    }

    [Fact]
    public async Task Sync_counts_attempts_and_failures()
    {
        var client = new FakeSntpClient().Fails("a.ntp", "超时");
        var service = NewService(client, servers: "a.ntp");

        await service.SyncAsync();
        await service.SyncAsync();

        service.AttemptCount.Should().Be(2);
        service.FailureCount.Should().Be(2);

        client.Returns("a.ntp", TimeSpan.FromMilliseconds(1));
        var ok = await service.SyncAsync();

        ok.Ok.Should().BeTrue();
        service.AttemptCount.Should().Be(3);
        service.FailureCount.Should().Be(2, "成功不清空历史失败计数");
        service.LastError.Should().BeNull("成功后应清掉上次的错误");
    }

    [Fact]
    public void FormatOffset_renders_sign_and_seconds()
    {
        TimeSyncService.FormatOffset(TimeSpan.FromMilliseconds(128)).Should().Be("+0.128s");
        TimeSyncService.FormatOffset(TimeSpan.FromMilliseconds(-1500)).Should().Be("-1.5s");
        TimeSyncService.FormatOffset(TimeSpan.Zero).Should().Be("+0s");
    }

    // ═══════════════════════════════════════════════════════════
    // 替身
    // ═══════════════════════════════════════════════════════════

    private sealed class FakeSntpClient : ISntpClient
    {
        private readonly Dictionary<string, Func<SntpQueryResult>> _servers = new(StringComparer.Ordinal);

        public List<string> Queried { get; } = new();

        public FakeSntpClient Returns(string host, TimeSpan offset)
        {
            _servers[host] = () => new SntpQueryResult(
                offset,
                TimeSpan.FromMilliseconds(5),
                2,
                DateTimeOffset.UtcNow.Add(offset));
            return this;
        }

        public FakeSntpClient Fails(string host, string message)
        {
            _servers[host] = () => throw new TimeoutException(message);
            return this;
        }

        public Task<SntpQueryResult> QueryAsync(
            string host,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Queried.Add(host);

            return _servers.TryGetValue(host, out var factory)
                ? Task.FromResult(factory())
                : Task.FromException<SntpQueryResult>(new TimeoutException($"「{host}」未在替身里配置"));
        }
    }
}
