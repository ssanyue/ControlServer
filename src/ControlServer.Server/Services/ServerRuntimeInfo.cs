using System.Reflection;

namespace ControlServer.Server.Services;

/// <summary>
/// 服务端运行期事实（M4.1）：发现应答与"版本一致性提示"都要用它。
/// <para>
/// 为什么把这些值从 <c>Program.cs</c> 抽出来当依赖注入：它们是在组合根"算出来的"
/// （端口可能来自数据目录里的覆盖文件，不一定是配置里的 8899），
/// 而消费者（发现监听、下载端点）散落在各处 —— 让它们各自去读配置，
/// 就会重演"配置里写 8899、实际听在别的端口"这类最难查的不一致。
/// </para>
/// </summary>
/// <param name="HttpPort">真正在监听的 HTTP 端口（可能来自端口覆盖文件）。</param>
/// <param name="PairingPort">对接码监听端口（发现应答里回传，客户端仅用于展示/排障）。</param>
/// <param name="DiscoveryPort">发现端口。</param>
/// <param name="Version">服务端版本（<c>0.1.0</c> 形式；客户端据此判断安装包是否过旧）。</param>
public sealed record ServerRuntimeInfo(int HttpPort, int PairingPort, int DiscoveryPort, string Version)
{
    /// <summary>本程序集的产品版本（取三段：主.次.修订）。</summary>
    public static string ResolveVersion()
    {
        var version = typeof(ServerRuntimeInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ServerRuntimeInfo).Assembly.GetName().Version?.ToString();

        if (string.IsNullOrWhiteSpace(version)) return "0.0.0";

        // 去掉 source-revision 后缀（形如 0.1.0+abc123）；不处理会让界面显示一长串哈希。
        var plus = version.IndexOf('+');
        var trimmed = plus >= 0 ? version[..plus] : version;

        // 归一成三段：1 → 1.0.0；1.2 → 1.2.0；1.2.3.4 → 1.2.3。
        var parts = trimmed.Split('.');
        if (parts.Length >= 3) return string.Join('.', parts.Take(3));
        if (parts.Length == 2) return $"{parts[0]}.{parts[1]}.0";
        return $"{trimmed}.0.0";
    }

    /// <summary>
    /// 构建标识：<c>InformationalVersion</c> 里 <c>'+'</c> 之后的那一段（形如 <c>build.20261017-2230</c>）。
    /// <para>
    /// <b>为什么必须有这个东西（M6.4）</b>：升级/回滚的可执行性，前提是"能确认现在跑的是哪个包"。
    /// 在此之前 <see cref="ResolveVersion"/> 把 <c>'+'</c> 之后一律丢掉，而三处版本号又各自写死
    /// （<c>Directory.Build.props</c> 0.1.0 / <c>ControlServer.iss</c> 1.0.0 / <c>build-installer.ps1</c> 1.0.0），
    /// 于是升级完、回滚完，日志里那一行**完全一样** —— 无法证明动作生效。
    /// </para>
    /// <para>
    /// ⚠️ 不用程序集文件的 <c>LastWriteTime</c> 兜底：解压/复制会改它，那会造出"看起来有标识但其实是拷贝时间"的假证据。
    /// 没有就返回 <c>null</c>，让调用方明确显示"未注入"（本仓"未知一律 null"的口径）。
    /// </para>
    /// </summary>
    public static string? ResolveBuildStamp()
    {
        var informational = typeof(ServerRuntimeInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational)) return null;

        var plus = informational.IndexOf('+');
        if (plus < 0 || plus == informational.Length - 1) return null;

        var stamp = informational[(plus + 1)..].Trim();
        return stamp.Length == 0 ? null : stamp;
    }

    /// <summary>
    /// 给人看的完整版本号：<c>0.1.0+build.20261017-2230</c>；没注入构建标识时明确写出来，
    /// 不伪装成"看起来正常"。仅用于日志与界面展示 —— <b>不要用它做版本比较</b>
    /// （<see cref="AgentInstallerLocator"/> 的主次版本比对走 <see cref="ResolveVersion"/>）。
    /// </summary>
    public static string DescribeVersion()
    {
        var stamp = ResolveBuildStamp();
        return stamp is null ? $"{ResolveVersion()}(未注入构建标识)" : $"{ResolveVersion()}+{stamp}";
    }
}
