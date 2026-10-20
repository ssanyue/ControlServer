using System.Net.NetworkInformation;
using ControlServer.Contracts;

namespace ControlServer.Platform.Abstractions;

// ═══════════════════════════════════════════════════════════════════════════
// M4.2 · 平台采集端口
//
// 三条与 IHostHardwareInfo 完全一致的纪律：
//   ① **绝不抛异常**（采集跑在客户机的服务里，一次 WMI/PDH 超时不该让上报循环挂掉）；
//   ② **取不到就是 null**，绝不用 0 代替（0 会被画进曲线、被算进平均，污染整份报表）；
//   ③ 上层（Agent）不写 `if (Windows)` —— 平台差异只落在实现类里。
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 一次"实时指标"采样的原始读数。
/// <para>
/// <b>与 <see cref="AgentMetricsSampleDto"/> 的区别</b>：这里没有时间戳、没有
/// <c>UptimeSeconds</c> —— 那些是"上报"这件事的属性，由 Agent 层补。
/// 端口只负责回答"此刻这台机器多忙"。
/// </para>
/// <para>
/// <c>Bps</c> 类的项<b>需要两次采样求差</b>（计数器是累计值）：实现内部保存上一次读数，
/// 因此<b>本接口的实现必须是有状态的单例</b>，且第一次调用时它们只能是 null。
/// </para>
/// </summary>
/// <param name="Devices">
/// <b>逐设备读数</b>（2026-10-13）。每种实现只填自己认得的那几种设备：
/// Windows 的 PDH 源填 CPU / 磁盘 / 显卡，BCL 的网卡采样器填网卡。
/// 为 null / 空 = 这个源不提供逐设备明细（页面按"没有"处理，不是"全是 0"）。
/// </param>
public sealed record HostMetricReading(
    double? CpuPct,
    long? MemUsedMb,
    long? MemTotalMb,
    double? DiskReadBps,
    double? DiskWriteBps,
    double? NetUpBps,
    double? NetDownBps,
    double? GpuPct,
    IReadOnlyList<AgentDeviceReadingDto>? Devices = null);

/// <summary>
/// 实时指标采集（M4.2，5 秒一次）。
/// </summary>
public interface IHostMetricsSource
{
    /// <summary>
    /// 采一次。<b>绝不抛</b>；任何取不到的项一律 null。
    /// </summary>
    HostMetricReading Read();
}

/// <summary>
/// 开关机 / 异常事件读数（来源：Windows 事件日志；Linux 上退化为不可用）。
/// </summary>
/// <param name="At">事件时刻。</param>
/// <param name="Kind">见 <see cref="AgentPowerEventKinds"/>。</param>
/// <param name="EventId">原始事件 ID。</param>
/// <param name="RecordId">
/// 事件日志的记录号。<b>它是增量拉取的唯一可靠水位</b> —— 时间戳会因服务重启、
/// 时钟校正而出现"同一秒多条"甚至"倒流"，记录号单调递增，不会。
/// </param>
/// <param name="Actor">发起者（仅 1074 有）。</param>
/// <param name="Detail">原始补充信息。</param>
public sealed record HostPowerEventReading(
    DateTimeOffset At,
    string Kind,
    int? EventId,
    long? RecordId,
    string? Actor,
    string? Detail);

/// <summary>一次开关机事件的读取结果。</summary>
/// <param name="Events">按时间升序。</param>
/// <param name="MaxRecordId">本批见到的最大记录号（调用方持久化它，下次传进 <c>afterRecordId</c>）。</param>
public sealed record HostPowerEventBatch(IReadOnlyList<HostPowerEventReading> Events, long? MaxRecordId)
{
    public static HostPowerEventBatch Empty { get; } = new(Array.Empty<HostPowerEventReading>(), null);
}

/// <summary>
/// 开关机 / 异常事件采集（M4.2）。
/// </summary>
public interface IPowerEventSource
{
    /// <summary>本平台是否支持（Linux 未实现 ⇒ false，页面显示"当前平台不支持开关机记录"而不是空表）。</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 读 <paramref name="from"/> 之后、且记录号大于 <paramref name="afterRecordId"/> 的事件。
    /// <para>
    /// <b>两个条件都要</b>：只看时间会因为"时钟被校正过"而重复或漏掉；
    /// 只看记录号则在日志被清理/回绕后（记录号变小）会永远读不到新事件。
    /// 两者取交集，再靠服务端的 (AgentId, At, EventId) 去重兜底。
    /// </para>
    /// </summary>
    /// <param name="from">回溯起点（Agent 首次启动时的"回溯补扫"用得上）。</param>
    /// <param name="afterRecordId">上次读到的最大记录号；首次传 null。</param>
    HostPowerEventBatch Read(DateTimeOffset from, long? afterRecordId);
}

/// <summary>
/// 硬件清单采集（M4.2，启动时 + 每 6 小时一次）。
/// <para>
/// 返回类型直接用契约 DTO：它就是"上报载荷"本身，再造一个同形状的中间类型
/// 只会多一次映射和一处漂移风险。
/// </para>
/// </summary>
public interface IHostInventorySource
{
    /// <summary>采一份清单。<b>绝不抛</b>；各段取不到就是空列表 / null。</summary>
    AgentInventoryDto Collect();
}

/// <summary>
/// 一次温度采样的读数（M4.3）。
/// <para>
/// 三项都是独立的可选量：一台台式机可能只有 CPU 温度（没有 GPU 传感器），
/// 一块 NVMe 可能不暴露温度。<b>取不到就是 null</b>，不参与任何"平均"或"求和"。
/// </para>
/// </summary>
public sealed record HostThermalReading(double? CpuC, double? GpuC, double? StorageC)
{
    public static HostThermalReading Empty { get; } = new(null, null, null);
}

/// <summary>
/// 硬件温度采集（M4.3，<b>可选能力</b>）。
/// <para>
/// 🔥 <b>这是全项目唯一一处"不保证拿得到"的采集</b>：Windows 上要读 CPU/主板温度必须加载
/// 一个内核级驱动（LibreHardwareMonitor 内嵌的 WinRing0），而它需要管理员权限、
/// 可能被安全软件拦截、在部分虚拟机上根本不支持。
/// </para>
/// <para>
/// 因此这个端口的契约是"<b>三态 + 永不抛</b>"：
/// </para>
/// <list type="bullet">
///   <item><see cref="State"/> 是<see cref="AgentThermalStates"/> 里的值，说明"为什么有/没有温度"；</item>
///   <item>初始化<b>只尝试一次</b>：失败就永久停在 <c>failed</c>，不重试、不刷日志
///         （一个每 5 秒重试一次的驱动加载会把事件日志刷爆，还会让杀软反复弹窗）；</item>
///   <item>未启用时实现<b>连驱动都不碰</b> —— 不释放文件、不加载、不装服务。</item>
/// </list>
/// </summary>
public interface IThermalSource
{
    /// <summary>见 <see cref="AgentThermalStates"/>。进程生命周期内恒定（初始化一次）。</summary>
    string State { get; }

    /// <summary>状态的人话补充（失败原因是这里），没有就是 null。</summary>
    string? Note { get; }

    /// <summary>
    /// 采一次温度。<b>绝不抛</b>；未启用 / 失败 / 该硬件没传感器时对应项为 null。
    /// <para>首次调用会触发驱动加载（若已启用），因此可能比后续调用慢（数十~数百毫秒）。</para>
    /// </summary>
    HostThermalReading Read();
}

/// <summary>
/// 没有温度能力的平台上的兜底（Linux / 未启用驱动的 Windows）。
/// <para>注册它而不是"不注册"，理由与 <see cref="NoopPowerEventSource"/> 完全一致：
/// 让上层一个 if 都不写。</para>
/// </summary>
/// <param name="note">为什么没有温度（会显示在页面上）。null 时页面只说"未启用"。</param>
public sealed class NoopThermalSource(string? note = null) : IThermalSource
{
    public string State => AgentThermalStates.Disabled;

    public string? Note { get; } = note;

    public HostThermalReading Read() => HostThermalReading.Empty;
}

/// <summary>
/// 不可用平台上的兜底（Linux 客户端尚未实现事件日志读取）。
/// <para>注册它而不是"不注册"，是为了让上层代码<b>一个 if 都不写</b>：
/// 页面上的"当前平台不支持"由 <see cref="IsAvailable"/> 一路带上去。</para>
/// </summary>
public sealed class NoopPowerEventSource : IPowerEventSource
{
    public bool IsAvailable => false;

    public HostPowerEventBatch Read(DateTimeOffset from, long? afterRecordId) => HostPowerEventBatch.Empty;
}

/// <summary>
/// 纯 BCL 的兜底指标实现。
/// <para>
/// <b>能力边界（刻意如此）</b>：
/// </para>
/// <list type="bullet">
///   <item>网络速率：<b>可用</b>（<c>NetworkInterface</c> 的累计字节差分，跨平台）；</item>
///   <item>内存：Linux 读 <c>/proc/meminfo</c>；其它平台 null（Windows 由 WindowsHostMetricsSource 负责）；</item>
///   <item>CPU：Linux 读 <c>/proc/stat</c>；其它平台 null；</item>
///   <item>磁盘速率 / GPU：null（需要 PDH / 驱动）。</item>
/// </list>
/// <para>
/// ⚠️ Windows 上它会与 <c>WindowsHostMetricsSource</c> <b>并存</b>：
/// 后者提供 CPU/内存/磁盘/GPU，前者补网络速率。两者由组合根合成一个
/// <see cref="CompositeHostMetricsSource"/>，谁取到算谁的。
/// </para>
/// </summary>
public sealed class BasicHostMetricsSource : IHostMetricsSource
{
    private readonly NetworkRateSampler _network = new();

    private long? _prevCpuTotal;
    private long? _prevCpuIdle;

    public HostMetricReading Read()
    {
        var net = _network.SampleDetailed();

        var (used, total) = ReadLinuxMemory();
        var cpu = ReadLinuxCpuPercent();

        return new HostMetricReading(
            cpu,
            used,
            total,
            null,
            null,
            net.UpBps,
            net.DownBps,
            null,
            // 这一路只提供网卡明细（CPU / 磁盘 / 显卡的明细在 Windows 的 PDH 源里）。
            net.Devices.Count > 0 ? net.Devices : null);
    }

    private static (long? UsedMb, long? TotalMb) ReadLinuxMemory()
    {
        if (!OperatingSystem.IsLinux()) return (null, null);

        try
        {
            long? total = null;
            long? available = null;

            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    total = ParseKb(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    available = ParseKb(line);
                }

                if (total is not null && available is not null) break;
            }

            if (total is not { } t || available is not { } a || t <= 0) return (null, null);

            return ((t - a) / 1024, t / 1024);
        }
        catch
        {
            return (null, null);
        }

        // "MemTotal:       16384000 kB" → 16384000
        static long? ParseKb(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], out var kb) ? kb : null;
        }
    }

    /// <summary>
    /// Linux 整机 CPU 占用率：读 <c>/proc/stat</c> 的 <c>cpu</c> 行并做差分。
    /// <para>第一次调用返回 null（没有上一帧可比）。</para>
    /// </summary>
    private double? ReadLinuxCpuPercent()
    {
        if (!OperatingSystem.IsLinux()) return null;

        try
        {
            var first = File.ReadLines("/proc/stat").FirstOrDefault();

            if (first is null || !first.StartsWith("cpu ", StringComparison.Ordinal)) return null;

            var values = first.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(x => long.TryParse(x, out var v) ? v : 0L)
                .ToArray();

            if (values.Length < 5) return null;

            // user nice system idle iowait irq softirq steal …
            var idle = values[3] + values[4];
            var total = values.Sum();

            var prevTotal = _prevCpuTotal;
            var prevIdle = _prevCpuIdle;

            _prevCpuTotal = total;
            _prevCpuIdle = idle;

            if (prevTotal is not { } pt || prevIdle is not { } pi) return null;

            var deltaTotal = total - pt;
            var deltaIdle = idle - pi;

            if (deltaTotal <= 0) return null;

            var busy = (deltaTotal - deltaIdle) * 100.0 / deltaTotal;
            return busy < 0 ? 0 : busy > 100 ? 100 : busy;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 把多个采集源合并成一个（谁取到算谁的；全都没有就是 null）。
/// <para>
/// 存在的意义：Windows 上"网络速率"走 BCL、"CPU/磁盘/GPU"走 PDH，
/// 分成两个实现比在 PDH 那个类里塞一处 BCL 调用更清楚，而上层只想看到"一个源"。
/// </para>
/// </summary>
public sealed class CompositeHostMetricsSource : IHostMetricsSource
{
    private readonly IReadOnlyList<IHostMetricsSource> _sources;

    public CompositeHostMetricsSource(params IHostMetricsSource[] sources)
        => _sources = sources ?? Array.Empty<IHostMetricsSource>();

    public HostMetricReading Read()
    {
        double? cpu = null, diskRead = null, diskWrite = null, netUp = null, netDown = null, gpu = null;
        long? memUsed = null, memTotal = null;

        // ⚠️ 逐设备明细**合并而不是"第一个非空取胜"**：Windows 上磁盘/CPU/显卡在 PDH 源里、
        //    网卡在 BCL 源里 —— 取第一个非空的话，谁排在前面谁就把另一边的设备全吃掉。
        //    按 (Kind, Key) 去重：同一个设备被两个源都报了才可能撞上，后报的覆盖先报的。
        var devices = new List<AgentDeviceReadingDto>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var source in _sources)
        {
            HostMetricReading reading;

            try
            {
                reading = source.Read();
            }
            catch
            {
                // 铁律：采集失败不该影响别的源（例如 PDH 挂了但网络速率还想要）。
                continue;
            }

            cpu ??= reading.CpuPct;
            memUsed ??= reading.MemUsedMb;
            memTotal ??= reading.MemTotalMb;
            diskRead ??= reading.DiskReadBps;
            diskWrite ??= reading.DiskWriteBps;
            netUp ??= reading.NetUpBps;
            netDown ??= reading.NetDownBps;
            gpu ??= reading.GpuPct;

            if (reading.Devices is not { Count: > 0 } fromSource) continue;

            foreach (var device in fromSource)
            {
                var dedupeKey = $"{device.Kind}\u0001{device.Key}";

                if (index.TryGetValue(dedupeKey, out var at))
                {
                    devices[at] = device;
                }
                else
                {
                    index[dedupeKey] = devices.Count;
                    devices.Add(device);
                }
            }
        }

        return new HostMetricReading(
            cpu,
            memUsed,
            memTotal,
            diskRead,
            diskWrite,
            netUp,
            netDown,
            gpu,
            devices.Count > 0 ? devices : null);
    }
}

/// <summary>
/// 网卡速率的差分采样器（BCL，跨平台）。
/// <para>
/// <b>为什么不用 PDH 的 <c>\Network Interface(*)\Bytes Received/sec</c></b>：
/// 那个计数器把回环、虚拟网卡、Hyper-V 交换机全都算进来，一台装了 Docker/WSL 的机器
/// 会报出与实际链路无关的大数字。走 <c>NetworkInterface</c> 可以自己把
/// <c>Loopback</c> / <c>Tunnel</c> / 非 <c>Up</c> 的适配器剔掉，
/// 得到的数更接近"这台机器真的在收发多少"。
/// </para>
/// <para>
/// 按适配器分别记上一次的累计值（而不是只记一个总数）：插拔网线、VPN 连断时
/// 适配器集合会变，只记总数会把"少了一块网卡"误算成"负流量"。
/// </para>
/// </summary>
public sealed class NetworkRateSampler
{
    private readonly Dictionary<string, (long Received, long Sent, long Timestamp)> _previous = new(StringComparer.Ordinal);

    /// <summary>差分采样。首次（或距上次不足 1 秒）返回 (null, null)。</summary>
    public (double? UpBps, double? DownBps) Sample()
    {
        var detailed = SampleDetailed();

        return (detailed.UpBps, detailed.DownBps);
    }

    /// <summary>
    /// 差分采样，**并且给出逐网卡的速率**（2026-10-13）。
    /// <para>
    /// 整机值与逐网卡值是**同一次遍历**算出来的：分成两次遍历会各自更新
    /// <c>_previous</c>，第二次遍历的"上一次"就变成了刚刚 —— 于是逐网卡速率恒为 0。
    /// 这是"看起来很自然的两行代码"会踩的坑，所以刻意做成一个方法返回两样东西。
    /// </para>
    /// </summary>
    public (double? UpBps, double? DownBps, IReadOnlyList<AgentDeviceReadingDto> Devices) SampleDetailed()
    {
        var devices = new List<AgentDeviceReadingDto>();

        try
        {
            var now = Environment.TickCount64;
            long totalReceived = 0;
            long totalSent = 0;
            long totalElapsedTicks = 0;
            var usable = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;

                    var stats = nic.GetIPv4Statistics();
                    var key = nic.Id;
                    seen.Add(key);

                    double? upPerNic = null;
                    double? downPerNic = null;

                    if (_previous.TryGetValue(key, out var prev))
                    {
                        // 计数器回绕（或网卡重置）会得到负值 —— 跳过这一块，不算进总量。
                        var deltaReceived = stats.BytesReceived - prev.Received;
                        var deltaSent = stats.BytesSent - prev.Sent;

                        if (deltaReceived >= 0 && deltaSent >= 0)
                        {
                            var elapsedTicks = now - prev.Timestamp;

                            // 单块网卡的速率用自己的时间差算：多块网卡的上一次时刻未必相同
                            //（有的刚插上），用整机那个时间差去除会得出荒谬的大数。
                            if (elapsedTicks >= 1000)
                            {
                                var seconds = elapsedTicks / 1000.0;
                                downPerNic = deltaReceived / seconds;
                                upPerNic = deltaSent / seconds;
                            }

                            totalReceived += deltaReceived;
                            totalSent += deltaSent;

                            if (totalElapsedTicks == 0) totalElapsedTicks = elapsedTicks;
                        }
                    }

                    _previous[key] = (stats.BytesReceived, stats.BytesSent, now);

                    // 🔥 Key 用适配器 **Id（GUID）**，不用名字：网卡被重命名（"以太网" → "展厅上联"）
                    //    是现场常做的事，用名字当 Key 会让那一天的曲线断成两截。
                    devices.Add(new AgentDeviceReadingDto(
                        AgentDeviceKinds.Net,
                        key,
                        SafeName(nic),
                        UpBps: upPerNic,
                        DownBps: downPerNic));

                    usable++;
                }
                catch
                {
                    // 单块网卡读失败不影响其它网卡。
                }
            }

            // 清掉已经消失的适配器，避免字典无限增长 + 重插后拿旧计数算出差值。
            foreach (var key in _previous.Keys.Where(k => !seen.Contains(k)).ToList()) _previous.Remove(key);

            if (usable == 0 || totalElapsedTicks < 1000) return (null, null, devices);

            var totalSeconds = totalElapsedTicks / 1000.0;

            return (totalSent / totalSeconds, totalReceived / totalSeconds, devices);
        }
        catch
        {
            return (null, null, devices);
        }
    }

    /// <summary>网卡的展示名：优先"描述（名字）"，取不到就退回名字，再退回 Id。</summary>
    private static string SafeName(NetworkInterface nic)
    {
        try
        {
            var name = nic.Name;

            // ⚠️ Description 与 Name 在 Windows 上通常是"Intel(R) Ethernet Connection I219-V"
            //    与"以太网" —— 后者是用户可改的别名，前者才是那块芯片。
            //    两个都放上：现场报修时"以太网"分不清是哪一块，"I219-V"运维未必认得。
            var description = nic.Description;

            if (!string.IsNullOrWhiteSpace(description) && !string.Equals(description, name, StringComparison.Ordinal))
            {
                return $"{description}（{name}）";
            }

            return string.IsNullOrWhiteSpace(name) ? nic.Id : name;
        }
        catch
        {
            return nic.Id;
        }
    }
}

/// <summary>
/// 纯 BCL 的硬件清单兜底：只给卷、操作系统与启动时间。
/// <para>内存条 / 磁盘型号 / 显卡 / 显示器在 Windows 上由 WMI 实现提供。</para>
/// </summary>
public sealed class BasicHostInventorySource : IHostInventorySource
{
    public AgentInventoryDto Collect()
    {
        var now = DateTimeOffset.Now;
        var uptime = SafeUptime();

        return new AgentInventoryDto(
            now,
            Cpu: null,
            TotalMemoryMb: null,
            MemoryModules: Array.Empty<AgentMemoryModuleDto>(),
            Volumes: ReadVolumes(),
            Disks: Array.Empty<AgentDiskDto>(),
            Gpus: Array.Empty<AgentGpuDto>(),
            Displays: Array.Empty<AgentDisplayDto>(),
            OsVersion: Environment.OSVersion.VersionString,
            OsBuild: Environment.OSVersion.Version.Build.ToString(),
            OsArchitecture: System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            Domain: null,
            BootTime: uptime is { } seconds ? now - TimeSpan.FromSeconds(seconds) : null,
            UptimeSeconds: uptime,
            LoggedOnUser: SafeUserName());
    }

    /// <summary>
    /// 系统已运行秒数。
    /// <para><c>Environment.TickCount64</c> 是"自开机以来的毫秒数"，不依赖 <c>DateTime.Now</c>，
    /// 因此系统时钟被校正/回拨也不会算出负的在线时长。</para>
    /// </summary>
    public static long? SafeUptime()
    {
        try
        {
            return Environment.TickCount64 / 1000;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeUserName()
    {
        try
        {
            return Environment.UserName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>各分区容量。<b>固定盘与可移动盘都收，但跳过光驱等无介质的卷</b>。</summary>
    private static List<AgentVolumeDto> ReadVolumes()
    {
        var result = new List<AgentVolumeDto>();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;

                    var totalMb = drive.TotalSize / 1024 / 1024;
                    if (totalMb <= 0) continue;

                    result.Add(new AgentVolumeDto(
                        drive.Name.TrimEnd('\\'),
                        string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel,
                        drive.DriveFormat,
                        totalMb,
                        drive.AvailableFreeSpace / 1024 / 1024));
                }
                catch
                {
                    // 单个卷读失败（例如权限）跳过。
                }
            }
        }
        catch
        {
            // 整段失败就当没有分区信息。
        }

        return result;
    }
}
