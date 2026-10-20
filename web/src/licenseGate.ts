import { inject, ref, type InjectionKey, type Ref } from 'vue'

/**
 * 「核心能力已被授权闸门停摆」这一个布尔量，从 App 透传给**任意深度**的调用点。
 *
 * <p>
 * 为什么用 `provide`/`inject` 而不是逐层传 prop：
 * 需要知道这件事的地方分散在两处 —— 控制台（App 自己）与编辑页里的「试运行」
 * （`ActionEditor`，位于 App → EditPanel → ButtonForm → ActionListEditor → ActionEditor
 * 的第四层，子按钮那条甚至第五层）。为这一个只读布尔量，把 `licenseBlocked`
 * 一路穿过四个与授权毫无关系的组件，是典型的 prop 钻孔：每个中间组件都被迫认识一个
 * 它自己根本不用的概念。`provide`/`inject` 正是为这种"环境级"状态准备的。
 * </p>
 *
 * <p>
 * <b>这只是提前禁用 + 说明</b>。真正的判定始终在服务端 —— 被拦的端点会返回
 * `403 license-required`，绕过前端也进不去（闸门见 `LicenseGate`）。
 * </p>
 */
export const LICENSE_BLOCKED_KEY: InjectionKey<Ref<boolean>> = Symbol('license-blocked')

/**
 * 「为什么点不动」的统一文案。
 *
 * 与服务端 403 的 `message` 同源同义。放在这里而不是各组件里各写一份，
 * 是为了避免出现"控制台说没授权、编辑页说网络错误"这种自相矛盾的解释。
 */
export const LICENSE_BLOCKED_HINT =
  '本机尚未获得授权（或授权已过期）：动作执行、对接码监听与定时触发已停用。请到「授权」页导入授权文件。'

/**
 * 读取停摆状态。
 *
 * 默认返回一个恒为 `false` 的 ref：组件被单独挂载（测试 / 预览）时不必先 provide
 * 也能正常工作，不会因为少一个 provide 就抛错。
 */
export function useLicenseBlocked(): Ref<boolean> {
  return inject(LICENSE_BLOCKED_KEY, ref(false))
}
