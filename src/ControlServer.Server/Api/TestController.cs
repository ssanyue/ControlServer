using System.Text;
using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ControlServer.Server.Api;

/// <summary>
/// 端到端连通性测试 —— M1 骨架的"细线"验证入口。
/// 浏览器点一下 → 服务端发 UDP → 设备/回环收到 → 日志经 SignalR 回推浏览器。
/// </summary>
[ApiController]
[Route("api/test")]
[Authorize(Policy = Policies.CanOperate)]
public sealed class TestController : ControllerBase
{
    private readonly NetworkService _network;
    private readonly PairingListenerService _listener;
    private readonly LogService _log;
    private readonly INotifier _notifier;

    public TestController(
        NetworkService network,
        PairingListenerService listener,
        LogService log,
        INotifier notifier)
    {
        _network = network;
        _listener = listener;
        _log = log;
        _notifier = notifier;
    }

    /// <summary>向任意目标发送一条 UDP 报文。</summary>
    [HttpPost("udp")]
    public async Task<IActionResult> SendUdp([FromBody] UdpTestRequest request, CancellationToken cancellationToken)
    {
        var runId = NewRunId();
        var payload = Encoding.UTF8.GetBytes(request.Payload ?? string.Empty);

        var result = await _network
            .SendUdpAsync(request.Host, request.Port, payload, cancellationToken)
            .ConfigureAwait(false);

        _log.Write(
            result.Ok ? LogLevelKind.Info : LogLevelKind.Error,
            "Test",
            $"UDP 测试 → {request.Host}:{request.Port}：{result.Message}");

        await _notifier
            .ActionResultAsync(new ActionResultDto(runId, result.Ok, result.Message, BeijingTime.Now), cancellationToken)
            .ConfigureAwait(false);

        return Ok(new UdpTestResponse(runId, result.Ok, result.Message, result.BytesSent, $"{request.Host}:{request.Port}"));
    }

    /// <summary>
    /// 自环测试：把报文发到本机对接码监听端口。
    /// 这是 M1 最干净的端到端证据 —— 一次请求内串起 HTTP → UDP 出 → UDP 入 → 日志 → SignalR。
    /// </summary>
    [HttpPost("loopback")]
    public async Task<IActionResult> Loopback(
        [FromQuery] string payload = "TEST-CODE-001",
        CancellationToken cancellationToken = default)
    {
        var runId = NewRunId();
        var port = _listener.Port;

        if (!_listener.IsRunning || port <= 0)
        {
            const string notRunning = "对接码监听未在运行，无法执行自环测试";
            _log.Warn("Test", notRunning);
            await _notifier
                .ActionResultAsync(new ActionResultDto(runId, false, notRunning, BeijingTime.Now), cancellationToken)
                .ConfigureAwait(false);
            return Ok(new LoopbackTestResponse(runId, false, notRunning, port, 0));
        }

        var bytes = Encoding.UTF8.GetBytes(payload);
        var result = await _network
            .SendUdpAsync("127.0.0.1", port, bytes, cancellationToken)
            .ConfigureAwait(false);

        var message = result.Ok
            ? $"自环测试已发出 {result.BytesSent}B → 127.0.0.1:{port}，请在日志面板查看对接码接收记录"
            : result.Message;

        _log.Write(result.Ok ? LogLevelKind.Info : LogLevelKind.Error, "Test", message);
        await _notifier
            .ActionResultAsync(new ActionResultDto(runId, result.Ok, message, BeijingTime.Now), cancellationToken)
            .ConfigureAwait(false);

        return Ok(new LoopbackTestResponse(runId, result.Ok, message, port, result.BytesSent));
    }

    private static string NewRunId() => Guid.NewGuid().ToString("N")[..8];
}
