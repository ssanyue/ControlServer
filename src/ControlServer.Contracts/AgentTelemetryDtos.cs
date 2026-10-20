namespace ControlServer.Contracts;

// ═══════════════════════════════════════════════════════════════════════════
// M4.2 · 遥测（指标 / 开关机记录 / 硬件清单）
//
// 三条贯穿全篇的约定，改动前先想清楚：
//
// ① **未知一律 null，绝不用 0 代替**。
//    「CPU 0%」和「CPU 查不到」在页面上是「一切正常」与「采集坏了」两件完全相反的事。
//    0 会被当真、会被画进曲线、会被拿去算平均值 —— 一个假 0 能污染整天的报表。
//
// ② **时间戳一律 DateTimeOffset（北京时间）**，由客户端用本机钟打，服务端只做
//    "合理区间"校验（见 AgentTelemetryService）。用服务器接收时刻代替是不行的 ——
//    那会把"机器上午 9 点关机"记成"下午 3 点才收到关机事件"。
//
// ③ **批量、单向、客户端推**。客户端**不开任何入站端口**（M4.1 的承诺；
//    M4.5 起唯一的例外是「UDP 指令通道」—— 它收的是"关机 / 音量"这类操作指令，
//    与遥测上报无关。⚠️ 该通道自 2026-10-13 起是**明文 + 来源 IP 黑名单**，
//    不再有签名，所以它只能待在可信网络里），
//    服务端从不主动向客户机要数据。
// ═══════════════════════════════════════════════════════════════════════════

// ═══════════════════════════════════════════════════════════════════════════
// 温度 / 风扇 / 电压的状态（M4.3）
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 硬件温度监控的状态（M4.3）。
/// <para>
/// <b>为什么温度要单独有一个"状态"而不是"null 就是没有"</b>：
/// 温度取不到有三种完全不同的原因，页面上该说的话也完全不同 ——
/// </para>
/// <list type="number">
///   <item><b>未启用</b>（<see cref="Disabled"/>）：装机时没勾那个选项。这是<b>正常且预期</b>的状态，
///         页面该说"未启用（怎么开）"，而不是留一片空白让人以为采集坏了；</item>
///   <item><b>驱动加载失败</b>（<see cref="Failed"/>）：勾了但内核驱动没装上（被安全软件拦 / 非管理员 /
///         系统不支持）。页面该把<b>原因</b>摆出来，否则运维只能猜；</item>
///   <item><b>可用</b>（<see cref="Ok"/>）：驱动正常，某个温度仍可能是 null
///         （该硬件没有这个传感器），此时页面显示 <c>—</c> 即可。</item>
/// </list>
/// <para>
/// 🔥 <b>无论哪种情况，都绝不用 0 冒充。</b>显示 <c>0°C</c> 会被读成"机器很凉快"，
/// 比显示"未启用"危险得多（与全项目的 null 纪律一致）。
/// </para>
/// <para><b>只用字符串常量不用 enum</b> —— 理由同 <see cref="AgentPowerEventKinds"/>（SignalR 会把 enum 序列化成数字）。</para>
/// </summary>
public static class AgentThermalStates
{
    /// <summary>
    /// 已启用，且<b>拿到了至少一项</b>温度读数。
    /// <para>
    /// ⚠️ 注意这里判的是"<b>有没有拿到读数</b>"，不是"驱动有没有装上" ——
    /// 显卡温度（NVIDIA / AMD）走的是<b>用户态</b> NVAPI / ADL，不需要内核驱动；
    /// 于是完全可能出现在"驱动没装上（甚至没尝试）"的机器上，状态依然是 <c>ok</c>、
    /// GPU 有度数，而 CPU / 硬盘因为拿不到而为 null（页面显示 <c>—</c>）。
    /// <b>这是有意为之</b>：如实呈现"拿到了什么"，比按来源猜一个总结论更有用。
    /// 反过来，一项都拿不到才算 <see cref="Failed"/>。
    /// </para>
    /// </summary>
    public const string Ok = "ok";

    /// <summary>未启用温度监控（安装时未勾选，或配置里关掉）。<b>这是默认值，也是正常状态</b>。</summary>
    public const string Disabled = "disabled";

    /// <summary>已启用，但驱动加载失败（被拦截 / 无权限 / 系统不支持）。具体原因见 <c>ThermalNote</c>。</summary>
    public const string Failed = "failed";

    /// <summary>全部状态（顺序即前端展示顺序）。</summary>
    public static readonly IReadOnlyList<string> All = new[] { Ok, Disabled, Failed };

    /// <summary>中文显示名（仅展示用，不参与比较）。</summary>
    public static string DisplayName(string? state) => state switch
    {
        Ok => "已启用",
        Disabled => "未启用",
        Failed => "驱动加载失败",
        _ => "未知",
    };

    /// <summary>是否需要给运维提个醒（页面上用黄/红标出来）。</summary>
    public static bool NeedsAttention(string? state) => state is Failed;

    /// <summary>是否为合法状态（服务端摄入时的白名单校验）。</summary>
    public static bool IsValid(string? state) => state is not null && All.Contains(state);
}

// ═══════════════════════════════════════════════════════════════════════════
// 逐设备指标（2026-10-13）
//
// **为什么要做**：此前所有指标都是**整机口径**（磁盘 = 所有物理盘合计、网络 = 所有网卡合计、
// GPU = 所有引擎求和）。一台装了系统盘 + 素材盘的机器，"磁盘一直在读"这件事
// 在整机曲线上看不出是哪块盘 —— 而运维真正要问的恰恰是"哪块盘在拖"。
//
// **口径纪律**：逐设备读数与整机读数是**两条独立采集路径**，不做"设备之和 = 整机值"的假设。
// 它们是不同计数器（`_Total` 实例 vs 各实例），在同一毫秒上取到，求和未必严格相等。
// 因此这里**不提供任何"用逐设备还原整机"的推导**，两边各按各的显示。
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 逐设备指标的设备种类。
/// <para><b>这些名字会落库</b>（<c>agent_metrics.DevicesJson</c>），改名等于要写数据迁移。</para>
/// </summary>
public static class AgentDeviceKinds
{
    /// <summary>一颗<b>物理 CPU</b>（多路机器上是"插槽"，命名实例形如 <c>0,1</c>）。</summary>
    public const string Cpu = "cpu";

    /// <summary>一块<b>物理磁盘</b>（不含分区；PDH 的 <c>PhysicalDisk</c> 实例）。</summary>
    public const string Disk = "disk";

    /// <summary>一块<b>网卡</b>（已排除回环与隧道适配器）。</summary>
    public const string Net = "net";

    /// <summary>一块<b>显卡</b>（按适配器 LUID 区分）。</summary>
    public const string Gpu = "gpu";

    /// <summary>全部种类（顺序即前端展示顺序）。</summary>
    public static readonly IReadOnlyList<string> All = new[] { Cpu, Disk, Net, Gpu };

    /// <summary>中文名（仅展示用）。</summary>
    public static string DisplayName(string? kind) => kind switch
    {
        Cpu => "处理器",
        Disk => "磁盘",
        Net => "网卡",
        Gpu => "显卡",
        _ => kind ?? string.Empty,
    };

    /// <summary>是否为合法种类（服务端摄入时的白名单校验）。</summary>
    public static bool IsValid(string? kind) => kind is not null && All.Contains(kind);
}

/// <summary>
/// <b>一块硬件在某一刻的读数</b>。
///
/// <para>
/// ⚠️ <b>为什么把四种设备的字段塞进一个类型（而不是四种 DTO）</b>：
/// 它们要走同一条"采集 → 上报 → 落库 → 聚合 → 画曲线"的管子，
/// 拆成四种就是四套序列化、四处白名单校验、四个存储列。而每种设备<b>真正会填的字段只有一两个</b>
/// （磁盘只填读写、网卡只填上下行），其余保持 null —— 与全项目"取不到就是 null"的纪律一致。
/// </para>
///
/// <para><b>按 <see cref="Kind"/> 该看哪两个字段</b>（前端与聚合都按这张表办事）：</para>
/// <list type="bullet">
///   <item><see cref="AgentDeviceKinds.Cpu"/> → <see cref="CpuPct"/></item>
///   <item><see cref="AgentDeviceKinds.Disk"/> → <see cref="ReadBps"/> / <see cref="WriteBps"/></item>
///   <item><see cref="AgentDeviceKinds.Net"/> → <see cref="UpBps"/> / <see cref="DownBps"/></item>
///   <item><see cref="AgentDeviceKinds.Gpu"/> → <see cref="GpuPct"/></item>
/// </list>
/// </summary>
/// <param name="Kind">见 <see cref="AgentDeviceKinds"/>。</param>
/// <param name="Key">
/// <b>稳定标识</b> —— 曲线历史靠它接续。同一台机器上同一块硬件，重启 / 重新采集后必须得到同一个 Key。
/// <list type="bullet">
///   <item>CPU：<c>cpu0</c> / <c>cpu1</c>（插槽序号）；</item>
///   <item>磁盘：<c>disk0</c> / <c>disk1</c>（PDH 实例前缀的物理盘号）；</item>
///   <item>网卡：适配器 <c>Id</c>（GUID，重命名网卡也不会变）；</item>
///   <item>显卡：<c>luid_0x........_0x........</c>（Windows 只按 LUID 区分适配器）。</item>
/// </list>
/// <para>⚠️ <b>不要用显示名当 Key</b>：改名（网卡重命名 / 磁盘型号字符串微调）会让曲线断成两截。</para>
/// </param>
/// <param name="Name">
/// 展示名（型号优先）。由<b>客户机</b>尽量填成"型号"（如 <c>Samsung SSD 980 PRO 1TB</c>），
/// 填不出来时退回一个能辨认的名字（如 <c>物理磁盘 0</c>）—— 但**绝不编造型号**。
/// </param>
/// <param name="CpuPct">该 CPU 的占用率（0–100）。</param>
/// <param name="ReadBps">该磁盘读速率（字节/秒）。</param>
/// <param name="WriteBps">该磁盘写速率（字节/秒）。</param>
/// <param name="UpBps">该网卡上行速率（字节/秒）。</param>
/// <param name="DownBps">该网卡下行速率（字节/秒）。</param>
/// <param name="GpuPct">该显卡占用率（0–100）。</param>
public sealed record AgentDeviceReadingDto(
    string Kind,
    string Key,
    string Name,
    double? CpuPct = null,
    double? ReadBps = null,
    double? WriteBps = null,
    double? UpBps = null,
    double? DownBps = null,
    double? GpuPct = null);

/// <summary>
/// 一个采样瞬间的实时指标（M4.2）。
/// <para>
/// <b>刻意做"窄"</b>：这是 5 秒一条的高频数据，加一个字段就是给每台机器每天
/// 多写 17 万次。硬件型号/序列号那种"一辈子不变"的东西放
/// <see cref="AgentInventoryDto"/>，不在这里。
/// </para>
/// <para>
/// 按 <c>300</c> 台 × 5 秒算，一条样本 JSON 约 400 字节 ⇒ 单条记录写入量极小，
/// 但 30 天明细仍有 GB 级，因此服务端<b>必须</b>配套降采样与保留策略（见 §6.4）。
/// </para>
/// </summary>
/// <param name="At">采样时刻（客户端本机钟，北京时间）。</param>
/// <param name="CpuPct">整机 CPU 占用率（0–100）；采不到为 null。</param>
/// <param name="MemUsedMb">已用物理内存（MB）。</param>
/// <param name="MemTotalMb">物理内存总量（MB）。</param>
/// <param name="DiskReadBps">所有物理磁盘合计读速率（字节/秒）。<b>需要两次采样求差</b>，首次为 null。</param>
/// <param name="DiskWriteBps">合计写速率（字节/秒）。</param>
/// <param name="NetUpBps">所有网卡合计上行速率（字节/秒）。</param>
/// <param name="NetDownBps">合计下行速率（字节/秒）。</param>
/// <param name="GpuPct">GPU 整体使用率（0–100）；集成显卡/无驱动时常为 null。</param>
/// <param name="UptimeSeconds">系统已运行秒数（用于画出"这几台是不是刚重启过"）。</param>
/// <param name="CpuTempC">
/// CPU 温度（摄氏度，M4.3）；<b>未启用温度监控 / 驱动加载失败时为 null</b>。
/// <para>
/// ⚠️ 这三个温度字段<b>刻意排在最后且带默认值</b>：它们本来就是"有就带、没有就没有"的可选量，
/// 让它们可选能让既有调用点（尤其是几十处测试夹具）一个都不用改。
/// </para>
/// </param>
/// <param name="GpuTempC">GPU 温度（摄氏度）；取不到为 null。</param>
/// <param name="StorageTempC">物理磁盘温度（摄氏度，多块取最高）；取不到为 null。</param>
/// <param name="Devices">
/// <b>逐设备读数</b>（2026-10-13）。空列表 = 这台客户机不上报逐设备明细（老客户端）；
/// null 表示"这一帧没带" —— 两者都合法，页面按"该时段没有逐设备数据"处理。
/// <para>
/// ⚠️ <b>采样节奏与整机指标完全一致（5 秒）</b>，不做"逐设备慢一点"的特殊化：
/// 两个节奏会让"整机在飙、逐设备却是 1 分钟前的"变成一种常态，
/// 而用户切换设备时看到的第一件事就是这两条曲线对不上。
/// </para>
/// </param>
public sealed record AgentMetricsSampleDto(
    DateTimeOffset At,
    double? CpuPct,
    long? MemUsedMb,
    long? MemTotalMb,
    double? DiskReadBps,
    double? DiskWriteBps,
    double? NetUpBps,
    double? NetDownBps,
    double? GpuPct,
    long? UptimeSeconds,
    double? CpuTempC = null,
    double? GpuTempC = null,
    double? StorageTempC = null,
    IReadOnlyList<AgentDeviceReadingDto>? Devices = null);

/// <summary>
/// 一批指标样本（客户端定时打包上报）。
/// <para>
/// <b>为什么要批量</b>：5 秒一条 × 300 台 = 60 次 Hub 调用/秒。不批量的话，
/// 光是 SignalR 的调用信封开销就比数据本身大一个数量级。客户端断线重连期间
/// 采到的样本也会攒进下一批（服务端按 <see cref="AgentMetricsSampleDto.At"/> 去重）。
/// </para>
/// <para>服务端对批次大小有上限（<c>Server:Agent:Telemetry:MaxBatchSamples</c>），
/// 超限整批拒收 —— 不截断，静默丢一半比明确拒绝更难查。</para>
/// </summary>
public sealed record AgentMetricsBatchDto(IReadOnlyList<AgentMetricsSampleDto> Samples);

/// <summary>
/// 一台机器"最新一帧"的指标摘要（列表页与详情页顶部用）。
/// <para>与 <see cref="AgentMetricsSampleDto"/> 的区别：这是<b>算过的</b>结果
/// （内存占用率由 used/total 算出），前端不必各处重复算一遍。</para>
/// </summary>
/// <param name="Devices">
/// 这一帧的<b>逐设备读数</b>（2026-10-13 补）。
/// <para>
/// 🔥 <b>为什么摘要里必须有逐设备读数，而不是"详情页另外去拉"</b>：
/// <see cref="AgentMetricsSampleDto.Devices"/> 的注释里已经定下一条规则 ——
/// 逐设备与整机指标<b>同一个采样节奏</b>，不许让两者漂移。而详情页的曲线是这样长出来的：
/// 打开时整段拉一次历史，之后每来一帧摘要就"往前挪一格"。摘要不带逐设备的话，
/// 挪的只有整机那几条线，<b>而硬件选择器上的"当前值"和硬件曲线会永远停在"打开页面那一刻"</b> ——
/// 用户切到某块硬盘时看到的正好是"整机在飙、这块盘却一动不动"。
/// </para>
/// <para>
/// <b>代价与取舍</b>：摘要会被塞进在线表列表（每台约 +1 KB）并随
/// <c>AgentTelemetryUpdated</c> 广播（300 台 × 每 2 秒 ⇒ 约 0.2 MB/s，内网可忽略）。
/// 另一条路是"详情页每 N 秒重拉一次整个窗口"，被否掉：那是一次真的扫库查询
/// （窗口最长 30 天），而这份数据客户端<b>本来就采到了</b>，只是原来没推。
/// </para>
/// </param>
public sealed record AgentMetricsSummaryDto(
    DateTimeOffset? At,
    double? CpuPct,
    double? MemPct,
    long? MemUsedMb,
    long? MemTotalMb,
    double? DiskReadBps,
    double? DiskWriteBps,
    double? NetUpBps,
    double? NetDownBps,
    double? GpuPct,
    long? UptimeSeconds,
    double? CpuTempC = null,
    double? GpuTempC = null,
    double? StorageTempC = null,
    IReadOnlyList<AgentDeviceReadingDto>? Devices = null);

/// <summary>
/// 一条"这台机器此刻怎么样"的实时推送（M4.2，<c>AgentTelemetryUpdated</c> 的载荷）。
/// <para>
/// <b>只带最新一帧，不带历史</b>：历史曲线由前端按需拉一次
/// <c>GET /api/agents/{id}/metrics</c>，之后靠本事件"往前挪一格"。
/// 把历史塞进推送会让每条消息涨到几十 KB，而 300 台时那是每秒几 MB 的广播。
/// </para>
/// </summary>
/// <param name="AgentId">哪台机器。</param>
/// <param name="Metrics">最新一帧摘要。</param>
/// <param name="At">服务端广播这一刻（前端据此判断"这帧是不是新的"，防止乱序覆盖）。</param>
public sealed record AgentLiveMetricsDto(
    int AgentId,
    AgentMetricsSummaryDto? Metrics,
    DateTimeOffset At);

/// <summary>时序图上的一个点（服务端可能已降采样；<paramref name="BucketSeconds"/> &gt; 0 表示是桶均值）。</summary>
/// <param name="Devices">
/// 该时刻的逐设备读数（2026-10-13）。
/// <para>
/// ⚠️ <b>只有明细层（<c>raw</c>）带它</b>：日聚合（<c>daily</c>）不聚合逐设备读数 ——
/// 见 <c>DevicesJson</c> 的落地说明。因此查 90 天时这里会是 null，
/// 前端要能说清"该时段只有整机聚合，逐设备曲线不可用"，而不是画一条空线。
/// </para>
/// </param>
/// <param name="IsGap">
/// <b>这个点不是一次采样，而是一个「这里没有数据」的断点标记</b>（2026-10-16 补）。
/// <para>
/// 它只出现在<b>两段真实数据之间</b>，且只在中间空得足够久时才插（判据见
/// <c>AgentMetricsAggregator.GapThreshold</c>）：<see cref="At"/> = 空白开始的那个桶边界，
/// <b>所有指标一律为 null</b>。
/// </para>
/// <para>
/// 🔥 <b>为什么要有它，而不是"让前端自己看时间差"</b>：空桶在聚合时就被丢掉了
/// （见 <c>Bucketize</c>），于是"客户端离线两小时"在数据里表现为
/// <b>前后两个点相隔两小时</b> —— 而折线会把这<b>两点直接连起来</b>，
/// 那是<b>凭空编了一段数据</b>：用户看到的是一条"这段时间一直很稳"的直线。
/// 插一个全 null 的标记点之后，折线在它那里<b>自然断开</b>，
/// 页面不需要再各自去猜"多大的时间差算离线"（那会变成第二处判据，且与在线表的灰点对不上）。
/// </para>
/// <para>
/// ⚠️ 页面上因此有两条**必须同时成立**的说法：曲线在空白处断开、且那段空白不能是"沉默的"
/// —— 悬停要说明"无数据"，图下要说明"空白＝当时没有采样（通常是客户端离线）"。
/// 只断开不说明，用户会以为图表坏了。
/// </para>
/// </param>
public sealed record AgentMetricsPointDto(
    DateTimeOffset At,
    double? CpuPct,
    double? MemPct,
    long? MemUsedMb,
    long? MemTotalMb,
    double? DiskReadBps,
    double? DiskWriteBps,
    double? NetUpBps,
    double? NetDownBps,
    double? GpuPct,
    long? UptimeSeconds,
    double? CpuTempC = null,
    double? GpuTempC = null,
    double? StorageTempC = null,
    IReadOnlyList<AgentDeviceReadingDto>? Devices = null,
    bool IsGap = false);

/// <summary>
/// 一段时序查询的结果。
/// </summary>
/// <param name="Source">
/// 数据来源：<c>raw</c>（5 秒明细）/ <c>hourly</c>（小时聚合）/ <c>daily</c>（日聚合）。
/// <para>
/// <b>必须告诉前端它看的是哪一层</b>：查 90 天时服务端只可能给日聚合，
/// 而页面上两条相邻折线之间可能隔了一整天 —— 用户以为"CPU 一直 40% 很稳"，
/// 其实那是 90 个"当天均值"连起来的假平滑。页面上明确标注"按天聚合"才诚实。
/// </para>
/// </param>
/// <param name="RawCount">聚合前命中的原始样本行数（前端据此显示"由 N 个采样点聚合"）。</param>
/// <param name="GapThresholdSeconds">
/// 「多久没有采样就算客户端离线（曲线留空）」的判据（秒，2026-10-16 补）。
/// 取值 = <c>max(离线判定阈值, 2 × 分桶粒度)</c>，由服务端算好下发。
/// <para>
/// 🔥 <b>为什么要下发而不是让前端自己定</b>：详情页的曲线是"打开时拉一段历史，
/// 之后每来一帧就往右挪一格"（见 <c>AgentLiveMetricsDto</c>）。挪格那一步同样会遇到
/// "上一格是十分钟前"的情形 —— 不去判它，被控端离线十分钟后回来，
/// 曲线会被<b>一条直线接过去</b>，直到用户手动点一次「刷新」才恢复成空白。
/// 两处判据必须同源，否则"刷新一下就好了"会变成一种随机行为。
/// </para>
/// <para>0 = 未判（服务端没算 / 老服务端），前端按"不插断点"处理。</para>
/// </param>
public sealed record AgentMetricsSeriesDto(
    int AgentId,
    DateTimeOffset From,
    DateTimeOffset To,
    int BucketSeconds,
    string Source,
    int RawCount,
    IReadOnlyList<AgentMetricsPointDto> Points,
    int GapThresholdSeconds = 0);

// ═══════════════════════════════════════════════════════════════════════════
// 开关机 / 异常记录（用户明确要求的第 3 条）
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 开关机 / 异常事件种类（M4.2）。
/// <para><b>只用字符串常量，不用 enum</b> —— 理由与 <see cref="AgentStates"/> 完全一致（SignalR 会把 enum 序列化成数字）。</para>
/// <para>这些名字<b>会落库</b>（<c>agent_power_events.kind</c>），改名等于要写数据迁移。</para>
/// </summary>
public static class AgentPowerEventKinds
{
    /// <summary>开机（System 日志 6005：事件日志服务已启动）。</summary>
    public const string Boot = "boot";

    /// <summary>正常关机（System 日志 6006）。</summary>
    public const string Shutdown = "shutdown";

    /// <summary>由用户 / 进程发起的重启或关机（System 日志 1074，含发起者）。</summary>
    public const string Restart = "restart";

    /// <summary>
    /// 意外断电 / 非正常关机（System 日志 6008）。
    /// <para>
    /// ⚠️ <b>时间戳的口径要知道</b>：6008（以及 41）是<b>下次开机时</b>才写进日志的，
    /// 所以 <c>At</c> 是"发现上次没干净关机"的那一刻（≈ 本次开机时刻），
    /// <b>不是</b>真正的断电时刻。真正的时刻只写在事件的消息正文里。
    /// </para>
    /// <para>
    /// 这里刻意<b>不去解析消息正文</b>把它抠出来：那段文本随系统语言变化（中文 / 英文 / 其它），
    /// 解析失败就会得到一个看着很合理的错时间 —— 本项目的一贯取舍是"取不到就承认取不到"。
    /// 因此页面上把这个数字标成"发现异常的次数"，而不是"断电时刻"。
    /// </para>
    /// <para>
    /// ⚠️ <b>"不解析" ≠ "不显示"</b>（两件事别混为一谈）：<see cref="AgentPowerEventDto.Detail"/>
    /// 就是那段正文的<b>原文</b>，只是原样搬出来给人工核对，没有被解析成任何字段。
    /// 想找真正的断电时刻，读 Detail 即可。
    /// </para>
    /// </summary>
    public const string Unexpected = "unexpected";

    /// <summary>蓝屏（System 日志 1001，BugCheck）。</summary>
    public const string Bsod = "bsod";

    /// <summary>进入睡眠（System 日志 42）。</summary>
    public const string Sleep = "sleep";

    /// <summary>从睡眠恢复（System 日志 107）。</summary>
    public const string Wake = "wake";

    /// <summary>全部种类（顺序即前端筛选下拉框顺序）。</summary>
    public static readonly IReadOnlyList<string> All =
        new[] { Boot, Shutdown, Restart, Unexpected, Bsod, Sleep, Wake };

    /// <summary>中文显示名（仅展示用，不参与比较）。</summary>
    public static string DisplayName(string? kind) => kind switch
    {
        Boot => "开机",
        Shutdown => "正常关机",
        Restart => "计划重启/关机",
        Unexpected => "意外关机",
        Bsod => "蓝屏",
        Sleep => "进入睡眠",
        Wake => "睡眠唤醒",
        _ => kind ?? string.Empty,
    };

    /// <summary>是否属于"坏事"（页面上用红/黄标出来）。</summary>
    public static bool IsProblem(string? kind) => kind is Unexpected or Bsod;

    /// <summary>是否为合法种类（服务端摄入时的白名单校验）。</summary>
    public static bool IsValid(string? kind) => kind is not null && All.Contains(kind);
}

/// <summary>
/// 一条开关机 / 异常记录。
/// </summary>
/// <param name="At">事件时刻（来自事件日志，客户端本机钟）。</param>
/// <param name="Kind">见 <see cref="AgentPowerEventKinds"/>。</param>
/// <param name="EventId">原始 Windows 事件 ID（6005 / 6006 / 6008 / 41 / 1074 / 1001 / 42 / 107）。保留它是为了"能自己去事件查看器对一遍"。</param>
/// <param name="Actor">
/// 发起者（仅 1074 有）：形如 <c>DOMAIN\user</c> 或进程名。
/// <para>⚠️ 事件日志的属性顺序<b>不是契约</b>，取不到就是 null —— 宁可空着，也不猜一个错的人名。</para>
/// </param>
/// <param name="Detail">
/// 事件正文原文（与事件查看器里那条消息逐字一致），上限 400 字；取不到时退回事件的插入字符串。
/// <para>
/// ⚠️ <b>只收文本型属性</b>：<c>byte[]</c> 会被渲染成字面量 <c>"System.Byte[]"</c>，
/// 而 Kernel-Power 41 的载荷是一串没有 schema 的裸数字 —— 两者都不是"详情"，
/// 只会让这一列（唯一用途是与事件查看器逐条核对）失效。这类值一律丢弃，宁可留空。
/// </para>
/// </param>
public sealed record AgentPowerEventDto(
    DateTimeOffset At,
    string Kind,
    int? EventId,
    string? Actor,
    string? Detail);

/// <summary>一批开关机事件（客户端回溯补扫或增量上报）。</summary>
public sealed record AgentPowerEventBatchDto(IReadOnlyList<AgentPowerEventDto> Events);

/// <summary>
/// 开关机记录的统计摘要（服务端算，客户端只上报原始事件）。
/// <para>
/// <b>为什么统计放在服务端</b>：口径会变（"意外关机率"要不要把睡眠算进去？）。
/// 放服务端就能改口径而不动客户端 —— 否则每次调整都要重装所有客户机。
/// </para>
/// </summary>
public sealed record AgentPowerStatsDto(
    int BootCount,
    int ShutdownCount,
    int RestartCount,
    int UnexpectedCount,
    int BsodCount,
    double? AverageUptimeSeconds,
    DateTimeOffset? LastBootAt,
    DateTimeOffset? LastShutdownAt);

/// <summary>一台机器的开关机记录列表 + 统计。</summary>
public sealed record AgentPowerEventListDto(
    int AgentId,
    IReadOnlyList<AgentPowerEventDto> Items,
    int Total,
    AgentPowerStatsDto Stats);

// ═══════════════════════════════════════════════════════════════════════════
// 硬件清单（变化极少，启动时 + 每 6 小时一次）
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>CPU 明细。</summary>
public sealed record AgentCpuInfoDto(
    string? Name,
    int? PhysicalCores,
    int? LogicalProcessors,
    int? BaseClockMhz,
    int? MaxClockMhz,
    string? Socket);

/// <summary>一根内存条（<c>Win32_PhysicalMemory</c>）。</summary>
public sealed record AgentMemoryModuleDto(
    string? Bank,
    string? Manufacturer,
    long? CapacityMb,
    int? SpeedMhz,
    string? PartNumber);

/// <summary>一个分区 / 卷。</summary>
public sealed record AgentVolumeDto(
    string Drive,
    string? Label,
    string? FileSystem,
    long? TotalMb,
    long? FreeMb);

/// <summary>一块物理磁盘。</summary>
public sealed record AgentDiskDto(
    int? Index,
    string? Model,
    string? Serial,
    long? SizeGb,
    string? MediaType);

/// <summary>
/// 一块显卡。
/// <para>
/// 🔥 <b>2026-10-13 起补了三项"更具体"的型号信息</b>（<see cref="Vendor"/> /
/// <see cref="VideoProcessor"/> / <see cref="PciId"/>）：现场报修时只看
/// <see cref="Name"/> 常常分不清是哪一块 —— "NVIDIA GeForce GTX 1060 6GB" 这样的名字
/// 在多卡机器上会重复两遍，而同型号不同批次（不同 VEN/DEV）驱动行为并不一样。
/// </para>
/// <para>
/// ⚠️ 三项都<b>刻意排在最后且带默认值</b>：老客户端不报它们（→ null），
/// 既有调用点（含十几处测试夹具）一个都不用改。
/// </para>
/// </summary>
/// <param name="Name">WMI 上报的适配器名（如 <c>NVIDIA GeForce RTX 3060</c>）。</param>
/// <param name="DriverVersion">驱动版本号。</param>
/// <param name="DriverDate">驱动日期。</param>
/// <param name="MemoryMb">显存（MB）。<b>4 GB 以上取不到</b>（WMI 的 <c>AdapterRAM</c> 是 UInt32，会回绕）。</param>
/// <param name="Vendor">厂商名（<c>AdapterCompatibility</c>，如 <c>NVIDIA</c> / <c>Intel Corporation</c>）。</param>
/// <param name="VideoProcessor">GPU 芯片名（<c>VideoProcessor</c>，常比 <see cref="Name"/> 更短更准）。</param>
/// <param name="PciId">
/// PCI 厂商 / 设备号，形如 <c>10DE:2504</c>（从 <c>PNPDeviceID</c> 里的
/// <c>VEN_xxxx&amp;DEV_xxxx</c> 解出来）。<b>多卡同型号时靠它区分</b>。
/// </param>
public sealed record AgentGpuDto(
    string? Name,
    string? DriverVersion,
    DateTimeOffset? DriverDate,
    long? MemoryMb,
    string? Vendor = null,
    string? VideoProcessor = null,
    string? PciId = null);

/// <summary>一台显示器（远控前置信息）。</summary>
public sealed record AgentDisplayDto(
    string? DeviceName,
    int? Width,
    int? Height,
    int? RefreshHz,
    bool Primary);

/// <summary>
/// 完整硬件清单（M4.2）。
/// <para>
/// 与 <see cref="AgentHostInfoDto"/> 的关系：后者是<b>注册时的最小摘要</b>（机器码 + 主机名 + 几项），
/// 必须永远保持轻量（它跟着每一次注册请求走）；本类型是<b>可选的详细清单</b>，
/// 单独一次调用上报，且失败/超时绝不影响注册与心跳。
/// </para>
/// </summary>
public sealed record AgentInventoryDto(
    DateTimeOffset CapturedAt,
    AgentCpuInfoDto? Cpu,
    long? TotalMemoryMb,
    IReadOnlyList<AgentMemoryModuleDto> MemoryModules,
    IReadOnlyList<AgentVolumeDto> Volumes,
    IReadOnlyList<AgentDiskDto> Disks,
    IReadOnlyList<AgentGpuDto> Gpus,
    IReadOnlyList<AgentDisplayDto> Displays,
    string? OsVersion,
    string? OsBuild,
    string? OsArchitecture,
    string? Domain,
    DateTimeOffset? BootTime,
    long? UptimeSeconds,
    string? LoggedOnUser,
    /// <summary>
    /// 该客户端所在平台是否支持开关机记录（M4.2）。
    /// <para>
    /// 由客户端自己上报，服务端<b>不猜</b>：Windows 客户端能读事件日志，Linux 客户端还没实现。
    /// 页面上因此能显示"当前平台不支持"而不是一个让人以为"这台机器从没关过机"的空表。
    /// </para>
    /// </summary>
    bool PowerEventsSupported = true,
    /// <summary>
    /// 硬件温度监控的状态（M4.3），见 <see cref="AgentThermalStates"/>。
    /// <para>
    /// <b>放在清单里而不是每个采样点里</b>：这个状态是<b>机器级</b>的（驱动在进程启动时加载一次，
    /// 之后不再变化），而清单"一机一行、6 小时一次"。把它塞进 5 秒一条的指标里，
    /// 等于用 300 台 × 17280 条/天 的代价去重复一个恒定值。
    /// </para>
    /// <para>null = 该客户端版本还没上报过这个字段（老客户端），页面按"未知"处理。</para>
    /// </summary>
    string? ThermalState = null,
    /// <summary>
    /// 温度不可用时的人话补充（可选）。
    /// <para>
    /// <see cref="AgentThermalStates.Failed"/> 时是<b>失败原因</b>
    /// （例如"内核驱动加载被拒绝（需要管理员权限）" / "安全软件拦截了驱动释放"）；
    /// <see cref="AgentThermalStates.Disabled"/> 时是对"为什么没启用"的说明，
    /// 且必须**具体到该去哪儿解决** —— 例如
    /// "配置里没有 <c>Agent:Telemetry:ThermalEnabled</c> 这一项（绿色版 / 手工启动的客户端
    /// 不会自动开启）" 与 "安装时未勾选" 是**两条不同的排查路径**，不能含糊成同一句
    /// （"当前平台未实现温度采集" 是 <c>NoopThermalSource</c> 那一头）。
    /// </para>
    /// <para>页面把它原样显示出来 —— 否则运维只能对着"驱动加载失败"六个字猜。</para>
    /// <para>
    /// 🔥 <b>2026-10-19 起 <see cref="AgentThermalStates.Ok"/> 时也可能是非 null</b>：
    /// 温度整体可用、但 <b>CPU 那一项</b>没读到（AMD 机器上很常见）时，这里是
    /// "枚举到哪颗 CPU / 它有没有温度传感器 / 读数是否无效"的诊断。
    /// 这三种原因对应三种完全不同的动作（查驱动 / 换驱动库 / 机器本来就没有），
    /// 所以不能一句"没读到"了事 —— 页面上那个孤零零的 <c>CPU —</c> 必须有人解释。
    /// </para>
    /// </summary>
    string? ThermalNote = null,
    /// <summary>
    /// 本机允许被<b>下发写入</b>的目录白名单（M4.4，<c>Agent:Transfer:InboxDirectories</c>）。
    /// <para>
    /// <b>为什么由客户端上报、而不由服务端下发</b>：服务端不知道对方磁盘长什么样，
    /// 它只能"把目标目录的名字/路径原样发过去"；真正知道"这台机器上哪些目录允许被写"的只有客户机自己。
    /// 上报之后，<b>分发页就能把这些目录列出来让人选</b>，而不是让人凭空手输一个路径再去撞客户机的白名单。
    /// </para>
    /// <para>
    /// <b>放在清单里</b>：白名单是<b>机器级恒定量</b>（管理员改 <c>appsettings.json</c> 才变），
    /// 与 <see cref="ThermalState"/> 同一个道理 —— 清单"一机一行、6 小时一次"，正好。
    /// </para>
    /// <para>
    /// ⚠️ <b>这里只是"能选什么"的提示，不是安全边界</b>：真正的拦截发生在客户机落地前
    /// （<c>FileTransferClient</c> 比对白名单，越界直接拒绝并回报失败）。
    /// 服务端不校验、也不该校验 —— 它没有对方磁盘的真相。
    /// </para>
    /// <para>null = 该客户端版本还没上报过这个字段（老客户端），页面按"未上报"处理。</para>
    /// </summary>
    IReadOnlyList<string>? TransferInboxDirectories = null,
    /// <summary>
    /// 本机可访问的<b>根目录（盘符）</b>，用作回传"源路径"输入框的<b>候选提示</b>（M4.4 起）。
    /// <para>
    /// 🔥 <b>2026-10-15 起它不是白名单、也不构成任何限制</b>：客户机对回传源路径
    /// <b>不限制目录</b>（用户拍板），原来那个可收窄的 <c>Agent:Transfer:OutboxDirectories</c>
    /// 已连同机制一并删除。字段名沿用是为了不动线协议与前端预填逻辑。
    /// </para>
    /// <para>
    /// <b>但还是有用</b>：回传页拿它当"这台机器上有哪几个盘"的提示
    /// （与「浏览」的顶层是同一个集合），免得运维盲填一个不存在的盘符。
    /// 报的是 <c>{盘符}\</c>（如 <c>C:\</c>），不含未映射的子目录。
    /// </para>
    /// <para>null = 老客户端未上报；空数组 = 这个客户端知道这件事，但本机没启用文件传输。</para>
    /// </summary>
    IReadOnlyList<string>? TransferOutboxDirectories = null);

/// <summary>
/// 清单查询的结果。
/// </summary>
/// <param name="CapturedAt"><b>null = 从未上报过硬件清单</b>（不是"采到了空清单"）。</param>
/// <param name="Inventory">清单本体；为 null 表示没上报过或那份 JSON 已损坏。</param>
public sealed record AgentInventoryEnvelopeDto(
    int AgentId,
    DateTimeOffset? CapturedAt,
    AgentInventoryDto? Inventory);

/// <summary>
/// 一次上报的应答（M4.2）。
/// <para>
/// <b>为什么要应答</b>：客户端靠它决定"这批算不算送达"。
/// 尤其是开关机事件的水位 —— 服务端明确说没接受，客户端就必须<b>不推进水位</b>、
/// 下次重发，否则那几条记录会永远消失（而"这台机器上个月关过几次机"恰恰是拿不到第二遍的数据）。
/// </para>
/// </summary>
/// <param name="Ok">是否被接受。</param>
/// <param name="Status">机器可读的原因（<c>ok</c> / <c>empty</c> / <c>toomany</c> / <c>rejected</c> / <c>error</c>）。</param>
/// <param name="Message">人话原因（会进客户端本地日志）。</param>
/// <param name="Accepted">真正落库的条数（去重后）。</param>
/// <param name="NeedKeyframe">
/// <b>请被控端"补一个关键帧"</b>（M5.4-c①，只对推帧那条链路有意义）。
///
/// <para>
/// 🔥 <b>为什么必须由中心说这一句</b>：H.264 的 P 帧参考前面的帧，
/// <b>丢一帧等于在参考链上戳一个洞</b> —— 从那一刻起观看端解出来的每一帧都是花的，
/// 而<b>解码器不会报错</b>。被控端自己丢帧时知道这件事（它置 <c>wantKeyframe</c>），
/// 而分片引入了<b>新的丢帧来源，它在中心</b>（重组失败 / 片丢了），
/// 被控端<b>无从知道</b>。没有这条回传，一次丢帧的恢复代价是
/// <b>最多一个 GOP</b>（<c>gopSize = 60</c>、默认 12 fps ⇒ <b>5 秒花屏</b>）；
/// 有了它，压到<b>下一帧</b>。
/// </para>
///
/// <para>
/// ⚠️ <b>默认 <c>false</c>，而且它只在 <see cref="Ok"/> 为真时才有意义</b>：
/// 被拒绝的那条应答本身就是一个刹车信号（"这路流该停了"），
/// 在它上面再叠一句"请补关键帧"只会让调用方两头都想照顾。
/// </para>
///
/// <para>
/// 🔴 <b>收到它<b>必须</b>节流</b>：中心在持续丢帧时会<b>每一片/每一帧</b>都带这个标志，
/// 而"每帧都要关键帧"等于把一次丢帧放大成<b>一路 IDR</b> —— 8K 的 IDR 正是最贵的东西。
/// 去重 / 节流的责任在<b>收到它的那一侧</b>（被控端服务），不在中心：
/// 中心不知道对面多久能补上一个，而对面知道。
/// </para>
/// </param>
public sealed record AgentIngestAckDto(bool Ok, string Status, string? Message, int Accepted, bool NeedKeyframe = false)
{
    public static AgentIngestAckDto Success(int accepted = 0) => new(true, "ok", null, accepted);

    /// <summary>"收下了，但我刚丢了一帧 —— 请补一个关键帧"（M5.4-c①，见 <see cref="NeedKeyframe"/>）。</summary>
    public static AgentIngestAckDto SuccessNeedKeyframe() => new(true, "ok", null, 0, NeedKeyframe: true);

    public static AgentIngestAckDto Rejected(string status, string message) => new(false, status, message, 0);
}
