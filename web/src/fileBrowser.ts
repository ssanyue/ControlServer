/**
 * 远控面板「文件」页的纯逻辑：**客户机路径、传输阶段、队列调度**。
 *
 * <p>
 * 🔥 <b>为什么这些不放组件里</b>：组件内的私有函数在本项目的测试基建下根本测不到
 * （纯 node 环境、不挂载组件）—— 而这里的每一条都是**会静默错**的规则：
 * 路径末段取错 ⇒ 从客户机取回的错误文件；阶段推进错 ⇒ "上传成功但建单失败"被报成
 * "传输失败"从而重传一遍 2 GB。抽出来才能被用例钉住（与 `reconnect.ts` / `transferRate.ts` 同一做法）。
 * </p>
 *
 * <p>
 * 本文件<b>不碰网络、不碰 Vue、不碰 DOM</b>：调用方把结果喂进来，这里只回答"下一步是什么"。
 * </p>
 */

// ═══════════════════════════════════════════════════════════════════════════
// 1. 客户机路径
//
// 客户机的路径是**它自己的**坐标系（通常是 Windows：`D:\logs\a.log`），
// 而这段代码跑在浏览器里、中心可能跑在 Linux 上。
// ⚠️ 所以这里绝不使用 `path.basename` 这一类"按运行平台分隔符"的实现 ——
//    在 Linux 上它会把整串 Windows 路径原样返回（见 DistributionsController.LeafNameOf 的同一条注释）。
// ═══════════════════════════════════════════════════════════════════════════

/** 这段路径习惯用哪种分隔符。全无分隔符时按 Windows 处理（客户机以 Windows 为主）。 */
export function separatorOf(path: string): string {
  return path.includes('\\') ? '\\' : '/'
}

/** 最后一个分隔符的下标；没有则 -1。 */
function lastSeparatorIndex(path: string): number {
  return Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'))
}

/** 去掉尾部分隔符（`C:\` → `C:`、`/` → ``）。 */
function trimTrailingSeparators(path: string): string {
  return path.replace(/[\\/]+$/, '')
}

/**
 * 取路径末段（文件名）。
 *
 * <p>`leafOf('D:\logs\a.log') === 'a.log'` —— **与运行平台无关**。</p>
 * <p>盘根返回空串（`C:\` 没有"名字"），调用方据此不要把盘根当成文件名。</p>
 */
export function leafOf(raw: string): string {
  const path = trimTrailingSeparators(raw.trim())
  if (!path) return ''

  // 盘根（`C:\` 去掉尾分隔符后是 `C:`）不是文件 —— 返回空串，
  // 否则页面会把"选中 C 盘"当成选中了一个叫 `C:` 的文件。
  if (/^[A-Za-z]:$/.test(path)) return ''

  const index = lastSeparatorIndex(path)
  return index >= 0 ? path.slice(index + 1) : path
}

/**
 * 上一级目录；**没有上一级时返回 `null`**。
 *
 * <p>
 * `null` 的语义是"该回到盘符列表了"，不是"失败了" —— 页面据此把"上一级"渲染成
 * 可点的（回到此电脑）还是灰的。
 * </p>
 *
 * <ul>
 *   <li>`C:\logs` → `C:\`</li>
 *   <li>`C:\` → `null`（盘根）</li>
 *   <li>`\\server\share` → `null`（UNC 根，再往上没有意义）</li>
 *   <li>`/home/x` → `/home`；`/` → `null`</li>
 * </ul>
 */
export function parentOf(raw: string): string | null {
  const path = trimTrailingSeparators(raw.trim())
  if (!path) return null

  // 盘符根：`C:`
  if (/^[A-Za-z]:$/.test(path)) return null

  // UNC 根：`\\server\share`（两段，再往上就是网络邻居，不提供）
  if (/^[\\/]{2}[^\\/]+[\\/][^\\/]+$/.test(path)) return null

  const index = lastSeparatorIndex(path)
  if (index < 0) return null

  // `C:\logs` → `C:\`（保留盘根的那个分隔符）
  if (index === 2 && /^[A-Za-z]:[\\/]/.test(path)) return path.slice(0, 3)

  // `/home` → `/`
  if (index === 0) return separatorOf(path)

  return path.slice(0, index)
}

/**
 * 拼一个子路径。
 *
 * <p>⚠️ 已经以分隔符结尾时**不再补一个** —— `C:\` + `a.txt` 必须是 `C:\a.txt`，
 * 不是 `C:\\a.txt`（后者在客户机上仍能打开，但会污染路径记忆与同名比较）。</p>
 */
export function joinPath(dir: string, name: string): string {
  const base = dir.trim()
  if (!base) return name

  const separator = separatorOf(base)
  const hasTrailing = /[\\/]$/.test(base)

  return hasTrailing ? `${base}${name}` : `${base}${separator}${name}`
}

/** 是不是一个"写全了"的绝对路径（盘符 / UNC / 类 Unix 根）。 */
export function isAbsolutePath(raw: string): boolean {
  const path = raw.trim()
  if (!path) return false

  return (
    /^[A-Za-z]:[\\/]/.test(path) || // C:\ 或 C:/
    /^[\\/]{2}[^\\/]/.test(path) || // \\server\share
    path.startsWith('/')
  )
}

/** 面包屑：从某个根目录到当前目录的逐段路径（根段也包含在内）。 */
export function breadcrumbOf(current: string): { name: string; path: string }[] {
  const path = trimTrailingSeparators(current.trim())
  if (!path) return []

  const separator = separatorOf(path)

  // 盘符根：一段 `C:\`
  if (/^[A-Za-z]:$/.test(path)) {
    return [{ name: `${path}${separator}`, path: `${path}${separator}` }]
  }

  const parts = path.split(/[\\/]+/).filter(Boolean)

  // UNC：`\\server\share\dir` → 根是 `\\server\share`
  if (path.startsWith('\\\\') || path.startsWith('//')) {
    const root = `${separator}${separator}${parts[0]}${separator}${parts[1]}`
    const crumbs = [{ name: root, path: root }]

    let acc = root
    for (const part of parts.slice(2)) {
      acc = joinPath(acc, part)
      crumbs.push({ name: part, path: acc })
    }

    return crumbs
  }

  // 类 Unix：根是 `/`
  if (path.startsWith('/')) {
    const crumbs = [{ name: '/', path: '/' }]
    let acc = ''

    for (const part of parts) {
      acc = `${acc}/${part}`
      crumbs.push({ name: part, path: acc })
    }

    return crumbs
  }

  // 盘符 + 若干段
  const root = `${parts[0]}${separator}`
  const crumbs = [{ name: root, path: root }]
  let acc = root

  for (const part of parts.slice(1)) {
    acc = joinPath(acc, part)
    crumbs.push({ name: part, path: acc })
  }

  return crumbs
}

// ═══════════════════════════════════════════════════════════════════════════
// 2. 传输阶段
//
// 两个方向的阶段**刻意不同名**，因为它们卡住的地方不同、处置也不同：
//
//   发到客户机：  uploading → publishing → delivering → done
//                 ↑ 传字节到中心   ↑ 建单派发   ↑ 客户机拉取落盘
//
//   取回本机：    collecting → downloading → done
//                 ↑ 客户机上传到中心  ↑ 浏览器下载并写入本机目录
//
// 🔥 `publishing` 必须是**独立阶段**：上传成功而建单失败时，重试要从"建单"开始，
//    而不是把 2 GB 再传一遍。把它并进 `uploading` 会让重试代价变成整份文件。
// ═══════════════════════════════════════════════════════════════════════════

export type TransferDirection = 'to-agent' | 'to-local'

export type TransferPhase =
  | 'pending'
  | 'uploading'
  | 'publishing'
  | 'collecting'
  | 'delivering'
  | 'downloading'
  | 'done'
  | 'failed'

/** 一条传输任务（一个文件）。 */
export interface TransferJob {
  /** 本地唯一 id（不是服务端任务 id；两者在同一个文件上可能都有）。 */
  key: string
  direction: TransferDirection
  fileName: string
  /** 本机测得的字节数（取回方向在 collect 完成前是 0）。 */
  size: number
  phase: TransferPhase
  /** 已完成的字节数（0…size）。 */
  doneBytes: number
  /** 失败原因（人话）。非 failed 时为 null。 */
  error: string | null
  /** 失败前那一步 —— 重试从这一步开始（见 `retryPhase`）。非 failed 时为 null。 */
  resumeFrom: TransferPhase | null
  /** 建单后服务端任务 id；还没建单为 null。 */
  taskId: number | null
  /** 上传完成后仓库文件 id（`to-agent` 用）；还没上传为 null。 */
  fileId: number | null
  /** 回传完成后仓库文件 id（`to-local` 用，来自 `resultFileId`）。 */
  resultFileId: number | null
}

/** 起始阶段。两个方向都不是 `pending` —— 建单/上传是紧接着做的第一步。 */
export function initialPhase(direction: TransferDirection): TransferPhase {
  return direction === 'to-agent' ? 'uploading' : 'collecting'
}

/** 正常推进的下一步；已经是终态则返回 `null`。 */
export function nextPhase(direction: TransferDirection, phase: TransferPhase): TransferPhase | null {
  if (direction === 'to-agent') {
    switch (phase) {
      case 'uploading':
        return 'publishing'
      case 'publishing':
        return 'delivering'
      case 'delivering':
        return 'done'
      default:
        return null
    }
  }

  switch (phase) {
    case 'collecting':
      return 'downloading'
    case 'downloading':
      return 'done'
    default:
      return null
  }
}

/** 终态：不会再自己变化（页面据此停止轮询）。 */
export function isTerminal(phase: TransferPhase): boolean {
  return phase === 'done' || phase === 'failed'
}

/**
 * 标记失败：把**失败前的那一步**记进 `resumeFrom`。
 *
 * <p>
 * ⚠️ 不能只写 `phase='failed'`：那样"从哪一步重来"就丢了，
 * 调用方只能一律从头 —— 而 `publishing` 失败时从头 = 把整份文件再传一遍。
 * </p>
 */
export function failJob(job: TransferJob, error: string): TransferJob {
  return {
    ...job,
    phase: 'failed',
    error: error.trim() || '未说明原因',
    // 已经在 failed 上再失败一次时，别把 resumeFrom 覆盖成 `failed` 本身。
    resumeFrom: job.phase === 'failed' ? job.resumeFrom : job.phase,
  }
}

/**
 * 失败后**从哪一步重来**。
 *
 * <p>判据是"那一步之前的结果还能不能复用"：</p>
 * <ul>
 *   <li>`uploading` 失败 ⇒ 从 `uploading`（已传的字节由服务端会话续上，不是从头）；</li>
 *   <li>`publishing` 失败 ⇒ 从 `publishing`（**文件已经在仓库里了**，重试只重建单）；</li>
 *   <li>`collecting` 失败 ⇒ 从 `collecting`；</li>
 *   <li>`downloading` 失败 ⇒ 从 `downloading`（仓库里那份还在，重新下载即可）。</li>
 * </ul>
 */
export function retryPhase(job: TransferJob): TransferPhase {
  if (job.resumeFrom && job.resumeFrom !== 'failed' && job.resumeFrom !== 'done') return job.resumeFrom
  return initialPhase(job.direction)
}

/** 这一条是否值得给一个「重试」按钮。 */
export function canRetry(job: TransferJob): boolean {
  return job.phase === 'failed'
}

/** 阶段的中文人话（页面上那一行字；**必须能被断言**，不要写在模板里散着）。 */
export function phaseLabel(direction: TransferDirection, phase: TransferPhase): string {
  if (phase === 'failed') return '失败'
  if (phase === 'done') return '已完成'

  if (direction === 'to-agent') {
    switch (phase) {
      case 'uploading':
        return '上传到中心'
      case 'publishing':
        return '派发给这台机器'
      case 'delivering':
        return '机器接收中'
      default:
        return '等待'
    }
  }

  switch (phase) {
    case 'collecting':
      return '机器上传中'
    case 'downloading':
      return '下载到本机'
    default:
      return '等待'
  }
}

// ═══════════════════════════════════════════════════════════════════════════
// 3. 队列调度
// ═══════════════════════════════════════════════════════════════════════════

/**
 * 浏览器侧同时进行的传输数。
 *
 * <p>
 * 与客户机的 `MaxConcurrent`（默认 2）对齐：再多也只会让每一路都变慢，
 * 而进度条全都卡在 30% 是现场最难解释的一种"卡住"。
 * </p>
 */
export const MAX_CONCURRENT = 2

/** 现在可以开工的几条（挑最早的 `pending`，最多补到 `limit` 条）。 */
export function pickRunnable(jobs: TransferJob[], limit = MAX_CONCURRENT): TransferJob[] {
  const running = jobs.filter((j) => j.phase !== 'pending' && !isTerminal(j.phase)).length
  const room = Math.max(0, limit - running)

  if (room === 0) return []

  return jobs.filter((j) => j.phase === 'pending').slice(0, room)
}

/** 整体进度：已完成字节 / 已知总字节（总字节全未知时返回 null，渲染成不确定态）。 */
export function overallProgress(jobs: TransferJob[]): { done: number; total: number; pct: number } | null {
  let done = 0
  let total = 0
  let known = false

  for (const job of jobs) {
    if (job.size > 0) {
      known = true
      total += job.size
      done += Math.min(Math.max(0, job.doneBytes), job.size)
    } else {
      // 大小还不知道（回传还没量出来）：字节算进"已做"，但不进分母。
      done += Math.max(0, job.doneBytes)
    }
  }

  if (!known) return null

  return { done, total, pct: total > 0 ? Math.round((done / total) * 100) : 0 }
}
