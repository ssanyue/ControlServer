using System.Diagnostics;
using System.Globalization;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Linux;

/// <summary>
/// Linux 上的音量控制（M7.2 起）：<b>按顺序试三种后端</b>（PipeWire / PulseAudio / ALSA）。
///
/// <para>
/// 🔥 <b>为什么不能只选一种</b>：现代桌面（Ubuntu 22.04+ / Fedora）默认 PipeWire，
/// 但它<b>装着 <c>pactl</c> 兼容层</b>；老一点的用纯 PulseAudio；而工控机 / 最小安装
/// 上可能只有 ALSA（<c>amixer</c>）。只认一种的症状是"这台机器上音量调不动，日志说没有音量设备"，
/// 而实际上只是那个命令名不对。
/// </para>
///
/// <para>
/// 🔴 <b>M7.3 的关键修正：探测与执行都必须"站在登录用户那边"</b>。
/// 本类以前在构造函数里用<b>当前进程</b>的环境探测 —— 而它要跑在
/// <b>systemd 系统服务（root）</b>里，<c>XDG_RUNTIME_DIR</c> 不是任何用户的，
/// 于是三个后端一个都探不到（结论变成"这台机器没有音量设备"）；就算探到了，
/// 改的也是 root 的音频上下文（<b>退出码 0、用户桌面一格不动</b>）。现在改为：
/// </para>
/// <list type="number">
///   <item><b>延迟探测</b>：不在构造时探。开机自启的服务早于用户登录，
///         构造时那一眼必然是"没有会话"，而那个结论会被永久固化。</item>
///   <item><b>先定位会话再探</b>：见 <see cref="LinuxSessionLocator"/>。</item>
///   <item><b>以会话用户身份执行</b>：见 <see cref="LinuxSessionCommands"/>（<c>runuser</c> / <c>sudo -n</c>）。</item>
///   <item><b>失败短缓存</b>：失败 5 秒内不重探（挡住连点），成功 30 秒后重新确认
///         （用户注销 / 切换后不能还报"可用"）。</item>
/// </list>
///
/// <para>
/// 🔥 <b>与 Windows 实现的共同约定</b>（同一个端口就必须同语义）：
/// <b>绝不抛异常</b> / <b>读不到就是 null</b> / <b>操作后重新读回真实状态</b>
/// （而不是把"我想要的值"当成结果回出去）。
/// </para>
/// </summary>
public sealed class LinuxAudioController : IAudioController
{
    private const int CommandTimeoutMs = 3000;

    /// <summary>一格音量 = 5%（与 Windows 那边 <c>+</c> / <c>-</c> 的步长对齐）。</summary>
    private const int StepPercent = 5;

    /// <summary>探测失败后的重试间隔（用户可能刚登录 / 刚起音频服务）。</summary>
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>探测成功后的复检间隔（注销 / 切换用户后不能还报"可用"）。</summary>
    private static readonly TimeSpan SuccessRecheckDelay = TimeSpan.FromSeconds(30);

    private readonly Func<(LinuxLoginSession? Session, string Note)> _locate;
    private readonly Func<LinuxLoginSession, string, string, (int ExitCode, string Output)> _runAs;
    private readonly Func<string, string?> _findExecutable;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _log;

    private readonly object _gate = new();

    private Backend _backend = Backend.None;
    private string? _exe;
    private LinuxLoginSession? _session;
    private string _note = "尚未探测（第一次访问时才会确定这台机器上走哪条后端）。";
    private bool _probed;
    private DateTimeOffset _probeExpiresAt;
    private bool _loggedSelection;

    /// <summary>生产用构造：走真实的 <c>loginctl</c> 与真实进程。</summary>
    /// <param name="log">诊断出口（选中哪个后端 / 会话时写一行；<c>null</c> = 不写）。</param>
    public LinuxAudioController(Action<string>? log = null)
        : this(
            LinuxSessionLocator.Locate,
            RunAsSessionUser,
            () => DateTimeOffset.UtcNow,
            FindExecutable,
            log)
    {
    }

    /// <summary>单测用构造：把会话定位、命令执行、可执行文件查找都换成可控委托。</summary>
    internal LinuxAudioController(
        Func<(LinuxLoginSession? Session, string Note)> locate,
        Func<LinuxLoginSession, string, string, (int ExitCode, string Output)> runAs,
        Func<DateTimeOffset> clock,
        Func<string, string?> findExecutable,
        Action<string>? log = null)
    {
        _locate = locate;
        _runAs = runAs;
        _clock = clock;
        _findExecutable = findExecutable;
        _log = log;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠️ 这个属性<b>可能触发一次探测</b>（第一次访问，或缓存过期）。
    /// 让它可以 I/O 是刻意的：一个"未探测就先回答 false"的可用性标记，
    /// 正是现场"能力明明在、页面却说没有"的来源。
    /// </remarks>
    public bool IsAvailable
    {
        get
        {
            EnsureProbed();

            return _backend != Backend.None;
        }
    }

    /// <inheritdoc />
    public string AvailabilityNote
    {
        get
        {
            EnsureProbed();

            return _note;
        }
    }

    /// <summary>选中的后端（诊断用；名字带 Name 后缀是为了不与那个内部枚举同名）。</summary>
    public string BackendName
    {
        get
        {
            EnsureProbed();

            return _backend.ToString();
        }
    }

    /// <inheritdoc />
    public HostAudioState Read()
    {
        EnsureProbed();

        var session = _session;

        if (_backend == Backend.None || _exe is null || session is null) return HostAudioState.Unknown;

        return _backend switch
        {
            Backend.Wpctl => ReadWpctl(session),
            Backend.Pactl => ReadPactl(session),
            _ => ReadAmixer(session),
        };
    }

    /// <inheritdoc />
    public HostAudioResult Apply(HostAudioAction action)
    {
        EnsureProbed();

        var session = _session;

        if (_backend == Backend.None || _exe is null || session is null)
        {
            return HostAudioResult.Failure($"本机无法控制音量：{_note}");
        }

        var (ok, message) = _backend switch
        {
            Backend.Wpctl => ApplyWpctl(action, session),
            Backend.Pactl => ApplyPactl(action, session),
            _ => ApplyAmixer(action, session),
        };

        // 🔥 **操作后的状态一律重新读**：命令返回 0 只等于"它接受了参数"，
        //    真正的音量可能因为上限 / 独占 / 被别的程序盖住而没有变。
        return ok
            ? HostAudioResult.Success(message, Read())
            : HostAudioResult.Failure(message);
    }

    // ═══════════════════════════════════════════════════════════
    // 探测（延迟 ＋ 缓存）
    // ═══════════════════════════════════════════════════════════

    private void EnsureProbed()
    {
        lock (_gate)
        {
            if (_probed && _clock() < _probeExpiresAt) return;

            Probe();
        }
    }

    /// <summary>真正探一次：先定位会话，再按序试三个后端。<b>调用方持锁。</b></summary>
    private void Probe()
    {
        _probed = true;

        var (session, sessionNote) = _locate();

        if (session is null)
        {
            _backend = Backend.None;
            _exe = null;
            _session = null;
            _note = sessionNote;
            _probeExpiresAt = _clock() + FailureRetryDelay;

            return;
        }

        _session = session;

        var failures = new List<string>();

        // 顺序有意义：PipeWire 的 pactl 兼容层也会应答，所以 wpctl 优先。
        foreach (var (backend, name, probe) in new[]
                 {
                     (Backend.Wpctl, "wpctl", "get-volume @DEFAULT_AUDIO_SINK@"),
                     (Backend.Pactl, "pactl", "get-sink-volume @DEFAULT_SINK@"),
                     (Backend.Amixer, "amixer", "get Master"),
                 })
        {
            var exe = _findExecutable(name);

            if (exe is null)
            {
                // 🔥 与"命令在但没应答"分开记：前者是没装（去装 alsa-utils / pulseaudio-utils），
                //    后者是守护进程没起（去起 pipewire / pulseaudio）—— 两个完全不同的排查动作。
                failures.Add($"{name} 不在这台机器上（/usr/bin、/bin、/usr/local/bin 里都没有）");
                continue;
            }

            // ⚠️ 必须**跑一次读**才算可用：命令存在但守护进程没起来（pulse 没跑）时
            //    `pactl` 会返回非 0，而"命令在"这件事会让人以为它能用。
            var run = _runAs(session, exe, probe);

            if (run.ExitCode == 0)
            {
                _backend = backend;
                _exe = exe;
                _note = $"{DisplayName(backend)}，会话用户 {session.UserName}"
                    + $"（{session.Type}，会话 {session.SessionId}）";
                _probeExpiresAt = _clock() + SuccessRecheckDelay;

                if (!_loggedSelection)
                {
                    _loggedSelection = true;
                    _log?.Invoke($"音量后端：{_note}");
                }

                return;
            }

            failures.Add($"{name} 在，但读状态失败（退出码 {run.ExitCode}）："
                + $"{FirstLine(run.Output)}{CauseHint(run.ExitCode)}");
        }

        _backend = Backend.None;
        _exe = null;
        _note = $"图形会话 {session.UserName}（{session.Type}，会话 {session.SessionId}）已登录，"
            + "但三个音量后端都用不了 —— " + string.Join("；", failures);
        _probeExpiresAt = _clock() + FailureRetryDelay;
    }

    private static string DisplayName(Backend backend) => backend switch
    {
        Backend.Wpctl => "PipeWire（wpctl）",
        Backend.Pactl => "PulseAudio / pipewire-pulse（pactl）",
        Backend.Amixer => "ALSA（amixer）",
        _ => "（无）",
    };

    /// <summary>
    /// 只给一句<b>方向</b>，不猜具体原因（stderr 原文已经在同一行里）。
    /// 这里不会出现 127（那是 shell 的"命令不存在"，我们是直接 exec）。
    /// </summary>
    private static string CauseHint(int exitCode)
        => exitCode == 1 ? "（常见于音频守护进程没起，或该用户没有图形会话）" : string.Empty;

    // ═══════════════════════════════════════════════════════════
    // 各后端
    // ═══════════════════════════════════════════════════════════

    private HostAudioState ReadWpctl(LinuxLoginSession session)
    {
        var run = _runAs(session, _exe!, "get-volume @DEFAULT_AUDIO_SINK@");

        return run.ExitCode == 0 && TryParseWpctl(run.Output, out var percent, out var muted)
            ? new HostAudioState(percent, muted)
            : HostAudioState.Unknown;
    }

    private (bool Ok, string Message) ApplyWpctl(HostAudioAction action, LinuxLoginSession session)
    {
        var (args, message) = action switch
        {
            HostAudioAction.VolumeUp => ($"set-volume @DEFAULT_AUDIO_SINK@ {StepPercent}%+", $"音量 +{StepPercent}%"),
            HostAudioAction.VolumeDown => ($"set-volume @DEFAULT_AUDIO_SINK@ {StepPercent}%-", $"音量 -{StepPercent}%"),
            HostAudioAction.Mute => ("set-mute @DEFAULT_AUDIO_SINK@ 1", "已静音"),
            _ => ("set-mute @DEFAULT_AUDIO_SINK@ 0", "已取消静音"),
        };

        var run = _runAs(session, _exe!, args);

        return run.ExitCode == 0
            ? (true, message)
            : (false, $"wpctl 执行失败（退出码 {run.ExitCode}）：{FirstLine(run.Output)}");
    }

    private HostAudioState ReadPactl(LinuxLoginSession session)
    {
        var volume = _runAs(session, _exe!, "get-sink-volume @DEFAULT_SINK@");
        var mute = _runAs(session, _exe!, "get-sink-mute @DEFAULT_SINK@");

        int? percent = volume.ExitCode == 0 && TryParsePactlVolume(volume.Output, out var p) ? p : null;
        bool? muted = mute.ExitCode == 0 && TryParsePactlMute(mute.Output, out var m) ? m : null;

        return percent is null && muted is null ? HostAudioState.Unknown : new HostAudioState(percent, muted);
    }

    private (bool Ok, string Message) ApplyPactl(HostAudioAction action, LinuxLoginSession session)
    {
        var (args, message) = action switch
        {
            HostAudioAction.VolumeUp => ($"set-sink-volume @DEFAULT_SINK@ +{StepPercent}%", $"音量 +{StepPercent}%"),
            HostAudioAction.VolumeDown => ($"set-sink-volume @DEFAULT_SINK@ -{StepPercent}%", $"音量 -{StepPercent}%"),
            HostAudioAction.Mute => ("set-sink-mute @DEFAULT_SINK@ 1", "已静音"),
            _ => ("set-sink-mute @DEFAULT_SINK@ 0", "已取消静音"),
        };

        var run = _runAs(session, _exe!, args);

        return run.ExitCode == 0
            ? (true, message)
            : (false, $"pactl 执行失败（退出码 {run.ExitCode}）：{FirstLine(run.Output)}");
    }

    private HostAudioState ReadAmixer(LinuxLoginSession session)
    {
        var run = _runAs(session, _exe!, "get Master");

        return run.ExitCode == 0 && TryParseAmixer(run.Output, out var percent, out var muted)
            ? new HostAudioState(percent, muted)
            : HostAudioState.Unknown;
    }

    private (bool Ok, string Message) ApplyAmixer(HostAudioAction action, LinuxLoginSession session)
    {
        // ⚠️ amixer 用 `sset`（simple set）而不是 `set`：前者接受 "5%+" 这种相对写法。
        var (args, message) = action switch
        {
            HostAudioAction.VolumeUp => ($"sset Master {StepPercent}%+ unmute", $"音量 +{StepPercent}%"),
            HostAudioAction.VolumeDown => ($"sset Master {StepPercent}%- unmute", $"音量 -{StepPercent}%"),
            HostAudioAction.Mute => ("sset Master mute", "已静音"),
            _ => ("sset Master unmute", "已取消静音"),
        };

        var run = _runAs(session, _exe!, args);

        return run.ExitCode == 0
            ? (true, message)
            : (false, $"amixer 执行失败（退出码 {run.ExitCode}）：{FirstLine(run.Output)}");
    }

    // ═══════════════════════════════════════════════════════════
    // 解析（纯函数 —— 单测就盯着这几个）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <c>wpctl get-volume @DEFAULT_AUDIO_SINK@</c> 的输出：
    /// <c>Volume: 0.48</c> 或 <c>Volume: 0.48 [MUTED]</c>。
    /// <para>⚠️ 它是 **0~1 的小数**，不是百分数 —— 直接当百分数用会得到 0%。</para>
    /// </summary>
    internal static bool TryParseWpctl(string? output, out int percent, out bool muted)
    {
        percent = 0;
        muted = false;

        if (string.IsNullOrWhiteSpace(output)) return false;

        var index = output.IndexOf("Volume:", StringComparison.Ordinal);

        if (index < 0) return false;

        var rest = output[(index + "Volume:".Length)..];
        var token = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        if (token is null) return false;

        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var volume))
        {
            return false;
        }

        percent = (int)Math.Round(volume * 100, MidpointRounding.AwayFromZero);
        muted = output.Contains("[MUTED]", StringComparison.Ordinal);

        return true;
    }

    /// <summary>
    /// <c>pactl get-sink-volume @DEFAULT_SINK@</c> 的第一行：
    /// <c>Volume: front-left: 65536 / 100% / 0.00 dB,   front-right: ...</c>
    /// <para>⚠️ 多声道的百分比可能不同（左右平衡被拖过），这里取**第一个** —— 与 <c>pactl</c> 自己的显示顺序一致。</para>
    /// </summary>
    internal static bool TryParsePactlVolume(string? output, out int percent)
    {
        percent = 0;

        if (string.IsNullOrWhiteSpace(output)) return false;

        var marker = output.IndexOf('%');

        if (marker < 0) return false;

        // 从 '%' 往前找到那个数字（形如 " 100%"）。
        var start = marker;

        while (start > 0 && (char.IsDigit(output[start - 1]) || output[start - 1] == '.')) start--;

        var digits = output[start..marker];

        if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        percent = (int)Math.Round(value, MidpointRounding.AwayFromZero);

        return true;
    }

    /// <summary><c>pactl get-sink-mute</c> 的输出：<c>Mute: yes</c> / <c>Mute: no</c>。</summary>
    internal static bool TryParsePactlMute(string? output, out bool muted)
    {
        muted = false;

        if (string.IsNullOrWhiteSpace(output)) return false;

        var index = output.IndexOf("Mute:", StringComparison.Ordinal);

        if (index < 0) return false;

        var rest = output[(index + "Mute:".Length)..].Trim().ToLowerInvariant();

        if (rest.StartsWith("yes", StringComparison.Ordinal))
        {
            muted = true;
            return true;
        }

        if (rest.StartsWith("no", StringComparison.Ordinal)) return true;

        return false;
    }

    /// <summary>
    /// <c>amixer get Master</c> 的输出里有 <c>[75%] [on]</c> 或 <c>[75%] [off]</c>。
    /// <para>⚠️ 一条输出里通常有**两行**（左/右声道），取第一处即可（它们同步变）。</para>
    /// </summary>
    internal static bool TryParseAmixer(string? output, out int percent, out bool muted)
    {
        percent = 0;
        muted = false;

        if (string.IsNullOrWhiteSpace(output)) return false;

        var open = output.IndexOf('[');

        if (open < 0) return false;

        var close = output.IndexOf('%', open);

        if (close < 0) return false;

        var digits = output[(open + 1)..close];

        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        percent = value;

        // 静音判定：同一行里出现 [off]（amixer 用 off 表示 mute）。
        var lineEnd = output.IndexOf('\n', open);
        var scope = lineEnd < 0 ? output : output[..lineEnd];

        muted = scope.Contains("[off]", StringComparison.Ordinal);

        return true;
    }

    // ═══════════════════════════════════════════════════════════
    // 探测与执行
    // ═══════════════════════════════════════════════════════════

    private static string? FindExecutable(string name)
    {
        foreach (var dir in new[] { "/usr/bin", "/bin", "/usr/local/bin" })
        {
            var path = Path.Combine(dir, name);

            if (File.Exists(path)) return path;
        }

        return null;
    }

    /// <summary>以会话用户的身份执行一条命令（<c>runuser</c> / <c>sudo -n</c>，见 <see cref="LinuxSessionCommands"/>）。</summary>
    private static (int ExitCode, string Output) RunAsSessionUser(
        LinuxLoginSession session,
        string exe,
        string arguments)
    {
        var invocation = LinuxSessionCommands.Build(
            exe,
            arguments,
            session,
            LinuxSessionLocator.ReadCurrentUid(),
            File.Exists,
            out var note);

        if (invocation is null) return (-1, note);

        return Run(invocation.FileName, invocation.Arguments);
    }

    private static (int ExitCode, string Output) Run(string exe, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start()) return (-1, "进程没能启动。");

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

            if (!process.WaitForExit(CommandTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 杀不掉也只能放弃，超时这句话本身已经是结论。
                }

                return (-1, $"`{exe} {arguments}` 超过 {CommandTimeoutMs} 毫秒没有结束。");
            }

            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return string.IsNullOrWhiteSpace(line) ? "（没有输出）" : line.Trim();
    }

    private enum Backend
    {
        None = 0,
        Wpctl = 1,
        Pactl = 2,
        Amixer = 3,
    }
}
