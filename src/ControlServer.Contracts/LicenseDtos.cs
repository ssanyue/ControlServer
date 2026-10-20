namespace ControlServer.Contracts;

/// <summary>
/// 授权文件里的展示性信息（不含签名字段 —— 签名属于内部实现，没有展示价值）。
/// </summary>
/// <param name="SerialNumber">序列号（<c>sn</c>）。</param>
/// <param name="Vendor">厂商标识（<c>vi</c>）。</param>
/// <param name="Customer">客户备注（<c>hn</c>）。</param>
/// <param name="IssuedOn">签发日（北京日期，<c>yyyy-MM-dd</c>）。</param>
/// <param name="IsUniversal">是否为通用授权（签发时未绑机器码）。</param>
/// <param name="BoundMachineCode">当前生效的绑定机器码（文档里的 <c>mc</c>，或通用授权的侧车绑定）。</param>
/// <param name="BoundAt">绑定时刻（北京时间文本）；未记录为空串。</param>
/// <param name="NetworkVerifiedAt">授权里的联网校对时间 <c>ivt</c>（北京时间文本）。</param>
/// <param name="ImportDeadline">通用授权的最迟导入绑定时刻（北京时间文本）；不限为空串。</param>
public sealed record LicenseDocumentDto(
    string SerialNumber,
    string Vendor,
    string Customer,
    string IssuedOn,
    bool IsUniversal,
    string BoundMachineCode,
    string BoundAt,
    string NetworkVerifiedAt,
    string ImportDeadline);

/// <summary>
/// 本机自动试用的展示信息。
/// </summary>
/// <param name="IssuedAt">试用起始时刻（北京时间文本）。</param>
/// <param name="ExpiresOn">到期日（北京日期，<c>yyyy-MM-dd</c>）。</param>
/// <param name="Days">试用总天数。</param>
/// <param name="AnchorSource">
/// 起点时间来源：<c>ntp</c> = 联网校时；<c>local</c> = 联网不可得，退回本机时钟。
/// <para>如实展示，让人能判断这份试用的起点可不可信。</para>
/// </param>
public sealed record LicenseTrialDto(
    string IssuedAt,
    string ExpiresOn,
    int Days,
    string AnchorSource);

/// <summary>
/// 设备授权状态（前端横幅、徽章、授权面板都用它）。
/// </summary>
/// <param name="StatusCode">状态码，<c>0..4</c>，与既有实现对齐（见 Core 的 <c>LicenseStatus</c>）。</param>
/// <param name="Status">状态名（<c>NotAuthorized</c> / <c>Authorized</c> / <c>Expired</c> / <c>Error</c> / <c>Trial</c>）。</param>
/// <param name="StatusDisplayName">中文显示名。</param>
/// <param name="IsUsable">
/// 设备当前是否可用。<b>前端必须用它决定是否禁用入口，不要自己比较状态码</b> ——
/// 状态码是有序数字，但"可用"是一个白名单，不是区间。
/// </param>
/// <param name="Kind">授权类型（<c>full</c> / <c>trial</c> / <c>temp</c>；本机试用也是 <c>trial</c>）。</param>
/// <param name="RemainingDays">剩余天数；<c>-1</c> = 未知，<c>99999</c> = 永久，<c>0</c> = 已过期。</param>
/// <param name="ExpiresAt">到期时刻（北京时间）；永久或未知为 <c>null</c>。</param>
/// <param name="Info">面向用户的一句话摘要。</param>
/// <param name="LastError">失败原因；可用时为空串。</param>
/// <param name="MachineCode">本机机器码（管理员可复制给供应方）。</param>
/// <param name="Enforcement">当前<b>生效</b>的执行档位（<c>Off</c> / <c>Warn</c> / <c>Enforce</c>）。</param>
/// <param name="ConfiguredEnforcement">配置文件里写的档位（与 <paramref name="Enforcement"/> 可能不同，见 <c>LicenseService</c>）。</param>
/// <param name="IsLocalTrial">当前生效的是否为本机自动试用（而不是厂商签发的授权）。</param>
/// <param name="Document">厂商授权的展示信息；无授权文件为 <c>null</c>。</param>
/// <param name="Trial">本机试用记录；从未生成过为 <c>null</c>。</param>
public sealed record LicenseDto(
    int StatusCode,
    string Status,
    string StatusDisplayName,
    bool IsUsable,
    string Kind,
    string KindDisplayName,
    int RemainingDays,
    DateTimeOffset? ExpiresAt,
    string Info,
    string LastError,
    string MachineCode,
    string Enforcement,
    string ConfiguredEnforcement,
    bool IsLocalTrial,
    LicenseDocumentDto? Document,
    LicenseTrialDto? Trial);

/// <summary>
/// 授权写操作的统一响应：<b>无论成败都带回服务端当前全量状态</b>。
/// <para>
/// 与 <see cref="UserMutationResult"/> 同思路。失败时前端可立刻用它刷新界面，
/// 不必再发一次 GET，也就不会出现"导入失败了但界面还显示加载中"的中间态。
/// </para>
/// </summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Status">结果码（<c>Ok</c> / <c>Malformed</c> / <c>SignatureInvalid</c> / …）。</param>
/// <param name="Message">面向用户的一句话说明。</param>
/// <param name="License">服务端当前授权状态（全量）。</param>
public sealed record LicenseMutationResult(
    bool Ok,
    string Status,
    string Message,
    LicenseDto License);

/// <summary>导入授权。<c>LicenseText</c> 是从 <c>.lic</c> 文件里复制出来的 Base64 文本（换行与空格会被忽略）。</summary>
public sealed record ImportLicenseRequest(string LicenseText);

/// <summary>
/// 「授权申请文本」：管理员把它发给供应方，对方据此签发绑定本机的授权。
/// </summary>
/// <param name="MachineCode">本机机器码。</param>
/// <param name="MachineName">计算机名（供对方核对）。</param>
/// <param name="RequestedAt">生成时刻（北京时间）。</param>
/// <param name="Text">整段申请文本（可直接复制发送）。</param>
public sealed record LicenseRequestDto(
    string MachineCode,
    string MachineName,
    DateTimeOffset RequestedAt,
    string Text);
