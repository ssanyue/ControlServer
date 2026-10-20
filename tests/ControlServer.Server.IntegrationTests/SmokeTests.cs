using System.Net;
using System.Net.Http.Json;
using ControlServer.Contracts;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 端到端冒烟：匿名拒绝 → 登录 → 拿到状态 → 状态里的时间是北京时间。
/// 每个测试独占一个宿主（xunit 每个测试新建测试类实例）。
/// </summary>
public sealed class SmokeTests : IDisposable
{
    private readonly ControlServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Health_endpoint_reports_alive()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Protected_endpoint_returns_401_when_anonymous()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/status");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_with_default_admin_succeeds_and_unlocks_status()
    {
        var client = _factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin123"));
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var status = await client.GetAsync("/api/status");
        status.StatusCode.Should().Be(HttpStatusCode.OK);

        var snapshot = await status.Content.ReadFromJsonAsync<StatusSnapshot>();
        snapshot.Should().NotBeNull();
        snapshot!.ServerTime.Offset.Should().Be(TimeSpan.FromHours(8), "全应用时间戳必须统一为北京时间");
        snapshot.PairingPort.Should().Be(_factory.PairingPort, "服务实际绑定端口应等于配置端口");
        snapshot.ListenerRunning.Should().BeTrue("对接码监听应随宿主启动");
    }

    [Fact]
    public async Task Login_with_wrong_password_is_rejected()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "wrong-password"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
