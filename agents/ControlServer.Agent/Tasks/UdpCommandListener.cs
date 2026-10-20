using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlServer.Agent.Config;
using ControlServer.Agent.Diagnostics;
using ControlServer.Contracts;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent.Tasks;

/// <summary>
/// 客户机上的 <b>UDP 指令通道</b>（M4.5）。
///
/// <para>
/// 这是本项目里<b>唯一一个客户机主动开着的入站端口</b>（M4.1 的铁律是"客户端不开入站端口"，
/// 这里按参考项目 <c>BootAssistant</c> 的习惯开一个口子）。
/// </para>
///
/// <para><b>线路协议是纯明文</b>（2026-10-13 起，见 <see cref="UdpCommandProtocol"/>）：</para>
/// <code>
/// DOWN      → 关机
/// RESTART   → 重启（整机）
/// +  / -    → 音量加 / 音量减
/// 0  / 1    → 静音 / 取消静音
/// </code>
///
/// <para><b>一道数据报要过三关，任何一关不过就丢弃：</b></para>
/// <list type="number">
///   <item><b>本机开关</b> —— <c>Agent:Tasks:Enabled</c> 为假时<b>根本不绑定端口</b>
///         （不是"绑定了但丢弃"：不开的端口在 <c>netstat</c> 里根本看不见，这才是"关"该有的样子）；</item>
///   <item><b>来源 IP 黑名单</b> —— 名单里的来源一律丢弃；名单为空则谁都不拦（默认）；</item>
///   <item><b>报文解析</b> —— 认不出的字符串直接丢弃（并节流记一行日志）。</item>
/// </list>
///
/// <para><b>"这条通道现在到底该不该听"只有两个决定者，优先级从高到低：</b></para>
/// <list type="number">
///   <item><b>本机总开关</b> <c>Agent:Tasks:Enabled</c> —— 为假则<b>永不听</b>（任何配置、任何连接状态都翻不过来）；</item>
///   <item><b>是否连着中心服务端</b> —— 由 <see cref="Apply"/> 的 <c>connected</c> 参数表达。
///         <b>没连上中心时一律听</b>（2026-10-14，需求①）：展厅断网 / 中心在维护时，
///         这台设备仍然要能被现场工具（或局域网里任意一台机器）直接关机 / 重启 / 调音量 ——
///         此时配置里的 <c>Enabled=false</c> 被<b>忽略</b>，端口沿用配置里的那个
///         （配置里的端口不可用时回落 <see cref="FallbackPort"/>，
///         而不是拒绝监听 —— 拒绝监听等于把这条能力关掉，与需求相反）。
///         只有<b>连上中心之后</b>才回到"配置说了算"（<c>Enabled=false</c> ⇒ 真的不绑端口）。</item>
/// </list>
///
/// <para>
/// 🔥 <b>2026-10-13 起删掉了 HMAC 验签、时间窗与 nonce 防重放</b>，并<b>把白名单换成了黑名单</b>。
/// 原设计是"未签名的版本绝不上线"，但它让"用任意工具发一个 DOWN"永远无效 ——
/// 现场看到的只是"指令没反应"。产品决定改为明文后，<b>安全边界从密码学退回到网络可达性</b>：
/// <b>同网段任意主机都能关掉这台设备</b>。因此这条通道的端口不能暴露到不可信网络，
/// 不想被控的机器就在「客户端」页把它关掉。
/// </para>
///
/// <para>
/// <b>防重放也一并删了</b>（这是有意的，不是漏做）：明文报文里没有 nonce，
/// 而且"同一条指令再发一次"本来就是一个<b>合法的新指令</b>（音量连按三次就该 +3）。
/// 用重放表去挡它，挡掉的是正常操作。
/// </para>
///
/// <para><b>执行位置</b>：关机 / 重启在<b>服务侧</b>执行（SYSTEM 天然有 <c>SeShutdownPrivilege</c>）；
/// 音量在<b>会话侧</b>执行（Session 0 里没有音频端点，自己动手是"执行成功、毫无声音"的无声失败）。</para>
///
/// <para>
/// <b>它是被"配置 + 连接状态"驱动的</b>：端口、开关、黑名单来自下发的配置或本机配置页，
/// 而"该不该听"的最终裁决还要看此刻有没有连着中心，所以 <see cref="Apply"/> 是它唯一的入口 ——
/// 换个端口 = 把旧监听停掉、按新参数重新绑一次；
/// 只是连接状态变了（端口与黑名单没变）= 只换状态文案，<b>端口一动不动</b>（见 <see cref="Listen"/>）。
/// </para>
/// </summary>
public sealed class UdpCommandListener : IAsyncDisposable
{
    /// <summary>同类丢弃日志的最小间隔（秒）。灌包时不能一条一行。</summary>
    private const int RejectLogSeconds = 5;

    private readonly AgentOptions _options;
    private readonly LogService _log;
    private readonly AgentCommandExecutor _commands;
    private readonly ReceivedDataLog _received;

    private readonly object _gate = new();

    /// <summary>当前这次监听（null = 没有在听）。换端口时整份换掉，不做字段级修补。</summary>
    private Channel? _channel;

    /// <summary>上一次打出去的"通道为什么没开"的原因（去重，避免每次 Apply 都刷一行）。</summary>
    private string? _lastNote;

    /// <summary>上一次打出去的"配置里的端口用不上、已回落默认端口"的提示（去重）。</summary>
    private string? _lastFallbackNote;

    /// <summary>
    /// "此刻这条通道是什么状态"的人话，供本机页面/中心详情页展示。
    /// <para>它与 <see cref="_lastNote"/> 的区别：那个是<b>日志去重</b>用的（同一句只打一次），
    /// 这个是<b>随问随答</b>的当前值 —— 页面每次问都要拿到最完整的一句。</para>
    /// </summary>
    private string? _stateText = "尚未应用任何配置 —— UDP 指令通道还没启动。";

    private DateTimeOffset _lastRejectLog = DateTimeOffset.MinValue;
    private int _suppressedRejects;

    private bool _disposed;

    /// <summary>
    /// 🔥 <b>指令怎么执行不归本类管</b>：统一交给 <see cref="AgentCommandExecutor"/>。
    /// 中心（SignalR）下发的指令走的是<b>同一份</b>实现 —— 否则"UDP 能关机、页面按钮关不了"
    /// 这种事迟早再来一次（2026-10-15 就是这么发生的）。
    /// </summary>
    /// <param name="fallbackPort">
    /// 「配置里没写端口 / 端口越界」时<b>回落</b>到的端口。<b>不传 = 生产默认值</b>
    /// <see cref="AgentConfigDefaults.DefaultUdpPort"/> ——
    /// 也就是说生产接线（<c>Program.cs</c> 的注册）<b>什么都不用改</b>。
    ///
    /// <para>
    /// 它存在的<b>唯一</b>理由是让用例能在这个端口上换一个临时端口，见 <see cref="FallbackPort"/>。
    /// </para>
    /// </param>
    public UdpCommandListener(
        AgentOptions options,
        LogService log,
        AgentCommandExecutor commands,
        ReceivedDataLog received,
        int? fallbackPort = null)
    {
        _options = options;
        _log = log;
        _commands = commands;
        _received = received;
        FallbackPort = fallbackPort ?? AgentConfigDefaults.DefaultUdpPort;
    }

    /// <summary>
    /// 配置里没写端口（或写了越界端口）时<b>回落</b>到的那个端口。
    ///
    /// <para>
    /// 生产环境＝<see cref="AgentConfigDefaults.DefaultUdpPort"/>（<b>8090</b>，现场那些发送脚本
    /// 指着它），由 <c>Program.cs</c> 的注册<b>不传参</b>得到（见构造函数）。
    /// </para>
    ///
    /// <para>
    /// 🔥 <b>它之所以是可注入的，只有一个理由</b>（2026-10-15）：用例必须能在
    /// <b>"本机正装着真 Agent、8090 已被它占住"的开发机上跑</b>。
    /// 在这之前，<see cref="UdpCommandListenerTests"/> 里有 5 条用例<b>真的去绑 8090</b>，
    /// 于是它们的红绿取决于"这台机器上有没有装服务"，而不是取决于代码对不对 ——
    /// 一台装了开机自启 Agent 的开发机上，同一份代码会稳定报 5 条红。
    /// <b>那种红比不做断言更坏</b>：它会训练人忽略红色。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 注入<b>只影响"回落用哪个端口"</b>，<b>不影响"该不该回落"</b> ——
    /// 无配置 / 端口越界仍然照常回落，那条语义由用例原样守着。
    /// </para>
    /// </summary>
    public int FallbackPort { get; }

    /// <summary>此刻是否真的绑定了端口在听。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate) return _channel is not null;
        }
    }

    /// <summary>当前监听端口（未运行时为 null）。</summary>
    public int? Port
    {
        get
        {
            lock (_gate) return _channel?.Port;
        }
    }

    /// <summary>
    /// "这条通道此刻是什么状态"的人话（没在听时带上原因）。给本机页面与中心详情页用。
    /// </summary>
    public string? StateText
    {
        get
        {
            lock (_gate) return _stateText;
        }
    }

    /// <summary>收到了会不会真的执行（false = 演练模式）。见 <c>Agent:Tasks:ExecuteUdpCommands</c>。</summary>
    public bool ExecuteEnabled => _options.Tasks.ExecuteUdpCommands;

    /// <summary>
    /// 按配置（重新）决定这条通道的状态。**幂等**，可以从任何时机反复调用。
    /// <para>
    /// ⚠️ 2026-10-13 起<b>不再需要 agentKey</b>（报文不签名了）——
    /// 于是"装完还没注册成功"的那段时间通道也能起起来，这正是"装完就能用"该有的样子。
    /// </para>
    /// </summary>
    /// <param name="config">
    /// 当前生效的配置。<b>null = 还没有任何配置</b>，此时按<b>默认值</b>起
    /// （启用 + <see cref="FallbackPort"/>），
    /// <b>不是</b>"当作关闭"—— 见下面那段注释。
    /// </param>
    /// <param name="connected">
    /// 此刻<b>是否连着中心服务端</b>。
    ///
    /// <para>
    /// 🔥 <b>2026-10-14 新增（需求①），它优先于 <c>config.Udp.Enabled</c></b>：
    /// 未连接时<b>一律监听</b>，配置里那个 <c>Enabled=false</c> 被忽略。
    /// 理由是这条通道的定位 —— 它本来就是"中心网页点一下就能关机"之外的那条<b>旁路</b>，
    /// 而旁路的价值恰恰在<b>中心不可达时</b>才体现（展厅断网、中心在维护、机器刚上架还没注册）。
    /// 中心在的时候"配置说关就关"，中心不在的时候"必须能听"，这才自洽。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 端口仍<b>沿用配置</b>（没配置就 <see cref="FallbackPort"/>，生产＝8090）——
    /// 现场那些发送脚本指的是一个固定端口，
    /// 因为断了一次中心就把端口换掉，等于把脚本也一起弄坏了。
    /// 配置里的端口越界时回落 <see cref="FallbackPort"/>，<b>而不是拒绝监听</b>。
    /// </para>
    ///
    /// <para>⚠️ 唯一的例外是 <c>Agent:Tasks:Enabled=false</c> —— 那是<b>本机总开关</b>，
    /// 任何情况下都优先，见方法开头。</para>
    /// </param>
    public void Apply(AgentConfigDto? config, bool connected)
    {
        // ── 总开关优先于一切，**包括下面那条"未连接必须开"**（铁律：关就要在 netstat 里看不见）──
        if (!_options.Tasks.Enabled)
        {
            Stop("本机已在配置中停用任务下发（Agent:Tasks:Enabled=false）。");
            return;
        }

        // 🔥 <b>没有配置 = 用默认值（启用 + 8090），不是"当作关闭"</b>（2026-10-14 修）。
        //
        // 旧写法把 `config is null` 判成"中心下发的配置没有启用 UDP 指令通道"⇒
        // 压根不绑端口（netstat 里看不见），而现场看到的只是"指令没反应" ——
        // 这正是"客户机本该默认开着、实际却没有"的来源之一：
        //   · 装完还没拉到配置的那段时间（服务端不可达 / 首次注册中）；
        //   · 中心侧<b>从未保存过</b>这台机器的配置（`FindAsync` 返回 null）。
        //
        // 它与 AgentConfigContent.Empty 是同一个决定：这条通道的起点是"装完就能用"，
        // 要关就<b>显式</b>把 Enabled 关掉（或者在客户端页把它关掉）。
        var udp = config?.Udp;
        var wanted = udp?.Port ?? FallbackPort;

        // ── 未连接中心 ⇒ 强制监听（2026-10-14，需求①）──
        if (!connected)
        {
            if (wanted < AgentConfigDefaults.MinUdpPort || wanted > AgentConfigDefaults.MaxUdpPort)
            {
                // ⚠️ 这里**刻意不 Stop**：拒绝监听等于把这条能力关掉，与需求正好相反。
                LogFallbackNote(
                    $"当前未连接中心，且配置里的 UDP 端口 {wanted} 超出允许范围"
                    + $"（{AgentConfigDefaults.MinUdpPort}~{AgentConfigDefaults.MaxUdpPort}）—— "
                    + $"已回落到 {FallbackPort} 继续监听。");

                wanted = FallbackPort;
            }

            Listen(wanted, udp?.BlockedSourceIps, standalone: true);
            return;
        }

        // ── 已连接中心：完全按配置来（以下三条是原有语义，一字未改）──
        if (udp is { Enabled: false })
        {
            Stop("中心下发的配置没有启用 UDP 指令通道。");
            return;
        }

        if (wanted < AgentConfigDefaults.MinUdpPort || wanted > AgentConfigDefaults.MaxUdpPort)
        {
            Stop($"下发端口 {wanted} 超出允许范围（{AgentConfigDefaults.MinUdpPort}~{AgentConfigDefaults.MaxUdpPort}），已拒绝监听。");
            return;
        }

        Listen(wanted, udp?.BlockedSourceIps, standalone: false);
    }

    /// <summary>
    /// 让这条通道<b>明确不要监听</b>，并把 <paramref name="reason"/> 作为对外状态文案。
    ///
    /// <para><b>它与 <see cref="Apply"/> 的分工</b>：<c>Apply</c> 回答"按配置 + 连接状态我该怎样"，
    /// 本方法回答"不管配置怎么写，现在就是不许听"。它给两类调用方用：</para>
    /// <list type="bullet">
    ///   <item>配置说了不该听（本机停用任务下发 / 中心关掉了通道 / 端口越界）——
    ///         <c>Apply</c> 内部就走的这条路；</item>
    ///   <item><b>本机自己决定</b>不该听（本机配置页上的总开关、将来别的本机策略）。
    ///         ⚠️ 这种场合<b>不能用 <c>Apply(null, …)</c> 来表达"关"</b>：
    ///         null 的语义是"还没有配置 ⇒ 用默认值"，而默认值是<b>开着</b>的。</item>
    /// </list>
    ///
    /// <para>
    /// 🔥 <b>2026-10-14 起"被管理员吊销"不再走这条路</b>（需求①：被吊销也属于"没连着中心"，
    /// 那台机器<b>仍然要能被 UDP 控制</b>，见 <see cref="Apply"/> 的 <c>connected</c>）。
    /// 换句话说，本方法现在只表达一件事：<b>配置层面明确说了不要听</b>。
    /// </para>
    ///
    /// <para><b>幂等</b>：本来就没在听时只刷新文案与去重日志。</para>
    /// </summary>
    public void Stop(string reason)
    {
        lock (_gate)
        {
            if (_disposed) return;

            var running = _channel;

            if (running is not null)
            {
                _channel = null;
                StopChannel(running);

                _lastNote = null;
                _log.Warn("Udp", $"UDP 指令通道已停止（原端口 {running.Port}）：{reason}");
            }
            else
            {
                LogStateNote(reason);
            }

            _stateText = reason;
        }
    }

    /// <summary>按端口 / 黑名单 / 是否"未连接强制监听"起监听（或把正在听的换到新参数上）。</summary>
    /// <param name="standalone">
    /// true = 此刻没连着中心，这条通道是<b>按需求强制开着</b>的（状态文案要说得出来）。
    /// <para>它与 <c>port</c>/<c>blocked</c> 的处理方式<b>不一样</b>：那两个变了要重绑端口，
    /// 它变了<b>只换文案</b> —— 见下面那段注释。</para>
    /// </param>
    private void Listen(int port, IReadOnlyList<string>? blocked, bool standalone)
    {
        lock (_gate)
        {
            if (_disposed) return;

            var running = _channel;

            // ── 端口与黑名单一字未变 ──
            if (running is not null && Matches(running, port, blocked))
            {
                // 🔥 `standalone` 变了**不重绑端口**，只翻标志 + 刷文案。
                //
                // 连接状态是会抖的（一次网络抖动就是"断 → 连"），而重绑端口在那两个瞬间
                // 会让端口短暂没人听 —— 那是实打实的收包空窗，可能正好丢掉一条 DOWN。
                // 更关键的是：**"未连接才强制听"影响的从来不是"听不听"，只是"为什么在听"**，
                // 所以端口该一动不动。
                if (running.Standalone != standalone)
                {
                    running.Standalone = standalone;

                    _log.Info(
                        "Udp",
                        standalone
                            ? $"与中心的连接已断开 —— UDP 指令通道继续监听 0.0.0.0:{port}（未连接期间强制开启）。"
                            : $"已连上中心 —— UDP 指令通道按中心下发的配置继续监听 0.0.0.0:{port}。");
                }

                // 状态文案必须跟着刷：上一次可能是"没在听"，而这次它已经在听了。
                _stateText = DescribeRunning(port, blocked, standalone);
                return;
            }

            if (running is not null)
            {
                _channel = null;
                StopChannel(running);
                _log.Info("Udp", $"UDP 指令通道切换端口：{running.Port} → {port}");
            }

            try
            {
                _channel = Start(port, blocked, standalone);
            }
            catch (Exception ex)
            {
                // 端口被占 / 权限不足 / 地址不可用：只记一行，**不抛** ——
                // 这里抛出去会把配置应用流程打断（任务本身没问题，只是这条通道起不来）。
                _channel = null;
                _stateText = $"监听 {port} 端口失败：{ex.Message}";

                _log.Error("Udp", $"UDP 指令通道启动失败（端口 {port}，任务不受影响）：{ex.Message}");
                return;
            }

            _lastNote = null;
            _stateText = DescribeRunning(port, blocked, standalone);

            _log.Info(
                "Udp",
                $"UDP 指令通道已启动{StandaloneNote(standalone)}：监听 0.0.0.0:{port}｜"
                + $"{BlockedText(blocked)}｜"
                + "明文指令 DOWN / RESTART / + / - / 0 / 1。");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 监听
    // ═══════════════════════════════════════════════════════════

    private Channel Start(int port, IReadOnlyList<string>? blocked, bool standalone)
    {
        // 绑定 IPAddress.Any：局域网任意网卡上来的指令都要能收到（与服务端的对接码监听、
        // 发现监听保持同一习惯）。这正是"任意来源皆可"的实现。
        var client = new UdpClient(new IPEndPoint(IPAddress.Any, port));

        var cts = new CancellationTokenSource();

        var channel = new Channel(port, blocked ?? Array.Empty<string>(), standalone, client, cts);

        channel.Loop = Task.Run(() => ReceiveLoopAsync(channel, cts.Token));

        return channel;
    }

    private async Task ReceiveLoopAsync(Channel channel, CancellationToken cancellationToken)
    {
        var client = channel.Client;

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;

            try
            {
                result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Stop() 里 Dispose 了 socket：正常路径。
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                // ⚠️ Windows 上取消一个挂起的 UDP 接收，抛的常常是
                // SocketException(995, WSA_OPERATION_ABORTED) 而不是 OperationCanceledException。
                // 少了这一条，每次停监听都会多打一行"网卡抖动"的假警告。
                break;
            }
            catch (Exception ex)
            {
                _log.Warn("Udp", $"UDP 接收出错（将继续监听）：{ex.Message}");

                try
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                continue;
            }

            // 串行处理（与对接码监听同语义）：保证"先发的指令先执行"，
            // 也让日志顺序与操作顺序一致 —— 两条指令交错时这一点很重要。
            try
            {
                await HandleAsync(channel, result, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 铁律 #6：一条报文的处理异常绝不能让整条通道静默死掉
                // （那会表现成"UDP 通道某天开始没反应了"，而日志里什么都没有）。
                _log.Error("Udp", "处理 UDP 指令时出错（通道继续监听）", ex);
            }
        }
    }

    /// <summary>处理一条数据报：黑名单 → 解析 → 执行。</summary>
    private async Task HandleAsync(Channel channel, UdpReceiveResult result, CancellationToken cancellationToken)
    {
        var remoteIp = result.RemoteEndPoint.Address.ToString();
        var source = $"{remoteIp}:{result.RemoteEndPoint.Port}";
        var text = Encoding.UTF8.GetString(result.Buffer ?? Array.Empty<byte>());

        // ── 第 2 关：来源黑名单（在解析之前 —— 拉黑的机器不该消耗解析与后续日志）──
        if (UdpCommandProtocol.IsSourceBlocked(channel.BlockedIps, remoteIp))
        {
            LogRejection(remoteIp, "来源 IP 在黑名单内");
            _received.Add(AgentReceivedKinds.Udp, source, text, AgentReceivedOutcomes.Blocked, "来源 IP 在本机黑名单内，报文在解析之前就被丢弃。");
            return;
        }

        // ── 第 3 关：解析 ──
        if (!UdpCommandProtocol.TryParse(text, out var command, out var error) || command is null)
        {
            var reason = error ?? "报文无法识别";

            LogRejection(remoteIp, reason);
            _received.Add(AgentReceivedKinds.Udp, source, text, AgentReceivedOutcomes.Unrecognized, reason);
            return;
        }

        _log.Info("Udp", $"收到指令：{command.Token}（来自 {remoteIp}）");

        // ⚠️ 这里**先记"收到并已识别"再执行**吗？不 —— 记录的是**结论**，
        //    所以要等执行完拿到真实结果再写一条。中间那点延迟正是它该有的样子：
        //    页面上写着"已执行"的那一刻，机器确实已经动手了。
        //
        //    （演练模式下也是一条结论 —— Ignored ＋ "为什么不执行"，
        //     而不是"什么都没记"：那会让现场以为报文根本没到。）
        var outcome = await _commands.ExecuteAsync(
            command.Name,
            command.Argument,
            $"UDP 指令（来自 {remoteIp}）",
            "Udp",
            cancellationToken).ConfigureAwait(false);

        _received.Add(AgentReceivedKinds.Udp, source, text, outcome.Outcome, outcome.Detail);
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 记一条"丢弃"。<b>按时间节流</b>：一次灌包可能是每秒上万条，
    /// 逐条记录会把客户机的日志写爆，日志滚动还会把有用的历史挤掉。
    /// 第 1 条立刻记，之后每 <see cref="RejectLogSeconds"/> 秒记一条，并附带被压掉的总数。
    /// </summary>
    private void LogRejection(string remoteIp, string reason)
    {
        int suppressed;
        DateTimeOffset now = DateTimeOffset.Now;

        lock (_gate)
        {
            if (now - _lastRejectLog < TimeSpan.FromSeconds(RejectLogSeconds))
            {
                _suppressedRejects++;
                return;
            }

            suppressed = _suppressedRejects;
            _suppressedRejects = 0;
            _lastRejectLog = now;
        }

        var extra = suppressed > 0 ? $"（另有 {suppressed} 条同类报文被丢弃，未逐条记录）" : string.Empty;

        _log.Warn("Udp", $"丢弃来自 {remoteIp} 的报文：{reason}{extra}");
    }

    /// <summary>同一句"通道没开"的原因只打一次（否则每次版本对齐都会刷一行）。</summary>
    private void LogStateNote(string note)
    {
        if (string.Equals(_lastNote, note, StringComparison.Ordinal)) return;

        _lastNote = note;
        _log.Info("Udp", $"UDP 指令通道未启动：{note}");
    }

    /// <summary>
    /// 同一句"端口回落"的提示只打一次。
    /// <para>它是个 <c>Warn</c> 而不是 <c>Info</c>：配置里写的端口没用上，现场必须能看见 ——
    /// 否则现象就是"我明明配了 9000，脚本发 9000 没反应"。</para>
    /// </summary>
    private void LogFallbackNote(string note)
    {
        if (string.Equals(_lastFallbackNote, note, StringComparison.Ordinal)) return;

        _lastFallbackNote = note;
        _log.Warn("Udp", note);
    }

    /// <summary>"正在听"的那句状态文案（端口 + 黑名单 + 为什么在听），页面与日志共用同一句。</summary>
    private static string DescribeRunning(int port, IReadOnlyList<string>? blocked, bool standalone)
        => $"正在监听 0.0.0.0:{port}{StandaloneNote(standalone)}，{BlockedText(blocked)}。";

    /// <summary>"为什么这条通道是开着的"—— 未连接中心时那句必须说出来，否则现场会以为这是配置的功劳。</summary>
    private static string StandaloneNote(bool standalone)
        => standalone ? "（未连接中心，已强制开启）" : string.Empty;

    /// <summary>黑名单那句话（日志与状态文案共用，避免两处漂移）。</summary>
    private static string BlockedText(IReadOnlyList<string>? blocked)
        => $"来源黑名单 {(blocked is { Count: > 0 } ? $"{blocked.Count} 条" : "为空（不限制来源）")}";

    /// <summary>端口 / 黑名单两者一字不差才算"没变"。</summary>
    /// <remarks>
    /// ⚠️ <b>刻意不把 <c>standalone</c> 算进来</b>：它变了不该触发重绑端口（见 <see cref="Listen"/>）。
    /// </remarks>
    private static bool Matches(Channel channel, int port, IReadOnlyList<string>? blocked)
    {
        if (channel.Port != port) return false;

        var desired = blocked ?? Array.Empty<string>();

        if (channel.BlockedIps.Count != desired.Count) return false;

        for (var i = 0; i < desired.Count; i++)
        {
            if (!string.Equals(channel.BlockedIps[i], desired[i], StringComparison.Ordinal)) return false;
        }

        return true;
    }

    /// <summary>停掉一次监听。<b>不阻塞调用方</b>（收尾在自己的任务里做）。</summary>
    private static void StopChannel(Channel channel)
    {
        try
        {
            channel.Cts.Cancel();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            // Dispose 掉 socket 让阻塞中的 ReceiveAsync 立刻醒来 ——
            // 只 Cancel 的话，Windows 上那个调用未必马上返回。
            channel.Client.Dispose();
        }
        catch
        {
            // 忽略。
        }

        _ = FinishAsync(channel);
    }

    /// <summary>
    /// 等收包循环真正退出再释放取消源。
    /// <para>⚠️ <b>不能"取消完立刻 Dispose"</b>：循环里还在用那个 token，
    /// 这时候 Dispose 会让它抛 <c>ObjectDisposedException</c> —— 一个只在
    /// "改配置的瞬间恰好来了一条报文"时才出现的偶发异常。</para>
    /// </summary>
    private static async Task FinishAsync(Channel channel)
    {
        try
        {
            await channel.Loop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 循环内部已全量捕获，这里只是不让异常冒到未观测状态。
        }

        try
        {
            channel.Cts.Dispose();
        }
        catch
        {
            // 忽略。
        }
    }

    public async ValueTask DisposeAsync()
    {
        Channel? running;

        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
            running = _channel;
            _channel = null;
            _stateText = "UDP 指令通道已随进程停机关闭。";
        }

        if (running is null) return;

        try
        {
            running.Cts.Cancel();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            running.Client.Dispose();
        }
        catch
        {
            // 忽略。
        }

        // 停机路径**要等**（与 Stop 不同）：进程马上就没了，这时候不等就再也等不到了。
        await FinishAsync(running).ConfigureAwait(false);

        _log.Info("Udp", "UDP 指令通道已随进程停机关闭。");
    }

    /// <summary>一次监听的全部状态（端口 / 黑名单 / 是否"未连接强制监听" / socket / 取消源 / 收包循环）。</summary>
    private sealed class Channel
    {
        public Channel(
            int port,
            IReadOnlyList<string> blockedIps,
            bool standalone,
            UdpClient client,
            CancellationTokenSource cts)
        {
            Port = port;
            BlockedIps = blockedIps;
            Standalone = standalone;
            Client = client;
            Cts = cts;
        }

        public int Port { get; }

        public IReadOnlyList<string> BlockedIps { get; }

        /// <summary>
        /// 这次监听是不是"因为没连着中心才强制开着的"（<see cref="Apply"/> 的 <c>connected=false</c>）。
        /// <para>唯一的用途是让状态文案说得出原因，以及决定有没有必要重绑端口 ——
        /// <b>它是可写的</b>：连接状态变了只翻它，不动 socket（见 <see cref="Listen"/>）。</para>
        /// </summary>
        public bool Standalone { get; set; }

        public UdpClient Client { get; }

        public CancellationTokenSource Cts { get; }

        public Task Loop { get; set; } = Task.CompletedTask;
    }
}
