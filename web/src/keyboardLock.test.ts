import { describe, expect, it, vi } from 'vitest'
import {
  keyboardApiOf,
  lockBrowserKeys,
  REMOTE_LOCKED_KEY_CODES,
  shouldWarnBeforeUnload,
  unlockBrowserKeys,
} from './keyboardLock'

/**
 * **浏览器保留键的接管**（M7.11）的判据。
 *
 * 🔥 这一层为什么值得单测：它治的是一个**完全不报错**的故障 ——
 * 远控时按 Ctrl+W，被浏览器截走关掉观看端自己的标签页，页面无声消失。
 * 而那套"锁什么、什么时候还、锁不上怎么办"的判据全在本模块里，
 * `navigator.keyboard` 又可以在这里造出来。
 *
 * ⚠️ 另一半同样重要：**逃生通道不许锁**。把 Escape 锁进去，
 * 用户在全屏里就退不出来了 —— 那个故障比误关标签页严重得多。
 */

describe('keyboardApiOf', () => {
  it('没有 navigator 时返回 null', () => {
    expect(keyboardApiOf(undefined)).toBeNull()
    expect(keyboardApiOf(null)).toBeNull()
    expect(keyboardApiOf('navigator')).toBeNull()
  })

  it('keyboard 属性存在但没有 lock 时返回 null', () => {
    // ⚠️ 判据必须是"lock 是函数"，不能是 `'keyboard' in navigator`：
    //    有的实现挂了属性但值是 undefined。
    expect(keyboardApiOf({ keyboard: undefined })).toBeNull()
    expect(keyboardApiOf({ keyboard: {} })).toBeNull()
    expect(keyboardApiOf({ keyboard: { lock: 'x' } })).toBeNull()
  })

  it('有 lock 时返回这个对象', () => {
    const keyboard = { lock: vi.fn(), unlock: vi.fn() }
    const api = keyboardApiOf({ keyboard })

    expect(api).not.toBeNull()
    expect(api?.lock).toBe(keyboard.lock)
  })
})

describe('lockBrowserKeys', () => {
  it('环境没有 API 时返回 false，且不抛', async () => {
    await expect(lockBrowserKeys({})).resolves.toBe(false)
  })

  it('锁上时把键码清单原样交给 lock()', async () => {
    const lock = vi.fn().mockResolvedValue(undefined)

    const ok = await lockBrowserKeys({ keyboard: { lock } })

    expect(ok).toBe(true)
    expect(lock).toHaveBeenCalledTimes(1)
    expect(lock.mock.calls[0][0]).toEqual([...REMOTE_LOCKED_KEY_CODES])
  })

  it('lock 被拒绝（非全屏 / 无用户激活）时返回 false，且不抛', async () => {
    const lock = vi.fn().mockRejectedValue(new Error('NotAllowedError'))

    await expect(lockBrowserKeys({ keyboard: { lock } })).resolves.toBe(false)
  })

  it('lock() 同步抛也不外泄', async () => {
    const lock = vi.fn(() => {
      throw new Error('boom')
    })

    await expect(lockBrowserKeys({ keyboard: { lock } })).resolves.toBe(false)
  })
})

describe('unlockBrowserKeys', () => {
  it('有 API 时调 unlock()', () => {
    const unlock = vi.fn()

    unlockBrowserKeys({ keyboard: { lock: vi.fn(), unlock } })

    expect(unlock).toHaveBeenCalledTimes(1)
  })

  it('没有 API / unlock 抛都不外泄', () => {
    expect(() => unlockBrowserKeys({})).not.toThrow()

    const unlock = vi.fn(() => {
      throw new Error('InvalidStateError')
    })

    expect(() => unlockBrowserKeys({ keyboard: { lock: vi.fn(), unlock } })).not.toThrow()
  })
})

describe('REMOTE_LOCKED_KEY_CODES', () => {
  it('包含那几条会"关掉/刷新观看端"的键', () => {
    expect(REMOTE_LOCKED_KEY_CODES).toContain('KeyW')
    expect(REMOTE_LOCKED_KEY_CODES).toContain('KeyR')
    expect(REMOTE_LOCKED_KEY_CODES).toContain('F5')
  })

  it('🔴 逃生通道不许锁：Escape / F11 不在清单里', () => {
    expect(REMOTE_LOCKED_KEY_CODES).not.toContain('Escape')
    expect(REMOTE_LOCKED_KEY_CODES).not.toContain('F11')
  })

  it('修饰键不锁（否则被控端的 Alt+Tab 之类会失灵）', () => {
    expect(REMOTE_LOCKED_KEY_CODES).not.toContain('AltLeft')
    expect(REMOTE_LOCKED_KEY_CODES).not.toContain('ControlLeft')
    expect(REMOTE_LOCKED_KEY_CODES).not.toContain('ShiftLeft')
  })
})

describe('shouldWarnBeforeUnload', () => {
  it('没有会话时不拦（关页是正常操作）', () => {
    expect(shouldWarnBeforeUnload(null)).toBe(false)
    expect(shouldWarnBeforeUnload(undefined)).toBe(false)
    expect(shouldWarnBeforeUnload('')).toBe(false)
  })

  it('正在观看时拦', () => {
    expect(shouldWarnBeforeUnload('sess-1')).toBe(true)
  })
})
