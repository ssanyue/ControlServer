using System.Text;
using System.Text.Json;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// 配置备份包的解析结果。
/// </summary>
/// <param name="Ok">是否解析成功。</param>
/// <param name="Backup">解析出的备份包；失败时为 <c>null</c>。</param>
/// <param name="Format">识别到的输入格式（见 <see cref="ConfigBackupFormats"/>），用于告知用户。</param>
/// <param name="Error">失败原因；成功时为 <c>null</c>。</param>
/// <param name="IgnoredFields">被忽略的配置字段（服务版裁剪掉的单机版专有字段）。</param>
public sealed record ConfigParseResult(
    bool Ok,
    ConfigBackup? Backup,
    string Format,
    string? Error,
    IReadOnlyList<string> IgnoredFields);

/// <summary>
/// 备份包应用结果。
/// </summary>
/// <param name="Settings">可直接交给 <see cref="ControlSettingsService.SaveAsync"/> 的配置（图片路径已还原为服务端绝对路径）。</param>
/// <param name="RestoredImages">
/// 实际还原到本机的图片数。**按去重后的图片张数计**，而不是"还原操作次数" ——
/// 同一张图被「默认按钮图 + 多个按钮」重复引用时是**一张**图，
/// 报成 N 会让用户以为备份里凭空多了图（导出侧同样是去重计数，两侧口径一致）。
/// </param>
/// <param name="SkippedImages">被安全校验拒绝的图片键名（路径穿越 / 非法字符）。这些键会被原样保留在配置里，不落盘。</param>
public sealed record ConfigApplyResult(
    ControlSettings Settings,
    int RestoredImages,
    IReadOnlyList<string> SkippedImages);

/// <summary>输入格式常量，用于回显给用户「识别成了什么」。</summary>
public static class ConfigBackupFormats
{
    /// <summary>明文 <c>ConfigBackup</c> JSON —— 旧版「导出配置」的 <c>.controlbak</c> / <c>.json</c>。</summary>
    public const string JsonBackup = "json+backup";

    /// <summary>base64 包装的 <c>ConfigBackup</c> JSON —— 服务版导出格式。</summary>
    public const string Base64Backup = "base64+backup";

    /// <summary>裸 <c>ControlSettings</c> JSON（没有 <c>settings</c> 外层的旧文件）。</summary>
    public const string JsonSettings = "json+settings";

    /// <summary>base64 包装的裸 <c>ControlSettings</c> JSON。</summary>
    public const string Base64Settings = "base64+settings";
}

/// <summary>
/// 配置导入 / 导出内核（M2-F）。
/// <para>
/// <b>为什么放在 Core 而不是 Controller</b>：解析、图片编解码、字段裁剪都是**纯逻辑**，
/// 不依赖 ASP.NET Core；放这里才能被单测用文本向量直接驱动（含非法输入与路径穿越攻击）。
/// </para>
/// <para>
/// <b>兼容目标</b>：旧版「导出配置」产出的 <c>.controlbak</c> 必须能**原样导入**。
/// 旧版的导出是 <c>ConfigBackup</c> 的**明文缩进 camelCase JSON**（不是 base64、不是加密的
/// <c>control_config.dat</c> —— 后者需 LicenseManager 解密，属 M3），因此明文 JSON 是主路径。
/// </para>
/// </summary>
public sealed class ConfigBackupService
{
    /// <summary>备份包格式版本（与旧版一致）。</summary>
    public const string BackupVersion = "1.0";

    /// <summary>
    /// 服务版 <see cref="ControlSettings"/> 认得的字段名（camelCase）。
    /// 导入时不在这个集合里的字段会被**显式列出**告知用户，而不是无声吞掉（M2 方案 §3.2 的承诺）。
    /// </summary>
    /// <summary>
    /// 服务版**认得**的配置字段名（用于区分「真的是一份配置」与「一个碰巧是对象的 JSON」，
    /// 也用于告诉用户"哪些字段被忽略了"）。
    ///
    /// <para>
    /// 🔥 <b>2026-10-15 改成从模型反射生成</b>（原先是手写的一张清单）。原因是一个**真缺陷**：
    /// 加了 <c>comboButtons</c> 之后没人想起同步那张清单，于是导入一份含组合按钮的配置时，
    /// 界面会告诉用户「已忽略字段：comboButtons」—— 数据其实没丢（反序列化走的是模型），
    /// 但那句话会让人以为**组合按钮全丢了**，而且真正会被忽略的单机版字段会被这句话淹掉。
    /// </para>
    /// <para>
    /// 反射之后，"加了字段却忘了登记"这一类 bug 从根上消失。
    /// ⚠️ 前提是 <see cref="ControlSettings"/> 上**只有数据属性、没有计算属性** ——
    /// 加了计算属性要在这里排除掉（否则它会被当成"认得的一个字段"，
    /// 进而抬高 <see cref="CountRecognizedFields"/> 的计数）。
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> KnownSettingsFields = new HashSet<string>(
        typeof(ControlSettings)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(static p => p.Name),
        StringComparer.OrdinalIgnoreCase);

    private readonly IDataDirectory _dataDir;
    private readonly LogService _log;

    public ConfigBackupService(IDataDirectory dataDir, LogService log)
    {
        _dataDir = dataDir;
        _log = log;
    }

    // ═══════════════════════════════════════════════════════════
    // 导出
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 构建自包含备份：把**所有被引用且真实存在**的图片以 base64 内嵌，
    /// 并把副本中的图片路径改写为内嵌键名（<c>img0.png</c>…）。整体结构照旧版 <c>BuildBackup</c> 复刻。
    /// </summary>
    public ConfigBackup BuildBackup(ControlSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // 1) 收集被引用的、真实存在的图片路径（去重，忽略大小写）
        var paths = new List<string>();
        void Collect(string? p)
        {
            if (!string.IsNullOrWhiteSpace(p)
                && File.Exists(p)
                && !paths.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(p);
            }
        }

        Collect(settings.BackgroundImagePath);
        Collect(settings.DefaultButtonImage);
        foreach (var b in settings.Buttons) Collect(b.ButtonImage);
        foreach (var s in settings.SubButtonSets)
        {
            Collect(s.ButtonImage);
            foreach (var sub in s.SubButtons) Collect(sub.ButtonImage);
        }

        // 2) 生成唯一键名并读取 base64
        var absToKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        var idx = 0;
        foreach (var p in paths)
        {
            var key = $"img{idx++}{Path.GetExtension(p)}";
            absToKey[p] = key;
            try
            {
                images[key] = Convert.ToBase64String(File.ReadAllBytes(p));
            }
            catch (Exception ex)
            {
                // 单张图片读失败不应让整次导出失败 —— 但要让用户知道少了一张。
                images.Remove(key);
                absToKey.Remove(p);
                _log.Warn("Config", $"导出配置：图片读取失败已跳过（{p}）—— {ex.Message}");
            }
        }

        // 3) 深拷贝配置，把图片路径换成内嵌键名
        var clone = ConfigCloner.CloneSettings(settings);

        string Map(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return v ?? string.Empty;
            return absToKey.TryGetValue(v, out var k) ? k : v;
        }

        clone.BackgroundImagePath = Map(clone.BackgroundImagePath);
        clone.DefaultButtonImage = Map(clone.DefaultButtonImage);
        foreach (var b in clone.Buttons) b.ButtonImage = Map(b.ButtonImage);
        foreach (var s in clone.SubButtonSets)
        {
            s.ButtonImage = Map(s.ButtonImage);
            foreach (var sub in s.SubButtons) sub.ButtonImage = Map(sub.ButtonImage);
        }

        return new ConfigBackup
        {
            Version = BackupVersion,
            Settings = clone,
            Images = images,
        };
    }

    /// <summary>把备份包序列化为**明文 JSON**（缩进 + camelCase，与旧版一字不差）。</summary>
    public static string ToJson(ConfigBackup backup)
        => ControlJson.Serialize(backup);

    /// <summary>把备份包序列化后做 UTF-8 → Base64，得到**单行文本**（便于浏览器里复制粘贴）。</summary>
    public static string ToBase64(ConfigBackup backup)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(ToJson(backup)));

    /// <summary>建议文件名（与旧版同一命名习惯，扩展名换成服务版自己的 <c>.controlbak</c>）。</summary>
    public static string SuggestFileName()
        => $"中控配置备份_{BeijingTime.Now:yyyyMMdd_HHmm}.controlbak";

    // ═══════════════════════════════════════════════════════════
    // 导入：解析
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 解析导入文本 —— 自动识别四种形态（见 <see cref="ConfigBackupFormats"/>）。
    /// <para>
    /// 识别顺序：先试 base64（顶层不是 <c>{</c> 且能解出 UTF-8 JSON），再按 JSON 顶层有无 <c>settings</c>
    /// 区分备份包与裸配置。**以内容为准，与文件扩展名无关**（旧版「重命名后也能导入」的行为）。
    /// </para>
    /// </summary>
    public static ConfigParseResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ConfigParseResult(false, null, string.Empty, "导入内容为空。", Array.Empty<string>());
        }

        var raw = StripBom(text).Trim();
        var isBase64 = false;

        // ── 1. base64 探测：以内容为准，不做长度 / 字符集猜测 ──
        var firstChar = raw.Length > 0 ? raw[0] : '\0';
        if (firstChar != '{' && firstChar != '[')
        {
            if (TryDecodeBase64(raw, out var decoded))
            {
                raw = decoded.Trim();
                isBase64 = true;
            }
            else
            {
                return new ConfigParseResult(
                    false, null, string.Empty,
                    "无法识别导入内容：既不是 JSON，也不是有效的 base64 文本。",
                    Array.Empty<string>());
            }
        }

        // ── 2. 解析 JSON 顶层 ──
        JsonDocument root;
        try
        {
            root = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            return new ConfigParseResult(
                false, null, string.Empty,
                $"不是有效的 JSON：{ex.Message}", Array.Empty<string>());
        }

        using (root)
        {
            if (root.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ConfigParseResult(
                    false, null, string.Empty,
                    "JSON 顶层不是对象，不是有效的配置备份。", Array.Empty<string>());
            }

            // 顶层有 settings → 备份包；否则视作裸 ControlSettings。
            var hasSettingsWrapper = root.RootElement.TryGetProperty("settings", out var settingsElement)
                && settingsElement.ValueKind == JsonValueKind.Object;

            var format = (isBase64 ? "base64+" : "json+") + (hasSettingsWrapper ? "backup" : "settings");
            var ignored = hasSettingsWrapper
                ? FindIgnoredFields(settingsElement)
                : FindIgnoredFields(root.RootElement);

            if (hasSettingsWrapper)
            {
                ConfigBackup? backup;
                try
                {
                    backup = ControlJson.DeserializeLenient<ConfigBackup>(raw);
                }
                catch (Exception ex)
                {
                    return new ConfigParseResult(false, null, format, $"备份包解析失败：{ex.Message}", ignored);
                }

                if (backup?.Settings is null)
                {
                    return new ConfigParseResult(false, null, format, "备份包缺少 settings 配置数据。", ignored);
                }

                backup.Images ??= new Dictionary<string, string>();
                return new ConfigParseResult(true, backup, format, null, ignored);
            }

            // 裸配置：包一层备份壳，后续流程统一。
            //
            // ⚠️ 但必须先确认「这真的是配置数据」—— 否则任意一个 JSON 对象（如 {"version":"1.0"}、
            // 甚至一段不相关的接口响应）都会被宽松反序列化成一个**全默认值的空配置**，
            // 然后在导入流程里把用户现有配置整体覆盖掉。这是一次点击就丢全部数据的路径，
            // 因此要求"至少认得一个已知字段"才放行。
            var recognized = CountRecognizedFields(root.RootElement);
            if (recognized == 0)
            {
                return new ConfigParseResult(
                    false, null, format,
                    "JSON 里没有任何已知的配置字段（appName / buttons / subButtonSets…），" +
                    "无法确认这是配置数据。若这是备份文件，请确认它包含 settings 节点。",
                    ignored);
            }

            ControlSettings? settings;
            try
            {
                settings = ControlJson.DeserializeLenient<ControlSettings>(raw);
            }
            catch (Exception ex)
            {
                return new ConfigParseResult(false, null, format, $"配置解析失败：{ex.Message}", ignored);
            }

            if (settings is null)
            {
                return new ConfigParseResult(false, null, format, "配置内容为空，无法解析。", ignored);
            }

            return new ConfigParseResult(
                true,
                new ConfigBackup { Version = BackupVersion, Settings = settings, Images = new Dictionary<string, string>() },
                format, null, ignored);
        }
    }

    /// <summary>
    /// 列出服务版不认识的配置字段（单机版专有项：窗口尺寸 / 锁定窗口 / 关闭时最小化 / 开机自启 / 用户密码哈希…）。
    /// 仅做告知，不阻断导入。
    /// </summary>
    public static IReadOnlyList<string> FindIgnoredFields(JsonElement settingsElement)
    {
        var ignored = new List<string>();

        if (settingsElement.ValueKind != JsonValueKind.Object) return ignored;

        foreach (var property in settingsElement.EnumerateObject())
        {
            if (!KnownSettingsFields.Contains(property.Name))
            {
                ignored.Add(property.Name);
            }
        }

        ignored.Sort(StringComparer.Ordinal);
        return ignored;
    }

    /// <summary>
    /// 数一数 JSON 对象里有多少个**服务版认得的**配置字段。
    /// 用于区分「真的是一份配置」与「只是一个碰巧是对象的 JSON」。
    /// </summary>
    public static int CountRecognizedFields(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;

        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (KnownSettingsFields.Contains(property.Name)) count++;
        }

        return count;
    }

    // ═══════════════════════════════════════════════════════════
    // 导入：应用
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 应用备份：把内嵌图片还原到本机 <c>ImagesDirectory</c>，并把配置里的键名改回**绝对路径**。
    /// 键名不在内嵌字典里时原样保留（兼容"直接导出原始配置"的场景）。
    /// </summary>
    /// <remarks>
    /// <b>安全</b>：图片键名来自**不可信的上传内容**，因此必须过 <see cref="IsSafeImageKey"/> 白名单校验，
    /// 否则 <c>../../../Windows/System32/x.dll</c> 这类键名会造成目录穿越写文件。
    /// 被拒绝的键名会原样留在配置里（不落盘）并在结果中列出，便于用户排查。
    /// </remarks>
    public ConfigApplyResult Apply(ConfigBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);

        var settings = backup.Settings ?? new ControlSettings();
        var images = backup.Images ?? new Dictionary<string, string>();

        // 用集合去重：同一张图被多处引用时只算一张、只报一次。
        var restoredKeys = new HashSet<string>(StringComparer.Ordinal);
        var skippedKeys = new HashSet<string>(StringComparer.Ordinal);

        string Restore(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return v ?? string.Empty;
            if (!images.TryGetValue(v, out var b64)) return v;

            if (!IsSafeImageKey(v))
            {
                skippedKeys.Add(v);
                _log.Warn("Config", $"导入配置：图片键名未通过安全校验（疑似目录穿越），已跳过落盘：{v}");
                return v;
            }

            // 已经还原过同一个键：直接返回目标路径，不重复解码与写盘。
            if (restoredKeys.Contains(v))
            {
                return Path.Combine(_dataDir.ImagesDirectory, v);
            }

            try
            {
                _dataDir.EnsureCreated();
                var dest = Path.Combine(_dataDir.ImagesDirectory, v);
                File.WriteAllBytes(dest, Convert.FromBase64String(b64));
                restoredKeys.Add(v);
                return dest;
            }
            catch (Exception ex)
            {
                // base64 非法 / 写盘失败：保留键名，不阻断整体导入。
                skippedKeys.Add(v);
                _log.Warn("Config", $"导入配置：图片还原失败，已跳过（{v}）—— {ex.Message}");
                return v;
            }
        }

        settings.BackgroundImagePath = Restore(settings.BackgroundImagePath);
        settings.DefaultButtonImage = Restore(settings.DefaultButtonImage);
        foreach (var b in settings.Buttons) b.ButtonImage = Restore(b.ButtonImage);
        foreach (var s in settings.SubButtonSets)
        {
            s.ButtonImage = Restore(s.ButtonImage);
            foreach (var sub in s.SubButtons) sub.ButtonImage = Restore(sub.ButtonImage);
        }

        return new ConfigApplyResult(settings, restoredKeys.Count, skippedKeys.OrderBy(k => k, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// 图片键名白名单：只允许「单层文件名」（字母 / 数字 / <c>_ - . ( ) 空格</c> 与中文）。
    /// <list type="bullet">
    ///   <item>拒绝任何目录分隔符（<c>/ \</c>）与盘符（<c>:</c>）；</item>
    ///   <item>拒绝 <c>..</c> 片段；</item>
    ///   <item>拒绝 URI 前缀（<c>http:</c> 之类会被上面的冒号规则一并挡掉）。</item>
    /// </list>
    /// </summary>
    public static bool IsSafeImageKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        // 必须与 Path.GetFileName 等价 —— 不等价说明含目录成分。
        if (!string.Equals(key, Path.GetFileName(key), StringComparison.Ordinal)) return false;

        if (key.Contains("..", StringComparison.Ordinal)) return false;

        foreach (var c in key)
        {
            if (char.IsLetterOrDigit(c)) continue;
            if (c is '_' or '-' or '.' or '(' or ')' or ' ') continue;
            // 中文文件名很常见，放行。
            if (c > 0x2E80) continue;

            return false;
        }

        return true;
    }

    // ═══════════════════════════════════════════════════════════
    // 工具
    // ═══════════════════════════════════════════════════════════

    private static string StripBom(string text)
        => text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;

    /// <summary>
    /// 尝试 base64 解码。**必须能解出本身就是 JSON 的 UTF-8 文本**才算成功 ——
    /// 否则任意普通英文串都会被 Convert.FromBase64String 解成乱码字节，误判成 base64。
    /// </summary>
    private static bool TryDecodeBase64(string text, out string decoded)
    {
        decoded = string.Empty;

        // base64 里不允许出现空白；若有大量换行则先剔除（用户从记事本粘贴常见）。
        var compact = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (compact.Length == 0 || compact.Length % 4 != 0) return false;

        try
        {
            var bytes = Convert.FromBase64String(compact);
            var s = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

            if (s.Length == 0 || (s[0] != '{' && s[0] != '[')) return false;

            // 再确认是 JSON —— 避免"看起来像 base64 的乱码"被当成合法输入。
            using var probe = JsonDocument.Parse(s);
            decoded = s;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
