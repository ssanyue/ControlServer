<script setup lang="ts">
import type { ActionExecutionDto } from '../types'
import { fmtDateTime } from '../format'

const props = withDefaults(
  defineProps<{
    result: ActionExecutionDto | null
    error: string | null
    /**
     * 还有多少个任务正在执行中。
     *
     * 本面板展示的是**最近完成**的那一次 —— 按钮可以并发执行之后，
     * "看到结果"不再等于"全都跑完了"。不提示的话，用户会以为另外那几次被吞了。
     */
    inflight?: number
  }>(),
  { inflight: 0 },
)

const emit = defineEmits<{
  (e: 'close'): void
}>()
</script>

<template>
  <section v-if="error" class="panel">
    <div class="panel-head">
      <h2>执行结果</h2>
      <button class="sm" @click="emit('close')">关闭</button>
    </div>
    <p class="notice err">
      <strong>请求失败</strong>
      <span>{{ error }}</span>
    </p>
  </section>

  <!-- 还没有结果、但确实在执行（长延迟按钮点下去的头几秒就是这一屏） -->
  <section v-else-if="!result && props.inflight > 0" class="panel">
    <div class="panel-head">
      <h2>执行结果</h2>
    </div>
    <p class="notice info">
      <strong>执行中…</strong>
      <span>{{ props.inflight }} 个按钮正在发送，完成后这里会显示逐条明细。</span>
    </p>
  </section>

  <section v-else-if="result" class="panel">
    <div class="panel-head">
      <h2>
        执行结果
        <span class="badge" :class="result.ok ? 'ok' : 'err'">{{ result.ok ? '全部成功' : '有失败' }}</span>
      </h2>
      <button class="sm" @click="emit('close')">关闭</button>
    </div>

    <p v-if="props.inflight > 0" class="notice info small">
      <strong>并发执行中</strong>
      <span>
        下表是<strong>最近完成</strong>的那一次；还有 {{ props.inflight }} 个按钮正在发送，稍后刷新即为它们的结果。
      </span>
    </p>

    <p class="notice" :class="result.ok ? 'ok' : 'warn'">
      <strong>{{ result.buttonName }}</strong>
      <span>
        {{ result.successCount }} 成功 / {{ result.failureCount }} 失败 · 耗时 {{ result.elapsedMs }}ms ·
        {{ fmtDateTime(result.startedAt) }}
      </span>
      <span class="mono small muted">{{ result.runId }}</span>
    </p>

    <div class="table-wrap" style="margin-top: 10px; max-height: 320px">
      <table class="grid">
        <thead>
          <tr>
            <th style="width: 34px">#</th>
            <th style="width: 54px">结果</th>
            <th style="width: 120px">动作</th>
            <th style="width: 56px">协议</th>
            <th style="width: 168px">目标</th>
            <th style="width: 76px">字节</th>
            <th>发送内容 / 说明</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="step in result.steps" :key="step.index" :class="{ fail: !step.ok }">
            <td class="muted">{{ step.index }}</td>
            <td>
              <span class="badge" :class="step.ok ? 'ok' : 'err'">{{ step.ok ? '成功' : '失败' }}</span>
            </td>
            <td>
              {{ step.actionName }}
              <div v-if="step.resolvedFromCommand" class="muted small">指令库：{{ step.resolvedFromCommand }}</div>
            </td>
            <td class="mono">{{ step.protocol }}</td>
            <td class="mono">{{ step.targetIp }}:{{ step.targetPort }}</td>
            <td class="mono">{{ step.bytesSent }}</td>
            <td class="wrap">
              <div v-if="step.dataPreview">{{ step.dataPreview }}</div>
              <div v-if="step.message" class="muted small">{{ step.message }}</div>
            </td>
          </tr>
          <tr v-if="result.steps.length === 0">
            <td colspan="7" class="muted center">这个按钮没有配置任何动作。</td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</template>
