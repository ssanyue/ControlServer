using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ControlServer.Platform.Windows;

/// <summary>服务当前状态（只取我们关心的几种）。</summary>
public enum WindowsServiceState
{
    /// <summary>服务未安装（或查询不到）。</summary>
    NotInstalled = 0,

    Stopped,
    StartPending,
    StopPending,
    Running,
    Paused,
    Unknown,
}

/// <summary>服务状态快照。</summary>
public sealed record WindowsServiceStatus(
    WindowsServiceState State,
    uint ProcessId,
    int Win32ExitCode,
    string? ErrorMessage = null)
{
    public bool IsRunning => State == WindowsServiceState.Running;

    public bool IsPending => State is WindowsServiceState.StartPending or WindowsServiceState.StopPending;

    /// <summary>给用户看的中文描述。</summary>
    public string Describe() => State switch
    {
        WindowsServiceState.Running => "运行中",
        WindowsServiceState.Stopped => "已停止",
        WindowsServiceState.StartPending => "正在启动…",
        WindowsServiceState.StopPending => "正在停止…",
        WindowsServiceState.NotInstalled => "未安装为服务",
        WindowsServiceState.Paused => "已暂停",
        _ => "状态未知",
    };
}

/// <summary>
/// 通过 <b>SCM</b>（服务控制管理器）查询与控制 Windows 服务。
/// <para>
/// <b>为什么用 SCM 而不是自研 IPC</b>（方案文档 §11.3 的 D1）：托盘助手需要"停/启服务"，
/// 而 Windows 自带的服务控制管理器就是干这个的 —— 零自研协议、零凭据托管，
/// 只要安装时用 <c>sc.exe sdset</c> 把 <c>SERVICE_START/STOP/QUERY_STATUS</c> 授权给交互用户，
/// 普通用户即可操作，无需提权。
/// </para>
/// <para>
/// <b>为什么不抽 <c>IServiceController</c> 接口</b>：目前唯一的使用者
/// <c>ControlServer.TrayAgent</c> 本身就是 Windows 专属（托盘 + SCM 都只在 Windows 存在）。
/// 为一个消费者造抽象属于 YAGNI；真到 Linux 要做 systemd 托盘时再抽不迟。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceController
{
    // ── SCM 访问权限 ────────────────────────────────────────────
    private const uint SC_MANAGER_CONNECT = 0x0001;

    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_STOP = 0x0020;

    // ── 控制码 / 状态 ───────────────────────────────────────────
    private const uint SERVICE_CONTROL_STOP = 0x00000001;

    private const uint SERVICE_STOPPED = 0x00000001;
    private const uint SERVICE_START_PENDING = 0x00000002;
    private const uint SERVICE_STOP_PENDING = 0x00000003;
    private const uint SERVICE_RUNNING = 0x00000004;
    private const uint SERVICE_PAUSED = 0x00000007;

    private const int SC_STATUS_PROCESS_INFO = 0;

    /// <summary>ERROR_SERVICE_DOES_NOT_EXIST —— 服务没装。</summary>
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;

    private const int ERROR_ACCESS_DENIED = 5;

    private readonly string _serviceName;

    public WindowsServiceController(string serviceName)
    {
        _serviceName = serviceName;
    }

    public string ServiceName => _serviceName;

    /// <summary>查询当前状态。<b>永不抛异常</b>（SCM 打不开时返回 Unknown + 原因）。</summary>
    public WindowsServiceStatus QueryStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsServiceStatus(WindowsServiceState.Unknown, 0, 0, "非 Windows 平台。");
        }

        var manager = NativeMethods.OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (manager == IntPtr.Zero)
        {
            return new WindowsServiceStatus(
                WindowsServiceState.Unknown, 0, 0,
                $"无法连接服务控制管理器（Win32 {Marshal.GetLastWin32Error()}）。");
        }

        try
        {
            var service = NativeMethods.OpenServiceW(manager, _serviceName, SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
            {
                var code = Marshal.GetLastWin32Error();

                return code == ERROR_SERVICE_DOES_NOT_EXIST
                    ? new WindowsServiceStatus(WindowsServiceState.NotInstalled, 0, 0)
                    : new WindowsServiceStatus(
                        WindowsServiceState.Unknown, 0, 0,
                        $"打开服务「{_serviceName}」失败（Win32 {code}）。");
            }

            try
            {
                var bufferSize = Marshal.SizeOf<NativeMethods.SERVICE_STATUS_PROCESS>();
                var buffer = Marshal.AllocHGlobal(bufferSize);

                try
                {
                    if (!NativeMethods.QueryServiceStatusEx(
                            service, SC_STATUS_PROCESS_INFO, buffer, (uint)bufferSize, out _))
                    {
                        return new WindowsServiceStatus(
                            WindowsServiceState.Unknown, 0, 0,
                            $"查询服务状态失败（Win32 {Marshal.GetLastWin32Error()}）。");
                    }

                    var status = Marshal.PtrToStructure<NativeMethods.SERVICE_STATUS_PROCESS>(buffer);

                    return new WindowsServiceStatus(
                        MapState(status.dwCurrentState),
                        status.dwProcessId,
                        (int)status.dwWin32ExitCode);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                NativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(manager);
        }
    }

    /// <summary>启动服务。成功返回 null，失败返回可读原因。</summary>
    public string? Start()
    {
        return WithService(SERVICE_START | SERVICE_QUERY_STATUS, hService =>
        {
            if (NativeMethods.StartServiceW(hService, 0, IntPtr.Zero)) return null;

            var code = Marshal.GetLastWin32Error();

            // 1056 ERROR_SERVICE_ALREADY_RUNNING
            return code == 1056 ? null : $"启动服务失败（Win32 {code}{DescribeCode(code)}）。";
        });
    }

    /// <summary>停止服务。成功返回 null，失败返回可读原因。</summary>
    public string? Stop()
    {
        return WithService(SERVICE_STOP | SERVICE_QUERY_STATUS, hService =>
        {
            var status = new NativeMethods.SERVICE_STATUS();

            if (NativeMethods.ControlService(hService, SERVICE_CONTROL_STOP, ref status)) return null;

            var code = Marshal.GetLastWin32Error();

            // 1062 ERROR_SERVICE_NOT_ACTIVE
            return code == 1062 ? null : $"停止服务失败（Win32 {code}{DescribeCode(code)}）。";
        });
    }

    /// <summary>
    /// 打开服务句柄并执行动作（自动关句柄）。SCM/服务打不开时返回可读原因。
    /// </summary>
    private string? WithService(uint access, Func<IntPtr, string?> action)
    {
        if (!OperatingSystem.IsWindows()) return "非 Windows 平台。";

        var manager = NativeMethods.OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (manager == IntPtr.Zero)
        {
            return $"无法连接服务控制管理器（Win32 {Marshal.GetLastWin32Error()}）。";
        }

        try
        {
            var service = NativeMethods.OpenServiceW(manager, _serviceName, access);
            if (service == IntPtr.Zero)
            {
                var code = Marshal.GetLastWin32Error();

                return code switch
                {
                    ERROR_SERVICE_DOES_NOT_EXIST => $"服务「{_serviceName}」尚未安装。",
                    ERROR_ACCESS_DENIED =>
                        "访问被拒绝：当前账号没有操作该服务的权限。"
                        + "安装脚本应执行 `sc sdset` 把 SERVICE_START/STOP 授权给交互用户（见方案 §11.3）。",
                    _ => $"打开服务「{_serviceName}」失败（Win32 {code}）。",
                };
            }

            try
            {
                return action(service);
            }
            finally
            {
                NativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(manager);
        }
    }

    private static WindowsServiceState MapState(uint state) => state switch
    {
        SERVICE_STOPPED => WindowsServiceState.Stopped,
        SERVICE_START_PENDING => WindowsServiceState.StartPending,
        SERVICE_STOP_PENDING => WindowsServiceState.StopPending,
        SERVICE_RUNNING => WindowsServiceState.Running,
        SERVICE_PAUSED => WindowsServiceState.Paused,
        _ => WindowsServiceState.Unknown,
    };

    private static string DescribeCode(int code) => code switch
    {
        5 => "：访问被拒绝（需要安装时授权）",
        1055 => "：服务已被禁用",
        1058 => "：服务已被禁用",
        1060 => "：服务不存在",
        1062 => "：服务当前未运行",
        1069 => "：服务因登录失败无法启动",
        _ => string.Empty,
    };
}
