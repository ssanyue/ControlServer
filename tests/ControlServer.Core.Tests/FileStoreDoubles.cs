using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;

namespace ControlServer.Core.Tests;

/// <summary>
/// 内存版 <see cref="IFileRepositoryStore"/>，语义对齐 <c>EfFileRepositoryStore</c>。
/// <para>
/// <b>为什么不用 EF InMemory</b>：本项目的存储端口是手写的窄接口，目的就是让核心逻辑
/// 能在一个不依赖 EF、不依赖 SQLite 的替身上被测。用 InMemory provider 反而会把
/// "EF 的表达能力"（LINQ 翻译、跟踪行为）拖进来 —— 那是集成测试的活。
/// </para>
/// </summary>
internal sealed class FakeFileRepositoryStore : IFileRepositoryStore
{
    private readonly List<FileRepoItem> _items = new();
    private readonly Dictionary<string, FileUploadSession> _sessions = new(StringComparer.Ordinal);

    private int _nextId = 1;

    /// <summary>置 true 让 <see cref="AddAsync"/> 抛异常 —— 模拟"文件已落位但落库失败"。</summary>
    public bool FailAdd { get; set; }

    /// <summary>已入库的条目。</summary>
    public IReadOnlyList<FileRepoItem> Items => _items;

    /// <summary>仍在的会话。</summary>
    public IReadOnlyCollection<FileUploadSession> Sessions => _sessions.Values;

    public Task<IReadOnlyList<FileRepoItem>> ListAsync(string? kind, CancellationToken cancellationToken = default)
    {
        IEnumerable<FileRepoItem> rows = _items;

        if (kind is not null) rows = rows.Where(i => string.Equals(i.Kind, kind, StringComparison.Ordinal));

        return Task.FromResult<IReadOnlyList<FileRepoItem>>(
            rows.OrderByDescending(i => i.UploadedAt).ThenByDescending(i => i.Id).ToList());
    }

    public Task<FileRepoItem?> FindAsync(int id, CancellationToken cancellationToken = default)
        => Task.FromResult(_items.FirstOrDefault(i => i.Id == id));

    public Task<FileRepoItem> AddAsync(FileRepoItem item, CancellationToken cancellationToken = default)
    {
        if (FailAdd) throw new InvalidOperationException("模拟落库失败");

        var saved = item with { Id = _nextId++ };
        _items.Add(saved);

        return Task.FromResult(saved);
    }

    public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
        => Task.FromResult(_items.RemoveAll(i => i.Id == id) > 0);

    public Task CreateSessionAsync(FileUploadSession session, CancellationToken cancellationToken = default)
    {
        _sessions[session.Id] = session;
        return Task.CompletedTask;
    }

    public Task<FileUploadSession?> FindSessionAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(_sessions.TryGetValue(id, out var session) ? session : null);

    public Task<IReadOnlyList<FileUploadSession>> ListZombieSessionsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FileUploadSession>>(
            _sessions.Values
                .Where(s => s.ExpiresAt < now || !string.Equals(s.State, FileUploadStates.Open, StringComparison.Ordinal))
                .ToList());

    public Task<IReadOnlyList<FileUploadSession>> ListSessionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FileUploadSession>>(_sessions.Values.ToList());

    public Task<bool> DeleteSessionAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(_sessions.Remove(id));
}

/// <summary>
/// 内存版 <see cref="IDistributionStore"/>，语义对齐 <c>EfDistributionStore</c>
/// （含"建任务时一次性建满明细"、"下发明细的 TotalBytes = 文件大小、回传为 0"）。
/// </summary>
internal sealed class FakeDistributionStore : IDistributionStore
{
    private readonly List<DistributionTask> _tasks = new();
    private readonly List<DistributionItem> _items = new();

    private int _nextTaskId = 1;
    private int _nextItemId = 1;

    /// <summary>已建的任务。</summary>
    public IReadOnlyList<DistributionTask> Tasks => _tasks;

    /// <summary>全部明细。</summary>
    public IReadOnlyList<DistributionItem> Items => _items;

    /// <summary><see cref="UpdateItemAsync"/> 被调用的次数。</summary>
    public int UpdateItemCalls { get; private set; }

    /// <summary><see cref="UpdateTaskStateAsync"/> 被调用的次数。</summary>
    public int UpdateTaskStateCalls { get; private set; }

    public Task<DistributionTask> CreateTaskAsync(
        DistributionTask task,
        IReadOnlyList<DistributionTarget> targets,
        CancellationToken cancellationToken = default)
    {
        var saved = task with { Id = _nextTaskId++ };
        _tasks.Add(saved);

        foreach (var target in targets)
        {
            _items.Add(new DistributionItem(
                Id: _nextItemId++,
                TaskId: saved.Id,
                AgentId: target.AgentId,
                State: DistributionItemStates.Pending,
                ReceivedBytes: 0,
                // 与 EF 存储一致：下发的总大小已知（文件大小），回传要等客户端量出来。
                TotalBytes: string.Equals(saved.Direction, DistributionDirections.Push, StringComparison.Ordinal)
                    ? saved.FileSize
                    : 0,
                Error: null,
                Sha256: null,
                StartedAt: null,
                CompletedAt: null,
                // 与 EF 存储一致：逐台写各自的目录，不回填任务级的（那会让"每台一个目录"退化）。
                TargetDir: target.TargetDir));
        }

        return Task.FromResult(saved);
    }

    public Task<DistributionTask?> FindTaskAsync(int id, CancellationToken cancellationToken = default)
        => Task.FromResult(_tasks.FirstOrDefault(t => t.Id == id));

    public Task<IReadOnlyList<DistributionTask>> ListTasksAsync(int limit, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DistributionTask>>(
            _tasks.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Take(Math.Max(1, limit)).ToList());

    public Task<IReadOnlyList<DistributionItem>> ListItemsAsync(int taskId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DistributionItem>>(
            _items.Where(i => i.TaskId == taskId).OrderBy(i => i.Id).ToList());

    public Task<DistributionItem?> FindItemAsync(int itemId, CancellationToken cancellationToken = default)
        => Task.FromResult(_items.FirstOrDefault(i => i.Id == itemId));

    public Task<IReadOnlyList<DistributionItem>> ListOpenItemsAsync(int agentId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DistributionItem>>(
            _items
                .Where(i => i.AgentId == agentId
                            && (i.State == DistributionItemStates.Pending || i.State == DistributionItemStates.Running))
                .OrderBy(i => i.Id)
                .ToList());

    public Task<IReadOnlyList<DistributionItem>> ListItemsForTasksAsync(
        IReadOnlyList<int> taskIds,
        CancellationToken cancellationToken = default)
    {
        if (taskIds.Count == 0) return Task.FromResult<IReadOnlyList<DistributionItem>>(Array.Empty<DistributionItem>());

        return Task.FromResult<IReadOnlyList<DistributionItem>>(
            _items.Where(i => taskIds.Contains(i.TaskId)).OrderBy(i => i.TaskId).ThenBy(i => i.Id).ToList());
    }

    public Task UpdateItemAsync(DistributionItem item, CancellationToken cancellationToken = default)
    {
        UpdateItemCalls++;

        var index = _items.FindIndex(i => i.Id == item.Id);
        if (index >= 0) _items[index] = item;

        return Task.CompletedTask;
    }

    public Task UpdateTaskStateAsync(int taskId, string state, CancellationToken cancellationToken = default)
    {
        UpdateTaskStateCalls++;

        var index = _tasks.FindIndex(t => t.Id == taskId);
        if (index >= 0) _tasks[index] = _tasks[index] with { State = state };

        return Task.CompletedTask;
    }

    public Task<int> PurgeTerminalTasksAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var doomed = _tasks
            .Where(t => t.CreatedAt < cutoff && DistributionStates.IsTerminal(t.State))
            .ToList();

        foreach (var task in doomed)
        {
            _items.RemoveAll(i => i.TaskId == task.Id);
        }

        _tasks.RemoveAll(t => doomed.Contains(t));

        return Task.FromResult(doomed.Count);
    }
}
