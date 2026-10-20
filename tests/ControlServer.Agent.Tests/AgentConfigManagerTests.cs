using System.Net;
using System.Text;
using ControlServer.Agent.Config;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// 客户机那份<b>可写</b>配置（<see cref="AgentConfigManager"/>，2026-10-14 起的双向同步）的单元测试。
///
/// <para>
/// <b>为什么这些用例值得单独存在</b>：需求原话是"客户端本身可独立使用，在连接到服务端时
/// 会自动实时将配置同步给服务端"。这条链路上有<b>两种失败都是静默的</b>：
/// </para>
/// <list type="bullet">
///   <item><b>丢了「待同步」标志</b> ⇒ 本机照自己的配置跑、中心显示旧配置，<b>两边都不报错</b>。
///         它最容易在"客户机重启过"之后发生（标志只活在内存里），现场表现为
///         "这台机器网页上怎么改都不生效"—— 而机器其实一直在按用户在本机页面上写的那份跑。</item>
///   <item><b>没有"跳过中心那一档"的编号</b> ⇒ 本机刚改完、一连上中心就被中心那份<b>旧</b>配置盖掉，
///         用户看到的是"我改的东西自己变回去了"。</item>
/// </list>
///
/// <para>
/// 全部走<b>真</b>的 <see cref="AgentLocalConfigStore"/>（真落盘、真读回），
/// 只有 HTTP 那一层是桩：这里要验的是"状态怎么存、怎么判"，不是"HTTP 怎么发"
/// （后者由 <c>deploy/smoke</c> 与端到端脚本覆盖）。
/// </para>
/// </summary>
public sealed class AgentConfigManagerTests
{
    // ═══════════════════════════════════════════════════════════
    // 「待同步」必须跨重启活着
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 本组最要紧的一条：<b>离线改的那一版，要在进程重启之后仍然被认成"待推给中心"。</b>
    /// <para>
    /// 丢掉它的后果不是"晚一点同步"，而是<b>永远不会同步</b> —— 推送循环只认这个标志，
    /// 而为假时它一个字节都不会发。两边都不报错，所以只能靠这条用例钉住。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_local_edit_survives_a_restart_and_is_still_pending_sync()
    {
        await using var harness = new Harness();

        var first = harness.NewManager();

        var saved = first.ApplyLocalEdit(LocalEdit(udpPort: 8095), out var error);

        error.Should().BeNull();
        saved.Should().NotBeNull();
        saved!.Version.Should().Be(1, "本机起手是空的，第一次本机改动就是 v1");
        first.Origin.Should().Be(AgentConfigOrigins.Local);
        first.HasUnsyncedLocalEdit.Should().BeTrue();

        // ── 模拟"客户机服务重启"（离线期间被重启，或整机重启）──
        var restarted = harness.NewManager();

        restarted.RestoreFromStore().Should().BeTrue();
        restarted.AppliedVersion.Should().Be(1, "重启后要按本机那份继续跑（降级运行）");
        restarted.Origin.Should().Be(AgentConfigOrigins.Local);

        restarted.HasUnsyncedLocalEdit.Should().BeTrue(
            "离线改的那一版必须能跨重启活到下次连上中心 —— 否则它永远不会被推上去，且两边都不报错");
    }

    /// <summary>中心写进来的那份没有"待推"可言，重启后也不该冒出这个标志。</summary>
    [Fact]
    public async Task A_config_written_by_the_center_is_not_pending_and_survives_a_restart()
    {
        await using var harness = new Harness();

        var manager = harness.NewManager();

        manager.AcceptServerConfig(Config(version: 5, udpPort: 8097));

        manager.HasUnsyncedLocalEdit.Should().BeFalse();
        manager.Origin.Should().Be(AgentConfigOrigins.Server);

        var restarted = harness.NewManager();

        restarted.RestoreFromStore().Should().BeTrue();
        restarted.AppliedVersion.Should().Be(5);
        restarted.Origin.Should().Be(AgentConfigOrigins.Server);
        restarted.HasUnsyncedLocalEdit.Should().BeFalse("中心给的配置本来就在中心手上，谈不上待推");
    }

    /// <summary>
    /// 升级路径：这个字段出现<b>之前</b>写下的文档里没有它。
    /// 那时只能按"来源是本机"推断 —— 而这也正是当时唯一可能有未推改动的情形。
    /// </summary>
    [Theory]
    [InlineData(AgentConfigOrigins.Local, true)]
    [InlineData(AgentConfigOrigins.Server, false)]
    public async Task An_old_document_without_the_flag_infers_it_from_the_origin(string origin, bool expected)
    {
        await using var harness = new Harness();

        // 手写一份老格式文档：只有 config + origin，没有 hasUnsyncedLocalEdit。
        var raw = "{\"config\":" + ControlJson.Serialize(Config(1, 8090)) + ",\"origin\":\"" + origin + "\"}";

        File.WriteAllText(AgentPaths.LocalConfigFile(harness.StateDirectory), raw, Encoding.UTF8);

        var manager = harness.NewManager();

        manager.RestoreFromStore().Should().BeTrue("老格式文档要能被读出来（否则会退化成'本机没有配置'）");

        manager.HasUnsyncedLocalEdit.Should().Be(
            expected,
            "老文档缺这个字段时，按'来源是本机就算待同步'推断");
    }

    /// <summary>
    /// 「放弃同步」这个结论同样要跨重启保持。
    /// <para>
    /// 不落盘的话，重启后又从文件里读出一个"待同步"，把一份中心已经<b>明确回绝</b>过的配置
    /// 再推一遍 —— 而重试的结果不会有任何不同，只是白跑一趟网络与日志。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Abandoning_the_local_sync_is_persisted_so_a_restart_does_not_retry()
    {
        await using var harness = new Harness();

        var manager = harness.NewManager();

        manager.ApplyLocalEdit(LocalEdit(udpPort: 8095), out _);
        manager.AbandonLocalSync();

        manager.HasUnsyncedLocalEdit.Should().BeFalse();

        var restarted = harness.NewManager();

        restarted.RestoreFromStore().Should().BeTrue();

        restarted.HasUnsyncedLocalEdit.Should().BeFalse("'放弃同步'是个已经做出的结论，重启不该把它忘掉");
        restarted.Origin.Should().Be(
            AgentConfigOrigins.Local,
            "放弃同步只是'不再尝试推送'，并不改变'这份是本机写的'");
    }

    // ═══════════════════════════════════════════════════════════
    // 本机编辑：校验、编号、判据
    // ═══════════════════════════════════════════════════════════

    /// <summary>校验没过的改动根本不该被记成"待同步"，也不该推进版本号。</summary>
    [Fact]
    public async Task An_invalid_local_edit_is_rejected_and_leaves_nothing_pending()
    {
        await using var harness = new Harness();

        var manager = harness.NewManager();

        // 80 端口在允许范围（1024–65535）之外。
        var saved = manager.ApplyLocalEdit(LocalEdit(udpPort: 80), out var error);

        saved.Should().BeNull();
        error.Should().NotBeNull().And.Contain("1024");

        manager.HasUnsyncedLocalEdit.Should().BeFalse("校验没过的改动不该产生任何待同步状态");
        manager.AppliedVersion.Should().Be(0);
    }

    /// <summary>
    /// 🔥 本机改动的版本号必须<b>跳过中心那一档</b>（取 <c>max(本机, 已知中心) + 1</c>）。
    /// <para>
    /// 少了这一步，就会出现"全新机器上本机改了一版，一连上中心就被中心那份旧配置盖掉"——
    /// 用户视角是"我改的东西自己变回去了"。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_local_edit_is_numbered_above_the_centers_version()
    {
        await using var harness = new Harness();

        var manager = harness.NewManager();

        manager.AcceptServerConfig(Config(version: 5, udpPort: 8090));

        var saved = manager.ApplyLocalEdit(LocalEdit(udpPort: 8095), out var error);

        error.Should().BeNull();
        saved!.Version.Should().Be(6, "max(本机 5, 已知中心 5) + 1 —— 要压过中心那一档，不能被它盖掉");
        manager.HasUnsyncedLocalEdit.Should().BeTrue();
    }

    /// <summary>
    /// 「后写覆盖」的判据：本机有未推改动时，<b>只比时间戳</b>（此时两边的版本号不是同一个基准）。
    /// <list type="bullet">
    ///   <item>中心那份更早 ⇒ 保住本机那份（本机刚写的）；</item>
    ///   <item>中心那份更晚 ⇒ 本机当场改用中心那份，不留"两边各跑一份"的中间状态。</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task An_unsynced_local_edit_outlives_an_older_config_and_yields_to_a_newer_one()
    {
        await using var harness = new Harness();

        var manager = harness.NewManager();

        manager.ApplyLocalEdit(LocalEdit(udpPort: 8095), out _);

        // ① 中心那份是**三天前**保存的（v5）—— 本机刚写的那一版不能被它盖掉。
        harness.ServerConfig = Config(version: 5, udpPort: 8090, updatedAt: DateTimeOffset.Now.AddDays(-3));

        var kept = await manager.SyncAsync(
            "http://127.0.0.1:1", 1, "agent-key", knownVersion: 5, force: false, CancellationToken.None);

        kept.Applied.Should().BeFalse("中心那份更早 ⇒ 保住本机那份");
        manager.AppliedVersion.Should().Be(1);
        manager.Current!.Udp.Port.Should().Be(8095, "本机写的端口要留着");
        manager.HasUnsyncedLocalEdit.Should().BeTrue("本机那份还没推上去");

        // ② 中心那份是**一分钟后**保存的（v6）—— 后写覆盖，本机当场改用中心那份。
        harness.ServerConfig = Config(version: 6, udpPort: 8097, updatedAt: DateTimeOffset.Now.AddMinutes(1));

        var taken = await manager.SyncAsync(
            "http://127.0.0.1:1", 1, "agent-key", knownVersion: 6, force: false, CancellationToken.None);

        taken.Applied.Should().BeTrue();
        manager.AppliedVersion.Should().Be(6);
        manager.Origin.Should().Be(AgentConfigOrigins.Server);
        manager.HasUnsyncedLocalEdit.Should().BeFalse("中心那份已经追平/超过本机 ⇒ 不再有待推的改动");
        manager.Current!.Udp.Port.Should().Be(8097);
    }

    /// <summary>中心"暂无配置"（404）时本机保持现状，绝不把本机那份清掉。</summary>
    [Fact]
    public async Task A_missing_server_config_never_wipes_the_local_one()
    {
        await using var harness = new Harness();

        var manager = harness.NewManager();

        manager.ApplyLocalEdit(LocalEdit(udpPort: 8095), out _);

        harness.ServerConfig = null;

        var outcome = await manager.SyncAsync(
            "http://127.0.0.1:1", 1, "agent-key", knownVersion: null, force: false, CancellationToken.None);

        outcome.Applied.Should().BeFalse();
        manager.AppliedVersion.Should().Be(1, "服务端一次瞬时 404 绝不能变成'客户机上的任务全被清掉'");
        manager.Current!.Udp.Port.Should().Be(8095);
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    private static AgentConfigDto Config(int version, int udpPort, DateTimeOffset? updatedAt = null) => new(
        Version: version,
        UpdatedAt: updatedAt ?? DateTimeOffset.Now,
        Udp: new AgentUdpChannelDto(Enabled: true, Port: udpPort, BlockedSourceIps: null),
        SendTasks: Array.Empty<AgentSendTaskDto>(),
        ScheduledTasks: Array.Empty<AgentScheduledTaskDto>(),
        LaunchTasks: Array.Empty<AgentLaunchTaskDto>());

    private static AgentConfigSaveRequest LocalEdit(int udpPort) => new(
        Udp: new AgentUdpChannelDto(Enabled: true, Port: udpPort, BlockedSourceIps: null),
        SendTasks: new[]
        {
            new AgentSendTaskDto(
                Id: "s1",
                Protocol: "UDP",
                TargetIp: "192.168.1.100",
                TargetPort: 9000,
                DataType: "String",
                Data: "hello",
                DelaySeconds: 3,
                IsLoop: true,
                LoopIntervalSeconds: 30,
                Name: "开机报数",
                Enabled: true),
        });

    /// <summary>
    /// 被测对象的全套依赖：<b>真</b>状态目录 + <b>真</b>落盘 + 桩 HTTP。
    /// <para><see cref="NewManager"/> 每次都重建管理器（并重新读同一份状态目录），
    /// 用它模拟"客户机服务重启"。</para>
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TestDataDirectory _data;

        public Harness()
        {
            _data = new TestDataDirectory(
                Path.Combine(Path.GetTempPath(), "cs-agent-config-tests", Guid.NewGuid().ToString("N")));

            StateDirectory = AgentPaths.EnsureStateDirectory(_data.Root);

            // flushIntervalMs 设得很大：这些用例只关心配置，不需要日志落盘。
            Log = new LogService(_data, capacity: 1000, flushIntervalMs: 600_000);

            Options = new AgentOptions { StateDirectory = StateDirectory };

            var http = new HttpClient(new StubHandler(_ => Respond()));

            Client = new AgentConfigClient(http, Log);
        }

        public string StateDirectory { get; }

        public LogService Log { get; }

        public AgentOptions Options { get; }

        public AgentConfigClient Client { get; }

        /// <summary>桩服务端此刻会返回的配置；<c>null</c> ⇒ 404「暂无配置」。测试中途可改。</summary>
        public AgentConfigDto? ServerConfig { get; set; }

        public AgentConfigManager NewManager()
            => new(Client, new AgentLocalConfigStore(StateDirectory, Log), Options, Log);

        private HttpResponseMessage Respond()
            => ServerConfig is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        ControlJson.Serialize(ServerConfig), Encoding.UTF8, "application/json"),
                };

        public async ValueTask DisposeAsync()
        {
            await Log.DisposeAsync();

            try
            {
                if (Directory.Exists(_data.Root)) Directory.Delete(_data.Root, recursive: true);
            }
            catch
            {
                // 测试清理失败不影响结论。
            }
        }
    }

    /// <summary>把所有请求都交给一个委托处理的桩 <see cref="HttpMessageHandler"/>（不碰真网络）。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
