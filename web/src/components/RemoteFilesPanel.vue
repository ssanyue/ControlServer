<script setup lang="ts">
/**
 * 远控面板的「文件」页：**两侧资源管理器 + 多文件双向传输**。
 *
 * <pre>
 *   客户机（Agent，经中心浏览）          本机（浏览器所在电脑，经 File System Access API）
 *   ┌──────────────────────┐           ┌──────────────────────┐
 *   │ D:\logs              │   ←取回   │ 选定的根目录          │
 *   │  app-1.log   82 MB ☑ │  ────→   │  report.zip   12 MB ☑ │
 *   └──────────────────────┘   ←发送   └──────────────────────┘
 * </pre>
 *
 * <h3>三条必须知道的前提（写在代码里，因为它们在界面上看不出）</h3>
 *
 * <ol>
 *   <li><b>本机侧没有"此电脑"</b>：浏览器不允许枚举盘符，只能由用户<b>点选一个根目录</b>
 *       （`showDirectoryPicker`），之后在该根之内逐级浏览。所以"本机资源管理器"是
 *       "以你选的那个目录为根"的资源管理器 —— 这是安全边界，不是没做。</li>
 *   <li><b>整个页面必须是安全上下文</b>（https 或 localhost）：`showDirectoryPicker`
 *       只在那种上下文里存在。所以非 https 时本页<b>整体不提供文件功能</b>，
 *       只给一句"改用 https://…:8443"的说明 —— 中心默认就监听 8443。</li>
 *   <li><b>两段式**而不是**点对点</b>：发文件 = 先上传到中心仓库，再让客户机去拉；
 *       取文件 = 客户机先传到中心仓库，再下载到本机。所以进度必须分两段显示
 *       （"上传到中心"与"机器接收中"是两件不同的事，合成一个假进度会让人以为卡住了）。</li>
 * </ol>
 *
 * <h3>规则都在 `fileBrowser.ts` 里</h3>
 * 路径解析、阶段推进、失败重试从哪一步开始、并发上限 —— 都不在这个组件里。
 * 组件只做"把状态画出来 + 把用户动作变成 API 调用"。
 *
 * ⚠️ <b>已知限制</b>：收起参数面板（`sheetOpen=false`）会卸载本组件，正在上传的那一段
 * 会被中断（客户机已经在拉的那一段不受影响，因为它在 Agent 侧）。所以页面上有一句常驻提示。
 */
import { computed, onMounted, onUnmounted, ref, shallowRef, watch } from 'vue'
import { api, ApiError, uploadFileChunked } from '../api'
import type { AgentBrowseEntryDto, DistributionItemDto } from '../types'
import { fmtBytes } from '../format'
import { RateTracker } from '../transferRate'
import {
  canRetry,
  failJob,
  isTerminal,
  joinPath,
  leafOf,
  phaseLabel,
  pickRunnable,
  retryPhase,
  type TransferDirection,
  type TransferJob,
  type TransferPhase,
} from '../fileBrowser'

const props = defineProps<{
  /** 这台客户机的 id（`null` = 还没选机器）。 */
  agentId: number | null
  agentName: string
  /** 客户机是否在线（离线时浏览必然失败，提前说清楚）。 */
  online: boolean
  /**
   * 这一页现在是不是"正被看着"。
   *
   * 🔥 用途只有一个：**别在用户没打开这一页时就去问被控机要目录**。
   * 一次 browse 是"网页 → 中心 → 长连接 → 客户机 → 列目录 → 原路返回"，
   * 打开十个窗口就是十次；而其中九个没人看文件页。
   */
  active: boolean
}>()

// ═══════════════════════════════════════════════════════════════════════════
// File System Access API 的最小类型（lib.dom 里没有 entries() / showDirectoryPicker）
// ═══════════════════════════════════════════════════════════════════════════

interface FsFileHandle {
  readonly kind: 'file'
  readonly name: string
  getFile(): Promise<File>
  createWritable(options?: { keepExistingData?: boolean }): Promise<FsWritable>
}

/** `FileSystemWritableFileStream` 的最小接口（自己声明，避免依赖 lib.dom 的版本差异）。 */
interface FsWritable {
  write(data: Uint8Array): Promise<void>
  close(): Promise<void>
  abort(): Promise<void>
}

interface FsDirectoryHandle {
  readonly kind: 'directory'
  readonly name: string
  entries(): AsyncIterableIterator<[string, FsFileHandle | FsDirectoryHandle]>
  getFileHandle(name: string, options?: { create?: boolean }): Promise<FsFileHandle>
  queryPermission?: (options?: { mode: 'read' | 'readwrite' }) => Promise<PermissionState>
}

type DirectoryPicker = (options?: { mode?: 'read' | 'readwrite' }) => Promise<FsDirectoryHandle>

interface LocalEntry {
  name: string
  kind: 'file' | 'directory'
  size: number
  handle: FsFileHandle | FsDirectoryHandle
}

// ═══════════════════════════════════════════════════════════════════════════
// 环境
// ═══════════════════════════════════════════════════════════════════════════

/** 安全上下文判定。**每次进入页面只看一次** —— 它在一个页面生命周期内不会变。 */
const secure = ref(typeof window !== 'undefined' ? window.isSecureContext : false)

const pickerAvailable = computed(() => {
  if (typeof window === 'undefined') return false
  return typeof (window as unknown as { showDirectoryPicker?: unknown }).showDirectoryPicker === 'function'
})

function directoryPicker(): DirectoryPicker | null {
  const host = window as unknown as { showDirectoryPicker?: DirectoryPicker }
  return host.showDirectoryPicker ?? null
}

// ═══════════════════════════════════════════════════════════════════════════
// 客户机一侧
// ═══════════════════════════════════════════════════════════════════════════

/** 当前目录；`null` = 盘符列表（"此电脑"）。 */
const clientPath = ref<string | null>(null)
const clientParent = ref<string | null>(null)
const clientEntries = ref<AgentBrowseEntryDto[]>([])
const clientLoading = ref(false)
const clientError = ref<string | null>(null)
/** 选中的文件路径（多选）。目录不参与选择 —— 点它是"进去"。 */
const clientSelected = ref<string[]>([])

/** 能不能把文件发到"现在这个位置"：必须是进了某个目录，而不是停在盘符列表。 */
const canSendHere = computed(() => clientPath.value !== null && props.online && props.agentId !== null)

async function loadClient(path: string | null) {
  if (props.agentId === null) {
    clientError.value = '先选一台机器。'
    return
  }

  clientLoading.value = true
  clientError.value = null

  try {
    const result = await api.browseAgentDirectory(props.agentId, path)

    // ⚠️ 失败时**不动** `clientPath`：进一个没权限的目录失败后，应当停在原地，
    //    而不是把用户弹回"此电脑"（那会让他以为刚才那一层也不见了）。
    if (!result.ok) {
      clientError.value = result.message ?? '这台机器没有说明原因。'
      clientEntries.value = []
      return
    }

    clientPath.value = result.requestedPath
    clientParent.value = result.parentPath
    clientEntries.value = result.entries ?? []
    clientSelected.value = []
  } catch (error) {
    clientError.value = messageOf(error)
  } finally {
    clientLoading.value = false
  }
}

function enterClientDirectory(entry: AgentBrowseEntryDto) {
  if (!entry.isDirectory) return
  void loadClient(entry.path)
}

function upClient() {
  if (clientPath.value === null) return

  const parent = clientParent.value

  // 已经是盘根（`C:\`）⇒ 回"此电脑"（传 null 让它列盘符）。
  void loadClient(parent === null ? null : parent)
}

function toggleClient(path: string) {
  const index = clientSelected.value.indexOf(path)

  if (index >= 0) clientSelected.value.splice(index, 1)
  else clientSelected.value.push(path)
}

// ═══════════════════════════════════════════════════════════════════════════
// 本机一侧
// ═══════════════════════════════════════════════════════════════════════════

const localRoot = shallowRef<FsDirectoryHandle | null>(null)
const localDir = shallowRef<FsDirectoryHandle | null>(null)
/** 从根到当前目录的栈（第 0 项是用户选的根）。用 `shallowRef`：handle 不能被 Vue 代理。 */
const localStack = shallowRef<{ name: string; handle: FsDirectoryHandle }[]>([])
const localEntries = ref<LocalEntry[]>([])
const localLoading = ref(false)
const localError = ref<string | null>(null)
const localSelected = ref<string[]>([])

const localBreadcrumb = computed(() => localStack.value.map((x) => x.name).join(' / '))
const canFetchHere = computed(() => localDir.value !== null && clientSelected.value.length > 0 && props.online)

async function pickLocalRoot() {
  const picker = directoryPicker()

  if (!picker) {
    localError.value = '这个浏览器没有目录选择能力（需要 Chrome / Edge，且页面在 https 或 localhost 下）。'
    return
  }

  localError.value = null

  try {
    // `readwrite` 在**选择的那一刻**就把写权限要到手 —— 等到"下载"时再要，
    // 就会在传输已经跑起来之后弹一个权限框，而那时用户以为一切已经开始了。
    const handle = await picker({ mode: 'readwrite' })

    localRoot.value = handle
    localDir.value = handle
    localStack.value = [{ name: handle.name, handle }]

    await loadLocal()
  } catch (error) {
    // 用户点了取消 —— 这不是错误，别在界面上留一行红字。
    if (isAbort(error)) return
    localError.value = messageOf(error)
  }
}

async function loadLocal() {
  const dir = localDir.value

  if (!dir) return

  localLoading.value = true
  localError.value = null

  try {
    const entries: LocalEntry[] = []

    for await (const [name, handle] of dir.entries()) {
      if (handle.kind === 'directory') {
        entries.push({ name, kind: 'directory', size: 0, handle })
        continue
      }

      // 文件大小要读一次 metadata（`getFile()` 不加载内容，只拿句柄信息）。
      const file = await handle.getFile()
      entries.push({ name, kind: 'file', size: file.size, handle })
    }

    entries.sort((a, b) => {
      if (a.kind !== b.kind) return a.kind === 'directory' ? -1 : 1
      return a.name.localeCompare(b.name, 'zh-CN')
    })

    localEntries.value = entries
    localSelected.value = []
  } catch (error) {
    localError.value = messageOf(error)
    localEntries.value = []
  } finally {
    localLoading.value = false
  }
}

async function enterLocalDirectory(entry: LocalEntry) {
  if (entry.kind !== 'directory') return

  const handle = entry.handle as FsDirectoryHandle

  localDir.value = handle
  localStack.value = [...localStack.value, { name: entry.name, handle }]

  await loadLocal()
}

async function upLocal() {
  if (localStack.value.length <= 1) return

  const stack = localStack.value.slice(0, -1)

  localStack.value = stack
  localDir.value = stack[stack.length - 1].handle

  await loadLocal()
}

function toggleLocal(name: string) {
  const index = localSelected.value.indexOf(name)

  if (index >= 0) localSelected.value.splice(index, 1)
  else localSelected.value.push(name)
}

// ═══════════════════════════════════════════════════════════════════════════
// 传输队列
// ═══════════════════════════════════════════════════════════════════════════

const jobs = ref<TransferJob[]>([])
const rates = new RateTracker()

/** 每个 job 一个数字 id（`RateTracker` 按 number 记窗口）。 */
const rateIds = new Map<string, number>()
let rateSeq = 0

/** 执行器：`pump()` 取出可开工的 job 后调它。取出即删，保证同一条只跑一次。 */
const starters = new Map<string, () => Promise<void>>()
const controllers = new Map<string, AbortController>()
const inFlight = new Set<string>()

let keySeq = 0
let disposed = false

function addJob(direction: TransferDirection, fileName: string, size: number): TransferJob {
  const job: TransferJob = {
    key: `f${++keySeq}`,
    direction,
    fileName,
    size,
    phase: 'pending',
    doneBytes: 0,
    error: null,
    resumeFrom: null,
    taskId: null,
    fileId: null,
    resultFileId: null,
  }

  jobs.value.push(job)
  rateIds.set(job.key, ++rateSeq)

  return job
}

function jobOf(key: string): TransferJob | undefined {
  return jobs.value.find((j) => j.key === key)
}

function patchJob(key: string, patch: Partial<TransferJob>) {
  const job = jobOf(key)
  if (job) Object.assign(job, patch)
}

function setPhase(key: string, phase: TransferPhase) {
  patchJob(key, { phase, error: null })
}

function fail(key: string, message: string) {
  const job = jobOf(key)
  if (job) Object.assign(job, failJob(job, message))
}

function finish(key: string) {
  patchJob(key, { phase: 'done', doneBytes: jobOf(key)?.size ?? 0, error: null })
}

function noteProgress(key: string, bytes: number) {
  const job = jobOf(key)
  if (!job) return

  job.doneBytes = bytes

  const rateId = rateIds.get(key)
  if (rateId !== undefined) rates.observe(rateId, Date.now(), bytes, null, null)
}

function rateOf(key: string): number | null {
  const rateId = rateIds.get(key)
  return rateId === undefined ? null : rates.rate(rateId)
}

/** 调度：按并发上限取出待开工的 job 并启动。 */
function pump() {
  for (const job of pickRunnable(jobs.value)) {
    if (inFlight.has(job.key)) continue

    const starter = starters.get(job.key)
    if (!starter) continue

    starters.delete(job.key)
    inFlight.add(job.key)

    // 🔥 不要裸 `void starter()`：那个写法会连异常一起吞（见项目铁律 10）。
    //    执行器内部自己 try/catch/finally，这里只是不等它。
    void starter()
  }
}

// ── 发到客户机 ──

function sendSelectedToAgent() {
  if (!canSendHere.value) return

  const picked = localEntries.value.filter(
    (e) => e.kind === 'file' && localSelected.value.includes(e.name),
  )

  if (picked.length === 0) return

  const targetDir = clientPath.value as string

  // 同名覆盖：客户机落地是 `File.Move(overwrite: true)`，覆盖就是真覆盖。
  const existing = new Set(
    clientEntries.value.filter((e) => !e.isDirectory).map((e) => e.name.toLowerCase()),
  )
  const conflicts = picked.filter((e) => existing.has(e.name.toLowerCase()))

  if (
    conflicts.length > 0 &&
    !window.confirm(
      `目标目录里已有同名文件，继续会直接覆盖：\n\n${conflicts.map((c) => c.name).join('\n')}\n\n继续？`,
    )
  ) {
    return
  }

  for (const entry of picked) {
    const job = addJob('to-agent', entry.name, entry.size)

    starters.set(job.key, () => runToAgent(job.key, entry, targetDir))
  }

  localSelected.value = []
  pump()
}

async function runToAgent(key: string, entry: LocalEntry, targetDir: string) {
  const job = jobOf(key)
  if (!job) return

  const controller = new AbortController()
  controllers.set(key, controller)

  // 🔥 重试时**必须能只重建单**：`publishing` 失败的那一刻，文件已经在中心仓库里了。
  //    再传一遍 2 GB 是这一页最贵的一种错误（而且网慢时它看起来像"卡住了"）。
  const resumeFrom = job.resumeFrom
  patchJob(key, { resumeFrom: null })

  try {
    let fileId = job.fileId

    if (resumeFrom === 'publishing' && fileId) {
      // 复用仓库里那一份 —— id 在上一轮上传完成时就记下来了。
    } else {
      setPhase(key, 'uploading')

      const file = await (entry.handle as FsFileHandle).getFile()

      const uploaded = await uploadFileChunked(file, {
        signal: controller.signal,
        onProgress: (progress) => noteProgress(key, progress.uploaded),
      })

      fileId = uploaded.id
      patchJob(key, { fileId })
    }

    setPhase(key, 'publishing')

    const task = await api.createPushDistribution({
      fileId: fileId as number,
      targets: [{ agentId: props.agentId as number, targetDir }],
    })

    patchJob(key, { taskId: task.id })

    setPhase(key, 'delivering')

    const item = await waitForItem(task.id)

    if (item.state !== 'completed') {
      throw new Error(item.error || `这台机器没有完成接收（${item.state}）。`)
    }

    finish(key)

    // 让新文件出现在列表里（不刷新的话用户会以为"没传过去"）。
    await loadClient(clientPath.value)
  } catch (error) {
    fail(key, describeFailure(error))
  } finally {
    controllers.delete(key)
    inFlight.delete(key)
    pump()
  }
}

// ── 取回本机 ──

function fetchSelectedToLocal() {
  if (!canFetchHere.value) return

  const dir = localDir.value
  if (!dir) return

  const picked = clientEntries.value.filter(
    (e) => !e.isDirectory && clientSelected.value.includes(e.path),
  )

  if (picked.length === 0) return

  for (const entry of picked) {
    const job = addJob('to-local', entry.name, entry.size)

    starters.set(job.key, () => runToLocal(job.key, entry, dir))
  }

  clientSelected.value = []
  pump()
}

async function runToLocal(key: string, entry: AgentBrowseEntryDto, dir: FsDirectoryHandle) {
  const job = jobOf(key)
  if (!job) return

  const controller = new AbortController()
  controllers.set(key, controller)

  // 与发方向同理：`downloading` 失败时仓库里那份还在，重试只重新下载，
  // 不该让客户机再上传一遍。
  const resumeFrom = job.resumeFrom
  patchJob(key, { resumeFrom: null })

  try {
    let resultFileId = job.resultFileId

    if (resumeFrom === 'downloading' && resultFileId) {
      // 复用仓库里那一份。
    } else {
      setPhase(key, 'collecting')

      const task = await api.createCollectDistribution({
        targetAgentIds: [props.agentId as number],
        sourcePath: entry.path,
      })

      patchJob(key, { taskId: task.id })

      const item = await waitForItem(task.id)

      if (item.state !== 'completed') {
        throw new Error(item.error || `这台机器没有完成上传（${item.state}）。`)
      }

      // 🔥 回传任务的 `fileId` 恒为 0 —— 真正的仓库文件 id 只能由服务端派生后给出。
      //    拿不到 = 那份文件已经被从仓库删了；此时给一句人话，而不是一个点了 404 的按钮。
      if (!item.resultFileId) {
        throw new Error('这台机器传完了，但仓库里找不到那份文件（可能已被清理）。')
      }

      resultFileId = item.resultFileId

      patchJob(key, {
        resultFileId: item.resultFileId,
        size: item.totalBytes > 0 ? item.totalBytes : entry.size,
      })
    }

    setPhase(key, 'downloading')

    await downloadIntoLocal(dir, entry.name, resultFileId as number, (bytes) =>
      noteProgress(key, bytes),
    )

    finish(key)

    await loadLocal()
  } catch (error) {
    fail(key, describeFailure(error))
  } finally {
    controllers.delete(key)
    inFlight.delete(key)
    pump()
  }
}

/**
 * 把仓库里的一个文件**流式**写进本机目录。
 *
 * <p>
 * ⚠️ 刻意不用 `await response.blob()`：那会把整份文件读进内存，2 GB 的日志直接让标签页崩掉。
 * 这里用 `ReadableStream` 逐块写 `FileSystemWritableFileStream`，内存占用只有一个块。
 * </p>
 */
async function downloadIntoLocal(
  dir: FsDirectoryHandle,
  fileName: string,
  fileId: number,
  onProgress: (bytes: number) => void,
) {
  const conflict = localEntries.value.some(
    (e) => e.kind === 'file' && e.name.toLowerCase() === fileName.toLowerCase(),
  )

  if (
    conflict &&
    !window.confirm(`本机当前目录里已有同名文件，继续会覆盖：\n\n${fileName}\n\n继续？`)
  ) {
    throw new Error('已取消（本机已有同名文件）。')
  }

  const response = await fetch(api.fileDownloadUrl(fileId))

  if (!response.ok || !response.body) {
    throw new Error(`从仓库下载失败（HTTP ${response.status}）。`)
  }

  const handle = await dir.getFileHandle(fileName, { create: true })
  const writable = await handle.createWritable()
  const reader = response.body.getReader()

  let written = 0
  let completed = false

  try {
    for (;;) {
      const { done, value } = await reader.read()

      if (done) break

      // `done=false` 时 `value` 理论上一定有；类型上仍是可选，这里显式跳过而不是断言。
      if (!value) continue

      await writable.write(value)
      written += value.byteLength
      onProgress(written)
    }

    completed = true
  } finally {
    // 中途失败必须 abort：close() 会把半截文件落成"正常文件"，
    // 而它的大小看起来完全合法（只是内容是前半段）。
    if (completed) await writable.close()
    else await writable.abort()
  }
}

/**
 * 等这一台机器把活干完。
 *
 * <p>
 * 🔥 <b>为什么轮询而不是订阅 <c>/hubs/events</c></b>：面板可能同时有多条任务、
 * 还可能因为切换会话重连；订阅一旦漏掉一条推送，这一行就永远停在 40%。
 * 轮询把"最终状态"当成唯一真相 —— 代价只是每 1.5 秒一次轻量查询。
 * 判据是**服务端那一行的 state**，不是"等够久了"。
 * </p>
 */
async function waitForItem(taskId: number, timeoutMs = 30 * 60 * 1000): Promise<DistributionItemDto> {
  const startedAt = Date.now()

  for (;;) {
    if (disposed) throw new Error('参数面板已收起，传输中断。')

    const task = await api.distributionDetail(taskId)

    const item =
      task.items.find((x) => x.agentId === props.agentId) ?? task.items[0]

    if (!item) throw new Error('这个任务里没有这台机器的条目。')

    if (isTerminalItem(item.state)) return item

    if (Date.now() - startedAt > timeoutMs) {
      throw new Error('等待这台机器完成超时（30 分钟）。')
    }

    await sleep(1500)
  }
}

function isTerminalItem(state: string): boolean {
  return state === 'completed' || state === 'failed' || state === 'canceled'
}

function retry(key: string) {
  const job = jobOf(key)
  if (!job || job.phase !== 'failed') return

  const from = retryPhase(job)

  // 重试用同一个 `entry`：发方向的文件句柄还在 `localEntries` 里，
  // 收方向只需要源路径 —— 两条都能从原 entry 重新起。
  const localEntry = localEntries.value.find((e) => e.name === job.fileName)
  const clientEntry = clientEntries.value.find((e) => e.name === job.fileName && !e.isDirectory)

  patchJob(key, { phase: 'pending', error: null, resumeFrom: from, doneBytes: 0 })

  if (job.direction === 'to-agent') {
    if (!localEntry || !clientPath.value) {
      fail(key, '本机已找不到这个文件（可能已移动），请重新选择。')
      return
    }

    starters.set(key, () => runToAgent(key, localEntry, clientPath.value as string))
  } else {
    if (!clientEntry) {
      fail(key, '这台机器的目录里已找不到这个文件，请刷新后重选。')
      return
    }

    const dir = localDir.value
    if (!dir) {
      fail(key, '本机目录已失效（可能权限被收回），请重新选择根目录。')
      return
    }

    starters.set(key, () => runToLocal(key, clientEntry, dir))
  }

  pump()
}

function dismiss(key: string) {
  jobs.value = jobs.value.filter((j) => j.key !== key)
}

const activeCount = computed(
  () => jobs.value.filter((j) => !isTerminal(j.phase) && j.phase !== 'pending').length,
)

// ═══════════════════════════════════════════════════════════════════════════
// 生命周期
// ═══════════════════════════════════════════════════════════════════════════

/** 这一页已经问过一次目录了吗（避免每次切回标签都重来一遍）。 */
const loadedOnce = ref(false)

/** 第一次真正被看到时才去要目录。 */
function ensureLoaded() {
  if (loadedOnce.value || props.agentId === null) return

  loadedOnce.value = true
  void loadClient(null)
}

onMounted(ensureLoaded)

watch(
  () => props.active,
  (active) => {
    if (active) ensureLoaded()
  },
)

watch(
  () => props.agentId,
  (id) => {
    // 换了一台机器：上一台的目录、勾选与错误都不再成立。
    clientPath.value = null
    clientParent.value = null
    clientEntries.value = []
    clientSelected.value = []
    clientError.value = null
    loadedOnce.value = false

    // 如果此刻正看着文件页，立刻问新机器要目录；否则等用户切过来。
    if (id !== null && props.active) ensureLoaded()
  },
)

onUnmounted(() => {
  disposed = true

  // 正在上传的那一段由浏览器发起 —— 组件没了就没法继续。明确把它们标成中断，
  // 而不是让它们停在 43% 假装还在跑（`waitForItem` 也会因此退出）。
  for (const controller of controllers.values()) controller.abort()
  controllers.clear()
})

// ═══════════════════════════════════════════════════════════════════════════
// 小工具
// ═══════════════════════════════════════════════════════════════════════════

function messageOf(error: unknown): string {
  if (error instanceof ApiError) return error.message
  if (error instanceof Error) return error.message
  return String(error)
}

function describeFailure(error: unknown): string {
  if (error instanceof DOMException && error.name === 'AbortError') return '已取消。'
  return messageOf(error)
}

function isAbort(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError'
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

/** 阶段中文名（规则在 `fileBrowser.phaseLabel`，这里只做绑定）。 */
function labelOf(job: TransferJob): string {
  return phaseLabel(job.direction, job.phase)
}

function percentOf(job: TransferJob): number {
  if (job.size <= 0) return job.phase === 'done' ? 100 : 0
  return Math.min(100, Math.round((job.doneBytes / job.size) * 100))
}

function rateText(job: TransferJob): string {
  const rate = rateOf(job.key)
  return rate === null ? '' : `${fmtBytes(rate)}/s`
}

/** 本机列表里"哪些被选中"——模板里要反复判断。 */
function isLocalSelected(name: string): boolean {
  return localSelected.value.includes(name)
}

function isClientSelected(path: string): boolean {
  return clientSelected.value.includes(path)
}

/** 面包屑最后一段（列表标题）。 */
const clientTitle = computed(() => {
  if (clientPath.value === null) return '此电脑'
  return leafOf(clientPath.value) || clientPath.value
})

/** 给模板用的 `joinPath`（提示"会落到哪"）。 */
function targetHint(): string {
  if (clientPath.value === null) return '先进入一个目录'
  return joinPath(clientPath.value, '…')
}
</script>

<template>
  <div class="rf">
    <!-- ① 安全上下文：这是硬前提，不是提醒。 -->
    <div v-if="!secure" class="notice warn">
      <strong>文件传输需要 HTTPS 访问</strong>
      <p>
        浏览本机目录用的是浏览器的文件系统接口，它只在<b>安全上下文</b>里存在
        （https 或 localhost）。当前页面是 http://…，所以拿不到你的本机目录。
      </p>
      <p>
        改用 <code>https://&lt;中心地址&gt;:8443</code> 打开本页（中心默认就监听 8443）。
        首次访问需要信任中心的自签证书。
      </p>
    </div>

    <div v-else-if="!pickerAvailable" class="notice warn">
      <strong>这个浏览器没有目录选择能力</strong>
      <p>需要 Chrome / Edge 105 及以上。其他浏览器只能浏览客户机，无法读写本机目录。</p>
    </div>

    <template v-else>
      <p v-if="!agentId" class="notice info small">先连上一台机器，这里才会出现它的文件。</p>

      <template v-else>
        <!-- ② 客户机 -->
        <section class="rf-pane">
          <header class="rf-head">
            <span class="rf-title">客户机 · {{ agentName }}</span>
            <span v-if="!online" class="rf-badge off">离线</span>
          </header>

          <div class="rf-bar">
            <button class="sm" :disabled="clientPath === null || clientLoading" @click="upClient">
              上一级
            </button>
            <button class="sm" :disabled="clientLoading" @click="loadClient(clientPath)">刷新</button>
            <span class="rf-path" :title="clientPath ?? '此电脑'">{{ clientPath ?? '此电脑' }}</span>
          </div>

          <p v-if="clientError" class="notice err small">{{ clientError }}</p>
          <p v-else-if="clientLoading" class="muted small">正在向这台机器要目录…</p>

          <ul class="rf-list">
            <li v-for="entry in clientEntries" :key="entry.path">
              <button
                v-if="entry.isDirectory"
                class="rf-row dir"
                :disabled="clientLoading"
                @click="enterClientDirectory(entry)"
              >
                <span class="rf-icon">▸</span>
                <span class="rf-name">{{ entry.name }}</span>
              </button>

              <label v-else class="rf-row" :class="{ on: isClientSelected(entry.path) }">
                <input
                  type="checkbox"
                  :checked="isClientSelected(entry.path)"
                  @change="toggleClient(entry.path)"
                />
                <span class="rf-name" :title="entry.name">{{ entry.name }}</span>
                <span class="muted small">{{ fmtBytes(entry.size) }}</span>
              </label>
            </li>
          </ul>

          <p v-if="clientEntries.length === 0 && !clientLoading && !clientError" class="empty small">
            {{ clientPath === null ? '这台机器没有可列出的盘符。' : '这个目录是空的。' }}
          </p>

          <div class="rf-foot">
            <span class="muted small">已选 {{ clientSelected.length }} 个</span>
            <button class="sm primary" :disabled="!canFetchHere" @click="fetchSelectedToLocal">
              取回到本机
            </button>
          </div>
        </section>

        <!-- ③ 本机 -->
        <section class="rf-pane">
          <header class="rf-head">
            <span class="rf-title">本机 · 你选定的目录</span>
            <button class="sm" @click="pickLocalRoot">
              {{ localRoot ? '换一个根目录' : '选择根目录' }}
            </button>
          </header>

          <div v-if="localRoot" class="rf-bar">
            <button class="sm" :disabled="localStack.length <= 1 || localLoading" @click="upLocal">
              上一级
            </button>
            <button class="sm" :disabled="localLoading" @click="loadLocal">刷新</button>
            <span class="rf-path" :title="localBreadcrumb">{{ localBreadcrumb }}</span>
          </div>

          <p v-if="localError" class="notice err small">{{ localError }}</p>

          <p v-if="!localRoot" class="muted small">
            浏览器不允许列出盘符，只能由你选一个目录作为起点（之后可以逐级进出）。
          </p>

          <ul v-else class="rf-list">
            <li v-for="entry in localEntries" :key="entry.name">
              <button
                v-if="entry.kind === 'directory'"
                class="rf-row dir"
                :disabled="localLoading"
                @click="enterLocalDirectory(entry)"
              >
                <span class="rf-icon">▸</span>
                <span class="rf-name">{{ entry.name }}</span>
              </button>

              <label v-else class="rf-row" :class="{ on: isLocalSelected(entry.name) }">
                <input
                  type="checkbox"
                  :checked="isLocalSelected(entry.name)"
                  @change="toggleLocal(entry.name)"
                />
                <span class="rf-name" :title="entry.name">{{ entry.name }}</span>
                <span class="muted small">{{ fmtBytes(entry.size) }}</span>
              </label>
            </li>
          </ul>

          <p
            v-if="localRoot && localEntries.length === 0 && !localLoading && !localError"
            class="empty small"
          >
            这个目录是空的。
          </p>

          <div class="rf-foot">
            <span class="muted small">已选 {{ localSelected.length }} 个</span>
            <button class="sm primary" :disabled="!canSendHere || localSelected.length === 0" @click="sendSelectedToAgent">
              发到客户机
            </button>
          </div>

          <p class="muted small">目标：{{ targetHint() }}</p>
        </section>

        <!-- ④ 队列 -->
        <section v-if="jobs.length > 0" class="rf-jobs">
          <header class="rf-head">
            <span class="rf-title">传输（{{ activeCount }} 个进行中）</span>
            <button
              class="sm"
              :disabled="activeCount > 0"
              @click="jobs = jobs.filter((j) => !isTerminal(j.phase))"
            >
              清掉已完成
            </button>
          </header>

          <ul class="rf-list">
            <li v-for="job in jobs" :key="job.key" class="rf-job">
              <div class="rf-job-top">
                <span class="rf-dir">{{ job.direction === 'to-agent' ? '→' : '←' }}</span>
                <span class="rf-name" :title="job.fileName">{{ job.fileName }}</span>
                <span class="muted small">{{ labelOf(job) }}</span>
              </div>

              <div class="rf-bar-track">
                <div
                  class="rf-bar-fill"
                  :class="{ bad: job.phase === 'failed', ok: job.phase === 'done' }"
                  :style="{ width: `${percentOf(job)}%` }"
                ></div>
              </div>

              <div class="rf-job-foot">
                <span class="muted small">
                  {{ fmtBytes(job.doneBytes) }} / {{ job.size > 0 ? fmtBytes(job.size) : '大小未知' }}
                  <template v-if="rateText(job)"> · {{ rateText(job) }}</template>
                </span>

                <span class="rf-job-actions">
                  <button v-if="canRetry(job)" class="sm" @click="retry(job.key)">重试</button>
                  <button v-if="isTerminal(job.phase)" class="sm" @click="dismiss(job.key)">移除</button>
                </span>
              </div>

              <p v-if="job.error" class="notice err small">{{ job.error }}</p>
            </li>
          </ul>
        </section>

        <p class="muted small rf-note">
          传文件走中心中转（先上传到中心、再由对方来取）。<b>收起参数面板会中断正在上传的那一段</b>，
          已经在对方机器上取的那一段不受影响。
        </p>
      </template>
    </template>
  </div>
</template>

<style scoped>
.rf {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-height: 0;
}

.rf-pane {
  border: 1px solid #e6e8ec;
  border-radius: 6px;
  padding: 8px;
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
}

.rf-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
}

.rf-title {
  font-size: 12px;
  font-weight: 600;
  color: #2b3440;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.rf-badge.off {
  font-size: 11px;
  color: #a33;
  border: 1px solid #e3b4b4;
  border-radius: 3px;
  padding: 0 4px;
}

.rf-bar {
  display: flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
}

/* 路径要能被读，也要能看出"它被截断了" —— 只留省略号而不给 title 是不负责任的。 */
.rf-path {
  flex: 1;
  min-width: 0;
  font-size: 11px;
  color: #5a6472;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  direction: rtl;
  text-align: left;
}

.rf-list {
  list-style: none;
  margin: 0;
  padding: 0;
  max-height: 180px;
  overflow: auto;
  border-top: 1px solid #f0f2f5;
}

.rf-list li {
  border-bottom: 1px solid #f6f7f9;
}

.rf-row {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 3px 2px;
  border: 0;
  background: transparent;
  font-size: 12px;
  text-align: left;
  cursor: pointer;
  color: inherit;
}

.rf-row:hover:not(:disabled) {
  background: #f4f7fb;
}

.rf-row.on {
  background: #eef5ff;
}

.rf-row.dir {
  font-weight: 500;
}

.rf-icon {
  color: #7d8794;
  width: 10px;
  flex: 0 0 10px;
}

.rf-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.rf-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
}

.rf-jobs {
  border: 1px solid #e6e8ec;
  border-radius: 6px;
  padding: 8px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.rf-job {
  padding: 4px 0;
}

.rf-job-top {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
}

.rf-dir {
  color: #4a6fa5;
  font-weight: 600;
}

.rf-bar-track {
  height: 4px;
  background: #eef0f3;
  border-radius: 2px;
  overflow: hidden;
  margin: 3px 0;
}

.rf-bar-fill {
  height: 100%;
  background: #4a86e8;
  transition: width 0.2s linear;
}

.rf-bar-fill.ok {
  background: #2e9e5b;
}

.rf-bar-fill.bad {
  background: #cf4b4b;
}

.rf-job-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
}

.rf-job-actions {
  display: flex;
  gap: 4px;
}

.rf-note {
  line-height: 1.5;
}

code {
  background: #f2f4f7;
  padding: 0 3px;
  border-radius: 3px;
}
</style>
