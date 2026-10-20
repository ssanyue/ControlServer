using ControlServer.Core.Abstractions;

namespace ControlServer.Core.Services;

/// <summary>
/// 图片存储参数。由组合根从强类型配置映射而来 —— Core 不依赖配置框架，
/// 与 <c>TimeSyncSettings</c> / <c>TimerSchedulerOptions</c> 同一手法。
/// </summary>
/// <param name="MaxUploadBytes">单张图片上传大小上限（字节）。</param>
public sealed record ImageStoreOptions(long MaxUploadBytes);

/// <summary>上传结果状态。<see cref="ImageSaveStatus.Ok"/> 之外的每一项都要能被前端翻译成一句人话。</summary>
public enum ImageSaveStatus
{
    /// <summary>已保存。</summary>
    Ok,

    /// <summary>表单里没有文件（字段缺失 / 空文件）。</summary>
    NoFile,

    /// <summary>扩展名不在白名单内。</summary>
    UnsupportedExtension,

    /// <summary>超过大小上限。</summary>
    TooLarge,

    /// <summary>内容不是受支持的图片格式（魔数校验失败）。</summary>
    NotAnImage,

    /// <summary>落盘失败（磁盘满 / 权限不足 / 目录不可写）。</summary>
    WriteFailed,
}

/// <summary>
/// 上传结果。
/// </summary>
/// <param name="Status">结果状态。</param>
/// <param name="FileName">服务端生成的文件名（仅 <see cref="ImageSaveStatus.Ok"/> 时非空）。</param>
/// <param name="Path">
/// 落盘后的**绝对路径**（仅 <see cref="ImageSaveStatus.Ok"/> 时非空）。
/// <para>
/// ⚠️ 必须回传给前端、并由前端写进 <c>ButtonImage</c> / <c>BackgroundImagePath</c> 等字段 ——
/// 配置里这些字段存的就是**绝对路径**，而导出侧
/// （<see cref="ConfigBackupService.BuildBackup"/>）是按 <c>File.Exists(字段值)</c> 收集图片的。
/// 若前端只存一个裸文件名，<c>File.Exists("imgXXX.png")</c> 会相对**进程工作目录**求值而返回 false，
/// 于是那张图**导出时不会被打包** —— 表现为"导出→导入后按钮图没了"，且全程没有任何报错。
/// </para>
/// </param>
/// <param name="Bytes">实际写入的字节数。</param>
/// <param name="Message">面向用户的一句话。</param>
public sealed record ImageSaveResult(
    ImageSaveStatus Status,
    string? FileName,
    string? Path,
    long Bytes,
    string Message);

/// <summary>删除结果状态。</summary>
public enum ImageDeleteStatus
{
    /// <summary>已删除。</summary>
    Ok,

    /// <summary>
    /// 文件名不合法（目录穿越 / 非法字符）**或**文件不存在。
    /// <para>
    /// 两者刻意合并成同一个对外状态：区分开就等于告诉攻击者"这个路径合法但没文件"，
    /// 白白送出一份存在性探测能力（与 <c>GET</c> 图片端点的处理一致）。
    /// </para>
    /// </summary>
    NotFound,

    /// <summary>文件存在但删不掉（被占用 / 权限）。</summary>
    DeleteFailed,
}

/// <param name="Status">结果状态。</param>
/// <param name="Message">面向用户的一句话。</param>
public sealed record ImageDeleteResult(ImageDeleteStatus Status, string Message);

/// <summary>
/// 图片存储内核（M2-G1）：上传落盘 / 删除 / 安全的文件名解析。
/// <para>
/// <b>为什么放在 Core</b>：扩展名白名单、大小上限、魔数嗅探、路径归属判定全是**纯逻辑**，
/// 不依赖 ASP.NET Core。放这里才能用普通单测直接驱动（含目录穿越与伪装文件这类恶意输入），
/// 也和 <c>ConfigBackupService</c> 的位置保持一致。
/// </para>
/// <para>
/// <b>与"配置里的图片"的关系</b>：本服务只负责 <c>ImagesDirectory</c> 里的**物理文件**，
/// 不碰配置。配置里那些图片**路径字段**的增删改由 <c>ControlSettingsService</c> 负责，
/// 两者通过"文件名字符串"耦合（端点 <c>GET /api/config/images/{fileName}</c> 也按文件名匹配）。
/// </para>
/// </summary>
public sealed class ImageStorageService
{
    /// <summary>
    /// 允许上传的扩展名白名单（小写，含点）。
    /// <para>
    /// <b>刻意不含 <c>.svg</c></b>：SVG 是 XML，可以内嵌 <c>&lt;script&gt;</c> 与外部实体，
    /// 一旦被浏览器当成文档渲染就是**存储型 XSS**。位图格式没有这个能力。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    private readonly IDataDirectory _dataDir;
    private readonly LogService _log;
    private readonly ImageStoreOptions _options;

    public ImageStorageService(IDataDirectory dataDir, LogService log, ImageStoreOptions options)
    {
        _dataDir = dataDir;
        _log = log;
        _options = options;
    }

    /// <summary>单张图片大小上限（字节）。</summary>
    public long MaxUploadBytes => _options.MaxUploadBytes;

    /// <summary>本服务实际读写的图片目录。</summary>
    public string ImagesDirectory => _dataDir.ImagesDirectory;

    // ═══════════════════════════════════════════════════════════
    // 校验（纯函数，公开以便单测直接驱动）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 从客户端文件名里取出扩展名并判断是否白名单内。
    /// <para>
    /// 客户端文件名**只用来取扩展名与记日志**，永远不进路径 ——
    /// 落盘名由服务端生成（见 <see cref="SaveAsync"/>）。
    /// 因此这里对"名字本身奇不奇怪"很宽容（例如名字就是 <c>.png</c>），只关心后缀能不能对上。
    /// </para>
    /// </summary>
    /// <param name="originalFileName">客户端提供的文件名（不可信输入）。</param>
    /// <param name="extension">规范化（小写）后的扩展名；未通过时为 <c>null</c>。</param>
    public static bool TryGetAllowedExtension(string? originalFileName, out string? extension)
    {
        extension = null;

        if (string.IsNullOrWhiteSpace(originalFileName)) return false;

        // ⚠️ 实测（.NET 8）：Path.GetExtension(".png") 返回 ".png" 而**不是**空串 ——
        //    它只认"最后一个点之后的部分"，不区分那个点是不是文件名开头。
        //    所以 `.png` / `.gitignore` 这类点开头的名字会各自拿到自己的整个名字当扩展名
        //    （后者因此被白名单拒掉，前者被放行 —— 两者都无害，因为最终文件名是本服务生成的）。
        var ext = Path.GetExtension(originalFileName.Trim());
        if (string.IsNullOrEmpty(ext)) return false;

        ext = ext.ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext, StringComparer.Ordinal)) return false;

        extension = ext;
        return true;
    }

    /// <summary>
    /// 魔数嗅探：内容是否是受支持的位图格式。
    /// <para>
    /// <b>为什么扩展名校验之外还要做这一步</b>：扩展名是**客户端自称**的，
    /// 只校验扩展名等于允许把任意字节（脚本、压缩包、可执行文件）存进图片目录，
    /// 并让它以 <c>image/png</c> 的 Content-Type 被服务出去。魔数是被上传内容**自身**的性质，
    /// 伪造它需要让文件同时是一个合法图片 —— 那不是攻击面而是正常图片。
    /// </para>
    /// <para>只读取文件头，不需要完整解析，因此不会因为畸形图片而变慢或抛异常。</para>
    /// </summary>
    /// <param name="head">文件起始若干字节（建议 ≥ 12）。</param>
    public static bool LooksLikeSupportedImage(ReadOnlySpan<byte> head)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (head.Length >= 8
            && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
            && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
        {
            return true;
        }

        // JPEG: FF D8 FF
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return true;
        }

        // GIF: "GIF87a" / "GIF89a"
        if (head.Length >= 6
            && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x38
            && (head[4] == 0x37 || head[4] == 0x39) && head[5] == 0x61)
        {
            return true;
        }

        // BMP: "BM"
        if (head.Length >= 2 && head[0] == 0x42 && head[1] == 0x4D)
        {
            return true;
        }

        // WEBP: "RIFF" .... "WEBP"（第 4..8 字节是文件长度，故 WebP 标记在偏移 8）
        if (head.Length >= 12
            && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46
            && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50)
        {
            return true;
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════
    // 路径解析（两道闸，供上传 / 读取 / 删除共用）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把"单段文件名"解析成图片目录内的绝对路径，并确认该文件**确实存在**。
    /// <para>
    /// <b>两道闸缺一不可</b>（顺序即优先级）：
    /// <list type="number">
    ///   <item><see cref="ConfigBackupService.IsSafeImageKey"/> 白名单 —— 只允许单层文件名，
    ///         挡掉 <c>..\..\appsettings.json</c> 这类目录穿越；</item>
    ///   <item>解析后的绝对路径必须**仍位于 <see cref="ImagesDirectory"/> 之内** ——
    ///         即使白名单被绕过（比如将来放宽了字符集），也读不到授权文件 / 数据库。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 所有涉及"按文件名取图片"的动作（<c>GET</c> / <c>DELETE</c>）都走这一个实现 ——
    /// 三处各写一遍是安全代码最容易随时间漂移的地方（改了两处忘了第三处）。
    /// </para>
    /// </summary>
    public bool TryResolveExisting(string fileName, out string fullPath)
    {
        fullPath = string.Empty;

        if (!ConfigBackupService.IsSafeImageKey(fileName)) return false;

        var imagesRoot = Path.GetFullPath(_dataDir.ImagesDirectory);
        var candidate = Path.GetFullPath(Path.Combine(imagesRoot, fileName));

        // 末尾补分隔符，避免 "images-evil" 这类**同前缀**目录被 StartsWith 误判为在目录内。
        var rootWithSeparator = imagesRoot.EndsWith(Path.DirectorySeparatorChar)
            ? imagesRoot
            : imagesRoot + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(candidate)) return false;

        fullPath = candidate;
        return true;
    }

    // ═══════════════════════════════════════════════════════════
    // 上传
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 保存一张上传的图片。
    /// <para>
    /// <b>文件名一律由服务端生成</b>（<c>img{GUID:N}{扩展名}</c>），**完全不使用客户端提供的文件名** ——
    /// 这是从根上消灭路径穿越的做法：不可信字符串根本不参与路径拼接，
    /// 与 <see cref="ConfigBackupService"/> 导出侧的 <c>imgN.ext</c> 命名口径同源。
    /// </para>
    /// </summary>
    /// <param name="content">文件内容流（调用方负责生命周期）。</param>
    /// <param name="originalFileName">客户端提供的原始文件名 —— **只用于取扩展名与记日志**，不进路径。</param>
    /// <param name="declaredLength">客户端声明的长度（快速失败用；实际仍按流上真实读到的字节数判定）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ImageSaveResult> SaveAsync(
        Stream content,
        string? originalFileName,
        long declaredLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var limit = _options.MaxUploadBytes;

        if (!TryGetAllowedExtension(originalFileName, out var extension))
        {
            return new ImageSaveResult(
                ImageSaveStatus.UnsupportedExtension,
                null,
                null,
                0,
                $"只支持 {string.Join(" / ", AllowedExtensions)} 格式的图片。");
        }

        // 快速失败：声明长度已经超限就不必再读一遍内容。
        if (declaredLength > limit)
        {
            return new ImageSaveResult(
                ImageSaveStatus.TooLarge,
                null,
                null,
                0,
                TooLargeMessage(limit));
        }

        byte[] bytes;
        try
        {
            // 有上限地读 —— 不信任 `declaredLength`（流上真实读到的字节数才是事实）。
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];

            while (true)
            {
                var read = await content
                    .ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)
                    .ConfigureAwait(false);

                if (read <= 0) break;

                if (buffer.Length + read > limit)
                {
                    return new ImageSaveResult(ImageSaveStatus.TooLarge, null, null, buffer.Length, TooLargeMessage(limit));
                }

                buffer.Write(chunk, 0, read);
            }

            bytes = buffer.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("Config", $"上传图片：读取内容失败（{DescribeSource(originalFileName)}）—— {ex.Message}");
            return new ImageSaveResult(ImageSaveStatus.WriteFailed, null, null, 0, "读取上传内容失败，请重试。");
        }

        if (bytes.Length == 0)
        {
            return new ImageSaveResult(ImageSaveStatus.NoFile, null, null, 0, "上传的文件是空的。");
        }

        if (!LooksLikeSupportedImage(bytes))
        {
            _log.Warn("Config", $"上传图片：内容不是受支持的图片格式，已拒绝（{DescribeSource(originalFileName)}，{bytes.Length} 字节）");
            return new ImageSaveResult(
                ImageSaveStatus.NotAnImage,
                null,
                null,
                bytes.Length,
                "文件内容不是受支持的图片格式（可能只是改了扩展名）。");
        }

        var fileName = $"img{Guid.NewGuid():N}{extension}";

        try
        {
            _dataDir.EnsureCreated();
            var dest = Path.Combine(_dataDir.ImagesDirectory, fileName);

            // 原子写：先写同目录临时文件再改名。半截文件会被 <c>GET</c> 当成正常图片发出去
            // （浏览器拿到一段截断的 PNG 只能显示半张或直接裂图），比"没这个文件"更难排查。
            var temp = dest + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, dest, overwrite: false);

            _log.Info(
                "Config",
                $"上传图片：{fileName}（{bytes.Length} 字节，来源文件名 {DescribeSource(originalFileName)}）");

            return new ImageSaveResult(ImageSaveStatus.Ok, fileName, dest, bytes.Length, "上传成功。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("Config", $"上传图片：落盘失败（{fileName}）", ex);
            return new ImageSaveResult(ImageSaveStatus.WriteFailed, null, null, 0, $"保存图片失败：{ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 删除
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 删除图片目录内的一个文件。
    /// <para>
    /// ⚠️ <b>本方法不做引用检查</b> —— "这张图还被别的按钮用着吗"是**配置层**的知识，
    /// 见 <see cref="ImageReferenceScanner"/>。端点必须先问过它再决定要不要调这里。
    /// </para>
    /// </summary>
    public ImageDeleteResult Delete(string fileName)
    {
        if (!TryResolveExisting(fileName, out var full))
        {
            return new ImageDeleteResult(ImageDeleteStatus.NotFound, "图片不存在。");
        }

        try
        {
            File.Delete(full);
            _log.Info("Config", $"删除图片：{fileName}");
            return new ImageDeleteResult(ImageDeleteStatus.Ok, "已删除。");
        }
        catch (Exception ex)
        {
            _log.Warn("Config", $"删除图片失败（{fileName}）—— {ex.Message}");
            return new ImageDeleteResult(ImageDeleteStatus.DeleteFailed, $"删除失败：{ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    private static string TooLargeMessage(long limit)
        => $"图片不能超过 {limit / 1024.0 / 1024.0:0.##} MB。";

    /// <summary>
    /// 把不可信的文件名洗成一个可以安全写进日志的短串。
    /// <para>
    /// <b>为什么必须洗</b>：文件名里可以塞 <c>\r\n</c> 与伪造的时间戳前缀，
    /// 直接写日志就是**日志伪造**（在 <c>server-*.log</c> 里凭空造出一条看起来像真事件的记录）。
    /// </para>
    /// </summary>
    private static string DescribeSource(string? originalFileName)
    {
        if (string.IsNullOrWhiteSpace(originalFileName)) return "(未提供文件名)";

        var cleaned = new string(originalFileName.Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length <= 120 ? cleaned : cleaned[..120] + "…";
    }
}
