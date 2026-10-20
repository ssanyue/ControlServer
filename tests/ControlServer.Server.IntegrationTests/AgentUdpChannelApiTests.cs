using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using ControlServer.Contracts;
using ControlServer.Server.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// UDP 指令通道的「**以客户机实际状态为准**」判定（2026-10-14）。
///
/// <para>
/// 这组用例守的是一件事：<b>中心能不能发 UDP 指令，必须以客户机自己报上来的状态为准，
/// 而不是以"我下发过的配置里写着 Enabled=true"为准</b>。
/// </para>
///
/// <para>在改动之前，判定链是"配置说了算"，于是有两类固定的误判：</para>
/// <list type="number">
///   <item><b>假阴性</b>：客户机因为"中心从未保存过配置"而一直用默认值在听（8090），
///         中心却因为查不到配置记录直接拒绝代发 —— 明明能发，它说不能；</item>
///   <item><b>假阳性</b>：配置里写着启用，但客户机那侧因为本机停用 / 端口被占 / 还没应用
///         而压根没绑端口，中心照样"已发出" —— 明明不能发，它说能。
///         这正是现场"点了没反应，两边都没错"的根源。</item>
/// </list>
///
/// <para>
/// ⚠️ 这些用例真发 UDP 到<b>本机回环</b>上的一个真实 socket 并断言收到报文 ——
/// 因为"报文的端口对不对"只有真收到才算验过（配置端口与实际端口不一致时尤其如此）。
/// </para>
/// </summary>
public sealed class AgentUdpChannelApiTests : IDisposable
{
    private readonly ControlServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin123"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "登录必须成功，否则后面的断言全无意义");
        return client;
    }

    private async Task<int> RegisterOneAsync(string machineKey)
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            "/api/agents/register",
            new AgentRegisterRequest(new AgentHostInfoDto(
                machineKey,
                "UDP-PC",
                "Windows 11 (integration test)",
                "Test CPU @ 1.00GHz",
                4,
                8192,
                "0.1.0",
                "tester",
                new[] { new AgentNetworkInterfaceDto("以太网", "127.0.0.1", "AA:BB:CC:DD:EE:0A", true) })));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var result = (await response.Content.ReadFromJsonAsync<AgentRegisterResult>())!;

        return result.AgentId!.Value;
    }

    /// <summary>
    /// 模拟"客户机连上来并报了状态"。
    ///
    /// <para>
    /// ⚠️ 这里刻意<b>分两步</b>（心跳 + 独立的状态上报），而不是一次性地把状态塞进
    /// <c>MarkSeen</c> —— 那正是生产上的形状：心跳只有一个参数（SignalR 的实参个数必须与签名
    /// 严格一致，见 <c>HubContractArityTests</c>），状态走 <c>ReportUdpChannel</c> 那条独立消息。
    /// 用例照着真实调用序走，测出来的才是真实行为。
    /// </para>
    /// <para>
    /// ⚠️ 也刻意<b>不写库</b>：心跳的真实形态就是"只进内存，60 秒才成批落盘"，
    /// 而"中心刚重启 / 刚落库之间"正是本组用例要覆盖的窗口。
    /// </para>
    /// </summary>
    private void SimulateHeartbeat(int agentId, string address, AgentUdpChannelStateDto? udp)
    {
        var registry = _factory.Services.GetRequiredService<AgentHostRegistry>();

        registry.MarkSeen(agentId, address, "0.1.0");

        if (udp is not null) registry.SetUdpState(agentId, udp);
    }

    /// <summary>在回环上开一个真 socket 当"客户机的 UDP 通道"，返回它的端口。</summary>
    private static (UdpClient Listener, int Port) ListenOnLoopback()
    {
        var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        return (listener, ((IPEndPoint)listener.Client.LocalEndPoint!).Port);
    }

    /// <summary>等一条报文（带超时，避免用例失败时挂死）。</summary>
    private static async Task<string> ReceiveAsync(UdpClient listener)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await listener.ReceiveAsync(cts.Token);

        return Encoding.UTF8.GetString(result.Buffer);
    }

    // ═══════════════════════════════════════════════════════════
    // 假阴性：配置里查不到，但机器明明在听
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task A_listening_agent_can_be_commanded_even_when_the_center_never_saved_a_config()
    {
        var client = await LoginAsync();
        var id = await RegisterOneAsync("udp-noconfig");

        var (listener, port) = ListenOnLoopback();

        using (listener)
        {
            // 客户机报："我在 8090 上听着"（这就是它没拿到配置时的**默认**状态）。
            // 这里用真实端口以便断言报文真的到了。
            SimulateHeartbeat(id, "127.0.0.1", new AgentUdpChannelStateDto(true, port, $"正在监听 0.0.0.0:{port}。"));

            var response = await client.PostAsJsonAsync(
                $"/api/agents/{id}/udp-command",
                new AgentUdpCommandRequest("DOWN"));

            // 🔥 改动前这里必然 409 channeloff（"该机未启用 UDP 指令通道"）——
            //    而事实是它一直在听。默认开启的通道不该被"中心没有配置记录"判死。
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

            (await ReceiveAsync(listener)).Should().Be(
                "DOWN",
                "中心必须把报文发到客户机**实际在听的**那个端口上");
        }
    }

    [Fact]
    public async Task The_live_port_wins_over_the_port_written_in_the_config()
    {
        var client = await LoginAsync();
        var id = await RegisterOneAsync("udp-portwins");

        // 中心下发过一份配置：启用，但端口写的是 19999（已废弃的值）。
        var saved = await client.PutAsJsonAsync(
            $"/api/agents/{id}/config",
            new AgentConfigSaveRequest(new AgentUdpChannelDto(Enabled: true, Port: 19999)));

        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());

        var (listener, port) = ListenOnLoopback();

        using (listener)
        {
            // 客户机报的是**另一个**端口（它按新配置重绑过，或者干脆还在用默认值）。
            SimulateHeartbeat(id, "127.0.0.1", new AgentUdpChannelStateDto(true, port, $"正在监听 0.0.0.0:{port}。"));

            var response = await client.PostAsJsonAsync(
                $"/api/agents/{id}/udp-command",
                new AgentUdpCommandRequest("RESTART"));

            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

            (await ReceiveAsync(listener)).Should().Be(
                "RESTART",
                "🔥 必须发到**实际在听**的端口：发到配置里那个没人听的端口，页面照样会说'已发出'");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 假阳性：配置写着启用，但机器根本没在听
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task A_silent_agent_is_refused_with_its_own_explanation()
    {
        var client = await LoginAsync();
        var id = await RegisterOneAsync("udp-silent");

        // 中心这边配置写着"启用"（所以旧逻辑一定会放行）。
        var saved = await client.PutAsJsonAsync(
            $"/api/agents/{id}/config",
            new AgentConfigSaveRequest(new AgentUdpChannelDto(Enabled: true, Port: 8090)));

        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());

        const string reason = "本机已在配置中停用任务下发（Agent:Tasks:Enabled=false）。";
        SimulateHeartbeat(id, "127.0.0.1", new AgentUdpChannelStateDto(false, null, reason, ExecuteEnabled: false));

        var response = await client.PostAsJsonAsync(
            $"/api/agents/{id}/udp-command",
            new AgentUdpCommandRequest("DOWN"));

        response.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            "客户机自己说没在听，就不该再说'已发出' —— 那正是最难查的一种现场");

        var body = (await response.Content.ReadFromJsonAsync<AgentMutationResult>())!;

        // ⚠️ 用**反序列化后的**字符串做包含判断，不要拿原始 JSON 文本去匹配中文：
        //    服务端用的是 System.Text.Json 的默认编码器，非 ASCII 会被转成 \uXXXX 转义，
        //    于是"肉眼看着一模一样"的两句话在原始文本里对不上。
        body.Message.Should().Contain(
            reason,
            "要把客户机自己那句原因原样转出来，而不是由中心总结一句'通道未启用'（那会指向错误的排查方向）");
    }

    [Fact]
    public async Task Without_any_live_state_and_without_a_config_the_refusal_says_so()
    {
        var client = await LoginAsync();
        var id = await RegisterOneAsync("udp-unknown");

        var response = await client.PostAsJsonAsync(
            $"/api/agents/{id}/udp-command",
            new AgentUdpCommandRequest("DOWN"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var body = (await response.Content.ReadFromJsonAsync<AgentMutationResult>())!;

        // ⚠️ 必须说清这是"**不知道**"，而不是"它没在听" —— 两者的处置完全不同
        //    （前者等一次心跳/升级客户端，后者去改配置）。
        body.Status.Should().Be("channeloff");
        body.Message.Should().Contain("还没有收到该机的 UDP 通道状态");
    }

    // ═══════════════════════════════════════════════════════════
    // 状态确实进了在线表（心跳 → 列表 DTO）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task The_reported_channel_state_is_visible_in_the_agent_list()
    {
        var client = await LoginAsync();
        var id = await RegisterOneAsync("udp-listed");

        SimulateHeartbeat(
            id,
            "127.0.0.1",
            new AgentUdpChannelStateDto(true, 8090, "正在监听 0.0.0.0:8090，来源黑名单为空（不限制来源）。"));

        var list = await client.GetFromJsonAsync<AgentListDto>("/api/agents");

        var host = list!.Items.Single(h => h.Id == id);

        host.UdpChannel.Should().NotBeNull(
            "状态只存在于中心进程的内存里（不落库），所以它必须能活着走到列表 DTO 上");

        host.UdpChannel!.Listening.Should().BeTrue();
        host.UdpChannel.Port.Should().Be(8090);
        host.UdpChannel.StateText.Should().Contain("正在监听");
    }

    [Fact]
    public async Task An_agent_that_never_reported_has_an_unknown_state_not_a_false_one()
    {
        var client = await LoginAsync();
        var id = await RegisterOneAsync("udp-neverreported");

        var list = await client.GetFromJsonAsync<AgentListDto>("/api/agents");

        var host = list!.Items.Single(h => h.Id == id);

        // 🔥 null = "它没告诉我"，与"它告诉我说没在听"是两件事。
        //    页面上一句"尚未上报"和一句"未监听"，会把人引向完全不同的排查方向。
        host.UdpChannel.Should().BeNull();
    }

    [Fact]
    public async Task A_plain_heartbeat_does_not_erase_the_separately_reported_state()
    {
        var client = await LoginAsync();
        var id = await RegisterOneAsync("udp-oldclient");

        SimulateHeartbeat(id, "127.0.0.1", new AgentUdpChannelStateDto(true, 8090, "正在监听 0.0.0.0:8090。"));

        // 🔥 再走一遍**普通心跳**（不带任何状态）—— 这就是生产上每 10 秒发生一次的事。
        //    它绝不能把上一次报上来的状态抹成"未知"或"未监听"：
        //    状态与心跳现在是两条独立的消息，谁也不该覆盖谁。
        SimulateHeartbeat(id, "127.0.0.1", null);

        var list = await client.GetFromJsonAsync<AgentListDto>("/api/agents");
        var host = list!.Items.Single(h => h.Id == id);

        host.UdpChannel.Should().NotBeNull("心跳不该把独立上报的状态冲掉");
        host.UdpChannel!.Listening.Should().BeTrue();
        host.UdpChannel.Port.Should().Be(8090);
    }

    [Fact]
    public async Task Sending_requires_an_administrator()
    {
        var client = await LoginAsync();
        var id = await RegisterOneAsync("udp-anon");

        SimulateHeartbeat(id, "127.0.0.1", new AgentUdpChannelStateDto(true, 8090, "正在监听 0.0.0.0:8090。"));

        using var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            $"/api/agents/{id}/udp-command",
            new AgentUdpCommandRequest("DOWN"));

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "它能关掉别人展厅里的机器，必须过管理员那道闸门");
    }

    // ═══════════════════════════════════════════════════════════
    // 变化检测：只推"真的变了"，不推"每一跳"
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <see cref="AgentHostRegistry.DiffUdpStates"/> 决定"要不要立刻推一次给网页"。
    ///
    /// <para>两头都要守住：</p>
    /// <list type="bullet">
    ///   <item><b>真变了必须推</b> —— 否则页面滞后 30 秒，"刚点的指令为什么不生效"这个判断
    ///         又建立在一个过期的真相上（那正是本次要消灭的东西）；</item>
    ///   <item><b>没变必须不推</b> —— 客户机每 10 秒上报一次同样的状态，
    ///         每次都推等于把页面按 10 秒一次刷起来。</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Only_a_real_change_in_the_channel_state_is_broadcast()
    {
        var client = await LoginAsync();
        var id = await RegisterOneAsync("udp-diff");

        var registry = _factory.Services.GetRequiredService<AgentHostRegistry>();

        var list = await client.GetFromJsonAsync<AgentListDto>("/api/agents");
        var bare = list!.Items.Single(h => h.Id == id);

        var listening = bare with
        {
            UdpChannel = new AgentUdpChannelStateDto(true, 8090, "正在监听 0.0.0.0:8090，来源黑名单为空（不限制来源）。"),
        };

        var stopped = bare with
        {
            UdpChannel = new AgentUdpChannelStateDto(false, null, "中心下发的配置没有启用 UDP 指令通道。"),
        };

        // ① 首轮播种：不推。
        registry.DiffUdpStates(new[] { listening }, seedOnly: true).Should().BeEmpty();

        // ② 一字不差 ⇒ 不推。
        registry.DiffUdpStates(new[] { listening }, seedOnly: false).Should().BeEmpty(
            "客户机每 10 秒上报一次同样的状态，每次都推会把页面刷爆");

        // ③ 真的关了 ⇒ 推。
        registry.DiffUdpStates(new[] { stopped }, seedOnly: false)
            .Should().HaveCount(1, "通道从'在听'变成'没在听'是运维最需要立刻看到的一次变化");

        // ④ "没告诉我"（UdpChannel 为 null）**不是变化**，也不该把已知状态抹掉。
        registry.DiffUdpStates(new[] { bare }, seedOnly: false).Should().BeEmpty(
            "null 的语义是'这台机器还没告诉我'，把它当成'状态变成未知了'会在中心重启后满屏假刷新");
    }

    /// <summary>
    /// 服务端重启后，客户机的**第一次**上报必须能推出去 ——
    /// 那时中心对这台机器一无所知，而它可能恰好是"刚被改坏的那一台"。
    /// </summary>
    [Fact]
    public async Task The_first_report_after_a_cold_start_is_broadcast()
    {
        var client = await LoginAsync();
        var id = await RegisterOneAsync("udp-coldstart");

        var registry = _factory.Services.GetRequiredService<AgentHostRegistry>();

        var list = await client.GetFromJsonAsync<AgentListDto>("/api/agents");
        var bare = list!.Items.Single(h => h.Id == id);

        // 冷启动那一次：全都是"还没告诉我" ⇒ 播种不下任何东西。
        registry.DiffUdpStates(new[] { bare }, seedOnly: true).Should().BeEmpty();

        var reported = bare with
        {
            UdpChannel = new AgentUdpChannelStateDto(true, 8090, "正在监听 0.0.0.0:8090。"),
        };

        registry.DiffUdpStates(new[] { reported }, seedOnly: false)
            .Should().HaveCount(1, "第一次拿到状态就该让页面看见");
    }
}
