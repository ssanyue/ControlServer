/**
 * `RemoteVideoBuffer` 的用例（M5.4-b §8 第 8 步）。
 *
 * <p>
 * 🔥 <b>这一批钉的是本步的核心形状</b>：同一个抖动缓冲，接两种"丢帧语义相反"的解码器 ——
 * MJPEG（可以丢没解码的帧）与 H.264（**不可以**，丢了会花到下一个关键帧）。
 * 排期层如果按 MJPEG 那套去服务 H.264，产出就是花屏，
 * 而"花屏"在真机上与"编码参数错"长得一模一样 —— 所以只能靠用例分清楚。
 * </p>
 *
 * <p>
 * ⚠️ 时钟与 rAF 都是**手动驱动**的：`performance.now` 被桩住、`requestAnimationFrame`
 * 把回调存起来由用例决定何时跑。否则用例会依赖真实时间，变成一片噪声。
 * </p>
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { DecodedFrame, DecodedSink, FrameDecoder, FrameDecoderStats } from './frameDecoder'
import { RemoteVideoBuffer, SessionClock } from './remotePlayback'
import { REMOTE_CODECS, type RemoteStreamFrame } from './types'

// ─────────────────────────────────────────────────────────────
// 夹具
// ─────────────────────────────────────────────────────────────

let nowMs = 0
let rafQueue: Array<() => void> = []

beforeEach(() => {
  nowMs = 1_000
  rafQueue = []

  vi.spyOn(performance, 'now').mockImplementation(() => nowMs)

  vi.stubGlobal('window', {
    requestAnimationFrame: (cb: FrameRequestCallback) => {
      rafQueue.push(() => cb(0))

      return rafQueue.length
    },
    cancelAnimationFrame: () => undefined,
  })
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

/** 跑一轮已登记的 rAF 回调（`tickOnce` 会再登记一次）。 */
function tick(): void {
  const pending = rafQueue.splice(0, rafQueue.length)

  for (const cb of pending) cb()
}

function makeFrame(opts: {
  seq: number
  monoMs: number
  isKeyframe?: boolean
  codec?: string
}): RemoteStreamFrame {
  return {
    header: {
      sessionId: 's1',
      sequence: opts.seq,
      codec: opts.codec ?? REMOTE_CODECS.h264,
      isKeyframe: opts.isKeyframe ?? false,
      width: 2,
      height: 2,
      capturedUnixMs: 1_700_000_000_000,
      capturedMonoMs: opts.monoMs,
      dropped: 0,
      codecString: null,
      description: null,
    },
    data: btoa('x'),
  }
}

/** 一个可以手动交付结果的假解码器（两条丢帧语义都能装）。 */
class FakeDecoder implements FrameDecoder {
  readonly stats: FrameDecoderStats = { decoded: 0, resets: 0 }
  readonly fed: number[] = []

  constructor(
    private readonly sink: DecodedSink,
    readonly codec: string = REMOTE_CODECS.h264,
    readonly canDropUndecoded: boolean = false,
    readonly maxInFlight: number = Number.POSITIVE_INFINITY,
  ) {}

  feed(frame: RemoteStreamFrame): void {
    this.fed.push(frame.header.sequence)
  }

  reset(): void {
    this.stats.resets += 1
  }

  close(): void {
    // 有意为空。
  }

  /** 手动交付一帧解码结果（模拟解码器的输出回调）。 */
  deliver(seq: number): void {
    this.stats.decoded += 1
    this.sink(seq, {
      source: null as unknown as CanvasImageSource,
      width: 2,
      height: 2,
      release: () => undefined,
    })
  }

  /** 手动交付一次"解不出来"。 */
  fail(seq: number): void {
    this.sink(seq, null)
  }
}

interface Rig {
  buffer: RemoteVideoBuffer
  decoder: FakeDecoder
  painted: number[]
}

function makeRig(config?: {
  codec?: string
  canDropUndecoded?: boolean
  maxInFlight?: number
}): Rig {
  const clock = new SessionClock()
  const painted: number[] = []
  const rig: Partial<Rig> = { painted }

  const buffer = new RemoteVideoBuffer(
    clock,
    (_source, _size, meta) => {
      painted.push(meta.seq)
    },
    (sink: DecodedSink) => {
      const decoder = new FakeDecoder(
        sink,
        config?.codec ?? REMOTE_CODECS.h264,
        config?.canDropUndecoded ?? false,
        config?.maxInFlight ?? Number.POSITIVE_INFINITY,
      )

      rig.decoder = decoder

      return decoder
    },
  )

  buffer.start()
  rig.buffer = buffer

  return rig as Rig
}

// ─────────────────────────────────────────────────────────────
// 用例
// ─────────────────────────────────────────────────────────────

describe('丢帧语义：排期层按解码器给的两个标志分叉', () => {
  it('🔴 H.264：已经"过期"的帧**照样喂进去**（丢它会花到下一个关键帧）', () => {
    const rig = makeRig()

    // anchor 用第一帧钉死：now(1000) − mono(1400) = −400。
    rig.buffer.push(makeFrame({ seq: 1, monoMs: 1400, isKeyframe: true }))

    // 到 1100 时 playhead = 1500−60 = 1440 ⇒ seq2(mono 1000) 已经过期 440 ms。
    nowMs = 1100
    rig.buffer.push(makeFrame({ seq: 2, monoMs: 1000 }))

    tick()

    expect(rig.decoder.fed).toEqual([1, 2])
    expect(rig.buffer.stats.stale).toBe(0)
  })

  it('MJPEG：同一帧**直接丢掉**，并计进 `stale`（逐帧独立 ⇒ 丢它没关系）', () => {
    const rig = makeRig({ codec: REMOTE_CODECS.mjpeg, canDropUndecoded: true, maxInFlight: 2 })

    rig.buffer.push(makeFrame({ seq: 1, monoMs: 1400, isKeyframe: true }))
    nowMs = 1100
    rig.buffer.push(makeFrame({ seq: 2, monoMs: 1000 }))

    tick()

    expect(rig.decoder.fed).toEqual([1])
    expect(rig.buffer.stats.stale).toBe(1)
  })
})

describe('"不丢帧、只丢上屏"', () => {
  it('🔴 解出来了、但没轮上上屏的帧计进 `skipped`（H.264 的常态）', () => {
    const rig = makeRig()

    rig.buffer.push(makeFrame({ seq: 1, monoMs: 940 }))
    rig.buffer.push(makeFrame({ seq: 2, monoMs: 950 }))
    rig.buffer.push(makeFrame({ seq: 3, monoMs: 960 }))

    rig.decoder.deliver(1)
    rig.decoder.deliver(2)
    rig.decoder.deliver(3)

    // 到 1060 时 playhead = 960 ⇒ 该显示 seq3；seq1/seq2 解过但没上屏。
    nowMs = 1060
    tick()

    expect(rig.painted).toEqual([3])
    expect(rig.buffer.stats.skipped).toBe(2)
    expect(rig.buffer.stats.decodeErrors).toBe(0)
  })

  it('`skipped` 与 `overflow`/`stale` 是**不同**的数（一个是"没解"，一个是"解了没上屏"）', () => {
    const rig = makeRig()

    rig.buffer.push(makeFrame({ seq: 1, monoMs: 940 }))
    rig.buffer.push(makeFrame({ seq: 2, monoMs: 950 }))

    rig.decoder.deliver(1)
    rig.decoder.deliver(2)

    nowMs = 1060
    tick()

    expect(rig.buffer.stats.overflow).toBe(0)
    expect(rig.buffer.stats.stale).toBe(0)
    expect(rig.buffer.stats.skipped).toBe(1)
  })
})

describe('参考链断了（队列溢出 + 不能丢未解码帧）', () => {
  it('🔴 挤掉一帧"还没喂"的 ⇒ 重建解码器，并把队列里非关键帧一起丢', () => {
    const rig = makeRig()

    // 关键帧打头，后面全是 P 帧。**全程不 tick** ⇒ 一帧都没喂出去。
    rig.buffer.push(makeFrame({ seq: 1, monoMs: 1000, isKeyframe: true }))

    for (let seq = 2; seq <= 13; seq += 1) {
      rig.buffer.push(makeFrame({ seq, monoMs: 1000 + seq }))
    }

    // 第 13 帧进来时队列已满（上限 12）⇒ 挤掉 seq1（**从没喂过**）⇒ 参考链断。
    expect(rig.decoder.stats.resets).toBe(1)
    expect(rig.buffer.stats.overflow).toBe(1)
    // ⚠️ 是 **11**（seq2…seq12），不是 12：`breakChain` 跑在"挤掉之后、推入之前"，
    //    所以刚到的那一帧（seq13）不在被丢的那一批里 —— 它是**断链之后**的第一帧，
    //    如果它恰好是关键帧，画面就能从这里接上。
    expect(rig.buffer.stats.skipped).toBe(11)
  })

  it('MJPEG 挤掉没喂过的帧**不算**断链（没有参考链可断）', () => {
    const rig = makeRig({ codec: REMOTE_CODECS.mjpeg, canDropUndecoded: true, maxInFlight: 2 })

    for (let seq = 1; seq <= 13; seq += 1) {
      rig.buffer.push(makeFrame({ seq, monoMs: 1000 + seq }))
    }

    expect(rig.decoder.stats.resets).toBe(0)
    expect(rig.buffer.stats.overflow).toBe(1)
    expect(rig.buffer.stats.skipped).toBe(0)
  })
})

describe('收尾与重开', () => {
  it('`stop()` 会关解码器，`clear()` **不会**（后者还会被继续用）', () => {
    const rig = makeRig()
    const closeSpy = vi.spyOn(rig.decoder, 'close')

    rig.buffer.clear()
    expect(closeSpy).not.toHaveBeenCalled()

    rig.buffer.stop()
    expect(closeSpy).toHaveBeenCalledTimes(1)
  })

  it('`clear()` 把两组计数一起归零（含 `skipped` 与 `gaps`）', () => {
    const rig = makeRig()

    rig.buffer.push(makeFrame({ seq: 1, monoMs: 940 }))
    rig.buffer.push(makeFrame({ seq: 2, monoMs: 950 }))

    rig.decoder.deliver(1)
    rig.decoder.deliver(2)

    nowMs = 1060
    tick()

    expect(rig.buffer.stats.skipped).toBe(1)

    // 再制造一次缺口（缺 3）—— 顺带检查它对 `skipped` 没有副作用
    // （seq2 已经解好了，`breakChain` 不会去动已经解出来的帧）。
    rig.buffer.push(makeFrame({ seq: 4, monoMs: 970 }))

    expect(rig.buffer.stats.gaps).toBe(1)
    expect(rig.buffer.stats.skipped).toBe(1)

    rig.buffer.clear()

    expect(rig.buffer.stats.skipped).toBe(0)
    expect(rig.buffer.stats.gaps).toBe(0)
    expect(rig.buffer.stats.buffered).toBe(0)
    expect(rig.buffer.stats.overflow).toBe(0)
  })

  it('空载荷的帧直接丢掉（0 字节 chunk 会让解码器进 error 态）', () => {
    const rig = makeRig()
    const frame = makeFrame({ seq: 1, monoMs: 940 })

    frame.data = ''

    rig.buffer.push(frame)

    expect(rig.buffer.stats.buffered).toBe(0)
  })

  it('解码器这一侧的统计单独回读（`resets` 不与排期计数混在一起）', () => {
    const rig = makeRig()

    expect(rig.buffer.decoderStats.resets).toBe(0)

    rig.decoder.reset()

    expect(rig.buffer.decoderStats.resets).toBe(1)
  })
})

describe('序号跳变 ⇒ 参考链自愈（M5.4-c① 步 4）', () => {
  /**
   * 🔥 这组用例治的是**一条今天就在发生、而且没有任何一层知道**的故障：
   * 中心那条容量 1 的 `Fanout` 会悄悄丢帧（`DropOldest`，不回报），
   * 于是观看端缺一个参考帧继续解 —— 画面一路花到下一个关键帧，
   * 而"没有收到"这件事，除了序号之外**没有任何别的线索**。
   *
   * ⚠️ 之所以说判据是准的（不会误报被控端自己的丢帧）：**被控端的序号是连续的** ——
   * 它丢帧时不占序号（见 `RemoteStreamHost.cs` 里 `OversizeGiveUp` 那段注释，
   * 那里是实测出来的结论）。所以序号一旦不连续，缺的那几帧就只能是"离开被控端之后"丢的。
   */

  it('🔴 H.264：跳过一格 ⇒ 立刻重建解码器（不然会一路花到下一个关键帧）', () => {
    const rig = makeRig()

    rig.buffer.push(makeFrame({ seq: 1, monoMs: 1000, isKeyframe: true }))
    rig.buffer.push(makeFrame({ seq: 2, monoMs: 1010 }))
    rig.buffer.push(makeFrame({ seq: 4, monoMs: 1020 })) // 3 号没收到

    expect(rig.decoder.stats.resets).toBe(1)
    expect(rig.buffer.stats.gaps).toBe(1)
  })

  it('`gaps` 数的是**帧**（跳 3 个号 ⇒ +3），不是"跳了几次"', () => {
    const rig = makeRig()

    rig.buffer.push(makeFrame({ seq: 1, monoMs: 1000, isKeyframe: true }))
    rig.buffer.push(makeFrame({ seq: 5, monoMs: 1010 })) // 2/3/4 都没收到

    expect(rig.buffer.stats.gaps).toBe(3)
    expect(rig.decoder.stats.resets).toBe(1) // 一次缺口 = 一次重建
  })

  it('跳变之后紧接着的连续帧**不再**重复重建（缺 2/3/4 也只是一次断链）', () => {
    const rig = makeRig()

    rig.buffer.push(makeFrame({ seq: 1, monoMs: 1000, isKeyframe: true }))
    rig.buffer.push(makeFrame({ seq: 3, monoMs: 1010 })) // 缺 2
    rig.buffer.push(makeFrame({ seq: 4, monoMs: 1020 }))
    rig.buffer.push(makeFrame({ seq: 5, monoMs: 1030 }))

    expect(rig.decoder.stats.resets).toBe(1)
    expect(rig.buffer.stats.gaps).toBe(1)
  })

  it('MJPEG：序号跳变**不**重建解码器（逐帧独立，没有链可断）—— 但 `gaps` 照记', () => {
    const rig = makeRig({ codec: REMOTE_CODECS.mjpeg, canDropUndecoded: true, maxInFlight: 2 })

    rig.buffer.push(makeFrame({ seq: 1, monoMs: 1000, isKeyframe: true }))
    rig.buffer.push(makeFrame({ seq: 3, monoMs: 1010 }))

    expect(rig.decoder.stats.resets).toBe(0)
    expect(rig.buffer.stats.gaps).toBe(1)
  })

  it('第一帧永远不算跳变（订阅时中心先给最近一帧，序号未必从 0 开始）', () => {
    const rig = makeRig()

    rig.buffer.push(makeFrame({ seq: 977, monoMs: 1000, isKeyframe: true }))

    expect(rig.buffer.stats.gaps).toBe(0)
    expect(rig.decoder.stats.resets).toBe(0)
  })

  it('第一帧的序号就是基线（后一帧接着走 ⇒ 不算跳变）', () => {
    const rig = makeRig()

    rig.buffer.push(makeFrame({ seq: 977, monoMs: 1000, isKeyframe: true }))
    rig.buffer.push(makeFrame({ seq: 978, monoMs: 1010 }))

    expect(rig.buffer.stats.gaps).toBe(0)
    expect(rig.decoder.stats.resets).toBe(0)
  })

  it('🔴 队列积压时**不许**误报 —— 基线是"收到的号"，不是"画过的号"', () => {
    // 这是最像"顺手复用现成字段"的那种改法唯一的坑：`lastPaintedSeq` 只在**上屏**时才前进，
    // 而队列最多能积 12 帧 ⇒ 拿它当基线会把"正常到达"读成"跳了 11 个号"，
    // 于是每一帧都重建解码器 —— 画面永远停在关键帧上，且**没有任何报错**。
    const rig = makeRig()

    // 先让 seq1 真的上屏（`lastPaintedSeq` 变成 1）。
    rig.buffer.push(makeFrame({ seq: 1, monoMs: 940 }))
    rig.decoder.deliver(1)
    nowMs = 1060
    tick()

    // 再连着灌 10 帧，中间**一次都不 tick** ⇒ 它们全积在队列里，一帧都没轮到上屏。
    for (let seq = 2; seq <= 11; seq += 1) {
      rig.buffer.push(makeFrame({ seq, monoMs: 1000 + seq }))
    }

    expect(rig.buffer.stats.gaps).toBe(0)
    expect(rig.decoder.stats.resets).toBe(0)
  })

  it('🔴 断链时，队列里"还没解、且不是关键帧"的一起丢 —— 包括刚到的这一帧若是关键帧则留下', () => {
    const rig = makeRig()

    rig.buffer.push(makeFrame({ seq: 1, monoMs: 1000, isKeyframe: true }))
    rig.buffer.push(makeFrame({ seq: 2, monoMs: 1010 }))
    rig.buffer.push(makeFrame({ seq: 3, monoMs: 1020 }))
    rig.buffer.push(makeFrame({ seq: 6, monoMs: 1030, isKeyframe: true })) // 缺 4/5

    expect(rig.buffer.stats.gaps).toBe(2)
    expect(rig.decoder.stats.resets).toBe(1)
    // seq1/2/3 里，seq1 是关键帧（留下），seq2/seq3 被丢弃。
    expect(rig.buffer.stats.skipped).toBe(2)
    // 四帧都还在队列里 —— 包括刚到的那个关键帧（它是"接上"的那一帧，不许被自己触发的那次断链标掉）。
    expect(rig.buffer.stats.buffered).toBe(4)
  })

  it('重复到达的帧（序号没前进）不算跳变', () => {
    const rig = makeRig()

    rig.buffer.push(makeFrame({ seq: 1, monoMs: 1000, isKeyframe: true }))
    rig.buffer.push(makeFrame({ seq: 2, monoMs: 1010 }))
    rig.buffer.push(makeFrame({ seq: 2, monoMs: 1010 }))

    expect(rig.buffer.stats.gaps).toBe(0)
    expect(rig.decoder.stats.resets).toBe(0)
  })
})
