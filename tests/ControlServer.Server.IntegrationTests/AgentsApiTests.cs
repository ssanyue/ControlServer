using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using ControlServer.Server.Options;
using ControlServer.Server.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 客户端在线表与注册（M4.1）的端到端测试 —— 真宿主 + 真 SQLite + 真 HTTP 管线。
/// <para>
/// <b>2026-10-13 起注册免凭据</b>：装好客户端、能发现到中心服务端，就直接登记上线，
/// 不再有令牌 / 加入码这道门槛。因此本文件的重点从"凭据校验"整体转移到
/// <b>"免凭据之后什么是真的、什么是假的"</b>：
/// </para>
/// <list type="number">
///   <item><b>agentKey 只出现一次</b>：注册应答给，之后任何读端点都不许再有 ——
///         它一旦在任何列表里回显，等于把"这台机器的长期凭据"公开给了所有登录用户。</item>
///   <item><b>没有次数门槛</b>：不同机器码各注册一次都成功；同一机器码重复注册则
///         复用同一条记录（那是"重装了客户端"，不是新机器）。</item>
///   <item><b>写操作的响应体必须是 DTO 本身</b>，不能是 MVC 的 <c>ObjectResult</c> 包装
///         （见 <c>Mutation_endpoints_return_the_payload_directly</c>）。</item>
///   <item><b>离线时下发重启是 409 而不是 500</b>：请求合法，只是"现在做不到"。</item>
///   <item><b>🔥 离线不再等于"拒绝下发"</b>（2026-10-15）：关机 / 重启在长连接不在时
///         改走 UDP 指令通道尽力发出，返回 <c>ok:true</c> + <c>status:"udp"</c>，
///         并在消息里明写"该客户端当前离线"。同时"页面显示的在线状态"与"能不能下发"
///         从此<b>同源</b> —— 一组用例把这两件事绑在一起断言。</item>
/// </list>
/// <para>
/// 失败路径（缺机器标识 / 在线表已满）与"被拒必须能在服务端日志里被看见"这一组回归，
/// 因为需要把在线表容量调小才好触发，单独放在 <see cref="AgentRegistrationRejectionTests"/>。
/// </para>
/// </summary>
public sealed class AgentsApiTests : IDisposable
{
    private readonly ControlServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> LoginAsync(string userName = "admin", string password = "admin123")
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(userName, password));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "登录必须成功，否则后面的断言全无意义");
        return client;
    }

    // ═══════════════════════════════════════════════════════════
    // 读
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Fresh_instance_has_an_empty_list_and_anonymous_access_is_rejected()
    {
        using var anonymous = _factory.CreateClient();

        var anonymousResponse = await anonymous.GetAsync("/api/agents");
        anonymousResponse.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "在线表是设备资产台账，不该给匿名用户看");

        var client = await LoginAsync();
        var list = await client.GetFromJsonAsync<AgentListDto>("/api/agents");

        list.Should().NotBeNull();
        list!.Items.Should().BeEmpty();
        list.Total.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════
    // 注册（免凭据准入）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 核心语义：<b>没有任何凭据也能注册，而且没有"用一次就作废"这回事</b>。
    /// <para>
    /// 这曾经是反过来的 —— 注册必须带令牌 / 加入码，且加入码是单次的。
    /// 产品口径改成"只要装了客户端，局域网内都能自动发现，不设任何门槛"之后，
    /// 单次性就不成立了：三台机器各报一个机器码，都必须成功。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Register_without_any_credential_succeeds_for_every_machine()
    {
        using var anonymous = _factory.CreateClient();

        var first = await RegisterAsync(anonymous, "machine-1");

        first.Ok.Should().BeTrue();
        first.AgentKey.Should().NotBeNullOrWhiteSpace("agentKey 只在注册应答里给一次");
        first.AgentKey!.Length.Should().Be(43, "32 字节随机数的 Base64Url 表示");
        first.AgentId.Should().NotBeNull();
        first.HeartbeatSeconds.Should().NotBeNull("心跳间隔由服务端下发");

        // 第二个机器码再来一次 —— 免凭据准入下必须同样成功。
        var second = await RegisterAsync(anonymous, "machine-2");

        second.Ok.Should().BeTrue();
        second.AgentId.Should().NotBe(first.AgentId, "不同机器码 = 不同的机器");

        using var admin = await LoginAsync();
        var list = await admin.GetFromJsonAsync<AgentListDto>("/api/agents");
        list!.Total.Should().Be(2, "两次注册都成功，两台机器都要出现在在线表里");
    }

    /// <summary>
    /// 同一机器码重复注册 = "这台机器重装了客户端"，服务端<b>复用</b>原记录。
    /// <para>
    /// 免凭据之后这条语义尤其重要：<b>它决定了"吊销"能不能真的让一台机器下线</b>。
    /// 因为拦不住模仿，重新注册会把记录复活 —— 想真下线得去那台机器上卸载。
    /// 这里钉死"复用而不是新建"，避免有人把它改成"每次注册插一条"。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Registering_the_same_machine_key_reuses_the_existing_record()
    {
        using var anonymous = _factory.CreateClient();

        var first = await RegisterAsync(anonymous, "machine-reinstall");
        var second = await RegisterAsync(anonymous, "machine-reinstall");

        second.AgentId.Should().Be(
            first.AgentId,
            "同一机器码是同一台机器重装客户端 —— 必须复用记录，否则在线表会被重装刷出一堆幽灵");
        second.AgentKey.Should().NotBe(
            first.AgentKey,
            "每次注册都签发新的 agentKey（旧的那份从此失效，这正是「重装后旧凭据作废」的机制）");

        using var admin = await LoginAsync();
        var list = await admin.GetFromJsonAsync<AgentListDto>("/api/agents");
        list!.Total.Should().Be(1, "复用记录 ⇒ 在线表里只有一台");
    }

    /// <summary>
    /// 唯一一条"内容层面"的注册校验：机器标识必须报上来。
    /// <para>
    /// 免凭据并不等于什么都不检查 —— 没有 <c>machineKey</c> 就无法在线表里去重，
    /// 同一个客户端每次重试都会凭空多出一台机器。所以这一条必须是 400 而不是放行。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Register_without_a_machine_key_is_rejected_with_400()
    {
        using var anonymous = _factory.CreateClient();

        // ① 整个 HostInfo 都没给。
        var noInfo = await anonymous.PostAsJsonAsync(
            "/api/agents/register", new AgentRegisterRequest(null));

        noInfo.StatusCode.Should().Be(
            HttpStatusCode.BadRequest, "连主机信息都没有 ⇒ 没法登记");

        var noInfoResult = await noInfo.Content.ReadFromJsonAsync<AgentRegisterResult>();
        noInfoResult!.Ok.Should().BeFalse();
        noInfoResult.Status.Should().Be("invalid");

        // ② 有 HostInfo 但机器码是空白。
        var blank = await anonymous.PostAsJsonAsync(
            "/api/agents/register", new AgentRegisterRequest(HostInfo("   ")));

        blank.StatusCode.Should().Be(HttpStatusCode.BadRequest, "空机器码和没给是一回事");
        (await blank.Content.ReadFromJsonAsync<AgentRegisterResult>())!.Status.Should().Be("invalid");

        using var admin = await LoginAsync();
        var list = await admin.GetFromJsonAsync<AgentListDto>("/api/agents");
        list!.Total.Should().Be(0, "被拒的注册不能往在线表里塞东西");
    }

    // ═══════════════════════════════════════════════════════════
    // 写（含"响应体形状"的回归）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 回归守门：写操作的响应体必须是 <see cref="AgentMutationResult"/> <b>本身</b>。
    /// <para>
    /// 曾经写成 <c>IActionResult Mutation(...) =&gt; Ok(new AgentMutationResult(...))</c>
    /// 而调用方又 <c>return Ok(Mutation(...))</c>，于是外层多包了一层
    /// <c>ObjectResult</c>，序列化结果是
    /// <c>{"value":{...},"formatters":[],"contentTypes":[],"declaredType":null,"statusCode":200}</c>。
    /// </para>
    /// <para>
    /// 危害是<b>静默的</b>：状态码仍是 200，前端读 <c>result.ok</c> 拿到 <c>undefined</c>，
    /// 表现成"点了按钮没反应"。这条用例把它钉死 —— 一旦有人再写成收口式返回，
    /// 它会立刻变红并指出那个 <c>value</c> 字段。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Mutation_endpoints_return_the_payload_directly()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var response = await admin.PutAsJsonAsync(
            $"/api/agents/{agentId}",
            new AgentUpdateRequest("改过的名字", "一层", "靠窗那台"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.TryGetProperty("ok", out var ok).Should().BeTrue(
            "写操作的响应必须是 DTO 本体；当前响应体：{0}", root.ToString());
        ok.GetBoolean().Should().BeTrue();

        root.TryGetProperty("message", out _).Should().BeTrue();
        root.TryGetProperty("list", out _).Should().BeTrue();

        root.TryGetProperty("value", out _).Should().BeFalse(
            "出现 value 说明外层又包了 ObjectResult —— 那是 MVC 内部结构，不是我们的契约");

        // 顺带确认载荷内容真的是我们那个 DTO（字段名没被改坏）。
        // 用 JsonElement 直接断言**线上格式**，而不是 Deserialize<T>() 走一遍再断言 ——
        // 后者会引入序列化选项的差异，测的就不是"服务端发了什么"了。
        root.GetProperty("status").GetString().Should().Be("ok");
        root.GetProperty("list").GetProperty("total").GetInt32().Should().Be(1);
        root.GetProperty("list").GetProperty("items")[0].GetProperty("displayName")
            .GetString().Should().Be("改过的名字");
    }

    [Fact]
    public async Task Update_renames_regroups_and_persists()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var response = await admin.PutAsJsonAsync(
            $"/api/agents/{agentId}",
            new AgentUpdateRequest("展厅正面墙", "一层", "负责人：老张"));

        var mutation = await response.Content.ReadFromJsonAsync<AgentMutationResult>();
        mutation!.Ok.Should().BeTrue();

        var updated = mutation.List!.Items.Single();
        updated.DisplayName.Should().Be("展厅正面墙");
        updated.Group.Should().Be("一层");
        updated.Note.Should().Be("负责人：老张");

        // 再读一次：写操作必须真落库，而不是只改了内存快照。
        var detail = await admin.GetFromJsonAsync<AgentDetailDto>($"/api/agents/{agentId}");
        detail!.Host.DisplayName.Should().Be("展厅正面墙");
    }

    [Fact]
    public async Task Block_marks_the_host_and_unblock_clears_it()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var blocked = await admin.PostAsync($"/api/agents/{agentId}/block", null);
        blocked.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterBlock = await blocked.Content.ReadFromJsonAsync<AgentMutationResult>();
        var host = afterBlock!.List!.Items.Single();
        host.IsBlocked.Should().BeTrue();
        host.State.Should().Be(
            AgentStates.Blocked,
            "拉黑必须压过一切时间判据 —— 刚注册完 1 秒也算已拉黑");

        var unblocked = await admin.PostAsync($"/api/agents/{agentId}/unblock", null);
        var afterUnblock = await unblocked.Content.ReadFromJsonAsync<AgentMutationResult>();

        afterUnblock!.List!.Items.Single().IsBlocked.Should().BeFalse();
        afterUnblock.List.Items.Single().State.Should().NotBe(AgentStates.Blocked);
    }

    /// <summary>
    /// 🔥 <b>拉黑之后，同一台机器连注册都进不来</b>（2026-10-15 取代原「吊销」的关键差别）。
    /// <para>
    /// 旧「吊销」是**会被重新注册清掉**的：`UpdateRegistrationAsync` 里有
    /// <c>IsRevoked = false</c>，于是"吊销"实际只能挡到那台机器下一次重注册为止
    /// （当时的测试甚至把这个行为钉成了"必然结果"，见 git 历史）。
    /// 现在拉黑在<b>注册入口</b>就挡住，且不再清标记 —— 管理员说不行，就是不行，
    /// 除非他自己去点「取消拉黑」。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_blocked_host_cannot_register_again()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        (await admin.PostAsync($"/api/agents/{agentId}/block", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        using var anonymous = _factory.CreateClient();

        // ⚠️ 这里**不能**用 RegisterAsync 那个辅助方法：它内部断言"必须 200"
        //（那是给"正常注册"用的）。被拒是这条用例的**预期结果**，所以直接发请求读 DTO。
        var again = await RegisterRawAsync(anonymous, "machine-under-test");

        again.Ok.Should().BeFalse("被拉黑的机器连注册都要被拒 —— 否则拉黑能被客户机自己绕过");
        again.Status.Should().Be("blocked");
        again.AgentKey.Should().BeNull("被拒时绝不能给出新凭据");

        var detail = await admin.GetFromJsonAsync<AgentDetailDto>($"/api/agents/{agentId}");
        detail!.Host.IsBlocked.Should().BeTrue("被拒的注册不该顺手把拉黑标记清掉");

        // 取消拉黑之后……**下一次注册就该成功**（这就是"取消后立即恢复连接"的服务端那半）。
        (await admin.PostAsync($"/api/agents/{agentId}/unblock", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var third = await RegisterAsync(anonymous, "machine-under-test");

        third.Ok.Should().BeTrue("取消拉黑之后必须能重新进门，否则那条记录就成了死结");
        third.AgentId.Should().Be(agentId, "还是同一条记录（复用语义），不是新长出来一台");
    }

    /// <summary>发一次注册请求并读回 DTO，**不断言状态码**（给"预期被拒"的用例用）。</summary>
    private static async Task<AgentRegisterResult> RegisterRawAsync(HttpClient client, string machineKey)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/agents/register", new AgentRegisterRequest(HostInfo(machineKey)));

        return (await response.Content.ReadFromJsonAsync<AgentRegisterResult>())!;
    }

    /// <summary>
    /// 被<b>移除</b>的机器重新注册会回来（**新 Id**）—— 与「拉黑」刻意不同。
    /// <para>
    /// 移除 = 硬删记录，所以它下次注册走"新建一行"，拿到的是一台"新设备"；
    /// 拉黑 = 留着记录但拒绝（见 <see cref="A_blocked_host_cannot_register_again"/>）。
    /// 这两条合起来才是完整的语义：<b>要它消失并重新长出来用「移除」，
    /// 要它原地停住用「拉黑」</b>。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_removed_host_comes_back_as_a_new_record()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        (await admin.DeleteAsync($"/api/agents/{agentId}")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        using var anonymous = _factory.CreateClient();
        var again = await RegisterAsync(anonymous, "machine-under-test");

        again.Ok.Should().BeTrue();
        again.AgentId.Should().NotBe(agentId, "移除是硬删，重新注册必然是**新的一行**（新 Id）");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 关机 / 重启整机（2026-10-15：这个端点以前"下发成功、机器毫无反应"）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 本轮修的那个 BUG：这个端点一直返回"已下发「关机」指令"，
    /// 而客户端<b>收到之后没有任何人处理</b> <c>power</c> 指令（实现只长在 UDP 通道里）
    /// ⇒ 现象是"中心说已下发、机器毫无反应"。
    /// <para>
    /// 所以这里钉的是<b>契约</b>而不是执行结果：动作不合法是 400、机器不存在是 404、
    /// 没有长连接是 409。<b>"机器到底有没有真的关机"不归中心管</b> ——
    /// 那台机器上还有一道自己的闸（演练模式）与一整条自己的链路（平台权限 / 会话侧），
    /// 中心只能保证指令上了路，所以成功时的返回语必须明说去哪儿看结果。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Power_rejects_a_bad_action_and_an_unknown_agent()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var bad = await admin.PostAsJsonAsync(
            $"/api/agents/{agentId}/power", new AgentPowerRequest("halt"));

        bad.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "action 只能是 shutdown / restart —— 传别的必须挡在门口，不能发出去让客户机猜");

        var missing = await admin.PostAsJsonAsync(
            "/api/agents/999999/power", new AgentPowerRequest(AgentPowerActions.Shutdown));

        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// 注册过但<b>没有长连接</b>、<b>也不知道地址</b> ⇒ 真的送不出去 ⇒ 409（不是 500）。
    /// <para>
    /// ⚠️ 2026-10-15 之后这条<b>只覆盖"连 UDP 也够不着"的情形</b>：
    /// 客户机离线但地址已知时，服务端会改走 UDP 指令通道尽力发出（见
    /// <see cref="Power_falls_back_to_udp_when_the_long_connection_is_gone"/>）。
    /// 两种结果的差别是"报文已经上了网线"与"根本发不出去"，前端要分开说。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Power_is_a_conflict_when_the_client_is_not_connected_and_unreachable()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var response = await admin.PostAsJsonAsync(
            $"/api/agents/{agentId}/power", new AgentPowerRequest(AgentPowerActions.Shutdown));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var mutation = await response.Content.ReadFromJsonAsync<AgentMutationResult>();
        mutation!.Ok.Should().BeFalse();
        mutation.Status.Should().Be("offline");
        mutation.Message.Should().Contain("不在线", "要说清是『它现在够不着』，而不是一句含糊的『失败』");
    }

    /// <summary>
    /// 🔥 2026-10-15（需求②）：<b>长连接不在时不再直接拒绝，而是改走 UDP 指令通道尽力发出</b>。
    ///
    /// <para>现场口径："即便客户端离线，也要把指令发出去，但要如实告诉用户它离线"。</para>
    ///
    /// <para>这条用例钉两件事，缺一不可：</para>
    /// <list type="number">
    ///   <item>报文<b>真的</b>发到了客户机在听的那个端口（回环上开真 socket 收，而不是只看返回码）；</item>
    ///   <item>返回语里<b>必须</b>写着"该客户端当前离线" —— 否则运维会把"已发出"读成"机器马上会关"，
    ///         而那正是本题要消灭的那类误导。</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Power_falls_back_to_udp_when_the_long_connection_is_gone()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var registry = _factory.Services.GetRequiredService<AgentHostRegistry>();

        // 回环上开一个真 socket 当"客户机的 UDP 指令通道"。
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        // 客户机**曾经**连着：报过地址与它实际在听的端口。此刻长连接已经不在
        //（没有任何 MarkConnected ⇒ ConnectionIdOf 为 null）—— 这正是要覆盖的窗口。
        registry.MarkSeen(agentId, "127.0.0.1", "0.1.0");
        registry.SetUdpState(agentId, new AgentUdpChannelStateDto(true, port, $"正在监听 0.0.0.0:{port}。"));

        registry.ConnectionIdOf(agentId).Should().BeNull("前提条件：这一刻它没有长连接");

        var response = await admin.PostAsJsonAsync(
            $"/api/agents/{agentId}/power", new AgentPowerRequest(AgentPowerActions.Shutdown));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "离线不再等于「发不出去」：这是需求②的核心");

        var mutation = await response.Content.ReadFromJsonAsync<AgentMutationResult>();

        mutation!.Ok.Should().BeTrue();
        mutation.Status.Should().Be("udp", "走的是 UDP 兜底，不是一个含混的 ok");
        mutation.Message.Should().Contain("离线", "必须如实告诉用户那台机器当前离线");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await listener.ReceiveAsync(cts.Token);

        Encoding.UTF8.GetString(received.Buffer).Should().Be(
            "DOWN",
            "报文必须是客户机认得的那条线路 token（TryFormat 与 TryResolveCommand 成对）");
    }

    /// <summary>
    /// 🔥 2026-10-15 修的 BUG：<b>心跳刚过、但长连接已经断了的机器，不能再显示"在线"</b>。
    ///
    /// <para>
    /// 一条用例把"页面显示"与"能不能下发"绑在一起 —— 它们曾经各说各话：
    /// 页面按心跳时间说"在线"（绿点、按钮可点），而下发闸门按连接 Id 说"离线"（409）。
    /// 于是现场就是"明明在线，一点关机却说客户端离线"。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_host_whose_connection_dropped_is_not_shown_online()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var registry = _factory.Services.GetRequiredService<AgentHostRegistry>();

        // ① 连上了：MarkConnected 会写下连接 Id 并刷新最后心跳。
        registry.MarkConnected(agentId, "conn-1");

        var online = await admin.GetFromJsonAsync<AgentListDto>("/api/agents");
        online!.Items.Single(h => h.Id == agentId).State.Should().Be(AgentStates.Online);

        // ② 断开（OnDisconnected 的真实形状）：最后心跳会被**钉成断开那一刻**，
        //    所以"只看时间"的旧判定在接下来 30 秒里仍然会说"在线"。
        registry.MarkDisconnected(agentId, "conn-1");

        var afterDrop = await admin.GetFromJsonAsync<AgentListDto>("/api/agents");

        afterDrop!.Items.Single(h => h.Id == agentId).State.Should().Be(
            AgentStates.Unstable,
            "长连接已经断了就不许再显示『在线』—— 那正是『明明在线却说离线』的来源");
    }

    /// <summary>
    /// 🔥 中心刚重启时也一样：库里那条"最后心跳"可能只落后几秒，但<b>一条连接都没有</b>
    /// —— 不许据此显示"在线"（这是同一个 BUG 的另一半）。
    /// <para>
    /// 覆盖方式：拿一个新的注册表去装载库里的历史（＝"中心刚重启"的内存态）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task After_a_center_restart_the_loaded_hosts_are_not_shown_online()
    {
        await LoginAsync();
        var agentId = await RegisterOneAsync();

        var store = _factory.Services.GetRequiredService<IAgentHostStore>();

        // 库里的"最后心跳"只落后几秒（那是落盘值，最坏落后 60 秒）。
        await store.TouchAsync(agentId, BeijingTime.Now, "127.0.0.1", "0.1.0");

        // 全新注册表 = 中心刚重启：内存态是一张白纸，只有库里的历史。
        var fresh = new AgentHostRegistry(store, _factory.Services.GetRequiredService<LogService>());
        await fresh.RefreshAsync();

        var host = fresh.Hosts.Single(h => h.Id == agentId);
        var options = _factory.Services.GetRequiredService<IOptions<ServerOptions>>().Value.Agent;

        fresh.ToDto(host, options, BeijingTime.Now).State.Should().Be(
            AgentStates.Unstable,
            "库里那条心跳只说明『它几秒前还在』；重启后一条连接都还没有，不能显示成在线");

        fresh.ConnectionIdOf(agentId).Should().BeNull("同一时刻它也发不出指令 —— 显示与下发必须同源");
    }

    /// <summary>
    /// 拉黑的机器不再接受任何指令（2026-10-15）。
    /// <para>
    /// 这是给"离线也要发"这条新需求补的闸门：UDP 兜底恰恰是"不建连接也能控制"的后门，
    /// 不在这里挡住，拉黑就成了半拉 —— 一台被拉黑的机器照样能被中心关机。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_blocked_host_refuses_commands_even_when_it_could_be_reached()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var registry = _factory.Services.GetRequiredService<AgentHostRegistry>();

        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        registry.MarkSeen(agentId, "127.0.0.1", "0.1.0");
        registry.SetUdpState(agentId, new AgentUdpChannelStateDto(true, port, $"正在监听 0.0.0.0:{port}。"));

        (await admin.PostAsync($"/api/agents/{agentId}/block", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var response = await admin.PostAsJsonAsync(
            $"/api/agents/{agentId}/power", new AgentPowerRequest(AgentPowerActions.Shutdown));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var mutation = await response.Content.ReadFromJsonAsync<AgentMutationResult>();
        mutation!.Status.Should().Be("blocked");
        mutation.Message.Should().Contain("拉黑");
    }

    [Fact]
    public async Task Reboot_is_a_conflict_when_the_client_is_not_connected()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var response = await admin.PostAsync($"/api/agents/{agentId}/reboot", null);

        response.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            "请求本身合法、只是「现在做不到」 —— 500 会让人以为服务端坏了");

        var mutation = await response.Content.ReadFromJsonAsync<AgentMutationResult>();
        mutation!.Ok.Should().BeFalse();
        mutation.Status.Should().Be("offline");
    }

    /// <summary>
    /// 「重启 Agent 服务」<b>没有 UDP 兜底</b>（2026-10-15）：UDP 指令通道只支持
    /// 关机 / 重启整机 / 音量，而这条必须由 Agent 自己在长连接上处理。
    /// <para>
    /// 所以它对一台"离线但地址已知"的机器仍然是 <b>409 offline</b>，而且提示语必须指向
    /// <b>正确方向</b>（"只能经长连接下发"），不能让人照着"开机 / 查地址"去排查。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Reboot_says_it_needs_the_long_connection_instead_of_offering_udp()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var registry = _factory.Services.GetRequiredService<AgentHostRegistry>();
        registry.MarkSeen(agentId, "127.0.0.1", "0.1.0");

        var response = await admin.PostAsync($"/api/agents/{agentId}/reboot", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var mutation = await response.Content.ReadFromJsonAsync<AgentMutationResult>();
        mutation!.Status.Should().Be("offline");
        mutation.Message.Should().Contain("长连接", "要指向正确的排查方向：这条指令只能经长连接下发");
    }

    [Fact]
    public async Task Delete_removes_the_record_and_says_it_does_not_uninstall()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var response = await admin.DeleteAsync($"/api/agents/{agentId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var mutation = await response.Content.ReadFromJsonAsync<AgentMutationResult>();
        mutation!.Ok.Should().BeTrue();
        mutation.List!.Total.Should().Be(0);
        mutation.Message.Should().Contain(
            "卸载",
            "必须讲清「移除记录 ≠ 卸载」 —— 否则用户会以为那台机器已经下线了");

        var missing = await admin.GetAsync($"/api/agents/{agentId}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Read_endpoints_never_expose_the_agent_key()
    {
        var admin = await LoginAsync();
        var (agentId, agentKey) = await RegisterOneAsyncReturningKeyAsync();

        foreach (var path in new[] { "/api/agents", $"/api/agents/{agentId}" })
        {
            var raw = await admin.GetStringAsync(path);

            raw.Should().NotContain(
                agentKey,
                "agentKey 是这台机器的长期凭据，任何读端点回显它都等于把它公开");
            raw.Should().NotContainEquivalentOf("agentKey");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 权限
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Operator_can_read_the_board_but_cannot_mutate_the_ledger()
    {
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var create = await admin.PostAsJsonAsync(
            "/api/users", new { userName = "agent-operator", password = "Operator@2026!", role = "Operator" });

        create.StatusCode.Should().Be(HttpStatusCode.OK);

        var op = await LoginAsync("agent-operator", "Operator@2026!");

        (await op.GetAsync("/api/agents")).StatusCode.Should().Be(
            HttpStatusCode.OK, "运维日常要看在线表");

        (await op.PostAsync($"/api/agents/{agentId}/block", null)).StatusCode.Should().Be(
            HttpStatusCode.Forbidden, "拉黑 = 切断与一台设备的连接，只给 Admin");

        (await op.PutAsJsonAsync($"/api/agents/{agentId}", new AgentUpdateRequest("x", null, null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "改名 = 动资产台账，只给 Admin");
    }

    // ═══════════════════════════════════════════════════════════
    // 安装包下载
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Installer_info_reports_unavailable_then_downloads_with_range_support()
    {
        var admin = await LoginAsync();

        var info = await admin.GetFromJsonAsync<AgentInstallerInfoDto>(
            "/api/downloads/agent-installer/info");

        info!.Available.Should().BeFalse();
        info.Hint.Should().NotBeNullOrWhiteSpace("必须告诉管理员「该把文件放到哪」");
        info.Hint.Should().Contain("downloads");

        (await admin.GetAsync("/api/downloads/agent-installer")).StatusCode.Should().Be(
            HttpStatusCode.NotFound);

        // 放一个"安装包"进去。
        var payload = new byte[1024 * 64];

        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

        var directory = Path.Combine(_factory.DataDirectory, "downloads");
        Directory.CreateDirectory(directory);
        var fileName = "ControlServer-Agent-Setup-0.1.0.exe";
        await File.WriteAllBytesAsync(Path.Combine(directory, fileName), payload);

        var after = await admin.GetFromJsonAsync<AgentInstallerInfoDto>(
            "/api/downloads/agent-installer/info");

        after!.Available.Should().BeTrue();
        after.FileName.Should().Be(fileName);
        after.SizeBytes.Should().Be(payload.Length);
        after.Version.Should().Be("0.1.0", "版本从约定文件名里解析");

        var download = await admin.GetAsync("/api/downloads/agent-installer");
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        (await download.Content.ReadAsByteArrayAsync()).Should().Equal(payload);

        download.Content.Headers.ContentDisposition!.FileName!.Trim('"').Should().Be(fileName);
        download.Headers.AcceptRanges.Should().Contain("bytes", "断点续传的前提");

        // 真 Range 请求。
        using var rangeRequest = new HttpRequestMessage(
            HttpMethod.Get, "/api/downloads/agent-installer");

        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 9);

        var rangeResponse = await admin.SendAsync(rangeRequest);
        rangeResponse.StatusCode.Should().Be(HttpStatusCode.PartialContent);

        var chunk = await rangeResponse.Content.ReadAsByteArrayAsync();
        chunk.Should().HaveCount(10);
        chunk.Should().Equal(payload.Take(10));
    }

    // ═══════════════════════════════════════════════════════════
    // 种子状态
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task A_freshly_registered_host_is_waiting_to_connect_not_offline()
    {
        // 「等待连接」与「离线」导出的下一步动作完全不同：前者查"客户端装起来了吗"，
        // 后者查"机器 / 网络怎么了"。合并成一个会让现场排查一开始就走错方向。
        var admin = await LoginAsync();
        var agentId = await RegisterOneAsync();

        var detail = await admin.GetFromJsonAsync<AgentDetailDto>($"/api/agents/{agentId}");

        detail!.Host.State.Should().Be(AgentStates.Never);
        detail.Host.StateDisplay.Should().Be("等待连接");
        detail.Host.LastSeenAt.Should().BeNull("从未连过就不该伪造一个时间");
        detail.Connected.Should().BeFalse();
        detail.Host.MachineKey.Should().Be("machine-under-test");
    }

    [Fact]
    public async Task Host_info_with_unknown_hardware_stays_null_instead_of_zero()
    {
        // 0 会被当成真数据展示成"0 核 CPU / 0 MB 内存"。
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            "/api/agents/register",
            new AgentRegisterRequest(new AgentHostInfoDto(
                "machine-unknown", "PC", "Windows", null, null, null, "0.1.0", null,
                Array.Empty<AgentNetworkInterfaceDto>())));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var admin = await LoginAsync();
        var list = await admin.GetFromJsonAsync<AgentListDto>("/api/agents");
        var host = list!.Items.Single();

        host.CpuName.Should().BeNull();
        host.LogicalProcessors.Should().BeNull();
        host.TotalMemoryMb.Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════

    /// <summary>免凭据注册一次，返回注册结果；状态码不是 200 直接失败。</summary>
    private static async Task<AgentRegisterResult> RegisterAsync(HttpClient client, string machineKey)
    {
        var response = await client.PostAsJsonAsync(
            "/api/agents/register", new AgentRegisterRequest(HostInfo(machineKey)));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<AgentRegisterResult>())!;
    }

    private static AgentHostInfoDto HostInfo(string machineKey)
        => new(
            machineKey,
            "SMOKE-PC",
            "Windows 11 (integration test)",
            "Test CPU @ 1.00GHz",
            4,
            8192,
            "0.1.0",
            "tester",
            new[] { new AgentNetworkInterfaceDto("以太网", "192.168.1.10", "AA:BB:CC:DD:EE:01", true) });

    /// <summary>免凭据注册一台固定机器码的机器，返回它的 Id。</summary>
    private async Task<int> RegisterOneAsync()
    {
        var (id, _) = await RegisterOneAsyncReturningKeyAsync();
        return id;
    }

    private async Task<(int Id, string AgentKey)> RegisterOneAsyncReturningKeyAsync()
    {
        using var anonymous = _factory.CreateClient();

        var result = await RegisterAsync(anonymous, "machine-under-test");

        return (result.AgentId!.Value, result.AgentKey!);
    }
}
