using System.Net;
using System.Net.Sockets;

namespace ControlServer.Server.Services;

/// <summary>端口可用性预检（M2-E5）。抽成接口是为了让"端口被占用"这条分支能在单元测试里复现。</summary>
public interface IPortProbe
{
    /// <summary>尝试在 <paramref name="host"/>:<paramref name="port"/> 上绑定一次，能绑上就返回 true。</summary>
    bool IsAvailable(string host, int port);
}

/// <summary>
/// 基于 <see cref="TcpListener"/> 的真实探测。
/// <para>
/// <b>为什么需要它</b>：热切换必须先 <c>StopAsync</c> 再 <c>StartAsync</c>，
/// 而承载这次 API 请求的正是即将被停掉的 Kestrel —— 切换动作只能等响应返回之后再做。
/// 若不预检，"新端口被占用"这个最常见的失败就只能异步发现，前端拿到的是"已受理"然后失联，
/// 用户体验是"点了改端口，页面白屏了"。
/// </para>
/// <para>
/// <b>它不保证成功</b>：预检只是"能不能 bind"，与 Kestrel 真正启动还有差异
/// （HTTPS 证书加载、监听选项冲突、TOCTOU 竞态）。因此切换路径上**仍然必须回滚**，
/// 预检只是把绝大多数失败提前到同步阶段、让用户当场看到原因。
/// </para>
/// </summary>
public sealed class TcpPortProbe : IPortProbe
{
    public bool IsAvailable(string host, int port)
    {
        if (port is < 1 or > 65535)
        {
            return false;
        }

        try
        {
            var listener = new TcpListener(ResolveBindAddress(host), port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // 地址形态不合法（如主机名解析不出来）→ 当作不可用，让调用方给出"地址无效"类提示。
            return false;
        }
    }

    /// <summary>
    /// 把监听地址里的 host 段映射成绑定用的 IP。
    /// <para>
    /// Kestrel 的 URI 前缀允许 <c>*</c> / <c>+</c> / <c>0.0.0.0</c> 表示"所有网卡"，
    /// 而这几个字面量都不是合法 IP，不能直接丢给 <see cref="TcpListener"/>。
    /// </para>
    /// </summary>
    private static IPAddress ResolveBindAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host is "*" or "+" or "0.0.0.0")
        {
            return IPAddress.Any;
        }

        if (host is "::" or "[::]")
        {
            return IPAddress.IPv6Any;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        var trimmed = host.Trim('[', ']');

        if (IPAddress.TryParse(trimmed, out var parsed))
        {
            return parsed;
        }

        // 主机名：解析出来就绑那个地址，解析不到退回 Any
        // （退回而不是报错，是为了不在预检阶段引入"DNS 不可用就改不了端口"的新失败模式）。
        try
        {
            var resolved = Dns.GetHostAddresses(trimmed)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

            return resolved ?? IPAddress.Any;
        }
        catch (SocketException)
        {
            return IPAddress.Any;
        }
    }
}
