<script setup lang="ts">
import { computed, reactive } from 'vue'
import type { ButtonConfig, ComboButton, SubButtonSet, Tile } from '../types'
import { clampSize, imageUrl } from '../format'
import { LICENSE_BLOCKED_HINT, useLicenseBlocked } from '../licenseGate'

const props = defineProps<{
  tiles: Tile[]
  freeLayout: boolean
  /**
   * 正在执行中的按钮 / 子按钮 Id 集合。
   *
   * 用集合而不是单个 Id —— 多个按钮可以**同时**在跑（见 App.vue 的 `runningIds`），
   * 传单值会让后一个把前一个的"发送中"状态顶掉。
   */
  busyIds: Set<string>
  /** 刚刚执行过的按钮 Id 集合（短暂高亮）。 */
  justRanIds: Set<string>
}>()

const emit = defineEmits<{
  (e: 'run-button', button: ButtonConfig): void
  /** 触发一个组合按钮（2026-10-15，需求⑤）：服务端会按成员顺序逐个执行。 */
  (e: 'run-combo', combo: ComboButton): void
  (e: 'open-set', set: SubButtonSet): void
}>()

/** 图片加载失败的 Id 集合 —— 回退到纯色，且不再反复重试。 */
const brokenImages = reactive(new Set<string>())

/**
 * 授权闸门停摆时，**按钮**瓦片置灰；**集合**瓦片不受影响。
 *
 * 为什么分开：集合瓦片点开的是子按钮弹层（只读导航），里面的子按钮才执行动作。
 * 把集合也禁掉等于"没授权就连看都看不了"，而看板本来就是特意放行给现场看
 * "为什么不能用"的。子按钮的禁用放在 `SubButtonSheet` 里各管各的。
 */
const blocked = useLicenseBlocked()

function tileDisabled(t: AnyTile): boolean {
  if (isBusy(t)) return true
  return blocked.value && t.kind === 'button'
}

function tileTitle(t: AnyTile): string {
  return tileDisabled(t) && !isBusy(t) ? LICENSE_BLOCKED_HINT : tileLabel(t)
}

function tileSize(width: number, height: number) {
  return {
    width: `${clampSize(width, 96, 280, 140)}px`,
    height: `${clampSize(height, 52, 180, 80)}px`,
  }
}

function labelFont(size: number) {
  return `${clampSize(size, 10, 28, 13)}px`
}

/**
 * 三类瓦片共有的"外观字段"。
 *
 * 组合按钮的外观字段与主按钮**逐字段同名**（颜色 / 字号 / 图 / 宽高 / 坐标），
 * 所以这里只需要挑出对象，不必给每一处再写一遍三元 ——
 * 那种写法的代价是"以后再加一种瓦片就要改八个地方"，而漏掉任何一处都不会编译报错。
 */
function looks(t: AnyTile) {
  return t.kind === 'button' ? t.button : t.kind === 'combo' ? t.combo : t.set
}

/** 组合按钮的默认底色与主按钮一致（#512DA8）；子按钮集用的是青色，这里维持各自主色。 */
function defaultColor(t: AnyTile): string {
  return t.kind === 'set' ? '#00695C' : '#512DA8'
}

function delayTotal(actions: { delayMs?: number }[] | undefined): number {
  return (actions ?? []).reduce((sum, a) => sum + (a.delayMs ?? 0), 0)
}

/**
 * 自由排布：坐标非 0,0 的瓦片绝对定位，其余仍走流式布局
 * （模型里 <c>LayoutX/LayoutY = 0</c> 的语义就是"未定位，用流式"）。
 */
const positioned = computed(() =>
  props.freeLayout
    ? props.tiles.filter((t) => {
        const l = looks(t)
        return (l.layoutX || l.layoutY) !== 0
      })
    : [],
)

const flowing = computed(() =>
  props.tiles.filter((t) => !positioned.value.includes(t)),
)

const canvasHeight = computed(() => {
  let max = 0
  for (const t of positioned.value) {
    const l = looks(t)
    max = Math.max(max, (l.layoutY || 0) + clampSize(l.height, 52, 180, 80))
  }
  return max > 0 ? `${max + 16}px` : '0px'
})

type AnyTile = Tile

function tileKey(t: AnyTile): string {
  return looks(t).id
}

/** 这一格是否正在执行（并发下按各自 Id 独立判定）。 */
function isBusy(t: AnyTile): boolean {
  return props.busyIds.has(tileKey(t))
}

/** 这一格是否刚执行完（短暂高亮）。 */
function isJustRan(t: AnyTile): boolean {
  return props.justRanIds.has(tileKey(t))
}

function tileLabel(t: AnyTile): string {
  return looks(t).label
}

function tileFont(t: AnyTile): string {
  return labelFont(looks(t).nameFontSize)
}

/** 瓦片外观：三类共用一套算法，只有默认底色不同。 */
function tileStyle(t: AnyTile) {
  const l = looks(t)
  const url = brokenImages.has(tileKey(t)) ? null : imageUrl(l.buttonImage)

  return {
    ...tileSize(l.width, l.height),
    background: l.buttonColor || defaultColor(t),
    color: l.textColor || '#FFFFFF',
    backgroundImage: url ? `url("${url}")` : undefined,
    backgroundSize: 'cover',
    backgroundPosition: 'center',
  }
}

function tilePosition(t: AnyTile) {
  const l = looks(t)
  return { left: `${l.layoutX || 0}px`, top: `${l.layoutY || 0}px` }
}

/** 背景图 URL（模板里用）。 */
function tileImage(t: AnyTile): string | null {
  return brokenImages.has(tileKey(t)) ? null : imageUrl(looks(t).buttonImage) ?? null
}

function subLine(t: AnyTile): string {
  if (t.kind === 'button') {
    const n = t.button.actions?.length ?? 0
    const d = delayTotal(t.button.actions)
    return d > 0 ? `${n} 个动作 · 延迟 ${d}ms` : `${n} 个动作`
  }

  if (t.kind === 'combo') {
    const steps = t.combo.steps ?? []

    // 段间延迟只算 **n-1** 段：最后一步的延迟永远不会生效（服务端也会把它归零）。
    const between = steps.slice(0, -1).reduce((sum, st) => sum + (st.delayMs ?? 0), 0)

    return between > 0 ? `${steps.length} 个按钮 · 间隔 ${between}ms` : `${steps.length} 个按钮`
  }

  return `${t.set.subButtons?.length ?? 0} 个子按钮 · ${t.set.targetIp}:${t.set.targetPort}`
}

function onClick(t: AnyTile) {
  if (t.kind === 'button') emit('run-button', t.button)
  else if (t.kind === 'combo') emit('run-combo', t.combo)
  else emit('open-set', t.set)
}

function onImageError(t: AnyTile) {
  brokenImages.add(tileKey(t))
}
</script>

<template>
  <div v-if="tiles.length === 0" class="empty">
    <strong>还没有任何按钮</strong>
    打开「设置」标签页，导入旧版导出的 <code>.controlbak</code> 备份文件，即可把按钮搬过来。
  </div>

  <div v-else>
    <!-- 自由排布层（坐标非 0 的瓦片） -->
    <div v-if="positioned.length > 0" class="canvas" :style="{ height: canvasHeight }">
      <button
        v-for="t in positioned"
        :key="tileKey(t)"
        class="tile positioned"
        :class="{ busy: isBusy(t), ran: isJustRan(t), blocked: blocked && t.kind === 'button' }"
        :style="{ ...tileStyle(t), ...tilePosition(t) }"
        :disabled="tileDisabled(t)"
        :title="tileTitle(t)"
        @click="onClick(t)"
      >
        <img v-if="tileImage(t)" class="bg-img" :src="tileImage(t)!" alt="" @error="onImageError(t)" />
        <span class="tile-label" :style="{ fontSize: tileFont(t) }">{{ tileLabel(t) }}</span>
        <span class="tile-sub">{{ isBusy(t) ? '发送中…' : subLine(t) }}</span>
        <span v-if="t.kind === 'set'" class="tile-flag">集</span>
        <span v-else-if="t.kind === 'combo'" class="tile-flag combo">组</span>
      </button>
    </div>

    <!-- 流式布局层 -->
    <div class="flow">
      <button
        v-for="t in flowing"
        :key="tileKey(t)"
        class="tile"
        :class="{
          busy: isBusy(t),
          ran: isJustRan(t),
          'is-set': t.kind === 'set',
          blocked: blocked && t.kind === 'button',
        }"
        :style="tileStyle(t)"
        :disabled="tileDisabled(t)"
        :title="tileTitle(t)"
        @click="onClick(t)"
      >
        <img v-if="tileImage(t)" class="bg-img" :src="tileImage(t)!" alt="" @error="onImageError(t)" />
        <span class="tile-label" :style="{ fontSize: tileFont(t) }">{{ tileLabel(t) }}</span>
        <span class="tile-sub">{{ isBusy(t) ? '发送中…' : subLine(t) }}</span>
        <span v-if="t.kind === 'set'" class="tile-flag">集</span>
        <span v-else-if="t.kind === 'combo'" class="tile-flag combo">组</span>
      </button>
    </div>
  </div>
</template>

<style scoped>
.canvas {
  position: relative;
  width: 100%;
}

.flow {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  align-items: flex-start;
}

.tile {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 2px;
  padding: 6px 10px;
  border: 1px solid rgba(0, 0, 0, 0.12);
  border-radius: 10px;
  box-shadow: var(--shadow);
  text-align: center;
  overflow: hidden;
  line-height: 1.35;
  transition: transform 0.08s, box-shadow 0.12s, opacity 0.12s;
}

.tile.positioned {
  position: absolute;
}

.tile:hover:not(:disabled) {
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(16, 24, 40, 0.16);
  color: inherit;
  border-color: rgba(0, 0, 0, 0.24);
}

.tile:active:not(:disabled) {
  transform: translateY(0);
}

.tile:disabled {
  opacity: 1;
  cursor: progress;
}

/*
  未授权置灰（M3）：与 `busy`（发送中，saturate 0.6）刻意用不同的手法 ——
  两者语义完全不同，若都用同一个滤镜，用户会把"没授权"误读成"正在发送"。
  `not-allowed` 的光标 + 明显的降透明度，一眼能看出是"不让点"而不是"在忙"。
*/
.tile.blocked {
  opacity: 0.45;
  filter: grayscale(0.7);
  cursor: not-allowed;
}

.tile.is-set {
  border-style: dashed;
  border-width: 2px;
}

.tile.busy {
  filter: saturate(0.6);
}

.tile.ran {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.tile-label {
  font-weight: 500;
  word-break: break-word;
  z-index: 1;
}

.tile-sub {
  font-size: 10px;
  opacity: 0.78;
  z-index: 1;
}

.tile-flag {
  position: absolute;
  top: 4px;
  right: 5px;
  font-size: 10px;
  padding: 0 4px;
  border-radius: 3px;
  background: rgba(255, 255, 255, 0.22);
  z-index: 1;
}

/* 组合按钮的角标（2026-10-15）：用暖色把它与「集」区分开 ——
   两者都是"容器"，现场最怕的是点错了那一格。 */
.tile-flag.combo {
  background: rgba(255, 193, 7, 0.85);
  color: #3e2723;
}

.bg-img {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: cover;
}

code {
  font-family: var(--mono);
  font-size: 12px;
  background: var(--panel-soft);
  border: 1px solid var(--border);
  border-radius: 4px;
  padding: 0 4px;
}
</style>
