namespace ControlServer.Agent.Transfer;

/// <summary>
/// 客户机侧<b>文件传输</b>的配置（M4.4，<c>Agent:Transfer</c> 节）。
/// <para>
/// 🔥 <b>2026-10-20 起：读与写都不再有目录白名单</b> ——
/// </para>
/// <list type="bullet">
/// <item><b>写（下发落地）</b>：任务里的 <c>TargetDir</c> 只要写得成绝对路径就采用，
/// 目录不存在会创建。<see cref="InboxDirectories"/> / <see cref="DefaultInboxDirectory"/>
/// 现在的含义只剩"<b>没指定目标目录时落到哪</b>"（以及短标识的候选），不再是一道安全边界。</item>
/// <item><b>读（回传）</b>：2026-10-15 起就已无白名单，只要求绝对路径 + 文件存在且非空。</item>
/// </list>
/// <para>
/// 🔴 <b>因此"白名单是安全边界"这句话在这个版本里已经不成立</b>：
/// 两个方向的边界都退化为 <c>AdminOnly</c> + 授权闸门 + "管理员在资源管理器里显式点选"。
/// Agent 往往以 LocalSystem 运行 ⇒ 能往任意路径写 = 持有管理员身份者可在被控机上落任意内容。
/// 这条取舍必须向部署方讲清，不要以为"默认目录很窄"就还有约束。
/// </para>
/// <para>
/// ⚠️ 回传读取范围刻意不提供开关：曾经有过一个 <c>OutboxDirectories</c>（回传读取白名单，
/// 可收窄），2026-10-15 被要求连同机制一起删除 —— 留一个"配了才生效的收窄开关"会退化成
/// "配了等于没配"的陷阱，而那种陷阱在本项目里是明令禁止的。
/// </para>
/// <para>
/// ⚠️ <see cref="InboxDirectories"/> <b>从命令行传必须带索引</b>
/// （<c>--Agent:Transfer:InboxDirectories:0=D:\logs</c>）；裸键不报错也不生效，详见该属性的注释。
/// </para>
/// </summary>
public sealed class TransferOptions
{
    /// <summary>
    /// 是否启用文件传输。默认开启 —— 它与"硬件温度"那种会装内核驱动的可选能力不同，
    /// 只是几个 HTTP 调用，且默认白名单是数据目录下的私有子目录，不动系统任何地方。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// <b>默认下发目录</b>的候选清单（2026-10-20 起<b>不再是白名单</b>）。
    ///
    /// <para>
    /// 它现在只决定两件事：① 任务没指定 <c>TargetDir</c> 时落到哪；② 传的是短标识
    /// （<c>inbox</c> / <c>logs</c>）时按末段名匹配到哪个绝对路径。
    /// <b>绝对路径不再需要命中本表</b> —— 见类注释里那条代价说明。
    /// </para>
    ///
    /// <para>
    /// 留空 = 只用 <see cref="DefaultInboxDirectory"/>；两个都留空 = 用 <c>{状态目录}\inbox</c>。
    /// </para>
    /// <para>
    /// ⚠️ <b>命令行传这项必须带索引</b>：<c>--Agent:Transfer:InboxDirectories:0=D:\logs</c>。
    /// 写成不带 <c>:0</c> 的 <c>--Agent:Transfer:InboxDirectories=D:\logs</c> <b>不会报错、也不会生效</b>
    /// （配置绑定器把裸键当标量，而这里要的是列表 ⇒ 结果是一个空表 ⇒ 退回默认）。
    /// 这是"配了等于没配"里最难查的一种：服务照常启动，名单却还是默认值。
    /// </para>
    /// </summary>
    public List<string> InboxDirectories { get; set; } = new();

    /// <summary>默认下发目录（任务没指定 <c>TargetDir</c> 时用它）。留空 = 白名单第一项。</summary>
    public string DefaultInboxDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 本机同时进行的传输数上限（1–8，默认 2）。
    /// <para>
    /// <b>为什么客户机也要限并发</b>：一次下发给 100 台机器，若每台都"来多少传多少"，
    /// 中心侧的连接数与磁盘 IO 会同时被打满；而客户机自己再开一堆传输，
    /// 只会让每一路都变慢、进度条全都卡住。限 2 路既能让单机尽快传完，
    /// 又给中心侧留出背压的空间（拉取模型下，"慢"本身就是背压）。
    /// </para>
    /// </summary>
    public int MaxConcurrent { get; set; } = 2;

    /// <summary>进度回报步长（百分比，1–50，默认 5）。<b>按百分比而不是按字节</b>：2GB 的文件若每块都报，会变成 2000 次 POST。</summary>
    public int ProgressStepPercent { get; set; } = 5;

    /// <summary>回传上传时每块的字节数（服务端会给出建议值，以它为准）。</summary>
    public int UploadChunkBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>可重试失败的最大尝试次数（1–10，默认 3）。<b>致命错误（越界 / 文件不存在 / 摘要不符）不重试</b>。</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>两次尝试之间的间隔秒数（1–300，默认 5）。</summary>
    public int RetryDelaySeconds { get; set; } = 5;
}
