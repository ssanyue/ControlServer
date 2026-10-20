using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.Contracts;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 指令库端点（M2-E3）的端到端测试 —— 真宿主 + 真 SQLite + 真 HTTP 管线。
/// <para>
/// 重点在三条**容易出事的语义**：
/// ① 名称为空 / 协议越界 / 重名必须是 <b>400 且不落库</b>；
/// ② 指令库是<b>独立文档</b>，改它不能动到 <c>settings</c> 的版本，也不能进配置导出；
/// ③ 动作以「指令库」引用时，执行链真的按指令库里的协议 / 数据发出去（不是只存不用）。
/// </para>
/// </summary>
public sealed class CommandsApiTests : IDisposable
{
    private readonly ControlServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin123"));
        response.StatusCode.Should().Be(HttpStatusCode.OK, "默认管理员必须能登录");
        return client;
    }

    private static object Body(
        string commandJson,
        string? updatedBy = "integration-test")
        => new
        {
            command = JsonSerializer.Deserialize<JsonElement>(commandJson),
            updatedBy,
        };

    private static string Command(
        string name,
        string protocol = "UDP",
        string dataType = "UTF-8",
        string data = "ON",
        string? id = null)
        => JsonSerializer.Serialize(new { id, name, protocol, dataType, data });

    // ═══════════════════════════════════════════════════════════
    // 鉴权
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Commands_endpoint_rejects_anonymous()
    {
        var client = _factory.CreateClient();

        (await client.GetAsync("/api/commands")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/commands", Body(Command("开灯")))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    // ═══════════════════════════════════════════════════════════
    // 读
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Fresh_database_returns_an_empty_library()
    {
        var client = await LoginAsync();

        var list = await client.GetFromJsonAsync<CommandListDto>("/api/commands");

        list.Should().NotBeNull();
        list!.Count.Should().Be(0);
        list.Version.Should().Be(0, "首次读取不落盘，版本保持 0");
        list.Commands.ValueKind.Should().Be(JsonValueKind.Array);
        list.Commands.GetArrayLength().Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════
    // 增
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_then_list_round_trips_over_http()
    {
        var client = await LoginAsync();

        var created = await client.PostAsJsonAsync(
            "/api/commands", Body(Command("开灯", protocol: "TCP", dataType: "Hex", data: "41 42")));

        created.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await created.Content.ReadFromJsonAsync<CommandMutationDto>();
        dto!.Ok.Should().BeTrue(dto.Message);
        dto.Status.Should().Be("Ok");
        dto.Version.Should().Be(1);
        dto.Commands.GetArrayLength().Should().Be(1);
        dto.Command.Should().NotBeNull();
        dto.Command!.Value.GetProperty("name").GetString().Should().Be("开灯");
        dto.Command.Value.GetProperty("protocol").GetString().Should().Be("TCP");

        var list = await client.GetFromJsonAsync<CommandListDto>("/api/commands");
        list!.Count.Should().Be(1);
        list.Version.Should().Be(1);
        list.Commands[0].GetProperty("data").GetString().Should().Be("41 42");
    }

    [Fact]
    public async Task Create_rejects_a_duplicate_name_and_returns_the_current_library()
    {
        var client = await LoginAsync();
        await client.PostAsJsonAsync("/api/commands", Body(Command("开灯")));

        // 只差大小写 —— 指令名唯一性是**大小写不敏感**的（与 ResolveAsync 同一口径）。
        var dup = await client.PostAsJsonAsync("/api/commands", Body(Command("开灯")));

        dup.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var dto = await dup.Content.ReadFromJsonAsync<CommandMutationDto>();
        dto!.Ok.Should().BeFalse();
        dto.Status.Should().Be("DuplicateName");
        dto.ConflictId.Should().NotBeNullOrWhiteSpace("前端要靠它高亮冲突的那一条");
        dto.Commands.GetArrayLength().Should().Be(1, "被拒的请求不能落库");
        dto.Version.Should().Be(1, "版本保持不动");
    }

    [Theory]
    [InlineData("   ", "UDP")]        // 名称空白
    [InlineData("合法名", "WoL")]      // 指令库不含 WoL
    public async Task Create_rejects_invalid_payloads(string name, string protocol)
    {
        var client = await LoginAsync();

        var response = await client.PostAsJsonAsync("/api/commands", Body(Command(name, protocol: protocol)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var dto = await response.Content.ReadFromJsonAsync<CommandMutationDto>();
        dto!.Status.Should().Be("Invalid");
        dto.Message.Should().NotBeNullOrWhiteSpace("必须告诉用户哪里不合法");
    }

    // ═══════════════════════════════════════════════════════════
    // 改 / 删
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_replaces_the_command_and_bumps_the_version()
    {
        var client = await LoginAsync();

        var created = await client.PostAsJsonAsync("/api/commands", Body(Command("旧名", data: "OLD")));
        var id = (await created.Content.ReadFromJsonAsync<CommandMutationDto>())!
            .Command!.Value.GetProperty("id").GetString()!;

        var updated = await client.PutAsJsonAsync(
            $"/api/commands/{id}", Body(Command("新名", protocol: "TCP", dataType: "Hex", data: "41")));

        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await updated.Content.ReadFromJsonAsync<CommandMutationDto>();
        dto!.Ok.Should().BeTrue(dto.Message);
        dto.Version.Should().Be(2);
        dto.Commands.GetArrayLength().Should().Be(1, "改的是同一条，不是新增");
        dto.Command!.Value.GetProperty("id").GetString().Should().Be(id);
        dto.Command.Value.GetProperty("name").GetString().Should().Be("新名");
        dto.Command.Value.GetProperty("protocol").GetString().Should().Be("TCP");
    }

    [Fact]
    public async Task Update_returns_404_for_an_unknown_id()
    {
        var client = await LoginAsync();
        await client.PostAsJsonAsync("/api/commands", Body(Command("开灯")));

        var response = await client.PutAsJsonAsync("/api/commands/不存在的id", Body(Command("开灯")));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<CommandMutationDto>())!.Status.Should().Be("NotFound");
    }

    [Fact]
    public async Task Delete_removes_the_command()
    {
        var client = await LoginAsync();

        var a = await client.PostAsJsonAsync("/api/commands", Body(Command("甲")));
        var id = (await a.Content.ReadFromJsonAsync<CommandMutationDto>())!
            .Command!.Value.GetProperty("id").GetString()!;
        await client.PostAsJsonAsync("/api/commands", Body(Command("乙")));

        var deleted = await client.DeleteAsync($"/api/commands/{id}");

        deleted.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await deleted.Content.ReadFromJsonAsync<CommandMutationDto>();
        dto!.Ok.Should().BeTrue(dto.Message);
        dto.Commands.GetArrayLength().Should().Be(1);

        var list = await client.GetFromJsonAsync<CommandListDto>("/api/commands");
        list!.Count.Should().Be(1);
        list.Commands[0].GetProperty("name").GetString().Should().Be("乙");
    }

    [Fact]
    public async Task Delete_returns_404_for_an_unknown_id()
    {
        var client = await LoginAsync();

        var response = await client.DeleteAsync("/api/commands/nope");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ═══════════════════════════════════════════════════════════
    // 与配置的关系
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Command_library_is_a_separate_document_from_settings()
    {
        var client = await LoginAsync();

        var before = await client.GetFromJsonAsync<ConfigViewDto>("/api/config");

        await client.PostAsJsonAsync("/api/commands", Body(Command("开灯")));
        await client.PostAsJsonAsync("/api/commands", Body(Command("关灯")));

        var after = await client.GetFromJsonAsync<ConfigViewDto>("/api/config");

        after!.Version.Should().Be(before!.Version, "改指令库绝不能推动配置版本 —— 否则编辑页会误报冲突");
    }

    [Fact]
    public async Task Command_library_is_not_part_of_the_config_export()
    {
        var client = await LoginAsync();

        // 造一份非空配置（导出要求有内容才不会触发"空配置"提示），再加一条独特的指令。
        await client.PostAsJsonAsync(
            "/api/config/import",
            new
            {
                content = """
                { "version": "1.0",
                  "settings": { "appName": "指令库隔离测试", "buttons": [ { "id": "b1", "label": "开灯" } ], "subButtonSets": [] },
                  "images": {} }
                """,
                expectedVersion = 0,
            });

        await client.PostAsJsonAsync("/api/commands", Body(Command("独一无二的指令名")));

        var export = await client.GetFromJsonAsync<ConfigExportDto>("/api/config/export?format=json");

        export!.Content.Should().NotContain("独一无二的指令名", "指令库与旧版一致，不参与配置导入导出");
    }

    // ═══════════════════════════════════════════════════════════
    // 执行链贯通：动作以「指令库」引用时真的按指令库发
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task An_action_can_reference_the_command_library_and_sends_its_payload()
    {
        var client = await LoginAsync();

        await client.PostAsJsonAsync(
            "/api/commands",
            Body(Command("灯开", protocol: "UDP", dataType: "UTF-8", data: "CMD-PAYLOAD")));

        var exec = await client.PostAsJsonAsync("/api/actions/execute", new
        {
            label = "引用指令库的按钮",
            actions = new[]
            {
                new
                {
                    name = "按下",
                    targetIp = "127.0.0.1",
                    targetPort = 59997,
                    protocol = "UDP",
                    dataType = "指令库",
                    dataContent = "灯开",
                },
            },
        });

        exec.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await client.GetFromJsonAsync<List<TrafficLogDto>>("/api/traffic/recent");
        items.Should().ContainSingle();

        var entry = items![0];
        entry.Success.Should().BeTrue();
        entry.Data.Should().Be("CMD-PAYLOAD", "发出去的必须是指令库里存的数据，而不是「灯开」这个名字");
        entry.ButtonName.Should().Be("引用指令库的按钮");
    }

    [Fact]
    public async Task An_action_referencing_a_missing_command_fails_without_killing_the_run()
    {
        var client = await LoginAsync();

        var exec = await client.PostAsJsonAsync("/api/actions/execute", new
        {
            label = "引用不存在的指令",
            actions = new[]
            {
                new
                {
                    targetIp = "127.0.0.1",
                    targetPort = 59996,
                    protocol = "UDP",
                    dataType = "指令库",
                    dataContent = "根本没有这条",
                },
            },
        });

        exec.StatusCode.Should().Be(HttpStatusCode.OK, "单条失败不该让整个请求失败（与旧版一致）");

        var payload = await exec.Content.ReadAsStringAsync();
        payload.Should().Contain("失败", "结果里要明确这条没发出去");
    }
}
