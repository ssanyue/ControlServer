using ControlServer.Contracts;
using ControlServer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ControlServer.Server.Api;

/// <summary>
/// 远程桌面端点（M5.0 交付骨架 → M5.1 接入只读观看 → <b>M5.2 接入键鼠回传</b>）。
///
/// <para>
/// <b>已经在工作的</b>：建会话 / 查会话 / 结束会话 / 黑白切换 / 能力探测查询，
/// 以及 <b>M5.2 的输入回传与「允许控制」闸门</b>。
/// 码流本身与输入事件都<b>不走这里</b>：它们走 SignalR
/// （<c>/hubs/remote</c>，中心 → 浏览器推画面、浏览器 → 中心送输入），
/// 走 HTTP 一问一答既做不到 60 fps，也会把每个动作都变成一次完整的请求开销。
/// </para>
///
/// <para>
/// ⚠️ 本控制器里的 <see cref="Input"/> 与 <see cref="SetInputEnabled"/> 是给
/// <b>自动化脚本</b>留的入口（低频、离散的用法），它们与 Hub 那两条共用同一份服务逻辑。
/// </para>
///
/// <para>
/// <b>为什么整个控制器都要求 <see cref="Policies.AdminOnly"/></b>：
/// 决策 11 —— <b>非管理员连"只读观看"都不允许</b>。
/// 远控的价值点在这里是"看到别人屏幕"本身，不是"点得动别人屏幕"；
/// 所以连只读端点也不放宽（这与"在线表允许普通角色看"是两条不同的判据）。
/// </para>
///
/// <para>
/// ⚠️ 新增端点后<b>必须同步更新 <c>LicenseGate.cs</c> 的分类表</b>，
/// 否则 <c>LicenseGateTests.Every_api_endpoint_is_explicitly_classified</c> 会失败，
/// 且 fail-closed 会让它在生产表现为"这个端点 404"。
/// </para>
/// </summary>
[ApiController]
[Route("api/remote")]
[Authorize(Policy = Policies.AdminOnly)]
public sealed class RemoteController : ControllerBase
{
    private readonly AgentHostRegistry _hosts;
    private readonly AgentEncoderCapabilityService _capabilities;
    private readonly AgentScreenDirectory _screens;
    private readonly RemoteSessionRegistry _sessions;
    private readonly RemoteSessionService _service;

    public RemoteController(
        AgentHostRegistry hosts,
        AgentEncoderCapabilityService capabilities,
        AgentScreenDirectory screens,
        RemoteSessionRegistry sessions,
        RemoteSessionService service)
    {
        _hosts = hosts;
        _capabilities = capabilities;
        _screens = screens;
        _sessions = sessions;
        _service = service;
    }

    // ═══════════════════════════════════════════════════════════
    // 能力探测（只读）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 某台被控机的编码能力。
    /// <para>
    /// 三种状态在这里<b>必须分得清</b>，因为它们对应现场完全不同的下一步：
    /// </para>
    /// <list type="number">
    ///   <item><b>机器不在在线表</b> ⇒ 404（先去装 Agent / 查网络）；</item>
    ///   <item><b>在表但没上报过能力</b> ⇒ 200 + <c>probed=false</c>（功能进度问题，不是故障）；</item>
    ///   <item><b>上报过</b> ⇒ 200 + <c>probed=true</c> + 适配器明细。</item>
    /// </list>
    /// <para>
    /// ⚠️ 第 2 种<b>绝不能</b>显示成"该机器不支持远控" —— 那是一个凭空的否定结论。
    /// </para>
    /// </summary>
    [HttpGet("capabilities/{agentId:int}")]
    public IActionResult Capabilities(int agentId)
    {
        if (agentId <= 0)
        {
            return BadRequest(new RemoteNotAvailableDto(
                RemoteErrorCodes.AgentNotFound, "机器 ID 必须是正整数。"));
        }

        if (_hosts.Hosts.FirstOrDefault(h => h.Id == agentId) is null)
        {
            return NotFound(new RemoteNotAvailableDto(
                RemoteErrorCodes.AgentNotFound,
                $"在线表里没有 ID 为 {agentId} 的机器。"));
        }

        // ── 读了这份，就是"被控端自己说的它能不能编码"（M5.0 第二批接入）──
        //
        // 🔥 三种状态必须分得清（上面的注释说了两种，这里是第三种）：
        //   · 报过 ⇒ 200 + probed=true + 适配器明细；
        //   · 没报过 ⇒ 200 + probed=false —— **这不等于"这台机器不支持远控"**。
        if (_capabilities.TryGet(agentId) is { } reported) return Ok(reported);

        return Ok(new RemoteCapabilityDto(
            Probed: false,
            Adapters: Array.Empty<RemoteAdapterDto>(),
            SupportedCodecs: Array.Empty<string>(),
            RecommendedCodec: null,
            Summary: "该机器尚未上报编码能力 —— 客户机在建立长连接后会立刻报一次。"
                     + "注意：这不等于「该机器不支持远程桌面」。",
            ProbedAt: null));
    }

    // ═══════════════════════════════════════════════════════════
    // 会话
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 当前所有远程桌面会话。
    /// <para>
    /// ⚠️ 它读的是<b>内存账本</b>，因此"中心重启后为空"是正常的、也是正确的：
    /// 重启会断掉所有长连接，被控端的推流也随之停止
    /// （见 <c>RemoteSessionRegistry</c> 的类注释）。
    /// </para>
    /// </summary>
    [HttpGet("sessions")]
    public IActionResult Sessions() => Ok(_sessions.List());

    /// <summary>单个会话的详情。不存在则 404。</summary>
    [HttpGet("sessions/{sessionId}")]
    public IActionResult Session(string sessionId)
    {
        var dto = _sessions.TryGet(sessionId);

        return dto is null
            // 回 404 而不是 501：这是一个**按 ID 取资源**的端点，
            // 而该 ID 的会话此刻**确实不存在**，404 是准确的语义。
            ? NotFound(new RemoteNotAvailableDto(
                RemoteErrorCodes.SessionNotFound,
                $"不存在会话「{sessionId}」（可能已经结束）。"))
            : Ok(dto);
    }

    /// <summary>
    /// 建立会话 —— <b>让一台机器开始抓屏推流</b>。
    ///
    /// <para>
    /// 🔥 这是本控制器里<b>第一个真正"作用到别的机器"的写操作</b>，
    /// 已在 <c>LicenseGate</c> 归 <c>Gated</c>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>返回 200 不代表画面已经出来了</b>：中心下发指令用的是
    /// <c>SendAsync</c>，而客户机的指令处理是用后即忘的
    /// （见 <c>RemoteSessionService</c> 的类注释）。
    /// 所以这里回的是 <c>state=requested</c>；真正的结论
    /// （<c>streaming</c> / <c>failed</c> + 原因）由被控端上报后经
    /// <c>/hubs/remote</c> 的 <c>SessionChanged</c> 推给页面。
    /// </para>
    /// </summary>
    [HttpPost("sessions")]
    public async Task<IActionResult> CreateSession(
        [FromBody] RemoteSessionRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new RemoteNotAvailableDto(
                RemoteErrorCodes.AgentNotFound, "请求体为空。"));
        }

        // ⚠️ 与 `Capabilities` 保持同一个判据：**非法的机器 Id 是 400，不是 404**。
        //    404 的语义是"资源确实不存在"，而 `agentId = 0` 是一个连查都不必查的畸形请求；
        //    把两者混成一个码，现场会去在线表里找一个根本不存在的"0 号机器"。
        if (request.AgentId <= 0)
        {
            return BadRequest(new RemoteNotAvailableDto(
                RemoteErrorCodes.AgentNotFound, "机器 ID 必须是正整数。"));
        }

        var opened = await _service.StartAsync(request, cancellationToken).ConfigureAwait(false);

        return opened.Session is { } session ? Ok(session) : Fail(opened);
    }

    /// <summary>切换黑白（灰度）模式（决策 14）。</summary>
    /// <remarks>
    /// ⚠️ 它是<b>会话级</b>参数，不是观看端私有的：编码只做一路，
    /// 所以一个人切黑白，<b>所有</b>看这路流的人都会变灰。页面必须提示这一点。
    /// </remarks>
    [HttpPost("sessions/{sessionId}/grayscale")]
    public async Task<IActionResult> SetGrayscale(
        string sessionId,
        [FromBody] RemoteGrayscaleRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new RemoteNotAvailableDto(
                RemoteErrorCodes.BadRequest, "请求体为空。"));
        }

        // 先单独判"有没有这个会话"，好让 404 与"指令没送到"是两种答复 ——
        // 合并成一句"失败了"的话，现场分不清"是不是我点错了会话"。
        if (_sessions.TryGet(sessionId) is null)
        {
            return NotFound(new RemoteNotAvailableDto(
                RemoteErrorCodes.SessionNotFound,
                $"不存在会话「{sessionId}」（可能已经结束）。"));
        }

        var dto = await _service.SetGrayscaleAsync(sessionId, request.Enabled, cancellationToken)
            .ConfigureAwait(false);

        if (dto is null)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new RemoteNotAvailableDto(
                    RemoteErrorCodes.StartFailed,
                    $"黑白切换指令没能送达会话「{sessionId}」的被控端（它可能刚掉线）。"
                    + "画面没有改变。"));
        }

        return Ok(dto);
    }

    /// <summary>
    /// <b>改一路会话的质量档位</b>（M5.5 运行时自适应）。
    ///
    /// <para>
    /// 🔥 <b>为什么它是独立端点，而不是建会话请求里的一个字段</b>：建会话的字段只在<b>那一刻</b>
    /// 生效，而自适应要在会话跑着的时候改 —— 码率变化会让托盘重建编码器（≈0.3 s 空窗），
    /// 这件事必须能被页面上的一次明确动作触发，也必须能被日志单独看到。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 与黑白模式同一条缺口：返回成功只意味着"指令没抛异常"。
    /// 页面上的语义是"<b>请求值</b>"，真实效果看帧率/码率那一格。
    /// </para>
    /// </summary>
    [HttpPost("sessions/{sessionId}/quality")]
    public async Task<IActionResult> SetQuality(
        string sessionId,
        [FromBody] RemoteQualityRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new RemoteNotAvailableDto(
                RemoteErrorCodes.BadRequest, "请求体为空。"));
        }

        if (_sessions.TryGet(sessionId) is null)
        {
            return NotFound(new RemoteNotAvailableDto(
                RemoteErrorCodes.SessionNotFound,
                $"不存在会话「{sessionId}」（可能已经结束）。"));
        }

        // ⚠️ 不认得的档位名**当场拒绝**，不静默回落：
        //    静默回落的表现是"点了低画质、画面一点没变"，而用户会以为是降档没效果。
        if (request.Profile is not null && !RemoteQualityProfiles.IsKnown(request.Profile))
        {
            return BadRequest(new RemoteNotAvailableDto(
                RemoteErrorCodes.BadRequest,
                $"不认得的质量档位「{request.Profile}」（认得的只有 "
                + $"{RemoteQualityProfiles.Auto} / {RemoteQualityProfiles.Low} / "
                + $"{RemoteQualityProfiles.Balanced} / {RemoteQualityProfiles.High}）。"));
        }

        var dto = await _service
            .SetQualityAsync(sessionId, request.Profile, request.MaxFps, cancellationToken)
            .ConfigureAwait(false);

        return dto is null
            ? StatusCode(
                StatusCodes.Status502BadGateway,
                new RemoteNotAvailableDto(
                    RemoteErrorCodes.StartFailed,
                    $"质量切换指令没能送达会话「{sessionId}」的被控端（它可能刚掉线）。画面没有改变。"))
            : Ok(dto);
    }

    // ═══════════════════════════════════════════════════════════
    // ROI：只编视口那一块（M5.5 第 5 项）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>让这一路只编画面里的这一块</b>（M5.5 的 ROI 编码；宽或高为 0 = 关掉）。
    ///
    /// <para>
    /// 🔥 <b>它省的是"编码面积"</b>：8K 桌面在 1080p 视口下只需编 1/16 的像素量 ——
    /// 而"编整幅再缩小显示"会把这份开销原样付掉。现场那种"8K 只有 30 fps"的场景，
    /// 这是唯一能在不换硬件的前提下把帧率拉上一个量级的办法。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>每次调用都可能触发一次编码器重建</b>（≈0.3 秒空窗、首帧必关键帧）。
    /// <b>去抖是调用方的事</b>（页面：滚动停止 300 ms 才发）。
    /// 不在这里去抖，是因为"等多久算停"与页面上的滚动体验绑在一起 ——
    /// 两处各去抖一次会变成 600 ms，用户会觉得"滚完还卡一下"。
    /// </para>
    /// </summary>
    [HttpPost("sessions/{sessionId}/viewport")]
    public async Task<IActionResult> SetViewport(
        string sessionId,
        [FromBody] RemoteViewportRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new RemoteNotAvailableDto(
                RemoteErrorCodes.BadRequest, "请求体为空。"));
        }

        if (_sessions.TryGet(sessionId) is null)
        {
            return NotFound(new RemoteNotAvailableDto(
                RemoteErrorCodes.SessionNotFound,
                $"不存在会话「{sessionId}」（可能已经结束）。"));
        }

        // ⚠️ 明显的错值**当场拒绝**：负数坐标、1 像素宽的视口都不是"状态不允许"，
        //    而是**请求本身写错了**。放过去只会让被控端夹出一个用户没要求的区域。
        if (request.Width != 0 || request.Height != 0)
        {
            if (request.X < 0 || request.Y < 0 || request.Width < 2 || request.Height < 2)
            {
                return BadRequest(new RemoteNotAvailableDto(
                    RemoteErrorCodes.BadRequest,
                    $"视口 {request.X},{request.Y} {request.Width}×{request.Height} 不合法："
                    + "坐标不能为负，宽高至少 2（NV12 的最小可编单元）；"
                    + "想关掉 ROI 就把宽高都填 0。"));
            }
        }

        var dto = await _service
            .SetViewportAsync(sessionId, request.X, request.Y, request.Width, request.Height, cancellationToken)
            .ConfigureAwait(false);

        return dto is null
            ? StatusCode(
                StatusCodes.Status502BadGateway,
                new RemoteNotAvailableDto(
                    RemoteErrorCodes.StartFailed,
                    $"视口切换指令没能送达会话「{sessionId}」的被控端（它可能刚掉线）。画面没有改变。"))
            : Ok(dto);
    }

    // ═══════════════════════════════════════════════════════════
    // 多屏（M5.5）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>某台被控机有哪些屏可抓</b>（M5.5）。
    ///
    /// <para>
    /// 🔥 <b>它是"问一次、等一秒、读缓存"的形态</b>，不是一问一答：
    /// 中心 → 客户机的指令是 fire-and-forget（回执止步于服务进程），
    /// 屏清单经 <c>ReportScreens</c> 上行回来再进缓存。
    /// 所以这个端点<b>永远先返回</b>（<c>pending=true</c> 时让页面过一秒再问一次），
    /// 而不是把 HTTP 请求挂在那儿等一条可能永远不来的上行。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>三条状态必须分得清</b>：
    /// ①<c>pending=true</c> = 正在问；②<c>screens=[]</c> 且不 pending = <b>那台机器真的没有显示器</b>
    /// （没人登录）；③有列表 = 正常。把①画成②，现场会去查显示器插没插。
    /// </para>
    /// </summary>
    [HttpGet("screens/{agentId:int}")]
    public async Task<IActionResult> Screens(
        int agentId,
        [FromQuery] bool refresh,
        CancellationToken cancellationToken)
    {
        if (agentId <= 0)
        {
            return BadRequest(new RemoteNotAvailableDto(
                RemoteErrorCodes.AgentNotFound, "机器 ID 必须是正整数。"));
        }

        if (_hosts.Hosts.FirstOrDefault(h => h.Id == agentId) is null)
        {
            return NotFound(new RemoteNotAvailableDto(
                RemoteErrorCodes.AgentNotFound,
                $"在线表里没有 ID 为 {agentId} 的机器。"));
        }

        var cached = _screens.TryGet(agentId);

        // 🔥 什么时候问：从没问过 / 页面明确要求刷新 / 缓存太旧。
        //    ⚠️ 缓存太旧也照样返回旧列表（只是标 pending），不能当空 ——
        //    一次网络抖动让屏选择框变空，用户会以为"这台机器没屏了"。
        var stale = cached is null || !AgentScreenDirectory.IsFresh(cached);

        var pending = false;

        if (refresh || stale)
        {
            pending = await _service
                .RequestScreensAsync(agentId, cancellationToken)
                .ConfigureAwait(false);

            if (!pending)
            {
                return StatusCode(
                    StatusCodes.Status502BadGateway,
                    new RemoteNotAvailableDto(
                        RemoteErrorCodes.AgentOffline,
                        $"机器 #{agentId} 此刻没有长连接，问不到屏清单。"));
            }
        }

        return Ok(new RemoteScreensDto(
            cached?.Screens ?? Array.Empty<RemoteScreenDto>(),
            Pending: pending,
            Note: pending
                ? "正在向客户机索取屏清单（约 1 秒），请稍后再读一次。"
                : cached?.Note,
            ReportedAt: cached?.ReportedAt));
    }

    // ═══════════════════════════════════════════════════════════
    // 剪贴板（M5.5）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>读被控机的剪贴板</b>（M5.5）。下发指令后立刻返回，文本经
    /// <c>ClipboardChanged</c> 推到观看页。
    /// </summary>
    /// <remarks>
    /// 🔥 <b>为什么是 POST</b>：它对被控机是一次真实动作（打开剪贴板读一次），
    /// 而"某个进程一直开着剪贴板"在 Windows 上会让别的程序粘贴失败 ——
    /// 这种事不该被当成可缓存、可预取、可被浏览器/代理重放的 GET。
    /// </remarks>
    [HttpPost("sessions/{sessionId}/clipboard/read")]
    public async Task<IActionResult> ReadClipboard(string sessionId, CancellationToken cancellationToken)
    {
        if (_sessions.TryGet(sessionId) is null)
        {
            return NotFound(new RemoteNotAvailableDto(
                RemoteErrorCodes.SessionNotFound,
                $"不存在会话「{sessionId}」（可能已经结束）。"));
        }

        var sent = await _service.RequestClipboardAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (!sent)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new RemoteNotAvailableDto(
                    RemoteErrorCodes.AgentOffline,
                    "读取指令没能送达被控端（它可能刚掉线）。"));
        }

        // 先把上一次读到的给出去（可能有），页面随后会被 ClipboardChanged 覆盖。
        return Ok(_sessions.TryGetClipboard(sessionId) ?? new RemoteClipboardDto(
            null, Note: "已向被控机请求剪贴板，内容到达后会推送到页面。"));
    }

    /// <summary>最近一次读到的剪贴板内容（<c>null</c> = 还没读过）。</summary>
    [HttpGet("sessions/{sessionId}/clipboard")]
    public IActionResult Clipboard(string sessionId)
    {
        if (_sessions.TryGet(sessionId) is null)
        {
            return NotFound(new RemoteNotAvailableDto(
                RemoteErrorCodes.SessionNotFound,
                $"不存在会话「{sessionId}」（可能已经结束）。"));
        }

        return Ok(_sessions.TryGetClipboard(sessionId));
    }

    /// <summary>
    /// <b>把一段文字写进被控机的剪贴板</b>（M5.5）。
    ///
    /// <para>
    /// 🔴 <b>铁律 7：这是作用到别的机器的写操作</b>。本端点整体是 <c>AdminOnly</c>，
    /// 且必须<"正在看这一路">才写 —— 后者在这条 HTTP 路径上无法判断（没有 Hub 连接的概念），
    /// 所以判定依据是<b>管理员身份 + 会话存在</b>；Hub 那条路径仍会做归属校验。
    /// </para>
    /// </summary>
    [HttpPost("sessions/{sessionId}/clipboard")]
    public async Task<IActionResult> WriteClipboard(
        string sessionId,
        [FromBody] RemoteClipboardRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new RemoteNotAvailableDto(
                RemoteErrorCodes.BadRequest, "请求体为空。"));
        }

        if (_sessions.TryGet(sessionId) is null)
        {
            return NotFound(new RemoteNotAvailableDto(
                RemoteErrorCodes.SessionNotFound,
                $"不存在会话「{sessionId}」（可能已经结束）。"));
        }

        var result = await _service
            .SetClipboardAsync(sessionId, request.Text, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new RemoteNotAvailableDto(
                    RemoteErrorCodes.AgentOffline,
                    "写入指令没能送达被控端（它可能刚掉线）。被控机的剪贴板没有改变。"));
        }

        // ⚠️ 超限是 400（请求本身的问题），不是 502：调用方要改的是内容，不是网络。
        return result.Text is null && result.Note is not null
            ? BadRequest(result)
            : Ok(result);
    }

    // ═══════════════════════════════════════════════════════════
    // 输入回传（M5.2）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>送回一批输入（键鼠）</b>（M5.2）。
    ///
    /// <para>
    /// ⚠️ <b>这是一条"脚本入口"，不是观看页面的入口</b>：页面走
    /// <c>/hubs/remote</c> 上的 <c>SendInput</c>（它已经有一条现成的长连接，
    /// 而输入是高频动作，走 HTTP 一问一答会让每次点击都摊上一次完整的请求开销）。
    /// 本端点保留给<b>自动化</b>：按坐标点一下、输入一串文字，这类"低频、离散"的用法
    /// 用 HTTP 描述起来最自然，而且它天然能被 <c>LicenseGate</c> 分类表管住。
    /// </para>
    ///
    /// <para>
    /// 🔥 <b>两条入口共用同一个服务方法</b>（<c>RemoteSessionService.SendInputAsync</c>），
    /// 所以四道判断（会话在不在 / 闸门开没开 / 批次大小 / 机器在不在线）
    /// <b>只有一份</b>。这一点是刻意的 —— 两条路各写一遍判断，
    /// 迟早会出现"从 Hub 进来被挡住、从 HTTP 进来却放过去"那种最难发现的分叉。
    /// </para>
    /// </summary>
    /// <response code="404">会话不存在。</response>
    /// <response code="400">会话 Id 为空。</response>
    /// <response code="409">
    /// 会话在，但输入没被接受（<b>没打开「允许控制」</b> / 机器掉线 / 被控端不支持）。
    /// <para>
    /// 🔥 用 <c>409</c> 而不是 <c>400</c>：请求本身是合法的，
    /// 只是<b>当前状态</b>不允许 —— 而这正是调用方该去改状态（打开开关）
    /// 而不是改请求的地方。
    /// </para>
    /// </response>
    [HttpPost("sessions/{sessionId}/input")]
    public async Task<IActionResult> Input(
        string sessionId,
        [FromBody] RemoteInputRequest? request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(new RemoteNotAvailableDto(
                RemoteErrorCodes.SessionNotFound, "会话 Id 为空。"));
        }

        // 先单独判"有没有这个会话"（与黑白那个端点同一条理由）：
        // 合并成一句"失败了"的话，现场分不清"是不是我记错了会话 Id"。
        if (_sessions.TryGet(sessionId) is null)
        {
            return NotFound(new RemoteNotAvailableDto(
                RemoteErrorCodes.SessionNotFound,
                $"不存在会话「{sessionId}」（可能已经结束）。"));
        }

        var ack = await _service
            .SendInputAsync(sessionId, request?.Events ?? Array.Empty<RemoteInputEventDto>(), cancellationToken)
            .ConfigureAwait(false);

        if (ack.Ok) return Ok(ack);

        // 只有"会话状态不允许"才回 409；批次过大是**请求本身**的问题，回 400。
        var status = ack.Code == RemoteErrorCodes.InputTooLarge
            ? StatusCodes.Status400BadRequest
            : StatusCodes.Status409Conflict;

        return StatusCode(status, ack);
    }

    /// <summary>
    /// <b>打开 / 关闭这一路会话的输入回传闸门</b>（M5.2）。
    ///
    /// <para>
    /// 🔥 <b>为什么它是独立的端点，而不是 <c>Input</c> 请求体里的一个开关</b>：
    /// 开关决定的是"这一路会话当前允不允许被操作"，它是一个<b>状态</b>，
    /// 必须能被单独读取（页面上一行字、"现在谁在控"的判断都靠它）。
    /// 夹在事件流里的开关回答不了"它现在是开还是关"。
    /// </para>
    /// <para>
    /// ⚠️ 它与"黑白模式"的差别值得记一笔：黑白要下指令给被控端，
    /// 所以改成功了也只是"我们让它改"；<b>本闸门完全在中心这一侧</b> ——
    /// 关掉之后中心根本不会转发输入，所以"已关闭"是<b>可靠的</b>结论。
    /// 而"已打开"只说明闸门开着，不代表被控端收得下（那由会话快照里的
    /// <c>inputState</c> 如实反映）。
    /// </para>
    /// </summary>
    [HttpPost("sessions/{sessionId}/input/enabled")]
    public IActionResult SetInputEnabled(string sessionId, [FromBody] RemoteInputToggleDto? request)
    {
        if (request is null)
        {
            // ⚠️ 这里**不能**回 InputNotEnabled：那句话的意思是"中心的控制开关是关着的"
            //    （去点开关），而空请求体是"请求没按约定发"（去改调用方）。
            //    两句都被读成"输入有问题"，但要做的事完全相反。
            return BadRequest(new RemoteNotAvailableDto(
                RemoteErrorCodes.BadRequest, "请求体为空。"));
        }

        var changed = _sessions.SetInputEnabled(sessionId, request.Enabled);

        return changed is null
            ? NotFound(new RemoteNotAvailableDto(
                RemoteErrorCodes.SessionNotFound,
                $"不存在会话「{sessionId}」（可能已经结束）。"))
            : Ok(changed);
    }

    /// <summary>
    /// 结束会话。
    /// <para>
    /// 🔥 注意它在 <c>LicenseGate</c> 里是 <b>Allowed</b> —— 因为它<b>只做减法</b>。
    /// 未授权时必须仍然能"让正在进行的远控停下来"。
    /// </para>
    /// </summary>
    [HttpDelete("sessions/{sessionId}")]
    public async Task<IActionResult> StopSession(string sessionId, CancellationToken cancellationToken)
    {
        var dto = await _service
            .StopAsync(sessionId, "管理员结束了这路会话", cancellationToken)
            .ConfigureAwait(false);

        return dto is null
            ? NotFound(new RemoteNotAvailableDto(
                RemoteErrorCodes.SessionNotFound,
                $"不存在会话「{sessionId}」（可能已经结束）。"))
            : Ok(dto);
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把建会话的失败结论翻成 HTTP 状态码。
    /// <para>
    /// 🔥 <b>状态码在这里不是装饰</b>：它决定页面要不要重试、
    /// 以及在现场该往哪个方向查。所以"没这台机器"（404）、
    /// "有但掉线 / 已有会话"（409）、"链路或客户端出错"（502）三者必须分开。
    /// </para>
    /// </summary>
    private ObjectResult Fail(RemoteSessionOpened opened)
    {
        var status = opened.ErrorCode switch
        {
            RemoteErrorCodes.AgentNotFound or RemoteErrorCodes.SessionNotFound
                => StatusCodes.Status404NotFound,

            RemoteErrorCodes.AgentOffline
                or RemoteErrorCodes.SessionBusy
                or RemoteErrorCodes.SessionSideUnavailable

                // 🔥 配额用完是**当前状态不允许**（去结束一路 / 等别人结束），
                //    不是请求错了 —— 与 SessionBusy 同一类，所以也是 409。
                or RemoteErrorCodes.QuotaExceeded
                => StatusCodes.Status409Conflict,

            _ => StatusCodes.Status502BadGateway,
        };

        return StatusCode(
            status,
            new RemoteNotAvailableDto(
                opened.ErrorCode ?? RemoteErrorCodes.StartFailed,
                opened.Message ?? "建立远程桌面会话失败。"));
    }
}
