using ControlServer.Contracts;
using ControlServer.Platform.Windows;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// <see cref="WindowsHostMetricsSource"/> 的<b>真机</b>逐设备用例（M4.8）。
///
/// <para>
/// 🔥 <b>为什么这个文件非存在不可</b>：2026-10-13 那一版逐设备实现里有两个缺陷，
/// <b>编译期、单测、以及所有的桩测试都发现不了</b>，因为它们在结构上只表现为
/// "某个列表少了几项" —— 而那正好是"这台机器没采到"的正常形态：
/// </para>
///
/// <list type="number">
///   <item>
///     <b><c>PdhGetFormattedCounterArray</c> 缺 <c>CharSet.Unicode</c></b> ⇒ 绑到了 ANSI 版本，
///     实例名是一串 ANSI 字节；用 <c>PtrToStringUni</c> 解出来是乱码。
///     <b>值全都读得对</b>（结构体布局一样），只有名字坏了；
///     上层按设计把认不出的名字静默跳过 ⇒ <b>磁盘与 CPU 一条都不剩</b>。
///   </item>
///   <item>
///     <b>把 <c>Processor Information</c> 的实例当成了"插槽"</b>。
///     它的实例名 <c>组号,处理器号</c> 里的第二个数是<b>组内逻辑处理器序号</b>，
///     于是一台 1 插槽 / 4 核 / 8 线程的机器被报成 <b>8 颗物理 CPU</b> —— 不是精度问题，是说错话。
///     （修法：再用 <c>GetLogicalProcessorInformationEx(RelationProcessorPackage)</c> 把
///     "逻辑处理器 → 物理封装"查出来再聚合。顺带一提，那个枚举里 Package 是 <b>3</b> 不是 0，
///     传 0 会拿到"每颗核心一个条目"，4 核机器上就是 4 颗"CPU"。）
///   </item>
/// </list>
///
/// <para>
/// 这两条都<b>只有真的调一次 Windows API</b> 才看得见。所以本文件不打桩。
/// </para>
///
/// <para>
/// ⚠️ 非 Windows 上直接返回（本用例跑在 Linux CI 上没有意义，也不该假装通过）。
/// </para>
/// </summary>
public class WindowsHostMetricsSourceTests
{
    /// <summary>速率类计数器要两次采样才有值；设备<a>本身</a>第一帧就该有。</summary>
    private static WindowsHostMetricsSource ReadTwice(out HostReading first, out HostReading second)
    {
        var source = new WindowsHostMetricsSource();

        first = Snapshot(source);
        Thread.Sleep(400);              // 让速率计数器有一个可用的时间间隔
        second = Snapshot(source);

        return source;

        static HostReading Snapshot(WindowsHostMetricsSource s)
        {
            var r = s.Read();

            return new HostReading(
                r.Devices?.ToList(),
                r.CpuPct,
                r.DiskReadBps,
                s.PhysicalPackageCount);
        }
    }

    private readonly record struct HostReading(
        List<AgentDeviceReadingDto>? Devices,
        double? CpuPct,
        double? DiskReadBps,
        int? PowerPackageCount);

    [Fact]
    public void Disk_and_cpu_devices_are_actually_reported()
    {
        if (!OperatingSystem.IsWindows()) return;

        var source = ReadTwice(out _, out var second);

        second.Devices.Should().NotBeNull(
            "一台 Windows 机器必然至少有一块物理磁盘与一颗 CPU —— 一条都没有，说明逐设备采集整段没生效");

        var devices = second.Devices!;

        devices.Should().Contain(d => d.Kind == AgentDeviceKinds.Disk,
            "物理磁盘是逐设备采集的最基本一条");

        devices.Should().Contain(d => d.Kind == AgentDeviceKinds.Cpu,
            "CPU 同理");
    }

    [Fact]
    public void Device_names_are_human_readable()
    {
        if (!OperatingSystem.IsWindows()) return;

        ReadTwice(out _, out var second);

        var devices = second.Devices ?? new List<AgentDeviceReadingDto>();

        devices.Should().NotBeEmpty();

        foreach (var d in devices)
        {
            d.Key.Should().NotBeNullOrWhiteSpace($"每一块硬件都要有稳定 Key（{d.Kind}）");
            d.Name.Should().NotBeNullOrWhiteSpace($"每一块硬件都要有能认的名字（{d.Kind}）");

            // 🔥 乱码的特征：U+FFFD（替换字符）与"半角字符被当成 UTF-16 解码"后的私用区 / 汉字乱码。
            //    正常名字是 `物理磁盘 0（C:）` / `物理 CPU 0` / 网卡型号 / 适配器 GUID 名 —— 不会有替换字符。
            d.Name.Should().NotContain("\uFFFD", $"名字不应当是解码失败的乱码：{d.Name}");

            d.Name.Should().NotMatchRegex(@"[\uE000-\uF8FF]", $"名字不该落进私用区（典型的 ANSI→UTF16 误读）：{d.Name}");
        }
    }

    /// <summary>
    /// 🔥 这条是本文件的核心：<b>CPU 设备的条数必须等于物理封装数</b>。
    /// <para>
    /// 报少了（退化成"只报一颗"）还能解释成保守；报多了就是**在页面上撒谎** ——
    /// 用户会以为这台机器装了 8 颗 CPU，然后去查一个不存在的硬件。
    /// </para>
    /// </summary>
    [Fact]
    public void Cpu_device_count_matches_the_physical_package_count()
    {
        if (!OperatingSystem.IsWindows()) return;

        var source = ReadTwice(out _, out var second);

        var expected = source.PhysicalPackageCount;

        var cpuDevices = (second.Devices ?? new List<AgentDeviceReadingDto>())
            .Where(d => d.Kind == AgentDeviceKinds.Cpu)
            .ToList();

        if (expected is null)
        {
            // 查不到拓扑时的保守退路：只报一颗。**绝不能**退化成"报 8 颗"。
            cpuDevices.Should().HaveCount(1, "拓扑查不出来时只报一颗 CPU（大多数机器本来就只有一颗）");
            return;
        }

        expected.Value.Should().BeGreaterThan(0);

        cpuDevices.Should().HaveCount(expected.Value,
            "一个物理封装一条 CPU 曲线 —— 多一条就是把逻辑处理器当成了 CPU");

        // 键必须是 cpu0 / cpu1 …，顺序稳定（曲线历史靠它接续）。
        cpuDevices.Select(d => d.Key).Should().Equal(
            Enumerable.Range(0, expected.Value).Select(i => $"cpu{i}").ToArray(),
            "Key 必须是 cpu0 起的连续编号（它不是显示名，是曲线的身份）");

        // 逻辑处理器数是封装数的整数倍，反之不成立；这里只用它卡一个下界：
        // **封装数不可能超过逻辑处理器数**。
        expected.Value.Should().BeLessThanOrEqualTo(Environment.ProcessorCount);
    }

    [Fact]
    public void Every_device_only_fills_the_fields_that_belong_to_its_kind()
    {
        if (!OperatingSystem.IsWindows()) return;

        ReadTwice(out _, out var second);

        foreach (var d in second.Devices ?? new List<AgentDeviceReadingDto>())
        {
            switch (d.Kind)
            {
                case AgentDeviceKinds.Cpu:
                    d.CpuPct.Should().NotBeNull($"CPU 设备必须带占用率（{d.Key}）");
                    d.ReadBps.Should().BeNull("CPU 不是磁盘");
                    d.UpBps.Should().BeNull("CPU 不是网卡");
                    d.GpuPct.Should().BeNull("CPU 不是显卡");
                    break;

                case AgentDeviceKinds.Disk:
                    d.CpuPct.Should().BeNull("磁盘没有 CPU 占用率");
                    d.UpBps.Should().BeNull("磁盘不是网卡");
                    // 读写速率在第二次采样后应当有值（第一次没有基线，那是正确的 null）。
                    (d.ReadBps is not null || d.WriteBps is not null)
                        .Should().BeTrue($"第二次采样后磁盘应有读写速率（{d.Key}）");
                    break;

                case AgentDeviceKinds.Gpu:
                    d.CpuPct.Should().BeNull("显卡没有 CPU 占用率");
                    d.ReadBps.Should().BeNull("显卡不是磁盘");
                    break;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 显卡型号（需求②）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 逐设备的显卡名字必须是<b>真型号</b>，不能停在 <c>GPU 适配器 1</c>。
    /// <para>
    /// 判据不是"我以为这台机器上有什么显卡"，而是<b>解析器自己给出的那张表</b> ——
    /// 解析器能查到型号的每一块，逐设备曲线就必须用那个型号。
    /// 这样在没显卡的 CI 虚拟机上不会假红，而在有显卡的机器上不会假绿。
    /// </para>
    /// </summary>
    [Fact]
    public void Gpu_devices_are_named_after_the_real_adapter_model()
    {
        if (!OperatingSystem.IsWindows()) return;

        ReadTwice(out _, out var second);

        var gpus = (second.Devices ?? new List<AgentDeviceReadingDto>())
            .Where(d => d.Kind == AgentDeviceKinds.Gpu)
            .ToList();

        if (gpus.Count == 0) return;    // 没有任何适配器在用（无显卡的机器）：无可断言。

        // 软件光栅器（WARP）是**合成器**用的，不是显卡。它恒定 0% 占用，
        // 混进"显卡"里会让人以为这台机器装了三块显卡。
        gpus.Should().NotContain(g => GpuAdapterNames.IsSoftwareRenderer(g.Name),
            "Microsoft Basic Render Driver 是软件光栅器，不该以显卡身份出现在逐设备列表里");

        var resolved = GpuAdapterNames.Resolve(gpus.Select(g => g.Key).ToList());

        resolved.Count.Should().BeGreaterThan(0,
            "本机有 GPU Engine 计数（说明有适配器在用），那么 DXGI 或 D3DKMT 至少有一条该能问到型号 —— "
            + "两条都为空意味着\"LUID → 型号\"整条链没生效，页面会退回 GPU 适配器 N");

        foreach (var g in gpus)
        {
            if (!GpuAdapterNames.TryFind(resolved, g.Key, out var named)) continue;

            g.Name.Should().Be(named.Name,
                $"LUID {g.Key} 查得到型号，逐设备曲线就该用型号（而不是占位名）");
        }
    }

    /// <summary>
    /// <c>D3DKMT</c> 是"DXGI 在 Session 0 里枚举不出来"时的保险 —— 它要能被真的跑到。
    /// <para>
    /// 🔥 为什么单独测它：<see cref="GpuAdapterNames.Resolve"/> 正常情况下永远走 DXGI，
    /// 兜底那条<b>一次都不会被执行</b>；而它恰恰是唯一在产品环境（服务、Session 0）里
    /// 可能用上的那条。不被测到的保险等于没有保险。
    /// </para>
    /// </summary>
    [Fact]
    public void The_d3dkmt_fallback_also_yields_model_names()
    {
        if (!OperatingSystem.IsWindows()) return;

        ReadTwice(out _, out var second);

        var luidKeys = (second.Devices ?? new List<AgentDeviceReadingDto>())
            .Where(d => d.Kind == AgentDeviceKinds.Gpu)
            .Select(d => d.Key)
            .ToList();

        if (luidKeys.Count == 0) return;

        var byD3dkmt = GpuAdapterNames.ResolveByD3dkmt(luidKeys);

        byD3dkmt.Count.Should().BeGreaterThan(0,
            "D3DKMTOpenAdapterFromLuid 接受的就是 PDH 给的那个 LUID —— 一条都问不出来说明结构体或调用约定写错了");

        byD3dkmt.Values.Should().Contain(v => !v.IsSoftware,
            "至少要有一块是物理显卡（不能全是软件光栅器，那说明型号串读错了位置）");
    }

    /// <summary>
    /// LUID 的解析与比较。<b>纯函数</b>，不打桩也不碰真机。
    /// <para>
    /// ⚠️ 钉住的是"<b>数值</b>比较"这个决定：PDH 给的十六进制补零位数是实现细节
    /// （<c>0x0000D7B1</c> / <c>0xD7B1</c> 都合法），拿字符串当键会在某台机器上
    /// 突然"一条都匹配不上"，而且<b>没有任何报错</b> —— 只表现为退回占位名。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("luid_0x00000000_0x0000D7B1", 0x0000D7B1u, 0)]
    [InlineData("luid_0x0_0xD7B1", 0x0000D7B1u, 0)]              // 不补零也认
    [InlineData("luid_0x0_0xd7b1", 0x0000D7B1u, 0)]              // 小写也认
    [InlineData("luid_0xFFFFFFFF_0x0000E55E", 0x0000E55Eu, -1)]  // 高 32 位可以非零（且是负数）
    public void Luid_keys_are_parsed_numerically(string key, uint low, int high)
    {
        GpuAdapterNames.TryParseLuid(key, out var luid).Should().BeTrue();

        luid.Low.Should().Be(low);
        luid.High.Should().Be(high);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pid_1234")]                       // 根本没有 luid 段
    [InlineData("luid_0x00000000")]                 // 少一段
    [InlineData("luid_0xZZZZZZZZ_0x0000D7B1")]      // 不是十六进制
    [InlineData("luid_00000000_0000D7B1")]          // 缺 0x 前缀
    public void Malformed_luid_keys_are_rejected(string key)
    {
        GpuAdapterNames.TryParseLuid(key, out _).Should().BeFalse(
            "认不出的实例名必须被丢掉，不能当成某块适配器");
    }

    /// <summary>补零位数不同也必须能对上号（这正是改用数值当键的理由）。</summary>
    [Fact]
    public void Lookup_ignores_the_hex_padding_of_the_key()
    {
        var map = new Dictionary<GpuAdapterNames.Luid, GpuAdapterNames.Named>
        {
            [new GpuAdapterNames.Luid(0x0000D7B1, 0)] = new("NVIDIA GeForce MX250", false),
        };

        GpuAdapterNames.TryFind(map, "luid_0x00000000_0x0000D7B1", out var a).Should().BeTrue();
        a.Name.Should().Be("NVIDIA GeForce MX250");

        GpuAdapterNames.TryFind(map, "luid_0x0_0xD7B1", out var b).Should().BeTrue();
        b.Name.Should().Be("NVIDIA GeForce MX250", "补零位数是实现细节，不该影响匹配");
    }

    [Theory]
    [InlineData("Microsoft Basic Render Driver", true)]
    [InlineData("Microsoft Basic Render Driver (WARP)", true)]
    [InlineData("Intel(R) UHD Graphics 620", false)]
    [InlineData("NVIDIA GeForce MX250", false)]
    public void The_software_renderer_is_recognised_by_name(string name, bool software)
    {
        // 只在 D3DKMT 兜底路径上用（那条路拿不到 DXGI 的 Flags）。
        GpuAdapterNames.IsSoftwareRenderer(name).Should().Be(software);
    }
}
