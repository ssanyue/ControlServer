using ControlServer.Contracts;

namespace ControlServer.Core.Abstractions;

/// <summary>
/// 实时通知端口。Core 只依赖此接口；具体实现（SignalR / 其他）在 Infrastructure。
/// 这样保证内核与 ASP.NET Core 解耦，可被未来的被控端 Agent 直接复用。
/// </summary>
public interface INotifier
{
    Task LogAddedAsync(LogDto item, CancellationToken cancellationToken = default);

    Task StatusChangedAsync(StatusSnapshot snapshot, CancellationToken cancellationToken = default);

    Task ActionResultAsync(ActionResultDto result, CancellationToken cancellationToken = default);

    /// <summary>
    /// 推送一条**收发日志**（M2-F 新增）。
    /// <para>与 <see cref="LogAddedAsync"/> 是两条独立通道：前者是「运维日志」，本条是「收发报文」，
    /// 旧版主界面上的「最近日志」面板消费的正是后者。</para>
    /// </summary>
    Task TrafficLogAddedAsync(TrafficLogDto item, CancellationToken cancellationToken = default);

    /// <summary>通知配置已变更（M2-F 新增）。客户端据此重新拉取配置。</summary>
    Task ConfigChangedAsync(ConfigChangedDto change, CancellationToken cancellationToken = default);

    /// <summary>
    /// 通知设备授权状态已变更（M3 授权子系统新增）。
    /// <para>
    /// 授权是设备级状态：一次导入对<b>所有</b>在线会话同时生效。不推送的话，
    /// 别的窗口会停留在旧横幅上，而它背后的按钮其实已经能用了 —— 界面与实际行为不一致，
    /// 是现场最难解释的一类问题。
    /// </para>
    /// </summary>
    Task LicenseChangedAsync(LicenseDto license, CancellationToken cancellationToken = default);

    /// <summary>
    /// 客户端主机记录发生变化（M4.1 新增）：注册 / 改名 / 吊销 / 心跳刷新 / 列表增删。
    /// <para>前端收到后<b>重新拉一次全量列表</b> —— 在线表规模有限（几十到几百台），
    /// 全量刷新比增量合并简单得多，也不会出现"漏了一条就没同步"的死角。</para>
    /// </summary>
    Task AgentChangedAsync(AgentHostDto host, CancellationToken cancellationToken = default);

    /// <summary>一台客户端主机上线（M4.1 新增）。</summary>
    Task AgentOnlineAsync(AgentHostDto host, CancellationToken cancellationToken = default);

    /// <summary>一台客户端主机离线（M4.1 新增）。</summary>
    Task AgentOfflineAsync(AgentHostDto host, CancellationToken cancellationToken = default);

    /// <summary>
    /// 某台客户机的遥测数据已刷新（M4.2 新增）。
    /// <para>
    /// <b>这一条是高频的</b>（服务端节流到默认 2 秒一轮，一轮里每台变化的机器各一条），
    /// 因此载荷刻意只带"最新一帧"（<see cref="AgentLiveMetricsDto"/>）——
    /// 把历史塞进来会让每条消息涨到几十 KB，而 300 台时那是每秒几 MB 的广播。
    /// </para>
    /// </summary>
    Task AgentTelemetryUpdatedAsync(AgentLiveMetricsDto live, CancellationToken cancellationToken = default);

    /// <summary>
    /// 一条文件传输的进度变化（M4.4 新增）。
    /// <para>
    /// <b>粒度是"单个目标的一行"</b>，而不是整个任务：100+ 台机器时一次下发就是 100 行，
    /// 每 5% 推一次全量会让页面被自己的重绘压垮。前端据此原地更新那一行。
    /// </para>
    /// <para>任务终态（完成 / 部分成功 / 失败）同样借这条事件送达，前端据此刷新汇总。</para>
    /// </summary>
    Task TransferProgressAsync(TransferProgressDto progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// 某台客户机的配置（任务清单 / UDP 通道）已变更（M4.5）。
    /// <para>
    /// <b>载荷只有客户机 Id</b>：这是给网页用的"打开着那台详情页的话，去重拉一次配置"。
    /// 与 <see cref="AgentChangedAsync"/> 分开是因为后者连心跳都会触发（很频繁），
    /// 而配置变更很少发生 —— 混在一起会让页面在每次心跳时都重拉一遍任务清单。
    /// </para>
    /// </summary>
    Task AgentConfigChangedAsync(int agentId, CancellationToken cancellationToken = default);
}
