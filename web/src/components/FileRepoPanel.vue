<script setup lang="ts">
/**
 * 文件仓库（M4.4）。
 *
 * 这一页回答三件事：
 *   1. **仓库里有什么？**（普通文件 / 客户机回传 / 安装包，按类别筛选）
 *   2. **怎么放进去？**（拖拽或选择文件 → **分块可续传**上传，>2GB 也行）
 *   3. **怎么拿出去？**（下载直链；或交给「分发」面板推给客户机）
 *
 * ⚠️ 三条"看起来像 bug 其实是有意为之"的语义，UI 上都写清楚了：
 *   · **上传是分块的**：一个几 GB 的文件会一块一块地发，进度条按**服务端确认的字节**走，
 *     所以断网重传不会从头开始（进度不会归零）；
 *   · **摘要由服务端算**：客户端不报 SHA256 —— 浏览器算 2GB 的摘要要把整个文件读进内存，
 *     而服务端反正会自己算一遍当权威值（见 `FilesController.CompleteUpload`）；
 *   · **删除只删仓库**：已经分发到客户机上的副本不受影响（它们在人家的磁盘上）。
 *
 * 权限：读要 `CanOperate`（能看仓库）；**上传要 `CanOperate` 之外的 `FileIngest`**
 * （Operator 也没有 —— 能往仓库写就等于能把任意内容推给所有客户机，见 `Policies.FileIngest`）；
 * 删除只给 Admin。前端按角色隐藏入口只是"别让人点了才 403"，闸门在服务端。
 */
import { computed, ref } from 'vue'
import { api, uploadFileChunked } from '../api'
import type { CurrentUser, FileRepoItemDto, FileRepoListDto } from '../types'
import { FILE_KINDS, fileKindLabel } from '../types'
import { copyText, fmtBytes, fmtDateTime } from '../format'

const props = defineProps<{
  files: FileRepoListDto | null
  loading: boolean
  user: CurrentUser | null
}>()

const emit = defineEmits<{
  (e: 'notice', kind: 'ok' | 'err' | 'warn', text: string): void
  (e: 'reload'): void
  (e: 'distribute', file: FileRepoItemDto): void
}>()

/**
 * 能否上传。
 *
 * ⚠️ 这里**不能**只看登录状态：`POST /api/files/uploads` 是 `FileIngest`，
 * **Operator 也被挡在外面**。给 Operator 显示一个"点了就 403"的上传框，
 * 比干脆不显示更糟 —— 他会以为是自己网络出问题了。
 */
const canUpload = computed(() => props.user?.role === 'Admin')

const isAdmin = computed(() => props.user?.role === 'Admin')

// ═══════════════════════════════════════════════════════════════
// 筛选
// ═══════════════════════════════════════════════════════════════

/** 类别筛选（本地过滤；服务端也支持 `?kind=`，但全量只有一份数据，本地筛选不必往返）。 */
const kindFilter = ref<'all' | 'normal' | 'installer' | 'collected'>('all')

const shown = computed(() => {
  const items = props.files?.items ?? []
  if (kindFilter.value === 'all') return items
  return items.filter((x) => x.kind === kindFilter.value)
})

/** 当前筛选下的累计占用 —— 不是全库占用（筛选时看全库数字会让人对不上账）。 */
const shownBytes = computed(() => shown.value.reduce((sum, x) => sum + (x.size || 0), 0))

const collectedCount = computed(
  () => (props.files?.items ?? []).filter((x) => x.kind === FILE_KINDS.collected).length,
)

function kindClass(kind: string): string {
  switch (kind) {
    case FILE_KINDS.collected:
      return 'recv'
    case FILE_KINDS.installer:
      return 'warn'
    default:
      return ''
  }
}

/** 摘要只显示前 12 位 —— 完整 64 位在列表里没有阅读价值。 */
function shortSha(sha: string): string {
  if (!sha) return '—'
  return sha.length > 12 ? `${sha.slice(0, 12)}…` : sha
}

async function copySha(sha: string) {
  try {
    await copyText(sha)
    emit('notice', 'ok', '摘要已复制。')
  } catch (e) {
    emit('notice', 'err', e instanceof Error ? e.message : '复制失败，请手动选中复制。')
  }
}

// ═══════════════════════════════════════════════════════════════
// 上传队列
// ═══════════════════════════════════════════════════════════════

/**
 * 上传任务（一个文件一条）。
 *
 * `uploaded` 是**服务端已确认的字节数**，不是"浏览器已发出"的字节数 ——
 * 分块协议的意义就在这儿：只有服务端记下了，这个数才会前进。
 * 因此在同一个分块内部，进度靠 XHR 的 `upload.onprogress` 平滑推进；
 * 一旦服务端确认了新的偏移，就跳回权威值。
 */
interface UploadJob {
  id: number
  file: File
  uploaded: number
  total: number
  state: 'waiting' | 'uploading' | 'done' | 'error' | 'canceled'
  message: string
}

const jobs = ref<UploadJob[]>([])
/** 同一时刻只跑一个 —— 并发上传会抢满上行带宽，也会让进度条互相干扰。 */
const pumping = ref(false)
let jobSeq = 0
let activeController: AbortController | null = null

const activeJob = computed(
  () => jobs.value.find((j) => j.state === 'uploading') ?? jobs.value.find((j) => j.state === 'waiting') ?? null,
)

const pendingCount = computed(() => jobs.value.filter((j) => j.state === 'waiting').length)

const filePicker = ref<HTMLInputElement | null>(null)
const dragOver = ref(false)

function pickFiles() {
  filePicker.value?.click()
}

function onPicked(event: Event) {
  const input = event.target as HTMLInputElement
  if (input.files && input.files.length > 0) enqueue(input.files)

  // 清空，否则"再选同一个文件"不会触发 change 事件。
  input.value = ''
}

function onDrop(event: DragEvent) {
  dragOver.value = false
  const dropped = event.dataTransfer?.files
  if (!dropped || dropped.length === 0) return
  enqueue(dropped)
}

function enqueue(files: FileList | File[]) {
  if (!canUpload.value) {
    emit('notice', 'warn', '当前账号没有上传权限（需要管理员）。')
    return
  }

  for (const file of Array.from(files)) {
    jobs.value.push({
      id: ++jobSeq,
      file,
      uploaded: 0,
      total: file.size,
      state: 'waiting',
      message: '',
    })
  }

  void pump()
}

/** 顺序消费队列（一次一个）。 */
async function pump() {
  if (pumping.value) return

  const next = jobs.value.find((j) => j.state === 'waiting')
  if (!next) return

  pumping.value = true
  next.state = 'uploading'
  next.message = ''

  const controller = new AbortController()
  activeController = controller

  try {
    await uploadFileChunked(next.file, {
      kind: FILE_KINDS.normal,
      signal: controller.signal,
      onProgress: (p) => {
        next.uploaded = p.uploaded
        next.total = p.total
      },
    })

    next.state = 'done'
    next.uploaded = next.total
    emit('notice', 'ok', `已上传：${next.file.name}`)
    emit('reload')
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') {
      next.state = 'canceled'
      next.message = '已取消'
    } else {
      next.state = 'error'
      next.message = e instanceof Error ? e.message : '上传失败'
      emit('notice', 'err', `上传「${next.file.name}」失败：${next.message}`)
    }
  } finally {
    pumping.value = false
    activeController = null
    void pump()
  }
}

/** 取消正在跑的那一个（队列里等着的由「清除未开始」清掉）。 */
function cancelActive() {
  const job = jobs.value.find((j) => j.state === 'uploading')
  if (!job) return

  activeController?.abort()
}

/** 清掉所有"已结束"的记录（完成的 / 失败的 / 取消的），保留还在跑的。 */
function clearFinished() {
  jobs.value = jobs.value.filter((j) => j.state === 'waiting' || j.state === 'uploading')
}

/** 丢掉所有"还没开始"的（取消排队）。 */
function clearQueued() {
  jobs.value = jobs.value.filter((j) => j.state !== 'waiting')
}

// ═══════════════════════════════════════════════════════════════
// 下载 / 删除
// ═══════════════════════════════════════════════════════════════

/**
 * 下载（走浏览器原生下载）。
 *
 * 用隐藏 `<a>` 而不是 `window.open`：后者在部分浏览器里会被弹出拦截器挡下，
 * 而用户看到的是"点了没反应"。服务端已带 `Content-Disposition`，所以不会跳走页面。
 */
function download(item: FileRepoItemDto) {
  const a = document.createElement('a')
  a.href = api.fileDownloadUrl(item.id)
  a.rel = 'noopener'
  document.body.appendChild(a)
  a.click()
  a.remove()
}

/** 两段式删除确认（与用户管理页 / 客户端页同一套交互）。 */
const confirmDeleteId = ref<number | null>(null)
const deletingId = ref<number | null>(null)
let disarmTimer: number | null = null

function armDelete(item: FileRepoItemDto) {
  if (confirmDeleteId.value === item.id) {
    confirmDeleteId.value = null

    if (disarmTimer !== null) {
      window.clearTimeout(disarmTimer)
      disarmTimer = null
    }

    void doDelete(item)
    return
  }

  confirmDeleteId.value = item.id

  if (disarmTimer !== null) window.clearTimeout(disarmTimer)

  disarmTimer = window.setTimeout(() => {
    confirmDeleteId.value = null
    disarmTimer = null
  }, 4000)
}

async function doDelete(item: FileRepoItemDto) {
  deletingId.value = item.id

  try {
    const result = await api.deleteFile(item.id)
    emit('notice', result.ok ? 'ok' : 'warn', result.message)
    emit('reload')
  } catch (e) {
    emit('notice', 'err', e instanceof Error ? e.message : '删除失败')
  } finally {
    deletingId.value = null
  }
}

function jobBarWidth(job: UploadJob): string {
  if (!job.total) return job.state === 'done' ? '100%' : '0%'
  return `${Math.min(100, Math.max(0, (job.uploaded / job.total) * 100))}%`
}
</script>

<template>
  <section class="panel">
    <div class="panel-head">
      <h2>
        文件仓库
        <span class="muted small">
          （{{ props.files?.items.length ?? 0 }} 个文件 · 占用
          {{ fmtBytes(props.files?.totalBytes ?? 0) }}
          <template v-if="kindFilter !== 'all'">· 当前筛选 {{ shown.length }} 个 / {{ fmtBytes(shownBytes) }}</template>
          <template v-if="collectedCount > 0">· 客户机回传 {{ collectedCount }} 个</template>）
        </span>
      </h2>

      <div class="panel-actions">
        <label class="inline">
          类别
          <select v-model="kindFilter" class="kind">
            <option value="all">全部</option>
            <option value="normal">普通文件</option>
            <option value="collected">客户机回传</option>
            <option value="installer">安装包</option>
          </select>
        </label>
        <button class="sm" :disabled="props.loading" @click="emit('reload')">
          {{ props.loading ? '刷新中…' : '刷新' }}
        </button>
      </div>
    </div>

    <p class="muted small" style="margin: -4px 0 12px">
      上传是<strong>分块可续传</strong>的：几 GB 的文件也能传，中途断网重连会<strong>从断点接着传</strong>。
      文件摘要（SHA256）由服务端在收完后自己算一遍，客户端不报 —— 那是"文件有没有传坏"的判据。
    </p>

    <!-- ═══ 上传 ═══ -->
    <div
      v-if="canUpload"
      class="drop"
      :class="{ over: dragOver }"
      @dragover.prevent="dragOver = true"
      @dragleave.prevent="dragOver = false"
      @drop.prevent="onDrop"
      @click="pickFiles"
    >
      <strong>把文件拖到这里，或点击选择</strong>
      <span class="muted small">可多选；大文件会排队逐个上传（同一时刻只传一个）</span>
      <input ref="filePicker" class="hidden-file" type="file" multiple @change="onPicked" />
    </div>

    <p v-else class="muted small" style="margin: 0 0 12px">
      当前账号（{{ props.user?.userName || '未登录' }}）<strong>没有上传权限</strong>。
      往仓库写文件等于能把任意内容推给所有客户机，所以这个动作只给管理员。
    </p>

    <!-- 上传队列 -->
    <div v-if="jobs.length > 0" class="jobs">
      <div class="jobs-head">
        <span class="muted small">
          上传队列：{{ jobs.length }} 项
          <template v-if="pendingCount > 0">· 等待 {{ pendingCount }}</template>
        </span>
        <div class="panel-actions">
          <button v-if="activeJob" class="sm danger" @click="cancelActive">取消当前</button>
          <button v-if="pendingCount > 0" class="sm" @click="clearQueued">清空等待</button>
          <button class="sm" @click="clearFinished">清除已结束</button>
        </div>
      </div>

      <div v-for="job in jobs" :key="job.id" class="job-row">
        <span class="job-name" :title="job.file.name">{{ job.file.name }}</span>

        <span class="job-bar">
          <i
            :class="{ ok: job.state === 'done', err: job.state === 'error' }"
            :style="{ width: jobBarWidth(job) }"
          />
        </span>

        <span class="job-text mono">
          {{ fmtBytes(job.uploaded) }} / {{ fmtBytes(job.total) }}
        </span>

        <span class="muted small job-state">
          <template v-if="job.state === 'waiting'">等待中</template>
          <template v-else-if="job.state === 'uploading'">上传中…</template>
          <template v-else-if="job.state === 'done'">已完成</template>
          <template v-else-if="job.state === 'canceled'">已取消</template>
          <template v-else class="err">{{ job.message }}</template>
        </span>
      </div>
    </div>

    <!-- ═══ 列表 ═══ -->
    <div v-if="!props.files || props.files.items.length === 0" class="empty">
      <strong>仓库是空的</strong>
      把文件拖进上面的区域上传；客户机回传的文件也会出现在这里。
    </div>

    <div v-else-if="shown.length === 0" class="empty">
      <strong>这个类别下没有文件</strong>
      换个类别，或选「全部」。
    </div>

    <div v-else class="table-wrap tall">
      <table class="grid">
        <thead>
          <tr>
            <th style="width: 72px">类别</th>
            <th>文件名</th>
            <th style="width: 86px">大小</th>
            <th style="width: 108px">摘要</th>
            <th style="width: 150px">上传者</th>
            <th style="width: 142px">上传时间</th>
            <th style="width: 190px">操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in shown" :key="item.id">
            <td>
              <span class="badge" :class="kindClass(item.kind)">{{ fileKindLabel(item.kind) }}</span>
            </td>
            <td>
              <div class="strong ellipsis" :title="item.name">{{ item.name }}</div>
              <div v-if="item.note" class="muted small ellipsis" :title="item.note">{{ item.note }}</div>
            </td>
            <td class="mono nowrap">{{ fmtBytes(item.size) }}</td>
            <td class="mono">
              <button class="sha" :title="item.sha256 || '无摘要'" @click="copySha(item.sha256)">
                {{ shortSha(item.sha256) }}
              </button>
            </td>
            <td class="muted small">
              <div class="ellipsis" :title="item.uploadedBy ?? ''">{{ item.uploadedBy || '—' }}</div>
              <div v-if="item.originAgentId !== null" class="muted small">客户机 #{{ item.originAgentId }}</div>
            </td>
            <td class="muted small mono">{{ fmtDateTime(item.uploadedAt) }}</td>
            <td>
              <div class="row-actions">
                <button class="sm" @click="download(item)">下载</button>

                <button
                  v-if="isAdmin"
                  class="sm"
                  :title="'把这份文件推给客户机（在下方「分发任务」里选目标）'"
                  @click="emit('distribute', item)"
                >
                  分发
                </button>

                <button
                  v-if="isAdmin"
                  class="sm danger"
                  :disabled="deletingId === item.id"
                  :title="'只从仓库删除；已经分发到客户机上的副本不受影响'"
                  @click="armDelete(item)"
                >
                  {{ confirmDeleteId === item.id ? '确认删除？' : '删除' }}
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
.kind {
  width: 130px;
  padding: 3px 6px;
  font-size: 12px;
}

.hidden-file {
  display: none;
}

.drop {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 4px;
  padding: 20px 16px;
  margin-bottom: 12px;
  border: 1px dashed var(--border-strong);
  border-radius: var(--radius);
  background: var(--panel-soft);
  cursor: pointer;
  text-align: center;
  transition: border-color 0.12s, background 0.12s;
}

.drop:hover,
.drop.over {
  border-color: var(--accent);
  background: var(--accent-soft);
}

/* ── 上传队列 ── */
.jobs {
  border: 1px solid var(--border);
  border-radius: var(--radius);
  padding: 8px 10px;
  margin-bottom: 12px;
  background: var(--panel-soft);
}

.jobs-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
  margin-bottom: 6px;
}

.job-row {
  display: grid;
  grid-template-columns: minmax(140px, 1.4fr) minmax(90px, 1fr) 132px 128px;
  gap: 8px;
  align-items: center;
  font-size: 12px;
  padding: 3px 0;
}

.job-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.job-bar {
  height: 6px;
  border-radius: 3px;
  background: #fff;
  border: 1px solid var(--border);
  overflow: hidden;
}

.job-bar i {
  display: block;
  height: 100%;
  border-radius: 3px;
  background: var(--accent);
  transition: width 0.2s ease;
}

.job-bar i.ok {
  background: var(--ok);
}

.job-bar i.err {
  background: var(--err);
}

.job-text {
  font-size: 11px;
  text-align: right;
  white-space: nowrap;
}

.job-state {
  text-align: right;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.strong {
  font-weight: 500;
}

.ellipsis {
  max-width: 380px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.nowrap {
  white-space: nowrap;
}

.row-actions {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.inline {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  margin: 0;
  font-size: 12px;
  color: var(--muted);
  white-space: nowrap;
}

/* 摘要：看着像文本，点一下复制（不占地方，也不和"操作"列的按钮抢注意力）。 */
.sha {
  padding: 1px 4px;
  font-family: var(--mono);
  font-size: 11px;
  color: var(--muted);
  background: none;
  border: 1px dashed transparent;
}

.sha:hover:not(:disabled) {
  border-color: var(--border-strong);
  color: var(--accent);
}
</style>
