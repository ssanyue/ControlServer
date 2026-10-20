using System.Text.Json;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Licensing;

namespace ControlServer.Infrastructure.Storage;

/// <summary>
/// 授权状态的文件存储。
///
/// <para>
/// 三个文件，都在数据目录下：
/// <list type="bullet">
/// <item><description><c>license.lic</c> —— 签发时的授权原文，<b>原样保存、绝不改写</b>（改了签名就废）。</description></item>
/// <item><description><c>license.binding.json</c> —— 通用授权的首次绑定记录（见 <see cref="LicenseBinding"/>）。</description></item>
/// <item><description><c>license.trial.json</c> —— 本机自动试用记录（见 <see cref="LocalTrialRecord"/>）。</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>写入一律走「临时文件 + 原子替换」</b>：授权文件被写到一半时断电/强杀，
/// 留下一个半截文件，服务下次启动就会判成"签名无效"——现场看到的是"授权突然失效了"，
/// 而真正的原因是一次没写完的 IO。临时文件 + <c>File.Move(overwrite)</c> 让这种中间态不可能出现。
/// </para>
///
/// <para>
/// <b>读取不缓存</b>：文件很小（&lt; 2 KB），而"外部把授权文件直接拷进数据目录"
/// 是一种真实存在的运维动作（尤其在没有前端可用的早期阶段）。
/// 每次读盘能让这种操作立刻生效，省掉一整类"我明明放好了却没反应"的排查。
/// </para>
/// </summary>
public sealed class LicenseFileStore : ILicenseStore
{
    /// <summary>绑定侧车文件名。</summary>
    public const string BindingFileName = "license.binding.json";

    /// <summary>本机试用记录文件名。</summary>
    public const string TrialFileName = "license.trial.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IDataDirectory _dataDirectory;

    /// <summary>串行化读写：导入与后台刷新可能同时发生，不能让两个写互相覆盖。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LicenseFileStore(IDataDirectory dataDirectory) => _dataDirectory = dataDirectory;

    /// <summary>授权文件路径（<c>{DataDir}\license.lic</c>）。</summary>
    public string LicenseFilePath => _dataDirectory.LicenseFile;

    /// <summary>绑定侧车路径。</summary>
    public string BindingFilePath => Path.Combine(_dataDirectory.Root, BindingFileName);

    /// <summary>本机试用记录路径。</summary>
    public string TrialFilePath => Path.Combine(_dataDirectory.Root, TrialFileName);

    public async Task<LicenseSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(LicenseFilePath))
            {
                return LicenseSnapshot.Missing();
            }

            var text = await ReadTextAsync(LicenseFilePath, cancellationToken).ConfigureAwait(false);

            var binding = await ReadJsonAsync<LicenseBinding>(BindingFilePath, cancellationToken)
                .ConfigureAwait(false);

            if (!LicenseCodec.TryDecode(text, out var document, out var error))
            {
                return new LicenseSnapshot(true, text, null, binding, error);
            }

            return new LicenseSnapshot(true, text, document, binding, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(
        string licenseText,
        LicenseBinding? binding,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(licenseText))
        {
            throw new ArgumentException("授权文本不能为空。", nameof(licenseText));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_dataDirectory.Root);

            await WriteAtomicAsync(LicenseFilePath, licenseText, cancellationToken).ConfigureAwait(false);

            if (binding is null)
            {
                // 绑定式授权不需要侧车。这里**主动删掉**上一次通用授权留下的那份：
                // 留着不会造成误判（文档里有 mc 时优先用它），但会让下一个排查的人
                // 看到一份与当前授权无关的绑定记录而困惑。
                TryDelete(BindingFilePath);
            }
            else
            {
                await WriteAtomicAsync(
                    BindingFilePath,
                    JsonSerializer.Serialize(binding, JsonOptions),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // ⚠️ 只删授权与绑定，**不删本机试用记录**。
            // 否则"移除授权"就成了一条重置试用期的免费通道：移除 → 重启 → 又白拿 30 天。
            TryDelete(LicenseFilePath);
            TryDelete(BindingFilePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalTrialRecord?> ReadTrialAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadJsonAsync<LocalTrialRecord>(TrialFilePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteTrialAsync(LocalTrialRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_dataDirectory.Root);

            await WriteAtomicAsync(
                TrialFilePath,
                JsonSerializer.Serialize(record, JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    private static async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        // 明确以 UTF-8 读取：授权文本虽只有 ASCII，但客户名等字段含中文，
        // 依赖默认编码会在不同区域设置下读出乱码。
        using var reader = new StreamReader(path, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path)) return null;

        try
        {
            var text = await ReadTextAsync(path, cancellationToken).ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(text)
                ? null
                : JsonSerializer.Deserialize<T>(text, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 损坏的侧车 = 没有侧车。绝不能因为一个辅助文件读不了就让整个授权体系报错 ——
            // 那会把"一个 JSON 写坏了"升级成"服务不能用"。
            return null;
        }
    }

    /// <summary>临时文件 + 原子替换写入。</summary>
    private static async Task WriteAtomicAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";

        await File.WriteAllTextAsync(temporary, content, new System.Text.UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        File.Move(temporary, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // 删除失败不影响语义（内容会在下次写入时被覆盖）。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}
