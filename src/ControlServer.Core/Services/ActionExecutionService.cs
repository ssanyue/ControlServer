using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// 单条动作的执行结果。
/// </summary>
/// <param name="Index">在本次执行序列中的下标（从 0 起）。</param>
/// <param name="ActionName">动作名（为空时回退为协议名，与旧版一致）。</param>
/// <param name="Protocol">实际使用的协议（指令库解析后的值）。</param>
/// <param name="TargetIp">实际目标 IP（已应用 override）。</param>
/// <param name="TargetPort">实际目标端口（已应用 override）。</param>
/// <param name="Ok">是否发送成功。</param>
/// <param name="BytesSent">实际发出字节数。</param>
/// <param name="Message">结果说明。</param>
/// <param name="DataPreview">落到日志里的数据展示（Hex 回显原文 / ANSI 按 GBK 还原）。</param>
/// <param name="ResolvedFromCommand">若该动作经「指令库」解析，这里是命中的指令名；否则为 null。</param>
public sealed record ActionStepResult(
    int Index,
    string ActionName,
    string Protocol,
    string TargetIp,
    int TargetPort,
    bool Ok,
    int BytesSent,
    string Message,
    string DataPreview,
    string? ResolvedFromCommand);

/// <summary>
/// 一次「多动作执行」的整体结果。
/// </summary>
/// <param name="RunId">本次执行标识（8 位，便于在日志里对账）。</param>
/// <param name="ButtonName">所属按钮 / 子按钮名称（用于日志与前端提示）。</param>
/// <param name="StartedAt">开始时间（北京时间）。</param>
/// <param name="Elapsed">总耗时（含各动作前置延迟）。</param>
/// <param name="Steps">逐条动作的结果，顺序与配置一致。</param>
public sealed record ActionExecutionResult(
    string RunId,
    string ButtonName,
    DateTimeOffset StartedAt,
    TimeSpan Elapsed,
    IReadOnlyList<ActionStepResult> Steps)
{
    /// <summary>成功条数。</summary>
    public int SuccessCount => Steps.Count(s => s.Ok);

    /// <summary>失败条数。</summary>
    public int FailureCount => Steps.Count(s => !s.Ok);

    /// <summary>整体是否成功（<b>无失败即成功</b>；空序列视为成功）。单条失败不中断后续，沿用旧版语义。</summary>
    public bool Ok => FailureCount == 0;

    /// <summary>一句话摘要，用于 SignalR 推送与前端 toast。</summary>
    public string Summary
    {
        get
        {
            if (Steps.Count == 0) return $"{ButtonName}：没有可执行的动作";

            var elapsedMs = (long)Elapsed.TotalMilliseconds;
            return FailureCount == 0
                ? $"{ButtonName}：{SuccessCount} 条动作全部成功（{elapsedMs}ms）"
                : $"{ButtonName}：{SuccessCount} 成功 / {FailureCount} 失败（{elapsedMs}ms）";
        }
    }
}

/// <summary>
/// 组合按钮里**一段**的执行结果（2026-10-15，需求⑤）。
/// </summary>
/// <param name="ButtonId">成员按钮 Id（引用）。</param>
/// <param name="ButtonLabel">成员按钮显示名（按钮后来被改名时，这里是**执行那一刻**的名字）。</param>
/// <param name="DelayAfterMs">本段执行完之后等待的毫秒数（最后一段恒为 0）。</param>
/// <param name="Result">这一段内部的动作执行结果（<c>null</c> = 这一段的按钮没有动作可执行）。</param>
/// <param name="Skipped">被跳过的原因；正常执行为 <c>null</c>。</param>
public sealed record ComboSegmentOutcome(
    string ButtonId,
    string ButtonLabel,
    int DelayAfterMs,
    ActionExecutionResult? Result,
    string? Skipped = null);

/// <summary>
/// 一次「组合按钮执行」的整体结果。
///
/// <para>
/// 🔥 <b>它是"分段"的，不是"动作平铺"的</b>：组合的语义是"先执行按钮 A、等 500ms、再执行按钮 B"，
/// 而每个成员各自有它的名字、它的动作序列、它自己的成败 ——
/// 平铺成一条动作列表会把"这几条属于哪个按钮"这个信息丢掉，
/// 现场看到"第 3 条失败"时根本不知道是哪个按钮的第几条。
/// </para>
/// </summary>
/// <param name="RunId">本次执行标识（8 位，便于在日志里对账）。</param>
/// <param name="ComboLabel">组合按钮显示名。</param>
/// <param name="StartedAt">开始时间（北京时间）。</param>
/// <param name="Elapsed">总耗时（含各段延迟）。</param>
/// <param name="Segments">按执行顺序的每一段。</param>
/// <param name="SkippedButtonIds">被跳过的成员 Id（对应主按钮已不存在）。</param>
public sealed record ComboExecutionResult(
    string RunId,
    string ComboLabel,
    DateTimeOffset StartedAt,
    TimeSpan Elapsed,
    IReadOnlyList<ComboSegmentOutcome> Segments,
    IReadOnlyList<string> SkippedButtonIds)
{
    /// <summary>全部动作条数（各段相加）。</summary>
    public int TotalSteps => Segments.Sum(static s => s.Result?.Steps.Count ?? 0);

    /// <summary>成功条数。</summary>
    public int SuccessCount => Segments.Sum(static s => s.Result?.SuccessCount ?? 0);

    /// <summary>失败条数。</summary>
    public int FailureCount => Segments.Sum(static s => s.Result?.FailureCount ?? 0);

    /// <summary>整体是否成功（<b>无失败即成功</b>；一段都没有也视为成功，与单按钮口径一致）。</summary>
    public bool Ok => FailureCount == 0;

    /// <summary>一句话摘要（用于日志、SignalR 推送与前端提示）。</summary>
    public string Summary
    {
        get
        {
            var elapsedMs = (long)Elapsed.TotalMilliseconds;

            if (Segments.Count == 0)
            {
                return SkippedButtonIds.Count > 0
                    ? $"{ComboLabel}：没有可执行的成员（{SkippedButtonIds.Count} 个成员指向的按钮已不存在）"
                    : $"{ComboLabel}：组合里还没有成员";
            }

            var headline = FailureCount == 0
                ? $"{ComboLabel}：{Segments.Count} 个成员 / {SuccessCount} 条动作全部成功（{elapsedMs}ms）"
                : $"{ComboLabel}：{Segments.Count} 个成员 / {SuccessCount} 成功 / {FailureCount} 失败（{elapsedMs}ms）";

            return SkippedButtonIds.Count > 0
                ? headline + $"；另有 {SkippedButtonIds.Count} 个成员指向的按钮已不存在"
                : headline;
        }
    }
}

/// <summary>
/// 动作执行服务：把一组 <see cref="ButtonAction"/> 按旧版语义逐条发出去。
/// <para>
/// <b>行为对齐点（M2 方案 §5.3 / §11.3，逐条照旧版 <c>NetworkService.ExecuteActionsAsync</c> 复刻）</b>：
/// <list type="number">
///   <item><b>前置延迟</b>：第 i 条的 <c>DelayMs</c> 表示「上一条完成后等待这么久再发本动作」；
///         <b>第 0 条不等待</b>（其 DelayMs 不生效）。</item>
///   <item><b>失败不中断</b>：某条失败后继续执行后续动作，最终汇总成功 / 失败条数。</item>
///   <item><b>子按钮 override</b>：<c>overrideIp</c> 非空时，所有动作的统一 IP / 端口被覆盖，
///         且 <c>WoL</c> 被**强制降级为 UDP**（子按钮集不支持网络唤醒）。</item>
///   <item><b>指令库</b>：<c>DataType = 指令库</c> 时用 <c>DataContent</c> 当**指令名**去解析，
///         解析出的协议 / 类型 / 数据完全取代原动作；<b>未找到则本条记失败但继续</b>。</item>
///   <item><b>WoL</b>：端口为 0 用 9，目标为空或 <c>0.0.0.0</c> 用 <c>255.255.255.255</c>。</item>
/// </list>
/// </para>
/// <para>
/// <b>依赖注入的都是端口</b>（<see cref="ITrafficLog"/> / <see cref="ICommandLibrary"/> / <see cref="INotifier"/>），
/// 因此本服务可被单测完整驱动，不需要真实数据库或 SignalR。
/// </para>
/// </summary>
public sealed class ActionExecutionService
{
    private readonly NetworkService _network;
    private readonly ITrafficLog _trafficLog;
    private readonly ICommandLibrary _commands;
    private readonly INotifier _notifier;
    private readonly LogService _log;

    public ActionExecutionService(
        NetworkService network,
        ITrafficLog trafficLog,
        ICommandLibrary commands,
        INotifier notifier,
        LogService log)
    {
        _network = network;
        _trafficLog = trafficLog;
        _commands = commands;
        _notifier = notifier;
        _log = log;
    }

    /// <summary>
    /// 顺序执行一组动作。
    /// </summary>
    /// <param name="actions">动作列表（按配置顺序）。</param>
    /// <param name="buttonLabel">所属按钮 / 子按钮名称，仅用于日志与结果展示。</param>
    /// <param name="overrideIp">统一覆盖目标 IP（子按钮场景传所属集合的 IP）。<c>null</c> = 不覆盖。</param>
    /// <param name="overridePort">统一覆盖目标端口；<c>null</c> 时保留动作自身端口。</param>
    /// <param name="cancellationToken">取消令牌。取消是**唯一**会中断整个序列的情况。</param>
    public async Task<ActionExecutionResult> ExecuteAsync(
        IReadOnlyList<ButtonAction> actions,
        string? buttonLabel = null,
        string? overrideIp = null,
        int? overridePort = null,
        CancellationToken cancellationToken = default)
    {
        var list = actions ?? Array.Empty<ButtonAction>();
        var label = string.IsNullOrWhiteSpace(buttonLabel) ? "未命名" : buttonLabel!;
        var runId = NewRunId();

        var startedAt = BeijingTime.Now;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var steps = new List<ActionStepResult>(list.Count);

        for (var i = 0; i < list.Count; i++)
        {
            var action = list[i];

            // ── 前置延迟：第 0 条不等待（其 DelayMs 不生效）──
            if (i > 0 && action.DelayMs > 0)
            {
                await Task.Delay(action.DelayMs, cancellationToken).ConfigureAwait(false);
            }

            var effective = ApplyOverride(action, overrideIp, overridePort);
            var step = await SendOneAsync(effective, i, label, cancellationToken).ConfigureAwait(false);
            steps.Add(step);
        }

        sw.Stop();

        var result = new ActionExecutionResult(runId, label, startedAt, sw.Elapsed, steps);

        _log.Write(
            result.Ok ? LogLevelKind.Info : LogLevelKind.Warning,
            "Action",
            $"执行「{label}」：{result.Summary}");

        await NotifyAsync(result, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// **按组合按钮执行**：逐段执行成员按钮的全部动作，段间按配置等待延迟（2026-10-15，需求⑤）。
    ///
    /// <para>
    /// <b>为什么复用 <see cref="ExecuteAsync"/> 而不是把成员的动作拍平成一个列表</b>：
    /// </para>
    /// <list type="bullet">
    ///   <item>每个成员要有**自己的 runId / 日志** —— 现场排障时"是哪个按钮出的问题"
    ///         必须一眼可见（拍平之后只剩一个笼统的"组合执行了 12 条"）；</item>
    ///   <item>成员的失败**不中断**后续成员（与"单按钮内某条动作失败不中断"是同一口径）——
    ///         复用现成的执行器天然就是这样；</item>
    ///   <item>段间延迟与成员内部的"首条动作前置延迟"是**两个独立概念**，各走各的，
    ///         不会出现"两个延迟叠在一起算不清"。</item>
    /// </list>
    ///
    /// <para>
    /// <b>顺序是严格的</b>：第 N 段一定在第 N-1 段（含它内部的延迟）**全部完成**之后才开始。
    /// </para>
    /// </summary>
    /// <param name="plan">组合解析结果（见 <see cref="ComboResolver"/>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ComboExecutionResult> ExecuteComboAsync(
        ComboPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var runId = NewRunId();
        var startedAt = BeijingTime.Now;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var outcomes = new List<ComboSegmentOutcome>(plan.Segments.Count);

        foreach (var segment in plan.Segments)
        {
            // 成员按钮自己没有动作：**照样记一段**（记成 skipped）——
            // 少一段会让"组合里有 3 个成员、结果只有 2 段"变得没法解释。
            if (segment.Button.Actions is null || segment.Button.Actions.Count == 0)
            {
                outcomes.Add(new ComboSegmentOutcome(
                    segment.Button.Id,
                    segment.Button.Label,
                    segment.DelayAfterMs,
                    null,
                    "这个按钮里没有动作"));

                await DelayAsync(segment.DelayAfterMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var result = await ExecuteAsync(
                segment.Button.Actions,
                segment.Button.Label,
                overrideIp: null,
                overridePort: null,
                cancellationToken).ConfigureAwait(false);

            outcomes.Add(new ComboSegmentOutcome(
                segment.Button.Id,
                segment.Button.Label,
                segment.DelayAfterMs,
                result));

            // ⚠️ 延迟放在**本段之后**：语义是"每两个成员之间等多久"，
            //    最后一段的延迟在解析时已被归零（见 ComboResolver）。
            await DelayAsync(segment.DelayAfterMs, cancellationToken).ConfigureAwait(false);
        }

        sw.Stop();

        var combo = new ComboExecutionResult(
            runId,
            plan.ComboLabel,
            startedAt,
            sw.Elapsed,
            outcomes,
            plan.SkippedButtonIds);

        _log.Write(
            combo.Ok ? LogLevelKind.Info : LogLevelKind.Warning,
            "Action",
            $"执行组合「{combo.ComboLabel}」：{combo.Summary}");

        return combo;
    }

    /// <summary>段间等待。抽出来只为一件事：让"0 毫秒不去调 Task.Delay"这条不言自明。</summary>
    private static Task DelayAsync(int delayMs, CancellationToken cancellationToken)
        => delayMs > 0 ? Task.Delay(delayMs, cancellationToken) : Task.CompletedTask;

    // ═══════════════════════════════════════════════════════════
    // 单条动作
    // ═══════════════════════════════════════════════════════════

    private async Task<ActionStepResult> SendOneAsync(
        ButtonAction action,
        int index,
        string buttonLabel,
        CancellationToken cancellationToken)
    {
        // 动作名回退为协议名 —— 与旧版一致。
        var actionName = !string.IsNullOrWhiteSpace(action.Name) ? action.Name : action.Protocol;
        string? resolvedFrom = null;

        // ── 指令库解析（先于 try：旧版同样在 try 之外，解析失败单独记失败）──
        if (DataEncoder.IsCommandLibrary(action.DataType))
        {
            var commandName = action.DataContent;
            var command = await _commands.ResolveAsync(commandName, cancellationToken).ConfigureAwait(false);
            if (command is null)
            {
                var msg = $"指令库解析失败：未找到指令「{commandName}」";
                _trafficLog.Write(TrafficLogEntry.Sent(
                    action.Protocol, action.TargetIp, action.TargetPort,
                    commandName, false, msg, buttonLabel, actionName));

                return Failed(index, actionName, action.Protocol, action.TargetIp, action.TargetPort, msg, commandName, null);
            }

            resolvedFrom = command.Name;
            action = ApplyCommand(action, command);
        }

        try
        {
            if (IsWol(action.Protocol))
            {
                return await SendWakeOnLanAsync(action, index, buttonLabel, actionName, resolvedFrom, cancellationToken)
                    .ConfigureAwait(false);
            }

            var data = DataEncoder.Encode(action.DataType, action.DataContent);
            var preview = DataEncoder.Preview(action.DataType, data, action.DataContent);

            var send = IsTcp(action.Protocol)
                ? await _network.SendTcpAsync(action.TargetIp, action.TargetPort, data, cancellationToken).ConfigureAwait(false)
                : await _network.SendUdpAsync(action.TargetIp, action.TargetPort, data, cancellationToken).ConfigureAwait(false);

            _trafficLog.Write(TrafficLogEntry.Sent(
                action.Protocol, action.TargetIp, action.TargetPort,
                preview, send.Ok, send.Message, buttonLabel, actionName));

            return new ActionStepResult(
                index, actionName, action.Protocol, action.TargetIp, action.TargetPort,
                send.Ok, send.BytesSent, send.Message, preview, resolvedFrom);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 编码异常（Hex 非法 / 奇数长度）与网络异常都走这里 —— 与旧版一致：记失败、继续下一条。
            var msg = $"{action.Protocol} -> {action.TargetIp}:{action.TargetPort} 失败: {ex.Message}";
            _trafficLog.Write(TrafficLogEntry.Sent(
                action.Protocol, action.TargetIp, action.TargetPort,
                action.DataContent, false, msg, buttonLabel, actionName));

            return Failed(index, actionName, action.Protocol, action.TargetIp, action.TargetPort, msg, action.DataContent, resolvedFrom);
        }
    }

    private async Task<ActionStepResult> SendWakeOnLanAsync(
        ButtonAction action,
        int index,
        string buttonLabel,
        string actionName,
        string? resolvedFrom,
        CancellationToken cancellationToken)
    {
        try
        {
            var macBytes = DataEncoder.TryParseMac(action.DataContent);
            if (macBytes is null)
            {
                var failMsg = $"MAC 地址格式无效: {action.DataContent}";
                _trafficLog.Write(TrafficLogEntry.Sent(
                    TransportProtocols.Wol, action.TargetIp, action.TargetPort,
                    action.DataContent, false, failMsg, buttonLabel, actionName));

                return Failed(index, actionName, TransportProtocols.Wol, action.TargetIp, action.TargetPort, failMsg, action.DataContent, resolvedFrom);
            }

            var packet = DataEncoder.BuildMagicPacket(macBytes);
            var port = action.TargetPort > 0 ? action.TargetPort : DataEncoder.DefaultWolPort;
            var host = DataEncoder.NormalizeWolHost(action.TargetIp);

            var send = await _network.SendWakeOnLanAsync(host, port, packet, cancellationToken).ConfigureAwait(false);

            var msg = send.Ok
                ? $"WoL -> {host}:{port} MAC={action.DataContent}"
                : $"WoL 失败: {send.Message}";

            _trafficLog.Write(TrafficLogEntry.Sent(
                TransportProtocols.Wol, host, port,
                action.DataContent, send.Ok, msg, buttonLabel, actionName));

            return new ActionStepResult(
                index, actionName, TransportProtocols.Wol, host, port,
                send.Ok, send.BytesSent, msg, action.DataContent, resolvedFrom);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var msg = $"WoL 失败: {ex.Message}";
            _trafficLog.Write(TrafficLogEntry.Sent(
                TransportProtocols.Wol, action.TargetIp, action.TargetPort,
                action.DataContent, false, msg, buttonLabel, actionName));

            return Failed(index, actionName, TransportProtocols.Wol, action.TargetIp, action.TargetPort, msg, action.DataContent, null);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 语义辅助
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 子按钮场景的统一覆盖：IP 覆盖、端口可选覆盖、<b>WoL 强制降级为 UDP</b>。
    /// <paramref name="overrideIp"/> 为 null 时**原样返回**（连 WoL 都不动）。
    /// </summary>
    private static ButtonAction ApplyOverride(ButtonAction action, string? overrideIp, int? overridePort)
    {
        if (overrideIp is null) return action;

        return new ButtonAction
        {
            Id = action.Id,
            Name = action.Name,
            TargetIp = overrideIp,
            TargetPort = overridePort ?? action.TargetPort,
            Protocol = IsWol(action.Protocol) ? TransportProtocols.Udp : action.Protocol,
            DataType = action.DataType,
            DataContent = action.DataContent,
            DelayMs = action.DelayMs,
            PairingCode = action.PairingCode,
            PairingCodeType = action.PairingCodeType,
        };
    }

    /// <summary>
    /// 用指令库条目替换动作的协议 / 类型 / 数据（其余字段保留）。
    /// 与旧版一致：指令的协议优先，指令为空时回退动作自身协议。
    /// </summary>
    private static ButtonAction ApplyCommand(ButtonAction action, CommandItem command)
        => new()
        {
            Id = action.Id,
            Name = action.Name,
            TargetIp = action.TargetIp,
            TargetPort = action.TargetPort,
            Protocol = string.IsNullOrWhiteSpace(command.Protocol) ? action.Protocol : command.Protocol,
            DataType = command.DataType,
            DataContent = command.Data,
            DelayMs = action.DelayMs,
            PairingCode = action.PairingCode,
            PairingCodeType = action.PairingCodeType,
        };

    /// <summary>
    /// 指令库解析后动作已不是原对象（<see cref="ApplyCommand"/> 会把 DataType 换成指令的真实类型），
    /// 因此「来源」在 <see cref="SendOneAsync"/> 里用局部变量显式记录，不从这里推断。
    /// </summary>
    private static ActionStepResult Failed(
        int index,
        string actionName,
        string protocol,
        string targetIp,
        int targetPort,
        string message,
        string preview,
        string? resolvedFrom)
        => new(index, actionName, protocol, targetIp, targetPort, false, 0, message, preview, resolvedFrom);

    private static bool IsWol(string? protocol)
        => string.Equals(protocol, TransportProtocols.Wol, StringComparison.OrdinalIgnoreCase);

    private static bool IsTcp(string? protocol)
        => string.Equals(protocol, TransportProtocols.Tcp, StringComparison.OrdinalIgnoreCase);

    private async Task NotifyAsync(ActionExecutionResult result, CancellationToken cancellationToken)
    {
        try
        {
            await _notifier
                .ActionResultAsync(
                    new ActionResultDto(result.RunId, result.Ok, result.Summary, BeijingTime.Now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 推送失败不影响执行结果（前端拿不到推送还能靠轮询 / 下次请求）。
            _log.Warn("Action", $"动作结果推送失败：{ex.Message}");
        }
    }

    private static string NewRunId() => Guid.NewGuid().ToString("N")[..8];
}
