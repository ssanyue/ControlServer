using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// 一次对接码派发的结果。
/// </summary>
/// <param name="Match">匹配结果（未命中时 <see cref="PairingMatchResult.Kind"/> = None）。</param>
/// <param name="Execution">
/// 动作执行结果；**未命中时为 <c>null</c>**（没有动作可执行，不是"执行失败"）。
/// </param>
public sealed record PairingDispatchOutcome(
    PairingMatchResult Match,
    ActionExecutionResult? Execution,

    /// <summary>
    /// 组合按钮的执行结果（仅 <see cref="PairingMatchKind.Combo"/> 非 null）。
    /// <para>
    /// ⚠️ <b>与 <see cref="Execution"/> 二选一</b>：组合是"一串按钮各自执行"，
    /// 硬塞进单个 <see cref="ActionExecutionResult"/> 会把"这几条属于哪个按钮"抹掉。
    /// </para>
    /// </summary>
    ComboExecutionResult? ComboExecution = null)
{
    /// <summary>是否命中。</summary>
    public bool Matched => Match.IsMatch;

    /// <summary>命中层级。</summary>
    public PairingMatchKind Kind => Match.Kind;

    /// <summary>命中对象名（未命中为空串）。</summary>
    public string OwnerLabel => Match.OwnerLabel;
}

/// <summary>
/// 对接码派发：把「收到的一包对接码」变成「一次真实的动作执行」。
/// <para>
/// 语义基准：旧版 <c>ListenerService.ListenAsync</c> 里的那段编排 ——
/// 查按钮 → 查子按钮 → 查动作 → 记录接收日志 → 执行 → 抛事件。
/// 抽成独立服务有两个好处：
/// <list type="number">
///   <item>不依赖 ASP.NET Core / 宿主，<b>可被单测用替身完整驱动</b>（假配置 + 假日志 + 真 UDP 回环）；</item>
///   <item>宿主的 HostedService 退化为"启监听 + 转发"，未来接别的事件源（TCP / 串口）也能复用这条链。</item>
/// </list>
/// </para>
/// <para>
/// <b>每次都取最新配置</b>：编辑页刚保存的对接码必须下一包就生效，因此这里不走
/// <c>CachedSettings</c> 快照，而是调 <see cref="ControlSettingsService.GetAsync"/>
/// （它内部按版本号判断，未变更时不重新反序列化，开销可接受）。
/// </para>
/// </summary>
public sealed class PairingDispatchService
{
    private readonly ControlSettingsService _settings;
    private readonly ActionExecutionService _executor;
    private readonly ITrafficLog _trafficLog;
    private readonly LogService _log;

    public PairingDispatchService(
        ControlSettingsService settings,
        ActionExecutionService executor,
        ITrafficLog trafficLog,
        LogService log)
    {
        _settings = settings;
        _executor = executor;
        _trafficLog = trafficLog;
        _log = log;
    }

    /// <summary>
    /// 处理一包对接码：匹配 → 记「接收」日志 → 执行命中动作 → 记命中摘要。
    /// </summary>
    /// <remarks>
    /// <b>异常语义</b>：本方法会把非取消类异常交给调用方（监听循环负责记录并继续），
    /// 但**执行动作时的失败不会抛出** —— <see cref="ActionExecutionService"/> 内部已按
    /// "失败不中断" 语义汇总成结果。取消令牌会正常向上传播。
    /// </remarks>
    public async Task<PairingDispatchOutcome> DispatchAsync(
        PairingDatagram datagram,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(datagram);

        var view = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var settings = view.Settings;

        var match = PairingMatchService.Match(
            datagram.Text,
            datagram.Payload,
            settings.Buttons,
            settings.SubButtonSets,
            settings.ComboButtons);

        // ═══════════ 未命中：只记「接收」日志，不执行任何动作 ═══════════
        if (!match.IsMatch)
        {
            _trafficLog.Write(TrafficLogEntry.Received(
                TransportProtocols.Udp,
                datagram.RemoteIp,
                datagram.RemotePort,
                datagram.Text,
                $"接收数据 {datagram.Text.Length} 字符"));

            _log.Info(
                "Listener",
                $"收到对接码 [{datagram.Length}B] 来自 {datagram.RemoteEndpoint} · 未命中任何对接码 · TEXT=\"{datagram.Text}\" · HEX={datagram.HexText}");

            return new PairingDispatchOutcome(match, null);
        }

        // ═══════════ 命中：先记「接收」日志（带命中对象名），再执行 ═══════════
        // 顺序与旧版一致（旧版也是先 LogReceive 再 ExecuteActionsAsync）。
        _trafficLog.Write(TrafficLogEntry.Received(
            TransportProtocols.Udp,
            datagram.RemoteIp,
            datagram.RemotePort,
            datagram.Text,
            match.ReceiveMessage,
            match.LogButtonName));

        // ═══════════ 组合按钮：走"逐段执行"那条路（2026-10-15，需求⑤）═══════════
        if (match.Kind == PairingMatchKind.Combo)
        {
            var combo = await _executor
                .ExecuteComboAsync(
                    new ComboPlan(match.OwnerLabel, match.Segments, match.SkippedButtonIds),
                    cancellationToken)
                .ConfigureAwait(false);

            _log.Info(
                "Listener",
                $"对接码 [{datagram.Length}B] 来自 {datagram.RemoteEndpoint} 命中{match.KindName}「{match.OwnerLabel}」→ {combo.Summary}");

            return new PairingDispatchOutcome(match, null, combo);
        }

        var execution = await _executor
            .ExecuteAsync(
                match.Actions,
                match.OwnerLabel,
                match.OverrideIp,
                match.OverridePort,
                cancellationToken)
            .ConfigureAwait(false);

        _log.Info(
            "Listener",
            $"对接码 [{datagram.Length}B] 来自 {datagram.RemoteEndpoint} 命中{match.KindName}「{match.OwnerLabel}」→ {execution.Summary}");

        return new PairingDispatchOutcome(match, execution);
    }
}
