using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using ControlServer.Agent.Config;
using ControlServer.Agent.Tasks;
using ControlServer.Contracts;
using ControlServer.Core.Services;

namespace ControlServer.Agent.Remote;

/// <summary>
/// <b>远控会话的中枢</b>（M5.1）—— 跑在<b>客户机的服务进程</b>里。
///
/// <para>
/// 🔥 <b>它是这条链路上唯一的"承上启下"处，位置不是随便放的</b>：
/// </para>
/// <list type="bullet">
///   <item><b>向上</b>：中心的指令经 SignalR 落到服务进程（服务持有凭据与长连接）；</item>
///   <item><b>向下</b>：真正抓屏/编码在<b>托盘</b>（只有它有桌面），服务经命名管道把指令转过去、
///         再把码流接回来。</item>
/// </list>
///
/// <para>
/// ⚠️ <b>为什么不把托盘直接接到中心</b>：托盘刻意<b>不持有任何凭据</b>
/// （连 <c>status.json</c> 里都没有 agentKey）。给它发一份凭据去连中心，
/// 等于把 M4 那套"凭据只在服务进程里"的设计推翻 —— 代价远大于多一跳本机管道。
/// </para>
///
/// <para><b>三个动作三种节奏，都在这里收口</b>：</para>
/// <list type="number">
///   <item><b>开始</b>：转指令给托盘 → 立刻起一个接流任务 → 先报 <c>requested</c>，
///         收到第一帧时再报 <c>streaming</c>；</item>
///   <item><b>停止</b>：取消接流任务 → 转指令给托盘；</item>
///   <item><b>黑白</b>：只转一条指令（不重建会话）。</item>
/// </list>
///
/// <para>
/// 🔥 <b>上报是"必须可靠"的那一类</b>：被控端"起了但没起来"时<b>一帧都不会有</b>，
/// 而中心只有靠状态上报才知道是失败而不是还在连接。
/// 所以"上报"与"推帧"要分开看 —— 推帧可以丢，状态不能丢。
/// </para>
/// </summary>
public sealed class RemoteSessionCoordinator : IAsyncDisposable
{
    /// <summary>
    /// 指令载荷的 JSON 约定。
    /// <para>
    /// ⚠️ <b>不复用 <c>ControlJson.Options</c></b>：那是给配置文件用的（缩进、宽松命名），
    /// 这里要的是"能读中心那头 camelCase 的紧凑载荷"，大小写必须不敏感
    /// （两边的命名策略一旦有一个变了，这里会在运行期静默反序列化成一堆 null）。
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ISessionCommandChannel _session;
    private readonly ISessionStreamChannel _stream;
    private readonly ISessionInputChannel _input;
    private readonly ISessionAudioChannel _audio;
    private readonly AgentInputPolicy _inputPolicy;
    private readonly LogService _log;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();

    private Active? _active;
    private bool _disposed;

    public RemoteSessionCoordinator(
        ISessionCommandChannel session,
        ISessionStreamChannel stream,
        ISessionInputChannel input,
        ISessionAudioChannel audio,
        AgentInputPolicy inputPolicy,
        LogService log)
    {
        _session = session;
        _stream = stream;
        _input = input;
        _audio = audio;
        _inputPolicy = inputPolicy;
        _log = log;
    }

    /// <summary>
    /// 往中心送一帧。<b>由 <c>AgentRuntime</c> 在每次连上时赋值</b>。
    /// <para>
    /// 做成可替换的委托，是因为"当前那条长连接"本身是会换的（断线重连）；
    /// 让本类持有连接对象，就得处理"我手里这个还能不能用"，而那个问题
    /// <c>AgentRuntime</c> 本来就一直在回答。
    /// </para>
    /// <para><b>为 <c>null</c> = 中心不在</b>。此时收到帧必须让流停下来，而不是白烧 CPU。</para>
    /// </summary>
    public Func<RemoteStreamFrame, CancellationToken, Task>? FrameSink { get; set; }

    /// <summary>
    /// 往中心送一段音频（M5.3）。<c>null</c> = 中心不在。
    /// <para>
    /// ⚠️ 与 <see cref="FrameSink"/> <b>分开</b>：它们是两条独立的上行，
    /// 共用一个委托会让"音频送不出去"与"画面送不出去"变成同一件事，
    /// 而处置完全不同（一个去查声卡，一个去查编码）。
    /// </para>
    /// </summary>
    public Func<RemoteAudioFrame, CancellationToken, Task>? AudioSink { get; set; }

    /// <summary>往中心送一条会话状态。<c>null</c> = 中心不在（那就只记日志）。</summary>
    public Func<AgentRemoteSessionStateDto, Task>? StateSink { get; set; }

    /// <summary>
    /// <b>屏清单的出口</b>（M5.5）：托盘答出清单之后，由这里送回中心。
    /// <para>
    /// ⚠️ 它**不是**回执：中心 → 客户机的指令是 fire-and-forget（见
    /// <c>RemoteSessionService</c> 的类注释），所以"答案"必须另走一条上行。
    /// </para>
    /// </summary>
    public Func<AgentScreensDto, Task>? ScreensSink { get; set; }

    /// <summary>
    /// <b>剪贴板内容的出口</b>（M5.5）：读到被控机的剪贴板之后，由这里送回中心。
    /// <para>⚠️ 与 <see cref="ScreensSink"/> 同一条理由：那也是"答案"，不是回执。</para>
    /// </summary>
    public Func<RemoteClipboardDto, Task>? ClipboardSink { get; set; }

    /// <summary>此刻在推的会话 Id；没有则 <c>null</c>。</summary>
    public string? ActiveSessionId
    {
        get
        {
            lock (_gate) return _active?.SessionId;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 入口
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 执行一条来自中心的远控指令。<b>不抛</b>（异常会被转成失败结论）。
    /// </summary>
    /// <param name="argument"><see cref="RemoteSessionCommandDto"/> 的 JSON。</param>
    /// <param name="logCategory">日志分类（沿用调用方那一套）。</param>
    public async Task<AgentCommandOutcome> ExecuteAsync(
        string? argument,
        string logCategory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return new AgentCommandOutcome(AgentReceivedOutcomes.Failed, "远控指令缺少参数。");
        }

        RemoteSessionCommandDto? command;

        try
        {
            command = JsonSerializer.Deserialize<RemoteSessionCommandDto>(argument, Json);
        }
        catch (JsonException ex)
        {
            return new AgentCommandOutcome(AgentReceivedOutcomes.Failed, $"远控指令不是合法 JSON：{ex.Message}");
        }

        if (command is null) return new AgentCommandOutcome(AgentReceivedOutcomes.Failed, "远控指令为空。");

        if (string.IsNullOrWhiteSpace(command.SessionId))
        {
            return new AgentCommandOutcome(AgentReceivedOutcomes.Failed, "远控指令缺少会话 Id。");
        }

        try
        {
            return command.Action switch
            {
                RemoteSessionActions.Start => await StartAsync(command, logCategory, cancellationToken)
                    .ConfigureAwait(false),

                RemoteSessionActions.Stop => await StopAsync(command.SessionId, logCategory, cancellationToken)
                    .ConfigureAwait(false),

                RemoteSessionActions.Grayscale => await GrayscaleAsync(command, logCategory, cancellationToken)
                    .ConfigureAwait(false),

                // M5.5：运行时改质量档（帧率 + 灰度即时生效；码率要重建，见托盘的 SetQuality）。
                RemoteSessionActions.Quality => await QualityAsync(command, logCategory, cancellationToken)
                    .ConfigureAwait(false),

                // M5.5：屏清单 —— 答案不回给中心（这条链路的回执止步于本进程），
                // 而是由本类**主动上行**（ScreensSink）。
                RemoteSessionActions.Screens => await ScreensAsync(command, logCategory, cancellationToken)
                    .ConfigureAwait(false),

                // M5.5：剪贴板读/写。读的答案同样走上行（ClipboardSink）。
                RemoteSessionActions.Clipboard => await ClipboardAsync(command, logCategory, cancellationToken)
                    .ConfigureAwait(false),

                // M5.5：只编视口那一块（ROI）。这条**没有上行**：它就是一次设置，
                // 回执里那句"改了多大一块"已经足够（中心据此更新会话状态）。
                RemoteSessionActions.Viewport => await ViewportAsync(command, logCategory, cancellationToken)
                    .ConfigureAwait(false),

                _ => new AgentCommandOutcome(
                    AgentReceivedOutcomes.Failed,
                    $"不认识的远控动作「{command.Action ?? "(空)"}」（中心与客户端版本可能不一致）。"),
            };
        }
        catch (Exception ex)
        {
            // ⚠️ 绝不外抛：这里是 SignalR 回调的调用栈，抛出去会让整条长连接静默死掉。
            _log.Error(logCategory, $"执行远控指令「{command.Action}」时出错", ex);

            await ReportAsync(new AgentRemoteSessionStateDto(
                command.SessionId, RemoteSessionStates.Failed, $"执行远控指令时出错：{ex.Message}", 0, 0, 0, 0))
                .ConfigureAwait(false);

            return new AgentCommandOutcome(AgentReceivedOutcomes.Failed, $"执行远控指令时出错：{ex.Message}");
        }
    }

    /// <summary>
    /// 与中心的长连接断了。<b>由 <c>AgentRuntime</c> 调用</b>。
    /// <para>
    /// 🔥 <b>必须停掉正在推的流</b>：码流送不出去，而被控端会一直抓屏、一直编码 ——
    /// 表现是"中心显示着离线的机器，可它的 CPU 一直是满的"。等重连成功，
    /// 中心重新建会话即可（一帧都不会少看到，因为旧的也已经没人收）。
    /// </para>
    /// </summary>
    public void OnDisconnected()
    {
        Active? active;

        lock (_gate)
        {
            active = _active;
            _active = null;
        }

        if (active is null) return;

        _log.Warn("Remote", $"[Remote] 与中心断开，已停止会话 {active.SessionId} 的推流（帧已无处可送）。");

        active.Cancel();

        // 顺手请托盘也停：不给它留一条"没人接的管子在那儿等"。
        _ = AskTrayToStopAsync(active.SessionId);
    }

    // ═══════════════════════════════════════════════════════════
    // 三个动作
    // ═══════════════════════════════════════════════════════════

    private async Task<AgentCommandOutcome> StartAsync(
        RemoteSessionCommandDto command,
        string logCategory,
        CancellationToken cancellationToken)
    {
        Active active;

        // 🔒 本机否决**在进锁之前读一次**：这个值决定"要不要起输入泵"，
        //    而它是一件"本机自己的事"，跟会话登记之间没有先后依赖。
        //    （放在锁里读也不出错，只是让 _gate 多持有一会儿文件系统调用的时间。）
        var inputAllowed = _inputPolicy.AllowsInput;

        // ── ⓪ 已经有一路在推时：是「重复指令」还是「上一路成了孤儿」？ ──
        //
        // 🔥🔥 **"M5.1 只允许一路"不等于"拒绝新的那一路"**（2026-10-19 现场定案）。
        //
        //    中心对每台机器同样只允许一路（`RemoteSessionRegistry` 的 `SessionBusy`），
        //    所以"同一台机器上又来了一个**不同 id** 的 start"只有一个解释：
        //    中心那边**已经把旧会话关账了**（掉线超时 / 页面关了 / 管理员结束），
        //    而那条 stop 没有到本机。
        //
        //    继续拒绝的后果不是"少一路会话"，而是**这台机器在托盘重启之前彻底远控不了**：
        //    <code>
        //    09:18:22  中心：会话 091729 已结束（15 秒没收到任何帧与状态）
        //    09:18:34  本机：已有会话「091729」在推流 → 拒绝
        //    09:18:55 / 09:19:18 / 09:19:45  同上（四次）
        //    09:28:46  才终于开起来（本机自己的空闲超时把旧那路收了）
        //    </code>
        //    而页面上那句话是"没人登录 / 托盘没在跑 / 客户端版本过旧" —— 三条都不对。
        //
        //    ⇒ 以中心的指令为准：**停掉旧的、起新的**（中心是唯一的下令方，
        //      它说"开一路新的"，就意味着它认为旧的已经不存在了）。
        //      这一步只能在锁外做 —— 停旧的要把指令发给托盘并等它回。
        Active? running;
        lock (_gate) { running = _active; }

        var admission = RemoteSessionAdmission.Decide(running?.SessionId, command.SessionId);

        if (admission == RemoteSessionAdmissionDecision.Duplicate)
        {
            // 同一路重复下发（中心重连后重试）：幂等。
            // ⚠️ 这里**不许**"停掉再重建"：那会把刚建好的编码器拆了重来，
            //    而用户看到的是"画面黑一下、然后重新等首帧"。
            return new AgentCommandOutcome(
                AgentReceivedOutcomes.Accepted,
                $"会话 {command.SessionId} 已经在推流（重复指令，未做改动）。");
        }

        if (admission == RemoteSessionAdmissionDecision.SupersedeAndStart && running is not null)
        {
            _log.Warn(
                logCategory,
                RemoteSessionAdmission.DescribeSupersede(running.SessionId, command.SessionId));

            await StopAsync(running.SessionId, logCategory, cancellationToken).ConfigureAwait(false);
        }

        string? reject;

        lock (_gate)
        {
            // ⚠️ 兜底：上面刚停过，正常情况下这里一定为 null。
            //    真不为 null（并发下发）时只能如实说"忙"，但**这条结论必须报给中心** ——
            //    否则中心会在 20 秒后给出"没人登录 / 托盘没在跑"这种完全指错方向的解释。
            reject = _disposed
                ? "客户端正在退出。"
                : _active is not null
                    ? $"本机已有会话「{_active.SessionId}」在推流（M5.1 只允许一路）。"
                    : null;

            active = reject is null
                ? new Active(command.SessionId, _lifetime.Token)
                : null!;

            if (reject is null)
            {
                // 🔥 输入状态**在登记那一刻就定成 connecting**，而不是等输入泵起来再设：
                //    紧接着那条 requested 上报要把这个字段带出去，晚设一步就会让它
                //    带着 null 出去 —— 而 null 在契约里的含义是"这台机器压根没提过输入"
                //    （旧版客户端）。同一条消息，早 1 毫秒发就是一个完全不同的结论。
                //
                // ⚠️ 但"本机已否决"是一个**当场就知道**的结论，所以它直接盖掉 connecting：
                //    对外说"正在连"等于把一件已经定下来的事说成还没定，用户会去等一个
                //    永远不会来的结果。见 AgentInputPolicy。
                active.SetInputState(
                    inputAllowed ? AgentInputStates.Connecting : AgentInputStates.Unavailable,
                    inputAllowed ? null : DescribePolicyDenial());

                // 🔥 音频同理：**在登记那一刻就定成 connecting**。
                //    音频没有"本机否决"这一层（有声音是这路会话的默认行为），
                //    所以这里无条件写 connecting —— 真正的结论（有没有声卡）
                //    由音频泵问过托盘之后覆盖，见 AudioPumpAsync。
                active.SetAudioState(AgentAudioStates.Connecting, null);

                _active = active;
            }
        }

        if (reject is not null)
        {
            if (!_disposed)
            {
                // 🔥 报出去，让别人看见真原因（原来这里只回一句本地失败，中心永远收不到）。
                await ReportAsync(new AgentRemoteSessionStateDto(
                    command.SessionId, RemoteSessionStates.Failed, reject, 0, 0, 0, 0))
                    .ConfigureAwait(false);
            }

            return new AgentCommandOutcome(AgentReceivedOutcomes.Failed, reject);
        }

        // ── ①′ 先报一次 requested：「指令已经到了本机，正在建会话」──
        //
        // 🔥🔥 **这条上报的时机就是本轮修掉的那个挂账本身。**
        //
        //    它原先在下面 `_session.SendAsync(...)` **之后**，而那个调用要等托盘把
        //    抓屏器、D3D 设备、硬编 MFT **全部建完**才返回 —— 在多显示器、
        //    首次加载 GPU 编码器的机器上，这段时间完全可能超过中心那个
        //    `RemoteSessionRegistry.RequestedTimeout`（20 秒）。
        //
        //    于是现场会看到一种很难查的组合：**中心报"建会话超时"，而被控端日志一切正常**
        //    （它确实建好了，只是晚了几秒）。两边的日志都不算说谎，因为"超时"在中心那边
        //    的真实含义是"20 秒没听见任何回话"，而 20 秒里被控端正在建、还没报。
        //
        //    挪到 SendAsync 之前，中心那条判据才回到它注释里写的本意：
        //    **"几十毫秒内有没有回话"**，用来区分「指令没到」与「正在建」。
        //    "建不出来"则由托盘自己的失败回执（下面 ②）与 30 秒首帧预算各自负责。
        //
        // ⚠️ 报早了会不会撒谎？不会：requested 的契约就是"已请求、尚未推流"。
        //    若托盘随后拒绝，下面 ② 会把它改成 failed —— 那是允许的转移，
        //    而且比"20 秒空白之后再报失败"更接近事实。
        active.ReportedState = RemoteSessionStates.Requested;

        await ReportAsync(new AgentRemoteSessionStateDto(
            command.SessionId,
            RemoteSessionStates.Requested,
            "正在建立串流会话（抓屏器 / 编码器初始化中）…",
            0,
            0,
            0,
            0,
            active.InputState,
            active.AudioState,
            active.AudioReason)).ConfigureAwait(false);

        // ── ① 请托盘开始抓屏推流 ──
        //
        // 🔥 第 7 步起把**中心协商出来的格式集合与封装**一起透传下去。
        //    此前这两件事在协议里已经有位置、但**没有任何一处写过它们** ——
        //    于是第 6 步做好的"带内 / 带外"两条路，从中心根本够不着（永远走默认）。
        var payload = JsonSerializer.Serialize(
            new RemoteStreamRequest(
                Action: RemoteSessionActions.Start,
                SessionId: command.SessionId,
                ScreenId: command.ScreenId,
                MaxFps: command.MaxFps,
                Packaging: command.Packaging,
                Codecs: command.Codecs),
            Json);

        var started = await _session.SendAsync(
            new SessionCommandRequest(SessionCommandMethods.RemoteStream, payload),
            cancellationToken).ConfigureAwait(false);

        if (!started.Ok)
        {
            // 🔥 这一句会直接进页面。它最常见的形态是"会话侧不可用"（没人登录 / 托盘没跑），
            //    而那**不是软件故障** —— 所以文案必须把话说清，不能只说"启动失败"。
            lock (_gate)
            {
                if (ReferenceEquals(_active, active)) _active = null;
            }

            var reason = _session.IsAvailable
                ? $"被控端拒绝开始推流：{started.Message}"
                : $"被控端此刻没有可用的会话侧（{started.Message}）—— "
                  + "抓屏只能在有人登录的桌面里做，请确认这台机器已登录且托盘在运行。";

            _log.Warn(logCategory, $"[Remote] 会话 {command.SessionId} 启动失败：{reason}");

            await ReportAsync(new AgentRemoteSessionStateDto(
                command.SessionId, RemoteSessionStates.Failed, reason, 0, 0, 0, 0)).ConfigureAwait(false);

            return new AgentCommandOutcome(AgentReceivedOutcomes.Failed, reason);
        }

        // ── ② 再报一次 requested，这次带上「格式裁决说明」──
        //
        // ⚠️ 与 ①′ 那次**不是重复**：`CodecNote`（"首选 A、实际用了 B、为什么"）
        //    只有托盘建完之后才知道，而它必须随每一次状态上报带出去。
        //    中心收到这一次会刷新 `LastAgentAt`，页面上的文案也从"初始化中"
        //    变成真正的裁决结果。
        //
        // ⚠️ 它与 streaming 必须分开（见 RemoteSessionStates 的注释）：
        //    合成一个"开启中"，现场就只能看到"点了没反应"。
        active.ReportedState = RemoteSessionStates.Requested;

        // 🔥 第 7 步：把托盘的**格式裁决说明**留下来，随每一次状态上报带出去。
        //    它是"中心首选 A、实际用了 B、为什么"的唯一通道 ——
        //    而中心自己推不出来（裁决发生在本机挑屏之后）。见 AgentRemoteSessionStateDto.CodecNote。
        active.CodecNote = started.CodecNote;

        if (active.CodecNote is not null)
        {
            _log.Info(logCategory, $"[Remote] 会话 {command.SessionId} 的编码格式裁决：{active.CodecNote}");
        }

        await ReportAsync(new AgentRemoteSessionStateDto(
            command.SessionId,
            RemoteSessionStates.Requested,
            started.Message,
            0,
            0,
            0,
            0,
            active.InputState,
            active.AudioState,
            active.AudioReason,
            CodecNote: active.CodecNote)).ConfigureAwait(false);

        // ── ③ 起接流任务（不 await：它要一直跑到这路流结束）──
        active.Pump = Task.Run(() => PumpAsync(active, logCategory), CancellationToken.None);

        // ── ③′ 并行起音频通道（M5.3）──
        //
        // 🔥 与输入同样的理由：**会话一起来就接**，不等中心"打开声音"（也没有那个开关）。
        //    它给中心两个当场答案：声音能不能到、到不了是为什么。
        //    ⚠️ 而它**不影响画面** —— 这是独立任务，音频失败只让"声音"那一格变成不可用。
        active.AudioPump = Task.Run(() => AudioPumpAsync(active, logCategory), CancellationToken.None);

        // ── ④ 并行起输入通道（M5.2）──
        //
        // 🔥 **它是"会话一起来就接"，不是"等中心打开控制开关才接"**。
        //    理由有两条：
        //      · 打开开关那一刻用户就在等着点鼠标 —— 让他在那一刻再等一次建连，
        //        体验上是"点了开关还要愣一秒才动"；
        //      · 更重要的是它给了中心一个**如实的能力答案**：
        //        接不上（旧托盘没建这条管道）就当场报 <c>unavailable</c>，
        //        而不是等用户点了半天才怀疑。
        //    代价只是一条本机管道 + 一个空闲的读循环，可以忽略。
        //
        // 🔒 但**本机否决时连试都不试**（这是否决，不是"试了没成功"）：
        //    不去建连、不去等那 15 秒超时，状态已经在上面登记那一刻定成 unavailable 了。
        //    托盘那边同样不建管道 —— 两道都拦，任何一道单独失效都不会变成"能注进去了"。
        if (inputAllowed)
        {
            active.InputPump = Task.Run(() => InputPumpAsync(active, logCategory), CancellationToken.None);
        }
        else
        {
            _log.Warn(logCategory, $"[Remote] 会话 {command.SessionId} 不起输入通道：{DescribePolicyDenial()}");
        }

        _log.Info(logCategory, $"[Remote] 会话 {command.SessionId} 已开始：{started.Message}");

        return new AgentCommandOutcome(AgentReceivedOutcomes.Executed, started.Message);
    }

    /// <summary>
    /// "本机否决了远程输入"这句话（一份文案，两处用：状态上报与人读的日志）。
    /// <para>
    /// ⚠️ 文案要给出**怎么改**，否则现场看到"输入不可用"只会去查网络 / 驱动 / 服务。
    /// </para>
    /// </summary>
    private string DescribePolicyDenial()
        => $"这台机器本机已禁止远程键鼠注入（{_inputPolicy.FilePath}存在即表示禁止，"
           + "删掉该文件即可恢复）。这是被控机自己的决定，中心无法覆盖。";

    private async Task<AgentCommandOutcome> StopAsync(
        string sessionId,
        string logCategory,
        CancellationToken cancellationToken)
    {
        Active? active;

        lock (_gate)
        {
            active = _active;

            if (active is not null && string.Equals(active.SessionId, sessionId, StringComparison.Ordinal))
            {
                _active = null;
            }
            else
            {
                // 请求停的不是当前这一路：不拦，但仍然把停止指令转下去（幂等），
                // 因为托盘那边可能还在等接收端（那种状态下本机已经"有会话"了）。
                active = null;
            }
        }

        active?.Cancel();

        var payload = JsonSerializer.Serialize(
            new RemoteStreamRequest(RemoteSessionActions.Stop, sessionId),
            Json);

        // 🔥 停止**不因为通道不可用就报失败**：真正要保证的是"被控端不再推流"，
        //    而这件事在"托盘本来就没了"的时候已经成立了。
        var stopped = await _session.SendAsync(
            new SessionCommandRequest(SessionCommandMethods.RemoteStream, payload),
            cancellationToken).ConfigureAwait(false);

        var message = stopped.Ok
            ? stopped.Message
            : $"已在本机取消推流；托盘侧未确认（{stopped.Message}）。";

        _log.Info(logCategory, $"[Remote] 会话 {sessionId} 停止：{message}");

        await ReportAsync(NewState(sessionId, active, RemoteSessionStates.Stopped, message))
            .ConfigureAwait(false);

        return new AgentCommandOutcome(AgentReceivedOutcomes.Executed, message);
    }

    private async Task<AgentCommandOutcome> GrayscaleAsync(
        RemoteSessionCommandDto command,
        string logCategory,
        CancellationToken cancellationToken)
    {
        var enabled = command.Grayscale ?? false;

        var payload = JsonSerializer.Serialize(
            new RemoteStreamRequest(
                Action: RemoteSessionActions.Grayscale,
                SessionId: command.SessionId,
                Enabled: enabled),
            Json);

        var result = await _session.SendAsync(
            new SessionCommandRequest(SessionCommandMethods.RemoteStream, payload),
            cancellationToken).ConfigureAwait(false);

        _log.Info(logCategory, $"[Remote] 会话 {command.SessionId} 黑白={(enabled ? "开" : "关")}：{result.Message}");

        return result.Ok
            ? new AgentCommandOutcome(AgentReceivedOutcomes.Executed, result.Message)
            : new AgentCommandOutcome(AgentReceivedOutcomes.Failed, result.Message);
    }

    /// <summary>
    /// 运行时改质量档（M5.5）：把码率 / 帧率 / 灰度一起交给托盘。
    /// </summary>
    /// <remarks>
    /// 🔥 <b>返回值里那句话是"实际改了什么"的账单</b>（托盘的 <c>SetQuality</c> 会逐项说明），
    /// 它经 <c>AgentCommandOutcome</c> 进"接收到的数据"面板 ——
    /// 现场排查"切了低画质为什么码率没降"时，第一眼要看到的就是它。
    /// </remarks>
    private async Task<AgentCommandOutcome> QualityAsync(
        RemoteSessionCommandDto command,
        string logCategory,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new RemoteStreamRequest(
                Action: RemoteSessionActions.Quality,
                SessionId: command.SessionId,
                MaxFps: command.MaxFps,
                Enabled: command.Grayscale,
                Quality: command.Quality,
                BitrateKbps: command.BitrateKbps),
            Json);

        var result = await _session.SendAsync(
            new SessionCommandRequest(SessionCommandMethods.RemoteStream, payload),
            cancellationToken).ConfigureAwait(false);

        _log.Info(logCategory, $"[Remote] 会话 {command.SessionId} 质量切换：{result.Message}");

        return result.Ok
            ? new AgentCommandOutcome(AgentReceivedOutcomes.Executed, result.Message)
            : new AgentCommandOutcome(AgentReceivedOutcomes.Failed, result.Message);
    }

    /// <summary>
    /// <b>只编视口那一块</b>（M5.5 第 5 项：ROI 编码）。
    /// </summary>
    /// <remarks>
    /// 🔥 <b>它是"转发 + 回报账单"，不是"中心自己算"</b>：视口坐标必须由
    /// <b>发起方</b>（页面）给，因为"用户现在看到的是哪一块"只有那一侧知道。
    /// 中心转发时<b>不做任何换算</b>（像素坐标一路到底）——
    /// 中间插一层缩放/裁边，就会出现"滚到右边时画面偏一格"这种查不出来源的问题。
    ///
    /// <para>
    /// ⚠️ 与 <c>Quality</c> 一样，回执里的那句话是"实际改了什么"，会进"接收到的数据"面板。
    /// </para>
    /// </remarks>
    private async Task<AgentCommandOutcome> ViewportAsync(
        RemoteSessionCommandDto command,
        string logCategory,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new RemoteStreamRequest(
                Action: RemoteSessionActions.Viewport,
                SessionId: command.SessionId,
                ViewportX: command.ViewportX,
                ViewportY: command.ViewportY,
                ViewportWidth: command.ViewportWidth,
                ViewportHeight: command.ViewportHeight),
            Json);

        var result = await _session.SendAsync(
            new SessionCommandRequest(SessionCommandMethods.RemoteStream, payload),
            cancellationToken).ConfigureAwait(false);

        _log.Info(logCategory, $"[Remote] 会话 {command.SessionId} 视口切换：{result.Message}");

        return result.Ok
            ? new AgentCommandOutcome(AgentReceivedOutcomes.Executed, result.Message)
            : new AgentCommandOutcome(AgentReceivedOutcomes.Failed, result.Message);
    }

    /// <summary>
    /// 索取屏清单并<b>上行</b>（M5.5）。
    /// </summary>
    /// <remarks>
    /// 🔥🔴 <b>这里最容易犯的错：把这句当成"已经回答了中心"。</b>
    /// 这条指令的回执只回到本进程（<c>AgentCommandOutcome</c> 进的是"接收到的数据"面板），
    /// 中心看不到。所以拿到清单之后<b>必须</b>调 <see cref="ScreensSink"/> ——
    /// 否则页面上永远显示"正在索取屏清单"，而两边的日志都写着"成功"。
    /// </remarks>
    private async Task<AgentCommandOutcome> ScreensAsync(
        RemoteSessionCommandDto command,
        string logCategory,
        CancellationToken cancellationToken)
    {
        // ⚠️ 不校验 command.SessionId：这条指令本来就**不针对**某一路会话
        //    （中心用 RemoteScreenRequests.NoSessionId 占位）。
        var result = await _session.SendAsync(
            new SessionCommandRequest(SessionCommandMethods.Screens),
            cancellationToken).ConfigureAwait(false);

        if (!result.Ok)
        {
            _log.Warn(logCategory, $"[Remote] 取屏清单失败：{result.Message}");

            return new AgentCommandOutcome(AgentReceivedOutcomes.Failed, result.Message);
        }

        if (string.IsNullOrWhiteSpace(result.Payload))
        {
            // ⚠️ 成功却没有载荷 = 两端版本不一致（旧托盘只会回一句 message）。
            //    如实说"没有清单"，而不是回一个空列表 —— 空列表的含义是"这台机器没有显示器"。
            return new AgentCommandOutcome(
                AgentReceivedOutcomes.Failed,
                "被控端回了成功，但没有带屏清单（托盘版本可能过旧）。");
        }

        AgentScreensDto? screens;

        try
        {
            screens = JsonSerializer.Deserialize<AgentScreensDto>(result.Payload, Json);
        }
        catch (JsonException ex)
        {
            _log.Warn(logCategory, $"[Remote] 屏清单载荷解析失败：{ex.Message}");

            return new AgentCommandOutcome(
                AgentReceivedOutcomes.Failed,
                $"屏清单载荷解析失败：{ex.Message}");
        }

        if (screens is null)
        {
            return new AgentCommandOutcome(AgentReceivedOutcomes.Failed, "屏清单载荷为空。");
        }

        if (ScreensSink is { } sink)
        {
            await sink(screens).ConfigureAwait(false);
        }
        else
        {
            _log.Warn(logCategory, "[Remote] 没有挂屏清单的上行出口 —— 清单只留在本机日志里。");
        }

        _log.Info(logCategory, $"[Remote] 屏清单：{screens.Screens.Count} 项（已上行）。");

        return new AgentCommandOutcome(
            AgentReceivedOutcomes.Executed,
            $"屏清单 {screens.Screens.Count} 项（已上报中心）。");
    }

    /// <summary>
    /// 读写被控机剪贴板（M5.5）。<b>读的答案走上行</b>。
    /// </summary>
    private async Task<AgentCommandOutcome> ClipboardAsync(
        RemoteSessionCommandDto command,
        string logCategory,
        CancellationToken cancellationToken)
    {
        var action = command.ClipboardAction ?? RemoteClipboardActions.Get;

        var payload = JsonSerializer.Serialize(
            new RemoteStreamRequest(
                Action: RemoteSessionActions.Clipboard,
                SessionId: command.SessionId,
                ClipboardAction: action,
                ClipboardText: command.ClipboardText),
            Json);

        var result = await _session.SendAsync(
            new SessionCommandRequest(SessionCommandMethods.Clipboard, payload),
            cancellationToken).ConfigureAwait(false);

        if (!result.Ok)
        {
            _log.Warn(logCategory, $"[Remote] 剪贴板（{action}）失败：{result.Message}");

            return new AgentCommandOutcome(AgentReceivedOutcomes.Failed, result.Message);
        }

        if (!string.Equals(action, RemoteClipboardActions.Get, StringComparison.OrdinalIgnoreCase))
        {
            // 写入：回执就是"写进去了没有"，这句话本身有价值（它会进接收数据面板）。
            _log.Info(logCategory, $"[Remote] 剪贴板写入：{result.Message}");

            return new AgentCommandOutcome(AgentReceivedOutcomes.Executed, result.Message);
        }

        if (string.IsNullOrWhiteSpace(result.Payload))
        {
            return new AgentCommandOutcome(
                AgentReceivedOutcomes.Failed,
                "被控端回了成功，但没有带剪贴板内容（托盘版本可能过旧）。");
        }

        RemoteClipboardDto? clipboard;

        try
        {
            clipboard = JsonSerializer.Deserialize<RemoteClipboardDto>(result.Payload, Json);
        }
        catch (JsonException ex)
        {
            _log.Warn(logCategory, $"[Remote] 剪贴板载荷解析失败：{ex.Message}");

            return new AgentCommandOutcome(
                AgentReceivedOutcomes.Failed,
                $"剪贴板载荷解析失败：{ex.Message}");
        }

        if (clipboard is null)
        {
            return new AgentCommandOutcome(AgentReceivedOutcomes.Failed, "剪贴板载荷为空。");
        }

        if (ClipboardSink is { } sink)
        {
            await sink(clipboard).ConfigureAwait(false);
        }
        else
        {
            _log.Warn(logCategory, "[Remote] 没有挂剪贴板的上行出口 —— 内容只留在本机日志里。");
        }

        _log.Info(
            logCategory,
            $"[Remote] 剪贴板读取：{(clipboard.Text is null ? "没有文本" : $"{clipboard.Text.Length} 个字符")}"
            + (clipboard.Note is null ? "" : $"（{clipboard.Note}）"));

        return new AgentCommandOutcome(
            AgentReceivedOutcomes.Executed,
            clipboard.Text is null ? "剪贴板里没有文本（已上报中心）。" : $"读到 {clipboard.Text.Length} 个字符（已上报中心）。");
    }

    // ═══════════════════════════════════════════════════════════
    // 请补关键帧（M5.4-c① 步 5）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>同一个会话的两次关键帧请求之间至少隔多久</b>。
    ///
    /// <para>
    /// 🔥 <b>为什么必须有这个数</b>：中心<b>持续</b>丢帧时会每帧都要
    /// （一帧十几片、每片都可能带 <c>NeedKeyframe</c>）。不节流 = <b>每帧一个 IDR</b>
    /// ⇒ 码率翻几倍、帧率掉下来，而现场只会看到"推流很卡"，
    /// 没人会想到根因是"补救动作做得太勤"。8K 的 IDR 正是整条链路上最贵的东西。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>但也不能取消</b>：完全没有 ⇒ 丢一帧之后观看端花/冻到下一个自然 GOP
    /// （<c>GopSize 60</c> / 12 fps ⇒ <b>约 5 秒</b>），用户会以为软件卡死了。
    /// 1 秒 ≈ 12 帧一个 IDR，是"恢复得快"与"不把码率打爆"之间的一格。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 它是 <c>internal</c> 而<b>不是</b>私有的唯一理由：让用例读它、
    /// 而不是自己写死一个 1 秒 —— 否则以后调这个参数，用例会以一个莫名其妙的
    /// "1 vs 2" 失败，而看不出是谁动的手。
    /// </para>
    /// </summary>
    internal static readonly TimeSpan KeyframeRequestMinInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 问托盘"桌面静止多久了"时的<b>本地超时</b>（M5.4-c② 步 7）。
    ///
    /// <para>
    /// 🔥 <b>为什么它必须比管道自带的 5 秒短</b>：这条查询与<b>每秒一次的计数上报</b>
    /// 在同一秒里排队，而那条上报是"这路还活着"的<b>唯一正证据</b>
    /// （中心 20 秒收不到就判死，见 <c>RemoteSessionRegistry</c> 的 Sweep）。
    /// 托盘的编码线程一旦卡住，用 5 秒去等一格<b>显示</b>会把活性信号一起拖住 ——
    /// 那是"为了装饰品牺牲生命体征"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 1 秒是"远远够用"的量级：本机命名管道一次往返在<b>亚毫秒</b>级
    /// （<c>SessionPipeChannel</c> 的类注释里就写着这一点）。
    /// </para>
    /// </summary>
    internal static readonly TimeSpan DesktopIdleQueryTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// <b>请托盘补一个关键帧</b>（中心重组分片失败时）。<b>不抛、不阻塞。</b>
    ///
    /// <para>
    /// 🔥 <b>为什么是同步的 <c>void</c>，而里面又 fire-and-forget</b>：
    /// 它的调用点<b>在发帧的路径上</b>（每帧最多一次）。让发一帧去等一次 IPC 往返，
    /// 等于给每一帧都加一份延迟 —— 而这是一个<b>补救</b>动作，不是主路。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 于是"<b>不许 fire-and-forget 吞异常</b>"那条纪律（<c>void asyncFn()</c> 的坑）
    /// 必须在这里被<b>显式兑现</b>：下面那个 <c>SendKeyframeRequestAsync</c>
    /// 从第一行到最后一行都在 <c>try</c> 里，异常落 <c>Warn</c> 日志，
    /// <b>绝不外泄</b>到 SignalR 的调用栈（那会掐掉整条连接上的所有东西）。
    /// </para>
    ///
    /// <para>
    /// <b>会话不在 ⇒ 什么都不做</b>（不是失败）：请求是发给"正在推的那一路"的，
    /// 而那一路已经没了的时候，发过去只会换来一句必然失败的回复 + 一条像故障的日志。
    /// </para>
    /// </summary>
    public void RequestKeyframe(string sessionId)
    {
        Active? active;

        lock (_gate) active = _active is { } current && current.SessionId == sessionId ? current : null;

        if (active is null) return;

        // 🔥 **节流在这里，不在中心**：中心只知道"我这帧拼不起来了"，它不知道
        //    对面多久能补上一个关键帧 —— 让它去重，等于让它猜。
        //    而本进程是"知道托盘那边有一路流正在跑"的那一侧，它才该决定要不要麻烦托盘。
        if (!active.TryBeginKeyframeRequest(KeyframeRequestTicks)) return;

        _ = SendKeyframeRequestAsync(active.SessionId);
    }

    /// <summary><see cref="KeyframeRequestMinInterval"/> 换算成 <see cref="Stopwatch"/> 的 tick。</summary>
    /// <remarks>
    /// ⚠️ 换算必须走 <see cref="Stopwatch.Frequency"/>，不能当"1 tick = 100ns"：
    /// 那个假设在 Windows 上恰好成立（QPC 是 10 MHz），换平台就不成立 ——
    /// 而错了的表现是"节流窗口变成了一个随平台漂的数"，没有任何报错。
    /// </remarks>
    private static readonly long KeyframeRequestTicks =
        (long)(KeyframeRequestMinInterval.TotalSeconds * Stopwatch.Frequency);

    /// <summary>
    /// 真正把那条指令送出去。<b>全包在 <c>try</c> 里</b> —— 见 <see cref="RequestKeyframe"/>。
    /// </summary>
    private async Task SendKeyframeRequestAsync(string sessionId)
    {
        try
        {
            var payload = JsonSerializer.Serialize(
                new RemoteStreamRequest(RemoteSessionActions.RequestKeyframe, sessionId),
                Json);

            var result = await _session.SendAsync(
                new SessionCommandRequest(SessionCommandMethods.RemoteStream, payload),
                CancellationToken.None).ConfigureAwait(false);

            // ⚠️ 失败只记日志，**不**升级成"会话坏了"：托盘此刻可能正好在换屏 / 刚重启，
            //    下一次丢帧中心还会再要一遍。
            if (!result.Ok)
            {
                _log.Warn("Remote", $"[Remote] 会话 {sessionId} 补关键帧的请求没送到：{result.Message}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Remote", $"[Remote] 会话 {sessionId} 补关键帧的请求出错（无妨）：{ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 接流泵
    // ═══════════════════════════════════════════════════════════

    private async Task PumpAsync(Active active, string logCategory)
    {
        var reportedStreaming = false;

        // ── 推流期间的**周期性计数上报**（1 秒一次）──
        //
        // 🔥 为什么必须有这一条：页面那格「被控端实测帧率」，以及"两个方向的帧计数
        //    并排"这条最重要的诊断，靠的都是中心手上的 `AgentStats` —— 而它只有在
        //    **被控端报过计数**之后才不为 null。原先整条会话里只有**终态那条**上报
        //    带计数（"进入 streaming"那条刻意 `withCounters: false`）⇒ 会话活着的时候
        //    中心手上关于"被控端到底发出去多少"的信息是空的，页面那几格恒为 `—`；
        //    这恰恰把诊断退回到"只给一个数"—— 那时「链路掉帧」（治网络）与
        //    「被控端编不动」（治编码）长得一模一样，而处置相反。
        //
        // ⚠️ 周期取 1 秒：中心收到后会立刻 `SessionChanged` 广播给观看端，
        //    所以这个数在页面上是**活的**，不必等那个 5 秒的兜底轮询。
        // ⚠️ 它**必须**在终态上报之前彻底停下来（顺序见下面的 finally）：
        //    否则一条迟到的 `streaming` 会盖在 `stopped` 之后，页面就变成
        //    "会话还在推流"，而其实早就停了。
        using var counterCts = CancellationTokenSource.CreateLinkedTokenSource(active.Token);

        async Task ReportCountersAsync(CancellationToken token)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);

                    // ⚠️ 这里读的是**另一个线程**写的标志。写的是 `true` 且只写一次，
                    //    所以读到旧值最多让这一秒不报 —— 无害，不必上锁。
                    //
                    //    还没出第一帧就先别报：那时 active.ReportedState 还是
                    //    requested / starting，报上去只会让中心看到一串"没变化"的状态。
                    if (reportedStreaming)
                    {
                        // 🔥 先问一句"桌面静止多久了"，再把状态报出去（M5.4-c② 步 7）：
                        //    顺序反了的话，这一秒报出去的永远是上一秒的静止时长，
                        //    页面上那句"已静止 12 秒"会稳定地落后一秒（而它看起来完全正常）。
                        await RefreshDesktopIdleAsync(active).ConfigureAwait(false);

                        await ReportChannelStatesAsync(active).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常收场（会话结束，或收场时主动取消）。
            }
        }

        var counterPump = ReportCountersAsync(counterCts.Token);

        RemoteStreamOutcome outcome;

        try
        {
            outcome = await _stream.RunAsync(
                active.SessionId,
                async (frame, token) =>
                {
                    if (!reportedStreaming)
                    {
                        reportedStreaming = true;
                        active.ReportedState = RemoteSessionStates.Streaming;

                        await ReportAsync(NewState(
                            active.SessionId,
                            active,
                            RemoteSessionStates.Streaming,
                            null,
                            withCounters: false)).ConfigureAwait(false);
                    }

                    var sink = FrameSink;

                    // 🔥 中心不在就别再收了：每收一帧，被控端就白抓白编一帧。
                    //    抛出去 → 接流通道把这个流结束掉（见 SessionStreamChannel）。
                    if (sink is null) throw new InvalidOperationException("与中心的长连接不在了，帧无处可送。");

                    await sink(frame, token).ConfigureAwait(false);

                    active.Record(frame);
                },
                active.Token).ConfigureAwait(false);
        }
        finally
        {
            // 🔥 顺序不能反：**先停周期上报、等它真的停了**，再去报终态。
            try
            {
                counterCts.Cancel();
            }
            catch
            {
                // 忽略：取消失败无妨，下面那个 await 仍会因 token 已取消而返回。
            }

            try
            {
                await counterPump.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 与所有上报同一条纪律：绝不把一次上报失败升级成"断线重连"。
                _log.Warn(logCategory, $"[Remote] 周期上报收尾时出错（无妨）：{ex.Message}");
            }
        }

        lock (_gate)
        {
            if (ReferenceEquals(_active, active)) _active = null;
        }

        // ── 收场：上报终态 ──
        //
        // 🔥 终态**必须报**，哪怕这一路是被"停止指令"正常收掉的：
        //    中心那边如果只看到"帧不再来了"，是分不清"停了"与"网断了"的。
        var state = outcome.Ok ? RemoteSessionStates.Stopped : RemoteSessionStates.Failed;

        active.ReportedState = state;

        _log.Info(
            logCategory,
            $"[Remote] 会话 {active.SessionId} 收场（{state}）：{outcome.Message}"
            + $"｜发帧 {active.Frames}｜{active.Bytes / 1024} KB｜{outcome.Seconds:0.0} 秒");

        await ReportAsync(NewState(active.SessionId, active, state, outcome.Message)).ConfigureAwait(false);

        // 顺手让托盘收摊：正常收场时它多半已经自己停了，这条对它是幂等的空操作；
        // 而异常收场（比如中心断了）时，它正是那条"别留个没人接的管子"的保险。
        await AskTrayToStopAsync(active.SessionId).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════
    // 输入回传（M5.2）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 收一批来自中心的输入事件，<b>投进通向托盘的管道</b>。
    ///
    /// <para>
    /// 🔥 <b>它刻意不是 <c>async</c></b>，与 <c>AgentHub.PushRemoteFrame</c> 同一条纪律：
    /// 这是高频路径（拖动鼠标时每秒几十批），方法体只做"校验 + 塞进队列"，
    /// 而那两件事加起来是几次比较和一次永不阻塞的 <c>TryWrite</c>。
    /// 一旦这里出现 <c>await</c>，被控机的输入延迟就会被别的东西拖着 ——
    /// 而"鼠标发飘"比"画面卡一下"更让人用不下去。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>返回 <c>Rejected</c> 不意味着"要停下什么"</b>（与推帧那条完全不同）：
    /// 输入被拒只是"这一点没作用到桌面上"，画面还在、会话还在。
    /// 所以这个拒绝是<b>给页面看的解释</b>，不是刹车信号。
    /// </para>
    /// </summary>
    public AgentIngestAckDto AcceptInput(RemoteInputBatchDto? batch)
    {
        if (batch is null) return AgentIngestAckDto.Rejected("empty", "载荷为空。");

        if (batch.Events is null || batch.Events.Count == 0)
        {
            // 空批**算成功**：它是一种合法的"什么都没发生"（前端可能因为节流发了个空的），
            // 报失败会让页面上莫名其妙闪一条错误。
            return AgentIngestAckDto.Success();
        }

        if (batch.Events.Count > RemoteInputLimits.MaxEventsPerBatch)
        {
            return AgentIngestAckDto.Rejected(
                RemoteErrorCodes.InputTooLarge,
                $"一批 {batch.Events.Count} 条输入超过上限 {RemoteInputLimits.MaxEventsPerBatch} 条。");
        }

        Active? active;

        lock (_gate) active = _active;

        // 🔥 归属校验在这一层是**结构性**的：中心送来的 sessionId 必须与
        //    本机此刻在推的那一路一致。不校验的话，一台机器可以往
        //    "另一个会话"注入输入 —— 而那个会话可能属于另一台机器。
        if (active is null || !string.Equals(active.SessionId, batch.SessionId, StringComparison.Ordinal))
        {
            return AgentIngestAckDto.Rejected(
                RemoteErrorCodes.SessionNotFound,
                $"本机此刻没有会话「{batch.SessionId}」在推流，输入无处可注入。");
        }

        if (active.InputState == AgentInputStates.Unavailable)
        {
            // 如实说"我做不了"而不是"收到了"：这是中心侧唯一能拿到
            // "被控端根本没有输入通道"的地方（见 AgentInputStates 的注释）。
            return AgentIngestAckDto.Rejected(
                RemoteErrorCodes.InputUnavailable,
                active.InputReason ?? "被控端的输入通道不可用。");
        }

        foreach (var input in batch.Events)
        {
            active.Input.Writer.TryWrite(input);
        }

        Interlocked.Add(ref active.InputQueued, batch.Events.Count);

        return AgentIngestAckDto.Success();
    }

    /// <summary>
    /// 把输入通道接上，并把"接上了 / 确定接不上"如实报给中心。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它<b>与接流泵并行</b>，生命周期绑在同一个会话 token 上：
    /// 停止会话时两条一起收。但它们的<b>失败语义完全不同</b> ——
    /// 接流断了要把会话收掉，输入断了只是点不动（见 <c>ISessionInputChannel</c>）。
    /// </remarks>
    private async Task InputPumpAsync(Active active, string logCategory)
    {
        var outcome = await _input.RunAsync(
            active.SessionId,
            active.Input.Reader,
            async (ok, reason, _) =>
            {
                active.SetInputState(
                    ok ? AgentInputStates.Ready : AgentInputStates.Unavailable,
                    reason);

                _log.Info(
                    logCategory,
                    ok
                        ? $"[Remote] 会话 {active.SessionId} 输入通道已就绪（可以注入）。"
                        : $"[Remote] 会话 {active.SessionId} 输入通道不可用：{reason}");

                await ReportChannelStatesAsync(active).ConfigureAwait(false);
            },
            active.Token).ConfigureAwait(false);

        // ── 通道收场 ──
        //
        // 如果它原本是"就绪"的，现在断了，必须把状态改回去 —— 否则中心会一直显示
        // "输入就绪"，而用户点了半天没有反应（那正是这套状态字段要消灭的歧义）。
        if (active.InputState == AgentInputStates.Ready)
        {
            active.SetInputState(AgentInputStates.Unavailable, outcome.Message);

            // 只在会话还没被停掉时才报：正常停止时这一条没有意义，
            // 而它会把一条误导人的"输入不可用"写进页面。
            if (!active.Token.IsCancellationRequested)
            {
                await ReportChannelStatesAsync(active).ConfigureAwait(false);
            }
        }

        _log.Info(
            logCategory,
            $"[Remote] 会话 {active.SessionId} 输入通道收场：{outcome.Message}"
            + $"｜写入 {outcome.Written} 条｜{outcome.Seconds:0.0} 秒");
    }

    // ═══════════════════════════════════════════════════════════
    // 音频回传（M5.3）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 接上音频通道，并把结论如实报给中心。
    ///
    /// <para>
    /// 🔥 <b>分两步，顺序不能颠倒</b>：</para>
    /// <list type="number">
    ///   <item><b>先问托盘"有没有音频能力"</b>（<see cref="RemoteSessionActions.AudioProbe"/>）。
    ///         服务侧能直接观察到的只有"管道连不上"，而那有几个完全不同的原因
    ///         （没有声卡 / 客户端过旧 / 音频引擎拒绝格式），处置各不相同。
    ///         这一问把"连不上"从一个现象变成一个结论；</item>
    ///   <item><b>再去连管道</b>。连上了就从 connecting 变 ready；
    ///         连不上就用第 ① 步的结论作为"为什么"。</item>
    /// </list>
    ///
    /// <para>
    /// ⚠️ <b>与接流泵完全不同：音频失败不该让会话失败。</b>
    /// 它跑在独立任务上，"没有声音"只是页面上一格的状态 —— 画面照常。
    /// </para>
    /// </summary>
    private async Task AudioPumpAsync(Active active, string logCategory)
    {
        try
        {
            var probe = await _session.SendAsync(
                new SessionCommandRequest(
                    SessionCommandMethods.RemoteStream,
                    JsonSerializer.Serialize(
                        new RemoteStreamRequest(RemoteSessionActions.AudioProbe, active.SessionId),
                        Json)),
                active.Token).ConfigureAwait(false);

            // 🔥 契约（见 RemoteSessionActions.AudioProbe）：**失败 = 没有能力，Message 就是原因**。
            if (!probe.Ok)
            {
                active.SetAudioState(AgentAudioStates.Unavailable, probe.Message);

                _log.Warn(logCategory, $"[Remote] 会话 {active.SessionId} 没有音频：{probe.Message}");

                if (!active.Token.IsCancellationRequested)
                {
                    await ReportChannelStatesAsync(active).ConfigureAwait(false);
                }

                return;
            }

            _log.Info(logCategory, $"[Remote] 会话 {active.SessionId} {probe.Message}");

            var announced = false;

            var outcome = await _audio.RunAsync(
                active.SessionId,
                async (frame, token) =>
                {
                    if (!announced)
                    {
                        // 🔥 到这一刻才算"就绪"：在此之前都是"正在连"。
                        //    页面据此能把"还没开始"与"开始了但被控机在放静音"分开。
                        announced = true;
                        active.SetAudioState(AgentAudioStates.Ready, null);

                        await ReportChannelStatesAsync(active).ConfigureAwait(false);
                    }

                    var sink = AudioSink;

                    // 中心不在就别再收了：每收一段，被控端就白采白编一段。
                    // 抛出去 → 接音频通道把这路结束掉（见 SessionAudioChannel）。
                    if (sink is null) throw new InvalidOperationException("与中心的长连接不在了，音频无处可送。");

                    await sink(frame, token).ConfigureAwait(false);

                    active.RecordAudio(frame);
                },
                active.Token).ConfigureAwait(false);

            _log.Info(
                logCategory,
                $"[Remote] 会话 {active.SessionId} 音频收场：{outcome.Message}"
                + $"｜收段 {outcome.Frames}｜{outcome.Bytes / 1024} KB｜{outcome.Seconds:0.0} 秒");

            // ── 收场：把状态改回去并上报 ──
            //
            // 🔥 两种情形都要报，而且都必须是 unavailable 而不是继续 connecting：
            //    · 从没到过 ready（管道连不上 / 中途出错）⇒ 原因就是 outcome.Message；
            //    · 原本 ready、后来断了 ⇒ 中心一直显示"音频正常"会让人以为是自己扬声器的问题。
            //
            // ⚠️ 会话已经在取消途中时不报：那时这一条只会给页面塞一句误导的"音频不可用"
            //    （会话本来就要收了，声音当然会没）。
            if (!active.Token.IsCancellationRequested)
            {
                active.SetAudioState(AgentAudioStates.Unavailable, outcome.Message);

                await ReportChannelStatesAsync(active).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常收场（会话停止 / 进程退出）。
        }
        catch (Exception ex)
        {
            // ⚠️ 绝不外抛：这是后台任务，抛出去只会变成一个被 await 时才炸的 Task。
            _log.Error(logCategory, $"[Remote] 会话 {active.SessionId} 音频通道出错", ex);
        }
    }

    /// <summary>
    /// 问一句托盘"桌面静止多久了"，把答案记到会话上（M5.4-c② 步 7）。<b>绝不外抛。</b>
    ///
    /// <para>
    /// 🔥 <b>为什么这件事是"问"、而不是本进程自己数"多久没收到帧"</b>：
    /// "没有新帧"至少有三种成因 —— <b>桌面真的没变</b>（正常）、<b>被控端编不出来</b>（故障）、
    /// <b>中心把帧丢了</b>（链路）。三者计数上一模一样、处置相反，而只有<b>抓屏器</b>
    /// 知道是哪一种。见 <see cref="RemoteSessionActions.DesktopIdle"/>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>失败时保留上一次的值，不清成 <c>null</c></b>：一次超时不等于
    /// "这台机器突然不认识这个字段了"。而失败本身<b>只在状态翻转时记一行</b> ——
    /// 一秒一条会把日志刷爆，反而盖住真问题。
    /// </para>
    /// </summary>
    private async Task RefreshDesktopIdleAsync(Active active)
    {
        SessionCommandResult answer;

        try
        {
            var payload = JsonSerializer.Serialize(
                new RemoteStreamRequest(RemoteSessionActions.DesktopIdle, active.SessionId),
                Json);

            // ⚠️ 超时用**我们自己的**短时限（见 DesktopIdleQueryTimeout 的说明）：
            //    管道自带那个 5 秒会把每秒一次的活性上报一起拖住。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(active.Token);
            cts.CancelAfter(DesktopIdleQueryTimeout);

            answer = await _session.SendAsync(
                new SessionCommandRequest(SessionCommandMethods.RemoteStream, payload),
                cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 🔥 与所有上报同一条纪律：一次问不到**不许**升级成"会话坏了"。
            //    （取消也算在这里：那是我们自己的超时，不是会话被停。）
            answer = SessionCommandResult.Failure(ex.Message);
        }

        var answered = answer.Ok && answer.DesktopIdleMs is not null;

        // ⚠️ 只在**有值**时覆盖：`0`（刚动过）是一个答案，`null`（没答）不是。
        if (answered) active.DesktopIdleMs = answer.DesktopIdleMs;

        // ── 只在翻转时各记一行（同一个理由见 SessionPipeChannel.Report）──
        if (active.DesktopIdleQueryOk == answered) return;

        if (active.DesktopIdleQueryOk == false)
        {
            _log.Info(
                "Remote",
                $"[Remote] 会话 {active.SessionId}：又能问到桌面静止时长了"
                + $"（当前 {Describe(active.DesktopIdleMs)}）。");
        }
        else if (!answered)
        {
            _log.Warn(
                "Remote",
                $"[Remote] 会话 {active.SessionId}：问不到桌面静止时长（{answer.Message}）"
                + "—— 多半是托盘版本较旧，页面上那一格会显示「—」（不是「0 秒」）。");
        }

        active.DesktopIdleQueryOk = answered;

        static string Describe(long? ms) => ms is { } value ? $"{value} 毫秒" : "未知";
    }

    /// <summary>
    /// 报一次"当前状态 + 最新的输入 / 音频通道状态"。
    /// <para>
    /// ⚠️ 带上 <see cref="Active.ReportedState"/> 而不是硬写 <c>streaming</c>：
    /// 输入 / 音频通道可能在<b>第一帧到达之前</b>就失败了（旧托盘压根没建那条管道，
    /// 连不上是毫秒级的），此时会话还是 <c>requested</c>；
    /// 硬写 streaming 会让中心把一条还没起来的流显示成"正在推流"。
    /// </para>
    /// <para>
    /// 🔥 <b>两条通道共用这一个上报</b>：它们都是"会话级的一次性结论"，
    /// 而拆成两条消息会让中心收到两条<b>内容几乎相同</b>的状态 ——
    /// 后到的那条总是把先到的那条覆盖掉，而两条里各有一半字段是默认值。
    /// 一次报全，中心按字段各自更新。
    /// </para>
    /// <para>
    /// ⚠️ 这里第三个参数（会话原因）<b>传 null</b>：这一条说的是"通道状态"，
    /// 会话本身没有结束、也就没有结束原因。通道不通的原因各走
    /// <c>InputReason</c> / <c>AudioReason</c> 自己的字段
    /// —— 不再往会话原因那个位置里塞（那正是 M5.2 留下的那个坑）。
    /// </para>
    /// <para>
    /// 🔥 <b>它还有第三个调用方：推流期间每秒一次的那条周期上报</b>
    /// （<c>PumpAsync</c> 里的 <c>ReportCountersAsync</c>）。复用同一个方法不是图省事 ——
    /// 那一条要的正是"当前状态 + 计数 + 两条通道状态"这一整份，而且它**必须**
    /// 与通道那两条走同一套字段拼装（<see cref="NewState"/> 的那十几个位置参数
    /// 少传一个就整体错位，编译器不报）。
    /// </para>
    /// </summary>
    private async Task ReportChannelStatesAsync(Active active)
    {
        await ReportAsync(NewState(active.SessionId, active, active.ReportedState, null))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 造一条状态上报（<b>四个上报点共用</b>）。
    /// </summary>
    /// <param name="sessionId">会话 Id。</param>
    /// <param name="active">当前会话；停止指令打到一条<b>已经不在表里</b>的会话时可以是 null。</param>
    /// <param name="state">要报的状态（取值见 <c>RemoteSessionStates</c>）。</param>
    /// <param name="reason">理由（含义随 <paramref name="state"/> 变，见各调用点）。</param>
    /// <param name="withCounters">
    /// 要不要带上计数。<c>false</c> = 只报"通道状态 + 这几个字段"，计数一律 0。
    /// <para>
    /// ⚠️ 只有"第一帧到达、宣布进入 streaming"那一条用 <c>false</c> ——
    /// 那一句要说的是"我开始推了"，而那一刻计数<b>必然是 0</b>
    /// （<c>Record</c> 要等这一帧送出去之后才跑）。报一份 0 出去只会让中心把
    /// <c>AgentStats</c> 从"还没有"改成"有，但全是 0"，页面从 <c>—</c> 变成 <c>0 fps</c>
    /// —— 一样是错的，还不如不报。
    /// </para>
    /// <para>
    /// 🔥 推流期间的计数由 <c>PumpAsync</c> 里那条**每秒一次**的周期上报给
    /// （它走 <see cref="ReportChannelStatesAsync"/> ⇒ 本参数取默认 <c>true</c>）。
    /// 那是"页面上的被控端实测帧率"能变成**活数**的唯一来源；
    /// 终态那条上报给的只是最后一帧的定格值。
    /// </para>
    /// </param>
    /// <remarks>
    /// 🔥 <b>四个上报点必须共用这一个方法</b>，理由不是省几行，而是防一类<b>不报错的漏</b>：
    /// <see cref="AgentRemoteSessionStateDto"/> 有十几个<b>位置参数</b>，
    /// 手动在一个调用点少传一个，后面所有同类型的字段就整体错位
    /// （视频的帧数跑到音频的帧数上、音频的字节数跑到丢帧数上），
    /// <b>编译器一个字都不会说</b>，而现场看到的是"页面上的音频计数永远不对劲"。
    /// 加一个字段只改这里一处，是唯一能保证四边同步的做法。
    /// </remarks>
    private static AgentRemoteSessionStateDto NewState(
        string sessionId,
        Active? active,
        string state,
        string? reason,
        bool withCounters = true)
    {
        // ⚠️ 计数与"通道状态"要分开取：关掉计数时仍然要带上输入 / 音频的三态
        //    （它们恰恰是在"还没出第一帧"的时候就得先报出去的那部分，见 ApplyState）。
        var counted = withCounters ? active : null;

        return new AgentRemoteSessionStateDto(
            sessionId,
            state,
            reason,
            counted?.Frames ?? 0,
            counted?.Bytes ?? 0,
            counted?.Fps ?? 0,
            counted?.Dropped ?? 0,
            active?.InputState,
            active?.AudioState,
            active?.AudioReason,
            counted?.AudioFrames ?? 0,
            counted?.AudioBytes ?? 0,
            counted?.AudioDropped ?? 0,

            // ⚠️ 输入的原因走**自己那个字段**，不再借 reason 这个位置 ——
            //    借一次就够出一次事故（见 AgentRemoteSessionStateDto.InputReason 的说明）。
            active?.InputReason,

            // 🔥 格式裁决说明也走**自己那个字段**（第 7 步）。理由与上一条同源：
            //    `Reason` 的本义是"这路会话为什么结束"，而这句话说的是
            //    "它为什么没按中心的首选做、但仍然正常在推" —— 两者语义相反。
            active?.CodecNote,

            // 🔥 桌面静止时长（M5.4-c② 步 7）也走自己那个字段。
            //    ⚠️ **null 与 0 不是一回事**：null = 这台机器答不出（旧托盘 / 还没问到），
            //    0 = 刚动过。中心那边只在"明确给了值"时才覆盖（见 Entry.ApplyState）。
            active?.DesktopIdleMs);
    }

    /// <summary>请托盘停止（用后即忘，失败只记日志）。</summary>
    private async Task AskTrayToStopAsync(string sessionId)
    {
        try
        {
            var payload = JsonSerializer.Serialize(
                new RemoteStreamRequest(RemoteSessionActions.Stop, sessionId),
                Json);

            await _session.SendAsync(
                new SessionCommandRequest(SessionCommandMethods.RemoteStream, payload),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("Remote", $"[Remote] 请托盘停止会话 {sessionId} 时出错（无妨，超时也会自己收摊）：{ex.Message}");
        }
    }

    /// <summary>
    /// 送一条状态给中心。<b>中心不在时只记日志</b>，不抛。
    /// </summary>
    private async Task ReportAsync(AgentRemoteSessionStateDto state)
    {
        var sink = StateSink;

        if (sink is null)
        {
            _log.Info("Remote", $"[Remote] 会话 {state.SessionId} → {state.State}（中心不在，未上报）"
                                + (state.Reason is null ? string.Empty : $"：{state.Reason}"));

            return;
        }

        try
        {
            await sink(state).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ⚠️ 上报失败绝不能升级成"断线重连"（与遥测上报同一条纪律）。
            _log.Warn("Remote", $"[Remote] 上报会话状态失败（{state.SessionId} → {state.State}）：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Active? active;

        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
            active = _active;
            _active = null;
        }

        try
        {
            _lifetime.Cancel();
        }
        catch
        {
            // 忽略。
        }

        active?.Cancel();

        // 三条泵都要等：只等接流那条的话，另外两条可能还在往一个正在释放的
        // 对象上写（而它们的收场路径里还有必须跑完的收摊动作 ——
        // 输入那条要"松开所有按键"，音频那条要释放 WASAPI 客户端与它的线程）。
        foreach (var pump in new[] { active?.Pump, active?.InputPump, active?.AudioPump })
        {
            if (pump is null) continue;

            try
            {
                await pump.ConfigureAwait(false);
            }
            catch
            {
                // 泵内部已全量兜住。
            }
        }

        try
        {
            _lifetime.Dispose();
        }
        catch
        {
            // 忽略。
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 当前会话
    // ═══════════════════════════════════════════════════════════

    private sealed class Active
    {
        private readonly Stopwatch _clock = new();
        private readonly CancellationTokenSource _cts;

        private long _frames;
        private long _bytes;
        private long _dropped;

        // ── 音频（M5.3）── 与视频那组分开，见 RecordAudio 的注释。
        private long _audioFrames;
        private long _audioBytes;

        /// <summary>
        /// 被控端累计丢弃（编不出来）的音频段数。
        /// <para>
        /// ⚠️ 它<b>不由本进程自己数</b>，而是搭在每段音频的帧头上过来
        /// （<c>RemoteAudioFrameHeader.Dropped</c>，由托盘那个编码循环累加）——
        /// 原因很实在：丢帧发生在<b>托盘</b>（它才知道自己有没有编出来），
        /// 本进程只能看到"进来的那一段比预期少"。自己在这里另数一份，就变成
        /// "左边一个丢帧数、右边一个丢帧数，而它们永远对不上"。
        /// 与视频那边处理 <c>Dropped</c> 的方式完全一致：<b>取大值</b>。
        /// </para>
        /// </summary>
        private long _audioDropped;

        /// <summary>
        /// 上一次<b>真的发出</b>关键帧请求的时刻（会话单调轴 tick，0 = 从没发过）。
        ///
        /// <para>
        /// 🔥 <b>它为什么在会话对象上、而不是整个协调器一个</b>：节流窗口是
        /// "<b>这一路流</b>多久该补一个关键帧"，不是"本进程多久允许发一条指令"。
        /// 放到外面的话，两路流（不是本批的事，但迟早会有）会互相压掉对方的请求。
        /// </para>
        ///
        /// <para>
        /// ⚠️ <b>它用 <see cref="Interlocked"/> 而不是在 <c>_gate</c> 内改</b> ——
        /// 这是本类"<c>Active</c> 的字段一律在 <c>_gate</c> 内改"那条规矩的
        /// <b>第 2 个例外</b>（第 1 个是中心的 <c>_reassemblyDropped</c>），
        /// 理由：<c>RequestKeyframe</c> 的调用点在<b>发帧路径</b>上，
        /// 每帧都可能来一次；为了一个时间戳去抢账本那把锁，等于让它和
        /// 建会话 / 收状态上报互相排队。**必须写出来**，否则下一个人会给它套上 <c>_gate</c>。
        /// </para>
        /// </summary>
        private long _lastKeyframeRequestTicks;

        /// <summary>
        /// 这一次关键帧请求<b>该不该真的发出去</b>（节流闸门）。
        /// <para>
        /// 值是"发"，<c>false</c> 是"窗口内，这一次不要"。
        /// </para>
        /// </summary>
        /// <remarks>
        /// 🔥 <b>为什么用比较并交换而不是"读-判断-写"</b>：两个线程同时通过判断时，
        /// 朴素写法会让它们<b>都发</b> —— 而这里恰好是"并发越多越要压住"的场景
        /// （大帧逐片 ack 全带标志，几乎同时触发）。CAS 只有一个能赢。
        /// </remarks>
        public bool TryBeginKeyframeRequest(long minIntervalTicks)
        {
            var now = _clock.ElapsedTicks;
            var last = Interlocked.Read(ref _lastKeyframeRequestTicks);

            // 0 = 从没请求过 ⇒ 立刻允许（会话刚起来就丢了一帧，没理由等）。
            if (last != 0 && now - last < minIntervalTicks) return false;

            return Interlocked.CompareExchange(ref _lastKeyframeRequestTicks, now, last) == last;
        }

        public Active(string sessionId, CancellationToken hostToken)
        {
            SessionId = sessionId;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);

            // 🔥 通向托盘的输入队列：**容量 1024、满了丢最旧**。
            //
            //    为什么不是无界：一个卡住的管道 + 无界队列 = 内存越吃越多，
            //    而那种故障的表现是"跑了半天之后这台机器开始吃内存"，
            //    跟"输入"的联系在日志里看不出来。
            //
            //    ⚠️ 丢最旧的**代价必须说清楚**：极端拥塞下可能丢掉一条"抬起"，
            //    于是那个键在被控机上一直按着。兜底是托盘那边 ——
            //    会话收场时它会强制松开所有还按着的键（见 RemoteStreamHost）。
            Input = Channel.CreateBounded<RemoteInputEventDto>(new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

            _clock.Start();
        }

        public string SessionId { get; }

        /// <summary>
        /// 本机对"这一路用什么格式编"的裁决说明（第 7 步）；<c>null</c> = 照中心首选、没什么可说的。
        /// <para>
        /// ⚠️ 它是<b>会话级、一次定下</b>的事实（编码器建的那一刻就定了），
        /// 所以随每一次状态上报原样带出去，而不是"只在第一条消息里说一次" ——
        /// 后者会让"页面刷新 / 中心重启后重连"的那一侧看不到它。
        /// </para>
        /// </summary>
        public string? CodecNote { get; set; }

        /// <summary>
        /// 桌面已经静止了多久（毫秒；M5.4-c② 步 7）。
        ///
        /// <para>
        /// 🔥 <b>它的来源是"服务每秒问托盘一句"</b>（<c>RemoteSessionActions.DesktopIdle</c>），
        /// 不是本进程自己数"多久没收到帧"—— 后者在"被控端编不出来"时也会变大，
        /// 而那与桌面有没有变毫无关系（见该动作的说明）。
        /// </para>
        ///
        /// <para>
        /// ⚠️ <b><c>null</c> = 从没问到过 / 托盘答不出</b>（旧版本），与 <c>0</c>（刚动过）
        /// 是两件事。问失败时<b>保留上一次的值</b>，不清成 null：
        /// 一次超时不等于"这台机器突然不认识这个字段了"。
        /// </para>
        /// </summary>
        public long? DesktopIdleMs { get; set; }

        /// <summary>
        /// 上一次问"桌面静止多久"时，托盘<b>答没答上来</b>（<c>null</c> = 还没问过）。
        ///
        /// <para>
        /// 🔥 <b>它只为一件事存在：让日志按"状态翻转"记，而不是按时间记。</b>
        /// 这条查询一秒一次，托盘旧一点就会一秒失败一次 —— 每次一行日志会把
        /// 真正的故障盖掉（这也正是"日志刷屏反而查不出问题"的典型形态）。
        /// </para>
        ///
        /// <para>
        /// ⚠️ 只由 <c>ReportCountersAsync</c> 那条循环读写（单写者），所以不必上锁。
        /// </para>
        /// </summary>
        public bool? DesktopIdleQueryOk { get; set; }

        public CancellationToken Token => _cts.Token;

        public Task? Pump { get; set; }

        /// <summary>输入通道那条循环。</summary>
        public Task? InputPump { get; set; }

        /// <summary>音频通道那条循环（M5.3）。</summary>
        public Task? AudioPump { get; set; }

        /// <summary>通向托盘的输入事件队列。</summary>
        public Channel<RemoteInputEventDto> Input { get; }

        /// <summary>被控端<b>自己</b>的输入通道状态（见 <see cref="AgentInputStates"/>）。</summary>
        public string? InputState { get; private set; }

        /// <summary>输入通道不可用的原因（人话，会随状态上报进页面）。</summary>
        public string? InputReason { get; private set; }

        /// <summary>被控端音频通道状态（见 <see cref="AgentAudioStates"/>）。</summary>
        public string? AudioState { get; private set; }

        /// <summary>音频不可用的原因（人话，会随状态上报进页面）。</summary>
        public string? AudioReason { get; private set; }

        /// <summary>
        /// 记一次音频状态。
        /// <para>
        /// ⚠️ 只有 <c>unavailable</c> 才留原因 —— 规矩写在
        /// <see cref="RemoteChannelStates.ReasonToKeep"/> 里，与输入共用同一份实现。
        /// </para>
        /// </summary>
        public void SetAudioState(string state, string? reason)
        {
            AudioState = state;
            AudioReason = RemoteChannelStates.ReasonToKeep(state, AgentAudioStates.Unavailable, reason);
        }

        /// <summary>
        /// 记一段已经<b>成功送到中心</b>的音频。
        /// <para>
        /// ⚠️ 与视频那组分开计（<see cref="Frames"/> / <see cref="Bytes"/>）：
        /// 两组摆在一起才能回答"是音频本身没产出，还是产出之后在链路上丢了"。
        /// </para>
        /// </summary>
        public void RecordAudio(RemoteAudioFrame frame)
        {
            Interlocked.Increment(ref _audioFrames);
            Interlocked.Add(ref _audioBytes, frame.Data.Length);

            // 帧头上的丢帧数是**累加值**，取大值就等于"取最新的那一份"。
            // 与视频的 Observe 同一条规矩（那里也是 `_dropped = header.Dropped`）。
            var dropped = frame.Header.Dropped;

            if (dropped > Interlocked.Read(ref _audioDropped))
            {
                Interlocked.Exchange(ref _audioDropped, dropped);
            }
        }

        /// <summary>已经送到中心的音频段数。</summary>
        public long AudioFrames => Interlocked.Read(ref _audioFrames);

        /// <summary>已经送到中心的音频字节数。</summary>
        public long AudioBytes => Interlocked.Read(ref _audioBytes);

        /// <summary>被控端累计丢弃的音频段数（正常恒为 0）。</summary>
        public long AudioDropped => Interlocked.Read(ref _audioDropped);

        /// <summary>最近一次上报给中心的会话状态（用于"只改输入状态"的那条上报不把状态写错）。</summary>
        public string ReportedState { get; set; } = RemoteSessionStates.Requested;

        /// <summary>投进输入队列的累计条数（只用于日志）。</summary>
        public long InputQueued;

        public void SetInputState(string state, string? reason)
        {
            InputState = state;

            // ⚠️ 只有"不可用"才留原因：就绪时把上一次的失败原因留在那儿，
            //    页面上会变成"已就绪：连不上管道"这种自相矛盾的一句话。
            //    规矩本身在 RemoteChannelStates.ReasonToKeep，与音频共用一份实现。
            InputReason = RemoteChannelStates.ReasonToKeep(state, AgentInputStates.Unavailable, reason);
        }

        public long Frames => Interlocked.Read(ref _frames);

        public long Bytes => Interlocked.Read(ref _bytes);

        public long Dropped => Interlocked.Read(ref _dropped);

        /// <summary>客户机侧的实测帧率（取"发出帧数 ÷ 已过去秒数"）。</summary>
        public double Fps
        {
            get
            {
                var seconds = _clock.Elapsed.TotalSeconds;

                return seconds > 0.5 ? Math.Round(Frames / seconds, 1) : 0;
            }
        }

        /// <summary>记一帧（帧头里带着托盘侧的累计丢帧，直接透传）。</summary>
        public void Record(RemoteStreamFrame frame)
        {
            Interlocked.Increment(ref _frames);
            Interlocked.Add(ref _bytes, frame.Data.Length);

            _dropped = frame.Header.Dropped;
        }

        public void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch
            {
                // 忽略。
            }
        }
    }
}
