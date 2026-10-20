namespace ControlServer.Infrastructure.Persistence.Entities;

using ControlServer.Core.Services;

/// <summary>键值设置。M2 会承载外观、安全、时间等全部可配置项。</summary>
public sealed class Setting
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// 更新时刻（**北京时间**）。
    /// <para>
    /// ⚠️ 与 <see cref="AppUser.CreatedAt"/> 同理：全应用时间口径统一为 UTC+8
    /// （见 <c>BeijingTime</c>），默认值不能取 <c>DateTimeOffset.UtcNow</c> ——
    /// 否则"忘了显式赋值"就会静默写进一个差 8 小时的时间戳。
    /// </para>
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; } = BeijingTime.Now;
}
