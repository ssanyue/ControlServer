<script setup lang="ts">
/**
 * 指令库管理页（M2-G6）。
 *
 * 后端 E3 的三条端点（`GET/POST/PUT/DELETE /api/commands`）早就做完了，
 * 此前一直没有 UI 入口 —— 这一页把它们接出来。
 *
 * 两个关键语义（都由服务端决定，前端只负责如实展示）：
 * 1. 动作以**名称**引用指令（`dataType='指令库'` 时 `dataContent` 是指令名，大小写不敏感），
 *    所以**改名 / 删除都会让引用它的动作发送失败**。这里靠扫描本地配置给出预警。
 * 2. 指令库是独立文档，**不参与配置导入导出**（与旧版一致）。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '../api'
import { commandLibrary } from '../commandLibrary'
import { newId } from '../factory'
import type {
  ButtonAction,
  CommandItem,
  CommandMutationDto,
  ConfigView,
  CurrentUser,
  EncodePreviewDto,
} from '../types'

const props = defineProps<{
  user: CurrentUser | null
  /** 只用于扫描"哪些动作引用了这条指令" —— 指令库本身不依赖配置。 */
  config: ConfigView | null
}>()

const emit = defineEmits<{
  (e: 'notice', kind: 'ok' | 'err' | 'warn', text: string): void
}>()

type Mode = 'idle' | 'create' | 'edit'

// 与后端 Policies.CanOperate 对齐：Admin / Operator 可写，Viewer 只读。
// （前端只做提前禁用，真正的授权判定始终在服务端 —— 越权请求会被 403。）
const canOperate = computed(() => props.user?.role === 'Admin' || props.user?.role === 'Operator')

const items = ref<CommandItem[]>([])
const version = ref(0)
const count = ref(0)

const loading = ref(false)
const saving = ref(false)
const loadError = ref('')

const mode = ref<Mode>('idle')
const editingId = ref('')
/** 编辑前的原始条目（用于判断"改了名"并据此预警）。 */
const original = ref<CommandItem | null>(null)
const form = ref<CommandItem>(blank())
const formError = ref('')

const previewing = ref(false)
const preview = ref<EncodePreviewDto | null>(null)

const deleteArmedId = ref('')
let disarmTimer: number | null = null

/** 指令库允许的协议 —— **不含 WoL**（指令没有 MAC 语义），与服务端 Normalize 一致。 */
const PROTOCOLS = ['UDP', 'TCP']

function blank(): CommandItem {
  return { id: '', name: '', protocol: 'UDP', dataType: 'String', data: '' }
}

// ═══════════════════════════════════════════════════════════════
// 引用扫描（纯前端推断，仅用于预警）
// ═══════════════════════════════════════════════════════════════

/**
 * 指令名（小写）→ 引用它的动作描述。
 *
 * 服务端用 `OrdinalIgnoreCase` 匹配名称，所以这里也按小写归一。
 * 只统计 `dataType` 恰好为「指令库」的动作 —— 别的类型下 `dataContent` 是数据本身。
 */
const references = computed(() => {
  const map = new Map<string, string[]>()

  const scan = (owner: string, actions: ButtonAction[] | undefined) => {
    for (const a of actions ?? []) {
      if (a.dataType !== '指令库') continue

      const key = (a.dataContent ?? '').trim().toLowerCase()
      if (!key) continue

      const list = map.get(key) ?? []
      list.push(a.name?.trim() ? `${owner} › ${a.name.trim()}` : owner)
      map.set(key, list)
    }
  }

  const settings = props.config?.settings
  if (!settings) return map

  for (const b of settings.buttons ?? []) scan(`按钮「${b.label}」`, b.actions)

  for (const set of settings.subButtonSets ?? []) {
    for (const sub of set.subButtons ?? []) {
      scan(`子按钮集「${set.label}」› 子按钮「${sub.label}」`, sub.actions)
    }
  }

  return map
})

function refsOf(name: string): string[] {
  return references.value.get((name ?? '').trim().toLowerCase()) ?? []
}

/** 改名会切断按名称建立的引用 —— 这是本页最容易踩的坑，所以显式预警。 */
const renameWarning = computed(() => {
  if (mode.value !== 'edit' || !original.value) return ''

  const oldName = original.value.name.trim()
  const newName = form.value.name.trim()

  if (!newName || newName.toLowerCase() === oldName.toLowerCase()) return ''

  const refs = refsOf(oldName)
  if (refs.length === 0) return ''

  const shown = refs.slice(0, 3).join('、')
  const more = refs.length > 3 ? ` 等 ${refs.length} 处` : ''

  return `有 ${refs.length} 个动作仍按旧名「${oldName}」引用这条指令（${shown}${more}）。`
    + '改名后它们会找不到指令而发送失败 —— 请到「编辑」页把那些动作的指令重新选一次'
    + '（重新选中会把这条指令的定义填入动作、转为独立动作，之后就与这里的改名无关了）。'
})

// 数据类型或内容一变，旧预览就不作数了。
watch(
  () => [form.value.dataType, form.value.data],
  () => {
    preview.value = null
  },
)

// ═══════════════════════════════════════════════════════════════
// 读
// ═══════════════════════════════════════════════════════════════

async function load() {
  loading.value = true
  loadError.value = ''

  try {
    const res = await api.commands()
    items.value = res.commands ?? []
    version.value = res.version
    count.value = res.count

    // 顺手把共享缓存刷成同一份 —— 编辑页的「指令」下拉读的就是它，
    // 免得出现"刚在这儿看到指令，切到编辑页却选不到"。
    commandLibrary.sync(items.value, res.version)
  } catch (e) {
    loadError.value = `读取指令库失败：${e instanceof Error ? e.message : String(e)}`
  } finally {
    loading.value = false
  }
}

/**
 * 增删改的响应**无论成败都带回服务端最新全量列表**，
 * 所以直接用它覆盖本地，省掉一次 GET —— 也让"被拒绝"时界面立刻回到真实状态。
 */
function applyMutation(res: CommandMutationDto) {
  items.value = res.commands ?? []
  version.value = res.version
  count.value = res.commands?.length ?? 0

  // 同上：连共享缓存一起更新。这是"新增一条指令后回到动作编辑页就能选到"的关键一步。
  commandLibrary.sync(items.value, res.version)
}

// ═══════════════════════════════════════════════════════════════
// 编辑
// ═══════════════════════════════════════════════════════════════

function startCreate() {
  mode.value = 'create'
  editingId.value = ''
  original.value = null
  form.value = { ...blank(), id: newId() }
  formError.value = ''
  preview.value = null
}

function startEdit(item: CommandItem) {
  mode.value = 'edit'
  editingId.value = item.id
  original.value = { ...item }
  form.value = { ...item }
  formError.value = ''
  preview.value = null
}

/** 复制成一条新指令（名称自动避让重名，服务端对重名是直接拒绝的）。 */
function duplicate(item: CommandItem) {
  const used = new Set(items.value.map((c) => (c.name ?? '').trim().toLowerCase()))

  let name = `${item.name} 副本`
  let n = 2
  while (used.has(name.trim().toLowerCase())) {
    name = `${item.name} 副本${n}`
    n += 1
  }

  mode.value = 'create'
  editingId.value = ''
  original.value = null
  form.value = { ...item, id: newId(), name }
  formError.value = ''
  preview.value = null
}

function cancelEdit() {
  mode.value = 'idle'
  editingId.value = ''
  original.value = null
  form.value = blank()
  formError.value = ''
  preview.value = null
}

async function save() {
  const name = form.value.name.trim()
  if (!name) {
    formError.value = '指令名称不能为空。'
    return
  }

  saving.value = true
  formError.value = ''

  // 复制成普通对象再提交：避免把列表里的对象引用直接送出去。
  const payload: CommandItem = {
    id: form.value.id,
    name,
    protocol: form.value.protocol,
    dataType: form.value.dataType,
    data: form.value.data ?? '',
  }

  const isCreate = mode.value === 'create'

  try {
    const res = isCreate
      ? await api.createCommand(payload, props.user?.userName)
      : await api.updateCommand(editingId.value, payload, props.user?.userName)

    applyMutation(res)

    if (res.ok) {
      emit('notice', 'ok', isCreate ? `已新增指令「${name}」。` : `已保存指令「${name}」。`)
      cancelEdit()
    } else {
      // 重名 / 越界 / 找不到：服务端把原因放在 message 里，界面已用最新列表刷新。
      formError.value = res.message ?? '保存被拒绝。'
    }
  } catch (e) {
    formError.value = `保存失败：${e instanceof Error ? e.message : String(e)}`
  } finally {
    saving.value = false
  }
}

async function doDelete(item: CommandItem) {
  // 两段式确认：第一次点只"上膛"，5 秒内再点才真删。
  if (deleteArmedId.value !== item.id) {
    const refs = refsOf(item.name)

    if (refs.length > 0) {
      emit(
        'notice',
        'warn',
        `指令「${item.name}」还被 ${refs.length} 个动作引用（${refs.slice(0, 3).join('、')}）。`
          + '删除后那些动作会发送失败。再次点击删除按钮以确认。',
      )
    }

    deleteArmedId.value = item.id
    if (disarmTimer !== null) window.clearTimeout(disarmTimer)
    disarmTimer = window.setTimeout(() => (deleteArmedId.value = ''), 5000)
    return
  }

  deleteArmedId.value = ''
  if (disarmTimer !== null) {
    window.clearTimeout(disarmTimer)
    disarmTimer = null
  }

  saving.value = true

  try {
    const res = await api.deleteCommand(item.id, props.user?.userName)
    applyMutation(res)
    emit(
      'notice',
      res.ok ? 'ok' : 'err',
      res.ok ? `已删除指令「${item.name}」。` : (res.message ?? '删除失败。'),
    )
  } catch (e) {
    emit('notice', 'err', `删除失败：${e instanceof Error ? e.message : String(e)}`)
  } finally {
    saving.value = false
  }
}

async function doPreview() {
  previewing.value = true
  preview.value = null
  formError.value = ''

  try {
    preview.value = await api.previewEncode(form.value.dataType, form.value.data ?? '')
  } catch (e) {
    formError.value = `预览失败：${e instanceof Error ? e.message : String(e)}`
  } finally {
    previewing.value = false
  }
}

onMounted(load)
</script>

<template>
  <section class="panel">
    <div class="panel-head">
      <h2>
        指令库
        <span class="muted small">（{{ count }} 条 · 版本 v{{ version }}）</span>
      </h2>

      <div class="panel-actions">
        <button class="sm" :disabled="loading" @click="load">
          {{ loading ? '读取中…' : '刷新' }}
        </button>
        <button class="sm primary" :disabled="!canOperate || mode !== 'idle'" @click="startCreate">
          新增指令
        </button>
      </div>
    </div>

    <p class="muted small" style="margin-top: 0">
      指令库是可以复用的「发送什么」定义。动作把数据类型选成 <strong>指令库</strong>、
      数据内容填<strong>指令名</strong>即可引用（大小写不敏感，目标地址仍由动作提供）。
      指令库<strong>不参与配置导入导出</strong> —— 换机器时请单独重建。
    </p>

    <p v-if="!canOperate" class="notice warn">
      当前角色为 {{ user?.role ?? '未知' }}，只能查看，不能修改指令库。
    </p>

    <p v-if="loadError" class="notice err">{{ loadError }}</p>

    <!-- ═══ 编辑表单 ═══ -->
    <div v-if="mode !== 'idle'" class="editor">
      <div class="editor-head">
        <strong>
          {{ mode === 'create' ? '新增指令' : `编辑「${original?.name ?? ''}」` }}
        </strong>
        <button class="sm" @click="cancelEdit">取消</button>
      </div>

      <div class="grid-2">
        <div>
          <label>指令名称（动作按这个名字引用它）</label>
          <input v-model="form.name" placeholder="例如：开灯" @keyup.enter="save" />
        </div>
        <div>
          <label>协议</label>
          <select v-model="form.protocol">
            <option v-for="p in PROTOCOLS" :key="p" :value="p">{{ p }}</option>
          </select>
        </div>
      </div>

      <div class="grid-2 data">
        <div>
          <label>数据类型</label>
          <select v-model="form.dataType">
            <option value="String">String（UTF-8 文本）</option>
            <option value="Hex">Hex（十六进制）</option>
            <option value="ANSI">ANSI（本机代码页 GBK）</option>
            <option value="ASCII">ASCII（非 ASCII 字符替换为 ?）</option>
          </select>
        </div>
        <div>
          <label>{{ form.dataType === 'Hex' ? '数据内容（十六进制，如 AA BB）' : '数据内容' }}</label>
          <input v-model="form.data" placeholder="Hello" />
        </div>
      </div>

      <p class="muted small" style="margin: 6px 0 0">
        指令库只支持 UDP / TCP 与上面 4 种数据类型 —— 不含 WoL（指令没有 MAC 语义），
        也不能再选「指令库」（那会让解析无限套娃）。服务端会拒绝这类取值。
      </p>

      <p v-if="renameWarning" class="notice warn small" style="margin-top: 10px">{{ renameWarning }}</p>

      <div class="row-actions">
        <button class="primary" :disabled="saving" @click="save">
          {{ saving ? '保存中…' : mode === 'create' ? '新增' : '保存' }}
        </button>
        <button :disabled="previewing" @click="doPreview">
          {{ previewing ? '解析中…' : '预览实际字节' }}
        </button>
      </div>

      <p v-if="formError" class="notice err" style="margin-top: 8px">{{ formError }}</p>

      <div
        v-if="preview"
        class="notice small"
        :class="preview.error ? 'err' : 'info'"
        style="margin-top: 8px"
      >
        <template v-if="preview.error">
          <strong>无法编码</strong><span>{{ preview.error }}</span>
        </template>
        <template v-else>
          <span class="mono">{{ preview.length }} 字节</span>
          <span class="mono">HEX {{ preview.hexCompact }}</span>
          <span v-if="preview.ansiEncoding" class="mono">ANSI {{ preview.ansiEncoding }}</span>
        </template>
      </div>
    </div>

    <!-- ═══ 列表 ═══ -->
    <p v-if="loading && items.length === 0" class="muted small">正在读取指令库…</p>

    <div v-else-if="items.length === 0" class="empty">
      <strong>指令库还是空的</strong>
      <span>
        点右上角「新增指令」建一条，然后在「编辑」页把某个动作的数据类型选成「指令库」、
        数据内容填这条指令的名字。
      </span>
    </div>

    <div v-else class="table-wrap">
      <table class="grid">
        <thead>
          <tr>
            <th>名称</th>
            <th>协议</th>
            <th>类型</th>
            <th>数据内容</th>
            <th>被引用</th>
            <th>操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="c in items" :key="c.id" :class="{ editing: mode === 'edit' && editingId === c.id }">
            <td><strong>{{ c.name }}</strong></td>
            <td><span class="badge">{{ c.protocol }}</span></td>
            <td><span class="badge">{{ c.dataType }}</span></td>
            <td class="wrap">{{ c.data }}</td>
            <td>
              <span
                v-if="refsOf(c.name).length > 0"
                class="badge warn"
                :title="refsOf(c.name).join('\n')"
              >
                {{ refsOf(c.name).length }} 处
              </span>
              <span v-else class="muted small">—</span>
            </td>
            <td>
              <div class="row-actions" style="margin: 0">
                <button class="sm" :disabled="!canOperate" @click="startEdit(c)">编辑</button>
                <button class="sm" :disabled="!canOperate" @click="duplicate(c)">复制</button>
                <button
                  class="sm"
                  :class="{ danger: deleteArmedId === c.id }"
                  :disabled="!canOperate || saving"
                  @click="doDelete(c)"
                >
                  {{ deleteArmedId === c.id ? '确认删除' : '删除' }}
                </button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</template>

<style scoped>
.editor {
  border: 1px solid var(--border-strong);
  border-radius: var(--radius);
  background: var(--panel-soft);
  padding: 12px 14px;
  margin-bottom: 14px;
}

.editor-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 4px;
}

.grid-2 {
  display: grid;
  grid-template-columns: 1.6fr 1fr;
  gap: 0 12px;
}

.grid-2.data {
  grid-template-columns: 1fr 1.8fr;
}

@media (max-width: 780px) {
  .grid-2,
  .grid-2.data {
    grid-template-columns: 1fr;
  }
}

.row-actions {
  display: flex;
  gap: 6px;
  align-items: center;
  flex-wrap: wrap;
  margin-top: 10px;
}

table.grid td.wrap {
  max-width: 320px;
}

tr.editing td {
  background: var(--accent-soft);
}
</style>
