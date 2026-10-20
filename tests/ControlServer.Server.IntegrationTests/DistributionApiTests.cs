using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ControlServer.Contracts;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 文件分发的集成测试（M4.4）：建任务 / 列表 / 客户机补领 / 回报进度与结果 / 归属校验。
///
/// <para>
/// 重点是几处<b>"离线不是失败"</b>与"谁只能动自己那行"的语义：
/// 建任务时就把明细建满（离线机器上线补领）；已收字节只增不减；终态一旦定下不再改写；
/// 客户机不能动别的机器的条目。这些错了都不抛异常，只会让页面上的数字慢慢变得不可信。
/// </para>
/// </summary>
public sealed class DistributionApiTests : IDisposable
{
    private readonly List<ControlServerFactory> _factories = new();

    public void Dispose()
    {
        foreach (var factory in _factories) factory.Dispose();
    }

    // ═══════════════════════════════════════════════════════════
    // 工具
    // ═══════════════════════════════════════════════════════════

    private ControlServerFactory NewFactory()
    {
        var factory = new ControlServerFactory();
        _factories.Add(factory);
        return factory;
    }

    private static async Task<HttpClient> LoginAsync(
        ControlServerFactory factory,
        string userName = "admin",
        string password = "admin123")
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, password));
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{userName} 必须能登录");

        return client;
    }

    private static async Task<HttpClient> CreateUserAndLoginAsync(
        ControlServerFactory factory,
        HttpClient admin,
        string userName,
        string role)
    {
        const string password = "Str0ng-Pass!";

        var created = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest(userName, password, role));
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        return await LoginAsync(factory, userName, password);
    }

    private static AgentHostInfoDto HostInfo(string machineKey)
        => new(
            machineKey,
            "SMOKE-PC",
            "Windows 11 (integration test)",
            "Test CPU @ 1.00GHz",
            4,
            8192,
            "0.1.0",
            "tester",
            new[] { new AgentNetworkInterfaceDto("以太网", "192.168.1.10", "AA:BB:CC:DD:EE:01", true) });

    /// <summary>免凭据注册一台客户机并返回一个带着它 AgentKey 的 HttpClient。</summary>
    private static async Task<(int Id, HttpClient Client)> RegisterAgentAsync(
        ControlServerFactory factory,
        string machineKey)
    {
        var anonymous = factory.CreateClient();

        var registered = await anonymous.PostAsJsonAsync(
            "/api/agents/register", new AgentRegisterRequest(HostInfo(machineKey)));

        registered.StatusCode.Should().Be(HttpStatusCode.OK, await registered.Content.ReadAsStringAsync());

        var result = await registered.Content.ReadFromJsonAsync<AgentRegisterResult>();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AgentAuthentication.HeaderName, result!.AgentKey);

        return (result.AgentId!.Value, client);
    }

    private static async Task<(int Id, string Sha)> UploadFileAsync(HttpClient admin, string name, byte[] payload)
    {
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var created = await admin.PostAsJsonAsync(
            "/api/files/uploads", new FileUploadCreateRequest(name, payload.Length, null, sha));

        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        var session = (await created.Content.ReadFromJsonAsync<FileUploadSessionDto>())!;

        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        (await admin.PutAsync($"/api/files/uploads/{session.UploadId}?offset=0", content)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var completed = await admin.PostAsJsonAsync(
            $"/api/files/uploads/{session.UploadId}/complete", new FileUploadCompleteRequest());

        completed.StatusCode.Should().Be(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync());

        var result = (await completed.Content.ReadFromJsonAsync<FileUploadResultDto>())!;

        return (result.Id, result.Sha256);
    }

    private static async Task<AgentTransferAckDto> SendProgressAsync(
        HttpClient agent,
        int itemId,
        long received,
        long total)
    {
        var response = await agent.PostAsJsonAsync(
            $"/api/agents/transfers/{itemId}/progress", new AgentTransferProgressRequest(received, total));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<AgentTransferAckDto>())!;
    }

    private static async Task<AgentTransferAckDto> SendCompleteAsync(
        HttpClient agent,
        int itemId,
        bool ok,
        long received,
        string? sha = null,
        string? error = null)
    {
        var response = await agent.PostAsJsonAsync(
            $"/api/agents/transfers/{itemId}/complete",
            new AgentTransferCompleteRequest(ok, received, sha, error));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<AgentTransferAckDto>())!;
    }

    /// <summary>造目标清单：一批 id，都不带目录（用各机的默认目录）。</summary>
    private static IReadOnlyList<DistributionTargetRequest> Targets(params int[] ids)
        => ids.Select(id => new DistributionTargetRequest(id)).ToList();

    /// <summary>造目标清单：每台带一个目录。</summary>
    private static IReadOnlyList<DistributionTargetRequest> Dirs(params (int Id, string? Dir)[] specs)
        => specs.Select(s => new DistributionTargetRequest(s.Id, s.Dir)).ToList();

    // ═══════════════════════════════════════════════════════════
    // 建任务与权限
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Creating_a_task_requires_admin()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var (fileId, _) = await UploadFileAsync(admin, "pkg.bin", RandomBytes(512));

        var op = await CreateUserAndLoginAsync(factory, admin, "operator1", Roles.Operator);

        (await op.PostAsJsonAsync("/api/distributions", new DistributionCreateRequest(fileId, Targets(1))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "推文件到客户机与「远程重启」同级，只有 Admin 能做");

        // 但 Operator 能看任务列表。
        (await op.GetAsync("/api/distributions")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Creating_a_push_for_a_missing_file_is_not_found()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var response = await admin.PostAsJsonAsync(
            "/api/distributions", new DistributionCreateRequest(9999, Targets(1)));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Creating_a_task_without_targets_or_source_is_bad_request()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var (fileId, _) = await UploadFileAsync(admin, "pkg.bin", RandomBytes(512));

        (await admin.PostAsJsonAsync(
                "/api/distributions",
                new DistributionCreateRequest(fileId, Array.Empty<DistributionTargetRequest>())))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "至少选一台客户机");

        // 旧形状（只给 id 列表、全批共用 TargetDir）也必须照常被接受 —— 冒烟脚本与老调用方还在用它。
        (await admin.PostAsJsonAsync(
                "/api/distributions",
                new DistributionCreateRequest(fileId, TargetAgentIds: new[] { 0 })))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "旧形状里的非法 id 同样要被挡下");

        (await admin.PostAsJsonAsync("/api/distributions/collect", new CollectCreateRequest(new[] { 1 }, null)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "回传必须给源路径");
    }

    // ═══════════════════════════════════════════════════════════
    // 离线机器也能收到：明细建满 + 上线补领
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 建任务时<b>一次性为每个目标建满明细</b>（哪怕机器离线）。
    /// <para>
    /// 否则任务总览会显示成"只派了 1 台"，而后台其实还惦记着另外 1 台 —— 页面与现实不一致。
    /// 这也正是"离线机器上线后调 GET /api/agents/transfers 补领"能成立的前提。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_task_creates_one_item_per_target_even_when_they_are_offline()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var (a1, _) = await RegisterAgentAsync(factory, "machine-a");
        var (a2, _) = await RegisterAgentAsync(factory, "machine-b");

        var (fileId, _) = await UploadFileAsync(admin, "pkg.bin", RandomBytes(4_096));

        var created = await admin.PostAsJsonAsync(
            "/api/distributions", new DistributionCreateRequest(fileId, Dirs((a1, "inbox"), (a2, "inbox"), (a1, "inbox"))));

        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        var task = (await created.Content.ReadFromJsonAsync<DistributionTaskDto>())!;
        task.Direction.Should().Be(DistributionDirections.Push);
        task.Total.Should().Be(2, "重复的 id 被去重");
        task.Items.Should().OnlyContain(i => i.State == DistributionItemStates.Pending);
        task.Items.Should().OnlyContain(i => i.TotalBytes == 4_096, "下发时总大小已知");
        task.TargetDir.Should().Be("inbox", "全批同一个目录 ⇒ 任务级也记一份（列表页摘要用）");
        task.Items.Should().OnlyContain(i => i.TargetDir == "inbox", "每台各自的目录落在明细上");

        // 列表里也能看到（含每目标进度）。
        var list = (await admin.GetFromJsonAsync<DistributionListDto>("/api/distributions"))!;
        list.Total.Should().Be(1);
        list.Items.Single().Id.Should().Be(task.Id);

        // 单台详情同理。
        (await admin.GetFromJsonAsync<DistributionTaskDto>($"/api/distributions/{task.Id}"))!
            .Items.Should().HaveCount(2);

        // 两台都还没握上 SignalR ⇒ 派发时它们的连接 Id 为空 ⇒ 不推。
        // 但这不妨碍它们稍后自己补领（下一条用例专门验这件事）—— 明细已经建好了。
    }

    /// <summary>
    /// 客户机上线后调 <c>GET /api/agents/transfers</c> 补领——这是"离线机器也能收到文件"的实现点。
    /// 载荷里必须带方向 / 文件名 / 大小 / 期望摘要 / 目标目录（客户端据此决定怎么传）。
    /// </summary>
    [Fact]
    public async Task An_agent_catches_up_on_its_pending_transfers()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var (agentId, agent) = await RegisterAgentAsync(factory, "machine-a");

        var (fileId, sha) = await UploadFileAsync(admin, "pkg.bin", RandomBytes(4_096));

        var task = (await (await admin.PostAsJsonAsync(
                "/api/distributions", new DistributionCreateRequest(fileId, Dirs((agentId, "inbox")))))
            .Content.ReadFromJsonAsync<DistributionTaskDto>())!;

        var pending = (await agent.GetFromJsonAsync<List<AgentTransferTaskDto>>("/api/agents/transfers"))!;

        pending.Should().ContainSingle();
        var transfer = pending.Single();
        transfer.TaskId.Should().Be(task.Id);
        transfer.ItemId.Should().Be(task.Items.Single().Id);
        transfer.Direction.Should().Be(DistributionDirections.Push);
        transfer.FileId.Should().Be(fileId);
        transfer.FileName.Should().Be("pkg.bin");
        transfer.Size.Should().Be(4_096);
        transfer.Sha256.Should().Be(sha, "客户端落地前必须校验摘要");
        transfer.TargetDir.Should().Be("inbox");
        transfer.SourcePath.Should().BeNull("下发没有源路径");
    }

    /// <summary>
    /// 🔥 <b>每台客户机各自的存放路径</b>（2026-10-13 需求）：
    /// 同批下发里 A 落到 <c>D:\stage</c>、B 落到 <c>E:\share</c>，
    /// 两台补领时拿到的 <c>TargetDir</c> 必须<b>各自不同</b>。
    /// <para>
    /// 这是本需求最容易被"顺手回填任务级目录"悄悄做坏的一处 ——
    /// 页面上仍显示"每台一个目录"，但实际两台都会落到同一个地方。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Each_target_carries_its_own_directory()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var (a1, c1) = await RegisterAgentAsync(factory, "machine-a");
        var (a2, c2) = await RegisterAgentAsync(factory, "machine-b");

        var (fileId, _) = await UploadFileAsync(admin, "pkg.bin", RandomBytes(1_024));

        var task = (await (await admin.PostAsJsonAsync(
                "/api/distributions",
                new DistributionCreateRequest(fileId, Dirs((a1, @"D:\stage"), (a2, @"E:\share")))))
            .Content.ReadFromJsonAsync<DistributionTaskDto>())!;

        task.TargetDir.Should().BeNull("两台目录不同 ⇒ 不能挑一个冒充全局");
        task.Items.Single(i => i.AgentId == a1).TargetDir.Should().Be(@"D:\stage");
        task.Items.Single(i => i.AgentId == a2).TargetDir.Should().Be(@"E:\share");

        var p1 = (await c1.GetFromJsonAsync<List<AgentTransferTaskDto>>("/api/agents/transfers"))!.Single();
        var p2 = (await c2.GetFromJsonAsync<List<AgentTransferTaskDto>>("/api/agents/transfers"))!.Single();

        p1.TargetDir.Should().Be(@"D:\stage", "A 拿到的必须是 A 自己的目录");
        p2.TargetDir.Should().Be(@"E:\share", "B 拿到的必须是 B 自己的目录");
    }

    /// <summary>
    /// 旧形状（<c>TargetAgentIds</c> + 全批共用的 <c>TargetDir</c>）要照常工作 ——
    /// 冒烟脚本与老调用方还在用它，改成"每台一个目录"不该把它们打断。
    /// </summary>
    [Fact]
    public async Task The_legacy_single_directory_shape_still_works()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var (a1, c1) = await RegisterAgentAsync(factory, "machine-a");

        var (fileId, _) = await UploadFileAsync(admin, "pkg.bin", RandomBytes(1_024));

        var task = (await (await admin.PostAsJsonAsync(
                "/api/distributions",
                new DistributionCreateRequest(fileId, TargetAgentIds: new[] { a1 }, TargetDir: "inbox")))
            .Content.ReadFromJsonAsync<DistributionTaskDto>())!;

        task.TargetDir.Should().Be("inbox", "旧形状仍把共用目录记在任务级");
        task.Items.Single().TargetDir.Should().Be("inbox", "并逐台写进明细");

        (await c1.GetFromJsonAsync<List<AgentTransferTaskDto>>("/api/agents/transfers"))!
            .Single().TargetDir.Should().Be("inbox");
    }

    // ═══════════════════════════════════════════════════════════
    // 回报进度与结果
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Progress_and_completion_flow_from_agent_to_task_overview()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var (a1, c1) = await RegisterAgentAsync(factory, "machine-a");
        var (a2, c2) = await RegisterAgentAsync(factory, "machine-b");

        var (fileId, sha) = await UploadFileAsync(admin, "pkg.bin", RandomBytes(10_000));

        var task = (await (await admin.PostAsJsonAsync(
                "/api/distributions", new DistributionCreateRequest(fileId, Targets(a1, a2))))
            .Content.ReadFromJsonAsync<DistributionTaskDto>())!;

        var item1 = task.Items.Single(i => i.AgentId == a1).Id;
        var item2 = task.Items.Single(i => i.AgentId == a2).Id;

        // ① 回报进度 ⇒ 明细进入 running，任务总状态跟着变。
        var progress = await SendProgressAsync(c1, item1, 5_000, 10_000);
        progress.Ok.Should().BeTrue();
        progress.Status.Should().Be(DistributionItemStates.Running);

        (await admin.GetFromJsonAsync<DistributionTaskDto>($"/api/distributions/{task.Id}"))!
            .State.Should().Be(DistributionStates.Running);

        // ② 迟到的旧进度不能把已收字节改小。
        await SendProgressAsync(c1, item1, 2_000, 10_000);

        var afterStale = (await admin.GetFromJsonAsync<DistributionTaskDto>($"/api/distributions/{task.Id}"))!;
        afterStale.Items.Single(i => i.AgentId == a1).ReceivedBytes.Should().Be(5_000, "进度只增不减");
        afterStale.Items.Single(i => i.AgentId == a1).ProgressPct.Should().Be(50);

        // ③ 一台完成 ⇒ 任务还没结束（另一台 pending）。
        await SendCompleteAsync(c1, item1, ok: true, received: 10_000, sha: sha);

        var halfway = (await admin.GetFromJsonAsync<DistributionTaskDto>($"/api/distributions/{task.Id}"))!;
        halfway.State.Should().Be(DistributionStates.Pending, "还有一台没动");
        halfway.Succeeded.Should().Be(1);

        // ④ 另一台失败 ⇒ 任务变成 partial（有成有败，需要人看一眼）。
        await SendCompleteAsync(c2, item2, ok: false, received: 0, error: "目标目录不在本机白名单内");

        var done = (await admin.GetFromJsonAsync<DistributionTaskDto>($"/api/distributions/{task.Id}"))!;
        done.State.Should().Be(DistributionStates.Partial);
        done.Succeeded.Should().Be(1);
        done.Failed.Should().Be(1);
        done.Items.Single(i => i.AgentId == a2).Error.Should().Be("目标目录不在本机白名单内");

        // ⑤ 终结后补领不到它了（只剩另一台的…… 两台都终结了 ⇒ 空）。
        (await c1.GetFromJsonAsync<List<AgentTransferTaskDto>>("/api/agents/transfers"))!
            .Should().BeEmpty();
    }

    /// <summary>
    /// 🔥 归属校验：客户机只能动<b>自己那行</b>。
    /// <para>少了这一步，任何一台拿到 AgentKey 的机器都能把别人的传输标成"完成"或"失败"。</para>
    /// </summary>
    [Fact]
    public async Task An_agent_cannot_report_on_another_agents_item()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var (a1, c1) = await RegisterAgentAsync(factory, "machine-a");
        var (a2, c2) = await RegisterAgentAsync(factory, "machine-b");

        var (fileId, _) = await UploadFileAsync(admin, "pkg.bin", RandomBytes(2_048));

        var task = (await (await admin.PostAsJsonAsync(
                "/api/distributions", new DistributionCreateRequest(fileId, Targets(a1, a2))))
            .Content.ReadFromJsonAsync<DistributionTaskDto>())!;

        var item1 = task.Items.Single(i => i.AgentId == a1).Id;

        // 机器 B 去动机器 A 的条目 ⇒ 404（不是 403，免得泄露"这个 id 存在"）。
        (await c2.PostAsJsonAsync($"/api/agents/transfers/{item1}/progress", new AgentTransferProgressRequest(1, 1)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await c2.PostAsJsonAsync(
                $"/api/agents/transfers/{item1}/complete",
                new AgentTransferCompleteRequest(true, 2_048, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 机器 A 那条仍然是 pending，一点没被碰过。
        (await admin.GetFromJsonAsync<DistributionTaskDto>($"/api/distributions/{task.Id}"))!
            .Items.Single(i => i.AgentId == a1).State.Should().Be(DistributionItemStates.Pending);

        // 机器 A 自己动则成功。
        (await SendCompleteAsync(c1, item1, ok: true, received: 2_048)).Ok.Should().BeTrue();
    }

    /// <summary>重复上报"完成"不能把已完成翻成失败（客户端重连后重报是常态）。</summary>
    [Fact]
    public async Task Completion_is_idempotent()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var (agentId, agent) = await RegisterAgentAsync(factory, "machine-a");
        var (fileId, _) = await UploadFileAsync(admin, "pkg.bin", RandomBytes(1_024));

        var task = (await (await admin.PostAsJsonAsync(
                "/api/distributions", new DistributionCreateRequest(fileId, Targets(agentId))))
            .Content.ReadFromJsonAsync<DistributionTaskDto>())!;

        var itemId = task.Items.Single().Id;

        await SendCompleteAsync(agent, itemId, ok: true, received: 1_024);
        await SendCompleteAsync(agent, itemId, ok: false, received: 0, error: "重连后误报");

        var final = (await admin.GetFromJsonAsync<DistributionTaskDto>($"/api/distributions/{task.Id}"))!;
        final.Items.Single().State.Should().Be(DistributionItemStates.Completed, "终态一旦定下就不再改写");
        final.State.Should().Be(DistributionStates.Completed);
    }

    [Fact]
    public async Task Collect_tasks_have_an_unknown_size_until_the_agent_reports_it()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var (agentId, agent) = await RegisterAgentAsync(factory, "machine-a");

        var created = await admin.PostAsJsonAsync(
            "/api/distributions/collect",
            new CollectCreateRequest(new[] { agentId }, @"C:\logs\app.log", "排查用"));

        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        var task = (await created.Content.ReadFromJsonAsync<DistributionTaskDto>())!;
        task.Direction.Should().Be(DistributionDirections.Collect);
        task.FileName.Should().Be("app.log", "展示名只取最后一段");
        task.Items.Single().TotalBytes.Should().Be(0, "客户端还没量出源文件大小");

        // 客户机补领到的是"回传"指令，带源路径、没有摘要。
        var pending = (await agent.GetFromJsonAsync<List<AgentTransferTaskDto>>("/api/agents/transfers"))!;
        pending.Single().Direction.Should().Be(DistributionDirections.Collect);
        pending.Single().SourcePath.Should().Be(@"C:\logs\app.log");
        pending.Single().Sha256.Should().BeNull();

        // 客户端量出大小后回报 ⇒ 总大小回填、百分比随之出现。
        var itemId = task.Items.Single().Id;
        await SendProgressAsync(agent, itemId, 500, 2_000);

        var halfway = (await admin.GetFromJsonAsync<DistributionTaskDto>($"/api/distributions/{task.Id}"))!;
        halfway.Items.Single().TotalBytes.Should().Be(2_000);
        halfway.Items.Single().ProgressPct.Should().Be(25);
    }

    // ═══════════════════════════════════════════════════════════
    // 「记住的默认目录」与建单的关系（2026-10-15 修）
    //
    // 现场原话：「文件下发到客户机的路径无法保存」。两条链路各自都对，
    // 合起来却是"保存成功、然后马上被抹掉"：
    //   · 浏览器点「记为此机默认目录」 ⇒ PUT /api/agents/{id}/path-memory  ✅
    //   · 紧接着建了一次"存放路径留空"的下发 ⇒ 建单后的回写把记忆写成**空串**  ❌
    // 这里把"留空不该有删记忆的副作用"钉在接口上。
    // ═══════════════════════════════════════════════════════════

    /// <summary>调一次「记为此机默认目录」，返回这台机器**服务端记着**的路径。</summary>
    private static async Task<string?> RememberAndReadAsync(HttpClient admin, int agentId, string? targetDir)
    {
        var response = await admin.PutAsJsonAsync(
            $"/api/agents/{agentId}/path-memory", new AgentPathMemoryRequest(targetDir, null));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var result = (await response.Content.ReadFromJsonAsync<AgentMutationResult>())!;
        var host = result.List!.Items.Single(a => a.Id == agentId);

        return host.LastTargetDir;
    }

    private static async Task<string?> LastTargetDirAsync(HttpClient admin, int agentId)
    {
        var list = (await admin.GetFromJsonAsync<AgentListDto>("/api/agents"))!;

        return list.Items.Single(a => a.Id == agentId).LastTargetDir;
    }

    /// <summary>
    /// <b>存放路径留空的下发，不能把已记住的默认目录冲掉。</b>
    /// <para>
    /// 留空只表示"这一次不定目录"（落点由客户机的默认目录决定），
    /// 而"删掉记忆"是一个破坏性的、屏幕上完全看不见的副作用 —— 它正是
    /// "保存了路径却怎么都不生效"的成因。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_push_without_a_directory_does_not_wipe_the_remembered_one()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var (agentId, _) = await RegisterAgentAsync(factory, "machine-a");
        var (fileId, _) = await UploadFileAsync(admin, "pkg.bin", RandomBytes(512));

        // ① 运维在浏览面板里挑好了目录并「记为此机默认目录」。
        (await RememberAndReadAsync(admin, agentId, @"D:\播放内容")).Should().Be(@"D:\播放内容");

        // ② 紧接着建了一次存放路径留空的单（= 用客户机默认目录）。
        var created = await admin.PostAsJsonAsync(
            "/api/distributions", new DistributionCreateRequest(fileId, Targets(agentId)));

        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        var task = (await created.Content.ReadFromJsonAsync<DistributionTaskDto>())!;
        task.Items.Single().TargetDir.Should().BeNull("留空就是留空：落点由客户机自己决定");

        // ③ 记忆必须还在 —— 这一条就是本次修复的全部意义。
        (await LastTargetDirAsync(admin, agentId)).Should().Be(
            @"D:\播放内容", "一次留空的建单不该有删掉记忆的副作用");
    }

    /// <summary>
    /// 真指定了目录时，记忆要<b>跟着更新</b>（下一个任务就预填这个新目录）；
    /// 而且只动下发方向，不碰回传方向那一格。
    /// </summary>
    [Fact]
    public async Task A_push_with_a_directory_updates_the_remembered_one()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var (agentId, _) = await RegisterAgentAsync(factory, "machine-a");
        var (fileId, _) = await UploadFileAsync(admin, "pkg.bin", RandomBytes(512));

        await RememberAndReadAsync(admin, agentId, @"D:\播放内容");

        var created = await admin.PostAsJsonAsync(
            "/api/distributions",
            new DistributionCreateRequest(fileId, Dirs((agentId, @"E:\素材\2026"))));

        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        (await LastTargetDirAsync(admin, agentId)).Should().Be(
            @"E:\素材\2026", "指定了新目录 ⇒ 下次该预填新的");

        var list = (await admin.GetFromJsonAsync<AgentListDto>("/api/agents"))!;

        list.Items.Single(a => a.Id == agentId).LastSourceDir
            .Should().BeNull("下发方向不该顺手改回传方向那一格");
    }

    /// <summary>
    /// 回传方向反过来：源路径的父目录会被记住，并且<b>同样不动下发方向</b>。
    /// </summary>
    [Fact]
    public async Task A_collect_task_remembers_the_source_directory_only()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var (agentId, _) = await RegisterAgentAsync(factory, "machine-a");

        await RememberAndReadAsync(admin, agentId, @"D:\播放内容");

        var created = await admin.PostAsJsonAsync(
            "/api/distributions/collect",
            new CollectCreateRequest(new[] { agentId }, @"C:\logs\2026-10\app.log", null));

        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        var host = (await admin.GetFromJsonAsync<AgentListDto>("/api/agents"))!
            .Items.Single(a => a.Id == agentId);

        host.LastSourceDir.Should().Be(@"C:\logs\2026-10", "记的是目录，不是文件本身");
        host.LastTargetDir.Should().Be(@"D:\播放内容", "回传不该动下发方向的记忆");
    }

    [Fact]
    public async Task Anonymous_agents_are_rejected_from_the_transfer_endpoints()
    {
        var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        (await anonymous.GetAsync("/api/agents/transfers")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(20261012).NextBytes(bytes);
        return bytes;
    }
}
