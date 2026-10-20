using ControlServer.Core.Licensing;
using ControlServer.Core.Services;

namespace ControlServer.Server.Services;

/// <summary>
/// 授权闸门的<b>运行时状态</b>：把"这台机器现在该不该被拦"收敛成一个布尔量，
/// 供 HTTP 中间件与两个后台服务共用。
///
/// <para>
/// <b>为什么不直接让各处读 <see cref="LicenseService"/></b>：闸门判定横跨两个字段
/// （<see cref="LicenseService.EffectiveEnforcement"/> + <see cref="LicenseService.IsUsable"/>），
/// 分散在三处各写一遍，迟早会出现"某处漏了一个条件"。这里只说一次，
/// 并且把<b>变化</b>以事件形式广播出去 —— 后台服务据此起停，而不是各自轮询。
/// </para>
///
/// <para>
/// <b>为什么用"变化事件"而不是轮询</b>：轮询要么慢（授权恢复后监听要等下一个周期才回来），
/// 要么费（把周期压到秒级，两个后台任务一直在空转）。事件由
/// <see cref="LicenseService.Refreshed"/> 驱动 —— 也就是"每次真的重读过授权状态之后"，
/// 包括启动引导、60 秒后台刷新、以及导入/试用/移除这些显式动作。
/// </para>
///
/// <para>
/// <b>订阅方必须在启动时自查一次 <see cref="IsBlocked"/></b>：事件只在<b>变化</b>时触发，
/// 若订阅时状态已经是"拦"，它不会再补发一次。
/// </para>
/// </summary>
public sealed class LicenseGateState : IDisposable
{
    private readonly LicenseService _license;
    private readonly LogService _log;
    private readonly Action _onRefreshed;

    /// <summary>0 = 放行，1 = 拦截。<c>volatile</c> 语义由 <see cref="Interlocked"/> 提供。</summary>
    private int _blocked;

    public LicenseGateState(LicenseService license, LogService log)
    {
        _license = license;
        _log = log;

        // 保存委托引用，Dispose 时才能精确退订（lambda 写法会漏掉，订阅就永久泄漏）。
        _onRefreshed = Reconcile;
        _license.Refreshed += _onRefreshed;

        // 先订再算：构造发生在宿主启动早期，此刻的判定结果就是"启动时的真值"。
        Reconcile();
    }

    /// <summary>授权闸门当前是否处于拦截状态（<c>Enforce</c> 且设备不可用）。</summary>
    public bool IsBlocked => Volatile.Read(ref _blocked) == 1;

    /// <summary>拦截状态发生变化时触发；参数为变化后的值。</summary>
    public event Action<bool>? BlockedChanged;

    /// <summary>
    /// 订阅拦截状态变化。返回的 <see cref="IDisposable"/> 用于退订。
    /// </summary>
    public IDisposable Subscribe(Action<bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        BlockedChanged += handler;
        return new Subscription(this, handler);
    }

    /// <summary>
    /// 重新计算并（如有变化）广播。由 <see cref="LicenseService.Refreshed"/> 驱动，
    /// 也可被显式调用（测试、运维）。
    /// </summary>
    public void Reconcile()
    {
        var blocked = ComputeBlocked();
        var current = blocked ? 1 : 0;
        var previous = Interlocked.Exchange(ref _blocked, current);

        if (previous == current) return;

        _log.Warn(
            "License",
            blocked
                ? "授权闸门：已进入拦截状态 —— 动作执行将被拒绝，对接码监听与定时触发停摆。"
                : "授权闸门：已解除拦截 —— 动作执行与后台触发恢复正常。");

        try
        {
            BlockedChanged?.Invoke(blocked);
        }
        catch (Exception ex)
        {
            // 订阅方（起停 UDP 监听这类动作）自身失败绝不能冒泡回 RefreshAsync ——
            // 那会把一次监听启停问题变成"授权状态读取失败"。铁律 #6。
            _log.Warn("License", $"授权闸门状态通知失败：{ex.Message}");
        }
    }

    public void Dispose() => _license.Refreshed -= _onRefreshed;

    /// <summary>
    /// 唯一的判定式。
    /// <list type="bullet">
    /// <item><description><c>Off</c> / <c>Warn</c> ⇒ 永不拦截（Warn 由前端横幅提示）。</description></item>
    /// <item><description><c>Enforce</c> + 设备不可用 ⇒ 拦截。</description></item>
    /// </list>
    /// <para>
    /// 注意 <see cref="LicenseService.EffectiveEnforcement"/> 会把
    /// "配置 <c>Warn</c> 但本机试用记录已存在" 升格为 <c>Enforce</c> ——
    /// 这正是"默认档位不锁死升级、但试用到期真的会拦"的落点，此处无需重复判断。
    /// </para>
    /// </summary>
    private bool ComputeBlocked()
        => _license.EffectiveEnforcement == LicenseEnforcement.Enforce && !_license.IsUsable;

    private sealed class Subscription(LicenseGateState owner, Action<bool> handler) : IDisposable
    {
        private Action<bool>? _handler = handler;

        public void Dispose()
        {
            var handler = Interlocked.Exchange(ref _handler, null);
            if (handler is not null) owner.BlockedChanged -= handler;
        }
    }
}
