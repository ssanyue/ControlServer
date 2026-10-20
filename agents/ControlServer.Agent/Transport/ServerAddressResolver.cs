namespace ControlServer.Agent.Transport;

/// <summary>
/// 这一轮用的地址是<b>谁决定的</b>。
/// <para>
/// 🔥 它的存在不是为了给日志分类，而是因为<b>三种来源的正确处置完全相反</b>：
/// 人指定的地址（本机页 / 安装参数）被自动替换掉，等于"我让你连 A，你偷偷去连 B"；
/// 而<b>缓存</b>里的地址（上次注册成功时记下的）被永久信任，就变成了
/// "服务端搬家之后再也不会自己回来"—— 2026-10-20 的现场事故正是后者。
/// </para>
/// </summary>
public enum ServerAddressSource
{
    /// <summary>没有任何可用地址（还没发现到，也没记住过）。</summary>
    None,

    /// <summary>
    /// <b>人</b>指定的地址（本机配置页的 <c>server-override.json</c>，或安装参数写进
    /// <c>Agent:ServerUrl</c>）。<b>永不自动替换</b>。
    /// </summary>
    Pinned,

    /// <summary>
    /// 上次注册成功时<b>记住</b>的地址（<c>credentials.dat</c>）。
    /// <para>它只是缓存 —— 连不上时允许被广播发现的结果替换（见 <see cref="ServerAddressResolver"/>）。</para>
    /// </summary>
    Remembered,

    /// <summary>本轮广播发现的地址。</summary>
    Discovered,
}

/// <summary>「这一轮该连哪个地址」的结论。</summary>
/// <param name="Url">要连的地址；null = 这一轮没得连。</param>
/// <param name="Source">地址的来源（决定后面怎么处置）。</param>
/// <param name="Note">要写进日志/状态的那句话；null = 一切正常，不必说。</param>
public readonly record struct ServerAddressChoice(string? Url, ServerAddressSource Source, string? Note);

/// <summary>
/// 「这一轮该连哪个地址」的<b>纯判定</b>（2026-10-20）。
///
/// <para>
/// 🔥🔥 <b>它修的是什么</b>：客户的机器**先启动、中心后启动**（或者中心换了 IP），
/// 客户端会拿着 <c>credentials.dat</c> 里那次记住的地址一直重试 ——
/// 而<b>它连广播发现都不会走</b>（发现只在"从来没有凭据"时才被调用）。
/// 于是哪怕中心就在同一个局域网里回应广播，客户端也不会去问一句，
/// 只能等人到本机配置页上手动改地址。现场日志（2026-10-20 09:15–09:20）就是这个形状：
/// 十几轮「连接 192.168.3.33:8899 失败」，改地址后<b>一秒</b>就连上了。
/// </para>
///
/// <para>
/// <b>规则（三条，按优先级）</b>：
/// </para>
/// <list type="number">
///   <item>
///     <b>人指定的地址不替换</b>（本机页覆盖 / 安装时的 <c>Agent:ServerUrl</c>）。
///     同一个局域网里可能有两套中心，替换等于把机器悄悄挪到别人的中心上 ——
///     那种错误比"连不上"贵得多。这种情况只<b>记一条线索</b>（局域网里有哪个中心），
///     让人自己决定要不要改。
///   </item>
///   <item>
///     <b>缓存地址先信一次</b>：如果它上一次还能用，第一轮直接连它 ——
///     正常情况下这一轮就成功了，省掉 9 秒广播（装机 99% 的时间都是这个情形）。
///   </item>
///   <item>
///     🔥 <b>连续 <see cref="DiscoveryFallbackAfterFailures"/> 轮连不上就广播</b>：
///     发现到<b>不同</b>的地址就换过去（换址后重新注册，凭据属于旧中心，本来也用不了）；
///     发现到的还是它、或者什么都没发现，就继续用它 —— 至少下次还能试。
///   </item>
/// </list>
///
/// <para>
/// ⚠️ <b>为什么阈值是 2 而不是 1</b>：一轮失败可能只是"中心正好在重启"（几秒的窗口）。
/// 等第二轮再说，能避免"中心抖一下，全网客户机一起改地址"这种自找的混乱；
/// 而 2 轮的代价只是多等一个退避（1～2 秒），相对"永远回不来"可以忽略。
/// </para>
/// </summary>
public static class ServerAddressResolver
{
    /// <summary>记住的地址连续失败多少轮之后，才允许改用广播发现的结果。</summary>
    public const int DiscoveryFallbackAfterFailures = 2;

    /// <summary>
    /// 这一轮要不要真的去广播。
    /// <para>
    /// 没有地址时必须广播（这就是"装完就自己出现"）；有地址时只在连续失败之后才广播 ——
    /// 每轮都广播会让"中心只是慢了一拍"变成"9 秒的等待"，而那段等待毫无信息量。
    /// </para>
    /// </summary>
    public static bool WantsDiscovery(string? rememberedUrl, int consecutiveFailures, bool discoveryAllowed)
    {
        if (!discoveryAllowed) return false;

        // 负数/0 都当"还没失败过"：调用方忘了初始化也不该突然跳过缓存地址。
        if (consecutiveFailures < 0) consecutiveFailures = 0;

        return string.IsNullOrWhiteSpace(rememberedUrl)
            || consecutiveFailures >= DiscoveryFallbackAfterFailures;
    }

    /// <summary>
    /// 挑这一轮的地址。<b>绝不抛</b>；没得连时返回 <see cref="ServerAddressSource.None"/>。
    /// </summary>
    /// <param name="rememberedUrl">凭据里记住的地址（可能为 null）。</param>
    /// <param name="discoveredUrl">本轮广播发现到的地址（没发现到为 null）。</param>
    /// <param name="consecutiveFailures">记住的地址已经连续失败了几轮。</param>
    /// <param name="discoveryAllowed">是否允许广播发现（<c>Agent:AllowBroadcastDiscovery</c>）。</param>
    public static ServerAddressChoice Resolve(
        string? rememberedUrl,
        string? discoveredUrl,
        int consecutiveFailures,
        bool discoveryAllowed)
    {
        if (consecutiveFailures < 0) consecutiveFailures = 0;

        var discovered = discoveryAllowed ? Trim(discoveredUrl) : null;

        if (string.IsNullOrWhiteSpace(rememberedUrl))
        {
            return discovered is null
                ? new ServerAddressChoice(null, ServerAddressSource.None, "未发现中心服务")
                : new ServerAddressChoice(discovered, ServerAddressSource.Discovered, null);
        }

        var remembered = rememberedUrl.Trim();

        // 还没失败够，或者压根不许广播：照旧用记住的那个。
        if (consecutiveFailures < DiscoveryFallbackAfterFailures || !discoveryAllowed)
        {
            return new ServerAddressChoice(remembered, ServerAddressSource.Remembered, null);
        }

        if (discovered is not null && !SameAddress(discovered, remembered))
        {
            return new ServerAddressChoice(
                discovered,
                ServerAddressSource.Discovered,
                $"记住的地址 {remembered} 连续 {consecutiveFailures} 轮连不上，改用广播发现的 {discovered}"
                + "（凭据属于旧中心，将重新注册）。");
        }

        // 发现的还是同一个地址（说明它其实在应答，失败另有原因），或者什么都没发现。
        return new ServerAddressChoice(
            remembered,
            ServerAddressSource.Remembered,
            discovered is null
                // ⚠️ 这句话里**不写失败次数**：调用方按文案去重（同一句只记一次），
                //    带上会变的数字，去重就永远不成立 —— 中心停一夜就是一千多行同样的日志。
                ? $"记住的地址 {remembered} 连续多轮连不上，广播也没有找到任何中心，继续重试。"
                : null);
    }

    /// <summary>
    /// 两个地址是不是<b>同一个中心</b>（比协议 + 主机 + 端口，忽略大小写与末尾斜杠）。
    /// <para>
    /// ⚠️ 用 <see cref="Uri"/> 比而不是直接 <c>string.Equals</c>：发现的地址是拼出来的
    /// （<c>http://10.0.0.5:8899</c>），而人可能填成 <c>HTTP://10.0.0.5:8899/</c> ——
    /// 字符串比较会判成"两个不同中心"，于是每轮都"换一次地址"、每轮都重新注册。
    /// </para>
    /// </summary>
    public static bool SameAddress(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;

        var a = Authority(left);
        var b = Authority(right);

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string Authority(string url)
    {
        var trimmed = url.Trim();

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.GetLeftPart(UriPartial.Authority)
            : trimmed.TrimEnd('/');
    }

    private static string? Trim(string? url)
        => string.IsNullOrWhiteSpace(url) ? null : url.Trim();
}
