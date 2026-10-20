using System.Text;
using System.Text.Json;
using ControlServer.Core.Services;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent;

/// <summary>
/// 注册成功后拿到的长期凭据。**agentKey 只在这里存一次**（注册应答里），之后再也取不回来。
/// </summary>
/// <param name="AgentId">服务端在线表里的 Id。</param>
/// <param name="AgentKey">长期凭据（32 字节随机数，Base64Url）。</param>
/// <param name="ServerUrl">签发给它的中心服务地址（换服务端后凭据作废，必须重注册）。</param>
/// <param name="MachineKey">本机机器码派生标识（用于核对"这份凭据是不是这台机器的"）。</param>
/// <param name="IssuedAt">签发时间。</param>
public sealed record AgentCredentials(
    int AgentId,
    string AgentKey,
    string ServerUrl,
    string MachineKey,
    DateTimeOffset IssuedAt);

/// <summary>
/// 客户端凭据的落盘（M4.1）。
/// <para>
/// <b>文件内容有两种形态，靠前缀区分</b>：
/// </para>
/// <list type="bullet">
///   <item><c>dpapi:&lt;base64&gt;</c> —— Windows 上用 DPAPI（LocalMachine 作用域）加密。
///         托盘（普通用户）与服务（SYSTEM）都能解开，这是选 LocalMachine 的唯一理由。</item>
///   <item><c>plain:&lt;json&gt;</c> —— 没有可用保护实现时的降级（非 Windows）。
///         刻意写成"带前缀的明文"而不是"假装加密"：**看得见才不会被忘记**。</item>
/// </list>
/// <para>
/// <b>写入一律"临时文件 + 替换"</b>：断电或被杀进程时留下半截文件，
/// 会让下次启动读不出凭据 → 表现为"每次重启都要求重新注册"，
/// 而那个症状看起来像服务端问题，极难定位到本地一个残缺的文件。
/// </para>
/// </summary>
public sealed class AgentIdentityStore
{
    private const string ProtectedPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";

    private readonly string _path;
    private readonly ISecretProtector? _protector;

    public AgentIdentityStore(string stateDirectory, ISecretProtector? protector)
    {
        _path = AgentPaths.CredentialsFile(stateDirectory);
        _protector = protector;
    }

    /// <summary>凭据文件路径（排障用；托盘会把它显示出来）。</summary>
    public string FilePath => _path;

    /// <summary>凭据是否受保护（页面据此提示"这台机器的凭据是明文的"）。</summary>
    public bool IsProtected => _protector is not null && OperatingSystem.IsWindows();

    /// <summary>
    /// 读取凭据。**任何异常都返回 null**（并留一条本地日志由调用方决定）：
    /// 读不出来只应表现为"需要重新注册"，绝不该让服务起不来。
    /// </summary>
    public AgentCredentials? Load(out string? error)
    {
        error = null;

        try
        {
            if (!File.Exists(_path)) return null;

            var raw = File.ReadAllText(_path, Encoding.UTF8);

            if (raw.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            {
                if (_protector is null)
                {
                    error = "凭据是受保护的，但当前平台没有可用的解密实现。";
                    return null;
                }

                var json = _protector.Unprotect(raw[ProtectedPrefix.Length..]);
                return ControlJson.Deserialize<AgentCredentials>(json);
            }

            if (raw.StartsWith(PlainPrefix, StringComparison.Ordinal))
            {
                return ControlJson.Deserialize<AgentCredentials>(raw[PlainPrefix.Length..]);
            }

            error = "凭据文件格式不可识别（可能被手工改过）。";
            return null;
        }
        catch (Exception ex)
        {
            error = $"读取凭据失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>保存凭据。失败时抛 —— 凭据没落盘就"看起来注册成功了"，重启后又要重注册。</summary>
    public void Save(AgentCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var json = ControlJson.Serialize(credentials);

        var content = _protector is not null && OperatingSystem.IsWindows()
            ? ProtectedPrefix + _protector.Protect(json)
            : PlainPrefix + json;

        var directory = Path.GetDirectoryName(_path);

        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // 原子替换：半截文件比"没有文件"更糟（见类注释）。
        var temp = _path + ".tmp";
        File.WriteAllText(temp, content, Encoding.UTF8);
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>
    /// 清除凭据（换服务端时；以及"中心明确不认这份凭据"之后的重新注册）。失败静默 —— 它只是"让下次重注册"，不该阻断流程。
    /// </summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch
        {
            // 忽略。
        }
    }
}

/// <summary>
/// 客户端状态快照（服务写、托盘读）。
/// <para>
/// <b>为什么用文件而不是命名管道</b>：M4.1 托盘只需要"看一眼状态"（连没连上、是哪台、地址是什么），
/// 这是**低频只读**需求。为它引入一套双向 IPC 协议，成本和出错面都远大于收益。
/// 命名管道留给真正的双向场景（M5 远程桌面的信令）。
/// </para>
/// <para>
/// ⚠️ 状态文件里<b>不含 agentKey</b>：托盘不需要它，而多一处明文扩散就多一次泄露机会。
/// </para>
/// </summary>
/// <param name="Status">人话状态（直接显示在托盘提示里）。</param>
/// <param name="Connected">此刻是否握着长连接。</param>
/// <param name="ServerVersion">
/// 中心服务端的版本（2026-10-14 补）。
/// <para>
/// 来自注册应答与欢迎消息里的 <c>ServerVersion</c>，为 null = <b>还没跟中心说上话</b>
/// （不是"中心没有版本"）。本机配置页的「服务端」卡片要显示它 ——
/// 现场"某个功能对不上"时，第一件事就是确认两边版本。
/// </para>
/// </param>
/// <param name="LastSeenAt">
/// <b>最后一次与中心成功通信</b>的时间（2026-10-14 补）—— 欢迎消息与每一条成功的心跳回执都会刷新它。
/// <para>
/// ⚠️ 它与 <paramref name="UpdatedAt"/> <b>不是一回事</b>：那个是"状态文件最后一次被写"的时间，
/// 状态没变它就不动；这个是"中心最后一次听见这台机器"。断线之后它<b>会停住不动</b>，
/// 而那正是它有用的地方 —— 一眼看出"最后一次说得上话是在什么时候"。
/// </para>
/// </param>
public sealed record AgentStatusSnapshot(
    string Status,
    bool Connected,
    string? ServerUrl,
    int? AgentId,
    string? DisplayName,
    string? LastError,
    DateTimeOffset UpdatedAt,
    string? ServerVersion = null,
    DateTimeOffset? LastSeenAt = null);

/// <summary>状态文件的读写。</summary>
public static class AgentStatusFile
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 写状态文件的互斥闸（2026-10-14）。
    /// <para>
    /// 写它的人<b>不止一个线程</b>：主循环（连接状态变化）、SignalR 回调（欢迎消息 / 被拉黑）、
    /// 以及心跳循环（刷新「最后通信时间」）。它们用<b>同一个临时文件名</b>，
    /// 不加这道闸就可能两个线程同时往那个 <c>.tmp</c> 里写、内容交错，
    /// 再被对方 <c>Move</c> 成正式文件 —— 托盘读到的就是一份坏 JSON。
    /// </para>
    /// </summary>
    private static readonly object WriteGate = new();

    /// <summary>写状态（失败静默：状态文件写不出去不该影响客户端运行）。</summary>
    public static void Write(string stateDirectory, AgentStatusSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(stateDirectory);

            lock (WriteGate)
            {
                var path = AgentPaths.StatusFile(stateDirectory);

                // 🔥 先写临时文件、再原子替换（2026-10-14）。
                //
                // 为什么要多这一步：心跳每跳都会刷新「最后通信时间」（默认 10 秒一次），
                // 而托盘与本机配置页正在**同时**读这个文件 —— 直接覆盖写的话，
                // 读的一方可能拿到半截 JSON，`Read` 会把它当成"读坏了"返回 null，
                // 于是托盘上冷不丁闪一下"未连接"。写入频率从"状态变化时"提到"每 10 秒"之后，
                // 这个概率不再可以忽略。
                var temp = path + ".tmp";

                File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, WriteOptions), Encoding.UTF8);

                File.Move(temp, path, overwrite: true);
            }
        }
        catch
        {
            // 忽略。
        }
    }

    /// <summary>读状态（查不到/读坏一律返回 null，让托盘显示"未知"而不是崩掉）。</summary>
    public static AgentStatusSnapshot? Read(string stateDirectory)
    {
        try
        {
            var path = AgentPaths.StatusFile(stateDirectory);
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<AgentStatusSnapshot>(json);
        }
        catch
        {
            return null;
        }
    }
}
