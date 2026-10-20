/**
 * <b>播放延迟预算</b>（M7.16）—— 画面与声音"排期"时故意落后多少。
 *
 * <p>
 * 🔥 <b>它为什么值得单独一个模块</b>：这个数<b>同时</b>决定两件互相打架的事 ——
 * <list type="bullet">
 *   <item><b>端到端延迟</b>：每一帧都在这个延迟之后才上屏，它直接加在"手到眼"的那条路上；</item>
 *   <item><b>能不能吃住抖动</b>：太小了，一抖就"该显示的帧还没到"，画面一顿一顿，声音断字。</item>
 * </list>
 * 而它<b>没有任何界面证据</b>：调错了只表现为"有点卡"或"有点慢"，两端日志全干净。
 * 所以判据必须写在能被断言的地方。
 * </p>
 *
 * <p>
 * 🔥🔥 <b>M7.16 之前它是一个写死的 60 ms</b>。60 的来处是"局域网的抖动是个位数毫秒"，
 * 也就是<b>按最坏的路况给的固定缓冲</b>。问题是它与帧率无关：
 * 60 fps 下 60 ms 是 <b>3.6 帧</b>的积压，而缓冲真正要买的只有"两帧" ——
 * 剩下的 1.6 帧（≈27 ms）是<b>白送的延迟</b>；12 fps 下 60 ms 还不到一帧，它几乎什么也没买。
 * </p>
 *
 * <p>
 * ⇒ 现在按<b>帧间隔</b>算：缓冲买两帧，夹在 [<see cref="LATENCY_MIN_MS"/>,
 * <see cref="LATENCY_MAX_MS"/>] 之间。
 * </p>
 *
 * <table>
 *   <tr><th>目标帧率</th><th>M7.16 前</th><th>M7.16 起</th><th>说明</th></tr>
 *   <tr><td>60</td><td>60 ms</td><td><b>33 ms</b></td><td>2 帧余量，省下 27 ms</td></tr>
 *   <tr><td>45</td><td>60 ms</td><td><b>44 ms</b></td><td>同上</td></tr>
 *   <tr><td>30</td><td>60 ms</td><td><b>60 ms</b></td><td>2 帧 = 67 ms ⇒ 夹回上限，与以前一致</td></tr>
 *   <tr><td>12 及更低</td><td>60 ms</td><td><b>60 ms</b></td><td>不到一帧，不变</td></tr>
 * </table>
 *
 * <p>
 * ⚠️ <b>低帧率为什么不跟着降</b>：12 fps 的帧间隔是 83 ms，延迟给 20 ms 等于"一帧都不缓冲" ——
 * 那不是低延迟，那是每帧都迟到。**只有高帧率才有"多余的缓冲"可省**。
 * </p>
 *
 * <p>
 * 🔥 <b>声音必须用同一个数</b>：A/V 对齐的唯一依据就是"两条流按同一个延迟排期"
 * （见 `RemoteAudioPlayer` 里 `targetPerf + 延迟` 那一行）。
 * 所以这里是一个**进程级单值**，两条流都实时读它 —— 而不是各存一份。
 * </p>
 */

/** 缓冲的下限（毫秒）。再小就买不到任何东西（一次解码抖动就掉帧）。 */
export const LATENCY_MIN_MS = 20

/** 缓冲的上限（毫秒）。M7.16 之前的固定值，现在只在低帧率下才会用到。 */
export const LATENCY_MAX_MS = 60

/**
 * 缓冲买**几帧**的余量。
 *
 * <p>
 * 🔥 取 2：一帧不够（帧恰好按间隔到达时才刚好来得及，一点抖动就掉），
 * 三帧以上就纯粹是延迟。2 帧是"能吃掉一个完整周期的乱序"的最小值。
 * </p>
 */
const BUFFER_FRAMES = 2

/**
 * 按目标帧率算出该用多少播放延迟。
 *
 * @param targetFps 会话的目标帧率（`null` / 非法值 ⇒ 用上限，即 M7.16 之前的行为）。
 */
export function chooseLatencyMs(targetFps: number | null | undefined): number {
  // ⚠️ 拿不到帧率时**必须回到上限**而不是下限：不知道路况就别赌，慢一点比"一顿一顿"好。
  if (
    targetFps === null ||
    targetFps === undefined ||
    !Number.isFinite(targetFps) ||
    targetFps <= 0
  ) {
    return LATENCY_MAX_MS
  }

  const frameIntervalMs = 1000 / targetFps
  const want = frameIntervalMs * BUFFER_FRAMES

  return Math.round(Math.min(LATENCY_MAX_MS, Math.max(LATENCY_MIN_MS, want)))
}

/**
 * 当前生效的延迟（毫秒）。
 *
 * ⚠️ 它<b>故意是可变的模块级状态</b>，而不是传参：
 * 视频那条路在 `RemoteVideoBuffer` 里、声音那条路在 `RemoteAudioPlayer` 里，
 * 两者相隔几百行、由不同的回调驱动。传参会很快出现"一个改了、另一个没改"——
 * 而那正是 A/V 慢慢错开的原因（症状是"声音比嘴慢半拍"，很难归因）。
 */
let currentLatencyMs = LATENCY_MAX_MS

/** 读当前延迟。两条流每一帧都要现读（不是缓存下来的副本）。 */
export function getPlaybackLatencyMs(): number {
  return currentLatencyMs
}

/**
 * 设当前延迟。
 *
 * @param targetFps 会话的目标帧率；`null` ⇒ 回到上限。
 * @returns 实际生效的值（夹过）。
 */
export function setPlaybackLatencyForFps(targetFps: number | null | undefined): number {
  currentLatencyMs = chooseLatencyMs(targetFps)
  return currentLatencyMs
}

/** 恢复默认（换会话 / 断开时调，免得上一路 60 fps 的选择留在低帧率会话上）。 */
export function resetPlaybackLatency(): void {
  currentLatencyMs = LATENCY_MAX_MS
}
