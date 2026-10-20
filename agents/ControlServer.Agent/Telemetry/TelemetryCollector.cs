using ControlServer.Contracts;
using ControlServer.Core.Services;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent.Telemetry;

/// <summary>
/// 客户端侧的遥测配置（<c>Agent:Telemetry</c> 节）。
/// <para>
/// <b>这里的每一项都只是"连上服务端之前的默认值"</b>：真正生效的节奏由服务端在
/// <c>Welcome</c> 里下发（见 <see cref="AgentTelemetrySettingsDto"/>）。
/// 保留本地默认值是为了"还没连上就已经开始采集" —— 否则窗口一断，曲线就出现一段空白。
/// </para>
/// </summary>
public sealed class AgentTelemetryOptions
{
    /// <summary>是否采集（默认开）。关掉后客户端连采样都不做，省电省 CPU。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>指标采样间隔（秒）。</summary>
    public int MetricsSeconds { get; set; } = 5;

    /// <summary>开关机事件轮询间隔（秒）。</summary>
    public int PowerEventPollSeconds { get; set; } = 30;

    /// <summary>硬件清单上报间隔（小时）。</summary>
    public int InventoryHours { get; set; } = 6;

    /// <summary>首次启动时回溯多少天的开关机事件。</summary>
    public int PowerEventBackfillDays { get; set; } = 7;

    /// <summary>单次上报的最大样本数（必须 ≤ 服务端的 <c>MaxBatchSamples</c>，否则整批被拒）。</summary>
    public int MaxBatchSamples { get; set; } = 600;

    /// <summary>
    /// 断线期间本地最多攒多少条样本。
    /// <para>
    /// 按 5 秒一条算，1200 条 ≈ 100 分钟。服务端重启 / 网络抖动通常几分钟内恢复，
    /// 攒得住；真断了 100 分钟以上，最老的数据也就没有保留价值了（服务端本来就只留 30 天明细）。
    /// </para>
    /// <para><b>必须有上限</b>：无界队列在"服务端地址配错"这种情形下会把客户机内存吃光。</para>
    /// </summary>
    public int MaxQueuedSamples { get; set; } = 1200;

    // ═══════════════════════════════════════════════════════════════
    // ⚠️ 这里原本有一个 `ThermalEnabled` 属性，**已于 2026-10-14 删除** ——
    //    它是**死代码**：全仓没有任何一处读或写它，真的开关在组合根里。
    //
    // 🔥 温度到底开不开，由**这两处**决定，别在这里找：
    //
    //   ① 安装时（正常现场）：安装向导「启用硬件温度监控」**默认勾选**（需求①），
    //      `install-agent.ps1` 会把 `Agent:Telemetry:ThermalEnabled=true` 显式写进
    //      `appsettings.json` ⇒ **装完就是开的**；取消勾选才写 false。
    //      这么定的理由：温度是"这台机器是不是快过热了"的唯一线索，而"过去的时间补不回来"——
    //      默认关省下的只是一个驱动加载动作，代价却是**永久缺失一段最需要的数据**。
    //
    //   ② 裸起（绿色版 / 手工排障 / 冒烟）：没有 `appsettings.json` ⇒ 键缺失
    //      ⇒ `WindowsThermalSource(false)` ⇒ **关**。这是**故意**的
    //      （见 `Agent.Service/Program.cs` 里那段注释与方案 §22）：
    //      一个会释放并加载内核级驱动（WinRing0）的能力不该"配置缺失时默认开"。
    //
    // ⚠️ 为什么非要把这段话说在这里：那个被删掉的属性默认值是 `true`，注释还写着
    //    "与安装勾选项一致"。任何人读到它都会以为"不带配置就是开" —— 而真实行为**正好相反**
    //    （冒烟里为此红过一条断言，见 REF §27.6）。
    //    一个**写了却不生效**的默认值，比没有这个属性更危险：它把人引向错误的结论。
    //
    // ⚠️ 另外，这一项**不进服务端下发的 `AgentTelemetrySettingsDto`**：
    //    它描述的是"这台机器上驱动装没装"这个**本机事实**，而不是一条可以全网统一的策略。
    //    让服务端能远程打开它，只会得到一批"驱动没装却以为开了"的机器。
    // ═══════════════════════════════════════════════════════════════
}

/// <summary>
/// 客户端遥测采集器（M4.2）：把平台端口采到的原始读数<b>攒成本地队列</b>，
/// 由连接层按批取走上报。
/// <para>
/// <b>为什么要有"攒"这一层</b>：采样节奏（5 秒）与上报节奏（连上才有意义）是两件独立的事。
/// 直接"采一条发一条"的话，断线期间的样本会就地丢失 —— 而"这台机器刚才是不是卡过"
/// 恰恰是断线前后最想知道的事。攒一层之后，重连的第一批就把缺口补上了。
/// </para>
/// <para>
/// <b>无锁化取舍</b>：采样在遥测循环里跑，取批也在同一个循环里跑，本来不需要锁。
/// 但 <see cref="PendingSampleCount"/> 会被别的线程读（日志 / 排障），所以用一个
/// 极短的锁把状态读写圈起来 —— 临界区里只有队列操作，没有 IO。
/// </para>
/// </summary>
public sealed class TelemetryCollector
{
    private readonly IHostMetricsSource _metrics;
    private readonly IPowerEventSource _power;
    private readonly IHostInventorySource _inventory;
    private readonly IThermalSource _thermal;
    private readonly AgentTelemetryOptions _options;
    private readonly TelemetryStateStore _state;
    private readonly LogService _log;

    private readonly LinkedList<AgentMetricsSampleDto> _pending = new();
    private readonly object _gate = new();

    /// <summary>
    /// 最近一次采到的硬件清单（<b>只用于给逐设备读数配名字</b>，2026-10-13）。
    /// <para>
    /// 平台层只知道"disk0 / cpu1 / luid_..._..."，型号在清单里（WMI）。
    /// 让平台层为了一个显示名去开 WMI，代价远大于收益 —— 所以命名这一步放在这里，
    /// 用的是**已经在内存里的那一份**（清单 6 小时采一次，本字段不产生任何额外采集）。
    /// </para>
    /// <para>null = 还没采过清单（刚启动的那几十秒），此时设备名用平台给的占位名。</para>
    /// </summary>
    private volatile AgentInventoryDto? _lastInventory;

    private long _droppedSamples;

    /// <summary>增量读取时向前开的重叠窗口（覆盖"关机瞬间补写事件"这类迟到写入）。</summary>
    private static readonly TimeSpan PowerEventOverlap = TimeSpan.FromHours(2);

    public TelemetryCollector(
        IHostMetricsSource metrics,
        IPowerEventSource power,
        IHostInventorySource inventory,
        IThermalSource thermal,
        AgentTelemetryOptions options,
        TelemetryStateStore state,
        LogService log)
    {
        _metrics = metrics;
        _power = power;
        _inventory = inventory;
        _thermal = thermal;
        _options = options;
        _state = state;
        _log = log;
    }

    /// <summary>本平台是否支持开关机记录（Linux 未实现 ⇒ false，会一路带到页面上）。</summary>
    public bool PowerEventsSupported => _power.IsAvailable;

    /// <summary>
    /// 硬件温度监控的状态（M4.3），见 <see cref="AgentThermalStates"/>。
    /// <para>
    /// ⚠️ <b>读它就可能触发驱动加载</b>（首次），因此这个方法只在"本来就要采温度"的路径上调用。
    /// 启动日志那条自述想要这个状态，就去调它 —— 那是唯一一次"提前"取值。
    /// </para>
    /// </summary>
    public string ThermalState => _thermal.State;

    /// <summary>温度状态的人话补充（失败原因 / 未启用原因），没有就是 null。</summary>
    public string? ThermalNote => _thermal.Note;

    /// <summary>当前还有多少样本没送出去。</summary>
    public int PendingSampleCount
    {
        get
        {
            lock (_gate) return _pending.Count;
        }
    }

    /// <summary>因队列上限被丢掉的样本总数（>0 说明断线时间超长，值得记一笔日志）。</summary>
    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);

    // ───────────────────────────────────────────────────────────────────────
    // 指标
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 采一次并入队。返回本次采到的样本（供"立刻发一条"用；不关心就用 <c>_</c> 丢）。
    /// <para><b>绝不抛</b>：采集源本身已经保证不抛，这里再兜一层 —— 遥测挂掉不该影响心跳。</para>
    /// </summary>
    public AgentMetricsSampleDto? Sample()
    {
        HostMetricReading reading;

        try
        {
            reading = _metrics.Read();
        }
        catch (Exception ex)
        {
            _log.Warn("Agent", $"指标采集失败（跳过本次）：{ex.Message}");
            return null;
        }

        // 温度（M4.3）：**一次采样只读一次**。
        // ⚠️ LHM 的 Read 会把整棵硬件树刷一遍（走驱动的 IOCTL），
        //    写成 `CpuTemp: _thermal.Read().CpuC, GpuTemp: _thermal.Read().GpuC` 就是三倍开销。
        var thermal = ReadThermal();

        // 时间戳一律用本机钟（北京时间）打，服务端只做合理区间校验 ——
        // 用服务端接收时刻代替会把"上午 9 点的卡顿"记成"下午 3 点"。
        var sample = new AgentMetricsSampleDto(
            DateTimeOffset.Now,
            reading.CpuPct,
            reading.MemUsedMb,
            reading.MemTotalMb,
            reading.DiskReadBps,
            reading.DiskWriteBps,
            reading.NetUpBps,
            reading.NetDownBps,
            reading.GpuPct,
            BasicHostInventorySource.SafeUptime(),
            thermal.CpuC,
            thermal.GpuC,
            thermal.StorageC,
            NameDevices(reading.Devices));

        lock (_gate)
        {
            _pending.AddLast(sample);
        }

        return sample;
    }

    /// <summary>
    /// 单个样本里最多带几台设备。
    /// <para>
    /// 必须封顶：一个把 64 块盘 + 若干虚拟网卡 / 虚拟显卡都算上的开发机并不罕见，
    /// 而这一帧是 5 秒一条、还要乘以机器数。超出部分**丢掉**（保留前若干个）——
    /// 曲线少几种设备远好过让整批上报被服务端拒收。
    /// </para>
    /// <para>⚠️ 这个数与服务端 <c>AgentTelemetryService</c> 里的上限是同一个语义，改动要一起改。</para>
    /// </summary>
    public const int MaxDevicesPerSample = 64;

    /// <summary>
    /// 给逐设备读数配一个<b>尽量是型号</b>的名字（2026-10-13）。
    ///
    /// <para>
    /// 平台层给的名字分两种：<b>已经是型号</b>（显卡靠 LUID 从 DXGI / D3DKMT 问到，
    /// 以及网卡的「描述（别名）」），和<b>能辨认的占位名</b>（<c>物理磁盘 0（C:）</c> /
    /// <c>GPU 适配器 1</c>）。后者的型号在硬件清单里，这一步把两者对上 ——
    /// 用的是**已经在内存里的那份清单**，不产生任何额外采集。
    /// </para>
    ///
    /// <para>
    /// 🔥 <b>不确定就不猜</b>：多路 CPU / 多块显卡时，清单里那一条无法确定对应哪一个插槽 / 适配器，
    /// 于是保持占位名。填一个"看起来像型号"的错名字，比写"GPU 适配器 1"有害得多 ——
    /// 前者会被当成事实，后者一眼就知道它只是个编号。
    /// </para>
    /// </summary>
    private IReadOnlyList<AgentDeviceReadingDto>? NameDevices(IReadOnlyList<AgentDeviceReadingDto>? devices)
    {
        if (devices is null || devices.Count == 0) return null;

        var inventory = _lastInventory;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var device in devices)
        {
            counts[device.Kind] = counts.GetValueOrDefault(device.Kind) + 1;
        }

        var result = new List<AgentDeviceReadingDto>(Math.Min(devices.Count, MaxDevicesPerSample));

        foreach (var device in devices)
        {
            if (result.Count >= MaxDevicesPerSample) break;

            result.Add(device with { Name = Clamp(BetterName(device, inventory, counts), 128) });
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>在"能唯一确定"的前提下把占位名换成型号；否则原样返回。</summary>
    internal static string BetterName(
        AgentDeviceReadingDto device,
        AgentInventoryDto? inventory,
        Dictionary<string, int> counts)
    {
        if (inventory is null) return device.Name;

        switch (device.Kind)
        {
            case AgentDeviceKinds.Disk:
            {
                var index = KeyIndex(device.Key, "disk");
                if (index is null) return device.Name;

                var model = inventory.Disks.FirstOrDefault(d => d.Index == index.Value)?.Model;

                if (string.IsNullOrWhiteSpace(model)) return device.Name;

                // 把占位名里的「物理磁盘 N」换成型号 —— 盘符之类的补充信息保留下来，
                // 得到的是 "Samsung SSD 980 PRO 1TB（C:）" 这种一眼能对上现场的名字。
                var placeholder = $"物理磁盘 {index.Value}";

                return device.Name.Contains(placeholder, StringComparison.Ordinal)
                    ? device.Name.Replace(placeholder, model, StringComparison.Ordinal)
                    : model;
            }

            case AgentDeviceKinds.Cpu:
            {
                // ⚠️ 只有一颗 CPU 时才补型号：多路机器上清单只留了一份 CPU 信息，
                //    拿它给每一颗都贴上"看起来对"的名字，是真的在猜。
                if (counts.GetValueOrDefault(AgentDeviceKinds.Cpu) != 1) return device.Name;

                var cpu = inventory.Cpu?.Name;

                return string.IsNullOrWhiteSpace(cpu) ? device.Name : cpu;
            }

            case AgentDeviceKinds.Gpu:
            {
                // 🔥 平台层现在能按 **LUID** 从 DXGI / D3DKMT 问到型号（2026-10-14）——
                //    那是**逐块适配器**准确的（"这块 LUID 就是 NVIDIA MX250"），
                //    比"清单里第一条"靠得住。已经有型号就别拿清单去覆盖它。
                if (!IsGpuPlaceholder(device.Name)) return device.Name;

                // 只有在"这块机器只报了一块显卡"时才敢拿清单里那一条来补 ——
                // 多卡时清单那一条无法确定对应哪块，拿它给每块都贴同一个名字是在猜。
                if (counts.GetValueOrDefault(AgentDeviceKinds.Gpu) != 1) return device.Name;

                var gpu = inventory.Gpus.FirstOrDefault()?.Name;

                return string.IsNullOrWhiteSpace(gpu) ? device.Name : gpu;
            }

            default:
                // 网卡不用补：平台给的名字已经是「描述（别名）」，而清单里的 Name 只是那个别名。
                return device.Name;
        }
    }

    /// <summary>
    /// 这个名字是不是平台层<b>没能问到型号</b>时留下的占位名（<c>GPU 适配器 2</c>）。
    /// <para>
    /// 用途：只有在<b>仍然是占位名</b>时才允许拿硬件清单里的型号去补 ——
    /// 已经有真型号（DXGI / D3DKMT 按 LUID 问出来的）就别覆盖，
    /// 那是逐块适配器准确的，比"清单里第一条"靠得住。
    /// </para>
    /// </summary>
    private static bool IsGpuPlaceholder(string? name)
        => string.IsNullOrWhiteSpace(name) || name.StartsWith("GPU 适配器", StringComparison.Ordinal);
    /// <summary><c>"disk3"</c> → 3；前缀对不上或后面不是数字则 null。</summary>
    private static int? KeyIndex(string key, string prefix)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return null;

        return int.TryParse(key.AsSpan(prefix.Length), out var value) ? value : null;
    }

    private static string Clamp(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        return value.Length <= max ? value : value[..max];
    }

    /// <summary>
    /// 读一次温度。<b>绝不抛</b>，且<b>失败不写日志也不改状态</b>。
    /// <para>
    /// ⚠️ 这里刻意<b>不</b>在异常时打日志：温度源在"未启用 / 驱动失败"时本来就走正常路径返回 null，
    /// 真抛出来只可能是读到一半驱动被卸掉这类瞬态问题 —— 而采样是 5 秒一次，
    /// 在这里落一行日志等于给客户机灌日志。状态变化由 <see cref="ThermalState"/> 在启动时自述一次。
    /// </para>
    /// </summary>
    private HostThermalReading ReadThermal()
    {
        try
        {
            return _thermal.Read();
        }
        catch
        {
            return HostThermalReading.Empty;
        }
    }

    /// <summary>
    /// 取一批（最多 <paramref name="max"/> 条）<b>但不移除</b>：发送成功后才
    /// <see cref="CommitSamples"/>，失败就什么都不做（下一轮再取同样的数据）。
    /// <para>
    /// 服务端按 <c>(AgentId, At)</c> 去重，所以"发出去但应答丢了"导致的重发是安全的 ——
    /// 这比"先删后发"安全得多：先删后发在应答丢失时会永久丢掉一批数据。
    /// </para>
    /// </summary>
    public IReadOnlyList<AgentMetricsSampleDto> PeekSamples(int max)
    {
        if (max <= 0) return Array.Empty<AgentMetricsSampleDto>();

        lock (_gate)
        {
            if (_pending.Count == 0) return Array.Empty<AgentMetricsSampleDto>();

            var count = Math.Min(max, _pending.Count);
            var result = new List<AgentMetricsSampleDto>(count);
            var node = _pending.First;

            while (node is not null && result.Count < count)
            {
                result.Add(node.Value);
                node = node.Next;
            }

            return result;
        }
    }

    /// <summary>确认前 <paramref name="count"/> 条已送达，从队列里摘掉。</summary>
    public void CommitSamples(int count)
    {
        if (count <= 0) return;

        lock (_gate)
        {
            for (var i = 0; i < count && _pending.First is { } first; i++)
            {
                _pending.RemoveFirst();
            }
        }
    }

    /// <summary>把队列裁剪到上限内（丢最旧的）。返回本次丢掉多少条。</summary>
    public int Trim(int maxQueued)
    {
        if (maxQueued <= 0) return 0;

        var dropped = 0;

        lock (_gate)
        {
            while (_pending.Count > maxQueued)
            {
                _pending.RemoveFirst();
                dropped++;
            }
        }

        if (dropped > 0) Interlocked.Add(ref _droppedSamples, dropped);

        return dropped;
    }

    // ───────────────────────────────────────────────────────────────────────
    // 开关机事件
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 读一批新的开关机事件。
    /// <para>
    /// <see cref="PowerEventReadResult.Watermark"/> 是"如果这批被接受，水位应当推进到哪"。
    /// <b>只有服务端应答 ok 之后才允许调用 <see cref="CommitPowerEvents"/></b> ——
    /// 提前推进水位会让这批事件永远消失，而"这台机器上个月关过几次机"是拿不到第二遍的数据。
    /// </para>
    /// <para>
    /// <b>"读到 0 条"也算一次成功的读取</b>（<see cref="PowerEventReadResult.HasEvents"/> 为 false），
    /// 调用方据此推进水位 / 打上"已回溯"标记 —— 否则一个 7 天没关过机的窗口就会
    /// 每 30 秒重扫一遍 7 天日志。
    /// </para>
    /// </summary>
    /// <returns>平台不支持或本次读取异常时返回 null。</returns>
    public PowerEventReadResult? ReadPowerEvents(DateTimeOffset now)
    {
        if (!_power.IsAvailable) return null;

        var state = _state.Snapshot();

        // 首次：回溯 N 天，把"装 Agent 之前的历史"一并捞回来。
        // 之后：只开一个小重叠窗口 —— 事件日志的写入顺序不保证与时间戳严格一致
        //       （系统在关机瞬间会补写 6006），重叠窗口用来兜住这类"迟到的旧时间戳"。
        //       重复由 (AgentId, At, EventId) 去重吸收，代价可忽略。
        var from = state.PowerEventBackfilled
            ? now - PowerEventOverlap
            : now - TimeSpan.FromDays(Math.Max(1, _options.PowerEventBackfillDays));

        HostPowerEventBatch batch;

        try
        {
            batch = _power.Read(from, state.PowerEventRecordId);
        }
        catch (Exception ex)
        {
            _log.Warn("Agent", $"开关机事件读取失败（跳过本次）：{ex.Message}");
            return null;
        }

        var events = new List<AgentPowerEventDto>(batch.Events.Count);

        foreach (var item in batch.Events)
        {
            // 白名单校验：平台上多认识了一个事件 ID，不该让整批被服务端拒收。
            if (!AgentPowerEventKinds.IsValid(item.Kind)) continue;

            events.Add(new AgentPowerEventDto(
                item.At,
                item.Kind,
                item.EventId,
                item.Actor,
                item.Detail));
        }

        return new PowerEventReadResult(new AgentPowerEventBatchDto(events), batch.MaxRecordId);
    }

    /// <summary>
    /// 服务端已接受 ⇒ 推进水位。
    /// <para>
    /// <paramref name="watermark"/> 为 null 表示这批没带回记录号（理论上不该发生）——
    /// 此时只标记"做过首次回溯"，不推进记录号，下次多读一点总比漏读好。
    /// </para>
    /// </summary>
    public void CommitPowerEvents(long? watermark, DateTimeOffset at)
    {
        _state.Update(state =>
        {
            state.PowerEventBackfilled = true;
            state.PowerEventCheckedAt = at;

            if (watermark is { } recordId
                && (state.PowerEventRecordId is null || recordId > state.PowerEventRecordId))
            {
                state.PowerEventRecordId = recordId;
            }
        });
    }

    // ───────────────────────────────────────────────────────────────────────
    // 硬件清单
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>上次成功上报清单的时刻（null = 从没报过）。</summary>
    public DateTimeOffset? LastInventoryAt => _state.Snapshot().InventoryReportedAt;

    /// <summary>
    /// 采一份硬件清单。
    /// <para><b>绝不抛</b>；失败返回 null（下一轮再试，不影响心跳与指标）。</para>
    /// </summary>
    public AgentInventoryDto? CollectInventory()
    {
        try
        {
            var inventory = _inventory.Collect();

            // 客户端自己声明两件"服务端猜不出来的事"：
            //   ① 我这边支不支持开关机记录（于是页面能显示"当前平台不支持"，
            //      而不是一张让人以为"从没关过机"的空表）；
            //   ② 温度采集是什么状态（未启用 / 驱动失败 / 正常）—— 见 AgentThermalStates。
            // ⚠️ 清单是 6 小时一次、一机一行，正好是放这种"机器级恒定量"的位置。
            var enriched = inventory with
            {
                PowerEventsSupported = PowerEventsSupported,
                ThermalState = this.ThermalState,
                ThermalNote = this.ThermalNote,
            };

            // 缓存一份给逐设备命名用（见 NameDevices）。
            // 这里**不做深拷贝**：清单是不可变记录，谁都不会去改它。
            _lastInventory = enriched;

            return enriched;
        }
        catch (Exception ex)
        {
            _log.Warn("Agent", $"硬件清单采集失败（跳过本次）：{ex.Message}");
            return null;
        }
    }

    /// <summary>服务端已接受 ⇒ 记下上报时刻（下次要等满一个间隔才会再报）。</summary>
    public void CommitInventory(DateTimeOffset at)
        => _state.Update(state => state.InventoryReportedAt = at);
}

/// <summary>
/// 一次开关机事件读取的结果（M4.2）。
/// <para>
/// 用"结果对象"而不是"空列表 = 没有新事件 / null = 读取失败"的二值返回：
/// 那两种情况的<b>处置完全不同</b>（前者要推进水位打上回溯标记，后者要原地不动等下次），
/// 用 null 兼表两义迟早会被某一处调用方写错。
/// </para>
/// </summary>
/// <param name="Batch">这批事件（可能为空列表）。</param>
/// <param name="Watermark">本批见到的最大记录号；服务端接受后据此推进水位。</param>
public sealed record PowerEventReadResult(AgentPowerEventBatchDto Batch, long? Watermark)
{
    /// <summary>本批是否真的带了事件。</summary>
    public bool HasEvents => Batch.Events.Count > 0;
}
