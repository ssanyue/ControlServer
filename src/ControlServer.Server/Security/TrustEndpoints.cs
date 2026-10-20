using ControlServer.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ControlServer.Server.Security;

/// <summary>
/// <b>根证书的下载与"这台服务在哪个 https 地址上"</b>（M5.3）。
///
/// <para>
/// 🔥 <b>为什么这条路必须是普通的 http（不加密）</b>：
/// 观看端第一次访问时<b>还没信任那张证书</b>，此时从 https 端口去下载根证书，
/// 浏览器会先拦一道"此连接不安全"，用户点"继续"才拿得到 —— 而这一步点的次数一多，
/// 大家的习惯就变成"看见警告点继续"，那正好把这一整套信任机制的教育效果抵消掉。
/// 根证书里<b>只有公钥</b>，从明文端口发出去没有任何机密性损失。
/// </para>
///
/// <para>
/// 🔥 <b>为什么不放在 <c>/api</c> 之下</b>：授权闸门（<c>LicenseGate</c>）只作用于
/// <c>/api</c>，而这条路的可用性必须早于任何"已授权"的前提 ——
/// 一台还没导授权的机器照样要能把根证书装到观看端上。
/// 顺带它也避开了"每个新 <c>/api</c> 端点都要进分类表"的那条纪律，
/// 少一处将来会漂的东西。
/// </para>
///
/// <para><b>路由表</b>：</para>
/// <list type="bullet">
///   <item><c>GET /trust/info</c> —— 给前端横幅用：https 端口、根证书地址、指纹；</item>
///   <item><c>GET /trust/controlserver-ca.crt</c> —— 根证书本体（DER），
///         双击即可进 Windows 的证书导入向导。</item>
/// </list>
/// </summary>
public static class TrustEndpoints
{
    /// <summary><c>application/x-x509-ca-cert</c>：Windows 与 macOS 认这个 MIME 并直接拉起导入向导。</summary>
    private const string CaContentType = "application/x-x509-ca-cert";

    public static void MapTrustEndpoints(this WebApplication app)
    {
        // ⚠️ 显式 AllowAnonymous：这两个端点**必须**在没有登录、没有授权、
        //    甚至证书都还没被信任的时候就能访问 —— 否则就陷入
        //    "要装证书才能访问、要访问才能拿到证书" 的死结。
        app.MapGet("/trust/info", (HttpsSetup https, HttpsCertificateProvider provider, ServerAddressRegistry registry) =>
        {
            var authority = provider.Current;

            return Results.Ok(new TrustInfoDto(
                HttpsEnabled: https.Enabled && authority is not null,
                HttpsPort: https.Port,

                // ⚠️ 端口**由服务器给出**，不让前端猜：端口是运维改得动的，
                //    猜错的表现是"点了横幅之后连不上"。
                HttpsUrls: HttpsUrlsOf(registry, https),

                CaUrl: $"/trust/{LocalCertificateAuthority.CaDownloadFileName}",
                CaFingerprint: authority?.CaFingerprint,
                CaReason: authority is null
                    ? "服务端没能建立本地证书（数据目录不可写或平台不支持），因此 https 端点不可用；"
                      + "局域网观看端会落在非安全上下文里，远控音频与视频硬解不可用。"
                    : null));
        }).AllowAnonymous();

        app.MapGet($"/trust/{LocalCertificateAuthority.CaDownloadFileName}", (HttpsCertificateProvider provider) =>
        {
            var authority = provider.Current;

            if (authority is null)
            {
                return Results.Problem(
                    title: "根证书不可用",
                    detail: "服务端没能建立本地证书，请到服务端日志里查「Security」分类下的说明。",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // ⚠️ 发的是**内存里的公钥字节**，不是磁盘上任何一个文件。
            //    写成 Results.File(路径) 的话，一旦有人把文件名改成 server.pfx，
            //    发出去的就是私钥 —— 这类"改一个字符串就泄密"的形状不该留在代码里。
            return Results.File(
                authority.CaDer,
                contentType: CaContentType,
                fileDownloadName: LocalCertificateAuthority.CaDownloadFileName);
        }).AllowAnonymous();
    }

    /// <summary>实际在听的 https 地址（监听器还没装配时回落到配置推断出来的那一个）。</summary>
    private static IReadOnlyList<string> HttpsUrlsOf(ServerAddressRegistry registry, HttpsSetup https)
    {
        var live = registry.Urls
            .Where(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (live.Length > 0) return live;

        return https.Enabled ? new[] { $"https://0.0.0.0:{https.Port}" } : Array.Empty<string>();
    }
}

/// <summary><c>GET /trust/info</c> 的载荷（给前端横幅用）。</summary>
/// <param name="HttpsEnabled">
/// 服务端<b>确实</b>能提供 https（配置开着**而且**证书建得出来）。
/// <para>⚠️ 它与"配置里写了 https"不是一回事 —— 证书建不出来时这里是 false，前端应当直说。</para>
/// </param>
/// <param name="HttpsPort">https 端口（前端据此拼地址）。</param>
/// <param name="HttpsUrls">服务端实际在听的 https 地址（排障用；前端一般只用端口）。</param>
/// <param name="CaUrl">根证书的<b>同源相对</b>地址。</param>
/// <param name="CaFingerprint">根证书指纹（大写十六进制、冒号分隔），用于人工核对"装的是不是这一张"。</param>
/// <param name="CaReason">不可用时的原因（人话，可直接展示）。</param>
public sealed record TrustInfoDto(
    bool HttpsEnabled,
    int HttpsPort,
    IReadOnlyList<string> HttpsUrls,
    string CaUrl,
    string? CaFingerprint,
    string? CaReason);
