using System.Net;
using System.Net.Http.Json;
using ControlServer.Contracts;
using ControlServer.Server.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 「配置版本 / 已应用版本」这两个数字在 API 上的可见性（M4.5）。
///
/// <para>
/// 这块值得单独一个文件，因为它守的是<b>一个已经真实发生过的缺陷</b>：
/// <c>AgentHostDto</c> 上早就有了 <c>AppliedConfigVersion</c>，但
/// <c>AgentHostRegistry.ToDto</c> 从来没把它填进去（三个调用点都只补了 <c>ConfigVersion</c>）——
/// 于是这个字段在<b>所有</b>响应里恒为 <c>null</c>，
/// 而症状是页面永远显示"未回报"：<b>不报错、不崩、就是一直不对</b>。
/// 这类缺陷只有"端到端读一次那个字段"才抓得到（单元测试里各段各自都对）。
/// </para>
///
/// <para>
/// 另一条纪律同样在这里钉住：<b><c>null</c> 不等于 0</b>。
/// "从没回报过"（老客户端 / 刚注册）与"已应用 v0"是两件完全不同的事，
/// 前端据此走的是两条不同的展示分支。
/// </para>
/// </summary>
public sealed class AgentConfigVersionTests : IDisposable
{
    private readonly ControlServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("admin", "admin123"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "登录必须成功，否则后面的断言全无意义");
        return client;
    }

    [Fact]
    public async Task Both_versions_are_null_or_zero_before_anything_happens()
    {
        using var admin = await LoginAsync();
        var id = await RegisterOneAsync();

        var detail = await admin.GetFromJsonAsync<AgentDetailDto>($"/api/agents/{id}");
        var listed = await GetListedAsync(admin, id);

        detail!.Host.ConfigVersion.Should().Be(0, "还没保存过配置 ⇒ 版本 0（0 = 该机还没有配置）");

        // 🔥 这里是 null 而不是 0：客户机一次都没回报过，不能拿 0 冒充"已应用 v0"。
        detail.Host.AppliedConfigVersion.Should().BeNull(
            "从没回报过就是 null —— 用 0 冒充会让人以为它已经应用过一个版本");

        listed.ConfigVersion.Should().Be(0);
        listed.AppliedConfigVersion.Should().BeNull(
            "列表接口与详情接口必须给同一个结论（它们走的是同一个 ToDto）");
    }

    [Fact]
    public async Task Applied_version_becomes_visible_in_both_endpoints_after_the_client_reports_it()
    {
        using var admin = await LoginAsync();
        var id = await RegisterOneAsync();

        var version = await SaveConfigAsync(admin, id);

        version.Should().Be(1, "第一次保存 ⇒ 版本从 1 开始");

        // 保存之后、客户机回报之前：已下发 v1，但已应用还是 null（它还没说话）。
        (await GetListedAsync(admin, id)).AppliedConfigVersion.Should().BeNull();

        await ReportAppliedAsync(id, version);

        var detail = await admin.GetFromJsonAsync<AgentDetailDto>($"/api/agents/{id}");
        var listed = await GetListedAsync(admin, id);

        // 🔥 这一条就是那个缺陷的回归守卫：以前这里恒为 null。
        detail!.Host.AppliedConfigVersion.Should().Be(
            version, "客户机回报了就应该能在详情里看到（这正是 ToDto 漏填的那个字段）");

        listed.AppliedConfigVersion.Should().Be(
            version, "列表与详情都得带上它 —— 漏掉任何一处都只会表现为页面永远显示'未回报'");
    }

    [Fact]
    public async Task A_late_report_never_pulls_the_version_backwards()
    {
        using var admin = await LoginAsync();
        var id = await RegisterOneAsync();

        var v1 = await SaveConfigAsync(admin, id);
        var v2 = await SaveConfigAsync(admin, id);

        v2.Should().Be(v1 + 1, "每次保存都单调 +1");

        // 先到的是新回报，后到的是迟到的旧回报（SignalR / HTTP 都不保证顺序）。
        await ReportAppliedAsync(id, v2);
        await ReportAppliedAsync(id, v1);

        (await GetListedAsync(admin, id)).AppliedConfigVersion.Should().Be(
            v2, "迟到的旧回报不能把页面上的版本退回去 —— 只增不减");
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    /// <summary>列表里那一台（顺序由服务端按状态与显示名排，所以按 Id 找）。</summary>
    private static async Task<AgentHostDto> GetListedAsync(HttpClient admin, int id)
    {
        var list = await admin.GetFromJsonAsync<AgentListDto>("/api/agents");

        return list!.Items.Single(x => x.Id == id);
    }

    /// <summary>保存一次（空补丁でも版本会 +1），返回新版本号。</summary>
    private static async Task<int> SaveConfigAsync(HttpClient admin, int id)
    {
        var response = await admin.PutAsJsonAsync(
            $"/api/agents/{id}/config", new AgentConfigSaveRequest());

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var saved = await response.Content.ReadFromJsonAsync<AgentConfigSaveResult>();

        saved!.Ok.Should().BeTrue();
        return saved.Config!.Version;
    }

    /// <summary>
    /// 走<b>真正的服务</b>回报"配置已应用"（而不是直接改库）：
    /// 只有走服务才会同时更新 SQL 与内存快照，而列表接口读的正是内存快照 ——
    /// 直接改库的话这条测试会漏掉"快照没跟着更新"这一类问题。
    /// </summary>
    private async Task ReportAppliedAsync(int id, int version)
    {
        var configs = _factory.Services.GetRequiredService<AgentConfigService>();

        await configs.AcceptAppliedAsync(id, new AgentConfigAppliedDto(version, true));
    }

    /// <summary>免凭据注册一台机器（2026-10-13 起不需要令牌 / 加入码）。</summary>
    private async Task<int> RegisterOneAsync()
    {
        var anonymous = _factory.CreateClient();

        var registered = await anonymous.PostAsJsonAsync(
            "/api/agents/register",
            new AgentRegisterRequest(
                new AgentHostInfoDto(
                    "config-version-under-test",
                    "SMOKE-PC",
                    "Windows 11 (integration test)",
                    "Test CPU @ 1.00GHz",
                    4,
                    8192,
                    "0.1.0",
                    "tester",
                    new[] { new AgentNetworkInterfaceDto("以太网", "192.168.1.10", "AA:BB:CC:DD:EE:02", true) })));

        registered.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await registered.Content.ReadFromJsonAsync<AgentRegisterResult>();

        return result!.AgentId!.Value;
    }
}
