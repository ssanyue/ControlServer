<script setup lang="ts">
/**
 * 时序折线图（M4.2，零依赖手写 SVG）。
 *
 * 为什么手写而不是引一个图表库：
 *   · 只需要"折线 + 网格 + 刻度"这点能力，引 ECharts/Chart.js 会给一个
 *     **离线内网部署**的前端凭空加上几百 KB（而它必须打进 wwwroot 再嵌进程序集）；
 *   · 本项目最要紧的一条数据纪律是"**null 必须断开折线**"，图表库默认会把
 *     null 当 0 或跳过连接 —— 那正好是本项目明确禁止的那件事。
 *
 * ⚠️ **null 处理是本组件的核心语义，不是细节**：
 *   采样点某个指标为 null（采不到 / 首帧还没基线）时，折线在那里**断开**，
 *   而不是连一条直线过去。连过去等于凭空编了一段数据出来。
 *
 * 🔥 同一套语义还担着第二件事（2026-10-16）：**客户端离线的时间段，曲线留空**。
 *   服务端在空白够久的地方插一个"全 null 的断点标记"（`isGap`，见
 *   `AgentMetricsAggregator.GapThreshold` / `MarkGaps`），折线自动在那里断开 ——
 *   本组件**不需要**知道"多久算离线"，那是服务端的事（唯一出处）。
 *   本组件只多做两件小事：给空白垫一层浅底衬（`gapBands`）、悬停时把
 *   六个 `—` 换成一句"这段没有数据"（断线本身是沉默的，不说就像图表坏了）。
 */
import { computed, ref } from 'vue'
import type { AgentMetricsPointDto, ChartSeries } from '../types'
import { beijingIso, fmtDateTime } from '../format'

const props = withDefaults(
  defineProps<{
    points: AgentMetricsPointDto[]
    series: ChartSeries[]
    /** 时间轴起点（ISO，带 +08:00）。不传则用第一个点的时刻。 */
    from?: string | null
    /** 时间轴终点。不传则用最后一个点的时刻。 */
    to?: string | null
    /** 纵轴上限；null = 按数据自动（留 10% 余量）。 */
    yMax?: number | null
    /** 纵轴下限，默认 0（本项目的指标没有负值）。 */
    yMin?: number
    /** 纵轴刻度文本。 */
    axisFormat?: (v: number) => string
    /** 悬停读数里每一项的文本。 */
    valueFormat?: (v: number) => string
    /** 绘图高度（viewBox 单位）。 */
    height?: number
    emptyText?: string
  }>(),
  {
    from: null,
    to: null,
    yMax: null,
    yMin: 0,
    height: 150,
    emptyText: '这段时间没有采样数据',
  },
)

/** viewBox 逻辑宽度。整张图按它等比缩放到容器宽度。 */
const VIEW_W = 720
/** 左侧留给纵轴刻度、右侧留一点边距。 */
const PAD_LEFT = 54
const PAD_RIGHT = 14
const PAD_TOP = 10
const PAD_BOTTOM = 22

const hoverIndex = ref<number | null>(null)

/**
 * 从点上取一个字段（键宽化）。
 *
 * ⚠️ 为什么要绕这一道：`ChartSeries.key` 是 `string`（逐设备曲线会摊进
 * `d0` / `d0r` 这种运行时构造的槽位字段），而点仍然是 `AgentMetricsPointDto`。
 * 直接 `p[s.key]` 过不了类型检查，硬转一次比把这个组件的入参类型改成
 * 带索引签名的形状更安全 —— 后者会波及 `AgentMetricsPointDto` 的所有使用方。
 */
function raw(point: AgentMetricsPointDto, key: string): unknown {
  return (point as unknown as Record<string, unknown>)[key]
}

/** 取一个有限的数值；不是数（null / 未定义 / NaN）返回 null。 */
function numberAt(point: AgentMetricsPointDto | null, key: string): number | null {
  if (!point) return null
  const v = raw(point, key)
  return typeof v === 'number' && Number.isFinite(v) ? v : null
}

const plotW = VIEW_W - PAD_LEFT - PAD_RIGHT
const plotH = computed(() => Math.max(40, props.height - PAD_TOP - PAD_BOTTOM))

/** 时间轴范围（毫秒）。两端相同（只有一个点）时撑开 1 分钟，避免除零。 */
const timeRange = computed(() => {
  const first = props.from ?? props.points[0]?.at
  const last = props.to ?? props.points[props.points.length - 1]?.at

  let a = first ? Date.parse(first) : NaN
  let b = last ? Date.parse(last) : NaN

  if (!Number.isFinite(a) || !Number.isFinite(b)) {
    a = 0
    b = 60_000
  }

  if (b <= a) b = a + 60_000

  return { a, b }
})

const yRange = computed(() => {
  const values: number[] = []

  for (const p of props.points) {
    for (const s of props.series) {
      const v = numberAt(p, s.key)
      if (v !== null) values.push(v)
    }
  }

  const min = props.yMin

  if (props.yMax !== null && props.yMax !== undefined) {
    return { min, max: props.yMax > min ? props.yMax : min + 1 }
  }

  if (values.length === 0) return { min, max: min + 1 }

  const peak = Math.max(...values)

  // 峰值极小时（例如整段都是 0.2%），用一个最小量程免得把噪声放大成锯齿山。
  const max = Math.max(peak * 1.1, min + 1)

  return { min, max }
})

function xOf(iso: string): number {
  const t = Date.parse(iso)
  const { a, b } = timeRange.value
  const ratio = Number.isFinite(t) ? (t - a) / (b - a) : 0
  return PAD_LEFT + Math.min(1, Math.max(0, ratio)) * plotW
}

function yOf(v: number): number {
  const { min, max } = yRange.value
  const ratio = (v - min) / (max - min)
  return PAD_TOP + (1 - Math.min(1, Math.max(0, ratio))) * plotH.value
}

/** 纵轴刻度（5 条：min → max）。 */
const yTicks = computed(() => {
  const { min, max } = yRange.value
  const format = props.axisFormat

  return Array.from({ length: 5 }, (_, i) => {
    const value = min + ((max - min) * i) / 4
    return {
      y: yOf(value),
      text: format ? format(value) : value.toFixed(0),
    }
  })
})

/** 横轴刻度（起 / 中 / 止）。 */
const xTicks = computed(() => {
  const { a, b } = timeRange.value

  return [a, (a + b) / 2, b].map((t, i) => ({
    x: PAD_LEFT + (plotW * i) / 2,
    anchor: i === 0 ? 'start' : i === 2 ? 'end' : 'middle',
    // ⚠️ 用 beijingIso 而不是 toISOString()：后者产出 `Z`（UTC），
    //    被 fmtDateTime 一截会少显示 8 小时。
    text: fmtDateTime(beijingIso(t)),
  }))
})

/**
 * 每条曲线的折线路径。
 *
 * **按 null 切段**：返回的是若干条独立路径，null 处断开（见文件头注释）。
 * 只有一个点的段画成一个极短的线段 —— 单点用 `M` 配合 `stroke-linecap="round"`
 * 什么都不显示，而那恰恰是"刚接上、只有一个采样点"时的画面。
 */
const paths = computed(() =>
  props.series.map((s) => ({
    ...s,
    segments: splitSegments(s.key),
  })),
)

function splitSegments(key: string): string[] {
  const segments: string[][] = []
  let current: string[] = []

  for (const p of props.points) {
    const v = numberAt(p, key)

    if (v === null) {
      if (current.length > 0) {
        segments.push(current)
        current = []
      }
      continue
    }

    current.push(`${xOf(p.at).toFixed(1)},${yOf(v).toFixed(1)}`)
  }

  if (current.length > 0) segments.push(current)

  return segments.map((pts) =>
    pts.length === 1 ? `M${pts[0]} l0.6,0` : `M${pts.join(' L')}`,
  )
}

/**
 * 「这段时间没有数据」的区间（2026-10-16）。
 *
 * 服务端会在空白超过判据处插一个全 null 的**断点标记**（`IsGap`），折线因此在它那里
 * 自然断开 —— 但**断开本身是沉默的**：用户看见一条断掉的线，第一反应是"图表坏了"。
 * 所以再给这段空白垫一层浅底衬，让"留空"看起来是**有意的**。
 *
 * 区间取「标记点之前那个真实点 → 之后那个真实点」：这才与折线上**真正空掉的那一段**
 * 严丝合缝（标记点只是落在空白起点的那一格上，不是空白的全部）。
 * 用 `isGap` 而不是"看值是不是全 null"来认标记：见 `AgentMetricsPointDto.isGap`。
 */
const gapBands = computed(() => {
  const bands: { x: number; width: number }[] = []
  const pts = props.points

  for (let i = 0; i < pts.length; i += 1) {
    if (!pts[i].isGap) continue

    // 结尾的空白不需要（也没有）标记点，但真出现时也不画 —— 画了会拖到画布外。
    const before = pts[i - 1]
    const after = pts[i + 1]
    if (!before || !after) continue

    const x1 = xOf(before.at)
    const x2 = xOf(after.at)

    bands.push({ x: Math.min(x1, x2), width: Math.max(1, Math.abs(x2 - x1)) })
  }

  return bands
})

/** 悬停到的那个点（读数条用）。 */
const hoverPoint = computed(() =>
  hoverIndex.value === null ? null : (props.points[hoverIndex.value] ?? null),
)

/**
 * 悬停到了一个断点标记上 —— 读数条要把"全是 `—`"改成一句人话。
 *
 * 六个 `—` 容易被读成"这台机器此刻什么都没采到"，而事实是"那段时间它不在线/没上报"。
 */
const hoverIsGap = computed(() => hoverPoint.value?.isGap === true)

function onPointerMove(event: PointerEvent) {
  if (props.points.length === 0) return

  const svg = event.currentTarget as SVGSVGElement
  const rect = svg.getBoundingClientRect()
  const vx = ((event.clientX - rect.left) / Math.max(1, rect.width)) * VIEW_W

  let best = 0
  let bestDistance = Number.POSITIVE_INFINITY

  for (let i = 0; i < props.points.length; i += 1) {
    const d = Math.abs(xOf(props.points[i].at) - vx)
    if (d < bestDistance) {
      bestDistance = d
      best = i
    }
  }

  hoverIndex.value = best
}

/** 悬停标记点的纵坐标；这条曲线在该点没有值时给出图外的哨兵值（不画点）。 */
function hoverY(s: ChartSeries): number {
  const v = numberAt(hoverPoint.value, s.key)
  return v === null ? -99 : yOf(v)
}

function readoutOf(p: AgentMetricsPointDto, s: ChartSeries): string {
  const v = numberAt(p, s.key)
  if (v === null) return '—'
  return props.valueFormat ? props.valueFormat(v) : v.toFixed(2)
}
</script>

<template>
  <div class="metrics-chart">
    <div v-if="points.length === 0" class="metrics-empty">{{ emptyText }}</div>

    <template v-else>
      <div class="metrics-legend">
        <span v-for="s in series" :key="String(s.key)" class="metrics-legend-item">
          <i class="metrics-swatch" :style="{ background: s.color }" />
          {{ s.label }}
        </span>
      </div>

      <svg
        class="metrics-svg"
        :viewBox="`0 0 ${VIEW_W} ${height}`"
        role="img"
        @pointermove="onPointerMove"
        @pointerleave="hoverIndex = null"
      >
        <!--
          空白底衬（2026-10-16）：客户端离线 / 没有采样的时间段。
          画在最底层（网格与折线之下），所以它只"说明"空白，不遮挡任何读数。
        -->
        <rect
          v-for="(b, i) in gapBands"
          :key="`gap${i}`"
          :x="b.x"
          :y="PAD_TOP"
          :width="b.width"
          :height="plotH"
          class="metrics-gap"
        />

        <!-- 网格 + 纵轴刻度 -->
        <g>
          <line
            v-for="t in yTicks"
            :key="`g${t.y}`"
            :x1="PAD_LEFT"
            :x2="VIEW_W - PAD_RIGHT"
            :y1="t.y"
            :y2="t.y"
            stroke="var(--border)"
            stroke-width="1"
            vector-effect="non-scaling-stroke"
          />
          <text
            v-for="t in yTicks"
            :key="`y${t.y}`"
            :x="PAD_LEFT - 6"
            :y="t.y + 3.5"
            text-anchor="end"
            class="metrics-axis"
          >
            {{ t.text }}
          </text>
        </g>

        <!-- 横轴刻度 -->
        <text
          v-for="t in xTicks"
          :key="`x${t.x}`"
          :x="t.x"
          :y="height - 6"
          :text-anchor="t.anchor"
          class="metrics-axis"
        >
          {{ t.text }}
        </text>

        <!-- 悬停竖线 -->
        <line
          v-if="hoverPoint"
          :x1="xOf(hoverPoint.at)"
          :x2="xOf(hoverPoint.at)"
          :y1="PAD_TOP"
          :y2="PAD_TOP + plotH"
          stroke="var(--border-strong)"
          stroke-width="1"
          stroke-dasharray="3 3"
          vector-effect="non-scaling-stroke"
        />

        <!-- 折线 -->
        <template v-for="s in paths" :key="String(s.key)">
          <path
            v-for="(d, i) in s.segments"
            :key="`${String(s.key)}-${i}`"
            :d="d"
            fill="none"
            :stroke="s.color"
            :stroke-dasharray="s.dash ?? undefined"
            stroke-width="1.6"
            stroke-linejoin="round"
            stroke-linecap="round"
            vector-effect="non-scaling-stroke"
          />
        </template>

        <!-- 悬停点标记 -->
        <template v-if="hoverPoint">
          <circle
            v-for="s in series"
            :key="`h${String(s.key)}`"
            :cx="xOf(hoverPoint.at)"
            :cy="hoverY(s)"
            r="3"
            :fill="s.color"
          />
        </template>
      </svg>

      <!-- 读数：悬停时显示该点，否则显示最后一个点（"现在"） -->
      <div class="metrics-readout">
        <span class="metrics-readout-time">
          {{ hoverPoint ? fmtDateTime(hoverPoint.at) : fmtDateTime(points[points.length - 1].at) }}
          <em v-if="!hoverPoint">（最新）</em>
          <!-- 断点标记上六个 `—` 会被读成"采集坏了"，这里直说是什么事。 -->
          <em v-if="hoverIsGap" class="metrics-gap-tag">（这段没有数据）</em>
        </span>
        <span
          v-for="s in series"
          :key="`r${String(s.key)}`"
          class="metrics-readout-item"
        >
          <i class="metrics-swatch" :style="{ background: s.color }" />
          {{ s.label }}
          <b>{{ hoverPoint ? readoutOf(hoverPoint, s) : readoutOf(points[points.length - 1], s) }}</b>
        </span>
      </div>
    </template>
  </div>
</template>

<style scoped>
.metrics-chart {
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--panel);
  padding: 8px 10px 6px;
}

.metrics-legend {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  margin-bottom: 4px;
  font-size: 12px;
  color: var(--muted);
}

.metrics-legend-item {
  display: inline-flex;
  align-items: center;
  gap: 5px;
}

.metrics-swatch {
  width: 8px;
  height: 8px;
  border-radius: 2px;
  display: inline-block;
  flex: none;
}

.metrics-svg {
  display: block;
  width: 100%;
  height: auto;
  touch-action: none;
}

.metrics-axis {
  font-size: 10px;
  fill: var(--muted);
  font-family: var(--mono);
}

.metrics-empty {
  padding: 26px 0;
  text-align: center;
  color: var(--muted);
  font-size: 13px;
}

/*
 * 空白段的底衬。用 `--border-strong` 加很低的不透明度，而不是写死一个灰：
 * 它在浅色主题下是极浅的一层灰（看得出与白底有别），将来加暗色主题时自动跟着走。
 */
.metrics-gap {
  fill: var(--border-strong);
  opacity: 0.3;
}

.metrics-readout {
  display: flex;
  flex-wrap: wrap;
  gap: 14px;
  align-items: center;
  border-top: 1px solid var(--border);
  margin-top: 4px;
  padding-top: 5px;
  font-size: 12px;
  color: var(--muted);
}

.metrics-readout-time {
  font-family: var(--mono);
}

.metrics-readout-time em {
  font-style: normal;
  color: var(--ok);
}

/* 断点上的说明：中性色，别用 `--ok` 那抹绿（那是"最新"专属，混在一起会看着像好消息）。 */
.metrics-readout-time .metrics-gap-tag {
  color: var(--muted);
}

.metrics-readout-item {
  display: inline-flex;
  align-items: center;
  gap: 5px;
}

.metrics-readout-item b {
  color: var(--text);
  font-family: var(--mono);
}
</style>
