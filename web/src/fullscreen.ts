/**
 * **把远程画面放大到整块屏幕**（M7.9）—— 浏览器 Fullscreen API 的薄封装。
 *
 * <h2>为什么这件小事要单独一层</h2>
 *
 * <p>
 * 直接写 `el.requestFullscreen()` 能跑，但现场会撞上三种"点了没反应"，而且三种的
 * 处置完全不同，页面上却都表现为**按钮按下去什么都没发生**：
 * </p>
 *
 * <table>
 *   <tr><th>现象</th><th>真因</th><th>该做什么</th></tr>
 *   <tr>
 *     <td>点了没反应，控制台报 <c>NotAllowedError</c></td>
 *     <td>全屏**必须在用户手势的同一个任务里**发起。中间只要 `await` 过一次
 *         （哪怕是一次 200 ms 的接口调用），手势凭据就失效</td>
 *     <td>改代码：按钮的点击处理里**先**进全屏，再去做别的</td>
 *   </tr>
 *   <tr>
 *     <td>点了没反应，<c>requestFullscreen</c> 不存在</td>
 *     <td>老 Safari / 被 iframe 挡住（缺 <c>allowfullscreen</c>）</td>
 *     <td>如实说"这个环境不支持"，而不是把按钮画成能用的样子</td>
 *   </tr>
 *   <tr>
 *     <td>进了全屏，按 Esc 之后页面还以为在全屏</td>
 *     <td>退出**不会**走我们的按钮 —— 浏览器自己收的，事后只发一个
 *         <c>fullscreenchange</c></td>
 *     <td>状态只认 <see cref="fullscreenElementOf"/>，不认本地那个布尔</td>
 *   </tr>
 * </table>
 *
 * <p>
 * 🔥 <b>本模块只做"能不能"和"怎么说"，不碰 Vue 状态</b>：
 * `document` 那一侧是可以造出来的（见单测），而组件里那些 `ref` 造不出来。
 * 把判据留在这一层，才能在**没有浏览器**的地方验"失败时说的是哪句话"。
 * </p>
 *
 * <p>
 * ⚠️ <b>全屏的是"画面那一块"，不是整页</b>：页面上还有工具栏、读数、剪贴板。
 * 全屏整页等于把一堆排障数字放大到投影上，而用户要的是**看那台机器的屏幕**。
 * </p>
 */

/** `document` 里与全屏有关的那几格（含旧 WebKit 前缀）。 */
export interface FullscreenDocumentLike {
  readonly fullscreenElement?: Element | null
  readonly webkitFullscreenElement?: Element | null
  readonly fullscreenEnabled?: boolean
  readonly webkitFullscreenEnabled?: boolean
  exitFullscreen?: () => Promise<void> | void
  webkitExitFullscreen?: () => Promise<void> | void
}

/** 元素的全屏入口（含旧 WebKit 前缀）。 */
export interface FullscreenElementLike {
  requestFullscreen?: (options?: { navigationUI?: 'auto' | 'hide' | 'show' }) => Promise<void> | void
  webkitRequestFullscreen?: () => Promise<void> | void
}

/**
 * "这个环境压根没有全屏能力"。
 *
 * <p>
 * ⚠️ 单独一个错误类型，是为了让 {@link describeFullscreenFailure} 能把它与
 * "浏览器拒绝了这次请求"分开 —— 前者要换浏览器 / 改 iframe，后者再点一次就好。
 * </p>
 */
export class FullscreenUnsupportedError extends Error {
  constructor(message = '这个环境没有 Fullscreen API。') {
    super(message)

    // 与 DOMException 的命名对齐：调用方按 `name` 判，不必认识这个类。
    this.name = 'NotSupportedError'
  }
}

/**
 * 此刻**真正**在全屏的那个元素；没有则 `null`。
 *
 * <p>
 * 🔥 标准属性与 WebKit 前缀属性要**都看**：Safari 16.4 之前只有
 * `webkitFullscreenElement`，而它非空时标准属性是 `null` —— 只读标准属性的话，
 * 页面会认为"没在全屏"，于是按钮永远显示「全屏显示」，点下去却什么都不变。
 * </p>
 */
export function fullscreenElementOf(doc: FullscreenDocumentLike): Element | null {
  return doc.fullscreenElement ?? doc.webkitFullscreenElement ?? null
}

/**
 * 这个文档**允不允许**全屏。
 *
 * <p>
 * ⚠️ <b>`undefined` 按"允许"处理</b>：`fullscreenEnabled` 在部分 WebKit 上是
 * `undefined`（不是 `false`），把它当"不支持"会让那些浏览器上的按钮永远是灰的。
 * 真正不支持时 `requestFullscreen` 本来就不存在，那一条由
 * {@link FullscreenUnsupportedError} 说。反过来，显式 `false` 是可信的 ——
 * 那正是 iframe 缺 `allowfullscreen` 时的样子。
 * </p>
 */
export function isFullscreenSupported(doc: FullscreenDocumentLike): boolean {
  const enabled = doc.fullscreenEnabled ?? doc.webkitFullscreenEnabled

  return enabled !== false
}

/** 此刻全屏的是不是**这一块**（用来同步按钮文案，以及"别人全屏了别乱退"）。 */
export function isElementFullscreen(doc: FullscreenDocumentLike, element: Element | null): boolean {
  if (!element) return false

  return fullscreenElementOf(doc) === element
}

/**
 * 请求把 `element` 全屏。
 *
 * <p>
 * 🔴 <b>调用点必须是一次用户点击的同一任务里</b>（中间不许 `await`）：
 * 浏览器只认手势，等过一个网络请求再来，Chrome 直接抛 `NotAllowedError`。
 * </p>
 *
 * @throws {FullscreenUnsupportedError} 这个环境两个入口都没有。
 * @throws 浏览器拒绝（`NotAllowedError` / `SecurityError` 等）时原样抛出。
 */
export async function enterFullscreen(element: Element): Promise<void> {
  const target = element as FullscreenElementLike

  if (typeof target.requestFullscreen === 'function') {
    await target.requestFullscreen()

    return
  }

  if (typeof target.webkitRequestFullscreen === 'function') {
    await target.webkitRequestFullscreen()

    return
  }

  throw new FullscreenUnsupportedError(
    '这个元素没有 requestFullscreen（浏览器太旧，或页面被 iframe 挡住了）。',
  )
}

/**
 * 退出全屏（本来就不在全屏时也安全）。
 *
 * <p>
 * ⚠️ 退出**必须无条件尝试**：用户按 Esc 时浏览器已经自己退了，这时再调一次
 * 会抛 `InvalidStateError` —— 那是无害的，调用方按"已经退出来了"处理即可。
 * </p>
 *
 * @throws {FullscreenUnsupportedError} 这个环境没有退出入口。
 */
export async function exitFullscreen(doc: FullscreenDocumentLike): Promise<void> {
  if (typeof doc.exitFullscreen === 'function') {
    await doc.exitFullscreen()

    return
  }

  if (typeof doc.webkitExitFullscreen === 'function') {
    await doc.webkitExitFullscreen()

    return
  }

  throw new FullscreenUnsupportedError('这个环境没有 exitFullscreen。')
}

/**
 * 把全屏失败翻成**下一步该做什么**。
 *
 * <p>
 * 🔥 这里刻意不写"全屏失败"这种话：用户已经看见了"没进全屏"，他需要的是原因。
 * 而三类原因的处置完全不同（换浏览器 / 再点一次 / 改页面嵌入方式），
 * 合成一句就等于让人去猜。
 * </p>
 */
export function describeFullscreenFailure(err: unknown): string {
  const name = err instanceof Error ? err.name : ''
  const raw = err instanceof Error ? err.message : ''

  switch (name) {
    case 'NotSupportedError':
      return '这个浏览器（或这个页面环境）不支持把画面全屏 —— 换 Edge / Chrome 打开本页即可；'
        + '画面本身不受影响，宽高照样是对的。'

    case 'NotAllowedError':
      // ⚠️ 最常见的一条，而且**不是用户的问题**：请求前面挂了一次 await 就会这样。
      return '浏览器只允许在**一次真实点击**里进入全屏，刚才这一下没被认作点击'
        + '（通常是页面在点完之后又等了一次接口）。请再点一次「全屏显示」。'

    case 'SecurityError':
      return '当前页面环境不允许全屏（多半是被嵌在 iframe 里，缺 allowfullscreen）。'
        + '直接用本页地址打开即可。'

    case 'InvalidStateError':
      return '这一下与浏览器自己处理的全屏状态撞上了（比如刚按过 Esc）—— 再点一次即可，没有副作用。'

    default:
      return raw ? `全屏没有成功：${raw}` : '全屏没有成功（浏览器没有给出原因）。'
  }
}
