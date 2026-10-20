/**
 * **把浏览器保留键还给被控端**（M7.11）—— Keyboard Lock API 的薄封装。
 *
 * <h2>要解决的问题</h2>
 *
 * <p>
 * 远控画面拿到焦点后，用户的意图是"在这些键上操作**被控机**"：
 * 按 Ctrl+W 想关掉被控机上的窗口、按 F5 想刷新那边的页面。
 * 但浏览器里这几个键是**保留键**：`event.preventDefault()` 对它们**完全无效** ——
 * Chromium 在页面拿到事件之前就已经把它当"关标签页"处理掉了。
 * </p>
 *
 * <p>
 * 症状因此非常容易被误判：标签页**无声地消失**（没有报错、没有崩溃页、控制台干干净净），
 * 用户看到的是"页面被 Edge 强制关闭了"。而真正发生的事是：
 * 他在远控里按的那一下 Ctrl+W，先被浏览器截走关掉了自己的这一页。
 * </p>
 *
 * <p>
 * 🔥 <b>唯一能拦住它的机制就是 Keyboard Lock</b>：它要求页面**已经全屏**，
 * 锁住的键此后直接送给页面，浏览器不再执行"关标签页 / 刷新"这些动作。
 * 所以本模块只在全屏状态下有意义 —— 非全屏时由组件里的 `beforeunload`
 * 兜底（把"直接消失"变成"浏览器先问一句"）。
 * </p>
 *
 * <p>
 * ⚠️ <b>逃生通道必须保留</b>：`Escape` 与 `F11` **不在锁定清单里**。
 * 把 Esc 也锁掉的话，用户在全屏里就再也退不出来了 —— 那是比"误关标签页"
 * 严重得多的故障，而且现场没法自救。
 * </p>
 */

/**
 * `navigator.keyboard` 里我们用到的那两格。
 *
 * <p>
 * ⚠️ 不用 `lib.dom` 里的 `Keyboard` 类型：它在新版 TS 里存在、旧版里不存在，
 * 直接引用会让"换一台机器编译"变成一种运气。这里只声明真正调用的两个方法。
 * </p>
 */
export interface KeyboardLike {
  lock?: (keyCodes?: string[]) => Promise<void> | void
  unlock?: () => void
}

/**
 * 需要从浏览器手里抢回来的键（`KeyboardEvent.code` 形式）。
 *
 * <table>
 *   <tr><th>code</th><th>不锁的话会发生什么</th></tr>
 *   <tr><td><c>KeyW</c></td><td>Ctrl+W 关掉**观看端自己这个标签页**（最要命的那个）</td></tr>
 *   <tr><td><c>KeyR</c></td><td>Ctrl+R 刷新本页 ⇒ 画面重连、会话被重建</td></tr>
 *   <tr><td><c>F5</c></td><td>刷新本页（同上）</td></tr>
 *   <tr><td><c>KeyT</c> / <c>KeyN</c></td><td>新标签页 / 新窗口——观众会以为"点错了什么"</td></tr>
 *   <tr><td><c>KeyP</c> / <c>KeyS</c> / <c>KeyO</c></td><td>打印 / 保存 / 打开文件，全都会打断观看</td></tr>
 * </table>
 *
 * <p>
 * 刻意**不锁** `Escape`、`F11`、`AltLeft`、`CtrlLeft`、`ShiftLeft`：
 * 前两个是退出全屏的逃生通道；后三个是修饰键，锁住它们会让 Alt+Tab 之类的
 * 系统级组合在被控端失灵，而那不是我们该插手的地方。
 * </p>
 */
export const REMOTE_LOCKED_KEY_CODES: readonly string[] = [
  'KeyW',
  'KeyR',
  'KeyT',
  'KeyN',
  'KeyP',
  'KeyS',
  'KeyO',
  'F5',
]

/**
 * 取出 `navigator.keyboard`；这个环境没有就返回 `null`。
 *
 * <p>
 * ⚠️ 不要用 `'keyboard' in navigator` 判：部分浏览器把这个属性挂在原型上，
 * `in` 为真但值是 `undefined`。真正可信的判据是"`lock` 是个函数"。
 * </p>
 */
export function keyboardApiOf(nav: unknown): KeyboardLike | null {
  if (!nav || typeof nav !== 'object') return null

  const candidate = (nav as { keyboard?: unknown }).keyboard

  if (!candidate || typeof candidate !== 'object') return null

  const api = candidate as KeyboardLike

  return typeof api.lock === 'function' ? api : null
}

/**
 * 锁住浏览器保留键。**永不抛**，返回"到底锁上没有"。
 *
 * <p>
 * 失败是**正常情况**，不是异常：不在全屏、浏览器不支持、用户激活已过期
 * 都会让 `lock()` 拒绝。此时页面照常工作，只是保留键仍归浏览器 ——
 * 那一条由 `beforeunload` 兜底，不该在页面上弹红条。
 * </p>
 *
 * @param nav 一般是 `navigator`（测试里可以造）。
 * @param codes 要锁的键，默认 {@link REMOTE_LOCKED_KEY_CODES}。
 */
export async function lockBrowserKeys(
  nav: unknown,
  codes: readonly string[] = REMOTE_LOCKED_KEY_CODES,
): Promise<boolean> {
  const api = keyboardApiOf(nav)

  if (!api) return false

  try {
    await api.lock?.(codes as string[])

    return true
  } catch {
    // 非全屏 / 无用户激活 / 浏览器策略拒绝 —— 都按"没锁上"处理。
    return false
  }
}

/**
 * 解除锁定（没有锁过、或这个环境没有 API 时是空操作）。
 *
 * <p>
 * ⚠️ 退出全屏、切走会话、组件卸载时**都要调**：锁是挂在文档上的，
 * 留着不还，用户回到普通页面后这些键仍然不会触发浏览器行为 ——
 * 表现是"Ctrl+W 关不掉别的标签页了"，而那会被算成"这个网站很怪"。
 * </p>
 */
export function unlockBrowserKeys(nav: unknown): void {
  const api = keyboardApiOf(nav)

  if (!api) return

  try {
    api.unlock?.()
  } catch {
    /* 已经不在全屏 / 本来就没锁：按"已解除"处理。 */
  }
}

/**
 * "现在关掉这一页会不会丢东西"。
 *
 * <p>
 * 🔥 判据是**是否正在观看某一路会话**（不是"有没有未保存的表单"）：
 * 正在看画面时关掉页面，被控端那边会白起一次编码、观众还得重连一遍；
 * 而没在看的时候拦人，只会让"记录 → 关页"这种正常操作多一次确认。
 * </p>
 */
export function shouldWarnBeforeUnload(activeSessionId: string | null | undefined): boolean {
  return typeof activeSessionId === 'string' && activeSessionId.length > 0
}
