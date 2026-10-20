namespace ControlServer.Agent.Remote;

/// <summary>
/// <b>一路 start 指令到本机时该怎么处置</b>（<see cref="RemoteSessionCoordinator.StartAsync"/> 的判据）。
/// </summary>
public enum RemoteSessionAdmissionDecision
{
    /// <summary>本机当前没有在推的会话 ⇒ 直接开。</summary>
    Start,

    /// <summary>本机在推的<b>就是这一路</b> ⇒ 重复指令，按幂等处理，什么都不做。</summary>
    Duplicate,

    /// <summary>
    /// 本机在推的是<b>另一路</b> ⇒ 那一路上中心已经不要了（详见
    /// <see cref="RemoteSessionAdmission"/>）⇒ 停掉旧的、起新的。
    /// </summary>
    SupersedeAndStart,
}

/// <summary>
/// <b>"M5.1 只允许一路"到底该怎么落地</b> —— 一个纯判据，单独钉住。
///
/// <para>
/// 🔥🔥 <b>这件事的现场代价必须写下来</b>（2026-10-19，被控端 DESKTOP-2DFUKBT）：
/// </para>
/// <code>
/// 09:18:22  中心：会话 091729 已结束（15 秒没收到任何帧与状态）
/// 09:18:34  本机：已有会话「091729」在推流 → 拒绝新会话
/// 09:18:55  同上
/// 09:19:18  同上
/// 09:19:45  同上
/// 09:28:46  才终于开起来（本机自己的空闲超时把旧那路收了）
/// </code>
/// <para>
/// 那十分钟里页面上写的是"建会话后 20 秒内没有收到被控端的任何状态上报
/// （最常见的原因：这台机器没人登录 / 托盘没在跑 / 客户端版本过旧）" ——
/// <b>三条都不对</b>：机器在线、托盘在跑、版本也认得这条指令，
/// 它只是<b>坚定地拒绝</b>。用户看到的则是"连不上 / 刚连上就断"。
/// </para>
///
/// <para>
/// 🔥 <b>为什么"拒绝新的那一路"是错的判据</b>：中心对<b>每台机器</b>也只允许一路
/// （<c>RemoteSessionRegistry</c> 的 <c>SessionBusy</c>），所以一个**不同 id** 的 start
/// 能到达本机，只有一个解释 —— <b>中心那边已经把旧会话关账了</b>
/// （掉线超时 / 页面关了 / 管理员结束），而那条 stop 没送到。
/// 也就是说：旧的那一路对本机是"在推"，<b>对中心已经是死的</b>。
/// 这时候唯一正确的动作是<b>以中心的指令为准</b>：停掉旧的、起新的。
/// </para>
///
/// <para>
/// ⚠️ <b>为什么不能"先拒绝，等中心发 stop"</b>：那条 stop 恰恰就是丢了的那个东西 ——
/// 让它"再发一次"等于把可用性押在一条已经证明会丢的消息上。
/// 而反过来（接管）不会让任何一路多跑：旧的那一路<b>先被停掉</b>才开始新的。
/// </para>
///
/// <para>
/// ⚠️ <b>为什么不能"无脑重建"</b>（同一路 id 也停掉重开）：中心在重连后可能重发同一条 start，
/// 那时重建会把刚建好的抓屏器 / 硬编 MFT 拆掉重来，用户看到的是"画面黑一下、再等首帧"。
/// 同 id 必须是<b>幂等</b>的。
/// </para>
/// </summary>
public static class RemoteSessionAdmission
{
    /// <summary>本机当前在推 <paramref name="activeSessionId"/> 时，来了一个 <paramref name="requestedSessionId"/>，怎么办。</summary>
    /// <param name="activeSessionId">
    /// 本机正在推的会话 id（<c>null</c> / 空白 = 本机没在推）。
    /// </param>
    /// <param name="requestedSessionId">这一条 start 要开的会话 id。</param>
    public static RemoteSessionAdmissionDecision Decide(
        string? activeSessionId,
        string? requestedSessionId)
    {
        if (string.IsNullOrWhiteSpace(activeSessionId)) return RemoteSessionAdmissionDecision.Start;

        // ⚠️ 用 Ordinal 比：会话 id 是中心生成的机器标识，不是给人看的文本，
        //    任何"文化相关"的比较在这里都只会带来意外。
        return string.Equals(activeSessionId, requestedSessionId, StringComparison.Ordinal)
            ? RemoteSessionAdmissionDecision.Duplicate
            : RemoteSessionAdmissionDecision.SupersedeAndStart;
    }

    /// <summary>接管那一刻写进日志的一句话（现场排查时它是"为什么换了会话"的唯一线索）。</summary>
    public static string DescribeSupersede(string? oldSessionId, string? newSessionId)
        => $"本机还挂着会话「{oldSessionId}」，而中心已经不再认它（于是新的 start 被发了过来）"
           + $"—— 停掉旧的、由新会话「{newSessionId}」接管。";
}
