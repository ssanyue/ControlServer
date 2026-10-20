/**
 * 远控观看端的**播放侧**（M5.3）：会话时钟 + 音频播放 + 视频抖动缓冲。
 *
 * <p>
 * 三件事写在同一个模块里，因为它们是**同一条时间轴上的三个部件**，
 * 拆开之后"它们共享哪个基准"就看不见了 —— 而音画对齐出问题时，
 * 唯一要想清楚的就是这件事。
 * </p>
 *
 * ─────────────────────────────────────────────────────────────
 * ## 一、基准：为什么不能用 `Date.now()`
 *
 * 被控端在每一帧（视频与音频都是）的帧头上带两个时刻：
 * - `capturedUnixMs`：墙钟。用来算"端到端多久"，**要求两台机器对过时**；
 * - `capturedMonoMs`：**这一路会话开始以来的毫秒数**。两台机器不需要对时。
 *
 * 🔥 播放调度只能用后者。原因不是"精度"而是"归属"：
 * 墙钟会被 NTP 校时、时区设置、手工改表**在任何时刻拉走一大截**，
 * 而拉走的那一刻正好会把播放位置整体推走 —— 表现是声音或者画面卡死一大截再跳回来。
 * 单调时钟在一个进程的生命周期里是**只增不减**的，这一整类故障就不存在。
 *
 * ─────────────────────────────────────────────────────────────
 * ## 二、怎么把"对端的时间"换成"本机的时间"
 *
 * 拿一个观测就有一个等式：`到达时刻(本机) = 锚点 + 采集时刻(对端) + 这段路的延迟`。
 * 延迟未知、且永远为正，所以**取所有观测里最小的那个**就是锚点的最优估计
 * （这是网络授时里最经典的做法，叫 min-filter）：延迟越小，等式越接近真值。
 * 用最小值而不是平均值，是因为平均会被某一次卡顿整个带偏，而最小值只会被"运气最好的一次"决定。
 *
 * ⚠️ 它的代价也要说清楚：锚点因此是**单调下移**的（新观测只会让它更小、不会更大），
 * 于是播放位置会偶尔**向前微跳**一次（几毫秒）。这个代价换来的是"不会被一次卡顿带偏"，
 * 值得 —— 而且跳的是"往正确方向跳"，因为原来的估计只会偏大（延迟估计得偏大）。
 *
 * ─────────────────────────────────────────────────────────────
 * ## 三、音画怎么对齐
 *
 * 两者都用同一句：**`本机时刻 = 锚点 + 采集时刻 + 目标延迟`**。
 * 音频按这句话排"该在第几秒响"，视频按这句话选"该显示哪一帧"。
 * 目标延迟（{@link TARGET_LATENCY_MS}）两边取同一个值，于是偏差按构造≈0，
 * 剩下的（解码耗时、排队深度、调度的舍入）就是**真正被测出来的那部分** ——
 * 见 `RemoteDesktopPanel.vue` 里的"音画偏差"读数。
 *
 * ⚠️ 所以那个读数**不是**自证式的（不是"因为我这么算了所以它一定是 0"）：
 * 它比的是"实际上屏的那一帧的采集时刻"与"实际上正在响的那一段的采集时刻"，
 * 两者各自带上自己的解码与排队耗时。
 */

import type {
  DecodedFrame,
  FrameDecoder,
  FrameDecoderFactory,
  FrameDecoderStats,
} from './frameDecoder'
import type { RemoteAudioFrame, RemoteStreamFrame } from './types'
import { REMOTE_AUDIO_CODECS } from './types'
import { getPlaybackLatencyMs, LATENCY_MAX_MS } from './playbackLatency'

/**
 * 播放延迟的<b>上限</b>（毫秒）—— 也就是 M7.16 之前那个写死的 60。
 *
 * <p>
 * 🔥 <b>M7.16 起它不再是"当前用的那个数"</b>：实际预算按目标帧率算
 * （60 fps ⇒ 33 ms），见 {@link import('./playbackLatency').getPlaybackLatencyMs}。
 * 这里保留导出只是因为页面上要显示"上限是多少"。
 * </p>
 *
 * <p>
 * ⚠️ <b>为什么原来的 60 对 60 fps 是浪费</b>：60 ms ＝ 3.6 帧的积压，
 * 而缓冲真正要买的只有两帧（吃掉一个周期的乱序）。多出来的 1.6 帧
 * （≈27 ms）是每一帧都白付的端到端延迟 —— 手到眼的那条路上，27 ms 是能感觉到的。
 * </p>
 */
export const TARGET_LATENCY_MS = LATENCY_MAX_MS

/** 环形缓冲最多留多少帧；超了从最旧的丢（视频专用，音频在 worklet 里自己管）。 */
const VIDEO_QUEUE_LIMIT = 12

/**
 * 一帧到了这个岁数还没轮到解码，就不解了（它已经过了该显示的时机）。
 *
 * ⚠️ **只对"能丢未解码帧"的解码器（MJPEG）生效**：H.264/HEVC 有参考链，
 * 丢一个 P 帧会让后面全花到下一个关键帧 ⇒ 它的"丢"只能发生在上屏那一层（见 `tickOnce`）。
 */
const VIDEO_STALE_MS = 250

// ═════════════════════════════════════════════════════════════════════
// 会话时钟
// ═════════════════════════════════════════════════════════════════════

/**
 * 把"对端的会话单调毫秒"映射到"本机的 `performance.now()` 毫秒"。
 *
 * <p>
 * ⚠️ 刻意<b>不</b>做长时间平均、也不做平滑滤波：锚点只用"历史最小值"。
 * 平滑会让"某一次特别慢的包"也参与进来，而那正是要排除的东西。
 * </p>
 */
export class SessionClock {
  /** 锚点：本机 perf 毫秒 − 对端会话毫秒。越大 = 这段路越慢。 */
  private anchor = Number.POSITIVE_INFINITY

  /** 一共吃了多少观测（日志/排障用）。 */
  private samples = 0

  /** 已经能用了没有。 */
  get ready(): boolean {
    return Number.isFinite(this.anchor)
  }

  /** 观测次数。 */
  get observed(): number {
    return this.samples
  }

  /**
   * 吃一个观测。
   *
   * @param capturedMonoMs 对端在这一帧/段上写的会话单调毫秒。**必须是真的**单调时钟，
   *   传进来一个 0 或 `null` 会把锚点一次性拉到极低（表现为"播放位置跳到未来"）。
   */
  observe(capturedMonoMs: number | null | undefined, nowPerf = performance.now()): void {
    if (capturedMonoMs === null || capturedMonoMs === undefined) return
    if (!Number.isFinite(capturedMonoMs) || capturedMonoMs < 0) return

    const candidate = nowPerf - capturedMonoMs

    if (candidate < this.anchor) this.anchor = candidate

    this.samples += 1
  }

  /** 对端会话毫秒 → 本机 `performance.now()` 毫秒。锚点还没建立时返回 `null`。 */
  toPerf(capturedMonoMs: number): number | null {
    return this.ready ? this.anchor + capturedMonoMs : null
  }

  /** 本机此刻落在对端那条时间轴的哪里（毫秒）。锚点还没建立时返回 `null`。 */
  nowMono(nowPerf = performance.now()): number | null {
    return this.ready ? nowPerf - this.anchor : null
  }

  /** 换会话时清空 —— 留下上一路的锚点会让新会话一开始就偏一大截。 */
  reset(): void {
    this.anchor = Number.POSITIVE_INFINITY
    this.samples = 0
  }
}

// ═════════════════════════════════════════════════════════════════════
// base64 → 字节
// ═════════════════════════════════════════════════════════════════════

/**
 * 音频载荷 → 字节。`null` = 这条载荷根本没法用（空 / 类型不对）。
 *
 * ⚠️ 与视频那条路**刻意不同**：视频的 base64 可以直接当 `data:` URL 喂给 `Image`，
 * 省掉 `atob`；而音频必须解出真正的字节才能塞进 `EncodedAudioChunk`。
 * 所以这里不能复用视频那个函数。
 */
export function audioPayloadToBytes(raw: unknown): Uint8Array | null {
  if (raw instanceof Uint8Array) return raw.length === 0 ? null : raw

  if (Array.isArray(raw)) {
    const bytes = new Uint8Array(raw as number[])
    return bytes.length === 0 ? null : bytes
  }

  if (typeof raw !== 'string' || raw.length === 0) return null

  try {
    const binary = atob(raw)
    const bytes = new Uint8Array(binary.length)

    for (let i = 0; i < binary.length; i += 1) bytes[i] = binary.charCodeAt(i)

    return bytes.length === 0 ? null : bytes
  } catch {
    // atob 对畸形 base64 直接抛。单段坏数据不该让整路音频停下来。
    return null
  }
}

// ═════════════════════════════════════════════════════════════════════
// 音频播放
// ═════════════════════════════════════════════════════════════════════

export interface AudioStats {
  /** 采样率 / 声道数（来自被控端，解码前必须知道）。 */
  sampleRate: number
  channels: number
  /** 送进解码器的段数。 */
  submitted: number
  /** 解码出结果并已安排播放的段数。 */
  scheduled: number
  /** 因为"已经过了该响的时刻"而被丢掉的段数（正常为 0）。 */
  lateSkipped: number
  /** 解码失败的段数。 */
  decodeErrors: number
  /** 排不上队、直接超出 1 秒仍没响的段数（worklet 报的"饿死"次数）。 */
  underruns: number
  /** 时间轴上被补了静音的帧数（丢包）。 */
  gapFrames: number
  /** 因为迟到而被丢掉的头部帧数。 */
  staleFrames: number
  /** 正在响的那一段在**会话单调轴**上的位置（毫秒）；还没有则为 `null`。 */
  playheadMonoMs: number | null
  /** 上面那个值的**读取时刻**（本机 `performance.now()`）—— 用它才能把播放头外推到"此刻"。 */
  playheadAtPerf: number
  /** 是否已经真的响过了（没响过就不要对外宣称"音频正常"）。 */
  audible: boolean
}

export type AudioStartResult = { ok: true } | { ok: false; reason: string }

/**
 * Opus → PCM → 扬声器。
 *
 * <p>
 * 链路：`AudioDecoder`（WebCodecs，解码裸 Opus 包）→ 每声道一段 `Float32Array`
 * → 转移给 AudioWorklet 的环形缓冲 → 音频线程按绝对帧号输出。
 * </p>
 *
 * <p>
 * ⚠️ <b>`AudioDecoder` 与 `AudioWorklet` 都只在**安全上下文**里存在</b>
 * （https 或 http://localhost）。局域网里按 IP 访问 http:// 时，
 * `window.AudioDecoder` <b>是 undefined</b> —— 不是"报错"，而是"这东西不存在"。
 * 所以 {@link start} 必须先判存在性，把结论说成一句人话，
 * 而不是等一个 `TypeError: AudioDecoder is not a constructor` 掉在控制台里。
 * </p>
 */
export class RemoteAudioPlayer {
  private context: AudioContext | null = null
  private decoder: AudioDecoder | null = null
  private node: AudioWorkletNode | null = null

  private sampleRate = 0
  private channels = 0

  private readonly clock: SessionClock

  /** 每段音频的"墙钟采集时刻"，按会话单调毫秒索引 —— 只用于测端到端延迟。 */
  private readonly unixByMono = new Map<number, number>()

  private statsState: AudioStats = {
    sampleRate: 0,
    channels: 0,
    submitted: 0,
    scheduled: 0,
    lateSkipped: 0,
    decodeErrors: 0,
    underruns: 0,
    gapFrames: 0,
    staleFrames: 0,
    playheadMonoMs: null,
    playheadAtPerf: 0,
    audible: false,
  }

  /** 上一次报出来的读游标（worklet 那边报的是绝对帧号）。 */
  private workletFrame: number | null = null

  /** 主线程记的"最后一段排到哪一帧"，用于判断"到底出过声没有"。 */
  private workletFrameMemory = 0

  private running = false

  constructor(clock: SessionClock) {
    this.clock = clock
  }

  get stats(): AudioStats {
    return { ...this.statsState }
  }

  get isRunning(): boolean {
    return this.running
  }

  /** 这一路音频的采样率 / 声道数（解码前必须知道；没开始则为 0）。 */
  get format(): { sampleRate: number; channels: number } {
    return { sampleRate: this.sampleRate, channels: this.channels }
  }

  /**
   * 建起整条播放链。可以在第一段音频到达时再调 —— 那时才知道采样率与声道数。
   *
   * @returns 失败时给出**一句人话**，直接可以显示给用户。
   */
  async start(sampleRate: number, channels: number): Promise<AudioStartResult> {
    if (this.running) {
      // 采样率变了（理论上不会）就重建：录音设备被换过是可能的。
      if (this.sampleRate === sampleRate && this.channels === channels) return { ok: true }

      this.stop()
    }

    // ── ① 先判"这些 API 到底在不在" ──
    //
    // 🔥 顺序很重要：不安全上下文里 AudioDecoder 是 undefined，
    //    和"浏览器太老"是**两件处置完全不同的事**（一个去开 https，一个去换浏览器）。
    if (typeof window !== 'undefined' && !window.isSecureContext) {
      return {
        ok: false,
        reason:
          '当前页面不是安全上下文（用 http + 局域网 IP 打开），浏览器在这种页面上禁用了 ' +
          'WebCodecs 与 AudioWorklet —— 音频没法解码。请改用 https 打开本页（第一次需先安装根证书）。',
      }
    }

    if (typeof AudioDecoder === 'undefined') {
      return {
        ok: false,
        reason: '这个浏览器没有 WebCodecs 音频解码（需要 Chrome / Edge 94 及以上）。',
      }
    }

    if (typeof AudioContext === 'undefined') {
      return { ok: false, reason: '这个浏览器不支持 Web Audio。' }
    }

    try {
      // 🔥 **显式指定 48000**：Opus 解码出来就是 48 kHz，而设备的混音格式可能是 44.1 kHz。
      //    不指定的话，音频上下文按 44.1 kHz 取数据，而我们写进去的是 48 kHz 的采样 ——
      //    结果是**整条音频快了 8.8%**（音调偏高、越跑越超前），
      //    而所有的时间计算都是对的，看日志永远看不出来。
      //    让上下文跑在 48 kHz、由浏览器去重采样到设备，是唯一不引入这层错位做法的做法。
      const context = new AudioContext({ sampleRate })
      const actual = context.sampleRate

      if (Math.abs(actual - sampleRate) > 1) {
        await context.close().catch(() => undefined)

        return {
          ok: false,
          reason:
            `浏览器没有按 ${sampleRate} Hz 建立音频上下文（实际 ${actual} Hz）。` +
            '继续播放会让声音音调不对，所以这里停下不用它 —— 请换 Chrome / Edge 再试。',
        }
      }

      await context.audioWorklet.addModule('/remote-audio-worklet.js')

      const node = new AudioWorkletNode(context, 'remote-audio-ring', {
        numberOfInputs: 0,
        numberOfOutputs: 1,
        outputChannelCount: [channels],
        channelCount: channels,
        channelCountMode: 'explicit',
        processorOptions: { sampleRate, channels },
      })

      node.port.onmessage = (event) => this.onWorkletMessage(event.data)
      node.connect(context.destination)

      const decoder = new AudioDecoder({
        output: (data) => this.onDecoded(data),
        error: (error) => {
          this.statsState.decodeErrors += 1

          // 解码器报错之后它自己就废了（状态变成 closed）。如实记下来，
          // 由上层决定要不要重建 —— 这里不偷偷重建，因为重建之后前端完全看不出来
          // "音频中途断过一次"。
          void error
        },
      })

      // ⚠️ **不传 `description`**：按 WebCodecs 的 Opus 注册规范，
      //    传了 description 会被当成 **Ogg 封装**，而不传才是"裸 Opus 包"。
      //    被控端送过来的正是裸包（Concentus 直接编出来的），
      //    一旦误传一个 OpusHead，解码器会按 Ogg 去拆，然后一直解不出东西 ——
      //    症状是"一段错误都不报，就是没有声音"。
      decoder.configure({ codec: REMOTE_AUDIO_CODECS.opus, sampleRate, numberOfChannels: channels })

      this.context = context
      this.node = node
      this.decoder = decoder
      this.sampleRate = sampleRate
      this.channels = channels
      this.running = true
      this.workletFrame = null

      this.statsState = {
        ...this.statsState,
        sampleRate,
        channels,
        submitted: 0,
        scheduled: 0,
        lateSkipped: 0,
        decodeErrors: 0,
        underruns: 0,
        gapFrames: 0,
        staleFrames: 0,
        playheadMonoMs: null,
        playheadAtPerf: 0,
        audible: false,
      }

      return { ok: true }
    } catch (error) {
      this.stop()

      return {
        ok: false,
        reason: `音频播放链建立失败：${error instanceof Error ? error.message : String(error)}`,
      }
    }
  }

  /**
   * 收一段音频：解码 + 排期播放。
   *
   * <p>
   * ⚠️ 与视频那条路**相反**：这里<b>不丢帧</b>。视频"忙就丢最新"是对的（人眼看不出少一帧），
   * 而音频丢一段就是一个可听见的断点 —— 所以宁可让它晚一点响，也不丢。
   * 只有"晚到已经超过一整帧"时才放弃这一块（那时响出来反而会和后一块叠在一起）。
   * </p>
   */
  push(frame: RemoteAudioFrame): void {
    if (!this.running || !this.decoder || this.decoder.state !== 'configured') return

    const header = frame.header

    if (!header) return

    // 采样率/声道数与建链时不一致：只有第一段会走到这里（`start` 是用它建的）。
    if (header.sampleRate !== this.sampleRate || header.channels !== this.channels) return

    const bytes = audioPayloadToBytes(frame.data)

    if (bytes === null) return

    const mono = header.capturedMonoMs

    if (!Number.isFinite(mono)) return

    // 记下这一段的墙钟时刻，供"正在响的是多久以前的声音"用。
    // ⚠️ 这个 Map 必须**有界**：解码器的输出与提交一一对应，正常时它最多留几十项；
    //    万一解码器把某一段吞了，不做上限就是一条稳定的内存泄漏。
    if (Number.isFinite(header.capturedUnixMs)) this.unixByMono.set(mono, header.capturedUnixMs)

    if (this.unixByMono.size > 256) {
      const oldest = this.unixByMono.keys().next()

      if (!oldest.done) this.unixByMono.delete(oldest.value)
    }

    try {
      this.decoder.decode(
        new EncodedAudioChunk({
          // 音频每一段都是随机访问点（Opus 的每一包都独立可解）。
          type: 'key',
          // ⚠️ 时间戳用**会话单调毫秒 → 微秒**，而不是墙钟：
          //    它会被原样带回到 `AudioData.timestamp` 上，是"这一段是哪一刻采的"
          //    唯一能穿过解码器的字段（解码器不保证输出顺序以外的东西）。
          timestamp: Math.round(mono * 1000),
          duration: Math.round((header.samplesPerFrame / header.sampleRate) * 1_000_000),
          data: bytes,
        }),
      )

      this.statsState.submitted += 1
    } catch (error) {
      // decode() 在队列满 / 已关闭时抛。记一笔，别让整路停下来。
      this.statsState.decodeErrors += 1
      void error
    }
  }

  /** 解码结果：拷成每声道一段，按会话时刻排期后转移给 worklet。 */
  private onDecoded(data: AudioData): void {
    try {
      const context = this.context
      const node = this.node

      if (!context || !node) return

      const channels = Math.min(this.channels, data.numberOfChannels)
      const frames = data.numberOfFrames
      const mono = data.timestamp / 1000

      if (frames <= 0 || channels <= 0) return

      const planes: Float32Array[] = []

      for (let c = 0; c < channels; c += 1) {
        const plane = new Float32Array(frames)

        // `f32-planar` 是最省事的目标格式：WebAudio 的 AudioBuffer / AudioWorklet
        // 收的就是它，转换由浏览器（可能带 SIMD）完成，比我们在 JS 里手动交织快得多。
        data.copyTo(plane, { planeIndex: c, format: 'f32-planar' })

        planes.push(plane)
      }

      const targetPerf = this.clock.toPerf(mono)

      // 锚点还没建立时直接按"现在"排：宁可早一点出声，也别憋着不出。
      // ⚠️ 这一支刻意**不计数**：它不是"丢了一段"，只是"第一个观测还没到"。
      //    混进 lateSkipped 会让页面上显示"已丢弃 1 段"，而其实一点问题都没有。
      // ⚠️ **每一段现读**（M7.16）：预算随目标帧率变（`playbackLatency.ts`），
      //    缓存一份副本会让"改了帧率之后声音还按老预算排"，而那正是 A/V 慢慢错开的起点。
      const desiredPerf =
        targetPerf === null ? performance.now() : targetPerf + getPlaybackLatencyMs()

      // 🔥 已经过了该响的时刻怎么办：**只在这种情况下才放弃这一段**。
      //    阈值取"一帧的时长"（20 ms）是刻意的 —— 晚不到一帧就让它挤进去响，
      //    晚过一帧再响就会和后一段叠一起（听感是"重音"），那比丢一段更糟。
      const lateMs = performance.now() - desiredPerf
      const frameMs = (frames / this.sampleRate) * 1000

      if (lateMs > frameMs) {
        this.statsState.lateSkipped += 1
        return
      }

      // 换算成音频上下文时间轴上的绝对帧号。
      // ⚠️ 这里用 `context.currentTime + delta` 而不是记住一个偏移量：
      //    每次现算，主线程与音频时钟之间那点缓慢漂移就自然被吃掉了。
      const whenSec = context.currentTime + Math.max(0, (desiredPerf - performance.now()) / 1000)
      const atFrame = Math.round(whenSec * this.sampleRate)

      this.workletFrameMemory = atFrame + frames

      node.port.postMessage(
        { type: 'push', frame: atFrame, channels: planes },
        planes.map((p) => p.buffer),
      )

      this.statsState.scheduled += 1
    } catch (error) {
      this.statsState.decodeErrors += 1
      void error
    } finally {
      // ⚠️ **必须关**：`AudioData` 背后往往是一块 GPU/原生缓冲，
      //    不关的话解码很快就会因为配额耗尽而彻底停下 ——
      //    表现是"放了几十秒之后突然没声音了"，而所有计数看起来都正常。
      data.close()
    }
  }

  private onWorkletMessage(msg: unknown): void {
    if (!msg || typeof msg !== 'object') return

    const payload = msg as { type?: string; frame?: number; underruns?: number; gaps?: number; stale?: number }

    if (payload.type !== 'progress') return

    if (typeof payload.frame === 'number') this.workletFrame = payload.frame
    if (typeof payload.underruns === 'number') this.statsState.underruns = payload.underruns
    if (typeof payload.gaps === 'number') this.statsState.gapFrames = payload.gaps
    if (typeof payload.stale === 'number') this.statsState.staleFrames = payload.stale

    const frame = this.workletFrame

    if (frame === null || this.sampleRate <= 0) return

    // 读游标（音频上下文时间轴上的绝对帧）→ 本机 perf 域 → 会话单调域。
    //
    // ⚠️ 这一路换算刻意**不**去记"某一段的 mono 起点"再线性外推，
    //    而是把"音频上下文时间"与"本机 perf 时间"都当成真实时钟来用：
    //    `frame / sampleRate` 就是这一段在上下文时间轴上响的时刻，
    //    再把上下文时间轴换到 perf 轴（两者都是本机时钟，只差一个缓慢漂移的偏移）。
    //    这样做才能测出**真实的**播放位置，而不是"我们以为排到的位置"。
    const audioSec = frame / this.sampleRate
    const perfMs = (audioSec - this.context!.currentTime) * 1000 + performance.now()
    const monoMs = this.clock.nowMono(perfMs)

    if (monoMs === null) return

    this.statsState.playheadMonoMs = monoMs
    this.statsState.playheadAtPerf = performance.now()

    // "真的出过声"的判据：读游标越过我们排过的第一段。
    // 用 `scheduled > 0 && monoMs > 0` 而不是"排过队"：排过队但一直没响，
    // 页面上不能显示"音频正常"。
    if (this.statsState.scheduled > 0 && this.workletFrameMemory > 0) {
      this.statsState.audible = true
    }
  }

  /** 正在响的那一段的**墙钟**采集时刻（用于测端到端延迟）；没有则为 `null`。 */
  currentCaptureUnixMs(): number | null {
    const mono = this.statsState.playheadMonoMs

    if (mono === null) return null

    // 找最接近的那个（Map 是"会话单调毫秒 → 墙钟毫秒"）。
    let best: number | null = null
    let bestDelta = Number.POSITIVE_INFINITY

    for (const [key, value] of this.unixByMono) {
      const delta = Math.abs(key - mono)

      if (delta < bestDelta) {
        bestDelta = delta
        best = value
      }
    }

    // 差的太多说明那段已经不在表里了（被后进来的挤掉），不要硬凑一个数。
    return bestDelta <= 100 ? best : null
  }

  /** 拆掉整条链（切会话 / 关页面时调）。 */
  stop(): void {
    this.running = false

    try {
      this.node?.port.close()
      this.node?.disconnect()
    } catch {
      // 忽略：节点可能已经被上下文一起关掉了。
    }

    this.node = null

    try {
      if (this.decoder && this.decoder.state !== 'closed') this.decoder.close()
    } catch {
      // 忽略。
    }

    this.decoder = null

    const context = this.context
    this.context = null

    if (context) void context.close().catch(() => undefined)

    this.unixByMono.clear()
    this.workletFrame = null
    this.workletFrameMemory = 0
    this.statsState.audible = false
    this.statsState.playheadMonoMs = null
  }
}

// ═════════════════════════════════════════════════════════════════════
// 视频抖动缓冲
// ═════════════════════════════════════════════════════════════════════

/**
 * 一帧在缓冲里的状态。
 *
 * 🔥 **M5.4-b 第 8 步改过形状**：原来是 `url`（data URL）＋ `image`（`HTMLImageElement`），
 * 那是 **MJPEG 专用**的。换成 `raw`（原始帧，交给解码器）＋ `decoded`（解好的画面）之后，
 * 同一个缓冲才能同时服务 MJPEG 与 H.264 —— 而**排期逻辑（选帧/提前解/时钟观测）一行没改**。
 */
interface BufferedFrame {
  seq: number
  monoMs: number | null
  unixMs: number
  /** 原始帧，解码器从它取数据（MJPEG 拼 data URL，H.264 转 bytes 喂 `EncodedVideoChunk`）。 */
  raw: RemoteStreamFrame
  /** 已经交给解码器了。⚠️ **不表示解好了**（H.264 的输出是异步回调）。 */
  fed: boolean
  /** 交给解码器的本机时刻，用来算解码耗时。 */
  fedAt: number
  /** 解好的画面；`null` = 还没好（或解不出来）。 */
  decoded: DecodedFrame | null
  /** 这一帧不会再上屏了（被丢 / 解失败 / 参考链断了）。 */
  dropped: boolean
  /** 这一帧的解码耗时（毫秒）。它是"画面为什么比声音晚"最常见的那个原因。 */
  decodeMs: number
}

export interface VideoBufferStats {
  /** 队列里还有几帧。 */
  buffered: number
  /** 现在有几帧"喂进解码器了但还没出来"。 */
  decoding: number
  /** 因为队列满被挤掉的帧数（观看端自己的网速/解码跟不上）。 */
  overflow: number
  /**
   * 因为"还没解码时就已经过了该显示的时机"被放弃的帧数。
   *
   * ⚠️ **只对 MJPEG 会非零**：H.264 有参考链，帧必须按序全喂（见 {@link skipped}）。
   */
  stale: number
  /** 解码失败的帧数。 */
  decodeErrors: number
  /**
   * **解出来了、但轮到它时已经过了显示时机 ⇒ 没上屏的帧数**（M5.4-b 第 8 步新增）。
   *
   * <p>
   * 🔥 它是 H.264 那条路上**最常态的一个数** —— 那正是"**不丢帧、只丢上屏**"的代价：
   * 帧照序全喂（保参考链完整），但只有"到点的那一帧"被画上去。
   * </p>
   *
   * ⚠️ 它与 {@link overflow} / {@link stale} **不是一回事**，别加在一起：
   * 那两个是"因为跟不上而**没解**"（画面会跳一下），这个是"**解了，只是为了解码链才解的**"
   * （画面是连贯的，只是帧率被降下来了）。
   */
  skipped: number
  /**
   * 🔥 **根本没收到几帧**（M5.4-c① 步 4 新增）—— 序号跳变累计出来的**帧数**。
   *
   * <p>
   * 它与 {@link overflow}／{@link stale} 是**同一量纲**（都是"丢了几帧"，可以横向比大小），
   * 但**归属完全不同**，这正是它存在的全部理由：
   * </p>
   *
   * <table>
   *   <tr><th></th><th>谁丢的</th><th>该去查什么</th></tr>
   *   <tr><td>{@link overflow}／{@link stale}</td><td><b>我自己</b>（观看端）</td><td>本机 CPU、下行带宽、队列深度</td></tr>
   *   <tr><td>{@link gaps}</td><td><b>上游</b>（中心／链路）</td><td>中心那条容量 1 的 `Fanout`</td></tr>
   * </table>
   *
   * <p>
   * 🔥 <b>为什么它指向得这么准</b>：<b>被控端自己丢帧时序号是连续的</b> ——
   * 丢帧不占序号（序号的含义是"真的发出去了第几帧"），被控端本地的丢帧由帧头里的
   * `dropped` 字段回答。所以序号一旦不连续，那几帧就<b>只能是"离开被控端之后"丢的</b>。
   * ⇒ 在整条链路上，能"悄悄丢一帧"的只有中心那个<b>容量 1 ＋ `DropOldest`、且不回报</b>的
   * `Fanout`（见 `docs/M5.4c-实施方案.md` §2.1 第 ④ 跳）。
   * </p>
   *
   * <p>
   * ⚠️ 数的是<b>帧</b>不是<b>事件</b>：一次跳 5 个号会 +5，不会 +1。
   * 「断了几次」看 {@link FrameDecoderStats.resets}（但那个数也含队列溢出触发的重建，
   * 想知道"上游断的"就只能看这一个）。
   * </p>
   */
  gaps: number
  /** 该显示哪一帧**了**（会话单调轴毫秒）；时钟没就绪则为 `null`。 */
  playheadMonoMs: number | null
}

export type VideoPaint = (
  /** `HTMLImageElement`（MJPEG）或 `VideoFrame`（H.264/HEVC）—— 两者都能直接进 `drawImage`。 */
  source: CanvasImageSource,
  /** 画面**像素**尺寸（不是 CSS 尺寸）。 */
  size: { width: number; height: number },
  meta: { seq: number; monoMs: number | null; unixMs: number; decodeMs: number },
) => void

/**
 * 视频抖动缓冲（M5.3）。
 *
 * <p>
 * 🔥 <b>它替换的是 M5.1 的"收到就画"</b>。那种做法在单看画面时没问题，
 * 但它把<b>网络的抖动原样变成了画面的抖动</b>：一帧晚到 30 ms，它就在屏幕上多停 30 ms，
 * 而下一帧来了立刻跳过去。音画对齐在这种画面上根本无从谈起 ——
 * 画面根本就没有一个稳定的"我现在显示的是哪一刻"。
 * </p>
 *
 * <p>
 * 换成"按 {@link TARGET_LATENCY_MS} 排期显示"之后，画面才第一次有了
 * <b>可以用一句话说清楚的当前位置</b>：<c>锚点 + 该帧采集时刻 + 目标延迟</c>。
 * 音频用的是同一句话。剩下的偏差就是真被测出来的那部分。
 * </p>
 *
 * <p>
 * ⚠️ <b>它不做"到了该显示的那一刻"才开始的解码</b>，而是提前喂给解码器。
 * 解码是异步的，等到了点再开始解，画面就会稳定晚一个解码时长 ——
 * 那个偏移量在 4K 上是可观的。
 * </p>
 *
 * <p>
 * 🔥 <b>M5.4-b 第 8 步：这个类**不再知道任何编码格式的事**</b>。
 * 原来它内含"`new Image()` ＋ `image.decode()`"与"队列满就把最旧的丢掉"，
 * 后者对 MJPEG 成立、对 H.264 是<b>错的</b>（P 帧有参考链，丢一个后面全花）。
 * ⇒ 现在"解码"与"能不能丢还没解码的帧"都由注入的 {@link FrameDecoder} 回答，
 * 本类只保留**与编解码无关**的那三件事：按时钟选帧、提前喂、按策略丢。
 * </p>
 */
export class RemoteVideoBuffer {
  private readonly queue: BufferedFrame[] = []
  private readonly clock: SessionClock
  private readonly paint: VideoPaint
  private readonly decoder: FrameDecoder

  private timer: number | null = null
  private lastPaintedSeq = -1
  /**
   * **上一个"新"帧的序号**（M5.4-c① 步 4）—— 与 {@link lastPaintedSeq} **不是一回事**。
   *
   * <p>
   * ⚠️ 缺口检测<b>必须</b>用这一个，不能用 `lastPaintedSeq`：队列最多积压
   * {@link VIDEO_QUEUE_LIMIT} 帧没上屏，而 `lastPaintedSeq` 只在**上屏**时才前进 ——
   * 拿它当基线，队列里积了 5 帧就会把"正常到达"读成"跳了 5 个号"，于是每帧都重建解码器。
   * 那种故障的表现是**画面永远停在关键帧上**（一直在等下一个关键帧），而没有任何报错。
   * </p>
   */
  private lastReceivedSeq = -1
  private statsState: VideoBufferStats = {
    buffered: 0,
    decoding: 0,
    overflow: 0,
    stale: 0,
    decodeErrors: 0,
    skipped: 0,
    gaps: 0,
    playheadMonoMs: null,
  }

  /**
   * @param makeDecoder **按这一路的编码格式造解码器**（面板据 `header.codec` 选）。
   *   它拿到的回调是"某一帧解好了"，缓冲用它把画面挂回对应的 item 上。
   */
  constructor(clock: SessionClock, paint: VideoPaint, makeDecoder: FrameDecoderFactory) {
    this.clock = clock
    this.paint = paint
    this.decoder = makeDecoder((seq, decoded) => this.onDecoded(seq, decoded))
  }

  get stats(): VideoBufferStats {
    let decoding = 0

    for (const item of this.queue) {
      if (item.fed && !item.decoded && !item.dropped) decoding += 1
    }

    return { ...this.statsState, buffered: this.queue.length, decoding }
  }

  /**
   * **解码器这一侧**的统计。
   *
   * 🔥 刻意与 {@link stats} **分成两个对象**（而不是合并成一张表）：它们是两个量级的事 ——
   * 一个是"排期丢了几帧"（画面跳一下），另一个是"解码链断了几次"（画面花到下一个关键帧）。
   * 合成一张表，现场就会把"降帧"读成"解码故障"。
   */
  get decoderStats(): FrameDecoderStats {
    return { ...this.decoder.stats }
  }

  /** 这一路实际用的编码格式（由解码器回答，不由缓冲猜）。 */
  get codec(): string {
    return this.decoder.codec
  }

  /** 时钟不可用时（旧被控端没有 `capturedMonoMs`）退化成"收到就画"的判据。 */
  get hasClock(): boolean {
    return this.clock.ready
  }

  start(): void {
    if (this.timer !== null) return

    const tick = (): void => {
      this.tickOnce()
      this.timer = window.requestAnimationFrame(tick)
    }

    this.timer = window.requestAnimationFrame(tick)
  }

  stop(): void {
    if (this.timer !== null) window.cancelAnimationFrame(this.timer)

    this.timer = null
    this.clear()

    // ⚠️ **只有 `stop()` 关解码器，`clear()` 不关** —— 两者的语义不同：
    //    `stop()` 是"这一路看完了"（缓冲要被丢掉）；`clear()` 是"把队列清空重来"
    //    （缓冲还会被继续用，比如用户点一下"清空画面"）。在 `clear()` 里关掉解码器，
    //    之后所有帧都会静默地解不出来 —— 而画面只是"再也不动"，没有任何报错。
    this.decoder.close()
  }

  /** 清空队列与计数（切会话 / 重新开始时调）。**不关解码器**（见 {@link stop}）。 */
  clear(): void {
    for (const item of this.queue) release(item)

    this.queue.length = 0
    this.lastPaintedSeq = -1
    this.lastReceivedSeq = -1
    this.statsState = {
      buffered: 0,
      decoding: 0,
      overflow: 0,
      stale: 0,
      decodeErrors: 0,
      skipped: 0,
      gaps: 0,
      playheadMonoMs: null,
    }
  }

  push(frame: RemoteStreamFrame): void {
    const header = frame.header

    if (!header) return

    // 迟到的旧帧（重连后重发 / 乱序）：直接丢，别让它有机会盖掉新的。
    if (header.sequence <= this.lastPaintedSeq) return

    // 🔥 **序号体检**（M5.4-c① 步 4）—— 必须在入队**之前**跑：
    //    断链时要丢的是"队列里那些还没解的旧帧"，而**刚到的这一帧是要留下的**
    //    （它恰好是关键帧的话，画面就是从它接上的）。
    this.checkSequence(header.sequence)

    // 空载荷：直接丢。
    // ⚠️ 它原来长在 MJPEG 的 `toDataUrl` 里（返回 `null` 就丢弃）。挪到这里的理由不是整理：
    //    WebCodecs 喂一个 **0 字节**的 `EncodedVideoChunk` 会让解码器进 `error` 态，
    //    而那个态的表现是"**整条链断掉**"—— 远比"少一帧"严重。
    if (isEmptyPayload(frame)) return

    const mono = typeof header.capturedMonoMs === 'number' ? header.capturedMonoMs : null

    if (mono !== null) this.clock.observe(mono)

    // 队列满：**从最旧的丢**。这与人眼无关，纯粹是"观看端自己跟不上"，
    // 所以必须计数 —— 它与"被控端编不动"是两种故障，页面上要分开说。
    while (this.queue.length >= VIDEO_QUEUE_LIMIT) {
      const evicted = this.queue.shift()

      if (!evicted) break

      const neverFed = !evicted.fed

      release(evicted)
      this.statsState.overflow += 1

      // 🔥 **参考链断了**：被挤掉的这一帧**还没喂给解码器**，而这一路的解码器
      //    **不能丢未解码帧**（H.264/HEVC）⇒ 后面那些 P 帧缺参考，解出来必然是花的。
      //    ⇒ 明确重建解码器、等下一个关键帧（见 `breakChain`）。
      if (neverFed && !this.decoder.canDropUndecoded) this.breakChain()
    }

    this.queue.push({
      seq: header.sequence,
      monoMs: mono,
      unixMs: header.capturedUnixMs,
      raw: frame,
      fed: false,
      fedAt: 0,
      decoded: null,
      dropped: false,
      decodeMs: 0,
    })
  }

  /**
   * **参考链断了** —— 两个触发源，处置相同：
   *
   * <table>
   *   <tr><th>触发源</th><th>谁丢的</th><th>进这里的调用点</th></tr>
   *   <tr><td>队列满是挤掉了一帧**还没喂**的</td><td>观看端自己</td><td>{@link push} 里那个 `while`</td></tr>
   *   <tr><td>序号出现缺口（M5.4-c① 步 4）</td><td>中心／链路</td><td>{@link checkSequence}</td></tr>
   * </table>
   *
   * 🔥 处置是"**重建解码器、等下一个关键帧**"，而不是"继续按原样解下去"。
   * 这个选择本身就是最容易搞错的地方之一：继续解**也会出画面**，
   * 只是从断点开始一路花到下一个关键帧 —— 而那个画面看起来像"编码器坏了"，
   * 于是人会去查编码参数，而真因是"参考帧压根就没到"。
   */
  private breakChain(): void {
    this.decoder.reset()

    // 队列里"还没解出来、且不是关键帧"的，参考链已经没了 ⇒ 一起丢，别让它们再上屏。
    // ⚠️ 已经 `fed` 的那些，解码器之后仍会把输出送回来；`onDecoded` 会看到 `dropped`
    //    并为它释放资源（`VideoFrame` 不释放就是漏显存）。
    for (const item of this.queue) {
      if (item.dropped || item.decoded) continue
      if (item.raw.header?.isKeyframe) continue

      item.dropped = true
      this.statsState.skipped += 1
    }
  }

  /**
   * 🔥 **序号体检**：这一帧与上一帧之间有没有缺口？有 ⇒ 参考链已经断了，立刻自愈。
   *
   * <p>
   * 这是 M5.4-c① 步 4 治的那个洞：中心那条<b>容量 1 ＋ `DropOldest`、且不回报</b>的
   * `Fanout` 会悄悄丢帧（见 `docs/M5.4c-实施方案.md` §2.1 第 ④ 跳）。丢了之后
   * 观看端缺一个参考帧继续解 ⇒ 画面一路花到下一个关键帧，而<b>没有任何一层知道</b> ——
   * 能看见这件事的线索只剩下序号本身。
   * </p>
   *
   * <p>
   * 🔥 <b>判据是准的</b>：被控端自己丢帧时<b>序号是连续的</b>（丢帧不占序号，
   * 本地丢帧由帧头里的 `dropped` 回答 —— 那是实测结论，见 `RemoteStreamHost.cs`
   * 里 `OversizeGiveUp` 那段注释）。所以缺口只可能出现在<b>离开被控端之后</b>：中心或链路。
   * </p>
   *
   * <p>
   * ⚠️ 数的是<b>帧</b>不是<b>事件</b>：跳 5 个号记 5。一次缺口只重建一次解码器
   * （缺口之后连续到达的帧不再触发）—— 这一条由"基线只在序号**前进**时更新"保证，
   * 而它同时让"重复到达／乱序"这两种（序号没有前进的）帧自然地不触发任何东西。
   * </p>
   */
  private checkSequence(sequence: number): void {
    const previous = this.lastReceivedSeq

    // ⚠️ 第一帧**永远不算**跳变：订阅时中心会先把最近一帧给过来，而序号是
    //    被控端一路发下来的号（未必从 0 开始）。把它读成"跳了 977 个号"，
    //    会让每一个新订阅者一进来就重建一次解码器 —— 那等于延迟到下一个关键帧才出画面。
    if (previous >= 0 && sequence > previous + 1) {
      this.statsState.gaps += sequence - previous - 1

      // 🔥 只有"有参考链"的解码器才需要重建。MJPEG 逐帧独立，缺一帧就是缺一帧 ——
      //    重建它反而白白丢掉队列里那些**还能正常解**的帧。
      if (!this.decoder.canDropUndecoded) this.breakChain()
    }

    if (sequence > previous) this.lastReceivedSeq = sequence
  }

  /** 一个 rAF 周期：先决定该显示哪一帧，再顺手把解码提前做掉。 */
  private tickOnce(): void {
    const now = performance.now()
    const nowMono = this.clock.nowMono(now)

    // 时钟不可用（旧被控端）：退回"收到就画最新的"，并在页面上说明无法对齐。
    if (nowMono === null) {
      this.statsState.playheadMonoMs = null
      this.pumpDecode(null)
      this.paintNewest(now)
      return
    }

    // ⚠️ 与声音读的是**同一个**函数（`playbackLatency.ts` 里的单值），
    //    所以"两条流按同一个延迟排期"这条对齐依据在 M7.16 之后仍然成立。
    const playhead = nowMono - getPlaybackLatencyMs()

    this.statsState.playheadMonoMs = playhead

    // 找出"该显示的最后那一帧"：`monoMs <= playhead` 里最新的一个。
    let chosen = -1

    for (let i = 0; i < this.queue.length; i += 1) {
      const mono = this.queue[i].monoMs

      if (mono !== null && mono <= playhead) chosen = i
    }

    if (chosen >= 0) {
      const item = this.queue[chosen]

      // 🔥 **H.264 的"丢"就发生在这里**：`chosen` 之前那些（已经为了参考链解出来的）
      //    帧会在 `dropUpTo` 里被丢掉、并计进 `skipped`。它们**该解的已经解过了**，
      //    只是没轮到上屏 —— 这正是"不丢帧、只丢上屏"，也是它为什么不花屏。
      if (item.decoded) {
        this.doPaint(item, performance.now())
        this.dropUpTo(chosen)
      }
      // 还没解好就等下一次 rAF —— 解码已经在路上了（见 pumpDecode）。
    }

    this.pumpDecode(playhead)
  }

  /**
   * 提前喂帧。从最旧的没喂的开始，**上限由解码器自己给**（`FrameDecoder.maxInFlight`）。
   *
   * 🔥 **上限刻意不写在排期层**：MJPEG 是 2（每帧一次真异步解码，喂多了只是白烧），
   * 而 H.264 是**不设限** —— 它的 `decode()` 入队即返回、输出走回调，
   * 解码器内部要缓存若干帧才有第一帧输出；限深会在**第一帧上死锁**。
   */
  private pumpDecode(playhead: number | null): void {
    let inFlight = 0

    for (const item of this.queue) {
      if (item.fed && !item.decoded && !item.dropped) inFlight += 1
    }

    for (const item of this.queue) {
      if (inFlight >= this.decoder.maxInFlight) break
      if (item.fed || item.decoded || item.dropped) continue

      // 已经过了该显示的时机太多：不解了（解出来也没机会上屏），但要计数 ——
      // 这个数说的是"观看端解码跟不上"，而不是"网络慢"。
      //
      // 🔥 **只有"能丢未解码帧"的解码器（MJPEG）才做这个判断。**
      //    H.264/HEVC 有参考链，丢一个 P 帧会让后面全花到下一个关键帧
      //    ⇒ 它们的帧必须**按序全喂**，"丢"只能发生在 `tickOnce` 的上屏那一层。
      if (
        this.decoder.canDropUndecoded &&
        playhead !== null &&
        item.monoMs !== null &&
        item.monoMs < playhead - VIDEO_STALE_MS
      ) {
        item.dropped = true
        this.statsState.stale += 1
        continue
      }

      item.fed = true
      item.fedAt = performance.now()
      inFlight += 1

      // ⚠️ **不 await**：H.264 的解码器会内部缓存若干帧才有第一帧输出，
      //    "喂一帧、等它出来再喂下一帧"会在第一帧上**死锁**（见 `frameDecoder.ts`）。
      this.decoder.feed(item.raw)
    }
  }

  /**
   * 某一帧解好了（或解不出来）—— 由解码器的回调进来。
   *
   * ⚠️ 它可能是**任意一帧**，而且可能在缓冲已经被清空 / 会话已切走之后才到，
   * 那时队列里已经没有它了。**必须为它释放资源**，否则 H.264 的 `VideoFrame` 会漏显存 ——
   * 症状是"看一会儿就卡"，而日志一片安静（漏的是 GPU 内存，不是 JS 堆，
   * GC 不会帮你收，也不会有任何异常）。
   */
  private onDecoded(seq: number, decoded: DecodedFrame | null): void {
    let item: BufferedFrame | undefined

    for (const candidate of this.queue) {
      if (candidate.seq === seq) {
        item = candidate
        break
      }
    }

    if (!item || item.dropped) {
      // 队列里已经没有它了（被挤掉 / 已被切走清空 / 参考链断了）⇒ 解出来的直接还回去。
      decoded?.release()
      return
    }

    if (decoded === null) {
      item.dropped = true
      this.statsState.decodeErrors += 1

      // 单帧坏图**不该**让整路停下来：记一句、继续等下一帧。
      // 一断流就会掉进"要重新建会话"的坑，而重建被控端的抓屏是有成本的。
      return
    }

    item.decoded = decoded
    item.decodeMs = Math.max(0, performance.now() - item.fedAt)
  }

  /** 时钟不可用时：把队列里最新的那一帧画上去（M5.1 的老行为）。 */
  private paintNewest(nowPerf: number): void {
    for (let i = this.queue.length - 1; i >= 0; i -= 1) {
      if (this.queue[i].decoded) {
        this.doPaint(this.queue[i], nowPerf)
        this.dropUpTo(i)
        return
      }
    }
  }

  private doPaint(item: BufferedFrame, nowPerf: number): void {
    const decoded = item.decoded

    if (!decoded || item.seq <= this.lastPaintedSeq) return

    this.lastPaintedSeq = item.seq

    this.paint(
      decoded.source,
      { width: decoded.width, height: decoded.height },
      {
        seq: item.seq,
        monoMs: item.monoMs,
        unixMs: item.unixMs,
        decodeMs: item.decodeMs,
      },
    )
  }

  /** 把 `index` 之前（不含）的都丢掉 —— 它们已经没有机会再上屏了。 */
  private dropUpTo(index: number): void {
    for (let i = 0; i < index; i += 1) {
      const item = this.queue[i]

      // 🔥 "**解出来了、但没轮上上屏**" —— H.264 那条路上这是**常态**
      //    （"不丢帧、只丢上屏"的代价）。它与 `overflow`/`stale` 是两回事：
      //    那两个是"因为跟不上而**没解**"，这个是"**解了，只是为了解码链才解的**"。
      if (item.decoded && !item.dropped) this.statsState.skipped += 1

      release(item)
    }

    this.queue.splice(0, index)
  }
}

/**
 * 这一帧占的资源还回去。**幂等**（同 `DecodedFrame.release` 的合同）：
 * 同一帧可能先被 `dropUpTo` 丢掉，之后又被 `clear()` 走到一次。
 */
function release(item: BufferedFrame): void {
  item.dropped = true

  if (item.decoded) {
    // `VideoFrame` 不 `close()` 就是漏显存；MJPEG 的 `release()` 是空操作（GC 负责）。
    // ⚠️ 排期层**不问**"哪种解码结果需要释放" —— 那是解码器的事（见 `mjpegFrame`）。
    item.decoded.release()
    item.decoded = null
  }
}

/** 这一帧有没有载荷。空载荷直接丢（理由见 `push` 里那段）。 */
function isEmptyPayload(frame: RemoteStreamFrame): boolean {
  const raw: unknown = frame.data

  if (typeof raw === 'string') return raw.length === 0
  if (raw instanceof Uint8Array || Array.isArray(raw)) return raw.length === 0

  // 认不出的形状：当作"没有载荷"。⚠️ 与 §2 铁律同族：**未知一律当作"没有"**，
  // 而不是"大概有" —— 后者会让一个空 chunk 进到解码器里，把它打成 error 态。
  return true
}

// ⚠️ MJPEG 的"帧字节 → data URL"（`toDataUrl`）**已搬到 `mjpegDecoder.ts`**：
//    它是 MJPEG **独有**的知识（`data:image/jpeg;base64,` 这个头对 H.264 没有意义），
//    留在这里就等于排期层仍然知道"这一路是 JPEG"。
//    H.264 那条路反过来要付一次 `atob`（WebCodecs 要 `Uint8Array`），见 `h264Decoder.ts`。

// ═════════════════════════════════════════════════════════════════════
// 音画偏差
// ═════════════════════════════════════════════════════════════════════

/**
 * 音画偏差（毫秒）：**画面正在显示的那一刻** 减去 **声音正在响的那一刻**，
 * 两者都取会话单调轴上的位置。
 *
 * <p>
 * 🔥 <b>为什么两个位置都要"外推到此刻"再相减</b>：两者都是**离散**的 ——
 * 画面在两次上屏之间不动，声音在每一段之间不动。直接比"它们各自的最后一条记录"，
 * 差的就是"两次记录之间的时间差"，而这个量随采样时刻乱跳（视频 10–30 fps，
 * 音频约 37 次/秒），读出来会是一片噪声。把它们各自按真实时间**外推**到同一个时刻，
 * 差的才是真正的"这两条流此刻错开了多久"。
 * </p>
 *
 * @returns 正值 = 画面超前于声音；负值 = 声音超前于画面；缺一边则 `null`。
 */
export function audioVideoOffsetMs(
  video: { monoMs: number | null; atPerf: number },
  audio: { monoMs: number | null; atPerf: number },
  nowPerf = performance.now(),
): number | null {
  if (video.monoMs === null || audio.monoMs === null) return null
  if (video.atPerf <= 0 || audio.atPerf <= 0) return null

  const videoNow = video.monoMs + (nowPerf - video.atPerf)
  const audioNow = audio.monoMs + (nowPerf - audio.atPerf)

  return videoNow - audioNow
}
