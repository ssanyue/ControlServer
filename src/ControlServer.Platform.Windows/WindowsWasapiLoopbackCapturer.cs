using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// Windows <b>系统声音采集</b>（M5.3），走 WASAPI 的 <b>loopback</b>（环回）模式。
///
/// <para>
/// 🔥 <b>为什么是 loopback、而且只能在这里做</b>：远控要听到的是<b>被控机放出来的声音</b>，
/// 不是麦克风。而 <b>Session 0 没有音频端点</b>（服务就住在那里），
/// 所以采集与抓屏、注入受同一条约束：<b>只能由用户会话里的托盘进程做</b>。
/// </para>
///
/// <para><b>格式策略：主动要求一个我们想要的格式，而不是"拿设备给什么就用什么"</b></para>
/// <list type="bullet">
///   <item>设备混音格式在 Windows 上通常是 <b>32 位浮点</b>，而 Opus 要的是 <b>16 位整数</b>；</item>
///   <item>采样率也不一定是 48000，而 <b>Opus 只支持 8/12/16/24/48 kHz</b> ——
///         拿到 44100 的机器若原样送下去，编码器会直接拒绝；</item>
///   <item>所以这里请求 <c>48000 / 立体声 / 16 位 PCM</c>，并打开
///         <c>AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | SRC_DEFAULT_QUALITY</c>，
///         让<b>音频引擎自己</b>做重采样与格式转换。</item>
/// </list>
/// <para>
/// 🔥 <b>为什么宁可依赖引擎也不自己写重采样</b>：引擎那个转换器是系统级的、质量远高于
/// "随手写的线性插值"，而且它是<b>在音频线程上以正确的时间关系</b>完成的 ——
/// 自己插值会把 AV 同步的时间轴也一起搞乱。代价是若某些驱动不认这组标志，
/// 我们就<b>明确报"不可用"</b>（带原因），而不是送出一段听不出问题的坏音频。
/// </para>
///
/// <para><b>线程模型</b>：WASAPI 的 COM 对象要求"创建与使用在同一个套间里"，
/// 而调用方是托盘的异步循环（线程是换着用的）。所以本类<b>自己起一条专用线程</b>，
/// 在那条线程上完成 <c>CoInitializeEx</c> → 建客户端 → 循环读 → 收场，
/// 对外只暴露一个异步序列。见 <see cref="CaptureAsync"/>。
/// </para>
///
/// <para>
/// ⚠️ <b>没有音频输出设备的机器</b>（虚拟机 / 无声卡工控机 / 声卡被禁用）上
/// <c>GetDefaultAudioEndpoint</c> 会返回 <c>E_NOTFOUND</c>。这时 <see cref="IsAvailable"/> 为假、
/// <see cref="AvailabilityNote"/> 给出人话原因，而不是抛一个 <c>COMException</c> 上去
/// 把整条推流链打断 —— 这<b>不是</b>异常情况，是一种真实存在的机器。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWasapiLoopbackCapturer : IAudioCapturer
{
    // ── CoreAudio / WASAPI 常量 ──────────────────────────────────────────────

    /// <summary>CLSCTX_INPROC_SERVER：CoreAudio 的实现是进程内 COM 组件。</summary>
    private const int ClsCtxInProcServer = 0x1;

    /// <summary>EDataFlow.eRender：<b>输出</b>设备 —— loopback 采的是"播出来的声音"。</summary>
    private const int Render = 0;

    /// <summary>ERole.eConsole：系统声音所属的默认端点。</summary>
    private const int Console = 0;

    /// <summary>AUDCLNT_SHAREMODE_SHARED：共享模式（不独占设备）。</summary>
    private const int ShareModeShared = 0;

    /// <summary>AUDCLNT_STREAMFLAGS_LOOPBACK：环回 —— 抓"本机正在播放的声音"。</summary>
    private const int StreamFlagLoopback = 0x00020000;

    /// <summary>
    /// AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM：允许音频引擎做格式转换。
    /// <para>⚠️ 它是 <c>0x80000000</c>，作为 <c>int</c> 是负数 —— 必须 <c>unchecked</c>，
    /// 否则编译期会直接报"常量太大"，或者更糟：有人把它写成 <c>0x00000000</c> 而毫无察觉。</para>
    /// </summary>
    private const int StreamFlagAutoConvertPcm = unchecked((int)0x80000000);

    /// <summary>AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY：使用引擎默认采样率转换质量。</summary>
    private const int StreamFlagSrcDefaultQuality = 0x08000000;

    /// <summary>AUDCLNT_BUFFERFLAGS_SILENT：这一段是静音，缓冲区内容<b>未定义</b>。</summary>
    private const uint BufferFlagSilent = 0x2;

    /// <summary>HRESULT E_NOTFOUND：没有该类设备。</summary>
    private const int ENotFound = unchecked((int)0x80070490);

    /// <summary>COINIT_MULTITHREADED。</summary>
    private const uint CoInitMultithreaded = 0x0;

    /// <summary>没有可读数据时的轮询间隔。<b>必须远小于 20 ms 一帧的节奏</b>，见类的说明。</summary>
    private const int PollIntervalMs = 4;

    /// <summary>采集队列容量。约 1.3 秒（64 × 20 ms）—— 见 <see cref="CaptureAsync"/> 的说明。</summary>
    private const int QueueCapacity = 64;

    // ── 状态 ────────────────────────────────────────────────────────────────

    private readonly AudioCaptureFormat _format = new(SampleRate, Channels);

    /// <summary>我们要的采样率。48 kHz 是 Opus 的原生采样率，转换损失最小。</summary>
    private const int SampleRate = 48_000;

    /// <summary>我们要的声道数。</summary>
    private const int Channels = 2;

    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();

    private Channel<AudioChunk>? _queue;
    private Thread? _thread;
    private bool _disposed;

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;

            var client = TryOpenClient(out _, out _);

            if (client is null) return false;

            Release(client);

            return true;
        }
    }

    /// <inheritdoc />
    public string AvailabilityNote
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return "当前平台未实现系统声音采集（M5.3 的远程桌面只覆盖 Windows 被控端）。";
            }

            var client = TryOpenClient(out var error, out _);

            if (client is null) return error ?? "拿不到可采集的默认音频输出设备。";

            Release(client);

            return string.Empty;
        }
    }

    /// <inheritdoc />
    public AudioCaptureFormat? Format => IsAvailable ? _format : null;

    /// <inheritdoc />
    public IAsyncEnumerable<AudioChunk> CaptureAsync(CancellationToken cancellationToken)
    {
        // ⚠️ 本方法**刻意不是 iterator**：iterator 里的代码要等第一次 MoveNext 才跑，
        //    而"起线程"这件事必须在这里就发生 —— 否则调用方拿到一个序列、
        //    以为开始采了，实际什么都没起（表现为"永远等不到第一段音频"）。
        StartThreadOnce();

        var reader = _queue?.Reader;

        if (reader is null) return EmptyAsync(cancellationToken);

        // ⚠️ 取消要一路传到采集线程：那条线程正在 WaitForSingleObject 之外自己轮询，
        //    不把它叫醒的话它会一直读到设备消失为止。
        var registration = cancellationToken.Register(() =>
        {
            try
            {
                _lifetime.Cancel();
            }
            catch
            {
                // 已释放 / 正在退出：无计可施。
            }
        });

        return Wrap(reader, registration, cancellationToken);
    }

    private static async IAsyncEnumerable<AudioChunk> Wrap(
        ChannelReader<AudioChunk> reader,
        CancellationTokenRegistration registration,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            // 读方退出（正常 / 取消 / 抛）都要把采集线程一并收掉，否则它会一直跑下去。
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async IAsyncEnumerable<AudioChunk> EmptyAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    private void StartThreadOnce()
    {
        lock (_sync)
        {
            if (_thread is not null || _disposed) return;

            _queue = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(QueueCapacity)
            {
                // 🔥 队列满 ⇒ 丢<b>最旧的</b>，而不是阻塞采集线程。
                //    阻塞采集线程的后果比丢几段音频严重得多：那是**音频引擎的缓冲被填满**，
                //    表现是整个系统的声音出问题（不只是我们听不到）。
                //    老实说这是"两害相权"：真正要避免的是走到这一步 ——
                //    一段 20 ms 的 Opus 只有几百字节，本地管道写不下它的概率极低。
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });

            _thread = new Thread(() => RunCaptureThread(_queue!.Writer))
            {
                IsBackground = true,

                // 名字进调试器与日志，出事时能一眼看出是谁。
                Name = "wasapi-loopback-capture",
            };

            _thread.Start();
        }
    }

    /// <summary>
    /// 采集线程的整个生命周期（<b>COM 的创建与使用都在这条线程上</b>）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>它绝不抛</b>：托盘是 WinExe，未捕获异常会让进程悄无声息地消失
    /// （用户看到的是"托盘图标不见了"，而画面还在推 —— 那种组合根本没法解释）。
    /// </remarks>
    private void RunCaptureThread(ChannelWriter<AudioChunk> writer)
    {
        var comInitialized = false;

        object? enumerator = null;
        object? device = null;
        object? clientObject = null;
        object? captureObject = null;

        try
        {
            var hr = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);

            // S_OK(0) 与 S_FALSE(1) 都表示"这条线程上 COM 可用了"，
            // 区别只是"是不是我们初始化的"。两者都需要配对一次 CoUninitialize。
            comInitialized = hr >= 0;

            var client = OpenClientOnThisThread(out enumerator, out device, out clientObject);

            if (client is null)
            {
                writer.TryComplete();
                return;
            }

            var started = false;

            try
            {
                var iid = typeof(IAudioCaptureClient).GUID;

                hr = client.GetService(ref iid, out captureObject);

                if (hr < 0 || captureObject is null)
                {
                    writer.TryComplete();
                    return;
                }

                var capture = (IAudioCaptureClient)captureObject;

                hr = client.Start();

                if (hr < 0)
                {
                    writer.TryComplete();
                    return;
                }

                started = true;

                // ── 时间轴基准：把 QPC 的 tick 与墙钟在**同一瞬间**对上 ──
                //
                // 🔥 这样做是为了让每段的 CapturedAt 由 QPC 推出来，而不是"读到的时刻"：
                //    读到的时刻会带上轮询间隔（最多 PollIntervalMs）与调度抖动，
                //    而用 QPC 推出来的那一份**不含这些**，同时还能免疫会话中途的墙钟跳变。
                var qpcAtStart = Stopwatch.GetTimestamp();
                var wallAtStart = DateTimeOffset.UtcNow;

                var token = _lifetime.Token;

                while (!token.IsCancellationRequested)
                {
                    hr = capture.GetNextPacketSize(out var pending);

                    if (hr < 0) break;

                    if (pending == 0)
                    {
                        // 没有新数据：睡一小会儿再来。设备在"没人放声音"时也会持续产出
                        // 静音包，所以这里的空转通常只发生在两次设备周期之间（约 10 ms）。
                        Thread.Sleep(PollIntervalMs);
                        continue;
                    }

                    // 把这一批已就绪的包全部取走。**必须循环取到 0**，而不是"一次取一个、下次再说"：
                    // 取不完会一直累积，表现为音频越来越滞后于画面（AV 同步被慢慢拉坏）。
                    while (pending > 0 && !token.IsCancellationRequested)
                    {
                        hr = capture.GetBuffer(
                            out var data,
                            out var frames,
                            out var flags,
                            out _,
                            out var qpcPosition);

                        if (hr < 0) break;

                        try
                        {
                            if (frames > 0)
                            {
                                var samples = new short[checked((int)frames * Channels)];
                                var silent = (flags & BufferFlagSilent) != 0;

                                // 🔥 静音时**绝不去拷缓冲区**：那里面是上一段的残留，
                                //    拷出来就是"没放声音时反而听到旧声音"。
                                //    数组刚 new 出来本来就是全 0，正是我们要的。
                                if (!silent && data != IntPtr.Zero)
                                {
                                    Marshal.Copy(data, samples, 0, samples.Length);
                                }

                                var capturedAt = qpcPosition > 0
                                    ? wallAtStart.Add(TicksToTimeSpan(ConvertQpcTicks(qpcPosition) - qpcAtStart))
                                    : DateTimeOffset.UtcNow;

                                var ticks = qpcPosition > 0
                                    ? ConvertQpcTicks(qpcPosition)
                                    : Stopwatch.GetTimestamp();

                                // TryWrite 在 DropOldest 下永不阻塞、永不失败（除非已 Complete）。
                                writer.TryWrite(new AudioChunk(samples, silent, ticks, capturedAt));
                            }
                        }
                        finally
                        {
                            // ⚠️ ReleaseBuffer 必须在 finally 里：漏掉一次，这块缓冲就再也回不到引擎，
                            //    累积若干次之后采集会彻底停住（表现为"声音忽然没了，之后也不再回来"）。
                            capture.ReleaseBuffer(frames);
                        }

                        hr = capture.GetNextPacketSize(out pending);

                        if (hr < 0) break;
                    }
                }

                if (started)
                {
                    // Stop 失败无计可施（设备已经掉了），不值得打扰上层。
                    _ = client.Stop();
                }
            }
            finally
            {
                writer.TryComplete();
            }
        }
        catch
        {
            // 见方法注释：绝不外抛。
            writer.TryComplete();
        }
        finally
        {
            Release(captureObject);
            Release(clientObject);
            Release(device);
            Release(enumerator);

            if (comInitialized)
            {
                try
                {
                    CoUninitialize();
                }
                catch
                {
                    // 忽略。
                }
            }
        }
    }

    /// <summary>
    /// 取默认输出端点并激活 <see cref="IAudioClient"/>，<b>并且</b>用它尝试一次 loopback 初始化。
    /// <para>
    /// 🔥 <b>为什么要试到 <c>Initialize</c> 这一步才算"可用"</b>：
    /// "拿得到端点"与"能对这条端点做环回采集"是两件事。只验前者的话，
    /// 在那些不支持格式转换的驱动上会报"可用"，而真开始采时立刻失败 ——
    /// 用户看到的是"有声音开关、但永远是静音"。
    /// </para>
    /// </summary>
    /// <returns>成功时返回<b>所有权已交接</b>的客户端（调用方负责释放）；失败返回 <c>null</c> 并给出原因。</returns>
    private static IAudioClient? TryOpenClient(out string? error, out string? detail)
    {
        object? enumerator = null;
        object? device = null;
        object? clientObject = null;

        try
        {
            enumerator = new MMDeviceEnumeratorComObject();

            var hr = ((IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(Render, Console, out device);

            if (hr == ENotFound)
            {
                error = "本机没有可用的音频输出设备（无声卡、声卡被禁用，或没有已启用的播放设备）。"
                        + "这类机器上远程桌面照常可用，只是没有声音。";
                detail = null;

                return null;
            }

            if (hr < 0 || device is null)
            {
                error = $"拿不到默认音频输出设备（0x{hr:X8}）。";
                detail = null;

                return null;
            }

            var iid = typeof(IAudioClient).GUID;

            hr = ((IMMDevice)device).Activate(ref iid, ClsCtxInProcServer, IntPtr.Zero, out clientObject);

            if (hr < 0 || clientObject is null)
            {
                error = $"音频端点不支持环回采集（0x{hr:X8}）。";
                detail = null;

                return null;
            }

            var client = (IAudioClient)clientObject;

            var format = BuildFormat();
            var flags = StreamFlagLoopback | StreamFlagAutoConvertPcm | StreamFlagSrcDefaultQuality;

            hr = client.Initialize(ShareModeShared, flags, 0, 0, ref format, IntPtr.Zero);

            if (hr < 0)
            {
                // 🔥 报原样 HRESULT：这一句是"为什么没声音"的唯一线索，
                //    抹成一句"初始化失败"会让现场只能在驱动列表里瞎猜。
                error =
                    "默认音频输出设备不支持本次要求的采集格式（48000 Hz / 立体声 / 16 位）。"
                    + "常见于不允许格式自动转换的驱动。"
                    + $"（HRESULT 0x{hr:X8}）";

                detail = $"Initialize(loopback+autoconvert) 返回 0x{hr:X8}";

                return null;
            }

            detail = null;
            error = null;

            // 所有权交接给调用方（把局部变量置空，finally 就不会释放它）。
            clientObject = null;
            device = null;
            enumerator = null;

            return client;
        }
        catch (Exception ex)
        {
            error = $"访问音频设备失败：{ex.Message}";
            detail = ex.GetType().Name;

            return null;
        }
        finally
        {
            Release(clientObject);
            Release(device);
            Release(enumerator);
        }
    }

    /// <summary>在<b>采集线程</b>上重做一遍打开（COM 必须在这条线程上创建）。</summary>
    private static IAudioClient? OpenClientOnThisThread(
        out object? enumerator,
        out object? device,
        out object? clientObject)
    {
        enumerator = null;
        device = null;
        clientObject = null;

        try
        {
            enumerator = new MMDeviceEnumeratorComObject();

            var hr = ((IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(Render, Console, out device);

            if (hr < 0 || device is null) return null;

            var iid = typeof(IAudioClient).GUID;

            hr = ((IMMDevice)device).Activate(ref iid, ClsCtxInProcServer, IntPtr.Zero, out clientObject);

            if (hr < 0 || clientObject is null) return null;

            var client = (IAudioClient)clientObject;

            var format = BuildFormat();
            var flags = StreamFlagLoopback | StreamFlagAutoConvertPcm | StreamFlagSrcDefaultQuality;

            hr = client.Initialize(ShareModeShared, flags, 0, 0, ref format, IntPtr.Zero);

            return hr < 0 ? null : client;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>我们要请求的格式：48 kHz / 立体声 / 16 位 PCM。</summary>
    private static WAVEFORMATEX BuildFormat() => new()
    {
        wFormatTag = 1 /* WAVE_FORMAT_PCM */,
        nChannels = Channels,
        nSamplesPerSec = SampleRate,
        wBitsPerSample = 16,
        nBlockAlign = (ushort)(Channels * 16 / 8),
        nAvgBytesPerSec = SampleRate * Channels * 16 / 8,
        cbSize = 0,
    };

    /// <summary>
    /// 把 WASAPI 给的 QPC 位置（<b>100 纳秒</b>单位）换成 <c>Stopwatch</c> 的 tick 单位。
    /// <para>
    /// 两者底层的计数器是同一个（Windows 上 <c>Stopwatch</c> 就是 QPC），
    /// 差的只是单位：QPC 位置是 100 ns，而 <c>Stopwatch</c> 的频率由 <see cref="Stopwatch.Frequency"/> 给出。
    /// </para>
    /// </summary>
    private static long ConvertQpcTicks(ulong qpcPosition100ns)
    {
        // ⚠️ 用 double 而不是整数乘除：100 ns → tick 的换算因子不是整数，
        //    整数运算会引入一个随会话时长增长的累积误差（跑几小时后能差出几十毫秒）。
        return (long)(qpcPosition100ns * (double)Stopwatch.Frequency / 10_000_000.0);
    }

    private static TimeSpan TicksToTimeSpan(long ticks)
        => TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);

    /// <summary>
    /// 释放 RCW。<b>用 <c>FinalReleaseComObject</c></b>：这几个对象是我们独占创建、独占使用的，
    /// 不需要留给谁复用；不确定时就释放，免得音频服务那边的引用计数越积越多
    /// （引用计数没还回去的表现是<b>设备被永久占用</b>，用户重启机器才能恢复播放）。
    /// </summary>
    private static void Release(object? comObject)
    {
        if (comObject is null) return;

        try
        {
            if (Marshal.IsComObject(comObject)) Marshal.FinalReleaseComObject(comObject);
        }
        catch
        {
            // 释放失败（已被释放 / 进程正在退出）无计可施，也不值得打扰上层。
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;

            _disposed = true;
        }

        try
        {
            _lifetime.Cancel();
        }
        catch
        {
            // 忽略。
        }

        _queue?.Writer.TryComplete();

        // 给采集线程一点时间自己收摊（它每 4 ms 轮询一次，正常情况下毫秒级就退出了）。
        // ⚠️ 不 Join 到底：它是 IsBackground 线程，且**绝不持有需要专门清理的宿主资源**
        //    （COM 由它自己在 finally 里释放）—— 卡死在这里会拖住托盘的退出。
        try
        {
            _thread?.Join(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _lifetime.Dispose();
        }
        catch
        {
            // 忽略。
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}

// ═══════════════════════════════════════════════════════════════════════════
// WASAPI 的最小互操作声明
//
// 🔥 与 WindowsHostAudioController.cs 里那一段同一条铁律：
//    **每一行都按 vtable 槽位顺序排列，顺序本身就是契约。**
//    COM 接口方法的调用位置由"它是第几个方法"决定，方法名只对人类有意义。
//    少写一个方法 ⇒ 后面所有方法整体前移一格 ⇒ 调用 GetBuffer 实际调到 ReleaseBuffer，
//    参数个数还对不上 —— 表现为**栈被写坏 / 进程直接崩**，而不是一个编译错误。
//
//    所以：**不要删除声明，也不要重排顺序。**
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// <c>WAVEFORMATEX</c>（mmreg.h）。
/// <para>
/// ⚠️ <b>必须 <c>Pack = 1</c></b>：它前面全是 2/4 字节字段，默认对齐在末尾会多塞填充，
/// 而 WASAPI 是按"结构体多大"来读缓冲的 —— 大小对不上会返回
/// <c>AUDCLNT_E_UNSUPPORTED_FORMAT</c>（一句完全不指向真实原因的错）。
/// </para>
/// <para><c>wFormatTag</c> 只有 PCM / 浮点这类简单格式才用它；复杂格式走
/// <c>WAVE_FORMAT_EXTENSIBLE</c> + 额外的 <c>WAVEFORMATEXTENSIBLE</c> 尾巴（<c>cbSize &gt; 0</c>）。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

/// <summary>
/// <c>IAudioClient</c>：只需要「初始化 + 起停 + 取 capture 服务」，
/// 但中间那些方法<b>必须按序占位</b>（见上面的警告）。
/// </summary>
[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    // 1 ← 实际用到：初始化（共享模式 + loopback + 格式自动转换）
    [PreserveSig]
    int Initialize(
        int shareMode,
        int streamFlags,
        long bufferDuration,
        long periodicity,
        ref WAVEFORMATEX format,
        IntPtr audioSessionGuid);

    // 2
    [PreserveSig]
    int GetBufferSize(out uint frames);

    // 3
    [PreserveSig]
    int GetStreamLatency(out long latency);

    // 4
    [PreserveSig]
    int GetCurrentPadding(out uint padding);

    // 5
    [PreserveSig]
    int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

    // 6
    [PreserveSig]
    int GetMixFormat(out IntPtr format);

    // 7
    [PreserveSig]
    int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

    // 8 ← 实际用到：开始
    [PreserveSig]
    int Start();

    // 9 ← 实际用到：停止
    [PreserveSig]
    int Stop();

    // 10
    [PreserveSig]
    int Reset();

    // 11
    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    // 12 ← 实际用到：取 IAudioCaptureClient
    [PreserveSig]
    int GetService(
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.IUnknown)] out object? service);
}

/// <summary>
/// <c>IAudioCaptureClient</c>：环回采集的读取端（<b>槽位只有 3 个，一个都不能少</b>）。
/// </summary>
[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    /// <summary>
    /// 槽位 1：取一段数据。
    /// </summary>
    /// <param name="data">数据指针（<b>在 ReleaseBuffer 之前一直有效</b>）。</param>
    /// <param name="frames">这一段的<b>每声道</b>样本数（不是字节数）。</param>
    /// <param name="flags">含 <c>AUDCLNT_BUFFERFLAGS_SILENT</c> 时表示内容未定义。</param>
    /// <param name="devicePosition">设备位置（帧）。</param>
    /// <param name="qpcPosition">
    /// 这一段被<b>采到</b>时的 QPC 值（100 ns）。
    /// 🔥 它是 AV 同步唯一可靠的时间来源 —— 见 <c>AudioChunk.CapturedQpcTicks</c>。
    /// </param>
    [PreserveSig]
    int GetBuffer(
        out IntPtr data,
        out uint frames,
        out uint flags,
        out ulong devicePosition,
        out ulong qpcPosition);

    /// <summary>槽位 2：把缓冲还给引擎。<b>漏调一次就少一块缓冲</b>，最终采集会停住。</summary>
    [PreserveSig]
    int ReleaseBuffer(uint frames);

    /// <summary>槽位 3：下一段的每声道样本数；<c>0</c> = 现在没有可读数据。</summary>
    [PreserveSig]
    int GetNextPacketSize(out uint frames);
}
