namespace ControlServer.Platform.Linux;

/// <summary>
/// 一个<b>已登录</b>的 Linux 会话（<c>loginctl</c> 的一条记录）。
///
/// <para>
/// 🔥 <b>为什么音量一定要知道"是哪个会话"</b>：<c>wpctl</c> / <c>pactl</c> 找音频端点
/// 靠的是 <c>$XDG_RUNTIME_DIR</c> 下的 socket（<c>pipewire-0</c> / <c>pulse/native</c>）。
/// 服务进程是 root，它自己的 <c>XDG_RUNTIME_DIR</c> 不是任何用户的 ——
/// 直接跑命令的结果是"退出码 0、用户桌面上音量一格不动"
/// （见 <see cref="ControlServer.Platform.Abstractions.IAudioController"/> 的接口注释）。
/// </para>
/// </summary>
/// <param name="UserName">登录名（<c>loginctl</c> 的 USER 列）。用于 <c>runuser -u</c>。</param>
/// <param name="Uid">用户 id（USER 列旁边的 UID）。用于判断"当前进程是不是已经是他"。</param>
/// <param name="SessionId">会话号（<c>loginctl</c> 的 SESSION 列）。号大者登录更晚。</param>
/// <param name="Type">会话类型：<c>x11</c> / <c>wayland</c> / <c>tty</c> / …</param>
public sealed record LinuxLoginSession(string UserName, int Uid, string SessionId, string Type)
{
    /// <summary>该用户的运行时目录。PipeWire 与 PulseAudio 的 socket 都在这里。</summary>
    public string RuntimeDir => $"/run/user/{Uid}";

    /// <summary>是不是图形会话（只有图形会话上才有"用户桌面上的声音"这回事）。</summary>
    public bool IsGraphical =>
        string.Equals(Type, "x11", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Type, "wayland", StringComparison.OrdinalIgnoreCase);
}

/// <summary>一条要执行的外部命令（可执行文件 ＋ 参数串）。</summary>
/// <param name="FileName">可执行文件的绝对路径。</param>
/// <param name="Arguments">参数串（不含程序名本身）。</param>
public sealed record LinuxInvocation(string FileName, string Arguments);

/// <summary>
/// 构造"<b>以某个登录用户的身份</b>执行"的命令行（M7.3）。
///
/// <para>
/// 三条路，按可靠性排序（<b>全部是纯函数，单测直接断言字符串</b> —— 这里错一位的表现是
/// "命令返回 0 但什么也没发生"，不可能在 Windows 上靠肉眼发现）：
/// </para>
/// <list type="number">
///   <item>当前进程已经是那个用户 ⇒ <b>不加任何前缀</b>（"Agent 以用户身份跑"的部署）；</item>
///   <item><c>runuser</c> 存在 ⇒ <c>runuser -u &lt;user&gt; -- env XDG_RUNTIME_DIR=… &lt;cmd&gt;</c>；
///         util-linux 自带、**只允许 root 使用**、不依赖 sudoers 配置。</item>
///   <item><c>sudo</c> 存在 ⇒ <c>sudo -n -u &lt;user&gt; -- env …</c>。
///         🔴 <c>-n</c> 不能省：没有它，sudo 会在无人值守的服务进程里<b>等密码</b>，
///         表现是"点一下音量卡三秒然后超时"。</item>
/// </list>
///
/// <para>
/// 🔥 <b><c>XDG_RUNTIME_DIR</c> 必须显式设</b>：<c>runuser</c> 只换 uid，不换环境 ——
/// 它继承的是 root 那一套，而 <c>wpctl</c> / <c>pactl</c> 正是靠这个变量找 socket。
/// 少了它，命令会以一个"连不上守护进程"的错误退出，看起来像"这台机器没有音频"。
/// </para>
/// </summary>
public static class LinuxSessionCommands
{
    /// <summary><c>runuser</c> 的候选路径（发行版之间 /sbin 与 /usr/sbin 不一致）。</summary>
    internal static readonly string[] RunUserCandidates =
        ["/usr/sbin/runuser", "/sbin/runuser", "/usr/bin/runuser", "/bin/runuser"];

    /// <summary><c>sudo</c> 的候选路径。</summary>
    internal static readonly string[] SudoCandidates = ["/usr/bin/sudo", "/bin/sudo"];

    /// <summary>
    /// 构造命令行。<b>构造不出来时返回 <c>null</c> 并给出原因</b>（不猜、不降级成"直接跑"——
    /// 直接跑等于以 root 改 root 的音量，用户桌面上毫无动静，比报错难查得多）。
    /// </summary>
    /// <param name="exe">要执行的程序（<c>wpctl</c> / <c>pactl</c> / <c>amixer</c> 的绝对路径）。</param>
    /// <param name="arguments">参数串。</param>
    /// <param name="session">目标会话。</param>
    /// <param name="currentUid">当前进程的 uid；<c>null</c> = 读不到（当成"不是他"处理）。</param>
    /// <param name="exists">文件是否存在（测试注入）。</param>
    internal static LinuxInvocation? Build(
        string exe,
        string arguments,
        LinuxLoginSession session,
        int? currentUid,
        Func<string, bool> exists,
        out string note)
    {
        note = string.Empty;

        // ⚠️ 登录名要拼进命令行，先确认它"长得像个登录名"。
        //    这不是洁癖：一个从 loginctl 输出里带空格的用户名会让参数串整体错位，
        //    而错位的结果是 runuser 报"用户不存在"——现场会去查账户，查的是不存在的问题。
        if (!IsSafeLoginName(session.UserName))
        {
            note = $"会话用户名「{session.UserName}」不像合法的登录名，拒绝拼进命令行。";
            return null;
        }

        if (currentUid == session.Uid)
        {
            // 已经是这个用户在跑（例如 Agent 被装成 user 服务）：不需要任何切换。
            return new LinuxInvocation(exe, arguments);
        }

        var env = $"env XDG_RUNTIME_DIR={session.RuntimeDir}";

        if (FirstExisting(RunUserCandidates, exists) is { } runUser)
        {
            return new LinuxInvocation(
                runUser,
                $"-u {session.UserName} -- {env} {exe} {arguments}");
        }

        if (FirstExisting(SudoCandidates, exists) is { } sudo)
        {
            return new LinuxInvocation(
                sudo,
                $"-n -u {session.UserName} -- {env} {exe} {arguments}");
        }

        note = "找不到 runuser 与 sudo，无法以会话用户"
            + $"「{session.UserName}」的身份执行命令（服务是 root，直接执行只会改到 root 的音频上下文）。";

        return null;
    }

    /// <summary>登录名白名单：字母/下划线开头，后面可以是字母数字与 <c>._-</c>。</summary>
    internal static bool IsSafeLoginName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        if (!char.IsAsciiLetter(name[0]) && name[0] != '_') return false;

        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') continue;

            return false;
        }

        return true;
    }

    private static string? FirstExisting(string[] candidates, Func<string, bool> exists)
    {
        foreach (var path in candidates)
        {
            if (exists(path)) return path;
        }

        return null;
    }
}
