using System.Collections.Concurrent;
using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using ControlServer.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ControlServer.Server.Services;

/// <summary>
/// 客户机配置的读写与下发（M4.5）。
/// <para>
/// 它把三件事缝在一起：<b>落库</b>（<see cref="IAgentConfigStore"/>）、
/// <b>推送</b>（给客户机发 <c>ConfigChanged</c>、给网页发 <c>AgentConfigChanged</c>）、
/// 以及<b>版本号的内存缓存</b>。
/// </para>
/// <para>
/// <b>为什么要有那个缓存</b>：心跳应答（每台每 10 秒一次）要回传"服务端当前版本"，
/// 好让客户机发现自己落后（推送丢了也能自愈）。100 台 = 10 次/秒的库读，
/// 虽然 SQLite 撑得住，但这是完全不必要的 —— 版本号是个很小的整数，
/// 缓存在内存里、<b>且只由本服务写</b>，不会出现"两处数字不一致"。
/// </para>
/// <para>
/// ⚠️ <b>缓存是"写穿透"的</b>：保存 / 删除都先落库再改缓存。反过来（先改缓存）会在
/// 落库失败时留下一个"页面说 v5、库里还是 v4"的假象。
/// </para>
/// </summary>
public sealed class AgentConfigService
{
    private readonly IAgentConfigStore _store;
    private readonly IAgentHostStore _hosts;
    private readonly AgentHostRegistry _registry;
    private readonly IHubContext<AgentHub> _agentHub;
    private readonly INotifier _notifier;
    private readonly LogService _log;

    /// <summary>Id → 当前配置版本。没有配置的机器缓存 0（= 与"没配过"同一语义）。</summary>
    private readonly ConcurrentDictionary<int, int> _versions = new();

    public AgentConfigService(
        IAgentConfigStore store,
        IAgentHostStore hosts,
        AgentHostRegistry registry,
        IHubContext<AgentHub> agentHub,
        INotifier notifier,
        LogService log)
    {
        _store = store;
        _hosts = hosts;
        _registry = registry;
        _agentHub = agentHub;
        _notifier = notifier;
        _log = log;
    }

    /// <summary>读该机的配置（null = 从未保存过）。</summary>
    public Task<AgentConfigRecord?> FindAsync(int agentId, CancellationToken cancellationToken = default)
        => _store.FindAsync(agentId, cancellationToken);

    /// <summary>该机当前配置版本（0 = 没有配置）。命中缓存直接返回，未命中才查库。</summary>
    public async Task<int> GetVersionAsync(int agentId, CancellationToken cancellationToken = default)
    {
        if (_versions.TryGetValue(agentId, out var cached)) return cached;

        var record = await _store.FindAsync(agentId, cancellationToken).ConfigureAwait(false);
        var version = record?.Version ?? 0;

        _versions[agentId] = version;
        return version;
    }

    /// <summary>批量取版本（列表页用）。</summary>
    public async Task<IReadOnlyDictionary<int, int>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        var all = await _store.ListVersionsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var pair in all) _versions[pair.Key] = pair.Value;

        return all;
    }

    /// <summary>
    /// 保存配置：落库（版本 +1）→ 更新缓存 → 通知客户机与网页。
    /// </summary>
    public async Task<AgentConfigRecord> SaveAsync(
        int agentId,
        AgentConfigContent content,
        CancellationToken cancellationToken = default)
    {
        var saved = await _store.SaveAsync(agentId, content, cancellationToken).ConfigureAwait(false);

        _versions[agentId] = saved.Version;

        await PushConfigChangedAsync(agentId, saved.Version, cancellationToken).ConfigureAwait(false);

        _log.Info("Agent", $"客户机配置已保存（Id={agentId}）：v{saved.Version}，"
            + $"发送 {saved.Content.SendTasks.Count} / 定时 {saved.Content.ScheduledTasks.Count} / 启动 {saved.Content.LaunchTasks.Count}");

        return saved;
    }

    /// <summary>删除该机的配置（移除设备时连带清理）。</summary>
    public async Task<bool> DeleteAsync(int agentId, CancellationToken cancellationToken = default)
    {
        var ok = await _store.DeleteAsync(agentId, cancellationToken).ConfigureAwait(false);

        _versions.TryRemove(agentId, out _);
        return ok;
    }

    /// <summary>
    /// 收下<b>客户机自己推来</b>的那一版配置（2026-10-14 的双向同步）。
    ///
    /// <para>判据是「后写覆盖」，两条任一成立就收：</para>
    /// <list type="number">
    ///   <item>客户机的版本号<b>更高</b>（客户机保存时会取 <c>max(本机, 已知中心版本) + 1</c>，
    ///         所以它手上那份只要动过，版本号必然高过中心）；</item>
    ///   <item>否则看<b>保存时刻</b>：客户机那份比中心这份<b>更晚</b>。
    ///         兜的是"客户机从没成功同步过就离线改了配置"——那时它的版本号可能低于中心，
    ///         光看版本号会把一份刚写的改动判成旧的。</item>
    /// </list>
    ///
    /// <para>两条都不成立 ⇒ 中心这份更晚 ⇒ 回 <c>conflict</c>，<b>并把中心这份一起回给客户机</b>：
    /// 让它这一次往返就把该改的改掉，不必再等一次推送。这就是"后写覆盖"的落地形态 ——
    /// 落败的一方当场改用对方那份，不留"两边各跑一份"的中间状态。</para>
    ///
    /// <para>⚠️ <b>落库的版本号永远是"中心那份 + 1"</b>，不采用客户机送来的数字：
    /// 中心这条水位是单调递增的、被心跳与页面共同依赖，让客户机来指定它会引入
    /// "两个写入方各自编号"的经典冲突。客户机拿回执里的新版本号对齐即可。</para>
    /// </summary>
    public async Task<AgentConfigSaveResult> PushFromAgentAsync(
        int agentId,
        AgentConfigPushDto? pushed,
        CancellationToken cancellationToken = default)
    {
        if (pushed?.Content is null)
        {
            return new AgentConfigSaveResult(false, "empty", "推送的配置为空。", null);
        }

        var existing = await _store.FindAsync(agentId, cancellationToken).ConfigureAwait(false);

        if (!ShouldAccept(existing, pushed))
        {
            var current = existing is null ? null : AgentConfigMapper.ToDto(existing);

            _log.Info(
                "Agent",
                $"客户机推来的配置比中心这份更早（Id={agentId}，本机 v{pushed.Version}，"
                + $"中心 v{existing?.Version ?? 0}）—— 已回绝并让本机改用中心那份。");

            return new AgentConfigSaveResult(
                false,
                "conflict",
                $"中心这份更晚（v{existing?.Version ?? 0}），已让客户机改用中心那份。",
                current);
        }

        // 与页面保存走**完全相同**的合并 / 规范化 / 校验三步：两边判据只有一份，
        // 否则会出现"客户机放行、中心拒绝"（或反过来），表现为"本机保存成功但中心那份没变"。
        var normalized = AgentConfigRules.Normalize(
            AgentConfigRules.Merge(existing?.Content, AgentConfigMapper.ToPatch(pushed.Content)));

        var problems = AgentConfigRules.Validate(normalized);

        if (problems.Count > 0)
        {
            _log.Warn("Agent", $"客户机推来的配置未通过校验（Id={agentId}）：{string.Join("；", problems)}");

            return new AgentConfigSaveResult(false, "invalid", string.Join("；", problems), null);
        }

        // 复用 SaveAsync：落库（版本 +1）→ 刷版本缓存 → 推 ConfigChanged 给客户机、通知网页。
        // 推回给"刚推上来的那台机器"是**刻意保留**的：它的版本水位要靠这条消息对齐，
        // 而内容与它手上那份一致时会走"内容没变、不重起任务"那条路（见 AgentConfigManager.Apply）。
        var saved = await SaveAsync(agentId, normalized, cancellationToken).ConfigureAwait(false);

        _log.Info(
            "Agent",
            $"已收下客户机自己改的那一版（Id={agentId}）：本机 v{pushed.Version} → 中心 v{saved.Version}。");

        return new AgentConfigSaveResult(true, "ok", $"已收下本机改动（中心 v{saved.Version}）。", AgentConfigMapper.ToDto(saved));
    }

    /// <summary>这份"客户机推来的"要不要收（见 <see cref="PushFromAgentAsync"/> 的注释）。</summary>
    private static bool ShouldAccept(AgentConfigRecord? existing, AgentConfigPushDto pushed)
    {
        if (existing is null) return true;

        if (pushed.Version > existing.Version) return true;

        // ⚠️ 只有**两边都有时间戳**时才拿它们比。缺失一律按"不比"处理 ——
        // 用一个凭空补出来的时刻去覆盖对方，是最说不清的一类数据损坏。
        return pushed.UpdatedAt is { } sentAt && sentAt > existing.UpdatedAt;
    }

    /// <summary>
    /// 记录客户机回报的「已应用配置版本」。
    /// <para>
    /// 落库（只增不减，由 SQL 的 <c>MAX()</c> 保证）+ <b>同步刷内存快照</b> ——
    /// 不刷的话，页面上"已应用 v3"要等最多 60 秒（下一次快照刷新）才出现，
    /// 而这段时间里运维看到的是"已下发 v3 / 还没应用"，会以为下发失败又点一次。
    /// </para>
    /// </summary>
    public async Task AcceptAppliedAsync(int agentId, AgentConfigAppliedDto applied, CancellationToken cancellationToken = default)
    {
        if (!applied.Ok)
        {
            // 应用失败也要留痕：否则"机器在跑旧配置"这件事在服务端毫无迹象。
            _log.Warn("Agent", $"客户机回报配置应用失败（Id={agentId}，v{applied.Version}）：{applied.Message}");
            return;
        }

        if (applied.Version <= 0)
        {
            _log.Warn("Agent", $"客户机回报了非法的配置版本（Id={agentId}，v{applied.Version}），已忽略。");
            return;
        }

        await _hosts.SetAppliedConfigVersionAsync(agentId, applied.Version, cancellationToken).ConfigureAwait(false);

        // 就地刷内存快照。⚠️ **不能**用 `ApplyChange` —— 那是"实时态"（连接 / 地址 / 最后心跳）
        // 的钩子，除了 IsBlocked/LastSeenAt 它不读传入 host 的其它字段，拿它刷版本号是个
        // 静默的空操作（这里曾经就是那么写的：库改了、页面永远显示"未回报"）。
        _registry.SetAppliedConfigVersion(agentId, applied.Version);
    }

    /// <summary>
    /// 通知客户机"配置变了，来拉最新的"。
    /// <para>
    /// <b>离线机器不推</b>：它连连接 Id 都没有。没关系 —— 它上线时 <c>Welcome</c> 里就会拿到
    /// 最新版本号，发现自己落后就会去拉。这是"推送 + 版本对齐"两条腿里的第二条。
    /// </para>
    /// </summary>
    public async Task PushConfigChangedAsync(int agentId, int version, CancellationToken cancellationToken = default)
    {
        var connectionId = _registry.ConnectionIdOf(agentId);

        if (!string.IsNullOrEmpty(connectionId))
        {
            try
            {
                await _agentHub.Clients.Client(connectionId)
                    .SendAsync(AgentHubMethods.ConfigChanged, new AgentConfigChangedDto(version), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 推送失败不该让"保存配置"这个操作失败：配置已经在库里了，
                // 客户机下次心跳/重连时会靠版本对齐自己发现。这里只留痕。
                _log.Warn("Agent", $"下发配置变更通知失败（Id={agentId}，v{version}）：{ex.Message}");
            }
        }

        await _notifier.AgentConfigChangedAsync(agentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 让客户机"无论版本是否变化，立刻重新拉一遍配置"。
    /// <para>
    /// 与 <see cref="PushConfigChangedAsync"/> 的分工：那条是"版本变了"的常规通知；
    /// 这条是运维在页面上点的「重新下发配置」—— 用于"机器看起来没按配置动"的现场排障，
    /// 不依赖版本判断，强制拉一次。
    /// </para>
    /// </summary>
    public async Task<bool> RequestReloadAsync(int agentId, CancellationToken cancellationToken = default)
    {
        var connectionId = _registry.ConnectionIdOf(agentId);

        if (string.IsNullOrEmpty(connectionId)) return false;

        try
        {
            await _agentHub.Clients.Client(connectionId)
                .SendAsync(
                    AgentHubMethods.Command,
                    new AgentCommandDto(AgentCommandNames.RefreshConfig, null),
                    cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Agent", $"下发「重新拉取配置」指令失败（Id={agentId}）", ex);
            return false;
        }
    }
}
