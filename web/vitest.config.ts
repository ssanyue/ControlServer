import { defineConfig } from 'vitest/config'

/**
 * 前端用例配置（M5.4-b 第 8 步新引入）。
 *
 * <p>
 * 🔥 **刻意不加载 `@vitejs/plugin-vue`**：这些用例的目标是"**能抽成纯函数**的编排逻辑"，
 * 它们本来就不该依赖组件（`.vue`）或浏览器 API。不加载插件等于顺带钉住这条约束 ——
 * 哪天有人把纯函数挪进组件里、或让被测模块去摸 `window`（用例里那几处 `vi.stubGlobal`
 * 是**显式**的），用例会立刻红。
 * </p>
 *
 * <p>
 * ⚠️ `environment: 'node'` 而不是 `jsdom`：H.264 解码编排里凡是碰浏览器的地方
 * （`VideoDecoder` / `CanvasImageSource` / 解码器工厂）都必须由调用方注入，
 * 于是纯函数部分完全不需要 DOM。用 `node` 是"把这条纪律写进配置"。
 * </p>
 */
export default defineConfig({
  // ⚠️ 缓存目录**放在项目内**。默认会落到系统 temp，而在受限环境（沙箱 / 受管构建机）里
  //    写那里可能被拒 —— 表现是 `EPERM ... Temp\...\ssr\...`，**而且它会连累用例文件的收集**。
  cacheDir: 'node_modules/.vitest',

  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],

    /**
     * 🔥 **必须单进程跑** —— 这不是性能调优，是**正确性**。
     *
     * <p>
     * vitest 会把每个"被转换过的模块"写进一个**临时文件**再 import。
     * 多进程并行时，同一批模块的写入会撞在一起；在 Windows 上这表现为
     * `EPERM: operation not permitted, open '…\ssr\<sha1>'`。
     * </p>
     *
     * <p>
     * 🔴 <b>真正危险的不是那个报错，而是它的后果</b>：这个错误发生在**收集阶段**，
     * 于是"4 个用例文件只跑了 1～3 个"，而输出写的仍然是
     * <b>`Test Files 3 passed` / `Tests 48 passed` / `Errors 1`</b> ——
     * 一次<b>假绿</b>。本仓对"假 PASS"的态度见铁律 9（"假 FAIL 比不做断言更坏"）；
     * 这里更坏一档：它连"少测了东西"都不说。
     * </p>
     *
     * <p>
     * 实测：`--maxWorkers=1 --minWorkers=1` 连跑三次都是 4/4 文件、58/58 用例。
     * 总共约 2 秒，并行带来的收益本来也接近零。
     * </p>
     */
    pool: 'forks',
    poolOptions: { forks: { singleFork: true } },
  },
})
