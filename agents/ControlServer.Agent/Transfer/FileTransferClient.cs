using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ControlServer.Contracts;
using ControlServer.Core.Services;

namespace ControlServer.Agent.Transfer;

/// <summary>
/// 一次传输失败。<see cref="Fatal"/> 表示"再试也没用"（越界 / 文件不存在 / 服务端明确拒绝），
/// 工作线程据此决定是重试还是直接上报失败。
/// </summary>
public sealed class TransferException : Exception
{
    public TransferException(string message, bool fatal, Exception? inner = null)
        : base(message, inner) => Fatal = fatal;

    /// <summary>是否致命（不可重试）。</summary>
    public bool Fatal { get; }
}

/// <summary>一次传输成功后的结果。</summary>
/// <param name="ReceivedBytes">最终字节数。</param>
/// <param name="Sha256">客户端自己算出的摘要（服务端会给它一次比对）。</param>
public readonly record struct TransferOutcome(long ReceivedBytes, string? Sha256);

/// <summary>
/// 服务端上传应答里我们真正用到的字段（其余忽略）。字段名与 DTO 对齐，故可为 null 表示"没给"。
/// </summary>
internal sealed record UploadAckBody(long? ReceivedBytes, string? Message = null);

/// <summary>通用错误体（只取 <c>message</c>）。</summary>
internal sealed record ErrorBody(string? Message);

/// <summary>
/// 客户机侧的字节搬运工（M4.4）：<b>下载（下发）用 Range 续传，上传（回传）用分块追加</b>。
/// <para>
/// <b>为什么字节不走 SignalR</b>：SignalR 是"控制信令"通道 —— 把 2GB 塞进消息帧里，
/// 既会撑爆服务端缓冲区，也会让心跳被大流量挤得判成离线。所以分工是
/// <b>SignalR 只说"去传哪个文件"，HTTP 负责搬字节</b>（方案 §7.2）。
/// </para>
/// <para>
/// 🔥 <b>断点续传的偏移以"磁盘上那个 <c>.cs-part</c> 有多长"为唯一事实来源</b>，
/// 而不是内存里的计数器 —— 进程被掐掉之后计数器就没了，而文件长度还在。
/// 这与服务端 <c>FileRepositoryService</c> 用 <c>.part</c> 长度作进度是同一条纪律。
/// </para>
/// <para>
/// ⚠️ <b>本类只对"写"有边界</b>：下发落地必须命中本机可写白名单（<see cref="ResolveInboxDirectory"/>）。
/// <b>回传（读）没有白名单、不限制目录</b>（2026-10-15 用户拍板，见 <see cref="TransferOptions"/>），
/// 只校验"路径合法 + 文件真实存在"。
/// 而"这台机器有没有被吊销"由上层（<c>TransferWorker</c> / <c>AgentRuntime</c>）把关。
/// </para>
/// </summary>
public sealed class FileTransferClient
{
    /// <summary>拷贝缓冲（1 MB）。够大以摊薄系统调用，够小以让进度条不至于一跳一大格。</summary>
    private const int CopyBufferBytes = 1 << 20;

    /// <summary>总大小未知时的进度回报间隔（字节）。</summary>
    private const long UnknownTotalReportStep = 8L * 1024 * 1024;

    /// <summary>回传时允许"被服务端纠正偏移"的最大次数（防止死循环）。</summary>
    private const int MaxRewinds = 5;

    private readonly HttpClient _control;
    private readonly HttpClient _transfer;
    private readonly string _serverUrl;
    private readonly string _agentKey;
    private readonly TransferOptions _options;
    private readonly string _stateDirectory;
    private readonly LogService _log;

    /// <summary>
    /// <para>
    /// <b>为什么是两个 <see cref="HttpClient"/></b>：控制类调用（补领 / 进度 / 完成 / 建会话）
    /// 必须有一个短超时 —— 挂住的回报会让工作线程卡死；而搬字节那条路不能有任何总超时
    /// （2GB 在慢链路上跑十几分钟是正常的），只能靠调用方的取消令牌来中止。
    /// 把两者塞进同一个客户端，就得在"某一类调用被误杀"之间二选一。
    /// </para>
    /// </summary>
    /// <param name="control">控制面客户端（短超时）。</param>
    /// <param name="transfer">字节面客户端（无总超时）。</param>
    public FileTransferClient(
        HttpClient control,
        HttpClient transfer,
        string serverUrl,
        string agentKey,
        TransferOptions options,
        string stateDirectory,
        LogService log)
    {
        _control = control;
        _transfer = transfer;
        _serverUrl = serverUrl.TrimEnd('/');
        _agentKey = agentKey;
        _options = options;
        _stateDirectory = stateDirectory;
        _log = log;
    }

    /// <summary>中心服务地址（排障用）。</summary>
    public string ServerUrl => _serverUrl;

    /// <summary>本机此刻生效的"下发落地白名单"（供日志与排障显示）。</summary>
    public IReadOnlyList<string> InboxWhitelist => BuildInboxWhitelist();

    // ═══════════════════════════════════════════════════════════
    // 控制面：补领 / 回报
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 补领本机未完成的传输条目。
    /// <para>
    /// <b>这一步是"离线机器也能收到文件"的落地</b>：任务创建时对离线机器推不动 SignalR，
    /// 但明细已经建好了（<c>pending</c>）。机器上线后调它就拿到了。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<AgentTransferTaskDto>> ListPendingAsync(CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, "/api/agents/transfers");
        using var response = await _control.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new TransferException($"补领待办失败（HTTP {(int)response.StatusCode}）。", fatal: false);

        var list = await response.Content
            .ReadFromJsonAsync<List<AgentTransferTaskDto>>(cancellationToken)
            .ConfigureAwait(false);

        return list ?? new List<AgentTransferTaskDto>();
    }

    /// <summary>回报单条传输的进度。<b>404 视为致命</b>（条目已被清理，再传也没人接）。</summary>
    public async Task ReportProgressAsync(
        int itemId,
        long receivedBytes,
        long totalBytes,
        string? error,
        CancellationToken cancellationToken)
    {
        using var request = NewJsonRequest(
            HttpMethod.Post,
            $"/api/agents/transfers/{itemId}/progress",
            new AgentTransferProgressRequest(receivedBytes, totalBytes, error));

        using var response = await _control.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new TransferException("这个传输条目已不存在（可能已被清理）。", fatal: true);

        if (!response.IsSuccessStatusCode)
            throw new TransferException($"进度回报失败（HTTP {(int)response.StatusCode}）。", fatal: false);
    }

    /// <summary>回报单条传输的最终结果。</summary>
    public async Task ReportCompleteAsync(
        int itemId,
        bool ok,
        long receivedBytes,
        string? sha256,
        string? error,
        CancellationToken cancellationToken)
    {
        using var request = NewJsonRequest(
            HttpMethod.Post,
            $"/api/agents/transfers/{itemId}/complete",
            new AgentTransferCompleteRequest(ok, receivedBytes, sha256, error));

        using var response = await _control.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new TransferException("这个传输条目已不存在（可能已被清理）。", fatal: true);

        if (!response.IsSuccessStatusCode)
            throw new TransferException($"结果回报失败（HTTP {(int)response.StatusCode}）。", fatal: false);
    }

    // ═══════════════════════════════════════════════════════════
    // 下发：中心 → 本机（Range 断点续传）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 下载一个仓库文件并<b>原子落地</b>到白名单目录。
    /// <para>
    /// 落地顺序刻意是：写 <c>.cs-part</c> → 校验 SHA256 → <c>File.Move</c>。
    /// 因为 <c>Move</c> 是同卷的元数据操作，所以不存在"目标目录里躺着一个下了半截的文件"
    /// 这种窗口 —— 而那个窗口一旦出现，下游的消费程序就会去处理一个坏文件。
    /// </para>
    /// </summary>
    /// <param name="onProgress">进度回调（已传、总数）。<b>总数未知时为 0</b>。</param>
    public async Task<TransferOutcome> DownloadAsync(
        AgentTransferTaskDto task,
        Func<long, long, Task> onProgress,
        CancellationToken cancellationToken)
    {
        var targetDirectory = ResolveInboxDirectory(task.TargetDir);
        var fileName = FileTransferMath.SanitizeFileName(task.FileName);
        var finalPath = Path.GetFullPath(Path.Combine(targetDirectory, fileName));

        // 二次防线：即使白名单配置写错，也绝不把文件写到目录之外。
        if (!FileTransferMath.IsInside(targetDirectory, finalPath))
            throw new TransferException($"目标路径越出白名单目录：{fileName}", fatal: true);

        Directory.CreateDirectory(targetDirectory);

        var tempPath = finalPath + ".cs-part";

        var offset = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0L;

        if (task.Size > 0 && offset > task.Size)
        {
            // 暂存比目标还长 = 同名但换了文件（上一轮留下的）。从头来。
            TryDelete(tempPath);
            offset = 0;
        }

        using var request = NewRequest(HttpMethod.Get, $"/api/files/{task.FileId}/download");

        if (offset > 0)
        {
            // 断点续传：只要后面那一段。服务端若不支持 Range 会退化成 200（全量），下面处理。
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        using var response = await _transfer
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // 服务端说"你要的那段不存在"（文件被换过 / 变短了）：丢掉暂存，从零重来。
            TryDelete(tempPath);
            throw new TransferException("服务端拒绝了续传请求（文件可能已变更），将从零重试。", fatal: false);
        }

        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.PartialContent)
            throw new TransferException($"下载失败（HTTP {(int)response.StatusCode}）。", fatal: false);

        if (offset > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            // 服务端忽略了 Range ⇒ 返回的是全量内容，暂存必须重写，否则会拼成一段怪物。
            TryDelete(tempPath);
            offset = 0;
        }

        var total = task.Size > 0 ? task.Size : ResolveTotalFromHeaders(response, offset);

        await using (var file = new FileStream(
            tempPath,
            offset == 0 ? FileMode.Create : FileMode.Append,
            FileAccess.Write,
            FileShare.None,
            CopyBufferBytes,
            useAsync: true))
        {
            await CopyAsync(response, file, offset, total, onProgress, cancellationToken).ConfigureAwait(false);
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var received = new FileInfo(tempPath).Length;

        if (total > 0 && received != total)
            throw new TransferException($"下载未完整（已收到 {received} / 应为 {total} 字节）。", fatal: false);

        // 摘要由客户端**自己算**：服务端给的值只当"期望值"比对（与"上传时服务端自算"是同一条纪律）。
        var sha = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(task.Sha256)
            && !string.Equals(task.Sha256.Trim(), sha, StringComparison.OrdinalIgnoreCase))
        {
            // 内容坏了。留着暂存只会让下一轮"从错误的位置续传"⇒ 删掉，逼一次干净的重下。
            TryDelete(tempPath);

            throw new TransferException(
                $"文件校验失败（SHA256 不一致）：期望 {Shorten(task.Sha256)}，实际 {Shorten(sha)}",
                // 非致命：重下一次多半就好了（传输期损坏是常态）。
                fatal: false);
        }

        // 原子落位：先 .cs-part → 校验通过 → Move。overwrite 是刻意的 ——
        // 重新下发同一个文件就是要覆盖旧的那份。
        File.Move(tempPath, finalPath, overwrite: true);

        return new TransferOutcome(received, sha);
    }

    // ═══════════════════════════════════════════════════════════
    // 回传：本机 → 中心（分块追加 + 服务端自算摘要）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把一个本机文件回传到仓库。
    /// <para>
    /// ⚠️ <b>源路径没有白名单限制</b>（2026-10-15 起）：只要求路径合法、文件真实存在且非空。
    /// 边界不在这里 —— 在"谁能建这张单"（<c>POST api/distributions/collect</c> 归授权闸门 + AdminOnly）。
    /// </para>
    /// <para>
    /// <b>先建会话、再分块追加</b>：服务端每收到一块就把 <c>?offset=</c> 与它自己的真实进度比对，
    /// 不一致就回 409 + 真实偏移，本机据此对齐后继续 —— 所以"这一轮里断了一次"是能续的。
    /// </para>
    /// <para>
    /// <b>摘要最后再算</b>（重读一遍源文件）：这样即使中途被服务端纠正过偏移，
    /// 交上去的也是整份文件的正确摘要，而不是一段拼接内容的摘要。
    /// </para>
    /// </summary>
    public async Task<TransferOutcome> UploadAsync(
        AgentTransferTaskDto task,
        Func<long, long, Task> onProgress,
        CancellationToken cancellationToken)
    {
        var sourcePath = ResolveSourcePath(task.SourcePath);
        var info = new FileInfo(sourcePath);

        if (!info.Exists) throw new TransferException($"源文件不存在：{sourcePath}", fatal: true);
        if (info.Length <= 0) throw new TransferException($"源文件为空：{sourcePath}", fatal: true);

        var size = info.Length;
        var fileName = FileTransferMath.SanitizeFileName(info.Name);

        // ① 建会话（客户机发起的一律落成 collected，服务端强制，这里不必也不该自称 kind）。
        var session = await CreateSessionAsync(fileName, size, cancellationToken).ConfigureAwait(false);

        // 以服务端建议的块大小为准（它知道自己的接收上限）；只做上下限夹逼。
        // 夹逼之后一定落在 int 范围内，故这里的显式转换是安全的。
        var chunkBytes = (int)Math.Clamp(
            session.ChunkSizeBytes > 0 ? session.ChunkSizeBytes : _options.UploadChunkBytes,
            64 * 1024,
            32 * 1024 * 1024);

        var offset = 0L;
        var rewinds = 0;
        var buffer = new byte[chunkBytes];

        await onProgress(0, size).ConfigureAwait(false);

        await using (var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, chunkBytes, useAsync: true))
        {
            while (offset < size)
            {
                source.Seek(offset, SeekOrigin.Begin);

                var want = (int)Math.Min(chunkBytes, size - offset);
                var got = await ReadExactAsync(source, buffer, want, cancellationToken).ConfigureAwait(false);

                if (got <= 0)
                    throw new TransferException($"读取源文件提前结束（偏移 {offset}）。", fatal: false);

                var moved = await PutChunkAsync(session.UploadId, offset, buffer, got, cancellationToken)
                    .ConfigureAwait(false);

                if (moved <= offset)
                {
                    // 服务端把我们打回去了（纠正偏移）：允许几次，多了就是死循环。
                    if (++rewinds > MaxRewinds)
                        throw new TransferException("上传被服务端反复纠正偏移，已放弃这一轮。", fatal: false);

                    _log.Warn("Transfer", $"上传偏移被服务端纠正：{offset} → {moved}（重新对齐后继续）。");
                }

                if (moved > size)
                    throw new TransferException($"服务端报告的进度 {moved} 超过文件大小 {size}。", fatal: true);

                offset = moved;
                await onProgress(offset, size).ConfigureAwait(false);
            }
        }

        // ② 完整落地：算摘要 → 让服务端完成（它会自己再算一遍并比对）。
        var sha = await ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        await CompleteUploadAsync(session.UploadId, sha, cancellationToken).ConfigureAwait(false);

        return new TransferOutcome(size, sha);
    }

    // ═══════════════════════════════════════════════════════════
    // 私有：HTTP 细节
    // ═══════════════════════════════════════════════════════════

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, _serverUrl + path);

        // 凭据走**头**不走查询串：查询串会被服务端的请求日志原样记下来（与 M4.1 同一条理由）。
        request.Headers.TryAddWithoutValidation(AgentAuthentication.HeaderName, _agentKey);

        return request;
    }

    private HttpRequestMessage NewJsonRequest<T>(HttpMethod method, string path, T body)
    {
        var request = NewRequest(method, path);
        request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<FileUploadSessionDto> CreateSessionAsync(
        string fileName,
        long size,
        CancellationToken cancellationToken)
    {
        using var request = NewJsonRequest(
            HttpMethod.Post,
            "/api/files/uploads",
            new FileUploadCreateRequest(fileName, size));

        using var response = await _control.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            var session = await response.Content
                .ReadFromJsonAsync<FileUploadSessionDto>(cancellationToken)
                .ConfigureAwait(false);

            if (session is not null && !string.IsNullOrEmpty(session.UploadId)) return session;

            throw new TransferException("服务端返回了无法解析的上传会话。", fatal: false);
        }

        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            throw new TransferException($"文件超过服务端允许的上限：{fileName}", fatal: true);

        throw new TransferException($"创建上传会话失败（HTTP {(int)response.StatusCode}）。", fatal: false);
    }

    /// <summary>送一块并返回服务端确认后的新偏移。</summary>
    private async Task<long> PutChunkAsync(
        string uploadId,
        long offset,
        byte[] buffer,
        int count,
        CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Put, $"/api/files/uploads/{uploadId}?offset={offset}");

        var content = new ByteArrayContent(buffer, 0, count);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content = content;

        using var response = await _transfer.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var ack = await TryReadAsync<UploadAckBody>(response, cancellationToken).ConfigureAwait(false);
            return ack?.ReceivedBytes ?? offset + count;
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // 409 不是错误，是"你该从这儿接着传"。对齐后继续即可。
            var ack = await TryReadAsync<UploadAckBody>(response, cancellationToken).ConfigureAwait(false);
            var real = ack?.ReceivedBytes ?? offset;

            if (real == offset)
            {
                // 偏移没变却让重传 = 会话已过期 / 已关闭（那种 409 也带 receivedBytes）。
                throw new TransferException(
                    ack?.Message ?? "服务端拒绝了这一分块（上传会话可能已过期）。",
                    fatal: false);
            }

            return real;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new TransferException("上传会话已不存在（可能已过期）。", fatal: true);

        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            throw new TransferException("这一分块会让文件超过服务端声明的总大小。", fatal: false);

        throw new TransferException($"上传分块失败（HTTP {(int)response.StatusCode}）。", fatal: false);
    }

    private async Task CompleteUploadAsync(string uploadId, string sha256, CancellationToken cancellationToken)
    {
        using var request = NewJsonRequest(
            HttpMethod.Post,
            $"/api/files/uploads/{uploadId}/complete",
            new FileUploadCompleteRequest(sha256));

        using var response = await _control.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.OK) return;

        var error = await TryReadAsync<ErrorBody>(response, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new TransferException(error?.Message ?? "上传会话已不存在。", fatal: true);

        // 409：摘要不符或还没收满。会话还在 ⇒ 允许重试（下一轮重建会话，代价可控）。
        throw new TransferException(
            error?.Message ?? $"完成上传失败（HTTP {(int)response.StatusCode}）。",
            fatal: false);
    }

    private static async Task<T?> TryReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 出错体读不出来不该盖住真正的错误 —— 调用方会退回到状态码的默认文案。
            return default;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 私有：字节搬运
    // ═══════════════════════════════════════════════════════════

    private async Task CopyAsync(
        HttpResponseMessage response,
        FileStream file,
        long offset,
        long total,
        Func<long, long, Task> onProgress,
        CancellationToken cancellationToken)
    {
        await using var source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        var buffer = new byte[CopyBufferBytes];

        var received = offset;
        var lastReported = offset;

        // 进度按"百分比步长"折算成字节；总数未知时退化成每 8MB 报一次。
        var stepPercent = Math.Clamp(_options.ProgressStepPercent, 1, 50);
        var reportStep = total > 0
            ? Math.Max(1, total * stepPercent / 100)
            : UnknownTotalReportStep;

        await onProgress(received, total).ConfigureAwait(false);

        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;

            if (received - lastReported < reportStep) continue;

            lastReported = received;

            // 先落盘再上报：上报说"已经到 N 了"，就得真的到 N 了 ——
            // 否则进程被掐掉后，服务端记着 90%、磁盘上只有 80%，而续传以磁盘为准。
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            await onProgress(received, total).ConfigureAwait(false);
        }
    }

    private static long ResolveTotalFromHeaders(HttpResponseMessage response, long offset)
    {
        // 206 会带 Content-Range（含总长）；200 只有 Content-Length（要加上续传起点）。
        if (response.Content.Headers.ContentRange?.Length is { } length && length > 0) return length;

        if (response.Content.Headers.ContentLength is { } contentLength && contentLength >= 0)
            return offset + contentLength;

        return 0;
    }

    private static async Task<int> ReadExactAsync(
        Stream stream,
        byte[] buffer,
        int count,
        CancellationToken cancellationToken)
    {
        var total = 0;

        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken)
                .ConfigureAwait(false);

            if (read <= 0) break;

            total += read;
        }

        return total;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ═══════════════════════════════════════════════════════════
    // 私有：白名单与路径
    // ═══════════════════════════════════════════════════════════

    /// <summary>下发落地白名单（默认目录排最前，它同时是"任务没指定目录"时的落点）。</summary>
    private IReadOnlyList<string> BuildInboxWhitelist()
        => TransferPaths.InboxWhitelist(_options, _stateDirectory);

    /// <summary>
    /// 把任务里的目标目录标识解析成一个可写的绝对路径。
    ///
    /// <para>
    /// 🔥 <b>2026-10-20 起：写方向不再有白名单</b>（产品决定，配合远控面板的"双侧资源管理器"）。
    /// 三种写法：① 空 ⇒ 默认目录（<c>inbox</c>）；② 绝对路径 ⇒ <b>直接使用</b>；
    /// ③ 目录名（<c>logs</c> / <c>inbox</c> 这种短标识）⇒ 仍按白名单末段名匹配 ——
    /// 保留它只是为了不让既有调用方（分发页 / 冒烟脚本 / 老任务）在升级后突然失败。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>这条放开是有代价的，不要当成"顺手松绑"</b>：
    /// Agent 通常以 LocalSystem 运行，所以"能往任意绝对路径写文件"等价于
    /// "持有管理员 Cookie 的人可以在被控机上落任意内容"（含启动目录）——
    /// 安全边界退化为<b>中心的管理员账号 + 授权闸门</b>，与"远程桌面里点几下就把文件存到任意位置"
    /// 是同一量级。因此：
    /// <list type="bullet">
    ///   <item>仍然要求<b>绝对路径</b>（相对路径会随安装方式解析到不同目录，是更难查的一类错）；</item>
    ///   <item>落盘前仍做 <see cref="FileTransferMath.IsInside"/> 的<b>文件名逃逸</b>校验
    ///         （防 <c>FileName</c> 里带 <c>..\</c> 把文件写出目标目录）；</item>
    ///   <item>目标目录不存在时会<b>创建</b>（见 <see cref="DownloadAsync"/>），所以"任意目录"含新建目录。</item>
    /// </list>
    /// </para>
    ///
    /// <para>解析不出一律拒收 —— <b>宁可让运维看到一句原因，也不让一个错拼的路径变成一次意外写</b>。</para>
    /// </summary>
    private string ResolveInboxDirectory(string? targetDir)
    {
        var allowed = BuildInboxWhitelist();

        // ① 空 = 默认目录。默认目录仍然来自配置（它就是"没指定时落哪"的定义）。
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            if (allowed.Count == 0)
                throw new TransferException("本机没有可用的下发目录（Agent:Transfer 未配置）。", fatal: true);

            return allowed[0];
        }

        var wanted = targetDir.Trim();

        // ② 绝对路径：直接采用（2026-10-20 起不再与白名单比对）。
        if (Path.IsPathFullyQualified(wanted))
        {
            var normalized = FileTransferMath.NormalizeDirectories(new[] { wanted });

            if (normalized.Count > 0) return normalized[0];

            throw new TransferException($"目标目录无法解析为有效路径：{wanted}", fatal: true);
        }

        // ③ 短标识：仍按白名单末段名匹配（兼容旧调用方）。
        foreach (var dir in allowed)
        {
            var leaf = Path.GetFileName(dir);

            if (!string.IsNullOrEmpty(leaf) && string.Equals(leaf, wanted, StringComparison.OrdinalIgnoreCase))
                return dir;
        }

        throw new TransferException(
            $"目标目录无法识别：{wanted}。请填绝对路径（如 D:\\logs），或本机已配置的目录名"
            + $"（{string.Join("、", allowed.Select(Path.GetFileName))}）。",
            fatal: true);
    }

    /// <summary>
    /// 解析回传源路径。<b>没有任何目录白名单</b>（2026-10-15 起）——
    /// 只做两件事：路径必须写得出来（合法）、必须是个绝对路径。
    /// <para>
    /// <b>为什么可以这么放开</b>：能建回传单的角色已经被两道门筛过 ——
    /// 授权闸门（<c>collect</c> 归 Gated）+ <c>AdminOnly</c>；而"哪个文件"是运维
    /// 在浏览里逐个点出来的，不是客户端或服务端自己挑的。风险面等于"管理员能读这台机器上的文件"，
    /// 与他直接坐到那台机器前是同一量级。
    /// </para>
    /// <para>
    /// ⚠️ <b>仍然要求绝对路径</b>：相对路径会被解析成"客户机进程的当前目录"，而那个目录
    /// 随安装方式（服务 / 托盘 / 调试）变化 —— 同样的配置在两台机器上取到不同的文件，
    /// 是比"取不到"更难查的一种错。
    /// </para>
    /// </summary>
    private static string ResolveSourcePath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new TransferException("回传任务没有指定源路径。", fatal: true);

        var wanted = sourcePath.Trim();

        if (!Path.IsPathFullyQualified(wanted))
            throw new TransferException($"回传源路径必须是绝对路径：{sourcePath}", fatal: true);

        try
        {
            return Path.GetFullPath(wanted);
        }
        catch (Exception ex)
        {
            throw new TransferException($"源路径无法解析：{sourcePath}（{ex.Message}）", fatal: true);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.Warn("Transfer", $"删除临时文件失败（{path}）：{ex.Message}");
        }
    }

    private static string Shorten(string? sha)
        => string.IsNullOrEmpty(sha) ? "(无)" : sha.Length <= 12 ? sha : sha[..12] + "…";
}
