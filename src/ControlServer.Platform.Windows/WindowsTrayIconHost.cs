using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// Windows 托盘实现：隐藏顶层窗口 + 专用 STA 消息循环线程 + <c>Shell_NotifyIcon</c>。
/// <para>
/// <b>为什么不用 WinForms 的 <c>NotifyIcon</c></b>（能省 500 行）：它要求
/// <c>TargetFramework=net8.0-windows</c> + <c>UseWindowsForms</c>。而本解决方案的
/// <c>Directory.Build.props</c> 把 TFM 统一为 <c>net8.0</c>，且铁律 #3 规定 <c>Platform.*</c>
/// 必须是 <c>net8.0</c>（<c>net8.0</c> 项目<b>无法引用</b> <c>net8.0-windows</c>，NU1201）。
/// 一旦把本工程改成 <c>net8.0-windows</c>，引用它的 Server 也只能在 Windows 编译
/// ⇒ <b>Linux 交付当场失败</b>。所以这里用 P/Invoke 自研。
/// </para>
/// <para>
/// <b>可用性铁律</b>：只在"用户交互式会话"里工作。Windows 服务位于 <b>Session 0</b>，
/// 那里没有 explorer、没有通知区域 —— 建窗口/加托盘会<b>无声失败</b>。
/// 所以 <see cref="IsAvailable"/> 会同时检查 <see cref="OperatingSystem.IsWindows"/> 与
/// <see cref="Environment.UserInteractive"/>；调用方必须先判它。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrayIconHost : ITrayIconHost
{
    private const int IconId = 1;

    /// <summary>托盘回调消息（鼠标事件从这里来）。</summary>
    private const int CallbackMessage = NativeMethods.WM_APP + 1;

    /// <summary>用来把外部动作投递到托盘线程执行的唤醒消息。</summary>
    private const int ExecuteMessage = NativeMethods.WM_APP + 2;

    private const string WindowClassName = "ControlServer.TrayIcon.HiddenWindow";

    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly TaskCompletionSource<bool> _shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _loopExited = new(false);

    private Thread? _thread;
    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _hIcon = IntPtr.Zero;
    private uint _taskbarCreatedMessage;

    /// <summary>
    /// ⚠️ <b>必须持有这个委托的引用</b>：<c>Marshal.GetFunctionPointerForDelegate</c> 只转换一次，
    /// 若委托被 GC 回收，窗口过程就变成了野指针（进程随机崩溃，且极难定位）。
    /// </summary>
    private NativeMethods.WndProc? _wndProc;

    private volatile TrayIconOptions? _options;
    private string? _unavailableReason;
    private bool _disposed;

    public bool IsAvailable => OperatingSystem.IsWindows() && Environment.UserInteractive;

    public string? UnavailableReason =>
        _unavailableReason ?? (IsAvailable
            ? null
            : "当前平台/会话没有通知区域（Linux，或 Windows 服务所在的 Session 0）。");

    public event Action<string>? CommandInvoked;

    public async Task<bool> ShowAsync(TrayIconOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsAvailable)
        {
            _unavailableReason = UnavailableReason;
            return false;
        }

        _options = options;

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "ControlServer-TrayIcon",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        using var registration = cancellationToken.Register(() => _shown.TrySetResult(false));
        return await _shown.Task.ConfigureAwait(false);
    }

    public Task UpdateAsync(TrayIconOptions options, CancellationToken cancellationToken = default)
    {
        _options = options;
        Post(() => ShellNotify(NativeMethods.NIM_MODIFY));
        return Task.CompletedTask;
    }

    public Task ShowBalloonAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        Post(() =>
        {
            if (_hwnd == IntPtr.Zero) return;

            var data = CreateNotifyData(NativeMethods.NIF_INFO);
            data.szInfo = Truncate(message, 255);
            data.szInfoTitle = Truncate(title, 63);
            data.uTimeoutOrVersion = 10_000;

            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
        });

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            var hwnd = _hwnd;

            // 退出前必须 NIM_DELETE：否则图标会**残留**在通知区域，鼠标划过才消失。
            Post(() =>
            {
                ShellNotify(NativeMethods.NIM_DELETE);
                NativeMethods.DestroyWindow(hwnd);
            });

            // 等消息循环退出，保证上面的清理真的跑完了才继续释放 HICON。
            await Task.Run(() => _loopExited.Wait(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
            _hwnd = IntPtr.Zero;
        }

        if (_hIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        CommandInvoked = null;
    }

    // ── 托盘线程 ────────────────────────────────────────────────

    private void MessageLoop()
    {
        try
        {
            var hInstance = NativeMethods.GetModuleHandleW(null);

            _wndProc = WindowProc;   // ⚠️ 见字段注释：必须持有引用

            var windowClass = new NativeMethods.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = hInstance,
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = WindowClassName,
                hIconSm = IntPtr.Zero,
            };

            if (NativeMethods.RegisterClassExW(ref windowClass) == 0)
            {
                Fail($"注册窗口类失败（Win32 错误码 {Marshal.GetLastWin32Error()}）。");
                return;
            }

            _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

            // 必须是**顶层隐藏窗口**（dwStyle = 0，不带 WS_VISIBLE），不能是 HWND_MESSAGE 消息窗口：
            // TaskbarCreated 是通过 HWND_BROADCAST 广播的，而消息窗口**收不到广播**。
            _hwnd = NativeMethods.CreateWindowExW(
                dwExStyle: 0,
                lpClassName: WindowClassName,
                lpWindowName: "ControlServer.TrayIcon",
                dwStyle: 0,
                x: 0, y: 0, nWidth: 0, nHeight: 0,
                hWndParent: IntPtr.Zero,
                hMenu: IntPtr.Zero,
                hInstance: hInstance,
                lpParam: IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                Fail($"创建托盘隐藏窗口失败（Win32 错误码 {Marshal.GetLastWin32Error()}）。");
                return;
            }

            _hIcon = IconLoader.Load(NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON));

            if (!ShellNotify(NativeMethods.NIM_ADD))
            {
                var code = Marshal.GetLastWin32Error();
                NativeMethods.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
                Fail($"添加托盘图标失败（Win32 错误码 {code}）。");
                return;
            }

            _shown.TrySetResult(true);

            while (true)
            {
                var result = NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0);
                if (result <= 0) break;   // 0 = WM_QUIT；-1 = 出错
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            Fail($"托盘消息循环异常：{ex.Message}");
        }
        finally
        {
            _shown.TrySetResult(false);   // 已置 true 时是 no-op
            _loopExited.Set();
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == (uint)CallbackMessage)
            {
                // 未调用 NIM_SETVERSION(4) 时，lParam 的低字就是鼠标消息（经典语义，最稳）。
                var mouseEvent = (int)(lParam.ToInt64() & 0xFFFF);
                switch (mouseEvent)
                {
                    case NativeMethods.WM_RBUTTONUP:
                    case NativeMethods.WM_CONTEXTMENU:
                        ShowContextMenu();
                        return IntPtr.Zero;

                    case NativeMethods.WM_LBUTTONDBLCLK:
                        InvokeDefaultCommand();
                        return IntPtr.Zero;
                }

                return IntPtr.Zero;
            }

            if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
            {
                // explorer 崩溃/重启后通知区域会被重建，原来的图标随之失效 ——
                // 不重新 NIM_ADD 的话，图标会**永久消失**，只能重启应用。
                ShellNotify(NativeMethods.NIM_ADD);
                return IntPtr.Zero;
            }

            if (msg == (uint)ExecuteMessage)
            {
                while (_pending.TryDequeue(out var action))
                {
                    try
                    {
                        action();
                    }
                    catch
                    {
                        // 单个动作失败不能拖垮消息循环。
                    }
                }

                return IntPtr.Zero;
            }

            if (msg == (uint)NativeMethods.WM_CLOSE)
            {
                // 外部关闭请求：`taskkill`（不带 /F）会给进程的顶层窗口发 WM_CLOSE。
                //
                // ⚠️ 这里**不能**交给 DefWindowProc —— 那会只销毁托盘窗口，进程变成一个
                //    "没有任何控制面的孤儿"（WinExe 下没有控制台、也没有托盘，只能去任务管理器杀）。
                // 正确做法：把它翻译成"退出"命令，走上层的**优雅停机**
                // （Serilog flush → SQLite 收摊 → NIM_DELETE → 退出码 0）。
                RaiseCommand(TrayCommands.Exit);
                return IntPtr.Zero;
            }

            if (msg == (uint)NativeMethods.WM_DESTROY)
            {
                // 窗口真正被销毁时（正常停机路径）也要摘掉图标。
                // 此刻 HWND 仍然有效（WM_DESTROY 之后才失效），正是 NIM_DELETE 的正确时机。
                ShellNotify(NativeMethods.NIM_DELETE);

                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
            }
        }
        catch
        {
            // WndProc 里漏异常会顺着消息循环炸掉整条线程 —— 一律吞掉并交回默认处理。
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var options = _options;
        if (options is null) return;

        var hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            var commandIds = new List<string>();

            foreach (var item in options.Menu)
            {
                if (item.IsSeparator)
                {
                    NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
                    continue;
                }

                commandIds.Add(item.Id!);

                var flags = NativeMethods.MF_STRING;
                if (!item.Enabled) flags |= NativeMethods.MF_GRAYED;
                if (item.IsDefault) flags |= NativeMethods.MF_DEFAULT;

                // 菜单 id 从 1 开始（0 会被 TrackPopupMenuEx 当作"没选中"）。
                NativeMethods.AppendMenuW(hMenu, (uint)flags, (UIntPtr)(uint)commandIds.Count, item.Text);
            }

            if (!NativeMethods.GetCursorPos(out var pt)) return;

            // ⚠️ 微软文档明确要求这个组合，否则菜单会出现"点了不消失/点了没反应"：
            //    1) TrackPopupMenu 之前 SetForegroundWindow；
            //    2) 之后向本窗口 PostMessage(WM_NULL)。
            NativeMethods.SetForegroundWindow(_hwnd);

            var selected = NativeMethods.TrackPopupMenuEx(
                hMenu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY,
                pt.X, pt.Y, _hwnd, IntPtr.Zero);

            NativeMethods.PostMessageW(_hwnd, (uint)NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (selected > 0 && selected <= commandIds.Count)
            {
                RaiseCommand(commandIds[selected - 1]);
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu);
        }
    }

    private void InvokeDefaultCommand()
    {
        var defaultId = _options?.DefaultCommandId;
        if (!string.IsNullOrEmpty(defaultId)) RaiseCommand(defaultId!);
    }

    private void RaiseCommand(string commandId)
    {
        var handler = CommandInvoked;
        if (handler is null) return;

        // 投递到线程池，而**不是**在托盘线程上同步调用：
        // 处理器很可能马上又要调托盘（UpdateAsync 会 PostMessage 回托盘线程），
        // 在托盘线程上同步等待就会自锁。
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                handler(commandId);
            }
            catch
            {
                // 处理器自己的异常不该影响托盘。
            }
        });
    }

    // ── 小工具 ─────────────────────────────────────────────────

    private void Post(Action action)
    {
        var hwnd = _hwnd;
        if (hwnd == IntPtr.Zero) return;

        _pending.Enqueue(action);
        NativeMethods.PostMessageW(hwnd, (uint)ExecuteMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private bool ShellNotify(int message)
    {
        if (_hwnd == IntPtr.Zero) return false;

        var data = CreateNotifyData(
            NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP);

        return NativeMethods.Shell_NotifyIconW(message, ref data);
    }

    private NativeMethods.NOTIFYICONDATA CreateNotifyData(int flags) => new()
    {
        // ⚠️ cbSize 必须按当前结构布局计算；写死会因位数/版本差异导致**静默失败**。
        cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _hIcon,
        szTip = Truncate(_options?.ToolTip, 127),
        dwState = 0,
        dwStateMask = 0,
        szInfo = string.Empty,
        uTimeoutOrVersion = 0,
        szInfoTitle = string.Empty,
        dwInfoFlags = 0,
        guidItem = Guid.Empty,
        hBalloonIcon = IntPtr.Zero,
    };

    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxChars ? value : value[..maxChars];
    }

    private void Fail(string reason)
    {
        _unavailableReason = reason;
        _shown.TrySetResult(false);
    }
}
