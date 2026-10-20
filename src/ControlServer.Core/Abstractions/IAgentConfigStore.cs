using ControlServer.Core.Models;

namespace ControlServer.Core.Abstractions;

/// <summary>
/// 客户机配置的持久化端口（M4.5）。
/// <para>
/// <b>版本号在实现内部生成</b>，接口上不暴露"我要写版本 N"这条路 ——
/// 见 <see cref="AgentConfigContent"/> 的注释：版本号是客户端判断新旧唯一依据，
/// 允许外部指定就会撞车。
/// </para>
/// <para>
/// 与既有的客户端存储端口同一模式：Singleton + 内部 <c>IServiceScopeFactory</c> 开 scope。
/// </para>
/// </summary>
public interface IAgentConfigStore
{
    /// <summary>读该机的配置；<b>null = 从未保存过</b>（等价于"空配置 + 版本 0"，但这里如实返回 null，
    /// 让调用方自己能区分"没配过"与"配了个空的"）。</summary>
    Task<AgentConfigRecord?> FindAsync(int agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 全部客户机的「已保存配置版本」（Id → Version）。
    /// <para>
    /// 列表页要给每行显示版本号，逐台查会打成 N 次查询（100+ 台时很痛）。
    /// 没配过的机器<b>不会出现在结果里</b>，调用方按 0 处理。
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<int, int>> ListVersionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存（全量替换内容），版本号 <b>+1</b>。返回保存后的记录。
    /// <para>版本从 1 起：首次保存得到 1，之后 2、3……</para>
    /// </summary>
    Task<AgentConfigRecord> SaveAsync(
        int agentId,
        AgentConfigContent content,
        CancellationToken cancellationToken = default);

    /// <summary>删除该机的配置（移除设备时连带清理）。</summary>
    Task<bool> DeleteAsync(int agentId, CancellationToken cancellationToken = default);
}
