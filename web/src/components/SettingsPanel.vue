<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { ApiError, api } from '../api'
import type { ConfigConflictBody } from '../api'
import type { ConfigImportResult, ConfigView, ControlSettings, CurrentUser } from '../types'
import { copyText, downloadText, fmtBytes } from '../format'
import SystemInfoPanel from './SystemInfoPanel.vue'
import ListenAddressPanel from './ListenAddressPanel.vue'

const props = defineProps<{
  config: ConfigView | null
  user: CurrentUser | null
}>()

const emit = defineEmits<{
  (e: 'refresh-config'): void
  /** 监听端口已热切换 —— 当前页原点失效，交给 App.vue 倒计时搬家。 */
  (e: 'address-switched', port: number): void
}>()

// ── 导入 ──
const importText = ref('')
const importing = ref(false)
const importResult = ref<ConfigImportResult | null>(null)
const importError = ref('')
const conflict = ref<ConfigConflictBody | null>(null)
const pickedFileName = ref('')

// ── 导出 ──
const exportFormat = ref<'base64' | 'json'>('base64')
const exporting = ref(false)
const exported = ref<{ fileName: string; content: string; imageCount: number; bytes: number } | null>(null)
const exportError = ref('')
const copied = ref(false)

// ── 恢复出厂 ──
const resetArmed = ref(false)
const resetting = ref(false)

// ── 系统信息 ──
const machine = ref<{ machineCode: string; fingerprint: string; osDescription: string } | null>(null)

const isAdmin = computed(() => props.user?.role === 'Admin')
const currentIsEmpty = computed(() => props.config?.isEmpty ?? true)

/**
 * 能否修改全局设置 —— 与编辑页同一条线（后端 `Policies.CanOperate`）：
 * Admin / Operator 可写，Viewer 只读。
 *
 * 为什么这里不用 `isAdmin`：剩下的这一项（监听端口）本质是"配置里的默认值"，
 * 操作员日常就会动；真正的全站级动作（热切换端口，会让所有客户端失联）
 * 另有 AdminOnly 的入口，见 ListenAddressPanel。
 */
const canOperate = computed(
  () => props.user?.role === 'Admin' || props.user?.role === 'Operator',
)

// ═══════════════════════════════════════════════════════════════
// 全局（只剩「监听端口」一项）
// ═══════════════════════════════════════════════════════════════

/*
 * 这里原先还挂着四项：程序名、启用自由排布、默认按钮图片、主界面背景图。
 * 2026-10-15 按需求精简 —— **只留监听端口**，其余四项的编辑入口全部撤掉。
 *
 * ⚠️ 撤掉的是**界面入口**，不是数据模型里的字段：`ControlSettings` 仍是与旧版同构的结构，
 * 旧配置导入 / 导出要保持往返保真（见 `ControlServer.Core/Models/ControlSettings.cs`）。
 * 所以提交时**不去碰**那几个字段，整份带回去 —— 老配置里的程序名 / 图片不会被清空。
 * （想让它们彻底从模型里消失，是一次跨契约 + EF 迁移 + 导入导出的改动，不在本轮范围。）
 *
 * ⚠️ 提交时会用一次 `PUT /api/config` **整份替换** —— 服务端是全量语义（见 REF §23），
 * 所以这里从 `props.config.settings` 深拷贝一份再改这一个字段，其余（按钮、子按钮集、
 * 组合按钮）原样带回去。若改成"只发这一个字段"，保存一次就会把按钮全清空。
 */
const globalDraft = reactive({
  listenPort: 9999,
})

/** 上次与服务端同步时的快照，用来判断"有没有未保存改动"。 */
const globalBaseline = ref('')
const savingGlobal = ref(false)
const globalError = ref('')
const globalSaved = ref('')
/** 409 时服务端给的最新配置（供"载入服务端最新"用）。 */
const globalConflict = ref<ControlSettings | null>(null)

function serializeGlobal(): string {
  return JSON.stringify(globalDraft)
}

/**
 * 有没有未保存的改动。
 *
 * ⚠️ 必须用 `computed` 而不是函数：模板里写成 `v-if="globalDirty"` 时会按"函数对象"
 * 判定，永远为真 —— 于是"有未保存改动"的提示会一直挂着。
 */
const globalDirty = computed(() => serializeGlobal() !== globalBaseline.value)

/** 把一份服务端配置的全局字段读进本地草稿，并把它记为新的基线。 */
function applyGlobal(settings: ControlSettings) {
  globalDraft.listenPort = settings.listenPort ?? 9999

  globalBaseline.value = serializeGlobal()
  globalError.value = ''
  globalSaved.value = ''
  globalConflict.value = null
}

/** 重新从当前配置同步（放弃本地未保存的改动）。 */
function discardGlobal() {
  if (props.config) applyGlobal(props.config.settings)
}

// 服务端配置一变（别处改过、导入过、点了「重新读取」）就跟着走；
// 但**有未保存改动时不覆盖**用户的输入 —— 与编辑页同一条规矩。
watch(
  () => props.config,
  (next) => {
    if (!next) return
    if (globalDirty.value) return
    applyGlobal(next.settings)
  },
  { immediate: true },
)

async function saveGlobal() {
  const current = props.config
  if (!current || savingGlobal.value) return

  if (!canOperate.value) {
    globalError.value = '当前角色为查看者，只能浏览，不能修改全局设置。'
    return
  }

  savingGlobal.value = true
  globalError.value = ''
  globalSaved.value = ''
  globalConflict.value = null

  try {
    const settings: ControlSettings = JSON.parse(JSON.stringify(current.settings))
    settings.listenPort = globalDraft.listenPort

    const view = await api.saveConfig(settings, current.version, props.user?.userName)

    // 落库成功后再把基线推进到刚提交的值，否则界面会一直显示"有未保存改动"。
    globalBaseline.value = serializeGlobal()
    globalSaved.value = `已保存，当前版本 v${view.version}。`
    emit('refresh-config')
  } catch (e) {
    if (e instanceof ApiError && e.status === 409) {
      const body = e.body as ConfigConflictBody | null
      // 409 的响应体里带着服务端当前配置 —— 直接拿它的 settings，冲突时才能一键对齐。
      globalConflict.value = body?.current?.settings ?? null
      globalError.value =
        `配置已被其他会话修改（服务端 v${body?.actualVersion ?? '?'}），你的改动没有落库。`
      emit('refresh-config')
    } else {
      globalError.value = e instanceof Error ? e.message : String(e)
    }
  } finally {
    savingGlobal.value = false
  }
}

/** 冲突后改用服务端最新值（放弃本地改动）。 */
function loadServerGlobal() {
  if (globalConflict.value) applyGlobal(globalConflict.value)
  else discardGlobal()
}

async function onPickFile(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) return

  pickedFileName.value = file.name
  importError.value = ''
  importResult.value = null

  try {
    // 旧版导出的是明文 JSON，服务版导出的是 base64 单行文本 —— 两种都是文本，直接读。
    importText.value = await file.text()
  } catch (e) {
    importError.value = `读取文件失败：${e instanceof Error ? e.message : String(e)}`
  } finally {
    // 允许重复选择同一个文件（否则第二次 change 不触发）。
    input.value = ''
  }
}

async function doImport() {
  if (!importText.value.trim()) {
    importError.value = '请先粘贴备份内容，或选择一个备份文件。'
    return
  }

  importing.value = true
  importError.value = ''
  importResult.value = null
  conflict.value = null

  try {
    const result = await api.importConfig(importText.value, props.config?.version ?? null, props.user?.userName)
    importResult.value = result
    emit('refresh-config')
  } catch (e) {
    handleWriteError(e, '导入失败')
  } finally {
    importing.value = false
  }
}

async function doExport() {
  exporting.value = true
  exportError.value = ''
  copied.value = false

  try {
    const result = await api.exportConfig(exportFormat.value)
    exported.value = {
      fileName: result.fileName,
      content: result.content,
      imageCount: result.imageCount,
      bytes: result.bytes,
    }
  } catch (e) {
    exportError.value = e instanceof Error ? e.message : String(e)
    exported.value = null
  } finally {
    exporting.value = false
  }
}

async function doCopy() {
  if (!exported.value) return

  try {
    await copyText(exported.value.content)
    copied.value = true
    window.setTimeout(() => (copied.value = false), 2000)
  } catch (e) {
    exportError.value = `复制失败：${e instanceof Error ? e.message : String(e)}`
  }
}

async function doReset() {
  if (!resetArmed.value) {
    resetArmed.value = true
    window.setTimeout(() => (resetArmed.value = false), 5000)
    return
  }

  resetting.value = true
  conflict.value = null

  try {
    await api.resetConfig(props.config?.version ?? null, props.user?.userName)
    resetArmed.value = false
    emit('refresh-config')
  } catch (e) {
    handleWriteError(e, '恢复出厂失败')
  } finally {
    resetting.value = false
  }
}

function clearImport() {
  importText.value = ''
  pickedFileName.value = ''
  importResult.value = null
  importError.value = ''
  conflict.value = null
}

async function loadMachine() {
  try {
    machine.value = await api.machineCode()
  } catch (e) {
    machine.value = { machineCode: '读取失败', fingerprint: '', osDescription: e instanceof Error ? e.message : '' }
  }
}

/** 端口热切换已受理 —— 上抛给 App.vue，由它统一做「N 秒后跳新地址」。 */
function onAddressSwitched(port: number) {
  emit('address-switched', port)
}

/**
 * 统一处理写接口错误：409 是**乐观锁冲突**（别人先改了配置），
 * 服务端已把当前配置一并返回，这里存下来直接展示，用户刷新即可看到最新版本。
 */
function handleWriteError(e: unknown, prefix: string) {
  if (e instanceof ApiError && e.status === 409) {
    const body = e.body as ConfigConflictBody | null
    conflict.value = body
    importError.value = body?.message ?? '配置已被其他会话修改。'
    emit('refresh-config')
    return
  }

  importError.value = `${prefix}：${e instanceof Error ? e.message : String(e)}`
}

loadMachine()
</script>

<template>
  <div>
    <!-- ═══ 配置状态 ═══ -->
    <section class="panel">
      <div class="panel-head">
        <h2>配置状态</h2>
        <button class="sm" @click="emit('refresh-config')">重新读取</button>
      </div>

      <div v-if="config" class="stats">
        <div><span class="muted">版本</span><strong>v{{ config.version }}</strong></div>
        <div><span class="muted">按钮</span><strong>{{ config.counts.buttons }}</strong></div>
        <div><span class="muted">子按钮集</span><strong>{{ config.counts.subButtonSets }}</strong></div>
        <div><span class="muted">子按钮</span><strong>{{ config.counts.subButtons }}</strong></div>
        <div>
          <span class="muted">状态</span>
          <span class="badge" :class="config.state === 'Ok' ? 'ok' : 'warn'">{{ config.state }}</span>
        </div>
      </div>

      <p v-if="!config" class="muted small">正在读取配置…</p>

      <p v-if="config?.state === 'Corrupt'" class="notice err" style="margin-top: 10px">
        <strong>配置文档损坏</strong>
        <span>服务端已回退默认值，且<strong>没有覆盖</strong>存储内容（避免把数据彻底抹掉）。请人工检查数据库的
          <code>config_documents</code> 表。</span>
      </p>

      <p v-else-if="currentIsEmpty" class="notice warn" style="margin-top: 10px">
        <strong>配置为空</strong>
        <span>还没有任何按钮。在下面「导入配置」里粘贴旧版导出的备份内容，即可把按钮全部搬过来。</span>
      </p>

      <div v-if="config && config.notes.length > 0" class="notice info" style="margin-top: 10px">
        <strong>读取时做了这些清洗</strong>
        <ul style="margin: 0; padding-left: 18px">
          <li v-for="(n, i) in config.notes" :key="i">{{ n }}</li>
        </ul>
      </div>
    </section>

    <!-- ═══ 全局 ═══ -->
    <section class="panel">
      <div class="panel-head">
        <h2>
          全局
          <span v-if="globalDirty" class="badge warn">有未保存的改动</span>
        </h2>

        <div class="panel-actions">
          <button class="sm" :disabled="!globalDirty || savingGlobal" @click="discardGlobal">
            放弃修改
          </button>
          <button
            class="sm primary"
            :disabled="!canOperate || !globalDirty || savingGlobal"
            @click="saveGlobal"
          >
            {{ savingGlobal ? '保存中…' : '保存' }}
          </button>
        </div>
      </div>

      <p class="muted small" style="margin-top: 0">
        这一项属于<strong>整套配置</strong>（与单个按钮无关）。保存时会连同按钮与子按钮集一起整份提交，
        因此这是唯一会与「编辑」页未保存改动打架的地方。
      </p>

      <p v-if="!config" class="muted small" style="margin: 0">正在读取配置…</p>

      <p v-else-if="!canOperate" class="notice warn">
        当前账号角色为 {{ user?.role ?? '未知' }}，只有管理员与操作员可以修改全局设置。
      </p>

      <!--
        用 `fieldset` 一并置灰里面的输入框：查看者仍能看到当前值 —— 只是改不动。
        （原先这里还有两个图片选择器，也是靠 fieldset 一起置灰的，已撤掉。）
      -->
      <fieldset v-if="config" class="global-fields" :disabled="!canOperate">
        <div class="field-narrow">
          <label>监听端口（配置项；换端口请用下面的「监听地址」热切换）</label>
          <input v-model.number="globalDraft.listenPort" type="number" min="1" max="65535" />
        </div>
      </fieldset>

      <p v-if="globalSaved" class="notice ok" style="margin-top: 10px">{{ globalSaved }}</p>

      <p v-if="globalError" class="notice err" style="margin-top: 10px">
        <strong>保存失败</strong>
        <span>{{ globalError }}</span>
      </p>

      <div v-if="globalConflict" class="notice warn" style="margin-top: 10px">
        <strong>版本冲突</strong>
        <span>你的改动还在，但没有落库。载入服务端最新值会丢掉这些改动。</span>
        <button class="sm" :disabled="savingGlobal" @click="loadServerGlobal">载入服务端最新</button>
      </div>
    </section>

    <!-- ═══ 导入 ═══ -->
    <section class="panel">
      <div class="panel-head">
        <h2>导入配置</h2>
      </div>

      <p class="muted small" style="margin-top: 0">
        支持三种内容，<strong>自动识别</strong>，与文件名无关：
        ① 旧版「设置 → 导出配置」产出的 <code>.controlbak</code> / JSON 明文；
        ② 本服务「导出配置」产出的 base64 单行文本；
        ③ 裸配置 JSON。导入是<strong>整体覆盖</strong>，且会连同图片一起写回本机。
      </p>

      <!--
        导入/导出/恢复出厂都是 **AdminOnly**（服务端 `config/import|export|reset`）：
        导入一次就能整体替换全站配置，导出则等于把整套现场配置（含图片）带走 ——
        这两件事都属于"系统级"操作，不是操作员的日常编辑。
      -->
      <p v-if="!isAdmin" class="notice warn">
        当前账号角色为 {{ user?.role ?? '未知' }}，只有管理员可以导入或导出配置。
      </p>

      <label>选择备份文件（也可以直接把内容粘贴到下面的框里）</label>
      <input type="file" accept=".controlbak,.json,.txt,application/json,text/plain" @change="onPickFile" />
      <p v-if="pickedFileName" class="muted small">已读取：{{ pickedFileName }}</p>

      <label>备份内容</label>
      <textarea v-model="importText" rows="8" placeholder="把 .controlbak 文件内容，或 base64 文本粘贴到这里…"></textarea>

      <div class="row-actions">
        <button class="primary" :disabled="!isAdmin || importing" @click="doImport">
          {{ importing ? '导入中…' : '导入并覆盖当前配置' }}
        </button>
        <button :disabled="importing" @click="clearImport">清空</button>
      </div>

      <p v-if="importError" class="notice err" style="margin-top: 10px">
        <strong>导入失败</strong>
        <span>{{ importError }}</span>
      </p>

      <div v-if="conflict" class="notice warn" style="margin-top: 10px">
        <strong>版本冲突</strong>
        <span>
          你的版本 v{{ conflict.expectedVersion }}，服务端当前 v{{ conflict.actualVersion }} ——
          配置已被其他会话改过。上面的状态已刷新为最新版本，请确认后再提交。
        </span>
      </div>

      <div v-if="importResult?.ok" class="notice ok" style="margin-top: 10px">
        <strong>{{ importResult.message }}</strong>
        <span class="small">
          按钮 {{ importResult.counts.buttons }} · 子按钮集 {{ importResult.counts.subButtonSets }} ·
          子按钮 {{ importResult.counts.subButtons }} · 图片 {{ importResult.imageCount }} · 新版本 v{{ importResult.version }}
        </span>
      </div>

      <div v-if="importResult && importResult.ignoredFields.length > 0" class="notice info" style="margin-top: 10px">
        <strong>已忽略 {{ importResult.ignoredFields.length }} 个服务版不使用的字段</strong>
        <span class="small mono">{{ importResult.ignoredFields.join('、') }}</span>
        <span class="small">
          这些是单机版专有项（窗口尺寸、锁定窗口、开机自启、用户密码哈希等），服务版没有对应概念，因此不会带入。
        </span>
      </div>

      <div v-if="importResult && importResult.notes.length > 0" class="notice info" style="margin-top: 10px">
        <strong>导入时的清洗说明</strong>
        <ul style="margin: 0; padding-left: 18px">
          <li v-for="(n, i) in importResult.notes" :key="i">{{ n }}</li>
        </ul>
      </div>
    </section>

    <!-- ═══ 导出 ═══ -->
    <section class="panel">
      <div class="panel-head">
        <h2>导出配置</h2>
      </div>

      <p class="muted small" style="margin-top: 0">
        导出自包含单文件：所有被引用的图片会以 base64 内嵌，因此一个文件就能在别的机器上完整恢复。
      </p>

      <p v-if="!isAdmin" class="notice warn">
        当前账号角色为 {{ user?.role ?? '未知' }}，只有管理员可以导出配置。
      </p>

      <div class="row-actions">
        <select v-model="exportFormat" style="width: 220px">
          <option value="base64">base64 单行文本（便于复制粘贴）</option>
          <option value="json">明文 JSON（便于查看与 git 对比）</option>
        </select>
        <button class="primary" :disabled="!isAdmin || exporting" @click="doExport">
          {{ exporting ? '生成中…' : '生成备份' }}
        </button>
      </div>

      <p v-if="exportError" class="notice err" style="margin-top: 10px">{{ exportError }}</p>

      <div v-if="exported" style="margin-top: 12px">
        <p class="muted small">
          {{ exported.fileName }} · 内嵌图片 {{ exported.imageCount }} 张 · 原始 JSON {{ fmtBytes(exported.bytes) }}
        </p>

        <textarea :value="exported.content" rows="8" readonly></textarea>

        <div class="row-actions">
          <button @click="doCopy">{{ copied ? '已复制' : '复制到剪贴板' }}</button>
          <button @click="downloadText(exported.fileName, exported.content)">下载文件</button>
        </div>
      </div>
    </section>

    <!-- ═══ 机器码与授权 ═══ -->
    <section class="panel">
      <div class="panel-head">
        <h2>机器码与授权</h2>
        <button class="sm" @click="loadMachine">刷新</button>
      </div>

      <p class="muted small" style="margin-top: 0">
        机器码用于授权核对，<strong>仅管理员可读</strong>。运行诊断（机器名、端口、运行时长、
        时间同步）在下面的「运行诊断」里，任何能操作的角色都能看。
      </p>

      <div v-if="machine" class="stats">
        <div><span class="muted">机器码</span><strong class="mono">{{ machine.machineCode }}</strong></div>
        <div class="wide"><span class="muted">系统</span><span>{{ machine.osDescription }}</span></div>
        <div class="wide"><span class="muted">指纹</span><span class="mono small">{{ machine.fingerprint }}</span></div>
      </div>
    </section>

    <!-- ═══ 运维：运行诊断 / 时间同步 / 监听地址（M2-E4 / E5）═══ -->
    <div class="ops-panels">
      <SystemInfoPanel />
      <ListenAddressPanel :user="user" @address-switched="onAddressSwitched" />
    </div>

    <!-- ═══ 危险区 ═══ -->
    <section class="panel">
      <div class="panel-head">
        <h2>恢复出厂设置</h2>
      </div>

      <p class="muted small" style="margin-top: 0">
        清空全部按钮、子按钮集、组合按钮与自定义图片，监听端口回到 9999。
        <strong>此操作不可撤销</strong> —— 建议先导出备份。
      </p>

      <p v-if="!isAdmin" class="notice warn">
        当前账号角色为 {{ user?.role ?? '未知' }}，只有管理员可以恢复出厂设置。
      </p>

      <button
        v-else
        class="danger"
        :disabled="resetting"
        @click="doReset"
      >
        {{ resetting ? '处理中…' : resetArmed ? '再次点击以确认恢复出厂' : '恢复出厂设置' }}
      </button>
    </section>
  </div>
</template>

<style scoped>
.ops-panels {
  margin-top: 16px;
}

/* fieldset 默认自带边框、内边距与 min-width，会把面板内部的对齐撑歪 —— 全部清零。 */
.global-fields {
  border: 0;
  margin: 0;
  padding: 0;
  min-width: 0;
}

/* 全局只剩一个数字框，input 全局是 width:100% —— 不限宽会横贯整块面板。 */
.field-narrow {
  max-width: 220px;
}

/* 这两个组件的根不是 .panel（SystemInfoPanel 是多面板容器），
   所以全局的 `.panel + .panel` 间距规则覆盖不到，这里补齐。 */
.ops-panels > * + * {
  margin-top: 16px;
}

.stats {
  display: flex;
  flex-wrap: wrap;
  gap: 10px 26px;
}

.stats > div {
  display: flex;
  flex-direction: column;
  font-size: 13px;
  min-width: 80px;
}

.stats > div.wide {
  flex: 1 1 320px;
  min-width: 240px;
}

.stats strong {
  font-size: 15px;
  font-weight: 500;
}

.row-actions {
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
  margin-top: 12px;
}

code {
  font-family: var(--mono);
  font-size: 12px;
  background: var(--panel-soft);
  border: 1px solid var(--border);
  border-radius: 4px;
  padding: 0 4px;
}
</style>
