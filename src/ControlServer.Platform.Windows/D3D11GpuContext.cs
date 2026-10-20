using System.Runtime.InteropServices;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// <b>一块显示适配器上的 D3D11 设备，以及它派生出来的一切</b>（M5.4 零拷贝管线的"底座"）。
///
/// <para>
/// 🔥 <b>为什么要有这个类，而不是让抓屏器和编码器各自建一个设备</b>：
/// 零拷贝成立的前提是<b>整条链上只有一个 D3D11 设备</b>。
/// </para>
/// <list type="number">
///   <item><c>ID3D11VideoProcessorBlt</c> 的输入/输出视图必须与设备同源；</item>
///   <item><c>MFCreateDXGISurfaceBuffer</c> 造出的 buffer 只有在<b>编码器认得那个设备</b>时才走显存 ——
///         而"认得"就是拿到同一个 <c>IMFDXGIDeviceManager</c>。</item>
/// </list>
/// <para>
/// 两条里任何一条不成立，表现都是<b>静默地退化成"拷回内存"或直接失败</b>，而<b>不会有任何报错</b>。
/// ⇒ 所以把设备抽成一个显式的对象，让"同一个设备"变成<b>结构上的事实</b>，
/// 而不是散落在两处注释里的约定。
/// </para>
///
/// <para>
/// ⚠️ <b>选适配器必须按"输出（显示器）"反查，不能按序号</b>：本机是双显卡
/// （Intel UHD 620 + MX250），而 <c>EnumAdapters</c> 暴露的<b>节点数与物理卡数不一致</b>
/// （M5.0 第二批实测：4 个节点、归并后 2 块卡）。按序号挑的症状是<b>全黑帧</b>，不是报错。见方案 §5.1。
/// </para>
/// </summary>
internal sealed class D3D11GpuContext : IDisposable
{
    private IntPtr _factory;
    private IntPtr _adapter;
    private IntPtr _output;
    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _dxgiDevice;
    private IntPtr _videoDevice;
    private IntPtr _videoContext;
    private IntPtr _deviceManager;

    private bool _disposed;

    /// <summary><c>ID3D11Device*</c>。抓屏与编码<b>都必须用它</b>。</summary>
    internal IntPtr Device => _device;

    /// <summary><c>ID3D11DeviceContext*</c>（立即上下文）。</summary>
    internal IntPtr Context => _context;

    /// <summary><c>ID3D11VideoDevice*</c>（由 <see cref="Device"/> QueryInterface 得来）。</summary>
    internal IntPtr VideoDevice => _videoDevice;

    /// <summary><c>ID3D11VideoContext*</c>（由 <see cref="Context"/> QueryInterface 得来）。</summary>
    internal IntPtr VideoContext => _videoContext;

    /// <summary><c>IDXGIAdapter1*</c>（仅供取描述信息）。</summary>
    internal IntPtr Adapter => _adapter;

    /// <summary>
    /// <c>IDXGIOutput*</c> —— 抓屏要的那块显示器。
    /// <para><c>IntPtr.Zero</c> = 本次没有绑定具体显示器（只建了设备）。</para>
    /// </summary>
    internal IntPtr Output => _output;

    /// <summary><c>IMFDXGIDeviceManager*</c>（零拷贝的接线口）。</summary>
    internal IntPtr DeviceManager => _deviceManager;

    /// <summary><c>IMFDXGIDeviceManager::ResetDevice</c> 用的令牌。</summary>
    internal uint ResetToken { get; private set; }

    /// <summary>
    /// 🔥 <b>设备管理器手上真的握着一块设备</b>（建完之后 <c>OpenDeviceHandle</c> 开到过句柄）。
    ///
    /// <para>
    /// ⚠️ <b>为什么不能拿 <c>ResetDevice</c> 的返回值代替它</b>：
    /// <c>pUnkDevice</c> 在 C++ 侧是 <c>IUnknown*</c>，<b>弱类型</b>，
    /// 而官方文档对这一参的说法自相矛盾（参数表说 DXGI 设备、备注说 D3D 设备，
    /// 详见 <see cref="CreateDeviceManager"/>）⇒ 传错东西**照样回 <c>S_OK</c>**。
    /// 真正把这个弱类型"收紧"的是后面的 <c>OpenDeviceHandle</c>：
    /// 它要拿到句柄就必须先 <c>QI IDXGIDevice</c>，QI 不过就开不出来。
    /// </para>
    /// <para>
    /// ⇒ <b>这一格是 <c>S_OK</c> 证明不了的那件事的唯一证据。</b>
    /// 它 <c>false</c> 而 <c>ResetDevice</c> 却回了 <c>S_OK</c>，是个真实且安静的坏信号。
    /// </para>
    /// </summary>
    internal bool DeviceHandleVerified { get; private set; }

    /// <summary>适配器型号名（日志与能力报告用）。</summary>
    internal string AdapterName { get; private set; } = "未知";

    /// <summary>实际拿到的特性级别（<b>不是请求的那个</b> —— 请求 11_1 可能只给 11_0）。</summary>
    internal int FeatureLevel { get; private set; }

    /// <summary>
    /// 🔥 <b>这个设备是不是带 <c>D3D11_CREATE_DEVICE_VIDEO_SUPPORT</c> 建的。</b>
    ///
    /// <para>
    /// 官方对它的措辞是 "the device <b>should</b> be created with"（见
    /// <c>D3D11Interop.CreateDeviceVideoSupport</c> 的说明），而<b>它会影响能不能建出设备</b> ——
    /// 驱动没到 WDDM 1.2 时它会强制把特性级别压到 9.x。所以本类是"先带它试、失败退回"，
    /// <b>而退回过这件事必须能被读出来</b>：否则"零拷贝链路建起来了但性能不对"这种现场
    /// 会变成一句没有线索的抱怨。
    /// </para>
    /// </summary>
    internal bool VideoSupportRequested { get; private set; }

    /// <summary>设备上是否已开启多线程保护（<c>ID3D10Multithread::SetMultithreadProtected</c>）。</summary>
    internal bool MultithreadProtected { get; private set; }

    private D3D11GpuContext()
    {
    }

    /// <summary>
    /// 按<b>输出设备名</b>（<c>\\.\DISPLAY1</c> 这种）建上下文。
    /// <para><paramref name="outputDeviceName"/> 为 <c>null</c> ⇒ 挑第一个"接着桌面"的输出。</para>
    /// </summary>
    /// <param name="failure">失败原因（<b>一定</b>是给人看的一句话；成功时为 <c>null</c>）。</param>
    /// <param name="adapterIndex">
    /// 🔥 <b>只给诊断用</b>：强制用第 N 块适配器（<c>null</c> = 正常路径，按 output 名反查）。
    ///
    /// <para>
    /// ⚠️ <b>为什么需要这个口子</b>：按 output 名反查时，如果<b>两块适配器都挂着同名的
    /// 「\\.\DISPLAY1」</b>（双显卡笔记本上很常见 —— 独显按 Optimus 也会把显示器列出来），
    /// 就会选中"先枚举到的那一块"。而它可能<b>根本不是真正在扫描输出的那一块</b>，
    /// 症状是 <b>抓到全黑的帧，且一个错误码都不报</b>。
    /// </para>
    /// <para>
    /// 这个参数让诊断代码能"把每一块都试一遍、看哪块不是黑的"，
    /// 而不是靠猜 —— 生产路径不该用它（<b>猜适配器序号是不允许的</b>，见类注释）。
    /// </para>
    /// </param>
    internal static D3D11GpuContext? TryCreate(
        string? outputDeviceName, out string? failure, int? adapterIndex = null)
    {
        failure = null;

        if (!OperatingSystem.IsWindows())
        {
            failure = "D3D11 / DXGI 只在 Windows 上可用。";

            return null;
        }

        var ctx = new D3D11GpuContext();

        try
        {
            if (!ctx.OpenAdapterAndOutput(outputDeviceName, adapterIndex, out failure))
            {
                return ctx.DisposeAndReturn();
            }

            if (!ctx.CreateDevice(out failure)) return ctx.DisposeAndReturn();

            if (!ctx.CreateDeviceManager(out failure)) return ctx.DisposeAndReturn();

            return ctx;
        }
        catch (DllNotFoundException ex)
        {
            failure = $"d3d11.dll / dxgi.dll / mfplat.dll 缺失：{ex.Message}";

            return ctx.DisposeAndReturn();
        }
        catch (Exception ex)
        {
            failure = $"建 D3D11 设备时抛出 {ex.GetType().Name}：{ex.Message}";

            return ctx.DisposeAndReturn();
        }
    }

    /// <summary>本上下文用的是第几块适配器（诊断用；正常路径里它是"反查的结果"而不是输入）。</summary>
    internal int AdapterIndex { get; private set; } = -1;

    /// <summary>
    /// 枚举本机所有 DXGI 适配器节点，以及每个节点上挂的输出。
    ///
    /// <para>
    /// 🔥 <b>它是给"能力报告"与排障用的，不是给生产路径选卡用的</b>：
    /// 一个物理显卡可以暴露成<b>多个节点</b>（本机 Intel 核显实测 2 个不同 LUID），
    /// 而 <b>DXGI 节点数 ≠ 显卡块数</b> —— 报告里必须按型号归并，
    /// 选卡必须按 output 反查。两者都不是"按序号挑"。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<DxgiAdapterInfo> DescribeAdapters()
    {
        var result = new List<DxgiAdapterInfo>();

        if (!OperatingSystem.IsWindows()) return result;

        var factory = IntPtr.Zero;

        try
        {
            var iid = DxgiInterop.IID_IDXGIFactory1;

            if (!ComVtable.Succeeded(DxgiInterop.CreateDXGIFactory1(ref iid, out factory))) return result;

            var enumAdapters = ComVtable.Bind<DxgiInterop.Delegates.EnumAdapters1Fn>(
                factory, DxgiInterop.Factory1.EnumAdapters1);

            for (uint index = 0; ; index++)
            {
                var hr = enumAdapters(factory, index, out var adapter);

                if (!ComVtable.Succeeded(hr))
                {
                    if (hr == DxgiInterop.ErrorNotFound) break;

                    break;
                }

                try
                {
                    var name = "未知";
                    var vendorId = 0u;
                    var deviceId = 0u;
                    var luid = default(DxgiInterop.Luid);

                    var getDesc = ComVtable.Bind<DxgiInterop.Delegates.GetAdapterDesc1Fn>(
                        adapter, DxgiInterop.Adapter1.GetDesc1);

                    if (ComVtable.Succeeded(getDesc(adapter, out var desc)))
                    {
                        name = string.IsNullOrWhiteSpace(desc.Description) ? "未知" : desc.Description.Trim();
                        vendorId = desc.VendorId;
                        deviceId = desc.DeviceId;
                        luid = desc.AdapterLuid;
                    }

                    result.Add(new DxgiAdapterInfo(
                        (int)index,
                        name,
                        vendorId,
                        deviceId,
                        $"0x{luid.HighPart:X8}:0x{luid.LowPart:X8}",
                        DescribeOutputs(adapter)));
                }
                finally
                {
                    ComVtable.ReleasePointer(ref adapter);
                }
            }
        }
        catch (DllNotFoundException)
        {
            // 没有 dxgi.dll ⇒ 空表。
        }
        finally
        {
            ComVtable.ReleasePointer(ref factory);
        }

        return result;
    }

    private static List<DxgiOutputInfo> DescribeOutputs(IntPtr adapter)
    {
        var outputs = new List<DxgiOutputInfo>();

        try
        {
            var enumOutputs = ComVtable.Bind<DxgiInterop.Delegates.EnumOutputsFn>(
                adapter, DxgiInterop.Adapter1.EnumOutputs);

            for (uint index = 0; ; index++)
            {
                var hr = enumOutputs(adapter, index, out var output);

                if (!ComVtable.Succeeded(hr)) break;

                try
                {
                    var getDesc = ComVtable.Bind<DxgiInterop.Delegates.GetOutputDescFn>(
                        output, DxgiInterop.Output.GetDesc);

                    if (!ComVtable.Succeeded(getDesc(output, out var desc))) continue;

                    outputs.Add(new DxgiOutputInfo(
                        desc.DeviceName,
                        desc.AttachedToDesktop != 0,
                        desc.DesktopCoordinates.Width,
                        desc.DesktopCoordinates.Height,

                        // ⚠️ 0 是 UNSPECIFIED、1 才是 IDENTITY —— 别读反。
                        desc.Rotation == DxgiInterop.ModeRotationIdentity ? "identity" : $"rotation={desc.Rotation}"));
                }
                finally
                {
                    ComVtable.ReleasePointer(ref output);
                }
            }
        }
        catch (DllNotFoundException)
        {
            // 同上。
        }

        return outputs;
    }

    /// <summary>一个 DXGI 适配器节点（<b>不是</b>"一块显卡" —— 一块卡可以有多个节点）。</summary>
    internal sealed record DxgiAdapterInfo(
        int Index,
        string Name,
        uint VendorId,
        uint DeviceId,
        string Luid,
        IReadOnlyList<DxgiOutputInfo> Outputs);

    /// <summary>挂着的一个输出（显示器）。</summary>
    internal sealed record DxgiOutputInfo(
        string DeviceName,
        bool AttachedToDesktop,
        int Width,
        int Height,
        string Rotation);

    // ── 第一步：找 adapter 与 output ──

    private bool OpenAdapterAndOutput(string? outputDeviceName, int? adapterIndex, out string? failure)
    {
        failure = null;

        var iid = DxgiInterop.IID_IDXGIFactory1;

        var hr = DxgiInterop.CreateDXGIFactory1(ref iid, out _factory);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"CreateDXGIFactory1 失败（{DxgiInterop.Describe(hr)}）。";

            return false;
        }

        var enumAdapters = ComVtable.Bind<DxgiInterop.Delegates.EnumAdapters1Fn>(
            _factory, DxgiInterop.Factory1.EnumAdapters1);

        // 诊断路径：只看指定的那一块。
        var first = adapterIndex is { } forced ? (uint)forced : 0u;
        var last = adapterIndex is { } only ? (uint)only : uint.MaxValue;

        for (var adapterIndexCurrent = first; adapterIndexCurrent <= last; adapterIndexCurrent++)
        {
            var adapterHr = enumAdapters(_factory, adapterIndexCurrent, out var adapter);

            // DXGI_ERROR_NOT_FOUND = 枚举到尾巴了（正常终止条件）。
            if (!ComVtable.Succeeded(adapterHr))
            {
                if (adapterHr == DxgiInterop.ErrorNotFound) break;

                failure = $"枚举适配器 #{adapterIndexCurrent} 失败（{DxgiInterop.Describe(adapterHr)}）。";

                return false;
            }

            if (TryMatchOutput(adapter, outputDeviceName, out var output, out var matchedName))
            {
                _adapter = adapter;
                _output = output;
                AdapterName = matchedName;
                AdapterIndex = (int)adapterIndexCurrent;

                return true;
            }

            // 这块卡上没找到目标屏 —— 放掉它，继续下一块。
            ComVtable.ReleasePointer(ref adapter);
        }

        failure = adapterIndex is { } wanted
            ? $"强制指定的适配器 #{wanted} 上没有挂着 {outputDeviceName ?? "接着桌面的"} 输出。"
            : outputDeviceName is null
                ? "没有任何一块适配器上挂着「接着桌面」的输出 —— 通常意味着当前会话没有可抓的桌面（§11.1：无人登录）。"
                : $"没有任何适配器上挂着名为 {outputDeviceName} 的输出。";

        return false;
    }

    private static bool TryMatchOutput(
        IntPtr adapter, string? wantedDeviceName, out IntPtr output, out string adapterName)
    {
        output = IntPtr.Zero;
        adapterName = "未知";

        // 先拿型号名（即使这块卡没有匹配的输出，名字也可以进日志）。
        var getDesc = ComVtable.Bind<DxgiInterop.Delegates.GetAdapterDesc1Fn>(
            adapter, DxgiInterop.Adapter1.GetDesc1);

        if (ComVtable.Succeeded(getDesc(adapter, out var adapterDesc)))
        {
            adapterName = string.IsNullOrWhiteSpace(adapterDesc.Description) ? "未知" : adapterDesc.Description.Trim();
        }

        var enumOutputs = ComVtable.Bind<DxgiInterop.Delegates.EnumOutputsFn>(
            adapter, DxgiInterop.Adapter1.EnumOutputs);

        var getOutputDesc = default(DxgiInterop.Delegates.GetOutputDescFn);

        for (uint outputIndex = 0; ; outputIndex++)
        {
            var hr = enumOutputs(adapter, outputIndex, out var candidate);

            if (!ComVtable.Succeeded(hr))
            {
                // NOT_FOUND = 这块卡没有更多输出了。
                return false;
            }

            getOutputDesc ??= ComVtable.Bind<DxgiInterop.Delegates.GetOutputDescFn>(
                candidate, DxgiInterop.Output.GetDesc);

            var descHr = getOutputDesc(candidate, out var desc);

            var matches = ComVtable.Succeeded(descHr)
                && desc.AttachedToDesktop != 0
                && (wantedDeviceName is null
                    || string.Equals(desc.DeviceName, wantedDeviceName, StringComparison.OrdinalIgnoreCase));

            if (matches)
            {
                // ⚠️ 从 IDXGIOutput 升到 IDXGIOutput1 才能 DuplicateOutput。
                //    这里就把 QI 做掉：拿不到就直接算失败，别拖到抓屏那一刻才发现。
                var iid = DxgiInterop.IID_IDXGIOutput1;

                if (ComVtable.Succeeded(ComVtable.QueryInterfaceRaw(candidate, ref iid, out var output1)))
                {
                    ComVtable.ReleasePointer(ref candidate);

                    output = output1;

                    return true;
                }
            }

            ComVtable.ReleasePointer(ref candidate);
        }
    }

    // ── 第二步：建 D3D11 设备 ──

    private bool CreateDevice(out string? failure)
    {
        failure = null;

        // ⚠️ 传了 adapter 就必须用 D3D_DRIVER_TYPE_UNKNOWN，否则 E_INVALIDARG。
        var featureLevels = new[] { D3D11Interop.FeatureLevel11_1, D3D11Interop.FeatureLevel11_0 };

        // 🔥 先带 D3D11_CREATE_DEVICE_VIDEO_SUPPORT 试，失败退回。
        //
        //    文档对它用的是 "should"（见 D3D11Interop.CreateDeviceVideoSupport 的说明），
        //    但它有一条硬副作用：驱动没实现到 WDDM 1.2 时，设了它等于"只要 9.x 特性级别的设备"，
        //    于是**请求 10 以上会直接创建失败**。
        //    ⇒ 盲目加它就等于把"老驱动上原本能跑"变成"老驱动上起不来"，
        //      而这不是本方案要的取舍；先试后退，同时把"退回过"记下来。
        var hr = D3D11Interop.D3D11CreateDevice(
            _adapter,
            D3D11Interop.DriverTypeUnknown,
            IntPtr.Zero,
            D3D11Interop.CreateDeviceBgraSupport | D3D11Interop.CreateDeviceVideoSupport,
            featureLevels,
            (uint)featureLevels.Length,
            D3D11Interop.SdkVersion,
            out _device,
            out var featureLevel,
            out _context);

        if (ComVtable.Succeeded(hr))
        {
            VideoSupportRequested = true;
        }
        else
        {
            // 退回到只有 BGRA —— 抓屏与视频处理器照样能用（它们在 M5.4-#25 就是在没有这个标志时跑通的）。
            hr = D3D11Interop.D3D11CreateDevice(
                _adapter,
                D3D11Interop.DriverTypeUnknown,
                IntPtr.Zero,
                D3D11Interop.CreateDeviceBgraSupport,
                featureLevels,
                (uint)featureLevels.Length,
                D3D11Interop.SdkVersion,
                out _device,
                out featureLevel,
                out _context);
        }

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"D3D11CreateDevice 失败（{ComVtable.Describe(hr)}）—— 适配器「{AdapterName}」。"
                + "（带 D3D11_CREATE_DEVICE_VIDEO_SUPPORT 与不带都试过了。）";

            return false;
        }

        // 🔥 多线程保护：MF 的硬编码器 MFT 会从它自己的线程上用这个立即上下文，
        //    而我们的抓屏/转换/拷贝用的是同一个上下文。两边同时用而没开保护 ⇒ 未定义行为。
        //    官方在 IMFDXGIDeviceManager::ResetDevice 的 Remarks 里明确要求开它。
        EnableMultithreadProtection();

        if (!QueryInterface(_device, D3D11Interop.IID_ID3D11VideoDevice, out _videoDevice, "ID3D11VideoDevice", out failure))
        {
            return false;
        }

        if (!QueryInterface(_context, D3D11Interop.IID_ID3D11VideoContext, out _videoContext, "ID3D11VideoContext", out failure))
        {
            return false;
        }

        // IDXGIDevice 在"反查设备落在哪块卡上"时用得到，先拿在手里。
        if (!QueryInterface(_device, DxgiInterop.IID_IDXGIDevice, out _dxgiDevice, "IDXGIDevice", out failure))
        {
            return false;
        }

        FeatureLevel = featureLevel;

        return true;
    }

    /// <summary>
    /// 在设备上开启多线程保护（<c>ID3D10Multithread::SetMultithreadProtected(TRUE)</c>）。
    ///
    /// <para>
    /// 🔥 <b>为什么这件事非做不可</b>：这条链上<b>有两个不同的线程在碰同一个立即上下文</b> ——
    /// 我们的（抓屏 <c>CopyResource</c>、视频处理器 <c>Blt</c>），
    /// 和 MF 硬编码器 MFT 内部的（它在自己线程上读输入的 NV12 纹理）。
    /// 没开保护时 D3D11 不做任何加锁，两边同时用就是<b>未定义行为</b> ——
    /// 报出来的是 <c>E_UNEXPECTED</c> 这种没有任何信息量的 HRESULT。
    /// 官方在 <c>IMFDXGIDeviceManager::ResetDevice</c> 的 Remarks 里明确要求开它。
    /// </para>
    /// <para>
    /// ⚠️ <b>失败不等于要停</b>：某些驱动根本不暴露 <c>ID3D10Multithread</c>，
    /// 或者本来就是隐式串行的。所以这里<b>只记录、不返回失败</b> ——
    /// 但记录本身很重要：真出那种"偶发、无信息量"的编码错误时，这一格是第一个要看的。
    /// </para>
    /// </summary>
    private void EnableMultithreadProtection()
    {
        // ⚠️ `QueryInterfaceRaw` 要 `ref Guid`，而静态 readonly 字段不能按引用传 ⇒ 先拷一份。
        var iid = D3D11Interop.IID_ID3D10Multithread;

        // 从 ID3D11Device 上 QI 出 D3D10 的那个接口 —— 这不是笔误，是官方文档的做法。
        if (!ComVtable.Succeeded(ComVtable.QueryInterfaceRaw(_device, ref iid, out var multithread)))
        {
            // 拿不到就保持 false：它是"没开成"，不是"不需要开"。
            return;
        }

        try
        {
            var set = ComVtable.Bind<D3D11Interop.Delegates.SetMultithreadProtectedFn>(
                multithread, D3D11Interop.MultithreadSlots.SetMultithreadProtected);

            // ⚠️ BOOL 是 4 字节 int，不是 bool。这个调用**没有返回值可查**（返回 void），
            //    所以"设没设上"只能靠复位后自己记着 —— 也就是这一行下面的那个赋值。
            set(multithread, 1);

            MultithreadProtected = true;
        }
        catch (Exception)
        {
            MultithreadProtected = false;
        }
        finally
        {
            ComVtable.ReleasePointer(ref multithread);
        }
    }

    // ── 第三步：DXGI 设备管理器（零拷贝的接线口）──

    private bool CreateDeviceManager(out string? failure)
    {
        failure = null;

        // mfplat 必须先起（重复调用返回 S_FALSE，不是失败）。
        MediaFoundationInterop.MFStartup((int)MediaFoundationInterop.MfVersion, 0);

        var hr = MediaFoundationInterop.MFCreateDXGIDeviceManager(out var token, out _deviceManager);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"MFCreateDXGIDeviceManager 失败（{ComVtable.Describe(hr)}）。";

            return false;
        }

        ResetToken = token;

        // ⚠️⚠️ **这一格是"文档自相矛盾"，不是"文档说得很清楚"。**
        //
        //   `IMFDXGIDeviceManager::ResetDevice` 的第一参在**同一条命令的"参数表"
        //   与"备注"里指着两个不同的东西**（`mfobjects.h` 的官方文档逐字）：
        //
        //     · 参数表：`pUnkDevice` = "A pointer to the IUnknown interface of
        //       **the DXGI device**."
        //     · 备注：  "When you first create the DXGI Device Manager, call this
        //       method with a pointer to **the Direct3D device**."
        //
        //   ⇒ 不能拿"文档明写要 DXGI 设备"当作一条硬结论 —— **它同时明写了要
        //      D3D 设备**。真正把两者统一起来的机制是：设备管理器内部会拿这个
        //      `IUnknown*` 去 **QI `IDXGIDevice`**，而 `ID3D11Device` **本来就能
        //      QI 出 `IDXGIDevice`** ⇒ **两种传法都能 `S_OK`**。
        //      ⇒ 这也解释了为什么"传错"这件事在本项目里**所有用例都发现不了**：
        //        返回值根本不区分。**它不构成"零拷贝名存实亡"的已证真因** ——
        //        只是"按参数表那一句，这里该给 DXGI 那一层"。
        //
        //   改它的理由因此降级为"**对齐参数表的措辞 + 少一次内部 QI**"，
        //   而不是"修一个已知 bug"。**真正的零拷贝自检另有其法**
        //   （见 `WindowsMediaFoundationVideoEncoder.Describe()` 里
        //   `D3D 设备管理器` 那一格的取数方式）。
        //
        //   ⚠️ 另有一条与排障有关的原文："If this method succeeds, all open device
        //      handles become invalid." ⇒ **每调一次 `ResetDevice`，之前
        //      `OpenDeviceHandle` 拿到的句柄全部作废**，必须重新开。
        if (_dxgiDevice == IntPtr.Zero)
        {
            failure = "还没拿到 IDXGIDevice 就去配设备管理器了 —— " +
                      "参数表要求这里给的是 DXGI 那一层的设备。";

            return false;
        }

        var reset = ComVtable.Bind<MediaFoundationInterop.Delegates.DxgiDeviceManagerResetDeviceFn>(
            _deviceManager, MediaFoundationInterop.DxgiDeviceManagerSlots.ResetDevice);

        hr = reset(_deviceManager, _dxgiDevice, token);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"IMFDXGIDeviceManager::ResetDevice 失败（{ComVtable.Describe(hr)}）—— " +
                      "设备管理器没能接管这个 D3D11 设备，零拷贝路径建不起来。";

            return false;
        }

        // 🔥 **自检一次。** `S_OK` 上面已经拿到了，但它证明不了设备管理器手里有东西 ——
        //    `pUnkDevice` 是 `IUnknown*`（弱类型），见上面那段。
        //    `OpenDeviceHandle` 是唯一能把它证实的动作：它要开句柄就得先 QI `IDXGIDevice`。
        VerifyDeviceHandle();

        return true;
    }

    /// <summary>
    /// 🔥 <b>零拷贝那条链子的"通断测试"</b>：设备管理器能不能开出一个设备句柄。
    ///
    /// <para>
    /// 这是本项目里唯一能把「<c>ResetDevice</c> 回了 <c>S_OK</c>」与
    /// 「设备管理器手上真的有一块能用 QI 出 <c>IDXGIDevice</c> 的设备」分开的动作。
    /// 两者<b>不等价</b>：前者只说明调用没报错（<c>IUnknown*</c> 弱类型，传什么都不挑），
    /// 后者才是编码器 MFT 里 <c>MFCreateDXGISurfaceBuffer</c> 那条路能不能跑起来的前提。
    /// </para>
    /// <para>
    /// ⚠️ 开出来的句柄<b>立刻关掉</b>：它只是探针，本类不持有它
    /// （真正要用句柄的地方在编码器 MFT 内部，它自己会开）。
    /// 而且文档说 <c>ResetDevice</c> 成功会作废所有已开的句柄 ——
    /// 所以"提前开一个留着"这种做法本身就是错的。
    /// </para>
    /// <para>
    /// ⚠️ <b>失败只记录、不返回 false。</b> 理由与 <see cref="MultithreadProtected"/> 一致：
    /// 少数驱动/远程桌面场景下这条路径的行为与本地不同，把它升级成致命错误会让
    /// 本来能跑的机器直接建不起编码器。但<b>记录本身是必须的</b> ——
    /// 现场那句"零拷贝好像没生效、但没有任何报错"，第一个要看的就是这一格。
    /// </para>
    /// </summary>
    private void VerifyDeviceHandle()
    {
        try
        {
            var open = ComVtable.Bind<MediaFoundationInterop.Delegates.OpenDeviceHandleFn>(
                _deviceManager, MediaFoundationInterop.DxgiDeviceManagerSlots.OpenDeviceHandle);

            if (!ComVtable.Succeeded(open(_deviceManager, out var handle))) return;

            if (handle == IntPtr.Zero) return;

            DeviceHandleVerified = true;

            var close = ComVtable.Bind<MediaFoundationInterop.Delegates.CloseDeviceHandleFn>(
                _deviceManager, MediaFoundationInterop.DxgiDeviceManagerSlots.CloseDeviceHandle);

            // 关不掉也不影响结论（它只是个探针），但别让异常逃出去。
            _ = close(_deviceManager, handle);
        }
        catch (Exception)
        {
            DeviceHandleVerified = false;
        }
    }

    private static bool QueryInterface(
        IntPtr instance, Guid iid, out IntPtr result, string what, out string? failure)
    {
        var local = iid;

        var hr = ComVtable.QueryInterfaceRaw(instance, ref local, out result);

        if (ComVtable.Succeeded(hr))
        {
            failure = null;

            return true;
        }

        failure = $"这个设备没有 {what}（{ComVtable.Describe(hr)}）—— " +
                  "多半是驱动太旧，或者特性级别不够。";

        return false;
    }

    private D3D11GpuContext? DisposeAndReturn()
    {
        Dispose();

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        // 释放顺序刻意是"依赖者先走"：设备管理器握着设备，视图握着设备……
        // 与创建顺序相反，是 COM 的老规矩。
        ComVtable.ReleasePointer(ref _deviceManager);

        ComVtable.ReleasePointer(ref _output);
        ComVtable.ReleasePointer(ref _videoContext);
        ComVtable.ReleasePointer(ref _videoDevice);
        ComVtable.ReleasePointer(ref _dxgiDevice);
        ComVtable.ReleasePointer(ref _context);
        ComVtable.ReleasePointer(ref _device);
        ComVtable.ReleasePointer(ref _adapter);
        ComVtable.ReleasePointer(ref _factory);
    }
}
