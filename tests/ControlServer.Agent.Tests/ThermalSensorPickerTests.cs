using ControlServer.Platform.Abstractions;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// 温度挑选策略（<see cref="ThermalSensorPicker"/>）。
///
/// <para>
/// 🔥 <b>为什么这个文件在"AMD 读不到 CPU 温度"之后才出现</b>：原来挑值逻辑写在
/// <c>WindowsThermalSource</c> 里，而测试工程不引 Platform.Windows（引了在 Linux 上跑不起来）
/// ⇒ <b>这段逻辑从来没有被任何测试覆盖过</b>。AMD 的命名差异（CCD / Tctl / Tdie）
/// 与"老 AMD 的 CPU 温度在主板 SuperIO 里"这两件事，正是因为没有测试才一直没人发现。
/// </para>
///
/// <para>
/// 这里钉的是三件事：<b>取哪一项</b>（分级顺序）、<b>什么时候算没读到</b>（值域）、
/// <b>没读到时说得出为什么</b>（诊断文案）。
/// </para>
/// </summary>
public class ThermalSensorPickerTests
{
    // ───────────────────────────────────────────────────────────────────────
    // 造数据
    // ───────────────────────────────────────────────────────────────────────

    private static ThermalSensorReading Cpu(string hardware, string sensor, double value)
        => new(ThermalHardwareKind.Cpu, hardware, sensor, value);

    private static ThermalSensorReading Gpu(string hardware, string sensor, double value)
        => new(ThermalHardwareKind.Gpu, hardware, sensor, value);

    private static ThermalSensorReading Storage(string hardware, string sensor, double value)
        => new(ThermalHardwareKind.Storage, hardware, sensor, value);

    private static ThermalSensorReading Board(string hardware, string sensor, double value)
        => new(ThermalHardwareKind.Motherboard, hardware, sensor, value);

    // ───────────────────────────────────────────────────────────────────────
    // CPU：Intel
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>Intel 的 "CPU Package" 是封装温度，优先于逐核心。</summary>
    [Fact]
    public void Intel_package_wins_over_per_core()
    {
        var pick = ThermalSensorPicker.Pick(new[]
        {
            Cpu("Intel Core i5-8265U", "Core #1", 55),
            Cpu("Intel Core i5-8265U", "CPU Package", 61),
            Cpu("Intel Core i5-8265U", "Core Max", 70),
        });

        pick.CpuC.Should().Be(61);
        pick.CpuSource.Should().Contain("CPU Package");
        pick.CpuNote.Should().BeNull("三项都在，没什么要解释的");
    }

    /// <summary>没有封装温度时，"Core Max" 应先于逐核心平均（它更接近厂商口径）。</summary>
    [Fact]
    public void Intel_core_max_wins_over_core_average_when_no_package()
    {
        var pick = ThermalSensorPicker.Pick(new[]
        {
            Cpu("Intel Core i7", "Core #1", 50),
            Cpu("Intel Core i7", "Core #2", 60),
            Cpu("Intel Core i7", "Core Max", 72),
        });

        // "Core Max" 不含 package/tctl/tdie/average ⇒ 落到逐核心平均那一档。
        // 这是**已知**的行为：Intel 平台几乎总有 Package，缺它的情况不值得再开一档。
        pick.CpuC.Should().BeApproximately((50 + 60 + 72) / 3.0, 0.001);
    }

    // ───────────────────────────────────────────────────────────────────────
    // CPU：AMD Zen
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>AMD Zen 的 "Core (Tctl/Tdie)" 就是"整颗 CPU 的温度"。</summary>
    [Fact]
    public void Amd_tctl_tdie_is_the_cpu_temperature()
    {
        var pick = ThermalSensorPicker.Pick(new[]
        {
            Cpu("AMD Ryzen 7 5800X", "Core (Tctl/Tdie)", 62.5),
            Cpu("AMD Ryzen 7 5800X", "CCD1 (Tdie)", 58),
        });

        pick.CpuC.Should().Be(62.5);
        pick.CpuSource.Should().Contain("Tctl/Tdie");
    }

    /// <summary>
    /// 🔥 <b>回归点</b>：<c>CCD1 (Tdie)</c> 名字里也带 Tdie。
    /// 旧逻辑"含 Tdie 就取"会把它当整颗 CPU（而且会取到第一个 CCD 的裸片温度）。
    /// </summary>
    [Fact]
    public void Amd_ccd_is_not_mistaken_for_the_whole_cpu()
    {
        var pick = ThermalSensorPicker.Pick(new[]
        {
            Cpu("AMD Ryzen Threadripper", "CCD1 (Tdie)", 71),
            Cpu("AMD Ryzen Threadripper", "CCD2 (Tdie)", 79),
        });

        // 没有整颗 CPU 的读数 ⇒ 逐 CCD 平均，而不是"第一个 CCD 的 71"。
        pick.CpuC.Should().Be(75);
        pick.CpuSource.Should().Contain("CCD");
    }

    /// <summary>多 CCD 平台：Max 比 Average 更贴近"最热的核心"，排在它前面。</summary>
    [Fact]
    public void Amd_ccds_max_wins_over_ccds_average()
    {
        var pick = ThermalSensorPicker.Pick(new[]
        {
            Cpu("AMD Ryzen 9 7950X", "CCDs Average (Tdie)", 64),
            Cpu("AMD Ryzen 9 7950X", "CCDs Max (Tdie)", 78),
        });

        pick.CpuC.Should().Be(78);
        pick.CpuSource.Should().Contain("CCDs Max");
    }

    /// <summary>逐核心平均：单核瞬时温度跳动很大，画在曲线上没法看。</summary>
    [Fact]
    public void Amd_per_core_values_are_averaged()
    {
        var pick = ThermalSensorPicker.Pick(new[]
        {
            Cpu("AMD Ryzen 5", "Core #1", 40),
            Cpu("AMD Ryzen 5", "Core #2", 50),
            Cpu("AMD Ryzen 5", "Core #3", 60),
            Cpu("AMD Ryzen 5", "Core #4", 70),
        });

        pick.CpuC.Should().Be(55);
        pick.CpuNote.Should().BeNull();
    }

    // ───────────────────────────────────────────────────────────────────────
    // CPU：老 AMD 走主板 SuperIO
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔥 <b>这一条就是"老 AMD 读不到 CPU 温度"的回归测试</b>：
    /// K10/K15、FM2/AM3 的 CPU 硬件上没有任何温度传感器，读数只在主板的 SuperIO 里。
    /// </summary>
    [Fact]
    public void Old_amd_falls_back_to_motherboard_superio_cpu_sensor()
    {
        var pick = ThermalSensorPicker.Pick(new[]
        {
            Cpu("AMD A10-7850K", "CPU Cores", double.NaN), // 真机上确实会有一个 NaN 占位
            Board("Nuvoton NCT6796D", "System", 33),
            Board("Nuvoton NCT6796D", "CPU Core", 47),
        });

        pick.CpuC.Should().Be(47);
        pick.CpuSource.Should().Contain("主板传感器",
            "SuperIO 测的是插槽附近，必须标出来源，否则会拿它跟厂商软件对不上");
    }

    /// <summary>
    /// 🔥 <b>宁缺勿错</b>：SuperIO 只有 "System" 时不能用它冒充 CPU 温度 ——
    /// 那是一条"常年 33℃"的假曲线，比显示 — 危险得多。
    /// </summary>
    [Fact]
    public void Motherboard_system_sensor_is_never_used_as_cpu()
    {
        var pick = ThermalSensorPicker.Pick(new[]
        {
            Cpu("AMD A10-7850K", "CPU Cores", double.NaN),
            Board("Nuvoton NCT6796D", "System", 33),
            Board("Nuvoton NCT6796D", "Chipset", 41),
        });

        pick.CpuC.Should().BeNull();
        pick.CpuNote.Should().NotBeNull();
    }

    // ───────────────────────────────────────────────────────────────────────
    // 值域
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔥 <b>只挡"不可能是读数"的值</b>：NaN（LHM 的"没读到"）、无穷大、超出 [-40, 150] 的离谱值。
    /// 区间与 <c>AgentMetricsAggregator</c> 的净化规则一致 —— 两边界不同会造成
    /// "采集认为有效、落库被抹成 null"的静默丢失。
    /// </summary>
    [Theory]
    [InlineData(-100)]
    [InlineData(200)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Implausible_values_are_treated_as_missing(double value)
    {
        var pick = ThermalSensorPicker.Pick(new[] { Cpu("AMD Ryzen 7 9700X", "Core (Tctl/Tdie)", value) });

        pick.CpuC.Should().BeNull();
        pick.CpuNote.Should().Contain("无效值",
            "要能区分'没有传感器'和'有传感器但读数不可信' —— 前者换驱动库，后者查驱动");
    }

    /// <summary>
    /// 🔥 <b>0 ℃ 是合法读数</b>（文档 §22.2：冷库 / 冬天户外机柜真的会到 0 ℃）。
    /// "见 0 就丢"是替传感器做决定，而且会把本来可以异常的曲线变成"没有数据"。
    /// </summary>
    [Fact]
    public void Zero_is_a_legal_reading()
    {
        var pick = ThermalSensorPicker.Pick(new[] { Cpu("AMD Ryzen 7 5800X", "Core (Tctl/Tdie)", 0) });

        pick.CpuC.Should().Be(0);
        pick.CpuNote.Should().BeNull();
    }

    /// <summary>GPU 同理：0 ℃ 也是合法读数（户外机柜的显卡在冬天真的会到 0 附近）。</summary>
    [Fact]
    public void Gpu_zero_is_a_legal_reading()
    {
        var pick = ThermalSensorPicker.Pick(new[] { Gpu("AMD Radeon RX 6800", "GPU Core", 0) });

        pick.GpuC.Should().Be(0);
    }

    // ───────────────────────────────────────────────────────────────────────
    // 诊断文案
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>一个 CPU 硬件都没枚举到 ⇒ 指向"驱动没生效"，不是"型号太新"。</summary>
    [Fact]
    public void No_cpu_hardware_points_at_the_driver()
    {
        var pick = ThermalSensorPicker.Pick(new[]
        {
            Board("Nuvoton NCT6796D", "System", 33),
            Storage("Samsung SSD 970", "Temperature", 42),
        });

        pick.CpuC.Should().BeNull();
        pick.CpuNote.Should().Contain("没有 CPU 项");
    }

    /// <summary>
    /// 🔥 有 CPU 硬件、但一个温度传感器都没有 ⇒ <b>带上型号</b>。
    /// 这是 Zen5 这类"比驱动库新"的机器唯一的现场线索：型号 + "没识别出传感器"，
    /// 运维拿着这两样才能判断"该换驱动库"而不是"该重装客户端"。
    /// </summary>
    [Fact]
    public void Cpu_without_sensors_reports_the_model()
    {
        var pick = ThermalSensorPicker.Pick(
            new[] { Storage("Samsung SSD 970", "Temperature", 42) },
            presentCpuHardwareNames: new[] { "AMD Ryzen 7 9700X" });

        pick.CpuC.Should().BeNull();
        pick.CpuNote.Should().Contain("AMD Ryzen 7 9700X");
        pick.CpuNote.Should().Contain("驱动库");
    }

    /// <summary>所有项都齐时不要塞诊断 —— 让 Note 保持 null，页面就不会有额外噪音。</summary>
    [Fact]
    public void Complete_reading_has_no_note()
    {
        var pick = ThermalSensorPicker.Pick(new[]
        {
            Cpu("AMD Ryzen 7 5800X", "Core (Tctl/Tdie)", 45),
            Gpu("NVIDIA GeForce RTX 3060", "GPU Core", 40),
            Storage("Samsung SSD 970", "Temperature", 38),
        });

        pick.CpuC.Should().Be(45);
        pick.GpuC.Should().Be(40);
        pick.StorageC.Should().Be(38);
        pick.CpuNote.Should().BeNull();
    }

    // ───────────────────────────────────────────────────────────────────────
    // GPU / 存储
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>结温比核心温度高 10~20℃+，绝不能被当成"显卡温度"。</summary>
    [Fact]
    public void Gpu_hot_spot_is_the_last_resort()
    {
        var preferred = ThermalSensorPicker.Pick(new[]
        {
            Gpu("AMD Radeon RX 6800", "GPU Hot Spot", 100),
            Gpu("AMD Radeon RX 6800", "GPU Core", 78),
        });

        preferred.GpuC.Should().Be(78);

        var onlyHotSpot = ThermalSensorPicker.Pick(new[]
        {
            Gpu("AMD Radeon RX 6800", "GPU Hot Spot", 100),
        });

        onlyHotSpot.GpuC.Should().Be(100, "没有核心温度时只能给结温，但只在没有更优来源时");
    }

    /// <summary>多块盘取最高 —— 平均会把一块 70℃ 的盘藏在一块 30℃ 的盘后面。</summary>
    [Fact]
    public void Storage_picks_the_hottest_disk()
    {
        var pick = ThermalSensorPicker.Pick(new[]
        {
            Storage("Samsung SSD 970", "Temperature", 38),
            Storage("WD Blue", "Temperature", 52),
            Storage("Seagate HDD", "Temperature", 29),
        });

        pick.StorageC.Should().Be(52);
    }

    /// <summary>空输入不是错误：这台机器就是没有温度可读。</summary>
    [Fact]
    public void Empty_input_returns_empty_pick()
    {
        var pick = ThermalSensorPicker.Pick(Array.Empty<ThermalSensorReading>());

        pick.CpuC.Should().BeNull();
        pick.GpuC.Should().BeNull();
        pick.StorageC.Should().BeNull();
        pick.CpuNote.Should().NotBeNull();
    }
}
