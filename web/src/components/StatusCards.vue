<script setup lang="ts">
import type { ConfigCounts, StatusSnapshot } from '../types'
import { fmtDateTime, fmtUptime } from '../format'

defineProps<{
  status: StatusSnapshot | null
  counts: ConfigCounts
  configState: string
  configVersion: number
}>()
</script>

<template>
  <section class="cards">
    <div class="card">
      <div class="card-label">服务器时间</div>
      <div class="card-value">{{ fmtDateTime(status?.serverTime) || '—' }}</div>
      <div class="card-sub muted">{{ status?.timeZone ?? '北京时间 (UTC+8)' }}</div>
    </div>

    <div class="card">
      <div class="card-label">对接码监听</div>
      <div class="card-value">
        <span :class="status?.listenerRunning ? 'ok' : 'err'">
          {{ status?.listenerRunning ? '运行中' : '未运行' }}
        </span>
      </div>
      <div class="card-sub muted">UDP {{ status?.pairingPort ?? '—' }}</div>
    </div>

    <div class="card">
      <div class="card-label">按钮 / 子按钮</div>
      <div class="card-value">
        {{ counts.buttons }} <span class="muted small">/ {{ counts.subButtons }}</span>
      </div>
      <div class="card-sub muted">子按钮集 {{ counts.subButtonSets }} 个</div>
    </div>

    <div class="card">
      <div class="card-label">配置版本</div>
      <div class="card-value">
        v{{ configVersion }}
        <span v-if="configState !== 'Ok'" class="badge warn" style="margin-left: 6px">{{ configState }}</span>
      </div>
      <div class="card-sub muted">HTTP 端口 {{ status?.httpPort ?? '—' }}</div>
    </div>

    <div class="card">
      <div class="card-label">运行时长</div>
      <div class="card-value">{{ fmtUptime(status?.uptimeSeconds ?? 0) }}</div>
      <div class="card-sub muted">{{ status?.machineName ?? '—' }}</div>
    </div>
  </section>
</template>

<style scoped>
.cards {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 12px;
}

.card {
  background: var(--panel);
  border: 1px solid var(--border);
  border-radius: var(--radius);
  box-shadow: var(--shadow);
  padding: 12px 14px;
}

.card-label {
  font-size: 12px;
  color: var(--muted);
}

.card-value {
  font-size: 18px;
  font-weight: 500;
  margin: 3px 0 1px;
  word-break: break-all;
}

.card-sub {
  font-size: 12px;
}
</style>
