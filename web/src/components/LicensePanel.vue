<script setup lang="ts">
/**
 * 设备授权页（M3）。
 *
 * <p>
 * 这一页同时是**现场唯一能自救的地方**：授权闸门（`LicenseGate`）特意把
 * `/api/license/*` 放进白名单，就是为了避免"没授权 → 不能导入授权"的死结。
 * 因此这一页在**未授权时也必须完全可用** —— 不能有任何依赖授权才显示的入口。
 * </p>
 *
 * <p>
 * 读写权限不同（与服务端 `LicenseController` 一致）：状态（`GET /api/license`）
 * 所有已登录角色都能看，因为"为什么按钮点不动"这件事人人需要知道；
 * 而申请文本 / 导入 / 试用 / 移除都是**设备级凭证**操作，一律管理员。
 * 前端按角色提前禁用，真正的判定仍在服务端。
 * </p>
 */
import { computed, onMounted, ref } from 'vue'
import { ApiError, api } from '../api'
import type { CurrentUser, LicenseDto, LicenseMutationResult, LicenseRequestDto } from '../types'
import { copyText, fmtDateTime } from '../format'

const props = defineProps<{
  user: CurrentUser | null
  /** 由 App 持有的授权状态（顶栏徽章与横幅读的是同一份，避免两处不一致）。 */
  license: LicenseDto | null
}>()

const emit = defineEmits<{
  (e: 'notice', kind: 'ok' | 'err' | 'warn', text: string): void
  (e: 'changed', license: LicenseDto): void
}>()

const d = computed(() => props.license)
const isAdmin = computed(() => props.user?.role === 'Admin')

const loading = ref(false)
const busy = ref(false)

/** 申请文本（管理员按需加载 —— 它含机器码，没必要每次进页面都取）。 */
const request = ref<LicenseRequestDto | null>(null)
const loadingRequest = ref(false)

const importText = ref('')
const importError = ref('')
const fileInput = ref<HTMLInputElement | null>(null)
const dragover = ref(false)

/** 移除授权是设备级操作，两段式确认（5 秒内再点一次才真删）。 */
const removeArmed = ref(false)
let disarmTimer: number | null = null

// ═══════════════════════════════════════════════════════════════
// 展示
// ═══════════════════════════════════════════════════════════════

/** 核心能力当前是否真的被拦（与服务端 `LicenseGateState.IsBlocked` 同一判据）。 */
const blocked = computed(
  () => d.value !== null && d.value.enforcement === 'Enforce' && !d.value.isUsable,
)

const badgeClass = computed(() => {
  if (!d.value) return ''
  return d.value.isUsable ? (d.value.kind === 'trial' ? 'warn' : 'ok') : 'err'
})

/**
 * 剩余天数的可读文案。
 *
 * `99999` / `-1` 是**哨兵值**而不是天数（见 `LicenseState`），直接渲染会出现
 * "剩余 99999 天"这种一眼假的显示，必须特判。
 */
const remainingText = computed(() => {
  const v = d.value?.remainingDays
  if (v === undefined || v === null) return '—'
  if (v === 99999) return '永久'
  if (v === -1) return '未知'
  if (v <= 0) return '已过期'
  return `${v} 天`
})

const expiresText = computed(() => {
  const at = d.value?.expiresAt
  return at ? fmtDateTime(at) : '—'
})

const ENFORCEMENT_LABELS: Record<string, string> = {
  Off: '关闭（不校验）',
  Warn: '仅提示',
  Enforce: '强制拦截',
}

function enforcementLabel(value: string | undefined): string {
  return value ? (ENFORCEMENT_LABELS[value] ?? value) : '—'
}

/**
 * 生效档位与配置档位不一致时给一句解释。
 *
 * 唯一的正常来源是那条**升级规则**：配置写 `Warn`、但本机已存在试用记录时，
 * 生效档位升为 `Enforce` —— 否则试用到期只是提示、拦不住人。
 * 把这句话讲出来，省得现场以为是配置没生效。
 */
const enforcementNote = computed(() => {
  const s = d.value
  if (!s || s.enforcement === s.configuredEnforcement) return ''

  if (s.configuredEnforcement === 'Warn' && s.enforcement === 'Enforce') {
    return '配置里写的是「仅提示」，但本机已生成过试用记录，因此生效档位被提升为「强制拦截」——'
      + '否则试用到期只会提示、拦不住人。'
  }

  return `配置里写的是「${enforcementLabel(s.configuredEnforcement)}」，实际生效为「${enforcementLabel(s.enforcement)}」。`
})

// ═══════════════════════════════════════════════════════════════
// 读
// ═══════════════════════════════════════════════════════════════

async function refresh() {
  loading.value = true

  try {
    const next = await api.license()
    emit('changed', next)
  } catch (e) {
    emit('notice', 'err', `读取授权状态失败：${describe(e)}`)
  } finally {
    loading.value = false
  }
}

async function loadRequest() {
  if (!isAdmin.value) return

  loadingRequest.value = true

  try {
    request.value = await api.licenseRequest()
  } catch (e) {
    emit('notice', 'err', `生成申请文本失败：${describe(e)}`)
  } finally {
    loadingRequest.value = false
  }
}

// ═══════════════════════════════════════════════════════════════
// 写
// ═══════════════════════════════════════════════════════════════

function describe(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.status === 403) return '当前角色无权执行该操作（需要管理员）。'
    return e.message
  }
  return e instanceof Error ? e.message : String(e)
}

/**
 * 写操作的响应**无论成败都带回服务端当前全量状态**，直接用它刷新界面。
 *
 * 失败时它是以 400/409/503 抛出来的（`unwrap` 只认 2xx），所以要从 `ApiError.body`
 * 里把那份状态捞出来 —— 这正是"失败也不停留在旧状态"的关键：例如导入被判
 * "机器码不符"时，界面上的机器码应当立刻变成服务端的真实值，而不是继续显示旧的。
 */
function applyResult(result: LicenseMutationResult): void {
  if (result.license) emit('changed', result.license)
}

function handleFailure(e: unknown): string {
  if (e instanceof ApiError) {
    const body = e.body as LicenseMutationResult | null

    if (body && typeof body.message === 'string' && body.message) {
      applyResult(body)
      return body.message
    }

    if (e.status === 403) return '当前角色无权执行该操作（需要管理员）。'
    if (e.status === 503) return e.message
    return e.message
  }

  return e instanceof Error ? e.message : String(e)
}

async function submitImport() {
  const text = importText.value.trim()
  if (!text) {
    importError.value = '请粘贴授权文件内容，或直接把 .lic 文件拖到下面的虚线框里。'
    return
  }

  busy.value = true
  importError.value = ''

  try {
    const res = await api.importLicense(text)
    applyResult(res)
    importText.value = ''
    emit('notice', 'ok', res.message || '授权已导入。')
  } catch (e) {
    importError.value = handleFailure(e)
  } finally {
    busy.value = false
  }
}

async function pickFile() {
  fileInput.value?.click()
}

async function onFileChosen(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = '' // 允许再次选择同一个文件
  if (file) await readFile(file)
}

async function onDrop(event: DragEvent) {
  dragover.value = false
  const file = event.dataTransfer?.files?.[0]
  if (file) await readFile(file)
}

/**
 * 读一个 `.lic` 文件到文本框。
 *
 * 只读**文本内容**，不落盘 —— 授权文件由用户主动提供，服务端只认签名，
 * 前端不需要（也不应该）替用户判断内容对不对。
 */
async function readFile(file: File) {
  importError.value = ''

  if (file.size > 1024 * 1024) {
    importError.value = `「${file.name}」有 ${Math.round(file.size / 1024)} KB，授权文件不该这么大，请确认选对了文件。`
    return
  }

  try {
    importText.value = await file.text()
    emit('notice', 'ok', `已读取「${file.name}」，点「导入授权」即可生效。`)
  } catch (e) {
    importError.value = `读取「${file.name}」失败：${e instanceof Error ? e.message : String(e)}`
  }
}

async function generateTrial() {
  busy.value = true

  try {
    const res = await api.generateTrial()
    applyResult(res)
    emit('notice', 'ok', res.message || '试用已生成。')
    await loadRequest()
  } catch (e) {
    emit('notice', 'err', handleFailure(e))
  } finally {
    busy.value = false
  }
}

async function doRemove() {
  // 两段式确认：第一次点只"上膛"，5 秒内再点才真删。
  if (!removeArmed.value) {
    removeArmed.value = true
    if (disarmTimer !== null) window.clearTimeout(disarmTimer)
    disarmTimer = window.setTimeout(() => (removeArmed.value = false), 5000)
    return
  }

  removeArmed.value = false
  if (disarmTimer !== null) {
    window.clearTimeout(disarmTimer)
    disarmTimer = null
  }

  busy.value = true

  try {
    const res = await api.removeLicense()
    applyResult(res)
    emit('notice', 'ok', res.message || '授权文件已移除。')
  } catch (e) {
    emit('notice', 'err', handleFailure(e))
  } finally {
    busy.value = false
  }
}

async function copy(text: string, what: string) {
  try {
    await copyText(text)
    emit('notice', 'ok', `${what}已复制到剪贴板。`)
  } catch (e) {
    emit('notice', 'err', `复制失败：${e instanceof Error ? e.message : String(e)}`)
  }
}

onMounted(() => {
  // 正常路径上 App 已经拉过一次，这里只处理"进页面时还没拿到"的情况，避免重复请求。
  if (!props.license) void refresh()
})
</script>

<template>
  <section class="panel">
    <div class="panel-head">
      <h2>
        设备授权
        <span v-if="d" class="badge" :class="badgeClass" style="margin-left: 6px">
          {{ d.statusDisplayName }}
        </span>
      </h2>

      <div class="panel-actions">
        <button class="sm" :disabled="loading" @click="refresh">
          {{ loading ? '读取中…' : '刷新' }}
        </button>
      </div>
    </div>

    <p class="muted small" style="margin-top: 0">
      授权绑定<strong>本台机器</strong>（按机器码判定）。未授权时，登录、查看、编辑配置与
      <strong>导入授权仍然可用</strong>，但<strong>执行动作 / 对接码监听 / 定时触发</strong>会被停用。
    </p>

    <p v-if="!d" class="muted small">正在读取授权状态…</p>

    <template v-else>
      <!-- ═══ 状态摘要 ═══ -->
      <div class="lic-card" :class="d.isUsable ? (d.kind === 'trial' ? 'warn' : 'ok') : 'err'">
        <div class="lic-main">
          <strong class="lic-title">{{ d.statusDisplayName }}</strong>
          <span class="muted small">{{ d.kindDisplayName }} · 剩余 {{ remainingText }}</span>
          <p class="lic-info">{{ d.info }}</p>
          <p v-if="d.lastError" class="lic-error">失败原因：{{ d.lastError }}</p>
          <p v-if="blocked" class="lic-error">
            核心能力<strong>已停摆</strong>：动作执行会被服务端以 403 拒绝，对接码监听与定时触发也已暂停。
            导入授权后<strong>无需重启</strong>，后台任务会自动恢复。
          </p>
        </div>

        <dl class="lic-meta">
          <div>
            <dt>到期时间</dt>
            <dd>{{ expiresText }}</dd>
          </div>
          <div>
            <dt>执行档位</dt>
            <dd>
              {{ enforcementLabel(d.enforcement) }}
              <span class="muted small">（配置：{{ enforcementLabel(d.configuredEnforcement) }}）</span>
            </dd>
          </div>
        </dl>
      </div>

      <p v-if="enforcementNote" class="notice info" style="margin-bottom: 14px">
        {{ enforcementNote }}
      </p>

      <!-- ═══ 机器码 / 申请文本 ═══ -->
      <h3 class="lic-h3">本机机器码</h3>
      <div class="lic-row">
        <code class="lic-code">{{ d.machineCode }}</code>
        <button class="sm" @click="copy(d.machineCode, '机器码')">复制</button>
      </div>

      <template v-if="isAdmin">
        <h3 class="lic-h3">授权申请</h3>
        <p class="muted small" style="margin-top: 0">
          把下面这段发给供应方，对方据此签发绑定本机的授权文件；拿到 `.lic` 后在下面导入即可。
        </p>

        <div class="lic-row">
          <button class="sm" :disabled="loadingRequest" @click="loadRequest">
            {{ loadingRequest ? '生成中…' : request ? '重新生成' : '生成申请文本' }}
          </button>
          <button v-if="request" class="sm" @click="copy(request.text, '申请文本')">复制申请文本</button>
        </div>

        <pre v-if="request" class="lic-pre">{{ request.text }}</pre>
      </template>

      <!-- ═══ 导入 ═══ -->
      <h3 class="lic-h3">导入授权文件</h3>

      <template v-if="isAdmin">
        <div
          class="lic-drop"
          :class="{ active: dragover }"
          @dragover.prevent="dragover = true"
          @dragleave.prevent="dragover = false"
          @drop.prevent="onDrop"
        >
          <span>把 `.lic` 文件拖到这里，或</span>
          <button class="sm" @click="pickFile">选择文件</button>
          <input
            ref="fileInput"
            type="file"
            accept=".lic,.txt,text/plain"
            style="display: none"
            @change="onFileChosen"
          />
          <span class="muted small">也可以直接把文件里的文本粘到下面</span>
        </div>

        <textarea
          v-model="importText"
          class="lic-textarea"
          rows="5"
          placeholder="粘贴授权文件内容（整段 Base64 文本，换行与空格会被忽略）"
        />

        <div class="row-actions">
          <button class="primary" :disabled="busy || !importText.trim()" @click="submitImport">
            {{ busy ? '处理中…' : '导入授权' }}
          </button>
          <button class="sm" :disabled="busy || !importText" @click="importText = ''">清空</button>

          <button
            v-if="!d.isUsable"
            class="sm"
            :disabled="busy"
            title="生成一份本机 30 天试用。仅在既无授权、也无有效试用时可用"
            @click="generateTrial"
          >
            生成 30 天试用
          </button>

          <button
            v-if="d.document"
            class="sm"
            :class="{ danger: removeArmed }"
            :disabled="busy"
            title="移除授权文件（不影响本机试用记录）"
            @click="doRemove"
          >
            {{ removeArmed ? '确认移除' : '移除授权' }}
          </button>
        </div>

        <p v-if="importError" class="notice err" style="margin-top: 8px">{{ importError }}</p>
      </template>

      <p v-else class="muted small">
        导入授权、生成试用与移除授权需要<strong>管理员</strong>身份。当前角色可以查看状态与机器码。
      </p>

      <!-- ═══ 明细 ═══ -->
      <template v-if="d.document">
        <h3 class="lic-h3">授权文件明细</h3>
        <div class="table-wrap">
          <table class="grid">
            <tbody>
              <tr>
                <th>序列号</th>
                <td>{{ d.document.serialNumber || '—' }}</td>
                <th>厂商标识</th>
                <td>{{ d.document.vendor || '—' }}</td>
              </tr>
              <tr>
                <th>客户备注</th>
                <td>{{ d.document.customer || '—' }}</td>
                <th>签发日</th>
                <td>{{ d.document.issuedOn || '—' }}</td>
              </tr>
              <tr>
                <th>绑定机器码</th>
                <td class="wrap">{{ d.document.boundMachineCode || '（通用授权，未绑定）' }}</td>
                <th>绑定时刻</th>
                <td>{{ d.document.boundAt || '—' }}</td>
              </tr>
              <tr>
                <th>联网校对时间</th>
                <td>{{ d.document.networkVerifiedAt || '—' }}</td>
                <th>导入截止</th>
                <td>{{ d.document.importDeadline || '不限' }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>

      <template v-if="d.trial">
        <h3 class="lic-h3">本机试用</h3>
        <div class="table-wrap">
          <table class="grid">
            <tbody>
              <tr>
                <th>起点</th>
                <td>{{ fmtDateTime(d.trial.issuedAt) }}</td>
                <th>到期日</th>
                <td>{{ d.trial.expiresOn }}</td>
              </tr>
              <tr>
                <th>天数</th>
                <td>{{ d.trial.days }}</td>
                <th>起点来源</th>
                <td>
                  {{ d.trial.anchorSource === 'ntp' ? '联网校时' : '本机时钟（当时未能联网校时）' }}
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <p class="muted small" style="margin-bottom: 0">
          本机试用只是<strong>本机的策略状态</strong>（不带签名），不是厂商签发的凭证；
          移除授权不会重置它，因此不会出现"删掉授权又白拿 30 天"。
        </p>
      </template>
    </template>
  </section>
</template>

<style scoped>
.lic-card {
  display: flex;
  gap: 18px;
  justify-content: space-between;
  flex-wrap: wrap;
  border: 1px solid var(--border);
  border-left-width: 3px;
  border-radius: var(--radius);
  background: var(--panel-soft);
  padding: 12px 14px;
  margin-bottom: 14px;
}

.lic-card.ok {
  border-left-color: var(--ok);
}

.lic-card.warn {
  border-left-color: var(--warn);
}

.lic-card.err {
  border-left-color: var(--err);
}

.lic-main {
  min-width: 260px;
  flex: 1;
}

.lic-title {
  font-size: 15px;
  margin-right: 8px;
}

.lic-info {
  margin: 6px 0 0;
  font-size: 13px;
}

.lic-error {
  margin: 6px 0 0;
  font-size: 13px;
  color: var(--err);
}

.lic-meta {
  display: flex;
  gap: 22px;
  margin: 0;
}

.lic-meta dt {
  color: var(--muted);
  font-size: 12px;
}

.lic-meta dd {
  margin: 2px 0 0;
  font-size: 13px;
}

.lic-h3 {
  font-size: 13px;
  color: var(--muted);
  margin: 16px 0 6px;
}

.lic-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.lic-code {
  font-family: var(--mono);
  font-size: 14px;
  letter-spacing: 0.4px;
  background: var(--accent-soft);
  border: 1px solid #bfdbfe;
  border-radius: 6px;
  padding: 4px 10px;
  user-select: all;
}

.lic-pre {
  margin: 8px 0 0;
  padding: 10px 12px;
  background: var(--panel-soft);
  border: 1px solid var(--border);
  border-radius: var(--radius);
  font-family: var(--mono);
  font-size: 12px;
  white-space: pre-wrap;
  word-break: break-all;
  max-height: 220px;
  overflow: auto;
}

.lic-drop {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  border: 1px dashed var(--border-strong);
  border-radius: var(--radius);
  padding: 14px;
  background: var(--panel-soft);
  transition: border-color 0.12s, background 0.12s;
}

.lic-drop.active {
  border-color: var(--accent);
  background: var(--accent-soft);
}

.lic-textarea {
  width: 100%;
  margin-top: 10px;
  font-family: var(--mono);
  font-size: 12px;
  resize: vertical;
}

.row-actions {
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
  margin-top: 10px;
}
</style>
