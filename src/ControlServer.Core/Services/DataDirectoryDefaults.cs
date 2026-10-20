namespace ControlServer.Core.Services;

/// <summary>各平台默认数据根目录。</summary>
public static class DataDirectoryDefaults
{
    public const string WindowsFolderName = "ControlServer";
    public const string LinuxRoot = "/var/lib/controlserver";

    public static string ResolveDefaultRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(common)) common = AppContext.BaseDirectory;
            return Path.Combine(common, WindowsFolderName);
        }

        return LinuxRoot;
    }
}
