/**
 * 指令库的**前端共享缓存**。
 *
 * 为什么需要它：动作编辑页里一个按钮动辄十几条动作，每条都可能选「指令库」类型。
 * 如果让每张卡片各自 `GET /api/commands`，一次进编辑页就是十几个重复请求（而且它们
 * 会同时发出、谁也占不到便宜）。这里做**模块级单例**：第一次 `ensure()` 真发请求，
 * 之后所有组件共享同一份响应；`ensure()` 并发调用也只会产生一次真实请求。
 *
 * 一致性：指令库面板（`CommandLibraryPanel`）增删改后，把服务端**回传的全量列表**
 * 直接喂给 `sync()` —— 服务端的增删改响应无论成败都会带回最新列表（见
 * `CommandMutationDto`），因此不必再补一次 GET，也不会出现"刚在指令库页新增的指令
 * 回到动作编辑页却选不到"。
 *
 * 缓存是「读过一次就长期有效」：新鲜度由 `sync()` 维护，而不是靠超时。
 * 刻意不加 TTL —— 定时失效会让界面在某个不可预期的时刻突然重新拉取，
 * 反而更难解释。需要强刷时调 `ensure(true)`。
 */
import { computed, ref } from 'vue'
import { api } from './api'
import type { CommandItem } from './types'

const commands = ref<CommandItem[]>([])
const version = ref(0)
const loading = ref(false)
const error = ref('')
/** 是否已经成功读过一次（读失败不算，失败后下次 `ensure()` 会重试）。 */
const loaded = ref(false)

/** 在途请求。并发调用 `ensure()` 时共用它，避免打出多个重复 GET。 */
let inflight: Promise<void> | null = null

async function ensure(force = false): Promise<void> {
  if (!force && loaded.value) return
  if (inflight) return inflight

  loading.value = true
  error.value = ''

  const task = (async () => {
    try {
      const res = await api.commands()
      commands.value = res.commands ?? []
      version.value = res.version
      loaded.value = true
    } catch (e) {
      // 不弹通知、不抛：调用方（动作编辑器）按需展示一行提示即可。
      // 读不到指令库不该让整个编辑页报错。
      error.value = e instanceof Error ? e.message : String(e)
    } finally {
      loading.value = false
      inflight = null
    }
  })()

  inflight = task
  return task
}

/** 用服务端返回的全量列表覆盖缓存（指令库面板增删改后调用）。 */
function sync(next: CommandItem[] | null | undefined, nextVersion?: number): void {
  commands.value = next ?? []
  if (typeof nextVersion === 'number') version.value = nextVersion
  loaded.value = true
  error.value = ''
}

/**
 * 按名称查指令（**大小写不敏感**，与后端解析口径一致 ——
 * `ICommandLibrary` 的查找就是忽略大小写的）。
 */
function findByName(name: string | null | undefined): CommandItem | null {
  const key = (name ?? '').trim().toLowerCase()
  if (!key) return null
  return commands.value.find((c) => (c.name ?? '').trim().toLowerCase() === key) ?? null
}

export const commandLibrary = {
  /** 只读视图 —— 外部不要直接改，用 `sync()`。 */
  commands: computed(() => commands.value),
  version: computed(() => version.value),
  loading: computed(() => loading.value),
  error: computed(() => error.value),
  loaded: computed(() => loaded.value),
  ensure,
  sync,
  findByName,
}
