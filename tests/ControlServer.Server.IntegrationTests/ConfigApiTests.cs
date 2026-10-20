using System.Net;
using System.Net.Http.Json;
using ControlServer.Contracts;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 配置与日志端点（M2-F）的端到端测试 —— 真宿主 + 真 SQLite + 真 HTTP 管线。
/// <para>
/// 重点不是"接口能返回 200"，而是三条**容易出事的语义**：
/// ① 未登录一律 401；② 乐观锁冲突必须 409 且带着服务端当前配置；
/// ③ 导入的**安全边界**（非法内容被拒、空内容不能覆盖非空配置、图片端点挡目录穿越）。
/// </para>
/// </summary>
public sealed class ConfigApiTests : IDisposable
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

    // ═══════════════════════════════════════════════════════════
    // 鉴权
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("/api/config")]
    [InlineData("/api/config/export")]
    [InlineData("/api/traffic/recent")]
    [InlineData("/api/traffic/count")]
    public async Task Config_and_traffic_endpoints_reject_anonymous(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ═══════════════════════════════════════════════════════════
    // 读：全新库
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Fresh_database_returns_empty_default_config()
    {
        var client = await LoginAsync();

        var config = await client.GetFromJsonAsync<ConfigViewDto>("/api/config");

        config.Should().NotBeNull();
        config!.Version.Should().Be(0, "首次读取不落盘，版本保持 0");
        config.State.Should().Be("Missing");
        config.IsEmpty.Should().BeTrue();
        config.Counts.Should().Be(new ConfigCountsDto(0, 0, 0));
        config.Settings.GetProperty("appName").GetString().Should().Be("中控");
    }

    [Fact]
    public async Task Fresh_database_traffic_buffer_is_empty()
    {
        var client = await LoginAsync();

        var items = await client.GetFromJsonAsync<List<TrafficLogDto>>("/api/traffic/recent");

        items.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════
    // 写：乐观锁
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Put_with_matching_version_succeeds_and_bumps_version()
    {
        var client = await LoginAsync();

        var put = await client.PutAsJsonAsync(
            "/api/config",
            Body(DesktopStyleSettings(buttonCount: 2), expectedVersion: 0));

        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await put.Content.ReadFromJsonAsync<ConfigViewDto>();
        updated!.Version.Should().Be(1);
        updated.Counts.Buttons.Should().Be(2);
        updated.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task Put_with_stale_version_returns_409_and_the_current_config()
    {
        var client = await LoginAsync();

        // 第一次保存成功（版本 0 → 1），模拟"另一个会话先改了配置"。
        var first = await client.PutAsJsonAsync(
            "/api/config",
            Body(DesktopStyleSettings(buttonCount: 2), expectedVersion: 0));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // 第二次仍然带旧版本 0 → 必须冲突。
        var second = await client.PutAsJsonAsync(
            "/api/config",
            Body(DesktopStyleSettings(buttonCount: 5), expectedVersion: 0));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var payload = await second.Content.ReadFromJsonAsync<ConflictProbe>();
        payload.Should().NotBeNull();
        payload!.Conflict.Should().BeTrue();
        payload.ActualVersion.Should().Be(1);
        payload.Current.Should().NotBeNull();
        payload.Current!.Counts.Buttons.Should().Be(2, "409 必须带回服务端**当前**配置，前端才能立刻对齐");
    }

    [Fact]
    public async Task Put_with_null_version_forces_overwrite()
    {
        var client = await LoginAsync();

        await client.PutAsJsonAsync("/api/config", Body(DesktopStyleSettings(2), expectedVersion: 0));

        var forced = await client.PutAsJsonAsync("/api/config", Body(DesktopStyleSettings(7), expectedVersion: null));

        forced.StatusCode.Should().Be(HttpStatusCode.OK);
        (await forced.Content.ReadFromJsonAsync<ConfigViewDto>())!.Counts.Buttons.Should().Be(7);
    }

    [Fact]
    public async Task Put_rejects_non_object_settings()
    {
        var client = await LoginAsync();

        var response = await client.PutAsJsonAsync(
            "/api/config",
            new { settings = "不是对象", expectedVersion = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ═══════════════════════════════════════════════════════════
    // 导出 / 导入
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Import_accepts_desktop_controlbak_json_and_reports_ignored_fields()
    {
        var client = await LoginAsync();

        // 旧版「导出配置」的样子：明文 JSON + 一堆服务版没有的字段。
        const string controlbak = """
        {
          "version": "1.0",
          "settings": {
            "appName": "展厅中控",
            "listenPort": 9999,
            "windowWidth": 1280,
            "lockWindow": true,
            "userPasswordHash": "DEADBEEF",
            "buttons": [
              { "id": "b1", "order": 0, "label": "开灯", "pairingCode": "LIGHT-ON", "buttonColor": "#512DA8", "actions": [] },
              { "id": "b2", "order": 1, "label": "关灯", "pairingCode": "LIGHT-OFF", "actions": [] }
            ],
            "subButtonSets": [
              { "id": "s1", "order": 2, "label": "机房", "targetIp": "10.0.0.9", "targetPort": 5000,
                "subButtons": [ { "id": "sub1", "label": "重启", "actions": [] } ] }
            ]
          },
          "images": {}
        }
        """;

        var response = await client.PostAsJsonAsync(
            "/api/config/import",
            new { content = controlbak, expectedVersion = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ConfigImportResultDto>();
        result!.Ok.Should().BeTrue(result.Message);
        result.Counts.Should().Be(new ConfigCountsDto(2, 1, 1));
        result.IgnoredFields.Should().Equal("lockWindow", "userPasswordHash", "windowWidth");

        // 真正落盘了吗 —— 再读一次确认。
        var config = await client.GetFromJsonAsync<ConfigViewDto>("/api/config");
        config!.Counts.Buttons.Should().Be(2);
        config.Settings.GetProperty("buttons")[0].GetProperty("label").GetString().Should().Be("开灯");
    }

    [Fact]
    public async Task Export_then_import_round_trip_over_http()
    {
        var client = await LoginAsync();

        // 先用导入造出非空配置，再导出成 base64，最后把它导回（版本强制覆盖）。
        await client.PostAsJsonAsync(
            "/api/config/import",
            new { content = DesktopStyleBackup(3), expectedVersion = 0 });

        var export = await client.GetFromJsonAsync<ConfigExportDto>("/api/config/export?format=base64");
        export.Should().NotBeNull();
        export!.Encoding.Should().Be("base64");
        export.Content.Should().NotContain("{", "base64 里不应出现裸 JSON 的括号");
        export.FileName.Should().EndWith(".controlbak");

        var reimport = await client.PostAsJsonAsync(
            "/api/config/import",
            new { content = export.Content, expectedVersion = export.Version });

        reimport.StatusCode.Should().Be(HttpStatusCode.OK);
        (await reimport.Content.ReadFromJsonAsync<ConfigImportResultDto>())!.Counts.Buttons.Should().Be(3);
    }

    [Fact]
    public async Task Export_can_produce_plain_json()
    {
        var client = await LoginAsync();
        await client.PostAsJsonAsync("/api/config/import", new { content = DesktopStyleBackup(1), expectedVersion = 0 });

        var export = await client.GetFromJsonAsync<ConfigExportDto>("/api/config/export?format=json");

        export!.Encoding.Should().Be("json");
        export.Content.Should().Contain("\"settings\"");
    }

    [Theory]
    [InlineData("")]
    [InlineData("这不是配置")]
    [InlineData("{\"hello\":\"world\"}")]
    [InlineData("YWJj")]
    public async Task Import_rejects_content_that_is_not_a_config(string content)
    {
        var client = await LoginAsync();

        var response = await client.PostAsJsonAsync(
            "/api/config/import",
            new { content, expectedVersion = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<ConfigImportResultDto>();
        result!.Ok.Should().BeFalse();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Import_of_empty_config_over_nonempty_one_is_refused()
    {
        var client = await LoginAsync();

        // 先建立非空配置。
        await client.PostAsJsonAsync("/api/config/import", new { content = DesktopStyleBackup(4), expectedVersion = 0 });
        var before = await client.GetFromJsonAsync<ConfigViewDto>("/api/config");

        // 再尝试导入一份"没有按钮也没有子按钮集"的合法配置。
        const string emptyButValid = """{ "appName": "空配置", "buttons": [], "subButtonSets": [] }""";
        var response = await client.PostAsJsonAsync(
            "/api/config/import",
            new { content = emptyButValid, expectedVersion = before!.Version });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<ConfigImportResultDto>();
        result!.Message.Should().Contain("恢复出厂", "必须明确告诉用户该走哪条路");

        // 关键断言：现有配置**一点没被改动**。
        var after = await client.GetFromJsonAsync<ConfigViewDto>("/api/config");
        after!.Counts.Buttons.Should().Be(4);
        after.Settings.GetProperty("appName").GetString().Should().NotBe("空配置");
    }

    // ═══════════════════════════════════════════════════════════
    // 图片
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Imported_image_can_be_fetched_through_the_image_endpoint()
    {
        var client = await LoginAsync();

        var pixels = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var base64 = Convert.ToBase64String(pixels);

        var backup = $$"""
        {
          "version": "1.0",
          "settings": {
            "appName": "带图",
            "defaultButtonImage": "img0.png",
            "buttons": [ { "id": "b1", "label": "开灯", "buttonImage": "img0.png", "actions": [] } ],
            "subButtonSets": []
          },
          "images": { "img0.png": "{{base64}}" }
        }
        """;

        var import = await client.PostAsJsonAsync("/api/config/import", new { content = backup, expectedVersion = 0 });
        import.StatusCode.Should().Be(HttpStatusCode.OK);
        (await import.Content.ReadFromJsonAsync<ConfigImportResultDto>())!.ImageCount.Should().Be(1);

        var image = await client.GetAsync("/api/config/images/img0.png");

        image.StatusCode.Should().Be(HttpStatusCode.OK);
        (await image.Content.ReadAsByteArrayAsync()).Should().Equal(pixels);
    }

    [Fact]
    public async Task Image_endpoint_refuses_path_traversal_and_unknown_files()
    {
        var client = await LoginAsync();

        // 注意这里刻意用 %5C（反斜杠）而不是 %2F：编码的斜杠会被路由提前当成路径分隔符处理，
        // 那样测到的是路由行为，而不是我们这道「文件名白名单 + 目录归属」的闸门。
        foreach (var name in new[] { "appsettings.json", "..%5C..%5Cappsettings.json", "control.db", "nope.png" })
        {
            var response = await client.GetAsync($"/api/config/images/{name}");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, "「{0}」不该被服务出去", name);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 收发日志
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Executing_actions_records_a_send_entry_in_the_traffic_buffer()
    {
        var client = await LoginAsync();

        var exec = await client.PostAsJsonAsync("/api/actions/execute", new
        {
            label = "集成测试按钮",
            actions = new[]
            {
                new { name = "发一包", targetIp = "127.0.0.1", targetPort = 59999, protocol = "UDP", dataType = "UTF-8", dataContent = "IT-TRAFFIC" },
            },
        });
        exec.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await client.GetFromJsonAsync<List<TrafficLogDto>>("/api/traffic/recent");

        items.Should().ContainSingle();
        var entry = items![0];
        entry.Direction.Should().Be("发送");
        entry.Protocol.Should().Be("UDP");
        entry.TargetIp.Should().Be("127.0.0.1");
        entry.Port.Should().Be(59999);
        entry.Data.Should().Be("IT-TRAFFIC");
        entry.ButtonName.Should().Be("集成测试按钮");
        entry.ActionName.Should().Be("发一包");
        entry.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Clearing_traffic_empties_the_buffer()
    {
        var client = await LoginAsync();

        await client.PostAsJsonAsync("/api/actions/execute", new
        {
            label = "temp",
            actions = new[] { new { targetIp = "127.0.0.1", targetPort = 59998, protocol = "UDP", dataContent = "X" } },
        });

        var delete = await client.DeleteAsync("/api/traffic");
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.GetFromJsonAsync<List<TrafficLogDto>>("/api/traffic/recent")).Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════
    // 恢复出厂
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Reset_factory_clears_buttons_and_restores_identity_defaults()
    {
        var client = await LoginAsync();

        await client.PostAsJsonAsync("/api/config/import", new { content = DesktopStyleBackup(3), expectedVersion = 0 });
        var before = await client.GetFromJsonAsync<ConfigViewDto>("/api/config");

        var reset = await client.PostAsJsonAsync("/api/config/reset", new { expectedVersion = before!.Version });
        reset.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await reset.Content.ReadFromJsonAsync<ConfigViewDto>();
        after!.Counts.Should().Be(new ConfigCountsDto(0, 0, 0));
        after.IsEmpty.Should().BeTrue();
        after.Settings.GetProperty("appName").GetString().Should().Be("中控");
        after.Settings.GetProperty("listenPort").GetInt32().Should().Be(9999);
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    private static object Body(string settingsJson, int? expectedVersion)
        => new
        {
            // 用 Deserialize<JsonElement> 而不是 JsonDocument.Parse：
            // 后者返回 IDisposable 的 document，不释放会让代码看起来"泄漏了"。
            settings = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(settingsJson),
            expectedVersion,
            updatedBy = "integration-test",
        };

    private static string DesktopStyleSettings(int buttonCount)
    {
        var buttons = string.Join(
            ",",
            Enumerable.Range(0, buttonCount).Select(i => $$"""{ "id": "b{{i}}", "order": {{i}}, "label": "按钮{{i}}" }"""));

        return $$"""{ "appName": "测试配置", "buttons": [{{buttons}}], "subButtonSets": [] }""";
    }

    private static string DesktopStyleBackup(int buttonCount)
        => $$"""
        {
          "version": "1.0",
          "settings": {{DesktopStyleSettings(buttonCount)}},
          "images": {}
        }
        """;

    /// <summary>409 响应体的探针（只取断言需要的字段）。</summary>
    private sealed record ConflictProbe(string Message, bool Conflict, int ExpectedVersion, int ActualVersion, ConfigViewDto? Current);
}
