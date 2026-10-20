using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using ControlServer.Contracts;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent;

/// <summary>
/// 采集本机的主机摘要（M4.1；完整硬件清单在 M4.2 补齐）。
/// <para>
/// <b>每一项都必须"取不到就留空"，绝不允许抛异常</b>：这段代码跑在注册路径上，
/// 一次 WMI 超时或一块网卡的驱动异常就会让"这台机器永远注册不上" ——
/// 而它其实完全正常，只是某个字段查不到。
/// </para>
/// <para>
/// <b>⚠️ 未知一律为 null，不用 0 代替。</b> 界面上"内存 0 MB"是"机器有问题"的信号，
/// 而真相往往只是"没查出来"。0 与 null 在页面上就是"红警"与"—"的区别。
/// </para>
/// </summary>
public static class HostInfoCollector
{
    /// <summary>采集主机信息。</summary>
    /// <param name="machineKey">机器稳定标识（由 <c>IMachineCodeProvider</c> 提供）。</param>
    /// <param name="agentVersion">客户端版本。</param>
    /// <param name="hardware">平台硬件信息实现；null 时退化为 BCL 能提供的信息。</param>
    /// <param name="displayNameOverride">显示名覆盖（配置里填了就用它）。</param>
    public static AgentHostInfoDto Collect(
        string machineKey,
        string agentVersion,
        IHostHardwareInfo? hardware,
        string? displayNameOverride = null)
    {
        return new AgentHostInfoDto(
            MachineKey: machineKey,
            HostName: ResolveHostName(displayNameOverride),
            OsDescription: ResolveOsDescription(),
            CpuName: ResolveCpuName(hardware),
            LogicalProcessors: SafeProcessorCount(),
            TotalMemoryMb: ResolveMemoryMb(hardware),
            AgentVersion: agentVersion,
            UserName: SafeUserName(),
            Interfaces: CollectInterfaces());
    }

    private static string ResolveHostName(string? displayNameOverride)
    {
        if (!string.IsNullOrWhiteSpace(displayNameOverride)) return displayNameOverride.Trim();

        try
        {
            return Environment.MachineName;
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ResolveOsDescription()
    {
        try
        {
            return RuntimeInformation.OSDescription;
        }
        catch
        {
            return "unknown";
        }
    }

    private static string? ResolveCpuName(IHostHardwareInfo? hardware)
    {
        try
        {
            var name = hardware?.CpuName;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch
        {
            // 落到环境变量兜底。
        }

        try
        {
            var identifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
            return string.IsNullOrWhiteSpace(identifier) ? null : identifier.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static long? ResolveMemoryMb(IHostHardwareInfo? hardware)
    {
        try
        {
            var value = hardware?.TotalMemoryMb;
            return value is > 0 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? SafeProcessorCount()
    {
        try
        {
            var count = Environment.ProcessorCount;
            return count > 0 ? count : null;
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

    /// <summary>
    /// 采集全部已启用网卡（IPv4）。
    /// <para>
    /// <b>为什么上报"全部"而不是"主网卡"</b>：客户机常有多个网段（控制网 + 办公网），
    /// 而中心服务端在哪个网段上事先不知道。全报上去，排障时一眼能看出
    /// "这台机器有 10.0.1.7 和 192.168.1.20，服务端在 10.0.1.x —— 通路是通的"。
    /// </para>
    /// </summary>
    private static List<AgentNetworkInterfaceDto> CollectInterfaces()
    {
        var result = new List<AgentNetworkInterfaceDto>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    // 回环、隧道、未启用的一律不报：它们对"能不能连通服务端"毫无信息量，
                    // 却会让界面上的网卡列表变成一长串噪音。
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;

                    var mac = FormatMac(nic.GetPhysicalAddress()?.GetAddressBytes());

                    foreach (var address in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                        result.Add(new AgentNetworkInterfaceDto(
                            nic.Name,
                            address.Address.ToString(),
                            mac,
                            IsUp: true));
                    }
                }
                catch
                {
                    // 单块网卡读失败不该影响其它网卡。
                }
            }
        }
        catch
        {
            // 整段都失败就上报空列表 —— 注册照样要成功。
        }

        return result;
    }

    private static string FormatMac(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return string.Empty;

        return string.Join(':', bytes.Select(b => b.ToString("X2")));
    }
}
