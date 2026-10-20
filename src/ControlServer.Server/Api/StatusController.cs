using ControlServer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ControlServer.Server.Api;

[ApiController]
[Route("api/status")]
[Authorize]
public sealed class StatusController : ControllerBase
{
    private readonly StatusProvider _statusProvider;

    public StatusController(StatusProvider statusProvider)
    {
        _statusProvider = statusProvider;
    }

    [HttpGet]
    public IActionResult Get() => Ok(_statusProvider.Create());
}
