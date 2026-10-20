using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using ControlServer.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ControlServer.Server.Services;

/// <summary>建会话的结果（成功给 <see cref="Session"/>，失败给 <see cref="ErrorCode"/> + <see cref="Message"/>）。</summary>
public sealed record RemoteSessionOpened(
    RemoteSessionDto? Session,
    string? ErrorCode,
    string? Message)
{
    public static RemoteSessionOpened Ok(RemoteSessionDto session) => new(session, null, null);

    public static RemoteSessionOpened Fail(string code, string message) => new(null, code, message);
}

/// <summary>
/// <b>远控的并发准入上限</b>（M5.5）。
///
/// <para>
/// 🔥 <b>为什么是一个显式参数，而不是让注册表去读 <c>IConfiguration</c></b>：
/// 账本必须是"给我上限、我照判"的纯状态容器。让它自己读配置，
/// 用例就没法在不改全局配置的情况下验"第 5 路会被挡住"，
/// 而这一条正是配额存在的全部意义。
/// </para>
/// </summary>
/// <param name="MaxSessions">最多几路（含新建的这一路）。</param>
/// <param name="MaxTotalBitsPerSecond">全部会话合计的入向码率上限；<c>0</c> = 不判。</param>
public readonly record struct RemoteQuota(int MaxSessions, double MaxTotalBitsPerSecond);

/// <summary>
/// <b>一条被巡检判死的会话</b>（<see cref="RemoteSessionRegistry.Sweep"/> 的产物）。
///
/// <para>
/// 🔥 <b>为什么要带 <see cref="AgentId"/></b>（2026-10-19）：巡检原先只返回会话 Id，
/// 于是"中心这边把它摘掉了"<b>就到头了</b> —— 被控端收不到任何通知，
/// 会继续以为自己有一路在推，<b>从此拒绝这台机器上所有新会话</b>。
/// 现场看到的是"黑屏 → 断线 → 连不上"，而中心日志里只有一句"已结束"。
/// 带上机器 Id，收尸人才能把这条 stop 真的发到那台机器上去。
/// </para>
/// </summary>
/// <param name="SessionId">被判死的会话。</param>
/// <param name="AgentId">它跑在哪台机器上（发 stop 用）。</param>
/// <param name="Reason">判死的理由（会原样进日志与页面）。</param>
public readonly record struct SweptSession(string SessionId, int AgentId, string Reason);

/// <summary>
/// <b>远控会话注册表</b>（M5.1）：中心侧的"谁在推、推到哪儿了、谁在看"。
///
/// <para>
/// 🔥 <b>刻意是内存态、刻意不落库</b>（与 <c>AgentEncoderCapabilityService</c> 同一条理由）：
/// 会话的生命周期<b>不可能长过长连接</b> —— 中心一重启，所有长连接全断，
/// 被控端那边的推流也会因为接流任务消失而当场停下。
/// 把这种"只活在连接期间"的东西落库，只会留下一堆"重启前就已经死了、
/// 但数据库里还写着 streaming"的僵尸行，而它们比"没有记录"更难排查。
/// </para>
///
/// <para><b>它同时是码流的转发点</b>，而这里有本条链路上最要紧的一个设计：</para>
/// <list type="bullet">
///   <item>每个会话有一条<b>容量 1、丢旧留新</b>的转发队列，由<b>一个</b>后台任务消费。</item>
/// </list>
///
/// <para>
/// 🔥 <b>为什么不能直接把每一帧 <c>await</c> 广播出去</b>：被控端推帧是一条
/// SignalR 调用，广播是另一条。如果在这里同步等广播完成，那么
/// <b>一个慢的观看端会直接把被控端的推流速度拖下来</b> —— 表现为
/// "人越多画面越卡"，而且卡的是被控机。
/// 容量 1 + 丢旧留新意味着：观看端跟不上时丢的是<b>画面</b>（人眼看不出少一帧），
/// 而不是把压力传回被控端。
/// </para>
/// </summary>
public sealed class RemoteSessionRegistry : IAsyncDisposable
{
    /// <summary>帧率统计窗口：把"最近这段时间收到了多少帧"换算成 fps。</summary>
    /// <remarks>
    /// ⚠️ 不能拿"总帧数 ÷ 总时长"：那会让帧率在流跑久之后<b>几乎不动</b>
    /// ——被控端从 12 fps 掉到 2 fps，页面上还是显示 11.9，等于没有这个指标。
    /// </remarks>
    private static readonly TimeSpan FpsWindow = TimeSpan.FromSeconds(3);

    /// <summary>会话之后不再收到帧、也没有状态上报时，多久判成"断了"。</summary>
    /// <remarks>
    /// 🔥 <b>这条判据的实现只有一处：<c>Entry.LastAgentAt</c></b>（由 <c>Observe</c> 与
    /// <c>ApplyState</c> 各刷新一处，音频刻意不算）。⚠️ 它<b>不是</b> <c>LastFrameAt</c> ——
    /// 那一个只回答"最近一帧什么时候到的"，而<b>桌面静止时它可以永远不动</b>。
    /// 两者混用的后果已经踩过一次（见 <c>LastAgentAt</c> 的说明）。
    /// </remarks>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 建会话之后，多久还没等到被控端的<b>任何</b>状态上报就判成"没起来"。
    /// <para>
    /// 🔥 <b>没有它就会出现"一次失败变成永久故障"</b>：会话停在 <c>requested</c>
    /// （被控端没人登录 / 托盘没跑 / 客户端版本太旧不认这条指令 / 那条指令根本没送到），
    /// 而下面的空闲判定<b>刻意跳过了 requested</b>（那时"没有帧"本来就很正常）——
    /// 于是这条会话永远留在表里，<c>_byAgent</c> 也永远占着，
    /// 之后每一次建会话都被 <see cref="RemoteErrorCodes.SessionBusy"/> 挡掉。
    /// 现场看到的是"这台机器再也连不上了"，而真正的原因早就过去了。
    /// </para>
    /// <para>
    /// 20 秒这个值只覆盖<b>「那条指令到没到」</b>，<b>不包括被控端建会话的耗时</b> ——
    /// 这件事以前是一处真实的错位：被控端原先要等托盘把抓屏器 / D3D 设备 / 硬编 MFT
    /// 全部建完，才发第一声 <c>requested</c>，而那一刻可能已经过了 20 秒。
    /// 现场的症状是「中心报建会话超时，被控端日志却一切正常」—— 两边都没错，
    /// 只是中心在等一声本该在毫秒级到达的回话。
    /// </para>
    /// <para>
    /// 🔥 现在被控端在<b>开始建会话之前</b>就会先回一次 <c>requested</c>
    /// （见 <c>RemoteSessionCoordinator.StartAsync</c> 的 ①′），
    /// 所以 20 秒真的只用来区分「指令没到」与「正在建」。
    /// 「建不出来」由托盘自己的失败回执与它那边 30 秒的首帧预算负责 ——
    /// <b>两个数是两个判据，不是同一个阈值的两处手抄</b>。
    /// </para>
    /// <para>
    /// 🔥 <b>计时的起点是"最后一次回话"（<c>LastAgentAt</c>），不是"建会话那一刻"</b> ——
    /// 用 <c>RequestedAt</c> 会把"接到了、正在建编码器/切分辨率"误判成"没接到"。
    /// 两者在页面上是两句完全不同的话（"去升级客户端"vs"再等一会儿"）。
    /// </para>
    /// </summary>
    private static readonly TimeSpan RequestedTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 音频转发队列的容量（<b>包数</b>）。
    ///
    /// <para>
    /// 🔥 <b>它刻意不是视频那个 1</b>，而且这个差别是本节最要紧的一处：
    /// 视频队列容量 1 的意思是"只保留最新一帧，其余全丢" —— 丢一帧画面只是抖一下；
    /// 音频<b>丢一包就是一个可听见的断点</b>（"哒"或一个字的缺口），
    /// 所以容量 1 会把音频变成一台故障机器。
    /// </para>
    ///
    /// <para>
    /// 50 包 = 20 ms × 50 = <b>1 秒</b>。它同时是"允许多大落后"的上限：
    /// 队列没满时（正常情况，转发任务立刻排空）实际排队深度是 0–1，
    /// <b>不引入任何额外延迟</b>；只有当观看端真的追不上时才会积累到这么多，
    /// 而那时<b>从最旧的开始丢</b>（<c>DropOldest</c>）让它跳回现在 ——
    /// 落后 1 秒的音频相比"掉一包"，对 AV 同步的破坏要大得多。
    /// </para>
    /// </summary>
    private const int AudioQueueCapacity = 50;

    private readonly IHubContext<RemoteHub> _hub;
    private readonly RemoteInputRelay _input;
    private readonly LogService _log;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    /// <summary>每台机器最多一路（M5.1）。用机器 Id 而不是"遍历找"，让判定是 O(1) 且无歧义。</summary>
    private readonly Dictionary<int, string> _byAgent = new();

    private readonly CancellationTokenSource _lifetime = new();

    /// <param name="input">
    /// 输入中继（M5.2）。账本拿它<b>只做一件事</b>：会话结束时叫它忘掉那个会话的待发缓冲。
    /// 真正的"下发输入"不在本类（见 <see cref="RemoteInputRelay"/> 的类注释）。
    /// </param>
    public RemoteSessionRegistry(IHubContext<RemoteHub> hub, RemoteInputRelay input, LogService log)
    {
        _hub = hub;
        _input = input;
        _log = log;
    }

    // ═══════════════════════════════════════════════════════════
    // 查询
    // ═══════════════════════════════════════════════════════════

    /// <summary>当前所有会话（含已结束的，按建立时间倒序）。</summary>
    public IReadOnlyList<RemoteSessionDto> List()
    {
        lock (_gate) return _sessions.Values.Select(e => e.ToDto()).OrderByDescending(s => s.RequestedAt).ToArray();
    }

    /// <summary>取一个会话；没有则 <c>null</c>。</summary>
    public RemoteSessionDto? TryGet(string sessionId)
    {
        lock (_gate) return _sessions.TryGetValue(sessionId, out var entry) ? entry.ToDto() : null;
    }

    /// <summary>某台机器上此刻有几路（M5.1 最多 1）。</summary>
    public int ActiveCountOn(int agentId)
    {
        lock (_gate)
        {
            if (!_byAgent.TryGetValue(agentId, out var sessionId)) return 0;

            return _sessions.TryGetValue(sessionId, out var entry) && entry.IsLive ? 1 : 0;
        }
    }

    /// <summary>新观众进来时先给他一帧（否则他要等到下一帧才知道画面长什么样）。</summary>
    public RemoteStreamFrame? Latest(string sessionId)
    {
        lock (_gate) return _sessions.TryGetValue(sessionId, out var entry) ? entry.Latest : null;
    }

    /// <summary>
    /// 切一路会话的黑白模式（决策 14）。返回变更后的会话；会话不存在时 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它<b>只改中心侧的记录并广播</b>，不碰网络、不下指令。
    /// 真正让画面变灰是 <c>RemoteSessionService</c> 另发一条指令给被控端的事。
    /// 在这里顺手发指令是错的：本类是"谁在推、推到哪儿了"的账本，
    /// 一旦它开始下发指令，就变成了什么都干的上帝对象，而下一个改它的人
    /// 会以为自己只是改了一个字典。
    /// </remarks>
    public RemoteSessionDto? SetGrayscale(string sessionId, bool enabled)
    {
        RemoteSessionDto dto;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var entry)) return null;

            entry.SetGrayscale(enabled);
            dto = entry.ToDto();
        }

        // 必须广播：黑白是**会话级**的（编码只做一路），
        // 所以一个人切了，所有看这路流的人都该跟着变 —— 页面不提示就是 bug 观感。
        _ = BroadcastAsync(RemoteHubMethods.SessionChanged, dto, CancellationToken.None);

        return dto;
    }

    /// <summary>
    /// 记下这一路的新质量策略并广播（M5.5）。会话不存在时 <c>null</c>。
    /// <para>
    /// ⚠️ 与 <see cref="SetGrayscale"/> 同一条纪律：<b>只记账、不下发</b>。
    /// </para>
    /// </summary>
    public RemoteSessionDto? ApplyQuality(
        string sessionId,
        string profile,
        int targetFps,
        int targetBitrateKbps,
        bool downgraded,
        string? note)
    {
        RemoteSessionDto dto;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var entry)) return null;

            entry.ApplyQuality(profile, targetFps, targetBitrateKbps, downgraded, note);
            dto = entry.ToDto();
        }

        _ = BroadcastAsync(RemoteHubMethods.SessionChanged, dto, CancellationToken.None);

        return dto;
    }

    /// <summary>
    /// 记下"只编这一块"并广播（M5.5 ROI）。会话不存在时 <c>null</c>。
    /// <para>
    /// ⚠️ 同 <see cref="ApplyQuality"/>：<b>只记账、不下发</b>。
    /// 视口是<b>发起方</b>（页面）的意图，中心只负责转达与显示；
    /// 中心自己拿这个值去裁画面，就会出现"中心裁一次、被控端再裁一次"的双重裁剪。
    /// </para>
    /// </summary>
    public RemoteSessionDto? ApplyViewport(string sessionId, int x, int y, int width, int height)
    {
        RemoteSessionDto dto;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var entry)) return null;

            entry.ApplyViewport(x, y, width, height);
            dto = entry.ToDto();
        }

        _ = BroadcastAsync(RemoteHubMethods.SessionChanged, dto, CancellationToken.None);

        return dto;
    }

    /// <summary>
    /// <b>全部在用会话的实测入向码率合计</b>（bit/s；M5.5 的带宽配额判据）。
    /// <para>
    /// ⚠️ 它是 <b>实测</b>，不是"配置了多少码率"：配置只是上界，
    /// 实际编出来多少完全取决于画面内容（满屏噪点能冲到目标的 1.5 倍）。
    /// </para>
    /// </summary>
    public double TotalBitsPerSecond
    {
        get
        {
            lock (_gate)
            {
                var total = 0d;

                foreach (var entry in _sessions.Values)
                {
                    if (entry.IsLive) total += entry.BitsPerSecond;
                }

                return total;
            }
        }
    }

    /// <summary>当前在用的会话路数（<c>requested</c> 与 <c>streaming</c> 都算）。</summary>
    public int LiveCount
    {
        get
        {
            lock (_gate)
            {
                var count = 0;

                foreach (var entry in _sessions.Values)
                {
                    if (entry.IsLive) count++;
                }

                return count;
            }
        }
    }

    /// <summary>
    /// 按被控机找它<b>当前那一路</b>会话（一台机器同时只有一路）。没有则 <c>null</c>。
    /// <para>
    /// 🔥 剪贴板上行要用它：中心 → 客户机的指令是 fire-and-forget，
    /// "读到的那段文本"要回来时，托盘并不知道（也不该知道）中心的 sessionId 规则，
    /// 所以只能按连接身份反查。
    /// </para>
    /// </summary>
    public RemoteSessionDto? TryGetByAgent(int agentId)
    {
        lock (_gate)
        {
            return _byAgent.TryGetValue(agentId, out var sessionId)
                   && _sessions.TryGetValue(sessionId, out var entry)
                   && entry.IsLive
                ? entry.ToDto()
                : null;
        }
    }

    /// <summary>
    /// 记下一段刚从被控机读到的剪贴板内容，并<b>只发给这一路会话的观看者</b>。
    /// </summary>
    /// <returns>这台机器当前没有在用的会话时 <c>false</c>（内容无处可放，调用方记一行日志即可）。</returns>
    public bool RecordClipboard(int agentId, RemoteClipboardDto clipboard)
    {
        string sessionId;

        lock (_gate)
        {
            if (!_byAgent.TryGetValue(agentId, out var id)
                || !_sessions.TryGetValue(id, out var entry)
                || !entry.IsLive)
            {
                return false;
            }

            entry.SetClipboard(clipboard);
            sessionId = id;
        }

        // ⚠️ 只发分组：剪贴板内容可能很大（60 KB），全站广播等于每次读都发一遍给所有控制台。
        _ = _hub.Clients.Group(sessionId)
            .SendAsync(RemoteHubMethods.ClipboardChanged, clipboard, CancellationToken.None);

        return true;
    }

    /// <summary>取这一路会话最近一次读到的剪贴板内容（<c>null</c> = 还没读过）。</summary>
    public RemoteClipboardDto? TryGetClipboard(string sessionId)
    {
        lock (_gate) return _sessions.TryGetValue(sessionId, out var entry) ? entry.Clipboard : null;
    }

    /// <summary>
    /// 打开 / 关闭这一路会话的<b>输入回传闸门</b>（M5.2）。
    /// 返回变更后的会话；会话不存在时 <c>null</c>。
    ///
    /// <para>
    /// 🔥 <b>它与黑白模式（<see cref="SetGrayscale"/>）看着像，其实是两件不同的事</b>：
    /// 灰度要下手到被控端（改编码参数），所以"改了"只等于"我们发过指令"；
    /// 而闸门<b>完全在中心这一侧</b> —— 关掉它，中心根本不会把输入转发出去，
    /// 所以"关了"是一个<b>可靠的</b>结论，可靠到不需要被控端配合。
    /// </para>
    /// <para>
    /// ⚠️ 默认<b>关</b>（见 <see cref="RemoteSessionDto.InputEnabled"/> 的注释）：
    /// "能看"与"能控"必须是两次明确的动作，而不是一次授权顺带送出的能力。
    /// </para>
    /// </summary>
    public RemoteSessionDto? SetInputEnabled(string sessionId, bool enabled)
    {
        RemoteSessionDto dto;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var entry)) return null;

            entry.SetInputEnabled(enabled);
            dto = entry.ToDto();
        }

        // 与黑白同理，必须广播：这是**会话级**的开关，一个人打开了，
        // 所有正在看这路流的人（以及之后进来的人）都该看见"这台机器现在可以被操作"。
        // 不广播的话，会出现"两个人同时抢鼠标而互不知情"。
        _ = BroadcastAsync(RemoteHubMethods.SessionChanged, dto, CancellationToken.None);

        _log.Info("Remote", $"[Remote] 会话 {sessionId} 的输入回传＝{(enabled ? "已允许" : "已禁止")}。");

        return dto;
    }

    // ═══════════════════════════════════════════════════════════
    // 开 / 关
    // ═══════════════════════════════════════════════════════════

    /// <summary>登记一路新会话（<b>只是登记</b>，真正让被控端开始推是调用方的事）。</summary>
    public RemoteSessionOpened Open(
        int agentId,
        string? screenId,
        string? displayMode,
        int? maxFps,
        DateTimeOffset now,
        string? quality = null,
        RemoteQuota? quota = null)
    {
        var profile = RemoteQualityProfiles.Normalize(quality);
        var (profileFps, profileKbps) = RemoteQualityProfiles.Resolve(profile);

        lock (_gate)
        {
            if (_byAgent.TryGetValue(agentId, out var existing)
                && _sessions.TryGetValue(existing, out var live)
                && live.IsLive)
            {
                return RemoteSessionOpened.Fail(
                    RemoteErrorCodes.SessionBusy,
                    $"这台机器上已有会话「{existing}」在推流（M5.1 只允许一路）。先结束它再开新的。");
            }

            // ── M5.5 并发准入（决策 8）──
            //
            // 🔥 判定放在**同一把锁里、并且在登记之前**：先数一遍再登记的话，
            //    两个几乎同时到达的请求会各自数到 3 路、各自认为"还有一格"，
            //    于是一起通过 —— 上限 4 变成上限 5，而这件事**没有任何日志**。
            if (quota is { } limits)
            {
                var liveCount = 0;
                var totalBps = 0d;

                foreach (var running in _sessions.Values)
                {
                    if (!running.IsLive) continue;

                    liveCount++;
                    totalBps += running.BitsPerSecond;
                }

                if (liveCount >= limits.MaxSessions)
                {
                    return RemoteSessionOpened.Fail(
                        RemoteErrorCodes.QuotaExceeded,
                        $"中心的远控路数已经到上限（{liveCount}/{limits.MaxSessions} 路）。"
                        + "请先结束别的机器上的一路，或者调大 Server:Remote:MaxConcurrentSessions。");
                }

                if (limits.MaxTotalBitsPerSecond > 0 && totalBps >= limits.MaxTotalBitsPerSecond)
                {
                    return RemoteSessionOpened.Fail(
                        RemoteErrorCodes.QuotaExceeded,
                        $"远控总码率已经到上限（当前约 {totalBps / 1_000_000:0.0} Mbps，"
                        + $"上限 {limits.MaxTotalBitsPerSecond / 1_000_000:0} Mbps）。"
                        + "请先结束一路，或者把别的会话降到低画质档；"
                        + "也可以调大 Server:Remote:MaxTotalMbps。");
                }
            }

            // ⚠️ 会话 Id 用"时间前缀 + 短随机"，而不是纯 GUID：
            //    排查时一眼能看出这批会话是什么时候开的，而纯 GUID 只能靠查表。
            var sessionId = $"{now:HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";

            var entry = new Entry(sessionId, agentId, screenId, displayMode, maxFps, now);

            // 🔥 帧率与码率**分开折**（M7.16）：
            //    帧率以「用户要的」为准（档位只在没指定时提供默认值），
            //    码率只跟档位走。旧写法 `Min(请求, 档位)` 会让帧率旋钮在档位之上整块失效
            //    （默认档是 auto ⇒ 12 fps，于是 20/30/60 选了都还是 12）。
            entry.ApplyQuality(
                profile,
                targetFps: RemoteQualityProfiles.FoldTargetFps(maxFps, profileFps),
                targetBitrateKbps: profileKbps,
                downgraded: false,
                note: null);

            _sessions[sessionId] = entry;
            _byAgent[agentId] = sessionId;

            // 🔥 转发任务**在登记那一刻就挂上**，而不是"等第一帧到了再挂"。
            //    晚挂的那段时间里写进队列的帧会一直躺在容量 1 的队列里没人读，
            //    表现是"第一帧永远出不去、直到第二帧把它顶掉" —— 一帧的差别，
            //    在"刚点开、第一帧还没到"的场景里正好就是最显眼的那一帧。
            //
            // ⚠️ 音频那条转发任务**同样必须在这里挂**，而且它比视频更经不起晚挂：
            //    视频晚挂丢的是"最显眼的一帧"，音频晚挂丢的是"开头那几个字"，
            //    而音频队列的 DropOldest 会把它们从最旧的开始挤出去 ——
            //    现场听到的是"刚开始说话的半个字没了"，没人会往"队列挂晚了"上想。
            entry.StartFanouts(SendToGroupAsync, SendAudioToGroupAsync);

            _log.Info(
                "Remote",
                $"[Remote] 登记会话 {sessionId}（机器 #{agentId}，屏={screenId ?? "自动"}，"
                + $"目标 {maxFps?.ToString() ?? "默认"} fps）。");

            return RemoteSessionOpened.Ok(entry.ToDto());
        }
    }

    /// <summary>
    /// 结束一路会话。
    /// </summary>
    /// <returns>结束前的最后状态；会话不存在时 <c>null</c>。</returns>
    public RemoteSessionDto? Close(string sessionId, string reason, bool failed = false)
    {
        Entry? entry;
        RemoteSessionDto dto;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out entry)) return null;

            entry.MarkEnded(reason, failed);

            if (_byAgent.TryGetValue(entry.AgentId, out var current) && current == sessionId)
            {
                _byAgent.Remove(entry.AgentId);
            }

            dto = entry.ToDto();

            // 完成后从表里删掉：M5.1 的会话列表只关心"现在有什么"。
            // 保留历史会引出"保留多久 / 谁清理"这一整类问题，而它对现场毫无价值
            // （被控端那边的日志才是追溯"这路流什么时候跑的"的地方）。
            entry.Dispose();
            _sessions.Remove(sessionId);
        }

        // ⚠️ 必须在锁外叫中继：它自己有锁，两把锁叠着迟早会有人反过来拿一次
        //    （那就成死锁了，而且只在特定时序下出现）。
        //
        // 为什么值得清：一个已经结束的会话会永远留着最多 64 条事件的缓冲 ——
        // 量不大，但它是"每个会话都漏一点"的那种漏。
        _input.Forget(sessionId);

        _log.Info(
            "Remote",
            $"[Remote] 会话 {sessionId} 已结束（{reason}）｜中心收帧 {dto.Stats?.FramesReceived ?? 0}"
            + $"｜中心收音频 {dto.Stats?.AudioFramesReceived ?? 0}。");

        _ = BroadcastAsync(RemoteHubMethods.SessionChanged, dto, CancellationToken.None);

        return dto;
    }

    /// <summary>
    /// 把所有还没结束的会话一次性收掉（供<b>进程停机</b>时调用）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它<b>不下发任何指令</b>：停机时被控端的长连接马上就要断，
    /// 而 <c>RemoteSessionCoordinator.OnDisconnected</c> 会自己把流停掉 ——
    /// 在停机路径上再等一轮网络往返，只会让"关服务"变成一个会卡住的动作。
    /// </remarks>
    public void CloseAll(string reason)
    {
        string[] ids;

        lock (_gate) ids = _sessions.Keys.ToArray();

        // 刻意逐个走 Close 而不是"清空两个字典"：收场要经过 MarkEnded、
        // 要发 SessionChanged、要 Dispose 转发队列 —— 停机路径上也不该走一条
        // 与平时不同的捷径（那条捷径就是"只在关机时才跑、于是从没被验证过"的代码）。
        foreach (var id in ids) Close(id, reason);
    }

    // ═══════════════════════════════════════════════════════════
    // 来自被控端
    // ═══════════════════════════════════════════════════════════
    /// <summary>
    /// 收一帧。<paramref name="agentId"/> 是<b>发帧那台机器的身份</b>，用于归属校验。
    /// </summary>
    /// <remarks>
    /// 🔥 <b>归属校验必须做在这一层</b>：`sessionId` 是客户机从中心的指令里拿到的，
    /// 它可以被改成任何一个别的会话 Id。不校验的话，一台机器就能
    /// <b>往别人的会话里灌画面</b>（而页面上写着的是另一台机器的名字）。
    /// 这与"分发任务必须校验归属"是同一条判据。
    /// </remarks>
    public AgentIngestAckDto AcceptFrame(int agentId, RemoteStreamFrame? frame)
    {
        if (frame?.Header is null) return AgentIngestAckDto.Rejected("empty-frame", "空帧。");

        Entry? entry;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(frame.Header.SessionId, out entry))
            {
                // 会话已经被关掉了（观众点了结束 / 中心重启过）。
                // 报 false 会让被控端**当场停下这路流** —— 这正是我们要的：
                // 继续推只会白烧它的 CPU。
                return AgentIngestAckDto.Rejected(
                    RemoteErrorCodes.SessionNotFound,
                    $"会话「{frame.Header.SessionId}」不存在或已结束。");
            }

            if (entry.AgentId != agentId)
            {
                _log.Warn(
                    "Remote",
                    $"[Remote] 机器 #{agentId} 试图往会话 {frame.Header.SessionId}（属于机器 #{entry.AgentId}）推帧，已拒绝。");

                return AgentIngestAckDto.Rejected(
                    "not-owner",
                    "这个会话不属于你这台机器。");
            }

            entry.Observe(frame);
        }

        return AgentIngestAckDto.Success();
    }

    /// <summary>
    /// 收一段音频（M5.3）。<paramref name="agentId"/> 是发音频那台机器的身份，用于归属校验。
    /// </summary>
    /// <remarks>
    /// 🔥 <b>它与 <see cref="AcceptFrame"/> 的代码几乎一样，但仍然分开写</b>：
    /// 两者唯一的共同点是"都要校验归属"，其余<b>全部相反</b> ——
    /// 计数不同、队列容量不同（1 vs 50）、广播方法名不同（<c>Frame</c> vs <c>Audio</c>）、
    /// 而且<b>绝不能共用一条队列</b>（合并的话，被控端每推一帧音频就会把待发的视频帧顶掉，
    /// 症状是"一有声音画面就掉到 1 fps"，而两边日志都正常）。
    /// 把两件事塞进一个方法、靠参数分叉，只会让下一个人以为它们可以一起改。
    /// </remarks>
    public AgentIngestAckDto AcceptAudio(int agentId, RemoteAudioFrame? frame)
    {
        if (frame?.Header is null) return AgentIngestAckDto.Rejected("empty-frame", "空音频帧。");

        Entry? entry;
        var overflowed = false;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(frame.Header.SessionId, out entry))
            {
                // 与视频同一条纪律：报 false 会让被控端**当场停掉这条音频通道**。
                // 这正是我们要的 —— 会话都结束了，继续采只会白烧它的 CPU。
                return AgentIngestAckDto.Rejected(
                    RemoteErrorCodes.SessionNotFound,
                    $"会话「{frame.Header.SessionId}」不存在或已结束。");
            }

            if (entry.AgentId != agentId)
            {
                _log.Warn(
                    "Remote",
                    $"[Remote] 机器 #{agentId} 试图往会话 {frame.Header.SessionId}（属于机器 #{entry.AgentId}）推音频，已拒绝。");

                return AgentIngestAckDto.Rejected("not-owner", "这个会话不属于你这台机器。");
            }

            overflowed = entry.ObserveAudio(frame);
        }

        // ⚠️ 日志放在锁外：LogService 会写盘，拿着账本的锁去写盘
        //    就是在给"所有会话的所有帧"加一个 IO 等待。
        if (overflowed)
        {
            _log.Warn(
                "Remote",
                $"[Remote] 会话 {frame.Header.SessionId} 的音频转发队列已积压到 {AudioQueueCapacity} 包"
                + "（约 1 秒），开始从最旧的开始丢 —— 观看端跟不上这路音频了。");
        }

        return AgentIngestAckDto.Success();
    }

    /// <summary>
    /// 收<b>一片</b>远控画面（M5.4-c① 的分片）。拼齐之后按<b>整帧</b>走 <see cref="AcceptFrame"/>。
    /// </summary>
    /// <remarks>
    /// 🔥 <b>归属校验在<b>锁内</b>、拼字节在<b>锁外</b>，这个分工是本方法的全部形状</b>：
    /// </remarks>
    /// <list type="number">
    ///   <item><b>会话存在与归属校验</b>—— 与 <see cref="AcceptFrame"/> 完全一致，
    ///         而且<b>每一片都做</b>（不是只在第 0 片做）。分片没有把"归属"这件事变成一次性的；</item>
    ///   <item><b>拼字节</b>—— 在锁外，用那个会话<b>自己的</b>小锁（见 <see cref="RemoteFrameReassembler"/>）。
    ///         ⚠️ 把它放进 <c>_gate</c>，等于让<b>所有会话的所有帧</b>去等一次十几 MB 的拷贝
    ///         —— 拿账本的锁去做大内存操作，是这类设计里最常见的一种"平时看不出来"的卡顿；</item>
    ///   <item><b>拼齐之后</b>再调既有的 <see cref="AcceptFrame"/> ⇒
    ///         <c>Observe</c> / <c>Latest</c> / <c>Fanout</c> 的并发假设<b>一行都不用改</b>。</item>
    /// </list>
    /// <para>
    /// ⚠️ <b>拼不齐时返回的是成功（而不是拒绝）</b>：拒绝是<b>刹车信号</b>
    /// （被控端收到会当场停掉这路流），而"还没齐"完全正常。
    /// 一次误报的拒绝会让画面毫无理由地断掉。
    /// </para>
    /// </remarks>
    public AgentIngestAckDto AcceptFrameChunk(int agentId, RemoteStreamFrameChunk? chunk)
    {
        if (chunk?.Header is null) return AgentIngestAckDto.Rejected("empty-frame", "空帧片。");

        Entry entry;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(chunk.Header.SessionId, out var found))
            {
                // 与整帧那条同一个理由：报 false 会让被控端当场停下这路流。
                // 会话都结束了，继续编只会白烧它的 CPU。
                return AgentIngestAckDto.Rejected(
                    RemoteErrorCodes.SessionNotFound,
                    $"会话「{chunk.Header.SessionId}」不存在或已结束。");
            }

            if (found.AgentId != agentId)
            {
                _log.Warn(
                    "Remote",
                    $"[Remote] 机器 #{agentId} 试图往会话 {chunk.Header.SessionId}"
                    + $"（属于机器 #{found.AgentId}）推帧片，已拒绝。");

                return AgentIngestAckDto.Rejected("not-owner", "这个会话不属于你这台机器。");
            }

            // 🔥 一片也是"这个被控端还在"的证据（与 Observe 同级，只是被切开了）。
            //    ⚠️ 按本类的规矩，每一条新上行都必须在这里**明写一次表态** ——
            //    见 Entry.LastAgentAt 的说明与 Entry.TouchAgentActivity。
            found.TouchAgentActivity();

            entry = found;
        }

        // ⚠️ 拼字节在锁外（见方法注释第 2 条）。
        var frame = entry.Reassembler.Accept(chunk, out var dropReason);

        if (dropReason is not null)
        {
            // 首次丢帧提示一次就够：持续丢帧会持续走到这里，而每条都写日志会把日志刷满
            // （与音频队列积压那条同一条理由：真正有用的信息是"什么时候开始丢"）。
            if (entry.NoteReassemblyDrop())
            {
                _log.Warn(
                    "Remote",
                    $"[Remote] 会话 {chunk.Header.SessionId} 的分片重组丢了一帧：{dropReason}"
                    + "（已在下一条应答上请被控端补一个关键帧；后续同类丢帧不再重复提示）");
            }
        }

        if (frame is null)
        {
            // 还没拼齐（或这一片非法）：**是成功**，不是拒绝 —— 见方法注释最后那段。
            return dropReason is null
                ? AgentIngestAckDto.Success()
                : AgentIngestAckDto.SuccessNeedKeyframe();
        }

        var ack = AcceptFrame(agentId, frame);

        // 🔥 **真正收下了才算"拼成"**（不是"拼出来了就算"）：
        //    锁外这一小段里会话可能刚好被关掉，那时 `AcceptFrame` 会拒绝 ——
        //    把那种帧记进"重组完成"会让这个数比实际画面上的帧数多，
        //    而"看到重组在涨却看不到画面变化"是最难查的一种读数。
        if (ack.Ok) entry.NoteReassemblyCompleted();

        // 拒绝是刹车信号，它上面不再叠"请补关键帧"（被控端要做的是停，不是补）。
        return dropReason is null || !ack.Ok ? ack : ack with { NeedKeyframe = true };
    }

    /// <summary>收一条会话状态上报。</summary>
    /// <remarks>
    /// 🔥 <b>音频的三态也是搭这条上报上来的</b>（<c>AgentRemoteSessionStateDto.AudioState</c>），
    /// 原因与 M5.2 的输入完全相同："起过但没起来"的时候<b>一条音频都不会有</b>，
    /// 而"正在接"与"没有声卡"从数据流上是同一个现象。见 <see cref="Entry.ApplyState"/>。
    /// </remarks>
    public AgentIngestAckDto AcceptAgentState(int agentId, AgentRemoteSessionStateDto? state)
    {
        if (state is null) return AgentIngestAckDto.Rejected("empty-state", "空状态。");

        RemoteSessionDto? dto = null;
        var close = false;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(state.SessionId, out var entry))
            {
                return AgentIngestAckDto.Success();
            }

            if (entry.AgentId != agentId)
            {
                return AgentIngestAckDto.Rejected("not-owner", "这个会话不属于你这台机器。");
            }

            entry.ApplyState(state, out close);

            dto = entry.ToDto();
        }

        _ = BroadcastAsync(RemoteHubMethods.SessionChanged, dto, CancellationToken.None);

        if (close) Close(state.SessionId, state.Reason ?? "被控端已结束这路流");

        return AgentIngestAckDto.Success();
    }

    /// <summary>
    /// 清掉已经断掉的会话（帧停了、状态也不再上报）。
    /// <para>
    /// 🔥 没有它就会出现"页面上一路流永远显示正在推流，而其实它早就断了" ——
    /// 因为中心看到的最后一帧与"此刻"之间没有区别。
    /// </para>
    /// <para>
    /// 🔥🔥 <b>返回值带上机器 Id，是因为"摘掉账本"只做了一半的事</b>：
    /// 被控端那边还挂着一路会话，而<b>它不知道中心已经放弃了</b>。
    /// 调用方（<c>RemoteSessionSweeperHostedService</c>）必须照着返回值
    /// 给那些机器补一条 stop —— 否则那台机器会**在托盘重启之前永远远控不了**
    /// （2026-10-19 现场：09:18 判死，之后四次建会话全被"已有会话在推流"挡掉）。
    /// </para>
    /// </summary>
    public IReadOnlyList<SweptSession> Sweep(DateTimeOffset now)
    {
        List<SweptSession> stale = new();

        lock (_gate)
        {
            foreach (var (sessionId, entry) in _sessions)
            {
                if (!entry.IsLive) continue;

                // ── 还没起来的：用另一条判据 ──
                //
                // 🔥 这里**不能**跟着下面一起 continue（那正是原来的洞）：
                //    卡在 requested 的会话不会有任何帧，于是永远不会被清掉，
                //    而那台机器也永远建不了新会话。见 RequestedTimeout 的注释。
                if (entry.State == RemoteSessionStates.Requested)
                {
                    // 🔥 判据是"最后一次**回话**到现在多久"，不是"建会话到现在多久" ——
                    //    这正是上面那句注释与错误文案说的意思（"没有收到被控端的任何状态上报"）。
                    //    取 `RequestedAt` 会把"接到了、正在建编码器"误判成"没接到"。
                    var lastSignal = entry.LastAgentAt ?? entry.RequestedAt;

                    if (now - lastSignal > RequestedTimeout)
                    {
                        stale.Add(new SweptSession(
                            sessionId,
                            entry.AgentId,
                            $"建会话后 {(int)RequestedTimeout.TotalSeconds} 秒内没有收到被控端的任何状态上报"
                            + "（最常见的原因：这台机器没人登录 / 托盘没在跑 / 客户端版本过旧不认识这条指令）。"));
                    }

                    continue;
                }

                // ── 推过流、又停了的 ──
                //
                // 🔥 **判据是「帧 **或** 状态上报」，不是「帧」**（见 LastAgentAt 的说明）：
                //    被控端桌面静止时 DDA 一帧都不给，而这条路完全正常。
                //    只看 `LastFrameAt` 的后果是**展厅里定格在一张图上的机器每 15 秒被判死一次**
                //    —— 已经踩过，由 `A_static_screen_is_not_a_dead_session` 钉住。
                var last = entry.LastAgentAt ?? entry.StartedAt ?? entry.RequestedAt;

                if (now - last > IdleTimeout)
                {
                    stale.Add(new SweptSession(
                        sessionId,
                        entry.AgentId,
                        $"超过 {(int)IdleTimeout.TotalSeconds} 秒没有收到任何帧与状态，判定为已断开"));
                }
            }
        }

        foreach (var (sessionId, _, reason) in stale)
        {
            Close(sessionId, reason, failed: true);
        }

        // ⚠️ 返回的是**已经判死的那批**（照旧），不是"清完之后还剩哪些" ——
        //    调用方要照着它去给被控端补 stop（见方法注释）。
        return stale;
    }

    // ═══════════════════════════════════════════════════════════
    // 广播
    // ═══════════════════════════════════════════════════════════

    private Task BroadcastAsync(string method, object payload, CancellationToken cancellationToken)
        => _hub.Clients.Group(GroupOf(payload)).SendAsync(method, payload, cancellationToken);

    /// <summary>把一帧发给"进了这个会话分组"的连接。由每路会话的转发任务调用。</summary>
    /// <remarks>
    /// ⚠️ 它<b>只发分组</b>，不做全站广播：一帧 1080p MJPEG 给每个控制台连接都发一遍，
    /// 是拿带宽换事故（而绝大多数连接根本没在看这个会话）。
    /// </remarks>
    private Task SendToGroupAsync(string sessionId, RemoteStreamFrame frame, CancellationToken cancellationToken)
        => _hub.Clients.Group(sessionId).SendAsync(RemoteHubMethods.Frame, frame, cancellationToken);

    /// <summary>
    /// 把一段音频发给"进了这个会话分组"的连接。由每路会话的<b>另一条</b>转发任务调用。
    /// </summary>
    /// <remarks>
    /// 🔥 <b>为什么它必须是另一个 Hub 方法名（<c>Audio</c> 而不是复用 <c>Frame</c>）</b>：
    /// 观看端的处理链完全不同（视频进 MJPEG 画布、音频进 <c>AudioDecoder</c>），
    /// 而且前端要按"收到的是哪种"分别喂给两条<b>独立的抖动缓冲</b>。
    /// 复用 <c>Frame</c> 就得在载荷里再加一个判别字段 —— 那正是"两个队列长得一样、
    /// 于是下一个人以为可以合并"的开端。
    /// </remarks>
    private Task SendAudioToGroupAsync(string sessionId, RemoteAudioFrame frame, CancellationToken cancellationToken)
        => _hub.Clients.Group(sessionId).SendAsync(RemoteHubMethods.Audio, frame, cancellationToken);

    private static string GroupOf(object payload)
        => payload switch
        {
            RemoteSessionDto dto => dto.SessionId,
            RemoteStreamFrame frame => frame.Header.SessionId,
            _ => string.Empty,
        };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Entry[] entries;

        lock (_gate)
        {
            entries = _sessions.Values.ToArray();
            _sessions.Clear();
            _byAgent.Clear();
        }

        foreach (var entry in entries) entry.Dispose();

        try
        {
            _lifetime.Cancel();
        }
        catch
        {
            // 忽略。
        }

        foreach (var entry in entries)
        {
            // 🔥 三条后台任务都要等：视频转发、音频转发、以及被控端那三条上报都跑在
            //    各自的 Task.Run 里。漏等任何一条，停机时它会在宿主已经拆掉之后
            //    还在往一个死掉的 Hub 上写 —— 表现是"关机时往日志里刷一堆异常"，
            //    而那会盖掉真正的停机信息。
            foreach (var loop in new[] { entry.FanoutLoop, entry.AudioLoop })
            {
                if (loop is null) continue;

                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch
                {
                    // 转发任务内部已全量兜住。
                }
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
    // 一路会话的内部状态
    // ═══════════════════════════════════════════════════════════

    private sealed class Entry
    {
        private readonly Stopwatch _window = Stopwatch.StartNew();
        private readonly CancellationTokenSource _cts;

        private long _frames;
        private long _bytes;
        private long _dropped;
        private long _windowFrames;

        /// <summary>最近一个统计窗口内收到的码流字节数（与 <see cref="_windowFrames"/> 同窗口）。</summary>
        private long _windowBytes;

        /// <summary>最后一帧的字节数。<c>int</c> 而不是 <c>long</c> ——
        /// 单帧被封在 <c>RemoteStreamLimits.MaxFrameBytes</c> 以内
        /// （64 MiB，见 <c>RemoteStreamProtocol.MaxPayloadBytes</c>：它<b>整帧</b>穿本机管道，
        /// 到了中心之前才按 <c>MaxChunkPayloadBytes</c> 切片），
        /// 而契约里这个字段本来也是 <c>int</c>。</summary>
        private int _lastFrameBytes;

        // ── 被控端**自己**报的那组数（与上面"中心收到多少"是两件事，别合并）──
        private long _agentFrames;
        private long _agentBytes;
        private double _agentFps;

        /// <summary>
        /// 被控端的桌面已经静止了多久（毫秒；M5.4-c② 步 7）。
        ///
        /// <para>
        /// 🔥 <b>它是"会话级事实"，不是计数</b>：挂在 <see cref="RemoteSessionDto"/> 上而不在
        /// <see cref="RemoteSessionAgentStatsDto"/> 里 —— 后者有一条"一帧都没发过就是 null"
        /// 的判据，而"从开流起桌面就没动过"的机器<b>恰恰一帧都发不出去</b>。
        /// </para>
        ///
        /// <para>
        /// ⚠️ <c>null</c> = 这台机器从没说过（旧客户端）/ 还没报过；<c>0</c> = 刚动过。
        /// <b>两者不是一回事</b>，所以只在"报上来的那条明确给了值"时才覆盖。
        /// </para>
        /// </summary>
        private long? _desktopIdleMs;

        // 被控端报来的音频计数（同样与"中心收到多少"是两件事）。
        // ⚠️ 与 _audioFrames/_audioBytes（中心自己数的）**必须分开**：
        //    合成一个数之后，"链路上掉了"与"它根本没采出来"就再也分不开了，
        //    而那正是这一整组计数存在的全部意义。
        private long _agentAudioFrames;
        private long _agentAudioBytes;
        private long _agentAudioDropped;

        // ── 音频（M5.3）──
        //
        // 🔥 与视频那几个计数**并列存在、绝不合并**。理由与"两个帧计数必须并排"完全一致：
        //    "画面不动了"与"声音没了"在只有一组总数的时候长得一模一样，
        //    而处置相反（一个查编码吞吐、一个查有没有声卡）。
        //    再加上"中心收到多少"与"被控端发出多少"这一对，音频一共四组数，
        //    页面上要能看到它们两两对得上。
        private long _audioFrames;
        private long _audioBytes;
        private DateTimeOffset? _audioLastAt;

        /// <summary>"已经因为队列满而丢掉过音频"提示过没有（每次会话只提示一次）。</summary>
        /// <remarks>
        /// 🔥 为什么要这个标志：<c>DropOldest</c> 通道<b>不会告诉我们它丢了东西</b>，
        /// 只能在写之前看一次队列深度。而这个事件一旦发生就会持续发生
        /// （观看端追不上就是追不上），每条都写日志会把日志刷满，
        /// 反倒把"什么时候开始丢的"这个唯一有用的信息埋掉。
        /// </remarks>
        private bool _audioOverflowLogged;

        // ── 分片重组（M5.4-c①）──
        //
        // 🔥 它**不是**上面那个 `_dropped` 的一部分，两者**绝不合并**：
        //    · `_dropped` = **被控端自己**丢的帧（编不出来 / 单帧超限）—— 去查被控机的负载；
        //    · 本字段 = **中心**在重组时丢的帧（缺片 / 片非法）—— 去查链路。
        //    合并之后这两个故障长得一模一样（都是"画面卡"），而处置相反。
        //    它们并排存在，正是这一整组计数存在的意义（与"两个方向的帧计数必须并排"同一条）。
        //
        // ⚠️ 它由**锁外**的重组路径递增（见 Reassembler 的说明）⇒ 用 Interlocked，不用普通赋值。
        private long _reassemblyDropped;

        /// <summary>"因为重组失败而丢帧"提示过没有（每次会话只提示一次，与音频那条同一条理由）。</summary>
        /// <remarks>
        /// ⚠️ 它也在锁外写。极端情况下（两片几乎同时被判丢）可能提示两次 ——
        /// 那只是多一行日志，而**不是**任何会影响判断的事，所以不值得为它上锁。
        /// </remarks>
        private bool _reassemblyDropLogged;

        /// <summary>
        /// 中心<b>由多片拼成</b>的帧数（M5.4-c① 步 6）。
        /// </summary>
        /// <remarks>
        /// 🔥 <b>它与 <see cref="FramesReceived"/> 是两个数，不许合成一个</b>（见 DTO 的说明）：
        /// 「进来几帧」在 2160×1440 / 4K 上**永远**大于「拼出来几帧」，因为那些帧根本装得下、
        /// 走的是老路。而"分片到底有没有在工作"这个问题，只有当这两个数<b>并排</b>时才答得出来。
        ///
        /// <para>
        /// 🔴 <b>它是端到端冒烟里唯一那条硬判据</b>：本机没有 8K 屏，
        /// 要把"被控端真的切了 / 中心真的拼了 / 浏览器真的画了"变成一条可断言的证据，
        /// 只能把阈值降下来（<c>--chunk-threshold</c>）再看这个数是不是 &gt; 0。
        /// 没有它，冒烟会安安静静跑完一整轮老路然后报绿。 
        /// </para>
        ///
        /// <para>
        /// ⚠️ 与 <see cref="_reassemblyDropped"/> 同一条：由**锁外**的重组路径递增 ⇒ 用 Interlocked。
        /// </para>
        /// </remarks>
        private long _reassembled;

        public Entry(string sessionId, int agentId, string? screenId, string? displayMode, int? maxFps, DateTimeOffset now)
        {
            SessionId = sessionId;
            AgentId = agentId;
            ScreenId = screenId;
            DisplayMode = string.IsNullOrWhiteSpace(displayMode) ? RemoteDisplayModes.Fit : displayMode;
            MaxFps = maxFps;
            RequestedAt = now;
            State = RemoteSessionStates.Requested;

            _cts = new CancellationTokenSource();

            // 🔥 容量 1 + 丢旧留新：观众跟不上时丢画面，而不是把压力传回被控端。
            Fanout = Channel.CreateBounded<RemoteStreamFrame>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

            // 🔥 音频是**另一条**队列，同样的 DropOldest、但容量是 50（见 AudioQueueCapacity）。
            //    刻意不共用：共用的话，被控端每推一帧音频就会把待发的视频帧挤掉
            //    （容量 1 那条只留得住一帧），症状是"一有声音画面就几乎不动"。
            AudioFanout = Channel.CreateBounded<RemoteAudioFrame>(new BoundedChannelOptions(AudioQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        }

        public string SessionId { get; }

        public int AgentId { get; }

        public string? ScreenId { get; }

        public string DisplayMode { get; }

        public int? MaxFps { get; }

        public DateTimeOffset RequestedAt { get; }

        public string State { get; private set; }

        public bool Grayscale { get; private set; }

        // ── 质量档位（M5.5 运行时自适应）──
        //
        // 🔥 三个字段是**策略、算出来的上界、救场痕迹**，不是一件事：
        //   · Quality         —— 用户/页面选的档位名（auto 也是档位，它表示"让 governor 管"）；
        //   · TargetFps/TargetBitrateKbps —— 由档位与请求里的 MaxFps 折出来的**当前目标**；
        //   · QualityDowngraded/Note —— governor 是否已经降过档、为什么。
        // 只存档位名的话，"automatic 已经把你降到低档"与"还在均衡档"在页面上长得一样。
        private int? _targetBitrateKbps;

        /// <summary>这一路当前的质量档位（见 <see cref="RemoteQualityProfiles"/>）。</summary>
        public string Quality { get; private set; } = RemoteQualityProfiles.Auto;

        /// <summary>当前目标帧率（<c>null</c> = 还没折出来）。</summary>
        public int? TargetFps { get; private set; }

        /// <summary>governor 已经降过档没有。</summary>
        public bool QualityDowngraded { get; private set; }

        /// <summary>降档原因（仅在 <see cref="QualityDowngraded"/> 为真时有值）。</summary>
        public string? QualityNote { get; private set; }

        /// <summary>
        /// <b>这一路现在切成几块在编</b>（M5.5 分块编码；1 = 没切）。
        /// <para>
        /// 🔥 由<b>帧头</b>读回来，不是由中心下发：切不切是<b>被控端按自己的硬件定</b>的
        /// （格式上限、能建几路编码器），中心只知道自己"许不许"。
        /// 拿下发值当实情，就会出现"页面说切了 2 块、实际一块没切"。
        /// </para>
        /// </summary>
        public int TileCount { get; private set; } = 1;

        /// <summary>中心<b>要求</b>的视口（0 = 没要求，即整幅画面）。</summary>
        public int ViewportX { get; private set; }

        /// <summary>同上，Y。</summary>
        public int ViewportY { get; private set; }

        /// <summary>视口宽（0 = 没有视口）。</summary>
        public int ViewportWidth { get; private set; }

        /// <summary>视口高（0 = 没有视口）。</summary>
        public int ViewportHeight { get; private set; }

        /// <summary>
        /// 最近一次从被控机读回来的剪贴板内容（M5.5）。
        /// <para>
        /// 🔥 <b>刻意不进 <see cref="RemoteSessionDto"/></b>：那份 DTO 会在每次状态变化、
        /// 每次切档时广播给所有连接，而这段文本可能有 60 KB ——
        /// 塞进去等于"切一次档就把剪贴板重发一遍"。
        /// </para>
        /// </summary>
        public RemoteClipboardDto? Clipboard { get; private set; }

        /// <summary>记下最近一次读到的剪贴板（不广播，广播由注册表负责）。</summary>
        public void SetClipboard(RemoteClipboardDto clipboard) => Clipboard = clipboard;

        // ── 输入回传（M5.2）──
        // 三个字段是**三件事**，别合并：
        //   · InputEnabled —— 中心这道闸门开没开（中心自己说了算）；
        //   · InputState   —— 被控端那边接上了没有（只有它知道）；
        //   · InputReason  —— 接不上的话为什么（"客户端过旧"与"本机禁止"要分开，
        //                     因为一个要升级客户端、一个只要删一个文件）。
        // 缺任何一个都点不动，而它们的处置完全相反（一个去点开关，一个去查客户端版本）。
        public bool InputEnabled { get; private set; }

        public string? InputState { get; private set; }

        /// <summary>输入通道不可用的原因（仅 <c>unavailable</c> 时有值）。</summary>
        public string? InputReason { get; private set; }

        // ── 音频回传（M5.3）──
        //
        // 🔥 只有**两**个字段，而输入那边有三个 —— 这个差别不是抄漏了：
        //    音频没有中心侧的会话级闸门（见 AgentAudioStates 的说明）。
        //    "有声音"是这路会话的默认行为，不像"能不能动别人的鼠标"那样需要逐次授权。
        //    所以：AudioState（被控端接上了没有）+ AudioReason（接不上为什么）。
        /// <summary>被控端的音频通道状态（见 <see cref="AgentAudioStates"/>）；从未上报过时为 <c>null</c>。</summary>
        public string? AudioState { get; private set; }

        /// <summary>音频不可用的原因（仅 <c>unavailable</c> 时有值）。</summary>
        public string? AudioReason { get; private set; }

        public DateTimeOffset? StartedAt { get; private set; }

        public DateTimeOffset? EndedAt { get; private set; }

        public string? EndedReason { get; private set; }

        public string? Codec { get; private set; }

        /// <summary>
        /// 被控端对"这一路用什么格式编"的裁决说明（M5.4-b 第 7 步）；<c>null</c> = 没什么可说的。
        ///
        /// <para>
        /// 🔥 <b>为什么不与 <see cref="Codec"/> 合并</b>：<see cref="Codec"/> 是
        /// <b>从首帧头里读到的事实</b>（"实际用的是 h264"），而本字段是
        /// <b>被控端给出的理由</b>（"中心首选 h264 但它用了 hevc，因为那块屏 5120 宽"）。
        /// 一个是"是什么"，一个是"为什么" —— 页面上要能分开说。
        /// </para>
        /// </summary>
        public string? CodecNote { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public DateTimeOffset? LastFrameAt { get; private set; }

        /// <summary>
        /// 最近一次收到这个被控端的<b>任何上行</b>（一帧画面 <b>或</b> 一条状态上报）的时刻
        /// —— 「这路会话还活着吗」的<b>唯一</b>判据（见 <see cref="Sweep"/>）。
        ///
        /// <para>
        /// 🔥 <b>为什么必须单独立一个字段，而不是继续用 <see cref="LastFrameAt"/></b>：
        /// 被控端的桌面<b>一丝都没动</b>时，DXGI Desktop Duplication 按约定<b>不给新帧</b>，
        /// 于是这条链上一帧都不会产生 —— 而「没有新帧」与「这路流断了」是两个完全不同的事实。
        /// <b>展厅里定格在一张图上的机器走的正是前一条</b>（见 <c>RemoteStreamHost</c> 里
        /// 那段「画面没有新帧 —— 它不是失败、不是丢帧」的说明）。
        /// 拿 <see cref="LastFrameAt"/> 当活性判据，症状不是"少一帧"，而是
        /// <b>这路会话每 15 秒被判死一次、永远连不上</b>。
        /// </para>
        ///
        /// <para>
        /// ⚠️ <b>新增一条上行时，必须在这里明确表态</b>：它的到达算不算"这路会话活着"的证据？
        /// 算 → 刷新这里；不算 → 在下面那处写清为什么不刷新
        /// （<see cref="ObserveAudio"/> 就是<b>不算</b>的那个例子）。
        /// 默不作声地漏掉一处，症状是"某台机器每 15 秒掉一次线"，
        /// 而这行代码离现场隔着一条 SignalR 连接与两个进程 —— 现场根本查不到这里。
        /// </para>
        ///
        /// <para>
        /// ⚠️ 它与 <see cref="LastFrameAt"/> <b>用途不同、不许合并</b>：
        /// 后者是<b>给页面看的</b>（「最近一帧是什么时候到的」），只由 <see cref="Observe"/> 刷新。
        /// </para>
        /// </summary>
        public DateTimeOffset? LastAgentAt { get; private set; }

        public RemoteStreamFrame? Latest { get; private set; }

        public Channel<RemoteStreamFrame> Fanout { get; }

        public Task? FanoutLoop { get; private set; }

        /// <summary>音频转发队列（容量 <see cref="AudioQueueCapacity"/>，与 <see cref="Fanout"/> 是两条）。</summary>
        public Channel<RemoteAudioFrame> AudioFanout { get; }

        /// <summary>
        /// 这一路的<b>分片重组器</b>（M5.4-c①）。<b>每路会话一个</b>，单槽。
        ///
        /// <para>
        /// ⚠️ <b>它自带一把锁，而且调用方必须在账本那把锁（<c>_gate</c>）之外调它</b>：
        /// 拼一份十几 MB 的字节不许压在 <c>_gate</c> 上（那等于让所有会话的所有帧排队等它）。
        /// 见 <see cref="RemoteFrameReassembler"/> 的类注释与
        /// <c>RemoteSessionRegistry.AcceptFrameChunk</c>。
        /// </para>
        /// </summary>
        public RemoteFrameReassembler Reassembler { get; } = new();

        public Task? AudioLoop { get; private set; }

        /// <summary>"还没结束"（正在推 / 刚登记）。</summary>
        public bool IsLive => State is RemoteSessionStates.Requested or RemoteSessionStates.Streaming;

        /// <summary>把两条转发任务都挂上（由注册表在登记会话时调）。</summary>
        /// <remarks>
        /// ⚠️ <b>两条都要挂，而且必须在同一处挂</b>：只挂视频那条，音频会一直躺在队列里
        /// 直到容量 50 被填满、然后从最旧的开始被挤出去 —— 现场听到的是
        /// "一开口就丢前 1 秒"，而没有任何一处日志说得出这件事。
        /// </remarks>
        public void StartFanouts(
            Func<string, RemoteStreamFrame, CancellationToken, Task> sendFrame,
            Func<string, RemoteAudioFrame, CancellationToken, Task> sendAudio)
        {
            FanoutLoop ??= PumpAsync(Fanout, sendFrame);
            AudioLoop ??= PumpAsync(AudioFanout, sendAudio);
        }

        /// <summary>一条转发任务的通用骨架：<b>排空队列 + 逐条发出去</b>。</summary>
        /// <remarks>
        /// 🔥 <b>这里只放"排空 + 发送"这两件没有流类型差异的事，任何按流类型分叉的策略都不许进来</b>
        /// （丢帧规则、队列容量、"忙就丢"还是"攒够再发"全部在<b>通道本身</b>的构造里定，
        /// 见构造函数）。一旦有人往这个循环体里塞一句"视频才要做的事"，
        /// 它就会静默地对音频生效 —— 而两边都不会报错。
        /// </remarks>
        private Task PumpAsync<T>(Channel<T> channel, Func<string, T, CancellationToken, Task> send)
            => Task.Run(async () =>
            {
                try
                {
                    await foreach (var item in channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                    {
                        await send(SessionId, item, _cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常收场。
                }
                catch
                {
                    // ⚠️ 转发失败绝不外抛：它跑在后台任务里，抛出去就是"整条转发链静默死掉"，
                    //    而表现是"会话还在、画面/声音不动了"。
                }
            });

        /// <summary>
        /// <b>「这个被控端还在」—— 收到一片也是证据</b>（M5.4-c① 的分片）。
        ///
        /// <para>
        /// 🔥 <b>为什么它必须单独存在，而不是"顺手在重组那条路上写一句 LastAgentAt"</b>：
        /// 本类有一条规矩 —— <b>每新增一条上行，都必须在 <see cref="LastAgentAt"/> 那里
        /// 明确表态"它算不算活性"</b>（见那个字段的说明）。
        /// 音频是"<b>不算</b>"的那个例子（只有声音在走、画面早就没了，不该让它赖着不走）；
        /// <b>分片算</b> —— 它与整帧走的是同一条流、同一种内容，只是被切成几段送，
        /// 而且它同样可能"拼齐了"（那就走 <see cref="Observe"/> 刷一次）。
        /// </para>
        ///
        /// <para>⚠️ 调用方必须在 <c>_gate</c> <b>之内</b>调它（本类的字段一律锁内改）。</para>
        /// </summary>
        public void TouchAgentActivity() => LastAgentAt = DateTimeOffset.UtcNow;

        /// <summary>
        /// 记一次"<b>中心因重组失败而丢帧</b>"（M5.4-c①）。
        /// </summary>
        /// <returns>真的 = <b>这是本次会话第一次</b>丢（调用方据此提示一次）。</returns>
        /// <remarks>
        /// ⚠️ 用 <see cref="Interlocked"/> 而不是普通赋值：它由<b>锁外</b>的重组路径递增
        /// （拼字节刻意离开 <c>_gate</c>，见 <see cref="Reassembler"/> 的说明）。
        /// "字段一律锁内改"这条规矩在这里的<b>例外必须被写出来</b>，
        /// 否则下一个人会以为这里漏加了一把锁，然后给它套上 <c>_gate</c> ——
        /// 而那正好把"拼十几 MB 不许拿账本的锁"那件事又破了。
        /// </remarks>
        public bool NoteReassemblyDrop()
        {
            Interlocked.Increment(ref _reassemblyDropped);

            if (_reassemblyDropLogged) return false;

            _reassemblyDropLogged = true;

            return true;
        }

        /// <summary>
        /// 记一次"<b>中心把多片拼成了一帧</b>"（M5.4-c① 步 6）。
        /// </summary>
        /// <remarks>
        /// ⚠️ 与 <see cref="NoteReassemblyDrop"/> 同一条：由<b>锁外</b>的重组路径递增。
        /// ⚠️ 它<b>不</b>替 <c>FramesReceived</c> 加数 —— 那个由
        /// <c>AcceptFrame</c> → <c>Observe</c> 递增（老路与分片路**都必须**经过它，
        /// 这样"帧率"这个数在两个模式下口径一致）。两者各记各的，合起来才有信息量。
        /// </remarks>
        public void NoteReassemblyCompleted() => Interlocked.Increment(ref _reassembled);

        public void Observe(RemoteStreamFrame frame)
        {
            var header = frame.Header;

            // 🔥 一帧就是"这个被控端还在"的正证据 —— 两个时间戳**同取一次 now**，
            //    免得它们之间差出一次调度（那会让"最近一帧"偶尔晚于"最近回话"，
            //    读日志的人会以为有一帧是凭空来的）。
            var now = DateTimeOffset.UtcNow;

            _frames++;
            _bytes += frame.Data.Length;
            _windowFrames++;
            _windowBytes += frame.Data.Length;
            _lastFrameBytes = frame.Data.Length;

            // 🔥 **分块 / ROI 之后，头里的 Width/Height 只是"这一块"**（M5.5）。
            //    会话的尺寸必须报**整幅画面** —— 页面的画布就按它建。
            //    混用会让一开视口，页面画布就缩成那一块（"整个页面只剩画面的一角"，且不报错）。
            //
            // 🔴 **而且"整幅"是码流坐标系里的整幅，不是物理桌面尺寸**（M7.8）。
            //    被控端会降采样（MJPEG 把长边压到 1920），那时 5760×2160 的三屏拼接
            //    在码流里只有 1920×720，帧头也如实报 1920×720。
            //    ⚠️ 这里**必须**跟着帧头走，不能悄悄换回物理尺寸：两者混用正是
            //    "画面只占左上 1/9"那条 bug 的来源。要看物理尺寸请查屏清单
            //    （`GET /api/remote/screens/{agentId}`），那是另一个来源。
            Width = header.FrameWidth > 0 ? header.FrameWidth : header.Width;
            Height = header.FrameHeight > 0 ? header.FrameHeight : header.Height;
            TileCount = header.TileCount > 0 ? header.TileCount : 1;
            Codec = header.Codec;
            Latest = frame;
            LastFrameAt = now;
            LastAgentAt = now;
            _dropped = header.Dropped;

            StartedAt ??= DateTimeOffset.UtcNow;

            if (State != RemoteSessionStates.Streaming) State = RemoteSessionStates.Streaming;

            // 窗口到期就把"最近这 3 秒的帧数"换算成 fps，并重开窗口。
            if (_window.Elapsed >= FpsWindow)
            {
                var seconds = _window.Elapsed.TotalSeconds;

                Fps = Math.Round(_windowFrames / seconds, 1);

                // 🔥 码率与帧率**同一个窗口、同一次重开**：两个数摆在一起才有意义
                //    （"帧率掉了一半而码率没变" ⇒ 每帧变大了；"两个都掉" ⇒ 编码侧整体变慢）。
                //    各用各的窗口时，它们永远对不齐。
                BitsPerSecond = Math.Round(_windowBytes * 8 / seconds, 0);

                _window.Restart();
                _windowFrames = 0;
                _windowBytes = 0;
            }

            // 丢旧留新的写入**永不阻塞**（满了就把最旧的挤出去），所以这里可以放心调用。
            Fanout.Writer.TryWrite(frame);
        }

        public double Fps { get; private set; }

        /// <summary>
        /// <b>最近 3 秒的实测入向码率</b>（bit/s）。
        ///
        /// <para>
        /// 🔥 <b>它是并发配额唯一的判据</b>（见 <c>RemoteOptions.MaxTotalMbps</c>）：
        /// 用"配置的码率上界"去判会永远判不出来（配 20 Mbps 的路可能只跑 3 Mbps），
        /// 而用"从开流到现在的总平均"去判，会在码率刚冲高的那几秒里看不见危险。
        /// 只有滑动窗口能回答"<b>此刻</b>这块网卡上跑了多少"。
        /// </para>
        ///
        /// <para>⚠️ 它只在 <see cref="FpsWindow"/> 到期时更新一次，
        /// 所以两次更新之间读到的是上一窗口的值（最大 3 秒陈旧）—— 配额判定接受这个粒度。</para>
        /// </summary>
        public double BitsPerSecond { get; private set; }

        /// <summary>
        /// 记一段音频，并把它塞进音频转发队列。
        /// </summary>
        /// <returns>
        /// 真的 = <b>第一次</b>发现这支队列已经积压到需要丢最旧的了（调用方据此提示一次）。
        /// </returns>
        /// <remarks>
        /// 🔥 <b>与 <see cref="Observe"/> 有一处刻意的不同：这里不保存"最后一段"。</b>
        /// 视频那边 <c>Latest</c> 有一个明确用途 —— 新观众进来时补一帧，别让他对着黑屏等。
        /// 音频<b>绝对不能</b>这么干：给刚进来的观众补一段"上一秒采集的音频"，
        /// 他会听到一声来路不明的短音，而且那一段的 <c>CapturedMonoMs</c> 远早于
        /// 他的播放锚点，直接把它自己的对齐钟拉歪。
        /// 新观众从"下一段"开始听，是唯一正确的行为。
        /// </remarks>
        public bool ObserveAudio(RemoteAudioFrame frame)
        {
            _audioFrames++;
            _audioBytes += frame.Data.Length;
            _audioLastAt = DateTimeOffset.UtcNow;

            // 音频不像视频那样参与状态机：它**不是**"这路会话活着"的判据。
            // （一台没有声卡的机器照样在正常推画面，把音频当成活性信号会让它被 Sweep 误杀。）
            // 所以这里刻意不碰 State / StartedAt / LastFrameAt。
            //
            // 🔥 **也刻意不碰 LastAgentAt** —— 那是本类里"算不算活性"唯一的开关，
            //    所以这条"不算"必须是一次**明写出来**的决定，而不是"忘了加"。
            //    反过来问一句：只有声音在走、画面早就没了，该不该让它赖着不走？**不该。**
            //    （有 `Audio_alone_is_not_a_liveness_signal` 钉住这一点。）

            // DropOldest 通道**不会回报"我丢了一条"**，所以只能在写之前看一眼队列深度。
            // 能看的前提是通道支持计数（CreateBounded 出来的都支持）。
            var overflowed = false;

            if (!_audioOverflowLogged
                && AudioFanout.Reader.CanCount
                && AudioFanout.Reader.Count >= AudioQueueCapacity)
            {
                _audioOverflowLogged = true;
                overflowed = true;
            }

            // 同样永不阻塞：满了就把最旧的挤出去。
            AudioFanout.Writer.TryWrite(frame);

            return overflowed;
        }

        public void ApplyState(AgentRemoteSessionStateDto state, out bool close)
        {
            close = false;

            // 🔥 **收到任何状态上报就是"这个被控端还在"的正证据**（见 LastAgentAt 的说明）。
            //
            //    ⚠️ 必须在**任何 return 之前**刷新，而且**不看** state.State 是哪一个：
            //       一条"我还停在 requested"的回话同样是活的证据 —— 它证明指令送到了。
            //       把它按状态分档处理（"只有 streaming 才算"），就会把
            //       "接到了、正在建编码器"当成"没接到"，而那正是 20 秒那条判据要区分的事。
            LastAgentAt = DateTimeOffset.UtcNow;

            // 🔥 被控端报的数**必须留**，不能只拿它判状态就扔掉。
            //    它与"中心收到多少"摆在一起才是这条链路上最有用的一个诊断：
            //      它发 500 / 我们收 12 ⇒ 链路上在掉；它发 3 / 我们收 3 ⇒ 是被控端编不动。
            //    只看一个数，这两种故障长得一模一样（都是"画面很卡"）。
            _agentFrames = state.Frames;
            _agentBytes = state.Bytes;
            _agentFps = state.Fps;

            _agentAudioFrames = state.AudioFrames;
            _agentAudioBytes = state.AudioBytes;

            // 音频的丢帧数与视频同一条规矩：状态上报里是"到此刻为止的累计值"，
            // 取大值就等于"取更新的那一份"。
            if (state.AudioDropped > _agentAudioDropped) _agentAudioDropped = state.AudioDropped;

            // 帧头里带的丢帧数更新得更勤（每帧都有），状态上报里的是终态那一份。
            // 两边是同一个累计计数器，取大值就等于"取更新的那个"。
            if (state.Dropped > _dropped) _dropped = state.Dropped;

            // 🔥 桌面静止时长（M5.4-c② 步 7）：**只在报上来的那条明确给了值时才覆盖**。
            //
            //    ⚠️ `null` = 这台机器答不出（旧托盘）/ 这一条没带 —— 那是"没说"，
            //       不是"桌面动了"。每次上报都清一遍的话，只要混进一条老版本的消息，
            //       页面就会从"已静止 12 秒"闪回"—"，而那条消息什么都没否认。
            //    ⚠️ `0` = 刚动过，是一个**值**：写成 `if (idle > 0)` 会让"静止 12 秒 →
            //       用户动鼠标"之后那句话再也不消失（直到会话结束）。
            if (state.DesktopIdleMs is { } desktopIdleMs) _desktopIdleMs = desktopIdleMs;

            // 🔥 输入通道状态**独立于会话状态**，所以它必须在 switch **之前**就记下来。
            //    原因很实在：输入通道的结论往往比第一帧来得还早
            //    （旧托盘根本没建那条管道，连不上是毫秒级的），
            //    而那时会话还停在 requested —— 若把它塞在某个 case 里，
            //    这条"被控端不支持输入"的结论就会因为"状态分支不对"而丢掉，
            //    页面上于是永远显示"正在连接输入通道"。
            //
            // ⚠️ 只在被控端**明确说了**的时候才覆盖：null = 这条上报里没提
            //    （旧客户端不认识这个字段），此时保留上一次的值。
            if (state.InputState is not null)
            {
                InputState = state.InputState;

                // ⚠️ 原因**只在"不可用"时有值**，与 Active.SetInputState 同一条规矩：
                //    就绪之后还留着上一次的失败原因，页面上就会变成
                //    "通道就绪：本机已禁止远程输入" —— 一句自相矛盾的话。
                //
                // ⚠️ 用**专门那个 InputReason 字段**，不借 state.Reason ——
                //    state.Reason 的本义是"这路会话为什么结束"，
                //    拿它当输入不通的原因，会在"会话正常结束 + 输入本来就不通"时
                //    显示成"输入通道不可用：已在本机取消推流"。见该字段的说明。
                InputReason = RemoteChannelStates.ReasonToKeep(
                    state.InputState, AgentInputStates.Unavailable, state.InputReason);
            }

            // 🔥 音频与输入**同一条纪律、同一个位置**：必须在 switch 之前记，
            //    因为"接不上"这个结论往往比第一帧来得还早（托盘没有声卡 / 客户端过旧），
            //    那时会话还停在 requested —— 塞进某个 case 里就会把结论丢掉，
            //    页面上于是永远显示"正在连接音频通道"，而实际上它根本连不上。
            //
            // ⚠️ 只在被控端**明确说了**的时候才覆盖（null = 旧客户端不认识这个字段）。
            // ⚠️ 原因只在"不可用"时有值，与 Active.SetAudioState 同一条规矩：
            //    就绪之后还留着上一次的失败原因，页面上会出现
            //    "音频通道就绪：本机没有音频输出设备" 这种自相矛盾的话。
            if (state.AudioState is not null)
            {
                AudioState = state.AudioState;

                // ⚠️ 这里用的是专门那个 AudioReason 字段，**不是** state.Reason ——
                //    与输入那边不同。区别很实在：state.Reason 是"这路会话为什么结束"，
                //    一条成功结束的会话（观众点了停止）也会带上它，
                //    拿它当"没有声卡"的原因去显示，用户会看到一句完全不相关的话。
                AudioReason = RemoteChannelStates.ReasonToKeep(
                    state.AudioState, AgentAudioStates.Unavailable, state.AudioReason);
            }

            // 🔥 格式裁决说明（第 7 步）：**与上面两条的规矩刻意不同**，它不做"只在不可用时保留"
            //    那种处理 —— 因为它说的不是"某个通道坏了"，而是
            //    "这一路为什么没按中心的首选编、但仍然正常在推"，这是一个**始终成立的事实**。
            //    把 ReasonToKeep 那套挪过来，会在会话状态从 requested 变成 streaming 时
            //    把这句话清掉 —— 而那正是观众最需要看到它的时刻。
            //
            // ⚠️ 只在被控端明确说了的时候才覆盖：null = 这条上报里没提（老客户端不认识这个字段）。
            if (state.CodecNote is not null) CodecNote = state.CodecNote;

            switch (state.State)
            {
                case RemoteSessionStates.Streaming:
                    StartedAt ??= DateTimeOffset.UtcNow;
                    if (State == RemoteSessionStates.Requested) State = RemoteSessionStates.Streaming;
                    break;

                case RemoteSessionStates.Stopped:
                    State = RemoteSessionStates.Stopped;
                    EndedReason = state.Reason;
                    close = true;
                    break;

                case RemoteSessionStates.Failed:
                    State = RemoteSessionStates.Failed;
                    EndedReason = state.Reason;
                    close = true;
                    break;

                default:
                    // requested 之类：不改动已有状态（它可能已经是 streaming 了，
                    // 此时被一条迟到的 requested 覆盖会退回去 —— 那是错的）。
                    break;
            }
        }

        public void SetGrayscale(bool enabled) => Grayscale = enabled;

        /// <summary>
        /// 改这一路的质量策略（M5.5）。<b>只记账，不下发</b> —— 下发在
        /// <c>RemoteSessionService.SetQualityAsync</c>。
        ///
        /// <para>
        /// 🔥 <b>为什么目标值是参数、而不是在这里按档位名算</b>：
        /// governor 降档时档位仍然是 <c>auto</c>（用户选的是"让系统管"），
        /// 但目标值已经变成低档的数。如果在这里按名字算，
        /// "auto 降过档"与"auto 还没降"会被算成同一组数 —— 而那正是要分开的两件事。
        /// </para>
        /// </summary>
        public void ApplyQuality(string profile, int targetFps, int targetBitrateKbps, bool downgraded, string? note)
        {
            Quality = RemoteQualityProfiles.Normalize(profile);
            TargetFps = targetFps;
            _targetBitrateKbps = targetBitrateKbps;
            QualityDowngraded = downgraded;
            QualityNote = note;
        }

        /// <summary>
        /// 记下"中心要求只编这一块"（M5.5 ROI）。<b>只记账，不下发</b>
        /// —— 下发在 <c>RemoteSessionService.SetViewportAsync</c>。
        /// </summary>
        public void ApplyViewport(int x, int y, int width, int height)
        {
            var off = width <= 0 || height <= 0;

            ViewportX = off ? 0 : Math.Max(0, x);
            ViewportY = off ? 0 : Math.Max(0, y);
            ViewportWidth = off ? 0 : width;
            ViewportHeight = off ? 0 : height;
        }

        /// <summary>开 / 关输入回传的闸门（中心侧，见 <see cref="SetInputEnabled"/>）。</summary>
        public void SetInputEnabled(bool enabled) => InputEnabled = enabled;

        public void MarkEnded(string reason, bool failed)
        {
            if (!IsLive) return;

            State = failed ? RemoteSessionStates.Failed : RemoteSessionStates.Stopped;
            EndedAt = DateTimeOffset.UtcNow;
            EndedReason = reason;
        }

        public RemoteSessionDto ToDto() => new(
            SessionId: SessionId,
            AgentId: AgentId,
            State: State,
            DisplayMode: DisplayMode,
            Grayscale: Grayscale,
            RequestedAt: RequestedAt,
            StartedAt: StartedAt,
            EndedAt: EndedAt,
            EndedReason: EndedReason,
            ScreenId: ScreenId,
            Codec: Codec,
            Stats: _frames == 0 && LastFrameAt is null && _audioFrames == 0
                    && Interlocked.Read(ref _reassemblyDropped) == 0
                ? null
                : new RemoteSessionStatsDto(
                    FramesReceived: _frames,
                    BytesReceived: _bytes,
                    Fps: Fps,
                    LastFrameBytes: _lastFrameBytes,
                    LastFrameAt: LastFrameAt,
                    DroppedFrames: _dropped,
                    AudioFramesReceived: _audioFrames,
                    AudioBytesReceived: _audioBytes,

                    // ⚠️ 与 DroppedFrames **并排、不合并**（见 _reassemblyDropped 的说明）：
                    //    一个是"被控端编不动"，一个是"链路上把片丢了"，处置相反。
                    //
                    // 🔥 上面那个"Stats 为 null"的判据里**也把它算了进去**：
                    //    否则"每一帧都因为缺片而被丢掉"这种情况（真出现时 `_frames` 恒为 0）
                    //    会在页面上什么都不显示 —— 而那正是最该被看见的一种故障。
                    ReassemblyDroppedFrames: Interlocked.Read(ref _reassemblyDropped),

                    // ⚠️ 与 FramesReceived **并排、不合并**：那个数在 2160×1440 / 4K 上
                    //    永远大于本参数（那些帧装得下，走的是老路）。
                    //    "分片到底有没有在工作"只有两个数并排时才答得出来。
                    //    不必加进上面那个 null 判据：本参数 > 0 一定意味着 `_frames` > 0
                    //    （拼成的帧要经过 AcceptFrame → Observe 才会记进 `_frames`）。
                    ReassembledFrames: Interlocked.Read(ref _reassembled)),
            AgentStats: _agentFrames == 0 && _agentBytes == 0 && _agentAudioFrames == 0
                ? null
                : new RemoteSessionAgentStatsDto(
                    FramesSent: _agentFrames,
                    BytesSent: _agentBytes,
                    Fps: _agentFps,
                    AudioFramesSent: _agentAudioFrames,
                    AudioBytesSent: _agentAudioBytes,
                    AudioDropped: _agentAudioDropped),
            InputEnabled: InputEnabled,
            InputState: InputState,
            InputReason: InputReason,
            AudioState: AudioState,
            AudioReason: AudioReason,
            CodecNote: CodecNote,

            // 🔥 会话级、与计数解耦（M5.4-c② 步 7）：一帧都没发过也要能带上它
            //    —— "从开流第一秒桌面就没动过"的机器正是最需要这句话的那一台。
            DesktopIdleMs: _desktopIdleMs,

            // 🔥 同样与会话级事实并列（M5.5）：档位与目标即使一帧都没来也要显示，
            //    否则"刚建会话、还在建编码器"的那几秒里，页面会以为这一路没有质量策略。
            Quality: Quality,
            TargetFps: TargetFps,
            TargetBitrateKbps: _targetBitrateKbps,
            QualityDowngraded: QualityDowngraded,
            QualityNote: QualityNote,
            ViewportX: ViewportX,
            ViewportY: ViewportY,
            ViewportWidth: ViewportWidth,
            ViewportHeight: ViewportHeight,
            TileCount: TileCount);

        public void Dispose()
        {
            try
            {
                Fanout.Writer.TryComplete();
            }
            catch
            {
                // 忽略。
            }

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