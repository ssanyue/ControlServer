<script setup lang="ts">
import { computed, ref } from 'vue'
import { api } from '../api'
import type { SystemInfoDto, TimeSyncResultDto } from '../types'
import { fmtDateTime, fmtUptime } from '../format'

/**
 * 运行诊断 + 时间同步（M2-E4 的两个端点终于有了入口）。
 *
 * 两块刻意合在一个组件里：时间同步的全部关键指标（偏移、上次校准、来源）
 * 都由 `GET /api/system/info` 提供，而「立即校准」只是主动触发一次并回读。
 * 拆成两个组件就得把同一份 `info` 在父级持有再下发，反而更绕。
 */

const info = ref<SystemInfoDto | null>(null)
const loadError = ref('')
const loading = ref(false)

const syncing = ref(false)
const syncResult = ref<TimeSyncResultDto | null>(null)

async function load() {
  loading.value = true
  loadError.value = ''

  try {
    info.value = await api.systemInfo()
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

async function doSync() {
  syncing.value = true
  syncResult.value = null

  try {
    // 校准失败也是 200（ok:false），所以这里抛出来的只有 5xx 与网络错误。
    syncResult.value = await api.timeSync()
    // 偏移变了，info 里的时间同步字段必须重读才准。
    await load()
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    syncing.value = false
  }
}

const ts = computed(() => info.value?.timeSync ?? null)

/** 同步状态徽标：从未成功 > 已校正 > 已同步（偏移为 0）。 */
const syncBadge = computed(() => {
  const t = ts.value
  if (!t) return { cls: '', text: '未知' }
  if (!t.synchronized) return { cls: 'warn', text: '从未成功校准' }
  if (t.corrected) return { cls: 'ok', text: '已同步 · 已校正' }
  return { cls: 'ok', text: '已同步 · 无偏移' }
})

void load()
</script>

<template>
  <div>
    <!-- ═══ 运行诊断 ═══ -->
    <section class="panel">
      <div class="panel-head">
        <h2>运行诊断</h2>
        <button class="sm" :disabled="loading" @click="load">
          {{ loading ? '读取中…' : '刷新' }}
        </button>
      </div>

      <p v-if="loadError" class="notice err">
        <strong>读取系统信息失败</strong>
        <span>{{ loadError }}</span>
      </p>

      <p v-else-if="!info" class="muted small">正在读取系统信息…</p>

      <template v-else>
        <div class="stats">
          <div><span class="muted">机器名</span><strong>{{ info.machineName }}</strong></div>
          <div><span class="muted">产品</span><strong>{{ info.product }} v{{ info.version }}</strong></div>
          <div class="wide"><span class="muted">操作系统</span><span>{{ info.osPlatform }}</span></div>
          <div>
            <span class="muted">服务端时间</span>
            <strong class="mono">{{ fmtDateTime(info.serverTime) }}</strong>
          </div>
          <div class="wide"><span class="muted">时间口径</span><span>{{ info.timeZone }}</span></div>
          <div><span class="muted">HTTP 端口</span><strong>{{ info.httpPort }}</strong></div>
          <div>
            <span class="muted">对码端口</span>
            <strong>
              {{ info.pairingPort }}
              <span class="badge" :class="info.listenerRunning ? 'ok' : 'warn'">
                {{ info.listenerRunning ? '监听中' : '未监听' }}
              </span>
            </strong>
          </div>
          <div><span class="muted">已运行</span><strong>{{ fmtUptime(info.uptimeSeconds) }}</strong></div>
          <div><span class="muted">内存日志</span><strong>{{ info.recentLogCount }} 条</strong></div>
        </div>

        <label style="margin-top: 14px">数据目录（配置、图片、日志都在这里）</label>
        <div class="path mono small">{{ info.dataDirectory }}</div>

        <p class="muted small" style="margin-bottom: 0">
          定时调度：{{ info.timers.enabled ? '已启用' : '已停用' }} ·
          生效触发点 {{ info.timers.triggerCount }} 个 ·
          本进程已触发 {{ info.timers.firedCount }} 次、漏触发 {{ info.timers.missedCount }} 条 ·
          下次触发
          <span class="mono">{{ info.timers.nextFireAt ? fmtDateTime(info.timers.nextFireAt) : '—' }}</span>
          。逐条清单见「定时」页。
        </p>
      </template>
    </section>

    <!-- ═══ 时间同步 ═══ -->
    <section class="panel">
      <div class="panel-head">
        <h2>时间同步</h2>
        <div class="panel-actions">
          <span v-if="ts" class="badge" :class="syncBadge.cls">{{ syncBadge.text }}</span>
          <button class="sm" :disabled="syncing || !ts" @click="doSync">
            {{ syncing ? '校准中…' : '立即校准' }}
          </button>
        </div>
      </div>

      <p v-if="!ts" class="muted small">正在读取…</p>

      <template v-else>
        <p v-if="!ts.synchronized" class="notice warn">
          <strong>尚未成功校准过</strong>
          <span>
            定时器按服务端时间判定，若本机时钟有偏差，按钮会在错误的时间点触发。
            点一次「立即校准」即可确认网络与 NTP 服务器是否可达。
          </span>
        </p>

        <p v-if="ts.lastError" class="notice err">
          <strong>上次校准失败</strong>
          <span>{{ ts.lastError }}</span>
        </p>

        <div class="stats">
          <div><span class="muted">当前偏移</span><strong class="mono">{{ ts.offsetText }}</strong></div>
          <div><span class="muted">上次校准</span><span>{{ fmtDateTime(ts.lastSyncAt) || '从未' }}</span></div>
          <div class="wide">
            <span class="muted">来源服务器</span>
            <span class="mono small">{{ ts.source ?? '—' }}</span>
          </div>
          <div>
            <span class="muted">往返耗时</span>
            <span>{{ ts.roundTripMs !== null ? ts.roundTripMs + ' ms' : '—' }}</span>
          </div>
          <div>
            <span class="muted">自动重校</span>
            <span>
              {{ ts.autoSync ? `每 ${Math.max(1, Math.round(ts.resyncIntervalSeconds / 60))} 分钟` : '已关闭' }}
            </span>
          </div>
          <div>
            <span class="muted">累计尝试</span>
            <span>{{ ts.attemptCount }} 次 · 失败 {{ ts.failureCount }}</span>
          </div>
        </div>

        <label style="margin-top: 14px">生效的 NTP 服务器（按顺序尝试）</label>
        <div class="chips">
          <span v-for="s in ts.servers" :key="s" class="badge mono">{{ s }}</span>
        </div>

        <!-- 本次手动校准的结果 -->
        <div
          v-if="syncResult"
          :class="['notice', syncResult.ok ? 'ok' : 'err']"
          style="margin-top: 12px"
        >
          <strong>{{ syncResult.ok ? '校准成功' : '校准失败' }}</strong>
          <span v-if="syncResult.ok">
            偏移 {{ syncResult.offsetText }}，相对上次变化 {{ syncResult.offsetJumpMs }} ms；
            来源 <span class="mono">{{ syncResult.source }}</span>，
            往返 {{ syncResult.roundTripMs }} ms。
            <template v-if="syncResult.offsetJumpMs !== 0">
              偏移变化会<strong>立刻改变</strong>全应用的日志时间戳与定时器判定口径。
            </template>
          </span>
          <span v-else>{{ syncResult.error }}</span>

          <ul v-if="syncResult.attempts.length > 0" style="margin: 6px 0 0; padding-left: 18px">
            <li v-for="(a, i) in syncResult.attempts" :key="i" class="small mono">{{ a }}</li>
          </ul>
        </div>

        <p class="muted small" style="margin-bottom: 0">
          时间口径固定为<strong>北京时间（UTC+8）</strong>，与操作系统时区解耦。
          校准只影响本进程的偏移量，<strong>不修改系统时钟</strong>。
        </p>
      </template>
    </section>
  </div>
</template>

<style scoped>
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
  flex: 1 1 260px;
  min-width: 200px;
}

.stats strong {
  font-size: 15px;
  font-weight: 500;
}

.chips {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.path {
  background: var(--panel-soft);
  border: 1px solid var(--border);
  border-radius: 4px;
  padding: 6px 8px;
  word-break: break-all;
}
</style>
