/**
 * **MJPEG 解码器**（M5.4-b §8 第 8 步：从 `remotePlayback.ts` 搬出来）。
 *
 * <p>
 * 🔥 <b>搬家的目的不是整理代码，是把"丢帧语义"从排期层拿走</b>。原来这段逻辑长在
 * `RemoteVideoBuffer.decodeInto` 里，于是排期层"知道"MJPEG 的帧可以独立丢 ——
 * 而那个知识对 H.264 是<b>错的</b>（见 {@link FrameDecoder}）。
 * 现在它明确地写在 {@link createMjpegDecoder} 的 `canDropUndecoded: true` 上。
 * </p>
 *
 * <p>
 * ⚠️ 行为**逐字保留**（这是 M5.1 以来唯一真正在跑的画面路径，不许顺手"改进"）：
 * 唯一变化是"解码"这一步的调度语义由接口回答，其余（data URL 拼法、
 * `image.decode()`、坏帧不中断整路）一字未动。
 * </p>
 */

import type { DecodedFrame, DecodedSink, FrameDecoder, FrameDecoderStats } from './frameDecoder'
import { REMOTE_CODECS, type RemoteStreamFrame } from './types'

/**
 * 同时最多几帧在解码。
 *
 * 给 2 是"够提前、又不会为注定要丢的帧白烧 CPU"。
 *
 * ⚠️ 这个数**只对 MJPEG 成立**：它每帧一次真异步解码，喂多了只是白烧。
 * H.264 那条路的 `decode()` 是"入队即返回"，限深会**死锁**（见 `frameDecoder.ts` 文件头）。
 */
export const MJPEG_MAX_IN_FLIGHT = 2

/**
 * 造一个 MJPEG 解码器。
 *
 * @param sink 解好（或解不出）时回调。**必须**在 `close()` 之后不再被调用。
 */
export function createMjpegDecoder(sink: DecodedSink): FrameDecoder {
  const stats: FrameDecoderStats = { decoded: 0, resets: 0 }
  // 用一个对象而不是布尔，是为了让"已关闭"这件事**穿过闭包**被 feed 里那条异步链看到：
  // `image.decode()` 回来时可能已经切走了会话，那一刻手里的 image 只能丢。
  const state = { closed: false }

  return {
    codec: REMOTE_CODECS.mjpeg,

    // 🔥 MJPEG 逐帧独立（每帧一张自包含的 JPEG）⇒ 丢任何一帧都不影响别的帧。
    canDropUndecoded: true,

    maxInFlight: MJPEG_MAX_IN_FLIGHT,

    stats,

    feed(frame: RemoteStreamFrame): void {
      void decodeOne(frame, sink, stats, state)
    },

    /**
     * MJPEG **没有参考链可断** ⇒ 重建没有意义，空操作。
     *
     * ⚠️ 接口要求这个方法存在（排期层在队列溢出时会统一调它），而这个空实现
     * **不是因为懒得写** —— 它是"MJPEG 不需要等关键帧"这件事的落点。
     */
    reset(): void {
      // 有意为空。见上面的说明。
    },

    close(): void {
      state.closed = true
    },
  }
}

/** 解一帧。**不抛**：坏帧记一句、继续等下一帧。 */
async function decodeOne(
  frame: RemoteStreamFrame,
  sink: DecodedSink,
  stats: FrameDecoderStats,
  state: { closed: boolean },
): Promise<void> {
  const seq = frame.header?.sequence ?? -1

  if (seq < 0) return

  const url = toDataUrl(frame)

  if (url === null) {
    // 空数据：当作"这一帧解不出来"，而不是抛。
    sink(seq, null)
    return
  }

  const image = new Image()

  try {
    image.src = url
    await image.decode()

    // 解码期间会话可能已经切走了（`close()`）⇒ 结果丢掉。
    // ⚠️ 这里**不调 sink**：队列已经清空，塞回去只会让排期层去找一个不存在的 item。
    if (state.closed) return

    stats.decoded += 1
    sink(seq, mjpegFrame(image))
  } catch {
    if (state.closed) return

    // 单帧坏图**不该**让整路停下来：记一句、继续等下一帧。
    // 一断流就会掉进"要重新建会话"的坑，而重建被控端的抓屏是有成本的。
    sink(seq, null)
  }
}

/** 把一张解好的图包成 {@link DecodedFrame}。 */
function mjpegFrame(image: HTMLImageElement): DecodedFrame {
  return {
    source: image,
    width: image.naturalWidth,
    height: image.naturalHeight,
    // `HTMLImageElement` 由 GC 回收，没有"必须显式还"的原生资源 ⇒ 空操作。
    // ⚠️ 但仍然要实现：**排期层不许知道"哪种解码结果需要 release"**，
    //    否则那个知识就会在下一次换解码器时漂掉（`VideoFrame` 漏的就是显存）。
    release: () => {
      /* 有意为空：GC 负责。 */
    },
  }
}

/**
 * 帧字节 → 可直接喂 `Image` 的 URL。
 *
 * 🔥 两条分支都不是多余的：
 *   · **字符串**是今天真实走的那条 —— SignalR 的 JSON 协议把 `byte[]` 序列化成 base64，
 *     而 JS 客户端不会帮你转回来。base64 前面加个 `data:` 头就能直接用，省掉一次 `atob`。
 *   · **字节数组**是 M5.4 改走 MessagePack 之后会走的那条。现在写上，
 *     是为了那天出问题时**不是**"画面全黑而控制台一片安静"。
 *
 * ⚠️ **H.264 那条路用不上这个函数**：WebCodecs 要的是 `Uint8Array`，
 * 反过来必须付 `atob` 那一趟（见 `h264Decoder.ts`）—— 同一个 base64 在这里是便利，
 * 在那里是成本。
 */
export function toDataUrl(frame: RemoteStreamFrame): string | null {
  const raw: unknown = frame.data

  if (typeof raw === 'string') {
    return raw.length === 0 ? null : `data:image/jpeg;base64,${raw}`
  }

  if (raw instanceof Uint8Array || Array.isArray(raw)) {
    const bytes = raw instanceof Uint8Array ? raw : new Uint8Array(raw as number[])

    if (bytes.length === 0) return null

    return `data:image/jpeg;base64,${bytesToBase64(bytes)}`
  }

  return null
}

/**
 * 字节 → base64。
 *
 * ⚠️ 不能用 `String.fromCharCode(...bytes)`：一帧几十万字节会把调用栈打爆
 * （这正是这段代码一直以来的注释在说的事）。
 */
export function bytesToBase64(bytes: Uint8Array): string {
  let binary = ''

  for (let i = 0; i < bytes.length; i += 1) binary += String.fromCharCode(bytes[i])

  // ⚠️ 用全局 `btoa` 而不是 `window.btoa`：前者在浏览器与 Node 里都有，
  //    后者只在浏览器里 —— 而这个函数是要**被用例直接调**的（用例跑在 node 环境）。
  return btoa(binary)
}
