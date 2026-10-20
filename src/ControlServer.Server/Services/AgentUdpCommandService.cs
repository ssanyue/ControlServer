using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlServer.Core.Services;

namespace ControlServer.Server.Services;

/// <summary>发一条 UDP 指令的结果。</summary>
/// <param name="Ok">是否已发出（<b>不是</b>"对方已执行"—— UDP 没有应答）。</param>
/// <param name="Status">机器可读状态：<c>ok</c> / <c>noaddress</c> / <c>badport</c> / <c>error</c>。</param>
/// <param name="Message">人可读说明（会原样显示在页面上）。</param>
public sealed record AgentUdpSendOutcome(bool Ok, string Status, string Message);

/// <summary>
/// 服务端侧的 <b>UDP 指令代发</b>（M4.6）。
///
/// <para>
/// <b>为什么需要它</b>：UDP 指令通道是客户机唯一开着的入站端口，而网页上要有一个
/// "给这台机器关机"的按钮 —— 由中心代发比让人自己去装一个 UDP 调试工具现实得多。
/// </para>
///
/// <para>
/// 🔥 <b>2026-10-13 起报文是纯明文</b>（<c>DOWN</c> / <c>RESTART</c> / <c>+</c> / <c>-</c> / <c>0</c> / <c>1</c>）：
/// 原来那套"密钥由客户机上报、服务端拿它签名"的机制<b>整体删除</b>了 ——
/// 既然明文就能执行，密钥再传一遍只是徒增一条要维护的链路。
/// 于是这里<b>不需要记住任何每台机器的状态</b>，服务端重启也不会丢能力。
/// </para>
///
/// <para>
/// ⚠️ <b>"已发出"不等于"已执行"</b>：UDP 无应答，这里只能保证<b>报文已经发到网线上</b>。
/// 对方是否收到、是否真的关机，只能去那台机器的日志里看（<c>Udp</c> 分类：
/// <c>收到指令</c> / <c>丢弃来自 … 的报文</c>）。这条语义必须在页面上讲清，
/// 否则"点了没反应"又要查半天网络。
/// </para>
/// </summary>
public sealed class AgentUdpCommandService
{
    private readonly LogService _log;

    public AgentUdpCommandService(LogService log) => _log = log;

    /// <summary>
    /// 向某台机器发一条<b>明文</b> UDP 指令。
    /// </summary>
    /// <param name="agentId">客户端 Id（只用于日志与页面回显）。</param>
    /// <param name="address">客户机地址（一般取在线表里的 <c>LastAddress</c>）。</param>
    /// <param name="port">客户机的 UDP 通道端口（来自它当前的配置）。</param>
    /// <param name="command">
    /// 线路 token。取值见 <see cref="UdpCommandProtocol.TryResolveCommand"/>：
    /// 明文短码（<c>DOWN</c> / <c>+</c> …）或本项目的 <c>name:argument</c>（<c>power:shutdown</c> …）。
    /// 两种都由同一个解析器校验 —— 保证"这里放行的"与"客户机能认的"永远一致。
    /// </param>
    public async Task<AgentUdpSendOutcome> SendAsync(
        int agentId,
        string? address,
        int port,
        string command,
        CancellationToken cancellationToken)
    {
        // ⚠️ 先做解析校验，**再**发：一条客户机必然不认的字符串不该被发到网上 ——
        //    那会变成"页面说发出去了、机器上什么都没有"，是本题里最难查的一种形态。
        if (!UdpCommandProtocol.TryResolveCommand(command, out _, out _))
        {
            return new AgentUdpSendOutcome(
                false,
                "badcommand",
                $"「{command}」不是本系统认识的指令（可用：DOWN / RESTART / + / - / 0 / 1）。");
        }

        if (string.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address.Trim(), out var ip))
        {
            return new AgentUdpSendOutcome(
                false,
                "noaddress",
                $"还不知道该机的网络地址（当前「{address ?? "(空)"}」），无法发 UDP —— 等它上报一次心跳即可。");
        }

        if (port is < 1 or > 65535)
        {
            return new AgentUdpSendOutcome(false, "badport", $"该机配置里的 UDP 通道端口不合法（{port}）。");
        }

        try
        {
            using var udp = new UdpClient(ip.AddressFamily);

            udp.Connect(ip, port);

            var bytes = Encoding.UTF8.GetBytes(command.Trim());

            // ⚠️ 用 ReadOnlyMemory 那个重载：`SendAsync(byte[], int, CancellationToken)` 不存在，
            //    三参重载的第三个参数是 `IPEndPoint?` —— 传 ct 会编译失败（已踩）。
            await udp.SendAsync(new ReadOnlyMemory<byte>(bytes), cancellationToken).ConfigureAwait(false);

            _log.Info("Udp", $"已代发 UDP 指令「{command}」→ {address}:{port}（客户端 {agentId}，无应答）。");

            return new AgentUdpSendOutcome(
                true,
                "ok",
                $"已向 {address}:{port} 发出 UDP 指令「{command}」。UDP 无应答 —— 是否真的执行请看那台机器的日志。");
        }
        catch (Exception ex)
        {
            // 目标不可达 / 权限 / 地址族不匹配：只记一行并如实回报，别笼统说"失败了"。
            _log.Error("Udp", $"代发 UDP 指令失败（客户端 {agentId} → {address}:{port}）", ex);

            return new AgentUdpSendOutcome(false, "error", $"发送失败：{ex.Message}");
        }
    }
}
