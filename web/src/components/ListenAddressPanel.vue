<script setup lang="ts">
import { computed, ref } from 'vue'
import { ApiError, api } from '../api'
import type { CurrentUser, ServerAddressChangeResultDto, ServerAddressDto } from '../types'

/**
 * 监听地址热切换（M2-E5 的 UI 入口）。
 *
 * 三个必须讲清楚的事实，否则用户一定会踩：
 * 1. 这里显示的是 **Kestrel 实际在听的地址**，不是配置值 —— 切换后两者不一致；
 * 2. `0.0.0.0` 表示监听全部网卡，但**它本身不可访问**，局域网要用本机 IP；
 * 3. 切完之后**当前页面所在的原点就没了**，必须跳到新地址（由父级完成倒计时跳转）。
 */

const props = defineProps<{
  user: CurrentUser | null
}>()

const emit = defineEmits<{
  /** 切换已受理 —— 当前页原点的失效，请父级开启跳转倒计时。 */
  (e: 'address-switched', port: number): void
}>()

const address = ref<ServerAddressDto | null>(null)
const loadError = ref('')
const loading = ref(false)

const newPort = ref<number | null>(null)
const switching = ref(false)
const result = ref<ServerAddressChangeResultDto | null>(null)
const failure = ref('')

const isAdmin = computed(() => props.user?.role === 'Admin')

/** 当前页面自己的 host（用户真正在用的那个），跳转后只用它换端口。 */
const currentHost = computed(() => window.location.hostname)

/** 当前页面的端口（省略时按协议补默认值）—— 模板里访问不到 window，必须走 computed。 */
const currentPort = computed(
  () => window.location.port || (window.location.protocol === 'https:' ? '443' : '80'),
)

/** 是否监听全部网卡（0.0.0.0 / [::] / *）—— 这类地址浏览器访问不到。 */
const listensAll = computed(() =>
  (address.value?.urls ?? []).some((u) => /\/\/(0\.0\.0\.0|\[::\]|\*)/.test(u)),
)

const portUnchanged = computed(
  () =>
    newPort.value !== null && address.value !== null && newPort.value === address.value.port,
)

function isValidPort(p: unknown): p is number {
  return typeof p === 'number' && Number.isInteger(p) && p >= 1 && p <= 65535
}

async function load() {
  loading.value = true
  loadError.value = ''

  try {
    address.value = await api.address()
    newPort.value = address.value.port
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

async function doSwitch() {
  if (!isValidPort(newPort.value)) {
    failure.value = '端口必须是 1–65535 之间的整数。'
    result.value = null
    return
  }

  switching.value = true
  failure.value = ''
  result.value = null

  try {
    const r = await api.changeAddress({ port: newPort.value })
    result.value = r

    if (r.status === 'Accepted' && r.newPort) {
      // 服务端同时会广播 ServerAddressChanged，但实时通道可能已断线 ——
      // 这里直接兜底触发；父级的倒计时函数会重置计时器，重复触发无害。
      emit('address-switched', r.newPort)
    }
  } catch (e) {
    if (e instanceof ApiError) {
      // 400 Invalid / 409 PortUnavailable 的响应体就是同一个 DTO，带着可读原因。
      const body = e.body as ServerAddressChangeResultDto | null
      result.value = body
      failure.value = body?.message ?? e.message
    } else {
      failure.value = e instanceof Error ? e.message : String(e)
    }
  } finally {
    switching.value = false
  }
}

void load()
</script>

<template>
  <section class="panel">
    <div class="panel-head">
      <h2>监听地址（不重启热切换）</h2>
      <button class="sm" :disabled="loading" @click="load">
        {{ loading ? '读取中…' : '刷新' }}
      </button>
    </div>

    <p v-if="loadError" class="notice err">
      <strong>读取监听地址失败</strong>
      <span>{{ loadError }}</span>
    </p>

    <template v-else-if="address">
      <div class="stats">
        <div class="wide">
          <span class="muted">实际监听（Kestrel）</span>
          <strong class="mono">{{ address.urls.join('、') || '—' }}</strong>
        </div>
        <div>
          <span class="muted">当前端口</span>
          <strong>{{ address.port }}</strong>
        </div>
        <div>
          <span class="muted">端口来源</span>
          <span class="badge" :class="address.persisted ? 'ok' : ''">
            {{ address.persisted ? '已持久化' : '配置/默认值' }}
          </span>
        </div>
      </div>

      <p class="muted small" style="margin-bottom: 0">
        这里读的是 Kestrel <strong>真正在听的地址</strong>，不是配置里的值 —— 热切换之后两者会不一致，
        所以界面一律以实际值为准。你当前正访问：
        <span class="mono">{{ currentHost }}:{{ currentPort }}</span>
      </p>

      <p v-if="listensAll" class="notice info" style="margin-top: 10px">
        <strong>当前监听全部网卡</strong>
        <span>
          <code>0.0.0.0</code> 表示接受任意网卡的连接，但<strong>它不是能被访问的地址</strong> ——
          本机请用 <code>127.0.0.1</code>，局域网内其他设备请用本机 IP（如
          <code>192.168.x.x</code>）。下面的跳转会自动沿用你当前使用的地址，只改端口。
        </span>
      </p>

      <!-- ── 权限 ── -->
      <p v-if="!isAdmin" class="notice warn" style="margin-top: 16px">
        <strong>权限不足</strong>
        <span>
          当前角色为 {{ user?.role ?? '未知' }}。热切换会让所有已连接的客户端失联，属全站级运维动作，
          仅管理员（Admin）可执行；「运维」权限只能查看当前地址。
        </span>
      </p>

      <!-- ── 切换表单 ── -->
      <template v-else>
        <label style="margin-top: 16px">切换到新端口</label>

        <div class="row-actions">
          <input
            v-model.number="newPort"
            type="number"
            min="1"
            max="65535"
            style="width: 130px"
            @keyup.enter="doSwitch"
          />
          <button class="primary" :disabled="switching" @click="doSwitch">
            {{ switching ? '切换中…' : '切换端口' }}
          </button>
        </div>

        <p v-if="portUnchanged" class="muted small">
          与当前端口相同 —— 服务端会返回「无需切换」，不产生任何动作。
        </p>

        <p class="muted small">
          切换成功后<strong>本页所在地址立刻失效</strong>，会自动倒计时跳到
          <span class="mono">{{ currentHost }}:{{ isValidPort(newPort) ? newPort : '…' }}</span>。
          端口被占用时服务端返回 409，此时旧监听<strong>一动没动</strong> ——
          不存在「服务停在新端口却没起来」的中间状态。
        </p>
      </template>

      <!-- ── 结果 ── -->
      <div v-if="result && result.status === 'Accepted'" class="notice ok" style="margin-top: 12px">
        <strong>{{ result.message }}</strong>
        <span>
          <span class="mono">{{ result.oldUrls.join('、') }}</span> →
          <span class="mono">{{ result.newUrls.join('、') }}</span>
        </span>
      </div>

      <div
        v-else-if="result && result.status === 'NoChange'"
        class="notice info"
        style="margin-top: 12px"
      >
        <strong>{{ result.message }}</strong>
      </div>

      <div v-if="failure" class="notice err" style="margin-top: 12px">
        <strong>切换失败</strong>
        <span>{{ failure }}</span>
        <span v-if="result?.oldUrls?.length">
          服务仍在原地址 <span class="mono">{{ result.oldUrls.join('、') }}</span>，未受任何影响。
        </span>
      </div>

      <p v-if="isAdmin && address" class="muted small" style="margin-bottom: 0">
        端口持久化文件：<span class="mono">{{ address.overrideFile }}</span>
        {{ address.persisted ? '（已存在，重启后从它恢复）' : '（尚不存在，改过端口才生成）' }}。
        端口取值的生效顺序是
        <strong>命令行 &gt; 环境变量 &gt; 本文件 &gt; 配置文件</strong>
        —— 所以删掉它即回到配置文件里的端口；而命令行传了 <code>--Server:Http:Urls</code>
        时，本文件会被命令行盖过。
      </p>
    </template>
  </section>
</template>

<style scoped>
.stats {
  display: flex;
  flex-wrap: wrap;
  gap: 10px 26px;
  margin-bottom: 10px;
}

.stats > div {
  display: flex;
  flex-direction: column;
  font-size: 13px;
  min-width: 80px;
}

.stats > div.wide {
  flex: 1 1 300px;
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
  margin-bottom: 8px;
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
