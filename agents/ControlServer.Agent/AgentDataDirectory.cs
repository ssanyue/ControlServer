using ControlServer.Core.Abstractions;

namespace ControlServer.Agent;

/// <summary>
/// 客户端 Agent 自己的数据目录实现（M4.1）。
/// <para>
/// <b>为什么不复用 Infrastructure 的 <c>FileSystemDataDirectory</c></b>：
/// 那一个住在 <c>ControlServer.Infrastructure</c> 里，而那个工程引用了 EF Core
/// 与 ASP.NET Core —— 客户端为了一个"取几个路径"的能力，会把整套数据访问栈
/// 打进安装包。违反"客户端与服务端是两个独立部署物"的边界（见 Agent.csproj 的注释）。
/// </para>
/// <para>
/// <b>那些与服务端有关的路径（数据库 / 授权 / 配置）在这里没有意义</b>，
/// 但接口要求给值。它们一律指向状态目录下的同名子路径 ——
/// "不用的东西也有个合理去处"，胜过返回空串让下游去判空。
/// </para>
/// </summary>
public sealed class AgentDataDirectory : IDataDirectory
{
    public AgentDataDirectory(string? configuredStateDirectory)
    {
        Root = AgentPaths.ResolveStateDirectory(configuredStateDirectory);
    }

    public string Root { get; }

    // ── 客户端**真正用到**的两个 ──

    public string LogDirectory => Path.Combine(Root, "logs");

    // ── 以下为接口完整性（客户端不使用数据库 / 授权 / 图片 / 分发）──

    public string DatabaseFile => Path.Combine(Root, "agent.db");

    public string ConfigDirectory => Path.Combine(Root, "config");

    public string FilesDirectory => Path.Combine(Root, "files");

    public string ImagesDirectory => Path.Combine(Root, "images");

    public string CertificateDirectory => Path.Combine(Root, "https");

    public string LicenseFile => Path.Combine(Root, "license.lic");

    public string TimeStampFile => Path.Combine(Root, "timesync.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }
}
