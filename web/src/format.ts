/** 展示层格式化工具（纯函数，无副作用）。 */

/**
 * 把服务端返回的 ISO 时间转成 `YYYY-MM-DD HH:mm:ss`。
 *
 * 服务端所有时间戳都是**带 +08:00 偏移的北京时间**（见 `BeijingTime`），
 * 因此这里**直接截取字符串**而不是 `new Date(...).toLocaleString()` ——
 * 后者会按浏览器所在时区二次换算，在非 +8 时区的电脑上会显示错时间。
 */
export function fmtDateTime(iso: string | null | undefined): string {
  if (!iso) return ''
  return iso.replace('T', ' ').slice(0, 19)
}

/** 只保留 `HH:mm:ss`。 */
export function fmtTimeOnly(iso: string | null | undefined): string {
  const full = fmtDateTime(iso)
  return full.length >= 19 ? full.slice(11, 19) : full
}

/** 秒 → `3h 05m 12s`。 */
export function fmtUptime(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds))
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  return `${h}h ${String(m).padStart(2, '0')}m ${String(s % 60).padStart(2, '0')}s`
}

/**
 * 绝对时刻（毫秒）→ **带 `+08:00` 偏移**的 ISO 字符串。
 *
 * ⚠️ 存在的唯一理由是配合上面 `fmtDateTime` 的"截字符串"实现：
 * `new Date(ms).toISOString()` 产出的是 `...Z`（UTC），
 * 被 `fmtDateTime` 一截就会把北京时间少显示 8 小时 —— 一个非常容易被忽略的显示错误。
 *
 * 做法是把时刻先平移 8 小时，再按 UTC 格式化，最后把 `Z` 换回 `+08:00`。
 * 只用于**展示与图表坐标轴**；发给服务端的时刻可以直接用 `toISOString()`（那是绝对量）。
 */
export function beijingIso(milliseconds: number): string {
  if (!Number.isFinite(milliseconds)) return ''

  const shifted = new Date(milliseconds + 8 * 3600 * 1000)
  return shifted.toISOString().replace('Z', '+08:00')
}

/**
 * 字节数 → 人类可读（最多到 GB）。
 *
 * ⚠️ 必须一直开到 GB：文件仓库支持 **>2GB** 的文件，只到 MB 会显示成
 * `2048.0 MB` / `5120.0 MB` —— 那不是"人类可读"，而且量纲到了 GB 才是现场的说法。
 * 安装包（几十 MB）与导出体积走同一套，行为不变。
 */
export function fmtBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '-'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}

/**
 * 速率（字节/秒）→ 人类可读。
 *
 * ⚠️ 与 `fmtBytes` 分开而不是加个后缀参数：磁盘 / 网络的速率会到 GB/s 量级，
 * 而文件大小到 MB 就该停了 —— 两个函数各自有自己合适的量纲上限。
 *
 * **null 必须原样传回来**（渲染成 `—`）：把"采不到"显示成 `0 B/s`
 * 会让一条中断的曲线看起来像"网络完全空闲"。
 */
export function fmtRate(bps: number | null | undefined): string {
  if (bps === null || bps === undefined || !Number.isFinite(bps) || bps < 0) return '—'
  if (bps < 1024) return `${bps.toFixed(0)} B/s`
  if (bps < 1024 * 1024) return `${(bps / 1024).toFixed(1)} KB/s`
  if (bps < 1024 * 1024 * 1024) return `${(bps / 1024 / 1024).toFixed(1)} MB/s`
  return `${(bps / 1024 / 1024 / 1024).toFixed(2)} GB/s`
}

/**
 * 百分比 → `37.4%`。
 *
 * 同样保留 null（渲染成 `—`）。服务端已把值夹到 0–100，这里只做展示。
 */
export function fmtPercent(value: number | null | undefined, digits = 1): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '—'
  return `${value.toFixed(digits)}%`
}

/** MB → 人类可读的容量（`7.8 GB`）。null 保留。 */
export function fmtMb(megabytes: number | null | undefined): string {
  if (megabytes === null || megabytes === undefined || !Number.isFinite(megabytes)) return '—'
  if (megabytes < 1024) return `${megabytes} MB`
  return `${(megabytes / 1024).toFixed(1)} GB`
}

/**
 * 已运行时长 → `3天 5小时` / `5小时 12分` / `12分 30秒`。
 *
 * 与 `fmtUptime` 的区别：那个是给"定时器下次触发还有多久"用的（精确到秒）；
 * 这里给"这台机器开了多久"用 —— 客户机的 uptime 动辄几十万秒，
 * 写成 `79h 55m 52s` 没人能一眼读出"三天多"。
 *
 * null 保留（渲染成 `—`）：`uptimeSeconds` 取不到时**不是** 0。
 */
export function fmtUptimeHuman(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds) || seconds < 0) {
    return '—'
  }

  const s = Math.floor(seconds)
  const days = Math.floor(s / 86400)
  const hours = Math.floor((s % 86400) / 3600)
  const minutes = Math.floor((s % 3600) / 60)

  if (days > 0) return `${days}天 ${hours}小时`
  if (hours > 0) return `${hours}小时 ${minutes}分`
  if (minutes > 0) return `${minutes}分 ${s % 60}秒`
  return `${s}秒`
}

/** 运维日志级别 → CSS 类。 */
export function levelClass(level: string): string {
  switch (level) {
    case 'Error':
    case 'Fatal':
      return 'err'
    case 'Warning':
      return 'warn'
    case 'Debug':
      return 'muted'
    default:
      return ''
  }
}

/** 单条动作的摘要（给网格瓦片上的小字提示用）。 */
export function actionSummary(actionCount: number, delayTotalMs: number): string {
  if (actionCount <= 0) return '无动作'
  if (delayTotalMs > 0) return `${actionCount} 个动作 · 共延迟 ${delayTotalMs}ms`
  return `${actionCount} 个动作`
}

/** 瓦片上的尺寸：把配置里的宽高夹到合理区间，避免一处极端配置把整个网格撑坏。 */
export function clampSize(value: number, min: number, max: number, fallback: number): number {
  if (!Number.isFinite(value) || value <= 0) return fallback
  return Math.min(max, Math.max(min, value))
}

/**
 * 配置里的图片字段是**服务端绝对路径**，浏览器加载不到，必须走服务端的转发端点。
 *
 * 规则：只取路径的最后一段作为文件名去请求 `GET /api/config/images/{文件名}`。
 * 服务端只服务它自己图片目录内的文件，因此：
 * - 从旧版导入且图片已还原到本机 → 正常显示；
 * - 引用的是别处的路径（图片没跟过来）→ 404，调用方按 `@error` 回退到纯色瓦片。
 */
export function imageUrl(path: string | null | undefined): string | null {
  if (!path) return null

  const name = path.split(/[\\/]/).pop()?.trim() ?? ''
  if (!name) return null

  return `/api/config/images/${encodeURIComponent(name)}`
}

/** 触发浏览器下载一段文本。 */
export function downloadText(fileName: string, content: string): void {
  const blob = new Blob([content], { type: 'text/plain;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  document.body.appendChild(a)
  a.click()
  document.body.removeChild(a)
  // 立刻释放会让部分浏览器来不及开始下载，延后一拍更稳。
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

/** 复制文本到剪贴板（失败时抛出，让调用方给出提示）。 */export async function copyText(text: string): Promise<void> {
  if (navigator.clipboard?.writeText) {
    await navigator.clipboard.writeText(text)
    return
  }

  // 非 HTTPS / 老浏览器回退：临时 textarea + execCommand。
  const ta = document.createElement('textarea')
  ta.value = text
  ta.style.position = 'fixed'
  ta.style.opacity = '0'
  document.body.appendChild(ta)
  ta.select()

  try {
    const ok = document.execCommand('copy')
    if (!ok) throw new Error('浏览器拒绝了复制操作')
  } finally {
    document.body.removeChild(ta)
  }
}
