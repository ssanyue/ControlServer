namespace ControlServer.Agent.Tasks;

/// <summary>
/// <b>抓屏轮询的节奏</b>（M5.4-c② 步 8）：桌面静止时把轮询退避下来，一动就立刻回到目标帧率。
///
/// <para>
/// 🔥 <b>为什么这件事值得单独立一个类型</b>：退避的判据（"静止多久"）<b>只能靠真的等</b>
/// 才能触发（本仓没有可注入时钟），所以"多久该退到多快"这层算术必须能被单独钉住 ——
/// 否则每验证一个边界都要睡好几秒，而边界恰恰是这里最容易写错的地方
/// （阈值 <c>&gt;=</c> vs <c>&gt;</c>、下限与比例打架、周期无上限）。
/// </para>
///
/// <para>
/// 🔥🔥 <b>两条铁律</b>：
/// </para>
/// <list type="number">
///   <item>
///     <b>不许把"降帧"做成"停止"</b>（<c>REF-M5 §14.14</c> 刚因为"静止画面被 Sweep 判死"翻过车）。
///     本类用<b>两个</b>各自独立的保险守住这一条：
///     <b>①</b> <see cref="MaxIdlePeriod"/> —— 退避后的周期永远不超过 1 秒
///     （由 <see cref="MinIdleFps"/> 保证，并由用例钉住），所以抓屏轮询<b>一直还在跑</b>；
///     <b>②</b> 被控端每秒一次的计数上报（<c>RemoteSessionCoordinator</c> 里那条周期上报）
///     —— 中心一直听得见它。
///     <para>
///     ⚠️⚠️ <b>这两条必须一起看</b>：优化掉任何一条，展厅里"定格在一张图上"的机器
///     就会被当成死机器。而那台机器<b>正是本步要救的</b>。
///     </para>
///   </item>
///   <item>
///     <b>退避幅度与 <c>TargetFps</c> 成比例，不许写死"2 fps"</b>：目标 60 fps 的机器退到 2 fps
///     与目标 12 fps 的机器退到 2 fps，激进程度差 5 倍。会话参数决定"多快"，本类只决定"怎么退"。
///   </item>
/// </list>
/// </summary>
public static class RemoteStreamPacing
{
    /// <summary>
    /// 桌面静止<b>多久</b>之后才开始退避。
    ///
    /// <para>
    /// ⚠️ <b>不能太小</b>：人看一份文档时每敲一个字才动一下，2 秒以内不动的间隔很常见 ——
    /// 一停就退避，会让"打字时画面一顿一顿的"，而那比多烧一点 CPU 更让人用不下去。
    /// </para>
    /// <para>
    /// ⚠️ <b>也不能太大</b>：它的另一面是"用户停手之后多久省下这份 CPU"，
    /// 而展厅里那种一停就是几小时的场景，2 秒已经足够便宜。
    /// </para>
    /// </summary>
    public static readonly TimeSpan IdleBackoffAfter = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 退避后的帧率 = 目标帧率 ÷ 它（<c>12 ⇒ 2</c>、<c>60 ⇒ 10</c>）。
    /// <para>
    /// 🔥 <b>它守的是"比例"这件事本身</b>：写死成某个绝对帧率，
    /// 在 12 fps 的会话上是"降到六分之一"、在 60 fps 的会话上就是"降到三十分之一"。
    /// </para>
    /// </summary>
    public const int IdleBackoffRatio = 6;

    /// <summary>
    /// 退避后的帧率<b>下限</b>。再往下就离"停止"太近了（见类注释里的两条铁律）。
    /// </summary>
    public const int MinIdleFps = 2;

    /// <summary>
    /// 退避后<b>周期</b>的上限：任何配置下都必须一秒内醒一次。
    /// <para>
    /// 🔥 这一条与 <see cref="MinIdleFps"/> 是<b>两个方向的夹子</b>，都需要：
    /// 帧率下限管"别退太狠"，周期上限管"目标帧率本来就极低时也别睡过头"。
    /// </para>
    /// <para>
    /// ⚠️ <b>它是一条「契约」，不是一个运行时分支</b>（M5.5 挂账清理时确认）：
    /// 在 <see cref="MinIdleFps"/> <c>= 2</c> 的前提下，退避周期恒 ≤ 500 ms，
    /// 所以代码里那句 <c>Math.Min(period, MaxIdlePeriod)</c> <b>永远不触发</b>。
    /// 一句永远为假的守卫比没有更坏 —— 它让人以为"下限被改坏也有人兜着"。
    /// 现在这条性质由 <c>RemoteStreamPacingTests.The_polling_period_is_capped_so_it_never_stops</c>
    /// 直接钉住：把 <see cref="MinIdleFps"/> 改小，那条用例立刻红。
    /// </para>
    /// </summary>
    public static readonly TimeSpan MaxIdlePeriod = TimeSpan.FromSeconds(1);

    /// <summary>这一次轮询该间隔多久。</summary>
    /// <param name="targetFps">会话的目标帧率（已 clamp 过）。</param>
    /// <param name="idleFor">桌面已经静止了多久。</param>
    public static TimeSpan PollPeriod(int targetFps, TimeSpan idleFor)
    {
        var target = TargetPeriod(targetFps);

        // 桌面刚动过（或还没静止够）⇒ 一点折扣都不打。
        if (idleFor < IdleBackoffAfter) return target;

        var period = TimeSpan.FromSeconds(1.0 / BackoffFps(targetFps));

        // 🔥 **这里刻意不再夹一次 <see cref="MaxIdlePeriod"/>**：在 `MinIdleFps = 2`
        //    的前提下本方法的返回值恒 ≤ 500 ms，那句夹取是一行永远不会执行的代码。
        //    守卫的位置换成了上面那条用例 —— 它能真的失败，而这句话不会。
        return period;
    }

    /// <summary>退避后按多少帧率抓屏（只用于日志与人话）。</summary>
    /// <param name="targetFps">会话的目标帧率（已 clamp 过）。</param>
    public static int BackoffFps(int targetFps)
    {
        var proportional = Math.Max(MinIdleFps, targetFps / IdleBackoffRatio);

        // ⚠️ **必须夹一次上限**：目标本来就是 1 fps 时，`Max(2, 0) = 2` 会让
        //    "退避后比不退避还快" —— 一条只会更烧 CPU、且与本步意图完全相反的曲线。
        return Math.Min(targetFps, proportional);
    }

    /// <summary>
    /// <b>"这一轮没拿到新画面"之后还要等多久</b>（M7.4 §12.3）。
    ///
    /// <para>
    /// 🔥 <b>它守的是"一次错过只付一次钱"</b>：<c>AcquireNextFrame</c> 自己就会等
    /// （本仓取 15 ms），再整整睡一个周期就是 <b>15 + 16.7 ≈ 32 ms 一帧都不产</b>。
    /// 60 fps 下这种错过很常见（与桌面合成同频采样，抢不到就是常态）：
    /// 实测 5 秒 278 轮里 22 轮属于这种 —— 687 ms、13.7% 的窗口，全花在重复等待上。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>它不是"取消节流"</b>：返回值仍然把这一轮已经花掉的时间算进周期，
    /// 所以真正静止（退避到 100 ms）时照样按退避周期等 —— 只是不再"抓屏等一遍、节流再等一遍"。
    /// </para>
    /// </summary>
    /// <param name="pollPeriod">本轮该有的轮询周期（<see cref="PollPeriod"/> 的结果）。</param>
    /// <param name="spentInRound">本轮从开始到现在已经花掉的时间（含抓屏自己的等待）。</param>
    public static TimeSpan DelayAfterMiss(TimeSpan pollPeriod, TimeSpan spentInRound)
    {
        var remaining = pollPeriod - spentInRound;

        // ⚠️ 不能回负数去喂 Task.Delay（它会抛 ArgumentOutOfRange）——
        //    而"这一轮已经超周期"是本方法最常见的返回场景之一。
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// <b>软件 JPEG 这条路（GDI 抓屏 ＋ CPU 编码）的帧率硬上限。</b>
    ///
    /// <para>
    /// 🔥 <b>它由抓屏决定，不由编码决定</b>（2026-10-18 本机实测，i5-8265U / 2160×1440）：
    /// <c>CopyFromScreen</c> 中位 <b>47.1 ms</b>（P95 61.6 ms）⇒ 这条路的物理上限约 <b>21 fps</b>；
    /// 而同一帧的 JPEG q70 编码只要 27.4 ms（1920×1280）—— 抓屏比编码还贵。
    /// </para>
    /// <para>
    /// ⇒ 目标帧率给到 60 时，这条路的循环只会被 47 ms 的抓屏卡住：<b>一帧都不会多</b>，
    /// 而抓屏线程从此满转、被控机（展厅里那台正在跑业务的机器）白烧一个核。
    /// 所以帧率在这里夹一次 —— 这不是保守，是<b>把"做不到"如实说出来</b>。
    /// </para>
    /// </summary>
    public const int SoftJpegMaxFps = 20;

    /// <summary>
    /// 按编码器种类把目标帧率夹到「这条路真能做到」的范围。
    /// </summary>
    /// <param name="targetFps">中心要求的目标帧率（已按 1–60 夹过）。</param>
    /// <param name="hardwareEncode">
    /// 这一路的编码器是不是 GPU 硬编（<c>ISurfaceFrameEncoder</c>）。
    /// <b>是</b> ⇒ 原样返回；<b>否</b> ⇒ 夹到 <see cref="SoftJpegMaxFps"/>。
    /// </param>
    /// <remarks>
    /// ⚠️ <b>判据是"编码器种类"，不是"这次发送失败了"</b>（与 M7.3 那条同源）：
    /// 拿一次超时去反推"这台机器跑不动 60"，会把一次网络抖动永久记成硬件不行。
    /// </remarks>
    public static int ApplyEncoderCap(int targetFps, bool hardwareEncode)
        => hardwareEncode ? targetFps : Math.Min(targetFps, SoftJpegMaxFps);

    /// <summary>目标帧率对应的周期（<b>唯一</b>一处换算，别再在调用点写 <c>1.0 / fps</c>）。</summary>
    private static TimeSpan TargetPeriod(int targetFps)
        => TimeSpan.FromSeconds(1.0 / Math.Max(1, targetFps));
}
