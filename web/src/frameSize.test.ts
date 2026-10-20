import { describe, expect, it } from 'vitest'
import {
  describeEncodingScale,
  describeSizeMismatch,
  pickDrawRect,
  sizeDiffers,
} from './frameSize'

/**
 * **「4K 只显示四分之一」的几何判据**（M7.7）。
 *
 * 🔥 现场原话：「部分客户机访问远程是无法正确显示 4K 画面（仅显示四分之一）」。
 * "四分之一"在几何上只有一种含义 —— 宽高各半。而它既可能是**编码端**把整幅
 * 缩了一半再编，也可能是**解码端**只解出了左上那块，**修法完全相反**。
 *
 * ⇒ 本组用例钉两件事：
 *   ① 绘制**永远按区域铺满**（从根上消灭"静静缩在左上角"这个默认行为）；
 *   ② 对不上时那句话**必须同时给出两个数**，不许只写"画面被缩小了"。
 */

const UHD = { x: 0, y: 0, width: 3840, height: 2160 }

describe('pickDrawRect', () => {
  it('正常情况：解码尺寸 = 区域尺寸，落点与尺寸原样不变', () => {
    expect(pickDrawRect(UHD, { width: 3840, height: 2160 })).toEqual(UHD)
  })

  it('解码只出了一半 ⇒ 仍按区域铺满（不许缩在左上角）', () => {
    const rect = pickDrawRect(UHD, { width: 1920, height: 1080 })

    expect(rect).toEqual(UHD)

    // 这条断言是这组用例的核心：原来用 decoded 尺寸画，画出来正好是整幅的 1/4。
    expect(rect.width * rect.height).toBe(3840 * 2160)
  })

  it('分块时落点是这一块的坐标，不是 (0,0)', () => {
    const tile = { x: 1920, y: 0, width: 1920, height: 2160 }

    expect(pickDrawRect(tile, { width: 1920, height: 2160 })).toEqual(tile)
  })

  it('解码比区域大 ⇒ 交给 drawImage 裁掉，尺寸仍取区域', () => {
    const rect = pickDrawRect(UHD, { width: 3856, height: 2176 })

    expect(rect.width).toBe(3840)
    expect(rect.height).toBe(2160)
  })
})

describe('describeSizeMismatch', () => {
  it('对得上就不说话（差 2 px 以内算对得上）', () => {
    expect(describeSizeMismatch(UHD, { width: 3840, height: 2160 })).toBeNull()
    expect(describeSizeMismatch(UHD, { width: 3842, height: 2160 })).toBeNull()
  })

  it('一半 ⇒ 必须两个数都写出来，并且点明"四分之一"', () => {
    const note = describeSizeMismatch(UHD, { width: 1920, height: 1080 })

    expect(note).toBeTruthy()

    // 两个数都要在 —— 少任何一个，现场都判不出是哪一端缩的。
    expect(note).toContain('1920×1080')
    expect(note).toContain('3840×2160')
    expect(note).toContain('四分之一')
    expect(note).toContain('50%')
  })

  it('两种成因都要提到，不许替现场下结论', () => {
    const note = describeSizeMismatch(UHD, { width: 1920, height: 1080 })!

    expect(note).toContain('被控端')
    expect(note).toContain('浏览器')
  })

  it('只缩了一个方向 ⇒ 比例取更糟的那一维，不说成 50%', () => {
    // 宽不变、高减半：面积是 1/2，但形变是 50% 的高 —— 取 50% 才反映现场看到的。
    const note = describeSizeMismatch(UHD, { width: 3840, height: 1080 })!

    expect(note).toContain('50%')
  })

  it('分块的区域要按"这一块"说，不能拿整幅比（否则每块都被判成缩小）', () => {
    const tile = { width: 1920, height: 2160 }

    expect(describeSizeMismatch(tile, { width: 1920, height: 2160 }, 3840)).toBeNull()

    const note = describeSizeMismatch(tile, { width: 960, height: 1080 }, 3840)!

    expect(note).toContain('这一块区域')
    expect(note).toContain('1920×2160')
  })

  it('解出来比区域还大 ⇒ 说的是对齐填充，不是缩小', () => {
    const note = describeSizeMismatch(UHD, { width: 3872, height: 2176 })!

    expect(note).toContain('宏块对齐')
    expect(note).not.toContain('四分之一')
  })

  it('尺寸缺失（0）时不下结论', () => {
    expect(describeSizeMismatch(UHD, { width: 0, height: 0 })).toBeNull()
    expect(describeSizeMismatch({ width: 0, height: 0 }, { width: 1, height: 1 })).toBeNull()
  })
})

describe('sizeDiffers', () => {
  it('同一尺寸不触发界面更新', () => {
    expect(sizeDiffers({ width: 3840, height: 2160 }, { width: 3840, height: 2160 })).toBe(false)
    expect(sizeDiffers({ width: 3840, height: 2160 }, { width: 3841, height: 2160 })).toBe(false)
  })

  it('差一半要触发（否则页面永远显示上一路的尺寸）', () => {
    expect(sizeDiffers({ width: 3840, height: 2160 }, { width: 1920, height: 1080 })).toBe(true)
  })
})

/**
 * **「多屏合并显示画面不对」的判据**（M7.8）。
 *
 * 🔥 现场：三屏拼接 5760×2160，帧头报 5760×2160，实际编出来 1920×720
 * （MJPEG 把长边压到 1920）。前端画布按帧头建 5760×2160、把 1920×720
 * 画在 (0,0) ⇒ **画面只占左上 1/9，其余全黑**。
 *
 * ⇒ 这组钉两件事：
 *   ① 真的缩了必须说出来（含两个尺寸与百分比）—— 否则"糊"永远查不到原因；
 *   ② 没缩 / 拿不到屏幕尺寸时**一个字都不说**（常驻告警会淹掉真问题）。
 */
describe('describeEncodingScale', () => {
  it('三屏拼接 5760×2160 被压到 1920×720 ⇒ 说出 33.3%', () => {
    const note = describeEncodingScale(
      { width: 5760, height: 2160 },
      { width: 1920, height: 720 },
    )

    expect(note).not.toBeNull()
    expect(note).toContain('5760×2160')
    expect(note).toContain('1920×720')
    expect(note).toContain('33.3%')
    // 必须给出出路，否则现场只知道"糊"，不知道能怎么办。
    expect(note).toContain('切到单块屏')
  })

  it('单屏 4K 被压到 1920×1080 ⇒ 50%', () => {
    const note = describeEncodingScale(
      { width: 3840, height: 2160 },
      { width: 1920, height: 1080 },
    )

    expect(note).toContain('50%')
  })

  it('1:1 编码（硬编）⇒ 不说话', () => {
    expect(
      describeEncodingScale({ width: 3840, height: 2160 }, { width: 3840, height: 2160 }),
    ).toBeNull()
  })

  it('差几个像素（任务栏/宏块对齐）不算降采样', () => {
    // 差 2% 不该触发告警 —— 一旦常驻，真的缩了一半反而看不见。
    expect(
      describeEncodingScale({ width: 2560, height: 1440 }, { width: 2528, height: 1424 }),
    ).toBeNull()
  })

  it('拿不到屏幕尺寸时不下结论', () => {
    expect(describeEncodingScale(null, { width: 1920, height: 720 })).toBeNull()
    expect(describeEncodingScale(undefined, { width: 1920, height: 720 })).toBeNull()
    expect(describeEncodingScale({ width: 0, height: 0 }, { width: 1920, height: 720 })).toBeNull()
  })
})
