using ControlServer.Contracts;

namespace ControlServer.Core.Models;

/// <summary>
/// 一台客户端主机（在线表的一行）。
/// <para>
/// <b>识别键是 <see cref="MachineKey"/>（机器码派生的稳定标识），不是 IP。</b>
/// 客户机的 IP 会变（DHCP 续租、换网段、插拔网线），机器码不会 ——
/// 按 IP 建表会出现"同一台机器换了个 IP 就多出一行"，而旧行永远挂着不消失。
/// </para>
/// <para>
/// <b><see cref="AgentKeyHash"/> 存哈希、不存明文</b>：与口令同理 ——
/// 库文件被拷走时，明文密钥等于把"任意一台客户机的身份"一起送出去。
/// 代价是服务端<b>永远无法再给出</b> agentKey，丢了只能移除记录后再重新注册（这是刻意的取舍）。
/// </para>
/// </summary>
/// <param name="Id">主键。</param>
/// <param name="MachineKey">机器稳定标识（由机器码派生，形如 <c>084C-98CF-BE5A-9AEC</c>）。</param>
/// <param name="DisplayName">显示名（管理员可改；默认取主机名）。</param>
/// <param name="Group">分组（展厅A / 机房B…，纯展示与批量操作的目标）。</param>
/// <param name="Note">备注。</param>
/// <param name="AgentKeyHash">agentKey 的 SHA-256（16 进制）。</param>
/// <param name="AgentVersion">最后一次上报的 Agent 版本。</param>
/// <param name="LastAddress">
/// 最后一次联系的来源地址（<c>IP:端口</c>）—— 注册与心跳都会更新它。
/// <para>
/// 它与 <see cref="LastSeenAt"/> 刻意解耦：一台机器可能"注册过（所以有地址）但从未连上"，
/// 那种情况下列表显示「等待连接」+ 一个地址，正好回答"它从哪来、为什么没连上"。
/// </para>
/// </param>
/// <param name="RegisteredAt">首次注册时间。</param>
/// <param name="LastSeenAt">
/// 最后一次<b>心跳</b>时间；<b>null = 注册后从未连上</b>。
/// <para>⚠️ 注册本身<b>不</b>写这个字段 —— 它是在线状态的唯一判据（见 <c>AgentPresence</c>）。</para>
/// </param>
/// <param name="IsBlocked">
    /// 是否已被管理员**拉黑**（2026-10-15，取代原「吊销」）。
    /// <para>
    /// 拉黑后<b>不再接受它的任何连接</b>（REST 与长连接都拒），但<b>记录与 agentKey 都保留</b> ——
    /// 于是"取消拉黑"之后那台机器下一次重试就能连上，不必重新注册。
    /// </para>
    /// </param>
/// <param name="Info">最后一次上报的主机信息（M4.2 起扩成完整硬件清单）。</param>
/// <param name="AppliedConfigVersion">
/// 客户机回报的「已应用配置版本」（M4.5）；<b>null = 从未回报过</b>（老客户端 / 刚注册）。
/// <para>
/// ⚠️ <b>这里只存"客户端说它应用到了哪一版"，不存"服务端下发了哪一版"</b> ——
/// 后者是 <c>AgentConfigs.Version</c>。同一个数字存两处迟早不同步，
/// 而不同步的表现是页面显示"已同步"而客户机其实在跑旧配置（这类假阴性最难查）。
/// </para>
/// </param>
/// <param name="LastTargetDir">
/// 这台客户机**上一次被下发文件时用的落脚目录**（2026-10-13，需求："不用每次都去选路径"）。
/// <para>
/// 由服务端在**创建下发任务时**记下（取该目标那条明细的最终目录），下次打开下发页就把它预填回这一行。
/// 它**只是默认值**：填进去之后仍然可以改、可以清空 —— 空串在界面上的语义就是
/// "用那台机器自己的默认目录"。
/// </para>
/// <para>null = 这台机器还没被下发过（或从没用过自定义目录）。</para>
/// </param>
/// <param name="LastSourceDir">
/// 这台客户机**上一次被回传时读的目录**（同上，回传方向）。
/// <para>⚠️ 存的是**目录**而不是完整文件路径：预填时能直接用它当浏览器的落点，也便于人改。</para>
/// </param>
public sealed record AgentHost(
    int Id,
    string MachineKey,
    string DisplayName,
    string? Group,
    string? Note,
    string? AgentKeyHash,
    string? AgentVersion,
    string? LastAddress,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? LastSeenAt,
    bool IsBlocked,
    AgentHostInfoDto? Info,
    int? AppliedConfigVersion = null,
    string? LastTargetDir = null,
    string? LastSourceDir = null);
