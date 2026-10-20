using ControlServer.Contracts;
using ControlServer.Core.Services;
using ControlServer.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ControlServer.Server.Services;

/// <summary>
/// <b>输入回传的中继与合并</b>（M5.2）—— 中心侧唯一一处把输入<b>发给被控机</b>的地方。
///
/// <para>
/// 🔥 <b>为什么它不是 <see cref="RemoteSessionRegistry"/> 的一部分</b>：
/// 账本（"谁在推、开关开没开"）要能被 Hub、被清理任务、被控制器<b>随便读</b>；
/// 而本类会<b>发网络消息</b>。把两者放进一个类之后，
/// "我只是想读一下有没有这个会话"就变成了一个可能发出指令的动作 ——
/// 而那正是账本那条注释里点名要避免的"上帝对象"。
/// </para>
///
/// <para>
/// 🔥 <b>它做一件不做就会毁掉体验的事：合并连续移动</b>。
/// 鼠标划过屏幕时，浏览器会以几百赫兹吐 <c>mousemove</c>；
/// 一条一条转发的话，被控机的注入循环会被一堆<b>已经过时</b>的位置淹没
/// —— 而每个位置的处置都是"移动指针"，人眼只关心最后停在哪。
/// 所以：<b>相邻的移动只留最新的一条</b>，其余三类事件（按键 / 滚轮 / 鼠标键）<b>一条不丢</b>
/// （丢掉"抬起"的后果是那个键在被控机上一直按着）。
/// </para>
///
/// <para>
/// ⚠️ <b>合并只发生在"还没发出去"的缓冲里</b>：已经下发的事件绝不会被后续事件追回，
/// 所以它<u>不会</u>改变语义，只减少冗余。
/// </para>
/// </summary>
public sealed class RemoteInputRelay
{
    /// <summary>
    /// 两次下发之间的最小间隔（≈ 60 Hz）。
    ///
    /// <para>
    /// 🔥 <b>它是"合并"真正生效的地方</b>：没有按时间节流，合并只能在
    /// "同一瞬间到达的事件"之间生效，而 <c>mousemove</c> 天生就是一条一条到的。
    /// 60 Hz 是"看不出来卡"与"不把被控端打爆"之间的常见取值，与显示器刷新率同量级。
    /// </para>
    /// <para>
    /// ⚠️ 含非移动事件的批次<b>立刻下发、不等这个间隔</b>：按键 / 点击是离散动作，
    /// 让人多等 16 毫秒毫无意义；而它们在顺序上必须压在"它前面那条移动"之后
    /// （一次"先移动再点击"如果反了，点中的就是上一个位置）。
    /// </para>
    /// </summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// 单次 <c>InvokeAsync</c> 的等待上限。
    /// <para>
    /// ⚠️ 必须有：被控端那条连接可能刚好处在半死状态（TCP 还没超时，但对面不响应）。
    /// 没有上限的话，浏览器的每一次输入都会堆积在这里，
    /// 而表现是"点一下、十几秒后光标才开始乱飞" —— 那种滞后比直接失败更难查。
    /// </para>
    /// </summary>
    private static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 每个会话最多缓存多少条"还没发出去"的事件。
    /// <para>超出后丢弃<b>最旧的</b>（并计入合并数）—— 正常永远碰不到。</para>
    /// </summary>
    private const int MaxPending = 64;

    private readonly AgentHostRegistry _hosts;
    private readonly IHubContext<AgentHub> _agentHub;
    private readonly LogService _log;

    private readonly object _gate = new();

    /// <summary>会话 Id → 待发缓冲（会话结束时由账本通知清掉）。</summary>
    private readonly Dictionary<string, Outbox> _outboxes = new(StringComparer.Ordinal);

    public RemoteInputRelay(AgentHostRegistry hosts, IHubContext<AgentHub> agentHub, LogService log)
    {
        _hosts = hosts;
        _agentHub = agentHub;
        _log = log;
    }

    /// <summary>
    /// 收下一批输入并<b>尽快</b>下发给该会话所属的机器。
    /// </summary>
    /// <param name="agentId">会话所属的机器（由账本给出，<b>不由调用方报</b>）。</param>
    /// <returns>给页面的回执（见 <see cref="RemoteInputAckDto"/>）。</returns>
    /// <remarks>
    /// 🔥 <b>它是本方案里唯一一条"要等被控端回话"的高频路径</b>。
    /// 之所以值得等：输入的失败在画面上<b>完全看不见</b>
    /// （"点了没反应"既是"键没送到"，也可能是"那个键在那台机器上本来就没用"）。
    /// 没有回执，页面只能说"已发送" —— 而那句话在现场的价值是零。
    /// </remarks>
    public async Task<RemoteInputAckDto> SendAsync(
        string sessionId,
        int agentId,
        IReadOnlyList<RemoteInputEventDto> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0) return new RemoteInputAckDto(true, null, "没有事件。", 0);

        if (string.IsNullOrEmpty(_hosts.ConnectionIdOf(agentId)))
        {
            return new RemoteInputAckDto(
                false,
                RemoteErrorCodes.AgentOffline,
                "这台机器此刻没有长连接，输入送不过去。",
                0);
        }

        // ── ① 合并进缓冲 ──
        int coalesced;
        bool flushNow;

        lock (_gate)
        {
            if (!_outboxes.TryGetValue(sessionId, out var outbox))
            {
                outbox = new Outbox();
                _outboxes[sessionId] = outbox;
            }

            coalesced = outbox.Append(events);

            // 该不该现在发：有离散事件（必须马上），或离上次下发已经够久。
            flushNow = outbox.HasDiscrete || outbox.DueForSend(FlushInterval);
        }

        if (!flushNow)
        {
            // 🔥 还没到该发的点 —— 但**必须安排一次兜底下发**。
            //
            //    否则会出现这样一个很像是 bug 的现象：鼠标划到某处**停下**，
            //    之后没有任何新事件来触发下发，于是最后那条移动永远躺在缓冲里，
            //    远端指针停在**倒数第二个**位置上。用户看到的正是"指针慢半拍"。
            ScheduleFlush(sessionId, agentId);

            // ⚠️ Accepted 报 0 而不是 events.Count：这个字段的语义是"实际下发了多少"，
            //    而我们此刻一条都还没发。报 events.Count 会让页面上的计数说谎。
            return new RemoteInputAckDto(true, null, "已收下，将与紧邻的事件合并后下发。", 0, coalesced);
        }

        return await FlushAsync(sessionId, agentId, cancellationToken).ConfigureAwait(false)
               ?? new RemoteInputAckDto(true, null, null, 0, coalesced);
    }

    /// <summary>
    /// 把缓冲里的事件真正下发出去。<b>缓冲为空时不发任何东西</b>（返回 <c>null</c>）。
    /// </summary>
    private async Task<RemoteInputAckDto?> FlushAsync(
        string sessionId,
        int agentId,
        CancellationToken cancellationToken)
    {
        List<RemoteInputEventDto> pending;

        lock (_gate)
        {
            if (!_outboxes.TryGetValue(sessionId, out var outbox)) return null;

            pending = outbox.TakeAll();
        }

        if (pending.Count == 0) return null;

        var connectionId = _hosts.ConnectionIdOf(agentId);

        if (string.IsNullOrEmpty(connectionId))
        {
            return new RemoteInputAckDto(
                false,
                RemoteErrorCodes.AgentOffline,
                "这台机器此刻没有长连接，输入送不过去。",
                pending.Count);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InvokeTimeout);

        try
        {
            var ack = await _agentHub.Clients.Client(connectionId)
                .InvokeAsync<AgentIngestAckDto>(
                    AgentHubMethods.RemoteInput,
                    new RemoteInputBatchDto(sessionId, pending),
                    timeout.Token)
                .ConfigureAwait(false);

            if (ack is { Ok: false })
            {
                return new RemoteInputAckDto(
                    false,
                    ack.Status,
                    ack.Message ?? "被控端拒绝了这批输入。",
                    pending.Count);
            }

            return new RemoteInputAckDto(true, null, null, pending.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 观众把页面关了：不是故障。
            return new RemoteInputAckDto(false, RemoteErrorCodes.StartFailed, "请求已取消。", 0);
        }
        catch (OperationCanceledException)
        {
            return new RemoteInputAckDto(
                false,
                RemoteErrorCodes.InputUnavailable,
                $"被控端在 {(int)InvokeTimeout.TotalSeconds} 秒内没有回应输入。",
                pending.Count);
        }
        catch (Exception ex)
        {
            // 🔥 最常见的一种就是它：**被控端的客户端版本过旧，不认识这条消息**。
            //    SignalR 会回一个"方法不存在"的异常 —— 而这正是我们能拿到
            //    "这台机器的客户端不支持输入"的唯一地方
            //    （旧协议里连这个字段都不存在，所以它也没法自己报 unavailable）。
            _log.Warn("Remote", $"[Remote] 会话 {sessionId} 的输入没能送到机器 #{agentId}：{ex.Message}");

            return new RemoteInputAckDto(
                false,
                RemoteErrorCodes.InputUnavailable,
                $"输入没能送到被控端：{ex.Message}"
                + "（如果这台机器还在跑旧版客户端，它不支持输入回传。）",
                pending.Count);
        }
    }

    /// <summary>
    /// 安排一次"稍后下发"（用后即忘）。
    /// <para>
    /// ⚠️ 每个会话同时最多只有一次 —— 否则高频移动会给每一条事件都排一个任务，
    /// 那比不合并还糟。
    /// </para>
    /// </summary>
    private void ScheduleFlush(string sessionId, int agentId)
    {
        lock (_gate)
        {
            if (!_outboxes.TryGetValue(sessionId, out var outbox)) return;

            if (outbox.FlushScheduled) return;

            outbox.FlushScheduled = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FlushInterval, CancellationToken.None).ConfigureAwait(false);

                // 结果没人等，但失败要留痕：静默失败在这里的表现正是"指针慢半拍"。
                var ack = await FlushAsync(sessionId, agentId, CancellationToken.None).ConfigureAwait(false);

                if (ack is { Ok: false })
                {
                    _log.Warn("Remote", $"[Remote] 会话 {sessionId} 的延迟下发失败：{ack.Message}");
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Remote", $"[Remote] 会话 {sessionId} 的延迟下发出错：{ex.Message}");
            }
            finally
            {
                lock (_gate)
                {
                    if (_outboxes.TryGetValue(sessionId, out var outbox)) outbox.FlushScheduled = false;
                }
            }
        });
    }

    /// <summary>
    /// 忘掉一个会话的待发缓冲（会话结束时由账本调用）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不清的话，一个已经结束的会话会永远留着最多 64 条事件的缓冲 ——
    /// 量不大，但它是"每个会话都漏一点"的那种漏。
    /// </remarks>
    public void Forget(string sessionId)
    {
        lock (_gate) _outboxes.Remove(sessionId);
    }

    /// <summary>一个会话的待发缓冲。</summary>
    /// <remarks>
    /// 🔥 <b>用 <c>List</c> 而不是队列，就是为了"只留最新的一条移动"这件事</b>：
    /// 队列只能从两头进出，而合并要动的是<b>尾部那条</b>
    /// （最新的那条移动还没发出去，后面又来了一条，那么前一条已经没有意义了）。
    /// </remarks>
    private sealed class Outbox
    {
        private readonly List<RemoteInputEventDto> _pending = new();

        private DateTimeOffset _lastSentAt = DateTimeOffset.MinValue;

        /// <summary>已经排了一次"稍后下发"（防止给每条移动都排一个任务）。</summary>
        public bool FlushScheduled { get; set; }

        /// <summary>缓冲里有没有"离散事件"（非移动）—— 有就必须立刻发。</summary>
        public bool HasDiscrete
        {
            get
            {
                foreach (var input in _pending)
                {
                    if (input.Kind != RemoteInputKinds.PointerMove) return true;
                }

                return false;
            }
        }

        /// <summary>离上次下发是否已经够久。</summary>
        public bool DueForSend(TimeSpan interval)
            => DateTimeOffset.UtcNow - _lastSentAt >= interval;

        /// <summary>
        /// 追加一批事件，返回<b>被合并掉</b>的条数。
        /// </summary>
        public int Append(IReadOnlyList<RemoteInputEventDto> events)
        {
            var coalesced = 0;

            foreach (var input in events)
            {
                // 🔥 相邻的两条移动 → 只留后一条。
                //    判据是"上一条也是移动"，不是"缓冲里有移动"：
                //    "移动 → 点击 → 移动"里的那条点击必须留在原位，
                //    否则用户会看到"点下去的位置"跳到别处。
                if (input.Kind == RemoteInputKinds.PointerMove
                    && _pending.Count > 0
                    && _pending[^1].Kind == RemoteInputKinds.PointerMove)
                {
                    _pending[^1] = input;
                    coalesced++;

                    continue;
                }

                _pending.Add(input);
            }

            // 兜底上限：正常永远碰不到（16 毫秒的窗口里塞不满 64 条），
            // 但"碰不到"不能当成"不可能" —— 一个坏掉的前端可以连续猛发。
            while (_pending.Count > MaxPending)
            {
                _pending.RemoveAt(0);
                coalesced++;
            }

            return coalesced;
        }

        /// <summary>取走全部待发事件并推进"上次下发时刻"。</summary>
        public List<RemoteInputEventDto> TakeAll()
        {
            if (_pending.Count == 0) return new List<RemoteInputEventDto>();

            var batch = new List<RemoteInputEventDto>(_pending);

            _pending.Clear();
            _lastSentAt = DateTimeOffset.UtcNow;

            return batch;
        }
    }
}
