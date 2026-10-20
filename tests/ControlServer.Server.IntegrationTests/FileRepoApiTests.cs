using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ControlServer.Contracts;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 文件仓库端点的集成测试（M4.4）：分块上传 / 列表 / Range 下载 / 删除 / 权限。
///
/// <para>
/// 重点是那些<b>"接口看着对、字节其实不对"</b>的地方：
/// ① 偏移不符真的回 409 + 真实进度（不是 400）；
/// ② 摘要是服务端自己算的（不信客户端自报）；
/// ③ 摘要不符时文件<b>不入库</b>；
/// ④ Range 真的返回 206 且内容正确（断点续传的地基）；
/// ⑤ 两种身份（人 / 机器）各自能做什么、不能做什么。
/// </para>
/// </summary>
public sealed class FileRepoApiTests : IDisposable
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

    /// <summary>免凭据注册一台客户机（匿名端点），返回它的 Id 与 AgentKey。</summary>
    private static async Task<(int Id, string Key)> RegisterAgentAsync(
        ControlServerFactory factory,
        string machineKey = "machine-under-test")
    {
        var anonymous = factory.CreateClient();

        var registered = await anonymous.PostAsJsonAsync(
            "/api/agents/register", new AgentRegisterRequest(HostInfo(machineKey)));

        registered.StatusCode.Should().Be(HttpStatusCode.OK, await registered.Content.ReadAsStringAsync());

        var result = await registered.Content.ReadFromJsonAsync<AgentRegisterResult>();

        return (result!.AgentId!.Value, result.AgentKey!);
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(20261012).NextBytes(bytes);
        return bytes;
    }

    private static string Sha256Of(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static async Task<FileUploadSessionDto> CreateUploadAsync(
        HttpClient client,
        string fileName,
        long size,
        string? sha = null,
        string? kind = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/files/uploads", new FileUploadCreateRequest(fileName, size, kind, sha));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<FileUploadSessionDto>())!;
    }

    private static Task<HttpResponseMessage> PutChunkAsync(HttpClient client, string uploadId, long offset, byte[] data)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        return client.PutAsync($"/api/files/uploads/{uploadId}?offset={offset}", content);
    }

    /// <summary>上传一个文件并返回它的仓库 id（走完整的分块协议）。</summary>
    private static async Task<(int Id, string Sha)> UploadFileAsync(HttpClient client, string name, byte[] payload)
    {
        var sha = Sha256Of(payload);
        var session = await CreateUploadAsync(client, name, payload.Length, sha);

        var offset = 0;
        const int chunk = 16 * 1024;

        while (offset < payload.Length)
        {
            var piece = payload[offset..Math.Min(offset + chunk, payload.Length)];

            var response = await PutChunkAsync(client, session.UploadId, offset, piece);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("receivedBytes").GetInt64().Should().Be(offset + piece.Length);

            offset += piece.Length;
        }

        var completed = await client.PostAsJsonAsync(
            $"/api/files/uploads/{session.UploadId}/complete", new FileUploadCompleteRequest());

        completed.StatusCode.Should().Be(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync());

        var result = (await completed.Content.ReadFromJsonAsync<FileUploadResultDto>())!;

        return (result.Id, result.Sha256);
    }

    // ═══════════════════════════════════════════════════════════
    // 主流程
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 全流程：分块上传（含一次故意错偏移）→ 中途查进度 → 完成 → 列表 → Range 下载 → 全量下载 → 删除。
    /// </summary>
    [Fact]
    public async Task Chunked_upload_survives_an_offset_mismatch_and_downloads_with_range()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var payload = RandomBytes(50_000);
        var sha = Sha256Of(payload);

        var session = await CreateUploadAsync(admin, "payload.bin", payload.Length, sha);
        session.ReceivedBytes.Should().Be(0, "刚创建的会话一个字节都没收");
        session.ChunkSizeBytes.Should().BeGreaterThan(0, "服务端把建议块大小告诉客户端，避免两边各拍一个数");

        // ① 故意先发一个错误偏移：服务端应回 409 + 真实进度（这是续传的对齐信号，不是错误）
        var mismatch = await PutChunkAsync(admin, session.UploadId, 999_999, payload[..16]);
        mismatch.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await mismatch.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("receivedBytes").GetInt64().Should().Be(0);

        // ② 正式分块上传
        const int chunk = 20_000;
        for (var offset = 0; offset < payload.Length; offset += chunk)
        {
            var piece = payload[offset..Math.Min(offset + chunk, payload.Length)];

            var response = await PutChunkAsync(admin, session.UploadId, offset, piece);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

            (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("receivedBytes").GetInt64().Should().Be(offset + piece.Length);
        }

        // ③ 中途问一次进度（断线重连就是靠它知道"收到哪了"）
        var status = await admin.GetFromJsonAsync<FileUploadSessionDto>($"/api/files/uploads/{session.UploadId}");
        status!.ReceivedBytes.Should().Be(payload.Length);
        status.State.Should().Be(FileUploadStates.Open);

        // ④ 完成
        var completed = await admin.PostAsJsonAsync(
            $"/api/files/uploads/{session.UploadId}/complete", new FileUploadCompleteRequest());

        completed.StatusCode.Should().Be(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync());

        var result = (await completed.Content.ReadFromJsonAsync<FileUploadResultDto>())!;
        result.Size.Should().Be(payload.Length);
        result.Sha256.Should().Be(sha, "摘要是服务端自己算的，与本地算的一致");
        result.Kind.Should().Be(FileKinds.Normal, "用户上传默认是普通文件");

        // 完成即注销会话 —— 它没有"已完成"状态。
        (await admin.GetAsync($"/api/files/uploads/{session.UploadId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        // ⑤ 列表
        var list = (await admin.GetFromJsonAsync<FileRepoListDto>("/api/files"))!;
        list.Total.Should().Be(1);
        list.TotalBytes.Should().Be(payload.Length);
        list.Items.Single().Id.Should().Be(result.Id);
        list.Items.Single().Sha256.Should().Be(sha);

        // ⑥ Range 下载：只要最后 100 字节 —— 206 + 正确的内容（断点续传的地基）
        var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/files/{result.Id}/download");
        rangeRequest.Headers.Range = new RangeHeaderValue(payload.Length - 100, null);

        var ranged = await admin.SendAsync(rangeRequest);
        ranged.StatusCode.Should().Be(HttpStatusCode.PartialContent, "带 Range 的下载必须回 206，否则续传无从谈起");
        ranged.Content.Headers.ContentRange.Should().NotBeNull();
        (await ranged.Content.ReadAsByteArrayAsync()).Should().Equal(payload[^100..]);

        // 全量下载内容一致
        (await admin.GetByteArrayAsync($"/api/files/{result.Id}/download")).Should().Equal(payload);

        // ⑦ 删除（两段式确认由前端做，端点本身只认 id）
        (await admin.DeleteAsync($"/api/files/{result.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.GetFromJsonAsync<FileRepoListDto>("/api/files"))!.Total.Should().Be(0);
        (await admin.GetAsync($"/api/files/{result.Id}/download")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "删了就是真的没了（连着磁盘文件一起）");
    }

    [Fact]
    public async Task Completing_before_all_bytes_arrive_is_rejected()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var payload = RandomBytes(4_000);
        var session = await CreateUploadAsync(admin, "half.bin", payload.Length, Sha256Of(payload));

        (await PutChunkAsync(admin, session.UploadId, 0, payload[..1_000])).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var completed = await admin.PostAsJsonAsync(
            $"/api/files/uploads/{session.UploadId}/complete", new FileUploadCompleteRequest());

        completed.StatusCode.Should().Be(HttpStatusCode.Conflict, "没传完就完成必须先被拦下");
        (await completed.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("receivedBytes").GetInt64().Should().Be(1_000, "要把真实进度回给客户端");

        (await admin.GetFromJsonAsync<FileRepoListDto>("/api/files"))!.Total.Should().Be(0);
    }

    [Fact]
    public async Task A_file_whose_hash_does_not_match_is_never_stored()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var payload = RandomBytes(2_048);

        // 声明一个必然错误的摘要。
        var session = await CreateUploadAsync(admin, "corrupt.bin", payload.Length, new string('0', 64));

        (await PutChunkAsync(admin, session.UploadId, 0, payload)).StatusCode.Should().Be(HttpStatusCode.OK);

        var completed = await admin.PostAsJsonAsync(
            $"/api/files/uploads/{session.UploadId}/complete", new FileUploadCompleteRequest());

        completed.StatusCode.Should().Be(HttpStatusCode.Conflict, "内容校验失败必须是 409（可重传），不是 400");
        (await admin.GetFromJsonAsync<FileRepoListDto>("/api/files"))!.Total.Should().Be(0);
    }

    /// <summary>
    /// 上传会话在磁盘上留下的是 <c>.part</c>；没完成的会话不该出现在仓库列表里
    /// （它连"文件"都还算不上）。
    /// </summary>
    [Fact]
    public async Task An_unfinished_upload_does_not_show_up_in_the_repository()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var session = await CreateUploadAsync(admin, "inflight.bin", 1_000, null);
        (await PutChunkAsync(admin, session.UploadId, 0, RandomBytes(500))).StatusCode
            .Should().Be(HttpStatusCode.OK);

        (await admin.GetFromJsonAsync<FileRepoListDto>("/api/files"))!.Total.Should().Be(0);

        // 主动中止后，会话与 .part 都该消失。
        (await admin.DeleteAsync($"/api/files/uploads/{session.UploadId}")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        (await admin.GetAsync($"/api/files/uploads/{session.UploadId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    // ═══════════════════════════════════════════════════════════
    // 权限：两种身份
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Anonymous_cannot_touch_the_repository()
    {
        var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        (await anonymous.GetAsync("/api/files")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/files/uploads", new FileUploadCreateRequest("x.bin", 10)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_bogus_agent_key_is_not_a_way_in()
    {
        var factory = NewFactory();
        using var impostor = factory.CreateClient();
        impostor.DefaultRequestHeaders.Add(AgentAuthentication.HeaderName, "not-a-real-key");

        (await impostor.PostAsJsonAsync("/api/files/uploads", new FileUploadCreateRequest("x.bin", 10)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Viewer 读不了仓库（<see cref="Policies.CanOperate"/> / <see cref="Policies.FileRead"/> 都不含它）；
    /// Operator 能读能传，但<b>不能删</b>（删除是 AdminOnly）。
    /// </summary>
    [Fact]
    public async Task Viewer_cannot_read_and_operator_cannot_delete()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var (fileId, _) = await UploadFileAsync(admin, "shared.bin", RandomBytes(1_024));

        var viewer = await CreateUserAndLoginAsync(factory, admin, "looker", Roles.Viewer);

        (await viewer.GetAsync("/api/files")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "Viewer 不在 CanOperate 里");
        (await viewer.GetAsync($"/api/files/{fileId}/download")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "下载也不给 Viewer");

        var op = await CreateUserAndLoginAsync(factory, admin, "operator1", Roles.Operator);

        (await op.GetAsync($"/api/files/{fileId}/download")).StatusCode
            .Should().Be(HttpStatusCode.OK, "Operator 能读");
        (await op.PostAsJsonAsync("/api/files/uploads", new FileUploadCreateRequest("op.bin", 10))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "FileIngest 不含 Operator —— 能写仓库就等于能把任意内容推给所有客户机");
        (await op.DeleteAsync($"/api/files/{fileId}")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "删除是 AdminOnly");
    }

    /// <summary>
    /// 🔥 客户机用 AgentKey 上传（回传）：<b>落库类别被强制成 <c>collected</c></b>，
    /// 并记下来源机器 —— 不让客户端自称 kind（否则它能把回传伪装成 installer / normal）。
    /// </summary>
    [Fact]
    public async Task An_agent_upload_lands_as_collected_and_records_its_origin()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory);

        var (agentId, key) = await RegisterAgentAsync(factory);

        using var agent = factory.CreateClient();
        agent.DefaultRequestHeaders.Add(AgentAuthentication.HeaderName, key);

        var payload = RandomBytes(3_000);
        var sha = Sha256Of(payload);

        // 客户机自报 kind=installer —— 服务端必须无视它。
        var session = await CreateUploadAsync(agent, "field.log", payload.Length, sha, kind: FileKinds.Installer);
        session.Kind.Should().Be(FileKinds.Collected, "客户机发起的一律是回传");

        (await PutChunkAsync(agent, session.UploadId, 0, payload)).StatusCode.Should().Be(HttpStatusCode.OK);

        var completed = await agent.PostAsJsonAsync(
            $"/api/files/uploads/{session.UploadId}/complete", new FileUploadCompleteRequest());

        completed.StatusCode.Should().Be(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync());

        var item = (await admin.GetFromJsonAsync<FileRepoListDto>("/api/files"))!.Items.Single();
        item.Kind.Should().Be(FileKinds.Collected);
        item.OriginAgentId.Should().Be(agentId, "回传要记下来源机器");
        item.UploadedBy.Should().NotBeNullOrWhiteSpace("来源机器的显示名会被解析出来");
        item.Sha256.Should().Be(sha);

        // 按类别过滤也只出现在 collected 里。
        (await admin.GetFromJsonAsync<FileRepoListDto>($"/api/files?kind={FileKinds.Collected}"))!.Total
            .Should().Be(1);
        (await admin.GetFromJsonAsync<FileRepoListDto>($"/api/files?kind={FileKinds.Normal}"))!.Total
            .Should().Be(0);
    }
}
