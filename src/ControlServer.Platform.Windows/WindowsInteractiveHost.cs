using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ControlServer.Platform.Windows;

/// <summary>
/// "交互式宿主"相关的 Win32 行为：附着父控制台、弹窗兜底、单实例互斥。
/// <para>
/// 之所以集中在这里：<c>Program.cs</c> 是<b>组合根</b>，不该出现 P/Invoke；
/// 而这三个能力都只在 Windows 有意义，正好落在本工程（铁律 #2）。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsInteractiveHost
{
    /// <summary>
    /// 标准输出是否已经被"上级"接管成 <b>文件或管道</b>（而不是控制台）。
    /// <para>
    /// <b>为什么需要它</b>：<see cref="TryAttachParentConsole"/> 一旦附着成功就会把
    /// <c>Console.Out</c> 重新绑到<b>那个控制台</b>。而冒烟脚本
    /// （<c>deploy/smoke/run-all.py</c>）是 <c>Popen(stdout=log)</c> —— 它<b>故意</b>把输出收进日志文件。
    /// 如果我们不辨青红皂白地附着，日志就会被"抢"到终端上，文件里什么都没了。
    /// </para>
    /// <para>
    /// 判据用 <c>GetFileType</c> 而不是 <c>Console.IsOutputRedirected</c>：
    /// 后者在"GUI 子系统 + 完全没有句柄"（双击启动）时也返回 true，与"重定向到文件"无法区分。
    /// </para>
    /// </summary>
    public static bool OutputIsCapturedToFileOrPipe()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var type = NativeMethods.GetFileType(NativeMethods.GetStdHandle(NativeMethods.STD_OUTPUT_HANDLE));
            return type is NativeMethods.FILE_TYPE_DISK or NativeMethods.FILE_TYPE_PIPE;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 决定本进程的"控制台归属"，并返回 <b>控制台输出此刻是否可用</b>
    /// （调用方据此决定要不要挂 Serilog 的 Console sink）。
    /// <para>
    /// <b>为什么需要它（这是"关闭 CMD 窗口不能停止服务"的唯一实现方式）</b>：
    /// 控制台中启动的进程会<b>继承</b>那个控制台。Win32 的规则是 —— 控制台窗口被关闭时，
    /// 系统向<b>所有</b>客户端进程发 <c>CTRL_CLOSE_EVENT</c>，且<b>应用无法否决</b>
    /// （处理器返回 TRUE 也只能把死期从 0 秒拖到约 5 秒）。
    /// 所以"拦住关窗口"在拥有控制台的前提下<b>做不到</b>；只能<b>先离开这个控制台</b>：
    /// <c>FreeConsole()</c> 之后本进程不再属于它，那扇窗再被关掉就与我们无关。
    /// </para>
    /// <para>
    /// 决策表（顺序即优先级）：
    /// <list type="number">
    ///   <item>输出已被上级收进<b>文件/管道</b> ⇒ 什么都不做，返回 true。
    ///         （让集成测试宿主与冒烟脚本零副作用；此时日志本来就不经过控制台。）</item>
    ///   <item>非交互式会话（Windows 服务 / Session 0）⇒ 返回 false，没什么可脱离的。</item>
    ///   <item><paramref name="attachToParent"/> 为 true ⇒ 附着/保留控制台（开发调试），
    ///         代价是关掉那个终端会同时停服务。</item>
    ///   <item>默认 ⇒ <see cref="DetachFromConsole"/>，关窗不再影响服务。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>脱离的代价要如实说清</b>：这个终端之后<b>看不到实时日志</b>了（日志照旧写文件）。
    /// 但被重定向到文件/管道的情形不受影响 —— <c>FreeConsole</c> 只关闭<b>指向控制台</b>的
    /// 标准句柄，普通文件/管道句柄不会被碰。
    /// </para>
    /// </summary>
    /// <returns>控制台输出此刻是否可用。</returns>
    public static bool ConfigureConsoleOutput(bool attachToParent)
    {
        if (!OperatingSystem.IsWindows()) return true;          // Linux：终端/重定向照旧

        // ① 输出已被上级接管（文件或管道）：保持现状，两种动作都不做。
        if (OutputIsCapturedToFileOrPipe()) return true;

        // ② 服务 / Session 0：没有控制台可言。
        if (!Environment.UserInteractive) return false;

        // ③ 显式要求保留控制台（开发调试）。关掉该终端会同时停止服务 —— 这是有意为之。
        if (attachToParent)
        {
            TryAttachParentConsole();
            return HasUsableConsole();
        }

        // ④ 默认：脱离控制台，让服务不再随启动它的那扇窗一起消失。
        DetachFromConsole();
        return false;
    }

    /// <summary>
    /// 脱离当前控制台。之后关闭"启动本进程的那个终端"<b>不会</b>再波及本进程。
    /// <para>
    /// 没有任何控制台时返回 false，属正常（双击启动的 GUI 进程就没有）。
    /// </para>
    /// </summary>
    public static bool DetachFromConsole()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            return NativeMethods.FreeConsole();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 本进程当前是否真的挂在一个控制台上（能往里写日志）。
    /// <para>
    /// 判据用 <see cref="NativeMethods.GetConsoleWindow"/> 而不是 <c>AttachConsole</c> 的返回值：
    /// 从 cmd 启动时进程<b>已经</b>继承控制台，这时再 <c>AttachConsole</c> 会因"已有控制台"
    /// 而失败，但控制台其实是可用的。
    /// </para>
    /// </summary>
    public static bool HasUsableConsole()
    {
        if (!OperatingSystem.IsWindows()) return true;

        try
        {
            return NativeMethods.GetConsoleWindow() != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 尝试附着到**父进程的控制台**（即"从终端启动"的场景）。
    /// <para>
    /// 用途：exe 改成 <c>WinExe</c>（GUI 子系统，双击时没有黑窗口）之后，
    /// 从 cmd/PowerShell 启动时仍希望看到实时日志。附着成功后必须
    /// <b>重新绑定 <see cref="Console.Out"/>/<see cref="Console.Error"/></b> ——
    /// .NET 可能在附着之前就已经缓存过"无效句柄"的快照，不重绑就什么都打不出来。
    /// </para>
    /// <para>
    /// <b>已知代价</b>：附着后本进程成为该控制台的持有者之一，用户关掉那个终端窗口会收到
    /// <c>CTRL_CLOSE_EVENT</c> 并被强杀。因此它<b>只用于开发/排障</b>
    /// （由 <c>Server:Startup:AttachConsole=true</c> 显式开启）；
    /// 默认路径是 <see cref="ConfigureConsoleOutput"/> 里的<b>脱离</b>。
    /// </para>
    /// </summary>
    /// <returns>是否附着成功（已在父控制台上、或双击启动时返回 false，且不应视为错误）。</returns>
    public static bool TryAttachParentConsole()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            if (!NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS)) return false;

            // 重新绑定：拿附着之后的新句柄重建 writer。
            var stdout = Console.OpenStandardOutput();
            var stderr = Console.OpenStandardError();

            var writer = new StreamWriter(stdout) { AutoFlush = true };
            Console.SetOut(writer);

            var errorWriter = new StreamWriter(stderr) { AutoFlush = true };
            Console.SetError(errorWriter);

            return true;
        }
        catch
        {
            // 附着失败（双击启动就会走这里）不是错误 —— 静默继续。
            return false;
        }
    }

    /// <summary>
    /// 弹一个错误框。
    /// <para>
    /// <b>为什么必须有它</b>：exe 改成 <c>WinExe</c> 后，<c>Console.Error.WriteLine</c>
    /// 在双击场景下<b>没有任何人看得到</b>（连窗口都没有），启动失败会表现为"闪一下、什么都没发生"。
    /// </para>
    /// </summary>
    public static void ShowError(string title, string message)
        => Show(title, message, NativeMethods.MB_ICONERROR);

    /// <summary>弹一个信息框。</summary>
    public static void ShowInformation(string title, string message)
        => Show(title, message, NativeMethods.MB_ICONINFORMATION);

    private static void Show(string title, string message, int icon)
    {
        if (!OperatingSystem.IsWindows()) return;

        // 非交互式会话（服务 / Session 0）弹窗会**永久阻塞** —— 必须挡住。
        if (!Environment.UserInteractive) return;

        try
        {
            NativeMethods.MessageBoxW(
                IntPtr.Zero,
                message,
                title,
                (uint)(NativeMethods.MB_OK | icon | NativeMethods.MB_SETFOREGROUND | NativeMethods.MB_TOPMOST));
        }
        catch
        {
            // 弹窗失败没有更下面的兜底了，只能咽掉。
        }
    }

    /// <summary>
    /// 单实例互斥锁。命名互斥体在所有会话间可见，适合"同一台机器只允许跑一个服务实例"。
    /// </summary>
    /// <returns>拿到锁返回可释放句柄；**已经有实例在跑**返回 null。</returns>
    public static IDisposable? TryAcquireSingleInstance(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;

        // 不请求所有权（bInitialOwner: false）：这里只需要"对象是否已存在"，不需要持有它，
        // 免得牵扯到线程所有权语义。
        var handle = NativeMethods.CreateMutexW(IntPtr.Zero, bInitialOwner: false, lpName: name);

        // ⚠️ 错误码必须在**紧接着**的下一句取：用 Marshal.GetLastWin32Error()（由 interop 桩缓存），
        //    不要另外 P/Invoke 一个 GetLastError —— 中间的任何托管调用都可能把它冲掉。
        var lastError = Marshal.GetLastWin32Error();

        if (handle == IntPtr.Zero) return null;

        if (lastError == NativeMethods.ERROR_ALREADY_EXISTS)
        {
            NativeMethods.CloseHandle(handle);
            return null;
        }

        return new MutexHandle(handle);
    }

    private sealed class MutexHandle : IDisposable
    {
        private IntPtr _handle;

        internal MutexHandle(IntPtr handle) => _handle = handle;

        public void Dispose()
        {
            var handle = _handle;
            _handle = IntPtr.Zero;
            if (handle != IntPtr.Zero) NativeMethods.CloseHandle(handle);
        }
    }
}
