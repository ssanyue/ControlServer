using System.Runtime.InteropServices;

namespace ControlServer.Platform.Windows;

/// <summary>
/// <b>D3D11（含 D3D11 Video）的裸 vtable 声明</b>（M5.4 零拷贝管线的色彩转换侧）。
///
/// <para>
/// 🔥 <b>只声明用到的槽位</b>，理由与核对方法见 <see cref="DxgiInterop"/> 的类注释与
/// <c>docs/M5.4-实施方案.md</c> §3.1。
/// </para>
///
/// <para>
/// ⚠️ <c>ID3D11Device</c> 有 <b>43</b> 个槽、<c>ID3D11VideoContext</c> 有 <b>65</b> 个，
/// 而本批只用其中 6 个。这就是"抄全 vtable"这条路走不通的原因。
/// </para>
/// </summary>
internal static class D3D11Interop
{
    // ──────────────────────────── 接口 IID ────────────────────────────

    internal static readonly Guid IID_ID3D11Device = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    internal static readonly Guid IID_ID3D11DeviceContext = new("c0bfa96c-e089-44fb-8eaf-26f8796190da");

    /// <summary>
    /// <c>ID3D11Texture2D</c>（<c>d3d11.h:5651</c>）。
    /// <para>
    /// 🔥 它是 <c>MFCreateDXGISurfaceBuffer</c> 的<b>第一个参数</b> —— 那个函数要的不是"一张纹理句柄"，
    /// 而是"请按这个 IID 去打开它"，所以必须给得准。
    /// </para>
    /// </summary>
    internal static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    internal static readonly Guid IID_ID3D11VideoDevice = new("10ec4d5b-975a-4689-b9e4-d0aac30fe333");
    internal static readonly Guid IID_ID3D11VideoContext = new("61f21c45-3c0e-4a74-9cea-67100d9ad5e4");
    internal static readonly Guid IID_ID3D11VideoProcessorEnumerator = new("31627037-53ab-4200-9061-05faa9ab45f9");
    internal static readonly Guid IID_ID3D11VideoProcessor = new("1d7b0652-185f-41c6-85ce-0c5be3d4ae6c");
    internal static readonly Guid IID_ID3D11VideoProcessorInputView = new("11ec5a5f-51dc-4945-ab34-6e8c21300ea5");
    internal static readonly Guid IID_ID3D11VideoProcessorOutputView = new("a048285e-25a9-4527-bd93-d68b68c44254");

    /// <summary>
    /// <c>ID3D10Multithread</c>（<c>d3d10.h:5687</c>）。
    ///
    /// <para>
    /// 🔥 <b>为什么 D3D11 的工程要去 QI 一个 D3D10 的接口</b>：MF 官方文档就是这么教的 ——
    /// <c>IMFDXGIDeviceManager::ResetDevice</c> 的 Remarks 里写着
    /// "先在 <c>ID3D11Device</c> 上 QueryInterface 出 <c>ID3D10Multithread</c>，
    /// 再 <c>SetMultithreadProtected(TRUE)</c>"。
    /// </para>
    /// <para>
    /// ⚠️ <b>它的名字里那个 "10" 不是笔误，也不是"旧版本"</b>：
    /// 多线程保护这件事从 D3D10 起就没有再改过接口形态，D3D11 也复用它。
    /// 换成 <c>ID3D11Multithread</c>（在 <c>d3d11_4.h</c> 里）就要求 Windows 10 的运行时。
    /// </para>
    /// </summary>
    internal static readonly Guid IID_ID3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");

    // ──────────────────────────── 枚举取值 ────────────────────────────

    /// <summary><c>D3D_DRIVER_TYPE_UNKNOWN</c>。<b>显式传 adapter 时必须用这个</b>，不能用 HARDWARE。</summary>
    internal const int DriverTypeUnknown = 0;

    /// <summary><c>D3D_DRIVER_TYPE_HARDWARE</c>。</summary>
    internal const int DriverTypeHardware = 1;

    /// <summary><c>D3D_FEATURE_LEVEL_11_1</c>。</summary>
    internal const int FeatureLevel11_1 = 0xb100;

    /// <summary><c>D3D_FEATURE_LEVEL_11_0</c>。</summary>
    internal const int FeatureLevel11_0 = 0xb000;

    /// <summary><c>D3D11_CREATE_DEVICE_BGRA_SUPPORT</c> —— 抓屏所必需的。</summary>
    internal const uint CreateDeviceBgraSupport = 0x20;

    /// <summary>
    /// <c>D3D11_CREATE_DEVICE_VIDEO_SUPPORT</c>（<c>d3d11.h</c>，<b>0x800</b>）。
    ///
    /// <para>
    /// 🔥 <b>官方文档对它是"should"，而不是"may"</b>：<c>IMFDXGIDeviceManager::ResetDevice</c>
    /// 的 Remarks 里写着「The device <b>should</b> be created with the
    /// <c>D3D11_CREATE_DEVICE_VIDEO_SUPPORT</c> device creation flag」。
    /// </para>
    /// <para>
    /// ⚠️ <b>但它有一条不显眼的副作用</b>（<c>D3D11_CREATE_DEVICE_FLAG</c> 文档原文）：
    /// 如果显卡驱动没实现到 WDDM 1.2，设了它就是"只允许特性级别 9.1/9.2/9.3 的设备"，
    /// 而 <b>请求 10 以上会直接创建失败</b>。⇒ 所以它<b>不能盲目加上去</b>，
    /// 必须先带它试、失败再退回（见 <c>D3D11GpuContext.CreateDevice</c>）。
    /// </para>
    /// </summary>
    internal const uint CreateDeviceVideoSupport = 0x800;

    /// <summary><c>D3D11_CREATE_DEVICE_DEBUG</c>（调试层，正常路径不开）。</summary>
    internal const uint CreateDeviceDebug = 0x2;

    /// <summary><c>D3D11_USAGE_DEFAULT</c>（纯显存，CPU 不碰 —— 零拷贝要的就是它）。</summary>
    internal const int UsageDefault = 0;

    /// <summary><c>D3D11_USAGE_DYNAMIC</c>。</summary>
    internal const int UsageDynamic = 2;

    /// <summary><c>D3D11_USAGE_STAGING</c>（CPU 能读，代价是每次访问都要过一遍 PCIe）。</summary>
    internal const int UsageStaging = 3;

    /// <summary><c>D3D11_CPU_ACCESS_READ</c>。<b>STAGING 纹理要 <c>Map</c> 就必须带它。</b></summary>
    internal const uint CpuAccessRead = 0x20000;

    /// <summary><c>D3D11_CPU_ACCESS_WRITE</c>。</summary>
    internal const uint CpuAccessWrite = 0x10000;

    /// <summary><c>D3D11_MAP_READ</c>（<c>Map</c> 的参数）。</summary>
    internal const int MapRead = 1;

    /// <summary><c>D3D11_MAP_WRITE</c>（对 STAGING 纹理写时用它）。</summary>
    internal const int MapWrite = 2;

    /// <summary><c>D3D11_MAP_WRITE_DISCARD</c>（<c>Map</c> 的参数，动态纹理写整块时用）。</summary>
    internal const int MapWriteDiscard = 4;

    /// <summary><c>D3D11_BIND_RENDER_TARGET</c>。<b>VideoProcessor 的输出纹理需要它。</b></summary>
    internal const uint BindRenderTarget = 0x20;

    /// <summary><c>D3D11_BIND_SHADER_RESOURCE</c>。</summary>
    internal const uint BindShaderResource = 0x8;

    /// <summary><c>D3D11_BIND_UNORDERED_ACCESS</c>（灰度那条路可能要）。</summary>
    internal const uint BindUnorderedAccess = 0x80;

    /// <summary><c>D3D11_VIDEO_USAGE_PLAYBACK_NORMAL</c>。</summary>
    internal const int VideoUsagePlaybackNormal = 0;

    /// <summary><c>D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE</c>。</summary>
    internal const int VideoFrameFormatProgressive = 0;

    /// <summary><c>D3D11_VIDEO_PROCESSOR_ROTATION_IDENTITY</c>。</summary>
    internal const int VideoProcessorRotationIdentity = 0;

    /// <summary><c>D3D11_VPIV_DIMENSION_TEXTURE2D</c>。<b>不是 0。</b></summary>
    internal const int VpivDimensionTexture2D = 1;

    /// <summary><c>D3D11_VPOV_DIMENSION_TEXTURE2D</c>。<b>不是 0。</b></summary>
    internal const int VpovDimensionTexture2D = 1;

    /// <summary>VideoProcessor 支持把这个格式当<b>输入</b>。</summary>
    internal const uint VideoProcessorFormatSupportInput = 0x1;

    /// <summary>VideoProcessor 支持把这个格式当<b>输出</b>。</summary>
    internal const uint VideoProcessorFormatSupportOutput = 0x2;

    /// <summary><c>D3D11_VIDEO_PROCESSOR_FILTER_SATURATION</c>。</summary>
    internal const int FilterSaturation = 3;

    /// <summary><c>D3D11_VIDEO_PROCESSOR_FILTER_NOISE_REDUCTION</c>。</summary>
    internal const int FilterNoiseReduction = 4;

    /// <summary>
    /// <c>D3D11_VIDEO_PROCESSOR_FILTER_EDGE_ENHANCEMENT</c>。
    /// <para>
    /// 🔥 <b>它必须被显式关掉</b>：本机驱动报的默认值是 <b>44 / 64</b>（<b>不是 0</b>），
    /// 也就是说"什么都不设"并不等于"不做增强" —— 而它作用在亮度上，
    /// 会让灰度那一趟的输出亮度与彩色那趟对不上（实测最大差 5）。
    /// </para>
    /// </summary>
    internal const int FilterEdgeEnhancement = 5;

    /// <summary><c>VIDEO_PROCESSOR_FILTER_CAPS_SATURATION</c>（能力位）。</summary>
    internal const uint FilterCapsSaturation = 0x8;

    // ──────────────────────────── 结构体 ────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct SampleDesc
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public SampleDesc SampleDesc;
        public int Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;

        /// <summary>造一张"别人能当输入/输出视图用"的普通单张 2D 纹理。</summary>
        internal static Texture2DDesc Plain(uint width, uint height, int format, uint bindFlags) => new()
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Usage = UsageDefault,
            BindFlags = bindFlags,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };

        /// <summary>
        /// 造一张 <b>CPU 能读</b>的暂存纹理（读回显存内容用的中转站）。
        /// <para>
        /// ⚠️ <b>它必须同时满足三条</b>：<c>USAGE_STAGING</c> + <c>CPU_ACCESS_READ</c> +
        /// <b>不带任何 BindFlags</b>。少任何一条 <c>Map</c> 都会失败，
        /// 而"失败"在这里是 <c>E_INVALIDARG</c>（不给原因的那一种）。
        /// </para>
        /// </summary>
        internal static Texture2DDesc Staging(uint width, uint height, int format) => new()
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Usage = UsageStaging,
            BindFlags = 0,
            CPUAccessFlags = CpuAccessRead,
            MiscFlags = 0,
        };

        /// <summary>
        /// 造一张 <b>CPU 可写</b>的暂存纹理（造测试数据用的；<c>Map(WRITE)</c> 需要它）。
        /// </summary>
        internal static Texture2DDesc StagingWritable(uint width, uint height, int format) => new()
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Usage = UsageStaging,
            BindFlags = 0,
            CPUAccessFlags = CpuAccessWrite,
            MiscFlags = 0,
        };

        /// <summary>造一张<b>既能当拷贝源、又能当拷贝目标</b>的普通显存纹理（<c>CopyResource</c> 用）。</summary>
        internal static Texture2DDesc Copyable(uint width, uint height, int format) => new()
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Usage = UsageDefault,
            BindFlags = 0,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };

        /// <summary>
        /// 造一张<b>能当视频处理器输入</b>的显存纹理（<c>D3D11_BIND_SHADER_RESOURCE</c>）。
        /// <para>
        /// ⚠️ <see cref="Copyable"/>（BindFlags=0）能 <c>CopyResource</c>，但<b>不能</b>建
        /// <c>VideoProcessorInputView</c> —— 后者的前提是资源带 RENDER_TARGET 或
        /// SHADER_RESOURCE。造测试图案时踩过：纹理造出来了、拷贝也成功、
        /// <c>TryConvert</c> 却回一句"建不出输入视图"，而那句话看起来像转换器的毛病。
        /// </para>
        /// </summary>
        internal static Texture2DDesc PipelineInput(uint width, uint height, int format) => new()
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Usage = UsageDefault,
            BindFlags = BindShaderResource,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };
    }

    /// <summary>
    /// <c>D3D11_MAPPED_SUBRESOURCE</c>。
    /// <para>
    /// ⚠️ <b><c>RowPitch</c> 必须用，不能拿 <c>width × 4</c> 代替</b>：
    /// 驱动为了对齐往往把它撑大（4K 下常见多出几十到几百字节）。
    /// 按宽度硬拷的话每行会<b>错开一点点</b> —— 画面看起来只是"斜了 / 花了"，
    /// 在 1080p 以下甚至可能完全看不出来。
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MappedSubresource
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    /// <summary>
    /// <c>D3D11_BOX</c>（<c>CopySubresourceRegion</c> / <c>UpdateSubresource</c> 用来限定区域）。
    /// <para>
    /// ⚠️ 坐标是<b>左闭右开</b>（<c>right</c> / <c>bottom</c> 不含），与 <c>DxgiInterop.Rect</c> 同一约定
    /// —— 但<b>两个是不同的结构体</b>（这个多一个 <c>front/back</c>），别混用。
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11Box
    {
        public uint Left;
        public uint Top;
        public uint Front;
        public uint Right;
        public uint Bottom;
        public uint Back;

        /// <summary>从一块 <c>DxgiInterop.Rect</c> 造一个（<c>3D</c> 那一维给 0..1）。</summary>
        internal static D3D11Box From(DxgiInterop.Rect rect) => new()
        {
            Left = (uint)rect.Left,
            Top = (uint)rect.Top,
            Front = 0,
            Right = (uint)rect.Right,
            Bottom = (uint)rect.Bottom,
            Back = 1,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoProcessorContentDesc
    {
        public int InputFrameFormat;      // D3D11_VIDEO_FRAME_FORMAT
        public DxgiInterop.Rational InputFrameRate;
        public uint InputWidth;
        public uint InputHeight;
        public DxgiInterop.Rational OutputFrameRate;
        public uint OutputWidth;
        public uint OutputHeight;
        public int Usage;                 // D3D11_VIDEO_USAGE

        internal static VideoProcessorContentDesc For(
            uint width, uint height, uint fpsNumerator, uint fpsDenominator)
            => For(width, height, width, height, fpsNumerator, fpsDenominator);

        /// <summary>
        /// 输入与输出<b>尺寸不同</b>的那一种（M5.5 的 ROI / 分块编码）。
        /// <para>
        /// ⚠️ 输入尺寸必须与<b>实际喂进去的纹理</b>一致。两者不一致时 Blt 照样回
        /// <c>S_OK</c>，而输出是未定义的 —— M5.4-b 第 10 步实测过「指针写进去了、
        /// 整屏被写坏」，那次就是输入尺寸声明错了。
        /// </para>
        /// </summary>
        internal static VideoProcessorContentDesc For(
            uint inputWidth,
            uint inputHeight,
            uint outputWidth,
            uint outputHeight,
            uint fpsNumerator,
            uint fpsDenominator) => new()
            {
                InputFrameFormat = VideoFrameFormatProgressive,
                InputFrameRate = new DxgiInterop.Rational { Numerator = fpsNumerator, Denominator = fpsDenominator },
                InputWidth = inputWidth,
                InputHeight = inputHeight,
                OutputFrameRate = new DxgiInterop.Rational { Numerator = fpsNumerator, Denominator = fpsDenominator },
                OutputWidth = outputWidth,
                OutputHeight = outputHeight,
                Usage = VideoUsagePlaybackNormal,
            };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoProcessorCaps
    {
        public uint DeviceCaps;
        public uint FeatureCaps;
        public uint FilterCaps;
        public uint InputFormatCaps;
        public uint AutoStreamCaps;
        public uint StereoCaps;
        public uint RateConversionCapsCount;
        public uint MaxInputStreams;
        public uint MaxStreamStates;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoProcessorRateConversionCaps
    {
        public uint PastFrames;
        public uint FutureFrames;
        public uint ProcessorCaps;
        public uint ITelecineCaps;
        public uint CustomRateCount;
    }

    /// <summary><c>D3D11_TEX2D_VPIV</c>（输入视图描述里的那个 union 成员）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Tex2dVpiv
    {
        public uint ArraySlice;
    }

    /// <summary><c>D3D11_TEX2D_ARRAY_VPOV</c>（输出视图描述里的那个 union 成员，<b>比输入那个大</b>）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Tex2dArrayVpov
    {
        public uint ArraySlice;
        public uint MipSlice;
    }

    /// <summary>
    /// <c>D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC</c>。
    /// <para>
    /// ⚠️ union 里的 <b>数组切片字段必须显式布局到偏移 8</b>：
    /// C 侧那个 union 紧跟在两个 UINT 之后（4 + 4 = 8），
    /// 而 <c>LayoutKind.Sequential</c> 默认会把它排在偏移 8 —— 一致，可以放心用 Sequential。
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoProcessorInputViewDesc
    {
        public uint FourCC;
        public int ViewDimension;
        public Tex2dVpiv Texture2D;
    }

    /// <summary><c>D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC</c>。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoProcessorOutputViewDesc
    {
        public int ViewDimension;

        /// <summary>union 里<b>最大</b>的那个成员（8 字节），用它在布局上占位。</summary>
        public Tex2dArrayVpov Texture2DArray;
    }

    /// <summary>
    /// <c>D3D11_VIDEO_PROCESSOR_COLOR_SPACE</c> —— 一个 <c>UINT</c> 位域。
    /// <para>
    /// 位布局（头文件核实）：<c>Usage:1</c> / <c>RGB_Range:1</c> / <c>YCbCr_Matrix:1</c> /
    /// <c>YCbCr_xvYCC:1</c> / <c>Nominal_Range:2</c> / <c>Reserved:26</c>。
    /// </para>
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>这两个值写错的症状是「颜色偏亮或偏暗一点点」</b>（全范围 vs 有限范围），
    /// <b>不会报错</b>，而且在"看起来还行"的范围内 —— 所以它们是显式设的、并且在这里写清楚理由，
    /// 而不是留给默认值。
    ///
    /// <para>
    /// 🔥 <b>位序（对着 <c>d3d11.h</c> 核过，官方文档 <c>ns-d3d11-d3d11_video_processor_color_space</c> 也逐条列了）</b>：
    /// <c>Usage:1</c>（0=Playback / 1=Video processing）、<c>RGB_Range:1</c>（<b>0=Full 0–255</b> / 1=Limited 16–235）、
    /// <c>YCbCr_Matrix:1</c>（0=BT.601 / 1=BT.709）、<c>YCbCr_xvYCC:1</c>、<c>Nominal_Range:2</c>（0=UNDEFINED / 1=16–235 / 2=0–255）。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>这里踩过一次</b>：早先写的是 <c>0b011</c> 并注释成"全范围"，而
    /// <c>0b011</c> 的 <c>RGB_Range</c> 位是 <b>1 ＝ 有限范围</b> ——
    /// 恰好把"桌面是 0–255"说成了"桌面是 16–235"，于是驱动会先把画面**压进**有限范围再转 YCbCr，
    /// 结果整体发闷、深色发灰。<b>它一个错误都不报。</b>
    /// ⇒ 位域这种东西必须<b>按官方文档的取值表写</b>，不能按"二进制看起来对"写。
    /// </para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoProcessorColorSpace
    {
        public uint Value;

        /// <summary>
        /// 输入流：<b>全范围 RGB</b>（桌面内容就是 0–255）。
        /// <para><c>Usage = 0</c>（Playback，最常见的取值；真正影响像素值的是 <c>RGB_Range</c>）。</para>
        /// </summary>
        internal static VideoProcessorColorSpace RgbFullRangeInput => new() { Value = 0b000 };

        /// <summary>
        /// 输出：<b>标准视频用的 YCbCr</b> —— BT.709 矩阵 + 有限范围 16–235。
        ///
        /// <para>
        /// 🔥 <b>为什么定格在 BT.709 而不是 BT.601</b>：601 是标清时代的矩阵，
        /// 用在 1080p 及以上会让颜色系统性偏移 —— 而偏得不多，肉眼很难判定"这就是错的"。
        /// 本管线的最低目标就是 2160×1440，所以 709 是唯一正确的选择。
        /// </para>
        /// <para>
        /// <c>Nominal_Range</c> 显式写 16–235 而不是留 <c>UNDEFINED</c>：
        /// H.264/HEVC 的"视频范围"就是 16–235，交给驱动取默认值虽然<b>通常</b>也是这个，
        /// 但"通常"在色彩上不是一个可接受的说法。
        /// </para>
        /// </summary>
        internal static VideoProcessorColorSpace YCbCr709LimitedOutput => new() { Value = 0b1_0100 };

        /// <summary>
        /// <b>输入流是 YCbCr</b>（NV12）时的取值 —— 与 <see cref="YCbCr709LimitedOutput"/> 同值。
        ///
        /// <para>
        /// 🔥 位模式一样，但<b>语义不同、写出来必须能分辨</b>：当输入流是 YCbCr 时
        /// 起作用的是 <c>YCbCr_Matrix</c> 与 <c>Nominal_Range</c>，而 <c>RGB_Range</c> 无关；
        /// 反过来（RGB 输入）时正好相反。用同一个具名常量会让读代码的人以为"这里也能填 RGB 那套"。
        /// </para>
        /// </summary>
        internal static VideoProcessorColorSpace YCbCr709LimitedInput => new() { Value = 0b1_0100 };
    }

    /// <summary>
    /// <c>D3D11_VIDEO_PROCESSOR_FILTER_RANGE</c>（由驱动自己报出来的量程）。
    ///
    /// <para>
    /// 🔥 <b>灰度模式（决策 14）就落在它身上</b>：把 <b>饱和度</b>滤镜调到 <c>Minimum</c>，
    /// 驱动就会把色度算成中性值 —— 也就是我们要的"色度平面置常量 128"。
    /// 这条路<b>不需要 HLSL、不需要额外依赖</b>，是 D3D11 视频处理器自带的能力。
    /// </para>
    /// <para>
    /// ⚠️ <b>必须用驱动报的 <c>Minimum</c> 而不是硬写 0</b>：量程是驱动定义的
    /// （常见 <c>0..100</c>、默认 <c>50</c>，但不是规范强制的）。硬写 0 在某个只接受 <c>10..100</c> 的驱动上
    /// 会变成<b>越界且被静默忽略</b>。
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoProcessorFilterRange
    {
        public int Minimum;
        public int Maximum;
        public int Default;
        public float Multiplier;
    }

    /// <summary>
    /// <c>D3D11_VIDEO_PROCESSOR_STREAM</c>。
    /// <para>
    /// ⚠️ 字段顺序<b>不能改</b>：C 侧 <c>ppPastSurfaces</c> 与 <c>pInputSurface</c> 之间夹着一层指针，
    /// 而有 6 个指针字段是 <b>NULL 也算合法</b>（本批只用 <c>Enable</c> + <c>pInputSurface</c>）。
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoProcessorStream
    {
        public int Enable;
        public uint OutputIndex;
        public uint InputFrameOrField;
        public uint PastFrames;
        public uint FutureFrames;
        public IntPtr PastSurfaces;
        public IntPtr InputSurface;
        public IntPtr FutureSurfaces;
        public IntPtr PastSurfacesRight;
        public IntPtr InputSurfaceRight;
        public IntPtr FutureSurfacesRight;
    }

    // ──────────────────────────── 槽位号 ────────────────────────────

    /// <summary><c>ID3D11Texture2D</c>（43 槽）。</summary>
    internal static class Texture2DSlots
    {
        /// <summary><c>ID3D11Texture2D::GetDesc</c>（<c>IUnknown</c> 3 + <c>DeviceChild</c> 4 + <c>Resource</c> 3）。</summary>
        internal const int GetDesc = 10;
    }

    /// <summary><c>ID3D11DeviceContext</c>（115 槽 —— 全库最长的接口）。</summary>
    internal static class ContextSlots
    {
        /// <summary><c>ID3D11DeviceContext::Map</c>（读回显存内容时必须配一次 <c>Unmap</c>）。</summary>
        internal const int Map = 14;

        internal const int Unmap = 15;

        /// <summary>
        /// <c>ID3D11DeviceContext::CopySubresourceRegion</c> —— <b>部分区域</b>拷贝。
        /// <para>
        /// 本批用它的地方只有一处：把桌面副本上「指针那一小块」搬进一张 STAGING 纹理读回 CPU。
        /// </para>
        /// <para>
        /// 🔥 槽位核对：它是 <c>CopyResource</c> 的<b>前一个</b>槽（46 / 47）。这个前后关系在本仓
        /// 已被 <c>Map=14</c>、<c>Unmap=15</c>、<c>Begin=27</c>、<c>End=28</c>、<c>Flush=111</c>
        /// 五条独立观察交叉验证过（见 <c>M5.4-a</c> 的槽位表）。
        /// </para>
        /// </summary>
        internal const int CopySubresourceRegion = 46;

        /// <summary><c>ID3D11DeviceContext::CopyResource</c>（整块纹理之间拷，<b>不过 CPU</b>）。</summary>
        internal const int CopyResource = 47;

        /// <summary>
        /// <c>ID3D11DeviceContext::UpdateSubresource</c>（CPU 内存 → 显存纹理，可用 <c>D3D11_BOX</c> 限区域）。
        /// <para>
        /// ⚠️ 本批只用它写 <b>BGRA</b> 小纹理（指针合成出来的那个小块）。
        /// <b>不拿它写 NV12</b>：NV12 的色度平面在 D3D11 的纹理坐标里<b>没有独立地址空间</b>
        /// （<c>Desc.Height</c> 只有图像高度，色度是"Y 之后按 RowPitch 排下去的那一段"），
        /// 用 box 根本表达不了 —— 所以指针写进 NV12 这件事交给 VideoProcessor（见
        /// <see cref="VideoContextSlots.VideoProcessorSetStreamDestRect"/>）。
        /// </para>
        /// </summary>
        internal const int UpdateSubresource = 48;

        /// <summary><c>ID3D11DeviceContext::Flush</c>。</summary>
        internal const int Flush = 111;
    }

    /// <summary><c>ID3D11Device</c>（43 槽）。</summary>
    internal static class DeviceSlots
    {
        internal const int CreateTexture2D = 5;
        internal const int GetImmediateContext = 40;
    }

    /// <summary>
    /// <c>ID3D10Multithread</c>（7 槽）。
    /// <para>
    /// 🔥 整个接口 4 个方法，本工程<b>只用得到第 3 个</b>。名字与位置：
    /// <c>Enter = 3</c> / <c>Leave = 4</c> / <c>SetMultithreadProtected = 5</c> /
    /// <c>GetMultithreadProtected = 6</c>。
    /// ⚠️ <b>别按"保护/设置"这类词去猜位置</b>：<c>Enter</c>/<c>Leave</c> 排在前面，
    /// 凭直觉很容易把 Set 写成 3 或 4，而那会调到 <c>Enter</c>（无参）——
    /// 症状是"调了没反应"，比崩溃更难查。
    /// </para>
    /// </summary>
    internal static class MultithreadSlots
    {
        internal const int SetMultithreadProtected = 5;
    }

    /// <summary><c>ID3D11VideoDevice</c>（20 槽）。</summary>
    internal static class VideoDeviceSlots
    {
        internal const int CreateVideoProcessor = 4;
        internal const int CreateVideoProcessorInputView = 8;
        internal const int CreateVideoProcessorOutputView = 9;
        internal const int CreateVideoProcessorEnumerator = 10;
    }

    /// <summary><c>ID3D11VideoContext</c>（65 槽）。</summary>
    internal static class VideoContextSlots
    {
        /// <summary>
        /// <c>VideoProcessorSetOutputTargetRect</c> —— <b>这一趟要处理输出的哪一块</b>。
        ///
        /// <para>
        /// 🔴 <b>不设它＝整屏被填黑（M5.4-b 第 10 步的真因）</b>：<c>VideoProcessorBlt</c> 的语义是
        /// <b>target rect 之内、没有被任何 stream 的 dest rect 覆盖的部分，一律填背景色</b>（默认黑）。
        /// 整屏那一趟因为 dest rect ≡ 整屏 ≡ target rect，<b>一个像素的空白都没有</b>，
        /// 所以这个坑能安然无恙地藏掉整个里程碑；而指针那一趟 dest rect 只有指针那一小块
        /// ⇒ 其余全被当成"空白"填掉 ⇒ 观看端看到的是"没有鼠标"
        /// （实测「矩形内亮度变化 56.5、矩形外 184.00」——184 正是桌面平均亮度被填成 0 的差）。
        /// </para>
        ///
        /// <para>
        /// 🔥 <b>槽位来源（可推导，不是数数猜的）</b>：<c>ID3D11VideoContext</c> 的前段是
        /// <b>固定骨架</b> —— <c>IUnknown</c> 占 <c>0..6</c>（含 <c>GetDevice</c> /
        /// <c>GetPrivateData</c> / <c>SetPrivateData</c> / <c>SetPrivateDataInterface</c>），
        /// 紧接 <b>decoder 那 6 个</b>占 <c>7..12</c>（<c>GetDecoderBuffer</c>、
        /// <c>ReleaseDecoderBuffer</c>、<c>DecoderBeginFrame</c>、<c>DecoderEndFrame</c>、
        /// <c>SubmitDecoderBuffers</c>、<c>DecoderExtension</c>），
        /// 然后才是 <c>VideoProcessorSet*</c> 那一组 —— <b>打头的就是本槽位</b>。
        /// ⇒ <b>13 = SetOutputTargetRect</b>，14 = <c>SetOutputBackgroundColor</c>，
        /// 15 = <c>SetOutputColorSpace</c>（<b>本表里已有的那个值，正好落在同一条链上</b>）。
        /// </para>
        ///
        /// <para>
        /// ⚠️ 万一错位，代价也<b>不是崩溃</b>：13~19 这一组的签名高度同形
        /// （都是 <c>(this, processor, BOOL, …)</c>），错一位只会"设了个没用的属性"。
        /// 真正的验证在 <c>D3D11PointerOverlaySmokeTests</c>：设对了，"矩形外"就会从 184 掉到 0。
        /// </para>
        /// </summary>
        internal const int VideoProcessorSetOutputTargetRect = 13;

        internal const int VideoProcessorSetOutputColorSpace = 15;

        internal const int VideoProcessorSetStreamFrameFormat = 27;

        internal const int VideoProcessorSetStreamColorSpace = 28;

        /// <summary>
        /// <c>VideoProcessorSetStreamSourceRect</c>（<b>源纹理</b>里取哪一块）。
        /// <para>
        /// 🔥 逐条对数（<c>mingw d3d11.h:9388…9411</c>，<c>ID3D11VideoContextVtbl</c>）：
        /// <c>SetStreamFrameFormat=27</c> / <c>SetStreamColorSpace=28</c> / <c>SetStreamOutputRate=29</c> /
        /// <b><c>SetStreamSourceRect=30</c></b> / <c>SetStreamDestRect=31</c>。
        /// </para>
        /// </summary>
        internal const int VideoProcessorSetStreamSourceRect = 30;

        /// <summary>
        /// <c>VideoProcessorSetStreamDestRect</c>（写到<b>目标纹理</b>的哪个矩形）。
        /// <para>
        /// 本批用它做指针合成的最后一步：把已经合成好的 BGRA 小块，由 VideoProcessor
        /// 转到 NV12 并写到输出的指针矩形上。
        /// </para>
        /// <para>
        /// 🔴 <b>这个槽位在本仓已经出过一次进程级崩溃</b>：它的第 4 个参数是 <c>const RECT*</c>，
        /// 曾经把 <c>enable</c>（0 / 1）当指针传进去 ⇒ 解引用地址 1 ⇒
        /// <c>AccessViolationException</c>，测试宿主整个消失。
        /// ⇒ 用它的地方，<b>第 4 个参数必须是真 <c>Rect</c> 的地址或 <c>NULL</c></b>。
        /// </para>
        /// </summary>
        internal const int VideoProcessorSetStreamDestRect = 31;

        /// <summary>
        /// <c>VideoProcessorSetStreamAutoProcessingMode</c>（<b>37，不是 31</b>）。
        /// <para>
        /// 自动模式开着时由驱动自己挑处理步骤（会忽略显式设的滤镜）；
        /// 默认是关（<c>FALSE</c>），显式写一遍是为了"不依赖默认值"。
        /// </para>
        /// <para>
        /// 🔴 <b>这里写错过一次，代价是进程级崩溃</b>：31 是 <c>VideoProcessorSetStreamDestRect</c>，
        /// 它的第 4 个参数是 <c>const RECT*</c>；我们把 <c>enable</c>（0 / 1）当指针传了进去，
        /// 于是解引用地址 1 ⇒ <c>AccessViolationException</c>，整个测试宿主进程没了。
        /// 这一组 <c>Set*</c> 是<b>连着的 14 个</b>，名字长得像、位置全靠数 ——
        /// <b>只能对着 <c>d3d11.h</c> 数，不能按"名字看起来在哪"填。</b>
        /// </para>
        /// </summary>
        internal const int VideoProcessorSetStreamAutoProcessingMode = 37;

        /// <summary>
        /// <c>VideoProcessorSetStreamFilter</c> —— <b>灰度模式的落点</b>（饱和度调到最小）。
        /// <para>
        /// 🔥 <b>为什么是 38 而不是 33</b>：这一组 Set/Get 是<b>成对交错</b>的
        /// （先是 14 个 Set，再是 14 个 Get，然后才 <c>Blt</c>）。
        /// 按"Set 全在前、Get 全在后"的直觉去数会差 13 个槽 —— 而差一个的后果
        /// 是调到一个"签名长得差不多"的方法上，<b>不报错</b>，只是效果诡异。
        /// 本表逐条对着 <c>d3d11.h</c> 的 <c>ID3D11VideoContextVtbl</c> 解析过（方案 §3.1）。
        /// </para>
        /// </summary>
        internal const int VideoProcessorSetStreamFilter = 38;

        /// <summary>
        /// <c>VideoProcessorGetStreamFilter</c> —— 把驱动<b>实际存下来</b>的滤镜状态读回来。
        /// <para>
        /// 🔥 <c>SetStreamFilter</c> 返回 <c>void</c>，所以"设了但没生效"是<b>完全静默</b>的。
        /// 只有读回来才能把「调用被忽略了」与「调用生效了但驱动不拿它当回事」分开 ——
        /// 这两件事的处置完全相反。
        /// </para>
        /// </summary>
        internal const int VideoProcessorGetStreamFilter = 51;

        /// <summary>真正干活的那一下：BGRA → NV12。</summary>
        internal const int VideoProcessorBlt = 53;
    }

    /// <summary><c>ID3D11VideoProcessorEnumerator</c>（13 槽）。</summary>
    internal static class VideoProcessorEnumeratorSlots
    {
        internal const int GetVideoProcessorContentDesc = 7;
        internal const int CheckVideoProcessorFormat = 8;
        internal const int GetVideoProcessorCaps = 9;
        internal const int GetVideoProcessorRateConversionCaps = 10;

        /// <summary><c>GetVideoProcessorFilterRange</c> —— 问驱动"饱和度能调到多低"。</summary>
        internal const int GetVideoProcessorFilterRange = 12;
    }

    // ──────────────────────────── 函数原型 ────────────────────────────

    /// <summary>
    /// <c>D3D11CreateDevice</c>。
    /// <para>
    /// 🔥 <paramref name="adapter"/> 非零时 <paramref name="driverType"/> <b>必须是</b>
    /// <see cref="DriverTypeUnknown"/> —— 传 HARDWARE 会被 <c>E_INVALIDARG</c> 顶回来，
    /// 而错误信息不会告诉你"是这两个参数冲突"。
    /// </para>
    /// </summary>
    [DllImport("d3d11.dll", ExactSpelling = true)]
    internal static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        int[]? featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out int obtainedFeatureLevel,
        out IntPtr immediateContext);

    /// <summary><c>D3D11_SDK_VERSION</c>。</summary>
    internal const uint SdkVersion = 7;

    // ──────────────────────────── 委托 ────────────────────────────

    internal static class Delegates
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int CreateTexture2DFn(
            IntPtr self, ref Texture2DDesc desc, IntPtr initialData, out IntPtr texture);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void GetImmediateContextFn(IntPtr self, out IntPtr context);

        /// <summary>
        /// <c>ID3D10Multithread::SetMultithreadProtected(BOOL bMTProtect)</c>。
        /// <para>
        /// ⚠️ 参数是 <c>BOOL</c>（4 字节 <c>int</c>），<b>不是 1 字节的 <c>bool</c></b> ——
        /// 用 <c>bool</c> 会让后面几个字节变成未定义的值，而这个调用<b>没有返回值可查</b>。
        /// </para>
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int SetMultithreadProtectedFn(IntPtr self, int protect);

        /// <summary><c>ID3D11Texture2D::GetDesc</c>。抓屏回来的纹理要靠它读宽高与格式。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetTexture2DDescFn(IntPtr self, out Texture2DDesc desc);

        /// <summary>
        /// <c>ID3D11DeviceContext::Map(resource, subresource, mapType, mapFlags, *mapped)</c>。
        /// <para>⚠️ 成功之后<b>必须</b>配一次 <c>Unmap</c>，否则那张纹理再也 <c>Map</c> 不上。</para>
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int MapFn(
            IntPtr self, IntPtr resource, uint subresource, int mapType, uint mapFlags,
            out MappedSubresource mapped);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void UnmapFn(IntPtr self, IntPtr resource, uint subresource);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void CopyResourceFn(IntPtr self, IntPtr destination, IntPtr source);

        /// <summary>
        /// <c>ID3D11DeviceContext::CopySubresourceRegion</c>。
        /// <para>⚠️ 返回 <c>void</c> —— <b>没有错误码可查</b>。给错区域它就是安静地什么都不做/给垃圾。</para>
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void CopySubresourceRegionFn(
            IntPtr self,
            IntPtr destination, uint destinationSubresource,
            uint destinationX, uint destinationY, uint destinationZ,
            IntPtr source, uint sourceSubresource, ref D3D11Box sourceBox);

        /// <summary>
        /// <c>ID3D11DeviceContext::UpdateSubresource</c>。
        /// <para>
        /// ⚠️ 返回 <c>void</c>。这里的 <paramref name="destinationBox"/> 用 <c>ref</c> 表达
        /// （本仓只用它写"整块"这一种情形）；若哪天要传 <c>NULL</c>（＝整张纹理），
        /// 得换成 <c>IntPtr</c>，<b>不能</b>拿一个 <c>default</c> 的 box 去糊 —— 那表示"写到 (0,0,0)"。
        /// </para>
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void UpdateSubresourceFn(
            IntPtr self, IntPtr destination, uint destinationSubresource, ref D3D11Box destinationBox,
            IntPtr sourceData, uint sourceRowPitch, uint sourceDepthPitch);

        /// <summary>
        /// <c>VideoProcessorSetStreamSourceRect</c> / <c>VideoProcessorSetStreamDestRect</c>
        /// （两者签名完全一样）。
        /// <para>
        /// 🔴 <paramref name="rect"/> <b>必须是真 <c>Rect</c> 的地址</b>（这里用 <c>ref</c> 表达）。
        /// 把它写成 <c>int</c> 再传 0/1，就是本仓那次进程级崩溃。
        /// </para>
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void VideoProcessorSetStreamRectFn(
            IntPtr self, IntPtr processor, uint streamIndex, int enable, ref DxgiInterop.Rect rect);

        /// <summary>
        /// <c>VideoProcessorSetOutputTargetRect(self, processor, enable, rect)</c>。
        /// <para>
        /// ⚠️ 比 <see cref="VideoProcessorSetStreamRectFn"/> <b>少一个 <c>streamIndex</c></b>
        /// —— 它挂在输出上，不是挂在某个流上。签名照 <c>d3d11.h</c>。
        /// </para>
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void VideoProcessorSetOutputTargetRectFn(
            IntPtr self, IntPtr processor, int enable, ref DxgiInterop.Rect rect);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int CreateVideoProcessorEnumeratorFn(
            IntPtr self, ref VideoProcessorContentDesc desc, out IntPtr enumerator);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int CreateVideoProcessorFn(
            IntPtr self, IntPtr enumerator, uint rateConversionIndex, out IntPtr processor);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int CreateVideoProcessorInputViewFn(
            IntPtr self, IntPtr resource, IntPtr enumerator,
            ref VideoProcessorInputViewDesc desc, out IntPtr view);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int CreateVideoProcessorOutputViewFn(
            IntPtr self, IntPtr resource, IntPtr enumerator,
            ref VideoProcessorOutputViewDesc desc, out IntPtr view);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetVideoProcessorContentDescFn(IntPtr self, out VideoProcessorContentDesc desc);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int CheckVideoProcessorFormatFn(IntPtr self, int format, out uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetVideoProcessorCapsFn(IntPtr self, out VideoProcessorCaps caps);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetVideoProcessorRateConversionCapsFn(
            IntPtr self, uint typeIndex, out VideoProcessorRateConversionCaps caps);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int VideoProcessorSetOutputColorSpaceFn(
            IntPtr self, IntPtr processor, ref VideoProcessorColorSpace colorSpace);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int VideoProcessorSetStreamFrameFormatFn(
            IntPtr self, IntPtr processor, uint streamIndex, int format);

        /// <summary><c>VideoProcessorSetStreamAutoProcessingMode(self, processor, streamIndex, enable)</c>。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void VideoProcessorSetStreamAutoProcessingModeFn(
            IntPtr self, IntPtr processor, uint streamIndex, int enable);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int VideoProcessorSetStreamColorSpaceFn(
            IntPtr self, IntPtr processor, uint streamIndex, ref VideoProcessorColorSpace colorSpace);

        /// <summary>
        /// <c>VideoProcessorSetStreamFilter(self, processor, streamIndex, filter, enable, level)</c>。
        /// <para>
        /// ⚠️ <c>enable</c> 是 <c>WINBOOL</c>（4 字节 <c>int</c>），<b>不是 <c>bool</c></b>；
        /// 也不是"1 字节的真假"。用错宽度会把后面那个 <c>level</c> 读成垃圾值。
        /// </para>
        /// <para>返回 <c>void</c> —— 它<b>不会告诉你"这个滤镜我不认"</b>，所以能力判断必须另外做（见下面的取量程）。</para>
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void VideoProcessorSetStreamFilterFn(
            IntPtr self, IntPtr processor, uint streamIndex, int filter, int enable, int level);

        /// <summary>
        /// <c>VideoProcessorGetStreamFilter(self, processor, streamIndex, filter, enabled, level)</c>。
        /// <para>⚠️ 两个出参都是 <c>WINBOOL*</c> / <c>int*</c>，也就是 <b>4 字节</b>。</para>
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void VideoProcessorGetStreamFilterFn(
            IntPtr self, IntPtr processor, uint streamIndex, int filter, out int enabled, out int level);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetVideoProcessorFilterRangeFn(
            IntPtr self, int filter, out VideoProcessorFilterRange range);

        /// <summary>
        /// <c>VideoProcessorBlt(self, processor, outputView, outputFrame, streamCount, streams)</c>
        /// —— <b>整个零拷贝路径上的一次真正的色彩转换</b>。
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int VideoProcessorBltFn(
            IntPtr self, IntPtr processor, IntPtr outputView, uint outputFrame, uint streamCount,
            ref VideoProcessorStream streams);
    }
}
