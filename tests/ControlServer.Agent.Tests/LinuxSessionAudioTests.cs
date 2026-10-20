using ControlServer.Platform.Abstractions;
using ControlServer.Platform.Linux;

namespace ControlServer.Agent.Tests;

/// <summary>
/// M7.3：<b>以登录用户身份调音量</b>这条链路上的纯逻辑。
///
/// <para>
/// 🔥 <b>为什么这一组值钱</b>：真机上的表现只有两种 —— "音量动了"或"没动"，
/// 而"没动"的原因有三层（没有图形会话 / 没有音频后端 / 命令没降权执行），
/// 现场从现象上<b>完全分不开</b>。这三层的判定全在这几个纯函数里，
/// 而它们恰好能在 Windows 开发机上跑。
/// </para>
///
/// <para>
/// ⚠️ 仍然<b>证明不了</b>的：<c>runuser</c> 到 PipeWire socket 的真实连通性、
/// SELinux/AppArmor 是否放行、多用户同时登录时的取舍。那些进 <c>docs/M7-真机验证清单.md</c>。
/// </para>
/// </summary>
public sealed class LinuxSessionAudioTests
{
    private static readonly LinuxLoginSession Alice = new("alice", 1000, "2", "wayland");

    // ═══════════════════════════════════════════════════════════
    // loginctl 输出解析
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Session_list_takes_the_first_three_columns()
    {
        const string output = "  1  1000 alice seat0 tty2\n  2  1001 bob   seat0 tty3\n";

        var sessions = LinuxSessionLocator.ParseSessionList(output);

        Assert.Equal(2, sessions.Count);
        Assert.Equal(("1", 1000, "alice"), sessions[0]);
        Assert.Equal(("2", 1001, "bob"), sessions[1]);
    }

    [Fact]
    public void Session_list_needs_at_least_three_columns()
    {
        // 后两列（SEAT / TTY）在裸机与容器里经常是 "-" 甚至不存在 —— 不能当必填。
        const string output = "garbage\n\n7 1002 carol\n8 notanumber dave seat0 tty1\n";

        var sessions = LinuxSessionLocator.ParseSessionList(output);

        Assert.Single(sessions);
        Assert.Equal(("7", 1002, "carol"), sessions[0]);
    }

    [Fact]
    public void Session_list_of_empty_output_is_empty()
    {
        Assert.Empty(LinuxSessionLocator.ParseSessionList(null));
        Assert.Empty(LinuxSessionLocator.ParseSessionList("   \n"));
    }

    [Fact]
    public void Show_session_reads_active_remote_and_type()
    {
        Assert.True(LinuxSessionLocator.TryParseShowSession(
            "Active=yes\nRemote=no\nType=wayland\n", out var active, out var remote, out var type));

        Assert.True(active);
        Assert.False(remote);
        Assert.Equal("wayland", type);
    }

    [Fact]
    public void Show_session_without_an_active_line_is_rejected()
    {
        // 没有 Active 这一行 ⇒ 这条记录不能用来判断"活跃"，整条作废（不许默认成 false）。
        Assert.False(LinuxSessionLocator.TryParseShowSession(
            "Remote=no\nType=x11\n", out _, out _, out _));
    }

    [Fact]
    public void Proc_status_uid_uses_the_real_uid()
    {
        const string status = "Name:\tcontrolserver\nUid:\t1000\t1000\t1000\t1000\nGid:\t1000\t1000\t1000\t1000\n";

        Assert.True(LinuxSessionLocator.TryParseProcUid(status, out var uid));
        Assert.Equal(1000, uid);
    }

    // ═══════════════════════════════════════════════════════════
    // 会话选取
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void The_newest_graphical_session_wins_not_the_first_listed()
    {
        // 🔥 列表顺序反着给：logind 的会话号单调递增，取"第一个"会让音量跑到别人的桌面上。
        (string, int, string)[] sessions = [("5", 1001, "bob"), ("2", 1000, "alice")];

        var shows = new Dictionary<string, string>
        {
            ["2"] = "Active=yes\nRemote=no\nType=wayland",
            ["5"] = "Active=yes\nRemote=no\nType=x11",
        };

        var picked = LinuxSessionLocator.PickActiveGraphical(sessions, id => shows[id]);

        Assert.NotNull(picked);
        Assert.Equal("5", picked.SessionId);
        Assert.Equal("bob", picked.UserName);
    }

    [Fact]
    public void Inactive_remote_and_tty_sessions_are_all_skipped()
    {
        (string, int, string)[] sessions = [("1", 0, "root"), ("2", 1000, "alice"), ("3", 1001, "bob")];

        var shows = new Dictionary<string, string>
        {
            ["1"] = "Active=yes\nRemote=no\nType=tty",      // 不是图形会话
            ["2"] = "Active=no\nRemote=no\nType=wayland",   // 已切走
            ["3"] = "Active=yes\nRemote=yes\nType=x11",     // 远程会话：声音不在这台机器的扬声器上
        };

        Assert.Null(LinuxSessionLocator.PickActiveGraphical(sessions, id => shows[id]));
    }

    [Fact]
    public void A_session_whose_show_call_failed_does_not_break_the_scan()
    {
        (string, int, string)[] sessions = [("2", 1000, "alice"), ("5", 1001, "bob")];

        var shows = new Dictionary<string, string>
        {
            ["5"] = "Active=yes\nRemote=no\nType=wayland",
        };

        var picked = LinuxSessionLocator.PickActiveGraphical(sessions, id => shows.GetValueOrDefault(id));

        Assert.NotNull(picked);
        Assert.Equal("5", picked.SessionId);   // 2 号那条查不到明细 ⇒ 跳过它，但扫描继续
    }

    // ═══════════════════════════════════════════════════════════
    // 降权执行的前缀
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void No_prefix_when_the_service_already_runs_as_that_user()
    {
        var invocation = LinuxSessionCommands.Build(
            "/usr/bin/wpctl", "get-volume @DEFAULT_AUDIO_SINK@", Alice, 1000, _ => true, out var note);

        Assert.NotNull(invocation);
        Assert.Equal("/usr/bin/wpctl", invocation.FileName);
        Assert.Equal("get-volume @DEFAULT_AUDIO_SINK@", invocation.Arguments);
        Assert.Equal(string.Empty, note);
    }

    [Fact]
    public void Runuser_carries_the_runtime_dir()
    {
        var invocation = LinuxSessionCommands.Build(
            "/usr/bin/wpctl", "get-volume @DEFAULT_AUDIO_SINK@", Alice, 0,
            path => path == "/usr/sbin/runuser", out var note);

        Assert.NotNull(invocation);
        Assert.Equal("/usr/sbin/runuser", invocation.FileName);
        Assert.Equal(
            "-u alice -- env XDG_RUNTIME_DIR=/run/user/1000 /usr/bin/wpctl get-volume @DEFAULT_AUDIO_SINK@",
            invocation.Arguments);
        Assert.Equal(string.Empty, note);
    }

    [Fact]
    public void Sudo_fallback_is_non_interactive()
    {
        var invocation = LinuxSessionCommands.Build(
            "/usr/bin/wpctl", "get-volume @DEFAULT_AUDIO_SINK@", Alice, 0,
            path => path == "/usr/bin/sudo", out _);

        Assert.NotNull(invocation);
        Assert.Equal("/usr/bin/sudo", invocation.FileName);

        // 🔴 `-n` 不能省：没有它，sudo 会在无人值守的服务进程里等密码 ——
        //    表现是"点一下音量卡三秒然后超时"，而日志里只有一句"失败"。
        Assert.StartsWith("-n -u alice -- env XDG_RUNTIME_DIR=/run/user/1000 ", invocation.Arguments);
    }

    [Fact]
    public void No_switcher_at_all_is_a_failure_not_a_silent_root_execution()
    {
        var invocation = LinuxSessionCommands.Build(
            "/usr/bin/wpctl", "get-volume @DEFAULT_AUDIO_SINK@", Alice, 0, _ => false, out var note);

        // 降级成"直接跑"等于以 root 改 root 的音频上下文：退出码 0、用户桌面毫无动静。
        // 那比报错难查得多，所以这里必须构造失败。
        Assert.Null(invocation);
        Assert.Contains("runuser", note);
        Assert.Contains("alice", note);
    }

    [Fact]
    public void A_suspicious_login_name_is_refused()
    {
        var weird = new LinuxLoginSession("ali ce", 1000, "2", "wayland");

        var invocation = LinuxSessionCommands.Build(
            "/usr/bin/wpctl", "get-volume", weird, 0, _ => true, out var note);

        Assert.Null(invocation);
        Assert.Contains("不像合法的登录名", note);
    }

    // ═══════════════════════════════════════════════════════════
    // 控制器：三态 ＋ 缓存
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Without_a_graphical_session_no_command_is_executed()
    {
        var runs = 0;

        var controller = new LinuxAudioController(
            () => (null, "这台机器当前没有图形会话登录（loginctl 里没有 Active 的 x11/wayland 会话），音量无处可调。"),
            (_, _, _) =>
            {
                runs++;
                return (0, string.Empty);
            },
            () => DateTimeOffset.UnixEpoch,
            _ => "/usr/bin/wpctl");

        var result = controller.Apply(HostAudioAction.VolumeUp);

        Assert.False(result.Ok);
        Assert.Contains("没有图形会话", result.Message);
        Assert.Equal(0, runs);   // ← 关键：没有会话就不该去执行任何命令
    }

    [Fact]
    public void The_selected_backend_is_read_back_through_the_session_user()
    {
        var calls = new List<string>();

        var controller = new LinuxAudioController(
            () => (Alice, "已选中会话 2：alice（wayland）"),
            (session, exe, args) =>
            {
                calls.Add($"{session.UserName}|{exe}|{args}");

                return args.StartsWith("get-volume", StringComparison.Ordinal)
                    ? (0, "Volume: 0.48 [MUTED]\n")
                    : (0, string.Empty);
            },
            () => DateTimeOffset.UnixEpoch,
            name => name == "wpctl" ? "/usr/bin/wpctl" : null);

        var state = controller.Read();

        Assert.True(controller.IsAvailable);
        Assert.Equal(48, state.VolumePercent);
        Assert.True(state.Muted);

        // 每一条命令都必须带会话用户名：漏了就是"以 root 改 root 的音量"。
        Assert.All(calls, call => Assert.StartsWith("alice|/usr/bin/wpctl|", call));
    }

    [Fact]
    public void A_failed_probe_is_not_retried_within_five_seconds()
    {
        var probes = 0;
        var now = DateTimeOffset.UnixEpoch;

        var controller = new LinuxAudioController(
            () =>
            {
                probes++;
                return (null, "这台机器当前没有图形会话登录。");
            },
            (_, _, _) => (0, string.Empty),
            () => now,
            _ => "/usr/bin/wpctl");

        controller.Apply(HostAudioAction.VolumeUp);
        controller.Apply(HostAudioAction.VolumeDown);

        Assert.Equal(1, probes);   // 连点两下不该跑两遍 loginctl

        now = now.AddSeconds(6);
        controller.Apply(HostAudioAction.VolumeUp);

        Assert.Equal(2, probes);   // 过了重试窗口要重新探（用户可能刚登录）
    }

    [Fact]
    public void The_note_tells_no_session_apart_from_no_backend()
    {
        var noSession = new LinuxAudioController(
            () => (null, "这台机器当前没有图形会话登录（loginctl 未列出 x11/wayland 会话），音量无处可调。"),
            (_, _, _) => (0, string.Empty),
            () => DateTimeOffset.UnixEpoch,
            _ => "/usr/bin/wpctl");

        var noBackend = new LinuxAudioController(
            () => (Alice, "已选中会话 2：alice（wayland）"),
            (_, _, _) => (1, "Failed to connect to bus: No such file or directory"),
            () => DateTimeOffset.UnixEpoch,
            name => name == "wpctl" ? "/usr/bin/wpctl" : null);

        Assert.Contains("没有图形会话", noSession.AvailabilityNote);

        // 这一句必须同时说清三件事：会话在、后端全挂、每一条为什么挂 ——
        // 否则现场会去查账户（而账户没问题），真正该查的是音频守护进程。
        Assert.Contains("alice", noBackend.AvailabilityNote);
        Assert.Contains("三个音量后端都用不了", noBackend.AvailabilityNote);
        Assert.Contains("Failed to connect to bus", noBackend.AvailabilityNote);
        Assert.False(noBackend.IsAvailable);
    }
}
