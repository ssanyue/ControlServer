using System.Diagnostics;
using System.Globalization;

namespace ControlServer.Platform.Linux;

/// <summary>
/// 找"<b>当前该往哪个用户桌面上调音量</b>"（M7.3）。
///
/// <para>
/// 🔥 <b>为什么需要它</b>：被控端是 <c>systemd</c> 系统服务（root），而音频端点是
/// <b>用户会话</b>的东西。服务直接跑 <c>wpctl</c> 得到的是 root 的上下文 ——
/// 退出码 0，用户桌面上毫无变化。要动那台机器上的音量，必须
/// ①知道是哪个用户 ②以他的身份 ③带着他的 <c>XDG_RUNTIME_DIR</c> 去执行。
/// 本类负责 ①，<see cref="LinuxSessionCommands"/> 负责 ②③。
/// </para>
///
/// <para>
/// <b>选取规则</b>（全部写进日志，现场一眼能看出"它选的是谁"）：
/// </para>
/// <list type="number">
///   <item><c>loginctl show-session</c> 报 <c>Active=yes</c>、<c>Remote=no</c>、且
///         <c>Type</c> 是 <c>x11</c> / <c>wayland</c>；</item>
///   <item>多个命中 ⇒ 取 <b>SessionId 最大</b>的那个（<c>logind</c> 的会话号单调递增 ⇒ 最近登录）；</item>
///   <item>一个都没有 ⇒ <b>如实报"没有图形会话"</b>，<b>不去猜</b>（见下）。</item>
/// </list>
///
/// <para>
/// 🔴 <b>刻意不做"扫 /run/user 目录兜底"</b>：无人登录时就算某个 uid 下还留着
/// <c>pipewire-0</c>（用户级服务的 linger），那台机器上也<b>没有人能听见任何声音</b>。
/// 挑一个 uid 去调音量是"猜"，而猜错的表现是一次成功且毫无意义的操作 ——
/// 本仓的纪律是"未知一律 null"，不是"尽量给个值"。
/// </para>
///
/// <para>所有解析都是纯函数：<c>loginctl</c> 的输出格式稳定但分段多，单测直接喂文本。</para>
/// </summary>
public static class LinuxSessionLocator
{
    private const int CommandTimeoutMs = 3000;

    private static readonly string[] LoginctlCandidates =
        ["/usr/bin/loginctl", "/bin/loginctl", "/usr/sbin/loginctl", "/sbin/loginctl"];

    /// <summary>探测并选中一个会话。返回的 <c>Note</c> 是<b>结论</b>（成功也写清选了谁）。</summary>
    public static (LinuxLoginSession? Session, string Note) Locate()
        => Locate(RunLoginctl);

    /// <summary>
    /// 可注入的版本（单测用）：<paramref name="runLoginctl"/> 收到参数串，返回输出；
    /// 返回 <c>null</c> 表示 <c>loginctl</c> 本身不可用。
    /// </summary>
    internal static (LinuxLoginSession? Session, string Note) Locate(Func<string, string?> runLoginctl)
    {
        var list = runLoginctl("list-sessions --no-legend");

        if (list is null)
        {
            return (null, "loginctl 不可用（这台机器没有 systemd-logind），无法确定当前登录的图形会话。");
        }

        var sessions = ParseSessionList(list);

        if (sessions.Count == 0)
        {
            return (null, "这台机器当前没有任何登录会话（loginctl list-sessions 为空），音量无处可调。");
        }

        var picked = PickActiveGraphical(
            sessions,
            id => runLoginctl($"show-session {id} -p Active -p Remote -p Type"));

        if (picked is null)
        {
            return (null, $"这台机器当前没有图形会话登录（loginctl 里 {sessions.Count} 个会话，"
                + "没有一个同时满足 Active=yes、Remote=no、Type=x11/wayland），音量无处可调。");
        }

        return (picked, $"已选中会话 {picked.SessionId}：{picked.UserName}（{picked.Type}）");
    }

    // ═══════════════════════════════════════════════════════════
    // 解析（纯函数）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <c>loginctl list-sessions --no-legend</c> 的每一行：<c>SESSION UID USER SEAT TTY</c>。
    /// <para>
    /// ⚠️ 后两列可能是 <c>-</c>；也只保证前三列存在 —— 按"至少三列"解析，
    /// 不要把 SEAT/TTY 当成必填（裸机 / 容器里它们经常是空的）。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<(string SessionId, int Uid, string UserName)> ParseSessionList(string? output)
    {
        var result = new List<(string, int, string)>();

        if (string.IsNullOrWhiteSpace(output)) return result;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.Length == 0) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 3) continue;

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid))
            {
                continue;
            }

            result.Add((parts[0], uid, parts[2]));
        }

        return result;
    }

    /// <summary>
    /// <c>loginctl show-session &lt;id&gt; -p Active -p Remote -p Type</c> 的输出：
    /// <c>Active=yes</c> / <c>Remote=no</c> / <c>Type=wayland</c>（顺序不固定，可能夹着别的键）。
    /// </summary>
    internal static bool TryParseShowSession(string? output, out bool active, out bool remote, out string type)
    {
        active = false;
        remote = false;
        type = string.Empty;

        if (string.IsNullOrWhiteSpace(output)) return false;

        var sawActive = false;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.Length == 0) continue;

            var separator = line.IndexOf('=');

            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            switch (key)
            {
                case "Active":
                    active = string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
                    sawActive = true;
                    break;

                case "Remote":
                    remote = string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
                    break;

                case "Type":
                    type = value;
                    break;
            }
        }

        // 没有 Active 这一行 ⇒ 这条 show-session 的结果不能用来判断"活跃"，整条作废。
        return sawActive;
    }

    /// <summary>
    /// 从会话列表里挑出**最近登录的图形会话**。
    /// <para>
    /// 🔥 <b>取 SessionId 最大而不是列表第一个</b>：<c>logind</c> 的会话号单调递增，
    /// 而 <c>list-sessions</c> 的顺序不保证（多席位时尤其乱）。取第一个的后果是
    /// "机器上同时登录两个人时，音量调到了另一个人的桌面上"—— 一个不可能被单机复现的错。
    /// </para>
    /// </summary>
    internal static LinuxLoginSession? PickActiveGraphical(
        IReadOnlyList<(string SessionId, int Uid, string UserName)> sessions,
        Func<string, string?> showSession)
    {
        LinuxLoginSession? best = null;
        var bestId = int.MinValue;

        foreach (var (sessionId, uid, userName) in sessions)
        {
            if (!int.TryParse(sessionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            {
                numeric = int.MinValue;
            }

            var show = showSession(sessionId);

            if (show is null) continue;

            if (!TryParseShowSession(show, out var active, out var remote, out var type)) continue;

            if (!active || remote) continue;

            var candidate = new LinuxLoginSession(userName, uid, sessionId, type);

            if (!candidate.IsGraphical) continue;

            if (numeric <= bestId) continue;

            best = candidate;
            bestId = numeric;
        }

        return best;
    }

    /// <summary>
    /// <c>/proc/self/status</c> 里的 <c>Uid:</c> 行：<c>Uid:\t0\t0\t0\t0</c>（real/effective/saved/fs）。
    /// 取<b>第一个</b>（real uid）—— 与 <c>getuid()</c> 同义。
    /// </summary>
    internal static bool TryParseProcUid(string? statusText, out int uid)
    {
        uid = -1;

        if (string.IsNullOrWhiteSpace(statusText)) return false;

        foreach (var rawLine in statusText.Split('\n'))
        {
            var line = rawLine.Trim();

            if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;

            var rest = line["Uid:".Length..].Trim();
            var first = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            return first is not null
                && int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out uid);
        }

        return false;
    }

    /// <summary>当前进程的 real uid；读不到（非 Linux / 容器里没挂 procfs）给 <c>null</c>。</summary>
    internal static int? ReadCurrentUid()
    {
        try
        {
            return TryParseProcUid(File.ReadAllText("/proc/self/status"), out var uid) ? uid : null;
        }
        catch (Exception)
        {
            // 读不到就是读不到。上层会当成"不是会话用户"处理（多一层 runuser 而已，不会做错事）。
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 执行
    // ═══════════════════════════════════════════════════════════

    private static string? RunLoginctl(string arguments)
    {
        var exe = LoginctlCandidates.FirstOrDefault(File.Exists);

        if (exe is null) return null;

        var run = Run(exe, arguments);

        // ⚠️ 非 0 退出码不等于"没有 loginctl"：没有 session 时它可能回 0 加空输出。
        //    这里只在**拿不到任何输出**时才当成不可用，把"确实没有会话"留给调用方判断。
        return run.ExitCode == 0 ? run.Output : null;
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

            if (!process.Start()) return (-1, string.Empty);

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

            if (!process.WaitForExit(CommandTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 杀不掉也只能放弃；"超时"这句话本身已经是结论。
                }

                return (-1, string.Empty);
            }

            return (process.ExitCode, output);
        }
        catch (Exception)
        {
            return (-1, string.Empty);
        }
    }
}
