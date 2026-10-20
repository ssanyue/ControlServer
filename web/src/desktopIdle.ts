/**
 * **「被控端桌面静止中」那一格的三态**（M5.4-c② 步 7）。
 *
 * 🔥 页面上一帧都不来时，原来只有两种话：**"画面卡住"** 与 **"一切正常"** ——
 * 而展厅里定格在一张图上的那台机器**两种都不是**（它好好的，只是没人动它）。
 * 被控端一直知道这件事（抓屏器回"没有新帧"就是答案），之前它只是不说。
 *
 * 所以这一格必须把三种情况**分开画**：
 *
 * - `null` ⇒ `—`（这台机器不提这件事：旧客户端 / 还没上报过）—— **不是 0**；
 * - `0` ⇒ 画面在变 —— "刚动过"是一个**答案**；
 * - `12000` ⇒ 已静止 12 秒。
 *
 * ⚠️ 把 `null` 画成 `0` 是这里最容易犯的错，而它不会报任何错：
 * 每台旧客户端都会统一显示"画面在变"，看起来完全正常。
 */

/**
 * 超过这个时长才值得提示一句"桌面静止中"。
 *
 * 🔥 **不能取 0**：画面在动的时候，两次读取之间本来就隔着一次抓屏周期
 * （12 fps ⇒ 83 毫秒；被控端静止退避后更长），取 0 会让提示在画面上每秒闪好几次。
 * 取 2 秒也给"用户刚停手、还没来得及动"留了余量。
 */
export const DESKTOP_IDLE_HINT_MS = 2000

/**
 * 把"桌面静止了多久（毫秒）"变成页面上那一句话。
 *
 * @param ms 被控端报的静止时长；`null`/`undefined` = 这台机器不提这件事。
 */
export function describeDesktopIdle(ms: number | null | undefined): string {
  // ⚠️ "不知道"与"0 秒"是两句话，不能合并：前者要升级客户端，后者什么都不用做。
  if (ms === null || ms === undefined) return '—'

  // 防御：负数 / NaN 不是一个可以展示的答案（时钟差方向写反时别画出"-3 秒"）。
  if (!Number.isFinite(ms) || ms < 0) return '—'

  if (ms < 1000) return '画面在变'

  return `已静止 ${Math.floor(ms / 1000)} 秒`
}

/**
 * 要不要额外提示一句"被控端桌面静止中"。
 *
 * ⚠️ 与 {@link describeDesktopIdle} 的判据**故意不同**（这里是 2 秒、那里是 1 秒）：
 * 那一格是"读数"，随时可以显示 1 秒；而提示语会占一行、会抢注意力，
 * 值得说的只有"真的停了一会儿"。
 */
export function isDesktopIdleNotice(ms: number | null | undefined): boolean {
  if (ms === null || ms === undefined) return false
  if (!Number.isFinite(ms) || ms < 0) return false

  return ms >= DESKTOP_IDLE_HINT_MS
}
