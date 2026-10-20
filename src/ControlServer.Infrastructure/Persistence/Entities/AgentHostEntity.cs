namespace ControlServer.Infrastructure.Persistence.Entities;

/// <summary>
/// 客户端主机（在线表的一行）。M4.1 新增。
/// <para>
/// 领域模型见 <see cref="ControlServer.Core.Models.AgentHost"/>；
/// 这张表是它的持久化形态。<b>两份形状必须同步改</b> —— 领域模型是"业务看到的"，
/// 实体是"库里的"，中间由 <c>EfAgentHostStore</c> 做映射。
/// </para>
/// </summary>
public sealed class AgentHostEntity
{
    /// <summary>主键（自增）。</summary>
    public int Id { get; set; }

    /// <summary>
    /// 机器稳定标识（机器码派生，<c>XXXX-XXXX-XXXX-XXXX</c>）。
    /// <b>唯一索引</b> —— 它是"同一台机器"的唯一依据，重复即代表在线表已经脏了。
    /// </summary>
    public string MachineKey { get; set; } = string.Empty;

    /// <summary>显示名（默认取主机名，管理员可改）。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>分组（展厅A / 机房B…）。</summary>
    public string? Group { get; set; }

    /// <summary>备注。</summary>
    public string? Note { get; set; }

    /// <summary>
    /// agentKey 的 SHA-256（16 进制 64 字符）。<b>只存哈希，不存明文</b>（见领域模型注释）。
    /// 建索引 —— 每一次 Agent 认证都按它等值查询，是热路径。
    /// </summary>
    public string? AgentKeyHash { get; set; }

    /// <summary>最后一次上报的 Agent 版本。</summary>
    public string? AgentVersion { get; set; }

    /// <summary>最后一次心跳的来源地址。</summary>
    public string? LastAddress { get; set; }

    /// <summary>首次注册时间。</summary>
    public DateTimeOffset RegisteredAt { get; set; }

    /// <summary>最后一次心跳时间；null = 注册后从未连上。</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>是否已被管理员吊销。</summary>
    public bool IsBlocked { get; set; }

    /// <summary>最后一次上报的主机信息（JSON；M4.2 起扩成完整硬件清单）。</summary>
    public string? HostInfoJson { get; set; }

    /// <summary>
    /// 客户机回报的「已应用配置版本」（M4.5）；null = 从未回报过。
    /// <para>
    /// ⚠️ <b>这里只放客户端回报的值</b>。"服务端下发了哪一版"在 <c>AgentConfigs.Version</c> ——
    /// 同一个数字存两处迟早不同步，而不同步的表现是"页面说已同步、机器其实在跑旧配置"。
    /// </para>
    /// </summary>
    public int? AppliedConfigVersion { get; set; }

    /// <summary>
    /// 上一次下发文件时用的落脚目录（2026-10-13）。null = 还没被下发过。
    /// <para>
    /// 服务端在创建下发任务时记下，下发页用它预填每一行的「存放路径」——
    /// 现场同一台机器每次落的地方几乎都一样，让人每回重新点一遍目录纯属浪费。
    /// </para>
    /// </summary>
    public string? LastTargetDir { get; set; }

    /// <summary>上一次回传时读的目录（回传方向，语义同 <see cref="LastTargetDir"/>）。</summary>
    public string? LastSourceDir { get; set; }
}

/// <summary>
/// 注册令牌 / 加入码。M4.1 新增，**2026-10-13 起已无任何读写方**。
/// <para>
/// ⚠️ <b>保留它、以及 `AppDbContext.EnrollmentTokens` 的映射，是刻意的</b>：
/// 「取消准入令牌」选择了"行为 + 界面全拆、但不动数据库"的方案 ——
/// 删掉实体或 DbSet 会让 EF 模型与 <c>M4_AgentHosts</c> 迁移 / 模型快照不一致，
/// 从而必须补一个 <c>DROP TABLE</c> 的破坏性迁移（老部署升级时会真的丢掉这张表）。
/// </para>
/// <para>
/// 现在的状态是：表还在（新装库照样建、老库照样留着），但没有任何代码读它或写它。
/// 将来若确认不需要，可以单独发一个只做 <c>DROP TABLE</c> 的迁移把它清掉。
/// </para>
/// </summary>
public sealed class EnrollmentTokenEntity
{
    public int Id { get; set; }

    /// <summary>令牌的 SHA-256（16 进制）。<b>唯一索引</b>：注册时按它等值查询。</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>过期时间；null = 永久有效。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>吊销时间；null = 未吊销。（⚠️ 这张表已废弃，见上面的类注释
    /// —— 这个字段**与 M4 的「拉黑」无关**，别按名字联想。）</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>备注。</summary>
    public string? Note { get; set; }

    /// <summary>已被使用次数。</summary>
    public int UseCount { get; set; }

    /// <summary>true = 加入码（单次使用 + 短时效）。</summary>
    public bool SingleUse { get; set; }
}
