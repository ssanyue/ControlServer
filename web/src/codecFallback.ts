import { REMOTE_CODECS } from './types'

/**
 * **编码格式在页面上怎么说**（M7.11）。
 *
 * <p>
 * 🔥 <b>为什么这两个函数必须能脱离组件被断言</b>：它们说的是"这一路现在是什么、
 * 为什么不如人"，而这恰好是本仓最容易长期说谎的一处 —— M5.1 那轮写下的
 * 「画面还是 MJPEG 联调脚手架 / 帧率上不去默认 12 fps / 4K60 要等 M5.4」
 * 在 M5.4 之后<b>每个字都不成立</b>，却一直挂在页面顶部，直到现场照着它
 * 去判断故障（见 `docs/M7.11-页面编码文案更正.md`）。
 * 抽成纯函数以后，<b>说错话会被用例挡住</b>，而不是等人肉眼发现。
 * </p>
 *
 * <p>
 * ⚠️ 这里刻意<b>不碰任何浏览器 API、也不碰 `window`</b>：`vitest.config.ts` 里
 * `environment: 'node'` 且不加载 vue 插件，就是为了让这一层永远保持可测。
 * </p>
 */

/**
 * 编码格式的展示名。
 *
 * <para>
 * 🔥 <b>`mjpeg` 的标签是「MJPEG（降级）」而不是「联调脚手架」</b>：
 * "脚手架"的言外之意是"临时搭的、还没做完"，而它其实是**接线正确的降级档** ——
 * 多屏拼接（DDA 一次只对一个 output 建 duplication）和没有硬编的机器
 * **只能**走它。叫错名字会让运维以为这个功能还没交付。
 * </para>
 *
 * <para>
 * ⚠️ `null` 返回 `'—'` 而不是空串：页面那一格是"会话 xxx ｜ ???"，
 * 空串会让它看起来像"这一路没有编码格式"，而 `null` 的真实含义是
 * <b>"还没收到首帧、不知道"</b>。
 * </para>
 */
export function codecLabel(codec: string | null | undefined): string {
  if (!codec) return '—'

  return codec === REMOTE_CODECS.mjpeg ? 'MJPEG（降级）' : codec.toUpperCase()
}

/**
 * **走了 MJPEG 降级路径时**给一句话；其余一切情况返回 `null`（页面什么都不显示）。
 *
 * <para>
 * 🔥 <b>为什么不做成常驻横幅</b>：旧横幅对**每一路**都出现，包括硬编一切正常的那一路。
 * 一条"总是出现、且只讲坏消息"的横幅，最后一定会被所有人当背景噪声无视 ——
 * 那时它连"真的降级了"都提示不到。现在它只在 `codec === 'mjpeg'` 时出现。
 * </para>
 *
 * <para>
 * ⚠️ <b>它不解释"为什么降级"</b>：原因有三条完全不同的来路 ——
 * ① 多屏拼接只能软编；② 这台机器没有硬编；③ 浏览器只声明了 MJPEG。
 * 前端只能确定地判其中一部分，猜错会把排查引到错的方向。
 * 原因是<b>被控端裁决之后自己写的话</b>（会话的 `codecNote`），
 * 这里只负责说<b>事实与出路</b>。
 * </para>
 */
export function describeCodecFallback(codec: string | null | undefined): string | null {
  if (codec !== REMOTE_CODECS.mjpeg) return null

  return (
    '这一路走的是 MJPEG 降级路径：被控端用 CPU 逐帧软编 JPEG，'
    + '帧率与清晰度都低于硬编（H.264 / HEVC）。'
    + '要看细节请切到单块屏（硬编 1:1）；要 60 帧必须走硬编。'
  )
}
