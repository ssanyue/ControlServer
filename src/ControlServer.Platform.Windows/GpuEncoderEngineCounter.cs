using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// 从 PDH 的 <c>\GPU Engine(*)\Utilization Percentage</c> 里数<b>编码引擎</b>实例（M5.0 第二批）。
///
/// <para>
/// 🔥🔥 <b>它给出的只是"下界"，而且必须先读懂这一点再决定怎么用。</b>
/// 2026-10-15 在本机实测：该计数器的<b>全部 231 条实例都带 <c>pid_</c> 前缀</b>
/// —— 也就是说它是「<b>哪个进程正在用哪个引擎</b>」的计数器，
/// <b>没有编码活动时根本不会产生 <c>VideoEncode</c> 实例</b>。
/// </para>
///
/// <list type="bullet">
///   <item>观测到 N 个 ⇒ 这台机器<b>至少有</b> N 个编码引擎在刚才那一刻被用过（可信）；</item>
///   <item>观测到 0 个 ⇒ <b>什么都说明不了</b>（可能没有硬编，也可能只是没人编码）。
///         把这一条当成"没有硬编"是<b>错的</b>，而且是本方案最不能犯的那种错。</item>
/// </list>
///
/// <para>
/// ⇒ 所以它在本项目里的定位是：<b>在推流过程中给引擎数一个可信的下界</b>
/// （远控一连上就必然有编码活动），而 <b>空闲时的探测不能靠它下结论</b> ——
/// 那一步交给 <see cref="MediaFoundationEncoders"/> 的注册表侧证据。
/// </para>
///
/// <para>
/// ⚠️ <b>另一条实测出来的坑</b>：MFT 枚举里每个编码器会返回 2 条同名记录，
/// 但那是"驱动按多种设备型态各注册一份"，<b>不是 2 个引擎</b>。
/// 引擎数只能来自这里（或厂商 API），绝不能来自 MFT 的条目数。
/// </para>
/// </summary>
internal sealed class GpuEncoderEngineCounter : IDisposable
{
    /// <summary>
    /// 唯一的计数器路径。
    /// <para>PDH 的 GPU 计数器只有一个「引擎」对象（<c>GPU Engine</c>），
    /// 没有"按适配器"的变体 —— 适配器身份藏在实例名的 <c>luid_</c> 段里。</para>
    /// </summary>
    private static readonly string[] CounterPaths = { @"\GPU Engine(*)\Utilization Percentage" };

    private readonly PdhQuery? _query;

    private GpuEncoderEngineCounter(PdhQuery? query) => _query = query;

    /// <summary>
    /// 建一个计数器。<b>不抛异常</b>：一台关掉了性能计数器的加固系统
    /// 不该让整个能力探测崩掉（探测的第一原则是"不确定就承认不确定"）。
    /// </summary>
    internal static GpuEncoderEngineCounter Create()
        => new(OperatingSystem.IsWindows() ? PdhQuery.TryCreate(CounterPaths) : null);

    /// <summary>
    /// 数一数此刻<b>正在被使用</b>的编码引擎实例。
    ///
    /// <para>
    /// 返回 <c>null</c> = <b>数不了</b>（计数器不可用）；返回空表 = 数得了，但此刻一个都没有。
    /// <b>两者绝不可混同</b>：前者是"不知道"，后者才是"观测到 0"。
    /// </para>
    /// </summary>
    internal IReadOnlyDictionary<GpuAdapterNames.Luid, int>? TryCount()
    {
        if (_query is null) return null;

        try
        {
            _query.Collect();

            var values = _query.ReadNamedArray(0);

            if (values is null) return null;

            var counts = new Dictionary<GpuAdapterNames.Luid, HashSet<int>>();

            foreach (var (name, _) in values)
            {
                if (!TryParseInstance(name, out var luid, out var engineIndex, out var engineType)) continue;

                // ⚠️ 只认编码引擎。名字里的 engtype 大小写在各版本 Windows 上并不统一
                //    （本机实测给出的是小写 `videodecode` / `videoprocessing`，
                //     而同仓旧代码的注释里记的是大写 `3D`）⇒ 必须大小写不敏感。
                if (!engineType.Equals("VideoEncode", StringComparison.OrdinalIgnoreCase)) continue;

                if (!counts.TryGetValue(luid, out var engines))
                {
                    engines = new HashSet<int>();
                    counts[luid] = engines;
                }

                engines.Add(engineIndex);
            }

            // 🔥 数的是**不同的引擎序号**，不是实例条数：
            //    同一个引擎会被多个进程各报一条（还可能有多个 pidi 实例），
            //    直接数条数会把"3 个进程在编码"报成"3 个引擎"。
            return counts.ToDictionary(pair => pair.Key, pair => pair.Value.Count);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 拆实例名：
    /// <c>pid_10508_luid_0x00000000_0x0000e577_phys_0_eng_0_engtype_VideoEncode</c>。
    /// <para>
    /// 按 <c>_</c> 切段后找关键字，而不是按固定下标取 —— 段数在不同 Windows 版本上出过硬差异
    /// （<c>phys_</c> 段在有些版本上不出现），按下标取会<b>静默错位</b>。
    /// </para>
    /// </summary>
    internal static bool TryParseInstance(string? instanceName, out GpuAdapterNames.Luid luid, out int engineIndex, out string engineType)
    {
        luid = default;
        engineIndex = -1;
        engineType = string.Empty;

        if (string.IsNullOrWhiteSpace(instanceName)) return false;

        var parts = instanceName.Split('_');

        var haveLuid = false;
        var haveEngine = false;
        var haveType = false;

        for (var i = 0; i < parts.Length; i++)
        {
            switch (parts[i])
            {
                case "luid" when i + 2 < parts.Length:
                    haveLuid = GpuAdapterNames.TryParseLuidRaw(parts[i + 1], parts[i + 2], out luid);
                    break;

                case "eng" when i + 1 < parts.Length:
                    haveEngine = int.TryParse(parts[i + 1], out engineIndex);
                    break;

                case "engtype" when i + 1 < parts.Length:
                    engineType = parts[i + 1];
                    haveType = !string.IsNullOrEmpty(engineType);
                    break;
            }
        }

        return haveLuid && haveEngine && haveType;
    }

    public void Dispose() => _query?.Dispose();
}
