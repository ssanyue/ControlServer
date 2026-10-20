<script setup lang="ts">
/**
 * 用户账号管理页（M3 RBAC）。
 *
 * 这一页是「谁能进来」这件事本身，因此整个 `/api/users` 控制器都是 `AdminOnly`。
 * 前端按角色隐藏入口只是**避免让 Operator 看到一堆点了就 403 的按钮**，
 * 真正的授权判定始终在服务端 —— 越权请求会被 403。
 *
 * 三条来自服务端的守卫，前端只做"提前禁用 + 说明"，不试图自己实现规则：
 * 1. `LastAdmin` —— 不能停用 / 降级 / 删除**最后一个启用的管理员**；
 * 2. `SelfTarget` —— 不能停用 / 删除自己（改名、改自己密码可以）；
 * 3. `DuplicateName` —— 用户名**大小写不敏感**唯一。
 *
 * ⚠️ 服务端允许"有同伴时给自己降级"，这是有意的：否则管理员永远无法把权限
 * 交接给别人后自己退出。前端因此**不**把自己那一行的降级选项禁掉，
 * 只把"最后一个管理员"这一条禁掉 —— 与后端判据保持一致。
 */
import { computed, onMounted, ref } from 'vue'
import { ApiError, api } from '../api'
import type { CurrentUser, RoleOption, UserDto, UserMutationResult } from '../types'
import { fmtDateTime } from '../format'

const props = defineProps<{
  user: CurrentUser | null
}>()

const emit = defineEmits<{
  (e: 'notice', kind: 'ok' | 'err' | 'warn', text: string): void
}>()

const users = ref<UserDto[]>([])
const roles = ref<RoleOption[]>([])
const loading = ref(false)
const loadError = ref('')

/** 有请求在飞 —— 期间禁用所有写按钮，避免重复提交把界面搅乱。 */
const busyId = ref<number | 'create' | null>(null)

// ── 新增 ──
const creating = ref(false)
const form = ref({ userName: '', password: '', role: 'Operator' })
const formError = ref('')
const passwordShown = ref(false)

// ── 改名（就地编辑用户名，只改这一个字段）──
const renameFor = ref<UserDto | null>(null)
const renameValue = ref('')

// ── 重置他人密码 ──
const resetFor = ref<UserDto | null>(null)
const resetPassword = ref('')
const resetError = ref('')

// ── 删除（两段式确认）──
const deleteArmedId = ref<number | null>(null)
let disarmTimer: number | null = null

const roleName = computed(() => {
  const map = new Map<string, string>()
  for (const r of roles.value) map.set(r.name, r.displayName)
  return map
})

/** 启用中的管理员数量 —— 用来判断"这一行是不是最后一个管理员"。 */
const enabledAdminCount = computed(
  () => users.value.filter((u) => u.role === 'Admin' && u.isEnabled).length,
)

function displayRole(role: string): string {
  return roleName.value.get(role) ?? role
}

/** 用户名大小写不敏感，与服务端的重名判据一致。 */
function isSelf(u: UserDto): boolean {
  const me = props.user?.userName ?? ''
  return me !== '' && me.trim().toLowerCase() === u.userName.trim().toLowerCase()
}

/** 停用 / 降级 / 删除它可以吗 —— 只做前端提前禁用，服务端仍会独立判定。 */
function isLastAdmin(u: UserDto): boolean {
  return u.role === 'Admin' && u.isEnabled && enabledAdminCount.value <= 1
}

// ═══════════════════════════════════════════════════════════════
// 读
// ═══════════════════════════════════════════════════════════════

async function load() {
  loading.value = true
  loadError.value = ''

  try {
    const [list, roleList] = await Promise.all([api.users(), api.roles()])
    users.value = list ?? []
    roles.value = roleList ?? []
  } catch (e) {
    loadError.value = `读取账号列表失败：${describe(e)}`
  } finally {
    loading.value = false
  }
}

function describe(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.status === 403) return '当前角色无权管理账号（需要管理员）。'
    return e.message
  }
  return e instanceof Error ? e.message : String(e)
}

// ═══════════════════════════════════════════════════════════════
// 写
// ═══════════════════════════════════════════════════════════════

/**
 * 写操作的响应**无论成败都带回服务端最新全量列表**，直接覆盖本地。
 *
 * 失败时它是以 400/404/409 抛出来的（`unwrap` 只认 2xx），所以要从 `ApiError.body`
 * 里把那份列表捞出来 —— 这正是"失败也不停留在旧状态"的关键：
 * 例如"最后一个管理员"被拒时，列表里可能已经有别人改过的角色，覆盖后界面立刻为真。
 */
function applyFailure(e: unknown): string {
  if (e instanceof ApiError) {
    const body = e.body as UserMutationResult | null
    if (body && Array.isArray(body.users)) {
      users.value = body.users
      return body.message || `操作被拒绝（HTTP ${e.status}）。`
    }
    if (e.status === 403) return '当前角色无权管理账号（需要管理员）。'
    return e.message
  }
  return e instanceof Error ? e.message : String(e)
}

async function submitCreate() {
  const userName = form.value.userName.trim()
  const password = form.value.password

  if (!userName) {
    formError.value = '用户名不能为空。'
    return
  }
  if (!password) {
    formError.value = '请设置初始密码。'
    return
  }

  busyId.value = 'create'
  formError.value = ''

  try {
    const res = await api.createUser(userName, password, form.value.role)
    users.value = res.users ?? []
    emit('notice', 'ok', `已新建账号「${userName}」，首次登录时需修改密码。`)
    cancelCreate()
  } catch (e) {
    formError.value = applyFailure(e)
  } finally {
    busyId.value = null
  }
}

function startCreate() {
  creating.value = true
  form.value = { userName: '', password: '', role: 'Operator' }
  formError.value = ''
  passwordShown.value = false
  cancelRename()
  cancelReset()
}

function cancelCreate() {
  creating.value = false
  form.value = { userName: '', password: '', role: 'Operator' }
  formError.value = ''
  passwordShown.value = false
}

async function submitRename(u: UserDto) {
  const name = renameValue.value.trim()
  if (!name) return

  if (name.toLowerCase() === u.userName.trim().toLowerCase()) {
    cancelRename()
    return
  }

  busyId.value = u.id

  try {
    const res = await api.updateUser(u.id, { userName: name })
    users.value = res.users ?? []
    emit('notice', 'ok', `账号「${u.userName}」已改名为「${name}」。`)
    cancelRename()
  } catch (e) {
    emit('notice', 'err', applyFailure(e))
  } finally {
    busyId.value = null
  }
}

function startRename(u: UserDto) {
  renameFor.value = u
  renameValue.value = u.userName
  cancelReset()
}

function cancelRename() {
  renameFor.value = null
  renameValue.value = ''
}

async function changeRole(u: UserDto, role: string) {
  if (role === u.role) return

  busyId.value = u.id

  try {
    const res = await api.updateUser(u.id, { role })
    users.value = res.users ?? []
    emit('notice', 'ok', `账号「${u.userName}」的角色已改为 ${displayRole(role)}。`)
  } catch (e) {
    emit('notice', 'err', applyFailure(e))
  } finally {
    busyId.value = null
  }
}

async function toggleEnabled(u: UserDto) {
  const next = !u.isEnabled
  busyId.value = u.id

  try {
    const res = await api.updateUser(u.id, { isEnabled: next })
    users.value = res.users ?? []
    emit('notice', 'ok', next ? `已启用账号「${u.userName}」。` : `已停用账号「${u.userName}」。`)
  } catch (e) {
    emit('notice', 'err', applyFailure(e))
  } finally {
    busyId.value = null
  }
}

function startReset(u: UserDto) {
  resetFor.value = u
  resetPassword.value = ''
  resetError.value = ''
  cancelRename()
}

function cancelReset() {
  resetFor.value = null
  resetPassword.value = ''
  resetError.value = ''
}

async function submitReset() {
  const target = resetFor.value
  if (!target) return

  if (!resetPassword.value) {
    resetError.value = '请输入新密码。'
    return
  }

  busyId.value = target.id

  try {
    const res = await api.resetUserPassword(target.id, resetPassword.value)
    users.value = res.users ?? []
    emit('notice', 'ok', `已重置「${target.userName}」的密码，对方下次登录时须立即修改。`)
    cancelReset()
  } catch (e) {
    resetError.value = applyFailure(e)
  } finally {
    busyId.value = null
  }
}

async function doDelete(u: UserDto) {
  // 两段式确认：第一次点只"上膛"，5 秒内再点才真删。
  if (deleteArmedId.value !== u.id) {
    if (isSelf(u)) {
      emit('notice', 'warn', '不能删除当前登录的账号。若要注销自己，请用其它管理员账号操作。')
      return
    }

    if (isLastAdmin(u)) {
      emit('notice', 'warn', `「${u.userName}」是最后一个启用的管理员，删除后没有人能再管理系统。`)
      return
    }

    deleteArmedId.value = u.id
    if (disarmTimer !== null) window.clearTimeout(disarmTimer)
    disarmTimer = window.setTimeout(() => (deleteArmedId.value = null), 5000)
    return
  }

  deleteArmedId.value = null
  if (disarmTimer !== null) {
    window.clearTimeout(disarmTimer)
    disarmTimer = null
  }

  busyId.value = u.id

  try {
    const res = await api.deleteUser(u.id)
    users.value = res.users ?? []
    emit('notice', 'ok', `已删除账号「${u.userName}」。`)
  } catch (e) {
    emit('notice', 'err', applyFailure(e))
  } finally {
    busyId.value = null
  }
}

onMounted(load)
</script>

<template>
  <section class="panel">
    <div class="panel-head">
      <h2>
        用户账号
        <span class="muted small">（{{ users.length }} 个 · 仅管理员可见）</span>
      </h2>

      <div class="panel-actions">
        <button class="sm" :disabled="loading" @click="load">
          {{ loading ? '读取中…' : '刷新' }}
        </button>
        <button class="sm primary" :disabled="creating || busyId !== null" @click="startCreate">
          新建账号
        </button>
      </div>
    </div>

    <p class="muted small" style="margin-top: 0">
      三种角色：<strong>管理员</strong>（全部权限）· <strong>操作员</strong>（可执行与编辑，不能管理账号与导入导出）·
      <strong>查看者</strong>（只读）。新建账号与重置密码都会把该账号标记为
      <strong>首次登录必须改密</strong> —— 管理员设的是临时口令。
    </p>

    <p v-if="loadError" class="notice err">{{ loadError }}</p>

    <!-- ═══ 新建表单 ═══ -->
    <div v-if="creating" class="editor">
      <div class="editor-head">
        <strong>新建账号</strong>
        <button class="sm" @click="cancelCreate">取消</button>
      </div>

      <div class="grid-3">
        <div>
          <label>用户名</label>
          <input v-model="form.userName" placeholder="例如：operator1" @keyup.enter="submitCreate" />
        </div>
        <div>
          <label>初始密码（至少 8 位，不能与用户名相同）</label>
          <input
            v-model="form.password"
            :type="passwordShown ? 'text' : 'password'"
            placeholder="临时口令"
            @keyup.enter="submitCreate"
          />
        </div>
        <div>
          <label>角色</label>
          <select v-model="form.role">
            <option v-for="r in roles" :key="r.name" :value="r.name">{{ r.displayName }}</option>
          </select>
        </div>
      </div>

      <label class="inline">
        <input v-model="passwordShown" type="checkbox" />
        <span>显示密码</span>
      </label>

      <div class="row-actions">
        <button class="primary" :disabled="busyId === 'create'" @click="submitCreate">
          {{ busyId === 'create' ? '创建中…' : '创建' }}
        </button>
      </div>

      <p v-if="formError" class="notice err" style="margin-top: 8px">{{ formError }}</p>
    </div>

    <!-- ═══ 重置密码 ═══ -->
    <div v-if="resetFor" class="editor">
      <div class="editor-head">
        <strong>重置「{{ resetFor.userName }}」的密码</strong>
        <button class="sm" @click="cancelReset">取消</button>
      </div>

      <p class="muted small" style="margin: 0 0 4px">
        重置后该账号会被标记为<strong>首次登录必须改密</strong> —— 你设的只是临时口令。
      </p>

      <div class="grid-2">
        <div>
          <label>新密码（至少 8 位，不能与该账号用户名相同）</label>
          <input
            v-model="resetPassword"
            type="password"
            placeholder="临时口令"
            @keyup.enter="submitReset"
          />
        </div>
        <div style="align-self: end">
          <button class="primary" :disabled="busyId === resetFor.id" @click="submitReset">
            {{ busyId === resetFor.id ? '提交中…' : '确认重置' }}
          </button>
        </div>
      </div>

      <p v-if="resetError" class="notice err" style="margin-top: 8px">{{ resetError }}</p>
    </div>

    <!-- ═══ 列表 ═══ -->
    <p v-if="loading && users.length === 0" class="muted small">正在读取账号…</p>

    <div v-else-if="users.length === 0" class="empty">
      <strong>没有读到账号</strong>
      <span>正常情况下至少会有一个内置管理员。若为空，请检查服务端数据目录是否正确。</span>
    </div>

    <div v-else class="table-wrap">
      <table class="grid">
        <thead>
          <tr>
            <th>用户名</th>
            <th>角色</th>
            <th>状态</th>
            <th>创建时间</th>
            <th>最近登录</th>
            <th>操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="u in users" :key="u.id" :class="{ editing: renameFor?.id === u.id }">
            <td>
              <template v-if="renameFor?.id === u.id">
                <div class="inline-form">
                  <input
                    v-model="renameValue"
                    @keyup.enter="submitRename(u)"
                    @keyup.esc="cancelRename"
                  />
                  <button class="sm primary" :disabled="busyId === u.id" @click="submitRename(u)">
                    保存
                  </button>
                  <button class="sm" @click="cancelRename">取消</button>
                </div>
              </template>
              <template v-else>
                <strong>{{ u.userName }}</strong>
                <span v-if="isSelf(u)" class="badge send" style="margin-left: 6px">我</span>
              </template>
            </td>

            <td>
              <select
                :value="u.role"
                :disabled="busyId === u.id || isLastAdmin(u)"
                :title="isLastAdmin(u) ? '最后一个启用的管理员，不能降级' : ''"
                @change="changeRole(u, ($event.target as HTMLSelectElement).value)"
              >
                <option v-for="r in roles" :key="r.name" :value="r.name">{{ r.displayName }}</option>
              </select>
            </td>

            <td>
              <span v-if="!u.isEnabled" class="badge err">已停用</span>
              <span v-else-if="u.isLocked" class="badge warn" :title="`锁定至 ${fmtDateTime(u.lockedUntil)}`">
                锁定中
              </span>
              <span v-else class="badge ok">正常</span>

              <span
                v-if="u.mustChangePassword"
                class="badge warn"
                style="margin-left: 4px"
                title="该账号尚未修改初始/重置密码，除改密外几乎所有操作都会被服务端拒绝"
              >
                待改密
              </span>
            </td>

            <td class="muted">{{ fmtDateTime(u.createdAt) }}</td>
            <td class="muted">{{ u.lastLoginAt ? fmtDateTime(u.lastLoginAt) : '从未登录' }}</td>

            <td>
              <div class="row-actions" style="margin: 0">
                <button class="sm" :disabled="busyId !== null" @click="startRename(u)">改名</button>

                <button class="sm" :disabled="busyId !== null" @click="startReset(u)">重置密码</button>

                <button
                  class="sm"
                  :disabled="busyId !== null || isSelf(u) || isLastAdmin(u)"
                  :title="
                    isSelf(u)
                      ? '不能停用自己'
                      : isLastAdmin(u)
                        ? '最后一个启用的管理员，不能停用'
                        : ''
                  "
                  @click="toggleEnabled(u)"
                >
                  {{ u.isEnabled ? '停用' : '启用' }}
                </button>

                <button
                  class="sm"
                  :class="{ danger: deleteArmedId === u.id }"
                  :disabled="busyId !== null || isSelf(u) || isLastAdmin(u)"
                  :title="
                    isSelf(u)
                      ? '不能删除自己'
                      : isLastAdmin(u)
                        ? '最后一个启用的管理员，不能删除'
                        : ''
                  "
                  @click="doDelete(u)"
                >
                  {{ deleteArmedId === u.id ? '确认删除' : '删除' }}
                </button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <p class="muted small" style="margin-bottom: 0">
      停用与降级<strong>当场生效</strong>：对方已登录的会话在下一次请求时就会被服务端复核并作废，
      不必等到重新登录。
    </p>
  </section>
</template>

<style scoped>
.editor {
  border: 1px solid var(--border-strong);
  border-radius: var(--radius);
  background: var(--panel-soft);
  padding: 12px 14px;
  margin-bottom: 14px;
}

.editor-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 4px;
}

.grid-2 {
  display: grid;
  grid-template-columns: 1.8fr 1fr;
  gap: 0 12px;
}

.grid-3 {
  display: grid;
  grid-template-columns: 1.2fr 1.4fr 1fr;
  gap: 0 12px;
}

@media (max-width: 820px) {
  .grid-2,
  .grid-3 {
    grid-template-columns: 1fr;
  }
}

.row-actions {
  display: flex;
  gap: 6px;
  align-items: center;
  flex-wrap: wrap;
  margin-top: 10px;
}

.inline-form {
  display: flex;
  gap: 6px;
  align-items: center;
}

.inline-form input {
  min-width: 130px;
}

label.inline {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 10px 0 0;
}

label.inline input {
  width: auto;
}

table.grid td select {
  width: auto;
  min-width: 96px;
  padding: 2px 6px;
  font-size: 12px;
}

tr.editing td {
  background: var(--accent-soft);
}
</style>
