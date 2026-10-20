using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;

namespace ControlServer.Core.Tests;

/// <summary>
/// 共享的 <see cref="INotifier"/> 替身：不推送任何东西，但**记录**收到的载荷。
/// <para>
/// 原先 <c>ActionExecutionServiceTests</c> 与 <c>PairingDispatchServiceTests</c> 里各有一份
/// （后者甚至是纯空实现）。M2-F 给 <see cref="INotifier"/> 加了两个成员后，"改一处漏一处"立刻暴露，
/// 因此合并为唯一一份：**新增接口成员时只需改这里一处**。
/// </para>
/// </summary>
internal sealed class NullNotifier : INotifier
{
    /// <summary>收到的动作执行结果（按顺序）。</summary>
    public List<ActionResultDto> Results { get; } = new();

    /// <summary>收到的收发日志（按顺序）。</summary>
    public List<TrafficLogDto> TrafficLogs { get; } = new();

    /// <summary>收到的配置变更通知。</summary>
    public List<ConfigChangedDto> ConfigChanges { get; } = new();

    /// <summary>收到的授权状态变更通知。</summary>
    public List<LicenseDto> LicenseChanges { get; } = new();

    public Task LogAddedAsync(LogDto item, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task StatusChangedAsync(StatusSnapshot snapshot, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task ActionResultAsync(ActionResultDto result, CancellationToken cancellationToken = default)
    {
        Results.Add(result);
        return Task.CompletedTask;
    }

    public Task TrafficLogAddedAsync(TrafficLogDto item, CancellationToken cancellationToken = default)
    {
        TrafficLogs.Add(item);
        return Task.CompletedTask;
    }

    public Task ConfigChangedAsync(ConfigChangedDto change, CancellationToken cancellationToken = default)
    {
        ConfigChanges.Add(change);
        return Task.CompletedTask;
    }

    public Task LicenseChangedAsync(LicenseDto license, CancellationToken cancellationToken = default)
    {
        LicenseChanges.Add(license);
        return Task.CompletedTask;
    }

    // ── M4.1：客户端在线表的三条通知。本替身只记录「有没有收到」，不关心载荷 ──

    /// <summary>收到的客户端主机变化通知（M4.1）。</summary>
    public List<AgentHostDto> AgentChanges { get; } = new();

    /// <summary>收到的下线通知（M4.1）。</summary>
    public List<AgentHostDto> AgentOfflines { get; } = new();

    public Task AgentChangedAsync(AgentHostDto host, CancellationToken cancellationToken = default)
    {
        AgentChanges.Add(host);
        return Task.CompletedTask;
    }

    public Task AgentOnlineAsync(AgentHostDto host, CancellationToken cancellationToken = default)
    {
        AgentChanges.Add(host);
        return Task.CompletedTask;
    }

    public Task AgentOfflineAsync(AgentHostDto host, CancellationToken cancellationToken = default)
    {
        AgentChanges.Add(host);
        AgentOfflines.Add(host);
        return Task.CompletedTask;
    }

    public Task AgentTelemetryUpdatedAsync(AgentLiveMetricsDto live, CancellationToken cancellationToken = default)
    {
        // M4.2：遥测推送是高频事件，替身只登记不参与业务断言（要看它得单独造一个记录器）。
        return Task.CompletedTask;
    }

    /// <summary>收到的文件传输进度（M4.4，按顺序）。</summary>
    public List<TransferProgressDto> Transfers { get; } = new();

    public Task TransferProgressAsync(TransferProgressDto progress, CancellationToken cancellationToken = default)
    {
        Transfers.Add(progress);
        return Task.CompletedTask;
    }

    /// <summary>收到过配置变更通知的客户机 Id（M4.5，按顺序）。</summary>
    public List<int> ConfigChangedAgents { get; } = new();

    public Task AgentConfigChangedAsync(int agentId, CancellationToken cancellationToken = default)
    {
        ConfigChangedAgents.Add(agentId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 真·临时目录版 <see cref="IDataDirectory"/>：每个实例一个独立目录，<see cref="Dispose"/> 时整棵删除。
/// 需要真的读写文件（配置文档、图片落盘）的测试一律用它，不要 mock 文件系统。
/// </summary>
internal sealed class TempDataDirectory : IDataDirectory, IDisposable
{
    public TempDataDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "cs-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string DatabaseFile => Path.Combine(Root, "control.db");

    public string ConfigDirectory => Path.Combine(Root, "config");

    public string LogDirectory => Path.Combine(Root, "logs");

    public string FilesDirectory => Path.Combine(Root, "files");

    public string ImagesDirectory => Path.Combine(Root, "images");

    public string CertificateDirectory => Path.Combine(Root, "https");

    public string LicenseFile => Path.Combine(Root, "license.lic");

    public string TimeStampFile => Path.Combine(Root, "timestamp.dat");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ImagesDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // 测试清理失败可忽略（文件仍被占用等）。
        }
    }
}

/// <summary>
/// 路径齐全但**不建目录**的 <see cref="IDataDirectory"/>：给只用到路径字符串、
/// 不需要真落盘的测试用（如构造 <c>LogService</c> 只为拿它的环形缓冲）。
/// <para>
/// ⚠️ 「不建目录」只对 <see cref="EnsureCreated"/> 成立 —— <c>LogService.FlushAsync</c> 会自己
/// <c>Directory.CreateDirectory(LogDirectory)</c> 再把 ndjson 落盘。所以这里如果是
/// **每实例一个 guid 目录**，就等于**每跑一次测试都在 %TEMP% 里多撒几个顶层目录**
/// （2026-10-08 才发现已经积到 248 个，每次运行 +7~8 个）。
/// </para>
/// <para>
/// 现在改成：所有实例共用**唯一一个**固定根 <c>cs-noop</c>。无论跑多少次、建多少实例，
/// %TEMP% 下最多只存在这一棵树；静态构造还会在每次运行开工时先把上次的残留清掉。
/// </para>
/// <para>
/// 共用目录导致多个测试写同一个 <c>log-YYYY-MM-DD.ndjson</c> —— 无所谓：
/// <c>LogService</c> 对落盘失败一律静默吞掉（"日志落盘失败不得影响业务"），
/// 而这些测试只消费它的环形缓冲。
/// </para>
/// </summary>
internal sealed class NoopDataDirectory : IDataDirectory
{
    /// <summary>本进程所有 Noop 实例共用的唯一根目录。</summary>
    private static readonly string SharedRoot = Path.Combine(Path.GetTempPath(), "cs-noop");

    static NoopDataDirectory()
    {
        // 上一次运行的残留（vstest 结束时不走 ProcessExit，清不掉）：这次一开工就先清。
        // 效果：无论历史积了多少，%TEMP% 里始终最多只有一棵 cs-noop 树。
        TryDeleteSharedRoot();

        // 正常退出时收尾。用 ProcessExit 而不是 IDisposable：调用方（各测试的辅助工厂）
        // 只在构造 LogService 时临时 new 一个，没人持有引用、更没人 Dispose。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteSharedRoot();
    }

    private static void TryDeleteSharedRoot()
    {
        // 重试几轮：ProcessExit 时 LogService 的后台 flush 线程可能还握着日志文件，
        // 头一次删除会抛 IOException，稍等即可成功。
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (!Directory.Exists(SharedRoot)) return;
                Directory.Delete(SharedRoot, recursive: true);
                return;
            }
            catch
            {
                // 测试清理失败可忽略（文件仍被占用等）—— 会在下次运行开工时再试。
                Thread.Sleep(50);
            }
        }
    }

    public string Root => SharedRoot;

    public string DatabaseFile => Path.Combine(SharedRoot, "control.db");

    public string ConfigDirectory => Path.Combine(SharedRoot, "config");

    public string LogDirectory => Path.Combine(SharedRoot, "logs");

    public string FilesDirectory => Path.Combine(SharedRoot, "files");

    public string ImagesDirectory => Path.Combine(SharedRoot, "images");

    public string CertificateDirectory => Path.Combine(SharedRoot, "https");

    public string LicenseFile => Path.Combine(SharedRoot, "license.dat");

    public string TimeStampFile => Path.Combine(SharedRoot, "timestamp.dat");

    public void EnsureCreated()
    {
        // 刻意什么都不做。
    }
}

/// <summary>
/// 内存版配置文档存储，<b>语义与 <c>EfConfigDocumentStore</c> 一致</b>（含乐观锁冲突抛
/// <see cref="ConfigConcurrencyException"/>）。
/// <para>
/// 三个测试文件曾各写一份，属性还不一样（一个要 <c>Documents</c>、一个要 <c>NextVersion</c>）。
/// 合并为唯一一份：新增需要时只改这里。
/// </para>
/// </summary>
internal sealed class InMemoryConfigStore : IConfigDocumentStore
{
    private readonly Dictionary<string, (string Value, int Version)> _documents = new(StringComparer.Ordinal);

    /// <summary>已写入的文档内容（键 → 原文）。</summary>
    public IReadOnlyDictionary<string, string> Documents
        => _documents.ToDictionary(p => p.Key, p => p.Value.Value);

    /// <summary>下一个可用版本号（供 <see cref="Seed"/> 显式播种时使用）。</summary>
    public int NextVersion => _documents.TryGetValue(ConfigKeys.Settings, out var d) ? d.Version + 1 : 1;

    /// <summary>绕过版本校验直接播种一份文档（模拟"配置早已存在"）。</summary>
    public void Seed(string key, string value, int version) => _documents[key] = (value, version);

    public Task<ConfigDocumentSnapshot> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_documents.TryGetValue(key, out var doc))
        {
            return Task.FromResult(ConfigDocumentSnapshot.Missing(key));
        }

        return Task.FromResult(new ConfigDocumentSnapshot(key, doc.Value, doc.Version, BeijingTime.Now, "test"));
    }

    public Task<int> WriteAsync(
        string key,
        string value,
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        var actual = _documents.TryGetValue(key, out var doc) ? doc.Version : 0;

        if (expectedVersion is not null && expectedVersion.Value != actual)
        {
            throw new ConfigConcurrencyException(key, expectedVersion.Value, actual);
        }

        var next = actual + 1;
        _documents[key] = (value, next);
        return Task.FromResult(next);
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(_documents.Keys.ToList());

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _documents.Remove(key);
        return Task.CompletedTask;
    }
}

/// <summary>记录型收发日志替身：只存不推。</summary>
internal sealed class RecordingTrafficLog : ITrafficLog
{
    private readonly List<TrafficLogEntry> _entries = new();

    public IReadOnlyList<TrafficLogEntry> Entries => _entries;

    public int Count => _entries.Count;

    public void Write(TrafficLogEntry entry) => _entries.Add(entry);

    public IReadOnlyList<TrafficLogEntry> Recent(int count = 200) => _entries.TakeLast(count).ToList();
}

/// <summary>永不命中的指令库替身。</summary>
internal sealed class FakeCommandLibrary : ICommandLibrary
{
    public Task<IReadOnlyList<CommandItem>> GetAllAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CommandItem>>(Array.Empty<CommandItem>());

    public Task<CommandItem?> ResolveAsync(string? name, CancellationToken cancellationToken = default)
        => Task.FromResult<CommandItem?>(null);
}

/// <summary>
/// 可推进的假时钟。
/// <para>
/// 可直接用<b>北京时间</b>构造（偏移量写成 +08:00）—— 调度器内部会对它做
/// <c>ToBeijing</c>，而"已经是 +08:00 的同一时刻"再转一次结果不变，
/// 因此测试里写 <c>new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.FromHours(8))</c>
/// 就是"7 月 21 日 08:00:00（北京）"。
/// </para>
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public TestTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void SetUtcNow(DateTimeOffset value) => _utcNow = value;

    public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
}

/// <summary>
/// 内存版 <see cref="ITimerFireStore"/>：用字典模拟<b>内存版数据库的唯一约束</b>。
/// <para>与 <c>EfTimerFireStore</c> 的契约一致：同一 <c>(RefId, FireDate, FireTime)</c> 只有第一次返回 true。</para>
/// </summary>
internal sealed class FakeTimerFireStore : ITimerFireStore
{
    private readonly Dictionary<string, TimerFireEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>累计调用 <see cref="TryClaimAsync"/> 的次数（不管成没成）。</summary>
    public int ClaimAttempts { get; private set; }

    /// <summary>累计调用 <see cref="PruneAsync"/> 的次数。</summary>
    public int PruneCalls { get; private set; }

    /// <summary>已占用的触发点。</summary>
    public IReadOnlyCollection<TimerFireEntry> Entries => _entries.Values;

    public Task<bool> TryClaimAsync(TimerFireEntry entry, CancellationToken cancellationToken = default)
    {
        ClaimAttempts++;

        var key = $"{entry.RefId}|{entry.FireDate}|{entry.FireTime}";

        if (_entries.ContainsKey(key))
        {
            return Task.FromResult(false);
        }

        _entries[key] = entry;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<TimerFireEntry>> ListAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var text = date.ToString("yyyy-MM-dd");

        return Task.FromResult<IReadOnlyList<TimerFireEntry>>(
            _entries.Values.Where(e => string.Equals(e.FireDate, text, StringComparison.Ordinal)).ToList());
    }

    public Task<int> PruneAsync(DateOnly keepFrom, CancellationToken cancellationToken = default)
    {
        PruneCalls++;

        var cutoff = keepFrom.ToString("yyyy-MM-dd");
        var doomed = _entries
            .Where(kv => string.CompareOrdinal(kv.Value.FireDate, cutoff) < 0)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in doomed)
        {
            _entries.Remove(key);
        }

        return Task.FromResult(doomed.Count);
    }
}
