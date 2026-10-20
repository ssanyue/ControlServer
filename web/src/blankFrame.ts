/**
 * 「这一帧几乎是一整片纯色」的判据（M7.6）。
 *
 * <h2>🔴 它存在的原因：一个被误判过两轮的现场形态</h2>
 *
 * <p>
 * 症状是<b>画面全黑，但被控端有人动一下鼠标就恢复</b>，而两端日志干干净净。
 * 前两轮分别怀疑过抓屏坏了、编码器坏了 —— 都不是。真因是没人碰的机器
 * 静置之后按电源计划<b>关掉了显示器</b>（或起了黑屏屏保），而 DDA 这时照样"抓得到"，
 * 抓到的是一整片黑，于是帧在流、码率正常、画面全黑。
 * </p>
 *
 * <p>
 * ⇒ 这一层要做的事只有一件：<b>把"这张画面几乎没有内容"这件事如实说出来</b>，
 * 别让页面上只剩一块黑、什么解释都没有。
 * </p>
 *
 * <h2>🔑 判据为什么是"每像素字节数"，而不是"帧小于多少 KB"</h2>
 *
 * <p>
 * 绝对字节数依赖分辨率：同一个纯色画面，1080p 的 IDR 约 1 KB、4K 约 3 KB、
 * 8K 就能到 10 KB —— 写死一个"小于 30 KB"的阈值，在 8K 上会把正常画面判成纯色。
 * 而 H.264 的<b>每像素字节数</b>在同一内容下几乎与分辨率无关，所以它才是可比的那个数。
 * </p>
 *
 * <p>
 * 实测锚点（同一套代码、同为桌面推流、同为 H.264 IDR）：
 * </p>
 *
 * <table>
 *   <tr><th>画面</th><th>IDR 字节</th><th>像素</th><th>bpp</th></tr>
 *   <tr><td>正常桌面（2160×1440，本机）</td><td>101341</td><td>3.11 MPix</td><td><b>0.0326</b></td></tr>
 *   <tr><td>被控端那片黑（3840×2160）</td><td>2457</td><td>8.29 MPix</td><td><b>0.0003</b></td></tr>
 * </table>
 *
 * <p>
 * 两者差 <b>100 倍</b>，阈值取中间的 {@link BLANK_BPP_THRESHOLD} 有足够余量。
 * </p>
 *
 * <h2>⚠️ 它只能回答"画面是不是纯色"，不能回答"为什么纯色"</h2>
 *
 * <p>
 * 纯色可能是<b>黑屏（关显示器 / 屏保 / 全屏黑窗口）</b>，也可能是
 * <b>纯白壁纸、全屏单色应用、正在放 PPT 的空白页</b>。这两类对处置是相反的：
 * 前者要现场处理，后者什么都不用做。
 * </p>
 *
 * <p>
 * ⇒ 所以文案必须<b>把两种可能都写出来</b>，而不是替用户下结论。
 * 替用户猜"你一定息屏了"，就会在"人家壁纸是纯色"的机器上变成一条甩不掉的假告警，
 * 而假告警的代价是真告警被忽略。
 * </p>
 */

/**
 * 关键帧的「每像素字节数」低于这个值 ⇒ 画面基本是一整片纯色。
 *
 * <p>
 * 0.002 的含义：8.3 MPix 的 4K 画面 ≤ 16.6 KB，3.1 MPix 的 2160×1440 ≤ 6.2 KB。
 * 正常桌面在 0.03 量级（见文件头的实测锚点），所以这个门槛离"真画面"有 15 倍余量，
 * 离"那片黑"有 6 倍余量。
 * </p>
 */
export const BLANK_BPP_THRESHOLD = 0.002

/**
 * 低于这个像素数就不判 —— 太小的画面（分块到碎、或异常帧）里，
 * bpp 会被帧头的固定开销带跑，判出来的东西没有意义。
 */
const MIN_PIXELS_TO_JUDGE = 160 * 120

/**
 * 估算 base64 字符串对应的字节数（不解码）。
 *
 * <p>
 * ⚠️ 故意不调 <c>base64ToBytes</c>：那个会真的分配一份解码后的 buffer，
 * 而这里只想知道"多大"。为了一次读数去解 300 KB 的载荷，是拿帧率换一句话。
 * </p>
 */
export function base64ByteLength(encoded: string): number {
  if (!encoded) return 0

  const length = encoded.length

  if (length < 2) return 0

  let padding = 0

  if (encoded.endsWith('==')) padding = 2
  else if (encoded.endsWith('=')) padding = 1

  // base64 每 4 个字符 3 字节。
  const bytes = Math.floor((length * 3) / 4) - padding

  return bytes > 0 ? bytes : 0
}

/**
 * 这一帧是不是"几乎一整片纯色"。
 *
 * @param bytes  载荷字节数（关键帧）。
 * @param pixels 这一帧的像素数（宽 × 高）。
 */
export function isBlankKeyframe(bytes: number, pixels: number): boolean {
  if (!Number.isFinite(bytes) || !Number.isFinite(pixels)) return false
  if (bytes <= 0 || pixels <= 0) return false
  if (pixels < MIN_PIXELS_TO_JUDGE) return false

  return bytes / pixels <= BLANK_BPP_THRESHOLD
}

/** 把字节数写成人读的样子（300 KB / 2.4 KB / 980 字节）。 */
function fmtBytes(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KB`

  return `${Math.round(bytes)} 字节`
}

/**
 * 给页面的那句提示；不值得提示时返回 `null`。
 *
 * <p>
 * 🔥 <b>为什么这句话必须同时给出两种解释</b>：见文件头最后一段 ——
 * 纯色既可能是"息屏了"，也可能是"人家就是一张单色壁纸"。
 * 只写前者会在后者上变成甩不掉的假告警；只写后者则会漏掉真故障。
 * </p>
 *
 * @param bytes  最近一个**关键帧**的载荷字节数。
 * @param pixels 这一帧的像素数。
 * @param width  画面宽（只用于文案）。
 * @param height 画面高（只用于文案）。
 */
export function describeBlankFrame(
  bytes: number,
  pixels: number,
  width: number,
  height: number,
): string | null {
  if (!isBlankKeyframe(bytes, pixels)) return null

  const bpp = bytes / pixels

  return (
    `这一帧几乎是纯色（关键帧只有 ${fmtBytes(bytes)}，` +
    `${width}×${height}，合 ${bpp.toFixed(4)} 字节/像素）。` +
    `如果现场那块屏是亮的，那它只是纯色壁纸 / 全屏单色窗口，不用管；` +
    `如果现场那块屏是黑的 —— 这台机器已经关闭显示器或正在跑黑屏屏保，` +
    `在被控端动一下鼠标就会恢复（我们这时才抓得到真实桌面）。`
  )
}
