using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using ControlServer.Contracts;
using ControlServer.Server.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Data.Sqlite;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 三角色 RBAC 与用户账号管理的端到端测试（M3）。
/// <para>
/// 重点在四件**容易做成"看着有、其实没有"的事**：
/// ① 角色分级真的在服务端生效（不是靠前端藏按钮）；
/// ② 改角色 / 停用<b>当场</b>作用于已签发的会话（不是等 Cookie 过期）；
/// ③ 守卫规则（不能删自己、不能干掉最后一个管理员）真的拦得住；
/// ④ 账号接口<b>绝不</b>把口令材料回给浏览器。
/// </para>
/// </summary>
public sealed class UserRbacTests : IDisposable
{
    private const string StrongPassword = "Str0ng-Pass!";

    private readonly ControlServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // ═══════════════════════════════════════════════════════════
    // 工具
    // ═══════════════════════════════════════════════════════════

    private async Task<HttpClient> LoginAsync(string userName = "admin", string password = "admin123")
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, password));
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{userName} 必须能登录");
        return client;
    }

    private async Task<HttpClient> CreateUserAndLoginAsync(HttpClient admin, string userName, string role)
    {
        var created = await admin.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest(userName, StrongPassword, role));

        created.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"新建 {userName} 应成功：{await created.Content.ReadAsStringAsync()}");

        return await LoginAsync(userName, StrongPassword);
    }

    private async Task<int> FindUserIdAsync(HttpClient admin, string userName)
    {
        var users = await admin.GetFromJsonAsync<List<UserDto>>("/api/users");
        users.Should().NotBeNull();

        return users!.Single(u => u.UserName == userName).Id;
    }

    private static object CommandBody(string name)
        => new { command = new { name, protocol = "UDP", dataType = "String", data = "ON" } };

    /// <summary>直接读库（只读连接，与 <c>MigrationTests</c> 同一套做法）。</summary>
    private List<T> QueryDb<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={_factory.DatabaseFile};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);

        var rows = new List<T>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(read(reader));

        return rows;
    }

    // ═══════════════════════════════════════════════════════════
    // 访问控制
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Users_endpoint_is_admin_only()
    {
        var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var admin = await LoginAsync();
        var op = await CreateUserAndLoginAsync(admin, "op-gate", Roles.Operator);
        var viewer = await CreateUserAndLoginAsync(admin, "vw-gate", Roles.Viewer);

        (await admin.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.OK);

        // 账号管理是"谁能进来"这件事本身 —— 给 Operator 就等于让它自己提权。
        (await op.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await op.PostAsJsonAsync("/api/users", new CreateUserRequest("sneaky", StrongPassword, Roles.Admin)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "Operator 不能给自己造一个管理员");
    }

    [Fact]
    public async Task Viewer_can_read_the_board_but_cannot_write_anything()
    {
        var admin = await LoginAsync();
        var viewer = await CreateUserAndLoginAsync(admin, "viewer-read", Roles.Viewer);

        // ── 读：看板所需的全部只读面 ──
        (await viewer.GetAsync("/api/config")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.GetAsync("/api/status")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.GetAsync("/api/logs/recent?count=5")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.GetAsync("/api/timers")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.GetAsync("/api/commands")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.GetAsync("/api/traffic/recent?count=5")).StatusCode.Should().Be(HttpStatusCode.OK);

        // ── 写：一律 403 ──
        (await viewer.PostAsJsonAsync("/api/commands", CommandBody("viewer-write")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsJsonAsync("/api/actions/execute", new { }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.GetAsync("/api/system/info")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Operator_may_change_business_config_but_not_system_level_state()
    {
        var admin = await LoginAsync();
        var op = await CreateUserAndLoginAsync(admin, "op-scope", Roles.Operator);

        // 指令库写入 —— Operator 的本职
        (await op.PostAsJsonAsync("/api/commands", CommandBody("op-command")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // 全量配置写入 —— 同样属于业务操作
        var config = await admin.GetFromJsonAsync<JsonElement>("/api/config");
        var put = await op.PutAsJsonAsync("/api/config", new
        {
            settings = config.GetProperty("settings"),
            expectedVersion = config.GetProperty("version").GetInt32(),
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        // 系统级：导出一份整机配置 / 导入覆盖全站 —— 收紧为管理员
        (await op.GetAsync("/api/config/export?format=json")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await op.PostAsJsonAsync("/api/config/import", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await op.GetAsync("/api/system/machine-code")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 运行诊断对 Operator 仍可见（它要判断"为什么发不出去"）
        (await op.GetAsync("/api/system/info")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ═══════════════════════════════════════════════════════════
    // 会话即时生效（这是 RBAC 的"保证"与"承诺"之分）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Demoting_a_logged_in_user_takes_effect_on_their_existing_session()
    {
        var admin = await LoginAsync();
        var op = await CreateUserAndLoginAsync(admin, "op-live", Roles.Operator);

        (await op.PostAsJsonAsync("/api/commands", CommandBody("before-demote")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var id = await FindUserIdAsync(admin, "op-live");

        (await admin.PutAsJsonAsync($"/api/users/{id}", new UpdateUserRequest(null, Roles.Viewer, null)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Cookie 里存的是签发时的快照；没有逐请求复核的话，这里会继续放行最长 12 小时。
        (await op.PostAsJsonAsync("/api/commands", CommandBody("after-demote")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await op.GetAsync("/api/commands")).StatusCode.Should().Be(HttpStatusCode.OK, "降级后仍可只读");
    }

    [Fact]
    public async Task Disabling_a_user_kills_their_session_and_blocks_relogin()
    {
        var admin = await LoginAsync();
        var victim = await CreateUserAndLoginAsync(admin, "victim", Roles.Viewer);

        (await victim.GetAsync("/api/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        var id = await FindUserIdAsync(admin, "victim");

        (await admin.PutAsJsonAsync($"/api/users/{id}", new UpdateUserRequest(null, null, false)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // 已签发的会话当场失效 —— 不能等 Cookie 12 小时过期。
        (await victim.GetAsync("/api/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 重新登录同样被拒（口令是对的，但账号已停用）。
        var relogin = await _factory.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new LoginRequest("victim", StrongPassword));

        relogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await relogin.Content.ReadAsStringAsync()).Should().Contain("停用");
    }

    // ═══════════════════════════════════════════════════════════
    // 守卫规则
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Admin_cannot_disable_or_delete_their_own_account()
    {
        var admin = await LoginAsync();
        var adminId = await FindUserIdAsync(admin, "admin");

        var disable = await admin.PutAsJsonAsync($"/api/users/{adminId}", new UpdateUserRequest(null, null, false));
        disable.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await disable.Content.ReadFromJsonAsync<UserMutationResult>())!.Status.Should().Be("SelfTarget");

        var delete = await admin.DeleteAsync($"/api/users/{adminId}");
        delete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await delete.Content.ReadFromJsonAsync<UserMutationResult>())!.Status.Should().Be("SelfTarget");

        // 账号仍然可用
        (await admin.GetAsync("/api/status")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_only_admin_cannot_demote_themselves()
    {
        var admin = await LoginAsync();
        var adminId = await FindUserIdAsync(admin, "admin");

        var demote = await admin.PutAsJsonAsync($"/api/users/{adminId}", new UpdateUserRequest(null, Roles.Viewer, null));

        demote.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var dto = await demote.Content.ReadFromJsonAsync<UserMutationResult>();
        dto!.Status.Should().Be("LastAdmin");
        dto.Users.Single(u => u.Id == adminId).Role.Should().Be(Roles.Admin, "被拒的操作不能留下一半效果");
    }

    [Fact]
    public async Task An_admin_with_a_peer_may_demote_themselves()
    {
        var first = await LoginAsync();
        var second = await CreateUserAndLoginAsync(first, "admin-two", Roles.Admin);

        var firstId = await FindUserIdAsync(first, "admin");

        // 有第二位管理员时，交接后把自己退成查看者是正常诉求。
        (await first.PutAsJsonAsync($"/api/users/{firstId}", new UpdateUserRequest(null, Roles.Viewer, null)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // 降级立刻作用于自己的会话
        (await first.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await second.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.OK, "另一位管理员不受影响");
    }

    // ═══════════════════════════════════════════════════════════
    // 增删改与校验
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task User_names_are_unique_case_insensitively()
    {
        var admin = await LoginAsync();

        var duplicate = await admin.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest("ADMIN", StrongPassword, Roles.Viewer));

        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await duplicate.Content.ReadFromJsonAsync<UserMutationResult>())!.Status.Should().Be("DuplicateName");
    }

    [Fact]
    public async Task Create_rejects_invalid_role_and_weak_password()
    {
        var admin = await LoginAsync();

        var badRole = await admin.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest("role-test", StrongPassword, "Superuser"));
        badRole.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await badRole.Content.ReadFromJsonAsync<UserMutationResult>())!.Status.Should().Be("Invalid");

        var weak = await admin.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest("weak-test", "short", Roles.Viewer));
        weak.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await weak.Content.ReadFromJsonAsync<UserMutationResult>())!.Status.Should().Be("Invalid");

        // 随程序发布的默认口令是公开值，不能拿来当新账号的口令。
        var shipped = await admin.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest("default-pass", "admin123", Roles.Viewer));
        shipped.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await shipped.Content.ReadFromJsonAsync<UserMutationResult>())!.Status.Should().Be("Invalid");

        var nameAsPassword = await admin.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest("SameAsName", "sameasname", Roles.Viewer));
        nameAsPassword.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await admin.GetFromJsonAsync<List<UserDto>>("/api/users"))!.Should().HaveCount(1, "失败的创建不能留下账号");
    }

    [Fact]
    public async Task Created_accounts_are_not_forced_to_change_password()
    {
        var admin = await LoginAsync();
        await CreateUserAndLoginAsync(admin, "fresh-user", Roles.Viewer);

        var listed = (await admin.GetFromJsonAsync<List<UserDto>>("/api/users"))!;
        listed.Should().HaveCount(2);

        var fresh = listed.Single(u => u.UserName == "fresh-user");
        fresh.MustChangePassword.Should().BeFalse("2026-10-09 起新建账号不再强制改密");
        fresh.IsEnabled.Should().BeTrue();
        fresh.LastLoginAt.Should().NotBeNull("刚登录过");

        listed.Single(u => u.UserName == "admin").MustChangePassword.Should()
            .BeFalse("出厂管理员同样不再被标记");
    }

    [Fact]
    public async Task Update_can_rename_and_toggle_role()
    {
        var admin = await LoginAsync();
        var target = await CreateUserAndLoginAsync(admin, "rename-me", Roles.Viewer);

        var id = await FindUserIdAsync(admin, "rename-me");

        (await admin.PutAsJsonAsync($"/api/users/{id}", new UpdateUserRequest("renamed", Roles.Operator, null)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var users = await admin.GetFromJsonAsync<List<UserDto>>("/api/users");
        var updated = users!.Single(u => u.Id == id);
        updated.UserName.Should().Be("renamed");
        updated.Role.Should().Be(Roles.Operator);

        // 改名后旧名不能登录、新名可以
        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("rename-me", StrongPassword)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await target.GetAsync("/api/status")).StatusCode.Should().Be(HttpStatusCode.OK, "已签发的会话不该因为改名而失效");
    }

    [Fact]
    public async Task Deleting_a_user_blocks_further_login()
    {
        var admin = await LoginAsync();
        await CreateUserAndLoginAsync(admin, "doomed", Roles.Viewer);

        var id = await FindUserIdAsync(admin, "doomed");
        (await admin.DeleteAsync($"/api/users/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("doomed", StrongPassword)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await admin.GetFromJsonAsync<List<UserDto>>("/api/users"))!.Should().HaveCount(1);

        // 删完再删 → 404
        (await admin.DeleteAsync($"/api/users/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reset_password_lets_the_owner_log_in_without_being_forced_to_change()
    {
        var admin = await LoginAsync();
        await CreateUserAndLoginAsync(admin, "reset-target", Roles.Viewer);

        var id = await FindUserIdAsync(admin, "reset-target");
        const string tempPassword = "Temp-Assigned-Pass!";

        var reset = await admin.PostAsJsonAsync($"/api/users/{id}/password", new ResetPasswordRequest(tempPassword));
        reset.StatusCode.Should().Be(HttpStatusCode.OK);

        // 旧口令失效
        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("reset-target", StrongPassword)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 新口令可用，且不再被要求改密（2026-10-09 起取消强制改密）
        var client = await LoginAsync("reset-target", tempPassword);
        (await client.GetFromJsonAsync<CurrentUser>("/api/auth/me"))!.MustChangePassword.Should().BeFalse();

        // 重置成弱口令要被拒
        (await admin.PostAsJsonAsync($"/api/users/{id}/password", new ResetPasswordRequest("123")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ═══════════════════════════════════════════════════════════
    // 改自己的密码
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Changing_own_password_requires_the_current_one_and_clears_the_flag()
    {
        var client = await LoginAsync();

        // 当前密码不对 → 400
        (await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest("wrong-one", "Another-Pass!1")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 新密码不满足策略 → 400
        (await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest("admin123", "short")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 新旧相同 → 400
        (await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest("admin123", "admin123")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 正路
        (await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest("admin123", "New-Admin-Pass!")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.GetFromJsonAsync<CurrentUser>("/api/auth/me"))!.MustChangePassword.Should().BeFalse();

        // 旧口令失效、新口令可用
        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin123")))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "New-Admin-Pass!")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ═══════════════════════════════════════════════════════════
    // 登录失败锁定
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Repeated_failures_lock_the_account_for_a_fixed_window()
    {
        var client = _factory.CreateClient();

        for (var i = 0; i < UserAccountService.MaxFailedAttempts; i++)
        {
            (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "definitely-wrong")))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var lockedAt = ReadLockedUntil("admin");
        lockedAt.Should().NotBeNull("连续失败达到阈值后必须锁定");

        // 口令正确也进不去，且原因说得清楚（口令错时绝不透露账号状态）
        var correct = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin123"));
        correct.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await correct.Content.ReadAsStringAsync()).Should().Contain("锁定");

        // 锁定期间继续输错**不得**延长锁定期，否则账号会被永久锁死。
        for (var i = 0; i < UserAccountService.MaxFailedAttempts * 2; i++)
        {
            await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "still-wrong"));
        }

        ReadLockedUntil("admin").Should().Be(lockedAt, "锁定是一个固定窗口，不能被后续尝试无限推后");
    }

    // ═══════════════════════════════════════════════════════════
    // 审计与最小暴露面
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task User_mutations_are_written_to_the_audit_log()
    {
        var admin = await LoginAsync();
        await CreateUserAndLoginAsync(admin, "audited", Roles.Viewer);

        var id = await FindUserIdAsync(admin, "audited");
        await admin.DeleteAsync($"/api/users/{id}");

        var actions = QueryDb(
            "SELECT Action FROM audit_logs ORDER BY Id;",
            r => r.GetString(0));

        actions.Should().Contain("user-created");
        actions.Should().Contain("user-deleted");
    }

    [Fact]
    public async Task User_list_never_exposes_password_material()
    {
        var admin = await LoginAsync();
        await CreateUserAndLoginAsync(admin, "leak-check", Roles.Viewer);

        var raw = await admin.GetStringAsync("/api/users");

        raw.Should().NotContain("passwordHash");
        raw.Should().NotContain("PasswordHash");
        raw.Should().NotContain("salt");

        // 形状抽查：字段确实在，别把"漏了字段"当成"没泄漏"。
        // ⚠️ 服务端序列化是 camelCase，这里必须用 Web 默认（大小写不敏感）才能读到值 ——
        //    用默认的 JsonSerializerOptions 会静默得到一堆 null，然后"没泄漏"就成了假结论。
        var users = JsonSerializer.Deserialize<List<UserDto>>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        users.Should().NotBeNull();
        users!.Should().HaveCount(2);
        users.Should().AllSatisfy(u => u.UserName.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public async Task Role_catalog_is_served_so_the_ui_need_not_hardcode_names()
    {
        var admin = await LoginAsync();

        var roles = await admin.GetFromJsonAsync<List<JsonElement>>("/api/users/roles");

        roles.Should().NotBeNull();
        roles!.Select(r => r.GetProperty("name").GetString())
            .Should().BeEquivalentTo(new[] { "Admin", "Operator", "Viewer" });
        roles!.Select(r => r.GetProperty("displayName").GetString())
            .Should().Contain("管理员");
    }

    private string? ReadLockedUntil(string userName)
        => QueryDb(
            "SELECT LockedUntil FROM users WHERE UserName = $name;",
            r => r.IsDBNull(0) ? null : r.GetString(0),
            ("$name", userName))
            .FirstOrDefault();
}

/// <summary>
/// "必须先改密"闸门（<c>Server:Auth:EnforcePasswordChange</c>）—— 2026-10-09 起<b>默认关闭</b>。
/// <para>
/// 产品已取消"首次使用强制改密"：出厂管理员不再标记，新建账号与管理员重置密码也不再标记，
/// 因此默认状态下没有任何账号会触发本闸门。这里守两件事：
/// </para>
/// <para>
/// ① <b>默认行为</b>：出厂 / 新建账号登录后直接可用业务接口，且不带"必须改密"标记；
/// </para>
/// <para>
/// ② <b>机制仍在</b>：显式开启闸门并手工标记账号时，闸门照常拦截、白名单照常放行 ——
/// 否则"保留这个开关"就成了一句没人验证的承诺。
/// </para>
/// </summary>
public sealed class ForcedPasswordChangeTests
{
    [Fact]
    public async Task Default_host_does_not_force_the_seeded_admin_to_change_password()
    {
        using var factory = new ControlServerFactory(); // 默认：闸门关、账号不标记
        var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin123"));
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        (await login.Content.ReadFromJsonAsync<LoginResponse>())!.MustChangePassword
            .Should().BeFalse("出厂不再强制改密");
        (await client.GetFromJsonAsync<CurrentUser>("/api/auth/me"))!.MustChangePassword
            .Should().BeFalse();

        // 这正是"取消首次强制改密"最直观的效果：业务接口开箱即用。
        (await client.GetAsync("/api/status")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Newly_created_account_is_not_marked_as_must_change()
    {
        using var factory = new ControlServerFactory();
        var admin = factory.CreateClient();
        (await admin.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin123")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var created = await admin.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest("no-forced", "Str0ng-Pass!", Roles.Operator));
        created.StatusCode.Should().Be(HttpStatusCode.OK);

        (await created.Content.ReadFromJsonAsync<UserMutationResult>())!
            .User!.MustChangePassword.Should().BeFalse("新建账号不再强制改密");
    }

    [Fact]
    public async Task Gate_still_enforced_when_enabled_and_account_is_flagged()
    {
        using var factory = new ControlServerFactory(null, enforcePasswordChange: true);

        // 出厂已不标记任何账号，这里手工把 admin 标成"必须先改密"，
        // 模拟"手工标记 / 旧库升级"这类仍会触发闸门的场景。
        // （CreateClient 会同步完成建库与播种，之后才能写库。）
        var client = factory.CreateClient();
        MarkMustChangePassword(factory.DatabaseFile, "admin", true);

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin123"));
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        (await login.Content.ReadFromJsonAsync<LoginResponse>())!.MustChangePassword.Should().BeTrue();

        // me 也必须带回这个标记 —— 否则刷新一次页面就绕过去了。
        (await client.GetFromJsonAsync<CurrentUser>("/api/auth/me"))!.MustChangePassword.Should().BeTrue();

        // 业务接口被闸门挡住
        (await client.GetAsync("/api/status")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/config")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 但会话本身还在，且改密端点必须在白名单里，
        // 否则用户卡在"要先改密"与"改密接口不允许访问"之间的死结。
        (await client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        var changed = await client.PostAsJsonAsync(
            "/api/auth/change-password",
            new ChangePasswordRequest("admin123", "Brand-New-Pass!"));
        changed.StatusCode.Should().Be(HttpStatusCode.OK);

        // 改完当场放行：不能出现"响应说成功了、下一个请求还是 403"的窗口。
        (await client.GetFromJsonAsync<CurrentUser>("/api/auth/me"))!.MustChangePassword.Should().BeFalse();
        (await client.GetAsync("/api/status")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Stale_flag_in_an_older_database_is_cleared_on_startup()
    {
        // 造一个"旧库"：先把 admin 标记为必须改密，再用**同一个数据目录**重启宿主。
        // 启动清洗必须把它清掉 —— 否则升级上来的实例会继续弹强制改密窗（闸门虽已关）。
        var dataDir = Path.Combine(
            Path.GetTempPath(),
            "controlserver-tests",
            "stale-" + Guid.NewGuid().ToString("N"));

        try
        {
            using (var first = new ControlServerFactory(dataDir))
            {
                _ = first.CreateClient();                  // 触发建库与播种
                MarkMustChangePassword(first.DatabaseFile, "admin", true);
            }

            using var second = new ControlServerFactory(dataDir);
            var client = second.CreateClient();

            var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin123"));
            login.StatusCode.Should().Be(HttpStatusCode.OK);

            (await login.Content.ReadFromJsonAsync<LoginResponse>())!.MustChangePassword
                .Should().BeFalse("升级上来的旧库标记应被启动清洗清掉");
        }
        finally
        {
            try
            {
                if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
            }
            catch
            {
                // 清理失败不影响结论。
            }
        }
    }

    /// <summary>直接写库把账号标成"必须先改密"（只读连接不足以制造触发条件）。</summary>
    private static void MarkMustChangePassword(string databaseFile, string userName, bool value)
    {
        using var connection = new SqliteConnection($"Data Source={databaseFile}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET MustChangePassword = $v WHERE UserName = $n;";
        command.Parameters.AddWithValue("$v", value ? 1 : 0);
        command.Parameters.AddWithValue("$n", userName);
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// 授权标注的守门测试。
/// <para>
/// 整个项目<b>没有全局回退策略</b>（<c>FallbackPolicy</c>），每个端点的"需要登录"靠自己的标注。
/// 这意味着"忘了写 <c>[Authorize]</c>"的后果是<b>端点对匿名开放</b>，且没有任何运行期提示。
/// 而 <c>/readyz</c>、<c>/healthz</c>、<c>MapFallbackToFile</c> 又依赖"不加回退策略"才正常 ——
/// 所以这里用一条反射断言把"忘标注"变成编译后立刻失败的测试，而不是靠人工 review。
/// </para>
/// </summary>
public sealed class AuthorizationCoverageTests
{
    [Fact]
    public void Every_controller_action_is_either_authorized_or_explicitly_anonymous()
    {
        var controllers = typeof(Program).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(ControllerBase).IsAssignableFrom(t))
            .ToList();

        controllers.Should().NotBeEmpty("至少要能扫到控制器，否则这条测试就是空转");

        var offenders = new List<string>();

        foreach (var controller in controllers)
        {
            var classAnonymous = controller.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
            var classAuthorized = controller.GetCustomAttribute<AuthorizeAttribute>() is not null;

            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (action.GetCustomAttribute<HttpMethodAttribute>(inherit: true) is null) continue;

                var anonymous = classAnonymous || action.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
                var authorized = classAuthorized || action.GetCustomAttribute<AuthorizeAttribute>() is not null;

                if (!anonymous && !authorized)
                {
                    offenders.Add($"{controller.Name}.{action.Name}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "以下端点没有 [Authorize]，在当前没有全局回退策略的情况下等于**对匿名开放**："
            + string.Join(", ", offenders));
    }
}
