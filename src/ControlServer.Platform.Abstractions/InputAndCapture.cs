namespace ControlServer.Platform.Abstractions;

/// <summary>
/// 键鼠注入。用于远程桌面（被控端 Agent）。
/// 【M5 实现】Windows: SendInput；Linux: XTest / uinput。
///
/// <para>
/// ⚠️ <b>Windows 服务位于 Session 0</b>，那里没有桌面 —— 在 Session 0 里注入等于
/// <b>往一个不存在的桌面发按键</b>，用户什么都看不到（"执行成功、毫无效果"）。
/// 因此注入的<b>唯一合法执行位置是用户会话</b>（被控端托盘进程），
/// 见 <c>agents/ControlServer.Agent/Tasks/ISessionCommandChannel</c>。
/// </para>
///
/// <para>
/// 🔥 <b>M5.0 补了三个成员</b>（原来只有"移动 / 点击 / 按键 / 打字"四个快捷键式动作）：
/// 远控真正要用的语义是<b>"按下 / 抬起分离"</b>——
/// 拖拽（选中文件、拉滚动条、拖窗口）必须是"按住 → 移动 → 松开"，
/// 只用 <see cref="ClickPointer"/> 做不到；组合键（Ctrl+C）与长按（方向键连播）
/// 同理，只有 <see cref="KeyPress"/> 这个"按一下就抬"的动作也做不到。
/// </para>
/// </summary>
public interface IInputInjector
{
    /// <summary>把指针移到<b>虚拟屏幕坐标</b>（多屏拼接后的整屏坐标系，不是单个显示器的坐标）。</summary>
    void MovePointer(int x, int y);

    /// <summary>按下并抬起（一次完整点击）。拖拽请改用 <see cref="PointerButton"/>。</summary>
    void ClickPointer(MouseButtonKind button);

    /// <summary>按下并抬起一个虚拟键。</summary>
    void KeyPress(int virtualKey);

    /// <summary>逐字符输入文本（含输入法无关的直接注入）。</summary>
    void TypeText(string text);

    /// <summary>
    /// <b>按下或抬起</b>一个鼠标键（M5 新增）。
    /// <para>拖拽需要它：<c>PointerButton(Left, true)</c> → <c>MovePointer</c> → <c>PointerButton(Left, false)</c>。</para>
    /// </summary>
    void PointerButton(MouseButtonKind button, bool down);

    /// <summary>
    /// 滚轮（M5 新增）。
    /// <para>
    /// <paramref name="delta"/> 正值 = 向上滚。<b>刻度由平台层解释</b>
    /// （Windows 的一格是 <c>WHEEL_DELTA</c> = 120）—— 抽象层不替平台定这个数。
    /// </para>
    /// </summary>
    void ScrollPointer(int delta);

    /// <summary>
    /// <b>按下或抬起</b>一个虚拟键（M5 新增）。组合键与长按需要它。
    /// <para>⚠️ 调用方负责成对使用；漏掉抬起会让那个键在用户那边"卡住"。</para>
    /// </summary>
    void KeyState(int virtualKey, bool down);
}

/// <summary>鼠标键。</summary>
public enum MouseButtonKind
{
    Left,
    Middle,
    Right,
}

/// <summary>
/// <b>归一化坐标 ⇄ 屏幕像素</b>的换算（M5.2）。
///
/// <para>
/// 🔥 <b>为什么把它单独抽出来、而且放在抽象层</b>：这是远控里<b>最容易错、
/// 错了又最难查</b>的一处数学 —— 错了的表现是"鼠标整体偏一点"或"分辨率一变就越点越偏"，
/// 而它在单屏 + 100% 缩放的开发机上<b>完全看不出来</b>。
/// 抽成纯函数（不碰原生 API、不看任何状态）之后，
/// 它可以被一组普通的用例钉死，而不是靠"多试几台机器"。
/// </para>
///
/// <para>
/// <b>量程</b>：0–65535，与 Windows <c>SendInput</c> 的
/// <c>MOUSEEVENTF_ABSOLUTE</c> 一致 —— 最后一跳因此不需要再换算一次。
/// </para>
///
/// <para>
/// ⚠️ <b>分母是"尺寸 − 1"，不是"尺寸"</b>：0 与 65535 是两端，
/// 只有 n−1 段才跨得完 n 个像素。用 n 当分母，最右/最下一列永远取不到 ——
/// 8K 宽下那是 30 多个像素宽的一整条边。
/// </para>
/// </summary>
public static class InputCoordinates
{
    /// <summary>归一化量程的上界（两端都是闭区间：0 与 65535 分别是最左/最右）。</summary>
    public const int Max = 65535;

    /// <summary>
    /// 归一化坐标 → 目标屏（或虚拟桌面）的像素坐标。
    /// </summary>
    /// <param name="left">该屏在虚拟桌面里的左偏移（<see cref="ScreenDescriptor.Left"/>）。</param>
    /// <param name="top">同上，纵坐标。</param>
    /// <param name="width">该屏宽度（像素）。</param>
    /// <param name="height">该屏高度（像素）。</param>
    /// <remarks>
    /// ⚠️ <paramref name="width"/> 或 <paramref name="height"/> 不合理（≤1）时
    /// <b>原样返回归一化值</b>：这是一个"我们不知道该往哪儿换算"的信号，
    /// 猜一个尺寸会让鼠标跑到完全无关的位置上，而原样返回至少让调用方能看出自己拿到了什么。
    /// </remarks>
    public static (int X, int Y) ToPixels(
        int normalizedX,
        int normalizedY,
        int left,
        int top,
        int width,
        int height)
    {
        if (width <= 1 || height <= 1) return (normalizedX, normalizedY);

        var x = left + (int)Math.Round(Math.Clamp(normalizedX, 0, Max) * (width - 1) / (double)Max);
        var y = top + (int)Math.Round(Math.Clamp(normalizedY, 0, Max) * (height - 1) / (double)Max);

        return (x, y);
    }

    /// <summary>
    /// 像素坐标 → 归一化（<b>用于自检与用例</b>：反过来算一遍就知道这两个函数是不是一对）。
    /// </summary>
    public static (int X, int Y) ToNormalized(
        int x,
        int y,
        int left,
        int top,
        int width,
        int height)
    {
        if (width <= 1 || height <= 1) return (x, y);

        var nx = (int)Math.Round((x - left) * Max / (double)(width - 1));
        var ny = (int)Math.Round((y - top) * Max / (double)(height - 1));

        return (Math.Clamp(nx, 0, Max), Math.Clamp(ny, 0, Max));
    }
}

/// <summary>
/// 屏幕捕获。
/// 【M5 实现】Windows: DXGI Desktop Duplication；Linux: X11(XShm) / Wayland(PipeWire)。
///
/// <para>
/// ⚠️ 同 <see cref="IInputInjector"/>：抓屏也<b>必须在用户会话里</b>做 ——
/// Session 0 没有桌面可抓。被控端服务只负责把"要开会话"的指令转给会话侧。
/// </para>
/// </summary>
public interface IScreenCapturer
{
    /// <summary>枚举可捕获的显示器。</summary>
    IReadOnlyList<ScreenDescriptor> EnumerateScreens();

    /// <summary>
    /// 抓取一帧（内存拷贝路径）。
    /// <para>
    /// ⚠️ <b>这条路在 4K 及以上不适用</b>：4K60 的 BGRA 是 <b>1.99 GB/s</b>（8K 是 8 GB/s），
    /// 每帧多一次显存→内存拷贝就够把帧率吃掉。它只保留给
    /// <b>M5.1 的占位 MJPEG 联调</b>与"单帧截图"这类低频用途；
    /// 生产路径一律走 <see cref="CaptureSurfaceAsync"/>。
    /// </para>
    /// </summary>
    Task<CapturedFrame> CaptureAsync(string screenId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 抓取一帧 <b>GPU 侧</b>画面（M5 新增，零拷贝路径的入口）。
    /// <para>
    /// 🔥 <b>返回 <c>null</c> 不是失败</b>：DXGI Desktop Duplication 在
    /// "画面没有变化"时本来就会超时返回 <c>WAIT_TIMEOUT</c>。
    /// 把这个正常情形表达成异常，会逼着调用方写 <c>catch</c> 来当正常分支用；
    /// 而表达成 <c>null</c>，上层"丢帧 / 复用上一帧"的判断就是一行 <c>is null</c>。
    /// </para>
    /// <para>
    /// ⚠️ 返回的纹理句柄由<b>实现方持有</b>，调用方不得释放，且必须在
    /// 下一次调用本方法前用完（DDA 的纹理是复用的）。
    /// </para>
    /// <para>
    /// ⚠️ <b>只有内存路径的实现会抛 <see cref="NotSupportedException"/></b>
    /// （例如 GDI 抓屏：它拿不到 GPU 纹理）。这是<b>刻意的</b>：
    /// 让它"每次返回 <c>null</c>"会被上层读成"画面没有变化"，
    /// 于是一条零拷贝管线会安静地空转 —— 那是最难查的一类故障。
    /// 抛出来才说得清"你问错了实现"。
    /// </para>
    /// </summary>
    Task<CapturedSurface?> CaptureSurfaceAsync(
        string screenId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 一个可捕获的屏幕。
/// <para>
/// 🔥 <b>M5.0 加了两个字段</b>，都源自排查"抓错屏"这一类故障：
/// </para>
/// <list type="bullet">
///   <item><see cref="SessionId"/> —— 多用户同时登录时（快速切换 / 多远程桌面会话），
///         同一个 <see cref="Id"/> 可能指向别的会话的桌面。不绑会话号就会出现
///         <b>"看着 A 的屏幕、实际操作 B"</b>，而且日志上完全看不出异常。</item>
///   <item><see cref="IsVirtualDesktop"/> —— 被控端是 <b>2×2 拼 4K 的视频墙</b>时，
///         要抓的是"整个虚拟桌面"（7680×4320）而不是某一个输出。这两种东西的
///         宽高完全不同，混用会直接抓出半个画面。</item>
/// </list>
/// </summary>
/// <param name="SessionId">
/// 该屏所属的会话编号。<b>未知给 <c>null</c>，不要用 0 代替</b> ——
/// 0 是"Session 0"这个<b>真实存在且含义明确</b>的会话（服务会话，没有桌面），
/// 拿它表示"不知道"会让"抓到 Session 0 的空桌面"与"没探测到会话"变成同一件事。
/// </param>
/// <param name="Left">
/// 这块屏在<b>虚拟桌面坐标系</b>里的左边缘（M5.2 新增）。
///
/// <para>
/// 🔥 <b>它只为"输入回传"而存在，而且非有不可</b>：中心送来的坐标是
/// <b>相对这一路画面</b>的归一化值（0–65535，见 <c>RemoteInputEventDto</c>），
/// 而注入用的 <c>SendInput</c> 吃的是<b>虚拟桌面像素</b>。
/// 换算必须是 <c>Left + x × Width ÷ 65535</c> —— 少了 <c>Left</c>，
/// 在单屏机器上一切正常（偏移是 0），而<b>多屏机器上鼠标会整体错位</b>。
/// 这类错在开发机上根本复现不出来（开发机常常就是单屏），是本方案里最贵的一种 bug。
/// </para>
/// <para>虚拟桌面本身（<see cref="IsVirtualDesktop"/> 为真）的左/上偏移恒为它自己的原点。</para>
/// </param>
/// <param name="Top">同上，纵坐标。</param>
public sealed record ScreenDescriptor(
    string Id,
    string Name,
    int Width,
    int Height,
    bool IsPrimary,
    int? SessionId = null,
    bool IsVirtualDesktop = false,
    int Left = 0,
    int Top = 0);

/// <summary>
/// 一帧<b>内存里的</b>画面（BGRA）。
/// <para>只用于低频用途（MJPEG 联调、单帧截图）；生产路径见 <see cref="CapturedSurface"/>。</para>
/// </summary>
/// <param name="IsCursorIncluded">
/// 🔥 <b>这一帧里到底有没有鼠标指针。</b>
/// <para>
/// 与 <see cref="CapturedSurface.IsCursorIncluded"/> 同一个理由，只是路径不同：
/// <b>GDI 的 <c>BitBlt</c> 默认不含指针</b>（与 DXGI Desktop Duplication 一样）。
/// 不叠加的直接后果是用户看到"鼠标不动"，并据此判定"这软件不实时" ——
/// 而屏幕上其实一切正常。
/// </para>
/// <para>
/// 这个字段同样<b>只许如实上报，不许上层猜</b>：
/// 猜"有"而实际没有 ⇒ 用户以为鼠标已损坏；猜"没有"而实际有 ⇒ 影分身。
/// </para>
/// </param>
public sealed record CapturedFrame(
    int Width,
    int Height,
    int Stride,
    byte[] Buffer,
    DateTimeOffset CapturedAt,
    bool IsCursorIncluded = false);

/// <summary>
/// 一帧<b>GPU 侧</b>的画面（M5 新增，零拷贝路径）。
///
/// <para>
/// 🔥 <b>为什么必须新开一个类型而不是给 <see cref="CapturedFrame"/> 加字段</b>：
/// 两者的数据位置根本不同（显存 vs 内存），混在一个类型里会让
/// "这一帧到底能不能直接喂给硬件编码器"变成一个<b>运行时才知道</b>的问题。
/// </para>
/// </summary>
/// <param name="TextureHandle">
/// 平台纹理句柄（Windows 上是 <c>ID3D11Texture2D*</c>）。<b>故意用 <c>nint</c></b>
/// 而不是平台接口类型 —— 这一层是跨平台的，<c>Core</c>/<c>Abstractions</c> 不引平台 API。
/// 具体类型由实现方与使用方（同一个平台层）自行约定。
/// </param>
/// <param name="IsCursorIncluded">
/// 🔥 <b>帧里到底有没有鼠标指针</b>。
/// <para>
/// DXGI Desktop Duplication 支持"让驱动把指针画进帧里"，但<b>它默认不画</b>，
/// 且切换桌面/全屏应用时行为会变。这件事<b>必须由实现方如实上报</b>，
/// 不能由上层猜：
/// 猜"有"而实际没有 ⇒ 用户看到"鼠标不动"，直接判定为"这软件不实时"；
/// 猜"没有"而实际有 ⇒ 手动叠加的指针与真实指针影分身。
/// </para>
/// </param>
/// <param name="DirtyRects">
/// 本帧相对上一帧发生变化的矩形（DDA 提供）。
/// <b>空表 = 整屏都可能变了</b>（那不是"什么都没变"，别读反）；
/// <c>null</c> = 该平台不提供脏区信息。
/// </param>
public sealed record CapturedSurface(
    int Width,
    int Height,
    nint TextureHandle,
    SurfacePixelFormat Format,
    bool IsCursorIncluded,
    DateTimeOffset CapturedAt,
    IReadOnlyList<DirtyRect>? DirtyRects = null);

/// <summary>GPU 纹理的像素格式。<b>只有这两种</b> —— 它们是硬件编码器唯一接受的输入。</summary>
public enum SurfacePixelFormat
{
    /// <summary>桌面抓取的原始格式（每像素 4 字节）。</summary>
    Bgra8,

    /// <summary>硬编的输入格式（YUV 4:2:0 半平面）。<b>色彩转换必须在 GPU 上完成</b>。</summary>
    Nv12,
}

/// <summary>一个脏矩形（DDA 的坐标系，左闭右开）。</summary>
public readonly record struct DirtyRect(int Left, int Top, int Right, int Bottom);
