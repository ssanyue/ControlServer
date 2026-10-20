/**
 * 断线重连的**退避决策**（M7.13）。
 *
 * <p>
 * 🔥 <b>为什么把它从组件里搬出来</b>：这段规则有三条容易写错的判断
 * （"正在连就别再排一次"、"同一次断线被报两次只能算一次"、"次数用完必须放弃"），
 * 而它们全都<b>没有任何界面证据</b> —— 错了的表现只是"重试得比该有的快/慢"，
 * 或者"被控端关机后每 20 秒打一次建会话请求"。写在 `.vue` 里就只能靠人眼读代码，
 * 于是它和 M7.11 那条横幅一样：<b>没人读，就会一直错下去</b>。
 * </p>
 *
 * <p>
 * ⚠️ 本模块<b>只管状态机</b>：不知道机器是谁、也不知道窗口开没开。
 * 那些是组件的判断，这里只回答"这一次触发该变成什么"。
 * </p>
 */

/** 第 1..5 次尝试前分别等几秒（指数退避，合计约 31 秒）。 */
export const RECONNECT_DELAYS = [1, 2, 4, 8, 16] as const

/** 重连状态机的四个状态（与组件里的 `reconnect.state` 同义）。 */
export type ReconnectState = 'idle' | 'waiting' | 'connecting' | 'failed'

/**
 * 一次触发的处置结果。
 *
 * - `wait`：安排一次重连，`delayMs` 之后执行，`attempt` 是**这一次**的序号（从 1 起）；
 * - `merge`：已经有一个倒计时在等了，**只更新原因**，不消耗次数；
 * - `ignore`：正在连 / 已经放弃，什么都不做；
 * - `giveup`：次数用完，进入 `failed`。
 */
export type ReconnectAction = 'wait' | 'merge' | 'ignore' | 'giveup'

export interface ReconnectPlan {
  action: ReconnectAction
  /** 这一次（或继续沿用的）尝试序号。 */
  attempt: number
  /** `action === 'wait'` 时等多久（毫秒）；其它情况为 `0`。 */
  delayMs: number
}

/**
 * 根据当前状态与已用次数，算出这次触发该怎么办。
 *
 * <p>
 * 🔥 <b>三条规则各自的来由</b>：
 * </p>
 * <list type="bullet">
 *   <item><b>`connecting` ⇒ `ignore`</b>：已经有一个建会话请求在飞，
 *         再排一个只会得到 409「那台机器正忙」，而那条错误会覆盖真正的原因；</item>
 *   <item><b>`waiting` ⇒ `merge`</b>：同一次断线会被 `SessionChanged` 推送
 *         与 5 秒一次的列表轮询<b>各报一次</b>。不合并的话，一次真实断线
 *         直接烧掉两格次数，5 次机会只剩 2 次；</item>
 *   <item><b>`failed` ⇒ `ignore`</b>：已经放弃过。这时再让同一个断线事件把它拉回
 *         `waiting`，页面上会出现"放弃 → 又开始倒数"的循环，而用户什么都没点。</item>
 * </list>
 *
 * @param state 当前状态。
 * @param usedAttempts 已经用掉的次数（`attempt` 字段的当前值）。
 * @param delays 每一档的等待秒数。默认 {@link RECONNECT_DELAYS}。
 */
export function planReconnect(
  state: ReconnectState,
  usedAttempts: number,
  delays: readonly number[] = RECONNECT_DELAYS,
): ReconnectPlan {
  if (state === 'connecting' || state === 'failed') {
    return { action: 'ignore', attempt: usedAttempts, delayMs: 0 }
  }

  if (state === 'waiting') {
    return { action: 'merge', attempt: usedAttempts, delayMs: 0 }
  }

  const next = usedAttempts + 1

  // 次数用完 ⇒ 放弃。**必须有上限**：被控端关机时无限重试＝每 20 秒
  // 往中心打一次建会话请求，永远不会好，而日志里全是"会话建立失败"。
  if (next > delays.length) {
    return { action: 'giveup', attempt: usedAttempts, delayMs: 0 }
  }

  return { action: 'wait', attempt: next, delayMs: delays[next - 1] * 1000 }
}

/**
 * 重连条上那句"还有几秒"。
 *
 * <p>
 * ⚠️ 向上取整：剩 0.4 秒时显示"1 秒后"比显示"0 秒后"更诚实 ——
 * 后者会让现场以为"卡住了，没有在倒计时"。
 * </p>
 */
export function secondsLeft(dueAtMs: number, nowMs: number): number {
  return Math.max(0, Math.ceil((dueAtMs - nowMs) / 1000))
}
