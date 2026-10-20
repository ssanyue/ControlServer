using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using ControlServer.Agent.Audio;
using ControlServer.Agent.Remote;
using ControlServer.Agent.Video;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent.Tasks;

/// <summary>
/// 一路推流的<b>实时状态</b>（托盘自检、日志、会话上报都用它）。
/// </summary>
/// <param name="ActualFps">
/// 实测帧率。
/// <para>
/// 🔥 它与 <paramref name="TargetFps"/> <b>必须同时存在</b>：
/// 只有目标值时，现场看到"画面很卡"无从判断是"我们本来就要 12 fps"
/// 还是"根本达不到 12"。两个数放在一起，问题立刻分得清。
/// </para>
/// </param>
/// <param name="DroppedFrames">
/// 丢掉的帧数（编码失败 / 单帧超限）。
/// <para>⚠️ 与"发出去的帧数"分开计：编码在失败与链路在堵，是两种完全不同的故障。</para>
/// </param>
public sealed record RemoteStreamStatus(
    string SessionId,
    string ScreenId,
    string ScreenName,
    int Width,
    int Height,
    int TargetFps,
    double ActualFps,
    long FramesSent,
    long BytesSent,
    long DroppedFrames,
    DateTimeOffset StartedAt,
    string? LastError,
    string? AudioState = null,
    string? AudioReason = null,
    string? AudioFormat = null,
    long AudioFramesSent = 0,
    long AudioBytesSent = 0,
    long AudioDropped = 0,

    /// <summary>
    /// <b>桌面已经静止了多久</b>（毫秒；M5.4-c② 步 7）。
    /// <para>
    /// 🔥 它与 <paramref name="ActualFps"/> 的关系是"看起来像、其实相反"：
    /// 帧率掉到 0 既可能是"桌面没动"也可能是"编不出来"，而本参数只答前一种 ——
    /// 它是<b>抓屏器</b>的结论（"没有新帧"＝桌面没变），不是编码器或链路的状态。
    /// </para>
    /// <para>
    /// ⚠️ <b>内存（MJPEG）路径恒为 <c>0</c></b>：那条路上抓屏器每轮必出一帧，
    /// 根本没有"桌面有没有变"这个概念。所以那里的 0 含义是"有帧在流"，
    /// 不是"我们检测到桌面在变" —— 别拿它当 GDI 的静止检测。
    /// </para>
    /// </summary>
    long? DesktopIdleMs = null);

/// <summary>
/// <b>建一个视频编码器所需的全部输入</b>（M5.4-b 第 6 步）。
///
/// <para>
/// 🔥 <b>为什么不是三个位置参数、也不是让工厂自己从别处读</b>：GPU 那条路上
/// <c>TryCreate</c> 需要的这三样<b>只有宿主知道</b>（画质由中心下发、帧率由会话决定、
/// 屏号由请求带进来），而工厂是在<b>构造宿主时</b>就给出去的一个闭包 ——
/// 它没有任何办法"稍后"再问宿主。
/// </para>
///
/// <para>
/// ⚠️ <b>少了 <see cref="ScreenId"/> 会怎样，值得写在这里</b>：GPU 门面在自己内部挑屏
/// （优先主屏，<b>与内存路径"优先虚拟桌面"刻意不同</b>）。工厂拿不到屏号就只能让它去挑主屏，
/// 于是"中心点名要第 2 块屏"会被<b>安静地</b>做成"抓主屏" —— 症状正是最贵的那一类：
/// <b>看着 A 的屏幕、实际操作 B</b>，而两边日志都正常。
/// </para>
/// <para>
/// ⚠️ <b>少了 <see cref="Fps"/> 会怎样</b>：帧率会进硬编的媒体类型字段，
/// 写错不会报错，只会让编码器的码率控制按错的帧率估 —— 表现是"码率忽高忽低"。
/// </para>
/// </summary>
/// <param name="Quality">
/// JPEG 画质（1–100）。<b>只有内存路径用它</b>；GPU 路径按码率控制，忽略它。
/// </param>
/// <param name="Fps">目标帧率（已 clamp 过）。</param>
/// <param name="ScreenId">
/// 中心点名要抓的屏；<c>null</c>/空 = <b>没有点名</b>，由实现按自己的规则挑
/// （内存路径优先虚拟桌面，GPU 路径优先主屏）。
/// </param>
/// <param name="Codecs">
/// <b>可接受的编码格式，按偏好排序</b>（第 0 项 = 中心首选；M5.4-b 第 7 步）。
/// 空表 = 中心没指定。
///
/// <para>
/// 🔥 <b>为什么把"整个集合"交给工厂、而不是宿主自己挑一个</b>：格式由<b>目标分辨率</b>决定
/// （H.264 硬编封顶 4096、HEVC 封顶 8192），而分辨率<b>只有在挑屏之后才知道</b> ——
/// 而挑屏正是编码器工厂（<c>TryCreate</c>）在做的事。
/// 宿主先挑一个再交给工厂，就会出现"两个组件各自按不同信息做同一个决定"。
/// </para>
/// <para>
/// ⚠️ 工厂<b>只许在这个集合里挑</b>（例外见 <c>GdiJpegFrameEncoder</c> 那条注释：
/// MJPEG 不需要 WebCodecs，任何浏览器都能显示，所以它不算"越界"）。
/// </para>
/// </param>
public readonly record struct VideoEncoderRequest(
    int Quality,
    int Fps,
    string? ScreenId,
    IReadOnlyList<string>? Codecs = null);

/// <summary>
/// <b>发一块的结果</b>（M5.5）—— 三种结局对应三种<b>完全不同的</b>下一步动作。
///
/// <para>
/// 🔥 <b>为什么必须是枚举而不是 bool</b>："没发出去"有两种，而它们该做的事相反：
/// <b>丢帧</b>（这一块太大/打包失败）⇒ 下一轮再试，并要求关键帧；
/// <b>致命</b>（管道断了 / 超限到该放弃）⇒ <b>结束整条流</b>。
/// 合成一个 bool 时，唯一能写的是"都当成丢帧"—— 那会让断线的会话一直空转，
/// 一直在烧被控机的 CPU。
/// </para>
/// </summary>
internal enum FrameSendResult
{
    /// <summary>发出去了。</summary>
    Sent,

    /// <summary>这一帧没发出去（太大 / 打包失败），<b>下一轮再试</b>。</summary>
    Dropped,

    /// <summary>链路断了或者该放弃了 ⇒ <b>结束这一路推流</b>。</summary>
    Fatal,
}

/// <summary>
/// <b>会话侧的推流宿主</b>（M5.1）—— 跑在<b>托盘进程</b>里。
///
/// <para>
/// 🔥 <b>为什么整条抓屏链路必须在托盘</b>：抓屏与注入都<b>只能在有桌面的会话里做</b>。
/// 服务跑在 Session 0，那里没有桌面 —— 在那抓屏要么失败、要么抓到一片空。
/// 所以服务只负责"把要推流的指令转过来"，真正干活的是这里。
/// </para>
///
/// <para><b>它做五件事，顺序固定</b>：</para>
/// <list type="number">
///   <item>枚举屏幕，挑出要抓的那块（<b>在校验阶段就挑</b>，而不是等抓的时候才炸）；</item>
///   <item>把码流管道架起来（托盘是服务端，方向理由见 <see cref="RemoteStreamProtocol"/>）；</item>
///   <item>等接收端连上来（<b>有超时</b> —— 没人来接就收摊，不能让它永远占着抓屏）；</item>
///   <item>循环：抓一帧 → 编码 → 写一帧 → 按目标帧率节流。</item>
///   <item><b>再架一条反方向的输入管道</b>（M5.2）：服务写、这里读，
///         读到一条就往桌面注入一条（见 <see cref="InputLoopAsync"/>）。</item>
/// </list>
///
/// <para>
/// 🔥 <b>两条管道是分开的，而且必须分开</b>：码流是"托盘产、服务消费"，
/// 输入是"服务产、托盘消费"。同一条字节流上双向写会让帧边界协议彻底失效
/// （见 <see cref="RemoteInputProtocol"/>）。它们也各自独立地生死：
/// 画面断了还能点（虽然没什么意义），输入断了画面照常 —— 不该互相牵连。
/// </para>
///
/// <para>
/// 🔥 <b>注入只能在这里做</b>：与抓屏同一条理由 —— 服务在 Session 0，那里没有桌面。
/// 于是托盘手上握着两类操作别人的机器的方法（看 + 动），
/// 这也是为什么它<b>刻意不持有任何凭据</b>：它只听本机那条管道的话。
/// </para>
///
/// <para>
/// 🔥 <b>队列深度 = 1（这里实际是 0）：不排队，跟不上就掉帧。</b>
/// 本实现是<b>严格串行循环</b>：上一帧没写完，绝不抓下一帧。
/// 这是刻意的 —— 一旦开了队列，"编码器跟不上"就会变成"待编帧越积越多"，
/// 表现出来的不是掉帧，而是<b>延迟越拖越长</b>（帧率看着还行，但画面越来越滞后）。
/// 宁可掉帧，不要堆积。
/// </para>
///
/// <para>
/// ⚠️ <b>它绝不抛异常</b>：托盘是 WinExe，未捕获异常会让进程悄无声息地消失，
/// 用户看到的是"托盘图标不见了"。所以从建管道到每一帧编码，逐层自己兜住并留痕。
/// </para>
/// </summary>
public sealed class RemoteStreamHost : IAsyncDisposable
{
    /// <summary>默认目标帧率。</summary>
    /// <remarks>
    /// 🔥 <b>12 是刻意的低值，不是保守</b>：MJPEG 只有软编（NVENC / QSV 都不编 JPEG），
    /// 而它吃的是 CPU —— 也就是<b>被控机那台正在跑展厅业务的机器</b>的 CPU。
    /// M5.1 的任务是"证明链路通"，不是"看着流畅"；后者要等 M5.4 的硬编。
    /// </remarks>
    public const int DefaultMaxFps = 12;

    /// <summary>帧率上限的合理区间（防"传个 1000 进来把这台机器烧了"）。</summary>
    private const int MinFps = 1;
    private const int MaxFps = 60;

    /// <summary>
    /// 从开始推流起，多久还没产出<b>第一帧</b>就判定为"这路流起不来"，收场。
    ///
    /// <para>
    /// 🔥 <b>为什么必须有这条</b>：抓屏失败的重试策略是"一直重试"（见 <c>PumpAsync</c> 里的注释，
    /// 那是为锁屏 / UAC 安全桌面 / 显示器热插拔准备的，它们都会自己恢复）。
    /// 但有一类失败<b>永远不会恢复</b>——最典型的是"要抓的屏在这台机器上不存在"
    /// （中心按多屏机器下发了 <c>virtual-desktop</c>，而这台只有一块屏；
    /// <c>Resolve</c> 会抛"找不到屏…本机可抓的屏有：…"）。
    /// </para>
    ///
    /// <para>
    /// 没有这条预算时的现场是这样闭环的：托盘的循环以 1 Hz 一直重试、每 30 次记一条日志；
    /// 服务一直等在第一帧上，因此<b>永远不向中心报状态</b>；中心那边 20 秒后按"建会话超时"
    /// 把会话收掉并<b>从账本里删掉</b>——而收掉时中心<b>不会</b>下发停止指令
    /// （账本不碰网络）。于是这个循环<b>没人叫停</b>：被控机一直在抓屏失败，
    /// 直到再次有人点"结束"（那时中心只会回 404，因为会话已经不在了）。
    /// 表现是"这台机器莫名一直很卡"，而所有日志都只说"抓屏失败（会继续重试）"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 判据是"<b>一帧都没产出过</b>"，不是"连续失败次数"：只要出过一帧，
    /// 说明配置是对的，之后的重试就都该归到"会恢复"那一类（锁屏也能恢复）。
    /// 用失败次数当判据会把"跑了一小时、中途锁屏两分钟"这种正常情况误杀。
    /// </para>
    /// </summary>
    private static readonly TimeSpan StartupFrameBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <b>指针合成诊断的日志间隔</b>（M5.4-b 第 5 步）。
    ///
    /// <para>
    /// 🔥 <b>为什么是"周期"而不是"开流时打一句"</b>：<c>CursorDiagnostics</c> 是
    /// <b>某一刻的快照</b>，而开流那一句（见 <c>Start</c>）打在<b>一帧都还没编之前</b>
    /// ⇒ 那时统计必然是空的。"收流时打一句"同样不行：会话多半是被中心判超时清掉的，
    /// 被控端根本收不到那一声。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 5 秒是"够看完整个开机过程、又不会刷爆托盘日志"的折中（12 fps 下约 60 帧一条）。
    /// </para>
    /// </summary>
    private static readonly TimeSpan CursorLogPeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 「桌面没有新帧」那条日志的节流周期（M5.4-c② 步 8）。
    ///
    /// <para>
    /// 🔥 从"每 300 次"改成"每 10 秒"：静止退避之后每 tick 的时长会从 16 毫秒变成 100 毫秒，
    /// 同样 300 次就从 10 秒变成两分半 —— 于是"刚静止"与"静止半小时"在日志上一个样。
    /// 按时间节流对<b>任何</b>退避档位都成立，这才能在事后一眼看出"它到底静止了多久"。
    /// </para>
    /// </summary>
    private static readonly TimeSpan IdleLogPeriod = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <b>连续</b>几个关键帧发不出去，就判定"这个分辨率 / 画质组合<b>根本传不过去</b>"，收场。
    ///
    /// <para>
    /// 🔥 <b>它防的是"无限重试一件不可能的事"。</b>一个帧超过管道上限时，正确处置是
    /// "丢掉它 + 下一帧强制关键帧"（丢掉任何一帧都会在参考链上留一个洞，
    /// 细节见 <see cref="Session.PumpAsync"/> 里那段）。但如果<b>关键帧本身就超限</b>，
    /// 那么"再要一个关键帧"只会得到同样大的关键帧 —— 一个永远出不去的循环：
    /// 被控机一直抓、一直编、一直丢，观看端什么都没有，而两边计数都"正常"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>为什么是 3 次而不是 1 次</b>：关键帧的大小<b>随内容变</b>。
    /// 整屏切换（展厅里切一次节目）那一瞬间的关键帧可能远大于稳态，
    /// 而它下一帧就正常了。给两次重来的机会，能把这类偶发形状与
    /// "分辨率真的选大了"分开。
    /// </para>
    ///
    /// <para>
    /// 🔸 与 <see cref="StartupFrameBudget"/> 那个"用<b>从来没出过帧</b>当判据、
    /// 不用连续失败次数"是<b>刻意不同</b>的两条规矩，理由也不同：
    /// 那边失败的是"抓屏配置"（坏就永远坏，所以第一次就该有结论）；
    /// 这边失败的是"这一帧的形状"（随内容变，所以要给几次机会）。
    /// </para>
    /// </summary>
    private const int OversizeKeyframeGiveUp = 3;

    private readonly Func<IScreenCapturer> _capturers;

    /// <summary>
    /// 视频编码器工厂（M5.4-b 起是 <see cref="IVideoFrameEncoder"/> 而不是
    /// <see cref="IFrameEncoder"/>）。
    ///
    /// <para>
    /// 🔥 <b>为什么可以（而且必须）抬到公共底座上</b>：宿主<b>同时只持有一个编码器</b>
    /// —— 要么 GPU 硬编、要么内存路径的 MJPEG 降级（方案 §3.5 的回退链），
    /// 二者不会并存。有了底座，宿主就只在"抓屏那一步"分一次叉
    /// （<c>is ISurfaceFrameEncoder</c>），不必为两条路各开一个字段、
    /// 也不必到处写 <c>if (a is not null) … else if (b is not null) …</c>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>"谁来选哪一条路"不在宿主这里</b>：宿主拿到的就是<b>已经定好的那一个</b>。
    /// 回退（GPU 建不起来 ⇒ 换 MJPEG）发生在托盘装配那一层，因为它才认识
    /// <c>GpuPipelineVerdict</c>（停在哪一级、为什么），而那句话要进日志给现场看。
    /// 宿主只看"拿到的是不是 <see cref="ISurfaceFrameEncoder"/>"，不猜为什么。
    /// </para>
    /// </summary>
    private readonly Func<VideoEncoderRequest, IVideoFrameEncoder> _encoders;

    /// <summary>
    /// 参数集（SPS/PPS）的打包方式（M5.4-b 第 6 步）——
    /// <see cref="RemoteStreamPackagings"/> 的取值。
    ///
    /// <para>
    /// 🔥 <b>它是"观看端能力"的下游，不是被控端的偏好</b>：浏览器解不了带内的
    /// <c>avc3</c>，我们在这边"默认带内"也白搭。所以它最终应当由中心按
    /// 「浏览器能力 ∩ 被控端能力」取交集后下发（§8 第 7 步）；在这一步它先是宿主参数。
    /// </para>
    /// <para>
    /// ⚠️ 两条路<b>必须整条自洽</b>：<c>avc3</c>/<c>hev1</c> = 参数集在码流里；
    /// <c>avc1</c>/<c>hvc1</c> = 参数集在 <c>description</c> 里、码流必须换成长度前缀。
    /// 「贴了 avcC、码流却还是 Annex-B」是<b>连体错误</b>：不报错，只是花屏或 configure 失败。
    /// </para>
    /// </summary>
    private readonly string _packaging;

    private readonly IInputInjector? _injector;
    private readonly Func<bool> _inputAllowed;

    /// <summary>
    /// 音频采集器工厂（M5.3）。<c>null</c> = <b>本进程没有音频采集能力</b>。
    /// <para>
    /// ⚠️ 与输入那条同理：为 <c>null</c> 时<b>连音频管道都不建</b> ——
    /// 让服务侧的连接<b>立刻失败</b>，而不是对着一个永远接不上的名字空等 5 秒。
    /// </para>
    /// </summary>
    private readonly Func<IAudioCapturer>? _audioCapturers;

    /// <summary>音频编码器工厂（M5.3）。<c>null</c> = 本进程没有音频编码能力。</summary>
    private readonly Func<IAudioEncoder>? _audioEncoders;

    /// <summary>
    /// 每路流一个的耗时探针（M7.4 §6.2）。<b>做成工厂</b>：探针带着"窗口起点"这种一次性状态，
    /// 跨会话复用会让新会话的第一份报告混进上一路流的数字。
    /// </summary>
    private readonly Func<RemoteStreamProbe> _probes;

    /// <summary>
    /// 唤醒锁（M7.6）：有人看这一路时，阻止被控端关屏 / 起屏保 / 睡眠。
    ///
    /// <para>
    /// 🔥 <b>它治的是"画面全黑，但被控端有人动一下就恢复"这个形态</b>
    /// （见 <see cref="IDisplayWakeLock"/> 的类注释）。这类黑屏在本仓被误判过两轮 ——
    /// 抓屏没坏、编码没坏、链路没坏，坏的是<b>被控端屏幕被系统关掉了</b>，
    /// 而两边日志干干净净。
    /// </para>
    /// <para>
    /// ⚠️ 与抓屏器 / 编码器不同，它<b>必须跨会话复用一个实例</b>：持有状态挂在实现里的专用线程上，
    /// 每次开流新建一个的话，前一个的线程就成了没人关得掉的孤儿（而且它还在替我们
    /// 拦着"别关屏"，一直到进程退出为止）。
    /// </para>
    /// <para>
    /// ⚠️ 它可以为 <c>null</c>（非 Windows / 托盘没接线 / 被配置关掉）——
    /// 那时推流照常，只是"不阻止息屏"。<b>不许</b>因为它缺席就让开流失败。
    /// </para>
    /// </summary>
    private readonly IDisplayWakeLock? _displayWakeLock;

    private readonly Action<string> _log;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();

    private Session? _current;
    private bool _disposed;

    /// <param name="capturers">抓屏器工厂（每次开流新建一个，用完即放）。</param>
    /// <param name="encoders">
    /// 视频编码器工厂。<b>参数是 <see cref="VideoEncoderRequest"/>（画质 ＋ 帧率 ＋ 屏号）</b>，
    /// 返回值是公共底座 <see cref="IVideoFrameEncoder"/> —— 到底拿到 GPU 硬编还是 MJPEG 降级，
    /// 由这个工厂实现方决定（见字段注释）。
    /// <para>
    /// 🔥 做成工厂而不是共享单例：编码器持有原生编码器状态，
    /// 跨会话复用会让"上一个会话的灰度设置"泄漏到下一个会话。
    /// </para>
    /// </param>
    /// <param name="injector">
    /// 键鼠注入器（M5.2）。<c>null</c> = <b>本进程没有注入能力</b>
    /// （非 Windows，或调用方刻意不开）—— 此时<b>不建输入管道</b>，
    /// 服务侧连不上就会如实报"被控端不支持输入"，而不是让它对着一个永远接不上的名字空等。
    /// </param>
    /// <param name="inputAllowed">
    /// <b>这台机器此刻允不允许远程输入</b>（M5.2 的本机否决）。
    /// <para>
    /// 🔥 做成委托而不是构造时取一个布尔值：否决要能<b>不重启托盘</b>就生效 ——
    /// 用户在现场把标记文件建出来之后，<b>下一个会话</b>就不该再能控制。
    /// 构造时取一次，会让"刚关掉、再开一路照样能点"，而那个人会以为开关坏了。
    /// </para>
    /// <para>
    /// ⚠️ 它<b>与 <paramref name="injector"/> 是两件事</b>，别合并成一个参数：
    /// <c>injector == null</c> 说的是"这台机器<b>做不了</b>"（平台/权限），
    /// 而这个委托说的是"这台机器<b>不做</b>"（人的决定）。日志与中心看到的文案完全不同
    /// —— 前者要人换机器或提权，后者只要删一个文件。
    /// </para>
    /// </param>
    /// <param name="audioCapturers">
    /// 音频采集器工厂（M5.3）。<c>null</c> = <b>本进程没有音频采集能力</b>
    /// （非 Windows / 调用方刻意关掉）—— 此时<b>不建音频管道</b>，
    /// 服务侧连不上就会如实报"被控端没有音频"，而不是对着一个永远接不上的名字空等 5 秒。
    /// <para>
    /// 🔥 做成工厂而不是现成实例：采集器<b>每次开流新建、用完即放</b> ——
    /// 它内部握着一个 COM 客户端与一条专用线程，跨会话复用的后果是
    /// "上一个会话的设备句柄"与"这一次的设备"对不上（插入耳机、切换默认设备之后尤其明显）。
    /// </para>
    /// </param>
    /// <param name="audioEncoders">
    /// 音频编码器工厂（M5.3）。<c>null</c> = 本进程没有音频编码能力。
    /// <para>
    /// ⚠️ 与视频编码器同一条理由：编码器持有内部状态（Opus 的残帧缓冲），
    /// 跨会话复用会让"上一个会话攒的半帧"被编进下一个会话的第一帧 ——
    /// 表现是"刚开始的一小段有回声"。
    /// </para>
    /// </param>
    /// <param name="log">极简日志出口（托盘是零 DI 进程，只有一个写 %TEMP% 的日志函数）。</param>
    /// <param name="packaging">
    /// 参数集打包方式（<see cref="RemoteStreamPackagings"/>）。默认<b>带内</b>
    /// —— 那是本批实测后选的默认档：MF 在每个关键帧都重复 SPS/PPS，
    /// 所以"参数集留在码流里"天然满足 WebCodecs 对关键块的要求，
    /// 而且热路径上<b>零转换</b>（不必把 Annex-B 改写成长度前缀）。
    /// </param>
    /// <param name="displayWakeLock">
    /// 唤醒锁（M7.6）。会话期间持有 ⇒ 被控端不会自动关屏 / 起屏保。
    /// <para>
    /// 🔥 <b>不传也能跑</b>：非 Windows、或现场把这条关掉了，推流一切照旧 ——
    /// 只是"没人看时那台机器会按计划息屏"。而那一档的表现正是
    /// <b>"画面全黑、被控端一动就好、两端日志全干净"</b>，是本仓误判过两轮的形态。
    /// </para>
    /// </param>
    public RemoteStreamHost(
        Func<IScreenCapturer> capturers,
        Func<VideoEncoderRequest, IVideoFrameEncoder> encoders,
        IInputInjector? injector,
        Func<bool> inputAllowed,
        Action<string> log,
        Func<IAudioCapturer>? audioCapturers = null,
        Func<IAudioEncoder>? audioEncoders = null,
        string packaging = RemoteStreamPackagings.InBand,
        Func<RemoteStreamProbe>? probes = null,
        IDisplayWakeLock? displayWakeLock = null)
    {
        _capturers = capturers;
        _encoders = encoders;
        _injector = injector;
        _inputAllowed = inputAllowed;
        _log = log;
        _audioCapturers = audioCapturers;
        _audioEncoders = audioEncoders;
        _probes = probes ?? (static () => new RemoteStreamProbe());
        _displayWakeLock = displayWakeLock;

        // 🔥 原样存着、**不在这里归一化**：归一化（以及"这个值我不认得"那句日志）
        //    要等到真正开流那一刻、在有日志出口的地方做 —— 在这里悄悄改掉一个错值，
        //    现场就永远看不到"中心下发了一个我不认识的打包方式"。
        _packaging = packaging ?? string.Empty;
    }

    /// <summary>此刻有没有一路在推（含"管道已架好、接收端还没连上"）。</summary>
    public bool IsStreaming
    {
        get
        {
            lock (_gate) return _current is not null;
        }
    }

    /// <summary>当前一路的状态快照；没有则 <c>null</c>。</summary>
    public RemoteStreamStatus? Status
    {
        get
        {
            lock (_gate) return _current?.Snapshot();
        }
    }

    /// <summary>切换黑白模式（决策 14）。<b>下一帧生效</b>。</summary>
    public SessionCommandResult SetGrayscale(bool enabled)
    {
        Session? session;

        lock (_gate) session = _current;

        if (session is null) return SessionCommandResult.Failure("此刻没有正在推流的会话。");

        session.Grayscale = enabled;

        return SessionCommandResult.Success(
            enabled
                ? "已切到黑白模式（下一帧生效）。⚠️ 编码只做一路，所以全部观看者会同时变灰。"
                : "已切回彩色（下一帧生效）。");
    }

    /// <summary>
    /// <b>请下一帧补一个关键帧</b>（M5.4-c① 步 5）。<b>不抛。</b>
    ///
    /// <para>
    /// 🔥 <b>谁在什么情况下要它</b>：中心重组分片失败（缺片 / 重复片 / 片数不一致）
    /// ⇒ 那一帧在链路中途被丢了。H.264 的 P 帧参考前面的帧，<b>丢一帧等于在参考链上
    /// 戳一个洞</b> —— 观看端从那一刻起每帧都是花的，而<b>解码器不会报错</b>。
    /// 补一个关键帧，能把恢复代价从"最多一个 GOP"（<c>GopSize 60</c> / 12 fps ⇒ 5 秒）
    /// 压到"<b>下一帧</b>"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>它自己丢帧时不需要走这条路</b>：泵里那个 <c>wantKeyframe</c> 本地就置上了
    /// （它是"我知道我丢了"）。这条路径专门给"<b>丢在别处</b>"用 —— 被控端无从知道
    /// 中心那边拼不起来，只能等对面告诉它。
    /// </para>
    ///
    /// <para>
    /// <b>没有正在推流的会话 = 失败</b>（与 <see cref="SetGrayscale"/> 同形）：
    /// 没有流就没有下一帧，请求无处安放。服务侧会把这句原文进日志 ——
    /// 它比"静默成功"有用得多（那会让"中心一直要关键帧、画面一直不动"无法定位）。
    /// </para>
    /// </summary>
    public SessionCommandResult RequestKeyframe()
    {
        Session? session;

        lock (_gate) session = _current;

        if (session is null) return SessionCommandResult.Failure("此刻没有正在推流的会话。");

        session.RequestKeyframe();

        return SessionCommandResult.Success("已请求补一个关键帧（下一帧生效）。");
    }

    /// <summary>
    /// <b>回答"这台机器的桌面已经静止多久了"</b>（M5.4-c② 步 7）。<b>不抛。</b>
    ///
    /// <para>
    /// 🔥 <b>为什么这句话只有这里答得出来</b>：抓屏器回"没有新帧"就是
    /// "桌面内容与指针都没变"—— 而"多久没收到帧"在<b>服务</b>那一侧至少有三种成因
    /// （桌面真的没变 / 编不出来 / 链路把帧丢了），三者计数上完全一样，处置相反。
    /// 见 <c>RemoteSessionActions.DesktopIdle</c>。
    /// </para>
    ///
    /// <para>
    /// <b>没有正在推流的会话 = 失败</b>（与 <see cref="RequestKeyframe"/> 同形）：
    /// 安静地回一个 0 会让服务把"这台机器没有流"上报成"桌面刚动过"。
    /// </para>
    /// </summary>
    public SessionCommandResult DescribeDesktopIdle()
    {
        Session? session;

        lock (_gate) session = _current;

        if (session is null)
        {
            return new SessionCommandResult(false, "此刻没有正在推流的会话。");
        }

        return new SessionCommandResult(true)
        {
            DesktopIdleMs = session.DesktopIdleMs,
        };
    }

    // ═══════════════════════════════════════════════════════════
    // 启 / 停
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 按动作名分发一条远控指令。<b>不抛</b>。
    /// <para>
    /// ⚠️ 不认识的动作用<b>失败</b>而不是"忽略"：这条管道对面就是服务，
    /// 出现不认识的动作说明两边版本对不上 —— 那种事必须响，不能悄悄吞掉
    /// （否则表现是"服务说发过去了，托盘毫无反应"）。
    /// </para>
    /// </summary>
    public SessionCommandResult Handle(RemoteStreamRequest request)
    {
        if (request is null) return SessionCommandResult.Failure("远控指令为空。");

        return request.Action switch
        {
            RemoteSessionActions.Start => Start(request),
            RemoteSessionActions.Stop => Stop(request.SessionId),
            RemoteSessionActions.Grayscale => SetGrayscale(request.Enabled ?? false),

            // M5.4-c① 步 5：中心丢了帧（拼不起来）⇒ 让下一帧做成关键帧。
            // ⚠️ 它**不**新建会话、不碰抓屏器：没有流在跑时是失败（见 RequestKeyframe）。
            RemoteSessionActions.RequestKeyframe => RequestKeyframe(),

            // M5.4-c② 步 7：服务每秒问一次"桌面静止多久了"（用于页面显示，
            // ⚠️ 不参与中心那边"这路还活着吗"的判据）。
            RemoteSessionActions.DesktopIdle => DescribeDesktopIdle(),

            RemoteSessionActions.AudioProbe => DescribeAudio(),

            // M5.5：运行时改质量档（帧率 + 灰度即时生效；码率要重建编码器，见 SetQuality）。
            RemoteSessionActions.Quality => SetQuality(request),

            // M5.5：屏清单（多屏选择）。返回的是**这个用户会话**的显示器。
            RemoteSessionActions.Screens => DescribeScreens(),

            // M5.5：只编视口那一块（ROI）。宽或高为 0 = 关掉。
            RemoteSessionActions.Viewport => SetViewport(request),

            _ => SessionCommandResult.Failure(
                $"不认识的远控动作「{request.Action ?? "(空)"}」（服务与托盘版本可能不一致）。"),
        };
    }

    /// <summary>
    /// 回答"这台机器有没有音频采集能力"（M5.3 的 <see cref="RemoteSessionActions.AudioProbe"/>）。
    ///
    /// <para>
    /// 🔥 <b>成功 = 有能力；失败 = 没有能力，且 Message 就是给人看的原因。</b>
    /// 服务侧把失败时那句话<b>原样</b>写进会话状态，于是页面上"音频不可用"后面
    /// 跟着的是"本机没有可用的音频输出设备"或"HRESULT 0x…"，而不是一句"没有声音"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 它<b>只探测、不建管道</b>：真正的采集器是会话开始时另外新建的。
    /// 用一次探测实例去采集，会让"探测通过、采集时设备却被拔了"这类竞态
    /// 变成一个很难解释的状态。探测的开销是几次 COM 调用，频率是每路会话一次。
    /// </para>
    /// </summary>
    public SessionCommandResult DescribeAudio()
    {
        if (_audioCapturers is null || _audioEncoders is null)
        {
            return SessionCommandResult.Failure(
                "本进程没有音频采集能力（被控端可能不是 Windows，或未装配音频模块）。");
        }

        IAudioCapturer? capturer = null;

        try
        {
            capturer = _audioCapturers();

            if (!capturer.IsAvailable)
            {
                return SessionCommandResult.Failure(capturer.AvailabilityNote);
            }

            var format = capturer.Format;

            if (format is null)
            {
                // 说"可用"却报不出格式，本身就是一种矛盾 —— 如实说出来，别猜一个默认值。
                return SessionCommandResult.Failure(
                    "音频采集器报告可用，但取不到格式（内部状态不一致）。");
            }

            return SessionCommandResult.Success($"音频：{format}");
        }
        catch (Exception ex)
        {
            return SessionCommandResult.Failure($"探测音频设备失败：{ex.Message}");
        }
        finally
        {
            try
            {
                capturer?.Dispose();
            }
            catch
            {
                // 忽略。
            }
        }
    }

    /// <summary>
    /// 改这一路的质量（M5.5 运行时自适应）。<b>不抛</b>。
    ///
    /// <para>
    /// 🔥 <b>哪些能立即生效、哪些不能，必须在返回值里说清楚</b>：
    /// </para>
    /// <list type="bullet">
    ///   <item><b>帧率</b> —— 立即生效：节流周期是每轮重算的（<see cref="Session.SetTargetFps"/>）；</item>
    ///   <item><b>灰度</b> —— 立即生效：它本来就是运行时可切的（编码前色度置 128）；</item>
    ///   <item><b>码率</b> —— <b>不能</b>：MF 的码率在创建时定死，运行中改不回来
    ///         （REF-M5 铁律 16，实测回读不变）。要改只能重建编码器，而重建会换掉抓屏器、
    ///         也就换掉了中心那侧正在读的那条流 —— 那等于整路重开。</item>
    /// </list>
    ///
    /// <para>
    /// ⚠️ <b>所以本方法的返回值是一份"实际改了什么"的账单</b>，而不是一句"已切换"。
    /// 现场拿着"已切换到低画质"去查为什么码率没降，是最容易浪费半小时的一种局面。
    /// </para>
    /// </summary>
    public SessionCommandResult SetQuality(RemoteStreamRequest request)
    {
        Session? session;

        lock (_gate) session = _current;

        if (session is null)
        {
            // 与 grayscale / keyframe 同形：这条指令的作用对象是"正在推的那一路"。
            return SessionCommandResult.Failure(
                "本机此刻没有正在推流的会话 —— 质量档没有作用对象。");
        }

        var applied = new List<string>();
        var skipped = new List<string>();

        if (request.MaxFps is { } requestedFps)
        {
            // 🔥 运行中改帧率**同样要夹**（与开流那一处同一个判据）：软件 JPEG 路线做不到高帧率，
            //    在这里放行 60 就等于"页面上写着 60、实际 20"——而那不会有任何一层报错。
            var clamped = Math.Clamp(requestedFps, MinFps, MaxFps);
            var fps = RemoteStreamPacing.ApplyEncoderCap(clamped, session.HasHardwareEncode);
            var before = session.TargetFps;

            if (fps != before)
            {
                session.SetTargetFps(fps);
                applied.Add($"帧率 {before} → {fps} fps"
                    + (fps == clamped
                        ? string.Empty
                        : $"（本机走软件 JPEG，上限 {RemoteStreamPacing.SoftJpegMaxFps} fps）"));
            }
            else
            {
                applied.Add($"帧率保持 {fps} fps"
                    + (fps == clamped
                        ? string.Empty
                        : $"（本机走软件 JPEG，上限 {RemoteStreamPacing.SoftJpegMaxFps} fps）"));
            }
        }

        if (request.Enabled is { } grayscale)
        {
            if (session.Grayscale != grayscale)
            {
                session.Grayscale = grayscale;
                applied.Add(grayscale ? "已切黑白" : "已切回彩色");
            }
        }

        if (request.BitrateKbps is { } bitrateKbps)
        {
            skipped.Add(
                $"码率 {bitrateKbps} kbps 没有生效 —— MF 的码率在编码器创建时定死，"
                + "运行中改不回来（实测回读不变）。要降码率请结束这一路再重开；"
                + "只想立刻减轻带宽，降帧率同样有效（帧少了，字节数就少了）。");
        }

        var message = (applied.Count > 0 ? string.Join("；", applied) : "这次没有可即时生效的项") +
                      (skipped.Count > 0 ? "。" + string.Join("；", skipped) : "。");

        _log($"[Stream] 会话 {session.SessionId} 的质量调整：{message}");

        return SessionCommandResult.Success(message);
    }

    /// <summary>
    /// <b>只编画面里的这一块</b>（M5.5 第 5 项：ROI 编码）。
    ///
    /// <para>
    /// 🔥 <b>它为什么值得单独做</b>：8K 桌面在 1080p 的视口里，实际只需要编 1/16 的像素量
    /// —— 可"编整幅再缩小显示"会把这份开销原样付掉，一秒都省不下来。
    /// 现场那种"8K 只有 30 fps"的场景里，这一项是<b>唯一</b>能在不换硬件的前提下
    /// 把帧率拉上一个量级的办法。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>代价是重建编码器</b>（≈0.3 秒空窗 + 首帧必关键帧），所以这里<b>只负责转达</b>，
    /// 去抖在<b>发起方</b>（页面：滚动停止 300 ms 才发）。在这里再去抖一次 =
    /// 在已经等过的 300 ms 上再加 300 ms，用户会明显觉得"滚完还卡一下"。
    /// </para>
    /// </summary>
    public SessionCommandResult SetViewport(RemoteStreamRequest request)
    {
        Session? session;

        lock (_gate) session = _current;

        if (session is null)
        {
            return SessionCommandResult.Failure(
                "本机此刻没有正在推流的会话 —— 视口没有作用对象。");
        }

        return session.SetViewport(
            request.ViewportX, request.ViewportY, request.ViewportWidth, request.ViewportHeight, _log);
    }

    /// <summary>
    /// <b>这台机器有哪些屏可抓</b>（M5.5 多屏选择）。<b>不抛</b>。
    ///
    /// <para>
    /// 🔥 <b>为什么要新建一个抓屏器来枚举</b>：屏清单是"当前这个用户会话的显示器"，
    /// 而 <c>IScreenCapturer.EnumerateScreens()</c> 是唯一一份答案
    /// （与真正抓屏时用的是同一个枚举，见 <c>WindowsScreenEnumerator</c> 的类注释）。
    /// 自己再写一份 <c>EnumDisplayMonitors</c> 的代价不是多几十行，
    /// 而是<b>下拉框里的选项与后端能抓的东西悄悄分叉</b>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>枚举完就释放</b>：DXGI 抓屏器一建出来就握着设备与 duplication；
    /// 拿着它只为读一遍显示器列表，多留一个实例就是多占一份只允许一个客户端的 duplication
    /// （<c>E_ACCESSDENIED</c> 那个坑）。
    /// </para>
    /// </summary>
    public SessionCommandResult DescribeScreens()
    {
        IScreenCapturer? capturer = null;

        try
        {
            capturer = _capturers();

            var screens = capturer.EnumerateScreens();

            if (screens.Count == 0)
            {
                // 与 PrepareFromMemoryCapturer 里那句同义：**这不是软件坏了**。
                return SessionCommandResult.Failure(
                    "这台机器上枚举不到任何显示器 —— 说明当前会话没有可抓的桌面"
                    + "（最可能的原因：没人登录）。");
            }

            var dtos = screens
                .Select(s => new RemoteScreenDto(
                    s.Id, s.Name, s.Left, s.Top, s.Width, s.Height, s.IsPrimary, s.IsVirtualDesktop))
                .ToList();

            var physical = screens.Count(s => !s.IsVirtualDesktop);

            // ⚠️ 这句话是"选了拼屏却回落到 MJPEG"的**唯一**事前提示。
            //    不写的话，现场会以为"这台机器硬编坏了"（见 RemoteSessionDto.CodecNote 的那条纪律）。
            var note = physical > 1
                ? "多屏：选「全部显示器（拼接）」在硬编路径下抓不了（DDA 一次只能抓一个 output），"
                  + "会自动回落 MJPEG；想让这一路用硬编，请选具体某一块屏。"
                : physical == 0
                    ? "只列到拼接项，没有单独的显示器 —— 通常意味着显示器枚举降级了。"
                    : null;

            var payload = JsonSerializer.Serialize(
                new AgentScreensDto(dtos, note, BeijingTime.Now),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            _log($"[Remote] 屏清单：{string.Join("、", screens.Select(s => $"{s.Id}（{s.Name} {s.Width}×{s.Height}）"))}");

            return SessionCommandResult.SuccessWithPayload(payload);
        }
        catch (Exception ex)
        {
            return SessionCommandResult.Failure($"枚举显示器失败：{ex.Message}");
        }
        finally
        {
            // 🔥 **枚举完必须放掉抓屏器**，哪怕只是问了一句"你有几块屏"。
            //    DXGI 抓屏器一建出来就握着 duplication，而 **一个 output 只允许一个客户端** ——
            //    不放的话，紧接着真的去开流时会拿到 `E_ACCESSDENIED`，
            //    现象是"看过一次屏清单之后远控就打不开了"，且只与先后顺序有关、重启就好。
            //
            // ⚠️ 为什么是 `as IDisposable` 而不是直接 `Dispose()`：
            //    `IScreenCapturer` 接口**刻意不继承** `IDisposable`（GDI 抓屏不持有任何
            //    跨调用资源，给它挂个空的 Dispose 反倒让人以为它有东西要放）。
            //    `as` 恰好表达了"能放才放"。
            try
            {
                (capturer as IDisposable)?.Dispose();
            }
            catch
            {
                // 忽略。
            }
        }
    }

    /// <summary>
    /// 开始一路推流。<b>不抛</b>；失败返回带原因的结果（那句原因会一路回到中心页面上）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它<b>不等接收端连上</b>就返回。理由有两条：接流是服务侧收到"已开始"之后
    /// 才发起的（在这里等就成了死锁）；且会话管道那条请求本身只有 5 秒超时，
    /// 把建连（最多 8 秒）塞进去必然超时。连接与首帧的结果走日志与状态，不进同步返回值。
    /// </remarks>
    public SessionCommandResult Start(RemoteStreamRequest command)
    {
        if (command is null || string.IsNullOrWhiteSpace(command.SessionId))
        {
            return SessionCommandResult.Failure("缺少会话 Id。");
        }

        var session = new Session(command.SessionId!.Trim(), _packaging, _probes());

        lock (_gate)
        {
            if (_disposed) return SessionCommandResult.Failure("托盘正在退出。");

            if (_current is not null)
            {
                // 🔥 M5.1 刻意只允许一路：一路 MJPEG 已经能吃掉被控机一块可观的 CPU，
                //    再叠一路就是"开了远控之后展厅业务卡住"。
                //    真要放开，得先有硬编（M5.4）。
                return SessionCommandResult.Failure(
                    $"本机已有会话「{_current.SessionId}」在推流（M5.1 只允许一路，多路要等硬编编码器）。");
            }

            _current = session;
        }

        var fps = Math.Clamp(command.MaxFps ?? DefaultMaxFps, MinFps, MaxFps);
        var quality = Math.Clamp(command.JpegQuality ?? 70, 1, 100);

        // ── 🔥 先建编码器，再挑屏（M5.4-b 起顺序与 M5.1 相反）──
        //
        //    为什么必须反：GPU 那条路上**编码器自己就是抓屏器的持有者**（零拷贝要求
        //    「抓屏 → 色彩转换 → 硬编」共用同一个 D3D11 设备，而那个设备是抓屏器在绑屏时建的）。
        //    按老顺序"先挑屏、后建编码器"，就会出现两个各自挑过屏的组件
        //    —— 而**两个后端的挑屏规则是刻意不同的**（内存路径优先虚拟桌面，
        //    GPU 路径优先主屏，见方案 §3.3.1）⇒ 多屏机上必然挑到不同的屏，
        //    症状是"看着 A 的屏幕、实际操作 B"而日志完全正常。
        //    ⇒ 唯一出处：让门面挑，宿主拿它的结论（见 Session.Prepare）。
        IVideoFrameEncoder encoder;

        // 🔥 第 7 步：把中心给的**整个可接受集合**交给工厂（而不是宿主先挑一个）。
        //    格式由目标分辨率决定，而分辨率只有在挑屏之后才知道 —— 挑屏是工厂在做的事。
        var codecs = RemoteVideoNegotiation.Normalize(command.Codecs);

        try
        {
            encoder = _encoders(new VideoEncoderRequest(quality, fps, command.ScreenId, codecs));
        }
        catch (Exception ex)
        {
            // ⚠️ 与抓屏失败同一条纪律：这里是**同步返回值**能报错的最后机会，
            //    漏出去就变成"开始了但没画面"。
            lock (_gate) _current = null;

            return SessionCommandResult.Failure(
                $"创建视频编码器失败：{ex.GetType().Name}：{ex.Message}");
        }

        // 🔥 **帧率必须按"这条路真能做到"夹一次**（M7.4）：
        //    软件 JPEG 那条路是 GDI 抓屏（本机 2160×1440 实测中位 47 ms ⇒ ≈21 fps），
        //    目标给 60 只会让抓屏循环满转、一帧不多 —— 而被控机正是展厅里那台在跑业务的机器。
        //    ⚠️ 判据是**编码器种类**（是不是 GPU 硬编），不是"这次没发出去"：
        //    后者会把一次网络抖动永久记成"这台机器不行"。
        var effectiveFps = RemoteStreamPacing.ApplyEncoderCap(fps, encoder is ISurfaceFrameEncoder);

        if (effectiveFps != fps)
        {
            var isSoftwareJpeg = encoder is IFrameEncoder;

            _log($"[Stream] 目标帧率 {fps} → {effectiveFps} fps："
                 + $"这一路走的是{(isSoftwareJpeg ? "软件 JPEG（GDI 抓屏 ＋ CPU 编码）" : "非硬编路径")}，"
                 + $"实测上限约 {RemoteStreamPacing.SoftJpegMaxFps} fps（抓屏单帧 ~47 ms）——"
                 + "给更高的目标只会烧 CPU、不会多一帧。要 60 帧得先让这台机器的硬编建起来"
                 + "（检查日志里那条编码器裁决说明）。");
        }

        // 🔥 把"这台机器根本没有可抓的桌面"这类**确定性失败**
        //    在同步返回值里说清楚 —— 现场不必去翻日志才知道自己点错了什么。
        if (!session.Prepare(_capturers, command.ScreenId, encoder, out var failure))
        {
            lock (_gate) _current = null;

            // ⚠️ 编码器**必须**在这里放掉：GPU 那条路上它握着一个 D3D11 设备、
            //    一个视频处理器与一个 MF 编码器；不放就等于"每次开流失败漏一整套 COM 对象"。
            try
            {
                encoder.Dispose();
            }
            catch
            {
                // 忽略：正在报的是更能说明问题的那条失败原因。
            }

            return SessionCommandResult.Failure(failure);
        }

        // 🔥 M7.10：按**实际抓到的画面尺寸**决定降采样长边。
        //
        //    为什么必须在这里做（而不是建编码器的时候）：编码器是**在挑屏之前**建的
        //    （GPU 路径要求它自己持有抓屏器），那时还不知道这一路要编多大。
        //    而固定长边 1920 对多屏拼接是错的 —— 三块 1920 并排压到长边 1920，
        //    每块屏只剩 640 宽，缩略图看不出来，一点全屏就满屏糊。
        var scalableLongEdge = ApplyScalableLongEdge(encoder, session.Width, session.Height);

        // 🔒 本机否决**在这里问一次**（每次开流问一次，而不是构造时取一次）：
        //    这样"把标记文件建出来"对**下一个会话**立刻生效，不需要重启托盘。
        var injector = _inputAllowed() ? _injector : null;

        session.Begin(
            _lifetime.Token,
            effectiveFps,
            encoder,
            injector,
            _log,
            _audioCapturers,
            _audioEncoders,
            command.Packaging,
            _displayWakeLock);

        var packaging = session.Packaging;

        // ── 第 7 步：本机对编码格式的**裁决说明** ──
        //
        // 🔥 为什么这句话必须存在：中心给的是"偏好 ＋ 可接受集合"，**裁决在本机**
        //    （交集还有第三项输入 —— 分辨率 —— 只有本机在挑屏之后才知道）。
        //    于是会出现"中心首选 H.264、实际送出来的是 HEVC"这种**正确的**偏离。
        //    没有这句话，现场看到的是"页面写着 H.264、码流却是 HEVC"，
        //    而那是"哪一侧坏了"完全无法判断的局面。
        var codecNote = DescribeCodecChoice(codecs, encoder.Codec, session.IsVirtualDesktop);

        _log($"[Stream] 开始推流：会话={session.SessionId}｜屏={session.ScreenName}"
             + $"（{session.Width}×{session.Height}）｜目标 {effectiveFps} fps"
             + $"｜编码={DescribeEncoder(encoder, packaging)}"
             + $"｜管道={RemoteStreamProtocol.PipeNameForSession(session.SessionId)}（等接收端连接）"
             + $"｜输入管道={DescribeInputChannel(session.SessionId, injector)}"
             + $"｜音频管道={DescribeAudioChannel(session.SessionId)}");

        if (codecNote is not null) _log($"[Stream] {codecNote}");

        // 🔥 M7.10：把"为什么是这个尺寸"写进日志。
        //    没有这一行，现场看到"合并显示比单屏糊"时只能猜；有了它，
        //    一眼能对上"整幅 5760 宽压到长边 3840 ⇒ 每块屏 1280"这个算式。
        if (scalableLongEdge is { } decidedLongEdge)
        {
            var downscaled = session.Width > decidedLongEdge || session.Height > decidedLongEdge;

            _log($"[Stream] 编码长边={decidedLongEdge}（画面 {session.Width}×{session.Height}，"
                 + (downscaled ? "按它等比降采样" : "不需要降采样")
                 + (session.IsVirtualDesktop
                     ? "）；这是**整幅**的长边 —— 拼接时每块屏只有它的 1/N，"
                       + "要看细节请切到单屏（硬编 + 原生分辨率）。"
                     : "）。"));
        }

        // ⚠️ 这一句说的是**"中心下发的那个值我不认得"**，与"中心没说、走了默认"是两回事。
        //    没有它，一个写成 `out-of-band`（连字符）的请求会安静地按带内跑，
        //    而第 8 步那侧的症状是"解码器报错"——排查方向会从中心被带到浏览器。
        if (!string.IsNullOrWhiteSpace(command.Packaging)
            && !RemoteStreamPackagings.IsKnown(command.Packaging))
        {
            _log($"[Stream] ⚠️ 中心下发的打包方式「{command.Packaging}」不是一个已知取值"
                 + $"（{RemoteStreamPackagings.InBand} / {RemoteStreamPackagings.OutOfBand}），"
                 + $"已按默认「{packaging}」执行。");
        }

        return SessionCommandResult.Success(
            $"已开始抓屏推流：{session.ScreenName}（{session.Width}×{session.Height}），目标 {fps} fps（{DescribeEncoderShort(encoder)}）。",
            codecNote);
    }

    /// <summary>
    /// 把"这一路该编多大"落到编码器上（M7.10）。返回<b>实际生效的长边</b>，
    /// 编码器不支持调整时返回 <c>null</c>。
    ///
    /// <para>
    /// 🔥 <b>为什么不是"建编码器时传进去"</b>：见 <c>IScalableFrameEncoder</c> 的说明 ——
    /// 编码器必须在挑屏之前建好，那一刻还不知道画面尺寸。
    /// </para>
    /// <para>
    /// ⚠️ 判据（长边怎么算、为什么封顶）在 <see cref="RemoteFrameExtent.ResolveMaxLongEdge"/>，
    /// 这里只负责"设置"与"返回"。把判据留在一个纯函数里，是为了它能被单测钉住，
    /// 而不是靠"某台三屏机器上看起来对不对"。
    /// </para>
    /// </summary>
    internal static int? ApplyScalableLongEdge(IVideoFrameEncoder encoder, int screenWidth, int screenHeight)
    {
        if (encoder is not IScalableFrameEncoder scalable) return null;

        var longEdge = RemoteFrameExtent.ResolveMaxLongEdge(screenWidth, screenHeight);

        scalable.MaxLongEdge = longEdge;

        return longEdge;
    }

    /// <summary>
    /// "本机为什么用了这个格式"（M5.4-b 第 7 步）。
    ///
    /// <para>
    /// 🔥 <b>四种情况要说成四句不同的话</b>（与那条"编码格式"日志的纪律一致）：
    /// </para>
    /// <list type="number">
    ///   <item><b>照中心首选</b> ⇒ <c>null</c>（没什么可说的，别在状态上报里占地方）；</item>
    ///   <item><b>在同一集合内改判</b>（中心首选 h264，本机用了 hevc）⇒ 说清"是分辨率/硬编做不到首选"，
    ///         并点明<b>没有越出集合</b>（所以浏览器一定解得了）；</item>
    ///   <item><b>中心没指定</b> ⇒ 说清"这是本机自选"，免得日后被当成"中心下发的"；</item>
    ///   <item><b>请求了「全部显示器」</b> ⇒ 说清<b>"拼接只能走 MJPEG"</b>。
    ///         ⚠️ 这一条与上面那条"硬编没建起来"必须分开：那里的原因是<b>不确定</b>的
    ///         （驱动 / 占用 / 尺寸上限，都值得去查），而这里的原因是<b>确定的、正常的</b>
    ///         （DDA 一次只抓一个 output）。合成一句会让现场去查一个没有坏的东西。</item>
    /// </list>
    ///
    /// <para>
    /// ⚠️ <b>它不负责判断"该不该改判"</b> —— 那件事在编码器工厂里（它才知道
    /// <c>TryCreate</c> 停在哪一级）。这里只把**已经发生的事实**翻译成一句话。
    /// </para>
    /// </summary>
    internal static string? DescribeCodecChoice(
        IReadOnlyList<string> requested, string actual, bool isVirtualDesktop = false)
    {
        var isMjpeg = string.Equals(actual, RemoteCodecs.Mjpeg, StringComparison.Ordinal);

        // 🔥 「全部显示器」这一条**排在最前面**：它让"为什么变成 MJPEG"有了一个确定答案。
        //    不这么写的话，现场只能看到"硬编那条路没建起来（见上一行）"，而"上一行"
        //    在被控端的日志里 —— 中心页面上没有那一行。于是这条最常见的、**完全正常**的
        //    回退，会被当成"这台机器的硬编坏了"，方向从头就错了。
        if (isVirtualDesktop && isMjpeg)
        {
            var why = requested.Count == 0
                ? "中心没有指定编码格式，本机自选了 MJPEG"
                : $"中心首选 {requested[0]}，本机实际用了 MJPEG";

            return $"⚠️ {why} —— 因为这一路抓的是「全部显示器」：GPU 硬编那条路一次只抓**一块**屏"
                + "（DDA 只对单个 output 建 duplication），**拼接只能走 MJPEG**。"
                + "画面是**全的**（GDI 一次 BitBlt 抓整块虚拟桌面，不是只抓一块），"
                // 🔴 这里原来说"尺寸也不是错的"——那句话是**假的**（M7.8 现场抓出来）：
                //    拼接 5760×2160 会被 MJPEG 压到长边 1920，也就是每块屏只剩 640 像素。
                //    写"尺寸没问题"会让现场以为"糊"是显示方式的事，从而去查不该查的地方。
                + "但长边会被压到 1920 再编码（省 CPU），所以合并显示是**缩略图清晰度** —— "
                + "要看 1:1 清晰画面请切到单块屏（那一块走硬编，不降采样）。"
                + "代价是编码从硬编变成软编。**这不是故障，也不必去查硬编。**";
        }

        // 中心没指定 ⇒ 本机自选。这句话要说出来：否则"实际是 hevc"会被误当成"中心下发的 hevc"。
        if (requested.Count == 0)
        {
            return $"中心没有指定编码格式 ⇒ 本机自选 {actual}。";
        }

        var preferred = requested[0];

        if (string.Equals(actual, preferred, StringComparison.Ordinal)) return null;

        // MJPEG 不是"集合里的另一个成员"，而是 Last-Resort 回退（见下一条注释），单独说。
        if (isMjpeg)
        {
            return $"⚠️ 中心首选 {preferred}，本机实际用了 MJPEG —— 硬编那条路没建起来"
                + "（停在哪一级见上一行）。MJPEG 不需要 WebCodecs，所以**任何浏览器都显示得了**，"
                + "这次回退不会让观看端黑屏。";
        }

        return $"⚠️ 中心首选 {preferred}，本机改判为 {actual}（那块屏/那台机器做不到 {preferred}，"
            + "停在哪一级见上一行）。⚠️ 它**没有越出**中心给的可接受集合"
            + $"（{string.Join(" > ", requested)}），所以观看端的浏览器一定解得了。";
    }

    /// <summary>
    /// "这一路用的什么编码器、什么打包方式"（推流日志里那一格）。
    /// <para>
    /// 🔥 这一格存在的理由与输入/音频那两格一样：<b>三种情况要说成三句不同的话</b> ——
    /// GPU 硬编（参数集成带内还是带外会改变码流形状）／MJPEG 降级（根本没有参数集这回事）／
    /// 以及"说好的硬编怎么变成 MJPEG 了"。合成一句"编码：h264"会让现场
    /// 拿 MJPEG 的现场去查硬编的问题。
    /// </para>
    /// </summary>
    private string DescribeEncoder(IVideoFrameEncoder encoder, string packaging)
    {
        if (encoder is not ISurfaceFrameEncoder)
        {
            // ⚠️ MJPEG 是**逐帧独立**的，没有参数集、也没有 GOP ⇒ 打包方式对它毫无意义。
            //    这里如实说"不适用"，而不是把 `inband` 也印上去（印了会让人以为它被用上了）。
            return $"{encoder.Codec}（内存路径，逐帧独立：无参数集/GOP，打包方式不适用）";
        }

        return $"{encoder.Codec}（GPU 硬编，参数集{(packaging == RemoteStreamPackagings.InBand ? "带内 avc3/hev1" : "带外 avcC/hvcC ＋ 长度前缀")}）";
    }

    /// <summary>上面那一格的<b>短形式</b>（同步返回给中心页面的那句里用）。</summary>
    private static string DescribeEncoderShort(IVideoFrameEncoder encoder) =>
        encoder is ISurfaceFrameEncoder ? $"{encoder.Codec} 硬编" : $"{encoder.Codec} 软编";

    /// <summary>
    /// "输入管道这一路是什么情况"（推流日志里那一格）。
    /// <para>
    /// ⚠️ 三种情况必须说成三句不同的话 —— 它们在现场的处理方式完全不同：
    /// 没能力（换机器）／被本机否决（删一个文件）／正常（管道名是什么）。
    /// 合成一句"不支持"会让人去查错的方向。
    /// </para>
    /// </summary>
    private string DescribeInputChannel(string sessionId, IInputInjector? injector)
    {
        if (injector is not null) return RemoteInputProtocol.PipeNameForSession(sessionId);

        return _injector is null
            ? "不提供（本进程没有注入能力）"
            : "不提供（本机已禁止远程键鼠注入 —— 删掉 remote-input.deny 即恢复）";
    }

    /// <summary>
    /// "音频管道这一路是什么情况"（推流日志里那一格）。
    /// <para>
    /// ⚠️ 与输入那一格同样的纪律：<b>三种情况说三句不同的话</b> ——
    /// 本进程没能力（换机器）／本来就没有音频设备（插声卡）／正常（管道名是什么）。
    /// </para>
    /// </summary>
    private string DescribeAudioChannel(string sessionId)
    {
        if (_audioCapturers is null || _audioEncoders is null)
        {
            return "不提供（本进程没有音频采集能力）";
        }

        // ⚠️ 这里**不现场探测设备**：探测的结论由服务侧在音频通道接不上时
        //    用 AudioProbe 单独问（见 DescribeAudio）。在这一行里再探一次，
        //    等于在建会话的路径上多花几次 COM 调用，而且它与服务侧随后拿到的结论
        //    可能不一致 —— 日志上会出现"这里说没有设备、那边说设备正常"这种自相矛盾。
        return RemoteAudioProtocol.PipeNameForSession(sessionId);
    }

    /// <summary>
    /// 停止一路推流。<b>不抛</b>。
    /// <para>
    /// ⚠️ 会话 Id 对不上时回<b>成功</b>而不是失败：那种情形（中心重启后重发了一条停指令、
    /// 或本机已经换了一路流）真正的含义是"你要停的那个已经不在了"。
    /// 回失败会让操作者以为"没停成"进而反复点 —— 而那期间本机可能正在推另一路流。
    /// </para>
    /// </summary>
    public SessionCommandResult Stop(string? sessionId)
    {
        Session? session;

        lock (_gate)
        {
            session = _current;

            if (session is null)
            {
                return SessionCommandResult.Success("本机此刻没有正在推流的会话（无需停止）。");
            }

            if (!string.IsNullOrWhiteSpace(sessionId)
                && !string.Equals(session.SessionId, sessionId.Trim(), StringComparison.Ordinal))
            {
                return SessionCommandResult.Success(
                    $"本机正在推的是会话「{session.SessionId}」，与请求的「{sessionId}」不是同一个 —— 未做任何改动。");
            }

            _current = null;
        }

        session.Stop();

        var status = session.Snapshot();

        _log($"[Stream] 停止推流：会话={session.SessionId}｜发帧 {status.FramesSent}"
             + $"｜累计 {status.BytesSent / 1024} KB｜丢帧 {status.DroppedFrames}"
             + $"｜实测 {status.ActualFps} fps");

        return SessionCommandResult.Success(
            $"已停止推流（共发出 {status.FramesSent} 帧，实测 {status.ActualFps} fps）。");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Session? session;

        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
            session = _current;
            _current = null;
        }

        try
        {
            _lifetime.Cancel();
        }
        catch
        {
            // 忽略。
        }

        session?.Stop();

        try
        {
            _lifetime.Dispose();
        }
        catch
        {
            // 忽略。
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════
    // 一路会话
    // ═══════════════════════════════════════════════════════════

    private sealed class Session
    {
        private readonly Stopwatch _clock = new();

        private IScreenCapturer? _capturer;
        private IVideoFrameEncoder? _encoder;
        private IInputInjector? _injector;
        private CancellationTokenSource? _cts;

        /// <summary>
        /// 唤醒锁（M7.6）。<b>实例由宿主持有并共享</b>，这里只是借用 ——
        /// 持有状态挂在实现自己的专用线程上，会话不该拥有它的生命周期。
        /// </summary>
        private IDisplayWakeLock? _displayWakeLock;

        /// <summary>
        /// 这一路画面在<b>虚拟桌面</b>里的原点（M5.2 的坐标换算要用）。
        /// <para>
        /// ⚠️ 它<b>必须在 Prepare 时就记住</b>，不能等注入的时候再去枚举一遍屏：
        /// 显示器热插拔之后坐标会变，而那时候这一路流抓的还是原来那块屏 ——
        /// 重新枚举出来的偏移与画面已经对不上了。
        /// </para>
        /// </summary>
        private int _screenLeft;
        private int _screenTop;

        /// <summary>正在按着的键与鼠标键（<b>会话收场时要把它们松开</b>，见 InputLoopAsync）。</summary>
        private readonly HashSet<int> _pressedKeys = new();
        private readonly HashSet<MouseButtonKind> _pressedButtons = new();

        private long _inputsApplied;
        private long _inputsRejected;
        private string? _lastInputError;

        private long _framesSent;
        private long _bytesSent;
        private long _dropped;

        /// <summary>
        /// 三段耗时探针（M7.4 §6.2）：抓屏 / 编码 / 写管道<b>分开</b>计。
        /// <para>
        /// 🔥 它是<b>只读</b>的：不参与任何判据、不改任何行为，关着的时候三处
        /// <c>Mark()</c> 返回哨兵值、累加直接退出。开它的方式见 <see cref="RemoteStreamProbe.IsRequested"/>。
        /// </para>
        /// <para>
        /// ⚠️ 每路流各一个（不是宿主字段）：窗口与计数都该跟着会话走，
        /// 跨会话复用会让"刚开流那 5 秒"混进上一路流的数字。
        /// </para>
        /// <para>
        /// ⚠️ 它<b>由宿主注入</b>而不是在这里 <c>new</c>：单测要能把时钟换成假的，
        /// 否则"窗口到没到 5 秒"只能靠真的睡 5 秒（脆且慢）。生产侧注入的就是
        /// <see cref="RemoteStreamProbe"/> 的默认构造。
        /// </para>
        /// </summary>
        private readonly RemoteStreamProbe _probe;


        /// <summary>
        /// 「<b>真的发出去了第几帧</b>」的计数。序号只在<b>真要把帧头写下去</b>的那一刻取。
        /// </summary>
        private long _sequence;

        /// <summary>上一帧的内容指纹（软编路径的静止检测）。</summary>
        private ulong _lastFingerprint;

        /// <summary>
        /// 是否已经有一帧指纹可比。<b>不能拿 <c>_lastFingerprint == 0</c> 当"没有"</b>：
        /// 0 是一个合法的哈希值，那样写会在"第一帧恰好哈希成 0"时白丢一帧。
        /// </summary>
        private bool _hasFingerprint;

        /// <summary>
        /// 当前这一批块的序号；<c>0</c> = 这一批还<b>没取号</b>（第一块真发出去时才取）。
        ///
        /// <para>
        /// 🔥 <b>为什么必须是"懒惰取值"而不是"进循环前先取一个"</b>：这两件事在
        /// <c>RemoteStreamOversizeTests.An_oversize_frame_is_dropped_without_ending_the_stream</c>
        /// 与前端逐块拼接的逻辑里都被钉死了 ——
        /// </para>
        ///
        /// <list type="number">
        ///   <item><b>同一批块共享同号：</b>前端每块各有一个独立抖动缓冲，各自靠序号判"断没断"。
        ///         若每块各自 +1，每一块都会以为"每帧跳了 N 个号"⇒ 每帧重建解码器
        ///         ⇒ 画面永远停在关键帧上，而页面上不报任何错。</item>
        ///   <item><b>被丢掉的帧不占号：</b>序号的语义是"真的发出去了第几帧"。
        ///         提前取号会让一次丢帧在序号上留一个洞，而<b>没人消费那个洞</b>
        ///         （"掉了多少"要看帧头里的 <c>Dropped</c> 那个字段）——
        ///         于是它只是噪音。<c>RemoteStreamOversizeTests</c> 明确要求首帧
        ///         <c>Sequence == 1</c>，就是为了不让这个语义悄悄漂掉。</item>
        /// </list>
        ///
        /// <para>
        /// 只有推流泵线程读写它（批开始时清 0），所以不需要 <see cref="Interlocked"/>。
        /// </para>
        /// </summary>
        private long _batchSequence;

        /// <summary>
        /// <b>「请补一个关键帧」的未消费请求</b>（0 / 1，<see cref="Interlocked"/> 读写）。
        ///
        /// <para>
        /// 🔥 <b>为什么是一个"标志"而不是一个"队列"或多算几次</b>：这个请求来自
        /// 中心（重组丢了一帧）→ 服务 → 托盘，一路上可能来很多次（大帧有十几片，
        /// 中心持续丢帧时每片都会带一次）。而<b>要的只是"下一帧做成关键帧"</b> ——
        /// 连来 10 次与来 1 次，结果完全相同。用标志 ⇒ 天然合并，
        /// 不会出现"积压了 10 个请求 ⇒ 连出 10 个 IDR"（8K 的 IDR 正是最贵的东西）。
        /// </para>
        ///
        /// <para>
        /// ⚠️ <b>必须用 <see cref="Interlocked"/> 而不是普通字段</b>：置位发生在
        /// 服务指令线程上，消费发生在推流泵线程上 —— 而这是一个会跑一整场会话的值，
        /// 不是"建会话时定下来就再也不变"的那种。
        /// </para>
        /// </summary>
        private int _keyframeRequested;

        /// <summary>
        /// 记下"请补一个关键帧"。<b>可跨线程调用</b>；多次调用合并成一次。
        /// <para>
        /// ⚠️ 它<b>只置位、不等待</b>：真正的关键帧要等推流泵下一轮抓编时才出。
        /// 因此"请求成功"不等于"关键帧已经发出去了" —— 这一点必须写清楚，
        /// 否则调用方会把它当成一次同步动作来用。
        /// </para>
        /// </summary>
        public void RequestKeyframe() => Interlocked.Exchange(ref _keyframeRequested, 1);

        /// <summary>
        /// 取走请求（<b>取一次就清零</b>）。推流泵每轮开头调一次。
        /// <para>
        /// 🔥 语义是"<b>自上次问过以来，有没有人请求过</b>"，不是"现在是否需要关键帧" ——
        /// 泵自己那个 <c>wantKeyframe</c> 才是后者的答案（它还包含"刚丢了一帧"这个来源）。
        /// 两者是<b>并集</b>关系，不是替代关系。
        /// </para>
        /// </summary>
        public bool TakeKeyframeRequest() => Interlocked.Exchange(ref _keyframeRequested, 0) == 1;

        /// <summary>
        /// 因为<b>单帧超过管道上限</b>而被丢掉的帧数。
        /// <para>
        /// 🔥 <b>它是 <see cref="_dropped"/> 的一个子集，但必须单独有一格</b>：
        /// 两者混在一起时，"被控端编不过来"与"分辨率选大了、一帧根本发不出去"
        /// 长得一模一样（都是"丢帧数在涨"），而处置完全相反 ——
        /// 一个要去查编码器，一个要去降分辨率。这是本项目那条
        /// "两个计数必须并排"纪律的又一次应用。
        /// </para>
        /// <para>
        /// ⚠️ 目前它只进<b>托盘日志</b>，<b>不进</b> <c>RemoteStreamStatus</c> 的字段
        /// （那要动契约、中心 DTO 与前端三处）。要让操作员看到的那句话在
        /// <see cref="_lastError"/> 里 —— 它本来就会随状态上报回中心、显示在页面上，
        /// 而且带着<b>两个并排的数字</b>（这一帧多大 / 上限多大）。
        /// </para>
        /// </summary>
        private long _droppedOversize;

        /// <summary>
        /// <b>连续</b>几个关键帧没能通过尺寸闸门。
        /// <para>
        /// 🔥 为什么要"连续"而不是总数：关键帧过不去意味着观看端<b>永远拿不到参考帧</b>
        /// （后面的每一个 P 帧都会花），所以它不是"丢一帧"而是"这路流废了"。
        /// 但单看一次又不够 —— 内容突变（整屏切换）会让某一帧异常大，下一帧就正常了。
        /// 所以：连续 <see cref="OversizeKeyframeGiveUp"/> 次才判定"这个分辨率传不过去"，
        /// 而中间只要<b>成功发出过一个关键帧</b>就清零。
        /// </para>
        /// </summary>
        private long _oversizeKeyframeStreak;

        // ── 参数集（SPS/PPS）的打包（M5.4-b 第 6 步）──
        //
        // 🔥 这两个是**会话级缓存**，只在关键帧上刷新。缓存而不是每帧算，有两个理由：
        //    ① 参数集只可能在关键帧上变（它不是逐帧的东西）；
        //    ② "每帧扫 800 KB 找 SPS"是纯浪费，而那是热路径。
        //
        // ⚠️ 两者**必须来自同一份 SPS**：`_codecString` 是从那份 SPS 反推出的
        //    profile/level 串（写错的表现是 `NotSupportedError`，看起来像"浏览器不支持 H.264"），
        //    `_codecDescription` 是那份 SPS/PPS 编出的 avcC。指向不同的字节 =
        //    "配置里说的与码流里的对不上"，同样不报错、只是花屏。
        private string? _codecString;

        /// <summary>带外模式用的 avcC / hvcC。<c>null</c> = 至今没解出参数集。</summary>
        private byte[]? _codecDescription;

        /// <summary>有参数集的关键帧数 / 没有参数集的关键帧数（<b>两个都要看</b>，见下面的日志）。</summary>
        private long _keyframesWithParameterSets;
        private long _keyframesWithoutParameterSets;

        /// <summary>已经就"某个关键帧缺参数集"说过一次了（日志去重，不是错误去重）。</summary>
        private bool _warnedParameterSetsMissing;

        /// <summary>已经就"带外模式但解不出参数集、只好按带内发"说过一次了。</summary>
        private bool _warnedOutOfBandUnavailable;

        // ── 音频（M5.3）──
        //
        // 🔥 与视频那组计数**分开**，理由不是"分得细好"，而是两类故障长得完全一样：
        //    "画面卡了"与"声音断了"都可能表现为"上面那个总数不动"。
        //    分开之后，"只有音频在掉"这件事才有地方体现出来。
        private long _audioFramesSent;
        private long _audioBytesSent;
        private long _audioDropped;
        private long _audioSequence;

        /// <summary>音频通道状态（见 <see cref="AgentAudioStates"/>）。</summary>
        private string? _audioState;

        private string? _audioReason;

        /// <summary>
        /// 会话开始时那一刻的 QPC 值 —— <b>会话单调时钟的原点</b>。
        ///
        /// <para>
        /// 🔥 音视频两个时间戳都换算成"距它多少毫秒"，前端才能算它们之间差多少。
        /// 用 <c>Stopwatch.GetTimestamp()</c>（Windows 上就是 QPC）而不是墙钟，
        /// 是因为<b>墙钟会被 NTP 校正</b>，而会话中途一次校正会让 AV 同步整体错位
        /// 且之后再也不回来（见 <c>RemoteAudioFrameHeader.CapturedMonoMs</c> 的备注）。
        /// </para>
        /// </summary>
        private long _startQpc;

        /// <summary>
        /// <b>最后一次"抓到新画面"的时刻</b>（QPC 读数；M5.4-c② 步 7）。
        ///
        /// <para>
        /// 🔥 <b>它是"桌面静止了多久"的唯一来源</b>，与"最后一次<b>发出</b>帧"不是一个时刻：
        /// 编码失败 / 超限丢弃都会让"发帧"停下，而那时桌面可能一直在变 ——
        /// 拿发帧时刻算静止，会把"编不出来"报成"桌面没动"，而那正是要区分的两件事。
        /// </para>
        ///
        /// <para>
        /// ⚠️ <b>初值是"会话开始那一刻"</b>（见 <see cref="Prepare"/>）：于是
        /// "从开流起桌面就没动过"的机器，这个数会从 0 开始长 —— 那正是它该有的样子。
        /// 初值留 0 的话，<see cref="DesktopIdleMs"/> 在头一瞬间会返回"问不到"，
        /// 而那一刻恰恰是最需要说"它真的没动"的时候。
        /// </para>
        /// </summary>
        private long _lastNewFrameAt;

        private string? _lastError;

        /// <summary>
        /// 宿主默认的打包方式（构造时传进来的那个）。
        ///
        /// <para>
        /// 🔥 <b>为什么非要在这里存一份，而不是直接读宿主的字段</b>：本类是<b>嵌套类</b>，
        /// 而嵌套类<b>不持有</b>外部类的实例 —— 它只能看见外部类的 <c>static</c>/<c>const</c> 成员。
        /// 早期那些常量（<see cref="DefaultMaxFps"/>、<see cref="StartupFrameBudget"/>）恰好都是
        /// static/const，所以这个限制一直没露头；到会话级配置这里就藏不住了。
        /// </para>
        /// <para>
        /// ⚠️ 也<b>不该</b>为了省这一个参数把宿主字段改成 static：那等于让"两台机器两种配置"
        /// 变成"进程一份"，而这是进程内的单实例对象，不是全局配置。
        /// </para>
        /// </summary>
        private readonly string _defaultPackaging;

        public Session(string sessionId, string defaultPackaging, RemoteStreamProbe probe)
        {
            SessionId = sessionId;
            _probe = probe;

            // ⚠️ 在这里就归一化：`Begin` 里那次是"中心没说 ⇒ 用默认"，
            //    而默认值本身必须已经是一个能当命令用的值。
            _defaultPackaging = RemoteStreamPackagings.Normalize(defaultPackaging);
        }

        public string SessionId { get; }

        public string ScreenId { get; private set; } = string.Empty;

        public string ScreenName { get; private set; } = string.Empty;

        /// <summary>
        /// 这一路抓的是不是<b>「全部显示器」（虚拟桌面拼接）</b>。
        ///
        /// <para>
        /// 🔥 <b>它存在的唯一用途是给"为什么变成了 MJPEG"一个确定答案</b>：
        /// GPU 硬编那条路一次只抓一块屏（DDA 只对单个 output 建 duplication），
        /// 于是请求「全部显示器」时<b>必然</b>回落到 MJPEG（GDI 那边能拼）。
        /// 没有这个标记，页面上的说明只能写"硬编那条路没建起来（见上一行）"，
        /// 而"上一行"在<b>被控端的日志里</b>，中心页面上根本没有 ——
        /// 现场看到的就是"我选了全部屏，画质忽然变差了，不知道为什么"。
        /// </para>
        ///
        /// <para>
        /// ⚠️ 取值来源是<b>挑屏的那一处枚举结果</b>（<see cref="ScreenDescriptor.IsVirtualDesktop"/>），
        /// 不按屏号字符串比 —— "virtual-desktop" 这个字面量在协议层没有常量，
        /// 写死一处就等于多了一个会漂移的真相。
        /// </para>
        /// </summary>
        public bool IsVirtualDesktop { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public int TargetFps { get; private set; } = DefaultMaxFps;

        /// <summary>
        /// 运行中改目标帧率（M5.5 运行时自适应）。
        ///
        /// <para>
        /// 🔥 <b>它能即时生效，是因为节流周期是每轮重算的</b>（见 <c>LoopAsync</c> 里
        /// "帧率变了就重算 targetPeriod"那一段）。若把周期算在循环外面，
        /// 这个 setter 就变成"改了个没人读的字段"—— 而页面上会显示"已切到 8 fps"、
        /// 实际仍按 12 fps 在推，没有任何一处报错。
        /// </para>
        ///
        /// <para>
        /// ⚠️ 只改<b>节流</b>，不碰编码器：MF 的帧率写在媒体类型里，
        /// 运行中改不了（与码率同一条，见 REF-M5 铁律 16）。这里降的是
        /// "每秒抓多少帧去编"，输出码率随之下降 —— 这正是自适应要的效果。
        /// </para>
        /// </summary>
        public void SetTargetFps(int fps) => TargetFps = Math.Clamp(fps, MinFps, MaxFps);

        /// <summary>
        /// <b>只编画面里的这一块</b>（M5.5 第 5 项：ROI）。
        ///
        /// <para>
        /// 🔥 <b>为什么它必须由编码器执行、而宿主只转达</b>："只编一块"要求转换器
        /// 把源矩形取成那一块（1:1 不缩放）、编码器的尺寸也跟着改成那一块
        /// —— 两件事都在编码器内部，宿主看不见它们的生命周期。
        /// 宿主唯一该做的是<b>把话转过去并把代价说清楚</b>。
        /// </para>
        ///
        /// <para>
        /// ⚠️ <b>宽或高 &lt;= 0 表示关掉 ROI</b>（回到整幅画面）。
        /// </para>
        /// </summary>
        public SessionCommandResult SetViewport(int x, int y, int width, int height, Action<string> log)
        {
            var encoder = _encoder;

            if (encoder is not IRegionSurfaceEncoder region)
            {
                return SessionCommandResult.Failure(
                    "这一路用的编码器不支持只编视口（当前是 MJPEG/内存路径）—— "
                    + "ROI 只在 GPU 硬编那条路上有效。");
            }

            var turningOff = width <= 0 || height <= 0;

            var viewport = turningOff ? (EncodeRegion?)null : new EncodeRegion(x, y, width, height);

            if (!region.SetViewport(viewport))
            {
                return SessionCommandResult.Failure(
                    $"视口 {x},{y} {width}×{height} 不合法（宽高至少 2，坐标不能是负数）—— 保持原样。");
            }

            var message = turningOff
                ? "已关掉视口（回到整幅画面）"
                : $"视口改为 {x},{y} {width}×{height}（只编这一块，1:1 不缩放）";

            // ⚠️ **代价必须说出来**：下一次编帧会重建转换器与编码器（≈0.3 秒空窗、
            //    首帧必关键帧）。不说的话，用户看到的就是"滚一下黑一下"，
            //    而那看起来像网络问题。
            log($"[Stream] 会话 {SessionId}：{message} —— 下一次编帧时重建编码器"
                + "（≈0.3 秒空窗、首帧必关键帧）。这是 ROI 的固有代价，不是故障。");

            return SessionCommandResult.Success(message);
        }

        /// <summary>
        /// <b>这一路实际用的参数集打包方式</b>（归一化之后的值，恒为
        /// <see cref="RemoteStreamPackagings"/> 的两个取值之一）。
        ///
        /// <para>
        /// 🔥 <b>它记在会话上、不是每帧从宿主字段读</b>：一路流的打包方式在它开始那一刻就定了。
        /// 每帧去读宿主字段，等于允许"推流途中宿主那个值变了"——而那种变化会让**前后半段的
        /// 码流形状不一样**，观看端在关键帧之外的地方没法跟着改（改配置只能发生在关键帧上）。
        /// </para>
        /// </summary>
        public string Packaging { get; private set; } = RemoteStreamPackagings.InBand;

        public DateTimeOffset StartedAt { get; private set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// 这一路的编码器是不是<b>GPU 硬编</b>（即能走 DXGI 抓屏、不经过 CPU 读回）。
        ///
        /// <para>
        /// 🔥 <b>它是"帧率上限"的唯一判据</b>（见 <see cref="RemoteStreamPacing.ApplyEncoderCap"/>）：
        /// 软件 JPEG 那条路是 GDI 抓屏，本机实测 2160×1440 单帧 47 ms ⇒ 约 21 fps ——
        /// 给 60 不会多一帧，只会满转烧 CPU。判据用类型而不是"上次发得慢"，
        /// 是因为后者会把一次网络抖动永久记成硬件不行。
        /// </para>
        /// </summary>
        public bool HasHardwareEncode => _encoder is ISurfaceFrameEncoder;

        /// <summary>
        /// 灰度开关（决策 14）。
        /// <para>
        /// 🔥 <b>两条路的生效时机不同，而这件事由编码器自己负责、宿主不介入</b>：
        /// MJPEG 逐帧独立 ⇒ <b>下一帧就变</b>；GPU 那条路内容发生了不连续，
        /// 必须重开 GOP（否则观众看到的是"慢慢从彩色褪成灰度"，看起来就像坏了）
        /// —— 那件事在 <c>WindowsGpuFrameEncoder.Grayscale</c> 的 setter 里做。
        /// </para>
        /// <para>
        /// ⚠️ 这里<b>看不到 <see cref="IFrameEncoder"/> 那个更强的类型</b>：宿主只持公共底座，
        /// 于是"置位后落不落地"完全由实现方保证 —— 换实现时这一层不必改。
        /// </para>
        /// </summary>
        public bool Grayscale
        {
            get => _encoder?.Grayscale ?? false;
            set
            {
                if (_encoder is null) return;

                _encoder.Grayscale = value;

                // 🔥 **必须作废指纹**：软编路径的静止检测会跳过"像素与上一帧相同"的帧，
                //    而灰度是**同一批像素、不同输出** —— 不作废的话，一张静止的桌面上切灰度
                //    会完全没有反应（编码器参数变了，但一帧都没有重编）。
                //    这条坑只在"静止画面 + 切灰度"时出现，而展厅里恰恰经常是静止画面。
                InvalidateFrameFingerprint();
            }
        }

        /// <summary>
        /// 让<b>下一帧无条件编码并发送</b>，哪怕像素与上一帧完全相同。
        /// <para>用在"输入没变、但输出应该变"的场合（目前是灰度切换）。</para>
        /// </summary>
        private void InvalidateFrameFingerprint() => _hasFingerprint = false;

        /// <summary>
        /// 挑出要抓的那块屏，并把 <b>画面来源</b>定下来。失败时 <paramref name="failure"/> 是人话原因。
        /// </summary>
        /// <remarks>
        /// <b>两件事放一起做</b>是刻意的：只建抓屏器不挑屏，就要等到抓第一帧才发现
        /// "屏 ID 不存在"，而那时已经在同步返回值之外了 —— 现场只会看到"开始了但没画面"。
        /// </remarks>
        /// <param name="encoder">
        /// <b>已经建好</b>的视频编码器（见 <c>Start</c> 里那段"顺序反了"的说明）。
        /// 它是 <see cref="ISurfaceFrameEncoder"/> 时，本方法<b>不自己挑屏</b>，
        /// 而是把门面挑好的结论搬过来 —— 理由见 <see cref="PrepareFromGpuSurface"/>。
        /// </param>
        public bool Prepare(
            Func<IScreenCapturer> capturers,
            string? screenId,
            IVideoFrameEncoder encoder,
            out string failure)
        {
            if (encoder is ISurfaceFrameEncoder surface)
            {
                return PrepareFromGpuSurface(surface, out failure);
            }

            return PrepareFromMemoryCapturer(capturers, screenId, out failure);
        }

        /// <summary>
        /// <b>GPU 那条路：屏号以门面为准</b>。
        ///
        /// <para>
        /// 🔥🔥 <b>这一段是本批最容易写错、也最不容易被发现的地方</b>。
        /// 两个后端各自挑屏，而<b>挑的规则刻意不同</b>：
        /// </para>
        /// <list type="bullet">
        ///   <item>内存路径（GDI 那条）：<b>优先虚拟桌面</b>
        ///         —— GDI 能把多块屏拼成一张，所以"全部显示器"是它能做的；</item>
        ///   <item>GPU 门面：<b>优先主屏</b> —— DDA 一次只能抓<b>一个 output</b>，
        ///         虚拟桌面它做不了（它自己的 <c>Prepare</c> 会以"环境问题"为由拒绝）。</item>
        /// </list>
        ///
        /// <para>
        /// ⇒ 若宿主在这里<b>自己按"优先虚拟桌面"挑一次</b>、门面又按"优先主屏"挑一次，
        /// 多屏机上<b>必然挑到不同的屏</b>。两个组件各自都"挑对了"，
        /// 于是没有任何一处会报错、日志上也看不出异常 ——
        /// 症状是 <b>看着 A 的屏幕、实际操作 B</b>。这是本方案里最贵的一类 bug。
        /// </para>
        ///
        /// <para>
        /// ⇒ 修法一句话：<b>门面挑，宿主搬</b>。门面是唯一同时知道"DDA 的能力边界"
        /// 与"这一帧到底从哪块屏来"的那个组件。
        /// </para>
        ///
        /// <para>
        /// ⚠️ <b>这里刻意不接受 <c>screenId</c></b>：门面在建的时候就已经按它挑过了
        /// （<c>Start</c> 把它放进了 <see cref="VideoEncoderRequest"/>）。
        /// 在这里再挑一次，就是要修的那个 bug 本身。
        /// </para>
        /// </summary>
        private bool PrepareFromGpuSurface(ISurfaceFrameEncoder surface, out string failure)
        {
            if (string.IsNullOrEmpty(surface.ScreenId))
            {
                // 走到这里说明门面"说自己建好了、却没有屏号"，是它自己的不变式破了。
                // 静默用一个空屏号会让抓屏在运行期才失败，而那时已经在同步返回值之外。
                failure = "GPU 编码器报告已就绪，却没给出它抓的是哪块屏 —— 这是它自己的状态错误，"
                          + "请改用 MJPEG 路径（或把这个现象报给开发）。";

                return false;
            }

            _capturer = surface.Capturer;

            ScreenId = surface.ScreenId;
            ScreenName = surface.ScreenName;

            var (width, height) = surface.FrameSize;

            Width = width;
            Height = height;

            // 🔥 左上角偏移**不能从门面拿**（它不暴露这个字段），必须按屏号回查一次枚举。
            //    而这一次枚举是**无状态**的（`WindowsScreenEnumerator.Enumerate`），
            //    不碰 GPU 设备、也不会重建 duplication —— 所以它破坏不了门面那套管线。
            //    ⚠️ 反过来，"拿 `CaptureSurfaceAsync` 去试抓一帧量个尺寸"是**禁止**的：
            //       门面对"不是它选的屏"会重建整条 duplication，把刚建好的管线拆掉。
            ScreenDescriptor? target;

            try
            {
                target = surface.Capturer
                    .EnumerateScreens()
                    .FirstOrDefault(
                        s => string.Equals(s.Id, surface.ScreenId, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                failure = $"枚举屏幕失败（GPU 路径）：{ex.Message}";

                return false;
            }

            if (target is null)
            {
                // 理论上到不了（屏是门面刚枚举出来的）。但**输入坐标的换算全靠这两个值**
                // —— 少了 Left，单屏机看不出问题、多屏机上鼠标整体错位 ——
                // 所以宁可在这里拒绝，也不要拿 0 当"就是原点"。
                failure = $"GPU 编码器抓着屏「{surface.ScreenId}」，但重新枚举时找不到它"
                          + "（显示器刚被拔掉？）—— 输入坐标会算错，拒绝开这一路。";

                return false;
            }

            _screenLeft = target.Left;
            _screenTop = target.Top;

            // GPU 路径目前**只会**挑到一块具体的屏（请求虚拟桌面时门面在建之前就拒了，
            // 见 WindowsGpuFrameEncoder.Prepare）。这里照样赋值而不是写 false：
            // 将来硬编支持拼接时，"这一路是拼接"这件事必须自动跟着成立，
            // 而不是再回来找一个写死 false 的地方。
            IsVirtualDesktop = target.IsVirtualDesktop;

            failure = string.Empty;

            return true;
        }

        /// <summary>
        /// 内存路径：自己建抓屏器、自己挑屏（<b>优先虚拟桌面</b>，见上面那段对照）。
        /// </summary>
        private bool PrepareFromMemoryCapturer(
            Func<IScreenCapturer> capturers,
            string? screenId,
            out string failure)
        {
            try
            {
                _capturer = capturers();
            }
            catch (Exception ex)
            {
                failure = $"创建抓屏器失败：{ex.Message}";

                return false;
            }

            IReadOnlyList<ScreenDescriptor> screens;

            try
            {
                screens = _capturer.EnumerateScreens();
            }
            catch (Exception ex)
            {
                failure = $"枚举屏幕失败：{ex.Message}";

                return false;
            }

            if (screens.Count == 0)
            {
                // 🔥 这是现场最需要看到的一句话：抓屏与注入只能在**有桌面的会话**里做。
                //    没人登录时这台机器上根本没有可抓的桌面 —— 那不是"软件坏了"。
                failure = "这台机器上枚举不到任何显示器 —— 说明**当前没有可抓的桌面**"
                          + "（最可能的原因是没人登录，或这个进程不在用户会话里）。";

                return false;
            }

            var wanted = string.IsNullOrWhiteSpace(screenId)
                ? screens.FirstOrDefault(s => s.IsVirtualDesktop) ?? screens[0]
                : screens.FirstOrDefault(s => string.Equals(s.Id, screenId, StringComparison.OrdinalIgnoreCase));

            if (wanted is null)
            {
                failure = $"找不到屏「{screenId}」。本机可抓的屏："
                          + string.Join("、", screens.Select(s => $"{s.Id}（{s.Name}）"));

                return false;
            }

            ScreenId = wanted.Id;
            ScreenName = wanted.Name;
            Width = wanted.Width;
            Height = wanted.Height;
            IsVirtualDesktop = wanted.IsVirtualDesktop;
            _screenLeft = wanted.Left;
            _screenTop = wanted.Top;

            failure = string.Empty;

            return true;
        }

        /// <summary>起推流循环 + 输入循环（<b>都不阻塞</b>）。</summary>
        /// <param name="encoder">
        /// 已经建好、并且已经<b>参与过挑屏</b>的那个编码器（见 <see cref="Prepare"/>）。
        /// <b>所有权在这里转移给会话</b>：会话结束时由 <c>LoopAsync</c> 的 finally 释放
        /// —— 托盘的调用方<b>不要</b>再放一次。
        /// </param>
        public void Begin(
            CancellationToken hostToken,
            int fps,
            IVideoFrameEncoder encoder,
            IInputInjector? injector,
            Action<string> log,
            Func<IAudioCapturer>? audioCapturers = null,
            Func<IAudioEncoder>? audioEncoders = null,
            string? packaging = null,
            IDisplayWakeLock? displayWakeLock = null)
        {
            TargetFps = fps;
            _encoder = encoder;
            _injector = injector;
            _displayWakeLock = displayWakeLock;
            StartedAt = DateTimeOffset.UtcNow;

            // 🔥 中心说了算（`command.Packaging`），中心没说才用宿主默认。
            //    顺序反了会变成"中心点名要带外、被控端照自己的默认跑带内"——
            //    而两边的日志都看不出异常，只有浏览器那边解不出来。
            //    ⚠️ 归一化在这里做（而不是在 Start 里）：会话是那个能把它印到日志上的地方。
            Packaging = RemoteStreamPackagings.Normalize(
                string.IsNullOrWhiteSpace(packaging) ? _defaultPackaging : packaging);

            // 🔥 单调时钟的原点在这里取，而且**必须在起任何循环之前** ——
            //    晚取一步，音视频两个时间戳就会差出一个建会话的耗时（几十毫秒），
            //    而那个偏差会被前端原样当成"音画不同步"。
            _startQpc = Stopwatch.GetTimestamp();

            // 🔥 "最后一次抓到新画面"从会话开始那一刻起算（M5.4-c② 步 7）：
            //    从开流起桌面就没动过时，静止时长要能从 0 一路长上去 ——
            //    留 0 会让它在头一瞬间答"问不到"，而那一刻恰恰最该说"它真的没动"。
            Volatile.Write(ref _lastNewFrameAt, _startQpc);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            _cts = cts;

            _ = Task.Run(() => LoopAsync(cts.Token, log), CancellationToken.None);

            // 🔥 输入循环**与推流并行起**，不等码流接上：
            //    两条管道各等各的接收端。让输入去等"画面先接上"会多一个
            //    "输入到底有没有在等"的状态，而它对用户毫无意义 —— 用户要的是点得动。
            if (injector is not null)
            {
                _ = Task.Run(() => InputLoopAsync(cts.Token, injector, log), CancellationToken.None);
            }

            // ── 音频循环（M5.3）──
            //
            // 🔥 与输入同样的理由并行起、各等各的接收端：音频管道接不上**不该影响画面**，
            //    而在同一条循环里串起来，就会变成"音频卡住 = 画面也卡住"。
            //
            // ⚠️ 任一工厂为 null ⇒ 连循环都不起（也就不会建管道）：
            //    让服务侧的连接**立刻**失败并如实报"被控端没有音频"，
            //    而不是拿一个 5 秒超时去换一个更含糊的结论。
            if (audioCapturers is not null && audioEncoders is not null)
            {
                _ = Task.Run(
                    () => AudioLoopAsync(cts.Token, audioCapturers, audioEncoders, log),
                    CancellationToken.None);
            }
            else
            {
                SetAudioState(AgentAudioStates.Unavailable, "本进程没有音频采集能力（被控端未装配音频模块）。");
            }
        }

        /// <summary>请求停止（可重复调用）。</summary>
        public void Stop()
        {
            try
            {
                _cts?.Cancel();
            }
            catch
            {
                // 忽略。
            }
        }

        public RemoteStreamStatus Snapshot()
        {
            var frames = Interlocked.Read(ref _framesSent);

            lock (_clock)
            {
                var seconds = _clock.Elapsed.TotalSeconds;

                return new RemoteStreamStatus(
                    SessionId,
                    ScreenId,
                    ScreenName,
                    Width,
                    Height,
                    TargetFps,
                    seconds > 0.5 ? Math.Round(frames / seconds, 1) : 0,
                    frames,
                    Interlocked.Read(ref _bytesSent),
                    Interlocked.Read(ref _dropped),
                    StartedAt,
                    _lastError,
                    AudioState: _audioState,
                    AudioReason: _audioReason,
                    AudioFramesSent: Interlocked.Read(ref _audioFramesSent),
                    AudioBytesSent: Interlocked.Read(ref _audioBytesSent),
                    AudioDropped: Interlocked.Read(ref _audioDropped),
                    DesktopIdleMs: DesktopIdleMs);
            }
        }

        /// <summary>
        /// 桌面已经静止了多久（毫秒；<c>null</c> = 还说不出来）。
        /// <para>
        /// 🔥 它是<b>抓屏器的结论</b>（"没有新帧"），不是"多久没发帧" ——
        /// 后者在"编码失败"时也会变大，而那与桌面有没有变毫无关系。
        /// </para>
        /// <para>
        /// ⚠️ <b>内存（MJPEG）路径上它恒接近 0</b>：那条路每轮必出一帧，
        /// 抓屏器没有"桌面有没有变"这个概念。那里的 0 含义是"有帧在流"，
        /// 不是"我们检测到桌面在变"（见 <see cref="RemoteStreamStatus.DesktopIdleMs"/>）。
        /// </para>
        /// </summary>
        public long? DesktopIdleMs
        {
            get
            {
                var last = Interlocked.Read(ref _lastNewFrameAt);

                // 还没有起点（会话还没 Prepare）⇒ "问不到"，而不是"静止了 0 毫秒"。
                if (last == 0) return null;

                var elapsed = Stopwatch.GetElapsedTime(last);

                // 防御：QPC 与本地读数之间不该出现负值，真出现就按 0 报 ——
                // 页面上画出"-3 秒"是比"0"更糟的答案。
                return elapsed <= TimeSpan.Zero ? 0 : (long)elapsed.TotalMilliseconds;
            }
        }

        /// <summary>音频通道状态（见 <see cref="AgentAudioStates"/>）；从未评估过时为 <c>null</c>。</summary>
        public string? AudioState => _audioState;

        /// <summary>
        /// 记一次音频状态。
        /// <para>
        /// ⚠️ 与输入那条同一条规矩：<b>只有"不可用"才留原因</b> ——
        /// 就绪之后还挂着上一次的失败原因，页面上会变成
        /// "音频正常：本机没有可用的音频输出设备"这种自相矛盾的一句话。
        /// </para>
        /// </summary>
        private void SetAudioState(string state, string? reason)
        {
            _audioState = state;
            _audioReason = RemoteChannelStates.ReasonToKeep(state, AgentAudioStates.Unavailable, reason);
        }

        /// <summary>
        /// 把一个 QPC 值换算成<b>距会话开始多少毫秒</b>（会话单调时钟）。
        /// <para>
        /// 音视频两个帧头里的 <c>CapturedMonoMs</c> 都出自这里，所以它们天然同源 ——
        /// 前端只需要做一次减法就能知道"这一段音频比那一帧画面早/晚多少"。
        /// </para>
        /// </summary>
        private long MonotonicMs(long qpcTicks)
            => (long)((qpcTicks - _startQpc) * 1000.0 / Stopwatch.Frequency);

        /// <summary>
        /// 在关键帧上刷新参数集缓存（<c>_codecString</c> / <c>_codecDescription</c>）。
        ///
        /// <para>
        /// 🔥🔥 <b>参数集从"这一帧自己的码流"里解，而不是从编码器的某个属性上问。</b>
        /// 理由不是省事，是<b>正确性</b>：带外那份 avcC 必须与码流里的参数集<b>是同一份</b>
        /// —— 从别处问来的 SPS 完全可能与流里的不同（换分辨率、编码器重建之后尤其如此），
        /// 而"配置与码流不一致"这种错<b>不会报错</b>，只会花屏。
        /// </para>
        ///
        /// <para>
        /// 🔸 <b>它顺带回答了方案里一处悬而未决的矛盾</b>：本仓有两份注释，一份说
        /// "MF 在每个关键帧都重复 SPS/PPS"，另一份说"落盘的 30 帧里 SPS 只出现 1 次、
        /// 所以带内这条路不成立"。后一句是<b>从一个只有一个关键帧的样本里读出来的</b>
        /// （那批实测 30 帧 = 29 P + 1 I），所以它证明不了任何事。
        /// 而这个计数（<c>_keyframesWith/WithoutParameterSets</c>）会把它变成可查的事实。
        /// </para>
        ///
        /// <para>
        /// ⚠️ 抓不到参数集<b>不当作故障</b>：沿用上一次那一份（参数集不会无缘无故变），
        /// 只在第一次发生时说一句。真要紧的是"<b>第一个</b>关键帧就没有" ——
        /// 那种情况下观看端连一次正确的 configure 都做不了，而症状是"一直不出画面"。
        /// </para>
        /// </summary>
        private void TryRefreshParameterSets(ReadOnlySpan<byte> accessUnit, VideoCodec codec, Action<string> log)
        {
            if (TryParseParameterSets(accessUnit, codec, out var codecString, out var description))
            {
                _keyframesWithParameterSets++;

                _codecString = codecString;
                _codecDescription = description;

                return;
            }

            _keyframesWithoutParameterSets++;

            if (_warnedParameterSetsMissing) return;

            _warnedParameterSetsMissing = true;

            log($"[Stream] 会话 {SessionId}：⚠️ 出现了一个<b>自己的码流里没有完整 SPS/PPS</b> 的关键帧"
                + $"（有参数集的关键帧 {_keyframesWithParameterSets} 个，没有的 {_keyframesWithoutParameterSets} 个）。"
                + "这不是故障 —— 我们沿用上一次解出来的那一份；"
                + "真正要担心的是**第一个**关键帧就缺（那时观看端连一次正确的 configure 都做不了）。");
        }

        /// <summary>
        /// 从一段 Annex-B 里解出 <b>codec 串 + avcC/hvcC</b>（纯函数，不碰任何会话状态）。
        /// </summary>
        /// <remarks>
        /// ⚠️ 一律按<b>带外</b>的采样入口名去解（<c>avc1</c> / <c>hvc1</c>）：
        /// 解出来的 avcC 与 profile/level 串<b>与模式无关</b>，带内那一份只是把前缀换成
        /// <c>avc3</c> / <c>hev1</c>（见 <c>VideoSampleEntries.ToInBand</c>）。
        /// 分开解两次会得到两份"看起来一样"的东西 —— 而它们一旦不一致就是花屏。
        /// </remarks>
        private static bool TryParseParameterSets(
            ReadOnlySpan<byte> accessUnit,
            VideoCodec codec,
            out string? codecString,
            out byte[]? description)
        {
            var sampleEntry = codec == VideoCodec.Hevc
                ? VideoSampleEntries.Hvc1
                : VideoSampleEntries.Avc1;

            var sets = VideoParameterSetReader.TryRead(accessUnit, codec, sampleEntry);

            if (sets is null)
            {
                codecString = null;
                description = null;

                return false;
            }

            codecString = sets.CodecString;
            description = sets.Description;

            return true;
        }

        /// <summary>
        /// 一次也没从码流里解出参数集时的<b>兜底</b>：问编码器自己声明的那份
        /// （<c>MF_MT_MPEG_SEQUENCE_HEADER</c>）。
        ///
        /// <para>
        /// 🔥 它<b>只做兜底、不做首选</b>：那是"编码器说自己用的参数集"，
        /// 而码流里真正用的那一份才是唯一权威。首选它就会出现"配置与码流不一致"
        /// 这种不报错的错；只做兜底则是"没有更好的来源了，先用它把 configure 撑起来"。
        /// </para>
        /// <para>
        /// ⚠️ 它<b>不动那两个关键帧计数</b>：来源不是关键帧，把它算进去会让
        /// "有多少关键帧自带参数集"这个读数失真 —— 而那个读数正是我们要拿去
        /// 回答"带内这条路成不成立"的证据。
        /// </para>
        /// </summary>
        private bool TryRescueParameterSetsFromEncoder(
            ISurfaceFrameEncoder surfaceEncoder,
            VideoCodec codec,
            Action<string> log)
        {
            if (!surfaceEncoder.TryReadParameterSets(out var sequenceHeader)) return false;

            if (!TryParseParameterSets(sequenceHeader, codec, out var codecString, out var description))
            {
                return false;
            }

            _codecString = codecString;
            _codecDescription = description;

            log($"[Stream] 会话 {SessionId}：码流里没解出参数集，已改用编码器声明的"
                + $" MF_MT_MPEG_SEQUENCE_HEADER（{sequenceHeader.Length} 字节）作为配置来源。");

            return true;
        }

        // ── 主循环 ──

        private async Task LoopAsync(CancellationToken cancellationToken, Action<string> log)
        {
            NamedPipeServerStream? pipe = null;

            lock (_clock) _clock.Restart();

            // 🔥 M7.6：**先**把"别再息屏"这件事做掉，再做别的。
            //    顺序不能反 —— 建管道 ＋ 等接收端可能要好几秒，而系统的关屏计时
            //    根本不等人：那几秒足够它把屏幕关掉，于是用户连上来看到的第一个画面就是黑的。
            var wakeHeld = HoldDisplayAwake(log);

            try
            {
                pipe = new NamedPipeServerStream(
                    RemoteStreamProtocol.PipeNameForSession(SessionId),
                    PipeDirection.Out,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,

                    // 缓冲区给到一帧的量级：默认 4 KB 会把 300 KB 的帧切成几十次写入，
                    // 每次都要一次内核往返。这是本机管道，放大缓冲几乎不花代价。
                    //
                    // ⚠️ **它不是容量闸门**：命名管道是字节流，一帧比这个缓冲大
                    //    只是被系统分几次搬运，不会被拒绝（真正的上限是
                    //    RemoteStreamProtocol.MaxPayloadBytes，在写之前就判掉了）。
                    //    所以这里<b>不需要</b>跟着那个上限一起抬 —— 抬了等于每路会话
                    //    白占几 MB 内核池，而它换来的只是少几次拷贝。
                    inBufferSize: 0,
                    outBufferSize: 512 * 1024);

                // ── 等接收端 ──
                //
                // 🔥 这一步**必须有超时**：没人来接就收摊。
                //    否则托盘会一直开着抓屏循环空转，而它自己毫无察觉 ——
                //    表现是"没人看它，可这台机器一直很卡"。
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(RemoteStreamProtocol.ConnectTimeoutMs);

                    try
                    {
                        await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        _lastError = $"等了 {RemoteStreamProtocol.ConnectTimeoutMs} 毫秒也没有接收端连上来。";

                        log($"[Stream] 会话 {SessionId} 作废：{_lastError}");

                        return;
                    }
                }

                log($"[Stream] 接收端已连接（会话={SessionId}），开始推帧。");

                await PumpAsync(pipe, cancellationToken, log).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _lastError ??= "已停止。";
            }
            catch (Exception ex)
            {
                // ⚠️ 绝不外抛（见类注释）。
                _lastError = $"{ex.GetType().Name}：{ex.Message}";

                log($"[Stream] 会话 {SessionId} 推流中断：{_lastError}");
            }
            finally
            {
                lock (_clock) _clock.Stop();

                // 🔥 会话一结束就放开：一条流看完之后还替它拦着"别关屏"，
                //    等于把一台机器的电源策略改了却没人知道。
                if (wakeHeld) ReleaseDisplayAwake(log);

                try
                {
                    if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 忽略。
                }

                try
                {
                    _encoder?.Dispose();
                }
                catch
                {
                    // 忽略。
                }
            }
        }

        /// <summary>
        /// 会话期间阻止被控端关屏 / 起屏保 / 睡眠（M7.6）。
        ///
        /// <para>
        /// 🔥 <b>为什么这件事非做不可</b>：没人碰的机器静置几分钟后，系统会按电源计划
        /// 关掉显示器或起屏保。DDA 这时仍然"抓得到" —— 抓到一片纯黑，于是
        /// <b>帧在流、码率正常、两端日志干净，画面全黑</b>；被控端有人动一下就恢复。
        /// 这个形态在本仓被误判过两轮（先怀疑过抓屏坏了，又怀疑过编码器）。
        /// </para>
        ///
        /// <para>
        /// ⚠️ 任何失败都只记一行日志，<b>绝不让开流失败</b>：这条能力是"让画面别黑"，
        /// 它自己坏掉不该升级成"远控用不了"。日志里必须带上
        /// "后果是什么"，否则现场看到"没能阻止息屏"只会当成一句无关紧要的警告。
        /// </para>
        /// </summary>
        private bool HoldDisplayAwake(Action<string> log)
        {
            if (_displayWakeLock is null) return false;

            try
            {
                var ok = _displayWakeLock.Acquire(out var note);

                log(ok
                    ? $"[Stream] 会话 {SessionId}：{note}"
                    : $"[Stream] 会话 {SessionId}：没能阻止被控端息屏 —— {note}"
                        + "｜画面照常推；但静置一段时间后若这台机器关屏或起屏保，"
                        + "远端看到的会是一片全黑（而被控端一动就恢复）");

                return ok;
            }
            catch (Exception ex)
            {
                // 与推流循环同一条纪律：这里抛出去会把开流带停。
                log($"[Stream] 会话 {SessionId}：申请唤醒锁时抛出 {ex.GetType().Name}：{ex.Message}（推流照常）");

                return false;
            }
        }

        private void ReleaseDisplayAwake(Action<string> log)
        {
            if (_displayWakeLock is null) return;

            try
            {
                _displayWakeLock.Release(out var note);

                log($"[Stream] 会话 {SessionId}：{note}");
            }
            catch (Exception ex)
            {
                log($"[Stream] 会话 {SessionId}：释放唤醒锁时抛出 {ex.GetType().Name}：{ex.Message}");
            }
        }

        private async Task PumpAsync(
            NamedPipeServerStream pipe,
            CancellationToken cancellationToken,
            Action<string> log)
        {
            var capturer = _capturer!;
            var encoder = _encoder!;

            // 🔥 **两条路只有"抓"这一步不同**（M5.4-b 起）：内存路径抓 `CapturedFrame`（字节），
            //    GPU 路径抓 `CapturedSurface`（纹理）。分一次叉就够了 —— 因为"编"这一步
            //    各自的方法签名不同，编译器在**这一层**替我们把关：
            //    `TryEncode` 只认 CPU 帧，`TryEncodeSurface` 只认纹理。
            //
            //    ⚠️ 为什么不在接口里合成一个 `TryEncode(object)`：那样"像素在哪"就变成
            //       一个运行时才知道的问题，而两条路真正的差别恰恰在**编译期**就能定下来。
            //       详见 `IVideoFrameEncoder` 的注释。
            var surfaceEncoder = encoder as ISurfaceFrameEncoder;
            var memoryEncoder = encoder as IFrameEncoder;

            // 🔥 **两条路必须恰好"有一个能走"。**
            //    都为空 = 拿到了一个我们谁也不认识的编码器 —— 这时候**不能安静地推空流**：
            //    症状会是"推流已开始、一帧都没有、日志干净"。当场收场，把话说明白。
            if (surfaceEncoder is null && memoryEncoder is null)
            {
                _lastError = $"编码器「{encoder.GetType().Name}」既不是 GPU 纹理路径"
                             + $"（{nameof(ISurfaceFrameEncoder)}）也不是内存帧路径"
                             + $"（{nameof(IFrameEncoder)}）—— 本宿主不认识它，无法推流。";

                log($"[Stream] 会话 {SessionId} 收场：{_lastError}");

                return;
            }

            var targetPeriod = TimeSpan.FromSeconds(1.0 / TargetFps);
            var pacedFps = TargetFps;
            var consecutiveFailures = 0L;

            // 🔥 **下一帧要不要强制关键帧。**
            //
            //   正常情况下永远是 false —— 让编码器按自己的 GOP 走（GopSize 默认 60）。
            //   有**两个**来源会把它置 true，而它们对应两种完全不同的丢帧：
            //     ① **本地**：某一帧因为超过管道上限被丢掉（见 OversizeGiveUp）；
            //     ② **别处**：中心重组分片失败 ⇒ 从服务经 <see cref="RequestKeyframe"/>
            //        送回来的请求（M5.4-c① 步 5）。
            //   为什么两者都非补不可：H.264 的 P 帧参考前面的帧，丢一帧等于在参考链上
            //   戳一个洞，从那一刻起观看端解出来的每一帧都是花的 —— **而解码器不会报错**。
            //   所以"丢一帧"必须配一次"立刻补一个关键帧"，把恢复代价从"最多一个 GOP"
            //   压到"下一帧"。
            //
            //   ⚠️ ②之所以必须**跨进程**绕一圈（中心 → 服务 → 托盘）而不是本地判断：
            //      丢帧发生在中心，被控端这边**什么都不知道** —— 它只是把每一片都发出去了。
            var wantKeyframe = false;

            // 🔥 **参数集（SPS/PPS）的缓存放在字段上**（`_codecString` / `_codecDescription`）：
            //    它要跨帧存活 —— 参数集只可能在关键帧上变，而帧头里那份要一直有。
            //    ⚠️ 两个字段**必须成对刷新、指向同一份 SPS**：`_codecString` 是从那份 SPS
            //       反推出来的 profile/level 串（写错的表现是 `NotSupportedError`，
            //       看起来像"浏览器不支持 H.264"），`_codecDescription` 是那份 SPS/PPS 编出的 avcC。
            //       两者指向不同的字节 = "配置里说的与码流里的对不上"，同样不报错、只是花屏。

            // 只量"推流阶段"的时长：上面等接收端那一步最多可能耗掉 ConnectTimeoutMs，
            // 把它算进来会让真正留给我们出第一帧的时间变短，而那是两件不同的事。
            var pumpStartedAt = Stopwatch.GetTimestamp();

            // 🔥 "画面没有新帧"连续发生了多少次（**不是**失败次数，见下面那段）。
            var idleTicks = 0L;

            /// 上一次把"没有新帧"打进日志的时刻（0 = 还没打过）。
            /// <para>
            /// ⚠️ 节流**按时间**、不按次数（M5.4-c② 步 8）：退避之后每 tick 的时长会变，
            /// 用"第 N 次"节流会让同一个 300 次从 10 秒变成两分半。
            /// </para>
            var idleLoggedAt = 0L;

            /// 此刻是否处在"静止退避"里（只在翻转时各说一句，见下面那段）。
            var backedOff = false;

            /// 进入退避的时刻（只用于日志里那句"退避了 X 秒"）。
            var backedOffAt = 0L;

            /// <summary>
            /// 桌面<b>重新开始变化</b> ⇒ 立刻回到目标帧率（M5.4-c② 步 8）。
            /// <para>
            /// 🔥 这条日志必须与"退避到 N fps"<b>成对</b>出现：只有前者的话，
            /// 现场永远不知道这台机器还在不在省 CPU（也就不知道退避有没有卡住不回来）。
            /// </para>
            /// </summary>
            void ResumeTargetPacing()
            {
                if (!backedOff) return;

                backedOff = false;

                log($"[Stream] 会话 {SessionId}：桌面恢复变化，抓屏轮询立刻回到目标帧率 {TargetFps} fps"
                    + $"（此前退避了 {Stopwatch.GetElapsedTime(backedOffAt).TotalSeconds:0.0} 秒）。");
            }

            // 一帧都没产出过、且已经超了预算 ⇒ 这路流起不来，该收场。
            //
            // 见 RemoteStreamHost.StartupFrameBudget 的注释 ——
            // 没有它就会出现"中心已经把会话收掉了、被控端却一直空转"。
            bool StartupBudgetExhausted(string reason)
            {
                if (Interlocked.Read(ref _framesSent) > 0) return false;

                if (Stopwatch.GetElapsedTime(pumpStartedAt) <= StartupFrameBudget) return false;

                _lastError =
                    $"开始推流后 {(int)StartupFrameBudget.TotalSeconds} 秒内一帧都没能产出，"
                    + $"判定为这路流起不来，已收场。最后一次失败：{reason}";

                log($"[Stream] 会话 {SessionId} 收场：{_lastError}");

                return true;
            }

            // 单帧超过管道上限时的处置。<c>true</c> = 该收场了。
            //
            // 🔥🔥 **这里修的是一个"症状指向相反"的老毛病**：早先"帧太大"是**抛异常**，
            //    而外面那个 catch 把任何写异常都当成"接收端已断开"⇒ return，整条流结束。
            //    于是现场看到的是「码流写不出去，接收端已断开」—— 而接收端好端端地在等着，
            //    真因只是某一帧比上限大了几十 KB。
            //    现在它是一次**可读的丢帧**：丢这一帧、下一帧补关键帧、继续跑。
            bool OversizeGiveUp(int payloadBytes, bool isKeyframe)
            {
                var dropped = Interlocked.Increment(ref _dropped);
                var oversize = Interlocked.Increment(ref _droppedOversize);

                // ⚠️ 这一帧**不占序号**（序号是在下面构造帧头时才取的，而丢帧根本走不到那儿）。
                //
                //    🔥 这一点是**实测出来的，不是设计出来的**：序号的含义是
                //       「真的发出去了第几帧」，所以它<b>不该</b>被丢帧撑出空洞 ——
                //       去看"有没有丢"要看 `Dropped` 那一个字段（它随每一帧带过去，
                //       是中心与页面上唯一能看见被控端本地丢帧的地方）。
                //       曾经想当然地以为"丢帧会在序号上留一个洞"，实测 `Sequence` 是连续的。
                long streak = 0;

                if (isKeyframe)
                {
                    streak = Interlocked.Increment(ref _oversizeKeyframeStreak);
                }

                // 日志节流（与抓屏/编码那两条一致）：一秒钟一条会把托盘日志刷爆，
                // 反而盖住真问题。但第一条一定要出来 —— 那是"开始丢"的时刻。
                if (oversize % 30 == 1)
                {
                    _lastError =
                        $"单帧 {RemoteStreamLimits.Describe(payloadBytes)} 超过管道上限 "
                        + $"{RemoteStreamLimits.Describe(RemoteStreamProtocol.MaxPayloadBytes)}"
                        + $"（{(isKeyframe ? "关键帧" : "普通帧")}），"
                        + "已丢弃并为下一帧强制关键帧。";

                    log($"[Stream] {_lastError}（累计丢帧 {dropped}，其中超限 {oversize}）");
                }

                if (!isKeyframe) return false;

                // ⚠️ 关键帧过不去 = 观看端<b>永远拿不到参考帧</b>，后面每一个 P 帧都会花。
                //    这时"再要一个关键帧"是唯一出路，但如果每次都超限，那就是一个
                //    **不可能完成的循环** —— 如实收场，别让被控机一直抓一直编一直丢。
                if (streak < OversizeKeyframeGiveUp) return false;

                _lastError =
                    $"连续 {streak} 个关键帧都超过管道上限（最近一次 {RemoteStreamLimits.Describe(payloadBytes)}"
                    + $" > {RemoteStreamLimits.Describe(RemoteStreamProtocol.MaxPayloadBytes)}）"
                    + "—— 这个分辨率 / 画质组合传不过去：观看端拿不到参考帧，画面不可能出来。"
                    + "⚠️ 分片（M5.4-c①）已经在链路上做完了，所以走到这里说明这一帧超过了"
                    + "**单帧防呆上限**（而不是「一条消息装不下」）—— 唯一的出路是在被控端降分辨率。已收场。";

                log($"[Stream] 会话 {SessionId} 收场：{_lastError}");

                return true;
            }

            // ── 静止：两条路共用的一段（M5.4-c② 步 8；软编路径在 M5.5 补上）──
            //
            // 🔥 **"画面没有新内容"不是失败、不是丢帧，也不是"这条路坏了"。**
            //    把它当成失败，会立刻得到两个错误结论：
            //      ① `Dropped` 开始涨 ⇒ 页面报"掉了很多帧"，而实际一帧都没丢
            //         （同族的错误见 §3.6：**计数错了，排查方向就反了**）；
            //      ② 启动预算按"从没出过帧"判定 ⇒ 一台**静止画面**的机器
            //         （展厅里定格在一张图上、没人动鼠标）会在 30 秒后被收场，
            //         而它其实完全正常。
            //
            // 🔥 **为什么这里要退避**：展厅里那种"定格在一张图上、一停几小时"的机器上，
            //    抓屏是白白每秒跑 12–60 次（GPU 路每次都要碰一遍桌面合成，
            //    软编路更贵 —— 还要整帧比对 + JPEG 编码）。
            //
            // ⚠️ **为什么"多久静止"读 `_lastNewFrameAt` 而不是本地的 `idleTicks`**：
            //    退避之后每 tick 的**时长本身在变**（16 毫秒 → 100 毫秒），
            //    拿"第几次"当判据会让阈值随退避漂 —— 同一个"静止 2 秒"
            //    在退避前后对应完全不同的 tick 数。
            //
            // 🔴🔴 **降帧 ≠ 停止，两道保险必须一起在**：
            //      ① `RemoteStreamPacing.MaxIdlePeriod`（≤1 秒）—— 抓屏轮询**永远还在跑**，
            //         所以"最后有新画面"这件事随时能被发现；
            //      ② 服务侧**每秒一次**的计数上报（`RemoteSessionCoordinator`）——
            //         中心一直听得见这台机器。
            //    ⚠️ 优化掉任何一条，展厅里定格在一张图上的机器就会被判成死机器
            //       （`REF-M5 §14.14` 已经因为这件事翻过一次车）。
            //
            // <param name="what">日志里的主语：GPU 路是"抓屏"，软编路是"帧比对"。</param>
            // <param name="roundStartedAt">
            // 本轮开始的那个 <c>tick</c>。<b>不能省</b>：抓屏自己已经等过一遍了
            // （<c>AcquireNextFrame</c> 15 ms），再整整睡一个周期就是一次错过付两次钱，
            // 而 60 fps 下这种错过是常态（M7.4 §12.3 实测 22/278 轮）。
            // </param>
            // <returns>本轮该等多久（调用方 <c>DelayAsync</c> 之后 <c>continue</c>）。</returns>
            TimeSpan HandleStaticFrame(string what, long roundStartedAt)
            {
                idleTicks++;

                var idleFor = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastNewFrameAt));

                var idlePeriod = RemoteStreamPacing.PollPeriod(TargetFps, idleFor);

                if (idlePeriod > targetPeriod && !backedOff)
                {
                    backedOff = true;
                    backedOffAt = Stopwatch.GetTimestamp();

                    log($"[Stream] 会话 {SessionId}：桌面已静止 "
                        + $"{idleFor.TotalSeconds:0.0} 秒，抓屏轮询从 {TargetFps} fps "
                        + $"退避到 {RemoteStreamPacing.BackoffFps(TargetFps)} fps"
                        + "（这只是省 CPU，**不是停止**：轮询仍在跑，且服务每秒仍在上报状态，"
                        + "中心不会把它判成死机器）。");
                }

                // ⚠️ 日志按**时间**节流（10 秒一条），不是按次数：
                //    退避之后"300 次"这条判据的物理含义会从 10 秒变成 2.5 分钟，
                //    于是"刚静止"和"静止半小时"在日志上长得一模一样。
                //    但第一条必须立刻出来 —— 那是"它开始不动了"的时刻。
                if (idleLoggedAt == 0 || Stopwatch.GetElapsedTime(idleLoggedAt) >= IdleLogPeriod)
                {
                    idleLoggedAt = Stopwatch.GetTimestamp();

                    log($"[Stream] 会话 {SessionId}：{what}连续 {idleTicks} 次没有新帧"
                        + "（桌面内容与指针都没变）—— 这是正常现象，**没有丢帧**，"
                        + "观看端停在上一张画面上。");
                }

                // 🔥 **把启动预算的时钟重置**，这是本段里唯一一处"反直觉但必须"的动作：
                //    StartupFrameBudget 的判据是"从开始到现在一帧都没出过" ⇒ 不重置的话，
                //    一个静止桌面跑满 30 秒之后**只要出现一次偶发失败**，就会立刻被收场
                //    （预算早就超了、帧数还是 0）。而那时候我们并没有任何证据说这路流起不来。
                //    ⇒ 重置的含义是："预算只统计'我们有东西可发却发不出去'的那段时间"。
                pumpStartedAt = Stopwatch.GetTimestamp();

                // 🔥 **扣掉这一轮已经花掉的时间**（M7.4 §12.3）：抓屏那条路自己就等了 15 ms，
                //    再整整睡一个周期 ⇒ 一次"没抢到新画面"要花 ~32 ms 且一帧都不产。
                //    实测 5 秒 278 轮里 22 轮属于这种，占掉 13.7% 的窗口。
                return RemoteStreamPacing.DelayAfterMiss(idlePeriod, Stopwatch.GetElapsedTime(roundStartedAt));
            }

            // ── 指针合成诊断的节流钟（M5.4-b 第 5 步）──
            //
            // ⚠️ 0 = "从没打过" ⇒ 第一轮必定打（Stopwatch.GetElapsedTime(0) 算的是
            //    "系统启动到现在"，远大于 5 秒）—— 这是有意的：开流头几帧的情况最值得看。
            var cursorLogAt = 0L;

            while (!cancellationToken.IsCancellationRequested)
            {
                var tick = Stopwatch.GetTimestamp();

                // 🔥 **每一轮都记**（包括那些一帧都没产出的轮次）：`一轮 N ms` 只统计产出了帧的轮，
                //    差额全在空转轮里 —— 不记它们，"5 秒 238 帧"与"16.6 ms/帧"会同时成立而账不平。
                _probe.CountRound();

                // 🔥 <b>帧率是每轮重算的</b>（M5.5 运行时自适应）。
                //    算在循环外面的话，中心的"降到低画质"就变成"改了个没人读的字段" ——
                //    页面上显示 8 fps、实际仍按 12 fps 推，而没有任何一处报错。
                //    ⚠️ 只在**真的变了**时重算：每轮都算一次 TimeSpan.FromSeconds 是白费，
                //      更糟的是它会让 `remaining = targetPeriod - elapsed` 那个判据
                //      每秒抖动一次（无谓的提前/延后一帧）。
                if (TargetFps != pacedFps)
                {
                    pacedFps = TargetFps;
                    targetPeriod = TimeSpan.FromSeconds(1.0 / pacedFps);

                    log($"[Stream] 会话 {SessionId}：目标帧率改为 {pacedFps} fps（节流周期 {targetPeriod.TotalMilliseconds:0} ms）。");
                }

                // ── 耗时探针（M7.4 §6.2）──
                //
                // 🔥 **放在这里**（而不是"发完一帧之后"）：循环里那几条 `continue`
                //    （静止 / 抓屏失败 / 编码失败 / 超限丢弃）都会跳过后半段，
                //    而"一帧都没出"的那种卡住<b>恰恰最需要报告</b>。
                //    放在循环开头 ⇒ 只要轮询还在跑（退避后最慢 0.5 秒一次）就一定会报。
                //
                // ⚠️ 打的是<b>此刻生效的</b> `TargetFps`：改档到底有没有到托盘，
                //    这一句和上一句"目标帧率改为 N fps"摆在一起就能自证
                //    （本轮 18.2 fps 那个坑正是"改没改到"说不清造成的）。
                if (_probe.TryReport(TargetFps, out var probeLine)) log(probeLine);

                // 🔥 **并入外部的"请补一个关键帧"请求**（M5.4-c① 步 5）。
                //
                //    放在循环**最开头**、抓屏之前：请求要影响的是"这一轮编出来的帧"，
                //    而编码发生在这一轮的中段 —— 放到后面就等于白等一轮（83 ms @12fps）。
                //
                //    ⚠️ 取一次就清零（TakeKeyframeRequest 的语义），但 `wantKeyframe`
                //       是**循环外**的变量：置上之后它一直是 true，直到真的发出了一个关键帧
                //       才清（见下面成功那一段的 `if (payload.IsKeyframe)`）。
                //       所以"本轮抓屏/编码失败走了 continue"不会把请求弄丢 —— 下一轮照样带着。
                //       这条性质很重要：请求来的时候恰好撞上一次抓屏失败（锁屏/UAC）很常见，
                //       而"请求被一次失败吃掉"的现场表现与"压根没做这个功能"一模一样。
                if (TakeKeyframeRequest()) wantKeyframe = true;

                // ── 指针合成：每 5 秒如实报一次（M5.4-b 第 5 步）──
                //
                // 🔥 这一行是"观看端看不到鼠标"唯一的现场入口。没有它，症状只剩一句"没动"，
                //    而"没动"至少有四种完全不同的成因（本来没指针／形状没拿到／读回失败／上传失败）。
                // ⚠️ 只在 GPU 路径打：内存 / MJPEG 路径根本不合成指针，CursorDiagnostics 返回 null。
                if (surfaceEncoder is not null
                    && Stopwatch.GetElapsedTime(cursorLogAt) >= CursorLogPeriod)
                {
                    cursorLogAt = Stopwatch.GetTimestamp();

                    log($"[Stream] 指针：{surfaceEncoder.CursorDiagnostics}");
                }

                // ── 抓 ──
                EncodedStreamPayload? payload;

                // 这一轮的"一批块"（M5.5）：单块时只有 1 项，分块时有 N 项。
                // ⚠️ 每轮都新建（几何级小对象，但**不能跨轮复用**——上一轮没发完的块
                //    会被当成这一轮的块再发一次，画面会在两块之间来回跳）。
                var batch = new List<EncodedRegionPayload>(3);

                if (surfaceEncoder is null)
                {
                    CapturedFrame frame;

                    var captureStartedAt = _probe.Mark();

                    try
                    {
                        frame = await capturer.CaptureAsync(ScreenId, cancellationToken).ConfigureAwait(false);

                        // ⚠️ 只在**真的抓到**之后记：异常那条路走到 catch 就 return/continue 了，
                        //    不该把它算成一次"抓屏耗时"（见 RemoteStreamProbe 类注释）。
                        _probe.AddCapture(captureStartedAt);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // 🔥 抓屏失败<b>会恢复</b>（锁屏、UAC 安全桌面、显示器热插拔都会短暂失败），
                        //    所以这里**不退出循环**，只记一次并在下一轮重试。
                        //    退出循环的代价是"每次锁屏回来都要人工点一次开始"，那太脆了。
                        //    ⚠️ 日志按 30 次节流：一秒钟一条会把托盘日志刷爆，反而盖住真问题。
                        if (consecutiveFailures++ % 30 == 0)
                        {
                            _lastError = $"抓屏失败（会继续重试）：{ex.Message}";

                            log($"[Stream] {_lastError}");
                        }

                        // ⚠️ 但"一帧都没出过"是另一回事 —— 那说明配置就是错的，
                        //    重试一辈子也不会好。见 StartupFrameBudget。
                        if (StartupBudgetExhausted(ex.Message)) return;

                        _probe.CountSkip(RemoteStreamProbe.SkipReason.CaptureFailed);

                        await DelayAsync(targetPeriod, cancellationToken).ConfigureAwait(false);

                        continue;
                    }

                    // ══════════════════════════════════════════════════════════════
                    // 🔥 **软编路径的静止检测（M5.5 挂账）**
                    //
                    //    GDI / X11 抓屏**每调一次就返回一整幅**（它没有"桌面变没变"这个概念），
                    //    所以这条路以前在定格画面上会一直抓、一直编、一直发 ——
                    //    一张 4K 桌面每秒 12–60 次 JPEG 编码，全白烧。
                    //    GPU 那条路不存在这个问题：DDA 桌面没变时直接回 null。
                    //
                    //    判据是**内容指纹**（`FrameFingerprint`），不是"抓屏器说没变" ——
                    //    抓屏器没有这个信息，而像素才是唯一的事实来源。
                    //
                    // ⚠️ 两处容易做错、且都不会报错：
                    //   ① `wantKeyframe` 时**必须发**：新观看者加入 / 中心重组失败都会走到这里，
                    //      而"画面静止"恰恰是新观众最常遇到的情形 —— 跳过它就永远是黑屏。
                    //   ② 像素没变时**不能更新 `_lastNewFrameAt`**：那是"静止多久"的唯一计时起点，
                    //      更新它会让退避永远不触发（idleFor 恒 0），这条分支等于没写。
                    // ══════════════════════════════════════════════════════════════
                    var fingerprint = FrameFingerprint.Of(frame);

                    if (_hasFingerprint && fingerprint == _lastFingerprint && !wantKeyframe)
                    {
                        // ⚠️ 与 GPU 那条同构：不动 `consecutiveFailures`，也不动 `_dropped` ——
                        //    "画面没变"既不是失败也不是丢帧。
                        _probe.CountSkip(RemoteStreamProbe.SkipReason.NoNewFrame);

                        await DelayAsync(HandleStaticFrame("帧比对", tick), cancellationToken).ConfigureAwait(false);

                        continue;
                    }

                    _lastFingerprint = fingerprint;
                    _hasFingerprint = true;

                    idleTicks = 0;

                    // 🔥 抓到**新**画面 ⇒ 桌面不再静止（M5.4-c② 步 7）。
                    //    这个时刻是"桌面静止多久"的**唯一**起点 —— 它必须在这里更新，
                    //    而不是在"帧发出去"那一刻：编码失败 / 超限丢弃同样会让发帧停下，
                    //    而那与桌面有没有变毫无关系。
                    Volatile.Write(ref _lastNewFrameAt, Stopwatch.GetTimestamp());

                    // 🔥 一有变化就**立刻**回到目标帧率（不是"等这一轮退避周期跑完"）。
                    ResumeTargetPacing();

                    // ── 编（内存路径）──
                    //
                    // ⚠️ 用的是 `memoryEncoder!` 而不是 `encoder`：`TryEncode` 只存在于
                    //    `IFrameEncoder` 上，而公共底座**刻意没有**它 —— 那是两条路真正的分水岭
                    //    （入参是内存字节还是纹理句柄）。上面那道守卫已经保证
                    //    "`surfaceEncoder is null` ⇒ `memoryEncoder` 一定不是 null"。
                    var encodeStartedAt = _probe.Mark();

                    var encoded = memoryEncoder!.TryEncode(frame, forceKeyframe: wantKeyframe, out payload);

                    _probe.AddEncode(encodeStartedAt);

                    if (!encoded || payload is null)
                    {
                        var dropped = Interlocked.Increment(ref _dropped);

                        if (consecutiveFailures++ % 30 == 0)
                        {
                            _lastError = $"编码失败（会继续重试）：{encoder.LastFailure ?? "编码器返回了 false。"}";

                            log($"[Stream] {_lastError}（累计丢帧 {dropped}）");
                        }

                        // 编码一侧同理：如果每一帧都编不出来，"再等等"永远不会变好。
                        if (StartupBudgetExhausted(encoder.LastFailure ?? "编码器返回了 false。")) return;

                        _probe.CountSkip(RemoteStreamProbe.SkipReason.EncodeNotReady);

                        await DelayAsync(targetPeriod, cancellationToken).ConfigureAwait(false);

                        continue;
                    }
                }
                else
                {
                    // ══════════════════════════════════════════════════════════════
                    // GPU 路径（M5.4-b）：抓纹理 → 零拷贝硬编
                    // ══════════════════════════════════════════════════════════════
                    CapturedSurface? surface;

                    var captureStartedAt = _probe.Mark();

                    try
                    {
                        surface = await capturer
                            .CaptureSurfaceAsync(ScreenId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // 与内存路径同样的处置：抓屏失败会恢复，不退出循环。
                        // ⚠️ 这里能进得来的是**真失败**（`CaptureSurfaceAsync` 只在有 failure 时抛），
                        //    "画面没变"走的是下面的 `surface is null`，不是异常。
                        if (consecutiveFailures++ % 30 == 0)
                        {
                            _lastError = $"抓屏失败（会继续重试）：{ex.Message}";

                            log($"[Stream] {_lastError}");
                        }

                        if (StartupBudgetExhausted(ex.Message)) return;

                        _probe.CountSkip(RemoteStreamProbe.SkipReason.CaptureFailed);

                        await DelayAsync(targetPeriod, cancellationToken).ConfigureAwait(false);

                        continue;
                    }

                    if (surface is null)
                    {
                        // "没有新帧"不是失败：DXGI Desktop Duplication 是**增量投递**的，
                        // 桌面没变、指针也没动时它按约定返回 null。完整的理由见
                        // `HandleStaticFrame` 上方那段注释。
                        //
                        // ⚠️ 这里**不动 `consecutiveFailures`**：它是"连续失败"计数，
                        //    把成功的抓取混进去会让真正的失败被冲淡（那正是节流日志的判据）。
                        _probe.CountSkip(RemoteStreamProbe.SkipReason.NoNewFrame);

                        await DelayAsync(HandleStaticFrame("抓屏", tick), cancellationToken).ConfigureAwait(false);

                        continue;
                    }

                    // ⚠️ 走到这里 = **真的抓到了画面**（`surface is null` 那一支已经 continue），
                    //    所以这里记的才是抓屏成本本身；`surface is null` 那条路等的是
                    //    `AcquireNextFrame` 的超时，不属于抓屏成本（见 RemoteStreamProbe 类注释）。
                    _probe.AddCapture(captureStartedAt);

                    idleTicks = 0;

                    // 🔥 抓到新画面 ⇒ 桌面不再静止（M5.4-c② 步 7）。
                    //    这个时刻是"桌面静止多久"的**唯一**起点 —— 它必须在这里更新，
                    //    而不是在"帧发出去"那一刻：编码失败 / 超限丢弃同样会让发帧停下，
                    //    而那与桌面有没有变毫无关系。
                    Volatile.Write(ref _lastNewFrameAt, Stopwatch.GetTimestamp());

                    // 🔥 一有变化就**立刻**回到目标帧率（不是"等这一轮退避周期跑完"）：
                    //    用户动手那一刻的延迟必须还是目标帧率的量级，
                    //    否则"省 CPU"就会变成"每次动鼠标先卡一下"。
                    ResumeTargetPacing();

                    // ── 编（GPU 路径）──
                    //
                    // 🔥 **调用前后各读一次 `DiscardedFrames`**，这是接口给出的唯一判据
                    //    （见 `IVideoFrameEncoder.DiscardedFrames`）：增量 > 0 才算真丢帧。
                    var discardedBefore = encoder.DiscardedFrames;

                    // 🔥 **分块/ROI（M5.5）**：编码器说了算"这次要编几块"。
                    //    单块时走老接口（M5.4 的行为一个字都不变），多块时才走区域接口。
                    //    ⚠️ 判据是 `Regions.Count`，不是"有没有设过视口"：视口恰好等于整幅时
                    //    （用户把浏览器拉到和桌面一样大）那本来就该走过去那条路。
                    var regionEncoder = surfaceEncoder as IRegionSurfaceEncoder;

                    var tiled = regionEncoder is not null && regionEncoder.Regions.Count > 1;

                    bool encodedOk;

                    payload = null;

                    var encodeStartedAt = _probe.Mark();

                    if (tiled)
                    {
                        encodedOk = regionEncoder!.TryEncodeSurfaceRegions(surface, wantKeyframe, batch);

                        // 第 1 块就是老接口那颗"整帧"—— 它没出来时 `TryEncodeSurfaceRegions` 已经回了 false，
                        // 这里只是把码流取出来给下面统一的失败处理用。
                        if (batch.Count > 0) payload = batch[0].Stream;
                    }
                    else
                    {
                        encodedOk = surfaceEncoder.TryEncodeSurface(
                            surface, forceKeyframe: wantKeyframe, out payload);
                    }

                    _probe.AddEncode(encodeStartedAt);

                    if (!encodedOk || payload is null)
                    {
                        // ══════════════════════════════════════════════════════
                        // 🔥🔥 **这里必须分清"丢帧"与"异步延迟"，判据是"喂进去没有"。**
                        //
                        //   异步 MFT 的输出**晚一次调用才到**（M5.4-b 实测：喂 17 帧出 16 帧）
                        //   ⇒ 这一轮返回 `false` 完全可能只是"码流还没出来"，而不是"这一帧丢了"。
                        //   把两者当同一件事，就同时犯两个错：
                        //     ① `Dropped` 虚高 —— 页面报"掉了很多帧"而实际一帧没缺
                        //        （现场会把排查方向从"编码器"带到"网络"去）；
                        //     ② 恢复动作也会做错 —— 见下面那句"不要求关键帧"。
                        //
                        //   🔥 **判据不是去读 `encoder.LastFailure` 的文案**（文案一改分类就错），
                        //      而是**计数**：门面已经把这个区分做进 `DiscardedFrames` 里了。
                        // ══════════════════════════════════════════════════════
                        var discardedDelta = encoder.DiscardedFrames - discardedBefore;

                        if (discardedDelta > 0) Interlocked.Add(ref _dropped, discardedDelta);

                        if (consecutiveFailures++ % 30 == 0)
                        {
                            var why = encoder.LastFailure ?? "编码器返回了 false，但没有给出原因。";

                            // ⚠️ 两种情况的**文案必须不同**，因为它们该去查的东西完全不同：
                            //    一个是"编码器/转换器有问题"，另一个是"一切正常，只是要等一下"。
                            _lastError = discardedDelta > 0
                                ? $"编码失败（会继续重试，本次真丢 {discardedDelta} 帧）：{why}"
                                : $"这一帧的码流还没出来（异步编码的正常延迟，**不是丢帧**）：{why}";

                            log($"[Stream] {_lastError}");
                        }

                        // 🔥 **正常延迟这一格绝不能要求关键帧。**
                        //    那个帧根本没丢，参考链上没有洞；强行插一个 IDR 只是白白让码率抖一下
                        //    （IDR 是 P 帧的十几倍大，而 4K 的 IDR 本来就已经贴着管道上限了）。
                        if (discardedDelta > 0) wantKeyframe = true;

                        if (StartupBudgetExhausted(encoder.LastFailure ?? "编码器返回了 false。")) return;

                        // ⚠️ 分类**必须**跟着上面那个 `discardedDelta` 走：真丢帧是闸门/编码器的问题，
                        //    "码流还没出来"是异步延迟的正常现象 —— 两者的处置完全不同，
                        //    算成一类就等于把结论指向另一层。
                        _probe.CountSkip(discardedDelta > 0
                            ? RemoteStreamProbe.SkipReason.Dropped
                            : RemoteStreamProbe.SkipReason.EncodeNotReady);

                        await DelayAsync(targetPeriod, cancellationToken).ConfigureAwait(false);

                        continue;
                    }
                }

                consecutiveFailures = 0;

                // 走到这里的 payload **一定不是 null**（上面两个分支都在它为 null 时 continue 了）。
                // ⚠️ 但还是留一道显式守卫：这一层绝不允许 NRE 逃出去 —— 那会让整个托盘进程
                //    悄无声息地消失（WinExe + 未捕获异常）。
                if (payload is null) continue;

                // ══════════════════════════════════════════════════════════════
                // ── 统一成"一批块"（M5.5 分块/ROI）──
                //
                // 🔥 **单块路径也要有 Region 信息**：帧头缺了它，前端只能按"整幅"画，
                //    于是"只编了视口"的那一路会被拉满整个画布 —— 而那种画面
                //    看起来像"缩放错了"，不像"少了三个字段"。
                // ══════════════════════════════════════════════════════════════
                if (batch.Count == 0)
                {
                    // ══════════════════════════════════════════════════════════════
                    // 🔴 **"整幅"到底是多大 —— 这个判据只能由编码能力决定，不能想当然。**
                    //
                    //    正确的语义是「整幅画面**在码流坐标系里**的尺寸」：
                    //    `Region` 与 `FrameWidth/FrameHeight` 必须是同一个坐标系，
                    //    否则前端把一块画到画布上时比例就是错的。
                    //
                    //    ✅ 支持区域编码的编码器（GPU）：接口承诺 1:1、不缩放
                    //       （见 `EncodeRegion` 的契约）⇒ 整幅恒等于源纹理尺寸。
                    //    🔴 不支持区域编码的编码器（MJPEG 等内存路径）：它编的**就是整幅**，
                    //       但它会**主动降采样**（`GdiJpegFrameEncoder.DefaultMaxLongEdge = 1920`）。
                    //       这时整幅在码流里只有 payload 那么大。
                    //
                    //    ⚠️ 曾经这里无条件用会话的屏尺寸，理由是"内存路径按屏尺寸填"。
                    //       那个假设是错的，而且是"不报错、只是画面不对"的那一类：
                    //       三屏拼接 5760×2160 被压到 1920×720 后，帧头仍报 5760×2160
                    //       ⇒ 前端画布 5760×2160、把 1920×720 画在 (0,0)
                    //       ⇒ **画面只占左上 1/9，其余全黑**。
                    //       单屏 4K 同理占左上 1/4 —— 看起来像"只显示了四分之一"，
                    //       而两端日志、码率、帧率全都正常。
                    // ══════════════════════════════════════════════════════════════
                    var keepsNativeSize = surfaceEncoder is IRegionSurfaceEncoder;

                    var (frameWidth, frameHeight) = RemoteFrameExtent.Resolve(
                        keepsNativeSize,
                        Width,
                        Height,
                        payload.Width,
                        payload.Height);

                    batch.Add(new EncodedRegionPayload(
                        TileIndex: 0,
                        TileCount: 1,
                        Region: new EncodeRegion(0, 0, payload.Width, payload.Height),
                        FrameWidth: frameWidth,
                        FrameHeight: frameHeight,
                        Stream: payload));
                }

                // 🔥 **同一批块共享同一个序号**：前端每一块各有一个独立的抖动缓冲，
                //    它靠序号判"断没断"。每块各自 +1 会让每一块都以为"每帧都跳了 N 个号"
                //    ⇒ 每帧重建解码器 ⇒ 画面永远停在关键帧上（而且不报错）。
                //
                // ⚠️ 但**不能在这里就把号取掉**：序号回答的是"真的发出去了第几帧"，
                //    而这一批里的每一块都还可能在下面的尺寸闸门被丢掉。
                //    提前取号 ⇒ 一次丢帧在序号上留一个没人消费的洞
                //    ⇒ 首帧不再是 1（`RemoteStreamOversizeTests` 钉着这一条）。
                //    所以：**清 0，谁真正要写帧头谁取**，同批的后续块直接复用。
                _batchSequence = 0;

                var anyDropped = false;

                foreach (var tile in batch)
                {
                    var tileResult = await SendOneAsync(tile).ConfigureAwait(false);

                    if (tileResult == FrameSendResult.Fatal) return;

                    if (tileResult == FrameSendResult.Dropped) anyDropped = true;

                    // ⚠️ **丢一块不等于丢整帧**（分块时）：每块是**独立码流、独立解码器**，
                    //    丢一块只让"那一条区域"停在上一帧，其他区域照常刷新。
                    //    若在这里 break，一次超限就把整幅画面冻住 —— 而分块的全部意义
                    //    正是"把一次大超限摊成几块小超限"。
                    //    单块路径（TileCount == 1）下这里是等价的（本来就没有下一块）。
                }

                if (anyDropped)
                {
                    _probe.CountSkip(RemoteStreamProbe.SkipReason.Dropped);

                    await DelayAsync(targetPeriod, cancellationToken).ConfigureAwait(false);

                    continue;
                }

                // ── 节流 ──
                //
                // 🔥 **发完就记"这一轮花了多久"**（M7.4 §12.2）：它必须记在节流<b>之前</b>，
                //    否则这一项里会混进睡眠 —— 而"睡掉了"与"忙不过来"是两条完全相反的路
                //    （前者调节流，后者才动热路径），混在一起就分不出来了。
                _probe.AddCycle(tick);

                var remaining = targetPeriod - Stopwatch.GetElapsedTime(tick);

                if (remaining > TimeSpan.Zero)
                {
                    var idleStartedAt = _probe.Mark();

                    await DelayAsync(remaining, cancellationToken).ConfigureAwait(false);

                    // 🔥 请求睡 `remaining`，真睡多久只有这一步知道 ——
                    //    它与 `remaining` 的差就是睡眠过冲（1 ms 粒度下通常是 1–2 ms）。
                    _probe.AddIdle(idleStartedAt);

                    continue;
                }

                // 这一帧已经超时了。**不补偿、不追帧** —— 追帧会让后面每一帧都更晚，
                // 延迟越积越多。宁可实际帧率低于目标（那会如实反映在状态里）。
                await Task.Yield();
            }

            // ══════════════════════════════════════════════════════════════
            // ── 发一块（打包 + 闸门 + 写管道）──
            //
            // 🔥 抽成局部函数只为一个理由：**单块与分块必须走同一段代码**。
            //    分块那条路如果自己再写一遍打包，就会出现"带外时只有第一块能解"
            //    （每块各有自己的 SPS/PPS）这种不报错的错 —— 而它只在真机上、
            //    只在大分辨率下出现。
            // ══════════════════════════════════════════════════════════════
            async Task<FrameSendResult> SendOneAsync(EncodedRegionPayload tile)
            {
                var payload = tile.Stream;

                // ══════════════════════════════════════════════════════════════
                // ── 打包（M5.4-b 第 6 步）──
                //
                // 🔥🔥 **这一段的全部内容就是"让帧头与码流必须自洽"**，而它是本批
                //    最容易写出"不报错的错"的地方。规则只有一条，但它有两个方向：
                //
                //      · 带内（avc3 / hev1）：参数集<b>在码流里</b> ⇒ 码流一个字节都不动，
                //        `Description` **必须**是 null（贴了 avcC 却还是 Annex-B = configure 失败或花屏）；
                //      · 带外（avc1 / hvc1）：参数集<b>在帧头里</b> ⇒ 码流**必须**换成长度前缀，
                //        `Description` 必须是 avcC/hvcC。
                //
                //    ⇒ 「贴了 avcC、码流却还是 Annex-B」这两半是**连体的**：
                //      只做一半不会报错，只会在浏览器那边变成"黑屏"或"花屏"，
                //      而两端日志都干净。所以下面两条路各自**一整块**写在一起，不做成两个开关。
                //
                // ⚠️ 它必须在**尺寸闸门之前**：带外会把每个 NAL 的起始码（3 或 4 字节）
                //    换成固定的 4 字节长度前缀，**码流长度会变**（可能变大）——
                //    拿转换前的长度去过闸门，等于用一个错的数字去决定要不要丢帧。
                // ══════════════════════════════════════════════════════════════
                var frameData = payload.Data;
                string? frameCodecString = null;
                byte[]? frameDescription = null;

                // ⚠️ MJPEG 走不进这里（它没有参数集、没有 GOP，"打包方式"对它毫无意义）。
                //    判据用 `TryParse` 而不是"是不是 surfaceEncoder"：真正决定
                //    "这段字节能不能当 H.264/HEVC 访问单元来解"的是**编码名**。
                if (StreamCodecMap.TryParse(payload.Codec, out var frameCodec))
                {
                    if (payload.IsKeyframe)
                    {
                        TryRefreshParameterSets(payload.Data, frameCodec, log);

                        // 第一个关键帧就缺参数集时，码流里那份是靠不住的 ⇒ 问编码器要一次兜底。
                        // ⚠️ 只在"一次都没解出来过"时问：它是第二来源，常规路径不该依赖它。
                        if (_codecDescription is null && surfaceEncoder is not null)
                        {
                            TryRescueParameterSetsFromEncoder(surfaceEncoder, frameCodec, log);
                        }
                    }

                    // 🔴 **分块时一律带内**：`_codecDescription` 是"第一块"的参数集，
                    //    而每块的 SPS/PPS 可能不同（宽度不同、有的编码器还会给不同的 level）。
                    //    拿第一块的 avcC 去配置每一块 = 后面那些块 `<b>configure()</b>` 失败或花屏。
                    var outOfBand = Packaging == RemoteStreamPackagings.OutOfBand
                        && _codecDescription is not null
                        && tile.TileCount <= 1;

                    if (outOfBand)
                    {
                        // ── 带外：把 Annex-B 改写成长度前缀形式 ──
                        var needed = AnnexB.MeasureCanonicalAccessUnit(
                            payload.Data, frameCodec, includeParameterSets: false);

                        var buffer = new byte[Math.Max(needed, 1)];

                        var written = AnnexB.WriteCanonicalAccessUnit(
                            payload.Data, frameCodec, includeParameterSets: false, buffer);

                        if (written < 0)
                        {
                            // 🔥 **绝不截断**：量出来的长度就是写进去的长度（两边用的是同一个判据），
                            //    走到这里说明实现有问题。截断的码流在浏览器里是花屏，
                            //    而且没有任何一处会报错 —— 那比丢一帧坏得多。
                            var missed = Interlocked.Increment(ref _dropped);

                            _lastError =
                                $"带外打包时长度前缀写不进去（需要 {needed} 字节，缓冲区 {buffer.Length} 字节）"
                                + "—— 这是本机实现的问题，已丢掉这一帧，未截断。";

                            log($"[Stream] {_lastError}（累计丢帧 {missed}）");

                            wantKeyframe = true;

                            return FrameSendResult.Dropped;
                        }

                        frameData = written == buffer.Length
                            ? buffer
                            : buffer.AsSpan(0, written).ToArray();

                        frameDescription = _codecDescription;
                        frameCodecString = _codecString;
                    }
                    else
                    {
                        if (Packaging == RemoteStreamPackagings.OutOfBand
                            && tile.TileCount <= 1
                            && !_warnedOutOfBandUnavailable)
                        {
                            _warnedOutOfBandUnavailable = true;

                            // ⚠️ 这是"退而求其次"，不是故障：带内那条路本身是完整的
                            //    （只要参数集真在码流里 —— 上面那个计数会告诉我们）。
                            log($"[Stream] 会话 {SessionId}：请求的是带外打包，但至今没能解出参数集"
                                + "（avcC/hvcC 建不出来）—— **本帧按带内发**（码流原样、不带 description）。"
                                + "观看端会以带内方式 configure；等解出参数集后自动恢复带外。");
                        }

                        frameCodecString = VideoSampleEntries.ToInBand(_codecString);
                    }
                }

                // ── 尺寸闸门 ──
                //
                // 🔥 **在写之前判，不走异常路径**：这时候我们还来得及做"对的事"
                //    （丢这一帧 + 下一帧强制关键帧），而不必先撞一次异常再回头猜它是什么意思。
                //    见 RemoteStreamProtocol.FitsInPipe 与 FrameTooLargeException 的说明。
                if (!RemoteStreamProtocol.FitsInPipe(frameData.Length))
                {
                    if (OversizeGiveUp(frameData.Length, payload.IsKeyframe)) return FrameSendResult.Fatal;

                    wantKeyframe = true;

                    return FrameSendResult.Dropped;
                }

                // ── 取号 ──
                //
                // 🔥 **序号在这里才取**（打包完成、闸门已过 = 这一块真的要写出去了）。
                //    同批的其余块复用同一个号；被丢掉的那一块根本走不到这里，
                //    所以它不占号，序号上不会出现没人消费的空洞。
                if (_batchSequence == 0) _batchSequence = Interlocked.Increment(ref _sequence);

                var sequence = _batchSequence;

                // ── 发 ──
                var header = new RemoteStreamFrameHeader(
                    SessionId: SessionId,
                    Sequence: sequence,
                    Codec: payload.Codec,
                    IsKeyframe: payload.IsKeyframe,
                    Width: payload.Width,
                    Height: payload.Height,
                    CapturedUnixMs: payload.CapturedUnixMs,

                    // 累计丢帧随每一帧带过去：中心看不到被控端本地发生的事，
                    // 这是"画面为什么卡"唯一能穿过来的证据。
                    Dropped: Interlocked.Read(ref _dropped),

                    // 🔥 AV 同步用的单调时刻。**优先取编码器带出来的那个**
                    //    （`payload.CapturedMonoTicks` = 抓到那一帧的瞬间）。
                    //
                    //    ⚠️⚠️ 这里修的是一个**既有缺陷**：早先这里写的是
                    //        `MonotonicMs(Stopwatch.GetTimestamp())`，也就是"编完之后的现在"。
                    //        而异步 MFT 的输出**晚一次调用才到**（喂 17 帧出 16 帧）⇒
                    //        这段字节其实是**上一帧**的，而它被贴上了"这一帧的到达时刻"。
                    //        两个后果：① AV 同步整体差一帧（30 fps 下 33 ms，唇音看得出）；
                    //        ② 更糟的是**没人会往这个方向查** —— 因为 `Dropped` 同时也在虚高，
                    //        现场会先怀疑网络。这就是本方案 §3.6 那条"计数错了，排查方向就反了"
                    //        的同族错误。
                    //    `Ticks == 0` = 该编码器没上报（内存路径就属于这种）⇒ 退回"现在"，
                    //    并接受那份编码耗时的偏差（MJPEG 逐帧独立，本来就没有这个问题）。
                    CapturedMonoMs: payload.CapturedMonoTicks > 0
                        ? MonotonicMs(payload.CapturedMonoTicks)
                        : MonotonicMs(Stopwatch.GetTimestamp()),
                    CodecString: frameCodecString,
                    Description: frameDescription,

                    // ── 分块/ROI（M5.5）──
                    // 🔥 这四个值让前端知道"这一帧画在整幅的哪个位置、整幅有多大"。
                    //    缺了它们，前端只能按"整幅"画 —— 而那时画面错的不是一点点，
                    //    是整体缩放/错位，且页面不会报任何错。
                    FrameWidth: tile.FrameWidth > 0 ? tile.FrameWidth : payload.Width,
                    FrameHeight: tile.FrameHeight > 0 ? tile.FrameHeight : payload.Height,
                    RegionX: tile.Region.X,
                    RegionY: tile.Region.Y,
                    TileIndex: tile.TileIndex,
                    TileCount: tile.TileCount);

                var writeStartedAt = _probe.Mark();

                try
                {
                    await RemoteStreamProtocol
                        .WriteFrameAsync(pipe, header, frameData, cancellationToken)
                        .ConfigureAwait(false);

                    // 🔥 **这一项里含着"到中心的往返"**（M7.4 §6.2）：管道另一头是服务侧
                    //    逐帧 `await` 的 SignalR 上行，管道写满时 `WriteAsync` 会在这里等
                    //    ⇒ 那 44 ms 如果真在"回执卡住循环"，就会全部显现在这个数上。
                    _probe.AddWrite(writeStartedAt);
                }
                catch (OperationCanceledException)
                {
                    return FrameSendResult.Fatal;
                }
                catch (FrameTooLargeException ex)
                {
                    // 🔥 **兜底：这里本该永远走不到**（上面那道闸门用的是同一个常量）。
                    //    留着它是因为"忘了预检"的代价曾经是**整条流结束** ——
                    //    而现在最坏也只是丢一帧。真正的价值不在这次 catch 的执行，
                    //    而在它把"太大"从下面那个 catch (Exception) 里**摘了出去**。
                    if (OversizeGiveUp(ex.PayloadBytes, payload.IsKeyframe)) return FrameSendResult.Fatal;

                    wantKeyframe = true;

                    return FrameSendResult.Dropped;
                }
                catch (Exception ex)
                {
                    // 🔥 写不出去 = 接收端没了（服务重启 / 会话被结束）。
                    //    这是**终止条件**，不是可重试的故障：继续循环只会一遍遍失败，
                    //    而每失败一次就白抓白编一帧 —— 一直在烧被控机的 CPU。
                    //
                    // ⚠️ **前提是"帧本身没问题"**。上面已经把"太大"那一类摘掉了，
                    //    所以走到这里才真的可以断定是链路断了。
                    //    这就是本节开头那个 bug 的修法：分类错一次，
                    //    就把一次丢帧升级成一次掉线，而且症状指向完全相反的方向。
                    _lastError = $"码流写不出去，接收端已断开：{ex.Message}";

                    log($"[Stream] 会话 {SessionId} 结束：{_lastError}");

                    return FrameSendResult.Fatal;
                }

                Interlocked.Increment(ref _framesSent);
                Interlocked.Add(ref _bytesSent, frameData.Length);

                // ⚠️ 探针的帧数**只在真的写出去了之后**加：走 `Dropped` 那条路的一帧
                //    没有序号、也没有字节，把它算进"出帧"会让实测帧率虚高。
                _probe.CountFrame(frameData.Length);

                // 🔥 **只有真的发出了关键帧，才把"要关键帧"这件事清掉。**
                //
                //    原来是**无条件**清 —— 而那就有一个静默的洞：如果编码器没有照办
                //    （`forceKeyframe: true` 只是个请求，不是命令），我们会在
                //    "并没有拿到关键帧"的情况下把需求忘掉 ⇒ 请求被吞，观看端继续花。
                //    这与下面 `_oversizeKeyframeStreak` 用 `if (payload.IsKeyframe)` 才清
                //    是同一条纪律：**"有帧发出去了"不等于"观看端恢复了"**。
                if (payload.IsKeyframe) wantKeyframe = false;

                // 🔥 关键帧**成功发出去**了 ⇒ 观看端重新拿到了参考帧，之前那些丢帧
                //    留下的洞到此为止。连续计数清零。
                //
                //    ⚠️ 只在关键帧成功时才清：普通帧发成功**不代表**观看端恢复了
                //       —— 它如果缺参考帧，解出来的还是一屏花。用"有帧发出去了"
                //       当恢复判据，正是"计数正常、画面全花"的成因。
                if (payload.IsKeyframe) Interlocked.Exchange(ref _oversizeKeyframeStreak, 0);

                return FrameSendResult.Sent;
            }
        }

        // ── 音频采集循环（M5.3）──

        /// <summary>
        /// 架好音频管道（<b>方向 Out</b>：这里写、服务读），把 WASAPI 环回采到的系统声音
        /// 编成 Opus 后一帧帧写下去。
        ///
        /// <para>
        /// 🔥 <b>它与推流循环的差别只有一处，但那一处很要紧</b>：画面那一条是
        /// "抓一帧 → 编一帧 → 写一帧"的<b>严格串行节流</b>（宁可掉帧，不要堆积延迟）；
        /// 而音频是<b>采集侧推、这里消费</b>：采集器自己有一条带上限的队列，
        /// 跟不上时它丢最旧的。这一条之所以反过来，是因为音频<b>不能靠"少发几帧"来降载</b> ——
        /// 少发一帧就是一声可听见的断点，而少发一帧画面只是顿一下。
        /// </para>
        ///
        /// <para>
        /// ⚠️ <b>本方法绝不抛</b>：托盘是 WinExe，未捕获异常会让进程悄无声息地消失。
        /// 而且音频失败<b>绝不该拖垮画面</b> —— 这条循环是独立的任务，它死掉只影响声音。
        /// </para>
        /// </summary>
        private async Task AudioLoopAsync(
            CancellationToken cancellationToken,
            Func<IAudioCapturer> capturers,
            Func<IAudioEncoder> encoders,
            Action<string> log)
        {
            IAudioCapturer? capturer = null;
            IAudioEncoder? encoder = null;
            NamedPipeServerStream? pipe = null;

            var reason = "音频通道未建立。";

            try
            {
                // ── 采集器 ──
                try
                {
                    capturer = capturers();
                }
                catch (Exception ex)
                {
                    Fail($"创建音频采集器失败：{ex.Message}");
                    return;
                }

                if (!capturer.IsAvailable)
                {
                    // 🔥 这是**真实存在**的一种机器（虚拟机 / 无声卡工控机），不是故障。
                    //    如实报出去，页面才有机会说"这台机器没有可用的音频输出设备"，
                    //    而不是让用户去查网络。
                    Fail(capturer.AvailabilityNote);
                    return;
                }

                var format = capturer.Format;

                if (format is null)
                {
                    Fail("音频采集器报告可用，但取不到格式（内部状态不一致）。");
                    return;
                }

                // ── 编码器 ──
                try
                {
                    encoder = encoders();
                }
                catch (Exception ex)
                {
                    Fail($"创建音频编码器失败：{ex.Message}");
                    return;
                }

                SetAudioState(AgentAudioStates.Connecting, null);

                // ── 管道 ──
                //
                // 🔥 托盘建管道（服务端）、服务连上来（客户端）—— 与码流、输入两条一致，
                //    理由见 RemoteAudioProtocol（默认 DACL 只给 SYSTEM + 管理员 + 创建者）。
                pipe = new NamedPipeServerStream(
                    RemoteAudioProtocol.PipeNameForSession(SessionId),
                    PipeDirection.Out,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 16 * 1024);

                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(RemoteAudioProtocol.ConnectTimeoutMs);

                    try
                    {
                        await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        Fail(cancellationToken.IsCancellationRequested
                            ? "会话已结束。"
                            : $"等了 {RemoteAudioProtocol.ConnectTimeoutMs / 1000} 秒没有接收端来音频管道"
                              + "（服务侧可能没有起音频泵 —— 客户端 / 服务端版本不一致？）。");

                        return;
                    }
                }

                log($"[Audio] 会话 {SessionId} 开始采集：{format}｜Opus "
                    + $"{OpusAudioEncoder.FrameMilliseconds} ms / 帧｜"
                    + $"管道={RemoteAudioProtocol.PipeNameForSession(SessionId)}");

                var payloads = new List<EncodedAudioPayload>(4);
                var announced = false;

                await foreach (var chunk in capturer.CaptureAsync(cancellationToken).ConfigureAwait(false))
                {
                    payloads.Clear();

                    if (!encoder.TryEncode(chunk, payloads))
                    {
                        // ⚠️ 这里**不退出**：一帧编不出来不该让整条音频死掉
                        //    （持续失败会由 dropped 计数在状态里体现出来）。
                        //    也**不 continue**：TryEncode 的 false 是"这一批里有一部分没成"，
                        //    而不是"一个都没成" —— 已经编出来的那些仍然要发。
                        Interlocked.Increment(ref _audioDropped);
                    }

                    foreach (var payload in payloads)
                    {
                        var header = new RemoteAudioFrameHeader(
                            SessionId: SessionId,
                            Sequence: Interlocked.Increment(ref _audioSequence),
                            Codec: payload.Codec,
                            SampleRate: payload.SampleRate,
                            Channels: payload.Channels,
                            SamplesPerFrame: payload.SamplesPerChannel,
                            CapturedUnixMs: payload.CapturedAt.ToUnixTimeMilliseconds(),
                            CapturedMonoMs: MonotonicMs(payload.CapturedQpcTicks),
                            Dropped: Interlocked.Read(ref _audioDropped));

                        try
                        {
                            await RemoteAudioProtocol
                                .WriteFrameAsync(pipe, header, payload.Data, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            // 写不出去 = 接收端没了（服务重启 / 会话被结束）。
                            // 这是**终止条件**：继续采只会一遍遍失败，白烧被控机的 CPU。
                            reason = $"音频写不出去，接收端已断开：{ex.Message}";
                            log($"[Audio] 会话 {SessionId} 音频结束：{reason}");
                            return;
                        }

                        Interlocked.Increment(ref _audioFramesSent);
                        Interlocked.Add(ref _audioBytesSent, payload.Data.Length);

                        if (!announced)
                        {
                            // 🔥 到这一刻才算"就绪"：前面那些都是"正在连"。
                            //    把"连上了"与"出过声音"分开报，是因为它们对应
                            //    "还没开始"与"开始了但没声音（被控机真的在放静音）"两种现场。
                            announced = true;
                            SetAudioState(AgentAudioStates.Ready, null);
                        }
                    }
                }

                reason = "音频采集正常结束。";
            }
            catch (OperationCanceledException)
            {
                reason = "已取消（会话结束）。";
            }
            catch (Exception ex)
            {
                // 见方法注释：绝不外抛。
                reason = $"音频采集出错：{ex.GetType().Name}：{ex.Message}";

                log($"[Audio] 会话 {SessionId} {reason}");
            }
            finally
            {
                try
                {
                    pipe?.Dispose();
                }
                catch
                {
                    // 忽略。
                }

                try
                {
                    encoder?.Dispose();
                }
                catch
                {
                    // 忽略。
                }

                try
                {
                    capturer?.Dispose();
                }
                catch
                {
                    // 忽略。
                }

                // 🔥 从没到过 ready ⇒ 如实标成"不可用"。
                //    没有这一步，一路音频起不来在状态里就会**保持 connecting** ——
                //    而页面上"正在连接"会让人一直等一个永远不会来的结果。
                if (!string.Equals(_audioState, AgentAudioStates.Ready, StringComparison.Ordinal))
                {
                    SetAudioState(AgentAudioStates.Unavailable, _audioReason ?? reason);
                }
            }

            void Fail(string why)
            {
                SetAudioState(AgentAudioStates.Unavailable, why);

                log($"[Audio] 会话 {SessionId} 没有音频：{why}");
            }
        }

        // ── 输入回传循环（M5.2）──

        /// <summary>
        /// 架好输入管道（<b>方向 In</b>：服务写、这里读），读到一条就注入一条。
        ///
        /// <para>
        /// 🔥 <b>收场时必须"松开所有还按着的键"</b>：中心那边可能正好在
        /// "按下 Ctrl"与"抬起 Ctrl"之间断了（页面关了、网络断了、开关被关掉）。
        /// 不松开的后果是<b>那个键在被控机上一直按着</b> ——
        /// 用户之后每一次点击都变成 Ctrl+点击，而屏幕上完全看不出原因。
        /// 这是远控里最经典的一类事故，所以它由本方法无条件兜住，不依赖对面发"抬起"。
        /// </para>
        ///
        /// <para>
        /// ⚠️ <b>本方法绝不抛</b>：托盘是 WinExe，未捕获异常会让进程悄无声息地消失
        /// （用户看到的是"托盘图标不见了"，而画面还在推 —— 那种组合根本没法解释）。
        /// </para>
        /// </summary>
        private async Task InputLoopAsync(
            CancellationToken cancellationToken,
            IInputInjector injector,
            Action<string> log)
        {
            NamedPipeServerStream? pipe = null;

            try
            {
                pipe = new NamedPipeServerStream(
                    RemoteInputProtocol.PipeNameForSession(SessionId),
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,

                    // 站在"建管道这一方"看：这条管子我们只**读**，所以 in 缓冲给大、
                    // out 缓冲给 0。事件很小，64 KB 足够几千条。
                    inBufferSize: 64 * 1024,
                    outBufferSize: 0);

                // ── 等"下指令的人"连上来 ──
                //
                // ⚠️ 超时之后**不报错退出整路会话**：画面照常在推，只是点不动。
                //    把"点不动"升级成"看不了"是拿大故障换小故障。
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(RemoteInputProtocol.ConnectTimeoutMs);

                    try
                    {
                        await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        _lastInputError =
                            $"等了 {RemoteInputProtocol.ConnectTimeoutMs} 毫秒也没有人来连输入管道，"
                            + "这路会话只能看、不能控。";

                        log($"[Input] 会话 {SessionId}：{_lastInputError}");

                        return;
                    }
                }

                log($"[Input] 输入通道已连接（会话={SessionId}），可以注入。");

                while (!cancellationToken.IsCancellationRequested)
                {
                    RemoteInputEventDto? input;

                    try
                    {
                        input = await RemoteInputProtocol.ReadAsync(pipe, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // 畸形帧：那是两边对协议的理解不一致，继续读只会注入一堆假事件
                        // （而假事件会真的作用到用户的桌面上）。到此为止。
                        _lastInputError = $"输入读取中断：{ex.Message}";

                        log($"[Input] 会话 {SessionId} {_lastInputError}");

                        return;
                    }

                    // null = 对面干净地结束了（控制开关关了 / 服务退出）。
                    if (input is null)
                    {
                        log($"[Input] 会话 {SessionId}：对面已关闭输入通道。");

                        return;
                    }

                    Inject(input, injector, log);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常收场（停止推流 / 托盘退出）。
            }
            catch (Exception ex)
            {
                _lastInputError = $"{ex.GetType().Name}：{ex.Message}";

                log($"[Input] 会话 {SessionId} 输入通道中断：{_lastInputError}");
            }
            finally
            {
                var released = ReleasePressedKeys(injector, log);

                log($"[Input] 会话 {SessionId} 收场：注入 {Interlocked.Read(ref _inputsApplied)} 条"
                    + $"｜拒绝 {Interlocked.Read(ref _inputsRejected)} 条"
                    + $"｜松开 {released} 个仍按着的键");

                try
                {
                    if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 忽略。
                }
            }
        }

        /// <summary>
        /// 注入一条事件。
        /// <para>
        /// ⚠️ <b>不认识的类型一律拒绝并计数</b>，而不是"忽略掉算了"：
        /// 忽略会让"新版中心发了个新事件、旧托盘不认识"这件事<b>完全无声</b> ——
        /// 现场只能看到"某个手势不好使"，而日志上干干净净。
        /// </para>
        /// </summary>
        private void Inject(RemoteInputEventDto input, IInputInjector injector, Action<string> log)
        {
            switch (input.Kind)
            {
                case RemoteInputKinds.PointerMove:
                {
                    // 🔥 归一化（相对**这一路画面**）→ 虚拟桌面像素。
                    //    换算在这里做而不是在上游：只有这一层同时知道
                    //    "画面是哪块屏"与"那块屏在虚拟桌面里的偏移"。
                    var (x, y) = InputCoordinates.ToPixels(
                        input.X, input.Y, _screenLeft, _screenTop, Width, Height);

                    injector.MovePointer(x, y);

                    break;
                }

                case RemoteInputKinds.PointerButton:
                {
                    if (!TryParseButton(input.Button, out var button))
                    {
                        Reject(input, $"不认识的鼠标键「{input.Button ?? "(空)"}」", log);

                        return;
                    }

                    injector.PointerButton(button, input.Down);

                    if (input.Down) _pressedButtons.Add(button);
                    else _pressedButtons.Remove(button);

                    break;
                }

                case RemoteInputKinds.Wheel:
                    injector.ScrollPointer(input.Delta);

                    break;

                case RemoteInputKinds.Key:
                {
                    // 虚拟键码是 1–254 的一个字节（见 Windows 的 VK 定义），
                    // 越界的一律拒绝：把它截断成低字节会把"某个不存在的键"
                    // 变成"一个真实存在的键"，而那是一次**无意的**按键。
                    if (input.VirtualKey <= 0 || input.VirtualKey > 0xFE)
                    {
                        Reject(input, $"虚拟键码越界（{input.VirtualKey}）", log);

                        return;
                    }

                    injector.KeyState(input.VirtualKey, input.Down);

                    if (input.Down) _pressedKeys.Add(input.VirtualKey);
                    else _pressedKeys.Remove(input.VirtualKey);

                    break;
                }

                default:
                    Reject(input, $"不认识的事件类型「{input.Kind ?? "(空)"}」", log);

                    return;
            }

            Interlocked.Increment(ref _inputsApplied);
        }

        private void Reject(RemoteInputEventDto input, string reason, Action<string> log)
        {
            var rejected = Interlocked.Increment(ref _inputsRejected);

            _lastInputError = reason;

            // ⚠️ 按 30 条节流：坏掉的输入流可能以几十赫兹一直来，
            //    一秒钟一条日志会把托盘日志刷爆，反而盖住真正的问题。
            if (rejected % 30 == 1)
            {
                log($"[Input] 会话 {SessionId} 拒绝输入：{reason}（累计拒绝 {rejected} 条，事件={input.Kind}）");
            }
        }

        /// <summary>
        /// 松开所有还按着的键。返回松开的<b>键数</b>（含鼠标键）。
        /// </summary>
        /// <remarks>
        /// 🔥 这是本类里最值钱的十行：少了它，"远控过程中 Ctrl 一直按着"
        /// 这类故障在用户眼里是"我的电脑坏了"，而我们这边一点痕迹都没有。
        /// </remarks>
        private int ReleasePressedKeys(IInputInjector? injector, Action<string> log)
        {
            if (injector is null) return 0;

            var count = _pressedButtons.Count + _pressedKeys.Count;

            if (count == 0) return 0;

            foreach (var button in _pressedButtons)
            {
                injector.PointerButton(button, down: false);
            }

            foreach (var key in _pressedKeys)
            {
                injector.KeyState(key, down: false);
            }

            _pressedButtons.Clear();
            _pressedKeys.Clear();

            log($"[Input] 会话 {SessionId}：已强制松开 {count} 个仍处于按下状态的键"
                + "（否则它们会在被控机上一直按着）。");

            return count;
        }

        private static bool TryParseButton(string? raw, out MouseButtonKind button)
        {
            switch (raw)
            {
                case RemoteMouseButtons.Left: button = MouseButtonKind.Left; return true;
                case RemoteMouseButtons.Right: button = MouseButtonKind.Right; return true;
                case RemoteMouseButtons.Middle: button = MouseButtonKind.Middle; return true;
                default: button = MouseButtonKind.Left; return false;
            }
        }

        private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 由主循环的下一轮判断收工。
            }
        }
    }
}
