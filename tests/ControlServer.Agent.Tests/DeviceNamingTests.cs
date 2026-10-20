using ControlServer.Agent.Telemetry;
using ControlServer.Contracts;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// 逐设备读数<b>命名</b>的规矩（<see cref="TelemetryCollector.BetterName"/> 是纯函数，可直接钉）。
///
/// <para>
/// 这条规矩只有一句话：<b>平台层已经给出真型号时，不许再拿硬件清单去覆盖它</b>。
/// </para>
///
/// <para>
/// 🔥 <b>为什么它值得一个文件</b>：2026-10-14 之前，显卡型号在平台层是拿不到的（PDH 只给 LUID），
/// 所以这里有一条"只有一块显卡时才拿清单里那一条来补"的退路。等平台层能按 LUID 从
/// DXGI / D3DKMT 问到型号之后，那条退路就<b>从"补救"变成了"覆盖"</b> ——
/// 而覆盖是**降级**：清单里的名字是"这台机器装了哪些显卡"的列表，
/// 逐设备名字是"这一条 LUID 对应哪块卡"的事实，后者更具体。
/// </para>
///
/// <para>
/// 这个变化不会让任何测试变红，也不会报错，只表现为"曲线上的名字悄悄换了一个"。
/// 所以必须显式钉住。
/// </para>
/// </summary>
public class DeviceNamingTests
{
    private static AgentDeviceReadingDto Gpu(string key, string name)
        => new(AgentDeviceKinds.Gpu, key, name, GpuPct: 12.5);

    private static AgentDeviceReadingDto Disk(string key, string name)
        => new(AgentDeviceKinds.Disk, key, name, ReadBps: 100, WriteBps: 200);

    private static AgentDeviceReadingDto Cpu(string key, string name)
        => new(AgentDeviceKinds.Cpu, key, name, CpuPct: 30);

    private static AgentInventoryDto Inventory(
        string? cpu = null,
        string? disk0 = null,
        string? gpu0 = null,
        string? gpu1 = null)
    {
        var gpus = new List<AgentGpuDto>();

        if (gpu0 is not null) gpus.Add(new AgentGpuDto(gpu0, null, null, null));
        if (gpu1 is not null) gpus.Add(new AgentGpuDto(gpu1, null, null, null));

        return new AgentInventoryDto(
            DateTimeOffset.UnixEpoch,
            cpu is null ? null : new AgentCpuInfoDto(cpu, 4, 8, 1800, 1800, "CPU0"),
            8192,
            Array.Empty<AgentMemoryModuleDto>(),
            Array.Empty<AgentVolumeDto>(),
            disk0 is null ? Array.Empty<AgentDiskDto>() : new[] { new AgentDiskDto(0, disk0, null, 512, null) },
            gpus,
            Array.Empty<AgentDisplayDto>(),
            "Windows", "19045", "64 位", "WORKGROUP",
            null, null, null);
    }

    private static Dictionary<string, int> Counts(params AgentDeviceReadingDto[] devices)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var d in devices) counts[d.Kind] = counts.GetValueOrDefault(d.Kind) + 1;

        return counts;
    }

    // ═══════════════════════════════════════════════════════════════
    // 显卡：平台层已给型号 ⇒ 不许覆盖（需求②，2026-10-14）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void A_real_gpu_model_from_the_platform_is_never_overwritten_by_the_inventory()
    {
        var device = Gpu("luid_0x00000000_0x00010976", "NVIDIA GeForce MX250");

        var inventory = Inventory(gpu0: "Intel(R) UHD Graphics 620");

        // 只有一块显卡（最容易"顺手覆盖"的那种情形）。
        var name = TelemetryCollector.BetterName(device, inventory, Counts(device));

        name.Should().Be("NVIDIA GeForce MX250",
            "平台层按 LUID 问到的型号是逐块准确的；清单里那条只是「这台机器有哪些显卡」的列表");
    }

    [Fact]
    public void Multi_gpu_machines_keep_every_platform_model()
    {
        var a = Gpu("luid_0x00000000_0x0000E55E", "Intel(R) UHD Graphics 620");
        var b = Gpu("luid_0x00000000_0x00010976", "NVIDIA GeForce MX250");

        var counts = Counts(a, b);
        var inventory = Inventory(gpu0: "Intel(R) UHD Graphics 620", gpu1: "NVIDIA GeForce MX250");

        TelemetryCollector.BetterName(a, inventory, counts).Should().Be("Intel(R) UHD Graphics 620");
        TelemetryCollector.BetterName(b, inventory, counts).Should().Be("NVIDIA GeForce MX250",
            "多卡机器上，两块卡各自那一条必须是**各自**的型号（这正是需求要的效果）");
    }

    /// <summary>
    /// 平台层拿不到型号时（两条路都失败）仍然退回占位名，并且这时才允许用清单兜底 ——
    /// 但只要清单里不是唯一一块，就<b>宁可留占位名</b>。
    /// </summary>
    [Fact]
    public void A_placeholder_is_filled_from_the_inventory_only_when_the_gpu_is_the_only_one()
    {
        var only = Gpu("luid_0x00000000_0x0000D7B1", "GPU 适配器 1");

        TelemetryCollector.BetterName(only, Inventory(gpu0: "Intel(R) UHD Graphics 620"), Counts(only))
            .Should().Be("Intel(R) UHD Graphics 620",
                "全机只有一块显卡时，清单里那一条就是它 —— 这时兜底没有猜的成分");

        var a = Gpu("luid_0x00000000_0x0000E55E", "GPU 适配器 1");
        var b = Gpu("luid_0x00000000_0x00010976", "GPU 适配器 2");

        var counts = Counts(a, b);
        var inventory = Inventory(gpu0: "Intel(R) UHD Graphics 620", gpu1: "NVIDIA GeForce MX250");

        TelemetryCollector.BetterName(a, inventory, counts).Should().Be("GPU 适配器 1",
            "多卡 + 拿不到 LUID 型号时，清单顺序与计数器顺序并不保证一致 —— 硬配会把曲线张冠李戴");
        TelemetryCollector.BetterName(b, inventory, counts).Should().Be("GPU 适配器 2");
    }

    [Fact]
    public void A_missing_inventory_leaves_everything_alone()
    {
        var device = Gpu("luid_0x00000000_0x0000D7B1", "GPU 适配器 1");

        TelemetryCollector.BetterName(device, null, Counts(device))
            .Should().Be("GPU 适配器 1", "清单还没采到（或没上报）时什么都不该改");
    }

    // ═══════════════════════════════════════════════════════════════
    // 磁盘 / CPU：这两条退路没变，一起钉住免得被上面的改动带偏
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void A_disk_placeholder_gets_the_model_but_keeps_the_drive_letters()
    {
        var device = Disk("disk0", "物理磁盘 0（C:）");

        TelemetryCollector.BetterName(device, Inventory(disk0: "WD Blue SN580 1TB"), Counts(device))
            .Should().Be("WD Blue SN580 1TB（C:）",
                "盘符是现场对号入座的关键，换型号时不能把它一起吃掉");
    }

    [Fact]
    public void A_single_cpu_gets_the_inventory_name_but_a_dual_socket_one_does_not()
    {
        var only = Cpu("cpu0", "物理 CPU 0");

        TelemetryCollector.BetterName(only, Inventory(cpu: "Intel(R) Core(TM) i5-8265U"), Counts(only))
            .Should().Be("Intel(R) Core(TM) i5-8265U");

        var a = Cpu("cpu0", "物理 CPU 0");
        var b = Cpu("cpu1", "物理 CPU 1");

        var counts = Counts(a, b);

        TelemetryCollector.BetterName(a, Inventory(cpu: "Intel(R) Xeon(R) Gold 6248"), counts)
            .Should().Be("物理 CPU 0", "清单只留了一份 CPU 信息，拿它给两颗都贴同一个名字是在猜");
    }
}
