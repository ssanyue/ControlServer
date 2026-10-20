namespace ControlServer.Agent.Tasks;

/// <summary>
/// 会话侧指令的方法名（服务侧 → 托盘）。
/// <para>
/// <b>为什么要有这组常量</b>：与 <c>AgentHubMethods</c> 同理 —— 它是服务与托盘之间的字符串合同，
/// 写错一个字母的表现是"服务说发过去了、托盘毫无反应"，<b>没有任何编译错误</b>。
/// </para>
/// </summary>
public static class SessionCommandMethods
{
    /// <summary>启动一个程序。<c>Argument</c> 是 <c>AgentLaunchTaskDto</c> 的 JSON。</summary>
    public const string Launch = "launch";

    /// <summary>音量操作。<c>Argument</c> 取 <c>AgentVolumeActions</c> 之一。</summary>
    public const string Volume = "volume";

    /// <summary>
    /// 远控码流（M5.1）：开始 / 停止推流、切黑白。
    /// <c>Argument</c> 是 <c>RemoteStreamRequest</c> 的 JSON。
    /// <para>
    /// 🔥 <b>为什么它必须先经过这条管道，而不是让托盘直连中心</b>：
    /// 托盘刻意<b>不持有任何凭据</b>（<c>status.json</c> 里连 agentKey 都没有）。
    /// 给它发一份凭据去连中心，等于把 M4 那套"凭据只在服务进程里"的设计推翻 ——
    /// 代价远大于多一跳本机管道。
    /// </para>
    /// <para>
    /// ⚠️ 真正搬码流的是<b>另一条</b>管道（<c>RemoteStreamProtocol</c>）：
    /// 这条是一次一问一答的低频指令通道，扛不住 10–30 fps 的码流。
    /// </para>
    /// </summary>
    public const string RemoteStream = "remote-stream";

    /// <summary>
    /// <b>要一份屏清单</b>（M5.5 多屏选择）。<c>Argument</c> 为空。
    /// <para>
    /// 🔥 它走这条管道而不是码流管道：屏清单是<b>低频、要回执</b>的一次问答，
    /// 而"一次一问一答 + 结构化回执"正是这条管道的形状
    /// （<c>SessionCommandResult.Payload</c> 就是为它加的）。
    /// </para>
    /// </summary>
    public const string Screens = "screens";

    /// <summary>
    /// <b>读 / 写本机剪贴板</b>（M5.5）。<c>Argument</c> 是 <c>RemoteStreamRequest</c> 的 JSON
    /// （复用它的 <c>clipboardAction</c> / <c>clipboardText</c> 两个字段）。
    /// </summary>
    public const string Clipboard = "clipboard";
}

/// <summary>一条发生会话侧的指令。</summary>
/// <param name="Method">见 <see cref="SessionCommandMethods"/>。</param>
/// <param name="Argument">
/// 方法参数。<see cref="SessionCommandMethods.Volume"/> 时是动作名；
/// <see cref="SessionCommandMethods.Launch"/> 时是 <c>AgentLaunchTaskDto</c> 的 JSON。
/// </param>
public sealed record SessionCommandRequest(string Method, string? Argument = null);

/// <summary>会话侧指令的执行结果。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Message">
/// 人话说明。<b>成功时也可能有内容</b> —— 例如"音量已调高：50%（未静音）"，
/// 那句话是<b>从系统读回来的真实状态</b>（见 <c>IAudioController</c>），
/// 让服务侧的日志能直接回答"到底动成什么样了"。失败时是原因。
/// </param>
public sealed record SessionCommandResult(bool Ok, string? Message = null, string? CodecNote = null)
{
    /// <summary>成功。</summary>
    /// <param name="message">可选的"结果说明"（例如执行后的真实状态）。</param>
    /// <param name="codecNote">
    /// 可选的<b>编码格式裁决说明</b>（M5.4-b 第 7 步）：<c>null</c> = 一切照中心的首选。
    /// <para>
    /// 🔥 <b>为什么它是独立的一个字段、而不是并进 <paramref name="message"/></b>：
    /// <paramref name="message"/> 是<b>给人看的日志行</b>，而这一句要作为
    /// "结构化事实"被继续传下去（进 <c>AgentRemoteSessionStateDto.CodecNote</c> 报给中心）。
    /// 并进去的话，下游只能靠解析文案把它再抠出来 —— 而文案一改就静默失败。
    /// </para>
    /// </param>
    /// <param name="desktopIdleMs">
    /// 可选的<b>桌面静止时长（毫秒）</b>（M5.4-c② 步 7）：<c>null</c> = 这一条回复里没有这个数。
    /// <para>
    /// 🔥 <b>为什么它不能并进 <paramref name="message"/></b>：理由与
    /// <paramref name="codecNote"/> 完全相同 —— 一个**毫秒数**要是靠解析人话抠出来，
    /// 那句文案（"桌面已经静止了 12.4 秒"）一改，这个数就静默变成 0 或 null，
    /// 而页面上会显示一个看起来完全正常的错值。
    /// </para>
    /// <para>
    /// ⚠️ <b>它是"有值且可能是 0"，与"没有"是两件事</b>：<c>0</c> = 桌面刚动过
    /// （一个**答案**），<c>null</c> = 这个途径没提供它。
    /// 把它写成 <c>long</c> 并用 0 表示"没有"，就会把"刚动过"与"不知道"压成一件事。
    /// </para>
    /// </param>
    public static SessionCommandResult Success(
        string? message = null,
        string? codecNote = null,
        long? desktopIdleMs = null) =>
        new(true, message, codecNote) { DesktopIdleMs = desktopIdleMs };

    /// <summary>失败（带原因）。</summary>
    public static SessionCommandResult Failure(string message) => new(false, message);

    /// <summary>
    /// 成功并带回一段<b>结构化载荷</b>（JSON 文本；M5.5）。
    ///
    /// <para>
    /// 🔥 <b>为什么不并进 <paramref name="message"/></b>：屏清单与剪贴板文本都是
    /// <b>要被继续传下去的数据</b>，不是给人看的日志行。靠解析人话抠出来，
    /// 文案一改就静默失败（这条教训已经在 CodecNote / DesktopIdleMs 上吃过两次）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 它是<b>字符串</b>而不是 <c>object</c>：这条通道的两端是两个进程、
    /// 版本可能不一致 —— 一个 <c>object</c> 会让"旧端收到新形状"变成
    /// 反序列化异常（整条通道失败），而字符串最坏只是"解析不了 ⇒ 忽略这一条"。
    /// </para>
    /// </summary>
    public static SessionCommandResult SuccessWithPayload(string payload, string? message = null) =>
        new(true, message) { Payload = payload };

    /// <summary>
    /// 桌面静止了多久（毫秒；见 <see cref="Success"/> 的说明）。
    /// <para>
    /// ⚠️ 它<b>只由 <c>desktop-idle</c> 那一条查询填写</b>；其余指令一律是 <c>null</c>。
    /// </para>
    /// </summary>
    public long? DesktopIdleMs { get; init; }

    /// <summary>
    /// 结构化载荷（JSON 文本；M5.5）。<c>null</c> = 这条指令没有载荷。
    /// <para>目前只有 <see cref="SessionCommandMethods.Screens"/> 与
    /// <see cref="SessionCommandMethods.Clipboard"/> 会填它。</para>
    /// </summary>
    public string? Payload { get; init; }
}

/// <summary>
/// 服务侧 → <b>用户会话侧</b>（托盘进程）的指令通道（M4.5）。
///
/// <para><b>为什么服务不自己干这件事</b>：客户端服务跑在 <b>Session 0</b>，那里
/// 没有桌面、没有音频会话。在 Session 0 里 <c>Process.Start</c> 出来的窗口
/// <b>用户根本看不见</b>；在那里改音量改的是 Session 0 的音频端点，
/// <b>用户一个音节都听不到</b>。两者都是"执行成功、毫无效果"的无声失败 ——
/// 现场最难查的一类故障。所以这类事必须交给会话里的进程做。</para>
///
/// <para><b>方向是"服务主动连托盘"</b>：托盘监听一条命名管道，服务作为客户端连上去。
/// 反过来的话，托盘就得知道"服务此刻在哪台机器上、用什么凭据"，
/// 而托盘是普通用户权限、还可能根本没在运行。</para>
///
/// <para>具体实现（命名管道 + 每会话一个管道名）见 <see cref="SessionPipeChannel"/> 与
/// <see cref="SessionPipeServer"/>；本接口刻意只描述"发一条指令、拿一个结果"。</para>
/// </summary>
public interface ISessionCommandChannel
{
    /// <summary>
    /// 这条通道在当前平台上是否<b>结构上可用</b>（平台支持 + 已经接线）。
    /// <para>
    /// 🔥 <b>与 <see cref="IsAvailable"/> 的区别必须分清</b>：
    /// 本属性回答"这东西<b>有朝一日</b>能不能用"，<see cref="IsAvailable"/> 回答"<b>此刻</b>能不能用"。
    /// </para>
    /// <para>
    /// 为什么要分开：为假时（例如非 Windows 平台）等待毫无意义，调用方应当<b>直接放弃</b>；
    /// 为真而 <see cref="IsAvailable"/> 为假时，含义是"用户还没登录 / 托盘还没启动"，
    /// 调用方应当<b>等它出现</b>（开机时服务先于用户登录启动，"开机把程序拉起来"正是靠这个语义成立）。
    /// 只给一个布尔值的话，这两种情形会被迫同样处理，必然错一边。
    /// </para>
    /// </summary>
    bool IsSupported { get; }

    /// <summary>此刻该通道是否可用（托盘在跑 / 平台支持）。</summary>
    bool IsAvailable { get; }

    /// <summary>发一条指令。失败返回 <see cref="SessionCommandResult.Failure"/>，<b>不抛</b>。</summary>
    Task<SessionCommandResult> SendAsync(SessionCommandRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 会话侧通道不可用时的<b>空实现</b>。
/// <para>
/// 它存在的意义是让服务侧<b>一个 if 都不用写</b>：所有"会话侧的能力"照常调用，
/// 拿到的是一句可读的失败原因，而不是 <c>NullReferenceException</c>。
/// </para>
/// <para>
/// ⚠️ 它<b>不是</b>"功能降级"的默认值 —— 注册它的时候必须给出一句明确的原因
/// （平台不支持 / 托盘未安装 / 尚未接入），这句话会原样进日志，
/// 免得现场看到"音量调不动"却不知道是从哪一层断的。
/// </para>
/// </summary>
public sealed class UnavailableSessionCommandChannel : ISessionCommandChannel
{
    private readonly string _reason;

    public UnavailableSessionCommandChannel(string reason) => _reason = reason;

    /// <inheritdoc />
    /// <remarks>
    /// 恒为假：注册这个实现就表示"本平台上永远不会有会话侧通道"，
    /// 因此等它出现是没有意义的（见 <see cref="ISessionCommandChannel.IsSupported"/>）。
    /// </remarks>
    public bool IsSupported => false;

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <summary>不可用的原因（会被打进日志）。</summary>
    public string Reason => _reason;

    /// <inheritdoc />
    public Task<SessionCommandResult> SendAsync(SessionCommandRequest request, CancellationToken cancellationToken)
        => Task.FromResult(SessionCommandResult.Failure($"会话侧指令通道不可用：{_reason}"));
}
