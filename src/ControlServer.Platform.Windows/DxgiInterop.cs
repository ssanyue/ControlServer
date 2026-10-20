using System.Runtime.InteropServices;

namespace ControlServer.Platform.Windows;

/// <summary>
/// <b>DXGI 的裸 vtable 声明</b>（M5.4 零拷贝管线的抓屏侧）。
///
/// <para>
/// 🔥 <b>只声明用到的槽位</b>，理由见 <see cref="ComVtable"/> 的说明。
/// 每个槽位常量后面都标了它的依据（`*Vtbl` 结构里的声明序号）。
/// </para>
///
/// <para>
/// ⚠️ <b>这些数字不是从记忆里来的</b>：本机没有 Windows SDK，因此实施时下载了
/// mingw-w64 头文件（`dxgi.h` / `dxgi1_2.h` / `dxgitype.h` / `dxgiformat.h` / `winerror.h`），
/// 逐接口解析 `*Vtbl` 的方法顺序得出。核对方法与完整表格见
/// <c>docs/M5.4-实施方案.md</c> §3.1。
/// <b>改动任何一个数字之前，请重新对头文件核一遍。</b>
/// </para>
/// </summary>
internal static class DxgiInterop
{
    // ──────────────────────────── 接口 IID（QueryInterface 用）────────────────────────────

    internal static readonly Guid IID_IDXGIFactory = new("7b7166ec-21c7-44ae-b21a-c9ae321ae369");
    internal static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    internal static readonly Guid IID_IDXGIAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");
    internal static readonly Guid IID_IDXGIOutput = new("ae02eedb-c735-4690-8d52-5a8dc20213aa");
    internal static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    internal static readonly Guid IID_IDXGIOutputDuplication = new("191cfac3-a341-470d-b26e-a864f428319c");
    internal static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    internal static readonly Guid IID_IDXGIResource = new("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");
    internal static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    // ──────────────────────────── 枚举取值（头文件核实）────────────────────────────

    /// <summary><c>DXGI_MODE_ROTATION_IDENTITY</c>。<b>不是 0</b> —— 0 是 UNSPECIFIED。</summary>
    internal const int ModeRotationIdentity = 0x1;

    /// <summary><c>DXGI_MODE_ROTATION_UNSPECIFIED</c>。</summary>
    internal const int ModeRotationUnspecified = 0x0;

    /// <summary><c>DXGI_FORMAT_B8G8R8A8_UNORM</c>（桌面抓取的原始格式）。</summary>
    internal const int FormatB8G8R8A8Unorm = 0x57;

    /// <summary><c>DXGI_FORMAT_NV12</c>（硬编输入的 4:2:0 半平面格式）。</summary>
    internal const int FormatNv12 = 0x67;

    /// <summary><c>DXGI_FORMAT_R8_UNORM</c>（NV12 的 Y 平面）。</summary>
    internal const int FormatR8Unorm = 0x3d;

    /// <summary><c>DXGI_FORMAT_R8G8_UNORM</c>（NV12 的 UV 平面）。</summary>
    internal const int FormatR8G8Unorm = 0x31;

    // ──────────────────────────── HRESULT（winerror.h 核实）────────────────────────────

    internal const int S_OK = 0;

    /// <summary>
    /// 🔥 <c>DXGI_ERROR_WAIT_TIMEOUT</c> = <c>0x887A0027</c>。
    /// <para>
    /// <b>画面没有变化时 <see cref="Delegates.AcquireNextFrameFn"/> 就会返回它。</b>
    /// 这是<b>正常情形</b>，不是故障 —— 把它当错误处理会让"桌面静止"被读成"采集坏了"。
    /// </para>
    /// </summary>
    internal const int ErrorWaitTimeout = unchecked((int)0x887A0027);

    /// <summary><c>DXGI_ERROR_ACCESS_LOST</c>：切桌面 / 全屏独占 / 分辨率变化 ⇒ <b>必须重建 duplication</b>。</summary>
    internal const int ErrorAccessLost = unchecked((int)0x887A0026);

    /// <summary><c>DXGI_ERROR_NOT_CURRENTLY_AVAILABLE</c>：通常意味着<b>已经有别的进程（或本进程的另一个实例）在抓这块屏</b>。</summary>
    internal const int ErrorNotCurrentlyAvailable = unchecked((int)0x887A0022);

    /// <summary><c>DXGI_ERROR_UNSUPPORTED</c>：这块 adapter/output 组合不支持 duplication。</summary>
    internal const int ErrorUnsupported = unchecked((int)0x887A0004);

    /// <summary>
    /// <c>E_ACCESSDENIED</c>（<b>Win32 错误码</b>，不是 DXGI 那一族）。
    ///
    /// <para>
    /// 🔥 <b>为什么它值得单独一个常量</b>：<c>DuplicateOutput</c> 在三种完全不同的
    /// 处境下都会回它，而三者的处置各不相同：
    /// </para>
    /// <list type="number">
    ///   <item><b>已经有一个进程抓着这块屏</b>（录屏 / 投屏 / 另一份本程序）——
    ///         最常见，处置是"关掉那个程序"；</item>
    ///   <item><b>当前会话不可交互</b>（Session 0 服务、RDP 已断开、桌面被锁）——
    ///         处置是"确认有人登录且桌面没锁"；</item>
    ///   <item><b>UAC 安全桌面正盖在上面</b>（提升提示）—— 等几秒自己会好。</item>
    /// </list>
    /// <para>
    /// 它原先落在兜底分支里，只留下一句"DuplicateOutput 失败" —— 现场会把它当成代码 bug。
    /// </para>
    /// </summary>
    internal const int ErrorAccessDenied = unchecked((int)0x80070005);

    /// <summary><c>DXGI_ERROR_NOT_FOUND</c>：<b>枚举到尾巴了</b>（正常终止条件，不是错误）。</summary>
    internal const int ErrorNotFound = unchecked((int)0x887A0002);

    /// <summary>
    /// <c>DXGI_ERROR_MORE_DATA</c>：给的空间不够。
    /// <para>🔥 <b>它在这里是"探路"的正常返回值</b>：<c>GetFrameDirtyRects</c> 先按 0 字节问一次，
    /// 它就会回这个码并顺便告诉你需要多大 —— 别把它当失败。</para>
    /// </summary>
    internal const int ErrorMoreData = unchecked((int)0x887A0003);

    /// <summary><c>DXGI_ERROR_INVALID_CALL</c>：参数或调用时机不对（例如没 <c>Acquire</c> 就去读脏区）。</summary>
    internal const int ErrorInvalidCall = unchecked((int)0x887A0001);

    /// <summary><c>DXGI_ERROR_DEVICE_REMOVED</c>：显卡掉了 / 驱动重置。</summary>
    internal const int ErrorDeviceRemoved = unchecked((int)0x887A0005);

    /// <summary><c>DXGI_ERROR_DEVICE_RESET</c>：驱动被重置，设备对象已失效。</summary>
    internal const int ErrorDeviceReset = unchecked((int)0x887A0007);

    // ──────────────────────────── 结构体（字段顺序照头文件）────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rational
    {
        public uint Numerator;
        public uint Denominator;

        /// <summary>化成 double（分母为 0 时返回 0 —— 那种数据本身没有意义，不该抛）。</summary>
        public readonly double ToDouble() => Denominator == 0 ? 0 : (double)Numerator / Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ModeDesc
    {
        public uint Width;
        public uint Height;
        public Rational RefreshRate;
        public int Format;
        public int ScanlineOrdering;
        public int Scaling;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct OutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        public Rect DesktopCoordinates;

        /// <summary><c>BOOL</c>：这块屏是否接在桌面上。<b>false 时 duplication 建不起来。</b></summary>
        public int AttachedToDesktop;

        public int Rotation;

        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;

        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public Luid AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct OutduplDesc
    {
        public ModeDesc ModeDesc;
        public int Rotation;
        public int DesktopImageInSystemMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct OutduplPointerPosition
    {
        public int X;
        public int Y;
        public int Visible;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct OutduplPointerShapeInfo
    {
        public uint Type;
        public uint Width;
        public uint Height;
        public uint Pitch;
        public int HotSpotX;
        public int HotSpotY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct OutduplFrameInfo
    {
        /// <summary>
        /// <c>LARGE_INTEGER</c>（100 ns 单位）。⚠️ <b>这是 duplication 自己的时间轴</b>，
        /// 与 QPC / 会话单调时钟都不是同一个原点 —— 不要拿它去做音画同步。
        /// </summary>
        public long LastPresentTime;

        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public OutduplPointerPosition PointerPosition;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    // ──────────────────────────── 槽位号（见类注释）────────────────────────────

    /// <summary><c>IDXGIFactory1</c> 的槽位。</summary>
    internal static class Factory1
    {
        /// <summary><c>IDXGIObject</c> 起 7 个之后的第 1 个：<c>EnumAdapters</c>。</summary>
        internal const int EnumAdapters = 7;

        /// <summary>13 槽接口里的最后一个：<c>EnumAdapters1</c>。</summary>
        internal const int EnumAdapters1 = 12;
    }

    /// <summary><c>IDXGIAdapter1</c> 的槽位。</summary>
    internal static class Adapter1
    {
        /// <summary><c>IDXGIAdapter::EnumOutputs</c>。</summary>
        internal const int EnumOutputs = 7;

        /// <summary><c>IDXGIAdapter1::GetDesc1</c>。</summary>
        internal const int GetDesc1 = 10;
    }

    /// <summary><c>IDXGIOutput</c> / <c>IDXGIOutput1</c> 的槽位。</summary>
    internal static class Output
    {
        /// <summary><c>IDXGIOutput::GetDesc</c>。</summary>
        internal const int GetDesc = 7;

        /// <summary><c>IDXGIOutput1::DuplicateOutput</c>（本批的入口）。</summary>
        internal const int DuplicateOutput = 22;
    }

    /// <summary><c>IDXGIOutputDuplication</c> 的槽位。</summary>
    internal static class Duplication
    {
        internal const int GetDesc = 7;
        internal const int AcquireNextFrame = 8;
        internal const int GetFrameDirtyRects = 9;
        internal const int GetFramePointerShape = 11;
        internal const int ReleaseFrame = 14;
    }

    /// <summary><c>IDXGIDevice</c> 的槽位。</summary>
    internal static class Device
    {
        /// <summary><c>IDXGIDevice::GetAdapter</c>（设备造出来之后反查它落在哪块卡上）。</summary>
        internal const int GetAdapter = 7;
    }

    /// <summary><c>IDXGIResource</c> 的槽位。</summary>
    internal static class Resource
    {
        /// <summary><c>IDXGIResource::GetSharedHandle</c>（本批不用，留作诊断）。</summary>
        internal const int GetSharedHandle = 8;

        /// <summary><c>IDXGIResource::GetUsage</c>。</summary>
        internal const int GetUsage = 9;
    }

    // ──────────────────────────── 函数原型 ────────────────────────────

    [DllImport("dxgi.dll", ExactSpelling = true)]
    internal static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    // ──────────────────────────── 委托 ────────────────────────────

    internal static class Delegates
    {
        /// <summary><c>DXGI_OUTDUPL_DESC <c>*</c></c> —— <see cref="OutduplDesc"/> 的接收形状。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int EnumOutputsFn(IntPtr self, uint index, out IntPtr output);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetAdapterDesc1Fn(IntPtr self, out AdapterDesc1 desc);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetOutputDescFn(IntPtr self, out OutputDesc desc);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int DuplicateOutputFn(IntPtr self, IntPtr device, out IntPtr duplication);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetDuplicationDescFn(IntPtr self, out OutduplDesc desc);

        /// <summary>
        /// <c>AcquireNextFrame(self, timeoutMs, *frameInfo, **desktopResource)</c>。
        /// </summary>
        /// <remarks>
        /// 🔥 三个返回值都要正确对待，否则抓屏循环会走向完全不同的三种错法：
        /// <list type="bullet">
        ///   <item><c>S_OK</c> ⇒ 拿到一帧，<b>必须</b>配一次 <c>ReleaseFrame</c>；</item>
        ///   <item><see cref="ErrorWaitTimeout"/> ⇒ <b>画面没变</b>（正常），
        ///        <b>不要</b> ReleaseFrame，也<b>不要</b>当作失败；</item>
        ///   <item><see cref="ErrorAccessLost"/> ⇒ 需要重建 duplication。</item>
        /// </list>
        /// </remarks>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int AcquireNextFrameFn(
            IntPtr self, uint timeoutMs, out OutduplFrameInfo frameInfo, out IntPtr desktopResource);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetFrameDirtyRectsFn(
            IntPtr self, uint bufferSize, IntPtr dirtyRects, out uint requiredSize);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetFramePointerShapeFn(
            IntPtr self, uint bufferSize, IntPtr shapeBuffer, out uint requiredSize, ref OutduplPointerShapeInfo shapeInfo);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int ReleaseFrameFn(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetAdapterFn(IntPtr self, out IntPtr adapter);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetUsageFn(IntPtr self, out uint usage);
    }

    /// <summary>把一个 HRESULT 说成人话（日志与失败原因用）。</summary>
    internal static string Describe(int hr) => hr switch
    {
        S_OK => "S_OK",
        ErrorWaitTimeout => "DXGI_ERROR_WAIT_TIMEOUT（画面没有变化 —— 正常情形）",
        ErrorAccessLost => "DXGI_ERROR_ACCESS_LOST（桌面切换 / 分辨率变化 ⇒ 需要重建）",
        ErrorNotCurrentlyAvailable => "DXGI_ERROR_NOT_CURRENTLY_AVAILABLE（这块屏已经被别的进程抓走了）",
        ErrorUnsupported => "DXGI_ERROR_UNSUPPORTED（该 adapter / output 组合不支持抓屏）",
        ErrorNotFound => "DXGI_ERROR_NOT_FOUND（枚举到尾巴了）",
        ErrorMoreData => "DXGI_ERROR_MORE_DATA（给的缓冲区不够 —— 对脏区查询而言这是「探路」的正常返回）",
        ErrorInvalidCall => "DXGI_ERROR_INVALID_CALL（调用时机不对，例如没 Acquire 就读脏区）",
        ErrorDeviceRemoved => "DXGI_ERROR_DEVICE_REMOVED（显卡掉了 / 驱动重置）",
        ErrorDeviceReset => "DXGI_ERROR_DEVICE_RESET（驱动被重置）",
        _ => ComVtable.Describe(hr),
    };
}
