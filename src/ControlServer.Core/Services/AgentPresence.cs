using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Contracts;

namespace ControlServer.Core.Services;

/// <summary>
/// 客户端在线状态的**纯函数**判定（M4.1）。
/// <para>
/// 抽成静态纯函数是为了能被单测钉死：状态判定牵涉"心跳间隔 × 阈值"这组时间参数，
/// 一旦算错，页面上表现出来的只是"某台机器偶尔变灰"，几乎不可能靠肉眼定位。
/// </para>
/// <para>
/// <b>不做 ping、不做 TCP 探测。</b> 那些会把"网络抖一下"变成"设备离线"的假告警。
/// 唯一判据是"最后一次心跳距今多久"。
/// </para>
/// </summary>
public static class AgentPresence
{
    /// <summary>
    /// 判定某台客户机当前处于什么状态。
    /// </summary>
    /// <param name="lastSeenAt">
    /// 最后一次心跳时间；<b>null 表示"注册过但从未连上"</b>（区别于"曾经连上过、现在离线"——
    /// 前者要去查客户端装没起来，后者要去查机器/网络）。
    /// </param>
    /// <param name="now">当前时间（北京时间口径）。</param>
    /// <param name="staleAfter">超过此间隔算"不稳定"。</param>
    /// <param name="offlineAfter">超过此间隔算"离线"。</param>
    /// <param name="isBlocked">是否已被管理员拉黑（优先级最高，压过一切时间判定）。</param>
    /// <param name="longConnection">
    /// 🔥 <b>中心此刻是否知道"它还挂着 / 已经不挂着"那条长连接</b>（2026-10-15 新增）。
    /// <list type="bullet">
    ///   <item><c>true</c> —— 它确实连着。时间判定照旧（心跳真的停了仍要变黄/变灰，
    ///         否则一条僵尸连接会把故障藏起来）；</item>
    ///   <item><c>false</c> —— <b>已知这条长连接已经断了</b> ⇒ <b>不可能</b>再判成「在线」，
    ///         最多「不稳定」。这条事实只有中心进程自己知道（内存连接表），
    ///         库里的心跳时间戳给不出来；</item>
    ///   <item><c>null</c> —— 不知道（这台机器在本次运行期里没有任何记录，理论上只剩防御性用途）。</item>
    /// </list>
    /// </param>
    /// <remarks>
    /// 🔥 <b>为什么必须有这个参数</b>（2026-10-15 修的 BUG）：原判定<b>只看心跳时间</b>，
    /// 而客户端断开的那一刻，中心会把"最后心跳"钉成<b>断开时刻</b>
    /// （<c>AgentHostRegistry.MarkDisconnected</c>，为的是给离线计时一个确定起点）——
    /// 于是<b>断开后的头 30 秒页面照样是绿的</b>，而同一时刻下发的闸门
    /// （<c>ConnectionIdOf</c>）已经变成 null ⇒ 现场就是"明明显示在线，一点关机却说当前离线"。
    /// 客户机的重连退避是 1→2→4→…→60 秒，所以这个自相矛盾的窗口能长达几十秒。
    /// <para>
    /// 取舍：连接在不在是中心<b>唯一确定知道的实时事实</b>，它必须压过"时间看起来还很新"。
    /// 代价是客户端短暂断开重连时会出现一闪而过的黄点 —— 那是<b>诚实的</b>：
    /// 那一刻中心确实发不出指令，而"绿着却不能下发"比一个黄点危险得多。
    /// </para>
    /// </remarks>
    public static string Classify(
        DateTimeOffset? lastSeenAt,
        DateTimeOffset now,
        TimeSpan staleAfter,
        TimeSpan offlineAfter,
        bool isBlocked,
        bool? longConnection = null)
    {
        if (isBlocked) return AgentStates.Blocked;
        if (lastSeenAt is null) return AgentStates.Never;

        var age = now - lastSeenAt.Value;

        // 🔥 已知长连接已断 ⇒「在线」这个结论不再成立（见上面的 remarks）。
        if (longConnection == false)
        {
            return age <= offlineAfter ? AgentStates.Unstable : AgentStates.Offline;
        }

        // 客户机时钟快于服务端时 age 为负 —— 按"刚刚还在"处理，不能因为时钟偏差判离线。
        if (age <= staleAfter) return AgentStates.Online;
        if (age <= offlineAfter) return AgentStates.Unstable;
        return AgentStates.Offline;
    }

    /// <summary>
    /// 该状态在页面上是否画"绿点"（活跃）。
    /// </summary>
    public static bool IsActive(string? state) => state == AgentStates.Online || state == AgentStates.Unstable;

    /// <summary>
    /// 把"离线多久"说成人话（列表页第二行展示用）。
    /// <para>刚离线与离线三天，运维要做的事完全不同 —— 后者多半是"这台机器已经拆了"。</para>
    /// </summary>
    public static string DescribeAge(DateTimeOffset? lastSeenAt, DateTimeOffset now)
    {
        if (lastSeenAt is null) return "从未连接";

        var age = now - lastSeenAt.Value;
        if (age < TimeSpan.Zero) return "刚刚";

        if (age.TotalSeconds < 60) return $"{(int)age.TotalSeconds} 秒前";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} 分钟前";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours} 小时前";

        return $"{(int)age.TotalDays} 天前";
    }
}

/// <summary>
/// 密钥的生成与哈希（M4.1）。
/// <para>
/// <b>为什么不用 <c>Sha256PasswordHasher</c>（带盐、多次迭代）</b>：
/// 那套是为"人选的低熵口令"设计的。这里的 agentKey 是<b>32 字节随机数</b>，
/// 熵已经拉满，彩虹表与暴力破解都不成立，加盐只会让"每次认证都要按 key 反查"
/// 变成"每行都要试一次"，把热路径从一次索引命中变成全表扫描。
/// 因此<b>直接 SHA-256（无盐）</b>，配合唯一索引按哈希等值查询（O(log n)）。
/// </para>
/// </summary>
public static class AgentKeys
{
    /// <summary>agentKey 的字节长度（32 字节 = 256 位熵）。</summary>
    public const int SecretBytes = 32;

    /// <summary>
    /// 生成一个新的随机密钥（Base64Url，无 <c>+</c>/<c>/</c>/<c>=</c>，
    /// 可以直接放进 URL 查询串与命令行参数而不必转义）。
    /// </summary>
    public static string NewSecret()
    {
        var buffer = RandomNumberGenerator.GetBytes(SecretBytes);
        return Base64UrlEncode(buffer);
    }

    /// <summary>密钥的 SHA-256 十六进制（小写 64 字符）—— 库里存的就是它。</summary>
    public static string Hash(string secret)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
