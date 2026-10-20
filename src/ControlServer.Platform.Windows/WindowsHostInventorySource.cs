using System.Management;
using ControlServer.Contracts;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// Windows 上的硬件清单采集（M4.2）：CPU / 内存条 / 分区 / 物理磁盘 / 显卡 / 操作系统 / 启动时间。
/// <para>
/// <b>全部走 WMI，但每一条查询独立 try/catch</b>：某些加固环境会禁用 WMI，
/// 那时"内存条列表拿不到"不该让"分区容量"也一起消失。
/// </para>
/// <para>
/// <b>⚠️ 为什么这里<span>没有</span>显示器（多屏分辨率）</b>：
/// Agent 服务进程运行在 <b>Session 0</b>，它<b>没有交互式桌面</b> ——
/// <c>EnumDisplayMonitors</c> 在 Session 0 里枚举到的是"服务桌面"，通常返回 0 个显示器
/// 或者一个假的 1024×768。把它填进清单就是<b>一个看着像真的的错数据</b>，
/// 而远控（M5）恰恰要靠这个信息判断屏幕布局。
/// </para>
/// <para>
/// 正确做法是由<b>用户会话里的托盘进程</b>采集后上报 —— 那条链路属于 M5 的前置工作，
/// 因此 M4.2 明确地把 <c>Displays</c> 留成空列表，页面上显示"待托盘进程上报"。
/// </para>
/// </summary>
public sealed class WindowsHostInventorySource : IHostInventorySource
{
    public AgentInventoryDto Collect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new BasicHostInventorySource().Collect();
        }

        var now = DateTimeOffset.Now;
        var uptime = BasicHostInventorySource.SafeUptime();

        var (osVersion, osBuild, osArch, bootTime, domain, loggedOn) = ReadOperatingSystem(now, uptime);

        return new AgentInventoryDto(
            now,
            Cpu: ReadCpu(),
            TotalMemoryMb: ReadTotalMemoryMb(),
            MemoryModules: ReadMemoryModules(),
            Volumes: ReadVolumes(),
            Disks: ReadDisks(),
            Gpus: ReadGpus(),
            Displays: Array.Empty<AgentDisplayDto>(),
            OsVersion: osVersion,
            OsBuild: osBuild,
            OsArchitecture: osArch,
            Domain: domain,
            BootTime: bootTime,
            UptimeSeconds: uptime,
            LoggedOnUser: loggedOn);
    }

    // ── CPU ──

    private static AgentCpuInfoDto? ReadCpu()
    {
        return QueryFirst(
            "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed, SocketDesignation FROM Win32_Processor",
            o => new AgentCpuInfoDto(
                Str(o, "Name"),
                Int(o, "NumberOfCores"),
                Int(o, "NumberOfLogicalProcessors"),
                // 当前频率在部分机器上恒为 0（电源管理/虚拟化），那就退回最大频率 —— 但**不编造**。
                Int(o, "CurrentClockSpeed") is { } current && current > 0
                    ? current
                    : Int(o, "MaxClockSpeed"),
                Int(o, "MaxClockSpeed"),
                Str(o, "SocketDesignation")));
    }

    // ── 操作系统 / 域 / 登录用户 ──

    private static (string? Version, string? Build, string? Arch, DateTimeOffset? Boot, string? Domain, string? User)
        ReadOperatingSystem(DateTimeOffset now, long? uptime)
    {
        string? version = null;
        string? build = null;
        string? arch = null;
        DateTimeOffset? boot = null;
        string? domain = null;
        string? user = null;

        try
        {
            foreach (var o in Query("SELECT Caption, Version, BuildNumber, OSArchitecture, LastBootUpTime FROM Win32_OperatingSystem"))
            {
                using (o)
                {
                    var caption = Str(o, "Caption");
                    var rawVersion = Str(o, "Version");

                    version = string.IsNullOrWhiteSpace(caption)
                        ? RuntimeInformationOsDescription()
                        : string.IsNullOrWhiteSpace(rawVersion) ? caption : $"{caption} ({rawVersion})";

                    build = Str(o, "BuildNumber");
                    arch = Str(o, "OSArchitecture");
                    boot = Date(o, "LastBootUpTime");
                }

                break;
            }
        }
        catch
        {
            version ??= RuntimeInformationOsDescription();
        }

        try
        {
            foreach (var o in Query("SELECT Domain, UserName FROM Win32_ComputerSystem"))
            {
                using (o)
                {
                    // 未加域时 Domain 是工作组名或机器名 —— 照样如实上报，
                    // 页面上看"这台机器属于 WORKGROUP"本身也是有效信息。
                    domain = Str(o, "Domain");
                    user = Str(o, "UserName")?.Split('\\').LastOrDefault(s => s.Length > 0);
                }

                break;
            }
        }
        catch
        {
            // 忽略。
        }

        // 系统时钟被校正过时 LastBootUpTime 可能与"开机时长"对不上，以开机时长为准（它来自单调时钟）。
        if (boot is null && uptime is { } seconds) boot = now - TimeSpan.FromSeconds(seconds);

        return (version, build, arch, boot, domain, user);
    }

    private static string RuntimeInformationOsDescription()
    {
        try
        {
            return System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        }
        catch
        {
            return "Windows";
        }
    }

    private static long? ReadTotalMemoryMb()
    {
        try
        {
            foreach (var o in Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
            {
                using (o)
                {
                    // 刻意不用 QueryFirst<long?>：泛型约束限定引用类型（见 QueryFirst 注释），
                    // 值类型走这条直路反而更清楚。
                    var bytes = Long(o, "TotalPhysicalMemory");

                    return bytes is > 0 ? bytes / 1024 / 1024 : null;
                }
            }
        }
        catch
        {
            // 忽略。
        }

        return null;
    }

    // ── 内存条 ──

    private static List<AgentMemoryModuleDto> ReadMemoryModules()
    {
        var result = new List<AgentMemoryModuleDto>();

        try
        {
            foreach (var o in Query("SELECT BankLabel, DeviceLocator, Manufacturer, Capacity, Speed, PartNumber FROM Win32_PhysicalMemory"))
            {
                using (o)
                {
                    var capacityBytes = Long(o, "Capacity");

                    result.Add(new AgentMemoryModuleDto(
                        // BankLabel 常常为空，DeviceLocator（"DIMM_A1"）更常见 —— 两个都试。
                        Str(o, "BankLabel") ?? Str(o, "DeviceLocator"),
                        Str(o, "Manufacturer"),
                        capacityBytes is { } bytes and > 0 ? bytes / 1024 / 1024 : null,
                        Int(o, "Speed"),
                        Str(o, "PartNumber")));
                }
            }
        }
        catch
        {
            // 忽略。
        }

        return result;
    }

    // ── 分区 ──

    private static List<AgentVolumeDto> ReadVolumes() => new BasicHostInventorySource().Collect().Volumes.ToList();

    // ── 物理磁盘 ──

    private static List<AgentDiskDto> ReadDisks()
    {
        var result = new List<AgentDiskDto>();

        try
        {
            foreach (var o in Query("SELECT Index, Model, SerialNumber, Size, MediaType, InterfaceType FROM Win32_DiskDrive"))
            {
                using (o)
                {
                    var sizeBytes = Long(o, "Size");

                    result.Add(new AgentDiskDto(
                        Int(o, "Index"),
                        Str(o, "Model"),
                        Str(o, "SerialNumber")?.Trim(),
                        sizeBytes is { } bytes and > 0 ? (long)Math.Round(bytes / 1024.0 / 1024 / 1024) : null,
                        // MediaType 形如 "Fixed hard disk media"；InterfaceType 是 "SCSI"/"IDE"（NVMe 也报 SCSI）。
                        Str(o, "MediaType") ?? Str(o, "InterfaceType")));
                }
            }
        }
        catch
        {
            // 忽略。
        }

        return result;
    }

    // ── 显卡 ──

    private static List<AgentGpuDto> ReadGpus()
    {
        var result = new List<AgentGpuDto>();

        try
        {
            foreach (var o in Query(
                "SELECT Name, DriverVersion, DriverDate, AdapterRAM, AdapterCompatibility, VideoProcessor, PNPDeviceID FROM Win32_VideoController"))
            {
                using (o)
                {
                    var ram = Long(o, "AdapterRAM");

                    // ⚠️ AdapterRAM 是 UInt32，4 GB 以上的显存会**回绕**成很小的数
                    //（8 GB 显存常报成 4 GB 甚至几十 MB）。因此只在"小于 4 GB"时才敢信它，
                    //否则宁可留空 —— 一个错的显存容量比没有更糟。
                    long? memoryMb = ram is { } bytes and > 0 and < 4L * 1024 * 1024 * 1024
                        ? bytes / 1024 / 1024
                        : null;

                    result.Add(new AgentGpuDto(
                        Str(o, "Name"),
                        Str(o, "DriverVersion"),
                        Date(o, "DriverDate"),
                        memoryMb,
                        Str(o, "AdapterCompatibility"),
                        Str(o, "VideoProcessor"),
                        ParsePciId(Str(o, "PNPDeviceID"))));
                }
            }
        }
        catch
        {
            // 忽略。
        }

        return result;
    }

    /// <summary>
    /// 从 <c>PNPDeviceID</c> 里抠出 PCI 厂商 / 设备号。
    /// <para>
    /// 典型形态：<c>PCI\VEN_10DE&amp;DEV_2504&amp;SUBSYS_...</c> → <c>10DE:2504</c>。
    /// 解析不出来（虚拟适配器 / 远程桌面适配器根本没有这一段）就返回 null ——
    /// <b>不猜</b>。PCI 号在多卡同型号的机器上是唯一能区分两块卡的东西。
    /// </para>
    /// </summary>
    private static string? ParsePciId(string? pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId)) return null;

        try
        {
            var text = pnpDeviceId.ToUpperInvariant();

            var ven = Extract(text, "VEN_");
            var dev = Extract(text, "DEV_");

            return ven is not null && dev is not null ? $"{ven}:{dev}" : null;

            // "VEN_10DE&DEV_2504…" → "10DE"（取到非十六进制字符为止）。
            static string? Extract(string text, string prefix)
            {
                var start = text.IndexOf(prefix, StringComparison.Ordinal);
                if (start < 0) return null;

                start += prefix.Length;

                var end = start;
                while (end < text.Length && Uri.IsHexDigit(text[end])) end++;

                return end > start ? text[start..end] : null;
            }
        }
        catch
        {
            return null;
        }
    }

    // ── WMI 取值助手（每一项都独立容错） ──

    private static IEnumerable<ManagementObject> Query(string wql)
    {
        using var searcher = new ManagementObjectSearcher(wql);

        // ToList 之后再释放 searcher：直接 yield 会让枚举在 searcher 释放后继续（COM 对象已失效）。
        return searcher.Get().Cast<ManagementObject>().ToList();
    }

    private static T? QueryFirst<T>(string wql, Func<ManagementObject, T?> projector) where T : class
    {
        try
        {
            foreach (var o in Query(wql))
            {
                using (o)
                {
                    return projector(o);
                }
            }
        }
        catch
        {
            // 忽略：取不到就是 null。
        }

        return null;
    }

    private static string? Str(ManagementObject o, string property)
    {
        try
        {
            return o[property]?.ToString()?.Trim() is { Length: > 0 } value ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? Int(ManagementObject o, string property)
    {
        try
        {
            var raw = o[property];
            return raw is null ? null : int.TryParse(raw.ToString(), out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static long? Long(ManagementObject o, string property)
    {
        try
        {
            var raw = o[property];
            return raw is null ? null : long.TryParse(raw.ToString(), out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>WMI 的 DMTF 时间字符串（<c>20261012013000.000000+480</c>）→ DateTimeOffset。</summary>
    private static DateTimeOffset? Date(ManagementObject o, string property)
    {
        try
        {
            var raw = o[property];

            if (raw is null) return null;

            var text = raw.ToString();

            if (string.IsNullOrWhiteSpace(text) || text.StartsWith("0000", StringComparison.Ordinal)) return null;

            return new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(text), TimeSpan.FromHours(8));
        }
        catch
        {
            return null;
        }
    }
}
