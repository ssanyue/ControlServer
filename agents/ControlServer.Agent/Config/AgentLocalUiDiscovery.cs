using System.Text;
using ControlServer.Core.Services;

namespace ControlServer.Agent.Config;

/// <summary>
/// 本机配置页面的<b>发现文件</b>（<c>local-ui.json</c>，2026-10-14）。
/// <para>
/// 绑定端口的那个进程（客户端服务）把自己在哪写下来，托盘的「打开本机配置」照着打开。
/// 为什么不各算各的：托盘只有 <c>--state-dir</c> 与 <c>--service-name</c> 两个参数，
/// 它不读 <c>appsettings.json</c>，端口一旦被改成非默认值就必然猜错。
/// </para>
/// </summary>
public sealed record AgentLocalUiDiscovery(string Url);

/// <summary>发现文件的读写。<b>任何异常都不抛</b> —— 它只是"一个方便"，坏了不该影响任何一方。</summary>
public static class AgentLocalUiDiscoveryFile
{
    /// <summary>把"本机配置页在哪"写下来。失败静默。</summary>
    public static void Write(string stateDirectory, string url)
    {
        try
        {
            Directory.CreateDirectory(stateDirectory);

            // 用 ControlJson（camelCase）而不是裸 JsonSerializer：这个文件是"给人看的排障材料"，
            // 与同目录下的 local-config.json / status.json 保持同一种写法。
            File.WriteAllText(
                AgentPaths.LocalUiDiscoveryFile(stateDirectory),
                ControlJson.Serialize(new AgentLocalUiDiscovery(url)),
                Encoding.UTF8);
        }
        catch
        {
            // 忽略：托盘的「打开本机配置」还有"默认端口"这条兜底。
        }
    }

    /// <summary>读出 URL；没有 / 读坏一律返回 null。</summary>
    public static string? Read(string stateDirectory)
    {
        try
        {
            var path = AgentPaths.LocalUiDiscoveryFile(stateDirectory);

            if (!File.Exists(path)) return null;

            var value = ControlJson.Deserialize<AgentLocalUiDiscovery>(File.ReadAllText(path, Encoding.UTF8));

            return string.IsNullOrWhiteSpace(value?.Url) ? null : value.Url;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清掉（页面停了就没人监听了，留着会让托盘打开一个死地址）。失败静默。</summary>
    public static void Clear(string stateDirectory)
    {
        try
        {
            var path = AgentPaths.LocalUiDiscoveryFile(stateDirectory);

            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略。
        }
    }
}
