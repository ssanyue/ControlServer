<script setup lang="ts">
/**
 * 可视化配置编辑器。
 *
 * 工作方式：把服务端配置**深拷贝**一份作为 `draft`，所有编辑都作用在 draft 上，
 * 点「保存」时整体 `PUT /api/config`。之所以不做逐字段的即时保存：
 * 动作列表的增删改互相牵连（顺序、Id），整体提交才能保证服务端看到的一定是
 * 一个自洽的配置；单一管理员场景下这点并发代价可以接受。
 *
 * 两个必须守住的细节：
 * 1. **不重建对象** —— 子表单就地改 draft 的嵌套字段，服务端多出来的字段（比如
 *    旧的 `timerTime`）才不会被抹掉；
 * 2. **脏检测用序列化对比** —— 改回原样会重新变干净，比"动过就永远是脏的"准确。
 */
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { ApiError, api } from '../api'
import type { ConfigConflictBody } from '../api'
import type { ConfigView, ControlSettings, CurrentUser, Tile } from '../types'
import { mergeTiles } from '../types'
import { newButton, newComboButton, newId, newSubButtonSet } from '../factory'
import ButtonForm from './ButtonForm.vue'
import ComboForm from './ComboForm.vue'
import SubSetForm from './SubSetForm.vue'

const props = defineProps<{
  config: ConfigView | null
  user: CurrentUser | null
}>()

const emit = defineEmits<{
  (e: 'refresh-config'): void
  (e: 'notice', kind: 'ok' | 'err' | 'warn', text: string): void
}>()

type Selection = { kind: 'button' | 'combo' | 'set'; id: string }

/**
 * 能否编辑配置 —— 与后端 `Policies.CanOperate` 对齐：Admin / Operator 可写，Viewer 只读。
 *
 * 前端只做"提前禁用 + 说明"，真正的授权判定始终在服务端（越权请求会被 403）。
 * 之所以还要在这里拦一道：让查看者点进编辑页时**一眼看出是权限问题**，
 * 而不是改了半天、点保存才发现存不下去。
 */
const canOperate = computed(
  () => props.user?.role === 'Admin' || props.user?.role === 'Operator',
)

const draft = ref<ControlSettings | null>(null)
/** 上次与服务端同步时的序列化快照，用来判断"有没有未保存改动"。 */
const baseline = ref('')
const dirty = ref(false)
const selected = ref<Selection | null>(null)

const saving = ref(false)
const saveError = ref('')
const conflict = ref<ConfigConflictBody | null>(null)
const remoteDirty = ref(false)
const pendingDelete = ref<string | null>(null)

let pendingDeleteTimer: number | null = null

function serialize(value: unknown): string {
  return JSON.stringify(value)
}

function deepCopy<T>(value: T): T {
  return JSON.parse(serialize(value)) as T
}

const tiles = computed(() => mergeTiles(draft.value))

const selectedButton = computed(() => {
  const d = draft.value
  const sel = selected.value
  if (!d || sel?.kind !== 'button') return null
  return d.buttons.find((b) => b.id === sel.id) ?? null
})

const selectedCombo = computed(() => {
  const d = draft.value
  const sel = selected.value
  if (!d || sel?.kind !== 'combo') return null
  return (d.comboButtons ?? []).find((c) => c.id === sel.id) ?? null
})

const selectedSet = computed(() => {
  const d = draft.value
  const sel = selected.value
  if (!d || sel?.kind !== 'set') return null
  return d.subButtonSets.find((s) => s.id === sel.id) ?? null
})

/**
 * 组合按钮的成员候选：**只有主按钮**（用户明确要求，不含子按钮集）。
 * <p>子按钮集是"容器 + 子按钮"的二级结构，它的动作要靠集合级的 IP / 端口覆盖才有意义 ——
 * 塞进组合会让"到底按哪个 IP 发"变成要看两层配置才能回答的问题。</p>
 */
const comboMemberOptions = computed(() =>
  (draft.value?.buttons ?? []).map((b) => ({ id: b.id, label: b.label || '（未命名）' })),
)

// ═══════════════════════════════════════════════════════════════
// 脏检测
// ═══════════════════════════════════════════════════════════════

watch(
  draft,
  () => {
    dirty.value = serialize(draft.value) !== baseline.value
  },
  { deep: true, flush: 'post' },
)

// ═══════════════════════════════════════════════════════════════
// 与服务端同步
// ═══════════════════════════════════════════════════════════════

function existsIn(settings: ControlSettings, sel: Selection): boolean {
  if (sel.kind === 'button') return settings.buttons.some((b) => b.id === sel.id)
  if (sel.kind === 'combo') return (settings.comboButtons ?? []).some((c) => c.id === sel.id)

  return settings.subButtonSets.some((s) => s.id === sel.id)
}

function resetDraft(settings: ControlSettings, keepSelection = false) {
  const copy = deepCopy(settings)
  const keep = keepSelection ? selected.value : null

  draft.value = copy
  baseline.value = serialize(copy)
  dirty.value = false
  remoteDirty.value = false
  conflict.value = null
  saveError.value = ''
  selected.value = keep && existsIn(copy, keep) ? keep : null
}

watch(
  () => props.config,
  (next) => {
    if (!next) return

    // 正在编辑时**绝不**覆盖用户的改动 —— 只提示服务端已变，由用户决定怎么办。
    if (dirty.value) {
      remoteDirty.value = true
      return
    }

    resetDraft(next.settings, true)
  },
  { immediate: true },
)

// ═══════════════════════════════════════════════════════════════
// 瓦片列表
// ═══════════════════════════════════════════════════════════════

function tileKey(t: Tile): string {
  if (t.kind === 'button') return t.button.id
  if (t.kind === 'combo') return t.combo.id

  return t.set.id
}

function tileLabel(t: Tile): string {
  if (t.kind === 'button') return t.button.label
  if (t.kind === 'combo') return t.combo.label

  return t.set.label
}

/** 瓦片类型的中文短名（列表里的那枚小标签）。 */
function tileTag(t: Tile): string {
  if (t.kind === 'button') return '按钮'
  return t.kind === 'combo' ? '组' : '集'
}

/** 瓦片类型对应的 CSS 类（与 `tileTag` 一一对应）。 */
function tileTagClass(t: Tile): string {
  return t.kind
}

function tileSub(t: Tile): string {
  if (t.kind === 'button') {
    const n = t.button.actions?.length ?? 0
    const timer = t.button.timerEnabled ? ' · 定时' : ''
    return `${n} 个动作${timer}`
  }

  if (t.kind === 'combo') {
    const steps = t.combo.steps ?? []
    const between = steps.slice(0, -1).reduce((sum, st) => sum + (st.delayMs ?? 0), 0)
    return between > 0 ? `${steps.length} 个按钮 · 间隔 ${between}ms` : `${steps.length} 个按钮`
  }

  return `${t.set.subButtons?.length ?? 0} 个子按钮 · ${t.set.targetIp}:${t.set.targetPort}`
}

/** 按当前顺序把 order 重编号为 0..N-1（服务端清洗时也会做同样的事，这里是为了界面即时正确）。 */
function applyOrder(list: Tile[]) {
  list.forEach((t, i) => {
    if (t.kind === 'button') t.button.order = i
    else if (t.kind === 'combo') t.combo.order = i
    else t.set.order = i
  })
}

function moveTile(tile: Tile, delta: number) {
  const list = mergeTiles(draft.value)
  const from = list.findIndex((t) => tileKey(t) === tileKey(tile))
  const to = from + delta
  if (from < 0 || to < 0 || to >= list.length) return

  const [moved] = list.splice(from, 1)
  list.splice(to, 0, moved)
  applyOrder(list)
}

function duplicateTile(tile: Tile) {
  const d = draft.value
  if (!d) return

  const list = mergeTiles(d)
  const at = list.findIndex((t) => tileKey(t) === tileKey(tile))

  if (tile.kind === 'button') {
    const copy = deepCopy(tile.button)
    copy.id = newId()
    copy.label = `${tile.button.label} 副本`
    copy.actions = (copy.actions ?? []).map((a) => ({ ...a, id: newId() }))
    copy.monthlyPlans = (copy.monthlyPlans ?? []).map((p) => ({ ...p, id: newId() }))

    d.buttons.push(copy)
    list.splice(at + 1, 0, { kind: 'button', order: 0, button: copy })
    applyOrder(list)
    selected.value = { kind: 'button', id: copy.id }
  } else if (tile.kind === 'combo') {
    const copy = deepCopy(tile.combo)
    copy.id = newId()
    copy.label = `${tile.combo.label} 副本`

    // ⚠️ 成员**照抄**（还是引用同一批主按钮）—— 复制出来的组合不是"新的按钮"，
    //    它只是同一串按钮的另一条触发路径。若这里顺手换 Id，用户会得到一个指向不存在按钮的空组合。
    if (!d.comboButtons) d.comboButtons = []
    d.comboButtons.push(copy)
    list.splice(at + 1, 0, { kind: 'combo', order: 0, combo: copy })
    applyOrder(list)
    selected.value = { kind: 'combo', id: copy.id }
  } else {
    const copy = deepCopy(tile.set)
    copy.id = newId()
    copy.label = `${tile.set.label} 副本`
    copy.subButtons = (copy.subButtons ?? []).map((s) => ({
      ...s,
      id: newId(),
      actions: (s.actions ?? []).map((a) => ({ ...a, id: newId() })),
    }))

    d.subButtonSets.push(copy)
    list.splice(at + 1, 0, { kind: 'set', order: 0, set: copy })
    applyOrder(list)
    selected.value = { kind: 'set', id: copy.id }
  }
}

function removeTile(tile: Tile) {
  const d = draft.value
  if (!d) return

  const key = tileKey(tile)

  // 两段式确认：第一次点击只是"待确认"，避免误删整组动作。
  if (pendingDelete.value !== key) {
    pendingDelete.value = key
    if (pendingDeleteTimer !== null) window.clearTimeout(pendingDeleteTimer)
    pendingDeleteTimer = window.setTimeout(() => (pendingDelete.value = null), 4000)
    return
  }

  pendingDelete.value = null

  if (tile.kind === 'button') {
    const at = d.buttons.findIndex((b) => b.id === tile.button.id)
    if (at >= 0) d.buttons.splice(at, 1)
  } else if (tile.kind === 'combo') {
    // ⚠️ `comboButtons` 在类型上是可选的（老配置反序列化后可能是 undefined），
    //    所以这里先把它落成一个**局部变量**再判断 —— 直接写 `(d.comboButtons ?? [])` 找索引，
    //    后面的 `d.comboButtons.splice` 编译器仍然认为它可能是 undefined。
    const combos = d.comboButtons ?? []
    const at = combos.findIndex((c) => c.id === tile.combo.id)
    if (at >= 0) combos.splice(at, 1)
  } else {
    const at = d.subButtonSets.findIndex((s) => s.id === tile.set.id)
    if (at >= 0) d.subButtonSets.splice(at, 1)
  }

  applyOrder(mergeTiles(d))
  if (selected.value?.id === key) selected.value = null
}

function addButton() {
  const d = draft.value
  if (!d) return

  // order 给一个大值，重编号后自然落在末尾。
  const button = newButton(9999)
  d.buttons.push(button)
  applyOrder(mergeTiles(d))
  selected.value = { kind: 'button', id: button.id }
}

function addCombo() {
  const d = draft.value
  if (!d) return

  const combo = newComboButton(9999)
  if (!d.comboButtons) d.comboButtons = []
  d.comboButtons.push(combo)
  applyOrder(mergeTiles(d))
  selected.value = { kind: 'combo', id: combo.id }
}

function addSet() {
  const d = draft.value
  if (!d) return

  const set = newSubButtonSet(9999)
  d.subButtonSets.push(set)
  applyOrder(mergeTiles(d))
  selected.value = { kind: 'set', id: set.id }
}

function select(tile: Tile) {
  selected.value = { kind: tile.kind, id: tileKey(tile) }
}

// ═══════════════════════════════════════════════════════════════
// 保存
// ═══════════════════════════════════════════════════════════════

async function save() {
  const d = draft.value
  const current = props.config
  if (!d || !current || saving.value) return

  // 双保险：模板里按钮已禁用，这里再拦一道（快捷键/程序化调用都绕不过）。
  if (!canOperate.value) {
    saveError.value = '当前角色为查看者，只能浏览配置，不能保存。'
    return
  }

  saving.value = true
  saveError.value = ''
  conflict.value = null

  try {
    await api.saveConfig(d, current.version, props.user?.userName)
    baseline.value = serialize(d)
    dirty.value = false
    remoteDirty.value = false
    emit('notice', 'ok', '配置已保存。')
    emit('refresh-config')
  } catch (e) {
    if (e instanceof ApiError && e.status === 409) {
      const body = e.body as ConfigConflictBody | null
      conflict.value = body
      saveError.value = body?.message ?? '配置已被其他会话修改。'
    } else {
      saveError.value = e instanceof Error ? e.message : String(e)
    }
  } finally {
    saving.value = false
  }
}

/** 冲突后强行用本地版本覆盖：带上服务端最新版本号重新提交一次。 */
async function forceSave() {
  const d = draft.value
  const actual = conflict.value?.actualVersion
  if (!d || actual === undefined) return
  if (!canOperate.value) return

  saving.value = true
  saveError.value = ''

  try {
    await api.saveConfig(d, actual, props.user?.userName)
    baseline.value = serialize(d)
    dirty.value = false
    conflict.value = null
    remoteDirty.value = false
    emit('notice', 'warn', '已用本地版本覆盖服务端配置。')
    emit('refresh-config')
  } catch (e) {
    saveError.value = e instanceof Error ? e.message : String(e)
  } finally {
    saving.value = false
  }
}

function discard() {
  if (!props.config) return
  resetDraft(props.config.settings, true)

  if (!selected.value && tiles.value.length > 0) selected.value = null
  emit('notice', 'ok', '已放弃未保存的改动。')
}

// ═══════════════════════════════════════════════════════════════
// 生命周期
// ═══════════════════════════════════════════════════════════════

function onBeforeUnload(e: BeforeUnloadEvent) {
  if (!dirty.value) return
  e.preventDefault()
  e.returnValue = ''
}

onMounted(() => window.addEventListener('beforeunload', onBeforeUnload))

onUnmounted(() => {
  window.removeEventListener('beforeunload', onBeforeUnload)
  if (pendingDeleteTimer !== null) window.clearTimeout(pendingDeleteTimer)
})
</script>

<template>
  <div>
    <!-- ═══ 工具条 ═══ -->
    <div class="toolbar panel">
      <div class="left">
        <span v-if="dirty" class="badge warn">有未保存的改动</span>
        <span v-else class="badge ok">与服务端一致</span>
        <span class="muted small">版本 v{{ config?.version ?? 0 }}</span>
        <span class="muted small">
          {{ draft?.buttons?.length ?? 0 }} 个按钮 · {{ draft?.subButtonSets?.length ?? 0 }} 个子按钮集
        </span>
      </div>

      <div class="panel-actions">
        <button class="sm" :disabled="!dirty || saving" @click="discard">放弃修改</button>
        <button class="sm" @click="emit('refresh-config')">重新读取</button>
        <button class="primary" :disabled="!canOperate || !dirty || saving" @click="save">
          {{ saving ? '保存中…' : '保存配置' }}
        </button>
      </div>
    </div>

    <p v-if="!canOperate" class="notice warn" style="margin-bottom: 12px">
      <strong>只读模式</strong>
      <span>
        当前角色为 {{ user?.role ?? '未知' }}，只能浏览配置，不能修改或保存。
        需要编辑请联系管理员调整角色。
      </span>
    </p>

    <p v-if="remoteDirty" class="notice warn" style="margin-bottom: 12px">
      <strong>服务端配置已被别处修改</strong>
      <span>
        你的改动还在，但直接保存会与对方冲突。
        <button class="sm" style="margin-left: 6px" @click="discard">载入服务端最新（放弃我的改动）</button>
      </span>
    </p>

    <p v-if="saveError" class="notice err" style="margin-bottom: 12px">
      <strong>保存失败</strong>
      <span>{{ saveError }}</span>
    </p>

    <div v-if="conflict" class="notice warn" style="margin-bottom: 12px">
      <strong>版本冲突</strong>
      <span>
        你的版本 v{{ conflict.expectedVersion }}，服务端当前 v{{ conflict.actualVersion }}。
      </span>
      <button class="sm" :disabled="saving" @click="forceSave">用我的版本覆盖</button>
      <button class="sm" :disabled="saving" @click="discard">放弃我的改动</button>
    </div>

    <!--
      用一个 `fieldset` 承载整个可编辑区：`disabled` 会**原生地**把里面所有输入框与按钮
      一并置灰，不必逐个加 `:disabled`（漏一个就是一个可点的漏洞）。
      左侧瓦片行本身是可点的 `div`，不受影响 —— 查看者仍能浏览每一项的内容。
    -->
    <fieldset class="layout" :disabled="!canOperate">
      <!-- ═══ 左栏：瓦片列表 ═══ -->
      <aside class="side">
        <section class="panel">
          <div class="panel-head">
            <h2>瓦片（{{ tiles.length }}）</h2>
          </div>

          <div class="panel-actions" style="margin-bottom: 8px">
            <button class="sm primary" @click="addButton">+ 主按钮</button>
            <button class="sm" @click="addCombo">+ 组合按钮</button>
            <button class="sm" @click="addSet">+ 子按钮集</button>
          </div>

          <div v-if="tiles.length === 0" class="empty small">
            <strong>还没有瓦片</strong>
            点击上面的「+ 主按钮」开始，或在「设置」页导入旧备份。
          </div>

          <div
            v-for="(t, i) in tiles"
            :key="tileKey(t)"
            class="tile-row"
            :class="{ on: selected?.id === tileKey(t) }"
            @click="select(t)"
          >
            <span class="ord">{{ i + 1 }}</span>
            <span class="tag" :class="tileTagClass(t)">
              {{ tileTag(t) }}
            </span>

            <span class="info">
              <strong>{{ tileLabel(t) || '（未命名）' }}</strong>
              <span class="muted small">{{ tileSub(t) }}</span>
            </span>

            <span class="ops" @click.stop>
              <button class="sm" type="button" title="上移" :disabled="i === 0" @click="moveTile(t, -1)">↑</button>
              <button
                class="sm"
                type="button"
                title="下移"
                :disabled="i === tiles.length - 1"
                @click="moveTile(t, 1)"
              >
                ↓
              </button>
              <button class="sm" type="button" title="复制" @click="duplicateTile(t)">复制</button>
              <button
                class="sm"
                :class="pendingDelete === tileKey(t) ? 'danger solid' : 'danger'"
                type="button"
                @click="removeTile(t)"
              >
                {{ pendingDelete === tileKey(t) ? '确认删除' : '删除' }}
              </button>
            </span>
          </div>
        </section>
      </aside>

      <!-- ═══ 右栏：表单 ═══ -->
      <main class="main">
        <section class="panel">
          <div class="panel-head">
            <h2>
              <template v-if="selectedButton">按钮「{{ selectedButton.label || '未命名' }}」</template>
              <template v-else-if="selectedCombo">组合按钮「{{ selectedCombo.label || '未命名' }}」</template>
              <template v-else-if="selectedSet">子按钮集「{{ selectedSet.label || '未命名' }}」</template>
              <template v-else>请选择左边的一个瓦片</template>
            </h2>
          </div>

          <!--
            `:key` 绑选中 Id：换一个瓦片时整棵表单**重挂载**。
            只靠 props 变化的话 Vue 会复用同一个组件实例，折叠状态会从上一个按钮
            串到下一个（"刚展开了 3 条动作，切到别的按钮还是展开的"）。
          -->
          <ButtonForm
            v-if="selectedButton"
            :key="`button:${selectedButton.id}`"
            :button="selectedButton"
          />
          <ComboForm
            v-else-if="selectedCombo"
            :key="`combo:${selectedCombo.id}`"
            :combo="selectedCombo"
            :members="comboMemberOptions"
          />
          <SubSetForm
            v-else-if="selectedSet"
            :key="`set:${selectedSet.id}`"
            :set="selectedSet"
          />

          <div v-else class="empty">
            <strong>未选中任何瓦片</strong>
            在左侧列表中点击一项即可编辑它的外观、动作与定时。
          </div>
        </section>
      </main>
    </fieldset>
  </div>
</template>

<style scoped>
.toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
  margin-bottom: 14px;
  padding: 10px 14px;
}

.toolbar .left {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.layout {
  display: grid;
  grid-template-columns: 340px 1fr;
  gap: 16px;
  align-items: start;

  /* fieldset 默认带边框/内边距/最小宽度，会把栅格布局撑歪 —— 全部清零。 */
  border: 0;
  margin: 0;
  padding: 0;
  min-width: 0;
}

@media (max-width: 980px) {
  .layout {
    grid-template-columns: 1fr;
  }
}

.side {
  display: flex;
  flex-direction: column;
  gap: 16px;
  position: sticky;
  top: 12px;
}

@media (max-width: 980px) {
  .side {
    position: static;
  }
}

.tile-row {
  display: flex;
  align-items: center;
  gap: 7px;
  padding: 6px 8px;
  border: 1px solid transparent;
  border-radius: 6px;
  cursor: pointer;
}

.tile-row:hover {
  background: var(--panel-soft);
}

.tile-row.on {
  background: var(--accent-soft);
  border-color: #bfdbfe;
}

.tile-row .ord {
  font-family: var(--mono);
  font-size: 11px;
  color: var(--muted);
  width: 16px;
  text-align: right;
}

.tile-row .tag {
  flex: none;
  font-size: 10px;
  padding: 0 5px;
  border-radius: 3px;
  border: 1px solid var(--border);
  background: var(--panel-soft);
  color: var(--muted);
}

.tile-row .tag.btn {
  background: var(--accent-soft);
  border-color: #bfdbfe;
  color: #1e40af;
}

.tile-row .tag.set {
  background: #ecfdf5;
  border-color: #a7f3d0;
  color: #065f46;
}

.tile-row .info {
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
}

.tile-row .info strong {
  font-size: 13px;
  font-weight: 500;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.tile-row .ops {
  display: flex;
  gap: 3px;
  flex-wrap: wrap;
  justify-content: flex-end;
}

button.danger.solid {
  background: var(--err);
  border-color: var(--err);
  color: #fff;
}
</style>
