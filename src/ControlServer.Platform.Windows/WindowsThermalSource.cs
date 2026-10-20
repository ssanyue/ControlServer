using System.Diagnostics;
using ControlServer.Contracts;
using ControlServer.Platform.Abstractions;
using LibreHardwareMonitor.Hardware;

namespace ControlServer.Platform.Windows;

/// <summary>
/// Windows 硬件温度源（M4.3）—— <b>可选能力</b>，LibreHardwareMonitor 的封装。
///
/// <para>━━━ 这个类为什么长这样 ━━━</para>
///
/// <para>
/// 读 CPU / 主板温度在 Windows 上**没有用户态正路**，必须加载一个内核级驱动
/// （LHM 内嵌的 WinRing0）。这带来三件必须由代码负责的事：
/// </para>
///
/// <list type="number">
///   <item>
///     <b>未启用时一个字节都别碰</b>：驱动是 DLL 里的一段 gz 资源，
///     <b>只有 <c>Ring0.Open()</c> 被调用时才会释放到磁盘并加载</b>。
///     所以"默认不装"是靠"根本不去 new Computer"实现的，而不是靠打包时排除文件 ——
///     排除文件的方案迟早会被某次"顺手引用一下"破坏。
///   </item>
///   <item>
///     <b>驱动加载失败只尝试一次，之后永久闭嘴</b>：失败最常见的原因是
///     安全软件拦截 / 非管理员 / 虚拟机不支持。这类失败<b>重试一百次也还是失败</b>，
///     而每 5 秒重试一次会把事件日志刷爆，还会让杀软反复弹窗。
///     ⚠️ 唯独"驱动起来了、只是没读到 CPU 温度"这一种可以再试几次 ——
///     那是传感器初始化慢 / 型号识别时机问题，见 <see cref="ShouldAttempt"/>。
///   </item>
///   <item>
///     <b>拿不到就承认拿不到</b>：温度字段为 null，页面显示「未启用」/「—」，
///     <b>绝不用 0 冒充</b>。理由见 <see cref="AgentThermalStates"/>。
///   </item>
/// </list>
///
/// <para>━━━ 2026-10-19：AMD CPU 温度 ━━━</para>
///
/// <para>
/// 现场反馈"AMD 机器读不到 CPU 温度"。查下来有四条各自独立的原因，现已逐条处理：
/// </para>
///
/// <list type="number">
///   <item>
///     <b>老 AMD 的温度不在 CPU 硬件上</b>：K10/K15、FM2/AM3 这类平台的 CPU 温度由
///     主板 SuperIO（Nuvoton / ITE）提供，挂在 <c>Motherboard</c> 的 <c>SubHardware</c> 下，
///     传感器名是 <c>CPU Core</c>。原实现只遍历顶层 <c>HardwareType.Cpu</c>，
///     ⇒ <b>这类机器永远 null</b>。现在把子硬件一起拍平，SuperIO 作为最后一档兜底。
///   </item>
///   <item>
///     <b>AMD 的命名没覆盖全</b>：除了 <c>Core (Tctl/Tdie)</c>，还有
///     <c>CCD1 (Tdie)</c> / <c>CCDs Max (Tdie)</c> / <c>CCDs Average (Tdie)</c>。
///     名字里都带 Tdie，旧逻辑"含 Tdie 就取"会把单个 CCD 当成整颗 CPU。
///     分级策略已挪到 <see cref="ThermalSensorPicker"/>，并按厂商方言逐条钉住。
///   </item>
///   <item>
///     <b>值域边界与落库净化不一致</b>：旧代码只挡 <c>NaN</c>，而聚合器把
///     <c>&lt; -40</c> / <c>&gt; 150</c> 也当无效 —— 于是出现"采集认为有效、
///     落库时被抹成 null"的静默丢失。现在两处用同一个区间（<b>0 ℃ 保留</b>，
///     理由见文档 §22.2：冷库 / 户外机柜真的会到 0 ℃）。
///   </item>
///   <item>
///     <b>读不到时没有一个字解释</b>：状态是 <c>ok</c>（因为硬盘温度有值）、
///     <c>Note</c> 是 null，页面上只剩一个 <c>CPU —</c>。现在 CPU 缺失时
///     <c>Note</c> 会说清"枚举到哪颗 CPU、有没有传感器、值是否无效" ——
///     这三句话分别对应"去查驱动 / 换驱动库 / 机器本来没有"三种完全不同的动作。
///   </item>
/// </list>
///
/// <para>
/// ⚠️ <b>驱动库版本仍是 0.9.3，不升</b>：0.9.4 的 CPU 实现类与 0.9.3 完全相同
/// （没有新增 AMD 家族支持），却会把传递依赖升到 System.Management 9 与本项目集中钉的 8 冲突。
/// 真正的 Zen5 支持要等 0.9.5+，那是一次独立的依赖决策 ——
/// 在它发生之前，<b>至少要让现场从页面上看出"是这颗 U 没被识别"</b>，而不是干等。
/// </para>
/// </summary>
public sealed class WindowsThermalSource : IThermalSource, IDisposable
{
    /// <summary>
    /// 最多 Open 几次。<b>1 = 首次，2~3 = "驱动起来了但没拿到 CPU 温度"的重试</b>。
    /// </summary>
    private const int MaxOpenAttempts = 3;

    /// <summary>两次重试之间的最小间隔。传感器初始化慢的机器，隔几秒重试通常就够了。</summary>
    private const int RetryIntervalMs = 30_000;

    /// <summary>
    /// 首次枚举后连刷几遍。<para>部分传感器（存储温度、AMD 的 SMN 电源/温度）要在第二次
    /// 之后才有值；只刷一次会把这类机器误判成"驱动失败"。</para>
    /// </summary>
    private const int RefreshCount = 3;

    /// <summary>是否已启用（来自安装时勾选 / 配置）。</summary>
    private readonly bool _enabled;

    private readonly object _gate = new();

    private Computer? _computer;
    private UpdateVisitor? _visitor;

    /// <summary>已经 Open 过几次（含失败）。</summary>
    private int _openAttempts;

    /// <summary>上一次 Open 的时刻（<see cref="Stopwatch.GetTimestamp"/>）。</summary>
    private long _lastAttemptTimestamp;

    /// <summary>这次失败是否值得重试（见 <see cref="ShouldAttempt"/>）。</summary>
    private bool _retryable;

    private string _state = AgentThermalStates.Disabled;
    private string? _note;

    private bool _disposed;

    /// <summary>
    /// </summary>
    /// <param name="enabled">
    /// 是否启用硬件温度监控。由组合根从配置读出后传进来 ——
    /// <b>刻意不传配置对象</b>：Platform.* 不引用 Agent/Core，这里只要一个 bool。
    /// </param>
    /// <param name="disabledNote">
    /// <b>没启用时</b>给运维看的那句话（"到底为什么没有温度"）。
    ///
    /// <para>🔥 为什么要从外面传、而不是在这里写死一句：这里只拿得到一个
    /// <c>enabled == false</c>，<b>分不清"配置里根本没有这一项"还是"配置里显式关掉了"</b>。
    /// 而这两件事对运维**指向完全不同的排查动作**：</para>
    /// <list type="bullet">
    ///   <item>「安装时没勾」 ⇒ 去安装包 / 安装脚本里找那个勾选框；</item>
    ///   <item>「配置里没有这一项」 ⇒ 绿色版 / 手工起的客户端，去 appsettings.json 补一项就行。</item>
    /// </list>
    /// <para>⚠️ 这里曾经写死的是「安装时未启用硬件温度监控。」—— 在**绿色版**和**裸起 exe**
    /// 这两条路上那是**一句假话**：它会把运维支去翻一个根本不存在（或没被用过）的安装勾选框，
    /// 而真正的答案就在配置文件里。含糊的一句话等于让现场继续靠猜，
    /// 所以改由**知道真相的那一层**（组合根）把结论算好传进来。</para>
    /// </param>
    public WindowsThermalSource(bool enabled, string? disabledNote = null)
    {
        _enabled = enabled;

        _note = enabled
            ? null
            : disabledNote ?? "没有启用硬件温度监控（配置里没有这一项，或被显式关掉了）。";
    }

    /// <summary>
    /// 见 <see cref="AgentThermalStates"/>。进程生命周期内恒定（初始化一次）。
    /// <para>
    /// ⚠️ <b>读这个属性会触发一次性驱动加载</b>（若已启用）。刻意如此 ——
    /// 否则"清单上报"与"第一次采样"谁先跑就成了竞态：清单要是先跑，
    /// 就会把一个"其实已启用"的机器报成"未启用"，页面上一直挂着错的提示直到 6 小时后。
    /// </para>
    /// </summary>
    public string State
    {
        get
        {
            EnsureInitialized();
            return _state;
        }
    }

    /// <summary>状态的人话补充（失败原因 / CPU 缺失的诊断），没有就是 null。同样会触发一次性初始化。</summary>
    public string? Note
    {
        get
        {
            EnsureInitialized();
            return _note;
        }
    }

    /// <summary>
    /// 线程安全，且<b>只在需要时</b>才 Open。未启用时<b>直接返回，一个字节都不碰</b>。
    /// </summary>
    private void EnsureInitialized()
    {
        if (!_enabled || _disposed) return;
        if (!OperatingSystem.IsWindows()) return;

        lock (_gate)
        {
            if (_disposed) return;

            // 已经成功就再也不碰驱动 —— Close/Open 会把内核服务卸了重装。
            if (_state == AgentThermalStates.Ok) return;

            if (!ShouldAttempt()) return;

            Initialize();
        }
    }

    /// <summary>
    /// 这一次要不要尝试打开驱动。
    /// <para>
    /// 🔥 判据只有一条：<b>失败是不是"可以靠再等一会儿变好"的那一种</b>。
    /// </para>
    /// <list type="bullet">
    ///   <item>一次都没试过 ⇒ 试；</item>
    ///   <item>上次是"驱动加载失败 / 被拦截"⇒ <b>永不重试</b>（重试只会刷日志、弹杀软窗）；</item>
    ///   <item>上次是"驱动起来了，但 CPU 没读到温度" ⇒ 最多再试 2 次、每次间隔 30 秒
    ///         （传感器初始化慢 / 型号识别时机问题）。仍然失败就安静地停在 failed。</item>
    /// </list>
    /// </summary>
    private bool ShouldAttempt()
    {
        if (_openAttempts == 0) return true;
        if (!_retryable) return false;
        if (_openAttempts >= MaxOpenAttempts) return false;

        return Stopwatch.GetElapsedTime(_lastAttemptTimestamp).TotalMilliseconds >= RetryIntervalMs;
    }

    /// <summary>
    /// 采一次温度。
    /// <para>
    /// 首次调用会触发驱动加载（若已启用），因此可能比后续调用慢（数十~数百毫秒）。
    /// 之后每次调用只是让 LHM 刷新一遍传感器读数。
    /// </para>
    /// </summary>
    public HostThermalReading Read()
    {
        // 未启用：连 Computer 都不构造。这是"默认不装驱动"的实现本体。
        if (_disposed || !_enabled) return HostThermalReading.Empty;

        EnsureInitialized();

        lock (_gate)
        {
            if (_disposed || _state != AgentThermalStates.Ok || _computer is null)
            {
                return HostThermalReading.Empty;
            }

            try
            {
                // 刷新一轮再读。LHM 的 Update 是"把驱动里的当前值搬进 Sensor.Value"。
                _computer.Accept(_visitor!);

                var (sensors, cpuNames) = Flatten(_computer);
                var pick = ThermalSensorPicker.Pick(sensors, cpuNames);

                return new HostThermalReading(pick.CpuC, pick.GpuC, pick.StorageC);
            }
            catch
            {
                // 读到一半失败（驱动被卸掉 / 设备热插拔）：这一次给 null。
                // ⚠️ 刻意**不改状态、不刷日志** —— 驱动还在，下一轮大概率就正常了；
                //    真持续失败也只是曲线上断一段，比刷日志好。
                return HostThermalReading.Empty;
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────
    // 初始化（最多 MaxOpenAttempts 次）
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 尝试加载驱动并枚举硬件。<b>只在锁内调用</b>。
    /// <para>
    /// 判定"失败"的判据：拿不到<b>任何</b>温度读数。这是最贴近用户感受的信号 ——
    /// 勾了选项却什么都没有，对运维来说就是"没生效"。
    /// 但现在失败会**分成三种**写进 Note（没枚举到 CPU / 有 CPU 无传感器 / 值无效），
    /// 因为它们对应的处置动作完全不同。
    /// </para>
    /// </summary>
    private void Initialize()
    {
        _openAttempts++;
        _lastAttemptTimestamp = Stopwatch.GetTimestamp();
        _retryable = false;

        Computer? computer = null;

        try
        {
            computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsStorageEnabled = true,
                IsMotherboardEnabled = true,

                // 这几项与温度无关，但 LHM 会把它们也算进轮询开销里
                // （内存/网络/控制器要各自走一遍 WMI 或驱动），因此显式关掉。
                IsMemoryEnabled = false,
                IsNetworkEnabled = false,
                IsControllerEnabled = false,
            };

            // 🔥 这一行是"装驱动"的动作本身：LHM 会把内嵌的 WinRing0x64.gz 解出来落盘、
            //    创建内核服务并启动它。失败时它**不一定抛**，所以下面还要靠"有没有读数"判断。
            computer.Open();

            var visitor = new UpdateVisitor();

            // 连刷几遍：部分传感器（存储温度、AMD 的 SMN 温度）要在后续 Update 之后才有值，
            // 只刷一次会把这些机器误判成"驱动失败"。次数与间隔都要小 —— 这段在锁内。
            for (var i = 0; i < RefreshCount; i++)
            {
                computer.Accept(visitor);

                if (i < RefreshCount - 1) Thread.Sleep(80);
            }

            var (sensors, cpuNames) = Flatten(computer);
            var pick = ThermalSensorPicker.Pick(sensors, cpuNames);

            var hasAnyReading = pick.CpuC is not null || pick.GpuC is not null || pick.StorageC is not null;

            if (!hasAnyReading)
            {
                SafeClose(computer);

                _state = AgentThermalStates.Failed;
                _note = "已启用硬件温度监控，但一项温度都没拿到。"
                    + (pick.CpuNote ?? string.Empty)
                    + "另外也常见于：内核驱动被安全软件拦截、进程不是管理员/SYSTEM、"
                    + "或该机型/虚拟机不提供温度传感器。";

                // 只在"确实枚举到了 CPU 硬件"时才值得重试 —— 一个都没枚举到，
                // 说明驱动根本没起来，重试一百次也还是同一个结果。
                _retryable = sensors.Any(s => s.Kind == ThermalHardwareKind.Cpu);

                return;
            }

            _computer = computer;
            _visitor = visitor;
            _state = AgentThermalStates.Ok;

            // 🔥 ok 也可以有 Note：三项里缺了 CPU 时，那个"CPU —"必须有人解释。
            //    页面把它挂在"温度"那一格的 title 上（见 AgentDetailPanel.vue）。
            //    三项都齐时保持 null —— 别让"一切正常"去占注意力。
            _note = pick.CpuC is null ? pick.CpuNote : null;
        }
        catch (Exception ex)
        {
            // 异常信息往往就是答案（"Access is denied" / "Cannot load driver"），原样带出去。
            _state = AgentThermalStates.Failed;
            _note = $"加载硬件温度驱动失败：{ex.GetType().Name}: {ex.Message}";
            _computer = null;
            _visitor = null;
            _retryable = false;

            if (computer is not null) SafeClose(computer);
        }
    }

    // ───────────────────────────────────────────────────────────────────────
    // 拍平硬件树
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 把 LHM 的硬件树拍平成 <see cref="ThermalSensorReading"/> 列表。
    ///
    /// <para>
    /// 🔥 <b>必须递归 <c>SubHardware</c></b>：主板温度（尤其是老 AMD 的 CPU 温度）
    /// 挂在 <c>Motherboard → SuperIO</c> 这一层，顶层循环根本看不到。
    /// 这是"AMD 读不到 CPU 温度"里最容易被漏掉的一条 —— 因为它在 Intel 机器上完全正常
    /// （Intel 走 <c>CPU Package</c>，就在顶层 CPU 硬件上）。
    /// </para>
    /// <para>
    /// 子硬件继承父硬件的分类（SuperIO 自己的 <c>HardwareType</c> 是 SuperIO，不是 Motherboard）。
    /// </para>
    /// <para>
    /// 返回值里<b>额外带一份"枚举到的 CPU 硬件名"</b>：一颗一个温度传感器都没有的 CPU
    /// 在读数列表里根本不出现，只靠读数会把"这颗 U 太新"误判成"驱动没生效"
    /// （详见 <see cref="ThermalSensorPicker.Pick"/> 的 <c>presentCpuHardwareNames</c>）。
    /// </para>
    /// </summary>
    private static (List<ThermalSensorReading> Sensors, List<string> CpuHardwareNames) Flatten(IComputer computer)
    {
        var result = new List<ThermalSensorReading>();
        var cpuNames = new List<string>();

        foreach (var hardware in computer.Hardware)
        {
            Walk(hardware, null, result, cpuNames);
        }

        return (result, cpuNames);

        static void Walk(
            IHardware hardware,
            ThermalHardwareKind? inherited,
            List<ThermalSensorReading> into,
            List<string> cpuNames)
        {
            var own = Map(hardware.HardwareType);

            // CPU 硬件**无论有没有温度传感器**都记下来 —— 这就是诊断"这颗 U 没被识别"的依据。
            if (own == ThermalHardwareKind.Cpu && !string.IsNullOrWhiteSpace(hardware.Name))
            {
                var name = hardware.Name;

                if (!cpuNames.Contains(name, StringComparer.OrdinalIgnoreCase)) cpuNames.Add(name);
            }

            var kind = own ?? inherited;

            if (kind is { } k)
            {
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature) continue;
                    if (sensor.Value is not { } value) continue;

                    into.Add(new ThermalSensorReading(k, hardware.Name, sensor.Name, value));
                }
            }

            foreach (var sub in hardware.SubHardware)
            {
                Walk(sub, kind, into, cpuNames);
            }
        }

        static ThermalHardwareKind? Map(HardwareType type) => type switch
        {
            HardwareType.Cpu => ThermalHardwareKind.Cpu,
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => ThermalHardwareKind.Gpu,
            HardwareType.Storage => ThermalHardwareKind.Storage,
            HardwareType.Motherboard => ThermalHardwareKind.Motherboard,
            _ => null,
        };
    }

    // ───────────────────────────────────────────────────────────────────────
    // 私有
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>关掉 Computer 并把异常吞掉 —— 收尾路径上抛异常会掩盖真正的原因。</summary>
    private static void SafeClose(Computer computer)
    {
        try
        {
            computer.Close();
        }
        catch
        {
            // 关不掉就算了，进程马上就要退出了。
        }
    }

    /// <summary>
    /// 释放。<b>会把驱动服务一并卸掉</b>（LHM 的 Close 语义），
    /// 因此绝不能在 Read 的正常路径上调用。
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            if (_computer is { } computer)
            {
                SafeClose(computer);
                _computer = null;
                _visitor = null;
            }
        }
    }

    /// <summary>
    /// LHM 的"刷新"访问者：把硬件树走一遍，每层调用 <c>Update()</c>。
    /// <para>这是 LHM 官方文档给出的标准写法（<c>IVisitor</c> 没有默认实现）。</para>
    /// </summary>
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();

            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }
}
