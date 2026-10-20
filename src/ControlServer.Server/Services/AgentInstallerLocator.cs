using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Server.Options;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.Services;

/// <summary>
/// 客户端安装包的定位（M4.1）。
/// <para>
/// <b>它解决一个很具体的尴尬</b>：现场工程师第一次接触本系统，最需要的入口是
/// "把客户端拿下来装到那台机器上"，而在此之前他必须先能登录网页。
/// 于是顶栏右上角有一个下载按钮 —— 它背后的东西就是这个类。
/// </para>
/// <para>
/// <b>安装包放哪</b>：数据目录下的 <c>downloads\</c>（默认），文件名形如
/// <c>ControlServer-Agent-Setup-0.1.0.exe</c>。
/// </para>
/// <para>
/// <b>⚠️ 为什么不放进发布目录</b>：绿色版发布目录必须<b>恰好 1 个文件</b>
/// （<c>build-portable-exe.ps1</c> 有硬校验，那是"双击即用"这个承诺的载体）。
/// 往里塞安装包会直接把那条校验打红。
/// </para>
/// <para>
/// <b>版本不一致只提示、不阻断</b>：现场常有"客户端还差一版"的过渡期，
/// 把下载按钮禁掉只会让人退回"私下拷文件"这条更不可控的路。
/// </para>
/// </summary>
public sealed class AgentInstallerLocator
{
    /// <summary>约定前缀（构建脚本按它命名，解析出的版本号也来自它）。</summary>
    private const string Prefix = "ControlServer-Agent-Setup-";

    private readonly IDataDirectory _dataDirectory;
    private readonly IOptions<ServerOptions> _options;
    private readonly ServerRuntimeInfo _runtime;

    public AgentInstallerLocator(
        IDataDirectory dataDirectory,
        IOptions<ServerOptions> options,
        ServerRuntimeInfo runtime)
    {
        _dataDirectory = dataDirectory;
        _options = options;
        _runtime = runtime;
    }

    /// <summary>安装包目录的绝对路径（未创建也要能给出，页面要把它显示给管理员）。</summary>
    public string DirectoryPath
    {
        get
        {
            var name = _options.Value.Agent.InstallerDirectoryName;
            if (string.IsNullOrWhiteSpace(name)) name = "downloads";
            return Path.Combine(_dataDirectory.Root, name);
        }
    }

    /// <summary>定位当前可下载的安装包。</summary>
    public AgentInstallerInfoDto Locate()
    {
        var serverVersion = _runtime.Version;
        var directory = DirectoryPath;

        if (!System.IO.Directory.Exists(directory))
        {
            return new AgentInstallerInfoDto(
                false, null, 0, null, serverVersion, false,
                $"尚未上传客户端安装包。把它放到：{directory}");
        }

        var files = new DirectoryInfo(directory).GetFiles("*.exe");

        // 优先取"按约定命名"的；没有才退而求其次取任意 exe
        // —— 现场常有人手工改名，一条硬规则会让按钮莫名其妙变灰。
        var chosen =
            files.Where(f => f.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                 .OrderByDescending(f => f.LastWriteTimeUtc)
                 .FirstOrDefault()
            ?? files.OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();

        if (chosen is null)
        {
            return new AgentInstallerInfoDto(
                false, null, 0, null, serverVersion, false,
                $"目录里没有安装包（.exe）。放到：{directory}");
        }

        var version = ParseVersion(chosen.Name);
        var mismatch = version is not null && !SameMajorMinor(version, serverVersion);

        var hint = mismatch
            ? $"安装包版本 {version} 与服务端 {serverVersion} 的主次版本不一致，建议重新构建客户端安装包。"
            : $"客户端安装包 {chosen.Name}";

        return new AgentInstallerInfoDto(
            true,
            chosen.Name,
            chosen.Length,
            version,
            serverVersion,
            mismatch,
            hint);
    }

    /// <summary>把文件名里的版本解析出来（<c>ControlServer-Agent-Setup-0.1.0.exe</c> → <c>0.1.0</c>）。</summary>
    private static string? ParseVersion(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);

        if (name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            var tail = name[Prefix.Length..];
            return string.IsNullOrWhiteSpace(tail) ? null : tail;
        }

        return null;
    }

    /// <summary>
    /// 主次版本是否一致。
    /// <para>只比前两段：修订号差异（0.1.0 vs 0.1.3）是正常迭代，每次都提示会让提示失去意义。</para>
    /// </summary>
    private static bool SameMajorMinor(string left, string right)
        => string.Equals(MajorMinor(left), MajorMinor(right), StringComparison.OrdinalIgnoreCase);

    private static string MajorMinor(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : version;
    }
}
