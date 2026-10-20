using System.Collections.Concurrent;
using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using ControlServer.Server.Options;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.Services;

/// <summary>
/// 客户端在线表的**进程内状态**（M4.1）。
/// <para>
/// <b>它解决什么问题</b>：心跳每 10 秒一台一次，若每次都写 SQLite，
/// 20 台机器就是 2 次写/秒；上百台会把 WAL 写满、把"改个备注"这种真正的写操作挤在后面。
/// 因此心跳<b>只更新内存</b>，由后台任务每 60 秒成批落盘一次。
/// </para>
/// <para>
/// <b>为什么还要缓存主机列表</b>：状态判定（<see cref="AgentPresence.Classify"/>）需要
/// "最后一次心跳"，而它分散在库里（重启前的）与内存里（本次运行期间的）。
/// 每 5 秒去库里全量读一遍、还要把每行的主机信息 JSON 反序列化一次 —— 纯属浪费。
/// 这里持有一份快照：注册 / 改名 / 拉黑 / 删除时立即失效重建，另有 60 秒兜底刷新。
/// </para>
/// <para>
/// <b>进程内状态 ⇒ 重启会丢</b>。这是刻意的取舍：丢了只表现为"页面上的最后在线时间
/// 回到了 60 秒前的快照"，而那一份是从库里读回来的，<b>永远不会丢成 null</b>
/// （null 的语义是"从未连接"，把已连过的机器显示成"从未连接"才是真正会误导人的错误）。
/// </para>
/// </summary>
public sealed class AgentHostRegistry
{
    private readonly IAgentHostStore _store;
    private readonly LogService _log;

    /// <summary>主机列表快照（不可变数组；读方无锁，替换时整体换代）。</summary>
    private volatile AgentHost[] _hosts = Array.Empty<AgentHost>();

    private readonly object _sync = new();

    /// <summary>本次运行期间的内存态（按主机 Id）。</summary>
    private readonly Dictionary<int, LiveState> _live = new();

    /// <summary>
    /// 最新一帧遥测摘要（M4.2 的"热层"，按主机 Id）。
    /// <para>
    /// <b>为什么放这里</b>：列表页每刷新一次就要显示"此刻 CPU / 内存多少"，而那是一次
    /// 高频、全量、只读的渲染。放内存里，页面刷新完全不碰数据库；落盘是另一条路（<c>EfAgentTelemetryStore</c>）。
    /// </para>
    /// <para>⚠️ <b>它只由实时上报填充，不从库里加载</b>：离线的机器在列表上"没有实时值"才是对的 ——
    /// 拿一份几小时前的 CPU 占用冒充"此刻"，比显示「—」危险得多。</para>
    /// </summary>
    private readonly Dictionary<int, AgentMetricsSummaryDto> _metrics = new();

    /// <summary>上一次广播出去的状态（用于只推"跃迁"，不推"每一跳"）。</summary>
    private readonly Dictionary<int, string> _lastBroadcastState = new();

    /// <summary>上一次广播出去的 UDP 通道状态签名（2026-10-14，见 <see cref="DiffUdpStates"/>）。</summary>
    private readonly Dictionary<int, string> _lastUdpSignature = new();

    public AgentHostRegistry(IAgentHostStore store, LogService log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>当前主机列表快照（只读，可能比库落后最多 60 秒 —— 见类注释）。</summary>
    public IReadOnlyList<AgentHost> Hosts => _hosts;

    /// <summary>本次运行期间是否还持有该主机（用于"它现在到底连没连着"）。</summary>
    public bool IsConnected(int id)
    {
        lock (_sync)
        {
            return _live.TryGetValue(id, out var live) && live.ConnectionId is not null;
        }
    }

    /// <summary>该主机的 SignalR 连接 Id（离线时为 null）；用于下发指令。</summary>
    public string? ConnectionIdOf(int id)
    {
        lock (_sync)
        {
            return _live.TryGetValue(id, out var live) ? live.ConnectionId : null;
        }
    }

    /// <summary>
    /// 从库里重新读一份快照。启动时调一次，之后由后台任务周期调用（60 秒）。
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var hosts = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
            _hosts = hosts.ToArray();

            // 🔥 给每一台装载进来的机器**建一条"本次运行期"的内存态**（2026-10-15）。
            //
            // 为什么必须有这一步：状态判定要回答"这条长连接在不在"（见 AgentPresence.Classify
            // 的 longConnection），而"内存表里没有这台机器"与"内存表里记着它没连着"是两回事。
            // 库里那条 LastSeenAt 是**落盘值**（最多落后 60 秒），中心刚重启时它可能还很新 ——
            // 不建这条记录，页面就会在重启后继续显示绿色，而那时**一条连接都没有**
            // （现场表现："明明在线，一点关机却说客户端离线"）。
            //
            // ⚠️ 用 GetOrAdd：**绝不能覆盖已有的连接 Id** —— 连通着的那台机器由 MarkConnected
            //    写过，这里只补空缺。
            lock (_sync)
            {
                foreach (var host in _hosts) GetOrAdd(host.Id);
            }
        }
        catch (Exception ex)
        {
            // 刷新失败保留旧快照：页面顶多显示得旧一点，绝不该因此变成空列表
            // （"一台机器都没有"会让人以为客户端全掉线了）。
            _log.Error("Agent", "客户端在线表刷新失败（沿用上一份快照）", ex);
        }
    }

    /// <summary>
    /// 计算某台主机的**有效最后心跳**：内存态与库态取较新的那个。
    /// </summary>
    public DateTimeOffset? EffectiveLastSeen(AgentHost host)
    {
        lock (_sync)
        {
            if (_live.TryGetValue(host.Id, out var live) && live.LastSeenAt is { } liveSeen)
            {
                return host.LastSeenAt is { } dbSeen && dbSeen > liveSeen ? dbSeen : liveSeen;
            }

            return host.LastSeenAt;
        }
    }

    /// <summary>
    /// 把一台主机序列化成 DTO（合并内存态；状态由纯函数算出）。
    /// <para>
    /// ⚠️ <b><c>ConfigVersion</c> 刻意留默认值 0</b>：它存在 <c>agent_configs</c> 表里，
    /// 而列表接口要一次批量取回全部版本（逐台查在 100 台时就是 100 次查询）——
    /// 所以由<b>调用方</b>用 <c>with { ConfigVersion = … }</c> 补上，这里不查库。
    /// </para>
    /// <para>
    /// 反过来 <c>AppliedConfigVersion</c> 就在这里带上：它是主机自己的字段
    /// （落在 <c>agent_hosts</c>、并随内存快照更新），不需要额外查询，
    /// 而且三个调用点都得用同一个值 —— 漏掉任何一处都只会表现为页面永远显示"未回报"。
    /// </para>
    /// </summary>
    public AgentHostDto ToDto(AgentHost host, AgentOptions options, DateTimeOffset now)
    {
        var lastSeen = EffectiveLastSeen(host);

        string? address;
        string? version;
        AgentMetricsSummaryDto? metrics;
        AgentUdpChannelStateDto? udp;
        bool? longConnection;

        lock (_sync)
        {
            _live.TryGetValue(host.Id, out var live);
            address = live?.Address ?? host.LastAddress;
            version = live?.AgentVersion ?? host.AgentVersion;
            _metrics.TryGetValue(host.Id, out metrics);

            // ⚠️ UDP 实时态**只从内存读**（与 _metrics 同一条纪律）：它由心跳填充，
            //    库里没有、也不该有 —— 中心重启后它回到 null（"不知道"），
            //    而不是从某个旧值假装"此刻还在听"。
            udp = live?.Udp;

            // 🔥 长连接事实（2026-10-15）：内存表里没有这台机器 ⇒ null（不知道）；
            //    有记录 ⇒ 它的 ConnectionId 在不在，就是"中心此刻能不能给它下发"的**唯一**依据。
            //    ⚠️ 别写成 `live?.ConnectionId is not null` —— 那样 live 为 null 时会得到 false
            //    （"确定它没连着"），与"不知道"混为一谈，正是这个 BUG 的另一副面孔。
            longConnection = live is null ? null : live.ConnectionId is not null;
        }

        var state = AgentPresence.Classify(
            lastSeen,
            now,
            TimeSpan.FromSeconds(options.StaleAfterSeconds),
            TimeSpan.FromSeconds(options.OfflineAfterSeconds),
            host.IsBlocked,
            longConnection);

        return new AgentHostDto(
            host.Id,
            host.MachineKey,
            host.DisplayName,
            host.Group,
            host.Note,
            state,
            AgentStates.DisplayName(state),
            host.IsBlocked,
            version,
            host.RegisteredAt,
            lastSeen,
            address,
            host.Info?.HostName,
            host.Info?.OsDescription,
            host.Info?.CpuName,
            host.Info?.LogicalProcessors,
            host.Info?.TotalMemoryMb,
            metrics,
            AppliedConfigVersion: host.AppliedConfigVersion,
            LastTargetDir: host.LastTargetDir,
            LastSourceDir: host.LastSourceDir,
            UdpChannel: udp);
    }

    /// <summary>
    /// 取某台客户机的<b>有效网络地址</b>：内存里的（本次运行期心跳报的）优先，其次才是库里的。
    /// <para>
    /// 🔥 <b>为什么不直接用 <c>host.LastAddress</c></b>：那是<b>落盘</b>值，而心跳只更新内存、
    /// 每 60 秒才成批落盘一次 —— 于是中心刚重启、或机器刚换 IP 的那段时间里，
    /// 库里的地址要么是空的、要么是旧的。拿它去发 UDP，结果就是
    /// "明明几十秒前刚收到它心跳，却说不知道它的地址"。
    /// </para>
    /// <para>UDP 代发用它；页面显示继续用 DTO 里那个值（那里本来就是同样的优先级）。</para>
    /// </summary>
    public string? EffectiveAddress(AgentHost host)
    {
        lock (_sync)
        {
            if (_live.TryGetValue(host.Id, out var live) && !string.IsNullOrWhiteSpace(live.Address))
            {
                return live.Address;
            }

            return host.LastAddress;
        }
    }

    /// <summary>
    /// 取某台客户机<b>自己报上来的</b> UDP 指令通道状态（没有则 null）。
    /// <para>
    /// 🔥 <b>代发 UDP 指令前必须问它</b>，而不是看配置里写了什么：配置是"我要它怎样"，
    /// 这份是"它现在怎样"。两者不一致正是"点了没反应"的头号成因。
    /// </para>
    /// <para>⚠️ 进程内状态 ⇒ 中心重启后为 null（等下一次心跳）。null = <b>不知道</b>，
    /// 调用方要退回"看配置"并如实说明，不能把它当成"没在听"。</para>
    /// </summary>
    public AgentUdpChannelStateDto? UdpStateOf(int id)
    {
        lock (_sync)
        {
            return _live.TryGetValue(id, out var live) ? live.Udp : null;
        }
    }

    /// <summary>
    /// 就地更新某台主机的「已应用配置版本」（M4.5）。
    /// <para>
    /// 🔥 <b>只写库是不够的</b>：列表与详情读的是这里的内存快照（<c>_hosts</c>），
    /// 而它每 60 秒才整体重建一次。只写库的话，页面上"已应用 v3"最多要晚 60 秒才出现 ——
    /// 运维看着"灰着不动"会以为下发失败、再点一次保存，于是版本号白白涨一格、客户机多拉一次配置。
    /// </para>
    /// <para>
    /// ⚠️ <b>也不能用 <see cref="ApplyChange"/></b>：那是"本次运行期的实时态"（连接 / 地址 /
    /// 最后心跳）的钩子，除了 <c>IsBlocked</c> 与 <c>LastSeenAt</c> 它<b>不读取传入 host 的其它字段</b>，
    /// 拿它刷版本号是个静默的空操作（这里曾经就是这么写的，症状是页面永远显示"未回报"）。
    /// </para>
    /// <para>只替换数组里的那一个元素：读方依旧无锁（整体换代，见 <see cref="Hosts"/>）。</para>
    /// </summary>
    /// <returns>是否真的更新了（主机不在快照里 / 传来的版本没有更新 ⇒ false）。</returns>
    public bool SetAppliedConfigVersion(int id, int version)
    {
        lock (_sync)
        {
            var hosts = _hosts;
            var index = Array.FindIndex(hosts, h => h.Id == id);

            if (index < 0) return false;

            // 只增不减 —— 与 SQL 侧的 `MAX(COALESCE(AppliedConfigVersion, -1), @v)` 同一取舍：
            // 迟到的旧回报不能把页面上的版本退回去。
            if (version <= (hosts[index].AppliedConfigVersion ?? 0)) return false;

            var next = (AgentHost[])hosts.Clone();
            next[index] = hosts[index] with { AppliedConfigVersion = version };
            _hosts = next;

            return true;
        }
    }

    /// <summary>
    /// 就地更新某台主机的「上次用的落脚目录」（2026-10-13）。
    /// <para>
    /// 🔥 <b>为什么不只写库</b>：与 <see cref="SetAppliedConfigVersion"/> 同一个理由 ——
    /// 下发页读的是内存快照，只写库的话它要等最多 60 秒（下一次快照刷新）才会回显，
    /// 而运维在这段时间里会以为"没记住"再点一次。
    /// </para>
    /// <para><paramref name="targetDir"/> / <paramref name="sourceDir"/> 传 null = 不动那一项。</para>
    /// </summary>
    public bool SetLastPath(int id, string? targetDir, string? sourceDir)
    {
        if (targetDir is null && sourceDir is null) return false;

        lock (_sync)
        {
            var hosts = _hosts;
            var index = Array.FindIndex(hosts, h => h.Id == id);

            if (index < 0) return false;

            var next = (AgentHost[])hosts.Clone();

            next[index] = hosts[index] with
            {
                LastTargetDir = targetDir ?? hosts[index].LastTargetDir,
                LastSourceDir = sourceDir ?? hosts[index].LastSourceDir,
            };

            _hosts = next;

            return true;
        }
    }

    /// <summary>
    /// 更新某台机器的"最新一帧"遥测（M4.2）。
    /// <para><b>只保留时间更新的那一帧</b>：批次可能因重连而乱序到达，
    /// 用一个更旧的样本覆盖新的，会让页面上的曲线突然往回跳。</para>
    /// </summary>
    public void SetMetrics(int id, AgentMetricsSummaryDto metrics)
    {
        lock (_sync)
        {
            if (_metrics.TryGetValue(id, out var current)
                && current.At is { } currentAt
                && metrics.At is { } incoming
                && incoming <= currentAt)
            {
                return;
            }

            _metrics[id] = metrics;
        }
    }

    /// <summary>取某台机器的最新一帧遥测（没有则 null）。</summary>
    public AgentMetricsSummaryDto? MetricsOf(int id)
    {
        lock (_sync)
        {
            return _metrics.TryGetValue(id, out var metrics) ? metrics : null;
        }
    }

    /// <summary>一台主机刚刚连上（SignalR 连接建立）。</summary>
    public void MarkConnected(int id, string connectionId)
    {
        lock (_sync)
        {
            var live = GetOrAdd(id);
            live.ConnectionId = connectionId;
            live.LastSeenAt = BeijingTime.Now;
        }
    }

    /// <summary>
    /// 存下客户机报上来的 UDP 指令通道实时态（2026-10-14）。
    /// <para>
    /// 🔥 <b>刻意与 <see cref="MarkSeen"/> 分开</b>：那条消息的路由是"心跳"，
    /// 而心跳的 Hub 方法签名<b>不许加参数</b>（SignalR 的实参个数必须与签名严格一致，
    /// 加参数会让旧客户端整条心跳失败 —— 见 <c>AgentHub.Heartbeat</c> 的实测备注）。
    /// 所以状态走一条<b>独立的消息</b>，这里也就需要一个不改"最后心跳时刻"的入口：
    /// 拿它冒充心跳会把离线判定也一起推后，那是另一码事。
    /// </para>
    /// </summary>
    public void SetUdpState(int id, AgentUdpChannelStateDto state)
    {
        lock (_sync)
        {
            GetOrAdd(id).Udp = state;
        }
    }

    /// <summary>
    /// 收到心跳。<b>不写库</b> —— 只标脏，由后台任务成批落盘。
    /// </summary>
    /// <param name="udp">
    /// 客户机自己报的 UDP 通道实时态（2026-10-14）—— 走心跳这一趟是<b>兼容路径</b>：
    /// 生产上由 <c>ReportUdpChannel</c> 独立上报，这里留着是为了让"一次调用带齐"仍然可用
    /// （例如测试与将来的协议演进）。
    /// <para>
    /// ⚠️ <b>null = 这一跳没带状态，保持上一次的值不动</b>。
    /// 这与"客户机明确说它没在听"（<c>Listening=false</c>）是两回事。
    /// </para>
    /// </param>
    public void MarkSeen(int id, string? address, string? agentVersion, AgentUdpChannelStateDto? udp = null)
    {
        lock (_sync)
        {
            var live = GetOrAdd(id);
            live.LastSeenAt = BeijingTime.Now;
            if (!string.IsNullOrWhiteSpace(address)) live.Address = address;
            if (!string.IsNullOrWhiteSpace(agentVersion)) live.AgentVersion = agentVersion;
            if (udp is not null) live.Udp = udp;
        }
    }

    /// <summary>
    /// 连接断开。
    /// <para>
    /// <b>把"最后心跳"直接钉成"断开的那一刻"</b>：否则离线计时从最后一次心跳起算，
    /// 而断线时刻通常比它晚（进程被杀前那一跳可能已过去 9 秒）。钉住之后，
    /// "关机 ≤90 秒变离线"这条验收才有确定的起点。
    /// </para>
    /// </summary>
    public void MarkDisconnected(int id, string connectionId)
    {
        lock (_sync)
        {
            if (!_live.TryGetValue(id, out var live)) return;

            // 同一条连接才作数：重连后旧连接的 OnDisconnected 可能姗姗来迟，
            // 若不加这道判断，新连接刚建立的在线状态会被旧连接的回调抹掉。
            if (!string.Equals(live.ConnectionId, connectionId, StringComparison.Ordinal)) return;

            live.ConnectionId = null;
            live.LastSeenAt = BeijingTime.Now;
            live.FlushRequested = true;
        }
    }

    /// <summary>注册 / 改名 / 拉黑 / 删除后调用：内存态跟着变，并标记需要落盘。</summary>
    public void ApplyChange(AgentHost host)
    {
        lock (_sync)
        {
            var live = GetOrAdd(host.Id);
            live.FlushRequested = true;

            if (host.IsBlocked)
            {
                // 拉黑 = 立刻切断。留着长连接等于"说不要它了、对面却还能收指令"，
                // 服务端与页面会同时表现出不一致。
                live.LastSeenAt = host.LastSeenAt;
            }
        }
    }

    /// <summary>删掉一台主机（内存态一并清掉，含遥测热层）。</summary>
    public bool Forget(int id)
    {
        lock (_sync)
        {
            _lastBroadcastState.Remove(id);
            _lastUdpSignature.Remove(id);
            _metrics.Remove(id);
            return _live.Remove(id);
        }
    }

    /// <summary>
    /// 取出"需要落盘"的条目并更新其已落盘水位。
    /// <para>
    /// <paramref name="force"/> = true 时把所有有变化的都返回（停机 / 断开场景用）。
    /// </para>
    /// </summary>
    public IReadOnlyList<PendingTouch> TakePendingTouches(DateTimeOffset now, TimeSpan minInterval, bool force)
    {
        var result = new List<PendingTouch>();

        lock (_sync)
        {
            foreach (var (id, live) in _live)
            {
                if (live.LastSeenAt is not { } seen) continue;

                var due = force
                    || live.FlushRequested
                    || now - live.PersistedLastSeenAt >= minInterval;

                if (!due) continue;

                result.Add(new PendingTouch(id, seen, live.Address, live.AgentVersion));
                live.PersistedLastSeenAt = seen;
                live.FlushRequested = false;
            }
        }

        return result;
    }

    /// <summary>
    /// 与上一次广播过的状态做对比，返回<b>发生跃迁</b>的那些（用于推 AgentOnline / AgentOffline）。
    /// </summary>
    /// <param name="current">本次算出的全部主机 DTO。</param>
    /// <param name="seedOnly">
    /// true = 只播种不推送。<b>服务刚启动时必须有这一次</b>：否则 20 台机器会被当作
    /// "刚刚上线"推一遍，每个打开的页面在启动瞬间弹一堆提示。
    /// </param>
    public IReadOnlyList<StateTransition> DiffStates(
        IReadOnlyList<AgentHostDto> current,
        bool seedOnly)
    {
        var changes = new List<StateTransition>();

        lock (_sync)
        {
            if (seedOnly)
            {
                _lastBroadcastState.Clear();
                foreach (var host in current) _lastBroadcastState[host.Id] = host.State;
                return changes;
            }

            var seen = new HashSet<int>();

            foreach (var host in current)
            {
                seen.Add(host.Id);

                if (_lastBroadcastState.TryGetValue(host.Id, out var previous))
                {
                    if (!string.Equals(previous, host.State, StringComparison.Ordinal))
                    {
                        changes.Add(new StateTransition(host, previous, host.State));
                    }
                }
                else
                {
                    // 新出现的机器：无论什么状态都推一次（页面要把它加进列表）。
                    changes.Add(new StateTransition(host, null, host.State));
                }

                _lastBroadcastState[host.Id] = host.State;
            }

            // 已经被删掉的主机：清掉记录，避免 Id 复用后误判成"没变"。
            var stale = _lastBroadcastState.Keys.Where(k => !seen.Contains(k)).ToList();
            foreach (var key in stale) _lastBroadcastState.Remove(key);
        }

        return changes;
    }

    /// <summary>一次状态跃迁（<paramref name="Previous"/> 为 null 表示"这台机器是新出现的"）。</summary>
    public sealed record StateTransition(AgentHostDto Host, string? Previous, string State);

    /// <summary>
    /// 与上一次广播过的 <b>UDP 通道状态</b>比对，返回发生变化的那几台（2026-10-14）。
    ///
    /// <para>
    /// 🔥 <b>为什么需要它</b>：在线表平时只在"状态跃迁"或"每 30 秒兜底"时才推给网页。
    /// 而 UDP 通道状态正是需求里那句"服务端与客户端要能实时同步"的主角 ——
    /// 让运维盯着一个最多滞后 30 秒的显示去判断"我刚点的那条指令为什么不生效"，
    /// 等于把那个判断继续建立在过期的真相上（只是换了一个数据来源）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b><c>Udp</c> 为 null 时不参与比对</b>：那是"这台机器还没告诉我"，
    /// 不是"它变成了 null"。拿它当变化，会在中心刚重启后给每台机器各推一次空刷新。
    /// </para>
    /// <para>
    /// ⚠️ 只有 <b>listening / port / executeEnabled / stateText</b> 四样进了签名 ——
    /// 别的字段变化与这条通道无关，不该借它把页面刷起来。
    /// </para>
    /// </summary>
    /// <param name="current">本次算出的全部主机 DTO。</param>
    /// <param name="seedOnly">true = 只播种不推送（服务刚启动那一次，见 <see cref="DiffStates"/>）。</param>
    public IReadOnlyList<AgentHostDto> DiffUdpStates(
        IReadOnlyList<AgentHostDto> current,
        bool seedOnly)
    {
        var changed = new List<AgentHostDto>();

        lock (_sync)
        {
            if (seedOnly)
            {
                _lastUdpSignature.Clear();

                foreach (var host in current)
                {
                    if (Signature(host.UdpChannel) is { } signature) _lastUdpSignature[host.Id] = signature;
                }

                return changed;
            }

            var seen = new HashSet<int>();

            foreach (var host in current)
            {
                seen.Add(host.Id);

                // 没上报 ⇒ 不参与比对，也**不覆盖**已知的签名：
                // 否则一次"没告诉我"会被记成"变成未知了"，下一次真上报又成了"变化"（多推一次）。
                if (Signature(host.UdpChannel) is not { } signature) continue;

                if (!_lastUdpSignature.TryGetValue(host.Id, out var previous)
                    || !string.Equals(previous, signature, StringComparison.Ordinal))
                {
                    changed.Add(host);
                    _lastUdpSignature[host.Id] = signature;
                }
            }

            var stale = _lastUdpSignature.Keys.Where(k => !seen.Contains(k)).ToList();
            foreach (var key in stale) _lastUdpSignature.Remove(key);
        }

        return changed;

        static string? Signature(AgentUdpChannelStateDto? state)
            => state is null
                ? null
                : $"{state.Listening}|{state.Port}|{state.ExecuteEnabled}|{state.StateText}";
    }

    private LiveState GetOrAdd(int id)
    {
        if (_live.TryGetValue(id, out var live)) return live;

        live = new LiveState();
        _live[id] = live;
        return live;
    }

    /// <summary>待落盘的一次心跳。</summary>
    public sealed record PendingTouch(int Id, DateTimeOffset LastSeenAt, string? Address, string? AgentVersion);

    /// <summary>内存态（可变字段；读写都在 <c>_sync</c> 下）。</summary>
    private sealed class LiveState
    {
        public DateTimeOffset? LastSeenAt;
        public DateTimeOffset PersistedLastSeenAt = DateTimeOffset.MinValue;
        public string? Address;
        public string? AgentVersion;
        public string? ConnectionId;
        public bool FlushRequested;

        /// <summary>客户机最近一次报上来的 UDP 通道实时态（<b>不落库</b>，见类注释）。</summary>
        public AgentUdpChannelStateDto? Udp;
    }
}
