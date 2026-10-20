/**
 * **WebCodecs 解码器**（M5.4-b §8 第 8 步）—— 本批唯一的"新画面路径"。
 *
 * <p>
 * ⚠️ 文件名不叫 `h264Decoder` 是**故意的**：这份代码**同时服务 H.264 与 HEVC**。
 * 两者的差别**全部**在 `codecString`（`avc3.640032` vs `hev1.…`）与 `description`
 * （`avcC` vs `hvcC`）里 —— 而它们都是被控端给的，本文件一个字节都不解析。
 * 叫 `h264Decoder` 会让人以为"改 HEVC 时要动这里"，而实际上**一行都不用动**
 * （唯一要动的是 `remoteCodecProbe.ts` 的候选串）。
 * </p>
 *
 * <h2>🔴 本文件里最容易搞错的三件事</h2>
 *
 * <h3>1. `configure()` 的时机 —— 不是"第一帧配一次"</h3>
 *
 * <p>
 * `description` **允许跨帧可变**：带外模式下若某个关键帧解不出参数集，被控端会让
 * <b>那一帧单独退回带内</b>。所以规则是"**关键帧上 `(codecString, description)` 这一对
 * 与上次不同就重新 `configure()`**"（见方案 §3.9 (h) 与 §8 第 8 步）。
 * 只在第一帧配一次的表现是：那几个关键帧之后**花屏**，而两端日志全正常。
 * </p>
 *
 * <h3>2. `decode()` 是"入队"，不是"等它解完"</h3>
 *
 * <p>
 * 所以本解码器**不返回 Promise**、也**不限制喂入深度**（`maxInFlight = Infinity`）——
 * 见 `frameDecoder.ts` 文件头那段死锁说明。输出通过 `output` 回调来，
 * 序号靠 `timestamp` 配回去（**不靠 FIFO**，理由见 {@link takePendingFor}）。
 * </p>
 *
 * <h3>3. "丢"只能丢在**上屏**那一层</h3>
 *
 * <p>
 * 本文件**从不丢弃一个已收到的帧**（除非它根本解不了）。参考链必须完整 ——
 * 丢一个 P 帧，后面全花到下一个关键帧。降帧由排期层在"上屏"处做（`skipped` 那个计数）。
 * </p>
 */

import type { DecodedFrame, DecodedSink, FrameDecoder, FrameDecoderStats } from './frameDecoder'
import type { RemoteStreamFrame, RemoteStreamFrameHeader } from './types'

/** 上一次 `configure()` 用的那一对（外加码流尺寸）。 */
export interface ConfigureKey {
  codecString: string | null
  description: string | null

  /**
   * 码流的编码尺寸（帧头里**这一块**的宽高）。
   *
   * <p>
   * 🔥 <b>为什么必须传给解码器</b>（M7.7，现场症状：「部分客户机的 4K 只显示四分之一」）：
   * <c>codedWidth/codedHeight</c> 在 WebCodecs 里是**可选**的，不传时浏览器会
   * "自己看码流/自己猜"。而部分机器的**硬件**解码路径会按一个**默认上限**（常见就是
   * 1920×1080）分配解码表面 ⇒ 4K 码流只解出左上那一块，<b>既不报错、也不掉帧</b>。
   * 而软件解码路径不受影响 —— 这正是"部分客户机"这个分布的唯一合理解释。
   * </p>
   *
   * <p>
   * ⚠️ 它<b>不是</b>"显示尺寸"：分块时每一块只有 1920 宽，这里就该是 1920。
   * 传整幅（3840）会让解码器按 3840 分配、拿 1920 宽的码流去填 —— 花屏。
   * </p>
   */
  codedWidth?: number
  codedHeight?: number
}

/** 等输出的那一帧。 */
export interface PendingOutput {
  /** 原始帧的序号 —— 配上了就把它交回给排期层。 */
  seq: number
  /** 喂进去时放在 chunk 上的 `timestamp`（微秒），**输出就是靠它配回来的**。 */
  ts: number
}

/**
 * 这个关键帧要不要重新 `configure()`？
 *
 * <p>
 * 🔥 判据是"**这一对**变了没有"，不是"换了关键帧没有"：
 * `codecString` 说明**解码串**（profile/level 或封装变了），
 * `description` 说明**码流是长度前缀还是 Annex-B** —— 后者变了而没重配，
 * 表现是花屏（码流被按错误的规则解读），**不报错**。
 * </p>
 *
 * ⚠️ 只在关键帧上调用（非关键帧上重配会把解码器状态连同参考帧一起清掉）。
 */
export function needsReconfigure(configured: ConfigureKey | null, incoming: ConfigureKey): boolean {
  if (configured === null) return true

  return (
    configured.codecString !== incoming.codecString ||
    configured.description !== incoming.description ||
    // 🔥 尺寸也要比（M7.7）：换屏 / 换分块之后码流尺寸会变，而 codec 串可能一模一样
    //    （同一个 profile/level）。不重配的后果是解码器仍按旧尺寸解，画面会被裁或拉伸。
    configured.codedWidth !== incoming.codedWidth ||
    configured.codedHeight !== incoming.codedHeight
  )
}

/** `buildDecoderConfig` 的结果。**不抛** —— 失败是一种正常返回。 */
export type DecoderConfigResult =
  | { ok: true; config: VideoDecoderConfig }
  | { ok: false; reason: string }

/**
 * 造 `VideoDecoderConfig`。纯函数（**不碰浏览器**，所以能被用例直接测）。
 *
 * <p>
 * 🔥 它同时把一条**从未被验过的约定**变成了代码：`codec` 串的前缀与 `description` 的
 * 有无**必须自洽**（`avc3`/`hev1` = 带内 ⇒ **不许**带 `description`；
 * `avc1`/`hvc1` = 带外 ⇒ **必须**带）。不自洽时 `configure()` 的表现是
 * **花屏或直接失败**，而两边日志都正常 —— 所以在**进解码器之前**就挡掉，
 * 并且把理由写成一句话（它会进 `decodeErrors` 的旁边，供现场读）。
 * </p>
 */
export function buildDecoderConfig(key: ConfigureKey): DecoderConfigResult {
  const codec = key.codecString

  if (!codec) {
    // ⚠️ 被控端只在"能解出参数集"的关键帧上才填 `codecString` ⇒ 空值意味着
    //    "这一帧的 SPS 没解出来"。**不许手写一个默认串**（例如 `avc1.42E01E`）：
    //    错串的表现是"能力探测说支持、真解却失败"，而那会把排查引到浏览器版本上。
    return { ok: false, reason: '这一帧没有带来 codec 串（被控端未能解出参数集）' }
  }

  const inBand = codec.startsWith('avc3') || codec.startsWith('hev1')
  const outOfBand = codec.startsWith('avc1') || codec.startsWith('hvc1')
  const hasDescription = key.description !== null && key.description.length > 0

  if (outOfBand && !hasDescription) {
    return { ok: false, reason: `codec 串「${codec}」声明带外，但没有 description` }
  }

  if (inBand && hasDescription) {
    return { ok: false, reason: `codec 串「${codec}」声明带内，却带了 description` }
  }

  const config: VideoDecoderConfig = {
    codec,
    // 🔥 远控是"低延迟优先"的典型：让解码器少攒几帧再出图。
    optimizeForLatency: true,
  }

  // 🔥 把**真实编码尺寸**告诉解码器（M7.7）。不传时部分硬件解码器会按
  //    一个默认上限分配解码表面 ⇒ 4K 只解出左上 1/4，且**不报错**。
  //    ⚠️ 只在两个值都合法时传：只传一个会让某些实现按 0 处理。
  if (
    key.codedWidth !== undefined && key.codedWidth > 1
    && key.codedHeight !== undefined && key.codedHeight > 1
  ) {
    config.codedWidth = key.codedWidth
    config.codedHeight = key.codedHeight
  }

  if (hasDescription) config.description = base64ToBytes(key.description as string)

  return { ok: true, config }
}

/**
 * base64 → 字节。**纯函数**。
 *
 * 🔥 这是 H.264 那条路相对 MJPEG 的**新增成本**：MJPEG 能把 base64 直接拼成
 * `data:` URL（省掉这一趟），而 WebCodecs 要 `Uint8Array` ⇒ 必须付。
 * 一帧 IDR 约 193–307 KB ⇒ 解码后约 145–230 KB。
 *
 * ⚠️ 不用 `Uint8Array.from(atob(s), c => c.charCodeAt(0))`：那会为每个字节调一次回调，
 * 在 200 KB 上是几万次函数调用 —— 帧循环里不允许（同 `mjpegDecoder` 里那条"不许 spread"）。
 */
export function base64ToBytes(base64: string): Uint8Array {
  const binary = atob(base64)
  const bytes = new Uint8Array(binary.length)

  for (let i = 0; i < binary.length; i += 1) bytes[i] = binary.charCodeAt(i)

  return bytes
}

/**
 * 这一帧喂给解码器时用的 `timestamp`（**微秒**）。
 *
 * 🔥 **它必须是真实时间，不许拿序号冒充**：`VideoFrame.timestamp` 会在输出那一侧
 * 被读回来当"这一帧是哪一帧"的凭据，而一个"序号冒充的时间戳"就是在给下游一个
 * 看起来合法、其实是编的值。优先用会话单调时刻（与音频同轴）。
 */
export function chunkTimestampUs(header: RemoteStreamFrameHeader): number {
  if (typeof header.capturedMonoMs === 'number') return header.capturedMonoMs * 1000

  // 旧被控端没有单调时刻 ⇒ 退回墙钟（**也是真实时间**，只是基准不同）。
  return header.capturedUnixMs * 1000
}

/**
 * 从"等输出"的队列里取出这一帧对应的一格。**纯函数**（返回新数组，不改入参）。
 *
 * <p>
 * 🔥 <b>为什么是"按 timestamp 找"而不是"取队头"</b>：本批的编码器**没有 B 帧**
 * （§3.4 第 ① 条实测），所以输出与输入同序，取队头在今天是够用的。但"够用"不等于
 * "对" —— 一旦哪天开了 B 帧、或换个会重排的解码器，取队头会把 <b>A 帧的序号配到 B 的画面</b>上，
 * 表现是"画面内容与时间戳对不上"，而那**没有任何一处会报错**。
 * </p>
 *
 * <p>
 * ⚠️ 配不上时返回 {@link takePendingFor} 的 `seq: null` ⇒ 调用方**关掉这一帧**。
 * 宁可少一帧，也不许把一帧画到错误的位置上。
 * </p>
 */
export function takePendingFor(
  pending: readonly PendingOutput[],
  frameTs: number,
): { seq: number | null; rest: PendingOutput[] } {
  const head = pending[0]

  if (head && head.ts === frameTs) return { seq: head.seq, rest: pending.slice(1) }

  const index = pending.findIndex((p) => p.ts === frameTs)

  if (index < 0) return { seq: null, rest: pending.slice() }

  const rest = pending.slice()
  const [found] = rest.splice(index, 1)

  return { seq: found.seq, rest }
}

/** 解码器的结构契约（注入用；默认就是浏览器全局那个 `VideoDecoder`）。 */
export interface VideoDecoderLike {
  configure(config: VideoDecoderConfig): void
  decode(chunk: EncodedVideoChunkLike): void
  reset(): void
  close(): void
}

/** `decode()` 收的东西。真正实现是 `EncodedVideoChunk`。 */
export interface EncodedVideoChunkLike {
  readonly type: 'key' | 'delta'
  readonly timestamp: number
}

export interface WebCodecsDecoderOptions {
  /**
   * 造一个解码器。默认用浏览器全局的 `VideoDecoder`。
   *
   * 🔥 留这个口子是为了**能被用例驱动**：真 `VideoDecoder` 在 node 里不存在，
   * 而"输出怎么配回序号"这件事恰恰是最该被测的一段逻辑。
   *
   * ⚠️ 返回 `null` = 这个环境里没有解码器（非安全上下文 / 浏览器不支持 / `VideoDecoder`
   * 压根 undefined）⇒ 所有帧都解不出来，**但绝不抛**。
   */
  createDecoder?: (init: VideoDecoderInit) => VideoDecoderLike | null
  /** 造一个 chunk。默认 `new EncodedVideoChunk(...)`。同上，为可测。 */
  createChunk?: (init: {
    type: 'key' | 'delta'
    timestamp: number
    data: Uint8Array
  }) => EncodedVideoChunkLike
}

/**
 * 造一个 WebCodecs 解码器（服务 H.264 **与** HEVC）。
 *
 * @param codec 这一路实际用的格式名（只用于 {@link FrameDecoder.codec} 的回读）。
 * @param sink 解好（或解不出）时回调。
 */
export function createWebCodecsDecoder(
  codec: string,
  sink: DecodedSink,
  options: WebCodecsDecoderOptions = {},
): FrameDecoder {
  const stats: FrameDecoderStats = { decoded: 0, resets: 0 }

  const state: {
    decoder: VideoDecoderLike | null
    configured: ConfigureKey | null
    pending: PendingOutput[]
    closed: boolean
  } = { decoder: null, configured: null, pending: [], closed: false }

  const createDecoder = options.createDecoder ?? defaultCreateDecoder
  const createChunk = options.createChunk ?? defaultCreateChunk

  /** 把所有"还在等输出"的帧报成"解不出来"，并清空等待队列。 */
  function failAllPending(): void {
    const waiting = state.pending

    state.pending = []

    for (const item of waiting) sink(item.seq, null)
  }

  /** 丢掉解码器本身（不含"等输出"队列的收尾，那是调用方的事）。 */
  function dropDecoder(): void {
    const previous = state.decoder

    state.decoder = null
    state.configured = null

    if (previous) {
      try {
        previous.close()
      } catch {
        // 已关闭 / 已进 error 态：都不是问题。
      }
    }
  }

  function onOutput(frame: VideoFrame): void {
    const taken = takePendingFor(state.pending, frame.timestamp)

    state.pending = taken.rest

    if (taken.seq === null) {
      // 配不上 ⇒ 关掉它。⚠️ 这不是"顺手清理"：不关就是漏显存，而漏显存的表现是
      //    "看一会儿就卡"，日志一片安静。
      closeQuietly(frame)

      return
    }

    stats.decoded += 1
    sink(taken.seq, wrapVideoFrame(frame))
  }

  function onError(): void {
    // 解码器进 `error` 态之后**已经自毁**（spec：state 变 `closed`）⇒ 换一个新的。
    // 之前所有在等的帧都拿不到输出了。
    failAllPending()
    dropDecoder()

    // 新的那个由 `feed()` 懒建（`state.decoder === null` 就走那条路）。
    stats.resets += 1
  }

  return {
    codec,

    // 🔥 H.264/HEVC **有参考链**：丢一个 P 帧，后面全花到下一个关键帧。
    //    ⇒ 排期层"能不能省掉一次解码"这件事，答案是**不能**。
    canDropUndecoded: false,

    // 🔥 **不设限**：`decode()` 入队即返回，而解码器内部要攒若干帧才有第一帧输出。
    //    限深（例如 2）会在**第一帧上死锁** —— 见 `frameDecoder.ts` 文件头。
    maxInFlight: Number.POSITIVE_INFINITY,

    stats,

    feed(frame: RemoteStreamFrame): void {
      const header = frame.header

      if (!header || state.closed) return

      const bytes = payloadBytes(frame)
      const seq = header.sequence

      if (bytes === null || bytes.length === 0) {
        sink(seq, null)

        return
      }

      // 懒建解码器（第一次真要用它的时候才建 —— 早建会在"其实一路都不需要"时白占资源）。
      if (state.decoder === null) {
        state.decoder = createDecoder({ output: onOutput, error: onError })

        if (state.decoder === null) {
          // 这个环境没有解码器（非安全上下文 / 不支持）。**不抛**，逐帧报"解不出来"。
          sink(seq, null)

          return
        }
      }

      const key: ConfigureKey = {
        codecString: header.codecString,
        description: header.description,
        // 🔥 这一块的编码尺寸（不是整幅）—— 见 `ConfigureKey.codedWidth` 的长注释：
        //    不传它，部分硬件解码器会按默认上限分配表面，4K 只解出左上 1/4。
        codedWidth: header.width,
        codedHeight: header.height,
      }

      // 🔥 只有关键帧才可能触发重配（见 §3.9 (h)）。非关键帧上那一对**允许为空**
      //    （被控端只在关键帧上填），所以"非关键帧没有 description"不是异常。
      if (header.isKeyframe && needsReconfigure(state.configured, key)) {
        const built = buildDecoderConfig(key)

        if (!built.ok) {
          // 配不上 ⇒ 这一帧解不出来（等下一个带齐参数集的关键帧）。
          sink(seq, null)

          return
        }

        try {
          state.decoder.configure(built.config)
          state.configured = key
        } catch {
          // `NotSupportedError` 等：**不抛**。这一帧解不出来，但不该让整路停下来。
          state.configured = null
          sink(seq, null)

          return
        }
      }

      if (state.configured === null) {
        // 还没配过、这一帧又不是关键帧 ⇒ 缺参数集，喂进去必报错。
        // ⇒ 明确丢掉它（它本来就解不出来），而不是让解码器进 error 态 ——
        //    后者会让**整条链断掉**，代价远大过少一帧。
        sink(seq, null)

        return
      }

      const ts = chunkTimestampUs(header)

      state.pending.push({ seq, ts })

      try {
        state.decoder.decode(
          createChunk({
            type: header.isKeyframe ? 'key' : 'delta',
            timestamp: ts,
            data: bytes,
          }),
        )
      } catch {
        // 喂不进去 ⇒ 把刚压进去的那一格弹掉（否则它会一直等一个永不到来的输出）。
        state.pending.pop()
        sink(seq, null)
      }
    },

    reset(): void {
      const had = state.decoder !== null

      failAllPending()
      dropDecoder()

      // ⚠️ 只有**确实重建了一次**才算数：`breakChain()` 对"还没建解码器"的情况也会调它，
      //    那时记一笔 `resets` 就是在编一个没发生的故障。
      if (had) stats.resets += 1
    },

    close(): void {
      state.closed = true
      state.pending = []
      dropDecoder()
    },
  }
}

/** 默认：浏览器全局的 `VideoDecoder`。**没有就返回 null（不抛）**。 */
function defaultCreateDecoder(init: VideoDecoderInit): VideoDecoderLike | null {
  // ⚠️ 非安全上下文（`http://` 的非本机地址）里 `VideoDecoder` **是 undefined，不是报错**
  //    ⇒ 必须用 `typeof` 判，而不是 try/catch 去 new 一个。
  if (typeof VideoDecoder === 'undefined') return null

  try {
    return new VideoDecoder(init)
  } catch {
    return null
  }
}

/** 默认：`new EncodedVideoChunk(...)`。 */
function defaultCreateChunk(init: {
  type: 'key' | 'delta'
  timestamp: number
  data: Uint8Array
}): EncodedVideoChunkLike {
  return new EncodedVideoChunk(init)
}

/** 把 `VideoFrame` 包成 {@link DecodedFrame}。 */
function wrapVideoFrame(frame: VideoFrame): DecodedFrame {
  let released = false

  return {
    source: frame,
    width: frame.displayWidth,
    height: frame.displayHeight,
    release: () => {
      // ⚠️ 幂等是**合同**（见 `DecodedFrame.release`）：同一帧可能被丢两次。
      if (released) return

      released = true
      closeQuietly(frame)
    },
  }
}

/** `frame.close()`，**不抛**（已关过 / 已失效都当成功）。 */
function closeQuietly(frame: VideoFrame): void {
  try {
    frame.close()
  } catch {
    // 有意为空：关闭一个已经关掉的 frame 不是错误。
  }
}

/**
 * 从帧里取码流字节。
 *
 * ⚠️ `data` 正常是 **base64 字符串**（SignalR 的 JSON 协议把 `byte[]` 序列化成 base64）。
 * 字节数组那条是 MessagePack 之后会走的 —— 现在也认，是为了那天出问题时
 * **不是**"画面全黑而控制台一片安静"。
 */
function payloadBytes(frame: RemoteStreamFrame): Uint8Array | null {
  const raw: unknown = frame.data

  if (typeof raw === 'string') return raw.length === 0 ? null : base64ToBytes(raw)
  if (raw instanceof Uint8Array) return raw
  if (Array.isArray(raw)) return new Uint8Array(raw as number[])

  return null
}
