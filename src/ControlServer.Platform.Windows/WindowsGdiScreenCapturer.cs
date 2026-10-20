using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// <b>GDI 抓屏</b>（M5.1）。
///
/// <para>
/// 🔥 <b>它不是生产路径，别把它当 DXGI 的替代品。</b>
/// 生产路径是 <c>IDXGIOutputDuplication</c>（零拷贝、能拿脏区、能拿 GPU 纹理），
/// 那才是 M5.4 的地基。本类的定位有两条，都不是"临时的"：
/// </para>
/// <list type="number">
///   <item><b>M5.1 打通链路</b> —— 先用最省事的一步把
///         「抓屏 → 编码 → 管道 → 中继 → 浏览器」接上。DXGI 那套 P/Invoke
///         有它自己的风险，不该和"链路能不能通"这个完全不同的问题捆在一轮里。</item>
///   <item><b>降级链末端</b>（方案 §1.5）—— 本类<b>对 GPU 没有任何要求</b>，
///         在连硬编都没有的机器上，它是"还能看见画面"的最后一档。</item>
/// </list>
///
/// <para><b>代价（必须说清，不要让它悄悄退化）</b>：</para>
/// <list type="bullet">
///   <item>每帧<b>一整次显存→内存拷贝</b>。4K60 的 BGRA 是 <b>1.99 GB/s</b>，
///         这条路<b>物理上做不到</b> 60 fps —— 所以它只配 MJPEG 这类低频脚手架；</item>
///   <item><b>没有脏区信息</b>（<c>CapturedSurface.DirtyRects</c> 那一路），
///         所以"画面没变就跳过编码"这条重要优化在本路径上无从谈起；</item>
///   <item>它会<b>强制把桌面合成到内存</b>，被控机在做全屏游戏/视频时开销更明显。</item>
/// </list>
///
/// <para>
/// 🔥 <b>关于鼠标指针</b>：GDI 的 <c>BitBlt</c>（哪怕带 <c>CAPTUREBLT</c>）
/// <b>不含鼠标指针</b> —— 与 DXGI Desktop Duplication 是同一个坑。
/// 本类<b>主动把指针画进帧里</b>（<c>GetCursorInfo</c> + <c>DrawIconEx</c>），
/// 并如实把 <see cref="CapturedFrame.IsCursorIncluded"/> 置为 <c>true</c>。
/// 不画的话，观看端的直接感受是"鼠标不动"，进而判定"这软件不实时"。
/// </para>
///
/// <para>
/// ⚠️ <b>DPI</b>：本类取的是<b>系统认为的</b>显示器矩形。若托盘进程不是
/// DPI 感知的，Windows 会把桌面按缩放比虚拟化给这个进程，
/// 抓到的就是<b>缩小且模糊</b>的画。因此托盘启动时显式声明
/// per-monitor-v2 感知（见 <c>Agent.Tray/Program.cs</c> 顶部）。
/// 这一条不做，"画面看着糊"与"显示器本身糊"就分不出来。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGdiScreenCapturer : IScreenCapturer
{
    /// <summary>虚拟桌面（所有显示器拼成的整块桌面）的屏 ID。</summary>
    /// <remarks>
    /// 🔥 它是 <c>null</c>/空 <c>screenId</c> 的落点：被控端是 2×2 拼 4K 的视频墙时，
    /// 要抓的就是这一块（7680×4320），而不是某一个输出。
    /// <para>⚠️ M5.4 起它的<b>定义</b>搬到了 <see cref="WindowsScreenEnumerator"/>：
    /// 抓屏后端不止一个了，而"有哪些屏"必须只有一份答案。这里保留常量只是为了不破坏已有引用。</para>
    /// </remarks>
    public const string VirtualDesktopId = WindowsScreenEnumerator.VirtualDesktopId;

    private readonly int _sessionId;

    /// <param name="sessionId">
    /// 本进程所在的会话编号（托盘进程 = 用户会话）。
    /// <para>
    /// 🔥 <b>必须由调用方传进来，不能在这里"猜"</b>：
    /// 它就是 <see cref="ScreenDescriptor.SessionId"/> 的来源，
    /// 而那个字段存在的唯一理由就是"别抓到别的会话的桌面"。
    /// </para>
    /// </param>
    public WindowsGdiScreenCapturer(int sessionId) => _sessionId = sessionId;

    /// <inheritdoc />
    /// <remarks>
    /// 🔥 实现搬到了 <see cref="WindowsScreenEnumerator.Enumerate"/> ——
    /// 与 DXGI 后端<b>共用同一份</b>。前端下拉框里的选项必须与两个后端能抓的东西完全一致，
    /// 各抄一份的后果不是"多写 90 行"，而是两者会悄悄分叉。
    /// </remarks>
    public IReadOnlyList<ScreenDescriptor> EnumerateScreens() => WindowsScreenEnumerator.Enumerate(_sessionId);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// 抓屏失败（没有桌面 / 目标屏不存在 / <c>BitBlt</c> 出错）。
    /// <para>
    /// ⚠️ 这里<b>刻意抛</b>而不是返回一张空图：空图会一路流到观看端，
    /// 变成"画面全黑" —— 而"全黑"与"被控端真的在黑屏"无法区分，
    /// 现场只能猜。抛出来，推流循环就能把真实原因写进日志与会话状态。
    /// </para>
    /// </exception>
    public Task<CapturedFrame> CaptureAsync(string screenId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Capture(screenId, _sessionId));
    }

    /// <summary>同步抓一帧（托盘侧的自检工具直接用它，不绕 <see cref="Task"/>）。</summary>
    public CapturedFrame Capture(string? screenId, int sessionId)
    {
        _ = sessionId;

        var target = Resolve(screenId);

        using var frame = new GdiFrame(target.X, target.Y, target.Width, target.Height);

        frame.BlitFromScreen();
        frame.DrawCursor(target.X, target.Y);

        return new CapturedFrame(
            target.Width,
            target.Height,
            Stride: target.Width * 4,
            Buffer: frame.ToBuffer(),
            CapturedAt: DateTimeOffset.UtcNow,
            IsCursorIncluded: true);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// <b>恒抛</b>。GDI 抓屏拿不到 GPU 纹理，这条路它<b>结构上就做不到</b>。
    /// <para>
    /// 🔥 为什么不用"每次返回 <c>null</c>"来搪塞：<c>null</c> 在
    /// <see cref="IScreenCapturer.CaptureSurfaceAsync"/> 的约定里是
    /// <b>"画面没有变化"</b>。一条零拷贝管线接到本实现上会安静地空转，
    /// 而且日志上一切正常 —— 那是最难查的一类故障。
    /// </para>
    /// </exception>
    public Task<CapturedSurface?> CaptureSurfaceAsync(
        string screenId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "GDI 抓屏没有 GPU 纹理可给（CaptureSurfaceAsync 是零拷贝路径的入口）。"
            + "零拷贝请使用 DXGI Desktop Duplication 的实现。");

    // ═══════════════════════════════════════════════════════════
    // 目标矩形
    // ═══════════════════════════════════════════════════════════

    private readonly record struct Target(string Device, int X, int Y, int Width, int Height);

    /// <summary>
    /// 把"要抓哪块屏"落到一个矩形上。
    /// <para>
    /// ⚠️ 空 <paramref name="screenId"/> 落到<b>虚拟桌面</b>而不是主显示器：
    /// 多屏拼接的被控端上，只抓主屏会让另外几块屏的内容彻底看不见，
    /// 而界面上还写着"正在共享屏幕"。
    /// </para>
    /// </summary>
    private Target Resolve(string? screenId)
    {
        var screens = EnumerateScreens();

        if (screens.Count == 0)
        {
            throw new InvalidOperationException(
                "枚举不到任何显示器 —— 说明**这个进程所在的会话没有可抓的桌面**"
                + "（典型原因：它跑在 Session 0 里，或这台机器没人登录）。"
                + "抓屏必须在用户会话中进行。");
        }

        if (string.IsNullOrWhiteSpace(screenId))
        {
            // 没指定 ⇒ 有虚拟桌面就用它，没有就用第一块。
            var auto = screens.FirstOrDefault(s => s.IsVirtualDesktop) ?? screens[0];

            return RectOf(auto);
        }

        var wanted = screens.FirstOrDefault(
            s => string.Equals(s.Id, screenId, StringComparison.OrdinalIgnoreCase));

        if (wanted is null)
        {
            var available = string.Join("、", screens.Select(s => s.Id));

            throw new InvalidOperationException(
                $"找不到屏「{screenId}」。本机可抓的屏有：{available}。");
        }

        return RectOf(wanted);
    }

    /// <summary>把描述符还原成显示器矩形（虚拟桌面就是所有屏的并集）。</summary>
    private static Target RectOf(ScreenDescriptor screen)
    {
        var rects = WindowsScreenEnumerator.MonitorRects();

        if (!screen.IsVirtualDesktop)
        {
            var monitor = rects.FirstOrDefault(m => m.Device == screen.Id);

            return monitor is null
                ? throw new InvalidOperationException($"显示器「{screen.Id}」刚刚消失了（枚举与抓取之间被拔掉？）。")
                : new Target(monitor.Device, monitor.X, monitor.Y, monitor.Width, monitor.Height);
        }

        if (rects.Count == 0) throw new InvalidOperationException("虚拟桌面里一块显示器都没有。");

        var left = rects.Min(r => r.X);
        var top = rects.Min(r => r.Y);
        var right = rects.Max(r => r.X + r.Width);
        var bottom = rects.Max(r => r.Y + r.Height);

        return new Target(VirtualDesktopId, left, top, right - left, bottom - top);
    }

    // ═══════════════════════════════════════════════════════════
    // 一块 DIB 的抓取
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 一次抓屏的临时资源：内存 DC + DIB。用完即放。
    ///
    /// <para>
    /// 🔥 <b>用 <c>CreateDIBSection</c> 而不是"兼容位图 + <c>GetDIBits</c>"</b>：
    /// 前者把像素内存的指针直接交给我们（一次 <c>BitBlt</c> + 一次 <c>Marshal.Copy</c>），
    /// 后者要 GDI 再做一次格式转换与整帧拷贝。4K 下这一下就是几十毫秒与几毫秒的差别。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>GDI 句柄泄漏是这条路最容易犯的错</b>：句柄不像内存会 GC，
    /// 泄漏的表现是"跑了几小时后画面再也出不来"，且日志一行都不报。
    /// 所以这里把"删"写在 <see cref="Dispose"/> 里、顺序严格反着来
    /// （先 <c>SelectObject</c> 换回旧位图，再删 DC，再删位图）。
    /// </para>
    /// </summary>
    private sealed class GdiFrame : IDisposable
    {
        private readonly int _x;
        private readonly int _y;

        private IntPtr _screenDc;
        private IntPtr _memoryDc;
        private IntPtr _bitmap;
        private IntPtr _previousBitmap;
        private IntPtr _bits;

        private bool _disposed;

        public GdiFrame(int x, int y, int width, int height)
        {
            _x = x;
            _y = y;
            Width = width;
            Height = height;

            _screenDc = GetDC(IntPtr.Zero);

            if (_screenDc == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"取不到屏幕 DC（Win32 错误 {Marshal.GetLastWin32Error()}）—— "
                    + "这个进程所在会话没有桌面，抓屏无从谈起。");
            }

            _memoryDc = CreateCompatibleDC(_screenDc);

            if (_memoryDc == IntPtr.Zero)
            {
                throw new InvalidOperationException($"创建内存 DC 失败（Win32 错误 {Marshal.GetLastWin32Error()}）。");
            }

            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,

                    // ⚠️ 负高度 = **自上而下**的 DIB。正高度会得到自下而上的行序，
                    //    而编码器（JPEG）要的是自上而下 —— 用正高度就得逐行倒着拷，
                    //    正是最容易拷错方向、还只在"上下颠倒"时才看出来的那种错。
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                },
            };

            _bitmap = CreateDIBSection(_screenDc, ref info, DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);

            if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"创建 {width}×{height} 的 DIB 失败（Win32 错误 {Marshal.GetLastWin32Error()}）。");
            }

            _previousBitmap = SelectObject(_memoryDc, _bitmap);
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>把屏幕上的那一块拷进 DIB。</summary>
        public void BlitFromScreen()
        {
            // CAPTUREBLT：把分层窗口（部分工具栏、某些播放器）也算进去。
            // 不加它的经典症状是"某些窗口在远控里是透明的"。
            if (!BitBlt(_memoryDc, 0, 0, Width, Height, _screenDc, _x, _y, SRCCOPY | CAPTUREBLT))
            {
                throw new InvalidOperationException(
                    $"BitBlt 抓屏失败（Win32 错误 {Marshal.GetLastWin32Error()}）。"
                    + "常见原因：目标显示器已断开、或当前桌面正被安全桌面（UAC / 锁屏）遮住。");
            }
        }

        /// <summary>
        /// 把鼠标指针画进帧里。
        ///
        /// <para>
        /// 🔥 <b>这一步不是"锦上添花"，是必需品</b>：GDI 与 DXGI 抓屏都<b>不含指针</b>。
        /// 不画的直接后果是观看端看到"鼠标不动"，并据此认定"这软件不实时"。
        /// </para>
        ///
        /// <para>
        /// ⚠️ <c>DrawIconEx</c> 画图标/指针时<b>不会自动套用热点</b>，
        /// 必须自己减去 <c>xHotspot / yHotspot</c>；否则指针整体偏右下若干像素
        /// —— 在小屏上不明显，4K 上就是"点哪儿都偏"。
        /// </para>
        ///
        /// <para>
        /// ⚠️ <c>GetIconInfo</c> 会<b>返回两张位图的所有权</b>给调用方，
        /// 不删就是每帧泄漏两个 GDI 对象（几小时后就抓不动了）。
        /// </para>
        /// </summary>
        public void DrawCursor(int originX, int originY)
        {
            try
            {
                var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };

                if (!GetCursorInfo(ref info)) return;

                if ((info.flags & CURSOR_SHOWING) == 0) return;
                if (info.hCursor == IntPtr.Zero) return;

                // 指针在"整块虚拟桌面"坐标系里 —— 减去本帧原点才是帧内坐标。
                var x = info.ptScreenPos.X - originX;
                var y = info.ptScreenPos.Y - originY;

                // 完全在帧外的指针不必画（多屏时很常见）。
                if (x < -64 || y < -64 || x > Width + 64 || y > Height + 64) return;

                ICONINFO icon = default;

                if (GetIconInfo(info.hCursor, ref icon))
                {
                    try
                    {
                        DrawIconEx(
                            _memoryDc,
                            x - icon.xHotspot,
                            y - icon.yHotspot,
                            info.hCursor,
                            0, 0, 0, IntPtr.Zero, DI_NORMAL);
                    }
                    finally
                    {
                        // 🔥 这两张位图的所有权归我们，必须删（见方法注释）。
                        if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
                        if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
                    }
                }
            }
            catch
            {
                // 画不上指针绝不值得让整条流断掉 —— 画面本身是好的。
            }
        }

        /// <summary>把 DIB 里的像素拷成 BGRA 数组。</summary>
        public byte[] ToBuffer()
        {
            var length = Width * Height * 4;
            var buffer = new byte[length];

            Marshal.Copy(_bits, buffer, 0, length);

            // DIB 是 32bpp，第 4 字节是"未使用"；GDI+ 的 Format32bppRgb 会忽略它，
            // 所以上层不必关心它是 0 还是 255。
            return buffer;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            try
            {
                // ⚠️ 顺序不能反：位图必须先"选出去"才能删，否则删的是空操作、
                //    而 DC 仍然占着它 —— 表现为明明释放了却还在泄漏。
                if (_previousBitmap != IntPtr.Zero && _memoryDc != IntPtr.Zero)
                {
                    SelectObject(_memoryDc, _previousBitmap);
                }

                if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
                if (_memoryDc != IntPtr.Zero) DeleteDC(_memoryDc);
                if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
            }
            catch
            {
                // 释放失败没有可做的事，且绝不能从 finally 里抛。
            }

            _bitmap = IntPtr.Zero;
            _memoryDc = IntPtr.Zero;
            _screenDc = IntPtr.Zero;
            _bits = IntPtr.Zero;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Win32
    // ═══════════════════════════════════════════════════════════
    //
    // ⚠️ 与"有哪些屏"有关的那几个声明（MONITORINFOEX / EnumDisplayMonitors /
    //    GetMonitorInfo / GetSystemMetrics / RECT）M5.4 起挪到了 WindowsScreenEnumerator，
    //    因为 DXGI 后端也要用同一份。

    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int CURSOR_SHOWING = 0x00000001;
    private const int DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;

        // 24bpp 及以下才用得到调色板；32bpp 用不到，但结构体必须占够位置。
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public uint[] bmiColors;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorInfo(ref CURSORINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr icon, ref ICONINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DrawIconEx(
        IntPtr dc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(
        IntPtr dest, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int rop);
}
