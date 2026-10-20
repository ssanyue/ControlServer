using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using ControlServer.Server.Options;
using ControlServer.Server.Services;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.HostedServices;

/// <summary>
/// 客户端在线状态的**唯一裁判**（M4.1）。
/// <para>
/// 每 5 秒做一遍四件事：
/// </para>
/// <list type="number">
///   <item>按"最后一次心跳距今多久"算出每台机器的状态；</item>
///   <item>与上一次广播出去的状态比对，<b>只推跃迁</b>（在线 ↔ 离线），不推每一跳；</item>
///   <item>比对各机报上来的 <b>UDP 指令通道状态</b>，变了就立刻推一次（2026-10-14）
///         —— 它是"刚点的指令为什么不生效"的判据，不能等每 30 秒的兜底刷新；</item>
///   <item>把内存里的心跳成批落盘（60 秒一次）。</item>
/// </list>
/// <para>
/// <b>为什么不靠 SignalR 的 OnDisconnected 判离线</b>：进程被正常结束时会触发它，
/// 但<b>硬断电、拔网线、蓝屏都不会</b> —— 那些情况下唯一的线索就是"心跳停了"。
/// 两条路都要走：断开事件让状态立刻准确，心跳超时兜住所有没来得及告别的场景。
/// </para>
/// <para>
/// <b>本服务只记录、不判定业务</b>：状态的算法本体在
/// <see cref="AgentPresence.Classify"/>（纯函数，已被单测钉住），这里只负责编排与广播。
/// </para>
/// </summary>
public sealed class AgentPresenceHostedService : BackgroundService
{
    /// <summary>判定节拍。5 秒是"够快"与"够省"之间的折中：离线最多晚 5 秒被发现。</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    /// <summary>心跳落盘间隔。心跳很密、而"最后在线时间"精度要求不高，成批写省下大量 SQLite 写。</summary>
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(60);

    /// <summary>主机列表兜底刷新（管理员在别处改了名字 / 别的实例写了库）。</summary>
    private static readonly TimeSpan SnapshotRefresh = TimeSpan.FromSeconds(60);

    /// <summary>
    /// "心跳广播"间隔：即使没有任何状态跃迁，也定期推一次 AgentChanged。
    /// <para>目的是让页面上的"在线 2 小时 13 分"这类相对时间自己走起来 ——
    /// 否则它会停在上一次事件的时刻，看起来像页面卡住了。</para>
    /// </summary>
    private static readonly TimeSpan IdleBroadcast = TimeSpan.FromSeconds(30);

    private readonly AgentHostRegistry _registry;
    private readonly IAgentHostStore _store;
    private readonly INotifier _notifier;
    private readonly LogService _log;
    private readonly IOptions<ServerOptions> _options;

    private bool _seeded;
    private DateTimeOffset _lastPersist = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastIdle = DateTimeOffset.MinValue;

    public AgentPresenceHostedService(
        AgentHostRegistry registry,
        IAgentHostStore store,
        INotifier notifier,
        LogService log,
        IOptions<ServerOptions> options)
    {
        _registry = registry;
        _store = store;
        _notifier = notifier;
        _log = log;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 先把库里的历史载进快照，否则"重启后页面一片空白"会持续到第一次刷新为止。
        await _registry.RefreshAsync(stoppingToken).ConfigureAwait(false);

        var first = _registry.Hosts.Count;

        _log.Info(
            "Agent",
            first == 0
                // ⚠️ 免凭据准入（2026-10-13）后，"被发现"与"被接纳"已经是同一件事：
                //    同网段任何装了客户端的机器都能直接注册上线，不再需要令牌/加入码。
                //    所以空表只剩两种解释 —— ① 真没装客户端；② 装了但不在同一网段
                //    （或 UDP 9998 / HTTP 8899 被防火墙拦住了）。
                ? "客户端在线表：暂无注册记录。客户端装好后会自动发现本服务端并直接注册"
                  + "（无需任何令牌/加入码）；若长时间为空，请确认客户机已安装、且与本服务端在同一网段、"
                  + "防火墙放行了 UDP 9998 与 HTTP 8899。"
                : $"客户端在线表：已载入 {first} 台注册记录。");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 铁律 #6：后台任务异常必须捕获并记录，绝不允许静默杀死进程。
                _log.Error("Agent", "客户端在线状态巡检出错（下一次节拍继续）", ex);
            }

            try
            {
                await Task.Delay(Tick, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // 停机前把内存里的心跳落盘一次：否则正常重启会让"最后在线时间"倒退最多 60 秒。
        await FlushAsync(force: true).ConfigureAwait(false);
    }

    private async Task TickOnceAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value.Agent;
        var now = BeijingTime.Now;

        var hosts = _registry.Hosts;
        var dtos = hosts.Select(h => _registry.ToDto(h, options, now)).ToList();

        // ⚠️ 两处都先读 `_seeded` 再置位：UDP 比对与服务状态比对用的是**同一档**的"首轮播种"语义。
        var seedOnly = !_seeded;

        var transitions = _registry.DiffStates(dtos, seedOnly);
        var udpChanged = _registry.DiffUdpStates(dtos, seedOnly);

        _seeded = true;

        foreach (var transition in transitions)
        {
            await BroadcastAsync(transition).ConfigureAwait(false);
        }

        // ── UDP 通道状态变了就立刻推一次（2026-10-14）──
        //
        // 为什么要单列：列表平时只在"跃迁"或每 30 秒兜底时才刷，而这条通道的状态
        // 正是"我刚点的指令为什么不生效"的判据 —— 让它滞后 30 秒，等于让人继续
        // 拿过期的真相做判断（需求原文："服务端与客户端的监听状态需要能够实时同步"）。
        // 这里只推"任意字段变化"那条事件（页面据此重绘，不弹提示）。
        foreach (var host in udpChanged)
        {
            _log.Info(
                "Agent",
                $"客户端 {host.DisplayName} 的 UDP 指令通道状态变化："
                + (host.UdpChannel?.Listening == true
                    ? $"正在监听 {host.UdpChannel.Port}"
                    : host.UdpChannel?.StateText ?? "未知"));

            await _notifier.AgentChangedAsync(host, cancellationToken).ConfigureAwait(false);
        }

        if (now - _lastRefresh >= SnapshotRefresh)
        {
            _lastRefresh = now;
            await _registry.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (now - _lastPersist >= PersistInterval)
        {
            _lastPersist = now;
            await FlushAsync(force: false).ConfigureAwait(false);
        }

        if (transitions.Count == 0 && now - _lastIdle >= IdleBroadcast && dtos.Count > 0)
        {
            _lastIdle = now;

            // 只推"列表已刷新"这一个信号，不带跃迁语义 —— 前端据此重绘，不会弹提示。
            await _notifier.AgentChangedAsync(dtos[0], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task BroadcastAsync(AgentHostRegistry.StateTransition transition)
    {
        var host = transition.Host;

        if (host.State == AgentStates.Online && transition.Previous is not (AgentStates.Online or AgentStates.Unstable))
        {
            _log.Info("Agent", $"客户端上线：{host.DisplayName}（{host.MachineKey}，{host.LastAddress ?? "未知地址"}）");
            await _notifier.AgentOnlineAsync(host).ConfigureAwait(false);
        }
        else if (host.State == AgentStates.Offline && transition.Previous is AgentStates.Online or AgentStates.Unstable)
        {
            _log.Info("Agent", $"客户端离线：{host.DisplayName}（{host.MachineKey}）");
            await _notifier.AgentOfflineAsync(host).ConfigureAwait(false);
        }

        await _notifier.AgentChangedAsync(host).ConfigureAwait(false);
    }

    private async Task FlushAsync(bool force)
    {
        var pending = _registry.TakePendingTouches(BeijingTime.Now, PersistInterval, force);

        foreach (var touch in pending)
        {
            try
            {
                await _store
                    .TouchAsync(touch.Id, touch.LastSeenAt, touch.Address, touch.AgentVersion)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 落盘失败不致命（内存态还在，下一轮会重试）。但必须留痕 ——
                // "最后在线时间永远停在某个时刻"这类问题只有日志能解释。
                _log.Error("Agent", $"客户端心跳落盘失败（主机 Id={touch.Id}）", ex);
            }
        }
    }
}
