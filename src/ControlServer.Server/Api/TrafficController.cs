using ControlServer.Contracts;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ControlServer.Server.Api;

/// <summary>
/// 收发日志端点（M2-F）。
/// <para>
/// 数据源是 <see cref="TrafficLogService"/> 的内存环形缓冲（400 条）——
/// 落盘、按日期查询、保留期裁剪属 <b>M2-E</b>，本轮刻意不做：现在开一个"看起来能查历史、
/// 实际只查得到内存里 400 条"的接口，会让前端做出错误的交互假设。
/// </para>
/// </summary>
[ApiController]
[Route("api/traffic")]
[Authorize]
public sealed class TrafficController : ControllerBase
{
    private readonly TrafficLogService _traffic;

    public TrafficController(TrafficLogService traffic)
    {
        _traffic = traffic;
    }

    /// <summary>最近收发日志（时间正序）。默认 200 条，上限 400（环形缓冲容量）。</summary>
    [HttpGet("recent")]
    public IActionResult Recent([FromQuery] int count = 200)
    {
        var capacity = TrafficLogService.DefaultCapacity;
        var take = Math.Clamp(count, 1, capacity);

        var items = _traffic.Recent(take).Select(ToDto).ToArray();
        return Ok(items);
    }

    /// <summary>当前缓冲里的条数（前端显示「最近日志（N）」用，避免为了计数拉全量）。</summary>
    [HttpGet("count")]
    public IActionResult Count() => Ok(new { count = _traffic.Count, capacity = TrafficLogService.DefaultCapacity });

    /// <summary>清空缓冲（不影响配置与运维日志文件）。</summary>
    [HttpDelete]
    [Authorize(Policy = Policies.CanOperate)]
    public IActionResult Clear()
    {
        _traffic.Clear();
        return Ok(new { count = _traffic.Count });
    }

    private static TrafficLogDto ToDto(TrafficLogEntry e)
        => new(e.Time, e.Direction, e.Protocol, e.TargetIp, e.Port, e.Data, e.Success, e.Message, e.ButtonName, e.ActionName);
}
