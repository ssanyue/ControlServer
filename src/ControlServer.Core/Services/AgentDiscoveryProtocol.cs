using System.Globalization;
using ControlServer.Contracts;

namespace ControlServer.Core.Services;

/// <summary>
/// 客户端 Agent 发现报文的协议常量与解析（M4.1）。
/// <para>
/// <b>为什么把协议放在 Core</b>：解析逻辑是纯函数（无 IO、无平台依赖），
/// 放 Core 就能被单测直接钉住 —— 而"客户端广播了、服务端没反应"这类故障，
/// 九成是报文格式或匹配关键字差了一个字符，且现场没有任何日志能看出来。
/// </para>
/// <para>
/// <b>为什么是空格分隔的纯文本而不是 JSON</b>：UDP 广播是<b>无连接、不可靠</b>的，
/// 报文必须极短（一个数据报装得下，不触发分片）且解析绝不抛异常。
/// 文本形式排障时能直接看（Wireshark / 抓包工具里一眼可读），
/// 而 JSON 解析失败时给出的异常栈对现场毫无帮助。
/// </para>
/// </summary>
public static class AgentProtocol
{
    /// <summary>产品标识（应答报文里回传，客户端据此确认"这是我要找的中心服务"）。</summary>
    public const string Product = "ControlServer";

    /// <summary>发现端口默认值。<b>刻意避开对码的 9999</b> —— 两者语义不同，不该抢同一个端口。</summary>
    public const int DefaultDiscoveryPort = 9998;

    /// <summary>客户端广播"中心服务在哪"的关键字。</summary>
    public const string DiscoverKeyword = "CS-DISCOVER";

    /// <summary>服务端应答"我在这里"的关键字。</summary>
    public const string HereKeyword = "CS-HERE";

    /// <summary>
    /// 单个数据报的最大可接受长度（字节）。
    /// <para>
    /// 限长是为了不被"超大 UDP 包"拖住：广播端口是对整个局域网开放的，
    /// 任何主机都能往这里灌数据。<b>超过这个长度的一律丢弃</b>，连解析都不做。
    /// </para>
    /// </summary>
    public const int MaxDatagramBytes = 512;
}

/// <summary>客户端广播的内容（<c>CS-DISCOVER &lt;machineKey&gt; &lt;agentVersion&gt;</c>）。</summary>
/// <param name="MachineKey">发起广播的机器稳定标识。</param>
/// <param name="AgentVersion">发起方的 Agent 版本（服务端回包头里带上自己的版本，双方可各自比对）。</param>
public sealed record AgentDiscoverMessage(string MachineKey, string AgentVersion);

/// <summary>
/// 服务端应答的内容（<c>CS-HERE &lt;product&gt; &lt;version&gt; &lt;httpPort&gt; &lt;pairingPort&gt; &lt;discoveryPort&gt; &lt;serverId&gt;</c>）。
/// <para>
/// <b>只含定位信息，不含任何凭据。</b> 广播域内的任何主机都能看到这个应答 ——
/// 这是刻意的：应答的作用是"告诉客户端往哪连"，真正需要保密的注册凭据只走 HTTPS。
/// </para>
/// </summary>
/// <param name="HttpPort">中心服务端的 HTTP 端口（客户端据此拼出注册地址）。</param>
/// <param name="ServerId">服务端实例标识（机器码派生）。多台中心服务同网段时用于区分。</param>
public sealed record AgentHereMessage(
    string Product,
    string Version,
    int HttpPort,
    int PairingPort,
    int DiscoveryPort,
    string ServerId);

/// <summary>
/// 发现报文的构造与解析。
/// <para>
/// <b>解析一律"宽容但不猜"</b>：字段数不对、端口不是数字、关键字不匹配 —— 一律返回 false
/// 并给出可读原因，绝不抛异常（它跑在 UDP 收包循环里，抛出去会打断整个监听）。
/// </para>
/// </summary>
public static class AgentDiscoveryProtocol
{
    /// <summary>构造客户端的发现广播。</summary>
    public static string BuildDiscover(string machineKey, string agentVersion)
        => $"{AgentProtocol.DiscoverKeyword} {Sanitize(machineKey)} {Sanitize(agentVersion)}";

    /// <summary>构造服务端的应答。</summary>
    public static string BuildHere(AgentHereMessage message)
        => string.Join(
            ' ',
            AgentProtocol.HereKeyword,
            Sanitize(message.Product),
            Sanitize(message.Version),
            message.HttpPort.ToString(CultureInfo.InvariantCulture),
            message.PairingPort.ToString(CultureInfo.InvariantCulture),
            message.DiscoveryPort.ToString(CultureInfo.InvariantCulture),
            Sanitize(message.ServerId));

    /// <summary>解析发现广播。</summary>
    public static bool TryParseDiscover(string? text, out AgentDiscoverMessage message, out string? error)
    {
        message = null!;
        error = null;

        if (!TrySplit(text, out var parts, out error)) return false;

        if (parts.Length != 3)
        {
            error = $"字段数应为 3，实际 {parts.Length}";
            return false;
        }

        if (!IsKeyword(parts[0], AgentProtocol.DiscoverKeyword))
        {
            error = $"首字段不是 {AgentProtocol.DiscoverKeyword}";
            return false;
        }

        if (!IsSafeToken(parts[1]))
        {
            error = "机器标识非法（空或含控制字符）";
            return false;
        }

        message = new AgentDiscoverMessage(parts[1], NormalizeVersion(parts[2]));
        return true;
    }

    /// <summary>解析服务端应答。</summary>
    public static bool TryParseHere(string? text, out AgentHereMessage message, out string? error)
    {
        message = null!;
        error = null;

        if (!TrySplit(text, out var parts, out error)) return false;

        if (parts.Length != 7)
        {
            error = $"字段数应为 7，实际 {parts.Length}";
            return false;
        }

        if (!IsKeyword(parts[0], AgentProtocol.HereKeyword))
        {
            error = $"首字段不是 {AgentProtocol.HereKeyword}";
            return false;
        }

        if (!IsSafeToken(parts[1]))
        {
            error = "产品标识非法";
            return false;
        }

        if (!TryPort(parts[3], out var httpPort) || !TryPort(parts[4], out var pairingPort) || !TryPort(parts[5], out var discoveryPort))
        {
            error = "端口不是 1..65535 的整数";
            return false;
        }

        message = new AgentHereMessage(
            parts[1],
            NormalizeVersion(parts[2]),
            httpPort,
            pairingPort,
            discoveryPort,
            parts[6]);

        return true;
    }

    // ── 私有 ──

    /// <summary>
    /// 切分 + 长度闸门。返回的数组<b>至少一个元素</b>（下标的越界检查交给各调用方按字段数判定）。
    /// </summary>
    private static bool TrySplit(string? text, out string[] parts, out string? error)
    {
        parts = Array.Empty<string>();
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "空报文";
            return false;
        }

        // 按 UTF-8 计长：字符串长度不等价于字节数，而限长的目的是"别被超大包拖住"。
        if (System.Text.Encoding.UTF8.GetByteCount(text) > AgentProtocol.MaxDatagramBytes)
        {
            error = $"报文超过 {AgentProtocol.MaxDatagramBytes} 字节";
            return false;
        }

        // 去掉行尾（UDP 报文里带 \r\n 是常见误操作，不该因此整包作废）。
        parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            error = "空报文";
            return false;
        }

        return true;
    }

    private static bool IsKeyword(string actual, string expected)
        => actual.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool TryPort(string text, out int port)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    /// <summary>
    /// 字段是否"看起来像那么回事"：非空、无空格（切分已保证）、无控制字符、长度合理。
    /// <para>
    /// 这道检查不是为了防攻击（应答本身无凭据），而是为了<b>排障</b> ——
    /// 一条被截断或粘了乱码的报文如果被当成合法，客户端会去连一个不存在的地址，
    /// 表现为"发现成功了但连不上"，比直接拒绝难查得多。
    /// </para>
    /// </summary>
    private static bool IsSafeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;

        foreach (var ch in value)
        {
            if (char.IsControl(ch)) return false;
        }

        return true;
    }

    private static string Sanitize(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim().Replace(' ', '_');

    private static string NormalizeVersion(string value)
        => IsSafeToken(value) ? value : "-";
}
