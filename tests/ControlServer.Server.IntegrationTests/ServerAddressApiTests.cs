using System.Net;
using System.Net.Http.Json;
using ControlServer.Contracts;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 监听地址端点（M2-E5）的契约测试。
///
/// <para>
/// <b>这里测不到"真的换了端口"</b>：测试夹具用的是 <c>WebApplicationFactory</c> 的内存
/// <c>TestServer</c>，没有真实 Kestrel socket，也就没有可换的监听器。
/// 于是本文件覆盖<b>契约</b>（状态码、字段、降级行为），
/// 而"切换真的生效 / 端口被占用仍存活"由两处覆盖：
/// </para>
/// <list type="bullet">
///   <item><description><c>SwitchableConnectionListenerTests</c> —— 用假 listener 钉死切换与回滚语义；</description></item>
///   <item><description>真机验证 —— 真实 Kestrel + 真实端口占用（见当日工作日志）。</description></item>
/// </list>
/// </summary>
public sealed class ServerAddressApiTests : IDisposable
{
    private readonly ControlServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { userName = "admin", password = "admin123" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, "测试宿主必须能用默认管理员登录");
        return client;
    }

    [Fact]
    public async Task Address_reports_the_file_that_persists_the_port()
    {
        var client = await LoginAsync();

        var response = await client.GetAsync("/api/system/address");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<ServerAddressDto>();
        dto.Should().NotBeNull();

        // overrideFile 必须指向**本次宿主的数据目录** —— 这是运维找到"端口被改到哪去了"的唯一线索。
        dto!.OverrideFile.Should().StartWith(_factory.DataDirectory);
        dto.OverrideFile.Should().EndWith("server-overrides.json");
        dto.Persisted.Should().BeFalse("全新数据目录里还没有覆盖文件");
        dto.Urls.Should().NotBeEmpty();

        // 测试宿主没有真实 Kestrel 监听器，此时应回退到配置值而不是返回空列表。
        dto.Port.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Address_change_requires_login()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            "/api/system/address",
            new ServerAddressChangeRequest(Port: 19999, Urls: null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("ftp://0.0.0.0:9000", "scheme")]
    [InlineData("http://0.0.0.0", "端口")]
    [InlineData("", "不合法")]
    public async Task Address_change_rejects_malformed_urls(string url, string expectedHint)
    {
        var client = await LoginAsync();

        var response = await client.PostAsJsonAsync(
            "/api/system/address",
            new ServerAddressChangeRequest(new[] { url }, Port: null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var dto = await response.Content.ReadFromJsonAsync<ServerAddressChangeResultDto>();
        dto!.Status.Should().Be("Invalid");
        dto.Message.Should().Contain(expectedHint);
        dto.Ok.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public async Task Address_change_rejects_out_of_range_ports(int port)
    {
        var client = await LoginAsync();

        var response = await client.PostAsJsonAsync(
            "/api/system/address",
            new ServerAddressChangeRequest(Urls: null, Port: port));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var dto = await response.Content.ReadFromJsonAsync<ServerAddressChangeResultDto>();
        dto!.Status.Should().Be("Invalid");
        dto.Message.Should().Contain("越界");
    }

    [Fact]
    public async Task Address_change_without_target_is_rejected()
    {
        var client = await LoginAsync();

        var response = await client.PostAsJsonAsync(
            "/api/system/address",
            new ServerAddressChangeRequest(Urls: null, Port: null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var dto = await response.Content.ReadFromJsonAsync<ServerAddressChangeResultDto>();
        dto!.Message.Should().Contain("urls 或 port");
    }

    [Fact]
    public async Task Address_change_to_the_current_address_is_a_no_op()
    {
        var client = await LoginAsync();

        var current = await client.GetFromJsonAsync<ServerAddressDto>("/api/system/address");

        var response = await client.PostAsJsonAsync(
            "/api/system/address",
            new ServerAddressChangeRequest(current!.Urls, Port: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<ServerAddressChangeResultDto>();
        dto!.Ok.Should().BeTrue();
        dto.Status.Should().Be("NoChange");
        dto.SecondsUntilSwitch.Should().Be(0, "没有任何切换动作需要等待");
    }

    /// <summary>
    /// 宿主形态不支持热切换时（本例：内存 TestServer 没有真实监听器），必须<b>明确降级</b>：
    /// 409 + 说清原因，而<b>不是</b>假装成功、更不是把服务弄挂。
    /// </summary>
    [Fact]
    public async Task Address_change_degrades_visibly_when_the_host_has_no_real_listener()
    {
        var client = await LoginAsync();

        var current = await client.GetFromJsonAsync<ServerAddressDto>("/api/system/address");

        var targetPort = current!.Port + 1;

        var response = await client.PostAsJsonAsync(
            "/api/system/address",
            new ServerAddressChangeRequest(Urls: null, Port: targetPort));

        // 内存 TestServer → 没有可换的监听器 → 409 NotReady（而不是 500 或假成功）。
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var dto = await response.Content.ReadFromJsonAsync<ServerAddressChangeResultDto>();
        dto!.Ok.Should().BeFalse();
        dto.Status.Should().Be("NotReady");
        dto.NewPort.Should().Be(targetPort);

        // 服务必须毫发无损：数据目录里也不该留下任何覆盖文件（失败不落盘）。
        File.Exists(Path.Combine(_factory.DataDirectory, "server-overrides.json"))
            .Should().BeFalse("切换没成功就绝不能写持久化文件 —— 否则下次启动会去绑一个已知不可用的端口");

        (await client.GetAsync("/readyz")).StatusCode.Should().Be(HttpStatusCode.OK, "服务必须仍然可用");
    }

    [Fact]
    public async Task System_info_and_address_agree_on_the_port()
    {
        var client = await LoginAsync();

        var address = await client.GetFromJsonAsync<ServerAddressDto>("/api/system/address");
        var info = await client.GetFromJsonAsync<SystemInfoDto>("/api/system/info");

        // 两处若各查一次来源，就会出现"首页说 8080、诊断页说 9000"这种最难查的不一致。
        info!.HttpPort.Should().Be(address!.Port);
    }

    /// <summary>
    /// 热切换只作用于<b>主</b>地址，所以一次给多个地址必须被拒 ——
    /// 否则会出现"只切了第一个、却把全部写进持久化文件"的假成功，
    /// 重启后多出来的地址会真的被绑上，运行期与重启后行为不一致。
    /// </summary>
    [Fact]
    public async Task Address_change_rejects_more_than_one_url()
    {
        var client = await LoginAsync();

        var response = await client.PostAsJsonAsync(
            "/api/system/address",
            new ServerAddressChangeRequest(
                new[] { "http://0.0.0.0:9101", "http://0.0.0.0:9102" },
                Port: null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var dto = await response.Content.ReadFromJsonAsync<ServerAddressChangeResultDto>();
        dto!.Status.Should().Be("Invalid");
        dto.Ok.Should().BeFalse();
        dto.Message.Should().Contain("只能改一个");

        // 拒绝之后不能留下任何痕迹。
        File.Exists(Path.Combine(_factory.DataDirectory, "server-overrides.json"))
            .Should().BeFalse("参数没通过校验就不该写持久化文件");
    }

    /// <summary>
    /// 同一个地址重复给两遍（归一化后其实只有 1 个）应当<b>放行</b> ——
    /// 拒绝的门槛是"去重后仍多于 1 个"，不是"数组长度大于 1"。
    /// </summary>
    [Fact]
    public async Task Address_change_tolerates_a_duplicated_single_url()
    {
        var client = await LoginAsync();

        var current = await client.GetFromJsonAsync<ServerAddressDto>("/api/system/address");
        var url = current!.Urls[0];

        var response = await client.PostAsJsonAsync(
            "/api/system/address",
            new ServerAddressChangeRequest(new[] { url, url }, Port: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<ServerAddressChangeResultDto>();
        dto!.Status.Should().Be("NoChange");
    }

    /// <summary>
    /// 「地址没变」不等于「无需任何动作」：持久化文件可能落后于现状
    /// （例如早期版本把多地址写了进去）。此时若直接返回，界面上就永远删不掉多余的地址。
    /// </summary>
    [Fact]
    public async Task No_change_still_converges_a_stale_override_file()
    {
        var client = await LoginAsync();

        var current = await client.GetFromJsonAsync<ServerAddressDto>("/api/system/address");
        var path = Path.Combine(_factory.DataDirectory, "server-overrides.json");

        // 手工造一份"落后"的持久化文件：里面的地址与实际监听不一致。
        await File.WriteAllTextAsync(
            path,
            """{"Server":{"Http":{"Urls":"http://0.0.0.0:1234;http://0.0.0.0:5678"}}}""");

        var response = await client.PostAsJsonAsync(
            "/api/system/address",
            new ServerAddressChangeRequest(current!.Urls, Port: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<ServerAddressChangeResultDto>();
        dto!.Status.Should().Be("NoChange");

        // 关键断言：即便地址没变，落后的文件也必须被收敛。
        var text = await File.ReadAllTextAsync(path);
        text.Should().Contain(current.Urls[0], "文件应被改写成当前实际地址");
        text.Should().NotContain("1234", "多余的旧地址必须被清掉，否则界面上永远改不动");
        text.Should().NotContain("5678", "多余的旧地址必须被清掉，否则界面上永远改不动");
    }

    /// <summary>
    /// 反向守门：文件<b>不存在</b>时，「地址没变」是一次纯粹的空操作，
    /// 不该凭空生成持久化文件 —— 否则一次"点一下确认"就会把环境变量传进来的临时端口固化下来。
    /// </summary>
    [Fact]
    public async Task No_change_does_not_create_an_override_file_out_of_nowhere()
    {
        var client = await LoginAsync();

        var current = await client.GetFromJsonAsync<ServerAddressDto>("/api/system/address");

        var path = Path.Combine(_factory.DataDirectory, "server-overrides.json");
        File.Exists(path).Should().BeFalse("前置条件：全新数据目录");

        var response = await client.PostAsJsonAsync(
            "/api/system/address",
            new ServerAddressChangeRequest(current!.Urls, Port: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        File.Exists(path).Should().BeFalse("地址没变且从来没有覆盖文件时，不该产生副作用");
    }
}
