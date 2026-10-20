namespace ControlServer.Platform.Abstractions;

/// <summary>
/// 视频编码格式。
/// <para>
/// 🔥 <b>它不是可以自由挑的</b>：<b>H.264 硬编封顶 4096×4096</b>（所有 NVIDIA 架构都是这个数），
/// 超过就只能走 <see cref="Hevc"/>（封顶 8192×8192）。
/// ⇒ 换句话说，编码格式<b>由被控端分辨率决定</b>，由能力探测给出建议值。
/// </para>
/// </summary>
public enum VideoCodec
{
    H264,
    Hevc,
}

/// <summary>
/// 一台机器上<b>某个编码器</b>的能力。
///
/// <para>
/// 🔥 <b>本类型的一半字段是可空的，这不是偷懒，是本方案的地基之一。</b>
/// 8K 目标的第一步就是"如实知道这台机器能编什么"（§1.6 手段 1），
/// 而"没测出来"和"测出来是不支持"是<b>两件完全不同的事</b>：
/// <c>EncodeEngines = 0</c>（明确无硬编，如 MX250）与 <c>EncodeEngines = null</c>（还没测）
/// 若被写成同一个值，能力探测就会<b>在整条 8K 路线最关键的地方说谎</b>。
/// ⇒ 因此：<b>拿不到就填 <c>null</c>，绝不用 0 / false 顶替。</b>
/// </para>
/// </summary>
/// <param name="EncodeEngines">
/// 编码引擎数量。<c>null</c> = 未知；<c>0</c> = <b>明确</b>无硬编；
/// <c>1</c> = 单引擎（4K60 尚可，8K 约 30 fps）；<c>2</c> = 双引擎（8K60 唯一有落点的一档）。
/// </param>
/// <param name="EngineBasis">
/// <b>"这个引擎数是怎么来的"</b>—— 例如 <c>"PDH 实测"</c> / <c>"官方矩阵：GP108 不支持"</c> / <c>"未知"</c>。
/// <para>
/// 它是刻意加的：现场拿到一个数字时，必须能判断<b>这条结论有多可信</b>，
/// 而不是对着一个不知道从哪来的数拍板采购。
/// </para>
/// </param>
public sealed record EncoderCapabilities(
    IReadOnlyList<VideoCodec> SupportedCodecs,
    int? MaxWidth,
    int? MaxHeight,
    int? MaxFps,
    long? MaxPixelsPerSecond,
    int? EncodeEngines,
    bool? SupportsTenBit,
    bool? SupportsSplitEncode,
    string? AdapterName,
    string? EngineBasis);

/// <summary>
/// 编码器吐出的一帧<b>码流</b>（不是像素 —— 像素从不上 CPU）。
/// </summary>
/// <param name="Sequence">单调递增序号。用于前端"只渲染最新帧"时丢弃过期帧。</param>
public sealed record EncodedFrame(
    VideoCodec Codec,
    bool IsKeyframe,
    ReadOnlyMemory<byte> Data,
    long Sequence);

/// <summary>
/// 视频硬件编码器（M5 核心接口，<b>接口先行的落点</b>）。
///
/// <para>
/// 🔥 <b>为什么第一天就要把它定出来</b>：M5.1 打算用 MJPEG 先把链路打通，
/// 而 MJPEG 是<b>唯一一个必须在 CPU 上软编</b>的选项（4K 单帧 40–80 ms ⇒ 上限 12–25 fps），
/// 4K60 连门都摸不到。若等 M5.1 写完了再抽象，
/// "MJPEG 那套"会渗进管线每一层，换成硬编时等于重写。
/// </para>
///
/// <para>
/// <b>调用形状刻意是"传纹理、拿码流"</b>：<see cref="TryEncode"/> 收一个 GPU 纹理句柄
/// 而不是 <c>byte[]</c>。收 <c>byte[]</c> 会强制把 33 MB（4K）/ 133 MB（8K）的帧
/// 从显存搬到内存再搬回去 —— 那正是零拷贝管线要消灭的那次拷贝。
/// </para>
/// </summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>本实例实际使用的编码格式。</summary>
    VideoCodec Codec { get; }

    /// <summary>
    /// 🔥 <b>黑白（灰度）模式</b>（M5 决策 14）。
    /// <para>
    /// 置位后，编码前把<b>色度平面填成常量 128（中性灰）</b>，输出仍是标准 4:2:0 码流
    /// （真正的 4:0:0 单色既不被硬编器接受，浏览器也基本不解）。
    /// 好处是解码端<b>零改动</b>、无兼容性风险。
    /// </para>
    /// <para>
    /// ⚠️ <b>收益取决于内容，不能当省带宽的银弹</b>：彩色视频省 10–30%，
    /// 而<b>白底黑字的界面/表格趋近 0</b>（luma 已承载全部细节）。
    /// 它真正的互补收益是<b>消除 4:2:0 色边、改善文字锐度</b>。
    /// </para>
    /// <para>
    /// ⚠️ <b>置位后必须调用 <see cref="RequestKeyframe"/></b>，否则新模式要等下一个
    /// 自然关键帧才生效（上层表现为"切了没反应"）。
    /// </para>
    /// </summary>
    bool Grayscale { get; set; }

    /// <summary>本实例的能力（来自能力探测）。</summary>
    EncoderCapabilities Capabilities { get; }

    /// <summary>
    /// 编一帧。
    /// <para>
    /// 返回 <c>false</c> 表示<b>这一帧没编出来</b>（编码器忙 / 队列满 / 本例不支持的输入）。
    /// 调用方按"<b>丢帧</b>"处理即可 —— 队列深度 = 1、忙就丢帧是本管线的既定策略，
    /// <b>丢帧不是异常</b>，堆积延迟才是。
    /// </para>
    /// <para><b>不抛异常</b>：异常一旦从编码循环里逃出去，整条流就静默死掉了。</para>
    /// </summary>
    bool TryEncode(
        nint textureHandle,
        int width,
        int height,
        bool forceKeyframe,
        out EncodedFrame? frame);

    /// <summary>让下一个编出的帧是<b>关键帧</b>（切换编码参数 / 新观众加入时用）。</summary>
    void RequestKeyframe();
}

/// <summary>
/// 内存路径编码器产出的<b>线上编码名</b>。
///
/// <para>
/// ⚠️ <b>它必须与 <c>ControlServer.Contracts.RemoteCodecs</c> 逐字一致</b>，
/// 而两个程序集之间没有引用关系（Abstractions 不认识 Contracts，这是刻意的分层）。
/// 于是这里必然出现<b>两处字符串</b> —— 不一致的表现是前端"认不出这个编码"，
/// 而且没有任何编译错误。=> <b>由集成用例钉住它们相等</b>（见
/// <c>EncoderCapabilityProbeTests.The_stream_codec_names_line_up</c>）。
/// 这与 <c>VideoCodec</c> ⇄ <c>RemoteCodecs</c> 的处理方式是同一条规矩。
/// </para>
/// </summary>
public static class StreamCodecs
{
    /// <summary>H.264 / AVC。语义见 <c>RemoteCodecs.H264</c>（硬编封顶 4096×4096）。</summary>
    /// <remarks>
    /// ⚠️ M5.1 不用它（那时管道上跑的是 MJPEG），但名字<b>现在就要在这里</b>：
    /// 管道上的编码名是"中心侧要认出来的东西"，
    /// 而 M5.4 换硬编时改的是<b>编码器</b>、不是这条链路上的任何一层。
    /// 等到那时才加常量，等于承认"这层当时只为 MJPEG 写"，而它会漂。
    /// </remarks>
    public const string H264 = "h264";

    /// <summary>HEVC / H.265。语义见 <c>RemoteCodecs.Hevc</c>（<b>超 4K 的唯一硬编路径</b>）。</summary>
    public const string Hevc = "hevc";

    /// <summary>MJPEG（每帧一张独立 JPEG）。语义见 <c>RemoteCodecs.Mjpeg</c>。</summary>
    public const string Mjpeg = "mjpeg";
}

/// <summary>
/// 🔥 编好的一帧（<b>线上编码名 + 裸码流</b>）。
/// <para>
/// <b>为什么不直接用 <see cref="EncodedFrame"/></b>：那个类型的
/// <see cref="EncodedFrame.Codec"/> 是 <see cref="VideoCodec"/> 枚举，
/// 而枚举里只有硬件编码器真能编的 H.264 / HEVC。
/// M5.1 的脚手架是 MJPEG —— 它<b>不是</b>一种可交付的编码格式（方案 §1.1 已排除），
/// 硬塞进枚举会让"这台机器支持 H.264"与"我们临时用 JPEG 联调"变成同一类事实。
/// </para>
///
/// <para><b>类型刻意不含会话信息</b>：编码器不该知道"这是谁的流"。</para>
/// </summary>
/// <param name="Codec">线上编码名（<c>ControlServer.Contracts.RemoteCodecs</c> 的取值）。</param>
/// <param name="CapturedUnixMs">抓屏时刻（UTC 纪元毫秒，仅用于测延迟，不进页面）。</param>
/// <param name="Description">
/// <b>解码器初始化描述</b>（M5.4-b 新增）—— <c>avcC</c> / <c>hvcC</c>。
///
/// <para>
/// 🔥 <b>它<b>有</b>还是<b>没有</b>，决定的是码流的<b>封装</b>，不是"多一段字节"</b>
/// （W3C 的 AVC/HEVC codec 注册里写死的）：带上它 ⇒ 码流被假定为<b>长度前缀</b>
/// （ISO 14496-15 §5.3.2 / §8.3.2 的 canonical 形式）；
/// 不带它 ⇒ 码流被假定为 <b>Annex-B</b>。
/// ⇒ 「贴了 avcC、码流却还是 Annex-B」＝ <c>configure()</c> 失败或者花屏，<b>两者是连体的</b>。
/// </para>
///
/// <para>
/// ⚠️ <b>两条路都是合法选择，而本批默认走"不带"</b>（方案 §3.4.2、§8 第 6 步：
/// 带内为默认、带外留开关）。默认带内的理由是<b>失败方式更干净</b>：
/// 它<b>对码流一个字节都不改</b>（热路径零转换），最坏只是"浏览器解不了"——
/// 而那在第 8 步会由 <c>isConfigSupported</c> 直接给一个布尔值。
/// </para>
///
/// <para>
/// 🔴 这里原来写着"本批实测倾向于不带，因为 MF 会在每个关键帧都重复 SPS/PPS，
/// 所以 Annex-B 内联天然满足'每个关键块自带全套参数集'"。
/// 前半句<b>有证据</b>（§3.4.2 两个关键帧；本轮 <c>gopSize = 4</c> 补到 <b>8/8</b>），
/// 结论也确实是"默认不带" —— 但理由要换成<b>失败方式</b>，而不是"已经万无一失"。
/// </para>
///
/// <para>
/// ⚠️ 因为"每个关键帧都带参数集"是<b>编码器的行为，不是规格的保证</b>：
/// 换一块卡、换一个驱动就可能不一样，而那时候症状是"重连之后一直花屏、两端日志都正常"。
/// ⇒ 推流宿主在<b>每个关键帧</b>上数一次（<c>RemoteStreamHost</c> 的
/// <c>_keyframesWith/WithoutParameterSets</c>），把这件事从"读文档"变成"看读数"；
/// 带外模式下遇到缺参数集的关键帧还会自动用缓存那份把 description 顶起来。
/// </para>
///
/// <para>⚠️ 只在关键帧上附带（新观众/重连天然拿到；每帧都发是白费带宽）。</para>
/// </param>
/// <param name="CapturedMonoTicks">
/// <b>抓到这一帧那一刻的单调时钟读数</b>（<see cref="System.Diagnostics.Stopwatch.GetTimestamp()"/> 的值）。
///
/// <para>
/// 🔥 <b>它为什么必须由编码器带出来，而不是让调用方自己在编完之后取一次</b>：
/// 异步编码器的输出<b>晚一次才到</b>（M5.4-b 实测：喂 17 帧出 16 帧）⇒ 本次调用取到的
/// 是<b>上一次</b>喂进去那帧的字节。调用方在编完之后取时刻，就把"这一帧的到达时刻"
/// 贴到了"上一帧的字节"上 —— 而 AV 同步（M5.3）正是靠这个数算音画差。
/// 几十毫秒的错位在唇音上看得出来。
/// </para>
///
/// <para>
/// ⚠️ 它<b>是原始计时器读数，不是毫秒</b>：同一台机器上拿会话原点的读数减它才有意义
/// （跨进程/跨机器比较没有意义，那是 QPC 的性质）。<c>0</c> = 本编码器没有上报
/// ⇒ 调用方只能退回"现在"（并应当知道那带上了一份编码耗时）。
/// </para>
/// </param>
public sealed record EncodedStreamPayload(
    string Codec,
    bool IsKeyframe,
    int Width,
    int Height,
    long CapturedUnixMs,
    byte[] Data,
    byte[]? Description = null,
    long CapturedMonoTicks = 0);

/// <summary>
/// <b>只编画面里的一个矩形</b>（M5.5 的 ROI 与分块编码共用的底座）。
///
/// <para>
/// 坐标一律是<b>画面（源纹理）坐标系</b>，<c>X/Y</c> 是这一块的左上角，
/// <c>Width/Height</c> 是它自己的像素尺寸（= 编码输出尺寸，<b>1:1，不缩放</b>）。
/// </para>
///
/// <para>
/// 🔥 <b>为什么 ROI 与分块编码必须共用同一个类型</b>：它们是同一件事的两个用法 ——
/// ROI 是"只要一块"，分块是"切成好几块"。如果各写一套（一个叫 viewport、一个叫 tile），
/// 转换器、指针叠加、帧头、前端拼接全都要写两遍，而两遍之间任何一处不一致
/// 都不会报错，只会让"8K 滚一下之后画面错位"这类缺陷永远查不出来。
/// </para>
///
/// <para>
/// ⚠️ <b>必须偶数对齐</b>：NV12 是 4:2:0 半平面，奇数宽高会让色度平面没有整数行数。
/// 这个约束<b>由分块布局（<c>RemoteTileLayout</c>）保证</b>，而不是在这里"尽量"——
/// 拿奇数尺寸去建转换器会直接失败，那一路流就一帧都出不来。
/// </para>
/// </summary>
public readonly record struct EncodeRegion(int X, int Y, int Width, int Height)
{
    /// <summary>这一块是不是正好等于整幅画面（<c>true</c> ⇒ 与 M5.4 的行为完全一致）。</summary>
    public bool Covers(int width, int height)
        => X == 0 && Y == 0 && Width == width && Height == height;

    /// <summary>与另一块的交集；没有交集时返回 <c>null</c>。</summary>
    public EncodeRegion? Intersect(EncodeRegion other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(X + Width, other.X + other.Width);
        var bottom = Math.Min(Y + Height, other.Y + other.Height);

        if (right <= left || bottom <= top) return null;

        return new EncodeRegion(left, top, right - left, bottom - top);
    }

    public override string ToString() => $"{Width}×{Height}+{X}+{Y}";
}

/// <summary>
/// <b>怎么把一幅画面切成可编的块</b>（M5.5）—— 纯函数，不碰设备，可以脱离机器单测。
///
/// <para>
/// 🔥 <b>为什么"切块"必须是纯函数并且只有一处实现</b>：块边界错一格不会报错，
/// 只会让画面在<b>接缝处撕裂</b>或者右下角少一条 —— 而"看起来只是编码质量差"
/// 会把排查带向码率、驱动、网卡。这里一旦被单测钉住，接缝问题就不再需要真机复现。
/// </para>
/// </summary>
public static class EncodeRegionLayout
{
    /// <summary>整幅画面（不切）。</summary>
    public static IReadOnlyList<EncodeRegion> Whole(int width, int height)
        => [new EncodeRegion(0, 0, Even(width), Even(height))];

    /// <summary>
    /// 把 <paramref name="viewport"/>（<c>null</c> = 整幅画面）切成若干<b>竖条</b>，
    /// 每条的宽 <b>不超过</b> <paramref name="maxTileWidth"/>（<c>0</c> = 不切）。
    ///
    /// <para>
    /// 竖条而不是网格：编码器的上限是<b>单边长度</b>（8192），而现场那种超限的形态
    /// 几乎都是<b>横向拼接</b>（3×4K = 11520×2160）。切成网格只会让块数翻倍、
    /// 接缝从 2 条变成 3 条，一块好处都没有。
    /// </para>
    /// </summary>
    public static IReadOnlyList<EncodeRegion> Tiles(EncodeRegion? viewport, int maxTileWidth)
    {
        var box = viewport ?? default;

        if (viewport is null)
        {
            // 调用方还没给尺寸时不要返回空列表（空 = "什么都不编"，
            // 那会让整条流一帧都出不来）。由调用方在知道尺寸后再来一次。
            return [];
        }

        var width = Even(box.Width);
        var height = Even(box.Height);

        if (width <= 0 || height <= 0) return [];

        if (maxTileWidth <= 0 || width <= maxTileWidth)
        {
            return [new EncodeRegion(box.X, box.Y, width, height)];
        }

        // ⚠️ 向上取整：宁可多一块小的，也不能让某一块超上限 ——
        //    超上限的那一块<b>整路流都建不起来</b>，而多出来的那块只是接缝多一点。
        var count = (width + maxTileWidth - 1) / maxTileWidth;

        // 均分并且条宽取偶：块宽相同 ⇒ 每块编出来的 P 帧大小接近，
        // 不会出现"最后一块特别小、码率分配不均"。偶数对齐保证 X 也是偶数（色度对齐）。
        var stripe = Even((width + count - 1) / count);

        if (stripe <= 0) stripe = Even(width);

        var regions = new List<EncodeRegion>(count);
        var x = box.X;
        var end = box.X + width;

        for (var i = 0; i < count && x < end; i++)
        {
            var remaining = end - x;

            // 🔴 **最后一块必须把剩下的全吃掉**，不能再用均分出来的那个宽度：
            //    均分值经过偶数对齐之后会"比应得的少 1–2 像素"，
            //    而这几像素被丢掉的表现正是**画面右边少一条**（看起来像抓屏裁掉了）。
            //    这个缺陷是本文件自己的用例抓出来的（16400 / 3 ⇒ 少 2 像素），
            //    所以这里的写法是"算出来的"，不是"看起来差不多"。
            var take = i == count - 1 ? remaining : Math.Min(stripe, remaining);

            // 最后一块可能不是偶数（width 是偶数、前面都是偶数 ⇒ 其实一定是偶数；
            // 这里仍然兜一次，免得"奇数尺寸"从别处漏进来把一个编码器搞崩）。
            take = Even(take);

            if (take <= 0) break;

            regions.Add(new EncodeRegion(x, box.Y, take, height));

            x += take;
        }

        return regions;
    }

    /// <summary>
    /// 把一个区域<b>夹进</b>画面内，并把 X/Y/宽/高都对齐到偶数。
    ///
    /// <para>
    /// ⚠️ 越界与奇数是两种<b>都会被驱动安静接受</b>的输入：越界就裁掉、奇数就让色度平面错位。
    /// 所以调用方必须<b>夹完再建</b>，而不是"建了再看能不能用"。
    /// </para>
    /// </summary>
    /// <returns>夹完为空的区域（宽或高 &lt; 2）返回 <c>null</c>。</returns>
    public static EncodeRegion? Clamp(EncodeRegion region, int width, int height)
    {
        var left = Math.Max(0, Even(region.X));
        var top = Math.Max(0, Even(region.Y));

        var right = Math.Min(Even(width), Even(region.X + region.Width));
        var bottom = Math.Min(Even(height), Even(region.Y + region.Height));

        // 至少 2×2：NV12 的最小可编单元就是 2×2。
        if (right - left < 2 || bottom - top < 2) return null;

        return new EncodeRegion(left, top, right - left, bottom - top);
    }

    /// <summary>向下取偶（负数的处理与"截断"一致，调用方负责先夹进画面）。</summary>
    private static int Even(int value) => value - (value % 2);
}

/// <summary>
/// <b>一块编好的码流</b>：它自己是画面的哪一块、整幅画面有多大。
///
/// <para>
/// 🔥 <b>为什么不把这些字段直接塞进 <see cref="EncodedStreamPayload"/></b>：
/// 那个记录是"M5.1 以来所有编码路径的公共出口"，MJPEG 与内存路径都产它，
/// 而它们<b>永远只产整帧</b>。给公共出口加四个"分块专用"的可空字段，
/// 等于让每条路都留四个"该填但没填"的坑 —— 而坑的填法没人能验证。
/// 这里另立一个类型，分块那条路<b>必须</b>给出区域（不可空），就骗不过去了。
/// </para>
/// </summary>
/// <param name="TileIndex">第几块（0 起）。单块时恒为 0。</param>
/// <param name="TileCount">这一帧一共几块。前端按它决定要不要等别的块到齐。</param>
/// <param name="Region">这块码流对应画面的哪个矩形。</param>
/// <param name="FrameWidth">整幅画面宽（前端画布的宽）。</param>
/// <param name="FrameHeight">整幅画面高。</param>
/// <param name="Stream">码流本身（与整帧那条路同一个形状）。</param>
public sealed record EncodedRegionPayload(
    int TileIndex,
    int TileCount,
    EncodeRegion Region,
    int FrameWidth,
    int FrameHeight,
    EncodedStreamPayload Stream);

/// <summary>
/// <b>能只编一个子矩形</b>的 GPU 编码器（M5.5）。
///
/// <para>
/// 🔥 <b>它是可选的（另一个接口），不是往 <see cref="ISurfaceFrameEncoder"/> 上加方法</b>：
/// 内存路径（MJPEG）与任何未来的软编路径都做不了"只编一块"，往公共接口加方法
/// 等于逼它们各写一个"我做不到"的假实现 —— 而假实现迟早会被当成真实现调用。
/// </para>
///
/// <para>
/// ⚠️ <b>换区域 = 重建整条管线</b>（转换器与编码器都写死了尺寸），
/// 代价是 ≈0.3 秒的空窗且首帧必是关键帧。所以调用方<b>必须去抖</b>
/// （见 <c>RemoteStreamHost</c> 的视口去抖），不能滚动一次就换一次。
/// </para>
/// </summary>
public interface IRegionSurfaceEncoder : ISurfaceFrameEncoder
{
    /// <summary>
    /// <b>当前实际要编的区域</b>（由视口与单块上限算出来的结果）。
    /// <para>
    /// 🔥 它<b>可读</b>不是为了好看：日志与用例都要能回答"这一路到底切成了几块、每块多大"。
    /// 只提供 Set 不提供 Get 时，一切验证都只能靠"帧率差不多"——
    /// 而那恰恰是"其实一块都没切、只是画面糊了点"的表现。
    /// </para>
    /// <para>
    /// ⚠️ 单块上限与当前视口<b>不单独对外暴露</b>（M5.5 收尾时删掉的）：它们<b>一个消费点都没有</b>，
    /// 留着就是"接口上看着能做的事"——而<b>运行中改块宽会重建整条管线</b>（≈0.3 秒空窗），
    /// 一个没人调、又这么贵的入口，最可能的归宿是被误用。要看"现在是什么状态"，
    /// 编码器的 <c>Describe()</c> 已经把两块信息都写进一句话里了。
    /// </para>
    /// </summary>
    IReadOnlyList<EncodeRegion> Regions { get; }

    /// <summary>
    /// 换视口（<c>null</c> = 回到整幅画面）。
    ///
    /// <para>
    /// ⚠️ <b>下一次编帧时生效，那一刻重建管线</b>（≈0.3 秒空窗、首帧必关键帧）。
    /// 调用方<b>必须去抖</b>：滚动一次就换一次会让画面一直在"重建 → 关键帧"里打转。
    /// </para>
    /// </summary>
    /// <returns><c>false</c> = 这个矩形不可能编（太碎 / 负坐标）⇒ <b>保持原样</b>。</returns>
    bool SetViewport(EncodeRegion? viewport);

    /// <summary>
    /// 一次编出<b>全部</b>区域。
    ///
    /// <para>
    /// 与 <see cref="ISurfaceFrameEncoder.TryEncodeSurface"/> 的差别只有一处，但很要紧：
    /// 后者只产<b>一块</b>，多块时其中几块会被"来不及"吞掉 —— 而<b>丢一块</b>
    /// 在画面上是"那一块停在上一帧"，不是"整帧卡一下"。
    /// </para>
    /// </summary>
    /// <param name="encoded">
    /// 输出。<b>先 <c>Clear()</c> 再填</b>：调用方复用同一个 List 是刻意的（每帧都新建
    /// 一个 List 会在 60 fps 上变成 GC 压力）。
    /// </param>
    bool TryEncodeSurfaceRegions(
        CapturedSurface surface,
        bool forceKeyframe,
        List<EncodedRegionPayload> encoded);
}

/// <summary>
/// <b>编码器的共同底座</b>（M5.4-b）——「不管像素从哪来，它都产码流」。
///
/// <para>
/// 🔥 <b>为什么要有这一层，而不是让两个编码器接口各写一遍这三个成员</b>：
/// 推流宿主（<c>RemoteStreamHost</c>）<b>同时只会持有一个编码器</b>
/// —— 要么走 GPU 硬编、要么走内存路径的 MJPEG 降级（见方案 §3.5 的回退链），
/// 二者<b>不会并存</b>。没有共同底座，宿主就得为"编码器"和"画面来源"各开一个字段
/// （<c>IFrameEncoder?</c> ／ <c>ISurfaceFrameEncoder?</c>）并到处写
/// <c>if (a is not null) ... else if (b is not null) ...</c>；
/// 有底座之后，它只持有一个 <see cref="IVideoFrameEncoder"/>，
/// 只在"抓屏那一步"分一次叉（<c>is ISurfaceFrameEncoder</c>）。
/// </para>
///
/// <para>
/// ⚠️ <b>它刻意不含「编一帧」</b>：那个方法的入参（<see cref="CapturedFrame"/> 的
/// <c>byte[]</c> 还是 <see cref="CapturedSurface"/> 的纹理句柄）正是两条路的分水岭。
/// 把它提到这一层，等于把"像素在哪"重新变成一个运行时才知道的问题 ——
/// 而 <see cref="CapturedFrame"/> 与 <see cref="CapturedSurface"/> 之所以是两个类型，
/// 就是为了让这个问题在<b>编译期</b>有答案。
/// </para>
/// </summary>
public interface IVideoFrameEncoder : IDisposable
{
    /// <summary>本编码器产出的线上编码名（<see cref="StreamCodecs"/> 的取值）。</summary>
    string Codec { get; }

    /// <summary>
    /// <b>真的丢了</b>多少帧（累计）。
    ///
    /// <para>
    /// 🔴🔴 <b>它与"<c>TryEncode*</c> 返回了 <c>false</c>"<b>不是</b>一件事，这条区分是必需的。</b>
    /// 异步编码器的输出<b>晚一次调用才到</b>（M5.4-b 实测：喂 17 帧出 16 帧）——
    /// 那一轮就是"返回了 false，但一帧都没丢"。把两者当同一件事，现场看到的是
    /// <b>页面上"掉了很多帧"而实际画面一帧没缺</b>，于是排查方向从"编码器"被带到"网络"去。
    /// </para>
    ///
    /// <para>
    /// ⇒ 推流循环应当这样用它：<b>调用前后各读一次，增量 &gt; 0 才算丢帧</b>；
    /// <b>不要</b>去读 <see cref="LastFailure"/> 的文案来分类 —— 文案一改，分类就错。
    /// （同一条规矩的另一处落点：<c>WindowsMediaFoundationVideoEncoder.FramesDroppedBusy</c>。）
    /// </para>
    /// </summary>
    long DiscardedFrames { get; }

    /// <summary>
    /// 最近一次「没编出来」的<b>原因</b>（成功过之后清零）；从未失败时为 <c>null</c>。
    /// <para>
    /// 🔥 它是<b>排障必需品，不是可选的装饰</b>：这条路上"画面卡住"有几个
    /// 完全不同的原因 —— <b>编码跟不上</b>（CPU 被吃满）、<b>帧超过上行单条消息上限</b>
    /// （被主动丢弃）、<b>编码器忙</b>（队列深度 1 ⇒ 主动丢帧）。它们都表现为
    /// <c>TryEncode*</c> 返回 <c>false</c>，没有这句话，日志上只剩一个"编码失败"，
    /// 而现场只能猜。
    /// </para>
    /// </summary>
    string? LastFailure { get; }

    /// <summary>
    /// 黑白（灰度）模式（M5 决策 14）：置位后色度取中性，输出仍是标准 4:2:0 码流，
    /// 解码端零改动。
    /// <para>
    /// ⚠️ <b>置位后必须让下一个帧成为关键帧</b>：内容发生了不连续（历史帧全是彩色的），
    /// 不重开 GOP 就会让观众看到"切了开关 → 画面慢慢从彩色渐变到灰度"，那看起来就像坏了。
    /// 两条路径达成这件事的方式不同，见各自的实现文档。
    /// </para>
    /// </summary>
    bool Grayscale { get; set; }

    /// <summary>
    /// <b>指针合成这一路的诊断快照</b>（M5.4-b 第 5 步）。
    ///
    /// <para>
    /// 🔥 <b>它为什么必须在接口上、而不能留在实现类里</b>：合成指针只有 <b>GPU 路径</b>会做，
    /// 而要看它的人（<c>RemoteStreamHost</c>，住在 Agent 库、<b>跨平台</b>）<b>只拿得到接口</b>
    /// —— 于是"观看端看不到鼠标"在现场日志里只剩一句"没动"。
    /// 这与 <see cref="DiscardedFrames"/> / <see cref="LastFailure"/> 是同一条理由：
    /// <b>排障必需品，不是装饰</b>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>"没画上"有三类，语气必须分得开</b>：① 这一帧本来就没有指针／指针在画面外 ——
    /// <b>正常</b>；② 形状还没拿到 —— <b>开机头几帧的正常</b>；③ 读回或上传失败 —— <b>真故障</b>。
    /// "画上"与"失败"两个计数分开记，就是为了不让前两类被读成第三类。
    /// </para>
    ///
    /// <para>
    /// 默认实现返回 <c>null</c> = <b>这条路不合成指针</b>（内存 / MJPEG 路径），没什么可说的。
    /// </para>
    /// </summary>
    string? CursorDiagnostics => null;
}

/// <summary>
/// <b>内存路径</b>的视频帧编码器（M5.1 脚手架）。
///
/// <para>
/// 🔥 <b>它与 <see cref="IVideoEncoder"/> 并存，不是重复设计</b>：
/// 两者的<b>数据位置</b>不同 —— 前者吃 <c>byte[]</c>（像素已经在内存里），
/// 后者吃 GPU 纹理句柄（像素不许离开显存）。生产路径（M5.4）走 <see cref="IVideoEncoder"/>；
/// 本接口只服务两件事：
/// </para>
/// <list type="number">
///   <item><b>M5.1 打通链路</b> —— 先用最便宜的编解码器把
///         「抓屏 → 编码 → 管道 → 中继 → 浏览器」这条链子接上，
///         这样 M5.4 换硬编时，链路上没有任何一层需要跟着改；</item>
///   <item><b>降级链的末端</b>（方案 §1.5）—— 连硬编都没有的机器上，
///         "720p30 MJPEG 兼容模式"仍是能让人看见画面的最后一档。</item>
/// </list>
///
/// <para>
/// ⚠️ 因此实现方<b>必须在 4K 及以上说"我干不了"</b>（<see cref="TryEncode"/> 返回 <c>false</c>，
/// 或主动拒开），而不是硬编到把被控机 CPU 吃光 —— 后者表现为"开了远控，展厅业务卡了"。
/// </para>
/// </summary>
public interface IFrameEncoder : IVideoFrameEncoder
{
    /// <summary>
    /// 编一帧。
    /// <para>
    /// 返回 <c>false</c> = <b>这一帧没编出来</b>（编码器忙 / 输入尺寸不支持）。
    /// 调用方按"<b>丢帧</b>"处理 —— 丢帧不是异常，堆积延迟才是。
    /// </para>
    /// <para><b>不抛异常</b>：异常一旦从推流循环里逃出去，整条流就静默死掉了。</para>
    /// </summary>
    /// <param name="forceKeyframe">
    /// 是否要求关键帧。MJPEG 每帧都是独立 JPEG，<b>这个参数无实际作用</b>，
    /// 保留它是为了与 <see cref="IVideoEncoder"/> 形状一致（换实现时上层不必改）。
    /// </param>
    bool TryEncode(CapturedFrame frame, bool forceKeyframe, out EncodedStreamPayload? encoded);
}

/// <summary>
/// <b>编码尺寸可以在开流之后按实际画面调整</b>的编码器（M7.10）。
///
/// <para>
/// 🔥 <b>为什么必须有这个口子，而不是"建编码器的时候把尺寸传进去"</b>：
/// GPU 那条路上编码器<b>自己就是抓屏器的持有者</b>（零拷贝要共用同一个 D3D11 设备），
/// 所以它必须赶在挑屏<b>之前</b>建好（见 <c>RemoteStreamHost.Start</c> 里
/// "先建编码器，再挑屏"那一段）。而挑屏之前<b>没人知道这一路要编多大</b> ——
/// 直到 <c>Prepare</c> 拿到屏描述符。
/// </para>
///
/// <para>
/// 🔥🔥 <b>固定一个降采样上限的现场代价</b>（2026-10-19）：MJPEG 把长边钉在 1920，
/// 单屏 1080p 正好、三屏拼接 5760×2160 就变成<b>每块屏只有 640 宽</b>。
/// 缩略图状态看不出来，一旦点"全屏"把画面放大，满屏都是糊的 ——
/// 而同一台机器的单屏是原生分辨率，于是用户看到的是"合并显示的全屏效果明显更差"。
/// </para>
///
/// <para>
/// ⚠️ <b>实现方必须做到"下一帧生效、不重建"</b>：这个属性只会被设置一两次
/// （开流时按实际画面尺寸算一次），不值得为它重建一整套编码器资源。
/// </para>
/// </summary>
public interface IScalableFrameEncoder
{
    /// <summary>
    /// 降采样后的长边上限（像素）。<c>0</c> = 不降采样。
    /// <para>
    /// ⚠️ 改它<b>不改变码流的坐标系</b>：帧头里的"整幅"仍按实际编出来的尺寸报
    /// （见 <c>RemoteFrameExtent</c>），否则画面会缩在左上角。
    /// </para>
    /// </summary>
    int MaxLongEdge { get; set; }
}

/// <summary>
/// <b>GPU 路径</b>的视频帧编码器（M5.4-b）—— 吃的是<b>显存里的纹理</b>，不是内存里的字节。
///
/// <para>
/// 🔥 <b>它为什么必须与 <see cref="IFrameEncoder"/> 分开、而不能合成一个接口</b>：
/// 两者的<b>数据位置</b>不同（显存 vs 内存），而"像素在哪"决定了整条链子的形状 ——
/// 合成一个接口只有两种写法，<b>两种都错</b>：
/// </para>
/// <list type="bullet">
///   <item>让 <c>TryEncode(CapturedFrame)</c> 在 GPU 实现里"忽略入参"⇒
///         调用方会<b>白抓一帧内存图再丢掉</b>（4K 一帧 33 MB 过 PCIe），
///         而"这个参数被忽略"是<b>看不见</b>的陷阱；</item>
///   <item>补一条"把内存帧传上显存再编"的路径 ⇒ 没有调用方需要它，
///         但每个实现都要维护它，而且它会把零拷贝的真实状态掩盖掉。</item>
/// </list>
///
/// <para>
/// 🔥 <b>它连抓屏器一起带着，这是结构性要求、不是顺手</b>：
/// 零拷贝要求「抓屏 → 色彩转换 → 硬编」<b>共用同一个 D3D11 设备</b>。
/// 而这个设备是<b>抓屏器在绑屏时建的</b>（<c>D3D11GpuContext</c>，随换屏重建）。
/// 若让"抓屏器"与"编码器"各由一条工厂线产出，两条线就必须在构造顺序上互相等
/// —— 那是靠调用顺序维持的不变式，而它<b>一旦被破坏不会报错</b>，
/// 只会让驱动偷偷做一次"回 CPU 再上去"的搬运（4K 是 1.99 GB/s）。
/// ⇒ 把两者收进同一个对象，这个不变式就变成<b>类型级</b>的。
/// </para>
///
/// <para>
/// ⚠️ <b>线程亲和</b>：抓屏与编码都要求"始终在同一个线程上"（DXGI Desktop Duplication
/// 内部有该线程的状态，异步 MFT 的事件队列同理）。因此本接口的
/// <see cref="TryEncodeSurface"/> <b>不是线程安全的</b>，且应当从推流主循环那一个线程调用。
/// </para>
/// </summary>
public interface ISurfaceFrameEncoder : IVideoFrameEncoder
{
    /// <summary>
    /// 🔥 <b>与编码器共用同一个 D3D11 设备的那个抓屏器</b>。
    /// <para>
    /// 它对外暴露有两个用途，都不是"顺手"：<b>①</b> 会话创建时要靠它
    /// <see cref="IScreenCapturer.EnumerateScreens"/> 挑屏与校验；
    /// <b>②</b> 推流循环要靠 <see cref="IScreenCapturer.CaptureSurfaceAsync"/> 取帧 ——
    /// 而那一帧的纹理<b>必须</b>是编码器手上那个设备产出的。
    /// </para>
    /// <para>
    /// ⚠️ <b>不要把它拿去另建一路流</b>：本实例与它共享设备与状态，
    /// 一个 <see cref="ISurfaceFrameEncoder"/> 对应一条推流链。
    /// </para>
    /// </summary>
    IScreenCapturer Capturer { get; }

    /// <summary>
    /// <b>本实例此刻抓的是哪一块屏</b>（<see cref="ScreenDescriptor.Id"/>）；
    /// 还没绑好时为<b>空串</b>。
    ///
    /// <para>
    /// 🔥🔥 <b>它为什么必须在接口上，而不是"实现类的细节"</b>：
    /// 挑屏规则在两条路上是<b>刻意不同</b>的 —— 内存路径优先<b>虚拟桌面</b>
    /// （GDI 能拼），本路径优先<b>主屏</b>（DDA 一次只能抓一个 output）。
    /// 而屏幕是<b>本实现的</b>抓屏器挑的（零拷贝要求抓屏与编码共用同一个 D3D11 设备）。
    /// </para>
    /// <para>
    /// ⇒ 推流宿主若<b>自己也挑一次</b>，多屏机上必然挑到另一块屏：
    /// 两个组件各自都"挑对了"，于是没有任何一处报错、日志也正常 ——
    /// 症状是 <b>看着 A 的屏幕、实际操作 B</b>。
    /// 唯一出路是"<b>实现挑、宿主搬</b>"，而"搬"的前提就是本属性与
    /// <see cref="ScreenName"/>、<see cref="FrameSize"/> 能读出来。
    /// </para>
    /// </summary>
    string ScreenId { get; }

    /// <summary>同上，给人看的名字（日志与页面上用它）。</summary>
    string ScreenName { get; }

    /// <summary>
    /// 当前编码用的画面尺寸（还没建起来时是 0×0）。
    /// <para>
    /// ⚠️ 它是<b>真实纹理的尺寸</b>，不是调用方要求的尺寸 —— 尺寸闸门与前端画布都该用它。
    /// </para>
    /// </summary>
    (int Width, int Height) FrameSize { get; }

    /// <summary>
    /// 编一帧 <b>GPU 侧</b>画面（零拷贝路径：转 NV12 → 硬编，全程不落内存）。
    /// <para>
    /// 返回 <c>false</c> = <b>这一帧没编出来</b>（编码器忙 / 转换失败）⇒ 调用方按丢帧处理，
    /// 理由见 <see cref="IVideoFrameEncoder.LastFailure"/>。
    /// </para>
    /// <para>
    /// ⚠️ <paramref name="surface"/> 的纹理<b>只在下一次抓屏之前有效</b>
    /// （DDA 的纹理是复用的）⇒ 本方法必须与"抓"紧挨着，中间不许有
    /// <c>await</c>、不许缓存这个 surface。
    /// </para>
    /// <para><b>不抛异常</b>：异常一旦从推流循环里逃出去，整条流就静默死掉了。</para>
    /// </summary>
    /// <param name="forceKeyframe">
    /// 是否要求关键帧。<b>本路径上它是真起作用的</b>（与 <see cref="IFrameEncoder"/> 那条对照）：
    /// H.264/HEVC 的 P 帧参考前面的帧，丢一帧就在参考链上戳一个洞，
    /// 此后每一帧都是花的 —— <b>而解码器不报错</b>。
    /// </param>
    bool TryEncodeSurface(
        CapturedSurface surface,
        bool forceKeyframe,
        out EncodedStreamPayload? encoded);

    /// <summary>
    /// 取<b>解码器初始化描述</b>的原始字节（<c>MF_MT_MPEG_SEQUENCE_HEADER</c>，
    /// 通常是带 <c>00 00 00 01</c> 起始码的 <b>Annex-B</b> 形式的 SPS/PPS）。
    ///
    /// <para>
    /// 🔥 <b>给出的是原始形式，不是 avcC</b>：这个属性在所有见过的编码器上都是 Annex-B
    /// （见方案 §3.4），而 WebCodecs 的 <c>description</c> 要的是 avcC。
    /// 中间那道转换是<b>纯函数</b>，住在 <c>ControlServer.Agent.Video</c> 里
    /// —— 平台层<b>不做</b>它（那是分层判据，不是省事）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>建实例的那一刻问它一定是 <c>false</c></b>：这个属性挂在"编码器<b>实际采用</b>的
    /// 输出类型"上，而那个类型要等第一次出数据时才谈定（异步 MFT 的
    /// <c>MF_E_TRANSFORM_STREAM_CHANGE</c>）。⇒ 本方法在<b>编出第一帧之后</b>才会开始返回真。
    /// </para>
    /// <para>
    /// ⚠️ <b>返回 <c>false</c> 不代表码流不完整</b>：MF 的编码器默认按 Annex-B 输出，
    /// 参数集直接混在码流里（M5.4-a 第 4 步实测：本属性读回 <b>36 字节</b>、
    /// 含 3 字节起始码 ⇒ 它<b>不是恒为空</b>）。
    /// 那时正确的做法是走"不带 description"那条路 —— <b>不是</b>编一段空的出来。
    /// </para>
    ///
    /// <para>
    /// 🔸 <b>推流宿主只把它当兜底、不当首选</b>：那些字节是"编码器<b>说</b>自己用的参数集"，
    /// 而码流里真正用的那一份才是唯一权威 —— 首选它会出现"配置与码流不一致"
    /// 这种不报错的错。参见 <c>RemoteStreamHost.TryRescueParameterSetsFromEncoder</c>。
    /// </para>
    /// </summary>
    bool TryReadParameterSets(out byte[] parameterSets);
}

/// <summary>
/// 「本平台没写内存路径编码器」的兜底实现。
/// <para>
/// 与 <see cref="UnsupportedEncoderCapabilityProbe"/> 同一条理由：
/// 它说的是"<b>我们没做</b>"，不是"这台机器编不了"。
/// </para>
/// </summary>
public sealed class UnsupportedFrameEncoder : IFrameEncoder
{
    private readonly string _reason;

    public UnsupportedFrameEncoder(string? reason = null)
        => _reason = string.IsNullOrWhiteSpace(reason)
            ? "本平台未提供内存路径的视频编码器。"
            : reason;

    /// <inheritdoc />
    public string Codec => "unsupported";

    /// <inheritdoc />
    public string? LastFailure => Reason;

    /// <inheritdoc />
    /// <remarks>它从来没编过任何一帧 ⇒ 也就没"丢"过什么（"没做"与"丢了"是两件事）。</remarks>
    public long DiscardedFrames => 0;

    /// <inheritdoc />
    public bool Grayscale { get; set; }

    /// <summary>不可用的原因（会被带进日志与页面）。</summary>
    public string Reason => _reason;

    /// <inheritdoc />
    public bool TryEncode(CapturedFrame frame, bool forceKeyframe, out EncodedStreamPayload? encoded)
    {
        encoded = null;

        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // 无资源可放。
    }
}

/// <summary>
/// 硬件编码<b>能力探测</b>（M5 §1.6 手段 1 的落点）。
///
/// <para>
/// 🔥 <b>它是整个 8K 目标的地基</b>：后面所有"目标帧率 / 编码格式 / 要不要开分帧编码"
/// 的决定都建立在它的结论上。这也意味着<b>它说错话的代价最大</b>——
/// 所以实现的第一原则不是"给出答案"，而是<b>"不确定就承认不确定"</b>
/// （见 <see cref="EncoderCapabilities.EncodeEngines"/> 对 <c>null</c> 与 <c>0</c> 的区分）。
/// </para>
/// </summary>
public interface IEncoderCapabilityProbe
{
    /// <summary>
    /// 探测本机的编码能力。<b>不抛异常</b> —— 探测不出来是"结论为空"，
    /// 不是"调用方崩掉"。
    /// </summary>
    Task<EncoderCapabilityReport> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 「本平台没写探测」的兜底实现。
///
/// <para>
/// 🔥 <b>它说的是"我们没做"，不是"这台机器没有编码能力"。</b>
/// 两者的区别在排查时很要紧：前者是排期问题，后者是硬件结论。
/// 注册一个能说出这句话的实现，是为了让上层<b>一个 <c>if</c> 都不用写</b> ——
/// 否则调用方要么写平台分支、要么拿到一句 <c>InvalidOperationException</c>
/// （"没有注册 IEncoderCapabilityProbe"），后者在日志里看起来像配置事故。
/// </para>
///
/// <para>
/// 放在本程序集（而不是各个 <c>Platform.*</c> 里各写一份）是刻意的：
/// 这个结论与平台无关，写两遍就会有<b>两句措辞不同、含义相同</b>的说明，
/// 而现场一定会有人以为它们不一样。参见同文件的 <c>NoopThermalSource</c>。
/// </para>
/// </summary>
public sealed class UnsupportedEncoderCapabilityProbe : IEncoderCapabilityProbe
{
    private readonly string _reason;

    public UnsupportedEncoderCapabilityProbe(string? reason = null)
        => _reason = string.IsNullOrWhiteSpace(reason)
            ? "本平台未提供编码能力探测（M5 的远程桌面只覆盖 Windows 被控端）。"
            : reason;

    /// <inheritdoc />
    public Task<EncoderCapabilityReport> ProbeAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new EncoderCapabilityReport(
            Adapters: Array.Empty<EncoderAdapterCapability>(),

            // ⚠️ 空表是"没探测"，不是"没有硬编" —— 所以 Summary 必须把话说死。
            SupportedCodecs: Array.Empty<VideoCodec>(),
            RecommendedCodec: null,
            Summary: _reason + "这不代表本机没有硬件编码能力。"));
}

/// <summary>一台机器整体的编码能力结论。</summary>
/// <param name="Adapters">
/// 枚举到的显示适配器。<b>包含软件光栅器</b>（<c>Microsoft Basic Render Driver</c>），
/// 并以 <see cref="EncoderAdapterCapability.IsSoftwareRenderer"/> 标记 ——
/// 由使用方决定要不要排除，而不是在这里悄悄丢掉（丢掉了就没人知道它存在过）。
/// </param>
/// <param name="SupportedCodecs">
/// <b>全机范围内确实有硬件编码器</b>的格式。它 <b>≠</b>「理论上能编的格式」：
/// 由 Media Foundation 的硬件编码器 MFT 枚举给出（M5.0 第二批）。
/// <para>
/// ⚠️ 空表是<b>有含义的</b>：说明这台机器一个硬件编码器都没发现 ——
/// 与"还没探测"（由 <c>null</c> 表示）是两件事，别混。
/// </para>
/// </param>
/// <param name="RecommendedCodec">
/// 建议使用的编码格式。<c>null</c> = <b>尚不能给出建议</b>（例如还没测出支持哪些格式）。
/// 判据是分辨率：<c>≤4096 宽用 H.264</c>，更宽<b>只能</b>用 HEVC。
/// </param>
/// <param name="Summary">给人看的一句话结论（直接进页面与日志）。</param>
public sealed record EncoderCapabilityReport(
    IReadOnlyList<EncoderAdapterCapability> Adapters,
    IReadOnlyList<VideoCodec> SupportedCodecs,
    VideoCodec? RecommendedCodec,
    string Summary);

/// <summary>单个显示适配器。</summary>
/// <param name="DedicatedVideoMemoryBytes">显存字节数（0 = 核显/共享内存，不是"未知"）。</param>
/// <param name="SupportedCodecs">
/// 这块适配器<b>确实能硬件编码</b>的格式（按厂商匹配硬件编码器 MFT 得出）。
/// <b>空表 = 没发现它的硬编能力</b>——这是本方案里唯一能可靠得出
/// 「某块卡明确无硬编」（引擎数 0）的依据。
/// </param>
public sealed record EncoderAdapterCapability(
    string Name,
    uint VendorId,
    uint DeviceId,
    ulong DedicatedVideoMemoryBytes,
    bool IsSoftwareRenderer,
    IReadOnlyList<VideoCodec> SupportedCodecs,
    int? EncodeEngines,
    string? EngineBasis);
