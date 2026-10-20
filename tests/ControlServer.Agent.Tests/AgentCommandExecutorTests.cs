using ControlServer.Agent;
using ControlServer.Agent.Config;
using ControlServer.Agent.Diagnostics;
using ControlServer.Agent.Tasks;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using ControlServer.Platform.Abstractions;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// <b>指令执行器</b>（2026-10-15）的测试。
///
/// <para>
/// 它存在的理由是一起真实的故障：中心页面上点「关机 / 重启」**毫无效果**，
/// 而 UDP 明文指令一直正常。根因是"关机"的实现只长在 <c>UdpCommandListener</c> 里，
/// 中心那条路（SignalR）到了客户端<b>没有任何人处理</b>。
/// 所以这里断言的是"两条通道共用的这一层"本身，而不是某一侧的接线。
/// </para>
///
/// <para>
/// ⚠️ <b>绝不真的关机</b>：电源与会话通道都是桩。
/// 桩同时也是断言手段 —— 记到桩上的调用次数就是"这条指令到底被执行了没有"的唯一事实。
/// </para>
/// </summary>
public sealed class AgentCommandExecutorTests : IAsyncDisposable
{
    private readonly TestDataDirectory _dir = new(
        Path.Combine(Path.GetTempPath(), "cs-command-executor-tests", Guid.NewGuid().ToString("N")));

    private readonly LogService _log;

    public AgentCommandExecutorTests()
    {
        // flushIntervalMs 设得很大：这些用例不关心落盘，只关心内存里的日志。
        _log = new LogService(_dir, capacity: 200, flushIntervalMs: 600_000);
    }

    public async ValueTask DisposeAsync()
    {
        await _log.DisposeAsync().ConfigureAwait(false);

        try
        {
            Directory.Delete(_dir.Root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不该让测试变红（Windows 上偶尔有句柄没释放）。
        }
    }

    private AgentCommandExecutor Create(bool executeEnabled = true, IHostPowerController? power = null,
        ISessionCommandChannel? session = null)
    {
        var options = new AgentOptions
        {
            Tasks = new AgentTaskOptions { Enabled = true, ExecuteUdpCommands = executeEnabled },
        };

        return new AgentCommandExecutor(
            options,
            _log,
            session ?? new StubSession(),
            power ?? new StubPower());
    }

    // ═══════════════════════════════════════════════════════════
    // 电源（服务侧）
    // ═══════════════════════════════════════════════════════════

    /// <summary>「关机」必须真的落到电源控制器，且结论是"已执行"而不是"已收到"。</summary>
    [Fact]
    public async Task Power_shutdown_really_calls_the_host_power_controller()
    {
        var power = new StubPower();

        var outcome = await Create(power: power)
            .ExecuteAsync(AgentCommandNames.Power, AgentPowerActions.Shutdown, "中心服务端下发的指令", "Agent");

        power.ShutdownCount.Should().Be(1);
        power.RestartCount.Should().Be(0);

        outcome.Outcome.Should().Be(AgentReceivedOutcomes.Executed);
        outcome.Detail.Should().Contain("关机");
    }

    /// <summary>
    /// 「重启」走的是另一条命令。两者混用的现场表现是
    /// "运维想重启一下客户端服务，结果把用户的机器关了"，所以分开断言。
    /// </summary>
    [Fact]
    public async Task Power_restart_really_calls_the_restart_path()
    {
        var power = new StubPower();

        var outcome = await Create(power: power)
            .ExecuteAsync(AgentCommandNames.Power, AgentPowerActions.Restart, "中心服务端下发的指令", "Agent");

        power.RestartCount.Should().Be(1);
        power.ShutdownCount.Should().Be(0);

        outcome.Outcome.Should().Be(AgentReceivedOutcomes.Executed);
    }

    /// <summary>参数不合法时**不动手**，且结论要写明原因（不能静默）。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("halt")]
    public async Task An_invalid_power_argument_does_nothing_and_says_why(string? action)
    {
        var power = new StubPower();

        var outcome = await Create(power: power)
            .ExecuteAsync(AgentCommandNames.Power, action, "中心服务端下发的指令", "Agent");

        power.ShutdownCount.Should().Be(0);
        power.RestartCount.Should().Be(0);

        outcome.Outcome.Should().Be(AgentReceivedOutcomes.Ignored);
        outcome.Detail.Should().Contain("不合法");
    }

    /// <summary>
    /// 电源控制器说失败（权限不足 / 平台不支持）时，结论必须是 Failed 并带上原因 ——
    /// 现场"点了没反应"里有一整类是这一层在失败。
    /// </summary>
    [Fact]
    public async Task A_failed_power_action_is_reported_as_failed_with_the_reason()
    {
        var outcome = await Create(power: new StubPower { FailWith = "权限不足（测试桩）" })
            .ExecuteAsync(AgentCommandNames.Power, AgentPowerActions.Shutdown, "中心服务端下发的指令", "Agent");

        outcome.Outcome.Should().Be(AgentReceivedOutcomes.Failed);
        outcome.Detail.Should().Contain("权限不足");
    }

    // ═══════════════════════════════════════════════════════════
    // 音量（会话侧）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 音量必须转给<b>会话侧</b>，服务侧一个音节都发不出来（Session 0 没有音频端点）。
    /// 所以断言的是"会话通道收到了请求"，同时"电源一次都没被碰"。
    /// </summary>
    [Fact]
    public async Task Volume_goes_to_the_session_side_and_never_to_the_service()
    {
        var session = new StubSession();
        var power = new StubPower();

        var outcome = await Create(power: power, session: session)
            .ExecuteAsync(AgentCommandNames.Volume, AgentVolumeActions.Up, "中心服务端下发的指令", "Agent");

        session.Requests.Should().ContainSingle(r =>
            r.Method == SessionCommandMethods.Volume && r.Argument == AgentVolumeActions.Up);

        power.ShutdownCount.Should().Be(0);
        power.RestartCount.Should().Be(0);

        outcome.Outcome.Should().Be(AgentReceivedOutcomes.Executed);

        // 成功时带上会话侧读回的**真实状态**，而不是一句"指令已发出"。
        outcome.Detail.Should().Contain("音量");
    }

    /// <summary>
    /// 会话侧不在（托盘没跑 / 没人登录）是"音量调不动"最常见的原因 ——
    /// 那句原因必须原样带到结论里。
    /// </summary>
    [Fact]
    public async Task A_failed_volume_action_carries_the_session_side_reason()
    {
        var outcome = await Create(session: new StubSession { FailWith = "托盘不在（测试桩）" })
            .ExecuteAsync(AgentCommandNames.Volume, AgentVolumeActions.Up, "中心服务端下发的指令", "Agent");

        outcome.Outcome.Should().Be(AgentReceivedOutcomes.Failed);
        outcome.Detail.Should().Contain("托盘不在");
    }

    // ═══════════════════════════════════════════════════════════
    // 演练模式：两条通道共用的一道闸
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 本轮的决策点：演练模式<b>不只是 UDP 的开关</b>。
    /// 安全闸不能留漏勺 —— 现场开它是为了"先观察、确认没有误触发再放行"，
    /// 若中心的关机指令能绕过它，那它就只是一个标签。
    /// </summary>
    [Theory]
    [InlineData(AgentCommandNames.Power, AgentPowerActions.Shutdown)]
    [InlineData(AgentCommandNames.Volume, AgentVolumeActions.Up)]
    public async Task Rehearsal_mode_blocks_the_centre_too_and_says_why(string name, string argument)
    {
        var power = new StubPower();
        var session = new StubSession();

        var outcome = await Create(executeEnabled: false, power: power, session: session)
            .ExecuteAsync(name, argument, "中心服务端下发的指令", "Agent");

        power.ShutdownCount.Should().Be(0);
        power.RestartCount.Should().Be(0);
        session.Requests.Should().BeEmpty();

        // ⚠️ 被拦下也**必须留下痕迹并说清原因**："点了没反应"里有一整类其实是被这里挡了。
        outcome.Outcome.Should().Be(AgentReceivedOutcomes.Ignored);
        outcome.Detail.Should().Contain("演练模式");
    }

    // ═══════════════════════════════════════════════════════════
    // 边界
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 「重启 Agent 服务」不是本执行器的活（只有宿主进程能重启自己），
    /// 它应当被明确地说成"本类不处理"，而不是被当成电源操作。
    /// </summary>
    [Fact]
    public async Task Restarting_the_agent_service_is_not_this_executors_job()
    {
        var power = new StubPower();

        var outcome = await Create(power: power)
            .ExecuteAsync(AgentCommandNames.Restart, null, "中心服务端下发的指令", "Agent");

        power.ShutdownCount.Should().Be(0);
        power.RestartCount.Should().Be(0);

        outcome.Outcome.Should().Be(AgentReceivedOutcomes.Ignored);
        outcome.Detail.Should().Contain(AgentCommandNames.Restart);
    }

    /// <summary>
    /// 底层抛异常时**不能把异常抛回调用方**（那是 SignalR 回调与 UDP 收包循环，
    /// 抛出去的后果是整条通道静默死掉），要转成一条 Failed 结论。
    /// </summary>
    [Fact]
    public async Task An_exception_becomes_a_failed_outcome_instead_of_crashing_the_channel()
    {
        var outcome = await Create(power: new ThrowingPower())
            .ExecuteAsync(AgentCommandNames.Power, AgentPowerActions.Shutdown, "中心服务端下发的指令", "Agent");

        outcome.Outcome.Should().Be(AgentReceivedOutcomes.Failed);
        outcome.Detail.Should().Contain("炸了（测试桩）");
    }

    // ═══════════════════════════════════════════════════════════
    // 桩
    // ═══════════════════════════════════════════════════════════

    private sealed class StubPower : IHostPowerController
    {
        private int _shutdowns;
        private int _restarts;

        /// <summary>给一个失败原因时，所有电源操作都失败（模拟权限不足 / 平台不支持）。</summary>
        public string? FailWith { get; init; }

        public int ShutdownCount => Volatile.Read(ref _shutdowns);

        public int RestartCount => Volatile.Read(ref _restarts);

        public bool IsAvailable => true;

        public string AvailabilityNote => string.Empty;

        public Task<HostPowerResult> ShutdownAsync(string? reason, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _shutdowns);

            return Task.FromResult(FailWith is null
                ? HostPowerResult.Success("已安排关机（测试桩）")
                : HostPowerResult.Failure(FailWith));
        }

        public Task<HostPowerResult> RestartAsync(string? reason, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _restarts);

            return Task.FromResult(FailWith is null
                ? HostPowerResult.Success("已安排重启（测试桩）")
                : HostPowerResult.Failure(FailWith));
        }
    }

    private sealed class ThrowingPower : IHostPowerController
    {
        public bool IsAvailable => true;

        public string AvailabilityNote => string.Empty;

        public Task<HostPowerResult> ShutdownAsync(string? reason, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("炸了（测试桩）");

        public Task<HostPowerResult> RestartAsync(string? reason, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("炸了（测试桩）");
    }

    private sealed class StubSession : ISessionCommandChannel
    {
        private readonly List<SessionCommandRequest> _requests = new();

        /// <summary>给一个失败原因时，所有会话侧指令都失败（模拟托盘不在 / 没人登录）。</summary>
        public string? FailWith { get; init; }

        public IReadOnlyList<SessionCommandRequest> Requests
        {
            get { lock (_requests) return _requests.ToArray(); }
        }

        public bool IsSupported => true;

        public bool IsAvailable => true;

        public Task<SessionCommandResult> SendAsync(
            SessionCommandRequest request,
            CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(request);

            return Task.FromResult(FailWith is null
                ? SessionCommandResult.Success("音量已调高：50%（未静音，测试桩）")
                : SessionCommandResult.Failure(FailWith));
        }
    }
}
