<script setup lang="ts">
/**
 * 动作列表编辑器 —— 主按钮与子按钮共用。
 *
 * 直接就地增删改传入的数组（它来自编辑面板 draft 的深拷贝）。
 */
import { newId } from '../factory'
import type { ButtonAction } from '../types'
import { newAction } from '../factory'
import ActionEditor from './ActionEditor.vue'

const props = withDefaults(
  defineProps<{
    actions: ButtonAction[]
    /** 子按钮集里 WoL 会被强制降级为 UDP，故不给选。 */
    allowWol?: boolean
    /** 子按钮：目标地址由集合统一提供。 */
    sharedTarget?: boolean
    /** 宿主名称（按钮名 / 子按钮名），仅用于单条执行的展示名与日志。 */
    ownerLabel?: string
    /** 子按钮场景：所属集合的目标地址 —— 单条执行时用它覆盖动作自身的旧值。 */
    overrideIp?: string
    overridePort?: number
  }>(),
  {
    allowWol: true,
    sharedTarget: false,
    ownerLabel: '',
    overrideIp: undefined,
    overridePort: undefined,
  },
)

function add() {
  props.actions.push(newAction())
}

function move(index: number, delta: number) {
  const target = index + delta
  if (target < 0 || target >= props.actions.length) return

  const [item] = props.actions.splice(index, 1)
  props.actions.splice(target, 0, item)
}

function duplicate(index: number) {
  // 复制出来必须换新 Id：动作 Id 会进日志与触发记录，重复会有歧义。
  const copy: ButtonAction = { ...props.actions[index], id: newId() }
  props.actions.splice(index + 1, 0, copy)
}

function remove(index: number) {
  props.actions.splice(index, 1)
}
</script>

<template>
  <div class="action-list">
    <div v-if="props.actions.length === 0" class="empty small">
      <strong>还没有动作</strong>
      没有动作的按钮点击后不会发送任何数据。
    </div>

    <div v-else>
      <p class="muted small" style="margin: 0 0 6px">
        按顺序依次发送；第 1 条不等待（其延迟不生效）；某条失败不会中断后续动作。
      </p>

      <ActionEditor
        v-for="(item, i) in props.actions"
        :key="item.id || i"
        :action="item"
        :index="i"
        :allow-wol="props.allowWol"
        :shared-target="props.sharedTarget"
        :owner-label="props.ownerLabel"
        :override-ip="props.overrideIp"
        :override-port="props.overridePort"
        @move="move(i, $event)"
        @duplicate="duplicate(i)"
        @remove="remove(i)"
      />
    </div>

    <div class="row-actions">
      <button class="sm primary" type="button" @click="add">+ 添加动作</button>
    </div>
  </div>
</template>

<style scoped>
.row-actions {
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
  margin-top: 10px;
}
</style>
