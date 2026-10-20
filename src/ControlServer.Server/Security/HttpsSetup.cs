namespace ControlServer.Server.Security;

/// <summary>
/// <b>HTTPS 端点到底该不该开、开在哪个端口</b>（M5.3）—— 全项目只在这一处解析。
///
/// <para>
/// 🔥 <b>为什么必须统一成一处</b>：这个值有两个消费者，而它们的要求<b>相反</b>：
/// <c>Program.cs</c> 要拿它去<b>拼监听地址</b>（拼错了 = 端口根本没起），
/// <see cref="Services.ServerAddressRegistry"/> 要拿它去<b>判 scheme</b>
/// （判错了 = 状态页把 https 地址显示成 http，运维照着重启一次仍然连不上）。
/// 两处各写一遍"从配置里推"，必然在某次改配置时只改对一边，
/// 而症状是"地址栏里写着 http://…:8443，浏览器报 SSL 错误"。
/// </para>
///
/// <para>
/// <b>优先级</b>：<c>Server:Http:Urls</c> 里**显式写了** <c>https://</c> 的一律以它为准
/// （运维既然手写了，那就是最终意图，不该被 <c>Server:Https:*</c> 覆盖）；
/// 否则才看 <c>Server:Https:Enabled</c> / <c>Server:Https:Port</c>。
/// </para>
/// </summary>
/// <param name="Enabled">是否要提供 https 端点。</param>
/// <param name="Port">https 端口（<paramref name="Enabled"/> 为 false 时无意义）。</param>
/// <param name="FromUrls">真 = 端口是从 <c>Server:Http:Urls</c> 里读出来的，不是默认值。</param>
public sealed record HttpsSetup(bool Enabled, int Port, bool FromUrls)
{
    /// <summary><c>Server:Https:Port</c> 的默认值。</summary>
    /// <remarks>
    /// ⚠️ 选 8443 而不是 443：443 在 Windows 上常被别的服务占着，而且绑 1024 以下的端口
    /// 在某些平台上要额外权限 —— 一个"默认值就起不来"的功能等于没有这个功能。
    /// 也刻意<b>不</b>用 8081（紧邻 8899，容易和别的开发服务撞）。
    /// </remarks>
    public const int DefaultPort = 8443;

    /// <summary>从配置 + 已拼好的地址列表里解析出最终方案。</summary>
    public static HttpsSetup Resolve(IConfiguration configuration, string? urls)
    {
        foreach (var url in Split(urls))
        {
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && TryExtractPort(url, out var parsed))
            {
                return new HttpsSetup(true, parsed, FromUrls: true);
            }
        }

        // ⚠️ 默认**开**：不开的话，观看端会落在非安全上下文里，
        //    而 WebCodecs / AudioWorklet 在那种上下文里**根本不存在** ——
        //    症状是"音频区域没有报错，就是永远没声音"。
        //    一个"默认关、要自己去开"的开关，等于把这个坑留给现场。
        var enabled = configuration.GetValue("Server:Https:Enabled", true);
        var port = configuration.GetValue("Server:Https:Port", DefaultPort);

        if (port is < 1 or > 65535) port = DefaultPort;

        return new HttpsSetup(enabled, port, FromUrls: false);
    }

    /// <summary>判断某个端口是不是那个 https 端点（供 <see cref="Services.ServerAddressRegistry"/> 显示用）。</summary>
    public bool IsHttpsPort(int port) => Enabled && port == Port;

    private static IEnumerable<string> Split(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryExtractPort(string url, out int port)
    {
        port = 0;

        // ⚠️ 刻意用 Uri.TryCreate 而不是正则：`https://[::]:8443` 这种 IPv6 字面量
        //    用正则写很容易漏，而漏掉的表现就是"注册表显示成 http"。
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Port <= 0) return false;

        port = uri.Port;

        return true;
    }
}
