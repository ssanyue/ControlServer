using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.Contracts;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 动作执行端点（<c>POST /api/actions/execute</c>）的端到端测试 —— 真宿主 + 真 SQLite + 真 HTTP 管线。
/// <para>
/// 本轮新增的是「<b>只执行一条</b>」这条路（请求体里的 <c>actionId</c>），所以要守住三件事：
/// ① 给了 <c>actionId</c> 就<b>真的只发一条</b>（不是把整组跑一遍）；
/// ② 子按钮的动作走 <c>actionId</c> 时，目标地址仍然<b>由所属集合决定</b>
///    （动作自己那个过期 IP 不许"逃逸"）；
/// ③ <c>actionId</c> 找不到 / 没带宿主按钮，分别返回 <b>404 / 400</b>，而不是静默按整组执行 ——
///    静默降级会让"我以为只发了一条，其实发了一整组"变成一次现场事故。
/// </para>
/// </summary>
public sealed class ActionsApiTests : IDisposable
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

    /// <summary>把一份配置写进服务端（版本从 0 起，测试里都是新库）。</summary>
    private static async Task SaveAsync(HttpClient client, string settingsJson)
    {
        var put = await client.PutAsJsonAsync(
            "/api/config",
            new
            {
                settings = JsonSerializer.Deserialize<JsonElement>(settingsJson),
                expectedVersion = 0,
                updatedBy = "integration-test",
            });

        put.StatusCode.Should().Be(HttpStatusCode.OK, "预置配置必须写成功，否则后面的断言都是在空配置上跑");
    }

    // ═══════════════════════════════════════════════════════════
    // 单条执行：主按钮
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Action_id_runs_only_that_one_action()
    {
        var client = await LoginAsync();
        await SaveAsync(client, TwoActionButtonSettings());

        var response = await client.PostAsJsonAsync(
            "/api/actions/execute",
            new { buttonId = "b0", actionId = "a1" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<ActionExecutionDto>();

        dto.Should().NotBeNull();
        dto!.Steps.Should().HaveCount(1, "指定了 actionId 就该只发这一条");
        dto.Steps[0].ActionName.Should().Be("第二包");
        dto.Steps[0].TargetPort.Should().Be(59992, "必须是第二条动作自己的端口");
        dto.Steps[0].Ok.Should().BeTrue();

        // 展示名带上动作名 —— 收发日志里"单条"与"整组"长得一模一样，只能靠名字区分。
        dto.ButtonName.Should().Be("双动作 · 第二包");
        dto.Summary.Should().StartWith("双动作 · 第二包");

        // 收发缓冲里也只能有一条 —— 这是"只发了一条"的**外部可观测**证据。
        var traffic = await client.GetFromJsonAsync<List<TrafficLogDto>>("/api/traffic/recent");
        traffic.Should().ContainSingle();
        traffic![0].Data.Should().Be("TWO");
        traffic[0].Port.Should().Be(59992);
        traffic[0].ActionName.Should().Be("第二包");
    }

    [Fact]
    public async Task Omitting_action_id_still_runs_the_whole_group()
    {
        var client = await LoginAsync();
        await SaveAsync(client, TwoActionButtonSettings());

        var response = await client.PostAsJsonAsync("/api/actions/execute", new { buttonId = "b0" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<ActionExecutionDto>();

        dto!.Steps.Should().HaveCount(2, "不传 actionId 就是原来的整组语义，不能被这次改动带偏");
        dto.ButtonName.Should().Be("双动作", "整组执行仍然只用按钮名");
        dto.Steps.Select(s => s.ActionName).Should().Equal(new[] { "第一包", "第二包" });
    }

    // ═══════════════════════════════════════════════════════════
    // 单条执行：子按钮（目标地址必须来自所属集合）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Sub_button_action_id_still_uses_the_owning_sets_target()
    {
        var client = await LoginAsync();
        await SaveAsync(client, SubSetSettings());

        var response = await client.PostAsJsonAsync(
            "/api/actions/execute",
            new { subButtonId = "sb0", actionId = "sa0" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<ActionExecutionDto>();
        var step = dto!.Steps.Should().ContainSingle().Subject;

        // 动作里写的是 10.9.9.9:1（过期值），集合里是 127.0.0.1:59993 —— 必须用后者。
        step.TargetIp.Should().Be("127.0.0.1", "子按钮的目标地址一律由所属集合决定");
        step.TargetPort.Should().Be(59993);
        dto.ButtonName.Should().Be("红色 · 红");

        var missing = await client.PostAsJsonAsync(
            "/api/actions/execute",
            new { subButtonId = "sb0", actionId = "sa-does-not-exist" });
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound, "集合在、动作不在，不能退化成「发整组」");
    }

    // ═══════════════════════════════════════════════════════════
    // 参数校验：不许静默降级
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Action_id_without_an_owning_button_is_rejected()
    {
        var client = await LoginAsync();
        await SaveAsync(client, TwoActionButtonSettings());

        var response = await client.PostAsJsonAsync("/api/actions/execute", new { actionId = "a1" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "没有宿主按钮就无从判断动作属于谁，不能猜");
    }

    [Fact]
    public async Task Unknown_action_id_or_button_returns_404()
    {
        var client = await LoginAsync();
        await SaveAsync(client, TwoActionButtonSettings());

        var badAction = await client.PostAsJsonAsync(
            "/api/actions/execute",
            new { buttonId = "b0", actionId = "a-does-not-exist" });
        badAction.StatusCode.Should().Be(HttpStatusCode.NotFound, "按钮在、动作不在，不能退化成「发整组」");

        var badButton = await client.PostAsJsonAsync(
            "/api/actions/execute",
            new { buttonId = "b-nope", actionId = "a1" });
        badButton.StatusCode.Should().Be(HttpStatusCode.NotFound, "按钮本身就不存在");
    }

    [Fact]
    public async Task Executing_an_action_from_a_button_that_has_none_still_returns_an_empty_run()
    {
        var client = await LoginAsync();
        await SaveAsync(
            client,
            """{ "appName": "空动作", "buttons": [ { "id": "b0", "order": 0, "label": "空的" } ], "subButtonSets": [] }""");

        // 不带 actionId：这是合法的"没有可执行的动作"，历史上就是 200 + 0 条，
        // 不能被这次新增的 404 逻辑连坐 —— 两种"空"的含义不同。
        var response = await client.PostAsJsonAsync("/api/actions/execute", new { buttonId = "b0" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ActionExecutionDto>())!.Steps.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════
    // 组合按钮（2026-10-15，需求⑤）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 组合按钮：一次请求跑**整串成员**，段间按配置等待，结果是**分段**的。
    /// <para>
    /// 断言的关键不是"返回 200"，而是三件事：① 两个成员的动作都发出去了（收发缓冲 2 条）；
    /// ② 结果的段数与成员一致、且带成员名；③ 段间延迟真的等了。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Combo_runs_every_member_in_order_with_the_delay()
    {
        var client = await LoginAsync();
        await SaveAsync(client, ComboSettings());

        var started = DateTimeOffset.UtcNow;
        var response = await client.PostAsJsonAsync("/api/actions/execute", new { comboId = "c0" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<ComboExecutionDto>();
        var elapsed = DateTimeOffset.UtcNow - started;

        dto.Should().NotBeNull();
        dto!.ComboLabel.Should().Be("开场");
        dto.Segments.Should().HaveCount(2);
        dto.Segments.Select(s => s.ButtonLabel).Should().Equal(new[] { "灯光", "音响" });
        dto.Segments[0].DelayAfterMs.Should().Be(120, "延迟挂在第一步上 = 执行完灯光之后等 120ms");
        dto.Segments[1].DelayAfterMs.Should().Be(0, "最后一段的延迟不生效");
        dto.SuccessCount.Should().Be(2);
        dto.Ok.Should().BeTrue();

        elapsed.TotalMilliseconds.Should().BeGreaterThan(90, "段间延迟必须真的等");

        // 外部可观测证据：两条都真的发出去了（而不是只在结果里写了"执行过"）。
        var traffic = await client.GetFromJsonAsync<List<TrafficLogDto>>("/api/traffic/recent");
        traffic.Should().HaveCount(2);
        traffic!.Select(t => t.Data).Should().Equal(new[] { "LIGHT", "AUDIO" });
        traffic!.Select(t => t.Port).Should().Equal(new[] { 59981, 59982 });
    }

    /// <summary>
    /// `comboId` 与 `buttonId` 同时给 ⇒ **400**（而不是"以某个为准"）。
    /// <para>两条路语义不同（一串按钮 vs 一个按钮），含糊地选一个会让现场查不清到底执行了什么。</para>
    /// </summary>
    [Fact]
    public async Task Combo_id_conflicts_with_button_id()
    {
        var client = await LoginAsync();
        await SaveAsync(client, ComboSettings());

        var response = await client.PostAsJsonAsync(
            "/api/actions/execute", new { comboId = "c0", buttonId = "b0" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_combo_id_is_not_found()
    {
        var client = await LoginAsync();
        await SaveAsync(client, ComboSettings());

        var response = await client.PostAsJsonAsync("/api/actions/execute", new { comboId = "不存在" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ═══════════════════════════════════════════════════════════
    // 权限
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Executing_a_single_action_rejects_anonymous()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/actions/execute",
            new { buttonId = "b0", actionId = "a1" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    /// <summary>一个带两条动作的按钮（端口不同，便于断言"发的是哪一条"）。</summary>
    private static string TwoActionButtonSettings()
        => """
        {
          "appName": "动作测试",
          "buttons": [
            {
              "id": "b0",
              "order": 0,
              "label": "双动作",
              "actions": [
                { "id": "a0", "name": "第一包", "targetIp": "127.0.0.1", "targetPort": 59991, "protocol": "UDP", "dataType": "UTF-8", "dataContent": "ONE" },
                { "id": "a1", "name": "第二包", "targetIp": "127.0.0.1", "targetPort": 59992, "protocol": "UDP", "dataType": "UTF-8", "dataContent": "TWO" }
              ]
            }
          ],
          "subButtonSets": []
        }
        """;

    /// <summary>
    /// 两个主按钮 + 一个把它们串起来的组合按钮（2026-10-15，需求⑤）。
    /// <para>两个成员的端口**故意不同**：这样"两个包都发了"这件事在收发缓冲里可以直接读出来。</para>
    /// </summary>
    private static string ComboSettings()
        => """
        {
          "appName": "组合测试",
          "buttons": [
            {
              "id": "b0",
              "order": 0,
              "label": "灯光",
              "actions": [
                { "id": "a0", "name": "开灯", "targetIp": "127.0.0.1", "targetPort": 59981, "protocol": "UDP", "dataType": "UTF-8", "dataContent": "LIGHT" }
              ]
            },
            {
              "id": "b1",
              "order": 1,
              "label": "音响",
              "actions": [
                { "id": "a1", "name": "开音响", "targetIp": "127.0.0.1", "targetPort": 59982, "protocol": "UDP", "dataType": "UTF-8", "dataContent": "AUDIO" }
              ]
            }
          ],
          "subButtonSets": [],
          "comboButtons": [
            {
              "id": "c0",
              "order": 2,
              "label": "开场",
              "steps": [
                { "buttonId": "b0", "delayMs": 120 },
                { "buttonId": "b1", "delayMs": 0 }
              ]
            }
          ]
        }
        """;

    /// <summary>一个子按钮集：集合的目标地址与子按钮动作里写的那个**故意不同**。</summary>
    private static string SubSetSettings()
        => """
        {
          "appName": "子按钮测试",
          "buttons": [],
          "subButtonSets": [
            {
              "id": "s0",
              "order": 0,
              "label": "颜色集",
              "targetIp": "127.0.0.1",
              "targetPort": 59993,
              "subButtons": [
                {
                  "id": "sb0",
                  "label": "红色",
                  "actions": [
                    { "id": "sa0", "name": "红", "targetIp": "10.9.9.9", "targetPort": 1, "protocol": "UDP", "dataType": "UTF-8", "dataContent": "RED" }
                  ]
                }
              ]
            }
          ]
        }
        """;
}
