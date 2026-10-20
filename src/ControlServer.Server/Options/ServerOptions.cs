using System.ComponentModel.DataAnnotations;
using ControlServer.Core.Licensing;

namespace ControlServer.Server.Options;

/// <summary>
/// 强类型配置（appsettings.json 的 "Server" 节）。
/// 启动即校验（ValidateOnStart）：非法配置直接给出明确错误，而不是运行期才崩。
/// </summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public StartupOptions Startup { get; set; } = new();

    public HttpOptions Http { get; set; } = new();

    public ListenerOptions Listener { get; set; } = new();

    public StorageOptions Storage { get; set; } = new();

    public AuthOptions Auth { get; set; } = new();

    public SecurityOptions Security { get; set; } = new();

    public TimeOptions Time { get; set; } = new();

    public TimerOptions Timer { get; set; } = new();

    public ImagesOptions Images { get; set; } = new();

    /// <summary>文件仓库与分发（M4.4）。对应 <c>Server:Files</c>。</summary>
    public FilesOptions Files { get; set; } = new();

    public LicenseOptions License { get; set; } = new();

    /// <summary>客户端 Agent 的发现（UDP 广播）配置（M4.1）。对应 <c>Server:Discovery</c>。</summary>
    public DiscoveryOptions Discovery { get; set; } = new();

    /// <summary>客户端 Agent 的在线判定与准入配置（M4.1）。对应 <c>Server:Agent</c>。</summary>
    public AgentOptions Agent { get; set; } = new();

    /// <summary>业务日志的保留策略（M6.5）。对应 <c>Server:Logging</c>。</summary>
    public LoggingOptions Logging { get; set; } = new();

    /// <summary>远程桌面的并发准入（M5.5）。对应 <c>Server:Remote</c>。</summary>
    public RemoteOptions Remote { get; set; } = new();
}

/// <summary>
/// <b>远程桌面的并发准入</b>（M5.5 决策 8：路数 × 引擎 × 总带宽三重配额）。对应 <c>Server:Remote</c>。
///
/// <para>
/// 🔥 <b>为什么必须有这一段</b>：每路超 4K 都要吃一台被控机的硬件编码引擎，
/// 而所有路共用<b>千兆</b>。不设配额时，第一次事故的形态是
/// "第 5 路开起来之后，<b>前 4 路一起变卡</b>" —— 现场只会看到"远控变卡了"，
/// 没人会把因果连到"刚多开了一路"上。配额把这个因果**在入口处**说清楚。
/// </para>
///
/// <para>
/// ⚠️ <b>三个值分三个维度</b>，因为它们超限时该做的事不同：
/// 路数超了要"先关一路"；带宽超了要"降一档质量"；单路超了是"这一路自己有问题"。
/// 合成一个"太忙了"会让现场三个方向全试一遍。
/// </para>
/// </summary>
public sealed class RemoteOptions
{
    /// <summary>
    /// 同时最多几路远控（<b>默认 4</b>，决策 8）。
    /// <para>⚠️ 它是<b>全局</b>上限，与"同一台机器只允许一路"是两道独立的闸门。</para>
    /// </summary>
    [Range(1, 64)]
    public int MaxConcurrentSessions { get; set; } = 4;

    /// <summary>
    /// 全部会话合计的<b>入方向码率</b>上限（Mbps，<b>默认 700</b> ≈ 千兆的 70%）。
    ///
    /// <para>
    /// 🔥 <b>为什么是 70% 而不是 100%</b>：这条链路上跑的不只是视频 ——
    /// 心跳、遥测、配置、音频、文件传输全在同一块网卡上。
    /// 把视频吃满到 100%，最先坏掉的是"别的功能开始超时"，
    /// 而那种故障在现场看起来与远控毫无关系。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 判定口径是<b>最近 3 秒的实测入向码率</b>（见 <c>RemoteSessionRegistry.TotalBitsPerSecond</c>），
    /// 不是"配置了多少码率"：配置只是上界，实际编出来多少要看内容。
    /// </para>
    /// </summary>
    [Range(1, 100_000)]
    public int MaxTotalMbps { get; set; } = 700;

    /// <summary>
    /// 单路码率上限（Mbps，<b>默认 40</b>）。
    /// <para>
    /// ⚠️ 它挡的是"某一路自己把带宽吃干净"：8K 高质量档按 20 Mbps 目标编，
    /// 内容突变（满屏噪点）时会冲高。单路上限让这种冲高**只影响它自己**。
    /// </para>
    /// </summary>
    [Range(1, 100_000)]
    public int MaxSessionMbps { get; set; } = 40;
}

/// <summary>
/// 业务日志（<c>logs/log-YYYY-MM-DD.ndjson</c>）的保留策略（M6.5）。对应 <c>Server:Logging</c>。
///
/// <para>
/// <b>为什么这两个值要做成配置项</b>：日志体积直接由"部署规模 × 出了多少事"决定。
/// 10 台展厅的日志一年也就几十 MB，而 300 台、又开了 Debug 级别的场合，
/// 一天就能顶掉一个月。烧死在代码里意味着换个规模就要重新发版。
/// </para>
///
/// <para>
/// ⚠️ 这里只管<b>业务日志</b>。宿主日志 <c>server-*.log</c> 由 Serilog 自己滚动
/// （<c>retainedFileCountLimit = 31</c>，见 <c>Program.cs</c>），两套不要混。
/// </para>
/// </summary>
public sealed class LoggingOptions
{
    /// <summary>保留策略总开关，默认开。关掉 = 回到 M6.5 之前"只增不减"的行为。</summary>
    public bool RetentionEnabled { get; set; } = true;

    /// <summary>
    /// 保留天数（含今天），默认 <b>30</b>。≤ 0 = 不按天数删。
    /// <para>30 天对应"出了事回头查得到"的常见要求；再长的追溯应当靠审计表而不是日志文件。</para>
    /// </summary>
    [Range(-1, 3650)]
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// 日志总体积上限（MB），默认 <b>200</b>。≤ 0 = 不按总量删。
    /// <para>
    /// 200 MB 是"日志暴涨"的兜底：把级别调到 Debug 排查时，一天能写掉几百 MB，
    /// 而磁盘满会连带把数据库与文件仓库一起卡死。宁可丢掉最旧的那几天。
    /// </para>
    /// </summary>
    [Range(-1, 1024 * 100)]
    public int RetentionMaxMegabytes { get; set; } = 200;

    /// <summary>转成 Core 侧的纯数据策略（Core 不认识本配置类型）。</summary>
    public Core.Services.LogRetentionOptions ToRetention() => new()
    {
        Enabled = RetentionEnabled,
        RetentionDays = RetentionDays,
        MaxTotalMegabytes = RetentionMaxMegabytes,
    };
}

/// <summary>
/// 客户端自动发现（M4.1）。对应 <c>Server:Discovery</c>。
/// <para>
/// <b>方向是"客户端广播、服务端应答"</b> —— 服务端<b>不主动扫描子网</b>。
/// 主动扫描会在 Windows 防火墙丢入站、跨网段失效、254 台超时、多网卡不知扫哪个、
/// 被安全软件告警五处同时失手；而"客户端连出来"永远成立。
/// 用户视角仍然是"装完就自己出现了"。
/// </para>
/// </summary>
public sealed class DiscoveryOptions
{
    /// <summary>是否启用发现应答。关掉后客户端只能靠安装时填写的固定地址注册。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 发现端口，默认 <b>9998</b>。
    /// <para>⚠️ 刻意<b>避开对码的 9999</b>：两者语义完全不同，抢同一个端口会让
    /// "对接码监听起不来"和"发现应答收不到"互相掩盖。</para>
    /// </summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 9998;
}

/// <summary>
/// 客户端 Agent 的在线判定与准入（M4.1）。对应 <c>Server:Agent</c>。
/// </summary>
public sealed class AgentOptions
{
    /// <summary>
    /// 心跳间隔（秒），默认 10。服务端在客户端连上时把该值下发，客户端据此定时上报。
    /// </summary>
    [Range(2, 300)]
    public int HeartbeatSeconds { get; set; } = 10;

    /// <summary>
    /// 超过"最后心跳 + 该秒数"即判<b>不稳定</b>（黄点），默认 30 秒。
    /// <para>⚠️ 必须 &gt; <see cref="HeartbeatSeconds"/>，否则正常心跳的间隙也会被判成不稳定。</para>
    /// </summary>
    [Range(3, 3600)]
    public int StaleAfterSeconds { get; set; } = 30;

    /// <summary>
    /// 超过"最后心跳 + 该秒数"即判<b>离线</b>（灰点），默认 75 秒。
    /// <para>
    /// <b>为什么是 75 而不是文档初稿的"5 分钟"</b>：验收标准是"关机 ≤90 秒变离线"。
    /// 硬断电时客户端来不及发"再见"，最后心跳最多早 <see cref="HeartbeatSeconds"/> 秒，
    /// 因此最坏情况 = 10 + 75 = <b>85 秒</b>，留 5 秒余量。
    /// 5 分钟的阈值会让"刚关机的机器在页面上还绿着"持续很久，反而更像故障。
    /// </para>
    /// </summary>
    [Range(5, 86400)]
    public int OfflineAfterSeconds { get; set; } = 75;

    /// <summary>
    /// 在线表容量上限（默认 500）。
    /// <para>
    /// 免凭据准入（2026-10-13）后，这是唯一的兜底闸门：同网段任何装了客户端的机器都能注册，
    /// 到上限后新注册会被明确拒绝并在日志里留下可查记录，而不是悄悄写进去几万行。
    /// 它不是准入手段 —— 只是防撑爆。
    /// </para>
    /// </summary>
    [Range(1, 100000)]
    public int MaxHosts { get; set; } = 500;

    /// <summary>客户端安装包所在的子目录名（相对数据目录），默认 <c>downloads</c>。</summary>
    [Required]
    public string InstallerDirectoryName { get; set; } = "downloads";

    /// <summary>遥测（M4.2）。对应 <c>Server:Agent:Telemetry</c>。</summary>
    public AgentTelemetryOptions Telemetry { get; set; } = new();
}

/// <summary>
/// 客户端遥测的采集 / 存储 / 推送策略（M4.2）。对应 <c>Server:Agent:Telemetry</c>。
/// <para>
/// <b>这些值都可以在部署后改</b>（appsettings.json 或环境变量），因为它们直接决定：
/// 库长多快、页面刷多勤、一次故障丢多少数据。把它们烧死在代码里会让"设备从 20 台涨到 300 台"
/// 变成一次重新发版。
/// </para>
/// </summary>
public sealed class AgentTelemetryOptions
{
    /// <summary>
    /// 是否启用遥测。关掉后服务端拒绝客户端上报，页面上显示"未启用"（而不是一片 0）。
    /// <para>给"只想看在线/离线、不想让库长起来"的部署留的开关。</para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>指标采样间隔（秒），默认 5。服务端在客户端连上时下发，客户端据此定时采样。</summary>
    [Range(1, 60)]
    public int MetricsSeconds { get; set; } = 5;

    /// <summary>
    /// 5 秒明细的保留天数，默认 30。
    /// <para>⚠️ 容量估算（方案 §6.4）：一台机器一天 17280 条，一条约 400 字节的 JSON 但落库后
    /// 约 60–80 字节 ⇒ <b>约 1.2 MB/台/天</b>。300 台 × 30 天 ≈ <b>11 GB</b>。
    /// 上百台规模的部署应当把这个值调小（例如 7 天）并依赖日聚合层。</para>
    /// </summary>
    [Range(1, 3650)]
    public int DetailRetentionDays { get; set; } = 30;

    /// <summary>日聚合的保留天数，默认 365。</summary>
    [Range(1, 3650)]
    public int DailyRetentionDays { get; set; } = 365;

    /// <summary>
    /// 内存缓冲的落盘间隔（秒），默认 30。
    /// <para>这是"写放大"与"崩溃丢多少数据"之间的旋钮：越大库越省、断电丢得越多。
    /// 30 秒对监控数据是合理取舍（丢半分钟曲线没人会察觉，而每秒一条写会把 WAL 塞满）。</para>
    /// </summary>
    [Range(5, 600)]
    public int FlushSeconds { get; set; } = 30;

    /// <summary>硬件清单的上报间隔（小时），默认 6。</summary>
    [Range(1, 168)]
    public int InventoryHours { get; set; } = 6;

    /// <summary>开关机记录的轮询间隔（秒），默认 30。</summary>
    [Range(10, 3600)]
    public int PowerEventPollSeconds { get; set; } = 30;

    /// <summary>客户端首次启动时对开关机事件的<b>回溯补扫</b>天数，默认 7。</summary>
    [Range(0, 365)]
    public int PowerEventBackfillDays { get; set; } = 7;

    /// <summary>单个指标批次的最大样本数，默认 600（5 秒 × 600 = 50 分钟）。超限整批拒收。</summary>
    [Range(10, 10000)]
    public int MaxBatchSamples { get; set; } = 600;

    /// <summary>单个开关机事件批次的最大条数，默认 200。超限整批拒收。</summary>
    [Range(1, 2000)]
    public int MaxBatchPowerEvents { get; set; } = 200;

    /// <summary>
    /// 实时推送（<c>AgentTelemetryUpdated</c>）的节流间隔（秒），默认 2。
    /// <para>置 0 = 不推送（前端只靠轮询刷新）。</para>
    /// </summary>
    [Range(0, 60)]
    public double NotifyThrottleSeconds { get; set; } = 2;

    /// <summary>时序查询的默认时间窗（小时），默认 6。</summary>
    [Range(1, 8760)]
    public int DefaultQueryHours { get; set; } = 6;

    /// <summary>
    /// 时序查询允许的最大跨度（天），默认 400。
    /// <para>挡住"查十年"这种把库扫穿、把浏览器画死的请求。</para>
    /// </summary>
    [Range(1, 3650)]
    public int MaxQueryDays { get; set; } = 400;
}

/// <summary>
/// 设备授权（M3 授权子系统）。对应 <c>Server:License</c>。
/// </summary>
public sealed class LicenseOptions
{
    /// <summary>
    /// 闸门执行档位。
    /// <para>
    /// ⚠️ <b>默认 <see cref="LicenseEnforcement.Warn"/>，不是 Enforce</b>。
    /// 现有部署（用户本机 + 新电脑）<b>都没有授权文件</b>；默认 Enforce 会让"升级当天
    /// 全线 403"，用户看到的是"装完就废了"。默认 Warn 保证升级这件事本身永不锁死，
    /// 再由下面的自动试用把设备带入"可用"状态。
    /// </para>
    /// </summary>
    public LicenseEnforcement Enforcement { get; set; } = LicenseEnforcement.Warn;

    /// <summary>
    /// 首次启动时，若既没有授权文件、也没有试用记录，则自动生成本机试用。
    /// <para>这是"装好就能跑"与"升级不锁死"两条验收标准的基础。</para>
    /// </summary>
    public bool AutoTrial { get; set; } = true;

    /// <summary>本机试用的天数。</summary>
    [Range(1, 3650)]
    public int TrialDays { get; set; } = 30;

    /// <summary>
    /// 联网时间不可得时，是否拒绝"生成试用"与"导入授权"。
    /// <para>
    /// 默认 <c>false</c>：内网无外网出口的部署是常态，硬性要求联网会把它们全部卡死在
    /// "连授权都导不进去"的状态（而授权导入是现场唯一的自救通道）。
    /// </para>
    /// <para>
    /// 置 <c>true</c> 会得到更强的防回拨保证（<c>ivt</c> 锚点必定来自真实网络时间），
    /// 代价是这类现场必须有人先解决网络出口。
    /// </para>
    /// </summary>
    public bool RequireNetworkTime { get; set; }

    /// <summary>
    /// 时钟容差（秒）：判定"本机时间是否早于授权的联网校对时间"时允许的回拨量。
    /// <para>默认 120 秒，与既有实现一致；服务器时钟漂移远小于此。</para>
    /// </summary>
    [Range(0, 600)]
    public double ClockToleranceSeconds { get; set; } = 120;
}

/// <summary>
/// 图片上传（M2-G1）。对应 <c>Server:Images</c>。
/// </summary>
public sealed class ImagesOptions
{
    /// <summary>
    /// 单张图片上传大小上限（字节），默认 4 MB。
    /// <para>
    /// 这个值同时决定 <c>FormOptions.MultipartBodyLengthLimit</c>
    /// （组合根里加 1 MB 余量给 multipart 头与边界），
    /// 因此**必须在启动时就绑定好** —— 它不只影响一句提示文案，
    /// 还决定 Kestrel 在读取请求体时肯缓冲多少字节。
    /// </para>
    /// </summary>
    [Range(1024, 64L * 1024 * 1024)]
    public long MaxUploadBytes { get; set; } = 4L * 1024 * 1024;
}

/// <summary>
/// 文件仓库与分发（M4.4）。对应 <c>Server:Files</c>。
/// </summary>
public sealed class FilesOptions
{
    /// <summary>
    /// 单文件大小上限（字节），默认 <b>4 GB</b>。
    /// <para>
    /// 验收要求"&gt;2GB 文件断点续传成功"，因此这个值必须明显大于 2GB。
    /// 它不是"防呆"用的（文件走分块流式写入，不会把内存吃光），而是挡住
    /// "误选了一个 50GB 的镜像把磁盘写爆"。
    /// </para>
    /// <para>⚠️ 与图片不同，<b>它不影响任何 Kestrel/Form 缓冲上限</b> ——
    /// 文件内容永远不经过 multipart 缓冲区，只按分块直接落盘。</para>
    /// </summary>
    [Range(1024, 1L * 1024 * 1024 * 1024 * 1024)]
    public long MaxFileBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>分块大小（字节），默认 8 MB。服务端把它下发给客户端，避免两边各拍一个数。</summary>
    [Range(64 * 1024, 256L * 1024 * 1024)]
    public int ChunkSizeBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>上传会话有效期（小时），默认 24。过期后暂存的 <c>.part</c> 会被后台清理回收。</summary>
    [Range(1, 24 * 30)]
    public int SessionExpiryHours { get; set; } = 24;

    /// <summary>
    /// 孤儿文件回收宽限期（小时），默认 <b>24</b>。对应 <c>Server:Files:OrphanGraceHours</c>。
    /// <para>
    /// 🔴 它不是一个"保守值"，是在防一个具体竞态：完成上传时先
    /// <c>File.Move(.part → repo)</c>、再落库，两步之间文件"已在盘上、库里还没有记录"，
    /// 与真正的孤儿长得一样。按"不在库里就删"去清理，会删掉刚刚上传成功的文件。
    /// 所以判据是<b>文件年龄</b>超期，而不是"本次扫描没对上"。
    /// </para>
    /// <para>调小它的唯一理由是你确信没有并发的等宽窗口；调到 0 以下按 1 小时处理。</para>
    /// </summary>
    [Range(1, 24 * 365)]
    public int OrphanGraceHours { get; set; } = 24;

    /// <summary>
    /// 已终结的分发任务保留天数，默认 30。
    /// <para>
    /// ⚠️ <b>只清"已终结"的</b>（完成 / 部分成功 / 失败 / 已取消）：还在等离线机器上线的任务，
    /// 再老也不能删 —— 删了那台机器一上线就再也补领不到，而运维看到的是"任务无故消失"。
    /// </para>
    /// </summary>
    [Range(1, 3650)]
    public int TaskRetentionDays { get; set; } = 30;
}

public sealed class StartupOptions
{
    /// <summary>
    /// 启动完成后自动用系统默认浏览器打开本机管理界面。
    /// 仅在「交互式控制台形态」下生效 —— Windows 服务 / systemd 等非交互会话
    /// 即使为 true 也会被跳过（Session 0 里没有用户的桌面可打开）。
    /// </summary>
    public bool AutoOpenBrowser { get; set; } = true;
}

public sealed class HttpOptions
{
    [Range(1, 65535)]
    public int Port { get; set; } = 8899;

    [Required(AllowEmptyStrings = true)]
    public string Urls { get; set; } = string.Empty;
}

public sealed class ListenerOptions
{
    [Range(1, 65535)]
    public int PairingPort { get; set; } = 9999;
}

public sealed class StorageOptions
{
    /// <summary>留空则使用平台默认数据目录（Windows: %ProgramData%\ControlServer）。</summary>
    public string DataDir { get; set; } = string.Empty;
}

public sealed class AuthOptions
{
    public string Mode { get; set; } = "Cookie";

    [Required]
    public string DefaultAdminUserName { get; set; } = "admin";

    [Required]
    [MinLength(6)]
    public string DefaultAdminPassword { get; set; } = "admin123";

    /// <summary>
    /// 是否强制"必须先改密"的账号在改密前不能调用业务接口（M3）。
    /// <para>
    /// <b>默认 <c>false</c>（2026-10-09 起）</b>：产品已取消"首次使用强制改密"——
    /// 出厂管理员、新建账号、管理员重置密码<b>都不再</b>被标记为"必须先改密"，
    /// 因此默认状态下没有任何账号会触发本闸门。
    /// 保留这个开关，是给确有需要的部署（例如服务暴露在不完全可信的网络）留一条
    /// 可自行开启的兜底路径：置 <c>true</c> 后，凡是被标记 <c>MustChangePassword = true</c>
    /// 的账号，在改密之前调用 <c>/api</c> 一律 403（白名单见 <c>Program.IsPasswordChangeAllowlisted</c>）。
    /// </para>
    /// <para>
    /// ⚠️ 集成测试宿主 <c>ControlServerFactory</c> 仍显式置 <c>false</c>：
    /// 每个用例只做一次登录就要跑业务断言，被闸门拦住会让业务覆盖整体退化成流程测试。
    /// 闸门机制本身由 <c>UserRbacTests.ForcedPasswordChangeTests</c> 用
    /// "显式开启闸门 + 直接标记账号"的方式覆盖。
    /// </para>
    /// </summary>
    public bool EnforcePasswordChange { get; set; } = false;

    /// <summary>登录端点的限速（M6.2）。</summary>
    public LoginRateLimitOptions LoginRateLimit { get; set; } = new();
}

/// <summary>
/// 登录限速（<c>Server:Auth:LoginRateLimit:*</c>，M6.2）。
///
/// <para><b>为什么需要它</b>：账号锁定（5 次失败锁 15 分钟）挡得住"猜口令"，
/// 但它<b>挡不住两种东西</b>：① 换账号名扫一遍（每个名字试 4 次，永不触发锁定）；
/// ② 把管理员账号<b>故意锁死</b>（输 5 次错密码即可，15 分钟内谁也进不来，
/// 循环即可永久拒绝服务）。按来源 IP 限速是这两条的同一副解药：
/// 单 IP 每分钟只能试有限次，上述两种打法的成本就被抬到不可接受。</para>
///
/// <para><b>为什么是"按 IP"而不是"按账号"</b>：按账号会与既有的锁定机制重复，
/// 而且给攻击者提供了"用别人的账号名触发限速"的新武器。按 IP 只惩罚发起方。</para>
///
/// <para>⚠️ <b>NAT 后的取舍</b>：整个展厅走一个出口 IP 时，所有人共享这一份额度。
/// 默认值（5 分钟 20 次）对"十几个人、偶尔登录"完全够用；如果现场确实更大，
/// 调 <see cref="PermitLimit"/> 即可，不必关掉它。</para>
/// </summary>
public sealed class LoginRateLimitOptions
{
    /// <summary>
    /// 是否启用。默认 <b>开</b>。
    /// <para>关掉只应发生在"前置网关已经做了限速"的部署，且要在运维文档里写明理由。</para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>窗口内允许的登录次数（每个来源 IP）。默认 20。</summary>
    [Range(1, 100_000)]
    public int PermitLimit { get; set; } = 20;

    /// <summary>窗口长度（分钟）。默认 5。</summary>
    [Range(1, 1440)]
    public int WindowMinutes { get; set; } = 5;
}

public sealed class SecurityOptions
{
    /// <summary>允许访问的网段（CIDR）。留空 = 不限制（内网部署默认）。</summary>
    public List<string> AllowedCidrs { get; set; } = new();
}

public sealed class TimeOptions
{
    /// <summary>
    /// 按顺序尝试的 NTP 服务器；第一个成功即停止。
    /// <para>
    /// ⚠️ <b>默认值刻意留空</b>：配置绑定对 <see cref="List{T}"/> 是<b>追加</b>而不是替换，
    /// 若这里带初始值，再配上 <c>appsettings.json</c> 的同名列表就会翻倍
    /// （实测 3 个默认值 + 3 个配置值 = 6 个），而且用户<b>无法通过配置删掉内置默认</b>。
    /// 内置默认值只放在 <c>TimeSyncSettings.DefaultNtpServers</c>，由组合根在列表为空时补上。
    /// </para>
    /// </summary>
    public List<string> NtpServers { get; set; } = new();

    /// <summary>
    /// 是否启用自动时钟校准（启动首校 + 周期重校）。
    /// <para>
    /// ⚠️ <b>集成测试环境必须置 <c>false</c></b>（<c>Server__Time__AutoSync=false</c>）——
    /// 否则测试会真的去联网查 NTP，既慢又不稳定。
    /// </para>
    /// <para>关闭后 <c>BeijingTime</c> 退化为"系统钟 + 固定 UTC+8"，即引入本特性之前的行为。</para>
    /// </summary>
    public bool AutoSync { get; set; } = true;

    /// <summary>单个 NTP 服务器的查询超时（毫秒）。</summary>
    [Range(100, 30000)]
    public int NtpTimeoutMs { get; set; } = 700;

    /// <summary>周期重校间隔（秒）。默认 30 分钟。</summary>
    [Range(60, 86400)]
    public int ResyncIntervalSeconds { get; set; } = 1800;

    /// <summary>偏差告警阈值（秒）。超过就打警告，但不拒绝定时触发（M2 方案 §5.4）。</summary>
    [Range(0.001, 600)]
    public double DriftWarnThresholdSeconds { get; set; } = 3;
}

/// <summary>
/// 定时调度（M2-D）的宿主配置。对应 <c>TimerSchedulerOptions</c>，
/// 之所以不直接绑定后者，是因为 Core 里用的是 <see cref="TimeSpan"/>，
/// 而配置文件里写秒数对人更友好（也避免 JSON 里出现 "00:00:01" 这种不直观的写法）。
/// </summary>
public sealed class TimerOptions
{
    /// <summary>定时调度总开关。关闭后宿主不注册调度循环。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>检测节拍（秒）。默认 1 秒，与旧版一致 —— 触发点是按秒精确匹配的，节拍大于 1 秒会漏触发。</summary>
    [Range(1, 60)]
    public double TickIntervalSeconds { get; set; } = 1;

    /// <summary>
    /// 进程内追赶窗口（秒）。单次 tick 迟到时，回补这段时间内的触发点。
    /// 超出该窗口的一律不补、只告警 —— 避免长时间阻塞（如 GC 停顿、磁盘卡顿）后集中爆发。
    /// </summary>
    [Range(1, 3600)]
    public int CatchUpWindowSeconds { get; set; } = 60;

    /// <summary>
    /// 启动时"服务停机期间漏了什么"的扫描窗口（秒）。默认 10 分钟。
    /// 置 0 关闭扫描。默认**只告警不执行**；要真补执行见 <see cref="BackfillMissed"/>。
    /// </summary>
    [Range(0, 86400)]
    public int MissedScanWindowSeconds { get; set; } = 600;

    /// <summary>
    /// 是否补执行"服务停机期间错过"的触发点（默认 <c>false</c>）。
    /// <para>
    /// 补执行＝**替现场按按钮**。开启后有三条硬约束（见 <c>TimerSchedulerService.BackfillMissedAsync</c>）：
    /// 每个触发点只补最近一次、总数不超过 <see cref="MaxBackfillCount"/>、只补
    /// <see cref="MissedScanWindowSeconds"/> 窗口内的。
    /// </para>
    /// <para>
    /// 展厅这类"闹钟式"配置（开屏/关屏/定点播片）建议开；任何"轮询式"配置
    /// （每分钟一次的数据上报）**必须关** —— 补执行对它没有意义，只会让动作连发。
    /// </para>
    /// </summary>
    public bool BackfillMissed { get; set; }

    /// <summary>单次启动补执行的总上限。超出只报告不执行（默认 20）。</summary>
    [Range(0, 500)]
    public int MaxBackfillCount { get; set; } = 20;

    /// <summary>触发记录（<c>timer_fires</c>）的保留天数。</summary>
    [Range(1, 3650)]
    public int RetentionDays { get; set; } = 35;
}
