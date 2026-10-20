using System.Runtime.InteropServices;

namespace ControlServer.Platform.Windows;

/// <summary>
/// 托盘与"交互式宿主"所需的 Win32 声明集中在此。
/// <para>
/// 集中放置的另一个目的是<b>可单测</b>：<see cref="NOTIFYICONDATA"/> 的布局/大小是托盘最经典的
/// "无声失败"来源（<c>cbSize</c> 填错 ⇒ <c>Shell_NotifyIcon</c> 返回 false，但没有任何提示），
/// 所以结构体与常量都在这里暴露给测试项目断言（<c>InternalsVisibleTo</c>）。
/// </para>
/// </summary>
internal static class NativeMethods
{
    // ── 窗口消息 ────────────────────────────────────────────────
    internal const int WM_APP = 0x8000;
    internal const int WM_NULL = 0x0000;
    internal const int WM_DESTROY = 0x0002;
    internal const int WM_CLOSE = 0x0010;
    internal const int WM_CONTEXTMENU = 0x007B;

    internal const int WM_LBUTTONDOWN = 0x0201;
    internal const int WM_LBUTTONUP = 0x0202;
    internal const int WM_LBUTTONDBLCLK = 0x0203;
    internal const int WM_RBUTTONDOWN = 0x0204;
    internal const int WM_RBUTTONUP = 0x0205;

    // ── Shell_NotifyIcon ───────────────────────────────────────
    internal const int NIM_ADD = 0x00000000;
    internal const int NIM_MODIFY = 0x00000001;
    internal const int NIM_DELETE = 0x00000002;
    internal const int NIM_SETVERSION = 0x00000004;

    internal const int NIF_MESSAGE = 0x00000001;
    internal const int NIF_ICON = 0x00000002;
    internal const int NIF_TIP = 0x00000004;
    internal const int NIF_INFO = 0x00000010;

    // ── 菜单 ───────────────────────────────────────────────────
    internal const int MF_STRING = 0x00000000;
    internal const int MF_SEPARATOR = 0x00000800;
    internal const int MF_GRAYED = 0x00000001;
    internal const int MF_DISABLED = 0x00000002;
    internal const int MF_DEFAULT = 0x00001000;

    internal const int TPM_RIGHTBUTTON = 0x0002;
    internal const int TPM_NONOTIFY = 0x0080;
    internal const int TPM_RETURNCMD = 0x0100;

    // ── 其它 ───────────────────────────────────────────────────
    internal const int SM_CXSMICON = 49;
    internal const int SM_CYSMICON = 50;

    /// <summary>图标版本号：0x00030000 = 3.0（Vista+ 格式，CreateIconFromResourceEx 用它）。</summary>
    internal const uint ICON_VERSION_3 = 0x00030000;

    internal const int MB_OK = 0x00000000;
    internal const int MB_ICONERROR = 0x00000010;
    internal const int MB_ICONINFORMATION = 0x00000040;
    internal const int MB_SETFOREGROUND = 0x00010000;
    internal const int MB_TOPMOST = 0x00040000;

    internal const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    internal const int ERROR_ALREADY_EXISTS = 183;

    internal const int IDI_APPLICATION = 32512;

    // ── 结构体 ─────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        internal IntPtr hwnd;
        internal uint message;
        internal IntPtr wParam;
        internal IntPtr lParam;
        internal uint time;
        internal POINT pt;
    }

    /// <summary>
    /// <c>NOTIFYICONDATAW</c>（Vista 之后的完整布局，含 <c>guidItem</c> / <c>hBalloonIcon</c>）。
    /// <para>
    /// <b>字段顺序必须与 winuser.h 完全一致</b>；<c>uTimeout</c> 与 <c>uVersion</c> 在 C 里是 union，
    /// 这里只保留一个 int（两者同为 4 字节，位置相同）。
    /// </para>
    /// <para>
    /// 期望大小：<b>x64 = 976、x86 = 956</b>（即 <c>NOTIFYICONDATA_V3_SIZE</c>）。
    /// <c>cbSize</c> 必须填 <see cref="Marshal.SizeOf{T}()"/> 的结果，写死会导致静默失败。
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        internal int cbSize;
        internal IntPtr hWnd;
        internal int uID;
        internal int uFlags;
        internal int uCallbackMessage;
        internal IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string szTip;

        internal int dwState;
        internal int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string szInfo;

        /// <summary>union { uTimeout; uVersion; }</summary>
        internal int uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        internal string szInfoTitle;

        internal int dwInfoFlags;
        internal Guid guidItem;
        internal IntPtr hBalloonIcon;
    }

    internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        internal int cbSize;
        internal int style;
        internal IntPtr lpfnWndProc;
        internal int cbClsExtra;
        internal int cbWndExtra;
        internal IntPtr hInstance;
        internal IntPtr hIcon;
        internal IntPtr hCursor;
        internal IntPtr hbrBackground;
        internal string? lpszMenuName;
        internal string lpszClassName;
        internal IntPtr hIconSm;
    }

    /// <summary><c>.ico</c> 文件头（ICONDIR，6 字节）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct ICONDIR
    {
        internal ushort idReserved;
        internal ushort idType;
        internal ushort idCount;
    }

    /// <summary>单个图标目录项（ICONDIRENTRY，16 字节）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct ICONDIRENTRY
    {
        internal byte bWidth;          // 0 表示 256
        internal byte bHeight;         // 0 表示 256
        internal byte bColorCount;
        internal byte bReserved;
        internal ushort wPlanes;
        internal ushort wBitCount;
        internal uint dwBytesInRes;
        internal uint dwImageOffset;
    }

    // ── user32 ─────────────────────────────────────────────────

    // ⚠️ Shell_NotifyIconW 在 **shell32.dll**，不在 user32.dll —— 写到 user32 上是
    //    EntryPointNotFoundException（"Unable to find an entry point ... in DLL 'user32.dll'"），
    //    而且只有真的跑起来才会发现（编译期完全正常）。
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int TrackPopupMenuEx(
        IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreateIconFromResourceEx(
        byte[] presbits, uint dwResSize, [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        uint dwVer, int cxDesired, int cyDesired, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    // ── kernel32 ───────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandleW(string? lpModuleName);

    internal const int STD_OUTPUT_HANDLE = -11;
    internal const int STD_ERROR_HANDLE = -12;

    /// <summary>句柄类型未知（无效句柄 / 无控制台）。</summary>
    internal const uint FILE_TYPE_UNKNOWN = 0x0000;

    /// <summary>字符设备（真正的控制台）。</summary>
    internal const uint FILE_TYPE_CHAR = 0x0002;

    /// <summary>磁盘文件（被重定向到文件）。</summary>
    internal const uint FILE_TYPE_DISK = 0x0001;

    /// <summary>管道（被重定向到管道，例如父进程用 Popen 抓输出）。</summary>
    internal const uint FILE_TYPE_PIPE = 0x0003;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetFileType(IntPtr hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FreeConsole();

    /// <summary>
    /// 取当前进程所属"控制台窗口"的句柄；**没有控制台时返回 <see cref="IntPtr.Zero"/>**。
    /// <para>
    /// 用途：判定"我现在到底挂在某个控制台上吗"。
    /// 注意它与 <see cref="AttachConsole"/> 的返回值<b>不是</b>一回事 ——
    /// 从 cmd 里启动时本进程<b>已经</b>继承了那个控制台，此时 <c>AttachConsole</c> 会
    /// 因"已有控制台"而失败（ERROR_ACCESS_DENIED），但控制台其实<b>是可用的</b>。
    /// 所以"输出能不能到控制台"必须用本函数判定。
    /// </para>
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateMutexW(IntPtr lpMutexAttributes, [MarshalAs(UnmanagedType.Bool)] bool bInitialOwner, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    // ── advapi32：服务控制管理器（SCM） ────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_STATUS
    {
        internal uint dwServiceType;
        internal uint dwCurrentState;
        internal uint dwControlsAccepted;
        internal uint dwWin32ExitCode;
        internal uint dwServiceSpecificExitCode;
        internal uint dwCheckPoint;
        internal uint dwWaitHint;
    }

    /// <summary>
    /// <c>SERVICE_STATUS_PROCESS</c>：比 <see cref="SERVICE_STATUS"/> 多出
    /// <c>dwProcessId</c> 与 <c>dwServiceFlags</c>，共 9 个 DWORD = 36 字节。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_STATUS_PROCESS
    {
        internal uint dwServiceType;
        internal uint dwCurrentState;
        internal uint dwControlsAccepted;
        internal uint dwWin32ExitCode;
        internal uint dwServiceSpecificExitCode;
        internal uint dwCheckPoint;
        internal uint dwWaitHint;
        internal uint dwProcessId;
        internal uint dwServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceStatusEx(
        IntPtr hService, int infoLevel, IntPtr lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartServiceW(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseServiceHandle(IntPtr hSCObject);

    // ── 会话（M4.5：找到"用户桌面在哪个会话"）────────────────────
    //
    // ⚠️ 必须从 kernel32 取而不是从 wtsapi32：本项目只用到这一个函数，
    //    为它多引一个 DLL 不值得（`WTSGetActiveConsoleSessionId` 就住在 kernel32 里）。
    //    返回 0xFFFFFFFF 表示**当前没有控制台会话**（无人登录）。
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WTSGetActiveConsoleSessionId();
}
