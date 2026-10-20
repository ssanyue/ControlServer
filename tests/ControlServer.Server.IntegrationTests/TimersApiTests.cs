using System.Net;
using System.Net.Http.Json;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 定时与系统诊断端点（M2-E4）的端到端测试 —— 真宿主 + 真 SQLite + 真 HTTP 管线。
/// <para>
/// <b>手动触发的"连点两次"闸门刻意不在这里测</b>：那要求两次请求落在同一秒内，
/// 是典型的 flaky 写法。它在 <c>TimerSchedulerServiceTests</c> 里用可精确控制的假时钟覆盖
/// （<c>Fire_now_is_blocked_by_the_same_second_idempotency_gate</c>），语义完全一致。
/// </para>
/// </summary>
public sealed class TimersApiTests : IDisposable
{
    private readonly ControlServerFactory _factory = new();

    public void Dispose()
    {
        _factory.Dispose();

        // 环境变量是进程级的：不清理会污染后续测试类。
        for (var i = 0; i < 4; i++)
        {
            Environment.SetEnvironmentVariable($"Server__Time__NtpServers__{i}", null);
        }

        Environment.SetEnvironmentVariable("Server__Time__NtpTimeoutMs", null);

        // 手动校准会写进程级静态偏移，测完必须复位（否则影响其他测试类的时间断言）。
        BeijingTime.ResetOffset();
    }

    /// <summary>
    /// 把所有 NTP 服务器指向 <c>192.0.2.1</c>（RFC 5737 的 TEST-NET-1，保证不可路由），
    /// 让手动校准**确定性地失败**：稳定、快，且离线环境同样可跑。
    /// <para>
    /// ⚠️ 环境变量对数组是<b>按下标覆盖</b>、不是整表替换：<c>appsettings.json</c> 里已有 3 个服务器，
    /// 只设 <c>__0</c> 的话后面两个照样会被尝试并**真的成功**（实测踩过：列表变成
    /// <c>[127.0.0.1, ntp.tencent.com, cn.pool.ntp.org]</c>，第二个一查就通了）。
    /// 因此这里把 <c>0..3</c> 全部覆盖掉。若哪天配置里的列表变长，这个测试要同步扩展。
    /// </para>
    /// <para>必须在宿主首次构建（<c>CreateClient</c>）之前调用。</para>
    /// </summary>
    private static void PointNtpAtUnroutableAddress()
    {
        for (var i = 0; i < 4; i++)
        {
            Environment.SetEnvironmentVariable($"Server__Time__NtpServers__{i}", "192.0.2.1");
        }

        Environment.SetEnvironmentVariable("Server__Time__NtpTimeoutMs", "300");
    }

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin123"));
        response.StatusCode.Should().Be(HttpStatusCode.OK, "默认管理员必须能登录");
        return client;
    }

    /// <summary>
    /// 建立一份带定时按钮的配置。时刻用 <c>23:59:59</c>：
    /// 无论测试在一天中的什么时候跑，"今天还没到点"都成立，断言因此是确定的。
    /// </summary>
    private static object TimedConfig() => new
    {
        settings = new
        {
            appName = "定时测试",
            buttons = new object[]
            {
                new
                {
                    id = "btn-daily",
                    order = 0,
                    label = "每天一次",
                    timerEnabled = true,
                    timerMode = "Daily",
                    timerTimes = new[] { "23:59:59" },
                    timerDays = new[] { 1, 2, 3, 4, 5 },
                    actions = new[]
                    {
                        new
                        {
                            id = "a1",
                            name = "发一包",
                            targetIp = "127.0.0.1",
                            targetPort = 59995,
                            protocol = "UDP",
                            dataType = "UTF-8",
                            dataContent = "TIMER-API",
                        },
                    },
                },
                new
                {
                    id = "btn-weekly",
                    order = 1,
                    label = "只周日跑",
                    timerEnabled = true,
                    timerMode = "SpecificDays",
                    timerTimes = new[] { "23:59:58" },
                    timerDays = new[] { 0 },
                },
            },
            subButtonSets = Array.Empty<object>(),
        },
        expectedVersion = 0,
        updatedBy = "integration-test",
    };

    // ═══════════════════════════════════════════════════════════
    // 鉴权
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Timer_and_system_endpoints_reject_anonymous()
    {
        var client = _factory.CreateClient();

        (await client.GetAsync("/api/timers")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsync("/api/timers/anything/fire", null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsync("/api/system/timesync", null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/system/info")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ═══════════════════════════════════════════════════════════
    // GET /api/timers
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Fresh_install_has_no_triggers()
    {
        var client = await LoginAsync();

        var overview = await client.GetFromJsonAsync<TimerOverviewDto>("/api/timers");

        overview.Should().NotBeNull();
        overview!.Triggers.Should().BeEmpty();
        overview.NextFireAt.Should().BeNull("配置里没有任何触发点");
        overview.Enabled.Should().BeFalse("测试宿主刻意关掉了后台调度循环（见 ControlServerFactory）");
        overview.ServerTime.Offset.Should().Be(TimeSpan.FromHours(8), "接口时间一律北京时间");
    }

    [Fact]
    public async Task Timers_list_reports_the_configured_triggers()
    {
        var client = await LoginAsync();

        var put = await client.PutAsJsonAsync("/api/config", TimedConfig());
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var overview = await client.GetFromJsonAsync<TimerOverviewDto>("/api/timers");

        overview!.Triggers.Should().HaveCount(2);
        overview.Triggers.Select(t => t.Time).Should().Equal(new[] { "23:59:58", "23:59:59" }, "清单按时刻升序");

        var daily = overview.Triggers.Single(t => t.RefId == "btn-daily");
        daily.ButtonLabel.Should().Be("每天一次");
        daily.Kind.Should().Be("Daily");
        daily.Time.Should().Be("23:59:59");
        daily.AppliesToday.Should().BeTrue("Daily 模式不校验星期");
        daily.FiredToday.Should().BeFalse("今天的 23:59:59 还没到");
        daily.Elapsed.Should().BeFalse();

        var weekly = overview.Triggers.Single(t => t.RefId == "btn-weekly");
        weekly.Kind.Should().Be("SpecificDays");
        weekly.Days.Should().Equal(new[] { 0 });

        // 用响应里的 ServerTime（北京时间）推星期，而不是测试机的本地星期 ——
        // 否则在非 UTC+8 的机器上、跨日那几小时会随机失败。
        weekly.AppliesToday.Should().Be(
            overview.ServerTime.DayOfWeek == DayOfWeek.Sunday, "判定必须与调度器同源");

        overview.NextFireAt.Should().NotBeNull("每天 23:59:59 一定存在下一次");
        overview.NextFireAt!.Value.Offset.Should().Be(TimeSpan.FromHours(8));
    }

    // ═══════════════════════════════════════════════════════════
    // POST /api/timers/{refId}/fire
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Firing_a_timer_manually_executes_it()
    {
        var client = await LoginAsync();
        await client.PutAsJsonAsync("/api/config", TimedConfig());

        var response = await client.PostAsync("/api/timers/btn-daily/fire", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<TimerFireResultDto>();
        result!.Ok.Should().BeTrue(result.Message);
        result.Status.Should().Be("Fired");
        result.RefId.Should().Be("btn-daily");
        result.ButtonLabel.Should().Be("每天一次");
        result.Date.Should().HaveLength(10);
        result.Time.Should().HaveLength(8);

        result.Execution.Should().NotBeNull("手动触发必须真的执行动作，而不是只写一条记录");
        result.Execution!.Ok.Should().BeTrue(result.Execution.Summary);
        result.Execution.Steps.Should().ContainSingle();
        result.Execution.Steps[0].DataPreview.Should().Be("TIMER-API");
    }

    [Fact]
    public async Task Firing_an_unknown_refid_returns_404()
    {
        var client = await LoginAsync();
        await client.PutAsJsonAsync("/api/config", TimedConfig());

        var response = await client.PostAsync("/api/timers/根本没有这个按钮/fire", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<TimerFireResultDto>())!.Status.Should().Be("NotFound");
    }

    // ═══════════════════════════════════════════════════════════
    // POST /api/system/timesync
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Timesync_reports_a_failure_as_200_because_the_failure_is_the_answer()
    {
        PointNtpAtUnroutableAddress();
        var client = await LoginAsync();

        var response = await client.PostAsync("/api/system/timesync", null);

        // 不可路由地址 → 校准必然失败，但**这是要展示的信息**，不是 4xx/5xx。
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<TimeSyncResultDto>();
        result.Should().NotBeNull();

        var seen = string.Join(" | ", result!.Attempts);
        result.Attempts.Should().NotBeEmpty("要留下逐个服务器的尝试记录，便于排障");
        result.Attempts[0].Should().Contain("192.0.2.1", $"环境变量注入的服务器应生效；实际 = [{seen}]");
        result.ServerTime.Offset.Should().Be(TimeSpan.FromHours(8));

        result.Ok.Should().BeFalse($"不可路由地址上不会有 NTP 服务；实际 = [{seen}]");
        result.Error.Should().NotBeNullOrWhiteSpace("失败必须说清原因");
    }

    // ═══════════════════════════════════════════════════════════
    // GET /api/system/info
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task System_info_exposes_time_sync_and_timer_summary()
    {
        var client = await LoginAsync();
        await client.PutAsJsonAsync("/api/config", TimedConfig());

        var info = await client.GetFromJsonAsync<SystemInfoDto>("/api/system/info");

        info.Should().NotBeNull();
        info!.TimeZone.Should().Be(BeijingTime.DisplayName);
        info.DataDirectory.Should().Be(_factory.DataDirectory, "数据目录必须回显实际生效值，而不是配置原文");
        info.ServerTime.Offset.Should().Be(TimeSpan.FromHours(8));

        info.TimeSync.AutoSync.Should().BeFalse("测试宿主关掉了自动校准");
        info.TimeSync.Synchronized.Should().BeFalse();
        info.TimeSync.LastSyncAt.Should().BeNull();
        info.TimeSync.Servers.Should().NotBeEmpty("即使配置为空也要回显生效的内置默认列表");

        info.Timers.TriggerCount.Should().Be(2);
        info.Timers.Enabled.Should().BeFalse();
        info.Timers.NextFireAt.Should().NotBeNull();
    }

    [Fact]
    public async Task System_info_does_not_leak_the_machine_code()
    {
        // 机器码属授权信息，只走 AdminOnly 的 /api/system/machine-code。
        var client = await LoginAsync();

        var info = await client.GetAsync("/api/system/info");
        var body = await info.Content.ReadAsStringAsync();

        body.Should().NotContain("machineCode");
        body.Should().NotContain("fingerprint");
    }
}
