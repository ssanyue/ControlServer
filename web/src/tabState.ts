/**
 * 顶栏页签（`App.vue` 的 `activeTab`）的**持久化**。
 *
 * <p>═══════════════════════════════════════════════════════════════</p>
 * <p>🔥 <b>为什么需要这个模块</b></p>
 *
 * <p>
 * `activeTab` 原本只是 `App.vue` 里一个内存 `ref`，初值写死 `'console'`。
 * 而**浏览器刷新就是"整页重建"** —— Vue 应用连同所有 `ref` 一起重来，
 * 于是用户无论在「远程桌面」还是「指令库」，按一下 F5 就被扔回控制台。
 * </p>
 *
 * <p>
 * 这和"点了没反应"是同一类毛病：**界面把用户的位置弄丢了**。
 * 在展厅场景下尤其刺眼 —— 操作员正远程看着某台机器，刷新一下画面就没了。
 * </p>
 *
 * <p>═══════════════════════════════════════════════════════════════</p>
 * <p>⚖️ <b>为什么是 sessionStorage，而不是 localStorage / URL hash</b></p>
 *
 * <p>
 * · <b>`localStorage`</b>：**跨标签页共享**。开两个标签分别停在「控制台」和「用户」，
 *   后者的写入会把前者的记录顶掉，刷新时两边打架；而"上次看的是哪一页"本来就属于
 *   **这一次浏览**，跨会话记住反而像是界面自己乱跳。共享的展厅机上尤其糟。
 * </p>
 *
 * <p>
 * · <b>URL hash（`#/remote`）</b>：能力更强 —— 可分享、可书签、后退键能返回上一页；
 *   但要额外接管 `hashchange`、非法值、以及<b>非管理员拿到 `#/remote` 链接</b>的回落，
 *   改动面大一圈。本次诉求只有"刷新别丢页签"一条，不值得为它引入一套路由。
 *   将来若真要前进/后退与分享，再整体换成 hash，`TabId` 与 `TAB_IDS` 可以直接复用。
 * </p>
 *
 * <p>
 * · <b>`sessionStorage`</b>：**按标签页隔离、随标签页消失**，语义精确对上"这一次浏览"。
 * </p>
 *
 * <p>
 * ⚠️ 所以这条记录<b>不该被当成用户偏好</b>（"这人喜欢从远程页开始"）—— 它是<b>位置</b>，
 * 不是设置。关掉标签页就该忘掉。
 * </p>
 *
 * <p>═══════════════════════════════════════════════════════════════</p>
 * <p>🧱 <b>纪律：本模块不摸 `window`</b></p>
 *
 * <p>
 * 存储由调用方<b>注入</b>（{@link TabStorage} 是本模块自己声明的最小契约）。
 * 理由与 `vitest.config.ts` 里那条一致：本仓的纯逻辑模块必须能在 `environment: 'node'`
 * 下被直接喂数据验证 —— 一旦这里写死 `window.sessionStorage`，用例就得靠
 * `vi.stubGlobal` 去伪造全局，等于把"能不能测"绑在运行时上。
 * </p>
 *
 * <p>
 * `null` 是<b>合法入参</b>：隐私模式 / 配额满 / 被策略禁用时取不到 storage，
 * 一律按"没有记录"处理 —— 绝不因为存储不可用而让页面打不开
 * （与登录页"记住账号密码"那条取舍同源，见 `App.vue` 的 `REMEMBER_KEY`）。
 * </p>
 */

/** 顶栏页签 id。**与 `App.vue` 模板里的 `v-else-if` 分支一一对应。** */
export type TabId =
  | 'console'
  | 'edit'
  | 'timers'
  | 'commands'
  | 'agents'
  | 'files'
  | 'remote'
  | 'traffic'
  | 'ops'
  | 'license'
  | 'users'
  | 'settings'

/**
 * 全部合法页签。
 *
 * <p>
 * ⚠️ 这份数组是"读过校验"的<b>唯一依据</b>，不是可有可无的元数据：存储里的值可能是
 * <b>上一个版本</b>留下的（页签被改名 / 删除），也可能被人手工改过。
 * <b>不认识的值一律回落 `console`</b>，而不是"原样塞进 `activeTab`" ——
 * 后者在模板里不匹配任何分支，会掉进末尾那个 `v-else`（「设置」页），
 * 于是又变成一次"刷新跳错页"。
 * </p>
 */
export const TAB_IDS: readonly TabId[] = [
  'console',
  'edit',
  'timers',
  'commands',
  'agents',
  'files',
  'remote',
  'traffic',
  'ops',
  'license',
  'users',
  'settings',
]

/** 没记录 / 记录不可用时的落点。 */
export const DEFAULT_TAB: TabId = 'console'

/**
 * 需要管理员的页签。
 *
 * <p>
 * 服务端对 `/api/remote`（整个）与 `/api/users` 都是 AdminOnly，模板里的 `v-else-if`
 * 也带了 `&& isAdmin`；这里把同一条规则<b>提前到"读取后立刻收敛"</b>，
 * 理由见 {@link clampTabForRole}。
 * </p>
 */
export const ADMIN_ONLY_TABS: readonly TabId[] = ['remote', 'users']

/** 存储 key。与 `App.vue` 的 `REMEMBER_KEY`（`cs.remembered-login`）同一 `cs.` 前缀。 */
export const ACTIVE_TAB_KEY = 'cs.active-tab'

/**
 * 本模块需要的**最小**存储契约。
 *
 * <p>
 * 只声明用到的三个方法：真实实现直接传 `window.sessionStorage`，
 * 用例传一个内存对象即可 —— 不必实现 `Storage` 的一整套接口。
 * </p>
 */
export type TabStorage = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>

/** 认出"这是不是一个合法页签"。 */
export function isTabId(value: unknown): value is TabId {
  return typeof value === 'string' && (TAB_IDS as readonly string[]).includes(value)
}

/**
 * 读取上次停留的页签。
 *
 * @param storage 存储；`null` / `undefined` ＝ 取不到（隐私模式等），按"没有记录"处理。
 */
export function readActiveTab(storage: TabStorage | null | undefined): TabId {
  if (!storage) return DEFAULT_TAB

  try {
    const raw = storage.getItem(ACTIVE_TAB_KEY)
    return isTabId(raw) ? raw : DEFAULT_TAB
  } catch {
    // Safari 隐私模式下 `getItem` 本身就可能抛 —— 与"没记录"等价处理。
    return DEFAULT_TAB
  }
}

/** 记下当前页签。存储不可用时**静默降级**为"本次不记住"，绝不打断页签切换。 */
export function persistActiveTab(storage: TabStorage | null | undefined, tab: TabId): void {
  if (!storage) return

  try {
    storage.setItem(ACTIVE_TAB_KEY, tab)
  } catch {
    /* 配额满 / 被禁用：忽略 */
  }
}

/**
 * 抹掉记录。
 *
 * <p>
 * <b>登出时用</b>：免得换个账号登进来，还停在别人离开的那一页
 * （若那一页恰好是「用户」/「远程」，还会再触发一次权限收敛）。
 * </p>
 */
export function clearActiveTab(storage: TabStorage | null | undefined): void {
  if (!storage) return

  try {
    storage.removeItem(ACTIVE_TAB_KEY)
  } catch {
    /* 同上 */
  }
}

/**
 * 按角色收敛页签。
 *
 * <p>
 * 🔥 <b>必须在"用户信息到手之后"再调一次</b>，且这一步不能省：
 * {@link readActiveTab} 跑在 `setup` 里，那时 `user` 还是 `null`（`me()` 尚未返回），
 * <b>根本无从判断权限</b>；所以它只看"值合不合法"，不看"这个人配不配"。
 * </p>
 *
 * <p>
 * 后果是：管理员停在「远程桌面」刷新后，若换成操作员登录（或同一标签页换了人用），
 * 会带着 `remote` 进入渲染 —— 模板里 `activeTab === 'remote' && isAdmin` 不成立，
 * <b>末尾兜底的 `v-else` 会把它悄悄送进「设置」页</b>。那同样是"跳错页"，
 * 只是从"跳回控制台"换成了"跳去设置"，都不该发生。
 * </p>
 *
 * @param tab 读到的页签。
 * @param isAdmin 当前登录用户是否管理员。
 */
export function clampTabForRole(tab: TabId, isAdmin: boolean): TabId {
  if (isAdmin) return tab
  return ADMIN_ONLY_TABS.includes(tab) ? DEFAULT_TAB : tab
}
