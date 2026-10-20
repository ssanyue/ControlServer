/**
 * `remoteCodecProbe` 的用例（M5.4-b §8 第 8 步）。
 *
 * <p>
 * 🔥 被测的中心规则是"**声明的是本页显示得了什么**，而不是浏览器能解什么"，
 * 以及"**两条前缀都试、用返回值定**"（方案 §3.4.2 明确不许靠推理）。
 * 这两条错掉的表现都是**一块黑屏而两端日志全正常**，所以必须钉住。
 * </p>
 */

import { afterEach, describe, expect, it, vi } from 'vitest'

import { mjpegOnlyResult, probeViewerVideo, VIEWER_PACKAGING, hasWebCodecs } from './remoteCodecProbe'
import { REMOTE_CODECS } from './types'

/** 用一组"支持哪些 codec 串"来装一个假的 `VideoDecoder`。 */
function stubVideoDecoder(supports: (codec: string) => boolean): void {
  vi.stubGlobal('VideoDecoder', {
    // 真实现是静态方法且返回 Promise —— 探测依赖这两点。
    isConfigSupported: async (config: { codec: string }) => ({ supported: supports(config.codec) }),
  })
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('hasWebCodecs', () => {
  it('没有 `VideoDecoder` ⇒ false（非安全上下文里的表现是 undefined，不是报错）', () => {
    expect(hasWebCodecs()).toBe(false)
  })
})

describe('probeViewerVideo', () => {
  it('没有 WebCodecs ⇒ 只声明 MJPEG（且不抛）', async () => {
    const result = await probeViewerVideo()

    expect(result.capabilities.codecs).toEqual([REMOTE_CODECS.mjpeg])
    expect(result.capabilities.declared).toBe(true)
    expect(result.summary).toContain('WebCodecs')
  })

  it('🔴 MJPEG **永远**在集合里（它只需要 `<img>` ⇒ 不可能"一种都显示不了"）', async () => {
    stubVideoDecoder(() => false)

    const result = await probeViewerVideo()

    expect(result.capabilities.codecs).toContain(REMOTE_CODECS.mjpeg)
  })

  it('带内（avc3）通过 ⇒ 保持带内，且不是 advisory', async () => {
    stubVideoDecoder((codec) => codec.startsWith('avc3'))

    const result = await probeViewerVideo()

    expect(result.capabilities.codecs).toContain(REMOTE_CODECS.h264)
    expect(result.capabilities.packaging).toBe(VIEWER_PACKAGING.inband)
    expect(result.advisory).toBe(false)
  })

  it('🔴 avc3 不认、avc1 认 ⇒ **改请求带外**，并且明确标成 advisory', async () => {
    // 这一条就是"Chromium 不认 `avc3` 这个前缀"那个未知数的落点：
    // 探到了就走带外，探不到就不声明 —— **不许靠推理**。
    stubVideoDecoder((codec) => codec.startsWith('avc1'))

    const result = await probeViewerVideo()

    expect(result.capabilities.codecs).toContain(REMOTE_CODECS.h264)
    expect(result.capabilities.packaging).toBe(VIEWER_PACKAGING.outofband)
    // ⚠️ 没有描述符的情况下问出来的结论 ⇒ 必须标成"大致"。
    expect(result.advisory).toBe(true)
    expect(result.summary).toContain('大致')
  })

  it('H.264 一条前缀都不认 ⇒ 不声明 H.264', async () => {
    stubVideoDecoder(() => false)

    const result = await probeViewerVideo()

    expect(result.capabilities.codecs).not.toContain(REMOTE_CODECS.h264)
  })

  it('声明了 H.264 才会顺带试 HEVC（>4096 时它是硬编的唯一出路）', async () => {
    stubVideoDecoder((codec) => codec.startsWith('avc3') || codec.startsWith('hev1'))

    const result = await probeViewerVideo()

    expect(result.capabilities.codecs).toContain(REMOTE_CODECS.hevc)
  })

  it('H.264 都不认时不会去声明 HEVC（声明了也没意义：偏好顺序里它在后面）', async () => {
    stubVideoDecoder((codec) => codec.startsWith('hev1'))

    const result = await probeViewerVideo()

    expect(result.capabilities.codecs).not.toContain(REMOTE_CODECS.hevc)
  })

  it('`isConfigSupported` 抛错 ⇒ 当"不支持"，**不抛**', async () => {
    vi.stubGlobal('VideoDecoder', {
      isConfigSupported: () => Promise.reject(new Error('nope')),
    })

    const result = await probeViewerVideo()

    expect(result.capabilities.codecs).toEqual([REMOTE_CODECS.mjpeg])
  })
})

describe('mjpegOnlyResult', () => {
  it('装机兜底：集合里只有 MJPEG，封装仍是中心最终会用的那个值', () => {
    const result = mjpegOnlyResult('因为某个原因')

    expect(result.capabilities.codecs).toEqual([REMOTE_CODECS.mjpeg])
    expect(result.capabilities.packaging).toBe(VIEWER_PACKAGING.inband)
    expect(result.summary).toBe('因为某个原因')
    expect(result.advisory).toBe(false)
  })
})
