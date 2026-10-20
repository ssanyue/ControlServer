namespace ControlServer.Infrastructure.Persistence.Entities;

/// <summary>
/// 客户机的配置（M4.5）。<b>一台机器一行</b>。
/// <para>
/// 领域模型见 <c>ControlServer.Core.Models.AgentConfigRecord</c>；
/// 内容（三张任务清单 + UDP 通道）整体存 <see cref="Json"/> —— 它是"一台机器的一组设置"，
/// 拆成多张表只会带来 join 与事务负担，却换不到任何查询能力
/// （没有任何场景需要"按任务的目标 IP 反查机器"）。
/// </para>
/// </summary>
public sealed class AgentConfigEntity
{
    /// <summary>主键（自增）。</summary>
    public int Id { get; set; }

    /// <summary>
    /// 归属的客户机 Id。
    /// <para><b>唯一索引</b> —— 一台机器只能有一份配置。多行会让"读配置"变成随机取一份，
    /// 而表现是"保存成功了但机器行为没变"。</para>
    /// </summary>
    public int AgentId { get; set; }

    /// <summary>
    /// 配置版本号（单调递增，从 1 开始）。
    /// <para>客户端据此判断"这版是不是比手上新"——乱序到达时旧版必须被丢弃，
    /// 否则会出现"刚应用完新配置又被旧配置覆盖回去"。</para>
    /// </summary>
    public int Version { get; set; }

    /// <summary>最后更新时间（Unix 毫秒）。</summary>
    /// <remarks>
    /// ⚠️ <b>用毫秒整数而不是 <c>DateTimeOffset</c></b>：SQLite 的 EF 提供程序拒绝翻译
    /// <c>DateTimeOffset</c> 上的排序（M4.2 踩过），而配置列表要按更新时间倒序。
    /// </remarks>
    public long UpdatedAtMs { get; set; }

    /// <summary>配置正文（JSON，<c>AgentConfigContent</c> 的序列化形态）。</summary>
    public string Json { get; set; } = string.Empty;
}
