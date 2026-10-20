using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// 指令库服务：读写 <c>commands</c> 配置文档（<b>不参与配置导入导出</b>，见 <see cref="ConfigKeys.Commands"/>）。
/// <para>
/// 与 <see cref="ControlSettingsService"/> 同样采用「版本变了才重新反序列化」的进程内缓存：
/// 指令库很小，但每个动作都可能解析一次，不能每次读库。
/// </para>
/// <para>
/// <b>写路径统一走 <see cref="MutateAsync"/></b>（锁内「读最新 → 校验 → 改 → 写全量」），
/// 与配置文档共用同一套乐观锁语义。增删改各自的业务规则（重名 / 找不到）在委托里判定，
/// 通过返回 <see cref="MutationFailure"/> 表达"拒绝写入"，而不是抛异常。
/// </para>
/// <para>
/// <b>为什么不开放 <c>expectedVersion</c> 参数</b>：指令库的增删改永远是"针对最新库施加一个局部意图"，
/// 语义上天然可合并（两个人分别加不同指令不该互相冲突）。整份替换的场景由
/// <see cref="SaveAsync"/> 覆盖，那条路径保留乐观锁。
/// </para>
/// </summary>
public sealed class CommandLibraryService : ICommandLibrary
{
    private readonly IConfigDocumentStore _store;
    private readonly LogService _log;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<CommandItem>? _cache;
    private int _cacheVersion = -1;

    /// <summary>
    /// 指令库允许的数据类型（**不含「指令库」**，避免动作间接指向指令库造成递归解析）。
    /// <para>
    /// <c>UTF-8</c> 仍在此列**仅为兼容旧调用方**（提交 <c>"utf-8"</c> 不该报错），
    /// 但 <see cref="Validate"/> 会把它归并成 <c>String</c> 落库 —— 两者编码结果完全相同，
    /// 库里只保留一个取值（见 2026-10-08 的别名合并）。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> AllowedDataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        DataTypes.String,
        DataTypes.Hex,
        DataTypes.Ansi,
        DataTypes.Ascii,
        DataTypes.Utf8,
    };

    public CommandLibraryService(IConfigDocumentStore store, LogService log)
    {
        _store = store;
        _log = log;
    }

    // ═══════════════════════════════════════════════════════════
    // 读
    // ═══════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<CommandItem>> GetAllAsync(CancellationToken cancellationToken = default)
        => await LoadAsync(cancellationToken).ConfigureAwait(false);

    public async Task<CommandItem?> ResolveAsync(string? name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var items = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return FindByName(items, name.Trim(), exceptId: null);
    }

    /// <summary>取当前版本号（供端点回显；不触发写入）。</summary>
    public async Task<int> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _store.ReadAsync(ConfigKeys.Commands, cancellationToken).ConfigureAwait(false);
        return snapshot.Version;
    }

    // ═══════════════════════════════════════════════════════════
    // 写（M2-E3）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 新增指令。名称为空 / 协议或数据类型越界 → <see cref="CommandMutationStatus.Invalid"/>；
    /// 与既有指令重名 → <see cref="CommandMutationStatus.DuplicateName"/>（大小写不敏感）。
    /// </summary>
    public Task<CommandMutationResult> AddAsync(
        CommandItem item,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        return MutateAsync(
            updatedBy,
            cancellationToken,
            list =>
            {
                var error = Normalize(item);
                if (error is not null) return new MutationFailure(CommandMutationStatus.Invalid, error);

                var conflict = FindByName(list, item.Name, exceptId: null);
                if (conflict is not null)
                {
                    return new MutationFailure(
                        CommandMutationStatus.DuplicateName,
                        $"指令名「{item.Name}」已被占用，请换一个名称。",
                        conflict.Id);
                }

                // Id 撞车（前端复制粘贴了同一条）→ 换新 Id，避免"新增"变成"悄悄覆盖"。
                if (list.Exists(c => string.Equals(c.Id, item.Id, StringComparison.Ordinal)))
                {
                    item.Id = Guid.NewGuid().ToString();
                }

                list.Add(item);
                return null;
            },
            $"新增指令「{item.Name}」",
            () => item);
    }

    /// <summary>
    /// 修改指令（按 Id 定位；Id 以路由/入参为准，**不允许改名撞车**）。
    /// 目标不存在 → <see cref="CommandMutationStatus.NotFound"/>。
    /// </summary>
    public Task<CommandMutationResult> UpdateAsync(
        string id,
        CommandItem item,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(item);

        return MutateAsync(
            updatedBy,
            cancellationToken,
            list =>
            {
                var index = list.FindIndex(c => string.Equals(c.Id, id, StringComparison.Ordinal));
                if (index < 0)
                {
                    return new MutationFailure(CommandMutationStatus.NotFound, $"指令 {id} 不存在（可能已被其他人删除）。");
                }

                var error = Normalize(item);
                if (error is not null) return new MutationFailure(CommandMutationStatus.Invalid, error);

                var conflict = FindByName(list, item.Name, exceptId: id);
                if (conflict is not null)
                {
                    return new MutationFailure(
                        CommandMutationStatus.DuplicateName,
                        $"指令名「{item.Name}」已被占用，请换一个名称。",
                        conflict.Id);
                }

                // Id 恒定取路由值：即使前端把 body 里的 Id 写错也不会"改一条、留一条"。
                item.Id = id;
                list[index] = item;
                return null;
            },
            $"修改指令 {id}",
            () => item);
    }

    /// <summary>删除指令。目标不存在 → <see cref="CommandMutationStatus.NotFound"/>。</summary>
    public Task<CommandMutationResult> DeleteAsync(
        string id,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return MutateAsync(
            updatedBy,
            cancellationToken,
            list =>
            {
                var removed = list.RemoveAll(c => string.Equals(c.Id, id, StringComparison.Ordinal));
                return removed == 0
                    ? new MutationFailure(CommandMutationStatus.NotFound, $"指令 {id} 不存在（可能已被其他人删除）。")
                    : null;
            },
            $"删除指令 {id}");
    }

    /// <summary>
    /// 整体保存指令库。用于批量替换 / 导入场景。
    /// <para>
    /// 保留 <c>expectedVersion</c>：这条路径是**整份意图**，必须能被乐观锁挡住。
    /// 逐条增删改请用 <see cref="AddAsync"/> / <see cref="UpdateAsync"/> / <see cref="DeleteAsync"/>。
    /// </para>
    /// </summary>
    public async Task<int> SaveAsync(
        IEnumerable<CommandItem> items,
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = items.Where(i => i is not null).ToList();
            var json = ControlJson.Serialize(list);

            var version = await _store
                .WriteAsync(ConfigKeys.Commands, json, expectedVersion, updatedBy, cancellationToken)
                .ConfigureAwait(false);

            _cache = list;
            _cacheVersion = version;

            _log.Info("CommandLibrary", $"指令库已保存（{list.Count} 条，版本 {version}，by {updatedBy ?? "system"}）");
            return version;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    /// <summary>业务规则未通过的描述。返回它 = 拒绝写入。</summary>
    private sealed record MutationFailure(CommandMutationStatus Status, string Message, string? ConflictId = null);

    /// <summary>
    /// 「锁内读最新 → 校验 → 改 → 写全量」。校验未通过时**不写库**，但把当前库原样带回。
    /// </summary>
    private async Task<CommandMutationResult> MutateAsync(
        string? updatedBy,
        CancellationToken cancellationToken,
        Func<List<CommandItem>, MutationFailure?> mutate,
        string operationName,
        Func<CommandItem?>? affectedSelector = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await _store.ReadAsync(ConfigKeys.Commands, cancellationToken).ConfigureAwait(false);
            var (list, corrupt) = ParseDocument(snapshot.Value, snapshot.Version);

            // 损坏时一律拒写：照常写下去 = 把用户还没抢救的原始内容永久覆盖。
            if (corrupt)
            {
                const string why = "指令库存储内容已损坏且无法解析，为避免覆盖原始内容，本次修改被拒绝。"
                                   + "请先排查 commands 文档（服务端日志有解析异常详情）。";

                _log.Warn("CommandLibrary", $"{operationName}：已拒绝 —— {why}");
                return CommandMutationResult.Failure(CommandMutationStatus.StorageCorrupt, snapshot.Version, list, why);
            }

            var failure = mutate(list);
            if (failure is not null)
            {
                _log.Warn("CommandLibrary", $"{operationName}：已拒绝 —— {failure.Message}");

                // 把最新的库缓存下来：这次请求虽然被拒，但读到的确实是当前真相。
                _cache = list;
                _cacheVersion = snapshot.Version;

                return CommandMutationResult.Failure(
                    failure.Status,
                    snapshot.Version,
                    list,
                    failure.Message,
                    failure.ConflictId);
            }

            var json = ControlJson.Serialize(list);
            var version = await _store
                .WriteAsync(ConfigKeys.Commands, json, expectedVersion: null, updatedBy, cancellationToken)
                .ConfigureAwait(false);

            _cache = list;
            _cacheVersion = version;

            _log.Info(
                "CommandLibrary",
                $"{operationName}：已保存（共 {list.Count} 条，版本 {version}，by {updatedBy ?? "system"}）");

            // 受影响的指令：新增 / 修改由调用方给；删除没有"幸存的受影响项"，回退到列表首条（可能为 null）。
            var affected = affectedSelector is not null ? affectedSelector() : list.FirstOrDefault();
            return CommandMutationResult.Success(version, list, affected, $"{operationName}：成功。");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>归一化 + 校验。返回错误消息；<c>null</c> 表示通过（并就地把字段归一）。</summary>
    private static string? Normalize(CommandItem item)
    {
        item.Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString() : item.Id.Trim();
        item.Name = (item.Name ?? string.Empty).Trim();
        item.Data = item.Data ?? string.Empty;

        if (item.Name.Length == 0)
        {
            return "指令名称不能为空。";
        }

        // 协议：留空回退默认（UDP），填了就必须认识 —— 不静默改写用户明确填错的值。
        if (string.IsNullOrWhiteSpace(item.Protocol))
        {
            item.Protocol = TransportProtocols.Udp;
        }
        else if (string.Equals(item.Protocol.Trim(), TransportProtocols.Udp, StringComparison.OrdinalIgnoreCase))
        {
            item.Protocol = TransportProtocols.Udp;
        }
        else if (string.Equals(item.Protocol.Trim(), TransportProtocols.Tcp, StringComparison.OrdinalIgnoreCase))
        {
            item.Protocol = TransportProtocols.Tcp;
        }
        else
        {
            return $"协议「{item.Protocol}」不受支持 —— 指令库只支持 UDP / TCP（不含 WoL）。";
        }

        // 数据类型：同上。特别注意「指令库」必须被挡在门外，否则 A 指令引用 B 指令会套娃。
        if (string.IsNullOrWhiteSpace(item.DataType))
        {
            item.DataType = DataTypes.String;
        }
        else
        {
            var match = AllowedDataTypes.FirstOrDefault(t => string.Equals(t, item.DataType.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return string.Equals(item.DataType.Trim(), DataTypes.CommandLibrary, StringComparison.Ordinal)
                    ? "指令库里的指令不能再把数据类型设为「指令库」—— 那会让解析无限套娃。"
                    : $"数据类型「{item.DataType}」不受支持 —— 指令库只支持 String / Hex / ANSI / ASCII。";
            }

            // 别名合并：UTF-8 与 String 编码结果完全相同，统一落成 String（与动作侧清洗器同一口径）。
            item.DataType = string.Equals(match, DataTypes.Utf8, StringComparison.OrdinalIgnoreCase)
                ? DataTypes.String
                : match;
        }

        return null;
    }

    /// <summary>按名称查找（大小写不敏感）；<paramref name="exceptId"/> 用于"改自己"时排除自身。</summary>
    private static CommandItem? FindByName(
        IReadOnlyList<CommandItem> items,
        string name,
        string? exceptId)
    {
        foreach (var item in items)
        {
            if (item is null) continue;
            if (exceptId is not null && string.Equals(item.Id, exceptId, StringComparison.Ordinal)) continue;
            if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) return item;
        }

        return null;
    }

    private async Task<IReadOnlyList<CommandItem>> LoadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _store.ReadAsync(ConfigKeys.Commands, cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is not null && _cacheVersion == snapshot.Version)
            {
                return _cache;
            }

            _cache = ParseDocument(snapshot.Value, snapshot.Version).Items;
            _cacheVersion = snapshot.Version;
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 解析指令库 JSON。
    /// <para>
    /// 解析失败时**不抛**：退化为空库并记 Error，动作侧会以"未找到指令"失败 ——
    /// 总好过整个发送链路崩掉。写路径靠 <see cref="IsCorrupt"/> 挡住，避免把损坏内容覆盖掉。
    /// </para>
    /// </summary>
    private (List<CommandItem> Items, bool Corrupt) ParseDocument(string? value, int version)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            // 文档缺失 / 空文本 = 真的空库，不是损坏。
            return (new List<CommandItem>(), false);
        }

        try
        {
            var list = ControlJson.DeserializeLenient<List<CommandItem>>(value!) ?? new List<CommandItem>();
            list.RemoveAll(c => c is null);
            return (list, false);
        }
        catch (Exception ex)
        {
            _log.Error("CommandLibrary", $"指令库 JSON 解析失败（版本 {version}），本次按空库处理，未覆盖存储内容。", ex);
            return (new List<CommandItem>(), true);
        }
    }
}
