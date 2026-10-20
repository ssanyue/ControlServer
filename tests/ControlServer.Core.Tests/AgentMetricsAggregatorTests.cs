using ControlServer.Contracts;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// <see cref="AgentMetricsAggregator"/> 的纯函数用例（M4.2）。
/// <para>
/// 这个类里的算法是"曲线画得对不对"的唯一裁决者，而它们全部跑在服务端、依赖库与时钟。
/// 做成纯函数就是为了能在这里逐条钉住 —— 下面每一条断言背后都有一个**具体的错法**：
/// 把 null 当 0 会让"采集坏了"看起来像"机器很闲"；空桶补 0 会画出一条假的掉底曲线；
/// 6008 与 41 不合并会让意外关机次数整整翻一倍。
/// </para>
/// </summary>
public sealed class AgentMetricsAggregatorTests
{
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);

    private static DateTimeOffset At(int hour, int minute = 0, int second = 0)
        => new(2026, 10, 8, hour, minute, second, Beijing);

    private static AgentMetricsSampleDto Sample(
        DateTimeOffset at,
        double? cpu = null,
        long? memUsed = null,
        long? memTotal = null,
        double? gpu = null,
        double? cpuTemp = null,
        double? gpuTemp = null,
        double? storageTemp = null)
        => new(at, cpu, memUsed, memTotal, null, null, null, null, gpu, null, cpuTemp, gpuTemp, storageTemp);

    // ═══════════════════════════════════════════════════════════
    // 内存占用率
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Memory_percent_is_null_when_either_side_is_missing()
    {
        AgentMetricsAggregator.MemoryPercent(null, 8000).Should().BeNull("没采到已用量时不能编一个数出来");
        AgentMetricsAggregator.MemoryPercent(4000, null).Should().BeNull("没采到总量时同样不能编");
    }

    [Fact]
    public void Memory_percent_is_null_when_total_is_zero()
    {
        // ⚠️ 总量为 0 不是"占用 0%"，而是"这条数据本身是坏的"。
        // 返回 0 会把它画成"机器很闲"，正好与事实相反。
        AgentMetricsAggregator.MemoryPercent(0, 0).Should().BeNull();
        AgentMetricsAggregator.MemoryPercent(4096, 0).Should().BeNull();
        AgentMetricsAggregator.MemoryPercent(4096, -1).Should().BeNull();
    }

    [Fact]
    public void Memory_percent_is_computed_and_clamped_to_0_100()
    {
        AgentMetricsAggregator.MemoryPercent(4000, 8000).Should().Be(50);
        AgentMetricsAggregator.MemoryPercent(8000, 8000).Should().Be(100);

        // 超过 100 说明采集口径有问题（例如把文件缓存也算进已用）。
        // 夹住比让曲线飞出画布更容易被发现 —— 这是**有意的**，不是"顺手 clamp"。
        AgentMetricsAggregator.MemoryPercent(12000, 8000).Should().Be(100);
        AgentMetricsAggregator.MemoryPercent(-1, 8000).Should().Be(0);
    }

    [Fact]
    public void Summarize_derives_memory_percent_from_used_and_total()
    {
        var summary = AgentMetricsAggregator.Summarize(Sample(At(10), cpu: 33.5, memUsed: 6000, memTotal: 8000));

        summary.Should().NotBeNull();
        summary!.CpuPct.Should().Be(33.5);
        summary.MemPct.Should().Be(75);
        summary.MemUsedMb.Should().Be(6000);
        summary.MemTotalMb.Should().Be(8000);
    }

    [Fact]
    public void Summarize_of_null_is_null()
        => AgentMetricsAggregator.Summarize(null).Should().BeNull("没有样本就没有摘要，不能编一个空的");

    /// <summary>
    /// 🔥 摘要必须**原样带出**逐设备读数（2026-10-13 补）。
    ///
    /// <para>
    /// 这是"详情页的硬件曲线会不会动"的唯一开关：页面打开时整段拉一次历史，
    /// 之后每来一帧摘要就"往前挪一格"。摘要不带逐设备的话，挪的只有整机那几条线 ——
    /// 用户切到某块硬盘，看到的正好是"整机在飙、这块盘一动不动"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 用 <c>BeSameAs</c> 而不是 <c>Equal</c>：要钉的是"透传"（同一个引用），
    /// 而不是"内容恰好一样"。中间若有人做了一次深拷贝或过滤，
    /// 内容可能仍然相等，但那意味着多了一份"可能被改坏的副本"。
    /// </para>
    /// </summary>
    [Fact]
    public void Summarize_carries_the_per_device_readings_through()
    {
        var devices = new[]
        {
            Cpu("cpu0", "物理 CPU 0", 12.5),
            Disk("disk0", "物理磁盘 0（C:）", 1024, 2048),
        };

        var summary = AgentMetricsAggregator.Summarize(DevicesAt(At(10), devices));

        summary.Should().NotBeNull();
        summary!.Devices.Should().BeSameAs(devices, "摘要不带逐设备读数，详情页的硬件曲线就会停在打开页面那一刻");
    }

    /// <summary>没有逐设备读数的样本（老客户端 / 明细缺失），摘要里也必须是 null —— 而不是空列表。</summary>
    [Fact]
    public void Summarize_keeps_devices_null_when_the_sample_has_none()
    {
        var summary = AgentMetricsAggregator.Summarize(Sample(At(10), cpu: 20));

        summary.Should().NotBeNull();
        summary!.Devices.Should().BeNull("null 表达「这一帧没带逐设备数据」，空列表会被渲染成一条贴着 0 的直线");
    }

    // ═══════════════════════════════════════════════════════════
    // 分桶粒度
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Choose_bucket_returns_a_step_from_the_fixed_ladder()
    {
        // 只返回"整齐"的粒度：页面上说"每 5 分钟一个点"远比"每 287 秒一个点"好解释。
        var ladder = new[] { 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400 };

        foreach (var span in new[]
                 {
                     TimeSpan.FromMinutes(1), TimeSpan.FromHours(1), TimeSpan.FromHours(6),
                     TimeSpan.FromHours(24), TimeSpan.FromDays(7), TimeSpan.FromDays(30),
                     TimeSpan.FromDays(365),
                 })
        {
            AgentMetricsAggregator.ChooseBucketSeconds(span).Should().BeOneOf(ladder);
        }
    }

    [Fact]
    public void Choose_bucket_is_monotonic_in_span()
    {
        var previous = 0;

        foreach (var span in new[]
                 {
                     TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), TimeSpan.FromHours(6),
                     TimeSpan.FromHours(24), TimeSpan.FromDays(7), TimeSpan.FromDays(30),
                     TimeSpan.FromDays(365),
                 })
        {
            var bucket = AgentMetricsAggregator.ChooseBucketSeconds(span);

            bucket.Should().BeGreaterThanOrEqualTo(previous, "窗口越长，粒度只能越粗");
            previous = bucket;
        }

        previous.Should().Be(86400, "一年的窗口最终会落到「每天一个点」");
    }

    [Fact]
    public void Choose_bucket_of_an_empty_or_negative_span_falls_back_to_the_finest_step()
    {
        AgentMetricsAggregator.ChooseBucketSeconds(TimeSpan.Zero).Should().Be(5);
        AgentMetricsAggregator.ChooseBucketSeconds(TimeSpan.FromHours(-3)).Should().Be(5);
        AgentMetricsAggregator.ChooseBucketSeconds(TimeSpan.FromHours(1), maxPoints: 0).Should().Be(5);
    }

    [Fact]
    public void Choose_bucket_keeps_points_under_the_cap()
    {
        const int maxPoints = 720;
        var span = TimeSpan.FromDays(30);

        var bucket = AgentMetricsAggregator.ChooseBucketSeconds(span, maxPoints);

        (span.TotalSeconds / bucket).Should().BeLessThanOrEqualTo(maxPoints, "粒度选粗的目的就是不让点数超上限");
    }

    // ═══════════════════════════════════════════════════════════
    // 分桶降采样
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Bucketize_of_nothing_is_nothing()
    {
        AgentMetricsAggregator
            .Bucketize(Array.Empty<AgentMetricsSampleDto>(), At(0), At(1), 60)
            .Should().BeEmpty();
    }

    [Fact]
    public void Bucketize_anchors_the_first_bucket_on_the_window_start()
    {
        var from = At(10, 0, 0);
        var to = from.AddMinutes(10);
        var samples = new[] { Sample(from, cpu: 10), Sample(from.AddSeconds(90), cpu: 20) };

        var points = AgentMetricsAggregator.Bucketize(samples, from, to, bucketSeconds: 60);

        points.Should().NotBeEmpty();
        points[0].At.Should().Be(from, "桶边界锚在查询窗口左界，图形才与查询条件对得上");
        points[0].CpuPct.Should().Be(10);
        points[1].At.Should().Be(from.AddMinutes(1));
        points[1].CpuPct.Should().Be(20);
    }

    [Fact]
    public void Bucketize_ignores_null_values_instead_of_treating_them_as_zero()
    {
        // ⚠️ 这是全篇最重要的一条：一台机器 3 个采样点，其中 1 个 CPU 采不到，
        //    平均值必须是另外 2 个的均值（30），**不是** (10 + 50 + 0) / 3 = 20。
        //    "当成 0"会把"采集失败"显示成"机器突然闲下来"，运维会去追一个不存在的事件。
        var from = At(10, 0, 0);
        var samples = new[]
        {
            Sample(from, cpu: 10),
            Sample(from.AddSeconds(10), cpu: null),
            Sample(from.AddSeconds(20), cpu: 50),
        };

        var points = AgentMetricsAggregator.Bucketize(samples, from, from.AddMinutes(1), bucketSeconds: 60);

        points.Should().HaveCount(1);
        points[0].CpuPct.Should().Be(30, "null 不参与均值，而不是当 0 参与");
    }

    [Fact]
    public void Bucketize_leaves_a_bucket_null_when_every_value_is_missing()
    {
        var from = At(10, 0, 0);
        var samples = new[] { Sample(from, cpu: null, gpu: null) };

        var points = AgentMetricsAggregator.Bucketize(samples, from, from.AddMinutes(1), bucketSeconds: 60);

        points.Should().HaveCount(1);
        points[0].CpuPct.Should().BeNull("整桶都采不到就老老实实是 null，前端据此断线");
        points[0].GpuPct.Should().BeNull();
    }

    [Fact]
    public void Bucketize_does_not_invent_points_for_gaps()
    {
        // 中间空了一大段（机器关机）应该**什么都不画**。
        // 补 0 会画出一条"掉到 0 又上来"的假曲线 —— 那正是"看起来像真的有事件"的假象。
        var from = At(10, 0, 0);
        var samples = new[]
        {
            Sample(from, cpu: 10),
            Sample(from.AddMinutes(30), cpu: 20),
        };

        var points = AgentMetricsAggregator.Bucketize(samples, from, from.AddHours(1), bucketSeconds: 60);

        points.Should().HaveCount(2, "空桶直接跳过，不补 0");
        points[0].At.Should().Be(from);
        points[1].At.Should().Be(from.AddMinutes(30));
    }

    [Fact]
    public void Bucketize_drops_samples_outside_the_window()
    {
        var from = At(10, 0, 0);
        var to = At(11, 0, 0);
        var samples = new[]
        {
            Sample(from.AddMinutes(-5), cpu: 99),  // 早于窗口
            Sample(from.AddMinutes(5), cpu: 11),
            Sample(to.AddMinutes(5), cpu: 98),     // 晚于窗口
        };

        var points = AgentMetricsAggregator.Bucketize(samples, from, to, bucketSeconds: 60);

        points.Should().HaveCount(1);
        points[0].CpuPct.Should().Be(11);
    }

    [Fact]
    public void Bucketize_averages_disk_network_and_gpu_per_bucket()
    {
        var from = At(10, 0, 0);
        var samples = new[]
        {
            new AgentMetricsSampleDto(from, 10, 1000, 2000, 100, 200, 300, 400, 5, 3600),
            new AgentMetricsSampleDto(from.AddSeconds(10), 20, 2000, 2000, 300, 400, 500, 600, 15, 3610),
        };

        var points = AgentMetricsAggregator.Bucketize(samples, from, from.AddMinutes(1), bucketSeconds: 60);

        points.Should().HaveCount(1);
        points[0].CpuPct.Should().Be(15);
        points[0].DiskReadBps.Should().Be(200);
        points[0].DiskWriteBps.Should().Be(300);
        points[0].NetUpBps.Should().Be(400);
        points[0].NetDownBps.Should().Be(500);
        points[0].GpuPct.Should().Be(10);
        points[0].MemUsedMb.Should().Be(1500);
        points[0].MemPct.Should().Be(75, "内存占用率由本桶的均值 used/total 现算");
    }

    [Fact]
    public void Bucketize_output_is_sorted_even_when_input_is_not()
    {
        var from = At(10, 0, 0);
        var samples = new[]
        {
            Sample(from.AddMinutes(2), cpu: 30),
            Sample(from, cpu: 10),
            Sample(from.AddMinutes(1), cpu: 20),
        };

        var points = AgentMetricsAggregator.Bucketize(samples, from, from.AddMinutes(5), bucketSeconds: 60);

        points.Select(p => p.CpuPct).Should().Equal(new double?[] { 10, 20, 30 }, "纯函数不该假设调用方排过序");
    }

    [Fact]
    public void Bucketize_with_a_non_positive_bucket_falls_back_to_the_finest_step()
    {
        var from = At(10, 0, 0);
        var samples = new[] { Sample(from, cpu: 10), Sample(from.AddSeconds(100), cpu: 20) };

        var points = AgentMetricsAggregator.Bucketize(samples, from, from.AddMinutes(5), bucketSeconds: 0);

        points.Should().HaveCount(2, "非法粒度退回 5 秒，而不是除零或把点全塞进一个桶");
    }

    // ═══════════════════════════════════════════════════════════
    // 逐设备（M4.8）
    // ═══════════════════════════════════════════════════════════

    /// <summary>只带逐设备读数的样本（整机指标全 null，免得测试被无关字段干扰）。</summary>
    private static AgentMetricsSampleDto DevicesAt(DateTimeOffset at, params AgentDeviceReadingDto[] devices)
        => new(at, null, null, null, null, null, null, null, null, null, null, null, null, devices);

    private static AgentDeviceReadingDto Cpu(string key, string name, double? pct)
        => new(AgentDeviceKinds.Cpu, key, name, CpuPct: pct);

    private static AgentDeviceReadingDto Disk(string key, string name, double? read, double? write)
        => new(AgentDeviceKinds.Disk, key, name, ReadBps: read, WriteBps: write);

    private static AgentDeviceReadingDto Net(string key, string name, double? up, double? down)
        => new(AgentDeviceKinds.Net, key, name, UpBps: up, DownBps: down);

    private static AgentDeviceReadingDto Gpu(string key, string name, double? pct)
        => new(AgentDeviceKinds.Gpu, key, name, GpuPct: pct);

    /// <summary>
    /// 🔥 一个 <c>NaN</c> 不能毒化整桶（2026-10-13 补）。
    ///
    /// <para>
    /// 求和的写法最容易踩这个：<c>_sum += NaN</c> 之后 <c>_sum</c> 就永远是 NaN，
    /// 于是**这一桶**的均值、以及画出来的整条曲线全是 NaN。
    /// 而 NaN 进了 JSON 会让 <c>GET /api/agents</c>（System.Text.Json 严格模式）**整个序列化失败** ——
    /// 一台坏客户端能把所有人的列表页拖垮。
    /// </para>
    ///
    /// <para>
    /// 语义与"没采到"一致：把非有限值当成**没有读数**（不计入均值），而不是"读数是 0"。
    /// </para>
    /// </summary>
    [Fact]
    public void A_not_a_number_device_reading_cannot_poison_the_bucket()
    {
        var from = At(10, 0, 0);

        var samples = new[]
        {
            DevicesAt(from, Disk("disk0", "物理磁盘 0", 100, double.NaN)),
            DevicesAt(from.AddSeconds(10), Disk("disk0", "物理磁盘 0", 300, 40)),
            DevicesAt(from.AddSeconds(20), Disk("disk0", "物理磁盘 0", double.PositiveInfinity, 60)),
        };

        var point = AgentMetricsAggregator.Bucketize(samples, from, from.AddMinutes(1), bucketSeconds: 60).Single();

        var disk = point.Devices!.Single(d => d.Kind == AgentDeviceKinds.Disk);

        disk.ReadBps.Should().Be(200, "NaN / ∞ 当成没有读数，不参与求平均");
        disk.WriteBps.Should().Be(50, "另外两个有效读数照常平均");
    }

    /// <summary>同样的纪律适用于整机指标 —— <c>Series</c> 是两者共用的一路。</summary>
    [Fact]
    public void A_not_a_number_machine_reading_cannot_poison_the_bucket()
    {
        var from = At(10, 0, 0);

        var samples = new[]
        {
            Sample(from, cpu: 10, memUsed: 1000, memTotal: 2000),
            Sample(from.AddSeconds(10), cpu: double.NaN, memUsed: 3000, memTotal: 4000),
        };

        var point = AgentMetricsAggregator.Bucketize(samples, from, from.AddMinutes(1), bucketSeconds: 60).Single();

        point.CpuPct.Should().Be(10, "坏读数被忽略，而不是把均值变成 NaN");
        point.MemUsedMb.Should().Be(2000);
    }

    [Fact]
    public void Bucketize_averages_each_device_on_its_own_key()
    {
        // 双路机器：cpu0 一直 10%，cpu1 一直 30%。合起来平均是 20 ——
        // 如果按"种类"聚合而不是按"块"聚合，两条曲线会被揉成一条，多路机器就白采了。
        var from = At(10, 0, 0);

        var samples = new[]
        {
            DevicesAt(from, Cpu("cpu0", "物理 CPU 0", 10), Cpu("cpu1", "物理 CPU 1", 30)),
            DevicesAt(from.AddSeconds(10), Cpu("cpu0", "物理 CPU 0", 20), Cpu("cpu1", "物理 CPU 1", 40)),
        };

        var points = AgentMetricsAggregator.Bucketize(samples, from, from.AddMinutes(1), bucketSeconds: 60);

        points.Should().HaveCount(1);

        var devices = points[0].Devices;
        devices.Should().NotBeNull();
        devices!.Should().HaveCount(2);

        devices!.Single(d => d.Key == "cpu0").CpuPct.Should().Be(15);
        devices!.Single(d => d.Key == "cpu1").CpuPct.Should().Be(35);
    }

    [Fact]
    public void Bucketize_keeps_two_devices_of_the_same_kind_apart()
    {
        // 两块硬盘：读/写各算各的。这是需求原文"多个硬盘要用具体型号分开显示"的最小可验证形态。
        var from = At(10, 0, 0);

        var samples = new[]
        {
            DevicesAt(from, Disk("disk0", "disk0", 100, 200), Disk("disk1", "disk1", 1000, 2000)),
            DevicesAt(from.AddSeconds(5), Disk("disk0", "disk0", 300, 400), Disk("disk1", "disk1", 3000, 4000)),
        };

        var points = AgentMetricsAggregator.Bucketize(samples, from, from.AddMinutes(1), bucketSeconds: 60);
        var devices = points[0].Devices!;

        devices.Should().HaveCount(2);
        devices.Single(d => d.Key == "disk0").ReadBps.Should().Be(200);
        devices.Single(d => d.Key == "disk0").WriteBps.Should().Be(300);
        devices.Single(d => d.Key == "disk1").ReadBps.Should().Be(2000);
        devices.Single(d => d.Key == "disk1").WriteBps.Should().Be(3000);
    }

    [Fact]
    public void Bucketize_orders_devices_by_kind_then_by_key()
    {
        // 输出顺序不取决于输入顺序 —— 否则同一台机器每次查询的图例顺序都在跳。
        // 期望顺序就是 AgentDeviceKinds.All 的声明顺序：处理器 → 磁盘 → 网卡 → 显卡。
        var from = At(10, 0, 0);

        var samples = new[]
        {
            DevicesAt(
                from,
                Gpu("luid_0x0_0x1", "GPU 适配器 1", 30),
                Disk("disk0", "disk0", 1, 2),
                Cpu("cpu0", "物理 CPU 0", 10),
                Net("nic-b", "网卡 B", 3, 4)),
        };

        var points = AgentMetricsAggregator.Bucketize(samples, from, from.AddMinutes(1), bucketSeconds: 60);

        points[0].Devices!.Select(d => d.Kind).Should().Equal(
            new[] { AgentDeviceKinds.Cpu, AgentDeviceKinds.Disk, AgentDeviceKinds.Net, AgentDeviceKinds.Gpu },
            "按 AgentDeviceKinds.All 的展示顺序输出");
    }

    [Fact]
    public void Bucketize_leaves_devices_null_when_no_sample_carries_them()
    {
        // 老客户端（不上报逐设备）与"这一帧没采到"都走这条路。
        // null 而不是空列表：前端要能说"该时段没有逐设备数据"，而不是画一条空线。
        var from = At(10, 0, 0);
        var samples = new[] { Sample(from, cpu: 10), Sample(from.AddSeconds(5), cpu: 20) };

        var points = AgentMetricsAggregator.Bucketize(samples, from, from.AddMinutes(1), bucketSeconds: 60);

        points[0].Devices.Should().BeNull("整桶一条逐设备读数都没有时必须是 null，不是空列表");
    }

    [Fact]
    public void Bucketize_drops_a_device_that_is_missing_from_the_last_sample_within_the_bucket()
    {
        // 同一桶里某块硬件中途消失（例如热插拔）时，它只能按"出现过的那些样本"算均值，
        // 不能因为最后一帧没有它就把它抹掉 —— 那会让"刚拔掉的硬盘"在曲线上凭空消失。
        var from = At(10, 0, 0);

        var samples = new[]
        {
            DevicesAt(from, Disk("disk0", "disk0", 100, 100), Disk("disk1", "disk1", 500, 500)),
            DevicesAt(from.AddSeconds(5), Disk("disk0", "disk0", 300, 300)),
        };

        var points = AgentMetricsAggregator.Bucketize(samples, from, from.AddMinutes(1), bucketSeconds: 60);
        var devices = points[0].Devices!;

        devices.Should().HaveCount(2);
        devices.Single(d => d.Key == "disk0").ReadBps.Should().Be(200, "两个样本的均值");
        devices.Single(d => d.Key == "disk1").ReadBps.Should().Be(500, "只有一个样本，就是它自己");
    }

    [Fact]
    public void Bucketize_uses_the_last_name_seen_for_a_device()
    {
        // Key 不变而 Name 变了（换了驱动 / 重命名网卡）：曲线必须**接上**，
        // 图例上显示最近一次见到的名字。这条正是"Key 不能用显示名"的反面证据 ——
        // 若拿 Name 当键，这里会变成两条各只有一个样本的半截曲线。
        var from = At(10, 0, 0);

        var samples = new[]
        {
            DevicesAt(from, Net("nic-1", "Realtek 老名字", 1, 1)),
            DevicesAt(from.AddSeconds(5), Net("nic-1", "Realtek 新名字", 3, 3)),
        };

        var points = AgentMetricsAggregator.Bucketize(samples, from, from.AddMinutes(1), bucketSeconds: 60);
        var devices = points[0].Devices!;

        devices.Should().HaveCount(1, "Key 相同就是同一块硬件，名字变了也不该拆成两条");
        devices[0].Name.Should().Be("Realtek 新名字");
        devices[0].UpBps.Should().Be(2);
    }

    [Fact]
    public void Bucketize_skips_devices_with_a_blank_kind_or_key()
    {
        // 防御：上游清洗漏了的时候跳过，而不是抛 —— 一条坏读数不该让整张图取不出来。
        var from = At(10, 0, 0);

        var samples = new[]
        {
            DevicesAt(
                from,
                Cpu("cpu0", "物理 CPU 0", 10),
                new AgentDeviceReadingDto(AgentDeviceKinds.Cpu, string.Empty, "没有 Key", CpuPct: 99),
                new AgentDeviceReadingDto(string.Empty, "no-kind", "没有种类", CpuPct: 99)),
        };

        var points = AgentMetricsAggregator.Bucketize(samples, from, from.AddMinutes(1), bucketSeconds: 60);

        points[0].Devices!.Should().HaveCount(1);
        points[0].Devices![0].Key.Should().Be("cpu0");
    }

    [Fact]
    public void To_point_passes_devices_through_untouched()
    {
        // 单点换算不做任何聚合加工 —— 它拿到的就是"某一刻的读数"。
        var at = At(10, 0, 0);
        var sample = DevicesAt(at, Cpu("cpu0", "物理 CPU 0", 42));

        var point = AgentMetricsAggregator.ToPoint(sample);

        point.Devices.Should().BeSameAs(sample.Devices, "逐设备列表原样透传，不复制也不重算");
    }

    [Fact]
    public void Daily_to_points_carries_no_per_device_readings()
    {
        // 日聚合表里没有 DevicesJson（一天一行，逐设备存不下也没人看），
        // 因此查 90 天时日聚合点上的 Devices 必须是 null ——
        // 前端据此提示"该时段只有整机聚合"，而不是画一条空线。
        var rows = new[]
        {
            new AgentMetricsDaily(new DateOnly(2026, 10, 8), 100, 500, 10, 20, 1000, 2000, 4000, 5, 6, 7, 8, 9, null, null, null, null),
        };

        var points = AgentMetricsAggregator.DailyToPoints(rows);

        points.Should().ContainSingle();
        points[0].Devices.Should().BeNull("日聚合层不聚合逐设备读数");
    }

    // ═══════════════════════════════════════════════════════════
    // 空白 → 断点标记（客户端离线时曲线留空，2026-10-16）
    //
    // 这一组用例守的是一个**看起来很对、其实会骗人**的画法：
    // 空桶被跳过之后，前后两个点会被折线**直接连起来** ——
    // "客户端关机两小时"于是画成一条"这段时间一直很稳"的直线。
    // ═══════════════════════════════════════════════════════════

    /// <summary>造一个"有数据"的点（<paramref name="cpu"/> 有值，其余为 null —— 形状不重要，位置才重要）。</summary>
    private static AgentMetricsPointDto PointAt(DateTimeOffset at, double? cpu = 10)
        => new(at, cpu, null, null, null, null, null, null, null, null, null);

    [Fact]
    public void Gap_threshold_is_the_offline_judgement_by_default()
    {
        // 5 秒一格、离线判定 75 秒 ⇒ 门槛是 75 秒（15 格），不是 2 格。
        // 取 2 格的话，网络抖动丢一批样本就会在曲线上抠出一个豁口。
        AgentMetricsAggregator
            .GapThreshold(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(75))
            .Should().Be(TimeSpan.FromSeconds(75));
    }

    [Fact]
    public void Gap_threshold_never_drops_below_two_buckets()
    {
        // 粗粒度窗口（例如 30 天窗按 1 小时一格）下，"少一格"本来就意味着空了整整一小时。
        // 若还按 75 秒判，图上的豁口会多到看不出哪段才是真的离线。
        AgentMetricsAggregator
            .GapThreshold(TimeSpan.FromHours(1), TimeSpan.FromSeconds(75))
            .Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public void Gap_threshold_falls_back_when_the_offline_window_is_nonsense()
    {
        // 配置被写成 0 / 负数时用 75 秒兜底，而不是"任何两个点都算断"。
        AgentMetricsAggregator
            .GapThreshold(TimeSpan.FromSeconds(5), TimeSpan.Zero)
            .Should().Be(TimeSpan.FromSeconds(75));

        AgentMetricsAggregator
            .GapThreshold(TimeSpan.Zero, TimeSpan.FromSeconds(75))
            .Should().Be(TimeSpan.FromSeconds(75));
    }

    [Fact]
    public void Mark_gaps_leaves_a_dense_series_untouched()
    {
        // 正常连着的一段：**一个点都不许加**（多一个全 null 的点就会被当成"采不到"）。
        var points = new[]
        {
            PointAt(At(10, 0, 0)),
            PointAt(At(10, 0, 30)),
            PointAt(At(10, 1, 0)),
        };

        var marked = AgentMetricsAggregator.MarkGaps(points, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(75));

        marked.Should().BeSameAs(points, "没有空白时零改动、零分配");
    }

    [Fact]
    public void Mark_gaps_breaks_a_long_hole_with_one_null_marker()
    {
        // 现场那一幕：10:00 还在报，之后关机两小时，12:00 又回来了。
        var points = new[]
        {
            PointAt(At(10, 0, 0), cpu: 40),
            PointAt(At(12, 0, 0), cpu: 40),
        };

        var marked = AgentMetricsAggregator.MarkGaps(points, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(75));

        marked.Should().HaveCount(3);
        marked[1].IsGap.Should().BeTrue("中间插一个标记点，折线才会在那里断开");
        marked[1].CpuPct.Should().BeNull("标记点绝不能带任何读数 —— 带一个 0 就是编数据");
        marked[1].At.Should().Be(At(10, 0, 30), "标记落在空白真正开始的那一格上（上一格 + 一格）");

        // 前后两个真实点原样保留（顺序与值都不动）。
        marked[0].Should().BeSameAs(points[0]);
        marked[2].Should().BeSameAs(points[1]);
    }

    [Fact]
    public void Mark_gaps_ignores_a_hole_shorter_than_the_threshold()
    {
        // 5 秒采样下偶尔丢一批样本是网络的正常抖动（客户端断线重连期间还会把样本攒进下一批）。
        // 45 秒的空白离 75 秒的门槛还差得远 ⇒ 不断线，否则曲线上会布满小豁口。
        var points = new[]
        {
            PointAt(At(10, 0, 0)),
            PointAt(At(10, 0, 45)),
        };

        var marked = AgentMetricsAggregator.MarkGaps(points, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(75));

        marked.Should().BeSameAs(points, "短于门槛的空白不插标记");
    }

    [Fact]
    public void Mark_gaps_keeps_the_latest_point_a_real_reading()
    {
        // 窗口**结尾**的空白不需要（也不能）标记：那里本来就没有线可以画错。
        // ⚠️ 末尾一旦插上标记点，读数条的"最新"就变成一整排 `—`，
        //    看着像"这台机器此刻什么都没采到"。
        var points = new[]
        {
            PointAt(At(10, 0, 0)),
            PointAt(At(12, 0, 0)),
        };

        var marked = AgentMetricsAggregator.MarkGaps(points, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(75));

        marked[^1].IsGap.Should().BeFalse("最后一个点永远是真实读数");
        marked[^1].Should().BeSameAs(points[^1]);
        marked[0].Should().BeSameAs(points[0], "开头也不补标记");
    }

    [Fact]
    public void Mark_gaps_marks_every_hole_separately()
    {
        // 一天里掉线两次 ⇒ 两个断点，而且必须**按时间排好**。
        // 只插第一个的话，第二段离线仍会被一条直线接过去。
        var points = new[]
        {
            PointAt(At(8, 0, 0)),
            PointAt(At(8, 0, 30)),
            PointAt(At(10, 0, 0)),
            PointAt(At(12, 0, 0)),
        };

        var marked = AgentMetricsAggregator.MarkGaps(points, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(75));

        marked.Should().HaveCount(6);
        marked.Where(p => p.IsGap).Should().HaveCount(2);
        marked.Select(p => p.At).Should().BeInAscendingOrder("断点也必须按时间排好，否则折线会乱画");
    }

    [Fact]
    public void Mark_gaps_is_idempotent()
    {
        // 本函数在"明细段"与"日聚合段"上各要跑一次（两段粒度不同、判据也不同）。
        // 幂等是安全网：将来若有人把它挪到更外层、让同一段数据流过两次，
        // 结果不该变成"每次多一个标记点"。
        var points = new[]
        {
            PointAt(At(10, 0, 0)),
            PointAt(At(12, 0, 0)),
        };

        var once = AgentMetricsAggregator.MarkGaps(points, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(75));
        var twice = AgentMetricsAggregator.MarkGaps(once, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(75));

        twice.Should().HaveCount(once.Count);
        twice.Where(p => p.IsGap).Should().HaveCount(1);
    }

    [Fact]
    public void Mark_gaps_treats_a_missing_day_as_a_hole_at_daily_granularity()
    {
        // 日聚合段：点落在每天 00:00，缺了一整天 ⇒ 间隔变成 2 天。
        // 门槛是 max(75 秒, 2 天) ⇒ 正好断在这里；连着两天都有数据的不会被断。
        var points = new[]
        {
            PointAt(At(0, 0, 0)),
            PointAt(At(0, 0, 0).AddDays(2)),
        };

        var marked = AgentMetricsAggregator.MarkGaps(points, TimeSpan.FromDays(1), TimeSpan.FromSeconds(75));

        marked.Should().HaveCount(3);
        marked[1].IsGap.Should().BeTrue();
        marked[1].At.Should().Be(At(0, 0, 0).AddDays(1), "标记落在缺失的那一天上");
    }

    [Fact]
    public void Merging_the_daily_and_raw_layers_does_not_treat_the_seam_as_a_hole()
    {
        // 长窗口是这么取数的：老的一段读**日聚合**（每天 00:00 一个点），最近一段读明细。
        // 接缝两侧天然隔了将近一天（老一段的最后一点在 rawFrom 前一天 00:00）。
        //
        // ⚠️ 若把两段拼起来再按一个判据判空白，**每一次查询**都会在接缝处插一个假断点：
        //    图上一条永远存在的豁口，还会随"现在几点"左右滑动 —— 看着像离线，其实什么都没发生。
        var dayA = At(0, 0, 0);
        var rawFrom = dayA.AddDays(1).AddHours(20); // 次日 20:00 起走明细

        var daily = new[] { PointAt(dayA) };
        var recent = new[] { PointAt(rawFrom), PointAt(rawFrom.AddSeconds(30)) };

        var merged = AgentMetricsAggregator.MergeLayers(
            daily,
            recent,
            TimeSpan.FromDays(1),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(75));

        // 整段只该有一个**假**空白候选（接缝），而它必须被放过。
        merged.Should().HaveCount(3, "接缝处不该插断点");
        merged.Should().NotContain(p => p.IsGap);
        merged.Select(p => p.At).Should().BeInAscendingOrder("两段交错，合并后必须按时间排好");
    }

    [Fact]
    public void Merging_the_layers_still_marks_a_hole_inside_either_layer()
    {
        // 反向护栏：接缝要放过，但**两段各自内部的真空白**必须照断 ——
        // 否则"查长窗口时离线就不留空了"，而那正是这条需求要的地方。
        var dayA = At(0, 0, 0);

        var daily = new[]
        {
            PointAt(dayA),
            PointAt(dayA.AddDays(3)), // 中间缺了两整天
        };

        var rawFrom = dayA.AddDays(10);
        var recent = new[]
        {
            PointAt(rawFrom),
            PointAt(rawFrom.AddHours(4)), // 明细段里空了 4 小时
        };

        var merged = AgentMetricsAggregator.MergeLayers(
            daily,
            recent,
            TimeSpan.FromDays(1),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(75));

        merged.Where(p => p.IsGap).Should().HaveCount(2, "日聚合段与明细段各有一处真空白");
        merged.Select(p => p.At).Should().BeInAscendingOrder();
    }

    // ═══════════════════════════════════════════════════════════
    // 日聚合
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Aggregate_daily_estimates_online_seconds_from_sample_count()
    {
        var samples = Enumerable.Range(0, 100)
            .Select(i => Sample(At(0, 0, 0).AddSeconds(i * 5), cpu: 10))
            .ToArray();

        var row = AgentMetricsAggregator.AggregateDaily(new DateOnly(2026, 10, 8), samples, sampleIntervalSeconds: 5);

        row.SampleCount.Should().Be(100);
        row.OnlineSeconds.Should().Be(500, "在线时长是 采样条数 × 采样间隔 的**估算**");
    }

    [Fact]
    public void Aggregate_daily_caps_online_seconds_at_one_day()
    {
        // 采样间隔配错（比如配成 0，会走默认 5 秒）时不能让"在线时长"超过一天。
        var samples = Enumerable.Range(0, 30_000)
            .Select(i => Sample(At(0, 0, 0).AddSeconds(i), cpu: 1))
            .ToArray();

        var row = AgentMetricsAggregator.AggregateDaily(new DateOnly(2026, 10, 8), samples, sampleIntervalSeconds: 5);

        row.OnlineSeconds.Should().Be(86_400, "一天的上限是硬上限");
    }

    [Fact]
    public void Aggregate_daily_keeps_peak_and_ignores_nulls()
    {
        var samples = new[]
        {
            Sample(At(0), cpu: 10, memUsed: 4000, memTotal: 8000),
            Sample(At(1), cpu: null, memUsed: null, memTotal: null),
            Sample(At(2), cpu: 70, memUsed: 6000, memTotal: 8000),
        };

        var row = AgentMetricsAggregator.AggregateDaily(new DateOnly(2026, 10, 8), samples, sampleIntervalSeconds: 5);

        row.CpuAvgPct.Should().Be(40, "均值只除「有值的个数」（2），不是 3");
        row.CpuMaxPct.Should().Be(70, "峰值是当天最高的一刻，不是均值");
        row.MemAvgMb.Should().Be(5000);
        row.MemMaxMb.Should().Be(6000);
    }

    [Fact]
    public void Aggregate_daily_of_an_empty_day_is_all_null()
    {
        var row = AgentMetricsAggregator.AggregateDaily(new DateOnly(2026, 10, 8), Array.Empty<AgentMetricsSampleDto>(), 5);

        row.SampleCount.Should().Be(0);
        row.OnlineSeconds.Should().Be(0);
        row.CpuAvgPct.Should().BeNull("那天一条都没采到，就不能给出任何平均值");
        row.CpuMaxPct.Should().BeNull();
        row.MemAvgMb.Should().BeNull();
        row.GpuAvgPct.Should().BeNull();
    }

    [Fact]
    public void Aggregate_daily_with_a_zero_interval_falls_back_to_five_seconds()
    {
        var samples = new[] { Sample(At(0), cpu: 10) };

        var row = AgentMetricsAggregator.AggregateDaily(new DateOnly(2026, 10, 8), samples, sampleIntervalSeconds: 0);

        row.OnlineSeconds.Should().Be(5);
    }

    [Fact]
    public void Daily_to_points_anchors_every_day_at_beijing_midnight()
    {
        var rows = new[]
        {
            new AgentMetricsDaily(new DateOnly(2026, 10, 5), 100, 500, 12, 40, 4000, 6000, 8000, 1, 2, 3, 4, 5),
        };

        var points = AgentMetricsAggregator.DailyToPoints(rows);

        points.Should().HaveCount(1);
        points[0].At.Should().Be(new DateTimeOffset(2026, 10, 5, 0, 0, 0, Beijing), "日聚合的点统一落在当天 00:00（北京时间）");
        points[0].At.Offset.Should().Be(Beijing, "偏移必须是 +08:00 —— 前端按截字符串显示，落成 UTC 会少 8 小时");
        points[0].CpuPct.Should().Be(12);
        points[0].MemPct.Should().Be(50);
        points[0].UptimeSeconds.Should().Be(100, "日聚合用「当天采样条数」近似 UptimeSeconds");
    }

    // ═══════════════════════════════════════════════════════════
    // 开关机统计
    // ═══════════════════════════════════════════════════════════

    private static AgentPowerEventDto Power(DateTimeOffset at, string kind, int? eventId = null, string? detail = null)
        => new(at, kind, eventId, null, detail);

    [Fact]
    public void Power_stats_of_nothing_is_all_zero_and_no_timestamps()
    {
        var stats = AgentMetricsAggregator.SummarizePower(Array.Empty<AgentPowerEventDto>());

        stats.BootCount.Should().Be(0);
        stats.ShutdownCount.Should().Be(0);
        stats.UnexpectedCount.Should().Be(0);
        stats.BsodCount.Should().Be(0);
        stats.AverageUptimeSeconds.Should().BeNull("一次完整的开机→关机都没有，就没有「平均在线时长」可言");
        stats.LastBootAt.Should().BeNull();
        stats.LastShutdownAt.Should().BeNull();
    }

    [Fact]
    public void Power_stats_pairs_a_boot_with_its_shutdown()
    {
        var stats = AgentMetricsAggregator.SummarizePower(new[]
        {
            Power(At(9, 0), AgentPowerEventKinds.Boot, 6005),
            Power(At(18, 30), AgentPowerEventKinds.Shutdown, 6006),
        });

        stats.BootCount.Should().Be(1);
        stats.ShutdownCount.Should().Be(1);
        stats.AverageUptimeSeconds.Should().Be(9.5 * 3600, "在线时长 = 关机时刻 − 开机时刻");
        stats.LastBootAt.Should().Be(At(9, 0));
        stats.LastShutdownAt.Should().Be(At(18, 30));
    }

    [Fact]
    public void Power_stats_does_not_count_a_trailing_boot_as_uptime()
    {
        // ⚠️ 这台机器现在还开着（只有开机、没有关机）。
        //    把它算进去的话，"平均在线时长"会随着开机时间越来越长而无限增长 —— 明显荒谬。
        var stats = AgentMetricsAggregator.SummarizePower(new[]
        {
            Power(At(9, 0), AgentPowerEventKinds.Boot, 6005),
            Power(At(18, 30), AgentPowerEventKinds.Shutdown, 6006),
            Power(At(20, 0), AgentPowerEventKinds.Boot, 6005),
        });

        stats.BootCount.Should().Be(2);
        stats.AverageUptimeSeconds.Should().Be(9.5 * 3600, "没有配对的最后一次开机不计入平均");
        stats.LastBootAt.Should().Be(At(20, 0));
    }

    [Fact]
    public void Power_stats_collapses_6008_and_41_into_one_unexpected_event()
    {
        // 🔥 一次意外断电必然**同时**产生 6008 与 41（实测本机：两者相差 8 秒）。
        //    不合并的话，"意外关机次数"会整整翻一倍，而这个数字是运维最看重的那个。
        var stats = AgentMetricsAggregator.SummarizePower(new[]
        {
            Power(At(18, 43, 41), AgentPowerEventKinds.Unexpected, 41),
            Power(At(18, 43, 49), AgentPowerEventKinds.Unexpected, 6008),
        });

        stats.UnexpectedCount.Should().Be(1, "同一次事故的两条记录只能算一次");
    }

    [Fact]
    public void Power_stats_counts_two_genuinely_separate_unexpected_events()
    {
        var stats = AgentMetricsAggregator.SummarizePower(new[]
        {
            Power(At(9, 8), AgentPowerEventKinds.Unexpected, 6008),
            Power(At(18, 30), AgentPowerEventKinds.Unexpected, 6008),
        });

        stats.UnexpectedCount.Should().Be(2, "相隔几小时就是两次事故，不能被合并窗口吃掉");
    }

    [Fact]
    public void Power_stats_ignores_sleep_and_wake_for_pairing_and_counts()
    {
        // 休眠期间机器并没有"下线"。把 42/107 算成关机，会让"每日开关机次数"翻倍，
        // 而运维真正关心的是"它是不是被频繁重启"。
        var stats = AgentMetricsAggregator.SummarizePower(new[]
        {
            Power(At(9, 0), AgentPowerEventKinds.Boot, 6005),
            Power(At(12, 0), AgentPowerEventKinds.Sleep, 42),
            Power(At(13, 0), AgentPowerEventKinds.Wake, 107),
            Power(At(18, 0), AgentPowerEventKinds.Shutdown, 6006),
        });

        stats.BootCount.Should().Be(1);
        stats.ShutdownCount.Should().Be(1);
        stats.AverageUptimeSeconds.Should().Be(9 * 3600, "睡眠不切断配对，在线时长应跨越睡眠段");
    }

    [Fact]
    public void Power_stats_treats_bsod_and_restart_as_closing_the_uptime()
    {
        var stats = AgentMetricsAggregator.SummarizePower(new[]
        {
            Power(At(1, 0), AgentPowerEventKinds.Boot, 6005),
            Power(At(3, 0), AgentPowerEventKinds.Bsod, 1001),
            Power(At(3, 5), AgentPowerEventKinds.Boot, 6005),
            Power(At(5, 5), AgentPowerEventKinds.Restart, 1074),
        });

        stats.BootCount.Should().Be(2);
        stats.BsodCount.Should().Be(1);
        stats.RestartCount.Should().Be(1);
        stats.AverageUptimeSeconds.Should().Be(2 * 3600, "两段各 2 小时");
        stats.LastShutdownAt.Should().Be(At(5, 5), "计划重启也应当刷新「最后一次关机」");
    }

    [Fact]
    public void Power_stats_ignores_a_shutdown_without_a_preceding_boot()
    {
        var stats = AgentMetricsAggregator.SummarizePower(new[]
        {
            Power(At(18, 30), AgentPowerEventKinds.Shutdown, 6006),
        });

        stats.ShutdownCount.Should().Be(1);
        stats.AverageUptimeSeconds.Should().BeNull("没有起点就配不出时长，不能从「0 点」开始算");
    }

    [Fact]
    public void Power_stats_is_order_independent()
    {
        var input = new[]
        {
            Power(At(18, 30), AgentPowerEventKinds.Shutdown, 6006),
            Power(At(9, 0), AgentPowerEventKinds.Boot, 6005),
        };

        AgentMetricsAggregator.SummarizePower(input).AverageUptimeSeconds
            .Should().Be(9.5 * 3600, "纯函数必须自己排序，不能依赖调用方给的顺序");
    }

    [Fact]
    public void Power_stats_ignores_unknown_kinds()
    {
        var stats = AgentMetricsAggregator.SummarizePower(new[]
        {
            Power(At(9, 0), "something-new-from-a-future-client"),
        });

        stats.BootCount.Should().Be(0);
        stats.ShutdownCount.Should().Be(0);
        (stats.AverageUptimeSeconds is null || stats.AverageUptimeSeconds >= 0)
            .Should().BeTrue("未知种类既不配对也不计数 —— 老服务端遇到新客户端不能崩，也不能乱算");
    }

    // ═══════════════════════════════════════════════════════════
    // 端到端口径：本机实测那 19 条记录的形态
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Power_stats_on_the_realistic_machine_shape_yields_five_unexpected()
    {
        // 复刻 2026-10-12 在本机读到的真实形态（5 组 6005 + 6008 + 41）。
        // 每组事故是 6008 与 41 各一条 ⇒ 5 组必须统计成 **5** 次，而不是 10 次。
        // ⚠️ 五组必须在**不同的一天**：相邻两组若落在 10 分钟内，合并窗口会把它们算成一次
        //    （这条用例第一版就踩了 —— 夹具写成 {5,6,7,7,8} 有两个重复日期 ⇒ 得到 4 而不是 5）。
        var events = new List<AgentPowerEventDto>();

        foreach (var offset in new[] { 0, 1, 2, 3, 4 })
        {
            var boot = At(9, 0).AddDays(offset);
            events.Add(Power(boot, AgentPowerEventKinds.Boot, 6005));
            events.Add(Power(boot.AddSeconds(2), AgentPowerEventKinds.Unexpected, 41));
            events.Add(Power(boot.AddSeconds(10), AgentPowerEventKinds.Unexpected, 6008));
        }

        var stats = AgentMetricsAggregator.SummarizePower(events);

        stats.BootCount.Should().Be(5);
        stats.UnexpectedCount.Should().Be(5, "5 次事故 × 2 条记录 → 5 次");
        stats.BsodCount.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════
    // 温度（M4.3）—— 语义与其它序列完全一致，但错法更隐蔽
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 温度在摘要里<b>原样透传</b>，且"没有"就是 null。
    /// <para>
    /// 这条挡的是"顺手给个默认值"：一旦温度在摘要里被写成 <c>?? 0</c>，
    /// 页面上"未启用温度监控"就会显示成 <c>0 ℃</c> —— 一个看起来完全正常的读数。
    /// </para>
    /// </summary>
    [Fact]
    public void Summary_passes_temperatures_through_without_defaulting_to_zero()
    {
        var withTemps = AgentMetricsAggregator.Summarize(
            Sample(At(10), cpu: 20, cpuTemp: 61.5, gpuTemp: 44.25, storageTemp: 38));

        withTemps!.CpuTempC.Should().Be(61.5);
        withTemps.GpuTempC.Should().Be(44.25);
        withTemps.StorageTempC.Should().Be(38);

        var withoutTemps = AgentMetricsAggregator.Summarize(Sample(At(10), cpu: 20));

        withoutTemps!.CpuTempC.Should().BeNull("未启用温度监控时必须是 null，不能是 0");
        withoutTemps.GpuTempC.Should().BeNull();
        withoutTemps.StorageTempC.Should().BeNull();
    }

    /// <summary>
    /// 温度净化：只挡"物理上不可能"的值。
    /// <para>
    /// 🔥 这套规则被<b>存储</b>与<b>在线表摘要</b>两条路径共用，原因不只是"好看"：
    /// 一个 <c>NaN</c> 混进在线表摘要，整个 <c>GET /api/agents</c> 都会序列化失败
    /// （System.Text.Json 严格模式拒绝 NaN/Infinity）——
    /// 那是"一台坏客户端让所有机器从页面上消失"。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(999)]
    [InlineData(-273)]
    [InlineData(-40.5)]
    [InlineData(150.5)]
    public void An_impossible_temperature_is_scrubbed_to_null(double value)
    {
        AgentMetricsAggregator.Temperature(value).Should().BeNull();
    }

    /// <summary>
    /// ⚠️ 反向护栏：<b>0 ℃ 与区间的两个端点都是合法读数</b>，不许被抹掉。
    /// "见 0 就丢"会误伤真实数据（冷库 / 冬天户外机柜），而且属于"替传感器做决定"。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-40)]
    [InlineData(150)]
    [InlineData(61.5)]
    public void A_plausible_temperature_survives_untouched(double value)
    {
        AgentMetricsAggregator.Temperature(value).Should().Be(value);
    }

    [Fact]
    public void A_missing_temperature_stays_missing()
    {
        AgentMetricsAggregator.Temperature(null).Should().BeNull();
    }

    /// <summary>
    /// 分桶均值：<b>null 不参与，而不是当 0</b>。
    /// <para>
    /// 三个采样点里只有一个有温度 ⇒ 均值就是那一个的值。
    /// 若把另外两个当 0，温度会被稀释成 20 ℃ —— 一台正在满载 80 ℃ 的机器看起来凉快得很。
    /// </para>
    /// </summary>
    [Fact]
    public void Bucketize_averages_only_the_samples_that_have_a_temperature()
    {
        var from = At(10);

        var points = AgentMetricsAggregator.Bucketize(
            new[]
            {
                Sample(from, cpu: 10, cpuTemp: 80),
                Sample(from.AddSeconds(10), cpu: 20),
                Sample(from.AddSeconds(20), cpu: 30),
            },
            from,
            from.AddMinutes(5),
            bucketSeconds: 300);

        points.Should().ContainSingle();
        points[0].CpuTempC.Should().Be(80, "只有 1 个点有读数，均值就是它 —— 不能拿 null 当 0 拉低");
    }

    /// <summary>
    /// 整桶都没有温度 ⇒ 该桶的温度是 null（曲线在那里断开）。
    /// <para>
    /// 这是"未启用温度监控"的机器唯一正确的画法：温度线整条是空的。
    /// 画成 0 ℃ 的平线会让人以为"机器一直很凉快" —— 与事实相反。
    /// </para>
    /// </summary>
    [Fact]
    public void Bucketize_leaves_temperature_null_when_nothing_in_the_bucket_has_one()
    {
        var from = At(10);

        var points = AgentMetricsAggregator.Bucketize(
            new[] { Sample(from, cpu: 10), Sample(from.AddSeconds(5), cpu: 20) },
            from,
            from.AddMinutes(5),
            bucketSeconds: 300);

        points.Should().ContainSingle();
        points[0].CpuPct.Should().Be(15, "CPU 本身照常聚合");
        points[0].CpuTempC.Should().BeNull("温度整桶没有读数 ⇒ null（不是 0）");
        points[0].StorageTempC.Should().BeNull();
    }

    /// <summary>一条温度都没有的一天：日聚合的温度字段必须是 null，不是 0。</summary>
    [Fact]
    public void Daily_aggregate_keeps_temperatures_null_when_the_driver_was_off_all_day()
    {
        var day = new DateOnly(2026, 10, 8);

        var row = AgentMetricsAggregator.AggregateDaily(
            day,
            new[] { Sample(At(9), cpu: 10), Sample(At(10), cpu: 90) },
            sampleIntervalSeconds: 5);

        row.CpuTempAvgC.Should().BeNull("整天没启用温度 ⇒ 均值是 null，不能是 0");
        row.CpuTempMaxC.Should().BeNull("峰值同理 —— 0 ℃ 会被读成'这台机器最凉'");
        row.GpuTempAvgC.Should().BeNull();

        // 同一份输入里 CPU 占用率照常有值：温度缺席不该影响别的序列。
        row.CpuAvgPct.Should().Be(50);
    }

    /// <summary>
    /// 日聚合的<b>峰值</b>是温度在冷层里最有用的一个数 —— "这台机器这周最高烧到多少度"。
    /// </summary>
    [Fact]
    public void Daily_aggregate_reports_the_temperature_peak_separately_from_the_average()
    {
        var row = AgentMetricsAggregator.AggregateDaily(
            new DateOnly(2026, 10, 8),
            new[]
            {
                Sample(At(9), cpuTemp: 40, gpuTemp: 30, storageTemp: 35),
                Sample(At(10), cpuTemp: 95, gpuTemp: 80, storageTemp: 55),
                Sample(At(11), cpuTemp: 60, gpuTemp: 50, storageTemp: 45),
            },
            sampleIntervalSeconds: 5);

        row.CpuTempAvgC.Should().Be(65);
        row.CpuTempMaxC.Should().Be(95, "峰值必须单独保留 —— 均值会把一次过热抹平");
        row.GpuTempAvgC.Should().BeApproximately(53.333, 0.01);
        row.StorageTempAvgC.Should().Be(45);
    }

    /// <summary>
    /// 日聚合折成图上的点时，三个温度要分别落到对应的三个字段上
    /// （写错顺序不会有编译错误 —— 它们全是 <c>double?</c>，这正是这条用例存在的理由）。
    /// </summary>
    [Fact]
    public void Daily_points_map_each_temperature_to_its_own_field()
    {
        var row = new AgentMetricsDaily(
            new DateOnly(2026, 10, 8),
            SampleCount: 100,
            OnlineSeconds: 500,
            CpuAvgPct: 30,
            CpuMaxPct: 90,
            MemAvgMb: 4000,
            MemMaxMb: 6000,
            MemTotalMb: 8000,
            NetDownAvgBps: null,
            NetUpAvgBps: null,
            DiskReadAvgBps: null,
            DiskWriteAvgBps: null,
            GpuAvgPct: 20,
            CpuTempAvgC: 71,
            CpuTempMaxC: 88,
            GpuTempAvgC: 52,
            StorageTempAvgC: 39);

        var points = AgentMetricsAggregator.DailyToPoints(new[] { row });

        points.Should().ContainSingle();
        points[0].CpuTempC.Should().Be(71, "日聚合点用的是当天均值（不是峰值）");
        points[0].GpuTempC.Should().Be(52);
        points[0].StorageTempC.Should().Be(39);
    }
}
