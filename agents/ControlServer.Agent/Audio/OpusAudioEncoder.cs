using ControlServer.Contracts;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent.Audio;

/// <summary>
/// 一个编好的音频帧（<b>线上编码名 + 裸码流 + 它在时间轴上的位置</b>）。
///
/// <para>
/// 🔥 <b>为什么时间戳跟着载荷走，而不是由调用方"送进去的时候顺便记一下"</b>：
/// 编码器<b>会攒帧</b>（见 <see cref="OpusAudioEncoder"/> 对 10 ms → 20 ms 的重组），
/// 所以一个载荷可能是由<b>两段</b>输入拼出来的。它的时间戳必须是
/// <b>第一段</b>的采集时刻 —— 让调用方在外层记，记到的是"送进去的那一刻"，
/// 而那一刻已经晚了半帧（10 ms）。40 ms 的同步预算经不起每帧白扔 10 ms。
/// </para>
/// </summary>
/// <param name="Codec">线上编码名（见 <see cref="RemoteAudioCodecs"/>）。</param>
/// <param name="SampleRate">采样率（Hz）。</param>
/// <param name="Channels">声道数。</param>
/// <param name="SamplesPerChannel">这一帧的<b>每声道</b>样本数（20 ms @ 48 kHz = 960）。</param>
/// <param name="CapturedQpcTicks">这一帧第一段音频被<b>采到</b>的时刻（<c>Stopwatch</c> tick）。</param>
/// <param name="CapturedAt">同上，墙钟表示。</param>
/// <param name="Data">Opus 包（裸字节，不带容器）。</param>
public sealed record EncodedAudioPayload(
    string Codec,
    int SampleRate,
    int Channels,
    int SamplesPerChannel,
    long CapturedQpcTicks,
    DateTimeOffset CapturedAt,
    byte[] Data);

/// <summary>
/// 音频编码器（M5.3）。
///
/// <para>
/// 🔥 <b>为什么它不像视频那样有"平台实现二选一"</b>：视频编码器必须分平台
/// （NVENC / QSV / MF 各自一套），而音频这里<b>只有一个</b> —— 跨平台、纯托管。
/// 所以本接口不是"平台端口"，而是<b>让推流宿主可以被用例替身驱动</b>的接缝：
/// 有了它，"攒帧重组 / 时间戳取第一段 / 编码失败如实上报"这三条能被直接钉住，
/// 而不必真的去开一条 WASAPI 环回。
/// </para>
/// </summary>
public interface IAudioEncoder : IDisposable
{
    /// <summary>本编码器产出的线上编码名。</summary>
    string Codec { get; }

    /// <summary>每声道每帧的样本数（Opus 固定 20 ms）。</summary>
    int SamplesPerChannel { get; }

    /// <summary>最近一次「没编出来」的原因（成功过之后清零）；从未失败时为 <c>null</c>。</summary>
    string? LastFailure { get; }

    /// <summary>
    /// 送入一段 PCM，产出 <b>0 个或多个</b>完整帧。
    ///
    /// <para>
    /// ⚠️ <b>"返回 false" 与 "一个都没产出" 是两件完全不同的事</b>，别混：
    /// </para>
    /// <list type="bullet">
    ///   <item><b>返回 true 且 <paramref name="output"/> 为空</b> —— 正常。
    ///         输入还不够一帧（WASAPI 常见按 10 ms 交数据，而 Opus 一帧是 20 ms），
    ///         编码器把它攒着等下一段；</item>
    ///   <item><b>返回 false</b> —— <b>故障</b>：这一段里至少有一帧没能编出来，
    ///         原因见 <see cref="LastFailure"/>。调用方必须把它当成
    ///         "音频有断点"记下来，而不是当作"还没攒够"。</item>
    /// </list>
    /// <para>
    /// 把两者压成同一个 <c>false</c>，会让"编码器坏了"与"还差 10 ms"在日志上长得一模一样，
    /// 而前者需要去查 CPU / 采样率，后者什么都不用做。
    /// </para>
    ///
    /// <para><b>不抛异常</b>：异常一旦从采集循环里逃出去，整条音频就静默死掉了。</para>
    /// </summary>
    /// <param name="chunk">刚采到的一段 PCM。</param>
    /// <param name="output">产出的完整帧会**追加**到这个集合（调用方负责清空）。</param>
    bool TryEncode(AudioChunk chunk, List<EncodedAudioPayload> output);
}

/// <summary>
/// <b>Opus 编码器</b>（M5.3），走纯托管的 Concentus。
///
/// <para>
/// 🔥 <b>它做的第一件正经事不是编码，是"重组帧"</b>：</para>
/// <list type="bullet">
///   <item>WASAPI 环回按<b>设备周期</b>交数据（共享模式常见 <b>10 ms</b>，即 480 个每声道样本）；</item>
///   <item>Opus 只接受 <b>2.5 / 5 / 10 / 20 / 40 / 60 ms</b> 的定长输入；</item>
///   <item>我们选 <b>20 ms</b>（960 样本）—— 它与浏览器 <c>AudioDecoder</c> 的默认行为最契合，
///         也是"每包开销 / 延迟"之间最常用的折中点。</item>
/// </list>
/// <para>
/// 所以本类内部有一个<b>残帧缓冲</b>：不足 960 就攒着，够了就编一帧、把剩下的挪到前面。
/// ⚠️ 这一步<b>不能省</b>：把 480 个样本直接丢给 Opus 而不告诉它这是半帧，
/// 得到的是"每 10 ms 编出一个 20 ms 的帧" —— 声音会以两倍速播放，而且听不出是哪儿错了。
/// </para>
///
/// <para><b>码率与复杂度</b>：默认 <b>96 kbps</b>（方案 §9.2 的 96–128 kbps 下沿）、
/// 复杂度 5。降复杂度的理由是<b>被控机上跑着展厅业务</b>：
/// Concentus 自述只有 libopus 的 40–50% 速度，而复杂度从 10 降到 5 能省下可观的 CPU，
/// 换来的是极小的音质差别（人耳在 96 kbps 立体声下基本听不出）。</para>
///
/// <para>
/// 🔥 <b>关于"静音不占带宽"（方案 §9.1）—— 这里刻意**不用** DTX，改用 VBR</b>：
/// Opus 默认就是变码率，一段静音编出来只有十几字节，按 50 帧/秒算是每秒不到 1 KB，
/// 已经足够"不占带宽"。而 DTX 的做法是<b>一段时间内根本不发包</b>，
/// 那会让接收端的时间轴上凭空少掉一段时间 —— 正是 AV 同步最怕的错。
/// 用"极小但连续"换"完全不发"，是这一处唯一正确的取舍。
/// </para>
/// </summary>
public sealed class OpusAudioEncoder : IAudioEncoder
{
    /// <summary>目标码率（bps）。96 kbps 是方案 §9.2 区间的下沿，对系统声音足够。</summary>
    public const int DefaultBitrate = 96_000;

    /// <summary>帧长（毫秒）。20 ms 是延迟与每包开销的常用折中。</summary>
    public const int FrameMilliseconds = 20;

    /// <summary>复杂度的默认值。<b>刻意不是 10</b>（见类注释）。</summary>
    public const int DefaultComplexity = 5;

    /// <summary>Opus 每包的理论上限（1275 字节）之外再留一倍余量。</summary>
    private const int MaxPacketBytes = 4000;

    /// <summary>Opus 只支持这几个采样率。出现别的值必须**当场拒开**，不能硬编。</summary>
    private static readonly int[] SupportedRates = { 8_000, 12_000, 16_000, 24_000, 48_000 };

    private readonly Concentus.IOpusEncoder _encoder;
    private readonly int _channels;
    private readonly int _sampleRate;

    /// <summary>残帧缓冲（交错，16 位）。</summary>
    private short[] _pending;
    private int _pendingCount;

    /// <summary>残帧里<b>第一段</b>音频的采集时刻（编出的那一帧要用它）。</summary>
    private long _pendingQpcTicks;
    private DateTimeOffset _pendingAt;

    private bool _disposed;

    /// <param name="channels">声道数（1 或 2）。</param>
    /// <param name="sampleRate">采样率（Hz），必须是 Opus 支持的那几个之一。</param>
    /// <param name="bitrate">目标码率（bps）。</param>
    /// <param name="complexity">复杂度 0–10。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 参数不在 Opus 支持范围内时抛。<b>这是刻意的</b>：它在会话建立时就该爆出来，
    /// 而不是等到推了几分钟之后才在日志里出现一堆"编码失败"。
    /// </exception>
    public OpusAudioEncoder(
        int channels = 2,
        int sampleRate = 48_000,
        int bitrate = DefaultBitrate,
        int complexity = DefaultComplexity)
    {
        if (channels is < 1 or > RemoteAudioLimits.MaxChannels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channels),
                channels,
                $"声道数必须在 1–{RemoteAudioLimits.MaxChannels} 之间（Opus 只支持 1 或 2，多声道需先下混）。");
        }

        if (Array.IndexOf(SupportedRates, sampleRate) < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate),
                sampleRate,
                "Opus 只支持 8000 / 12000 / 16000 / 24000 / 48000 这五个采样率 —— "
                + "44100 在被拒之列，必须在采集侧先重采样（本项目的采集器已请求 48000，见 WindowsWasapiLoopbackCapturer）。");
        }

        _channels = channels;
        _sampleRate = sampleRate;

        SamplesPerChannel = sampleRate / 1000 * FrameMilliseconds;

        _pending = new short[SamplesPerChannel * channels];
        _pendingCount = 0;

        _encoder = Concentus.OpusCodecFactory.CreateEncoder(
            sampleRate,
            channels,
            Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO);

        _encoder.Bitrate = bitrate;
        _encoder.Complexity = Math.Clamp(complexity, 0, 10);

        // ⚠️ 保持默认：FEC 是为**丢包**准备的，而这条链路是本机管道 + 局域网，
        //    丢包极少；开了它每帧都要多带一份冗余（码率上升），收益却用不上。
        _encoder.UseInbandFEC = false;

        // ⚠️ 见类注释：DTX 会让"静音期"在接收端的时间轴上消失，禁用。
        _encoder.UseDTX = false;
    }

    /// <inheritdoc />
    public string Codec => RemoteAudioCodecs.Opus;

    /// <inheritdoc />
    public int SamplesPerChannel { get; }

    /// <inheritdoc />
    public string? LastFailure { get; private set; }

    /// <inheritdoc />
    public bool TryEncode(AudioChunk chunk, List<EncodedAudioPayload> output)
    {
        if (_disposed) return false;

        var samples = chunk.Samples;

        if (samples.Length == 0) return true;

        // 一段输入必须是**整数个**声道样本的整数倍，否则说明格式对不上。
        // 悄悄丢几个样本会让左右声道互换（那是一种"听着像坏了但不报错"的错）。
        if (samples.Length % _channels != 0)
        {
            LastFailure =
                $"采集到的样本数 {samples.Length} 不是声道数 {_channels} 的整数倍，无法解释成 PCM。";

            return false;
        }

        var ok = true;
        var offset = 0;

        // 记录这一段的采集时刻 —— 只有在残帧为空、且这一段的**第一个**样本要进新帧时才用得上。
        var chunkQpc = chunk.CapturedQpcTicks;
        var chunkAt = chunk.CapturedAt;

        while (offset < samples.Length)
        {
            if (_pendingCount == 0)
            {
                // 新帧从这一刻起算。
                _pendingQpcTicks = chunkQpc;
                _pendingAt = chunkAt;
            }

            var take = Math.Min(samples.Length - offset, _pending.Length - _pendingCount);

            Array.Copy(samples, offset, _pending, _pendingCount, take);

            offset += take;
            _pendingCount += take;

            if (_pendingCount < _pending.Length) break; // 还没攒够一整帧。

            if (!EncodePending(output)) ok = false;

            _pendingCount = 0;
        }

        return ok;
    }

    /// <summary>把攒满的一帧编出来。</summary>
    private bool EncodePending(List<EncodedAudioPayload> output)
    {
        var packet = new byte[MaxPacketBytes];

        try
        {
            var written = _encoder.Encode(_pending.AsSpan(0, _pendingCount), SamplesPerChannel, packet, packet.Length);

            if (written <= 0)
            {
                // Opus 返回 0 或负值都表示"这一帧没编出来"。负值是错误码，0 是"空包"。
                LastFailure = $"Opus 没有产出数据（返回 {written}）。";

                return false;
            }

            var data = new byte[written];
            Array.Copy(packet, data, written);

            output.Add(new EncodedAudioPayload(
                Codec: Codec,
                SampleRate: _sampleRate,
                Channels: _channels,
                SamplesPerChannel: SamplesPerChannel,
                CapturedQpcTicks: _pendingQpcTicks,
                CapturedAt: _pendingAt,
                Data: data));

            // 成功一次就清掉上一次的失败记录 —— 与视频编码器同一条规矩：
            // 留着旧的失败会让"现在到底还坏不坏"变成一个要看时间戳才能回答的问题。
            LastFailure = null;

            return true;
        }
        catch (Exception ex)
        {
            // 绝不外抛（见接口注释）。
            LastFailure = $"Opus 编码失败：{ex.GetType().Name}：{ex.Message}";

            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        try
        {
            _encoder.Dispose();
        }
        catch
        {
            // 忽略。
        }
    }
}
