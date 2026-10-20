using ControlServer.Core.Services;

namespace ControlServer.LicenseTool;

/// <summary>
/// 取"联网校对时间"——授权文件里 <c>ivt</c> 字段的来源。
///
/// <para>
/// <b>为什么签发工具必须联网</b>：<c>ivt</c> 堵的是"改文件延长授权"这条路 ——
/// 手工拼出来的文件即使签名合法，也会因为缺少可信的联网记录而被服务端拒绝。
/// 如果签发时随手用本机时钟填这个字段，那它就从"可信锚点"退化成"又一个可以被改的数"，
/// 整条防线只剩签名一层。
/// </para>
///
/// <para>
/// <b>复用服务端同一条 SNTP 通道</b>（<see cref="SntpClient"/> + 同一份默认服务器列表），
/// 不另写第二套取时逻辑：两边口径一致，才不会出现"服务端认为现在几点"与
/// "签发时认为现在几点"打架。
/// </para>
/// </summary>
internal static class NetworkClock
{
    /// <summary>单个服务器的查询超时。签发是人坐在电脑前等，3 秒足够且不会让人以为卡死。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 按默认服务器列表依次尝试，返回第一个成功响应的<b>服务器时间</b>（而不是本机时间）。
    /// <para>全部失败返回 <c>null</c>，由调用方决定是报错退出还是（显式选择离线时）降级。</para>
    /// </summary>
    public static async Task<DateTimeOffset?> TryQueryAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var client = new SntpClient();

        foreach (var server in TimeSyncSettings.DefaultNtpServers)
        {
            try
            {
                var result = await client.QueryAsync(server, timeout, cancellationToken).ConfigureAwait(false);

                ConsoleOut.Ok(
                    $"已取得网络时间：{BeijingInput.Describe(result.ServerUtcTime)}"
                    + $"（来源 {server}，往返 {result.RoundTrip.TotalMilliseconds:F0} ms）");

                return result.ServerUtcTime;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单个服务器失败不是失败 —— 多服务器列表的意义就在于此。
                ConsoleOut.Hint($"{server} 取时失败：{ex.Message}");
            }
        }

        return null;
    }
}
