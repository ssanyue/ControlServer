using ControlServer.Contracts;
using ControlServer.Core.Models;
using ControlServer.Core.Services;

namespace ControlServer.Agent.Tasks;

/// <summary>
/// 客户机上的<b>任务执行器</b>（M4.5）：把服务端下发的三张清单跑起来。
///
/// <para><b>它执行的是"配置"，不是"指令"</b>。指令是"现在做这件事"（一次性），
/// 配置是"以后一直这么做"（状态）。所以这里的模型是：<b>换一份配置 = 把上一份的
/// 全部执行体停掉、按新的一份重新起一遍</b>。这也是
/// <see cref="AgentConfigManager.Applied"/> 事件唯一的消费者。</para>
///
/// <para><b>为什么要"整代重启"而不是"按 Id 差量增删"</b>：差量要维护
/// "哪条任务对应哪个执行体"的映射，而配置里 Id 是前端生成的、可能被复制粘贴产生重复。
/// 整代重启的实现只有一个取消源，出错面小得多；代价是"改一条任务的循环间隔，
/// 其它任务的延时会重新计时"——这个代价在配置变更这种低频操作上完全可以接受。
/// （延时语义本身就是"相对于本次应用配置"，照搬参考项目。）</para>
///
/// <para>三类任务的执行位置：</para>
/// <list type="bullet">
///   <item><b>延时 / 循环发送</b>、<b>定时发送</b> —— <b>服务侧执行</b>（纯网络发送，不需要桌面）；</item>
///   <item><b>延时启动程序</b> —— <b>会话侧执行</b>（Session 0 里启动的程序用户看不见，
///         所以交给托盘，见 <see cref="ISessionCommandChannel"/>）。</item>
/// </list>
/// </summary>
public sealed class AgentTaskRunner : IAsyncDisposable
{
    /// <summary>发送原语（无状态，直接用）。</summary>
    private readonly NetworkService _network = new();

    private readonly LogService _log;
    private readonly ISessionCommandChannel _session;

    /// <summary>进程级取消源（停机时用）。</summary>
    private readonly CancellationTokenSource _lifetime = new();

    private readonly object _gate = new();

    /// <summary>当前这一代的取消源（换配置时取消它）。</summary>
    private CancellationTokenSource? _generation;

    /// <summary>当前这一代的工作任务（含"上一代的收尾"），停机时等它。</summary>
    private Task _running = Task.CompletedTask;

    /// <summary>同一份配置重复应用时，避免把"没有变化"也刷进日志。</summary>
    private int _lastAppliedVersion = -1;

    /// <summary>是否已经收过尾（<see cref="DisposeAsync"/> 幂等）。</summary>
    private bool _disposed;

    public AgentTaskRunner(LogService log, ISessionCommandChannel session)
    {
        _log = log;
        _session = session;
    }

    /// <summary>
    /// 按一份新配置重起全部任务。<b>不阻塞调用方</b>（任务在自己的后台跑）。
    /// </summary>
    public void Apply(AgentConfigDto config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var sendTasks = config.SendTasks?.Where(t => t.Enabled).ToList() ?? new List<AgentSendTaskDto>();
        var scheduledTasks = config.ScheduledTasks?.Where(t => t.Enabled).ToList() ?? new List<AgentScheduledTaskDto>();
        var launchTasks = config.LaunchTasks?.Where(t => t.Enabled).ToList() ?? new List<AgentLaunchTaskDto>();

        if (config.Version != _lastAppliedVersion)
        {
            _lastAppliedVersion = config.Version;

            _log.Info(
                "Tasks",
                $"按配置 v{config.Version} 重起任务：发送 {sendTasks.Count} / 定时 {scheduledTasks.Count} / "
                + $"启动 {launchTasks.Count}（将停止上一版的全部任务）");
        }

        lock (_gate)
        {
            var previous = _generation;
            var previousWork = _running;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _generation = cts;

            previous?.Cancel();

            var work = RunGenerationAsync(sendTasks, scheduledTasks, launchTasks, cts.Token);

            _running = TrackAsync(work, previous, previousWork);
        }
    }

    /// <summary>停掉全部任务（停机 / 被吊销时用）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _generation?.Cancel();
            _generation = null;
        }
    }

    /// <summary>
    /// 跑一代任务：把每条启用的任务拉成一个长期运行的后台任务。
    /// <para><b>永远不抛</b> —— 每条任务自己吞异常，最坏情况是那一条不再工作，
    /// 而不是"整台机器上的任务全停"。</para>
    /// </summary>
    private async Task RunGenerationAsync(
        List<AgentSendTaskDto> sendTasks,
        List<AgentScheduledTaskDto> scheduledTasks,
        List<AgentLaunchTaskDto> launchTasks,
        CancellationToken cancellationToken)
    {
        var workers = new List<Task>(sendTasks.Count + scheduledTasks.Count + 1);

        foreach (var task in sendTasks) workers.Add(RunSendAsync(task, cancellationToken));

        foreach (var task in scheduledTasks) workers.Add(RunScheduledAsync(task, cancellationToken));

        if (launchTasks.Count > 0) workers.Add(RunLaunchChainAsync(launchTasks, cancellationToken));

        if (workers.Count == 0) return;

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常路径：换配置 / 停机就是这么结束的。
        }
        catch (Exception ex)
        {
            // 铁律 #6：这一层只是兜底 —— 每条任务内部都已经吞过一遍异常了。
            _log.Error("Tasks", "任务批次异常结束（部分任务可能已停止）", ex);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // ① 延时发送 / 循环发送
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 延时发送：等 <c>DelaySeconds</c> 之后发一次；<c>IsLoop</c> 为真则之后每隔
    /// <c>LoopIntervalSeconds</c> 再发一次，直到换配置或停机。
    /// <para>
    /// <b>延时是"相对于本次应用配置"</b>（照搬参考项目的"相对于客户机启动"语义，
    /// 只是这里多了一个"配置可以在运行期换"的维度）：这样"改完配置重新下发"
    /// 会重新计时，与"刚上电"完全一致 —— 现场就是靠这个把多个设备按顺序拉起来的。
    /// </para>
    /// </summary>
    private async Task RunSendAsync(AgentSendTaskDto task, CancellationToken cancellationToken)
    {
        var label = Describe(task);
        var interval = TimeSpan.FromSeconds(
            Math.Clamp(task.LoopIntervalSeconds, AgentConfigDefaults.MinLoopIntervalSeconds, AgentConfigDefaults.MaxDelaySeconds));

        var delay = Math.Clamp(task.DelaySeconds, 0, AgentConfigDefaults.MaxDelaySeconds);

        var sends = 0;
        var failures = 0;

        try
        {
            if (delay > 0)
            {
                _log.Info("Tasks", $"发送任务「{label}」将在 {delay} 秒后{(task.IsLoop ? "开始循环" : "发送")}。");
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                // ── 编码 ──
                // 编码失败是**配置问题**（例如 Hex 串长度是奇数），明说，别让它看起来像网络故障。
                // 直接结束这条任务：每次循环都会以同样的原因失败，重试没有意义。
                byte[] payload;

                try
                {
                    payload = DataEncoder.Encode(task.DataType, task.Data);
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        "Tasks",
                        $"发送任务「{label}」的数据无法按 {task.DataType} 编码：{ex.Message}（该任务已跳过）");
                    return;
                }

                var result = await DispatchAsync(task.Protocol, task.TargetIp, task.TargetPort, payload, cancellationToken)
                    .ConfigureAwait(false);

                sends++;

                if (result.Ok)
                {
                    failures = 0;

                    // ⚠️ 循环任务**只在第一次**打成功日志：5 秒一次的成功日志
                    //    会在一夜之间把客户机的日志文件刷到几十 MB（M4.2 踩过同一个坑）。
                    if (sends == 1)
                    {
                        _log.Info(
                            "Tasks",
                            $"发送任务「{label}」{(task.IsLoop ? $"已开始循环（每 {interval.TotalSeconds:0} 秒一次）" : "已发送")}：{result.Message}");
                    }
                }
                else
                {
                    failures++;

                    if (ShouldLogFailure(failures))
                    {
                        _log.Warn("Tasks", $"发送任务「{label}」失败：{result.Message}（累计失败 {failures} 次）");
                    }
                }

                if (!task.IsLoop) return;

                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 换配置 / 停机：正常路径，不是故障。
        }
    }

    // ═══════════════════════════════════════════════════════════
    // ② 定时发送
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 定时发送：每天在若干个 <c>HH:mm</c> 时刻各发一次。
    /// <para>
    /// <b>不用"每秒比对时刻"的轮询</b>（服务端的 <c>TimerSchedulerService</c> 那么做是因为它要
    /// 处理秒级精度与漏触发补发）：客户机这里是分钟精度，直接算出"下一个触发点"再睡过去，
    /// 空闲时<b>每秒不做任何事</b> —— 300 台机器上这仍然是零成本的。
    /// </para>
    /// <para>
    /// <b>等待切成 ≤30 秒的小段</b>：长时间 <c>Task.Delay</c> 在系统休眠 / 时钟被改之后
    /// 会整体偏移，切成小段则每段都重新按当前时间算一次"下一个触发点"，
    /// 越过午夜的日期翻转也就自然处理掉了。
    /// </para>
    /// <para>时间源用 <see cref="BeijingTime.Now"/>（UTC+8）—— 与全项目时间口径一致。</para>
    /// </summary>
    private async Task RunScheduledAsync(AgentScheduledTaskDto task, CancellationToken cancellationToken)
    {
        var label = Describe(task);

        var times = ParseTimes(task.Times);

        if (times.Count == 0)
        {
            _log.Warn("Tasks", $"定时任务「{label}」没有可用的时刻（Times 为空或格式不对），已跳过。");
            return;
        }

        _log.Info(
            "Tasks",
            $"定时任务「{label}」已排期（每天 {string.Join("、", times.Select(t => t.ToString("HH:mm")))}）。");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = BeijingTime.Now;
                var next = NextOccurrence(times, now);
                var wait = next - now;

                if (wait > TimeChunk)
                {
                    await Task.Delay(TimeChunk, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }

                await SendScheduledOnceAsync(task, label, cancellationToken).ConfigureAwait(false);

                // ── 守住这一分钟 ──
                // 上一步的 Delay 可能因为定时器粒度在"还差几毫秒到点"时返回，
                // 于是下一次循环算出的"下一个触发点"还是同一分钟 → 同一时刻发两遍。
                // 跳过 61 秒即可彻底关掉这个窗口（且不影响下一分钟的触发点计算）。
                await Task.Delay(FireGuard, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 换配置 / 停机：正常路径。
        }
    }

    private async Task SendScheduledOnceAsync(
        AgentScheduledTaskDto task,
        string label,
        CancellationToken cancellationToken)
    {
        byte[] payload;

        try
        {
            payload = DataEncoder.Encode(task.DataType, task.Data);
        }
        catch (Exception ex)
        {
            _log.Warn(
                "Tasks",
                $"定时任务「{label}」的数据无法按 {task.DataType} 编码：{ex.Message}（本次触发已跳过）");
            return;
        }

        var result = await DispatchAsync(task.Protocol, task.TargetIp, task.TargetPort, payload, cancellationToken)
            .ConfigureAwait(false);

        if (result.Ok) _log.Info("Tasks", $"定时任务「{label}」已触发：{result.Message}");
        else _log.Warn("Tasks", $"定时任务「{label}」触发失败：{result.Message}");
    }

    // ═══════════════════════════════════════════════════════════
    // ③ 延时启动程序（会话侧）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 延时启动程序链。<b>串行累加延时</b>（照搬参考项目语义）：
    /// 第一个任务相对"本次应用配置生效"计时，之后每个任务相对<b>上一个任务完成</b>计时。
    /// <para>现场就是靠这个把多个程序按固定顺序、固定间隔依次拉起来的。</para>
    ///
    /// <para>
    /// 🔥 <b>先等用户会话出现，再开始计时</b>（见 <see cref="WaitForSessionAsync"/>）。
    /// 这两个动作的先后顺序不是随便定的：这类任务要做的就是把程序放到<b>用户的桌面</b>上，
    /// 而"开机把播放器拉起来"这个最常见的用法里，服务总是先于用户登录启动 ——
    /// 如果不等会话、上来就按延时计时然后失败，这批任务在<b>每一次开机时都不会执行</b>，
    /// 而这个"从不执行"在日志里只留下一行谁也不会去看的 WARN。
    /// </para>
    /// </summary>
    private async Task RunLaunchChainAsync(List<AgentLaunchTaskDto> tasks, CancellationToken cancellationToken)
    {
        if (!await WaitForSessionAsync(tasks, cancellationToken).ConfigureAwait(false)) return;

        try
        {
            foreach (var task in tasks)
            {
                var delay = Math.Clamp(task.DelaySeconds, 0, AgentConfigDefaults.MaxDelaySeconds);

                if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);

                var request = new SessionCommandRequest(
                    SessionCommandMethods.Launch,
                    ControlJson.Serialize(task));

                var result = await _session.SendAsync(request, cancellationToken).ConfigureAwait(false);

                var display = Display(task.Name, task.ProgramPath);

                if (result.Ok)
                {
                    // 成功时带上会话侧回的那句话（含 PID），而不是只说"已请求" ——
                    // 现场核对"到底起没起来、起的是哪一个"全靠它。
                    _log.Info("Tasks", $"已在用户会话中启动程序：{result.Message ?? display}");
                }
                else
                {
                    _log.Warn("Tasks", $"启动程序失败（{display}）：{result.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 换配置 / 停机：正常路径。
        }
    }

    /// <summary>
    /// 等用户会话就绪。返回 <c>true</c> = 会话侧通道此刻可用，可以往下走；
    /// 返回 <c>false</c> = <b>已被取消</b>，或者本平台结构上就没有这条通道。
    ///
    /// <para>
    /// 🔥 <b>为什么要等，而不是"不可用就跳过"</b>：这台机器上的托盘要等<b>用户登录</b>才会起来，
    /// 而客户端服务在开机时就跑起来了。所以在服务启动后的头几十秒里，
    /// "通道不可用"是<b>常态而不是故障</b>。此刻若直接跳过，等价于"开机自启的程序永远不启动"。
    /// </para>
    ///
    /// <para>
    /// <b>等待不设上限</b>（换配置 / 停机 / 被吊销都会把它取消掉）：
    /// 一直没人登录时，这些任务本来就无法执行，而"挂在这里"的成本是一个睡着的任务，
    /// 换来的好处是"用户一登录，程序就按配置被拉起来"。
    /// </para>
    /// </summary>
    private async Task<bool> WaitForSessionAsync(List<AgentLaunchTaskDto> tasks, CancellationToken cancellationToken)
    {
        if (_session.IsAvailable) return true;

        // 结构上就没有这条通道（非 Windows / 未接线）：等下去毫无意义，明说后放弃。
        if (!_session.IsSupported)
        {
            _log.Warn("Tasks", BuildLaunchSkippedMessage(tasks));
            return false;
        }

        _log.Info(
            "Tasks",
            $"配置里有 {tasks.Count} 条延时启动任务，但用户会话尚未就绪（无人登录或托盘未启动）—— "
            + "已开始等待，用户登录后会继续。");

        var waited = TimeSpan.Zero;
        var nextReminder = SessionWaitReminder;

        try
        {
            while (true)
            {
                await Task.Delay(SessionPollInterval, cancellationToken).ConfigureAwait(false);

                waited += SessionPollInterval;

                if (_session.IsAvailable)
                {
                    _log.Info("Tasks", $"用户会话已就绪（等待 {waited.TotalSeconds:0} 秒），继续执行延时启动任务。");
                    return true;
                }

                if (waited >= nextReminder)
                {
                    nextReminder += SessionWaitReminder;

                    // 这句必须把"怎么办"一起说出来：现场看到它时，
                    // 问题十有八九是这台机器上根本没装托盘（或托盘被用户退出了）。
                    _log.Warn(
                        "Tasks",
                        $"已等待用户会话 {waited.TotalMinutes:0} 分钟，延时启动任务仍未执行。"
                        + "若本机没有安装（或没有启动）托盘伴侣，这类任务不会执行 —— "
                        + "程序必须在用户会话里启动，服务会话里启动的程序用户看不见。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 换配置 / 停机：正常路径。
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 发送原语
    // ═══════════════════════════════════════════════════════════

    /// <summary>按协议把字节发出去。<b>任何异常都转成失败的 <see cref="SendResult"/></b>。</summary>
    private async Task<SendResult> DispatchAsync(
        string? protocol,
        string targetIp,
        int targetPort,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        try
        {
            var normalized = protocol?.Trim();

            if (string.Equals(normalized, TransportProtocols.Udp, StringComparison.OrdinalIgnoreCase))
            {
                return await _network.SendUdpAsync(targetIp, targetPort, payload, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(normalized, TransportProtocols.Tcp, StringComparison.OrdinalIgnoreCase))
            {
                return await _network.SendTcpAsync(targetIp, targetPort, payload, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(normalized, TransportProtocols.Wol, StringComparison.OrdinalIgnoreCase))
            {
                // WoL 的 Data 是 MAC 地址文本（与 M2 的动作语义一致），不是普通载荷。
                return new SendResult(false, 0, "发送任务暂不支持 WoL（请用「网络唤醒」动作）");
            }

            return new SendResult(false, 0, $"未知的协议：{protocol}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SendResult(false, 0, ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    /// <summary>等待切块上限：每段最多睡这么久，之后重新算一次"下一个触发点"。</summary>
    private static readonly TimeSpan TimeChunk = TimeSpan.FromSeconds(30);

    /// <summary>触发后跳过这一分钟，避开定时器粒度造成的重复触发。</summary>
    private static readonly TimeSpan FireGuard = TimeSpan.FromSeconds(61);

    /// <summary>等待用户会话时，多久探一次"托盘起来了没有"。</summary>
    private static readonly TimeSpan SessionPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 等待用户会话时，多久提醒一次"还在等"。
    /// <para>30 分钟：既不会把日志刷爆（一天最多 48 行），又不至于让"没人登录"这件事
    /// 在日志里彻底消失 —— 现场排查"开机自启没生效"时，靠的就是这一行。</para>
    /// </summary>
    private static readonly TimeSpan SessionWaitReminder = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 第 1 次、之后每 20 次记一条失败日志。
    /// <para>与遥测上报同一套节流：循环任务失败时每秒一条会把日志刷爆，
    /// 而"第 1 条 + 周期性一条"既能看到出事了、又能看出持续多久。</para>
    /// </summary>
    private static bool ShouldLogFailure(int streak) => streak == 1 || streak % 20 == 0;

    /// <summary>把 <c>HH:mm</c>（也容忍 <c>H:mm</c> / 全角冒号 / 带秒）解析成时刻，去重并排序。</summary>
    private static List<TimeOnly> ParseTimes(IReadOnlyList<string>? raw)
    {
        var result = new List<TimeOnly>();

        if (raw is null) return result;

        foreach (var item in raw)
        {
            if (TryParseTime(item, out var time) && !result.Contains(time)) result.Add(time);
        }

        result.Sort();
        return result;
    }

    private static bool TryParseTime(string? value, out TimeOnly time)
    {
        time = default;

        if (string.IsNullOrWhiteSpace(value)) return false;

        // 全角冒号是现场手抄时刻最容易带进来的字符。
        var normalized = value.Trim().Replace('：', ':');

        // 优先按 HH:mm 解析；再退一步容忍 "9:5" 与带秒的写法。
        if (TimeOnly.TryParseExact(normalized, "HH:mm", out time)) return true;
        if (TimeOnly.TryParseExact(normalized, "H:mm", out time)) return true;
        if (TimeOnly.TryParseExact(normalized, "HH:mm:ss", out time)) return true;
        if (TimeOnly.TryParseExact(normalized, "H:mm:ss", out time)) return true;

        return false;
    }

    /// <summary>算出严格晚于 <paramref name="now"/> 的下一个触发点（跨日自动落到明天）。</summary>
    private static DateTimeOffset NextOccurrence(IReadOnlyList<TimeOnly> times, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.DateTime);
        var best = DateTimeOffset.MaxValue;

        foreach (var time in times)
        {
            var candidate = new DateTimeOffset(today.ToDateTime(time), now.Offset);

            // "严格晚于"：等于当前时刻也算今天已过 —— 否则触发后的同一秒会再算回自己。
            if (candidate <= now) candidate = candidate.AddDays(1);

            if (candidate < best) best = candidate;
        }

        return best == DateTimeOffset.MaxValue ? now.AddDays(1) : best;
    }

    private static string Describe(AgentSendTaskDto task)
        => Display(task.Name, $"{task.Protocol} {task.TargetIp}:{task.TargetPort}");

    private static string Describe(AgentScheduledTaskDto task)
        => Display(task.Name, $"{task.Protocol} {task.TargetIp}:{task.TargetPort}");

    private static string Display(string? name, string fallback)
        => string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();

    private static string BuildLaunchSkippedMessage(List<AgentLaunchTaskDto> tasks)
        => $"配置里有 {tasks.Count} 条延时启动任务，但本机会话侧通道不可用 —— 已跳过。"
           + "（服务在 Session 0 里启动的程序用户看不见，这类任务必须由用户会话中的托盘执行；"
           + "本平台尚未提供托盘伴侣。）";

    /// <summary>
    /// 收尾：等这一代任务真正退出，再释放它自己的取消源。
    /// <para>⚠️ <b>不能"取消完立刻 Dispose"</b>：正在 <c>Task.Delay</c> 上的任务
    /// 还没摘掉自己在取消源上的注册，这时候 Dispose 会让它抛
    /// <c>ObjectDisposedException</c> —— 一个只在"改配置的瞬间恰好到点"时才出现的偶发异常。</para>
    /// </summary>
    private static async Task TrackAsync(Task work, CancellationTokenSource? previous, Task previousWork)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // RunGenerationAsync 内部已全量捕获，这里只是不让异常冒到未观测状态。
        }

        if (previous is null) return;

        try
        {
            await previousWork.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 同上。
        }

        previous.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        // 幂等：本类既是 DI 单例（宿主停机时会 Dispose 一次），
        // 也会被 AgentRuntime 在主循环收尾时显式收一次。第二次必须是空操作。
        if (_disposed) return;

        _disposed = true;

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            // CancelAsync 只会在取消回调抛异常时报错；这里没有回调，正常不会走到。
        }

        Task running;

        lock (_gate)
        {
            _generation?.Cancel();
            running = _running;
        }

        try
        {
            await running.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 同上：任务内部已吞掉异常。
        }

        lock (_gate)
        {
            _generation?.Dispose();
            _generation = null;
        }

        _lifetime.Dispose();
    }
}
