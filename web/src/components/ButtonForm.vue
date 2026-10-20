<script setup lang="ts">
/**
 * 主按钮编辑表单。
 *
 * 就地修改传入的 `button`（编辑面板 draft 深拷贝里的那个对象），
 * 不重建对象，保证服务端多出来的字段不会被抹掉。
 */
import { computed, ref } from 'vue'
import type { ButtonConfig, MonthDaySchedule, MonthlyPlan } from '../types'
import { newMonthDaySchedule, newMonthlyPlan } from '../factory'
import ActionListEditor from './ActionListEditor.vue'
import TimeListEditor from './TimeListEditor.vue'

const props = withDefaults(
  defineProps<{
    button: ButtonConfig
  }>(),
  {},
)

const WEEKDAYS = [
  { value: 1, label: '一' },
  { value: 2, label: '二' },
  { value: 3, label: '三' },
  { value: 4, label: '四' },
  { value: 5, label: '五' },
  { value: 6, label: '六' },
  { value: 7, label: '日' },
]

const MONTHS = Array.from({ length: 12 }, (_, i) => i + 1)
const DAYS = Array.from({ length: 31 }, (_, i) => i + 1)

const totalDelay = computed(() =>
  (props.button.actions ?? []).reduce((sum, a) => sum + (a.delayMs || 0), 0),
)

// ═══════════════════════════════════════════════════════════════
// 「定时」「月度计划」默认收起
//
// 这两块只在少数按钮上用得上，却占了页面下半截的全部高度。默认收起后，
// 编辑一个普通按钮只需面对「基本 / 外观 / 对接码 / 动作」四块。
// 收起不等于藏起来 —— 表头带摘要（是否启用、什么模式、几个时刻），
// 不用展开也能判断需不需要点开。
//
// 折叠状态**不持久化**，由 EditPanel 的 `:key="button:id"` 在换瓦片时重置为收起。
// ═══════════════════════════════════════════════════════════════

const timerOpen = ref(false)
const plansOpen = ref(false)

/** 触发模式的显示名。值仍是 Daily / SpecificDays（配置契约不变），只改给人看的字。 */
const TIMER_MODE_LABELS: Record<string, string> = {
  Daily: '每日',
  SpecificDays: '每周',
}

const timerModeText = computed(
  () => TIMER_MODE_LABELS[props.button.timerMode] ?? props.button.timerMode,
)

const timerSummary = computed(() => {
  if (!props.button.timerEnabled) return '未启用'

  const times = (props.button.timerTimes ?? []).length
  const days =
    props.button.timerMode === 'SpecificDays'
      ? ` · ${(props.button.timerDays ?? []).length} 个星期`
      : ''

  return `已启用 · ${timerModeText.value}${days} · ${times} 个时刻`
})

function toggleWeekday(day: number) {
  const days = props.button.timerDays ?? (props.button.timerDays = [])
  const at = days.indexOf(day)
  if (at >= 0) days.splice(at, 1)
  else days.push(day)
  days.sort((a, b) => a - b)
}

function addPlan() {
  props.button.monthlyPlans ??= []
  props.button.monthlyPlans.push(newMonthlyPlan())
}

function removePlan(index: number) {
  props.button.monthlyPlans.splice(index, 1)
}

function addSchedule(plan: MonthlyPlan) {
  plan.monthDays ??= []
  // 默认接在一个还没被占用的月份上，省得每次都要改。
  const used = new Set(plan.monthDays.map((s) => s.month))
  const free = MONTHS.find((m) => !used.has(m)) ?? 1
  plan.monthDays.push({ ...newMonthDaySchedule(), month: free })
}

function removeSchedule(plan: MonthlyPlan, index: number) {
  plan.monthDays.splice(index, 1)
}

function toggleDay(schedule: MonthDaySchedule, day: number) {
  const days = schedule.days ?? (schedule.days = [])
  const at = days.indexOf(day)
  if (at >= 0) days.splice(at, 1)
  else days.push(day)
  days.sort((a, b) => a - b)
}
</script>

<template>
  <div class="form">
    <!-- ═══ 基本 ═══ -->
    <section class="block">
      <h3>基本</h3>

      <div class="grid-2">
        <div>
          <label>按钮名称</label>
          <input v-model="props.button.label" placeholder="例如：开灯" />
        </div>
        <div>
          <label>名称字号（10–28）</label>
          <input v-model.number="props.button.nameFontSize" type="number" min="10" max="28" />
        </div>
      </div>

      <div class="grid-3">
        <div>
          <label>按钮底色</label>
          <div class="color-cell">
            <input v-model="props.button.buttonColor" type="color" />
            <input v-model="props.button.buttonColor" class="mono" />
          </div>
        </div>
        <div>
          <label>文字颜色</label>
          <div class="color-cell">
            <input v-model="props.button.textColor" type="color" />
            <input v-model="props.button.textColor" class="mono" />
          </div>
        </div>
        <div>
          <label>按钮 Id（只读，日志与触发记录用）</label>
          <input :value="props.button.id" class="mono" readonly />
        </div>
      </div>
    </section>

    <!-- ═══ 外观 ═══ -->
    <section class="block">
      <h3>外观</h3>

      <div class="grid-2">
        <div>
          <label>宽（px，96–280）</label>
          <input v-model.number="props.button.width" type="number" min="96" max="280" />
        </div>
        <div>
          <label>高（px，52–180）</label>
          <input v-model.number="props.button.height" type="number" min="52" max="180" />
        </div>
      </div>
    </section>

    <!-- ═══ 对接码 ═══ -->
    <section class="block">
      <h3>对接码</h3>
      <p class="muted small" style="margin: 0 0 6px">
        监听端口收到数据时，用对接码比对决定触发哪个按钮。
      </p>

      <div class="grid-2">
        <div>
          <label>对接码</label>
          <input v-model="props.button.pairingCode" placeholder="留空表示不参与监听匹配" />
        </div>
        <div>
          <label>比较方式</label>
          <select v-model="props.button.pairingCodeType">
            <option value="String">String</option>
            <option value="UTF-8">UTF-8</option>
            <option value="Hex">Hex</option>
          </select>
        </div>
      </div>
    </section>

    <!-- ═══ 动作 ═══ -->
    <section class="block">
      <h3>
        点击发送的动作
        <span class="muted small">
          （{{ props.button.actions?.length ?? 0 }} 条<template v-if="totalDelay > 0">，共延迟 {{ totalDelay }}ms</template>）
        </span>
      </h3>

      <!-- owner-label 只用于「执行」单条动作时的展示名与收发日志；主按钮没有共享目标，
           每条动作用自己的 IP/端口，所以不传 override。 -->
      <ActionListEditor
        :actions="props.button.actions ?? (props.button.actions = [])"
        :owner-label="props.button.label"
      />
    </section>

    <!-- ═══ 定时（默认收起） ═══ -->
    <section class="block">
      <h3 class="sect-head" @click="timerOpen = !timerOpen">
        <span class="caret">{{ timerOpen ? '▾' : '▸' }}</span>
        定时
        <span class="muted small">（{{ timerSummary }}）</span>
      </h3>

      <template v-if="timerOpen">
        <label class="inline">
          <input v-model="props.button.timerEnabled" type="checkbox" />
          <span>启用定时自动触发</span>
        </label>

        <template v-if="props.button.timerEnabled">
          <div class="grid-2">
            <div>
              <label>执行模式</label>
              <select v-model="props.button.timerMode">
                <option value="Daily">每日</option>
                <option value="SpecificDays">每周</option>
              </select>
            </div>
          </div>

          <div v-if="props.button.timerMode === 'SpecificDays'" class="weekdays">
            <label>在这些星期触发</label>
            <div class="chips">
              <button
                v-for="w in WEEKDAYS"
                :key="w.value"
                class="chip"
                :class="{ on: (props.button.timerDays ?? []).includes(w.value) }"
                type="button"
                @click="toggleWeekday(w.value)"
              >
                {{ w.label }}
              </button>
            </div>
          </div>

          <TimeListEditor
            :times="props.button.timerTimes ?? (props.button.timerTimes = [])"
            label="触发时刻（可多个）"
          />
        </template>
      </template>
    </section>

    <!-- ═══ 月度计划（默认收起） ═══ -->
    <section class="block">
      <h3 class="sect-head" @click="plansOpen = !plansOpen">
        <span class="caret">{{ plansOpen ? '▾' : '▸' }}</span>
        月度计划
        <span class="muted small">（{{ props.button.monthlyPlans?.length ?? 0 }} 个）</span>
      </h3>

      <template v-if="plansOpen">
        <p class="muted small" style="margin: 0 0 8px">
          用于"只在某几个月的某几天触发"的场合，与上面的定时相互独立。
        </p>

        <div v-if="(props.button.monthlyPlans ?? []).length === 0" class="muted small">
          还没有月度计划。
        </div>

        <div v-for="(plan, pi) in props.button.monthlyPlans ?? []" :key="plan.id || pi" class="plan">
          <div class="plan-head">
            <label class="inline">
              <input v-model="plan.enabled" type="checkbox" />
              <span class="muted small">启用</span>
            </label>

            <input v-model="plan.name" placeholder="计划名称" style="max-width: 220px" />

            <span class="spacer" />
            <button class="sm danger" type="button" @click="removePlan(pi)">删除计划</button>
          </div>

          <TimeListEditor :times="plan.times ?? (plan.times = [])" label="触发时刻" />

          <label>生效排期（月份 + 该月的日期）</label>

          <div
            v-for="(sch, si) in plan.monthDays ?? []"
            :key="si"
            class="schedule"
          >
            <div class="sch-head">
              <select v-model.number="sch.month" style="max-width: 110px">
                <option v-for="m in MONTHS" :key="m" :value="m">{{ m }} 月</option>
              </select>
              <span class="muted small">已选 {{ (sch.days ?? []).length }} 天</span>
              <span class="spacer" />
              <button class="sm danger" type="button" @click="removeSchedule(plan, si)">删除</button>
            </div>

            <div class="chips days">
              <button
                v-for="d in DAYS"
                :key="d"
                class="chip"
                :class="{ on: (sch.days ?? []).includes(d) }"
                type="button"
                @click="toggleDay(sch, d)"
              >
                {{ d }}
              </button>
            </div>
          </div>

          <button class="sm" type="button" style="margin-top: 6px" @click="addSchedule(plan)">
            + 添加一个月份
          </button>
        </div>

        <div class="row-actions">
          <button class="sm primary" type="button" @click="addPlan">+ 添加月度计划</button>
        </div>
      </template>
    </section>
  </div>
</template>

<style scoped>
.block + .block {
  margin-top: 14px;
  border-top: 1px solid var(--border);
  padding-top: 12px;
}

.block h3 {
  font-size: 13px;
  margin-bottom: 6px;
}

/* 「定时 / 月度计划」的可折叠表头：整行可点，右侧摘要让人不展开也能判断要不要点开。 */
.block h3.sect-head {
  display: flex;
  align-items: center;
  gap: 4px;
  cursor: pointer;
  user-select: none;
}

.block h3.sect-head:hover {
  color: var(--accent);
}

.block h3.sect-head .caret {
  width: 12px;
  font-size: 11px;
  color: var(--muted);
  text-align: center;
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

label.inline {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 6px 0;
}

label.inline input {
  width: auto;
}

.chips {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.chip {
  padding: 2px 9px;
  font-size: 12px;
  border-radius: 5px;
}

.chip.on {
  background: var(--accent);
  border-color: var(--accent);
  color: #fff;
}

.weekdays {
  margin-top: 4px;
}

.plan {
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--panel-soft);
  padding: 8px 10px;
  margin-bottom: 8px;
}

.plan-head {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.plan-head .spacer {
  flex: 1 1 10px;
}

.schedule {
  border-top: 1px dashed var(--border);
  margin-top: 6px;
  padding-top: 6px;
}

.sch-head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 5px;
}

.sch-head .spacer {
  flex: 1 1 10px;
}

.days .chip {
  min-width: 28px;
  padding: 1px 4px;
}

.row-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  margin-top: 8px;
}
</style>
