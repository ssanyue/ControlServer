namespace ControlServer.Core.Abstractions;

/// <summary>
/// 授权体系需要的「可信时间」。
///
/// <para>
/// <b>为什么不能直接用系统时间</b>：授权里的 <c>ivt</c>（联网校对时间）就是用来
/// 挡住"把系统时钟调回去以延长授权"的。如果签发/导入时的锚点也取自系统钟，
/// 那么攻击者只要回拨时钟、再重新导入一次，就能把锚点一起调回去，防线自相矛盾。
/// 因此锚点必须来自<b>联网获得的</b>时间。
/// </para>
///
/// <para>
/// 实现直接复用既有的时钟校准设施（<c>TimeSyncService</c> + SNTP 三源），
/// <b>不新增第二条授时通道</b> —— 两套授时意味着两套失败模式，而且会互相打架。
/// </para>
/// </summary>
public interface ILicenseTimeSource
{
    /// <summary>
    /// 最近一次成功联网校准得到的<b>网络 UTC 时刻</b>；从未成功过为 <c>null</c>。
    /// <para>这里的"网络时刻"指 NTP 服务器报出的时刻，而不是"本机在那一刻的读数"。</para>
    /// </summary>
    DateTimeOffset? LastNetworkUtc { get; }

    /// <summary>
    /// 主动获取一次联网时间（可能触发一次 NTP 查询）。
    /// <para>失败返回 <c>null</c>，<b>不抛异常</b> —— 拿不到网络时间是现场常见情形（内网无外网出口），
    /// 应当由调用方转成一句人话提示，而不是一个 500。</para>
    /// </summary>
    Task<DateTimeOffset?> TryGetNetworkTimeAsync(CancellationToken cancellationToken = default);
}
