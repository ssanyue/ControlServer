import { describe, expect, it } from 'vitest'
import { codecLabel, describeCodecFallback } from './codecFallback'
import { REMOTE_CODECS } from './types'

/**
 * **页面关于编码格式的话，必须与事实一致**（M7.11）。
 *
 * 🔥 现场看到的是这样一条横幅：
 *
 * > 画面还是 M5.1 的 MJPEG 联调脚手架（每帧一张独立 JPEG），不是可交付的编码格式
 * > —— 硬编器都不编 JPEG，所以帧率上不去（默认 12 fps）。4K60 要等 M5.4 的 H.264/HEVC 硬编。
 *
 * 而那时被控端**正在跑 H.264 硬编**。这句话不是"有点旧"，是**每一句都反了**：
 * 硬编早就有了、MJPEG 退成了降级档、帧率上限也不是 12。
 *
 * ⇒ 本组用例钉三件事：
 *   ① MJPEG 的标签是**降级档**，不是"脚手架"（措辞错会让人以为功能没交付）；
 *   ② 降级提示**只在真的降级时出现**（硬编会话一个字都不该看到）；
 *   ③ 提示里要给出**出路**，不能只说"帧率上不去"。
 */

describe('codecLabel', () => {
  it('硬编格式按大写显示，不加任何多余标签', () => {
    expect(codecLabel(REMOTE_CODECS.h264)).toBe('H264')
    expect(codecLabel(REMOTE_CODECS.hevc)).toBe('HEVC')
  })

  it('MJPEG 标成「降级」，并且不再出现「脚手架」三个字', () => {
    const label = codecLabel(REMOTE_CODECS.mjpeg)

    expect(label).toContain('降级')
    expect(label).not.toContain('脚手架')
    expect(label).not.toContain('联调')
  })

  it('还不知道（null / undefined / 空串）一律画「—」，不冒充任何一个格式', () => {
    expect(codecLabel(null)).toBe('—')
    expect(codecLabel(undefined)).toBe('—')
    expect(codecLabel('')).toBe('—')
  })
})

describe('describeCodecFallback', () => {
  it('硬编会话一句话都不说', () => {
    expect(describeCodecFallback(REMOTE_CODECS.h264)).toBeNull()
    expect(describeCodecFallback(REMOTE_CODECS.hevc)).toBeNull()
  })

  it('还不知道的时候也不说（没有可解释的事就不要占地方）', () => {
    expect(describeCodecFallback(null)).toBeNull()
    expect(describeCodecFallback(undefined)).toBeNull()
  })

  it('MJPEG 时给一句话，且同时说清"是什么"与"往哪走"', () => {
    const note = describeCodecFallback(REMOTE_CODECS.mjpeg)

    expect(note).not.toBeNull()

    // 是什么：降级 + 软编
    expect(note).toContain('MJPEG')
    expect(note).toContain('降级')
    expect(note).toContain('软编')
    expect(note).toContain('硬编')

    // 往哪走：必须给可执行的两条出路，而不是只说"很慢"
    expect(note).toContain('单块屏')
    expect(note).toContain('60')
  })

  it('不提任何内部里程碑编号（M5.1 / M5.4 这类号对现场没有信息量）', () => {
    const note = describeCodecFallback(REMOTE_CODECS.mjpeg) ?? ''

    expect(note).not.toMatch(/\bM\d\.\d/)
  })
})
