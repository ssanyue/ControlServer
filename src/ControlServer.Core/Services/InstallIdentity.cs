namespace ControlServer.Core.Services;

/// <summary>
/// 安装标识：当硬件指纹不可得（WMI 失败 / 容器环境）时的稳定回退来源。
/// 首次生成后持久化，重启不变。
/// </summary>
public static class InstallIdentity
{
    private const string FileName = "install.id";

    public static string GetOrCreate(string dataDirectory)
    {
        try
        {
            var file = Path.Combine(dataDirectory, FileName);
            if (File.Exists(file))
            {
                var existing = File.ReadAllText(file).Trim();
                if (existing.Length > 0) return existing;
            }

            Directory.CreateDirectory(dataDirectory);
            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(file, id);
            return id;
        }
        catch
        {
            return Environment.MachineName;
        }
    }
}
