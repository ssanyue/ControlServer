namespace ControlServer.Core.Licensing;

/// <summary>
/// 一次授权判定的结果（对外视图的 Core 侧形态）。
/// </summary>
/// <param name="Status">状态码，取值见 <see cref="LicenseStatus"/>。</param>
/// <param name="Kind">授权类型（<c>full</c> / <c>trial</c> / <c>temp</c>）；无文件时为空串。</param>
/// <param name="RemainingDays">剩余天数；<c>-1</c> = 未知，<c>99999</c> = 永久，已过期为 <c>0</c>。</param>
/// <param name="ExpiresAt">到期时刻（北京时间墙钟）；永久或未知为 <c>null</c>。</param>
/// <param name="Info">面向用户的一句话摘要（成功时描述剩余期限，失败时说明原因）。</param>
/// <param name="LastError">失败原因；成功时为空串。</param>
public sealed record LicenseState(
    LicenseStatus Status,
    string Kind,
    int RemainingDays,
    DateTimeOffset? ExpiresAt,
    string Info,
    string LastError)
{
    /// <summary>永久授权的剩余天数哨兵值（与参照实现一致）。</summary>
    public const int PermanentRemainingDays = 99999;

    /// <summary>剩余天数未知的哨兵值。</summary>
    public const int UnknownRemainingDays = -1;

    /// <summary>
    /// 设备当前<b>可用</b>（正式授权生效中，或仍在试用期内）。
    ///
    /// <para>
    /// ⚠️ <b>这里刻意写成白名单枚举，而不是 <c>Status &gt;= 1</c></b>。
    /// 参照实现在导入流程里用了范围比较，于是"已过期(2)"与"错误(3)"也通过了成功判定 ——
    /// 用户导入一份过期授权后，门控直接关闭、进入主界面，<b>且没有任何提示</b>。
    /// 这个 bug 的症状是"一切看起来正常，但功能本不该可用"，比直接报错危险得多。
    /// </para>
    /// <para>任何"是否放行"的判断都必须经过本属性，禁止在别处重复写比较逻辑。</para>
    /// </summary>
    public bool IsUsable => Status is LicenseStatus.Authorized or LicenseStatus.Trial;

    /// <summary>是否为永久授权（无到期时间且不是试用）。</summary>
    public bool IsPerpetual => IsUsable && RemainingDays == PermanentRemainingDays;

    /// <summary>
    /// 当前生效的是<b>本机自动试用</b>（而不是厂商签发的授权文件）。
    /// <para>前端据此把文案写成"本机试用"并提示"导入正式授权"，避免用户以为已经买过。</para>
    /// </summary>
    public bool IsLocalTrial { get; init; }

    /// <summary>状态的中文显示名（仅用于界面，不参与任何比较）。</summary>
    public string StatusDisplayName => Status switch
    {
        LicenseStatus.NotAuthorized => "未授权",
        LicenseStatus.Authorized => "已授权",
        LicenseStatus.Expired => "已过期",
        LicenseStatus.Error => "授权异常",
        LicenseStatus.Trial => "试用中",
        _ => Status.ToString(),
    };

    /// <summary>文件存在但读不成合法授权（解析失败 / 校验异常）时的统一构造。</summary>
    public static LicenseState Damaged(string reason)
        => new(LicenseStatus.Error, string.Empty, UnknownRemainingDays, null, reason, reason);
}
