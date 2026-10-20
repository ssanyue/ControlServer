using System.Runtime.InteropServices;
using ControlServer.Platform.Windows;
using FluentAssertions;
using Xunit.Abstractions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 🔬 <b>「这个 MFT 在这台机器上到底能不能编」的孤立判据</b> ——
/// 同一个硬件编码器 MFT，<b>完全不走 D3D</b>（内存样本输入、不挂设备管理器），
/// 用最小面积的握手去跑。
///
/// <para>
/// 🔥 <b>为什么它必须留在仓库里（而不是查完就删）</b>：
/// 生产路径里「我们的 D3D 接线错了」与「这个 MFT 在这台机器上根本不出货」
/// 这两件事的症状<b>逐字节一样</b> —— 都是
/// <c>ProcessOutput</c> 回 <c>E_UNEXPECTED + NO_SAMPLE</c>、都是"一帧不出"。
/// 唯一能把它们分开的办法就是<b>把 D3D 那一层整个摘掉再跑一遍</b>：
/// 摘掉之后还不出货 ⇒ 问题在 MFT / 驱动；摘掉之后出货 ⇒ 问题在我们的接线。
/// 这正是本项目 2026-10-16 定位"QSV 一帧不出"时用的那一步 ——
/// 没有它当时会在 D3D 那一侧找一个根本不存在的错误。
/// </para>
///
/// <para>
/// 🔥 <b>为什么它现在比当时更该留下来</b>：4K / 8K 的验收口径是
/// 「<b>待现场机型</b>」。到现场第一件要知道的事就是"这台机器的硬编到底能不能用"，
/// 而那时中心侧同时在跑一堆别的东西 —— 没有这个探针，就只能靠猜。
/// </para>
///
/// <para>
/// ⚠️ <b>本用例的纪律（与 <see cref="DxgiHardwareEncodeSmokeTests"/> 不同）</b>：
/// 它<b>不要求</b>机器上有硬件编码器 —— 没有就<b>什么都不做</b>，绝不判失败。
/// 理由是本仓铁律：<b>假 FAIL 比不做断言更坏</b>。
/// 装了开机自启的软编、或者换了台机器，不该让这条用例变红，
/// 因为那时红的会是"这台机器上装了什么"、而不是代码对不对。
/// 它只钉两条<b>它自己说自己</b>的性质：
/// <list type="number">
/// <item><description><b>绝不沉默</b>：报了任何一个编码器，就必须对它有话说；</description></item>
/// <item><description><b>成功不能是空的</b>：说"成功"就必须真的吐出了字节。</description></item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ <b>它的字节数不要拿去和生产路径比。</b> 本探针<b>刻意不配码率控制</b>
/// （不设 <c>CODECAPI_AVEncCommonRateControlMode</c> / 质量 / 平均码率），
/// 而且喂的是"沿 y 恒定"的合成条带 —— 那种内容能被压到几百字节。
/// 它回答的问题只有一个：<b>这个 MFT 出不出码流</b>。
/// 要数字（码率、灰度收益）去看 <see cref="DxgiHardwareEncodeSmokeTests"/>。
/// </para>
/// </summary>
public sealed class MfMemoryModeEncodeProbeTests
{
    /// <summary>探针会跑的两个尺寸。</summary>
    ///
    /// <para>
    /// 🔥 <b>一大一小是刻意的</b>：小尺寸把"分辨率因素"排除掉，大尺寸与生产路径对齐。
    /// 只跑小尺寸会漏掉"只在 4K 级别才失败"这类问题 —— 实测里确实见过小尺寸能过、大尺寸必挂的编码器。
    /// </para>
    private static readonly (string Label, int Width, int Height)[] Sizes =
    [
        ("640×480（小尺寸，排除分辨率因素）", 640, 480),
        ("2160×1440（与生产路径同尺寸）", 2160, 1440),
    ];

    private readonly ITestOutputHelper _output;

    public MfMemoryModeEncodeProbeTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 🔥 铁律：<b>诊断工具可以失败，但不许沉默。</b>
    ///
    /// <para>
    /// 这条守的是"探针自己坏掉了、却看起来像机器有问题"：
    /// 一个 <c>Why</c> 为空的失败，在现场会被读成"这台机器编不了"，
    /// 而真实原因可能是探针内部的某一步写错了。
    /// </para>
    /// </summary>
    [Fact]
    public void The_probe_always_has_something_to_say_about_every_encoder_it_finds()
    {
        if (!OperatingSystem.IsWindows()) return;

        var outcomes = RunAll();

        if (outcomes.Count == 0)
        {
            // 本机没有硬件编码器 MFT ⇒ 什么都不判（见类注释的纪律那一段）。
            _output.WriteLine("本机没有枚举到硬件 H.264 编码器 MFT —— 本用例不判失败。");

            return;
        }

        foreach (var outcome in outcomes)
        {
            outcome.Why.Should().NotBeNullOrWhiteSpace(
                $"探针在「{outcome.Label}」上得出「{(outcome.Ok ? "成功" : "失败")}」，"
                + "却没写清楚为什么 —— 空白的失败现场会被读成「这台机器编不了」");
        }
    }

    /// <summary>
    /// 🔥 铁律：<b>说"成功"就必须真的吐出了字节。</b>
    ///
    /// <para>
    /// 这条防的是探针的"成功"变成一句空话：只看了某个 <c>HRESULT</c> 就报成功，
    /// 而实际一帧都没出来。那样它在排障时会指向<b>完全相反</b>的结论 ——
    /// 明明是"我们的接线错了"，它却说"这台机器没问题"，
    /// 于是人又回去查 D3D，绕一大圈。
    /// </para>
    /// </summary>
    [Fact]
    public void A_reported_success_must_have_actually_produced_bytes()
    {
        if (!OperatingSystem.IsWindows()) return;

        var outcomes = RunAll().Where(o => o.Ok).ToList();

        if (outcomes.Count == 0)
        {
            _output.WriteLine("没有任何一个编码器跑通 —— 本用例不判失败（见类注释的纪律）。");

            return;
        }

        foreach (var outcome in outcomes)
        {
            outcome.Produced.Should().BeGreaterThan(
                0,
                $"「{outcome.Label}」被报成成功，却一帧都没产出 —— 这个「成功」是空的");

            outcome.TotalBytes.Should().BeGreaterThan(
                0,
                $"「{outcome.Label}」被报成成功，产出的却是 0 字节 —— 样本是空的");
        }
    }

    /// <summary>一格探针跑出来的结果。<b>刻意带上它自己的数字</b>，好让断言不必再解析字符串。</summary>
    private sealed record ProbeOutcome(
        string Label, bool Ok, string Why, int Fed, int Produced, int TotalBytes);

    /// <summary>同一个 MFT 的一次尝试（不含尺寸标签）。</summary>
    private sealed record AttemptResult(bool Ok, string Why, int Fed, int Produced, int TotalBytes);

    /// <summary>
    /// 把本机枚举到的每一个硬件 H.264 编码器、每一个尺寸都跑一遍，并打印全过程。
    ///
    /// <para>
    /// ⚠️ 返回值是<b>"跑过的每一格"</b>，不是"最好的那一格"：
    /// 现场要看的恰恰是"哪一个 MFT 在哪个尺寸上挂了"，只留最好的一格会把线索丢掉。
    /// </para>
    /// </summary>
    private List<ProbeOutcome> RunAll()
    {
        var results = new List<ProbeOutcome>();

        MediaFoundationInterop.MFStartup((int)MediaFoundationInterop.MfVersion, 0);

        foreach (var (label, width, height) in Sizes)
        {
            _output.WriteLine($"════ {label} ════");

            results.AddRange(Run(label, width, height));
        }

        return results;
    }

    private List<ProbeOutcome> Run(string label, int width, int height)
    {
        var results = new List<ProbeOutcome>();

        var info = new MediaFoundationInterop.MftRegisterTypeInfo
        {
            Major = MediaFoundationInterop.MFMediaType_Video,
            Sub = MediaFoundationInterop.MFVideoFormat_H264,
        };

        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<MediaFoundationInterop.MftRegisterTypeInfo>());

        IntPtr activates;
        uint count;
        int hrEnum;

        try
        {
            Marshal.StructureToPtr(info, buffer, false);

            // 🔥 H.264 在**编码器**上是输出 ⇒ 填第 4 参 `pOutputType`。
            //    ⚠️ 解码器**反过来**（填第 3 参），别照抄 —— 位置填错不会报错，
            //       只会拿到 0 个，然后看起来像"本机没装这个格式"。
            hrEnum = MediaFoundationInterop.MFTEnumEx(
                MediaFoundationInterop.CategoryVideoEncoder,
                MediaFoundationInterop.EnumFlagHardware,
                IntPtr.Zero,
                buffer,
                out activates,
                out count);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"  枚举抛出 {ex.GetType().Name}：{ex.Message}");

            Marshal.FreeHGlobal(buffer);

            return results;
        }

        Marshal.FreeHGlobal(buffer);

        _output.WriteLine($"  MFTEnumEx（硬件 H264）：{ComVtable.Describe(hrEnum)}，{count} 个");

        if (count == 0) return results;

        for (var i = 0; i < count; i++)
        {
            var activate = Marshal.ReadIntPtr(activates, i * IntPtr.Size);

            _output.WriteLine($"  ── [{i}] ──");

            var label1 = $"{label} / MFT[{i}]";

            try
            {
                var attempt = Attempt(activate, width, height);

                _output.WriteLine($"  {(attempt.Ok ? "✅" : "✗")} [{i}] {attempt.Why}");

                results.Add(new ProbeOutcome(
                    label1, attempt.Ok, attempt.Why, attempt.Fed, attempt.Produced, attempt.TotalBytes));
            }
            catch (Exception ex)
            {
                // ⚠️ 这里**不能**只写异常类型：现场要的是一句能读懂的话。
                var why = $"抛出 {ex.GetType().Name}：{ex.Message}";

                _output.WriteLine($"  ✗ [{i}] {why}");

                results.Add(new ProbeOutcome(label1, false, why, 0, 0, 0));
            }
        }

        MediaFoundationInterop.CoTaskMemFree(activates);

        return results;
    }

    private AttemptResult Attempt(IntPtr activate, int width, int height)
    {
        // ⚠️ 三个计数器**声明在最前面**：下面有一堆提前返回的失败出口，
        //    它们也必须带上"当时喂了几帧、出了几帧" —— 否则一个失败结果
        //    剩下的信息就是一个字符串，排障时还得回去翻日志。
        var fed = 0;
        var produced = 0;
        var totalBytes = 0;

        AttemptResult Fail(string reason) => new(false, reason, fed, produced, totalBytes);

        _ = SetU32(activate, MediaFoundationInterop.MF_TRANSFORM_ASYNC_UNLOCK, 1);
        _ = SetU32(activate, MediaFoundationInterop.MF_LOW_LATENCY, 1);

        var iid = MediaFoundationInterop.IID_IMFTransform;

        var activateFn = ComVtable.Bind<MediaFoundationInterop.Delegates.ActivateObjectFn>(
            activate, MediaFoundationInterop.ActivateSlots.ActivateObject);

        var hr = activateFn(activate, ref iid, out var transform);

        if (!ComVtable.Succeeded(hr)) return Fail($"ActivateObject 失败 {ComVtable.Describe(hr)}");

        try
        {
            // 🔥 **异步解锁还有第二半**：只在 activate 上设不够，还要在 **transform 自己的
            //    属性仓**上再设一次。漏了这一步的症状就是 `MF_E_TRANSFORM_ASYNC_LOCKED`
            //    （0xC00D6D77），而且它是在**后面某一次调用**才报出来 —— 看起来像"那个函数不支持"。
            if (ComVtable.Succeeded(ComVtable.Bind<MediaFoundationInterop.Delegates.GetAttributesFn>(
                transform, MediaFoundationInterop.TransformSlots.GetAttributes)(transform, out var attrs)))
            {
                _ = SetU32(attrs, MediaFoundationInterop.MF_TRANSFORM_ASYNC_UNLOCK, 1);

                ComVtable.ReleasePointer(ref attrs);
            }

            // ── 事件发生器 ──
            var eventIid = MediaFoundationInterop.IID_IMFMediaEventGenerator;

            if (!ComVtable.Succeeded(ComVtable.QueryInterfaceRaw(transform, ref eventIid, out var events)))
            {
                return Fail("拿不到 IMFMediaEventGenerator");
            }

            try
            {
                // ── 输出类型：直接用编码器自己给的第一种 ──
                var getType = ComVtable.Bind<MediaFoundationInterop.Delegates.GetAvailableTypeFn>(
                    transform, MediaFoundationInterop.TransformSlots.GetOutputAvailableType);

                var setType = ComVtable.Bind<MediaFoundationInterop.Delegates.SetTypeFn>(
                    transform, MediaFoundationInterop.TransformSlots.SetOutputType);

                hr = getType(transform, 0, 0, out var outType);

                if (!ComVtable.Succeeded(hr))
                {
                    return Fail($"GetOutputAvailableType(0,0) 失败 {ComVtable.Describe(hr)}");
                }

                try
                {
                    // ⚠️ 打包**只用** `MediaFoundationInterop.Pack`，本类**不再自己写一份**。
                    //    这条是有代价换来的：本探针原先自己写了一个
                    //    `Pack(uint low, uint high)`，而平台层那个是 `Pack(uint high, uint low)` ——
                    //    **形参名正好相反**。于是这里老老实实按"宽度、高度"的语序传进去，
                    //    汇编出来却是**转置的**：`MF_MT_FRAME_SIZE` 被设成了 480×640 / 1440×2160，
                    //    `MF_MT_FRAME_RATE` 被设成了 **1/30**。
                    //    ⇒ 症状：喂进去的 NV12 是按 640 的跨度排的，而编码器按 480 的跨度去读
                    //      ⇒ 内容变成**跨行噪声** ⇒ P 帧大得离谱（实测 9 MB/帧），
                    //      而**一个错误码都没有**。这正是 §「量纲错与平面错位数字症状一模一样」那一条。
                    _ = SetU64(outType, MediaFoundationInterop.MF_MT_FRAME_SIZE,
                        MediaFoundationInterop.Pack((uint)width, (uint)height));

                    _ = SetU64(outType, MediaFoundationInterop.MF_MT_FRAME_RATE,
                        MediaFoundationInterop.Pack(30, 1));

                    hr = setType(transform, 0, outType, 0);

                    if (!ComVtable.Succeeded(hr)) return Fail($"SetOutputType 失败 {ComVtable.Describe(hr)}");
                }
                finally
                {
                    ComVtable.ReleasePointer(ref outType);
                }

                // ── 输入类型：NV12 内存样本 ──
                hr = MediaFoundationInterop.MFCreateMediaType(out var inType);

                if (!ComVtable.Succeeded(hr)) return Fail("MFCreateMediaType 失败");

                try
                {
                    _ = SetGuid(inType, MediaFoundationInterop.MF_MT_MAJOR_TYPE, MediaFoundationInterop.MFMediaType_Video);
                    _ = SetGuid(inType, MediaFoundationInterop.MF_MT_SUBTYPE, MediaFoundationInterop.MFVideoFormat_NV12);
                    // ⚠️ 同上：只用平台层的那个 `Pack`（<b>宽度在高 32 位</b>）。
                    _ = SetU64(inType, MediaFoundationInterop.MF_MT_FRAME_SIZE,
                        MediaFoundationInterop.Pack((uint)width, (uint)height));

                    _ = SetU64(inType, MediaFoundationInterop.MF_MT_FRAME_RATE,
                        MediaFoundationInterop.Pack(30, 1));

                    _ = SetU64(inType, MediaFoundationInterop.MF_MT_PIXEL_ASPECT_RATIO,
                        MediaFoundationInterop.Pack(1, 1));
                    _ = SetU32(inType, MediaFoundationInterop.MF_MT_INTERLACE_MODE, MediaFoundationInterop.InterlaceProgressive);
                    _ = SetU32(inType, MediaFoundationInterop.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);

                    var setIn = ComVtable.Bind<MediaFoundationInterop.Delegates.SetTypeFn>(
                        transform, MediaFoundationInterop.TransformSlots.SetInputType);

                    hr = setIn(transform, 0, inType, 0);

                    if (!ComVtable.Succeeded(hr)) return Fail($"SetInputType(NV12) 失败 {ComVtable.Describe(hr)}");
                }
                finally
                {
                    ComVtable.ReleasePointer(ref inType);
                }

                // ⚠️ 刻意**不发** MFT_MESSAGE_SET_D3D_MANAGER —— 这就是本探针与生产路径唯一的差别，
                //    也是它全部的价值所在（见类注释）。

                var processMessage = ComVtable.Bind<MediaFoundationInterop.Delegates.ProcessMessageFn>(
                    transform, MediaFoundationInterop.TransformSlots.ProcessMessage);

                hr = processMessage(transform, MediaFoundationInterop.MessageNotifyBeginStreaming, 0);

                if (!ComVtable.Succeeded(hr)) return Fail($"BEGIN_STREAMING 失败 {ComVtable.Describe(hr)}");

                hr = processMessage(transform, MediaFoundationInterop.MessageNotifyStartOfStream, 0);

                if (!ComVtable.Succeeded(hr)) return Fail($"START_OF_STREAM 失败 {ComVtable.Describe(hr)}");

                // ── 跑 ──
                var getInfo = ComVtable.Bind<MediaFoundationInterop.Delegates.GetOutputStreamInfoFn>(
                    transform, MediaFoundationInterop.TransformSlots.GetOutputStreamInfo);

                var getEvent = ComVtable.Bind<MediaFoundationInterop.Delegates.GetEventFn>(
                    events, MediaFoundationInterop.EventGeneratorSlots.GetEvent);

                var processInput = ComVtable.Bind<MediaFoundationInterop.Delegates.ProcessInputFn>(
                    transform, MediaFoundationInterop.TransformSlots.ProcessInput);

                var processOutput = ComVtable.Bind<MediaFoundationInterop.Delegates.ProcessOutputFn>(
                    transform, MediaFoundationInterop.TransformSlots.ProcessOutput);

                var status = "";
                var needInput = 0;
                var haveOutput = 0;
                var consumedOutputs = 0;

                for (var round = 0; round < 40; round++)
                {
                    // 取事件
                    for (var g = 0; g < 64; g++)
                    {
                        var ehr = getEvent(events, MediaFoundationInterop.EventFlagNoWait, out var mediaEvent);

                        if (ehr == MediaFoundationInterop.HResults.NoEventsAvailable) break;

                        if (!ComVtable.Succeeded(ehr))
                        {
                            status = $"取事件失败 {ComVtable.Describe(ehr)}";

                            break;
                        }

                        var handle = mediaEvent;

                        try
                        {
                            // ⚠️ `GetType` 在 **IMFMediaEvent** 上（槽 33），**不在**
                            //    `IMFMediaEventGenerator` 上（只有 7 槽）—— 绑错接口会读到
                            //    vtable 之后的内存当地址 call，症状是**整个进程消失**。
                            var type = ComVtable.Bind<MediaFoundationInterop.Delegates.GetEventTypeFn>(
                                handle, MediaFoundationInterop.MediaEventSlots.GetEventType);

                            if (ComVtable.Succeeded(type(handle, out var eventType)))
                            {
                                if (eventType == MediaFoundationInterop.EventTransformNeedInput) needInput++;
                                else if (eventType == MediaFoundationInterop.EventTransformHaveOutput) haveOutput++;
                            }
                        }
                        finally
                        {
                            ComVtable.ReleasePointer(ref handle);
                        }
                    }

                    if (needInput > fed && fed < 12)
                    {
                        var sample = MakeNv12Sample(width, height, fed, out var sampleWhy);

                        if (sample == IntPtr.Zero)
                        {
                            status = $"造样本失败：{sampleWhy}";

                            break;
                        }

                        try
                        {
                            var ihr = processInput(transform, 0, sample, 0);

                            if (!ComVtable.Succeeded(ihr))
                            {
                                status = $"ProcessInput 失败 {ComVtable.Describe(ihr)}";

                                break;
                            }

                            fed++;
                        }
                        finally
                        {
                            ComVtable.ReleasePointer(ref sample);
                        }
                    }

                    var infoHr = getInfo(transform, 0, out var osi);

                    if (!ComVtable.Succeeded(infoHr))
                    {
                        status = $"GetOutputStreamInfo 失败 {ComVtable.Describe(infoHr)}";

                        break;
                    }

                    var provides = (osi.Flags & MediaFoundationInterop.OutputStreamProvidesSamples) != 0;

                    // 🔥 **只在确实有 HaveOutput 令牌时才去要输出。**
                    //    异步 MFT 的约定是"事件到了才有东西可取"；没到就去问，
                    //    这个编码器回的是 E_UNEXPECTED + NO_SAMPLE（而不是
                    //    MF_E_TRANSFORM_NEED_MORE_INPUT），看起来像故障 ——
                    //    而它的真正后果更狠：那一次调用之后**它再也不发 NeedInput 了**。
                    if (haveOutput <= consumedOutputs) continue;

                    consumedOutputs++;

                    var data = new MediaFoundationInterop.MftOutputDataBuffer
                    {
                        StreamId = 0,
                        Sample = IntPtr.Zero,
                        Status = 0,
                        Events = IntPtr.Zero,
                    };

                    var shr = processOutput(transform, 0, 1, ref data, out _);

                    if (shr == MediaFoundationInterop.HResults.TransformNeedMoreInput)
                    {
                        // 正常：还没编出来。
                        if (fed >= 12 && needInput <= fed) break;

                        continue;
                    }

                    if (MediaFoundationInterop.HResults.TransformStreamChange == shr)
                    {
                        // 🔥 **硬件编码器出第一段码流前的必经一步**：它要把自己实际用的
                        //    profile/level 与 SPS/PPS 报出来，要求重谈输出类型。
                        //    不处理它的症状是"一帧都不出、且没有任何错误码"。
                        //    ⚠️ 必须**在同一次调用里重试** —— 它不会因为我们答应了换类型
                        //       而补发一个 HaveOutput 事件。
                        var newType = IntPtr.Zero;

                        try
                        {
                            hr = getType(transform, 0, 0, out newType);

                            if (ComVtable.Succeeded(hr)
                                && ComVtable.Succeeded(setType(transform, 0, newType, 0)))
                            {
                                status = "换过输出类型（并已重谈）";

                                continue;
                            }
                        }
                        finally
                        {
                            ComVtable.ReleasePointer(ref newType);
                        }

                        status = "回 MF_E_TRANSFORM_STREAM_CHANGE，且重谈失败";

                        break;
                    }

                    if (!ComVtable.Succeeded(shr))
                    {
                        status = $"ProcessOutput 失败 {ComVtable.Describe(shr)}"
                            + $"（状态字 0x{data.Status:X}，编码器自带样本 {(provides ? "是" : "否")}，"
                            + $"样本 {(data.Sample == IntPtr.Zero ? "空" : "有")}，已喂 {fed}，期望 {needInput}/{haveOutput}）";

                        break;
                    }

                    if (data.Sample != IntPtr.Zero)
                    {
                        produced++;

                        var bytes = LengthOf(data.Sample, out var lengthWhy);

                        totalBytes += bytes;

                        // ⚠️ **读不到长度不能被吞掉**：那会让"共 N 字节"变成一个
                        //    看起来正常、其实全是 0 的数。宁可把它说出来。
                        if (lengthWhy.Length > 0) status = $"样本长度读不到：{lengthWhy}";
                        else if (produced == 1) status = $"首帧 {bytes} 字节（{DescribeSample(data.Sample)}）";
                    }

                    ComVtable.ReleasePointer(ref data.Sample);
                    ComVtable.ReleasePointer(ref data.Events);

                    if (produced >= 5) break;
                }

                var why = $"喂 {fed}、出 {produced}、共 {totalBytes} 字节、事件 Need {needInput}/Have {haveOutput}"
                    + (status.Length == 0 ? "，无异常" : $"，{status}");

                return new AttemptResult(produced > 0, why, fed, produced, totalBytes);
            }
            finally
            {
                var e = events;
                ComVtable.ReleasePointer(ref e);
            }
        }
        finally
        {
            var t = transform;
            ComVtable.ReleasePointer(ref t);
        }
    }

    /// <summary>
    /// 把一个样本的"三种长度"并排打出来：<c>GetTotalLength</c>、<c>Lock</c> 报的
    /// <c>maxLength</c> 与 <c>currentLength</c>。
    ///
    /// <para>
    /// 🔥 <b>为什么值得并排打</b>：本探针最早用 <c>Lock</c> 的 <c>currentLength</c> 当
    /// "码流字节数"，在 640×480 上报出 <b>7.4 MB/帧</b>；而生产路径用同一个口子却是对的
    /// （325 KB / 30 帧）。<b>同一段代码、两台不同的样本来源</b>给出相差三个数量级的数 ——
    /// 这种时候"哪个数是真的"不能靠推理，只能并排打出来看。
    /// </para>
    /// </summary>
    private static string DescribeSample(IntPtr sample)
    {
        var total = "?";
        var max = "?";
        var current = "?";

        try
        {
            var getLength = ComVtable.Bind<MediaFoundationInterop.Delegates.GetTotalLengthFn>(
                sample, MediaFoundationInterop.SampleSlots.GetTotalLength);

            total = ComVtable.Succeeded(getLength(sample, out var t)) ? t.ToString() : "-";

            var getBuffer = ComVtable.Bind<MediaFoundationInterop.Delegates.GetBufferByIndexFn>(
                sample, MediaFoundationInterop.SampleSlots.GetBufferByIndex);

            if (ComVtable.Succeeded(getBuffer(sample, 0, out var buffer)))
            {
                try
                {
                    var lockFn = ComVtable.Bind<MediaFoundationInterop.Delegates.BufferLockFn>(
                        buffer, MediaFoundationInterop.MediaBufferSlots.Lock);

                    if (ComVtable.Succeeded(lockFn(buffer, out _, out var mx, out var cur)))
                    {
                        max = mx.ToString();
                        current = cur.ToString();
                    }

                    var unlock = ComVtable.Bind<MediaFoundationInterop.Delegates.BufferUnlockFn>(
                        buffer, MediaFoundationInterop.MediaBufferSlots.Unlock);

                    _ = unlock(buffer);
                }
                finally
                {
                    ComVtable.ReleasePointer(ref buffer);
                }
            }
        }
        catch (Exception ex)
        {
            return $"探测样本布局时抛出 {ex.GetType().Name}";
        }

        return $"GetTotalLength={total}，Lock.max={max}，Lock.current={current}";
    }

    /// <summary>造一个 NV12 内存样本（内容随便，只要是个合法的 NV12 帧）。</summary>
    private static IntPtr MakeNv12Sample(int width, int height, int frame, out string why)
    {
        why = string.Empty;

        var size = (uint)(width * height * 3 / 2);

        var hr = MediaFoundationInterop.MFCreateMemoryBuffer(size, out var buffer);

        if (!ComVtable.Succeeded(hr))
        {
            why = $"MFCreateMemoryBuffer {ComVtable.Describe(hr)}";

            return IntPtr.Zero;
        }

        try
        {
            var lockFn = ComVtable.Bind<MediaFoundationInterop.Delegates.BufferLockFn>(
                buffer, MediaFoundationInterop.MediaBufferSlots.Lock);

            hr = lockFn(buffer, out var data, out _, out _);

            if (!ComVtable.Succeeded(hr))
            {
                why = $"Lock {ComVtable.Describe(hr)}";

                return IntPtr.Zero;
            }

            try
            {
                // 亮度：随帧号移动的竖条（保证有内容、且每帧不同）；色度：常量 128。
                var luma = new byte[width * height];
                var shift = frame * 7;

                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        luma[(y * width) + x] = (byte)(((x + shift) % 64) * 4 > 255 ? 255 : ((x + shift) % 64) * 4);
                    }
                }

                Marshal.Copy(luma, 0, data, luma.Length);

                var chroma = new byte[width * height / 2];

                for (var i = 0; i < chroma.Length; i++) chroma[i] = 128;

                Marshal.Copy(chroma, 0, data + luma.Length, chroma.Length);
            }
            finally
            {
                var unlock = ComVtable.Bind<MediaFoundationInterop.Delegates.BufferUnlockFn>(
                    buffer, MediaFoundationInterop.MediaBufferSlots.Unlock);

                _ = unlock(buffer);
            }

            hr = MediaFoundationInterop.MFCreateSample(out var sample);

            if (!ComVtable.Succeeded(hr))
            {
                why = "MFCreateSample 失败";

                return IntPtr.Zero;
            }

            var addBuffer = ComVtable.Bind<MediaFoundationInterop.Delegates.AddBufferFn>(
                sample, MediaFoundationInterop.SampleSlots.AddBuffer);

            hr = addBuffer(sample, buffer);

            if (!ComVtable.Succeeded(hr))
            {
                why = $"AddBuffer {ComVtable.Describe(hr)}";

                var s = sample;
                ComVtable.ReleasePointer(ref s);

                return IntPtr.Zero;
            }

            var setTime = ComVtable.Bind<MediaFoundationInterop.Delegates.SetSampleTimeFn>(
                sample, MediaFoundationInterop.SampleSlots.SetSampleTime);

            _ = setTime(sample, (long)frame * 333_333);

            return sample;
        }
        finally
        {
            ComVtable.ReleasePointer(ref buffer);
        }
    }

    /// <summary>
    /// 取一个样本的字节数。
    ///
    /// <para>
    /// 🔥 <b>用 <c>IMFSample::GetTotalLength</c>（槽 45），不是"取第 0 个 buffer 再
    /// <c>Lock</c> 读 currentLength"。</b> 本探针一开始就是后者，实测在 640×480 上报出
    /// <b>7.4 MB / 帧</b>（真值是几 KB）—— 而它<b>不报错</b>，只是让数字大得离谱。
    /// 原因见 <see cref="MediaFoundationInterop.SampleSlots.GetTotalLength"/> 的说明：
    /// 编码器自带样本时给回来的常常是 DXGI 表面支撑的样本，那种 buffer 的
    /// <c>Lock</c> 报的长度与码流实际长度不是一回事。
    /// </para>
    /// <para>
    /// ⚠️ 取不到就回 0 并把原因写进 <paramref name="why"/> —— <b>不编一个数出来</b>。
    /// </para>
    /// </summary>
    private static int LengthOf(IntPtr sample, out string why)
    {
        why = string.Empty;

        try
        {
            var getLength = ComVtable.Bind<MediaFoundationInterop.Delegates.GetTotalLengthFn>(
                sample, MediaFoundationInterop.SampleSlots.GetTotalLength);

            var hr = getLength(sample, out var length);

            if (!ComVtable.Succeeded(hr))
            {
                why = $"GetTotalLength 失败 {ComVtable.Describe(hr)}";

                return 0;
            }

            if (length > int.MaxValue)
            {
                why = $"样本声称有 {length} 字节 —— 这个数一看就不对";

                return 0;
            }

            return (int)length;
        }
        catch (Exception ex)
        {
            why = $"取样本长度时抛出 {ex.GetType().Name}：{ex.Message}";

            return 0;
        }
    }

    private static int SetU32(IntPtr target, Guid key, uint value)
    {
        var k = key;

        return ComVtable.Bind<MediaFoundationInterop.Delegates.AttributeSetUInt32Fn>(
            target, MediaFoundationInterop.AttributeSlots.SetUINT32)(target, ref k, value);
    }

    private static int SetU64(IntPtr target, Guid key, ulong value)
    {
        var k = key;

        return ComVtable.Bind<MediaFoundationInterop.Delegates.AttributeSetUInt64Fn>(
            target, MediaFoundationInterop.AttributeSlots.SetUINT64)(target, ref k, value);
    }

    private static int SetGuid(IntPtr target, Guid key, Guid value)
    {
        var k = key;
        var v = value;

        return ComVtable.Bind<MediaFoundationInterop.Delegates.AttributeSetGuidFn>(
            target, MediaFoundationInterop.AttributeSlots.SetGUID)(target, ref k, ref v);
    }
}
