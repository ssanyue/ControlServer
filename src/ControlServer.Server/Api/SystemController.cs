using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using ControlServer.Server.HostedServices;
using ControlServer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ControlServer.Server.Api;

/// <summary>
/// 系统端点（M2-E4 扩展）。
/// <para>
/// <b>授权粒度刻意分两级</b>：机器码是**授权信息**，仍限管理员；
/// 系统信息与手动校准属于**运维日常**，任何能操作的角色都能用 ——
/// 把它们一起锁进 AdminOnly 只会逼着大家共用管理员账号。
/// </para>
/// </summary>
[ApiController]
[Route("api/system")]
[Authorize]
public sealed class SystemController : ControllerBase
{
    private readonly IMachineCodeProvider _machineCodeProvider;
    private readonly StatusProvider _status;
    private readonly TimeSyncService _timeSync;
    private readonly TimeSyncHostedOptions _timeSyncOptions;
    private readonly TimerSchedulerService _scheduler;
    private readonly IDataDirectory _dataDir;
    private readonly ServerAddressService _address;

    public SystemController(
        IMachineCodeProvider machineCodeProvider,
        StatusProvider status,
        TimeSyncService timeSync,
        TimeSyncHostedOptions timeSyncOptions,
        TimerSchedulerService scheduler,
        IDataDirectory dataDir,
        ServerAddressService address)
    {
        _machineCodeProvider = machineCodeProvider;
        _status = status;
        _timeSync = timeSync;
        _timeSyncOptions = timeSyncOptions;
        _scheduler = scheduler;
        _dataDir = dataDir;
        _address = address;
    }

    /// <summary>机器码与指纹（仅管理员；用于授权核对）。</summary>
    [HttpGet("machine-code")]
    [Authorize(Policy = Policies.AdminOnly)]
    public IActionResult MachineCode() => Ok(new
    {
        machineCode = _machineCodeProvider.GetMachineCode(),
        fingerprint = _machineCodeProvider.GetRawFingerprint(),
        platform = Environment.OSVersion.Platform.ToString(),
        osDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    });

    /// <summary>
    /// 系统与调度诊断信息。
    /// <para>
    /// 复用 <see cref="StatusProvider"/> 的机器 / 端口 / 运行时长字段，只在此之上叠加时间同步与定时计数 ——
    /// 两处各查一次会出现"首页说端口 8899、诊断页说 9000"这种最难查的不一致。
    /// </para>
    /// </summary>
    [HttpGet("info")]
    [Authorize(Policy = Policies.CanOperate)]
    public IActionResult Info()
    {
        var status = _status.Create();
        var offset = _timeSync.Offset;

        return Ok(new SystemInfoDto(
            Product: status.Product,
            Version: status.Version,
            MachineName: status.MachineName,
            OsPlatform: status.OsPlatform,
            ServerTime: BeijingTime.Now,
            TimeZone: BeijingTime.DisplayName,
            HttpPort: status.HttpPort,
            PairingPort: status.PairingPort,
            ListenerRunning: status.ListenerRunning,
            UptimeSeconds: status.UptimeSeconds,
            RecentLogCount: status.RecentLogCount,
            DataDirectory: _dataDir.Root,
            TimeSync: new TimeSyncInfoDto(
                AutoSync: _timeSyncOptions.AutoSync,
                ResyncIntervalSeconds: _timeSyncOptions.ResyncInterval.TotalSeconds,
                Synchronized: _timeSync.IsSynchronized,
                // 存的是 UTC，展示一律换成北京时间 —— 否则前端会显示小 8 小时的"上次校准"。
                LastSyncAt: _timeSync.LastSyncAtUtc is { } utc ? BeijingTime.ToBeijing(utc) : null,
                Source: _timeSync.LastSource,
                OffsetMs: (long)offset.TotalMilliseconds,
                OffsetText: TimeSyncService.FormatOffset(offset),
                RoundTripMs: _timeSync.LastRoundTrip is { } rt ? (long)rt.TotalMilliseconds : null,
                Corrected: BeijingTime.IsCorrected,
                LastError: _timeSync.LastError,
                AttemptCount: _timeSync.AttemptCount,
                FailureCount: _timeSync.FailureCount,
                Servers: _timeSync.Servers),
            Timers: new TimerSummaryDto(
                Enabled: _scheduler.Options.Enabled,
                TickIntervalSeconds: _scheduler.Options.EffectiveTickInterval.TotalSeconds,
                CatchUpWindowSeconds: _scheduler.Options.EffectiveCatchUpWindow.TotalSeconds,
                RetentionDays: _scheduler.Options.RetentionDays,
                FiredCount: _scheduler.FiredCount,
                MissedCount: _scheduler.MissedCount,
                TriggerCount: _scheduler.DescribeActiveTriggers().Count,
                NextFireAt: _scheduler.FindNextFireAt())));
    }

    /// <summary>
    /// 立刻做一次 SNTP 校准。
    /// <para>
    /// 成功与失败都返回 <b>200</b>：这是"查校准结果"的接口，失败原因本身就是要展示的信息
    /// （与 <c>POST /api/actions/preview</c> 的口径一致）。调用方看响应体里的 <c>ok</c>。
    /// </para>
    /// <para>
    /// 校准会<b>立刻改变全应用的时间口径</b>（日志时间戳、定时器判定都会跟着走），
    /// 因此这是运维操作而非自动行为 —— <c>Server:Time:AutoSync</c> 才负责自动重校。
    /// </para>
    /// </summary>
    [HttpPost("timesync")]
    [Authorize(Policy = Policies.CanOperate)]
    public async Task<IActionResult> TimeSync(CancellationToken cancellationToken)
    {
        var outcome = await _timeSync.SyncAsync(cancellationToken).ConfigureAwait(false);

        return Ok(new TimeSyncResultDto(
            Ok: outcome.Ok,
            OffsetMs: (long)outcome.Offset.TotalMilliseconds,
            OffsetText: TimeSyncService.FormatOffset(outcome.Offset),
            OffsetJumpMs: (long)outcome.OffsetJump.TotalMilliseconds,
            Source: outcome.Source,
            RoundTripMs: outcome.RoundTrip is { } rt ? (long)rt.TotalMilliseconds : null,
            Error: outcome.Error,
            Attempts: outcome.Attempts,
            ServerTime: BeijingTime.Now));
    }

    /// <summary>
    /// 当前**实际**监听地址。
    /// <para>
    /// 读的是 Kestrel 真正在听的地址而非配置值 —— 热切换之后两者会不一致，
    /// 界面必须以后者为准，否则会出现"设置页说 9000、实际连 8899"的死循环。
    /// </para>
    /// </summary>
    [HttpGet("address")]
    [Authorize(Policy = Policies.CanOperate)]
    public IActionResult Address() => Ok(_address.Describe());

    /// <summary>
    /// 热切换 HTTP 监听端口（**不重启进程、不断当前连接**）。
    /// <para>
    /// <b>为什么这是 AdminOnly</b>：一次端口变更会让所有已连接的客户端失联
    /// （浏览器必须在收到响应后主动跳到新原点），这是全站级别的运维动作，
    /// 权限门槛应高于"手动校准时间"这类日常操作。
    /// </para>
    /// <para>
    /// <b>失败是同步可见的</b>：新端口绑不上（被占用 / 权限不足）时，
    /// 旧监听<b>一动没动</b>，响应直接给出 409 与原因 —— 不存在
    /// "服务已停在新端口没起来"的中间状态（见 <c>SwitchableConnectionListener</c>）。
    /// </para>
    /// <para>
    /// 该操作会写审计日志，并可通过 <c>ServerAddressChanged</c> 事件推送给所有客户端。
    /// </para>
    /// </summary>
    /// <response code="200">已切换（或目标与现状一致）。看响应体的 <c>status</c>。</response>
    /// <response code="400">地址格式非法。</response>
    /// <response code="409">目标端口不可用（被占用 / 权限不足），服务仍在原端口。</response>
    [HttpPost("address")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> ChangeAddress(
        [FromBody] ServerAddressChangeRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _address.ChangeAsync(request, cancellationToken).ConfigureAwait(false);

        return result.Status switch
        {
            "Accepted" or "NoChange" => Ok(result),
            "Invalid" => BadRequest(result),
            _ => Conflict(result),
        };
    }
}
