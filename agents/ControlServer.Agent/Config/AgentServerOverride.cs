using ControlServer.Core.Services;

namespace ControlServer.Agent.Config;

/// <summary>
/// 「所连接的服务端地址」的<b>本机覆盖</b>（2026-10-15，需求①）。
///
/// <para>
/// <b>为什么需要它</b>：服务端换了 IP / 端口，而那台机器上的客户端配置（<c>appsettings.json</c>）
/// 在 <c>Program Files</c> 下、改起来要管理员权限，现场最方便的做法是<b>在本机配置页上改一下</b>。
/// 于是这个文件存"用户在本机页上填的那个地址"，<b>优先级高于 <c>Agent:ServerUrl</c> 和广播发现</b>。
/// </para>
///
/// <para>
/// <b>为什么单独一个文件而不是回写 appsettings.json</b>：
/// </para>
/// <list type="bullet">
///   <item>程序目录通常<b>不可写</b>（Program Files + 普通用户），而状态目录一定可写；</item>
///   <item>回写 appsettings.json 会让"安装时写进去的配置"和"运行期改的"混在一起，
///         下次升级覆盖安装就丢了 —— 分开存，语义清楚：<b>文件在，就以它为准</b>；</item>
///   <item>删掉这个文件 = 立刻回到"按安装配置 / 广播发现"的行为，不需要任何界面操作。</item>
/// </list>
///
/// <para>
/// ⚠️ 它同时承担一个<b>信号</b>职责：<see cref="Set"/> 之后 <see cref="ConsumeChangePending"/>
/// 会返回一次 <c>true</c> —— 主循环据此"掐掉当前长连接、按新地址重来"。
/// 没有这个信号，改完地址要等下一次自然断线（可能是几小时）才生效。
/// </para>
/// </summary>
public sealed class AgentServerOverride
{
    private const string FileName = "server-override.json";

    private readonly string _path;
    private readonly LogService _log;

    private readonly object _sync = new();
    private string? _url;

    /// <summary>"改过、但主循环还没处理"。</summary>
    private bool _changePending;

    /// <summary>
    /// "刚改过"的<b>唤醒信号</b>（2026-10-20，容量 1）。
    /// <para>
    /// 🔥 为什么需要它：主循环在两次尝试之间会睡一个退避（最长 60 秒）。
    /// 用户在本机页上改完地址、点了保存，那条路只会去下一次循环才看见 ——
    /// 现场实测等了 <b>48 秒</b>，用户的判断是"改了没用"，于是再改一次。
    /// 有了这个信号，主循环"等退避"变成"等退避或等变更"，改完立刻重连。
    /// </para>
    /// <para>
    /// ⚠️ 它只负责<b>叫醒</b>，不负责表达"改成了什么"：
    /// 真正的判定仍然是 <see cref="Url"/> 与 <see cref="ConsumeChangePending"/> ——
    /// 两条信息合起来才安全（醒来之后重新读地址，而不是拿信号里带的值）。
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _changed = new(0, 1);

    public AgentServerOverride(AgentOptions options, LogService log)
    {
        _log = log;

        var directory = AgentPaths.ResolveStateDirectory(options.StateDirectory);
        _path = Path.Combine(directory, FileName);

        _url = LoadFromDisk();
    }

    /// <summary>
    /// 文件在哪（排障时"我改的到底存哪了"就靠它）。
    /// <para>⚠️ 名字<b>不能叫 <c>Path</c></b>：那会把 <see cref="System.IO.Path"/> 遮住，
    /// 于是本类里所有 <c>Path.Combine</c> / <c>Path.GetDirectoryName</c> 全部编译不过（CS1061）。</para>
    /// </summary>
    public string FilePath => _path;

    /// <summary>当前覆盖地址；null = 没有覆盖（按安装配置 / 广播发现走）。</summary>
    public string? Url
    {
        get
        {
            lock (_sync)
            {
                return _url;
            }
        }
    }

    /// <summary>写入 / 清除覆盖。会落盘并置"需要重连"。</summary>
    public void Set(string? url)
    {
        var normalized = Normalize(url);

        lock (_sync)
        {
            _url = normalized;
            _changePending = true;
        }

        WriteToDisk(normalized);

        _log.Info(
            "Config",
            normalized is null
                ? $"已清除本机的服务端地址覆盖（{_path}）—— 之后按安装配置 / 广播发现找中心。"
                : $"已把本机连接的服务端地址改为 {normalized}（{_path}）—— 即将按新地址重连并重新注册。");

        // 地址变了，本地那份凭据属于**别的**中心，必须作废重来。
        // 这里不做（由主循环在消费信号时统一处理）：本类只负责"记住用户填了什么"。

        // 叫醒正在等退避的主循环（容量 1：连续保存两次也只算一次叫醒）。
        try
        {
            _changed.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已经有一个信号没被取走 —— 它本来就代表"有变更待处理"，不必再加一个。
        }
    }

    /// <summary>
    /// "改过、还没处理"（**只看不消费**）。
    /// <para>
    /// 心跳循环用它决定"要不要立刻掐掉当前长连接" —— 那里**不能**消费掉信号，
    /// 否则主循环就再也看不到它，凭据不会被作废，切换不会发生。
    /// </para>
    /// </summary>
    public bool ChangePending
    {
        get
        {
            lock (_sync)
            {
                return _changePending;
            }
        }
    }

    /// <summary>取一次"改过"信号（取过就清）。主循环每个 tick 调一次。</summary>
    public bool ConsumeChangePending()
    {
        bool pending;

        lock (_sync)
        {
            pending = _changePending;
            _changePending = false;
        }

        if (pending)
        {
            // 顺手把唤醒信号也清掉：否则主循环下一次"等退避"会立刻返回，
            // 表现为"改完地址之后连续两轮空转"（多打一次连接尝试，日志也会多一行）。
            _changed.Wait(0);
        }

        return pending;
    }

    /// <summary>
    /// 等一段时间；<b>中途地址被改过就提前返回</b>，返回 <c>true</c>。
    /// <para>
    /// 用途只有一个：主循环的退避等待。它<b>不消费</b> <see cref="ChangePending"/>
    /// （消费只发生在 <see cref="ConsumeChangePending"/>，那里还要做"上报旧中心 + 作废凭据"）。
    /// </para>
    /// </summary>
    public async Task<bool> WaitForChangeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _changed.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 关机竞态：当成"没被叫醒"，让调用方按普通退避收尾。
            return false;
        }
    }

    /// <summary>
    /// 归一化用户输入：允许 <c>10.0.0.5</c> / <c>10.0.0.5:8899</c> / <c>http://10.0.0.5:8899</c>。
    /// <para>
    /// 缺协议时**补 http**（内网默认；补 https 会让一个能用的地址变成连不上），
    /// 缺端口时**不补**（服务端默认 8899 由服务端自己决定，客户机不该猜）。
    /// </para>
    /// </summary>
    public static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var trimmed = url.Trim().TrimEnd('/');

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }

        // 只接受"能解析出主机名"的地址；解析不了就当作无效（调用方会拒）。
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? trimmed
            : null;
    }

    private string? LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_path)) return null;

            var json = File.ReadAllText(_path);
            var dto = ControlJson.Deserialize<ServerOverrideFile>(json);

            return Normalize(dto?.ServerUrl);
        }
        catch (Exception ex)
        {
            // 坏文件不该让客户端起不来：当作"没有覆盖"，并**留一行日志**。
            // （静默降级正是要避免的：用户会以为"我改过了"，而实际没生效。）
            _log.Warn("Config", $"读取服务端地址覆盖失败（按「没有覆盖」处理）：{ex.Message}");

            return null;
        }
    }

    private void WriteToDisk(string? url)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            if (url is null)
            {
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }

            File.WriteAllText(_path, ControlJson.Serialize(new ServerOverrideFile(url)));
        }
        catch (Exception ex)
        {
            // ⚠️ 这里**必须让用户知道**：写不进去 = 重启后失效，而页面上刚刚说了"已保存"。
            _log.Error("Config", $"写入服务端地址覆盖失败（重启后会丢失）：{ex.Message}", ex);
        }
    }

    /// <summary>落盘格式（字段名固定，将来加字段走可选参数）。</summary>
    private sealed record ServerOverrideFile(string ServerUrl);
}
