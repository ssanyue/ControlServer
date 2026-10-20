using System.Management;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// Windows 上的硬件摘要（M4.1）：CPU 型号 + 物理内存。
/// <para>
/// <b>为什么用 WMI 而不是注册表</b>：CPU 型号在注册表里读得到
/// （<c>HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0</c>），但<b>物理内存总量没有注册表情报</b>
/// —— 只有 <c>Win32_ComputerSystem.TotalPhysicalMemory</c> 或
/// <c>GlobalMemoryStatusEx</c>（P/Invoke）两个来源。既然 WMI 无论如何都要用，
/// 两个值就都走 WMI，省掉一份 P/Invoke 声明与它的结构体对齐坑。
/// </para>
/// <para>
/// <b>每个取值都独立 try/catch 并返回 null</b>：WMI 在某些加固环境里会被策略禁用，
/// 一处失败不该让整条上报链路挂掉 —— 那会让"这台机器在列表里永远没有硬件信息"，
/// 而实情只是"某个查询被拦了"。
/// </para>
/// <para>
/// <b>⚠️ 未知一律返回 null，绝不返回 0</b>：界面上"内存 0 MB"是"机器有问题"的信号，
/// 而真相往往只是"没查出来"。0 与 null 的区别在页面上就是"红警"与"—"。
/// </para>
/// </summary>
public sealed class WindowsHardwareInfo : IHostHardwareInfo
{
    private readonly Lazy<string?> _cpuName = new(ReadCpuName, isThreadSafe: true);
    private readonly Lazy<long?> _totalMemoryMb = new(ReadTotalMemoryMb, isThreadSafe: true);

    public string? CpuName => OperatingSystem.IsWindows() ? _cpuName.Value : null;

    public long? TotalMemoryMb => OperatingSystem.IsWindows() ? _totalMemoryMb.Value : null;

    private static string? ReadCpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_Processor");

            foreach (var item in searcher.Get())
            {
                using var processor = (ManagementObject)item;

                if (processor["Name"] is string name && !string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
            }
        }
        catch
        {
            // 见类注释：取不到就是 null，不抛、不返回 0。
        }

        return null;
    }

    private static long? ReadTotalMemoryMb()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");

            foreach (var item in searcher.Get())
            {
                using var system = (ManagementObject)item;

                if (system["TotalPhysicalMemory"] is { } raw
                    && ulong.TryParse(raw.ToString(), out var bytes)
                    && bytes > 0)
                {
                    return (long)(bytes / 1024 / 1024);
                }
            }
        }
        catch
        {
            // 同上。
        }

        return null;
    }
}
