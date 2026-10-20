using ControlServer.Core.Models;

namespace ControlServer.Core.Abstractions;

/// <summary>
/// 分发任务与明细的持久化端口（M4.4）。
/// <para>实现见 Infrastructure 的 <c>EfDistributionStore</c>。</para>
/// </summary>
public interface IDistributionStore
{
    /// <summary>
    /// 建一个任务，并<b>一次性</b>为每个目标机器建一行明细。
    /// <para>
    /// <b>为什么建任务时就把明细建满</b>：目标里有离线机器，任务是"等它上线再传"。
    /// 若改成"上线时才建明细"，任务总览页就会显示成"只派了 3 台"，
    /// 而后台其实还惦记着另外 20 台 —— 页面与现实不一致是最难查的。
    /// </para>
    /// <para>
    /// ⚠️ 参数是 <see cref="DistributionTarget"/> 而不是纯 id 列表：
    /// 每台可以各带一个落地目录（2026-10-13 起），而目录是<b>明细级</b>的属性。
    /// </para>
    /// </summary>
    Task<DistributionTask> CreateTaskAsync(
        DistributionTask task,
        IReadOnlyList<DistributionTarget> targets,
        CancellationToken cancellationToken = default);

    /// <summary>按 id 取任务。</summary>
    Task<DistributionTask?> FindTaskAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>任务列表（按创建时间倒序，最多 <paramref name="limit"/> 条）。</summary>
    Task<IReadOnlyList<DistributionTask>> ListTasksAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>一个任务的全部明细（按 id 升序）。</summary>
    Task<IReadOnlyList<DistributionItem>> ListItemsAsync(int taskId, CancellationToken cancellationToken = default);

    /// <summary>按 id 取一行明细。</summary>
    Task<DistributionItem?> FindItemAsync(int itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 某台机器<b>尚未完成</b>的明细（含 <c>pending</c> / <c>running</c>）。
    /// <para>客户端上线时调它补领任务 —— 这是"离线机器上线后也能收到文件"的实现点。</para>
    /// </summary>
    Task<IReadOnlyList<DistributionItem>> ListOpenItemsAsync(int agentId, CancellationToken cancellationToken = default);

    /// <summary>取多个任务的全部明细（任务列表页一次性渲染，避免 N+1 查询）。</summary>
    Task<IReadOnlyList<DistributionItem>> ListItemsForTasksAsync(
        IReadOnlyList<int> taskIds,
        CancellationToken cancellationToken = default);

    /// <summary>更新一行明细（进度 / 状态 / 失败原因）。</summary>
    Task UpdateItemAsync(DistributionItem item, CancellationToken cancellationToken = default);

    /// <summary>更新任务的总状态（由服务层按明细汇总后写入）。</summary>
    Task UpdateTaskStateAsync(int taskId, string state, CancellationToken cancellationToken = default);

    /// <summary>删掉早于 <paramref name="cutoff"/> 且已终结的任务（连带明细）。返回删掉的任务数。</summary>
    Task<int> PurgeTerminalTasksAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}
