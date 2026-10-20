import { describe, expect, it, beforeEach } from 'vitest'

import {
  LATENCY_MAX_MS,
  LATENCY_MIN_MS,
  chooseLatencyMs,
  getPlaybackLatencyMs,
  resetPlaybackLatency,
  setPlaybackLatencyForFps,
} from './playbackLatency'

/**
 * <b>播放延迟预算</b>（M7.16）。
 *
 * <p>
 * 🔥 这组用例存在的理由：这个数<b>没有任何界面证据</b> —— 调大了是"有点慢"，
 * 调小了是"有点卡"，两端日志都干净。所以"它到底该是多少"必须写在能断言的地方，
 * 而不是散在某个组件的一个常量上（M7.16 之前就是那个 60）。
 * </p>
 */
describe('播放延迟预算', () => {
  beforeEach(() => {
    resetPlaybackLatency()
  })

  /**
   * 60 fps 下缓冲只需要两帧 —— 其余的是白送的延迟。
   *
   * <p>
   * 这一条就是 M7.16 的<b>全部收益</b>：60 → 33 ms，端到端少 27 ms（≈ 45% 的缓冲）。
   * </p>
   */
  it('60 fps 用两帧余量（33 ms），而不是老的固定 60 ms', () => {
    expect(chooseLatencyMs(60)).toBe(33)
    expect(chooseLatencyMs(60)).toBeLessThan(LATENCY_MAX_MS)
  })

  it('45 fps 也是两帧（44 ms）', () => {
    expect(chooseLatencyMs(45)).toBe(44)
  })

  /**
   * 低帧率<b>一个都不用降</b>，而且必须夹回上限。
   *
   * <p>
   * 🔥 这是最容易写反的一格：12 fps 的帧间隔是 83 ms，"两帧"是 166 ms，
   * 直接给出去等于把延迟翻三倍。夹回 60 = 与 M7.16 之前完全一致。
   * </p>
   */
  it('30 fps 及以下仍然是 60 ms（两帧已经超过上限）', () => {
    expect(chooseLatencyMs(30)).toBe(LATENCY_MAX_MS)
    expect(chooseLatencyMs(20)).toBe(LATENCY_MAX_MS)
    expect(chooseLatencyMs(12)).toBe(LATENCY_MAX_MS)
    expect(chooseLatencyMs(5)).toBe(LATENCY_MAX_MS)
  })

  /** 再离谱的帧率也不许掉到下限以下（那等于"一帧都不缓冲"）。 */
  it('永远夹在 [下限, 上限] 之内', () => {
    for (const fps of [1, 2, 5, 10, 12, 15, 20, 24, 30, 45, 60, 90, 144, 1000]) {
      const value = chooseLatencyMs(fps)

      expect(value, `fps=${fps}`).toBeGreaterThanOrEqual(LATENCY_MIN_MS)
      expect(value, `fps=${fps}`).toBeLessThanOrEqual(LATENCY_MAX_MS)
    }
  })

  /**
   * 拿不到帧率 ⇒ <b>回到上限</b>。
   *
   * <p>
   * 🔥 这条防的是"把未知当成最小值"：`null` 走 `Math.max(0, ...)` 一类算法会得到 20 ms，
   * 于是旧被控端（不上报目标帧率）的会话全都变成"零缓冲"，一抖就掉帧 ——
   * 而页面上只会写"有点卡"。
   * </p>
   */
  it('帧率未知 / 非法 ⇒ 用上限（不知道路况就不赌）', () => {
    for (const fs of [null, undefined, 0, -1, Number.NaN, Number.POSITIVE_INFINITY]) {
      expect(chooseLatencyMs(fs as number | null | undefined), `fps=${String(fs)}`).toBe(
        LATENCY_MAX_MS,
      )
    }
  })

  /**
   * 声音与画面读的是<b>同一个值</b>。
   *
   * <p>
   * 🔥 A/V 对齐的唯一依据就是这一点（`RemoteAudioPlayer` 与 `RemoteVideoBuffer` 各自
   * 在每一帧现读，而不是各存一份副本）。所以这里钉的是"设置一次、两处都看得到"。
   * </p>
   */
  it('设置之后两条流读到的是同一个值', () => {
    expect(setPlaybackLatencyForFps(60)).toBe(33)
    expect(getPlaybackLatencyMs()).toBe(33)

    expect(setPlaybackLatencyForFps(12)).toBe(LATENCY_MAX_MS)
    expect(getPlaybackLatencyMs()).toBe(LATENCY_MAX_MS)
  })

  it('reset 之后回到上限（上一路的选择不许留在下一边上）', () => {
    setPlaybackLatencyForFps(60)
    expect(getPlaybackLatencyMs()).toBe(33)

    resetPlaybackLatency()
    expect(getPlaybackLatencyMs()).toBe(LATENCY_MAX_MS)
  })
})
