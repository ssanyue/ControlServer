using ControlServer.Core.Services;
using ControlServer.Server.Options;
using ControlServer.Server.Services;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.HostedServices;

/// <summary>
/// 对接码监听宿主服务（UDP，默认 9999）。随宿主启动，随宿主停止。
/// <para>
/// 职责刻意压到最小：<b>启监听 + 把数据报转给 <see cref="PairingDispatchService"/></b>。
/// 匹配与执行的语义都在 Core（可单测），这里只做编排与错误兜底。
/// </para>
/// <para>
/// 启动失败只记录、不抛出 —— 让 Web 界面仍然可用，用户可在页面上看到失败原因。
/// </para>
/// <para>
/// <b>授权闸门（M3）</b>：未授权（<c>Enforce</c> 且设备不可用）时<b>停摆</b>。
/// 这一条不能只靠 HTTP 闸门顶住 —— 对接码是 UDP 直连的，
/// 绕开 Web 界面直接发码照样能触发动作，所以必须在监听这一层关掉。
/// 授权恢复后由闸门事件自动重启，不必重启服务。
/// </para>
/// </summary>
public sealed class PairingListenerHostedService : BackgroundService
{
    private readonly PairingListenerService _listener;
    private readonly PairingDispatchService _dispatcher;
    private readonly LogService _log;
    private readonly IOptions<ServerOptions> _options;
    private readonly LicenseGateState _gate;

    /// <summary>
    /// 起停串行化。闸门回调可能在**任意线程**（比如"导入授权"那个请求线程）上到达，
    /// 而停机回调来自宿主线程 —— 两者同时操作同一个 <c>UdpClient</c> 会打架。
    /// </summary>
    private readonly object _sync = new();

    private IDisposable? _subscription;
    private volatile bool _stopping;

    public PairingListenerHostedService(
        PairingListenerService listener,
        PairingDispatchService dispatcher,
        LogService log,
        IOptions<ServerOptions> options,
        LicenseGateState gate)
    {
        _listener = listener;
        _dispatcher = dispatcher;
        _log = log;
        _options = options;
        _gate = gate;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener.DatagramHandler = OnDatagramAsync;
        _listener.HandlerError = OnHandlerError;

        // 先订后查：反过来的话，"订阅建立之前刚好发生了一次状态变化"会被永久漏掉，
        // 表现为"导入授权了但监听一直没起来"，且没有任何日志。
        _subscription = _gate.Subscribe(OnGateChanged);

        if (_gate.IsBlocked) LogBlocked();
        else StartListener();

        stoppingToken.Register(() => StopEverything());

        return Task.CompletedTask;
    }

    /// <summary>监听循环会 await 本方法 ⇒ 多个对接码依次处理（与旧版一致）。</summary>
    private Task OnDatagramAsync(PairingDatagram datagram, CancellationToken cancellationToken)
        => _dispatcher.DispatchAsync(datagram, cancellationToken);

    /// <summary>单条对接码处理失败：记日志，监听继续（绝不让一包坏数据搞停监听）。</summary>
    private void OnHandlerError(PairingDatagram datagram, Exception exception)
        => _log.Error(
            "Listener",
            $"处理对接码失败（来自 {datagram.RemoteEndpoint}，HEX={datagram.HexText}）—— 监听继续",
            exception);

    /// <summary>授权闸门变化 ⇒ 起停监听。</summary>
    private void OnGateChanged(bool blocked)
    {
        if (_stopping) return;

        try
        {
            if (blocked) StopListener("授权不可用：对接码监听已停止（授权恢复后自动重启）。");
            else StartListener();
        }
        catch (Exception ex)
        {
            // 闸门回调里绝不能抛：它是在"导入授权/刷新状态"的调用栈上被触发的，
            // 抛出去会让一次成功的授权导入表现成失败。铁律 #6。
            _log.Error("Listener", "响应授权状态变化时出错（监听可能未按预期起停）", ex);
        }
    }

    private void StartListener()
    {
        lock (_sync)
        {
            if (_stopping || _listener.IsRunning) return;

            var port = _options.Value.Listener.PairingPort;

            try
            {
                _listener.Start(port);
                _log.Info("Listener", $"对接码监听已启动（UDP {port}），已接入三级匹配（按钮 → 子按钮 → 动作）");
            }
            catch (Exception ex)
            {
                // 端口被占用等：只记录，让 Web 界面照常可用（/readyz 会如实报 not-ready）。
                _log.Error("Listener", $"对接码监听启动失败（UDP {port}）—— 端口可能被占用", ex);
            }
        }
    }

    private void StopListener(string message)
    {
        lock (_sync)
        {
            if (!_listener.IsRunning) return;

            _listener.Stop();
            _log.Info("Listener", message);
        }
    }

    private void LogBlocked()
        => _log.Warn("Listener", "授权不可用：对接码监听未启动（授权恢复后自动启动，无需重启服务）。");

    /// <summary>停机：退订 → 摘回调 → 停监听。放在同一个锁里，保证与闸门回调互斥。</summary>
    private void StopEverything()
    {
        _stopping = true;

        var subscription = _subscription;
        _subscription = null;
        subscription?.Dispose();

        _listener.DatagramHandler = null;
        _listener.HandlerError = null;

        lock (_sync)
        {
            if (_listener.IsRunning)
            {
                _listener.Stop();
                _log.Info("Listener", "对接码监听已停止");
            }
        }
    }
}
