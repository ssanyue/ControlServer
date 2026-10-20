using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// 码率控制方式。
///
/// <para>
/// 🔥 <b>它不是"画质偏好"，而是"这个数字能不能拿来做结论"的前提。</b>
/// M5 决策 14 要回答"灰度模式省多少带宽" —— 那必须<b>固定质量</b>跑两遍再比字节数：
/// 换成 CBR，编码器会把两种内容都撑到同一个码率上，
/// 于是无论灰度有没有用，测出来都是"零收益"。
/// </para>
/// </summary>
internal enum EncoderRateControl
{
    /// <summary><c>eAVEncCommonRateControlMode_Quality</c> —— 固定质量。</summary>
    Quality,

    /// <summary><c>eAVEncCommonRateControlMode_CBR</c>。</summary>
    ConstantBitrate,

    /// <summary><c>eAVEncCommonRateControlMode_UnconstrainedVBR</c>。</summary>
    UnconstrainedVbr,
}

/// <summary>
/// 编码器的一组参数。
/// <para>
/// ⚠️ <b>尺寸在这里是"写死在实例上的"</b>：分辨率一变就要重建编码器（媒体类型里写死了尺寸，
/// 拿旧实例喂新尺寸的帧，表现是<b>报一个看不懂的 <c>MF_E_INVALIDMEDIATYPE</c></b>，
/// 而不是"画面被缩放"）。这与转换器、视频处理器是同一条规矩。
/// </para>
/// </summary>
/// <param name="BitrateBps">
/// 目标码率。<b>在 <see cref="EncoderRateControl.Quality"/> 下它只是上界/提示</b>，
/// 真正决定码流大小的是 <paramref name="Quality"/>。
/// </param>
/// <param name="Quality">1–100，仅 <see cref="EncoderRateControl.Quality"/> 下有效。</param>
/// <param name="GopSize">
/// 最大关键帧间隔。<b>必须有</b>：没有它，"新观众加入"就只能等到下一个自然关键帧 ——
/// 而那个时刻没有定义。
/// </param>
/// <param name="BPictureCount">
/// 两个参考帧之间最多插几个 <b>B 帧</b>。默认 <c>0</c> = 禁用。
/// <para>
/// 🔥 <b>禁用不是为了画质，是为了"喂给前端的东西不需要重排"</b>：有 B 帧就
/// 解码顺序 ≠ 显示顺序。本机实测"没有 B 帧"，但那是<b>靠驱动默认值</b>
/// （见方案 §6.1 #1 的挂账）—— 现在改成"我们要求的"。
/// </para>
/// <para>
/// ⚠️ 它<b>不一定设得上</b>（有些 MFT 不实现这条属性），实际结果记在
/// <see cref="WindowsMediaFoundationVideoEncoder.CodecApiNote"/> 里 ——
/// 而"设上了"也<b>只等于"我们要求了"</b>，不等于"码流里真的没有 B 帧"。
/// 判据只能是解析码流（<c>.tools/m5.4b-measure.py</c> 干的就是这件事）。
/// </para>
/// </param>
internal sealed record MediaFoundationEncoderOptions(
    VideoCodec Codec,
    int Width,
    int Height,
    int Fps,
    int BitrateBps,
    EncoderRateControl RateControl = EncoderRateControl.Quality,
    int Quality = 70,
    int GopSize = 60,
    bool LowLatency = true,
    int BPictureCount = 0,

    /// <summary>
    /// <b>关掉编码器侧的预处理</b>（M5.5 手段 10）。
    ///
    /// <para>
    /// 🔥 <b>默认 <c>true</c>，而且这是一个"关掉比打开好"的旋钮</b>：那些预处理
    /// （自适应量化、为相机视频调的时域降噪、心理视觉优化、UHQ 高端调优）
    /// <b>全是为自然图像设计的</b>。桌面的内容是<b>硬边矩形、大面积纯色、细小文字</b>，
    /// 它们不但不受益，还要让编码器每帧多跑一趟分析 —— 8K 下这一趟是实打实的时间，
    /// 而方案 §0.5 记着一个<b>反直觉的坑</b>：UHQ 模式会让 8K 吞吐<b>更差</b>。
    /// </para>
    /// <para>
    /// ⚠️ 代价是画质略降（方案原文："画质略降（可接受）"）。所以它留成开关 ——
    /// 真机上如果发现文字糊了，把它关掉就能回到驱动默认。
    /// </para>
    /// </summary>
    bool DisablePreprocessing = true,

    /// <summary>
    /// <b>一帧切成几片</b>（M5.5 手段 2：分帧编码 / SFE）。<c>0</c> = 不切（默认）。
    ///
    /// <para>
    /// 🔥 <b>它是 8K60 唯一的那根救命稻草</b>：单 NVENC 引擎编 8K 实测 27–32 fps，
    /// 而阈值是 60 —— 只有<b>让两个引擎各编一半</b>才可能到 62 fps。
    /// </para>
    /// <para>
    /// 🔴 <b>但 MF 侧能表达的只有 slice control，而它在语义上并不等于 SFE</b>：
    /// slice 本来是为"错误恢复"切的片，多数驱动照样在一个引擎上串行编完。
    /// 真正的 SFE 只有 Ada（RTX 40）的 NVENC 会做。
    /// ⇒ 这里<b>尽力而为</b>，把"设上了没有 / 读回是几"写进 <c>CodecApiNote</c>，
    /// 由真机上的读数说话，<b>不在这里替硬件打包票</b>。
    /// </para>
    /// </summary>
    int SliceCount = 0)
{
    /// <summary>一组能直接开跑的默认值（本批垂直切片用）。</summary>
    internal static MediaFoundationEncoderOptions Default(
        VideoCodec codec, int width, int height, int fps)
    {
        // 约 0.05 bit/像素/帧 再兜一个 8 Mbps 的地板：桌面内容在 Quality 模式下够用，
        // 而且这个数是"上界提示"而不是硬约束，拍得保守一点没有副作用。
        var estimate = (long)width * height * fps / 20;
        var bitrate = (int)Math.Clamp(estimate, 8_000_000, 80_000_000);

        return new MediaFoundationEncoderOptions(codec, width, height, fps, bitrate);
    }

    /// <summary>一帧的时长（100 纳秒单位，MF 的时间基）。</summary>
    internal long SampleDuration => Fps > 0 ? 10_000_000L / Fps : 166_667L;
}

/// <summary>
/// <b>Media Foundation 硬件视频编码器</b>（M5.4 零拷贝管线的末端）。
///
/// <para>
/// 🔥 <b>本类的全部价值在一个字上：零拷贝。</b>喂进去的是<b>纹理句柄</b>，
/// 吐出来的是<b>码流字节</b> —— 中间那 33 MB（4K）/ 133 MB（8K）的像素
/// <b>一次都不经过 CPU</b>。这正是 <see cref="IVideoEncoder"/> 当初刻意
/// 不写成 <c>byte[] TryEncode(byte[])</c> 的原因。
/// </para>
///
/// <para>
/// <b>它建立在共享的 <see cref="D3D11GpuContext"/> 上</b>：抓屏、色彩转换、编码三者
/// 必须是同一个 D3D11 设备，否则要么编码器拒绝纹理、要么驱动偷偷做一次
/// "回 CPU 再上去"的搬运 —— 那件事的代价是 1.99 GB/s，而它<b>不会报任何错</b>。
/// </para>
///
/// <para>
/// 🔥 <b>异步 MFT 的握手方式与同步 MFT 完全不同，本类两条路都实现了：</b>
/// </para>
/// <list type="bullet">
///   <item><b>异步</b>（硬件编码器几乎都是）：MFT 通过 <c>IMFMediaEventGenerator</c>
///         抛 <c>METransformNeedInput</c>(601) / <c>METransformHaveOutput</c>(602)
///         <b>事件</b>来要输入、交输出。⚠️ 那两个是<b>枚举值不是 GUID</b>，
///         按 GUID 去查会一无所获，症状是"编码器一帧都不出"。</item>
///   <item><b>同步</b>：靠 <c>GetInputStatus</c> 问"收不收"，靠 <c>ProcessOutput</c> 轮询取，
///         没编出来时回 <c>MF_E_TRANSFORM_NEED_MORE_INPUT</c>（<b>那是正常状态，不是错误</b>）。</item>
/// </list>
///
/// <para>
/// ⚠️ <b>本类内部的纹理环（<see cref="TextureRingDepth"/> 张）是刻意的，不是多余拷贝。</b>
/// 异步编码器<b>会同时持有好几帧输入</b>（QSV 一类有 lookahead）。
/// 若每帧都把内容 <c>Blt</c> 进同一张纹理再喂进去，编码器正在读的那帧
/// 会被下一帧覆盖 —— 表现是<b>画面偶尔撕裂/花屏</b>，而且越忙越明显。
/// 所以每帧先 <c>CopyResource</c> 到环上的下一张（<b>显存内，不过 PCIe</b>），
/// 保证"喂出去的那张"在编码器用完之前不会被改写。
/// 代价是每帧一次 GPU 拷贝；M5.4-b 可以让色彩转换器<b>直接产出环上的纹理</b>来消掉它。
/// </para>
///
/// <para>
/// ⚠️ <b>线程亲和</b>：一个实例不是线程安全的（<c>ProcessInput</c>/<c>ProcessOutput</c>/事件队列
/// 三者必须同一个线程串行操作），内部用一把锁兜底。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsMediaFoundationVideoEncoder : IVideoEncoder
{
    /// <summary>输入纹理环的深度。见类注释里"为什么要一个环"。</summary>
    private const int TextureRingDepth = 3;

    /// <summary>排队的输出帧上限。超了说明调用方一直没来取 —— 那种情况该丢的是<b>旧</b>帧。</summary>
    private const int MaxQueuedOutputs = 8;

    /// <summary>等 <c>METransformDrainComplete</c> 的最多轮数（每轮 2 ms）。</summary>
    private const int MaxDrainPolls = 100;

    /// <summary><c>MFT_INPUT_STATUS_ACCEPT_DATA</c>。</summary>
    private const uint InputStatusAcceptData = 0x1;

    private readonly D3D11GpuContext _gpu;
    private readonly MediaFoundationEncoderOptions _options;
    private readonly object _gate = new();
    private readonly Queue<EncodedFrame> _ready = new();

    private IntPtr _transform;
    private IntPtr _events;         // IMFMediaEventGenerator（异步 MFT 才有）
    private IntPtr _codecApi;       // ICodecAPI（参数都从这里设）
    private IntPtr _outputType;

    private readonly IntPtr[] _ring = new IntPtr[TextureRingDepth];

    private int _ringIndex;
    private long _sampleTime;
    private long _sequence;

    private long _needInputTokens;
    private long _haveOutputTokens;
    private bool _drainComplete;

    /// <summary>
    /// 🔥 <b>收到过的事件类型 → 次数</b>。
    ///
    /// <para>
    /// 这一格是**被一次真实的空白逼出来的**：曾出现"编码器建好了、参数全设上了、
    /// 却一个令牌都收不到、一帧都不出"，而当时的日志只有
    /// <c>输入令牌 0 / 输出令牌 0</c> —— 那个数字<b>说不清是"没有事件"还是"事件都白收了"</b>，
    /// 而这两件事的处置完全相反（一个去查 MFT 状态机，一个去查槽位号）。
    /// </para>
    /// <para>
    /// ⚠️ 这不是调试残留，是**结构性缺口**：事件通路只有"计数"没有"分类"时，
    /// 任何"握手不成立"的故障都长得一模一样。
    /// </para>
    /// </summary>
    private readonly Dictionary<uint, int> _eventCounts = new();

    /// <summary>读事件类型**失败**的次数（<c>IMFMediaEvent::GetType</c> 没成功）。</summary>
    private long _eventTypeReadFailures;

    /// <summary>一轮 <see cref="PumpEvents"/> 取满上限还没取干净的次数（可能是死循环前兆）。</summary>
    private long _eventGuardHits;

    /// <summary>
    /// 编码器要求"重新谈一次输出类型"的次数（<c>MF_E_TRANSFORM_STREAM_CHANGE</c>）。
    /// <para>
    /// 🔥 <b>这几乎必然在第一次输出时发生一次</b>：QSV 的 H.264 编码器要先把
    /// 它自己真正用的那个输出类型（带 profile/level 与 SPS/PPS）报出来。
    /// 它不是故障，但<b>不处理它就一帧都出不来</b>，所以必须计数 ——
    /// "一直是 0" 说明这条路径根本没被走到。
    /// </para>
    /// </summary>
    private long _streamChanges;

    /// <summary>
    /// 去要输出时编码器回 <c>E_UNEXPECTED + NO_SAMPLE</c> 的次数。
    /// <para>
    /// ⚠️ 这是<b>异常但合法</b>的状态（本该是 <c>MF_E_TRANSFORM_NEED_MORE_INPUT</c>）。
    /// 按令牌门控之后它应该是 0；不是 0 就说明还在"没东西却去要"。
    /// </para>
    /// </summary>
    private long _emptyOutputPolls;

    private long _framesFed;
    private long _framesDroppedBusy;
    private long _framesOut;
    private long _keyframeFlagMissing;

    private bool _streaming;
    private bool _endOfStreamSent;
    private bool _keyframeRequested;
    private bool _grayscale;
    private bool _disposed;

    private string _lastFailure = "（还没有失败过）";

    private WindowsMediaFoundationVideoEncoder(D3D11GpuContext gpu, MediaFoundationEncoderOptions options)
    {
        _gpu = gpu;
        _options = options;

        Capabilities = new EncoderCapabilities(
            SupportedCodecs: new[] { options.Codec },
            // ⚠️ 下面这些**一律 null**：本类没有去探它们。
            //    按本仓铁律「未知一律 null，不用 0 代替」—— 填 0 会被读成"这台机器不支持"。
            MaxWidth: null,
            MaxHeight: null,
            MaxFps: null,
            MaxPixelsPerSecond: null,
            EncodeEngines: null,
            SupportsTenBit: null,
            SupportsSplitEncode: null,
            AdapterName: gpu.AdapterName,
            EngineBasis: "Media Foundation 硬编码器 MFT（本实例正在使用的那一个）");
    }

    /// <inheritdoc />
    public VideoCodec Codec => _options.Codec;

    /// <inheritdoc />
    public EncoderCapabilities Capabilities { get; }

    /// <summary>
    /// 🔥 <b>黑白模式</b>：本类<b>不改像素</b>，像素层的活是上游
    /// <see cref="D3D11Nv12Converter"/> 干的（它把色度压成常量 128，喂进来的就已经是灰的）。
    ///
    /// <para>
    /// 那这里为什么还要有它？因为<b>内容发生了不连续</b>：画面从彩色切到灰度，
    /// 已解码的历史帧全是彩色的。不重新起一个 GOP，观众会看到
    /// 「切了开关 → 画面慢慢从彩色渐变到灰度」，而那看起来就像<b>坏了</b>。
    /// ⇒ 置位时自动置一次"下一个关键帧"。
    /// </para>
    /// <para>
    /// 🔸 <b>刻意不做的事</b>：不去动 SPS/PPS/VUI（例如把
    /// <c>chroma_format_idc</c> 改成 0 做真 4:0:0）。输出仍是<b>标准 4:2:0</b>码流，
    /// 于是<b>解码端零改动</b> —— 这是 M5 决策 14 选这条路的原因。
    /// </para>
    /// </summary>
    public bool Grayscale
    {
        get
        {
            lock (_gate) return _grayscale;
        }

        set
        {
            lock (_gate)
            {
                if (_grayscale == value) return;

                _grayscale = value;
                _keyframeRequested = true;
            }
        }
    }

    /// <inheritdoc />
    public void RequestKeyframe()
    {
        lock (_gate) _keyframeRequested = true;
    }

    // ═══════════════════════════════════════════════════════════
    // 诊断面（给用例与现场报告用；生产路径不读它）
    // ═══════════════════════════════════════════════════════════

    /// <summary>实际用的那个 MFT 的显示名（现场排障第一个要问的东西）。</summary>
    internal string MftName { get; private set; } = "未知";

    /// <summary>是不是异步 MFT（决定握手方式）。</summary>
    internal bool IsAsync { get; private set; }

    /// <summary>
    /// <c>IMFDXGIDeviceManager</c> 有没有被编码器接受（<c>SET_D3D_MANAGER</c> 没报错）。
    ///
    /// <para>
    /// ⚠️ <b>它不是"零拷贝成立"。</b> 它只说"这条消息编进 MFT 了" ——
    /// 设备管理器手上到底有没有一块能用的设备，是另一件事，
    /// 由 <see cref="D3D11GpuContext.DeviceHandleVerified"/> 说
    /// （那个才是真的开过一次句柄）。出厂报告里这两格<b>必须分开写</b>：
    /// 合并成一句"零拷贝成立"，就等于把"消息发出去了"当成"链路通了"。
    /// </para>
    /// </summary>
    internal bool DeviceManagerAttached { get; private set; }

    /// <summary>码率控制实际设成了什么（CodecAPI 支持情况逐条记）。</summary>
    internal string CodecApiNote { get; private set; } = "未配置";

    /// <summary>低延迟有没有真的设上。</summary>
    internal bool LowLatencyApplied { get; private set; }

    /// <summary>编码器有没有给样本打 <c>MFSampleExtension_CleanPoint</c>。</summary>
    internal bool KeyframeFlagAvailable => _keyframeFlagMissing == 0;

    /// <summary>喂进去的帧数。</summary>
    internal long FramesFed => _framesFed;

    /// <summary>因为编码器忙而丢掉的帧数（<b>丢帧不是异常</b>，但必须能被看见）。</summary>
    internal long FramesDroppedBusy => _framesDroppedBusy;

    /// <summary>编出来的帧数。</summary>
    internal long FramesOut => _framesOut;

    /// <summary>还在队列里没被取走的输出帧数。</summary>
    internal int QueuedOutputs
    {
        get
        {
            lock (_gate) return _ready.Count;
        }
    }

    /// <summary><c>END_OF_STREAM</c> 之后有没有等到 <c>METransformDrainComplete</c>。</summary>
    internal bool DrainCompleted => _drainComplete;

    /// <summary>最近一次失败的具体原因（空跑不报错的场合全靠它）。</summary>
    internal string LastFailure => _lastFailure;

    /// <summary>
    /// 🔥 <b>零拷贝那条链子长什么样</b>（如实写出来，包括唯一多出来的那一步）。
    /// </summary>
    internal string ZeroCopyNote =>
        $"抓屏纹理 →(视频处理器 Blt)→ NV12 →(GPU CopyResource，显存内不过 PCIe)→ 环上第 N 张 →"
        + $"(MFCreateDXGISurfaceBuffer，只传句柄)→ {MftName}"
        + "⇒ 全程 CPU 不碰像素，也不存在把 NV12/BGRA Map 到内存的路径。";

    // ═══════════════════════════════════════════════════════════
    // 建立
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>建一个真的能编的硬件编码器</b>。
    ///
    /// <para>
    /// 返回 <c>null</c> 时 <paramref name="failure"/> 一定说明了原因 ——
    /// 包括"枚举到了 MFT 但每一个都建不起来"（那时原因里会带上逐个失败的过程）。
    /// </para>
    /// </summary>
    internal static WindowsMediaFoundationVideoEncoder? TryCreate(
        D3D11GpuContext gpu, MediaFoundationEncoderOptions options, out string? failure)
    {
        failure = null;

        if (options.Width <= 1 || options.Height <= 1)
        {
            failure = $"尺寸 {options.Width}×{options.Height} 太小，建不了编码器。";

            return null;
        }

        // NV12 的 4:2:0 半平面要求宽高偶数（与转换器同一条约束）。
        // ⚠️ 这里再判一次不是重复劳动：**编码器可能被单独拿来用**（不经转换器），
        //    而奇数尺寸的 NV12 在编码器里的表现是"接受类型、编出来是糊的"。
        if (options.Width % 2 != 0 || options.Height % 2 != 0)
        {
            failure = $"尺寸 {options.Width}×{options.Height} 不是偶数 —— NV12（4:2:0 半平面）要求宽高都是偶数。";

            return null;
        }

        if (gpu.Device == IntPtr.Zero)
        {
            failure = "这个 D3D11 设备是空的 —— 编码器必须建在抓屏/转换那同一个设备上。";

            return null;
        }

        if (gpu.DeviceManager == IntPtr.Zero)
        {
            failure = "共享设备上没有 IMFDXGIDeviceManager —— 没有它，编码器只能吃内存里的样本，"
                + "零拷贝不成立（而它不会报错，只会悄悄多一次 PCIe 往返）。";

            return null;
        }

        if (!TryEnumerateActivates(options.Codec, out var activates, out var count, out failure)) return null;

        var notes = new List<string>();
        var encoder = new WindowsMediaFoundationVideoEncoder(gpu, options);

        try
        {
            for (var i = 0; i < count; i++)
            {
                var activate = Marshal.ReadIntPtr(activates, i * IntPtr.Size);

                try
                {
                    var name = MediaFoundationEncoders.TryReadFriendlyName(activate) ?? $"(第 {i} 条，读不到名字)";

                    if (encoder.TryInitialize(activate, name, out var why)) return encoder;

                    notes.Add($"  [{i}] {name}：{why}");
                }
                catch (Exception ex)
                {
                    notes.Add($"  [{i}] 建的时候抛出 {ex.GetType().Name}：{ex.Message}");
                }
            }

            // 全部失败 ⇒ 把逐个原因带出去。只说"失败了"在现场等于没写。
            failure = $"本机枚举到 {count} 个 {options.Codec} 硬件编码器，但每一个都建不起来："
                + Environment.NewLine + string.Join(Environment.NewLine, notes);

            return encoder.DisposeAndReturn();
        }
        finally
        {
            FreeActivates(activates, count);
        }
    }

    /// <summary>
    /// 枚举本机<b>硬件</b>编码器。
    /// <para>
    /// ⚠️ 只加 <c>MFT_ENUM_FLAG_HARDWARE</c>（本机实测可用的组合）：
    /// 不加它会把微软的软编 <c>"H264 Encoder MFT"</c> 也列进来 ——
    /// 那正是 4K60 达不到的原因，而且<b>它会正常工作</b>，只是在 CPU 上。
    /// </para>
    /// </summary>
    private static bool TryEnumerateActivates(
        VideoCodec codec, out IntPtr activates, out uint count, out string? failure)
    {
        activates = IntPtr.Zero;
        count = 0;
        failure = null;

        var subtype = codec == VideoCodec.H264
            ? MediaFoundationInterop.MFVideoFormat_H264
            : MediaFoundationInterop.MFVideoFormat_HEVC;

        var builder = new MediaFoundationInterop.MftRegisterTypeInfo
        {
            Major = MediaFoundationInterop.MFMediaType_Video,
            Sub = subtype,
        };

        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<MediaFoundationInterop.MftRegisterTypeInfo>());

        try
        {
            Marshal.StructureToPtr(builder, buffer, false);

            int hr;

            try
            {
                // 返回值不检查：MF 已经初始化过时返回 S_FALSE，那不是失败。
                MediaFoundationInterop.MFStartup((int)MediaFoundationInterop.MfVersion, 0);

                hr = MediaFoundationInterop.MFTEnumEx(
                    MediaFoundationInterop.CategoryVideoEncoder,
                    MediaFoundationInterop.EnumFlagHardware,
                    IntPtr.Zero,
                    buffer,
                    out activates,
                    out count);
            }
            catch (DllNotFoundException)
            {
                failure = "找不到 mfplat.dll —— 这台机器上没有 Media Foundation。";

                return false;
            }
            catch (EntryPointNotFoundException)
            {
                failure = "mfplat.dll 里没有 MFTEnumEx —— MF 版本太旧。";

                return false;
            }

            if (!ComVtable.Succeeded(hr))
            {
                failure = $"MFTEnumEx 失败（{ComVtable.Describe(hr)}）。";

                return false;
            }

            if (count == 0 || activates == IntPtr.Zero)
            {
                failure = $"本机<b>没有 {codec} 的硬件编码器</b>（MFTEnumEx 查的是注册表里的注册项，"
                    + "与当前有没有编码活动无关，所以这个结论是可信的）。";

                return false;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void FreeActivates(IntPtr activates, uint count)
    {
        if (activates == IntPtr.Zero) return;

        for (var i = 0; i < count; i++)
        {
            var activate = Marshal.ReadIntPtr(activates, i * IntPtr.Size);

            if (activate != IntPtr.Zero) Marshal.Release(activate);
        }

        // ⚠️ 数组本身由 COM 任务分配器给出 ⇒ 只能 CoTaskMemFree。
        MediaFoundationInterop.CoTaskMemFree(activates);
    }

    /// <summary>按"能不能真的用"的顺序把这一步做完：解锁 → 激活 → 挂设备 → 设类型 → 参数 → 起流。</summary>
    private bool TryInitialize(IntPtr activate, string name, out string? failure)
    {
        failure = null;
        MftName = name;

        // ── ① 异步解锁必须在 ActivateObject **之前**写在这个 activate 上 ──
        //    🔥 顺序错了的报应是 `ActivateObject` 回 `MF_E_TRANSFORM_ASYNC_LOCKED`，
        //    而那条错误信息**不会告诉你"要去设这个属性"**。
        var unlocked = TrySetUInt32(activate, MediaFoundationInterop.MF_TRANSFORM_ASYNC_UNLOCK, 1);

        // 读不到 MF_TRANSFORM_ASYNC 不算失败：同步 MFT 本来就没有这个属性。
        _ = TryGetUInt32(activate, MediaFoundationInterop.MF_TRANSFORM_ASYNC, out var asyncFlag);

        IsAsync = asyncFlag != 0;

        if (IsAsync && !unlocked)
        {
            // 不直接失败：有些驱动把"设不上"表现为"本来就不需要设"。
            // 但**必须记下来** —— 真出问题时，这一条是第一个要看的。
            RecordFailure($"在 activate 上设 MF_TRANSFORM_ASYNC_UNLOCK 没成功（MFT「{name}」）。");
        }

        if (_options.LowLatency)
        {
            // 同理，低延迟也在 activate 上设（MF 建拓扑时就是这么做的）。
            _ = TrySetUInt32(activate, MediaFoundationInterop.MF_LOW_LATENCY, 1);
        }

        var iid = MediaFoundationInterop.IID_IMFTransform;

        var activateFn = ComVtable.Bind<MediaFoundationInterop.Delegates.ActivateObjectFn>(
            activate, MediaFoundationInterop.ActivateSlots.ActivateObject);

        var hr = activateFn(activate, ref iid, out _transform);

        if (!ComVtable.Succeeded(hr))
        {
            failure = hr == MediaFoundationInterop.HResults.TransformAsyncLocked
                ? "这是个异步 MFT 且处于锁定状态，而我们在 activate 上设 MF_TRANSFORM_ASYNC_UNLOCK 也没有解开它。"
                : $"ActivateObject 失败（{ComVtable.Describe(hr)}）。";

            return false;
        }

        // ── ② 事件发生器：异步 MFT 必须有，同步 MFT 可能没有 ──
        var eventIid = MediaFoundationInterop.IID_IMFMediaEventGenerator;

        if (ComVtable.Succeeded(ComVtable.QueryInterfaceRaw(_transform, ref eventIid, out var events)))
        {
            _events = events;
            IsAsync = true;
        }
        else if (IsAsync)
        {
            failure = "这个 MFT 自报异步，但拿不到 IMFMediaEventGenerator —— 按异步握手会永远等不到输入令牌。";

            return false;
        }

        // 顺手在实例上也解一次锁（对已经解锁的 MFT 是无副作用的重复）。
        if (IsAsync && TryGetAttributes(_transform, out var attrs))
        {
            _ = TrySetUInt32(attrs, MediaFoundationInterop.MF_TRANSFORM_ASYNC_UNLOCK, 1);
        }

        // ── ③ 挂设备管理器：**必须在设类型之前** ──
        //    输入类型是"NV12 纹理"还是"NV12 内存样本"，取决于编码器在不在 D3D11 模式，
        //    而这个模式就是这一条消息切换的。顺序反了 ⇒ 类型谈得成、但走的是 CPU 路径，
        //    于是"零拷贝"名存实亡，且没有任何一处报错。
        if (!AttachDeviceManager(out failure)) return false;

        // ── ④ 类型：编码器必须**先输出后输入** ──
        if (!TrySetOutputType(out failure)) return false;

        if (!TrySetInputType(out failure)) return false;

        // ── ⑤ CodecAPI 参数 ──
        ConfigureCodecApi();

        // ── ⑥ 起流 ──
        if (!StartStreaming(out failure)) return false;

        return true;
    }

    private bool AttachDeviceManager(out string? failure)
    {
        failure = null;

        var processMessage = ComVtable.Bind<MediaFoundationInterop.Delegates.ProcessMessageFn>(
            _transform, MediaFoundationInterop.TransformSlots.ProcessMessage);

        var hr = processMessage(
            _transform, MediaFoundationInterop.MessageSetD3DManager, _gpu.DeviceManager);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"MFT_MESSAGE_SET_D3D_MANAGER 失败（{ComVtable.Describe(hr)}）—— "
                + "这个编码器不接受共享的 D3D11 设备管理器。"
                + "⚠️ 这一条失败**不允许降级**：降级成「喂 CPU 内存里的样本」会让 4K60 的 PCIe 流量"
                + "变成 1.99 GB/s × 2，而那正是本方案存在的理由。";

            return false;
        }

        DeviceManagerAttached = true;

        return true;
    }

    private bool TrySetOutputType(out string? failure)
    {
        failure = null;

        // 先试"我们自己构造的类型"（档次、码率都写明确）。
        if (TryBuildOutputType(out var desired, out var buildFailure))
        {
            if (TryApplyType(output: true, desired, out var hr))
            {
                _outputType = desired;

                return true;
            }

            ComVtable.ReleasePointer(ref desired);

            // 退一步：拿编码器自己给的第一种输出类型，只往上盖尺寸/帧率/码率。
            // 🔥 这不是"妥协"，是**避免把"我们猜的档次"当成"编码器支持的档次"** ——
            //    某些硬编器只支持特定的 profile/level 组合。
            if (TryUseFirstAvailableOutputType(out failure)) return true;

            failure = $"自己构造的输出类型不被接受（{ComVtable.Describe(hr)}），"
                + $"而退回到编码器自带的类型也没成：{failure}";

            return false;
        }

        failure = buildFailure;

        return false;
    }

    private bool TryBuildOutputType(out IntPtr type, out string? failure)
    {
        type = IntPtr.Zero;
        failure = null;

        var hr = MediaFoundationInterop.MFCreateMediaType(out type);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"MFCreateMediaType 失败（{ComVtable.Describe(hr)}）。";

            return false;
        }

        var subtype = _options.Codec == VideoCodec.H264
            ? MediaFoundationInterop.MFVideoFormat_H264
            : MediaFoundationInterop.MFVideoFormat_HEVC;

        var profile = _options.Codec == VideoCodec.H264
            ? MediaFoundationInterop.H264ProfileHigh
            : MediaFoundationInterop.HevcProfileMain4208;

        var ok = TrySetGuid(type, MediaFoundationInterop.MF_MT_MAJOR_TYPE, MediaFoundationInterop.MFMediaType_Video)
            && TrySetGuid(type, MediaFoundationInterop.MF_MT_SUBTYPE, subtype)
            && TrySetUInt64(type, MediaFoundationInterop.MF_MT_FRAME_SIZE, Size())
            && TrySetUInt64(type, MediaFoundationInterop.MF_MT_FRAME_RATE, Rate())
            && TrySetUInt64(type, MediaFoundationInterop.MF_MT_PIXEL_ASPECT_RATIO, Ratio())
            && TrySetUInt32(type, MediaFoundationInterop.MF_MT_INTERLACE_MODE, MediaFoundationInterop.InterlaceProgressive)
            && TrySetUInt32(type, MediaFoundationInterop.MF_MT_AVG_BITRATE, (uint)_options.BitrateBps)
            && TrySetUInt32(type, MediaFoundationInterop.MF_MT_MPEG2_PROFILE, profile)
            && TrySetUInt32(type, MediaFoundationInterop.MF_MT_MAX_KEYFRAME_SPACING, (uint)_options.GopSize);

        if (!ok)
        {
            failure = "往输出类型上写属性失败 —— 这些属性里没有一个是可选的。";

            return false;
        }

        return true;
    }

    private bool TryUseFirstAvailableOutputType(out string? failure)
    {
        failure = null;

        var getType = ComVtable.Bind<MediaFoundationInterop.Delegates.GetAvailableTypeFn>(
            _transform, MediaFoundationInterop.TransformSlots.GetOutputAvailableType);

        var hr = getType(_transform, 0, 0, out var available);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"GetOutputAvailableType(0) 失败（{ComVtable.Describe(hr)}）—— "
                + "连编码器自己提供的类型都拿不到，这个 MFT 基本是不可用的。";

            return false;
        }

        try
        {
            // 只覆盖"必须与我们的输入对得上"的两项，其余保留编码器的取值。
            _ = TrySetUInt64(available, MediaFoundationInterop.MF_MT_FRAME_SIZE, Size());
            _ = TrySetUInt64(available, MediaFoundationInterop.MF_MT_FRAME_RATE, Rate());
            _ = TrySetUInt32(available, MediaFoundationInterop.MF_MT_AVG_BITRATE, (uint)_options.BitrateBps);
            _ = TrySetUInt32(available, MediaFoundationInterop.MF_MT_MAX_KEYFRAME_SPACING, (uint)_options.GopSize);

            if (TryApplyType(output: true, available, out var setHr))
            {
                _outputType = available;

                // 交给字段接管引用，这里不能再释放。
                available = IntPtr.Zero;

                return true;
            }

            failure = $"用编码器自带的输出类型也不行（{ComVtable.Describe(setHr)}）。";

            return false;
        }
        finally
        {
            ComVtable.ReleasePointer(ref available);
        }
    }

    private bool TrySetInputType(out string? failure)
    {
        failure = null;

        var hr = MediaFoundationInterop.MFCreateMediaType(out var type);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"MFCreateMediaType 失败（{ComVtable.Describe(hr)}）。";

            return false;
        }

        try
        {
            var ok = TrySetGuid(type, MediaFoundationInterop.MF_MT_MAJOR_TYPE, MediaFoundationInterop.MFMediaType_Video)
                && TrySetGuid(type, MediaFoundationInterop.MF_MT_SUBTYPE, MediaFoundationInterop.MFVideoFormat_NV12)
                && TrySetUInt64(type, MediaFoundationInterop.MF_MT_FRAME_SIZE, Size())
                && TrySetUInt64(type, MediaFoundationInterop.MF_MT_FRAME_RATE, Rate())
                && TrySetUInt64(type, MediaFoundationInterop.MF_MT_PIXEL_ASPECT_RATIO, Ratio())
                && TrySetUInt32(type, MediaFoundationInterop.MF_MT_INTERLACE_MODE, MediaFoundationInterop.InterlaceProgressive)
                && TrySetUInt32(type, MediaFoundationInterop.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);

            if (!ok)
            {
                failure = "往 NV12 输入类型上写属性失败。";

                return false;
            }

            if (TryApplyType(output: false, type, out var setHr)) return true;

            failure = $"编码器不接受 NV12 输入（{ComVtable.Describe(setHr)}）。"
                + "⚠️ NV12 是所有硬件编码器的输入格式；它都不认，说明这个 MFT 不是视频编码器，"
                + "或者被别的进程独占着。";

            return false;
        }
        finally
        {
            ComVtable.ReleasePointer(ref type);
        }
    }

    private bool TryApplyType(bool output, IntPtr type, out int hr)
    {
        var setType = ComVtable.Bind<MediaFoundationInterop.Delegates.SetTypeFn>(
            _transform, output
                ? MediaFoundationInterop.TransformSlots.SetOutputType
                : MediaFoundationInterop.TransformSlots.SetInputType);

        hr = setType(_transform, 0, type, 0);

        return ComVtable.Succeeded(hr);
    }

    private void ConfigureCodecApi()
    {
        var notes = new List<string>();

        var iid = MediaFoundationInterop.IID_ICodecAPI;

        if (!ComVtable.Succeeded(ComVtable.QueryInterfaceRaw(_transform, ref iid, out var codecApi)))
        {
            CodecApiNote = "编码器不提供 ICodecAPI ⇒ 码控模式/质量/GOP 只能用媒体类型里的默认值。";

            return;
        }

        _codecApi = codecApi;

        var mode = _options.RateControl switch
        {
            EncoderRateControl.ConstantBitrate => MediaFoundationInterop.RateControlCbr,
            EncoderRateControl.UnconstrainedVbr => MediaFoundationInterop.RateControlUnconstrainedVbr,
            _ => MediaFoundationInterop.RateControlQuality,
        };

        TrySetCodecUInt32(
            MediaFoundationInterop.CODECAPI_AVEncCommonRateControlMode, (uint)mode,
            "码控模式", notes);

        if (_options.RateControl == EncoderRateControl.Quality)
        {
            TrySetCodecUInt32(
                MediaFoundationInterop.CODECAPI_AVEncCommonQuality, (uint)Math.Clamp(_options.Quality, 1, 100),
                "质量", notes);
        }

        TrySetCodecUInt32(
            MediaFoundationInterop.CODECAPI_AVEncCommonMeanBitRate, (uint)_options.BitrateBps,
            "平均码率", notes);

        TrySetCodecUInt32(
            MediaFoundationInterop.CODECAPI_AVEncMPVGOPSize, (uint)_options.GopSize,
            "GOP", notes);

        // 🔥 禁 B 帧（理由见 MediaFoundationEncoderOptions.BPictureCount）。
        //    ⚠️ 这一条与"低延迟"是两件事：低延迟关的是 lookahead/重排缓冲，
        //       禁 B 帧改的是**这个码流里到底有没有需要重排的帧**。
        //       少了它，前端那条 WebCodecs 路径就要多一整套 POC 重排逻辑。
        TrySetCodecUInt32(
            MediaFoundationInterop.CODECAPI_AVEncMPVDefaultBPictureCount,
            (uint)Math.Max(_options.BPictureCount, 0),
            "B 帧上限", notes);

        if (_options.LowLatency)
        {
            // ⚠️ 这一条要的是 VT_BOOL，不是 VT_UI4（拿错类型会回 E_INVALIDARG）。
            var latency = MediaFoundationInterop.VariantBool.True;

            var setBool = ComVtable.Bind<MediaFoundationInterop.Delegates.CodecApiSetValueBoolFn>(
                _codecApi, MediaFoundationInterop.CodecApiSlots.SetValue);

            var api = MediaFoundationInterop.CODECAPI_AVEncCommonLowLatency;

            var hr = setBool(_codecApi, ref api, ref latency);

            LowLatencyApplied = ComVtable.Succeeded(hr);

            notes.Add($"低延迟：{(LowLatencyApplied ? "已设" : $"设不上（{ComVtable.Describe(hr)}）")}");
        }
        else
        {
            notes.Add("低延迟：本次不要求");
        }

        // ═══════════════════════════════════════════════════════════
        // M5.5 手段 10：关掉编码器侧预处理
        // ═══════════════════════════════════════════════════════════
        //
        // 🔥 这一组**每一条都读回**，而不是设完就记"已设"。
        //    理由是本仓已经踩过两次同一类坑（`AllowedCidrs` 存在但没人消费；
        //    `CODECAPI_AVEncCommonMeanBitRate` 运行中改回读不变）——
        //    「调用成功」与「参数真的变了」之间隔着一整个驱动的实现自由。
        if (_options.DisablePreprocessing)
        {
            // ① 场景声明：这是**桌面推流**，不是相机直播。
            //    最有语义的一条 —— 让编码器自己换一套为桌面优化的默认策略。
            TrySetCodecUInt32Verified(
                MediaFoundationInterop.CODECAPI_AVScenarioInfo,
                MediaFoundationInterop.ScenarioInfoDisplayRemoting,
                "场景=桌面推流", notes);

            // ② 自适应量化（AQ）：逐帧分析画面再分配码率。桌面内容收益小、开销实。
            TrySetCodecUInt32Verified(
                MediaFoundationInterop.CODECAPI_AVEncAdaptiveMode,
                MediaFoundationInterop.AdaptiveModeNone,
                "自适应量化=关", notes);

            // ③ 画质/速度倾向 = 最快。这条专门防 §0.5 那个 UHQ 反向坑。
            TrySetCodecUInt32Verified(
                MediaFoundationInterop.CODECAPI_AVEncCommonQualityVsSpeed,
                0, "画质倾向=最快(防UHQ)", notes);

            // ④ 参考帧只要 1 张：桌面推流不需要多参考，而 8K 一张 NV12 参考帧是 50 MB。
            TrySetCodecUInt32Verified(
                MediaFoundationInterop.CODECAPI_AVEncVideoMaxNumRefFrame,
                1, "参考帧=1", notes);
        }
        else
        {
            // ⚠️ 关掉这个开关时**什么都不写**，而不是写回"某个默认值"：
            //    我们并不知道驱动的默认值是多少（GetDefaultValue 能问，但那是驱动说的、
            //    未必是它实际在用的）。不动它，才是真的"回到原样"。
            notes.Add("预处理：本次不关（用驱动默认）");
        }

        // ═══════════════════════════════════════════════════════════
        // M5.5 手段 2：分帧编码（SFE）—— 尽力而为
        // ═══════════════════════════════════════════════════════════
        if (_options.SliceCount > 0)
        {
            TrySetCodecUInt32Verified(
                MediaFoundationInterop.CODECAPI_AVEncSliceControlMode,
                MediaFoundationInterop.SliceControlModeUniform,
                "切片模式=均匀", notes);

            TrySetCodecUInt32Verified(
                MediaFoundationInterop.CODECAPI_AVEncSliceControlSize,
                (uint)_options.SliceCount,
                $"每帧 {_options.SliceCount} 片", notes);

            // 🔴 这句话必须留下，而且必须在**与"已设"同一行**的位置：
            //    真机测试的人看到"每帧 2 片：已设 2（读回 2）"很容易以为 8K60 成了，
            //    而实际上驱动完全可以读回 2 却仍在一个引擎上串行编完。
            //    唯一的判据是**实测帧率**，不是这个读数。
            notes.Add("SFE：已请求切片；⚠️ 是否真的双引擎并行只能看实测帧率（本读数不作数）");
        }

        CodecApiNote = string.Join("；", notes);
    }

    private bool StartStreaming(out string? failure)
    {
        failure = null;

        var processMessage = ComVtable.Bind<MediaFoundationInterop.Delegates.ProcessMessageFn>(
            _transform, MediaFoundationInterop.TransformSlots.ProcessMessage);

        // 两个都要发：BEGIN_STREAMING 是"整个 MFT 开始干活"，
        // START_OF_STREAM 是"一段流从这里开始"（它还负责让编码器接受第一帧的时间戳）。
        var hr = processMessage(_transform, MediaFoundationInterop.MessageNotifyBeginStreaming, 0);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"MFT_MESSAGE_NOTIFY_BEGIN_STREAMING 失败（{ComVtable.Describe(hr)}）。";

            return false;
        }

        hr = processMessage(_transform, MediaFoundationInterop.MessageNotifyStartOfStream, 0);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"MFT_MESSAGE_NOTIFY_START_OF_STREAM 失败（{ComVtable.Describe(hr)}）。";

            return false;
        }

        _streaming = true;

        return true;
    }

    // ═══════════════════════════════════════════════════════════
    // 每帧
    // ═══════════════════════════════════════════════════════════

    /// <inheritdoc />
    public bool TryEncode(nint textureHandle, int width, int height, bool forceKeyframe, out EncodedFrame? frame)
    {
        lock (_gate)
        {
            var callStartedAt = Stopwatch.GetTimestamp();

            frame = null;

            if (_disposed || _transform == IntPtr.Zero) return false;

            if (width != _options.Width || height != _options.Height)
            {
                // 尺寸不匹配是**实例级**的用法错误（媒体类型里写死了尺寸），必须说出来。
                RecordFailure(
                    $"喂进来的纹理是 {width}×{height}，而本实例是按 {_options.Width}×{_options.Height} 建的。"
                    + "分辨率一变就必须重建编码器 —— 用旧实例编新尺寸不会得到缩放过的画面，只会得到噪点。");

                return false;
            }

            // 先把已经出来的收干净：异步编码器有延迟，输出往往在**下一次**调用才到。
            DrainOutputs();

            // 🔥🔥 **收完输出之后再泵一次事件**（M7.4 §10，2026-10-19 实测）。
            //
            //    上面那条"令牌＝权限"的注释说的是"没令牌不许要输出"，这里补的是它的另一半：
            //    **令牌是在 ProcessOutput 交付一帧之后才补发的**。而 `DrainOutputs()` 的
            //    `PumpEvents()` 跑在 `ProcessOutput` **之前** ⇒ 那一轮泵完、输出也取走了、
            //    MFT 这才把 NeedInput 挂上，可我们紧接着就去喂 —— 于是必然"没有令牌"。
            //
            //    实测症状（本机 i5-8265U / UHD 620，2160×1440，H.264 硬编）：
            //    `喂 34 / 丢 34 / 出 34`，事件 `NeedInput×34 / HaveOutput×34`，
            //    产出指纹是**严格交替的 0101…**，与喂帧节奏无关（12/s、30/s、60/s、不限速
            //    五种节奏下产出率都是 49–50%）。⇒ "两次调用才出一帧"不是编码器慢，
            //    是**我们每两次调用才成功喂进去一帧**。
            PumpEvents();

            if (forceKeyframe) _keyframeRequested = true;

            var fed = textureHandle != IntPtr.Zero && TryFeed(textureHandle);

            if (fed)
            {
                DrainOutputs();

                // 🔥 刚喂进去那一帧还差几毫秒才出来 —— **在这个调用里把它等回来**。
                //
                //    只问一次就交差的话，这一帧要等到**下一次调用**才被取走，
                //    于是"一次调用一帧"退化成"两次调用一帧"（= 帧率腰斩）。
                //    预算按"一帧周期减去本次调用已经花掉的时间"算：等超过一帧没有意义
                //    （那时这一帧本来就晚了），等到的时间也算在推流循环的节流里。
                WaitForReadyOutput(callStartedAt);
            }

            if (_ready.Count > 0)
            {
                frame = _ready.Dequeue();

                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// <b>把"刚喂进去那一帧的码流"在本调用内等回来</b>（M7.4 §10）。
    ///
    /// <para>
    /// 🔥 <b>为什么必须等</b>：异步 MFT 的输出本来就晚一点点到（硬编 2160×1440 实测
    /// 个位数毫秒）。不等 ⇒ 这一帧要等到**下一次调用**才被取走，而每次调用都喂一帧 ⇒
    /// 调用方看上去就是"每两次调用才出一帧"，帧率直接腰斩。
    /// 本仓 2026-10-19 实测的产出指纹是**严格交替的 <c>0101…</c>**，且在 12/30/36/60/不限速
    /// 五种节奏下都一样 ⇒ 它是个**结构性的 2:1**，不是"编码器慢到只能出这么多"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>预算 = 一个帧周期减去本次调用已经花掉的时间</b>：等得比一帧还久没有意义
    /// （那时这一帧本来就已经晚了，应当照常按丢帧处理并让循环的节流去兜），
    /// 而且这段时间<b>会算进推流循环的"本轮已用"</b>—— 循环的节流判据是
    /// <c>targetPeriod - 本轮耗时</c>，所以不会因为这里等待而把帧率叠成两倍周期。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>它是"锦上添花"，不是"必须成功"</b>：等不到就照旧返回 <c>false</c>，
    /// 由调用方按"这一帧还没出来"处理 —— 行为与只是多花了几毫秒的今天完全一致。
    /// </para>
    /// </summary>
    private void WaitForReadyOutput(long callStartedAt)
    {
        if (_ready.Count > 0) return;

        var periodTicks = _options.SampleDuration * (Stopwatch.Frequency / 10_000_000.0);
        var spentTicks = Stopwatch.GetTimestamp() - callStartedAt;
        var budgetTicks = periodTicks - spentTicks;

        if (budgetTicks <= 0) return;

        var deadline = Stopwatch.GetTimestamp() + (long)budgetTicks;

        while (_ready.Count == 0 && Stopwatch.GetTimestamp() < deadline)
        {
            // 1 ms：够细（软编一帧也要几毫秒），又不至于把核烧起来。
            Thread.Sleep(1);

            DrainOutputs();
        }
    }

    /// <summary>喂一帧进去。返回 <c>false</c> = 这一帧没被接受（忙），<b>按丢帧处理即可</b>。</summary>
    private bool TryFeed(nint texture)
    {
        // ── ① 问"现在收不收" ──
        if (IsAsync)
        {
            // 异步 MFT 的令牌来自事件队列：没有 NeedInput 事件就是"别喂"。
            // ⚠️ 这个判断**不能用 ProcessInput 的返回值代替**：
            //    异步 MFT 在没要输入时回 MF_E_NOTACCEPTING，而那是个"堵在锁里"的调用，
            //    在高帧率下会变成实打实的卡顿。
            if (_needInputTokens <= 0)
            {
                _framesDroppedBusy++;

                return false;
            }
        }
        else
        {
            var getStatus = ComVtable.Bind<MediaFoundationInterop.Delegates.GetInputStatusFn>(
                _transform, MediaFoundationInterop.TransformSlots.GetInputStatus);

            if (!ComVtable.Succeeded(getStatus(_transform, 0, out var flags))
                || (flags & InputStatusAcceptData) == 0)
            {
                _framesDroppedBusy++;

                return false;
            }
        }

        if (!EnsureRing(out var ringFailure))
        {
            RecordFailure($"准备输入纹理环失败：{ringFailure}");

            return false;
        }

        // ── ② 拷到环上下一张（显存内，不过 PCIe）。见类注释"为什么需要一个环"。 ──
        var target = _ring[_ringIndex];

        _ringIndex = (_ringIndex + 1) % TextureRingDepth;

        if (!TryCopyResource(target, texture, out var copyFailure))
        {
            RecordFailure($"把 NV12 拷到输入环上失败：{copyFailure}");

            return false;
        }

        // ── ③ 把纹理包成样本 ──
        var sample = CreateInputSample(target, out var sampleFailure);

        if (sample == IntPtr.Zero)
        {
            RecordFailure($"把纹理包成 MF 样本失败：{sampleFailure}");

            return false;
        }

        try
        {
            // ── ④ 消费掉那个输入令牌，并（按需）强制关键帧 ──
            if (IsAsync) _needInputTokens--;

            ApplyPendingKeyframe();

            var processInput = ComVtable.Bind<MediaFoundationInterop.Delegates.ProcessInputFn>(
                _transform, MediaFoundationInterop.TransformSlots.ProcessInput);

            var hr = processInput(_transform, 0, sample, 0);

            if (!ComVtable.Succeeded(hr))
            {
                if (hr == MediaFoundationInterop.HResults.NotAccepting)
                {
                    // 极少数情况下令牌到了但 MFT 仍不收 —— 当作丢帧，不当故障。
                    _framesDroppedBusy++;

                    return false;
                }

                RecordFailure($"ProcessInput 失败（{ComVtable.Describe(hr)}）。");

                return false;
            }

            _framesFed++;

            return true;
        }
        finally
        {
            ComVtable.ReleasePointer(ref sample);
        }
    }

    /// <summary>
    /// 把 <c>_keyframeRequested</c> 落到编码器上。
    /// <para>
    /// ⚠️ <b>这个属性是不是"设一次管一帧"没有权威说明</b>（有的编码器会自动复位，有的不会）。
    /// 若它不复位，症状是<b>每一帧都成关键帧、码率暴涨</b> —— 那是个很好认的现象，
    /// 由 M5.4-a 的验收用例盯住（见 <c>DxgiHardwareEncodeSmokeTests</c> 里的 GOP 断言）。
    /// </para>
    /// </summary>
    private void ApplyPendingKeyframe()
    {
        if (!_keyframeRequested || _codecApi == IntPtr.Zero) return;

        var value = MediaFoundationInterop.VariantUInt32.From(1);

        var setValue = ComVtable.Bind<MediaFoundationInterop.Delegates.CodecApiSetValueFn>(
            _codecApi, MediaFoundationInterop.CodecApiSlots.SetValue);

        var api = MediaFoundationInterop.CODECAPI_AVEncVideoForceKeyFrame;

        var hr = setValue(_codecApi, ref api, ref value);

        if (ComVtable.Succeeded(hr)) _keyframeRequested = false;
        else RecordFailure($"要求关键帧失败（{ComVtable.Describe(hr)}）—— 下一帧仍会再试。");
    }

    private bool TryCopyResource(IntPtr target, nint source, out string? failure)
    {
        failure = null;

        if (target == source) return true;

        try
        {
            var copy = ComVtable.Bind<D3D11Interop.Delegates.CopyResourceFn>(
                _gpu.Context, D3D11Interop.ContextSlots.CopyResource);

            copy(_gpu.Context, target, source);

            return true;
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}：{ex.Message}";

            return false;
        }
    }

    private IntPtr CreateInputSample(nint texture, out string? failure)
    {
        failure = null;

        var iid = D3D11Interop.IID_ID3D11Texture2D;

        var hr = MediaFoundationInterop.MFCreateDXGISurfaceBuffer(ref iid, texture, 0, 0, out var buffer);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"MFCreateDXGISurfaceBuffer 失败（{ComVtable.Describe(hr)}）—— "
                + "纹理本身或者它的用法（BindFlags / Usage）不合 MF 的要求。";

            return IntPtr.Zero;
        }

        var releaseBuffer = true;

        try
        {
            hr = MediaFoundationInterop.MFCreateSample(out var sample);

            if (!ComVtable.Succeeded(hr))
            {
                failure = $"MFCreateSample 失败（{ComVtable.Describe(hr)}）。";

                return IntPtr.Zero;
            }

            var releaseSample = true;

            try
            {
                var addBuffer = ComVtable.Bind<MediaFoundationInterop.Delegates.AddBufferFn>(
                    sample, MediaFoundationInterop.SampleSlots.AddBuffer);

                hr = addBuffer(sample, buffer);

                if (!ComVtable.Succeeded(hr))
                {
                    failure = $"IMFSample::AddBuffer 失败（{ComVtable.Describe(hr)}）。";

                    return IntPtr.Zero;
                }

                // 样本自己已经 AddRef 了那个 buffer ⇒ 我们这份可以放了。
                ComVtable.ReleasePointer(ref buffer);

                releaseBuffer = false;

                var setTime = ComVtable.Bind<MediaFoundationInterop.Delegates.SetSampleTimeFn>(
                    sample, MediaFoundationInterop.SampleSlots.SetSampleTime);

                hr = setTime(sample, _sampleTime);

                if (!ComVtable.Succeeded(hr))
                {
                    failure = $"IMFSample::SetSampleTime 失败（{ComVtable.Describe(hr)}）。";

                    return IntPtr.Zero;
                }

                _sampleTime += _options.SampleDuration;

                releaseSample = false;

                return sample;
            }
            finally
            {
                if (releaseSample)
                {
                    var local = sample;

                    ComVtable.ReleasePointer(ref local);
                }
            }
        }
        finally
        {
            if (releaseBuffer) ComVtable.ReleasePointer(ref buffer);
        }
    }

    /// <summary>
    /// 把事件队列里的令牌取干净。
    /// <para>
    /// 🔥 <b><c>MF_E_NO_EVENTS_AVAILABLE</c> 必须单独判</b>：它是一个"失败"的 HRESULT，
    /// 而它表达的是"现在没有事件"这种<b>完全正常</b>的状态。写成
    /// <c>if (!Succeeded(hr)) 报错</c> 会让每次轮询都记一条假故障。
    /// </para>
    /// </summary>
    private void PumpEvents()
    {
        if (_events == IntPtr.Zero) return;

        var getEvent = ComVtable.Bind<MediaFoundationInterop.Delegates.GetEventFn>(
            _events, MediaFoundationInterop.EventGeneratorSlots.GetEvent);

        // 🔴🔴🔴 `GetType` 是 **IMFMediaEvent** 的方法，**不是 IMFMediaEventGenerator 的**。
        //   曾经把它按 `_events` 绑（"反正只差一层"），代价是**整个测试宿主进程消失**：
        //   `IMFMediaEventGenerator` 只有 **7 个槽**，取第 33 槽会读到它 vtable **之后 264 字节**
        //   的内存，把那块内存当地址去 call ⇒ `AccessViolationException`。
        //   ⚠️ 而它的报错位置（PumpEvents）看起来像"事件处理写错了"，
        //   与真正的原因（**把接口 A 的槽位用在了接口 B 上**）隔着一层，很容易往错的方向查。
        //   ⇒ 教训与 `ComVtable` 类注释里那条同源：**槽位号是「接口内」的**，
        //     绑定时的 `instance` 必须正是你要调的那个接口。
        //
        //   ⚠️ 也不能像 `getEvent` 那样提到循环外缓存：每个事件是**独立的对象**。
        //   同一台编码器连续抛出的事件在实现上通常共用一张 vtable，所以这里按
        //   vtable 指针（而不是每次无条件重绑）做一次小缓存 —— 既是正确的，
        //   也满足 `ComVtable.Bind` 那条"委托要缓存"的要求。
        MediaFoundationInterop.Delegates.GetEventTypeFn? getType = null;
        var getTypeVtable = IntPtr.Zero;

        for (var guard = 0; guard < 64; guard++)
        {
            var hr = getEvent(_events, MediaFoundationInterop.EventFlagNoWait, out var mediaEvent);

            if (hr == MediaFoundationInterop.HResults.NoEventsAvailable) return;

            if (!ComVtable.Succeeded(hr))
            {
                RecordFailure($"取事件失败（{ComVtable.Describe(hr)}）。");

                return;
            }

            var handle = mediaEvent;

            try
            {
                var eventVtable = Marshal.ReadIntPtr(handle);

                if (getType is null || eventVtable != getTypeVtable)
                {
                    getType = ComVtable.Bind<MediaFoundationInterop.Delegates.GetEventTypeFn>(
                        handle, MediaFoundationInterop.MediaEventSlots.GetEventType);

                    getTypeVtable = eventVtable;
                }

                if (!ComVtable.Succeeded(getType(handle, out var eventType)))
                {
                    // 读不出类型 ⇒ 这一个事件白收。**必须计数**：它是"令牌 0"的两种成因之一，
                    // 而只看令牌数分不出它和"根本没事件"。
                    _eventTypeReadFailures++;

                    continue;
                }

                _eventCounts[eventType] = _eventCounts.GetValueOrDefault(eventType) + 1;

                switch (eventType)
                {
                    case MediaFoundationInterop.EventTransformNeedInput:
                        _needInputTokens++;
                        break;

                    case MediaFoundationInterop.EventTransformHaveOutput:
                        _haveOutputTokens++;
                        break;

                    case MediaFoundationInterop.EventTransformDrainComplete:
                        _drainComplete = true;
                        break;

                    default:
                        // METransformMarker 之类本批用不到；**不报错**（它们不表示故障）。
                        break;
                }
            }
            finally
            {
                ComVtable.ReleasePointer(ref handle);
            }
        }

        // 走到这里说明 64 次都没取空 —— 要么编码器真的泼了 64 个事件，
        // 要么 `GetEvent` 一直在给同一个事件（那是个死循环）。这一格把区别留了下来。
        _eventGuardHits++;
    }

    /// <summary>把已经备好的输出帧取出来放进队列。</summary>
    private void DrainOutputs()
    {
        PumpEvents();

        // 同步 MFT 没有事件：直接问它有没有输出，问不到就退出。
        if (!IsAsync)
        {
            for (var i = 0; i < 4; i++)
            {
                if (!TryProcessOutput(out var produced, out _, out _)) return;

                if (!produced) return;
            }

            return;
        }

        // 🔥🔥🔥 **异步 MFT 只在"手里有 HaveOutput 令牌"时才允许去要输出。**
        //
        //    这条是本项目**最贵的一个实测结论**（2026-10-16，Intel QSV H.264 编码器 MFT，
        //    UHD 620）。早先的写法是"一次调用里连着要 64 次、直到它说没有为止"，
        //    而那必然在**第一批帧还没喂进去之前**先要一次。这个编码器对"没东西却来要"
        //    回的是 `E_UNEXPECTED`（0x8000FFFF）配 `MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE` ——
        //    **不是**文档里那个 `MF_E_TRANSFORM_NEED_MORE_INPUT`。
        //
        //    更要命的是后果：那一次调用之后，**它再也不发 NeedInput 事件了**。
        //    于是现场是"编码器建得好好的、参数全设上了、喂进去 1 帧，然后一辈子不出货"，
        //    日志里只有一个没有任何信息量的 `0x8000FFFF` —— 而真正的原因
        //    （我们提前要了一次输出）在日志里连痕迹都没有。
        //
        //    ⇒ **令牌就是权限，没有令牌不许要。**
        //    ⚠️ 早先那句注释写的是"不按令牌个数定次数，因为一个令牌可能对应不止一帧" ——
        //      后半句是对的（B 帧/重排确实会这样），但它被用来当成了"可以无限次要"的理由，
        //      而正确的结论是：**以令牌数封顶，令牌内允许多取几次。**
        string? failure = null;

        for (var i = 0; i < 64 && _haveOutputTokens > 0; i++)
        {
            _haveOutputTokens--;

            // 一个令牌通常是一帧；重排时可能不止一帧 ⇒ 追到"没有产出"为止，最多再追 3 次。
            for (var extra = 0; extra < 4; extra++)
            {
                if (!TryProcessOutput(out var produced, out _, out failure)) break;

                if (!produced) break;
            }
        }

        if (failure is not null) RecordFailure(failure);
    }

    /// <summary>
    /// 调一次 <c>ProcessOutput</c>。
    /// <para>
    /// 返回 <c>false</c> 表示"这一次调用本身出错了"（<paramref name="failure"/> 里有话）；
    /// 返回 <c>true</c> 而 <paramref name="produced"/> 为 <c>false</c> 表示
    /// <b>"还没编好，再去喂一帧"</b>（<paramref name="needMoreInput"/> 为真）—— 那是正常状态。
    /// </para>
    /// </summary>
    private bool TryProcessOutput(out bool produced, out bool needMoreInput, out string? failure)
    {
        produced = false;
        needMoreInput = false;
        failure = null;

        // 🔥 缓冲不够时**翻倍重试**，不要一次就放弃：
        //    编码器报的预期大小是稳态值，而第一个关键帧（IDR）通常比它大得多 ——
        //    实测就是这样：前几帧全栽在 `MF_E_BUFFERTOOSMALL` 上，
        //    而现象看起来像"编码器编不出来"，其实是我们的缓冲太小。
        //
        // ⚠️ `GetOutputStreamInfo` 挪进循环里：**换过输出类型之后它报的大小会变**，
        //    在循环外只问一次就会一直用那个过期的值。
        uint size = 0;

        // 4 次：够 "换类型 → 重来" 与 "缓冲翻倍" 各发生一次，还留了余量。
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var infoHr = GetOutputStreamInfo(out var info);

            if (!ComVtable.Succeeded(infoHr))
            {
                failure = $"GetOutputStreamInfo 失败（{ComVtable.Describe(infoHr)}）。";

                return false;
            }

            // 编码器自己提供样本时**必须**传 NULL（传我们自己的反而可能被拒）。
            var mftProvides = (info.Flags & MediaFoundationInterop.OutputStreamProvidesSamples) != 0;

            if (size == 0)
            {
                size = info.Size > 0
                    ? info.Size
                    : (uint)Math.Clamp((long)_options.Width * _options.Height * 3 / 4, 1 << 20, 64L << 20);
            }

            var sample = mftProvides ? IntPtr.Zero : CreateOutputSample(size, out failure);

            if (!mftProvides && sample == IntPtr.Zero) return false;

            // ⚠️ 这个结构体在调用之后装的是"我们要负责释放的那份引用"
            //    （谁提供的都一样 —— MF 的约定是 ProcessOutput 交回来的样本归调用方）。
            var buffer = new MediaFoundationInterop.MftOutputDataBuffer
            {
                StreamId = 0,
                Sample = sample,
                Status = 0,
                Events = IntPtr.Zero,
            };

            try
            {
                var processOutput = ComVtable.Bind<MediaFoundationInterop.Delegates.ProcessOutputFn>(
                    _transform, MediaFoundationInterop.TransformSlots.ProcessOutput);

                var hr = processOutput(_transform, 0u, 1, ref buffer, out _);

                if (hr == MediaFoundationInterop.HResults.TransformNeedMoreInput)
                {
                    needMoreInput = true;

                    return true;
                }

                if (hr == MediaFoundationInterop.HResults.TransformStreamChange)
                {
                    // 🔥🔥🔥 **这一条是"QSV 编码器一帧都不出"的真正答案。**
                    //    它在第一次输出时会说"你把输出类型换回我认的那个"，而不是直接出货。
                    //    ⇒ 必须重谈，然后**在同一次调用里重试** —— 光"记下来下次再说"是不够的：
                    //      编码器没有因为我们答应换类型而补发一个 HaveOutput 事件。
                    if (!RenegotiateOutputType(out failure)) return false;

                    // 新类型可能报不同的缓冲大小 ⇒ 下一次循环重新问。
                    size = 0;

                    continue;
                }

                if (hr == MediaFoundationInterop.HResults.BufferTooSmall)
                {
                    if (size >= 256u << 20)
                    {
                        failure = $"缓冲已经放到 {size >> 20} MB 还是不够 —— 这个码流尺寸不合理。";

                        return false;
                    }

                    size *= 2;

                    continue;
                }

                if (!ComVtable.Succeeded(hr))
                {
                    // 🔥 **QSV 编码器在"这次没有输出"时回的是 `E_UNEXPECTED` +
                    //    `MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE`**，而不是文档里那个
                    //    `MF_E_TRANSFORM_NEED_MORE_INPUT`（0xC00D6D72）。本机实测确认。
                    //    按令牌门控之后它不该再出现；真出现就说明还有别处提前要了输出 ——
                    //    所以**计数**，并且按"还没好"处理，不当故障。
                    if (hr == MediaFoundationInterop.HResults.Unexpected
                        && buffer.Status == MediaFoundationInterop.OutputDataBufferNoSample)
                    {
                        _emptyOutputPolls++;

                        needMoreInput = true;

                        return true;
                    }

                    // 🔥 失败时把**当时那几个数**一起写出来。
                    //    `E_UNEXPECTED` 这类"没话说的" HRESULT 如果只有它自己，
                    //    就完全没法区分"缓冲不对 / 样本不该由我们给 / 编码器内部没起来"，
                    //    而这三件事的处置完全相反。报错信息里带上现场是唯一能让它们分开的办法。
                    failure = $"ProcessOutput 失败（{ComVtable.Describe(hr)}）"
                        + $"，现场：缓冲 {size >> 10} KB、输出流标志 0x{info.Flags:X}"
                        + $"、编码器自带样本 {(mftProvides ? "是" : "否")}"
                        + $"、交回来的样本 {(buffer.Sample == IntPtr.Zero ? "空" : "有")}"
                        + $"、状态字 0x{buffer.Status:X}"
                        + $"（{DescribeOutputStatus(buffer.Status)}）"
                        + $"、已喂 {_framesFed} 帧、出令牌 {_haveOutputTokens}";

                    return false;
                }

                if (buffer.Sample == IntPtr.Zero) return true;

                if (!TryReadSample(buffer.Sample, out var frame, out failure)) return false;

                if (frame is null) return true;

                if (_ready.Count >= MaxQueuedOutputs)
                {
                    // 队列满了说明调用方没在取 —— 丢**旧**帧（画面宁可跳一下，也不能越积越迟）。
                    _ready.Dequeue();
                }

                _ready.Enqueue(frame);

                produced = true;
                _framesOut++;

                return true;
            }
            finally
            {
                // ⚠️ 无论那份样本是我们建的还是编码器给的，**这里交回来的引用都要放**。
                //    早先写成"只放自己建的那份"会稳定泄漏 —— 而泄漏在 4K 下每帧就是几 MB，
                //    表现为"跑几分钟之后编码器开始失败"，与代码看起来毫无关系。
                ComVtable.ReleasePointer(ref buffer.Sample);
            }
        }

        failure = $"试满 4 次仍未取到输出（缓冲 {size >> 20} MB，换过 {_streamChanges} 次类型）。";

        return false;
    }

    /// <summary>
    /// 🔥 <b>答应编码器的"换一次输出类型"</b>（<c>MF_E_TRANSFORM_STREAM_CHANGE</c> 之后必须做的事）。
    ///
    /// <para>
    /// <b>为什么必须有这一步</b>：硬件编码器在第一次真正出码流之前，会把它自己<b>实际</b>采用的
    /// 输出类型报出来（带真实的 profile / level，以及 <c>MF_MT_MPEG_SEQUENCE_HEADER</c> ——
    /// SPS/PPS）。我们先前谈的那个是"我们构造的"，它只是<b>不反对</b>，并不等于它就用那个。
    /// 于是它的做法是：<b>先拒绝出帧，要求重新谈一次</b>。
    /// </para>
    /// <para>
    /// ⚠️ <b>不处理它的症状是"一帧都不出"</b>，而且<b>没有错误码</b> ——
    /// 那些帧就是不见了。这一点与 §3.4 里"驱动的能力声明不等于它的行为"是同一种病：
    /// <b>API 说可以的，和它真做的，是两件事。</b>
    /// </para>
    /// <para>
    /// 做法照 MF 的标准流程：<c>GetOutputAvailableType(stream, 0)</c> 拿它现在认的那个，
    /// 原样 <c>SetOutputType</c> 写回去。
    /// </para>
    /// </summary>
    private bool RenegotiateOutputType(out string? failure)
    {
        failure = null;

        var getType = ComVtable.Bind<MediaFoundationInterop.Delegates.GetAvailableTypeFn>(
            _transform, MediaFoundationInterop.TransformSlots.GetOutputAvailableType);

        var hr = getType(_transform, 0, 0, out var type);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"编码器要求换输出类型，但 GetOutputAvailableType 失败（{ComVtable.Describe(hr)}）。";

            return false;
        }

        var handedOver = false;

        try
        {
            // ⚠️ 用下标 **0**（"它现在认的那个"），不是 1（"下一个候选"）。
            //    本机实测两个下标给的是同一个类型，但语义上 0 才是对的 —— 用 1 在下标越界的
            //    编码器上会拿到 MF_E_NO_MORE_TYPES。
            if (!TryApplyType(output: true, type, out var setHr))
            {
                failure = $"编码器要求换输出类型，但把新类型写回去被拒（{ComVtable.Describe(setHr)}）—— "
                    + "它是自己给的类型，本该一定能设上；被拒说明这个 MFT 的状态已经乱了。";

                return false;
            }

            // 引用交给 _outputType（旧的让位）。
            var previous = _outputType;

            _outputType = type;

            handedOver = true;

            ComVtable.ReleasePointer(ref previous);

            _streamChanges++;

            return true;
        }
        finally
        {
            if (!handedOver) ComVtable.ReleasePointer(ref type);
        }
    }

    /// <summary>
    /// 把 <c>MFT_OUTPUT_DATA_BUFFER.dwStatus</c> 翻成人话。
    /// <para>
    /// 🔴 <b>按 <c>==</c> 判，不按位与</b>：那四个是互斥的枚举值不是位标志
    /// （见 <c>MediaFoundationInterop</c> 里那一组常量的说明）。
    /// </para>
    /// </summary>
    private static string DescribeOutputStatus(uint status) => status switch
    {
        0 => "无",
        MediaFoundationInterop.OutputDataBufferIncomplete => "INCOMPLETE",
        MediaFoundationInterop.OutputDataBufferFormatChange => "FORMAT_CHANGE",
        MediaFoundationInterop.OutputDataBufferStreamEnd => "STREAM_END",
        MediaFoundationInterop.OutputDataBufferNoSample => "NO_SAMPLE",
        _ => "未知状态",
    };

    /// <summary>
    /// 建一个装码流的样本。
    /// <para>
    /// ⚠️ <b>每帧新建，不复用</b>：调用方提供的输出样本在被 <c>ProcessOutput</c> 填过之后，
    /// 按 MF 的约定就不该原样再交回去（长度、只读标志、buffer 计数都变了）。
    /// 复用它省下的那点分配，换来的是"某一帧开始永远读出上一帧的尾巴"这种极难定位的故障。
    /// </para>
    /// </summary>
    private IntPtr CreateOutputSample(uint size, out string? failure)
    {
        failure = null;

        var hr = MediaFoundationInterop.MFCreateMemoryBuffer(size, out var buffer);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"MFCreateMemoryBuffer({size}) 失败（{ComVtable.Describe(hr)}）。";

            return IntPtr.Zero;
        }

        var releaseBuffer = true;

        try
        {
            hr = MediaFoundationInterop.MFCreateSample(out var sample);

            if (!ComVtable.Succeeded(hr))
            {
                failure = $"MFCreateSample 失败（{ComVtable.Describe(hr)}）。";

                return IntPtr.Zero;
            }

            var addBuffer = ComVtable.Bind<MediaFoundationInterop.Delegates.AddBufferFn>(
                sample, MediaFoundationInterop.SampleSlots.AddBuffer);

            hr = addBuffer(sample, buffer);

            if (!ComVtable.Succeeded(hr))
            {
                failure = $"IMFSample::AddBuffer 失败（{ComVtable.Describe(hr)}）。";

                var local = sample;

                ComVtable.ReleasePointer(ref local);

                return IntPtr.Zero;
            }

            ComVtable.ReleasePointer(ref buffer);

            releaseBuffer = false;

            return sample;
        }
        finally
        {
            if (releaseBuffer) ComVtable.ReleasePointer(ref buffer);
        }
    }

    /// <summary>从一个输出样本里把码流抠出来。</summary>
    private bool TryReadSample(nint sample, out EncodedFrame? frame, out string? failure)
    {
        frame = null;
        failure = null;

        try
        {
            MediaFoundationInterop.Delegates.GetBufferByIndexFn getBuffer;

            try
            {
                getBuffer = ComVtable.Bind<MediaFoundationInterop.Delegates.GetBufferByIndexFn>(
                    sample, MediaFoundationInterop.SampleSlots.GetBufferByIndex);
            }
            catch (Exception ex)
            {
                failure = $"拿不到 IMFMediaBuffer（{ex.GetType().Name}：{ex.Message}）。";

                return false;
            }

            var hr = getBuffer(sample, 0, out var buffer);

            if (!ComVtable.Succeeded(hr))
            {
                failure = $"GetBufferByIndex 失败（{ComVtable.Describe(hr)}）。";

                return false;
            }

            try
            {
                var lockFn = ComVtable.Bind<MediaFoundationInterop.Delegates.BufferLockFn>(
                    buffer, MediaFoundationInterop.MediaBufferSlots.Lock);

                hr = lockFn(buffer, out var data, out _, out var currentLength);

                if (!ComVtable.Succeeded(hr))
                {
                    failure = $"IMFMediaBuffer::Lock 失败（{ComVtable.Describe(hr)}）。";

                    return false;
                }

                byte[] bytes;

                try
                {
                    if (currentLength == 0)
                    {
                        // 空样本：不是故障，但也交不出帧。（有的编码器会发空的"流结束"样本。）
                        return true;
                    }

                    if (currentLength > int.MaxValue)
                    {
                        failure = $"输出样本声称有 {currentLength} 字节 —— 这个数一看就不对。";

                        return false;
                    }

                    bytes = new byte[currentLength];

                    Marshal.Copy(data, bytes, 0, (int)currentLength);
                }
                finally
                {
                    var unlock = ComVtable.Bind<MediaFoundationInterop.Delegates.BufferUnlockFn>(
                        buffer, MediaFoundationInterop.MediaBufferSlots.Unlock);

                    _ = unlock(buffer);
                }

                frame = new EncodedFrame(
                    _options.Codec,
                    IsKeyframeSample(sample),
                    bytes,
                    _sequence++);

                return true;
            }
            finally
            {
                ComVtable.ReleasePointer(ref buffer);
            }
        }
        catch (Exception ex)
        {
            failure = $"读码流时抛出 {ex.GetType().Name}：{ex.Message}";

            return false;
        }
    }

    /// <summary>
    /// 读 <c>MFSampleExtension_CleanPoint</c>。
    /// <para>
    /// ⚠️ <b>读不到时返回 <c>false</c> 并计数</b>（而不是猜）。见
    /// <see cref="MediaFoundationInterop.MFSampleExtension_CleanPoint"/> 的说明：
    /// 我们既不能假定第一帧就是关键帧，也不能在码流封装格式（Annex-B / AVCC）还没定的情况下
    /// 去扫 NAL。诚实上报"本机不提供该标记"，比编一个看起来合理的 <c>true</c> 强得多。
    /// </para>
    /// </summary>
    private bool IsKeyframeSample(nint sample)
    {
        if (!TryGetUInt32(sample, MediaFoundationInterop.MFSampleExtension_CleanPoint, out var value))
        {
            _keyframeFlagMissing++;

            return false;
        }

        return value != 0;
    }

    /// <summary>
    /// 🔥 <b>把剩下的帧全部挤出来</b>（流结束时用）。
    /// <para>
    /// 不做这一步会<b>少最后几帧</b>：异步编码器手上始终压着 lookahead。
    /// 少掉的是流末尾的画面 —— 而对"自编自解"的验收用例来说，
    /// 少几帧会让"解码器解出来多少帧"和"编进去多少帧"对不上，
    /// 于是那条断言变成噪声。
    /// </para>
    /// </summary>
    internal bool TryFlush(out IReadOnlyList<EncodedFrame> frames, out string? failure)
    {
        lock (_gate)
        {
            failure = null;

            var list = new List<EncodedFrame>();

            if (_disposed || _transform == IntPtr.Zero || _endOfStreamSent)
            {
                while (_ready.Count > 0) list.Add(_ready.Dequeue());

                frames = list;

                return _disposed is false;
            }

            DrainOutputs();

            var processMessage = ComVtable.Bind<MediaFoundationInterop.Delegates.ProcessMessageFn>(
                _transform, MediaFoundationInterop.TransformSlots.ProcessMessage);

            // 顺序固定：先"流到头了"，再"把剩下的吐出来"。
            var hr = processMessage(_transform, MediaFoundationInterop.MessageNotifyEndOfStream, 0);

            if (!ComVtable.Succeeded(hr))
            {
                failure = $"MFT_MESSAGE_NOTIFY_END_OF_STREAM 失败（{ComVtable.Describe(hr)}）。";
            }

            hr = processMessage(_transform, MediaFoundationInterop.MessageCommandDrain, 0);

            if (!ComVtable.Succeeded(hr))
            {
                failure = $"MFT_MESSAGE_COMMAND_DRAIN 失败（{ComVtable.Describe(hr)}）。";
            }

            _endOfStreamSent = true;

            // 轮询等它把尾巴交完。⚠️ 这里**只等有限轮**：等不到就把已有结果交出去，
            // 并把"没等到 DrainComplete"如实记在 DrainCompleted 上 —— 不装作成功。
            for (var i = 0; i < MaxDrainPolls; i++)
            {
                DrainOutputs();

                if (!IsAsync || _drainComplete) break;

                Thread.Sleep(2);
            }

            DrainOutputs();

            while (_ready.Count > 0) list.Add(_ready.Dequeue());

            frames = list;

            return true;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 资源
    // ═══════════════════════════════════════════════════════════

    private bool EnsureRing(out string? failure)
    {
        failure = null;

        if (_ring[0] != IntPtr.Zero) return true;

        var create = ComVtable.Bind<D3D11Interop.Delegates.CreateTexture2DFn>(
            _gpu.Device, D3D11Interop.DeviceSlots.CreateTexture2D);

        // 与转换器输出那张用**同样的**描述（CopyResource 要求两边逐字段一致）。
        var desc = D3D11Interop.Texture2DDesc.Plain(
            (uint)_options.Width, (uint)_options.Height, DxgiInterop.FormatNv12, D3D11Interop.BindRenderTarget);

        for (var i = 0; i < TextureRingDepth; i++)
        {
            var iid = D3D11Interop.IID_ID3D11Texture2D;

            var hr = create(_gpu.Device, ref desc, IntPtr.Zero, out var texture);

            if (!ComVtable.Succeeded(hr))
            {
                failure = $"CreateTexture2D（第 {i} 张 NV12）失败（{ComVtable.Describe(hr)}）。";

                DisposeRing();

                return false;
            }

            _ = iid;

            _ring[i] = texture;
        }

        return true;
    }

    private void DisposeRing()
    {
        for (var i = 0; i < _ring.Length; i++)
        {
            var texture = _ring[i];

            ComVtable.ReleasePointer(ref texture);

            _ring[i] = IntPtr.Zero;
        }
    }

    private int GetOutputStreamInfo(out MediaFoundationInterop.MftOutputStreamInfo info)
    {
        var getInfo = ComVtable.Bind<MediaFoundationInterop.Delegates.GetOutputStreamInfoFn>(
            _transform, MediaFoundationInterop.TransformSlots.GetOutputStreamInfo);

        return getInfo(_transform, 0, out info);
    }

    private bool TryGetAttributes(IntPtr instance, out IntPtr attributes)
    {
        attributes = IntPtr.Zero;

        try
        {
            var fn = ComVtable.Bind<MediaFoundationInterop.Delegates.GetAttributesFn>(
                instance, MediaFoundationInterop.TransformSlots.GetAttributes);

            return ComVtable.Succeeded(fn(instance, out attributes)) && attributes != IntPtr.Zero;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TrySetUInt32(IntPtr attributes, Guid key, uint value)
    {
        try
        {
            var fn = ComVtable.Bind<MediaFoundationInterop.Delegates.AttributeSetUInt32Fn>(
                attributes, MediaFoundationInterop.AttributeSlots.SetUINT32);

            return ComVtable.Succeeded(fn(attributes, ref key, value));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TrySetUInt64(IntPtr attributes, Guid key, ulong value)
    {
        try
        {
            var fn = ComVtable.Bind<MediaFoundationInterop.Delegates.AttributeSetUInt64Fn>(
                attributes, MediaFoundationInterop.AttributeSlots.SetUINT64);

            return ComVtable.Succeeded(fn(attributes, ref key, value));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TrySetGuid(IntPtr attributes, Guid key, Guid value)
    {
        try
        {
            var fn = ComVtable.Bind<MediaFoundationInterop.Delegates.AttributeSetGuidFn>(
                attributes, MediaFoundationInterop.AttributeSlots.SetGUID);

            return ComVtable.Succeeded(fn(attributes, ref key, ref value));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryGetUInt32(IntPtr attributes, Guid key, out uint value)
    {
        value = 0;

        try
        {
            var fn = ComVtable.Bind<MediaFoundationInterop.Delegates.AttributeGetUInt32Fn>(
                attributes, MediaFoundationInterop.AttributeSlots.GetUINT32);

            return ComVtable.Succeeded(fn(attributes, ref key, out value));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void TrySetCodecUInt32(Guid api, uint value, string what, List<string> notes)
    {
        var isSupported = ComVtable.Bind<MediaFoundationInterop.Delegates.CodecApiIsSupportedFn>(
            _codecApi, MediaFoundationInterop.CodecApiSlots.IsSupported);

        var key = api;

        if (!ComVtable.Succeeded(isSupported(_codecApi, ref key)))
        {
            notes.Add($"{what}：编码器不支持（如实记下，不去猜它默认多少）");

            return;
        }

        var variant = MediaFoundationInterop.VariantUInt32.From(value);

        var setValue = ComVtable.Bind<MediaFoundationInterop.Delegates.CodecApiSetValueFn>(
            _codecApi, MediaFoundationInterop.CodecApiSlots.SetValue);

        var hr = setValue(_codecApi, ref key, ref variant);

        notes.Add($"{what}：{(ComVtable.Succeeded(hr) ? $"已设 {value}" : $"设不上（{ComVtable.Describe(hr)}）")}");
    }

    /// <summary>
    /// 设一个 <c>VT_UI4</c> 属性，<b>并且把值读回来核对</b>。
    ///
    /// <para>
    /// 🔥 <b>为什么不能像 <see cref="TrySetCodecUInt32"/> 那样只报"已设"</b>：
    /// <c>ICodecAPI::SetValue</c> 对"我支持这个属性，但我打算忽略你给的值"是<b>合法</b>的
    /// —— 它回 <c>S_OK</c>，而 <c>GetValue</c> 读回来还是旧的。
    /// 本仓已经在 <c>CODECAPI_AVEncCommonMeanBitRate</c> 上实测到过这一模一样的行为
    /// （运行中改码率：回 <c>S_FALSE</c>、回读不变）。
    /// </para>
    /// <para>
    /// ⇒ 于是日志里这三句话必须<b>分得开</b>：
    /// <c>已设 N（读回 N）</c> = 真的生效了；
    /// <c>接受但读回 M</c> = 设进去了、被改成了别的值（驱动有话说）；
    /// <c>不支持</c> = 这台驱动压根没这个属性。
    /// 把前两种合并成一句"已设"，真机上就再也分不清"调优没做"和"调优做了没用"。
    /// </para>
    /// </summary>
    private void TrySetCodecUInt32Verified(Guid api, uint value, string what, List<string> notes)
    {
        var isSupported = ComVtable.Bind<MediaFoundationInterop.Delegates.CodecApiIsSupportedFn>(
            _codecApi, MediaFoundationInterop.CodecApiSlots.IsSupported);

        var key = api;

        if (!ComVtable.Succeeded(isSupported(_codecApi, ref key)))
        {
            notes.Add($"{what}：编码器不支持（如实记下，不去猜它默认多少）");

            return;
        }

        var variant = MediaFoundationInterop.VariantUInt32.From(value);

        var setValue = ComVtable.Bind<MediaFoundationInterop.Delegates.CodecApiSetValueFn>(
            _codecApi, MediaFoundationInterop.CodecApiSlots.SetValue);

        var setHr = setValue(_codecApi, ref key, ref variant);

        if (!ComVtable.Succeeded(setHr))
        {
            notes.Add($"{what}：设不上（{ComVtable.Describe(setHr)}）");

            return;
        }

        var actual = TryGetCodecUInt32(api);

        notes.Add(actual is null
            ? $"{what}：已设 {value}（⚠️ 读不回来，无法确认）"
            : actual.Value == value
                ? $"{what}：已设 {value}（读回 {actual.Value}）"
                : $"{what}：接受但读回 {actual.Value}（要求是 {value} —— 驱动改了它）");
    }

    /// <summary>读一个 <c>VT_UI4</c> 属性的当前值。<c>null</c> = 读不到（或读回来不是 <c>VT_UI4</c>）。</summary>
    private uint? TryGetCodecUInt32(Guid api)
    {
        var key = api;

        var getValue = ComVtable.Bind<MediaFoundationInterop.Delegates.CodecApiGetValueFn>(
            _codecApi, MediaFoundationInterop.CodecApiSlots.GetValue);

        var readback = default(MediaFoundationInterop.VariantUInt32);

        if (!ComVtable.Succeeded(getValue(_codecApi, ref key, ref readback))) return null;

        // ⚠️ 驱动可以把一个"整数属性"回成 VT_I4 / VT_BOOL。类型对不上时**不硬转**
        //    —— 拿错类型硬读出来的是一个看起来合理的错数字，比 null 坏得多。
        return readback.IsUInt32 ? readback.Value : null;
    }

    private void RecordFailure(string message) => _lastFailure = message;    private ulong Size() => MediaFoundationInterop.Pack((uint)_options.Width, (uint)_options.Height);

    private ulong Rate() => MediaFoundationInterop.Pack((uint)Math.Max(_options.Fps, 1), 1);

    private static ulong Ratio() => MediaFoundationInterop.Pack(1, 1);

    // ═══════════════════════════════════════════════════════════
    // 报告 / 释放
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>输出类型里那段"解码器初始化描述"</b>（<c>MF_MT_MPEG_SEQUENCE_HEADER</c>，SPS/PPS）。
    ///
    /// <para>
    /// <b>它没有并不代表码流不完整</b>：MF 的 H.264 编码器默认按 Annex-B 输出，
    /// SPS/PPS 直接混在码流里，解码器自己能解析出来。
    /// 但如果这里有值，就说明它走的是"描述与码流分离"的封装 ——
    /// 那时喂给解码器（以及将来前端 WebCodecs 的 <c>description</c>）<b>必须</b>带上它。
    /// </para>
    /// <para>⚠️ 本方法只读、不改状态；读不到就返回 <c>null</c>，不编造空数组。</para>
    /// </summary>
    internal byte[]? TryReadSequenceHeader()
    {
        lock (_gate)
        {
            if (_outputType == IntPtr.Zero) return null;

            var key = MediaFoundationInterop.MF_MT_MPEG_SEQUENCE_HEADER;

            try
            {
                var sizeFn = ComVtable.Bind<MediaFoundationInterop.Delegates.AttributeGetBlobSizeFn>(
                    _outputType, MediaFoundationInterop.AttributeSlots.GetBlobSize);

                if (!ComVtable.Succeeded(sizeFn(_outputType, ref key, out var size)) || size == 0) return null;

                var buffer = Marshal.AllocHGlobal((int)size);

                try
                {
                    var blobFn = ComVtable.Bind<MediaFoundationInterop.Delegates.AttributeGetBlobFn>(
                        _outputType, MediaFoundationInterop.AttributeSlots.GetBlob);

                    if (!ComVtable.Succeeded(blobFn(_outputType, ref key, buffer, size, out var written))) return null;

                    var bytes = new byte[written];

                    Marshal.Copy(buffer, bytes, 0, (int)written);

                    return bytes;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>一份能直接贴进验收报告的实例说明。</summary>
    internal string Describe()
    {
        lock (_gate)
        {
            var rateControl = _options.RateControl switch
            {
                EncoderRateControl.ConstantBitrate => "CBR",
                EncoderRateControl.UnconstrainedVbr => "UnconstrainedVBR",
                _ => $"固定质量（{_options.Quality}）",
            };

            return $"编码器「{MftName}」：{_options.Codec} {_options.Width}×{_options.Height}@{_options.Fps}"
                + $"，码率上限 {(double)_options.BitrateBps / 1_000_000:F1} Mbps，码控 {rateControl}，GOP {_options.GopSize}"
                + $"，{(IsAsync ? "异步 MFT（事件驱动）" : "同步 MFT（轮询）")}"
                + $"，D3D 设备管理器 {(DeviceManagerAttached ? "已挂上" : "没挂上")}"
                + $"（设备句柄自检 {(_gpu.DeviceHandleVerified ? "通过：零拷贝那条链子是真的" : "没通过：管理器手上可能没设备")}）"
                + $"，设备 {(_gpu.VideoSupportRequested ? "带 VIDEO_SUPPORT" : "退回到无 VIDEO_SUPPORT")}"
                + $"/多线程保护 {(_gpu.MultithreadProtected ? "已开" : "没开")}"
                + $"，{CodecApiNote}"
                + $"；喂 {_framesFed} / 丢 {_framesDroppedBusy} / 出 {_framesOut}，队列 {_ready.Count}"
                + $"，输入令牌 {_needInputTokens} / 输出令牌 {_haveOutputTokens}"
                + $"，换过 {_streamChanges} 次输出类型"
                + (_emptyOutputPolls > 0 ? $"（另有 {_emptyOutputPolls} 次空取）" : string.Empty)
                + $"，事件 {DescribeEvents()}"
                + $"，关键帧标记 {(KeyframeFlagAvailable ? "可用" : $"不可用（{_keyframeFlagMissing} 个样本没带 CleanPoint）")}"
                + $"；最近一次失败：{_lastFailure}";
        }
    }

    /// <summary>
    /// 事件直方图（见 <see cref="_eventCounts"/> 的说明）。
    /// <para>
    /// 已知的三个类型写成名字，其余写数字 —— <b>不猜名字</b>：MF 的事件类型是开放集合，
    /// 认不出来的那些按数字报出来，比编一个"大概是什么"有用得多。
    /// </para>
    /// </summary>
    private string DescribeEvents()
    {
        var parts = new List<string>();

        foreach (var pair in _eventCounts.OrderBy(p => p.Key))
        {
            var name = pair.Key switch
            {
                MediaFoundationInterop.EventTransformNeedInput => "NeedInput",
                MediaFoundationInterop.EventTransformHaveOutput => "HaveOutput",
                MediaFoundationInterop.EventTransformDrainComplete => "DrainComplete",
                _ => $"未知{pair.Key}",
            };

            parts.Add($"{name}×{pair.Value}");
        }

        if (_eventTypeReadFailures > 0) parts.Add($"读类型失败×{_eventTypeReadFailures}");

        if (_eventGuardHits > 0) parts.Add($"取满一轮×{_eventGuardHits}");

        return parts.Count == 0 ? "一个都没收到" : string.Join(" / ", parts);
    }

    private WindowsMediaFoundationVideoEncoder? DisposeAndReturn()
    {
        Dispose();

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;

            if (_streaming && _transform != IntPtr.Zero)
            {
                try
                {
                    var processMessage = ComVtable.Bind<MediaFoundationInterop.Delegates.ProcessMessageFn>(
                        _transform, MediaFoundationInterop.TransformSlots.ProcessMessage);

                    _ = processMessage(_transform, MediaFoundationInterop.MessageNotifyEndStreaming, 0);
                }
                catch (Exception)
                {
                    // 释放路径不抛异常：一个"结束流"失败不该把后面的清理全跳过。
                }
            }

            _streaming = false;

            DisposeRing();

            _ready.Clear();

            ComVtable.ReleasePointer(ref _codecApi);
            ComVtable.ReleasePointer(ref _outputType);
            ComVtable.ReleasePointer(ref _events);
            ComVtable.ReleasePointer(ref _transform);

            // ⚠️ **刻意不调 MFShutdown**：MF 是进程级的，别的组件（能力探测、将来的解码器）
            //    可能还在用。在析构里关掉它，症状是"另一个模块突然开始报 MF 未初始化"。
        }
    }
}
