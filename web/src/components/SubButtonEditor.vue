<script setup lang="ts">
/**
 * 单个子按钮的编辑卡片。
 *
 * 子按钮与主按钮的差别（编辑器层面刻意体现出来）：
 * - 没有独立的 IP / 端口，统一用所属集合的目标地址；
 * - 不支持定时与月度计划；
 * - 不支持 WoL（服务端清洗时会强制降级为 UDP）。
 */
import { computed, ref } from 'vue'
import type { SubButton } from '../types'
import ActionListEditor from './ActionListEditor.vue'

const props = defineProps<{
  sub: SubButton
  index: number
  /** 所属集合的目标地址 —— 单条执行的「执行」按钮必须用它，否则会发到子按钮里那份过期的 IP。 */
  setTargetIp: string
  setTargetPort: number
}>()

const emit = defineEmits<{
  (e: 'move', delta: number): void
  (e: 'duplicate'): void
  (e: 'remove'): void
}>()

const collapsed = ref(false)

const title = computed(() => props.sub.label?.trim() || `子按钮 ${props.index + 1}`)
const actionCount = computed(() => props.sub.actions?.length ?? 0)
</script>

<template>
  <div class="sub-card">
    <div class="card-head">
      <button
        class="sm ghost"
        type="button"
        :title="collapsed ? '展开' : '收起'"
        @click="collapsed = !collapsed"
      >
        {{ collapsed ? '▸' : '▾' }}
      </button>
      <span class="idx">#{{ props.index + 1 }}</span>
      <strong class="name">{{ title }}</strong>
      <span class="badge">{{ actionCount }} 个动作</span>

      <span class="spacer" />

      <button class="sm" type="button" title="上移" @click="emit('move', -1)">↑</button>
      <button class="sm" type="button" title="下移" @click="emit('move', 1)">↓</button>
      <button class="sm" type="button" title="复制本子按钮" @click="emit('duplicate')">复制</button>
      <button class="sm danger" type="button" title="删除本子按钮" @click="emit('remove')">删除</button>
    </div>

    <div v-if="!collapsed" class="card-body">
      <div class="grid-2">
        <div>
          <label>名称</label>
          <input v-model="props.sub.label" placeholder="例如：红色" />
        </div>
        <div>
          <label>名称字号（子按钮惯例为 12）</label>
          <input v-model.number="props.sub.nameFontSize" type="number" min="10" max="28" />
        </div>
      </div>

      <div class="grid-2">
        <div>
          <label>底色</label>
          <div class="color-cell">
            <input v-model="props.sub.buttonColor" type="color" />
            <input v-model="props.sub.buttonColor" class="mono" />
          </div>
        </div>
        <div>
          <label>文字颜色</label>
          <div class="color-cell">
            <input v-model="props.sub.textColor" type="color" />
            <input v-model="props.sub.textColor" class="mono" />
          </div>
        </div>
      </div>

      <div class="grid-3">
        <div>
          <label>宽（px）</label>
          <input v-model.number="props.sub.width" type="number" min="40" max="280" />
        </div>
        <div>
          <label>高（px）</label>
          <input v-model.number="props.sub.height" type="number" min="40" max="280" />
        </div>
        <div>
          <label>对接码比较方式</label>
          <select v-model="props.sub.pairingCodeType">
            <option value="String">String</option>
            <option value="UTF-8">UTF-8</option>
            <option value="Hex">Hex</option>
          </select>
        </div>
      </div>

      <div>
        <label>对接码</label>
        <input v-model="props.sub.pairingCode" placeholder="留空表示不参与监听匹配" />
      </div>

      <section class="sub-block">
        <h4>点击发送的动作</h4>
        <ActionListEditor
          :actions="props.sub.actions ?? (props.sub.actions = [])"
          :allow-wol="false"
          :shared-target="true"
          :owner-label="props.sub.label"
          :override-ip="props.setTargetIp"
          :override-port="props.setTargetPort"
        />
      </section>
    </div>
  </div>
</template>

<style scoped>
.sub-card {
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--panel-soft);
  padding: 8px 10px;
}

.sub-card + .sub-card {
  margin-top: 8px;
}

.card-head {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.card-head .spacer {
  flex: 1 1 12px;
}

.card-head .name {
  font-size: 13px;
  font-weight: 500;
}

.card-head .idx {
  font-family: var(--mono);
  font-size: 11px;
  color: var(--muted);
}

button.ghost {
  border-color: transparent;
  background: none;
  padding: 2px 5px;
  color: var(--muted);
}

.card-body {
  margin-top: 8px;
  border-top: 1px dashed var(--border);
  padding-top: 4px;
}

.sub-block {
  margin-top: 10px;
  border-top: 1px solid var(--border);
  padding-top: 8px;
}

.sub-block h4 {
  font-size: 12px;
  margin: 0 0 4px;
  color: var(--muted);
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
</style>
