using System.Runtime.Versioning;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;

namespace ControlServer.Platform.Windows;

/// <summary>
/// Windows 机器码：WMI 采集 CPU / 主板 / 产品 UUID → SHA256 → XXXX-XXXX-XXXX-XXXX。
/// 采集失败时回退到持久化安装标识（保证机器码稳定，不会每次启动变化）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMachineCodeProvider : IMachineCodeProvider
{
    private readonly IDataDirectory _dataDirectory;

    public WindowsMachineCodeProvider(IDataDirectory dataDirectory)
    {
        _dataDirectory = dataDirectory;
    }

    public string GetRawFingerprint()
    {
        var cpu = Query("Win32_Processor", "ProcessorId");
        var board = Query("Win32_BaseBoard", "SerialNumber");
        var uuid = Query("Win32_ComputerSystemProduct", "UUID");

        var raw = $"{cpu}|{board}|{uuid}";
        if (!string.IsNullOrWhiteSpace(raw.Trim('|'))) return raw;

        return $"install-id:{InstallIdentity.GetOrCreate(_dataDirectory.Root)}";
    }

    public string GetMachineCode() => MachineCodeFormatter.ToMachineCode(GetRawFingerprint());

    private static string Query(string wmiClass, string property)
    {
        if (!OperatingSystem.IsWindows()) return string.Empty;

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
            foreach (var instance in searcher.Get())
            {
                var value = instance[property]?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
        }
        catch
        {
            // WMI 在容器 / 受限权限下可能不可用 —— 交由上层回退。
        }

        return string.Empty;
    }
}
