using System.Runtime.InteropServices;
using ControlServer.Contracts;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// Windows 上的实时指标采集（M4.2）：CPU / 内存 / 磁盘速率 / GPU 占用。
/// <para>
/// <b>为什么网络速率不在这里</b>：PDH 的 <c>\Network Interface(*)\...</c> 会把回环、
/// 虚拟网卡、Hyper-V 交换机一起算进来，一台装了 Docker/WSL 的机器会报出与真实链路无关的大数字。
/// 那块交回 BCL 的 <see cref="NetworkRateSampler"/>，由组合根把两者合成一个源。
/// </para>
/// <para>
/// <b>三个数据源各自的取舍</b>：
/// </para>
/// <list type="bullet">
///   <item><b>CPU</b> 用 <c>GetSystemTimes</c>（P/Invoke）而不是 PDH —— 它一次给出
///         idle/kernel/user 三个累计值，"忙 = 总 − 闲"两步就出结果，没有计数器路径
///         在非英文系统上被本地化而查不到的风险。</item>
///   <item><b>内存</b> 用 <c>GlobalMemoryStatusEx</c> —— 与 WMI 的 <c>TotalPhysicalMemory</c>
///         同源，但不需要 COM，也不会因为 <c>Winmgmt</c> 服务异常而整段失败。</item>
///   <item><b>磁盘 / GPU</b> 只能走 PDH（详见 <see cref="PdhQuery"/> 的注释）。</item>
/// </list>
/// <para>
/// ⚠️ <b>速率类计数器必须采两次才有值</b>（第一次收集只是建立基线），
/// 所以本对象<b>必须作为单例长期持有</b>，且第一次 <c>Read()</c> 的速率项为 null —— 这是正确行为，不是缺陷。
/// </para>
/// </summary>
public sealed class WindowsHostMetricsSource : IHostMetricsSource
{
    /// <summary>
    /// 查"每个物理封装（插槽）里有哪些逻辑处理器"。
    ///
    /// <para>
    /// 🔥 <b>为什么非查不可</b>：PDH 的 <c>Processor Information</c> 实例是<b>逻辑处理器</b>
    /// （<c>组号,处理器号</c>），而页面上要画的是"这台机器有几颗 CPU"。
    /// 不做这层映射就只能把 8 线程的机器画成 8 颗 CPU —— 不是精度问题，是**说错话**。
    /// </para>
    ///
    /// <para>
    /// <b>为什么用 <c>GetLogicalProcessorInformationEx</c> 而不是 WMI</b>：
    /// 与 <see cref="WindowsHostAudioController"/> 同一套取舍 —— 不引入 COM。
    /// 采集循环是 5 秒一次的高频路径，而 WMI 查询在加固 / 虚拟机上有真实的卡顿史；
    /// 何况拓扑在运行期根本不变，这里只在构造时查**一次**。
    /// </para>
    ///
    /// <para>
    /// 返回 null 表示查不到（极端老的系统 / API 调用失败）——
    /// 调用方会退到"只报一颗 CPU"。这个方向的错误是**安全的**：现场绝大多数机器就是 1 插槽。
    /// </para>
    /// </summary>
    private static IReadOnlyList<HashSet<(int Group, int Number)>>? TryReadCpuPackages()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            uint length = 0;

            // 第一次只为问长度（必然"失败"并给出需要的字节数）。
            GetLogicalProcessorInformationEx(RelationProcessorPackage, IntPtr.Zero, ref length);

            if (length == 0) return null;

            var buffer = Marshal.AllocHGlobal((int)length);

            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorPackage, buffer, ref length)) return null;

                var packages = new List<HashSet<(int, int)>>();

                var offset = 0;

                while (offset < length)
                {
                    var relationship = (uint)Marshal.ReadInt32(buffer, offset);
                    var size = Marshal.ReadInt32(buffer, offset + 4);

                    // 防御：size 为 0 或越界都会让这个循环转不出去。
                    if (size <= 0 || offset + size > length) break;

                    if (relationship == RelationProcessorPackage)
                    {
                        var members = ReadPackageMembers(buffer, offset);

                        if (members.Count > 0) packages.Add(members);
                    }

                    offset += size;
                }

                return packages.Count > 0 ? packages : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从一个 <c>PROCESSOR_RELATIONSHIP</c> 条目里读出它覆盖的逻辑处理器集合。
    /// <para>
    /// 布局（x64）：条目头 <c>Relationship(4) + Size(4)</c>，联合体从偏移 8 开始；
    /// 联合体里 <c>GroupCount</c> 在 +22、<c>GroupMask[]</c> 从 +24 起，每项
    /// <c>GROUP_AFFINITY = { KAFFINITY Mask(指针宽度); WORD Group; WORD Reserved[3] }</c>。
    /// 32 位下 Mask 是 4 字节、<c>GROUP_AFFINITY</c> 共 12 字节，所以尺寸都用 <c>IntPtr.Size</c> 推。
    /// </para>
    /// </summary>
    private static HashSet<(int Group, int Number)> ReadPackageMembers(IntPtr buffer, int offset)
    {
        var members = new HashSet<(int, int)>();

        const int UnionOffset = 8;
        const int GroupCountOffset = 22;   // PROCESSOR_RELATIONSHIP 内
        const int GroupMaskOffset = 24;    // ditto

        var groupCount = (ushort)Marshal.ReadInt16(buffer, offset + UnionOffset + GroupCountOffset);

        // 亲和性结构按指针宽度对齐；每项都是"指针 + 2 + 6 字节填充"。
        var affinitySize = IntPtr.Size + 8;

        for (var g = 0; g < groupCount; g++)
        {
            var affinity = buffer + offset + UnionOffset + GroupMaskOffset + (g * affinitySize);

            var mask = (ulong)Marshal.ReadIntPtr(affinity).ToInt64();
            var group = (ushort)Marshal.ReadInt16(affinity, IntPtr.Size);

            for (var bit = 0; bit < 64; bit++)
            {
                if ((mask & (1UL << bit)) != 0) members.Add((group, bit));
            }
        }

        return members;
    }

    /// <summary>
    /// <c>LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorPackage</c>。
    /// <para>
    /// 🔥 <b>是 3，不是 0</b>：0 是 <c>RelationProcessorCore</c>（核心），1 是 NUMA 节点，2 是缓存。
    /// 传 0 会得到"每颗核心一个条目" —— 在 4 核机器上表现为 <b>4 颗"物理 CPU"</b>，
    /// 而那正是本方法要消除的那个错误，只是换了个数字。
    /// 枚举顺序见 <c>winnt.h</c>：Core, NumaNode, Cache, ProcessorPackage, Group, Die, NumaNodeEx, Module。
    /// </para>
    /// </summary>
    private const uint RelationProcessorPackage = 3;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(
        uint relationshipType,
        IntPtr buffer,
        ref uint returnedLength);

    /// <summary>
    /// 磁盘读写速率（物理磁盘合计，与任务管理器"磁盘"页同一口径）。
    /// </summary>
    private static readonly string[] DiskCounters =
    {
        @"\PhysicalDisk(_Total)\Disk Read Bytes/sec",
        @"\PhysicalDisk(_Total)\Disk Write Bytes/sec",
    };

    /// <summary>
    /// 逐物理盘的读写速率（2026-10-13）。
    /// <para>
    /// 通配实例 <c>PhysicalDisk(*)</c> 会同时给出 <c>_Total</c> 与每一块盘
    /// （实例名形如 <c>0 C:</c> —— 物理盘号 + 它上面的盘符）。这里把 <c>_Total</c> 剔掉：
    /// 它是整机口径，混进逐设备列表会多出一条"假的设备"。
    /// </para>
    /// </summary>
    private static readonly string[] DiskPerInstanceCounters =
    {
        @"\PhysicalDisk(*)\Disk Read Bytes/sec",
        @"\PhysicalDisk(*)\Disk Write Bytes/sec",
    };

    /// <summary>
    /// 逐 CPU（多路机器上就是"每颗物理 CPU / 每个插槽"）的占用率。
    /// <para>
    /// ⚠️ <b>为什么用 <c>Processor Information</c> 而不是 <c>Processor</c></b>：
    /// 后者不支持"处理器组"（&gt;64 逻辑处理器时必须分组），而前者支持。
    /// </para>
    /// <para>
    /// 🔥🔥 <b>但 <c>Processor Information</c> 的实例名 <c>组号,处理器号</c> 里的第二个数是
    /// <b>组内的逻辑处理器序号</b>，<u>不是插槽号</u>。</b>实测本机（1 插槽 / 4 核 / 8 线程）
    /// 得到 <c>0,0</c>…<c>0,7</c> 共 8 个实例 —— 直接拿它当插槽会把这台机器报成
    /// <b>8 颗物理 CPU</b>（2026-10-13 的真机验证就是这么发现的）。
    /// 所以这里的 <see cref="CollectCpuDevices"/> 必须先用
    /// <c>GetLogicalProcessorInformationEx(RelationProcessorPackage)</c> 把
    /// "逻辑处理器 → 物理封装"的对应关系查出来，再按封装聚合。
    /// </para>
    /// </summary>
    private static readonly string[] CpuPerInstanceCounters =
    {
        @"\Processor Information(*)\% Processor Time",
    };

    /// <summary>
    /// GPU 占用率。<c>GPU Engine</c> 是**多实例**计数器（每个进程的每种引擎一个实例），
    /// 因此必须用通配路径 + 数组取值（见 <see cref="PdhQuery.ReadArray"/>）。
    /// </summary>
    private static readonly string[] GpuCounters =
    {
        @"\GPU Engine(*)\Utilization Percentage",
    };

    private readonly PdhQuery? _disk;
    private readonly PdhQuery? _gpu;
    private readonly PdhQuery? _diskPerInstance;
    private readonly PdhQuery? _cpuPerInstance;

    /// <summary>
    /// 每个**物理封装（插槽）**包含哪些"（处理器组号，组内处理器号）"。
    /// <para>
    /// 拓扑在机器运行期间不会变，因此在构造时查一次就够 —— 别放进 5 秒一次的采集循环里。
    /// 查不到时为 null，<see cref="CollectCpuDevices"/> 会走"只报一颗 CPU"的保守退路。
    /// </para>
    /// </summary>
    private readonly IReadOnlyList<HashSet<(int Group, int Number)>>? _cpuPackages;

    /// <summary>
    /// 「GPU 适配器 LUID → 型号」的缓存（见 <see cref="GpuAdapterNames"/>）。
    /// <para>
    /// 显卡在机器运行期间几乎不变，而解析要枚举 DXGI（COM）/ 逐个开适配器句柄 ——
    /// 别塞进 5 秒一次的采集循环。但<b>外接显卡坞 / 驱动重启</b>会换 LUID，
    /// 所以每 <see cref="GpuNamesRefreshSamples"/> 次采集重查一次，平时走缓存。
    /// </para>
    /// </summary>
    private IReadOnlyDictionary<GpuAdapterNames.Luid, GpuAdapterNames.Named>? _gpuNames;

    private bool _gpuNamesLoaded;

    /// <summary>上次解析 GPU 型号时的 <see cref="_samples"/> 计数。</summary>
    private int _gpuNamesAt;

    /// <summary>GPU 型号表的刷新间隔（按采集次数计）。5 秒一采 ⇒ 60 次约 5 分钟。</summary>
    private const int GpuNamesRefreshSamples = 60;

    private long _prevIdle;
    private long _prevKernel;
    private long _prevUser;
    private bool _hasCpuBaseline;

    private int _samples;

    public WindowsHostMetricsSource()
    {
        if (!OperatingSystem.IsWindows()) return;

        _disk = PdhQuery.TryCreate(DiskCounters);
        _gpu = PdhQuery.TryCreate(GpuCounters);
        _diskPerInstance = PdhQuery.TryCreate(DiskPerInstanceCounters);
        _cpuPerInstance = PdhQuery.TryCreate(CpuPerInstanceCounters);
        _cpuPackages = TryReadCpuPackages();
    }

    /// <summary>PDH 是否至少有一组可用（组合根/排障可据此判断"这台机器能不能采到磁盘与 GPU"）。</summary>
    public bool PdhAvailable => _disk is not null || _gpu is not null;

    /// <summary>
    /// 本机有几个**物理封装（插槽）**；查不到拓扑时为 null。
    /// <para>
    /// 它同时也是"逐设备列表里应当有几条 CPU 曲线"的<b>唯一正确答案</b> ——
    /// 守门用例 <c>WindowsHostMetricsSourceTests</c> 拿它去钉住"别把逻辑处理器当成 CPU 报出去"。
    /// </para>
    /// </summary>
    public int? PhysicalPackageCount => _cpuPackages?.Count;

    public HostMetricReading Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new HostMetricReading(null, null, null, null, null, null, null, null);
        }

        var (memUsed, memTotal) = ReadMemory();

        // CPU 在第一帧没有基线（差分需要一个前值）。
        var cpu = ReadCpu();

        double? diskRead = null;
        double? diskWrite = null;

        if (_disk is not null)
        {
            _disk.Collect();

            // 第一帧只建立基线：速率计数器拿到的是"自上次收集以来的平均"，没有上次就没有值。
            if (_samples > 0)
            {
                diskRead = _disk.ReadSingle(0);
                diskWrite = _disk.ReadSingle(1);
            }
        }

        var devices = new List<AgentDeviceReadingDto>();

        CollectDiskDevices(devices);
        CollectCpuDevices(devices, cpu);

        double? gpu = null;

        if (_gpu is not null)
        {
            _gpu.Collect();

            var engines = ReadGpuEngines();

            if (engines.Count > 0)
            {
                double sum = 0;
                var any = false;

                foreach (var engine in engines)
                {
                    if (engine.Value is not { } v) continue;

                    sum += v;
                    any = true;
                }

                // ⚠️ 求和是**近似**：GPU Engine 的每个实例是"该引擎相对自己的占用"，
                //    多引擎同时繁忙时求和会超过 100。任务管理器用的是按物理适配器取
                //    各引擎最大值那一套，更精确但也更依赖驱动上报的实例命名。
                //    这里夹到 0–100：宁可略偏大，也不要画出超过 100% 的曲线。
                if (any) gpu = Math.Clamp(sum, 0, 100);

                CollectGpuDevices(devices, engines);
            }
        }

        _samples++;

        // 网络速率由 CompositeHostMetricsSource 里的 BCL 源补上（见类注释）。
        return new HostMetricReading(
            cpu,
            memUsed,
            memTotal,
            diskRead,
            diskWrite,
            null,
            null,
            gpu,
            devices.Count > 0 ? devices : null);
    }

    // ═══════════════════════════════════════════════════════════
    // 逐设备（2026-10-13）
    //
    // ⚠️ 这里只负责"是哪一个 + 多少"。**型号名不在这里查** ——
    //    它来自硬件清单（WMI），而清单在 Agent 层每 6 小时才采一次。
    //    平台层为了一个显示名去开一条 WMI 通道，代价远大于收益；
    //    所以这里的 Name 是一个**能辨认的占位名**（"物理磁盘 0（C:）"），
    //    由 Agent 层用清单里的型号覆盖（Key 保持不变，曲线照旧接得上）。
    // ═══════════════════════════════════════════════════════════

    private void CollectDiskDevices(List<AgentDeviceReadingDto> devices)
    {
        if (_diskPerInstance is null) return;

        _diskPerInstance.Collect();

        // 第一帧只有基线，速率项一律 null —— 但**设备本身照常上报**：
        // 前端要靠它把"这台机器有几块盘"这份名单建起来，否则第一帧的列表是空的。
        var reads = _diskPerInstance.ReadNamedArray(0);
        var writes = _diskPerInstance.ReadNamedArray(1);

        if (reads is null || reads.Length == 0) return;

        var writeByName = new Dictionary<string, double?>(StringComparer.Ordinal);

        if (writes is not null)
        {
            foreach (var (name, value) in writes) writeByName[name] = value;
        }

        foreach (var (name, readValue) in reads)
        {
            // `_Total` 是整机口径，不是一块设备 —— 混进来会多出一条"假磁盘"。
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('_')) continue;

            var index = LeadingNumber(name);

            if (index is null) continue;

            writeByName.TryGetValue(name, out var writeValue);

            devices.Add(new AgentDeviceReadingDto(
                AgentDeviceKinds.Disk,
                $"disk{index}",
                DescribeDiskInstance(name, index.Value),
                ReadBps: _samples > 0 ? readValue : null,
                WriteBps: _samples > 0 ? writeValue : null));
        }
    }

    private void CollectCpuDevices(List<AgentDeviceReadingDto> devices, double? machineCpu)
    {
        // 「逻辑处理器 → 物理封装」的对应关系查不出来时的**保守退路**：
        // 只报一颗 CPU、用整机占用率。
        // 🔥 宁可少报，也不要按 PDH 实例逐个报 —— 那是逻辑处理器，
        //    在 8 线程机器上会得到"8 颗物理 CPU"，是一句彻底的假话。
        //    （1 插槽是绝大多数现场机器的真相，所以这条退路在概率上也是对的。）
        if (_cpuPackages is not { Count: > 0 })
        {
            devices.Add(new AgentDeviceReadingDto(
                AgentDeviceKinds.Cpu,
                "cpu0",
                "物理 CPU 0",
                CpuPct: machineCpu));

            return;
        }

        var totals = new double?[_cpuPackages.Count];
        var sums = new double[_cpuPackages.Count];
        var counts = new int[_cpuPackages.Count];

        if (_cpuPerInstance is not null)
        {
            _cpuPerInstance.Collect();

            var values = _cpuPerInstance.ReadNamedArray(0);

            if (values is not null)
            {
                foreach (var (name, value) in values)
                {
                    if (value is not { } v) continue;

                    var at = IndexOfPackage(name);

                    if (at < 0) continue;   // `_Total` / `0,_Total` / 认不出的实例

                    sums[at] += v;
                    counts[at]++;
                }
            }
        }

        for (var i = 0; i < _cpuPackages.Count; i++)
        {
            // 该封装下所有逻辑处理器的**平均**占用率 —— 与"这颗 CPU 有多忙"的直觉一致
            // （求和会得到"核数 × 100%"这种没意义的数）。
            if (counts[i] > 0) totals[i] = Math.Clamp(sums[i] / counts[i], 0, 100);

            devices.Add(new AgentDeviceReadingDto(
                AgentDeviceKinds.Cpu,
                $"cpu{i}",
                $"物理 CPU {i}",
                CpuPct: _samples > 0 ? totals[i] : null));
        }
    }

    /// <summary>
    /// PDH 实例名（<c>组号,处理器号</c>）落在哪个物理封装里；认不出返回 −1。
    /// <para>识别不了的一律当"不是任何一颗 CPU"（<c>_Total</c>、<c>0,_Total</c>、格式变了的新实例名）——
    /// 与其猜，不如让它体现在"曲线少一条"上。</para>
    /// </summary>
    private int IndexOfPackage(string instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName)) return -1;

        var comma = instanceName.IndexOf(',');

        if (comma <= 0) return -1;

        if (!int.TryParse(instanceName.AsSpan(0, comma), out var group)) return -1;
        if (!int.TryParse(instanceName.AsSpan(comma + 1), out var number)) return -1;

        var packages = _cpuPackages;

        if (packages is null) return -1;

        for (var i = 0; i < packages.Count; i++)
        {
            if (packages[i].Contains((group, number))) return i;
        }

        return -1;
    }

    /// <summary>
    /// 把 <c>GPU Engine</c> 的实例按<b>适配器 LUID</b> 归并（每个引擎实例是"某块显卡上某种引擎"）。
    /// </summary>
    private List<(string Luid, double? Value)> ReadGpuEngines()
    {
        var result = new List<(string Luid, double? Value)>();

        var values = _gpu?.ReadNamedArray(0);

        if (values is null) return result;

        // LUID 顺序拿到，这样"适配器 1 / 2"在多台机器上稳定。
        var order = new List<string>();
        var sums = new Dictionary<string, double>(StringComparer.Ordinal);
        var any = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, value) in values)
        {
            var luid = ExtractLuid(name);

            if (luid is null) continue;

            if (!sums.ContainsKey(luid))
            {
                sums[luid] = 0;
                order.Add(luid);
            }

            if (value is not { } v) continue;

            sums[luid] += v;
            any.Add(luid);
        }

        foreach (var luid in order)
        {
            result.Add((luid, any.Contains(luid) ? Math.Clamp(sums[luid], 0, 100) : null));
        }

        return result;
    }

    private void CollectGpuDevices(List<AgentDeviceReadingDto> devices, List<(string Luid, double? Value)> engines)
    {
        // 兜底路径（D3DKMT）是"按 LUID 逐个问"，所以先把这一帧的 LUID 收齐。
        var luids = new List<string>(engines.Count);

        foreach (var (luid, _) in engines)
        {
            if (!string.IsNullOrEmpty(luid)) luids.Add(luid);
        }

        var names = GpuNames(luids);

        var index = 0;

        foreach (var (luid, value) in engines)
        {
            if (string.IsNullOrEmpty(luid)) continue;

            var named = GpuAdapterNames.TryFind(names, luid, out var hit) ? hit : null;

            // 🔥 软件光栅器（Microsoft Basic Render Driver / WARP）**不是显卡**。
            //    GPU Engine 计数器会把它报出来（占用率恒为 0），但把它当作"一块显卡"
            //    列进硬件清单，会让人以为这台机器装了三个显卡。剔掉。
            if (named is { IsSoftware: true }) continue;

            index++;

            devices.Add(new AgentDeviceReadingDto(
                AgentDeviceKinds.Gpu,
                luid,
                // 🔥 型号按 **LUID** 对号入座（DXGI 优先 / D3DKMT 兜底），
                //    而不是"按清单顺序硬配" —— 后者会把 MX250 的占用率画到 UHD 620 上，
                //    那个错误**看起来是对的**，比占位名危险得多。两条路都拿不到时退回占位名。
                named?.Name is { Length: > 0 } model ? model : $"GPU 适配器 {index}",
                // 设备名单第一帧就要建立（前端靠它列硬件），值等第二帧才有（速率类计数器）。
                GpuPct: _samples > 0 ? value : null));
        }
    }

    /// <summary>取「LUID → 型号」表（带缓存与周期刷新）。非 Windows 时返回空表。</summary>
    private IReadOnlyDictionary<GpuAdapterNames.Luid, GpuAdapterNames.Named> GpuNames(IReadOnlyList<string> luidKeys)
    {
        if (!OperatingSystem.IsWindows()) return EmptyGpuNames;

        if (!_gpuNamesLoaded || _samples - _gpuNamesAt >= GpuNamesRefreshSamples)
        {
            var fresh = GpuAdapterNames.Resolve(luidKeys);

            // 解析失败（例如驱动崩了正在恢复）时**保留上一次**的表：
            // 旧型号总比占位名有用，而且 LUID 不会因为一次失败就变。
            if (fresh.Count > 0 || _gpuNames is null) _gpuNames = fresh;

            _gpuNamesLoaded = true;
            _gpuNamesAt = _samples;
        }

        return _gpuNames ?? EmptyGpuNames;
    }

    private static readonly IReadOnlyDictionary<GpuAdapterNames.Luid, GpuAdapterNames.Named> EmptyGpuNames
        = new Dictionary<GpuAdapterNames.Luid, GpuAdapterNames.Named>();

    /// <summary>取字符串开头那段连续数字（<c>"0 C:"</c> → 0）。取不到返回 null。</summary>
    private static int? LeadingNumber(string text)
    {
        var digits = 0;

        while (digits < text.Length && char.IsAsciiDigit(text[digits])) digits++;

        if (digits == 0) return null;

        return int.TryParse(text.AsSpan(0, digits), out var value) ? value : null;
    }

    /// <summary><c>"0 C:"</c> → <c>物理磁盘 0（C:）</c>。</summary>
    private static string DescribeDiskInstance(string instance, int index)
    {
        var suffix = instance.Length > index.ToString().Length
            ? instance[index.ToString().Length..].Trim()
            : string.Empty;

        return suffix.Length == 0 ? $"物理磁盘 {index}" : $"物理磁盘 {index}（{suffix}）";
    }

    /// <summary>
    /// 从 <c>pid_1234_luid_0x00000000_0x0000D7B1_phys_0_eng_0_engtype_3D</c> 里抠出
    /// <c>luid_0x00000000_0x0000D7B1</c>。取不到返回 null（那种实例不属于任何适配器，直接丢）。
    /// </summary>
    private static string? ExtractLuid(string instanceName)
    {
        const string Marker = "luid_";

        var at = instanceName.IndexOf(Marker, StringComparison.Ordinal);

        if (at < 0) return null;

        var tail = instanceName[at..];
        var parts = tail.Split('_');

        // luid_0xAAAAAAAA_0xBBBBBBBB ⇒ 至少 3 段，且后两段要是十六进制数。
        if (parts.Length < 3) return null;

        if (!IsHex(parts[1]) || !IsHex(parts[2])) return null;

        return $"{parts[0]}_{parts[1]}_{parts[2]}";
    }

    /// <summary>形如 <c>0x0000D7B1</c> 的十六进制字面量。</summary>
    private static bool IsHex(string text)
        => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
           && text.Length > 2
           && text[2..].All(Uri.IsHexDigit);

    // ── CPU：GetSystemTimes 差分 ──

    private double? ReadCpu()
    {
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;

            var idleTicks = ToTicks(idle);
            var kernelTicks = ToTicks(kernel);
            var userTicks = ToTicks(user);

            var prevIdle = _prevIdle;
            var prevKernel = _prevKernel;
            var prevUser = _prevUser;

            _prevIdle = idleTicks;
            _prevKernel = kernelTicks;
            _prevUser = userTicks;

            if (!_hasCpuBaseline)
            {
                _hasCpuBaseline = true;
                return null;
            }

            // kernel 时间**包含** idle 时间，因此总量 = kernel + user，空闲 = idle。
            var totalDelta = (kernelTicks - prevKernel) + (userTicks - prevUser);
            var idleDelta = idleTicks - prevIdle;

            if (totalDelta <= 0) return null;

            var busy = (totalDelta - idleDelta) * 100.0 / totalDelta;
            return Math.Clamp(busy, 0, 100);
        }
        catch
        {
            return null;
        }
    }

    private static long ToTicks(FILETIME time) => ((long)time.HighDateTime << 32) | time.LowDateTime;

    // ── 内存：GlobalMemoryStatusEx ──

    private static (long? UsedMb, long? TotalMb) ReadMemory()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };

            if (!GlobalMemoryStatusEx(ref status)) return (null, null);

            var totalBytes = status.ullTotalPhys;
            var availBytes = status.ullAvailPhys;

            if (totalBytes <= 0 || availBytes > totalBytes) return (null, null);

            return ((long)((totalBytes - availBytes) / 1024 / 1024), (long)(totalBytes / 1024 / 1024));
        }
        catch
        {
            return (null, null);
        }
    }

    // ── P/Invoke ──

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}

/// <summary>
/// 一个最小的 PDH（Performance Data Helper）封装：只做"打开一个查询、加几个计数器、取值"。
/// <para>
/// <b>为什么自己写 P/Invoke 而不用 <c>System.Diagnostics.PerformanceCounter</c> 包</b>：
/// </para>
/// <list type="bullet">
///   <item>那个包在 Linux 上是 <c>PlatformNotSupported</c>，会逼着每处调用都加平台分支；</item>
///   <item>本项目的既有做法就是"平台差异用 P/Invoke + 运行时守卫"（见 <c>NativeMethods</c>、
///         <c>PairingListenerService</c>），保持一条路更好维护；</item>
///   <item>少一个包 = 客户机安装包小一点。</item>
/// </list>
/// <para>
/// <b>为什么用 <c>PdhAddEnglishCounter</c> 而不是 <c>PdhAddCounter</c></b>：
/// 后者按<b>本地化</b>名字查（中文系统上对象名是"物理磁盘"），
/// 用英文路径会直接查不到；前者接受英文路径并在内部做映射，因此同一份代码在任何语言的
/// Windows 上都能用。
/// </para>
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint ErrorSuccess = 0;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtDouble = 0x00000200;

    /// <summary>不把"整体最大 100"当作上限来归一化 —— GPU 这类计数器的语义本来就可能超过 100，由调用方决定怎么夹。</summary>
    private const uint PdhFmtNoCap100 = 0x00008000;

    private readonly IntPtr _query;
    private readonly List<IntPtr> _counters = new();

    private PdhQuery(IntPtr query) => _query = query;

    /// <summary>
    /// 建一个查询。<b>任何一步失败都返回 null</b>（调用方据此显示"未启用"），
    /// 而不是抛异常 —— 一台把性能计数器关掉的加固系统不该让采集循环崩掉。
    /// </summary>
    public static PdhQuery? TryCreate(IReadOnlyList<string> counterPaths)
    {
        if (!OperatingSystem.IsWindows() || counterPaths.Count == 0) return null;

        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out var query) != ErrorSuccess) return null;

            var result = new PdhQuery(query);

            foreach (var path in counterPaths)
            {
                if (PdhAddEnglishCounter(query, path, IntPtr.Zero, out var counter) != ErrorSuccess)
                {
                    // 少一个计数器不影响其它的：例如没有独显的机器没有 GPU Engine 计数器，
                    // 但磁盘速率照样要采。
                    continue;
                }

                result._counters.Add(counter);
            }

            if (result._counters.Count == 0)
            {
                result.Dispose();
                return null;
            }

            // 先收集一次建立基线（速率类计数器需要两个采样点）。
            result.Collect();

            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>收集一次（速率类计数器的"采样点"）。</summary>
    public void Collect()
    {
        try
        {
            PdhCollectQueryData(_query);
        }
        catch
        {
            // 忽略：取不到值就返回 null，由调用方处理。
        }
    }

    /// <summary>取单实例计数器的值（如 <c>PhysicalDisk(_Total)</c>）。取不到返回 null。</summary>
    public double? ReadSingle(int index)
    {
        try
        {
            if (index < 0 || index >= _counters.Count) return null;

            var status = PdhGetFormattedCounterValue(
                _counters[index],
                PdhFmtDouble | PdhFmtNoCap100,
                out _,
                out var value);

            return status == ErrorSuccess ? value.DoubleValue : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>取多实例计数器的值（丢掉实例名）。保留给"只关心值"的调用点。</summary>
    public double?[]? ReadArray(int index)
    {
        var named = ReadNamedArray(index);

        return named?.Select(x => x.Value).ToArray();
    }

    /// <summary>
    /// 取多实例计数器（通配路径）的<b>全部值连同实例名</b>。
    /// <para>
    /// 两段式调用：先用空缓冲问一次尺寸（返回 <c>PDH_MORE_DATA</c>），再按尺寸分配后取。
    /// 这是 PDH 的既定协议 —— 它不会告诉你"大概多少条"，只会告诉你"还差多少字节"。
    /// </para>
    /// <para>
    /// 🔥 <b>为什么要实例名</b>（2026-10-13 加的）：逐设备曲线靠它区分"是哪一块盘 / 哪颗 CPU / 哪块显卡"。
    /// 实例名是 PDH 给的<b>唯一</b>身份，丢了它就只能求和，也就是回到"整机口径"。
    /// </para>
    /// <para>单个实例的状态码非 0（例如刚出现的实例还没形成速率）时该项值为 null，但实例名照常返回。</para>
    /// </summary>
    public (string Name, double? Value)[]? ReadNamedArray(int index)
    {
        try
        {
            if (index < 0 || index >= _counters.Count) return null;

            var counter = _counters[index];
            uint size = 0;
            uint count = 0;

            var status = PdhGetFormattedCounterArray(
                counter,
                PdhFmtDouble | PdhFmtNoCap100,
                ref size,
                ref count,
                IntPtr.Zero);

            if (status != PdhMoreData || size == 0 || count == 0) return null;

            var buffer = Marshal.AllocHGlobal((int)size);

            try
            {
                status = PdhGetFormattedCounterArray(
                    counter,
                    PdhFmtDouble | PdhFmtNoCap100,
                    ref size,
                    ref count,
                    buffer);

                if (status != ErrorSuccess) return null;

                var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
                var result = new (string Name, double? Value)[count];

                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buffer + (i * itemSize));

                    // ⚠️ 名字指针指向的是 PDH 分配的缓冲，**只能在这块 buffer 还活着的时候读**。
                    //    出了这个 try 就变成野指针 —— 拷贝成本极低，没有理由冒险。
                    var name = item.Name == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(item.Name) ?? string.Empty;

                    // ⚠️ 这里必须**显式声明成 `double?`**（不能用 var）：三元的一边是 double、
                    //    一边是 null，没有目标类型时编译器推不出来（CS0173）。
                    double? value = item.Value.Status == ErrorSuccess ? item.Value.DoubleValue : null;

                    result[i] = (name, value);
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        }
        catch
        {
            // 忽略。
        }
    }

    /// <summary>
    /// <c>PDH_FMT_COUNTERVALUE</c>：一个状态码 + 一个联合体。
    /// <para>⚠️ 联合体在 64 位下必须落在偏移 8（double / LONGLONG 要求 8 字节对齐），
    /// 写成顺序布局会读到错位的数据，而且是<b>安静的错</b> —— 得到的是一个看着挺合理的数字。</para>
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PdhFmtCounterValue
    {
        [FieldOffset(0)]
        public uint Status;

        [FieldOffset(8)]
        public double DoubleValue;
    }

    /// <summary><c>PDH_FMT_COUNTERVALUE_ITEM_W</c>：实例名（宽字符串指针）+ 值。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr Name;

        public PdhFmtCounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern uint PdhGetFormattedCounterValue(
        IntPtr counter,
        uint format,
        out uint type,
        out PdhFmtCounterValue value);

    /// <summary>
    /// 🔥 <b>`CharSet.Unicode` 是**必须**的，不是可选的。</b>
    /// <para>
    /// 不加这一句，运行时会把入口点解析成 <c>PdhGetFormattedCounterArrayA</c>，
    /// 那个版本填的是 <c>PDH_FMT_COUNTERVALUE_ITEM_A</c> —— 结构体布局一模一样
    /// （指针 + 16 字节值），于是<b>值全都读得对</b>，只有 <c>szName</c> 变成了一串 ANSI 字节；
    /// 再用 <see cref="Marshal.PtrToStringUni"/> 去解，得到的是<b>乱码</b>。
    /// </para>
    /// <para>
    /// 为什么这个错误特别阴：它<b>不抛异常、不给错误码</b>。上面的调用方拿到乱码名字后，
    /// 按设计把认不出的实例<b>静默跳过</b>（那是正确的防御），于是最终表现只是
    /// "逐设备列表里少了磁盘和 CPU" —— 看起来像"这台机器没采到"，而不是"代码写错了"。
    /// 2026-10-13 就是靠一个真浏览器用例才发现：页面上只有一块网卡，没有任何磁盘。
    /// </para>
    /// <para>守门用例：<c>WindowsHostMetricsSourceTests</c> 里那条断言"实例名必须是人能读的"。</para>
    /// </summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        IntPtr itemBuffer);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
