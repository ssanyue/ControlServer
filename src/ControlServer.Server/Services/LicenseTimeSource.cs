using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;

namespace ControlServer.Server.Services;

/// <summary>
/// 把既有的时钟校准设施接到授权体系需要的「可信时间」上。
///
/// <para>
/// <b>不新增第二条授时通道</b>：项目已经有 <c>SntpClient</c> + <c>TimeSyncService</c>
/// （三源 NTP + 应用内校正）。再写一套意味着两套失败模式、两个真相源，
/// 而且两者对不上时无从判断谁对。这里只做"翻译"。
/// </para>
///
/// <para>
/// <b>取值策略：能复用就复用，没有才现查</b>。<see cref="TimeSyncService.LastNetworkUtc"/>
/// 如果已经有值（启动校时或周期校时成功过），直接返回它 —— 那同样是"网络说的时刻"，
/// 而且取它不用等网络。只有在从未成功过的情况下才主动发起一次查询。
/// 这样既保留了"锚点必须来自网络"的语义，又不会让每次生成试用都阻塞 2 秒。
/// </para>
///
/// <para>整条链路上任何失败都<b>不抛异常</b> —— 拿不到网络时间是现场常态
/// （内网无外网出口），调用方要的是一句人话提示，不是一个 500。</para>
/// </summary>
public sealed class LicenseTimeSource : ILicenseTimeSource
{
    private readonly TimeSyncService _timeSync;

    public LicenseTimeSource(TimeSyncService timeSync) => _timeSync = timeSync;

    public DateTimeOffset? LastNetworkUtc => _timeSync.LastNetworkUtc;

    public async Task<DateTimeOffset?> TryGetNetworkTimeAsync(CancellationToken cancellationToken = default)
    {
        if (_timeSync.LastNetworkUtc is not null)
        {
            return _timeSync.LastNetworkUtc;
        }

        try
        {
            await _timeSync.SyncAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // SyncAsync 本身设计为不抛（内部逐服务器 try/catch），这里只是防御性兜底。
        }

        return _timeSync.LastNetworkUtc;
    }
}
