using ControlServer.Core.Services;
using ControlServer.Server.Services;

namespace ControlServer.Server.HostedServices;

/// <summary>
/// 远控会话的<b>收尸人</b>（M5.1）—— 每 5 秒扫一遍，把已经断掉的会话从账本上摘掉。
///
/// <para>
/// 🔥 <b>为什么必须有它</b>：中心能看到的一切就是"最后一帧什么时候到的"，
/// 而<b>从来没有过第二帧</b>的那种失败（被控端崩了、网断了、没人登录）在账本上
/// 与"正在推、只是这一帧还没到"长得一模一样。
/// 没有这个清理，页面上会永远挂着一路"正在推流"，而它其实早就死了 ——
/// 更糟的是那台机器<b>再也建不了新会话</b>（账本认为它已经有一路了）。
/// </para>
///
/// <para>
/// <b>为什么是轮询而不是定时器</b>：这里判的是"距上一次事件过了多久"，
/// 每路会话的起点不同。用轮询一遍所有会话，判定逻辑就只有一处
/// （<c>RemoteSessionRegistry.Sweep</c>），而用每会话一个定时器就得处理
/// "事件来了要重排""会话结束了要销毁"这两类容易漏掉的收尾。
/// 会话数量在这一层是个位数，5 秒扫一遍毫无压力。
/// </para>
/// </summary>
public sealed class RemoteSessionSweeperHostedService : BackgroundService
{
    /// <summary>
    /// 节拍。5 秒是"够快"与"够省"之间的折中：空闲判定是 15 秒、建会话超时是 20 秒，
    /// 于是最坏情况下这两类失败会晚 5 秒被发现 —— 而页面上那 5 秒里
    /// 显示的仍然是"连接中"，本来就还没有结论可说。
    /// </summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    private readonly RemoteSessionRegistry _sessions;
    private readonly RemoteSessionService _service;
    private readonly LogService _log;

    public RemoteSessionSweeperHostedService(
        RemoteSessionRegistry sessions,
        RemoteSessionService service,
        LogService log)
    {
        _sessions = sessions;
        _service = service;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 🔥🔥 **判死之后必须告诉那台机器**（2026-10-19）。
                //    原先这一行只有 `_sessions.Sweep(...)`，返回值被丢掉 ——
                //    于是"中心把账本摘了"就到头了，被控端仍然认为自己有一路在推，
                //    从此拒绝这台机器上所有新会话。现场表现为
                //    "黑屏 → 断线 → 连不上"，而中心日志里只有一句"已结束"。
                foreach (var swept in _sessions.Sweep(BeijingTime.Now))
                {
                    // ⚠️ 逐条 await，不并发：一次巡检通常 0～1 条，
                    //    而"给同一台机器同时发两条 stop"没有任何好处。
                    await _service.StopOrphanedAsync(swept.AgentId, swept.SessionId)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // 铁律 #6：后台任务异常必须捕获并记录，绝不允许静默杀死进程。
                _log.Error("Remote", "[Remote] 远控会话巡检出错（下一次节拍继续）", ex);
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

        // 停机时把还在的会话全部收掉：它们所属的长连接马上就要断了，
        // 留着只会让下一次启动的页面上出现一批"上次重启前就死了"的会话。
        // （账本本来就是内存态，这一步严格说只是让日志干净，但日志正是排查的入口。）
        _sessions.CloseAll("中心服务正在停止");
    }
}
