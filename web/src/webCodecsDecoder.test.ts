/**
 * `webCodecsDecoder` 的用例（M5.4-b §8 第 8 步）。
 *
 * <p>
 * 🔥 <b>为什么这一批前端用例值得单独引入测试基础设施</b>：本文件被测的那几条规则
 * 全都是"**错了不会报错、只会花屏或黑屏**"的那一类 ——
 * `configure()` 该不该重配、`codec` 串与 `description` 自不自洽、
 * 输出该配回哪一帧。肉眼在真机上分不出"参考链断"还是"参数集错"还是"丢帧策略错"，
 * 所以这几条**必须**钉在用例里。
 * </p>
 */

import { describe, expect, it } from 'vitest'

import type { DecodedFrame } from './frameDecoder'
import { REMOTE_CODECS, type RemoteStreamFrame } from './types'
import {
  base64ToBytes,
  buildDecoderConfig,
  chunkTimestampUs,
  createWebCodecsDecoder,
  needsReconfigure,
  takePendingFor,
  type EncodedVideoChunkLike,
  type PendingOutput,
  type VideoDecoderLike,
} from './webCodecsDecoder'

// ─────────────────────────────────────────────────────────────
// 夹具
// ─────────────────────────────────────────────────────────────

function makeFrame(opts: {
  seq: number
  codec?: string
  isKeyframe?: boolean
  codecString?: string | null
  description?: string | null
  monoMs?: number | null
  data?: string
}): RemoteStreamFrame {
  return {
    header: {
      sessionId: 's1',
      sequence: opts.seq,
      codec: opts.codec ?? REMOTE_CODECS.h264,
      isKeyframe: opts.isKeyframe ?? false,
      width: 1920,
      height: 1080,
      capturedUnixMs: 1_700_000_000_000,
      capturedMonoMs: opts.monoMs === undefined ? 1000 : opts.monoMs,
      dropped: 0,
      codecString: opts.codecString ?? null,
      description: opts.description ?? null,
    },
    data: opts.data ?? btoa('fake-bitstream'),
  }
}

/** 一个能驱动的假解码器：记录 configure/decode，并能"吐出"一帧。 */
class FakeVideoDecoder implements VideoDecoderLike {
  readonly configs: VideoDecoderConfig[] = []
  readonly chunks: EncodedVideoChunkLike[] = []
  resetCount = 0
  closed = false

  /** 公开是为了让用例能**主动吐一帧 / 报一次错**（模拟解码器的两个回调）。 */
  readonly init: VideoDecoderInit

  constructor(init: VideoDecoderInit) {
    this.init = init
  }

  configure(config: VideoDecoderConfig): void {
    this.configs.push(config)
  }

  decode(chunk: EncodedVideoChunkLike): void {
    this.chunks.push(chunk)
  }

  reset(): void {
    this.resetCount += 1
  }

  close(): void {
    this.closed = true
  }

  /** 模拟解码器吐出一帧（`timestamp` 是我们喂进去的那个）。 */
  emit(timestamp: number): void {
    this.init.output({
      timestamp,
      displayWidth: 1920,
      displayHeight: 1080,
      close: () => undefined,
    } as unknown as VideoFrame)
  }

  /** 模拟解码器进 `error` 态。 */
  fail(): void {
    this.init.error(new Error('boom') as unknown as DOMException)
  }
}

interface Harness {
  decoder: FakeVideoDecoder
  seen: Array<{ seq: number; decoded: DecodedFrame | null }>
}

/** 造一个带假解码器的被测对象。 */
function harness(codec: string = REMOTE_CODECS.h264): {
  feed: ReturnType<typeof createWebCodecsDecoder>['feed']
  subject: ReturnType<typeof createWebCodecsDecoder>
  state: Harness
} {
  const state: Harness = { decoder: null as unknown as FakeVideoDecoder, seen: [] }

  const subject = createWebCodecsDecoder(codec, (seq, decoded) => {
    state.seen.push({ seq, decoded })
  }, {
    createDecoder: (init) => {
      state.decoder = new FakeVideoDecoder(init)
      return state.decoder
    },
    createChunk: (init) => ({ type: init.type, timestamp: init.timestamp }),
  })

  return { feed: subject.feed, subject, state }
}

// ─────────────────────────────────────────────────────────────
// 纯函数
// ─────────────────────────────────────────────────────────────

describe('needsReconfigure', () => {
  it('没配过 ⇒ 必须配', () => {
    expect(needsReconfigure(null, { codecString: 'avc3.640032', description: null })).toBe(true)
  })

  it('这一对没变 ⇒ 不重配（重配会把参考帧一起清掉）', () => {
    const key = { codecString: 'avc3.640032', description: null }

    expect(needsReconfigure(key, { ...key })).toBe(false)
  })

  it('codec 串变了 ⇒ 重配', () => {
    expect(
      needsReconfigure(
        { codecString: 'avc3.640032', description: null },
        { codecString: 'avc3.4D0032', description: null },
      ),
    ).toBe(true)
  })

  it('description 变了（即使 codec 串没变）⇒ 重配', () => {
    // 🔥 这一条就是方案 §3.9 (h) 要求的那个规则：带外的某个关键帧解不出参数集时
    //    会**单独**退回带内 —— 那时 codec 串从 avc1 变 avc3、description 从有变没有。
    //    "只在第一帧 configure 一次"的写法在这里会花屏，而两端日志全正常。
    expect(
      needsReconfigure(
        { codecString: 'avc1.640032', description: 'AQAAAA==' },
        { codecString: 'avc1.640032', description: null },
      ),
    ).toBe(true)
  })

  it('尺寸变了（codec 串一模一样）⇒ 也要重配（M7.7）', () => {
    // 🔥 换屏 / 换分块时，profile 与 level 可能完全不变（同一个编码器），
    //    只有分辨率变了。不重配的话解码器仍按旧尺寸解 —— 画面被裁或拉伸，且不报错。
    expect(
      needsReconfigure(
        { codecString: 'avc3.640033', description: null, codedWidth: 1920, codedHeight: 1080 },
        { codecString: 'avc3.640033', description: null, codedWidth: 3840, codedHeight: 2160 },
      ),
    ).toBe(true)
  })
})

describe('buildDecoderConfig', () => {
  it('带内（avc3）⇒ 不带 description', () => {
    const built = buildDecoderConfig({ codecString: 'avc3.640032', description: null })

    expect(built.ok).toBe(true)

    if (!built.ok) return

    expect(built.config.codec).toBe('avc3.640032')
    expect(built.config.description).toBeUndefined()
  })

  it('带外（avc1）⇒ 带上 description 的字节', () => {
    const built = buildDecoderConfig({
      codecString: 'avc1.640032',
      description: btoa('avcC'),
    })

    expect(built.ok).toBe(true)

    if (!built.ok) return

    expect(built.config.description).toBeInstanceOf(Uint8Array)
    expect((built.config.description as Uint8Array).length).toBe(4)
  })

  it('🔴 avc1 声明带外却没有 description ⇒ 拒绝（不许拿一个错的封装去 configure）', () => {
    const built = buildDecoderConfig({ codecString: 'avc1.640032', description: null })

    expect(built.ok).toBe(false)
  })

  it('🔴 avc3 声明带内却带了 description ⇒ 拒绝', () => {
    const built = buildDecoderConfig({ codecString: 'avc3.640032', description: btoa('x') })

    expect(built.ok).toBe(false)
  })

  // ── M7.7：把真实编码尺寸告诉解码器（"4K 只显示四分之一"的那条路）──
  describe('codedWidth / codedHeight', () => {
    it('给了尺寸就必须原样写进 config（4K 要写 3840×2160，不许被默认成 1080p）', () => {
      const built = buildDecoderConfig({
        codecString: 'avc3.640032',
        description: null,
        codedWidth: 3840,
        codedHeight: 2160,
      })

      expect(built.ok).toBe(true)

      if (!built.ok) return

      expect(built.config.codedWidth).toBe(3840)
      expect(built.config.codedHeight).toBe(2160)
    })

    it('分块时给的是**这一块**的尺寸，不是整幅', () => {
      const built = buildDecoderConfig({
        codecString: 'avc3.640032',
        description: null,
        codedWidth: 1920,
        codedHeight: 2160,
      })

      if (!built.ok) return

      expect(built.config.codedWidth).toBe(1920)
    })

    it('没给 / 给了非法值 ⇒ 不写（让浏览器按码流自己判断，而不是传一个 0）', () => {
      const missing = buildDecoderConfig({ codecString: 'avc3.640032', description: null })

      if (!missing.ok) return

      expect(missing.config.codedWidth).toBeUndefined()
      expect(missing.config.codedHeight).toBeUndefined()

      const zeroed = buildDecoderConfig({
        codecString: 'avc3.640032',
        description: null,
        codedWidth: 0,
        codedHeight: 0,
      })

      if (!zeroed.ok) return

      expect(zeroed.config.codedWidth).toBeUndefined()
    })

    it('只给一半也不写（一个 0 会把某些实现整成"解不出来"）', () => {
      const built = buildDecoderConfig({
        codecString: 'avc3.640032',
        description: null,
        codedWidth: 3840,
      })

      if (!built.ok) return

      expect(built.config.codedWidth).toBeUndefined()
    })
  })

  it('HEVC 的两条前缀同样规则', () => {
    expect(buildDecoderConfig({ codecString: 'hev1.1.6.L93.B0', description: null }).ok).toBe(true)

    expect(buildDecoderConfig({ codecString: 'hvc1.1.6.L93.B0', description: null }).ok).toBe(false)
  })

  it('🔴 没有 codec 串 ⇒ 拒绝（不许手写一个"看起来对"的默认串）', () => {
    const built = buildDecoderConfig({ codecString: null, description: null })

    expect(built.ok).toBe(false)

    if (built.ok) return

    expect(built.reason).toContain('codec 串')
  })
})

describe('base64ToBytes / chunkTimestampUs', () => {
  it('base64 往返一致（含 >127 的字节）', () => {
    const original = new Uint8Array([0, 1, 127, 128, 200, 255])
    let binary = ''

    for (const byte of original) binary += String.fromCharCode(byte)

    expect(Array.from(base64ToBytes(btoa(binary)))).toEqual(Array.from(original))
  })

  it('timestamp 用**真实时间**（单调毫秒 × 1000 ⇒ 微秒）', () => {
    const header = makeFrame({ seq: 3, monoMs: 1234 }).header

    expect(chunkTimestampUs(header)).toBe(1_234_000)
  })

  it('没有单调时刻 ⇒ 退回墙钟（**也是真实时间**，不许拿序号冒充）', () => {
    const header = makeFrame({ seq: 3, monoMs: null }).header

    expect(chunkTimestampUs(header)).toBe(1_700_000_000_000 * 1000)
  })
})

describe('takePendingFor', () => {
  const pending: PendingOutput[] = [
    { seq: 10, ts: 100 },
    { seq: 11, ts: 200 },
    { seq: 12, ts: 300 },
  ]

  it('同序 ⇒ 取队头', () => {
    const taken = takePendingFor(pending, 100)

    expect(taken.seq).toBe(10)
    expect(taken.rest.map((p) => p.seq)).toEqual([11, 12])
    // 纯函数：**不改入参**
    expect(pending.length).toBe(3)
  })

  it('乱序 ⇒ 仍配得上（**不取队头**）', () => {
    // 🔥 这一条钉的是"不许把 A 帧的序号配到 B 的画面上"：
    //    本批没有 B 帧、所以今天不会乱序，但"够用"不等于"对"。
    const taken = takePendingFor(pending, 300)

    expect(taken.seq).toBe(12)
    expect(taken.rest.map((p) => p.seq)).toEqual([10, 11])
  })

  it('配不上 ⇒ `seq` 为 null（调用方据此关掉这一帧，宁可少一帧）', () => {
    expect(takePendingFor(pending, 999).seq).toBeNull()
  })
})

// ─────────────────────────────────────────────────────────────
// 编排（喂帧 / 出帧 / 重建）
// ─────────────────────────────────────────────────────────────

describe('createWebCodecsDecoder 的编排', () => {
  it('第一个关键帧 configure 一次，chunk 是 key', () => {
    const h = harness()

    h.feed(makeFrame({ seq: 1, isKeyframe: true, codecString: 'avc3.640032' }))

    expect(h.state.decoder.configs.length).toBe(1)
    expect(h.state.decoder.configs[0].codec).toBe('avc3.640032')
    expect(h.state.decoder.chunks[0].type).toBe('key')
  })

  it('同一对参数的后续关键帧**不重配**（重配会清掉参考帧）', () => {
    const h = harness()
    const key = { isKeyframe: true, codecString: 'avc3.640032' }

    h.feed(makeFrame({ seq: 1, ...key }))
    h.feed(makeFrame({ seq: 2 }))
    h.feed(makeFrame({ seq: 3, ...key }))

    expect(h.state.decoder.configs.length).toBe(1)
  })

  it('🔴 关键帧上这一对变了 ⇒ 重配（§3.9 (h) 的那条规则）', () => {
    const h = harness()

    h.feed(makeFrame({ seq: 1, isKeyframe: true, codecString: 'avc1.640032', description: btoa('c') }))
    h.feed(makeFrame({ seq: 2, isKeyframe: true, codecString: 'avc3.640032', description: null }))

    expect(h.state.decoder.configs.length).toBe(2)
    expect(h.state.decoder.configs[1].codec).toBe('avc3.640032')
  })

  it('🔴 还没配过就来的 P 帧 ⇒ 报"解不出来"，且**不喂给解码器**（喂了会进 error 态）', () => {
    const h = harness()

    h.feed(makeFrame({ seq: 1, isKeyframe: false }))

    expect(h.state.seen).toEqual([{ seq: 1, decoded: null }])
    expect(h.state.decoder.chunks.length).toBe(0)
  })

  it('输出按 timestamp 配回正确的序号（乱序也不配错）', () => {
    const h = harness()

    h.feed(makeFrame({ seq: 1, isKeyframe: true, codecString: 'avc3.640032', monoMs: 10 }))
    h.feed(makeFrame({ seq: 2, monoMs: 20 }))

    // 反过来吐：先第 2 帧，再第 1 帧。
    h.state.decoder.emit(20_000)
    h.state.decoder.emit(10_000)

    expect(h.state.seen.map((s) => s.seq)).toEqual([2, 1])
    expect(h.state.seen.every((s) => s.decoded !== null)).toBe(true)
  })

  it('配不上的输出**被关掉**（不关就是漏显存）', () => {
    const h = harness()
    let closed = 0
    const leak = {
      timestamp: 999_999,
      displayWidth: 1,
      displayHeight: 1,
      close: () => {
        closed += 1
      },
    } as unknown as VideoFrame

    h.feed(makeFrame({ seq: 1, isKeyframe: true, codecString: 'avc3.640032' }))
    h.state.decoder.init.output(leak)

    expect(closed).toBe(1)
    expect(h.state.seen.length).toBe(0)
  })

  it('🔴 解码器报错 ⇒ 等在途的帧都报"解不出来"，并记一次重建', () => {
    const h = harness()

    h.feed(makeFrame({ seq: 1, isKeyframe: true, codecString: 'avc3.640032' }))
    h.feed(makeFrame({ seq: 2 }))

    h.state.decoder.fail()

    expect(h.state.seen.map((s) => s.seq)).toEqual([1, 2])
    expect(h.state.seen.every((s) => s.decoded === null)).toBe(true)
    expect(h.subject.stats.resets).toBe(1)
  })

  it('reset() 之后下一个关键帧会重新 configure（重建而不是复用）', () => {
    const h = harness()
    const key = { isKeyframe: true, codecString: 'avc3.640032' }

    h.feed(makeFrame({ seq: 1, ...key }))

    const first = h.state.decoder

    h.subject.reset()
    expect(h.subject.stats.resets).toBe(1)

    h.feed(makeFrame({ seq: 2, ...key }))

    expect(h.state.decoder).not.toBe(first)
    expect(h.state.decoder.configs.length).toBe(1)
  })

  it('⚠️ 还没建解码器时 reset() **不记** resets（不许编一个没发生的故障）', () => {
    const h = harness()

    h.subject.reset()

    expect(h.subject.stats.resets).toBe(0)
  })

  it('H.264 的 two flags：不能丢未解码帧、喂入不设限', () => {
    const h = harness()

    expect(h.subject.canDropUndecoded).toBe(false)
    expect(h.subject.maxInFlight).toBe(Number.POSITIVE_INFINITY)
  })

  it('close() 之后再喂不做事，且不再回到 sink', () => {
    const h = harness()

    h.subject.close()
    h.feed(makeFrame({ seq: 1, isKeyframe: true, codecString: 'avc3.640032' }))

    expect(h.state.seen.length).toBe(0)
  })

  it('没有解码器（非安全上下文 / 不支持）⇒ 逐帧报"解不出来"，**不抛**', () => {
    const seen: Array<{ seq: number; decoded: DecodedFrame | null }> = []
    const subject = createWebCodecsDecoder(REMOTE_CODECS.h264, (seq, decoded) => {
      seen.push({ seq, decoded })
    }, { createDecoder: () => null })

    expect(() =>
      subject.feed(makeFrame({ seq: 1, isKeyframe: true, codecString: 'avc3.640032' })),
    ).not.toThrow()

    expect(seen).toEqual([{ seq: 1, decoded: null }])
  })
})
