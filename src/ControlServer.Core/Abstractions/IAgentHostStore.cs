using ControlServer.Contracts;
using ControlServer.Core.Models;

namespace ControlServer.Core.Abstractions;

/// <summary>
/// 客户端主机在线表的持久化端口（M4.1）。
/// <para>
/// <b>为什么是端口接口</b>：Core 不能引用 EF Core（铁律 #1）。实现放 Infrastructure
/// （<c>EfAgentHostStore</c>），与既有的 <see cref="IConfigDocumentStore"/> 同一模式 ——
/// Singleton + 内部 <c>IServiceScopeFactory</c> 开 scope 拿 Scoped 的 <c>DbContext</c>。
/// </para>
/// <para>
/// <b>为什么必须是 Singleton</b>：注册（<c>POST /api/agents/register</c>）可能被多台机器<b>同时</b>
/// 打到。"按 MachineKey 查有没有、没有就插"这种先查后写在并发下会插出两行，
/// 唯一索引会抛异常让其中一台注册失败。实现内部必须把这条路串行化并把"唯一索引冲突"
/// 退化成"再查一次返回已有行"（见实现注释）。
/// </para>
/// </summary>
public interface IAgentHostStore
{
    /// <summary>全部主机（按显示名 / Id 排序）。</summary>
    Task<IReadOnlyList<AgentHost>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>按主键查。</summary>
    Task<AgentHost?> FindAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>按机器稳定标识查（注册时的"这台机器是不是已经注册过"）。</summary>
    Task<AgentHost?> FindByMachineKeyAsync(string machineKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 agentKey 的哈希查（每一次 Agent 认证都会走这里，<b>热路径</b>）。
    /// </summary>
    Task<AgentHost?> FindByAgentKeyHashAsync(string agentKeyHash, CancellationToken cancellationToken = default);

    /// <summary>新建一行（失败时抛，由调用方转成明确错误）。</summary>
    Task<AgentHost> CreateAsync(AgentHost host, CancellationToken cancellationToken = default);

    /// <summary>
    /// 已存在机器（同 MachineKey）的<b>重新注册</b>：换 agentKey、刷新主机信息。
    /// <para>
    /// ⚠️ <b>2026-10-15 起不再清「拉黑」标记</b>：被拉黑的机器连注册都会被拒（在
    /// <c>AgentEnrollmentService</c> 那一层就挡住了），所以这里根本走不到它。
    /// 改之前这里会清 `IsRevoked`，那是"吊销很弱、重注册即复活"的来源。
    /// </para>
    /// <para>
    /// 存在的意义：客户机重装系统 / 删掉数据目录后再注册，不该在列表里多出一行"重复设备"。
    /// </para>
    /// <para>
    /// ⚠️ <b>刻意不接收"最后心跳时间"参数</b>：重新注册不是一次心跳，不能刷新
    /// <c>LastSeenAt</c>。这样"在线状态"永远是心跳说了算 ——
    /// 一台刚注册、还没建立长连接的机器会如实显示成「等待连接」而不是「在线」。
    /// </para>
    /// </summary>
    Task<bool> UpdateRegistrationAsync(
        int id,
        string agentKeyHash,
        string? agentVersion,
        string? address,
        AgentHostInfoDto? info,
        CancellationToken cancellationToken = default);

    /// <summary>心跳：只更新"最后在线时间 / 地址 / 版本"，不碰别的字段。</summary>
    Task<bool> TouchAsync(
        int id,
        DateTimeOffset seenAt,
        string? address,
        string? agentVersion,
        CancellationToken cancellationToken = default);

    /// <summary>改名 / 分组 / 备注（三者都可空 = 不改）。</summary>
    Task<bool> UpdateDetailsAsync(
        int id,
        string? displayName,
        string? group,
        string? note,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置 / 解除**拉黑**标记（2026-10-15，取代原「吊销」）。
    /// <para>
    /// ⚠️ <b>刻意不动 agentKey</b>（旧吊销会把它清成 null）：清掉之后"取消拉黑"就变成
    /// "必须重新注册"，而需求要的是<b>立即恢复连接</b>。保留密钥 + 认证时按标记拒绝，
    /// 才是"挡住了、但随时能放开"。
    /// </para>
    /// </summary>
    Task<bool> SetBlockedAsync(int id, bool blocked, CancellationToken cancellationToken = default);

    /// <summary>
    /// 记录客户端回报的「已应用配置版本」（M4.5）。
    /// <para>
    /// <b>只增不减</b>：迟到的旧回报（网络乱序 / 客户端重发）不能把页面上的
    /// "已应用 v5" 退回到 v3 —— 那会让运维以为配置回滚了。
    /// 实现在 SQL 里用 <c>SET x = MAX(x, @v)</c> 保证原子，避免读改写竞态。
    /// </para>
    /// </summary>
    Task<bool> SetAppliedConfigVersionAsync(int id, int version, CancellationToken cancellationToken = default);

    /// <summary>
    /// 记住这台客户机"上次用的落脚目录"（2026-10-13）。
    /// <para>
    /// <b>两个方向各自独立</b>：<paramref name="targetDir"/> 是下发（落地）目录，
    /// <paramref name="sourceDir"/> 是回传（读取）目录。
    /// <b>传 null = 不动那一项</b>（不是"清空"）—— 调用方常常只更新其中一个方向。
    /// </para>
    /// <para>
    /// 它纯粹是**给人省一次点击的默认值**：下次打开下发页会把 <paramref name="targetDir"/>
    /// 预填回那一行，人照样可以改、可以清空。因此这里<b>不做任何路径合法性校验</b>
    /// （校验在客户机落盘那一步，见 <c>FileTransferClient</c>）。
    /// </para>
    /// </summary>
    Task<bool> SetLastPathAsync(
        int id,
        string? targetDir,
        string? sourceDir,
        CancellationToken cancellationToken = default);

    /// <summary>移除记录（<b>不等于卸载客户机上的 Agent</b>）。</summary>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}
