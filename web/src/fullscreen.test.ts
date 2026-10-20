import { describe, expect, it, vi } from 'vitest'
import {
  describeFullscreenFailure,
  enterFullscreen,
  exitFullscreen,
  FullscreenUnsupportedError,
  fullscreenElementOf,
  isElementFullscreen,
  isFullscreenSupported,
} from './fullscreen'

/**
 * **远程画面全屏**（M7.9）的判据。
 *
 * 🔥 这一层为什么值得单测：全屏的三类失败在页面上**长得一模一样**（点了没反应），
 * 而处置完全相反 —— 换浏览器 / 再点一次 / 改 iframe。这三句话都在
 * `describeFullscreenFailure` 里，而它只吃一个 error，可以在这里造。
 *
 * ⚠️ 另一半同样重要：**状态只认 document**。浏览器按 Esc 退出时不会走我们的按钮，
 * 只发一个 `fullscreenchange`；页面若拿本地布尔当状态，按钮就会永远停在「全屏显示」。
 */

/** 一个"什么都没有"的 document 替身（部分方法按需覆盖）。 */
function fakeDoc(overrides: Partial<Record<string, unknown>> = {}) {
  return { ...overrides } as unknown as Parameters<typeof fullscreenElementOf>[0]
}

/** 一个"能全屏"的元素替身。 */
function fakeElement(requestFullscreen: () => Promise<void> | void) {
  return { requestFullscreen } as unknown as Element
}

describe('fullscreenElementOf', () => {
  it('标准属性优先', () => {
    const el = {} as Element
    const other = {} as Element

    expect(fullscreenElementOf(fakeDoc({ fullscreenElement: el, webkitFullscreenElement: other }))).toBe(el)
  })

  it('只有 WebKit 属性时也认（Safari 16.4 之前只有它）', () => {
    // 🔴 只读标准属性的实现会在这里返回 null ⇒ 按钮永远显示「全屏显示」，
    //    而屏幕上明明已经全屏了。
    const el = {} as Element

    expect(fullscreenElementOf(fakeDoc({ webkitFullscreenElement: el }))).toBe(el)
  })

  it('都没有 ⇒ null（不是 undefined）', () => {
    expect(fullscreenElementOf(fakeDoc())).toBeNull()
  })
})

describe('isFullscreenSupported', () => {
  it('undefined 按"支持"处理（WebKit 上它就是 undefined）', () => {
    expect(isFullscreenSupported(fakeDoc())).toBe(true)
  })

  it('显式 false ⇒ 不支持（iframe 缺 allowfullscreen 的样子）', () => {
    expect(isFullscreenSupported(fakeDoc({ fullscreenEnabled: false }))).toBe(false)
  })

  it('只有 WebKit 那一位是 false 时同样判不支持', () => {
    expect(isFullscreenSupported(fakeDoc({ webkitFullscreenEnabled: false }))).toBe(false)
  })

  it('true ⇒ 支持', () => {
    expect(isFullscreenSupported(fakeDoc({ fullscreenEnabled: true }))).toBe(true)
  })
})

describe('isElementFullscreen', () => {
  it('全屏的就是这一块 ⇒ true', () => {
    const el = {} as Element

    expect(isElementFullscreen(fakeDoc({ fullscreenElement: el }), el)).toBe(true)
  })

  it('全屏的是别的东西 ⇒ false（不许把别人的全屏退掉）', () => {
    const el = {} as Element

    expect(isElementFullscreen(fakeDoc({ fullscreenElement: {} as Element }), el)).toBe(false)
  })

  it('元素还没挂上 ⇒ false，不抛', () => {
    expect(isElementFullscreen(fakeDoc({ fullscreenElement: {} as Element }), null)).toBe(false)
  })
})

describe('enterFullscreen', () => {
  it('走标准入口', async () => {
    const request = vi.fn(() => Promise.resolve())

    await enterFullscreen(fakeElement(request))

    expect(request).toHaveBeenCalledTimes(1)
  })

  it('标准入口不在时走 WebKit 前缀', async () => {
    const webkit = vi.fn(() => Promise.resolve())
    const el = { webkitRequestFullscreen: webkit } as unknown as Element

    await enterFullscreen(el)

    expect(webkit).toHaveBeenCalledTimes(1)
  })

  it('两个都没有 ⇒ NotSupportedError（页面据此说"换浏览器"，而不是沉默）', async () => {
    await expect(enterFullscreen({} as Element)).rejects.toBeInstanceOf(FullscreenUnsupportedError)
  })

  it('浏览器拒绝时原样抛出（不要把 NotAllowed 吞成"成功"）', async () => {
    const denied = new Error('denied')
    denied.name = 'NotAllowedError'

    await expect(enterFullscreen(fakeElement(() => Promise.reject(denied)))).rejects.toBe(denied)
  })
})

describe('exitFullscreen', () => {
  it('走标准入口', async () => {
    const exit = vi.fn(() => Promise.resolve())

    await exitFullscreen(fakeDoc({ exitFullscreen: exit }))

    expect(exit).toHaveBeenCalledTimes(1)
  })

  it('标准入口不在时走 WebKit 前缀', async () => {
    const exit = vi.fn(() => Promise.resolve())

    await exitFullscreen(fakeDoc({ webkitExitFullscreen: exit }))

    expect(exit).toHaveBeenCalledTimes(1)
  })

  it('两个都没有 ⇒ NotSupportedError', async () => {
    await expect(exitFullscreen(fakeDoc())).rejects.toBeInstanceOf(FullscreenUnsupportedError)
  })
})

describe('describeFullscreenFailure', () => {
  function named(name: string, message = ''): Error {
    const err = new Error(message)
    err.name = name

    return err
  }

  it('不支持 ⇒ 指向"换浏览器"，并明说画面本身没坏', () => {
    const text = describeFullscreenFailure(named('NotSupportedError'))

    expect(text).toContain('不支持')
    expect(text).toContain('Edge')
    // ⚠️ 少了这半句，用户会以为"全屏不了 = 远控有故障"。
    expect(text).toContain('画面本身不受影响')
  })

  it('NotAllowed ⇒ 指向"再点一次"，并点出真实原因（手势被 await 吃掉）', () => {
    const text = describeFullscreenFailure(named('NotAllowedError'))

    expect(text).toContain('再点一次')
    expect(text).toContain('点击')
  })

  it('SecurityError ⇒ 指向 iframe / 直接打开', () => {
    const text = describeFullscreenFailure(named('SecurityError'))

    expect(text).toContain('iframe')
  })

  it('InvalidStateError ⇒ 说明与 Esc 撞车，无副作用', () => {
    const text = describeFullscreenFailure(named('InvalidStateError'))

    expect(text).toContain('Esc')
    expect(text).toContain('没有副作用')
  })

  it('未知错误 ⇒ 带出原始 message（别把原因吞掉）', () => {
    expect(describeFullscreenFailure(named('Error', '设备不支持该操作'))).toContain('设备不支持该操作')
  })

  it('不是 Error 的东西也要有话说', () => {
    expect(describeFullscreenFailure('boom')).toContain('没有成功')
  })

  it('三类失败的文案必须彼此不同（合成一句就等于让人去猜）', () => {
    const texts = ['NotSupportedError', 'NotAllowedError', 'SecurityError'].map((name) =>
      describeFullscreenFailure(named(name)),
    )

    expect(new Set(texts).size).toBe(3)
  })
})
