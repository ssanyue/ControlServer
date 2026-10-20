using System.Net;
using System.Net.Http.Json;
using System.Text;
using ControlServer.Contracts;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 远程桌面端点（M5.1）的端到端测试 —— 真宿主 + 真 HTTP 管线 + 真 DI。
///
/// <para>
/// 这组用例守的是<b>"接线到底接上了没有"</b>，而不是"画面好不好看"。
/// 理由很实际：M5.1 的码流链路（抓屏 → 编码 → 管道 → Hub → 浏览器）<b>没有一条能在 CI 里跑通</b>
/// —— 它需要一块能抓的桌面，而那正是 CI 机器没有的东西。
/// 于是"整条链路失效"最可能的形态不是某一步算错，而是：
/// </para>
/// <list type="bullet">
///   <item>某个服务忘了注册进 DI（那会在**第一次请求**时炸，不是启动时）；</item>
///   <item>Hub 忘了 <c>MapHub</c>（客户端连不上，而服务端日志一片安静）；</item>
///   <item>端点忘了进 <c>LicenseGate</c> 的分类表（fail-closed ⇒ 生产上 404）；</item>
///   <item>授权给错了（决策 11 要求 AdminOnly，非管理员连看都不许）。</item>
/// </list>
/// <para>
/// 这四类都有一个共同点：<b>编译能过、单测能过、只有真起一个宿主才暴露</b>。
/// 所以这组用例全部走 <see cref="ControlServerFactory"/> 的真宿主。
/// </para>
///
/// <para>
/// ⚠️ <b>它做不到的事（诚实记在这里）</b>：一帧真实画面的抓取 / 编码 / 回传，
/// 以及两条机器之间的实际码流。那要靠真机自检（见 docs/M5.0-实施清单.md 的验收步骤）。
/// 本文件只保证"服务端这一半是活的"。
/// </para>
/// </summary>
public sealed class RemoteDesktopApiTests : IDisposable
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

    private static async Task<RemoteNotAvailableDto?> ErrorOfAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<RemoteNotAvailableDto>();

    // ═══════════════════════════════════════════════════════════
    // 授权（决策 11）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>匿名一律 401</b>：远控端点是"看到别人屏幕"，比在线表更敏感。
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/remote/sessions")]
    [InlineData("GET", "/api/remote/sessions/abc")]
    [InlineData("GET", "/api/remote/capabilities/1")]
    [InlineData("POST", "/api/remote/sessions")]
    [InlineData("POST", "/api/remote/sessions/abc/grayscale")]
    [InlineData("POST", "/api/remote/sessions/abc/input")]
    [InlineData("DELETE", "/api/remote/sessions/abc")]
    public async Task Anonymous_access_is_rejected(string method, string path)
    {
        using var anonymous = _factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        if (method == "POST") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await anonymous.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "远控链路上一共有七个端点，任何一个漏了鉴权都等于把「看别人屏幕」开放给全网");
    }

    // ═══════════════════════════════════════════════════════════
    // 接线是否真的接上了
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>这一条是本文件里最重要的一条</b>：它同时证明三件事，
    /// 而这三件事各自的失败形态都很隐蔽 ——
    /// <list type="number">
    ///   <item><c>RemoteSessionRegistry</c> 真的注册进了 DI（否则控制器构造就抛）；</item>
    ///   <item>这七个端点真的进了 <c>LicenseGate</c> 的分类表
    ///         （否则中间件 fail-closed，生产上表现为 <b>404</b>，而测试环境是 Warn 档，
    ///          所以这里必须断言 <b>不是</b> 404）；</item>
    ///   <item>返回的是<b>空数组</b>而不是 <c>null</c> —— 前端可以直接 `for` 它。</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Session_list_is_alive_and_gives_an_empty_array_not_a_null()
    {
        using var admin = await LoginAsync();

        var response = await admin.GetAsync("/api/remote/sessions");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "404 就意味着这个端点没被 LicenseGate 归类（fail-closed），"
            + "或者控制器压根没注册 —— 两者在页面上都表现为「功能没了」");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("[]", "空态必须是空数组：回 null 会让前端的 for-of 直接抛，"
                               + "而那种错会在别的调用栈里冒出来，最难查");
    }

    /// <summary>
    /// 🔥 <b>M5.2 的输入端点已经接上了，而"接上了"这件事<b>不能靠状态码来判</b>。</b>
    ///
    /// <para>
    /// 这条用例原来是断言 <c>501 not-implemented</c>（M5.0 的骨架期）。
    /// 现在它改守另一件事，而这件事比原来那条更要紧：
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>404 有两种完全不同的来源，它们的状态码一模一样</b>：
    /// </para>
    /// <list type="number">
    ///   <item>会话真的不存在（我们要的答案）；</item>
    ///   <item><c>LicenseGate</c> 没把这个路由归类 ⇒ <b>fail-closed</b>，生产上直接 404
    ///         （那是"功能整个没了"，而它在页面上与第 1 种<b>没有区别</b>）。</item>
    /// </list>
    /// <para>
    /// 所以这里必须断言<b>响应体里的机器可读码</b> —— 只有它能把这两种 404 分开。
    /// 这是本项目的一条通用纪律：<b>判端点别看状态码</b>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>它测不到的部分（诚实记在这里）</b>：「闸门没开 ⇒ 409 <c>remote-input-not-enabled</c>」
    /// 与「批次过大 ⇒ 400」都要求<b>先有一路真会话</b>，而在 CI 里造不出一路真会话
    /// （那需要一台装了客户端、有人登录、还连上了中心的机器）。
    /// 这两条由 <c>RemoteSessionService.SendInputAsync</c> 的四道判断顺序本身钉住，
    /// 真机自检步骤见 docs/M5.0-实施清单.md。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Input_endpoint_is_alive_and_a_missing_session_is_not_the_gate_404()
    {
        using var admin = await LoginAsync();

        var response = await admin.PostAsJsonAsync(
            "/api/remote/sessions/abc/input",
            new { events = Array.Empty<object>() });

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "会话不存在就是 404（与 LicenseGate 的 fail-closed 同码，所以下一条断言才是关键）");

        var error = await ErrorOfAsync(response);

        error!.Code.Should().Be(
            RemoteErrorCodes.SessionNotFound,
            "🔥 必须是「会话不存在」这个码 —— 如果它是 LicenseGate 的码，"
            + "说明这个路由没进分类表（生产上功能整个消失，而页面上完全看不出区别）");

        error.Message.Should().Contain("abc", "错误文案要把那个不存在的会话 Id 带出来，否则现场无从下手");
    }

    /// <summary>
    /// 控制开关那个端点同样必须活着、同样必须归类进 <c>LicenseGate</c>。
    /// </summary>
    [Fact]
    public async Task Input_toggle_endpoint_is_alive_for_a_missing_session()
    {
        using var admin = await LoginAsync();

        var response = await admin.PostAsJsonAsync(
            "/api/remote/sessions/abc/input/enabled", new { enabled = true });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var error = await ErrorOfAsync(response);

        error!.Code.Should().Be(
            RemoteErrorCodes.SessionNotFound,
            "同上：用机器可读码把「会话不存在」与「闸门没放行」这两种 404 分开");
    }

    // ═══════════════════════════════════════════════════════════
    // 失败路径：三种"做不到"必须分得清
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>"机器不在在线表里"是 404</b>（去装 Agent / 查网络），
    /// 与"它在表里但掉线了"是 409 是<b>两件事</b>（等它重连）。
    /// 合并成一句"客户端不存在"，现场会去重装一个本来好好的客户端。
    /// </summary>
    [Fact]
    public async Task Starting_a_session_on_an_unknown_agent_is_404()
    {
        using var admin = await LoginAsync();

        var response = await admin.PostAsJsonAsync(
            "/api/remote/sessions",
            new { agentId = 987654, screenId = (string?)null, displayMode = (string?)null, maxFps = (int?)null });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var error = await ErrorOfAsync(response);
        error!.Code.Should().Be(RemoteErrorCodes.AgentNotFound);
    }

    /// <summary>
    /// ⚠️ 畸形的机器 Id 是 <b>400</b>，不是 404。
    ///
    /// <para>
    /// 404 的语义是"这个资源确实不存在"（那要去在线表里查一查）；
    /// 而 <c>agentId = 0</c> 是一个连查都不必查的畸形请求。
    /// 混成一个码，现场会去找一个根本不存在的"0 号机器"。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Starting_a_session_with_a_bogus_agent_id_is_400(int agentId)
    {
        using var admin = await LoginAsync();

        var response = await admin.PostAsJsonAsync(
            "/api/remote/sessions",
            new { agentId, screenId = (string?)null, displayMode = (string?)null, maxFps = (int?)null });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// 不存在的会话：读 / 灰度 / 结束三条路都要回 <b>404</b>，而不是 500 或 200 空对象。
    /// </summary>
    [Fact]
    public async Task Operations_on_a_missing_session_are_404()
    {
        using var admin = await LoginAsync();

        (await admin.GetAsync("/api/remote/sessions/nope")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        (await admin.PostAsJsonAsync("/api/remote/sessions/nope/grayscale", new { enabled = true }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await admin.DeleteAsync("/api/remote/sessions/nope")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// 能力探测：不存在的机器 404、畸形 Id 400。
    ///
    /// <para>
    /// 🔥 注意<b>在表但没上报过</b>那一档不在这里测 —— 它需要一台真的注册上来的客户机。
    /// 它的语义（<c>probed=false</c> ≠ "不支持远控"）由
    /// <c>EncoderCapabilityProbeTests</c> 与控制器上的注释各处分别钉住。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Capability_probe_separates_unknown_agent_from_bogus_id()
    {
        using var admin = await LoginAsync();

        (await admin.GetAsync("/api/remote/capabilities/987654")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "在线表里没有这台机器");

        var bogus = await admin.GetAsync("/api/remote/capabilities/0");

        bogus.StatusCode.Should().Be(HttpStatusCode.BadRequest, "0 是畸形 Id，不是「找不到的机器」");
        (await ErrorOfAsync(bogus))!.Code.Should().Be(RemoteErrorCodes.AgentNotFound);
    }

    // ═══════════════════════════════════════════════════════════
    // 观看端的实时通道（M5.1 新增）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b><c>/hubs/remote</c> 必须真的挂上了，而且必须是 AdminOnly。</b>
    ///
    /// <para>
    /// 这条用例守的是本方案里<b>最容易静默失败</b>的一处：<c>MapHub</c> 少写一行，
    /// 客户端的表现是"实时通道连不上"，而服务端日志里<b>一行都不会有</b>
    /// （请求根本没进管线）。前端那边则会显示"实时通道：未连接"，
    /// 而人会先去怀疑网络与防火墙。
    /// </para>
    ///
    /// <para>
    /// SignalR 的协商端点就是这条路由的入口：匿名叫不通（401）、管理员能拿到
    /// <c>connectionId</c> 与可用传输方式。这就足以证明路由与控制器的鉴权都到位了。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Remote_hub_is_mapped_and_requires_an_admin()
    {
        const string negotiate = "/hubs/remote/negotiate?negotiateVersion=1";

        using var anonymous = _factory.CreateClient();

        (await anonymous.PostAsync(negotiate, null)).StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "决策 11：非管理员连只读观看都不允许 —— 协商这一步就得挡住");

        using var admin = await LoginAsync();

        var response = await admin.PostAsync(negotiate, null);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "404 说明 MapHub<RemoteHub> 没挂上；这个失败在运行期只表现为「连不上」，极难定位");

        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("connectionId", "协商成功必须给出连接 Id，否则客户端建不起连接");
    }

    /// <summary>
    /// 两台机器之间那条 Hub 也不该被这次改动带坏 —— 它认的是 <c>AgentKey</c>，
    /// 与人的 Cookie 是两套身份。匿名协商必须仍然被拒。
    /// </summary>
    [Fact]
    public async Task Agent_hub_still_rejects_anonymous_negotiation()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsync("/hubs/agent/negotiate?negotiateVersion=1", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
