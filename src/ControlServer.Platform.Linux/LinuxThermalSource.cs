using System.Globalization;
using ControlServer.Contracts;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Linux;

/// <summary>
/// Linux 上的硬件温度采集（M7.2）：<b>只读 <c>sysfs</c></b>，不需要 root、不加载任何驱动。
///
/// <para>
/// 🔥 <b>与 Windows 那条路的根本差别</b>：那边要读内核级驱动（WinRing0），
/// 于是有"装没装 / 被拦截 / 没权限"这一整类失败；这边 <c>/sys/class/thermal</c>
/// 与 <c>/sys/class/hwmon</c> 是普通文件，<b>有没有读数只取决于内核暴不暴露那个传感器</b>。
/// 所以这一实现不需要开关、不碰特权，也不存在"驱动加载失败"。
/// </para>
///
/// <para>
/// ⚠️ <b>但"没有读数"是常态，不是异常</b>：虚拟机、容器、部分 ARM 板子根本没有
/// <c>thermal_zone</c>。那时 <see cref="State"/> 是 <see cref="AgentThermalStates.Failed"/>
/// 且 <see cref="Note"/> 说清"这台机器没有暴露任何温度传感器" —— 页面显示「—」而不是 0。
/// </para>
///
/// <para>
/// 🔥 <b>为什么不按 <c>thermal_zone0</c> 之类的序号取值</b>：那个序号是**内核枚举顺序**，
/// 不同机器上 <c>zone0</c> 可能是 CPU、也可能是电池、还可能是一块根本不动的虚拟 zone。
/// 拿错的表现是 <b>CPU 温度常年 27℃</b> 而风扇在狂转 —— 一个看着完全正常的错值。
/// ⇒ 一律按 <c>type</c> / hwmon 的 <c>name</c> <b>分类</b>，分不出来的 sensor 直接不用。
/// </para>
/// </summary>
public sealed class LinuxThermalSource : IThermalSource
{
    private const string ThermalRoot = "/sys/class/thermal";
    private const string HwmonRoot = "/sys/class/hwmon";

    /// <summary>合理温度区间（℃）。超出这个范围的读数是"没读到"，不是"很冷/很热"。</summary>
    private const double MinPlausibleC = -20;
    private const double MaxPlausibleC = 150;

    private readonly IReadOnlyList<Sensor> _sensors;

    /// <param name="note">没读到任何传感器时给现场看的原因（构造函数里算清楚）。</param>
    public LinuxThermalSource()
    {
        _sensors = Discover();

        // 🔥 状态在**构造时定一次**（与 Windows 那个实现同一条纪律）：
        //    每 5 秒重扫一遍 sysfs 不会带来新信息，只会让日志里那行状态来回跳。
        if (_sensors.Count == 0)
        {
            State = AgentThermalStates.Failed;
            Note = $"{ThermalRoot} 与 {HwmonRoot} 里都没有可用的温度传感器"
                + "（虚拟机 / 容器 / 未暴露传感器的板子上很常见）—— 这不是故障，是这台机器没有温度可读。";
        }
        else
        {
            State = AgentThermalStates.Ok;
            Note = null;
        }
    }

    /// <inheritdoc />
    public string State { get; }

    /// <inheritdoc />
    public string? Note { get; }

    /// <summary>这一台机器上找到的传感器分类数（诊断用：页面/日志能说"找到 3 个"，不是一个笼统的 ok）。</summary>
    public int SensorCount => _sensors.Count;

    /// <inheritdoc />
    public HostThermalReading Read()
    {
        if (_sensors.Count == 0) return HostThermalReading.Empty;

        double? cpu = null, gpu = null, storage = null;

        // ⚠️ 同一类有多个传感器时**取最先命中的那个**（发现阶段已经按优先级排好序）：
        //    取平均会把"CPU 封装 61℃ + 某个空转的 zone 27℃"算成 44℃ —— 一个不存在的温度。
        foreach (var sensor in _sensors)
        {
            var value = ReadCelsius(sensor.Path);
            if (value is null) continue;

            switch (sensor.Kind)
            {
                case SensorKind.Cpu when cpu is null: cpu = value; break;
                case SensorKind.Gpu when gpu is null: gpu = value; break;
                case SensorKind.Storage when storage is null: storage = value; break;
            }
        }

        return new HostThermalReading(cpu, gpu, storage);
    }

    /// <summary>读一个 sysfs 温度文件（毫摄氏度）。读不到 / 不合理 ⇒ <c>null</c>。</summary>
    private static double? ReadCelsius(string path)
    {
        try
        {
            var raw = File.ReadAllText(path);

            return TryParseMilliCelsius(raw, out var celsius) ? celsius : null;
        }
        catch (Exception)
        {
            // ⚠️ 绝不抛：单个传感器消失（USB 硬盘拔掉、驱动重载）不该让整个遥测停摆。
            return null;
        }
    }

    /// <summary>
    /// 把 sysfs 的毫摄氏度文本转成摄氏度。
    /// <para>
    /// ⚠️ 合理性检查在这里而不在调用方：<c>0</c>、<c>-273000</c>、<c>999999</c>
    /// 都是真实会出现的值（驱动没填 / 传感器故障），它们是"没读到"而不是"读数"。
    /// </para>
    /// </summary>
    internal static bool TryParseMilliCelsius(string? raw, out double celsius)
    {
        celsius = 0;

        if (string.IsNullOrWhiteSpace(raw)) return false;

        if (!double.TryParse(
                raw.Trim(),
                NumberStyles.Integer | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var milli))
        {
            return false;
        }

        var value = milli / 1000.0;

        // 🔥 **恰好 0℃ 按"没读到"处理**（与上面那段注释同一条纪律）：
        //    驱动没填 / 传感器没接时 sysfs 里就是一个 `0`，而它在数值上完全合法。
        //    一台机器在 0℃ 的机房外还要正常开机，比"驱动给了个 0"少见得多；
        //    更关键的是：把 0 当读数画出来，现场看到的是"CPU 0℃"—— 一个看着正常、
        //    实际上什么信息都没有的数字。
        if (value == 0) return false;

        if (value < MinPlausibleC || value > MaxPlausibleC) return false;

        celsius = value;
        return true;
    }

    /// <summary>把 <c>thermal_zone*/type</c> 或 hwmon 的 <c>name</c> 归到三类之一。</summary>
    internal static SensorKind Classify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return SensorKind.Other;

        var text = name.Trim().ToLowerInvariant();

        // CPU：内核封装 / 各家驱动。⚠️ 顺序无所谓，但**不要**把 "cpu" 之外的泛词放前面，
        //      "acpitz" 是主板温度（经常恒为 27℃）—— 它既不是 CPU 也不是 GPU。
        if (text.Contains("x86_pkg_temp")
            || text.Contains("coretemp")
            || text.Contains("k10temp")
            || text.Contains("zenpower")
            || text.Contains("cpu")
            || text.Contains("soc"))
        {
            return SensorKind.Cpu;
        }

        if (text.Contains("amdgpu") || text.Contains("nouveau")
            || text.Contains("nvidia") || text.Contains("i915") || text.Contains("gpu"))
        {
            return SensorKind.Gpu;
        }

        if (text.Contains("nvme") || text.Contains("drivetemp")
            || text.Contains("sata") || text.Contains("ssd"))
        {
            return SensorKind.Storage;
        }

        return SensorKind.Other;
    }

    /// <summary>扫一遍 sysfs，按类别优先级排出候选（同类里越靠前越优先）。</summary>
    private static IReadOnlyList<Sensor> Discover()
    {
        var found = new List<Sensor>();

        // ── thermal_zone*（大多数 x86 / ARM 板子走这条）──
        try
        {
            if (Directory.Exists(ThermalRoot))
            {
                var zones = Directory.GetDirectories(ThermalRoot, "thermal_zone*");

                // ⚠️ 排序：`GetDirectories` 的顺序**不保证**是数字序，
                //    不排的话同一台机器两次启动可能挑到不同的 zone。
                Array.Sort(zones, StringComparer.Ordinal);

                foreach (var zone in zones)
                {
                    var kind = Classify(ReadTextOrNull(Path.Combine(zone, "type")));

                    if (kind == SensorKind.Other) continue;

                    var temp = Path.Combine(zone, "temp");
                    if (File.Exists(temp)) found.Add(new Sensor(kind, temp));
                }
            }
        }
        catch (Exception)
        {
            // 权限 / IO 出错就当这条来源没有。下面还有 hwmon。
        }

        // ── hwmon/hwmon*/temp1_input（NVMe、AMD GPU 常只出现在这里）──
        try
        {
            if (Directory.Exists(HwmonRoot))
            {
                var chips = Directory.GetDirectories(HwmonRoot, "hwmon*");
                Array.Sort(chips, StringComparer.Ordinal);

                foreach (var chip in chips)
                {
                    var kind = Classify(ReadTextOrNull(Path.Combine(chip, "name")));

                    if (kind == SensorKind.Other) continue;

                    var temp = Path.Combine(chip, "temp1_input");
                    if (File.Exists(temp)) found.Add(new Sensor(kind, temp));
                }
            }
        }
        catch (Exception)
        {
            // 同上。
        }

        // 同类里先来的先赢；同时保证 CPU 那一路优先用 thermal_zone（它是内核封装温度）。
        return found
            .OrderBy(s => s.Kind)
            .ToList();
    }

    private static string? ReadTextOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>三类可采的温度来源。</summary>
    internal enum SensorKind
    {
        Cpu = 0,
        Gpu = 1,
        Storage = 2,
        Other = 3,
    }

    private readonly record struct Sensor(SensorKind Kind, string Path);
}
