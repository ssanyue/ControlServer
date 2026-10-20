/**
 * **观看端的"解码"抽象**（M5.4-b §8 第 8 步）。
 *
 * <p>
 * 抖动缓冲（{@link RemoteVideoBuffer}）本来只服务 MJPEG 一条路，于是它把
 * "解码"与"排期"写在了一起（`new Image()` ＋ `image.decode()`）。接入 H.264 之后
 * 这两件事**必须分开**，而分开的理由不是"代码整洁"，是一条**会直接把画面搞坏**的事实：
 * </p>
 *
 * <h2>🔴 丢帧语义对两条路是**相反**的</h2>
 *
 * <table>
 *   <tr><th></th><th>丢一帧意味着什么</th><th>因此"丢"可以发生在哪一层</th></tr>
 *   <tr>
 *     <td><b>MJPEG</b></td>
 *     <td>少一张<b>独立</b>图，前后两帧照样能解</td>
 *     <td><b>解码前</b>就能丢（省一次 JPEG 解码）</td>
 *   </tr>
 *   <tr>
 *     <td><b>H.264 / HEVC</b></td>
 *     <td>丢一个 P 帧 ⇒ 它后面<b>所有</b>依赖它的帧都花，<b>直到下一个关键帧</b></td>
 *     <td>只能丢在<b>上屏</b>那一层（帧照序全喂给解码器）</td>
 *   </tr>
 * </table>
 *
 * <p>
 * ⇒ 所以"能不能丢还没解码的帧"这件事，**只有解码器自己知道**，排期层不许猜。
 * 这就是 {@link FrameDecoder.canDropUndecoded} 存在的全部理由。
 * </p>
 *
 * <h2>🔴 为什么 `feed` 不是 `Promise&lt;DecodedFrame&gt;`</h2>
 *
 * <p>
 * 看着更自然的写法是"喂一帧、等它出来、再喂下一帧"。**它在 H.264 上会死锁**：
 * WebCodecs 的 `VideoDecoder` 内部会<b>缓存若干帧</b>才有第一帧输出（解码器延迟），
 * 于是"等第一帧出来"这件事永远等不到 —— 因为它在等后面几帧。
 * </p>
 *
 * <p>
 * ⇒ 接口定成"<b>`feed` 立刻返回、结果走 {@link DecodedSink} 回调</b>"，
 * 并且用 {@link FrameDecoder.maxInFlight} 让**每个解码器自己**回答"排期层最多能提前喂几帧"
 * （MJPEG 是 2，H.264 是<b>不设限</b>）。排期层不写死这个数。
 * </p>
 */

import type { RemoteStreamFrame } from './types'

/**
 * 一帧**解好、能直接 `drawImage`** 的画面。
 *
 * <p>
 * ⚠️ 它**持有真实资源**：MJPEG 是 `HTMLImageElement`（会被 GC），
 * H.264 是 `VideoFrame`（**不会**被 GC —— 不 `close()` 就是漏显存）。
 * ⇒ {@link release} 不是可选的装饰，是"这个对象被用过之后必须还"的合同。
 * </p>
 */
export interface DecodedFrame {
  /**
   * `HTMLImageElement`（MJPEG）或 `VideoFrame`（H.264/HEVC）。
   *
   * 🔥 两者都能**直接进 `ctx.drawImage`** —— 这不是巧合，是选 `VideoFrame` 这条路的
   * 主要原因之一：上屏那一层因此完全不用改（见 `RemoteDesktopPanel.vue` 的 paint 回调）。
   */
  source: CanvasImageSource
  /** 画面**像素**宽（不是 CSS 尺寸）。它决定 canvas 的 `width`。 */
  width: number
  height: number
  /**
   * 还资源。
   *
   * 🔥 **必须幂等**：同一帧完全可能被释放两次（一次"没轮上上屏"，一次"清空队列"），
   * 而 `VideoFrame.close()` 调第二次会抛。幂等性写在实现里，调用方不必防。
   */
  release(): void
}

/**
 * **解码器这一侧**的统计。
 *
 * ⚠️ 与 `VideoBufferStats` **刻意不是一个数**，别合并显示：
 * `VideoBufferStats` 里记的是"**排期这一侧**丢了多少帧"（画面会跳一下），
 * 这里记的是"**解码链**出了什么事"（画面会花到下一个关键帧）。
 * 把两者加起来，就等于把"降帧"读成"解码故障"。
 */
export interface FrameDecoderStats {
  /**
   * 解码器**产出**过几帧。
   *
   * 🔥 它与"上屏侧"的数（`VideoBufferStats.skipped` ＋ 面板的已画帧数）**是一对** ——
   * 这正是本仓诊断惯用的那种"两个方向各自记一次，对不上就说明有一侧在说谎"
   * （同 `frameDiagnosis` 里"被控端发 N / 中心收 M"）。
   * ⚠️ 但**不要**把"解不出来"也算进这里：那一项由 `VideoBufferStats.decodeErrors` 记，
   * 只记一次。
   */
  decoded: number
  /**
   * 🔥 **解码器被重建（`reset()` ＋ 重新 `configure()`）的次数** —— 本接口里最重要的一个数。
   *
   * <p>
   * 只有参考链**断了**才会走到这里：有帧没喂进解码器就被排期挤掉了。
   * 之后必须等下一个关键帧才有画面 —— 而这段"花屏/卡住"的时间，
   * 与"排期丢了几帧"是完全不同量级的两件事。
   * </p>
   */
  resets: number
}

/** 解码结果回调：`decoded === null` 表示这一帧**解不出来**（不是"还没好"）。 */
export type DecodedSink = (seq: number, decoded: DecodedFrame | null) => void

/**
 * 一个解码器（一条编码格式一个）。
 *
 * ⚠️ 所有方法都**不抛**：解码失败是**正常的运行时事件**（坏帧、参数集缺失、
 * 解码器需要重建），抛出去会把"这一帧坏了"升级成"这一路会话断了"，
 * 而后者要走"重新建会话"那条重路 —— 被控端重建抓屏是有成本的。
 */
export interface FrameDecoder {
  /** 这一路用的是哪种格式（`mjpeg` / `h264` / `hevc`）。 */
  readonly codec: string
  /**
   * 🔥 **"把还没解码的帧直接丢掉"安不安全？**
   *
   * · MJPEG = `true`（逐帧独立）
   * · H.264 / HEVC = `false`（有参考链）
   *
   * 排期层据此决定"忙的时候"能不能省掉一次解码 —— 以及队列满时腾出的是不是一条
   * 已经断掉的参考链。
   */
  readonly canDropUndecoded: boolean
  /**
   * 排期层最多"提前喂"几帧。
   *
   * · MJPEG = **2**（每帧一次真异步解码：够提前、又不会为注定要丢的帧白烧 CPU）
   * · H.264 / HEVC = **`Number.POSITIVE_INFINITY`** —— ⚠️ **不能限**：
   *   解码器内部会缓存若干帧才有第一帧输出，限深会在**第一帧上死锁**（见文件头）。
   */
  readonly maxInFlight: number
  /** 解码器这一侧的统计（见 {@link FrameDecoderStats}）。 */
  readonly stats: FrameDecoderStats
  /** 喂一帧。**立刻返回**；结果通过构造时给的 {@link DecodedSink} 回来。 */
  feed(frame: RemoteStreamFrame): void
  /**
   * **重建**解码器（参考链断了 / 会话重连）。
   *
   * 对 `canDropUndecoded` 的解码器可以是空操作。重建之后要**等下一个关键帧**才有画面。
   */
  reset(): void
  /** 释放解码器本身（切会话时）。**幂等**。 */
  close(): void
}

/** 由一个 {@link DecodedSink} 造出一个解码器 —— 由面板按 `header.codec` 选哪一个。 */
export type FrameDecoderFactory = (sink: DecodedSink) => FrameDecoder
