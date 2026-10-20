namespace ControlServer.Platform.Abstractions;

/// <summary>音量操作（M4.5）。语义照搬现场已有的指令字母表（<c>+</c> / <c>-</c> / <c>0</c> / <c>1</c>）。</summary>
public enum HostAudioAction
{
    /// <summary>调高一格（<c>+</c>）。</summary>
    VolumeUp,

    /// <summary>调低一格（<c>-</c>）。</summary>
    VolumeDown,

    /// <summary>静音（<c>0</c>）。</summary>
    Mute,

    /// <summary>取消静音（<c>1</c>）。</summary>
    Unmute,
}

/// <summary>
/// 当前默认音频输出端点的<b>真实</b>状态。
/// <para>
/// 🔥 <b>刻意是两个可空值，而不是一个 <c>bool IsMuted</c></b>：拿不到就是 <c>null</c>。
/// 用 <c>false</c> 冒充"没静音"会让日志与页面显示一个从没读到过的状态 ——
/// 与遥测那条"绝不用 0 表示不知道"是同一条纪律。
/// </para>
/// </summary>
/// <param name="VolumePercent">主音量（0~100；读不到 = null）。</param>
/// <param name="Muted">是否静音（读不到 = null）。</param>
public sealed record HostAudioState(int? VolumePercent, bool? Muted)
{
    /// <summary>完全未知（读不到端点时的返回值）。</summary>
    public static readonly HostAudioState Unknown = new(null, null);

    /// <summary>人类可读的一行（进日志用）。<b>不知道就说不知道</b>，不要编。</summary>
    public string Describe()
    {
        if (VolumePercent is null && Muted is null) return "状态未知";

        var volume = VolumePercent is { } percent ? $"{percent}%" : "音量未知";
        var mute = Muted switch { true => "已静音", false => "未静音", null => "静音状态未知" };

        return $"{volume}（{mute}）";
    }
}

/// <summary>一次音量操作的结果。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Message">人话说明（失败原因是"为什么没动成"）。</param>
/// <param name="State"><b>操作之后</b>从系统读回来的真实状态（读不到则各项为 null）。</param>
public sealed record HostAudioResult(bool Ok, string Message, HostAudioState State)
{
    /// <summary>成功。</summary>
    public static HostAudioResult Success(string message, HostAudioState state) => new(true, message, state);

    /// <summary>失败。</summary>
    public static HostAudioResult Failure(string message) => new(false, message, HostAudioState.Unknown);
}

/// <summary>
/// 主机音量控制（M4.5）：调高 / 调低 / 静音 / 取消静音。
///
/// <para>
/// 🔥 <b>它只能在用户会话里跑</b>。会话是音频端点的宿主：Session 0（服务）里
/// <c>GetDefaultAudioEndpoint</c> 拿到的（或拿不到的）音频设备和用户桌面上那台完全不是一回事，
/// 在那里改音量是"返回成功、用户什么也听不到"的无声失败。所以本接口的实现由
/// <b>托盘进程</b>持有，服务只能通过 <c>ISessionCommandChannel</c> 请它代劳。
/// </para>
///
/// <para><b>为什么做成端口而不是直接写 CoreAudio</b>：COM 接口声明是平台专有的
/// （Windows CoreAudio / Linux PulseAudio），按项目铁律 #2，平台差异只落在
/// <c>Platform.Windows</c> / <c>Platform.Linux</c>，由组合根二选一注册。
/// 上层（会话管道）因此一个 <c>if (Windows)</c> 都不用写。</para>
///
/// <para><b>约定三条</b>（与遥测端口同）：<b>绝不抛异常</b> /
/// <b>取不到就是 null</b> / <b>上层不写平台判断</b>。</para>
/// </summary>
public interface IAudioController
{
    /// <summary>本平台是否支持音量控制（且此刻拿得到默认输出端点）。</summary>
    bool IsAvailable { get; }

    /// <summary>不可用的原因（可用时为空串）。会被打进日志 —— 让"音量调不动"能一眼定位到层。</summary>
    string AvailabilityNote { get; }

    /// <summary>读当前真实状态。<b>不改变任何东西</b>，读不到给 <see cref="HostAudioState.Unknown"/>。</summary>
    HostAudioState Read();

    /// <summary>执行一次音量操作，并返回操作后读回来的真实状态。</summary>
    HostAudioResult Apply(HostAudioAction action);
}

/// <summary>
/// 音量控制的<b>不支持实现</b>。
/// <para>
/// 与电源 / 遥测那几个 Noop 同一个思路：注册它而不是"不注册"，让上层一个 <c>if</c> 都不用写；
/// "本平台不支持"由 <see cref="IsAvailable"/> 与 <see cref="AvailabilityNote"/> 一路带到日志上，
/// 而不是表现为一次"成功但什么都没发生"。
/// </para>
/// </summary>
public sealed class UnsupportedAudioController : IAudioController
{
    private readonly string _note;

    public UnsupportedAudioController(string note) => _note = note;

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public string AvailabilityNote => _note;

    /// <inheritdoc />
    public HostAudioState Read() => HostAudioState.Unknown;

    /// <inheritdoc />
    public HostAudioResult Apply(HostAudioAction action)
        => HostAudioResult.Failure($"本平台不支持音量控制：{_note}");
}
