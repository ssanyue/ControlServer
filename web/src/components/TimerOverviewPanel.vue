<script setup lang="ts">
/**
 * 定时总览页（M2-G6）。
 *
 * 数据全部来自 `GET /api/timers` —— 服务端把配置里的任务定时与月度计划**推导**成
 * 一张"当前生效的触发点"清单，并附上"今天跑了没有"（查 `timer_fires` 表）。
 * 所以这一页**不需要读配置**：`enabled=false` 的按钮、月度计划里没勾的日期，
 * 在服务端就已经被过滤掉了，前端不作二次判断（否则两处规则会漂移）。
 *
 * 手动触发同样走 `timer_fires` 的幂等闸门，**不是**一条能绕过约束的后门。
 */
import { computed, onMounted, ref } from 'vue'
import { ApiError, api } from '../api'
import { fmtDateTime, fmtTimeOnly } from '../format'
import type { ActionExecutionDto, CurrentUser, TimerFireResultDto, TimerOverviewDto, TimerTriggerDto } from '../types'
import ActionResultPanel from './ActionResultPanel.vue'
import { LICENSE_BLOCKED_HINT, useLicenseBlocked } from '../licenseGate'

const props = defineProps<{ user: CurrentUser | null }>()

const emit = defineEmits<{
  (e: 'notice', kind: 'ok' | 'err' | 'warn', text: string): void
}>()

const canOperate = computed(() => props.user?.role === 'Admin' || props.user?.role === 'Operator')

/**
 * 未授权时「立即触发」置灰。
 *
 * 这道闸门与定时**后台**停摆是同一件事的两面：手动触发走的也是
 * `/api/timers/{refId}/fire`，同样被服务端以 403 拒绝。
 */
const blocked = useLicenseBlocked()

const firingDisabled = computed(() => !canOperate.value || blocked.value)

const firingTitle = computed(() => {
  if (blocked.value) return LICENSE_BLOCKED_HINT
  return canOperate.value ? '立刻执行一次这个触发点的动作' : '当前角色只能查看'
})

const overview = ref<TimerOverviewDto | null>(null)
const loading = ref(false)
const loadError = ref('')
const firingRef = ref('')

const lastResult = ref<ActionExecutionDto | null>(null)
const lastError = ref<string | null>(null)

/**
 * refId → 本次会话里最后一次手动触发的时刻。
 *
 * 为什么前端要自己记：服务端把手动触发的记录写在**点击那一刻**（`FireNowAsync` 用
 * `now.ToString("HH:mm:ss")`，见 `TimerSchedulerService`），而 `firedToday` 查的是
 * **触发点自己定义的时刻** —— 两者天然不相等，所以手动触发后 `firedToday` 仍是 false。
 * 这是有意设计（否则手动调试一次就会占掉当天的正式定时），但看起来像"点了没反应"，
 * 因此在这里如实留痕。
 */
const manualFired = ref<Record<string, string>>({})

const WEEKDAYS = ['周日', '周一', '周二', '周三', '周四', '周五', '周六']

const triggers = computed(() => overview.value?.triggers ?? [])

/** 今天适用的触发点里，还有多少没跑 —— 概况里最有用的一格。 */
const pendingToday = computed(
  () => triggers.value.filter((t) => t.appliesToday && !t.firedToday && !t.elapsed).length,
)

async function load() {
  loading.value = true
  loadError.value = ''

  try {
    overview.value = await api.timers()
  } catch (e) {
    loadError.value = `读取定时总览失败：${e instanceof Error ? e.message : String(e)}`
  } finally {
    loading.value = false
  }
}

/**
 * 触发类型的显示名。
 *
 * 与编辑页的下拉框保持一致：`SpecificDays` 统一叫「每周」、`Daily` 叫「每日」，
 * 不再出现「指定星期」这种叫法 —— 同一个概念在两个页面上叫两个名字，
 * 用户会以为它们不是一回事。底层取值仍是 `Daily` / `SpecificDays`（配置契约不变）。
 */
function kindText(kind: string): string {
  switch (kind) {
    case 'Daily':
      return '每日'
    case 'SpecificDays':
      return '每周'
    case 'MonthlyPlan':
      return '月度计划'
    default:
      return kind
  }
}

/** 把触发条件翻译成一句人话；无条件时返回空串。 */
function conditionText(t: TimerTriggerDto): string {
  if (t.kind === 'SpecificDays') {
    if (!t.days || t.days.length === 0) return '未选星期 → 不会触发'
    return t.days.map((d) => WEEKDAYS[d] ?? `第${d}天`).join('、')
  }

  if (t.kind === 'MonthlyPlan') {
    return t.monthDays && t.monthDays.length > 0 ? t.monthDays.join('、') : '未选日期 → 不会触发'
  }

  return '每日'
}

/**
 * 今天这一格的状态。四种互斥情况按优先级排列：
 * 已触发 > 今天不适用 > 已过点未触发 > 待触发。
 */
function todayState(t: TimerTriggerDto): { text: string; cls: string } {
  if (t.firedToday) return { text: '已触发', cls: 'ok' }
  if (!t.appliesToday) return { text: '今天不适用', cls: '' }
  if (t.elapsed) return { text: '已过点未触发', cls: 'warn' }
  return { text: '待触发', cls: 'send' }
}

async function fire(t: TimerTriggerDto) {
  firingRef.value = t.refId
  lastError.value = null

  try {
    const res = await api.fireTimer(t.refId)
    lastResult.value = res.execution
    if (res.ok && res.time) {
      manualFired.value = { ...manualFired.value, [t.refId]: res.time }
    }
    emit('notice', res.ok ? 'ok' : 'err', res.message)
    await load()
  } catch (e) {
    // 409 = 被幂等闸门挡下（同一秒内已经触发过）。服务端把完整结果放在响应体里。
    if (e instanceof ApiError && e.status === 409) {
      const body = e.body as TimerFireResultDto | null
      lastResult.value = null
      lastError.value = body?.message ?? '这一时刻已经触发过了（同秒内只生效一次）。'
      emit('notice', 'warn', lastError.value!)
      await load()
      return
    }

    lastError.value = `触发失败：${e instanceof Error ? e.message : String(e)}`
    lastResult.value = null
  } finally {
    firingRef.value = ''
  }
}

onMounted(load)

function clearResult() {
  lastResult.value = null
  lastError.value = null
}
</script>

<template>
  <div>
    <!-- ═══ 调度器概况 ═══ -->
    <section class="panel">
      <div class="panel-head">
        <h2>定时调度</h2>
        <div class="panel-actions">
          <button class="sm" :disabled="loading" @click="load">
            {{ loading ? '读取中…' : '刷新' }}
          </button>
        </div>
      </div>

      <p v-if="loadError" class="notice err">{{ loadError }}</p>

      <p v-if="overview && !overview.enabled" class="notice warn" style="margin-bottom: 10px">
        <strong>调度器已停用</strong>
        <span>配置项 <code>Server:Timer:Enabled</code> 为 false，所有定时都不会执行。</span>
      </p>

      <div v-if="overview" class="stats">
        <div>
          <span class="muted">调度状态</span>
          <span class="badge" :class="overview.enabled ? 'ok' : 'err'">
            {{ overview.enabled ? '启用' : '停用' }}
          </span>
        </div>
        <div>
          <span class="muted">触发点</span><strong>{{ overview.triggers.length }}</strong>
        </div>
        <div>
          <span class="muted">今天待触发</span><strong>{{ pendingToday }}</strong>
        </div>
        <div>
          <span class="muted">本次进程已触发</span><strong>{{ overview.firedCount }}</strong>
        </div>
        <div>
          <span class="muted">累计漏触发</span>
          <strong :class="{ warn: overview.missedCount > 0 }">{{ overview.missedCount }}</strong>
        </div>
        <div>
          <span class="muted">服务端时间</span>
          <strong class="mono">{{ fmtDateTime(overview.serverTime) }}</strong>
        </div>
        <div>
          <span class="muted">下次触发</span>
          <strong class="mono">{{ overview.nextFireAt ? fmtDateTime(overview.nextFireAt) : '—' }}</strong>
        </div>
      </div>

      <p v-if="overview" class="muted small" style="margin-bottom: 0">
        节拍 {{ overview.tickIntervalSeconds }} 秒 ·
        进程内追赶窗口 {{ overview.catchUpWindowSeconds }} 秒 ·
        启动漏扫窗口 {{ overview.missedScanWindowSeconds }} 秒 ·
        触发记录保留 {{ overview.retentionDays }} 天
        <template v-if="overview.tickIntervalSeconds > 1">
          <br />
          <strong class="warn">节拍大于 1 秒会漏触发</strong>
          —— 调度器按"整秒时刻"匹配，跳过的那一秒不会再补。
        </template>
      </p>
    </section>

    <!-- ═══ 触发点清单 ═══ -->
    <section class="panel">
      <div class="panel-head">
        <h2>
          生效的触发点
          <span class="muted small">（按时刻排序，时间均为北京时间）</span>
        </h2>
      </div>

      <p v-if="loading && !overview" class="muted small">正在读取…</p>

      <div v-else-if="triggers.length === 0" class="empty">
        <strong>还没有任何定时触发点</strong>
        <span>
          到「编辑」页给某个按钮打开「定时器」（每日 / 每周），或添加一条「月度计划」，
          保存后这里就会出现。
        </span>
      </div>

      <div v-else class="table-wrap">
        <table class="grid">
          <thead>
            <tr>
              <th>时刻</th>
              <th>类型</th>
              <th>所属按钮</th>
              <th>触发条件</th>
              <th>今天</th>
              <th>操作</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="t in triggers" :key="t.refId">
              <td class="mono"><strong>{{ fmtTimeOnly(t.time) }}</strong></td>
              <td><span class="badge">{{ kindText(t.kind) }}</span></td>
              <td>{{ t.buttonLabel || '（未命名按钮）' }}</td>
              <td>{{ conditionText(t) }}</td>
              <td>
                <span class="badge" :class="todayState(t).cls">{{ todayState(t).text }}</span>
                <span v-if="t.firedAt" class="muted small mono" style="margin-left: 6px">
                  {{ fmtTimeOnly(t.firedAt) }}
                </span>
                <template v-if="manualFired[t.refId]">
                  <br />
                  <span class="muted small mono">手动触发过 {{ manualFired[t.refId] }}</span>
                </template>
              </td>
              <td>
                <button
                  class="sm"
                  :disabled="firingDisabled || firingRef !== ''"
                  :title="firingTitle"
                  @click="fire(t)"
                >
                  {{ firingRef === t.refId ? '触发中…' : '立即触发' }}
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <p class="muted small" style="margin-bottom: 0">
        「立即触发」用于调试：它与定时触发<strong>共用同一张触发记录表</strong>与
        <strong>(触发点, 日期, 时刻)</strong> 唯一约束，因此<strong>同一秒内连点只有第一次生效</strong>
        （第二次返回 409）。这不是限制疏漏，而是有意为之 —— 能绕过幂等键的调试通道就是生产事故的入口。
        <br />
        另外留意：手动触发的记录写在<strong>你点的那一刻</strong>（如 21:47:12），而不是触发点自己的时刻，
        所以它<strong>不会</strong>把「今天」标成「已触发」。这也是有意的 ——
        否则手动调试一次就会占掉那个触发点<strong>当天的正式定时</strong>，到点反而不执行。
        刚触发过的行会在「今天」列留下时刻作为痕迹。
      </p>
    </section>

    <!-- ═══ 手动触发结果 ═══ -->
    <ActionResultPanel
      v-if="lastResult || lastError"
      :result="lastResult"
      :error="lastError"
      @close="clearResult"
    />
  </div>
</template>

<style scoped>
.stats {
  display: flex;
  flex-wrap: wrap;
  gap: 10px 26px;
  margin-bottom: 12px;
}

.stats > div {
  display: flex;
  flex-direction: column;
  font-size: 13px;
  min-width: 84px;
}

.stats strong {
  font-size: 15px;
  font-weight: 500;
}

.stats strong.warn {
  color: var(--warn);
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
