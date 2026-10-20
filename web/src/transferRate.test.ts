import { describe, expect, it } from 'vitest'
import { RateTracker } from './transferRate'

/**
 * 速率是**前端派生**的值（服务端只推 `receivedBytes`），这几条钉的是它的算法与边界。
 *
 * `T0` = 北京时间 2026-10-15 10:00:00 的**绝对毫秒**；`bj()` 造出服务端那种
 * "带 `+08:00` 偏移的 ISO"。测试里刻意自己传 `now`（不依赖 `Date.now()`），
 * 这样在任何时区的机器上跑出来的都是同一个数。
 */
const T0 = Date.parse('2026-10-15T10:00:00+08:00')

const MB = 1024 * 1024

/** 绝对毫秒 → 服务端格式的北京时间字符串（带 `+08:00`）。 */
function bj(ms: number): string {
  return new Date(ms + 8 * 3600 * 1000).toISOString().replace('Z', '+08:00')
}

describe('RateTracker', () => {
  it('没采过的行返回 null（渲染成 —，而不是 0 B/s）', () => {
    expect(new RateTracker().rate(999)).toBeNull()
  })

  it('一个字节都没收到时不报 0 B/s —— 那是"还没开始"，不是"网络空闲"', () => {
    const rates = new RateTracker()

    expect(rates.observe(1, T0 + 5_000, 0, bj(T0), null)).toBeNull()
  })

  it('只有一条事件时，用「自开始起的均值」兜底', () => {
    const rates = new RateTracker()

    // 服务端 10 秒前开始，此刻累计 10 MB ⇒ 1 MB/s
    expect(rates.observe(1, T0 + 10_000, 10 * MB, bj(T0), null)).toBeCloseTo(MB, 0)
  })

  it('有两条足够分开的事件时用滑窗吞吐，而不是整段均值', () => {
    const rates = new RateTracker()

    // 10 秒前开始、已经走了 100 MB（整段均值 10 MB/s），
    // 但最近 2 秒只走了 1 MB —— 用户盯着看的是"现在多快"，即后者。
    rates.observe(1, T0 + 10_000, 100 * MB, bj(T0), null)
    const rate = rates.observe(1, T0 + 12_000, 101 * MB, bj(T0), null)

    expect(rate).toBeCloseTo(MB / 2, 0)
  })

  it('两条事件挨得太近时不拿它当速率（400 ms 内的比值纯是噪声）', () => {
    const rates = new RateTracker()

    // 100 ms 走了 100 字节 ⇒ 字面算出来是 1000 B/s，但不可信 ⇒ 退回均值。
    rates.observe(1, T0 + 10_000, 10 * MB, bj(T0), null)
    const rate = rates.observe(1, T0 + 10_100, 10 * MB + 100, bj(T0), null)

    expect(rate).toBeCloseTo((10 * MB + 100) / 10.1, 0)
  })

  it('传完之后用服务端的完成时刻算均值，不受"现在是几点"影响', () => {
    const rates = new RateTracker()

    // 10:00:00 开始、10:00:20 完成、走了 8 MB ⇒ 0.4 MB/s。
    // `now` 远在完成之后（页面是后来才打开的）—— 那段空档不该算进时长里，
    // 否则一个传得很快的文件会显示成"慢得几乎没动"。
    const rate = rates.observe(1, T0 + 3_600_000, 8 * MB, bj(T0), bj(T0 + 20_000))

    expect(rate).toBeCloseTo(8 * MB / 20, 0)
  })

  it('不带时区偏移的时间戳按北京时间解释', () => {
    const rates = new RateTracker()

    // `2026-10-15T10:00:00` 就是北京的 10:00:00，与 `now`（北京 10:00:10 的绝对时刻）
    // 之间恰好 10 秒 ⇒ 5 MB / 10 s。
    //
    // ⚠️ 这条在 UTC+8 的机器上**钉不出差别**（本地时区恰好是 +8）；它的价值在
    // CI / 非 +8 的机器上 —— 那时若实现偷懒用了 `Date.parse` 原串，开始时刻会被当成
    // 晚 8 小时 ⇒ 差值变负 ⇒ 返回 null，这条就会红。
    const rate = rates.observe(1, T0 + 10_000, 5 * MB, '2026-10-15T10:00:00', null)

    expect(rate).toBeCloseTo(5 * MB / 10, 0)
  })

  it('这一瞬间算不出速率时保留上一次的值，不让数字闪回 —', () => {
    const rates = new RateTracker()

    const first = rates.observe(1, T0 + 10_000, 10 * MB, bj(T0), null)
    expect(first).toBeCloseTo(MB, 0)

    // 10 秒里一个字节都没动、而且这次没给 `startedAt` ⇒ 两条路都算不出。
    const after = rates.observe(1, T0 + 20_000, 10 * MB, null, null)

    expect(after).toBe(first)
    expect(rates.rate(1)).toBe(first)
  })

  it('长周期一路上报时，速率就是窗口内的真实吞吐（不随窗口滑动而漂）', () => {
    const rates = new RateTracker()

    // 每秒一条、每条多 1 MB，共 60 秒 —— 典型的大文件中途。
    for (let i = 1; i <= 60; i++) rates.observe(1, T0 + i * 1000, i * MB, bj(T0), null)

    // 20 秒窗口里收了 20 MB ⇒ 1 MB/s。窗口怎么滑都该是这个数。
    expect(rates.rate(1)).toBeCloseTo(MB, 0)
  })

  it('prune 只清掉不在列表里的行，活着的行速率原样保留', () => {
    const rates = new RateTracker()

    rates.observe(1, T0 + 10_000, 1 * MB, bj(T0), null)
    rates.observe(2, T0 + 10_000, 1 * MB, bj(T0), null)

    expect(rates.rate(2)).toBeCloseTo(1 * MB / 10, 0)

    rates.prune(new Set([2]))

    expect(rates.rate(1)).toBeNull()
    expect(rates.rate(2)).toBeCloseTo(1 * MB / 10, 0)
  })
})
