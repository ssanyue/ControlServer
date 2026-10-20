using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// <b>被拒的注册必须在中心服务端"看得见"</b> —— 免凭据准入之后，这条守护反而更要紧。
/// <para>
/// <b>为什么单独一个类</b>：这几条用例要触发"在线表已满 ⇒ 409 / <c>toomany</c>"，
/// 而按生产默认容量（500 台）得真灌 500 次注册才撞得到。于是这里把
/// <c>Server:Agent:MaxHosts</c> 调成 <b>1</b>，用"先占满、再注册新机器"一步到位。
/// 容量调小会改变注册结果，与 <see cref="AgentsApiTests"/> 里那些要多台机器的用例互斥，
/// 所以<b>不能塞进同一个宿主</b>。
/// </para>
///
/// <para>
/// <b>背景（为什么"看得见"是刚需）</b>：<c>register</c> 是匿名端点，失败原因原本只写在
/// <b>客户机自己</b>的日志里。运维坐在中心服务端这边，能看到的全部痕迹只有 Serilog 请求日志的一行
/// <c>POST /api/agents/register → 409</c>，<b>不带任何原因</b>。于是"某台机器一直在敲门但进不来"
/// 在中心侧完全不可见 —— 现场唯一的现象是"装了客户端，页面上一直没有它"，
/// 而发现（UDP 广播）其实<b>一直是好的</b>，排查一开始就朝"服务端发现不了客户端"走偏了。
/// </para>
/// <para>
/// ⚠️ 免凭据之后，剩下的失败原因只有两类：<c>invalid</c>（客户端连机器码都没报上来）
/// 与 <c>toomany</c>（在线表满）。原设计里最常见的 <c>missing</c>／<c>unauthorized</c>
/// 已随准入令牌一并消失 —— 所以"装了客户端却在中心侧看不见"
/// <b>不再可能由凭据问题引起</b>，排查可以直接跳到网络与服务端容量。
/// </para>
/// </summary>
public sealed class AgentRegistrationRejectionTests : IDisposable
{
    /// <summary>容量 1 的宿主：注册第二台机器必然撞上 <c>toomany</c>。</summary>
    private readonly ControlServerFactory _factory =
        new(dataDirectory: null, enforcePasswordChange: false, licenseEnforcement: null,
            licenseAutoTrial: false, agentMaxHosts: 1);

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("admin", "admin123"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "登录必须成功，否则后面的断言全无意义");
        return client;
    }

    /// <summary>占满那一格容量，让后续注册新机器必然被拒。</summary>
    private async Task FillTheTableAsync(HttpClient anonymous)
        => (await anonymous.PostAsJsonAsync(
            "/api/agents/register", new AgentRegisterRequest(HostInfo("machine-occupies-the-only-slot"))))
            .StatusCode.Should().Be(HttpStatusCode.OK, "第一台机器必须能进去，否则后面测的就不是「满了」");

    [Fact]
    public async Task Rejected_registration_is_visible_in_the_server_log_once_per_cause()
    {
        using var anonymous = _factory.CreateClient();
        await FillTheTableAsync(anonymous);

        var rejected = await anonymous.PostAsJsonAsync(
            "/api/agents/register", new AgentRegisterRequest(HostInfo("machine-over-capacity")));

        rejected.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            "在线表满是「请求合法但资源不允许」，409 比 400/500 都准");

        // 走真实端点读日志：管理员在网页上看到的「最近日志」就是这条链路，
        // 直接取 DI 里的内存缓冲等于绕过了要验证的那一层。
        var admin = await LoginAsync();
        var first = await FindRejectionsAsync(admin, "machine-over-capacity");

        first.Should().HaveCount(1, "被拒的注册必须在服务端留下一条带原因的警告");

        var line = first[0].Message;

        first[0].Category.Should().Be("Enrollment", "现场要能按类别筛出准入问题");
        first[0].Level.Should().Be("Warning", "被拒是异常事件，不能沉到 Debug / Info 里");
        line.Should().Contain("拒绝客户端注册");
        line.Should().Contain("toomany", "失败状态要带上 —— 现场靠它一眼定位是哪类拦截");
        line.Should().Contain(
            "machine-over-capacity",
            "不写机器码 = 运维不知道是哪台机器在敲门，只能挨台去翻客户机日志");
        line.Should().Contain("在线表已达上限", "现场排障时这句话就是全部线索");
    }

    /// <summary>
    /// 同一来源 + 同一机器码 + 同一原因<b>只记首次</b>，但换台机器必须各记一条。
    /// <para>
    /// 客户端注册失败后会<b>每 60 秒</b>重试一次（退避上限 60 秒），
    /// 一台被容量挡住的机器一天能刷 1440 行；十台就是 14400 行，足以把真正的信息淹掉。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Repeated_rejections_are_deduplicated_but_a_different_machine_is_not()
    {
        using var anonymous = _factory.CreateClient();
        await FillTheTableAsync(anonymous);

        async Task<HttpStatusCode> RejectAsync(string machineKey)
            => (await anonymous.PostAsJsonAsync(
                "/api/agents/register", new AgentRegisterRequest(HostInfo(machineKey)))).StatusCode;

        // 同一台机器、同一原因，连打四次 —— 客户端就是这么重试的。
        for (var i = 0; i < 4; i++)
        {
            (await RejectAsync("machine-repeat")).Should().Be(HttpStatusCode.Conflict);
        }

        var admin = await LoginAsync();

        (await FindRejectionsAsync(admin, "machine-repeat")).Should().HaveCount(
            1,
            "一天 1440 行会把真正的信息淹掉；同一来源 + 同一机器码 + 同一原因只该记首次");

        // 换一台机器 —— 必须各记各的，不能被别人的去重吃掉。
        (await RejectAsync("machine-second")).Should().Be(HttpStatusCode.Conflict);

        (await FindRejectionsAsync(admin, "machine-second")).Should().HaveCount(
            1,
            "去重键里必须含机器码，否则第二台机器的问题会被第一台的去重静默吞掉");

        // 另一类失败（缺机器码 ⇒ invalid）与上面两条的「机器码 + 状态」都不同，
        // 同样要各自留痕，不能被已有的去重条目吸收。
        (await anonymous.PostAsJsonAsync(
            "/api/agents/register", new AgentRegisterRequest(null))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);

        (await FindRejectionsAsync(admin, "（invalid）")).Should().HaveCount(
            1,
            "换一种失败原因必须单独记一条 —— 否则「为什么被拒」就丢了一半");
    }

    /// <summary>
    /// 这些警告必须真的落到磁盘上的日志文件里 —— 现场运维是去 tail 那个文件的，
    /// 只在内存环形缓冲里活着等于"重启就查无此事"。
    /// </summary>
    [Fact]
    public async Task Rejection_warning_reaches_the_persisted_ndjson_log()
    {
        using var anonymous = _factory.CreateClient();
        await FillTheTableAsync(anonymous);

        await anonymous.PostAsJsonAsync(
            "/api/agents/register", new AgentRegisterRequest(HostInfo("machine-persisted")));

        var file = await WaitForPersistedLogAsync("machine-persisted");

        file.Should().NotBeNull(
            "被拒的注册没落盘，现场「服务端日志」里就永远查不到它");

        // ⚠️ 落盘用 System.Text.Json 的**默认选项** —— 属性名是 PascalCase（`Category`/`Level`/`Message`），
        // 且非 ASCII 会被转义成 \uXXXX。所以这里必须**解析 JSON**，
        // 绝不能拿中文去 grep 原文件（那样永远不命中，看起来像"日志没写进去"）。
        var rejectionLine = File.ReadLines(file!)
            .Where(l => l.Length > 0)
            .FirstOrDefault(l =>
            {
                var e = JsonDocument.Parse(l).RootElement;

                return e.TryGetProperty("Category", out var category)
                    && category.GetString() == "Enrollment"
                    && e.GetProperty("Message").GetString()!.Contains("拒绝客户端注册")
                    && e.GetProperty("Message").GetString()!.Contains("machine-persisted");
            });

        rejectionLine.Should().NotBeNull("落盘的那一行必须带类别、原因与机器码，三者缺一现场都没法查");

        var entry = JsonDocument.Parse(rejectionLine!).RootElement;

        entry.GetProperty("Level").GetInt32().Should().Be(2, "LogLevelKind.Warning = 2");
        entry.GetProperty("LevelText").GetString().Should().Be("Warning");
    }

    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 从「最近日志」里挑出**关于这台机器 / 这个原因被拒**的警告。
    /// <list type="bullet">
    ///   <item>必须按 <c>Warning</c> 过滤：重复的那些被刻意降成了 Debug，
    ///         不过滤就等于把"去重生效"测成"去重没生效"。</item>
    ///   <item>必须按机器码过滤：日志是宿主级共享的，别的断言也会往里写。
    ///         缺机器码那一类的行里机器码字段是「未提供」，所以调用点传 <c>（invalid）</c>
    ///         这类"原因标记"来区分。</item>
    /// </list>
    /// </summary>
    private async Task<List<LogDto>> FindRejectionsAsync(HttpClient admin, string marker)
    {
        var items = await admin.GetFromJsonAsync<LogDto[]>("/api/logs/recent?count=500");

        return (items ?? Array.Empty<LogDto>())
            .Where(e => e.Category == "Enrollment"
                     && e.Level == "Warning"
                     && e.Message.Contains("拒绝客户端注册")
                     && e.Message.Contains(marker))
            .ToList();
    }

    /// <summary>
    /// 等日志被刷到磁盘，返回命中的那份文件路径（超时返回 null）。
    /// <para>
    /// ⚠️ 不能"调一次 FlushAsync 就去读"：<see cref="LogService.FlushAsync"/> 抢锁用的是
    /// <c>WaitAsync(0)</c> —— 后台刷盘定时器（默认 500ms）恰好握着锁时它<b>直接返回、什么都不刷</b>，
    /// 于是偶发地读不到刚写的那行。必须轮询。
    /// </para>
    /// </summary>
    private async Task<string?> WaitForPersistedLogAsync(string machineKey)
    {
        var log = _factory.Services.GetRequiredService<LogService>();
        var directory = Path.Combine(_factory.DataDirectory, "logs");
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            await log.FlushAsync();

            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.GetFiles(directory, "log-*.ndjson"))
                {
                    // 机器码是 ASCII，转义不会碰到它 —— 拿它粗筛是安全的。
                    if (File.ReadAllText(file).Contains(machineKey)) return file;
                }
            }

            await Task.Delay(50);
        }

        return null;
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
}
