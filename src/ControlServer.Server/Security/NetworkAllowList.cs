using System.Net;
using System.Net.Sockets;

namespace ControlServer.Server.Security;

/// <summary>
/// 来源网段白名单（<c>Server:Security:AllowedCidrs</c>，<b>M6.2 起真正生效</b>）。
///
/// <para>
/// 🔥 <b>审计发现</b>：这个配置项在 M1 就写进了 <c>SecurityOptions</c>，
/// 注释写着"允许访问的网段（CIDR）。留空 = 不限制"，但<b>全仓没有任何代码读它</b>。
/// 也就是说：运维照文档填了 <c>192.168.1.0/24</c> 以为锁住了边界，
/// 而服务其实对所有能路由到它的地址一视同仁 —— 一个只存在于配置文件里的安全措施，
/// 比没有这条措施更危险（人会因此不再做别的防护）。
/// </para>
///
/// <para>
/// <b>语义</b>：空列表 = 不限制（保持既有部署行为）；非空 = <b>只</b>放行落在任一网段内的来源。
/// </para>
///
/// <para>
/// <b>两条硬规则</b>：
/// </para>
/// <list type="number">
///   <item><b>回环永远放行</b>。托盘、<c>curl 127.0.0.1</c>、探活脚本都走回环；
///         把它们也挡掉，现场会出现"服务起来了，但本机打不开"这种最难自证的状态。</item>
///   <item><b>解析失败 = 拒绝启动</b>（fail-closed）。写错一个字符就静默放行，
///         等于把防火墙配置变成一句许愿。宁可起不来，也不要"看起来起了"。</item>
/// </list>
/// </summary>
public sealed class NetworkAllowList
{
    private readonly (IPAddress Network, int Prefix)[] _rules;

    private NetworkAllowList((IPAddress Network, int Prefix)[] rules) => _rules = rules;

    /// <summary>未配置任何网段 ⇒ 不做限制。</summary>
    public bool IsEmpty => _rules.Length == 0;

    /// <summary>已配置的网段数量（日志用）。</summary>
    public int Count => _rules.Length;

    /// <summary>
    /// 该地址是否允许访问。回环始终允许；<paramref name="address"/> 为 <c>null</c>
    /// （例如单元测试里没有连接信息）时按<b>拒绝</b>处理 —— 判不出来就不放。
    /// </summary>
    public bool Allows(IPAddress? address)
    {
        if (IsEmpty) return true;
        if (address is null) return false;

        var ip = Normalize(address);

        if (IPAddress.IsLoopback(ip)) return true;

        foreach (var (network, prefix) in _rules)
        {
            if (InRange(ip, network, prefix)) return true;
        }

        return false;
    }

    /// <summary>
    /// 从配置节读网段。<b>两种写法都要认</b>：
    /// <list type="bullet">
    ///   <item><c>appsettings.json</c> 里的数组：<c>"AllowedCidrs": [ "192.168.1.0/24" ]</c>；</item>
    ///   <item>环境变量 / 单行字符串：<c>Server__Security__AllowedCidrs=192.168.1.0/24,10.0.0.0/8</c>。</item>
    /// </list>
    /// <para>
    /// 🔥 第二种不是"顺手支持"：环境变量<b>绑不出数组</b>（要写成 <c>__0</c> / <c>__1</c> 这种带下标的形状），
    /// 而现场改配置最常用的就是环境变量与一行逗号分隔。只认数组的话，
    /// 运维写的那一行会被静默当成空 ⇒ 白名单"配了等于没配"——正是这次审计要消灭的那种失效。
    /// </para>
    /// </summary>
    public static NetworkAllowList FromConfiguration(
        Microsoft.Extensions.Configuration.IConfigurationSection? section)
    {
        if (section is null) return new NetworkAllowList(Array.Empty<(IPAddress, int)>());

        var entries = new List<string>();

        // 单行写法（环境变量 / "a,b" / "a;b"）
        var scalar = section.Value;

        if (!string.IsNullOrWhiteSpace(scalar))
        {
            entries.AddRange(scalar.Split(
                new[] { ',', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        // 数组写法（JSON 的 [ ... ] 会落成子节）
        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value)) entries.Add(child.Value.Trim());
        }

        return Parse(entries);
    }

    /// <summary>
    /// 解析配置。<paramref name="source"/> 里任何一条不合法都会抛
    /// <see cref="FormatException"/>（消息里带<b>是哪一条</b>坏 —— 现场就靠这句话定位）。
    /// </summary>
    public static NetworkAllowList Parse(IEnumerable<string>? source)
    {
        if (source is null) return new NetworkAllowList(Array.Empty<(IPAddress, int)>());

        var rules = new List<(IPAddress Network, int Prefix)>();

        foreach (var raw in source)
        {
            var text = raw?.Trim();

            // 空串直接跳过：配置里常见 "a,b," 这种拖尾，为它拒绝启动不值得。
            if (string.IsNullOrEmpty(text)) continue;

            var slash = text.IndexOf('/');

            var addressPart = slash < 0 ? text : text[..slash];
            var prefixPart = slash < 0 ? null : text[(slash + 1)..];

            if (!IPAddress.TryParse(addressPart.Trim(), out var address))
            {
                throw new FormatException($"网段「{text}」的地址部分无法解析（应为 192.168.1.0/24 这种写法）。");
            }

            address = Normalize(address);

            var maxPrefix = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;

            var prefix = maxPrefix;

            if (prefixPart is not null)
            {
                if (!int.TryParse(prefixPart.Trim(), out prefix) || prefix < 0 || prefix > maxPrefix)
                {
                    throw new FormatException(
                        $"网段「{text}」的前缀长度不合法（IPv4 为 0–32，IPv6 为 0–128）。");
                }
            }

            rules.Add((address, prefix));
        }

        return new NetworkAllowList(rules.ToArray());
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把 IPv4-mapped IPv6（<c>::ffff:192.168.1.5</c>）折回 IPv4。
    /// <para>
    /// 🔥 不折会出这种现场：Kestrel 双栈监听时 <c>RemoteIpAddress</c> 报的是
    /// <c>::ffff:192.168.1.5</c>（16 字节），而配置里写的是 <c>192.168.1.0/24</c>（4 字节），
    /// 逐字节比较必然不等 —— 表现为"白名单配对了，但所有人都被拒"。
    /// </para>
    /// </summary>
    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool InRange(IPAddress address, IPAddress network, int prefix)
    {
        var a = address.GetAddressBytes();
        var b = network.GetAddressBytes();

        if (a.Length != b.Length) return false;

        var fullBytes = prefix / 8;
        var remainingBits = prefix % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (a[i] != b[i]) return false;
        }

        if (remainingBits == 0) return true;

        var mask = (byte)(0xFF << (8 - remainingBits));

        return (a[fullBytes] & mask) == (b[fullBytes] & mask);
    }
}
