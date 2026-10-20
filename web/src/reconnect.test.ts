import { describe, expect, it } from 'vitest'

import { RECONNECT_DELAYS, planReconnect, secondsLeft } from './reconnect'

describe('planReconnect', () => {
  it('空闲时第一次触发就安排一次重连，等 1 秒', () => {
    expect(planReconnect('idle', 0)).toEqual({ action: 'wait', attempt: 1, delayMs: 1000 })
  })

  it('逐次递增：1 / 2 / 4 / 8 / 16 秒', () => {
    const delays = [0, 1, 2, 3, 4].map((used) => planReconnect('idle', used).delayMs)

    expect(delays).toEqual([1000, 2000, 4000, 8000, 16000])
  })

  it('⚠️ 同一次断线被报两次只消耗一格（第二次是 merge，不是 wait）', () => {
    // SessionChanged 推送先来 ⇒ 进入 waiting
    const first = planReconnect('idle', 0)
    expect(first.action).toBe('wait')

    // 5 秒轮询又来一次 ⇒ 只能合并
    const second = planReconnect('waiting', first.attempt)

    expect(second.action).toBe('merge')
    expect(second.attempt).toBe(first.attempt)
    expect(second.delayMs).toBe(0)
  })

  it('正在重连时不叠第二个请求（否则第二个必然 409）', () => {
    expect(planReconnect('connecting', 2).action).toBe('ignore')
  })

  it('已经放弃过就不再自己起来（否则会出现"放弃 → 又开始倒数"的循环）', () => {
    expect(planReconnect('failed', 5).action).toBe('ignore')
  })

  it('次数用完必须放弃 —— 否则被控端关机后会一直每 20 秒打一次建会话请求', () => {
    expect(planReconnect('idle', RECONNECT_DELAYS.length).action).toBe('giveup')
    expect(planReconnect('idle', RECONNECT_DELAYS.length + 3).action).toBe('giveup')
  })

  it('次数上限就是 delays 的长度（不多试一次，也不少试一次）', () => {
    // 第 5 次仍然是 wait（这是最后一次机会）
    expect(planReconnect('idle', RECONNECT_DELAYS.length - 1).action).toBe('wait')
  })

  it('退避必须单调不减，且最长不超过 30 秒', () => {
    const all = RECONNECT_DELAYS.map((_, i) => planReconnect('idle', i).delayMs)

    for (let i = 1; i < all.length; i += 1) expect(all[i]).toBeGreaterThanOrEqual(all[i - 1])
    expect(Math.max(...all)).toBeLessThanOrEqual(30_000)
  })

  it('传空 delays ⇒ 第一次就放弃（用于测试或"不重连"的场合）', () => {
    expect(planReconnect('idle', 0, []).action).toBe('giveup')
  })
})

describe('secondsLeft', () => {
  it('向上取整：剩 400 毫秒显示 1 秒（显示 0 会像"卡住了"）', () => {
    expect(secondsLeft(1400, 1000)).toBe(1)
  })

  it('已经过点了就是 0，不给负数', () => {
    expect(secondsLeft(1000, 1500)).toBe(0)
  })

  it('整 5 秒显示 5', () => {
    expect(secondsLeft(6000, 1000)).toBe(5)
  })
})
