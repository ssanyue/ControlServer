/**
 * **观看端的编码能力探测**（M5.4-b §8 第 7 步立，第 8 步补真探测）。
 *
 * <p>
 * 中心侧的协商是「浏览器 ∩ 被控端 ∩ 分辨率」，而本文件负责回答其中的**第二项**里
 * 属于观看端的那一半。
 * </p>
 *
 * <h2>🔴 本文件里最容易搞错的一件事：能力 ≠ 显示得了</h2>
 *
 * <p>
 * `VideoDecoder.isConfigSupported('avc1.640032')` 返回真，只说明**这个浏览器**能解 H.264。
 * 它**不说明**"我们这个页面会把 H.264 画到屏幕上" —— 那是本页渲染器的事。
 * </p>
 *
 * <p>
 * 🔥 第 8 步之前这一点是**致命**的：那时本页只有 MJPEG 这条路，按浏览器能力声明
 * 会让中心挑 H.264、被控端开硬编，而本页拿到 Annex-B 码流之后什么都不做 ⇒
 * <b>画面不出来，且两边日志全正常</b>。
 * </p>
 *
 * <p>
 * ⇒ 现在 {@link WEBCODECS_RENDERER_READY} 已经翻成 `true`（`webCodecsDecoder.ts` 接线完成），
 * 所以"浏览器支持"与"本页会去解"这两个条件**第一次同时成立**。
 * ⚠️ 但它**仍然是两个条件**，不许合并成一句判断：哪天渲染路径被摘掉或回滚，
 * 这个常量必须先回到 `false`，否则黑屏会以"两边日志全正常"的方式重现。
 * </p>
 *
 * <h2>🔴 探测是**异步**的，而且结论只是"大致"</h2>
 *
 * <p>
 * `isConfigSupported` 返回 `Promise` ⇒ 探测天然异步（第 7 步那个同步版本没法做真探测）。
 * </p>
 *
 * <p>
 * 更重要的是：`isConfigSupported` 要**逐字**给 codec 串，而真串（例如 `avc3.640032`）
 * 是从**首帧 SPS** 里反推出来的 —— 那要等第一帧到，而探测的意义恰恰是"建之前决定要不要建"。
 * ⇒ 所以这里试的是**本机已知的那一档**（本机 H.264 是 High@5.0），
 * 拿到的是"**大致支持/不支持**"，**绝不能当成"配置一定对"**。
 * 真正的 `configure()` 串一律以**被控端帧头上的 `codecString`** 为准（见 `webCodecsDecoder.ts`）。
 * </p>
 */

import { REMOTE_CODECS, type RemoteViewerVideoCapabilities } from './types'

/**
 * 本页的 **WebCodecs 渲染路径**是否已经接线。
 *
 * <p>
 * 🔥 **第 8 步把它翻成 `true`**：`webCodecsDecoder.ts` ＋ 解码器注入已经落地，
 * 本页第一次真的会把 H.264 画到 canvas 上。
 * </p>
 *
 * <p>
 * ⚠️ **不是**"浏览器支不支持" —— 那是运行时的 {@link hasWebCodecs}。
 * 两者必须同时成立，本页才敢声明 H.264 / HEVC；本常量表达的是**本页这一侧**那一半。
 * </p>
 */
export const WEBCODECS_RENDERER_READY = true

/** 本页要求的封装。 */
export const VIEWER_PACKAGING = {
  /** 参数集在码流里（Annex-B）⇒ codec 串用 `avc3` / `hev1`，不带 `description`。 */
  inband: 'inband',
  /** 参数集随帧头 ⇒ codec 串用 `avc1` / `hvc1`，带 `description`。 */
  outofband: 'outofband',
} as const

/**
 * 探测用的**候选** codec 串（两条前缀都试）。
 *
 * ⚠️ 它们不是"真串"，只是本机已知的那一档（High@5.0 ⇒ `6 4 0 0 3 2`）。
 * 用途只有一件：问浏览器"**这类**串你解不解得了"。
 */
const H264_IN_BAND_CANDIDATE = 'avc3.640032'
const H264_OUT_OF_BAND_CANDIDATE = 'avc1.640032'
const HEVC_IN_BAND_CANDIDATE = 'hev1.1.6.L93.B0'
const HEVC_OUT_OF_BAND_CANDIDATE = 'hvc1.1.6.L93.B0'

/** 探测结果（比 `RemoteViewerVideoCapabilities` 多两句人话，供横幅与诊断用）。 */
export interface ViewerProbeResult {
  /** 直接进建会话请求的 `viewerVideo` 字段。 */
  capabilities: RemoteViewerVideoCapabilities
  /** 一句话说明"为什么声明成这样"（横幅直接显示）。 */
  summary: string
  /**
   * ⚠️ 为真 = 这个结论是**大致**得出的（探测时拿不到准确的描述符，见文件头）。
   * 为真时**不许**把结论当成"配置一定对"来读。
   */
  advisory: boolean
}

/** 运行时：这个上下文里有没有 WebCodecs 的解码器。 */
export function hasWebCodecs(): boolean {
  // ⚠️ 非安全上下文里 `VideoDecoder` **是 undefined，不是报错** ——
  //    所以这一句必须用 `typeof` 判，而不是 try/catch 去 new 一个。
  return typeof VideoDecoder !== 'undefined'
}

/** 只声明 MJPEG（没有 WebCodecs / 探测整体失败时的兜底）。 */
export function mjpegOnlyResult(summary: string): ViewerProbeResult {
  return {
    capabilities: {
      codecs: [REMOTE_CODECS.mjpeg],
      declared: true,
      // ⚠️ MJPEG 没有参数集，封装对它**不适用**；这里仍然给出中心最终会用的那个值
      //    （带内），是为了"探测成功之后"不需要在别处再改一次。
      packaging: VIEWER_PACKAGING.inband,
    },
    summary,
    advisory: false,
  }
}

/**
 * 探测本页**显示得了哪些格式**。
 *
 * <p>
 * 返回值直接进建会话请求的 `viewerVideo` 字段。三种结果对应中心侧三种完全不同的处置：
 * </p>
 *
 * <table>
 *   <tr><th>结果</th><th>中心侧的处置</th></tr>
 *   <tr>
 *     <td><code>['mjpeg']</code></td>
 *     <td>正常：这一路走 MJPEG</td>
 *   </tr>
 *   <tr>
 *     <td><code>['h264','mjpeg']</code></td>
 *     <td>正常：中心取交集，优先 H.264</td>
 *   </tr>
 *   <tr>
 *     <td>**不发**这个字段（或 <code>declared: false</code>）</td>
 *     <td>中心按带内 H.264 回落，并留一条警告</td>
 *   </tr>
 * </table>
 *
 * <p>
 * ⚠️ <b>本函数**不抛**</b>：探测失败不是"这种情况没法处理"，
 * 而是一个**正常结果**（老浏览器、不安全上下文）。抛出去会让调用方
 * 把"探测没做成"变成"建会话失败"，而正确处置恰恰相反 —— 不声明、让中心回落。
 * </p>
 */
export async function probeViewerVideo(): Promise<ViewerProbeResult> {
  // MJPEG **永远**在集合里：它只需要 `<img>`，不需要 WebCodecs，也不需要安全上下文。
  // ⇒ 只要本页的 MJPEG 那条路还在，就**不可能**出现"一种都显示不了"。
  const codecs: string[] = [REMOTE_CODECS.mjpeg]

  if (!WEBCODECS_RENDERER_READY) {
    return mjpegOnlyResult(
      '本页的 WebCodecs 渲染路径**未接线** ⇒ 只声明 MJPEG（声明"能解"却不去解 = 一块黑屏）',
    )
  }

  if (!hasWebCodecs()) {
    return mjpegOnlyResult(
      '这个页面不在安全上下文里（或浏览器没有 WebCodecs）⇒ `VideoDecoder` 不存在，只声明 MJPEG',
    )
  }

  // ── 先试带内（`avc3`）──
  //
  // 🔥 顺序**不能反**：带内是第 6/7 步的**默认**，而且热路径上零转换
  //    （码流原样转发，不做 Annex-B → 长度前缀的改写）。带外只在"带内确实不行"时才要。
  let packaging: string = VIEWER_PACKAGING.inband
  let advisory = false

  const inBand = await isSupported(H264_IN_BAND_CANDIDATE)

  if (inBand) {
    codecs.push(REMOTE_CODECS.h264)
  } else {
    // 🔥 带内不行 ⇒ 试带外。这是**唯一**能让"Chromium 不认 `avc3` 这个前缀"时
    //    还用得上硬编的路（§3.4.2 明确要求"两条前缀都试，用返回值定，不靠推理"）。
    //
    // ⚠️ 但这一问是**没有描述符**的情况下问的，而真实的带外配置**必须**带 `avcC`
    //    ⇒ 结论只是"大致"，{@link ViewerProbeResult.advisory} 会置真。
    const outOfBand = await isSupported(H264_OUT_OF_BAND_CANDIDATE)

    if (outOfBand) {
      codecs.push(REMOTE_CODECS.h264)
      packaging = VIEWER_PACKAGING.outofband
      advisory = true
    }
  }

  // ── HEVC ──
  //
  // 🔥 声明 HEVC 的唯一意义是：屏幕 **> 4096** 时它**是硬编的唯一出路**
  //    （H.264 硬编封顶 4096）。而中心的偏好顺序是 H.264 > HEVC > MJPEG
  //    ⇒ 声明 HEVC **不会**让普通的 1440p/4K 会话跑去用 HEVC。
  //
  // ⚠️ 残余风险（**明确记着，不掩盖**）：这里试的是候选串，而真串由被控端的
  //    HEVC 编码器决定 ⇒ 探测通过**不等于**真 configure 会通过。本机屏只有
  //    2160×1440，> 4096 那条路**物理上验不了** ⇒ 留给第 9 步 / M5.4-c。
  if (codecs.includes(REMOTE_CODECS.h264)) {
    const hevcInBand = await isSupported(HEVC_IN_BAND_CANDIDATE)

    if (hevcInBand) codecs.push(REMOTE_CODECS.hevc)
    else if (await isSupported(HEVC_OUT_OF_BAND_CANDIDATE)) codecs.push(REMOTE_CODECS.hevc)
  }

  const summary = describeDeclaration(codecs, packaging, advisory)

  return {
    capabilities: { codecs, declared: true, packaging },
    summary,
    advisory,
  }
}

/**
 * 问一次浏览器"这个配置你解不解得了"。
 *
 * ⚠️ **不带 `description`**：探测阶段拿不到真的 `avcC`/`hvcC`
 * （它要从首帧 SPS 反推），而**手写一个"看起来对"的**正是本仓反复警告的事 ——
 * 错串的表现是"能力探测说支持、真解却失败"，把人引到浏览器版本上去。
 * ⇒ 于是这一问只对**带内**（`avc3`/`hev1`，本来就不需要描述符）是**准确**的，
 * 对带外（`avc1`/`hvc1`）只是**大致**的 —— 调用方据此置 `advisory`。
 */
async function isSupported(codec: string): Promise<boolean> {
  try {
    const support = await VideoDecoder.isConfigSupported({
      codec,
      optimizeForLatency: true,
    })

    return support.supported === true
  } catch {
    // 串形不合法 / 浏览器抛错：一律当"不支持"。
    // ⚠️ 这里**不抛**：探测失败是一个正常结果，不是异常（见 `probeViewerVideo` 的说明）。
    return false
  }
}

/**
 * 把探测结论拼成**补充说明**（横幅用）。
 *
 * ⚠️ 它**不重复**格式清单 —— 那一份由页面上的 `viewerVideoDeclaration` 显示，
 * 而两处都列一遍，迟早会出现"同一件事在页面上有两个不一样的写法"。
 * 这里只说"**为什么**是这样"。
 */
function describeDeclaration(codecs: readonly string[], packaging: string, advisory: boolean): string {
  if (codecs.length === 1) {
    return '⚠️ 这个浏览器/上下文里没有可用的 WebCodecs 解码器 ⇒ 这一路只能走 MJPEG 软编（这不代表浏览器不支持硬编格式）'
  }

  const outOfBand = packaging === VIEWER_PACKAGING.outofband
  const detail = outOfBand
    ? '（`avc1` ＋ 帧头里的 avcC；带内那条前缀这个浏览器不认）'
    : '（`avc3`，码流一字节不改）'

  if (advisory) {
    return `封装请求「${outOfBand ? '带外' : '带内'}」${detail} —— ⚠️ 这个结论是**大致**的（探测时拿不到描述符），真实的 configure 仍以被控端给的 codec 串为准`
  }

  return `封装请求「${outOfBand ? '带外' : '带内'}」${detail}`
}
