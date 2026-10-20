using System.Net.Http.Json;
using ControlServer.Contracts;
using ControlServer.Core.Services;

namespace ControlServer.Agent.Config;

/// <summary>
/// 拉取本机配置（M4.5）：<c>GET /api/agents/{id}/config</c>。
/// <para>
/// <b>为什么配置走普通 HTTP 而不是走 SignalR</b>：Hub 那条路只在"连着"的时候存在，
/// 而客户端要在<b>刚注册完、还没连上</b>的时候就把配置拿到手（否则第一轮任务要等到下一次重连）。
/// HTTP 拉取与连接状态无关，且天然支持"推送丢了之后自己补拉"。
/// </para>
/// <para>
/// ⚠️ 凭据走<b>请求头</b>（<c>X-Agent-Key</c>）而不是查询串 —— 与长连接同一条纪律：
/// 查询串会被服务端的请求日志原样记下来，一次拉取就等于把 agentKey 写进服务器日志。
/// </para>
/// </summary>
public sealed class AgentConfigClient
{
    private readonly HttpClient _http;
    private readonly LogService _log;

    public AgentConfigClient(HttpClient http, LogService log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// 拉一次全量配置。
    /// </summary>
    /// <returns>成功返回配置；<b>任何失败都返回 null</b> —— 调用方据此保持现状（降级运行）。</returns>
    public async Task<AgentConfigDto?> FetchAsync(
        string serverUrl,
        int agentId,
        string agentKey,
        CancellationToken cancellationToken)
    {
        var url = serverUrl.TrimEnd('/') + $"/api/agents/{agentId}/config";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(AgentAuthentication.HeaderName, agentKey);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 404 有两种来源，这里**都按"暂无配置"处理**（返回 null，客户端保持现状）：
                //   · 该机从未保存过配置（version = 0）—— 服务端对"没配过"就是这么答的；
                //   · 该机已被删除（配置行随设备一起删）。
                // 刻意**不**把它翻译成"一份空配置去覆盖本机"：那会让"服务端一次瞬时 404"
                // 变成"客户机上的任务全被清掉"，代价不对称。真要清空任务，
                // 管理员在页面上保存一次（版本号会 +1）即可 —— 那是一条走得通的正路。
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _log.Info("Config", "服务端暂无本机配置（未配置或已删除），保持现状。");
                    return null;
                }

                _log.Warn("Config", $"拉取配置失败（HTTP {(int)response.StatusCode}）：{url}");
                return null;
            }

            var config = await response.Content
                .ReadFromJsonAsync<AgentConfigDto>(cancellationToken)
                .ConfigureAwait(false);

            if (config is null) _log.Warn("Config", "服务端返回了空的配置正文（按拉取失败处理）。");

            return config;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 不抛：配置拉不到只应表现为"继续按手上这份跑"，绝不该把连接循环带崩。
            _log.Warn("Config", $"拉取配置出错（保持现状）：{ex.Message}");
            return null;
        }
    }
}
