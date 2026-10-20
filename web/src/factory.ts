/**
 * 编辑期用的默认对象工厂 —— 字段名与默认值逐字对齐 `ControlServer.Core.Models`，
 * 这样「新建按钮」保存后不会因为在服务端被纠正而产生意料之外的差异。
 */

import type {
  ButtonAction,
  ButtonConfig,
  ComboButton,
  MonthDaySchedule,
  MonthlyPlan,
  SubButton,
  SubButtonSet,
} from './types'

/**
 * 生成一个新 Id（UUID v4 形状的字符串）。
 *
 * ⚠️ 不能直接用 `crypto.randomUUID()`：服务是**局域网 HTTP** 访问的
 * （`http://192.168.x.x:8899`），那不是安全上下文，`randomUUID` 在这些页面上
 * 是 `undefined`，直接调用会抛错导致「新建按钮」整个失效。
 * `crypto.getRandomValues` 在非安全上下文里仍然可用，所以优先用它。
 */
export function newId(): string {
  const c = globalThis.crypto

  if (c && typeof c.randomUUID === 'function') {
    return c.randomUUID()
  }

  const bytes = new Uint8Array(16)
  if (c && typeof c.getRandomValues === 'function') {
    c.getRandomValues(bytes)
  } else {
    for (let i = 0; i < bytes.length; i += 1) bytes[i] = Math.floor(Math.random() * 256)
  }

  // 按 RFC 4122 打上版本位与变体位，形状与服务端的 Guid 字符串一致。
  bytes[6] = (bytes[6] & 0x0f) | 0x40
  bytes[8] = (bytes[8] & 0x3f) | 0x80

  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

export function newAction(): ButtonAction {
  return {
    id: newId(),
    name: '',
    protocol: 'UDP',
    targetIp: '192.168.1.100',
    targetPort: 8080,
    dataType: 'String',
    dataContent: 'Hello',
    delayMs: 0,
    pairingCode: '',
    pairingCodeType: 'String',
  }
}

export function newMonthDaySchedule(): MonthDaySchedule {
  return { month: 1, days: [] }
}

export function newMonthlyPlan(): MonthlyPlan {
  return {
    id: newId(),
    enabled: true,
    name: '计划',
    time: '',
    times: ['08:00'],
    months: [],
    days: [],
    monthDays: [newMonthDaySchedule()],
  }
}

export function newButton(order: number): ButtonConfig {
  return {
    id: newId(),
    order,
    label: '按钮',
    pairingCode: '',
    pairingCodeType: 'String',
    buttonColor: '#512DA8',
    textColor: '#FFFFFF',
    nameFontSize: 13,
    buttonImage: '',
    width: 140,
    height: 80,
    layoutX: 0,
    layoutY: 0,
    actions: [],
    timerEnabled: false,
    timerMode: 'Daily',
    timerTimes: ['08:00'],
    timerDays: [1, 2, 3, 4, 5],
    monthlyPlans: [],
  }
}

/**
 * 一个新的组合按钮（2026-10-15，需求⑤）。
 *
 * <p>默认<b>不含任何成员</b>：成员是"引用哪些已有主按钮"，没有合理的默认值可猜 ——
 * 猜第一个按钮会比空着更让人困惑（用户看到执行了一个他没选过的按钮）。</p>
 */
export function newComboButton(order: number): ComboButton {
  return {
    id: newId(),
    order,
    label: '组合按钮',
    pairingCode: '',
    pairingCodeType: 'String',
    buttonColor: '#512DA8',
    textColor: '#FFFFFF',
    nameFontSize: 13,
    buttonImage: '',
    width: 140,
    height: 80,
    layoutX: 0,
    layoutY: 0,
    steps: [],
  }
}

export function newSubButton(): SubButton {
  return {
    id: newId(),
    label: '子按钮',
    buttonColor: '#00897B',
    textColor: '#FFFFFF',
    nameFontSize: 12,
    buttonImage: '',
    width: 90,
    height: 90,
    pairingCode: '',
    pairingCodeType: 'UTF-8',
    actions: [],
  }
}

export function newSubButtonSet(order: number): SubButtonSet {
  return {
    id: newId(),
    order,
    label: '子按钮集',
    buttonColor: '#00695C',
    textColor: '#FFFFFF',
    nameFontSize: 13,
    buttonImage: '',
    width: 140,
    height: 80,
    layoutX: 0,
    layoutY: 0,
    targetIp: '192.168.1.100',
    targetPort: 8080,
    subButtons: [],
  }
}
