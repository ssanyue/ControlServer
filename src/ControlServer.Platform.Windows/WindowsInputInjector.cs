using System.Runtime.InteropServices;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// <b>Windows 的键鼠注入实现</b>（M5.2）—— 走 <c>SendInput</c>。
///
/// <para>
/// 🔥 <b>它只能在用户会话里跑</b>（被控端托盘进程）。服务在 Session 0，
/// 那里没有桌面 —— 在 Session 0 调 <c>SendInput</c> 是<b>往一个不存在的桌面发按键</b>：
/// 返回成功、日志正常、用户什么都看不到。这类"成功但无效"是本项目里最贵的一种故障，
/// 所以注入的调用点被刻意收在托盘这一个地方（见 <c>RemoteStreamHost</c>）。
/// </para>
///
/// <para>
/// 🔥 <b>用 <c>SendInput</c> 而不是 <c>mouse_event</c>/<c>keybd_event</c></b>：
/// 后两个是 16 位时代的遗留接口（微软已标注建议改用 <c>SendInput</c>），
/// 且它们<b>不能原子地表达"按下 + 移动"</b> —— 而一次拖动正是这个形状。
/// <c>SendInput</c> 还能把一串事件当<b>一次调用</b>投进输入队列，中间不会被别的输入插进来
/// （对拖拽这种"必须连续"的操作，插进来一次别的移动就是轨迹上的一个毛刺）。
/// </para>
///
/// <para>
/// ⚠️ <b>有两类"注定失败"，实现方必须如实说出来，不许装作成功</b>：
/// </para>
/// <list type="bullet">
///   <item><b>UAC 安全桌面（Winlogon / 提权确认框）</b>：注入返回 0，
///         因为那个桌面不在我们进程的窗口站里；</item>
///   <item><b>完整性级别更高的窗口（UIPI）</b>：以普通用户身份跑的托盘
///         注入不到一个"以管理员身份运行"的程序里 —— 那个窗口对输入完全无反应，
///         而其它窗口一切正常。这是 Windows 的安全机制，不是我们的 bug，
///         但现场一定会把它报成"远控时好时坏"。</item>
/// </list>
/// </summary>
public sealed class WindowsInputInjector : IInputInjector
{
    // ── SendInput 的鼠标事件标志 ──
    private const uint MouseMove = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint MiddleDown = 0x0020;
    private const uint MiddleUp = 0x0040;
    private const uint Wheel = 0x0800;

    /// <summary>
    /// 🔥 <b>这两个标志决定了"归一化坐标"到底相对于谁</b>，是 M5.2 最容易搞错的一处：
    /// <list type="bullet">
    ///   <item><see cref="Absolute"/> 不带 <see cref="VirtualDesk"/> 时，
    ///         0–65535 映射到<b>主显示器</b>；</item>
    ///   <item>带上 <see cref="VirtualDesk"/> 才映射到<b>整个虚拟桌面</b>。</item>
    /// </list>
    /// 被控端可能有 2×2 拼成的视频墙（方案 §0.4），那时只有后者是对的 ——
    /// 而两者在单屏机器上<b>行为完全一样</b>，所以少写一个标志在开发机上永远测不出来。
    /// </summary>
    private const uint Absolute = 0x8000;

    private const uint VirtualDesk = 0x4000;

    /// <summary>滚轮的一格。<b>由平台层解释</b>（抽象层不替平台定这个数）。</summary>
    private const int WheelDelta = 120;

    private const uint KeyUp = 0x0002;

    /// <summary>
    /// 以 Unicode 字符直接注入一个"按键"（不走键盘布局）。
    /// <para>
    /// 🔥 文本输入必须走它：按虚拟键码打字的话，被控端的键盘布局决定了最终字符 ——
    /// 在一台装了中文输入法或德语布局的机器上，"y"和"z"会换位置。
    /// </para>
    /// </summary>
    private const uint Unicode = 0x0004;

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    // GetSystemMetrics 的下标（虚拟桌面矩形）。
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    /// <summary>
    /// 最近一次注入失败的原因；成功之后不清零。
    ///
    /// <para>
    /// ⚠️ <b>它不是可选的装饰</b>：注入失败在画面上<b>不留任何痕迹</b>
    /// （用户只会看到"点了没用"），这是这条链路上唯一能说话的地方。
    /// </para>
    /// </summary>
    public string? LastFailure { get; private set; }

    /// <inheritdoc />
    public void MovePointer(int x, int y)
    {
        var normalized = ToAbsolute(x, y);

        Send(MouseInput(normalized.X, normalized.Y, 0, MouseMove | Absolute | VirtualDesk));
    }

    /// <inheritdoc />
    public void ClickPointer(MouseButtonKind button)
    {
        // 两次调用（按下、抬起）而不是一次把两个标志或在一起：
        // 输入队列是**有序**的，两次调用之间的顺序仍然成立，而合成一次
        // 在"按下与抬起之间还有别的动作"（拖拽）时就表达不了了 ——
        // 这里刻意用与拖拽同一条路，省得两条路各有一套行为。
        PointerButton(button, down: true);
        PointerButton(button, down: false);
    }

    /// <inheritdoc />
    public void PointerButton(MouseButtonKind button, bool down)
    {
        var flag = button switch
        {
            MouseButtonKind.Left => down ? LeftDown : LeftUp,
            MouseButtonKind.Right => down ? RightDown : RightUp,
            MouseButtonKind.Middle => down ? MiddleDown : MiddleUp,

            // ⚠️ 不认识的键**不发任何东西**：猜一个（比如当成左键）会让用户
            //    在拖拽时意外点开东西，而那种事故比"没反应"严重得多。
            _ => 0u,
        };

        if (flag == 0) return;

        // ⚠️ 不带 MOVE：只发按键标志时，Windows 用**当前**指针位置，
        //    这正是我们想要的 —— 坐标已由上一条 MovePointer 送过去了。
        //    若在这里再带一次坐标，就必须把"当前应该在哪儿"也存下来，
        //    而那个状态一旦与真实指针不同步，就变成"点偏了"。
        Send(MouseInput(0, 0, 0, flag));
    }

    /// <inheritdoc />
    public void ScrollPointer(int delta)
    {
        // 刻度在这里换算成 WHEEL_DELTA 的倍数：抽象层收的是"格"（正值 = 向上），
        // Windows 收的是 120 的倍数。
        var clicks = delta * WheelDelta;

        Send(MouseInput(0, 0, unchecked((uint)clicks), Wheel));
    }

    /// <inheritdoc />
    public void KeyState(int virtualKey, bool down)
    {
        Send(KeyInput((ushort)virtualKey, 0, down ? 0u : KeyUp));
    }

    /// <inheritdoc />
    public void KeyPress(int virtualKey)
    {
        KeyState(virtualKey, down: true);
        KeyState(virtualKey, down: false);
    }

    /// <inheritdoc />
    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // ⚠️ 逐 UTF-16 码元发：代理对（表情、罕见汉字）会被拆成两个码元，
        //    而 Windows 会把相邻的两个代理码元重新拼回一个字符 ——
        //    前提是它们**紧挨着**且中途没有别的键插进来，所以这里必须逐条顺序发。
        var inputs = new INPUT[text.Length * 2];

        for (var i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = KeyInput(0, text[i], Unicode);
            inputs[(i * 2) + 1] = KeyInput(0, text[i], Unicode | KeyUp);
        }

        Send(inputs);
    }

    /// <summary>
    /// 把虚拟桌面像素换算成 <c>SendInput</c> 要的 0–65535。
    ///
    /// <para>
    /// 🔥 <b>分母是"虚拟桌面尺寸 − 1"而不是"尺寸"</b>：0 与 65535 是<b>两端</b>，
    /// 只有 n−1 段才够跨完 n 个像素。用 n 当分母，最右/最下一列**永远点不到**
    /// （8K 宽下那是 30 多个像素的一整条边）—— 而那种偏差在 1080p 上只有 0.3 个像素，
    /// 肉眼完全看不出来，直到有人在一台 8K 被控机上发现"贴边点不中"。
    /// </para>
    /// </summary>
    private static (int X, int Y) ToAbsolute(int x, int y)
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);

        // 拿不到虚拟桌面尺寸（极端情况下会返回 0）：**不猜**，按 1:1 透传。
        // 猜一个会导致"越界就点不到"，而透传至少让主屏上的点击是对的。
        if (width <= 1 || height <= 1) return (x, y);

        var nx = (int)Math.Round((x - left) * 65535.0 / (width - 1));
        var ny = (int)Math.Round((y - top) * 65535.0 / (height - 1));

        return (Math.Clamp(nx, 0, 65535), Math.Clamp(ny, 0, 65535));
    }

    /// <summary>
    /// 投一串事件进输入队列，并把"一个都没进去"如实记下来。
    /// </summary>
    /// <remarks>
    /// 🔥 <b>返回值必须看</b>：<c>SendInput</c> 返回真正入队的事件数，
    /// 而 <b>0 表示被拦了</b>（UAC 安全桌面 / 会话已切换 / 完整性级别不够）。
    /// 不看它就会把"被系统拦住"当成"已经点下去了"，而现场看到的是"点了没反应" ——
    /// 那正是最难查的一种。
    /// </remarks>
    private void Send(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;

        try
        {
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

            if (sent == inputs.Length)
            {
                return;
            }

            LastFailure = sent == 0
                ? "系统没有接受这批输入（常见原因：UAC 安全桌面 / Winlogon 界面正在前台，"
                  + "或目标窗口以更高权限运行 —— 后者是 Windows 的完整性级别限制，不是故障）。"
                : $"这批 {inputs.Length} 条输入只入队了 {sent} 条。";

            // ⚠️ 不抛：注入循环跑在推流旁边，抛出去会把整条远控会话带走，
            //    而"一次点不动"远不该有这个代价。
        }
        catch (Exception ex)
        {
            LastFailure = $"{ex.GetType().Name}：{ex.Message}";

            // 同上：不抛。
        }
    }

    private static INPUT MouseInput(int dx, int dy, uint mouseData, uint flags) => new()
    {
        Type = InputMouse,
        Data = new InputUnion
        {
            Mouse = new MOUSEINPUT
            {
                Dx = dx,
                Dy = dy,
                MouseData = mouseData,
                Flags = flags,

                // ⚠️ 留 0 = "不是程序合成的"可被目标程序识别为伪造；
                //    这里刻意不伪装（见类注释的边界说明）——
                //    这份能力是给管理员用的，不是用来骗过被测软件的。
                ExtraInfo = IntPtr.Zero,
            },
        },
    };

    private static INPUT KeyInput(ushort virtualKey, ushort scan, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = virtualKey,
                Scan = scan,
                Flags = flags,
                ExtraInfo = IntPtr.Zero,
            },
        },
    };

    // ═══════════════════════════════════════════════════════════
    // P/Invoke
    // ═══════════════════════════════════════════════════════════

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Data;
    }

    /// <summary>
    /// 🔥 <b>必须是显式布局的联合体</b>：<c>SendInput</c> 按 <c>cbSize</c> 校验结构体大小，
    /// 而它期望的是"4 字节类型 + 32 字节联合体"。写成两个字段顺序排列（顺序布局），
    /// 结构体会变成 4 + 24 + 24 = 52 字节 ⇒ <c>SendInput</c> 直接返回 0 并且
    /// <c>GetLastError</c> 报参数错误 —— 症状是"注入完全没反应"。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT Mouse;

        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, INPUT[] inputs, int sizeOfInputStructure);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
