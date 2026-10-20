using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using ControlServer.Server.Hubs;
using ControlServer.Server.Options;
using ControlServer.Server.Security;
using ControlServer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.Api;

/// <summary>
/// 客户端主机管理（M4.1）。
/// <para>
/// <b>读写分离是刻意的</b>：
/// </para>
/// <list type="bullet">
///   <item><b>读</b>（列表 / 详情）走内存快照 <see cref="AgentHostRegistry"/>，
///         页面每次刷新都读它 —— 不碰数据库，也就不会因为"20 台机器每 5 秒刷一次"
///         把 SQLite 读热。代价是可能落后最多 60 秒，对"看板"完全够用。</item>
///   <item><b>写</b>（改名 / 吊销 / 移除）直接落库并<b>立刻刷新快照</b>，
///         所以"改完立刻看到"这条体验是成立的。</item>
/// </list>
/// <para>
/// <b>权限</b>（决策 D12）：看板给 <see cref="Policies.CanOperate"/>（运维日常要看），
/// 而改名 / 吊销 / 移除 / 重启这些"动设备准入与资产台账"的操作一律
/// <see cref="Policies.AdminOnly"/>。
/// </para>
/// </summary>
[ApiController]
[Route("api/agents")]
public sealed class AgentsController : ControllerBase
{
    private readonly AgentHostRegistry _registry;
    private readonly IAgentHostStore _hosts;
    private readonly AgentEnrollmentService _enrollment;
    private readonly AgentTelemetryService _telemetry;
    private readonly IAgentTelemetryStore _telemetryStore;
    private readonly AgentConfigService _configs;
    private readonly AgentUdpCommandService _udp;
    private readonly AgentFileBrowseService _browse;
    private readonly AgentReceivedDataService _received;
    private readonly AgentWakeService _wake;
    private readonly IHubContext<AgentHub> _agentHub;
    private readonly LogService _log;
    private readonly IOptions<ServerOptions> _options;

    public AgentsController(
        AgentHostRegistry registry,
        IAgentHostStore hosts,
        AgentEnrollmentService enrollment,
        AgentTelemetryService telemetry,
        IAgentTelemetryStore telemetryStore,
        AgentConfigService configs,
        AgentUdpCommandService udp,
        AgentFileBrowseService browse,
        AgentReceivedDataService received,
        AgentWakeService wake,
        IHubContext<AgentHub> agentHub,
        LogService log,
        IOptions<ServerOptions> options)
    {
        _registry = registry;
        _hosts = hosts;
        _enrollment = enrollment;
        _telemetry = telemetry;
        _telemetryStore = telemetryStore;
        _configs = configs;
        _udp = udp;
        _browse = browse;
        _received = received;
        _wake = wake;
        _agentHub = agentHub;
        _log = log;
        _options = options;
    }

    // ═══════════════════════════════════════════════════════════
    // 看：客户机目录（回传源路径选择，M4.4+）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 列出该客户机上某个目录的内容（回传任务的「源路径」下拉框用）。
    /// <para>
    /// <paramref name="path"/> 留空 = 列出那台机器的<b>顶层</b>（"此电脑"：全部盘符）——
    /// 页面先给"能去哪儿"，再逐级进去，用户就不必手输路径
    /// （手输是回传失败最常见的来源：盘符、大小写、多一个空格、中文全角）。
    /// <b>2026-10-15 起浏览与回传都不限制目录</b>，这里不存在"白名单根"的概念。
    /// </para>
    ///
    /// <para>
    /// 🔗 <b>这是本项目第一条要等客户机回话的端点</b>：它把请求转给那台机器的长连接
    /// （<c>ListDirectory</c>），等它回一个目录列表。客户机因此<b>仍然不开任何入站端口</b>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>失败一律回 200 + <c>ok:false</c>，而不是 4xx/5xx</b>：这里的失败几乎都是
    /// <b>预期内的业务结果</b>（机器离线、路径越界、客户端太旧），页面要把它们
    /// <b>就地显示在下拉框旁边</b>（"这台机器离线"）而不是弹一个错误提示 ——
    /// 弹提示会把"用户浏览到一个没权限的目录"渲染成"系统出错了"。
    /// 只有"请求本身不合法"（路径过长）与"机器不存在"才是 4xx。
    /// </para>
    ///
    /// <para>
    /// <b>权限</b>：<see cref="Policies.AdminOnly"/> —— 与"建立分发任务"同级。
    /// 它暴露的是客户机的目录与文件名，属于"作用到别的机器上"的动作。
    /// </para>
    /// </summary>
    [HttpGet("{id:int}/browse")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> BrowseDirectory(
        int id,
        [FromQuery] string? path,
        CancellationToken cancellationToken)
    {
        if (_registry.Hosts.FirstOrDefault(h => h.Id == id) is null)
            return NotFound(new { message = "客户端不存在。" });

        // 路径长度兜底：客户机那边也会判，但那是一次跨网络的往返；
        // 明显非法的请求不必去打扰它（Windows 的路径上限是 32767，这里取一个宽松但有限的数）。
        if (path is { Length: > 4096 })
            return BadRequest(new { message = "路径过长。" });

        var result = await _browse.BrowseAsync(id, path, cancellationToken).ConfigureAwait(false);

        return Ok(result);
    }

    // ═══════════════════════════════════════════════════════════
    // 看：客户机「接收到的数据」（排查"指令不生效"，2026-10-13）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 要一份该客户机的「接收到的数据」快照：它收到了什么、当时判成了什么。
    ///
    /// <para>
    /// <b>为什么需要它</b>：中心这一侧只知道"我 <c>SendAsync</c> 成功了"，
    /// 而现场看到的"机器毫无反应"其实是三种故障之一 ——
    /// <b>报文没到</b>（防火墙 / 网段）、<b>到了但认不出</b>（多一个空格就会这样）、
    /// <b>认出了但没执行</b>（演练模式开着 / 会话侧不在）。这三者的处置完全不同，
    /// 而区分它们唯一的证据在客户机手上。
    /// </para>
    ///
    /// <para>
    /// <b>返回 200 而不是 4xx 的两种情况</b>：机器离线、客户机版本太旧 ——
    /// 那是<b>预期内的业务结果</b>，页面应当就地显示（"这台机器不在线"）
    /// 而不是弹一个"系统出错了"。
    /// </para>
    ///
    /// <para><b>权限</b>：<see cref="Policies.AdminOnly"/> —— 与目录浏览同级。
    /// 它暴露的是客户机所在网段里谁在给它发指令，属于"别人的机器上的事"。</para>
    /// </summary>
    [HttpGet("{id:int}/received")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> ListReceivedData(int id, CancellationToken cancellationToken)
    {
        if (_registry.Hosts.FirstOrDefault(h => h.Id == id) is null)
            return NotFound(new { message = "客户端不存在。" });

        var result = await _received.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return Ok(result);
    }

    // ═══════════════════════════════════════════════════════════
    // 看：在线表
    // ═══════════════════════════════════════════════════════════

    /// <summary>客户端列表（在线 / 不稳定 / 离线，含最新主机摘要）。</summary>
    [HttpGet]
    [Authorize(Policy = Policies.CanOperate)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var now = BeijingTime.Now;
        var options = _options.Value.Agent;

        // 配置版本一次批量取回，避免逐台查（100+ 台时那是 100 次查询）。
        var versions = await _configs.ListVersionsAsync(cancellationToken).ConfigureAwait(false);

        var items = _registry.Hosts
            .Select(h => _registry.ToDto(h, options, now) with
            {
                ConfigVersion = versions.TryGetValue(h.Id, out var v) ? v : 0,
            })
            .OrderBy(x => x.State == AgentStates.Online ? 0 : x.State == AgentStates.Unstable ? 1 : 2)
            .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return Ok(new AgentListDto(
            items,
            items.Count,
            items.Count(x => x.State == AgentStates.Online),
            items.Count(x => x.State == AgentStates.Unstable),
            items.Count(x => x.State == AgentStates.Offline || x.State == AgentStates.Never)));
    }

    /// <summary>单台详情（含完整主机信息与"此刻是否握着长连接"）。</summary>
    [HttpGet("{id:int}")]
    [Authorize(Policy = Policies.CanOperate)]
    public async Task<IActionResult> Detail(int id, CancellationToken cancellationToken)
    {
        var host = FindHost(id);
        if (host is null) return NotFound(new { message = "客户端不存在。" });

        var dto = _registry.ToDto(host, _options.Value.Agent, BeijingTime.Now) with
        {
            ConfigVersion = await _configs.GetVersionAsync(id, cancellationToken).ConfigureAwait(false),
        };

        return Ok(new AgentDetailDto(dto, host.Info, _registry.IsConnected(id)));
    }

    // ═══════════════════════════════════════════════════════════
    // 看：遥测（M4.2）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 一段遥测时序（明细 / 降采样 / 日聚合，由服务端按窗口长度和点数上限自动选择）。
    /// </summary>
    /// <param name="from">起点（缺省 = <paramref name="to"/> 往前 <c>DefaultQueryHours</c> 小时）。</param>
    /// <param name="to">终点（缺省 = 现在）。</param>
    /// <param name="step">分桶粒度（秒）。传了就按它分桶，不传由服务端按 <paramref name="points"/> 选。</param>
    /// <param name="points">期望的最大点数（缺省 720）。</param>
    [HttpGet("{id:int}/metrics")]
    [Authorize(Policy = Policies.CanOperate)]
    public async Task<IActionResult> Metrics(
        int id,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? step,
        [FromQuery] int? points,
        CancellationToken cancellationToken)
    {
        var host = FindHost(id);
        if (host is null) return NotFound(new { message = "客户端不存在。" });

        var options = _options.Value.Agent.Telemetry;

        if (!options.Enabled)
        {
            // 明确回一个"未启用"，而不是空序列 —— 两者在页面上必须是不同的文案
            //（"没数据"和"这个功能被关掉了"该做的事完全不同）。
            return Ok(new AgentMetricsSeriesDto(
                id,
                from ?? BeijingTime.Now,
                to ?? BeijingTime.Now,
                0,
                "disabled",
                0,
                Array.Empty<AgentMetricsPointDto>()));
        }

        var end = to ?? BeijingTime.Now;
        var start = from ?? end.AddHours(-Math.Max(1, options.DefaultQueryHours));

        if (start > end) (start, end) = (end, start);

        // 挡住"查十年"：既会扫穿库，也会让浏览器画出几百万个点。
        var maxSpan = TimeSpan.FromDays(Math.Max(1, options.MaxQueryDays));
        if (end - start > maxSpan) start = end - maxSpan;

        var series = await _telemetry
            .QuerySeriesAsync(id, start, end, step, points, cancellationToken)
            .ConfigureAwait(false);

        return Ok(series);
    }

    /// <summary>开关机 / 蓝屏记录（最近在前）+ 全量统计。</summary>
    [HttpGet("{id:int}/power-events")]
    [Authorize(Policy = Policies.CanOperate)]
    public async Task<IActionResult> PowerEvents(
        int id,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var host = FindHost(id);
        if (host is null) return NotFound(new { message = "客户端不存在。" });

        var result = await _telemetry
            .QueryPowerEventsAsync(id, from, limit ?? 200, cancellationToken)
            .ConfigureAwait(false);

        return Ok(result);
    }

    /// <summary>最新一份硬件清单（从未上报过时 <c>CapturedAt</c> 为 null）。</summary>
    [HttpGet("{id:int}/inventory")]
    [Authorize(Policy = Policies.CanOperate)]
    public async Task<IActionResult> Inventory(int id, CancellationToken cancellationToken)
    {
        var host = FindHost(id);
        if (host is null) return NotFound(new { message = "客户端不存在。" });

        var record = await _telemetryStore.GetInventoryAsync(id, cancellationToken).ConfigureAwait(false);

        return Ok(new AgentInventoryEnvelopeDto(id, record?.CapturedAt, record?.Inventory));
    }

    // ═══════════════════════════════════════════════════════════
    // 写：资产台账
    // ═══════════════════════════════════════════════════════════

    /// <summary>改名 / 分组 / 备注。</summary>
    [HttpPut("{id:int}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Update(
        int id,
        [FromBody] AgentUpdateRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { message = "请求体不能为空。" });

        var ok = await _hosts
            .UpdateDetailsAsync(id, request.DisplayName, request.Group, request.Note, cancellationToken)
            .ConfigureAwait(false);

        if (!ok) return NotFound(new { message = "客户端不存在。" });

        _log.Info("Agent", $"客户端信息已更新（Id={id}，操作者 {User.Identity?.Name ?? "unknown"}）");
        await RefreshAsync(cancellationToken).ConfigureAwait(false);

        return Ok(await MutationAsync(true, "ok", "已保存。", cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 记住某台客户机的「默认目录」（2026-10-14）。
    ///
    /// <para>
    /// <b>为什么单独开一个端点</b>：过去中心的路径记忆只在**建单成功之后**才被回写
    /// （见 <c>DistributionsController</c> 里那次 <c>SetLastPathAsync</c>）。
    /// 于是"在资源管理器里挑对了目录、但这次先不建单"就白挑了 —— 下次还得从盘符一层层点进去，
    /// 而那正是这套界面里最烦的一步。这个端点是"把人挑的结果直接存下来"的显式动作。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>它与文件分发无关，是纯粹的服务端本地记录</b>（写自己的库），
    /// 不会碰任何客户机 —— 所以它归"未授权时放行"，与"建分发任务"那种"让一批机器去干活"不同。
    /// </para>
    /// <para>
    /// ⚠️ 存的是<b>预填值</b>，不是权限：真正拦越界的仍是客户机的白名单，
    /// 这里存什么都不放宽那条边界。
    /// </para>
    /// </summary>
    [HttpPut("{id:int}/path-memory")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> RememberPath(
        int id,
        [FromBody] AgentPathMemoryRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { message = "请求体不能为空。" });

        if (request.TargetDir is null && request.SourceDir is null)
        {
            return BadRequest(new { message = "targetDir 与 sourceDir 至少要给一个（都不给等于什么都没做）。" });
        }

        // 空串有明确语义（"就是那台机器的默认目录"），所以只 trim、不把空串当 null。
        var target = request.TargetDir?.Trim();
        var source = request.SourceDir?.Trim();

        var ok = await _hosts.SetLastPathAsync(id, target, source, cancellationToken).ConfigureAwait(false);
        if (!ok) return NotFound(new { message = "客户端不存在。" });

        // 内存快照同步刷一遍：只写库的话，页面要等最多 60 秒（下一次快照刷新）才会回显，
        // 而运维在这段时间里会以为"没记住"再点一次（与 SetAppliedConfigVersion 同一个理由）。
        _registry.SetLastPath(id, target, source);

        _log.Info("Agent", $"客户端默认目录已记忆（Id={id}，操作者 {User.Identity?.Name ?? "unknown"}）");

        return Ok(await MutationAsync(true, "ok", "已记住。下次打开这台机器的路径框会自动填上。", cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// 把该机**拉黑**（2026-10-15，取代原「吊销」）。
    /// <para>
    /// <b>拉黑 = 不再与它建立任何连接</b>：agentKey 仍然有效（刻意不清，见
    /// <c>EfAgentHostStore.SetBlockedAsync</c>），但认证层看到标记就一律拒绝，
    /// 于是它连不上、也没有任何端点可用。正在连的那条长连接会<b>立刻被掐断</b>。
    /// </para>
    /// <para>
    /// <b>与「移除」的区别</b>：移除是硬删记录（那台机器会重新注册、以新 Id 回来）；
    /// 拉黑是"记着它、但不理它"—— 记录、配置、历史遥测全部保留，
    /// 点一下「取消拉黑」它就在下一次重试时连回来（秒级，不需要重装或重新注册）。
    /// </para>
    /// </summary>
    [HttpPost("{id:int}/block")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Block(int id, CancellationToken cancellationToken)
    {
        var host = await _hosts.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (host is null) return NotFound(new { message = "客户端不存在。" });

        await _hosts.SetBlockedAsync(id, true, cancellationToken).ConfigureAwait(false);

        _log.Info("Agent", $"客户端已被拉黑：{host.DisplayName}（{host.MachineKey}，操作者 {User.Identity?.Name ?? "unknown"}）");

        // 立刻通知那台机器：让它自己停下来并在本地留下痕迹。
        //
        // 只靠"心跳时被服务端掐断"其实也能断开（≤10 秒），但现场会看到
        // "服务还在跑、托盘还是绿的、就是连不上"，那比直接告诉它难解释得多。
        // ⚠️ 客户端收到这条**不会**清掉本地身份（拉黑是可逆的），只进"被拉黑"状态。
        var connectionId = _registry.ConnectionIdOf(id);

        if (!string.IsNullOrEmpty(connectionId))
        {
            try
            {
                await _agentHub.Clients.Client(connectionId)
                    .SendAsync(AgentHubMethods.Blocked, "该客户端已被管理员拉黑。", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("Agent", $"向被拉黑客户端发送通知失败（不影响拉黑本身）：{ex.Message}");
            }
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);

        return Ok(await MutationAsync(
            true,
            "ok",
            "已拉黑。中心不再与该客户机建立连接；取消拉黑后会立刻恢复。",
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 取消拉黑：<b>立刻恢复连接</b>（不需要重新注册，也不需要在客户机上做任何事）。
    /// <para>
    /// 之所以能"立刻"：拉黑期间那台机器的 agentKey 一直有效，它只是在不停地被拒 ——
    /// 标记一清，它下一次重试（15 秒一轮）就直接连上了。
    /// </para>
    /// </summary>
    [HttpPost("{id:int}/unblock")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Unblock(int id, CancellationToken cancellationToken)
    {
        var ok = await _hosts.SetBlockedAsync(id, false, cancellationToken).ConfigureAwait(false);
        if (!ok) return NotFound(new { message = "客户端不存在。" });

        _log.Info("Agent", $"客户端拉黑已解除（Id={id}，操作者 {User.Identity?.Name ?? "unknown"}）");
        await RefreshAsync(cancellationToken).ConfigureAwait(false);

        return Ok(await MutationAsync(
            true,
            "ok",
            "已取消拉黑。该客户机会在下一次重试时（≤15 秒）自动连回来。",
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 移除记录（**硬删**）。
    /// <para><b>不等于卸载客户机上的 Agent</b> —— 那台机器上的服务还在跑，
    /// 它会发现自己连不上，然后<b>重新注册</b>并再次出现（以**新 Id**）。要真正下线，得去那台机器上卸载。</para>
    /// <para>
    /// 🔥 <b>"它会重新注册"这件事在 2026-10-14 之前是**不成立的**</b>（这条注释一直是句空话）：
    /// 客户端手里那把 agentKey 在库里被删掉之后必然 401，而它把 401 当普通连接失败、
    /// 拿着废钥匙无限重试 —— 表现就是"移除之后再也加不回来，连重启客户端都没用"。
    /// 现在由两处一起兜住：
    /// <list type="number">
    ///   <item>若这台机器**还连着**，<c>AgentHub.Heartbeat</c> 发现库里已经没它了 ⇒ 强制断开
    ///         （否则它会带着一条永远不再被承认的长连接，自己还以为在线）；</item>
    ///   <item>客户端重连时撞上 401 ⇒ <c>AgentRuntime</c> 用一次普通 HTTP 探测确认"不是网络问题、
    ///         是凭据被拒"，于是<b>作废本地身份并重新注册</b>。</item>
    /// </list>
    /// 回归用例：冒烟 <c>agent-smoke.py</c> 的 H 段（"移除之后加得回来"）。
    /// </para>
    /// </summary>
    [HttpDelete("{id:int}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var host = await _hosts.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (host is null) return NotFound(new { message = "客户端不存在。" });

        await _hosts.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        _registry.Forget(id);

        // 连带删掉这台机器的配置（M4.5）。留着的话，同一台机器重新注册会拿到一个新 Id，
        // 而旧 Id 的配置行永远不会被读到 —— 又是一批"没有主人的行"。
        try
        {
            await _configs.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("Agent", $"移除设备时清理配置失败（Id={id}）", ex);
        }

        // 连带清掉这台机器的遥测（明细 / 日聚合 / 开关机记录 / 清单）。
        // 不做的话，库会慢慢积累一堆"没有主人的行"——它们永远不会被读到，
        // 却会让保留清理的统计口径（"一共多少行"）变得没法解释。
        try
        {
            await _telemetryStore.PurgeAgentAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 清理失败不该让"移除设备"这个操作失败：记录已经删了，孤儿数据下一次清理时仍可人工处理。
            _log.Error("Agent", $"移除设备时清理遥测数据失败（Id={id}）", ex);
        }

        _log.Info("Agent", $"客户端记录已移除：{host.DisplayName}（{host.MachineKey}，操作者 {User.Identity?.Name ?? "unknown"}）");

        await RefreshAsync(cancellationToken).ConfigureAwait(false);

        return Ok(await MutationAsync(true, "ok", "记录已移除（客户机上的 Agent 不会被卸载，重新连上时会再次出现）。", cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 远程重启 Agent 服务（决策 D13：做重启，卸载只给指引）。
    /// <para>
    /// ⚠️ <b>与 <c>POST {id}/power</c>（重启<b>整机</b>）是两回事</b>：这条只把 Agent 进程拉起来，
    /// 机器不重启。两者混用会出现"想重启服务却把用户机器关了"。
    /// </para>
    /// <para>
    /// 🔸 <b>它没有 UDP 兜底</b>（2026-10-15）：UDP 指令通道只支持关机 / 重启整机 / 音量，
    /// 而"重启 Agent 服务"必须由 Agent 自己在长连接上处理 —— 连接断了就是断了，
    /// 只能如实回 409 并说清原因（<c>SendCommandAsync</c> 的 <c>noudp</c> 分支）。
    /// </para>
    /// </summary>
    [HttpPost("{id:int}/reboot")]
    [Authorize(Policy = Policies.AdminOnly)]
    public Task<IActionResult> Reboot(int id, CancellationToken cancellationToken)
        => SendCommandAsync(
            id,
            AgentCommandNames.Restart,
            null,
            "重启 Agent 服务",
            "已下发重启指令（重启的是 Agent 服务，不是整机）。",
            cancellationToken);

    // ═══════════════════════════════════════════════════════════
    // 配置与指令下发（M4.5 · BootAssistant 对齐）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 读该机的配置（三张任务清单 + UDP 指令通道）。
    /// <para>
    /// <b>两种身份都能读</b>：人（管理员 / 操作员，页面用）与<b>这台机器自己</b>
    /// （AgentKey，客户端启动 / 收到变更通知后拉配置用）。策略见 <see cref="Policies.AgentConfigRead"/>。
    /// </para>
    /// <para>
    /// ⚠️ <b>机器身份只能读自己的</b>：任务清单里有目标 IP 与发送内容，
    /// 少了这层归属校验，任何拿到某台 agentKey 的机器都能读到别的机器的配置。
    /// </para>
    /// </summary>
    [HttpGet("{id:int}/config")]
    [Authorize(Policy = Policies.AgentConfigRead)]
    public async Task<IActionResult> GetConfig(int id, CancellationToken cancellationToken)
    {
        var machineId = AgentKeyAuthenticationHandler.ReadAgentId(User);

        if (machineId is { } machine)
        {
            if (machine != id)
            {
                // 403 而不是 404：它确实是台合法机器，只是越权读别人的。
                return Forbid();
            }

            // ⚠️ 机器身份**不查内存快照**：客户机往往在注册成功后的下一秒就来拉配置，
            //    而在线表快照最长可能落后 60 秒 —— 那时 FindHost 会返回 null，
            //    于是客户端拿到 404，表现为"刚装好的机器一直拉不到配置"。
            //    它的身份已经由 AgentKey 认证（对着库校验过哈希）确认了，不需要快照再背书。
        }
        else if (FindHost(id) is null)
        {
            return NotFound(new { message = "客户端不存在。" });
        }

        var record = await _configs.FindAsync(id, cancellationToken).ConfigureAwait(false);

        return Ok(AgentConfigMapper.ToDto(record));
    }

    /// <summary>
    /// 保存该机的配置（整体替换内容，版本 +1，并立刻通知客户机来拉）。
    /// <para>
    /// 请求体里 <c>null</c> 的块 = <b>不动那一块</b>；空数组 = <b>清空那一块</b>。
    /// </para>
    /// </summary>
    [HttpPut("{id:int}/config")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> PutConfig(
        int id,
        [FromBody] AgentConfigSaveRequest? request,
        CancellationToken cancellationToken)
    {
        var host = FindHost(id);
        if (host is null) return NotFound(new { message = "客户端不存在。" });

        var existing = await _configs.FindAsync(id, cancellationToken).ConfigureAwait(false);

        // 合并 → 规范化 → 校验。三步都走 Core 的纯函数，保证与客户机执行前的判断完全一致
        //（各写一份的下场是"服务端放行、客户端拒绝"，表现为"保存成功但机器没动"）。
        var normalized = AgentConfigRules.Normalize(
            AgentConfigRules.Merge(existing?.Content, AgentConfigMapper.ToPatch(request)));
        var problems = AgentConfigRules.Validate(normalized);

        if (problems.Count > 0)
        {
            return BadRequest(new AgentConfigSaveResult(false, "invalid", string.Join("；", problems), null));
        }

        var saved = await _configs.SaveAsync(id, normalized, cancellationToken).ConfigureAwait(false);

        _log.Info("Agent", $"客户机配置已更新（Id={id}，v{saved.Version}，操作者 {User.Identity?.Name ?? "unknown"}）");

        return Ok(new AgentConfigSaveResult(true, "ok", null, AgentConfigMapper.ToDto(saved)));
    }

    /// <summary>
    /// 要求该机立刻重新拉取配置（**不依赖版本判断**）。
    /// <para>
    /// 用于"机器看起来没按配置动"的现场排障：常规路径是版本变更推送，
    /// 这一条是运维手动"再推一次"。离线时回 409（请求合法，只是现在做不到）。
    /// </para>
    /// <para>
    /// ⚠️ <b>它不做 UDP 兜底</b>（2026-10-15）：「重新拉取配置」是"让它去拉"，而拉配置本身
    /// 要经长连接 —— 断线时把这句话发过去毫无意义。配置一直留在中心，
    /// 那台机器上线后按<b>版本号</b>自己就会对齐（<c>Welcome</c> 里带着版本号），
    /// 所以这句话要说清"不用你操心"，而不是让人以为配置丢了。
    /// </para>
    /// </summary>
    [HttpPost("{id:int}/config/redeliver")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> RedeliverConfig(int id, CancellationToken cancellationToken)
    {
        var host = FindHost(id);
        if (host is null) return NotFound(new { message = "客户端不存在。" });

        if (!await _configs.RequestReloadAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return Conflict(await MutationAsync(
                false,
                "offline",
                "该客户端当前离线（长连接已断开），无法通知它立刻重新拉取配置。"
                + "配置不会丢：它上线后按版本号会自动对齐（也可等它连上后再点一次）。",
                cancellationToken).ConfigureAwait(false));
        }

        _log.Info("Agent", $"已要求客户端重新拉取配置：{host.DisplayName}（操作者 {User.Identity?.Name ?? "unknown"}）");

        return Ok(await MutationAsync(true, "ok", "已要求该客户端重新拉取配置。", cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 客户机告知「本机要换到别的服务端地址去了」（2026-10-15，需求①）。
    /// <para>
    /// <b>身份是"这台机器自己"</b>（AgentKey），且<b>只能报到自己名下</b>（<paramref name="id"/>
    /// 必须与凭据里的 AgentId 一致）—— 与读配置那条同一条归属纪律。
    /// </para>
    /// <para>
    /// ⚠️ <b>刻意不改任何服务端状态</b>（不摘记录、不标离线）：需求口径是"只通知，不阻塞保存"。
    /// 它唯一的作用是让日志里留下"这台机器是<b>主动搬走</b>的"，而不是"忽然掉线了"。
    /// 真正的离线由"它不再心跳"自然判定。
    /// </para>
    /// </summary>
    [HttpPost("{id:int}/server-change")]
    [Authorize(Policy = Policies.AgentSelfReport)]
    public IActionResult ServerChange(int id, [FromBody] AgentServerChangeRequest? request)
    {
        var machineId = AgentKeyAuthenticationHandler.ReadAgentId(User);

        if (machineId is { } machine && machine != id)
        {
            // 403 而不是 404：它确实是台合法机器，只是越权替别人报告。
            return Forbid();
        }

        var host = FindHost(id);

        _log.Info(
            "Agent",
            $"客户机告知将切换到新的中心：{host?.DisplayName ?? id.ToString()}"
            + $"（{host?.MachineKey ?? "未知机器码"}）→ {request?.ServerUrl ?? "(未提供地址)"}");

        return Ok(new { message = "已记录。本机可以按新地址重连。" });
    }

    /// <summary>
    /// **网络唤醒**（WoL，2026-10-15，需求③）。
    /// <para>
    /// 服务端直接朝局域网发魔术包，<b>不经过客户机</b> —— 因为目标机器是关着的。
    /// 对该机上报过的<b>每一块网卡 MAC</b> 各发一遍，并同时发往全局广播与
    /// 「该机最后来源 IP 的定向广播地址」（多网卡、跨网段都尽量覆盖）。
    /// </para>
    /// <para>
    /// ⚠️ <b>没有应答</b>（WoL 是单向的）：返回 <c>ok</c> 只代表"包发出去了"，
    /// 机器是否真的开机要看现场 —— 这句话也写在返回的 message 里，免得被读成"已开机"。
    /// </para>
    /// <para>返回 <b>409</b> 的两种情形：这台机器还没有硬件清单（拿不到 MAC）／清单里的 MAC 全都不可用。</para>
    /// </summary>
    [HttpPost("{id:int}/wake")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Wake(int id, CancellationToken cancellationToken)
    {
        var host = await _hosts.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (host is null) return NotFound(new { message = "客户端不存在。" });

        var outcome = await _wake
            .WakeAsync(id, host.LastAddress, host.Info?.Interfaces, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Ok) return Conflict(new { message = outcome.Message });

        return Ok(await MutationAsync(true, "ok", outcome.Message, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 下发电源操作：关机 / 重启计算机。
    /// <para>
    /// ⚠️ 这与 <c>POST {id}/reboot</c>（<b>重启 Agent 服务</b>）是两回事 ——
    /// 用户点错一次就是"整台机器被关掉"。前端必须把两者文案写清楚。
    /// </para>
    /// <para>
    /// 🔥 <b>长连接不在时不再拒绝，而是改走 UDP 指令通道尽力发出</b>（2026-10-15，需求②）——
    /// 见 <see cref="SendCommandAsync"/> 与 <see cref="SendWhileUnreachableAsync"/>。
    /// 于是<b>离线时它仍然可能返回 <c>200</c></b>（<c>status="udp"</c>，消息里明写"该客户端当前离线"），
    /// 这与"返回 409"的差别是"报文已经上了网线"与"根本发不出去"，前端要分开说。
    /// </para>
    /// </summary>
    [HttpPost("{id:int}/power")]
    [Authorize(Policy = Policies.AdminOnly)]
    public Task<IActionResult> Power(int id, [FromBody] AgentPowerRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || !AgentPowerActions.IsValid(request.Action))
        {
            return Task.FromResult<IActionResult>(
                BadRequest(new { message = "action 只能是 shutdown（关机）或 restart（重启计算机）。" }));
        }

        return SendCommandAsync(
            id,
            AgentCommandNames.Power,
            request.Action,
            AgentPowerActions.DisplayName(request.Action),
            $"已下发「{AgentPowerActions.DisplayName(request.Action)}」指令。",
            cancellationToken);
    }

    /// <summary>
    /// 下发音量操作：提高 / 降低 / 静音 / 解除静音。
    /// <para>
    /// <b>实际执行在会话侧（托盘进程）</b>：服务在 Session 0 里改音量是"无声失败"
    /// —— 它改的是 Session 0 的音频，用户那边听不到任何变化。
    /// </para>
    /// <para>
    /// 🔸 与关机 / 重启同一条纪律：长连接不在时改走 UDP 指令通道尽力发出（2026-10-15）。
    /// </para>
    /// </summary>
    [HttpPost("{id:int}/volume")]
    [Authorize(Policy = Policies.AdminOnly)]
    public Task<IActionResult> Volume(int id, [FromBody] AgentVolumeRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || !AgentVolumeActions.IsValid(request.Action))
        {
            return Task.FromResult<IActionResult>(
                BadRequest(new { message = "action 只能是 up / down / mute / unmute。" }));
        }

        return SendCommandAsync(
            id,
            AgentCommandNames.Volume,
            request.Action,
            AgentVolumeActions.DisplayName(request.Action),
            $"已下发「{AgentVolumeActions.DisplayName(request.Action)}」指令。",
            cancellationToken);
    }

    /// <summary>
    /// <b>经 UDP 指令通道</b>下发一条指令（M4.6）。
    /// <para>
    /// 与上面两个端点走<b>不同路径</b>：那条走 SignalR（要求机器在线），这条由服务端
    /// <b>直接向客户机的 UDP 通道端口发一条明文报文</b>。它的价值在于：
    /// 客户端的长连接断了（但机器还在、网还通）时，这是唯一还够得着的控制路径。
    /// </para>
    /// <para>
    /// 🔥 <b>2026-10-13 起报文是纯明文</b>（<c>DOWN</c> / <c>RESTART</c> / <c>+</c> / <c>-</c> / <c>0</c> / <c>1</c>）。
    /// 原来那条"客户机上报通道密钥、服务端拿它签名"的前置条件<b>已整体删除</b> ——
    /// 于是这里只剩两个前置条件，而失败态也少了一种。
    /// </para>
    /// <para>
    /// ⚠️ <b>"已发出"≠"已执行"</b>：UDP 无应答。报文是否被接受
    /// （来源黑名单、报文能否解析、端口是否绑着、防火墙有没有放行）
    /// 只能去那台机器的日志里看。所以这里回的是"已发出"，并且文案里明说这一点 ——
    /// 否则"点了没反应"会被当成服务端坏了。
    /// </para>
    /// <para>
    /// 🔥 <b>2026-10-14 起"能不能发"改按客户机报上来的实时态判</b>，不再只看配置：
    /// </para>
    /// <list type="number">
    ///   <item><b>有实时态（客户机正在上报）</b> ⇒ 以它为准。
    ///         没在听就直接拒绝，并把<b>客户机自己给的那句原因</b>原样转出来
    ///         （"本机已停用任务下发" / "监听 8090 失败：…"），端口也用它<b>实际在听的</b>那个。
    ///         这正是"指令不生效"从"要翻机器日志才知道"变成"页面上直接写清"的关键。</item>
    ///   <item><b>没有实时态</b>（旧版客户端 / 中心刚重启） ⇒ 退回看配置，
    ///         但文案里要<b>说清这是"配置态"而不是"实际态"</b>，别让人误以为已经确认过了。</item>
    /// </list>
    /// <para>
    /// 另外，整机地址仍然是必需条件（<c>LastAddress</c>）：没有地址就没有收包方。
    /// </para>
    /// </summary>
    [HttpPost("{id:int}/udp-command")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> SendUdpCommand(
        int id,
        [FromBody] AgentUdpCommandRequest? request,
        CancellationToken cancellationToken)
    {
        // 校验用与客户机**同一个**解析器：这样"这里放行的"必然"客户机认得"。
        if (request is null || !UdpCommandProtocol.TryResolveCommand(request.Command, out _, out _))
        {
            return BadRequest(new
            {
                message = "command 只能是 DOWN / RESTART / + / - / 0 / 1，"
                    + "或等价的 power:shutdown / volume:up 这类写法。",
            });
        }

        var host = _registry.Hosts.FirstOrDefault(h => h.Id == id);
        if (host is null) return NotFound(new { message = "客户端不存在。" });

        int port;
        string channelNote;

        // ── ① 优先用客户机自己报的实时态：它才是"此刻能不能收"的真相 ──
        if (_registry.UdpStateOf(id) is { } state)
        {
            if (!state.Listening)
            {
                return Conflict(await MutationAsync(
                    false,
                    "channeloff",
                    "该机的 UDP 指令通道此刻没有在监听，报文发过去也会被内核丢掉。"
                    + $"客户机自己给的原因是：{state.StateText ?? "(未说明)"}",
                    cancellationToken).ConfigureAwait(false));
            }

            // ⚠️ 用**实际在听的端口**，不是配置里的端口：两者可能不一致，
            //    拿配置端口发就是发给一个没人听的端口（而页面会显示"已发出"）。
            port = state.Port ?? 0;

            if (port is < 1 or > 65535)
            {
                return Conflict(await MutationAsync(
                    false,
                    "badport",
                    "客户机报告它在监听，却没有给出可用的端口号 —— 这是它那边的状态异常，"
                    + "请到那台机器上查看客户端日志。",
                    cancellationToken).ConfigureAwait(false));
            }

            channelNote = $"客户机实际在监听 {port}";
        }
        else
        {
            // ── ② 没有实时态：退回看配置，并明说这是"配置态" ──
            var record = await _configs.FindAsync(id, cancellationToken).ConfigureAwait(false);

            if (record is null)
            {
                return Conflict(await MutationAsync(
                    false,
                    "channeloff",
                    "还没有收到该机的 UDP 通道状态（它可能离线，或客户端版本过旧），"
                    + "而中心这边也从未下发过配置 —— 无从判断端口。"
                    + "请等它上线并上报一次心跳，或到「任务配置」里确认通道设置。",
                    cancellationToken).ConfigureAwait(false));
            }

            if (!record.Content.Udp.Enabled)
            {
                return Conflict(await MutationAsync(
                    false,
                    "channeloff",
                    "该机未启用 UDP 指令通道 —— 到「任务配置」里把 UDP 指令通道打开后再发。",
                    cancellationToken).ConfigureAwait(false));
            }

            port = record.Content.Udp.Port;
            channelNote = $"按中心下发的配置取端口 {port}（尚未收到该机的实时状态，"
                + "因此无法确认它此刻是否真的在监听）";
        }

        var outcome = await _udp
            .SendAsync(id, _registry.EffectiveAddress(host), port, request.Command, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Ok)
        {
            return Conflict(await MutationAsync(false, outcome.Status, outcome.Message, cancellationToken)
                .ConfigureAwait(false));
        }

        _log.Info("Agent", $"已代发 UDP 指令「{request.Command}」：{host.DisplayName}（操作者 {User.Identity?.Name ?? "unknown"}）");

        return Ok(await MutationAsync(
            true,
            "ok",
            outcome.Message + $"（{channelNote}）",
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 向客户机下发一条指令（电源 / 音量共用）。
    /// <para>
    /// 🔥 <b>2026-10-15 起：长连接不在时不再直接拒绝，而是改走 UDP 指令通道尽力发出</b>
    /// （产品口径："即便客户端离线，也要把指令发出去，但要如实告诉用户它离线"）。
    /// 两条路的差别：
    /// </para>
    /// <list type="number">
    ///   <item><b>有长连接</b> ⇒ SignalR 直推。这是唯一"中心能确认对方收到了"的通道；</item>
    ///   <item><b>没有长连接</b> ⇒ 退到 UDP 指令通道（<see cref="AgentUdpCommandService"/>）——
    ///         它不需要长连接，只要那张网通、且那台机器开着指令通道。
    ///         这正是"M4.6 说过的那条唯一还够得着的控制路径"。返回语里必须写明
    ///         「客户机长连接已断开、已改走 UDP 发出、是否生效要看那台机器」。</item>
    /// </list>
    /// <para>
    /// ⚠️ <b>"离线"不等于"一定能兜住"</b>：UDP 需要一个已知地址、一个能用的端口，
    /// 而且它<b>没有应答</b>。地址都不知道（注册后从未心跳过）时只能如实回 409 ——
    /// 那是"现在真的发不出去"，与被修掉的那个"明明在线却说离线"是两回事。
    /// </para>
    /// <para>
    /// 409 而不是 500 的原因没变：请求本身合法，只是"现在做不到"。
    /// </para>
    /// </summary>
    private async Task<IActionResult> SendCommandAsync(
        int id,
        string name,
        string? argument,
        string label,
        string successMessage,
        CancellationToken cancellationToken)
    {
        var host = await _hosts.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (host is null) return NotFound(new { message = "客户端不存在。" });

        // 拉黑 = 中心与它"不再有任何控制关系"（连心跳都要被掐断）。UDP 兜底恰恰是
        // "不建连接也能控制"的后门，不在这里挡住，拉黑就成了半拉。
        if (host.IsBlocked)
        {
            return Conflict(await MutationAsync(
                false,
                "blocked",
                $"该客户端已被拉黑，中心不再向它下发任何指令 —— 先「取消拉黑」再试。",
                cancellationToken).ConfigureAwait(false));
        }

        var connectionId = _registry.ConnectionIdOf(id);

        if (!string.IsNullOrEmpty(connectionId))
        {
            try
            {
                await _agentHub.Clients.Client(connectionId)
                    .SendAsync(AgentHubMethods.Command, new AgentCommandDto(name, argument), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 连接在"查到 Id"与"发送"之间断了（退避重连的那几十秒里很常见）。
                // 这种情况以前回 500"下发指令失败，请查看服务端日志"—— 而后台日志里
                // 只会有一条连接异常，读不出"该换个办法发"。所以这里不抛，落到与离线同一条兜底路径。
                _log.Error("Agent", $"下发「{label}」指令失败（Id={id}），改走 UDP 兜底", ex);

                return await SendWhileUnreachableAsync(host, name, argument, label, cancellationToken)
                    .ConfigureAwait(false);
            }

            _log.Info("Agent", $"已下发「{label}」指令：{host.DisplayName}（操作者 {User.Identity?.Name ?? "unknown"}）");

            // 🔥 **"已下发"不等于"已执行"** —— 中心只能保证指令上了路，
            //    客户机那边"收到没、认出没、敢不敢动手、动手了有没有成"各有各的原因
            //    （离线 / 演练模式 / 平台不支持 / 会话侧托盘不在）。
            //    只回一句"已下发"，现场就会对着一台毫无反应的机器发呆 ——
            //    所以把"去哪儿看结果"与"已知的拦路石"直接写进返回语。
            var notes = new List<string> { "指令是否真的执行，请看那台机器上的「接收到的数据」。" };

            if (_registry.UdpStateOf(id) is { ExecuteEnabled: false })
            {
                notes.Add("⚠ 该机处于演练模式（Agent:Tasks:ExecuteUdpCommands=false）—— "
                          + "指令会被记录，但不会真的执行。");
            }

            return Ok(await MutationAsync(
                true, "ok", $"{successMessage} {string.Join(" ", notes)}", cancellationToken).ConfigureAwait(false));
        }

        return await SendWhileUnreachableAsync(host, name, argument, label, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 没有长连接时的下发路径：<b>改走 UDP 指令通道尽力发出</b>（2026-10-15，需求②）。
    ///
    /// <para>
    /// <b>为什么不是直接 409</b>：客户机离线的最常见形态是"长连接断了，但机器还开着、
    /// 网还通"（重连退避最长 60 秒、或被防火墙拦了 WebSocket 而 UDP 通）。
    /// 那种情况下 UDP 报文是能到达并执行的 —— 直接拒绝等于把一条还能用的控制路径关上。
    /// </para>
    ///
    /// <para>
    /// <b>端口取哪个</b>：与 <c>POST {id}/udp-command</c> 同一条纪律 ——
    /// 客户机<b>自己报上来的实时态</b>优先（"它现在怎样"），没有实时态才退回配置里的端口
    /// （"我要它怎样"），并在返回语里说清是配置态。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>与 <c>/udp-command</c> 的一处刻意不同</b>：那个端点是"我就是要用 UDP 通道"，
    /// 所以客户机自述"没在听"时直接拒绝（中心不该谎报"已发出"）；
    /// 这里是"我要这台机器关机"，通道自述可能已经过期（它是上一次连着时报的），
    /// 于是仍然发出去，但把客户机自己那句原因<b>原样带进返回语</b>，
    /// 让运维知道这一发很可能落空、以及为什么。
    /// </para>
    /// </summary>
    private async Task<IActionResult> SendWhileUnreachableAsync(
        AgentHost host,
        string name,
        string? argument,
        string label,
        CancellationToken cancellationToken)
    {
        // ① 先判"这条指令到底能不能走 UDP"：不支持的（例如「重启 Agent 服务」）任何地址判断
        //    都是多余的 —— 它要的是长连接本身。反过来写会得到一句指向错误方向的提示
        //    （"不知道它的地址"），而现场真正该做的是等它上线。
        if (!UdpCommandProtocol.TryFormat(name, argument, out var token))
        {
            return Conflict(await MutationAsync(
                false,
                "offline",
                $"该客户端当前离线（长连接已断开），而「{label}」只能经长连接下发"
                + "（UDP 指令通道只支持关机 / 重启整机 / 音量）—— 请等它重新连上再试。",
                cancellationToken).ConfigureAwait(false));
        }

        var address = _registry.EffectiveAddress(host);

        if (string.IsNullOrWhiteSpace(address))
        {
            return Conflict(await MutationAsync(
                false,
                "offline",
                $"该客户端当前不在线（没有长连接，也还不知道它的网络地址），无法下发{label}指令"
                + " —— 它的地址要等它上线心跳一次才有，之后离线也能改走 UDP 下发。",
                cancellationToken).ConfigureAwait(false));
        }

        var state = _registry.UdpStateOf(host.Id);
        int port;
        string channelNote;

        // 客户机自己报的实时态优先：它才是"此刻在哪个口上听"的真相。
        if (state is { Listening: true, Port: > 0 and <= 65535 })
        {
            port = state.Port!.Value;
            channelNote = $"客户机上次上报它在监听 {port}";
        }
        else
        {
            // 没有可用的实时端口：退回看配置，并说清这是"配置态"而不是"实际态"。
            var record = await _configs.FindAsync(host.Id, cancellationToken).ConfigureAwait(false);

            if (record is null)
            {
                return Conflict(await MutationAsync(
                    false,
                    "channeloff",
                    $"该客户端当前离线（长连接已断开），而中心这边既没有它的 UDP 通道实时状态、"
                    + $"也从未下发过配置 —— 无从判断该把{label}指令发到哪个端口。"
                    + "等它上线一次（上报心跳与通道状态）之后，离线也能改走 UDP 下发。",
                    cancellationToken).ConfigureAwait(false));
            }

            port = record.Content.Udp.Port;

            channelNote = state is null
                ? $"按中心下发的配置取端口 {port}（尚未收到该机的实时状态，无法确认它此刻是否在监听）"
                : "客户机上次上报它**没有**在监听指令通道"
                  + $"（它自己给的原因是：{state.StateText ?? "(未说明)"}）—— "
                  + $"仍按配置端口 {port} 尽力发出，这一发很可能被丢弃";
        }

        if (port is < 1 or > 65535)
        {
            return Conflict(await MutationAsync(
                false,
                "badport",
                $"该客户端当前离线，而它的 UDP 通道端口不合法（{port}）——这是它那边的状态异常，"
                + "请到那台机器上查看客户端日志。",
                cancellationToken).ConfigureAwait(false));
        }

        var outcome = await _udp
            .SendAsync(host.Id, address, port, token, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Ok)
        {
            return Conflict(await MutationAsync(
                false,
                "offline",
                $"该客户端当前离线（长连接已断开），改走 UDP 也没能发出去：{outcome.Message}",
                cancellationToken).ConfigureAwait(false));
        }

        _log.Info(
            "Agent",
            $"已代发 UDP 指令「{token}」：{host.DisplayName}（长连接不在，操作者 {User.Identity?.Name ?? "unknown"}）");

        // 🔥 返回语要把三件事同时说清：① 客户机当前离线（所以别指望立刻生效）；
        //    ② 指令已经从另一条路发出去了；③ 结果去哪儿看。
        //    只写"已下发"会被读成"机器马上会关"，而被修掉的那个 BUG 正是这一类的误导。
        return Ok(await MutationAsync(
            true,
            "udp",
            $"⚠ 该客户端当前离线（长连接已断开），已改走 UDP 指令通道尽力发出{label}指令 —— "
            + $"「{token}」→ {address}:{port}（{channelNote}）。UDP 无应答：是否真的执行请看那台机器的日志"
            + "（Udp 分类：收到指令 / 丢弃来自 … 的报文）。",
            cancellationToken).ConfigureAwait(false));
    }

    // ═══════════════════════════════════════════════════════════
    // 客户端调用（匿名 + 免凭据）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 客户端首次注册（或换机重装后再注册）。<b>不需要任何凭据</b>。
    /// <para>
    /// <b>匿名端点</b>：客户端此刻还没有身份，而且从 2026-10-13 起也不再需要出示任何东西 ——
    /// 只要能访问到这个端点，就自动登记并拿到一份 agentKey。
    /// 因此它与用户会话无关，也不会被 RBAC / 授权闸门拦到。
    /// </para>
    /// <para>
    /// ⚠️ 语义后果见 <see cref="AgentRegisterRequest"/> 的注释：
    /// 同网段任意机器都能注册成一台客户机，所以这个端口不该暴露到不可信网络。
    /// </para>
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register(
        [FromBody] AgentRegisterRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { message = "请求体不能为空。" });

        var remoteAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = await _enrollment.RegisterAsync(request, remoteAddress, cancellationToken).ConfigureAwait(false);

        if (result.Ok) return Ok(result);

        return result.Status switch
        {
            "invalid" => BadRequest(result),

            // 409 而不是 500：请求本身完全合法，只是"此刻不行"。
            // ⚠️ 500 会让现场以为服务端坏了（这一条在"注册被拒"这类场景里尤其误导 ——
            //    被拒的原因就写在响应体里，状态码却在说"我出错了"）。
            "toomany" => StatusCode(StatusCodes.Status409Conflict, result),

            // 「已被管理员拉黑」也是同一类：它是一条**明确的业务拒绝**，
            // 而且客户机要靠读响应体里的 status="blocked" 才能进"被拉黑"状态
            // （见 AgentRuntime.TryRecoverIdentityAsync）—— 状态码必须与语义一致。
            "blocked" => StatusCode(StatusCodes.Status409Conflict, result),

            _ => StatusCode(StatusCodes.Status500InternalServerError, result),
        };
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    private AgentHost? FindHost(int id) => _registry.Hosts.FirstOrDefault(h => h.Id == id);

    // ── M4.5：配置的 DTO ↔ 领域模型映射 ──
    //
    // 🔥 2026-10-14 起**下沉到 Core 的 `AgentConfigMapper`**：客户机的「本机配置」页
    // 要以同一份形状读写同一份配置，映射再各自留一份就会变成两份 ——
    // 而两份映射最容易出的问题（"保存时少映射一个字段"）在编译期完全看不出来，
    // 现场表现为"改了不生效"。留在这里的三个调用点已经改成直接调那个。

    private async Task RefreshAsync(CancellationToken cancellationToken)
        => await _registry.RefreshAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 构造写操作的响应载荷。
    /// <para>
    /// ⚠️ <b>这里必须返回 DTO，而不是 <c>IActionResult</c></b>。曾经写成
    /// <c>IActionResult Mutation(...) =&gt; Ok(new AgentMutationResult(...))</c>，
    /// 调用方再 <c>return Ok(Mutation(...))</c> —— 于是外层又包了一层 <c>ObjectResult</c>，
    /// 序列化出来是 <c>{"value":{...},"formatters":[],"statusCode":200}</c> 这种 MVC 内部结构。
    /// 前端读 <c>result.ok</c> 拿到 <c>undefined</c>，按钮点了"没反应"却不报错 ——
    /// 由 <c>deploy/smoke/agent-smoke.py</c> 逮住。
    /// 交给调用方决定 200/409/500，这里只管载荷。
    /// </para>
    /// <para>
    /// M4.5 起变成异步：列表要带上每台的配置版本，而那是要查库的
    /// （<see cref="AgentConfigService.ListVersionsAsync"/> 会顺手把内存缓存烤热）。
    /// </para>
    /// </summary>
    private async Task<AgentMutationResult> MutationAsync(
        bool ok,
        string status,
        string message,
        CancellationToken cancellationToken)
        => new(ok, status, message, await BuildListAsync(cancellationToken).ConfigureAwait(false));

    private async Task<AgentListDto> BuildListAsync(CancellationToken cancellationToken)
    {
        var now = BeijingTime.Now;
        var options = _options.Value.Agent;
        var versions = await _configs.ListVersionsAsync(cancellationToken).ConfigureAwait(false);

        var items = _registry.Hosts
            .Select(h => _registry.ToDto(h, options, now) with
            {
                ConfigVersion = versions.TryGetValue(h.Id, out var v) ? v : 0,
            })
            .OrderBy(x => x.State == AgentStates.Online ? 0 : x.State == AgentStates.Unstable ? 1 : 2)
            .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new AgentListDto(
            items,
            items.Count,
            items.Count(x => x.State == AgentStates.Online),
            items.Count(x => x.State == AgentStates.Unstable),
            items.Count(x => x.State == AgentStates.Offline || x.State == AgentStates.Never));
    }
}
