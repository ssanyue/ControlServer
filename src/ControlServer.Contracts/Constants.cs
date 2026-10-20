namespace ControlServer.Contracts;

/// <summary>
/// 预置角色（三角色 RBAC）。
/// <para>
/// <b>Admin</b> = 超级管理员：用户管理、导入导出、恢复出厂、端口热切换、机器码；含 Operator 与 Viewer 的一切。
/// <b>Operator</b> = 操作员：执行业务动作、改配置（按钮 / 指令库 / 图片 / 定时）；
/// <b>Viewer</b> = 只读看板：只能看（看板 / 状态 / 日志 / 配置只读 / 定时总览 / 指令库只读）。
/// </para>
/// <para>这里只定义"角色名"这一层。<b>能力</b>由授权策略承载，见 <see cref="Policies"/>。</para>
/// </summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";

    /// <summary>全部合法角色（顺序即前端下拉框顺序）。</summary>
    public static readonly IReadOnlyList<string> All = new[] { Admin, Operator, Viewer };

    /// <summary>
    /// 角色名是否合法。<b>大小写敏感</b> —— 角色名会原样写入 <c>ClaimTypes.Role</c>，
    /// 而 <c>RequireRole</c> 的比较是大小写不敏感的；但库里存了 <c>admin</c> 之后，
    /// 前端下拉框回显、按角色过滤这类字符串比较会全部失配，所以入库前一律卡死。
    /// </summary>
    public static bool IsValid(string? role) => role is not null && All.Contains(role);

    /// <summary>中文显示名（仅用于界面展示，不参与任何比较）。</summary>
    public static string DisplayName(string? role) => role switch
    {
        Admin => "管理员",
        Operator => "操作员",
        Viewer => "查看者",
        _ => role ?? string.Empty,
    };
}

/// <summary>授权策略名。</summary>
public static class Policies
{
    /// <summary>仅 <see cref="Roles.Admin"/>。</summary>
    public const string AdminOnly = "AdminOnly";

    /// <summary><see cref="Roles.Admin"/> + <see cref="Roles.Operator"/>（业务操作与配置写入）。</summary>
    public const string CanOperate = "CanOperate";

    /// <summary>
    /// 读仓库文件（下载）：<see cref="Roles.Admin"/> + <see cref="Roles.Operator"/>，
    /// <b>或者</b>一台合法客户机（AgentKey）。
    /// <para>
    /// <b>为什么这里要把"人"和"机器"放进同一条策略</b>：下发文件时，字节是由
    /// <b>客户机自己</b>用 <c>Range</c> 拉走的 —— 它拿的是 AgentKey，不是 Cookie。
    /// 若坚持开两个端点，就会有"两处下载逻辑"，而它们的 Range / 缓存 / 摘要校验
    /// 迟早会分叉。所以这里显式接受两种身份，但<b>仍然拒绝只读的 Viewer</b>。
    /// </para>
    /// <para>
    /// ⚠️ 这不是把两套身份混为一谈（铁律见 <see cref="AgentAuthentication"/>）：
    /// 策略仍然能区分它们 —— 客户机身份没有角色声明，因此拿不到 <see cref="AdminOnly"/> / <see cref="CanOperate"/>。
    /// </para>
    /// </summary>
    public const string FileRead = "FileRead";

    /// <summary>
    /// 写仓库文件（上传分块 / 完成 / 删除）：<see cref="Roles.Admin"/>，
    /// <b>或者</b>一台合法客户机（回传上传）。
    /// <para><b>不含 Operator</b>：能往仓库写文件，就等于能把任意内容推给所有客户机。</para>
    /// </summary>
    public const string FileIngest = "FileIngest";

    /// <summary>
    /// 读某台客户机的配置（三张任务清单 + UDP 指令通道）：
    /// <see cref="Roles.Admin"/> + <see cref="Roles.Operator"/>，<b>或者</b>一台合法客户机。
    /// <para>
    /// 与 <see cref="FileRead"/> 同样的理由：<b>"人"和"机器"要读同一份数据</b> ——
    /// 页面读它是为了展示与编辑；客户机读它是为了执行（它拉自己的配置来跑任务）。
    /// 开两个端点就会有两份取数逻辑，而它们迟早会分叉。
    /// </para>
    /// <para>
    /// ⚠️ 归属校验（"客户机只能读自己那一份"）<b>不在这条策略里</b>，
    /// 而在控制器里显式比较 AgentId —— 策略只能判断"是不是一台合法机器"，
    /// 判断不了"它是不是这台"。少了那一步，任何一台机器都能读到别台的配置
    /// （里面有目标 IP 与发送内容）。
    /// </para>
    /// </summary>
    public const string AgentConfigRead = "AgentConfigRead";

    /// <summary>
    /// <b>客户机"报告自己身上的事"</b>（2026-10-15，需求①：告知"我要换中心了"）。
    /// <para>
    /// 与 <see cref="AgentConfigRead"/> 同构，但方向相反（一个是读、一个是写）——
    /// <b>刻意不复用那个名字</b>：策略名是给排障的人看的，
    /// 让一条写操作挂在"ConfigRead"下面，下一次改这块的人会先怀疑自己看错了文件。
    /// </para>
    /// <para>同样：<b>归属校验在控制器里</b>（客户机只能报自己名下）。</para>
    /// </summary>
    public const string AgentSelfReport = "AgentSelfReport";
}

/// <summary>
/// 自定义声明名。
/// <para>
/// 这些名字<b>会写进 Cookie 载荷</b>，改名等于让所有在线会话丢掉该项信息
/// （最坏情况是已改过密码的人被再次要求改密）。改动前先想清楚兼容性。
/// </para>
/// </summary>
public static class AuthClaims
{
    /// <summary>是否必须先修改密码。取值为字符串 <c>"true"</c> / <c>"false"</c>。</summary>
    public const string MustChangePassword = "cs.mcp";
}

/// <summary>固定路由。</summary>
public static class Routes
{
    public const string EventsHub = "/hubs/events";

    /// <summary>
    /// 客户端 Agent 的长连接通道（M4.1）。
    /// <para>
    /// <b>方向是"客户端连出来"</b>：Agent 主动建立这条 WebSocket，服务端通过它下发指令。
    /// 客户端因此<b>不需要开任何入站端口</b> —— 这一条同时解决了 Windows 防火墙、
    /// NAT、以及"客户机 IP 会变"三类问题。
    /// </para>
    /// </summary>
    public const string AgentHub = "/hubs/agent";

    /// <summary>
    /// <b>观看端</b>的实时通道（M5.1）。
    /// <para>
    /// 🔥 它与 <see cref="AgentHub"/> 的方向相反，服务的是完全不同的人：
    /// <c>/hubs/agent</c> 是<b>被控机连出来</b>的（送指令、收上报）；
    /// 本通道是<b>管理员浏览器连进来</b>的（看画面）。
    /// 两者的身份模型、鉴权策略、消息形状没有一处相同 ——
    /// 合成一条通道会让"谁在发这条消息"变成一个要靠上下文猜的问题。
    /// </para>
    /// </summary>
    public const string RemoteHub = "/hubs/remote";
}

/// <summary>
/// 服务端 → 客户机的 Hub 方法名（<c>/hubs/agent</c>）。
/// <para>
/// <b>为什么要有这组常量</b>：方法名是客户端与服务端之间的字符串合同，
/// 写错一个字母的表现是"服务端说发出去了、客户端毫无反应"，<b>没有任何编译错误</b>。
/// 集中一处、两边引用，是唯一能防住这类漂移的办法。
/// </para>
/// <para>
/// ⚠️ <b>MVC 路由不在这里</b>：控制器一律用不带前导斜杠的内联 <c>[Route("api/xxx")]</c>。
/// 这不是随意定的 —— 授权闸门（<c>LicenseGate</c>）按路由模板 <c>StartsWith("api/")</c> 判定，
/// 而 <c>[Route("/api/xxx")]</c> 产出的模板是 <c>/api/xxx</c>，会**静默匹配不上**、闸门形同虚设。
/// 这里的两个 Hub 路径带斜杠是因为它们喂给 <c>MapHub</c>，不走那条判据。
/// </para>
/// </summary>
public static class AgentHubMethods
{
    /// <summary>连接建立后服务端发的第一条消息。载荷 <see cref="AgentWelcomeDto"/>。</summary>
    public const string Welcome = "Welcome";

    /// <summary>下发一条指令（重启等）。载荷 <see cref="AgentCommandDto"/>。</summary>
    public const string Command = "Command";

    /// <summary>
    /// 通知该机<b>已被管理员拉黑</b>，请立刻停下（2026-10-15，取代原 <c>Revoked</c>）。
    /// 载荷为 string（原因）。
    /// <para>
    /// ⚠️ <b>老客户端不认识这个方法名</b>：它会安静地忽略，于是那台机器会多挂最多一个
    /// 心跳周期 —— 直到心跳被服务端 <c>Context.Abort()</c> 掐掉。
    /// 降级路径仍然成立，只是慢几秒。
    /// </para>
    /// </summary>
    public const string Blocked = "Blocked";

    /// <summary>派发一条文件传输任务（M4.4）。载荷 <see cref="AgentTransferTaskDto"/>。</summary>
    public const string PushTransferTask = "PushTransferTask";

    /// <summary>
    /// <b>列出客户机上的一个目录</b>（M4.4+，回传源路径选择用）。
    /// 载荷 <see cref="AgentBrowseRequestDto"/>，<b>返回值</b> <see cref="AgentBrowseResultDto"/>。
    ///
    /// <para>
    /// 🔥 本项目里<b>唯一一条"服务端要等客户机回话"</b>的 Hub 调用 ——
    /// 其余全是单向推送。落地靠 <c>ISingleClientProxy.InvokeAsync&lt;T&gt;</c>：
    /// 服务端拿到 <c>Task&lt;T&gt;</c>，超时由服务端自己掐（见 <c>AgentFileBrowseService</c>）。
    /// </para>
    /// <para>
    /// <b>为什么不给客户机开一个 HTTP 端点</b>：那会打破"客户端不开任何入站端口"这条铁律，
    /// 也就等于在每台展厅机器上重新引入防火墙 / NAT / IP 变动这三类问题。
    /// 长连接本来就在，借它一问一答是唯一不付新代价的做法。
    /// </para>
    /// </summary>
    public const string ListDirectory = "ListDirectory";

    /// <summary>
    /// <b>要客户机「接收到的数据」快照</b>（2026-10-13，排查"指令不生效"用）。
    /// 无载荷，<b>返回值</b> <see cref="AgentReceivedDataDto"/>。
    ///
    /// <para>
    /// 与 <see cref="ListDirectory"/> 同形（同为"服务端等客户机回话"），也同一条理由：
    /// 客户机依旧<b>一个入站端口都不开</b>。
    /// </para>
    /// <para>
    /// <b>为什么不带载荷</b>：客户机那个环形缓冲本来就只有 200 条，
    /// 一次全给回来最省事 —— 加一个 <c>Take</c> 参数只多一种"要几条"的分歧，
    /// 而它并不能省下什么（200 条结构化记录不过几十 KB）。
    /// </para>
    /// </summary>
    public const string ListReceivedData = "ListReceivedData";

    /// <summary>
    /// 该机配置已变更（M4.5）。载荷 <see cref="AgentConfigChangedDto"/>（只带版本号）。
    /// <para>
    /// <b>推送里不带配置正文</b>：正文走 HTTP 拉取（<c>GET /api/agents/{id}/config</c>）。
    /// 这样"推送"只承担"叫醒"的职责，配置体一旦变大也不会撑爆 Hub 消息；
    /// 而且客户端重新上线补拉、推送丢失这两种情况共用同一条取数路径。
    /// </para>
    /// </summary>
    public const string ConfigChanged = "ConfigChanged";

    /// <summary>
    /// <b>把一批输入事件送进被控机</b>（M5.2）。载荷 <see cref="RemoteInputBatchDto"/>。
    ///
    /// <para>
    /// 🔥 <b>为什么它必须是独立的 Hub 方法，而不是挂到 <see cref="Command"/> 上</b>
    /// （明明 <c>Command</c> 那个信封本来就能加一个新的指令名）：
    /// </para>
    /// <list type="bullet">
    ///   <item><b>频率</b>：拖动一次鼠标就是几十到几百条事件，每秒可能十几次调用。
    ///         <c>Command</c> 那条路每一发都会进"接收到的数据"环形缓冲并走一遍
    ///         指令执行器 —— 60 Hz 下它会把那个面板刷成全是鼠标移动，
    ///         而它存在的意义恰恰是"看清到底收到了什么指令"；</item>
    ///   <item><b>方向与语速相反</b>：输入是<b>我们要它立刻照做</b>的东西，
    ///         走"通用指令"那条路会被"指令排队 / 结论回执"这一整套语义拖住；</item>
    ///   <item><b>实参个数纪律</b>：SignalR 的参数个数不能变
    ///         （见 <see cref="AgentHubClientMethods.Heartbeat"/> 的实测备注），
    ///         给它单开一条是最省事的做法。</item>
    /// </list>
    ///
    /// <para>
    /// ⚠️ <b>旧客户端不认识这个方法</b>：它会安静地忽略，于是"输入完全没反应"。
    /// 中心侧无法从协议上分辨这件事（除非比对心跳带来的版本号），
    /// 所以页面必须把"输入回传需要 M5.2 及以上的客户端"写在提示里。
    /// </para>
    /// </summary>
    public const string RemoteInput = "RemoteInput";
}

/// <summary>
/// 客户端 → 服务端的 Hub 方法名（<c>/hubs/agent</c> 上由 Agent 主动调用）。
/// <para>
/// <b>为什么单独一组</b>：与 <see cref="AgentHubMethods"/> 相反方向。混在一起写，
/// 读代码时会分不清"这条是我要实现的、还是我要调用的"。
/// </para>
/// <para>
/// ⚠️ 这些名字必须与 <c>AgentHub</c> 上的<b>公开方法名逐一对应</b>（SignalR 按方法名绑定）。
/// 写成常量而不是字面量，是为了让编译器帮我们挡住拼写漂移 ——
/// 拼错的表现是"客户端调了、服务端毫无反应"，且没有任何编译错误。
/// </para>
/// </summary>
public static class AgentHubClientMethods
{
    /// <summary>
    /// 心跳（M4.1）。载荷：<c>agentVersion</c>（客户端版本字符串）。
    ///
    /// <para>🔥 <b>参数个数必须保持 1，不许加"可选参数"</b>。实测（SignalR 8.0.10）：</para>
    /// <list type="bullet">
    ///   <item>客户端传 1 个实参 → 服务端方法有 2 个参数（第 2 个带 C# 默认值）⇒ <b>调用失败</b>；</item>
    ///   <item>客户端传 2 个实参 → 服务端方法只有 1 个参数 ⇒ <b>同样失败</b>。</item>
    /// </list>
    /// <para>
    /// 也就是说 SignalR 的实参个数必须与签名<b>严格一致</b>，C# 的默认值参数在这里毫无作用。
    /// 于是"给心跳加字段"这条路是死的：它会让旧客户端的心跳整条失败（几十台机器一起变灰）。
    /// 要新增状态就走<b>独立的方法</b>，例如 <see cref="ReportUdpChannel"/>。
    /// </para>
    /// <para>这条纪律由集成测试里的 <c>HubContractArityTests</c> 用反射钉住。</para>
    /// </summary>
    public const string Heartbeat = "Heartbeat";

    /// <summary>
    /// 上报本机 UDP 指令通道的实时状态（2026-10-14）。载荷 <see cref="AgentUdpChannelStateDto"/>。
    /// <para>
    /// <b>为什么不塞进心跳</b>：见 <see cref="Heartbeat"/> 的实测备注 ——
    /// Hub 的参数个数不能动。多一条小消息远比"旧客户端心跳集体失败"划算。
    /// </para>
    /// <para>
    /// ⚠️ <b>旧服务端不认识它</b>：调用失败必须被客户端就地吞掉（与遥测上报同一条纪律），
    /// 绝不能让"这条能力暂不可用"升级成"断线重连"。
    /// </para>
    /// </summary>
    public const string ReportUdpChannel = "ReportUdpChannel";
    public const string ReportMetrics = "ReportMetrics";
    public const string ReportPowerEvents = "ReportPowerEvents";
    public const string ReportInventory = "ReportInventory";

    /// <summary>回报「配置已应用」（M4.5）。载荷 <see cref="AgentConfigAppliedDto"/>。</summary>
    public const string ReportConfigApplied = "ReportConfigApplied";

    /// <summary>
    /// <b>上报本机的视频硬编码能力</b>（M5.0 第二批）。载荷 <see cref="AgentEncoderCapabilityDto"/>。
    ///
    /// <para>
    /// 🔥 <b>为什么这份结论必须由客户机自己产生</b>：要编码的是<b>被控端</b>。
    /// 中心那台机器有没有 NVENC，与"这台客户机能不能跑远控 / 能不能 8K"完全无关。
    /// 这也是 M5 方案 §1.6 手段①（能力探测 + 自适应决策）唯一正确的落点。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>旧服务端不认识它</b>（新客户端先升级）：调用会抛，客户端必须<b>就地吞掉</b>
    /// （与遥测上报同一条纪律）—— "这条能力暂时用不上"绝不该升级成"断线重连"。
    /// </para>
    /// </summary>
    public const string ReportEncoderCapability = "ReportEncoderCapability";

    /// <summary>
    /// <b>上报本机的屏清单</b>（M5.5 多屏选择）。载荷 <see cref="AgentScreensDto"/>。
    ///
    /// <para>
    /// 🔥 <b>为什么不搭能力上报那趟车</b>：两件事发生在<b>两个不同的进程</b>里 ——
    /// 编码能力在服务进程探（Session 0 也能枚举 MFT），而屏清单只有
    /// <b>用户会话里的托盘</b>数得出来。合成一条消息就必须让其中一个跨进程取数据，
    /// 而那个方向（Session 0 去问用户会话）正是 M4.5 里被证明要走命名管道的方向。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 与能力上报同一条纪律：<b>旧服务端不认识它时，客户端必须就地吞掉失败</b>，
    /// 绝不能让"这条能力暂时用不上"升级成断线重连。
    /// </para>
    /// </summary>
    public const string ReportScreens = "ReportScreens";

    /// <summary>
    /// <b>上报一段被控机的剪贴板内容</b>（M5.5）。载荷 <see cref="RemoteClipboardDto"/>。
    ///
    /// <para>
    /// 🔥 <b>为什么是上行而不是回执</b>：中心 → 客户机的指令是 fire-and-forget
    /// （<c>SendAsync</c> 对一条已消失的连接是静默成功的），回执止步于服务进程。
    /// 所以"把被控机的剪贴板读回来"只能走：中心下发 <c>clipboard/get</c> →
    /// 托盘读 → 服务 → <b>本方法</b> → 中心缓存并广播给观看页。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>载荷里刻意不带 <c>sessionId</c></b>：中心按<b>连接身份</b>找到这台机器当前那一路
    /// 会话（一台机器同时只有一路）。让托盘自己填 sessionId 的话，
    /// 那条消息在"会话刚好结束"时会指向一个不存在的 Id，而中心只能丢掉它 ——
    /// 丢掉时页面看到的是"点了没反应"，真因却在托盘那边（它填了一个过期的 Id）。
    /// </para>
    /// </summary>
    public const string ReportClipboard = "ReportClipboard";

    /// <summary>
    /// <b>推送一帧远控画面</b>（M5.1）。载荷 <see cref="RemoteStreamFrame"/>。
    ///
    /// <para>
    /// 🔥 <b>为什么它必须是一条独立的 Hub 消息，而不是搭在心跳或某个已有的上报上</b>：
    /// SignalR 的实参个数必须与签名严格一致（见 <see cref="Heartbeat"/> 的实测备注），
    /// 所以任何"给已有方法加个字段"的想法都是死路。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>它是这条链路上唯一一条"高频大消息"</b>（其余全是低频小消息）。
    /// 单帧能有多大由 <see cref="RemoteStreamLimits"/> 说了算，而那个数<b>只在那一处定义</b>
    /// （中心与客户机都引用它）—— 历史教训是两处各写一份必然漂，
    /// 而漂的方向是"客户机还在发、中心已经把<b>整条连接</b>掐了"。
    /// </para>
    ///
    /// <para>
    /// 🔸 <b>M5.4-b 把上限从 512 KB 抬到了 8 MB</b>：M5.4-a 实测 2160×1440 的
    /// H.264 IDR 单帧 <b>306.9 KB</b>，而 512 KB 扣掉 base64 的 +33% 之后只留给裸码流约 384 KB
    /// ⇒ 余量只剩 20%，4K 必然撞穿。⚠️ 8 MB 只够 2160×1440 与 4K；
    /// 8K 要等<b>分片</b>（M5.4-c）。二进制协议（MessagePack）去掉 base64 那 33% 也仍然值得做，
    /// 但它与本条上限是两件事，别混为一谈。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>旧服务端不认识它</b>：调用会抛，客户端必须<b>就地吞掉</b>并结束推流
    /// （与遥测上报同一条纪律）—— 但这里不能像遥测那样"悄悄忽略"：
    /// 推流被拒必须让被控端<b>停下来</b>，否则会一直编一帧丢一帧地空烧 CPU。
    /// </para>
    /// </summary>
    public const string PushRemoteFrame = "PushRemoteFrame";

    /// <summary>
    /// <b>推送一帧远控画面的一片</b>（M5.4-c① 的分片）。载荷 <see cref="RemoteStreamFrameChunk"/>。
    ///
    /// <para>
    /// 🔥 <b>为什么必须是<b>新</b>的一条消息，而不是给 <see cref="PushRemoteFrame"/> 加一个参数</b>：
    /// SignalR 的实参个数必须与签名<b>严格一致</b>（见 <see cref="Heartbeat"/> 的实测备注），
    /// 加参数等于让<b>旧中心上所有新客户端的每一帧</b>调用失败 —— 那是"整路画面黑掉"，
    /// 而不是"某条消息不可用"。
    /// </para>
    ///
    /// <para>
    /// 🔸 <b>兼容性是"惰性"的</b>：只有<b>装不下一条消息</b>的帧才走这里；
    /// 2160×1440 与 4K 的帧仍然整帧走 <see cref="PushRemoteFrame"/>
    /// ⇒ <b>旧中心上它们照常工作</b>，只有真出现超大帧时才会停这一路流。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 与 <see cref="PushRemoteFrame"/> 同一条纪律：<b>中心说没收下就抛</b>，
    /// 让被控端停下来 —— "一直编、一直丢"是在白烧一台正在跑业务的机器的 CPU。
    /// </para>
    /// </summary>
    public const string PushRemoteFrameChunk = "PushRemoteFrameChunk";

    /// <summary>
    /// <b>送一段远控音频</b>（M5.3）。载荷 <see cref="RemoteAudioFrame"/>。
    ///
    /// <para>
    /// 🔥 <b>为什么它与 <see cref="PushRemoteFrame"/> 必须是两条消息，而不是
    /// "给帧头加一个媒体类型位"</b>：
    /// </para>
    /// <list type="bullet">
    ///   <item><b>丢帧策略相反</b>：视频那一路是"队列深度 1、忙就丢"（丢一帧只是画面顿一下），
    ///         而音频<b>丢一帧就是一个可听见的断点</b>。两者的队列必须分开，
    ///         否则一条队列没法同时服务两种相反的丢弃策略；</item>
    ///   <item><b>节奏差两个量级</b>：视频 12–60 fps，音频是 <b>50 帧/秒</b>（20 ms 一帧）恒定。
    ///         混在一起时，"这一秒收了多少帧"这个数就再也说不清是音是视；</item>
    ///   <item><b>发送方不同</b>：视频由抓屏循环产，音频由采集回调产，
    ///         它们在托盘里是<b>两个互不阻塞的循环</b>（音频绝不能被一次慢编码拖住）。</item>
    /// </list>
    ///
    /// <para>
    /// ⚠️ 与 <see cref="PushRemoteFrame"/> 同一条纪律：<b>中心说没收下就抛</b>，
    /// 让这路流停下来 —— 音频也一样，"一直采、一直丢"是在白烧被控机的 CPU。
    /// </para>
    /// </summary>
    public const string PushRemoteAudio = "PushRemoteAudio";

    /// <summary>
    /// <b>上报远控会话的状态变化</b>（M5.1）。载荷 <see cref="AgentRemoteSessionStateDto"/>。
    ///
    /// <para>
    /// 🔥 <b>为什么推流之外还要有这条</b>：被控端"起了但没起来"
    /// （没人登录 / 托盘没跑 / 没有可抓的桌面）的时候<b>一帧都不会有</b>。
    /// 只靠"没有帧"这个现象，中心侧分不清"正在连接"与"已经失败" ——
    /// 而这两者对应现场的下一步动作完全相反。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 它是<b>低频</b>消息（每次会话就几条：requested / streaming / stopped / failed），
    /// 与 <see cref="PushRemoteFrame"/> 的高频完全不是一回事。
    /// 正因为频率差这么多，它们才更该是两条独立消息 ——
    /// 搭在一起会让"状态"这种必须可靠送达的东西，跟着码流的失败一起丢。
    /// </para>
    /// </summary>
    public const string ReportRemoteSession = "ReportRemoteSession";

    /// <summary>
    /// 客户机把它<b>本机那份配置</b>推给中心（2026-10-14 的双向同步）。
    /// <para>
    /// 与 <see cref="ReportConfigApplied"/> 的分工：那条是"我按你给的这份跑起来了"，
    /// 这条是"这台机器<b>自己改</b>了一份，你收下"。它走 Hub 而不是 HTTP <c>PUT</c>：
    /// 连接本身就带着机器身份，归属校验是**结构性**的（改不了别人的），
    /// 不必再为"机器能不能写自己那份"单开一条策略与一套越权判断。
    /// </para>
    /// <para>
    /// ⚠️ 客户机<b>只在"本机改过、中心还不知道"时</b>才推；推成功之后标志就清了。
    /// 所以"收到一次推送"永远意味着"本机确实改过一版"，不是一个反复发生的轮询。
    /// </para>
    /// </summary>
    public const string PushConfig = "PushConfig";
}

/// <summary>
/// 客户端 Agent 的认证约定（M4.1）。
/// <para>
/// 与用户会话（Cookie）是<b>两套完全独立的身份</b>：Cookie 认的是"人"，AgentKey 认的是"机器"。
/// 因此这里单开一个认证 scheme，而不是复用 Cookie —— 复用会让"某台机器被吊销"
/// 与"某个账号被停用"纠缠在一起，两种情况该做的处置完全不同。
/// </para>
/// </summary>
public static class AgentAuthentication
{
    /// <summary>认证 scheme 名。</summary>
    public const string Scheme = "AgentKey";

    /// <summary>HTTP 头名（Agent 调 REST 端点时用）。</summary>
    public const string HeaderName = "X-Agent-Key";

    /// <summary>查询参数名（SignalR 的 WebSocket 握手无法带自定义头，只能走查询串）。</summary>
    public const string QueryName = "agentKey";

    /// <summary>声明：客户端主机 Id。</summary>
    public const string ClaimAgentId = "cs.agent.id";

    /// <summary>声明：机器稳定标识。</summary>
    public const string ClaimMachineKey = "cs.agent.machineKey";
}

/// <summary>SignalR 服务端 → 客户端事件名（与前端约定，改动需同步 web/src）。</summary>
public static class EventNames
{
    public const string LogAdded = "LogAdded";
    public const string StatusChanged = "StatusChanged";
    public const string ActionResult = "ActionResult";

    /// <summary>一条**收发日志**（与运维日志 <see cref="LogAdded"/> 是两回事）。M2-F 新增。</summary>
    public const string TrafficLogAdded = "TrafficLogAdded";

    /// <summary>配置已变更（含新版本号与变更者）。M2-F 新增。</summary>
    public const string ConfigChanged = "ConfigChanged";

    /// <summary>
    /// 监听地址（HTTP 端口）热切换的生命周期事件。M2-E5 新增。
    /// <para>
    /// <b>这是唯一一条会"通知客户端去自杀"的事件</b> —— 端口换了，当前页面所在的原点就不存在了，
    /// 浏览器不会自己迁移。因此服务端必须在切换<b>之前</b>推一条（此时旧端口还活着），
    /// 由前端倒计时后主动跳转到新原点。
    /// </para>
    /// <para>载荷见 <see cref="ServerAddressChangedDto"/>，<c>Phase</c> 为 switching / changed / failed。</para>
    /// </summary>
    public const string ServerAddressChanged = "ServerAddressChanged";

    /// <summary>
    /// 设备授权状态已变更（导入 / 生成试用 / 移除）。M3 授权子系统新增。
    /// <para>
    /// 授权是<b>设备级</b>状态，一次变更对<b>所有</b>在线会话同时生效 ——
    /// 所以必须推送：否则管理员导入授权后，别的窗口还停留在"未授权"横幅上，
    /// 而它的按钮已经开始工作了，界面与实际行为不一致。
    /// </para>
    /// <para>载荷见 <see cref="LicenseDto"/>。</para>
    /// </summary>
    public const string LicenseChanged = "LicenseChanged";

    /// <summary>
    /// 一台客户端主机上线（M4.1 新增）。
    /// <para>
    /// 与 <see cref="AgentChanged"/> 的区别：这一条是<b>状态跃迁</b>（离线 → 在线），
    /// 是"页面该弹个提示"的时刻；<see cref="AgentChanged"/> 是<b>任意字段变化</b>
    /// （改名、改分组、心跳刷新），是"列表该重绘"的时刻。
    /// 分开是为了让前端能只对前者做提醒，不被后者刷屏。
    /// </para>
    /// <para>载荷见 <see cref="AgentHostDto"/>。</para>
    /// </summary>
    public const string AgentOnline = "AgentOnline";

    /// <summary>一台客户端主机离线（M4.1 新增）。载荷见 <see cref="AgentHostDto"/>。</summary>
    public const string AgentOffline = "AgentOffline";

    /// <summary>
    /// 某台客户机的遥测数据已刷新（M4.2 新增）。
    /// <para>
    /// <b>服务端把它节流到 2 秒一次</b>（<c>Server:Agent:Telemetry:NotifyThrottleSeconds</c>）。
    /// 不节流的话，300 台 × 5 秒 = 60 条/秒 的推送会把每个打开的页面刷到卡死 ——
    /// 而人眼根本分辨不出 0.1 秒的差别，"看起来在跳"就够了。
    /// </para>
    /// <para>载荷见 <see cref="AgentLiveMetricsDto"/>：只带"这台机器此刻怎么样"，
    /// 历史曲线由前端按需拉 <c>GET /api/agents/{id}/metrics</c>。</para>
    /// </summary>
    public const string AgentTelemetryUpdated = "AgentTelemetryUpdated";

    /// <summary>
    /// 客户端主机记录发生变化（M4.1 新增）：注册 / 改名 / 吊销 / 心跳刷新 / 列表增删。
    /// <para>前端收到后重新拉一次 <c>GET /api/agents</c> —— 在线表规模有限（几十台），
    /// 全量刷新比增量合并简单得多，也不会出现"漏了一条就没同步"的死角。</para>
    /// </summary>
    public const string AgentChanged = "AgentChanged";

    /// <summary>
    /// 一条文件传输的进度变化（M4.4）。
    /// <para>
    /// <b>只推单个目标的一行变化</b>，而不是整个任务 —— 100+ 台机器时，
    /// 一次下发就是 100 条 item，每 5% 推一次全量会让页面被自己的重绘压垮。
    /// 载荷见 <see cref="TransferProgressDto"/>，前端据此原地更新那一行。
    /// </para>
    /// <para>任务终态（完成 / 部分成功 / 失败）也会借这条事件送达，前端据此刷新汇总。</para>
    /// </summary>
    public const string TransferProgress = "TransferProgress";

    /// <summary>
    /// 某台客户机的配置（任务清单 / UDP 通道）已变更（M4.5）。
    /// <para>
    /// 与 <see cref="AgentChanged"/> 分开：那条是"在线表重绘"（心跳都会触发，很频繁），
    /// 这条是"配置内容变了"（少而重）。前端据此只刷新<b>正在打开的那台详情页</b>的任务区块，
    /// 不必因为一次心跳就把整张在线表重拉一遍。
    /// </para>
    /// <para>载荷为 <c>int</c>（客户机 Id）。</para>
    /// </summary>
    public const string AgentConfigChanged = "AgentConfigChanged";
}

/// <summary>
/// <b>中心 → 观看端浏览器</b>的 Hub 方法名（<c>/hubs/remote</c>，M5.1）。
///
/// <para>
/// 🔥 与 <c>EventNames</c> 分开而不是混进去：那两个通道的<b>对象完全不同</b> ——
/// <c>EventNames</c> 是给"整个控制台"广播的（日志、在线表、传输进度），
/// 而这里只有<b>正在看某一路远控画面的人</b>才收得到。混在一起会让
/// "这条消息该不该发给某个连接"变成调用处的一个 if，而不是连接分组本身的性质。
/// </para>
/// </summary>
public static class RemoteHubMethods
{
    /// <summary>
    /// 一帧画面。载荷 <see cref="RemoteStreamFrame"/>。
    /// <para>
    /// 🔥 <b>它只发给"进了这个会话分组"的连接</b>（客户端先调 Hub 的 <c>Join</c>），
    /// 不做全站广播 —— 一帧 4K 画面给每个控制台连接都发一遍，是拿带宽换事故。
    /// </para>
    /// </summary>
    public const string Frame = "Frame";

    /// <summary>
    /// <b>一段音频</b>（M5.3）。载荷 <see cref="RemoteAudioFrame"/>。
    ///
    /// <para>
    /// 🔥 <b>与 <see cref="Frame"/> 分开，理由见 <c>AgentHubClientMethods.PushRemoteAudio</c></b>：
    /// 两条路的丢弃策略相反（视频丢帧、音频不丢），节奏差两个量级（12–60 fps vs 恒定 50 fps），
    /// 而且前端<b>根本不用同一条解码链</b>（视频走 <c>drawImage</c>/WebCodecs 视频解码，
    /// 音频走 WebCodecs 音频解码 + AudioWorklet）。三处都不同，合成一条只会让
    /// "这一条消息到底是音还是视"变成每个消费者各自去猜。
    /// </para>
    ///
    /// <para>与 <see cref="Frame"/> 一样<b>只发给进了这个会话分组的连接</b>。</para>
    /// </summary>
    public const string Audio = "Audio";

    /// <summary>
    /// 会话状态变化（建立 / 开始推流 / 结束 / 失败）。载荷 <see cref="RemoteSessionDto"/>。
    /// <para>
    /// ⚠️ <b>必须有这条</b>，不能只靠 <see cref="Frame"/>：被控端"起了但没起来"
    /// （无人登录 / 没硬编）时<b>永远不会有帧</b>，只看帧的话页面会永远停在
    /// "连接中"，而那正是最需要看到原因的时刻。
    /// </para>
    /// </summary>
    public const string SessionChanged = "SessionChanged";

    /// <summary>
    /// <b>被控机的剪贴板变了</b>（M5.5）。载荷 <see cref="RemoteClipboardDto"/>。
    ///
    /// <para>
    /// 🔥 <b>为什么它单独一条、且只发给这个会话分组</b>：剪贴板内容可能有 60 KB，
    /// 而 <see cref="SessionChanged"/> 会在每次状态变化、每次质量切换时发给<b>所有</b>连接。
    /// 把文本塞进会话快照，等于每切一次档就把 60 KB 重发一遍 —— 而收件人可能根本不用剪贴板。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>它只在"有人明确要读"的时候才发</b>（读剪贴板是一次显式动作），
    /// 不做自动同步轮询：那会让被控机每秒被读一次剪贴板，
    /// 而"某个进程一直开着剪贴板"在 Windows 上是会让别的程序粘贴失败的。
    /// </para>
    /// </summary>
    public const string ClipboardChanged = "ClipboardChanged";
}

/// <summary>
/// <b>观看端浏览器 → 中心</b>的 Hub 方法名（<c>/hubs/remote</c> 上由浏览器调用，M5.2）。
///
/// <para>
/// 🔥 <b>为什么单独一组而不是混进 <see cref="RemoteHubMethods"/></b>：
/// 那一组的每一条都是"中心发给浏览器"的，而这一组是反过来的 ——
/// 混在一起，读代码时就要靠"我记住它是哪个方向"来判断，而记错的表现是
/// <b>方法名对了但谁调谁搞反了</b>，两边都编译通过、只有真跑才空转。
/// </para>
///
/// <para>
/// ⚠️ 这些名字必须与 <c>RemoteHub</c> 上的<b>公开方法名逐一对应</b>
/// （SignalR 按名字绑定，拼错没有任何编译错误）。
/// </para>
/// </summary>
public static class RemoteHubClientMethods
{
    /// <summary>
    /// <b>送回一批输入事件</b>（M5.2）。载荷 <see cref="RemoteInputBatchDto"/>，
    /// 返回值 <see cref="RemoteInputAckDto"/>。
    ///
    /// <para>
    /// 🔥 <b>为什么用 <c>InvokeAsync</c>（要回执）而不是 <c>SendAsync</c>（不等回执）</b>：
    /// 输入这一类动作的失败<b>在画面上完全看不见</b> —— 按键没送到的样子
    /// 与"那个键在那台机器上本来就没用"一模一样。回执是唯一能让页面说出
    /// "这一批被拒了，因为没打开控制开关"的东西。
    /// </para>
    /// <para>
    /// ⚠️ 代价是每批一次往返，所以前端必须<b>合批</b>（拖动时按帧节流），
    /// 而不是"每个 <c>mousemove</c> 发一次"。
    /// </para>
    /// </summary>
    public const string SendInput = "SendInput";

    /// <summary>
    /// <b>打开 / 关闭这一路会话的输入回传</b>（M5.2）。载荷 <c>sessionId</c> 与 <c>enabled</c>，
    /// 返回变更后的 <see cref="RemoteSessionDto"/>。
    ///
    /// <para>
    /// 🔥 它是一个<b>独立的调用</b>，不是"在 <see cref="SendInput"/> 里带一个开关"：
    /// 开关必须<b>可被明确地表达和被看见</b>（"现在这台机器允许被控制"是页面上最要紧的一行字），
    /// 而夹在事件流里的开关无法回答"它现在是开还是关"。
    /// </para>
    /// </summary>
    public const string SetInputEnabled = "SetInputEnabled";
}
