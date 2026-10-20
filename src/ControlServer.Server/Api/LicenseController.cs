using ControlServer.Contracts;
using ControlServer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ControlServer.Server.Api;

/// <summary>
/// 设备授权（M3）。
///
/// <para>
/// <b>读与写的权限刻意不同</b>：
/// <list type="bullet">
/// <item><description>
/// <c>GET /api/license</c> —— <b>所有已登录角色</b>。授权状态必须人人可见：
/// 否则现场只看到"按钮点了没反应"，却不知道是因为没授权，只能来问我们。
/// </description></item>
/// <item><description>
/// <c>request</c> / <c>import</c> / <c>trial</c> / <c>DELETE</c> —— <b>AdminOnly</b>。
/// 导入授权会改设备的凭证（相当于把机器"过户"），属于系统级操作。
/// </description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>为什么导入端点必须在授权闸门的白名单里</b>：它是现场<b>唯一的自救通道</b>。
/// 一旦闸门把"导入授权"本身也拦掉，用户就会陷入"没授权 → 不能导入授权"的死结，
/// 只能去手工改配置文件或直接往数据目录拷文件。
/// </para>
///
/// <para>
/// 写操作统一返回 <see cref="LicenseMutationResult"/>（含服务端全量状态）：
/// 失败时前端可直接据此刷新，不必再发一次 GET，也就不会停在旧状态上。
/// </para>
/// </summary>
[ApiController]
[Route("api/license")]
[Authorize]
public sealed class LicenseController : ControllerBase
{
    private readonly LicenseService _license;

    public LicenseController(LicenseService license) => _license = license;

    /// <summary>当前设备授权状态（全量）。所有已登录角色可读。</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
        => Ok(await _license.GetAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// 授权申请文本（机器码 + 计算机名 + 申请时间），供管理员复制后发给供应方。
    /// <para>
    /// 方法名刻意不叫 <c>Request</c> —— 那会遮蔽 <see cref="ControllerBase.Request"/>
    /// （编译器会警告 CS0108，而遮蔽掉 <c>HttpContext.Request</c> 真的会出事）。
    /// </para>
    /// </summary>
    [HttpGet("request")]
    [Authorize(Policy = Policies.AdminOnly)]
    public IActionResult RequestText() => Ok(_license.BuildRequest());

    /// <summary>导入授权。</summary>
    [HttpPost("import")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Import(
        [FromBody] ImportLicenseRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.LicenseText))
        {
            return BadRequest(new
            {
                message = "请粘贴授权文件内容（.lic 文件里的整段文本）。",
                license = (object?)null,
            });
        }

        var result = await _license
            .ImportAsync(request.LicenseText, Actor(), cancellationToken)
            .ConfigureAwait(false);

        return Map(result);
    }

    /// <summary>生成本机试用（仅在既无授权、也无有效试用时允许）。</summary>
    [HttpPost("trial")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> GenerateTrial(CancellationToken cancellationToken)
    {
        var result = await _license
            .GenerateTrialAsync(Actor(), cancellationToken)
            .ConfigureAwait(false);

        return Map(result);
    }

    /// <summary>移除授权文件（不重置本机试用）。</summary>
    [HttpDelete]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Remove(CancellationToken cancellationToken)
    {
        var result = await _license
            .RemoveAsync(Actor(), cancellationToken)
            .ConfigureAwait(false);

        return Map(result);
    }

    // ═══════════════════════════════════════════════════════════

    private ActorContext Actor()
        => new(
            SessionPrincipal.ReadUserId(User) ?? 0,
            User.Identity?.Name ?? "unknown",
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty);

    /// <summary>
    /// 结果 → HTTP 状态码。
    /// <para>
    /// 授权相关的失败统一用 <c>400</c>：这些都是"这份文件/这个操作在当前状态下不被接受"，
    /// 请求本身没有语法错误，也不是"没找到资源"。真正的 <c>503</c> 只留给"拿不到网络时间"
    /// （那确实是服务端暂时不可用）。
    /// </para>
    /// </summary>
    private IActionResult Map(LicenseMutationResult result)
    {
        var dto = new LicenseMutationResult(result.Ok, result.Status, result.Message, result.License);

        if (result.Ok) return Ok(dto);

        return result.Status switch
        {
            "no-network-time" => StatusCode(StatusCodes.Status503ServiceUnavailable, dto),
            "already-licensed" or "trial-active" => Conflict(dto),
            _ => BadRequest(dto),
        };
    }
}
