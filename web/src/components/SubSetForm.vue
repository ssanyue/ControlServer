<script setup lang="ts">
/**
 * 子按钮集编辑表单。
 *
 * 就地修改传入的 `set`（编辑面板 draft 深拷贝里的那个对象）。
 * 子按钮共享所属集合的 IP / 端口 —— 这是子按钮集的核心语义，界面上要显眼。
 */
import type { SubButton, SubButtonSet } from '../types'
import { newId, newSubButton } from '../factory'
import SubButtonEditor from './SubButtonEditor.vue'

const props = withDefaults(
  defineProps<{
    set: SubButtonSet
  }>(),
  {},
)

function addSub() {
  props.set.subButtons ??= []
  props.set.subButtons.push(newSubButton())
}

function moveSub(index: number, delta: number) {
  const list = props.set.subButtons
  const target = index + delta
  if (target < 0 || target >= list.length) return

  const [item] = list.splice(index, 1)
  list.splice(target, 0, item)
}

function duplicateSub(index: number) {
  const source = props.set.subButtons[index]
  const copy: SubButton = {
    ...source,
    id: newId(),
    actions: (source.actions ?? []).map((a) => ({ ...a, id: newId() })),
  }
  props.set.subButtons.splice(index + 1, 0, copy)
}

function removeSub(index: number) {
  props.set.subButtons.splice(index, 1)
}
</script>

<template>
  <div class="form">
    <!-- ═══ 父集基本 ═══ -->
    <section class="block">
      <h3>子按钮集</h3>

      <div class="grid-2">
        <div>
          <label>名称</label>
          <input v-model="props.set.label" placeholder="例如：颜色选择" />
        </div>
        <div>
          <label>名称字号（10–28）</label>
          <input v-model.number="props.set.nameFontSize" type="number" min="10" max="28" />
        </div>
      </div>

      <div class="grid-3">
        <div>
          <label>底色</label>
          <div class="color-cell">
            <input v-model="props.set.buttonColor" type="color" />
            <input v-model="props.set.buttonColor" class="mono" />
          </div>
        </div>
        <div>
          <label>文字颜色</label>
          <div class="color-cell">
            <input v-model="props.set.textColor" type="color" />
            <input v-model="props.set.textColor" class="mono" />
          </div>
        </div>
        <div>
          <label>集合 Id（只读）</label>
          <input :value="props.set.id" class="mono" readonly />
        </div>
      </div>

      <div class="grid-2">
        <div>
          <label>宽（px，96–280）</label>
          <input v-model.number="props.set.width" type="number" min="96" max="280" />
        </div>
        <div>
          <label>高（px，52–180）</label>
          <input v-model.number="props.set.height" type="number" min="52" max="180" />
        </div>
      </div>
    </section>

    <!-- ═══ 共享目标 ═══ -->
    <section class="block">
      <h3>共享目标地址</h3>
      <p class="muted small" style="margin: 0 0 6px">
        集合内<strong>所有子按钮</strong>都用这个 IP 与端口发送，子按钮自己不再单独设置。
      </p>

      <div class="grid-2">
        <div>
          <label>目标 IP</label>
          <!--
            ⚠️ 这个 id 是**子按钮动作卡片里「去设置集合地址」按钮的目标**
               （ActionEditor.gotoSetAddress 按 id 找它并滚过去 + 聚焦）。
               改名字要两边一起改，否则按钮会静默失效 —— 点了没反应，也不报错。
          -->
          <input id="sub-set-target-ip" v-model="props.set.targetIp" placeholder="192.168.1.100" />
        </div>
        <div>
          <label>目标端口</label>
          <input v-model.number="props.set.targetPort" type="number" min="1" max="65535" />
        </div>
      </div>
    </section>

    <!-- ═══ 子按钮 ═══ -->
    <section class="block">
      <h3>
        子按钮
        <span class="muted small">（{{ props.set.subButtons?.length ?? 0 }} 个）</span>
      </h3>

      <div v-if="(props.set.subButtons ?? []).length === 0" class="empty small">
        <strong>还没有子按钮</strong>
        空集合在主界面上点开后将什么也不显示。
      </div>

      <SubButtonEditor
        v-for="(sub, i) in props.set.subButtons ?? []"
        :key="sub.id || i"
        :sub="sub"
        :index="i"
        :set-target-ip="props.set.targetIp"
        :set-target-port="props.set.targetPort"
        @move="moveSub(i, $event)"
        @duplicate="duplicateSub(i)"
        @remove="removeSub(i)"
      />

      <div class="row-actions">
        <button class="sm primary" type="button" @click="addSub">+ 添加子按钮</button>
      </div>
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

.row-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  margin-top: 10px;
}
</style>
