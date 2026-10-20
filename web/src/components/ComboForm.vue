<script setup lang="ts">
/**
 * 组合按钮编辑表单（2026-10-15，需求⑤）。
 *
 * 就地修改传入的 `combo`（编辑面板 draft 深拷贝里的那个对象）。
 *
 * 这一个表单要讲清三件事，它们都不是"看一眼就会"的：
 * <list type="number">
 *   <item><b>成员是引用</b>：改的是"用哪些已有主按钮"，不是复制一份动作 ——
 *         所以按钮后来改了名 / 改了动作，组合下次触发就是新的；</item>
 *   <item><b>延迟挂在哪</b>：<b>本步执行完之后、下一步之前</b>等多久；
 *         最后一步没有"下一步"，所以它的延迟框**根本不显示**（而不是显示一个填了也没用的框）；</item>
 *   <item><b>成员只能是主按钮</b>：不含子按钮集（那是二级结构，动作要靠集合级 IP 覆盖）。</item>
 * </list>
 */
import type { ComboButton } from '../types'

const props = withDefaults(
  defineProps<{
    combo: ComboButton
    /** 可选成员：全部主按钮（`{ id, label }`）。 */
    members: Array<{ id: string; label: string }>
  }>(),
  {},
)

/** 单段延迟上限 —— 与 Core 的 `ComboButtonStep.MaxDelayMs` **必须一致**。 */
const MAX_DELAY_MS = 60_000

/** 所有成员加起来的"等得起"估算（不含最后一步，因为它的延迟不生效）。 */
function totalDelay(): number {
  const steps = props.combo.steps ?? []
  return steps.slice(0, -1).reduce((sum, s) => sum + (s.delayMs ?? 0), 0)
}

function memberLabel(id: string): string {
  return props.members.find((m) => m.id === id)?.label ?? '（按钮已不存在）'
}

function addStep() {
  props.combo.steps ??= []

  // 默认选**第一个还没被用过的**按钮：连加几步时不必每次手动挑。
  // 全都用过了就退回第一个（用户可能就是要连按同一个按钮两次 —— 那是有意义的行为）。
  const used = new Set(props.combo.steps.map((s) => s.buttonId))
  const fresh = props.members.find((m) => !used.has(m.id)) ?? props.members[0]

  props.combo.steps.push({ buttonId: fresh?.id ?? '', delayMs: 0 })
}

function removeStep(index: number) {
  props.combo.steps.splice(index, 1)
}

/** 上移 / 下移：顺序就是执行顺序，所以这个操作有实际语义（不是"排版"）。 */
function moveStep(index: number, delta: number) {
  const list = props.combo.steps
  const target = index + delta
  if (target < 0 || target >= list.length) return

  const [item] = list.splice(index, 1)
  list.splice(target, 0, item)
}
</script>

<template>
  <div class="form">
    <!--
      ═══ 基本 ═══
      字段顺序、文案、分栏**逐字对齐主按钮**（ButtonForm 的「基本」块）：
      名称 / 字号 → 底色 / 文字颜色 / Id；宽高和主按钮一样另起「外观」块。

      ⚠️ 这里的分栏不是"随手分两行"：`.grid-2 / .grid-3 / .color-cell` 是**各表单
      scoped 各自一份**的（全局 `style.css` 里根本没有这几个类）。少补那一份样式，
      整块就会退化成竖排 —— 下面的 `<style>` 里必须留着它们。
    -->
    <section class="block">
      <h3>基本</h3>

      <div class="grid-2">
        <div>
          <label>组合名称</label>
          <input v-model="props.combo.label" placeholder="例如：开场（灯光＋投影＋音响）" />
        </div>
        <div>
          <label>名称字号（10–28）</label>
          <input v-model.number="props.combo.nameFontSize" type="number" min="10" max="28" />
        </div>
      </div>

      <div class="grid-3">
        <div>
          <label>组合底色</label>
          <div class="color-cell">
            <input v-model="props.combo.buttonColor" type="color" />
            <input v-model="props.combo.buttonColor" class="mono" />
          </div>
        </div>
        <div>
          <label>文字颜色</label>
          <div class="color-cell">
            <input v-model="props.combo.textColor" type="color" />
            <input v-model="props.combo.textColor" class="mono" />
          </div>
        </div>
        <div>
          <label>组合 Id（只读，日志与触发记录用）</label>
          <input :value="props.combo.id" class="mono" readonly />
        </div>
      </div>
    </section>

    <!-- ═══ 外观（与主按钮同名同顺序：宽 → 高） ═══ -->
    <section class="block">
      <h3>外观</h3>

      <div class="grid-2">
        <div>
          <label>宽（px，96–280）</label>
          <input v-model.number="props.combo.width" type="number" min="96" max="280" />
        </div>
        <div>
          <label>高（px，52–180）</label>
          <input v-model.number="props.combo.height" type="number" min="52" max="180" />
        </div>
      </div>
    </section>

    <!-- ═══ 对接码 ═══ -->
    <section class="block">
      <h3>对接码</h3>
      <p class="muted small" style="margin: 0 0 6px">
        收到这个码就执行<strong>整个组合</strong>（按下面的成员顺序，段间带各自延迟）。
      </p>

      <div class="grid-2">
        <div>
          <label>对接码</label>
          <input v-model="props.combo.pairingCode" placeholder="留空表示不参与监听匹配" />
        </div>
        <div>
          <label>比较方式</label>
          <select v-model="props.combo.pairingCodeType">
            <option value="String">String</option>
            <option value="UTF-8">UTF-8</option>
            <option value="Hex">Hex</option>
          </select>
        </div>
      </div>

      <!--
        对接码与主按钮**同级**匹配（按全局顺序合并扫描）—— 说清楚，
        否则"我把组合拖到前面了，码还是被后面的按钮抢走"会变成一个说不清的现场问题。
      -->
      <p class="muted small" style="margin: 6px 0 0">
        与主按钮<strong>同级</strong>参与匹配：同一个码同时配在按钮和组合上时，
        按左侧列表的先后顺序，<strong>先出现的那一个胜出</strong>。
      </p>
    </section>

    <!-- ═══ 成员（执行顺序） ═══ -->
    <section class="block">
      <h3>
        成员（按顺序执行，共 {{ (props.combo.steps ?? []).length }} 个）
        <span v-if="totalDelay() > 0" class="muted small">· 段间合计等待 {{ totalDelay() }}ms</span>
      </h3>

      <div v-if="props.members.length === 0" class="empty small">
        <strong>还没有可用的主按钮</strong>
        组合的成员只能是<strong>主按钮</strong> —— 先在左侧「+ 主按钮」建一个，再回来把它加进组合。
      </div>

      <div v-else-if="(props.combo.steps ?? []).length === 0" class="empty small">
        <strong>这个组合还没有成员</strong>
        点下面的「+ 添加成员」，把它要执行的按钮按顺序加进来。
      </div>

      <div v-for="(step, i) in props.combo.steps ?? []" :key="`step-${i}`" class="step-row">
        <span class="ord">{{ i + 1 }}</span>

        <select v-model="step.buttonId" class="grow">
          <!--
            ⚠️ 指向已删按钮的成员显示成一个**明确的提示项**，而不是静默消失：
            服务端清洗时确实会把它剔除，但用户在这一刻应当看到"这里少了一个"。
          -->
          <option v-if="!props.members.some((m) => m.id === step.buttonId)" :value="step.buttonId">
            （按钮已不存在）
          </option>
          <option v-for="m in props.members" :key="m.id" :value="m.id">{{ m.label }}</option>
        </select>

        <!--
          🔥 最后一步**没有**延迟输入框：它的延迟永远不会生效（后面没有下一步）。
          显示一个"填了也没用"的框，比不显示更糟 —— 用户会以为它生效了。
        -->
        <label v-if="i < (props.combo.steps ?? []).length - 1" class="delay">
          等待
          <input
            v-model.number="step.delayMs"
            type="number"
            min="0"
            :max="MAX_DELAY_MS"
            step="50"
            title="执行完这一个成员之后、下一个成员之前等待的毫秒数"
          />
          ms
        </label>
        <span v-else class="delay muted small" title="最后一步后面没有下一步，延迟不会生效">
          最后一步 · 无需延迟
        </span>

        <span class="ops">
          <button class="sm" type="button" title="上移" :disabled="i === 0" @click="moveStep(i, -1)">↑</button>
          <button
            class="sm"
            type="button"
            title="下移"
            :disabled="i === (props.combo.steps ?? []).length - 1"
            @click="moveStep(i, 1)"
          >
            ↓
          </button>
          <button class="sm danger" type="button" @click="removeStep(i)">删除</button>
        </span>
      </div>

      <div style="margin-top: 8px">
        <button class="sm primary" type="button" :disabled="props.members.length === 0" @click="addStep">
          + 添加成员
        </button>
      </div>

      <p class="muted small" style="margin: 8px 0 0">
        「等待」是<strong>执行完这一个成员之后、下一个成员之前</strong>的时间（上限 {{ MAX_DELAY_MS }}ms）。
        成员按钮自己动作里的「前置延迟」是另一回事，两者各算各的。
      </p>
      <p class="muted small" style="margin: 4px 0 0">
        成员显示 <strong>（按钮已不存在）</strong> 时表示那个主按钮已被删除：
        保存后服务端会把它从组合里剔除（不影响其它成员）。
      </p>
    </section>
  </div>
</template>

<style scoped>
/*
 * ⚠️ 这里原先的注释写的是「样式只用全局已有的那几个类」—— 那句话是**错的**。
 *    `.block / .grid-2 / .grid-3 / .color-cell` 在全局 `style.css` 里**一个都没有**，
 *    它们只是 ButtonForm / SubSetForm / SubButtonEditor 各自 scoped 里抄的一份。
 *    少了这几条，本表单的「基本」块会整块退化成竖排（色块撑满整行、Id 挤到下一行），
 *    和主按钮对不上。
 *
 * 所以下面这份排版**逐字照抄主按钮那一份**：列宽 `1fr 1fr 1.2fr`、色块 44px、
 * `gap: 0 12px`。数值必须一样 —— 只要有一处"看着差不多"，两边就还是不一致。
 */
.block + .block {
  margin-top: 14px;
  border-top: 1px solid var(--border);
  padding-top: 12px;
}

.block h3 {
  font-size: 13px;
  margin-bottom: 6px;
}

.grid-2 {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 0 12px;
}

.grid-3 {
  display: grid;
  grid-template-columns: 1fr 1fr 1.2fr;
  gap: 0 12px;
}

@media (max-width: 780px) {
  .grid-2,
  .grid-3 {
    grid-template-columns: 1fr;
  }
}

.color-cell {
  display: flex;
  gap: 6px;
}

.color-cell input[type='color'] {
  width: 44px;
  padding: 2px;
  flex: none;
}

.color-cell .mono {
  flex: 1;
}

/* ── 以下为本组件独有：成员行 ── */
.step-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 0;
  border-bottom: 1px dashed rgba(0, 0, 0, 0.08);
}

/*
 * 序号与「↑ ↓ 删除」，对齐左侧瓦片列表里 `.ord` / `.ops` 的观感。
 * （那两个类在 EditPanel 里也是 scoped，传不到这里，只能在此复刻 ——
 *   不复刻的话按钮会紧贴下拉框、彼此零间距。）
 */
.step-row .ord {
  flex: none;
  width: 16px;
  font-family: var(--mono);
  font-size: 11px;
  color: var(--muted);
  text-align: right;
}

.step-row .ops {
  display: flex;
  gap: 3px;
  flex: none;
}

.step-row .grow {
  flex: 1 1 auto;
  min-width: 120px;
}

.step-row .delay {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  white-space: nowrap;
}

.step-row .delay input {
  width: 84px;
}
</style>
