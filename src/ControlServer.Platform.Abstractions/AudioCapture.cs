namespace ControlServer.Platform.Abstractions;

/// <summary>
/// 采集到的音频格式。
///
/// <para>
/// 🔥 <b>故意写死成"16 位有符号整数、交错、<see cref="Channels"/> 声道"</b>，
/// 而不是带上"位深 / 是否浮点 / 是否交错"这一整套参数：</para>
/// <list type="bullet">
///   <item>Opus 的输入本来就是 <c>short[]</c>（16 位）；</item>
///   <item>WASAPI 在共享模式下交上来的常见格式是 32 位浮点，
///         而那一步转换<b>由平台实现负责</b>（见 <c>WindowsWasapiLoopbackCapturer</c>），
///         这样上层与编码器<b>永远只看到一种形状</b>；</item>
///   <item>多一种形状就多一处"忘了处理"的机会，而它的症状是刺耳的噪声
///         —— 那种"一听就知道错了、但完全不知道错在哪"的故障。</item>
/// </list>
/// </summary>
/// <param name="SampleRate">采样率（Hz）。</param>
/// <param name="Channels">声道数（1 或 2）。</param>
public sealed record AudioCaptureFormat(int SampleRate, int Channels)
{
    /// <summary>一秒钟的字节数（16 位 × 声道数），用于估算码率与缓冲。</summary>
    public int BytesPerSecond => SampleRate * Channels * 2;

    /// <summary>给人看的一行（进日志）。</summary>
    public override string ToString() => $"{SampleRate} Hz / {Channels} ch / 16-bit PCM";
}

/// <summary>
/// 采集到的一小段 PCM。
///
/// <para>
/// 🔥 <b>为什么带两个时间戳</b>：与视频帧头同一条理由（见 <c>RemoteStreamFrameHeader</c>）
/// —— <see cref="CapturedAt"/> 回答"这一段离现在多久了"（延迟），
/// <see cref="CapturedQpcTicks"/> 回答"音与画谁先谁后"（AV 同步）。
/// </para>
/// </summary>
/// <param name="Samples">
/// 交错 PCM 样本（16 位有符号）。长度 = <c>每声道样本数 × 声道数</c>。
/// </param>
/// <param name="IsSilent">
/// 🔥 <b>WASAPI 明确表示"这一段是静音、缓冲区内容未定义"。</b>
///
/// <para>
/// 无视这个标志（直接拿缓冲区当有效数据）会把<b>上一次残留的旧音频</b>当成新声音编出去 ——
/// 表现为"没播放声音时反而听到一段重复的杂音"。
/// 收到它时<b>必须把 <paramref name="Samples"/> 清零</b>，而不是照抄缓冲区。
/// </para>
/// <para>
/// ⚠️ 注意它<b>不等于"这一段可以丢"</b>：静音也是时间轴的一部分。
/// 丢掉它会让音频与视频之间凭空少掉一段时间，正好是 AV 同步最怕的那种错。
/// </para>
/// </param>
/// <param name="CapturedQpcTicks">
/// 音频引擎<b>真正采到这一段</b>的时刻，换算成 <c>Stopwatch</c> 的 tick 单位。
/// <para>
/// ⚠️ <b>不是"我们读到它的时刻"</b>：后者会带上一个可变的设备缓冲延迟（10–30 ms），
/// 而 40 ms 的 AV 同步预算经不起这么花。
/// </para>
/// </param>
/// <param name="CapturedAt">同上，墙钟表示（仅用于测延迟，不用于同步）。</param>
public sealed record AudioChunk(
    short[] Samples,
    bool IsSilent,
    long CapturedQpcTicks,
    DateTimeOffset CapturedAt)
{
    /// <summary>每声道的样本数（解码端据此算这一段的时长）。</summary>
    public int FramesPerChannel(int channels) => channels <= 0 ? Samples.Length : Samples.Length / channels;
}

/// <summary>
/// <b>系统播放声音的采集</b>（WASAPI loopback / PulseAudio monitor / …）。
///
/// <para>
/// 🔥 <b>抓的是"正在播放的声音"，不是麦克风</b>：远控要听到的是被控机上放出来的东西
/// （视频里的对白、系统提示音、展厅的演示音频），而不是被控机房里的环境噪声。
/// 麦克风回传是另一件事（对讲），本轮不做（方案 §9.4）。
/// </para>
///
/// <para>
/// 🔥 <b>它只能跑在用户会话里</b>：与抓屏、注入同一条不可回避的理由 ——
/// 服务在 <b>Session 0</b>，那里<b>没有音频端点</b>。
/// 在 Session 0 里采集拿到的要么是失败，要么是"一个不存在的桌面的声音"
/// （返回成功、用户什么都听不到）。
/// </para>
///
/// <para><b>约定三条</b>（与遥测 / 音量端口同）：<b>绝不抛异常</b> /
/// <b>拿不到就是"不可用"并给出人话原因</b> / <b>上层不写平台判断</b>。</para>
/// </summary>
public interface IAudioCapturer : IDisposable
{
    /// <summary>
    /// 本平台此刻是否<b>真的能采到系统声音</b>。
    /// <para>
    /// ⚠️ 与音量控制那条同样的坑：<b>不是简单的 <c>OperatingSystem.IsWindows()</c></b>。
    /// 没有音频输出设备的机器（虚拟机 / 无声卡工控机 / 声卡被禁用）上，
    /// "支持音频采集"这句话是<b>假的</b> —— 报 true 会让上层把
    /// "这台机器没有可用音频设备"退化成一句"没有声音"，现场只能去查网络。
    /// </para>
    /// <para>代价是每次询问都真的去取一次端点；调用点很少，不值得为它缓存（设备会插拔）。</para>
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>不可用的原因（可用时为空串）。会被打进日志 —— 让"没声音"能一眼定位到层。</summary>
    string AvailabilityNote { get; }

    /// <summary>实际会送出的格式；不可用时为 <c>null</c>（<b>不许猜一个默认值顶上</b>）。</summary>
    AudioCaptureFormat? Format { get; }

    /// <summary>
    /// 开始采集并<b>持续产出</b>，直到被取消或设备消失。
    ///
    /// <para>
    /// ⚠️ <b>它必须在一条专用线程上跑</b>（实现方负责）：WASAPI 的 COM 对象要求
    /// 创建与使用在同一个套间里，而这个接口的调用方是托盘的异步循环 ——
    /// 那上面的线程是换着用的。实现方自己起线程、自己 <c>CoInitializeEx</c>，
    /// 对上层只暴露一个异步序列。
    /// </para>
    /// <para>
    /// <b>绝不抛</b>：序列结束时要么是正常取消，要么是设备掉了 ——
    /// 后者要把原因写进日志（由实现方决定），但绝不能让它变成
    /// 一个把推流循环带崩的异常。
    /// </para>
    /// </summary>
    IAsyncEnumerable<AudioChunk> CaptureAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 「本平台没写音频采集」的兜底实现。
/// <para>
/// 与 <see cref="UnsupportedAudioController"/> 同一个思路：注册它而不是"不注册"，
/// 让上层一个 <c>if</c> 都不用写；"本平台不支持"由 <see cref="IsAvailable"/> 与
/// <see cref="AvailabilityNote"/> 一路带到日志与页面上，
/// 而不是表现为一次"成功但什么都没发生"。
/// </para>
/// </summary>
public sealed class UnsupportedAudioCapturer : IAudioCapturer
{
    private readonly string _note;

    public UnsupportedAudioCapturer(string? note = null)
        => _note = string.IsNullOrWhiteSpace(note)
            ? "本平台未实现系统声音采集（M5.3 的远程桌面只覆盖 Windows 被控端）。"
            : note;

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public string AvailabilityNote => _note;

    /// <inheritdoc />
    public AudioCaptureFormat? Format => null;

    /// <inheritdoc />
    public async IAsyncEnumerable<AudioChunk> CaptureAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // ⚠️ 一个空序列 + 一条日志（立刻结束）：**不能**在这里"永远等下去" ——
        //    那样上层会以为"音频在跑、只是暂时没有声音"，而真相是它根本不可能有声音。
        //    产出零个元素会让上层的循环当场收场，进而把 unavailable 如实报上去。
        await Task.CompletedTask;
        yield break;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // 无资源可放。
    }
}
