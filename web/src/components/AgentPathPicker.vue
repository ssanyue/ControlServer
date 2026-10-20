<script setup lang="ts">
/**
 * 客户机文件路径选择器（M4.4+）。
 *
 * **它解决什么**：下发要填"落到客户机的哪个目录"、回传要填"从客户机取哪个文件"，
 * 而**手输路径是现场最常见的失败源**
 * —— 盘符写错、目录名大小写错、多一个空格、中文路径被输入法带进全角字符。
 * 而这些错要等到"传输失败"才暴露，中间隔着建单、派发、机器上线好几道环节。
 * 这个组件把客户机的目录**像资源管理器一样**列出来，点进去、点一下就选中。
 *
 * 🔗 **一次真实往返**：网页 → 服务端 →（长连接）→ 客户机。所以要知道三件事：
 *   ① 客户机**离线**时根本浏览不了（服务端会回 `offline`）—— 手输那条路必须留着；
 *   ② 会**等**（服务端 8 秒超时），所以要有 loading 态，不能让界面看着像卡死；
 *   ③ 失败**不是异常**，是要**就地显示**的一句话（"这台机器离线"），
 *      弹一个"系统出错"的提示会把"点进了一个空目录"渲染成软件坏了。
 *
 * ⚠️ **它只是"帮着填"，不是安全边界**：浏览能看全盘，但真正拦越界的是客户机自己
 * （见 `FileTransferClient`）。所以这里选出来的路径同样会被客户机再校验一次 ——
 * 不要在这里做任何"替客户机把关"的假设。
 *
 * ⚠️ **多台目标时只能浏览一台**：目录是**每台机器各有一份**的，浏览器没有"多台的并集"
 * 这种概念。所以这里让用户显式选一台来看（默认第一台），并在文案里说清"路径是按这台挑的"。
 *
 * ── 2026-10-14 的两项改动（"加载很慢、像卡住了"）──
 *
 * 🔥 <b>① 读过的目录缓存起来（<see cref="CACHE_TTL_MS"/>）</b>。此前每一次点击都是一个完整的
 * 往返（网页 → 服务端 → 长连接 → 客户机 → 列目录 → 原路返回），
 * 于是"进一个目录、退回来、再进另一个"要点三次等待 —— 而这三层的清单其实一秒内都没变。
 * 现在同一条路径在 TTL 内**直接出结果**（"上一级"因此是瞬时的），「刷新」显式绕过缓存。
 *
 * 🔥 <b>② 读取期间必须"看得出来在读取"</b>。此前点击目录时旧列表**原样留着不动**
 * （loading 只禁用了工具栏按钮），于是屏幕看起来是**卡住**的 —— 用户会再点一次，
 * 而每一次点击都真的会再发一个请求。现在旧列表会**变灰 + 顶部出现进度行**，
 * 并且加载中忽略新的点击。这条不是修饰：那个"卡顿假象"本身就是这次要修的问题。
 */
import { computed, ref, watch } from 'vue'
import { api } from '../api'
import { AGENT_BROWSE_STATUSES } from '../types'
import type { AgentBrowseEntryDto, AgentBrowseResultDto } from '../types'
import { fmtBytes, fmtDateTime } from '../format'

const props = withDefaults(
  defineProps<{
    /** 当前选定的路径（v-model）。 */
    modelValue: string
    /**
     * 可以浏览哪些机器（= 已选目标）。空 = 还没选目标，浏览不可用。
     *
     * 之所以要"名字 + 在线状态"而不只是 id：浏览的**头号失败原因就是那台机器离线**，
     * 提前把状态摆在选择器里，比让用户点一次再看到失败要好。
     */
    targets: { id: number; name: string; online: boolean }[]
    /**
     * 手输时的候选（来自所选客户机自己上报的白名单目录）。
     *
     * **浏览不了的时候它就是唯一的帮手**（机器离线时浏览必然失败，
     * 而白名单根目录是离线也能提前知道的 —— 它跟着硬件清单一起来的）。
     */
    suggestions?: string[]
    /** 输入框上方的标签；传空串则不显示（行内紧凑用法）。 */
    label?: string
    /**
     * 要选什么：
     *   · `file`      —— 回传源路径：点文件即选中；
     *   · `directory` —— 下发存放目录：点目录是"进去"，另有一个「用这个目录」按钮。
     */
    accept?: 'file' | 'directory'
    /**
     * 输入框**还空着**时，打开浏览面板从哪里开始（M4.7 的"路径记忆"）。
     *
     * 为什么不直接把它填进输入框：回传方向记的是**目录**，而输入框要的是**文件**。
     * 直接把目录塞进去会让"看着像已经选好了"却提交一个目录路径 ——
     * 所以只拿它当浏览起点，输入框仍然空着，由用户点中具体文件才算选好。
     */
    initialPath?: string
    /**
     * 要不要在浏览器里给一个「记为此机默认目录」的按钮（2026-10-14）。
     *
     *   · `'target'` —— 下发方向：记住的是"存放目录"（`agent_hosts.LastTargetDir`）；
     *   · `'source'` —— 回传方向：记住的是"读取目录"（`LastSourceDir`）；
     *   · `null`（缺省）—— 不显示这个按钮。
     *
     * ⚠️ 它记住的是**当前所在的这一层目录**（就是 `here`），不是选中的那个文件 ——
     * 默认值要能当"下次的起落点"用，文件路径做不到这件事。
     */
    remember?: 'target' | 'source' | null
  }>(),
  {
    // 🔥 `modelValue` **必须有默认值**（2026-10-14 修）。
    //
    // 它声明成 `string`，但调用方可能给 `undefined`：下发页那一行绑的是
    // `v-model="targetDirs[agent.id]"`，而 `targetDirs` 的键是"勾选时才补"的 ——
    // **没有任何记忆值的那台机器**（全新部署、头一次用）这个键根本不存在 ⇒ `undefined`。
    // 于是 `props.modelValue.trim()` 抛 TypeError，而调用点是 `void openAt()`
    // ⇒ 异常被**无声吞掉**：面板照样打开，然后**什么都不发生**（不发请求、不报错、不转圈）。
    // 现场看到的就是"点了浏览没反应"，而它只在"这台机器还没记过目录"时出现 ——
    // 越新的部署越容易撞上，越难复现。
    //
    // 给了默认值还顺带消掉 Vue 的 "Missing required prop" 警告（有默认值就不再是 required）。
    modelValue: '',
    label: '源路径',
    accept: 'file',
    suggestions: () => [],
    initialPath: '',
    remember: null,
  },
)

const emit = defineEmits<{
  (e: 'update:modelValue', value: string): void
  /**
   * 刚刚把某个目录记成了这台机器的默认目录。
   *
   * 父级据此就地更新自己那份 `agents` 快照 —— 不更新的话页面要等下一次列表刷新
   * 才回显新值，而人已经看到"已记住"了（两种说法并存会让人怀疑到底存没存上）。
   */
  (e: 'remembered', payload: { agentId: number; targetDir?: string; sourceDir?: string }): void
}>()

/**
 * `<datalist>` 的 id 必须**逐个实例唯一**。
 *
 * ⚠️ 本组件现在会被**一台目标渲染一个**（下发页的目标行），共用同一个 id 会让
 * 后渲染的那些绑到第一个 datalist 上 —— 表现为"某一行的候选目录串到了别的机器上"，
 * 而且不报任何错。所以这里用一个模块级自增计数器。
 */
let instanceSeq = 0
const datalistId = `m44-browse-dirs-${++instanceSeq}`

const isDirectoryMode = computed(() => props.accept === 'directory')

/**
 * 正在浏览哪一台。
 *
 * 默认第一台；目标列表变化时**保持当前选择**（只要它还在列表里）——
 * 否则用户每勾一台新机器，浏览位置就会跳回去。
 */
const targetId = ref<number | null>(props.targets[0]?.id ?? null)

const open = ref(false)
const loading = ref(false)
const result = ref<AgentBrowseResultDto | null>(null)
/** 网络层错误（服务端 4xx/5xx 或断网）—— 与"业务性失败"（`result.ok === false`）分开存。 */
const failure = ref<string | null>(null)

/**
 * 已经读过的目录（键 = `机器 Id | 路径`，空路径 = 盘符列表）。
 *
 * 🔥 <b>这是"加载很慢"的主要修法</b>：不缓存时，每一次点击都是一个完整往返
 * （网页 → 服务端 → 长连接 → 客户机 → 列目录 → 原路返回）。而实际用起来是
 * "进目录 → 退回来 → 再进另一个"这种**来回点**的模式，同一层会被反复读好几遍，
 * 每次都要等一个新的往返 —— 观感就是"卡"。
 *
 * TTL 取 <see cref="CACHE_TTL_MS"/>：短到"刚在客户机上新建了文件"能很快看见，
 * 长到能覆盖上面那种来回点。要**立刻**看最新内容就点「刷新」（它绕过缓存）。
 *
 * ⚠️ 键里带 <c>targetId</c>：不同机器的磁盘完全不一样，共用一份缓存会串味。
 */
const cache = new Map<string, { at: number; result: AgentBrowseResultDto }>()

/** 缓存的保鲜时长。 */
const CACHE_TTL_MS = 20_000

function cacheKey(path: string | null): string {
  return `${targetId.value ?? 0}|${path ?? ''}`
}

const current = computed(() => props.targets.find((t) => t.id === targetId.value) ?? null)

const canBrowse = computed(() => props.targets.length > 0)

/** 当前这一层可点的条目。 */
const entries = computed<AgentBrowseEntryDto[]>(() => result.value?.entries ?? [])

const here = computed(() => result.value?.requestedPath ?? null)

/** 目录模式下，"当前这一层"就是可以直接选中的存放目录。 */
const canAdoptHere = computed(() => isDirectoryMode.value && !!here.value)

// ⚠️ `?? ''` 不是多余的：`modelValue` 声明成 `string`，但调用方可能给 `undefined`
//    （键不存在的 `targetDirs[id]`）。上面已给出默认值兜住，这里再兜一层 ——
//    这类"静态类型看着对、运行时是 undefined"的入参，值得每一处读取都自己防一手。
const adopted = computed(() => canAdoptHere.value && (props.modelValue ?? '').trim() === here.value)

watch(
  () => props.targets.map((t) => t.id),
  (ids) => {
    if (targetId.value !== null && ids.includes(targetId.value)) return
    targetId.value = ids[0] ?? null

    // 换了机器 ⇒ 上一台的目录列表立刻作废（每台的磁盘不一样，留着只会误导）。
    // ⚠️ 缓存**不清**：键里带着机器 Id，那台的清单等切回去时仍然有效。
    result.value = null
    failure.value = null
  },
)

watch(targetId, () => {
  result.value = null
  failure.value = null
  if (open.value) void browse(null)
})

/**
 * 打开面板时自动拉一次。
 *
 * 🔥 **起点优先落在输入框里已有的路径上**，而不是每次都从盘符列表重来 ——
 * 这就是"路径保存"在浏览器这一侧的表现：输入框被上次用过的目录（服务端记的
 * `lastTargetDir` / `lastSourceDir`）预填好，点开浏览就**原地**在那个目录，
 * 不用一层层点进去。从磁盘根开始点 4 层才到目标目录，是这套界面最烦的一步。
 */
function toggle() {
  open.value = !open.value
  if (open.value && result.value === null && failure.value === null) void openAt()
}

/**
 * 算出"从哪一层开始浏览"。
 *
 * · 目录模式：输入框里的就是目录，直接用；
 * · 文件模式（回传源路径）：输入框里可能是**文件名**，要退到它所在的那一层。
 *
 * 返回 `null` = 没有可用的起点 ⇒ 走原来的"盘符列表"。
 */
function startPath(): string | null {
  const value = (props.modelValue ?? '').trim()

  // 输入框空着 ⇒ 用"记住的上次目录"当起点（回传方向专走这一条）。
  if (value === '') {
    const fallback = (props.initialPath ?? '').trim()
    return fallback === '' ? null : fallback
  }

  if (isDirectoryMode.value) return value

  const cut = Math.max(value.lastIndexOf('\\'), value.lastIndexOf('/'))
  if (cut <= 0) return null

  const dir = value.slice(0, cut)
  // 盘根是 `C:\`，切完只剩 `C:`（2 字符）—— 得把分隔符补回去，否则客户端认不出这是个根。
  return dir.length === 2 && dir[1] === ':' ? `${dir}\\` : dir
}

/** 带兜底的打开：记着的目录**已经不在了**（被删 / 盘被拔 / 换了机器）就退回盘符列表。 */
async function openAt() {
  const start = startPath()
  if (start === null) {
    void browse(null)
    return
  }

  await browse(start)

  // `notfound` 是"那个目录现在没了"。这时留在原地只有一个报错框，
  // 而用户本来想看的是"这台机器有什么"⇒ 自动退回盘符列表（工具栏上「← 此电脑」仍然可用）。
  if (result.value && !result.value.ok && result.value.status === AGENT_BROWSE_STATUSES.notfound) {
    await browse(null)
  }
}

/**
 * 读取某个目录（或盘符列表）。
 *
 * @param path 目标路径；null = "此电脑"（盘符列表）。
 * @param force true = 绕过缓存（「刷新」按钮与失败重试走这条）。
 *
 * ⚠️ <b>加载中不清空 <c>result</c></b>：清空会让整个列表闪一下白，反而更像卡住。
 * 保留旧内容并给它加 `.stale`（变灰）+ 顶部进度行，才是"正在读取"该有的样子。
 * 但这也意味着**旧内容仍然可见** ⇒ 必须靠 <see cref="pick"/> 里的闸门挡住误点，
 * 否则用户点的是上一层的条目，却以为点的是新列表里的。
 */
async function browse(path: string | null, force = false) {
  if (targetId.value === null) return

  // 加载中忽略新的读取请求：每一次都真的会发一个往返，连点五下就是五个。
  if (loading.value) return

  if (!force) {
    const hit = cache.get(cacheKey(path))

    if (hit && Date.now() - hit.at < CACHE_TTL_MS) {
      result.value = hit.result
      failure.value = null
      return
    }
  }

  loading.value = true
  failure.value = null

  try {
    const fetched = await api.browseAgentDirectory(targetId.value, path)

    // ⚠️ 成功的**业务性失败**（"这台机器离线"）也缓存：它在 20 秒内同样不会变，
    //    而反复点一个离线的机器是最容易把人卡在这里的操作。
    cache.set(cacheKey(path), { at: Date.now(), result: fetched })

    result.value = fetched
  } catch (e) {
    // 走到这里说明请求本身没成功（断网 / 403 / 服务端 5xx）。
    // 与 `ok:false` 区分开：那是"问到了，答案是'不行'"，这里是"没问到"。
    failure.value = e instanceof Error ? e.message : '读取目录失败'
    result.value = null
  } finally {
    loading.value = false
  }
}

/** 点条目：目录一律"进去"；文件只在 `file` 模式里被选中。 */
function pick(entry: AgentBrowseEntryDto) {
  // ⚠️ 加载中不响应：此刻屏幕上显示的是**上一层**的条目（见 browse 的注释）。
  if (loading.value) return

  if (entry.isDirectory) {
    void browse(entry.path)
    return
  }

  if (!isDirectoryMode.value) emit('update:modelValue', entry.path)
}

/** 目录模式专用：把"当前所在的目录"填进输入框。 */
function adoptHere() {
  if (here.value) emit('update:modelValue', here.value)
}

/** 「返回上级」：`parentPath` 为 null 表示已经在盘根上 —— 此时回到盘符列表。 */
function up() {
  void browse(result.value?.parentPath ?? null)
}

// ── 「记为此机默认目录」（2026-10-14）──

const remembering = ref(false)
const rememberTip = ref<{ ok: boolean; text: string } | null>(null)

/**
 * 把**当前所在的那一层目录**记成这台机器的默认目录。
 *
 * 与"建单成功才回写"的区别：这是**显式**的一次保存 —— 挑对了目录但这次先不建单，
 * 也留得下来。它存的只是"下次预填什么"，**不是权限**（真正拦越界的仍是客户机的白名单）。
 */
async function rememberHere() {
  const dir = here.value
  const mode = props.remember

  if (!dir || mode === null || targetId.value === null || remembering.value) return

  remembering.value = true
  rememberTip.value = null

  try {
    const r = await api.rememberAgentPath(
      targetId.value,
      mode === 'target' ? { targetDir: dir } : { sourceDir: dir },
    )

    if (r.ok) {
      rememberTip.value = { ok: true, text: `已记住：${dir}` }

      emit('remembered', mode === 'target'
        ? { agentId: targetId.value, targetDir: dir }
        : { agentId: targetId.value, sourceDir: dir })
    } else {
      rememberTip.value = { ok: false, text: r.message || '保存失败。' }
    }
  } catch (e) {
    rememberTip.value = { ok: false, text: e instanceof Error ? e.message : '保存失败' }
  } finally {
    remembering.value = false
  }
}

/** 状态 → 一句人话。服务端已经给了 `message`，这里只兜底"没有正文"的情形。 */
const statusHint = computed(() => {
  const r = result.value
  if (!r || r.ok) return null
  if (r.message) return r.message

  switch (r.status) {
    case AGENT_BROWSE_STATUSES.offline:
      return '这台客户机当前离线，读不到它的目录。'
    case AGENT_BROWSE_STATUSES.timeout:
      return '客户机没有及时回话，稍后重试。'
    case AGENT_BROWSE_STATUSES.denied:
      return '这个路径不在那台机器的可读目录内（旧版客户端限制），换一个试试。'
    case AGENT_BROWSE_STATUSES.unsupported:
      return '该客户机版本还不支持目录浏览，请手输路径或升级客户端。'
    case AGENT_BROWSE_STATUSES.disabled:
      return '该客户机未启用文件传输，没有可浏览的目录。'
    default:
      return '读取目录失败。'
  }
})

function shortTime(entry: AgentBrowseEntryDto): string {
  return entry.isDirectory ? '' : fmtDateTime(entry.modifiedAt)
}
</script>

<template>
  <div class="path-picker">
    <label class="wide">
      <span v-if="props.label" class="label-text">{{ props.label }}</span>
      <span class="input-row">
        <input
          :value="props.modelValue"
          :list="datalistId"
          :placeholder="
            isDirectoryMode
              ? '点右侧「浏览」从客户机里选目录，或直接输入绝对路径；留空 = 用那台机器的默认目录'
              : '点右侧「浏览」从客户机目录里选，或直接输入绝对路径'
          "
          :title="
            isDirectoryMode
              ? '客户机上的绝对路径；不在它允许写入的目录内会被拒绝'
              : '客户机上的绝对路径；不在它允许读取的目录内会被拒绝'
          "
          @input="emit('update:modelValue', ($event.target as HTMLInputElement).value)"
        />
        <button
          type="button"
          class="sm"
          :disabled="!canBrowse"
          :title="canBrowse ? '读取客户机上的目录' : '请先选择至少一台目标客户机'"
          @click="toggle"
        >
          {{ open ? '收起' : '浏览…' }}
        </button>
      </span>
    </label>

    <!-- 手输的候选：`<datalist>` 不渲染可见 UI，只是给上面那个 input 提供下拉候选。 -->
    <datalist :id="datalistId">
      <option v-for="d in props.suggestions" :key="d" :value="d" />
    </datalist>

    <div v-if="open" class="browser">
      <!-- 浏览哪一台。多台目标时磁盘不一样，必须让用户明确"现在看的是哪台"。 -->
      <div v-if="props.targets.length > 1" class="toolbar">
        <label class="inline">
          查看机器
          <select v-model.number="targetId">
            <option v-for="t in props.targets" :key="t.id" :value="t.id">
              {{ t.name }}{{ t.online ? '' : '（离线）' }}
            </option>
          </select>
        </label>
        <span class="muted small">选中机器：{{ current?.name }}</span>
      </div>

      <div class="toolbar">
        <button
          type="button"
          class="sm"
          :disabled="loading || !result"
          :title="'回到这台机器的盘符列表'"
          @click="browse(null)"
        >
          ← 此电脑
        </button>
        <button
          type="button"
          class="sm"
          :disabled="loading || !result?.requestedPath"
          :title="result?.parentPath ? '回到上一级' : '已经在盘根上，没有上一级了'"
          @click="up"
        >
          上一级
        </button>
        <button type="button" class="sm" :disabled="loading" @click="browse(here, true)">
          {{ loading ? '读取中…' : '刷新' }}
        </button>

        <!-- 目录模式专用：把"当前所在目录"选为存放路径。 -->
        <button
          v-if="isDirectoryMode"
          type="button"
          class="sm adopt"
          :class="{ on: adopted }"
          :disabled="loading || !canAdoptHere"
          :title="canAdoptHere ? '把当前这个目录作为存放路径' : '请先进入一个目录'"
          @click="adoptHere"
        >
          {{ adopted ? '✓ 已选中此目录' : '用这个目录' }}
        </button>

        <!-- 「记为此机默认目录」（2026-10-14）：把浏览到的目录**存下来**，
             下次打开这台机器时路径框自动填上 —— 不必先建一次单才记得住。 -->
        <button
          v-if="props.remember"
          type="button"
          class="sm remember"
          :disabled="loading || remembering || !here"
          :title="
            !here
              ? '请先进入一个目录'
              : props.remember === 'target'
                ? '把这台机器**下发文件**时的默认存放目录记成当前这个目录'
                : '把这台机器**回传文件**时的默认读取目录记成当前这个目录'
          "
          @click="rememberHere"
        >
          {{ remembering ? '保存中…' : '记为此机默认目录' }}
        </button>

        <span class="muted small here mono" :title="here ?? '此电脑'">
          {{ here ?? '（此电脑）' }}
        </span>
      </div>

      <!-- 「不能再往上」不是"没有上一级"，要说清是哪一种。 -->
      <p v-if="result?.ok && here && !result.parentPath" class="muted small tight">
        这里已经是盘根了，没有上一级（点「← 此电脑」回到盘符列表）。
      </p>

      <!-- 网络层错误：没问到 -->
      <p v-if="failure" class="status-line err">
        {{ failure }}
        <button type="button" class="sm" @click="browse(here, true)">重试</button>
      </p>

      <!-- 业务性失败：问到了，答案是"不行" -->
      <p v-else-if="statusHint" class="status-line warn">
        {{ statusHint }}
        <button type="button" class="sm" @click="browse(here, true)">重试</button>
      </p>

      <!-- 🔥 「正在读取」必须**看得见**（2026-10-14）。
           此前这一行只在列表为空时才显示，于是"进一个目录"的表现是屏幕上什么都不变 ——
           用户以为卡住了，再点一次，而每次点击真的都会再发一个往返。
           现在它一直显示，配上下面变灰的旧列表，"正在读"这件事就没有歧义了。 -->
      <p v-if="loading" class="status-line busy">
        <span class="spinner" aria-hidden="true" />
        正在读取客户机目录…（一次真实往返：网页 → 服务端 → 客户机）
      </p>

      <!-- 「记住」的结果就地显示 —— 它是一次写操作，不能只靠一闪而过的提示。 -->
      <p
        v-if="rememberTip"
        class="status-line"
        :class="rememberTip.ok ? 'done' : 'err'"
      >
        {{ rememberTip.text }}
      </p>

      <p v-if="!loading && !failure && !statusHint && result?.ok && entries.length === 0" class="muted small tight">
        这个目录是空的。
      </p>

      <!-- ⚠️ 加载中旧列表**仍然可见**（避免闪白），所以必须视觉上说明它已经不是当前内容了。 -->
      <ul v-if="entries.length > 0" class="entries" :class="{ stale: loading }">
        <li
          v-for="entry in entries"
          :key="entry.path"
          :class="{ selected: !entry.isDirectory && entry.path === props.modelValue }"
        >
          <button
            type="button"
            class="entry"
            :class="{ inert: isDirectoryMode && !entry.isDirectory }"
            :title="
              entry.isDirectory
                ? entry.path
                : isDirectoryMode
                  ? '要选目录，这里只列出来给你看'
                  : entry.path
            "
            @click="pick(entry)"
          >
            <span class="icon">{{ entry.isDirectory ? '📁' : '📄' }}</span>
            <span class="name">{{ entry.name }}</span>
            <span class="meta muted small">
              <template v-if="!entry.isDirectory">{{ fmtBytes(entry.size) }}</template>
              <template v-if="shortTime(entry)"> · {{ shortTime(entry) }}</template>
            </span>
          </button>
        </li>
      </ul>

      <p v-if="result?.truncated" class="muted small tight">
        ⚠️ 这个目录里的条目太多，只显示了前 {{ entries.length }} 条 ——
        <strong>列表里没有的条目不代表它不存在</strong>，可以直接在输入框里手输完整路径。
      </p>

      <p class="muted small tight">
        <template v-if="isDirectoryMode">
          点目录进入下一级，走到目标目录后点<strong>「用这个目录」</strong>即可填进上面的输入框。
        </template>
        <template v-else>
          点目录进入下一级，点文件即选中。选中的路径会填进上面的输入框，可以由你继续手改。
        </template>
      </p>
    </div>
  </div>
</template>

<style scoped>
.path-picker label {
  margin-top: 0;
}

.label-text {
  display: block;
  margin-bottom: 4px;
}

.input-row {
  display: flex;
  gap: 6px;
  align-items: center;
}

.input-row input {
  flex: 1;
  min-width: 0;
}

.browser {
  margin-top: 6px;
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--panel-soft);
}

.toolbar {
  display: flex;
  gap: 6px;
  align-items: center;
  flex-wrap: wrap;
  margin-bottom: 6px;
}

.toolbar label.inline {
  display: inline-flex;
  gap: 6px;
  align-items: center;
  margin: 0;
  white-space: nowrap;
}

.here {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.adopt {
  border-color: var(--accent);
  color: var(--accent);
}

.adopt.on {
  background: var(--accent-soft);
}

.adopt:disabled {
  border-color: var(--border);
  color: var(--muted);
}

.status-line {
  display: flex;
  gap: 8px;
  align-items: center;
  margin: 4px 0;
  padding: 6px 8px;
  border-radius: var(--radius);
  font-size: 13px;
}

.status-line.warn {
  background: var(--warn-soft);
  color: var(--warn);
}

.status-line.err {
  background: var(--err-soft);
  color: var(--err);
}

/* 「正在读取」：中性色 + 一个转圈。它不是一个错误，也不该看着像错误。 */
.status-line.busy {
  background: var(--panel-soft);
  color: var(--muted);
}

/* 「已记住」：写操作成功，给一个明确的确认（而不是让它悄悄过去）。 */
.status-line.done {
  background: var(--ok-soft);
  color: var(--ok);
}

.spinner {
  flex: none;
  width: 12px;
  height: 12px;
  border: 2px solid var(--border-strong);
  border-top-color: var(--accent);
  border-radius: 50%;
  animation: path-picker-spin 0.7s linear infinite;
}

@keyframes path-picker-spin {
  to {
    transform: rotate(360deg);
  }
}

/* 🔥 加载中的旧列表：变灰 + 不可点。
   它还在屏幕上（不清空是为了避免闪白），但必须一眼看出"这不是当前这一层的内容"——
   否则用户会去点上一层的条目，而以为自己点的是新列表里的。 */
.entries.stale {
  opacity: 0.45;
  pointer-events: none;
}

.remember {
  border-color: var(--accent);
  color: var(--accent);
}

.remember:disabled {
  border-color: var(--border);
  color: var(--muted);
}

.tight {
  margin: 2px 0;
}

.entries {
  list-style: none;
  margin: 4px 0;
  padding: 0;
  max-height: 260px;
  overflow-y: auto;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--panel);
}

.entries li + li {
  border-top: 1px solid var(--border);
}

.entries li.selected {
  background: var(--accent-soft);
}

.entry {
  display: flex;
  gap: 8px;
  align-items: center;
  width: 100%;
  padding: 5px 8px;
  border: 0;
  border-radius: 0;
  background: transparent;
  color: var(--text);
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.entry:hover {
  background: var(--accent-soft);
}

/* 目录模式下的文件：列出来看得到，但点了没反应 —— 用样式说清"这里不可选"。 */
.entry.inert {
  color: var(--muted);
  cursor: default;
}

.entry.inert:hover {
  background: transparent;
}

.entry .icon {
  flex: none;
}

.entry .name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.entry .meta {
  flex: none;
  white-space: nowrap;
}
</style>
