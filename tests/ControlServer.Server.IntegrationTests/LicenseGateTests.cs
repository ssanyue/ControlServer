using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.Contracts;
using ControlServer.Core.Licensing;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using ControlServer.Server.Security;
using ControlServer.Server.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 授权闸门（M3 · S3）。
///
/// <para>
/// 这一层要守的是<b>两件都容易做错的事</b>：
/// <list type="number">
/// <item><description>
/// <b>该拦的拦住</b>：动作执行、手动触发、UDP 测试；以及<b>后台</b>的对接码监听
/// —— 后者不能只靠 HTTP 闸门，因为对接码是 UDP 直连，绕开 Web 照样能触发。
/// </description></item>
/// <item><description>
/// <b>不该拦的一个都别拦</b>：登录、只读、<b>导入授权</b>、用户管理、配置写入。
/// 尤其"导入授权"—— 它是现场唯一的自救通道，拦掉它就是死锁。
/// </description></item>
/// </list>
/// </para>
///
/// <para>
/// 另外还有一条 <b>fail-closed 守门用例</b>：反射枚举全部 <c>/api</c> 端点，
/// 任何一个没在 <see cref="LicenseGate"/> 的分类表里出现，这条用例就失败。
/// 这样"新增端点忘了归类"会在测试阶段暴露，而不是在生产表现为"新端点白送"或
/// "新端点被莫名拦掉"。
/// </para>
/// </summary>
public sealed class LicenseGateTests : IDisposable
{
    private readonly List<LicenseTestFactory> _factories = new();

    public void Dispose()
    {
        foreach (var factory in _factories) factory.Dispose();
    }

    // ═══════════════════════════════════════════════════════════
    // A. 分类表（纯静态，不需要宿主）
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("POST", "api/actions/execute")]
    [InlineData("POST", "api/timers/{refId}/fire")]
    [InlineData("POST", "api/test/udp")]
    [InlineData("POST", "api/test/loopback")]
    // M4.4：建分发/回传任务 = 让一批客户机去下载/上传，与"远程重启"同类 ⇒ 拦。
    [InlineData("POST", "api/distributions")]
    [InlineData("POST", "api/distributions/collect")]
    public void Value_endpoints_are_gated(string method, string template)
    {
        var httpMethod = new HttpMethod(method);

        // ⚠️ 必须先断言"确实归过类"再断言"结果是拦"：
        //    fail-closed 让"没归类"也返回 Gated，只看结果的话这条用例会被自己骗过去
        //    （`{refId}` 大小写不匹配时就是这么蒙混过关的，由反射守门用例才揪出来）。
        LicenseGate.IsClassified(httpMethod, template)
            .Should().BeTrue($"{method} {template} 必须在分类表里显式列出");

        LicenseGate.Classify(httpMethod, template)
            .Should().Be(LicenseGateAccess.Gated);
    }

    [Theory]
    [InlineData("POST", "api/auth/login")]
    [InlineData("POST", "api/auth/change-password")]
    [InlineData("GET", "api/auth/me")]
    [InlineData("GET", "api/license")]
    [InlineData("POST", "api/license/import")]
    [InlineData("POST", "api/actions/preview")]
    [InlineData("GET", "api/timers")]
    [InlineData("GET", "api/status")]
    [InlineData("GET", "api/users")]
    [InlineData("PUT", "api/config")]
    // 参数名带大写的那几个：RoutePattern.RawText 保留大小写，一旦归一化漏了就会误伤。
    [InlineData("GET", "api/config/images/{fileName}")]
    [InlineData("DELETE", "api/config/images/{fileName}")]
    [InlineData("PUT", "api/users/{id:int}")]
    // M4.4：仓库的字节读写全在服务端本地完成，不出这台机器 ⇒ 放行。
    // 客户机那三个端点只有任务真的建起来（已 Gated）才有流量 ⇒ 放行不会绕开闸门。
    [InlineData("GET", "api/files")]
    [InlineData("POST", "api/files/uploads")]
    [InlineData("GET", "api/files/uploads/{uploadId}")]
    [InlineData("PUT", "api/files/uploads/{uploadId}")]
    [InlineData("POST", "api/files/uploads/{uploadId}/complete")]
    [InlineData("DELETE", "api/files/uploads/{uploadId}")]
    [InlineData("GET", "api/files/{id:int}/download")]
    [InlineData("DELETE", "api/files/{id:int}")]
    [InlineData("GET", "api/distributions")]
    [InlineData("GET", "api/distributions/{id:int}")]
    [InlineData("GET", "api/agents/transfers")]
    [InlineData("POST", "api/agents/transfers/{itemId:int}/progress")]
    [InlineData("POST", "api/agents/transfers/{itemId:int}/complete")]
    public void Recovery_and_read_only_endpoints_are_allowed(string method, string template)
    {
        var httpMethod = new HttpMethod(method);

        LicenseGate.IsClassified(httpMethod, template)
            .Should().BeTrue($"{method} {template} 必须在分类表里显式列出");

        LicenseGate.Classify(httpMethod, template)
            .Should().Be(
                LicenseGateAccess.Allowed,
                "闸门只卡价值点 —— 一律 403 会让现场连授权都导不进去");
    }

    [Fact]
    public void Unclassified_route_is_gated_by_default()
    {
        var brandNew = new HttpMethod("POST");

        LicenseGate.IsClassified(brandNew, "api/whatever/brand-new").Should().BeFalse();
        LicenseGate.Classify(brandNew, "api/whatever/brand-new")
            .Should().Be(LicenseGateAccess.Gated, "fail-closed：没归类就按拦处理");
    }

    // ═══════════════════════════════════════════════════════════
    // B. 反射守门：每个 /api 端点都必须显式归类
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Every_api_endpoint_is_explicitly_classified()
    {
        var factory = NewFactory();
        using var _ = factory.CreateClient();

        var sources = factory.Services.GetServices<EndpointDataSource>().ToList();
        sources.Should().NotBeEmpty(
            "宿主应当暴露端点数据源 —— 否则这条守门用例会静默变成空断言，比不做还糟");

        var unclassified = new SortedSet<string>(StringComparer.Ordinal);
        var inspected = 0;

        foreach (var endpoint in sources.SelectMany(s => s.Endpoints))
        {
            if (endpoint is not RouteEndpoint route) continue;

            var template = route.RoutePattern.RawText;
            if (string.IsNullOrWhiteSpace(template)) continue;
            if (!template.StartsWith("api/", StringComparison.OrdinalIgnoreCase)) continue;

            inspected++;

            var declared = route.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            var methods = declared is null || declared.Count == 0
                ? new HttpMethod?[] { null }
                : declared.Select(m => new HttpMethod(m)).ToArray();

            foreach (var method in methods)
            {
                if (!LicenseGate.IsClassified(method, template))
                {
                    unclassified.Add($"{method?.Method ?? "*"} {template}");
                }
            }
        }

        // 数量下界：当前有 40+ 个 /api 端点，若数据源枚举方式变了（比如只拿到一个子集），
        // 这条会先失败，而不是让"全部已归类"变成一句空话。
        inspected.Should().BeGreaterThan(20, "应当枚举到全部 /api 端点");

        unclassified.Should().BeEmpty(
            "新增 /api 端点必须在 Security/LicenseGate.cs 的 BuildRoutes 里显式归类"
            + "（放行或拦截），否则 fail-closed 会把它按不可用处理");
    }

    // ═══════════════════════════════════════════════════════════
    // C. HTTP 矩阵
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Enforce_without_a_license_returns_403_with_a_machine_readable_code()
    {
        var factory = NewFactory(enforcement: Enforce);
        var client = await LoginAsync(factory);

        var response = await client.PostAsJsonAsync("/api/actions/execute", Ping());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be(LicenseGate.DeniedCode);
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Enforce_without_a_license_leaves_the_recovery_channels_open()
    {
        var factory = NewFactory(enforcement: Enforce);

        // 登录本身就必须能过 —— 否则连"进到界面导授权"都做不到。
        var client = await LoginAsync(factory);

        (await client.GetAsync("/api/status")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/license")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/license/request")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.OK);

        // 配置写入：现场可能需要先配好，待授权后即用（矩阵里明确放行）。
        (await client.PostAsJsonAsync("/api/config/reset", new { })).StatusCode
            .Should().NotBe(HttpStatusCode.Forbidden);

        // 探活端点不在 /api 之下，绝不能被授权闸门碰到。
        (await client.GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Warn_never_blocks()
    {
        var factory = NewFactory(enforcement: Warn);
        var client = await LoginAsync(factory);

        (await client.PostAsJsonAsync("/api/actions/execute", Ping())).StatusCode
            .Should().Be(
                HttpStatusCode.OK,
                "Warn 只提示不拦 —— 默认档位绝不能锁死升级后的现场");
    }

    [Fact]
    public async Task Off_never_blocks_even_when_unauthorized()
    {
        var factory = NewFactory(enforcement: nameof(LicenseEnforcement.Off));
        var client = await LoginAsync(factory);

        (await client.PostAsJsonAsync("/api/actions/execute", Ping())).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// 回归守门：`Server__License__Enforcement` 是<b>进程级</b>环境变量，
    /// 一旦某个宿主把它设成 <c>Enforce</c> 却没在下一个宿主创建时重置，
    /// 此后所有宿主都会被误判成"Enforce + 无授权"，于是所有要求动作执行的用例集体 403。
    ///
    /// <para>
    /// 症状极具迷惑性：<b>与授权毫无关系的用例突然变红</b>，而且是"换一次运行顺序就换一批"。
    /// 本用例直接钉死"新宿主不会继承上一个宿主的档位"这条不变量 ——
    /// 它不测闸门逻辑，测的是测试夹具本身不会互相污染。
    /// </para>
    /// </summary>
    [Fact]
    public void Enforcement_setting_does_not_leak_from_one_host_to_the_next()
    {
        var enforced = NewFactory(enforcement: Enforce);
        enforced.Services.GetRequiredService<LicenseService>()
            .ConfiguredEnforcement.Should().Be(LicenseEnforcement.Enforce);

        // 默认工厂（不传 enforcement）必须回到配置默认值，而不是继承上面的 Enforce。
        var plain = NewFactory();

        plain.Services.GetRequiredService<LicenseService>()
            .ConfiguredEnforcement.Should().Be(
                LicenseEnforcement.Warn,
                "测试夹具必须无条件重置进程级环境变量，否则用例之间会互相污染");
    }

    [Fact]
    public async Task Importing_a_license_lifts_the_gate_immediately()
    {
        var factory = NewFactory(enforcement: Enforce);
        var client = await LoginAsync(factory);

        (await client.PostAsJsonAsync("/api/actions/execute", Ping())).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        var machineCode = (await client.GetFromJsonAsync<LicenseDto>("/api/license"))!.MachineCode;

        var import = await client.PostAsJsonAsync(
            "/api/license/import", new ImportLicenseRequest(factory.Issue(NewDocument(machineCode))));

        import.StatusCode.Should().Be(HttpStatusCode.OK, await import.Content.ReadAsStringAsync());

        (await client.PostAsJsonAsync("/api/actions/execute", Ping())).StatusCode
            .Should().Be(HttpStatusCode.OK, "已授权 ⇒ 闸门放行");
    }

    [Fact]
    public async Task Enforce_without_a_license_blocks_the_manual_timer_fire()
    {
        var factory = NewFactory(enforcement: Enforce);
        var client = await LoginAsync(factory);

        (await client.PostAsync("/api/timers/whatever/fire", content: null)).StatusCode
            .Should().Be(
                HttpStatusCode.Forbidden,
                "手动触发就是执行按钮动作，必须与动作执行同档 —— 否则它成了一条绕过闸门的后门");
    }

    [Theory]
    [InlineData("/api/test/udp")]
    [InlineData("/api/test/loopback")]
    public async Task Enforce_without_a_license_blocks_the_network_test_endpoints(string path)
    {
        var factory = NewFactory(enforcement: Enforce);
        var client = await LoginAsync(factory);

        (await client.PostAsync(path, content: null)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "这些端点会真的往外发报文");
    }

    [Fact]
    public async Task A_corrupted_license_file_counts_as_unusable_and_blocks()
    {
        var factory = NewFactory(enforcement: Enforce);
        var client = await LoginAsync(factory);

        factory.WriteLicenseFile("这不是一份授权文件");

        // 先读一次状态，强制刷新（直接往数据目录写文件不会自动触发刷新）。
        var dto = await client.GetFromJsonAsync<LicenseDto>("/api/license");
        dto!.Status.Should().Be(nameof(LicenseStatus.Error));

        (await client.PostAsJsonAsync("/api/actions/execute", Ping())).StatusCode
            .Should().Be(
                HttpStatusCode.Forbidden,
                "文件在但读不成合法授权 ⇒ Error，绝不能悄悄退化成'未授权'或'放行'");
    }

    // ═══════════════════════════════════════════════════════════
    // D. 后台停摆
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Enforce_without_a_license_stops_the_pairing_listener()
    {
        var factory = NewFactory(enforcement: Enforce);
        await LoginAsync(factory);

        Listener(factory).IsRunning.Should().BeFalse(
            "对接码是 UDP 直连的，绕开 Web 界面照样能触发动作 —— 必须在监听这一层停摆");
    }

    [Fact]
    public async Task Warn_keeps_the_pairing_listener_running()
    {
        var factory = NewFactory(enforcement: Warn);
        await LoginAsync(factory);

        Listener(factory).IsRunning.Should().BeTrue("Warn 档位后台不停摆");
    }

    [Fact]
    public async Task Importing_a_license_restarts_the_pairing_listener_without_a_restart()
    {
        var factory = NewFactory(enforcement: Enforce);
        var client = await LoginAsync(factory);

        var listener = Listener(factory);
        listener.IsRunning.Should().BeFalse();

        var machineCode = (await client.GetFromJsonAsync<LicenseDto>("/api/license"))!.MachineCode;

        var import = await client.PostAsJsonAsync(
            "/api/license/import", new ImportLicenseRequest(factory.Issue(NewDocument(machineCode))));

        import.StatusCode.Should().Be(HttpStatusCode.OK, await import.Content.ReadAsStringAsync());

        listener.IsRunning.Should().BeTrue(
            "授权恢复后应当立刻重新开始监听 —— 要求现场重启一次服务才生效是不可接受的");
    }

    [Fact]
    public async Task Removing_the_license_stops_the_listener_again()
    {
        var factory = NewFactory(enforcement: Enforce);
        var client = await LoginAsync(factory);
        var listener = Listener(factory);

        var machineCode = (await client.GetFromJsonAsync<LicenseDto>("/api/license"))!.MachineCode;
        await client.PostAsJsonAsync(
            "/api/license/import", new ImportLicenseRequest(factory.Issue(NewDocument(machineCode))));

        listener.IsRunning.Should().BeTrue();

        (await client.DeleteAsync("/api/license")).StatusCode.Should().Be(HttpStatusCode.OK);

        listener.IsRunning.Should().BeFalse("移除授权后应重新进入停摆");
    }

    // ═══════════════════════════════════════════════════════════
    // 工具
    // ═══════════════════════════════════════════════════════════

    private const string Warn = nameof(LicenseEnforcement.Warn);
    private const string Enforce = nameof(LicenseEnforcement.Enforce);

    private LicenseTestFactory NewFactory(string? enforcement = null, bool autoTrial = false)
    {
        var factory = new LicenseTestFactory(enforcement, autoTrial);
        _factories.Add(factory);
        return factory;
    }

    private static PairingListenerService Listener(LicenseTestFactory factory)
        => factory.Services.GetRequiredService<PairingListenerService>();

    private static async Task<HttpClient> LoginAsync(LicenseTestFactory factory)
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("admin", "admin123"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "admin 必须能登录");

        return client;
    }

    /// <summary>一条真实的、会成功返回 200 的内联动作（发往 127.0.0.1:9 的丢弃端口）。</summary>
    private static ActionExecuteRequest Ping()
        => new(
            Label: "闸门探针",
            Actions:
            [
                new ActionExecuteItem(
                    TargetIp: "127.0.0.1",
                    TargetPort: 9,
                    Protocol: TransportProtocols.Udp,
                    DataType: DataTypes.String,
                    DataContent: "PING"),
            ]);

    /// <summary>一份字段合法、绑定本机、远期到期的授权（签名由 <see cref="LicenseTestFactory"/> 用测试私钥生成）。</summary>
    private static LicenseDocument NewDocument(string machineCode)
        => new()
        {
            V = LicenseDocument.CurrentVersion,
            Mc = machineCode,
            Lt = LicenseKind.Full,
            Id = "20261008",
            Ed = "20991231",
            Sn = "SN-GATE-001",
            Vi = "VENDOR-TEST",
            Hn = "闸门测试展厅",
            Ivt = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds(),
        };
}
