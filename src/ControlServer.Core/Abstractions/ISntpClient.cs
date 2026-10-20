namespace ControlServer.Core.Abstractions;

/// <summary>
/// 一次 SNTP 查询的结果。
/// </summary>
/// <param name="Offset">
/// 服务器时钟与本地时钟的偏移量（服务器 − 本地）。
/// 已按 SNTP 四时间戳公式消除网络往返的一半延迟；正值表示本地钟慢。
/// </param>
/// <param name="RoundTrip">一次往返耗时，用于判断结果可信度（内网通常 &lt; 10ms）。</param>
/// <param name="Stratum">服务器层级；0 表示 kiss-of-death（服务器拒绝服务），必须视为失败。</param>
/// <param name="ServerUtcTime">服务器报出的 UTC 时刻（已转成 <see cref="DateTimeOffset"/>）。</param>
public sealed record SntpQueryResult(
    TimeSpan Offset,
    TimeSpan RoundTrip,
    int Stratum,
    DateTimeOffset ServerUtcTime);

/// <summary>
/// SNTP 客户端端口（纯 UDP + 二进制报文，无平台依赖，因此在 Core）。
/// <para>
/// 抽成端口是为了让 <see cref="Services.TimeSyncService"/> 可以在**不联网**的前提下被测试 ——
/// 时钟校正这种逻辑一旦依赖真实网络就会变成不稳定测试。
/// </para>
/// </summary>
public interface ISntpClient
{
    /// <summary>
    /// 查询指定服务器的时钟偏移。
    /// </summary>
    /// <param name="host">NTP 服务器主机名或 IP。</param>
    /// <param name="timeout">单次超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="TimeoutException">超时无响应。</exception>
    /// <exception cref="InvalidOperationException">响应非法（长度不足 / 未同步 / kiss-of-death）。</exception>
    Task<SntpQueryResult> QueryAsync(
        string host,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
