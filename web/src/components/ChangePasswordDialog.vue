<script setup lang="ts">
/**
 * 修改自己的密码（M3）。
 *
 * 两种进入方式，共用这一个组件：
 * 1. **主动改**（顶栏「修改密码」）—— `forced = false`，可以取消/点遮罩关掉；
 * 2. **强制改**（`mustChangePassword = true`）—— `forced = true`，**关不掉**：
 *    没有关闭按钮、点遮罩与 ESC 都无效。因为此时服务端闸门已经生效，
 *    除白名单端点外所有 API 都返回 403，关掉弹窗只会让用户对着一个"什么都点不动"的界面。
 *
 * 注意这里**不校验收到的 UserDto** —— 改自己的密码只走 `/api/auth/change-password`，
 * 当前密码对了就行。而"密码复杂度"的权威判定在服务端 `PasswordPolicy`，
 * 前端这套只是提前反馈，避免白跑一趟网络。
 */
import { computed, ref, watch } from 'vue'
import { api } from '../api'

const props = defineProps<{
  open: boolean
  /** 由 `mustChangePassword` 驱动 —— 为 true 时不允许关闭。 */
  forced: boolean
  userName: string
}>()

const emit = defineEmits<{
  (e: 'close'): void
  /** 改密成功。会话已在服务端重签，调用方应重新拉一次 `me()`。 */
  (e: 'changed'): void
}>()

const current = ref('')
const next = ref('')
const confirm = ref('')
const error = ref('')
const submitting = ref(false)

/** 与服务端 `PasswordPolicy.MinLength` 对齐；前端只做提前反馈。 */
const MIN_LENGTH = 8

const tooShort = computed(() => next.value.length > 0 && next.value.length < MIN_LENGTH)
const mismatch = computed(() => confirm.value.length > 0 && confirm.value !== next.value)

function reset() {
  current.value = ''
  next.value = ''
  confirm.value = ''
  error.value = ''
  submitting.value = false
}

// 每次打开都清空 —— 否则上一次的输入（含密码）会留在框里。
watch(
  () => props.open,
  (open) => {
    if (open) reset()
  },
)

function tryClose() {
  // 强制改密期间不允许关闭：服务端闸门已经生效，关掉只会让用户以为界面坏了。
  if (props.forced || submitting.value) return
  emit('close')
}

async function submit() {
  error.value = ''

  if (!current.value) {
    error.value = '请输入当前密码。'
    return
  }
  if (next.value.length < MIN_LENGTH) {
    error.value = `新密码至少 ${MIN_LENGTH} 位。`
    return
  }
  if (next.value !== confirm.value) {
    error.value = '两次输入的新密码不一致。'
    return
  }
  if (next.value === current.value) {
    error.value = '新密码不能与当前密码相同。'
    return
  }

  submitting.value = true

  try {
    await api.changePassword(current.value, next.value)
    reset()
    emit('changed')
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div v-if="open" class="overlay" @click.self="tryClose">
    <div class="dialog" :class="{ forced }">
      <div class="dialog-head">
        <strong>{{ forced ? '必须先修改密码' : '修改密码' }}</strong>
        <button v-if="!forced" class="sm" :disabled="submitting" @click="tryClose">关闭</button>
      </div>

      <p v-if="forced" class="notice warn" style="margin: 0 0 8px">
        账号「{{ userName }}」使用的仍是初始或管理员重置的临时口令。
        在修改密码之前，除本操作外的功能都会被服务端拒绝。
      </p>
      <p v-else class="muted small" style="margin: 0 0 4px">
        当前账号：{{ userName }}
      </p>

      <label>当前密码</label>
      <input
        v-model="current"
        type="password"
        autocomplete="current-password"
        @keyup.enter="submit"
      />

      <label>新密码（至少 {{ MIN_LENGTH }} 位）</label>
      <input v-model="next" type="password" autocomplete="new-password" @keyup.enter="submit" />

      <label>确认新密码</label>
      <input v-model="confirm" type="password" autocomplete="new-password" @keyup.enter="submit" />

      <p v-if="tooShort" class="notice warn small" style="margin-top: 8px">
        新密码至少 {{ MIN_LENGTH }} 位。
      </p>
      <p v-else-if="mismatch" class="notice warn small" style="margin-top: 8px">
        两次输入的新密码不一致。
      </p>

      <p v-if="error" class="notice err" style="margin-top: 8px">{{ error }}</p>

      <div class="row-actions">
        <button class="primary" :disabled="submitting" @click="submit">
          {{ submitting ? '提交中…' : '确认修改' }}
        </button>
        <button v-if="!forced" :disabled="submitting" @click="tryClose">取消</button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.overlay {
  position: fixed;
  inset: 0;
  z-index: 50;
  background: rgba(17, 24, 39, 0.42);
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 20px;
}

.dialog {
  width: 100%;
  max-width: 420px;
  background: var(--panel);
  border: 1px solid var(--border);
  border-radius: var(--radius);
  box-shadow: 0 12px 32px rgba(16, 24, 40, 0.18);
  padding: 18px 20px 20px;
  max-height: 90vh;
  overflow: auto;
}

/* 强制态给一道更醒目的边，让"这是拦路而不是普通的弹窗"一眼可辨。 */
.dialog.forced {
  border-color: #fde68a;
  box-shadow:
    0 0 0 3px var(--warn-soft),
    0 12px 32px rgba(16, 24, 40, 0.18);
}

.dialog-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 10px;
}

.row-actions {
  display: flex;
  gap: 8px;
  align-items: center;
  margin-top: 16px;
}
</style>
