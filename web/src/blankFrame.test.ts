import { describe, expect, it } from 'vitest'
import {
  BLANK_BPP_THRESHOLD,
  base64ByteLength,
  describeBlankFrame,
  isBlankKeyframe,
} from './blankFrame'

/**
 * **「这一帧几乎是纯色」的判据**（M7.6）。
 *
 * 🔥 它守的是一个被误判过两轮的现场形态：**画面全黑，但被控端动一下鼠标就恢复**，
 * 两端日志干干净净。前两轮分别怀疑过抓屏坏了、编码器坏了 —— 都不是，
 * 真因是被控端静置之后关掉了显示器（或起了黑屏屏保）。
 *
 * ⇒ 这里钉的只有一件事：**纯色要被认出来**，别让页面只剩一块黑、一句解释都没有。
 *
 * ⚠️ 但它**不许替用户下结论**：纯色既可能是"息屏了"，也可能是"人家壁纸就是纯色"。
 * 所以下面有一条断言专门钉"两种解释都要在"。
 */

/** 实测锚点（见 blankFrame.ts 文件头）：正常桌面 4K 的 IDR 约 101 KB / 3.1 MPix。 */
const NORMAL_DESKTOP_BYTES = 101_341
const NORMAL_DESKTOP_PIXELS = 2160 * 1440

/** 实测锚点：被控端那片黑的 4K IDR 只有 2.4 KB。 */
const BLANK_SCREEN_BYTES = 2_457
const BLANK_SCREEN_PIXELS = 3840 * 2160

describe('isBlankKeyframe', () => {
  it('实测的那两个锚点必须判对（差 100 倍，这是本模块存在的全部理由）', () => {
    expect(isBlankKeyframe(NORMAL_DESKTOP_BYTES, NORMAL_DESKTOP_PIXELS)).toBe(false)
    expect(isBlankKeyframe(BLANK_SCREEN_BYTES, BLANK_SCREEN_PIXELS)).toBe(true)
  })

  it('阈值两边各留足余量（不许贴着实测值定）', () => {
    const normalBpp = NORMAL_DESKTOP_BYTES / NORMAL_DESKTOP_PIXELS
    const blankBpp = BLANK_SCREEN_BYTES / BLANK_SCREEN_PIXELS

    expect(BLANK_BPP_THRESHOLD).toBeLessThan(normalBpp / 4)
    expect(BLANK_BPP_THRESHOLD).toBeGreaterThan(blankBpp * 4)
  })

  it('同一个纯色内容，在任何分辨率下判出来都一样（判据是 bpp 而不是绝对字节数）', () => {
    // 0.0003 pp 的纯色，在 1080p / 4K / 8K 上都该判成纯色。
    for (const [w, h] of [
      [1920, 1080],
      [3840, 2160],
      [7680, 4320],
    ]) {
      const pixels = w * h
      const bytes = Math.round(pixels * 0.0003)

      expect(isBlankKeyframe(bytes, pixels), `${w}×${h}`).toBe(true)
    }
  })

  it('输入不受控的一律不判（0 / 负数 / NaN / 太小的画面）', () => {
    expect(isBlankKeyframe(0, 1000 * 1000)).toBe(false)
    expect(isBlankKeyframe(1000, 0)).toBe(false)
    expect(isBlankKeyframe(Number.NaN, 1000 * 1000)).toBe(false)
    expect(isBlankKeyframe(1000, Number.NaN)).toBe(false)

    // 太小的画面里帧头固定开销会主导 bpp，判出来没有意义 ⇒ 明确不判。
    expect(isBlankKeyframe(1, 160 * 120 - 1)).toBe(false)
  })
})

describe('base64ByteLength', () => {
  it('四种 padding 情况都对', () => {
    // 'A' * 4 → 3 字节
    expect(base64ByteLength('AAAA')).toBe(3)
    // 'AA==' → 1 字节
    expect(base64ByteLength('AA==')).toBe(1)
    // 'AAA=' → 2 字节
    expect(base64ByteLength('AAA=')).toBe(2)
    // 无 padding
    expect(base64ByteLength('AAA')).toBe(2)
  })

  it('空串是 0，不是 NaN', () => {
    expect(base64ByteLength('')).toBe(0)
    expect(base64ByteLength('A')).toBe(0)
  })

  it('与实际解码出来的长度一致（抽样几档）', () => {
    for (const size of [1, 2, 3, 1023, 4096, 100_000]) {
      const bytes = new Uint8Array(size)

      // ⚠️ 不用 Buffer：这是浏览器侧的模块，测试也不该依赖 Node 的全局。
      const encoded = btoa(Array.from(bytes, (b) => String.fromCharCode(b)).join(''))

      expect(base64ByteLength(encoded), `${size} 字节`).toBe(size)
    }
  })
})

describe('describeBlankFrame', () => {
  it('正常画面不出提示（假告警的代价是真告警被忽略）', () => {
    expect(
      describeBlankFrame(NORMAL_DESKTOP_BYTES, NORMAL_DESKTOP_PIXELS, 2160, 1440),
    ).toBeNull()
  })

  it('纯色必须出提示，并且把尺寸与 bpp 写进去（现场要能核）', () => {
    const text = describeBlankFrame(BLANK_SCREEN_BYTES, BLANK_SCREEN_PIXELS, 3840, 2160)

    expect(text).not.toBeNull()
    expect(text).toContain('2.4 KB')
    expect(text).toContain('3840×2160')
  })

  it('🔴 两种解释都要在：既可能是息屏，也可能只是纯色壁纸', () => {
    const text = describeBlankFrame(BLANK_SCREEN_BYTES, BLANK_SCREEN_PIXELS, 3840, 2160) ?? ''

    // 不许只写"你一定息屏了" —— 那在"人家壁纸就是纯色"的机器上会变成甩不掉的假告警。
    expect(text).toContain('壁纸')
    expect(text).toContain('关闭显示器')
    expect(text).toContain('屏保')
  })

  it('告诉我们"动一下就会恢复"（现场最省事的那一步）', () => {
    const text = describeBlankFrame(BLANK_SCREEN_BYTES, BLANK_SCREEN_PIXELS, 3840, 2160) ?? ''

    expect(text).toContain('鼠标')
  })
})
