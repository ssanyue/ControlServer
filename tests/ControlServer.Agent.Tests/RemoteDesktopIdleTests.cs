using System.Diagnostics;
using System.IO.Pipes;
using ControlServer.Agent.Tasks;
using ControlServer.Contracts;
using ControlServer.Platform.Abstractions;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// <b>被控端说得出"我的桌面已经静止了多久"</b>（M5.4-c② 步 7 的托盘这一侧）。
///
/// <para>
/// 🔥🔥 <b>它关掉的是一条很贵的挂账</b>（<c>REF-M5 §14.14</c>）：那次修完"活性判据"之后，
/// <b>「桌面静止」与「托盘卡住」在协议层上长得一模一样</b> ——
/// 都是"没有新帧、状态照报"，中心只能靠猜。而被控端<b>早就知道</b>
/// （DXGI 抓屏返回 <c>null</c> 就是"桌面内容与指针都没变"），它只是<b>不说</b>。
/// </para>
///
/// <para>
/// ⚠️ 这组用例<b>不验"数字准不准到毫秒"</b>（那需要可注入时钟，本仓没有）——
/// 它验的是三件在页面上看起来完全不同的事：
/// <list type="number">
///   <item>桌面真的静止 ⇒ 数字<b>在长</b>（"已 12 秒"）；</item>
///   <item>桌面在变 ⇒ 数字<b>接近 0</b>（"刚动过"）；</item>
///   <item>没有流 ⇒ <b>失败</b>，不是"静止 0 毫秒"（这两件事的处置完全不同）。</item>
/// </list>
/// </para>
///
/// <para>
/// 🔴 <b>第三件事为什么值得单独立一条</b>：如果用 <c>0</c> 表示"问不到"，
/// 那么"这一路根本没在推流"与"桌面刚动过"在页面上就是同一句话，
/// 而这正是 <see cref="AgentRemoteSessionStateDto"/> 那一族字段反复强调的
/// <b>「不许把 <c>null</c> 与 <c>0</c> 压成同一件事」</b>。
/// </para>
/// </summary>
public sealed class RemoteDesktopIdleTests
{
    /// <summary>托盘写出来的日志（行为用例要断言「退避 / 恢复」这两句话真的出现过）。</summary>
    private readonly List<string> _logs = new();
    /// <summary>
    /// 🔥 <b>核心用例</b>：桌面一直不变 ⇒ 问出来的静止时长<b>在增长</b>，而且能从两处读到。
    /// </summary>
    /// <remarks>
    /// ⚠️ 等 <b>1.2 秒</b>而不是 100 毫秒：判据是"至少静止了 0.9 秒"，
    /// 这样它对调度抖动有余量，而对"压根没在数"（恒 0）则是零容忍 ——
    /// 恒 0 会以 <c>0 &gt; 900</c> 失败，一眼看得出是哪一种坏法。
    /// </remarks>
    [Fact]
    public async Task A_static_desktop_reports_a_growing_idle_time()
    {
        var sessionId = "idle-static-" + Guid.NewGuid().ToString("N")[..8];

        var encoder = new ScriptedSurfaceEncoder();
        var capturer = new StubCapturer { NeverChanges = true };

        // 🔥 GPU 那条路上，抓屏器是**编码器自己持有的**（零拷贝要求共用同一个 D3D11 设备）
        //    ⇒ 不交过去，宿主用的会是另一个"会出画面"的抓屏器，
        //    于是这条用例会以一种非常难懂的方式红（桌面明明"静止"，却一直有帧）。
        encoder.CapturerToReport = capturer;

        await using var host = NewHost(encoder, capturer);

        // 🔴 **必须有人接那条码流管道**：推流循环在"接收端还没连上"之前**根本不会开始抓屏**
        //    （`RemoteStreamProtocol.ConnectTimeoutMs` 那一段）。不接的话，下面量到的
        //    "静止时长"其实只是"从建会话到现在过了多久"—— 一条**看起来对、什么也没证**的绿。
        using var receiver = StartReceiver(sessionId);

        host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 30))
            .Ok.Should().BeTrue();

        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        var answer = host.DescribeDesktopIdle();

        answer.Ok.Should().BeTrue($"这一路正在推流，问一句静止时长本该有答案；实际：{answer.Message}");

        answer.DesktopIdleMs.Should().NotBeNull(
            "桌面确实静止了 ⇒ 必须给出一个**数字**；`null` 的含义是「问不到 / 旧托盘不认识」，"
            + "拿它顶替会让人以为这台机器不提供这个信息");

        answer.DesktopIdleMs!.Value.Should().BeGreaterThan(
            900,
            "判据是「至少静止了 0.9 秒」。恒 0 说明根本没在计时 —— 而那正是本步要修的那个「不说」");

        // ── 同一份事实也必须能从状态快照里读到（托盘自检 / 日志走的是它）──
        //
        // ⚠️ 这里**不能断言两个数逐毫秒相等**：它是个"到此刻为止的时长"，
        //    两次读之间本来就隔着几毫秒（第一版用例这么写，红在 1211 vs 1214）。
        //    真正该钉的不变式是"两处指向同一个起点"，而不是"两次读数一样"。
        host.Status.Should().NotBeNull();

        host.Status!.DesktopIdleMs.Should().NotBeNull("快照里那一格也必须有值，否则托盘自检与日志看不到它");

        (host.Status.DesktopIdleMs!.Value - answer.DesktopIdleMs.Value).Should().BeInRange(
            -500,
            500,
            "两处必须指向同一个起点：分成两份计时必然漂，而漂掉的表现是"
            + "「页面说静止 3 秒、日志说 12 秒」，谁也不知道该信哪个");
    }

    /// <summary>
    /// 桌面在变 ⇒ 数字接近 <c>0</c>（"刚动过"），而<b>不是</b> <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 🔥 这一条钉的是三态里的中间那一格。<c>0</c> 与 <c>null</c> 在页面上是两句话
    /// （"刚动过" / "这台机器不提供这个信息"），把它们合并之后，
    /// "旧托盘"与"正在动的桌面"就再也分不开了。
    /// </remarks>
    [Fact]
    public async Task A_changing_desktop_reports_almost_zero_idle_time()
    {
        var sessionId = "idle-live-" + Guid.NewGuid().ToString("N")[..8];

        var encoder = new ScriptedSurfaceEncoder();
        var capturer = new StubCapturer { NeverChanges = false };

        encoder.CapturerToReport = capturer;

        await using var host = NewHost(encoder, capturer);

        // 同上：接上接收端，推流循环才会真的跑起来（否则量的只是"建会话到现在"）。
        using var receiver = StartReceiver(sessionId);

        host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 30))
            .Ok.Should().BeTrue();

        // 让它真的抓上几帧（一帧都不出的话，下面量到的还是"建会话到现在"）。
        await Task.Delay(TimeSpan.FromMilliseconds(400));

        var answer = host.DescribeDesktopIdle();

        answer.Ok.Should().BeTrue($"这一路正在推流；实际：{answer.Message}");

        answer.DesktopIdleMs.Should().NotBeNull("桌面一直在变，但「刚动过」也是一个**答案**，不是「不知道」");

        // ⚠️ 上限用 300 毫秒而不是 0：问答之间隔着一次抓屏周期（30 fps ⇒ 33 毫秒）
        //    与一次管道往返，卡在 0 上会变成一条取决于调度的假 FAIL。
        answer.DesktopIdleMs!.Value.Should().BeLessThan(
            300,
            "画面一直在变 ⇒ 静止时长必须贴着 0，而不是一个越走越大的数");
    }

    /// <summary>
    /// 没有正在推流的会话 ⇒ <b>失败</b>，而不是"静止了 0 毫秒"。
    /// </summary>
    /// <remarks>
    /// 🔥 与 <c>RequestKeyframe</c> / <c>SetGrayscale</c> 同形：这条查询的作用对象是
    /// "正在推的那一路"，没有它就没有对象。<b>安静地回 0</b> 会让服务侧把
    /// "这台机器根本没有流"上报成"桌面刚动过" —— 一句看不出是故障的话。
    /// </remarks>
    [Fact]
    public async Task Querying_the_idle_time_without_a_stream_fails()
    {
        var encoder = new ScriptedSurfaceEncoder();
        var capturer = new StubCapturer();

        encoder.CapturerToReport = capturer;

        await using var host = NewHost(encoder, capturer);

        var answer = host.DescribeDesktopIdle();

        answer.Ok.Should().BeFalse("没有流就没有「静止多久」这个问题");

        answer.DesktopIdleMs.Should().BeNull(
            "失败时**不许**顺手回一个 0 —— 那会把「没有流」画成「桌面刚动过」");

        answer.Message.Should().Contain(
            "推流",
            "这句话会原样进服务侧日志：它必须说清「此刻没有正在推流的会话」，"
            + "而不是一句「查询失败」让人去查管道");
    }

    // ═══════════════════════════════════════════════════════════
    // 静止退避（M5.4-c② 步 8）—— 行为那一层
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>桌面一直不变 ⇒ 抓屏轮询明显变慢，但<b>绝不停</b>。</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判据落在"<b>一秒里被抓了几次屏</b>"上，而不是"日志里有没有那句话"：
    /// 日志可以照打而轮询全速跑，那时 CPU 一点没省 —— 而这正是本步唯一的目的。
    /// </para>
    /// <para>
    /// 🔴 <b>下限 <c>&gt;= 1</c> 与本组的第一条铁律一一对应</b>：退避的下限必须仍然
    /// <b>定期有上行</b>。把"降帧"做成"停止"会让展厅里定格在一张图上的机器
    /// 被中心判死（<c>REF-M5 §14.14</c> 已经翻过一次）。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_static_desktop_backs_the_polling_off_but_never_stops()
    {
        var sessionId = "idle-backoff-" + Guid.NewGuid().ToString("N")[..8];

        var encoder = new ScriptedSurfaceEncoder();
        var capturer = new StubCapturer { NeverChanges = true };

        encoder.CapturerToReport = capturer;

        await using var host = NewHost(encoder, capturer);

        using var receiver = StartReceiver(sessionId);

        // 🔥 目标 60 fps：退避后应当落到 10 fps（1/6），所以"一秒几十次" vs "一秒十次"
        //    这个差别足够大，不会被调度抖动抹平（而 12 fps 那档只差 12 vs 2，也够，
        //    但它离"一秒窗"太近，容易量出边界值）。
        host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 60))
            .Ok.Should().BeTrue();

        // 先跑过退避阈值（2 秒）再多给 1 秒余量。
        await Task.Delay(TimeSpan.FromMilliseconds(3200));

        var before = capturer.CaptureCalls;

        await Task.Delay(TimeSpan.FromMilliseconds(1000));

        var late = capturer.CaptureCalls - before;

        late.Should().BeLessThan(
            25,
            "桌面静止 4 秒之后，抓屏轮询必须已经退避下来（60 fps ⇒ 10 fps 量级）。"
            + "一秒里还是几十次，说明退避根本没生效，CPU 一分没省");

        late.Should().BeGreaterThanOrEqualTo(
            1,
            "🔴 退避**不是停止**：降帧的下限必须仍然定期抓屏。"
            + "完全停下来会让中心把它判成死机器 —— 而展厅里那台正是本步要救的");

        host.IsStreaming.Should().BeTrue("一路静止的画面仍然是一路活着的流");

        Logs().Should().Contain(
            line => line.Contains("退避", StringComparison.Ordinal),
            "页面上省了 CPU、日志里一个字都没有，下一个人会以为这是「莫名其妙变卡了」");
    }

    /// <summary>
    /// 🔥 <b>桌面一动，立刻回到目标帧率</b>（不是"下一个退避周期之后"）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 它守的是一个用户体验上的硬要求：静止退避省的是 CPU，但<b>不能让人一动手就等</b>。
    /// 退避后的周期是 100 毫秒（60 fps 那档） ⇒ 恢复最多晚 100 毫秒，
    /// 而如果实现成"等到下一个整秒"或者"要连续几帧才恢复"，用户会明确感到"卡了一下"。
    /// </para>
    /// <para>
    /// ⚠️ 断言给到 400 毫秒的余量：里面有抓屏周期、编码、管道写、以及一次状态查询的往返。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_changed_desktop_returns_to_the_target_rate_immediately()
    {
        var sessionId = "idle-recover-" + Guid.NewGuid().ToString("N")[..8];

        var encoder = new ScriptedSurfaceEncoder();
        var capturer = new StubCapturer { NeverChanges = true };

        encoder.CapturerToReport = capturer;

        await using var host = NewHost(encoder, capturer);

        using var receiver = StartReceiver(sessionId);

        host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 60))
            .Ok.Should().BeTrue();

        // 先让它真的进入退避（2 秒阈值 + 余量）。
        await Task.Delay(TimeSpan.FromMilliseconds(3200));

        Logs().Should().Contain(
            line => line.Contains("退避", StringComparison.Ordinal),
            "前置条件：得先退避过，否则下面量的是「从来没退避」而不是「恢复」");

        // ── 桌面开始变化 ──
        capturer.NeverChanges = false;

        await Task.Delay(TimeSpan.FromMilliseconds(400));

        var answer = host.DescribeDesktopIdle();

        answer.DesktopIdleMs.Should().NotBeNull();
        answer.DesktopIdleMs!.Value.Should().BeLessThan(
            300,
            "桌面已经在变了 ⇒ 静止时长必须立刻归零；它要是一直很大，说明轮询没恢复");

        Logs().Should().Contain(
            line => line.Contains("恢复", StringComparison.Ordinal),
            "「退避到 N fps」与「恢复目标帧率」必须**成对**出现：只有前者的话，"
            + "现场永远不知道这台机器还在不在省 CPU");
    }

    /// <summary>本用例收集到的日志（写日志的是别的线程 ⇒ 必须加锁复制）。</summary>
    private List<string> Logs()
    {
        lock (_logs) return new List<string>(_logs);
    }

    // ═══════════════════════════════════════════════════════════
    // 夹具
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 接上这条会话的<b>码流管道</b>并把帧读掉。
    ///
    /// <para>
    /// 🔥 <b>不接会怎样</b>：推流循环在"接收端还没连上"之前<b>根本不开始抓屏</b>
    /// （见 <c>RemoteStreamProtocol.ConnectTimeoutMs</c>）。于是量到的"静止时长"
    /// 只是"从建会话到现在过了多久"—— 一条<b>看起来对、其实什么都没证</b>的绿。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 只读不解析：本组用例关心的是"抓屏有没有在跑"，不是码流内容。
    /// 用 <c>using</c> 收场，退出时取消 —— 读的任务不必 await（它一直被取消观察着）。
    /// </para>
    /// </summary>
    private static CancellationTokenSource StartReceiver(string sessionId)
    {
        var cts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".", RemoteStreamProtocol.PipeNameForSession(sessionId), PipeDirection.In, PipeOptions.Asynchronous);

                await client.ConnectAsync(3000, cts.Token);

                while (!cts.Token.IsCancellationRequested)
                {
                    var frame = await RemoteStreamProtocol.ReadFrameAsync(client, cts.Token);

                    if (frame is null) break;
                }
            }
            catch
            {
                // 收场 / 超时都无所谓：用例自己的断言会说话。
            }
        }, CancellationToken.None);

        return cts;
    }

    private RemoteStreamHost NewHost(ScriptedSurfaceEncoder encoder, StubCapturer capturer)
        => new(
            capturers: () => capturer,
            encoders: _ => encoder,
            injector: null,
            inputAllowed: () => false,
            log: line =>
            {
                lock (_logs) _logs.Add(line);
            },

            // ⚠️ 音频工厂传 null：这组用例与声音毫无关系，接上去只会去真采系统声音
            //    （在没声卡的机器上变成一条与本步无关的假 FAIL）。
            audioCapturers: null,
            audioEncoders: null);

    /// <summary>
    /// 最小抓屏器：<see cref="NeverChanges"/> 为真时永远回"没有新帧"（模拟静止桌面）。
    /// <para>⚠️ 不碰任何平台 API（用例要在非 Windows 上也能编译）。</para>
    /// </summary>
    private sealed class StubCapturer : IScreenCapturer
    {
        private long _captureCalls;

        private static readonly ScreenDescriptor Primary =
            new("screen-primary", "主屏", 2, 2, IsPrimary: true);

        /// <summary>
        /// 永远回"没有新帧"（DDA 的约定，不是错误）。
        /// <para>⚠️ 可写：恢复那条用例要在一路推流途中把它翻成 false（"用户动鼠标了"）。</para>
        /// </summary>
        public bool NeverChanges { get; set; }

        /// <summary>抓屏被调用了多少次 —— "退避到底省没省 CPU"唯一的判据。</summary>
        public long CaptureCalls => Interlocked.Read(ref _captureCalls);

        public IReadOnlyList<ScreenDescriptor> EnumerateScreens() => [Primary];

        public Task<CapturedFrame> CaptureAsync(string screenId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CapturedFrame(2, 2, 8, new byte[16], DateTimeOffset.UtcNow));

        public Task<CapturedSurface?> CaptureSurfaceAsync(
            string screenId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _captureCalls);

            return Task.FromResult<CapturedSurface?>(NeverChanges
                ? null
                : new CapturedSurface(2, 2, TextureHandle: 1, SurfacePixelFormat.Bgra8, false, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>
    /// 脚本化的 GPU 路径编码器：每一轮都交出一段固定的 Annex-B 访问单元。
    /// <para>
    /// ⚠️ 这组用例<b>不关心编出来的字节</b>，只关心"是不是有帧在流"；
    /// 交给它一段真实形状的 IDR 头是为了让"关键帧 / 参数集"那条路径不报错。
    /// </para>
    /// </summary>
    private sealed class ScriptedSurfaceEncoder : ISurfaceFrameEncoder
    {
        private static readonly byte[] AccessUnit = VideoTestVectors.AnnexB(
            VideoTestVectors.RealH264Sps,
            VideoTestVectors.RealH264Pps,
            VideoTestVectors.RealH264IdrHead);

        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public IScreenCapturer? CapturerToReport { get; set; }

        public IScreenCapturer Capturer => CapturerToReport
            ?? throw new InvalidOperationException("用例必须先给一个抓屏器（见 CapturerToReport 的说明）。");

        public string ScreenId => "screen-primary";

        public string ScreenName => "主屏";

        public (int Width, int Height) FrameSize => (2, 2);

        public string Codec => StreamCodecs.H264;

        public string? LastFailure => null;

        public long DiscardedFrames => 0;

        public bool Grayscale { get; set; }

        public bool TryEncodeSurface(CapturedSurface surface, bool forceKeyframe, out EncodedStreamPayload? encoded)
        {
            encoded = new EncodedStreamPayload(
                Codec: StreamCodecs.H264,
                IsKeyframe: true,
                Width: surface.Width,
                Height: surface.Height,
                CapturedUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data: AccessUnit,
                Description: null,
                CapturedMonoTicks: _clock.ElapsedTicks);

            return true;
        }

        public bool TryReadParameterSets(out byte[] parameterSets)
        {
            parameterSets = [];

            return false;
        }

        public void Dispose()
        {
        }
    }
}
