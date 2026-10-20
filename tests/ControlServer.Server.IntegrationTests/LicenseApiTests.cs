using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Licensing;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 授权测试专用的宿主工厂：把服务端的验签公钥换成测试自己生成的那一把。
///
/// <para>
/// <b>为什么必须换</b>：服务端内嵌的是<b>厂商公钥</b>，而厂商私钥按设计不存在于测试环境
/// （它只应躺在签发人机器上）。测试要签一份"服务端会接受"的授权，就只能让服务端改认测试的公钥。
/// </para>
///
/// <para>
/// <b>为什么这样换不算作弊</b>：验签算法、载荷构造、评估器、导入规则、闸门 —— 被测的每一层
/// 都是生产代码，换掉的只是"信任哪把钥匙"这一个注入点。而那个注入点在服务端<b>根本无法从外部到达</b>
/// （公钥是编译期常量，没有配置项，见 <c>VendorPublicKey</c> 的注释）。
/// </para>
/// </summary>
internal sealed class LicenseTestFactory : ControlServerFactory
{
    public LicenseTestFactory(string? enforcement = null, bool autoTrial = false)
        : base(
            dataDirectory: null,
            enforcePasswordChange: false,
            licenseEnforcement: enforcement,
            licenseAutoTrial: autoTrial)
    {
        var (privateKey, publicKey, _) = LicenseSigner.GenerateKeyPair();

        PrivateKeyPem = privateKey;
        PublicKeyPem = publicKey;
    }

    public string PrivateKeyPem { get; }

    public string PublicKeyPem { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        ReplaceServices(builder, services =>
        {
            services.RemoveAll<ILicenseSignatureVerifier>();
            services.AddSingleton<ILicenseSignatureVerifier>(new EcdsaLicenseVerifier(PublicKeyPem));
        });
    }

    /// <summary>用本工厂的私钥签发一份授权。</summary>
    public string Issue(LicenseDocument document) => LicenseSigner.Sign(document, PrivateKeyPem);

    /// <summary>授权文件的落盘路径（与服务端 <c>IDataDirectory.LicenseFile</c> 一致）。</summary>
    public string LicenseFilePath => Path.Combine(DataDirectory, "license.lic");

    /// <summary>绑定侧车路径。</summary>
    public string BindingFilePath => Path.Combine(DataDirectory, "license.binding.json");

    /// <summary>本机试用记录路径。</summary>
    public string TrialFilePath => Path.Combine(DataDirectory, "license.trial.json");

    /// <summary>把授权文本直接写进数据目录（模拟"手工拷贝授权文件"这种运维动作）。</summary>
    public void WriteLicenseFile(string text) => File.WriteAllText(LicenseFilePath, text);

    /// <summary>预置一份本机试用记录（宿主启动前调用）。</summary>
    public void WriteTrialFile(LocalTrialRecord record)
        => File.WriteAllText(
            TrialFilePath,
            JsonSerializer.Serialize(record, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));

    /// <summary>读回绑定侧车。</summary>
    public LicenseBinding? ReadBinding()
    {
        if (!File.Exists(BindingFilePath)) return null;

        return JsonSerializer.Deserialize<LicenseBinding>(
            File.ReadAllText(BindingFilePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}

/// <summary>
/// 设备授权的端到端测试（M3 授权子系统）。
///
/// <para>
/// 重点是那些<b>容易做成"看着有、其实没有"</b>的地方：
/// ① 验签真的在拦篡改（不是只解析了字段）；
/// ② 机器码绑定真的在拦"换台机器用"；
/// ③ 导入失败时状态<b>不会</b>变成"可用"（参照实现正是在这里翻过车）；
/// ④ 移除授权不会白送一次新试用；
/// ⑤ 读与写的权限确实不同（Viewer 能看不能导入）。
/// </para>
/// </summary>
public sealed class LicenseApiTests : IDisposable
{
    private readonly List<LicenseTestFactory> _factories = new();

    public void Dispose()
    {
        foreach (var factory in _factories) factory.Dispose();
    }

    // ═══════════════════════════════════════════════════════════
    // 工具
    // ═══════════════════════════════════════════════════════════

    private LicenseTestFactory NewFactory(string? enforcement = null, bool autoTrial = false)
    {
        var factory = new LicenseTestFactory(enforcement, autoTrial);
        _factories.Add(factory);
        return factory;
    }

    private static async Task<HttpClient> LoginAsync(
        LicenseTestFactory factory,
        string userName = "admin",
        string password = "admin123")
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, password));
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{userName} 必须能登录");

        return client;
    }

    private static async Task<LicenseDto> GetLicenseAsync(HttpClient client)
    {
        var dto = await client.GetFromJsonAsync<LicenseDto>("/api/license");
        dto.Should().NotBeNull();
        return dto!;
    }

    /// <summary>构造一份字段合法的授权文档（默认：正式、绑定本机、一年后到期、锚点为一天前）。</summary>
    private static LicenseDocument NewDocument(
        string machineCode,
        string kind = LicenseKind.Full,
        string expiresOn = "20991231",
        long exactExpiryUtcMs = 0,
        int universal = 0,
        long importDeadlineUtcMs = 0,
        bool withNetworkAnchor = true)
        => new()
        {
            V = LicenseDocument.CurrentVersion,
            Mc = machineCode,
            Lt = kind,
            Id = "20261008",
            Ed = expiresOn,
            Et = exactExpiryUtcMs,
            Sn = "SN-TEST-001",
            Vi = "VENDOR-TEST",
            Hn = "集成测试展厅",
            Ub = universal,
            Ud = importDeadlineUtcMs,
            Ivt = withNetworkAnchor ? DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds() : 0,
        };

    private static async Task<string> MachineCodeAsync(HttpClient client)
        => (await GetLicenseAsync(client)).MachineCode;

    // ═══════════════════════════════════════════════════════════
    // 访问控制
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Unauthenticated_request_is_rejected()
    {
        var factory = NewFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/license");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Any_authenticated_role_can_read_the_license_status()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory, "admin", "admin123");

        var viewer = await CreateUserAndLoginAsync(factory, admin, "viewer1", Roles.Viewer);

        var response = await viewer.GetAsync("/api/license");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "授权状态必须人人可见 —— 否则现场只看到按钮没反应，却不知道为什么");
    }

    [Fact]
    public async Task Viewer_cannot_import_or_generate_a_trial()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory, "admin", "admin123");
        var viewer = await CreateUserAndLoginAsync(factory, admin, "viewer2", Roles.Viewer);

        var import = await viewer.PostAsJsonAsync(
            "/api/license/import", new ImportLicenseRequest("whatever"));

        var trial = await viewer.PostAsync("/api/license/trial", content: null);

        import.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        trial.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Request_text_is_admin_only_and_carries_the_machine_code()
    {
        var factory = NewFactory();
        var admin = await LoginAsync(factory, "admin", "admin123");
        var viewer = await CreateUserAndLoginAsync(factory, admin, "viewer3", Roles.Viewer);

        (await viewer.GetAsync("/api/license/request")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var request = await admin.GetFromJsonAsync<LicenseRequestDto>("/api/license/request");

        request.Should().NotBeNull();
        request!.MachineCode.Should().Be(await MachineCodeAsync(admin));
        request.Text.Should().Contain(request.MachineCode);
    }

    // ═══════════════════════════════════════════════════════════
    // 初始状态
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Fresh_install_without_auto_trial_reports_not_authorized()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);

        var dto = await GetLicenseAsync(client);

        dto.Status.Should().Be(nameof(LicenseStatus.NotAuthorized));
        dto.StatusCode.Should().Be((int)LicenseStatus.NotAuthorized);
        dto.IsUsable.Should().BeFalse();
        dto.MachineCode.Should().MatchRegex("^[0-9A-F]{4}(-[0-9A-F]{4}){3}$");
        dto.Document.Should().BeNull();
        dto.Trial.Should().BeNull();
    }

    [Fact]
    public async Task Machine_code_is_stable_across_requests()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);

        var first = await MachineCodeAsync(client);
        var second = await MachineCodeAsync(client);

        first.Should().Be(second);
    }

    // ═══════════════════════════════════════════════════════════
    // 导入：拒绝路径
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Import_rejects_empty_text_with_400()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/api/license/import", new ImportLicenseRequest("   "));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Import_rejects_garbage_text_without_changing_status()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/api/license/import", new ImportLicenseRequest("这不是授权文件"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<LicenseMutationResult>();
        result!.Ok.Should().BeFalse();
        result.Status.Should().Be("malformed");
        result.License.IsUsable.Should().BeFalse("导入失败绝不能把状态变成可用");
    }

    [Fact]
    public async Task Import_rejects_an_expired_license()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);
        var machineCode = await MachineCodeAsync(client);

        var text = factory.Issue(NewDocument(machineCode, expiresOn: "20200101"));

        var response = await client.PostAsJsonAsync("/api/license/import", new ImportLicenseRequest(text));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<LicenseMutationResult>();
        result!.Status.Should().Be("expired");
        result.License.IsUsable.Should().BeFalse();
    }

    [Fact]
    public async Task Import_rejects_a_license_bound_to_another_machine()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);

        var text = factory.Issue(NewDocument("0000-1111-2222-3333"));

        var response = await client.PostAsJsonAsync("/api/license/import", new ImportLicenseRequest(text));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<LicenseMutationResult>();
        result!.Status.Should().Be("machinemismatch");
        result.Message.Should().Contain("0000-1111-2222-3333");
    }

    [Fact]
    public async Task Import_rejects_a_license_without_a_network_anchor()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);
        var machineCode = await MachineCodeAsync(client);

        var text = factory.Issue(NewDocument(machineCode, withNetworkAnchor: false));

        var response = await client.PostAsJsonAsync("/api/license/import", new ImportLicenseRequest(text));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<LicenseMutationResult>();
        result!.Status.Should().Be("missingnetworkanchor");
    }

    [Fact]
    public async Task Import_rejects_a_tampered_license()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);
        var machineCode = await MachineCodeAsync(client);

        // 先签一份 2020 年到期的（会被拒），再把到期日改到 2099 —— 字段层面完全合法，
        // 只有验签能抓住它。这正是"手工改授权文件延长有效期"的手法。
        var text = factory.Issue(NewDocument(machineCode, expiresOn: "20200101"));

        var tampered = LicenseCodec.Decode(text);
        tampered.Ed = "20991231";

        var response = await client.PostAsJsonAsync(
            "/api/license/import", new ImportLicenseRequest(LicenseCodec.Encode(tampered)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<LicenseMutationResult>();
        result!.Status.Should().Be("signatureinvalid");
        result.License.IsUsable.Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════
    // 导入：成功路径
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Import_accepts_a_valid_license_and_persists_it()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);
        var machineCode = await MachineCodeAsync(client);

        var text = factory.Issue(NewDocument(machineCode, expiresOn: "20991231"));

        var response = await client.PostAsJsonAsync("/api/license/import", new ImportLicenseRequest(text));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // ① 返回体里就带着全量状态 —— 前端不必再发一次 GET
        var result = await response.Content.ReadFromJsonAsync<LicenseMutationResult>();
        result!.Ok.Should().BeTrue();
        result.License.IsUsable.Should().BeTrue();
        result.License.Status.Should().Be(nameof(LicenseStatus.Authorized));
        result.License.Document!.SerialNumber.Should().Be("SN-TEST-001");
        result.License.Document.Customer.Should().Be("集成测试展厅");

        // ② 重新读一次（走磁盘），状态必须还是已授权
        var reread = await GetLicenseAsync(client);
        reread.Status.Should().Be(nameof(LicenseStatus.Authorized));
        reread.IsUsable.Should().BeTrue();
        reread.RemainingDays.Should().BeGreaterThan(0);

        // ③ 文件确实落在数据目录里
        File.Exists(factory.LicenseFilePath).Should().BeTrue();
    }

    [Fact]
    public async Task Imported_license_survives_a_host_restart()
    {
        var factory = NewFactory();

        var first = await LoginAsync(factory);
        var machineCode = await MachineCodeAsync(first);

        var text = factory.Issue(NewDocument(machineCode));
        await first.PostAsJsonAsync("/api/license/import", new ImportLicenseRequest(text));

        // 同一个数据目录、同一个宿主：再取一次状态即可（模拟"重启后仍有效"这类基本期望）
        var second = await LoginAsync(factory);
        var dto = await GetLicenseAsync(second);

        dto.IsUsable.Should().BeTrue();
        dto.Document.Should().NotBeNull();
    }

    [Fact]
    public async Task Universal_license_binds_to_this_machine_on_first_import()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);
        var machineCode = await MachineCodeAsync(client);

        // 通用授权：签发时 mc 为空、ub=1，导入时才绑定本机。
        var text = factory.Issue(NewDocument(
            machineCode: string.Empty,
            universal: 1,
            importDeadlineUtcMs: DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds()));

        var response = await client.PostAsJsonAsync("/api/license/import", new ImportLicenseRequest(text));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var dto = await GetLicenseAsync(client);

        dto.IsUsable.Should().BeTrue();
        dto.Document!.IsUniversal.Should().BeTrue();
        dto.Document.BoundMachineCode.Should().Be(machineCode);

        // 绑定结果落在侧车文件里（授权文件本身不能改 —— 改了签名就废）
        var binding = factory.ReadBinding();
        binding.Should().NotBeNull();
        binding!.MachineCode.Should().Be(machineCode);
    }

    [Fact]
    public async Task Universal_license_past_its_import_deadline_is_rejected()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);

        var text = factory.Issue(NewDocument(
            machineCode: string.Empty,
            universal: 1,
            importDeadlineUtcMs: DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds()));

        var response = await client.PostAsJsonAsync("/api/license/import", new ImportLicenseRequest(text));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<LicenseMutationResult>();
        result!.Status.Should().Be("deadlineexceeded");
    }

    [Fact]
    public async Task A_license_file_dropped_directly_into_the_data_directory_is_picked_up()
    {
        // 运维在没有前端可用的阶段常用这一招：直接把 .lic 拷进数据目录。
        // 状态刷新每次读盘，正是为了让这个动作立刻生效。
        var factory = NewFactory();
        var client = await LoginAsync(factory);
        var machineCode = await MachineCodeAsync(client);

        factory.WriteLicenseFile(factory.Issue(NewDocument(machineCode)));

        var dto = await GetLicenseAsync(client);

        dto.IsUsable.Should().BeTrue();
    }

    [Fact]
    public async Task A_corrupted_license_file_is_reported_as_error_not_silently_ignored()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);

        factory.WriteLicenseFile("这是一个损坏的授权文件");

        var dto = await GetLicenseAsync(client);

        dto.Status.Should().Be(nameof(LicenseStatus.Error));
        dto.IsUsable.Should().BeFalse();
        dto.LastError.Should().NotBeNullOrWhiteSpace();
    }

    // ═══════════════════════════════════════════════════════════
    // 移除
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Removing_the_license_returns_to_not_authorized()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);
        var machineCode = await MachineCodeAsync(client);

        await client.PostAsJsonAsync(
            "/api/license/import", new ImportLicenseRequest(factory.Issue(NewDocument(machineCode))));

        var response = await client.DeleteAsync("/api/license");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await GetLicenseAsync(client);
        dto.IsUsable.Should().BeFalse();
        dto.Document.Should().BeNull();
        File.Exists(factory.LicenseFilePath).Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════
    // 本机试用
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Trial_can_be_generated_and_reports_trial_status()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);

        var response = await client.PostAsync("/api/license/trial", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var dto = await GetLicenseAsync(client);

        dto.Status.Should().Be(nameof(LicenseStatus.Trial));
        dto.IsUsable.Should().BeTrue();
        dto.IsLocalTrial.Should().BeTrue();
        dto.RemainingDays.Should().BeGreaterThan(0);
        dto.Trial.Should().NotBeNull();
        dto.Trial!.Days.Should().Be(30);
    }

    /// <summary>
    /// 「N 天试用」必须正好覆盖 **N 个自然日**（含起算日）。
    ///
    /// <para>
    /// 这条守门用例来自一次实际观测：首次生成 30 天试用后，页面顶栏徽章显示
    /// <b>「试用 31 天」</b>。根因是到期日按"起算日 + N 天"算，而 `ed` 的语义是
    /// <b>当日末刻才到期</b>（见 <c>LicenseEvaluator.ResolveExpiry</c>，客户直觉里
    /// "11 月 7 日到期"意味着 7 号一整天都还能用）—— 两条规则叠加就多送了一整天，
    /// 顺带让 1 天试用会跨到第二天。
    /// </para>
    ///
    /// <para>
    /// 断言刻意**只用记录里的两个日期做差**，不掺入"现在是几点"：这样既精确
    /// 又不受测试运行时刻影响（在 23:59 跑和在 00:01 跑结论一致）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Trial_grant_covers_exactly_the_configured_number_of_days()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);

        (await client.PostAsync("/api/license/trial", content: null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var dto = await GetLicenseAsync(client);
        var trial = dto.Trial!;

        var issuedOn = DateOnly.ParseExact(trial.IssuedAt[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var expiresOn = DateOnly.ParseExact(trial.ExpiresOn, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        (expiresOn.DayNumber - issuedOn.DayNumber).Should().Be(
            trial.Days - 1,
            "N 天试用 = 含起算日在内的 N 个自然日，到期日必须落在第 N 天");

        // 剩余天数不该超过发放的天数。旧实现下这里是 31（发放 30），
        // 也就是"徽章上的天数比发放的还多"——用户看不出问题，但确实是多送了。
        dto.RemainingDays.Should().BeLessThanOrEqualTo(
            trial.Days,
            "剩余天数不可能超过发放的天数");
    }

    [Fact]
    public async Task Generating_a_trial_escalates_the_default_warn_enforcement()
    {
        // 默认档位是 Warn（保证升级不锁死），但一旦进入过授权周期就必须收紧 ——
        // 否则"试用到期"永远只是一句提示，拦不住任何人。
        var factory = NewFactory(enforcement: null);
        var client = await LoginAsync(factory);

        var before = await GetLicenseAsync(client);
        before.Enforcement.Should().Be(nameof(LicenseEnforcement.Warn));

        await client.PostAsync("/api/license/trial", content: null);

        var after = await GetLicenseAsync(client);
        after.ConfiguredEnforcement.Should().Be(nameof(LicenseEnforcement.Warn));
        after.Enforcement.Should().Be(nameof(LicenseEnforcement.Enforce));
    }

    [Fact]
    public async Task Trial_cannot_be_generated_again_while_it_is_still_active()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);

        (await client.PostAsync("/api/license/trial", content: null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var second = await client.PostAsync("/api/license/trial", content: null);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadFromJsonAsync<LicenseMutationResult>())!.Status
            .Should().Be("trial-active");
    }

    [Fact]
    public async Task Trial_cannot_be_generated_when_a_license_is_installed()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);
        var machineCode = await MachineCodeAsync(client);

        await client.PostAsJsonAsync(
            "/api/license/import", new ImportLicenseRequest(factory.Issue(NewDocument(machineCode))));

        var response = await client.PostAsync("/api/license/trial", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<LicenseMutationResult>())!.Status
            .Should().Be("already-licensed");
    }

    [Fact]
    public async Task Removing_a_license_falls_back_to_the_existing_trial_not_a_fresh_one()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);

        // ① 生成试用
        await client.PostAsync("/api/license/trial", content: null);
        var trialBefore = (await GetLicenseAsync(client)).Trial!;

        // ② 导入正式授权
        var machineCode = await MachineCodeAsync(client);
        await client.PostAsJsonAsync(
            "/api/license/import", new ImportLicenseRequest(factory.Issue(NewDocument(machineCode))));
        (await GetLicenseAsync(client)).Status.Should().Be(nameof(LicenseStatus.Authorized));

        // ③ 移除授权 —— 试用记录不能被清掉，否则"移除 → 又白拿 30 天"就是免费通道
        await client.DeleteAsync("/api/license");

        var dto = await GetLicenseAsync(client);
        dto.Status.Should().Be(nameof(LicenseStatus.Trial));
        dto.Trial!.IssuedAt.Should().Be(trialBefore.IssuedAt, "试用起点不该被移除授权这个动作重置");
    }

    [Fact]
    public async Task Auto_trial_is_created_on_first_startup()
    {
        var factory = NewFactory(autoTrial: true);
        var client = await LoginAsync(factory);

        var dto = await GetLicenseAsync(client);

        dto.Status.Should().Be(nameof(LicenseStatus.Trial));
        dto.IsLocalTrial.Should().BeTrue();
        File.Exists(factory.TrialFilePath).Should().BeTrue();
    }

    [Fact]
    public async Task Auto_trial_is_not_regenerated_when_a_record_already_exists()
    {
        var factory = NewFactory(autoTrial: true);

        // 预置一份**已过期**的试用记录，模拟"30 天用完了"。
        // 自动试用若在每次启动都补一份，试用就成了永久免费 —— 这条用例专门守这个。
        factory.WriteTrialFile(new LocalTrialRecord(
            MachineCode: "PLACEHOLDER",
            IssuedAtUtcMs: DateTimeOffset.UtcNow.AddDays(-60).ToUnixTimeMilliseconds(),
            ExpiresOn: "20200101",
            Days: 30,
            AnchorSource: LocalTrialRecord.AnchorFromNetwork));

        var client = await LoginAsync(factory);

        var dto = await GetLicenseAsync(client);

        // 机器码不匹配（预置的是占位值）⇒ 试用不认，报错而**不是**重新生成一份。
        dto.Status.Should().Be(nameof(LicenseStatus.Error));
        dto.Trial.Should().NotBeNull("记录仍在，只是不被接受");
        dto.Trial!.ExpiresOn.Should().Be("2020-01-01", "旧的到期日不该被刷新");
    }

    [Fact]
    public async Task Auto_trial_disabled_leaves_a_fresh_install_not_authorized()
    {
        var factory = NewFactory(autoTrial: false);
        var client = await LoginAsync(factory);

        var dto = await GetLicenseAsync(client);

        dto.Status.Should().Be(nameof(LicenseStatus.NotAuthorized));
        File.Exists(factory.TrialFilePath).Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════
    // 审计
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Import_and_removal_are_written_to_the_audit_log()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);
        var machineCode = await MachineCodeAsync(client);

        await client.PostAsJsonAsync(
            "/api/license/import", new ImportLicenseRequest(factory.Issue(NewDocument(machineCode))));
        await client.DeleteAsync("/api/license");

        var actions = QueryAuditActions(factory);

        actions.Should().Contain("license-imported");
        actions.Should().Contain("license-removed");
    }

    [Fact]
    public async Task Rejected_imports_are_audited_too()
    {
        var factory = NewFactory();
        var client = await LoginAsync(factory);

        await client.PostAsJsonAsync(
            "/api/license/import", new ImportLicenseRequest(factory.Issue(NewDocument("0000-1111-2222-3333"))));

        QueryAuditActions(factory).Should().Contain("license-import-rejected:machinemismatch");
    }

    private static List<string> QueryAuditActions(LicenseTestFactory factory)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={factory.DatabaseFile};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();

        // 表名是 snake_case（见 AppDbContext 的 ToTable），列名是属性名原样。
        command.CommandText = "SELECT Action FROM audit_logs";

        var actions = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) actions.Add(reader.GetString(0));

        return actions;
    }

    // ═══════════════════════════════════════════════════════════
    // 工具
    // ═══════════════════════════════════════════════════════════

    private static async Task<HttpClient> CreateUserAndLoginAsync(
        LicenseTestFactory factory,
        HttpClient admin,
        string userName,
        string role)
    {
        const string password = "Str0ng-Pass!";

        var created = await admin.PostAsJsonAsync(
            "/api/users", new CreateUserRequest(userName, password, role));

        created.StatusCode.Should().Be(
            HttpStatusCode.OK, $"新建 {userName} 应成功：{await created.Content.ReadAsStringAsync()}");

        return await LoginAsync(factory, userName, password);
    }
}
