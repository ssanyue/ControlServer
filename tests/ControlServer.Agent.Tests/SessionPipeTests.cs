using System.Text.Json;
using ControlServer.Agent.Tasks;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using ControlServer.Platform.Abstractions;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// 会话侧命名管道（服务 ↔ 托盘）的端到端测试：<b>真的开一条 Windows 命名管道</b>跑完整来回。
///
/// <para>
/// 覆盖三类东西：
/// ① <b>协议</b>——单行 JSON、参数反序列化、坏输入的降级；
/// ② <b>动作分发</b>——音量落到 <c>IAudioController</c>、启动落到启动委托；
/// ③ <b>可用性与日志</b>——探测不留噪音、状态翻转只记一次、不可用时给得出原因。
/// </para>
/// </summary>
public sealed class SessionPipeTests
{
    // ═══════════════════════════════════════════════════════════
    // ① 协议：单行
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>这条用例钉住整个行分隔协议的前提</b>：请求里嵌的"启动参数"本身是一段
    /// <c>ControlJson</c> 序列化出来的<b>多行缩进</b> JSON；它作为字符串被套进外层 JSON 之后，
    /// 换行必须被转义成 <c>\n</c>（两个字符），外层因此仍然只有一行。
    /// <para>
    /// 反过来说：谁把 <c>SessionPipeProtocol.Json</c> 的 <c>WriteIndented</c> 打开，
    /// 这条用例立刻红 —— 而线上表现会是"参数莫名其妙丢了半个"，极难定位。
    /// </para>
    /// </summary>
    [Fact]
    public void Serialized_Request_Stays_On_A_Single_Line_Even_With_Nested_Indented_Json()
    {
        var task = new AgentLaunchTaskDto("t1", "播放器", @"C:\demo\app.exe", "--fullscreen", 0, true);

        var nested = ControlJson.Serialize(task);

        nested.Should().Contain("\n", "ControlJson 是缩进格式，这条用例的价值正来自这个'坑'");

        var line = SessionPipeProtocol.Serialize(
            new SessionCommandRequest(SessionCommandMethods.Launch, nested));

        line.Should().NotContain("\n");
        line.Should().NotContain("\r");

        // 反过来也要能完整还原（包括那段多行 JSON）。
        var back = JsonSerializer.Deserialize<SessionCommandRequest>(line, SessionPipeProtocol.Json);

        back.Should().NotBeNull();
        back!.Method.Should().Be(SessionCommandMethods.Launch);

        var restored = JsonSerializer.Deserialize<AgentLaunchTaskDto>(back.Argument!, SessionPipeProtocol.Json);

        restored!.ProgramPath.Should().Be(@"C:\demo\app.exe");
        restored.Argument.Should().Be("--fullscreen");
    }

    [Fact]
    public void Pipe_Name_Is_Per_Session()
    {
        SessionPipeProtocol.PipeNameForSession(1).Should().NotBe(SessionPipeProtocol.PipeNameForSession(2));
        SessionPipeProtocol.PipeNameForSession(3).Should().Be(SessionPipeProtocol.PipeNamePrefix + "3");
    }

    /// <summary>
    /// <b>正式那套的管道名必须与老版一个字符都不差</b>（M7.4 §13）。
    /// <para>
    /// 🔥 这条守的是升级窗口：服务与托盘是**两个进程**，装机时先后被替换。
    /// 名字一旦变了，那几分钟里"服务是新版、托盘是旧版"就永远连不上，
    /// 而症状（会话侧一律不可用）与"托盘没起来"完全同形。
    /// </para>
    /// </summary>
    [Fact]
    public void Pipe_Name_Without_Scope_Is_Byte_Identical_To_The_Old_One()
    {
        SessionPipeProtocol.PipeNameForSession(1, null)
            .Should().Be(SessionPipeProtocol.PipeNamePrefix + "1");

        SessionPipeProtocol.PipeNameForSession(1, "   ")
            .Should().Be(SessionPipeProtocol.PipeNamePrefix + "1",
                "空白 scope 等于没给 —— 否则第二套的配置里一个空格就把自己变成另一条链");
    }

    /// <summary>
    /// 同机两套客户端（正式 ＋ 验证）必须落在**两个**管道名上，否则后建的那套
    /// 连的是先建那套的管道，拿到的是 `Access is denied`（M7.4 §13）。
    /// </summary>
    [Fact]
    public void Pipe_Name_Carries_The_Scope_So_Two_Clients_Do_Not_Collide()
    {
        var official = SessionPipeProtocol.PipeNameForSession(1);
        var probe = SessionPipeProtocol.PipeNameForSession(1, "ControlServerFpsProbe");

        probe.Should().NotBe(official);
        probe.Should().Contain("ControlServerFpsProbe");
        probe.Should().EndWith(".1", "会话编号仍然是**最后一段**：它才是'敲哪个桌面'的判据");
    }

    /// <summary>
    /// scope 里的非法字符要收窄成 <c>_</c>：管道名不能带反斜杠（会被当成层级）。
    /// <para>
    /// ⚠️ 两侧（服务与托盘）用**同一个**函数算名字，所以这里只需要钉住"不会算出非法名字"。
    /// </para>
    /// </summary>
    [Fact]
    public void Pipe_Name_Sanitizes_The_Scope()
    {
        var name = SessionPipeProtocol.PipeNameForSession(2, @"a\b/c d");

        name.Should().StartWith(SessionPipeProtocol.PipeNamePrefix);
        name.Should().EndWith(".2");
        name.Substring(SessionPipeProtocol.PipeNamePrefix.Length).Should().NotContain("\\");
        name.Substring(SessionPipeProtocol.PipeNamePrefix.Length).Should().NotContain("/");
        name.Substring(SessionPipeProtocol.PipeNamePrefix.Length).Should().NotContain(" ");
    }

    // ═══════════════════════════════════════════════════════════
    // ② 动作分发
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Volume_Command_Reaches_The_Audio_Controller_And_Returns_Real_State()
    {
        await using var harness = new SessionPipeHarness();

        var result = await harness.Channel.SendAsync(
            new SessionCommandRequest(SessionCommandMethods.Volume, AgentVolumeActions.Up),
            CancellationToken.None);

        result.Ok.Should().BeTrue();

        // 会话侧回的是"操作之后读回来的真实状态"，而不是"我以为调成了什么"。
        result.Message.Should().Contain("50%");

        harness.Audio.Actions.Should().Equal(new[] { HostAudioAction.VolumeUp });
        harness.ServerLines.Should().Contain(line => line.Contains("已执行"));
    }

    [Theory]
    [InlineData(AgentVolumeActions.Up, HostAudioAction.VolumeUp)]
    [InlineData(AgentVolumeActions.Down, HostAudioAction.VolumeDown)]
    [InlineData(AgentVolumeActions.Mute, HostAudioAction.Mute)]
    [InlineData(AgentVolumeActions.Unmute, HostAudioAction.Unmute)]
    public async Task Volume_Actions_Map_One_To_One(string action, HostAudioAction expected)
    {
        await using var harness = new SessionPipeHarness();

        var result = await harness.Channel.SendAsync(
            new SessionCommandRequest(SessionCommandMethods.Volume, action),
            CancellationToken.None);

        result.Ok.Should().BeTrue();
        harness.Audio.Actions.Should().Equal(new[] { expected });
    }

    [Fact]
    public async Task Volume_With_Unknown_Action_Is_Rejected_Without_Touching_Audio()
    {
        await using var harness = new SessionPipeHarness();

        var result = await harness.Channel.SendAsync(
            new SessionCommandRequest(SessionCommandMethods.Volume, "louder"),
            CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("不合法");
        harness.Audio.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task Volume_Fails_With_The_Controller_Reason_When_Device_Is_Missing()
    {
        await using var harness = new SessionPipeHarness();

        harness.Audio.IsAvailable = false;
        harness.Audio.AvailabilityNote = "本机没有可用的音频输出设备。";

        var result = await harness.Channel.SendAsync(
            new SessionCommandRequest(SessionCommandMethods.Volume, AgentVolumeActions.Mute),
            CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("没有可用的音频输出设备");
        harness.Audio.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task Launch_Command_Arrives_As_A_Deserialized_Task()
    {
        await using var harness = new SessionPipeHarness();

        var task = new AgentLaunchTaskDto("t9", "播放器", @"C:\demo\player.exe", "--fullscreen", 3, true);

        var result = await harness.Channel.SendAsync(
            new SessionCommandRequest(SessionCommandMethods.Launch, ControlJson.Serialize(task)),
            CancellationToken.None);

        result.Ok.Should().BeTrue();

        harness.LaunchedTasks.Should().ContainSingle();
        harness.LaunchedTasks[0].ProgramPath.Should().Be(@"C:\demo\player.exe");
        harness.LaunchedTasks[0].Argument.Should().Be("--fullscreen");
        harness.LaunchedTasks[0].Name.Should().Be("播放器");
    }

    [Fact]
    public async Task Launch_Failure_From_The_Session_Side_Is_Reported_Back()
    {
        await using var harness = new SessionPipeHarness();

        harness.LaunchReply = _ => SessionCommandResult.Failure("启动失败：系统找不到指定的文件（系统错误 2）");

        var result = await harness.Channel.SendAsync(
            new SessionCommandRequest(
                SessionCommandMethods.Launch,
                ControlJson.Serialize(new AgentLaunchTaskDto("t", "x", @"C:\nope.exe"))),
            CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("系统错误 2");
        harness.ServerLines.Should().Contain(line => line.Contains("未执行"));
    }

    // ═══════════════════════════════════════════════════════════
    // ③ 坏输入
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Malformed_Json_Is_Rejected_Instead_Of_Killing_The_Listener()
    {
        await using var harness = new SessionPipeHarness();

        var response = await SessionPipeHarness.RawAsync(harness.PipeName, "{ this is not json");

        response.Should().NotBeNullOrWhiteSpace();

        var result = JsonSerializer.Deserialize<SessionCommandResult>(response!, SessionPipeProtocol.Json);

        result!.Ok.Should().BeFalse();
        result.Message.Should().Contain("合法 JSON");

        // 关键：坏请求之后管道还能继续干活（不是"处理一条错的就死"）。
        var after = await harness.Channel.SendAsync(
            new SessionCommandRequest(SessionCommandMethods.Volume, AgentVolumeActions.Down),
            CancellationToken.None);

        after.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Empty_Line_Is_Rejected()
    {
        await using var harness = new SessionPipeHarness();

        var response = await SessionPipeHarness.RawAsync(harness.PipeName, string.Empty);

        var result = JsonSerializer.Deserialize<SessionCommandResult>(response!, SessionPipeProtocol.Json);

        result!.Ok.Should().BeFalse();
        result.Message.Should().Contain("空");
    }

    [Fact]
    public async Task Unknown_Method_Is_Rejected()
    {
        await using var harness = new SessionPipeHarness();

        var response = await SessionPipeHarness.RawAsync(
            harness.PipeName,
            SessionPipeProtocol.Serialize(new SessionCommandRequest("format-c", null)));

        var result = JsonSerializer.Deserialize<SessionCommandResult>(response!, SessionPipeProtocol.Json);

        result!.Ok.Should().BeFalse();
        result.Message.Should().Contain("不认识");
    }

    // ═══════════════════════════════════════════════════════════
    // ④ 可用性探测与日志
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>探测必须不留噪音</b>：服务每几十秒探一次可用性，
    /// 如果托盘每次都记一行"有人连上但没说话"，托盘日志会被刷爆，
    /// 而真正出问题的那条（超时的那条）反而被埋掉。
    /// </summary>
    [Fact]
    public async Task Availability_Probe_Leaves_No_Log_Noise()
    {
        await using var harness = new SessionPipeHarness();

        harness.Channel.IsAvailable.Should().BeTrue();

        // 紧跟一条真指令：单实例管道是先进先出的，这条指令能完成就说明探测早已被处理完，
        // 从而让下面的"没有噪音"断言不依赖 sleep。
        var result = await harness.Channel.SendAsync(
            new SessionCommandRequest(SessionCommandMethods.Volume, AgentVolumeActions.Up),
            CancellationToken.None);

        result.Ok.Should().BeTrue();

        harness.ServerLines.Should().NotContain(line => line.Contains("没有发出任何内容"));
    }

    [Fact]
    public async Task Availability_Is_Reported_Once_While_It_Keeps_Being_Available()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var harness = new SessionPipeHarness();

        harness.Channel.IsAvailable.Should().BeTrue();
        harness.Channel.IsAvailable.Should().BeTrue();
        harness.Channel.IsAvailable.Should().BeTrue();

        harness.ChannelLines.Count(line => line.Contains("会话侧指令通道已就绪")).Should().Be(1);
        harness.ChannelLines.Count(line => line.Contains("已断开")).Should().Be(0);
    }

    [Fact]
    public async Task Losing_The_Tray_Is_Reported_Exactly_Once()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var harness = new SessionPipeHarness();

        int? current = harness.SessionId;
        var channel = harness.CreateChannel(() => current);

        channel.IsAvailable.Should().BeTrue();

        // 会话编号换到一个没有托盘的会话 = "用户注销 / 换了个人登录"。
        current = 999_999;

        channel.IsAvailable.Should().BeFalse();
        channel.IsAvailable.Should().BeFalse();

        harness.ChannelLines.Count(line => line.Contains("已断开")).Should().Be(1);
        harness.ChannelLines.Count(line => line.Contains("控制台会话已切换")).Should().Be(1);
    }

    [Fact]
    public async Task No_Interactive_Session_Produces_A_Specific_Reason()
    {
        await using var harness = new SessionPipeHarness();

        var channel = harness.CreateChannel(() => null);

        channel.IsAvailable.Should().BeFalse();

        var result = await channel.SendAsync(
            new SessionCommandRequest(SessionCommandMethods.Volume, AgentVolumeActions.Up),
            CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("交互式控制台会话");
    }

    [Fact]
    public async Task Missing_Tray_Fails_The_Send_Without_Throwing()
    {
        await using var harness = new SessionPipeHarness();

        var channel = harness.CreateChannel(() => 999_998);

        channel.IsAvailable.Should().BeFalse();

        var result = await channel.SendAsync(
            new SessionCommandRequest(SessionCommandMethods.Launch, "{}"),
            CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("通信失败");
    }

    [Fact]
    public async Task Unavailable_Channel_Reports_Itself_As_Structurally_Unsupported()
    {
        var channel = new UnavailableSessionCommandChannel("非 Windows 平台尚未提供托盘伴侣。");

        channel.IsSupported.Should().BeFalse();
        channel.IsAvailable.Should().BeFalse();

        var result = await channel.SendAsync(
            new SessionCommandRequest(SessionCommandMethods.Volume, AgentVolumeActions.Up),
            CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("托盘伴侣");
    }

    // ═══════════════════════════════════════════════════════════
    // ⑤ 启动程序
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Program_Launcher_Refuses_An_Empty_Path()
    {
        var result = ProgramLauncher.Launch(new AgentLaunchTaskDto("t", "空路径", "   "));

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("程序路径");
    }

    [Fact]
    public void Program_Launcher_Reports_Win32_Error_For_A_Missing_File()
    {
        // 不存在的路径：不该抛，而该把系统错误码一起带回来（排障时靠它区分"路径错"与"权限不够"）。
        var missing = Path.Combine(Path.GetTempPath(), $"cs-missing-{Guid.NewGuid():N}.exe");

        var result = ProgramLauncher.Launch(new AgentLaunchTaskDto("t", "不存在", missing));

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("启动失败");
    }

    [Fact]
    public void Stub_Controller_Honours_The_Contract_Defaults()
    {
        // 这条用例守护的是"假实现别偷偷改变契约"：Read 不该影响 Apply 的记录。
        var audio = new StubAudioController();

        audio.IsAvailable.Should().BeTrue();
        audio.Read().VolumePercent.Should().Be(40);
        audio.Actions.Should().BeEmpty();
    }
}
