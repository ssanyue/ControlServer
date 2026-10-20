using System.Text.RegularExpressions;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using ControlServer.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ControlServer.Server.Services;

/// <summary>
/// 监听地址（HTTP 端口）热切换的门面（M2-E5）。
///
/// <para>
/// <b>与方案 §4.4 的偏离（已实测确认）</b>：方案写的是"<c>StopAsync</c> → 改
/// <c>IServerAddressesFeature.Addresses</c> → <c>StartAsync</c>"。这条路<b>走不通</b>：
/// <c>KestrelServerImpl</c> 的 <c>_hasStarted</c> 标志在停止时不会复位，二次启动直接抛
/// <c>"The server has already started."</c>；而绕开它自己 new 一个 Kestrel 也不行 ——
/// <c>StartAsync</c> 需要的 <c>IHttpApplication&lt;HostingApplication.Context&gt;</c> 是 internal 类型。
/// 实际做法见 <see cref="SwitchableConnectionListener"/>：Kestrel 不动，只换底下的监听 socket。
/// </para>
/// <para>
/// <b>这一点带来一个额外好处</b>：切换可以<b>同步</b>完成，于是不需要方案里那套
/// "先返回已受理、延迟 N 秒再切"的编排 —— 用户点一下就能当场知道"成没成、端口腔没占住"。
/// </para>
/// </summary>
public sealed class ServerAddressService
{
    /// <summary>URL 里 host 段的最简校验：IP / 主机名 / <c>*</c> / <c>+</c>。</summary>
    private static readonly Regex HostPattern = new(@"^[A-Za-z0-9_\-\.\*\+]+$", RegexOptions.Compiled);

    private readonly ServerAddressRegistry _registry;
    private readonly ServerAddressOverrideStore _store;
    private readonly IHubContext<EventsHub> _hub;
    private readonly LogService _log;
    private readonly IConfiguration _configuration;

    public ServerAddressService(
        ServerAddressRegistry registry,
        ServerAddressOverrideStore store,
        IHubContext<EventsHub> hub,
        LogService log,
        IConfiguration configuration)
    {
        _registry = registry;
        _store = store;
        _hub = hub;
        _log = log;
        _configuration = configuration;
    }

    /// <summary>
    /// 当前<b>实际</b>监听的地址（不是配置里的值）。
    /// <para>监听器尚未装配时（服务启动早期）回退到配置，避免这个属性返回空导致界面显示"端口 0"。</para>
    /// </summary>
    public IReadOnlyList<string> CurrentUrls
    {
        get
        {
            var live = _registry.Urls;
            if (live.Count > 0)
            {
                return live;
            }

            var configured = _configuration["Server:Http:Urls"];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return SplitUrls(configured!);
            }

            var port = _configuration.GetValue("Server:Http:Port", 8899);
            return new List<string> { $"http://0.0.0.0:{port}" };
        }
    }

    /// <summary>当前实际端口；解析不出来为 0。</summary>
    public int CurrentPort
        => _registry.Port is var p and > 0
            ? p
            : (TryExtractPort(CurrentUrls, out var port) ? port : 0);

    /// <summary>端口持久化文件路径。</summary>
    public string OverrideFile => _store.FilePath;

    /// <summary>端口是否来自持久化覆盖（false = 来自 appsettings / 环境变量 / 默认值）。</summary>
    public bool Persisted => _store.Exists;

    /// <summary>读取当前监听地址。</summary>
    public ServerAddressDto Describe() => new(CurrentUrls, CurrentPort, OverrideFile, Persisted);

    /// <summary>
    /// 热切换监听地址。
    /// <para>
    /// <b>顺序不可颠倒</b>：先切换（新端口绑不上就什么都不做）→ 成功后才持久化。
    /// 反过来先写文件的话，一次失败的切换会让下次启动去绑一个已知不可用的端口，服务直接起不来。
    /// </para>
    /// </summary>
    public async Task<ServerAddressChangeResultDto> ChangeAsync(
        ServerAddressChangeRequest request,
        CancellationToken cancellationToken)
    {
        var allUrls = CurrentUrls;

        // 🔥 把 https 端点**摘出去**再谈"改地址"（M5.3）。两处理由，都是实打实的：
        //
        //   ① **"变没变"的判据必须同口径**：`IsSame(oldUrls, newUrls)` 若拿"http+https 两条"
        //      去比"只有 http 一条"，那么"把端口改回它现在的值"会被判成"变了" ——
        //      于是一次纯粹的确认操作变成一次真切换（去 Retarget 一个已经在听的端口）。
        //   ② **方案里本就不打算动 https 端口**：它的证书是按 IP 签的，改端口对它毫无意义；
        //      下面那句 `_registry.ListenerCount > 1` 的提示语也正是为"还有别的监听地址"写的。
        //
        // ⚠️ 但 DisplayUrls（返回给界面的 OldUrls）用的是**全量**列表：
        //    运维需要看见"这台机器现在还听着 8443"，否则他会以为那个端点没了。
        var oldUrls = allUrls.Where(u => !IsHttpsUrl(u)).ToList();

        if (oldUrls.Count == 0)
        {
            // 极端配置（只配了 https）：没有可热切换的对象。如实说，别硬试。
            return Failure(
                "NotReady",
                allUrls,
                new List<string>(),
                null,
                "当前只有 https 监听地址，没有可热切换的 http 地址。"
                + "请直接修改配置文件里的 Server:Http:Urls 后重启。");
        }

        if (!TryBuildTarget(request, oldUrls, out var newUrls, out var invalidReason))
        {
            return Failure("Invalid", allUrls, new List<string>(), null, invalidReason!);
        }

        if (!TryExtractPort(newUrls, out var newPort))
        {
            return Failure("Invalid", allUrls, newUrls, null, "地址里必须带端口，例如 http://0.0.0.0:9000。");
        }

        if (IsSame(oldUrls, newUrls))
        {
            // 地址没变，但持久化文件可能"落后"于现状：典型场景是文件里还留着早期的多地址
            // （或另一个端口），用户想把它收敛回来 —— 此时目标与实际恰好相同，
            // 若直接返回，界面上就永远改不动那个文件。
            //
            // 判据刻意收紧成「文件存在、且内容与目标不一致」才重写：
            // 文件不存在时什么都不做，免得一次纯粹的"确认"操作凭空生成一个持久化文件、
            // 把环境变量/命令行传进来的临时端口固化下来。
            var persisted = _store.TryReadUrls();
            string? convergeError = null;
            if (persisted is not null && !IsSame(persisted, newUrls))
            {
                convergeError = TryPersist(newUrls);
            }

            return new ServerAddressChangeResultDto(
                Ok: true,
                Status: "NoChange",
                OldUrls: allUrls,
                NewUrls: newUrls,
                NewPort: newPort,
                SecondsUntilSwitch: 0,
                Message: convergeError is null
                    ? $"目标地址与当前一致（{string.Join(", ", newUrls)}），无需切换。"
                    : $"目标地址与当前一致（{string.Join(", ", newUrls)}），无需切换；"
                      + $"但更新持久化文件失败：{convergeError} —— 重启后可能回到旧配置。");
        }

        var primary = _registry.Primary;
        if (primary is null)
        {
            return Failure("NotReady", allUrls, newUrls, newPort,
                "监听尚未就绪（服务可能正在启动或停机），请稍后重试。");
        }

        _log.Info("Http", $"开始切换监听端口：{string.Join(", ", oldUrls)} → {string.Join(", ", newUrls)}");

        var outcome = await primary.RetargetAsync(newPort, cancellationToken).ConfigureAwait(false);

        if (!outcome.Ok)
        {
            _log.Warn("Http", $"监听端口切换失败（服务仍在原端口）：{outcome.Error}");
            await BroadcastAsync("failed", oldUrls, newUrls, newPort, outcome.Error, cancellationToken)
                .ConfigureAwait(false);

            return Failure("PortUnavailable", allUrls, newUrls, newPort, outcome.Error!);
        }

        var persistError = TryPersist(newUrls);

        var message = $"监听端口已切换到 {newPort}。";
        if (_registry.ListenerCount > 1)
        {
            message += $"（配置中有 {_registry.ListenerCount} 个监听地址，热切换只作用于主地址 {primary.DisplayHost}，其余地址按原配置继续监听。）";
        }

        if (persistError is not null)
        {
            message += $" ⚠️ 写入持久化文件失败：{persistError} —— 本次已生效，但重启后会回到原配置。";
        }

        _log.Info("Http", message);
        await BroadcastAsync("changed", oldUrls, newUrls, newPort, null, cancellationToken).ConfigureAwait(false);

        return new ServerAddressChangeResultDto(
            Ok: true,
            Status: "Accepted",
            OldUrls: oldUrls,
            NewUrls: newUrls,
            NewPort: newPort,
            SecondsUntilSwitch: 0,
            Message: message);
    }

    private string? TryPersist(IReadOnlyList<string> urls)
    {
        try
        {
            _store.Save(urls);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private async Task BroadcastAsync(
        string phase,
        IReadOnlyList<string> oldUrls,
        IReadOnlyList<string> newUrls,
        int? newPort,
        string? error,
        CancellationToken cancellationToken)
    {
        try
        {
            await _hub.Clients.All
                .SendAsync(
                    EventNames.ServerAddressChanged,
                    new ServerAddressChangedDto(phase, oldUrls, newUrls, newPort, error, MessageFor(phase, error, newPort)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 广播失败绝不能让"端口已经切好了"这件事变成一次失败操作 —— 前端还能从 HTTP 响应里拿到结果。
        }
    }

    private static string MessageFor(string phase, string? error, int? port) => phase switch
    {
        "changed" => $"服务监听地址已变更为端口 {port}，请更新收藏夹 / 快捷方式。",
        "failed" => $"监听地址变更失败（已保持原端口）：{error}",
        _ => "监听地址即将变更。",
    };

    private static ServerAddressChangeResultDto Failure(
        string status,
        IReadOnlyList<string> oldUrls,
        IReadOnlyList<string> newUrls,
        int? newPort,
        string message)
        => new(false, status, oldUrls, newUrls, newPort, 0, message);

    /// <summary>把请求里的 <c>Urls</c> / <c>Port</c> 归一成一份完整地址列表。</summary>
    private static bool TryBuildTarget(
        ServerAddressChangeRequest request,
        IReadOnlyList<string> currentUrls,
        out List<string> target,
        out string? reason)
    {
        target = new List<string>();
        reason = null;

        if (request.Urls is { Count: > 0 })
        {
            var cleaned = new List<string>();
            foreach (var raw in request.Urls)
            {
                if (!TryValidateUrl(raw, out var why))
                {
                    reason = $"地址 {raw} 不合法：{why}";
                    return false;
                }

                var normalized = raw.Trim();
                if (!cleaned.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    cleaned.Add(normalized);
                }
            }

            // 热切换只作用于**主**监听地址。若放行多个地址，就会出现
            // 「只切了第一个、却把全部写进持久化文件」的假成功 —— 重启后多出来的地址
            // 会真的被绑上，运行期与重启后行为不一致。既然做不到，就不假装能做到。
            if (cleaned.Count > 1)
            {
                reason = $"热切换一次只能改一个监听地址（收到 {cleaned.Count} 个）。"
                    + "需要同时监听多个地址，请直接编辑配置文件里的 Server:Http:Urls（分号分隔）后重启。";
                return false;
            }

            target = cleaned;
            return true;
        }

        if (request.Port is { } port)
        {
            if (port is < 1 or > 65535)
            {
                reason = $"端口 {port} 越界，必须在 1-65535 之间。";
                return false;
            }

            if (!TryParseUrl(currentUrls[0], out var scheme, out var host, out _))
            {
                reason = "无法从当前地址推断主机名，请直接给出完整地址（如 http://0.0.0.0:9000）。";
                return false;
            }

            target = new List<string> { $"{scheme}://{host}:{port}" };
            return true;
        }

        reason = "必须提供 urls 或 port 之一。";
        return false;
    }

    internal static bool TryValidateUrl(string? raw, out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            reason = "地址为空。";
            return false;
        }

        if (!TryParseUrl(raw, out var scheme, out var host, out var port))
        {
            reason = "格式应为 scheme://host:port（端口不能省略），scheme 限 http / https；"
                + "用 * 或 + 表示所有网卡（如 http://*:9000）。";
            return false;
        }

        if (port is < 1 or > 65535)
        {
            reason = "端口必须在 1-65535 之间。";
            return false;
        }

        if (!HostPattern.IsMatch(host.Trim('[', ']')))
        {
            reason = $"主机段 {host} 含非法字符。";
            return false;
        }

        _ = scheme;
        return true;
    }

    /// <summary>
    /// 极简 URL 解析：只处理 Kestrel 监听前缀允许的形态
    /// （<c>scheme://host:port</c>，host 可为 IP / 主机名 / <c>*</c> / <c>+</c>，IPv6 带方括号）。
    /// <para>
    /// 刻意不用 <see cref="Uri"/>：<c>Uri.TryCreate("http://*:9000")</c> 会失败，
    /// 而 <c>*</c> 恰恰是 Kestrel 最常用的写法（"监听所有网卡"）。
    /// </para>
    /// </summary>
    internal static bool TryParseUrl(string raw, out string scheme, out string host, out int port)
    {
        scheme = string.Empty;
        host = string.Empty;
        port = 0;

        var text = raw.Trim();
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return false;
        }

        scheme = text[..schemeEnd];
        if (!scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = text[(schemeEnd + 3)..];

        // 去掉路径 / 查询（Kestrel 前缀不接受它们，但顺手容忍一下，用户常从浏览器地址栏复制）
        var cut = rest.IndexOfAny(new[] { '/', '?' });
        if (cut >= 0)
        {
            rest = rest[..cut];
        }

        if (rest.Contains('@', StringComparison.Ordinal))
        {
            return false;
        }

        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            host = rest[..(close + 1)];
            var tail = rest[(close + 1)..];
            if (tail.Length < 2 || tail[0] != ':')
            {
                return false;
            }

            return int.TryParse(tail[1..], out port);
        }

        var colon = rest.LastIndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        host = rest[..colon];
        return host.Length > 0 && int.TryParse(rest[(colon + 1)..], out port);
    }

    internal static bool TryExtractPort(IReadOnlyList<string> urls, out int port)
    {
        port = 0;

        foreach (var url in urls)
        {
            if (TryParseUrl(url, out _, out _, out var parsed))
            {
                port = parsed;
                return true;
            }
        }

        return false;
    }

    private static bool IsSame(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => a.Count == b.Count
           && a.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
               .SequenceEqual(b.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    /// <summary>这条监听地址是不是 https 那一个（见 <see cref="ChangeAsync"/> 为何要把它们摘出去）。</summary>
    private static bool IsHttpsUrl(string url)
        => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static List<string> SplitUrls(string raw)
        => raw
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
