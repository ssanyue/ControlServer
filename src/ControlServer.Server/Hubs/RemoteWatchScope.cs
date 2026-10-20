namespace ControlServer.Server.Hubs;

/// <summary>
/// <b>"这条连接有没有资格操作这一路"的唯一判断点</b>（M5.2 的归属校验）。
///
/// <para>
/// 🔥 <b>为什么这么小的一件事要单独有个类型</b>：它是"直接操作别人键鼠"这条路上
/// 唯一的一道归属关卡，而它判错的两个方向<b>代价不对称</b>：
/// </para>
/// <list type="bullet">
///   <item>判<b>松</b>了 —— 一个早就刷新过、切走过的页面还能往被控机送鼠标事件。
///         它手上的坐标来自一张<b>已经不在屏幕上的旧画面</b>，
///         而坐在被控机前的人会看到鼠标自己动、完全不知道谁在动它；</item>
///   <item>判<b>紧</b>了 —— 正常用户点下去没反应，而"没反应"在屏幕上没有任何线索
///         （这正是 <c>RemoteInputAckDto</c> 存在的理由）。</item>
/// </list>
///
/// <para>
/// 🔥 <b>它<b>不是</b>"控制权"这个概念</b>：本方案<b>没有</b>独占控制权 ——
/// 多人同时看一路时，<b>任何一个正在看这一路的人</b>都有资格操作，
/// 这是刻意的（开关是<b>会话级</b>的：一个人打开，所有正在看的人都获得能力）。
/// 所以这里的"有资格" = "你此刻正在收这一路的画面"，仅此而已。
/// </para>
///
/// <para>
/// ⚠️ <b>它现在只认"单路"</b>（一条连接同时只看一路 ——
/// <c>RemoteHub</c> 里那个 <c>Context.Items</c> 存的是单值）。
/// 将来真要做"一格同时看多路"，改动点是这里 + 那个存储形状，
/// 而不是去每个调用处各加一次判断。
/// </para>
/// </summary>
public static class RemoteWatchScope
{
    /// <summary>
    /// <paramref name="watched"/>（这条连接当前在看的那一路）
    /// 是不是就是 <paramref name="sessionId"/>（它现在想操作的那一路）。
    /// </summary>
    /// <remarks>
    /// 🔥 <b>用 <see cref="StringComparison.Ordinal"/></b>：会话 Id 是机器生成的
    /// （<c>HHmmss-xxxxxxxx</c>），大小写敏感。用区域相关的比较会引入
    /// "在某些区域设置下两个不同的 Id 被当成同一个" —— 这类偏差只在别人的机器上出现。
    /// </remarks>
    public static bool IsWatching(string? watched, string? sessionId)
        // ⚠️ 请求里连 Id 都没有时永远不算"在看"。
        //    "空 == 空" 不该判成匹配：一个从没 Join 过的连接，
        //    与一个 Join 了空 Id 的连接，都不该拿到操作资格。
        => !string.IsNullOrWhiteSpace(sessionId)
           && string.Equals(watched, sessionId, StringComparison.Ordinal);
}
