namespace ControlServer.Platform.Abstractions;

/// <summary>
/// 防火墙规则管理。【M3 实现】Windows: netsh / INetFwPolicy2；Linux: ufw / firewalld。
/// </summary>
public interface IFirewallRuleManager
{
    /// <summary>放行指定 TCP 端口。</summary>
    Task<bool> AllowTcpPortAsync(int port, string ruleName, CancellationToken cancellationToken = default);

    /// <summary>放行指定 UDP 端口。</summary>
    Task<bool> AllowUdpPortAsync(int port, string ruleName, CancellationToken cancellationToken = default);
}

/// <summary>
/// 开机自启注册。【M1 由服务安装脚本处理，M3 抽象成 Provider】
/// Windows: 服务自动启动；Linux: systemctl enable。
/// </summary>
public interface IAutoStartRegistrar
{
    Task<bool> EnableAsync(string unitName, CancellationToken cancellationToken = default);

    Task<bool> DisableAsync(string unitName, CancellationToken cancellationToken = default);
}

/// <summary>
/// 敏感数据保护（授权文件、连接串等）。
/// Windows: DPAPI；Linux: 文件权限 + AES。
/// </summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    string Unprotect(string protectedText);
}

/// <summary>
/// 主机的"硬件摘要"（M4.1）。完整硬件清单（内存条 / 磁盘 / 显卡 / 显示器）在 M4.2 补齐。
/// <para>
/// <b>为什么不是一个静态工具类</b>：CPU 型号与物理内存既没有跨平台的 BCL API，
/// 在 Windows 上又要走 WMI（可能失败、可能被杀软拦）。做成端口之后：
/// 拿得到就用真实值，拿不到就退回 <see cref="BasicHostHardwareInfo"/> 的估算值，
/// 而上层代码（上报逻辑）完全不必写 <c>if (Windows)</c>，也不必处理异常。
/// </para>
/// <para>
/// <b>⚠️ 永远不要用 0 表示"不知道"</b>：界面上"内存 0 MB"会被当成"这台机器有问题"，
/// 而实情只是"没查出来"。这里用 <c>null</c> 表示未知，界面显示"—"。
/// </para>
/// </summary>
public interface IHostHardwareInfo
{
    /// <summary>CPU 型号（取不到返回 null）。</summary>
    string? CpuName { get; }

    /// <summary>物理内存总量（MB；取不到返回 null）。</summary>
    long? TotalMemoryMb { get; }
}

/// <summary>
/// 平台无关的兜底实现：只用 BCL 能拿到的信息。
/// <para>
/// <see cref="TotalMemoryMb"/> 用 <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c>
/// —— 它是**容器/进程可见的内存上限**，在有 cgroup 限制的机器上会小于物理内存。
/// 因此它只是"比 null 强一点"的估算，真实值由平台的实现给（Windows 走 WMI）。
/// </para>
/// </summary>
public sealed class BasicHostHardwareInfo : IHostHardwareInfo
{
    public string? CpuName => Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");

    public long? TotalMemoryMb
    {
        get
        {
            try
            {
                var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                return bytes > 0 ? bytes / 1024 / 1024 : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
