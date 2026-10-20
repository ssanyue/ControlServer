using ControlServer.Contracts;
using ControlServer.Core.Services;
using ControlServer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ControlServer.Server.Hubs;

/// <summary>
/// <b>观看端</b>的实时通道（<c>/hubs/remote</c>，M5.1）—— 管理员浏览器连进来<b>看画面</b>。
///
/// <para>
/// 🔥 <b>它与 <see cref="AgentHub"/> 是两条方向相反的通道，不要试图合并</b>：
/// <c>/hubs/agent</c> 是<b>被控机连出来</b>的（拿指令、送上报），认的是 <c>AgentKey</c>（机器身份）；
/// 本通道是<b>人连进来</b>的，认的是 Cookie（角色）。两者的身份模型、鉴权策略、
/// 消息形状没有一处相同 —— 合成一条，"谁在发这条消息"会变成一个要靠上下文猜的问题。
/// </para>
///
/// <para>
/// 🔥 <b>整个 Hub 要求 <see cref="Policies.AdminOnly"/></b>（决策 11）：
/// <b>非管理员连"只读观看"都不允许</b>。远控的价值点在这里是"看到别人屏幕"本身，
/// 不是"点得动别人屏幕"，所以连只读也不放宽
/// （这与"在线表允许普通角色看"是两条不同的判据）。
/// </para>
///
/// <para>
/// <b>为什么用"分组"而不是在服务端维护一张"谁在看哪一路"的表</b>：
/// 分组是 SignalR 自带的、且<b>连接断开时自动清理</b>。自己维护那张表的话，
/// 就得处理"连接异常掉线但没来得及通知"这一整类漏水问题 ——
/// 而漏掉的后果是往一个已经不存在的连接一直发 4K 帧。
/// </para>
///
/// <para>
/// 🔥 <b>"多人同时看一路"是支持的场景，不是要规避的情况</b>（决策 8：并发上限 4）。
/// 分组天然支持它 —— 每一路是一个组，谁 Join 谁收帧，彼此互不干扰。两件必须知道的事：
/// <list type="bullet">
///   <item>闸门（<c>InputEnabled</c>）是<b>会话级</b>的：一个人打开，<b>所有</b>正在看
///         这一路的人都获得控制能力。方案里刻意<b>没有</b>独占控制权，所以
///         "现在是谁在控"这件事服务端不区分 —— 页面上必须自己把它说清楚，
///         否则现场会出现两个人同时抢鼠标而互不知情；</item>
///   <item><b>收摊不看人数</b>：观众全走光不会让被控端停流（见
///         <see cref="OnDisconnectedAsync"/>），停流由会话空闲超时与"结束会话"兜底。</item>
/// </list>
/// </para>
/// </summary>
[Authorize(Policy = Policies.AdminOnly)]
public sealed class RemoteHub : Hub
{
    /// <summary>
    /// 连接当前"在看哪一路"的会话 Id。
    /// <para>
    /// 🔥 <b>为什么要把它寄在 <c>Context.Items</c> 上，而不是"每次去看一遍分组"</b>：
    /// SignalR 的分组 API 是<b>只写</b>的（能加能删，不能查"这个连接在不在这一组"）。
    /// 而输入回传必须回答这个问题 —— 见 <see cref="SendInput"/>。
    /// </para>
    /// <para>
    /// ⚠️ 用 <c>Items</c> 存是有前提的：<b>一条连接同时只看一路</b>。
    /// 前端就是这么用的（切会话先 <c>Leave</c>），所以它是单值；
    /// 而 <see cref="Join"/> 会<b>强制</b>这个前提（进来先把上一路退干净，
    /// 见 <see cref="UnwatchAsync"/>）—— 不指望客户端自觉。
    /// 将来真要做"一格同时看多路"，这里必须换成集合，
    /// 而归属判断要一起改：那个判断点被刻意收在 <see cref="RemoteWatchScope"/> 一处。
    /// </para>
    /// </summary>
    private const string WatchedSessionKey = "remote.watched-session";

    private readonly RemoteSessionRegistry _sessions;
    private readonly RemoteSessionService _service;
    private readonly LogService _log;

    public RemoteHub(RemoteSessionRegistry sessions, RemoteSessionService service, LogService log)
    {
        _sessions = sessions;
        _service = service;
        _log = log;
    }

    /// <summary>这条连接当前在看哪一路；没在看（或从没 Join 过）则 <c>null</c>。</summary>
    private string? WatchedSession
        => Context.Items.TryGetValue(WatchedSessionKey, out var value) ? value as string : null;

    /// <summary>这条连接有没有资格操作这一路（归属校验的唯一判据）。</summary>
    private bool IsWatching(string sessionId) => RemoteWatchScope.IsWatching(WatchedSession, sessionId);

    /// <summary>
    /// 加入一路会话的分组 —— 之后的每一帧都会推给这个连接。
    ///
    /// <para>
    /// 🔥 <b>为什么要先 Join 而不是"创建连接就自动收到所有帧"</b>：
    /// 一帧 1080p 的 MJPEG 就是几十到几百 KB，4K 更大。
    /// 给每个连上控制台的浏览器都发一遍全部会话的帧，是<b>拿带宽换事故</b>：
    /// 十个窗口开着、其中九个没在看画面，而那九个照样在收。
    /// 只有"明确说了我要看这一路"的连接才进分组。
    /// </para>
    ///
    /// <para>
    /// <b>返回加入时的快照 + 立刻补一帧</b>：不然新观众要等到下一帧才知道画面长什么样，
    /// 而在低帧率（被控端编不动时可能只有 2–3 fps）下，那意味着<b>半秒以上的黑屏</b> ——
    /// 恰好是"点了没反应"的观感。
    /// </para>
    /// </summary>
    /// <returns>见 <see cref="RemoteWatchAckDto"/>。失败时 <c>Ok=false</c> 并带机器可读的原因。</returns>
    public async Task<RemoteWatchAckDto> Join(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new RemoteWatchAckDto(
                false, RemoteErrorCodes.SessionNotFound, "会话 Id 为空。", null);
        }

        var session = _sessions.TryGet(sessionId);

        if (session is null)
        {
            // 会话已经结束（观众点了结束 / 被控端失败 / 中心刚重启）。
            // ⚠️ 刻意**不**在这里"顺手把它建起来"：Join 是只读动作，
            //    让它有副作用的话，一个刷新页面的动作就能在被控机上起一路流。
            //
            // 🔥 也刻意**不动**当前正在看的那一路：一次失败的 Join 不该把用户
            //    眼前好好的画面带走（切到一个刚被结束的会话时，页面上还剩着上一路的
            //    画面 —— 此时把他踢出上一路，等于"点错一次就黑屏"）。
            return new RemoteWatchAckDto(
                false,
                RemoteErrorCodes.SessionNotFound,
                $"不存在会话「{sessionId}」（可能已结束）。刷新列表看看现在有什么。",
                null);
        }

        // 🔥 **同一条连接同时只看一路** —— 上一路在这里退干净，
        //    而不是指望客户端先调 <see cref="Leave"/>。
        //
        //    理由：客户端会忘（刷新、切标签、网络抖动让那次 Leave 没送到），
        //    而**忘了的代价是双向的**：那条连接会一直收着上一路的高频帧
        //    （一帧几十到几百 KB，页面上却已经看不到它了），
        //    同时它还能继续往上一路送输入（见 SendInput 的归属校验）。
        //    把"只看一路"变成服务端强制的不变量，比靠客户端自觉稳。
        await UnwatchAsync().ConfigureAwait(false);

        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId).ConfigureAwait(false);

        // 记住"这个连接在看哪一路"（输入回传要靠它判归属，见 WatchedSessionKey）。
        Context.Items[WatchedSessionKey] = sessionId;

        // 先给一帧，别让新观众对着黑屏等下一帧。
        //
        // ⚠️ 顺序是"先入组、再补帧"：反过来的话，补的那一帧与入组之后的第一帧
        //    之间如果来了新帧，观众会看到一次**旧帧盖新帧**的闪回。
        if (_sessions.Latest(sessionId) is { } latest)
        {
            await Clients.Caller.SendAsync(RemoteHubMethods.Frame, latest).ConfigureAwait(false);
        }

        _log.Info("Remote", $"[Remote] 观看端加入会话 {sessionId}（连接 {Context.ConnectionId}）。");

        return new RemoteWatchAckDto(true, null, null, session);
    }

    /// <summary>
    /// 退出一路会话的分组（页面切走 / 关掉那一格时调用）。
    /// <para>
    /// 不调也不会漏帧给"已经不看的人"—— 连接断开时 SignalR 会自己清分组。
    /// 但页面在<b>同一条连接上</b>切到别的会话时，连接并没有断，所以必须显式退，
    /// 否则那个连接会一直收着上一路的高频帧。
    /// </para>
    /// </summary>
    public async Task Leave(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId).ConfigureAwait(false);

        // 退掉之后这个连接就**不再**有资格给这一路送回输入 ——
        // 不清掉这个标记的话，"切走了还能继续点"会变成一个很难理解的怪现象。
        //
        // ⚠️ **只在"退的正好是当前看的那一路"时才摘登记**：前端切会话的次序是
        //    "先 Leave 旧的、再 Join 新的"，但重复调用与网络重发都可能让
        //    `Leave` 带着一个**已经不是当前那一路**的 Id 到（例如切走之后那次迟到的重发）。
        //    无条件摘的话，那一瞬间会把**新**的观看登记误摘掉 ——
        //    症状是"刚切过去，点第一下就被拒"。
        if (IsWatching(sessionId))
        {
            Context.Items.Remove(WatchedSessionKey);
        }
    }

    /// <summary>
    /// 把这条连接的"正在看哪一路"清干净：<b>退组 + 摘登记</b>，两件事一起做。
    /// </summary>
    /// <returns>被清掉的那一路；本来就没在看则 <c>null</c>。</returns>
    /// <remarks>
    /// 🔥 两件事必须成对：只摘登记不退组 → 那条连接**一直收着**已经不看的那一路的帧
    /// （一帧几十到几百 KB，而页面上什么都没有，是最难解释的一类带宽占用）；
    /// 只退组不摘登记 → 它**还留着**给那一路送输入的资格。见 <see cref="RemoteWatchScope"/>。
    /// </remarks>
    private async Task<string?> UnwatchAsync()
    {
        if (WatchedSession is not { } previous) return null;

        Context.Items.Remove(WatchedSessionKey);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, previous).ConfigureAwait(false);

        return previous;
    }

    /// <summary>
    /// <b>送回一批输入事件</b>（M5.2）—— 本方案里唯一一条"从浏览器反向进屋"的操作流。
    ///
    /// <para>
    /// 🔥 <b>两道闸门，缺一不可</b>：
    /// </para>
    /// <list type="number">
    ///   <item><b>你正在看这一路</b>（<see cref="WatchedSessionKey"/>，
    ///         判据见 <see cref="RemoteWatchScope.IsWatching"/>）。
    ///         这不是形式主义：输入是"照着画面点"的动作，
    ///         一个没在看这一路的连接送进来的坐标没有任何意义，
    ///         而且它多半意味着"某个早已被遗忘的页面还在发东西"；
    ///         🔥 但它<b>不是</b>"控制权"—— 多人同时看一路时，
    ///         <b>任何一个正在看这一路的人</b>都有资格操作（见
    ///         <see cref="RemoteErrorCodes.NotWatching"/>）；</item>
    ///   <item><b>这一路的控制开关开着</b>（默认关，见
    ///         <see cref="RemoteSessionDto.InputEnabled"/>）——
    ///         "能看"与"能控"必须由一次明确的动作分开。</item>
    /// </list>
    ///
    /// <para>
    /// <b>角色这一关在类上</b>（<c>AdminOnly</c>）：非管理员连这个 Hub 都连不上，
    /// 所以这里不必再判一次"是不是管理员"——多判一次只会让人以为
    /// 有第二条绕过权限的路。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>返回值是"给页面的解释"，不是刹车信号</b>：这一路输入没送进去，
    /// 画面与会话都还在。所以它必须把话说清楚（没开开关 / 被控端是旧版 /
    /// 机器掉线）—— 因为"点了没反应"本身在屏幕上完全看不出是哪种原因。
    /// </para>
    /// </summary>
    public async Task<RemoteInputAckDto> SendInput(RemoteInputBatchDto? batch)
    {
        if (batch is null || string.IsNullOrWhiteSpace(batch.SessionId))
        {
            return new RemoteInputAckDto(false, RemoteErrorCodes.SessionNotFound, "请求里没有会话 Id。", 0);
        }

        var watched = Context.Items.TryGetValue(WatchedSessionKey, out var value) ? value as string : null;

        if (!RemoteWatchScope.IsWatching(watched, batch.SessionId))
        {
            return new RemoteInputAckDto(
                false,
                RemoteErrorCodes.NotWatching,
                "这个连接当前没有在观看这一路会话（可能已经切走或刷新过）。重新打开这一路再操作。",
                0);
        }

        return await _service
            .SendInputAsync(batch.SessionId, batch.Events, Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <b>打开 / 关闭这一路会话的输入回传</b>（M5.2）。
    /// </summary>
    /// <remarks>
    /// 🔥 它<b>不碰网络</b>：闸门就在中心的账本上（这一点与"黑白模式"完全不同，
    /// 后者要下指令给被控端）。所以：
    /// <list type="bullet">
    ///   <item>"已关闭"是<b>可靠</b>的 —— 关掉之后中心根本不会转发输入；</item>
    ///   <item>"已打开"只说明<b>闸门开着</b>，不代表被控端能收
    ///         （它可能压根没有输入通道，那由 <c>InputState</c> 如实反映）。</item>
    /// </list>
    /// <para>这两种事实在页面上必须是两句不同的话，不能合成一句"已启用"。</para>
    /// </remarks>
    /// <returns>
    /// 回执。<c>Ok=true</c> 表示闸门已经<b>落定</b>在目标状态上
    /// （不是"我们发过了"—— 它完全在中心这一侧，见下面的 remarks）。
    /// <para>
    /// ⚠️ <b>它不再返回会话快照</b>：变更由 <c>SessionChanged</c> 广播给
    /// 所有正在看这一路的人（包括调用者自己）。
    /// 用回执换掉快照，是为了让"会话没了"与"你不在看这一路"能被<b>分开说出来</b> ——
    /// 用同一个 <c>null</c> 说这两件事，页面就只能说一句"失败了"，
    /// 而它们一个要刷新列表、一个要重新打开这一路。
    /// </para>
    /// </returns>
    public Task<RemoteInputAckDto> SetInputEnabled(string sessionId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.FromResult(new RemoteInputAckDto(
                false, RemoteErrorCodes.BadRequest, "请求里没有会话 Id。", 0));
        }

        // 🔥 与 SendInput **同一道归属关卡**（<see cref="RemoteWatchScope"/>）：
        //    翻闸门的动作必须来自"正在看这一路"的连接。
        //
        //    为什么它比 SendInput 那一处更要紧：打开闸门之后是**所有人一起承担后果**
        //    （开关是会话级的），而一个陈旧页面（刷新过 / 切走过 / Leave 丢了）
        //    完全不知道自己打开的是谁的哪一路画面。
        if (!IsWatching(sessionId))
        {
            return Task.FromResult(new RemoteInputAckDto(
                false,
                RemoteErrorCodes.NotWatching,
                "这个连接当前没有在观看这一路会话（可能已经切走或刷新过）。重新打开这一路再操作。",
                0));
        }

        var changed = _sessions.SetInputEnabled(sessionId, enabled);

        if (changed is null)
        {
            return Task.FromResult(new RemoteInputAckDto(
                false,
                RemoteErrorCodes.SessionNotFound,
                $"不存在会话「{sessionId}」（可能已结束）。",
                0));
        }

        _log.Info(
            "Remote",
            $"[Remote] 连接 {Context.ConnectionId} 把会话 {sessionId} 的输入回传设为 {(enabled ? "允许" : "禁止")}。");

        return Task.FromResult(new RemoteInputAckDto(true, null, null, 0));
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // ⚠️ 这里**只记日志，不做任何清理**：分组由 SignalR 自己维护，
        //    再手写一遍"把它从各分组里拿掉"反而会引入一条只在异常路径下才跑的代码。
        //    被控端不会因为"没人看了"而停下来 —— 那是**刻意的**（见下）。
        //
        //    🔥 M5.2 加了"观看登记"（WatchedSessionKey）之后这一点**仍然成立**：
        //    那条登记寄在 Context.Items 上，而 Items 的生存期**就是连接本身** ——
        //    连接没了它跟着没，不需要谁去清（也不需要在这里写一条漏水路径）。
        //    这正是当初选 Items、而不是"中心自己维护一张谁在看哪一路的表"的理由。
        //
        // 🔥 观众全走光之后**被控端继续推流**，这是有意的取舍：
        //    它支撑的是"刷新页面不掉流"。反过来（有人一走就停流）会让
        //    一次 F5 变成"流断了、要重新建会话"，而重连被控端的抓屏是有成本的
        //    （建 D3D 设备、切分辨率）。真正的收摊由会话的**空闲超时**兜底
        //    （见 RemoteSessionRegistry.Sweep，以及观众主动点「结束会话」）。
        if (exception is not null)
        {
            _log.Warn("Remote", $"[Remote] 观看端连接异常断开（{Context.ConnectionId}）：{exception.Message}");
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }
}
