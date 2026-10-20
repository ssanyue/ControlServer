using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// <b>BGRA → NV12 的 GPU 色彩转换</b>（M5.4 零拷贝管线中间的那一环）。
///
/// <para>
/// 硬件编码器只吃 NV12（YUV 4:2:0 半平面），而抓屏拿到的桌面是 BGRA。
/// 这一次转换<b>必须在 GPU 上做</b>：4K60 下每帧 33 MB，落到 CPU 再回显存
/// 就是 1.99 GB/s × 2 的 PCIe 流量 —— 那正是本方案要消灭的那次拷贝。
/// </para>
///
/// <para>
/// 🔥 <b>为什么用 <c>ID3D11VideoProcessor</c> 而不是自己写着色器</b>：
/// 它是 D3D11 自带的<b>专用固件/硬件路径</b>（色彩矩阵、色度下采样、范围映射全在里面），
/// 既不用引 HLSL、也不用引 <c>d3dcompiler</c>，还比通用 shader 更省电。
/// 尤其是 <b>4:4:4 → 4:2:0 的下采样</b>：自己写很容易做出带色边的结果（文字边缘出彩边），
/// 而这恰恰是「灰度模式」想解决的问题本身。
/// </para>
///
/// <para>
/// 🔥 <b>灰度模式（M5 决策 14）＝ 两趟 Blt</b>，而且<b>不是</b>当初设计时以为的那样。
///
/// <para>
/// 原设计是"把视频处理器的<b>饱和度滤镜</b>调到驱动报的最小值，一次转换就出中性色度"——
/// 这个设计<b>在本机的 Intel UHD 620 上实测不成立</b>：驱动在 <c>FilterCaps</c> 里声明支持它、
/// <c>GetVideoProcessorFilterRange</c> 报得出量程、<c>SetStreamFilter</c> 之后
/// <c>GetStreamFilter</c> 读回来<b>正是我们设的值</b> —— 而输出与"完全关掉滤镜"<b>逐字节相同</b>
/// （输入/输出 <c>Usage</c> 位、自动处理模式、设置顺序、8 个档位全试过，8 个变体输出一致）。
/// ⇒ <b>RGB 输入通路上根本不接滤镜块</b>（亮度/对比度/色相/饱和度在量纲上都是 YUV 域的东西）。
/// </para>
/// <para>
/// ⇒ 所以现在是：第一趟 <c>BGRA→NV12</c> 照常，<b>第二趟把 NV12 在 YUV 定义域上再处理一遍</b>
/// 并压掉饱和度。代价是多一次全显存 Blt（不过 PCIe），只在灰度开着时才有。
/// </para>
/// <para>
/// ⚠️ 这件事故意写在这里而不是只写进文档：**它没有任何 API 能发现**，
/// 只能靠读回像素证伪 —— 所以类里还带一个 <see cref="RunGrayscaleSelfTest"/>。
/// 下一次有人想"顺手把两趟合成一趟省点带宽"时，请先看这段。
/// </para>
/// </para>
///
/// <para>
/// ⚠️ <b>线程亲和</b>：与抓屏器一样，一个实例不是线程安全的，且
/// <c>VideoProcessorBlt</c> 提交到<b>同一个</b> <c>ID3D11DeviceContext</c> 上，
/// 那个上下文本身也不是线程安全的（D3D11 的立即上下文要外部串行化）。
/// 内部用一把锁兜底。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class D3D11Nv12Converter : IDisposable
{
    private readonly D3D11GpuContext _gpu;
    private readonly object _gate = new();

    private IntPtr _enumerator;
    private IntPtr _processor;
    private IntPtr _output;          // NV12 输出纹理（本类持有）
    private IntPtr _outputView;
    private IntPtr _inputView;
    private IntPtr _inputViewFor;    // 这个输入视图是给哪张纹理建的
    private IntPtr _mirror;          // 直连不成时的显存内中转（BGRA）
    private IntPtr _staging;         // NV12 读回用的暂存纹理

    // ── 指针叠加（M5.4-b）：指向"合成结果"那张小 BGRA 的输入视图 ──
    // ⚠️ 它是**另一张**纹理的视图，不能与 `_inputView`（指整屏源）共用同一个字段 ——
    //    共用会让下一次整屏转换拿着一个指错纹理的视图去 Blt，而驱动**不会报错**，
    //    画面是"上一帧那一块糊上去"的样子。
    private IntPtr _overlayInputView;
    private IntPtr _overlayInputViewFor;

    // ── 灰度用的第二趟（NV12 → NV12）──
    // 🔥 它单独一个处理器，是因为**颜色空间与滤镜都是挂在处理器上的状态**：
    //    复用同一个处理器就必须在每帧的两趟之间来回改状态，而那正是"某个顺序下才出错"的温床。
    private IntPtr _grayProcessor;
    private IntPtr _grayOutput;
    private IntPtr _grayOutputView;
    private IntPtr _grayInputView;   // 指向 _output

    /// <summary>最近一次成功转换的结果纹理（彩色那一趟是 <c>_output</c>，灰度是 <c>_grayOutput</c>）。</summary>
    private IntPtr _lastOutput;

    private D3D11Interop.VideoProcessorFilterRange _saturation;
    private bool _grayscaleApplied;

    /// <summary>
    /// 灰度<b>自检</b>的结果：<c>null</c> = 还没做过，<c>true</c> = 验过且真的生效，<c>false</c> = 验过且不生效。
    /// <para>详见 <see cref="RunGrayscaleSelfTest"/> 与 <see cref="GrayscaleMechanism"/>。</para>
    /// </summary>
    private bool? _grayscaleSelfTest;

    private string _grayscaleSelfTestNote = string.Empty;

    private int _grayscaleSelfTestAttempts;

    /// <summary>
    /// 自检最多试几帧。
    /// <para>
    /// 🔥 它防的是"桌面长时间是纯色"这种情形把<b>每帧</b>都拖去读回一次 ——
    /// 那是一次全帧拷贝，摊到每帧上就够把帧率吃掉。试够就收手，并如实写明结论未经证实。
    /// </para>
    /// </summary>
    private const int MaxGrayscaleSelfTestAttempts = 3;

    private bool _disposed;

    private D3D11Nv12Converter(D3D11GpuContext gpu, int sourceWidth, int sourceHeight, EncodeRegion region)
    {
        _gpu = gpu;

        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;

        Region = region;

        Width = region.Width;
        Height = region.Height;
    }

    /// <summary>
    /// 输入的像素宽（<b>源纹理</b>，通常是整块屏）。
    /// <para>
    /// 🔥 它与 <see cref="Width"/> <b>不是一回事</b>：ROI/分块时输入是整屏、输出只是其中一块。
    /// 早先两者恒等（M5.4 只有整帧一条路），把"输入尺寸"当成"输出尺寸"用的代码
    /// 在那里永远是对的 —— 所以这条区分只能靠**字段分开命名**来保证，
    /// 而不是靠"记住别写错"。
    /// </para>
    /// </summary>
    internal int SourceWidth { get; }

    /// <summary>输入的像素高（源纹理）。</summary>
    internal int SourceHeight { get; }

    /// <summary>本转换器只处理源画面里的哪一块（整帧时是 <c>(0,0,W,H)</c>）。</summary>
    internal EncodeRegion Region { get; }

    /// <summary>输出（以及输入）的像素宽。</summary>
    internal int Width { get; }

    /// <summary>输出（以及输入）的像素高。<b>NV12 要求它是偶数</b>。</summary>
    internal int Height { get; }

    /// <summary>
    /// <b>最近一次成功转换的结果纹理</b>（<c>ID3D11Texture2D*</c>）。喂给编码器用的就是它。
    ///
    /// <para>
    /// ⚠️ <b>它在灰度的两种模式下的值不同</b>：彩色是转换器自己那张 NV12，
    /// 灰度是第二趟（NV12 → NV12 重处理）那张。所以<b>必须"转一帧、立刻取、立刻用"</b>，
    /// 不能在两次 <see cref="TryConvert"/> 之间把它存下来 —— 那样会拿到上一张。
    /// </para>
    /// </summary>
    internal nint OutputTexture => _lastOutput;

    /// <summary>设备型号名（报告用）。</summary>
    internal string AdapterName => _gpu.AdapterName;

    /// <summary>
    /// 🔥 <b>这台机器能不能做灰度</b>：驱动报的饱和度量程里，最小值必须<b>真的低于</b>默认值。
    ///
    /// <para>
    /// 只看"有没有 <c>SATURATION</c> 能力位"是不够的：量程 <c>Minimum == Default</c> 意味着
    /// 这个滤镜<b>没有可调空间</b>，调过去是个空操作 —— 于是"灰度模式"会<b>看起来生效了</b>
    /// （没有报错、日志也正常）却一点变化都没有。
    /// </para>
    /// </summary>
    internal bool SupportsGrayscale => _saturation.Minimum < _saturation.Default;

    /// <summary>
    /// 灰度到底是怎么实现的（如实写进能力报告）。
    /// <para>不支持时这里会说明原因，而不是留空让人以为"忘了写"。</para>
    /// </summary>
    internal string GrayscaleMechanism { get; private set; } = "未探测";

    /// <summary>
    /// 🔥 <b>输入视图是不是靠一次显存内中转才建起来的</b>。
    ///
    /// <para>
    /// 理想情况（<c>false</c>）是直接在 DDA 给的那张纹理上建输入视图，一个字节都不搬。
    /// 但官方对输入视图的纹理有绑定标志要求，而 DDA 的纹理是<b>系统建的、我们管不着</b>：
    /// 万一不符合，就退化成"先 <c>CopyResource</c> 到我们自己的一张 BGRA 纹理"（<c>true</c>）。
    /// </para>
    /// <para>
    /// ⚠️ 这个退化的代价<b>远小于</b>零拷贝要消灭的那次拷贝（它在显存内部，不过 PCIe），
    /// 所以可以接受；但它<b>必须能被看见</b> —— 「静默退化」正是本方案反复踩的那类坑。
    /// </para>
    /// </summary>
    internal bool UsesIntermediateCopy { get; private set; }

    // ═══════════════════════════════════════════════════════════
    // 建立
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 在<b>已有</b>的设备上建一个 BGRA→NV12 转换器。
    ///
    /// <para>
    /// 🔥 <b>为什么是"传进来一个设备"而不是自己建</b>：零拷贝要求整条链同源。
    /// 输入视图必须与抓屏那张纹理同设备、输出纹理必须与编码器的设备管理器同设备 ——
    /// 自己建一个的话，两条都会无声地退化成拷贝（见 <see cref="D3D11GpuContext"/> 的类注释）。
    /// </para>
    /// </summary>
    /// <param name="width">帧宽（偶数；奇数会被 NV12 拒绝）。</param>
    /// <param name="height">帧高（偶数）。</param>
    /// <param name="failure">失败原因（给人看的一句话）。</param>
    /// <remarks>
    /// ⚠️ <b>宽高一变就得重建</b>（换分辨率 / 拔插屏）：视频处理器的内容描述里写死了尺寸，
    /// 拿旧尺寸去 Blt 一张新尺寸的纹理，表现是<b>画面被裁掉一块或整片错位</b>，而不是报错。
    /// </remarks>
    internal static D3D11Nv12Converter? TryCreate(
        D3D11GpuContext gpu, int width, int height, out string? failure)
        => TryCreate(gpu, width, height, new EncodeRegion(0, 0, width, height), out failure);

    /// <summary>
    /// 建一个<b>只处理某个子矩形</b>的转换器（M5.5 的 ROI / 分块编码）。
    ///
    /// <para>
    /// 输入是 <paramref name="sourceWidth"/>×<paramref name="sourceHeight"/> 的整屏 BGRA，
    /// 输出是 <paramref name="region"/> 大小的 NV12，<b>1:1 不缩放</b>
    /// （靠 <c>VideoProcessorSetStreamSourceRect</c> 把源矩形取成那一块）。
    /// </para>
    ///
    /// <para>
    /// 🔥 <b>为什么"不缩放"必须由源矩形实现、而不是"缩小输出"</b>：
    /// 目标是"省掉编码面积"（8K 桌面在 1080p 视口下只编 1080p 的面积），
    /// 而缩放路径编出来的仍是同一幅画面、只是更糊 —— <b>一个像素都没省</b>。
    /// </para>
    /// </summary>
    internal static D3D11Nv12Converter? TryCreate(
        D3D11GpuContext gpu, int sourceWidth, int sourceHeight, EncodeRegion region, out string? failure)
    {
        failure = null;

        if (region.Width <= 1 || region.Height <= 1)
        {
            failure = $"尺寸 {region.Width}×{region.Height} 太小，建不了视频处理器。";

            return null;
        }

        if (sourceWidth <= 1 || sourceHeight <= 1)
        {
            failure = $"源画面尺寸 {sourceWidth}×{sourceHeight} 不合法。";

            return null;
        }

        if (region.X < 0 || region.Y < 0
            || region.X + region.Width > sourceWidth
            || region.Y + region.Height > sourceHeight)
        {
            // 🔴 越界的源矩形<b>不会报错</b>：VideoProcessor 会按边界裁掉，
            //    出来的是"右边/下边少一截"的画面 —— 与"抓屏错了"长得一模一样。
            failure = $"要编的区域 {region} 超出了源画面 {sourceWidth}×{sourceHeight}。";

            return null;
        }

        // NV12 是 4:2:0 半平面：宽高都必须是偶数，否则色度平面没有整数行数。
        if (region.Width % 2 != 0 || region.Height % 2 != 0)
        {
            failure = $"尺寸 {region.Width}×{region.Height} 不是偶数 —— NV12（4:2:0 半平面）要求宽高都为偶数，"
                + "奇数尺寸下色度平面没有整数行数。";

            return null;
        }

        if (gpu.VideoDevice == IntPtr.Zero || gpu.VideoContext == IntPtr.Zero)
        {
            failure = "这个 D3D11 设备上没有视频接口（ID3D11VideoDevice / ID3D11VideoContext）——"
                + "多半是驱动太旧或跑在基础渲染器上。";

            return null;
        }

        var converter = new D3D11Nv12Converter(gpu, sourceWidth, sourceHeight, region);

        try
        {
            if (!converter.CreateEnumeratorAndProbe(out failure)) return converter.DisposeAndReturn();

            if (!converter.CreateProcessorAndViews(out failure)) return converter.DisposeAndReturn();

            return converter;
        }
        catch (Exception ex)
        {
            failure = $"建色彩转换器时抛出 {ex.GetType().Name}：{ex.Message}";

            return converter.DisposeAndReturn();
        }
    }

    private bool CreateEnumeratorAndProbe(out string? failure)
    {
        failure = null;

        // 🔥 输入/输出<b>分别</b>声明：ROI 与分块编码时输入是整屏、输出只是其中一块。
        //    ⚠️ 早先两者恒等（都传 Width/Height），那时"拿输出尺寸当输入尺寸"永远是对的 ——
        //    到了 ROI 就变成"输入声明成一小块、实际喂整屏"，而 Blt 照样回 S_OK，
        //    输出是**未定义**的（M5.4-b 第 10 步已经在指针叠加那条路上踩过同一个坑）。
        var contentDesc = D3D11Interop.VideoProcessorContentDesc.For(
            (uint)SourceWidth, (uint)SourceHeight, (uint)Width, (uint)Height, 60, 1);

        var createEnum = ComVtable.Bind<D3D11Interop.Delegates.CreateVideoProcessorEnumeratorFn>(
            _gpu.VideoDevice, D3D11Interop.VideoDeviceSlots.CreateVideoProcessorEnumerator);

        var hr = createEnum(_gpu.VideoDevice, ref contentDesc, out _enumerator);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"CreateVideoProcessorEnumerator 失败（{ComVtable.Describe(hr)}）——"
                + $"尺寸 {Width}×{Height}。这一组尺寸/帧率超出了视频处理器能接受的组合。";

            return false;
        }

        // ── 先探格式，再建处理器 ──
        // 🔥 顺序很重要：探测要在"什么都没建"的时候做，失败时就能干净地回一句人话。
        //    若先建处理器、再去 Blt，失败会发生在每帧的循环里 —— 那是最难定位的位置。
        if (!CheckFormat(DxgiInterop.FormatB8G8R8A8Unorm, "BGRA8 输入", out var inFlags, out failure))
        {
            return false;
        }

        if ((inFlags & D3D11Interop.VideoProcessorFormatSupportInput) == 0)
        {
            failure = "视频处理器<b>不支持把 BGRA8 当输入</b>（它只把它当输出）。"
                + "桌面的原生格式就是 BGRA8 —— 这条路走不通就得改用 GDI / MJPEG 降级路径。";

            return false;
        }

        if (!CheckFormat(DxgiInterop.FormatNv12, "NV12 输出", out var outFlags, out failure)) return false;

        if ((outFlags & D3D11Interop.VideoProcessorFormatSupportOutput) == 0)
        {
            failure = "视频处理器<b>不支持把 NV12 当输出</b>。NV12 是所有硬件编码器的输入格式，"
                + "没有它就只能软编（4K60 达不到）。";

            return false;
        }

        // NV12 能不能当**输入**：灰度那条"NV12 → NV12 重处理"的路要用它。
        // 与上一条独立 —— 一个格式可以只支持输出而不支持输入。
        Nv12InputSupported = (outFlags & D3D11Interop.VideoProcessorFormatSupportInput) != 0;

        // ── 饱和度滤镜：灰度模式的唯一落点 ──
        var getCaps = ComVtable.Bind<D3D11Interop.Delegates.GetVideoProcessorCapsFn>(
            _enumerator, D3D11Interop.VideoProcessorEnumeratorSlots.GetVideoProcessorCaps);

        if (!ComVtable.Succeeded(getCaps(_enumerator, out var caps)))
        {
            failure = "读不到视频处理器能力（GetVideoProcessorCaps 失败）。没有它就无法判断灰度能不能做，"
                + "而「猜一个」正是本方案明令禁止的。";

            return false;
        }

        FilterCaps = caps.FilterCaps;
        MaxInputStreams = caps.MaxInputStreams;
        MaxStreamStates = caps.MaxStreamStates;

        if ((caps.FilterCaps & D3D11Interop.FilterCapsSaturation) == 0)
        {
            // ⚠️ 这里**不是**失败：彩色转换照样能做，只是灰度模式不可用。
            //    能力报告里必须把这件事说清楚，而不是安静地把"灰度开关"做成一个摆设。
            GrayscaleMechanism = "不可用：本机视频处理器不支持饱和度滤镜（FilterCaps 里没有 SATURATION 位）。";

            _saturation = default;
        }
        else
        {
            var getRange = ComVtable.Bind<D3D11Interop.Delegates.GetVideoProcessorFilterRangeFn>(
                _enumerator, D3D11Interop.VideoProcessorEnumeratorSlots.GetVideoProcessorFilterRange);

            var rangeHr = getRange(_enumerator, D3D11Interop.FilterSaturation, out _saturation);

            if (!ComVtable.Succeeded(rangeHr))
            {
                GrayscaleMechanism = $"不可用：读不到饱和度量程（{ComVtable.Describe(rangeHr)}）。";

                _saturation = default;
            }
            else if (!SupportsGrayscale)
            {
                GrayscaleMechanism =
                    $"不可用：饱和度量程是 {_saturation.Minimum}..{_saturation.Maximum}（默认 {_saturation.Default}），"
                    + "最小值不低于默认值 ⇒ 这个滤镜没有可调空间，调到哪儿都是原样。";

                _saturation = default;
            }
            else
            {
                GrayscaleMechanism =
                    $"可用（首次使用时会自证）：驱动声明支持饱和度滤镜，"
                    + $"量程 {_saturation.Minimum}..{_saturation.Maximum}（默认 {_saturation.Default}）。"
                    + "实现＝**两趟 Blt**：第一趟 BGRA→NV12 照常，第二趟把 NV12 在 **YUV 域**上重处理一遍"
                    + $"并把饱和度压到最小（{_saturation.Minimum}）⇒ 色度成为常量 128。"
                    + "⚠️ 为什么不合成一趟：实测至少有一款驱动（Intel UHD 620）在 **RGB 输入通路**上"
                    + "接受并保存这个滤镜、读回来也对，却**完全不执行它**（8 个变体输出逐字节相同）。";
            }
        }

        return true;
    }

    private bool CheckFormat(int format, string what, out uint flags, out string? failure)
    {
        failure = null;

        var check = ComVtable.Bind<D3D11Interop.Delegates.CheckVideoProcessorFormatFn>(
            _enumerator, D3D11Interop.VideoProcessorEnumeratorSlots.CheckVideoProcessorFormat);

        var hr = check(_enumerator, format, out flags);

        if (!ComVtable.Succeeded(hr))
        {
            flags = 0;

            failure = $"探测视频处理器对 {what} 的支持时失败（{ComVtable.Describe(hr)}）。";

            return false;
        }

        return true;
    }

    private bool CreateProcessorAndViews(out string? failure)
    {
        failure = null;

        var createProcessor = ComVtable.Bind<D3D11Interop.Delegates.CreateVideoProcessorFn>(
            _gpu.VideoDevice, D3D11Interop.VideoDeviceSlots.CreateVideoProcessor);

        // 第 2 个参数是"用哪一组帧率转换能力"的下标；0 = 不转换（我们不做插帧/抽帧）。
        var hr = createProcessor(_gpu.VideoDevice, _enumerator, 0, out _processor);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"CreateVideoProcessor 失败（{ComVtable.Describe(hr)}）。";

            return false;
        }

        // ── 输出纹理：NV12，必须带 RENDER_TARGET ──
        // 🔥 官方对视频处理器输出视图的要求就是 D3D11_BIND_RENDER_TARGET。
        //    漏了它 CreateVideoProcessorOutputView 会回一个不带解释的 E_INVALIDARG。
        var outputDesc = D3D11Interop.Texture2DDesc.Plain(
            (uint)Width, (uint)Height, DxgiInterop.FormatNv12, D3D11Interop.BindRenderTarget);

        var createTexture = ComVtable.Bind<D3D11Interop.Delegates.CreateTexture2DFn>(
            _gpu.Device, D3D11Interop.DeviceSlots.CreateTexture2D);

        hr = createTexture(_gpu.Device, ref outputDesc, IntPtr.Zero, out _output);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"建 NV12 输出纹理失败（{ComVtable.Describe(hr)}）—— {Width}×{Height}。";

            return false;
        }

        var outputViewDesc = new D3D11Interop.VideoProcessorOutputViewDesc
        {
            ViewDimension = D3D11Interop.VpovDimensionTexture2D,
            Texture2DArray = new D3D11Interop.Tex2dArrayVpov { ArraySlice = 0, MipSlice = 0 },
        };

        var createOutputView = ComVtable.Bind<D3D11Interop.Delegates.CreateVideoProcessorOutputViewFn>(
            _gpu.VideoDevice, D3D11Interop.VideoDeviceSlots.CreateVideoProcessorOutputView);

        hr = createOutputView(_gpu.VideoDevice, _output, _enumerator, ref outputViewDesc, out _outputView);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"CreateVideoProcessorOutputView 失败（{ComVtable.Describe(hr)}）—— "
                + "NV12 纹理多半缺了 D3D11_BIND_RENDER_TARGET。";

            return false;
        }

        // ── 三个状态设一次就够（它们挂在处理器上，Blt 时读取）──
        SetInitialProcessorState();

        return true;
    }

    private void SetInitialProcessorState()
    {
        var setStreamFrameFormat = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorSetStreamFrameFormatFn>(
            _gpu.VideoContext, D3D11Interop.VideoContextSlots.VideoProcessorSetStreamFrameFormat);

        setStreamFrameFormat(_gpu.VideoContext, _processor, 0, D3D11Interop.VideoFrameFormatProgressive);

        var setStreamColorSpace = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorSetStreamColorSpaceFn>(
            _gpu.VideoContext, D3D11Interop.VideoContextSlots.VideoProcessorSetStreamColorSpace);

        var inputSpace = D3D11Interop.VideoProcessorColorSpace.RgbFullRangeInput;

        setStreamColorSpace(_gpu.VideoContext, _processor, 0, ref inputSpace);

        var setOutputColorSpace = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorSetOutputColorSpaceFn>(
            _gpu.VideoContext, D3D11Interop.VideoContextSlots.VideoProcessorSetOutputColorSpace);

        var outputSpace = D3D11Interop.VideoProcessorColorSpace.YCbCr709LimitedOutput;

        setOutputColorSpace(_gpu.VideoContext, _processor, ref outputSpace);

        // 🔥 **基准矩形**（M5.5）：只编一块时，"输入取哪一块、写到输出的哪里"是这条管线的
        //    <b>常态</b>，而不是某一次调用的临时参数 —— 所以它必须和帧格式、色彩空间一样
        //    在建的时候设好，并且在<b>每次指针叠加之后复原</b>（见 RestoreBaseRects）。
        //    ⚠️ 整帧模式下一个矩形都不设：那是 M5.4 以来的既有行为，一个字节都不许变。
        RestoreBaseRects();
    }

    /// <summary>
    /// 本转换器是不是"只编一块"（区域严格小于源画面）。
    /// </summary>
    private bool HasBaseRects
        => Region.X != 0 || Region.Y != 0
            || Region.Width != SourceWidth || Region.Height != SourceHeight;

    /// <summary>
    /// 把源/目标矩形设回<b>基准状态</b>（不是"清空"）。
    ///
    /// <para>
    /// 🔥 <b>为什么叫"复原"而不是"复位"</b>：整帧模式下基准状态确实是"不设矩形"
    /// （＝整个输入映射到整个输出）；而只编一块时基准状态是"源取 region、目标写满输出"。
    /// 早先这里只有"清空"这一种语义（那时只有整帧一条路），
    /// 直接拿去做 ROI 会让<b>指针叠加之后的那一帧整幅被压进一个小输出</b> ——
    /// 画面看起来是"缩成了一小条"，且没有任何报错。
    /// </para>
    /// </summary>
    private void RestoreBaseRects()
    {
        // output target rect 恒为"不设"：整帧那一趟要求它处于"＝整个输出"的状态，
        // 而只编一块时我们<b>就是要整个输出</b>（那一块已经是全部内容了）。
        var unused = default(DxgiInterop.Rect);

        var target = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorSetOutputTargetRectFn>(
            _gpu.VideoContext, D3D11Interop.VideoContextSlots.VideoProcessorSetOutputTargetRect);

        target(_gpu.VideoContext, _processor, 0, ref unused);

        var source = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorSetStreamRectFn>(
            _gpu.VideoContext, D3D11Interop.VideoContextSlots.VideoProcessorSetStreamSourceRect);

        var destination = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorSetStreamRectFn>(
            _gpu.VideoContext, D3D11Interop.VideoContextSlots.VideoProcessorSetStreamDestRect);

        if (!HasBaseRects)
        {
            source(_gpu.VideoContext, _processor, 0, 0, ref unused);
            destination(_gpu.VideoContext, _processor, 0, 0, ref unused);

            return;
        }

        var from = new DxgiInterop.Rect
        {
            Left = Region.X,
            Top = Region.Y,
            Right = Region.X + Region.Width,
            Bottom = Region.Y + Region.Height,
        };

        var to = new DxgiInterop.Rect { Left = 0, Top = 0, Right = Width, Bottom = Height };

        source(_gpu.VideoContext, _processor, 0, 1, ref from);
        destination(_gpu.VideoContext, _processor, 0, 1, ref to);
    }

    // ═══════════════════════════════════════════════════════════
    // 转换
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把一张 BGRA 纹理转成 NV12，结果在 <see cref="OutputTexture"/> 里。
    ///
    /// <para>
    /// 🔥 <b>不抛异常，返回 <c>false</c> + 原因</b>：本方法会被每帧调用，
    /// 从这个位置逃出去一个异常，整条推流循环就<b>静默死掉</b>（与 <c>IVideoEncoder.TryEncode</c> 同一个理由）。
    /// </para>
    /// </summary>
    /// <param name="bgraTexture">
    /// 源纹理（<c>ID3D11Texture2D*</c>，DXGI Desktop Duplication 给的那张就行）。
    /// <b>调用方持有，本方法不释放它。</b>
    /// </param>
    /// <param name="grayscale">
    /// 请求灰度。<b>本机不支持时会失败并说明原因，绝不安静地出一张彩色图</b> ——
    /// "开关拨了、什么都没变"比"开关不可用"难查得多。
    /// </param>
    public bool TryConvert(nint bgraTexture, bool grayscale, out string? failure)
    {
        lock (_gate)
        {
            failure = null;

            if (_disposed)
            {
                failure = "转换器已经释放了。";

                return false;
            }

            if (bgraTexture == IntPtr.Zero)
            {
                failure = "源纹理是空指针。";

                return false;
            }

            if (grayscale && !EnsureGrayscaleSecondPass(out failure)) return false;

            // 🔥 第一次要灰度时先**自证**一次 —— 见 RunGrayscaleSelfTest 的注释。
            if (grayscale && _grayscaleSelfTest is null && !RunGrayscaleSelfTest(bgraTexture, out failure))
            {
                return false;
            }

            if (grayscale && _grayscaleSelfTest is false)
            {
                failure = _grayscaleSelfTestNote;

                return false;
            }

            return TryConvertInternal(bgraTexture, grayscale, out failure);
        }
    }

    /// <summary>
    /// 🔥 <b>灰度必须自证，不能只信驱动的声明</b>（本批最贵的一条教训）。
    ///
    /// <para>
    /// 在这块 Intel UHD 620 上实测到：驱动 <c>FilterCaps</c> 声明支持饱和度滤镜、
    /// 量程报得出来、<c>SetStreamFilter</c> 设进去之后 <c>GetStreamFilter</c>
    /// 读回来<b>正是我们设的值</b> —— 而输出与"完全关掉滤镜"<b>逐字节相同</b>。
    /// 把输入/输出颜色空间的 <c>Usage</c> 位、自动处理模式、设置顺序、量程上 8 个档位
    /// 全试过一遍，<b>8 个变体输出完全一致</b>。
    /// </para>
    /// <para>
    /// ⇒ <b>没有任何 API 能发现这件事</b>，只能靠"读回像素"证伪。
    /// 不去证的话，灰度开关在那种机器上就是一个<b>按了没反应、而日志上一切正常</b>的按钮。
    /// </para>
    /// <para>
    /// 代价：<b>每个转换器实例一次</b>，且只在"第一次真的要灰度"的那一帧上做。
    /// 从不开灰度的机器一分钱都不花。
    /// </para>
    /// </summary>
    private bool RunGrayscaleSelfTest(nint bgraTexture, out string? failure)
    {
        failure = null;

        // 这一帧本来就没有颜色 ⇒ 区分不了"滤波生效"与"滤波是空操作"。
        // ⚠️ **不假装通过、也不误判失败**：记成"未判定"，下一次再试；试够几次就如实收手。
        if (_grayscaleSelfTestAttempts >= MaxGrayscaleSelfTestAttempts)
        {
            _grayscaleSelfTest = true;
            _grayscaleSelfTestNote =
                $"自检未判定：连续 {MaxGrayscaleSelfTestAttempts} 帧都没有颜色，"
                + "无法区分「滤波真的生效」与「滤波是空操作」—— 但既然画面本来就没有颜色，"
                + "两者输出的结果本来就一样，因此按可用处理（此结论**未经证实**）。";

            return true;
        }

        _grayscaleSelfTestAttempts++;

        if (!EnsureInputView(bgraTexture, out failure)) return false;

        if (!BlitFirstPass(out failure)) return false;

        if (!TryReadPlanes(_output, out _, out var colorUv, out failure)) return false;

        if (!BlitGrayscaleSecondPass(out failure)) return false;

        if (!TryReadPlanes(_grayOutput, out _, out var grayUv, out failure)) return false;

        var colorVariation = StdDev(colorUv);
        var grayVariation = StdDev(grayUv);

        if (grayVariation == 0)
        {
            _grayscaleSelfTest = true;
            _grayscaleSelfTestNote =
                $"自检通过：彩色基准色度标准差 {colorVariation:F2}，灰度输出 {grayVariation:F2}（常量）。";

            return true;
        }

        if (colorVariation == 0)
        {
            failure = null;

            // 保持 null（未判定），下一次继续试。
            return true;
        }

        _grayscaleSelfTest = false;

        _grayscaleSelfTestNote =
            $"灰度自检未通过：这一帧彩色模式下色度标准差是 {colorVariation:F2}（确有颜色），"
            + $"灰度模式下仍然是 {grayVariation:F2}。"
            + "说明饱和度滤镜被驱动**接受但未执行** —— 状态读回来是对的，输出却完全没变。"
            + "这台机器上灰度不可用，请改回彩色（或走 GDI / MJPEG 降级路径）。";

        failure = _grayscaleSelfTestNote;

        return false;
    }

    /// <summary>一个平面上所有字节的标准差（"这个平面是不是常量"的判据）。</summary>
    private static double StdDev(byte[] plane)
    {
        if (plane.Length == 0) return 0;

        long sum = 0;

        foreach (var b in plane) sum += b;

        var mean = (double)sum / plane.Length;

        double variance = 0;

        foreach (var b in plane)
        {
            var d = b - mean;

            variance += d * d;
        }

        return Math.Sqrt(variance / plane.Length);
    }

    /// <summary>转换本体。</summary>
    private bool TryConvertInternal(nint bgraTexture, bool grayscale, out string? failure)
    {
        lock (_gate)
        {
            failure = null;

            if (_disposed)
            {
                failure = "转换器已经释放了。";

                return false;
            }

            if (bgraTexture == IntPtr.Zero)
            {
                failure = "源纹理是空指针。";

                return false;
            }

            if (!EnsureInputView(bgraTexture, out failure)) return false;

            if (!BlitFirstPass(out failure)) return false;

            return !grayscale || BlitGrayscaleSecondPass(out failure);
        }
    }

    /// <summary>
    /// 保证手上有一个"指向源纹理"的输入视图。
    ///
    /// <para>
    /// 🔥 <b>必须按纹理句柄缓存</b>：DDA 只要不重建，给的永远是同一张纹理，于是视图也只该建一次 ——
    /// 每帧重建视图会在几十秒内把显存碎片吃干净。而一旦 duplication 重建（换分辨率 / ACCESS_LOST），
    /// 句柄就变了，这时<b>必须</b>重新建：拿旧视图去 Blt 新纹理，驱动不会报错，
    /// 画面是<b>旧的</b>（看起来像"卡住了"）。
    /// </para>
    /// </summary>
    private bool EnsureInputView(nint texture, out string? failure)
    {
        failure = null;

        if (_inputView != IntPtr.Zero && _inputViewFor == texture) return true;

        ReleaseInputView();

        if (TryCreateInputView(texture, out failure))
        {
            _inputViewFor = texture;

            return true;
        }

        // ── 直连失败 ⇒ 显存内中转 ──
        // 记住第一次失败的原因：如果连中转也失败，要报的是"为什么会走到这一步"，而不是最后那句。
        var directReason = failure;

        if (!EnsureMirror(out failure)) return false;

        var copy = ComVtable.Bind<D3D11Interop.Delegates.CopyResourceFn>(
            _gpu.Context, D3D11Interop.ContextSlots.CopyResource);

        copy(_gpu.Context, _mirror, texture);

        if (!TryCreateInputView(_mirror, out failure))
        {
            failure = $"在源纹理上建输入视图失败（{directReason}），"
                + $"改用显存内中转之后仍然失败（{failure}）。";

            return false;
        }

        UsesIntermediateCopy = true;
        _inputViewFor = texture;

        return true;
    }

    private bool TryCreateInputView(nint texture, out string? failure)
    {
        var ok = TryCreateInputView(texture, out var view, out failure);

        _inputView = view;

        return ok;
    }

    /// <summary>
    /// 造一个指向 <paramref name="texture"/> 的输入视图，但<b>不</b>写进 <c>_inputView</c>
    /// —— 指针叠加那条路用的是另一张纹理，共用一个字段会让整屏那一趟拿着指错的视图去 Blt
    /// （驱动不报错，画面是"上一帧那一块糊上去"）。
    /// </summary>
    private bool TryCreateInputView(nint texture, out nint view, out string? failure)
    {
        failure = null;

        var desc = new D3D11Interop.VideoProcessorInputViewDesc
        {
            FourCC = 0,
            ViewDimension = D3D11Interop.VpivDimensionTexture2D,
            Texture2D = new D3D11Interop.Tex2dVpiv { ArraySlice = 0 },
        };

        var create = ComVtable.Bind<D3D11Interop.Delegates.CreateVideoProcessorInputViewFn>(
            _gpu.VideoDevice, D3D11Interop.VideoDeviceSlots.CreateVideoProcessorInputView);

        var hr = create(_gpu.VideoDevice, texture, _enumerator, ref desc, out view);

        if (ComVtable.Succeeded(hr)) return true;

        view = IntPtr.Zero;

        failure = $"{ComVtable.Describe(hr)}";

        return false;
    }

    private bool EnsureMirror(out string? failure)
    {
        failure = null;

        if (_mirror != IntPtr.Zero) return true;

        // ⚠️ 中转纹理刻意 **BindFlags = 0**：官方对输入视图的规定里，
        //    "任何含 DECODER / VIDEO_ENCODER / RENDER_TARGET / UNORDERED_ACCESS 的组合"和
        //    "完全不带绑定标志"都合法。既然目的是让它比 DDA 那张纹理更好建视图，就取最宽松的那一种。
        var desc = D3D11Interop.Texture2DDesc.Plain(
            (uint)SourceWidth, (uint)SourceHeight, DxgiInterop.FormatB8G8R8A8Unorm, bindFlags: 0);

        var create = ComVtable.Bind<D3D11Interop.Delegates.CreateTexture2DFn>(
            _gpu.Device, D3D11Interop.DeviceSlots.CreateTexture2D);

        var hr = create(_gpu.Device, ref desc, IntPtr.Zero, out _mirror);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"建中转用的 BGRA 纹理失败（{ComVtable.Describe(hr)}）—— {Width}×{Height}。";

            _mirror = IntPtr.Zero;

            return false;
        }

        return true;
    }

    /// <summary>
    /// 保证灰度那条路已经就绪：<b>建第二趟（NV12 → NV12）所需要的一切</b>。
    ///
    /// <para>
    /// 🔥🔥 <b>为什么灰度要分成两趟 —— 这是真机实测逼出来的</b>：
    /// 最直觉的做法是"在第一趟（BGRA → NV12）上挂一个饱和度滤镜"，一路 Blt 搞定。
    /// 文档这么写，驱动也这么报（<c>FilterCaps</c> 里 <c>SATURATION</c> 位是置着的，
    /// 量程也报得出来），<b>设进去再读回来也确实是我们要的值</b> ——
    /// 但输出与"完全关掉滤镜"<b>逐字节相同</b>。
    /// </para>
    /// <para>
    /// 实测把能想到的变量都试遍了（输入 / 输出颜色空间的 <c>Usage</c> 位、
    /// 自动处理模式、滤镜与颜色空间的先后顺序、量程上 8 个不同档位），
    /// <b>8 个变体输出全部逐字节相同</b>。⇒ 结论是这条通路在 RGB 输入上就没接滤镜块
    /// （亮度/对比度/色相/饱和度量纲上都是 YUV 域的东西，驱动对 RGB 输入走的是另一条转换路径）。
    /// </para>
    /// <para>
    /// ⇒ 于是改成：<b>第一趟照常做 RGB → NV12</b>，需要灰度时<b>再拿它的结果做一趟 NV12 → NV12</b>。
    /// 这一趟的输入是 YUV，滤镜才落在它的自然定义域上。
    /// 代价是多一次全显存的 Blt（1440p 约 1 ms 量级、不过 PCIe），<b>只在灰度开着时才有</b>。
    /// </para>
    /// <para>
    /// ⚠️ 它<b>单独一个处理器</b>：颜色空间与滤镜都是挂在处理器上的状态，
    /// 复用同一个就得在每帧的两趟之间来回改，而那正是"某个顺序下才出错"的温床。
    /// </para>
    /// </summary>
    private bool EnsureGrayscaleSecondPass(out string? failure)
    {
        failure = null;

        if (_grayProcessor != IntPtr.Zero) return true;

        if (!SupportsGrayscale)
        {
            failure = $"要开灰度，但{GrayscaleMechanism}";

            return false;
        }

        if (!Nv12InputSupported)
        {
            failure = "要开灰度，但本机视频处理器不支持把 NV12 当输入 —— "
                + "灰度的实现是「转完之后对 NV12 再做一趟重处理」，这条路走不通。";

            return false;
        }

        // ── ① 第二个处理器 ──
        var createProcessor = ComVtable.Bind<D3D11Interop.Delegates.CreateVideoProcessorFn>(
            _gpu.VideoDevice, D3D11Interop.VideoDeviceSlots.CreateVideoProcessor);

        var hr = createProcessor(_gpu.VideoDevice, _enumerator, 0, out _grayProcessor);

        if (!ComVtable.Succeeded(hr))
        {
            _grayProcessor = IntPtr.Zero;

            failure = $"建灰度用的第二个视频处理器失败（{ComVtable.Describe(hr)}）。";

            return false;
        }

        // ── ② 这一趟的状态：输入是 YCbCr、输出也是 YCbCr，饱和度拉到最小 ──
        var setFrameFormat = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorSetStreamFrameFormatFn>(
            _gpu.VideoContext, D3D11Interop.VideoContextSlots.VideoProcessorSetStreamFrameFormat);

        setFrameFormat(_gpu.VideoContext, _grayProcessor, 0, D3D11Interop.VideoFrameFormatProgressive);

        var setStreamColorSpace = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorSetStreamColorSpaceFn>(
            _gpu.VideoContext, D3D11Interop.VideoContextSlots.VideoProcessorSetStreamColorSpace);

        // ⚠️ 输入这里必须是 YCbCr 那一套（不是第一趟那个 RGB 全范围）。
        var inputSpace = D3D11Interop.VideoProcessorColorSpace.YCbCr709LimitedInput;

        setStreamColorSpace(_gpu.VideoContext, _grayProcessor, 0, ref inputSpace);

        var setOutputColorSpace = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorSetOutputColorSpaceFn>(
            _gpu.VideoContext, D3D11Interop.VideoContextSlots.VideoProcessorSetOutputColorSpace);

        var outputSpace = D3D11Interop.VideoProcessorColorSpace.YCbCr709LimitedOutput;

        setOutputColorSpace(_gpu.VideoContext, _grayProcessor, ref outputSpace);

        var setFilter = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorSetStreamFilterFn>(
            _gpu.VideoContext, D3D11Interop.VideoContextSlots.VideoProcessorSetStreamFilter);

        setFilter(
            _gpu.VideoContext, _grayProcessor, 0, D3D11Interop.FilterSaturation, 1, _saturation.Minimum);

        // 🔥 这两条必须显式关掉。
        //    它们不是"本来就关着"：本机驱动报的 EdgeEnhancement **默认值是 44 / 64**。
        //    而它们是作用在**亮度**上的 —— 不关的话，灰度那一趟的亮度会与彩色那趟对不上
        //    （真机实测最大差 5，而"灰度只该动色度"是本方案的既定语义）。
        setFilter(_gpu.VideoContext, _grayProcessor, 0, D3D11Interop.FilterNoiseReduction, 0, 0);
        setFilter(_gpu.VideoContext, _grayProcessor, 0, D3D11Interop.FilterEdgeEnhancement, 0, 0);

        // ── ③ 输出纹理 + 输出视图 ──
        var outputDesc = D3D11Interop.Texture2DDesc.Plain(
            (uint)Width, (uint)Height, DxgiInterop.FormatNv12, D3D11Interop.BindRenderTarget);

        var createTexture = ComVtable.Bind<D3D11Interop.Delegates.CreateTexture2DFn>(
            _gpu.Device, D3D11Interop.DeviceSlots.CreateTexture2D);

        hr = createTexture(_gpu.Device, ref outputDesc, IntPtr.Zero, out _grayOutput);

        if (!ComVtable.Succeeded(hr))
        {
            _grayOutput = IntPtr.Zero;

            failure = $"建灰度输出纹理失败（{ComVtable.Describe(hr)}）—— {Width}×{Height}。";

            return false;
        }

        var outputViewDesc = new D3D11Interop.VideoProcessorOutputViewDesc
        {
            ViewDimension = D3D11Interop.VpovDimensionTexture2D,
            Texture2DArray = new D3D11Interop.Tex2dArrayVpov { ArraySlice = 0, MipSlice = 0 },
        };

        var createOutputView = ComVtable.Bind<D3D11Interop.Delegates.CreateVideoProcessorOutputViewFn>(
            _gpu.VideoDevice, D3D11Interop.VideoDeviceSlots.CreateVideoProcessorOutputView);

        hr = createOutputView(
            _gpu.VideoDevice, _grayOutput, _enumerator, ref outputViewDesc, out _grayOutputView);

        if (!ComVtable.Succeeded(hr))
        {
            _grayOutputView = IntPtr.Zero;

            failure = $"建灰度输出视图失败（{ComVtable.Describe(hr)}）。";

            return false;
        }

        // ── ④ 输入视图：指向**第一趟的结果** ──
        // ⚠️ 它是长期有效的（_output 由本类持有、尺寸固定），所以不像第一趟那样要跟着源纹理句柄走。
        var inputViewDesc = new D3D11Interop.VideoProcessorInputViewDesc
        {
            FourCC = 0,
            ViewDimension = D3D11Interop.VpivDimensionTexture2D,
            Texture2D = new D3D11Interop.Tex2dVpiv { ArraySlice = 0 },
        };

        var createInputView = ComVtable.Bind<D3D11Interop.Delegates.CreateVideoProcessorInputViewFn>(
            _gpu.VideoDevice, D3D11Interop.VideoDeviceSlots.CreateVideoProcessorInputView);

        hr = createInputView(_gpu.VideoDevice, _output, _enumerator, ref inputViewDesc, out _grayInputView);

        if (!ComVtable.Succeeded(hr))
        {
            _grayInputView = IntPtr.Zero;

            failure = $"建灰度的输入视图失败（{ComVtable.Describe(hr)}）—— 它是第一趟那张 NV12 纹理。";

            return false;
        }

        return true;
    }

    // ═══════════════════════════════════════════════════════════
    // 诊断专用（生产路径不用；给"为什么灰度没生效"这类问题做隔离）
    // ═══════════════════════════════════════════════════════════

    /// <summary>驱动报的视频处理器滤镜能力位（<c>D3D11_VIDEO_PROCESSOR_FILTER_CAPS_*</c> 的组合）。</summary>
    internal uint FilterCaps { get; private set; }

    /// <summary>驱动报的"一路处理器最多几路输入流"（<c>MaxInputStreams</c>）。</summary>
    internal uint MaxInputStreams { get; private set; }

    /// <summary>
    /// 驱动报的"最多几个 stream state"（<c>MaxStreamStates</c>）。
    /// <para>
    /// 🔥 这条是 §3.3.1 里"VideoProcessor 多流方案（候选 C）不可行"的<b>硬证据</b>：
    /// 方案原文写的是"在很多驱动上为 1"——那是**按常理**，不是实测。
    /// 现在把它读出来了（第 5 步开工时的第一件事）。即便本机读到 ≥2，
    /// C 依然**做不了**：它只会 <c>src α over dst</c>，而指针的 <c>MASKED_COLOR</c> 要逐位 XOR、
    /// <c>MONOCHROME</c> 要 <c>(dst AND and) XOR xor</c>（见 <see cref="PointerCompositor"/>）。
    /// </para>
    /// </summary>
    internal uint MaxStreamStates { get; private set; }

    /// <summary>
    /// 灰度自检的结果：<c>null</c> = 还没做过 / 未能判定，<c>true</c> = 验过且真的生效，<c>false</c> = 验过且不生效。
    /// <para>它是<b>唯一能说明"灰度真能用"的东西</b> —— 驱动声明的能力位不算（见 <see cref="RunGrayscaleSelfTest"/>）。</para>
    /// </summary>
    internal bool? GrayscaleSelfTest => _grayscaleSelfTest;

    /// <summary>自检那句话（给人看的）。</summary>
    internal string GrayscaleSelfTestNote => _grayscaleSelfTestNote;

    /// <summary>视频处理器支不支持把 NV12 当<b>输入</b>（"NV12 → NV12 重处理"那条路的前提）。</summary>
    internal bool Nv12InputSupported { get; private set; }

    /// <summary>驱动报的某个滤镜量程（<b>不经过任何加工</b>）。</summary>
    internal bool TryGetFilterRangeRaw(
        int filter, out int minimum, out int maximum, out int def, out float multiplier)
    {
        minimum = 0;
        maximum = 0;
        def = 0;
        multiplier = 0;

        if (_enumerator == IntPtr.Zero) return false;

        var getRange = ComVtable.Bind<D3D11Interop.Delegates.GetVideoProcessorFilterRangeFn>(
            _enumerator, D3D11Interop.VideoProcessorEnumeratorSlots.GetVideoProcessorFilterRange);

        if (!ComVtable.Succeeded(getRange(_enumerator, filter, out var range))) return false;

        minimum = range.Minimum;
        maximum = range.Maximum;
        def = range.Default;
        multiplier = range.Multiplier;

        return true;
    }

    /// <summary>
    /// 把驱动<b>实际存下来</b>的饱和度滤镜状态读回来。
    ///
    /// <para>
    /// 🔥 它是"灰度没生效"这类问题的<b>分水岭</b>：
    /// 读回来等于我们设的值 ⇒ 调用生效了，问题在驱动/硬件路径上；
    /// 读回来不是 ⇒ 这次调用被忽略了，问题在我们的参数或槽位上。
    /// </para>
    /// </summary>
    /// <returns><c>null</c> = 读失败（连读都读不成，说明槽位或处理器有问题）。</returns>
    internal (bool Enabled, int Level)? TryGetSaturationRaw()
    {
        lock (_gate)
        {
            if (_disposed || _processor == IntPtr.Zero || _gpu.VideoContext == IntPtr.Zero) return null;

            var getFilter = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorGetStreamFilterFn>(
                _gpu.VideoContext, D3D11Interop.VideoContextSlots.VideoProcessorGetStreamFilter);

            getFilter(_gpu.VideoContext, _processor, 0, D3D11Interop.FilterSaturation, out var enabled, out var level);

            return (enabled != 0, level);
        }
    }

    /// <summary>
    /// <b>诊断专用</b>：直接用原始值设饱和度，绕过"支持 / 不支持"的判断。
    /// <para>用它做<b>量程扫掠</b>，才能回答"是不是只有某个取值才有效"。</para>
    /// </summary>
    internal void SetSaturationRaw(bool enable, int level)
    {
        lock (_gate)
        {
            if (_disposed || _processor == IntPtr.Zero) return;

            var setFilter = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorSetStreamFilterFn>(
                _gpu.VideoContext, D3D11Interop.VideoContextSlots.VideoProcessorSetStreamFilter);

            setFilter(_gpu.VideoContext, _processor, 0, D3D11Interop.FilterSaturation, enable ? 1 : 0, level);

            _grayscaleApplied = enable;
        }
    }

    /// <summary>
    /// <b>诊断专用</b>：用原始位值重设输入 / 输出的颜色空间。
    /// <para>
    /// 🔥 存在的理由：官方文档对那几位的 <c>Usage</c> 写着"设备可以据此<b>优化处理方式</b>"，
    /// 而"优化"在滤镜上的表现恰恰可能就是<b>把滤镜整段跳过</b>。
    /// 要验证这件事，就必须能单独改这两位再观察。
    /// </para>
    /// </summary>
    internal void SetColorSpaceRaw(uint inputValue, uint outputValue)
    {
        lock (_gate)
        {
            if (_disposed || _processor == IntPtr.Zero) return;

            var setStream = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorSetStreamColorSpaceFn>(
                _gpu.VideoContext, D3D11Interop.VideoContextSlots.VideoProcessorSetStreamColorSpace);

            var input = new D3D11Interop.VideoProcessorColorSpace { Value = inputValue };

            setStream(_gpu.VideoContext, _processor, 0, ref input);

            var setOutput = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorSetOutputColorSpaceFn>(
                _gpu.VideoContext, D3D11Interop.VideoContextSlots.VideoProcessorSetOutputColorSpace);

            var output = new D3D11Interop.VideoProcessorColorSpace { Value = outputValue };

            setOutput(_gpu.VideoContext, _processor, ref output);
        }
    }

    /// <summary><b>诊断专用</b>：显式设 / 清"自动处理模式"。</summary>
    internal void SetAutoProcessingRaw(bool enable)
    {
        lock (_gate)
        {
            if (_disposed || _processor == IntPtr.Zero) return;

            var setAuto = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorSetStreamAutoProcessingModeFn>(
                _gpu.VideoContext, D3D11Interop.VideoContextSlots.VideoProcessorSetStreamAutoProcessingMode);

            setAuto(_gpu.VideoContext, _processor, 0, enable ? 1 : 0);
        }
    }

    /// <summary>
    /// <b>诊断专用</b>：把原始饱和度值设在<b>第一趟</b>的处理器上，再转一帧 ——
    /// 用来验证"RGB 输入通路上滤镜到底起不起作用"这件事。
    /// <para>
    /// 🔥 生产路径<b>不</b>用它：真机实测证明在这块 Intel 核显上它对输出毫无影响，
    /// 灰度走的是 <see cref="BlitGrayscaleSecondPass"/> 那条两趟的路。
    /// </para>
    /// </summary>
    internal bool TryConvertWithRawSaturation(nint bgraTexture, bool enable, int level, out string? failure)
    {
        SetSaturationRaw(enable, level);

        return TryConvertInternal(bgraTexture, grayscale: false, out failure);
    }

    /// <summary>第一趟：BGRA → NV12（<b>不</b>动饱和度）。</summary>
    private bool BlitFirstPass(out string? failure)
    {
        failure = null;

        if (!Blt(_processor, _outputView, _inputView, out var hr))
        {
            failure = $"第一趟 VideoProcessorBlt 失败（{ComVtable.Describe(hr)}）—— {Width}×{Height}。";

            return false;
        }

        _lastOutput = _output;

        return true;
    }

    /// <summary>
    /// 第二趟：NV12 → NV12，而且这一趟的处理器上挂着「饱和度 = 最小值」。
    /// <para>它是灰度的<b>实际实现</b>，理由见 <see cref="EnsureGrayscaleSecondPass"/>。</para>
    /// </summary>
    private bool BlitGrayscaleSecondPass(out string? failure)
    {
        failure = null;

        if (!Blt(_grayProcessor, _grayOutputView, _grayInputView, out var hr))
        {
            failure = $"灰度那一趟 VideoProcessorBlt 失败（{ComVtable.Describe(hr)}）—— {Width}×{Height}。";

            return false;
        }

        _lastOutput = _grayOutput;

        return true;
    }

    // ═══════════════════════════════════════════════════════════
    // 指针叠加（M5.4-b）：把"合成好的那一小块 BGRA"转成 NV12 写到输出的一角
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把一张<b>小的</b> BGRA 纹理（调用方已按 DXGI 规格合成好的指针块）转成 NV12，
    /// <b>写到输出的指定矩形上</b>。
    ///
    /// <para>
    /// 🔥 <b>为什么这一步交给 VideoProcessor，而不是在 CPU 上把 BGRA 转 NV12 再写回</b>：
    /// </para>
    /// <list type="number">
    ///   <item>色彩<b>天然一致</b> —— 整屏那一趟与这一小块走的是同一个转换器、同一套系数，
    ///         不会出现"指针那一块和周围颜色对不上"的贴片感
    ///         （那正是 CPU 自己转最典型的翻车方式，且<b>不报错</b>）。</item>
    ///   <item>NV12 的色度平面在 D3D11 的纹理坐标里<b>没有独立地址空间</b>，
    ///         用 <c>UpdateSubresource</c> ＋ <c>D3D11_BOX</c> 根本写不进去
    ///         （见 <see cref="D3D11Interop.ContextSlots.UpdateSubresource"/> 的注释）。</item>
    /// </list>
    ///
    /// <para>
    /// ⚠️ <b>它是"覆盖"而不是"混合"，而这是故意的</b>：混合（含 <c>MASKED_COLOR</c> 的逐位 XOR、
    /// <c>MONOCHROME</c> 的 <c>(dst AND and) XOR xor</c>）已经在 CPU 上做完了，
    /// 交给它的就是"这块最终该长什么样"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 必须<b>先</b>成功跑过一次 <see cref="TryConvert"/> —— 目标就是那一趟的
    /// <see cref="OutputTexture"/>（灰度模式下是灰度那张）。
    /// </para>
    /// </summary>
    /// <param name="bgraSurface">
    /// 装着合成结果的 BGRA 纹理（调用方持有，本方法不释放）。
    /// <para>
    /// 🔴 <b>它的尺寸必须与 VideoProcessor 创建时声明的输入尺寸一致</b>（本仓＝整屏）。
    /// 早先这里收的是"region 尺寸的小纹理"，于是输入尺寸与声明尺寸不符 ——
    /// <c>Blt</c> 照样回 <c>S_OK</c>，但输出未定义（实测整屏被写坏）。见
    /// <c>WindowsGpuFrameEncoder.TryUploadAndOverlay</c> 的注释。
    /// </para>
    /// </param>
    /// <param name="source">
    /// 从输入纹理的哪一块取（<b>输入纹理坐标</b>）。产品路径上它<b>就是</b>
    /// <paramref name="destination"/> —— 指针已经写在那个位置上了，这是一趟 1:1 搬运。
    /// </param>
    /// <param name="destination">写到输出的哪个矩形（<b>输出纹理坐标</b>＝画面坐标）。</param>
    internal bool TryOverlay(
        nint bgraSurface, DxgiInterop.Rect source, DxgiInterop.Rect destination, out string? failure)
    {
        lock (_gate)
        {
            failure = null;

            if (_disposed)
            {
                failure = "转换器已经释放了。";

                return false;
            }

            if (_lastOutput == IntPtr.Zero)
            {
                failure = "还没有跑过第一趟转换 —— 没有可叠加的目标。";

                return false;
            }

            if (bgraSurface == IntPtr.Zero || source.Right <= source.Left || destination.Right <= destination.Left
                || source.Bottom <= source.Top || destination.Bottom <= destination.Top)
            {
                failure = $"叠加用的矩形不合法（句柄 {bgraSurface}，"
                    + $"源 {source.Left},{source.Top}..{source.Right},{source.Bottom}，"
                    + $"目标 {destination.Left},{destination.Top}..{destination.Right},{destination.Bottom}）。";

                return false;
            }

            // ⚠️ 目标是"最近一次转换的结果"：灰度模式下它是灰度那张。
            var targetView = _lastOutput == _grayOutput ? _grayOutputView : _outputView;

            if (!EnsureOverlayInputView(bgraSurface, out failure)) return false;

            // ⚠️ 源与目标**尺寸不等**时 VideoProcessor 会做缩放 —— 那也是一种"不报错但画面不对"。
            //    产品路径两边恒等（都是同一块 region），这里把它钉住：不等就是调用方写错了。
            if (source.Right - source.Left != destination.Right - destination.Left
                || source.Bottom - source.Top != destination.Bottom - destination.Top)
            {
                failure = $"叠加的源与目标尺寸不一致（源 "
                    + $"{source.Right - source.Left}×{source.Bottom - source.Top}，目标 "
                    + $"{destination.Right - destination.Left}×{destination.Bottom - destination.Top}）—— "
                    + "VideoProcessor 会静默缩放，画出来的指针是错的。";

                return false;
            }

            // 🔴 **必须先设 output target rect**（M5.4-b 第 10 步的真因）。
            //    `VideoProcessorBlt` 会把 target rect 之内、**没有被任何 stream 的 dest rect 覆盖**的
            //    部分一律填背景色（默认黑）⇒ 指针那一趟 dest rect 只有指针那一小块，
            //    不设它就会把整屏填黑（实测「矩形外亮度平均变化 184.00」＝桌面平均亮度被抹成 0）。
            //    ⚠️ 整屏那一趟之所以一直没事，正因为它的 dest rect ≡ 目标整屏，一个空白都没有。
            SetOutputTargetRect(destination);

            SetStreamRect(D3D11Interop.VideoContextSlots.VideoProcessorSetStreamSourceRect, source);
            SetStreamRect(D3D11Interop.VideoContextSlots.VideoProcessorSetStreamDestRect, destination);

            var ok = Blt(_processor, targetView, _overlayInputView, out var hr);

            // 🔥 **无论成败都要把矩形恢复成"没设"** —— 它们挂在处理器上（不是每次调用的参数），
            //    残留到下一帧会让"整屏那一趟"只转一个角 —— 而那种画面与"抓屏错了"长得一模一样。
            ResetOutputRects();

            if (!ok)
            {
                failure = $"指针叠加那一趟 VideoProcessorBlt 失败（{ComVtable.Describe(hr)}）—— "
                    + $"目标矩形 {destination.Left},{destination.Top}..{destination.Right},{destination.Bottom}。";

                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// 设一个流矩形（源或目标）。
    /// <para>⚠️ 这两个 Set 返回 <c>void</c> —— <b>设错了不会报错</b>，只会安静地按旧状态走。</para>
    /// </summary>
    private void SetStreamRect(int slot, DxgiInterop.Rect rect)
    {
        var set = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorSetStreamRectFn>(_gpu.VideoContext, slot);

        set(_gpu.VideoContext, _processor, 0, 1, ref rect);
    }

    /// <summary>
    /// 设"这一趟只处理输出的哪一块"（<c>VideoProcessorSetOutputTargetRect</c>）。
    ///
    /// <para>
    /// 🔴 <b>这是"指针画上去却整屏变黑"的解药</b>：不设的时候 target rect 是整个输出，
    /// 而 <c>Blt</c> 会把没被 stream 覆盖的部分填背景色（默认黑）。见槽位表里的长注释。
    /// </para>
    ///
    /// <para>⚠️ 与两个流矩形一样，它<b>返回 <c>void</c></b>：设错了不报错，只安静地按旧状态走。</para>
    /// </summary>
    private void SetOutputTargetRect(DxgiInterop.Rect rect)
    {
        var set = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorSetOutputTargetRectFn>(
            _gpu.VideoContext, D3D11Interop.VideoContextSlots.VideoProcessorSetOutputTargetRect);

        set(_gpu.VideoContext, _processor, 1, ref rect);
    }

    /// <summary>
    /// 把 output target rect 与两个流矩形都恢复成"没设"。
    /// <para>
    /// 🔴 <c>enable = 0</c> 时那个矩形参数<b>会被忽略</b> —— 但仍然必须给一个<b>合法地址</b>：
    /// 传 <c>0</c> 就是本仓那次进程级崩溃（把 0 当 <c>const RECT*</c> 解引用）。
    /// </para>
    /// </summary>
    private void ResetOutputRects() => RestoreBaseRects();

    private bool EnsureOverlayInputView(nint texture, out string? failure)
    {
        failure = null;

        if (_overlayInputView != IntPtr.Zero && _overlayInputViewFor == texture) return true;

        ReleaseOverlayInputView();

        if (!TryCreateInputView(texture, out var view, out failure)) return false;

        _overlayInputView = view;
        _overlayInputViewFor = texture;

        return true;
    }

    private void ReleaseOverlayInputView()
    {
        ComVtable.ReleasePointer(ref _overlayInputView);

        _overlayInputViewFor = IntPtr.Zero;
    }

    private bool Blt(IntPtr processor, IntPtr outputView, IntPtr inputView, out int hr)
    {
        var stream = new D3D11Interop.VideoProcessorStream
        {
            Enable = 1,
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
            PastSurfaces = IntPtr.Zero,
            InputSurface = inputView,
            FutureSurfaces = IntPtr.Zero,
            PastSurfacesRight = IntPtr.Zero,
            InputSurfaceRight = IntPtr.Zero,
            FutureSurfacesRight = IntPtr.Zero,
        };

        var blt = ComVtable.Bind<D3D11Interop.Delegates.VideoProcessorBltFn>(
            _gpu.VideoContext, D3D11Interop.VideoContextSlots.VideoProcessorBlt);

        hr = blt(_gpu.VideoContext, processor, outputView, 0, 1, ref stream);

        return ComVtable.Succeeded(hr);
    }

    // ═══════════════════════════════════════════════════════════
    // 读回（只给自检 / 排障用 —— 生产路径上这一步不存在）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把 NV12 结果的<b>两个平面</b>读回内存。
    ///
    /// <para>
    /// 🔥 <b>它存在的唯一理由是让"灰度到底有没有生效"可被直接验证</b>：
    /// 断言 UV 平面恒为 128，比"编出来再解回来、看看颜色淡不淡"确定得多
    /// （后者还要排除编码器自己的色度处理）。生产路径上<b>绝不该</b>调它 ——
    /// 那正是零拷贝要消灭的那次拷贝。
    /// </para>
    /// </summary>
    /// <param name="y">亮度平面（<c>Width × Height</c> 字节，行紧排）。</param>
    /// <param name="uv">色度平面（<c>Width × Height/2</c> 字节，U/V 交错）。</param>
    internal bool TryReadBackNv12Planes(
        out byte[] y, out byte[] uv, out int width, out int height, out string? failure)
    {
        width = Width;
        height = Height;

        lock (_gate)
        {
            return TryReadPlanes(_lastOutput, out y, out uv, out failure);
        }
    }

    /// <summary>读指定 NV12 纹理的两个平面（读回与自检共用）。</summary>
    private bool TryReadPlanes(nint source, out byte[] y, out byte[] uv, out string? failure)
    {
        y = Array.Empty<byte>();
        uv = Array.Empty<byte>();
        failure = null;

        if (source == IntPtr.Zero)
        {
            failure = "没有可读的结果（转换器已释放，或还没转过一帧）。";

            return false;
        }

        if (!EnsureNv12Staging(out failure)) return false;

        var copy = ComVtable.Bind<D3D11Interop.Delegates.CopyResourceFn>(
            _gpu.Context, D3D11Interop.ContextSlots.CopyResource);

        copy(_gpu.Context, _staging, source);

        var map = ComVtable.Bind<D3D11Interop.Delegates.MapFn>(
            _gpu.Context, D3D11Interop.ContextSlots.Map);

        var hr = map(_gpu.Context, _staging, 0, D3D11Interop.MapRead, 0, out var mapped);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"Map NV12 暂存纹理失败（{ComVtable.Describe(hr)}）。";

            return false;
        }

        var unmap = ComVtable.Bind<D3D11Interop.Delegates.UnmapFn>(
            _gpu.Context, D3D11Interop.ContextSlots.Unmap);

        try
        {
            var luma = new byte[Width * Height];
            var chroma = new byte[Width * (Height / 2)];

            // ⚠️ 逐行按 RowPitch 走，不能按 Width 硬算（驱动会把行距对齐撑大）。
            for (var row = 0; row < Height; row++)
            {
                Marshal.Copy(
                    mapped.Data + (row * (int)mapped.RowPitch), luma, row * Width, Width);
            }

            // 🔥 NV12 在 D3D11 里是**一张**纹理：色度平面紧跟在亮度平面之后，
            //    起点是 Data + RowPitch × Height（不是 Data + Width × Height）。
            var chromaBase = mapped.Data + ((int)mapped.RowPitch * Height);

            for (var row = 0; row < Height / 2; row++)
            {
                Marshal.Copy(
                    chromaBase + (row * (int)mapped.RowPitch), chroma, row * Width, Width);
            }

            y = luma;
            uv = chroma;

            return true;
        }
        finally
        {
            // 🔥 Map 成功之后必须 Unmap，否则这张暂存纹理再也 Map 不上。
            unmap(_gpu.Context, _staging, 0);
        }
    }

    private bool EnsureNv12Staging(out string? failure)
    {
        failure = null;

        if (_staging != IntPtr.Zero) return true;

        var desc = D3D11Interop.Texture2DDesc.Staging(
            (uint)Width, (uint)Height, DxgiInterop.FormatNv12);

        var create = ComVtable.Bind<D3D11Interop.Delegates.CreateTexture2DFn>(
            _gpu.Device, D3D11Interop.DeviceSlots.CreateTexture2D);

        var hr = create(_gpu.Device, ref desc, IntPtr.Zero, out _staging);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"建 NV12 读回暂存纹理失败（{ComVtable.Describe(hr)}）—— {Width}×{Height}。"
                + "有些驱动不给 NV12 提供 STAGING 用法。";

            _staging = IntPtr.Zero;

            return false;
        }

        return true;
    }

    // ═══════════════════════════════════════════════════════════
    // 收尾
    // ═══════════════════════════════════════════════════════════

    /// <summary>给能力报告 / 日志用的一句话。</summary>
    internal string Describe()
    {
        var selfTest = _grayscaleSelfTest switch
        {
            true => $"（自检通过：{_grayscaleSelfTestNote}）",
            false => $"（⚠️ 自检未通过：{_grayscaleSelfTestNote}）",
            _ => "（尚未自检：第一次真要灰度时会读回像素验一次）",
        };

        return $"{AdapterName}：BGRA8→NV12 可用（{Width}×{Height}）"
            + $"{(UsesIntermediateCopy ? "，⚠️ 走了显存内中转" : "，纹理直连")}；灰度 {selfTest} {GrayscaleMechanism}";
    }

    private void ReleaseInputView()
    {
        ComVtable.ReleasePointer(ref _inputView);

        _inputViewFor = IntPtr.Zero;
    }

    private D3D11Nv12Converter? DisposeAndReturn()
    {
        Dispose();

        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;

            // 释放顺序：依赖者先走（视图握着纹理、处理器握着枚举器）。
            ComVtable.ReleasePointer(ref _staging);
            ComVtable.ReleasePointer(ref _mirror);
            ReleaseInputView();
            ReleaseOverlayInputView();

            // ── 灰度那一趟（视图 → 纹理 → 处理器）──
            ComVtable.ReleasePointer(ref _grayInputView);
            ComVtable.ReleasePointer(ref _grayOutputView);
            ComVtable.ReleasePointer(ref _grayOutput);
            ComVtable.ReleasePointer(ref _grayProcessor);

            _lastOutput = IntPtr.Zero;

            ComVtable.ReleasePointer(ref _outputView);
            ComVtable.ReleasePointer(ref _output);
            ComVtable.ReleasePointer(ref _processor);
            ComVtable.ReleasePointer(ref _enumerator);

            // ⚠️ **不释放设备** —— 它是外来的（抓屏器 / 编码器共用那一个），
            //    在这里释放会让另一边的视图全部变成悬空指针。
        }
    }
}
