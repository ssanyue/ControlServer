<script setup lang="ts">
/**
 * `HH:mm` 时间点列表编辑器。
 *
 * 直接就地修改传入的数组（它来自编辑面板 draft 的深拷贝）。
 */
const props = withDefaults(
  defineProps<{
    times: string[]
    label?: string
  }>(),
  { label: '时间点' },
)

/**
 * 配置里的时刻是 **`HH:mm:ss`**（服务端规范化，见 `ControlSettingsNormalizer.NormalizeTimeString` ——
 * 定时器按 `HH:mm:ss` 做**精确字符串匹配**，写成 `08:00` 会永远匹配不上、静默不触发）。
 *
 * 但 `<input type="time">` 默认 `step=60`（分钟粒度），遇到**秒非 0** 的值（可能来自旧配置）
 * 会判定为非法 → 显示空白 → 并且把空串写回 v-model，于是那个时间点被**静默清空**。
 * 所以在边界上做一次转换：读进输入框时截成 `HH:mm`，写回时补 `:00`。
 * （秒为 0 的值本身能正常显示，这一步只是把"能显示"变成"一定安全"。）
 */
function toInput(raw: string): string {
  const m = /^(\d{1,2}):(\d{1,2})/.exec((raw ?? '').trim())
  return m ? `${m[1].padStart(2, '0')}:${m[2].padStart(2, '0')}` : ''
}

function onInput(index: number, event: Event) {
  const value = (event.target as HTMLInputElement).value
  // 清空 = 用户不要这个时间点；留成空串，服务端清洗时会剔除并给出说明。
  props.times[index] = value ? `${value}:00` : ''
}

function add() {
  props.times.push('08:00')
}

function remove(index: number) {
  props.times.splice(index, 1)
}

function sortTimes() {
  props.times.sort((a, b) => a.localeCompare(b))
}
</script>

<template>
  <div class="time-list">
    <label v-if="props.label">{{ props.label }}</label>

    <p v-if="props.times.length === 0" class="muted small" style="margin: 0">还没有时间点。</p>

    <div v-for="(t, i) in props.times" :key="i" class="time-row">
      <input :value="toInput(t)" type="time" @input="onInput(i, $event)" />
      <button class="sm danger" type="button" @click="remove(i)">删除</button>
    </div>

    <p v-if="props.times.some((t) => !toInput(t))" class="muted small" style="margin: 0 0 6px">
      有无法识别的时间点（保存时会被服务端忽略）：<span class="mono">{{
        props.times.filter((t) => !toInput(t)).join('、')
      }}</span>
    </p>

    <div class="acts">
      <button class="sm" type="button" @click="add">+ 添加时间点</button>
      <button v-if="props.times.length > 1" class="sm" type="button" @click="sortTimes">按时间排序</button>
    </div>
  </div>
</template>

<style scoped>
.time-list label {
  margin-top: 0;
}

.time-row {
  display: flex;
  gap: 6px;
  align-items: center;
  margin-bottom: 6px;
}

.time-row input {
  width: 130px;
}

.acts {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
  margin-top: 4px;
}
</style>
