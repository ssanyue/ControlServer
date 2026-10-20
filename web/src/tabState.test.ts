/**
 * `tabState` 的用例。
 *
 * <p>
 * 🔥 被测的中心规则有两条，各有一座"山"：
 * </p>
 * <ul>
 *   <li><b>读到的值必须过白名单</b> —— 放行不认识的值，页面会掉进模板末尾的
 *       `v-else`（「设置」页），症状与本次要修的 bug 一模一样，只是换了个目标页。</li>
 *   <li><b>存储不可用时不许抛</b> —— 隐私模式 / 配额满下 `sessionStorage` 能拿到但
 *       一用就抛；这里若把异常漏出去，<b>整个 `setup` 会中断、页面全白</b>。
 *       这比"刷新丢页签"严重得多，所以三处（读 / 写 / 清）各钉一条。</li>
 * </ul>
 *
 * <p>
 * ⚠️ 用例<b>不搭 DOM</b>：`TabStorage` 由本文件自己造一个内存实现注入，
 * 因此这套用例在 `vitest` 的 `environment: 'node'` 下直接可跑 —— 不依赖 `jsdom`，
 * 也不需要 `vi.stubGlobal('window', …)`。
 * </p>
 */

import { describe, expect, it } from 'vitest'

import {
  ACTIVE_TAB_KEY,
  DEFAULT_TAB,
  TAB_IDS,
  clampTabForRole,
  clearActiveTab,
  isTabId,
  persistActiveTab,
  readActiveTab,
  type TabId,
  type TabStorage,
} from './tabState'

/** 一个最小的内存 storage —— 只实现本模块用到的三个方法。 */
function memoryStorage(seed?: Record<string, string>): TabStorage {
  const map = new Map<string, string>(Object.entries(seed ?? {}))

  return {
    getItem: (key) => map.get(key) ?? null,
    setItem: (key, value) => {
      map.set(key, value)
    },
    removeItem: (key) => {
      map.delete(key)
    },
  }
}

/** 一个"一碰就抛"的 storage —— 模拟 Safari 隐私模式。 */
function throwingStorage(): TabStorage {
  return {
    getItem: () => {
      throw new Error('SecurityError')
    },
    setItem: () => {
      throw new Error('QuotaExceededError')
    },
    removeItem: () => {
      throw new Error('SecurityError')
    },
  }
}

describe('readActiveTab', () => {
  it('没有记录 ⇒ 回到控制台（默认落点）', () => {
    expect(readActiveTab(memoryStorage())).toBe('console')
  })

  it('🔴 每一个合法页签都能原样往返 —— 白名单不能漏项', () => {
    // 漏掉一个的后果很隐蔽：那个页签**单独**刷新会回控制台，别的页签却正常，
    // 看起来像"偶发"。所以这里对全表逐个验，而不是挑两个代表。
    for (const tab of TAB_IDS) {
      const storage = memoryStorage()
      persistActiveTab(storage, tab)
      expect(readActiveTab(storage)).toBe(tab)
    }
  })

  it('🔴 不认识的值 ⇒ 回落控制台，而不是原样返回', () => {
    // 三种来源：旧版本删掉的页签、手工改过的值、被当成"键"用的原型属性名。
    for (const garbage of ['nope', '', 'Console', '__proto__', 'constructor', 'REMOTE']) {
      expect(readActiveTab(memoryStorage({ [ACTIVE_TAB_KEY]: garbage }))).toBe(DEFAULT_TAB)
    }
  })

  it('storage 取不到（null / undefined）⇒ 控制台，且不抛', () => {
    expect(readActiveTab(null)).toBe('console')
    expect(readActiveTab(undefined)).toBe('console')
  })

  it('🔴 getItem 抛异常 ⇒ 控制台，且异常不漏出去', () => {
    // 漏出去的后果是 `setup` 中断 ⇒ **整页白屏**，比丢页签严重一档。
    expect(() => readActiveTab(throwingStorage())).not.toThrow()
    expect(readActiveTab(throwingStorage())).toBe(DEFAULT_TAB)
  })
})

describe('persistActiveTab', () => {
  it('写进去能读回来', () => {
    const storage = memoryStorage()

    persistActiveTab(storage, 'remote')

    expect(readActiveTab(storage)).toBe('remote')
  })

  it('后写覆盖先写（同一标签页内的正常切换）', () => {
    const storage = memoryStorage()

    persistActiveTab(storage, 'edit')
    persistActiveTab(storage, 'files')

    expect(readActiveTab(storage)).toBe('files')
  })

  it('🔴 setItem 抛 / storage 取不到 ⇒ 静默降级，不打断页签切换', () => {
    expect(() => persistActiveTab(throwingStorage(), 'ops')).not.toThrow()
    expect(() => persistActiveTab(null, 'ops')).not.toThrow()
  })
})

describe('clearActiveTab', () => {
  it('清掉之后回到控制台', () => {
    const storage = memoryStorage()
    persistActiveTab(storage, 'users')

    clearActiveTab(storage)

    expect(readActiveTab(storage)).toBe(DEFAULT_TAB)
  })

  it('清理一个本来就没有的记录不报错', () => {
    expect(() => clearActiveTab(memoryStorage())).not.toThrow()
  })

  it('🔴 removeItem 抛 / storage 取不到 ⇒ 不抛（登出不能被存储问题卡住）', () => {
    expect(() => clearActiveTab(throwingStorage())).not.toThrow()
    expect(() => clearActiveTab(null)).not.toThrow()
  })
})

describe('clampTabForRole', () => {
  it('🔴 非管理员停在「远程」/「用户」⇒ 回落控制台', () => {
    // 不收敛的话，模板里的 `&& isAdmin` 不成立，会掉进末尾 `v-else` 的「设置」页 ——
    // 又是一次"跳错页"，只是目标从控制台换成了设置。
    expect(clampTabForRole('remote', false)).toBe(DEFAULT_TAB)
    expect(clampTabForRole('users', false)).toBe(DEFAULT_TAB)
  })

  it('非管理员停在普通页签 ⇒ 不受影响', () => {
    for (const tab of ['edit', 'files', 'license', 'settings'] as TabId[]) {
      expect(clampTabForRole(tab, false)).toBe(tab)
    }
  })

  it('管理员 ⇒ 一律原样放行（含受限页）', () => {
    for (const tab of TAB_IDS) {
      expect(clampTabForRole(tab, true)).toBe(tab)
    }
  })
})

describe('isTabId', () => {
  it('只认全表里的字符串', () => {
    expect(isTabId('console')).toBe(true)
    expect(isTabId('remote')).toBe(true)
    expect(isTabId('console2')).toBe(false)
    expect(isTabId('')).toBe(false)
    expect(isTabId(null)).toBe(false)
    expect(isTabId(7)).toBe(false)
  })
})
