using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Core.Abstractions;

namespace ControlServer.Server.Services;

/// <summary>
/// 监听地址的**可写**持久化（M2-E5 §4.4 步骤 3）。
/// <para>
/// <b>为什么不直接改 appsettings.json</b>：
/// ① 部署目录常常只读（Program Files / 服务账户无写权限）；
/// ② 单文件发布时 appsettings.json 可能来自内嵌资源，改磁盘文件等于白改；
/// ③ 用户手写的注释与格式会被 JsonNode 重排掉。
/// 因此另起一个只放"覆盖项"的小文件，位置在**数据目录**（与数据库、日志同级，写权限有保证）。
/// </para>
/// <para>
/// <b>为什么文件格式还保留完整的 <c>Server:Http:Urls</c> 层级</b>：
/// 它同时被当作一个标准 JSON 配置源挂载（见 <c>Program.cs</c> §4），
/// 层级写全就能直接复用现成的配置绑定，不必再写一套"读覆盖文件"的解析逻辑。
/// </para>
/// <para>
/// <b>写入顺序</b>：本文件在配置源链里排在 <c>appsettings.json</c> <b>之后</b>、
/// <b>环境变量与命令行之前</b> —— 既保证覆盖默认值，又不剥夺运维用环境变量兜底的能力
/// （集成测试与容器部署依赖这一点）。
/// </para>
/// </summary>
public sealed class ServerAddressOverrideStore
{
    /// <summary>覆盖文件名（放在数据目录根下）。</summary>
    public const string FileName = "server-overrides.json";

    private const string SectionName = "Server";
    private const string SubSectionName = "Http";
    private const string PropertyName = "Urls";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public ServerAddressOverrideStore(IDataDirectory dataDirectory)
    {
        _path = Path.Combine(dataDirectory.Root, FileName);
    }

    /// <summary>持久化文件的绝对路径（诊断页会展示它，方便运维直接去改）。</summary>
    public string FilePath => _path;

    /// <summary>文件当前是否存在（存在 = 端口不是纯默认值）。</summary>
    public bool Exists => File.Exists(_path);

    /// <summary>
    /// 原子写入：先写同目录临时文件再替换，避免断电 / 被杀进程时留下半截 JSON
    /// （半截 JSON 会让下次启动直接抛 <c>JsonException</c>，那比端口回到默认值严重得多）。
    /// </summary>
    /// <remarks>
    /// <b>值一律写成字符串</b>（多个地址用分号连接），不写 JSON 数组 ——
    /// 因为 <c>Server:Http:Urls</c> 绑定到的是 <c>string</c> 属性，
    /// 数组形式会展开成 <c>Urls:0</c> / <c>Urls:1</c> 两组子键，
    /// 父节点 <c>Urls</c> 的值为 null，绑定时被直接跳过 ⇒ <b>写进去自己也读不回来</b>。
    /// </remarks>
    public void Save(IReadOnlyList<string> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);

        // 合并式写入：保留文件里其它与端口无关的键，别把用户手加的东西擦掉。
        var root = TryReadRoot() ?? new JsonObject();
        var server = EnsureObject(root, SectionName);
        var http = EnsureObject(server, SubSectionName);

        http[PropertyName] = JsonValue.Create(string.Join(';', urls));

        var json = root.ToJsonString(WriteOptions);
        var temp = _path + ".tmp";

        File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>
    /// 读回文件里持久化的监听地址。
    /// <para>
    /// 文件不存在、不可读、层级缺失或值形态不认识时返回 <c>null</c>
    /// （<c>null</c> = "没有可比较的持久化内容"，与"持久化了一份空列表"区分开）。
    /// </para>
    /// <para>
    /// 兼容早期写成 JSON 数组的版本 —— 那种格式自身读不回来，
    /// 但若要判断"文件里到底记的是什么"，这里是唯一能看见真相的地方。
    /// </para>
    /// </summary>
    public IReadOnlyList<string>? TryReadUrls()
    {
        var root = TryReadRoot();
        if (root?[SectionName] is not JsonObject server
            || server[SubSectionName] is not JsonObject http)
        {
            return null;
        }

        var node = http[PropertyName];
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var single))
        {
            var parts = Split(single);
            return parts.Count > 0 ? parts : null;
        }

        if (node is JsonArray array)
        {
            var parts = array
                .Select(item => item is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToList();

            return parts.Count > 0 ? parts : null;
        }

        return null;
    }

    private static List<string> Split(string raw)
        => raw
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    /// <summary>删除覆盖文件（用于"恢复默认端口"）。文件不存在时静默返回。</summary>
    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private JsonObject? TryReadRoot()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(_path);
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 读坏了就整体重写 —— 返回值 null 会让调用方从空对象开始，正好覆盖掉坏内容。
            // 这里不抛：能走到 Save 说明上层已经决定要写，让写法本身失败才是诚实的。
            return null;
        }
    }

    private static JsonObject EnsureObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }
}
