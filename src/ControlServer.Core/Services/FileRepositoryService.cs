using System.Security.Cryptography;
using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>发起上传的结果。</summary>
public enum FileUploadCreateStatus
{
    Ok,
    /// <summary>超过 <see cref="FileRepositoryOptions.MaxFileBytes"/>。</summary>
    TooLarge,
    /// <summary>参数不合法（大小为 0 / 负 / 文件名为空）。</summary>
    Invalid,
}

/// <summary>发起上传的结果。</summary>
public sealed record FileUploadCreateResult(
    FileUploadCreateStatus Status,
    FileUploadSession? Session,
    string? Message);

/// <summary>追加分块的结果。</summary>
public enum FileUploadAppendStatus
{
    Ok,
    /// <summary>会话不存在。</summary>
    NotFound,
    /// <summary>会话已过期 / 已关闭。</summary>
    NotOpen,
    /// <summary>
    /// 客户端给的偏移与服务端当前进度不符。
    /// <para><b>这不是错误，是续传的正常一环</b>：客户端重连后先按服务端回的
    /// <c>ReceivedBytes</c> 重新对齐即可。返回 409 让客户端能明确区分它。</para>
    /// </summary>
    OffsetMismatch,
    /// <summary>写进去会超过声明的总大小。</summary>
    TooLarge,
    /// <summary>磁盘写入失败（空间不足 / 权限）。</summary>
    WriteFailed,
}

/// <summary>追加分块的结果。</summary>
public sealed record FileUploadAppendResult(
    FileUploadAppendStatus Status,
    long ReceivedBytes,
    string? Message);

/// <summary>完成上传的结果。</summary>
public enum FileUploadCompleteStatus
{
    Ok,
    NotFound,
    /// <summary>还没收满声明的字节数。</summary>
    Incomplete,
    /// <summary>SHA256 与期望不符（内容在传输中损坏，或客户端传错了文件）。</summary>
    HashMismatch,
    WriteFailed,
}

/// <summary>完成上传的结果。</summary>
public sealed record FileUploadCompleteResult(
    FileUploadCompleteStatus Status,
    FileRepoItem? Item,
    long ReceivedBytes,
    string? Message);

/// <summary>
/// 文件仓库服务（M4.4）：<b>字节的读写在 Core（System.IO），元数据行经端口落库</b>。
/// <para>
/// 与 <c>ImageStorageService</c> 同构 —— 服务端自持目录布局，不让 EF provider 决定文件放哪。
/// </para>
/// <para>
/// <b>分块上传的状态机</b>：
/// <c>Create</c>（量 0 字节，建 <c>.part</c>）→ <c>Append×N</c>（按偏移续传）
/// → <c>Complete</c>（算摘要 → 校验 → 移入仓库 → 落元数据）→ 或 <c>Abort</c>。
/// </para>
/// <para>
/// ⚠️ <b>已收字节数的唯一事实来源是 <c>.part</c> 文件长度</b>，不是数据库里的计数器。
/// 数据库只记"这个会话存在、声明了多大、期望什么摘要"。一旦两者分开维护，
/// 它们迟早会不同步，而不同步的表现是"从错误的位置继续写" → 长度对、内容坏。
/// </para>
/// </summary>
public sealed class FileRepositoryService
{
    private readonly IFileRepositoryStore _store;
    private readonly IDataDirectory _dataDir;
    private readonly LogService _log;
    private readonly FileRepositoryOptions _options;

    public FileRepositoryService(
        IFileRepositoryStore store,
        IDataDirectory dataDir,
        LogService log,
        FileRepositoryOptions options)
    {
        _store = store;
        _dataDir = dataDir;
        _log = log;
        _options = options;
    }

    /// <summary>仓库文件的实际存放目录。</summary>
    public string RepositoryDirectory => Path.Combine(_dataDir.FilesDirectory, "repo");

    /// <summary>上传中途的 <c>.part</c> 暂存目录。</summary>
    public string StagingDirectory => Path.Combine(_dataDir.FilesDirectory, "uploads");

    /// <summary>单文件上限（供端点回显给前端）。</summary>
    public long MaxFileBytes => _options.MaxFileBytes;

    /// <summary>建议的分块大小（供端点告诉客户端）。</summary>
    public int ChunkSizeBytes => _options.ChunkSizeBytes;

    // ═══════════════════════════════════════════════════════════
    // 查询与删除
    // ═══════════════════════════════════════════════════════════

    public Task<IReadOnlyList<FileRepoItem>> ListAsync(string? kind, CancellationToken cancellationToken = default)
        => _store.ListAsync(kind, cancellationToken);

    public Task<FileRepoItem?> FindAsync(int id, CancellationToken cancellationToken = default)
        => _store.FindAsync(id, cancellationToken);

    /// <summary>仓库文件的磁盘绝对路径（<c>StoredName</c> 是服务端生成的单段名，无穿越风险）。</summary>
    public string ResolveStoredPath(FileRepoItem item)
        => Path.Combine(RepositoryDirectory, item.StoredName);

    /// <summary>删除一个仓库文件（先删元数据，再尽力删磁盘文件）。</summary>
    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var item = await _store.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (item is null) return false;

        var removed = await _store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        if (!removed) return false;

        // 元数据删掉了，磁盘文件删不掉也不该让请求失败 —— 否则运维会看到"删不掉"，
        // 但库里其实已经没有它了（磁盘上那个孤儿文件由清理任务兜底）。
        try
        {
            var path = ResolveStoredPath(item);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.Warn("Files", $"删除仓库文件时未能删除磁盘文件（Id={id}，{item.StoredName}）：{ex.Message}");
        }

        return true;
    }

    // ═══════════════════════════════════════════════════════════
    // 上传：创建 / 追加 / 完成 / 中止
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 发起一次上传。服务端立刻建一个 0 字节的 <c>.part</c> 文件，
    /// 之后所有分块都追加到它后面。
    /// </summary>
    public async Task<FileUploadCreateResult> CreateSessionAsync(
        FileUploadCreateRequest request,
        string? uploadedBy,
        int? originAgentId,
        string? forcedKind = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Size <= 0)
            return new FileUploadCreateResult(FileUploadCreateStatus.Invalid, null, "文件大小必须大于 0。");

        if (request.Size > _options.MaxFileBytes)
            return new FileUploadCreateResult(
                FileUploadCreateStatus.TooLarge,
                null,
                $"文件超过上限 {FormatBytes(_options.MaxFileBytes)}。");

        var kind = forcedKind ?? (FileKinds.IsValid(request.Kind) ? request.Kind! : FileKinds.Normal);
        var fileName = FileTransferMath.SanitizeFileName(request.FileName);
        var storedName = FileTransferMath.GenerateStoredName(fileName);
        var now = BeijingTime.Now;

        var session = new FileUploadSession(
            Id: $"u{Guid.NewGuid():N}",
            FileName: fileName,
            StoredName: storedName,
            Size: request.Size,
            Kind: kind,
            Sha256: NormalizeSha(request.Sha256),
            CreatedBy: uploadedBy,
            OriginAgentId: originAgentId,
            CreatedAt: now,
            ExpiresAt: now.AddHours(Math.Max(1, _options.SessionExpiryHours)),
            State: FileUploadStates.Open);

        try
        {
            Directory.CreateDirectory(StagingDirectory);
            // 先落一个 0 字节文件：它同时是"会话已开始"的物理凭据与后续追加的载体。
            await using (new FileStream(PartPath(session), FileMode.Create, FileAccess.Write, FileShare.None))
            {
            }
        }
        catch (Exception ex)
        {
            _log.Error("Files", $"创建上传暂存文件失败（{storedName}）", ex);
            return new FileUploadCreateResult(FileUploadCreateStatus.Invalid, null, $"无法创建暂存文件：{ex.Message}");
        }

        await _store.CreateSessionAsync(session, cancellationToken).ConfigureAwait(false);

        return new FileUploadCreateResult(FileUploadCreateStatus.Ok, session, null);
    }

    /// <summary>取会话状态（续传前客户端先调它问"收到哪了"）。</summary>
    public async Task<FileUploadSession?> FindSessionAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        var session = await _store.FindSessionAsync(uploadId, cancellationToken).ConfigureAwait(false);

        if (session is null) return null;

        // 过期判定放在读的时候做，而不是靠后台任务抢先标记 ——
        // 后台任务可能还没跑到，过期会话就会被用上。
        if (session.State == FileUploadStates.Open && session.ExpiresAt < BeijingTime.Now)
            return session with { State = FileUploadStates.Expired };

        return session;
    }

    /// <summary>会话当前已收字节数（文件不存在按 0）。</summary>
    public long ReceivedBytes(FileUploadSession session)
    {
        var info = new FileInfo(PartPath(session));
        return info.Exists ? info.Length : 0;
    }

    /// <summary>
    /// 从 <paramref name="offset"/> 处追加一段数据。
    /// <para>
    /// <b>偏移不匹配就拒绝</b>（返回当前真实长度）—— 这是"续传"的正确性基础：
    /// 若允许"按任意偏移写"，两个并发的重试就会把文件写成一段拼接怪物，
    /// 而它的长度甚至可能正好等于声明大小，从而骗过长度校验。
    /// </para>
    /// </summary>
    public async Task<FileUploadAppendResult> AppendChunkAsync(
        string uploadId,
        long offset,
        Stream data,
        CancellationToken cancellationToken = default)
    {
        var session = await FindSessionAsync(uploadId, cancellationToken).ConfigureAwait(false);

        if (session is null) return new FileUploadAppendResult(FileUploadAppendStatus.NotFound, 0, "上传会话不存在。");
        if (session.State != FileUploadStates.Open)
            return new FileUploadAppendResult(FileUploadAppendStatus.NotOpen, ReceivedBytes(session), "上传会话已过期，请重新发起。");

        var path = PartPath(session);

        try
        {
            await using var fs = new FileStream(
                path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true);

            if (fs.Length != offset)
                return new FileUploadAppendResult(FileUploadAppendStatus.OffsetMismatch, fs.Length, null);

            if (offset > session.Size)
                return new FileUploadAppendResult(FileUploadAppendStatus.TooLarge, fs.Length, "偏移超过文件总大小。");

            fs.Seek(0, SeekOrigin.End);
            await data.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            await fs.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (fs.Length > session.Size)
            {
                // 写多了：截回去，让会话保持可继续的状态（不清空重来 —— 已传的部分仍然有效）。
                fs.SetLength(offset);
                await fs.FlushAsync(cancellationToken).ConfigureAwait(false);

                return new FileUploadAppendResult(
                    FileUploadAppendStatus.TooLarge, fs.Length, "这段数据会让文件超过声明的总大小。");
            }

            return new FileUploadAppendResult(FileUploadAppendStatus.Ok, fs.Length, null);
        }
        catch (Exception ex)
        {
            _log.Error("Files", $"追加分块失败（会话 {uploadId}，偏移 {offset}）", ex);
            return new FileUploadAppendResult(FileUploadAppendStatus.WriteFailed, 0, $"写入失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 完成上传：校验大小 → 算 SHA256 → 比对（若声明过）→ 原子移入仓库 → 落元数据。
    /// <para>
    /// <b>摘要由服务端自己算，不信客户端自报</b>。客户端给的摘要只当作"期望值"来比对 ——
    /// 它的价值在于抓住"传输中损坏"，而不是用来跳过服务端的计算。
    /// </para>
    /// </summary>
    public async Task<FileUploadCompleteResult> CompleteSessionAsync(
        string uploadId,
        string? expectedSha256,
        CancellationToken cancellationToken = default)
    {
        var session = await FindSessionAsync(uploadId, cancellationToken).ConfigureAwait(false);

        if (session is null) return new FileUploadCompleteResult(FileUploadCompleteStatus.NotFound, null, 0, "上传会话不存在。");

        var partPath = PartPath(session);
        var info = new FileInfo(partPath);

        if (!info.Exists)
            return new FileUploadCompleteResult(FileUploadCompleteStatus.NotFound, null, 0, "暂存文件不存在，请重新发起上传。");

        if (info.Length != session.Size)
            return new FileUploadCompleteResult(
                FileUploadCompleteStatus.Incomplete,
                null,
                info.Length,
                $"文件尚未传完（已收 {FormatBytes(info.Length)}，应为 {FormatBytes(session.Size)}）。");

        string actualSha;
        try
        {
            actualSha = await ComputeSha256Async(partPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("Files", $"计算上传文件摘要失败（会话 {uploadId}）", ex);
            return new FileUploadCompleteResult(FileUploadCompleteStatus.WriteFailed, null, info.Length, $"计算摘要失败：{ex.Message}");
        }

        // 以创建时声明的摘要为准；完成时再给一个只有当创建时没给过时才采信（避免"完成时改口"）。
        var expected = session.Sha256 ?? NormalizeSha(expectedSha256);

        if (expected is not null && !string.Equals(expected, actualSha, StringComparison.OrdinalIgnoreCase))
        {
            // 保留会话：客户端可能是"传错了文件"，重新传一遍比重新建会话省事。
            return new FileUploadCompleteResult(
                FileUploadCompleteStatus.HashMismatch,
                null,
                info.Length,
                "文件内容校验失败（SHA256 与预期不符），请重新上传。");
        }

        var item = new FileRepoItem(
            Id: 0, // 由数据库生成
            Name: session.FileName,
            StoredName: session.StoredName,
            Size: session.Size,
            Sha256: actualSha,
            Kind: session.Kind,
            UploadedBy: session.CreatedBy,
            UploadedAt: BeijingTime.Now,
            OriginAgentId: session.OriginAgentId,
            Note: null);

        var destination = Path.Combine(RepositoryDirectory, item.StoredName);

        try
        {
            Directory.CreateDirectory(RepositoryDirectory);

            // 原子"落位"：同一卷内 Move 是元数据操作，不会出现"半个文件已经能被下载"的窗口。
            File.Move(partPath, destination, overwrite: false);
        }
        catch (Exception ex)
        {
            _log.Error("Files", $"上传文件落位失败（会话 {uploadId}）", ex);
            return new FileUploadCompleteResult(FileUploadCompleteStatus.WriteFailed, null, info.Length, $"落位失败：{ex.Message}");
        }

        FileRepoItem saved;
        try
        {
            saved = await _store.AddAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 落库失败 ⇒ 把文件挪回去，别在仓库里留一个"没有元数据的孤儿文件"。
            _log.Error("Files", $"上传文件落库失败（会话 {uploadId}），已回滚磁盘文件", ex);
            TryMoveBack(destination, partPath);
            return new FileUploadCompleteResult(FileUploadCompleteStatus.WriteFailed, null, info.Length, $"记录写入失败：{ex.Message}");
        }

        await _store.DeleteSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);

        _log.Info("Files", $"文件已入库：{saved.Name}（{FormatBytes(saved.Size)}，{saved.Kind}，id={saved.Id}）");

        return new FileUploadCompleteResult(FileUploadCompleteStatus.Ok, saved, saved.Size, null);
    }

    /// <summary>中止上传（删掉暂存文件与会话）。</summary>
    public async Task<bool> AbortSessionAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        var session = await _store.FindSessionAsync(uploadId, cancellationToken).ConfigureAwait(false);
        if (session is null) return false;

        TryDelete(PartPath(session));
        await _store.DeleteSessionAsync(uploadId, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>清理过期会话及其暂存文件。返回清理数量。</summary>
    public async Task<int> PurgeExpiredSessionsAsync(CancellationToken cancellationToken = default)
    {
        var zombies = await _store.ListZombieSessionsAsync(BeijingTime.Now, cancellationToken).ConfigureAwait(false);
        var purged = 0;

        foreach (var session in zombies)
        {
            TryDelete(PartPath(session));

            if (await _store.DeleteSessionAsync(session.Id, cancellationToken).ConfigureAwait(false)) purged++;
        }

        return purged;
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    private string PartPath(FileUploadSession session) => Path.Combine(StagingDirectory, session.StoredName + ".part");

    private static string? NormalizeSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return null;

        var trimmed = sha.Trim().ToLowerInvariant();

        // 只接受 64 位 hex：否则一并当成"没给"，免得拿一个残缺的值去比对、把好文件判成坏的。
        if (trimmed.Length != 64) return null;
        foreach (var ch in trimmed)
        {
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return null;
        }

        return trimmed;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.Warn("Files", $"删除暂存文件失败（{path}）：{ex.Message}");
        }
    }

    private void TryMoveBack(string from, string to)
    {
        try
        {
            if (File.Exists(from)) File.Move(from, to, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warn("Files", $"回滚文件失败（{from} → {to}）：{ex.Message}");
        }
    }

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024d, mb = kb * 1024, gb = mb * 1024;

        if (bytes >= gb) return $"{bytes / gb:0.##} GB";
        if (bytes >= mb) return $"{bytes / mb:0.##} MB";
        if (bytes >= kb) return $"{bytes / kb:0.##} KB";
        return $"{bytes} B";
    }
}
