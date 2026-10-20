<script setup lang="ts">
import { computed, ref } from 'vue'
import type { TrafficLogDto } from '../types'
import { fmtDateTime } from '../format'

const props = defineProps<{
  items: TrafficLogDto[]
  loading: boolean
  capacity: number
}>()

const emit = defineEmits<{
  (e: 'refresh'): void
  (e: 'clear'): void
}>()

const filter = ref<'all' | '发送' | '接收'>('all')

const visible = computed(() =>
  filter.value === 'all' ? props.items : props.items.filter((x) => x.direction === filter.value),
)

/**
 * 从新到旧展示（2026-10-15）。
 *
 * 服务端 `GET /api/traffic/recent` 给的是**时间正序**（环形缓冲的原始顺序）。
 * 但这里是"看刚刚发生了什么"的地方 —— 顺着渲染就得每次滚到表格底部才看得见最新一条，
 * 缓冲攒到 400 条时尤其难用。翻过来，与「运维日志」页（`OpsLogPanel`）保持同一口径。
 *
 * ⚠️ 只反转**显示**，就地不动 `props.items`：`App.vue` 靠"追加到尾部"维护那份数组
 * （`[...traffic, item]`），控制台小面板也靠 `slice(-N)` 取尾部。原地 `reverse()` 会把
 * 那两处一起搞坏（小面板会开始显示最旧的几条）。
 */
const ordered = computed(() => [...visible.value].reverse())

const failCount = computed(() => props.items.filter((x) => !x.success).length)
</script>

<template>
  <section class="panel">
    <div class="panel-head">
      <h2>
        收发日志
        <span class="muted small">
          （{{ visible.length }} / {{ items.length }} 条，最新在上，服务端缓冲上限 {{ capacity }}）
        </span>
      </h2>

      <div class="panel-actions">
        <div class="seg">
          <button
            v-for="opt in (['all', '发送', '接收'] as const)"
            :key="opt"
            class="sm"
            :class="{ active: filter === opt }"
            @click="filter = opt"
          >
            {{ opt === 'all' ? '全部' : opt }}
          </button>
        </div>
        <button class="sm" :disabled="loading" @click="emit('refresh')">
          {{ loading ? '刷新中…' : '刷新' }}
        </button>
        <button class="sm danger" :disabled="items.length === 0" @click="emit('clear')">清空</button>
      </div>
    </div>

    <p v-if="failCount > 0" class="notice err" style="margin-bottom: 10px">
      有 {{ failCount }} 条发送失败 —— 失败不会中断后续动作，但需要你确认目标地址是否可达。
    </p>

    <div v-if="visible.length === 0" class="empty">
      <strong>暂无收发记录</strong>
      在「控制台」里点一个按钮执行一次，或让设备向本机对接码端口发一条报文。
    </div>

    <div v-else class="table-wrap tall">
      <table class="grid">
        <thead>
          <tr>
            <th style="width: 148px">时间</th>
            <th style="width: 52px">方向</th>
            <th style="width: 52px">协议</th>
            <th style="width: 170px">对端</th>
            <th>数据 / 说明</th>
            <th style="width: 120px">触发</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(row, i) in ordered" :key="i" :class="{ fail: !row.success }">
            <td class="mono">{{ fmtDateTime(row.time) }}</td>
            <td>
              <span class="badge" :class="row.direction === '发送' ? 'send' : 'recv'">
                {{ row.direction }}
              </span>
            </td>
            <td class="mono">{{ row.protocol }}</td>
            <td class="mono">{{ row.targetIp }}:{{ row.port }}</td>
            <td class="wrap">
              <div>{{ row.data || '(空)' }}</div>
              <div v-if="row.message" class="muted small">{{ row.message }}</div>
            </td>
            <td class="muted small">
              <div v-if="row.buttonName">{{ row.buttonName }}</div>
              <div v-if="row.actionName">{{ row.actionName }}</div>
              <div v-if="!row.buttonName && !row.actionName">—</div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</template>

<style scoped>
.seg {
  display: inline-flex;
  border: 1px solid var(--border);
  border-radius: 6px;
  overflow: hidden;
}

.seg button {
  border: none;
  border-radius: 0;
  background: var(--panel);
  color: var(--muted);
}

.seg button + button {
  border-left: 1px solid var(--border);
}

.seg button.active {
  background: var(--accent-soft);
  color: var(--accent);
  font-weight: 500;
}
</style>
