/**
 * 传输速率估算（纯逻辑：无 Vue、无副作用、`now` 由调用方传入）。
 *
 * 服务端**不推速率**（`TransferProgress` 只带 `receivedBytes` / `totalBytes` / `progressPct`），
 * 所以"现在多快"完全是前端从进度事件里派生的。
 *
 * ⚠️ 单独成文件而不是写在 `DistributionPanel.vue` 里：这是本轮唯一有算法的部分，
 * 抽出来才能被单测钉住 —— 组件内的私有函数在现有测试基建下（纯 node 环境、
 * 不挂载组件）根本测不到，等于没人看着。
 */

/** 一次采样：时刻（绝对毫秒）+ 那一刻的累计已收字节。 */
export interface RatePoint {
  t: number
  bytes: number
}

/** 一行的采样窗口。 */
export interface RateWindow {
  /** 采样点，按时间升序；只保留窗口内的（见 `RateTracker.observe`）。 */
  points: RatePoint[]
  /** 最近一次算出的速率（字节/秒）；算不出来时保留上一次的值。 */
  last: number | null
}

/**
 * 滑窗宽度（毫秒）。
 *
 * 取 20 秒：进度事件**约每 8 MB 一条**（服务端建议的 `ChunkSizeBytes`），
 * 于是两点间隔本身就在剧烈变化 —— 大文件在慢链路上两条事件能隔几十秒。
 * 直接用"相邻两点"的 `Δ字节/Δ时间` 当瞬时速率会一会儿几百 MB/s、一会儿 0；
 * 窗口够宽才平滑，而窗口内的平均正好就是这段时间的真实吞吐。
 */
export const RATE_WINDOW_MS = 20_000

/**
 * 单行最多留多少个点（防爆保险）。
 *
 * 时间窗通常先起作用；但当事件密到 500 ms 一条时，20 秒能塞下 41 个点 ⇒ 这里兜住。
 */
export const RATE_MAX_POINTS = 40

/** 两点至少隔这么久，比值才可信 —— `Δ时间` 太小的话除出来纯是噪声。 */
export const RATE_MIN_SPAN_MS = 400

/** 带时区偏移的 ISO 结尾（`Z` 或 `±HH:MM` / `±HHMM`）。 */
const OFFSET_RE = /(?:Z|[+-]\d{2}:?\d{2})$/

/**
 * ISO → 绝对毫秒。空 / 非法一律 `null`（**不是 0** —— 0 是 1970 年）。
 *
 * ⚠️ 服务端的时间戳一律带 `+08:00`。万一哪天来了个**不带偏移**的
 * （`2026-10-15T10:00:00`），`Date.parse` 会按**运行环境的本地时区**解释 ——
 * 在非 +8 的机器上直接错 8 小时。这里按契约给它补上北京偏移，宁可显式也不留这个静默坑。
 *
 * 注意：这个坑只在拿它跟 `Date.now()` 相减时才会暴露（两个都带偏移的字符串相减，
 * 偏移是常量、会抵消 —— 所以"时长算错"不会出现在那条路径上）。
 */
function parseIso(iso: string | null): number | null {
  if (!iso) return null

  const normalized = OFFSET_RE.test(iso) ? iso : `${iso}+08:00`
  const ms = Date.parse(normalized)

  return Number.isFinite(ms) ? ms : null
}

/**
 * 由采样算速率。两条路，按可信度排序：
 *
 *   ① **滑窗**：窗口内首尾两点的吞吐 —— 传输途中它最能反映"现在多快"；
 *   ② **均值兜底**：`已收字节 / (结束时刻 − 开始时刻)` —— 小文件只来一两条事件时
 *      唯一算得出的（那时滑窗里还不够两个点）。大文件在传时这条也常有值，
 *      因为 `startedAt` 是**服务端**写的、比第一条事件早。
 */
function deriveRate(
  window: RateWindow,
  now: number,
  bytes: number,
  startedAt: string | null,
  completedAt: string | null,
): number | null {
  if (window.points.length >= 2) {
    const first = window.points[0]
    const last = window.points[window.points.length - 1]
    const spanMs = last.t - first.t
    const deltaBytes = last.bytes - first.bytes

    if (spanMs >= RATE_MIN_SPAN_MS && deltaBytes > 0) return (deltaBytes / spanMs) * 1000
  }

  const started = parseIso(startedAt)
  // 一个字节都没收到时**不报 0 B/s** —— 那会被读成"网络完全空闲"，而实际是"还没开始"。
  if (started === null || bytes <= 0) return null

  // `completedAt` 还没拿到（仍在传）就用**此刻**。但它只在采样那一刻求值一次，
  // 结果被 `window.last` 冻结 —— 所以传完之后不会看着数字慢慢往下掉（分母在涨、分子不动）。
  const finished = parseIso(completedAt) ?? now
  if (finished <= started) return null

  return (bytes / (finished - started)) * 1000
}

/**
 * 一行的速率估算器：喂进"这一刻收了多少"，吐出"多快"。
 *
 * 用法就是 `DistributionPanel` 里那一套：每条进度事件 `observe()` 一次，
 * 渲染时 `rate()`；列表整体刷新后用 `prune()` 清掉已经不在列表里的行。
 */
export class RateTracker {
  private readonly windows = new Map<number, RateWindow>()

  /**
   * 记一次采样，返回**更新后**的速率（字节/秒）；这一瞬间算不出来时返回上一次的值。
   *
   * *保留上一次的值* 是刻意的：中途偶发采不到不该让屏幕上的数字闪回 `—`。
   *
   * @param id          明细行 id（`DistributionItemDto.id`）
   * @param now         采样时刻（绝对毫秒）。**由调用方传入**而不是内部取 `Date.now()` ——
   *                    否则这个类就没法被确定性地测。
   * @param bytes       该行**此刻累计**的已收字节（只增不减的那个值）
   * @param startedAt   服务端写的开始时刻（ISO），没有就传 null
   * @param completedAt 服务端写的完成时刻（ISO），还没完成就传 null
   */
  observe(
    id: number,
    now: number,
    bytes: number,
    startedAt: string | null,
    completedAt: string | null,
  ): number | null {
    let window = this.windows.get(id)

    if (!window) {
      window = { points: [], last: null }
      this.windows.set(id, window)
    }

    window.points.push({ t: now, bytes })

    // 丢出窗口外的点，但**至少留 2 个**（一个点算不出速率）。
    while (
      window.points.length > 2 &&
      (now - window.points[0].t > RATE_WINDOW_MS || window.points.length > RATE_MAX_POINTS)
    ) {
      window.points.shift()
    }

    const rate = deriveRate(window, now, bytes, startedAt, completedAt)
    if (rate !== null) window.last = rate

    return window.last
  }

  /** 当前的速率。没采过 / 采不出就是 `null`（渲染成 `—`，**不是** `0 B/s`）。 */
  rate(id: number): number | null {
    return this.windows.get(id)?.last ?? null
  }

  /** 清掉不在 `aliveIds` 里的窗口，防止长期运行越积越多。 */
  prune(aliveIds: Set<number>): void {
    for (const id of [...this.windows.keys()]) {
      if (!aliveIds.has(id)) this.windows.delete(id)
    }
  }
}
