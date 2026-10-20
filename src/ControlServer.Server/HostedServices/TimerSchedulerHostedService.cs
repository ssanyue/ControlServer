using ControlServer.Core.Services;
using ControlServer.Server.Services;

namespace ControlServer.Server.HostedServices;

/// <summary>
/// 定时调度的后台驱动：把 <see cref="TimerSchedulerService.RunAsync"/> 挂到宿主生命周期上。
/// <para>
/// 这一层刻意做得极薄 —— 节拍、回补、幂等、漏触发扫描全在 Core 的服务里，
/// 好处是那些逻辑可以用注入的假时钟在单元测试里精确推进，不必启动宿主。
/// </para>
/// <para>
/// <b>授权闸门（M3）</b>：把"核心能力是否被停摆"以委托形式交给调度循环，
/// 由它<b>每一拍</b>自行判断 —— 这样授权恢复时定时功能能立刻继续，
/// 而不必重启服务，也不必在这里再写一套订阅/起停的编排。
/// </para>
/// </summary>
public sealed class TimerSchedulerHostedService : BackgroundService
{
    private readonly TimerSchedulerService _scheduler;
    private readonly LicenseGateState _gate;
    private readonly LogService _log;

    public TimerSchedulerHostedService(
        TimerSchedulerService scheduler,
        LicenseGateState gate,
        LogService log)
    {
        _scheduler = scheduler;
        _gate = gate;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ① 置初值（先订后置的话，"订阅之前刚好发生了一次变化"会被漏掉）——
        //    注意这里是**先订后读**：订阅回调可能与下面的读同时发生，
        //    但两者写的都是同一个布尔量，最坏结果也只是重复写一次相同的值。
        using var subscription = _gate.Subscribe(blocked => _scheduler.IsCoreBlocked = blocked);
        _scheduler.IsCoreBlocked = _gate.IsBlocked;

        try
        {
            await _scheduler.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机。
        }
        catch (Exception ex)
        {
            // 与 TimeSyncHostedService 同理：BackgroundService 的异常在 .NET 6+ 会
            // 按 HostOptions.BackgroundServiceExceptionBehavior 直接打挂宿主。
            // 定时功能失效只是少一个特性，不该让整个中控服务下线。
            _log.Error("Timer", "定时调度循环异常退出，定时触发功能已停用（服务其余部分正常）。", ex);
        }
    }
}
