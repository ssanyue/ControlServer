using ControlServer.Contracts;
using ControlServer.Core.Models;
using ControlServer.Core.Services;

namespace ControlServer.Agent.Config;

/// <summary>一次配置同步的结果。</summary>
/// <param name="Applied">是否真的推进了版本（false = 已是最新 / 拉取失败 / 本机停用）。</param>
/// <param name="Version">同步之后本机生效的版本号（0 = 还没有任何配置）。</param>
/// <param name="Error">失败原因（成功或"无需变更"时为 null）。</param>
public sealed record ConfigSyncOutcome(bool Applied, int Version, string? Error);

/// <summary>
/// 客户机上<b>当前生效的那份配置</b>（M4.5；2026-10-14 起双向可写）。
/// <para>
/// 它把五件事串起来：<b>拉取</b>（<see cref="AgentConfigClient"/>）、<b>版本判新旧</b>、
/// <b>本机编辑</b>（<see cref="ApplyLocalEdit"/>）、<b>落盘</b>（<see cref="AgentLocalConfigStore"/>）、
/// <b>通知执行者</b>（<see cref="Applied"/> 事件）。
/// </para>
///
/// <para>🔥 <b>两个写入方：中心服务端与本机配置页</b>。冲突按「后写覆盖」解：</para>
/// <list type="bullet">
///   <item><b>两边都是"中心写进来"的</b>（常规下发 / 降级快照）→ 比<b>版本号</b>，
///         版本相同再比时间戳。服务端的版本号单调递增，这一条就够。</item>
///   <item><b>本机改过、中心还不知道</b>（<see cref="HasUnsyncedLocalEdit"/>）→
///         比<b>时间戳</b>，不看版本号。理由：此时两边的版本号<b>不是同一个基准</b>
///         （本机从自己手上那份 +1，中心从它库里那份 +1），拿它们比大小会得出荒谬结论 ——
///         典型翻车是"全新机器上本机改了一次（v1），一连上中心就被中心那份 v5 盖掉"，
///         而那份 v5 其实是几天前的。</item>
/// </list>
///
/// <para><b>并发</b>：心跳、Welcome、ConfigChanged、手动刷新、本机页面保存五条路都可能来改它，
/// 用一道信号量把"网络同步"串起来、用一把锁护住状态。没有它，四路同时拉取会产生四个"新版本"，
/// 各自重启一遍任务 —— 表现是"改一次配置，循环发送被启动了四次"。</para>
/// </summary>
public sealed class AgentConfigManager
{
    private readonly AgentConfigClient _client;
    private readonly AgentLocalConfigStore _store;
    private readonly AgentOptions _options;
    private readonly LogService _log;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    private AgentConfigDto? _current;

    /// <summary>当前这份配置是<b>谁写进来的</b>（见 <see cref="AgentConfigOrigins"/>）。</summary>
    private string _origin = AgentConfigOrigins.Server;

    /// <summary>本机改过、还没推给中心。</summary>
    private bool _hasUnsyncedLocalEdit;

    /// <summary>
    /// 已知的<b>中心那份</b>配置版本（0 = 从没听说过）。
    /// <para>
    /// 它唯一的用途是：本机在<b>本机当前版本低于中心已知版本</b>时编辑，新版本号要跳过中心那一档，
    /// 否则本机改完（v2）一连上中心就被中心那份（v5）盖掉 —— 而用户明明是在中心那份之后改的。
    /// </para>
    /// </summary>
    private int _lastKnownServerVersion;

    public AgentConfigManager(
        AgentConfigClient client,
        AgentLocalConfigStore store,
        AgentOptions options,
        LogService log)
    {
        _client = client;
        _store = store;
        _options = options;
        _log = log;
    }

    /// <summary>此刻生效的配置（还没拿到时为 null）。</summary>
    public AgentConfigDto? Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    /// <summary>本机已应用的配置版本（0 = 还没有配置）。</summary>
    public int AppliedVersion
    {
        get
        {
            lock (_gate) return _current?.Version ?? 0;
        }
    }

    /// <summary>此刻这份配置的最后写入方（<see cref="AgentConfigOrigins"/> 里的值）。</summary>
    public string Origin
    {
        get
        {
            lock (_gate) return _origin;
        }
    }

    /// <summary>
    /// 本机改过配置、但中心还不知道（离线改的，或者推失败）。
    /// <para>页面上要把它显示出来 —— 否则用户以为"两边都知道了"，而中心那边还是旧的一份。</para>
    /// </summary>
    public bool HasUnsyncedLocalEdit
    {
        get
        {
            lock (_gate) return _hasUnsyncedLocalEdit;
        }
    }

    /// <summary>
    /// 这个版本号<b>中心也知道</b>吗（用来决定要不要回报「已应用」）。
    /// <para>
    /// ⚠️ 本机页面改出来的版本号（v7）在<b>推给中心之前</b>中心是不知道的（它还停在 v5）。
    /// 那一刻回报"已应用 v7"会让中心页面上出现"客户机回报的版本比服务端还高"的告警，
    /// 而那不是缺陷、也不是"有人改过库"—— 只是本机自己往前跑了一格。
    /// 所以：<b>中心不知道的版本号不回报</b>，等推上去（中心追平）之后再报。
    /// </para>
    /// </summary>
    public bool IsVersionKnownToServer(int version)
    {
        if (version <= 0) return false;

        lock (_gate) return version <= _lastKnownServerVersion;
    }

    /// <summary>
    /// 一份配置被应用上（含"从本机文件降级载入"）。<b>执行者订阅它来重启任务。</b>
    /// </summary>
    /// <param name="config">新生效的配置。</param>
    /// <param name="fromCache">true = 来自本机文件（中心不可达时的降级运行 / 重新启动后的恢复）。</param>
    public event Action<AgentConfigDto, bool>? Applied;

    /// <summary>
    /// 用本机那份配置先把任务跑起来（进程启动时调用一次）。
    /// <para>
    /// <b>这是整条降级路径的入口</b>：中心不可达时，客户机要能按最后一版配置继续工作
    /// （展厅现场的常见情形：中心那台机器在维护，但设备该发的报文一条都不能少）。
    /// 从 2026-10-14 起它还要负责"本机改过的那些，重启后还在"。
    /// </para>
    /// </summary>
    /// <returns>是否载入了配置。</returns>
    public bool RestoreFromStore()
    {
        if (!_options.Tasks.Enabled)
        {
            _log.Info("Config", "本机已在配置中停用任务下发（Agent:Tasks:Enabled=false），不载入本机配置。");
            return false;
        }

        var document = _store.Load();

        if (document is null || document.Config.Version <= 0)
        {
            _log.Info("Config", "本机还没有配置（等中心下发，或在本机配置页上直接编辑）。");
            return false;
        }

        bool unsynced;

        lock (_gate)
        {
            _current = document.Config;
            _origin = document.Origin;

            // 🔥 「待同步」必须跟着配置一起活过来（见 AgentLocalConfigDocument.HasUnsyncedLocalEdit）：
            //    离线改的那一版要能跨重启活到下次连上中心。只留在内存里的标志会在服务重启后丢掉，
            //    于是那份改动再也不会被推上去 —— 而且两边都不报错（本机照跑、中心显示旧配置），
            //    是最难发现的一类"静默不一致"。
            // ⚠️ 老文档没有这个字段（null）⇒ 按"来源是本机"推断：那正是这个字段出现之前
            //    唯一可能有未推改动的情形。
            unsynced = document.HasUnsyncedLocalEdit
                ?? document.Origin == AgentConfigOrigins.Local;

            _hasUnsyncedLocalEdit = unsynced;
        }

        _log.Info(
            "Config",
            $"已载入本机配置 v{document.Config.Version}"
            + $"（来源 {(document.Origin == AgentConfigOrigins.Local ? "本机页面" : "中心下发")}，"
            + $"发送 {Count(document.Config.SendTasks)} / 定时 {Count(document.Config.ScheduledTasks)} / "
            + $"启动 {Count(document.Config.LaunchTasks)}，"
            + $"UDP 通道 {(document.Config.Udp.Enabled ? $"开（{document.Config.Udp.Port}）" : "关")}）"
            + "—— 中心不可达时按它运行。"
            + (unsynced ? "（这份是本机改的、还没推给中心，连上后会补推。）" : string.Empty));

        RaiseApplied(document.Config, fromCache: true);
        return true;
    }

    /// <summary>
    /// 与服务端对齐一次配置。
    /// </summary>
    /// <param name="serverUrl">中心服务地址。</param>
    /// <param name="agentId">本机在服务端在线表里的 Id。</param>
    /// <param name="agentKey">本机长期凭据（同时是"只能读自己那份配置"的归属凭据）。</param>
    /// <param name="knownVersion">
    /// 服务端告诉我们的最新版本（来自 <c>Welcome</c> / <c>HeartbeatAck</c> / <c>ConfigChanged</c>）。
    /// <b>为 null = 不知道</b>，那就无条件拉一次。
    /// </param>
    /// <param name="force">true = 不看版本号强制拉取并重新应用（页面上的「重新下发配置」）。</param>
    public async Task<ConfigSyncOutcome> SyncAsync(
        string serverUrl,
        int agentId,
        string agentKey,
        int? knownVersion,
        bool force,
        CancellationToken cancellationToken)
    {
        if (!_options.Tasks.Enabled) return new ConfigSyncOutcome(false, AppliedVersion, null);

        if (knownVersion is { } known) RememberServerVersion(known);

        // 抢不到锁 = 已经有一次同步在飞：直接收工。它拉到之后版本就对齐了。
        if (!await _syncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new ConfigSyncOutcome(false, AppliedVersion, null);
        }

        try
        {
            AgentConfigDto? baseline;

            lock (_gate) baseline = _current;

            var applied = baseline?.Version ?? 0;

            // 已知服务端版本且不比本机新 → 连拉都不用拉（这是绝大多数心跳的情形）。
            // ⚠️ 本机"改过还没推"时这一步**照样跳过拉取**：那一刻本机的版本号已经跳过中心那一档，
            //    而且真正要紧的是别把本机那份盖掉（下面还有一道时间戳判据兜着）。
            if (!force && knownVersion is { } knownValue && knownValue <= applied)
            {
                return new ConfigSyncOutcome(false, applied, null);
            }

            var config = await _client.FetchAsync(serverUrl, agentId, agentKey, cancellationToken)
                .ConfigureAwait(false);

            if (config is null) return new ConfigSyncOutcome(false, applied, "无法获取配置");

            RememberServerVersion(config.Version);

            if (!force)
            {
                var decision = Decide(baseline, config);

                if (decision == Adoption.Skip)
                {
                    // 乱序到达的旧版：丢弃。**不回报** —— 回报一个更低的版本毫无意义
                    // （服务端那边也是"只增不减"），还会白占一条 Hub 消息。
                    return new ConfigSyncOutcome(false, applied, null);
                }

                if (decision == Adoption.KeepLocal)
                {
                    // 本机改过、而且改得比中心这份更晚 ⇒ 本机那份是对的，中心这份不要动它。
                    // ⚠️ 这里**只记日志、不回报**：中心那边还停在旧版本，"已应用 vN"要等本机那份
                    //    真正推上去（并把中心的版本顶到同一个数）之后才有意义。
                    _log.Info(
                        "Config",
                        $"中心的配置 v{config.Version}（保存于 {Describe(config.UpdatedAt)}）"
                        + $"比本机的改动 v{applied}（保存于 {Describe(baseline?.UpdatedAt)}）更早，保持本机那份；"
                        + "本机改动将在与中心同步时推送。");

                    return new ConfigSyncOutcome(false, applied, null);
                }
            }

            return Apply(config, fromCache: false, force);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 铁律 #6：同步失败绝不能把连接循环带崩 —— 保持现状即可。
            _log.Warn("Config", $"配置同步出错（保持现状）：{ex.Message}");
            return new ConfigSyncOutcome(false, AppliedVersion, ex.Message);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>
    /// <b>本机页面保存</b>：把一份增量补丁叠到本机当前配置上，立刻生效、立刻落盘。
    ///
    /// <para>校验走的是与服务端<b>同一份</b> Core 纯函数（<see cref="AgentConfigRules"/>）——
    /// 各写一份判据的下场是"本机放行、中心拒绝"（或反过来），而那种不一致
    /// 在界面上表现为"保存成功但机器没动"。</para>
    ///
    /// <para>⚠️ 新版本号取 <c>max(本机当前, 已知的中心版本) + 1</c>：跳过中心那一档，
    /// 这样"本机改完再连上中心"不会被中心那份旧配置盖掉。推给中心是下一步（见
    /// <see cref="MarkLocalEditPushed"/>）。</para>
    /// </summary>
    /// <param name="request">增量补丁；null 或某块为 null = 不动那一块。</param>
    /// <param name="error">校验失败时的<b>全部</b>问题（用「；」连接）；成功时为 null。</param>
    /// <returns>保存成功后的配置；失败时为 null。</returns>
    public AgentConfigDto? ApplyLocalEdit(AgentConfigSaveRequest? request, out string? error)
    {
        error = null;

        if (!_options.Tasks.Enabled)
        {
            error = "本机已在配置中停用任务下发（Agent:Tasks:Enabled=false），不接受本机配置改动。";
            return null;
        }

        AgentConfigDto config;

        lock (_gate)
        {
            var baseline = _current;

            var merged = AgentConfigRules.Normalize(AgentConfigRules.Merge(
                baseline is null ? null : AgentConfigMapper.ToContent(baseline),
                AgentConfigMapper.ToPatch(request)));

            var problems = AgentConfigRules.Validate(merged);

            if (problems.Count > 0)
            {
                error = string.Join("；", problems);
                return null;
            }

            var version = Math.Max(baseline?.Version ?? 0, _lastKnownServerVersion) + 1;

            config = AgentConfigMapper.ToDto(merged, version, BeijingTime.Now);

            _current = config;
            _origin = AgentConfigOrigins.Local;
            _hasUnsyncedLocalEdit = true;
        }

        _store.Save(config, AgentConfigOrigins.Local, hasUnsyncedLocalEdit: true);

        _log.Info(
            "Config",
            $"本机配置页保存了一版：v{config.Version}（发送 {Count(config.SendTasks)} / "
            + $"定时 {Count(config.ScheduledTasks)} / 启动 {Count(config.LaunchTasks)}，"
            + $"UDP 通道 {(config.Udp.Enabled ? $"开（{config.Udp.Port}）" : "关")}）—— 已在本机生效。");

        // 在锁外通知：执行者会重起任务（可能做同步 IO），不该占着锁。
        RaiseApplied(config, fromCache: false);

        return config;
    }

    /// <summary>
    /// 采纳一份<b>中心已经明确表态过</b>的配置（推送回执 / 冲突回执）。
    /// <para>
    /// 与 <see cref="SyncAsync"/> 的区别：那条路要<b>自己判断</b>新旧（可能选择保住本机那份），
    /// 这一条是中心已经说了"就按这份"—— 本机照办即可，不再比版本或时间戳。
    /// </para>
    /// <para>
    /// ⚠️ 它同时清掉"本机改动待同步"：中心收下了（或明确回绝了）本机那一版，
    /// 这件事就算结清了 —— 留着标志会让心跳循环一直重推一份中心已经处理过的配置。
    /// </para>
    /// </summary>
    public void AcceptServerConfig(AgentConfigDto config)
    {
        ArgumentNullException.ThrowIfNull(config);

        RememberServerVersion(config.Version);

        Apply(config, fromCache: false, force: false);

        _log.Info("Config", $"已采纳中心确认的那份配置（v{config.Version}，本机现在与它一致）。");
    }

    /// <summary>
    /// 放弃"把本机改动推给中心"这件事（中心明确回绝、而且重试不会有不同结果时用）。
    /// <para>
    /// ⚠️ 它<b>不改本机配置</b>：机器继续按本机那份跑（那正是用户在本机页面上要的），
    /// 只是不再反复重推 —— 否则心跳循环会 1 秒一次地重推一份永远不会被接受的配置。
    /// </para>
    /// <para>两边从此不一致，调用方必须留下日志痕迹。</para>
    /// </summary>
    public void AbandonLocalSync()
    {
        AgentConfigDto? current;
        string origin;

        lock (_gate)
        {
            _hasUnsyncedLocalEdit = false;
            current = _current;
            origin = _origin;
        }

        // ⚠️ 必须落盘：否则重启后又从文件里读出一个"待同步"，把一份中心已经明确回绝的配置
        //    再推一遍 —— 而重试的结果不会有任何不同。
        // `origin` 照原样保留："这份是本机写的"这个事实没变，变的只是"不再尝试同步"。
        if (current is not null) _store.Save(current, origin, hasUnsyncedLocalEdit: false);
    }

    /// <summary>清空本机配置（被吊销时用）：停任务 + 清文件。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _current = null;
            _origin = AgentConfigOrigins.Server;
            _hasUnsyncedLocalEdit = false;
        }

        _store.Clear();
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    /// <summary>要不要采用中心拉回来的这一份。</summary>
    private enum Adoption
    {
        /// <summary>采用（并落盘 + 重启任务）。</summary>
        Take,

        /// <summary>丢弃（比手上这份更早）。</summary>
        Skip,

        /// <summary>本机改过且改得更晚 —— 保住本机那份。</summary>
        KeepLocal,
    }

    private Adoption Decide(AgentConfigDto? baseline, AgentConfigDto candidate)
    {
        if (baseline is null) return Adoption.Take;

        if (_hasUnsyncedLocalEdit)
        {
            // 两边版本号不是同一个基准（见类注释）⇒ 只能比时间戳。
            var candidateAt = candidate.UpdatedAt ?? DateTimeOffset.MinValue;
            var localAt = baseline.UpdatedAt ?? DateTimeOffset.MinValue;

            return candidateAt > localAt ? Adoption.Take : Adoption.KeepLocal;
        }

        return AgentConfigMapper.IsNewer(candidate, baseline) ? Adoption.Take : Adoption.Skip;
    }

    private ConfigSyncOutcome Apply(AgentConfigDto config, bool fromCache, bool force)
    {
        AgentConfigDto? previous;

        lock (_gate)
        {
            previous = _current;
            _current = config;
            _origin = AgentConfigOrigins.Server;

            // 中心那份已经追平（或超过了）本机 ⇒ 本机不再有待同步的改动。
            _hasUnsyncedLocalEdit = false;
        }

        // 中心那份进来了 ⇒ 「待同步」一定为 false（上面刚清掉）。落盘时一并写死，
        // 免得重启后又从文件里读出一个已经作废的"待同步"。
        if (config.Version > 0) _store.Save(config, AgentConfigOrigins.Server, hasUnsyncedLocalEdit: false);

        // ── 内容没变就**不重起任务** ──
        // 两种常见情形：① 中心保存了一次但一个字段都没改（点保存就会 +1 版本）；
        // ② 本机那份刚推上去，中心原样推回来（#219 的回路）。
        // 这两种情况下重起一代任务都是白干 —— 循环发送会从头计时、延时任务会重新等一遍。
        // ⚠️ 但 force（「重新下发配置」）**必须**照样重起：那条路就是为"机器看起来没按配置动"
        //    准备的，内容相同恰恰是它的典型情形。
        if (!force && previous is not null && SameContent(previous, config))
        {
            _log.Info(
                "Config",
                $"配置版本推进到 v{config.Version}，但内容与 v{previous.Version} 完全一致 —— 不重起任务。");

            return new ConfigSyncOutcome(true, config.Version, null);
        }

        _log.Info(
            "Config",
            $"配置已换版：v{previous?.Version ?? 0} → v{config.Version}（发送 {Count(config.SendTasks)} / "
            + $"定时 {Count(config.ScheduledTasks)} / 启动 {Count(config.LaunchTasks)}，"
            + $"UDP 通道 {(config.Udp.Enabled ? $"开（{config.Udp.Port}）" : "关")}）");

        RaiseApplied(config, fromCache);

        return new ConfigSyncOutcome(true, config.Version, null);
    }

    private void RememberServerVersion(int version)
    {
        if (version <= 0) return;

        lock (_gate)
        {
            if (version > _lastKnownServerVersion) _lastKnownServerVersion = version;
        }
    }

    /// <summary>两份配置的<b>内容</b>是否等价（不含版本号与时间戳）。</summary>
    private static bool SameContent(AgentConfigDto a, AgentConfigDto b)
        => ControlJson.Serialize(AgentConfigMapper.ToContent(a))
           == ControlJson.Serialize(AgentConfigMapper.ToContent(b));

    private void RaiseApplied(AgentConfigDto config, bool fromCache)
    {
        try
        {
            Applied?.Invoke(config, fromCache);
        }
        catch (Exception ex)
        {
            _log.Error("Config", "应用配置时执行者报错（配置本身已生效）", ex);
        }
    }

    private static int Count<T>(IReadOnlyList<T>? list) => list?.Count ?? 0;

    private static string Describe(DateTimeOffset? value)
        => value is { } v ? v.ToString("yyyy-MM-dd HH:mm:ss") : "未知时间";
}
