<script setup lang="ts">
/**
 * 「文件分发」页（M4.4）= 文件仓库 + 新建分发任务 + 分发任务。
 *
 * ⚠️ 页签名 M7.15 从「文件」改成「文件分发」：原名与页内第一栏「文件仓库」同名，
 * 现场以为这是"管理服务器文件"的页面，找不到"把文件发到客户端"的入口。
 * 页签 key（`files`）与文件名都没动 —— 只改文案。
 *
 * 这一层只做三件事，业务都在两个子组件里：
 *   ① 加载共享数据（仓库列表 + 在线表）—— 两个子页都要用；
 *   ② 把仓库页的「分发」按钮接到分发页的 `focusFile`（**直接调方法**而不是走 prop：
 *      走 prop 时"对同一个文件再点一次"值没变、watch 不触发，表现为"第二次点没反应"）；
 *   ③ 把实时进度事件转发给分发页。
 *
 * ⚠️ 在线表在这里也拉一份，而不是让分发页自己拉：它和「客户端」页读的是同一个接口，
 * 分开拉会在两页之间造成"一边在线、一边离线"的短暂不一致；更重要的是
 * 分发页需要它来渲染目标多选，而它在页面里是常驻的。
 */
import { onMounted, onUnmounted, ref } from 'vue'
import { api } from '../api'
import type {
  AgentListDto,
  CurrentUser,
  FileRepoItemDto,
  FileRepoListDto,
  TransferProgressDto,
} from '../types'
import FileRepoPanel from './FileRepoPanel.vue'
import DistributionPanel from './DistributionPanel.vue'

const props = defineProps<{
  user: CurrentUser | null
}>()

const emit = defineEmits<{
  (e: 'notice', kind: 'ok' | 'err' | 'warn', text: string): void
}>()

const files = ref<FileRepoListDto | null>(null)
const agents = ref<AgentListDto | null>(null)
const loadingFiles = ref(false)

const distribution = ref<InstanceType<typeof DistributionPanel> | null>(null)

async function loadFiles() {
  loadingFiles.value = true

  try {
    files.value = await api.files()
  } catch (e) {
    emit('notice', 'err', `读取文件仓库失败：${e instanceof Error ? e.message : String(e)}`)
  } finally {
    loadingFiles.value = false
  }
}

/**
 * 拉在线表（分发页的目标多选 + 明细里的在线状态都要它）。
 *
 * 失败静默：拿不到在线表不该让整页打不开 —— 仓库列表仍然可用，
 * 只是没法选目标（分发页会显示"还没有客户端"）。
 */
async function loadAgents() {
  try {
    agents.value = await api.agents()
  } catch {
    /* 保持现状 */
  }
}

/** 仓库页点「分发」：切到分发页并带上那个文件。 */
function onDistribute(file: FileRepoItemDto) {
  distribution.value?.focusFile(file.id)
}

/** 把子组件的提示原样向上转一层（App 统一显示）。 */
function onNotice(kind: 'ok' | 'err' | 'warn', text: string) {
  emit('notice', kind, text)
}

/** 收到实时进度（App 转发的 `TransferProgress`）。 */
function applyProgress(progress: TransferProgressDto) {
  distribution.value?.applyProgress(progress)
}

/**
 * 只重拉在线表（不动仓库列表）。
 *
 * 由 App 在收到 `AgentChanged` / `AgentOnline` / `AgentOffline` 时调用 ——
 * 分发页的目标多选读的就是这份数据。少了这条线，用户会上演经典一幕：
 * 在这一页等着给一台刚开机的机器分发，而目标列表里根本没有它，
 * 只能切到客户端页再切回来（那会重新挂载组件）才会出现。
 */
async function refreshAgents() {
  await loadAgents()
}

/** 刷新全部（上传完成后由仓库页触发）。 */
async function refresh() {
  await Promise.all([loadFiles(), loadAgents()])
}

/** 合并"回传成功"的刷新（见 `onCollected`）。 */
let collectedTimer: number | null = null

/**
 * 分发页报告「有一台客户机把文件成功回传进仓库了」—— 立刻把**仓库列表**重拉一遍。
 *
 * ⚠️ 从前这里只刷**任务列表**：回传的任务卡片变成了"已完成"，而右边的文件仓库里
 * 死活看不到那个文件，非得手动点一次刷新 —— 最容易被当成"回传其实没成功"。
 *
 * 为什么要 debounce：一次回传给 N 台机器，N 条终态事件会在几秒内陆续到达，
 * 每条都拉一次就是 N 次请求 + N 次重渲染。合并成"最后一条之后 250ms"拉一次。
 *
 * ⚠️ 只有**实时事件**这条路走得到这里。漏掉事件的情形（断线期间传完）由两条既有
 * 路径兜底：重连时 App 会调 `refresh()`；页面重新挂载时 `onMounted` 也会调。
 */
function onCollected() {
  if (collectedTimer !== null) window.clearTimeout(collectedTimer)

  collectedTimer = window.setTimeout(() => {
    collectedTimer = null
    void loadFiles()
  }, 250)
}

onMounted(async () => {
  await refresh()
})

onUnmounted(() => {
  if (collectedTimer !== null) window.clearTimeout(collectedTimer)
  collectedTimer = null
})

defineExpose({ refresh, refreshAgents, applyProgress })
</script>

<template>
  <div>
    <FileRepoPanel
      :files="files"
      :loading="loadingFiles"
      :user="props.user"
      @notice="onNotice"
      @reload="loadFiles"
      @distribute="onDistribute"
    />

    <DistributionPanel
      ref="distribution"
      :user="props.user"
      :files="files"
      :agents="agents"
      @notice="onNotice"
      @collected="onCollected"
    />
  </div>
</template>
