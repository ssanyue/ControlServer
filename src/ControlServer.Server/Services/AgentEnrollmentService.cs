using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using ControlServer.Server.Options;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.Services;

/// <summary>
/// 客户端准入与注册（M4.1）。
/// <para>
/// <b>免凭据准入（2026-10-13 起）</b>：注册<b>不再校验任何令牌或加入码</b> ——
/// 客户机发现到中心服务端之后直接登记、直接拿到 agentKey。
/// 产品口径是"只要装了客户端，局域网内都能自动发现，不设任何门槛"。
/// </para>
/// <para>
/// ⚠️ <b>这意味着同网段任意能访问到 <c>/api/agents/register</c> 的机器都能冒充一台客户机。</b>
/// 原设计（M4.1）刻意维护了「安装包令牌 / 一次性加入码」两道凭据，并在注释里写明
/// 「绝不提供只校验来源网段的免凭据模式」—— 该结论已被本次产品决策<b>显式推翻</b>，
/// 相关代码（令牌表读写、令牌端点、加入码生成、安装包烘焙令牌）一并移除。
/// 现在的兜底只剩两条，都必须知道：
/// <list type="number">
///   <item>在线表容量上限（<see cref="AgentOptions.MaxHosts"/>）—— 防撑爆，不是准入门槛；</item>
///   <item>管理员在网页上的「拉黑 / 移除」—— 事后处置，不是事前拦截。</item>
/// </list>
/// 因此<b>中心服务的 HTTP 端口绝不该暴露到不可信网络</b>：一旦被端口映射 / 公网可达，
/// 等于对外网开放注册。
/// </para>
/// <para>
/// <b>register 仍是匿名端点</b>（客户端此刻还没有身份）。它自己把住的只剩：
/// 机器标识是否齐全、在线表是否已满、写入是否成功 —— 且失败一律给出<b>可读原因</b>
/// （现场排障时这一句话就是全部线索）。
/// </para>
/// </summary>
public sealed class AgentEnrollmentService
{
    private readonly IAgentHostStore _hosts;
    private readonly AgentHostRegistry _registry;
    private readonly INotifier _notifier;
    private readonly LogService _log;
    private readonly IOptions<ServerOptions> _options;

    /// <summary>
    /// 已经记过 Warn 的「来源 + 机器码 + 失败状态」组合（见 <see cref="MarkRejectionReported"/>）。
    /// <b>只在进程内、重启即清</b>：它的唯一用途是压住重复日志，不是审计。
    /// </summary>
    private readonly HashSet<string> _reportedRejections = new(StringComparer.Ordinal);

    public AgentEnrollmentService(
        IAgentHostStore hosts,
        AgentHostRegistry registry,
        INotifier notifier,
        LogService log,
        IOptions<ServerOptions> options)
    {
        _hosts = hosts;
        _registry = registry;
        _notifier = notifier;
        _log = log;
        _options = options;
    }

    // ═══════════════════════════════════════════════════════════
    // 注册（客户端调用，匿名）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 处理一次客户端注册。<b>不需要任何凭据。</b>
    /// </summary>
    /// <param name="request">注册请求（含主机信息）。</param>
    /// <param name="remoteAddress">请求来源地址（记进在线表，排障时"它从哪个 IP 来"很关键）。</param>
    public async Task<AgentRegisterResult> RegisterAsync(
        AgentRegisterRequest request,
        string? remoteAddress,
        CancellationToken cancellationToken = default)
    {
        var info = request.HostInfo;

        if (info is null || string.IsNullOrWhiteSpace(info.MachineKey))
        {
            return Fail(
                "invalid",
                "缺少主机信息或机器标识（machineKey），无法登记。",
                remoteAddress,
                info?.MachineKey);
        }

        var machineKey = info.MachineKey.Trim();

        var now = BeijingTime.Now;
        var options = _options.Value.Agent;

        var existing = await _hosts.FindByMachineKeyAsync(machineKey, cancellationToken).ConfigureAwait(false);

        // ── 🔥 拉黑：连**注册**都拒绝（2026-10-15，取代原「吊销」）──
        //
        // 这一条是"拉黑"区别于旧「吊销」的关键：旧吊销的标记会被**重新注册清掉**
        // （`UpdateRegistrationAsync` 里原先那行 `IsRevoked = false`），
        // 于是"吊销"实际上只能挡到那台机器下一次重注册为止。
        // 现在这里先挡住：**被拉黑的机器连门都进不来**，它拿不到新的 agentKey，
        // 也就没有任何办法绕过管理员的决定 —— 除非管理员去点「取消拉黑」。
        //
        // ⚠️ 为什么按 machineKey 而不是按 agentKey 判断：客户机在"被拉黑"状态下会不停地
        //    重试连接（这是设计的一部分，为的是"取消后立即恢复"），而它手上那把
        //    agentKey 仍然有效（拉黑刻意不清它）—— 所以这里其实两侧都会命中。
        //    按 machineKey 判断是**兜底**：万一那台机器把本地状态目录删了、以全新身份来注册，
        //    也照样挡得住。
        if (existing is { IsBlocked: true })
        {
            return Fail(
                "blocked",
                "该客户端已被管理员拉黑，中心不与其建立连接。请在网页上「取消拉黑」后重试。",
                remoteAddress,
                machineKey);
        }

        // 容量闸门：只挡"新机器"。已在表里的机器重新注册不该被上限拦住 ——
        // 否则一旦到达上限，那些机器重装后就再也回不来了。
        if (existing is null)
        {
            var count = (await _hosts.ListAsync(cancellationToken).ConfigureAwait(false)).Count;

            if (count >= options.MaxHosts)
            {
                // 只留一处日志（在 Fail 里统一写）—— 写两遍会让"同一件事两行、内容还各有侧重"，
                // 排障时反而要花时间判断它们是不是两回事。
                return Fail(
                    "toomany",
                    $"在线表已达上限（{options.MaxHosts} 台）。请先移除不再使用的客户机，或调大 Server:Agent:MaxHosts。",
                    remoteAddress,
                    machineKey);
            }
        }

        var agentKey = AgentKeys.NewSecret();
        var keyHash = AgentKeys.Hash(agentKey);

        AgentHost host;

        try
        {
            if (existing is null)
            {
                var displayName = string.IsNullOrWhiteSpace(info.HostName) ? machineKey : info.HostName.Trim();

                host = await _hosts.CreateAsync(
                    new AgentHost(
                        Id: 0,
                        MachineKey: machineKey,
                        DisplayName: displayName,
                        Group: null,
                        Note: null,
                        AgentKeyHash: keyHash,
                        AgentVersion: info.AgentVersion,
                        LastAddress: remoteAddress,
                        RegisteredAt: now,

                        // ⚠️ **注册不算"见过"** —— 这里刻意是 null。
                        //
                        // "最后一次心跳"是**在线状态**的唯一判据（见 AgentPresence）。把它在注册时
                        // 就填上，会让一台"只发了一次 HTTP 注册请求、从未建立长连接"的机器
                        // 在页面上显示成"在线"，而且「等待连接」这个状态**永远不可能出现**
                        // ——那恰恰是排查"客户端装起来了吗"时最需要的信号。
                        // 真正的第一次心跳发生在 /hubs/agent 连上之后（AgentHub.OnConnectedAsync）。
                        LastSeenAt: null,
                        IsBlocked: false,
                        Info: info),
                    cancellationToken).ConfigureAwait(false);

                _log.Info("Enrollment", $"客户端注册成功：{host.DisplayName}（{machineKey}，来自 {remoteAddress ?? "未知地址"}）");
            }
            else
            {
                // 走到这里 = 这台机器**没有被拉黑**（被拉黑的那条上面已经返回了）。
                //
                // 它对"移除后再加回来"是必需的：管理员「移除」是硬删记录，那台机器重新注册时
                // 走 `existing is null` 那条分支新建一行（**新 Id**）；而"自己机器码没变、
                // 只是重装/换机器"的情形走这里，复用同一行、只换 agentKey。
                await _hosts.UpdateRegistrationAsync(
                    existing.Id,
                    keyHash,
                    info.AgentVersion,
                    remoteAddress,
                    info,
                    cancellationToken).ConfigureAwait(false);

                host = await _hosts.FindAsync(existing.Id, cancellationToken).ConfigureAwait(false) ?? existing;

                _log.Info("Enrollment", $"客户端重新注册：{host.DisplayName}（{machineKey}，来自 {remoteAddress ?? "未知地址"}）");
            }
        }
        catch (Exception ex)
        {
            // 注册失败必须让客户端看到明确原因（它会在托盘气泡里显示），
            // 而不是让它在"重试 → 又失败"的循环里耗着。
            _log.Error("Enrollment", $"客户端注册写入失败（机器 {machineKey}）", ex);
            return Fail("error", "服务端写入失败，请查看服务端日志。", remoteAddress, machineKey);
        }

        // 让内存快照立刻包含这台机器，否则第一次列表刷新前页面看不到它。
        await _registry.RefreshAsync(cancellationToken).ConfigureAwait(false);
        _registry.ApplyChange(host);

        await NotifyAsync(host.Id, cancellationToken).ConfigureAwait(false);

        return new AgentRegisterResult(
            Ok: true,
            Status: "ok",
            Message: null,
            AgentId: host.Id,
            AgentKey: agentKey,
            HeartbeatSeconds: options.HeartbeatSeconds,
            ServerVersion: ServerRuntimeInfo.ResolveVersion(),
            DisplayName: host.DisplayName);
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    private async Task NotifyAsync(int id, CancellationToken cancellationToken)
    {
        var host = _registry.Hosts.FirstOrDefault(h => h.Id == id);
        if (host is null) return;

        var dto = _registry.ToDto(host, _options.Value.Agent, BeijingTime.Now);
        await _notifier.AgentChangedAsync(dto, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 构造失败结果，**并把"为什么被拒"写进服务端日志**。
    /// <para>
    /// <b>为什么必须记</b>：`register` 是匿名端点，失败时客户端只会得到一个 400/409，
    /// 而那句原因只写在<b>客户机自己</b>的日志里 —— 运维坐在中心服务端这边，
    /// 能看到的所有痕迹只有 Serilog 请求日志的一行 `POST /api/agents/register → 400`，
    /// <b>不带任何原因</b>。于是"某台机器一直在敲门但进不来"这件事在中心侧完全不可见，
    /// 现场唯一的现象就是"装了客户端，页面上一直没有它"，很容易被误判成
    /// "服务端发现不了客户端"——而发现（UDP 广播）其实一直是好的。
    /// </para>
    /// <para>
    /// <b>为什么去重</b>：客户端失败后会<b>每 60 秒</b>重试一次（退避上限 60 秒），
    /// 一台出问题的机器一天能刷 1440 行、十台就是 14400 行，足以把真正的信息淹掉。
    /// 所以同一个「来源 + 机器码 + 失败状态」组合<b>只记首次</b>，后续降到 Debug。
    /// </para>
    /// <para>
    /// ⚠️ 免凭据之后，这里剩下的失败原因只有两类：<c>invalid</c>（客户端连机器码都没报上来）
    /// 与 <c>toomany</c>（在线表满）。原设计里最常见的 <c>missing</c>／<c>unauthorized</c>
    /// 已随准入令牌一并消失 —— 所以"装了客户端却在中心侧看不见"，
    /// <b>不再可能由凭据问题引起</b>，排查可以直接跳到网络与服务端日志。
    /// </para>
    /// </summary>
    private AgentRegisterResult Fail(string status, string message, string? remoteAddress, string? machineKey)
    {
        var source = string.IsNullOrWhiteSpace(remoteAddress) ? "未知地址" : remoteAddress;
        var machine = string.IsNullOrWhiteSpace(machineKey) ? "未提供" : machineKey;
        var line = $"拒绝客户端注册（{status}）：{message}｜来源 {source}｜机器 {machine}";

        if (MarkRejectionReported(source, machine, status))
        {
            _log.Warn("Enrollment", line + "｜同一来源的相同原因只记录一次，重试不再重复。");
        }
        else
        {
            _log.Debug("Enrollment", line);
        }

        return new(false, status, message, null, null, null, null, null);
    }

    /// <summary>
    /// 这个「来源 + 机器码 + 状态」组合是不是第一次见。
    /// <para>
    /// 键里含客户端自报的 machineKey，而端点匿名 ⇒ 容量必须有上限，
    /// 否则一串伪造 machineKey 就能把内存顶爆。满了直接整表清空重来：
    /// 代价是"最坏情况下多记几行重复日志"，比"内存无界"划算得多。
    /// </para>
    /// </summary>
    private bool MarkRejectionReported(string source, string machine, string status)
    {
        var key = $"{source}|{machine}|{status}";

        lock (_reportedRejections)
        {
            if (_reportedRejections.Count >= MaxTrackedRejections) _reportedRejections.Clear();

            return _reportedRejections.Add(key);
        }
    }

    /// <summary>被拒组合的记忆上限。取值只影响"日志会不会重复"，不影响功能。</summary>
    private const int MaxTrackedRejections = 512;
}
