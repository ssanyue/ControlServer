using System.Net;
using ControlServer.Contracts;
using ControlServer.Core.Services;

namespace ControlServer.Server.Services;

/// <summary>
/// 网络唤醒（Wake-on-LAN，2026-10-15，需求③）。
///
/// <para>
/// <b>它和别的主机操作长得不一样，原因只有一个：目标机器是关着的。</b>
/// 关机 / 重启走 SignalR 指令（要求那台机器连着中心），而这台机器连电都可能没有 ——
/// 所以这里必须由<b>服务端自己</b>朝局域网发一组以太网广播帧（魔术包），
/// 由网卡硬件在"主机关机、只有网卡待机供电"的状态下把它认出来并开机。
/// ⇒ <b>整个过程不经过客户机，也不会有任何应答</b>。返回"成功"只等于"包发出去了"。
/// </para>
///
/// <para>
/// <b>多网卡怎么处理</b>（用户明确要求注意这一点）：
/// 一台机器可能同时插着有线 / 无线 / 虚拟网卡（Hyper-V、VMware、VPN 各造一块），
/// 而<b>只有真正连着那块能被唤醒</b>。服务端没法知道是哪块，所以策略是
/// <b>对它上报过的每一块网卡都发一遍</b> —— 对每块网卡各自组一个魔术包
/// （魔术包的内容就是那块网卡的 MAC 重复 16 次，MAC 不同包就不同）。
/// 代价是几帧几十字节的广播，收益是"不用用户去猜是哪块"。
/// </para>
///
/// <para>
/// <b>发到哪些地址</b>：只发一个地址是不够的 ——
/// </para>
/// <list type="bullet">
///   <item><c>255.255.255.255</c>（本地广播）：最通用，但只在**服务端自己所在的广播域**内有效；</item>
///   <item><b>该机最后来源 IP 的定向广播</b>（把最后一节换成 255，例如
///         <c>192.168.1.37</c> ⇒ <c>192.168.1.255</c>）：能跨到"客户机所在的那个网段"。
///         服务端与客户机同网段时它与上一条重合，跨网段（三层交换机 / VLAN）时它是唯一能到的一条。</item>
/// </list>
/// <para>
/// ⚠️ <b>"最后一节换 255"是对 /24 的假定</b>。我们手里只有客户机的 IPv4 地址，
/// 没有它的掩码（清单里没报）。绝大多数现场就是 /24，而且上一条全局广播本来也覆盖同网段；
/// 真要支持 /16 之类，需要客户机在清单里多报一个掩码字段 —— 那属于另一个改动，
/// 这里不假装支持。
/// </para>
/// </summary>
public sealed class AgentWakeService
{
    /// <summary>
    /// 魔术包的目标端口。
    /// <para>
    /// 9（discard）是通行默认值，也是本项目按键动作里 WoL 的默认端口；
    /// 7（echo）是老工具的习惯。<b>两个都发</b>：每个包只有 102 字节，
    /// 而"到底哪个端口能被那块网卡认出来"是硬件/驱动相关、现场说不准的事。
    /// </para>
    /// </summary>
    private static readonly int[] Ports = { DataEncoder.DefaultWolPort, 7 };

    private readonly NetworkService _network;
    private readonly LogService _log;

    public AgentWakeService(NetworkService network, LogService log)
    {
        _network = network;
        _log = log;
    }

    /// <summary>发唤醒包。</summary>
    /// <param name="agentId">目标客户机 Id（只用于日志）。</param>
    /// <param name="lastAddress">该机最后来源 IP（用来推定向广播地址；null 表示还不知道）。</param>
    /// <param name="interfaces">
    /// 该机上报过的网卡。
    /// <para>
    /// ⚠️ <b>它来自"注册时上报的主机信息"（<c>HostInfoJson</c>），不是 6 小时一次的硬件清单</b>
    /// —— 清单 DTO 里<b>没有</b>网卡这一项（先在清单里找了一圈，确认没有）。
    /// 注册/重新注册都会刷新它，而客户机每次重启都会重新注册，所以 MAC 变化最迟在下一次
    /// Agent 重启后就跟上了。
    /// </para>
    /// </param>
    public async Task<AgentWakeOutcome> WakeAsync(
        int agentId,
        string? lastAddress,
        IReadOnlyList<AgentNetworkInterfaceDto>? interfaces,
        CancellationToken cancellationToken = default)
    {
        if (interfaces is null || interfaces.Count == 0)
        {
            return AgentWakeOutcome.Failed(
                "这台机器还没有上报过网卡（网卡清单随注册一起上报），拿不到 MAC，无法唤醒。"
                + "等它下一次连上中心即可自动补上；也可以让它重装/重启一次客户端。");
        }

        // 只发能解析成 6 字节的那些 MAC；同一块网卡只发一次（清单里理论上不重复，
        // 但虚拟网卡换驱动之类的场景会留下重复项 —— 重复发没有意义）。
        var macs = interfaces
            .Select(iface => (Iface: iface, Bytes: DataEncoder.TryParseMac(iface.Mac)))
            .Where(x => x.Bytes is not null)
            .GroupBy(x => x.Iface.Mac, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (macs.Count == 0)
        {
            var reported = string.Join("、", interfaces.Select(i => $"{i.Name}={i.Mac}"));
            return AgentWakeOutcome.Failed(
                $"这台机器上报的 {interfaces.Count} 块网卡里没有一块有可用的 MAC（{reported}），无法唤醒。");
        }

        var targets = BuildTargets(lastAddress);

        var sent = 0;
        var failures = new List<string>();

        foreach (var (iface, bytes) in macs)
        {
            var packet = DataEncoder.BuildMagicPacket(bytes!);

            foreach (var target in targets)
            {
                foreach (var port in Ports)
                {
                    var result = await _network
                        .SendWakeOnLanAsync(target, port, packet, cancellationToken)
                        .ConfigureAwait(false);

                    if (result.Ok)
                    {
                        sent++;
                    }
                    else
                    {
                        failures.Add($"{target}:{port} ← {iface.Name} {iface.Mac}（{result.Message}）");
                    }
                }
            }
        }

        var macList = string.Join("、", macs.Select(m => $"{m.Iface.Mac}({m.Iface.Name})"));
        var targetList = string.Join("、", targets);

        if (sent == 0)
        {
            _log.Warn("Agent", $"网络唤醒失败（Id={agentId}）：{sent} 个包都没发出去。{string.Join("；", failures)}");

            return AgentWakeOutcome.Failed($"魔术包一个都没发出去：{string.Join("；", failures)}");
        }

        // ⚠️ 措辞里必须带"没有应答"这层意思：WoL 是单向的，
        //    写"已唤醒"会让用户以为机器已经开了。
        var message =
            $"已发出 {sent} 个魔术包（{macs.Count} 块网卡的 MAC × {targets.Length} 个广播地址 × {Ports.Length} 个端口），"
            + $"发往 {targetList}。MAC：{macList}。"
            + "⚠️ 网络唤醒没有应答 —— 这里只说明包已发出，机器是否真的开机要看现场。"
            + "若没起来，请检查那台机器的 BIOS/网卡是否开启了「网络唤醒」，以及它是否还有待机供电。";

        _log.Info("Agent", $"已发送网络唤醒魔术包（Id={agentId}）：{sent} 个包，MAC {macList} → {targetList}");

        return AgentWakeOutcome.Success(message, macs.Count, sent);
    }

    /// <summary>
    /// 组装目标地址：全局广播 + 该机所在网段的定向广播（对 /24 的假定，见类注释）。
    /// <para>去重：同网段时两者不会重合（<c>255.255.255.255</c> ≠ <c>192.168.1.255</c>），
    /// 但"服务端与客户机都在 255.255.255.0 之外的古怪配置"下可能重合，去一下更稳。</para>
    /// </summary>
    private static string[] BuildTargets(string? lastAddress)
    {
        var targets = new List<string> { "255.255.255.255" };

        var directed = DirectedBroadcast(lastAddress);
        if (directed is not null && !targets.Contains(directed, StringComparer.Ordinal)) targets.Add(directed);

        return targets.ToArray();
    }

    /// <summary>把 <c>a.b.c.d</c> 变成 <c>a.b.c.255</c>；不是 IPv4 就返回 null。</summary>
    private static string? DirectedBroadcast(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;

        // 客户机地址可能带端口（形如 10.0.0.5:52344）—— Kestrel 给的是纯地址，
        // 但历史数据 / 手工填的地址不保证，先切一刀更稳。
        var raw = address.Split(':')[0].Trim();

        if (!IPAddress.TryParse(raw, out var ip)
            || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return null;
        }

        var bytes = ip.GetAddressBytes();
        bytes[3] = 255;

        return new IPAddress(bytes).ToString();
    }
}

/// <summary>一次网络唤醒的结果。</summary>
/// <param name="Ok">魔术包是否至少发出去了一个。</param>
/// <param name="Message">给用户看的一句话（成功时也**必须**说明"无应答"）。</param>
/// <param name="NicCount">用到了几块网卡的 MAC。</param>
/// <param name="SentCount">实际发出的包数。</param>
public sealed record AgentWakeOutcome(bool Ok, string Message, int NicCount = 0, int SentCount = 0)
{
    public static AgentWakeOutcome Success(string message, int nicCount, int sentCount)
        => new(true, message, nicCount, sentCount);

    public static AgentWakeOutcome Failed(string message) => new(false, message);
}
