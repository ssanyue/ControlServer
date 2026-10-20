using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.Security;

/// <summary>
/// 客户端 Agent 的身份认证（M4.1）。
/// <para>
/// <b>与用户 Cookie 是两套独立身份</b>：Cookie 认"人"（谁在操作），本 scheme 认"机器"
/// （哪台客户机在连）。两者混用会让"停用某个账号"和"吊销某台机器"在代码与日志里纠缠不清 ——
/// 而这两种情况该做的处置完全不同（前者换个人登录，后者要重新装机）。
/// </para>
/// <para>
/// <b>凭据从哪来</b>：HTTP 走 <c>X-Agent-Key</c> 头；SignalR 的 WebSocket 握手<b>无法带自定义头</b>，
/// 只能走查询串 <c>?agentKey=…</c>。两条路都支持，优先级是"头 &gt; 查询串"。
/// </para>
/// <para>
/// <b>认证只认"哈希命中且未被吊销"</b>：库里存的是 agentKey 的 SHA-256
/// （理由见 <c>AgentKeys</c> 的注释），所以这里算一次哈希再等值查询 —— 走索引，O(log n)。
/// </para>
/// </summary>
public sealed class AgentKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IAgentHostStore _hosts;

    public AgentKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        UrlEncoder encoder,
        IAgentHostStore hosts)
        : base(options, logger, encoder)
    {
        _hosts = hosts;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = ReadKey();

        // 没有凭据 = "这次请求与本 scheme 无关"，交给别的 scheme（而不是判它失败）。
        // 返回 Fail 会让浏览器带上 Cookie 的普通请求也在这里报 401 并污染日志。
        if (string.IsNullOrWhiteSpace(key)) return AuthenticateResult.NoResult();

        var host = await _hosts
            .FindByAgentKeyHashAsync(AgentKeys.Hash(key), Context.RequestAborted)
            .ConfigureAwait(false);

        if (host is null) return AuthenticateResult.Fail("客户端凭据无效。");

        // 🔥 拉黑（2026-10-15，取代原「吊销」）：凭据本身仍然有效，只是**不被接纳**。
        //
        // 这一行是"拉黑"的全部力量来源 —— 它是 REST 与长连接**每一类请求**的必经之路，
        // 所以在库里留一个标记就足以让那台机器一个端点都碰不到。
        // ⚠️ 与旧吊销的关键差别：**不清 agentKey**（见 EfAgentHostStore.SetBlockedAsync），
        //    于是「取消拉黑」的下一秒它就正常连回来了，不需要重新注册。
        if (host.IsBlocked) return AuthenticateResult.Fail("该客户端已被管理员拉黑。");

        var claims = new List<Claim>
        {
            new(AgentAuthentication.ClaimAgentId, host.Id.ToString(CultureInfo.InvariantCulture)),
            new(AgentAuthentication.ClaimMachineKey, host.MachineKey),
            new(ClaimTypes.Name, host.DisplayName),
        };

        var identity = new ClaimsIdentity(claims, AgentAuthentication.Scheme);
        var principal = new ClaimsPrincipal(identity);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    /// <summary>从当前用户主体里读出客户端主机 Id（Hub 与端点共用）。</summary>
    public static int? ReadAgentId(ClaimsPrincipal? principal)
    {
        var raw = principal?.FindFirst(AgentAuthentication.ClaimAgentId)?.Value;

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    private string? ReadKey()
    {
        var header = Request.Headers[AgentAuthentication.HeaderName].ToString();
        if (!string.IsNullOrWhiteSpace(header)) return header.Trim();

        var query = Request.Query[AgentAuthentication.QueryName].ToString();
        return string.IsNullOrWhiteSpace(query) ? null : query.Trim();
    }
}
