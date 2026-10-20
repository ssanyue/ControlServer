using System.Runtime.Versioning;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// Windows 上的硬件编码<b>能力探测</b>（M5.0 第二批：三条证据链合并）。
///
/// <para><b>三条证据各自回答什么</b></para>
/// <list type="number">
///   <item><b>DXGI 适配器枚举</b> ⇒ <b>事实</b>：本机有哪些适配器、厂商 / 设备标识、显存、
///         是不是软件光栅器。不含任何推断。</item>
///   <item><b>Media Foundation 硬件编码器枚举</b>（<see cref="MediaFoundationEncoders"/>）
///         ⇒ <b>这台机器确实有硬编的是哪些格式、由哪家厂商提供</b>。
///         这是"明确无硬编"（引擎数 <c>0</c>）唯一的可靠依据 ——
///         它查的是驱动注册了什么，与当前有没有编码活动无关。</item>
///   <item><b>PDH 编码引擎实例</b>（<see cref="GpuEncoderEngineCounter"/>）
///         ⇒ 此刻<b>正在被使用</b>的编码引擎数，是引擎数的 <b>下界</b>。</item>
/// </list>
///
/// <para>
/// 🔥 <b>为什么"引擎数"这么难拿</b>（2026-10-15 本机实测后的结论）：
/// 方案原定用 PDH 的 <c>engtype_VideoEncode</c> 实例数当引擎数，实测发现
/// <b>该计数器的实例是按进程活动创建的</b>（本机 231 条实例全带 <c>pid_</c> 前缀）——
/// 没有编码活动就一条都没有。<b>一台空转的 4090 与一台 MX250 在这上面长得一模一样</b>，
/// 拿它下结论会把"没用过"说成"没有"。
/// ⇒ 所以这里把职责分开：<b>有没有</b>看 MFT（可靠），<b>有几个</b>只在有活动时给出并标明是下界。
/// 精确引擎数（决定 8K60 能否走双引擎 SFE）留待 M5.4 推流时实测 —— 那正是"尽力做"的验收项。
/// </para>
///
/// <para>
/// <b>为什么探测结果要缓存</b>：枚举 DXGI 要走 COM（裸 vtable），MFT 枚举要走另一套 COM。
/// 硬件能力在一次服务生命周期内不会变（换显卡要重启服务），
/// 但页面每次打开都会问一次 ⇒ 缓存 10 分钟既省开销，也不影响正确性。
/// </para>
///
/// <para>
/// 🔥 <b>一个实测出来的坑：DXGI 的适配器节点数 ≠ 显卡块数。</b>
/// 本机（Intel UHD 620 + NVIDIA MX250）实测 DXGI 枚举出 <b>4</b> 个适配器，
/// 而"Intel UHD Graphics 620"出现了<b>两次且 LUID 各不相同</b>
/// （<c>0x0000E577</c> / <c>0x00019C01</c>，DeviceId 相同）——
/// 也就是说同一块物理显卡可以对应<b>多个适配器节点</b>。
/// 直接罗列会让页面看起来像"装了两块 Intel 显卡"，而它们是同一块。
/// ⇒ 因此本报告按<b>型号</b>（<c>VendorId + DeviceId + 是否软件</c>）汇总。
/// </para>
///
/// <para>
/// ⚠️ 由此引申一条给 M5.1 的约束：<b>这份报告不是"抓屏设备清单"</b>。
/// 选 D3D 设备时<b>必须自己重新枚举 DXGI 并凭 LUID 挑</b> ——
/// 节点在"用哪块卡干活"这件事上是有意义的（可能只有其中一个节点有输出），
/// 只是对"能力"没有意义。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsEncoderCapabilityProbe : IEncoderCapabilityProbe
{
    /// <summary>探测结果的缓存时长。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>MFT 查询失败时用的依据文案。<b>它必须读起来像"不知道"，而不是"没有"。</b></summary>
    private const string BasisMftUnavailable = "未能查询 Media Foundation 的硬件编码器（查询本身失败了）——"
                                               + "这表示「不知道」，不表示「这台机器没有硬编」";

    /// <summary>
    /// "数不了"的依据文案。与 <see cref="BasisMftUnavailable"/> 同理，
    /// 刻意写成一句人话，好让现场一眼分清"没测"与"没有"。
    /// </summary>
    private const string BasisEngineUnavailable = "未能读取 GPU Engine 性能计数器，引擎数未知";

    private readonly object _gate = new();

    private EncoderCapabilityReport? _cached;
    private DateTimeOffset _cachedAt;

    /// <inheritdoc />
    public Task<EncoderCapabilityReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
            {
                return Task.FromResult(_cached);
            }
        }

        var report = Probe();

        lock (_gate)
        {
            _cached = report;
            _cachedAt = DateTimeOffset.UtcNow;
        }

        return Task.FromResult(report);
    }

    private static EncoderCapabilityReport Probe()
    {
        var dxgi = EnumerateAdapters();

        // null = "查不了"（不是"没有"，两者绝不能混）。
        var hardware = OperatingSystem.IsWindows() ? MediaFoundationEncoders.TryEnumerateHardware() : null;

        using var counter = GpuEncoderEngineCounter.Create();

        var liveEngines = counter.TryCount();

        var adapters = new List<EncoderAdapterCapability>(dxgi.Count);

        // 按**型号**汇总（理由见类注释：DXGI 会把同一块卡暴露成多个节点）。
        // 每个分组保留它全部节点的 LUID —— 引擎数要按 LUID 查，不能只看第一个。
        foreach (var group in dxgi.GroupBy(a => (a.VendorId, a.DeviceId, a.IsSoftware)))
        {
            adapters.Add(Describe(
                group.First(),
                group.Select(a => a.Luid).ToList(),
                hardware,
                liveEngines));
        }

        // 全机支持格式 = 物理显卡的并集（软件光栅器不参与：它本来就不是一块显卡）。
        var supported = adapters
            .Where(a => !a.IsSoftwareRenderer)
            .SelectMany(a => a.SupportedCodecs)
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        return new EncoderCapabilityReport(
            Adapters: adapters,
            SupportedCodecs: supported,

            // ⚠️ 探测层**刻意不推荐**编码格式：格式由**目标分辨率**决定
            //    （≤4096 宽可 H.264，更宽只能 HEVC），而分辨率是**会话**才知道的事
            //    （同一台机器接不同的屏，答案就不同）。
            //    在这里给一个格式，等于替一个还不知道分辨率的会话做决定 —— 这正是
            //    "没测出来的东西不许给建议值"那条纪律要拦的情形。
            RecommendedCodec: null,

            Summary: Summarize(adapters, dxgi.Count, supported, hardware, liveEngines));
    }

    /// <summary>枚举 DXGI 适配器；失败时返回空表（由 <see cref="Summarize"/> 说明"没探到"）。</summary>
    private static IReadOnlyList<GpuAdapterNames.Dxgi.Adapter> EnumerateAdapters()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<GpuAdapterNames.Dxgi.Adapter>();

        try
        {
            return GpuAdapterNames.Dxgi.Enumerate();
        }
        catch (Exception)
        {
            // 枚举失败不该把"探测"变成"崩"：结论退化为"一个都没探到"，
            // 由 Summary 把这件事说清楚（而不是伪装成"这台机器没有显卡"）。
            return Array.Empty<GpuAdapterNames.Dxgi.Adapter>();
        }
    }

    /// <summary>
    /// 给一块适配器下结论。
    /// <para>
    /// 🔥 三条分支的<b>先后顺序是有讲究的</b>：观测到的活动（PDH）优先于注册表证据（MFT），
    /// 因为前者是"我亲眼看到它在编"，后者是"驱动说它能编"。
    /// 而当两者都指向"没有"时，才敢写 <c>0</c>；任何一环"查不了"都退回 <c>null</c>。
    /// </para>
    /// </summary>
    private static EncoderAdapterCapability Describe(
        GpuAdapterNames.Dxgi.Adapter adapter,
        IReadOnlyList<GpuAdapterNames.Luid> luids,
        IReadOnlyList<MediaFoundationEncoders.HardwareEncoder>? hardware,
        IReadOnlyDictionary<GpuAdapterNames.Luid, int>? liveEngines)
    {
        // ── 软件光栅器（WARP）：它不是一块显卡，没有也不可能有硬件编码器 ──
        if (adapter.IsSoftware)
        {
            return new EncoderAdapterCapability(
                adapter.Description,
                adapter.VendorId,
                adapter.DeviceId,
                adapter.DedicatedVideoMemoryBytes,
                IsSoftwareRenderer: true,
                SupportedCodecs: Array.Empty<VideoCodec>(),
                EncodeEngines: 0,
                EngineBasis: "软件光栅器（Windows 自带的 WARP），不是物理显卡，没有硬件编码器");
        }

        // ── MFT 查不了 ⇒ 这台机器的编码能力**不可判定** ──
        if (hardware is null)
        {
            return new EncoderAdapterCapability(
                adapter.Description,
                adapter.VendorId,
                adapter.DeviceId,
                adapter.DedicatedVideoMemoryBytes,
                IsSoftwareRenderer: false,
                SupportedCodecs: Array.Empty<VideoCodec>(),
                EncodeEngines: null,
                EngineBasis: BasisMftUnavailable);
        }

        // ── 这块卡支持哪些硬编格式（按厂商归属）──
        var codecs = hardware
            .Where(h => GpuVendors.FriendlyNameMatchesVendor(h.FriendlyName, adapter.VendorId))
            .Select(h => h.Codec)
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        // ── 此刻是否确实观测到它在编码（同一块卡可能有多个节点，取各节点的最大值）──
        //
        // ⚠️ 取 max 而不是求和：那些节点是**同一块物理显卡**，
        //    求和会凭空把 2 个引擎报成 4 个 —— 正好会把"够不够 8K60"的结论带偏。
        int? observed = null;

        if (liveEngines is not null)
        {
            foreach (var luid in luids)
            {
                if (liveEngines.TryGetValue(luid, out var count))
                {
                    observed = observed is { } current ? Math.Max(current, count) : count;
                }
            }
        }

        // ── ① 此刻确实观测到它在编码 ⇒ 给出下界（可信，且优先）──
        if (observed is > 0)
        {
            return new EncoderAdapterCapability(
                adapter.Description,
                adapter.VendorId,
                adapter.DeviceId,
                adapter.DedicatedVideoMemoryBytes,
                IsSoftwareRenderer: false,
                codecs,
                EncodeEngines: observed,
                EngineBasis: $"PDH 实测：探测这一刻有 {observed} 个编码引擎正在被使用。"
                             + "⚠️ 这是**下界**不是硬件总数 —— 该计数器按进程活动创建实例，"
                             + "空闲的引擎不会出现在里面；要拿硬件上限请在推流过程中再测一次。");
        }

        // ── ② 注册表侧明确没有这家的硬件编码器 ⇒ 可以下"0" ──
        if (codecs.Count == 0)
        {
            return new EncoderAdapterCapability(
                adapter.Description,
                adapter.VendorId,
                adapter.DeviceId,
                adapter.DedicatedVideoMemoryBytes,
                IsSoftwareRenderer: false,
                codecs,
                EncodeEngines: 0,
                EngineBasis: $"Media Foundation 枚举**成功**且未发现 {GpuVendors.Describe(adapter.VendorId)} 的任何硬件编码器"
                             + "（这与「没查出来」不同：枚举本身是成功的）");
        }

        // ── ③ 有硬编，但引擎数拿不到 ⇒ 如实说 null ──
        var names = string.Join("、", hardware
            .Where(h => GpuVendors.FriendlyNameMatchesVendor(h.FriendlyName, adapter.VendorId))
            .Select(h => h.FriendlyName)
            .Distinct());

        var engineNote = liveEngines is null ? BasisEngineUnavailable : "此刻没有观测到它在编码";

        return new EncoderAdapterCapability(
            adapter.Description,
            adapter.VendorId,
            adapter.DeviceId,
            adapter.DedicatedVideoMemoryBytes,
            IsSoftwareRenderer: false,
            codecs,
            EncodeEngines: null,
            EngineBasis: $"已确认有硬编（{names}），但**引擎数未能实测**：{engineNote}。"
                         + "8K60 需要双引擎 + 分帧编码（SFE），这一条必须在 M5.4 推流时实测，不能靠推断。");
    }

    private static string Summarize(
        IReadOnlyList<EncoderAdapterCapability> adapters,
        int dxgiNodeCount,
        IReadOnlyList<VideoCodec> supported,
        IReadOnlyList<MediaFoundationEncoders.HardwareEncoder>? hardware,
        IReadOnlyDictionary<GpuAdapterNames.Luid, int>? liveEngines)
    {
        if (adapters.Count == 0)
        {
            return "未能枚举到任何显示适配器 —— 说明这次探测**没探到**，"
                   + "不等于这台机器没有显卡、更不等于它不能硬件编码。";
        }

        var physical = adapters.Count(a => !a.IsSoftwareRenderer);

        var parts = new List<string> { $"枚举到 {adapters.Count} 款适配器（物理显卡 {physical} 款）" };

        // 🔥 DXGI 的节点数与型号数不一致时**必须说出来**：本机实测 Intel 核显就有两个节点，
        //    不说的话，读的人拿着 Windows 设备管理器一对（只有 2 块卡）就会以为探测漏了或错了。
        if (dxgiNodeCount > adapters.Count)
        {
            parts.Add($"（DXGI 暴露了 {dxgiNodeCount} 个适配器节点，已按型号归并 ——"
                      + "同一块物理显卡可能对应多个节点，本机实测 Intel 核显就是如此）");
        }

        if (hardware is null)
        {
            parts.Add("Media Foundation 硬件编码器查询失败 ⇒ 本机编码能力**未知**（不是「不支持」）");
        }
        else if (supported.Count == 0)
        {
            parts.Add("未发现任何硬件编码器（枚举成功）⇒ 远控只能走软件编码，"
                      + "4K60 及以上的指标无法达成");
        }
        else
        {
            var vendors = adapters
                .Where(a => !a.IsSoftwareRenderer && a.SupportedCodecs.Count > 0)
                .Select(a => $"{a.Name}（{string.Join("/", a.SupportedCodecs.Select(CodecName))}）");

            parts.Add($"发现硬件编码器：{string.Join("；", vendors)}");
        }

        // 引擎数只在真的观测到的时候才提 —— 没观测到不代表没有，写上去会被误读。
        if (liveEngines is { Count: > 0 })
        {
            parts.Add($"探测这一刻观测到 {liveEngines.Values.Sum()} 个编码引擎在使用中（下界）");
        }
        else
        {
            parts.Add("引擎数未实测（GPU Engine 计数器按进程活动创建，空闲时不产生实例）——"
                      + "8K60 能否走双引擎须在推流过程中实测");
        }

        return string.Join("；", parts) + "。";
    }

    private static string CodecName(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H.264",
        VideoCodec.Hevc => "HEVC",
        _ => codec.ToString(),
    };
}
