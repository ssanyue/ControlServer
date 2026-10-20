using ControlServer.Contracts;
using ControlServer.Core.Services;
using ControlServer.Server.Options;
using ControlServer.Server.Security;
using ControlServer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.Hubs;

/// <summary>
/// 客户端 Agent 的长连接通道（M4.1，<c>/hubs/agent</c>）。
/// <para>
/// <b>方向是"客户端连出来"</b>：这条 WebSocket 由 Agent 主动建立，服务端借它下发指令。
/// 客户端因此<b>一个入站端口都不开</b> —— Windows 防火墙、NAT、"客户机 IP 会变"
/// 三类问题一并消失。
/// </para>
/// <para>
/// <b>心跳不写库</b>：只更新内存态（<c>AgentHostRegistry</c>），由后台任务每 60 秒成批落盘。
/// 20 台机器 × 每 10 秒一次 = 2 次写/秒，直接写 SQLite 会把"改个备注"这种真正的写操作挤在后面。
/// </para>
/// </summary>
[Authorize(AuthenticationSchemes = AgentAuthentication.Scheme)]
public sealed class AgentHub : Hub
{
    private readonly AgentHostRegistry _registry;
    private readonly AgentTelemetryService _telemetry;
    private readonly AgentConfigService _configs;
    private readonly AgentEncoderCapabilityService _encoderCapability;
    private readonly AgentScreenDirectory _screens;
    private readonly RemoteSessionRegistry _remoteSessions;
    private readonly LogService _log;
    private readonly IOptions<ServerOptions> _options;
    private readonly ServerRuntimeInfo _runtime;

    public AgentHub(
        AgentHostRegistry registry,
        AgentTelemetryService telemetry,
        AgentConfigService configs,
        AgentEncoderCapabilityService encoderCapability,
        AgentScreenDirectory screens,
        RemoteSessionRegistry remoteSessions,
        LogService log,
        IOptions<ServerOptions> options,
        ServerRuntimeInfo runtime)
    {
        _registry = registry;
        _telemetry = telemetry;
        _configs = configs;
        _encoderCapability = encoderCapability;
        _screens = screens;
        _remoteSessions = remoteSessions;
        _log = log;
        _options = options;
        _runtime = runtime;
    }

    /// <summary>连接建立：登记连接、下发心跳间隔。</summary>
    public override async Task OnConnectedAsync()
    {
        var agentId = AgentKeyAuthenticationHandler.ReadAgentId(Context.User);

        if (agentId is null)
        {
            // 认证通过却没有 Id 声明 = 代码不一致（给 scheme 加声明时漏了）。
            // 这种连接留着只会让后续消息全部找不到归属，直接掐掉并留痕。
            _log.Error("AgentHub", $"客户端连接缺少身份声明，已拒绝（连接 {Context.ConnectionId}）", null);
            Context.Abort();
            return;
        }

        var id = agentId.Value;
        _registry.MarkConnected(id, Context.ConnectionId);

        var options = _options.Value.Agent;
        var host = _registry.Hosts.FirstOrDefault(h => h.Id == id);

        // 遥测节奏随 Welcome 一起下发：客户端不必在本地维护一份配置，
        // 服务端改一个数、客户端重连即生效（与心跳间隔同一套路）。
        var telemetry = options.Telemetry;
        var telemetrySettings = telemetry.Enabled
            ? new AgentTelemetrySettingsDto(
                true,
                telemetry.MetricsSeconds,
                telemetry.PowerEventPollSeconds,
                telemetry.InventoryHours,
                telemetry.PowerEventBackfillDays)
            : null;

        // 配置版本随 Welcome 下发 —— 这是"推送丢了也能自愈"的关键：
        // 客户机拿它和自己已应用的版本一比，落后就去拉。否则一次丢失的
        // ConfigChanged 会让那台机器一直跑旧配置，直到下次重启。
        var configVersion = await _configs.GetVersionAsync(id, Context.ConnectionAborted).ConfigureAwait(false);

        // ── 硬件清单：服务端这边有没有？（2026-10-15）──
        //
        // 🔥 这一句修的是一个真 BUG："客户机不上报硬件清单"，且**只在重新注册之后出现**。
        //    清单排期由客户端**本地持久化**的"上次上报时刻"决定（周期 6 小时），
        //    而记录被删（管理员移除 / 重新注册换了新 Id）之后服务端是一张白纸 ——
        //    客户端不知道，于是最长 6 小时不会再报。现在由这里如实告诉它。
        var needsInventory = !await _telemetry
            .HasInventoryAsync(id, Context.ConnectionAborted)
            .ConfigureAwait(false);

        await Clients.Caller.SendAsync(AgentHubMethods.Welcome, new AgentWelcomeDto(
            id,
            host?.DisplayName ?? $"Agent-{id}",
            _runtime.Version,
            options.HeartbeatSeconds,
            configVersion,
            BeijingTime.Now,
            telemetrySettings,
            needsInventory)).ConfigureAwait(false);

        _log.Info("AgentHub", $"客户端已建立长连接：{host?.DisplayName ?? id.ToString()}（连接 {Context.ConnectionId}）");

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 心跳。客户端按服务端下发（或应答里回传）的间隔调用。
    /// </summary>
    /// <param name="agentVersion">客户端当前版本（顺带上报，便于页面显示"某台机器还是旧版"）。</param>
    /// <remarks>
    /// 🔥 <b>本方法的参数个数必须保持 1 —— 不许"顺手加个可选参数"</b>。
    ///
    /// <para>实测（<c>.tools/hub-arity-probe</c>，SignalR 8.0.10）：</para>
    /// <list type="bullet">
    ///   <item>客户端传 1 个实参 → 服务端方法有 2 个参数（第 2 个带 C# 默认值）⇒ <b>HubException，调用失败</b>；</item>
    ///   <item>客户端传 2 个实参 → 服务端方法只有 1 个参数 ⇒ <b>同样失败</b>。</item>
    /// </list>
    /// <para>
    /// ⇔ <b>SignalR 的实参个数与签名必须严格一致，C# 的默认值参数在这里不起任何作用</b>。
    /// 所以"给心跳挂上 UDP 状态"绝不能靠加参数或换成一个 DTO ——
    /// 那会让<b>还在跑旧版的客户机整条心跳失败</b>，表现成"几十台机器的在线状态一起变灰"，
    /// 而它们其实一切正常。新增信息走<b>独立的方法</b>（<see cref="ReportUdpChannel"/>），
    /// 两个方向的兼容性都能保住。
    /// </para>
    /// <para>这条纪律由 <c>HubContractArityTests</c> 用反射钉住 —— 别只靠这段注释。</para>
    /// </remarks>
    public async Task<AgentHeartbeatAckDto> Heartbeat(string? agentVersion)
    {
        var agentId = AgentKeyAuthenticationHandler.ReadAgentId(Context.User);
        var configVersion = 0;

        if (agentId is { } id)
        {
            var host = _registry.Hosts.FirstOrDefault(h => h.Id == id);

            // 🔥 记录**已经被移除**（管理员在网页上点「移除」，见 AgentsController.Delete）——
            //    必须**强制断开**，不能当成一次正常心跳（2026-10-14 修）。
            //
            // 为什么不能放过：`MarkSeen` 会给一个**已不存在的 Id** 建内存态并照常返回 ack，
            // 于是客户端以为自己还在线、长连接变成僵尸，而服务端早就把它忘了 ——
            // 现场表现就是"移除之后再也加不回来"（客户端连"我被移除了"都不知道，
            // 自然不会重新注册）。
            //
            // 为什么 `_registry.Hosts` 查不到就等于"被移除"：那份快照由
            // `AgentHostRegistry.RefreshAsync` 从库里装载（启动时一次 + 每 60 秒一次，
            // 且刷新失败时**沿用旧快照**），而注册成功那条路径会立刻 `ApplyChange` 补进去 ——
            // 所以"库里有的机器不在快照里"只可能是刚被删掉的那台。
            if (host is null)
            {
                _log.Warn("AgentHub", $"心跳来自一台已被移除的客户机，强制断开（Id={id}）—— 客户端应当重新注册。");
                Context.Abort();
            }
            else if (host.IsBlocked)
            {
                // 拉黑复核：认证只在**建连那一刻**做过一次，而管理员可能在此之后把它拉黑了。
                // 不复核的话，那条已经建立的长连接会一直活着 —— 表现为"拉黑了但它还在线"，
                // 而"拉黑"的语义恰恰是"不再与它连接、立刻断开"。
                _log.Warn("AgentHub", $"心跳发现该客户端已被拉黑，强制断开（Id={id}）");
                Context.Abort();
            }
            else
            {
                _registry.MarkSeen(id, Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString(), agentVersion);
                configVersion = await _configs.GetVersionAsync(id, Context.ConnectionAborted).ConfigureAwait(false);
            }
        }

        return new AgentHeartbeatAckDto(
            BeijingTime.Now,
            _options.Value.Agent.HeartbeatSeconds,
            configVersion);
    }

    /// <summary>
    /// 客户机上报它<b>此刻真实的</b> UDP 指令通道状态（2026-10-14）。
    ///
    /// <para><b>为什么单开一条消息，而不是搭心跳那趟车</b>：Hub 的实参个数必须与签名严格一致
    /// （见 <see cref="Heartbeat"/> 的备注，有实测证据），所以"给心跳加一个字段"这条路走不通。
    /// 代价只是每台机器每个心跳周期多一条很小的消息 —— 与"旧客户端心跳集体失败"相比不值一提。</para>
    ///
    /// <para><b>它解决什么</b>：中心判"这台机器能不能收 UDP 指令"时，在此之前只能拿
    /// 「我下发过的配置里写着 Enabled=true」当依据 —— 而配置写着启用<b>不等于它在听</b>
    /// （本机可能停用了任务下发、端口可能绑不上、配置可能还没应用）。于是
    /// "中心说已发出、机器毫无反应"成了最难查的一类现场问题。
    /// 收到这条之后，中心就拿到了<b>客户机自己说的真相</b>。</para>
    ///
    /// <para>⚠️ <b>旧服务端不认识这个方法</b>（新客户端先升级的场景）：客户端那边调用失败会被
    /// 就地吞掉（与遥测上报同一条纪律），<b>绝不该因为它升级成断线重连</b>。</para>
    ///
    /// <para>⚠️ <paramref name="state"/> 为 null = 载荷为空，此时<b>什么都不改</b>（保留上一次的值）。</para>
    /// </summary>
    public AgentIngestAckDto ReportUdpChannel(AgentUdpChannelStateDto? state)
    {
        var guard = RequireAgent(out var id);

        if (guard is not null) return guard;

        if (state is null) return AgentIngestAckDto.Rejected("empty", "载荷为空。");

        _registry.SetUdpState(id, state);

        return AgentIngestAckDto.Success();
    }

    /// <summary>
    /// 客户机回报「配置已应用」（M4.5）。
    /// <para>
    /// 客户端据此推进自己的"已应用版本"水位；服务端据此在页面上显示
    /// "已下发 v5 / 已应用 v4"。<b>两者不一致本身就是重要信息</b> ——
    /// 没有它，"改了配置但机器没生效"与"保存失败"在页面上长得一模一样。
    /// </para>
    /// <para>
    /// 注意 <paramref name="applied"/> 里的 <c>Version</c> 是<b>成功应用</b>的版本，
    /// 不是"收到的版本"：解析失败 / 应用失败时客户端不该推进它，否则服务端会以为已经生效。
    /// </para>
    /// </summary>
    public async Task<AgentIngestAckDto> ReportConfigApplied(AgentConfigAppliedDto? applied)
    {
        var guard = RequireAgent(out var id);

        if (guard is not null) return guard;

        if (applied is null) return AgentIngestAckDto.Rejected("empty", "载荷为空。");

        await _configs.AcceptAppliedAsync(id, applied, Context.ConnectionAborted).ConfigureAwait(false);

        return AgentIngestAckDto.Success();
    }

    /// <summary>
    /// 收下客户机<b>自己改的那一版</b>配置（2026-10-14 的双向同步）。
    ///
    /// <para><b>为什么走 Hub 而不是给 Agent 开一个 HTTP <c>PUT</c></b>：</para>
    /// <list type="bullet">
    ///   <item>归属校验在这里是<b>结构性</b>的 —— 身份就是 <c>Context.User</c> 里的 AgentId，
    ///         客户机<b>没法</b>推别人的配置，不必写"这个 id 是不是你自己的"那种判断；</item>
    ///   <item>不必为"机器写配置"单开一条授权策略、也不必给 <c>LicenseGate</c> 分类表加行
    ///         （那条路每漏一行就是 fail-closed 的守门用例失败）；</item>
    ///   <item>客户机本来就要靠这条连接上报状态，推配置是同一个方向上的动作。</item>
    /// </list>
    ///
    /// <para>⚠️ 离线机器推不了 —— 也不需要：它把"本机改过、还没推"记在自己身上，
    /// 上线后由心跳循环补推（见 <c>AgentRuntime</c>）。</para>
    /// </summary>
    public async Task<AgentConfigSaveResult> PushConfig(AgentConfigPushDto? pushed)
    {
        var guard = RequireAgent(out var id);

        if (guard is not null) return new AgentConfigSaveResult(false, guard.Status, guard.Message, null);

        return await _configs.PushFromAgentAsync(id, pushed, Context.ConnectionAborted).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════
    // 遥测上报（M4.2，客户端 → 服务端）
    //
    // ⚠️ 这三个方法**都要返回应答**，客户端据它决定"数据算不算送达"。
    //    尤其开关机事件：服务端明确说没收，客户端就必须不推进水位、下次重发 ——
    //    否则那几条记录永远消失（而"这台机器上个月关过几次机"是拿不到第二遍的数据）。
    // ═══════════════════════════════════════════════════════════

    /// <summary>上报一批实时指标（默认 5 秒一批；断线期间攒下的会在重连后一并补发）。</summary>
    public Task<AgentIngestAckDto> ReportMetrics(AgentMetricsBatchDto? batch)
    {
        var guard = RequireAgent(out var id);

        if (guard is not null) return Task.FromResult(guard);

        return Task.FromResult(_telemetry.AcceptMetrics(id, batch));
    }

    /// <summary>上报开关机 / 蓝屏事件（回溯补扫 + 增量）。</summary>
    public async Task<AgentIngestAckDto> ReportPowerEvents(AgentPowerEventBatchDto? batch)
    {
        var guard = RequireAgent(out var id);

        if (guard is not null) return guard;

        return await _telemetry.AcceptPowerEventsAsync(id, batch).ConfigureAwait(false);
    }

    /// <summary>上报硬件清单（启动时 + 每 6 小时一次）。</summary>
    public async Task<AgentIngestAckDto> ReportInventory(AgentInventoryDto? inventory)
    {
        var guard = RequireAgent(out var id);

        if (guard is not null) return guard;

        return await _telemetry.AcceptInventoryAsync(id, inventory).ConfigureAwait(false);
    }

    /// <summary>
    /// 客户机上报<b>它的视频硬编码能力</b>（M5.0 第二批）。载荷 <see cref="AgentEncoderCapabilityDto"/>。
    ///
    /// <para>
    /// 🔥 <b>为什么必须由客户机报、而不是中心探</b>：要编码的是<b>被控端</b>。
    /// 中心那台机器有没有 NVENC 与远控能不能跑毫无关系。这份结论是整个 M5 的地基
    /// （8K 目标、编码格式选择、要不要开分帧编码都建在它上面）。
    /// </para>
    ///
    /// <para>
    /// <b>为什么单开一条消息</b>：与 <see cref="ReportUdpChannel"/> 同一条纪律 ——
    /// SignalR 的实参个数必须与签名严格一致，搭心跳（或 Welcome）那趟车会让旧客户端整体失效。
    /// 代价是每台机器每次连接多一条很小的消息。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>旧服务端不认识它</b>（新客户端先升级）：客户端会就地吞掉这个失败，
    /// <b>绝不该让它升级成"断线重连"</b>（与遥测上报同一条纪律）。
    /// </para>
    /// </summary>
    public AgentIngestAckDto ReportEncoderCapability(AgentEncoderCapabilityDto? report)
    {
        var guard = RequireAgent(out var id);

        if (guard is not null) return guard;

        return _encoderCapability.Accept(id, report);
    }

    /// <summary>
    /// 客户机上报<b>它的屏清单</b>（M5.5 多屏选择）。载荷 <see cref="AgentScreensDto"/>。
    ///
    /// <para>
    /// 🔥 <b>为什么这条上行必须存在</b>：屏清单只有用户会话里的托盘数得出来，
    /// 而中心向客户机下发的指令是 fire-and-forget（回执止步于服务进程）。
    /// 没有这条上行，"给我看看你有几块屏"就永远等不到答案。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 与能力上报同一条纪律：旧服务端不认识它时，客户端就地吞掉失败，不得升级成断线。
    /// </para>
    /// </summary>
    public AgentIngestAckDto ReportScreens(AgentScreensDto? report)
    {
        var guard = RequireAgent(out var id);

        if (guard is not null) return guard;

        return _screens.Accept(id, report);
    }

    /// <summary>
    /// 客户机送回<b>一段被控机的剪贴板文本</b>（M5.5）。载荷 <see cref="RemoteClipboardDto"/>。
    ///
    /// <para>
    /// 🔥 <b>按连接身份反查会话</b>，不要托盘填 sessionId：它在会话刚结束那一瞬间
    /// 会指向一个已经不存在的 Id，而中心只能把内容丢掉 —— 页面看到的是"点了没反应"，
    /// 真因却在托盘那边。
    /// </para>
    /// </summary>
    public AgentIngestAckDto ReportClipboard(RemoteClipboardDto? clipboard)
    {
        var guard = RequireAgent(out var id);

        if (guard is not null) return guard;

        if (clipboard is null) return AgentIngestAckDto.Rejected("empty", "载荷为空。");

        // ⚠️ 超限的文本**不进缓存、也不广播**：托盘那边已经保证不超过上限，
        //    这里再判一次是因为"长度上限"是**中心**的接收约束 —— 一个坏掉的/被改过的
        //    客户端可以发一条 100 MB 的"剪贴板"，而它会把每个观看页的标签页撑爆。
        if (clipboard.Text is { Length: > RemoteClipboardLimits.MaxChars })
        {
            return AgentIngestAckDto.Rejected(
                "clipboard-too-large",
                $"剪贴板文本超过 {RemoteClipboardLimits.MaxChars} 字符上限，已丢弃。");
        }

        return _remoteSessions.RecordClipboard(id, clipboard)
            ? AgentIngestAckDto.Success()
            : AgentIngestAckDto.Rejected(
                "no-session",
                $"机器 {id} 此刻没有在用的远控会话，剪贴板内容无处可放。");
    }

    // ═══════════════════════════════════════════════════════════
    // 远程桌面（M5.1，客户端 → 服务端）
    //
    // ⚠️ 这两条与上面的上报**不是一类东西**，别照抄它们的纪律：
    //    · 其它上报"坏了就忽略"（顶多丢几条遥测）；
    //    · 推帧失败**必须让被控端停下来** —— 继续编只会白烧它的 CPU。
    //    所以下面两条的应答是被控端唯一的刹车信号，`Rejected` 是有意义的返回值。
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 收一帧远控画面（M5.1）。载荷 <see cref="RemoteStreamFrame"/>。
    ///
    /// <para>
    /// 🔥 <b>这是整个系统里唯一的高频大消息</b>（每帧几十到几百 KB、每秒十几帧），
    /// 所以它<b>不 async、不 await</b>：方法体只做"归属校验 + 计数 + 塞进转发队列"，
    /// 而那三件事加起来是几次字典查找和一次永不阻塞的 <c>TryWrite</c>。
    /// 一旦这里出现 <c>await</c>（比如"顺手把帧也写进日志"），
    /// 被控端的推流速度就会被中心的 IO 拖住 —— 而且拖的是<b>被控机</b>。
    /// </para>
    ///
    /// <para>
    /// <b>返回值是刹车信号</b>：<c>Rejected</c> 表示"这个会话已经没了"或"不属于你"，
    /// 被控端收到就会<b>当场停掉这路流</b>。所以拒绝必须是真的拒绝 ——
    /// 一个误报的 <c>Rejected</c> 会让画面毫无理由地断掉。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>旧服务端不认识它</b>（新客户端先升级）：调用会抛。
    /// 客户端必须<b>就地把流停下来</b>，而不是像遥测那样悄悄忽略 ——
    /// 否则会一直"编一帧、丢一帧"地空烧 CPU，而中心那边什么都看不到。
    /// </para>
    /// </summary>
    public AgentIngestAckDto PushRemoteFrame(RemoteStreamFrame? frame)
    {
        var guard = RequireAgent(out var id);

        if (guard is not null) return guard;

        return _remoteSessions.AcceptFrame(id, frame);
    }

    /// <summary>
    /// <b>收一帧远控画面的「一片」</b>（M5.4-c① 的分片）。载荷 <see cref="RemoteStreamFrameChunk"/>。
    ///
    /// <para>
    /// 🔥 <b>为什么是新的一条消息，而不是给 <see cref="PushRemoteFrame"/> 加一个参数</b>：
    /// SignalR 的实参个数必须与签名<b>严格一致</b>（见 <see cref="Heartbeat"/> 的实测备注），
    /// 加参数 = <b>旧中心上所有新客户端的每一帧</b>都失败。
    /// 分片的兼容性因此是"<b>惰性</b>"的：只有装不下一条消息的帧才走这里，
    /// 2160×1440 与 4K 仍然整帧走老路 ⇒ <b>旧中心上它们照常工作</b>。
    /// </para>
    ///
    /// <para>
    /// 🔥 <b>拼片在中心完成，而不是透传给浏览器</b>：中心的转发队列是"容量 1 + 丢最旧"，
    /// 而它丢弃的单位是<b>一条消息</b> —— 端到端分片时它会丢掉<b>同一帧的中间一片</b>，
    /// 前端于是拼出一帧<b>长度对、内容错</b>的码流（花屏），而<b>没有任何一层会报错</b>。
    /// 终止在中心则跳 4/5/6 一行都不用改。见 <c>RemoteFrameReassembler</c>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>与 <see cref="PushRemoteFrame"/> 一样不 async、不 await</b>：方法体只做
    /// "归属校验 + 拼片 + 入队"，而<b>拼片那一步刻意在账本锁之外</b>
    /// （十几 MB 的拷贝压在 <c>_gate</c> 里 = 让所有会话的所有帧去等它）。
    /// 这里一旦出现"顺手把片写进日志"那种 <c>await</c>，被控端的推流速度就会被中心的 IO 拖住 ——
    /// 而且拖的是<b>被控机</b>。
    /// </para>
    ///
    /// <para>
    /// <b>返回值是两条信息的合成</b>（见 <c>AgentIngestAckDto</c>）：
    /// <c>Ok = false</c> ⇒ 会话没了 / 不属于你 ⇒ 被控端<b>当场停掉这路流</b>；
    /// <c>NeedKeyframe = true</c> ⇒ 中心刚丢了一帧（缺片）⇒ 被控端<b>下一个帧编成 IDR</b>，
    /// 把恢复代价从"最多一个 GOP"（5 秒花屏）压到"下一帧"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>旧服务端不认识它</b>（新客户端先升级）：调用会抛。
    /// 客户端必须与推整帧那条同一条纪律 —— <b>就地把这路流停下来</b>，
    /// 而不是像遥测那样悄悄忽略（那会变成"编一帧、丢一帧"地空烧 CPU）。
    /// </para>
    /// </summary>
    public AgentIngestAckDto PushRemoteFrameChunk(RemoteStreamFrameChunk? chunk)
    {
        var guard = RequireAgent(out var id);

        if (guard is not null) return guard;

        return _remoteSessions.AcceptFrameChunk(id, chunk);
    }

    /// <summary>
    /// 收一段远控音频（M5.3）。载荷 <see cref="RemoteAudioFrame"/>。
    ///
    /// <para>
    /// 🔥 <b>它必须与 <see cref="PushRemoteFrame"/> 分成两个方法，不能靠载荷里的字段分叉</b>：
    /// 两者唯一的共同点是"都要校验归属"，其余全部相反 ——
    /// 速率差两个量级（视频 10–30 fps，音频固定 50 包/秒）、
    /// 丢帧策略相反（视频丢了就丢了，音频丢一包就是可听见的断点）、
    /// 中心的转发队列容量也相反（1 vs 50）。
    /// 而且 <b>SignalR 的实参个数必须与签名严格一致</b>：合并成一个多态载荷，
    /// 就等于把"两个方向不同的东西"塞进一个方法名里，
    /// 下一个改它的人必然会在某个分支上做错。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 与 <see cref="PushRemoteFrame"/> 一样<b>不 async、不 await</b>：
    /// 方法体只做"归属校验 + 计数 + 塞进队列"，那是一次字典查找和一次永不阻塞的
    /// <c>TryWrite</c>。这里一旦出现 <c>await</c>，50 包/秒 的采集节奏
    /// 就会被中心的广播 IO 拖住 —— 而拖的是<b>被控机</b>。
    /// </para>
    ///
    /// <para>
    /// <b>返回值同样是刹车信号</b>：<c>Rejected</c> 表示"会话已没了"或"不属于你"，
    /// 被控端收到就会当场停掉这条音频通道。误报 <c>Rejected</c> 会让声音毫无理由地断掉。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 旧中心不认识它（新客户机先升级）时调用会抛：客户端必须<b>就地把音频通道收掉</b>
    /// （表现是"音频=不可用"，原因里带着异常信息），
    /// <b>不能像遥测那样悄悄忽略</b> —— 那会变成 50 包/秒 的"采一段、丢一段"空烧 CPU。
    /// </para>
    /// </summary>
    public AgentIngestAckDto PushRemoteAudio(RemoteAudioFrame? frame)
    {
        var guard = RequireAgent(out var id);

        if (guard is not null) return guard;

        return _remoteSessions.AcceptAudio(id, frame);
    }

    /// <summary>
    /// 收一条远控会话状态上报（M5.1）。载荷 <see cref="AgentRemoteSessionStateDto"/>。
    ///
    /// <para>
    /// 🔥 <b>它必须与 <see cref="PushRemoteFrame"/> 分开，而且必须可靠</b>：
    /// 被控端"起了但没起来"（没人登录 / 托盘没跑 / 没有可抓的桌面）的时候
    /// <b>一帧都不会有</b>。只靠"没有帧"这个现象，中心分不清
    /// "正在连接"与"已经失败" —— 而这两者对应现场的下一步动作<b>完全相反</b>
    /// （前者等着，后者去被控机上登录）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>它也不 async</b>：库里的广播是"用后即忘"的（见 <c>RemoteSessionRegistry</c>）——
    /// 让一条状态上报去等所有观看端收完，等于把<b>观看端的网速</b>接到了
    /// 被控端的状态上报上，而那正是本方案从头到尾在避免的方向。
    /// 它低频、每条只有几百字节，不上 async 省不下什么，但也不该为了"看起来更现代"而上。
    /// </para>
    /// </summary>
    public AgentIngestAckDto ReportRemoteSession(AgentRemoteSessionStateDto? state)
    {
        var guard = RequireAgent(out var id);

        if (guard is not null) return guard;

        if (state is null) return AgentIngestAckDto.Rejected("empty", "载荷为空。");

        return _remoteSessions.AcceptAgentState(id, state);
    }

    /// <summary>连接断开：立刻把状态推向"离线计时起点"（硬断电时靠心跳超时兜底）。</summary>
    public override async Task OnDisconnectedAsync(Exception? exception)    {
        var agentId = AgentKeyAuthenticationHandler.ReadAgentId(Context.User);

        if (agentId is { } id)
        {
            _registry.MarkDisconnected(id, Context.ConnectionId);

            var host = _registry.Hosts.FirstOrDefault(h => h.Id == id);

            if (exception is not null)
            {
                _log.Warn("AgentHub", $"客户端连接异常断开：{host?.DisplayName ?? id.ToString()} —— {exception.Message}");
            }
            else
            {
                _log.Info("AgentHub", $"客户端连接已断开：{host?.DisplayName ?? id.ToString()}");
            }
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    /// <summary>
    /// 遥测上报的前置检查：拿到 AgentId，并复核"这台机器是不是已被拉黑"。
    /// <para>
    /// 返回非 null 表示应当直接把该值当作应答回给客户端（记录已被移除 / 已被拉黑）。
    /// </para>
    /// <para>
    /// 与心跳一样要复核拉黑：认证只在建连那一刻做过，而长连接可能挂着好几天，
    /// 期间管理员完全可能把它拉黑。不复核就会出现"拉黑了它还在往上传数据"。
    /// </para>
    /// </summary>
    private AgentIngestAckDto? RequireAgent(out int agentId)
    {
        var id = AgentKeyAuthenticationHandler.ReadAgentId(Context.User);

        if (id is null)
        {
            agentId = 0;
            return AgentIngestAckDto.Rejected("unauthenticated", "连接缺少身份声明。");
        }

        agentId = id.Value;

        // ⚠️ 必须先拷到局部变量：`out` 参数不能被 lambda 捕获（CS1628）。
        var resolved = agentId;

        var host = _registry.Hosts.FirstOrDefault(h => h.Id == resolved);

        // 🔥 记录已经被移除（2026-10-14 修，与 <see cref="Heartbeat"/> 同一件事）。
        //    上报类消息跑得比心跳更密（遥测 / 配置上行都是秒级），所以这里也要拦：
        //    否则在"被移除"到"下一次心跳把它掐掉"之间的那几秒里，
        //    中心会替一台**已经不存在的机器**写数据（那些行永远不会被读到）。
        if (host is null)
        {
            _log.Warn("AgentHub", $"收到一台已被移除的客户机的上报，强制断开（Id={resolved}）—— 它应当重新注册。");
            Context.Abort();

            return AgentIngestAckDto.Rejected("unregistered", "本机记录已被移除，请重新注册。");
        }

        if (host.IsBlocked)
        {
            _log.Warn("AgentHub", $"遥测上报发现该客户端已被拉黑，强制断开（Id={resolved}）");
            Context.Abort();

            return AgentIngestAckDto.Rejected("blocked", "该客户端已被管理员拉黑。");
        }

        return null;
    }
}
