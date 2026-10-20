import { describe, expect, it } from 'vitest'
import { DESKTOP_IDLE_HINT_MS, describeDesktopIdle, isDesktopIdleNotice } from './desktopIdle'

/**
 * **「被控端桌面静止中」那一格的三态**（M5.4-c② 步 7）。
 *
 * 🔥 这三条断言守的是一件很具体的事：页面上原来只有两种话 ——
 * "画面卡住" 与 "一切正常"，而**展厅里定格在一张图上的那台机器两种都不是**
 * （它好好的，只是没人动它）。被控端一直知道这件事（抓屏器回"没有新帧"就是答案），
 * 之前它只是不说；现在它说了，这一格就必须把三种情况**分开画**：
 *
 * - `null` ⇒ `—`（这台机器不提这件事：旧客户端 / 还没上报）——**不是 0**；
 * - `0` ⇒ 画面在变 ——"刚动过"是一个答案；
 * - `12000` ⇒ 已静止 12 秒。
 *
 * ⚠️ 把 `null` 画成 `0` 是这里最容易犯的错，而它不会报任何错：
 * 每台旧客户端都会统一显示"画面在变"，看起来完全正常。
 */
describe('describeDesktopIdle', () => {
  it('null 画成「—」，不是 0 秒也不是"画面在变"', () => {
    expect(describeDesktopIdle(null)).toBe('—')
    expect(describeDesktopIdle(undefined)).toBe('—')
  })

  it('0 与很小的值表示"画面一直在变"', () => {
    expect(describeDesktopIdle(0)).toBe('画面在变')
    expect(describeDesktopIdle(999)).toBe('画面在变')
  })

  it('满一秒开始说秒数，且向下取整（不把 1.9 秒说成 2 秒）', () => {
    expect(describeDesktopIdle(1000)).toBe('已静止 1 秒')
    expect(describeDesktopIdle(1999)).toBe('已静止 1 秒')
    expect(describeDesktopIdle(12000)).toBe('已静止 12 秒')
  })

  it('负数不是一个可以展示的答案（防御：时钟差方向写反时不要画出"-3 秒"）', () => {
    expect(describeDesktopIdle(-1)).toBe('—')
    expect(describeDesktopIdle(Number.NaN)).toBe('—')
  })
})

describe('isDesktopIdleNotice', () => {
  it('刚动过 / 不知道的时候都**不**提示"静止中"', () => {
    expect(isDesktopIdleNotice(null)).toBe(false)
    expect(isDesktopIdleNotice(undefined)).toBe(false)
    expect(isDesktopIdleNotice(0)).toBe(false)
    expect(isDesktopIdleNotice(DESKTOP_IDLE_HINT_MS - 1)).toBe(false)
  })

  it('到达阈值才提示 —— 阈值必须大于"一次抓屏周期"，否则画面在动也会闪出一条提示', () => {
    expect(DESKTOP_IDLE_HINT_MS).toBeGreaterThanOrEqual(1000)
    expect(isDesktopIdleNotice(DESKTOP_IDLE_HINT_MS)).toBe(true)
    expect(isDesktopIdleNotice(12000)).toBe(true)
  })
})
