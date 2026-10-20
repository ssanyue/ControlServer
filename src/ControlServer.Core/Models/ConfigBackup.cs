namespace ControlServer.Core.Models;

/// <summary>
/// 配置备份包：携带全部配置，并把所有被引用的图片以 base64 内嵌，
/// 使**单个文件**即可完整携带、跨设备恢复。与旧版 <c>ConfigBackup</c> 同构。
/// </summary>
public sealed class ConfigBackup
{
    /// <summary>备份格式版本，便于后续兼容判断。</summary>
    public string Version { get; set; } = "1.0";

    /// <summary>全部配置（图片路径在导出时已被改写为内嵌键名 <c>imgN.ext</c>）。</summary>
    public ControlSettings Settings { get; set; } = new();

    /// <summary>图片键名（如 <c>img0.png</c>） → base64 内容。</summary>
    public Dictionary<string, string> Images { get; set; } = new();
}
