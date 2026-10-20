using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;

namespace ControlServer.Platform.Linux;

/// <summary>
/// Linux 机器码：/etc/machine-id（或 dbus machine-id）+ DMI product_uuid → SHA256。
/// 两者都不可得时回退到持久化安装标识。
/// </summary>
public sealed class LinuxMachineCodeProvider : IMachineCodeProvider
{
    private readonly IDataDirectory _dataDirectory;

    public LinuxMachineCodeProvider(IDataDirectory dataDirectory)
    {
        _dataDirectory = dataDirectory;
    }

    public string GetRawFingerprint()
    {
        var parts = new List<string>();

        var machineId = ReadFirstLine("/etc/machine-id") ?? ReadFirstLine("/var/lib/dbus/machine-id");
        if (!string.IsNullOrWhiteSpace(machineId)) parts.Add(machineId);

        var uuid = ReadFirstLine("/sys/class/dmi/id/product_uuid");
        if (!string.IsNullOrWhiteSpace(uuid)) parts.Add(uuid);

        if (parts.Count == 0) parts.Add(InstallIdentity.GetOrCreate(_dataDirectory.Root));

        return string.Join("|", parts);
    }

    public string GetMachineCode() => MachineCodeFormatter.ToMachineCode(GetRawFingerprint());

    private static string? ReadFirstLine(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path).FirstOrDefault()?.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
