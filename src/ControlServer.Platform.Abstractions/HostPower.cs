namespace ControlServer.Platform.Abstractions;

/// <summary>一次电源操作的结果。</summary>
/// <param name="Ok">是否已成功<b>发起</b>（注意：不是"机器已经关掉了"）。</param>
/// <param name="Message">人话说明（成功时是一句"已安排…秒后…"，失败时是原因）。</param>
public sealed record HostPowerResult(bool Ok, string Message)
{
    /// <summary>已成功发起。</summary>
    public static HostPowerResult Success(string message) => new(true, message);

    /// <summary>发起失败（权限不足 / 平台不支持 / 命令不存在）。</summary>
    public static HostPowerResult Failure(string message) => new(false, message);
}

/// <summary>
/// 主机电源控制（M4.5）：关机 / 重启<b>整台计算机</b>。
///
/// <para>
/// ⚠️ <b>与「重启 Agent 服务」完全是两件事</b>：那个是 <c>AgentCommandNames.Restart</c>
/// （只把客户端服务重新拉起来），这里是 <c>AgentPowerActions.Restart</c>（整机重启）。
/// 两者混用的现场表现是"运维想重启一下客户端服务，结果把用户的机器关了"。
/// </para>
///
/// <para><b>为什么做成端口而不是直接调 <c>shutdown.exe</c></b>：命令名与参数是平台专有的
/// （Windows <c>shutdown</c> / Linux <c>systemctl</c>），按项目铁律 #2，
/// 平台差异只落在 <c>Platform.Windows</c> / <c>Platform.Linux</c>，
/// 由组合根按 <c>OperatingSystem.IsWindows()</c> 二选一注册。
/// 上层（UDP 指令通道）因此一个 <c>if (Windows)</c> 都不用写。</para>
///
/// <para>
/// <b>「已发起」不等于「已关机」</b>：本接口刻意采用<b>短延时</b>（数秒）而非立即断电，理由是
/// ① 日志得有机会落盘（"收到了关机指令"这一行是事后唯一能查到的证据）；
/// ② 用户会话里的程序能收到关机通知而不是被硬切。所以返回 true 的含义是
/// "命令已经被系统接受"，真正的断电发生在几秒之后。
/// </para>
/// </summary>
public interface IHostPowerController
{
    /// <summary>本平台是否支持电源控制。</summary>
    bool IsAvailable { get; }

    /// <summary>不可用的原因（可用时为空串）。会被打进启动日志 —— 让"关机指令没反应"能一眼定位到层。</summary>
    string AvailabilityNote { get; }

    /// <summary>发起关机。</summary>
    /// <param name="reason">原因文本（进日志 / 系统事件，便于事后追溯"是谁下的指令"）。</param>
    Task<HostPowerResult> ShutdownAsync(string? reason, CancellationToken cancellationToken = default);

    /// <summary>发起整机重启。</summary>
    /// <param name="reason">原因文本（进日志 / 系统事件）。</param>
    Task<HostPowerResult> RestartAsync(string? reason, CancellationToken cancellationToken = default);
}

/// <summary>
/// 电源控制的<b>不支持实现</b>。
/// <para>
/// 与遥测那几个 Noop 同一个思路：注册它而不是"不注册"，让上层一个 <c>if</c> 都不用写；
/// "本平台不支持"由 <see cref="IsAvailable"/> 与 <see cref="AvailabilityNote"/> 一路带到日志与页面上，
/// 而不是表现为一个 <c>NullReferenceException</c> 或者一次静默无操作。
/// </para>
/// </summary>
public sealed class UnsupportedHostPowerController : IHostPowerController
{
    private readonly string _note;

    public UnsupportedHostPowerController(string note) => _note = note;

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public string AvailabilityNote => _note;

    /// <inheritdoc />
    public Task<HostPowerResult> ShutdownAsync(string? reason, CancellationToken cancellationToken = default)
        => Task.FromResult(HostPowerResult.Failure($"本平台不支持电源控制：{_note}"));

    /// <inheritdoc />
    public Task<HostPowerResult> RestartAsync(string? reason, CancellationToken cancellationToken = default)
        => Task.FromResult(HostPowerResult.Failure($"本平台不支持电源控制：{_note}"));
}
