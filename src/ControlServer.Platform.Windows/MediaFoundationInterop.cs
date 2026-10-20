using System.Runtime.InteropServices;

namespace ControlServer.Platform.Windows;

/// <summary>
/// <b>Media Foundation 的裸 vtable 声明与工厂函数</b>（M5.4 零拷贝管线的编码侧）。
///
/// <para>
/// 🔥 <b>只声明用到的槽位</b>，理由与核对方法见 <see cref="DxgiInterop"/> 的类注释与
/// <c>docs/M5.4-实施方案.md</c> §3.1。
/// </para>
///
/// <para>
/// ⚠️ 本类同时是 <c>MediaFoundationEncoders</c> 用的 <c>MFStartup</c> / <c>MFTEnumEx</c> 的
/// <b>唯一声明处</b> —— 那两个原先写在它自己里，M5.4 要用同一批函数，
/// 于是顺手收敛到这里（同一条规矩不抄两遍）。
/// </para>
/// </summary>
internal static class MediaFoundationInterop
{
    // ──────────────────────────── GUID 常量（头文件核实）────────────────────────────

    internal static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFVideoFormat_HEVC = new("43564548-0000-0010-8000-00aa00389b71");

    internal static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    internal static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    internal static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    internal static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    internal static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    internal static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");

    /// <summary><c>MF_MT_MPEG2_PROFILE</c>（<c>mfapi.h:600</c>）。取值见 <see cref="H264ProfileHigh"/> 等。</summary>
    internal static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");

    /// <summary><c>MF_MT_MPEG2_LEVEL</c>（<c>mfapi.h:601</c>）。本批不设（让编码器自己挑）。</summary>
    internal static readonly Guid MF_MT_MPEG2_LEVEL = new("96f66574-11c5-4015-8666-bff516436da7");

    /// <summary><c>MF_MT_MAX_KEYFRAME_SPACING</c>（<c>mfapi.h:595</c>）—— GOP 长度的类型侧表达。</summary>
    internal static readonly Guid MF_MT_MAX_KEYFRAME_SPACING = new("c16eb52b-73a1-476f-8d62-839d6a020652");

    /// <summary>
    /// <c>MF_MT_MPEG_SEQUENCE_HEADER</c>（<c>mfapi.h:603</c>）—— <b>一段 <c>MF_MT_BLOB</c></b>，
    /// 装的是音频/视频的"解码器初始化描述"。
    ///
    /// <para>
    /// 🔥 <b>它是"自编自解"能不能成立的关键。</b>H.264 的构造有两种常见封装：
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Annex-B</b>：每个 NAL 前面带 <c>00 00 00 01</c> 起始码，SPS/PPS <b>混在码流里</b>；
    ///         解码器自己能解析出来。</item>
    ///   <item><b>AVCC / hvcC</b>：NAL 前面是长度，SPS/PPS <b>不在码流里</b>，
    ///         只能靠这份 blob（也就是前端 WebCodecs 要的 <c>description</c>）。</item>
    /// </list>
    /// <para>
    /// ⇒ 所以 <b>先读它、再决定怎么喂解码器</b>，不要凭"MF 一般输出 Annex-B"这种印象写代码。
    /// 本批的用例会把"它到底有没有、有多大"如实打出来。
    /// </para>
    /// </summary>
    internal static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");

    /// <summary>
    /// 🔥 <c>MFSampleExtension_CleanPoint</c>（<c>mfapi.h:237</c>）—— <b>样本是不是关键帧（随机访问点）</b>。
    ///
    /// <para>
    /// 它长在 <b>样本</b>上（编出来的那一帧码流），不在媒体类型上。
    /// 协议层要给前端 WebCodecs 的"<c>IsKeyframe</c>"就来自这里（见方案 §4）。
    /// </para>
    /// <para>
    /// ⚠️ <b>它是可选的</b>：编码器不写它就 <c>GetUINT32</c> 回
    /// <see cref="HResults.AttributeNotFound"/>。那时<b>不许猜</b> ——
    /// 我们既不能"假定第一帧就是关键帧"，也不能去猜码流格式（Annex-B / AVCC 还没定），
    /// 只能如实上报"本机不提供这个标记"。
    /// </para>
    /// </summary>
    internal static readonly Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");


    /// <summary>
    /// 🔥 <c>MF_TRANSFORM_ASYNC_UNLOCK</c>：<b>硬件编码器 MFT 默认是"锁定"的</b>，
    /// 不解锁它，<c>ActivateObject</c> / <c>SetInputType</c> 会返回
    /// <c>MF_E_TRANSFORM_ASYNC_LOCKED</c>，而那条错误信息不会告诉你"要设这个属性"。
    /// </summary>
    internal static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");

    /// <summary><c>MF_TRANSFORM_ASYNC</c>：用来判断一个 MFT 是不是异步的。</summary>
    internal static readonly Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");

    /// <summary><c>MF_SA_D3D11_AWARE</c>：MFT 自报"我认得 D3D11"。<b>能设 <c>SET_D3D_MANAGER</c> 的前提。</b></summary>
    internal static readonly Guid MF_SA_D3D11_AWARE = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");

    /// <summary><c>MF_LOW_LATENCY</c>：低延迟模式（编码器会据此关掉 lookahead 之类的东西）。</summary>
    internal static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    internal static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    internal static readonly Guid CODECAPI_AVEncCommonQuality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
    internal static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    internal static readonly Guid CODECAPI_AVEncCommonBufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    internal static readonly Guid CODECAPI_AVEncCommonLowLatency = new("9d3ecd55-89e8-490a-970a-0c9548d5a56e");
    internal static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    internal static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    /// <summary>
    /// <c>CODECAPI_AVEncMPVDefaultBPictureCount</c>：两个 I 之间最多插几个 <b>B 帧</b>。
    ///
    /// <para>
    /// 🔥 <b>本方案把它显式设成 0，理由不是画质而是"顺序"</b>：B 帧的存在意味着
    /// <b>解码顺序 ≠ 显示顺序</b>，于是前端必须在喂给解码器之前按
    /// <c>POC</c> 重排、并且为"某个 B 帧依赖的参考帧还没到"设一套等待逻辑。
    /// 设成 0 之后，这一整块复杂度<b>不存在</b>（实测本机确实没有 B 帧 —— 但那是
    /// <b>驱动默认值</b>，不是我们要求的；见方案 §6.1 #1 的挂账）。
    /// </para>
    /// <para>
    /// ⚠️ 槽位/常量来源：<c>codecapi.h:754</c>
    /// <c>STATIC_CODECAPI_AVEncMPVDefaultBPictureCount 0x8d390aac, 0xdc5c, 0x4200, ...</c>。
    /// 它<b>不在</b> MSDN 那篇「Codec API Properties」总表的显眼处，别按记忆写 GUID。
    /// </para>
    /// </summary>
    internal static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount
        = new("8d390aac-dc5c-4200-b57f-814d04babab2");

    // ─────────────────────── M5.5 吞吐调优（手段 2 / 8 / 9 / 10）───────────────────────
    //
    // 🔥 **这几条 GUID 全部对照 <c>codecapi.h</c> 逐字段抄的**（不是按记忆写）：
    //    同一批属性里已经有过一次教训（`AVEncMPVDefaultBPictureCount` 不在 MSDN 总表的显眼处）。
    //    GUID 写错的表现是 <c>IsSupported</c> 回 <c>S_FALSE</c> ⇒ 我们记一句"编码器不支持"，
    //    于是"调优没生效"被解释成"这台卡不行" —— **硬件被冤枉，而代码看起来完全正确**。

    /// <summary>
    /// <c>CODECAPI_AVEncAdaptiveMode</c>：编码器的<b>自适应模式</b>（AQ / 自适应分辨率 / 自适应帧率）。
    ///
    /// <para>
    /// 🔥 <b>本方案把它设成 <c>None</c>（0）</b>，这是手段 10「关闭编码器侧预处理」的第一条：
    /// 自适应量化会<b>逐帧分析画面</b>再分配码率，在桌面内容上收益很小，
    /// 却让编码器多跑一趟分析 —— 8K 下这一趟是实打实的时间。
    /// </para>
    /// <para>
    /// ⚠️ 取值见 <c>codecapi.h</c> 的 <c>eAVEncAdaptiveMode</c>：
    /// <c>None = 0</c> / <c>Resolution = 1</c> / <c>FrameRate = 2</c>。
    /// </para>
    /// </summary>
    internal static readonly Guid CODECAPI_AVEncAdaptiveMode
        = new("4419b185-da1f-4f53-bc76-097d0c1efb1e");

    /// <summary>
    /// <c>CODECAPI_AVEncCommonQualityVsSpeed</c>：画质 / 速度的<b>倾向旋钮</b>（0 = 最快，100 = 最好）。
    ///
    /// <para>
    /// 🔥 <b>设成 0 是手段 10 的核心那条</b>，而且它防的是一个<b>反直觉的坑</b>：
    /// NVIDIA 的 <b>UHQ（Ultra High Quality）</b>调优模式会让 <b>8K 吞吐崩掉</b>
    /// （方案 §0.5：8K 无 SFE 从 48 fps 掉到更低，因为该模式把帧交给 SM 做 RD 优化，
    /// 造成串行化、<b>饿死 NVENC</b>）⇒ <b>「开最高画质」在 8K 实时场景下是反向操作。</b>
    /// </para>
    /// <para>
    /// ⚠️ 它是"倾向"不是"档位"：驱动可以忽略它。所以本类<b>设完要读回</b>
    /// （见 <c>TrySetCodecUInt32Verified</c>），不把"要求了"当成"生效了"。
    /// </para>
    /// </summary>
    internal static readonly Guid CODECAPI_AVEncCommonQualityVsSpeed
        = new("98332df8-03cd-476b-89fa-3f9e442dec9f");

    /// <summary>
    /// <c>CODECAPI_AVScenarioInfo</c>：告诉编码器<b>这条流要拿去干什么</b>。
    ///
    /// <para>
    /// 🔥 <b>取值 <c>eAVScenarioInfo_DisplayRemoting</c>（1）是手段 10 里最有"语义"的一条</b>：
    /// 它让编码器自己关掉一批为<b>相机视频</b>准备的预处理（时域降噪、为运动模糊做的补偿、
    /// 为自然图像调的 AQ），并改用<b>桌面内容</b>的默认策略 —— 文字边缘、大面积纯色、
    /// 硬边矩形，这些才是我们要它优化好的东西。
    /// </para>
    /// <para>
    /// ⚠️ 它对**软件编码器**和一部分老驱动是"不认"的（<c>IsSupported</c> 回 <c>S_FALSE</c>）。
    /// 不认就说"不认"，<b>不要</b>退回去猜一个别的值 —— 那会让日志里出现一个
    /// 我们从未验证过的组合。
    /// </para>
    /// </summary>
    internal static readonly Guid CODECAPI_AVScenarioInfo
        = new("b28a6e64-3ff9-446a-8a4b-0d7a53413236");

    /// <summary>
    /// <c>CODECAPI_AVEncVideoMaxNumRefFrame</c>：最多保留几个<b>参考帧</b>。
    ///
    /// <para>
    /// 🔥 设成 1 是手段 10 的最后一条：参考帧越多，编码器越要保住多张历史帧
    /// （8K 一帧 33 MB 的 NV12 是 50 MB）⇒ <b>显存带宽</b>被它吃掉一块，而桌面推流
    /// 根本不需要多参考（画面是"一帧一帧换"的，不是连续运动）。
    /// </para>
    /// </summary>
    internal static readonly Guid CODECAPI_AVEncVideoMaxNumRefFrame
        = new("964829ed-94f9-43b4-b74d-ef40944b69a0");

    /// <summary>
    /// <c>CODECAPI_AVEncSliceControlMode</c>：一帧切成<b>几个 slice</b>，怎么切。
    ///
    /// <para>
    /// 🔥 <b>这是「手段 2：分帧编码（SFE）」在 Media Foundation 侧唯一找得到的开关。</b>
    /// SFE 的本质是"把一帧切成几片、让几个 NVENC 引擎<b>并行</b>编"，
    /// 而在 MF 的抽象里，能表达"一帧分几片"的只有 slice control。
    /// 单流 8K 从 ≈30 fps 到 ≈62 fps 的收益全部押在它身上。
    /// </para>
    /// <para>
    /// ⚠️ <b>取值含义</b>（<c>codecapi.h</c> 的 <c>eAVEncSliceControlMode</c>）：
    /// <c>0 = 关</c>、<c>1 = 均匀切</c>（每片行数由
    /// <see cref="CODECAPI_AVEncSliceControlSize"/> 给）、<c>2 = 自定义切</c>。
    /// </para>
    /// <para>
    /// 🔴 <b>"设上了"≠"真的并行编了"</b>：SliceControl 是老属性（H.264 时代为
    /// 错误恢复切的片），很多驱动支持它却<b>仍在一个引擎上串行编完所有片</b>。
    /// 真正的 SFE 只有 Ada（RTX 40）上的 NVENC 会做，且官方文档说它<b>不由这个属性控制</b>
    /// ⇒ 所以本类只用它<b>尽力而为</b>，并把"到底生效没有"如实写进 <c>CodecApiNote</c>，
    /// 让真机测试的人看到的是<b>读数</b>而不是承诺。
    /// </para>
    /// </summary>
    internal static readonly Guid CODECAPI_AVEncSliceControlMode
        = new("e9e782ef-5f18-44c9-a90b-e9c3c2c17b0b");

    /// <summary><c>CODECAPI_AVEncSliceControlSize</c>：均匀切时<b>每片多少行</b>（配合上一个属性）。</summary>
    internal static readonly Guid CODECAPI_AVEncSliceControlSize
        = new("92f51df3-07a5-4172-aefe-c69ca3b60e35");

    /// <summary><c>eAVScenarioInfo_DisplayRemoting</c>（1）—— 「这条流是拿去远程看桌面的」。</summary>
    /// <remarks>
    /// ⚠️ 这几个枚举值<b>照 <c>codecapi.h</c> 抄</b>：<c>Unknown=0 / DisplayRemoting=1 /
    /// VideoConference=2 / Archive=3 / LiveStreaming=4 / CameraRecord=5 /
    /// DisplayRemotingWithFeatureMap=6</c>。
    /// </remarks>
    internal const uint ScenarioInfoDisplayRemoting = 1;

    /// <summary><c>eAVEncAdaptiveMode_None</c>（0）—— 关掉自适应量化 / 自适应分辨率 / 自适应帧率。</summary>
    internal const uint AdaptiveModeNone = 0;

    /// <summary><c>eAVEncSliceControlMode</c>：<c>0 = 关</c> / <c>1 = 均匀切</c> / <c>2 = 自定义切</c>。</summary>
    internal const uint SliceControlModeUniform = 1;

    // ──────────────────────────── 接口 IID ────────────────────────────

    internal static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    internal static readonly Guid IID_IMFMediaEventGenerator = new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d");
    internal static readonly Guid IID_IMFMediaEvent = new("df598932-f10c-4e39-bba2-c308f101daa3");
    internal static readonly Guid IID_IMFAttributes = new("2cd2d921-c447-44a7-a13c-4adabfc247e3");
    internal static readonly Guid IID_IMFActivate = new("7fee9e9a-4a89-47a6-899c-b6a53a70fb67");
    internal static readonly Guid IID_IMFSample = new("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4");
    internal static readonly Guid IID_IMFMediaBuffer = new("045fa593-8799-42b8-bc8d-8968c6453507");
    internal static readonly Guid IID_IMFDXGIBuffer = new("e7174cfa-1c9e-48b1-8866-626226bfc258");
    internal static readonly Guid IID_IMFMediaType = new("44ae0fa8-ea31-4109-8d2e-4cae4997c555");
    internal static readonly Guid IID_IMFDXGIDeviceManager = new("eb533d5d-2db6-40f8-97a9-494692014f07");

    /// <summary>
    /// <c>IID_ICodecAPI</c>。⚠️ 它**不在** mingw 的 <c>strmif.h</c> 里带值（那里只有
    /// <c>EXTERN_C const IID</c>），值在 <c>icodecapi.h</c> 与 `libuuid` 里。
    /// 这里取的是 <c>icodecapi.h:68</c> 的 <c>DEFINE_GUID</c>，并与 FreeRDP 的
    /// <c>h264_mf.c</c>（<c>sIID_ICodecAPI</c>）逐字节一致。
    /// </summary>
    internal static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    // ──────────────────────────── 枚举取值 ────────────────────────────

    /// <summary><c>MFT_CATEGORY_VIDEO_ENCODER</c>。</summary>
    internal static readonly Guid CategoryVideoEncoder = new("f79eac7d-e545-4387-bdee-d647d7bde42a");

    /// <summary>
    /// <c>MFT_CATEGORY_VIDEO_DECODER</c>（<c>mfapi.h:266</c>）。
    /// <para>
    /// 🔥 它的用途是 M5 验收里那条「<b>自编自解</b>」：把编出来的码流喂给解码器 MFT 解回来 ——
    /// 这比"文件非空"强得多，因为<b>只有能解回来的码流才算码流</b>。
    /// </para>
    /// </summary>
    internal static readonly Guid CategoryVideoDecoder = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");

    /// <summary><c>MFT_ENUM_FLAG_HARDWARE</c>。</summary>
    internal const uint EnumFlagHardware = 0x00000004;

    /// <summary><c>MFT_ENUM_FLAG_SORTANDFILTER</c>。</summary>
    internal const uint EnumFlagSortAndFilter = 0x00000040;

    /// <summary><c>MFT_ENUM_FLAG_ASYNCMFT</c>。</summary>
    internal const uint EnumFlagAsyncMft = 0x00000002;

    internal const uint MessageCommandFlush = 0x0;
    internal const uint MessageCommandDrain = 0x1;

    /// <summary>
    /// 🔥 <c>MFT_MESSAGE_SET_D3D_MANAGER</c>：<b>零拷贝的接线口</b> ——
    /// 把 <c>IMFDXGIDeviceManager</c> 作为参数发给编码器，它才会走显存路径。
    /// </summary>
    internal const uint MessageSetD3DManager = 0x2;

    internal const uint MessageNotifyBeginStreaming = 0x10000000;
    internal const uint MessageNotifyEndStreaming = 0x10000001;

    /// <summary>⚠️ <c>NOTIFY_END_OF_STREAM = 0x10000002</c> —— <b>比 START 小</b>，别按直觉写。</summary>
    internal const uint MessageNotifyEndOfStream = 0x10000002;

    /// <summary>⚠️ <c>NOTIFY_START_OF_STREAM = 0x10000003</c>。</summary>
    internal const uint MessageNotifyStartOfStream = 0x10000003;

    internal const uint OutputStreamProvidesSamples = 0x100;
    internal const uint OutputStreamCanProvideSamples = 0x200;
    internal const uint OutputStreamWholeSamples = 0x1;

    internal const uint InputStreamWholeSamples = 0x1;
    internal const uint InputStreamFixedSampleSize = 0x4;

    // ── MF_EVENT_TYPE（⚠️ 这些是**枚举值**，不是 GUID）──

    /// <summary>
    /// 🔥 <c>METransformNeedInput</c> = <b>601</b>。
    /// <para>
    /// 异步 MFT 用<b>事件</b>告诉你"我要输入了"，而不是让你轮询 ——
    /// 靠 <c>IMFMediaEventGenerator::GetEvent</c> 拿到事件、
    /// <c>IMFMediaEvent::GetType</c> 读到这个数。
    /// </para>
    /// <para>
    /// ⚠️ <b>它长得像 GUID 常量，但它不是</b>（同族的 <c>METransformHaveOutput</c> 也一样）。
    /// 按 GUID 去查会一无所获，而症状是"编码器一帧都不出"。
    /// </para>
    /// </summary>
    internal const uint EventTransformNeedInput = 601;

    /// <summary><c>METransformHaveOutput</c> = 602。</summary>
    internal const uint EventTransformHaveOutput = 602;

    /// <summary><c>METransformDrainComplete</c> = 603。</summary>
    internal const uint EventTransformDrainComplete = 603;

    /// <summary><c>METransformMarker</c> = 604。</summary>
    internal const uint EventTransformMarker = 604;

    /// <summary><c>MF_EVENT_FLAG_NO_WAIT</c>：没有事件时立刻返回而不是阻塞。</summary>
    internal const uint EventFlagNoWait = 0x1;

    // ── 码率控制（M5 决策 14 的灰度收益**必须**在固定质量下测，见方案 §6.6.4）──

    internal const int RateControlCbr = 0;
    internal const int RateControlQuality = 3;
    internal const int RateControlUnconstrainedVbr = 2;

    // ── 档次 / 交错 / 取值（都是对头文件核过的）──

    /// <summary><c>MFVideoInterlace_Progressive</c>（<c>mfobjects.h:2686</c>）。</summary>
    internal const uint InterlaceProgressive = 2;

    /// <summary><c>eAVEncH264VProfile_High</c>（<c>codecapi.h:272</c>）。</summary>
    internal const uint H264ProfileHigh = 100;

    /// <summary><c>eAVEncH264VProfile_Main</c>（<c>codecapi.h:271</c>）。</summary>
    internal const uint H264ProfileMain = 77;

    /// <summary><c>eAVEncH265VProfile_Main_420_8</c>（<c>codecapi.h:291</c>）—— 8 位 4:2:0 主档。</summary>
    internal const uint HevcProfileMain4208 = 1;

    // ── HRESULT ──

    /// <summary>
    /// MF 的这批 <c>MF_E_*</c> 都是<b>非 0 的"错误"码</b>（最高位为 1），
    /// 而 <c>SUCCEEDED</c> 判的是"非负"。所以<b>必须</b>拿具体值比，
    /// 不能写成 <c>hr &gt; 0</c> / <c>hr == 0</c>：后者会把"还要更多输入"这种
    /// <b>正常状态</b>当成失败（它是本仓 M5 里最容易写错的一处判断）。
    /// <para>全部对着 <c>mferror.h</c> 核实。</para>
    /// </summary>
    internal static class HResults
    {
        /// <summary><c>MF_E_BUFFERTOOSMALL</c> = <c>0xC00D36B1</c>。</summary>
        internal const int BufferTooSmall = unchecked((int)0xC00D36B1);

        /// <summary><c>MF_E_INVALIDMEDIATYPE</c> = <c>0xC00D36B4</c>。类型不被这个 MFT 接受。</summary>
        internal const int InvalidMediaType = unchecked((int)0xC00D36B4);

        /// <summary><c>MF_E_NOTACCEPTING</c> = <c>0xC00D36B5</c>。<b>"这一刻不收输入"</b>，不是故障。</summary>
        internal const int NotAccepting = unchecked((int)0xC00D36B5);

        /// <summary><c>MF_E_NO_MORE_TYPES</c> = <c>0xC00D36B9</c>。可枚举的类型取完了。</summary>
        internal const int NoMoreTypes = unchecked((int)0xC00D36B9);

        /// <summary><c>MF_E_ATTRIBUTENOTFOUND</c> = <c>0xC00D36E6</c>（例如样本没有 CleanPoint）。</summary>
        internal const int AttributeNotFound = unchecked((int)0xC00D36E6);

        /// <summary><c>MF_E_INVALID_STREAM_DATA</c> = <c>0xC00D36CB</c>。</summary>
        internal const int InvalidStreamData = unchecked((int)0xC00D36CB);

        /// <summary><c>MF_E_NO_EVENTS_AVAILABLE</c> = <c>0xC00D3E80</c> —— 用 <c>NO_WAIT</c> 取事件时的正常返回。</summary>
        internal const int NoEventsAvailable = unchecked((int)0xC00D3E80);

        /// <summary><c>MF_E_TRANSFORM_TYPE_NOT_SET</c> = <c>0xC00D6D60</c>。</summary>
        internal const int TransformTypeNotSet = unchecked((int)0xC00D6D60);

        /// <summary><c>MF_E_TRANSFORM_STREAM_CHANGE</c> = <c>0xC00D6D61</c>。</summary>
        internal const int TransformStreamChange = unchecked((int)0xC00D6D61);

        /// <summary>
        /// 🔥 <c>MF_E_TRANSFORM_NEED_MORE_INPUT</c> = <c>0xC00D6D72</c>。
        /// <para><b>这是"我现在还没输出，再去喂一帧"的正常状态</b> —— 解码器每帧都会回它。</para>
        /// </summary>
        internal const int TransformNeedMoreInput = unchecked((int)0xC00D6D72);

        /// <summary>
        /// <c>MF_E_TRANSFORM_ASYNC_LOCKED</c> = <c>0xC00D6D77</c>。
        /// <para>看到它只有一件事要做：给 <c>MF_TRANSFORM_ASYNC_UNLOCK</c> 置 1（在 activate 上）。</para>
        /// </summary>
        internal const int TransformAsyncLocked = unchecked((int)0xC00D6D77);

        /// <summary>
        /// <c>E_UNEXPECTED</c> = <c>0x8000FFFF</c>。
        ///
        /// <para>
        /// 🔥 <b>它在这里不是"出了个没听过的错"，而是 Intel QSV 编码器 MFT 表达
        /// 「这次没有输出」的方式</b> —— 文档里那个位置本该是
        /// <c>MF_E_TRANSFORM_NEED_MORE_INPUT</c>，它却回一个完全没有信息量的
        /// <c>E_UNEXPECTED</c>，靠 <c>MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE</c> 才说得清。
        /// 本机（UHD 620）实测确认。
        /// </para>
        /// </summary>
        internal const int Unexpected = unchecked((int)0x8000FFFF);
    }

    // ── MFT_OUTPUT_DATA_BUFFER.dwStatus 的取值（mftransform.h:94 核实）──
    //
    // 🔴🔴🔴 **它们不是位标志，是四个互斥的枚举值** —— 这一点极其容易写错。
    //   头文件里它们长这样：
    //
    //       MFT_OUTPUT_DATA_BUFFER_INCOMPLETE     = 0x01000000
    //       MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE  = 0x00000100
    //       MFT_OUTPUT_DATA_BUFFER_STREAM_END     = 0x00000200
    //       MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE      = 0x00000300
    //
    //   ⚠️ 于是 `0x300` **不是** "INCOMPLETE|FORMAT_CHANGE"，而是 **NO_SAMPLE 一个值**。
    //   把 `dwStatus` 当位标志来读（写成 `& 0x200` 之类）会读出**完全相反**的结论 ——
    //   本项目就曾在这一点上把"这次没有输出"误读成"编码器要求换格式"。
    //   ⇒ 判它一律用 `==`，并且把名字写出来。

    /// <summary><c>MFT_OUTPUT_DATA_BUFFER_INCOMPLETE</c> = <c>0x01000000</c>。</summary>
    internal const uint OutputDataBufferIncomplete = 0x01000000;

    /// <summary><c>MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE</c> = <c>0x00000100</c>。</summary>
    internal const uint OutputDataBufferFormatChange = 0x00000100;

    /// <summary><c>MFT_OUTPUT_DATA_BUFFER_STREAM_END</c> = <c>0x00000200</c>。</summary>
    internal const uint OutputDataBufferStreamEnd = 0x00000200;

    /// <summary>
    /// <c>MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE</c> = <c>0x00000300</c> ——
    /// <b>"这次一个样本都没产出"</b>。配 <c>E_UNEXPECTED</c> 一起出现（见上）。
    /// </summary>
    internal const uint OutputDataBufferNoSample = 0x00000300;


    // ── VARIANT 类型码（wtypes.h 核实）──

    private const ushort VtUi4 = 19;
    private const ushort VtUi8 = 21;

    /// <summary><c>VT_BOOL</c> = 11（<c>wtypes.h</c>；实测取值）。</summary>
    private const ushort VtBool = 11;

    internal const uint MfVersion = 0x00020070;

    // ──────────────────────────── 结构体 ────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct MftRegisterTypeInfo
    {
        public Guid Major;
        public Guid Sub;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MftInputStreamInfo
    {
        public long MaxLatency;
        public uint Flags;
        public uint Size;
        public uint MaxLookahead;
        public uint Alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MftOutputStreamInfo
    {
        public uint Flags;
        public uint Size;
        public uint Alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MftOutputDataBuffer
    {
        public uint StreamId;
        public IntPtr Sample;
        public uint Status;
        public IntPtr Events;
    }

    /// <summary>
    /// 只装 <c>VT_UI4</c> 的 <c>VARIANT</c>（<c>ICodecAPI::SetValue</c> 用）。
    /// <para>
    /// ⚠️ <b>必须显式布局</b>：x64 上 <c>VARIANT</c> 的联合体从<b>偏移 8</b> 开始
    /// （前面是 <c>vt</c> 与三个保留字，各 2 字节）。用 <c>Sequential</c> 会把它排在偏移 4，
    /// 于是编码器读到一个"看着像个整数、其实不是这个数"的值 ——
    /// 而且多数情况下它<b>不报错</b>，只是参数没生效。
    /// </para>
    /// <para>
    /// 🔥 <b>尺寸写 24，不是 16</b>：x64 上 <c>sizeof(VARIANT) == 24</c>
    /// （联合体里最大的成员是 16 字节的 <c>DECIMAL</c>，对齐 8 ⇒ 8 + 16 = 24）。
    /// 这条<b>不是推的，是量出来的</b> —— 用 mingw 编了
    /// <c>printf("%d", sizeof(VARIANT))</c> 实跑确认，同时确认了
    /// <c>offsetof(vt)=0</c> / <c>offsetof(ulVal)=8</c> / <c>VT_UI4=19</c>。
    /// 写小了虽然"按指针传、只读前 12 字节"时也常常能用，
    /// 但那正是"换个驱动就出问题"的那类隐患。
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct VariantUInt32
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public uint Value;

        internal static VariantUInt32 From(uint value) => new() { Vt = VtUi4, Value = value };

        internal static VariantUInt32 From(int value) => new() { Vt = VtUi4, Value = unchecked((uint)value) };

        /// <summary>
        /// 读回来的<b>确实是个 <c>VT_UI4</c></b>。
        /// <para>
        /// ⚠️ 判它而不是直接取 <see cref="Value"/>：驱动可以把一个"整数属性"回成
        /// <c>VT_I4</c> / <c>VT_BOOL</c> / <c>VT_UI8</c>，而 <see cref="Value"/> 读的是
        /// 联合体固定偏移上的 4 个字节 —— 类型不对时那 4 个字节<b>仍然能读出一个数</b>，
        /// 于是得到"一个看起来合理的错值"。宁可承认读不到。
        /// </para>
        /// </summary>
        internal bool IsUInt32 => Vt == VtUi4;
    }

    /// <summary>
    /// 只装 <c>VT_BOOL</c> 的 <c>VARIANT</c>。
    /// <para>
    /// ⚠️ <b>有些 CodecAPI 要的是 BOOL 而不是 UI4</b>（例如
    /// <c>CODECAPI_AVEncCommonLowLatency</c>）。拿 <c>VT_UI4</c> 去写它，
    /// 编码器多半回 <c>E_INVALIDARG</c> —— 而"参数没设上"和"设上了但没效果"
    /// 在日志里长得一样，所以类型必须给对。
    /// </para>
    /// <para><c>VARIANT_BOOL</c> 是 2 字节、<b>真值 = -1</b>（不是 1）—— 实测确认。</para>
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct VariantBool
    {
        [FieldOffset(0)] public ushort Vt;

        /// <summary><c>VARIANT_BOOL</c>：<c>-1</c> = TRUE / <c>0</c> = FALSE。</summary>
        [FieldOffset(8)] public short Value;

        internal static VariantBool True => new() { Vt = VtBool, Value = -1 };

        internal static VariantBool False => new() { Vt = VtBool, Value = 0 };
    }

    // ──────────────────────────── 槽位号 ────────────────────────────

    /// <summary><c>IMFTransform</c>（26 槽）。</summary>
    internal static class TransformSlots
    {
        internal const int GetStreamCount = 4;
        internal const int GetInputStreamInfo = 6;
        internal const int GetOutputStreamInfo = 7;
        internal const int GetAttributes = 8;
        internal const int GetOutputAvailableType = 14;
        internal const int SetInputType = 15;
        internal const int SetOutputType = 16;
        internal const int GetInputStatus = 19;
        internal const int ProcessEvent = 22;
        internal const int ProcessMessage = 23;
        internal const int ProcessInput = 24;
        internal const int ProcessOutput = 25;
    }

    /// <summary><c>IMFMediaEventGenerator</c>（7 槽）。</summary>
    internal static class EventGeneratorSlots
    {
        internal const int GetEvent = 3;
    }

    /// <summary><c>IMFMediaEvent</c>（37 槽，前 33 槽继承自 <c>IMFAttributes</c>）。</summary>
    internal static class MediaEventSlots
    {
        /// <summary>
        /// ⚠️ <b>刻意不叫 <c>GetType</c></b>：那会隐藏 <see cref="object.GetType()"/>（<c>CS0108</c>），
        /// 而更坏的是调用点 —— <c>MediaEventSlots.GetType()</c> 会静默解析成那个反射方法，
        /// 症状是"读到的事件类型永远不是 601/602"，编码器一帧不出，日志里却毫无异常。
        /// </summary>
        internal const int GetEventType = 33;

        internal const int GetValue = 36;
    }

    /// <summary><c>IMFAttributes</c>（33 槽）。</summary>
    internal static class AttributeSlots
    {
        internal const int GetUINT32 = 7;
        internal const int GetUINT64 = 8;
        internal const int GetGUID = 10;
        internal const int GetAllocatedString = 13;

        /// <summary><c>GetBlobSize</c>（14）—— 读 <c>MF_MT_MPEG_SEQUENCE_HEADER</c> 要先问它多大。</summary>
        internal const int GetBlobSize = 14;

        /// <summary><c>GetBlob</c>（15）。⚠️ 它<b>不分配</b>：调用方要先 <c>GetBlobSize</c> 再给缓冲。</summary>
        internal const int GetBlob = 15;

        internal const int SetUINT32 = 21;
        internal const int SetUINT64 = 22;
        internal const int SetGUID = 24;

        /// <summary><c>SetBlob</c>（26）。</summary>
        internal const int SetBlob = 26;

        internal const int SetUnknown = 27;
    }

    /// <summary><c>IMFActivate</c>（36 槽；前 33 槽来自 <c>IMFAttributes</c>）。</summary>
    internal static class ActivateSlots
    {
        internal const int ActivateObject = 33;
    }

    /// <summary><c>IMFSample</c>（47 槽；前 33 槽来自 <c>IMFAttributes</c>）。</summary>
    internal static class SampleSlots
    {
        internal const int SetSampleTime = 36;
        internal const int SetSampleDuration = 38;
        internal const int GetBufferByIndex = 40;
        internal const int AddBuffer = 42;

        /// <summary>
        /// <c>GetTotalLength</c>（45）—— <b>取"这个样本一共多少字节"的正确口子。</b>
        /// <para>
        /// 🔥 <b>为什么不能用"取第 0 个 buffer 再 <c>Lock</c> 读 currentLength"代替</b>：
        /// 编码器 MFT <b>自带样本</b>（<c>MFT_OUTPUT_STREAM_PROVIDES_SAMPLES</c>）时，
        /// 它给回来的常常是 <b>DXGI 表面支撑的样本</b>，那种 buffer 的 <c>Lock</c>
        /// 报出来的长度与"码流实际长度"可以差<b>三个数量级</b>（实测 640×480 上
        /// 报出过 7.4 MB/帧，真值是几 KB）。而这个错<b>不会报错</b> ——
        /// 只会让人以为"码率大得离谱"，然后去查编码器参数。
        /// </para>
        /// <para>
        /// ⇒ <b>凡是要报"多少字节"，一律走这里。</b>
        /// </para>
        /// </summary>
        internal const int GetTotalLength = 45;
    }

    /// <summary><c>IMFMediaBuffer</c>（8 槽）。</summary>
    internal static class MediaBufferSlots
    {
        internal const int Lock = 3;
        internal const int Unlock = 4;
        internal const int GetCurrentLength = 5;

        /// <summary>
        /// <c>SetCurrentLength</c>（6）。
        /// <para>
        /// 🔥 <b>往缓冲里写完数据必须调它</b>：<c>Lock</c> 只告诉你"容量有多大"，
        /// 不调这一条，<c>GetCurrentLength</c> 回的还是 0 ——
        /// 于是解码器收到一个"零字节的输入样本"，
        /// 表现是<b>不报错、也解不出东西</b>（最难查的一类）。
        /// </para>
        /// </summary>
        internal const int SetCurrentLength = 6;
    }

    /// <summary><c>IMFDXGIBuffer</c>（7 槽）。</summary>
    internal static class DxgiBufferSlots
    {
        internal const int GetResource = 3;
        internal const int SetUnknown = 6;
    }

    /// <summary><c>IMFMediaType</c>（38 槽；前 33 槽来自 <c>IMFAttributes</c>）。</summary>
    internal static class MediaTypeSlots
    {
        internal const int GetMajorType = 33;
    }

    /// <summary><c>IMFDXGIDeviceManager</c>（10 槽）。</summary>
    internal static class DxgiDeviceManagerSlots
    {
        internal const int CloseDeviceHandle = 3;
        internal const int OpenDeviceHandle = 6;
        internal const int ResetDevice = 7;
    }

    /// <summary><c>ICodecAPI</c>（19 槽）。</summary>
    internal static class CodecApiSlots
    {
        internal const int IsSupported = 3;

        /// <summary>读一个编解码器属性的<b>当前值</b>（<c>GetValue</c>）。</summary>
        /// <remarks>
        /// 🔥 <b>槽位顺序是照 SDK 的 <c>icodecapi.h</c> 数的，不是照 DirectShow 时代的记忆写的：</b>
        /// <c>IsSupported(3)</c> → <c>IsModifiable(4)</c> → <c>GetParameterRange(5)</c> →
        /// <c>GetParameterValues(6)</c> → <c>GetDefaultValue(7)</c> → <c>GetValue(8)</c> →
        /// <c>SetValue(9)</c> → <c>RegisterForEvent(10)</c> …
        /// <para>
        /// ⚠️ 中间那个 <c>IsModifiable</c> 是<b>后来插进去的</b>，所以按老资料（没有它）数出来的
        /// <c>GetValue = 7 / SetValue = 8</c> <b>整体错一位</b> —— 而错位的表现是
        /// "把值写进了 RegisterForEvent"：<b>返回 S_OK、读回来还是旧值</b>，
        /// 于是每个调优项都显示"已设"，实际上一个都没设。这一条正是本仓反复强调
        /// 「设了 ≠ 生效」的原因。
        /// </para>
        /// </remarks>
        internal const int GetValue = 8;

        /// <summary>设一个编解码器属性（码控模式 / 质量 / 强制关键帧都走它）。</summary>
        internal const int SetValue = 9;
    }

    // ──────────────────────────── 工厂函数 ────────────────────────────

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFTEnumEx(
        Guid category,
        uint flags,
        IntPtr inputType,
        IntPtr outputType,
        out IntPtr activates,
        out uint count);

    /// <summary><c>MFCreateDXGIDeviceManager</c> —— 零拷贝的关键对象。</summary>
    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IntPtr deviceManager);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateMediaType(out IntPtr mediaType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateSample(out IntPtr sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateMemoryBuffer(uint maxLength, out IntPtr buffer);

    /// <summary>
    /// 🔥 <c>MFCreateDXGISurfaceBuffer</c> —— <b>把一张 D3D11 纹理变成 MF 的 buffer</b>。
    /// <para>
    /// 老写法是 <c>MFCreateVideoSampleFromSurface</c>，但它<b>已被 MSDN 标记为过时</b>，
    /// 而且 mingw 的 <c>mfapi.h</c> 里根本没有它。这条才是现在的正路。
    /// </para>
    /// </summary>
    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateDXGISurfaceBuffer(
        ref Guid riid, IntPtr surface, uint subresourceIndex, int bottomUpWhenLinear, out IntPtr buffer);

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern void CoTaskMemFree(IntPtr ptr);

    // ──────────────────────────── 委托 ────────────────────────────

    internal static class Delegates
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetStreamCountFn(IntPtr self, out uint inputs, out uint outputs);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetInputStreamInfoFn(IntPtr self, uint id, out MftInputStreamInfo info);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetOutputStreamInfoFn(IntPtr self, uint id, out MftOutputStreamInfo info);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetAttributesFn(IntPtr self, out IntPtr attributes);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetAvailableTypeFn(IntPtr self, uint id, uint index, out IntPtr mediaType);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int SetTypeFn(IntPtr self, uint id, IntPtr mediaType, uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetInputStatusFn(IntPtr self, uint id, out uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int ProcessMessageFn(IntPtr self, uint message, nint param);

        /// <summary>⚠️ 有<b>第 4 个</b> <c>flags</c> 参数 —— 凭印象写成 3 个会栈错位。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int ProcessInputFn(IntPtr self, uint id, IntPtr sample, uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int ProcessOutputFn(
            IntPtr self, uint flags, uint count, ref MftOutputDataBuffer samples, out uint status);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetEventFn(IntPtr self, uint flags, out IntPtr mediaEvent);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetEventTypeFn(IntPtr self, out uint eventType);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int ActivateObjectFn(IntPtr self, ref Guid riid, out IntPtr instance);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int AttributeSetUInt32Fn(IntPtr self, ref Guid key, uint value);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int AttributeSetUInt64Fn(IntPtr self, ref Guid key, ulong value);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int AttributeGetUInt32Fn(IntPtr self, ref Guid key, out uint value);

        /// <summary><c>IMFAttributes::GetGUID</c>（10）—— 例如问一个媒体类型的 <c>MF_MT_SUBTYPE</c>。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int AttributeGetGuidFn(IntPtr self, ref Guid key, out Guid value);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int AttributeSetUnknownFn(IntPtr self, ref Guid key, IntPtr unknown);

        /// <summary>
        /// <c>IMFAttributes::SetGUID</c>（槽位 24）。
        /// <para>
        /// ⚠️ <b><c>MF_MT_SUBTYPE</c> 是 GUID 属性</b>，必须走这个 ——
        /// 用 <c>SetUINT32</c> 去写它，返回的是"类型不对"（但很多调用点不看返回值，
        /// 于是症状变成"媒体类型看着设了、编码器说我不认"）。
        /// </para>
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int AttributeSetGuidFn(IntPtr self, ref Guid key, ref Guid value);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int AttributeGetAllocatedStringFn(
            IntPtr self, ref Guid key, out IntPtr value, out uint length);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int AttributeGetBlobSizeFn(IntPtr self, ref Guid key, out uint size);

        /// <summary>⚠️ <c>GetBlob</c> 的第 4 参是<b>缓冲容量（输入）</b>、第 5 参是<b>实际长度（输出）</b>。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int AttributeGetBlobFn(
            IntPtr self, ref Guid key, IntPtr buffer, uint bufferSize, out uint blobSize);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int MediaTypeGetMajorTypeFn(IntPtr self, out Guid majorType);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int SetSampleTimeFn(IntPtr self, long time);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetBufferByIndexFn(IntPtr self, uint index, out IntPtr buffer);

        /// <summary><c>IMFSample::GetTotalLength(out DWORD)</c>（45）—— 样本的字节数。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int GetTotalLengthFn(IntPtr self, out uint length);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int AddBufferFn(IntPtr self, IntPtr buffer);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int BufferLockFn(
            IntPtr self, out IntPtr data, out uint maxLength, out uint currentLength);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int BufferUnlockFn(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int BufferSetCurrentLengthFn(IntPtr self, uint length);

        /// <summary><c>IMFAttributes::SetBlob</c>（26）—— 给解码器输入类型写 SPS/PPS 用。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int AttributeSetBlobFn(IntPtr self, ref Guid key, IntPtr buffer, uint size);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int DxgiBufferGetResourceFn(IntPtr self, ref Guid riid, out IntPtr resource);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int DxgiDeviceManagerResetDeviceFn(IntPtr self, IntPtr device, uint resetToken);

        /// <summary>
        /// <c>IMFDXGIDeviceManager::OpenDeviceHandle(HANDLE *phDevice)</c>（槽 6）。
        /// <para>
        /// 🔥 <b>它是本项目唯一能"证明设备管理器手上真的有一块设备"的动作</b> ——
        /// <c>ResetDevice</c> 回的那个 <c>S_OK</c> 做不到这件事（见
        /// <c>D3D11GpuContext.CreateDeviceManager</c> 里那段"文档自相矛盾"的说明）。
        /// </para>
        /// <para>
        /// ⚠️ <c>HANDLE</c> 在 x64 上是 <b>8 字节</b>，所以是 <c>out IntPtr</c>，
        /// 不是 <c>out int</c> —— 写成 <c>int</c> 会把高 4 字节留给栈上的垃圾，
        /// 拿到一个"看着像句柄、其实不是"的数。
        /// </para>
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int OpenDeviceHandleFn(IntPtr self, out IntPtr deviceHandle);

        /// <summary><c>IMFDXGIDeviceManager::CloseDeviceHandle(HANDLE)</c>（槽 3）。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int CloseDeviceHandleFn(IntPtr self, IntPtr deviceHandle);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int CodecApiSetValueFn(IntPtr self, ref Guid api, ref VariantUInt32 value);

        /// <summary>同上，但要的是 <c>VT_BOOL</c> 的那些 API（如 <c>AVEncCommonLowLatency</c>）。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int CodecApiSetValueBoolFn(IntPtr self, ref Guid api, ref VariantBool value);

        /// <summary>
        /// <c>ICodecAPI::GetValue</c>：把属性<b>当前值</b>读进一个 <c>VARIANT</c>。
        /// <para>
        /// 🔥 它存在的唯一目的是把「要求了」与「生效了」分开：设值是 <b>Best effort</b>
        /// （驱动可以回 <c>S_FALSE</c> 或干脆忽略），只有读回来等值才算数。
        /// </para>
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int CodecApiGetValueFn(IntPtr self, ref Guid api, ref VariantUInt32 value);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int CodecApiIsSupportedFn(IntPtr self, ref Guid api);
    }

    // ──────────────────────────── 属性打包（照 mfapi.h 的行内实现）────────────────────────────

    /// <summary>
    /// <c>MFSetAttributeSize</c> 的等价物 —— <b>把一对 32 位值按"宽度在高位"打包</b>。
    ///
    /// <para>
    /// mfapi.h 里那批 <c>MFSetAttribute*</c> <b>是行内函数</b>（不是导出符号），
    /// 实现就是 <c>Pack2UINT32AsUINT64</c>：<c>((ulong)high &lt;&lt; 32) | low</c>。
    /// 所以这里直接写打包，不声明 P/Invoke（声明了也找不到入口）。
    /// </para>
    ///
    /// <para>
    /// 🔴🔴 <b>"宽度在高位"这件事必须对着头文件确认，不能凭形参名猜</b> ——
    /// 头文件里的形参名本身就是误导的：
    /// <code>
    /// inline UINT64 Pack2UINT32AsUINT64 (UINT32 uh, UINT32 ul) { return (((UINT64) uh) &lt;&lt; 32) | ul; }
    /// inline UINT64 PackSize (UINT32 uw, UINT32 uh) { return Pack2UINT32AsUINT64 (uw, uh); }
    /// inline HRESULT MFSetAttributeSize (…, UINT32 uw, UINT32 uh) { … MFSetAttribute2UINT32asUINT64 (…, uw, uh); }
    /// </code>
    /// ⇒ <c>MFSetAttributeSize</c> 把 <b>width 当作第一个实参</b>传进
    /// <c>Pack2UINT32AsUINT64</c>，而那个函数的第一个形参叫 <c>uh</c>（高位）
    /// ⇒ <b>width 落在高 32 位、height 落在低 32 位</b>。
    /// <c>PackRatio</c>/<c>MFSetAttributeRatio</c> 同理（<b>分子在高位</b>）。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>绝对不要在别处再写一份这个函数。</b> 本仓就发生过一次：
    /// 某个测试文件自己写了个 <c>Pack(uint low, uint high)</c>（<b>形参语序与这里正好相反</b>），
    /// 然后按"宽度、高度"的语序老实调用 —— 编译通过、运行不报错，
    /// 而 <c>MF_MT_FRAME_SIZE</c> 被设成了<b>转置的</b>尺寸、<c>MF_MT_FRAME_RATE</c>
    /// 被设成了 <b>1/30</b>。症状是编码器按错误的跨度去读 NV12 ⇒ 内容变成跨行噪声 ⇒
    /// 码流大得离谱（实测 <b>7.4 MB/帧</b>，修正后 <b>176 B/帧</b>），<b>且没有任何错误码</b>。
    /// </para>
    /// </summary>
    internal static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    /// <summary>把一对 32 位值拆回来（<c>MFGetAttributeSize</c> 的等价物）。</summary>
    internal static (uint High, uint Low) Unpack(ulong packed)
        => ((uint)(packed >> 32), (uint)(packed & 0xFFFFFFFF));
}
