using System.Text.Json;
using ControlServer.Contracts;
using ControlServer.Core;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ControlServer.Server.Api;

/// <summary>
/// 配置端点（M2-F 收口 + M2-G1 图片）。
/// <para>
/// 覆盖面刻意最小：<b>只做界面真正需要的事</b> —— 读全量 / 写全量 / 导出 / 导入，
/// 以及图片的读 / 上传 / 删（M2-G1）。
/// 细粒度配置操作（增删某个按钮、重排、维护类清空）走
/// <see cref="ControlSettingsService"/> 已有的 <c>MutateAsync</c> 家族，等 M2-G2 再按需开端点；
/// 现在提前开一堆没人调的 REST 只会让接口面变成"设计稿的化石"。
/// </para>
/// </summary>
[ApiController]
[Route("api/config")]
public sealed class ConfigController : ControllerBase
{
    private readonly ControlSettingsService _settings;
    private readonly ConfigBackupService _backup;
    private readonly ImageStorageService _images;
    private readonly ICommandLibrary _commands;
    private readonly LogService _log;

    /// <summary>按扩展名推断 Content-Type（图片端点用）。</summary>
    private static readonly Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider ContentTypeProvider = new();

    public ConfigController(
        ControlSettingsService settings,
        ConfigBackupService backup,
        ImageStorageService images,
        ICommandLibrary commands,
        LogService log)
    {
        _settings = settings;
        _backup = backup;
        _images = images;
        _commands = commands;
        _log = log;
    }

    // ═══════════════════════════════════════════════════════════
    // 读
    // ═══════════════════════════════════════════════════════════

    /// <summary>读取全量配置（含版本号，PUT 时须回传）。</summary>
    [HttpGet]
    [Authorize]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var view = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        return Ok(ToDto(view));
    }

    // ═══════════════════════════════════════════════════════════
    // 写
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 整体覆盖保存配置。
    /// <para>
    /// <c>expectedVersion</c> 与存储不一致时返回 <b>409</b>，响应体里**带上服务端当前配置** ——
    /// 前端据此可以立刻展示"别人改了什么"或直接以最新版本重试，而不必再发一次 GET
    /// （配置越大这一步越值钱）。
    /// </para>
    /// </summary>
    [HttpPut]
    [Authorize(Policy = Policies.CanOperate)]
    public async Task<IActionResult> Put(
        [FromBody] ConfigSaveRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { message = "请求体不能为空。" });
        }

        if (request.Settings.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(new { message = "settings 必须是 JSON 对象。" });
        }

        ControlSettings? incoming;
        try
        {
            incoming = request.Settings.Deserialize<ControlSettings>(ControlJson.LenientOptions);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"配置格式无法解析：{ex.Message}" });
        }

        if (incoming is null)
        {
            return BadRequest(new { message = "settings 解析结果为空。" });
        }

        return await SaveAndReturnAsync(
            incoming,
            request.ExpectedVersion,
            request.UpdatedBy ?? User.Identity?.Name ?? "web",
            "整体保存",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>恢复出厂设置（清空按钮 / 子按钮集 / 图片与外观项）。</summary>
    [HttpPost("reset")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Reset(
        [FromBody] ConfigResetRequest? request,
        CancellationToken cancellationToken)
    {
        try
        {
            var view = await _settings
                .FactoryResetAsync(
                    request?.ExpectedVersion,
                    request?.UpdatedBy ?? User.Identity?.Name ?? "web",
                    cancellationToken)
                .ConfigureAwait(false);

            return Ok(ToDto(view));
        }
        catch (ConfigConcurrencyException ex)
        {
            return await ConflictAsync(ex, cancellationToken).ConfigureAwait(false);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 导出 / 导入
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 导出自包含单文件配置。
    /// </summary>
    /// <param name="format">
    /// <c>base64</c>（默认，单行文本，便于在浏览器里复制粘贴 / 传给同事）或
    /// <c>json</c>（明文缩进，便于人工查看与版本管理）。
    /// </param>
    [HttpGet("export")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Export(
        [FromQuery] string format = "base64",
        CancellationToken cancellationToken = default)
    {
        var view = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var backup = _backup.BuildBackup(view.Settings);

        var asJson = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        var content = asJson ? ConfigBackupService.ToJson(backup) : ConfigBackupService.ToBase64(backup);
        var encoding = asJson ? "json" : "base64";

        var bytes = System.Text.Encoding.UTF8.GetByteCount(ConfigBackupService.ToJson(backup));

        var dto = new ConfigExportDto(
            ConfigBackupService.SuggestFileName(),
            encoding,
            content,
            view.Version,
            backup.Images?.Count ?? 0,
            bytes);

        _log.Info("Config", $"导出配置：{dto.ImageCount} 张图片内嵌，{bytes} 字节（{encoding}），版本 {view.Version}");
        return Ok(dto);
    }

    /// <summary>
    /// 导入配置 —— 接受旧版 <c>.controlbak</c> 明文 JSON、服务版 base64 文本、裸配置 JSON 三种输入，自动识别。
    /// <para>
    /// <b>M3 起收紧为仅管理员</b>：导入是<b>整体覆盖</b>全站配置（按钮 / 定时 / 子按钮集 / 外观全在内），
    /// 影响的是所有使用者的界面，属于系统级操作而非"操作员改个按钮"。导出同理（它会把整份配置与内嵌图片
    /// 交给调用方）。Operator 仍可编辑按钮与指令库，但搬不动整台机器的配置。
    /// </para>
    /// </summary>
    [HttpPost("import")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Import(
        [FromBody] ConfigImportRequest? request,
        CancellationToken cancellationToken)
    {
        var parsed = ConfigBackupService.Parse(request?.Content);

        if (!parsed.Ok || parsed.Backup is null)
        {
            _log.Warn("Config", $"导入配置失败（识别为 {parsed.Format}）：{parsed.Error}");

            return BadRequest(new ConfigImportResultDto(
                Ok: false,
                Version: 0,
                Counts: new ConfigCountsDto(0, 0, 0),
                ImageCount: 0,
                IgnoredFields: parsed.IgnoredFields,
                Notes: Array.Empty<string>(),
                Message: parsed.Error ?? "导入内容无法识别。"));
        }

        // ── 空配置保护 ──────────────────────────────────────────────
        // 导入是一次"整体覆盖"。若解析结果里一个按钮 / 子按钮集都没有，而现有配置非空，
        // 那这次导入的净效果就是"一键清空全部配置" —— 而这种操作已经有明确入口（恢复出厂），
        // 不该藏在"导入一个内容不对的文件"后面。宁可报错让用户看清。
        if (IsEmptyConfig(parsed.Backup.Settings))
        {
            var current = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            if (!IsEmptyConfig(current.Settings))
            {
                _log.Warn("Config", $"拒绝导入：解析出的配置为空（格式 {parsed.Format}），现有配置非空，已阻止覆盖。");

                return BadRequest(new ConfigImportResultDto(
                    Ok: false,
                    Version: current.Version,
                    Counts: ToCounts(current.Settings),
                    ImageCount: 0,
                    IgnoredFields: parsed.IgnoredFields,
                    Notes: Array.Empty<string>(),
                    Message: "导入内容里没有任何按钮与子按钮集，而当前配置不是空的 —— 已拒绝，以免误清空。"
                             + "若确实要清空全部配置，请使用「恢复出厂设置」。"));
            }
        }

        var applied = _backup.Apply(parsed.Backup);

        try
        {
            await _settings
                .SaveAsync(
                    applied.Settings,
                    request?.ExpectedVersion,
                    request?.UpdatedBy ?? User.Identity?.Name ?? "web",
                    cancellationToken)
                .ConfigureAwait(false);

            var view = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

            var message = BuildImportMessage(parsed.Format, applied, view);
            var libraryNotes = await DescribeLibraryRefsAsync(view.Settings, cancellationToken).ConfigureAwait(false);

            _log.Info(
                "Config",
                $"导入配置成功（格式 {parsed.Format}）：按钮 {view.Settings.Buttons.Count}，" +
                $"子按钮集 {view.Settings.SubButtonSets.Count}，还原图片 {applied.RestoredImages} 张，" +
                $"忽略字段 {parsed.IgnoredFields.Count} 个，新版本 {view.Version}");

            return Ok(new ConfigImportResultDto(
                Ok: true,
                Version: view.Version,
                Counts: ToCounts(view.Settings),
                ImageCount: applied.RestoredImages,
                IgnoredFields: parsed.IgnoredFields,
                Notes: view.Notes.Concat(libraryNotes).ToArray(),
                Message: message));
        }
        catch (ConfigConcurrencyException ex)
        {
            return await ConflictAsync(ex, cancellationToken).ConfigureAwait(false);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 图片
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 读取按钮 / 子按钮 / 背景图片。
    /// <para>
    /// 配置里的图片字段存的是**服务端绝对路径**，浏览器无法直接加载，
    /// 因此必须由服务端按文件名转发一次。前端只用 <c>Path.GetFileName</c> 那一段来请求
    /// （见 <c>web/src/format.ts</c> 的 <c>imageUrl</c>），所以这里的入参**只能是单段文件名**。
    /// </para>
    /// <para>
    /// <b>安全</b>：白名单 + 目录归属两道闸，统一实现在
    /// <see cref="ImageStorageService.TryResolveExisting"/> ——
    /// 与 <c>DELETE</c> 共用同一份实现，避免"改了两处忘了第三处"。
    /// </para>
    /// <para>
    /// 配置里若引用的是服务端图片目录之外的路径（从旧版导入且图片未能还原到本机的情形），
    /// 这里会返回 404，前端自动回退到纯色瓦片。
    /// </para>
    /// </summary>
    [HttpGet("images/{fileName}")]
    [Authorize]
    public IActionResult Image(string fileName)
    {
        if (!_images.TryResolveExisting(fileName, out var full))
        {
            return NotFound();
        }

        var contentType = ContentTypeProvider.TryGetContentType(fileName, out var detected)
            ? detected
            : "application/octet-stream";

        // 图片内容随导入变化，文件名不变 ⇒ 不能用长缓存；但允许 ETag 协商，避免反复整包传输。
        return PhysicalFile(full, contentType, enableRangeProcessing: false);
    }

    /// <summary>
    /// 上传一张图片（M2-G1）。
    /// <para>
    /// <b>为什么要有这个端点</b>：旧版是本地文件选择器直接写图片目录；Web 端没有那个能力，
    /// 不补这个端点则图片**只能靠导入备份带进来**，编辑页无法给按钮换图。
    /// </para>
    /// <para>
    /// 请求：<c>multipart/form-data</c>，文件字段名 <c>file</c>。
    /// 文件名**一律由服务端生成**（<c>img{GUID:N}{扩展名}</c>），客户端文件名只用来取扩展名与记日志。
    /// </para>
    /// <para>
    /// 三道校验：扩展名白名单（不含 <c>.svg</c>）→ 大小上限 → **魔数嗅探**（内容真的是位图）。
    /// 第三道是扩展名之外的独立防线：扩展名是客户端自称的，只信它等于允许把任意字节存进图片目录。
    /// </para>
    /// </summary>
    [HttpPost("images")]
    [Authorize(Policy = Policies.CanOperate)]
    public async Task<IActionResult> UploadImage(CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
        {
            return BadRequest(new { message = "请以 multipart/form-data 提交，文件字段名为 file。" });
        }

        IFormCollection form;
        try
        {
            form = await Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            // 请求体超过 FormOptions.MultipartBodyLengthLimit 时，框架**在读取阶段**就抛这个异常。
            // 必须在这里接住：否则它会冒到全局异常处理器，变成一个看不出原因的 500 ——
            // 而用户看到的应当是"图片太大"。
            _log.Warn("Config", $"上传图片：请求体过大，已拒绝 —— {ex.Message}");

            return StatusCode(StatusCodes.Status413PayloadTooLarge, new
            {
                message = $"图片不能超过 {_images.MaxUploadBytes / 1024.0 / 1024.0:0.##} MB。",
            });
        }

        var file = form.Files["file"];
        if (file is null)
        {
            return BadRequest(new { message = "请求里没有名为 file 的文件字段。" });
        }

        ImageSaveResult result;
        await using (var stream = file.OpenReadStream())
        {
            result = await _images
                .SaveAsync(stream, file.FileName, file.Length, cancellationToken)
                .ConfigureAwait(false);
        }

        return result.Status switch
        {
            ImageSaveStatus.Ok => Ok(new ConfigImageUploadResultDto(result.FileName!, result.Path!, result.Bytes)),
            ImageSaveStatus.TooLarge => StatusCode(
                StatusCodes.Status413PayloadTooLarge,
                new { message = result.Message }),
            ImageSaveStatus.WriteFailed => StatusCode(
                StatusCodes.Status500InternalServerError,
                new { message = result.Message }),
            _ => BadRequest(new { message = result.Message }),
        };
    }

    /// <summary>
    /// 删除一张图片（M2-G1）。
    /// <para>
    /// <b>只删物理文件，不动配置</b>。把某个按钮的图换掉、再删掉旧文件，是预期的编辑流程；
    /// 而"删一个仍被别的按钮引用的图"则会**静默**让那些按钮退化成纯色瓦片
    /// （前端对图片 404 的处理就是回退，不报错）。因此默认拒绝这类删除：
    /// 返回 <b>409</b> 并列出引用方，由用户决定。
    /// </para>
    /// <para>
    /// 确实要强行删除时加 <c>?force=true</c> —— 引用会留在配置里指向一个不存在的文件，
    /// 显示层回退为纯色，可随时重新上传或改指别的图片。
    /// </para>
    /// </summary>
    /// <param name="fileName">服务端生成的单段文件名。</param>
    /// <param name="force">跳过引用检查，强行删除。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpDelete("images/{fileName}")]
    [Authorize(Policy = Policies.CanOperate)]
    public async Task<IActionResult> DeleteImage(
        string fileName,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        // 文件名不合法直接 404，不必再查引用 —— 与 GET 的对外表现保持一致。
        if (!ConfigBackupService.IsSafeImageKey(fileName))
        {
            return NotFound();
        }

        if (!force)
        {
            var view = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            var referrers = ImageReferenceScanner.FindReferrers(view.Settings, fileName);

            if (referrers.Count > 0)
            {
                _log.Warn(
                    "Config",
                    $"删除图片被拒：{fileName} 仍被 {referrers.Count} 处引用（{string.Join("、", referrers)}）");

                return Conflict(new ConfigImageInUseDto(
                    $"图片 {fileName} 仍被 {referrers.Count} 处引用（{string.Join("、", referrers)}），未删除。"
                    + "强行删除会让引用它的按钮回退成纯色：确认要删就加 ?force=true，"
                    + "或先在编辑页把那些地方改成别的图片。",
                    referrers));
            }
        }

        var result = _images.Delete(fileName);

        return result.Status switch
        {
            ImageDeleteStatus.Ok => NoContent(),
            ImageDeleteStatus.NotFound => NotFound(),
            _ => StatusCode(StatusCodes.Status500InternalServerError, new { message = result.Message }),
        };
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    private async Task<IActionResult> SaveAndReturnAsync(
        ControlSettings settings,
        int? expectedVersion,
        string updatedBy,
        string what,
        CancellationToken cancellationToken)
    {
        try
        {
            // SaveAsync 只回版本号；紧接着 GetAsync 是**命中的进程内缓存**（SaveAsync 刚更新过），
            // 不产生额外的存储读。
            await _settings
                .SaveAsync(settings, expectedVersion, updatedBy, cancellationToken)
                .ConfigureAwait(false);

            var view = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            return Ok(ToDto(view));
        }
        catch (ConfigConcurrencyException ex)
        {
            return await ConflictAsync(ex, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 409 响应体：把服务端**当前**配置一并返回，前端可立即刷新而不是再发一次 GET。
    /// </summary>
    private async Task<IActionResult> ConflictAsync(
        ConfigConcurrencyException ex,
        CancellationToken cancellationToken)
    {
        var current = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        return Conflict(new
        {
            message = ex.Message,
            conflict = true,
            expectedVersion = ex.ExpectedVersion,
            actualVersion = ex.ActualVersion,
            current = ToDto(current),
        });
    }

    private static ConfigViewDto ToDto(ControlSettingsView view)
    {
        // SerializeToElement 产出的是自带 JsonDocument 的 JsonElement —— 不依赖被释放的 document。
        var element = JsonSerializer.SerializeToElement(view.Settings, ControlJson.Options);

        return new ConfigViewDto(
            element,
            view.Version,
            view.State.ToString(),
            view.Notes,
            ToCounts(view.Settings),
            view.Settings.Buttons.Count == 0 && view.Settings.SubButtonSets.Count == 0);
    }

    /// <summary>配置里既没有按钮也没有子按钮集 —— 用于导入前的「空配置保护」判定。</summary>
    private static bool IsEmptyConfig(ControlSettings settings)
        => (settings.Buttons?.Count ?? 0) == 0 && (settings.SubButtonSets?.Count ?? 0) == 0;

    private static ConfigCountsDto ToCounts(ControlSettings settings)
    {
        var subButtons = 0;
        foreach (var set in settings.SubButtonSets)
        {
            subButtons += set.SubButtons?.Count ?? 0;
        }

        return new ConfigCountsDto(settings.Buttons?.Count ?? 0, settings.SubButtonSets?.Count ?? 0, subButtons);
    }

    /// <summary>
    /// 导入后把「指令库」引用式动作的处境如实写进导入结果（只是**报告**，不改写任何数据）。
    /// <para>
    /// 为什么必须专门说这件事：指令库是独立配置键（<see cref="ConfigKeys.Commands"/>），
    /// <b>不参与导入导出</b>。因此「指令库」类型动作在备份文件里<b>只剩一个指令名</b>。
    /// 跨机器导入时目标库可能压根没有这条指令（导入后点按钮报「指令库解析失败」），
    /// 也可能有同名但定义不同的指令（会<b>静默发出别的数据</b>）。
    /// 这两种情况原本在导入界面<b>没有任何迹象</b>，只能等用户点按钮时才发现 —— 所以在这里点名。
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> DescribeLibraryRefsAsync(
        ControlSettings settings,
        CancellationToken cancellationToken)
    {
        var refs = CollectLibraryRefs(settings);
        if (refs.Count == 0)
        {
            return Array.Empty<string>();
        }

        var library = await _commands.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var known = new HashSet<string>(
            library.Select(c => (c.Name ?? string.Empty).Trim()),
            StringComparer.OrdinalIgnoreCase);

        var notes = new List<string>
        {
            $"本文件里有 {refs.Count} 条动作仍用「指令库」引用方式（动作里只存了指令名），"
            + "而指令库不随配置文件一起导入导出。"
            + "到「编辑」页把那些动作的指令重新选一次即可 —— 选中后会把指令的定义"
            + "（协议 / 数据类型 / 数据）填入动作，转为独立动作，之后就不再依赖指令库。",
        };

        var missing = refs.Where(r => !known.Contains(r.Name)).ToList();
        if (missing.Count > 0)
        {
            var shown = string.Join("、", missing.Take(3).Select(m => $"{m.Where}→「{m.Name}」"));
            var more = missing.Count > 3 ? $" 等 {missing.Count} 处" : string.Empty;

            notes.Add(
                $"其中 {missing.Count} 条在本机指令库里找不到，发送时会直接失败：{shown}{more}。");
        }

        _log.Info(
            "Config",
            $"导入后指令库引用式动作 {refs.Count} 条，其中本机查不到 {missing.Count} 条。");

        return notes;
    }

    /// <summary>收集配置里所有「指令库」引用式动作（按钮 + 子按钮集内的子按钮），带上位置描述。</summary>
    private static List<(string Where, string Name)> CollectLibraryRefs(ControlSettings settings)
    {
        var found = new List<(string, string)>();

        void Scan(string where, IEnumerable<ButtonAction>? actions)
        {
            foreach (var action in actions ?? Enumerable.Empty<ButtonAction>())
            {
                if (DataEncoder.IsCommandLibrary(action.DataType)
                    && !string.IsNullOrWhiteSpace(action.DataContent))
                {
                    found.Add((where, action.DataContent.Trim()));
                }
            }
        }

        foreach (var button in settings.Buttons ?? Enumerable.Empty<ButtonConfig>())
        {
            Scan($"按钮「{button.Label}」", button.Actions);
        }

        foreach (var set in settings.SubButtonSets ?? Enumerable.Empty<SubButtonSet>())
        {
            foreach (var sub in set.SubButtons ?? Enumerable.Empty<SubButton>())
            {
                Scan($"子按钮集「{set.Label}」› 子按钮「{sub.Label}」", sub.Actions);
            }
        }

        return found;
    }

    private static string BuildImportMessage(
        string format,
        ConfigApplyResult applied,
        ControlSettingsView view)
    {
        var readable = format switch
        {
            ConfigBackupFormats.JsonBackup => "旧版备份文件（明文 JSON）",
            ConfigBackupFormats.Base64Backup => "服务版 base64 备份",
            ConfigBackupFormats.JsonSettings => "裸配置 JSON",
            ConfigBackupFormats.Base64Settings => "base64 包装的裸配置",
            _ => format,
        };

        var parts = new List<string>
        {
            $"已按「{readable}」导入：按钮 {view.Settings.Buttons.Count} 个，子按钮集 {view.Settings.SubButtonSets.Count} 个。",
        };

        if (applied.RestoredImages > 0)
        {
            parts.Add($"还原图片 {applied.RestoredImages} 张。");
        }

        if (applied.SkippedImages.Count > 0)
        {
            parts.Add($"有 {applied.SkippedImages.Count} 张图片未落盘（键名未通过安全校验，或图片内容损坏）：{string.Join("、", applied.SkippedImages.Take(5))}。");
        }

        return string.Concat(parts);
    }
}
