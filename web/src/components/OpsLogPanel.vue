<script setup lang="ts">
import { computed, ref } from 'vue'
import type { LogDto } from '../types'
import { fmtDateTime, levelClass } from '../format'

const props = defineProps<{
  items: LogDto[]
  loading: boolean
}>()

const emit = defineEmits<{
  (e: 'refresh'): void
}>()

const onlyProblems = ref(false)

const visible = computed(() =>
  onlyProblems.value
    ? props.items.filter((x) => x.level === 'Error' || x.level === 'Fatal' || x.level === 'Warning')
    : props.items,
)

/** 从新到旧展示更符合排查习惯；服务端给的是时间正序。 */
const ordered = computed(() => [...visible.value].reverse())
</script>

<template>
  <section class="panel">
    <div class="panel-head">
      <h2>
        运维日志
        <span class="muted small">（{{ visible.length }} / {{ items.length }} 条，最新在上，服务端环形缓冲）</span>
      </h2>

      <div class="panel-actions">
        <label class="inline">
          <input v-model="onlyProblems" type="checkbox" />
          只看告警与错误
        </label>
        <button class="sm" :disabled="loading" @click="emit('refresh')">
          {{ loading ? '刷新中…' : '刷新' }}
        </button>
      </div>
    </div>

    <p class="muted small" style="margin: -4px 0 10px">
      这是<strong>服务端自身</strong>的运行日志（启动、配置、监听、错误）。报文级别的收发记录在「收发日志」标签页。
    </p>

    <div v-if="ordered.length === 0" class="empty">
      <strong>暂无日志</strong>
      服务端启动信息与配置变更会出现在这里。
    </div>

    <div v-else class="table-wrap tall">
      <table class="grid">
        <thead>
          <tr>
            <th style="width: 148px">时间</th>
            <th style="width: 62px">级别</th>
            <th style="width: 96px">分类</th>
            <th>内容</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(row, i) in ordered" :key="i">
            <td class="mono">{{ fmtDateTime(row.timestamp) }}</td>
            <td><span class="badge" :class="levelClass(row.level)">{{ row.level }}</span></td>
            <td class="muted">{{ row.category }}</td>
            <td class="wrap">{{ row.message }}</td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</template>

<style scoped>
.inline {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  margin: 0;
  font-size: 12px;
  color: var(--muted);
  white-space: nowrap;
}

.inline input {
  width: auto;
  margin: 0;
}
</style>
