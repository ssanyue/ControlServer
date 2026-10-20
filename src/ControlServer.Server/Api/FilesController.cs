using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using ControlServer.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace ControlServer.Server.Api;

/// <summary>
/// 文件仓库（M4.4）：上传（分块可续传）/ 列表 / 下载（Range）/ 删除。
/// <para>
/// <b>为什么上传要走分块协议而不是一次 multipart</b>：验收要求"&gt;2GB 文件断点续传"。
/// 一次性 multipart 要么被 <c>MultipartBodyLengthLimit</c> 拦死，要么把整个文件读进内存 ——
/// 两条路都不通。分块之后，客户端只需记住"已传多少字节"，断线重连从偏移处接着传。
/// </para>
/// <para>
/// <b>两种身份都能用</b>（见 <see cref="Policies.FileRead"/> / <see cref="Policies.FileIngest"/>）：
/// 人（Cookie）在网页上操作；客户机（AgentKey）在"下发"时用 Range 拉、"回传"时上传。
/// 共用同一套端点，是为了让 Range / 校验 / 原子落地这些逻辑只有一份实现。
/// </para>
/// <para>
/// ⚠️ 路由<b>不带前导斜杠</b>：授权闸门按模板 <c>StartsWith("api/")</c> 判定，
/// 带斜杠会让它静默判不中（详见 <c>Routes</c> 的注释）。
/// </para>
/// </summary>
[ApiController]
[Route("api/files")]
public sealed class FilesController : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly FileRepositoryService _files;
    private readonly IAgentHostStore _hosts;
    private readonly LogService _log;

    public FilesController(FileRepositoryService files, IAgentHostStore hosts, LogService log)
    {
        _files = files;
        _hosts = hosts;
        _log = log;
    }

    // ═══════════════════════════════════════════════════════════
    // 列表与删除（网页）
    // ═══════════════════════════════════════════════════════════

    /// <summary>仓库列表（按上传时间倒序；可用 <paramref name="kind"/> 过滤）。</summary>
    [HttpGet]
    [Authorize(Policy = Policies.CanOperate)]
    public async Task<IActionResult> List([FromQuery] string? kind, CancellationToken cancellationToken)
    {
        var filter = FileKinds.IsValid(kind) ? kind : null;

        var items = await _files.ListAsync(filter, cancellationToken).ConfigureAwait(false);

        var dtos = new List<FileRepoItemDto>(items.Count);

        foreach (var item in items)
        {
            dtos.Add(await ToDtoAsync(item, cancellationToken).ConfigureAwait(false));
        }

        return Ok(new FileRepoListDto(dtos, dtos.Count, dtos.Sum(x => x.Size)));
    }

    /// <summary>删除一个仓库文件（不影响已经分发到客户机上的副本）。</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var removed = await _files.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

        if (!removed) return NotFound(new { message = "仓库里没有这个文件。" });

        _log.Info("Files", $"仓库文件已删除（Id={id}，操作者 {User.Identity?.Name ?? "unknown"}）");

        return Ok(new { ok = true, status = "ok", message = "已删除。" });
    }

    // ═══════════════════════════════════════════════════════════
    // 下载（Range 断点续传；网页与客户机共用）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 下载一个仓库文件，<b>支持 Range</b>（断点续传 / 下载器接管）。
    /// <para>客户机"下发"时的字节就是从这里拉走的 —— 它带 <c>Range</c> 头从上次断点续传。</para>
    /// </summary>
    [HttpGet("{id:int}/download")]
    [Authorize(Policy = Policies.FileRead)]
    public async Task<IActionResult> Download(int id, CancellationToken cancellationToken)
    {
        var item = await _files.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (item is null) return NotFound(new { message = "仓库里没有这个文件。" });

        var path = _files.ResolveStoredPath(item);

        if (!System.IO.File.Exists(path))
        {
            // 元数据在、字节没了（被手工删掉 / 迁移丢文件）。明确说出来，别返回一个 0 字节的 200。
            _log.Warn("Files", $"仓库文件缺失于磁盘：Id={id}，{item.StoredName}");
            return NotFound(new { message = "文件元数据存在，但磁盘上的文件已丢失。" });
        }

        var contentType = ContentTypes.TryGetContentType(item.Name, out var detected)
            ? detected
            : "application/octet-stream";

        return PhysicalFile(path, contentType, item.Name, enableRangeProcessing: true);
    }

    // ═══════════════════════════════════════════════════════════
    // 上传：创建会话 → 追加分块 → 完成
    // ═══════════════════════════════════════════════════════════

    /// <summary>发起一次上传，返回会话 id 与建议分块大小。</summary>
    [HttpPost("uploads")]
    [Authorize(Policy = Policies.FileIngest)]
    public async Task<IActionResult> CreateUpload(
        [FromBody] FileUploadCreateRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { message = "请求体不能为空。" });

        var agentId = AgentKeyAuthenticationHandler.ReadAgentId(User);

        // 客户机发起的一律是"回传"；用户发起按请求里的 kind（缺省 normal）。
        // ⚠️ 不让客户机自称 kind：否则它能把回传伪装成 installer / normal。
        var forcedKind = agentId is not null ? FileKinds.Collected : null;

        var result = await _files
            .CreateSessionAsync(
                request,
                uploadedBy: agentId is null ? User.Identity?.Name : null,
                originAgentId: agentId,
                forcedKind: forcedKind,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Status == FileUploadCreateStatus.Ok && result.Session is { } session)
        {
            return Ok(new FileUploadSessionDto(
                session.Id,
                session.FileName,
                session.Size,
                session.Kind,
                0,
                _files.ChunkSizeBytes,
                session.State));
        }

        return result.Status switch
        {
            FileUploadCreateStatus.TooLarge => StatusCode(
                StatusCodes.Status413PayloadTooLarge,
                new { message = result.Message }),
            _ => BadRequest(new { message = result.Message }),
        };
    }

    /// <summary>
    /// 查会话当前进度（续传前先问"收到哪了"）。
    /// <para>返回的 <c>receivedBytes</c> 是客户端该用的下一个偏移。</para>
    /// </summary>
    [HttpGet("uploads/{uploadId}")]
    [Authorize(Policy = Policies.FileIngest)]
    public async Task<IActionResult> UploadStatus(string uploadId, CancellationToken cancellationToken)
    {
        var session = await _files.FindSessionAsync(uploadId, cancellationToken).ConfigureAwait(false);
        if (session is null) return NotFound(new { message = "上传会话不存在或已清理。" });

        return Ok(new FileUploadSessionDto(
            session.Id,
            session.FileName,
            session.Size,
            session.Kind,
            _files.ReceivedBytes(session),
            _files.ChunkSizeBytes,
            session.State));
    }

    /// <summary>
    /// 从 <c>?offset=</c> 处追加一段原始字节（请求体即分块内容）。
    /// <para>
    /// 偏移与服务端当前进度不符时返回 <b>409</b>，响应体里带上真实的 <c>receivedBytes</c> ——
    /// 客户端据此重新对齐后继续，而不是从头再来。
    /// </para>
    /// </summary>
    [HttpPut("uploads/{uploadId}")]
    [Authorize(Policy = Policies.FileIngest)]
    public async Task<IActionResult> AppendChunk(
        string uploadId,
        [FromQuery] long offset,
        CancellationToken cancellationToken)
    {
        var result = await _files
            .AppendChunkAsync(uploadId, offset, Request.Body, cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            FileUploadAppendStatus.Ok => Ok(new { ok = true, receivedBytes = result.ReceivedBytes }),

            // 409 + 当前进度：这不是错误，是"你该从这儿接着传"。
            FileUploadAppendStatus.OffsetMismatch => Conflict(new { receivedBytes = result.ReceivedBytes }),

            FileUploadAppendStatus.NotFound => NotFound(new { message = result.Message ?? "上传会话不存在。" }),

            FileUploadAppendStatus.NotOpen => StatusCode(
                StatusCodes.Status409Conflict,
                new { message = result.Message, receivedBytes = result.ReceivedBytes }),

            FileUploadAppendStatus.TooLarge => StatusCode(
                StatusCodes.Status413PayloadTooLarge,
                new { message = result.Message, receivedBytes = result.ReceivedBytes }),

            _ => StatusCode(
                StatusCodes.Status500InternalServerError,
                new { message = result.Message ?? "写入失败。" }),
        };
    }

    /// <summary>完成上传（服务端校验大小 + 自算 SHA256 + 原子移入仓库）。</summary>
    [HttpPost("uploads/{uploadId}/complete")]
    [Authorize(Policy = Policies.FileIngest)]
    public async Task<IActionResult> CompleteUpload(
        string uploadId,
        [FromBody] FileUploadCompleteRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _files
            .CompleteSessionAsync(uploadId, request?.Sha256, cancellationToken)
            .ConfigureAwait(false);

        if (result.Status == FileUploadCompleteStatus.Ok && result.Item is { } item)
        {
            var agentId = AgentKeyAuthenticationHandler.ReadAgentId(User);

            _log.Info("Files",
                $"文件上传完成：{item.Name}（{item.Size} B，{item.Kind}）"
                + (agentId is { } a ? $"，来自客户机 {a}" : $"，操作者 {User.Identity?.Name ?? "unknown"}"));

            return Ok(new FileUploadResultDto(item.Id, item.Name, item.Size, item.Sha256, item.Kind));
        }

        return result.Status switch
        {
            FileUploadCompleteStatus.NotFound => NotFound(new { message = result.Message }),
            FileUploadCompleteStatus.HashMismatch => StatusCode(
                StatusCodes.Status409Conflict,
                new { message = result.Message, receivedBytes = result.ReceivedBytes }),
            FileUploadCompleteStatus.Incomplete => StatusCode(
                StatusCodes.Status409Conflict,
                new { message = result.Message, receivedBytes = result.ReceivedBytes }),
            _ => StatusCode(
                StatusCodes.Status500InternalServerError,
                new { message = result.Message ?? "上传完成失败。" }),
        };
    }

    /// <summary>中止上传（客户端主动放弃时调用；过期会话由后台清理兜底）。</summary>
    [HttpDelete("uploads/{uploadId}")]
    [Authorize(Policy = Policies.FileIngest)]
    public async Task<IActionResult> AbortUpload(string uploadId, CancellationToken cancellationToken)
    {
        var removed = await _files.AbortSessionAsync(uploadId, cancellationToken).ConfigureAwait(false);

        return removed
            ? Ok(new { ok = true })
            : NotFound(new { message = "上传会话不存在或已清理。" });
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 领域模型 → DTO。回传文件的 <c>OriginAgentId</c> 顺带解析成显示名，
    /// 免得前端为了显示"来自哪台机器"再去拉一次在线表。
    /// </summary>
    private async Task<FileRepoItemDto> ToDtoAsync(FileRepoItem item, CancellationToken cancellationToken)
    {
        var uploadedBy = item.UploadedBy;

        if (item.OriginAgentId is { } agentId && uploadedBy is null)
        {
            var host = await _hosts.FindAsync(agentId, cancellationToken).ConfigureAwait(false);
            uploadedBy = host?.DisplayName ?? $"客户机 #{agentId}";
        }

        return new FileRepoItemDto(
            item.Id,
            item.Name,
            item.Size,
            item.Sha256,
            item.Kind,
            uploadedBy,
            item.UploadedAt,
            item.OriginAgentId,
            item.Note);
    }
}
