using System.Net;
using System.Net.Http.Json;
using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 客户机配置在 HTTP 面上的行为（M4.5）：<c>GET/PUT /api/agents/{id}/config</c>、
/// 「重新下发」、以及<b>双身份</b>的权限边界。
///
/// <para>
/// 这一组用例守的是三件<b>"做错了不报错、只是悄悄不对"</b>的事：
/// </para>
/// <list type="number">
///   <item><b>归属校验</b>：<c>GET config</c> 是"人"与"这台机器自己"共用的端点。
///         策略只能判断"是不是一台合法机器"，判断不了"是不是<b>这台</b>" ——
///         少了这层比较，任何拿到某台 agentKey 的机器都能读到<b>别的机器</b>的配置
///         （里面有目标 IP 与发送内容）。这是安全边界，不是便利性问题。</item>
///   <item><b>校验失败不能落库</b>：非法配置要 400 并<b>带上全部问题</b>，
///         且<b>版本号一动都不能动</b> —— 版本 +1 而内容没存，
///         客户端会白拉一次还把自己手上的好配置当成旧的。</item>
///   <item><b>推送</b>：保存成功要让页面知道"这台机器的配置变了"，
///         否则多个人同时开着客户端详情页时会看到各自不同的旧版本。</item>
/// </list>
///
/// <para>
/// 另外两条边界（<b>操作员能读不能写</b>、<b>观察者连读都不行</b>）也在这里钉住 ——
/// 它们是"角色分级真的在服务端生效，不是靠前端藏按钮"的一部分。
/// </para>
/// </summary>
public sealed class AgentConfigApiTests : IDisposable
{
    private const string StrongPassword = "Str0ng-Pass!";

    private readonly RecordingNotifierFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // ═══════════════════════════════════════════════════════════
    // 读：空配置的表示
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 "还没有配置"是<b>合法状态</b>而不是错误：返回 <c>Version=0</c> + <b>空内容</b>，
    /// 但 <c>UpdatedAt</c> 必须是 <c>null</c> —— 拿当前时间凑一个会让人以为"这台机器刚配过"。
    /// </summary>
    [Fact]
    public async Task A_fresh_agent_reports_version_zero_and_no_timestamp()
    {
        using var admin = await LoginAsync();
        var machine = await RegisterAgentAsync("cfg-fresh");

        var config = await admin.GetFromJsonAsync<AgentConfigDto>($"/api/agents/{machine.Id}/config");

        config!.Version.Should().Be(0, "0 = 该机还没有配置");
        config.UpdatedAt.Should().BeNull("从没配过就是 null，不能拿「现在」凑");
        // 空配置 = 通道默认开启 + 端口 8090（2026-10-13 起）。客户端据此自动绑端口开始收指令。
        config.Udp.Enabled.Should().BeTrue("UDP 指令通道默认开启");
        config.Udp.Port.Should().Be(8090, "默认端口 8090");
        config.SendTasks.Should().BeEmpty();
        config.ScheduledTasks.Should().BeEmpty();
        config.LaunchTasks.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════
    // 写：往返 / 规范化 / 版本
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task A_put_round_trips_all_three_task_lists_and_starts_the_version_at_one()
    {
        using var admin = await LoginAsync();
        var machine = await RegisterAgentAsync("cfg-roundtrip");

        var saved = await PutConfigAsync(admin, machine.Id, FullConfig());

        saved.Ok.Should().BeTrue(saved.Message);
        saved.Status.Should().Be("ok");
        saved.Config!.Version.Should().Be(1, "第一次保存 ⇒ 版本从 1 开始");
        saved.Config.UpdatedAt.Should().NotBeNull("配过了就该有时间戳");

        var fetched = await admin.GetFromJsonAsync<AgentConfigDto>($"/api/agents/{machine.Id}/config");

        fetched!.Version.Should().Be(1);
        fetched.Udp.Enabled.Should().BeTrue();
        fetched.Udp.Port.Should().Be(15000);
        fetched.Udp.BlockedSourceIps.Should().Equal(new[] { "10.0.0.7" });

        fetched.SendTasks.Should().HaveCount(1);
        fetched.SendTasks[0].Id.Should().Be("send-1");
        fetched.SendTasks[0].TargetIp.Should().Be("10.0.0.9");
        fetched.SendTasks[0].IsLoop.Should().BeTrue("「循环发送」是 IsLoop 标志，不是另一张表");

        fetched.ScheduledTasks.Should().HaveCount(1);
        fetched.ScheduledTasks[0].Times.Should().Equal(new[] { "08:00", "18:30" });

        fetched.LaunchTasks.Should().HaveCount(1);
        fetched.LaunchTasks[0].ProgramPath.Should().Be(@"C:\demo\player.exe");
    }

    /// <summary>
    /// 归一化发生在<b>存之前</b>：读回来的一定是"统一书写"后的形态。
    /// 前端因此不必自己再写一套（两套的偏差会表现为"我明明存了 tcp、列表显示 TCP、
    /// 但某处判断用的是 tcp"）。
    /// </summary>
    [Fact]
    public async Task A_put_stores_the_normalized_form_not_what_the_client_sent()
    {
        using var admin = await LoginAsync();
        var machine = await RegisterAgentAsync("cfg-normalize");

        await PutConfigAsync(admin, machine.Id, new AgentConfigSaveRequest(
            new AgentUdpChannelDto(true, 15000, new[] { " 10.0.0.7 ", "10.0.0.7" }),
            new[]
            {
                new AgentSendTaskDto(
                    "send-1", "udp", " 10.0.0.9 ", 9000, "UTF-8", "PAYLOAD",
                    DelaySeconds: 0, IsLoop: false, LoopIntervalSeconds: 60, Name: "  ", Enabled: true),
            },
            new[] { new AgentScheduledTaskDto("sch-1", "tcp", "10.0.0.9", 9000, "String", "X", new[] { "9:5" }) },
            Array.Empty<AgentLaunchTaskDto>()));

        var fetched = await admin.GetFromJsonAsync<AgentConfigDto>($"/api/agents/{machine.Id}/config");

        fetched!.SendTasks[0].Protocol.Should().Be("UDP", "协议名统一大写");
        fetched.SendTasks[0].TargetIp.Should().Be("10.0.0.9", "去首尾空白");
        fetched.SendTasks[0].DataType.Should().Be("String", "UTF-8 是历史别名，归一成 String");
        fetched.SendTasks[0].Name.Should().BeNull("纯空白当没填");

        fetched.ScheduledTasks[0].Times.Should().Equal(new[] { "09:05" }, "9:5 归一成 09:05");
        fetched.Udp.BlockedSourceIps.Should().Equal(new[] { "10.0.0.7" }, "去重 + 去空白");
    }

    /// <summary>
    /// 🔥 <b>校验失败绝不能留下半个效果</b>：版本号一动，客户端就会去拉，
    /// 而拉到的还是旧内容 —— 表现为"保存失败但版本变了、机器白忙一场"。
    /// </summary>
    [Fact]
    public async Task An_invalid_put_is_rejected_with_every_problem_and_changes_nothing()
    {
        using var admin = await LoginAsync();
        var machine = await RegisterAgentAsync("cfg-invalid");

        // 先存一份合法的，好验证"坏请求不会把它顶掉"。
        var good = await PutConfigAsync(admin, machine.Id, FullConfig());
        good.Config!.Version.Should().Be(1);

        var response = await admin.PutAsJsonAsync(
            $"/api/agents/{machine.Id}/config",
            new AgentConfigSaveRequest(
                new AgentUdpChannelDto(true, 80, new[] { "不是IP" }),
                new[] { new AgentSendTaskDto("bad", "FTP", "", 0, "指令库", "x") },
                null,
                null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<AgentConfigSaveResult>();

        result!.Ok.Should().BeFalse();
        result.Status.Should().Be("invalid");
        result.Message.Should().NotBeNullOrWhiteSpace("必须把问题一次说清，否则用户只能改一轮试一轮");
        result.Message.Should().Contain("协议", "问题清单里要有协议那条");
        result.Config.Should().BeNull("被拒的请求不该带回一份「看起来存上了」的配置");

        var after = await admin.GetFromJsonAsync<AgentConfigDto>($"/api/agents/{machine.Id}/config");

        after!.Version.Should().Be(1, "版本一动都不能动 —— 否则客户端会白拉一次");
        after.SendTasks.Should().HaveCount(1);
        after.SendTasks[0].Id.Should().Be("send-1", "原来那份配置必须原封不动");
    }

    [Fact]
    public async Task A_partial_put_only_touches_the_blocks_it_carries()
    {
        using var admin = await LoginAsync();
        var machine = await RegisterAgentAsync("cfg-partial");

        await PutConfigAsync(admin, machine.Id, FullConfig());

        // 只带 Udp 的补丁：三张清单（null）必须原样保留。
        await PutConfigAsync(admin, machine.Id, new AgentConfigSaveRequest(
            new AgentUdpChannelDto(true, 16000, null),
            null,
            null,
            null));

        var fetched = await admin.GetFromJsonAsync<AgentConfigDto>($"/api/agents/{machine.Id}/config");

        fetched!.Udp.Port.Should().Be(16000, "带上的那一块被替换");
        fetched.SendTasks.Should().HaveCount(1, "没带的块（null）= 不动");
        fetched.ScheduledTasks.Should().HaveCount(1);
        fetched.LaunchTasks.Should().HaveCount(1);
        fetched.Version.Should().Be(2, "每次保存都 +1，哪怕只改了一块");
    }

    [Fact]
    public async Task A_put_for_an_unknown_agent_is_not_found()
    {
        using var admin = await LoginAsync();

        var response = await admin.PutAsJsonAsync(
            "/api/agents/999999/config", new AgentConfigSaveRequest());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ═══════════════════════════════════════════════════════════
    // 推送
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 保存成功要广播"这台机器的配置变了"。少了这条，多个人同时开着客户端详情页时，
    /// 谁都不会看到别人的改动，而<b>没有任何地方会报错</b>。
    /// </summary>
    [Fact]
    public async Task A_successful_put_pushes_a_config_changed_notification()
    {
        using var admin = await LoginAsync();
        var machine = await RegisterAgentAsync("cfg-push");

        await PutConfigAsync(admin, machine.Id, FullConfig());

        _factory.Notifier.ConfigChangedAgents.Should().Contain(
            machine.Id, "保存成功后要给页面推一条「这台机器的配置变了」");

        _factory.Notifier.ConfigChangedAgents.Count(x => x == machine.Id).Should().Be(
            1, "一次保存只推一条，重复推送会让前端白刷");
    }

    // ═══════════════════════════════════════════════════════════
    // 双身份：机器自己 vs 人
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task The_agent_identity_can_read_its_own_config()
    {
        using var admin = await LoginAsync();
        var machine = await RegisterAgentAsync("cfg-self");

        await PutConfigAsync(admin, machine.Id, FullConfig());

        using var asMachine = _factory.CreateClient();
        asMachine.DefaultRequestHeaders.TryAddWithoutValidation(
            AgentAuthentication.HeaderName, machine.AgentKey);

        var response = await asMachine.GetAsync($"/api/agents/{machine.Id}/config");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "客户机启动 / 收到变更通知后要能拉自己的配置，否则任务跑不起来");

        var config = await response.Content.ReadFromJsonAsync<AgentConfigDto>();
        config!.SendTasks.Should().HaveCount(1);
    }

    /// <summary>
    /// 🔥 <b>归属校验是安全边界</b>：任务清单里有<b>目标 IP 与发送内容</b>。
    /// 少了这条比较，任何拿到某台 agentKey 的机器都能读到别的机器的配置 ——
    /// 相当于把整个现场的网络与指令布局摊开给每一台被控机。
    /// <para>用 403 而不是 404：它确实是一台合法机器，只是越权读别人的。</para>
    /// </summary>
    [Fact]
    public async Task The_agent_identity_cannot_read_another_machines_config()
    {
        using var admin = await LoginAsync();

        var mine = await RegisterAgentAsync("cfg-mine");
        var others = await RegisterAgentAsync("cfg-others");

        await PutConfigAsync(admin, others.Id, FullConfig());

        using var asMine = _factory.CreateClient();
        asMine.DefaultRequestHeaders.TryAddWithoutValidation(
            AgentAuthentication.HeaderName, mine.AgentKey);

        var response = await asMine.GetAsync($"/api/agents/{others.Id}/config");

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden, "机器身份只能读自己的 —— 越权读别人的配置必须被挡住");
    }

    [Fact]
    public async Task An_unknown_agent_key_cannot_read_anything()
    {
        using var admin = await LoginAsync();
        var machine = await RegisterAgentAsync("cfg-forged");

        using var forged = _factory.CreateClient();
        forged.DefaultRequestHeaders.TryAddWithoutValidation(
            AgentAuthentication.HeaderName, "this-key-was-never-issued");

        var response = await forged.GetAsync($"/api/agents/{machine.Id}/config");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "伪造的 agentKey 连认证都过不去");
    }

    /// <summary>操作员能读（要看运行状况），但改配置是需要管理员权限的动作。</summary>
    [Fact]
    public async Task An_operator_can_read_but_cannot_write_agent_config()
    {
        using var admin = await LoginAsync();
        var machine = await RegisterAgentAsync("cfg-operator");

        using var op = await CreateUserAndLoginAsync(admin, "cfg-op", Roles.Operator);

        (await op.GetAsync($"/api/agents/{machine.Id}/config")).StatusCode.Should().Be(HttpStatusCode.OK);

        var put = await op.PutAsJsonAsync(
            $"/api/agents/{machine.Id}/config", new AgentConfigSaveRequest());

        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// 观察者连读都不行：配置里有目标 IP 与发送内容，
    /// 它不是"看板数据"，而是"这套设备的网络与指令布局"。
    /// </summary>
    [Fact]
    public async Task A_viewer_cannot_read_or_write_agent_config()
    {
        using var admin = await LoginAsync();
        var machine = await RegisterAgentAsync("cfg-viewer");

        using var viewer = await CreateUserAndLoginAsync(admin, "cfg-vw", Roles.Viewer);

        (await viewer.GetAsync($"/api/agents/{machine.Id}/config")).StatusCode.Should().Be(
            HttpStatusCode.Forbidden);

        (await viewer.PutAsJsonAsync(
            $"/api/agents/{machine.Id}/config", new AgentConfigSaveRequest())).StatusCode.Should().Be(
            HttpStatusCode.Forbidden);
    }

    // ═══════════════════════════════════════════════════════════
    // 重新下发
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 「重新下发」是发给<b>活着的 SignalR 连接</b>的。没有连接（机器离线 / 还没连上 Hub）
    /// 时回 <b>409</b> 而不是 200 —— 200 会让运维以为"推过去了，机器没反应说明它坏了"，
    /// 而真实原因是"它根本不在线"。
    /// </summary>
    [Fact]
    public async Task Redeliver_returns_409_when_the_agent_has_no_live_connection()
    {
        using var admin = await LoginAsync();
        var machine = await RegisterAgentAsync("cfg-offline");

        var response = await admin.PostAsync($"/api/agents/{machine.Id}/config/redeliver", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var result = await response.Content.ReadFromJsonAsync<AgentMutationResult>();

        result!.Ok.Should().BeFalse();
        result.Status.Should().Be("offline");
        result.Message.Should().Contain("离线");
    }

    [Fact]
    public async Task Redeliver_for_an_unknown_agent_is_not_found()
    {
        using var admin = await LoginAsync();

        (await admin.PostAsync("/api/agents/999999/config/redeliver", null)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    private static AgentConfigSaveRequest FullConfig() => new(
        new AgentUdpChannelDto(true, 15000, new[] { "10.0.0.7" }),
        new[]
        {
            new AgentSendTaskDto(
                "send-1", "UDP", "10.0.0.9", 9000, "String", "PAYLOAD",
                DelaySeconds: 0, IsLoop: true, LoopIntervalSeconds: 30, Name: "循环", Enabled: true),
        },
        new[]
        {
            new AgentScheduledTaskDto(
                "sch-1", "UDP", "10.0.0.9", 9000, "String", "PAYLOAD",
                new[] { "08:00", "18:30" }, "定时", true),
        },
        new[] { new AgentLaunchTaskDto("launch-1", "播放器", @"C:\demo\player.exe", "--fullscreen", 5, true) });

    private async Task<AgentConfigSaveResult> PutConfigAsync(
        HttpClient admin, int agentId, AgentConfigSaveRequest request)
    {
        var response = await admin.PutAsJsonAsync($"/api/agents/{agentId}/config", request);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, $"保存应成功：{await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<AgentConfigSaveResult>())!;
    }

    private async Task<HttpClient> LoginAsync(string userName = "admin", string password = "admin123")
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, password));

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{userName} 必须能登录，否则后面的断言全无意义");
        return client;
    }

    private async Task<HttpClient> CreateUserAndLoginAsync(HttpClient admin, string userName, string role)
    {
        var created = await admin.PostAsJsonAsync(
            "/api/users", new CreateUserRequest(userName, StrongPassword, role));

        created.StatusCode.Should().Be(
            HttpStatusCode.OK, $"新建 {userName} 应成功：{await created.Content.ReadAsStringAsync()}");

        return await LoginAsync(userName, StrongPassword);
    }

    /// <summary>
    /// 免凭据注册一台机器，返回它的 Id 与 agentKey。
    /// <para>
    /// <b>2026-10-13 起注册不再需要令牌 / 加入码</b>：直接打匿名端点即可，
    /// 这里不再有"先建一个加入码"的前置步骤。
    /// </para>
    /// <para>agentKey 是"这台机器的身份"，用它才能验证双身份端点的归属校验。</para>
    /// </summary>
    private async Task<(int Id, string AgentKey)> RegisterAgentAsync(string machineKey)
    {
        using var anonymous = _factory.CreateClient();

        var registered = await anonymous.PostAsJsonAsync(
            "/api/agents/register",
            new AgentRegisterRequest(
                new AgentHostInfoDto(
                    machineKey,
                    "CFG-PC",
                    "Windows 11 (integration test)",
                    "Test CPU @ 1.00GHz",
                    4,
                    8192,
                    "0.1.0",
                    "tester",
                    new[] { new AgentNetworkInterfaceDto("以太网", "192.168.1.10", "AA:BB:CC:DD:EE:03", true) })));

        registered.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = (await registered.Content.ReadFromJsonAsync<AgentRegisterResult>())!;

        result.AgentKey.Should().NotBeNullOrWhiteSpace("注册成功要发回这台机器的长期凭据");

        return (result.AgentId!.Value, result.AgentKey!);
    }

    /// <summary>
    /// 把 <see cref="INotifier"/> 换成记录型（用宿主里<b>同一套</b>服务实例，验证真实组合根下的连线）。
    /// </summary>
    private sealed class RecordingNotifierFactory : ControlServerFactory
    {
        public RecordingNotifier Notifier { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            ReplaceServices(builder, services => services.AddSingleton<INotifier>(Notifier));
        }
    }

    private sealed class RecordingNotifier : INotifier
    {
        private readonly List<int> _configChangedAgents = new();

        public IReadOnlyList<int> ConfigChangedAgents
        {
            get { lock (_configChangedAgents) return _configChangedAgents.ToArray(); }
        }

        public Task LogAddedAsync(LogDto item, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task StatusChangedAsync(StatusSnapshot snapshot, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ActionResultAsync(ActionResultDto result, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task TrafficLogAddedAsync(TrafficLogDto item, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ConfigChangedAsync(ConfigChangedDto change, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task LicenseChangedAsync(LicenseDto license, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AgentChangedAsync(AgentHostDto host, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AgentOnlineAsync(AgentHostDto host, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AgentOfflineAsync(AgentHostDto host, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AgentTelemetryUpdatedAsync(AgentLiveMetricsDto live, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task TransferProgressAsync(TransferProgressDto progress, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AgentConfigChangedAsync(int agentId, CancellationToken cancellationToken = default)
        {
            lock (_configChangedAgents) _configChangedAgents.Add(agentId);
            return Task.CompletedTask;
        }
    }
}
