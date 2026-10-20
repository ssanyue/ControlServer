using System.Net;
using System.Net.Http.Json;
using ControlServer.Contracts;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 客户机「接收到的数据」端点（<c>GET /api/agents/{id}/received</c>，2026-10-13）。
///
/// <para>
/// 这一组用例守的是**失败形态的可读性**，而不是"能不能拿到数据"（那需要一条真的 SignalR
/// 长连接，属于冒烟脚本的活）。理由很直接：这个端点存在的<b>唯一</b>场合就是
/// "出了事、要查"，那时它自己千万不能以"500 系统错误"的样子出现 ——
/// 否则运维会以为是服务端坏了，而不是"那台机器不在线"。
/// </para>
/// <list type="number">
///   <item>不存在的机器 ⇒ <b>404</b>（请求本身不对）；</item>
///   <item>存在但没连上 ⇒ <b>200 + <c>ok:false</c> + <c>offline</c> + 人话</b>
///         （预期内的业务结果，页面要就地显示而不是弹错）；</item>
///   <item>匿名 ⇒ <b>401</b>（它暴露的是别人的机器上谁在发指令）。</item>
/// </list>
/// </summary>
public sealed class AgentReceivedApiTests : IDisposable
{
    private readonly ControlServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin123"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "登录必须成功，否则后面的断言全无意义");
        return client;
    }

    private async Task<int> RegisterOneAsync(string machineKey)
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            "/api/agents/register",
            new AgentRegisterRequest(new AgentHostInfoDto(
                machineKey,
                "RECV-PC",
                "Windows 11 (integration test)",
                "Test CPU @ 1.00GHz",
                4,
                8192,
                "0.1.0",
                "tester",
                new[] { new AgentNetworkInterfaceDto("以太网", "192.168.1.20", "AA:BB:CC:DD:EE:02", true) })));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var result = (await response.Content.ReadFromJsonAsync<AgentRegisterResult>())!;

        return result.AgentId!.Value;
    }

    [Fact]
    public async Task An_unknown_agent_is_a_404()
    {
        var client = await LoginAsync();

        var response = await client.GetAsync("/api/agents/999999/received");

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "请求本身指向一台不存在的机器 ⇒ 这才是真正的 4xx");
    }

    [Fact]
    public async Task A_registered_but_disconnected_agent_reports_offline_in_band_not_as_an_error()
    {
        var client = await LoginAsync();
        var id = await RegisterOneAsync("recv-offline");

        var response = await client.GetAsync($"/api/agents/{id}/received");

        // 🔥 关键：**200，不是 5xx**。「这台机器不在线」是预期内的业务结果 ——
        //    它恰恰是最常见的形态（想看记录的机器十有八九就是那台出问题的，
        //    而它很可能刚断线）。用状态码表达它，页面就只能弹一个"系统出错了"。
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<AgentReceivedDataDto>();

        result.Should().NotBeNull();
        result!.Ok.Should().BeFalse();
        result.Status.Should().Be(AgentReceivedStatuses.Offline);
        result.Message.Should().NotBeNullOrWhiteSpace("一定要有人话 —— 页面上要原样显示它");
        result.Entries.Should().BeNull("没问到就说没问到，不要给一个空的列表冒充'它什么都没收到'");
    }

    [Fact]
    public async Task Anonymous_access_is_rejected()
    {
        var client = await LoginAsync();
        var id = await RegisterOneAsync("recv-anonymous");

        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync($"/api/agents/{id}/received");

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "它暴露的是客户机所在网段里谁在给它发指令，不该给匿名用户看");
    }
}
