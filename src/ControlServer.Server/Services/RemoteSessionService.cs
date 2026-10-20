using System.Text.Json;
using System.Text.Json.Serialization;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using ControlServer.Server.Hubs;
using ControlServer.Server.Options;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.Services;

/// <summary>
/// <b>远控会话的编排</b>（M5.1）—— 把"页面上点一下"翻译成"某台机器开始抓屏推流"。
///
/// <para>
/// <b>它与 <see cref="RemoteSessionRegistry"/> 的分工</b>：
/// 那个是<b>账本</b>（谁在推、推到哪儿了、谁在看），本类是<b>动作</b>（下发指令、收场）。
/// 分开的理由很实际 —— 账本要能被 Hub、被后台清理任务、被控制器一起用，
/// 而"下发指令"需要 <see cref="IHubContext{T}"/> 与在线表；
/// 把这两件事塞进一个类，它就会同时是"状态容器"和"网络客户端"，
/// 而下一个改它的人分不清哪一部分是可以随便读的。
/// </para>
///
/// <para>
/// 🔥 <b>最重要的一条事实：本类无法知道被控端有没有真的开始推流。</b>
/// </para>
/// <list type="bullet">
///   <item>中心下发指令用的是 <c>SendAsync</c>，而客户机的指令处理是<b>用后即忘</b>的
///         （见 <c>AgentRuntime.OnCommand</c> 里的 <c>_ = ExecuteLocallyAsync(command)</c>）——
///         指令的结论不会回到这里；</item>
///   <item>而且 <c>Clients.Client(connectionId).SendAsync</c> 对一条<b>已经不存在的连接</b>
///         是静默成功的（SignalR 不报错，消息直接丢掉）。</item>
/// </list>
/// <para>
/// ⇔ 所以建会话的返回值里状态只能是 <see cref="RemoteSessionStates.Requested"/>，
/// <b>绝不能写 <c>streaming</c></b>。真实结论由被控端经 <c>ReportRemoteSession</c> 送回来
/// （成功→streaming、失败→failed 带原因）。页面上"点了没反应"与"正在连接"
/// 的区别，全靠这一条撑住。
/// </para>
/// </summary>
public sealed class RemoteSessionService
{
    /// <summary>
    /// 指令载荷的 JSON 约定。
    /// <para>
    /// ⚠️ 与客户机侧 <c>RemoteSessionCoordinator</c> 的那份必须<b>成对</b>：
    /// 它按 camelCase 写、按大小写不敏感读。这里写 camelCase 是与它对齐；
    /// 而 <c>WhenWritingNull</c> 是为了让 <c>grayscale:null</c> 这种"本次动作不涉及"的
    /// 字段干脆不出现 —— 客户机那边读到 <c>null</c> 与"字段不存在"是同一个结果，
    /// 少几个字段能让抓到的报文短到可以在日志里一眼看完。
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly RemoteSessionRegistry _sessions;
    private readonly RemoteInputRelay _input;
    private readonly AgentHostRegistry _hosts;
    private readonly AgentEncoderCapabilityService _capabilities;
    private readonly IHubContext<AgentHub> _agentHub;
    private readonly LogService _log;

    /// <summary>并发准入（M5.5）。<c>null</c> = 用例直接构造时没给配置，用默认值。</summary>
    private readonly RemoteOptions _remote;

    public RemoteSessionService(
        RemoteSessionRegistry sessions,
        RemoteInputRelay input,
        AgentHostRegistry hosts,
        AgentEncoderCapabilityService capabilities,
        IHubContext<AgentHub> agentHub,
        LogService log,
        IOptions<ServerOptions>? options = null)
    {
        _sessions = sessions;
        _input = input;
        _hosts = hosts;
        _capabilities = capabilities;
        _agentHub = agentHub;
        _log = log;

        // ⚠️ 默认值与 <see cref="RemoteOptions"/> 上的默认值**必须一致**：
        //    不一致的话，用例跑的是 4 路、生产跑的是别的数，而"配额到底管不管用"
        //    就永远只能靠读代码判断。这里只抄一次，且抄的是同一个类型的默认实例。
        _remote = options?.Value.Remote ?? new RemoteOptions();
    }

    // ═══════════════════════════════════════════════════════════
    // 建立
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 让一台机器开始抓屏推流。
    /// </summary>
    /// <remarks>
    /// 🔥 顺序是<b>刻意的</b>：① 查机器 → ② 查长连接 → ③ <b>协商编码格式</b> → ④ 登记 → ⑤ 下发。
    ///
    /// <para>
    /// ③ 与 ④ 的先后是本批（第 7 步）加的：<b>协商失败要在登记之前返回</b>，
    /// 否则账本上会留下一条"确定建不起来"的会话，而它会用 <c>SessionBusy</c>
    /// 挡掉这台机器之后<b>每一次</b>建会话 —— 一次失败变成永久故障。
    /// </para>
    ///
    /// <para>
    /// 反过来（先下发、后登记）有一个真实的竞态：被控端可能<b>快过</b>中心的登记 ——
    /// 它在同一毫秒里就回了 <c>streaming</c> 状态，甚至送出了第一帧，
    /// 而那时账本上还没有这个 <c>sessionId</c> ⇒ 帧与状态一起被判成"会话不存在"丢掉，
    /// 表现是<b>画面永远不出来，且两边日志都正常</b>。
    /// </para>
    /// <para>
    /// 先登记就没有这个窗口：指令发出之前，中心已经认得这个 Id 了。
    /// 代价是"指令下发失败"时要记得回滚登记 —— 就是下面那个 <c>Close</c>。
    /// </para>
    /// </remarks>
    public async Task<RemoteSessionOpened> StartAsync(
        RemoteSessionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || request.AgentId <= 0)
        {
            return RemoteSessionOpened.Fail(RemoteErrorCodes.AgentNotFound, "机器 ID 必须是正整数。");
        }

        // ── ① 机器在不在在线表里 ──
        var host = _hosts.Hosts.FirstOrDefault(h => h.Id == request.AgentId);

        if (host is null)
        {
            return RemoteSessionOpened.Fail(
                RemoteErrorCodes.AgentNotFound,
                $"在线表里没有 ID 为 {request.AgentId} 的机器。");
        }

        // ── ② 它此刻有没有长连接 ──
        //
        // 🔥 与①分开报（见 RemoteErrorCodes.AgentOffline 的注释）：
        //    "没这台机器"要去装 Agent，"有这台机器但它掉线了"只要等它重连 ——
        //    合并成一句"客户端不存在"，现场会去重装一个本来好好的客户端。
        var connectionId = _hosts.ConnectionIdOf(request.AgentId);

        if (string.IsNullOrEmpty(connectionId))
        {
            return RemoteSessionOpened.Fail(
                RemoteErrorCodes.AgentOffline,
                $"机器「{host.DisplayName}」此刻没有长连接，指令发不出去。等它重连后再试。");
        }

        // ── ③ 编码格式协商（**在建账之前**，M5.4-b 第 7 步）──
        //
        // 🔥 为什么放在登记之前：协商失败是"确定做不到"，不该在账本上留下一条会话 ——
        //    留下的话这台机器会显示"有一路会话"，而之后每一次建会话都被 SessionBusy 挡掉
        //    （就是下面 ④ 那个"一次失败变成永久故障"的形态）。
        //
        // 🔴 中心在这里**给不出最终答案**，只能给出"偏好 ＋ 可接受集合"：
        //    交集有三项输入，而第三项 —— 本次会话的分辨率 —— 只有被控端在挑屏之后才知道
        //    （H.264 硬编封顶 4096、HEVC 封顶 8192）。裁决在被控端做，见 RemoteStreamHost。
        var negotiated = RemoteVideoNegotiation.Decide(
            _capabilities.TryGet(request.AgentId)?.SupportedCodecs,
            request.ViewerVideo,
            out var packaging,
            out var negotiationNote,
            out var negotiationFailure);

        _log.Info("Remote", $"[Remote] 机器 #{request.AgentId} 的编码格式协商：{negotiationNote}");

        // 🔥 "不认得的值 ⇒ 回落 + **留痕**"：只回落不留痕的话，
        //    观看端写错一个连字符（out-of-band）会被安静按带内跑，
        //    而症状出现在浏览器（拿 avc1 的串去解 Annex-B 码流），真因在这里。
        var requestedPackaging = request.ViewerVideo?.Packaging;

        if (!string.IsNullOrWhiteSpace(requestedPackaging)
            && !RemoteStreamPackagings.IsKnown(requestedPackaging))
        {
            _log.Warn(
                "Remote",
                $"[Remote] 机器 #{request.AgentId}：观看端下发的封装「{requestedPackaging}」不是一个已知取值"
                + $"（认得的只有 {RemoteStreamPackagings.InBand} / {RemoteStreamPackagings.OutOfBand}）"
                + $"—— 已按默认「{packaging}」执行。");
        }

        if (negotiationFailure is not null)
        {
            _log.Warn("Remote", $"[Remote] 拒绝为机器 #{request.AgentId} 建会话（编码格式协商失败）：{negotiationFailure}");

            return RemoteSessionOpened.Fail(RemoteErrorCodes.CodecNotNegotiable, negotiationFailure);
        }

        _log.Info(
            "Remote",
            $"[Remote] 机器 #{request.AgentId} 的协商结论：格式 = {string.Join(" > ", negotiated)}"
            + $"，封装 = {packaging}。⚠️ 这只是**中心的偏好** —— 实际用的是哪个由被控端"
            + "在挑屏之后裁决（它才知道那块屏多大），最终以首帧的 codec 为准。");

        // ── ④ 登记（含"这台机器是否已有一路"与**全中心配额**的判定）──
        //
        // 🔥 配额判在账本里、与"这台机器是否已有一路"同一把锁：放进来的话，
        //    两个同时到达的请求会各自数到 3 路、一起通过 —— 上限 5 变成上限 6，
        //    而且**没有任何日志**（见 RemoteSessionRegistry.Open 里的说明）。
        var quota = new RemoteQuota(
            _remote.MaxConcurrentSessions,
            _remote.MaxTotalMbps * 1_000_000d);

        var opened = _sessions.Open(
            request.AgentId,
            request.ScreenId,
            request.DisplayMode,
            request.MaxFps,
            BeijingTime.Now,
            request.Quality,
            quota);

        if (opened.Session is not { } session) return opened;

        // ── ⑤ 下发指令 ──
        //
        // 🔥 帧率下发的是**折出来的目标**，不是请求原值：请求里可能没给 MaxFps，
        //    而档位自带一个推荐帧率。下发原值的话，"低画质档"在托盘侧只剩降码率，
        //    降档的效果就少了一半（而页面上写着"低画质"）。
        var command = new RemoteSessionCommandDto(
            Action: RemoteSessionActions.Start,
            SessionId: session.SessionId,
            ScreenId: request.ScreenId,
            MaxFps: session.TargetFps ?? request.MaxFps,
            Codecs: negotiated,
            Packaging: packaging,
            Quality: session.Quality,
            BitrateKbps: session.TargetBitrateKbps);

        var sent = await SendAsync(connectionId, request.AgentId, command, cancellationToken)
            .ConfigureAwait(false);

        if (!sent.Ok)
        {
            // 🔥 回滚登记：不回滚的话，这台机器会**一直显示"有一路会话在推"**，
            //    于是之后每一次建会话都被 SessionBusy 挡掉 —— 而真正的原因
            //    （一条指令没发出去）早就过去了。这就是"一次失败变成永久故障"的经典形态。
            _sessions.Close(session.SessionId, sent.Message ?? "指令下发失败", failed: true);

            return RemoteSessionOpened.Fail(sent.Code ?? RemoteErrorCodes.StartFailed, sent.Message ?? "指令下发失败。");
        }

        // ⚠️ 注意这里**没有**把状态改成 streaming。原因见类注释：
        //    下发成功只说明"没抛异常"，连"那台机器收到了"都不能保证。
        return opened;
    }

    // ═══════════════════════════════════════════════════════════
    // 结束
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 结束一路会话。返回收场时的快照；会话不存在时 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 🔥 <b>先下发停止、再把账本关掉</b>，与建立时的顺序<b>相反</b>。
    /// <para>
    /// 因为这里的失败模式不一样：登记还在的时候，被控端迟到的帧与状态才有人接
    /// （"停"的过程中它可能正好送出最后一帧、或者回一条 <c>stopped</c>）；
    /// 反过来先关账本，那些消息会全部撞上"会话不存在"，
    /// 而被控端那边的一条规则是<b>收到这种拒绝就当场停下这路流</b> ——
    /// 结果虽然也是停，但停的原因是"我们看到会话没了"，而不是"我们让它停的"，
    /// 事后翻日志会看到一条误导人的"会话不存在"。
    /// </para>
    /// <para>
    /// ⚠️ <b>停止指令发失败不算失败</b>：真正要保证的是"被控端不再推流"，
    /// 而这件事在"它本来就掉线"的时候已经成立了。被控端那边也有兜底
    /// （断开长连接时 <c>RemoteSessionCoordinator.OnDisconnected</c> 会自己停）。
    /// </para>
    /// </remarks>
    public async Task<RemoteSessionDto?> StopAsync(
        string sessionId,
        string reason,
        CancellationToken cancellationToken)
    {
        var session = _sessions.TryGet(sessionId);

        if (session is null) return null;

        var connectionId = _hosts.ConnectionIdOf(session.AgentId);

        if (!string.IsNullOrEmpty(connectionId))
        {
            var command = new RemoteSessionCommandDto(
                Action: RemoteSessionActions.Stop,
                SessionId: sessionId);

            var sent = await SendAsync(connectionId, session.AgentId, command, cancellationToken)
                .ConfigureAwait(false);

            if (!sent.Ok)
            {
                _log.Warn(
                    "Remote",
                    $"[Remote] 停止指令没能送达会话 {sessionId}（{sent.Message}）—— "
                    + "仍在本地收场；被控端那边会在断开或空闲时自己停。");
            }
        }
        else
        {
            _log.Warn(
                "Remote",
                $"[Remote] 会话 {sessionId} 所属机器 #{session.AgentId} 已掉线，"
                + "停止指令无处可发 —— 它重连时会自己停（见 RemoteSessionCoordinator.OnDisconnected）。");
        }

        return _sessions.Close(sessionId, reason);
    }

    /// <summary>
    /// <b>给"中心已经判死、而被控端还不知道"的会话补一条 stop</b>（2026-10-19）。
    ///
    /// <para>
    /// 🔥🔥 <b>为什么那条 stop 必须补</b>：<c>RemoteSessionSweeperHostedService</c> 判死一路会话时
    /// 只在<b>本地</b>把账本摘掉，被控端那边<b>完全不知道</b> —— 它会继续以为自己有一路在推，
    /// 于是<b>这台机器上之后每一次建会话都会被自己拒绝</b>：
    /// </para>
    /// <code>
    /// 09:18:22  中心：会话 091729 已结束（15 秒没有收到任何帧与状态）
    /// 09:18:34  被控端：已有会话「091729」在推流（M5.1 只允许一路）→ 拒绝
    /// 09:18:55  同上 ·  09:19:18 同上 ·  09:19:45 同上
    /// </code>
    /// <para>
    /// 而页面上那句话是"没人登录 / 托盘没在跑 / 客户端版本过旧" —— <b>三条都不对</b>。
    /// 真正的修法有两处：这里补 stop（从源头掐断），以及被控端自己允许新会话接管
    /// （<c>RemoteSessionCoordinator.StartAsync</c>，兜住"stop 又没送到"的那种情况）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>它是尽力而为、且绝不抛</b>：收尸人跑在后台循环里，一条 stop 发不出去
    /// 不该让整轮巡检失败（其余会话还要判）。机器不在线时也照样返回 —— 那条连接重连后
    /// 被控端会自己收摊（<c>OnDisconnected</c>），而且它现在也能被新会话接管。
    /// </para>
    /// </summary>
    /// <param name="agentId">那台机器。</param>
    /// <param name="sessionId">要它停的那一路。</param>
    public async Task StopOrphanedAsync(int agentId, string sessionId)
    {
        var connectionId = _hosts.ConnectionIdOf(agentId);

        if (string.IsNullOrEmpty(connectionId))
        {
            _log.Info(
                "Remote",
                $"[Remote] 会话 {sessionId} 已被中心判死；机器 #{agentId} 不在线，停止指令无处可发"
                + "（它重连时会自己收摊，新会话也能直接接管）。");

            return;
        }

        try
        {
            var command = new RemoteSessionCommandDto(
                Action: RemoteSessionActions.Stop,
                SessionId: sessionId);

            var sent = await SendAsync(connectionId, agentId, command, CancellationToken.None)
                .ConfigureAwait(false);

            _log.Info(
                "Remote",
                sent.Ok
                    ? $"[Remote] 会话 {sessionId} 已被中心判死，已通知机器 #{agentId} 停止推流。"
                    : $"[Remote] 会话 {sessionId} 已被中心判死，但停止指令没能送达机器 #{agentId}"
                      + $"（{sent.Message}）—— 它会由新会话接管兜住。");
        }
        catch (Exception ex)
        {
            // 铁律 #6：后台路径上的异常必须自己吞掉并记账，不许冒泡到巡检循环。
            _log.Warn(
                "Remote",
                $"[Remote] 会话 {sessionId} 判死后补发停止指令时出错（不影响其余会话）：{ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 黑白模式（决策 14）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 切一路会话的黑白模式。
    /// </summary>
    /// <returns>变更后的会话；会话不存在时 <c>null</c>。</returns>
    /// <remarks>
    /// ⚠️ <b>本方法有一个诚实的缺口，必须在页面上承认它</b>：
    /// 被控端执行灰度之后<b>没有任何回执</b>（它的 <c>GrayscaleAsync</c> 结论只进
    /// 客户机本地的"接收到的数据"环形缓冲，不回中心）。所以这里的"成功"
    /// 只意味着<b>指令没抛异常</b>，不意味着画面真的变灰了。
    /// 因此：
    /// <list type="bullet">
    ///   <item>指令抛异常 ⇒ 不改进账本，如实回失败（这时我们<b>知道</b>没生效）；</item>
    ///   <item>指令没抛 ⇒ 改进账本并广播（这是"我们让它这么做"，不是"它已经这样了"）。</item>
    /// </list>
    /// 页面上那个开关的语义因此是<b>"请求值"</b>。想真正验证，得看画面上的彩色内容
    /// 是不是变成了灰阶 —— 而那一眼比任何回执都可靠。
    /// </remarks>
    public async Task<RemoteSessionDto?> SetGrayscaleAsync(
        string sessionId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var session = _sessions.TryGet(sessionId);

        if (session is null) return null;

        var connectionId = _hosts.ConnectionIdOf(session.AgentId);

        if (!string.IsNullOrEmpty(connectionId))
        {
            var command = new RemoteSessionCommandDto(
                Action: RemoteSessionActions.Grayscale,
                SessionId: sessionId,
                Grayscale: enabled);

            var sent = await SendAsync(connectionId, session.AgentId, command, cancellationToken)
                .ConfigureAwait(false);

            if (!sent.Ok)
            {
                _log.Warn("Remote", $"[Remote] 会话 {sessionId} 黑白切换指令下发失败：{sent.Message}");

                // ⚠️ 不改进账本：改了就变成"页面上是灰的、画面还是彩的"，
                //    而那种不一致比"开关弹回去"难查得多（后者至少知道自己没切成功）。
                return null;
            }
        }
        else
        {
            // 掉线的机器：记下这个意图没有意义（它重连时不会读它），如实回失败。
            _log.Warn(
                "Remote",
                $"[Remote] 会话 {sessionId} 所属机器 #{session.AgentId} 已掉线，黑白切换无处可发。");

            return null;
        }

        return _sessions.SetGrayscale(sessionId, enabled);
    }

    // ═══════════════════════════════════════════════════════════
    // 质量档位（M5.5 运行时自适应）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 改一路会话的质量档位（码率 + 帧率，必要时连带灰度由调用方另发）。
    /// </summary>
    /// <returns>变更后的会话；会话不存在或指令没送达时 <c>null</c>。</returns>
    /// <remarks>
    /// 🔥 <b>"改成功"只等于"指令没抛异常"</b>，与黑白模式同一条缺口：
    /// 托盘侧会因为码率变化而<b>重建编码器</b>（≈0.3 s 空窗），
    /// 而它有没有重建成功，只有它自己知道。
    /// 所以这里记账的语义是"<b>我们让它这样</b>"，页面上那句话也必须这么写。
    ///
    /// <para>
    /// ⚠️ <b>手动选档会清掉"自动降档"的痕迹</b>：用户明确说"我就要高档"之后，
    /// 页面上还挂着"已自动降档"会让人以为自己的选择没生效。
    /// 而 governor 会在下一轮重新判 —— 真的编不动，它还会再降一次并重新留痕。
    /// </para>
    /// </remarks>
    public async Task<RemoteSessionDto?> SetQualityAsync(
        string sessionId,
        string? profile,
        int? maxFps,
        CancellationToken cancellationToken)
    {
        var session = _sessions.TryGet(sessionId);

        if (session is null) return null;

        var normalized = RemoteQualityProfiles.Normalize(profile);
        var (profileFps, profileKbps) = RemoteQualityProfiles.Resolve(normalized);

        // 与建会话同一条折法（M7.16）：帧率以请求为准，档位只提供默认值。
        var targetFps = RemoteQualityProfiles.FoldTargetFps(maxFps, profileFps);

        var connectionId = _hosts.ConnectionIdOf(session.AgentId);

        if (string.IsNullOrEmpty(connectionId))
        {
            _log.Warn(
                "Remote",
                $"[Remote] 会话 {sessionId} 所属机器 #{session.AgentId} 已掉线，质量切换无处可发。");

            return null;
        }

        var command = new RemoteSessionCommandDto(
            Action: RemoteSessionActions.Quality,
            SessionId: sessionId,
            MaxFps: targetFps,
            Quality: normalized,
            BitrateKbps: profileKbps,
            Grayscale: session.Grayscale);

        var sent = await SendAsync(connectionId, session.AgentId, command, cancellationToken)
            .ConfigureAwait(false);

        if (!sent.Ok)
        {
            _log.Warn("Remote", $"[Remote] 会话 {sessionId} 质量切换指令下发失败：{sent.Message}");

            return null;
        }

        _log.Info(
            "Remote",
            $"[Remote] 会话 {sessionId} 的质量档改为「{normalized}」"
            + $"（目标 {targetFps} fps / {profileKbps} kbps）。");

        return _sessions.ApplyQuality(
            sessionId, normalized, targetFps, profileKbps, downgraded: false, note: null);
    }

    // ═══════════════════════════════════════════════════════════
    // ROI：只编视口那一块（M5.5 第 5 项）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 让这一路<b>只编画面里的这一块</b>（<paramref name="width"/> 或
    /// <paramref name="height"/> ≤ 0 = 关掉，回到整幅画面）。
    /// </summary>
    /// <returns>变更后的会话；会话不存在或指令没送达时 <c>null</c>。</returns>
    /// <remarks>
    /// 🔥 <b>为什么坐标由页面给、中心不校验不换算</b>：视口就是"用户此刻看到的那一块"，
    /// 而"看到哪一块"只有浏览器知道（滚动位置、缩放、显示模式）。中心在这里
    /// 做任何夹取/吸附都会让<b>另一侧</b>再夹一次 —— 两次夹取的边界条件不一样，
    /// 表现是"滚到最右边时画面偏一格"，而两端日志都正常。
    ///
    /// <para>
    /// ⚠️ <b>越界的视口由被控端夹</b>（<c>EncodeRegionLayout.Clamp</c>，偶数对齐）；
    /// 中心只挡"明显是错的值"（负数、宽度 ≤ 1），那是**请求本身的错**，不是状态问题。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>"改成功"仍只等于"指令没抛异常"</b>：真正切过去需要重建编码器（≈0.3 秒）。
    /// 页面要知道这一点，否则用户会以为"按了没反应"。
    /// </para>
    /// </remarks>
    public async Task<RemoteSessionDto?> SetViewportAsync(
        string sessionId,
        int x,
        int y,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var session = _sessions.TryGet(sessionId);

        if (session is null) return null;

        var off = width <= 0 || height <= 0;

        if (!off && (x < 0 || y < 0))
        {
            _log.Warn("Remote", $"[Remote] 会话 {sessionId} 的视口坐标是负数（{x},{y}）—— 已拒绝。");

            return null;
        }

        var connectionId = _hosts.ConnectionIdOf(session.AgentId);

        if (string.IsNullOrEmpty(connectionId))
        {
            _log.Warn(
                "Remote",
                $"[Remote] 会话 {sessionId} 所属机器 #{session.AgentId} 已掉线，视口切换无处可发。");

            return null;
        }

        var command = new RemoteSessionCommandDto(
            Action: RemoteSessionActions.Viewport,
            SessionId: sessionId,
            ViewportX: off ? 0 : x,
            ViewportY: off ? 0 : y,
            ViewportWidth: off ? 0 : width,
            ViewportHeight: off ? 0 : height);

        var sent = await SendAsync(connectionId, session.AgentId, command, cancellationToken)
            .ConfigureAwait(false);

        if (!sent.Ok)
        {
            _log.Warn("Remote", $"[Remote] 会话 {sessionId} 视口切换指令下发失败：{sent.Message}");

            return null;
        }

        _log.Info(
            "Remote",
            off
                ? $"[Remote] 会话 {sessionId} 关掉视口（回到整幅画面）。"
                : $"[Remote] 会话 {sessionId} 的视口改为 {x},{y} {width}×{height}（只编这一块）。");

        return _sessions.ApplyViewport(sessionId, off ? 0 : x, off ? 0 : y, off ? 0 : width, off ? 0 : height);
    }

    // ═══════════════════════════════════════════════════════════
    // 多屏选择（M5.5）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 向某台被控机<b>索取一次屏清单</b>（下发指令；清单经上行异步回来）。
    /// </summary>
    /// <returns>指令发出去了没有。<b>发出去 ≠ 清单已经到手</b>（上行是另一条路）。</returns>
    /// <remarks>
    /// 🔥 <b>为什么不做成"一问一答"</b>：中心 → 客户机的长连接是 fire-and-forget
    /// （见类注释），回执止步于服务进程。所以这里是"问一次、等一秒、读缓存"的形态 ——
    /// 端点在 <c>RemoteController.Screens</c>，缓存是 <c>AgentScreenDirectory</c>。
    /// </remarks>
    public async Task<bool> RequestScreensAsync(int agentId, CancellationToken cancellationToken)
    {
        var connectionId = _hosts.ConnectionIdOf(agentId);

        if (string.IsNullOrEmpty(connectionId)) return false;

        var command = new RemoteSessionCommandDto(
            Action: RemoteSessionActions.Screens,

            // ⚠️ 这条指令**不针对任何一路会话**，但契约里 SessionId 是非空字段
            //    （其它每条指令都要它）。用这个常量而不是空串：空串会被
            //    客户机的通用校验当成"缺字段"挡下来，而这条指令本来就不需要它。
            SessionId: RemoteScreenRequests.NoSessionId);

        var sent = await SendAsync(connectionId, agentId, command, cancellationToken)
            .ConfigureAwait(false);

        return sent.Ok;
    }

    // ═══════════════════════════════════════════════════════════
    // 剪贴板（M5.5）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把一段文字<b>写进被控机的剪贴板</b>。
    /// </summary>
    /// <returns>指令送达 = 成功；会话不存在 / 掉线 = <c>null</c>。</returns>
    /// <remarks>
    /// 🔴 <b>铁律 7：这是"作用到别的机器的写操作"</b> ⇒ 归属校验必须在调用方做
    /// （Hub 那条走 <c>RemoteWatchScope.IsWatching</c>；HTTP 入口是管理员专用）。
    ///
    /// <para>
    /// ⚠️ <b>超限是"拒绝"，不是"截断"</b>：截断一段用户复制的内容，
    /// 对方粘出来少一半而且<b>没有任何提示</b> —— 比直接失败坏得多。
    /// </para>
    /// </remarks>
    public async Task<RemoteClipboardDto?> SetClipboardAsync(
        string sessionId,
        string? text,
        CancellationToken cancellationToken)
    {
        var session = _sessions.TryGet(sessionId);

        if (session is null) return null;

        if (text is null)
        {
            return new RemoteClipboardDto(
                null,
                Note: "没有要写入的内容（空文本）。",
                At: BeijingTime.Now);
        }

        if (text.Length > RemoteClipboardLimits.MaxChars)
        {
            return new RemoteClipboardDto(
                null,
                Note: $"这段文字有 {text.Length} 个字符，超过单次上限 {RemoteClipboardLimits.MaxChars} "
                      + "—— 已拒绝（不做截断：截断之后对方粘出来会少一半，而且看不出少了）。",
                At: BeijingTime.Now);
        }

        var connectionId = _hosts.ConnectionIdOf(session.AgentId);

        if (string.IsNullOrEmpty(connectionId))
        {
            _log.Warn("Remote", $"[Remote] 会话 {sessionId} 所属机器 #{session.AgentId} 已掉线，剪贴板写不进去。");

            return null;
        }

        var command = new RemoteSessionCommandDto(
            Action: RemoteSessionActions.Clipboard,
            SessionId: sessionId,
            ClipboardAction: RemoteClipboardActions.Set,
            ClipboardText: text);

        var sent = await SendAsync(connectionId, session.AgentId, command, cancellationToken)
            .ConfigureAwait(false);

        if (!sent.Ok)
        {
            _log.Warn("Remote", $"[Remote] 会话 {sessionId} 的剪贴板写入指令下发失败：{sent.Message}");

            return null;
        }

        _log.Info("Remote", $"[Remote] 会话 {sessionId}：已把 {text.Length} 个字符写进被控机剪贴板（请求值）。");

        return new RemoteClipboardDto(text, At: BeijingTime.Now);
    }

    /// <summary>
    /// <b>请被控机把它的剪贴板读过来</b>（M5.5）。
    /// </summary>
    /// <returns>指令发出去没有。<b>发出去 ≠ 内容已经到</b>：文本经
    /// <c>ReportClipboard</c> 上行回来，再由中心推给观看页（<c>ClipboardChanged</c>）。</returns>
    /// <remarks>
    /// 🔥 <b>为什么不做成"请求里就把文本带回来"</b>：中心 → 客户机的指令是
    /// fire-and-forget，回执止步于服务进程（见类注释）。要做成同步的，
    /// 就得让 HTTP 请求等一条跨三跳的消息 —— 而它随时可能永远不来，
    /// 于是这个端点会变成"每点一次卡 30 秒"。
    /// </remarks>
    public async Task<bool> RequestClipboardAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = _sessions.TryGet(sessionId);

        if (session is null) return false;

        var connectionId = _hosts.ConnectionIdOf(session.AgentId);

        if (string.IsNullOrEmpty(connectionId))
        {
            _log.Warn("Remote", $"[Remote] 会话 {sessionId} 所属机器 #{session.AgentId} 已掉线，读不到剪贴板。");

            return false;
        }

        var command = new RemoteSessionCommandDto(
            Action: RemoteSessionActions.Clipboard,
            SessionId: sessionId,
            ClipboardAction: RemoteClipboardActions.Get);

        var sent = await SendAsync(connectionId, session.AgentId, command, cancellationToken)
            .ConfigureAwait(false);

        if (!sent.Ok) _log.Warn("Remote", $"[Remote] 会话 {sessionId} 的剪贴板读取指令下发失败：{sent.Message}");

        return sent.Ok;
    }

    // ═══════════════════════════════════════════════════════════
    // 输入回传（M5.2）
    // ═══════════════════════════════════════════════════════════
    /// <summary>
    /// 把一批输入送进某一路会话所对应的机器。
    /// </summary>
    /// <remarks>
    /// 🔥 <b>它是本方案风险最高的一条路径</b>（方案里唯一"直接操作别人的鼠标键盘"的动作），
    /// 所以这里的四道判断<b>一道都不能省，而且顺序不能换</b>：
    /// <list type="number">
    ///   <item><b>会话在不在</b> —— 不在就没什么可注入的；</item>
    ///   <item><b>闸门开没开</b>（<c>InputEnabled</c>，默认关）——
    ///         <b>必须在取机器 Id 之前判</b>：否则一个没打开控制权的会话
    ///         也会走到"下发"那一步，而那里会把 <c>sessionId</c> 与机器绑起来
    ///         —— 一次本来该被挡住的调用留下了可观测的痕迹；</item>
    ///   <item><b>批次大小</b> —— 防一个坏掉的调用方一次塞十万条；</item>
    ///   <item><b>机器是不是还连着</b> —— 交给中继判（它手上有在线表）。</item>
    /// </list>
    /// <para>
    /// ⚠️ <b>它永远返回一个回执，不抛</b>：输入失败不是"异常情况"，
    /// 而是这一类功能最常见的结果之一（没开开关 / 被控端是旧版）。
    /// 抛异常会让这个"常见结果"变成一个 500，而页面上就只剩"请求失败"这种没信息量的话。
    /// </para>
    /// </remarks>
    public async Task<RemoteInputAckDto> SendInputAsync(
        string sessionId,
        IReadOnlyList<RemoteInputEventDto> events,
        CancellationToken cancellationToken)
    {
        var session = _sessions.TryGet(sessionId);

        if (session is null)
        {
            return new RemoteInputAckDto(
                false,
                RemoteErrorCodes.SessionNotFound,
                $"不存在会话「{sessionId}」（可能已结束）。",
                0);
        }

        if (!session.InputEnabled)
        {
            return new RemoteInputAckDto(
                false,
                RemoteErrorCodes.InputNotEnabled,
                "这一路会话还没有打开输入回传 —— 默认只能看，不能控。请先打开页面上的「允许控制」。",
                0);
        }

        if (events is null || events.Count == 0)
        {
            // 空批算成功（前端节流时可能发个空的），不算错误。
            return new RemoteInputAckDto(true, null, "没有事件。", 0);
        }

        if (events.Count > RemoteInputLimits.MaxEventsPerBatch)
        {
            return new RemoteInputAckDto(
                false,
                RemoteErrorCodes.InputTooLarge,
                $"一批 {events.Count} 条输入超过上限 {RemoteInputLimits.MaxEventsPerBatch} 条。",
                0);
        }

        return await _input.SendAsync(sessionId, session.AgentId, events, cancellationToken)
            .ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════
    // 下发
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把一条远控指令塞进该机的长连接。
    /// </summary>
    /// <remarks>
    /// 🔥 <b>走 <c>AgentHubMethods.Command</c> 而不是新开一个 Hub 方法</b>：
    /// SignalR 的实参个数必须与签名严格一致（有实测证据，见
    /// <c>AgentHubClientMethods.Heartbeat</c> 的备注），
    /// 而 <c>AgentCommandDto</c> 本来就是"名字 + 参数"的通用信封 ——
    /// 加一个新指令名不会影响任何旧客户端（它们会回一句"本机不处理的指令"）。
    /// </remarks>
    private async Task<(bool Ok, string? Code, string? Message)> SendAsync(
        string connectionId,
        int agentId,
        RemoteSessionCommandDto command,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = JsonSerializer.Serialize(command, Json);

            await _agentHub.Clients.Client(connectionId)
                .SendAsync(
                    AgentHubMethods.Command,
                    new AgentCommandDto(AgentCommandNames.RemoteSession, payload),
                    cancellationToken)
                .ConfigureAwait(false);

            return (true, null, null);
        }
        catch (OperationCanceledException)
        {
            // 观众把页面关了之类：不是故障，别记成错误。
            return (false, RemoteErrorCodes.StartFailed, "请求已取消。");
        }
        catch (Exception ex)
        {
            _log.Error("Remote", $"[Remote] 给机器 #{agentId} 下发远控指令（{command.Action}）失败", ex);

            return (false, RemoteErrorCodes.StartFailed, $"给客户端下发指令失败：{ex.Message}");
        }
    }
}
