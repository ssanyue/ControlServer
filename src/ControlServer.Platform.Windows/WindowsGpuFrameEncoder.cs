using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// GPU 管线<b>停在哪一级</b>（方案 §3.5 那条三级回退链的落点）。
///
/// <para>
/// 🔥 <b>为什么不能只用一句话就说清失败</b>：调用方要据此做<b>不同的决定</b> ——
/// "这台机器没硬编"该安静地回落 MJPEG（那是预期内的降级），
/// "转换器建不起来"是一条要进告警的缺陷，而"这块屏正被别的抓屏程序占着"
/// 是一条<b>用户自己能解决</b>的指引。三种都写成"GPU 路径不可用"，
/// 现场就只能猜（见方案 §3.5 那条"降级了与本来就这样必须分得开"）。
/// </para>
/// </summary>
public enum GpuPipelineStage
{
    /// <summary>没停在任何一级（成功）。</summary>
    None = 0,

    /// <summary>平台不对（非 Windows）。</summary>
    Platform,

    /// <summary>找不到要抓的屏（会话没桌面 / 请求的屏不存在）。<b>环境问题</b>。</summary>
    Screen,

    /// <summary>屏在、但抓不到画面（被别的抓屏程序占着 / 输出组合不支持 DDA）。<b>环境问题</b>。</summary>
    Capture,

    /// <summary>BGRA→NV12 转换器建不起来（多半是驱动太旧或跑在基础渲染器上）。</summary>
    ColorConvert,

    /// <summary>硬件编码器建不起来（本机没有 / 驱动不给用）。</summary>
    Encode,

    /// <summary>建的过程中抛了异常（门面兜住了，但这是"不该发生"的一类）。</summary>
    Unexpected,
}

/// <summary>GPU 管线起不来的<b>结论</b>：停在哪一级 + 给人看的一句话。</summary>
/// <param name="Stage">停在哪一级。</param>
/// <param name="Reason">
/// 给人看的原因。<b>刻意写得能原样进日志与页面</b> —— 它已经带上了"哪一个环节、
/// 为什么、现场能做什么"这三件事，调用方不该把它改写成"编码失败"。
/// </param>
public sealed record GpuPipelineVerdict(GpuPipelineStage Stage, string Reason)
{
    /// <summary>
    /// 是不是**环境**问题（而不是"我们没有/我们坏了"）。
    /// <para>
    /// 🔥 它决定验收时"跳过还是算 FAIL"：被控机上正跑着别的抓屏工具属于环境，
    /// 而"枚举到了硬编却建不起来"是<b>我们的缺陷</b>。两者的处置完全相反。
    /// </para>
    /// </summary>
    public bool IsEnvironmentProblem => Stage is GpuPipelineStage.Screen or GpuPipelineStage.Capture;

    /// <inheritdoc />
    public override string ToString() => $"[{Stage}] {Reason}";
}

/// <summary>
/// <b>GPU 零拷贝管线</b>的公开门面（M5.4-b）——「DXGI 抓屏 → GPU 转 NV12 → MF 硬编」整条包成一个对象。
///
/// <para>
/// 🔥 <b>为什么需要这个门面，而不是让托盘直接 new 那几个类</b>：
/// <see cref="WindowsMediaFoundationVideoEncoder"/> / <see cref="D3D11Nv12Converter"/> /
/// <see cref="D3D11GpuContext"/> <b>全是 <c>internal</c></b>，托盘够不着。
/// 而把它们改成 <c>public</c> 是错的 —— 那会把「怎么建视频处理器、槽位号是多少、
/// 事件令牌怎么门控」这些实现细节变成对外契约，从此每次内部调整都是破坏性变更。
/// ⇒ 门面只暴露<b>一条链子的两端</b>：喂一张 GPU 画面，拿一包码流。
/// </para>
///
/// <para>
/// 🔥 <b>它连抓屏器一起持有（<see cref="Capturer"/>），这是零拷贝的结构性要求</b>：
/// 三者必须共用<b>同一个 D3D11 设备</b>，而那个设备是抓屏器在绑屏时建的。
/// 详见 <see cref="ISurfaceFrameEncoder"/> 的类注释。
/// </para>
///
/// <para>
/// 🔥 <b>回退判决在哪</b>：本类的 <see cref="TryCreate"/> 要么给一个能用的实例，
/// 要么给一句<b>说清是哪一级不行</b>的原因（面板/日志直接转述，不许改写成"编码失败"）。
/// 方案 §3.5 的三级回退链是：
/// </para>
/// <list type="number">
///   <item>DXGI 抓屏 + NV12 转换 + MF 硬编（<b>本类</b>）——建不起来就往下走；</item>
///   <item>GDI 抓屏 + 软编 MJPEG（<c>GdiJpegFrameEncoder</c>，M5.1 那条路）；</item>
///   <item>连它都起不来 ⇒ <b>明确拒绝并说原因</b>，<b>不许安静地回落到一个更差的档</b>。</item>
/// </list>
/// ⚠️ <b>"降级了"与"本来就这样"必须分得开</b>：否则现场会拿"这台机器没有硬编"
/// 去解释一个其实是接线错误的故障（方案 §3.5 那条纪律）。
///
/// <para>
/// ⚠️ <b>线程亲和（两条，都很硬）</b>：
/// DXGI Desktop Duplication 的 <c>AcquireNextFrame</c> 与异步 MFT 的事件队列
/// 都要求<b>始终在同一个线程上</b>。⇒ 本类<b>不是</b>线程安全的，且
/// <see cref="TryEncodeSurface"/> 应当只从推流主循环那<b>一个</b>线程调用。
/// 内部那把锁只把"并发的错误用法"变成"串行"，<b>不解决线程跳变</b>
/// —— 拿它去 <c>Task.Run</c> 里并发跑，症状是随机的 <c>ACCESS_LOST</c>。
/// </para>
/// </summary>
/// <remarks>
/// 🔥 <b>它必须同时声明 <see cref="IRegionSurfaceEncoder"/></b>（M7.17 修的一个真 BUG）：
/// 三个成员（<c>Regions</c> / <c>SetViewport</c> / <c>TryEncodeSurfaceRegions</c>）本来全都实现了，
/// 但类声明上只写了 <see cref="ISurfaceFrameEncoder"/>，于是推流宿主的那个判据
/// （<c>encoder is not IRegionSurfaceEncoder</c>）恒为真 ⇒
/// <list type="bullet">
///   <item>M5.5 的<b>视口（ROI）整块失效</b>：<c>SetViewport</c> 永远返回"不支持只编视口"，
///         一次都没生效过（这恰好让"在 4K 屏上量 1080p"这条验收路径不存在）；</item>
///   <item>M5.5 的<b>分块编码</b>也是死码：三屏拼接永远落到 MJPEG 缩略图，
///         而 <c>_regions</c> / <c>ComputeRegions</c> / <c>TryEncodeSurfaceRegions</c>
///         那一整套只有测试在跑；</item>
///   <item>帧头坐标系按"不支持区域编码"算（<c>RemoteFrameExtent.Resolve</c>），
///         与接口契约（<b>1:1、不缩放</b>）不符。</item>
/// </list>
/// <para>
/// ⚠️ <b>这是"实现写好了但没接线"那一类</b>：没有报错、没有告警、日志也只是沉默 ——
/// 而症状只有"某个功能看起来就是不生效"。所以下面这行接口<b>不许</b>再删回去；
/// 要做"这块屏编不了"的判断，请用 <see cref="SetViewport"/> 的返回值，不要靠类型。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsGpuFrameEncoder : IRegionSurfaceEncoder
{
    /// <summary>
    /// <b>编译期契约钉子</b>：把类声明上的 <see cref="IRegionSurfaceEncoder"/> 删掉，
    /// 这一行的隐式转换就编不过（CS0266），于是"实现写好了但没接线"不可能再悄悄回来一次。
    /// <para>
    /// 为什么不在用例里钉：能引用 <c>ControlServer.Platform.Windows</c> 的测试工程
    /// （<c>net8.0-windows</c>）目前不存在，而这条事实<b>在编译期就有答案</b>，
    /// 没必要留到运行期靠一条没人写的反射断言。
    /// </para>
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Style", "IDE0051:Remove unused private members",
        Justification = "编译期接口契约，刻意不调用")]
#pragma warning disable IDE0051
    private static IRegionSurfaceEncoder ContractPin(WindowsGpuFrameEncoder encoder) => encoder;
#pragma warning restore IDE0051

    /// <summary>
    /// H.264 硬编的<b>宽度/高度硬上限</b>（所有硬件编码器都是这个数，见
    /// <see cref="VideoCodec"/> 的注释）。
    /// <para>
    /// 🔥 在门面上先判一次是有价值的：不判的话，超限会以
    /// "本机枚举到 N 个 H.264 硬件编码器，但每一个都建不起来"的形式报出来
    /// —— 那句话把人往"驱动/权限"方向带，而真因只是<b>尺寸超过了格式本身的上限</b>。
    /// </para>
    /// </summary>
    public const int H264MaxDimension = 4096;

    /// <summary>HEVC 硬编的上限（8K 那一档）。</summary>
    public const int HevcMaxDimension = 8192;

    /// <summary>全新 duplication 的头几帧<b>没有桌面内容</b>，最多探多少次（见 M5.4-a §3.3 第 4 条）。</summary>
    private const int ProbeAttempts = 12;

    /// <summary>
    /// "去问编码器要参数集"的最多尝试次数。
    /// <para>
    /// ⚠️ 不是重试同一个动作，而是"每次出关键帧时顺带看一眼"：参数集挂在编码器
    /// <b>实际采用</b>的输出类型上，那个类型要等第一次出数据才谈定（异步 MFT 的
    /// <c>MF_E_TRANSFORM_STREAM_CHANGE</c>）。给上限是为了<b>不去无限地问一个
    /// 永远没有的答案</b>（H.264 走 Annex-B 内联那条路时它本来就是 <c>null</c>）。
    /// </para>
    /// </summary>
    private const int ParameterSetAttempts = 30;

    private readonly object _gate = new();

    private readonly WindowsDxgiScreenCapturer _capturer;
    private readonly VideoCodec _codec;
    private readonly int _fps;
    private readonly int? _bitrateBps;
    private readonly int _gopSize;

    /// <summary>
    /// 一帧切几片（M5.5 手段 2：SFE）。<c>0</c> = 不切。
    /// <para>
    /// 🔥 <b>它是"尽力而为"的旋钮，不是承诺</b>：MF 侧只能通过 slice control 表达，
    /// 而多数驱动照样在一个引擎上串行编完。真的生效没有，只能看实测帧率
    /// —— 编码器会把设/读回的结果写进 <c>CodecApiNote</c>。
    /// </para>
    /// </summary>
    public int SliceCount { get; init; }

    /// <summary>
    /// 是否关掉编码器侧预处理（M5.5 手段 10）。默认开 = <b>关掉预处理</b>。
    /// </summary>
    public bool DisablePreprocessing { get; init; } = true;

    // ── 当前这条链子（换屏 / 换分辨率 / 换设备时整体重建）──

    private D3D11GpuContext? _gpu;
    private D3D11Nv12Converter? _converter;
    private WindowsMediaFoundationVideoEncoder? _encoder;

    // ── 编码区域：ROI（M5.5 第 5 项）与分块编码（M5.5 第 4 项）──
    //
    // 🔥 这两件事是**同一件事的两个用法**：ROI 是"只要一块"，分块是"切成好几块"。
    //    两者都归结成 `_regions` 这个清单，于是转换器、指针叠加、帧头、
    //    前端拼接都只有一条代码路径 —— 而"两条路径各写一遍"正是这类功能
    //    最容易出"8K 滚一下之后画面错位"的地方（错位不报错）。

    /// <summary>
    /// 源纹理（整幅抓屏画面）的尺寸。<b>与各区域的输出尺寸不是一回事</b>：
    /// 分块/ROI 时输入是整屏、输出只是其中一块。
    /// </summary>
    private int _sourceWidth;
    private int _sourceHeight;

    /// <summary>只编这一个矩形（<c>null</c> = 整幅）。</summary>
    private EncodeRegion? _viewport;

    /// <summary>单块最大宽度（0 = 不切）。按编码格式上限设。</summary>
    private int _maxTileWidth;

    /// <summary>当前生效的区域清单（下标 0 就是 <c>_converter/_encoder</c> 那一对）。</summary>
    private readonly List<EncodeRegion> _regions = new();

    /// <summary>第 2 块及以后的编码链（多块才有）。</summary>
    private readonly List<TileLane> _extraLanes = new();

    /// <summary>视口 / 分块上限变过 ⇒ 下一次编帧时重建。</summary>
    private bool _regionDirty;

    /// <summary>
    /// 本实例<b>实际抓的那块屏</b>（<c>Prepare</c> 挑完之后定下来）。
    ///
    /// <para>
    /// 🔥 <b>它必须能被读出来，否则会出现"两个后端各挑一次屏"</b>：
    /// 宿主（<c>RemoteStreamHost</c>）自己也要挑一次屏来填 <c>ScreenId</c> /
    /// <c>ScreenLeft</c>（输入回传的坐标换算要用），而那条链路<b>刻意</b>是"多屏时优先虚拟桌面"；
    /// 本门面那条是"优先主屏"（DDA 一次只能抓一个 output，虚拟桌面它做不了）。
    /// 两条规则**故意不同**，于是"各挑一次"必然在多屏机上挑到不同的屏 ——
    /// 症状正是本项目最贵的一类：<b>看着 A 的屏幕、实际操作 B</b>，而日志上完全正常。
    /// ⇒ 宿主的做法是：<b>拿本属性的结果去填自己的字段</b>（唯一出处）。
    /// </para>
    /// </summary>
    private string _screenId = string.Empty;

    private string _screenName = string.Empty;

    /// <summary>已经成功编出过一帧没有（判"建起来但从不产帧"用的）。</summary>
    private bool _producedAnyFrame;

    private byte[]? _parameterSets;
    private int _parameterSetAttempts;

    // ── 指针合成（M5.4-b 第 5 步）──
    //
    // 🔥 这两张纹理都**很小**（指针矩形，典型 32×32，最多 256×256），
    //    与整屏那 33 MB 不是一个量级 —— 所以"每帧读回一次"这件事在这里是可以承受的，
    //    而且它**只在指针真的动了的时候才发生**（不动就不读）。

    /// <summary>承装合成结果、给 VideoProcessor 当输入的小 BGRA 纹理（随矩形尺寸重建）。</summary>
    private IntPtr _pointerTexture;

    private int _pointerTextureWidth;
    private int _pointerTextureHeight;

    /// <summary>读回"桌面副本上指针那一小块"用的 STAGING 纹理（随尺寸重建）。</summary>
    private IntPtr _pointerStaging;

    private int _pointerStagingWidth;
    private int _pointerStagingHeight;

    private long _cursorComposited;
    private long _cursorFailed;

    /// <summary>最近一次"为什么没画上指针"（画上了 / 指针不可见时为 <c>null</c>）。</summary>
    private string? _cursorNote;

    /// <summary>
    /// 最近一次合成<b>用的输入</b>长什么样（指针位置 / 形状 / 算出的小矩形）。
    ///
    /// <para>
    /// 🔥 <b>它把"合成没成"这条链切成两段</b>：计数与 <c>_cursorNote</c> 只说明
    /// "三步调用都成功了"（回的都是 <c>true</c>），<b>不说明画上去的东西对不对</b>。
    /// 而"画上了却看不见"至少有两个完全不同的成因 ——
    /// <b>①输入是坏的</b>（位置一直不动 / 形状没拿到 / 矩形算到画面外）；
    /// <b>②输入是对的、那一刀写错了地方</b>。没有这一行，两者在日志上一模一样。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 记录的是<b>合成那一刻</b>的快照，不是"查询诊断时"的快照 ——
    /// 两者差几毫秒，而指针一秒能走几百像素。
    /// </para>
    /// </summary>
    private string? _cursorLastInput;

    private bool _grayscale;
    private bool _disposed;

    private long _rebuilds;
    private long _framesEncoded;
    private long _framesDropped;

    // ── "喂进去的帧" ↔ "取出来的码流" 的配对（M5.4-b 第 6 步）──
    //
    // 🔴🔴 **没有这个队列，时间戳会整体错位一帧。**
    //
    //   异步 MFT 的输出**晚一次调用才到**（M5.4-b 实测：喂 17 帧出 16 帧）。
    //   于是 `TryEncodeSurface(surface_N)` 拿到的是**第 N−1 帧**的字节。
    //   早先的实现把这份字节贴上了 `surface_N` 的 `CapturedUnixMs`，
    //   又把"本次没出帧"记成一次丢帧 —— 两个后果都不报错：
    //     ① 码流上带的抓屏时刻**比实际早一帧** ⇒ AV 同步（M5.3）差一帧（30 fps 下 33 ms，
    //        唇音上看得出来）；`IsKeyframe` 也会张冠李戴（前端拿它决定"等不等关键帧"）；
    //     ② 页面显示"掉了很多帧"，而实际**一帧没丢**（输出只是晚了）。
    //
    //   ⇒ 每喂进去一帧就把它自己的元数据入队，每取出一份码流就出队一份 —— 一一对应。
    //     判据是"编码器有没有把这一帧收下"（`FramesFed` 有没有动），**不读失败文案**。
    private readonly Queue<PendingFrame> _pendingFrames = new();

    /// <summary>配对队列的深度上限（超过就说明编码器吞了帧，不许无限涨）。</summary>
    private const int MaxPendingFrames = 4;

    /// <summary>队列溢出丢过几次元数据（诊断用：非零 = 编码器收了帧却没吐出来）。</summary>
    private long _pendingOverflows;

    /// <summary>取出码流时队列却是空的一共几次（诊断用：非零 = 配对错位）。</summary>
    private long _pairingMisses;

    /// <summary>
    /// 一帧的<b>"它自己的"</b>元数据（喂进去那一刻记下，取出来那一刻用）。
    /// </summary>
    private readonly record struct PendingFrame(
        int Width,
        int Height,
        long CapturedUnixMs,
        long CapturedMonoTicks);

    /// <summary>
    /// "本进程的定时器粒度已提到 1 ms"的凭据（<c>null</c> = 没提上去）。
    /// <para>⚠️ 它必须跟着实例生命周期成对出现，见构造函数的说明。</para>
    /// </summary>
    private IDisposable? _timerPrecision;

    private string? _lastFailure;

    /// <summary>
    /// <b>第 2 块及以后的编码链</b>（分块编码才会用到）。
    ///
    /// <para>
    /// 🔥 <b>为什么每一块都要自己的一套转换器 + 编码器</b>：
    /// H.264/HEVC 的 P 帧只参考<b>同一路码流</b>里前面的帧。把两块塞进同一个编码器
    /// 不是"隔开就行"——它们的参考链会互相污染，解出来是<b>两块画面轮流花屏</b>。
    /// 所以"一块一路流"是硬约束，不是实现偷懒。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 每块都带自己的"喂进↔取出"配对队列（<see cref="Pending"/>）：
    /// 异步 MFT 的延迟是<b>逐编码器</b>的，共用一支队列会让每块的时戳
    /// 都来自别的块 —— 症状是"有一块画面比别块慢半拍"，而日志全绿。
    /// </para>
    /// </summary>
    private sealed class TileLane
    {
        internal required EncodeRegion Region { get; init; }
        internal required D3D11Nv12Converter Converter { get; init; }
        internal required WindowsMediaFoundationVideoEncoder Encoder { get; init; }

        internal readonly Queue<PendingFrame> Pending = new();

        /// <summary>这一块编出过帧没有（诊断：某一块"建起来了但从不产帧"时能指到具体是哪块）。</summary>
        internal bool ProducedAnyFrame;

        internal string? LastFailure;
        internal long PendingOverflows;

        internal string Describe() => $"第 {Region} 块";
    }


    /// <summary>最近一次重建（含原因），进日志与报告。</summary>
    private string? _rebuildNote;

    private WindowsGpuFrameEncoder(
        WindowsDxgiScreenCapturer capturer,
        VideoCodec codec,
        int fps,
        int? bitrateBps,
        int gopSize)
    {
        _capturer = capturer;
        _codec = codec;
        _fps = Math.Clamp(fps, 1, 240);
        _bitrateBps = bitrateBps;
        _gopSize = gopSize > 0 ? gopSize : 60;

        // 🔥 **本实例活着 = 这条流在跑**，所以定时器粒度在这里提、在 Dispose 里还。
        //
        //    三处都靠 1 ms：① 本类等编码输出的 1 ms 轮询；② 推流循环按目标周期做的
        //    `Task.Delay(剩余)`；③ 两者都在**同一个进程**里（托盘），提一次就都受益。
        //    ⚠️ 放在这里而不是"每次编码前"，是因为它必须**成对**：多提一次不还，
        //    这台机器就永久停在 1 ms 粒度上（M7.4 §11）。
        _timerPrecision = WindowsTimerPrecision.Raise();
    }

    /// <summary>
    /// 🔥 <b>建一条真的能跑的 GPU 管线</b>（抓屏 → 转 NV12 → 硬编）。
    ///
    /// <para>
    /// 返回 <c>null</c> 时 <paramref name="failure"/> 一定说清了<b>停在哪一级、为什么</b>
    /// —— 那句话是给现场看的，可以原样进日志和页面。
    /// </para>
    /// <para>
    /// ⚠️ <b>它一定会抓至少一帧</b>（用来确定真实尺寸并验证 duplication 真能出画面）。
    /// 这不是浪费：只有拿到一张真实纹理才知道该建多大的转换器，
    /// 而且"建得起来"与"抓得到"本来就是两件事（后者可能被别的抓屏工具占着）。
    /// </para>
    /// </summary>
    /// <param name="sessionId">本进程所在会话号（托盘 = 用户会话）。</param>
    /// <param name="codec">编码格式。<b>超 4K 只能给 HEVC</b>（见 <see cref="TryValidateGeometry"/>）。</param>
    /// <param name="fps">目标帧率（也进媒体类型的帧率字段）。</param>
    /// <param name="failure">
    /// 失败<b>结论</b>（停在哪一级 + 为什么）；成功时为 <c>null</c>。
    /// 调用方据此决定"安静回落 MJPEG"还是"报错"，见 <see cref="GpuPipelineVerdict.IsEnvironmentProblem"/>。
    /// </param>
    /// <param name="screenId">
    /// 要抓的屏。<c>null</c> = 主屏（<see cref="WindowsDxgiScreenCapturer"/> 自己去挑）。
    /// </param>
    /// <param name="grayscale">初始是否灰度（对第一帧没有影响，只是设定）。</param>
    /// <param name="bitrateBps">目标码率；<c>null</c> = 按分辨率与帧率估。</param>
    /// <param name="gopSize">最大关键帧间隔。</param>
    /// <param name="sliceCount">
    /// 一帧切成几片（M5.5 手段 2：SFE 的 MF 侧落点）。<c>0</c> = 不切。
    /// <para>
    /// ⚠️ 它与 <c>gopSize</c> 一样<b>只在建管线时生效</b>：切片方式挂在编码器的媒体类型/属性上，
    /// 运行中改不了（与码率同一个理由，见 <c>MediaFoundationEncoderOptions</c>）。
    /// </para>
    /// </param>
    public static WindowsGpuFrameEncoder? TryCreate(
        int sessionId,
        VideoCodec codec,
        int fps,
        out GpuPipelineVerdict? failure,
        string? screenId = null,
        bool grayscale = false,
        int? bitrateBps = null,
        int gopSize = 60,
        int sliceCount = 0,
        bool disablePreprocessing = true,
        int maxTileWidth = 0)
    {
        failure = null;

        if (!OperatingSystem.IsWindows())
        {
            failure = new GpuPipelineVerdict(
                GpuPipelineStage.Platform, "DXGI 抓屏 ＋ MF 硬编只在 Windows 上可用。");

            return null;
        }

        var capturer = new WindowsDxgiScreenCapturer(sessionId);

        var encoder = new WindowsGpuFrameEncoder(capturer, codec, fps, bitrateBps, gopSize)
        {
            SliceCount = sliceCount,
            DisablePreprocessing = disablePreprocessing,
        };

        // 🔥 **分块上限必须在 Prepare 之前设好**：Prepare 会真抓一帧并建第一条管线，
        //    之后再设就等于"先按不切建一遍、再拆掉重建"—— 开机白等 0.3 秒，
        //    而且日志里会多一条莫名其妙的"编码区域从 1 块变成 2 块"。
        encoder._maxTileWidth = Math.Max(0, maxTileWidth);

        try
        {
            if (!encoder.Prepare(screenId, out failure))
            {
                encoder.Dispose();

                return null;
            }

            encoder._grayscale = grayscale;

            return encoder;
        }
        catch (Exception ex)
        {
            // 门面绝不把异常漏给调用方：托盘是 WinExe，未捕获异常会让进程悄无声息地消失。
            failure = new GpuPipelineVerdict(
                GpuPipelineStage.Unexpected, $"建 GPU 管线时抛出 {ex.GetType().Name}：{ex.Message}");

            encoder.Dispose();

            return null;
        }
    }

    /// <summary>本实例实际使用的编码格式（<c>h264</c> / <c>hevc</c>）。</summary>
    public string Codec => _codec switch
    {
        VideoCodec.Hevc => StreamCodecs.Hevc,
        _ => StreamCodecs.H264,
    };

    /// <inheritdoc />
    public IScreenCapturer Capturer => _capturer;

    /// <inheritdoc />
    public string? LastFailure
    {
        get
        {
            lock (_gate) return _lastFailure;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠️ <b>这里与 MJPEG 那条路<b>不一样</b></b>：MJPEG 逐帧独立，下一帧就变；
    /// 而本路径要重开 GOP，否则观众看到的是"慢慢从彩色褪成灰度"
    /// （历史帧全是彩色的）—— 那看起来就像坏了。
    /// ⇒ **改写这个属性会自动让下一个帧成为关键帧。**
    /// </remarks>
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

                // 内容发生了不连续 ⇒ 必须换一个关键帧。
                // ⚠️ 像素层的活是转换器干的（它逐帧吃这个标志，不需要重建）；
                //    所以这里**只**做"重开 GOP"这一件事。
                _encoder?.RequestKeyframe();

                // 🔥 分块时**每一块都要**重开 GOP：只给第一块插 IDR，别的块还在
                //    按旧参考链往下解 ⇒ 画面上是"左半边立刻变灰、右半边继续褪色"。
                foreach (var lane in _extraLanes) lane.Encoder.RequestKeyframe();
            }
        }
    }

    /// <summary>
    /// 当前编码用的画面尺寸（还没建起来时是 0×0）。
    ///
    /// <para>
    /// 🔥 <b>报的是<b>源纹理</b>的尺寸（整幅画面），不是某一块的尺寸</b>：
    /// 页面的画布、输入坐标换算、中心的会话信息要的都是"这台机器的桌面有多大"。
    /// 分块/ROI 之后如果这里报成一小块，前端画布会跟着缩成那一块 ——
    /// 表现是"一开视口，整个页面就只剩画面的一角"，而且**没有任何报错**。
    /// </para>
    /// </summary>
    public (int Width, int Height) FrameSize
    {
        get
        {
            lock (_gate) return (_sourceWidth, _sourceHeight);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 编码区域（M5.5 第 4/5 项）：视口 + 分块
    // ═══════════════════════════════════════════════════════════

    /// <inheritdoc />
    public IReadOnlyList<EncodeRegion> Regions
    {
        get
        {
            lock (_gate) return _regions.ToArray();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠️ <b>这里<b>不</b>当场重建管线</b>：改视口这件事可能每滚一下就发生一次，
    /// 而重建一次是 ≈0.3 秒的空窗。本方法只记账 + 打脏，
    /// 真正的重建发生在<b>下一次编帧</b>（<see cref="EnsurePipeline"/>），
    /// 而"该不该现在换"是调用方（推流循环）用时间去抖决定的。
    /// </remarks>
    public bool SetViewport(EncodeRegion? viewport)
    {
        lock (_gate)
        {
            if (viewport is { } v)
            {
                // 只做"能不能编"这一层判断；越界/奇数是**下一层**按真实尺寸夹的
                // —— 这时候我们还不知道源纹理多大（首帧之前没人告诉我们）。
                if (v.Width < 2 || v.Height < 2 || v.X < 0 || v.Y < 0) return false;
            }

            if (Nullable.Equals(_viewport, viewport)) return true;

            _viewport = viewport;
            _regionDirty = true;

            return true;
        }
    }

    /// <summary>
    /// 按当前视口与分块上限，算出<b>这次该编哪几块</b>。
    ///
    /// <para>
    /// ⚠️ 返回值<b>永远非空</b>：视口算空（越界 / 太碎）时退回整幅画面并记一条
    /// <see cref="_rebuildNote"/> —— 返回空清单等于"什么都不编"，
    /// 那会让整条流一帧都出不来，而日志上只有"编码失败"四个字。
    /// </para>
    /// </summary>
    private List<EncodeRegion> ComputeRegions(int sourceWidth, int sourceHeight)
    {
        var whole = new EncodeRegion(0, 0, sourceWidth, sourceHeight);

        var box = whole;

        if (_viewport is { } requested)
        {
            if (EncodeRegionLayout.Clamp(requested, sourceWidth, sourceHeight) is { } clamped)
            {
                box = clamped;
            }
            else
            {
                _rebuildNote = $"视口 {requested} 在 {sourceWidth}×{sourceHeight} 的画面上是空的 —— 退回整幅画面";

                _viewport = null;
            }
        }

        var tiles = EncodeRegionLayout.Tiles(box, _maxTileWidth);

        // Tiles 只在 box 为 null 时给空；这里 box 一定非空，所以这是"实现兜底"，
        // 不是"可能发生"（宁可多这四行，也不要一帧不出的流）。
        if (tiles.Count == 0) return [whole];

        return [.. tiles];
    }

    /// <summary>
    /// 本实例<b>实际抓的那块屏</b>的 Id（见 <c>_screenId</c> 的注释：宿主要拿它去填自己的字段）。
    /// <para>还没 <c>Prepare</c> 过时是空串。</para>
    /// </summary>
    public string ScreenId
    {
        get
        {
            lock (_gate) return _screenId;
        }
    }

    /// <summary>同上，给人看的名字。</summary>
    public string ScreenName
    {
        get
        {
            lock (_gate) return _screenName;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 诊断面（给用例与现场报告用；生产路径不读它）
    //
    // 🔥 这几个**必须存在**，理由与 `WindowsMediaFoundationVideoEncoder` 那几个诊断属性同源：
    //    报告里写了"零拷贝成立"，就必须有一个能被独立读出来的事实与之对照，
    //    否则"报告"和"实际"之间没有任何一刻会被对齐 —— 而这两者不一致时，
    //    现场看到的是一句理直气壮的假话。
    // ═══════════════════════════════════════════════════════════

    /// <summary>零拷贝链路上有没有多出来的那一步（DDA 纹理不合要求时的显存内 CopyResource）。</summary>
    internal bool UsesIntermediateCopy
    {
        get
        {
            lock (_gate) return _converter?.UsesIntermediateCopy ?? false;
        }
    }

    /// <summary>设备管理器手上**真的开过一次句柄**（零拷贝成立与否的唯一硬证据）。</summary>
    internal bool DeviceHandleVerified
    {
        get
        {
            lock (_gate) return _gpu?.DeviceHandleVerified ?? false;
        }
    }

    /// <summary>已经编出来多少帧。</summary>
    internal long FramesEncoded
    {
        get
        {
            lock (_gate) return _framesEncoded;
        }
    }

    /// <summary>
    /// 🔥 <b>真的丢了</b>多少帧（<b>不含</b>"输出还没出来"）。
    ///
    /// <para>
    /// 🔴 它<b>只统计"交出去但确实产不出码流"</b>的那些帧，<b>不</b>统计异步 MFT
    /// 的正常延迟。这条区分是必需的不是讲究：早先"本次没出帧"被记成丢帧，
    /// 于是页面上显示"掉了很多帧"而实际一帧没丢 —— 与 M5.4-b §3.6 那个
    /// "把一次丢帧升级成一次掉线"是同一族错误（<b>计数错了，排查方向就反了</b>）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 调用方（推流循环）靠它<b>自己算增量</b>来判"这一轮编码到底丢没丢"，
    /// 而<b>不是</b>去读 <see cref="LastFailure"/> 的文案 —— 文案一改，分类就错。
    /// </para>
    /// </summary>
    public long DiscardedFrames
    {
        get
        {
            lock (_gate) return _framesDropped;
        }
    }

    /// <summary>配对队列溢出过几次（诊断面：非零 = 编码器收了帧却没吐出来）。</summary>
    internal long PendingOverflows
    {
        get
        {
            lock (_gate) return _pendingOverflows;
        }
    }

    /// <summary>取出码流时配对队列却是空的一共几次（诊断面：非零 = 配对错位）。</summary>
    internal long PairingMisses
    {
        get
        {
            lock (_gate) return _pairingMisses;
        }
    }

    /// <summary>重建过几次（换屏 / 换分辨率 / 换设备都会触发）。</summary>
    internal long Rebuilds
    {
        get
        {
            lock (_gate) return _rebuilds;
        }
    }

    /// <inheritdoc />
    public bool TryEncodeSurface(
        CapturedSurface surface,
        bool forceKeyframe,
        out EncodedStreamPayload? encoded)
        => EncodeCore(surface, forceKeyframe, null, out encoded);

    /// <inheritdoc />
    /// <remarks>
    /// 🔥 <b>它与 <see cref="TryEncodeSurface"/> 的关系是"多块 / 一块"</b>，不是两条实现：
    /// 两者都走 <see cref="EncodeCore"/>，差别只在"要不要把第 2 块以后也编出来"。
    /// 分成两份实现的话，指针叠加、配对队列、丢帧分类会各写一遍 ——
    /// 而它们之间任何一处不一致都<b>不会报错</b>。
    /// </remarks>
    public bool TryEncodeSurfaceRegions(
        CapturedSurface surface,
        bool forceKeyframe,
        List<EncodedRegionPayload> encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);

        encoded.Clear();

        var ok = EncodeCore(surface, forceKeyframe, encoded, out _);

        return ok && encoded.Count > 0;
    }

    /// <summary>
    /// <b>编一帧的全部逻辑</b>（一块或多块）—— 只有这一处实现。
    /// </summary>
    /// <param name="all">
    /// <c>null</c> = 只要第一块（<see cref="TryEncodeSurface"/> 的老语义，M5.4 的行为一字不变）；
    /// 非 <c>null</c> = 把每一块都编出来并填进它。
    /// </param>
    /// <param name="first">第一块的码流（= 老接口要的那一个）。</param>
    private bool EncodeCore(
        CapturedSurface surface,
        bool forceKeyframe,
        List<EncodedRegionPayload>? all,
        out EncodedStreamPayload? first)
    {
        first = null;

        lock (_gate)
        {
            if (_disposed)
            {
                // 🔥 这与"编码器忙"是<b>完全两回事</b>：那是正常的丢帧，这是调用方的生命周期错误。
                //    统一成一句"编码失败"会让"释放之后还在推帧"永远查不出来。
                _lastFailure = "本编码器已经释放，但还在往里喂帧 —— 调用方的生命周期错了。";

                return false;
            }

            if (!EnsurePipeline(surface.Width, surface.Height, out var buildFailure))
            {
                _lastFailure = buildFailure!.Reason;

                _framesDropped++;

                return false;
            }

            var count = _regions.Count;
            var produced = 0;

            for (var i = 0; i < count; i++)
            {
                // 老接口的语义：只产一块。多出来的块**不编**（编了也没人接）。
                if (all is null && i > 0) break;

                bool ok;
                EncodedStreamPayload? payload;
                string? failure;

                if (i == 0)
                {
                    ok = EncodeOneLane(
                        _converter!,
                        _encoder!,
                        _regions[0],
                        _pendingFrames,
                        ref _pendingOverflows,
                        ref _producedAnyFrame,
                        captureParameterSets: true,
                        surface,
                        forceKeyframe,
                        out payload,
                        out failure);
                }
                else
                {
                    var lane = _extraLanes[i - 1];

                    ok = EncodeOneLane(
                        lane.Converter,
                        lane.Encoder,
                        lane.Region,
                        lane.Pending,
                        ref lane.PendingOverflows,
                        ref lane.ProducedAnyFrame,
                        captureParameterSets: false,
                        surface,
                        forceKeyframe,
                        out payload,
                        out failure);

                    lane.LastFailure = failure;
                }

                if (!ok || payload is null)
                {
                    // ⚠️ 第一块失败 = **整帧没出来**（老语义，调用方按丢帧处理并可能会要求关键帧）；
                    //    后面某一块失败 = 那一块停上一帧，其余块照常推进 ——
                    //    把整帧丢掉会让"一块建不起来"变成"整路流卡死"，那是更坏的结果。
                    if (i == 0)
                    {
                        _lastFailure = failure ?? "编码器返回了 false，但没有给出原因。";

                        return false;
                    }

                    continue;
                }

                produced++;

                if (i == 0) first = payload;

                all?.Add(new EncodedRegionPayload(
                    TileIndex: i,
                    TileCount: count,
                    Region: _regions[i],
                    FrameWidth: _sourceWidth,
                    FrameHeight: _sourceHeight,
                    Stream: payload));
            }

            if (produced > 0) _lastFailure = null;

            // 🔥 **多块时第一块没成功就不能报成功**：调用方（推流循环）会拿 `first`
            //    去打包、算尺寸闸门、决定要不要清 `wantKeyframe` —— 给它一个
            //    "没出来的帧"会让后面每一步都在处理 null。
            return first is not null;
        }
    }

    /// <summary>
    /// 编<b>一块</b>：转 NV12（只转这一块）→ 叠指针 → 硬编 → 配对元数据。
    ///
    /// <para>
    /// 🔥 <b>"喂进去没有"这个判据是本方法的核心</b>（M5.4-b 的教训，见
    /// <see cref="EncodedStreamPayload"/> 与推流循环里的长注释）：异步 MFT 的
    /// 输出晚一次调用才到，所以 <c>TryEncode</c> 返回 <c>false</c> 可能只是"还没出来"。
    /// 用 <c>FramesFed</c> 的增量区分，而不是去读失败文案。
    /// </para>
    /// </summary>
    private bool EncodeOneLane(
        D3D11Nv12Converter converter,
        WindowsMediaFoundationVideoEncoder encoder,
        EncodeRegion region,
        Queue<PendingFrame> pending,
        ref long pendingOverflows,
        ref bool producedAnyFrame,
        bool captureParameterSets,
        CapturedSurface surface,
        bool forceKeyframe,
        out EncodedStreamPayload? encoded,
        out string? failure)
    {
        encoded = null;
        failure = null;

        // ── ① 转 NV12（GPU 上，一字节都不落内存）──
        //
        // 🔥 转换器自己握着"只处理哪一块"（基准矩形）⇒ 这里传的还是整屏纹理，
        //    但它只把 region 那一块搬到输出。**不需要**在这里裁纹理。
        if (!converter.TryConvert(surface.TextureHandle, _grayscale, out var convertFailure))
        {
            failure = $"BGRA→NV12 转换失败：{convertFailure}";

            _framesDropped++;

            return false;
        }

        // ── ①.5 把指针合成进去 ──
        //
        // 🔥 顺序是**硬性**的：叠加的目标就是 ① 那一趟的输出（`converter.OutputTexture`）。
        // ⚠️ 但成败是**独立**的：指针画不上最坏就是"鼠标看不见"，画面本身是好的 ——
        //    为它把整帧丢掉，会让"用户一动鼠标画面就卡"，比不画还糟。
        TryCompositePointer(converter, surface, region);

        // ── ② 编 ──
        //
        // 🔥 **这里要分清三件事**（早先混成一件，代价见 `_pendingFrames` 的注释）：
        //    ① 编码器**收下了**这一帧、但输出还没出来（异步 MFT 的正常延迟）
        //       ⇒ **不是丢帧**：码流下一次就出来，且带着**它自己**的元数据；
        //    ② 编码器**没收**（没有输入令牌 = 忙）⇒ 丢帧，但**不会在参考链上留洞**
        //       （它根本没进编码器，下一个 P 帧参考的仍是上一个真编过的帧）⇒ 不必强制关键帧；
        //    ③ 真失败（转换失败 / 管线建不起来）⇒ 丢帧，且要说得出为什么。
        //
        //    判据是"**编码器有没有把这一帧收下**"（`FramesFed` 有没有动）——
        //    **不读失败文案**：文案一改，分类就错，而错的方向恰好是"把正常延迟当成丢帧"。
        var capturedMonoTicks = Stopwatch.GetTimestamp();
        var fedBefore = encoder.FramesFed;
        var busyBefore = encoder.FramesDroppedBusy;

        var produced = encoder.TryEncode(
            converter.OutputTexture, region.Width, region.Height, forceKeyframe, out var frame);

        var accepted = encoder.FramesFed > fedBefore;

        if (accepted)
        {
            EnqueuePendingFrame(pending, ref pendingOverflows, region, surface, capturedMonoTicks);
        }

        if (!produced || frame is null)
        {
            if (accepted)
            {
                // ① 正常延迟 —— **不计丢帧、不要求关键帧**。
                failure = "编码器收下了这一帧，但码流还没出来（异步 MFT 的正常延迟，不是丢帧）。";

                return false;
            }

            _framesDropped++;

            failure = encoder.FramesDroppedBusy > busyBefore
                ? "编码器这一帧没收（异步 MFT 没有输入令牌 = 忙）—— 按丢帧处理，不是故障。"
                : encoder.LastFailure ?? "编码器返回了 false，但没有给出原因。";

            return false;
        }

        _framesEncoded++;

        producedAnyFrame = true;

        // 参数集是**会话级**的（带外打包只带一份 description）⇒ 只从第一块取。
        if (frame.IsKeyframe && captureParameterSets) TryCaptureParameterSets(encoder);

        // 🔥 **码流必须与"它自己的"元数据配对**：这里拿到的是**更早**喂进去那一帧的字节
        //    （异步延迟），所以元数据也只能取队列里最早那一份 —— 拿本次的 `surface`
        //    就等于把"这一帧的抓屏时刻"贴到了"上一帧的码流"上。
        var source = DequeuePendingFrame(pending, region, surface);

        encoded = new EncodedStreamPayload(
            Codec: Codec,
            IsKeyframe: frame.IsKeyframe,

            // ⚠️ **报的是这一块的尺寸**（不是整幅）：前端要按它把码流画到
            //    `Region` 那个位置上。报成整幅会让每一块都被拉满整个画布
            //    —— 症状是"分块之后画面变成一堆互相覆盖的大图"。
            Width: region.Width,
            Height: region.Height,
            CapturedUnixMs: source.CapturedUnixMs,
            Data: frame.Data.ToArray(),

            // 🔴🔴 **这里刻意不填 `Description`**，而且这不是"还没做"。
            //
            //    MF 给的 `MF_MT_MPEG_SEQUENCE_HEADER` 是 **Annex-B** 形式的 SPS/PPS
            //    （带 `00 00 00 01` 起始码），而 `EncodedStreamPayload.Description`
            //    的契约是 **avcC / hvcC**（长度前缀的 AVCDecoderConfigurationRecord）。
            //    两者**不是一回事**：直接塞进去，浏览器的 `configure()` 会失败或花屏
            //    —— 那正是"贴了 avcC、码流却还是 Annex-B"这个连体错误的一半。
            //
            //    ⇒ 转化必须**与"把码流转成长度前缀"一起**发生，两件事是一体的。
            //      而那份转换是纯函数，住在 `ControlServer.Agent.Video`
            //      （平台层不做它 —— 那是分层判据）；接线**已经在**
            //      `RemoteStreamHost.PumpAsync` 的"打包"那一段完成（§8 第 6 步）。
            //
            //    ⇒ 所以本层交出去的 `Description` **恒为 null**，且这是**契约的一部分**
            //      （不是"还没做"）：带内要的是"码流原样 + 不带 description"，
            //      而带内正是本批的默认。参数集要么在码流里，要么由调用方
            //      通过 `TryReadParameterSets()` 拿去自己转。
            Description: null,

            // 🔥 **由本层带出去**，而不是让调用方在编完之后自己取一次：
            //    那样取到的是"本次调用"的时刻，而字节是上一次喂进去那一帧的
            //    ⇒ AV 同步会整体偏一个编码周期。见 `EncodedStreamPayload` 的说明。
            CapturedMonoTicks: source.CapturedMonoTicks);

        return true;
    }

    /// <summary>
    /// 记下"这一帧的元数据"，等它的码流出来时配对用。
    /// </summary>
    /// <remarks>
    /// ⚠️ 队列有上限：编码器若收了帧却一直不吐（那是故障，不是延迟），
    /// 元数据会越积越多而每一条都对应不上任何码流。到上限就把最老的挤掉并计数 ——
    /// <b>比起"无限涨"和"安静地挤掉"，我们至少要让它能被看见</b>。
    /// <para>
    /// ⚠️ 队列是<b>逐块</b>的（见 <see cref="TileLane.Pending"/>）：共用一支会让每块的
    /// 时戳都来自别的块，症状是"有一块画面比别块慢半拍"，而日志全绿。
    /// </para>
    /// </remarks>
    private void EnqueuePendingFrame(
        Queue<PendingFrame> pending,
        ref long overflows,
        EncodeRegion region,
        CapturedSurface surface,
        long capturedMonoTicks)
    {
        while (pending.Count >= MaxPendingFrames)
        {
            pending.Dequeue();

            overflows++;
        }

        pending.Enqueue(new PendingFrame(
            region.Width,
            region.Height,
            surface.CapturedAt.ToUnixTimeMilliseconds(),
            capturedMonoTicks));
    }

    /// <summary>
    /// 取出一份元数据与刚出来的码流配对。
    /// </summary>
    /// <remarks>
    /// ⚠️ 队列为空是<b>理论上不该发生</b>的事（出了一份码流，就一定有一个更早喂进去的帧）。
    /// 真发生时**不伪造**：退回本次 <paramref name="fallback"/> 的元数据，并计数 ——
    /// 让"这一路的抓屏时刻不可信"这件事有个可读的出处，而不是安静地贴错。
    /// </remarks>
    private PendingFrame DequeuePendingFrame(
        Queue<PendingFrame> pending, EncodeRegion region, CapturedSurface fallback)
    {
        if (pending.Count > 0) return pending.Dequeue();

        _pairingMisses++;

        return new PendingFrame(
            region.Width,
            region.Height,
            fallback.CapturedAt.ToUnixTimeMilliseconds(),
            Stopwatch.GetTimestamp());
    }

    /// <inheritdoc />
    public bool TryReadParameterSets(out byte[] parameterSets)
    {
        lock (_gate)
        {
            if (_parameterSets is null && !_disposed && _encoder is not null) TryCaptureParameterSets(_encoder);

            parameterSets = _parameterSets ?? Array.Empty<byte>();

            return _parameterSets is not null;
        }
    }

    /// <summary>
    /// 🔥 <b>尺寸/格式这道前置闸门</b>（<b>纯函数</b>，不碰任何设备 ⇒ 可以被普通用例钉死）。
    ///
    /// <para>
    /// 它拦的是三类"一定会失败、但失败信息会把排查带偏"的输入：
    /// </para>
    /// <list type="number">
    ///   <item>尺寸 ≤ 1 或<b>奇数</b> —— NV12 是 4:2:0 半平面，宽高必须都是偶数，
    ///         否则色度平面没有整数行数；</item>
    ///   <item><b>H.264 超过 4096</b>（任一维）—— 这不是驱动问题、也不是权限问题，
    ///         是格式本身的上限，唯一出路是换 HEVC；</item>
    ///   <item>HEVC 超过 8192 —— 8K 以上本方案不覆盖。</item>
    /// </list>
    /// </summary>
    /// <param name="codec">打算用的编码格式。</param>
    /// <param name="width">画面宽（像素）。</param>
    /// <param name="height">画面高（像素）。</param>
    /// <param name="reason">不通过的原因；通过时为 <c>null</c>。</param>
    public static bool TryValidateGeometry(VideoCodec codec, int width, int height, out string? reason)
    {
        reason = null;

        if (width <= 1 || height <= 1)
        {
            reason = $"尺寸 {width}×{height} 太小，建不了编码管线。";

            return false;
        }

        if ((width & 1) != 0 || (height & 1) != 0)
        {
            reason = $"尺寸 {width}×{height} 是奇数 —— NV12（4:2:0 半平面）要求宽高都为偶数，"
                + "奇数尺寸下色度平面没有整数行数。";

            return false;
        }

        var limit = codec == VideoCodec.Hevc ? HevcMaxDimension : H264MaxDimension;

        if (width > limit || height > limit)
        {
            reason = codec == VideoCodec.Hevc
                ? $"尺寸 {width}×{height} 超过 HEVC 硬编上限 {limit}×{limit} —— 本方案不覆盖 8K 以上。"
                : $"尺寸 {width}×{height} 超过 H.264 硬编上限 {limit}×{limit}"
                    + "（这是格式本身的上限，所有硬件编码器都是这个数）—— 超过 4K 只能用 HEVC。";

            return false;
        }

        return true;
    }

    /// <summary>一份能直接贴进验收报告的实例说明（含三方各自的如实状态）。</summary>
    public string Describe()
    {
        lock (_gate)
        {
            if (_converter is null || _encoder is null)
            {
                return $"GPU 管线未建起来（{_lastFailure ?? "原因未知"}）";
            }

            var parts = new List<string>
            {
                _converter.Describe(),
                _encoder.Describe(),
                $"零拷贝链路上有没有多出来的那一步：{(_converter.UsesIntermediateCopy ? "有" : "没有")}"
                    + (_converter.UsesIntermediateCopy
                        ? "（DDA 的纹理不符合输入视图的绑定要求，退化成显存内一次 CopyResource —— 不过 PCIe，可接受）"
                        : "（输入视图直接建在 DDA 那张纹理上）"),
                $"抓屏适配器：{_capturer.AdapterName ?? "未知"}（节点 {_capturer.AdapterIndex}）",
                $"尺寸 {_sourceWidth}×{_sourceHeight}，"
                    + $"编出 {_framesEncoded} 帧 / 丢 {_framesDropped} 帧"
                    + (_producedAnyFrame ? string.Empty : "（⚠️ 一帧都没出来过）"),
                // 🔥 分块/ROI 的实情必须能一眼看见：块数、每块多大、视口在哪。
                //    只写"8K 屏"而看不出"切了 2 块"，就没法判断帧率是被哪一块拖住的。
                $"编码区域：{_regions.Count} 块"
                    + (_regions.Count == 0
                        ? "（还没算出来）"
                        : $"（{string.Join(" / ", _regions)}；画面 {_sourceWidth}×{_sourceHeight}"
                            + (_viewport is { } vp ? $"，视口 {vp}" : "，整幅画面")
                            + $"，单块上限 {(_maxTileWidth == 0 ? "不切" : $"{_maxTileWidth} px")}）"),
                $"喂进↔取出配对：待配对 {_pendingFrames.Count} 帧"
                    + $"，队列溢出 {_pendingOverflows} 次"
                    + $"，配对落空 {_pairingMisses} 次"
                    + ((_pendingOverflows == 0 && _pairingMisses == 0)
                        ? "（两者都为 0 = 每份码流都对上了它自己那一帧的抓屏时刻）"
                        : "（⚠️ 非零 = 帧头的抓屏时刻可能对不上，别拿它测延迟）"),
                $"建/重建过 {_rebuilds} 次" + (_rebuildNote is null ? "（最近一次没有重建，就是首次那次）" : $"（最近一次：{_rebuildNote}）"),
                // 🔥 帧率被"定时器粒度"卡住时，这一行是唯一能看出区别的地方：
                //    15.625 ms 粒度下，`Task.Delay(8)` 会实睡 16 ms（见 WindowsTimerPrecision）。
                $"定时器：{WindowsTimerPrecision.CurrentNote}",
                $"参数集：{(_parameterSets is null
                    ? $"没拿到（已问 {_parameterSetAttempts} 次）—— 按 Annex-B 内联处理"
                    : $"{_parameterSets.Length} 字节（Annex-B）")}",
                // ⚠️ 与接口上的 CursorDiagnostics **同源**（一份真相）：宿主日志里那一行是本设备的
                //    现场入口，这里只是同一句话加上前缀、当报告里的一格。
                //    各写一份的话，改一处漏一处就会出现"报告说画上了、日志说没画上"。
                $"指针合成：{CursorDiagnosticsCore()}",
            };

            return string.Join(Environment.NewLine, parts);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;

            ReleasePipeline();

            // ⚠️ 抓屏器最后放：设备是它建的，而转换器/编码器都挂在那个设备上
            //    （先放它 = 让上面两个指向一段已经释放的内存）。
            _capturer.Dispose();

            // 🔥 定时器粒度是**成对**的：提一次就必须还一次（见构造函数里的说明）。
            _timerPrecision?.Dispose();
            _timerPrecision = null;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 建立 / 重建
    // ═══════════════════════════════════════════════════════════

    /// <summary>建实例时的准备：挑屏 → 抓一帧拿到真实尺寸 → 建转换器与编码器。</summary>
    private bool Prepare(string? screenId, out GpuPipelineVerdict? failure)
    {
        failure = null;

        // ── 挑屏（在校验阶段就挑，而不是等抓的时候才炸）──
        var screens = _capturer.EnumerateScreens();

        ScreenDescriptor? target;

        if (!string.IsNullOrWhiteSpace(screenId))
        {
            target = screens.FirstOrDefault(
                s => string.Equals(s.Id, screenId, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            target = screens.FirstOrDefault(s => s.IsPrimary)
                ?? screens.FirstOrDefault(s => !s.IsVirtualDesktop);
        }

        if (target is null)
        {
            failure = new GpuPipelineVerdict(
                GpuPipelineStage.Screen,
                $"找不到要抓的屏（请求的是「{screenId ?? "主屏"}」）。"
                + $"本机可抓的屏：{(screens.Count == 0 ? "一块都没有（这个会话没有桌面？）" : string.Join(" / ", screens.Select(s => s.Id)))}");

            return false;
        }

        // 🔥 **"全部显示器"这条请求要在建之前用 DDA 的理由拒掉**，不能让它一路走到尺寸闸门。
        //    不拦的话，7680×4320 会先在 H.264 的 4096 上限上失败，于是报出来的是
        //    "尺寸超格式上限" —— 而真因是 **DDA 一次只能抓一个 output**，
        //    这个分辨率本身没问题（换 GDI 那条路就能拼）。理由给错，排查方向就反了。
        //
        //    归 `Screen` 一级（环境问题）⇒ 调用方<b>安静回落 MJPEG</b>，与 §3.5 的回退链一致：
        //    GDI 那条路本来就能抓虚拟桌面，所以这里不是"降级到更差的档"，是"换一条能做的路"。
        if (target.IsVirtualDesktop)
        {
            failure = new GpuPipelineVerdict(
                GpuPipelineStage.Screen,
                "DXGI 抓屏一次只能抓**一块**显示器（DDA 只对单个 output 建 duplication），"
                + "所以 GPU 硬编那条路做不了「全部显示器」拼接"
                + "—— 请改走 GDI/MJPEG 那条路（它能拼），或者选一块具体的显示器。");

            return false;
        }

        // 🔥 尺寸闸门在**建之前**判，并且用的是"这台机器**真实**报出来的屏尺寸"
        //    （不是调用方说的），所以它拦得住"中心按 4K 下发、这台机器其实是 8K 屏"这种组合。
        //    ⚠️ 它归到 Encode 一级：这是**格式上限**，不是环境问题 ——
        //       实务上唯一的出路是换 HEVC 或降分辨率，都不该被当成"跳过"。
        if (!TryValidateGeometry(_codec, target.Width, target.Height, out var geometry))
        {
            failure = new GpuPipelineVerdict(
                GpuPipelineStage.Encode,
                $"屏「{target.Name}」{target.Width}×{target.Height} 不能用 {_codec}：{geometry}");

            return false;
        }

        // ── 抓一帧（既是"验证真能抓到"，也是拿真实纹理尺寸）──
        CapturedSurface? surface = null;
        string? lastFailure = null;

        for (var attempt = 1; attempt <= ProbeAttempts && surface is null; attempt++)
        {
            surface = _capturer.TryCaptureSurface(target.Id, CancellationToken.None, out lastFailure);
        }

        if (surface is null)
        {
            failure = new GpuPipelineVerdict(
                GpuPipelineStage.Capture,
                $"探了 {ProbeAttempts} 次都没从「{target.Name}」拿到一帧有内容的画面"
                + $"（最后一次：{lastFailure ?? "桌面没有变化"}）。"
                + "常见原因：这块屏正被别的抓屏程序占用（Windows 同一时刻只允许一个进程抓一块 output）；"
                + "或者这台机器的输出组合不支持 Desktop Duplication（该走 GDI 降级路径）。");

            return false;
        }

        if (!EnsurePipeline(surface.Width, surface.Height, out failure)) return false;

        // ⚠️ 只有在**整条链子都建起来之后**才记这块屏：
        //    记早了，一次失败的 Prepare 会留下一个"看起来已经绑好"的 ScreenId，
        //    而调用方会拿它去填自己的字段（见 `_screenId` 的注释）。
        _screenId = target.Id;
        _screenName = target.Name;

        return true;
    }

    /// <summary>
    /// 保证"当前这条链子"与"要编的那张画面"匹配，不匹配就整体重建。
    ///
    /// <para>
    /// 🔥 <b>三种不匹配都必须重建，而且三种的症状都不是报错</b>：
    /// </para>
    /// <list type="number">
    ///   <item><b>设备换了</b>（抓屏器换屏 / duplication 重建时会把旧设备 Dispose 掉）
    ///         ⇒ 旧的转换器与编码器指着一段已释放的内存，是<b>用后释放</b>，
    ///         可能崩、也可能"看着正常但画面是垃圾"；</item>
    ///   <item><b>分辨率变了</b>（用户改分辨率 / 拔插屏）⇒ 媒体类型与视频处理器里
    ///         都写死了尺寸，拿旧实例编新尺寸<b>不会得到缩放过的画面</b>，
    ///         只会得到噪点或被裁掉一块；</item>
    ///   <item>还没建过 ⇒ 头一次建。</item>
    /// </list>
    /// <para>
    /// ⚠️ 重建必然<b>换一次 GOP</b>（新编码器的第一个帧就是关键帧），所以重建之后
    /// 观众不会花屏；但它也意味着"重建"不能发生在每帧上 —— 判据必须是
    /// <b>引用相等 / 尺寸相等</b>这类廉价比较，不是"试一发看看"。
    /// </para>
    /// </summary>
    private bool EnsurePipeline(int sourceWidth, int sourceHeight, out GpuPipelineVerdict? failure)
    {
        failure = null;

        var gpu = _capturer.GpuContext;

        if (gpu is null)
        {
            failure = new GpuPipelineVerdict(
                GpuPipelineStage.ColorConvert,
                "抓屏器手上没有 D3D11 设备上下文 —— 说明这一帧不是它抓的，或者它刚才重建失败。"
                + "编码器必须建在抓屏那同一个设备上。");

            return false;
        }

        if (_gpu is not null && !ReferenceEquals(_gpu, gpu))
        {
            _rebuildNote = "抓屏器换了 D3D11 设备（换屏或 duplication 重建）—— 转换器与编码器整体重建";

            ReleasePipeline();
        }

        // ── ① 先算出"这次该编哪几块"（视口 + 分块上限 ⇒ 区域清单）──
        var desired = ComputeRegions(sourceWidth, sourceHeight);

        // ⚠️ 判据是**清单相等**，不是"尺寸相等"：ROI 只换位置（宽高不变）时，
        //    拿尺寸当判据会认为"没变"⇒ 视口永远不生效，而日志上什么都看不出来。
        var same = _converter is not null
            && _encoder is not null
            && !_regionDirty
            && _sourceWidth == sourceWidth
            && _sourceHeight == sourceHeight
            && desired.Count == _regions.Count
            && desired.SequenceEqual(_regions);

        if (same) return true;

        if (_converter is not null || _encoder is not null || _extraLanes.Count > 0)
        {
            var before = _regions.Count == 0 ? "（无）" : string.Join(" / ", _regions);

            _rebuildNote = _converter is null
                ? $"编码区域：{string.Join(" / ", desired)}（画面 {sourceWidth}×{sourceHeight}）—— 建转换器与编码器"
                : $"编码区域从 {before} 变成 {string.Join(" / ", desired)}"
                    + $"（画面 {sourceWidth}×{sourceHeight}）—— 转换器与编码器整体重建"
                    + "（代价 ≈0.3 秒空窗、首帧必关键帧）";

            ReleasePipeline();
        }

        _regions.Clear();
        _regions.AddRange(desired);

        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _regionDirty = false;

        var laneCount = _regions.Count;

        // ── ② 第一块（这就是 M5.4 那条链子，尺寸 = 这一块）──
        if (!TryBuildLane(
                gpu, _regions[0], sourceWidth, sourceHeight, laneCount,
                out var firstConverter, out var firstEncoder, out failure))
        {
            _regions.Clear();

            return false;
        }

        _converter = firstConverter;
        _encoder = firstEncoder;
        _gpu = gpu;

        // 新实例 ⇒ 参数集与"问过几次"的记录都作废。
        _parameterSets = null;
        _parameterSetAttempts = 0;

        // ── ③ 其余块 ──
        for (var i = 1; i < laneCount; i++)
        {
            var region = _regions[i];

            if (!TryBuildLane(
                    gpu, region, sourceWidth, sourceHeight, laneCount,
                    out var converter, out var encoder, out var why))
            {
                // 🔥 **绝不留半条链子**：半条链子的表现是"画面只有左半边在动"，
                //    而那看起来像编码器坏了，不像"少建了一块"。整条放掉、如实报错。
                ReleasePipeline();
                _regions.Clear();

                failure = new GpuPipelineVerdict(
                    GpuPipelineStage.Encode,
                    $"第 {i + 1}/{laneCount} 块（{region}）建不起来：{why?.Reason} —— "
                    + "分块编码这一路放弃；请改走 MJPEG，或者降低分辨率/改成单块屏。");

                return false;
            }

            _extraLanes.Add(new TileLane { Region = region, Converter = converter!, Encoder = encoder! });
        }

        _rebuilds++;

        return true;
    }

    /// <summary>
    /// 建<b>一块</b>的转换器 + 编码器。
    ///
    /// <para>
    /// 🔥 码率按块数<b>均分</b>：会话给的码率是<b>整路</b>的预算，
    /// 每块都拿满额会让 2 块变成 2 倍码率 —— 那正好把"分块换来的帧率"
    /// 又用带宽还回去，而且会在交换机上撞到别的东西（心跳/音频/配置同一条链路）。
    /// </para>
    /// </summary>
    private bool TryBuildLane(
        D3D11GpuContext gpu,
        EncodeRegion region,
        int sourceWidth,
        int sourceHeight,
        int laneCount,
        out D3D11Nv12Converter? converter,
        out WindowsMediaFoundationVideoEncoder? encoder,
        out GpuPipelineVerdict? failure)
    {
        converter = null;
        encoder = null;
        failure = null;

        // ⚠️ 闸门判的是**这一块**的尺寸（编码器的上限是单边长度）。
        if (!TryValidateGeometry(_codec, region.Width, region.Height, out var geometry))
        {
            failure = new GpuPipelineVerdict(
                GpuPipelineStage.Encode,
                $"{geometry}（这一块是 {region}，整幅画面 {sourceWidth}×{sourceHeight}）");

            return false;
        }

        converter = D3D11Nv12Converter.TryCreate(gpu, sourceWidth, sourceHeight, region, out var convertFailure);

        if (converter is null)
        {
            failure = new GpuPipelineVerdict(
                GpuPipelineStage.ColorConvert, $"BGRA→NV12 转换器建不起来（{region}）：{convertFailure}");

            return false;
        }

        var options = MediaFoundationEncoderOptions.Default(_codec, region.Width, region.Height, _fps);

        if (_bitrateBps is { } explicitBitrate)
        {
            var share = Math.Max(1, laneCount);
            var perLane = Math.Max(MinLaneBitrateBps, explicitBitrate / share);

            options = options with { BitrateBps = perLane };
        }

        options = options with { GopSize = _gopSize };

        // 🔥 这两条**只在建管线时**能改（与码率同一个理由：它们挂在编码器的属性/媒体类型上）。
        options = options with
        {
            SliceCount = SliceCount,
            DisablePreprocessing = DisablePreprocessing,
        };

        encoder = WindowsMediaFoundationVideoEncoder.TryCreate(gpu, options, out var encodeFailure);

        if (encoder is null)
        {
            converter.Dispose();
            converter = null;

            failure = new GpuPipelineVerdict(
                GpuPipelineStage.Encode, $"硬编码器建不起来（{region}，{options.BitrateBps / 1000} kbps）：{encodeFailure}");

            return false;
        }

        return true;
    }

    /// <summary>分块时单块码率的下限：再低就没有可用画面了（宁可少一块）。</summary>
    private const int MinLaneBitrateBps = 500_000;

    /// <summary>
    /// 放掉当前这条链子（<b>不动抓屏器与设备</b>）。
    /// <para>⚠️ 顺序是编码器 → 转换器：编码器手上可能还攥着引用转换器输出纹理的样本。</para>
    /// </summary>
    private void ReleasePipeline()
    {
        _encoder?.Dispose();
        _encoder = null;

        _converter?.Dispose();
        _converter = null;

        // 🔥 分块：其余块的链子**同生同灭**。留着任何一块，它都指着已经放掉的设备/输出纹理
        //    —— 那是"用后释放"，可能崩，也可能"看着正常但那一块是垃圾"。
        foreach (var lane in _extraLanes)
        {
            try
            {
                lane.Encoder.Dispose();
                lane.Converter.Dispose();
            }
            catch
            {
                // 忽略：已经在放整条链子，这里再抛就把后面几块也漏掉了。
            }
        }

        _extraLanes.Clear();
        _regions.Clear();

        _gpu = null;

        _parameterSets = null;
        _parameterSetAttempts = 0;

        // ⚠️ 指针用的两张纹理也建在那个设备上 —— 设备已经放了，它们的句柄必须跟着作废。
        //    留着会让下一轮的第一次合成拿着两个悬空句柄去 CopySubresourceRegion，
        //    而那个 API **返回 void**，出错连一句话都没有。
        ReleasePointerTexture();
        ReleasePointerStaging();

        // ⚠️ 配对队列也必须清掉：底下那个 MFT 是**新建的**（它的 `FramesFed` 从 0 开始），
        //    留着旧队列会让"新编码器的第一份输出"去配"旧编码器留下的那一帧" ——
        //    于是重建之后的第一帧，抓屏时刻是错的（而它恰好经常是那个关键帧）。
        _pendingFrames.Clear();
    }

    // ═══════════════════════════════════════════════════════════
    // 指针合成（M5.4-b 第 5 步）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把指针合成进"已经转好的 NV12 输出"。
    ///
    /// <para>
    /// 🔥 <b>它绝不返回失败、也绝不抛</b>：指针画不上不是"这一帧坏了"，而是"这一帧没有鼠标"。
    /// 把整帧丢掉会让用户一动鼠标画面反而更卡 —— 那是比不画更糟的结果。
    /// 真出问题记进 <see cref="CursorNote"/> 并计数，由报告如实说。
    /// </para>
    ///
    /// <para>
    /// 四个步骤：<b>算矩形 → 读回那块背景 → CPU 按 DXGI 规格合成 → 上传并让 VideoProcessor 写回</b>。
    /// 其中只有第三步是"聪明"的部分（三态语义），其余三步都是搬运。
    /// </para>
    /// </summary>
    private void TryCompositePointer(
        D3D11Nv12Converter converter, CapturedSurface surface, EncodeRegion clip)
    {
        // ⚠️ 指针快照来自抓屏器：形状是**缓存**的（DDA 只在形状真的变了时才给字节），
        //    而位置是**当帧**的 —— 两者混起来看就会得到"指针不动"。
        var pointer = _capturer.Pointer;

        if (!PointerCompositor.TryGetRegion(
                pointer, surface.Width, surface.Height, out var region, out var why))
        {
            // 指针不可见 / 完全在画面外 / 形状还没拿到 —— 都是**正常情形**，
            // 不该计进失败数（否则"没有鼠标"会被读成"指针合成坏了"）。
            _cursorNote = why;
            _cursorLastInput = $"{DescribePointerInput(pointer)}（没算出矩形：{why}）";

            return;
        }

        // 🔥 **只编一块时，指针矩形要夹进那一块**（M5.5）：指针跨在块边界上很常见
        //    （比如正好压在接缝上）。不夹的话，那块背景会去读<b>不存在的区域</b> ——
        //    `CopySubresourceRegion` 返回 void，读回来是别处的内容，
        //    画面上表现为"接缝处有一小块糊掉的鼠标"。
        if (!clip.Covers(surface.Width, surface.Height))
        {
            var wanted = new EncodeRegion(region.Left, region.Top, region.Width, region.Height);

            if (clip.Intersect(wanted) is not { } visible)
            {
                // 指针完全不在这一块里 ⇒ 这一块没有指针可画。**不是失败**。
                _cursorNote = $"指针在 {wanted}，不在这一块 {clip} 里";
                _cursorLastInput = $"{DescribePointerInput(pointer)}（不在这一块里）";

                return;
            }

            region = new DxgiInterop.Rect
            {
                Left = visible.X,
                Top = visible.Y,
                Right = visible.X + visible.Width,
                Bottom = visible.Y + visible.Height,
            };
        }

        if (!TryReadPointerRegion(surface.TextureHandle, region, out var background, out var pitch, out var note))
        {
            _cursorFailed++;
            _cursorNote = note;
            _cursorLastInput = $"{DescribePointerInput(pointer)} ⇒ 矩形 {DescribeRegion(region)}（读回背景失败：{note}）";

            return;
        }

        // 🔥 三态语义（含 `MASKED_COLOR` 的逐位 XOR、`MONOCHROME` 的 AND/XOR 双掩码）全在这里。
        //    它住在 `PointerCompositor` 里，是**纯函数**，可以脱离设备单测。
        //
        // ⚠️ 传的是**画面坐标**（不是块内坐标）：`Composite` 自己会减去原点
        //    （`baseX = originX - regionX`），所以这一处两种坐标系下都只有一种正确写法。
        PointerCompositor.Composite(
            pointer, region.Left, region.Top, background, pitch, region.Width, region.Height);

        // 写到输出的哪里 = 画面坐标减去这一块的原点（1:1 搬运，不缩放）。
        var destination = new DxgiInterop.Rect
        {
            Left = region.Left - clip.X,
            Top = region.Top - clip.Y,
            Right = region.Right - clip.X,
            Bottom = region.Bottom - clip.Y,
        };

        if (!TryUploadAndOverlay(converter, surface, region, destination, background, pitch, out note))
        {
            _cursorFailed++;
            _cursorNote = note;
            _cursorLastInput = $"{DescribePointerInput(pointer)} ⇒ 矩形 {DescribeRegion(region)}（写回失败：{note}）";

            return;
        }

        _cursorComposited++;
        _cursorNote = null;

        // ⚠️ 记在**成功之后**：它就是"三个 true 都返回了"这一刻的真实入参，
        //    也正是下一步要拿去和"画面里到底有没有"对质的那份证据。
        _cursorLastInput = $"{DescribePointerInput(pointer)} ⇒ 矩形 {DescribeRegion(region)}";
    }

    /// <summary>
    /// 从桌面副本上读回"指针所在的那一小块"BGRA。
    ///
    /// <para>
    /// 🔥 <b>读的是副本、不是 NV12 输出</b>：XOR / AND 这些语义<b>定义在 RGB 域上</b>
    /// （对 BGRA 值逐位做），在 YUV 域做是错的；而且从 NV12 解回 RGB 再编回去
    /// 会让那一块比周围"糊一点"（4:2:0 走两趟）。
    /// </para>
    /// </summary>
    private bool TryReadPointerRegion(
        nint desktopCopy, DxgiInterop.Rect region,
        out byte[] background, out int pitch, out string? failure)
    {
        background = [];
        pitch = region.Width * 4;
        failure = null;

        var gpu = _gpu!;

        if (!EnsurePointerStaging(region.Width, region.Height, out failure)) return false;

        // ⚠️ `CopySubresourceRegion` 返回 void —— 给错区域它**不会报错**，
        //    读回来是别的地方的内容（而那种图看起来只是"指针位置不对"）。
        var box = D3D11Interop.D3D11Box.From(region);

        var copy = ComVtable.Bind<D3D11Interop.Delegates.CopySubresourceRegionFn>(
            gpu.Context, D3D11Interop.ContextSlots.CopySubresourceRegion);

        copy(
            gpu.Context,
            _pointerStaging, 0, 0, 0, 0,
            desktopCopy, 0, ref box);

        var map = ComVtable.Bind<D3D11Interop.Delegates.MapFn>(
            gpu.Context, D3D11Interop.ContextSlots.Map);

        var hr = map(gpu.Context, _pointerStaging, 0, D3D11Interop.MapRead, 0, out var mapped);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"Map 指针小块失败（{ComVtable.Describe(hr)}）—— {region.Width}×{region.Height}。";

            return false;
        }

        var unmap = ComVtable.Bind<D3D11Interop.Delegates.UnmapFn>(
            gpu.Context, D3D11Interop.ContextSlots.Unmap);

        try
        {
            var result = new byte[pitch * region.Height];

            // ⚠️ 逐行拷，**必须用 RowPitch 而不是 width×4**（与整屏读回同一条教训）：
            //    驱动为了对齐会把行距撑大，按宽度硬拷会让每一行错开一点点 ——
            //    在这个尺寸上表现为"指针被拉斜了"，而不会报任何错。
            for (var y = 0; y < region.Height; y++)
            {
                Marshal.Copy(mapped.Data + (y * (int)mapped.RowPitch), result, y * pitch, pitch);
            }

            background = result;

            return true;
        }
        finally
        {
            unmap(gpu.Context, _pointerStaging, 0);
        }
    }

    /// <summary>
    /// 上传合成结果，并让 VideoProcessor 把它转成 NV12 写到输出的那个矩形上。
    ///
    /// <para>
    /// 🔥 <b>这一步是"让 VideoProcessor 做色彩转换"的关键</b>：整屏那一趟与这一小块
    /// 走同一个转换器、同一套系数 ⇒ 指针那一块的颜色与周围天然一致。
    /// 换成 CPU 自己转 BGRA→NV12 就会得到一块<b>能看出来</b>的贴片（而且不报错）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>不写 NV12 的关键理由</b>：<c>UpdateSubresource</c> 也做不到这件事 ——
    /// NV12 的色度平面在 D3D11 的纹理坐标里没有独立地址空间（<c>Desc.Height</c> 只有图像高度），
    /// 用 <c>D3D11_BOX</c> 表达不了"我要写色度那一段"。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>上传目标必须是「画面尺寸」的纹理（M5.4-b 第 10 步修的缺陷）</b>：
    /// 早先这里建的是一块 <b>region 尺寸</b>的小纹理，而 VideoProcessor 在创建时声明的是
    /// <b>输入＝整屏</b>（<c>VideoProcessorContentDesc.For(width, height, …)</c>，且本仓<b>从不</b>调
    /// <c>VideoProcessorSetStreamFormat</c> 去改它）⇒ <c>VideoProcessorBlt</c> 回 <c>S_OK</c>，
    /// 但输出是<b>未定义</b>的：实测「矩形内亮度变化 56.5、<b>矩形外 183.00</b>」——
    /// 指针确实写进去了，可<b>整屏都被写坏</b>，观看端看到的就是"没有鼠标"。
    /// </para>
    ///
    /// <para>
    /// 🔥 修法是让<b>尺寸恒等</b>：纹理建成整屏，用 <c>D3D11_BOX</c> 把这一小块直接写到它在画面上的
    /// 位置，再让那两趟矩形都取同一块 region（1:1，不缩放）。这样「输入尺寸 ≡ 声明尺寸」，
    /// 且只碰<b>已经逐条对数过槽位</b>的那三个调用。代价是一块画面尺寸的 BGRA
    /// （1080p ≈ 8 MB / 4K ≈ 33 MB，每会话一份）。
    /// </para>
    /// </summary>
    private bool TryUploadAndOverlay(
        D3D11Nv12Converter converter, CapturedSurface surface,
        DxgiInterop.Rect sourceRect, DxgiInterop.Rect destinationRect,
        byte[] background, int pitch, out string? failure)
    {
        failure = null;

        var gpu = _gpu!;

        // 🔥 画面尺寸，不是 region 尺寸 —— 理由见上面那一段。缓存键跟着**画面**尺寸走，
        //    所以指针形状变大变小（region 变）时**不会**重建，只有分辨率变了才重建。
        if (!EnsurePointerTexture(surface.Width, surface.Height, out failure)) return false;

        // 写入位置**就是**这块矩形在画面上的位置：源数据是"那一块"，写到"那一个 box"。
        var box = D3D11Interop.D3D11Box.From(sourceRect);

        var update = ComVtable.Bind<D3D11Interop.Delegates.UpdateSubresourceFn>(
            gpu.Context, D3D11Interop.ContextSlots.UpdateSubresource);

        // ⚠️ 项目没开 `AllowUnsafeBlocks`，所以这里用 pin 而不是 `fixed`。
        var handle = GCHandle.Alloc(background, GCHandleType.Pinned);

        try
        {
            update(gpu.Context, _pointerTexture, 0, ref box, handle.AddrOfPinnedObject(), (uint)pitch, 0);
        }
        finally
        {
            handle.Free();
        }

        // ⚠️ 源矩形**也**是 region：输入纹理是整屏尺寸的，指针已经写在 region 的位置上，
        //    所以这是一趟 1:1 的搬运 —— 源与目标同尺寸，VideoProcessor 不做任何缩放。
        //    🔥 但**目标矩形要减去这一块的原点**（只编一块时输出坐标系是"块内"）：
        //    两者混用会让指针被画到块外 ⇒ 输出里那块是黑的，而 Blt 回 S_OK。
        return converter.TryOverlay(_pointerTexture, sourceRect, destinationRect, out failure);
    }

    private bool EnsurePointerStaging(int width, int height, out string? failure)
    {
        failure = null;

        if (_pointerStaging != IntPtr.Zero
            && _pointerStagingWidth == width
            && _pointerStagingHeight == height)
        {
            return true;
        }

        ReleasePointerStaging();

        var gpu = _gpu!;

        var desc = D3D11Interop.Texture2DDesc.Staging(
            (uint)width, (uint)height, DxgiInterop.FormatB8G8R8A8Unorm);

        var create = ComVtable.Bind<D3D11Interop.Delegates.CreateTexture2DFn>(
            gpu.Device, D3D11Interop.DeviceSlots.CreateTexture2D);

        var hr = create(gpu.Device, ref desc, IntPtr.Zero, out _pointerStaging);

        if (!ComVtable.Succeeded(hr))
        {
            _pointerStaging = IntPtr.Zero;

            failure = $"建指针读回暂存失败（{ComVtable.Describe(hr)}）—— {width}×{height}。";

            return false;
        }

        _pointerStagingWidth = width;
        _pointerStagingHeight = height;

        return true;
    }

    private void ReleasePointerStaging()
    {
        ComVtable.ReleasePointer(ref _pointerStaging);

        _pointerStagingWidth = 0;
        _pointerStagingHeight = 0;
    }

    /// <summary>
    /// 建（或复用）指针上传用的那块 BGRA 纹理。
    ///
    /// <para>
    /// 🔴 <b>尺寸收的是「整屏」，不是"指针那一小块"</b>：VideoProcessor 建的时候声明的输入
    /// 就是整屏，输入纹理必须与它同尺寸 —— 否则 <c>Blt</c> 回 <c>S_OK</c> 而输出未定义
    /// （M5.4-b 第 10 步那个"整屏被写坏"的缺陷，详见 <see cref="TryUploadAndOverlay"/>）。
    /// </para>
    /// </summary>
    private bool EnsurePointerTexture(int width, int height, out string? failure)
    {
        failure = null;

        if (_pointerTexture != IntPtr.Zero
            && _pointerTextureWidth == width
            && _pointerTextureHeight == height)
        {
            return true;
        }

        ReleasePointerTexture();

        var gpu = _gpu!;

        // 🔥 `BindFlags = 0`：它是 VideoProcessor 的**输入**，
        //    而官方对输入视图的规定里"完全不带绑定标志"是合法的一种（与桌面副本同一个选择）。
        //    ⚠️ 这块纹理**每次只写 region 那一个 box**（`UpdateSubresource` ＋ `D3D11_BOX`），
        //       其余像素始终是未初始化的垃圾 —— **没关系**：源矩形也只在 region 上，
        //       VideoProcessor 根本读不到外面去。
        var desc = D3D11Interop.Texture2DDesc.Plain(
            (uint)width, (uint)height, DxgiInterop.FormatB8G8R8A8Unorm, bindFlags: 0);

        var create = ComVtable.Bind<D3D11Interop.Delegates.CreateTexture2DFn>(
            gpu.Device, D3D11Interop.DeviceSlots.CreateTexture2D);

        var hr = create(gpu.Device, ref desc, IntPtr.Zero, out _pointerTexture);

        if (!ComVtable.Succeeded(hr))
        {
            _pointerTexture = IntPtr.Zero;

            failure = $"建指针上传纹理失败（{ComVtable.Describe(hr)}）—— {width}×{height}。";

            return false;
        }

        _pointerTextureWidth = width;
        _pointerTextureHeight = height;

        return true;
    }

    private void ReleasePointerTexture()
    {
        ComVtable.ReleasePointer(ref _pointerTexture);

        _pointerTextureWidth = 0;
        _pointerTextureHeight = 0;
    }

    /// <summary>
    /// 最近一次"<b>为什么没画上指针</b>"（画上了、或者这一帧本来就没有指针时为 <c>null</c>）。
    /// <para>
    /// 🔥 没有它，"观看端看不到鼠标"在日志上只剩一句"没动"，查不出来是哪一种原因。
    /// </para>
    /// </summary>
    public string? CursorNote
    {
        get
        {
            lock (_gate) return _cursorNote;
        }
    }

    /// <inheritdoc />
    public string? CursorDiagnostics
    {
        get
        {
            lock (_gate) return CursorDiagnosticsCore();
        }
    }

    /// <summary>
    /// <see cref="CursorDiagnostics"/> 的实现体（<b>无锁</b>：调用方必须已经持有 <c>_gate</c>）。
    ///
    /// <para>
    /// 🔥 <b>抽出来是为了"一份真相"</b>：<see cref="Describe"/> 里"指针合成"那一格
    /// 与宿主日志里那一行必须是同一句话 —— 各写一份，改一处漏一处就会出现
    /// "报告说画上了、日志说没画上"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>"一帧都还没走到"与"走过了、最近一次画上了"都是 <c>_cursorNote is null</c></b>
    /// —— 必须说成两句不同的话。把前者读成后者，正是"以为指针没问题"的成因
    /// （开流头几帧就是这个状态）。
    /// </para>
    /// </summary>
    private string CursorDiagnosticsCore()
    {
        var ever = _cursorComposited + _cursorFailed > 0;

        return $"画上 {_cursorComposited} 帧 / 失败 {_cursorFailed} 帧"
            + (_cursorNote is not null
                ? $"（最近一次没画上：{_cursorNote}）"
                : ever
                    ? "（最近一次：画上了）"
                    : "（⚠️ 一帧都还没走到合成这一步）")
            + (_cursorLastInput is null ? string.Empty : $"｜最近一次的入参：{_cursorLastInput}");
    }

    /// <summary>
    /// "这一次合成用的指针快照"（诊断用，一行）。
    /// <para>
    /// ⚠️ 每个字段都有它的用处，别删：<c>X,Y</c> 与矩形一起回答"位置跟不跟着光标走"；
    /// <c>形状</c> 回答"形状到底拿到没有"；<c>热点</c> 是"位置要不要减掉"的依据；
    /// <c>形状字节</c> 为 0 就说明形状缓冲还是空的（那是"看得见但画不出"的典型）。
    /// </para>
    /// </summary>
    private static string DescribePointerInput(PointerSnapshot p) =>
        $"指针 {p.X},{p.Y} 形状 {p.Width}×{p.ActualHeight} {p.Type}"
        + $"（热点 {p.HotSpotX},{p.HotSpotY}，{(p.Visible ? "可见" : "不可见")}，形状字节 {p.Shape.Length}）";

    /// <summary>"算出来的那个小矩形"（诊断用，一行；画面坐标）。</summary>
    private static string DescribeRegion(DxgiInterop.Rect r) => $"{r.Left},{r.Top}..{r.Right},{r.Bottom}";

    /// <summary>成功把指针合成进画面的帧数。</summary>
    internal long FramesWithCursor
    {
        get
        {
            lock (_gate) return _cursorComposited;
        }
    }

    /// <summary>指针合成失败的帧数（<b>不含</b>"这一帧本来就没有指针"）。</summary>
    internal long CursorFailures
    {
        get
        {
            lock (_gate) return _cursorFailed;
        }
    }

    /// <summary>
    /// 去问编码器要一次参数集（只在出了关键帧之后叫它）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 拿不到<b>是常态、不是故障</b>：H.264 编码器默认按 Annex-B 输出，
    /// 参数集混在码流里，<c>MF_MT_MPEG_SEQUENCE_HEADER</c> 就是空的。
    /// 那时正确的应对是"走不带 description 那条路"，<b>不是</b>编一段空的出来。
    /// </remarks>
    private void TryCaptureParameterSets(WindowsMediaFoundationVideoEncoder encoder)
    {
        if (_parameterSets is not null) return;

        if (_parameterSetAttempts >= ParameterSetAttempts) return;

        _parameterSetAttempts++;

        if (encoder.TryReadSequenceHeader() is { Length: > 0 } sets) _parameterSets = sets;
    }
}
