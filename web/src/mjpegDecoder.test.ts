/**
 * `mjpegDecoder` 的用例（M5.4-b §8 第 8 步：从 `remotePlayback.ts` 搬出来）。
 *
 * <p>
 * ⚠️ 搬家是**行为不变**的重构，所以这一批用例主要钉两件事：
 * ① 帧字节 → data URL 的两条分支（字符串 / 字节数组）一条都没丢；
 * ② MJPEG 的**丢帧语义**（`canDropUndecoded = true`）第一次被写成了可断言的东西 ——
 * 它原来只是 `remotePlayback.ts` 里一段"能不能提前丢"的隐式行为。
 * </p>
 */

import { describe, expect, it } from 'vitest'

import { bytesToBase64, createMjpegDecoder, toDataUrl } from './mjpegDecoder'
import { REMOTE_CODECS, type RemoteStreamFrame } from './types'

function makeFrame(data: string | number[]): RemoteStreamFrame {
  return {
    header: {
      sessionId: 's1',
      sequence: 1,
      codec: REMOTE_CODECS.mjpeg,
      isKeyframe: true,
      width: 2,
      height: 2,
      capturedUnixMs: 1_700_000_000_000,
      capturedMonoMs: 1000,
      dropped: 0,
      codecString: null,
      description: null,
    },
    data: data as unknown as string,
  }
}

describe('toDataUrl', () => {
  it('base64 字符串 ⇒ 直接拼 `data:` 头（**省掉一次 atob**）', () => {
    expect(toDataUrl(makeFrame('/9j/4AAQ'))).toBe('data:image/jpeg;base64,/9j/4AAQ')
  })

  it('空字符串 ⇒ null（调用方据此丢弃）', () => {
    expect(toDataUrl(makeFrame(''))).toBeNull()
  })

  it('字节数组 ⇒ 先转 base64（MessagePack 之后会走的那条）', () => {
    // ⚠️ 这条分支今天不走，但保留着 —— 去掉它的话，改协议那天会变成
    //    "画面全黑而控制台一片安静"（这正是它一直以来的注释在说的事）。
    expect(toDataUrl(makeFrame([0xff, 0xd8, 0xff]))).toBe(
      `data:image/jpeg;base64,${btoa('\xff\xd8\xff')}`,
    )
  })

  it('空字节数组 ⇒ null', () => {
    expect(toDataUrl(makeFrame([]))).toBeNull()
  })

  it('认不出的形状 ⇒ null', () => {
    expect(toDataUrl(makeFrame(undefined as unknown as number[]))).toBeNull()
  })
})

describe('bytesToBase64', () => {
  it('不会因为帧太大而打爆调用栈（不许 spread）', () => {
    // 200 KB：用 `String.fromCharCode(...bytes)` 会 `RangeError`。
    const big = new Uint8Array(200_000)

    expect(() => bytesToBase64(big)).not.toThrow()
    expect(bytesToBase64(big).length).toBeGreaterThan(200_000)
  })
})

describe('createMjpegDecoder', () => {
  it('🔴 可以丢**还没解码**的帧（逐帧独立）—— 这是排期层"忙就丢"的依据', () => {
    const decoder = createMjpegDecoder(() => undefined)

    expect(decoder.canDropUndecoded).toBe(true)
  })

  it('提前解码上限是 2（每帧一次真异步解码，喂多了只是白烧）', () => {
    const decoder = createMjpegDecoder(() => undefined)

    expect(decoder.maxInFlight).toBe(2)
  })

  it('reset() 是空操作、也**不**记 resets（MJPEG 没有参考链可断）', () => {
    const decoder = createMjpegDecoder(() => undefined)

    decoder.reset()

    expect(decoder.stats.resets).toBe(0)
  })

  it('codec 是 mjpeg', () => {
    expect(createMjpegDecoder(() => undefined).codec).toBe(REMOTE_CODECS.mjpeg)
  })
})
