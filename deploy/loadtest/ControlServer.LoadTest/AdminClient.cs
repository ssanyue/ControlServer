using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace ControlServer.LoadTest;

/// <summary>
/// 走<b>管理员那条路</b>（HTTP + Cookie）驱动中心：登录、下发命令、建/关远控会话。
///
/// <para>
/// 🔥 <b>为什么命令要从 HTTP 打进去，而不是让假 Agent 自己"假装收到了"</b>：
/// 那样测的是假 Agent 的计数器，不是中心的下发路径。这里打的是
/// <c>POST /api/agents/{id}/reboot</c> —— 与网页上点那个按钮<b>完全同一条路</b>
/// （授权策略、UDP 兜底判断、SignalR 定向发送都在里面）。
/// 假 Agent 只是在另一端把"真的收到没有"数出来。
/// </para>
///
/// <para>
/// ⚠️ <b>它会对假机器发"重启 Agent 服务"指令</b>。假 Agent 只计数、不执行，
/// 所以对这台机器没有副作用；但如果把 <c>--server</c> 指到了真实现场，
/// 那就是真的让 100 台机器重启服务。目标必须显式给（见 <see cref="LoadTestOptions"/>）。
/// </para>
/// </summary>
public sealed class AdminClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public AdminClient(string server, string user, string password)
    {
        _http = new HttpClient(new HttpClientHandler
        {
            // 🔥 必须是**同一个**容器：登录拿到的票要能被后面的请求与假观看端一起复用。
            //    两个容器各管各的，症状是"登录成功、下一个请求 401"。
            CookieContainer = Cookies,
            UseCookies = true,
            // 局域网自签 https：压测实例通常只开 http，但真机演练可能开着 https。
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        })
        {
            BaseAddress = new Uri(server.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };

        User = user;
        Password = password;
    }

    private string User { get; }
    private string Password { get; }

    /// <summary>登录。<b>每轮只调一次</b> —— 登录端点有按 IP 的限速（20 次/5 分钟）。</summary>
    public async Task<(bool Ok, string? Message)> LoginAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http
                .PostAsJsonAsync("api/auth/login", new LoginRequest(User, Password), Json, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode) return (true, null);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (false, $"HTTP {(int)response.StatusCode}：{Trim(body)}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 下发一条「重启 Agent 服务」指令，返回<b>中心这一侧的</b>耗时。
    /// <para>200 = 中心已经把它交给长连接；409 = 那台机器没有长连接（对压测来说是失败）。</para>
    /// </summary>
    public async Task<(bool Ok, double Ms, string? Message)> RebootAsync(int agentId, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            using var response = await _http
                .PostAsync($"api/agents/{agentId}/reboot", content: null, cancellationToken)
                .ConfigureAwait(false);

            watch.Stop();

            if (response.IsSuccessStatusCode) return (true, watch.Elapsed.TotalMilliseconds, null);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (false, watch.Elapsed.TotalMilliseconds, $"HTTP {(int)response.StatusCode}：{Trim(body)}");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return (false, watch.Elapsed.TotalMilliseconds, ex.Message);
        }
    }

    /// <summary>建一路远控会话（中心登记 + 给假 Agent 下发 start）。</summary>
    public async Task<(RemoteSessionDto? Session, string? Message)> StartSessionAsync(
        int agentId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http
                .PostAsJsonAsync("api/remote/sessions", new RemoteSessionRequest(agentId), Json, cancellationToken)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return (null, $"HTTP {(int)response.StatusCode}：{Trim(body)}");

            return (JsonSerializer.Deserialize<RemoteSessionDto>(body, Json), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>结束一路会话（不发也行，中心有超时清扫；但正常收场更好看日志）。</summary>
    public async Task StopSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http
                .DeleteAsync($"api/remote/sessions/{sessionId}", cancellationToken)
                .ConfigureAwait(false);

            _ = response.StatusCode;
        }
        catch
        {
            // 收尾失败不影响统计。
        }
    }

    /// <summary>
    /// 假观看端要用的 Cookie 串（<c>/hubs/remote</c> 走 <c>AdminOnly</c>，靠这张票）。
    /// <para>
    /// ⚠️ SignalR 的 WebSocket 握手不走 <see cref="HttpClientHandler"/>，
    /// 必须把 Cookie 显式塞进 <c>HttpConnectionOptions.Cookies</c>，
    /// 否则表现是"HTTP 请求都 200，Hub 一连就 401"。
    /// </para>
    /// </summary>
    public CookieContainer Cookies { get; } = new();

    /// <summary>健康检查（每轮开头确认中心真的在跑）。</summary>
    public async Task<double?> PingAsync(CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            using var response = await _http.GetAsync("healthz", cancellationToken).ConfigureAwait(false);
            watch.Stop();

            return response.IsSuccessStatusCode ? watch.Elapsed.TotalMilliseconds : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Trim(string text) => text.Length <= 200 ? text : text[..200] + "…";

    public void Dispose() => _http.Dispose();
}
