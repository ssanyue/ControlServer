/**
 * **一帧画面该占多大**（M7.7）—— 纯函数，不碰 DOM。
 *
 * <h2>🔴 它存在的唯一理由：4K 只显示四分之一</h2>
 *
 * <p>
 * 现场报过"某些客户机的 4K 画面只显示四分之一"。这个症状的几何含义**只有一种**：
 * 画出来的矩形是它该占区域的一半宽、一半高（⇒ 面积 1/4，且贴着左上角）。
 * 而产生它的路径有两条，**表现完全相同、修法完全相反**：
 * </p>
 *
 * <table>
 *   <tr><th>哪一端缩了</th><th>帧内容的语义</th><th>该怎么做</th></tr>
 *   <tr>
 *     <td><b>编码端</b>把整幅缩放成一半再编</td>
 *     <td>内容仍然是<b>整屏</b>，只是分辨率低</td>
 *     <td>铺满区域（看着糊，但看得到全部）</td>
 *   </tr>
 *   <tr>
 *     <td><b>解码端</b>只解出了左上那一块</td>
 *     <td>内容<b>不是</b>整屏，剩下 3/4 根本不在码流里</td>
 *     <td>铺满只会把"看得见的一角"放大 ⇒ <b>必须如实说出来</b></td>
 *   </tr>
 * </table>
 *
 * <p>
 * ⇒ 所以本模块只做两件事，**不替谁下结论**：
 * </p>
 * <list type="number">
 *   <item>{@link pickDrawRect} —— 绘制**永远按区域尺寸铺满**。这不是"修复"，是把
 *         "画在左上角一小块"这个**必然产生误读**的默认行为拿掉；</item>
 *   <item>{@link describeSizeMismatch} —— 尺寸对不上时**把两个数都写出来**，
 *         让现场一眼看出是哪一端缩的，而不是继续猜。</item>
 * </list>
 *
 * <p>
 * 🔥 <b>为什么判据放在"区域 vs 解码"这一对上，而不是"屏幕 vs 解码"</b>：
 * 分块（M5.5）时每一个 tile 只占整幅的一条，拿它去和屏幕比会把**每一块都判成缩小**。
 * 而"这一块该占多大"是帧头里现成的（`regionX/regionY/width/height`），
 * 与屏幕尺寸无关 —— 单块时它恰好等于整幅。
 * </p>
 */

/** 宽高（像素）。 */
export interface PixelSize {
  width: number
  height: number
}

/**
 * 允许的"差几个像素"。
 *
 * <p>
 * 宏块对齐（H.264 是 16、MJPEG 是 8）会让 coded 尺寸比显示尺寸大几个像素，
 * 而 H.264 的 `displayWidth` 已经是裁剪过的 —— 两端都可能差 1–2 px。
 * 那是正常现象，**不该触发一条告警**（告警一旦常驻，真问题就淹了）。
 * </p>
 */
const TOLERANCE_PX = 2

/** 该把一个解码结果画进哪块矩形。 */
export interface DrawRect {
  x: number
  y: number
  width: number
  height: number
}

/**
 * **这一帧该画进哪块矩形**。
 *
 * <p>
 * 🔥 <b>尺寸恒取"区域"，不取"解码结果"</b>——这条规则本身就消灭了整类
 * "只显示四分之一 / 只显示一条"的缺陷：
 * </p>
 *
 * <p>
 * 原来的写法是 <c>drawImage(source, x, y, decoded.width, decoded.height)</c>：
 * 解码结果一旦小于它该占的区域（缩放过的编码、level 限制下的裁剪、下采样的硬解），
 * 画面就**静静地缩在左上角**，而页面上没有任何一处会说话。
 * 用区域尺寸画，同样的情形至少是"铺满但可能糊/可能只是放大了一角"，
 * 而 {@link describeSizeMismatch} 会同时把原因写出来。
 * </p>
 *
 * <p>
 * ⚠️ 区域信息缺失时（旧被控端不带 `frameWidth/regionX`）调用方会把区域设成
 * "整幅 = 这一帧"（见面板里 `tileGeometry` 的兜底），于是这里退化成
 * <b>与原行为完全等价</b>——不会把老客户端的画面拉变形。
 * </p>
 */
export function pickDrawRect(
  region: { x: number; y: number } & PixelSize,
  _decoded: PixelSize,
): DrawRect {
  return {
    x: region.x,
    y: region.y,
    // ⚠️ 不在这里判"要不要用 decoded"：只要区域可用，就用区域。
    //    判"差几个像素以内还是用 decoded"会多出一条分支，而那条分支的收益是
    //    "省一次无缩放拷贝"——浏览器对同尺寸 drawImage 本来就不重采样。
    width: region.width,
    height: region.height,
  }
}

/**
 * 解码尺寸与它该占的区域**对不上**时，给一句能定位的话；对得上返回 `null`。
 *
 * <p>
 * 🔥 返回的句子必须**同时包含两个数**。只写"画面被缩小了"等于没说 ——
 * 现场没法判断是"那台机器编码端缩了"还是"这个浏览器解不全"，
 * 而这两件事的下一步动作完全不同（前者要改被控端，后者要换浏览器/关硬解）。
 * </p>
 *
 * @param region 这一块该占的区域（帧头给的）。
 * @param decoded 实际解码出来的像素尺寸（`VideoFrame.displayWidth/Height` 或图片 natural 尺寸）。
 * @param screenWidth 整幅画面的宽（仅用于说明"这块是整幅的一部分"）。
 */
export function describeSizeMismatch(
  region: PixelSize,
  decoded: PixelSize,
  screenWidth?: number,
): string | null {
  if (region.width <= 0 || region.height <= 0) return null
  if (decoded.width <= 0 || decoded.height <= 0) return null

  const dw = Math.abs(decoded.width - region.width)
  const dh = Math.abs(decoded.height - region.height)

  if (dw <= TOLERANCE_PX && dh <= TOLERANCE_PX) return null

  const whole = screenWidth === undefined || Math.abs(screenWidth - region.width) <= TOLERANCE_PX

  // 比例取"更糟的那一维"：只缩了一个方向时，这个数才是现场真正看到的形变。
  const ratio = Math.min(decoded.width / region.width, decoded.height / region.height)
  const percent = Math.round(ratio * 1000) / 10

  const where = whole ? '整幅画面' : `这一块区域（${region.width}×${region.height}）`

  if (ratio < 1) {
    // ⚠️ "宽高各少一半"只对**等比缩小**成立。只缩一个方向时把它说出来，
    //    等于替现场编了一个不存在的形变 —— 而这句话正是他去量尺寸的依据。
    const shrunkW = decoded.width < region.width
    const shrunkH = decoded.height < region.height

    const shape =
      shrunkW && shrunkH
        ? '（宽高都只有该占的一半 ⇒ 看起来就是"只显示左上四分之一"）'
        : shrunkW
          ? '（只有宽度不够 ⇒ 看起来是"右边少一块"）'
          : '（只有高度不够 ⇒ 看起来是"下面少一块"）'

    return (
      `解码结果是 ${decoded.width}×${decoded.height}，`
      + `而${where}是 ${region.width}×${region.height} —— `
      + `只拿到 ${percent}%${shape}。`
      + '画面已按区域铺满，但内容本身只有这么多：'
      + '要么被控端把整幅缩小后再编码，要么这个浏览器只解出了左上那块。'
    )
  }

  return (
    `解码结果是 ${decoded.width}×${decoded.height}，`
    + `比${where}的 ${region.width}×${region.height} 还大 —— `
    + '画面已按区域铺满并裁掉多出来的部分（通常是编码器的宏块对齐填充）。'
  )
}

/**
 * 两块尺寸是不是**实质上不同**（差超过 {@link TOLERANCE_PX}）。
 *
 * <p>给"只在变了的时候才更新界面"用 —— 每帧都改一个 ref 会让面板每帧重渲染。</p>
 */
export function sizeDiffers(a: PixelSize, b: PixelSize): boolean {
  return (
    Math.abs(a.width - b.width) > TOLERANCE_PX
    || Math.abs(a.height - b.height) > TOLERANCE_PX
  )
}

/**
 * 比例低于这个值才认为"被控端真的降采样了"。
 *
 * <p>
 * 不能取 1：显示器的可用区域与桌面尺寸本来就可能差几像素（任务栏、居中等）。
 * 95% 这个位置离真实降采样（4K 是 50%、三屏拼接是 33%）很远，
 * 不会把"差一点点"误报成"缩了"。
 * </p>
 */
const DOWNSCALE_RATIO = 0.95

/**
 * **被控端编码时把画面缩小了多少** —— 只有真的缩了才返回一句话，否则 `null`。
 *
 * <p>
 * 🔥 <b>它回答的是另一个问题</b>：{@link describeSizeMismatch} 判的是
 * "解码结果 vs 这一帧该占的区域"（解不全）；这里是"编码后的整幅 vs 屏幕真实尺寸"
 * （编之前就缩小了）。两者**都正常**时数量关系完全不同 ——
 * 降采样时 region ≡ decoded，前一个判据一句话都不会说，而画面确实是糊的。
 * </p>
 *
 * <p>
 * 🔴 <b>为什么必须说出来</b>：MJPEG 路径会主动把长边压到
 * <c>GdiJpegFrameEncoder.DefaultMaxLongEdge</c>（1920）。三屏拼接 5760×2160
 * 压过去只剩每块屏 640 像素 —— 现场看到的"合并显示很糊"不是坏了，
 * 但如果页面不说，没人知道该"切到单块屏（走硬编 1:1）"这条出路。
 * </p>
 *
 * @param display 屏幕（或拼接后的虚拟桌面）的真实像素尺寸；拿不到就传 `null`。
 * @param frame 帧头声明的整幅尺寸（画布尺寸）。
 */
export function describeEncodingScale(
  display: PixelSize | null | undefined,
  frame: PixelSize,
): string | null {
  if (!display) return null
  if (display.width <= 0 || display.height <= 0) return null
  if (frame.width <= 0 || frame.height <= 0) return null

  const ratio = Math.min(frame.width / display.width, frame.height / display.height)

  if (ratio >= DOWNSCALE_RATIO) return null

  const percent = Math.round(ratio * 1000) / 10

  return (
    `被控端把 ${display.width}×${display.height} 缩小到 `
    + `${frame.width}×${frame.height}（${percent}%）之后再编码。`
    + '这是 MJPEG 为省 CPU 主动降采样（多屏拼接只能走它，硬编一次只抓一块屏）。'
    + '画面是完整的，只是分辨率低 —— 要看 1:1 清晰画面，请切到单块屏。'
  )
}
