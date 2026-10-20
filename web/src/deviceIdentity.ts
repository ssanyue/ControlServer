import type { AgentDeviceKind, AgentDeviceReadingDto, AgentMetricsPointDto } from './types'
import { AGENT_DEVICE_KINDS } from './types'

/**
 * 把「一段时间窗口里的逐设备读数」还原成「**这台机器现在有哪些硬件**」。
 *
 * 单独成模块（而不是留在 `AgentDetailPanel.vue` 里）的原因：这里的判据有反直觉之处，
 * 值得能被单独喂数据验证；而且它是**纯函数** —— 不碰 Vue、不碰 DOM。
 *
 * ═══════════════════════════════════════════════════════════════
 * 🔥 **为什么身份不能是实例 Key**
 *
 *   GPU 的 `LUID`、Hyper-V 虚拟网卡的 `Id` 都是**运行时标识符 —— 系统每次启动重新分配**。
 *
 *   实测那台 `DESKTOP-1UKRNGI`（只装了 **1** 块 GTX 1060）在一天里重启 6 次，窗口内因此
 *   留下 **6 个两两不重叠的 LUID**；同一台机器的 `vEthernet (Default Switch)` 留下
 *   **5 个不同的 GUID**，而它们的名字一模一样。
 *
 *   拿"窗口内出现过的所有 Key 的并集"当条目，就会把同一块硬件列成 6 条同名选项，
 *   并把一条本该连续的曲线切成 6 段互不相干的片段。
 *   证据（真实库反查，含 LUID 区间与 uptime 归零的对应）见
 *   `.workbuddy/memory/2026-10-15.md` §5。
 *
 *   对照：磁盘（`disk0/1/2`）与 CPU（`cpu0`）的 Key **重启后是稳定的**，本来就没这个问题 ——
 *   这次改动对它们无感。
 *
 * ═══════════════════════════════════════════════════════════════
 * ⚖️ **代价（必须知道）**
 *
 *   改用"名字"当跨重启的归并依据，换来的是另一个方向的缺失：
 *   **重命名一块网卡**（"以太网" → "展厅上联"）之后，"改名之前"那段历史会从列表里
 *   消失 —— 列表只反映**当前**那一代名字。
 *
 *   ⚠️ 但只要 **Key 没变，那段数据仍然画得出来**（取值走 Key，不走名字）——
 *      真正丢的只有"它在旧名字下也待过"这件事。要让它彻底断掉，得**改名与重启同时发生**，
 *      那很罕见。
 *
 *   取舍的理由：重启是**每天都会发生**、而且不可控的事；改名是**人工操作**，少得多，
 *   且"这个条目换了个名字"比"同一块硬件出现 6 条同名选项"好解释得多。
 *
 *   两条兜底：
 *     · **同一帧内**出现两个同名读数时**不归并**，而是加 `#n` —— 那才是真的多块同型号
 *       硬件（多卡机 / 多网卡），必须分开，否则它们的读数会被混成一条；
 *     · **名字为空**时退回用 Key（见 `groupKeyOf`）—— 空名字不含任何身份信息。
 */

/** 一块**物理硬件**。 */
export interface PhysicalDevice {
  /** 稳定 id：`kind` + 物理名（真的并存时带 `#n`）。 */
  id: string
  kind: AgentDeviceKind
  /** 展示名。**只有真的并存才带 `#n`。** */
  name: string
  /**
   * 这块硬件在窗口内用过的**全部实例 Key**（通常一个，重启过就是好几个）。
   * 画曲线时按时刻挑"当时还活着的那一个"，见 {@link pickReading}。
   */
  keys: string[]
}

/**
 * 采集抖动的容忍帧数。
 *
 * 遥测是**逐类**上报的（重启后头几帧常常只有磁盘 / 网卡），盯死"最新一帧"会让显卡条目
 * 闪一下不见；又不能无限往前找，否则已经拔掉的硬盘会永远赖在列表里
 * （现场口径：窗口内出现过、**现已不在**的硬件不单列）。
 */
export const LATEST_FRAME_LOOKBACK = 3

/**
 * 归并组键。
 *
 * ⚠️ 名字取不到时 `displayNameOf` 会退回 Key —— 那正是想要的：**空名字不含任何身份
 * 信息**，退回 Key 等于"这一条自己成一个身份，不跟任何别的合并"。
 *
 * ⚠️ 占位名（`GPU 适配器 1`，平台没问到型号时的退路）**照常参与归并**：那个编号来自
 * 计数器里的出现顺序，多少带点身份信息；而若"认不出型号"就不归并，就又回到
 * "同一块硬件被列成好几条"的老问题上去了 —— 那正是这次的病。
 * 代价是两块同型号、且都认不出型号的机器，重启后编号可能对调；那种情况下无解，
 * 但比"每次重启多一条"轻得多。
 */
function groupKeyOf(d: AgentDeviceReadingDto): string {
  return `${d.kind}\u0001${displayNameOf(d)}`
}

/** 展示名：名字为空时退回 Key，免得列表里出现一排空白条目。 */
function displayNameOf(d: AgentDeviceReadingDto): string {
  const name = (d.name ?? '').trim()
  return name === '' ? d.key : name
}

/**
 * 某一种类**最后一次出现**的那些读数（至多回看 `lookback` 帧）。
 *
 * 这是"别让采集抖动把一整类硬件从列表里抹掉"与"别让拔掉的硬件永远赖着"之间的取舍点。
 */
function lastFrameOf(
  points: AgentMetricsPointDto[],
  kind: string,
  lookback: number,
): AgentDeviceReadingDto[] | null {
  const stop = Math.max(0, points.length - lookback)

  for (let i = points.length - 1; i >= stop; i -= 1) {
    const list = (points[i].devices ?? []).filter((d) => d.kind === kind)
    if (list.length > 0) return list
  }

  return null
}

/** 帧内次序按 Key 排：与列表里 `#1`/`#2` 的先后一致，且跨帧稳定。 */
const byKey = (a: AgentDeviceReadingDto, b: AgentDeviceReadingDto): number =>
  a.key < b.key ? -1 : a.key > b.key ? 1 : 0

/**
 * 这台机器**现在**有哪些硬件 —— 硬件列表的唯一来源。
 *
 * 三步各有各的职责，顺序不能换：
 *   ① **同帧最大并存数** —— 某个组要不要发 `#n`。同一帧里同名 ≥2 条＝真的并存
 *      （多卡 / 多网卡）；跨帧各一条＝同一块硬件换了个身份，**直接归并**。
 *   ② **组 → 窗口内用过的全部 Key**（重启前那一代也收进来，曲线才接得上）。
 *   ③ **列表条目**：每个种类取它最后一次出现的那一帧。
 *
 * @param points 窗口内的采样点（按时刻升序）。
 * @param lookback 每类硬件至多往前找几帧，见 {@link LATEST_FRAME_LOOKBACK}。
 */
export function identifyPhysicalDevices(
  points: readonly AgentMetricsPointDto[],
  lookback: number = LATEST_FRAME_LOOKBACK,
): PhysicalDevice[] {
  if (points.length === 0) return []

  const maxConcurrent = new Map<string, number>()
  const groupKeys = new Map<string, string[]>()
  const slotVotes = new Map<string, Map<number, number>>()

  for (const p of points) {
    const perFrame = new Map<string, AgentDeviceReadingDto[]>()

    for (const d of p.devices ?? []) {
      const g = groupKeyOf(d)

      perFrame.set(g, [...(perFrame.get(g) ?? []), d])

      const keys = groupKeys.get(g) ?? []
      if (!keys.includes(d.key)) keys.push(d.key)
      groupKeys.set(g, keys)
    }

    for (const [g, list] of perFrame) {
      if (list.length > (maxConcurrent.get(g) ?? 0)) maxConcurrent.set(g, list.length)

      if (list.length <= 1) continue

      ;[...list].sort(byKey).forEach((d, i) => {
        const votes = slotVotes.get(d.key) ?? new Map<number, number>()
        votes.set(i + 1, (votes.get(i + 1) ?? 0) + 1)
        slotVotes.set(d.key, votes)
      })
    }
  }

  // 取众数而不是"首次出现的名次"：某一帧少报一块卡时，不至于把两块卡的 Key 混到一起。
  const slotOf = new Map<string, number>()

  for (const [key, votes] of slotVotes) {
    let best = 1
    let bestCount = -1

    for (const [slot, count] of votes) {
      if (count > bestCount) {
        bestCount = count
        best = slot
      }
    }

    slotOf.set(key, best)
  }

  const found = new Map<string, PhysicalDevice>()

  for (const kind of AGENT_DEVICE_KINDS) {
    const frame = lastFrameOf([...points], kind, lookback)
    if (!frame) continue

    const counter = new Map<string, number>()

    for (const d of [...frame].sort(byKey)) {
      const g = groupKeyOf(d)
      const shared = (maxConcurrent.get(g) ?? 1) > 1
      const n = (counter.get(g) ?? 0) + 1
      counter.set(g, n)

      const id = shared ? `${g}\u0001#${n}` : g

      found.set(id, {
        id,
        kind,
        // ⚠️ **只在真的并存时**才带序号：给一块孤零零的卡标上 `#1` 会让人以为还有 `#2`。
        name: shared ? `${displayNameOf(d)} #${n}` : displayNameOf(d),
        keys: shared
          ? (groupKeys.get(g) ?? []).filter((k) => (slotOf.get(k) ?? 1) === n)
          : (groupKeys.get(g) ?? []),
      })
    }
  }

  return [...found.values()]
}

/**
 * 这块硬件在某一刻的读数。
 *
 * ⚠️ **按"当时还活着的那一个 Key"取，而不是把同组所有 Key 的值加起来**：
 * 同一块卡重启前后的两个 LUID **永远不会同时有值**，相加只会把同一份占用算两遍 ——
 * 那是个"看着更大所以更像真事"的假象。
 * 真的并存（真的两块卡）在列表里已经是两条独立条目了，不会走到这里。
 */
export function pickReading(
  point: AgentMetricsPointDto,
  dev: PhysicalDevice,
): AgentDeviceReadingDto | null {
  const list = point.devices ?? []

  for (const key of dev.keys) {
    const hit = list.find((d) => d.key === key)
    if (hit) return hit
  }

  return null
}
