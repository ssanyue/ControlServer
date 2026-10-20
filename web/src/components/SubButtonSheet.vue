<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive } from 'vue'
import type { SubButton, SubButtonSet } from '../types'
import { clampSize, imageUrl } from '../format'
import { LICENSE_BLOCKED_HINT, useLicenseBlocked } from '../licenseGate'

const props = defineProps<{
  set: SubButtonSet
  /** 正在执行中的 Id 集合（多个子按钮可同时发送，故用集合）。 */
  busyIds: Set<string>
  justRanIds: Set<string>
}>()

const emit = defineEmits<{
  (e: 'close'): void
  (e: 'run-sub', sub: SubButton): void
}>()

const brokenImages = reactive(new Set<string>())

/** 未授权时子按钮置灰（打开集合本身是只读导航，不受影响；执行才是被拦的那一步）。 */
const blocked = useLicenseBlocked()

const diameter = (sub: SubButton) => clampSize(sub.width || sub.height, 56, 150, 90)

const subStyle = (sub: SubButton) => {
  const url = brokenImages.has(sub.id) ? null : imageUrl(sub.buttonImage)
  const d = diameter(sub)

  return {
    width: `${d}px`,
    height: `${d}px`,
    background: sub.buttonColor || '#00897B',
    color: sub.textColor || '#FFFFFF',
    backgroundImage: url ? `url("${url}")` : undefined,
    backgroundSize: 'cover',
    backgroundPosition: 'center',
    // 名称字号固定 12（与旧版一致，不支持逐个子按钮配置）。
    fontSize: '12px',
  }
}

const subLine = (sub: SubButton) => `${sub.actions?.length ?? 0} 个动作`

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape') emit('close')
}

onMounted(() => window.addEventListener('keydown', onKeydown))
onUnmounted(() => window.removeEventListener('keydown', onKeydown))

const title = computed(() => `#${props.set.label}`)
</script>

<template>
  <div class="backdrop" @click.self="emit('close')">
    <div class="sheet" role="dialog" aria-modal="true">
      <header class="sheet-head">
        <div>
          <h2>{{ title }}</h2>
          <p class="muted small" style="margin: 2px 0 0">
            统一目标 <span class="mono">{{ set.targetIp }}:{{ set.targetPort }}</span>
            · {{ set.subButtons.length }} 个子按钮
          </p>
        </div>
        <button class="sm" @click="emit('close')">关闭</button>
      </header>

      <div v-if="set.subButtons.length === 0" class="empty">这个集合里还没有子按钮。</div>

      <div v-else class="subs">
        <button
          v-for="sub in set.subButtons"
          :key="sub.id"
          class="sub"
          :class="{
            busy: props.busyIds.has(sub.id),
            ran: props.justRanIds.has(sub.id),
            blocked: blocked,
          }"
          :style="subStyle(sub)"
          :disabled="props.busyIds.has(sub.id) || blocked"
          :title="blocked ? LICENSE_BLOCKED_HINT : sub.label"
          @click="emit('run-sub', sub)"
        >
          <img
            v-if="imageUrl(sub.buttonImage)"
            class="bg-img"
            :src="imageUrl(sub.buttonImage)!"
            alt=""
            @error="brokenImages.add(sub.id)"
          />
          <span class="sub-label">{{ sub.label }}</span>
          <span class="sub-line">{{ props.busyIds.has(sub.id) ? '发送中…' : subLine(sub) }}</span>
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.backdrop {
  position: fixed;
  inset: 0;
  background: rgba(15, 23, 42, 0.42);
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
  z-index: 50;
}

.sheet {
  background: var(--panel);
  border-radius: 12px;
  box-shadow: 0 18px 48px rgba(15, 23, 42, 0.28);
  padding: 18px 20px 22px;
  width: min(720px, 100%);
  max-height: 86vh;
  overflow: auto;
}

.sheet-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 16px;
}

.sheet-head h2 {
  font-size: 16px;
}

.subs {
  display: flex;
  flex-wrap: wrap;
  gap: 14px;
}

.sub {
  position: relative;
  border-radius: 50%;
  border: 1px solid rgba(0, 0, 0, 0.12);
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 1px;
  padding: 4px;
  overflow: hidden;
  text-align: center;
  line-height: 1.25;
  box-shadow: var(--shadow);
  transition: transform 0.08s, box-shadow 0.12s;
}

.sub:hover:not(:disabled) {
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(16, 24, 40, 0.18);
  color: inherit;
  border-color: rgba(0, 0, 0, 0.24);
}

.sub:disabled {
  opacity: 1;
  cursor: progress;
}

/* 未授权置灰：与 `busy`（发送中）用不同手法，避免把"不让点"误读成"在忙"。 */
.sub.blocked {
  opacity: 0.45;
  filter: grayscale(0.7);
  cursor: not-allowed;
}

.sub.busy {
  filter: saturate(0.6);
}

.sub.ran {
  outline: 2px solid var(--accent);
  outline-offset: 3px;
}

.sub-label {
  z-index: 1;
  font-weight: 500;
  word-break: break-word;
  padding: 0 6px;
}

.sub-line {
  z-index: 1;
  font-size: 10px;
  opacity: 0.78;
}

.bg-img {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: cover;
  border-radius: 50%;
}
</style>
