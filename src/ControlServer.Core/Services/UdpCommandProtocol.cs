using System.Net;
using ControlServer.Contracts;

namespace ControlServer.Core.Services;

/// <summary>
/// 一条从 UDP 数据报里解析出来的指令。
/// </summary>
/// <param name="Name">规范指令名（<see cref="AgentCommandNames.Power"/> / <see cref="AgentCommandNames.Volume"/>）。</param>
/// <param name="Argument">指令参数（<c>shutdown</c> / <c>up</c> …）。</param>
/// <param name="Token">
/// 报文里那一段原文（<c>DOWN</c> / <c>+</c> …）。
/// <para>
/// 显式交出来（而不是让调用方自己再切一遍报文）是刻意的：切分规则是"线路格式"的一部分，
/// 只能有一处实现 —— 两处实现的典型后果是"改了一处、另一处按老格式取到了错的那一段"。
/// 日志里用它回显"到底收到了什么"，排障时这一句往往就是全部线索。
/// </para>
/// </param>
public sealed record UdpIncomingCommand(string Name, string? Argument, string Token);

/// <summary>
/// 客户机 UDP 指令通道的<b>线路协议</b>（M4.5；2026-10-13 改为纯明文）。
///
/// <para><b>线路格式就是一行纯文本</b>，没有签名、没有时间戳、没有 nonce：</para>
/// <code>
/// DOWN      → 关机
/// RESTART   → 重启（整机）
/// +  / -    → 音量加 / 音量减
/// 0  / 1    → 静音 / 取消静音
/// </code>
///
/// <para>
/// 🔥 <b>为什么把 HMAC 验签整条拆掉</b>（这是本项目唯一一处<b>主动降低</b>安全强度的改动，
/// 必须写清楚）：原设计是 <c>CS1|指令|时间戳|nonce|HMAC-SHA256</c> 五段报文，
/// 密钥由 <c>agentKey</c> 经 HKDF 派生。它能挡住伪造，代价是<b>发指令必须有密钥</b> ——
/// 于是"用 nc / 现成的中控面板 / 任意第三方程序发一个 DOWN"永远不生效，
/// 现场的表现就是"指令无效果"，而根本原因（段数不对）在发送端完全看不见。
/// </para>
/// <para>
/// 产品决定改为明文 + <b>来源 IP 黑名单</b>：谁都能发，只有被点名的主机发不进来。
/// 这是一条<b>明确的取舍</b>：安全边界从"密码学"退回到"网络可达性"，
/// 因此 <b>UDP 指令通道端口绝不能暴露到不可信网络</b>（与中心 HTTP 端口同级别的要求）。
/// </para>
///
/// <para><b>同时也接受本项目的 <c>name:argument</c> 写法</b>（<c>power:shutdown</c>、<c>volume:up</c>）——
/// 中心网页上的「远程指令」按钮与既有配置文件都用这套 token。两种写法走<b>同一个</b>解析器，
/// 保证"端点放行的"与"客户机认的"永远一致。</para>
///
/// <para>
/// <b>弃用的东西（不要再引入）</b>：报文的签名 / 时间窗 / nonce 防重放 / 通道密钥上报。
/// 明文报文里没有 nonce，"同一份报文再发一次"就是一个<b>合法的新指令</b> ——
/// 音量连按三次本来就该 +3，防重放在这里是反需求。所以重放表也一并删掉了。
/// </para>
/// </summary>
public static class UdpCommandProtocol
{
    /// <summary>
    /// 报文长度上限（字符）。一条指令最长也就 <c>power:shutdown</c> 这种十几字符 ——
    /// 留 64 是给"带尾随空白 / 换了行尾"这类手工操作的余量。
    /// <para>
    /// <b>为什么要有上限</b>：UDP 数据报最大 64 KB，一个灌包的脚本可以每秒送进来几万条
    /// 几十 KB 的垃圾。没有上限的话，"日志里那条丢弃原因"会被一坨乱码塞满。
    /// </para>
    /// </summary>
    public const int MaxTokenLength = 64;

    /// <summary>
    /// 解析一条报文。
    /// </summary>
    /// <param name="message">收到的原始报文（UTF-8 解出来的文本）。</param>
    /// <param name="command">成功时给出解析结果；失败时为 null。</param>
    /// <param name="error">失败原因（成功时为 null）。<b>可以记日志，但不要回给发送方</b>。</param>
    public static bool TryParse(string? message, out UdpIncomingCommand? command, out string? error)
    {
        command = null;
        error = null;

        if (string.IsNullOrWhiteSpace(message))
        {
            error = "报文为空。";
            return false;
        }

        // 去掉首尾空白与可能的 BOM —— 现场常用 `echo DOWN | nc` 之类的管道发指令，
        // 那会在末尾带上一个换行。因为一个换行就把指令丢掉，是最没道理的一种"无效果"。
        var token = message.Trim().TrimStart('\uFEFF').Trim();

        if (token.Length == 0)
        {
            error = "报文全是空白。";
            return false;
        }

        if (token.Length > MaxTokenLength)
        {
            error = $"报文过长（{token.Length} 字符，上限 {MaxTokenLength}）—— 已按可疑流量丢弃。";
            return false;
        }

        if (!TryResolveCommand(token, out var name, out var argument))
        {
            error = $"不认识的指令「{token}」。";
            return false;
        }

        command = new UdpIncomingCommand(name, argument, token);
        return true;
    }

    /// <summary>
    /// 把报文里的指令 token 解析成规范指令。
    /// <para><b>同时接受两套写法</b>：</para>
    /// <list type="bullet">
    ///   <item><b>明文短码</b>（现场手发 / 第三方中控面板）：<c>DOWN</c> / <c>RESTART</c> /
    ///         <c>+</c> / <c>-</c> / <c>0</c> / <c>1</c>；</item>
    ///   <item><b>本项目写法</b>（中心网页与配置文件）：<c>power:shutdown</c>、<c>volume:up</c>。</item>
    /// </list>
    /// </summary>
    public static bool TryResolveCommand(string? token, out string name, out string? argument)
    {
        name = string.Empty;
        argument = null;

        if (string.IsNullOrWhiteSpace(token)) return false;

        var text = token.Trim();

        // ⚠️ 这里的指令名走 AgentCommandNames 常量，而不是字面量 ——
        //    两处各写一遍字面量时，"改了一处"不会有任何编译错误。
        // ⚠️ 大小写不敏感（现场手打 `down` 是常态）。
        switch (text.ToUpperInvariant())
        {
            case "DOWN":
                name = AgentCommandNames.Power;
                argument = AgentPowerActions.Shutdown;
                return true;

            case "RESTART":
            case "REBOOT":   // 兼容旧写法：历史文档与老发送脚本里全是 REBOOT。
                name = AgentCommandNames.Power;
                argument = AgentPowerActions.Restart;
                return true;

            case "+":
                name = AgentCommandNames.Volume;
                argument = AgentVolumeActions.Up;
                return true;

            case "-":
                name = AgentCommandNames.Volume;
                argument = AgentVolumeActions.Down;
                return true;

            case "0":
                name = AgentCommandNames.Volume;
                argument = AgentVolumeActions.Mute;
                return true;

            case "1":
                name = AgentCommandNames.Volume;
                argument = AgentVolumeActions.Unmute;
                return true;
        }

        var colon = text.IndexOf(':');

        if (colon <= 0 || colon == text.Length - 1) return false;

        var head = text[..colon].Trim().ToLowerInvariant();
        var tail = text[(colon + 1)..].Trim().ToLowerInvariant();

        if (head is not (AgentCommandNames.Power or AgentCommandNames.Volume)) return false;

        // 参数也要在合法集合内 —— 否则 `power:xxx` 会被放行到执行侧才被拒，
        // 表现为"端点说发出去了、机器上什么都没发生"。
        if (head == AgentCommandNames.Power && !AgentPowerActions.IsValid(tail)) return false;
        if (head == AgentCommandNames.Volume && !AgentVolumeActions.IsValid(tail)) return false;

        name = head;
        argument = tail;
        return true;
    }

    /// <summary>
    /// 把一条<b>规范指令</b>（<c>power</c> + <c>shutdown</c>…）写回<b>线路 token</b>（2026-10-15）。
    /// <para>
    /// 🔥 <b>为什么需要它</b>：中心侧的"关机 / 重启 / 音量"按钮原先<b>只有 SignalR 一条路</b>
    /// （要求那台机器挂着长连接）。产品口径是"客户机离线时也要把指令发出去"，
    /// 于是这些按钮需要能改走 UDP 指令通道 —— 而 UDP 线路上跑的是 token，
    /// 不是指令名。反向映射留在这里、与 <see cref="TryResolveCommand"/> 成对，
    /// 保证"这里写出去的"必然"客户机认得出"。
    /// </para>
    /// <para>
    /// ⚠️ <b>它只做映射，不做别的</b>：调用方若拿它去给"配置里的动作"编码，
    /// 那是另一种需求（编码类型见 <c>DataEncoder</c>）。这里刻意只认本协议支持的那六个 token。
    /// </para>
    /// </summary>
    /// <param name="name">规范指令名（<see cref="AgentCommandNames.Power"/> / <see cref="AgentCommandNames.Volume"/>）。</param>
    /// <param name="argument">指令参数；缺参或参数不合法时为 null 并返回 false。</param>
    /// <param name="token">成功时的线路 token（<c>DOWN</c> / <c>RESTART</c> / <c>+</c> / <c>-</c> / <c>0</c> / <c>1</c>）。</param>
    public static bool TryFormat(string? name, string? argument, out string token)
    {
        token = string.Empty;

        if (string.IsNullOrWhiteSpace(name)) return false;

        var head = name.Trim();
        var tail = argument?.Trim().ToLowerInvariant() ?? string.Empty;

        if (head == AgentCommandNames.Power)
        {
            token = tail switch
            {
                AgentPowerActions.Shutdown => "DOWN",
                AgentPowerActions.Restart => "RESTART",
                _ => string.Empty,
            };

            return token.Length > 0;
        }

        if (head == AgentCommandNames.Volume)
        {
            token = tail switch
            {
                AgentVolumeActions.Up => "+",
                AgentVolumeActions.Down => "-",
                AgentVolumeActions.Mute => "0",
                AgentVolumeActions.Unmute => "1",
                _ => string.Empty,
            };

            return token.Length > 0;
        }

        return false;
    }

    /// <summary>
    /// 来源 IP 是否<b>被拉黑</b>（在名单里 = 这条报文不处理）。
    /// <para><b>名单为空 = 谁都不拦</b>（这是默认，也是"任意来源皆可"的实现）。</para>
    /// <para>
    /// ⚠️ IPv4-mapped IPv6（<c>::ffff:192.168.1.5</c>）要先去映射再比 ——
    /// 双栈 socket 上收到 IPv4 报文时，<c>RemoteEndPoint</c> 常常就是这个形态，
    /// 不归一化会让"明明拉黑了 192.168.1.5 却拦不住"这种问题查半天。
    /// </para>
    /// <para>
    /// ⚠️ <b>源地址解析失败时按"拦下"处理（fail-closed）</b>：这条路本身就是
    /// "我们无法证明它不在黑名单里"。TCP 之外没有握手，而这里唯一能拿到的身份就是源地址 ——
    /// 解析不出来的地址意味着我们连判据都没有，此时放行等于把黑名单变成装饰。
    /// </para>
    /// </summary>
    public static bool IsSourceBlocked(IReadOnlyList<string>? blocked, string? remoteIp)
    {
        if (blocked is null || blocked.Count == 0) return false;

        if (!TryParseIp(remoteIp, out var remote)) return true;

        foreach (var entry in blocked)
        {
            if (TryParseIp(entry, out var candidate) && candidate.Equals(remote)) return true;
        }

        return false;
    }

    private static bool TryParseIp(string? text, out IPAddress address)
    {
        address = IPAddress.None;

        if (string.IsNullOrWhiteSpace(text)) return false;

        if (!IPAddress.TryParse(text.Trim(), out var parsed)) return false;

        // 归一化到同一个"族"，否则 ::ffff:1.2.3.4 与 1.2.3.4 会被判为不同地址。
        address = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
        return true;
    }
}
