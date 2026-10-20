using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>配置加载结果状态。</summary>
public enum ConfigLoadState
{
    /// <summary>正常读取（含首次自动初始化）。</summary>
    Ok,

    /// <summary>文档尚不存在 —— 已按默认值初始化。</summary>
    Missing,

    /// <summary>存储中的 JSON 损坏 —— 已回退默认值，**未覆盖**存储内容（等人工处置）。</summary>
    Corrupt,
}

/// <summary>
/// 一次配置读取的结果：配置本身 + 版本 + 状态。
/// </summary>
/// <param name="Settings">已清洗归一化的配置。</param>
/// <param name="Version">乐观锁版本号（写入时需回传）。</param>
/// <param name="State">加载状态。</param>
/// <param name="Notes">本次清洗实际做了哪些迁移 / 校正（用于告知用户）。</param>
public sealed record ControlSettingsView(
    ControlSettings Settings,
    int Version,
    ConfigLoadState State,
    IReadOnlyList<string> Notes);

/// <summary>配置变更事件载荷。</summary>
/// <param name="Key">文档键。</param>
/// <param name="Version">变更后的新版本。</param>
/// <param name="ChangedBy">变更者。</param>
/// <param name="At">变更时间（北京时间）。</param>
public sealed record ConfigChangedEvent(string Key, int Version, string ChangedBy, DateTimeOffset At);

/// <summary>
/// 全局配置服务：配置读写 + 兼容清洗 + 排序归一化 + 版本控制。
/// <para>
/// <b>并发模型</b>（对齐 M2 方案 §4.3）：
/// <list type="number">
///   <item>内部 <see cref="SemaphoreSlim"/> 保证「读-改-写」原子；</item>
///   <item>版本号做乐观锁，冲突由 <see cref="IConfigDocumentStore"/> 抛 <see cref="ConfigConcurrencyException"/>；</item>
///   <item>写入成功后触发 <see cref="Changed"/>，由宿主桥接到 SignalR 广播。</item>
/// </list>
/// </para>
/// <para>
/// 进程内缓存：只走"版本变了才重新反序列化"，避免每次请求都解 JSON。
/// </para>
/// </summary>
public sealed partial class ControlSettingsService
{
    private readonly IConfigDocumentStore _store;
    private readonly LogService _log;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ControlSettings? _cachedSettings;
    private int _cachedVersion = -1;
    private ConfigLoadState _cachedState = ConfigLoadState.Missing;
    private IReadOnlyList<string> _cachedNotes = Array.Empty<string>();

    public ControlSettingsService(IConfigDocumentStore store, LogService log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>配置发生变更（写入成功后触发）。</summary>
    public event Action<ConfigChangedEvent>? Changed;

    // ═══════════════════════════════════════════════════════════
    // 读
    // ═══════════════════════════════════════════════════════════

    /// <summary>读取当前配置（带缓存，版本变化时自动重载）。</summary>
    public async Task<ControlSettingsView> GetAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _store.ReadAsync(ConfigKeys.Settings, cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 缓存命中：版本一致，直接返回同一实例。
            if (_cachedSettings is not null && _cachedVersion == snapshot.Version)
            {
                return new ControlSettingsView(_cachedSettings, _cachedVersion, _cachedState, _cachedNotes);
            }

            return LoadFromSnapshot(snapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 只读缓存快照（**不访问存储**）。仅用于已确保加载过之后的同步读取场景
    /// （如定时器每秒轮询）。未加载过时返回 null。
    /// </summary>
    public ControlSettings? CachedSettings => Volatile.Read(ref _cachedSettings);

    /// <summary>当前缓存版本；未加载时为 -1。</summary>
    public int CachedVersion => Volatile.Read(ref _cachedVersion);

    private ControlSettingsView LoadFromSnapshot(ConfigDocumentSnapshot snapshot)
    {
        if (!snapshot.Exists || string.IsNullOrWhiteSpace(snapshot.Value))
        {
            // 首次运行：按默认值初始化。此处**不落盘** —— 让 version 保持 0，
            // 前端首次 PUT 时携带 expectedVersion=0 即可正常写入（存储侧 0==0 通过），
            // 避免"读操作产生写"这种不必要副作用。
            var fresh = new ControlSettings();
            var notes = ControlSettingsNormalizer.Normalize(fresh);
            _log.Info("Config", "未发现配置文档，已按默认值初始化（尚未落盘，首次保存后版本变为 1）");

            _cachedSettings = fresh;
            _cachedVersion = 0;
            _cachedState = ConfigLoadState.Missing;
            _cachedNotes = notes;
            return new ControlSettingsView(fresh, 0, ConfigLoadState.Missing, notes);
        }

        try
        {
            var parsed = ControlJson.DeserializeLenient<ControlSettings>(snapshot.Value!) ?? new ControlSettings();
            var notes = ControlSettingsNormalizer.Normalize(parsed);

            _cachedSettings = parsed;
            _cachedVersion = snapshot.Version;
            _cachedState = ConfigLoadState.Ok;
            _cachedNotes = notes;
            return new ControlSettingsView(parsed, snapshot.Version, ConfigLoadState.Ok, notes);
        }
        catch (Exception ex)
        {
            // 配置损坏：**绝不回写**，否则会把用户数据彻底抹掉。
            // 返回默认值让服务能起来，同时把问题显式暴露在日志与状态里。
            _log.Error("Config", $"配置文档 JSON 解析失败（版本 {snapshot.Version}），已回退默认值，未覆盖存储内容。请人工检查 config_documents 表。", ex);

            var fallback = new ControlSettings();
            var notes = ControlSettingsNormalizer.Normalize(fallback);

            _cachedSettings = fallback;
            _cachedVersion = snapshot.Version;
            _cachedState = ConfigLoadState.Corrupt;
            _cachedNotes = notes;
            return new ControlSettingsView(fallback, snapshot.Version, ConfigLoadState.Corrupt, notes);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 写
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 整体覆盖保存（导入 / 编辑页保存走这里）。
    /// </summary>
    /// <param name="settings">待保存配置（内部会深拷贝后清洗，调用方的实例不被改动）。</param>
    /// <param name="expectedVersion">期望版本；<c>null</c> = 不做乐观锁校验。</param>
    /// <param name="updatedBy">写入者。</param>
    /// <returns>写入后的新版本号。</returns>
    public async Task<int> SaveAsync(
        ControlSettings settings,
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var working = ConfigCloner.CloneSettings(settings);
            ControlSettingsNormalizer.Normalize(working);

            var json = ControlJson.Serialize(working);
            var newVersion = await _store
                .WriteAsync(ConfigKeys.Settings, json, expectedVersion, updatedBy, cancellationToken)
                .ConfigureAwait(false);

            _cachedSettings = working;
            _cachedVersion = newVersion;
            _cachedState = ConfigLoadState.Ok;
            _cachedNotes = Array.Empty<string>();

            _log.Info("Config", $"配置已保存（版本 {newVersion}，按钮 {working.Buttons.Count}，子按钮集 {working.SubButtonSets.Count}，by {updatedBy ?? "system"}）");
            RaiseChanged(newVersion, updatedBy);
            return newVersion;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 原子「读-改-写」：在锁内先确保拿到最新配置，再交给 <paramref name="mutate"/> 修改，最后整体落盘。
    /// 所有细粒度操作（增删按钮 / 排序 / 清空）都走这个原语，避免绕过乐观锁。
    /// </summary>
    /// <param name="expectedVersion">期望版本；<c>null</c> = 不做乐观锁校验（内部调用）。</param>
    /// <param name="updatedBy">写入者。</param>
    /// <param name="mutate">对配置的修改动作（就地修改）。</param>
    /// <param name="operationName">操作名，仅用于日志。</param>
    public async Task<ControlSettingsView> MutateAsync(
        int? expectedVersion,
        string? updatedBy,
        Action<ControlSettings> mutate,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 锁内重新读存储，确保基于最新版本改。
            var snapshot = await _store.ReadAsync(ConfigKeys.Settings, cancellationToken).ConfigureAwait(false);

            ControlSettings working;
            var notes = new List<string>();

            if (!snapshot.Exists || string.IsNullOrWhiteSpace(snapshot.Value))
            {
                working = new ControlSettings();
            }
            else
            {
                working = ControlJson.DeserializeLenient<ControlSettings>(snapshot.Value!) ?? new ControlSettings();
            }

            notes.AddRange(ControlSettingsNormalizer.Normalize(working));

            mutate(working);

            // 修改后再跑一次归一化：把本次修改可能打乱的 Order 重新编号 0..N-1。
            // （旧格式迁移在第一次已处理完，此处不会重复产生迁移说明。）
            ControlSettingsNormalizer.SortAndReindex(working, notes);

            var json = ControlJson.Serialize(working);
            var newVersion = await _store
                .WriteAsync(ConfigKeys.Settings, json, expectedVersion, updatedBy, cancellationToken)
                .ConfigureAwait(false);

            _cachedSettings = working;
            _cachedVersion = newVersion;
            _cachedState = ConfigLoadState.Ok;
            _cachedNotes = notes;

            _log.Info("Config", $"{operationName}：已保存（版本 {newVersion}，by {updatedBy ?? "system"}）");
            RaiseChanged(newVersion, updatedBy);

            return new ControlSettingsView(working, newVersion, ConfigLoadState.Ok, notes);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RaiseChanged(int version, string? updatedBy)
    {
        try
        {
            Changed?.Invoke(new ConfigChangedEvent(ConfigKeys.Settings, version, updatedBy ?? "system", BeijingTime.Now));
        }
        catch (Exception ex)
        {
            // 事件订阅者（SignalR 广播）异常不得影响配置写入结果。
            _log.Warn("Config", $"配置变更事件处理异常：{ex.Message}");
        }
    }
}
