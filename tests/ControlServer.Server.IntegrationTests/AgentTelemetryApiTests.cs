using System.Net;
using System.Net.Http.Json;
using ControlServer.Contracts;
using ControlServer.Server.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 遥测的摄入与查询（M4.2）端到端 —— 真宿主 + 真 SQLite + 真 EF 查询。
/// <para>
/// <b>为什么摄入走 DI 直调而不是 HTTP</b>：客户端实际上是通过 SignalR Hub 上报的，
/// 没有对应的 REST 摄入端点；用 <c>TestServer</c> 模拟 SignalR 连接会把测试变成"Hub 测试"。
/// 这里要守的是<b>另一段</b>更容易静默出错的链路：
/// <c>AgentTelemetryService → EF 存储 → 聚合 → 查询端点</c>。
/// 而 Hub 那一层本身很薄（只做鉴权与转发），它的覆盖在 <c>AgentHub</c> 与冒烟脚本里。
/// </para>
/// <para>
/// ⚠️ 这个文件里最有价值的一条是 <see cref="Series_query_survives_a_utc_shaped_window"/>：
/// 遥测表的时间列一旦被改回 <c>DateTimeOffset</c>，EF 的 SQLite 提供程序会在
/// <c>ORDER BY</c> 上抛 <c>NotSupportedException</c> —— 而它<b>只在真查库的那一刻</b>炸，
/// 编译和单测都发现不了。这条用例就是那根保险丝。
/// </para>
/// </summary>
public sealed class AgentTelemetryApiTests : IDisposable
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);

    private readonly ControlServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // ═══════════════════════════════════════════════════════════
    // 基建
    // ═══════════════════════════════════════════════════════════

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("admin", "admin123"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "登录必须成功，否则后面的断言全无意义");
        return client;
    }

    /// <summary>免凭据注册一台机器（2026-10-13 起不需要令牌 / 加入码），返回它的 Id。</summary>
    /// <param name="machineKey">
    /// 机器码。<b>必须传不同的值才能得到不同的 AgentId</b> —— 用同一个机器码再注册
    /// 是"这台机器重装了客户端"，服务端会**复用**原有那条记录（这是 M4.1 的有意设计）。
    /// </param>
    private async Task<int> RegisterOneAsync(string machineKey = "machine-telemetry")
    {
        var anonymous = _factory.CreateClient();
        var registered = await anonymous.PostAsJsonAsync(
            "/api/agents/register",
            new AgentRegisterRequest(
                new AgentHostInfoDto(
                    machineKey, "TELEMETRY-PC", "Windows 11 (integration test)",
                    "Test CPU @ 1.00GHz", 4, 8192, "0.1.0", "tester",
                    new[] { new AgentNetworkInterfaceDto("以太网", "192.168.1.20", "AA:BB:CC:DD:EE:02", true) })));

        registered.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await registered.Content.ReadFromJsonAsync<AgentRegisterResult>();

        return result!.AgentId!.Value;
    }

    /// <summary>
    /// 直接从容器里取摄入服务 —— 它就是 Hub 调用的那一个实例（单例）。
    /// <para>⚠️ 必须走 <c>factory.Services</c> 而不是自己 new：那样写才不会与宿主里那份
    /// 内存缓冲/注册表脱节（在线表的"最新一帧"就是从这同一个实例写进注册表的）。</para>
    /// </summary>
    private AgentTelemetryService Telemetry => _factory.Services.GetRequiredService<AgentTelemetryService>();

    private static AgentMetricsSampleDto Sample(
        DateTimeOffset at,
        double? cpu = 10,
        long? memUsed = 4000,
        long? memTotal = 8000,
        double? gpu = null)
        => new(at, cpu, memUsed, memTotal, 100, 200, 300, 400, gpu, 3600);

    // ═══════════════════════════════════════════════════════════
    // 鉴权
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Telemetry_read_endpoints_reject_anonymous_access()
    {
        // 遥测是"这台机器在干什么"的实录：CPU 曲线、开关机时刻、装了哪些硬件。
        // 它是资产台账的一部分，不能给匿名用户看。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        using var anonymous = _factory.CreateClient();

        foreach (var path in new[]
                 {
                     $"/api/agents/{agentId}/metrics",
                     $"/api/agents/{agentId}/power-events",
                     $"/api/agents/{agentId}/inventory",
                 })
        {
            var response = await anonymous.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{path} 不该匿名可读");
        }
    }

    [Fact]
    public async Task Telemetry_of_an_unknown_agent_is_404_not_an_empty_series()
    {
        // "这台机器不存在"与"这台机器还没上报过"该做的事完全不同 ——
        // 前者说明页面上的 Id 是错的，后者只是"等一会儿"。合并成空序列会让前者永远查不出来。
        var admin = await LoginAsync();

        var response = await admin.GetAsync("/api/agents/9999/metrics");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ═══════════════════════════════════════════════════════════
    // 指标：摄入 → 落盘 → 查询
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Ingested_metrics_become_points_only_after_the_flush()
    {
        // 这一条钉住"内存缓冲 + 异步落盘"这个**有意的**设计：
        // 摄入时立刻返回成功（客户端不被 SQLite 拖慢），但库里要等 FlushAsync 才有数据。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var now = BeijingNo(DateTime.UtcNow).AddMinutes(-5);
        var batch = new AgentMetricsBatchDto(new[]
        {
            Sample(now, cpu: 12.5),
            Sample(now.AddSeconds(5), cpu: 20),
        });

        var ack = Telemetry.AcceptMetrics(agentId, batch);
        ack.Ok.Should().BeTrue();
        ack.Accepted.Should().Be(2);

        var before = await admin.GetFromJsonAsync<AgentMetricsSeriesDto>(
            $"/api/agents/{agentId}/metrics?from={Uri.EscapeDataString(Iso(now.AddMinutes(-1)))}" +
            $"&to={Uri.EscapeDataString(Iso(now.AddMinutes(1)))}");

        before!.RawCount.Should().Be(0, "还没落盘，库里应当什么都没有");
        before.Points.Should().BeEmpty();

        await Telemetry.FlushAsync();

        var after = await admin.GetFromJsonAsync<AgentMetricsSeriesDto>(
            $"/api/agents/{agentId}/metrics?from={Uri.EscapeDataString(Iso(now.AddMinutes(-1)))}" +
            $"&to={Uri.EscapeDataString(Iso(now.AddMinutes(1)))}");

        after!.RawCount.Should().Be(2);
        after.Source.Should().Be("raw");
        after.Points.Should().NotBeEmpty();
        after.Points.Sum(p => p.CpuPct ?? 0).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Series_query_survives_a_utc_shaped_window()
    {
        // ⚠️⚠️ 保险丝用例。前端 `new Date().toISOString()` 发出来的是 UTC（`...Z`）。
        //      EF 的 SQLite 提供程序**拒绝翻译** DateTimeOffset 的 ORDER BY，
        //      一旦遥测时间列被改回 DateTimeOffset，这条查询会在运行时抛
        //      NotSupportedException —— 编译、单测都发现不了，只有真查库才炸。
        //      服务端还必须把窗口归一化回 +08:00 再回显：前端 fmtDateTime 是截字符串显示的，
        //      回一个 `...Z` 会让横轴整体少 8 小时。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var now = BeijingNo(DateTime.UtcNow);
        Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(new[] { Sample(now) }));
        await Telemetry.FlushAsync();

        // 刻意用 UTC 形态（带 Z）发查询。
        var from = now.AddHours(-1).ToUniversalTime().ToString("O");
        var to = now.AddHours(1).ToUniversalTime().ToString("O");

        var response = await admin.GetAsync(
            $"/api/agents/{agentId}/metrics?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "SQLite 无法 ORDER BY DateTimeOffset；真炸了这里会是 500，而不是一个看不出原因的空白");

        var series = await response.Content.ReadFromJsonAsync<AgentMetricsSeriesDto>();

        series!.From.Offset.Should().Be(Beijing, "回显窗口必须归一化到 +08:00");
        series.To.Offset.Should().Be(Beijing);
    }

    [Fact]
    public async Task Telemetry_timestamps_round_trip_exactly_at_millisecond_precision()
    {
        // 🔥 把"遥测时间戳以 Unix 毫秒落库"这个决定的**可见后果**钉住。
        //    起因：EF 的 SQLite 提供程序拒绝翻译 DateTimeOffset 的 ORDER BY，
        //    于是遥测时间列改存 long（Unix 毫秒），转换只经 TelemetryTime 一处。
        //    代价就是亚毫秒被裁掉 —— 一个整秒基准必须**原样**回来（含 +08:00 偏移）。
        //    若哪天有人把它换回 DateTimeOffset，这条会立刻红。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var boot = new DateTimeOffset(2026, 10, 8, 9, 0, 0, Beijing);

        await Telemetry.AcceptPowerEventsAsync(agentId, new AgentPowerEventBatchDto(new[]
        {
            new AgentPowerEventDto(boot, AgentPowerEventKinds.Boot, 6005, null, null),
        }));

        var result = await admin.GetFromJsonAsync<AgentPowerEventListDto>(
            $"/api/agents/{agentId}/power-events?limit=10");

        var stored = result!.Items.Single();
        stored.At.Should().Be(boot, "整秒时间戳必须原样回来");
        stored.At.Offset.Should().Be(Beijing, "偏移必须保持 +08:00");
        stored.At.Millisecond.Should().Be(0);
    }

    [Fact]
    public async Task Power_events_query_survives_a_utc_shaped_window()
    {
        // 同一个坑的第二处（当初就是**这里**先炸的：开关机列表一打开就抛异常）。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        await Telemetry.AcceptPowerEventsAsync(agentId, new AgentPowerEventBatchDto(new[]
        {
            new AgentPowerEventDto(BeijingNo(DateTime.UtcNow).AddHours(-2), AgentPowerEventKinds.Boot, 6005, null, "事件日志服务已启动。"),
        }));

        var from = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");

        var response = await admin.GetAsync(
            $"/api/agents/{agentId}/power-events?from={Uri.EscapeDataString(from)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_sample_whose_values_are_missing_stays_null_instead_of_becoming_zero()
    {
        // ⚠️ 全篇最核心的约定：null = "采不到"，0 = "确实是 0"。
        //    CPU 显示 0% 与"采集失败"在运维眼里是两件完全相反的事。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var now = BeijingNo(DateTime.UtcNow).AddMinutes(-2);

        Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(new[]
        {
            new AgentMetricsSampleDto(now, null, null, null, null, null, null, null, null, null),
        }));

        await Telemetry.FlushAsync();

        var series = await admin.GetFromJsonAsync<AgentMetricsSeriesDto>(
            $"/api/agents/{agentId}/metrics?from={Uri.EscapeDataString(Iso(now.AddMinutes(-1)))}" +
            $"&to={Uri.EscapeDataString(Iso(now.AddMinutes(1)))}");

        series!.Points.Should().HaveCount(1);
        series.Points[0].CpuPct.Should().BeNull("采不到就是 null，绝不能用 0 代替");
        series.Points[0].GpuPct.Should().BeNull();
        series.Points[0].MemPct.Should().BeNull();
        series.Points[0].DiskReadBps.Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════
    // 客户端离线的时间段，曲线留空（2026-10-16）
    //
    // 守的是一个**看起来很对、其实会骗人**的画法：空桶被跳过之后，
    // 前后两个点会被折线**直接连起来** —— "关机半小时"于是画成
    // "这段时间一直很稳"的直线，而它比"补 0"更隐蔽（补 0 至少看得出掉下去了）。
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task An_offline_stretch_becomes_a_gap_marker_instead_of_a_straight_line()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync("machine-gap-marker");

        var t0 = BeijingNo(DateTime.UtcNow).AddMinutes(-60);

        var samples = new List<AgentMetricsSampleDto>();

        // 前 100 秒在报 …… 中间空 30 分钟（被控端关机 / 客户端没在跑）…… 之后又回来了。
        for (var i = 0; i < 20; i += 1) samples.Add(Sample(t0.AddSeconds(i * 5), cpu: 40));
        for (var i = 0; i < 20; i += 1) samples.Add(Sample(t0.AddMinutes(30).AddSeconds(i * 5), cpu: 40));

        Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(samples));
        await Telemetry.FlushAsync();

        var from = Iso(t0.AddMinutes(-1));
        var to = Iso(BeijingNo(DateTime.UtcNow));

        // ⚠️ 显式给 `step=30`：这条用例要钉的是"空白怎么标"，不是"分桶挑几秒"。
        var series = await admin.GetFromJsonAsync<AgentMetricsSeriesDto>(
            $"/api/agents/{agentId}/metrics?from={Uri.EscapeDataString(from)}" +
            $"&to={Uri.EscapeDataString(to)}&step=30");

        series!.Source.Should().Be("raw");

        var points = series.Points;
        var index = points.ToList().FindIndex(p => p.IsGap);

        index.Should().BeGreaterThan(0, "断点必须夹在两段真实数据之间 —— 否则它断给谁看");
        index.Should().BeLessThan(points.Count - 1);

        var marker = points[index];

        // 断点**不带任何读数**：带一个 0 就等于说"那段时间 CPU 是 0%"，那是编数据。
        marker.CpuPct.Should().BeNull();
        marker.MemPct.Should().BeNull();
        marker.Devices.Should().BeNull("逐设备曲线也要在同一处断开");
        marker.At.Should().Be(
            points[index - 1].At.AddSeconds(30),
            "断点落在空白开始的那一格上（上一格 + 一格），不是随手编的时刻");

        (points[index + 1].At - points[index - 1].At)
            .Should().BeGreaterThan(TimeSpan.FromMinutes(25), "断点两侧就是那半小时的空白");

        points[0].IsGap.Should().BeFalse("开头不补标记");
        points[points.Count - 1].IsGap.Should().BeFalse("最新那一格永远是真实读数，否则读数条会变成一排 `—`");

        series.GapThresholdSeconds.Should().Be(
            75, "判据要随序列一起下发 —— 前端『往前挪一格』必须用同一个数，否则刷新与不刷新会出现两种画法");
    }


    // ═══════════════════════════════════════════════════════════
    // 温度（M4.3）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Temperatures_round_trip_through_storage()
    {
        // M4.3 是"可选能力"，第一件事就是：真拿到读数时得原样存下去、原样读回来。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var now = BeijingNo(DateTime.UtcNow).AddMinutes(-4);

        Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(new[]
        {
            Sample(now) with { CpuTempC = 61.5, GpuTempC = 48, StorageTempC = 39 },
        }));

        await Telemetry.FlushAsync();

        var series = await admin.GetFromJsonAsync<AgentMetricsSeriesDto>(
            $"/api/agents/{agentId}/metrics?from={Uri.EscapeDataString(Iso(now.AddMinutes(-1)))}" +
            $"&to={Uri.EscapeDataString(Iso(now.AddMinutes(1)))}");

        var point = series!.Points.Single();
        point.CpuTempC.Should().Be(61.5);
        point.GpuTempC.Should().Be(48);
        point.StorageTempC.Should().Be(39);
    }

    [Fact]
    public async Task An_impossible_temperature_is_dropped_to_null()
    {
        // LHM 用 NaN 表示"这次没读到"，而 NaN 一旦落库会污染整段均值、一旦进 JSON 会让整个响应炸掉。
        // 999 ℃ / −273 ℃ 这种则是传感器或驱动坏了。它们都该变成 null（= 没有读数），
        // 而不是原样传下去，更不是变成 0。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var now = BeijingNo(DateTime.UtcNow).AddMinutes(-4);

        Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(new[]
        {
            Sample(now) with
            {
                CpuTempC = double.NaN,
                GpuTempC = double.PositiveInfinity,
                StorageTempC = 999,
            },
        }));

        await Telemetry.FlushAsync();

        var series = await admin.GetFromJsonAsync<AgentMetricsSeriesDto>(
            $"/api/agents/{agentId}/metrics?from={Uri.EscapeDataString(Iso(now.AddMinutes(-1)))}" +
            $"&to={Uri.EscapeDataString(Iso(now.AddMinutes(1)))}");

        var point = series!.Points.Single();
        point.CpuTempC.Should().BeNull("NaN 不是读数");
        point.GpuTempC.Should().BeNull("无穷大不是读数");
        point.StorageTempC.Should().BeNull("999 ℃ 是坏传感器");
    }

    [Fact]
    public async Task Zero_degrees_is_a_real_reading_and_must_not_be_erased()
    {
        // ⚠️ 反向护栏：净化只挡"物理上不可能"的值，**不把 0 当成"没读到"**。
        //    "见 0 就丢"会误伤真实数据（冷库 / 冬天户外机柜），而且属于"替传感器做决定"。
        //    本项目一贯"原样呈现、让异常可见"。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var now = BeijingNo(DateTime.UtcNow).AddMinutes(-4);

        Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(new[]
        {
            Sample(now) with { CpuTempC = 0, StorageTempC = 0 },
        }));

        await Telemetry.FlushAsync();

        var series = await admin.GetFromJsonAsync<AgentMetricsSeriesDto>(
            $"/api/agents/{agentId}/metrics?from={Uri.EscapeDataString(Iso(now.AddMinutes(-1)))}" +
            $"&to={Uri.EscapeDataString(Iso(now.AddMinutes(1)))}");

        var point = series!.Points.Single();
        point.CpuTempC.Should().Be(0, "0 ℃ 是真实读数，不是「没读到」");
        point.StorageTempC.Should().Be(0);
        point.GpuTempC.Should().BeNull("没带 GPU 温度，仍然是 null —— 0 与 null 不能混为一谈");
    }

    [Fact]
    public async Task A_broken_temperature_reading_cannot_take_down_the_online_list()
    {
        // 🔥 这一条守的是"一台坏客户端拖垮整张在线表"。
        //    在线表里每个主机的最新一帧摘要都会被序列化成 JSON，而 System.Text.Json 的
        //    严格模式**拒绝** NaN / Infinity —— 摘要里只要混进一个 NaN，GET /api/agents
        //    就会整个 500，页面上所有机器一起消失。温度恰恰是最容易出这种值的字段。
        //    所以摘要路径必须和存储路径用同一套净化（见 AgentMetricsAggregator.Temperature）。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(new[]
        {
            Sample(BeijingNo(DateTime.UtcNow).AddMinutes(-1)) with
            {
                CpuTempC = double.NaN,
                GpuTempC = double.PositiveInfinity,
                StorageTempC = 999,
            },
        }));

        var response = await admin.GetAsync("/api/agents");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "摘要里的非法温度必须先净化成 null，绝不能让整个在线表序列化失败");

        var list = await response.Content.ReadFromJsonAsync<AgentListDto>();
        var host = list!.Items.Single(i => i.Id == agentId);

        host.Metrics.Should().NotBeNull();
        host.Metrics!.CpuTempC.Should().BeNull("NaN 不是读数");
        host.Metrics.GpuTempC.Should().BeNull("无穷大不是读数");
        host.Metrics.StorageTempC.Should().BeNull("999 ℃ 是坏传感器");
    }

    [Fact]
    public async Task An_unknown_thermal_state_is_dropped_at_ingest_but_a_known_one_round_trips()
    {
        // 热状态是客户端自己上报的（服务端不猜），因此必须过白名单：
        // 一个拼错的状态（或老客户端发的别的值）不能被原样存进清单 ——
        // 页面会拿它去匹配三个分支，匹配不上就只能显示"未知"，等于悄悄丢掉了信息。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var capturedAt = BeijingNo(DateTime.UtcNow);

        var inventory = new AgentInventoryDto(
            capturedAt,
            Cpu: null,
            TotalMemoryMb: null,
            MemoryModules: Array.Empty<AgentMemoryModuleDto>(),
            Volumes: Array.Empty<AgentVolumeDto>(),
            Disks: Array.Empty<AgentDiskDto>(),
            Gpus: Array.Empty<AgentGpuDto>(),
            Displays: Array.Empty<AgentDisplayDto>(),
            OsVersion: null,
            OsBuild: null,
            OsArchitecture: null,
            Domain: null,
            BootTime: null,
            UptimeSeconds: null,
            LoggedOnUser: null,
            PowerEventsSupported: true,
            ThermalState: AgentThermalStates.Failed,
            ThermalNote: "加载硬件温度驱动失败：UnauthorizedAccessException: Access is denied");

        var ack = await Telemetry.AcceptInventoryAsync(agentId, inventory);
        ack.Ok.Should().BeTrue();

        var stored = await admin.GetFromJsonAsync<AgentInventoryEnvelopeDto>(
            $"/api/agents/{agentId}/inventory");

        stored!.Inventory!.ThermalState.Should().Be(AgentThermalStates.Failed);
        stored.Inventory.ThermalNote.Should().Contain("Access is denied", "失败原因必须原样带给运维");

        // 再报一次，这回状态是瞎编的 —— 服务端应当把状态清成 null，而不是存进去。
        var bad = await Telemetry.AcceptInventoryAsync(agentId, inventory with { ThermalState = "kinda-warm" });
        bad.Ok.Should().BeTrue();

        var afterBad = await admin.GetFromJsonAsync<AgentInventoryEnvelopeDto>(
            $"/api/agents/{agentId}/inventory");

        afterBad!.Inventory!.ThermalState.Should().BeNull("非白名单状态一律丢弃");
        afterBad.Inventory.ThermalNote.Should().BeNull("状态不可信时连原因一起清，别留下自相矛盾的一对");
    }

    // ═══════════════════════════════════════════════════════════
    // 逐设备读数（M4.8）
    // ═══════════════════════════════════════════════════════════

    /// <summary>按种类把读数放进对应的那一个字段（其余留 null —— 这正是真实形态）。</summary>
    private static AgentDeviceReadingDto Device(string kind, string key, string name, double? value)
        => kind switch
        {
            AgentDeviceKinds.Disk => new(kind, key, name, ReadBps: value, WriteBps: value),
            AgentDeviceKinds.Net => new(kind, key, name, UpBps: value, DownBps: value),
            AgentDeviceKinds.Gpu => new(kind, key, name, GpuPct: value),
            _ => new(kind, key, name, CpuPct: value),
        };

    [Fact]
    public async Task Device_readings_round_trip_through_storage()
    {
        // 这是需求"多路 CPU / 多块硬盘 / 多块显卡要用具体型号分开显示"的那条链路的底线：
        // 一份多设备读数喂进去，查回来必须还是**同样多的条数、同样的 Key、同样的型号名**。
        // 掉了任何一样，前端画出来的就是"少了一条曲线"或"两条曲线重名"。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var now = BeijingNo(DateTime.UtcNow).AddMinutes(-4);

        Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(new[]
        {
            Sample(now) with
            {
                Devices = new[]
                {
                    Device(AgentDeviceKinds.Cpu, "cpu0", "Intel Xeon Gold 6248R", 11),
                    Device(AgentDeviceKinds.Cpu, "cpu1", "Intel Xeon Gold 6248R", 22),
                    Device(AgentDeviceKinds.Disk, "disk0", "Samsung SSD 980 PRO 1TB", 300),
                    Device(AgentDeviceKinds.Disk, "disk1", "WDC WD40EZAZ-00SF3B0", 400),
                    Device(AgentDeviceKinds.Net, "nic-1", "Intel(R) Ethernet I225-V", 500),
                    Device(AgentDeviceKinds.Gpu, "luid_0x00000000_0x0000D7B1", "GeForce RTX 3080", 7),
                },
            },
        }));

        await Telemetry.FlushAsync();

        var series = await admin.GetFromJsonAsync<AgentMetricsSeriesDto>(
            $"/api/agents/{agentId}/metrics?from={Uri.EscapeDataString(Iso(now.AddMinutes(-1)))}" +
            $"&to={Uri.EscapeDataString(Iso(now.AddMinutes(1)))}");

        var point = series!.Points.Single();
        var devices = point.Devices;

        devices.Should().NotBeNull("落库前带了逐设备读数，查回来就不能没有");

        var list = devices!;
        list.Should().HaveCount(6, "两路 CPU / 两块硬盘 / 一块网卡 / 一块显卡各占一条");

        // Key 是曲线接续的唯一依据 —— 它变了整条历史就断成两截。
        list.Select(d => d.Key).Should().BeEquivalentTo(new[]
        {
            "cpu0", "cpu1", "disk0", "disk1", "nic-1", "luid_0x00000000_0x0000D7B1",
        });

        // 展示名必须原样回来（型号是"分开显示"的关键，不能被服务端改写）。
        list.Single(d => d.Key == "disk1").Name.Should().Be("WDC WD40EZAZ-00SF3B0");

        // 每种设备只填自己那两个字段，其余保持 null —— 不能因为"同一列里"就被串味。
        var nic = list.Single(d => d.Key == "nic-1");
        nic.UpBps.Should().Be(500);
        nic.DownBps.Should().Be(500);
        nic.CpuPct.Should().BeNull("网卡没有 CPU 占用率");
        nic.GpuPct.Should().BeNull("网卡不是显卡");

        list.Single(d => d.Key == "cpu1").CpuPct.Should().Be(22);
        list.Single(d => d.Key == "disk0").ReadBps.Should().Be(300);
    }

    [Fact]
    public async Task A_sample_without_device_readings_keeps_them_null()
    {
        // 老客户端（不上报逐设备明细）走的就是这条路。
        // 必须是 null 而不是空列表：前端靠这个 null 说"该时段没有逐设备数据"，
        // 空列表会被当成"有一组空的硬件"，页面要么什么都不画、要么自己去猜。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var now = BeijingNo(DateTime.UtcNow).AddMinutes(-4);

        Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(new[] { Sample(now) }));

        await Telemetry.FlushAsync();

        var series = await admin.GetFromJsonAsync<AgentMetricsSeriesDto>(
            $"/api/agents/{agentId}/metrics?from={Uri.EscapeDataString(Iso(now.AddMinutes(-1)))}" +
            $"&to={Uri.EscapeDataString(Iso(now.AddMinutes(1)))}");

        series!.Points.Single().Devices.Should().BeNull("没有逐设备读数时是 null，不是空列表");
    }

    [Fact]
    public async Task Unusable_device_readings_are_dropped_at_ingest_without_losing_the_frame()
    {
        // 逐设备明细是"锦上添花"，为了一条坏记录把整帧的 CPU / 内存丢掉不划算 ——
        // 因此这里**整帧照收**，只把不可用的那几条剔掉。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var now = BeijingNo(DateTime.UtcNow).AddMinutes(-4);

        Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(new[]
        {
            Sample(now, cpu: 42.5) with
            {
                Devices = new[]
                {
                    Device(AgentDeviceKinds.Cpu, "cpu0", "好的那条", 10),
                    // ① 种类不在白名单（拼错了 / 老客户端发的别的值）——
                    //    存进去会让页面多出一个永远画不出曲线的分组。
                    new AgentDeviceReadingDto("fan", "fan0", "风扇", CpuPct: 99),
                    // ② Key 全空白 —— 没有稳定身份的东西没法接续历史。
                    new AgentDeviceReadingDto(AgentDeviceKinds.Cpu, "   ", "没有 Key", CpuPct: 99),
                    // ③ 同一帧里 Key 重复 —— 落库后"取哪一条"取决于遍历顺序，那是条会变的曲线。
                    Device(AgentDeviceKinds.Cpu, "cpu0", "重复的 Key", 99),
                },
            },
        }));

        await Telemetry.FlushAsync();

        var series = await admin.GetFromJsonAsync<AgentMetricsSeriesDto>(
            $"/api/agents/{agentId}/metrics?from={Uri.EscapeDataString(Iso(now.AddMinutes(-1)))}" +
            $"&to={Uri.EscapeDataString(Iso(now.AddMinutes(1)))}");

        var point = series!.Points.Single();

        // 整帧的整机指标一根汗毛都没少 —— 这就是"不整批拒收"的证据。
        point.CpuPct.Should().Be(42.5, "坏设备记录不该连累整机指标");

        var devices = point.Devices;
        devices.Should().NotBeNull().And.HaveCount(1, "三条坏记录全被剔掉，只留下合法的那一条");
        devices![0].Key.Should().Be("cpu0");
        devices[0].Name.Should().Be("好的那条", "重复 Key 的第二条是**被丢掉**，不是覆盖第一条");
    }

    [Fact]
    public async Task More_devices_than_the_cap_are_truncated_at_ingest()
    {
        // 上限 64（与客户端 TelemetryCollector.MaxDevicesPerSample 同值）。
        // 这里守的是"截断而不是整帧丢掉"：一台机器暴露上百个计数器是可以想象的，
        // 那时我们宁可少几条曲线，也不能让它的 CPU / 内存历史整段消失。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var now = BeijingNo(DateTime.UtcNow).AddMinutes(-4);

        var many = Enumerable.Range(0, 100)
            .Select(i => Device(AgentDeviceKinds.Cpu, $"cpu{i}", $"物理 CPU {i}", i))
            .ToArray();

        Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(new[] { Sample(now) with { Devices = many } }));

        await Telemetry.FlushAsync();

        var series = await admin.GetFromJsonAsync<AgentMetricsSeriesDto>(
            $"/api/agents/{agentId}/metrics?from={Uri.EscapeDataString(Iso(now.AddMinutes(-1)))}" +
            $"&to={Uri.EscapeDataString(Iso(now.AddMinutes(1)))}");

        series!.Points.Single().Devices.Should().HaveCount(64, "超过上限的部分截掉，保留前 64 条");
    }

    [Fact]
    public async Task A_batch_bigger_than_the_limit_is_rejected_whole()
    {
        // 整批拒收而不是截断：截断会静默丢一半采样点，而"曲线有点稀疏"没人会去查原因。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var now = BeijingNo(DateTime.UtcNow);
        var samples = Enumerable.Range(0, 5000).Select(i => Sample(now.AddSeconds(-i))).ToArray();

        var ack = Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(samples));

        ack.Ok.Should().BeFalse();
        ack.Status.Should().Be("toomany");
    }

    [Fact]
    public async Task An_empty_batch_is_accepted_quietly()
    {
        // 客户端刚连上、还没到第一个采样点时就会发一次空批 —— 不该报错。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var ack = Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(Array.Empty<AgentMetricsSampleDto>()));

        ack.Ok.Should().BeTrue("空批不是错误");
    }

    [Fact]
    public async Task The_newest_frame_reaches_the_online_list_without_waiting_for_a_flush()
    {
        // 热层：页面上的"实时负载"列必须立刻有值，不能等落盘（默认 30 秒）。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(new[]
        {
            Sample(BeijingNo(DateTime.UtcNow), cpu: 66.5),
        }));

        var list = await admin.GetFromJsonAsync<AgentListDto>("/api/agents");
        var host = list!.Items.Single(i => i.Id == agentId);

        host.Metrics.Should().NotBeNull("摄入时同步写进注册表，不等落盘");
        host.Metrics!.CpuPct.Should().Be(66.5);
        host.Metrics.MemPct.Should().Be(50, "内存占用率由 used/total 现算");
    }

    /// <summary>
    /// 🔥 在线表里的那一帧摘要**必须带逐设备读数**（2026-10-13）。
    ///
    /// <para>
    /// 详情页的曲线是这样长的：打开时整段拉一次历史，之后每来一帧摘要就"往前挪一格"。
    /// 摘要不带逐设备的话，挪的只有整机那几条线 —— 硬件选择器上的「当前值」与硬件曲线
    /// 会永远停在**打开页面那一刻**，用户切到某块硬盘时看到的正好是
    /// "整机在飙、这块盘一动不动"。
    /// </para>
    ///
    /// <para>
    /// 这条用例走的是**真链路**（摄入 → 注册表 → <c>GET /api/agents</c> → JSON 序列化），
    /// 因此它同时挡住两件事：摘要在中间被丢掉逐设备字段，
    /// 以及 <c>AgentMetricsSummaryDto</c> 少了这个属性导致序列化时静默不写。
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_live_frame_in_the_online_list_carries_the_per_device_readings()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var sample = Sample(BeijingNo(DateTime.UtcNow), cpu: 66.5) with
        {
            Devices = new[]
            {
                Device(AgentDeviceKinds.Disk, "disk0", "物理磁盘 0（C:）", 1024),
                Device(AgentDeviceKinds.Net, "{943F9CA6}", "Intel(R) Wireless-AC 9560", 2048),
            },
        };

        Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(new[] { sample }));

        var list = await admin.GetFromJsonAsync<AgentListDto>("/api/agents");
        var metrics = list!.Items.Single(i => i.Id == agentId).Metrics;

        metrics.Should().NotBeNull();

        var devices = metrics!.Devices;

        devices.Should().NotBeNull(
            "摘要不带逐设备读数，详情页的硬件曲线就会停在打开页面那一刻");

        // ⚠️ 先落到局部变量再断言：FluentAssertions 的 NotBeNull **不会**让编译器做空值收窄，
        //    直接 `devices!` 然后 `devices.Single(...)` 会立刻报 CS8604（本项目已踩过数次）。
        var list2 = devices!;

        list2.Should().HaveCount(2);
        list2.Select(d => d.Key).Should().BeEquivalentTo(new[] { "disk0", "{943F9CA6}" });
        list2.Single(d => d.Key == "disk0").ReadBps.Should().Be(1024);
    }

    /// <summary>不上报逐设备明细的老客户端：摘要里是 null，而不是空列表（空列表会被画成贴着 0 的直线）。</summary>
    [Fact]
    public async Task An_old_client_without_device_readings_keeps_them_null_in_the_online_list()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(new[]
        {
            Sample(BeijingNo(DateTime.UtcNow), cpu: 11),
        }));

        var list = await admin.GetFromJsonAsync<AgentListDto>("/api/agents");

        list!.Items.Single(i => i.Id == agentId).Metrics!.Devices.Should().BeNull();
    }

    /// <summary>
    /// 🔥 逐设备读数里的 NaN / ±∞ **不能拖垮整张在线表**（2026-10-13 补）。
    ///
    /// <para>
    /// 逐设备读数现在会随"最新一帧摘要"进在线表，而 <c>GET /api/agents</c> 走的是
    /// System.Text.Json 的**严格模式**（默认拒绝 NaN/Infinity）。
    /// 入站不做这道清洗的话，一个坏读数就从"只毁自己那条曲线"升级成
    /// <b>"整张在线表序列化失败"</b> —— 一台坏客户端拖垮所有人的列表页。
    /// </para>
    ///
    /// <para>
    /// 这条用例同时钉住两件事：请求**能返回 200**（没被序列化炸掉），
    /// 以及坏字段被折成 <c>null</c>（= "没采到"），**不是 0**。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_not_a_number_device_reading_cannot_take_down_the_online_list()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var bad = new AgentDeviceReadingDto(
            AgentDeviceKinds.Disk,
            "disk0",
            "物理磁盘 0",
            ReadBps: double.NaN,
            WriteBps: double.PositiveInfinity);

        var ok = new AgentDeviceReadingDto(AgentDeviceKinds.Cpu, "cpu0", "物理 CPU 0", CpuPct: 33.5);

        Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(new[]
        {
            Sample(BeijingNo(DateTime.UtcNow), cpu: 20) with { Devices = new[] { bad, ok } },
        }));

        var response = await admin.GetAsync("/api/agents");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "NaN 进了 JSON 会让整个在线表序列化失败");

        var list = await response.Content.ReadFromJsonAsync<AgentListDto>();
        var devices = list!.Items.Single(i => i.Id == agentId).Metrics!.Devices;

        devices.Should().NotBeNull();

        // ⚠️ 先落到局部变量：FluentAssertions 的 NotBeNull 不做空值收窄，直接链式调用会报 CS8604。
        var list3 = devices!;

        var disk = list3.Single(d => d.Kind == AgentDeviceKinds.Disk);

        disk.ReadBps.Should().BeNull("NaN = 没采到，不是 0");
        disk.WriteBps.Should().BeNull("∞ 同理");

        list3.Single(d => d.Kind == AgentDeviceKinds.Cpu).CpuPct.Should().Be(33.5,
            "同一个帧里正常的那块硬件不受影响 —— 坏记录只剔自己");
    }

    [Fact]
    public async Task A_sample_with_an_absurd_timestamp_is_dropped()
    {
        // 一台时钟错乱的机器能把整张图拉成一根平线。允许 1 天的未来偏差（客户机略快是常态）。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var good = BeijingNo(DateTime.UtcNow);
        var ack = Telemetry.AcceptMetrics(agentId, new AgentMetricsBatchDto(new[]
        {
            Sample(good, cpu: 10),
            Sample(new DateTimeOffset(2001, 1, 1, 0, 0, 0, Beijing), cpu: 99),      // 太旧
            Sample(BeijingNo(DateTime.UtcNow).AddDays(30), cpu: 88),                 // 太新
        }));

        ack.Ok.Should().BeTrue();
        ack.Accepted.Should().Be(1, "只有时间戳合理的样本被接受");
    }

    // ═══════════════════════════════════════════════════════════
    // 开关机记录
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Power_events_are_persisted_and_deduplicated_by_time_and_event_id()
    {
        // ⚠️ 去重是**必须**的：客户端的回溯补扫与增量上报必然重叠
        //    （水位带一个 2 小时的重叠窗口），不去重的话每次重启服务都会多出一批重复记录。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        // 用整秒基准：遥测时间戳以 **Unix 毫秒** 落库（见 TelemetryTime），
        // 亚毫秒部分在往返中会被裁掉 —— 用它做期望值会得到一个"看着像时区错"的假失败。
        var baseAt = new DateTimeOffset(2026, 10, 8, 9, 0, 0, Beijing);

        // 夹具刻意做成**连贯的一天**：
        //   开机 → 4 小时后干净关机 → 1 小时后开机 → 8 小时后意外断电（6008 与 41 各一条）。
        // ⚠️ 顺序很重要：SummarizePower 是"开机与后续终止事件配对"。
        //    若把两条 unexpected 插在 boot 与 shutdown 之间，那段配对被意外关机提前关掉，
        //    平均在线时长就只剩几秒 —— 那不是 bug，是本方法的既定语义。
        var batch = new AgentPowerEventBatchDto(new[]
        {
            new AgentPowerEventDto(baseAt, AgentPowerEventKinds.Boot, 6005, null, "事件日志服务已启动。"),
            new AgentPowerEventDto(baseAt.AddHours(4), AgentPowerEventKinds.Shutdown, 6006, null, "事件日志服务已停止。"),
            new AgentPowerEventDto(baseAt.AddHours(5), AgentPowerEventKinds.Boot, 6005, null, "事件日志服务已启动。"),
            new AgentPowerEventDto(baseAt.AddHours(13), AgentPowerEventKinds.Unexpected, 41, null, "系统已在未先正常关机的情况下重新启动。"),
            new AgentPowerEventDto(baseAt.AddHours(13).AddSeconds(10), AgentPowerEventKinds.Unexpected, 6008, null, "上一次系统的关闭是意外的。"),
        });

        var first = await Telemetry.AcceptPowerEventsAsync(agentId, batch);
        first.Ok.Should().BeTrue();
        first.Accepted.Should().Be(5);

        // 原样再发一次（模拟客户端重连后的重发）。
        var second = await Telemetry.AcceptPowerEventsAsync(agentId, batch);
        second.Ok.Should().BeTrue();
        second.Accepted.Should().Be(0, "同 (时刻, EventId) 的记录应当被去重");

        var result = await admin.GetFromJsonAsync<AgentPowerEventListDto>(
            $"/api/agents/{agentId}/power-events?limit=200");

        result!.Total.Should().Be(5, "重发不该让总数翻倍");
        result.Items.Should().HaveCount(5);
        result.Stats.BootCount.Should().Be(2);
        result.Stats.ShutdownCount.Should().Be(1);
        result.Stats.UnexpectedCount.Should().Be(
            1,
            "6008 与 41 是同一次事故的两条记录 —— 不合并的话这个数字会整整翻一倍");
        result.Stats.LastBootAt.Should().Be(baseAt.AddHours(5));
        result.Stats.AverageUptimeSeconds.Should().Be(
            6 * 3600, "两段在线时长：4 小时（干净关机）+ 8 小时（意外断电）");
    }

    [Fact]
    public async Task A_power_event_batch_bigger_than_the_limit_is_rejected_whole()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var now = BeijingNo(DateTime.UtcNow);
        var events = Enumerable.Range(0, 5000)
            .Select(i => new AgentPowerEventDto(now.AddSeconds(-i), AgentPowerEventKinds.Boot, 6005, null, null))
            .ToArray();

        var ack = await Telemetry.AcceptPowerEventsAsync(agentId, new AgentPowerEventBatchDto(events));

        ack.Ok.Should().BeFalse();
        ack.Status.Should().Be("toomany");
    }

    [Fact]
    public async Task An_empty_power_event_batch_is_accepted_quietly()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var ack = await Telemetry.AcceptPowerEventsAsync(
            agentId, new AgentPowerEventBatchDto(Array.Empty<AgentPowerEventDto>()));

        ack.Ok.Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════
    // 硬件清单
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Inventory_round_trips_and_never_having_reported_is_a_null_captured_at()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        // 先看"从未上报过"的形态。
        var empty = await admin.GetFromJsonAsync<AgentInventoryEnvelopeDto>(
            $"/api/agents/{agentId}/inventory");

        empty!.CapturedAt.Should().BeNull(
            "null = 从未上报过，与「采到了一份空清单」是两件事，页面上文案完全不同");
        empty.Inventory.Should().BeNull();

        var capturedAt = BeijingNo(DateTime.UtcNow);
        var inventory = new AgentInventoryDto(
            capturedAt,
            new AgentCpuInfoDto("Intel(R) Core(TM) i5-8265U CPU @ 1.60GHz", 4, 8, 1800, 1800, "CPU0"),
            8037,
            new[] { new AgentMemoryModuleDto("BANK 0", "Samsung", 4096, 2133, "H9CCNNNBJTALAR-NVD") },
            new[] { new AgentVolumeDto("C:", "系统", "NTFS", 231424, 138240) },
            new[] { new AgentDiskDto(0, "WD Blue SN580 1TB", "E823_8FA6", 932, "Fixed hard disk media") },
            new[] { new AgentGpuDto("Intel(R) UHD Graphics 620", "27.20.100.8984", null, 1024) },
            Array.Empty<AgentDisplayDto>(),
            "Microsoft Windows 10 专业版 (10.0.19045)",
            "19045",
            "64 位",
            "WORKGROUP",
            capturedAt.AddDays(-3),
            260000,
            "IYLONG",
            PowerEventsSupported: true);

        var ack = await Telemetry.AcceptInventoryAsync(agentId, inventory);
        ack.Ok.Should().BeTrue();

        var stored = await admin.GetFromJsonAsync<AgentInventoryEnvelopeDto>(
            $"/api/agents/{agentId}/inventory");

        stored!.CapturedAt.Should().NotBeNull();
        stored.Inventory.Should().NotBeNull();
        stored.Inventory!.Cpu!.Name.Should().Be("Intel(R) Core(TM) i5-8265U CPU @ 1.60GHz");
        stored.Inventory.TotalMemoryMb.Should().Be(8037);
        stored.Inventory.MemoryModules.Should().ContainSingle();
        stored.Inventory.Volumes.Single().Drive.Should().Be("C:");
        stored.Inventory.Gpus.Should().ContainSingle();
        stored.Inventory.PowerEventsSupported.Should().BeTrue("客户端声明本平台支持开关机记录");
    }

    [Fact]
    public async Task Inventory_carries_the_transfer_directory_whitelists()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();
        var capturedAt = BeijingNo(DateTime.UtcNow);

        static AgentInventoryDto Minimal(DateTimeOffset at) => new(
            at,
            Cpu: null,
            TotalMemoryMb: null,
            MemoryModules: Array.Empty<AgentMemoryModuleDto>(),
            Volumes: Array.Empty<AgentVolumeDto>(),
            Disks: Array.Empty<AgentDiskDto>(),
            Gpus: Array.Empty<AgentGpuDto>(),
            Displays: Array.Empty<AgentDisplayDto>(),
            OsVersion: null,
            OsBuild: null,
            OsArchitecture: null,
            Domain: null,
            BootTime: null,
            UptimeSeconds: null,
            LoggedOnUser: null);

        // ① 老客户端没上报这两个字段 ⇒ 读出来必须是 null。
        //    null = "这台机器还不知道这件事"（页面退回手输），与 [] = "本机未启用文件传输"
        //    是**两件事** —— 分发页对它们的提示文案不一样，不能混。
        (await Telemetry.AcceptInventoryAsync(agentId, Minimal(capturedAt))).Ok.Should().BeTrue();

        var legacy = await admin.GetFromJsonAsync<AgentInventoryEnvelopeDto>(
            $"/api/agents/{agentId}/inventory");

        legacy!.Inventory!.TransferInboxDirectories.Should().BeNull();
        legacy.Inventory.TransferOutboxDirectories.Should().BeNull();

        // ② 上报了白名单 ⇒ 原样存、原样取 —— 分发页正是靠它给"目标目录"提候选。
        var withDirs = Minimal(capturedAt) with
        {
            TransferInboxDirectories = new[] { @"D:\dist", @"C:\ProgramData\ControlServer.Agent\inbox" },
            TransferOutboxDirectories = new[] { @"D:\logs" },
        };

        (await Telemetry.AcceptInventoryAsync(agentId, withDirs)).Ok.Should().BeTrue();

        var stored = await admin.GetFromJsonAsync<AgentInventoryEnvelopeDto>(
            $"/api/agents/{agentId}/inventory");

        stored!.Inventory!.TransferInboxDirectories.Should().Equal(
            new[] { @"D:\dist", @"C:\ProgramData\ControlServer.Agent\inbox" },
            "客户端登记的可写目录要原样带回，页面才能照着给它提候选");
        stored.Inventory.TransferOutboxDirectories.Should().Equal(new[] { @"D:\logs" });
    }

    [Fact]
    public async Task Inventory_with_unknown_fields_stays_null_and_does_not_blow_up()
    {
        // 一台采集不到任何硬件的机器（虚拟机 / 权限受限）应当是"一片 —"，而不是 0 或 500。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var ack = await Telemetry.AcceptInventoryAsync(agentId, new AgentInventoryDto(
            BeijingNo(DateTime.UtcNow),
            Cpu: null,
            TotalMemoryMb: null,
            MemoryModules: Array.Empty<AgentMemoryModuleDto>(),
            Volumes: Array.Empty<AgentVolumeDto>(),
            Disks: Array.Empty<AgentDiskDto>(),
            Gpus: Array.Empty<AgentGpuDto>(),
            Displays: Array.Empty<AgentDisplayDto>(),
            OsVersion: null,
            OsBuild: null,
            OsArchitecture: null,
            Domain: null,
            BootTime: null,
            UptimeSeconds: null,
            LoggedOnUser: null,
            PowerEventsSupported: true));

        ack.Ok.Should().BeTrue();

        var stored = await admin.GetFromJsonAsync<AgentInventoryEnvelopeDto>($"/api/agents/{agentId}/inventory");

        stored!.Inventory.Should().NotBeNull();
        stored.Inventory!.Cpu.Should().BeNull();
        stored.Inventory.TotalMemoryMb.Should().BeNull("未知一律 null，不能用 0 冒充");
        stored.Inventory.Volumes.Should().BeEmpty();
    }

    [Fact]
    public async Task Inventory_of_a_null_body_is_accepted_and_changes_nothing()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var ack = await Telemetry.AcceptInventoryAsync(agentId, null);

        ack.Ok.Should().BeTrue("客户端采集失败时会发一个空体，这不是服务端的错");

        var stored = await admin.GetFromJsonAsync<AgentInventoryEnvelopeDto>($"/api/agents/{agentId}/inventory");
        stored!.CapturedAt.Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════
    // 隔离：一台机器的遥测不能串到另一台
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Telemetry_is_scoped_per_agent()
    {
        var admin = await LoginAsync();
        var first = await RegisterOneAsync("machine-telemetry-a");
        var second = await RegisterOneAsync("machine-telemetry-b");

        // 两台不同的机器码 ⇒ 两条独立记录。
        // （⚠️ 用同一个机器码再注册会被服务端当成"同一台机器重装客户端"，返回同一个 Id ——
        //  那正是 M4.1 的有意设计，不是 bug；这里要的显然是两台机器。）
        first.Should().NotBe(second);

        var now = BeijingNo(DateTime.UtcNow).AddMinutes(-1);

        Telemetry.AcceptMetrics(first, new AgentMetricsBatchDto(new[] { Sample(now, cpu: 11) }));
        Telemetry.AcceptMetrics(second, new AgentMetricsBatchDto(new[] { Sample(now, cpu: 22) }));

        await Telemetry.FlushAsync();

        var from = Uri.EscapeDataString(Iso(now.AddMinutes(-1)));
        var to = Uri.EscapeDataString(Iso(now.AddMinutes(1)));

        var a = await admin.GetFromJsonAsync<AgentMetricsSeriesDto>(
            $"/api/agents/{first}/metrics?from={from}&to={to}");
        var b = await admin.GetFromJsonAsync<AgentMetricsSeriesDto>(
            $"/api/agents/{second}/metrics?from={from}&to={to}");

        a!.AgentId.Should().Be(first);
        b!.AgentId.Should().Be(second);
        a.Points.Should().OnlyContain(p => p.CpuPct == 11, "每台机器的曲线只能有自己的数据");
        b.Points.Should().OnlyContain(p => p.CpuPct == 22);
    }

    // ═══════════════════════════════════════════════════════════
    // 工具
    // ═══════════════════════════════════════════════════════════

    /// <summary>把任意时刻换算成一个"北京时间 +08:00"的 <c>DateTimeOffset</c>。</summary>
    private static DateTimeOffset BeijingNo(DateTimeOffset moment) => moment.ToOffset(Beijing);

    /// <summary>ISO8601（带 +08:00 偏移），用于拼查询串。</summary>
    private static string Iso(DateTimeOffset moment) => moment.ToString("yyyy-MM-ddTHH:mm:sszzz");

}
