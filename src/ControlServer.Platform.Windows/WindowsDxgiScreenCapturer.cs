using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// <b>DXGI Desktop Duplication 抓屏</b>（M5.4 的生产路径）。
///
/// <para>
/// 与 <see cref="WindowsGdiScreenCapturer"/> 的关系是<b>互补而不是替代</b>：
/// 本类要求"能建出 D3D11 设备 + 这块屏没人抢"，换来的是一张<b>本来就躺在显存里的纹理</b>
/// （于是色彩转换与编码可以全程不落内存）；GDI 那个对 GPU 零要求，是降级链末端。
/// </para>
///
/// <para>
/// 🔥 <b>它的两个入口对应两条完全不同的数据通路，别混：</b>
/// </para>
/// <list type="bullet">
///   <item><see cref="CaptureSurfaceAsync"/> —— 只给出一个纹理句柄，<b>一个字节的内存拷贝都没有</b>。
///         零拷贝管线（转 NV12 → 硬编）走这条。</item>
///   <item><see cref="CaptureAsync"/> —— 把纹理读回内存（BGRA）。
///         <b>慢得多</b>（4K 每帧约 33 MB 过一遍 PCIe），只配截图 / MJPEG 脚手架这类低频用途。
///         它的实现是复用 <see cref="TryReadBackBgra"/>。</item>
/// </list>
///
/// <para>
/// ⚠️ <b>线程亲和</b>：一个实例<b>不是</b>线程安全的，而且 <c>AcquireNextFrame</c> /
/// <c>DuplicateOutput</c> 最好始终在<b>同一个线程</b>上（DDA 内部有该线程的状态）。
/// 内部用一把锁把并发的错误用法变成"串行"，但<b>不解决线程跳变</b> ——
/// 拿它去 <c>Task.Run</c> 里并发跑，症状是随机的 <c>ACCESS_LOST</c>。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDxgiScreenCapturer : IScreenCapturer, IDisposable
{
    /// <summary>虚拟桌面（所有显示器拼成的整块桌面）的屏 ID。见 <see cref="WindowsScreenEnumerator"/>。</summary>
    public const string VirtualDesktopId = WindowsScreenEnumerator.VirtualDesktopId;

    /// <summary>
    /// 一次 <c>AcquireNextFrame</c> 最多等多久。
    /// <para>
    /// 🔥 <b>它是"静止桌面下的轮询间隔"，不是帧率上限</b>：画面一变就立刻返回。
    /// 调到 0 会让空闲时变成忙等（CPU 白烧），调太大则"画面刚变"到"我们发现"之间白等。
    /// </para>
    /// </summary>
    private const uint DefaultAcquireTimeoutMs = 15;

    /// <summary>脏区一次最多取多少个矩形（超过就只取前这么多 —— 那意味着整屏都在动，报"整屏"更诚实）。</summary>
    private const int MaxDirtyRects = 64;

    private readonly int _sessionId;
    private readonly uint _acquireTimeoutMs;
    private readonly int? _forcedAdapterIndex;
    private readonly object _gate = new();

    // ── 当前绑定的屏（换屏 ⇒ 连设备一起重建，见 D3D11GpuContext 的注释）──

    private D3D11GpuContext? _gpu;
    private string? _boundScreenId;
    private IntPtr _duplication;
    private OutduplDiagnostics _duplInfo = OutduplDiagnostics.Empty;

    private bool _frameHeld;
    private IntPtr _currentTexture;      // 借来的（我们自己 QI 出来的那份引用），ReleaseFrame 时放掉
    private DxgiInterop.OutduplFrameInfo _frameInfo;

    // ── 桌面副本（M5.4-b 指针合成的前提）──

    /// <summary>
    /// <b>桌面内容的副本</b>（BGRA，与画面同尺寸）。
    ///
    /// <para>
    /// 🔥 <b>为什么非要有它</b>：DDA 只在"桌面真的变了"时才填那张纹理
    /// （<c>AccumulatedFrames == 0</c> 时纹理内容是<b>未定义</b>的，本机实测为全 0）。
    /// 而"鼠标单独移动"<b>不算桌面更新</b> —— 可在指针合成这条路上，
    /// 那正是我们必须发出一帧的时刻（不发 ⇒ 观看端的指针不动）。
    /// ⇒ 手上必须留一份"上一次有效的桌面"，纯鼠标帧才有的可发。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 顺带解决第二件事：DDA 那张纹理在 <c>ReleaseFrame</c> 之后随时可能被复用。
    /// 副本的生命周期归我们自己 ⇒ 交出去的 <see cref="CapturedSurface.TextureHandle"/>
    /// 指向的是它，不再是那张借来的纹理。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 代价：每次<b>有桌面更新</b>时多一次显存内全屏拷贝（4K 约 33 MB，不落内存、
    /// 不过 PCIe，0.1 ms 量级）。"只拷脏区"是后续优化（脏区不可信时仍要退回全拷），见方案 §7。
    /// </para>
    /// </summary>
    private IntPtr _desktopCopy;

    private uint _desktopCopyWidth;
    private uint _desktopCopyHeight;

    /// <summary>副本<b>至少被完整填过一次</b>。在那之前，任何帧都不能交出去。</summary>
    private bool _desktopCopyFilled;

    // ── 指针（M5.4-b 第 5 步）──

    /// <summary>
    /// 指针形状的原始字节缓冲（<c>GetFramePointerShape</c> 往里写），<b>复用</b>。
    /// <para>随形状大小原地扩过，不随帧释放 —— 每帧重分配是白烧。</para>
    /// </summary>
    private IntPtr _pointerShapeBuffer;

    private int _pointerShapeCapacity;

    /// <summary>当前有效的指针快照（形状 ＋ 位置）。形状没变时沿用上一份。</summary>
    private PointerSnapshot _pointer = PointerSnapshot.None;

    /// <summary>上一帧 DDA 报的 <c>LastMouseUpdateTime</c>（用来判"本帧是不是真有鼠标事件"）。</summary>
    private long _lastMouseUpdateTime;

    /// <summary>
    /// <b>读回路径</b>用的上一帧缓存。
    /// <para>
    /// 🔥 它只为 <see cref="CaptureAsync"/> 而存在：那个方法的返回类型不可为空，
    /// 而 DXGI 在桌面静止时给不出新帧。见该方法的注释。
    /// </para>
    /// <para>⚠️ 换屏 / 重建 duplication 时必须清掉 —— 否则会把<b>另一块屏</b>的画面当成这一块的。</para>
    /// </summary>
    private CapturedFrame? _lastFrame;

    // ── 读回用的暂存纹理（随宽高/格式变化重建）──

    private IntPtr _staging;
    private uint _stagingWidth;
    private uint _stagingHeight;
    private int _stagingFormat = -1;

    /// <param name="sessionId">
    /// 本进程所在会话号（托盘进程 = 用户会话）。原样填进 <see cref="ScreenDescriptor.SessionId"/>。
    /// </param>
    /// <param name="acquireTimeout">
    /// 覆盖默认的抓取超时。<c>null</c> = <see cref="DefaultAcquireTimeoutMs"/>。
    /// </param>
    /// <param name="adapterIndex">
    /// 🔥 <b>只给诊断用</b>：强制用第 N 块 DXGI 适配器节点（<c>null</c> = 按 output 名反查，正常路径）。
    ///
    /// <para>
    /// ⚠️ 它存在的唯一理由是排查「<b>抓到全黑帧却一个错都不报</b>」：按 output 名反查时，
    /// 双显卡笔记本上<b>两块适配器可能都挂着同名的 <c>\\.\DISPLAY1</c></b>，
    /// 于是选中"先枚举到的那一块"，而它未必是真正在扫描输出的那一块。
    /// 有了这个参数，诊断代码可以把每一块都试一遍、看哪块不是黑的。
    /// </para>
    /// <para><b>生产路径不要用它</b> —— 猜适配器序号正是本方案明令禁止的做法。</para>
    /// </param>
    public WindowsDxgiScreenCapturer(int sessionId, TimeSpan? acquireTimeout = null, int? adapterIndex = null)
    {
        _sessionId = sessionId;
        _forcedAdapterIndex = adapterIndex;

        // 用 TimeSpan 表达"我要等多久"更像人话，转成 ms 时钳一下：
        // 负数会被 DXGI 当成 uint.MaxValue（≈49 天），那是"永远等下去"。
        _acquireTimeoutMs = acquireTimeout is { } t
            ? (uint)Math.Clamp(t.TotalMilliseconds, 0, 5000)
            : DefaultAcquireTimeoutMs;
    }

    /// <summary>当前绑定的适配器型号名（还没绑定时是 <c>null</c>）。诊断与能力报告用。</summary>
    public string? AdapterName => _gpu?.AdapterName;

    /// <summary>当前绑定的适配器节点序号（<b>它是按 output 名反查出来的结果，不是输入</b>）。</summary>
    public int AdapterIndex => _gpu?.AdapterIndex ?? -1;

    /// <summary>
    /// 当前绑定的 D3D11 设备上下文（<b>平台层内部用</b>）。
    ///
    /// <para>
    /// 🔥 <b>它为什么必须能被取出来</b>：色彩转换与硬件编码<b>必须建立在同一个 D3D11 设备上</b>，
    /// 否则两条链都会静默退化 —— 输入视图与设备不同源会失败，而
    /// <c>MFCreateDXGISurfaceBuffer</c> 造出的 buffer 只有在编码器认得那个设备时才走显存。
    /// 把"同一个设备"做成<b>能取出来的对象</b>，比写成两处注释可靠（见 <see cref="D3D11GpuContext"/>）。
    /// </para>
    /// <para><c>null</c> = 还没绑定任何屏（第一次抓取时才建）。</para>
    /// </summary>
    internal D3D11GpuContext? GpuContext
    {
        get
        {
            lock (_gate)
            {
                return _gpu;
            }
        }
    }

    /// <summary>
    /// 最近一次抓取的<b>结局</b>。
    ///
    /// <para>
    /// 🔥 <b>为什么要把这张表拉出来</b>：对调用方而言
    /// <see cref="TryCaptureSurface"/> 只有「拿到一帧」与「没拿到」两种结果，
    /// 但对<b>排障</b>而言"没拿到"至少有三种完全不同的原因，处置也完全不同：
    /// </para>
    /// <list type="bullet">
    ///   <item><see cref="AcquireOutcome.DesktopUpdated"/> —— 正常拿到一帧（唯一能编码的那种）；</item>
    ///   <item><see cref="AcquireOutcome.TimedOut"/> —— 等了一整段超时，桌面<b>确实没动</b>。
    ///         这不是故障，但如果它<b>一直</b>是这样，说明真的一直没动（例如锁屏）；</item>
    ///   <item><see cref="AcquireOutcome.NoDesktopUpdate"/> —— <b>拿到了帧，但 DDA 没攒到桌面图</b>
    ///         （<c>AccumulatedFrames == 0</c>）。上一版就是<b>栽在这里</b>：
    ///         全新 duplication 的第一帧恒为这个状态，而它的纹理内容是全 0 ——
    ///         看起来与"被控端真的黑屏"一模一样。区分它，才说得清"为什么刚连上是黑的"；</item>
    ///   <item><see cref="AcquireOutcome.Failed"/> —— 真失败（配 <c>failure</c> 那句话一起看）。</item>
    /// </list>
    /// </summary>
    public AcquireOutcome LastAcquireOutcome { get; private set; } = AcquireOutcome.None;

    /// <summary>一次抓取的结局。见 <see cref="LastAcquireOutcome"/>。</summary>
    public enum AcquireOutcome
    {
        /// <summary>还一次都没抓过。</summary>
        None,

        /// <summary>拿到了一帧<b>有桌面内容</b>的画面。</summary>
        DesktopUpdated,

        /// <summary>
        /// 拿到了一帧 —— 但这一帧只有<b>指针</b>变了（桌面本身没变）。
        /// <para>
        /// 🔥 它与 <see cref="DesktopUpdated"/> 是<b>两个不同的结局</b>，不要合并：
        /// 这条路上画面内容来自<b>上一帧的副本</b>，只有指针是新的。
        /// 合并之后，日志上"帧率很高"与"画面其实一直没动"就再也分不开了。
        /// </para>
        /// </summary>
        PointerUpdated,

        /// <summary>等到超时也没有桌面更新（画面确实没变）。</summary>
        TimedOut,

        /// <summary>拿到了帧，但 DDA 没有攒到桌面图 —— 纹理内容未定义，<b>不可用</b>。</summary>
        NoDesktopUpdate,

        /// <summary>失败了（原因在 <c>failure</c> 里）。</summary>
        Failed,
    }

    /// <summary>
    /// DDA 自己那份描述 + 本帧的计数（诊断用）。
    ///
    /// <para>
    /// 🔥 它要与 <see cref="LastPointerPosition"/> 一起看，才能把
    /// 「<b>压根没抓到</b>」与「<b>抓到了但内容不对</b>」分开 —— 这两件事的处置完全相反，
    /// 而在只看到一句"帧率为 0"的日志上，它们长得一模一样。
    /// </para>
    /// </summary>
    public OutduplDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return _duplInfo with
                {
                    AccumulatedFrames = _frameInfo.AccumulatedFrames,
                    TotalMetadataBufferSize = _frameInfo.TotalMetadataBufferSize,
                    PointerShapeBufferSize = _frameInfo.PointerShapeBufferSize,
                    ProtectedContentMaskedOut = _frameInfo.ProtectedContentMaskedOut != 0,
                    LastPresentTime = _frameInfo.LastPresentTime,
                };
            }
        }
    }

    /// <summary>DDA 的静态描述 + 最近一帧的计数。</summary>
    public sealed record OutduplDiagnostics(
        uint ModeWidth,
        uint ModeHeight,
        int ModeFormat,
        string Rotation,
        bool DesktopImageInSystemMemory,
        uint AccumulatedFrames,
        uint TotalMetadataBufferSize,
        uint PointerShapeBufferSize,
        bool ProtectedContentMaskedOut,
        long LastPresentTime)
    {
        internal static readonly OutduplDiagnostics Empty = new(0, 0, 0, "未绑定", false, 0, 0, 0, false, 0);
    }

    /// <inheritdoc />
    /// <remarks>与 GDI 后端<b>共用同一份</b>枚举 —— 两个后端必须看得见同样的屏列表。</remarks>
    public IReadOnlyList<ScreenDescriptor> EnumerateScreens() => WindowsScreenEnumerator.Enumerate(_sessionId);

    // ═══════════════════════════════════════════════════════════
    // 零拷贝入口：只给纹理
    // ═══════════════════════════════════════════════════════════

    /// <inheritdoc />
    /// <remarks>
    /// ⚠️ <b>会阻塞调用线程</b>最长 <c>acquireTimeout</c>（默认 15 ms）：
    /// DDA 本身就是一次带超时的等待，塞进线程池只会让"谁在等"变得看不出来。
    /// 推流循环是个专用线程，直接等即可。
    /// </remarks>
    public Task<CapturedSurface?> CaptureSurfaceAsync(
        string screenId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var surface = TryCaptureSurface(screenId, cancellationToken, out var failure);

        if (failure is not null) throw new InvalidOperationException(failure);

        return Task.FromResult(surface);
    }

    /// <summary>
    /// 同步抓一帧（推流循环与自检工具直接用这个，不绕 <see cref="Task"/>）。
    /// </summary>
    /// <returns>
    /// <c>null</c> = <b>画面没有变化</b>（这不是失败）。见
    /// <see cref="IScreenCapturer.CaptureSurfaceAsync"/> 对这条约定的说明。
    /// </returns>
    /// <param name="failure">
    /// 失败原因（能拿到的都是给人看的一句话）；成功与"画面没变"时都是 <c>null</c>。
    /// </param>
    public CapturedSurface? TryCaptureSurface(
        string? screenId, CancellationToken cancellationToken, out string? failure)
    {
        failure = null;

        lock (_gate)
        {
            _ = cancellationToken;

            if (!EnsureBound(screenId, out failure))
            {
                LastAcquireOutcome = AcquireOutcome.Failed;

                return null;
            }

            if (!TryAcquire(out var changed, out failure))
            {
                LastAcquireOutcome = AcquireOutcome.Failed;

                return null;
            }

            if (!changed) return null;

            // 🔥 尺寸/格式这件事在 `TryAcquire` 里**已经校验过一遍**（副本就是从那张纹理拷出来的）。
            //    这里再确认一次，是因为"要交出去的东西"是副本本身 ——
            //    以**副本自己的描述**为准，而不是以一张我们已经不持有的纹理为准。
            if (!TryDescribeTexture(_desktopCopy, "桌面副本", out var width, out var height, out var format, out failure))
            {
                LastAcquireOutcome = AcquireOutcome.Failed;

                return null;
            }

            if (format != DxgiInterop.FormatB8G8R8A8Unorm)
            {
                failure = $"桌面副本现在的格式是 {DescribeFormat(format)}，不是 32 位 BGRA。";

                LastAcquireOutcome = AcquireOutcome.Failed;

                return null;
            }

            var dirtyRects = TryReadDirtyRects();

            return new CapturedSurface(
                Width: (int)width,
                Height: (int)height,

                // 🔥 交出去的是**副本**，不是 DDA 那张借来的纹理：
                //    后者在 ReleaseFrame 之后随时可能被复用，而副本的生命周期归我们。
                TextureHandle: _desktopCopy,
                Format: SurfacePixelFormat.Bgra8,

                // 🔥 仍然是 false，而且**依旧是如实上报** —— 副本里确实没有指针。
                //    "把指针合成进画面"发生在**编码那一层**（`WindowsGpuFrameEncoder`）：
                //    它拿 `Pointer` 快照在 CPU 上合成好，再交给 VideoProcessor 转 NV12 写回。
                //    ⇒ 这个字段说的是"这张纹理里有没有"，不是"最终画面上有没有"。
                IsCursorIncluded: false,
                CapturedAt: DateTimeOffset.UtcNow,
                DirtyRects: dirtyRects);
        }
    }

    /// <summary>
    /// 上一帧里鼠标指针的位置（DDA 单独给的那份）。
    /// <para>
    /// ⚠️ <c>Visible == 0</c> 也可能是"这帧根本没有鼠标更新"——<c>LastMouseUpdateTime</c> 为 0 时
    /// 位置字段是<b>上一次</b>的值，别拿它当"现在在这"。
    /// </para>
    /// </summary>
    public (int X, int Y, bool Visible) LastPointerPosition
    {
        get
        {
            lock (_gate)
            {
                var p = _frameInfo.PointerPosition;

                return (p.X, p.Y, p.Visible != 0);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 内存入口：读回 BGRA
    // ═══════════════════════════════════════════════════════════

    /// <inheritdoc />
    /// <remarks>
    /// 🔥 走的是「抓一帧 → 拷进暂存纹理 → <c>Map</c> 读回」。<b>它是低频路径</b>：
    /// 4K 一帧 BGRA 是 33 MB，每帧过一遍 PCIe 就是几毫秒起步。生产路径见 <see cref="CaptureSurfaceAsync"/>。
    ///
    /// <para>
    /// ⚠️ <b>它与 <see cref="CaptureSurfaceAsync"/> 的语义有一处<b>刻意</b>的不同</b>：
    /// 后者在"桌面没动"时给 <c>null</c>（因为零拷贝管线由调用方自己决定丢不丢帧），
    /// 而本方法的返回类型 <c>CapturedFrame</c> <b>不可为空</b>，且 GDI 那一路是"每次都重新 BitBlt"，
    /// 永远给得出一帧。⇒ 为了换后端时调用方行为不变，这里<b>没有新帧就交上一次那一帧</b>
    /// （<c>CapturedAt</c> 保持它自己的时刻，不假装是现在）。
    /// </para>
    /// <para>
    /// 连一次都没成功过时（例如刚连上一块完全静止的桌面），会<b>有界等待</b>一小段
    /// （见 <see cref="FirstFrameWait"/>）；仍然拿不到就抛，并把原因说清楚 ——
    /// 因为"返回一张全 0 的图"正是本批踩过的那个坑（§3.3 第 4 条）。
    /// </para>
    /// </remarks>
    public Task<CapturedFrame> CaptureAsync(string screenId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!EnsureBound(screenId, out var bindFailure)) throw new InvalidOperationException(bindFailure);

            var deadline = DateTime.UtcNow + FirstFrameWait;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryAcquire(out var changed, out var acquireFailure))
                {
                    throw new InvalidOperationException(acquireFailure);
                }

                if (changed)
                {
                    if (!TryReadBackBgra(out var buffer, out var width, out var height, out var readFailure))
                    {
                        throw new InvalidOperationException(readFailure);
                    }

                    _lastFrame = new CapturedFrame(
                        Width: width,
                        Height: height,
                        Stride: width * 4,
                        Buffer: buffer,

                        // ⚠️ 与 CapturedSurface 同一个事实：DDA 的帧里没有指针。
                        //    这里如果照抄 GDI 的 true，观看端会以为已经有指针了。
                        IsCursorIncluded: false,

                        CapturedAt: DateTimeOffset.UtcNow);

                    return Task.FromResult(_lastFrame);
                }

                // 没拿到新帧：交上一次那一帧（时间戳仍是他自己的那一刻，不篡改）。
                if (_lastFrame is not null) return Task.FromResult(_lastFrame);

                if (DateTime.UtcNow >= deadline)
                {
                    throw new InvalidOperationException(
                        $"等了 {FirstFrameWait.TotalMilliseconds:F0} ms 也没能拿到第一帧。"
                        + "原因是这块屏当前**完全静止**：DXGI Desktop Duplication 只在桌面**变化**时才会"
                        + "投递可用的图像（见 docs/M5.4-实施方案.md §3.3 第 4 条）。"
                        + "对「截图」这种用途，等一下再调一次即可；对「实时推流」请改用 "
                        + "CaptureSurfaceAsync 的轮询语义。");
                }
            }
        }
    }

    /// <summary>
    /// 首次抓帧时最多等多久。
    /// <para>
    /// 🔥 它只影响「<b>从来没有成功过</b>」的那一次。挑 1.5 s 是因为：
    /// 短于它，静止桌面上很容易一次都拿不到；长于它，调用方（截图按钮）会觉得卡住。
    /// </para>
    /// </summary>
    public static readonly TimeSpan FirstFrameWait = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// 把<b>当前已抓到的那一帧</b>读回内存（BGRA，行紧排）。
    /// <para>自检工具用它：先 <see cref="TryCaptureSurface"/> 拿到纹理，再读回来存成 JPEG 肉眼看。</para>
    /// </summary>
    public bool TryReadBackBgra(out byte[] buffer, out int width, out int height, out string? failure)
    {
        buffer = Array.Empty<byte>();
        width = 0;
        height = 0;

        lock (_gate)
        {
            // 🔥 读回的是**桌面副本**，不是 DDA 那张借来的纹理 ——
            //    借来的那张在 ReleaseFrame 之后就失效了，而本方法常常在那之后才被调用。
            if (!_desktopCopyFilled || _desktopCopy == IntPtr.Zero)
            {
                failure = "当前没有已抓到的帧可读（先成功调一次 TryCaptureSurface）。";

                return false;
            }

            if (!TryDescribeTexture(_desktopCopy, "桌面副本", out var w, out var h, out var format, out failure))
            {
                return false;
            }

            if (format != DxgiInterop.FormatB8G8R8A8Unorm)
            {
                failure = $"桌面是 {DescribeFormat(format)} 格式，读回只实现了 BGRA。";

                return false;
            }

            if (!EnsureStaging(w, h, format, out failure)) return false;

            return CopyAndReadStaging((int)w, (int)h, out buffer, out width, out height, out failure);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 绑定与抓取
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 最近一次<b>建 duplication</b> 失败的 HRESULT（<c>0</c> = 没失败过 / 上次成功）。
    ///
    /// <para>
    /// 🔥 <b>为什么把码暴露出来，而不是只给一句人话</b>：自检用例要判断
    /// "这一次失败是环境问题，还是真的回归"。按 <c>failure</c> 的<b>文案</b>分类是这一步
    /// 最容易犯的错 —— 文案一改，分类就静默失效（本仓已经因为"读 LastFailure 文案"
    /// 翻过一次车）。码是稳定的，文案可以随便改。
    /// </para>
    /// </summary>
    public int LastFailureHResult { get; private set; }

    /// <summary>
    /// <b>自检探针</b>：只做"能不能在这块屏上建 duplication"这一步，不改任何画面状态。
    /// <para>
    /// 它存在的理由：三条 DXGI 冒烟用例需要<b>在断言之前</b>知道"这台机器现在允许抓屏吗"。
    /// 没有这一步时，环境不满足（别的录屏工具占着 / 桌面锁着）会让用例直接红，
    /// 而现场读到的却是"DXGI 抓屏坏了"—— 一条与事实相反的结论。
    /// </para>
    /// <para>
    /// ⚠️ 它是<b>幂等</b>的：已经绑好的屏会直接返回 true，不会重建。
    /// </para>
    /// </summary>
    /// <param name="screenId"><c>null</c> = 主屏。</param>
    /// <returns><c>false</c> 时 <paramref name="failure"/> 是人话，码在 <see cref="LastFailureHResult"/>。</returns>
    public bool TryInitialize(string? screenId, out string? failure)
    {
        lock (_gate) return EnsureBound(screenId, out failure);
    }

    private bool EnsureBound(string? screenId, out string? failure)
    {
        failure = null;

        // 空 id ⇒ 落到主屏。⚠️ 这里**不能**落到"虚拟桌面"：
        //    DDA 一次只能抓**一个 output**，而虚拟桌面是多块屏拼出来的。
        //    硬抓一块再声称是"全部" ⇒ 观看端看到半个画面，尺寸还是错的。
        if (string.IsNullOrWhiteSpace(screenId))
        {
            var screens = WindowsScreenEnumerator.Enumerate(_sessionId);

            var primary = screens.FirstOrDefault(s => s.IsPrimary)
                ?? screens.FirstOrDefault(s => !s.IsVirtualDesktop);

            if (primary is null)
            {
                failure = "枚举不到任何可抓的显示器 —— 说明**这个进程所在的会话没有桌面**"
                    + "（典型原因：它跑在 Session 0 里，或这台机器没人登录）。抓屏必须在用户会话中进行。";

                return false;
            }

            screenId = primary.Id;
        }

        if (string.Equals(screenId, VirtualDesktopId, StringComparison.OrdinalIgnoreCase))
        {
            failure = "DXGI 抓屏一次只能抓**一块**显示器（DDA 只对单个 output 建 duplication），"
                + "本批还不支持「全部显示器」拼接 —— 那里要做多块屏抓取 + 拼图，"
                + "属于多屏那一批。请先选一块具体的显示器。";

            return false;
        }

        if (_duplication != IntPtr.Zero && string.Equals(_boundScreenId, screenId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        RebuildDuplication();

        var gpu = D3D11GpuContext.TryCreate(screenId, out failure, _forcedAdapterIndex);

        if (gpu is null) return false;

        _gpu = gpu;

        var duplicate = ComVtable.Bind<DxgiInterop.Delegates.DuplicateOutputFn>(
            gpu.Output, DxgiInterop.Output.DuplicateOutput);

        var hr = duplicate(gpu.Output, gpu.Device, out _duplication);

        if (!ComVtable.Succeeded(hr))
        {
            // 🔥 记下码：上层（自检用例 / 托盘诊断）靠它区分"环境不允许"与"真故障"。
            LastFailureHResult = hr;

            failure = hr switch
            {
                DxgiInterop.ErrorNotCurrentlyAvailable =>
                    $"「{screenId}」现在抓不了：{DxgiInterop.Describe(hr)}。"

                    // 🔥 这条最容易被误判成"代码有 bug"：它的真身是"Windows 同一时刻
                    //    只允许一个进程对一块 output 建 duplication"。被控机上如果还开着
                    //    其它抓屏工具（录屏、投屏助手、另一份本程序），就会一直撞它。
                    + "Windows 同一时刻只允许**一个**进程抓这块屏 ——"
                    + "检查这台机器上是否已经跑着录屏 / 投屏 / 另一份本程序。",

                // 🔥🔥 这一条以前落在兜底分支，只留一句"DuplicateOutput 失败"。
                //    现场看到那句话只会去查代码，而它的三个真身全在环境侧（见常量注释）。
                DxgiInterop.ErrorAccessDenied =>
                    $"「{screenId}」抓不了：系统拒绝访问（{DxgiInterop.Describe(hr)}）。"
                    + "这不是程序故障，三种可能按常见程度排列："
                    + "① 这台机器上已经有别的程序在抓屏（录屏 / 投屏 / 另一份本程序）—— 关掉它；"
                    + "② 当前会话不是可交互桌面（服务进程跑在 Session 0、远程桌面已断开、桌面被锁）"
                    + "—— 确认有人登录且桌面没锁；"
                    + "③ 屏幕上正盖着一个 UAC 提升提示 —— 处置完它，几秒后自己恢复。",

                DxgiInterop.ErrorUnsupported =>
                    $"「{screenId}」所在的显卡/输出组合不支持 Desktop Duplication（{DxgiInterop.Describe(hr)}）。"
                    + "这台机器要走 GDI 降级路径。",

                _ => $"DuplicateOutput 失败（{DxgiInterop.Describe(hr)}）—— 屏「{screenId}」。",
            };

            RebuildDuplication();

            return false;
        }

        // 成功 ⇒ 清掉上一次的失败码，否则"后来成功了"这件事在自检那边看不出来。
        LastFailureHResult = 0;

        ReadDuplicationInfo();
        _boundScreenId = screenId;

        return true;
    }

    /// <summary>
    /// 记下 duplication 自己那份描述（诊断用）。
    /// <para>
    /// 🔥 <b>它才是"你应该期望多大一张图"的权威值</b> —— 与
    /// <see cref="ScreenDescriptor"/> 里那个（来自 <c>EnumDisplayMonitors</c>）是两套独立来源。
    /// 两者不一致就说明"枚举之后分辨率变了"，这种事必须能看出来。
    /// </para>
    /// </summary>
    private void ReadDuplicationInfo()
    {
        var getDesc = ComVtable.Bind<DxgiInterop.Delegates.GetDuplicationDescFn>(
            _duplication, DxgiInterop.Duplication.GetDesc);

        if (!ComVtable.Succeeded(getDesc(_duplication, out var desc)))
        {
            _duplInfo = OutduplDiagnostics.Empty;

            return;
        }

        _duplInfo = new OutduplDiagnostics(
            ModeWidth: desc.ModeDesc.Width,
            ModeHeight: desc.ModeDesc.Height,
            ModeFormat: desc.ModeDesc.Format,

            // ⚠️ 0 是 UNSPECIFIED、1 才是 IDENTITY。旋转不是 identity 时，
            //    DDA 给的图是**旋转前**的，尺寸与所见不符 —— 本批直接说"不支持"，不猜。
            Rotation: desc.Rotation == DxgiInterop.ModeRotationIdentity ? "identity" : $"rotation={desc.Rotation}",
            DesktopImageInSystemMemory: desc.DesktopImageInSystemMemory != 0,
            AccumulatedFrames: 0,
            TotalMetadataBufferSize: 0,
            PointerShapeBufferSize: 0,
            ProtectedContentMaskedOut: false,
            LastPresentTime: 0);
    }

    /// <summary>放掉当前绑定的一切（duplication + 设备），下次用的时候重建。</summary>
    private void RebuildDuplication()
    {
        ReleaseHeldFrame();

        ComVtable.ReleasePointer(ref _duplication);

        // ⚠️ 顺序要紧：这两张纹理都建在那个设备上，**必须先释放它们再放设备**。
        ReleaseStaging();
        ReleaseDesktopCopy();

        _gpu?.Dispose();
        _gpu = null;
        _boundScreenId = null;
        _duplInfo = OutduplDiagnostics.Empty;

        // 换屏 / 重建之后，上一帧属于「之前那块屏（或之前的设备）」，不能再用。
        _lastFrame = null;

        // 指针也一样：形状是"那块屏的桌面"给的，位置更是。
        _pointer = PointerSnapshot.None;
        _lastMouseUpdateTime = 0;
        PointerNote = null;
    }

    private bool TryAcquire(out bool changed, out string? failure)
    {
        changed = false;
        failure = null;

        // ⚠️ DDA 的硬性配对：上一次 Acquire 的结果**必须先 ReleaseFrame**，否则这次一定失败。
        //    本类把"持有"当作默认状态（调用方要用那张纹理），所以在下一次抓之前才放。
        ReleaseHeldFrame();

        var acquire = ComVtable.Bind<DxgiInterop.Delegates.AcquireNextFrameFn>(
            _duplication, DxgiInterop.Duplication.AcquireNextFrame);

        var hr = acquire(_duplication, _acquireTimeoutMs, out _frameInfo, out var desktopResource);

        switch (hr)
        {
            case DxgiInterop.S_OK:
                break;

            case DxgiInterop.ErrorWaitTimeout:
                // 画面没变 —— 正常，不是失败，也不用 ReleaseFrame。
                LastAcquireOutcome = AcquireOutcome.TimedOut;

                return true;

            case DxgiInterop.ErrorAccessLost:
                // 切 UAC 安全桌面 / 全屏独占 / 换分辨率 / 锁屏 ⇒ 这次抓不到，下次重建。
                // ⚠️ 不在这里重建：重建要动设备和 duplication，而调用方此刻可能在别的屏上。
                RebuildDuplication();

                failure = "DXGI_ERROR_ACCESS_LOST —— 桌面刚刚被切走（UAC 提示 / 全屏独占应用 / "
                    + "锁屏 / 分辨率变化都会这样）。下次抓取会自动重建。";

                return false;

            default:
                failure = $"AcquireNextFrame 失败（{DxgiInterop.Describe(hr)}）。";

                return false;
        }

        if (desktopResource == IntPtr.Zero)
        {
            failure = "AcquireNextFrame 报了成功，却没给桌面图像 —— 这不该发生。";

            return false;
        }

        // ⚠️ 给回来的是 IDXGIResource*，要 QI 成 ID3D11Texture2D 才能用。
        //    QI 成功后**必须**把 IDXGIResource 的引用放掉（那是额外的引用，不是同一个）。
        try
        {
            var iid = DxgiInterop.IID_ID3D11Texture2D;

            var qi = ComVtable.QueryInterfaceRaw(desktopResource, ref iid, out var texture);

            if (!ComVtable.Succeeded(qi))
            {
                failure = $"拿到的桌面图像不是 ID3D11Texture2D（{ComVtable.Describe(qi)}）。";

                return false;
            }

            _currentTexture = texture;
        }
        finally
        {
            ComVtable.ReleasePointer(ref desktopResource);
        }

        _frameHeld = true;

        // ────────────────────────────────────────────────────────────────
        // 🔥 本文件里最贵的一条实测结论：**AccumulatedFrames == 0 的那张纹理不能用**。
        //    （M5.4-b 之后它的**表述变了、含义没变**：判据从"这一帧的计数"变成
        //      "副本有没有被填过"，理由见下面 ③ 那一段。）
        //
        // DDA 是「增量投递」的：它只在你取帧的间隔里**攒到过桌面更新**时，才会把新的桌面图
        // 填进那块纹理。攒到 0 帧时它仍然返回 S_OK（因为**指针**可能有更新），
        // 但返回的纹理内容**是未定义的**。
        //
        // 本机实测（2160×1440 / Intel UHD 620）：
        //   第 1 帧  AccumulatedFrames=0  meta=0B   pointerShape=4096B → 读回来 **全 0**
        //   第 2 帧  AccumulatedFrames=13 meta=16B  pointerShape=0B    → 读回来 99.98% 非黑
        // 也就是说「全新 duplication 的第一帧」恰好就是这个状态 ——
        // 而这正是「刚连上远控就看见一片黑」的成因。
        //
        // 🔥 M5.4-b 把这里**改成了"退回上一帧"**，而且是有理由的：
        //    只有指针动的时候我们必须发出一帧（否则观看端的指针不动），
        //    而那一帧的画面内容就是"上一次的桌面" —— 于是手上必须留一份副本。
        //    判据里**没变**的那部分：**内容没被证明有效过，就不许交出去**
        //    （全新 duplication 的头几帧仍然被拦在 ③ 那一道）。
        //
        // ⚠️ 另一条代价（M5.4-b 之前它是个缺口，现在补上了）：鼠标单独移动**不算**桌面更新。
        //    所以下面额外把 `LastMouseUpdateTime` 也当判据 —— 在指针合成那条路上，
        //    "只有指针变了"正是必须发出一帧的时刻（不发 ⇒ 观看端的指针不动）。
        // ────────────────────────────────────────────────────────────────

        // ── ① 桌面更新 ⇒ 整块拷进副本 ──
        var desktopChanged = _frameInfo.AccumulatedFrames != 0;

        if (desktopChanged)
        {
            if (!TryCopyDesktopToMirror(out failure))
            {
                // 副本可能只被写了一半 —— 标成未填充，宁可少发几帧，
                // 也不发一张"半新半旧"的图（那种图看起来只是有点怪，最难查）。
                _desktopCopyFilled = false;

                return false;
            }

            _desktopCopyFilled = true;
        }

        // ── ② 读指针（形状 ＋ 位置）──
        TryReadPointer();

        // ⚠️ **这里刻意不 ReleaseFrame**：`CopyResource` 只是把命令**提交**进队列，
        //    而 ReleaseFrame 之后 DDA 随时可能重新往那张纹理里写。
        //    把释放留到"下一次抓之前"（本方法开头那次），等于给 GPU 整整一帧的时间跑完拷贝。
        //    ⇒ 这条约定是本类**早就有的**，M5.4-b 的副本没有改变它，只是变得更依赖它。

        // ── ③ 判这一帧要不要发 ──
        if (!_desktopCopyFilled)
        {
            // 全新 duplication 的头几帧：桌面从来没被填过，副本里是空的（本机实测为全 0）。
            // 🔥 这就是「刚连上远控看见一片黑」的那条不变式 —— 一丝都不许放宽。
            LastAcquireOutcome = AcquireOutcome.NoDesktopUpdate;

            return true;
        }

        // 纯鼠标移动也要发一帧：不发的话，观看端的指针就是不动。
        // ⚠️ 两个条件都要（而不是只看 `!= 0`）：DDA 在"本帧没有鼠标更新"时，
        //    既可能是把它清 0，也可能是**保留上一次的值** —— 两种实现都真实存在，
        //    所以这里同时排除"是 0"与"与上一帧相同"这两种"没有新事件"。
        var pointerMoved = _frameInfo.LastMouseUpdateTime != 0
            && _frameInfo.LastMouseUpdateTime != _lastMouseUpdateTime;

        _lastMouseUpdateTime = _frameInfo.LastMouseUpdateTime;

        if (!desktopChanged && !pointerMoved)
        {
            LastAcquireOutcome = AcquireOutcome.NoDesktopUpdate;

            return true;
        }

        changed = true;

        LastAcquireOutcome = desktopChanged ? AcquireOutcome.DesktopUpdated : AcquireOutcome.PointerUpdated;

        return true;
    }

    private void ReleaseHeldFrame()
    {
        ComVtable.ReleasePointer(ref _currentTexture);

        if (!_frameHeld) return;

        _frameHeld = false;

        if (_duplication == IntPtr.Zero) return;

        // 放帧本身不该因为"已经失效"就抛 —— 它是收尾动作，不该把主流程带崩。
        var release = ComVtable.Bind<DxgiInterop.Delegates.ReleaseFrameFn>(
            _duplication, DxgiInterop.Duplication.ReleaseFrame);

        release(_duplication);
    }

    // ═══════════════════════════════════════════════════════════
    // 桌面副本（M5.4-b）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把 DDA 这一帧的桌面内容<b>整块</b>拷进副本。
    ///
    /// <para>
    /// ⚠️ 首版是<b>全屏拷贝</b>。脏区那条路（只拷变化的部分）看着更省，但它的正确性完全押在
    /// <c>GetFrameDirtyRects</c> 上 —— 而本仓已经实测过"元数据缺失时它什么都不给"
    /// （<c>TotalMetadataBufferSize == 0</c>），那时必须退回全拷。
    /// 把这个优化留到 M5.4-c，见方案 §7。
    /// </para>
    /// </summary>
    private bool TryCopyDesktopToMirror(out string? failure)
    {
        if (!TryDescribeTexture(
                _currentTexture, "抓到的桌面纹理", out var width, out var height, out var format, out failure))
        {
            return false;
        }

        // 本批只认 BGRA。⚠️ HDR 开启时 DDA 会给 FP16（R16G16B16A16_FLOAT），
        //    那时"按 BGRA 解释"会得到一堆诡异的颜色 —— 宁可不认。
        if (format != DxgiInterop.FormatB8G8R8A8Unorm)
        {
            failure = $"这块屏现在的桌面格式是 {DescribeFormat(format)}，不是 32 位 BGRA。"
                + "常见原因是<b>开了 HDR</b>（那时 DDA 给的是 FP16）。"
                + "本批只处理 BGRA —— 不要按 BGRA 去解释一张 FP16 的图。";

            return false;
        }

        if (!EnsureDesktopCopy(width, height, out failure)) return false;

        var gpu = _gpu!;

        // ⚠️ `CopyResource` 要求源与目标**同格式、同尺寸、同 mip/数组层级** —— 由 EnsureDesktopCopy 保证。
        var copy = ComVtable.Bind<D3D11Interop.Delegates.CopyResourceFn>(
            gpu.Context, D3D11Interop.ContextSlots.CopyResource);

        copy(gpu.Context, _desktopCopy, _currentTexture);

        return true;
    }

    private bool EnsureDesktopCopy(uint width, uint height, out string? failure)
    {
        failure = null;

        if (_desktopCopy != IntPtr.Zero && _desktopCopyWidth == width && _desktopCopyHeight == height)
        {
            return true;
        }

        ReleaseDesktopCopy();

        var gpu = _gpu;

        if (gpu is null)
        {
            failure = "内部状态错误：设备已经没了。";

            return false;
        }

        // 🔥 `BindFlags = 0`：与 `D3D11Nv12Converter` 那张中转纹理同一个理由 ——
        //    官方对 VideoProcessor **输入视图**的规定里，"完全不带绑定标志"是合法的一种，
        //    而这张副本既要当 `CopyResource` 的目标、又要当 VideoProcessor 的输入。
        var desc = D3D11Interop.Texture2DDesc.Plain(
            width, height, DxgiInterop.FormatB8G8R8A8Unorm, bindFlags: 0);

        var create = ComVtable.Bind<D3D11Interop.Delegates.CreateTexture2DFn>(
            gpu.Device, D3D11Interop.DeviceSlots.CreateTexture2D);

        var hr = create(gpu.Device, ref desc, IntPtr.Zero, out _desktopCopy);

        if (!ComVtable.Succeeded(hr))
        {
            _desktopCopy = IntPtr.Zero;

            failure = $"建桌面副本失败（{ComVtable.Describe(hr)}）—— {width}×{height}。";

            return false;
        }

        _desktopCopyWidth = width;
        _desktopCopyHeight = height;

        return true;
    }

    private void ReleaseDesktopCopy()
    {
        ComVtable.ReleasePointer(ref _desktopCopy);

        _desktopCopyWidth = 0;
        _desktopCopyHeight = 0;

        // 🔥 副本没了 ⇒ "填过"这个事实也跟着没了。留着它会让新 duplication 的头几帧
        //    通过那道闸门，而那时副本里是空的 —— 正是"刚连上看见一片黑"。
        _desktopCopyFilled = false;
    }

    // ═══════════════════════════════════════════════════════════
    // 指针（M5.4-b 第 5 步）
    // ═══════════════════════════════════════════════════════════

    /// <summary>当前有效的指针快照（形状 ＋ 位置）。见 <see cref="PointerSnapshot"/>。</summary>
    internal PointerSnapshot Pointer
    {
        get
        {
            lock (_gate) return _pointer;
        }
    }

    /// <summary>
    /// 最近一次"<b>为什么没拿到指针形状</b>"（成功时为 <c>null</c>）。
    /// <para>
    /// ⚠️ 它<b>不是失败</b>：拿不到形状最坏就是这一帧没有指针，画面本身是好的。
    /// 但没有它，"观看端看不到鼠标"这件事在日志上就只剩一句"指针没动"，查不出来。
    /// </para>
    /// </summary>
    public string? PointerNote { get; private set; }

    /// <summary>
    /// 桌面副本是否<b>至少被完整填过一次</b>。
    /// <para>它是"能交出一帧"的前提（替代了原来"这一帧 <c>AccumulatedFrames &gt; 0</c>"那条不变式）。</para>
    /// </summary>
    public bool HasPopulatedDesktopCopy
    {
        get
        {
            lock (_gate) return _desktopCopyFilled;
        }
    }

    /// <summary>
    /// 读本帧的指针（形状 ＋ 位置），更新 <see cref="Pointer"/>。
    ///
    /// <para>
    /// 🔥 <b>"形状变了"与"位置变了"是两件独立的事</b>：
    /// DDA 只在形状<b>真的变了</b>时才给字节（<c>PointerShapeBufferSize &gt; 0</c>），
    /// 而位置<b>在有鼠标更新的帧上才有效</b>（<c>LastMouseUpdateTime != 0</c>）。
    /// ⇒ 形状靠缓存，位置也在没有更新的帧上<b>沿用上一份</b>（见方法体里的详解）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>本方法不返回失败</b>：读不到指针绝不值得让整条流断掉（与 GDI 那条路的
    /// <c>DrawCursor</c> 同一个取舍）。真出问题写进 <see cref="PointerNote"/>，由上层决定要不要说。
    /// </para>
    /// </summary>
    private void TryReadPointer()
    {
        var position = _frameInfo.PointerPosition;

        // 🔥 **只有真的"这一帧有鼠标更新"时，`PointerPosition` 才是可读的。**
        //
        //    DDA 的规矩：`LastMouseUpdateTime == 0` ⇒ 这一帧**没有鼠标更新**，
        //    `PointerPosition` 字段**不含有效数据**（本机实测回 `Visible=0, X=0, Y=0`）。
        //    ⇒ 这时必须**沿用上一份**快照的位置与可见性。
        //
        //    ⚠️ 原来这里是无条件照抄，于是凡是"屏幕内容在变、而鼠标没动"的帧
        //    （`AcquireFrame` 回 `DesktopUpdated`、`LastMouseUpdateTime == 0`），
        //    都会把一份**好快照**覆盖成"不可见 (0,0)" ⇒ 上层
        //    `WindowsGpuFrameEncoder.TryCompositePointer` 判定"指针当前不可见"直接返回、
        //    **不合成** ⇒ 现场看到的是「**观看端的鼠标消失**」（用户一动鼠标它又回来）。
        //    🔴 全程**不报错**：`PointerNote` 是 null、`CursorFailures` 不涨、日志里一个字都没有。
        //
        //    ⚠️ 同一份知识本来就在本文件里：`LastPointerPosition` 的注释
        //    （"`Visible == 0` 也可能是'这帧根本没有鼠标更新'"）与 `ShouldTreatAsDesktopChanged`
        //    那处的 `pointerMoved` 判据 —— 位置这条只是漏了照做。
        var pointerMoved = _frameInfo.LastMouseUpdateTime != 0;

        string? note = null;
        PointerSnapshot? fresh = null;

        if (_frameInfo.PointerShapeBufferSize > 0)
        {
            fresh = TryReadPointerShape(out note);
        }
        else if (_pointer == PointerSnapshot.None)
        {
            note = "DDA 这一帧没带指针形状（PointerShapeBufferSize == 0），"
                + "而手上还没有上一份可复用 ⇒ 这一次画不上指针。";
        }

        // 🔥 先把"上一份的位置"取出来：`fresh` 里带的 `X/Y/Visible` 是**占位值**
        //    （`TryReadPointerShape` 固定填 0/0/false），形状与位置在这里合流。
        var lastX = _pointer.X;
        var lastY = _pointer.Y;
        var lastVisible = _pointer.Visible;

        var current = fresh ?? (_pointer == PointerSnapshot.None ? (PointerSnapshot?)null : _pointer);

        if (current is { } snapshot)
        {
            _pointer = pointerMoved
                ? snapshot with { X = position.X, Y = position.Y, Visible = position.Visible != 0 }
                : snapshot with { X = lastX, Y = lastY, Visible = lastVisible };
        }

        PointerNote = note;
    }

    /// <summary>
    /// 从 <c>GetFramePointerShape</c> 读一份形状。
    ///
    /// <para>
    /// 🔥 <b>这个 API 官方支持两段式</b>（缓冲不够时回 <see cref="DxgiInterop.ErrorMoreData"/>
    /// 并给出 <c>RequiredSize</c>）。⚠️ 别把本仓那条"<b>不许</b>两段式"的教训挪过来 ——
    /// 它只属于 <c>GetFrameDirtyRects</c>（那个 API 那样问回来恒是 0，见
    /// <see cref="TryReadDirtyRects"/> 的注释）。
    /// </para>
    /// </summary>
    private PointerSnapshot? TryReadPointerShape(out string? note)
    {
        note = null;

        if (!EnsurePointerShapeBuffer((int)_frameInfo.PointerShapeBufferSize, out note)) return null;

        var getShape = ComVtable.Bind<DxgiInterop.Delegates.GetFramePointerShapeFn>(
            _duplication, DxgiInterop.Duplication.GetFramePointerShape);

        var info = default(DxgiInterop.OutduplPointerShapeInfo);

        var hr = getShape(
            _duplication, (uint)_pointerShapeCapacity, _pointerShapeBuffer, out var required, ref info);

        if (hr == DxgiInterop.ErrorMoreData)
        {
            if (!EnsurePointerShapeBuffer((int)required, out note)) return null;

            hr = getShape(
                _duplication, (uint)_pointerShapeCapacity, _pointerShapeBuffer, out required, ref info);
        }

        if (!ComVtable.Succeeded(hr))
        {
            note = $"GetFramePointerShape 失败（{ComVtable.Describe(hr)}）—— 这一帧画不上指针。";

            return null;
        }

        PointerShapeType? type = info.Type switch
        {
            (uint)PointerShapeType.Color => PointerShapeType.Color,
            (uint)PointerShapeType.MaskedColor => PointerShapeType.MaskedColor,
            (uint)PointerShapeType.Monochrome => PointerShapeType.Monochrome,
            _ => null,
        };

        if (type is null)
        {
            note = $"DDA 给了一个不认识的指针形状类型 {info.Type} —— 画不上指针（不猜）。";

            return null;
        }

        // ⚠️ `MONOCHROME` 的 `Height` 是**实际高度的 2 倍**（上半 AND 掩码、下半 XOR 掩码）。
        var actualHeight = type == PointerShapeType.Monochrome ? (int)info.Height / 2 : (int)info.Height;

        var rows = type == PointerShapeType.Monochrome ? actualHeight * 2 : actualHeight;
        var length = (int)info.Pitch * rows;

        if (info.Width == 0 || actualHeight <= 0 || info.Pitch == 0 || length <= 0)
        {
            note = $"DDA 给的指针形状尺寸不合法：{info.Width}×{info.Height}，Pitch {info.Pitch}，Type {info.Type}。";

            return null;
        }

        if (length > _pointerShapeCapacity)
        {
            note = $"指针形状需要 {length} 字节，缓冲只有 {_pointerShapeCapacity} 字节"
                + "（DDA 报的所需大小与形状头里的尺寸对不上）。";

            return null;
        }

        var bytes = new byte[length];

        Marshal.Copy(_pointerShapeBuffer, bytes, 0, length);

        return new PointerSnapshot(
            Type: type.Value,
            Width: (int)info.Width,
            ActualHeight: actualHeight,
            Pitch: (int)info.Pitch,
            HotSpotX: info.HotSpotX,
            HotSpotY: info.HotSpotY,
            X: 0,
            Y: 0,
            Visible: false,
            ShapeHasAlpha: type != PointerShapeType.Color || HasAnyAlpha(bytes),
            Shape: bytes);
    }

    private bool EnsurePointerShapeBuffer(int bytes, out string? note)
    {
        note = null;

        if (bytes <= 0)
        {
            note = "DDA 说这一帧的指针形状是 0 字节。";

            return false;
        }

        if (_pointerShapeBuffer != IntPtr.Zero && _pointerShapeCapacity >= bytes) return true;

        // ⚠️ 这是 Win32 堆内存，**不是** COM 指针 —— 不能用 `ComVtable.ReleasePointer`。
        if (_pointerShapeBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_pointerShapeBuffer);

        // 留一倍余量：形状大小偶尔在两次调用之间变，每次都重分配是白烧。
        var capacity = Math.Max(Math.Max(bytes, _pointerShapeCapacity * 2), 4096);

        _pointerShapeBuffer = Marshal.AllocHGlobal(capacity);
        _pointerShapeCapacity = capacity;

        return true;
    }

    /// <summary>
    /// 整份 BGRA 形状里有没有任何非 0 的 α。
    /// <para>见 <see cref="PointerSnapshot.ShapeHasAlpha"/> —— 它决定那份 α 算不算数。</para>
    /// </summary>
    private static bool HasAnyAlpha(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0) return true;
        }

        return false;
    }

    /// <summary>
    /// 读一张纹理自己的描述（宽 / 高 / 格式）。
    /// <para>
    /// 🔥 <b>它才是"应该期望多大一张图"的权威值</b>，而不是枚举结果 ——
    /// 分辨率在两次枚举之间可能已经变了（换分辨率 / 拔插屏），
    /// 以枚举为准的旧值会让编码器拿到一个与真实纹理不符的尺寸 ⇒ 编出花屏。
    /// </para>
    /// </summary>
    /// <param name="texture">要描述的纹理。</param>
    /// <param name="label">出错时写进原因里的名字（"桌面副本" / "抓到的纹理"），只为把话说清。</param>
    private static bool TryDescribeTexture(
        IntPtr texture, string label, out uint width, out uint height, out int format, out string? failure)
    {
        width = 0;
        height = 0;
        format = 0;
        failure = null;

        if (texture == IntPtr.Zero)
        {
            failure = $"内部状态错误：{label} 的句柄是空的。";

            return false;
        }

        var getDesc = ComVtable.Bind<D3D11Interop.Delegates.GetTexture2DDescFn>(
            texture, D3D11Interop.Texture2DSlots.GetDesc);

        var hr = getDesc(texture, out var desc);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"读不出{label}的纹理描述（{ComVtable.Describe(hr)}）。";

            return false;
        }

        width = desc.Width;
        height = desc.Height;
        format = desc.Format;

        return true;
    }

    /// <summary>
    /// 读本帧的脏矩形。
    /// <para>
    /// 🔥 <b>返回值只可能是「一组矩形」或 <c>null</c></b>，与本方案里
    /// <c>CapturedSurface.DirtyRects</c> 的约定对齐：
    /// 拿不到有效信息时给 <c>null</c>，<b>而不是给空表</b> ——
    /// 空表在那边被读成「整屏都可能变了」，性质完全不同。
    /// </para>
    /// <para>
    /// ⚠️ <b>不要用"先传 0 字节问大小"那套两段式</b>：本机实测（meta=16B，正好一个矩形）
    /// 那样问回来的"所需大小"是 <b>0</b>，于是恒判成"没有脏区"——
    /// 一个 16 字节就能装下的答案被问没了。改成一次给足缓冲、看实际写入多少。
    /// </para>
    /// </summary>
    private IReadOnlyList<DirtyRect>? TryReadDirtyRects()
    {
        // 没有元数据 = 本帧只有指针动过之类，DDA 不提供脏区。
        if (_frameInfo.TotalMetadataBufferSize == 0) return null;

        var getDirty = ComVtable.Bind<DxgiInterop.Delegates.GetFrameDirtyRectsFn>(
            _duplication, DxgiInterop.Duplication.GetFrameDirtyRects);

        var rectSize = Marshal.SizeOf<DxgiInterop.Rect>();
        var bufferBytes = (uint)(MaxDirtyRects * rectSize);

        var buffer = Marshal.AllocHGlobal((int)bufferBytes);

        try
        {
            var hr = getDirty(_duplication, bufferBytes, buffer, out var written);

            // MORE_DATA = 脏区多到装不下 ⇒ 那基本就是"整屏在动"。
            // 与其搬一大坨矩形过去，不如说"我不知道具体哪块"（null），让上层按整屏处理。
            if (!ComVtable.Succeeded(hr)) return null;

            var count = (int)(written / (uint)rectSize);

            if (count <= 0) return null;

            var rects = new List<DirtyRect>(count);

            for (var i = 0; i < count && i < MaxDirtyRects; i++)
            {
                var native = Marshal.PtrToStructure<DxgiInterop.Rect>(buffer + (i * rectSize));

                rects.Add(new DirtyRect(native.Left, native.Top, native.Right, native.Bottom));
            }

            return rects;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 显存 → 内存
    // ═══════════════════════════════════════════════════════════

    private bool EnsureStaging(uint width, uint height, int format, out string? failure)
    {
        failure = null;

        if (_staging != IntPtr.Zero && _stagingWidth == width && _stagingHeight == height
            && _stagingFormat == format)
        {
            return true;
        }

        ReleaseStaging();

        var gpu = _gpu;

        if (gpu is null)
        {
            failure = "内部状态错误：设备已经没了。";

            return false;
        }

        var desc = D3D11Interop.Texture2DDesc.Staging(width, height, format);

        var create = ComVtable.Bind<D3D11Interop.Delegates.CreateTexture2DFn>(
            gpu.Device, D3D11Interop.DeviceSlots.CreateTexture2D);

        var hr = create(gpu.Device, ref desc, IntPtr.Zero, out _staging);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"建读回用的暂存纹理失败（{ComVtable.Describe(hr)}）—— {width}×{height}。";

            return false;
        }

        _stagingWidth = width;
        _stagingHeight = height;
        _stagingFormat = format;

        return true;
    }

    private bool CopyAndReadStaging(
        int width, int height, out byte[] buffer, out int outWidth, out int outHeight, out string? failure)
    {
        buffer = Array.Empty<byte>();
        outWidth = 0;
        outHeight = 0;

        var gpu = _gpu;

        if (gpu is null)
        {
            failure = "内部状态错误：设备已经没了。";

            return false;
        }

        var copy = ComVtable.Bind<D3D11Interop.Delegates.CopyResourceFn>(
            gpu.Context, D3D11Interop.ContextSlots.CopyResource);

        copy(gpu.Context, _staging, _desktopCopy);

        var map = ComVtable.Bind<D3D11Interop.Delegates.MapFn>(
            gpu.Context, D3D11Interop.ContextSlots.Map);

        var hr = map(gpu.Context, _staging, 0, D3D11Interop.MapRead, 0, out var mapped);

        if (!ComVtable.Succeeded(hr))
        {
            failure = $"Map 暂存纹理失败（{ComVtable.Describe(hr)}）。";

            return false;
        }

        var unmap = ComVtable.Bind<D3D11Interop.Delegates.UnmapFn>(
            gpu.Context, D3D11Interop.ContextSlots.Unmap);

        try
        {
            var stride = width * 4;

            var result = new byte[stride * height];

            // ⚠️ 逐行拷，**必须用 RowPitch 而不是 width×4**：
            //    驱动为了对齐常常把行距撑大（4K 桌面实测多见多出几十到几百字节）。
            //    按宽度硬拷会让每一行错开一点点 —— 画面"斜着花"，低分辨率下还看不出来。
            //
            //    性能上不必担心：这是 CopyTo 而不是逐字节循环，一行一次 memmove。
            for (var y = 0; y < height; y++)
            {
                var source = mapped.Data + (y * (int)mapped.RowPitch);

                Marshal.Copy(source, result, y * stride, stride);
            }

            buffer = result;
            outWidth = width;
            outHeight = height;
            failure = null;

            return true;
        }
        finally
        {
            // 🔥 Map 成功之后**必须** Unmap（连抛异常也要走这里），
            //    否则这张暂存纹理再也 Map 不上：症状是"第二帧开始全是黑"。
            unmap(gpu.Context, _staging, 0);
        }
    }

    private void ReleaseStaging()
    {
        ComVtable.ReleasePointer(ref _staging);

        _stagingWidth = 0;
        _stagingHeight = 0;
        _stagingFormat = -1;
    }

    private static string DescribeFormat(int format) => format switch
    {
        DxgiInterop.FormatB8G8R8A8Unorm => "B8G8R8A8_UNORM",
        0x1C => "R8G8B8A8_UNORM",
        0x18 => "R10G10B10A2_UNORM（10 位桌面）",
        0x0A => "R16G16B16A16_FLOAT（HDR）",
        _ => $"DXGI_FORMAT({format})",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            ReleaseHeldFrame();

            ComVtable.ReleasePointer(ref _duplication);

            // ⚠️ 顺序要紧：两张纹理都建在那个设备上，先释放它们再放设备。
            ReleaseStaging();
            ReleaseDesktopCopy();

            _gpu?.Dispose();
            _gpu = null;
            _boundScreenId = null;

            // ⚠️ 指针形状缓冲是 Win32 堆内存（`AllocHGlobal`），不是 COM 对象。
            if (_pointerShapeBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_pointerShapeBuffer);

            _pointerShapeBuffer = IntPtr.Zero;
            _pointerShapeCapacity = 0;
            _pointer = PointerSnapshot.None;
        }
    }
}
