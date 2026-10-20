using ControlServer.Contracts;
using ControlServer.Core.Models;

namespace ControlServer.Core.Abstractions;

/// <summary>
/// 遥测数据的持久化端口（M4.2）。
/// <para>
/// <b>分层</b>（方案 §6.4）：
/// </para>
/// <list type="bullet">
///   <item><b>热层</b>：最新一帧在内存（<c>AgentHostRegistry</c>），不落这个端口；</item>
///   <item><b>温层</b>：5 秒明细（<c>agent_metrics</c>），保留 <c>DetailRetentionDays</c> 天；</item>
///   <item><b>冷层</b>：日聚合（<c>agent_metrics_daily</c>），保留 1 年；</item>
///   <item><b>事件层</b>：开关机记录（<c>agent_power_events</c>），永久保留（数据量极小）。</item>
/// </list>
/// <para>
/// ⚠️ <b>所有写入都必须是幂等的</b>：客户端断线重连后会把攒下的批次重发一遍，
/// 而"重连"在内网里一天可能发生几十次。按 <c>(AgentId, At)</c> / <c>(AgentId, At, EventId)</c>
/// 去重是这里唯一的正确做法 —— 让客户端"保证只发一次"是做不到的。
/// </para>
/// <para>实现见 Infrastructure 的 <c>EfAgentTelemetryStore</c>（Singleton + 内部 scope）。</para>
/// </summary>
public interface IAgentTelemetryStore
{
    // ── 指标明细 ──

    /// <summary>
    /// 追加一批指标样本，<b>已存在的（同 AgentId + At）忽略</b>。
    /// </summary>
    /// <returns>真正新写入的条数（用于日志与"重发没造成重复"的验证）。</returns>
    Task<int> AppendMetricsAsync(
        int agentId,
        IReadOnlyList<AgentMetricsSampleDto> samples,
        CancellationToken cancellationToken = default);

    /// <summary>按时间升序取明细。</summary>
    Task<IReadOnlyList<AgentMetricsSampleDto>> QueryMetricsAsync(
        int agentId,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>按时间升序取日聚合。</summary>
    Task<IReadOnlyList<AgentMetricsDaily>> QueryDailyAsync(
        int agentId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    /// <summary>把某一天的明细重算成一行日聚合（已存在则覆盖）。无明细时删掉该行。</summary>
    Task RebuildDailyAsync(DateOnly day, int sampleIntervalSeconds, CancellationToken cancellationToken = default);

    /// <summary>删掉早于 <paramref name="cutoff"/> 的指标明细。</summary>
    Task<int> PurgeMetricsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    /// <summary>删掉早于 <paramref name="cutoffDay"/> 的日聚合。</summary>
    Task<int> PurgeDailyAsync(DateOnly cutoffDay, CancellationToken cancellationToken = default);

    /// <summary>
    /// 各机器<b>最新一帧</b>摘要。
    /// <para>
    /// ⚠️ <b>故意没有"服务启动时用它把热层填满"这一步</b>：客户端每 5 秒推一次，
    /// 服务端重启后最多 5 秒online 的机器就都有值了。而离线的机器<b>本来就不该</b>
    /// 在列表里显示一个几小时前的 CPU 占用 —— 那会让人以为它还在跑。
    /// 因此热层只由实时推送填充，这个查询留给"详情页要一份最后一次已知值"的场景。
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<int, AgentMetricsSummaryDto>> GetLatestSummariesAsync(
        CancellationToken cancellationToken = default);

    // ── 开关机 / 异常事件 ──

    /// <summary>追加开关机事件（同 AgentId + At + EventId 去重）。返回真正新写入的条数。</summary>
    Task<int> AppendPowerEventsAsync(
        int agentId,
        IReadOnlyList<AgentPowerEventDto> events,
        CancellationToken cancellationToken = default);

    /// <summary>按时间<b>倒序</b>取开关机事件（最近的在最前）。</summary>
    Task<IReadOnlyList<AgentPowerEventDto>> QueryPowerEventsAsync(
        int agentId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>取一台机器全部开关机事件（用于算统计；数据量小，不分页）。</summary>
    Task<IReadOnlyList<AgentPowerEventDto>> QueryAllPowerEventsAsync(
        int agentId,
        CancellationToken cancellationToken = default);

    // ── 硬件清单 ──

    /// <summary>取最新一份硬件清单（没有则 null）。</summary>
    Task<AgentInventoryRecord?> GetInventoryAsync(int agentId, CancellationToken cancellationToken = default);

    /// <summary>保存硬件清单（覆盖旧的那一份）。</summary>
    Task SaveInventoryAsync(int agentId, AgentInventoryDto inventory, CancellationToken cancellationToken = default);

    // ── 主机删除时的连带清理 ──

    /// <summary>删掉一台机器的全部遥测数据（移除设备记录时调用，避免留下孤儿行）。</summary>
    Task PurgeAgentAsync(int agentId, CancellationToken cancellationToken = default);
}
