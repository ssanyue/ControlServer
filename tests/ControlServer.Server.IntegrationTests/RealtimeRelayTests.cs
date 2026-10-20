using System.Net.Http.Json;
using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using ControlServer.Server.HostedServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 实时推送桥接（M2-F）：内核事件 → SignalR。
/// <para>
/// 前端「收发日志」面板与「配置变更自动重拉」都依赖这两条通道。桥接一旦没接上，
/// 界面表现为"点了按钮但日志不刷新"—— 这类**静默失效**最难被发现，
/// 因此这里用记录型 <see cref="INotifier"/> 直接验证事件真的被转发了。
/// </para>
/// </summary>
public sealed class RealtimeRelayTests : IDisposable
{
    private readonly ControlServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Relay_forwards_traffic_log_and_config_changed_events()
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin123")))
            .EnsureSuccessStatusCode();

        var notifier = new RecordingNotifier();

        // 用宿主里**同一批**服务实例接上桥接，验证真实组合根下的连线。
        var relay = new RealtimeRelayHostedService(
            _factory.Services.GetRequiredService<TrafficLogService>(),
            _factory.Services.GetRequiredService<ControlSettingsService>(),
            notifier);

        await relay.StartAsync(CancellationToken.None);

        try
        {
            // ① 执行一次发送 → 应推一条收发日志
            var exec = await client.PostAsJsonAsync("/api/actions/execute", new
            {
                label = "实时桥接测试",
                actions = new[]
                {
                    new { targetIp = "127.0.0.1", targetPort = 59997, protocol = "UDP", dataContent = "RELAY" },
                },
            });
            exec.EnsureSuccessStatusCode();

            notifier.TrafficLogs.Should().ContainSingle();
            notifier.TrafficLogs[0].Data.Should().Be("RELAY");
            notifier.TrafficLogs[0].ButtonName.Should().Be("实时桥接测试");

            // ② 改配置 → 应推一条配置变更通知（且只带版本号，不带配置本体）
            var import = await client.PostAsJsonAsync("/api/config/import", new
            {
                content = """{ "appName": "桥接", "buttons": [ { "id": "b1", "label": "甲" } ], "subButtonSets": [] }""",
                expectedVersion = 0,
            });
            import.EnsureSuccessStatusCode();

            notifier.ConfigChanges.Should().ContainSingle();
            notifier.ConfigChanges[0].Version.Should().Be(1);
            notifier.ConfigChanges[0].ChangedBy.Should().Be("admin");
        }
        finally
        {
            await relay.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Relay_stops_forwarding_after_hosted_service_stops()
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin123")))
            .EnsureSuccessStatusCode();

        var notifier = new RecordingNotifier();
        var relay = new RealtimeRelayHostedService(
            _factory.Services.GetRequiredService<TrafficLogService>(),
            _factory.Services.GetRequiredService<ControlSettingsService>(),
            notifier);

        await relay.StartAsync(CancellationToken.None);
        await relay.StopAsync(CancellationToken.None);

        await client.PostAsJsonAsync("/api/actions/execute", new
        {
            label = "停机后",
            actions = new[] { new { targetIp = "127.0.0.1", targetPort = 59996, protocol = "UDP", dataContent = "X" } },
        });

        notifier.TrafficLogs.Should().BeEmpty("停机后必须退订，否则会重复推送 / 持有已释放的对象");
    }

    /// <summary>记录型通知器：把收到的载荷存起来供断言。</summary>
    private sealed class RecordingNotifier : INotifier
    {
        public List<TrafficLogDto> TrafficLogs { get; } = new();

        public List<ConfigChangedDto> ConfigChanges { get; } = new();

        public Task LogAddedAsync(LogDto item, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task StatusChangedAsync(StatusSnapshot snapshot, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ActionResultAsync(ActionResultDto result, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task TrafficLogAddedAsync(TrafficLogDto item, CancellationToken cancellationToken = default)
        {
            TrafficLogs.Add(item);
            return Task.CompletedTask;
        }

        public Task ConfigChangedAsync(ConfigChangedDto change, CancellationToken cancellationToken = default)
        {
            ConfigChanges.Add(change);
            return Task.CompletedTask;
        }

        public Task LicenseChangedAsync(LicenseDto license, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        // ── M4.1：在线表通知。本测试不涉及客户端主机，记录即可 ──

        public Task AgentChangedAsync(AgentHostDto host, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AgentOnlineAsync(AgentHostDto host, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AgentOfflineAsync(AgentHostDto host, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AgentTelemetryUpdatedAsync(AgentLiveMetricsDto live, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        // ── M4.4：文件传输进度。本测试不涉及分发，记录即可 ──

        public Task TransferProgressAsync(TransferProgressDto progress, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        // ── M4.5：客户机配置变更通知。本测试不涉及配置，忽略即可 ──

        public Task AgentConfigChangedAsync(int agentId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
