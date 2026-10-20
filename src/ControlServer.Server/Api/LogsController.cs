using ControlServer.Contracts;
using ControlServer.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ControlServer.Server.Api;

[ApiController]
[Route("api/logs")]
[Authorize]
public sealed class LogsController : ControllerBase
{
    private readonly LogService _log;

    public LogsController(LogService log)
    {
        _log = log;
    }

    /// <summary>最近日志（时间正序）。默认 200 条，上限 2000。</summary>
    [HttpGet("recent")]
    public IActionResult Recent([FromQuery] int count = 200)
    {
        var take = Math.Clamp(count, 1, 2000);
        var items = _log.Recent(take)
            .Select(e => new LogDto(e.Timestamp, e.LevelText, e.Category, e.Message))
            .ToArray();

        return Ok(items);
    }
}
