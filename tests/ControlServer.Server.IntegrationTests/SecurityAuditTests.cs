using System.Net;
using System.Net.Http.Json;
using ControlServer.Contracts;
using ControlServer.Server.Security;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// M6.2 安全审计的<b>必修项</b>证据。
///
/// <para>
/// 每条用例对应审计清单里的一行，而不是"顺手多写几个测试"：
/// <list type="bullet">
///   <item>口令哈希从单轮 SHA-256 升级到 PBKDF2，且<b>存量账号登录一次之后就自动升级</b>（不锁死任何人）；</item>
///   <item>登录端点有按 IP 的限速，超了回 429 且给中文提示；</item>
///   <item><c>Security:AllowedCidrs</c> 这个"从 M1 起就存在、却从未生效"的配置<b>真的接到管线上了</b>；</item>
///   <item>三条安全响应头真的在响应里。</item>
/// </list>
/// </para>
/// </summary>
public sealed class SecurityAuditTests
{
    private const string WeakLegacyPassword = "admin123";

    // ═══════════════════════════════════════════════════════════
    // ① 口令哈希：旧值可登录，且登录后就地升级
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// M6.2 最重要的一条证据：<b>把库里的值改回 M1 的格式，仍然能登录，而且登录完成后它变成了 PBKDF2</b>。
    /// <para>
    /// 只验"新哈希能验"是不够的 —— 真正的风险在存量部署：库里全是旧值，
    /// 升级如果要求先重置密码，现场就得挨个通知。这条用例证明"什么都不用做，用户下次登录即完成迁移"。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Legacy_sha256_password_still_logs_in_and_is_upgraded_in_place()
    {
        using var factory = new ControlServerFactory();

        // 先把宿主拉起来（CreateClient 会触发启动与播种）。
        using var client = factory.CreateClient();

        var legacy = WriteLegacyHash(factory.DatabaseFile, "admin", WeakLegacyPassword);

        legacy.Should().StartWith("LEGACY-SHA256:", "写入成功时返回的标记");

        // ── 用旧值登录：必须成功 ──
        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("admin", WeakLegacyPassword));

        login.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "存量部署的旧哈希必须还能登录，否则升级等于把所有人锁在门外");

        // ── 库里那条值必须已经变成 PBKDF2 ──
        var after = ReadHash(factory.DatabaseFile, "admin");

        after.Should().StartWith(
            Core.Services.Pbkdf2PasswordHasher.Prefix,
            "成功登录后应就地升级为 PBKDF2（rehash-on-login）");

        // ── 升级后的值照样能登录（证明重算的不是"另外某个口令"）──
        using var second = factory.CreateClient();
        var again = await second.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("admin", WeakLegacyPassword));

        again.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ═══════════════════════════════════════════════════════════
    // ② 登录限速
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 额度 2：前两次是 401（口令错），第三次必须 429。
    /// <para>
    /// 用<b>错误口令</b>打，是为了证明限速<b>先于</b>账号逻辑生效 ——
    /// 真实攻击者手里就是错口令；而正确口令连打三次只会暴露"额度太小"这一件事。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Login_is_rate_limited_per_ip()
    {
        using var factory = new ControlServerFactory(
            dataDirectory: null,
            enforcePasswordChange: false,
            licenseEnforcement: null,
            licenseAutoTrial: false,
            loginPermitLimit: 2);

        using var client = factory.CreateClient();

        var first = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "wrong-1"));
        var second = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "wrong-2"));
        var third = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "wrong-3"));

        first.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        second.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        third.StatusCode.Should().Be(
            HttpStatusCode.TooManyRequests,
            "额度用尽之后必须 429，否则「换用户名扫一遍」与「故意锁死管理员」都没有成本");

        // 429 必须给一句人话：前端会把 message 直接显示在登录框下面。
        var body = await third.Content.ReadAsStringAsync();
        body.Should().Contain("频繁");
    }

    // ═══════════════════════════════════════════════════════════
    // ③ 网段白名单：配置真的接到管线上了
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 配了白名单之后：DI 里那个单例<b>不是空的</b>（说明配置被读了），
    /// 且请求<b>确实被拦下</b>（说明中间件在管线上）。
    /// <para>
    /// ⚠️ TestServer 的 <c>RemoteIpAddress</c> 是 <c>null</c>，而"判不出来就不放"
    /// 是我们的既定语义（见 <see cref="NetworkAllowList.Allows"/>），因此这里期望 403。
    /// 若将来换成真实 Kestrel（回环），这条用例会变成 200 —— 那时应当把断言改成
    /// "回环放行"，而不是把白名单删掉。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Allowed_cidrs_is_wired_and_blocks_unknown_source()
    {
        using var factory = new ControlServerFactory(
            dataDirectory: null,
            enforcePasswordChange: false,
            licenseEnforcement: null,
            licenseAutoTrial: false,
            allowedCidrs: "192.168.1.0/24");

        using var client = factory.CreateClient();

        var allowList = factory.Services.GetRequiredService<NetworkAllowList>();

        allowList.IsEmpty.Should().BeFalse("配置被读到 DI 里了（这正是审计发现它从未被消费的那一处）");
        allowList.Count.Should().Be(1);

        var response = await client.GetAsync("/trust/info");

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "来源判不出来时按拒绝处理（fail-closed），且这条规则必须真的在管线上");
    }

    /// <summary>不配白名单时一切照旧 —— 这条是"改动没有伤到既有部署"的证据。</summary>
    [Fact]
    public async Task Without_allowed_cidrs_nothing_is_blocked()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();

        factory.Services.GetRequiredService<NetworkAllowList>().IsEmpty.Should().BeTrue();

        var response = await client.GetAsync("/trust/info");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ═══════════════════════════════════════════════════════════
    // ④ 安全响应头
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Responses_carry_baseline_security_headers()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/trust/info");

        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().ContainSingle().Which.Should().Be("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().ContainSingle().Which.Should().Be("no-referrer");
    }

    // ═══════════════════════════════════════════════════════════
    // 工具：直接改库（复现"存量部署"的状态）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把某个账号的哈希改写成 M1 的单轮 SHA-256 格式，返回 <c>LEGACY-SHA256:&lt;hash&gt;</c>。
    /// <para>
    /// 走 SQLite 直连而不是调接口：接口已经<b>只会</b>产生新格式了
    /// （那正是这次修复的目的），所以旧状态只能从库这一层造出来。
    /// </para>
    /// </summary>
    private static string WriteLegacyHash(string databaseFile, string userName, string password)
    {
        const string salt = "0123456789ABCDEF0123456789ABCDEF";

        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(salt + ":" + password)));

        using var connection = new SqliteConnection($"Data Source={databaseFile}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET PasswordHash = $hash, Salt = $salt WHERE UserName = $name";
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$salt", salt);
        command.Parameters.AddWithValue("$name", userName);

        command.ExecuteNonQuery().Should().Be(1, "必须真的改到那一行，否则后面的断言在验一个假前提");

        return "LEGACY-SHA256:" + hash;
    }

    private static string ReadHash(string databaseFile, string userName)
    {
        using var connection = new SqliteConnection($"Data Source={databaseFile}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT PasswordHash FROM users WHERE UserName = $name";
        command.Parameters.AddWithValue("$name", userName);

        return command.ExecuteScalar() as string ?? string.Empty;
    }
}
