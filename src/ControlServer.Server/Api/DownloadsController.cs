using ControlServer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ControlServer.Server.Api;

/// <summary>
/// 下载端点（M4.1）。
/// <para>
/// 目前只有一个东西：<b>客户端安装包</b>。它是现场工程师第一次接触本系统最需要的入口，
/// 因此挂在顶栏右上角（见 <c>web/src/App.vue</c> 的 <c>.topbar-right</c>）。
/// </para>
/// <para>
/// <b>为什么走 <c>PhysicalFile</c> + <c>enableRangeProcessing</c></b>：
/// 安装包几十 MB，支持 Range 就意味着断点续传、下载器接管、以及"网断了一下不用重来"。
/// 这是几行代码换来的体验差异，没有理由不做。
/// </para>
/// </summary>
[ApiController]
[Route("api/downloads")]
[Authorize]
public sealed class DownloadsController : ControllerBase
{
    private readonly AgentInstallerLocator _installer;

    public DownloadsController(AgentInstallerLocator installer)
    {
        _installer = installer;
    }

    /// <summary>
    /// 客户端安装包的元信息（顶栏按钮据此决定"能不能点 / 要不要给黄点"）。
    /// </summary>
    [HttpGet("agent-installer/info")]
    public IActionResult InstallerInfo() => Ok(_installer.Locate());

    /// <summary>下载客户端安装包（支持断点续传）。</summary>
    [HttpGet("agent-installer")]
    public IActionResult DownloadInstaller()
    {
        var info = _installer.Locate();

        if (!info.Available || info.FileName is null)
        {
            // 404 + 可读原因：前端把这个 message 直接显示在按钮的 tooltip 上，
            // 管理员因此知道"该把文件放到哪个目录"，而不是看到一个静默失效的按钮。
            return NotFound(new { message = info.Hint ?? "当前没有可下载的客户端安装包。" });
        }

        // 文件名来自 GetFiles 的枚举结果（不是用户输入），不存在路径穿越。
        var path = Path.Combine(_installer.DirectoryPath, info.FileName);

        if (!System.IO.File.Exists(path))
        {
            return NotFound(new { message = $"安装包已不存在：{info.FileName}" });
        }

        return PhysicalFile(
            path,
            "application/octet-stream",
            info.FileName,
            enableRangeProcessing: true);
    }
}
