using ControlServer.Agent.Config;
using ControlServer.Agent.Remote;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent.Tasks;

/// <summary>
/// 一条指令的<b>执行结论</b>。
/// <para>
/// 返回结论而不是 <c>void</c>：调用方要拿它写进「接收到的数据」——
/// 那上面写的必须是"到底做成没有"，而不是"我调用了"。
/// </para>
/// </summary>
/// <param name="Outcome">见 <see cref="AgentReceivedOutcomes"/>。</param>
/// <param name="Detail">人话说明（成功时带上会话侧读回的真实状态，失败时是原因）。</param>
public sealed record AgentCommandOutcome(string Outcome, string? Detail)
{
    public static AgentCommandOutcome Ignored(string detail) => new(AgentReceivedOutcomes.Ignored, detail);
}

/// <summary>
/// <b>指令执行器</b>（2026-10-15）：把「指令名 + 参数」落到真正干事的一方。
///
/// <para>
/// 🔥 <b>它是 UDP 通道与中心（SignalR）指令共用的同一份实现</b>。
/// 在它出现之前，"关机 / 重启 / 音量"只有 <see cref="UdpCommandListener"/> 里那一份，
/// 中心页面上下发的 <c>power</c> 指令到了客户端<b>没有任何人处理</b> ——
/// 表现为"页面说已下发、机器毫无反应"。
/// 抽出来而不是在那边复制一份，是因为<b>两条路两套实现必然漂</b>：
/// 今天补了电源、明天补音量，迟早只有一边被补上。
/// </para>
///
/// <para><b>执行位置（与通道无关，只与指令有关）：</b></para>
/// <list type="bullet">
///   <item><b>电源（关机 / 重启整机）</b> —— 服务侧。服务是 SYSTEM，天然持有 <c>SeShutdownPrivilege</c>。</item>
///   <item><b>音量</b> —— <b>会话侧</b>（托盘）。Session 0 里没有音频端点，
///         服务自己改音量是"执行成功、毫无声音"的无声失败。</item>
/// </list>
/// </summary>
public sealed class AgentCommandExecutor
{
    private readonly AgentOptions _options;
    private readonly LogService _log;
    private readonly ISessionCommandChannel _session;
    private readonly IHostPowerController _power;
    private readonly RemoteSessionCoordinator? _remote;
    private readonly IAudioController? _audio;

    public AgentCommandExecutor(
        AgentOptions options,
        LogService log,
        ISessionCommandChannel session,
        IHostPowerController power,
        RemoteSessionCoordinator? remote = null,
        IAudioController? audio = null)
    {
        _options = options;
        _log = log;
        _session = session;
        _power = power;
        _remote = remote;
        _audio = audio;
    }

    /// <summary>
    /// 执行一条指令。
    /// </summary>
    /// <param name="name">指令名（<see cref="AgentCommandNames"/>）。</param>
    /// <param name="argument">参数（电源动作 / 音量动作）。</param>
    /// <param name="reason">
    /// "是谁让这么干的"人话（"UDP 指令（来自 192.168.1.5）" / "中心服务端下发的指令"）——
    /// 它会进系统事件日志与本机日志，事后追溯"这台机器是被谁关掉的"全靠这一句。
    /// </param>
    /// <param name="logCategory">日志分类（UDP 侧写 "Udp"，中心指令写 "Agent"）—— 现场按分类翻日志。</param>
    /// <returns>执行结论；<b>不抛</b>（异常在内部分类捕获并转成 Failed）。</returns>
    public async Task<AgentCommandOutcome> ExecuteAsync(
        string name,
        string? argument,
        string reason,
        string logCategory,
        CancellationToken cancellationToken = default)
    {
        // ── 演练模式：两条通道共用的那道闸 ──
        //
        // 🔥 它**不只是 UDP 的开关**。开关的键名沿用历史（Agent:Tasks:ExecuteUdpCommands），
        //    但语义是"本机此刻敢不敢动手" —— 安全闸必须没有漏勺：
        //    现场开它是为了"观察一段时间、确认没有误触发再放行"，
        //    若中心的关机指令能绕过它，那它就不是一道闸，而只是一个标签。
        //
        // ⚠️ 被它拦下时**必须留痕说清原因**（Ignored + 一句人话）：
        //    "点了没反应"里有一整类是"其实被演练模式挡了"，而现场看不到客户端的配置。
        if (!_options.Tasks.ExecuteUdpCommands)
        {
            var note = "演练模式已开启（Agent:Tasks:ExecuteUdpCommands=false）—— 指令已识别但不执行。";
            _log.Warn(logCategory, note);

            return AgentCommandOutcome.Ignored(note);
        }

        try
        {
            return await ExecuteCoreAsync(name, argument, reason, logCategory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 🔥 指令执行这一层**不能把异常抛回给调用方**：调用方是 SignalR 的回调与 UDP 的收包循环，
            //    那里抛出去的后果是整条通道静默死掉（而且没有任何一行日志）。
            _log.Error(logCategory, $"执行指令「{name}」时出错", ex);

            return new AgentCommandOutcome(AgentReceivedOutcomes.Failed, $"执行指令时出错：{ex.Message}");
        }
    }

    private async Task<AgentCommandOutcome> ExecuteCoreAsync(
        string name,
        string? argument,
        string reason,
        string logCategory,
        CancellationToken cancellationToken)
    {
        if (name == AgentCommandNames.Power)
        {
            return await ExecutePowerAsync(argument, reason, logCategory, cancellationToken).ConfigureAwait(false);
        }

        if (name == AgentCommandNames.Volume)
        {
            return await ExecuteVolumeAsync(argument, logCategory, cancellationToken).ConfigureAwait(false);
        }

        if (name == AgentCommandNames.RemoteSession)
        {
            if (_remote is null)
            {
                // ⚠️ 不静默忽略：本机没装这条能力时，中心必须收到一句明确的话，
                //    否则表现是"服务说已下发、页面上却什么都没有"。
                return new AgentCommandOutcome(
                    AgentReceivedOutcomes.Failed,
                    "本客户端未启用远程桌面（客户端未接线或版本过旧）。");
            }

            return await _remote.ExecuteAsync(argument, logCategory, cancellationToken).ConfigureAwait(false);
        }

        // 走到这里说明调用方放行了本类不认的指令名（例如 AgentCommandNames.Restart ——
        // 那是"重启 Agent 服务"，只有宿主进程能干，见 AgentServiceHost）。
        _log.Warn(logCategory, $"本机不处理的指令：{name}（已忽略）");

        return AgentCommandOutcome.Ignored($"本机不处理的指令：{name}");
    }

    private async Task<AgentCommandOutcome> ExecutePowerAsync(
        string? action,
        string reason,
        string logCategory,
        CancellationToken cancellationToken)
    {
        if (!AgentPowerActions.IsValid(action))
        {
            _log.Warn(logCategory, $"电源指令参数不合法：{action ?? "(空)"}（已忽略）");

            return AgentCommandOutcome.Ignored($"电源指令参数不合法：{action ?? "(空)"}");
        }

        var label = AgentPowerActions.DisplayName(action);

        var result = action == AgentPowerActions.Shutdown
            ? await _power.ShutdownAsync(reason, cancellationToken).ConfigureAwait(false)
            : await _power.RestartAsync(reason, cancellationToken).ConfigureAwait(false);

        // ⚠️ 这是**会改变机器状态**的动作，用 Warn 而不是 Info ——
        // 翻日志时必须一眼看到"这台机器是被谁、用什么指令关掉的"。
        if (result.Ok) _log.Warn(logCategory, $"{label}：{result.Message}");
        else _log.Error(logCategory, $"{label}失败：{result.Message}");

        // 🔥 关机有个"假成功"的形态必须在这里说破：命令已经交给系统、机器几秒后才断，
        //    现场会以为"关机指令生效了一半"。所以成功也把原话带上。
        return result.Ok
            ? new AgentCommandOutcome(AgentReceivedOutcomes.Executed, $"{label}：{result.Message}")
            : new AgentCommandOutcome(AgentReceivedOutcomes.Failed, $"{label}失败：{result.Message}");
    }

    private async Task<AgentCommandOutcome> ExecuteVolumeAsync(
        string? action,
        string logCategory,
        CancellationToken cancellationToken)
    {
        if (!AgentVolumeActions.IsValid(action))
        {
            _log.Warn(logCategory, $"音量指令参数不合法：{action ?? "(空)"}（已忽略）");

            return AgentCommandOutcome.Ignored($"音量指令参数不合法：{action ?? "(空)"}");
        }

        // ── 回落：本平台**结构上**没有会话侧通道（Linux）时，交给服务侧的实现（M7.3）──
        //
        // 🔥 判据是 `IsSupported`（"这平台上有没有这条通道"），**不是**"这一次发送失败了"。
        //    用后者的话，Windows 上"托盘没起"会被误报成"本平台不支持音量"，
        //    现场会去查一个根本不存在的能力缺失 —— 而真正该做的是把托盘拉起来。
        //    非 Windows 上 `_session` 是 UnavailableSessionCommandChannel（`IsSupported` 恒 false）。
        if (!_session.IsSupported && _audio is not null)
        {
            return ExecuteVolumeLocally(action!, logCategory);
        }

        // 音量必须在会话侧执行。这里**刻意不先探一次可用性**：
        // SendAsync 自己会返回一句具体的失败原因（"当前没有交互式控制台会话" /
        // "托盘不在" / "管道无权连接"），比在这里笼统地说一句"通道不可用"有用得多，
        // 也少一次管道连接往返。
        var request = new SessionCommandRequest(SessionCommandMethods.Volume, action);

        var result = await _session.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // 成功时把会话侧读回来的**真实状态**记下来（"音量已调高：50%（未静音）"）——
        // 现场问"到底调到位没有"时，答案是这一行而不是"指令已发出"。
        if (result.Ok) _log.Info(logCategory, $"音量指令已执行 —— {result.Message}");
        else _log.Warn(logCategory, $"音量指令（{AgentVolumeActions.DisplayName(action)}）执行失败：{result.Message}");

        // 🔥 同样地，音量这条链路上"没反应"最常见的原因是**会话侧不在**
        //    （服务在 Session 0 里，托盘没跑 / 没人登录）—— 那句话必须原样带到页面上。
        return result.Ok
            ? new AgentCommandOutcome(AgentReceivedOutcomes.Executed, result.Message)
            : new AgentCommandOutcome(AgentReceivedOutcomes.Failed, result.Message);
    }

    /// <summary>
    /// 在<b>服务进程内</b>执行一次音量动作（M7.3，非 Windows 路径）。
    ///
    /// <para>
    /// 它存在的理由：Windows 上"用户会话里有一台托盘"这件事是当然的，
    /// 而 Linux 上<b>根本没有会话侧伴侣</b>（<see cref="ISessionCommandChannel"/> 在那边是
    /// <c>UnavailableSessionCommandChannel</c>）。如果就此放弃，Linux 的音量永远只能是
    /// "代码写了、接线零消费点"——正是本仓最忌讳的那种"看起来做了"。
    /// </para>
    ///
    /// <para>
    /// 真正的降权与 <c>XDG_RUNTIME_DIR</c> 处理在平台层
    /// （<c>LinuxAudioController</c> → <c>LinuxSessionCommands</c>），这里只做
    /// 动作名映射与结论翻译 —— 上层不写平台判断（铁律 #2）。
    /// </para>
    /// </summary>
    private AgentCommandOutcome ExecuteVolumeLocally(string action, string logCategory)
    {
        var mapped = action switch
        {
            AgentVolumeActions.Up => HostAudioAction.VolumeUp,
            AgentVolumeActions.Down => HostAudioAction.VolumeDown,
            AgentVolumeActions.Mute => HostAudioAction.Mute,
            _ => HostAudioAction.Unmute,
        };

        // ⚠️ `_audio` 的实现**绝不抛异常**（与 Windows 侧同一约定），
        //    所以这里不需要 try/catch —— 多包一层反而会掩盖"实现违约"这件事。
        var result = _audio!.Apply(mapped);

        if (result.Ok)
        {
            // 成功时把**操作后从系统读回来的真实状态**记下来（与通道那条路同一句话形）：
            // 现场问"到底调到位没有"，答案必须是这一行，而不是"指令已发出"。
            _log.Info(logCategory, $"音量指令已执行（服务侧）—— {result.Message}：{result.State.Describe()}");

            return new AgentCommandOutcome(
                AgentReceivedOutcomes.Executed,
                $"{result.Message}：{result.State.Describe()}");
        }

        _log.Warn(logCategory, $"音量指令（{AgentVolumeActions.DisplayName(action)}）执行失败：{result.Message}");

        return new AgentCommandOutcome(AgentReceivedOutcomes.Failed, result.Message);
    }
}
