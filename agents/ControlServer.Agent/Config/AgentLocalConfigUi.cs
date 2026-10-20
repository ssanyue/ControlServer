using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Agent.Diagnostics;
using ControlServer.Agent.Tasks;
using ControlServer.Contracts;
using ControlServer.Core.Models;
using ControlServer.Core.Services;

namespace ControlServer.Agent.Config;

/// <summary>
/// 客户机上的<b>本机配置页面</b>（2026-10-14）。
///
/// <para><b>它解决什么</b>：用户要求"客户端本身可独立使用" —— 没有中心服务端时，
/// 也要能改这台机器的延时 / 循环发送、定时发送、延时启动程序、UDP 端口。
/// 做法是在客户机上开一个小网页（<b>只绑 <c>127.0.0.1</c></b>），托盘菜单「打开本机配置」打开它。</para>
///
/// <para>🔒 <b>为什么用 <see cref="TcpListener"/> 手写一小段 HTTP，而不是 <c>HttpListener</c></b>：
/// <c>HttpListener</c> 在 Windows 上走 HTTP.SYS，注册前缀要么<b>以管理员身份运行</b>、
/// 要么先 <c>netsh http add urlacl</c>。而客户端在<b>前台排障</b>时恰恰是普通用户权限 ——
/// 那样它会以 <c>Access is denied</c> 静默起不来。裸 <see cref="TcpListener"/> 绑环回不需要任何授权，
/// 「双击就能用」这一条才成立。代价是 HTTP 解析自己写，所以这里<b>刻意只认</b>
/// 最朴素的一小撮语法：请求行 + 头部 + <c>Content-Length</c> 正文，其余一律 400。</para>
///
/// <para>🔒 <b>三道闸门</b>（这个页面能让本机改配置，等于本机的一个控制面）：</para>
/// <list type="number">
///   <item>只绑 <see cref="IPAddress.Loopback"/>；</item>
///   <item><b>Host 头必须是 <c>127.0.0.1</c> / <c>localhost</c></b> —— 挡 DNS 重绑定
///         （攻击者把自己的域名解析到 127.0.0.1，浏览器连过来的源地址确实是环回，
///         但 Host 头是那个域名）；</item>
///   <item>写操作要带<b>本进程随机生成的令牌</b>（<see cref="TokenHeaderName"/>）。
///         令牌写在页面的 JS 里，跨站页面读不到它，也就伪造不出这个请求
///         （自定义头还会先触发 CORS 预检，而我们不答 CORS 头）。</item>
/// </list>
/// <para>
/// 仍然要说清：<b>同一台机器上的其它本地进程/用户</b>能打开这个页面 ——
/// 这等价于"能坐在机器前的人"，与本项目"客户端不需要管理员在现场"的部署模型是一致的。
/// 想彻底关掉它：<c>Agent:LocalUi:Enabled=false</c>。
/// </para>
/// </summary>
public sealed class AgentLocalConfigUi : IAsyncDisposable
{
    /// <summary>写操作要带的令牌头名。</summary>
    public const string TokenHeaderName = "X-CS-Local-Token";

    /// <summary>页面里那个令牌占位符（服务时替换成真令牌）。</summary>
    private const string PageTokenPlaceholder = "__CS_LOCAL_TOKEN__";

    /// <summary>内嵌页面资源名（见 csproj 的 EmbeddedResource）。</summary>
    private const string PageResourceName = "ControlServer.Agent.Config.local-ui.html";

    /// <summary>请求头上限（16 KB）。超过就不是"浏览器发的正常请求"了。</summary>
    private const int MaxHeaderBytes = 16 * 1024;

    /// <summary>请求正文上限（1 MB）。四张清单加起来离它远得很。</summary>
    private const int MaxBodyBytes = 1024 * 1024;

    private readonly AgentOptions _options;
    private readonly AgentConfigManager _configs;
    private readonly LogService _log;
    private readonly ReceivedDataLog _received;
    private readonly UdpCommandListener _udp;
    private readonly AgentServerOverride _serverOverride;
    private readonly string _stateDirectory;

    /// <summary>每次进程启动随机生成，只活在这条进程里。</summary>
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public AgentLocalConfigUi(
        AgentOptions options,
        AgentConfigManager configs,
        LogService log,
        ReceivedDataLog received,
        UdpCommandListener udp,
        AgentServerOverride serverOverride)
    {
        _options = options;
        _configs = configs;
        _log = log;
        _received = received;
        _udp = udp;
        _serverOverride = serverOverride;
        _stateDirectory = AgentPaths.ResolveStateDirectory(options.StateDirectory);
    }

    /// <summary>此刻监听的地址；没起来时为 null。</summary>
    public string? Url { get; private set; }

    /// <summary>
    /// 起监听（幂等：已经起来就直接返回）。<b>失败只记日志，绝不抛</b> ——
    /// 这个页面是一个便利，它起不来不该让客户端主循环跟着起不来。
    /// </summary>
    public void Start()
    {
        if (_listener is not null) return;

        var ui = _options.LocalUi;

        if (!ui.Enabled)
        {
            _log.Info("Config", "本机配置页已停用（Agent:LocalUi:Enabled=false）—— 托盘菜单「打开本机配置」会提示未启用。");
            return;
        }

        var port = Math.Clamp(ui.Port, AgentConfigDefaults.MinUdpPort, AgentConfigDefaults.MaxUdpPort);

        if (port != ui.Port)
        {
            _log.Warn("Config", $"本机配置页端口 {ui.Port} 超出 {AgentConfigDefaults.MinUdpPort}–{AgentConfigDefaults.MaxUdpPort}，已改用 {port}。");
        }

        TcpListener listener;

        try
        {
            // ⚠️ 只绑环回。绑 Any 等于把"改本机配置"这件事挂到局域网上。
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
        }
        catch (Exception ex)
        {
            _log.Warn(
                "Config",
                $"本机配置页启动失败（端口 {port}）：{ex.Message}"
                + "｜若提示端口被占用，改 Agent:LocalUi:Port；若想关掉它，设 Agent:LocalUi:Enabled=false。"
                + "（中心下发那条路完全不受影响。）");
            return;
        }

        _listener = listener;
        Url = $"http://127.0.0.1:{port}/";

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));

        AgentLocalUiDiscoveryFile.Write(_stateDirectory, Url);

        _log.Info("Config", $"本机配置页已启动：{Url}（仅本机可访问）—— 托盘菜单「打开本机配置」可直接打开。");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Stop() 之后循环可能正好走到这里：正常退出路径。
                break;
            }
            catch (SocketException)
            {
                // 同上（Stop 会让挂起的 Accept 抛）。
                break;
            }
            catch (Exception ex)
            {
                // 铁律 #6：一条连接出错不能让监听循环退出。
                _log.Warn("Config", $"本机配置页接受连接失败（将继续监听）：{ex.Message}");
                continue;
            }

            // 每条连接各跑各的，互不阻塞（页面会并发发 GET 与 PUT）。
            _ = Task.Run(() => ServeAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;

                using var stream = client.GetStream();

                // 单条连接最多活 20 秒：防"连上来不发请求"把连接槽占住。
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));

                MiniResponse response;

                try
                {
                    var request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);

                    response = request is null
                        ? Text(400, "请求格式不正确（本机页面只接受最朴素的 HTTP/1.1 请求）。")
                        : Route(request);
                }
                catch (Exception ex)
                {
                    _log.Warn("Config", $"本机配置页处理请求出错：{ex.Message}");
                    response = Text(500, "本机配置页处理请求出错，详情见客户端日志。");
                }

                await WriteResponseAsync(stream, response, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 超时 / 停机：正常路径。
            }
            catch (Exception ex)
            {
                _log.Warn("Config", $"本机配置页连接异常：{ex.Message}");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 路由
    // ═══════════════════════════════════════════════════════════

    private MiniResponse Route(MiniRequest request)
    {
        // 闸门 ②：Host 头（挡 DNS 重绑定）。
        if (!IsLoopbackHost(request.Host))
        {
            _log.Warn("Config", $"本机配置页拒绝了 Host 为「{request.Host}」的请求（只接受 127.0.0.1 / localhost）。");
            return Text(403, "只接受以 127.0.0.1 或 localhost 访问的请求。");
        }

        if (request.Method == "GET" && request.Path is "/" or "/index.html")
        {
            return Html(Page().Replace(PageTokenPlaceholder, _token));
        }

        if (request.Method == "GET" && request.Path == "/api/config")
        {
            return Json(200, BuildState());
        }

        // 「接收到的数据」（2026-10-13）：**只读**，所以不需要令牌闸门 ③
        //（那个闸门守的是"改本机配置"这类写操作）。Host 头与环回绑定照旧是前提。
        if (request.Method == "GET" && request.Path == "/api/received")
        {
            return Json(200, BuildReceived());
        }

        if (request.Method == "PUT" && request.Path == "/api/config")
        {
            return Save(request);
        }

        // 「所连接的服务端地址」（2026-10-15，需求①）：**独立于业务配置**的写端点。
        // 为什么不塞进 `PUT /api/config`：那份配置是"中心下发 + 本机编辑"双向同步的，
        // 而这个地址是**纯本机**的（中心根本不知道、也不该知道）—— 混在一起会让
        // "本机改的也算未同步内容"，进而被推回中心，那是没意义的。
        if (request.Method == "PUT" && request.Path == "/api/server")
        {
            return SaveServer(request);
        }

        if (request.Method == "OPTIONS")
        {
            return new MiniResponse(204, "text/plain; charset=utf-8", Array.Empty<byte>());
        }

        return Text(404, $"未知路径：{request.Path}");
    }

    /// <summary>
    /// 保存"所连接的服务端地址"（2026-10-15，需求①）。
    /// <para>
    /// <b>只写本地覆盖文件</b>：真正的"告知当前中心 + 切换"由客户端主循环做
    /// （它才握着凭据），而且会在几秒内发生 —— 页面这里给它一句实话就行。
    /// </para>
    /// </summary>
    private MiniResponse SaveServer(MiniRequest request)
    {
        if (!string.Equals(request.Headers.GetValueOrDefault(TokenHeaderName), _token, StringComparison.Ordinal))
        {
            return Json(403, new { ok = false, message = "缺少或错误的本机令牌，请从托盘重新打开本机配置页。" });
        }

        AgentServerChangeRequest? payload;

        try
        {
            payload = ControlJson.Deserialize<AgentServerChangeRequest>(request.Body);
        }
        catch (Exception ex)
        {
            return Json(400, new { ok = false, message = $"请求正文不是合法 JSON：{ex.Message}" });
        }

        var raw = payload?.ServerUrl;

        // 留空 = **清除覆盖**（回到"按安装配置 / 广播发现"）—— 这是必要的退路：
        // 万一填错到一个连不上的地址，用户得能从这里退回来。
        if (string.IsNullOrWhiteSpace(raw))
        {
            _serverOverride.Set(null);

            return Json(200, new { ok = true, message = "已清除自定义地址，客户端将按安装配置 / 广播发现找中心。", serverUrl = (string?)null });
        }

        var normalized = AgentServerOverride.Normalize(raw);

        if (normalized is null)
        {
            return Json(400, new { ok = false, message = $"地址不合法：{raw}。请填 IP 或「IP:端口」，例如 192.168.1.10:8899。" });
        }

        _serverOverride.Set(normalized);

        return Json(200, new
        {
            ok = true,
            // ⚠️ 措辞必须说清"还没有立刻生效、也没有阻塞"：真实行为是
            //    主循环下一次心跳（≤10 秒）掐掉旧连接 → 告知旧中心 → 按新地址注册。
            message = $"已保存 {normalized}。客户端会在几秒内向当前中心告知本次变更，然后切到新地址并重新注册"
                      + "（若当前中心已不可达，会跳过告知直接切换）。",
            serverUrl = normalized,
        });
    }

    /// <summary>本机页面保存配置。</summary>
    private MiniResponse Save(MiniRequest request)
    {
        // 闸门 ③：令牌。缺了/错了就拒 —— 跨站页面拿不到它（见类注释）。
        if (!string.Equals(request.Headers.GetValueOrDefault(TokenHeaderName), _token, StringComparison.Ordinal))
        {
            return Json(403, new AgentConfigSaveResult(false, "forbidden", "缺少或错误的本机令牌，请从托盘重新打开本机配置页。", null));
        }

        AgentConfigSaveRequest? payload;

        try
        {
            payload = ControlJson.Deserialize<AgentConfigSaveRequest>(request.Body);
        }
        catch (Exception ex)
        {
            return Json(400, new AgentConfigSaveResult(false, "badjson", $"请求正文不是合法 JSON：{ex.Message}", null));
        }

        if (payload is null)
        {
            return Json(400, new AgentConfigSaveResult(false, "badjson", "请求正文不是合法 JSON。", null));
        }

        var saved = _configs.ApplyLocalEdit(payload, out var error);

        if (saved is null)
        {
            return Json(400, new AgentConfigSaveResult(false, "invalid", error ?? "校验未通过。", null));
        }

        return Json(200, new AgentConfigSaveResult(true, "ok", $"已在本机生效（v{saved.Version}）。", saved));
    }

    /// <summary>页面要的那一份"此刻状态"。</summary>
    private object BuildState()
    {
        // 连接状态从**状态文件**读：那个文件本来就是"服务写、托盘读"的共享通道，
        // 本机页面读它与托盘读它是同一个来源，不会再造一条"谁连着中心"的真相。
        var status = AgentStatusFile.Read(_stateDirectory);

        return new
        {
            version = _configs.Current?.Version ?? 0,
            updatedAt = _configs.Current?.UpdatedAt,
            origin = _configs.Origin,
            unsynced = _configs.HasUnsyncedLocalEdit,
            connected = status?.Connected ?? false,
            serverUrl = status?.ServerUrl,

            // ── 「所连接的服务端」可编辑字段（2026-10-15，需求①）──
            // serverOverride = **用户在本机页上填的地址**（null = 没填过）。
            // 它与上面那个 serverUrl 是两件事：那个是"此刻连着的"，这个是"我要连的"。
            serverOverride = _serverOverride.Url,
            configuredServerUrl = string.IsNullOrWhiteSpace(_options.ServerUrl) ? null : _options.ServerUrl,
            allowBroadcastDiscovery = _options.AllowBroadcastDiscovery,
            serverOverridePath = _serverOverride.FilePath,

            // ── 「服务端」卡片要的那几个字段（2026-10-14）──
            // 🔥 它们全都能为 null，而 null 的含义是**"还不知道"**，不是"没有"：
            //    没连上中心时地址/名称/ID 都是空的 —— 页面必须写成"未连接"，
            //    不能显示成 0 或者一个空字符串（那是"未知用 0 代替"的老毛病）。
            serverVersion = status?.ServerVersion,
            lastSeenAt = status?.LastSeenAt,
            agentId = status?.AgentId,
            displayName = status?.DisplayName,

            statusText = status?.Status,
            // 客户端版本（2026-10-13 补）：现场"指令不生效"里有一整类是
            // "客户机装的是旧版、根本没有这条能力"，页面上必须有版本号才能当场对上。
            agentVersion = AgentVersion.Current,
            config = _configs.Current,
        };
    }

    /// <summary>
    /// 「接收到的数据」那一份（2026-10-13）。
    /// <para>
    /// 它与 <see cref="BuildState"/> 分开成一个端点，是因为**刷新节奏不一样**：
    /// 配置是"改了才变"，而接收记录是"随时在变"—— 页面会对它单独、更勤地拉一次。
    /// </para>
    /// </summary>
    private object BuildReceived()
    {
        var snapshot = _received.Snapshot(
            udpListening: _udp.IsRunning,
            udpPort: _udp.Port,
            udpStateText: _udp.StateText,
            udpExecuteEnabled: _udp.ExecuteEnabled);

        return new
        {
            udpListening = snapshot.UdpListening,
            udpPort = snapshot.UdpPort,
            udpStateText = snapshot.UdpStateText,
            udpExecuteEnabled = snapshot.UdpExecuteEnabled,
            receivedCount = snapshot.ReceivedCount,
            startedAt = snapshot.StartedAt,
            capacity = ReceivedDataLog.Capacity,
            agentVersion = AgentVersion.Current,
            entries = snapshot.Entries,
        };
    }

    // ═══════════════════════════════════════════════════════════
    // 极简 HTTP/1.1
    // ═══════════════════════════════════════════════════════════

    private sealed record MiniRequest(
        string Method,
        string Path,
        string Host,
        Dictionary<string, string> Headers,
        string Body);

    private sealed record MiniResponse(int Status, string ContentType, byte[] Body);

    private static MiniResponse Text(int status, string body)
        => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(body));

    private static MiniResponse Html(string body)
        => new(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(body));

    private static MiniResponse Json(int status, object payload)
        => new(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(ControlJson.Serialize(payload)));

    private static async Task<MiniRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var collected = new MemoryStream();

        var headerEnd = -1;

        while (headerEnd < 0)
        {
            if (collected.Length >= MaxHeaderBytes) return null;

            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read <= 0) return null;

            collected.Write(buffer, 0, read);

            var so_far = collected.ToArray();
            headerEnd = IndexOfHeaderEnd(so_far, so_far.Length);
        }

        var all = collected.ToArray();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n");

        if (lines.Length == 0) return null;

        var startLine = lines[0].Split(' ');

        if (startLine.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');

            if (colon <= 0) continue;

            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        // 分块传输我们不实现 —— 明说，而不是把它当成 0 字节正文（那会静默丢配置）。
        if (headers.ContainsKey("Transfer-Encoding")) return null;

        var contentLength = 0;

        if (headers.TryGetValue("Content-Length", out var rawLength)
            && !int.TryParse(rawLength, out contentLength))
        {
            return null;
        }

        if (contentLength < 0 || contentLength > MaxBodyBytes) return null;

        var bodyStart = headerEnd + 4;
        var body = new byte[contentLength];

        var available = all.Length - bodyStart;
        var copied = Math.Max(0, Math.Min(available, contentLength));

        if (copied > 0) Array.Copy(all, bodyStart, body, 0, copied);

        var remaining = contentLength - copied;

        while (remaining > 0)
        {
            var read = await stream
                .ReadAsync(body.AsMemory(contentLength - remaining, remaining), cancellationToken)
                .ConfigureAwait(false);

            if (read <= 0) return null;

            remaining -= read;
        }

        var path = startLine[1];
        var query = path.IndexOf('?');

        if (query >= 0) path = path[..query];

        return new MiniRequest(
            startLine[0].ToUpperInvariant(),
            path,
            headers.GetValueOrDefault("Host") ?? string.Empty,
            headers,
            Encoding.UTF8.GetString(body));
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        MiniResponse response,
        CancellationToken cancellationToken)
    {
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(StatusText(response.Status)).Append("\r\n")
            .Append("Content-Type: ").Append(response.ContentType).Append("\r\n")
            .Append("Content-Length: ").Append(response.Body.Length).Append("\r\n")
            // 页面随时反映"此刻配置"，缓存它只会让人看到旧值后以为保存失败了。
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            // 不答 Access-Control-Allow-Origin：跨站页面读不到响应，也就写不了配置。
            .Append("Connection: close\r\n\r\n")
            .ToString();

        await stream.WriteAsync(Encoding.UTF8.GetBytes(head), cancellationToken).ConfigureAwait(false);

        if (response.Body.Length > 0)
        {
            await stream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>找 <c>\r\n\r\n</c> 的起点；没有则 -1。</summary>
    private static int IndexOfHeaderEnd(byte[] bytes, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (bytes[i - 3] == (byte)'\r'
                && bytes[i - 2] == (byte)'\n'
                && bytes[i - 1] == (byte)'\r'
                && bytes[i] == (byte)'\n')
            {
                return i - 3;
            }
        }

        return -1;
    }

    /// <summary>Host 头是不是"本机"（挡 DNS 重绑定）。</summary>
    private static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        var name = host.Trim();

        // 去掉端口。IPv6 字面量形如 [::1]:8091。
        if (name.StartsWith('['))
        {
            var close = name.IndexOf(']');
            if (close > 0) name = name[..(close + 1)];
        }
        else
        {
            var colon = name.IndexOf(':');
            if (colon > 0) name = name[..colon];
        }

        return name.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
               || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || name.Equals("[::1]", StringComparison.OrdinalIgnoreCase);
    }

    private static string StatusText(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        500 => "Internal Server Error",
        _ => "OK",
    };

    /// <summary>内嵌页面（只读一次）。</summary>
    private static string Page()
    {
        var assembly = typeof(AgentLocalConfigUi).Assembly;

        using var stream = assembly.GetManifestResourceStream(PageResourceName)
            ?? throw new InvalidOperationException($"找不到内嵌的本机配置页面资源：{PageResourceName}");

        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd();
    }

    public ValueTask DisposeAsync()
    {
        var listener = _listener;
        _listener = null;

        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            listener?.Stop();
        }
        catch
        {
            // 忽略。
        }

        _cts?.Dispose();
        _cts = null;

        Url = null;

        // 页面已经没人监听了，留着那个文件只会让托盘打开一个死地址。
        AgentLocalUiDiscoveryFile.Clear(_stateDirectory);

        // 循环是 Task.Run 出去的，取消 + Stop 之后它会自己退出；这里不 await
        //（停机路径上卡在这一步不值得 —— 它不做任何 IO，离开循环就结束）。
        _loop = null;

        return ValueTask.CompletedTask;
    }
}
