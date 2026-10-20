namespace ControlServer.Platform.Abstractions;

// ═══════════════════════════════════════════════════════════════════════════
// M4.3b · 温度挑选策略（2026-10-19）
//
// 为什么把"挑哪个传感器"从 Windows 实现里抽出来：
//   ① **它才是"读不到 AMD 温度"的现场**。硬件树（驱动/型号/主板）是环境给的，
//      挑错值/挑不到值是我们自己的逻辑 —— 后者必须能被单元测试按厂商方言逐条钉住；
//   ② Windows 那个实现只能在 Windows 上跑，而测试工程不引 Platform.Windows
//      （引了就会在非 Windows 上跑不起来）。抽到 Platform.Abstractions 之后，
//      Intel / AMD / 老 AMD 的命名差异全部可以在 CI 里验，不必等一台真机；
//   ③ 平台实现只剩一件事：**把硬件树拍平成读数**（见 WindowsThermalSource）。
//
// 🔥 判据一律是"取值"而不是"名字对得上"：
//   传感器名在各厂商/各版本的差异极大（见下面的分级），任何精确匹配换台机器就静默变 null。
// ════════════════════════════════════════════

/// <summary>一个温度读数来自哪一类硬件。<b>主板</b>单独一类，因为它只是"老平台的 CPU 温度通道"。</summary>
public enum ThermalHardwareKind
{
    Cpu = 0,
    Gpu = 1,
    Storage = 2,

    /// <summary>
    /// 主板（含它的 SuperIO 子硬件）。这一档**只用来兜底 CPU 温度** ——
    /// 老 AMD 平台（K10/K15、APU）的 CPU 温度不在 CPU 硬件上，而在主板的 SuperIO 芯片里
    /// （Nuvoton / ITE，传感器名通常是 "CPU Core"）。
    /// </summary>
    Motherboard = 3,
}

/// <summary>
/// 一条拍平后的温度读数：<c>(哪类硬件, 硬件名, 传感器名, 摄氏度)</c>。
/// <para>
/// <b>为什么带硬件名</b>：诊断要说得出"是哪颗 CPU 没读到" —— 只说"CPU 温度缺失"，
/// 现场没法判断是驱动没起、还是这颗 U 比驱动库新（后者换驱动库才有解，重装客户端没用）。
/// </para>
/// </summary>
public readonly record struct ThermalSensorReading(
    ThermalHardwareKind Kind,
    string? HardwareName,
    string? SensorName,
    double ValueC);

/// <summary>
/// 挑值结果。
/// <para>
/// <paramref name="CpuNote"/> 是<b>诊断</b>而不是错误：CPU 没读到温度时它说清原因
/// （没枚举到 CPU 硬件 / 这颗 U 没有温度传感器 / 传感器有但读数是 0 或 NaN）。
/// 其余情况下为 null —— 不要让一条"一切正常"的说明去占运维的注意力。
/// </para>
/// </summary>
public sealed record ThermalPick(
    double? CpuC,
    double? GpuC,
    double? StorageC,
    string? CpuSource,
    string? CpuNote)
{
    public static ThermalPick Empty { get; } = new(null, null, null, null, null);
}

/// <summary>
/// 从一堆温度读数里挑出"CPU / GPU / 硬盘"三个展示值。
///
/// <para>━━━ CPU 为什么必须分级 ━━━</para>
///
/// <para>
/// "CPU 温度"在不同平台上是<b>不同的物理量</b>，LHM 给的传感器名也各不相同：
/// </para>
/// <list type="bullet">
///   <item><b>Intel</b>：<c>CPU Package</c>（封装）／<c>Core #1..N</c>（单核）／
///         <c>Core Max</c>／<c>Core Average</c>（LHM 自己算的）；</item>
///   <item><b>AMD Zen</b>：<c>Core (Tctl/Tdie)</c> 最常见，也有只给
///         <c>Core (Tdie)</c> / <c>Core (Tctl)</c> 的；
///         <b>Tctl</b> 是控制温度（部分型号带 +10~20℃ 偏移），<b>Tdie</b> 是裸片温度；</item>
///   <item><b>AMD 多 CCD</b>（Threadripper / 部分 Ryzen）：<c>CCD1 (Tdie)</c>、
///         <c>CCDs Max (Tdie)</c>、<c>CCDs Average (Tdie)</c> ——
///         <b>⚠️ 它们的名字里都有 Tdie</b>，所以"含 Tdie 就取"会把单颗 CCD 当成整颗 CPU；</item>
///   <item><b>老 AMD</b>（K10/K15、FM2/AM3）：CPU 硬件上<b>一个温度传感器都没有</b>，
///         温度只在主板的 SuperIO 里，名字是 <c>CPU Core</c> / <c>System</c> ——
///         只遍历 <c>HardwareType.Cpu</c> 的实现永远读不到这类机器。</item>
/// </list>
///
/// <para>因此这里的顺序是"越接近厂商口径的越优先"，每一级都写清了为什么在这一档。</para>
///
/// <para>━━━ 值域 ━━━</para>
///
/// <para>
/// 只挡<b>不可能是读数</b>的值：<c>NaN</c>（LHM 用表示"这次没读到"）、无穷大、
/// 以及超出 [-40, 150] ℃ 的离谱值。这个区间与
/// <c>AgentMetricsAggregator.Temperature</c> 的净化规则**一字不差** ——
/// 两边用不同界，会出现"采集认为有效、落库时被抹成 null"的静默丢失。
/// </para>
///
/// <para>
/// 🔥 <b><c>0 ℃</c> 必须保留</b>（文档 §22.2 已定）：冷库 / 冬天户外机柜真的会到 0 ℃，
/// "见 0 就丢"是替传感器做决定，而且会让那条本来可以异常的曲线变成"没有数据"。
/// 这与 Linux 那条路的做法**故意不同**：sysfs 在驱动没填时会写一个 <c>0</c>，
/// 而 LHM 读不到时给的是 <c>NaN</c> —— 两个平台上 "0" 的含义不一样，
/// 不能为了"代码看起来统一"把其中一个的判据搬过去。
/// </para>
/// </summary>
public static class ThermalSensorPicker
{
    /// <summary>低于这个温度按"没读到"处理（与聚合器的净化下限一致）。</summary>
    public const double MinPlausibleC = -40;

    /// <summary>高于这个温度按"没读到"处理（未初始化的寄存器，正常 CPU 碰不到）。</summary>
    public const double MaxPlausibleC = 150;

    /// <summary>诊断里最多列几个传感器名 —— 再多就没人读了，而且 Note 要进清单行。</summary>
    private const int MaxNamesInNote = 6;

    /// <summary>
    /// 挑值。<b>绝不抛</b>；任何一项挑不到就是 null。
    /// </summary>
    /// <param name="sensors">拍平后的温度读数。</param>
    /// <param name="presentCpuHardwareNames">
    /// <b>枚举到的 CPU 硬件名</b>，<b>不管它有没有温度传感器</b>。
    ///
    /// <para>🔥 为什么必须单独传：拍平后的读数只包含"有温度读数的硬件"。
    /// 一颗 CPU 若一个温度传感器都没有（Zen5 这类比驱动库新的型号），
    /// 它在读数列表里根本不会出现 —— 于是只能得出"没枚举到 CPU"这个**错误**结论，
    /// 把运维支去查驱动，而这台机器的驱动其实好得很。</para>
    /// </param>
    public static ThermalPick Pick(
        IEnumerable<ThermalSensorReading>? sensors,
        IEnumerable<string>? presentCpuHardwareNames = null)
    {
        if (sensors is null) return ThermalPick.Empty;

        var cpu = PickCpu(sensors, presentCpuHardwareNames);
        var gpu = PickGpu(sensors);
        var storage = PickStorage(sensors);

        return new ThermalPick(cpu.Value, gpu, storage, cpu.Source, cpu.Note);
    }

    /// <summary>
    /// 这个读数是否可信。<c>NaN</c>（LHM 的"这次没读到"）、无穷大、以及 [-40, 150] 之外的
    /// 离谱值一律不可信。<b>0 ℃ 是合法读数</b>，见类型注释里那段。
    /// </summary>
    public static bool IsPlausible(double valueC)
        => !double.IsNaN(valueC)
           && !double.IsInfinity(valueC)
           && valueC >= MinPlausibleC
           && valueC <= MaxPlausibleC;

    // ───────────────────────────────────────────────────────────────────────
    // CPU
    // ───────────────────────────────────────────────────────────────────────

    private static (double? Value, string? Source, string? Note) PickCpu(
        IEnumerable<ThermalSensorReading> sensors,
        IEnumerable<string>? presentCpuHardwareNames)
    {
        double? package = null;      // 第 1 档：厂商口径的"整颗 CPU"
        string? packageSource = null;

        double? ccdMax = null;       // 第 2 档：多 CCD 里的最高温
        string? ccdMaxSource = null;

        double? average = null;      // 第 3 档：LHM 算的平均
        string? averageSource = null;

        double coreSum = 0;          // 第 4 档：逐核心平均
        var coreCount = 0;
        string? coreSource = null;

        double? fallback = null;     // 第 5 档：CPU 硬件上第一路有值的
        string? fallbackSource = null;

        double? superIo = null;      // 第 6 档：主板 SuperIO 的 "CPU Core"
        string? superIoSource = null;

        var hardwareNames = new List<string>();
        var sensorNames = new List<string>();
        var sawAnySensor = false;

        // 先记下"枚举到哪些 CPU"（哪怕它们一个温度传感器都没有）。
        if (presentCpuHardwareNames is not null)
        {
            foreach (var name in presentCpuHardwareNames)
            {
                if (!string.IsNullOrWhiteSpace(name)) AddUnique(hardwareNames, name);
            }
        }

        foreach (var sensor in sensors)
        {
            if (sensor.Kind == ThermalHardwareKind.Motherboard)
            {
                // 主板只认**明确带 CPU 字样**的温度。SuperIO 里还有 "System" / "Chipset" /
                // "VRM" —— 拿它们当 CPU 温度会得到一条"常年 35℃"的假曲线（比 — 危险得多）。
                if (superIo is not null) continue;

                var mainboardName = sensor.SensorName ?? string.Empty;

                if (mainboardName.Contains("cpu", StringComparison.OrdinalIgnoreCase)
                    && IsPlausible(sensor.ValueC))
                {
                    superIo = sensor.ValueC;
                    superIoSource = Describe(sensor);
                }

                continue;
            }

            if (sensor.Kind != ThermalHardwareKind.Cpu) continue;

            if (!string.IsNullOrWhiteSpace(sensor.HardwareName)) AddUnique(hardwareNames, sensor.HardwareName);

            var name = sensor.SensorName ?? string.Empty;

            // 诊断清单：**不管值可不可信**都记下来，否则"有传感器但读数全是 0"会被说成"没有传感器"。
            if (name.Length > 0)
            {
                sawAnySensor = true;
                AddUnique(sensorNames, name);
            }

            if (!IsPlausible(sensor.ValueC)) continue;

            var value = sensor.ValueC;
            var source = Describe(sensor);

            fallback ??= value;
            fallbackSource ??= source;

            var lower = name.ToLowerInvariant();

            // ⚠️ CCD / CCX 必须先判：AMD 的 "CCD1 (Tdie)"、"CCDs Max (Tdie)" 都含 tdie，
            //    当成"整颗 CPU"会把某一个 CCD 的裸片温度报成 CPU 温度。
            var isCcd = lower.Contains("ccd") || lower.Contains("ccx");

            if (!isCcd && (lower.Contains("package") || lower.Contains("tctl") || lower.Contains("tdie")))
            {
                package ??= value;
                packageSource ??= source;
            }
            else if (isCcd && lower.Contains("max"))
            {
                ccdMax ??= value;
                ccdMaxSource ??= source;
            }
            else if (lower.Contains("average"))
            {
                average ??= value;
                averageSource ??= source;
            }
            else if (lower.Contains("core") || isCcd)
            {
                coreSum += value;
                coreCount++;

                // 只记第一个名字，项数在最后拼 —— 在循环里拼会写出"等 1 项平均"这种半成品。
                coreSource ??= Describe(sensor);
            }
        }

        if (package is { } p) return (p, packageSource, null);
        if (ccdMax is { } m) return (m, ccdMaxSource, null);
        if (average is { } a) return (a, averageSource, null);

        if (coreCount > 0)
        {
            var source = coreSource is null ? "逐核心平均" : $"{coreSource} 等 {coreCount} 项平均";

            return (coreSum / coreCount, source, null);
        }

        if (fallback is { } f) return (f, fallbackSource, null);

        if (superIo is { } s)
        {
            // ⚠️ 这一档必须**标出来源**：主板 SuperIO 的 "CPU Core" 是老平台唯一能拿到的东西，
            //    但它测的是插槽附近，和 Tdie 差几度。不标注，运维会拿它跟厂商软件对不上。
            return (s, $"{superIoSource}（主板传感器）", null);
        }

        return (null, null, DescribeMissingCpu(hardwareNames.Count > 0, sawAnySensor, hardwareNames, sensorNames));
    }

    /// <summary>
    /// CPU 没读到温度时，把"为什么"说到能直接分派动作的程度：
    /// 去查驱动 / 去换驱动库 / 这颗机器本来就没有。
    /// </summary>
    private static string DescribeMissingCpu(
        bool sawCpuHardware,
        bool sawAnySensor,
        IReadOnlyList<string> hardwareNames,
        IReadOnlyList<string> sensorNames)
    {
        if (!sawCpuHardware)
        {
            return "CPU 没读到温度：硬件枚举里没有 CPU 项 —— 内核驱动大概率没有真正生效"
                + "（被安全软件拦截 / 进程没有管理员或 SYSTEM 权限）。";
        }

        var cpuName = hardwareNames.Count > 0 ? string.Join(" / ", hardwareNames) : "（型号未知）";

        if (!sawAnySensor)
        {
            return $"CPU 没读到温度：已枚举到「{cpuName}」，但驱动库没在这颗 CPU 上识别出任何温度传感器。"
                + "常见原因：这颗 CPU 比驱动库（LibreHardwareMonitor 0.9.3）新（如 Zen5 / 部分 Zen4 APU），"
                + "或该平台只把温度暴露在主板上。";
        }

        return $"CPU 没读到温度：已枚举到「{cpuName}」的传感器（{JoinNames(sensorNames)}），"
            + "但读数全是 NaN / 超出 [-40, 150] ℃ 的无效值 —— 驱动读到了传感器、没读到实际温度。";
    }

    // ───────────────────────────────────────────────────────────────────────
    // GPU / 存储
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GPU 温度。分级：<c>Core</c> → <c>Temperature</c>/<c>GPU</c> → <c>Hot Spot</c>/<c>Junction</c>。
    /// <para>
    /// 🔥 <b>结温（Hot Spot / Junction）必须排最后</b>：它比核心温度高 10~20℃，
    /// 拿它当"显卡温度"会让运维以为机器过热（真实案例：核心 80℃ 对应结温 100℃）。
    /// </para>
    /// <para>多块显卡（核显 + 独显）时取<b>第一个</b>命中的 —— 驱动枚举顺序通常是独显在前。</para>
    /// </summary>
    private static double? PickGpu(IEnumerable<ThermalSensorReading> sensors)
    {
        double? hotSpot = null;
        double? fallback = null;

        foreach (var sensor in sensors)
        {
            if (sensor.Kind != ThermalHardwareKind.Gpu) continue;
            if (!IsPlausible(sensor.ValueC)) continue;

            var value = sensor.ValueC;
            var name = (sensor.SensorName ?? string.Empty).ToLowerInvariant();

            fallback ??= value;

            if (name.Contains("hot spot") || name.Contains("hotspot") || name.Contains("junction"))
            {
                hotSpot ??= value;
                continue;
            }

            if (name.Contains("core") || name.Contains("temperature") || name.Contains("gpu"))
            {
                return value;
            }
        }

        return hotSpot ?? fallback;
    }

    /// <summary>
    /// 物理磁盘温度，多块盘取<b>最高</b> —— 这个字段回答的是"有没有哪块盘快热炸了"，
    /// 平均值会把一块 70℃ 的盘混在一块 30℃ 的盘里，看着一切正常。
    /// <para>⚠️ SATA 机械盘大多不暴露温度，只有部分盘有值是正常的。</para>
    /// </summary>
    private static double? PickStorage(IEnumerable<ThermalSensorReading> sensors)
    {
        double? max = null;

        foreach (var sensor in sensors)
        {
            if (sensor.Kind != ThermalHardwareKind.Storage) continue;
            if (!IsPlausible(sensor.ValueC)) continue;

            if (max is null || sensor.ValueC > max) max = sensor.ValueC;
        }

        return max;
    }

    // ───────────────────────────────────────────────────────────────────────
    // 小工具
    // ───────────────────────────────────────────────────────────────────────

    private static string Describe(ThermalSensorReading sensor)
        => string.IsNullOrWhiteSpace(sensor.HardwareName)
            ? sensor.SensorName ?? "未知传感器"
            : $"{sensor.HardwareName} / {sensor.SensorName ?? "未知传感器"}";

    private static void AddUnique(List<string> list, string value)
    {
        if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
    }

    private static string JoinNames(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return "无";

        var head = string.Join(" / ", names.Take(MaxNamesInNote));

        return names.Count > MaxNamesInNote ? $"{head} 等 {names.Count} 项" : head;
    }
}
